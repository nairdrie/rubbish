using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Shop trash for everyone in the shop (lead's N2 ask; Docs/Multiplayer.md §13.5): whatever a guest's machine spawns — a pile it
// tipped from a truck (N4's DepotTruckCargoNet hands the cargo to the tipper), the bag its sort station fetched, the items spilled
// on its sorting table — is published too, so the owner, employees and visitors all see the same pile. The rules:
//  - Guest-made objects are sent to the host WITH their real contents (bag JSON / item JSON: the cargo came from the host's
//    rolls anyway, §6.3); the host builds its copy and everyone else gets one. The guest keeps simulating it (Flight) until it
//    settles, then the host owns it at rest like any other.
//  - Bags off the curb carry their contents to every copy, so any machine can rip one open at its own station.
//  - Receivers inside a shop interior (bins, compactor, table, keep crate) run on the machine that delivers, so shop pay goes
//    where N3/N4 route it (the shop's business, or the employer through Ledger.Redirect); the host only drops the item for
//    everyone. Truck hoppers stay host-run (pay by the truck's business, N1).
//  - A local script that takes a copy it doesn't simulate (SetInFlight: the station's fetch, the compactor's vacuum) claims it.
//  - Only the owner and employees may touch trash inside a shop (Permissions.CanWorkIn on every request).
public partial class HandsNet
{
    // guest: objects sent to the host and waiting for their id (key → object)
    readonly Dictionary<string, Carryable> pendingPublish = new Dictionary<string, Carryable>(StringComparer.Ordinal);
    readonly HashSet<Carryable> sentPublish = new HashSet<Carryable>();
    // guest: ids this machine consumed itself (late messages about them must not rebuild a copy)
    readonly HashSet<uint> goneHere = new HashSet<uint>();
    float nextGuestScan;

    /// <summary>What a copy needs to be the real thing: bag contents or the item instance as JSON ("" for a curb bag).</summary>
    static string DataOf(Carryable c)
    {
        try
        {
            if (c is TrashBag tb)
                return tb.Contents != null && !tb.IsCurbBag && tb.Contents.items != null && tb.Contents.items.Count > 0 ? JsonUtility.ToJson(tb.Contents) : "";
            if (c is LooseItem li) return li.Item != null ? JsonUtility.ToJson(li.Item) : "";
        }
        catch (Exception e) { Debug.LogException(e); }
        return "";
    }

    void ConfigureGuards()
    {
        SessionService.SetGuard("rb.hands.pose", new NetGuard(240f, 240, 64));
        SessionService.SetGuard("rb.hands.publish", new NetGuard(40f, 80, 12 * 1024));
        SessionService.SetGuard("rb.hands.settle", new NetGuard(40f, 80, 64));
        SessionService.SetGuard("rb.hands.consumed", new NetGuard(40f, 80, 32));
        SessionService.SetGuard("rb.hands.reveal", new NetGuard(40f, 80, 32));
        SessionService.SetGuard("rb.hands.take", new NetGuard(20f, 40, 32));
        SessionService.SetGuard("rb.hands.pull", new NetGuard(20f, 20, 16));
        SessionService.SetGuard("rb.hands.wriggle", new NetGuard(15f, 15, 8));
    }

    // ==================================================================================== guest: publish what we spawned

    void GuestScan()
    {
        if (Time.unscaledTime < nextGuestScan) return;
        nextGuestScan = Time.unscaledTime + 0.25f;
        sentPublish.RemoveWhere(x => x == null);
        int budget = 8;
        var all = Carryable.All;
        for (int i = all.Count - 1; i >= 0 && budget > 0; i--)
        {
            var c = all[i];
            if (c == null || c.NetId != 0 || c.State == CarryState.Consumed || !c.isActiveAndEnabled || sentPublish.Contains(c)) continue;
            // A curb visual can outlive both its SourceStop and its record while the shrink animation runs.
            // IsCurbBag then becomes false; its immutable origin still keeps that stale visual off the publish path.
            if (c is TrashBag tb && (tb.HasBurst || tb.WasMaterializedAtCurb || tb.IsCurbBag)) continue;
            if (IsPendingCurb(c)) continue;
            string key = KeyOf(c);
            if (string.IsNullOrEmpty(key) || pendingPublish.ContainsKey(key)) continue;
            var desc = DescOf(c, 0);
            desc.key.CopyFromTruncated(key);
            bool held = c.Holder != null && c.Holder == TrashCarrier.Local;
            Vector3 v = c.Body != null && !c.Body.isKinematic ? c.Body.linearVelocity : Vector3.zero;
            pendingPublish[key] = c;
            sentPublish.Add(c);
            PublishRpc(desc, DataOf(c), c.transform.position, c.transform.rotation, v, held);
            budget--;
        }
    }

    static string KeyOf(Carryable c)
    {
        if (c is TrashBag tb && tb.Contents != null && !string.IsNullOrEmpty(tb.Contents.bagId)) return tb.Contents.bagId;
        if (c is LooseItem li && li.Item != null && !string.IsNullOrEmpty(li.Item.uid)) return li.Item.uid;
        return null;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PublishRpc(CarryDesc desc, string data, Vector3 pos, Quaternion rot, Vector3 vel, bool held, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.publish", sender, 64 + (data != null ? data.Length : 0))) return;
        string key = desc.key.ToString();
        if (desc.kind == CarryDesc.KindBag && byBagId.ContainsKey(key)) { NetAudit.Event("hands", new Dictionary<string, object> { { "op", "publish-dup" }, { "key", key }, { "client", sender } }); return; }
        if (!Permissions.CanWorkIn(sender, DepotRowLayout.InteriorAt(pos)))
        {
            NetAudit.Event("hands", new Dictionary<string, object> { { "op", "publish-refused" }, { "key", key }, { "client", sender } });
            return;
        }
        Carryable c = BuildObject(desc, data, pos, rot);
        if (c == null) return;
        if (!active) Activate();
        uint id = nextId++;
        var nc = c.gameObject.AddComponent<NetCarry>();
        nc.Init(this, id, desc, c);
        nc.Data = data ?? "";
        nc.Origin = sender;
        items[id] = nc;
        if (desc.kind == CarryDesc.KindBag) byBagId[key] = id;
        // The guest that made it keeps simulating it (its hands, or its flight until it settles).
        var s = new CarryNetState
        {
            id = id, seq = 1, phase = (byte)(held ? HandsPhase.Held : HandsPhase.Flight), holder = sender, other = None,
            pos = pos, rot = rot, vel = vel,
        };
        nc.ApplyState(s);
        ItemRpc(nc.Desc, s, Bump(), nc.Data, RpcTarget.NotServer);
        Census("published-by-guest", nc, s);
    }

    /// <summary>A plain local object from a description (+ its real contents when we have them).</summary>
    Carryable BuildObject(CarryDesc d, string data, Vector3 pos, Quaternion rot)
    {
        string key = d.key.ToString();
        try
        {
            if (d.kind == CarryDesc.KindBag)
            {
                BagContents contents = null;
                if (!string.IsNullOrEmpty(data)) contents = JsonUtility.FromJson<BagContents>(data);
                if (contents == null) contents = new BagContents { bagId = key, foundAt = "", bagMassKg = Mathf.Max(0.15f, d.mass), sackVariant = d.sack == 255 ? -1 : d.sack };
                if (string.IsNullOrEmpty(contents.bagId)) contents.bagId = key;
                return TrashBag.Spawn(contents, pos, rot);
            }
            ItemInstance it = null;
            if (!string.IsNullOrEmpty(data)) it = JsonUtility.FromJson<ItemInstance>(data);
            if (it == null) it = new ItemInstance { uid = key, defId = d.defId.ToString(), massKg = d.mass, value = d.value, condition = d.condition };
            return LooseItem.Spawn(it, pos, rot, Vector3.zero, d.revealed, d.finderClientId);
        }
        catch (Exception e) { Debug.LogException(e); return null; }
    }

    /// <summary>Guest: the host's answer to one of our publishes — our own object becomes that id (nothing moves).</summary>
    bool TryAdoptPublished(CarryDesc desc, CarryNetState s, string data)
    {
        string key = desc.key.ToString();
        if (!pendingPublish.TryGetValue(key, out var c)) return false;
        pendingPublish.Remove(key);
        if (c == null || c.State == CarryState.Consumed)
        {
            // Gone here before the host answered (binned, crushed): drop it everywhere.
            ConsumedRpc(s.id, CarryNetState.FlagReceived);
            return true;
        }
        var nc = c.gameObject.AddComponent<NetCarry>();
        nc.Init(this, s.id, desc, c);
        nc.Data = data ?? "";
        nc.St = s;
        nc.HostSt = s;
        c.NetRemote = !((s.Phase == HandsPhase.Held || s.Phase == HandsPhase.Flight) && s.holder == LocalId);
        items[s.id] = nc;
        if (desc.kind == CarryDesc.KindBag) byBagId[key] = s.id;
        return true;
    }

    // ==================================================================================== consumed on a guest

    /// <summary>Guest: a networked item we simulate was consumed here (an interior bin, the table, the compactor).</summary>
    void GuestConsumed(Carryable c, NetCarry nc)
    {
        if (nc == null)
        {
            // Not answered yet: the adoption sends it.
            return;
        }
        byte flags = nc.GoneFlags;
        if (c is TrashBag tb && tb.HasBurst) flags |= CarryNetState.FlagBurst;
        if (flags == 0) flags = CarryNetState.FlagReceived;
        nc.Announced = true;
        var s = nc.St;
        s.phase = (byte)HandsPhase.Gone;
        s.flags = flags;
        nc.St = s;
        nc.HostSt = s;
        Forget(nc);
        goneHere.Add(nc.Id);
        ConsumedRpc(nc.Id, flags);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ConsumedRpc(uint id, byte flags, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.consumed", sender, 16)) return;
        var nc = Get(id);
        if (nc == null || nc.Item == null) return;
        bool sim = (nc.St.Phase == HandsPhase.Held || nc.St.Phase == HandsPhase.Flight) && nc.St.holder == sender;
        bool rest = nc.St.Phase == HandsPhase.Rest;
        if ((!sim && !rest) || !Permissions.CanWorkIn(sender, DepotRowLayout.InteriorAt(nc.Item.transform.position)))
        {
            NetAudit.Event("hands", new Dictionary<string, object> { { "op", "consumed-refused" }, { "id", id }, { "client", sender }, { "phase", nc.St.Phase.ToString() } });
            return;
        }
        CancelTugFor(id, "consumed");
        nc.Announced = true;
        var s = Snapshot(nc);
        s.phase = (byte)HandsPhase.Gone;
        s.flags = flags;
        s.seq = nc.St.seq + 1;
        nc.St = s;
        nc.HostSt = s;
        Forget(nc);
        // The host's copy just goes (the guest's machine did the real thing: pay, sparkle, crunch).
        var item = nc.Item;
        applying++;
        try
        {
            if (item.Holder != null) item.Holder.ReleaseToScript();
            if (item.State != CarryState.Consumed) item.MarkConsumed();
        }
        finally { applying--; }
        if ((flags & CarryNetState.FlagBurst) != 0) Destroy(item.gameObject);
        else Juice.ShrinkAndDestroy(item.gameObject, 0.18f);
        ItemRpc(nc.Desc, s, Bump(), "", RpcTarget.Not(new[] { sender, NetworkManager.ServerClientId }, RpcTargetUse.Temp));
        Census("gone-by-guest", nc, s);
    }

    // ==================================================================================== a local script takes a copy

    void OnScriptTake(Carryable c)
    {
        var nc = Get(c.NetId);
        if (nc == null || applying > 0) return;
        if (IsServer)
        {
            // The host's own machinery taking something a guest is flying or holding: leave the guest's flight alone (no reverts).
            NetAudit.Event("hands", new Dictionary<string, object> { { "op", "host-take-skipped" }, { "id", c.NetId }, { "phase", nc.St.Phase.ToString() } });
            return;
        }
        if (!Permissions.LocalCanWorkAt(c.transform.position)) return;
        nc.PredictLocalFlight();
        TakeRpc(c.NetId, c.transform.position, c.transform.rotation);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TakeRpc(uint id, Vector3 pos, Quaternion rot, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.take", sender, 32)) return;
        var nc = Get(id);
        if (nc == null || nc.Item == null) return;
        bool free = nc.St.Phase == HandsPhase.Rest || ((nc.St.Phase == HandsPhase.Flight || nc.St.Phase == HandsPhase.Held) && nc.St.holder == sender);
        bool ok = free && !nc.WindowOpen && Permissions.CanWorkIn(sender, DepotRowLayout.InteriorAt(nc.Item.transform.position));
        NetAudit.Event("hands", new Dictionary<string, object> { { "op", ok ? "take" : "take-refused" }, { "id", id }, { "client", sender }, { "phase", nc.St.Phase.ToString() } });
        if (!ok)
        {
            ItemRpc(nc.Desc, Snapshot(nc), 0, nc.Data ?? "", RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        var s = nc.St;
        s.phase = (byte)HandsPhase.Flight;
        s.holder = sender;
        s.other = None;
        s.flags = 0;
        s.pos = pos;
        s.rot = rot;
        s.vel = Vector3.zero;
        HostSet(nc, s);
    }

    /// <summary>
    /// Host: retire stock in the departing owner's depot, regardless of which machine simulated its creation.
    /// Work delivered to somebody ELSE's depot stays there: an employee's rip does not make the employer's trash
    /// belong to that employee. Street items also stay. The closed depot's existing restoration policy is unchanged.
    /// </summary>
    void HostDropOriginItems(ulong client)
    {
        foreach (var nc in new List<NetCarry>(items.Values))
        {
            if (nc == null || nc.Item == null) continue;
            int lot = DepotRowLayout.InteriorAt(nc.Item.transform.position);
            var row = DepotRowNet.Instance;
            if (lot < 0 || row == null || !row.TryGetLot(lot, out var depot) ||
                !DepotBacklog.RetireWithOwner(depot.Claimed, depot.ownerClientId, client)) continue;
            CancelTugFor(nc.Id, "origin-left");
            nc.Announced = true;
            var s = Snapshot(nc);
            s.phase = (byte)HandsPhase.Gone;
            s.flags = CarryNetState.FlagReceived;
            s.seq = nc.St.seq + 1;
            nc.St = s;
            nc.HostSt = s;
            Forget(nc);
            var item = nc.Item;
            applying++;
            try
            {
                if (item.Holder != null) item.Holder.ReleaseToScript();
                if (item.State != CarryState.Consumed) item.MarkConsumed();
            }
            finally { applying--; }
            Destroy(item.gameObject);
            ItemRpc(nc.Desc, s, Bump(), "", RpcTarget.NotServer);
            Census("gone-origin-left", nc, s);
        }
    }
}

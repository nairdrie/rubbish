using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>What everyone may know about a player's Compactor Backpack (host-written; the contents stay on the host, §6.3).</summary>
public struct PackNet : INetworkSerializable, IEquatable<PackNet>
{
    public ulong client;
    public byte plain, crushed;
    /// <summary>Kilograms x10 of the plain and the crushed bags (the owner slows down by them).</summary>
    public ushort plainKg10, crushedKg10;

    public int Count => plain + crushed;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref client);
        s.SerializeValue(ref plain);
        s.SerializeValue(ref crushed);
        s.SerializeValue(ref plainKg10);
        s.SerializeValue(ref crushedKg10);
    }

    public bool Equals(PackNet o) => client == o.client && plain == o.plain && crushed == o.crushed && plainKg10 == o.plainKg10 && crushedKg10 == o.crushedKg10;
    public override bool Equals(object obj) => obj is PackNet o && Equals(o);
    public override int GetHashCode() => client.GetHashCode();
}

// The Compactor Backpack's inventory (Hauler calling, Docs/Progression.md §6.9; rules in HaulerRules). A bag on your back is
// not an object: the host takes its BagContents out of the world and keeps them per player; everyone else only sees a count
// (PackNet). A curb bag keeps its GameSession.CurbBags record the whole time (hidden at its curb, like a bag in someone's
// hands), so it pays, ages and counts for Rep exactly as if it had been carried by hand.
//  - Stash / take / crush / spill / unload are requests (guarded RPCs); the host checks who, where and how full.
//  - Unloading into a truck hopper or a walk-up hatch runs the receiver's real Accept on the host, one bag at a time, with the
//    actor credited like a hand toss: the hopper pays the TRUCK's business, the hatch pays the lot's.
//  - Rule 12: no player is affected but the owner. Counter: none needed. Cooldowns: crush 0.55 s a bag; unload by level.
// Solo runs the same host code with plain local objects (HandsNet is spawned but not Active).
public partial class HandsNet
{
    public const byte PackFxStash = 1, PackFxTake = 2, PackFxCrush = 3, PackFxUnload = 4, PackFxSpill = 5, PackFxYank = 6;
    const string MsgPack = "rb.hands.pack";

    /// <summary>A backpack moment on every machine: kind, whose pack, from, to.</summary>
    public static event Action<byte, ulong, Vector3, Vector3> PackFx;
    /// <summary>Any pack's public numbers changed (every machine).</summary>
    public static event Action PacksChanged;

    sealed class PackEntry { public BagContents contents; public bool crushed; }

    NetworkList<PackNet> packs;
    readonly Dictionary<ulong, List<PackEntry>> packBags = new Dictionary<ulong, List<PackEntry>>();
    readonly Dictionary<ulong, Vector3> packLastPos = new Dictionary<ulong, Vector3>();
    readonly Dictionary<ulong, float> packCrushAt = new Dictionary<ulong, float>();
    readonly Dictionary<ulong, float> poleSecondFor = new Dictionary<ulong, float>();
    readonly HashSet<ulong> packUnloading = new HashSet<ulong>();
    readonly List<ulong> packScratch = new List<ulong>();
    bool packGuarded;
    float nextPackTick;

    void AwakePack()
    {
        packs = new NetworkList<PackNet>();
        packs.OnListChanged += OnPacksChanged;
    }

    void OnPacksChanged(NetworkListEvent<PackNet> e)
    {
        try { PacksChanged?.Invoke(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetPackStatics() { PackFx = null; PacksChanged = null; }

    /// <summary>A player's pack as everyone sees it (empty when they carry nothing on their back).</summary>
    public PackNet PackOf(ulong client)
    {
        if (packs != null)
            for (int i = 0; i < packs.Count; i++) if (packs[i].client == client) return packs[i];
        return new PackNet { client = client };
    }

    /// <summary>A calling level the host may trust for <paramref name="client"/> (their own business's tree; the local
    /// player's upgrade level too, so a debug-granted level works before the tree catches up).</summary>
    public int HaulerLevel(ulong client, string callingId)
    {
        int level = Workshop.CallingLevelForClient(client, callingId);
        if (client == LocalId) level = Mathf.Max(level, GameSession.GetUpgradeLevel(callingId));
        return level;
    }

    public int PackSlotsOf(ulong client) =>
        HaulerRules.PackSlots(HaulerLevel(client, CallingIds.CompactorBackpack), HaulerLevel(client, CallingIds.WorkGloves));

    // ==================================================================================== local player: requests

    /// <summary>Can the local player's held bag be offered to the pack right now (the host has confirmed the grab)?</summary>
    public bool LocalCanStash(Carryable held)
    {
        if (!(held is TrashBag) || !IsSpawned) return false;
        if (!active) return IsServer;
        var nc = Get(held.NetId);
        return nc != null && !nc.PendingGrab && !nc.PendingGone && nc.HeldBy(LocalId);
    }

    public void LocalPackStash(Carryable held)
    {
        if (!LocalCanStash(held)) return;
        if (IsServer) HostPackStash(LocalId, held.NetId);
        else PackStashRpc(held.NetId);
    }

    public void LocalPackTake() { if (!IsSpawned) return; if (IsServer) HostPackTake(LocalId); else PackTakeRpc(); }
    public void LocalPackCrush() { if (!IsSpawned) return; if (IsServer) HostPackCrush(LocalId); else PackCrushRpc(); }
    public void LocalPackSpill(bool scatter) { if (!IsSpawned) return; if (IsServer) HostPackAskSpill(LocalId, scatter); else PackSpillRpc(scatter); }

    /// <summary>Unload the whole pack into the hopper or hatch the local player stands at.</summary>
    public void LocalPackUnload(ICarryReceiver r)
    {
        if (!IsSpawned || r == null || (r is UnityEngine.Object o && o == null)) return;
        if (IsServer) { HostPackUnload(LocalId, r); return; }
        bool hasTruck = false;
        NetworkObjectReference truckRef = default;
        if (r is Component comp)
        {
            var no = comp.GetComponentInParent<NetworkObject>();
            if (no != null && no.IsSpawned) { truckRef = new NetworkObjectReference(no); hasTruck = true; }
        }
        PackUnloadRpc(hasTruck, truckRef, Fixed64(r.GetType().Name), r.TossTargetPosition);
    }

    /// <summary>Grabby Hand level 3: the second bag of one squeeze (into the pack when it fits, else to your feet).</summary>
    public void LocalPoleSecond(Carryable item)
    {
        if (item == null || !IsSpawned) return;
        string key = item is TrashBag bag && bag.IsCurbBag ? bag.Contents?.bagId : null;
        if (IsServer) { HostPoleSecond(LocalId, item.NetId, key, item.WorldCenter, item); return; }
        if (item.NetId == 0 && string.IsNullOrEmpty(key)) return;
        PoleSecondRpc(item.NetId, Fixed64(key ?? ""), item.WorldCenter);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PackStashRpc(uint id, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(MsgPack, sender, 16)) HostPackStash(sender, id);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PackTakeRpc(RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(MsgPack, sender, 8)) HostPackTake(sender);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PackCrushRpc(RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(MsgPack, sender, 8)) HostPackCrush(sender);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PackSpillRpc(bool scatter, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(MsgPack, sender, 8)) HostPackAskSpill(sender, scatter);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PackUnloadRpc(bool hasTruck, NetworkObjectReference truckRef, FixedString64Bytes rtype, Vector3 rpos, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed(MsgPack, sender, 112)) return;
        string type = rtype.ToString();
        if (type != nameof(TruckHopper) && type != nameof(FootHatch)) return;
        HostPackUnload(sender, ResolveReceiver(hasTruck, truckRef, type, rpos));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PoleSecondRpc(uint id, FixedString64Bytes curbKey, Vector3 at, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(MsgPack, sender, 96)) HostPoleSecond(sender, id, curbKey.Length > 0 ? curbKey.ToString() : null, at, null);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void PackFxRpc(byte kind, ulong client, Vector3 a, Vector3 b)
    {
        try { PackFx?.Invoke(kind, client, a, b); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void PackResultRpc(int bags, long payCents, long footCents, long curbBonusCents, FixedString64Bytes business,
                       FixedString64Bytes why, Vector3 at, RpcParams rpc = default) =>
        ShowPackResult(bags, payCents, footCents, curbBonusCents, business.ToString(), why.ToString(), at, true);

    [Rpc(SendTo.SpecifiedInParams)]
    void PackNopeRpc(FixedString64Bytes why, RpcParams rpc = default) => ShowPackNope(why.ToString());

    // ==================================================================================== host: who and what

    bool PackActor(ulong client, out PlayerTells tells, out TrashCarrier carrier, out string why)
    {
        tells = PlayerTells.For(client);
        carrier = tells != null ? tells.GetComponent<TrashCarrier>() : null;
        why = "";
        var player = tells != null ? tells.Player : null;
        if (player == null) return false;
        if (player.IsDriving || player.IsPlayingChess || CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client))
        { why = "Not right now!"; return false; }
        var body = BodyOf(client);
        if (body.State != BodyState.Normal && body.State != BodyState.Riding || body.carrying != None)
        { why = "Hands busy!"; return false; }
        return true;
    }

    List<PackEntry> PackList(ulong client)
    {
        if (!packBags.TryGetValue(client, out var list)) packBags[client] = list = new List<PackEntry>();
        return list;
    }

    void PackCounts(ulong client, out int plain, out int crushed)
    {
        plain = crushed = 0;
        if (!packBags.TryGetValue(client, out var list)) return;
        foreach (var e in list) { if (e.crushed) crushed++; else plain++; }
    }

    /// <summary>Why this bag can't go on this player's back (null = it can).</summary>
    string PackRefusal(ulong client, BagContents contents)
    {
        int slots = PackSlotsOf(client);
        if (slots <= 0) return "You need a Compactor Backpack for that.";
        if (contents == null || contents.sackVariant < 0) return "Only whole bags go in the pack.";
        if (BagTells.Of(contents.bagId) == BagTell.Heavy) return "Too heavy for your back. It takes two!";
        PackCounts(client, out int plain, out int crushed);
        if (HaulerRules.PackHasRoom(slots, plain, crushed)) return null;
        return plain > 0 && HaulerRules.PackCanCrush(HaulerLevel(client, CallingIds.CompactorBackpack))
            ? "Pack's full: hold C to crush it down!" : "Pack's full!";
    }

    void PackPublish(ulong client)
    {
        if (!IsServer || packs == null) return;
        int index = -1;
        for (int i = 0; i < packs.Count; i++) if (packs[i].client == client) { index = i; break; }
        var n = new PackNet { client = client };
        float plainKg = 0f, crushedKg = 0f;
        if (packBags.TryGetValue(client, out var list))
            foreach (var e in list)
            {
                float kg = e.contents != null ? e.contents.TotalMassKg : 0f;
                if (e.crushed) { n.crushed++; crushedKg += kg; } else { n.plain++; plainKg += kg; }
            }
        n.plainKg10 = (ushort)Mathf.Clamp(Mathf.RoundToInt(plainKg * 10f), 0, ushort.MaxValue);
        n.crushedKg10 = (ushort)Mathf.Clamp(Mathf.RoundToInt(crushedKg * 10f), 0, ushort.MaxValue);
        if (n.Count == 0)
        {
            if (index >= 0) packs.RemoveAt(index);
            packBags.Remove(client);
            return;
        }
        if (index < 0) packs.Add(n);
        else if (!packs[index].Equals(n)) packs[index] = n;
    }

    void PackNope(ulong client, string why)
    {
        if (string.IsNullOrEmpty(why)) return;
        if (client == LocalId) ShowPackNope(why);
        else PackNopeRpc(Fixed64(why), RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    static Vector3 PackBack(Transform player) => player.position + Vector3.up * 1.05f - Flat(player.forward) * 0.45f;

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
    }

    // ==================================================================================== host: stash

    void HostPackStash(ulong client, uint id)
    {
        if (!IsServer) return;
        if (!PackActor(client, out _, out var carrier, out string why)) { PackNope(client, why); return; }
        NetCarry nc = null;
        Carryable item;
        if (active)
        {
            nc = Get(id);
            if (nc == null || nc.Announced || nc.PendingGone || nc.PendingGrab || nc.WindowOpen || !nc.HeldBy(client) || TugOf(client).HasValue)
            { PackNope(client, "Get a proper grip first!"); return; }
            item = nc.Item;
        }
        else
        {
            if (client != LocalId) return;
            item = carrier != null ? carrier.Held : null;
        }
        if (!(item is TrashBag bag) || bag.State == CarryState.Consumed || bag.HasBurst)
        { PackNope(client, "Only whole bags go in the pack."); return; }
        string no = PackRefusal(client, bag.Contents);
        if (no != null) { PackNope(client, no); return; }
        HostPackTakeIn(client, bag, nc);
    }

    /// <summary>Host: this bag goes into <paramref name="client"/>'s pack (already checked). Its record stays on the books.</summary>
    void HostPackTakeIn(ulong client, TrashBag bag, NetCarry nc)
    {
        var contents = bag.Contents;
        Vector3 from = bag.WorldCenter;
        PackList(client).Add(new PackEntry { contents = contents });
        var stop = bag.SourceStop;
        if (stop != null) { stop.NotifyBagCollected(bag); bag.SourceStop = null; }
        // Still a curb bag: hide it at its curb on every machine for as long as the record lives (as a held bag is).
        if (bag.IsCurbBag) WorldState.Instance?.HostMarkPublished(contents.bagId);
        HostPackVanish(bag, nc);
        PackPublish(client);
        PackFxRpc(PackFxStash, client, from, default);
        NetAudit.Event("pack", new Dictionary<string, object> { { "op", "stash" }, { "client", (long)client }, { "bag", contents.bagId ?? "" } });
    }

    /// <summary>Host: the bag object leaves the world WITHOUT its curb record being dropped (OnNetConsumed would drop it).</summary>
    void HostPackVanish(TrashBag bag, NetCarry nc)
    {
        if (nc != null && !nc.Announced)
        {
            nc.Announced = true;
            CancelTugFor(nc.Id, "gone");
            var s = Snapshot(nc);
            s.phase = (byte)HandsPhase.Gone;
            s.flags = CarryNetState.FlagReceived;
            s.seq = nc.St.seq + 1;
            nc.St = s;
            nc.HostSt = s;
            Forget(nc);
            ItemRpc(nc.Desc, s, Bump(), "", RpcTarget.NotServer);
            Census("gone-packed", nc, s);
        }
        applying++;
        try
        {
            if (bag.Holder != null) bag.Holder.ReleaseToScript();
            bag.MarkConsumed();
        }
        finally { applying--; }
        Juice.ShrinkAndDestroy(bag.gameObject, 0.15f);
    }

    // ==================================================================================== host: take one off / spill

    void HostPackTake(ulong client)
    {
        if (!IsServer || packUnloading.Contains(client)) return;
        if (!PackActor(client, out var tells, out var carrier, out string why)) { PackNope(client, why); return; }
        if (!packBags.TryGetValue(client, out var list) || list.Count == 0) return;
        Vector3 back = PackBack(tells.transform);
        if (!Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(back))) { PackNope(client, "Not in someone else's shop!"); return; }
        bool handsFree = active ? !HostHolding(client, null) : carrier != null && !carrier.IsCarrying;
        if (!handsFree) { PackNope(client, "Hands full!"); return; }
        var e = list[list.Count - 1];
        var bag = TrashBag.Spawn(e.contents, back, Quaternion.Euler(0f, tells.transform.eulerAngles.y, 0f));
        if (bag == null) return;
        list.RemoveAt(list.Count - 1);
        bag.NoBurstUntil = Time.time + 2f;
        PackPublish(client);
        PackFxRpc(PackFxTake, client, back, default);
        if (active)
        {
            var nc = HostPublish(bag, null, true);
            HostGrab(client, nc.Id, null, ServerNow, bag.transform.position, tells.transform.position);
        }
        else if (carrier != null) carrier.PickUp(bag);
    }

    /// <summary>Host: a player asks to dump their pack (the bale key, a wipe-out, sitting down at the sort station). Never
    /// inside a shop they don't work in: the bags would be stuck behind "Not your shop!".</summary>
    void HostPackAskSpill(ulong client, bool scatter)
    {
        if (!IsServer || packUnloading.Contains(client)) return;
        var tells = PlayerTells.For(client);
        if (tells == null) return;
        if (!Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(tells.transform.position))) { PackNope(client, "Not in someone else's shop!"); return; }
        HostPackSpill(client, scatter, null);
    }

    /// <summary>Host: every bag slides (or, on a wipe-out, flies) off <paramref name="client"/>'s back and lands as a real bag.</summary>
    void HostPackSpill(ulong client, bool scatter, Vector3? at)
    {
        if (!IsServer || !packBags.TryGetValue(client, out var list) || list.Count == 0) return;
        var tells = PlayerTells.For(client);
        Vector3 origin = at ?? (tells != null ? tells.transform.position : packLastPos.TryGetValue(client, out var last) ? last : Vector3.zero);
        Vector3 behind = tells != null ? -Flat(tells.transform.forward) : Vector3.back;
        Vector3 side = Vector3.Cross(Vector3.up, behind);
        var entries = new List<PackEntry>(list);
        list.Clear();
        PackPublish(client);
        for (int i = 0; i < entries.Count; i++)
        {
            // A tidy stack behind you, or a ring of flying bags (cosmetic spread only: no outcome rides on it).
            float lane = (i % 3 - 1) * 0.45f;
            Vector3 p = origin + behind * (0.7f + 0.3f * (i / 3 % 2)) + side * lane + Vector3.up * (0.5f + 0.42f * (i / 3));
            float angle = i * 2.399f; // golden angle: an even fan
            Vector3 v = scatter ? new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 2.6f + Vector3.up * 3.4f : behind * 0.6f + Vector3.up * 0.4f;
            var bag = TrashBag.Spawn(entries[i].contents, p, Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f), v);
            if (bag != null) bag.NoBurstUntil = Time.time + 4f;
        }
        PackFxRpc(PackFxSpill, client, origin + Vector3.up, scatter ? Vector3.one : Vector3.zero);
        NetAudit.Event("pack", new Dictionary<string, object> { { "op", "spill" }, { "client", (long)client }, { "n", entries.Count }, { "scatter", scatter } });
    }

    // ==================================================================================== host: crush

    void HostPackCrush(ulong client)
    {
        if (!IsServer) return;
        if (!HaulerRules.PackCanCrush(HaulerLevel(client, CallingIds.CompactorBackpack))) return;
        if (!PackActor(client, out var tells, out _, out _)) return;
        if (!packBags.TryGetValue(client, out var list)) return;
        float now = Time.unscaledTime;
        if (packCrushAt.TryGetValue(client, out var t) && now - t < HaulerTuning.CrushSecondsPerBag * 0.8f) return;
        foreach (var e in list)
        {
            if (e.crushed) continue;
            // The sack is squeezed, never the contents: every item (valuables too), the bag id and the curb record are untouched.
            e.crushed = true;
            packCrushAt[client] = now;
            PackPublish(client);
            PackFxRpc(PackFxCrush, client, PackBack(tells.transform), default);
            return;
        }
    }

    // ==================================================================================== host: unload

    void HostPackUnload(ulong client, ICarryReceiver r)
    {
        if (!IsServer || packUnloading.Contains(client)) return;
        if (r == null || (r is UnityEngine.Object o && o == null) || !(r is TruckHopper || r is FootHatch)) { PackNope(client, "Nothing to unload into here."); return; }
        if (!packBags.TryGetValue(client, out var list) || list.Count == 0) return;
        StartCoroutine(HostPackUnloadRoutine(client, r));
    }

    IEnumerator HostPackUnloadRoutine(ulong client, ICarryReceiver r)
    {
        packUnloading.Add(client);
        int bags = 0;
        long pay = 0, foot = 0, curbBonus = 0;
        string business = "", why = "";
        Vector3 at = r.TossTargetPosition;
        try
        {
            float interval = HaulerRules.UnloadInterval(HaulerLevel(client, CallingIds.CompactorBackpack));
            while (IsSpawned && packBags.TryGetValue(client, out var list) && list.Count > 0)
            {
                if (r is UnityEngine.Object o && o == null) { why = "It's gone!"; break; }
                if (!PackActor(client, out var tells, out var carrier, out why)) break;
                at = r.TossTargetPosition;
                Vector3 d = tells.transform.position - at; d.y = 0f;
                if (d.sqrMagnitude > HaulerTuning.UnloadRange * HaulerTuning.UnloadRange) { why = "Too far away."; break; }
                var e = list[list.Count - 1];
                Vector3 back = PackBack(tells.transform);
                var bag = TrashBag.Spawn(e.contents, back, Quaternion.identity);
                if (bag == null) break;
                string reason;
                bool fits = r is FootHatch door ? door.CanAcceptFrom(client, e.contents, out reason) : r.CanAccept(bag, out reason);
                if (!fits)
                {
                    // Not consumed, never published: the contents simply stay in the pack.
                    bag.gameObject.SetActive(false);
                    Destroy(bag.gameObject);
                    why = reason ?? "";
                    break;
                }
                // Credit the actor exactly like a hand toss (receivers read LastHolder), without touching their hands.
                if (carrier != null) bag.BeginCarry(carrier, carrier.HoldPoint);
                bag.SetInFlight();
                list.RemoveAt(list.Count - 1);
                PackPublish(client);
                r.Accept(bag);
                if (bag.State != CarryState.Consumed) { bag.NoBurstUntil = Time.time + 3f; bag.EndCarry(Vector3.up, false); } // a receiver that changed its mind
                if (r is TruckHopper hopper)
                {
                    pay += GameSession.ToCents(hopper.LastPay);
                    business = Businesses.Get(hopper.LastPayBusiness).Name ?? "";
                    if (hopper.LastCollectFeedback.curbCleared) curbBonus += hopper.LastCollectFeedback.curbBonusCents;
                }
                else if (r is FootHatch hatch)
                {
                    pay += hatch.LastPayCents;
                    foot += hatch.LastFootCents;
                    business = hatch.LastBusinessName ?? "";
                    if (hatch.LastFeedback.curbCleared) curbBonus += hatch.LastFeedback.curbBonusCents;
                }
                bags++;
                PackFxRpc(PackFxUnload, client, back, at);
                yield return new WaitForSeconds(interval);
            }
        }
        finally { packUnloading.Remove(client); }
        NetAudit.Event("pack", new Dictionary<string, object>
        {
            { "op", "unload" }, { "client", (long)client }, { "n", bags }, { "pay", pay }, { "foot", foot }, { "why", why },
        });
        if (client == LocalId) ShowPackResult(bags, pay, foot, curbBonus, business, why, at, false);
        else if (IsSpawned)
            PackResultRpc(bags, pay, foot, curbBonus, Fixed64(business), Fixed64(why), at, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    /// <summary>Host: tell a REMOTE actor what a hand-tossed bag earned at a walk-up hatch (the host's own HUD shows its own).</summary>
    internal void HostNotifyHatchPay(ulong client, long payCents, long footCents, CurbTrash.CollectFeedback feedback, string business, Vector3 at)
    {
        if (!IsServer || !IsSpawned || client == LocalId || client == FeedbackAudience.UnknownFinder) return;
        if (packUnloading.Contains(client)) return; // the unload's own summary covers it
        PackResultRpc(1, payCents, footCents, feedback.curbCleared ? feedback.curbBonusCents : 0, Fixed64(business ?? ""), default, at,
                      RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    // ==================================================================================== host: the Grabby Hand's second bag

    void HostPoleSecond(ulong client, uint id, string curbKey, Vector3 at, Carryable direct)
    {
        if (!IsServer || !HaulerRules.PoleGrabsTwo(HaulerLevel(client, CallingIds.GrabberPole))) return;
        if (!PackActor(client, out var tells, out _, out _)) return;
        // Only as the second half of a squeeze, and only once per squeeze. In a session the host must have just accepted the
        // first grab (poleAt); without one (solo) the local pole's own cooldown paces it.
        float now = Time.unscaledTime;
        bool used = poleSecondFor.TryGetValue(client, out var done);
        if (active)
        {
            if (!poleAt.TryGetValue(client, out var first) || now - first > HaulerTuning.TwoBagWindow) return;
            if (used && done >= first) return;
        }
        else if (client != LocalId || used && now - done < GrabberPole.Cooldown - 0.1f) return;
        poleSecondFor[client] = now;

        NetCarry nc = null;
        Carryable item = null;
        if (active)
        {
            nc = Get(id);
            if (nc == null && !string.IsNullOrEmpty(curbKey))
            {
                if (byBagId.TryGetValue(curbKey, out var published)) nc = Get(published);
                if (nc == null) nc = HostPublishCurb(curbKey, at);
            }
            if (nc == null || nc.Item == null || nc.St.Phase != HandsPhase.Rest || nc.WindowOpen || nc.Announced) return;
            item = nc.Item;
        }
        else if (client == LocalId) item = direct;
        if (!(item is TrashBag bag) || bag.State != CarryState.Resting || bag.Holder != null || bag.HasBurst || bag.IsInScriptedFlight) return;

        Vector3 hand = tells.transform.position + Vector3.up * 1.1f;
        float reach = HaulerRules.PoleReach(HaulerLevel(client, CallingIds.GrabberPole));
        Vector3 from = bag.WorldCenter;
        if (!PoleTargeting.WithinReach(hand, from, reach + 0.6f) || !PoleTargeting.ClearPath(hand, from, tells.transform, bag.transform)) return;
        if (!Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(bag.transform.position))) return;

        if (PackRefusal(client, bag.Contents) == null) { HostPackTakeIn(client, bag, nc); return; }

        // No room (or no pack): it lands at your feet instead.
        Vector3 feet = tells.transform.position + Flat(tells.transform.forward) * 0.8f + Vector3.up * 0.7f;
        bag.NoBurstUntil = Time.time + 2f;
        if (nc != null)
        {
            var s = nc.St;
            s.phase = (byte)HandsPhase.Flight;
            s.holder = NetworkManager.ServerClientId;
            s.other = None;
            s.flags = 0;
            s.pos = feet;
            s.rot = bag.transform.rotation;
            s.vel = Vector3.zero;
            bag.NetSetState(CarryState.Thrown);
            bag.NetSetKinematic(true);
            HostSet(nc, s);
        }
        else
        {
            bag.transform.position = feet;
            if (bag.Body != null) { bag.Body.position = feet; bag.Body.linearVelocity = Vector3.zero; bag.Body.WakeUp(); }
        }
        PackFxRpc(PackFxYank, client, from, feet);
    }

    // ==================================================================================== host upkeep (HaulerRuntime calls it every frame)

    internal void PackHostTick()
    {
        if (!IsServer || !IsSpawned) return;
        if (!packGuarded)
        {
            packGuarded = true;
            SessionService.SetGuard(MsgPack, new NetGuard(12f, 24, 192));
        }
        if (packBags.Count == 0 || Time.unscaledTime < nextPackTick) return;
        nextPackTick = Time.unscaledTime + 0.25f;
        packScratch.Clear();
        packScratch.AddRange(packBags.Keys);
        foreach (ulong client in packScratch)
        {
            if (!packBags.TryGetValue(client, out var list) || list.Count == 0) { packBags.Remove(client); continue; }
            bool connected = false;
            foreach (ulong id in NetworkManager.ConnectedClientsIds) if (id == client) { connected = true; break; }
            var tells = PlayerTells.For(client);
            if (tells != null) packLastPos[client] = tells.transform.position;
            if (!connected)
            {
                // They left with a full pack: it stays in the Town, where they last stood.
                HostPackSpill(client, false, packLastPos.TryGetValue(client, out var last) ? last : (Vector3?)null);
                packLastPos.Remove(client); packCrushAt.Remove(client); poleSecondFor.Remove(client);
                continue;
            }
            var player = tells != null ? tells.Player : null;
            if (player != null && !packUnloading.Contains(client) &&
                (player.IsDriving || player.IsPlayingChess || CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client) ||
                 list.Count > HaulerRules.PackMaxBags(PackSlotsOf(client), true)))
                HostPackSpill(client, false, null);
        }
    }

    // ==================================================================================== the actor's own HUD

    static void ShowPackNope(string why)
    {
        var me = PlayerTells.LocalTells;
        if (me != null) GameHUD.FloatingText(me.transform.position + Vector3.up * 2.1f, why, new Color(1f, 0.45f, 0.3f), 1f);
        Sfx.Play("buzz", me != null ? me.transform.position : (Vector3?)null, 0.5f, 1.1f);
    }

    /// <summary>The actor's summary. <paramref name="showPay"/> false on the host: its receivers already floated each bag's pay.</summary>
    static void ShowPackResult(int bags, long payCents, long footCents, long curbBonusCents, string business, string why, Vector3 at, bool showPay)
    {
        if (bags > 0)
        {
            if (showPay && payCents > 0)
            {
                GameHUD.FloatingText(at + Vector3.up * 1.15f, $"+{Economy.Money(payCents / 100f)}" + (string.IsNullOrEmpty(business) ? "" : $" to {business}"), CurbTrash.FreshColor, 1.15f);
                Sfx.Play("cash", at, 0.8f, 1.1f);
            }
            if (showPay && footCents > 0)
                GameHUD.FloatingText(at + Vector3.up * 1.75f, $"+{Economy.Money(footCents / 100f)} on foot!", new Color(1f, 0.85f, 0.3f), 1f);
            if (showPay && curbBonusCents > 0) GameHUD.Toast($"Curb clean! +{Economy.Money(curbBonusCents / 100f)}", ToastStyle.Good);
            if (bags > 1) GameHUD.FloatingText(at + Vector3.up * 2.3f, bags >= 6 ? $"BALE OF {bags}!" : $"{bags} off the back!", new Color(1f, 0.8f, 0.3f), 1.1f);
        }
        if (!string.IsNullOrEmpty(why)) GameHUD.Toast(why, ToastStyle.Bad);
    }
}

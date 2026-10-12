using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One curb bag as every machine sees it: no contents (Docs/Multiplayer.md §5.1, §6.3).</summary>
public struct CurbBagNet : INetworkSerializable, IEquatable<CurbBagNet>
{
    /// <summary><see cref="claimedBy"/> when nobody is carrying it.</summary>
    public const ulong NoClaim = ulong.MaxValue;
    /// <summary><see cref="claimedBy"/> once the bag left the curb as a networked object (N2, Net/Hands): hidden at every curb.</summary>
    public const ulong Published = ulong.MaxValue - 1;

    public FixedString64Bytes bagId;
    public FixedString32Bytes stopId;
    /// <summary>GameClock.TotalHours when it was put out (lateness pay; every machine runs the host's clock).</summary>
    public double putOutAtHours;
    /// <summary>Total mass (kg): how it carries and whether it fits in a truck. The items stay on the host.</summary>
    public float massKg;
    public byte slot, sack;
    /// <summary>The client carrying it (it's hidden at the curb everywhere else), or <see cref="NoClaim"/>.</summary>
    public ulong claimedBy;

    public string BagId => bagId.ToString();
    public bool IsClaimed => claimedBy != NoClaim;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref bagId);
        s.SerializeValue(ref stopId);
        s.SerializeValue(ref putOutAtHours);
        s.SerializeValue(ref massKg);
        s.SerializeValue(ref slot);
        s.SerializeValue(ref sack);
        s.SerializeValue(ref claimedBy);
    }

    public bool Equals(CurbBagNet o) =>
        bagId.Equals(o.bagId) && stopId.Equals(o.stopId) && putOutAtHours.Equals(o.putOutAtHours) && massKg.Equals(o.massKg) &&
        slot == o.slot && sack == o.sack && claimedBy == o.claimedBy;

    public override bool Equals(object obj) => obj is CurbBagNet o && Equals(o);
    public override int GetHashCode() => bagId.GetHashCode();
}

/// <summary>
/// The Town's shared world (Docs/Multiplayer.md §13.7, §5.1, package N1), on <c>NetSession.prefab</c>.
/// <list type="bullet">
/// <item><b>Curb records.</b> The host's <see cref="GameSession.CurbBags"/> (put out by <see cref="CurbTrashSpawner"/>, which
/// only the host runs) replicate here without contents; each guest mirrors them into its own GameSession (placeholder contents
/// with the right mass) so PickupStops, the route planner and the HUD work unchanged.</item>
/// <item><b>Claims.</b> Picking a curb bag up claims it: the host confirms, and every other machine hides it at the curb, so a
/// bag picked up by one player is gone for everyone. Two grabs at once: the first wins, the other player's bag goes poof.
/// Claims die with the carrier's connection (the bag reappears at the curb).</item>
/// <item><b>Requests.</b> A guest's toss into a truck (<see cref="RequestHopper"/>) and a burst (<see cref="RequestBurst"/>) are
/// requests; the host checks them (record, claim, truck, range, capacity) and pays the business that owns the truck through
/// <see cref="Ledger"/>. Nothing is ever paid twice: the record is removed when it's paid.</item>
/// <item><b>Clock.</b> The host's clock offset and base day (<see cref="GameClock.ApplyNetClock"/> on guests).</item>
/// </list>
/// N2 replaces the claim/carry part with networked grab ownership; the records stay.
/// </summary>
[NetSessionComponent(Order = 6)]
public class WorldState : NetworkBehaviour
{
    public static WorldState Instance { get; private set; }

    /// <summary>How far (m) a guest's player may be from a truck's hopper when the host accepts its toss (lag and throws).</summary>
    public const float HopperRange = 16f;
    const string MsgClaim = "rb.curb.claim", MsgHopper = "rb.curb.hopper", MsgBurst = "rb.curb.burst";

    NetworkList<CurbBagNet> curbs;
    readonly NetworkVariable<double> clockOffset = new NetworkVariable<double>();
    readonly NetworkVariable<int> baseDay = new NetworkVariable<int>(1);
    /// <summary>The campaign contract being worked (Campaign.cs): the host writes it, every machine's HUD reads it.</summary>
    readonly NetworkVariable<CampaignDisplay> campaign = new NetworkVariable<CampaignDisplay>(CampaignDisplay.None);

    bool hostHooked, guestDirty;
    uint version;
    // guest: stable CurbBag objects per id, so TrashBags keep their records across rebuilds
    readonly Dictionary<string, CurbBag> mirrorById = new Dictionary<string, CurbBag>(StringComparer.Ordinal);
    readonly List<CurbBag> mirror = new List<CurbBag>();
    // guest: tosses waiting for the host's answer (bagId → hopper position, for the floater)
    readonly Dictionary<string, Vector3> pendingHopper = new Dictionary<string, Vector3>(StringComparer.Ordinal);

    /// <summary>Host-written records (read <see cref="GameSession.CurbBags"/> for gameplay).</summary>
    public NetworkList<CurbBagNet> Curbs => curbs;

    /// <summary>A guest's toss was paid ((bagId, payCents, business name)) — floaters, bots.</summary>
    public static event Action<string, long, string> HopperPaid;

    void Awake() => curbs = new NetworkList<CurbBagNet>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        PickupStop.HiddenHere = IsHiddenHere;
        if (IsServer)
        {
            GameSession.CurbTrashChanged += OnHostCurbsChanged;
            SessionService.PlayerLeft += OnPlayerLeft;
            hostHooked = true;
            SyncFromHost();
            PublishClock();
        }
        else
        {
            curbs.OnListChanged += OnGuestCurbsChanged;
            clockOffset.OnValueChanged += OnClockChanged;
            baseDay.OnValueChanged += OnBaseDayChanged;
            campaign.OnValueChanged += OnCampaignChanged;
            GameClock.ApplyNetClock(clockOffset.Value, baseDay.Value);
            Campaign.ApplyRemote(campaign.Value);
            RebuildMirror();
        }
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        if (!IsServer)
        {
            curbs.OnListChanged -= OnGuestCurbsChanged;
            clockOffset.OnValueChanged -= OnClockChanged;
            baseDay.OnValueChanged -= OnBaseDayChanged;
            campaign.OnValueChanged -= OnCampaignChanged;
            Campaign.ApplyRemote(CampaignDisplay.None);
            GameClock.ClearNetClock();
        }
        if (PickupStop.HiddenHere == (Func<string, bool>)IsHiddenHere) PickupStop.HiddenHere = null;
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hostHooked) return;
        GameSession.CurbTrashChanged -= OnHostCurbsChanged;
        SessionService.PlayerLeft -= OnPlayerLeft;
        hostHooked = false;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsServer) { PublishClock(); PublishCampaign(); }
        else if (guestDirty) RebuildMirror();
    }

    // ------------------------------------------------------------------ campaign

    void PublishCampaign()
    {
        var now = Campaign.Display;
        if (!campaign.Value.Equals(now)) campaign.Value = now;
    }

    void OnCampaignChanged(CampaignDisplay before, CampaignDisplay now) => Campaign.ApplyRemote(now);

    // ------------------------------------------------------------------ clock

    void PublishClock()
    {
        double off = GameClock.OffsetSeconds;
        if (Math.Abs(clockOffset.Value - off) > 0.01) clockOffset.Value = off;
        if (baseDay.Value != GameClock.BaseDay) baseDay.Value = GameClock.BaseDay;
    }

    void OnClockChanged(double before, double now) => GameClock.ApplyNetClock(now, baseDay.Value);
    void OnBaseDayChanged(int before, int now) => GameClock.ApplyNetClock(clockOffset.Value, now);

    // ------------------------------------------------------------------ host: records

    void OnHostCurbsChanged() => SyncFromHost();

    /// <summary>Make the list equal the host's GameSession.CurbBags (keeping claims).</summary>
    void SyncFromHost()
    {
        if (!IsServer || !IsSpawned) return;
        var bags = GameSession.CurbBags;
        var want = new Dictionary<string, CurbBag>(bags.Count, StringComparer.Ordinal);
        foreach (var b in bags) if (b != null && !string.IsNullOrEmpty(b.bagId)) want[b.bagId] = b;
        bool changed = false;
        for (int i = curbs.Count - 1; i >= 0; i--)
        {
            string id = curbs[i].BagId;
            if (want.Remove(id)) continue;
            curbs.RemoveAt(i);
            changed = true;
        }
        foreach (var b in want.Values)
        {
            curbs.Add(ToNet(b));
            changed = true;
        }
        if (changed) Record();
    }

    static CurbBagNet ToNet(CurbBag b)
    {
        var n = new CurbBagNet
        {
            putOutAtHours = b.putOutAtHours,
            massKg = b.contents != null ? Mathf.Round(b.contents.TotalMassKg * 100f) / 100f : 6f,
            slot = (byte)Mathf.Clamp(b.slot, 0, 255),
            sack = (byte)Mathf.Clamp(b.contents != null ? b.contents.sackVariant : 0, 0, 255),
            claimedBy = CurbBagNet.NoClaim,
        };
        n.bagId.CopyFromTruncated(b.bagId);
        n.stopId.CopyFromTruncated(b.stopId ?? "");
        return n;
    }

    int IndexOf(string bagId)
    {
        if (string.IsNullOrEmpty(bagId)) return -1;
        for (int i = 0; i < curbs.Count; i++) if (curbs[i].BagId == bagId) return i;
        return -1;
    }

    void SetClaim(int i, ulong client)
    {
        var r = curbs[i];
        if (r.claimedBy == client) return;
        r.claimedBy = client;
        curbs[i] = r;
        Record();
        GameSession.NotifyCurbTrashChanged();
    }

    void OnPlayerLeft(PlayerKey key, ulong clientId)
    {
        if (!IsServer || !IsSpawned) return;
        for (int i = 0; i < curbs.Count; i++)
            if (curbs[i].claimedBy == clientId) SetClaim(i, CurbBagNet.NoClaim); // (N2's published bags stay published)
    }

    /// <summary>
    /// Host (N2, Net/Hands): this curb bag left the curb as a networked object (someone grabbed it): it's hidden at the curb on
    /// every machine from now on; the networked bag is the only copy. Its record stays (it still pays when it reaches a truck).
    /// </summary>
    public void HostMarkPublished(string bagId)
    {
        if (!IsServer || !IsSpawned) return;
        int i = IndexOf(bagId);
        if (i >= 0) SetClaim(i, CurbBagNet.Published);
    }

    /// <summary>
    /// Host: a published bag lost its object (a dog dropped it somewhere it vanished, it was destroyed): clear the Published
    /// mark so every PickupStop shows the record at the curb again instead of hiding it for good. True when the record is
    /// unclaimed afterwards (or unknown here); false while a connected client still holds a claim on it.
    /// </summary>
    public bool HostUnpublish(string bagId)
    {
        if (!IsServer || !IsSpawned) return true;
        int i = IndexOf(bagId);
        if (i < 0) return true;
        if (curbs[i].claimedBy == CurbBagNet.Published) SetClaim(i, CurbBagNet.NoClaim);
        return !curbs[i].IsClaimed;
    }

    /// <summary>True when this curb bag is now a networked object (N2).</summary>
    public bool IsPublished(string bagId)
    {
        int i = IsSpawned ? IndexOf(bagId) : -1;
        return i >= 0 && curbs[i].claimedBy == CurbBagNet.Published;
    }

    /// <summary>Host: <paramref name="client"/> picked this bag up. False when someone else already has it (or it's gone).</summary>
    bool ServerClaim(string bagId, ulong client)
    {
        int i = IndexOf(bagId);
        if (i < 0) return false;
        var r = curbs[i];
        if (r.IsClaimed && r.claimedBy != client) return false;
        SetClaim(i, client);
        return true;
    }

    // ------------------------------------------------------------------ claims (everyone)

    /// <summary>Hidden at the curb on this machine: someone else is carrying it.</summary>
    public bool IsHiddenHere(string bagId)
    {
        if (!IsSpawned || string.IsNullOrEmpty(bagId)) return false;
        int i = IndexOf(bagId);
        if (i < 0) return false;
        var r = curbs[i];
        return r.IsClaimed && r.claimedBy != NetworkManager.LocalClientId;
    }

    /// <summary>The local player picked up a curb bag: claim it (host: now; guest: ask).</summary>
    public void RequestClaim(TrashBag bag)
    {
        if (bag == null || bag.Contents == null || !IsSpawned) return;
        string id = bag.Contents.bagId;
        if (IndexOf(id) < 0) return; // not a curb bag (any more)
        if (IsServer)
        {
            if (!ServerClaim(id, NetworkManager.ServerClientId)) LoseBag(id, "Someone else grabbed that one!");
            return;
        }
        ClaimRpc(Fixed64(id));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ClaimRpc(FixedString64Bytes bagId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgClaim, sender, 64)) return;
        string id = bagId.ToString();
        if (ServerClaim(id, sender)) return;
        NetAudit.Event("curb", new Dictionary<string, object> { { "op", "claim-refused" }, { "bag", id }, { "client", sender } });
        LoseBagRpc(bagId, Fixed128("Someone else grabbed that one!"), RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void LoseBagRpc(FixedString64Bytes bagId, FixedString128Bytes why, RpcParams rpcParams) => LoseBag(bagId.ToString(), why.ToString());

    /// <summary>This machine's copy of a bag lost the race (or the host refused it): it goes poof out of the player's hands.</summary>
    void LoseBag(string bagId, string why)
    {
        pendingHopper.Remove(bagId);
        var held = TrashCarrier.Local != null ? TrashCarrier.Local.Held as TrashBag : null;
        if (held != null && held.Contents != null && held.Contents.bagId == bagId)
        {
            Vector3 at = held.WorldCenter;
            held.MarkConsumed();
            Juice.ShrinkAndDestroy(held.gameObject, 0.2f);
            Fx.Dust(at, 0.8f);
        }
        foreach (var stop in PickupStop.All)
        {
            if (stop == null) continue;
            foreach (var b in stop.SpawnedBags)
            {
                if (b == null || b.Contents == null || b.Contents.bagId != bagId || b.State == CarryState.Consumed) continue;
                Vector3 at = b.WorldCenter;
                b.MarkConsumed();
                Juice.ShrinkAndDestroy(b.gameObject, 0.2f);
                Fx.Dust(at, 0.8f);
                Sfx.Play("pop", at, 0.6f, 1.2f);
            }
        }
        if (!string.IsNullOrEmpty(why)) GameHUD.Toast(why, ToastStyle.Bad);
    }

    // ------------------------------------------------------------------ hopper

    /// <summary>Guest: the local player's curb bag went into <paramref name="hopper"/>; the host checks it and pays the truck's business.</summary>
    public void RequestHopper(TrashBag bag, TruckHopper hopper)
    {
        if (bag == null || bag.Contents == null || hopper == null || !IsSpawned || IsServer) return;
        var truck = hopper.GetComponentInParent<NetworkObject>();
        if (truck == null || !truck.IsSpawned)
        {
            LoseBag(bag.Contents.bagId, "That truck isn't in the Town.");
            return;
        }
        pendingHopper[bag.Contents.bagId] = hopper.TossTargetPosition;
        HopperRpc(Fixed64(bag.Contents.bagId), new NetworkObjectReference(truck), bag.ByArm);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void HopperRpc(FixedString64Bytes bagId, NetworkObjectReference truckRef, bool byArm, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgHopper, sender, 96)) return;
        string id = bagId.ToString();
        string why = ServerHopper(id, truckRef, byArm, sender, out long payCents, out string business,
                                  out CurbTrash.CollectFeedback feedback, out Vector3 fxPos);
        NetAudit.Event("curb", new Dictionary<string, object>
        {
            { "op", why == null ? "hopper" : "hopper-refused" }, { "bag", id }, { "client", sender }, { "business", business },
            { "pay", payCents }, { "why", why },
        });
        if (why != null)
        {
            int i = IndexOf(id);
            if (i >= 0 && curbs[i].claimedBy == sender) SetClaim(i, CurbBagNet.NoClaim); // back to the curb, not lost
        }
        HopperResultRpc(bagId, why == null, payCents, feedback.curbBonusCents, feedback.curbCleared,
                        Fixed64(feedback.label ?? ""), Fixed64(feedback.address ?? ""),
                        Fixed128(why ?? Businesses.Get(business).Name), RpcTarget.Single(sender, RpcTargetUse.Temp));
        if (why == null) BroadcastCollectionFx(fxPos, feedback, sender);
    }

    /// <summary>Host: check and collect a guest's toss. Null when paid; else why not.</summary>
    string ServerHopper(string bagId, NetworkObjectReference truckRef, bool byArm, ulong sender, out long payCents,
                        out string business, out CurbTrash.CollectFeedback feedback, out Vector3 fxPos)
    {
        payCents = 0;
        business = null;
        feedback = default;
        fxPos = default;
        var rec = GameSession.FindCurbBag(bagId);
        int i = IndexOf(bagId);
        if (rec == null || rec.contents == null || i < 0) return "Too late: that bag's already been picked up.";
        if (curbs[i].claimedBy == CurbBagNet.Published) return "That bag's in someone's hands.";
        if (curbs[i].IsClaimed && curbs[i].claimedBy != sender) return "Someone else has that bag.";
        if (!truckRef.TryGet(out NetworkObject truck, NetworkManager)) return "That truck isn't in the Town.";
        var hopper = truck.GetComponentInChildren<TruckHopper>();
        if (hopper == null) return "That's not a truck.";
        if (NetworkManager.ConnectedClients.TryGetValue(sender, out var cc) && cc.PlayerObject != null &&
            (cc.PlayerObject.transform.position - hopper.transform.position).sqrMagnitude > HopperRange * HopperRange)
            return "Too far from the truck.";
        business = hopper.ResolveBusiness();
        if (Businesses.TryGetTruckLoad(business, out float kg, out float cap) && kg + rec.contents.TotalMassKg > cap + 0.001f)
            return $"Truck's full! ({kg:0}/{cap:0} kg)";
        fxPos = hopper.TossTargetPosition;
        float pay = TruckHopper.CollectCurbBag(rec.contents, business, fxPos, byArm, rec.stopId, false, out feedback);
        payCents = GameSession.ToCents(pay);
        return null;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void HopperResultRpc(FixedString64Bytes bagId, bool ok, long payCents, long bonusCents, bool curbCleared,
                         FixedString64Bytes label, FixedString64Bytes address, FixedString128Bytes text, RpcParams rpcParams)
    {
        string id = bagId.ToString();
        Vector3 at = pendingHopper.TryGetValue(id, out var p) ? p : Vector3.zero;
        pendingHopper.Remove(id);
        if (!ok)
        {
            GameHUD.Toast(text.ToString(), ToastStyle.Bad);
            GameHUD.FloatingText(at + Vector3.up * 0.6f, "NOPE", new Color(1f, 0.35f, 0.25f), 1.1f);
            return;
        }
        string business = text.ToString();
        if (payCents > 0)
        {
            GameHUD.FloatingText(at + Vector3.up * 1.15f, $"+{Economy.Money(payCents / 100f)} {label}" + (string.IsNullOrEmpty(business) ? "" : $" to {business}"),
                                 CurbTrash.FreshColor, 1.1f);
            Sfx.Play("cash", at, 0.8f, 1.1f);
        }
        if (curbCleared)
        {
            string where = address.ToString();
            GameHUD.Toast(string.IsNullOrEmpty(where) ? $"Curb clean! +{Economy.Money(bonusCents / 100f)}"
                          : $"Curb clean! {where} +{Economy.Money(bonusCents / 100f)}", ToastStyle.Good);
        }
        try { HopperPaid?.Invoke(id, payCents, business); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Host mirrors only spatial collection effects. Personal pay/bonus HUD stays on the actor's machine.</summary>
    public void BroadcastCollectionFx(Vector3 hopperAt, CurbTrash.CollectFeedback feedback,
                                      ulong actorClientId = FeedbackAudience.UnknownFinder)
    {
        if (!IsSpawned || !IsServer) return;
        CollectionFxRpc(hopperAt, actorClientId, feedback.tipped, feedback.curbCleared, feedback.curbFxPos);
    }

    [Rpc(SendTo.NotServer)]
    void CollectionFxRpc(Vector3 hopperAt, ulong actorClientId, bool tipped, bool curbCleared, Vector3 curbAt)
    {
        var pc = PlayerController.Local; // coop:allow(local) distance filter for this observer's spatial collection feedback only
        if (pc == null) return;
        if (FeedbackAudience.Nearby((pc.transform.position - hopperAt).sqrMagnitude, 28f))
        {
            // The actor already saw the local hopper gulp; nearby spectators get its spatial counterpart.
            if (NetworkManager.LocalClientId != actorClientId)
            {
                Sfx.Play("toss_in", hopperAt, 0.65f);
                Fx.Dust(hopperAt, 0.8f);
            }
            if (tipped) Fx.Burst(hopperAt + Vector3.up * 0.8f, new Color(1f, 0.82f, 0.2f), 10, 3.5f);
        }
        if (curbCleared && FeedbackAudience.Nearby((pc.transform.position - curbAt).sqrMagnitude, 28f))
        {
            Sfx.Play("ding", curbAt, 0.8f, 1.2f);
            Fx.Burst(curbAt, new Color(0.55f, 1f, 0.95f), 22, 4.5f);
        }
    }

    // ------------------------------------------------------------------ bursts

    /// <summary>Guest: the local player's curb bag burst; the host drops its record (no pay).</summary>
    public void RequestBurst(string bagId)
    {
        if (!IsSpawned || IsServer || string.IsNullOrEmpty(bagId) || IndexOf(bagId) < 0) return;
        BurstRpc(Fixed64(bagId));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BurstRpc(FixedString64Bytes bagId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed(MsgBurst, sender, 64)) return;
        string id = bagId.ToString();
        int i = IndexOf(id);
        if (i < 0 || (curbs[i].IsClaimed && curbs[i].claimedBy != sender)) return;
        var rec = GameSession.FindCurbBag(id);
        if (rec != null) CurbTrash.RemoveRecord(rec.contents ?? new BagContents { bagId = id });
        NetAudit.Event("curb", new Dictionary<string, object> { { "op", "burst" }, { "bag", id }, { "client", sender } });
    }

    // ------------------------------------------------------------------ guest: mirror

    void OnGuestCurbsChanged(NetworkListEvent<CurbBagNet> e) => guestDirty = true;

    void RebuildMirror()
    {
        guestDirty = false;
        mirror.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < curbs.Count; i++)
        {
            var n = curbs[i];
            string id = n.BagId;
            if (!seen.Add(id)) continue;
            if (!mirrorById.TryGetValue(id, out var b))
            {
                string stop = n.stopId.ToString();
                var house = CurbTrashSpawner.FindHouse(stop);
                b = new CurbBag
                {
                    bagId = id, stopId = stop,
                    // Placeholder contents: the right mass and sack, no items (the host rolled them and keeps them, §6.3).
                    contents = new BagContents { bagId = id, foundAt = house != null ? house.address : "", bagMassKg = Mathf.Max(0.15f, n.massKg), sackVariant = n.sack },
                };
                mirrorById[id] = b;
            }
            b.slot = n.slot;
            b.putOutAtHours = n.putOutAtHours;
            mirror.Add(b);
        }
        var gone = new List<string>();
        foreach (var id in mirrorById.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (var id in gone) mirrorById.Remove(id);
        GameSession.NetMirror.SetCurbs(mirror);
        CurbTrash.Invalidate();
        Record();
    }

    // ------------------------------------------------------------------ audit

    void Record()
    {
        if (!NetAuditNet.Enabled) return;
        NetAuditNet.Record("curbs", ++version == 0 ? ++version : version, CurbHash(out int n), n);
    }

    ulong CurbHash(out int count)
    {
        ulong h = WorldHash.Seed;
        var ids = new List<string>(curbs.Count);
        for (int i = 0; i < curbs.Count; i++) ids.Add(curbs[i].BagId + "|" + curbs[i].stopId + "|" + curbs[i].slot + "|" + (curbs[i].IsClaimed ? 1 : 0));
        ids.Sort(StringComparer.Ordinal);
        foreach (var s in ids) h = WorldHash.Fnv(h, s);
        count = ids.Count;
        return h;
    }

    /// <summary>NetAudit hash domain "curbs": every machine must converge on the same records (id, house, slot, claimed).</summary>
    static bool CurbProbe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Seed;
        count = 0;
        var w = Instance;
        if (w == null || !w.IsSpawned) return false;
        hash = w.CurbHash(out count);
        return true;
    }

    /// <summary>
    /// NetAudit hash domain "clock": the clock's inputs (base day, offset to the second) must match, so with NGO's shared server
    /// time every machine shows the same day, time and bag ages. (Hours themselves would differ by the probe's travel time.)
    /// </summary>
    static bool ClockProbe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Fnv(WorldHash.Seed, GameClock.BaseDay);
        hash = WorldHash.Fnv(hash, (long)Math.Round(GameClock.OffsetSeconds));
        count = GameClock.Day;
        return Instance != null && Instance.IsSpawned;
    }

    static bool Allowed(string msg, ulong sender, int bytes)
    {
        if (SessionService.GuardFor(msg).Allow(sender, bytes, Time.realtimeSinceStartupAsDouble)) return true;
        NetAudit.Event("guard", $"{msg} from client {sender} over its limit");
        return false;
    }

    static FixedString64Bytes Fixed64(string s)
    {
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    static FixedString128Bytes Fixed128(string s)
    {
        var f = new FixedString128Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    // ------------------------------------------------------------------ statics

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        HopperPaid = null;
    }

    // After NetAuditNet's and TrashBag's SubsystemRegistration resets.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        NetAuditNet.RegisterProbe("curbs", CurbProbe);
        NetAuditNet.RegisterProbe("clock", ClockProbe);
    }
    // N2 (Net/Hands/HandsNet) replaced the N1 pickup claim: a local grab of a curb bag asks the host for ownership, and the host
    // publishes it (HostMarkPublished) as a networked bag in the grabber's hands. RequestClaim stays for old callers.
}

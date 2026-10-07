using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Package N2 "Hands on" + N2b "Tug of war" (Docs/Multiplayer.md §4.5–4.6, §5.1–5.2, §13.5b), on <c>NetSession.prefab</c>.
/// Everyone can grab, carry, throw, pass, bonk, shove, pick up flopped friends, hitch a ride on any truck's rear step and
/// tug over a bag; employment doesn't matter (§13.5b). The only limit is §13.5: you can't touch trash inside a shop you
/// don't work in (<see cref="Permissions"/>), so tugs over shop trash only happen between its owner and employees.
/// <para><b>Carryables</b> (this file). The host publishes every bag and loose item it has (curb bags stay records until
/// someone grabs one: the host then publishes it already in their hands, §4.6). Each machine keeps its own object per id
/// (<see cref="NetCarry"/>); guests build copies from a <see cref="CarryDesc"/> (no contents: the host keeps them, §6.3).
/// <list type="bullet">
/// <item><b>Grab</b>: your heave starts at once; the host confirms ownership immediately, or opens a 250 ms window when
/// someone else is within 3 m. A qualifying second grab in the window starts a tug (or, with tugs off, the earlier
/// clamped press stamp wins: "YOINK!"). Throws pressed during the heave are buffered (<see cref="TrashCarrier.NetCanRelease"/>).</item>
/// <item><b>Throws follow ownership</b>: the thrower simulates the flight and streams it; the host never takes a flight back
/// while it flies, so a thrown bag never snaps back. It detects catches, bonks, receivers and bursts; the host confirms.</item>
/// <item><b>Receivers</b> (hoppers, bins, compactor…): a guest's delivery is a request; the host runs the real
/// <c>Accept</c> on its copy (pay goes by the truck's business, N1) and everyone plays the gulp.</item>
/// <item><b>PASS</b>: look at a teammate within 8 m (15° aim assist) and press E; a scripted lob lands in their hands.</item>
/// </list></para>
/// Players (bonk, shove, flop, carry, throw, hitch) live in <c>HandsNet.Players.cs</c>, shared grips in
/// <c>HandsNet.Tug.cs</c>. Solo never activates any of it.
/// </summary>
[NetSessionComponent(Order = 30)]
public partial class HandsNet : NetworkBehaviour
{
    public static HandsNet Instance { get; private set; }
    public const ulong None = ulong.MaxValue;

    /// <summary>Tugs on (N2b). Off = Crew Up Lite's tie rule: the earlier clamped press wins (bots: <c>-handsnotug</c>).</summary>
    public static bool TugsEnabled = true;

    /// <summary>A guest's delivery was paid (bagId-or-id, cents, business name): floaters, bots.</summary>
    public static event Action<string, long, string> DeliveryPaid;
    /// <summary>Any item state this machine applied (bots and the audit).</summary>
    public static event Action<CarryNetState> StateApplied;

    readonly Dictionary<uint, NetCarry> items = new Dictionary<uint, NetCarry>();
    readonly Dictionary<string, uint> byBagId = new Dictionary<string, uint>(StringComparer.Ordinal);
    readonly Dictionary<string, Carryable> pendingCurb = new Dictionary<string, Carryable>(StringComparer.Ordinal);
    readonly Dictionary<ulong, float> lastGrabAt = new Dictionary<ulong, float>();
    uint nextId = 1;
    /// <summary>Host: bumped on every broadcast change; guests record the same versions (NetAudit "hands" domain).</summary>
    uint ver;
    /// <summary>&gt;0 while applying the network's word (local carrier events are ignored meanwhile).</summary>
    internal int applying;
    bool active, hooked;
    float nextScan;
    TrashCarrier hookedCarrier;

    public ulong LocalId => NetworkManager != null ? NetworkManager.LocalClientId : 0;
    /// <summary>NGO server time on this machine (guests: the host's time as last heard).</summary>
    public double ServerNow => NetworkManager != null ? NetworkManager.ServerTime.Time : 0;
    /// <summary>This machine's best guess of the host's clock right now (press stamps, §4.6).</summary>
    public double StampNow => IsServer ? ServerNow : ServerNow + RttSeconds(NetworkManager.ServerClientId) * 0.5;
    /// <summary>On: a co-op session (not solo). The host publishes once a guest has joined.</summary>
    public bool Active => active;
    public IReadOnlyDictionary<uint, NetCarry> Items => items;

    /// <summary>A solo game (NetSession says Hosting only after StartHost, so ask SessionService too).</summary>
    static bool Solo => NetSession.IsSolo || SessionService.SoloOnly;

    void Awake() { AwakePlayers(); AwakeRide(); }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (Solo) return; // solo stays exactly as it was
        hooked = true;
        Carryable.NetDeliver = OnNetDeliver;
        Carryable.NetConsumed = OnNetConsumed;
        TrashBag.NetBurst = OnNetBurst;
        TrashCarrier.NetCanRelease = CanRelease;
        TrashCarrier.NetCandidates = PassCandidates;
        Carryable.NetScriptTake = OnScriptTake;
        if (IsServer)
        {
            ConfigureGuards();
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            foreach (var id in NetworkManager.ConnectedClientsIds) HostEnsureBody(id);
            if (NetworkManager.ConnectedClientsIds.Count > 1) Activate();
        }
        else active = true;
        SpawnPlayers();
        SpawnTugs();
        if (!IsServer) CarImpactSyncRpc(); // late joiners inherit the host's car timetable interruptions
        if (GetComponent<TugHud>() == null) gameObject.AddComponent<TugHud>();
        NetAudit.Event("hands", new Dictionary<string, object> { { "op", "spawn" }, { "server", IsServer }, { "tugs", TugsEnabled } });
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        DespawnPlayers();
        DespawnTugs();
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
        if (!hooked) return;
        hooked = false;
        if (Carryable.NetDeliver == (Func<Carryable, ICarryReceiver, bool>)OnNetDeliver) Carryable.NetDeliver = null;
        if (Carryable.NetConsumed == (Action<Carryable>)OnNetConsumed) Carryable.NetConsumed = null;
        if (TrashBag.NetBurst == (Func<TrashBag, Vector3, Vector3, Vector3, bool>)OnNetBurst) TrashBag.NetBurst = null;
        if (TrashCarrier.NetCanRelease == (Func<TrashCarrier, bool>)CanRelease) TrashCarrier.NetCanRelease = null;
        if (TrashCarrier.NetCandidates == (Action<TrashCarrier, List<InteractionCandidate>>)PassCandidates) TrashCarrier.NetCandidates = null;
        if (Carryable.NetScriptTake == (Action<Carryable>)OnScriptTake) Carryable.NetScriptTake = null;
        HookCarrier(null);
        if (NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        // Leave every copy as a plain local object (a guest's copies vanish with the session; nothing is paid twice).
        foreach (var nc in new List<NetCarry>(items.Values))
            if (nc != null && nc.Item != null) { nc.Item.NetRemote = false; nc.Item.NetId = 0; Destroy(nc); }
        items.Clear();
        byBagId.Clear();
        pendingCurb.Clear();
        pendingPublish.Clear();
        sentPublish.Clear();
        goneHere.Clear();
    }

    void Activate()
    {
        if (active) return;
        active = true;
        HostScan();
        NetAudit.Event("hands", new Dictionary<string, object> { { "op", "active" }, { "items", items.Count } });
    }

    void OnClientConnected(ulong client)
    {
        if (!IsServer) return;
        HostEnsureBody(client);
        if (client == NetworkManager.ServerClientId) return; // the host alone: nobody to publish to yet
        Activate();
        // Late joiner: every published item, as it is now (§9 rule 10: explicit sync for late joiners).
        foreach (var nc in items.Values)
            if (nc != null && nc.Item != null && nc.St.Phase != HandsPhase.Gone)
                ItemRpc(nc.Desc, Snapshot(nc), 0, nc.Data ?? "", RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    void OnClientDisconnected(ulong client)
    {
        if (!IsServer) return;
        foreach (var nc in new List<NetCarry>(items.Values))
        {
            if (nc == null || nc.Item == null) continue;
            if (nc.WindowOpen) nc.Contenders.RemoveAll(c => c.client == client);
            bool theirs = (nc.St.Phase == HandsPhase.Held || nc.St.Phase == HandsPhase.Flight) && nc.St.holder == client;
            if (!theirs) continue;
            // Their hands or their flight: the host takes it where its copy is, and lets it fall (§5.1: host takes it back).
            var s = Snapshot(nc);
            s.phase = (byte)HandsPhase.Flight;
            s.holder = NetworkManager.ServerClientId;
            s.flags = 0;
            s.vel = Vector3.zero;
            HostSet(nc, s);
        }
        HostDropOriginItems(client);
        HostPlayerLeft(client);
    }

    // ==================================================================================== per frame

    void Update()
    {
        if (!IsSpawned || !hooked) return;
        if (TrashCarrier.Local != hookedCarrier) HookCarrier(TrashCarrier.Local);
        if (IsServer)
        {
            if (!active && NetworkManager.ConnectedClientsIds.Count > 1) Activate();
            if (active && Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.25f;
                HostScan();
            }
            HostWindows();
            HostPlayersTick();
            HostTugsTick();
        }
        else if (active) GuestScan();
        PlayersTick();
    }

    void HookCarrier(TrashCarrier c)
    {
        if (hookedCarrier != null)
        {
            hookedCarrier.PickedUp -= OnLocalPickedUp;
            hookedCarrier.Released -= OnLocalReleased;
        }
        hookedCarrier = c;
        if (c != null)
        {
            c.PickedUp += OnLocalPickedUp;
            c.Released += OnLocalReleased;
        }
    }

    // ==================================================================================== lookups

    public TrashCarrier CarrierOf(ulong client)
    {
        var pb = PlayerBody.For(client);
        return pb != null ? pb.Carrier : null;
    }

    public NetCarry Get(uint id) => id != 0 && items.TryGetValue(id, out var nc) && nc != null ? nc : null;

    /// <summary>Is <paramref name="client"/> holding a networked item (by the latest word this machine has)?</summary>
    public bool IsHolding(ulong client)
    {
        if (TugOf(client).HasValue) return true;
        foreach (var nc in items.Values) if (nc != null && nc.HeldBy(client)) return true;
        var c = CarrierOf(client);
        return c != null && c.IsCarrying;
    }

    internal void Forget(NetCarry nc)
    {
        if (nc == null) return;
        items.Remove(nc.Id);
        if (nc.Desc.kind == CarryDesc.KindBag)
        {
            string key = nc.Desc.key.ToString();
            if (byBagId.TryGetValue(key, out var id) && id == nc.Id) byBagId.Remove(key);
        }
    }

    internal void OnCarryDestroyed(NetCarry nc)
    {
        if (nc == null || !items.TryGetValue(nc.Id, out var cur) || cur != nc) return;
        if (IsServer && IsSpawned && !nc.Announced)
        {
            // Destroyed without being consumed (scene unload, a sweep): it's gone for everyone.
            nc.Announced = true;
            var s = nc.St;
            s.phase = (byte)HandsPhase.Gone;
            s.flags = CarryNetState.FlagReceived;
            s.seq++;
            Forget(nc);
            ItemRpc(nc.Desc, s, Bump(), "", RpcTarget.NotServer);
            Census("gone-destroyed", nc, s);
            return;
        }
        Forget(nc);
    }

    static float RttSecondsOf(NetworkManager nm, ulong client)
    {
        try
        {
            var t = nm != null && nm.NetworkConfig != null ? nm.NetworkConfig.NetworkTransport : null;
            if (t == null || client == nm.LocalClientId && nm.IsServer) return 0f;
            return Mathf.Clamp(t.GetCurrentRtt(client) / 1000f, 0f, 1.5f);
        }
        catch (Exception) { return 0.15f; }
    }

    float RttSeconds(ulong client) => RttSecondsOf(NetworkManager, client);

    // ==================================================================================== host: publishing

    void HostScan()
    {
        var all = Carryable.All;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var c = all[i];
            if (c == null || c.NetId != 0 || c.State == CarryState.Consumed || !c.isActiveAndEnabled) continue;
            if (c is TrashBag tb && (tb.HasBurst || (tb.SourceStop != null && tb.IsCurbBag && c.Holder == null))) continue; // curb: a record
            if (c.Holder != null && c.Holder == TrashCarrier.Local && pendingCurb.ContainsValue(c)) continue;
            HostPublish(c, null);
        }
    }

    /// <summary>Host: publish this object now (bots and tests; the scan does the same within 0.25 s).</summary>
    public NetCarry HostPublishNow(Carryable c)
    {
        if (!IsServer || c == null) return null;
        if (c.NetId != 0) return Get(c.NetId);
        if (!active) Activate();
        return c.NetId != 0 ? Get(c.NetId) : HostPublish(c, null);
    }

    /// <summary>Is this a curb bag the local player is lifting (waiting for the host)?</summary>
    public bool IsPendingCurb(Carryable c) => c is TrashBag tb && tb.Contents != null && tb.Contents.bagId != null &&
                                              pendingCurb.TryGetValue(tb.Contents.bagId, out var x) && x == c;

    static CarryDesc DescOf(Carryable c, uint id)
    {
        var d = new CarryDesc { mass = c.MassKg, finderClientId = FeedbackAudience.UnknownFinder };
        if (c is TrashBag tb)
        {
            d.kind = CarryDesc.KindBag;
            d.key.CopyFromTruncated(tb.Contents != null && !string.IsNullOrEmpty(tb.Contents.bagId) ? tb.Contents.bagId : "net/" + id);
            int sack = tb.Contents != null ? tb.Contents.sackVariant : 0;
            d.sack = (byte)(sack < 0 ? 255 : Mathf.Clamp(sack, 0, 254));
        }
        else if (c is LooseItem li)
        {
            d.kind = CarryDesc.KindItem;
            var it = li.Item;
            d.key.CopyFromTruncated(it != null && !string.IsNullOrEmpty(it.uid) ? it.uid : "item/" + id);
            d.defId.CopyFromTruncated(it != null ? it.defId ?? "" : "");
            d.value = it != null ? it.value : 0f;
            d.condition = it != null ? it.condition : 1f;
            d.revealed = li.Revealed;
            d.finderClientId = li.FinderClientId;
        }
        return d;
    }

    NetCarry HostPublish(Carryable c, ulong? holderOverride, bool forceRest = false)
    {
        uint id = nextId++;
        var desc = DescOf(c, id);
        var nc = c.gameObject.AddComponent<NetCarry>();
        nc.Init(this, id, desc, c);
        items[id] = nc;
        if (desc.kind == CarryDesc.KindBag)
        {
            string key = desc.key.ToString();
            byBagId[key] = id;
            if (c is TrashBag tb && tb.IsCurbBag) WorldState.Instance?.HostMarkPublished(key);
        }
        var s = new CarryNetState { id = id, seq = 1, pos = c.transform.position, rot = c.transform.rotation };
        if (forceRest) s.phase = (byte)HandsPhase.Rest;
        else if (holderOverride.HasValue) { s.phase = (byte)HandsPhase.Held; s.holder = holderOverride.Value; }
        else if (c.Holder != null && c.Holder.Player != null) { s.phase = (byte)HandsPhase.Held; s.holder = c.Holder.Player.OwnerClientId; }
        else if (c.State == CarryState.Thrown || c.IsInScriptedFlight) { s.phase = (byte)HandsPhase.Flight; s.holder = NetworkManager.ServerClientId; }
        else s.phase = (byte)HandsPhase.Rest;
        nc.St = s; // the host's object is already where it is
        nc.HostSt = s;
        c.NetRemote = !nc.LocalSim;
        nc.Data = DataOf(c);
        ItemRpc(desc, s, Bump(), nc.Data, RpcTarget.NotServer);
        return nc;
    }

    /// <summary>Host: a curb bag someone grabbed becomes a networked bag (the host's own curb copy, or a new one).</summary>
    NetCarry HostPublishCurb(string bagId, Vector3 near)
    {
        var rec = GameSession.FindCurbBag(bagId);
        if (rec == null || rec.contents == null) return null;
        TrashBag bag = null;
        if (pendingCurb.TryGetValue(bagId, out var mine) && mine is TrashBag mb && mb != null && mb.State != CarryState.Consumed) bag = mb;
        if (bag == null)
        {
            var stop = PickupStop.Find(rec.stopId);
            if (stop != null)
                foreach (var b in stop.SpawnedBags)
                    if (b != null && b.Contents != null && b.Contents.bagId == bagId && b.State != CarryState.Consumed) { bag = b; break; }
        }
        if (bag == null) bag = TrashBag.Spawn(rec.contents, near, Quaternion.identity);
        if (bag == null) return null;
        var st = bag.SourceStop;
        if (st != null) st.NotifyBagCollected(bag);
        bag.SourceStop = null;
        pendingCurb.Remove(bagId);
        // Published at rest: the grab that made it public goes through the same heave rules as any other (§4.6).
        return HostPublish(bag, null, true);
    }

    /// <summary>Host: the next version (recorded once the change is applied here).</summary>
    uint Bump()
    {
        ver++;
        if (ver == 0) ver = 1;
        RecordHash(ver);
        return ver;
    }

    void RecordHash(uint version)
    {
        if (!NetAuditNet.Enabled) return;
        NetAuditNet.Record("hands", version, HandsHash(out int n), n);
    }

    /// <summary>Every live networked item by the host's last word: id, phase, holder (Flight and Rest count the same).</summary>
    ulong HandsHash(out int count)
    {
        var ids = new List<uint>(items.Keys);
        ids.Sort();
        ulong h = WorldHash.Seed;
        count = 0;
        foreach (var id in ids)
        {
            var nc = items[id];
            if (nc == null) continue;
            var s = nc.HostSt;
            if (s.Phase == HandsPhase.Gone) continue;
            h = WorldHash.Fnv(h, (long)id);
            bool free = s.Phase == HandsPhase.Rest || s.Phase == HandsPhase.Flight;
            h = WorldHash.Fnv(h, free ? 0L : (long)s.phase);
            if (!free) h = WorldHash.Fnv(h, (long)s.holder);
            count++;
        }
        return h;
    }

    static CarryNetState Snapshot(NetCarry nc)
    {
        var s = nc.St;
        var t = nc.Item.transform;
        s.pos = t.position;
        s.rot = t.rotation;
        s.vel = nc.Item.Body != null && !nc.Item.Body.isKinematic ? nc.Item.Body.linearVelocity : Vector3.zero;
        return s;
    }

    /// <summary>Host: decide a new state, apply it here and tell everyone.</summary>
    void HostSet(NetCarry nc, CarryNetState s)
    {
        s.id = nc.Id;
        s.seq = nc.St.seq + 1;
        nc.ApplyState(s);
        nc.Item.NetRemote = !nc.LocalSim;
        ItemRpc(nc.Desc, s, Bump(), nc.Data ?? "", RpcTarget.NotServer);
        Census("state", nc, s);
        try { StateApplied?.Invoke(s); }
        catch (Exception e) { Debug.LogException(e); }
    }

    CarryNetState NewState(NetCarry nc, HandsPhase phase, ulong holder, byte flags = 0, ulong other = None)
    {
        var s = Snapshot(nc);
        s.phase = (byte)phase;
        s.holder = holder;
        s.other = other;
        s.flags = flags;
        return s;
    }

    void Census(string op, NetCarry nc, CarryNetState s)
    {
        if (!NetAuditNet.Enabled) return;
        NetAudit.Event("hands", new Dictionary<string, object>
        {
            { "op", op }, { "id", s.id }, { "seq", s.seq }, { "phase", s.Phase.ToString() }, { "holder", s.holder == None ? -1 : (long)s.holder },
            { "flags", s.flags }, { "key", nc != null ? nc.Desc.key.ToString() : "" }, { "kind", nc != null ? nc.Desc.kind : 0 },
        });
    }

    // ==================================================================================== everyone: the host's word

    [Rpc(SendTo.SpecifiedInParams)]
    void ItemRpc(CarryDesc desc, CarryNetState s, uint version, string data, RpcParams p = default)
    {
        if (IsServer || !hooked) return;
        try { ApplyItem(desc, s, data); }
        finally { if (version != 0) RecordHash(version); }
    }

    void ApplyItem(CarryDesc desc, CarryNetState s, string data)
    {
        if (goneHere.Contains(s.id)) return; // we consumed it here; the host's older word about it is moot
        if (!items.TryGetValue(s.id, out var nc) || nc == null)
        {
            if (s.Phase == HandsPhase.Gone) return;
            if (TryAdoptPublished(desc, s, data)) return;
            nc = BuildCopy(desc, s, data, out bool adopted);
            if (nc == null) return;
            if (adopted && s.Phase == HandsPhase.Rest)
            {
                // Our own heave of this curb bag: keep lifting it; the host's grab answer follows.
                nc.St.seq = s.seq;
                nc.HostSt = s;
                nc.PredictLocalGrab();
                return;
            }
        }
        else if (s.seq < nc.St.seq && s.Phase != HandsPhase.Gone) return; // stale (an equal seq re-applies the truth)
        else if (desc.kind == CarryDesc.KindBag && s.Phase == HandsPhase.Held && s.holder == LocalId)
        {
            // We grabbed our stale curb copy of a bag that's already public, and won it: swap the curb copy for the real one.
            string key = desc.key.ToString();
            if (pendingCurb.TryGetValue(key, out var stale) && stale != null && stale != nc.Item)
            {
                pendingCurb.Remove(key);
                applying++;
                try
                {
                    if (stale.Holder != null) stale.Holder.ReleaseToScript();
                    stale.MarkConsumed();
                }
                finally { applying--; }
                Destroy(stale.gameObject);
            }
        }
        nc.ApplyState(s);
        if (desc.kind == CarryDesc.KindItem && desc.revealed && nc.Item is LooseItem revealItem)
            ApplyNetworkReveal(nc, revealItem, desc.finderClientId);
        if (NetAuditNet.Enabled && (s.Phase == HandsPhase.Held || s.Phase == HandsPhase.Gone || s.Phase == HandsPhase.Tug)) Census("applied", nc, s);
        try { StateApplied?.Invoke(s); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Guest: our own copy of a published item (our grabbed curb copy if we raced for it, else a new one).</summary>
    NetCarry BuildCopy(CarryDesc d, CarryNetState s, string data, out bool adopted)
    {
        adopted = false;
        Carryable c = null;
        string key = d.key.ToString();
        if (d.kind == CarryDesc.KindBag && pendingCurb.TryGetValue(key, out var mine) && mine != null && mine.State != CarryState.Consumed)
        {
            c = mine;
            adopted = true;
            pendingCurb.Remove(key);
            if (c is TrashBag tb && tb.SourceStop != null) { tb.SourceStop.NotifyBagCollected(tb); tb.SourceStop = null; }
        }
        if (c == null) c = BuildObject(d, data, s.pos, s.rot);
        if (c == null) return null;
        var nc = c.gameObject.AddComponent<NetCarry>();
        nc.Init(this, s.id, d, c);
        nc.Data = data ?? "";
        c.NetRemote = true;
        if (c.Body != null && c.Holder == null) c.NetSetKinematic(true);
        items[s.id] = nc;
        if (d.kind == CarryDesc.KindBag) byBagId[key] = s.id;
        return nc;
    }

    // ==================================================================================== local player: grab / release

    bool CanRelease(TrashCarrier carrier)
    {
        var held = carrier != null ? carrier.Held : null;
        if (held == null) return true;
        if (held.NetId == 0) return !(held is TrashBag tb && tb.Contents != null && pendingCurb.ContainsKey(tb.Contents.bagId ?? ""));
        var nc = Get(held.NetId);
        return nc == null || !nc.PendingGrab;
    }

    void OnLocalPickedUp(Carryable c)
    {
        if (applying > 0 || c == null || !active) return; // (host alone: nobody to tell yet; Activate publishes later)
        var me = LocalId;
        double stamp = StampNow;
        Vector3 at = c.transform.position;
        if (c.NetId != 0)
        {
            var nc = Get(c.NetId);
            if (nc == null) return;
            if (IsServer) { HostGrab(me, c.NetId, null, stamp, at); return; } // the host decides at once (same window rules)
            nc.PredictLocalGrab();
            GrabRpc(c.NetId, default, stamp, at, LocalPlayerPos());
            return;
        }
        if (c is TrashBag tb && tb.Contents != null && !string.IsNullOrEmpty(tb.Contents.bagId) && tb.IsCurbBag)
        {
            string id = tb.Contents.bagId;
            pendingCurb[id] = c;
            if (IsServer) HostGrab(me, 0, id, stamp, at);
            else GrabRpc(0, Fixed64(id), stamp, at, LocalPlayerPos());
            return;
        }
        if (IsServer)
        {
            // An object the host hasn't published yet (just spawned): publish it in our hands.
            var nc = HostPublish(c, me);
            nc.PendingGrab = false;
        }
    }

    void OnLocalReleased(Carryable c)
    {
        if (applying > 0 || c == null) return;
        if (c.NetId == 0)
        {
            if (c is TrashBag tb && tb.Contents != null && tb.Contents.bagId != null) pendingCurb.Remove(tb.Contents.bagId);
            return;
        }
        var nc = Get(c.NetId);
        if (nc == null || c.State == CarryState.Consumed || nc.PendingGone) return;
        bool thrown = c.State == CarryState.Thrown;
        Vector3 v = c.Body != null && !c.Body.isKinematic ? c.Body.linearVelocity : Vector3.zero;
        if (IsServer) { HostRelease(LocalId, c.NetId, c.transform.position, c.transform.rotation, v, thrown); return; }
        nc.PredictLocalFlight();
        ReleaseRpc(c.NetId, c.transform.position, c.transform.rotation, v, thrown);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void GrabRpc(uint id, FixedString64Bytes curbKey, double stamp, Vector3 at, Vector3 me, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed("rb.hands.grab", sender, 112)) return;
        HostGrab(sender, id, curbKey.Length > 0 ? curbKey.ToString() : null, stamp, at, me);
    }

    Vector3 LocalPlayerPos()
    {
        var pb = PlayerBody.For(LocalId);
        return pb != null ? pb.transform.position : Vector3.zero;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReleaseRpc(uint id, Vector3 pos, Quaternion rot, Vector3 vel, bool thrown, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed("rb.hands.release", sender, 64)) return;
        HostRelease(sender, id, pos, rot, vel, thrown);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void LoseRpc(uint id, FixedString64Bytes curbKey, FixedString64Bytes why, RpcParams p = default) =>
        LocalLose(id, curbKey.ToString(), why.ToString());

    /// <summary>This machine's grab was refused. A curb copy we were lifting goes back down; a networked one follows the
    /// host's state (it arrives separately).</summary>
    void LocalLose(uint id, string curbKey, string why)
    {
        if (!string.IsNullOrEmpty(curbKey) && pendingCurb.TryGetValue(curbKey, out var c))
        {
            pendingCurb.Remove(curbKey);
            if (c != null && c.State != CarryState.Consumed)
            {
                Vector3 at = c.WorldCenter;
                applying++;
                try
                {
                    if (c.Holder != null) c.Holder.ReleaseToScript();
                    c.MarkConsumed();
                }
                finally { applying--; }
                Juice.ShrinkAndDestroy(c.gameObject, 0.2f);
                Fx.Dust(at, 0.8f);
            }
        }
        var nc = Get(id);
        if (nc != null)
        {
            nc.PendingGrab = false;
            if (IsServer) nc.ApplyState(nc.St); // the host's own refused heave: back to the truth (guests get a snapshot)
        }
        if (!string.IsNullOrEmpty(why))
        {
            var pb = PlayerBody.For(LocalId);
            if (pb != null) GameHUD.FloatingText(pb.transform.position + Vector3.up * 2.1f, why, new Color(1f, 0.45f, 0.3f), 1.1f);
        }
        NetAudit.Event("hands", new Dictionary<string, object> { { "op", "lose" }, { "id", id }, { "key", curbKey }, { "why", why } });
    }

    void HostLose(ulong client, uint id, string curbKey, string why)
    {
        NetAudit.Event("hands", new Dictionary<string, object> { { "op", "grab-refused" }, { "id", id }, { "key", curbKey ?? "" }, { "client", client }, { "why", why } });
        if (client == NetworkManager.ServerClientId) LocalLose(id, curbKey, why);
        else LoseRpc(id, Fixed64(curbKey ?? ""), Fixed64(why ?? ""), RpcTarget.Single(client, RpcTargetUse.Temp));
        // Make sure they see the truth (their predicted grab is undone by the current state).
        var nc = Get(id);
        if (nc != null && client != NetworkManager.ServerClientId)
            ItemRpc(nc.Desc, Snapshot(nc), 0, nc.Data ?? "", RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    // ==================================================================================== host: grab, heave window, release

    bool HostPlayerPos(ulong client, out Vector3 pos)
    {
        var pb = PlayerBody.For(client);
        pos = pb != null ? pb.transform.position : Vector3.zero;
        return pb != null;
    }

    bool OtherPlayerNear(Vector3 at, ulong except, float radius)
    {
        foreach (var pb in PlayerBody.All)
        {
            if (pb == null || pb.ClientId == except) continue;
            Vector3 d = pb.transform.position - at; d.y = 0f;
            if (d.sqrMagnitude <= radius * radius) return true;
        }
        return false;
    }

    double ClampStamp(ulong client, double stamp)
    {
        double arrival = ServerNow;
        double lo = arrival - RttSeconds(client) - HandsTuning.StampSlack;
        return stamp < lo ? lo : stamp > arrival ? arrival : stamp;
    }

    void HostGrab(ulong client, uint id, string curbKey, double stamp, Vector3 at, Vector3? claimed = null)
    {
        if (!active) Activate();
        NetCarry nc = Get(id);
        if (nc == null && !string.IsNullOrEmpty(curbKey))
        {
            if (byBagId.TryGetValue(curbKey, out var pid)) nc = Get(pid);
            if (nc == null) nc = HostPublishCurb(curbKey, at);
        }
        if (nc == null || nc.Item == null || nc.St.Phase == HandsPhase.Gone) { HostLose(client, id, curbKey, "Too slow!"); return; }
        id = nc.Id;
        Vector3 itemPos = nc.Item.transform.position;
        if (!Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(itemPos))) { HostLose(client, id, curbKey, Permissions.NotYourShop); return; }
        var body = BodyOf(client);
        if (TugOf(client).HasValue) { HostLose(client, id, curbKey, "Both hands are on a bag!"); return; }
        if (body.State != BodyState.Normal && body.State != BodyState.Riding || body.carrying != None) { HostLose(client, id, curbKey, "Hands busy!"); return; }
        // Range: the host's (interpolated, ~0.2 s old) view of the player, or where they say they stand if that's within a few
        // metres of it (friends' Towns: lag fairness over anti-cheat, §4.12).
        bool known = HostPlayerPos(client, out var ppos);
        if (known && claimed.HasValue && (claimed.Value - ppos).sqrMagnitude <= Sq(HandsTuning.ClaimSlack) &&
            (claimed.Value - itemPos).sqrMagnitude < (ppos - itemPos).sqrMagnitude) ppos = claimed.Value;
        if (known && (ppos - itemPos).sqrMagnitude > Sq(HandsTuning.GrabRange + HandsTuning.LagSlack + 2f))
        { HostLose(client, id, curbKey, "Too far!"); return; }
        foreach (var other in items.Values)
            if (other != null && other != nc && other.HeldBy(client)) { HostLose(client, id, curbKey, "Hands full!"); return; }

        double cs = ClampStamp(client, stamp);
        float now = Time.unscaledTime;
        bool recentGrab = lastGrabAt.TryGetValue(client, out var lg) && now - lg < HandsTuning.RegrabCooldown;
        lastGrabAt[client] = now;

        if (nc.WindowOpen)
        {
            if (nc.Contenders.Exists(x => x.client == client)) return;
            if (recentGrab || !Qualifies(client, nc, ppos)) { HostLose(client, id, curbKey, "Too slow!"); return; }
            nc.Contenders.Add((client, cs));
            return;
        }
        switch (nc.St.Phase)
        {
            case HandsPhase.Rest:
            case HandsPhase.Flight:
                if (OtherPlayerNear(itemPos, client, HandsTuning.ContestRadius))
                {
                    nc.WindowOpen = true;
                    nc.WindowEnd = now + HandsTuning.HeaveWindow;
                    nc.Contenders.Clear();
                    nc.Contenders.Add((client, cs));
                    HostSet(nc, NewState(nc, HandsPhase.Held, client, CarryNetState.FlagHeave));
                }
                else HostSet(nc, NewState(nc, HandsPhase.Held, client));
                return;
            case HandsPhase.Held:
                if (nc.St.holder == client) { HostSet(nc, NewState(nc, HandsPhase.Held, client)); return; }
                if (TugsEnabled && !recentGrab && Qualifies(client, nc, ppos) && PairFree(nc.St.holder, client))
                {
                    StartTug(nc, nc.St.holder, client);
                    return;
                }
                HostLose(client, id, curbKey, "Couldn't get a grip!");
                return;
            default:
                HostLose(client, id, curbKey, "Too slow!");
                return;
        }
    }

    /// <summary>Shared grips require a nearby second player (with lag slack). PairFree separately prevents immediate re-grabs.</summary>
    bool Qualifies(ulong client, NetCarry nc, Vector3 ppos)
    {
        Vector3 d = ppos - nc.Item.transform.position; d.y = 0f;
        return d.magnitude <= HandsTuning.QualifyRange + HandsTuning.LagSlack + 0.5f;
    }

    void HostWindows()
    {
        float now = Time.unscaledTime;
        foreach (var nc in items.Values)
        {
            if (nc == null || !nc.WindowOpen || now < nc.WindowEnd) continue;
            nc.WindowOpen = false;
            var list = nc.Contenders;
            if (list.Count == 0) continue;
            int best = 0;
            for (int i = 1; i < list.Count; i++)
            {
                double a = list[i].stamp, b = list[best].stamp;
                // Presses within one frame of each other are simultaneous: a fair coin (bag id × player) decides, never the host.
                if (a < b - HandsTuning.TieWindow || (Math.Abs(a - b) <= HandsTuning.TieWindow && TieBreak(nc.Id, list[i].client) < TieBreak(nc.Id, list[best].client))) best = i;
            }
            ulong winner = list[best].client;
            NetAudit.Event("race", new Dictionary<string, object>
            {
                { "id", nc.Id }, { "key", nc.Desc.key.ToString() }, { "winner", winner }, { "host", winner == NetworkManager.ServerClientId },
                { "n", list.Count }, { "stamps", StampText(list) }, { "tug", TugsEnabled && list.Count > 1 },
            });
            if (list.Count > 1 && TugsEnabled)
            {
                int second = best == 0 ? 1 : 0;
                for (int i = 0; i < list.Count; i++) if (i != best && list[i].stamp < list[second].stamp) second = i;
                ulong challenger = list[second].client;
                for (int i = 0; i < list.Count; i++)
                    if (i != best && i != second) HostLose(list[i].client, nc.Id, null, "Too slow!");
                StartTug(nc, winner, challenger);
            }
            else
            {
                byte flags = list.Count > 1 ? CarryNetState.FlagYoink : (byte)0;
                if (nc.St.Phase == HandsPhase.Held && nc.St.holder == winner && flags == 0)
                {
                    // Uncontested after all: just confirm (clears the heave on the holder's machine).
                    HostSet(nc, NewState(nc, HandsPhase.Held, winner));
                }
                else HostSet(nc, NewState(nc, HandsPhase.Held, winner, flags));
                for (int i = 0; i < list.Count; i++)
                    if (i != best) HostLose(list[i].client, nc.Id, null, "YOINK!");
            }
            list.Clear();
        }
    }

    static uint TieBreak(uint id, ulong client) => (uint)WorldHash.Fnv(WorldHash.Fnv(WorldHash.Seed, (long)id), (long)client);

    static string StampText(List<(ulong client, double stamp)> list)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var x in list) sb.Append(x.client).Append('@').Append(x.stamp.ToString("0.000")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    void HostRelease(ulong client, uint id, Vector3 pos, Quaternion rot, Vector3 vel, bool thrown)
    {
        var nc = Get(id);
        if (nc == null || nc.Item == null) return;
        bool holder = nc.St.Phase == HandsPhase.Held && nc.St.holder == client;
        bool tugHolder = nc.St.Phase == HandsPhase.Tug && nc.St.holder == client;
        if (!holder && !tugHolder)
        {
            NetAudit.Event("hands", new Dictionary<string, object> { { "op", "release-refused" }, { "id", id }, { "client", client }, { "phase", nc.St.Phase.ToString() } });
            if (client != NetworkManager.ServerClientId) ItemRpc(nc.Desc, Snapshot(nc), 0, nc.Data ?? "", RpcTarget.Single(client, RpcTargetUse.Temp));
            else nc.ApplyState(nc.St);
            return;
        }
        if (tugHolder) CancelTugFor(nc.Id, "thrown"); // a throw beats a snatch that arrived first: thrown bags never revert
        if (nc.WindowOpen)
        {
            nc.WindowOpen = false;
            foreach (var x in nc.Contenders) if (x.client != client) HostLose(x.client, nc.Id, null, "Too slow!");
            nc.Contenders.Clear();
        }
        if (vel.sqrMagnitude > HandsTuning.MaxThrowSpeed * HandsTuning.MaxThrowSpeed) vel = vel.normalized * HandsTuning.MaxThrowSpeed;
        var s = nc.St;
        s.phase = (byte)HandsPhase.Flight;
        s.holder = client;
        s.other = None;
        s.flags = 0;
        s.pos = pos;
        s.rot = rot;
        s.vel = vel;
        HostSet(nc, s);
        NetAudit.Event("throw", new Dictionary<string, object> { { "id", id }, { "client", client }, { "thrown", thrown }, { "speed", Math.Round(vel.magnitude, 2) } });
    }

    // ==================================================================================== flight: poses and settling

    internal void SendPose(NetCarry nc, Vector3 p, Quaternion r)
    {
        if (!IsSpawned || !active) return;
        if (IsServer) PoseOutRpc(nc.Id, nc.St.seq, p, r, RpcTarget.NotServer);
        else PoseInRpc(nc.Id, p, r);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void PoseInRpc(uint id, Vector3 p, Quaternion r, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.pose", sender, 40)) return;
        var nc = Get(id);
        if (nc == null || nc.St.Phase != HandsPhase.Flight || nc.St.holder != sender) return;
        nc.OnPose(nc.St.seq, p, r);
        PoseOutRpc(id, nc.St.seq, p, r, RpcTarget.Not(new[] { sender, NetworkManager.ServerClientId }, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable)]
    void PoseOutRpc(uint id, uint seq, Vector3 p, Quaternion r, RpcParams rp = default)
    {
        if (IsServer) return;
        Get(id)?.OnPose(seq, p, r);
    }

    internal void SendSettle(NetCarry nc, Vector3 p, Quaternion r)
    {
        if (IsServer) HostSettle(LocalId, nc.Id, p, r);
        else SettleRpc(nc.Id, p, r);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SettleRpc(uint id, Vector3 p, Quaternion r, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.settle", sender, 40)) return;
        HostSettle(sender, id, p, r);
    }

    void HostSettle(ulong client, uint id, Vector3 p, Quaternion r)
    {
        var nc = Get(id);
        if (nc == null || nc.St.Phase != HandsPhase.Flight || nc.St.holder != client) return;
        var s = nc.St;
        s.phase = (byte)HandsPhase.Rest;
        s.holder = None;
        s.flags = 0;
        s.pos = p;
        s.rot = r;
        s.vel = Vector3.zero;
        HostSet(nc, s);
    }

    // ==================================================================================== receivers

    /// <summary>Carryable.NetDeliver: a networked item reached a receiver on its simulating machine.</summary>
    bool OnNetDeliver(Carryable c, ICarryReceiver r)
    {
        var nc = Get(c.NetId);
        if (nc == null) return false;
        if (IsServer || IsShopReceiver(r))
        {
            // The host runs the real Accept; and in a shop interior the machine that delivers does (shop pay goes where
            // N3/N4 route it). Either way everyone else plays the gulp when it's announced gone.
            nc.GoneFlags |= CarryNetState.FlagReceived;
            return false;
        }
        if (nc.PendingGone) return true;
        // Guest: the host decides (it has the contents, the truck's business, the bins). Hide ours meanwhile.
        bool hasTruck = false;
        NetworkObjectReference truckRef = default;
        if (r is Component comp)
        {
            var no = comp.GetComponentInParent<NetworkObject>();
            if (no != null && no.IsSpawned) { truckRef = new NetworkObjectReference(no); hasTruck = true; }
        }
        var rt = new FixedString64Bytes();
        rt.CopyFromTruncated(r.GetType().Name);
        Vector3 pos = c.transform.position;
        Quaternion rot = c.transform.rotation;
        nc.HideForHost();
        DeliverRpc(c.NetId, hasTruck, truckRef, rt, r.TossTargetPosition, pos, rot);
        return true;
    }

    /// <summary>A receiver inside a shop interior that isn't a truck (bins, compactor, table, keep crate).</summary>
    static bool IsShopReceiver(ICarryReceiver r) =>
        !(r is TruckHopper) && DepotRowLayout.InteriorAt(r.TossTargetPosition) >= 0;

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeliverRpc(uint id, bool hasTruck, NetworkObjectReference truckRef, FixedString64Bytes rtype, Vector3 rpos, Vector3 pos, Quaternion rot, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.deliver", sender, 128)) return;
        string why = HostDeliver(sender, id, hasTruck, truckRef, rtype.ToString(), rpos, pos, rot,
                                 out long payCents, out string business, out CurbTrash.CollectFeedback feedback);
        NetAudit.Event("deliver", new Dictionary<string, object>
        {
            { "id", id }, { "client", sender }, { "receiver", rtype.ToString() }, { "ok", why == null }, { "why", why }, { "pay", payCents }, { "business", business },
        });
        DeliverResultRpc(id, why == null, payCents, feedback.curbBonusCents, feedback.curbCleared,
                         Fixed64(feedback.address ?? ""), Fixed64(why ?? business ?? ""), RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    ICarryReceiver ResolveReceiver(bool hasTruck, NetworkObjectReference truckRef, string rtype, Vector3 rpos)
    {
        if (hasTruck && truckRef.TryGet(out NetworkObject no, NetworkManager))
        {
            foreach (var mb in no.GetComponentsInChildren<MonoBehaviour>())
                if (mb is ICarryReceiver r && mb.GetType().Name == rtype) return r;
        }
        ICarryReceiver best = null;
        float bestD = 3f * 3f;
        foreach (var r in CarryReceivers.All)
        {
            if (r == null || (r is UnityEngine.Object o && o == null) || r.GetType().Name != rtype) continue;
            float d = (r.TossTargetPosition - rpos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    string HostDeliver(ulong client, uint id, bool hasTruck, NetworkObjectReference truckRef, string rtype, Vector3 rpos, Vector3 pos, Quaternion rot,
                       out long payCents, out string business, out CurbTrash.CollectFeedback feedback)
    {
        payCents = 0;
        business = null;
        feedback = default;
        var nc = Get(id);
        if (nc == null || nc.Item == null) return "Too late!";
        bool ok = (nc.St.Phase == HandsPhase.Flight || nc.St.Phase == HandsPhase.Held) && nc.St.holder == client;
        if (!ok) { ItemRpc(nc.Desc, Snapshot(nc), 0, nc.Data ?? "", RpcTarget.Single(client, RpcTargetUse.Temp)); return "Not yours any more."; }
        var r = ResolveReceiver(hasTruck, truckRef, rtype, rpos);
        if (r == null) return Bounce(nc, client, pos, rot, null, "Missed!");
        if (HostPlayerPos(client, out var ppos) && (ppos - r.TossTargetPosition).sqrMagnitude > WorldState.HopperRange * WorldState.HopperRange)
            return Bounce(nc, client, pos, rot, r, "Too far away.");
        if (!Permissions.CanWorkIn(client, DepotRowLayout.InteriorAt(r.TossTargetPosition))) return Bounce(nc, client, pos, rot, r, Permissions.NotYourShop);
        var item = nc.Item;
        applying++;
        try
        {
            if (item.Holder != null) item.Holder.ReleaseToScript();
        }
        finally { applying--; }
        item.transform.SetPositionAndRotation(pos, rot);
        item.NetSetState(CarryState.Thrown);
        if (!r.CanAccept(item, out string why)) return Bounce(nc, client, pos, rot, r, why);
        nc.GoneFlags |= CarryNetState.FlagReceived;
        r.Accept(item);
        if (r is TruckHopper h)
        {
            payCents = GameSession.ToCents(h.LastPay);
            business = Businesses.Get(h.LastPayBusiness).Name;
            feedback = h.LastCollectFeedback;
        }
        return null;
    }

    /// <summary>Host: a refused delivery bounces out of the receiver; the host simulates the bounce.</summary>
    string Bounce(NetCarry nc, ulong client, Vector3 pos, Quaternion rot, ICarryReceiver r, string why)
    {
        Vector3 back = Vector3.up * 3f;
        if (r != null)
        {
            Vector3 d = pos - r.TossTargetPosition; d.y = 0f;
            back += (d.sqrMagnitude > 0.01f ? d.normalized : Vector3.back) * 3f;
        }
        var s = nc.St;
        s.phase = (byte)HandsPhase.Flight;
        s.holder = NetworkManager.ServerClientId;
        s.flags = 0;
        s.pos = pos + Vector3.up * 0.3f;
        s.rot = rot;
        s.vel = back;
        nc.Item.NetSetState(CarryState.Thrown);
        nc.Item.NetSetKinematic(true);
        HostSet(nc, s);
        if (r is ICarryReceiverFeedback fb) fb.OnRejected(nc.Item, why ?? "", false);
        return why ?? "Nope.";
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void DeliverResultRpc(uint id, bool ok, long payCents, long bonusCents, bool curbCleared,
                          FixedString64Bytes address, FixedString64Bytes text, RpcParams rp = default)
    {
        var nc = Get(id);
        if (!ok)
        {
            if (nc != null) nc.Unhide();
            GameHUD.Toast(text.ToString(), ToastStyle.Bad);
            return;
        }
        string business = text.ToString();
        var pb = PlayerBody.For(LocalId);
        Vector3 at = nc != null && nc.Item != null ? nc.Item.transform.position : pb != null ? pb.transform.position : Vector3.zero;
        if (payCents > 0)
        {
            GameHUD.FloatingText(at + Vector3.up * 1.15f, $"+{Economy.Money(payCents / 100f)}" + (string.IsNullOrEmpty(business) ? "" : $" to {business}"), CurbTrash.FreshColor, 1.1f);
            Sfx.Play("cash", at, 0.8f, 1.1f);
        }
        if (curbCleared)
        {
            string where = address.ToString();
            GameHUD.Toast(string.IsNullOrEmpty(where) ? $"Curb clean! +{Economy.Money(bonusCents / 100f)}"
                          : $"Curb clean! {where} +{Economy.Money(bonusCents / 100f)}", ToastStyle.Good);
        }
        string key = nc != null ? nc.Desc.key.ToString() : id.ToString();
        try { DeliveryPaid?.Invoke(key, payCents, business); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Carryable.NetConsumed: on the host, a networked item was consumed (received, burst): tell everyone.</summary>
    void OnNetConsumed(Carryable c)
    {
        if (applying > 0) return;
        var nc = Get(c.NetId);
        if (nc == null) return;
        if (!IsServer)
        {
            GuestConsumed(c, nc);
            return;
        }
        if (nc.Announced) return;
        nc.Announced = true;
        CancelTugFor(nc.Id, "gone");
        byte flags = nc.GoneFlags;
        if (c is TrashBag tb)
        {
            if (tb.HasBurst) flags |= CarryNetState.FlagBurst;
            else if (tb.IsCurbBag) CurbTrash.RemoveRecord(tb.Contents); // a curb bag that ended anywhere but a truck: off the books
        }
        if (flags == 0) flags = CarryNetState.FlagReceived;
        var s = Snapshot(nc);
        s.phase = (byte)HandsPhase.Gone;
        s.flags = flags;
        s.seq = nc.St.seq + 1;
        nc.St = s;
        nc.HostSt = s;
        Forget(nc);
        ItemRpc(nc.Desc, s, Bump(), "", RpcTarget.NotServer);
        Census("gone", nc, s);
    }

    // ==================================================================================== bursts

    /// <summary>TrashBag.NetBurst: a networked bag this machine flies hit the world hard. Guests ask the host.</summary>
    bool OnNetBurst(TrashBag bag, Vector3 point, Vector3 normal, Vector3 relVel)
    {
        if (IsServer) return false; // the host bursts for real (its items get published)
        var nc = Get(bag.NetId);
        if (nc == null) return false;
        if (nc.PendingGone) return true;
        Vector3 pos = bag.WorldCenter;
        Sfx.Play("bag_burst", pos, 1f, 1f);
        Sfx.Play("splat", pos, 0.8f, 1f);
        Fx.Burst(pos, new Color(0.06f, 0.06f, 0.07f), 30, 6f);
        Fx.Dust(point, 1.3f);
        Juice.Shake(0.25f, 0.2f);
        GameHUD.FloatingText(pos + Vector3.up * 0.6f, "SPLORCH!", new Color(0.6f, 0.9f, 0.2f), 1.3f);
        Vector3 bp = bag.transform.position;
        Quaternion br = bag.transform.rotation;
        nc.HideForHost();
        BurstRpc(bag.NetId, bp, br, point, normal, relVel);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BurstRpc(uint id, Vector3 pos, Quaternion rot, Vector3 point, Vector3 normal, Vector3 relVel, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.burst", sender, 96)) return;
        var nc = Get(id);
        if (nc == null || !(nc.Item is TrashBag bag) || nc.St.Phase != HandsPhase.Flight || nc.St.holder != sender) return;
        bag.transform.SetPositionAndRotation(pos, rot);
        nc.GoneFlags |= CarryNetState.FlagBurst;
        NetAudit.Event("burst", new Dictionary<string, object> { { "id", id }, { "client", sender }, { "key", nc.Desc.key.ToString() } });
        bag.NetBurstAt(point, normal, relVel);
    }

    // ==================================================================================== pass, catch, bonk

    /// <summary>TrashCarrier.NetCandidates: "Pass to Dot" when a teammate is in the 15° cone within 8 m (§4.5).</summary>
    void PassCandidates(TrashCarrier carrier, List<InteractionCandidate> results)
    {
        if (carrier == null || carrier != TrashCarrier.Local || carrier.Held == null || carrier.Held.NetId == 0) return;
        var nc = Get(carrier.Held.NetId);
        if (nc == null || nc.PendingGrab) return;
        var target = PassTarget(carrier.transform, out _);
        if (target == null) return;
        passAction.Set(this, nc, target);
        results.Add(new InteractionCandidate { Target = passAction, Score = 3.5f });
    }

    readonly PassAction passAction = new PassAction();

    /// <summary>The teammate the local player is aiming a pass at (null if none).</summary>
    public PlayerBody PassTarget(Transform from, out float angle)
    {
        angle = 999f;
        PlayerBody best = null;
        Vector3 fwd = from.forward; fwd.y = 0f;
        foreach (var pb in PlayerBody.All)
        {
            if (pb == null || pb.ClientId == LocalId || !pb.CanCatch || IsHolding(pb.ClientId)) continue;
            Vector3 d = pb.transform.position - from.position; d.y = 0f;
            float dist = d.magnitude;
            if (dist > HandsTuning.PassRange || dist < 0.5f) continue;
            float a = Vector3.Angle(fwd, d);
            if (a > HandsTuning.PassCone || a >= angle) continue;
            angle = a;
            best = pb;
        }
        return best;
    }

    sealed class PassAction : IInteractable
    {
        HandsNet net;
        NetCarry nc;
        PlayerBody target;
        public void Set(HandsNet n, NetCarry c, PlayerBody t) { net = n; nc = c; target = t; }
        public bool CanInteract() => net != null && nc != null && target != null && nc.Item != null;
        public string GetInteractionPrompt() => target != null ? $"Pass to {target.DisplayName}" : "";
        public void OnInteract(GameObject interactor) { if (CanInteract()) net.LocalPass(nc, target.ClientId); }
        public void Highlight(bool highlight) { }
    }

    /// <summary>E on a player holding something takes the other side. The host confirms a shared grip or refuses it.</summary>
    public void LocalSnatch(NetCarry nc)
    {
        if (nc == null || nc.Item == null) return;
        Vector3 at = nc.Item.transform.position;
        if (IsServer) HostGrab(LocalId, nc.Id, null, StampNow, at);
        else GrabRpc(nc.Id, default, StampNow, at, LocalPlayerPos());
    }

    /// <summary>Compatibility entry for the old heavy-lift request: use the same grab and permission checks.</summary>
    internal void HostJoinGrip(ulong client, uint item)
    {
        var nc = Get(item);
        if (IsServer && nc != null && nc.Item != null)
            HostGrab(client, item, null, ServerNow, nc.Item.transform.position);
    }

    public void LocalPass(NetCarry nc, ulong target)
    {
        if (nc == null || nc.Item == null || nc.PendingGrab) return;
        Sfx.Play("throw", nc.Item.WorldCenter, 0.6f, 1.2f);
        applying++;
        try { nc.PredictPass(target); }
        finally { applying--; }
        if (IsServer) HostPass(LocalId, nc.Id, target);
        else PassRpc(nc.Id, target);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PassRpc(uint id, ulong target, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.pass", sender, 32)) return;
        HostPass(sender, id, target);
    }

    void HostPass(ulong client, uint id, ulong target)
    {
        var nc = Get(id);
        if (nc == null) return;
        bool ok = nc.St.Phase == HandsPhase.Held && nc.St.holder == client && client != target;
        var tb = BodyOf(target);
        ok &= tb.State == BodyState.Normal || tb.State == BodyState.Riding;
        ok &= tb.carrying == None && !HostHolding(target, nc);
        if (ok && HostPlayerPos(client, out var a) && HostPlayerPos(target, out var b)) ok = (a - b).sqrMagnitude <= Sq(HandsTuning.PassRange + 2.5f);
        NetAudit.Event("pass", new Dictionary<string, object> { { "id", id }, { "from", client }, { "to", target }, { "ok", ok } });
        if (!ok)
        {
            // Back to the thrower (and its holder's hands everywhere).
            var s = NewState(nc, nc.St.Phase, nc.St.holder, 0, nc.St.other);
            HostSet(nc, s);
            return;
        }
        if (nc.WindowOpen) { nc.WindowOpen = false; nc.Contenders.Clear(); }
        HostSet(nc, NewState(nc, HandsPhase.Held, target, CarryNetState.FlagPass, client));
    }

    bool HostHolding(ulong client, NetCarry except)
    {
        var shared = TugOf(client);
        if (shared.HasValue && (except == null || shared.Value.item != except.Id)) return true;
        foreach (var x in items.Values) if (x != null && x != except && x.HeldBy(client)) return true;
        return false;
    }

    internal void ClaimCatch(NetCarry nc, ulong catcher)
    {
        if (IsServer) HostCatch(LocalId, nc.Id, catcher);
        else CatchRpc(nc.Id, catcher);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CatchRpc(uint id, ulong catcher, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.catch", sender, 32)) return;
        HostCatch(sender, id, catcher);
    }

    void HostCatch(ulong thrower, uint id, ulong catcher)
    {
        var nc = Get(id);
        if (nc == null) return;
        bool ok = nc.St.Phase == HandsPhase.Flight && nc.St.holder == thrower;
        var cb = BodyOf(catcher);
        ok &= cb.State == BodyState.Normal || cb.State == BodyState.Riding;
        ok &= cb.carrying == None && !HostHolding(catcher, nc);
        if (ok && HostPlayerPos(catcher, out var cp)) ok = (cp + Vector3.up * 1.1f - nc.Item.WorldCenter).sqrMagnitude <= Sq(1.6f + HandsTuning.LagSlack);
        NetAudit.Event("catch", new Dictionary<string, object> { { "id", id }, { "thrower", thrower }, { "catcher", catcher }, { "ok", ok } });
        if (!ok) return;
        HostSet(nc, NewState(nc, HandsPhase.Held, catcher, CarryNetState.FlagCaught, thrower));
    }

    readonly Dictionary<(ulong, ulong), float> lastBonk = new Dictionary<(ulong, ulong), float>();

    internal void ClaimBonk(NetCarry nc, ulong victim, float speed, Vector3 dir)
    {
        if (IsServer) HostBonk(LocalId, nc.Id, victim, speed, dir);
        else BonkRpc(nc.Id, victim, speed, dir);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BonkRpc(uint id, ulong victim, float speed, Vector3 dir, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.bonk", sender, 48)) return;
        HostBonk(sender, id, victim, speed, dir);
    }

    void HostBonk(ulong thrower, uint id, ulong victim, float speed, Vector3 dir)
    {
        var nc = Get(id);
        string why = null;
        if (nc == null || nc.St.Phase != HandsPhase.Flight || nc.St.holder != thrower) why = "not-thrown";
        else if (speed > HandsTuning.MaxThrowSpeed + 1f) why = "too-fast";
        else if (lastBonk.TryGetValue((thrower, victim), out var t) && Time.unscaledTime - t < HandsTuning.SameBonkCooldown) why = "cooldown";
        else if (HostPlayerPos(victim, out var vp) && (vp + Vector3.up * 1.1f - nc.Item.WorldCenter).sqrMagnitude > Sq(1.5f + HandsTuning.LagSlack + 1f)) why = "far";
        if (why == null && !HostKnock(victim, thrower, dir, speed >= HandsTuning.KnockoutSpeed ? HitKind.KnockOut : HitKind.Stumble, true, "BONK!")) why = "immune";
        if (why == null) lastBonk[(thrower, victim)] = Time.unscaledTime;
        NetAudit.Event("bonk", new Dictionary<string, object> { { "id", id }, { "thrower", thrower }, { "victim", victim }, { "speed", Math.Round(speed, 2) }, { "ok", why == null }, { "why", why } });
    }

    // ==================================================================================== helpers

    static float Sq(float x) => x * x;

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

    /// <summary>Name to show for a client (their business's owner name).</summary>
    public static string NameOf(ulong client)
    {
        if (Businesses.TryGetForClient(client, out var b))
        {
            string n = b.ownerName.ToString();
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return "Player " + (client + 1);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        TugsEnabled = true;
        DeliveryPaid = null;
        StateApplied = null;
        TugEnded = null;
        HostKnocked = null;
    }
}

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>A puddle on the wire (host-written).</summary>
public struct PuddleNet : INetworkSerializable, IEquatable<PuddleNet>
{
    public uint id;
    public Vector3 pos;
    public float radius, life;
    public double born;
    public byte kind;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref pos);
        s.SerializeValue(ref radius);
        s.SerializeValue(ref life);
        s.SerializeValue(ref born);
        s.SerializeValue(ref kind);
    }

    public bool Equals(PuddleNet o) => id == o.id && pos == o.pos && radius.Equals(o.radius) && life.Equals(o.life) && born.Equals(o.born) && kind == o.kind;
    public override bool Equals(object obj) => obj is PuddleNet o && Equals(o);
    public override int GetHashCode() => (int)id;
}

/// <summary>Compatibility view of a heavy bag's shared grip. Both participants steer independently.</summary>
public struct LiftNet : INetworkSerializable, IEquatable<LiftNet>
{
    public const byte Lifting = 1, OutOfStep = 2;
    public uint item;
    public ulong holder, helper;
    public byte state;
    public double t;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref item);
        s.SerializeValue(ref holder);
        s.SerializeValue(ref helper);
        s.SerializeValue(ref state);
        s.SerializeValue(ref t);
    }

    public bool Equals(LiftNet o) => item == o.item && holder == o.holder && helper == o.helper && state == o.state && t.Equals(o.t);
    public override bool Equals(object obj) => obj is LiftNet o && Equals(o);
    public override int GetHashCode() => (int)item;
}

/// <summary>A player squished into a cube by a compactor (host-written).</summary>
public struct CubeNet : INetworkSerializable, IEquatable<CubeNet>
{
    public ulong client;
    public double until;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref client);
        s.SerializeValue(ref until);
    }

    public bool Equals(CubeNet o) => client == o.client && until.Equals(o.until);
    public override bool Equals(object obj) => obj is CubeNet o && Equals(o);
    public override int GetHashCode() => client.GetHashCode();
}

/// <summary>
/// F3 "Bag personalities and tools" on the wire (Docs/Multiplayer.md §14.3), on <c>NetSession.prefab</c>. Host-written lists:
/// <b>puddles</b> (leaky trails and cannon splats; guests ask, the host adds) and <b>cubes</b> (compactor-squished players).
/// Heavy lifts are a compatibility view of HandsNet's shared grips, not a separate attached-helper mechanic.
/// Tells themselves never travel: they're a hash of the bag id (<see cref="BagTells"/>). Plus a small relay for cosmetic moments
/// (pole swings and pokes, cannon shots and misfires, slips) so everyone sees them.
/// </summary>
[NetSessionComponent(Order = 40)]
public class BagTellsNet : NetworkBehaviour
{
    public static BagTellsNet Instance { get; private set; }

    public const byte FxPole = 1, FxPoke = 2, FxCannon = 3, FxMisfire = 4, FxSlip = 5, FxOutOfStep = 6, FxCube = 7,
        FxNeighbourSplat = 8;
    const string MsgPuddle = "rb.tells.puddle", MsgLift = "rb.tells.lift", MsgFx = "rb.tells.fx";

    NetworkList<PuddleNet> puddles;
    NetworkList<LiftNet> lifts;
    NetworkList<CubeNet> cubes;
    readonly List<PuddleData> scratch = new List<PuddleData>();
    readonly Dictionary<ulong, double> lastNeighbourSplat = new Dictionary<ulong, double>();
    float nextHostTick;
    bool hooked;

    /// <summary>Every machine: a lift was added/changed/removed.</summary>
    public static event Action LiftsChanged;
    /// <summary>A relayed moment arrived (or was played locally): kind, who did it, other, a, b.</summary>
    public static event Action<byte, ulong, ulong, Vector3, Vector3> FxPlayed;

    public bool Live => IsSpawned && NetworkManager != null && NetworkManager.IsListening;
    public ulong LocalId => NetworkManager != null ? NetworkManager.LocalClientId : 0;
    public double ServerNow => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
    public IReadOnlyList<LiftNet> Lifts { get { RebuildLiftView(); return liftView; } }
    readonly List<LiftNet> liftView = new List<LiftNet>();

    void Awake()
    {
        puddles = new NetworkList<PuddleNet>();
        lifts = new NetworkList<LiftNet>();
        cubes = new NetworkList<CubeNet>();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        hooked = true;
        Puddles.Clear();
        Puddles.Clock = () => ServerNow;
        if (IsServer)
        {
            SessionService.SetGuard(MsgPuddle, new NetGuard(6f, 12, 64));
            SessionService.SetGuard(MsgLift, new NetGuard(6f, 10, 32));
            SessionService.SetGuard(MsgFx, new NetGuard(12f, 24, 64));
            Puddles.Mirror = false;
            Puddles.Added = OnHostPuddleAdded;
            Puddles.Removed = OnHostPuddleRemoved;
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
        else
        {
            Puddles.Mirror = true;
            Puddles.NetRequest = RequestPuddle;
            puddles.OnListChanged += OnPuddlesChanged;
            OnPuddlesChanged(default);
        }
        lifts.OnListChanged += OnLiftsChanged;
        cubes.OnListChanged += OnCubesChanged;
        RebuildLiftView();
        NetAudit.Event("tells", new Dictionary<string, object> { { "op", "spawn" }, { "server", IsServer } });
    }

    public override void OnNetworkDespawn() => Unhook();

    public override void OnDestroy()
    {
        Unhook();
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hooked) return;
        hooked = false;
        if (Instance == this) Instance = null;
        Puddles.Mirror = false;
        Puddles.NetRequest = null;
        Puddles.Added = null;
        Puddles.Removed = null;
        Puddles.Clock = null;
        Puddles.Clear();
        if (puddles != null) puddles.OnListChanged -= OnPuddlesChanged;
        if (lifts != null) lifts.OnListChanged -= OnLiftsChanged;
        if (cubes != null) cubes.OnListChanged -= OnCubesChanged;
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        liftView.Clear();
        lastNeighbourSplat.Clear();
    }

    // ==================================================================================== puddles

    void OnHostPuddleAdded(PuddleData d)
    {
        if (!IsServer || !IsSpawned) return;
        puddles.Add(new PuddleNet { id = d.id, pos = d.pos, radius = d.radius, life = d.life, born = d.born, kind = (byte)d.kind });
    }

    void OnHostPuddleRemoved(uint id)
    {
        if (!IsServer || !IsSpawned) return;
        for (int i = 0; i < puddles.Count; i++) if (puddles[i].id == id) { puddles.RemoveAt(i); return; }
    }

    void OnPuddlesChanged(NetworkListEvent<PuddleNet> e)
    {
        scratch.Clear();
        for (int i = 0; i < puddles.Count; i++)
        {
            var p = puddles[i];
            scratch.Add(new PuddleData { id = p.id, pos = p.pos, radius = p.radius, life = p.life, born = p.born, kind = (PuddleKind)p.kind });
        }
        Puddles.SetMirror(scratch);
    }

    bool RequestPuddle(Vector3 pos, float radius, PuddleKind kind, float life)
    {
        if (!IsSpawned) return false;
        PuddleRpc(pos, radius, (byte)kind, life);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PuddleRpc(Vector3 pos, float radius, byte kind, float life, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgPuddle).Allow(sender, 24, Time.realtimeSinceStartupAsDouble)) return;
        if (!HostPlayerPos(sender, out var at) || (at - pos).sqrMagnitude > 30f * 30f) return; // near the asker only
        Puddles.AddLocal(pos, Mathf.Clamp(radius, 0.2f, 1.6f), (PuddleKind)Mathf.Min(kind, (byte)2), Mathf.Clamp(life, 5f, 90f));
    }

    // ==================================================================================== lifts

    void OnLiftsChanged(NetworkListEvent<LiftNet> e)
    {
        RebuildLiftView();
        try { LiftsChanged?.Invoke(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    void RebuildLiftView()
    {
        liftView.Clear();
        var hands = HandsNet.Instance;
        if (hands == null) return;
        foreach (var t in hands.TugsView)
        {
            var item = hands.Get(t.item)?.Item;
            if (t.result == 0 && item != null && BagTells.Of(item) == BagTell.Heavy)
                liftView.Add(new LiftNet { item = t.item, holder = t.holder, helper = t.challenger,
                    state = LiftNet.Lifting, t = t.beat0 });
        }
    }

    /// <summary>The lift on this networked bag, if any.</summary>
    public bool TryLift(uint item, out LiftNet lift)
    {
        RebuildLiftView();
        for (int i = 0; i < liftView.Count; i++) if (liftView[i].item == item) { lift = liftView[i]; return true; }
        lift = default;
        return false;
    }

    /// <summary>The lift this client is helping with, if any.</summary>
    public bool TryHelping(ulong client, out LiftNet lift)
    {
        RebuildLiftView();
        for (int i = 0; i < liftView.Count; i++) if (liftView[i].helper == client && liftView[i].state == LiftNet.Lifting) { lift = liftView[i]; return true; }
        lift = default;
        return false;
    }

    /// <summary>Ask to take the other end of the heavy bag <paramref name="item"/> someone is holding.</summary>
    public void RequestHelp(uint item)
    {
        if (!IsSpawned) return;
        if (IsServer) HostHelp(LocalId, item);
        else HelpRpc(item);
    }

    /// <summary>Let go of this grip. The legacy outOfStep argument is ignored; the other person keeps their grip.</summary>
    public void RequestLetGo(uint item, bool outOfStep)
    {
        if (!IsSpawned) return;
        if (IsServer) HostLetGo(LocalId, item, outOfStep);
        else LetGoRpc(item, outOfStep);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void HelpRpc(uint item, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgLift).Allow(sender, 8, Time.realtimeSinceStartupAsDouble)) return;
        HostHelp(sender, item);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LetGoRpc(uint item, bool outOfStep, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgLift).Allow(sender, 8, Time.realtimeSinceStartupAsDouble)) return;
        HostLetGo(sender, item, outOfStep);
    }

    void HostHelp(ulong client, uint item) => HandsNet.Instance?.HostJoinGrip(client, item);

    void HostLetGo(ulong client, uint item, bool outOfStep) => HandsNet.Instance?.HostLetGoGrip(client, item);

    // ==================================================================================== cubes

    void OnCubesChanged(NetworkListEvent<CubeNet> e) => CompactorCube.RaiseChanged();

    /// <summary>Server time this client stays a cube until (0 = not a cube).</summary>
    public double CubedUntil(ulong client)
    {
        if (cubes == null) return 0;
        for (int i = 0; i < cubes.Count; i++) if (cubes[i].client == client) return cubes[i].until;
        return 0;
    }

    /// <summary>Already-authorized host machine cycle. The operator may be a guest in a different depot from the host.</summary>
    internal void CubeFromMachine(ulong target, Vector3 compactorPos, Vector3 popOut)
    {
        if (!IsServer || !IsSpawned || CubedUntil(target) > ServerNow) return;
        if (!HostPlayerPos(target, out var at) || (at - compactorPos).sqrMagnitude > 5f * 5f) return;
        HandsNet.Instance?.HostPrepareForCompactor(target, 1f);
        RecordCube(target);
        CubePopOutRpc(target, popOut);
    }

    internal void BeginCompactorPull(uint token, ulong target, Vector3 chamber, Vector3 landing, float seconds)
    {
        if (!IsServer || !IsSpawned) return;
        HandsNet.Instance?.HostPrepareForCompactor(target, seconds + 1f);
        BeginCompactorPullRpc(token, target, chamber, landing, ServerNow + seconds);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void BeginCompactorPullRpc(uint token, ulong target, Vector3 chamber, Vector3 landing, double arriveAt)
    {
        CompactorPlayerPull.Begin(PlayerTells.For(target), token, chamber, landing, arriveAt);
    }

    internal void FinishCompactorPull(uint token, ulong target, Vector3 landing)
    {
        if (!IsServer || !IsSpawned || PlayerTells.For(target) == null || CubedUntil(target) > ServerNow) return;
        HandsNet.Instance?.HostPrepareForCompactor(target, 1f);
        RecordCube(target);
        FinishCompactorPullRpc(token, target, landing);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void FinishCompactorPullRpc(uint token, ulong target, Vector3 landing)
    {
        CompactorPlayerPull.Finish(PlayerTells.For(target), token, landing, true);
    }

    internal void CancelCompactorPull(uint token, ulong target)
    {
        if (!IsServer || !IsSpawned) return;
        CancelCompactorPullRpc(token, target);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void CancelCompactorPullRpc(uint token, ulong target) => CompactorPlayerPull.Cancel(PlayerTells.For(target), token);

    void RecordCube(ulong target)
    {
        for (int i = cubes.Count - 1; i >= 0; i--) if (cubes[i].client == target) cubes.RemoveAt(i);
        cubes.Add(new CubeNet { client = target, until = ServerNow + CompactorCube.Seconds });
    }

    [Rpc(SendTo.ClientsAndHost)]
    void CubePopOutRpc(ulong target, Vector3 at)
    {
        var player = PlayerTells.For(target);
        if (player == null) return;
        // Fling poses are also simulated on observer copies. Cancel all of them so an old fling cannot
        // pull the visible cube back into its flight after its owner has safely popped onto the floor.
        player.GetComponent<PlayerBody>()?.ResetForCompactor();
        player.GetComponent<RideRider>()?.ResetForCompactor();
        if (player.IsLocal) player.PopOutAsCube(at);
    }

    // ==================================================================================== cosmetic relay

    /// <summary>Play a moment here and (in co-op) on every other machine. Works without a session too (local only).</summary>
    public static void Send(byte kind, ulong other, Vector3 a, Vector3 b)
    {
        var n = Instance;
        if (n != null && n.IsSpawned) { n.Fx(kind, other, a, b); return; }
        Play(kind, 0, other, a, b);
    }

    /// <summary>Play a moment here and on every other machine (pole swing, poke, cannon shot, misfire, slip).</summary>
    public void Fx(byte kind, ulong other, Vector3 a, Vector3 b)
    {
        if (kind == FxNeighbourSplat) return; // only the host can choose who a misfire splattered
        Play(kind, LocalId, other, a, b);
        if (!IsSpawned) return;
        if (IsServer)
        {
            FxOutRpc(kind, LocalId, other, a, b, RpcTarget.NotServer);
            if (kind == FxMisfire) HostNeighbourSplat(LocalId, b);
        }
        else FxInRpc(kind, other, a, b);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void FxInRpc(byte kind, ulong other, Vector3 a, Vector3 b, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgFx).Allow(sender, 40, Time.realtimeSinceStartupAsDouble)) return;
        if (kind == FxNeighbourSplat) return;
        if (kind == FxPoke && !PokeOk(sender, other)) return;
        if (kind == FxMisfire && (!HostPlayerPos(sender, out var shooterAt) || (shooterAt - b).sqrMagnitude > 3f * 3f)) return;
        Play(kind, sender, other, a, b);
        foreach (var id in NetworkManager.ConnectedClientsIds)
            if (id != sender && id != NetworkManager.ServerClientId) FxOutRpc(kind, sender, other, a, b, RpcTarget.Single(id, RpcTargetUse.Temp));
        if (kind == FxMisfire) HostNeighbourSplat(sender, b);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void FxOutRpc(byte kind, ulong who, ulong other, Vector3 a, Vector3 b, RpcParams p = default) => Play(kind, who, other, a, b);

    /// <summary>Only the host selects a bystander, so every machine paints the same player (not the shooter's guess).</summary>
    void HostNeighbourSplat(ulong shooter, Vector3 feet)
    {
        if (!IsServer || !HostPlayerPos(shooter, out var shooterAt) || (shooterAt - feet).sqrMagnitude > 3f * 3f) return;
        double now = ServerNow;
        if (lastNeighbourSplat.TryGetValue(shooter, out var last) && now - last < BagCannon.Cooldown) return;
        PlayerTells nearest = null;
        float best = 2.8f * 2.8f;
        foreach (var p in PlayerTells.All)
        {
            if (p == null || p.ClientId == shooter || p.Player == null || p.Player.IsDriving) continue;
            Vector3 delta = p.transform.position - feet;
            if (Mathf.Abs(delta.y) > 2f) continue;
            delta.y = 0f;
            float d2 = delta.sqrMagnitude;
            if (d2 >= best) continue;
            best = d2;
            nearest = p;
        }
        if (nearest == null) return;
        lastNeighbourSplat[shooter] = now;
        ulong target = nearest.ClientId;
        Vector3 at = nearest.transform.position + Vector3.up * 1.15f;
        Puddles.Spawn(nearest.transform.position, 0.6f, PuddleKind.Splat);
        NetAudit.Event("tells", new Dictionary<string, object> { { "op", "cannon-neighbour-splat" }, { "shooter", (long)shooter }, { "target", (long)target } });
        Play(FxNeighbourSplat, shooter, target, feet, at);
        FxOutRpc(FxNeighbourSplat, shooter, target, feet, at, RpcTarget.NotServer);
    }

    bool PokeOk(ulong poker, ulong target)
    {
        if (!BusinessUpgradeNet.HasGrabberPoleForClient(poker) || CompactorCube.IsCubed(poker) || CompactorCube.IsSuctioning(poker)) return false;
        if (HandsNet.Instance != null && HandsNet.Instance.BodyOf(poker).State != BodyState.Normal) return false;
        if (!HostPlayerPos(poker, out var a) || !HostPlayerPos(target, out var b)) return false;
        return PoleTargeting.WithinReach(a + Vector3.up * 1.1f, b + Vector3.up * 1.1f, GrabberPole.PokeRange + 0.6f) &&
            PoleTargeting.ClearPath(a + Vector3.up * 1.1f, b + Vector3.up * 1.1f,
                NightPlayers.ForClient(poker)?.transform, NightPlayers.ForClient(target)?.transform);
    }

    static void Play(byte kind, ulong who, ulong other, Vector3 a, Vector3 b)
    {
        try { FxPlayed?.Invoke(kind, who, other, a, b); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ==================================================================================== host upkeep

    void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (Time.unscaledTime < nextHostTick) return;
        nextHostTick = Time.unscaledTime + 0.1f;
        double now = ServerNow;
        for (int i = cubes.Count - 1; i >= 0; i--) if (cubes[i].until < now - 0.5) cubes.RemoveAt(i);
    }

    void OnClientDisconnected(ulong client)
    {
        if (!IsServer) return;
        lastNeighbourSplat.Remove(client);
        for (int i = lifts.Count - 1; i >= 0; i--) if (lifts[i].helper == client || lifts[i].holder == client) lifts.RemoveAt(i);
        for (int i = cubes.Count - 1; i >= 0; i--) if (cubes[i].client == client) cubes.RemoveAt(i);
    }

    bool HostPlayerPos(ulong client, out Vector3 pos)
    {
        pos = default;
        var pt = PlayerTells.For(client);
        if (pt == null) return false;
        pos = pt.transform.position;
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        LiftsChanged = null;
        FxPlayed = null;
    }
}

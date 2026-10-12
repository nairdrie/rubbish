using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>One lot's Hoverhog on the wire (index = lot).</summary>
public struct HogLotState : INetworkSerializable, IEquatable<HogLotState>
{
    public const byte FlagPlaced = 1, FlagDumping = 2, FlagBeaming = 4, FlagBoosting = 8;

    public ulong pilot;
    public byte battery, flags;
    /// <summary>The owning business's upgrade levels (<see cref="HogLevels.Pack"/>).</summary>
    public ushort levels;
    public float loadKg, yaw;
    public Vector3 pos;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref pilot);
        s.SerializeValue(ref battery);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref levels);
        s.SerializeValue(ref loadKg);
        s.SerializeValue(ref yaw);
        s.SerializeValue(ref pos);
    }

    public bool Equals(HogLotState o) =>
        pilot == o.pilot && battery == o.battery && flags == o.flags && levels == o.levels && Mathf.Abs(loadKg - o.loadKg) < 0.05f &&
        Mathf.Abs(yaw - o.yaw) < 0.5f && (pos - o.pos).sqrMagnitude < 0.0025f;

    public override bool Equals(object obj) => obj is HogLotState o && Equals(o);
    public override int GetHashCode() => pilot.GetHashCode() ^ (battery << 8) ^ (flags << 16);
}

/// <summary>
/// The Hoverhogs in co-op (Docs/Progression.md §6.10). The host owns each lot's seat, battery, load and parked pose
/// (<see cref="Hogs"/>) and replicates them; the pilot's machine flies it and reports twice a second. While somebody is
/// seated, everyone else draws the Hoverhog at that pilot's player, whose owner-authoritative transform already
/// replicates, so there is no second pose stream. Claw and bomb bay are requests the host decides.
/// </summary>
[NetSessionComponent(Order = 27)]
public class HogNet : NetworkBehaviour
{
    public static HogNet Instance { get; private set; }

    NetworkList<HogLotState> lots;
    float nextSync;
    /// <summary>A guest's own claim is trusted locally until the host has had time to answer.</summary>
    readonly float[] claimGrace = new float[DepotRowLayout.LotCount];

    void Awake() => lots = new NetworkList<HogLotState>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer)
        {
            lots.Clear();
            for (int i = 0; i < DepotRowLayout.LotCount; i++) lots.Add(Pack(Hogs.Of(i)));
        }
        else Hogs.ResetAll();
    }

    public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }
    public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

    static HogLotState Pack(HogData d) => new HogLotState
    {
        pilot = d.pilot,
        battery = HogRules.PackBattery(d.battery),
        flags = (byte)((d.placed ? HogLotState.FlagPlaced : 0) | (d.dumping ? HogLotState.FlagDumping : 0) |
                       (d.beaming ? HogLotState.FlagBeaming : 0) | (d.boosting ? HogLotState.FlagBoosting : 0)),
        levels = d.levels,
        loadKg = d.loadKg, yaw = d.yaw, pos = d.pos,
    };

    void Update()
    {
        if (!IsSpawned || Time.unscaledTime < nextSync) return;
        nextSync = Time.unscaledTime + 0.1f;
        if (IsServer)
        {
            for (int i = 0; i < lots.Count && i < DepotRowLayout.LotCount; i++)
            {
                var want = Pack(Hogs.Of(i));
                if (!want.Equals(lots[i])) lots[i] = want;
            }
            return;
        }
        DeclareOwnLevels();
        for (int i = 0; i < lots.Count && i < DepotRowLayout.LotCount; i++)
        {
            var s = lots[i];
            var d = Hogs.Of(i);
            bool flying = Hoverhog.LocalSeat != null && Hoverhog.LocalSeat.Lot == i;
            bool grace = flying && Time.unscaledTime < claimGrace[i];
            if (!grace) d.pilot = s.pilot;
            d.loadKg = s.loadKg;
            d.levels = s.levels;
            d.dumping = (s.flags & HogLotState.FlagDumping) != 0;
            d.beaming = (s.flags & HogLotState.FlagBeaming) != 0;
            if (!(flying && d.pilot == Hogs.LocalClient)) d.boosting = (s.flags & HogLotState.FlagBoosting) != 0;
            // The seated pilot's own machine is ahead of the host on pose and battery.
            if (flying && d.pilot == Hogs.LocalClient) continue;
            d.placed = (s.flags & HogLotState.FlagPlaced) != 0;
            d.battery = HogRules.UnpackBattery(s.battery);
            d.pos = s.pos;
            d.yaw = s.yaw;
        }
    }

    float nextDeclare;

    /// <summary>A guest tells the host its own Hoverhog's upgrade levels until the replicated copy agrees.</summary>
    void DeclareOwnLevels()
    {
        int own = DepotRowWorld.LocalLot;
        if (own < 0 || own >= lots.Count || Hogs.Level(own) <= 0 || Time.unscaledTime < nextDeclare) return;
        ushort packed = HogLevels.Local.Pack();
        if (lots[own].levels == packed) return;
        nextDeclare = Time.unscaledTime + 1f;
        DeclareRpc(own, packed);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeclareRpc(int lot, ushort packed, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostDeclare(lot, sender, packed);
    }

    bool Allowed(ulong sender, int lot) =>
        Hogs.ValidLot(lot) && SessionService.GuardFor("rb.hog").Allow(sender, 24, Time.realtimeSinceStartupAsDouble);

    // ------------------------------------------------------------------ requests

    public void SendClaim(int lot)
    {
        if (!IsSpawned || !Hogs.ValidLot(lot)) return;
        claimGrace[lot] = Time.unscaledTime + 1.5f;
        ClaimRpc(lot);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ClaimRpc(int lot, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostClaim(lot, sender);
    }

    public void SendReport(int lot, Vector3 pos, float yaw, float battery, byte flags, bool park)
    {
        if (IsSpawned) ReportRpc(lot, pos, yaw, battery, flags, park);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportRpc(int lot, Vector3 pos, float yaw, float battery, byte flags, bool park, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostReport(lot, sender, pos, yaw, battery, flags, park);
    }

    public void SendClaw(int lot) { if (IsSpawned) ClawRpc(lot); }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ClawRpc(int lot, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostClaw(lot, sender);
    }

    public void SendDump(int lot, bool single) { if (IsSpawned) DumpRpc(lot, single); }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DumpRpc(int lot, bool single, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostDump(lot, sender, single);
    }

    public void SendBurn(int lot) { if (IsSpawned) BurnRpc(lot); }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BurnRpc(int lot, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed(sender, lot)) Hogs.HostBurn(lot, sender);
    }

    /// <summary>Host: the reactor burned a bag for a guest pilot, whose machine owns the battery while it flies.</summary>
    public void Credit(ulong client, int lot, float credit)
    {
        if (IsServer && IsSpawned) CreditRpc(lot, credit, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void CreditRpc(int lot, float credit, RpcParams rpc = default) => Hogs.ApplyCredit(lot, credit);

    // ------------------------------------------------------------------ host to everyone

    public void BroadcastClaw(int lot, Vector3 target, float seconds)
    {
        if (IsServer && IsSpawned) ClawFxRpc(lot, target, seconds);
    }
    [Rpc(SendTo.Everyone)]
    void ClawFxRpc(int lot, Vector3 target, float seconds)
    {
        var hog = Hoverhog.ForLot(lot);
        if (hog != null) hog.PresentClaw(target, seconds);
    }

    public void Notify(ulong client, string message, ToastStyle style)
    {
        if (IsServer && IsSpawned) NoticeRpc(message, (byte)style, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(string message, byte style, RpcParams rpc = default) => GameHUD.Toast(message, (ToastStyle)style, 2.5f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

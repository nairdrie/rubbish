using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>A trash-can trap (F4): host-written, every machine shows it.</summary>
public struct TrapNet : INetworkSerializable, IEquatable<TrapNet>
{
    public const byte Armed = 0, Caught = 1, CaughtRabid = 2;
    public int id;
    public Vector3 pos;
    public float yaw;
    public byte state;
    public ulong owner;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref pos);
        s.SerializeValue(ref yaw);
        s.SerializeValue(ref state);
        s.SerializeValue(ref owner);
    }

    public bool Equals(TrapNet o) => id == o.id && pos == o.pos && yaw.Equals(o.yaw) && state == o.state && owner == o.owner;
    public override bool Equals(object obj) => obj is TrapNet o && Equals(o);
    public override int GetHashCode() => id;
}

/// <summary>The Raccoon King's health bar (F4), host-written.</summary>
public struct KingNet : INetworkSerializable, IEquatable<KingNet>
{
    public bool active;
    public ushort critter;
    public short hp, maxHp;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref active);
        s.SerializeValue(ref critter);
        s.SerializeValue(ref hp);
        s.SerializeValue(ref maxHp);
    }

    public bool Equals(KingNet o) => active == o.active && critter == o.critter && hp == o.hp && maxHp == o.maxHp;
    public override bool Equals(object obj) => obj is KingNet o && Equals(o);
    public override int GetHashCode() => critter ^ (hp << 16);
}

// Traps, roof spotlight aims and the Raccoon King (F4 §14.4).
public partial class NightNet
{
    NetworkList<TrapNet> traps;
    readonly NetworkVariable<KingNet> king = new NetworkVariable<KingNet>();

    public NetworkList<TrapNet> Traps => traps;
    public KingNet King => king.Value;

    void AwakeExtras() => traps = new NetworkList<TrapNet>();

    void SpawnExtras()
    {
        traps.OnListChanged += OnTrapsChanged;
        NightTraps.SyncFrom(traps);
    }

    void DespawnExtras()
    {
        if (traps != null) traps.OnListChanged -= OnTrapsChanged;
        NightTraps.Clear();
    }

    void ExtrasTick() { }

    void OnTrapsChanged(NetworkListEvent<TrapNet> e) => NightTraps.SyncFrom(traps);

    // ---------------------------------------------------------------- traps

    /// <summary>Host: write the trap list (co-op).</summary>
    public void HostWriteTraps(List<TrapNet> list)
    {
        if (!IsServer) return;
        // Small list (≤8): rewrite only what changed.
        for (int i = traps.Count - 1; i >= 0; i--)
        {
            int id = traps[i].id;
            int j = list.FindIndex(t => t.id == id);
            if (j < 0) traps.RemoveAt(i);
            else if (!traps[i].Equals(list[j])) traps[i] = list[j];
        }
        foreach (var t in list)
        {
            bool have = false;
            for (int i = 0; i < traps.Count; i++) if (traps[i].id == t.id) { have = true; break; }
            if (!have) traps.Add(t);
        }
    }

    public void RequestTrapPlace(Vector3 pos, float yaw)
    {
        if (IsServer) NightTraps.HostPlace(LocalId, pos, yaw);
        else TrapPlaceRpc(pos, yaw);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TrapPlaceRpc(Vector3 pos, float yaw, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgTrap).Allow(sender, 20, Time.realtimeSinceStartupAsDouble)) return;
        var pc = NightPlayers.ForClient(sender);
        if (pc == null || (pc.transform.position - pos).sqrMagnitude > 6f * 6f) return;
        NightTraps.HostPlace(sender, pos, yaw);
    }

    public void RequestTrapAct(int id, byte act)
    {
        if (IsServer) NightTraps.HostAct(LocalId, id, act);
        else TrapActRpc(id, act);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TrapActRpc(int id, byte act, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgTrap).Allow(sender, 8, Time.realtimeSinceStartupAsDouble)) return;
        NightTraps.HostAct(sender, id, act);
    }

    /// <summary>Host: give <paramref name="client"/> an item for their own keep crate (a pet raccoon, a trophy).</summary>
    public static void HostGiveItem(ulong client, string defId, string uid, float value, string note)
    {
        var n = Instance;
        if (!Streaming || client == n.LocalId) { NightTraps.ReceiveItem(defId, uid, value, note); return; }
        var d = new FixedString64Bytes(); d.CopyFromTruncated(defId);
        var u = new FixedString64Bytes(); u.CopyFromTruncated(uid);
        var t = new FixedString64Bytes(); t.CopyFromTruncated(note ?? "");
        n.GiveItemRpc(d, u, value, t, n.RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void GiveItemRpc(FixedString64Bytes defId, FixedString64Bytes uid, float value, FixedString64Bytes note, RpcParams rp = default)
    {
        NightTraps.ReceiveItem(defId.ToString(), uid.ToString(), value, note.ToString());
    }

    /// <summary>Host: a guest's trap went back in their pocket.</summary>
    public static void HostRefundTrap(ulong client)
    {
        var n = Instance;
        if (!Streaming || client == n.LocalId) { GameSession.AddConsumable(RaccoonTrap.ConsumableId, 1); return; }
        n.RefundRpc(n.RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RefundRpc(RpcParams rp = default) => GameSession.AddConsumable(RaccoonTrap.ConsumableId, 1);

    // ---------------------------------------------------------------- roof spotlight

    /// <summary>The rider aiming a roof spotlight sends their aim (yaw/pitch in truck space, bytes).</summary>
    public void SendSpotAim(ulong truckId, byte yaw, byte pitch)
    {
        if (IsServer) SpotOutRpc(truckId, yaw, pitch, LocalId);
        else SpotInRpc(truckId, yaw, pitch);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void SpotInRpc(ulong truckId, byte yaw, byte pitch, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgSpot).Allow(sender, 10, Time.realtimeSinceStartupAsDouble)) return;
        var truck = TruckByNetId(truckId);
        if (truck == null) return;
        var riders = new List<RiderInfo>(4);
        Riding.RidersOn(truck, riders);
        bool onRung = false;
        for (int i = 0; i < riders.Count; i++)
            if (riders[i].clientId == sender && (riders[i].rung || HitchStep.StepLocal.Length <= 2)) { onRung = true; break; }
        if (!onRung) return;
        RoofSpotlight.ApplyAim(truckId, yaw, pitch, sender);
        SpotOutRpc(truckId, yaw, pitch, sender);
    }

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    void SpotOutRpc(ulong truckId, byte yaw, byte pitch, ulong aimer)
    {
        if (aimer == LocalId) return;
        RoofSpotlight.ApplyAim(truckId, yaw, pitch, aimer);
    }

    // ---------------------------------------------------------------- the Raccoon King

    public void HostSetKing(KingNet k)
    {
        if (IsServer && !king.Value.Equals(k)) king.Value = k;
    }
}

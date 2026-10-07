using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>A truck's riding mods and shared Safety Bar position.</summary>
public struct RideModsNet : INetworkSerializable, IEquatable<RideModsNet>
{
    public ulong truck;
    public byte padded, bar;
    public bool barDown;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref truck);
        s.SerializeValue(ref padded);
        s.SerializeValue(ref bar);
        s.SerializeValue(ref barDown);
    }

    public bool Equals(RideModsNet o) => truck == o.truck && padded == o.padded && bar == o.bar && barDown == o.barDown;
    public override bool Equals(object obj) => obj is RideModsNet o && Equals(o);
    public override int GetHashCode() => truck.GetHashCode();
}

/// <summary>One stress sample on the wire (~26 B): driver clock, sideways g, braking, drift, speed, where the truck was.</summary>
public struct RideSampleNet : INetworkSerializable
{
    public double t;
    public short lat, brake;
    public byte drift, speed;
    public Vector3 pos;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref t);
        s.SerializeValue(ref lat);
        s.SerializeValue(ref brake);
        s.SerializeValue(ref drift);
        s.SerializeValue(ref speed);
        s.SerializeValue(ref pos);
    }

    public static RideSampleNet Pack(in RideTruck.Sample s) => new RideSampleNet
    {
        t = s.t,
        lat = (short)Mathf.Clamp(Mathf.RoundToInt(s.lat * 20f), short.MinValue, short.MaxValue),
        brake = (short)Mathf.Clamp(Mathf.RoundToInt(s.brake * 20f), short.MinValue, short.MaxValue),
        drift = (byte)Mathf.Clamp(Mathf.RoundToInt(s.drift * 10f), 0, 255),
        speed = (byte)Mathf.Clamp(Mathf.RoundToInt(s.speed * 8f), 0, 255),
        pos = s.pos,
    };

    public RideTruck.Sample Unpack() => new RideTruck.Sample { t = t, lat = lat / 20f, brake = brake / 20f, drift = drift / 10f, speed = speed / 8f, pos = pos };
}

// Package F1 "Riding the truck" (Docs/Multiplayer.md §14.1, §4.4, red-team T4) on top of N2's hitching:
//  - Stress stream: the machine simulating a truck sends 10 samples/s + jolts while anyone rides it; the host relays them.
//  - Fling: the rider's own machine decides it at the truck tick it shows and tells the host; the host marks the body
//    Thrown and tells everyone the tick, the owner's world start and the velocity, so each machine starts the same flight
//    at the same corner. Landing: knocked out 1 s (soft landings in hedges: not), then 5 s immunity (§4.1 #4).
//  - Rider shove (RMB): attacker detects, host checks both ride the same truck within reach; the victim's machine gets
//    a HOLD ON. Knock-off / grip hits for other packages (Riding.KnockOff): host decides, the rider's machine applies.
//  - Pay (streak bonus, Crew Comfort tip): host computes the amount and credits the truck's business through the Ledger.
//  - Truck mods: each business reports its trucks' Padded Step / Safety Bar levels; the host keeps the list.
public partial class HandsNet
{
    public const byte RidePayStreak = 1, RidePayComfort = 2;

    NetworkList<RideModsNet> rideMods;
    bool rideGuards;
    readonly Dictionary<ulong, float> lastRideShoveAt = new Dictionary<ulong, float>();
    readonly Dictionary<ulong, float> lastRidePayAt = new Dictionary<ulong, float>();
    readonly Dictionary<ulong, float> lastRideBarAt = new Dictionary<ulong, float>();
    readonly Dictionary<ulong, double> lastRideFlingTick = new Dictionary<ulong, double>();

    void AwakeRide() => rideMods = new NetworkList<RideModsNet>();

    void EnsureRideGuards()
    {
        if (rideGuards) return;
        rideGuards = true;
        SessionService.SetGuard("rb.ride.sample", new NetGuard(24f, 48, 64));
        SessionService.SetGuard("rb.ride.jolt", new NetGuard(12f, 24, 64));
        SessionService.SetGuard("rb.ride.fling", new NetGuard(4f, 8, 96));
        SessionService.SetGuard("rb.ride.mods", new NetGuard(2f, 6, 32));
        SessionService.SetGuard("rb.ride.bar", new NetGuard(2f, 6, 16));
        SessionService.SetGuard("rb.ride.lean", new NetGuard(8f, 16, 16));
        SessionService.SetGuard("rb.ride.grip", new NetGuard(8f, 16, 16));
    }

    // ==================================================================================== queries

    /// <summary>Anyone riding the truck <paramref name="truckId"/>.</summary>
    public bool AnyRiderOn(ulong truckId)
    {
        for (int i = 0; i < bodies.Count; i++)
            if (bodies[i].State == BodyState.Riding && bodies[i].other == truckId) return true;
        return false;
    }

    public int RidersOn(ulong truckId)
    {
        int n = 0;
        for (int i = 0; i < bodies.Count; i++)
            if (bodies[i].State == BodyState.Riding && bodies[i].other == truckId) n++;
        return n;
    }

    public bool TryRideMods(ulong truckId, out byte padded, out byte bar, out bool barDown)
    {
        padded = bar = 0;
        barDown = true;
        if (rideMods == null) return false;
        for (int i = 0; i < rideMods.Count; i++)
            if (rideMods[i].truck == truckId)
            {
                padded = rideMods[i].padded;
                bar = rideMods[i].bar;
                barDown = rideMods[i].barDown;
                return true;
            }
        return false;
    }

    bool TruckSimulatedBy(ulong truckId, ulong client)
    {
        return NetworkManager.SpawnManager != null && NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(truckId, out var no) &&
               no != null && no.OwnerClientId == client;
    }

    // ==================================================================================== stress stream

    public void SendRideSample(RideTruck rt, in RideTruck.Sample s)
    {
        if (rt == null || rt.NetId == 0 || !IsSpawned) return;
        var n = RideSampleNet.Pack(s);
        if (IsServer) RideSampleAllRpc(rt.NetId, n);
        else RideSampleRpc(rt.NetId, n);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void RideSampleRpc(ulong truckId, RideSampleNet s, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.sample", sender, 40) || !TruckSimulatedBy(truckId, sender)) return;
        var rt = RideTruck.Find(truckId);
        if (rt != null && !rt.Simulated) rt.ReceiveSample(s.Unpack());
        RideSampleAllRpc(truckId, s);
    }

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    void RideSampleAllRpc(ulong truckId, RideSampleNet s)
    {
        var rt = RideTruck.Find(truckId);
        if (rt != null && !rt.Simulated) rt.ReceiveSample(s.Unpack());
    }

    public void SendRideJolt(RideTruck rt, in RideTruck.Jolt j)
    {
        if (rt == null || rt.NetId == 0 || !IsSpawned) return;
        byte cost = (byte)Mathf.Clamp(Mathf.RoundToInt(j.cost), 0, 255);
        if (IsServer) RideJoltAllRpc(rt.NetId, j.t, cost, j.kind, j.flingAll);
        else RideJoltRpc(rt.NetId, j.t, cost, j.kind, j.flingAll);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideJoltRpc(ulong truckId, double t, byte cost, byte kind, bool all, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.jolt", sender, 24) || !TruckSimulatedBy(truckId, sender)) return;
        var rt = RideTruck.Find(truckId);
        if (rt != null && !rt.Simulated) rt.ReceiveJolt(new RideTruck.Jolt { t = t, cost = cost, kind = kind, flingAll = all });
        RideJoltAllRpc(truckId, t, cost, kind, all);
    }

    [Rpc(SendTo.NotServer)]
    void RideJoltAllRpc(ulong truckId, double t, byte cost, byte kind, bool all)
    {
        var rt = RideTruck.Find(truckId);
        if (rt != null && !rt.Simulated) rt.ReceiveJolt(new RideTruck.Jolt { t = t, cost = cost, kind = kind, flingAll = all });
    }

    // ==================================================================================== fling and landing

    public void LocalRideFling(ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideFling(LocalId, truckId, hold, tF, worldStart, vel, flags, cause);
        else
        {
            // A lightweight unreliable path usually arrives before a retransmitted reliable RPC on a bad link.
            // The reliable copy remains the guarantee; the host de-duplicates by rider and truck tick.
            RideFlingFastRpc(truckId, hold, tF, worldStart, vel, flags, cause);
            RideFlingRpc(truckId, hold, tF, worldStart, vel, flags, cause);
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void RideFlingFastRpc(ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause,
        RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.fling", sender, 80)) return;
        HostRideFling(sender, truckId, hold, tF, worldStart, vel, flags, cause);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideFlingRpc(ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.fling", sender, 80)) return;
        HostRideFling(sender, truckId, hold, tF, worldStart, vel, flags, cause);
    }

    static bool RideFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool RideFinite(Vector3 value) => RideFinite(value.x) && RideFinite(value.y) && RideFinite(value.z);

    void HostRideFling(ulong client, ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        if (lastRideFlingTick.TryGetValue(client, out double previous) && Math.Abs(previous - tF) < 0.0001) return;
        int i = BodyIndex(client);
        string why = null;
        bool anchorAdjusted = false;
        if (i < 0) why = "missing";
        else
        {
            var b = bodies[i];
            if (b.State != BodyState.Riding || b.other != truckId) why = "not-riding";
            else if (hold >= RideHolds.Count || hold != b.step) why = "hold";
            else if (double.IsNaN(tF) || double.IsInfinity(tF) || tF < ServerNow - 4.0 || tF > ServerNow + 0.5) why = "tick";
            else if (!RideFinite(vel)) why = "velocity";
            else
            {
                var rt = RideTruck.Find(truckId);
                if (rt == null) why = "truck";
                else
                {
                    // The rider owns the visible fling point. A different truck interpolation on the host can
                    // be metres away, so allow a modest gap while bounding NPC/prop hits to the real truck.
                    if (!rt.TryPoseAtDriver(tF, out var truckPos, out var truckRot))
                    {
                        truckPos = rt.transform.position;
                        truckRot = rt.transform.rotation;
                    }
                    Vector3 expected = truckPos + truckRot * RideHolds.FeetMetres(hold) + Vector3.up * 0.15f;
                    if (!RideFinite(worldStart) || Vector3.Distance(worldStart, expected) > 3f)
                    {
                        worldStart = expected;
                        anchorAdjusted = true;
                    }
                    b.state = (byte)BodyState.Thrown;
                    b.other = None;
                    b.until = ServerNow + 3.5; // safety: the rider's machine reports the landing long before this
                    bodies[i] = b;
                    CancelTugsOf(client);
                }
            }
        }
        if (vel.magnitude > 30f) vel = vel.normalized * 30f;
        if (why == null)
        {
            lastRideFlingTick[client] = tF;
            RideFlungFastRpc(client, truckId, hold, tF, worldStart, vel, flags, cause);
            RideFlungRpc(client, truckId, hold, tF, worldStart, vel, flags, cause);
        }
        NetAudit.Event("ride", new Dictionary<string, object>
        {
            { "op", "hostfling" }, { "rider", (long)client }, { "truck", (long)truckId }, { "hold", hold },
            { "tF", double.IsNaN(tF) || double.IsInfinity(tF) ? -1.0 : Math.Round(tF, 4) },
            { "ok", why == null }, { "why", why }, { "flags", flags }, { "anchorAdjusted", anchorAdjusted },
        });
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RideFlungRpc(ulong rider, ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        RideRider.OnFlungEvent(rider, truckId, hold, tF, worldStart, vel, flags, cause);
    }

    [Rpc(SendTo.ClientsAndHost, Delivery = RpcDelivery.Unreliable)]
    void RideFlungFastRpc(ulong rider, ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        RideRider.OnFlungEvent(rider, truckId, hold, tF, worldStart, vel, flags, cause);
    }

    public void LocalRideLanded(bool soft)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideLanded(LocalId, soft);
        else RideLandedRpc(soft);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideLandedRpc(bool soft, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.landed", sender, 8)) return;
        HostRideLanded(sender, soft);
    }

    void HostRideLanded(ulong client, bool soft)
    {
        if (!soft) { HostLanded(client); return; }
        int i = BodyIndex(client);
        if (i < 0) return;
        var b = bodies[i];
        if (b.State != BodyState.Thrown) return;
        double now = ServerNow;
        b.state = (byte)BodyState.Normal;
        b.other = None;
        b.until = 0;
        b.immuneUntil = now + HandsTuning.Immunity;
        bodies[i] = b;
    }

    // ==================================================================================== riders shoving riders

    public void LocalRideShove(ulong target)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideShove(LocalId, target);
        else RideShoveRpc(target);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideShoveRpc(ulong target, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.shove", sender, 16)) return;
        HostRideShove(sender, target);
    }

    void HostRideShove(ulong attacker, ulong target)
    {
        string why = null;
        var a = BodyOf(attacker);
        var t = BodyOf(target);
        float now = Time.unscaledTime;
        Vector3 dir = Vector3.zero;
        if (attacker == target) why = "self";
        else if (a.State != BodyState.Riding || t.State != BodyState.Riding || a.other != t.other) why = "not-riding-together";
        else if (lastRideShoveAt.TryGetValue(attacker, out var last) && now - last < RideTuning.ShoveCooldown - 0.2f) why = "cooldown";
        else if (!HostPlayerPos(attacker, out var ap) || !HostPlayerPos(target, out var tp)) why = "missing";
        else if ((ap - tp).sqrMagnitude > Sq(RideTuning.ShoveRange + HandsTuning.LagSlack)) why = "far";
        else dir = tp - ap;
        if (why == null)
        {
            lastRideShoveAt[attacker] = now;
            dir.y = 0f;
            RideShovedRpc(target, attacker, dir);
        }
        NetAudit.Event("ride", new Dictionary<string, object> { { "op", "hostshove" }, { "attacker", (long)attacker }, { "target", (long)target }, { "ok", why == null }, { "why", why } });
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RideShovedRpc(ulong target, ulong attacker, Vector3 dir)
    {
        var r = RideRider.For(target);
        if (r != null) r.OnShoved(attacker, dir);
    }

    // ==================================================================================== knock-offs (other packages)

    /// <summary>Host: knock a rider off (<paramref name="forced"/>) or take grip (HOLD ON-able). See <see cref="Riding"/>.</summary>
    public void HostRideKnock(ulong target, Vector3 push, float cost, bool forced, bool savable, string word)
    {
        if (!IsServer || !IsSpawned) return;
        if (BodyOf(target).State != BodyState.Riding) return;
        var w = new FixedString32Bytes();
        w.CopyFromTruncated(word ?? "");
        RideKnockRpc(target, push, cost, forced, savable, w);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RideKnockRpc(ulong target, Vector3 push, float cost, bool forced, bool savable, FixedString32Bytes word)
    {
        var r = RideRider.For(target);
        if (r == null) return;
        if (r.IsLocal) r.ApplyHit(push, cost, forced, savable, word.ToString(), None);
        else if (word.Length > 0) GameHUD.FloatingText(r.transform.position + Vector3.up * 2.3f, word.ToString(), new Color(1f, 0.6f, 0.3f), 1f);
    }

    // ==================================================================================== pay (streak bonus, Crew Comfort)

    public void LocalRidePay(byte kind, ulong truckId, int n)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRidePay(LocalId, kind, truckId, n);
        else RidePayRpc(kind, truckId, n);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RidePayRpc(byte kind, ulong truckId, int n, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.hands.ridepay", sender, 16)) return;
        HostRidePay(sender, kind, truckId, n);
    }

    void HostRidePay(ulong client, byte kind, ulong truckId, int n)
    {
        var hs = HitchStep.Find(truckId);
        var truck = hs != null ? hs.Truck : null;
        string why = null;
        float amount = 0f, now = Time.unscaledTime;
        string reason = null, id = null;
        ulong key = client * 4 + kind;
        if (truck == null) why = "no-truck";
        else if (lastRidePayAt.TryGetValue(key, out var last) && now - last < (kind == RidePayComfort ? 6f : 0.8f)) why = "too-soon";
        else if (kind == RidePayStreak)
        {
            var b = BodyOf(client);
            if (b.State != BodyState.Riding || b.other != truckId) why = "not-riding";
            else
            {
                n = Mathf.Clamp(n, 2, 99);
                amount = Mathf.Min(RideTuning.StreakBonusCap, RideTuning.StreakBonusPer * (n - 1));
                reason = $"Drive-by STREAK x{n}";
                id = RideTuning.LedgerStreak;
            }
        }
        else if (kind == RidePayComfort)
        {
            int riders = RidersOn(truckId);
            if (truck.DriverClientId != client) why = "not-driver";
            else if (riders <= 0) why = "no-riders";
            else
            {
                amount = Mathf.Min(n, Mathf.RoundToInt(RideTuning.ComfortPerRider * riders * 100f)) / 100f;
                reason = "Crew Comfort tip";
                id = RideTuning.LedgerComfort;
            }
        }
        else why = "kind";
        if (why == null && amount > 0f)
        {
            lastRidePayAt[key] = now;
            var hopper = truck.GetComponentInChildren<TruckHopper>(true);
            string biz = hopper != null ? hopper.ResolveBusiness() : null;
            using (Ledger.ToBusiness(string.IsNullOrEmpty(biz) || biz == Businesses.LocalId ? null : biz))
                Ledger.Credit(amount, reason, id, truck.transform.position);
        }
        NetAudit.Event("ride", new Dictionary<string, object>
        {
            { "op", "hostpay" }, { "client", (long)client }, { "kind", kind }, { "truck", (long)truckId }, { "n", n },
            { "cents", Mathf.RoundToInt(amount * 100f) }, { "ok", why == null }, { "why", why },
        });
    }

    // ==================================================================================== truck mods

    public void LocalRideMods(ulong truckId, byte padded, byte bar)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideMods(LocalId, truckId, padded, bar);
        else RideModsRpc(truckId, padded, bar);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideModsRpc(ulong truckId, byte padded, byte bar, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.mods", sender, 16)) return;
        HostRideMods(sender, truckId, padded, bar);
    }

    void HostRideMods(ulong client, ulong truckId, byte padded, byte bar)
    {
        var hs = HitchStep.Find(truckId);
        if (hs == null || rideMods == null) return;
        string biz = DepotTruckCargo.BusinessOf(hs.Truck);
        if (!Businesses.TryGetForClient(client, out var b) || !b.IsValid || b.Id != biz) return;
        padded = (byte)Mathf.Min(padded, (byte)2);
        bar = (byte)Mathf.Min(bar, (byte)1);
        for (int i = 0; i < rideMods.Count; i++)
        {
            if (rideMods[i].truck != truckId) continue;
            // Repeated owner reports must not put a crew's raised bar down again.
            var old = rideMods[i];
            var m = new RideModsNet { truck = truckId, padded = padded, bar = bar,
                barDown = bar > 0 && (old.bar == 0 || old.barDown) };
            if (!rideMods[i].Equals(m)) rideMods[i] = m;
            return;
        }
        rideMods.Add(new RideModsNet { truck = truckId, padded = padded, bar = bar, barDown = bar > 0 });
    }

    /// <summary>Any rider may fold the truck's Safety Bar. The host owns the state in rideMods so late joiners see it.</summary>
    public void LocalRideBarToggle(ulong truckId)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideBarToggle(LocalId, truckId);
        else RideBarToggleRpc(truckId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideBarToggleRpc(ulong truckId, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.bar", sender, 12)) return;
        HostRideBarToggle(sender, truckId);
    }

    void HostRideBarToggle(ulong client, ulong truckId)
    {
        if (!IsServer || rideMods == null) return;
        var body = BodyOf(client);
        if (body.State != BodyState.Riding || body.other != truckId) return;
        float now = Time.unscaledTime;
        if (lastRideBarAt.TryGetValue(truckId, out float last) && now - last < 0.35f) return;
        for (int i = 0; i < rideMods.Count; i++)
        {
            var mods = rideMods[i];
            if (mods.truck != truckId || mods.bar == 0) continue;
            mods.barDown = !mods.barDown;
            rideMods[i] = mods;
            lastRideBarAt[truckId] = now;
            NetAudit.Event("ride", new Dictionary<string, object> { { "op", "bar" }, { "truck", (long)truckId },
                { "by", (long)client }, { "down", mods.barDown } });
            return;
        }
    }

    /// <summary>The rider's small A/D pose choice, for other players' puppets. Gameplay grip stays rider-local.</summary>
    public void LocalRideLean(sbyte lean)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideLean(LocalId, lean);
        else RideLeanRpc(lean);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RideLeanRpc(sbyte lean, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.lean", sender, 8)) return;
        HostRideLean(sender, lean);
    }

    void HostRideLean(ulong client, sbyte lean)
    {
        if (!IsServer || BodyOf(client).State != BodyState.Riding) return;
        RideLeanAllRpc(client, (sbyte)Mathf.Clamp(lean, -1, 1));
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RideLeanAllRpc(ulong client, sbyte lean)
    {
        var rider = RideRider.For(client);
        if (rider != null) rider.ReceiveRemoteLean(lean);
    }

    /// <summary>Five-Hz grip pips for the driver's crew HUD; rider-local grip remains authoritative for flings.</summary>
    public void LocalRideGrip(byte grip)
    {
        if (!IsSpawned) return;
        if (IsServer) HostRideGrip(LocalId, grip);
        else RideGripRpc(grip);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
    void RideGripRpc(byte grip, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        EnsureRideGuards();
        if (!Allowed("rb.ride.grip", sender, 8)) return;
        HostRideGrip(sender, grip);
    }

    void HostRideGrip(ulong client, byte grip)
    {
        if (!IsServer || BodyOf(client).State != BodyState.Riding) return;
        RideGripAllRpc(client, (byte)Mathf.Min(grip, 100));
    }

    [Rpc(SendTo.ClientsAndHost, Delivery = RpcDelivery.Unreliable)]
    void RideGripAllRpc(ulong client, byte grip)
    {
        var rider = RideRider.For(client);
        if (rider != null) rider.ReceiveRemoteGrip(grip);
    }
}

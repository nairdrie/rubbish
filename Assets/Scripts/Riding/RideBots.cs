using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// F1 test rig (Docs/Multiplayer.md §14.1, §8.4): a scripted truck route and bot brains that ride it.
/// <list type="bullet">
/// <item><see cref="RideRoute"/>: the "conductor" drives one truck around a fixed loop on Minivan Mile → Depot Rd
/// (teleport to the start, stop for boarding, creep past curb bags at 2 m/s, run at ~9.5 m/s, hard right at the corner,
/// brake) through <see cref="TruckControllerV2.InputOverride"/>. Phases follow NGO server time, so every machine knows
/// what's coming. Solo tests use it as a ghost driver.</item>
/// <item><c>-botbrain ride</c>: every bot rides (the host also conducts its lot-1 truck with a ghost driver, so four
/// players can ride at once); bots board at their hold, snag bags while creeping, one shoves its neighbour, they lean
/// (one into the turn, one the wrong way) and answer HOLD ON half the time.</item>
/// <item><c>-botbrain ridedrive</c>: this bot drives its own truck for real (the Crew Comfort tip) and conducts.</item>
/// </list>
/// Every machine logs <c>ride</c> audit lines (fling / flingseen with the truck pose at the fling's tick) for the checker.
/// </summary>
public static class RideBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        BotBrains.Register("ride", () => new RideBrain(false));
        BotBrains.Register("ridedrive", () => new RideBrain(true));
        BotBrains.Register("rideverify", () => new RideBrain(false, true));
    }

    /// <summary>The truck the test rides: one with a real driver, else lot 1's, else any truck with a hopper.</summary>
    public static TruckControllerV2 RideTruck()
    {
        var all = TruckControllerV2.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && all[i].IsSpawned && all[i].HasDriver && all[i].GetComponentInChildren<TruckHopper>(true) != null) return all[i];
        var dn = DepotRowNet.Instance;
        if (dn != null && dn.IsSpawned)
        {
            var t = dn.TruckOf(0);
            if (t != null) return t;
        }
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && all[i].GetComponentInChildren<TruckHopper>(true) != null) return all[i];
        return null;
    }

    sealed class RideBrain : IBotBrain
    {
        readonly bool driver, verify;
        RideRoute route;
        long boardedCycle = -1, hopCycle = -1, shoveCycle = -1;
        long barCycle = -1, barViewCycle = -1, poseViewCycle = -1;
        bool grantsDone;
        float teleportedAt = -1f;
        float nextSnagTry;
        bool holdOnDecided, holdOnPress;
        float lastHoldOnStart = -1f;
        bool enteredTruck;
        float minGrip = 100f;

        public RideBrain(bool driver, bool verify = false) { this.driver = driver; this.verify = verify; }

        public void Begin(BotContext c)
        {
            c.Stop();
            NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "begin" }, { "driver", driver } });
        }

        public void End(BotContext c)
        {
            if (route != null) route.Stop();
            var me = RideRider.LocalRider;
            if (me != null) me.BotLean = null;
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            var net = HandsNet.Instance;
            if (c.Player == null || c.Elapsed < 12f) return;
            if (verify && !grantsDone)
            {
                // Bot save roots are sandboxed; use the same upgrade store as real purchases and let the normal
                // truck-owner report share them. No test-only bypass of the Safety Bar network validation.
                grantsDone = true;
                GameSession.SetUpgradeLevel(RideShop.GripGloves, 1);
                GameSession.SetUpgradeLevel(RideShop.PaddedStep, 1);
                GameSession.SetUpgradeLevel(RideShop.SafetyBar, 1);
                NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "upgrades" },
                    { "gloves", RideShop.Local(RideShop.GripGloves) }, { "pad", RideShop.Local(RideShop.PaddedStep) },
                    { "bar", RideShop.Local(RideShop.SafetyBar) } });
            }
            var truck = RideBots.RideTruck();
            if (truck == null) return;
            double now = RideClock.Now;

            // ---- the conductor: the host's ghost driver (no ridedrive bot), or the ridedrive bot in its own truck
            if (driver)
            {
                var pc = c.Player.GetComponent<PlayerController>();
                var mine = OwnTruck();
                if (mine != null && !enteredTruck)
                {
                    HandsBots.Teleport(c.Player.gameObject, mine.transform.position - mine.transform.right * 2.6f, mine.transform.forward);
                    mine.OnInteract(c.Player.gameObject);
                    enteredTruck = mine.IsLocalPlayerSeated;
                    NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "enter" }, { "ok", enteredTruck } });
                    return;
                }
                if (mine != null && mine.IsOwner && mine.IsLocalDriver)
                {
                    if (route == null || route.Truck != mine) route = new RideRoute(mine, true);
                    route.Tick(now);
                }
                return;
            }
            bool someoneDrives = false;
            foreach (var t in TruckControllerV2.All) if (t != null && t.HasDriver) someoneDrives = true;
            if (c.IsHost && !someoneDrives)
            {
                if (route == null || route.Truck != truck) route = new RideRoute(truck, true);
                route.Tick(now);
            }
            else if (route != null) { route.Stop(); route = null; } // a real driver took over

            // ---- riding
            var me = RideRider.LocalRider;
            var hs = truck.GetComponentInChildren<HitchStep>(true);
            var rt = global::RideTruck.For(truck);
            if (me == null || hs == null || rt == null) return;
            long cycle = RideRoute.Cycle(now);
            double ph = RideRoute.Phase(now);
            int idx = RiderIndex(net, truck), hold = idx % RideHolds.Count;

            if (ph >= 1.5 && ph < 5.5 && boardedCycle != cycle && !me.IsRiding && !me.IsFlying && me.CanBoard)
            {
                if (teleportedAt < 0f)
                {
                    var pose = RideHolds.PoseOn(truck.transform, hold);
                    Vector3 out_ = RideHolds.IsRung(hold) ? pose.rotation * Vector3.right * RideHolds.Side(hold) : -(pose.rotation * Vector3.forward);
                    HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(pose.position + out_ * 0.9f), pose.rotation * Vector3.forward);
                    teleportedAt = Time.time;
                }
                else if (Time.time - teleportedAt > 0.7f)
                {
                    me.Hitch(hs, hold);
                    boardedCycle = cycle;
                    teleportedAt = -1f;
                    NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "board" }, { "cycle", cycle }, { "hold", hold }, { "idx", idx } });
                }
            }
            if (ph >= 5.5) teleportedAt = -1f;

            if (!me.IsRiding)
            {
                me.BotLean = null;
                return;
            }

            if (verify && idx == 1 && ph >= 7.0 && ph < 9.0 && barCycle != cycle && rt.BarLevel > 0)
            {
                barCycle = cycle;
                me.ToggleBar();
                NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "bar-request" },
                    { "cycle", cycle }, { "truck", (long)rt.NetId } });
            }
            if (verify && ph >= 10.0 && ph < 13.0 && barViewCycle != cycle)
            {
                barViewCycle = cycle;
                NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "bar-view" },
                    { "cycle", cycle }, { "rider", (long)me.ClientId }, { "bar", rt.BarLevel },
                    { "down", rt.BarDown }, { "gloves", RideShop.Local(RideShop.GripGloves) },
                    { "pad", rt.PaddedLevel }, { "remoteLean", me.RemoteLean } });
            }

            // Creep: snag bags in reach; one rider shoves the neighbour.
            if (ph >= RideRoute.CreepStart && ph < RideRoute.RunStart && Time.time >= nextSnagTry && me.SnagCandidate != null)
            {
                nextSnagTry = Time.time + 0.4f;
                me.BotSnag();
            }
            if (idx == 1 && ph >= 11.0 && shoveCycle != cycle) { shoveCycle = cycle; me.BotShove(); }

            // Run and corner (a right turn: the truck accelerates toward its right, so "into the turn" is D).
            if (ph >= RideRoute.RunStart) { me.BotLean = idx == 0 ? 1 : idx == 2 ? -1 : 0; minGrip = Mathf.Min(minGrip, me.Grip); }
            else me.BotLean = 0;
            if (verify && ph >= 22.0 && ph < 25.0 && poseViewCycle != cycle)
            {
                poseViewCycle = cycle;
                foreach (var other in RideRider.All)
                {
                    if (other == null || other == me || !other.TryGetRide(out var ort, out int otherHold) || ort != rt) continue;
                    NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "remote-view" },
                        { "cycle", cycle }, { "observer", (long)me.ClientId }, { "subject", (long)other.ClientId },
                        { "hold", otherHold }, { "lean", other.RemoteLean }, { "grip", Mathf.RoundToInt(other.GripForHud) } });
                }
            }

            // HOLD ON: answer about half the windows, a beat after they open.
            if (me.HoldOnActive)
            {
                if (lastHoldOnStart != me.HoldOnStart) { lastHoldOnStart = me.HoldOnStart; holdOnDecided = false; }
                if (!holdOnDecided) { holdOnDecided = true; holdOnPress = c.Value() < 0.5f; }
                if (holdOnPress && Time.time - me.HoldOnStart > 0.15f) { me.BotPressSpace(); holdOnPress = false; }
            }

            // Late in the cycle anyone still aboard hops off so the next lap starts clean.
            if (ph >= RideRoute.CycleSec - 1.5 && hopCycle != cycle)
            {
                hopCycle = cycle;
                NetAudit.Event("ridebot", new Dictionary<string, object> { { "phase", "survived" }, { "cycle", cycle }, { "idx", idx }, { "minGrip", Mathf.RoundToInt(minGrip) }, { "lean", me.BotLean ?? 0 } });
                minGrip = 100f;
                me.Unhitch();
            }
        }

        static TruckControllerV2 OwnTruck()
        {
            var dn = DepotRowNet.Instance;
            if (dn == null || !dn.IsSpawned) return null;
            for (int lot = 0; lot < DepotRowLayout.LotCount; lot++)
            {
                if (!dn.TryGetLot(lot, out var s) || !s.Claimed || s.ownerClientId != dn.NetworkManager.LocalClientId) continue;
                return dn.TruckOf(lot);
            }
            return null;
        }

        static int RiderIndex(HandsNet net, TruckControllerV2 truck)
        {
            var ids = new List<ulong>();
            foreach (var r in RideRider.All)
                if (r != null && r.Player != null && r.Player.IsSpawned && r.ClientId != truck.DriverClientId) ids.Add(r.ClientId);
            ids.Sort();
            var me = RideRider.LocalRider;
            int i = me != null ? ids.IndexOf(me.ClientId) : 0;
            return Mathf.Max(0, i);
        }
    }
}

/// <summary>
/// A scripted lap for one truck (test rig): Minivan Mile westbound past the Depot Row lots, then a hard right across
/// the old depot pad onto Depot Rd. Pure pursuit on waypoints + a speed loop, through <see cref="TruckControllerV2.InputOverride"/>.
/// Phases (seconds into a <see cref="CycleSec"/> cycle of NGO server time): 0 teleport to the start, 0–6 parked
/// (boarding, bags put out), 6–16 creep at 2 m/s past the bags, 16+ run at full throttle through the corner, then brake.
/// </summary>
public sealed class RideRoute
{
    public const double CycleSec = 56.0, CreepStart = 6.0, RunStart = 16.0;
    public const float CreepSpeed = 2f, RunSpeed = 9.6f, GentleSpeed = 5f;

    // Westbound on Minivan Mile (north lane) past the Depot Row lots, then a hard right across the old depot pad onto
    // Depot Rd / Elm Ave northbound. (Eastbound runs into the Minivan Meadows gate at x 206 without a permit.)
    public static readonly Vector3[] Path =
    {
        new Vector3(150f, 0f, -3.4f), new Vector3(24f, 0f, -3.4f), new Vector3(10f, 0f, 2f), new Vector3(2.5f, 0f, 14f), new Vector3(2.5f, 0f, 55f),
    };
    /// <summary>Curb bags go on the north curb, on the truck's right.</summary>
    static readonly Vector3 BagLine = new Vector3(0f, 0f, -0.6f);

    public static long Cycle(double now) => (long)Math.Floor(now / CycleSec);
    public static double Phase(double now) => now - Cycle(now) * CycleSec;

    public TruckControllerV2 Truck { get; }
    readonly bool spawnBags;
    long teleported = -1, bagsCycle = -1;
    Vector2 cmd;
    bool driving;
    readonly List<TrashBag> bags = new List<TrashBag>();
    float[] segLen;
    global::RideTruck rtruck;
    float lapMaxV, lapMaxLat, lapMaxBrake;
    float total;

    public RideRoute(TruckControllerV2 truck, bool spawnBags)
    {
        Truck = truck;
        this.spawnBags = spawnBags;
        segLen = new float[Path.Length - 1];
        for (int i = 0; i < segLen.Length; i++) { segLen[i] = Flat(Path[i + 1] - Path[i]).magnitude; total += segLen[i]; }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    public void Stop()
    {
        if (Truck != null) Truck.InputOverride = null;
        driving = false;
    }

    public void Tick(double now)
    {
        if (Truck == null) return;
        long cycle = Cycle(now);
        double ph = Phase(now);
        var body = Truck.Body;
        float v = body != null ? Vector3.Dot(body.linearVelocity, Truck.transform.forward) : 0f;

        if (rtruck == null) rtruck = global::RideTruck.For(Truck);
        if (teleported != cycle && ph < 1.0)
        {
            if (teleported >= 0)
                NetAudit.Event("ride", new Dictionary<string, object> { { "op", "lapstats" }, { "cycle", teleported }, { "maxV", System.Math.Round(lapMaxV, 2) }, { "maxLat", System.Math.Round(lapMaxLat, 2) }, { "maxBrake", System.Math.Round(lapMaxBrake, 2) } });
            lapMaxV = lapMaxLat = lapMaxBrake = 0f;
            teleported = cycle;
            Teleport();
            if (DepotTruckCargo.IsLocal(Truck) && GameSession.TruckFill01 > 0.5f) GameSession.TakeAllTruckBags();
        }
        if (spawnBags && bagsCycle != cycle && ph >= 1.0 && ph < CreepStart && (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || NetworkManager.Singleton.IsServer))
        {
            bagsCycle = cycle;
            PutOutBags(cycle);
        }

        // Even laps are hard (flings at the corner), odd laps gentle (riders hang on; the Crew Comfort tip).
        bool gentle = (cycle & 1) == 1;
        float run = gentle ? GentleSpeed : RunSpeed;
        float target;
        if (ph < CreepStart) target = 0f;
        else if (ph < RunStart) target = CreepSpeed;
        else target = Progress(Truck.transform.position) > PathS(3) + 12f ? 0f : run;
        if (ph >= CycleSec - 4.0) target = 0f;

        if (target <= 0.01f && Mathf.Abs(v) < 0.4f)
        {
            if (driving) { Truck.InputOverride = null; driving = false; } // parking brake holds it
            return;
        }
        if (ph >= RunStart && rtruck != null) { lapMaxV = Mathf.Max(lapMaxV, v); lapMaxLat = Mathf.Max(lapMaxLat, Mathf.Abs(rtruck.LatNow)); lapMaxBrake = Mathf.Max(lapMaxBrake, rtruck.BrakeNow); }
        float steer = Steer(v);
        float throttle = Mathf.Clamp((target - v) * 0.7f, -1f, 1f);
        if (target >= RunSpeed - 0.01f && v < target) throttle = 1f;
        if (target <= 0.01f) throttle = v > 0.2f ? (gentle ? -0.35f : -1f) : 0f;
        cmd = new Vector2(steer, throttle);
        if (!driving) { Truck.InputOverride = () => cmd; driving = true; }
    }

    void Teleport()
    {
        Vector3 p = Path[0] + Vector3.up * 0.25f;
        if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 20f, Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer), QueryTriggerInteraction.Ignore))
            p = hit.point + Vector3.up * 0.15f;
        Quaternion r = Quaternion.LookRotation(Flat(Path[1] - Path[0]).normalized, Vector3.up);
        Truck.InputOverride = null;
        driving = false;
        var body = Truck.Body;
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = p;
            body.rotation = r;
        }
        var nt = Truck.GetComponent<NetworkTransform>();
        if (nt != null && nt.IsSpawned && nt.CanCommitToTransform) nt.Teleport(p, r, Truck.transform.localScale);
        else Truck.transform.SetPositionAndRotation(p, r);
        NetAudit.Event("ride", new Dictionary<string, object> { { "op", "lap" }, { "truck", (long)(Truck.IsSpawned ? Truck.NetworkObjectId : 0) } });
    }

    void PutOutBags(long cycle)
    {
        foreach (var b in bags) if (b != null && b.State == CarryState.Resting) b.Consume();
        bags.Clear();
        for (int k = 0; k < 6; k++)
        {
            var contents = new BagContents { bagId = $"ridebag/{cycle}/{k}", foundAt = "", bagMassKg = 2.5f, sackVariant = k % 3 };
            Vector3 p = new Vector3(Path[0].x - 1.5f - k * 3.5f, 0.6f, BagLine.z);
            if (Physics.Raycast(p + Vector3.up * 4f, Vector3.down, out var hit, 10f, Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer), QueryTriggerInteraction.Ignore))
                p = hit.point + Vector3.up * 0.35f;
            var bag = TrashBag.Spawn(contents, p, Quaternion.identity);
            if (bag != null) bags.Add(bag);
        }
    }

    float PathS(int waypoint)
    {
        float s = 0f;
        for (int i = 0; i < waypoint && i < segLen.Length; i++) s += segLen[i];
        return s;
    }

    float Progress(Vector3 p)
    {
        float best = float.MaxValue, bestS = 0f, acc = 0f;
        for (int i = 0; i < segLen.Length; i++)
        {
            Vector3 a = Flat(Path[i]), b = Flat(Path[i + 1]);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(Flat(p) - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
            float d = (Flat(p) - (a + ab * t)).sqrMagnitude;
            if (d < best) { best = d; bestS = acc + segLen[i] * t; }
            acc += segLen[i];
        }
        return bestS;
    }

    Vector3 PointAt(float s)
    {
        s = Mathf.Clamp(s, 0f, total);
        for (int i = 0; i < segLen.Length; i++)
        {
            if (s <= segLen[i]) return Vector3.Lerp(Flat(Path[i]), Flat(Path[i + 1]), segLen[i] > 0f ? s / segLen[i] : 0f);
            s -= segLen[i];
        }
        return Flat(Path[Path.Length - 1]);
    }

    float Steer(float v)
    {
        Vector3 pos = Truck.transform.position;
        float look = Mathf.Clamp(2f + 0.35f * Mathf.Abs(v), 3.5f, 6f);
        Vector3 target = PointAt(Progress(pos) + look);
        Vector3 to = Flat(target - pos);
        Vector3 fwd = Flat(Truck.transform.forward);
        if (to.sqrMagnitude < 0.01f || fwd.sqrMagnitude < 0.01f) return 0f;
        float ang = Vector3.SignedAngle(fwd, to, Vector3.up);
        float maxSteer = Mathf.Lerp(Truck.steeringRange, Truck.steeringRangeAtMaxSpeed, Mathf.InverseLerp(0f, Truck.maxSpeed, Mathf.Abs(v)));
        return Mathf.Clamp(ang / Mathf.Max(1f, maxSteer), -1f, 1f);
    }
}

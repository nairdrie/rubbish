using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Package F1 "Riding the truck" (Docs/Multiplayer.md §14.1, §4.4): numbers, hold points, the clock, events and the
// public API other packages use (F4 night shift: spotlight rider, stowaway shooing).

/// <summary>
/// The numbers of §4.4 / §14.1 in one place. Riders stay on solidly: no grip meter, no lean, no HOLD ON, no shoving (Nick,
/// 12 Oct 2026). Rule 12 (every physical verb declares its limits):
/// <list type="bullet">
/// <item><b>Crash fling</b>: a heavy hit (the truck's speed changes by ≥ 5 m/s at once: a tree, a wall, another truck, being
/// rear-ended) throws every rider off. Detected by the machine that simulates the truck, applied by each rider's own machine
/// at the truck tick it shows; max helpless = the flight (≤ 2 s) + 1 s knocked out; opt-in (you chose to ride); counter =
/// the Padded Step (+1.5 / +3 m/s); preset toggle = the Safety Bar (B; while down, crashes under 8 m/s don't fling).</item>
/// <item><b>Switch spot</b> (Shift): the rider asks, the host checks the hold is still free; 0.25 s cooldown; affects
/// nobody else; never helpless (a 0.3–0.7 s shuffle round the back, still riding).</item>
/// <item><b>Knock-off API</b> (other packages): host decides, the rider's machine flies.</item>
/// </list>
/// </summary>
public static class RideTuning
{
    // crashes
    /// <summary>§4.4: "a heavy truck impact flings every rider at once": the truck's horizontal velocity changes by at least
    /// this much within <see cref="CrashWindowSteps"/> physics steps. Braking (≤ ~0.4 m/s a step even with locked wheels),
    /// hard cornering and drifting (≤ ~1.2 m/s over the window), curbs and potholes stay far below it.</summary>
    public const float FlingAllDv = 5f;
    /// <summary>A hit the solver spreads over two or three steps still reads as one Δv (~0.06 s at 50 Hz).</summary>
    public const int CrashWindowSteps = 3;
    /// <summary>One crash per <see cref="CrashCooldown"/> s, unless the hit is still building (Δv up by
    /// <see cref="CrashGrowth"/>: a later, bigger reading may beat a Safety Bar the first one didn't).</summary>
    public const float CrashCooldown = 0.3f, CrashGrowth = 1f;
    /// <summary>Safety Bar down: crashes under this Δv don't fling anyone. The Padded Step adds <see cref="PadDvPerLevel"/>
    /// per level on top (either way).</summary>
    public const float SafetyBarDv = 8f, PadDvPerLevel = 1.5f;

    /// <summary>The Δv a crash needs to throw this truck's riders off (its Padded Step level and Safety Bar).</summary>
    public static float CrashFlingDv(int padded, int barLevel, bool barDown) =>
        Mathf.Max(FlingAllDv, barLevel > 0 && barDown ? SafetyBarDv : 0f) + PadDvPerLevel * Mathf.Clamp(padded, 0, 2);

    // fling
    public const float FlingOut = 4f, FlingUp = 4.2f, FlingKeep = 1f, Gravity = 15f, MaxAir = 2f;
    public const float FlightRadius = 0.32f, FlightDt = 1f / 60f;
    public const float KnockedOut = 1.0f;

    // switching spots
    /// <summary>Shift moves a rider at most this often; the shuffle round the back takes <see cref="ShuffleMin"/> s plus
    /// <see cref="ShufflePerMetre"/> per metre of path, at most <see cref="ShuffleMax"/>, with a small hop.</summary>
    public const float SwitchCooldown = 0.25f, ShuffleMin = 0.3f, ShuffleMax = 0.7f, ShufflePerMetre = 0.06f, ShuffleHop = 0.12f;

    // drive-by grabs (§4.4: curb bags 1.1–1.8 m from the truck; step reach 1.6 m + a little stretch; ≤ 2.5 m/s)
    public const float SnagMaxSpeed = 2.5f, SnagReach = 1.9f, StreakGap = 10f;
    public const float StreakBonusPer = 0.5f, StreakBonusCap = 3f;

    // driver's Crew Comfort tip (per stop with riders aboard). The leg's roughest moment (sideways g, braking, drift) and its
    // bumps and knocks spoil it; all measured where the truck is simulated, never sent anywhere.
    public const float ComfortPerRider = 0.75f, ComfortMinLeg = 25f, ComfortStopSpeed = 0.4f;
    public const float ComfortLatFree = 2.5f, ComfortLatCost = 45f, ComfortBrakeFree = 5f, ComfortBrakeCost = 50f;
    public const float ComfortDriftFree = 1.2f, ComfortDriftCost = 30f;
    /// <summary>Per physics step: a vertical Δv over this is a bump/curb/pothole, a horizontal one over the other a knock;
    /// at most one counts per <see cref="ComfortKnockGap"/> s.</summary>
    public const float ComfortBumpDv = 0.9f, ComfortKnockDv = 1.1f, ComfortKnockGap = 0.3f;

    // pose stream (simulating machine → everyone, 10 Hz while anyone rides): only so each machine can measure how far its
    // displayed truck lags the driver's clock, and apply a crash when its own copy of the truck reaches it
    public const float SampleHz = 10f;

    /// <summary>Stock lag guess before the first match (seconds the remote truck is shown behind the driver).</summary>
    public const float LagGuess = 0.12f;

    public const string LedgerStreak = "ride_streak", LedgerComfort = "crew_comfort";
}

/// <summary>The four hold points of a truck, where you can climb on, and the way round the back between them, in truck metres
/// (Truck V2's root is scaled 0.67, like ShopModKit; <see cref="TruckModelVisuals.MapMetres"/> fits these rear-loader
/// numbers to the Curb Pup's smaller body).</summary>
public static class RideHolds
{
    public const int Count = 4;
    public const int RearLeft = 0, RearRight = 1, RungLeft = 2, RungRight = 3;
    public const float LadderHalfWidth = 0.18f;
    /// <summary>The boarding area (real metres): this far out behind the rear face, this far out past each side, and this
    /// far forward of the side ladders.</summary>
    public const float BoardBehind = 3f, BoardSide = 1.4f, BoardPastLadder = 1f;

    // Feet positions (metres from the root; ground ≈ y 0.4). Rear steps sit just behind the hopper mouth; the rungs on
    // each flank near the rear corner.
    static readonly Vector3[] feet =
    {
        new Vector3(-0.50f, 0.64f, -2.65f), new Vector3(0.50f, 0.64f, -2.65f),
        new Vector3(-1.52f, 0.78f, -1.45f), new Vector3(1.52f, 0.78f, -1.45f),
    };

    public static bool IsRung(int hold) => hold >= 2;
    /// <summary>−1 left, +1 right.</summary>
    public static int Side(int hold) => hold == RearLeft || hold == RungLeft ? -1 : 1;
    public static string Name(int hold) => IsRung(hold) ? "side ladder" : "rear step";

    public static Vector3 FeetMetres(int hold) => feet[Mathf.Clamp(hold, 0, Count - 1)];

    /// <summary>Where the rider's hands hold on (metres).</summary>
    public static Vector3 HandMetres(int hold)
    {
        var f = FeetMetres(hold);
        return IsRung(hold) ? new Vector3(f.x * 0.93f, f.y + 1.15f, f.z) : new Vector3(f.x, f.y + 1.1f, f.z + 0.25f);
    }

    /// <summary>Actual hand-contact points on the F1 kit rails. Rear mitts split a horizontal grab bar;
    /// side mitts take the two ladder uprights. Keep in step with RideTruck.BuildKit.</summary>
    public static Vector3 GripMetres(int hold, bool leftHand)
    {
        var f = FeetMetres(hold);
        if (IsRung(hold))
            return new Vector3(Side(hold) * 1.36f, 1.72f, f.z + (leftHand ? -LadderHalfWidth : LadderHalfWidth));
        return new Vector3(f.x + (leftHand ? -0.18f : 0.18f), 1.72f, -2.36f);
    }

    /// <summary>The camera yaw (degrees, relative to the truck) a rider on this hold looks along: a side ladder angles it in
    /// toward the body so the camera swings out beside the truck instead of into its flank.</summary>
    public static float ViewYaw(int hold) => IsRung(hold) ? -Side(hold) * 28f : 0f;

    // ---- boarding: one generous area round the back (Nick, 12 Oct 2026: "should just be e anywhere near the back")

    /// <summary>The boarding area in the truck's own real metres (rotation only, no model scale) for a body box
    /// (<see cref="TruckControllerV2.ModelBodyBoxMetres"/>) and its side ladders' real z: from <see cref="BoardBehind"/> behind
    /// the rear face, the body's width plus <see cref="BoardSide"/> each side, forward along both flanks to just past the
    /// ladders. The cab doors, the nose and the front half of the flanks stay outside it ("Enter Truck" lives there).</summary>
    public static Bounds BoardingArea(Bounds body, float ladderZ)
    {
        float half = Mathf.Max(-body.min.x, body.max.x) + BoardSide;
        var area = new Bounds();
        area.SetMinMax(new Vector3(-half, body.min.y - 1.4f, body.min.z - BoardBehind),
            new Vector3(half, body.min.y + 1.8f, Mathf.Max(ladderZ + BoardPastLadder, body.min.z + 0.5f)));
        return area;
    }

    /// <summary>This truck's boarding area for the body it has now (the Curb Pup's is smaller), in real truck metres.</summary>
    public static Bounds BoardingArea(TruckControllerV2 truck) => truck == null
        ? BoardingArea(TruckControllerV2.BodyBoxMetres, FeetMetres(RungLeft).z)
        : BoardingArea(truck.ModelBodyBoxMetres, TruckModelVisuals.MapMetres(truck.transform, FeetMetres(RungLeft)).z);

    /// <summary>A world point in the truck's real metres: position and rotation only, no root or model scale.</summary>
    public static Vector3 RealMetres(Transform truck, Vector3 world) => Quaternion.Inverse(truck.rotation) * (world - truck.position);

    /// <summary>Can a player whose feet are at <paramref name="world"/> climb on? (The host adds its lag slack.)</summary>
    public static bool InBoardingArea(TruckControllerV2 truck, Vector3 world, float slack = 0f)
    {
        if (truck == null) return false;
        var area = BoardingArea(truck);
        if (slack > 0f) area.Expand(slack * 2f);
        return area.Contains(RealMetres(truck.transform, world));
    }

    /// <summary>The free hold nearest a player at <paramref name="local"/> (real truck metres; the holds scaled by the body's
    /// <paramref name="modelScale"/>), or −1 when every hold is in <paramref name="takenMask"/> (a bit per hold). Nearest
    /// first means your own side first; dead centre behind the truck, your own side wins the tie.</summary>
    public static int NearestFreeHold(Vector3 local, Vector3 modelScale, int takenMask)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int k = 0; k < Count; k++)
        {
            if ((takenMask & (1 << k)) != 0) continue;
            Vector3 f = Vector3.Scale(FeetMetres(k), modelScale);
            float dx = f.x - local.x, dz = f.z - local.z;
            float d = dx * dx + dz * dz + (Side(k) * local.x < 0f ? 0.05f : 0f);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    public static int NearestFreeHold(Transform truck, Vector3 world, int takenMask) =>
        truck == null ? -1 : NearestFreeHold(RealMetres(truck, world), TruckModelVisuals.ScaleFor(truck), takenMask);

    // ---- switching spots (Shift): round the back, left ladder → rear-left step → rear-right step → right ladder → again

    static readonly int[] around = { RungLeft, RearLeft, RearRight, RungRight };

    /// <summary>The next free hold after <paramref name="from"/> going round the back (taken holds skipped), or −1.</summary>
    public static int NextFreeHold(int from, int takenMask)
    {
        int at = Array.IndexOf(around, from);
        if (at < 0) return -1;
        for (int k = 1; k < Count; k++)
        {
            int hold = around[(at + k) % Count];
            if ((takenMask & (1 << hold)) == 0) return hold;
        }
        return -1;
    }

    // The way round the back a switching rider shuffles (truck metres): left ladder, outside the left rear corner, both rear
    // steps, outside the right rear corner, right ladder. The corners clear both bodies, so nobody walks through the truck.
    static Vector3[] path;
    static float[] pathS;

    static float[] PathArc()
    {
        if (pathS != null) return pathS;
        path = new[] { feet[RungLeft], new Vector3(-1.62f, 0.70f, -2.80f), feet[RearLeft], feet[RearRight],
                       new Vector3(1.62f, 0.70f, -2.80f), feet[RungRight] };
        var s = new float[path.Length];
        for (int i = 1; i < path.Length; i++) s[i] = s[i - 1] + Vector3.Distance(path[i - 1], path[i]);
        return pathS = s;
    }

    /// <summary>Length of the whole way round the back, left ladder to right ladder (truck metres).</summary>
    public static float PathLength { get { var arc = PathArc(); return arc[arc.Length - 1]; } }

    /// <summary>Where hold <paramref name="hold"/> sits on the way round the back (metres from the left ladder).</summary>
    public static float PathS(int hold) =>
        PathArc()[hold == RungLeft ? 0 : hold == RearLeft ? 2 : hold == RearRight ? 3 : 5];

    /// <summary>The truck-metre point <paramref name="s"/> metres along the way round the back.</summary>
    public static Vector3 PathPoint(float s)
    {
        var arc = PathArc();
        s = Mathf.Clamp(s, 0f, arc[arc.Length - 1]);
        for (int i = 1; i < arc.Length; i++)
            if (s <= arc[i]) return Vector3.Lerp(path[i - 1], path[i], (s - arc[i - 1]) / Mathf.Max(1e-4f, arc[i] - arc[i - 1]));
        return path[path.Length - 1];
    }

    public static Vector3 Point(Transform truck, Vector3 metres) => ShopModKit.TruckPoint(truck, metres);

    public static Pose PoseOn(Transform truck, int hold)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(truck.forward, Vector3.up);
        var rot = Quaternion.LookRotation(fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward, Vector3.up);
        return new Pose(Point(truck, FeetMetres(hold)), rot);
    }
}

/// <summary>One clock for riding: NGO server time while a session runs (every machine agrees), else game time.</summary>
public static class RideClock
{
    public static double Now
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsListening ? nm.ServerTime.Time : Time.timeAsDouble;
        }
    }
}

/// <summary>Riding events (HUD, stats, other packages). Raised on the machine that sees them.</summary>
public static class RideEvents
{
    /// <summary>A rider landed after a fling: player, horizontal distance (m), air time (s). Raised on every machine.</summary>
    public static event Action<PlayerController, float, float> Flung;
    /// <summary>The local rider snagged a bag from the step: streak count.</summary>
    public static event Action<int> Snagged;

    internal static void RaiseFlung(PlayerController p, float m, float s) { try { Flung?.Invoke(p, m, s); } catch (Exception e) { Debug.LogException(e); } }
    internal static void RaiseSnagged(int n) { try { Snagged?.Invoke(n); } catch (Exception e) { Debug.LogException(e); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Flung = null; Snagged = null; }
}

/// <summary>A rider on a truck, for other packages (F4 spotlight / stowaways, F5 dogs).</summary>
public struct RiderInfo
{
    public PlayerController player;
    public ulong clientId;
    public int hold;
    public bool rung;
    /// <summary>−1 left, +1 right.</summary>
    public int side;
    public Vector3 handPos;
    public bool isLocal;
}

/// <summary>
/// The riding API for other packages. Everything works in solo and co-op.
/// <list type="bullet">
/// <item><see cref="RidersOn"/>: who rides a truck and where (F4 spotlight: a rider on a side rung; stowaway shooing).</item>
/// <item><see cref="KnockOff"/>: throw a rider off. Call on the host (or solo); the rider's own machine flies. Nothing calls
/// it today: riders stay on solidly unless their truck crashes (yard dogs only bark at them).</item>
/// </list>
/// </summary>
public static class Riding
{
    /// <summary>Fill <paramref name="into"/> with the riders on <paramref name="truck"/> on this machine's view.</summary>
    public static int RidersOn(TruckControllerV2 truck, List<RiderInfo> into)
    {
        if (into == null) return 0;
        into.Clear();
        if (truck == null) return 0;
        foreach (var r in RideRider.All)
        {
            if (r == null || !r.TryGetRide(out var rt, out int hold) || rt == null || rt.Truck != truck) continue;
            into.Add(new RiderInfo
            {
                player = r.Player, clientId = r.ClientId, hold = hold, rung = RideHolds.IsRung(hold), side = RideHolds.Side(hold),
                handPos = RideHolds.Point(truck.transform, RideHolds.HandMetres(hold)), isLocal = r.IsLocal,
            });
        }
        return into.Count;
    }

    /// <summary>True if anyone rides this truck (this machine's view).</summary>
    public static bool AnyRiders(TruckControllerV2 truck)
    {
        foreach (var r in RideRider.All)
            if (r != null && r.TryGetRide(out var rt, out _) && rt != null && rt.Truck == truck) return true;
        return false;
    }

    /// <summary>
    /// Knock a rider off their truck: they're flung with <paramref name="push"/> added. Host or solo only; ignored for anyone
    /// not riding. <paramref name="word"/> floats over them ("WOOF!").
    /// </summary>
    public static void KnockOff(ulong clientId, Vector3 push, string word = null)
    {
        var net = HandsNet.Instance;
        if (net != null && net.Active)
        {
            if (!net.IsServer) return;
            net.HostRideKnock(clientId, push, word);
            return;
        }
        var r = RideRider.LocalRider;
        if (r != null && r.ClientId == clientId) r.ApplyKnock(push, word, HandsNet.None);
    }
}

/// <summary>Personal riding records, saved per profile (account store key <c>stats/ride</c>, §9 rule 9).</summary>
public static class RideStats
{
    [Serializable]
    class Data { public float bestAirM, bestAirS, totalAirS; public int flings, bestStreak; }

    const string Key = "stats/ride";
    static Data data;
    static bool loading;

    public static float BestAirM => Get().bestAirM;
    public static float BestAirS => Get().bestAirS;
    public static int Flings => Get().flings;
    public static int BestStreak => Get().bestStreak;

    static Data Get()
    {
        if (data != null) return data;
        data = new Data();
        if (!loading)
        {
            loading = true;
            var store = AccountService.Store;
            if (store != null)
            {
                var d = data;
                store.ReadAsync(Key).ContinueWith(t =>
                {
                    try
                    {
                        if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || !t.Result.Found || t.Result.Data == null) return;
                        var loaded = JsonUtility.FromJson<Data>(System.Text.Encoding.UTF8.GetString(t.Result.Data));
                        if (loaded == null) return;
                        lock (d)
                        {
                            d.bestAirM = Mathf.Max(d.bestAirM, loaded.bestAirM);
                            d.bestAirS = Mathf.Max(d.bestAirS, loaded.bestAirS);
                            d.totalAirS += loaded.totalAirS;
                            d.flings += loaded.flings;
                            d.bestStreak = Mathf.Max(d.bestStreak, loaded.bestStreak);
                        }
                    }
                    catch (Exception e) { Debug.LogWarning("[Ride] stats read: " + e.Message); }
                });
            }
        }
        return data;
    }

    /// <summary>Record a landed fling. True when it's a new personal best distance.</summary>
    public static bool RecordFling(float metres, float seconds)
    {
        var d = Get();
        bool best;
        lock (d)
        {
            best = metres > d.bestAirM + 0.05f;
            d.bestAirM = Mathf.Max(d.bestAirM, metres);
            d.bestAirS = Mathf.Max(d.bestAirS, seconds);
            d.totalAirS += seconds;
            d.flings++;
        }
        Save();
        return best;
    }

    public static bool RecordStreak(int n)
    {
        var d = Get();
        if (n <= d.bestStreak) return false;
        lock (d) d.bestStreak = n;
        Save();
        return true;
    }

    static void Save()
    {
        var store = AccountService.Store;
        if (store == null || data == null) return;
        string json;
        lock (data) json = JsonUtility.ToJson(data);
        try { _ = store.WriteAsync(Key, System.Text.Encoding.UTF8.GetBytes(json), -1); }
        catch (Exception e) { Debug.LogWarning("[Ride] stats write: " + e.Message); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; loading = false; }
}

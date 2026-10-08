using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Package F1 "Riding the truck" (Docs/Multiplayer.md §14.1, §4.4): numbers, hold points, the clock, events and the
// public API other packages use (F4 night shift: spotlight rider, stowaway shooing; F5 weather + dogs).

/// <summary>
/// The numbers of §4.4 / §14.1 in one place. Rule 12 (every physical verb declares its limits):
/// <list type="bullet">
/// <item><b>Fling</b> (grip hits 0): detected by the rider's own machine at the truck tick it sees; max helpless = the flight
/// (≤ 2 s) + 1 s knocked out; opt-in (you chose to ride); counter = lean (A/D) and HOLD ON (Space in the window);
/// preset toggle = the Safety Bar (no flings below 8 m/s).</item>
/// <item><b>Rider shove</b> (RMB on a neighbouring rider): attacker detects, host checks both ride the same truck within
/// 2.4 m; 1.5 s cooldown; the victim gets a HOLD ON (Space saves it) and 3 s shove immunity; max helpless = a fling.</item>
/// <item><b>Domino</b>: a flung rider heading at the neighbouring hold costs that rider 55 grip (HOLD ON saves it); each
/// rider's own machine judges it from the shared fling event.</item>
/// <item><b>Knock-off API</b> (dogs, raccoons): host decides, the rider's machine flies.</item>
/// </list>
/// </summary>
public static class RideTuning
{
    // grip (§4.4: drain = 35 × max(0, sideways g − 3.5) + 50 × max(0, braking − 5), m/s²). Tuned for Truck V2: its steering
    // narrows to ~10° at top speed, so a flat-out corner peaks near 5.3 m/s², not the doc's 10; sideways drain starts at
    // 2.5 m/s² and costs 45 a second per m/s² over, so a full-speed corner flings an unleaned rider and a 5 m/s one is free.
    public const float GripMax = 100f;
    public const float LatFree = 2.5f, LatDrain = 45f;
    public const float BrakeFree = 5f, BrakeDrain = 50f;
    public const float DriftFree = 1.2f, DriftDrain = 30f;
    /// <summary>Lean into the turn (A/D toward the centre) multiplies the drain by this; leaning the wrong way by LeanWrong.</summary>
    public const float LeanInto = 0.3f, LeanWrong = 1.4f;
    /// <summary>Grip back per second when nothing drains it (and faster when the truck is nearly stopped).</summary>
    public const float Regain = 30f, RegainStopped = 60f, StoppedSpeed = 1f;

    // jolts
    /// <summary>Jolts that cost at least this get a "HOLD ON!" window: Space in it = no loss; a miss = double loss.</summary>
    public const float PromptCost = 15f, HoldOnWindow = 0.55f, MissMult = 2f;
    /// <summary>Per physics step: vertical Δv over BumpDv is a bump/curb/pothole; horizontal Δv over ImpactDv is a hit.</summary>
    public const float BumpDv = 0.9f, BumpCostPerDv = 16f, ImpactDv = 1.1f, ImpactCostPerDv = 14f;
    /// <summary>§4.4: "any truck impact above 5 m/s flings every rider at once".</summary>
    public const float FlingAllDv = 5f;
    public const float JoltCooldown = 0.3f, MaxJoltCost = 90f;

    // fling
    public const float FlingOut = 4f, FlingUp = 4.2f, FlingKeep = 1f, Gravity = 15f, MaxAir = 2f;
    public const float FlightRadius = 0.32f, FlightDt = 1f / 60f;
    public const float KnockedOut = 1.0f;

    // riders vs riders
    public const float DominoCost = 55f, ShoveCost = 50f, ShoveRange = 2.4f, ShoveCooldown = 1.5f, ShoveImmune = 3f;

    // drive-by grabs (§4.4: curb bags 1.1–1.8 m from the truck; step reach 1.6 m + 0.4 m lean; ≤ 2.5 m/s)
    public const float SnagMaxSpeed = 2.5f, SnagReach = 1.9f, SnagLeanReach = 0.6f, StreakGap = 10f;
    public const float StreakBonusPer = 0.5f, StreakBonusCap = 3f;

    // driver's Crew Comfort tip (per stop with riders aboard)
    public const float ComfortPerRider = 0.75f, ComfortMinLeg = 25f, ComfortStopSpeed = 0.4f;

    // Safety Bar (Scrapazon): while down, nobody is flung below this truck speed
    public const float SafetyBarSpeed = 8f;
    public static float SafetyBarSpeedFor(int level) => level > 0 ? SafetyBarSpeed : 0f;
    public static bool BarSaves(int level, bool down, float speed) =>
        level > 0 && down && speed < SafetyBarSpeedFor(level);

    // stress stream (driver → everyone), §4.4 "RideStress 10 times a second"
    public const float SampleHz = 10f, StaleAfter = 0.4f;

    /// <summary>Stock lag guess before the first match (seconds the remote truck is shown behind the driver).</summary>
    public const float LagGuess = 0.12f;

    public const string LedgerStreak = "ride_streak", LedgerComfort = "crew_comfort";
}

/// <summary>The four hold points of a truck, in truck metres (Truck V2's root is scaled 0.67, like ShopModKit).</summary>
public static class RideHolds
{
    public const int Count = 4;
    public const int RearLeft = 0, RearRight = 1, RungLeft = 2, RungRight = 3;
    public const float LadderHalfWidth = 0.18f;
    public const float BoardingFacingDot = 0.65f;

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

    /// <summary>Small, intentional boarding pockets in truck metres. The hopper's centre lane and the walk
    /// toward the cab are not boarding areas. Each pocket only grants its own hold, never a distant free seat.</summary>
    public static Bounds BoardingBounds(int hold)
    {
        var f = FeetMetres(hold);
        return IsRung(hold)
            ? new Bounds(new Vector3(Side(hold) * 1.92f, .65f, f.z), new Vector3(.68f, 1.5f, .86f))
            : new Bounds(new Vector3(f.x, .65f, -3.05f), new Vector3(.68f, 1.5f, .76f));
    }

    public static bool WithinBoardingBounds(int hold, Vector3 playerMetres, float slack = 0f)
    {
        if (hold < 0 || hold >= Count) return false;
        var bounds = BoardingBounds(hold);
        bounds.Expand(Mathf.Max(0f, slack) * 2f);
        return bounds.Contains(playerMetres);
    }

    /// <summary>Face the actual mount, using the horizontal interaction view rather than requiring an avatar
    /// turn. The held-bag toss point at the middle of the hopper is deliberately not a ladder target.</summary>
    public static bool FacesBoardingHold(int hold, Vector3 playerMetres, Vector3 viewForwardMetres)
    {
        if (hold < 0 || hold >= Count) return false;
        Vector3 toward = FeetMetres(hold) - playerMetres;
        toward.y = 0f;
        viewForwardMetres.y = 0f;
        return toward.sqrMagnitude > .0001f && viewForwardMetres.sqrMagnitude > .0001f &&
            Vector3.Dot(toward.normalized, viewForwardMetres.normalized) >= BoardingFacingDot;
    }

    public static Vector3 Point(Transform truck, Vector3 metres) => ShopModKit.TruckPoint(truck, metres);

    public static Pose PoseOn(Transform truck, int hold)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(truck.forward, Vector3.up);
        var rot = Quaternion.LookRotation(fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward, Vector3.up);
        return new Pose(Point(truck, FeetMetres(hold)), rot);
    }

    /// <summary>Neighbouring holds (a flung or shoved rider can take this one with them).</summary>
    public static bool Adjacent(int a, int b) =>
        (a == RearLeft && b == RearRight) || (a == RearRight && b == RearLeft) ||
        (a == RearLeft && b == RungLeft) || (a == RungLeft && b == RearLeft) ||
        (a == RearRight && b == RungRight) || (a == RungRight && b == RearRight);
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
    /// <summary>A flung rider took a neighbour off: (cause, victim). Raised on the victim's machine.</summary>
    public static event Action<PlayerController, PlayerController> Domino;
    /// <summary>The local rider snagged a bag from the step: streak count.</summary>
    public static event Action<int> Snagged;

    internal static void RaiseFlung(PlayerController p, float m, float s) { try { Flung?.Invoke(p, m, s); } catch (Exception e) { Debug.LogException(e); } }
    internal static void RaiseDomino(PlayerController a, PlayerController b) { try { Domino?.Invoke(a, b); } catch (Exception e) { Debug.LogException(e); } }
    internal static void RaiseSnagged(int n) { try { Snagged?.Invoke(n); } catch (Exception e) { Debug.LogException(e); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Flung = null; Domino = null; Snagged = null; }
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
    /// <summary>Grip 0..100 (only known for this machine's own rider; −1 for others).</summary>
    public float grip;
    public bool isLocal;
}

/// <summary>
/// The riding API for other packages. Everything works in solo and co-op.
/// <list type="bullet">
/// <item><see cref="GripDrainScale"/>: multiply grip drain (F5 rain sets 1.5). Read on each rider's machine.</item>
/// <item><see cref="RidersOn"/>: who rides a truck and where (F4 spotlight: a rider on a side rung; stowaway shooing).</item>
/// <item><see cref="KnockOff"/> / <see cref="GripHit"/>: dogs and raccoons. Call on the host (or solo); the rider's own
/// machine applies it.</item>
/// </list>
/// </summary>
public static class Riding
{
    /// <summary>Global grip-drain multiplier (1 = normal). F5 weather: rain.</summary>
    public static float GripDrainScale = 1f;

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
                handPos = RideHolds.Point(truck.transform, RideHolds.HandMetres(hold)), grip = r.IsLocal ? r.Grip : -1f, isLocal = r.IsLocal,
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
    /// Knock a rider off their truck (a dog leaps, a raccoon bites): they're flung with <paramref name="push"/> added.
    /// Host or solo only; ignored for anyone not riding. <paramref name="word"/> floats over them ("WOOF!").
    /// </summary>
    public static void KnockOff(ulong clientId, Vector3 push, string word = null) => Hit(clientId, push, 999f, true, true, word);

    /// <summary>Take <paramref name="cost"/> grip from a rider (savable: they get a HOLD ON window first). Host or solo only.
    /// At 0 grip they're flung as usual (the Safety Bar still holds below 8 m/s).</summary>
    public static void GripHit(ulong clientId, float cost, string word = null, bool savable = true) =>
        Hit(clientId, Vector3.zero, cost, false, savable, word);

    static void Hit(ulong clientId, Vector3 push, float cost, bool forced, bool savable, string word)
    {
        var net = HandsNet.Instance;
        if (net != null && net.Active)
        {
            if (!net.IsServer) return;
            net.HostRideKnock(clientId, push, cost, forced, savable, word);
            return;
        }
        var r = RideRider.LocalRider;
        if (r != null && r.ClientId == clientId) r.ApplyHit(push, cost, forced, savable, word, HandsNet.None);
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

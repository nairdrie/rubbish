using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Houses put their trash out at random times — no daily reset (Trash system, host only, DontDestroyOnLoad).
///
/// Model: every house runs a Poisson process on in-game time. Each "put-out" event puts 1–2 bags on its curb
/// (never more than the house's curb capacity; a full curb stops putting out, but its bags keep ageing), and
/// the whole town never has more than <see cref="GlobalCap"/> bags out. The rate follows the time of day —
/// busy mornings (6–9) and evenings (17–21), with an after-dark put-out wave that can fill neglected curbs
/// before dawn — and each house has a
/// stable "messiness" (bigger houses and some slobs put out more). Bags are only data here
/// (GameSession.CurbBags); PickupStops materialize them when the Neighborhood is loaded. Time that passed
/// without a simulation step (garage, debug skips, a hitch) is caught up in small steps with back-dated
/// put-out times, so ages and lateness are always right.
///
/// DISTRICTS (Docs/Progression.md §3.3, §5.1, §5.2): each unlocked district puts out
///   bagsPerDay (35 / 30 / 24 / 20) × subscribed share (0.25 + 0.75 × Trust) bags a day, from its SUBSCRIBED houses
///   only (Progression.IsSubscribed), with its own curb cap (60 / 50 / 40 / 34) under a global cap of 200. Locked
///   districts and unsubscribed houses put out nothing. Billionaire Bluffs doesn't trickle: each subscribed estate does a
///   Butler Drop of 1–2 bags (P(2) = 0.25) at 07:00 and 17:00 (±10 min, deterministic per estate and day). Bags roll
///   with the district's LootProfile × the gnome multipliers (UpgradeEffects.ValuableChanceMult) × Rookie Luck (×4 until
///   the first treasure is kept), and the district's Trust feeds the rich bag themes.
/// MIDNIGHT (host, once per rollover): CityCleanliness.MidnightInspection → ProgressionEvents.Midnight.
///
/// TUNING (solo player, 160 kg truck, 20-min day, ~37 houses):
///   TargetBagsPerDay = 35 bags/in-game day (uncapped) ≈ 1 bag every ~34 real seconds. A rolled bag weighs
///   ~5.8 kg on average, so a day's trash is ~205 kg ≈ 1.3 truckloads. On time = 4 in-game hours = 3.3 real
///   minutes; neglected = 16 h = 13 real minutes. An efficient player (a truckload in ~7–8 min, quick
///   dump/crush) keeps the town green; one who sorts every bag by hand falls behind and piles form — a curb
///   fills at its capacity (3–6), the town at 60 bags.
///   Events: 1 bag (55 %) or 2 bags (45 %) → 1.45 bags/event → ~24 events/day town-wide.
    ///   Time-of-day weights (per hour, normalised to a mean of 1): see HourWeights (after-dark peak 3.2;
    ///   roughly half the day's bags arrive between 20:30 and 05:00, when pickups pay ×1.5 but raccoons are out).
///   A fresh server starts with <see cref="StartingBags"/> bags already out (9 for one player), put out 0.3–3 h before 7 AM.
/// </summary>
public class CurbTrashSpawner : MonoBehaviour
{
    // ------------------------------------------------------------------ tuning
    /// <summary>Starter suburb's bags a day at 100 % Trust (each district: DistrictDef.bagsPerDay).</summary>
    public const float TargetBagsPerDay = 35f;
    public const float TwoBagChance = 0.45f;
    public const float MeanBagsPerEvent = 1f + TwoBagChance;
    /// <summary>All districts together (each district also has its own DistrictDef.curbCap: 60 / 50 / 40 / 34).</summary>
    public const int GlobalCap = 200;
    /// <summary>Butler Drops (Billionaire Bluffs): hours of day, jitter (± hours) and the chance an estate puts out 2 bags.</summary>
    public static readonly float[] ButlerDropHours = { 7f, 17f };
    public const float ButlerDropJitterHours = 10f / 60f;
    public const float ButlerTwoBagChance = 0.25f;
    /// <summary>Rookie Luck: curb bags roll with the valuable chance ×4 until the first treasure is kept (§3.2).</summary>
    public const float RookieLuckMult = 4f;
    public const int StartingBags = 8;
    /// <summary>One extra opening pickup for solo; co-op opening stock and per-bag weights/loot are unchanged.</summary>
    public static int StartingBagsForPlayers(int players) => players == 1 ? StartingBags + 1 : StartingBags;
    public const float StartingMinAgeHours = 0.3f, StartingMaxAgeHours = 3f;
    public const float MaxCatchUpHours = 48f;
    /// <summary>Simulation step (in-game hours). Catch-up runs in steps this size; live play steps once per ~1 s.</summary>
    public const double StepHours = 0.02;

    /// <summary>Relative put-out rate per hour of day (normalised at runtime so the day's mean is 1).
    /// The F4 after-dark wave makes curbs pile up overnight without increasing the district's daily budget.</summary>
    static readonly float[] HourWeights =
    {
        2.80f, 2.60f, 2.40f, 2.20f, 1.80f, 1.10f,   // 0-5  overnight: bags keep piling up before dawn
        1.60f, 2.60f, 2.60f, 1.40f,                 // 6-9  morning rush
        0.70f, 0.70f, 0.80f, 0.70f, 0.60f, 0.60f, 0.80f, // 10-16 daytime
        1.80f, 2.20f, 2.20f, 1.80f,                 // 17-20 evening
        2.80f, 3.20f, 3.00f,                        // 21-23 after-dark put-out wave
    };
    static float hourWeightNorm = -1f;

    /// <summary>
    /// Co-op (Docs/Multiplayer.md §13.7): the city puts out more trash when more businesses are open: 1 → 1.0×, 2 → 1.7×,
    /// 3 → 2.3×, 4 → 2.8× (put-out rate, the global cap and each district's cap). Prices never scale.
    /// </summary>
    public static float RateScale => TownWorkDirector.Scale * EventRateMul;

    // Capacity is a stable safety ceiling, never shrunk just because crews stopped to sort.
    public static float CapacityScale => BusinessRates.PutOutScale(Businesses.OnlineCount) * EventRateMul;

    /// <summary>F5 (Docs/Multiplayer.md §14.5): the Big Cleanup doubles the put-out rate (and the caps) while it runs. Host-set; 1 = normal.</summary>
    public static float EventRateMul = 1f;

    /// <summary>The town-wide bag cap right now (<see cref="GlobalCap"/> × <see cref="RateScale"/>).</summary>
    public static int Cap => Mathf.RoundToInt(GlobalCap * CapacityScale);

    /// <summary>A district's curb cap right now (its <c>curbCap</c> × <see cref="RateScale"/>).</summary>
    public static int CurbCap(DistrictDef d) => d == null ? 0 : Mathf.RoundToInt(d.curbCap * CapacityScale);

    /// <summary>Put-out rate multiplier for an hour of day (mean 1 over a day).</summary>
    public static float TimeOfDayWeight(float hour)
    {
        if (hourWeightNorm < 0f)
        {
            float sum = 0f;
            foreach (var hw in HourWeights) sum += hw;
            hourWeightNorm = sum / 24f;
        }
        hour = Mathf.Repeat(hour, 24f);
        int h0 = Mathf.FloorToInt(hour) % 24, h1 = (h0 + 1) % 24;
        float t = hour - Mathf.Floor(hour);
        // Smooth between the hour's value (centered on the half hour) and its neighbours.
        float w0 = HourWeights[h0], wPrev = HourWeights[(h0 + 23) % 24], wNext = HourWeights[h1];
        float w = t < 0.5f ? Mathf.Lerp((wPrev + w0) * 0.5f, w0, t * 2f) : Mathf.Lerp(w0, (w0 + wNext) * 0.5f, (t - 0.5f) * 2f);
        return w / hourWeightNorm;
    }

    // ------------------------------------------------------------------ houses (static registry)

    /// <summary>One house that puts trash out (from the HouseDirectory asset or the loaded scene's PickupStops).</summary>
    public sealed class House
    {
        public string stopId;
        public string address;
        public int capacity;
        public float wealth;
        public Vector3 position;
        /// <summary>District id (Districts.*).</summary>
        public string districtId;
        /// <summary>Share of its DISTRICT's put-out events (each district's houses sum to 1).</summary>
        public float share;
        public DistrictDef District => Districts.Get(districtId);
    }

    static readonly List<House> houses = new List<House>();
    static readonly Dictionary<string, House> houseById = new Dictionary<string, House>();
    static bool directoryTried;

    public static IReadOnlyList<House> Houses => houses;
    public static House FindHouse(string stopId) => stopId != null && houseById.TryGetValue(stopId, out var h) ? h : null;

    /// <summary>
    /// The loaded Neighborhood's PickupStops are the truth: replace the registry with them and drop curb records
    /// for houses that don't exist (stale directory). Called by TrashRoute when the scene loads.
    /// </summary>
    public static void RegisterSceneHouses(IReadOnlyList<PickupStop> stops)
    {
        if (stops == null || stops.Count == 0) return;
        houses.Clear(); houseById.Clear();
        foreach (var s in stops)
        {
            if (s == null || string.IsNullOrEmpty(s.stopId) || houseById.ContainsKey(s.stopId)) continue;
            AddHouse(s.stopId, s.address, s.Capacity, s.wealth, s.transform.position, s.District.id);
        }
        Normalise();
        directoryTried = true;

        // Orphans (a house that no longer exists) would never materialize: clear them.
        var bags = GameSession.CurbBags;
        for (int i = bags.Count - 1; i >= 0; i--)
            if (bags[i] != null && !houseById.ContainsKey(bags[i].stopId)) GameSession.RemoveCurbBag(bags[i].bagId);
    }

    static void EnsureHouses()
    {
        if (houses.Count > 0 || directoryTried) return;
        directoryTried = true;
        var dir = HouseDirectory.Load();
        if (dir == null || dir.houses == null) return;
        foreach (var e in dir.houses)
        {
            if (e == null || string.IsNullOrEmpty(e.stopId) || houseById.ContainsKey(e.stopId)) continue;
            string did = !string.IsNullOrEmpty(e.districtId) ? Districts.Get(e.districtId).id : Districts.ForStopId(e.stopId).id;
            AddHouse(e.stopId, e.address, Mathf.Clamp(e.capacity, 1, PickupStop.MaxSlots), e.wealth, e.position, did);
        }
        Normalise();
    }

    static void AddHouse(string id, string address, int capacity, float wealth, Vector3 pos, string districtId)
    {
        var h = new House
        {
            stopId = id, address = address, capacity = Mathf.Clamp(capacity, 1, PickupStop.MaxSlots), wealth = Mathf.Clamp01(wealth),
            position = pos, districtId = string.IsNullOrEmpty(districtId) ? Districts.ForStopId(id).id : districtId,
        };
        houses.Add(h);
        houseById[id] = h;
    }

    /// <summary>Stable per-house messiness 0.6..1.6 (some houses are just slobs).</summary>
    public static float Messiness(string stopId)
    {
        uint h = (uint)PickupVisuals.StableHash("mess:" + stopId);
        return 0.6f + (h % 1000u) / 1000f;
    }

    static void Normalise()
    {
        // Shares are per district: a house's slice of its own district's put-outs.
        var totals = new Dictionary<string, float>();
        foreach (var h in houses)
        {
            h.share = (h.capacity / 4.5f) * Messiness(h.stopId);
            totals[h.districtId] = (totals.TryGetValue(h.districtId, out var t) ? t : 0f) + h.share;
        }
        foreach (var h in houses) if (totals.TryGetValue(h.districtId, out var t) && t > 0f) h.share /= t;
    }

    /// <summary>The house puts trash out for us right now: its district is unlocked and it subscribes (Trust).</summary>
    public static bool IsEligible(House h) => h != null && Progression.IsTownSubscribed(h.stopId);

    /// <summary>Subscribed share of a district: 0.25 + 0.75 × Trust (scales its bags a day).</summary>
    public static float SubscribedShare(DistrictDef d) =>
        d == null ? 0f : 0.25f + 0.75f * DistrictAccess.TownTrust(d);

    /// <summary>Expected bags a day for one district right now (0 when locked).</summary>
    public static float DistrictBagsPerDay(DistrictDef d) =>
        d == null || !DistrictAccess.OpenInTown(d) ? 0f : d.bagsPerDay * SubscribedShare(d) * Campaign.RateMult(d); // a contract district sets its trash out more often (Campaign)

    // ------------------------------------------------------------------ runner

    public static CurbTrashSpawner Instance { get; private set; }
    /// <summary>True while the host simulation is running (server started, or fully offline).</summary>
    public static bool IsRunning => Instance != null && Instance.running;
    /// <summary>A house put a bag out (host). Fired after the record is in GameSession.CurbBags.</summary>
    public static event Action<CurbBag> BagPutOut;
    /// <summary>Bags put out since the server started (fun stat / debug).</summary>
    public static int BagsPutOutThisSession { get; private set; }

    bool running;
    bool seeded;
    double simulatedTo = -1;
    int lastDayIndex;
    System.Random rng = new System.Random();

    static bool Authority
    {
        get
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return true;              // offline (scene played directly)
            return nm.IsListening && nm.IsServer;      // host/server only
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        bool auth = Authority;
        if (auth && !running) BeginSession();
        else if (!auth && running) running = false;
        if (!running) return;

        TownWorkDirector.Tick();

        EnsureHouses();

        // Midnight: the inspection (once per day rollover, even if a debug skip jumped past it). Only once the town is
        // known — no free inspection of a city we've never simulated.
        int day = GameClock.DayIndex;
        if (day > lastDayIndex)
        {
            lastDayIndex = day;
            if (houses.Count > 0) RunMidnight();
        }

        if (houses.Count == 0) return; // nobody known yet: time keeps "owing" and is caught up once houses register

        if (!seeded)
        {
            seeded = true;
            SeedStartingTrash();
        }

        double now = GameClock.TotalHours;
        if (now - simulatedTo > MaxCatchUpHours) simulatedTo = now - MaxCatchUpHours;
        if (now - simulatedTo < StepHours) return;
        int guard = 0;
        while (now - simulatedTo >= StepHours && guard++ < 5000)
        {
            Step(simulatedTo, StepHours);
            simulatedTo += StepHours;
        }
    }

    void BeginSession()
    {
        TownWorkDirector.BeginTown();
        GameClock.EnsureSessionClock(); // the Town's saved time of day first, so it isn't caught up as hours of trash
        running = true;
        seeded = false;
        rng = new System.Random(Environment.TickCount);
        BagsPutOutThisSession = 0;
        // A new server session restarts the clock at 7 AM: yesterday's session's records would have nonsense ages.
        var bags = GameSession.CurbBags;
        for (int i = bags.Count - 1; i >= 0; i--) GameSession.RemoveCurbBag(bags[i].bagId);
        CurbTrash.Invalidate();
        Campaign.OnSessionBegin();
        simulatedTo = GameClock.TotalHours;
        lastDayIndex = GameClock.DayIndex;
        Debug.Log($"[CurbTrashSpawner] started at {GameClock.ClockText} (starter target {TargetBagsPerDay} bags/day at 100% Trust, cap {GlobalCap}, ×{RateScale:0.0} for {Businesses.OnlineCount} business(es)).");
    }

    /// <summary>Midnight rollovers handled this session (each raised exactly one ProgressionEvents.Midnight).</summary>
    public static int MidnightsRaised { get; private set; }

    /// <summary>
    /// The midnight order (Docs/Progression.md §9.3): 1. CityCleanliness pays Clean Streets per district, neglect fines,
    /// clean streaks; 2. raise ProgressionEvents.Midnight (builder A: Trust, Rep, Clean Day, ranks). Hutch kibble is not
    /// part of this order: Hutch.HostDayTick charges it per owning business when its roster's day rolls over.
    /// </summary>
    public static MidnightReport RunMidnight()
    {
        MidnightReport report;
        try { report = CityCleanliness.MidnightInspection(); }
        catch (Exception e) { Debug.LogException(e); report = new MidnightReport { day = Mathf.Max(1, GameClock.Day - 1) }; }
        MidnightsRaised++;
        ProgressionEvents.RaiseMidnight(report);
        return report;
    }

    void SeedStartingTrash()
    {
        // The contract districts' old heaps go out first, so they get the room under the curb caps (Campaign).
        Campaign.OnSpawnerSession();
        // A fresh server starts with a few bags out in the trickle districts (scaled to what's open: 9 solo, otherwise 8).
        double now = GameClock.TotalHours;
        float perDay = 0f;
        foreach (var d in Districts.All) if (!d.butlerDrops) perDay += DistrictBagsPerDay(d);
        int initialBags = StartingBagsForPlayers(Mathf.Max(1, SessionService.PlayerCount));
        int want = Mathf.Min(GlobalCap, Mathf.RoundToInt(initialBags * Mathf.Max(1f, perDay / (TargetBagsPerDay * 0.7f))));
        int placed = 0, tries = 0;
        while (placed < want && tries++ < 200)
        {
            var h = PickHouseByShare();
            if (h == null) break;
            double at = now - Mathf.Lerp(StartingMinAgeHours, StartingMaxAgeHours, (float)rng.NextDouble());
            int n = rng.NextDouble() < TwoBagChance ? 2 : 1;
            placed += PutOut(h, at, Mathf.Min(n, want - placed));
        }
    }

    /// <summary>A trickle-district house that puts trash out for us, weighted by its district's daily bags × its share.</summary>
    House PickHouseByShare()
    {
        double total = 0;
        foreach (var h in houses) total += Weight(h);
        if (total <= 0) return null;
        double r = rng.NextDouble() * total;
        House last = null;
        foreach (var h in houses)
        {
            double w = Weight(h);
            if (w <= 0) continue;
            last = h;
            r -= w;
            if (r <= 0) return h;
        }
        return last;

        static double Weight(House h)
        {
            var d = h.District;
            if (d.butlerDrops || !IsEligible(h)) return 0;
            return h.share * d.bagsPerDay;
        }
    }

    // Per-step scratch: each district's put-out rate split over its subscribed houses.
    readonly Dictionary<string, float> eligibleShare = new Dictionary<string, float>();
    readonly List<House> eligible = new List<House>();

    void Step(double t0, double dt)
    {
        if (GameSession.CurbBags.Count >= Cap) return;
        float hour = (float)((t0 + dt * 0.5) % 24.0);

        eligible.Clear(); eligibleShare.Clear();
        for (int i = 0; i < houses.Count; i++)
        {
            var h = houses[i];
            if (!IsEligible(h)) continue;
            eligible.Add(h);
            eligibleShare[h.districtId] = (eligibleShare.TryGetValue(h.districtId, out var t) ? t : 0f) + h.share;
        }

        // Butler Drops (no trickle in those districts).
        for (int i = 0; i < eligible.Count; i++)
        {
            var h = eligible[i];
            if (!h.District.butlerDrops) continue;
            ButlerDropsIn(h, t0, dt);
            if (GameSession.CurbBags.Count >= Cap) return;
        }

        float tod = TimeOfDayWeight(hour);
        for (int i = 0; i < eligible.Count; i++)
        {
            var h = eligible[i];
            var d = h.District;
            if (d.butlerDrops) continue;
            if (CurbTrash.BagsIn(d) >= CurbCap(d)) continue;
            float sub = eligibleShare.TryGetValue(h.districtId, out var t) ? t : 0f;
            if (sub <= 0f) continue;
            // District rate = bagsPerDay × subscribed share, split over the subscribed houses by their share.
            double eventsPerHour = DistrictBagsPerDay(d) / MeanBagsPerEvent / 24.0 * tod;
            double lambda = eventsPerHour * h.share / sub * RateScale;
            if (rng.NextDouble() >= 1.0 - Math.Exp(-lambda * dt)) continue;
            int n = rng.NextDouble() < TwoBagChance ? 2 : 1;
            PutOut(h, t0 + rng.NextDouble() * dt, n);
            if (GameSession.CurbBags.Count >= Cap) return;
        }
    }

    /// <summary>Estate h's Butler Drops that fall inside [t0, t0 + dt): 1–2 bags at 07:00 and 17:00 ± 10 min.</summary>
    static void ButlerDropsIn(House h, double t0, double dt)
    {
        int day = (int)Math.Floor(t0 / 24.0);
        for (int dd = day; dd <= (int)Math.Floor((t0 + dt) / 24.0); dd++)
        {
            for (int k = 0; k < ButlerDropHours.Length; k++)
            {
                double at = ButlerDropTime(h.stopId, dd, k);
                if (at < t0 || at >= t0 + dt) continue;
                int n = ButlerDropBags(h.stopId, dd, k);
                float roll = ((uint)PickupVisuals.StableHash($"butler-budget:{h.stopId}:{dd}:{k}") % 10000u) / 10000f;
                n = TownWorkDirector.ScaledDropCount(n, RateScale, roll);
                int placed = PutOut(h, at, n);
                if (placed > 0) AnnounceButlerDrop(h, dd, k);
            }
        }
    }

    /// <summary>When estate stopId's k-th Butler Drop of day index `day` happens (GameClock.TotalHours).</summary>
    public static double ButlerDropTime(string stopId, int day, int k)
    {
        uint hsh = (uint)PickupVisuals.StableHash($"butler:{stopId}:{day}:{k}");
        double jitter = ((hsh & 0xFFFF) / 65535.0 * 2.0 - 1.0) * ButlerDropJitterHours;
        return day * 24.0 + ButlerDropHours[Mathf.Clamp(k, 0, ButlerDropHours.Length - 1)] + jitter;
    }

    /// <summary>How many bags (1 or 2) estate stopId puts out in that drop.</summary>
    public static int ButlerDropBags(string stopId, int day, int k) =>
        (((uint)PickupVisuals.StableHash($"butlerbags:{stopId}:{day}:{k}") >> 8) & 0xFFFF) / 65535f < ButlerTwoBagChance ? 2 : 1;

    static int lastButlerAnnounce = -1;

    static void AnnounceButlerDrop(House h, int day, int k)
    {
        int key = day * 4 + k;
        if (key == lastButlerAnnounce) return;
        lastButlerAnnounce = key;
        var d = h.District;
        GameHUD.Toast($"BUTLER DROP! The {CityCleanliness.ShortName(d)} estates put their bags out. On time for {d.onTimeHours:0} h: {Economy.Money(d.fee)} + {Economy.Money(d.tip)} butler tip a bag!",
                      ToastStyle.Rare, 4.5f);
        Sfx.Play("notify", null, 0.9f, 1.3f);
        Sfx.Play("sparkle", null, 0.6f, 1.1f);
    }

    /// <summary>The loot profile a curb bag in district d rolls with right now: gnomes (UpgradeEffects) × Rookie Luck.</summary>
    public static LootProfile CurbProfile(DistrictDef d)
    {
        d ??= Districts.Starter;
        var baseProf = d.loot ?? Districts.Starter.loot;
        float mult = baseProf.valuableMult > 0f ? baseProf.valuableMult : 1f;
        mult *= UpgradeEffects.ValuableChanceMult(d.tier);
        if (RookieLuckActive) mult *= RookieLuckMult;
        double golden = GameSession.GetStamp("golden_gnome_until");
        float rareToEpic = golden > GameClock.TotalHours && golden - GameClock.TotalHours <= 24.5 ? 0.10f : 0f;
        if (Mathf.Approximately(mult, baseProf.valuableMult) && rareToEpic <= 0f) return baseProf;
        return baseProf.With(mult, rareToEpic);
    }

    /// <summary>Rookie Luck (§3.2): until the first treasure is kept, curb bags roll with the valuable chance ×4.</summary>
    public static bool RookieLuckActive => GameSession.TotalTreasuresFound == 0;

    /// <summary>House puts up to n bags on its curb (capped by its capacity, its district's cap and the global cap).</summary>
    static int PutOut(House h, double atHours, int n)
    {
        int placed = 0;
        var d = h.District;
        // A shop puts out its curb bags AND fills its dumpster (Trash/Dumpsters): one more bag per put-out, and each
        // bag goes to whichever of the two has room (mostly the dumpster while both do).
        bool dumpster = DumpsterRules.HasDumpster(d.id);
        if (dumpster) n++;
        for (int k = 0; k < n; k++)
        {
            if (GameSession.CurbBags.Count >= Cap) break;
            if (CurbTrash.BagsIn(d) >= CurbCap(d)) break;
            string bagId = $"{h.stopId}/{GameSession.NewUid()}";
            int slot = CurbTrash.FreeSlot(h.stopId, h.capacity);
            if (dumpster)
            {
                int inDumpster = DumpsterRules.FreeSlot(CurbTrash.StatsFor(h.stopId).usedSlotMask);
                if (DumpsterRules.GoesInDumpster(bagId, slot >= 0, inDumpster >= 0)) slot = inDumpster;
            }
            if (slot < 0) break; // curb (and dumpster) full: no more until it's picked up
            float trust = GameSession.GetDistrictTrust(d.id, d.trustStart);
            var contents = ItemCatalog.RollBag(bagId, PickupVisuals.StableHash(bagId), h.wealth, h.address, CurbProfile(d), trust);
            if (contents == null) break;
            if (string.IsNullOrEmpty(contents.bagId)) contents.bagId = bagId;
            if (string.IsNullOrEmpty(contents.foundAt)) contents.foundAt = h.address;
            var rec = new CurbBag { bagId = contents.bagId, stopId = h.stopId, slot = slot, putOutAtHours = atHours, contents = contents };
            GameSession.AddCurbBag(rec);
            CurbTrash.Invalidate();
            BagsPutOutThisSession++;
            placed++;
            try { BagPutOut?.Invoke(rec); } catch (Exception e) { Debug.LogException(e); }
        }
        return placed;
    }

    // ------------------------------------------------------------------ debug / tools

    /// <summary>Debug: n houses (weighted like the real process) put a bag out right now.</summary>
    public static int DebugPutOut(int bags, double ageHours = 0)
    {
        EnsureHouses();
        if (houses.Count == 0) return 0;
        var r = Instance != null ? Instance.rng : new System.Random();
        // Only houses that put trash out for us (unlocked district, subscribed), weighted like the real process.
        double total = 0;
        foreach (var h in houses) if (IsEligible(h)) total += h.share * Mathf.Max(1f, h.District.bagsPerDay);
        if (total <= 0) return 0;
        int placed = 0, tries = 0;
        while (placed < bags && tries++ < bags * 20)
        {
            double x = r.NextDouble() * total; House pick = null;
            foreach (var h in houses)
            {
                if (!IsEligible(h)) continue;
                pick = h;
                x -= h.share * Mathf.Max(1f, h.District.bagsPerDay);
                if (x <= 0) break;
            }
            if (pick == null) break;
            placed += PutOut(pick, GameClock.TotalHours - Math.Max(0, ageHours), 1);
        }
        return placed;
    }

    /// <summary>Debug: make every curb bag older (tests lateness / neglected piles).</summary>
    public static void DebugAgeAll(double hours)
    {
        foreach (var b in GameSession.CurbBags) if (b != null) b.putOutAtHours -= hours;
        CurbTrash.Invalidate();
    }

    /// <summary>Debug: clear every curb.</summary>
    public static void DebugClearAll()
    {
        var bags = GameSession.CurbBags;
        for (int i = bags.Count - 1; i >= 0; i--) GameSession.RemoveCurbBag(bags[i].bagId);
        CurbTrash.Invalidate();
    }

    /// <summary>Expected bags put out over a whole day at the current tuning, all unlocked districts (ignores caps).</summary>
    public static float ExpectedBagsPerDay()
    {
        float sum = 0f;
        for (int i = 0; i < 24 * 4; i++) sum += TimeOfDayWeight(i / 4f + 0.125f);
        float perDay = 0f;
        foreach (var d in Districts.All) perDay += DistrictBagsPerDay(d);
        return perDay * sum / (24 * 4);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        BagPutOut = null;
        EventRateMul = 1f;
        BagsPutOutThisSession = 0;
        MidnightsRaised = 0;
        lastButlerAnnounce = -1;
        houses.Clear(); houseById.Clear();
        directoryTried = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("[CurbTrashSpawner]");
        DontDestroyOnLoad(go);
        go.AddComponent<CurbTrashSpawner>();
    }
}

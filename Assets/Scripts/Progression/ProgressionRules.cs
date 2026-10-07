using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Progression rules (Docs/Progression.md §4, §5.2, §7, §9.3): turns the facts other builders raise on
/// <see cref="ProgressionEvents"/> into Rep (through <see cref="Ledger"/>), Work Orders, Trust, Clean Days, promotions, the
/// Key to the City, Trash-dex entries and daily stats. Host decides (a connected guest ignores facts: Multiplayer.md
/// rule 5). Company-scoped: all state is in <see cref="GameSession"/> counters / trust / stamps.
/// </summary>
public static partial class Progression
{
    // ------------------------------------------------------------------ public events (presentation listens)

    /// <summary>The Key to the City was just granted (HUD card, letter).</summary>
    public static event Action KeyToCityGranted;
    /// <summary>A permit was bought: (district). Mirrors ProgressionEvents.DistrictUnlocked for presentation.</summary>
    public static event Action<DistrictDef> DistrictOpened;
    /// <summary>The midnight review ran: (clean day?, Rep earned at midnight, the report used).</summary>
    public static event Action<bool, float, MidnightReport> MidnightReviewed;

    // ------------------------------------------------------------------ tuning (§4.2, §5.2, §7)

    public const float CleanDayTopDistrictMin = 0.70f;   // highest-tier unlocked district ≥ 70% clean
    public const int CleanDayMinBags = 5;                // ≥ 5 bags into your truck since the last midnight
    public const float TrustGreatClean = 0.85f, TrustGoodClean = 0.65f;
    public const float TrustGreatGain = 0.15f, TrustGoodGain = 0.05f, TrustDirtyLoss = 0.10f;
    public const float KeyCleanMin = 0.90f;              // all 4 districts ≥ 90% ...
    public const int KeyNightsNeeded = 3;                // ... on 3 consecutive midnights
    public const float DirtyLetterBelow = 0.50f;         // "Mayor Bandit's office called. Twice."
    public const double GnomeHours = 24.0;

    // counter keys
    const string KeyStreakCounter = "key_city_streak";
    const string ChessWinDayCounter = "chess_win_day";
    const string CompanySeedCounter = "company_seed";
    const string LuckyGnomeStamp = "lucky_gnome_until", GoldenGnomeStamp = "golden_gnome_until";
    const string LuckyGnomeLeft = "lucky_gnome_left_x100", GoldenGnomeLeft = "golden_gnome_left_x100";

    /// <summary>Milestone counter ids (value = the day it first happened). Docs/Progression.md §7.</summary>
    public static class Milestones
    {
        public const string RankUp = "milestone_rank_up", DistrictUnlocked = "milestone_district_unlocked",
            FirstTreasure = "milestone_first_treasure", FirstRare = "milestone_first_rare",
            FirstLegendary = "milestone_first_legendary", MagnusFirstSort = "milestone_magnus_first_sort",
            KevinHired = "milestone_kevin_hired", CityClean = "milestone_city_clean", KeyToCity = "milestone_key_to_city",
            GoldenTruck = "milestone_golden_truck", MagnusPrimeDefeated = "milestone_magnus_prime_defeated",
            TrashdexComplete = "milestone_trashdex_complete";
    }

    static bool hooked;
    static int lastMidnightDay;

    /// <summary>
    /// The company's current day. In a live session the clock's day counter (it continues the saved count) may be a
    /// frame ahead of GameSession.Day at the rollover, so the larger wins; outside a session it's GameSession.Day.
    /// </summary>
    public static int Today => Mathf.Max(1, SessionLive ? Mathf.Max(GameSession.Day, GameClock.Day) : GameSession.Day);

    /// <summary>A hosted / joined game is running (the clock runs on server time).</summary>
    public static bool SessionLive
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening;
        }
    }

    /// <summary>Deterministic company seed (Work Orders). Moves into the company file with everything else.</summary>
    public static int CompanySeed
    {
        get
        {
            int s = GameSession.GetCounter(CompanySeedCounter);
            if (s == 0)
            {
                s = PickupVisuals.StableHash("Rubbish Co.") | 1;
                GameSession.SetCounter(CompanySeedCounter, s);
            }
            return s;
        }
    }

    // ------------------------------------------------------------------ boot

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBoot()
    {
        if (!Application.isPlaying) return;
        Boot();
        ProgressionRunner.Ensure();
    }

    /// <summary>Subscribe the rules to the fact events (idempotent; edit-mode tests call it too).</summary>
    public static void Boot()
    {
        Unhook();
        ProgressionEvents.BagCollected += OnBagCollected;
        ProgressionEvents.CurbCleared += OnCurbCleared;
        ProgressionEvents.ItemSorted += OnItemSorted;
        ProgressionEvents.TreasureKept += OnTreasureKept;
        ProgressionEvents.LoadCrushed += OnLoadCrushed;
        ProgressionEvents.Midnight += OnMidnight;
        ProgressionEvents.DistrictEntered += OnDistrictEntered;
        ProgressionEvents.ChessGameEnded += OnChessGameEnded;
        ProgressionEvents.NeighborIncident += OnNeighborIncident;
        GameSession.RepChanged += OnRepChanged;
        GameSession.CashChanged += DailyStats.OnCashChanged;
        Ledger.Posted += DailyStats.OnLedgerPosted;
        EbaeMarket.ItemSold += OnEbaeSold;
        hooked = true;

        DailyStats.RollTo(Today);
        WorkOrders.EnsureFor(Today);
        BackfillDex();
        CheckPromotion();
        ScrapazonAdvisor.EnsureSeenInit();
    }

    /// <summary>Undo <see cref="Boot"/> (tests).</summary>
    public static void Unhook()
    {
        ProgressionEvents.BagCollected -= OnBagCollected;
        ProgressionEvents.CurbCleared -= OnCurbCleared;
        ProgressionEvents.ItemSorted -= OnItemSorted;
        ProgressionEvents.TreasureKept -= OnTreasureKept;
        ProgressionEvents.LoadCrushed -= OnLoadCrushed;
        ProgressionEvents.Midnight -= OnMidnight;
        ProgressionEvents.DistrictEntered -= OnDistrictEntered;
        ProgressionEvents.ChessGameEnded -= OnChessGameEnded;
        ProgressionEvents.NeighborIncident -= OnNeighborIncident;
        GameSession.RepChanged -= OnRepChanged;
        GameSession.CashChanged -= DailyStats.OnCashChanged;
        Ledger.Posted -= DailyStats.OnLedgerPosted;
        EbaeMarket.ItemSold -= OnEbaeSold;
        hooked = false;
    }

    public static bool IsBooted => hooked;

    static void ResetRuleStatics()
    {
        hooked = false;
        lastMidnightDay = 0;
        KeyToCityGranted = null; DistrictOpened = null; MidnightReviewed = null;
        cleanMethodLooked = false; cleanMethod = null;
        lastDirtyLetterDay = 0; lastKevinLetterDay = 0;
        dexDefs = null;
        DailyStats.ResetStatics();
        WorkOrders.ResetStatics();
        HeadOffice.ResetStatics();
    }

    // Host decides (Multiplayer.md rule 5): a connected guest's facts are presentation only. SaveGuard.Foreign also covers the
    // moment between a guest's disconnect and their own save coming back (fixer, W2; play mode only, so a latch left over from
    // a play session never mutes edit-mode evals).
    static bool Ignore => GameSession.IsGuest || (Application.isPlaying && SaveGuard.Foreign);

    // ------------------------------------------------------------------ facts → Rep (§4.1, §9.3)

    static void OnBagCollected(BagCollectedFact f)
    {
        if (Ignore) return;
        string did = !string.IsNullOrEmpty(f.districtId) ? f.districtId : Districts.ForStopId(f.stopId).id;
        float baseRep = f.onTime ? RepValues.BagOnTime : RepValues.BagLate;
        if (f.byKevin) baseRep *= RepValues.KevinShare;
        Ledger.Rep(f.onTime ? RepIds.BagOnTime : RepIds.BagLate, baseRep, did,
                   f.byKevin || !CurbTrash.ActorFeedbackActive ? (Vector3?)null : f.pos);
        DailyStats.OnBag(f);
        WorkOrders.OnBag(f, did);
    }

    static void OnCurbCleared(string stopId, string districtId, Vector3 pos)
    {
        if (Ignore) return;
        string did = !string.IsNullOrEmpty(districtId) ? districtId : Districts.ForStopId(stopId).id;
        Ledger.Rep(RepIds.CurbClean, RepValues.CurbClean, did, CurbTrash.ActorFeedbackActive ? pos : (Vector3?)null);
        DailyStats.OnCurb();
        WorkOrders.OnCurb();
    }

    static void OnItemSorted(ItemSortedFact f)
    {
        if (Ignore) return;
        ApplyItemSorted(f, true);
    }

    /// <summary>A host-confirmed robot sort in this guest's own Depot Row lot. The host already applied Rep to the
    /// correct business; only the owning machine may update its local Work Orders, stats and milestones here.</summary>
    public static void ApplyHostRobotSort(ItemSortedFact f)
    {
        if (!GameSession.IsConnectedGuest || !BusinessSlots.Declared) return;
        ApplyItemSorted(f, false);
    }

    static void ApplyItemSorted(ItemSortedFact f, bool awardRep)
    {
        string did = !string.IsNullOrEmpty(f.districtId) ? f.districtId : Districts.ForAddress(f.item?.foundAt).id;
        if (awardRep)
        {
            if (f.correct) Ledger.Rep(RepIds.ItemSorted, RepValues.ItemSorted, did, f.pos);
            else Ledger.Rep(RepIds.Contamination, RepValues.Contamination, did, f.pos);
        }
        DailyStats.OnSorted(f.correct);
        WorkOrders.OnSorted(f.correct, f.byMagnus);
        if (f.byMagnus && f.correct) Milestone(Milestones.MagnusFirstSort);
    }

    /// <summary>Base Rep for keeping a valuable of this rarity (§4.1).</summary>
    public static float TreasureRep(Rarity r) => r switch
    {
        Rarity.Legendary => RepValues.TreasureLegendary,
        Rarity.Epic => RepValues.TreasureEpic,
        Rarity.Rare => RepValues.TreasureRare,
        _ => RepValues.TreasureUncommon,
    };

    static string TreasureRepId(Rarity r) => r switch
    {
        Rarity.Legendary => RepIds.TreasureLegendary,
        Rarity.Epic => RepIds.TreasureEpic,
        Rarity.Rare => RepIds.TreasureRare,
        _ => RepIds.TreasureUncommon,
    };

    static void OnTreasureKept(ItemInstance item, bool byMagnus)
    {
        if (Ignore || item == null) return;
        ApplyTreasureKept(item, byMagnus, true);
    }

    /// <summary>Owner-side consequences of host-confirmed robot KEEP. Host has already credited Rep.</summary>
    public static void ApplyHostRobotTreasure(ItemInstance item, bool byMagnus)
    {
        if (!GameSession.IsConnectedGuest || !BusinessSlots.Declared || item == null) return;
        ApplyTreasureKept(item, byMagnus, false);
    }

    static void ApplyTreasureKept(ItemInstance item, bool byMagnus, bool awardRep)
    {
        var def = item.Def;
        var rarity = def != null ? def.rarity : Rarity.Uncommon;
        string did = Districts.ForAddress(item.foundAt).id;
        if (awardRep) Ledger.Rep(TreasureRepId(rarity), TreasureRep(rarity), did, null);
        bool newDex = RecordDex(item.defId, Today);
        DailyStats.OnTreasure(item);
        WorkOrders.OnTreasure(rarity);
        Milestone(Milestones.FirstTreasure);
        if (rarity >= Rarity.Rare) Milestone(Milestones.FirstRare);
        if (rarity == Rarity.Legendary && Milestone(Milestones.FirstLegendary))
            HeadOffice.Send("Legendary Find", "Head Office has been informed of your Legendary find. Head Office would like it. Head Office cannot have it.");
        if (newDex) DexAnnounce(def);
    }

    static void OnLoadCrushed(int bags, float kg, int treasuresLost)
    {
        if (Ignore) return;
        DailyStats.OnCrush();
        WorkOrders.OnCrush();
    }

    /// <summary>Owner-only receipt from the host's authoritative compactor, never a guest-invented result.</summary>
    public static void ApplyHostLoadCrushed()
    {
        if (!GameSession.IsConnectedGuest || !BusinessSlots.Declared) return;
        DailyStats.OnCrush();
        WorkOrders.OnCrush();
    }

    static void OnChessGameEnded(bool humanWon, int plies, int tier)
    {
        if (Ignore) return;
        ApplyChessGameEnded(humanWon, plies, tier);
    }

    /// <summary>The host-confirmed result of a chess game played by this connected guest. Chess Rep is a personal
    /// once-per-day award, so the owner's persisted counter, Work Orders and milestone are updated together.</summary>
    public static void ApplyHostChessResult(bool humanWon, int plies, int tier)
    {
        if (!GameSession.IsConnectedGuest || !BusinessSlots.Declared) return;
        ApplyChessGameEnded(humanWon, plies, tier);
    }

    static void ApplyChessGameEnded(bool humanWon, int plies, int tier)
    {
        if (humanWon && GameSession.GetCounter(ChessWinDayCounter) != Today)
        {
            GameSession.SetCounter(ChessWinDayCounter, Today);
            Ledger.Rep(RepIds.ChessWin, RepValues.ChessWin * Mathf.Max(1, tier));
        }
        if (humanWon && tier >= 11 && Milestone(Milestones.MagnusPrimeDefeated))
        {
            GameHUD.Toast("You beat MAGNUS PRIME! Title earned: Grandmaster of Garbage", ToastStyle.Legendary, 4f);
            HeadOffice.Send("Grandmaster of Garbage", "You defeated MAGNUS PRIME. He is sulking in the rail siding. Please let him win once. Not today, but once.");
        }
        WorkOrders.OnChess(humanWon, plies);
    }

    static void OnDistrictEntered(string districtId)
    {
        if (Ignore || string.IsNullOrEmpty(districtId)) return;
        string key = "visited_" + districtId;
        if (GameSession.GetCounter(key) != 0) return;
        GameSession.SetCounter(key, Today);
        var d = Districts.Get(districtId);
        if (d != null && d.tier > 1)
            HeadOffice.Send($"Welcome to {d.name}",
                $"Residents here pay {Economy.Money(d.fee)} a bag" + (d.tip > 0f ? $" plus a {Economy.Money(d.tip)} tip" : "") +
                $" if you're on time ({d.onTimeHours:0.#} h). Rep counts ×{d.repMult:0.#}. Trust starts low. Keep it clean and more houses sign up.");
    }

    // WorldLife counters (Docs/WorldLife.md §9.1): one int per key, (day << 10) | count, so the save never grows per day.
    const string NoiseCounterPrefix = "wl_rep_noise:", NeighborlyCounter = "wl_rep_neighborly";

    /// <summary>
    /// WorldLife neighbour incidents → Rep (host; §9.1). Flattened −4, car shunt −3, dog near miss / thrown flop −1, noise
    /// complaint −1 (at most 3 per night per district; the night runs 12:00 → 12:00 so 22:00–06:00 is one night), hand-over
    /// +1 (at most 10 per in-game day). × the district's Rep multiplier (Ledger.Rep); never escalated. Fines are WorldLife's
    /// (Ledger.Charge), not here.
    /// </summary>
    static void OnNeighborIncident(NeighborIncidentFact f)
    {
        if (Ignore) return;
        string did = !string.IsNullOrEmpty(f.districtId) ? f.districtId : Districts.ForStopId(f.stopId).id;
        Vector3? at = f.pos == Vector3.zero ? (Vector3?)null : f.pos;
        switch (f.kind)
        {
            case NeighborIncidentKind.Flattened:
                Ledger.Rep(RepIds.NeighborIncident, RepValues.NeighborFlattened, did, at);
                break;
            case NeighborIncidentKind.CarShunt:
                Ledger.Rep(RepIds.NeighborIncident, RepValues.NeighborCarShunt, did, at);
                break;
            case NeighborIncidentKind.DogNearMiss:
            case NeighborIncidentKind.ThrownFlop:
                Ledger.Rep(RepIds.NeighborIncident, RepValues.NeighborMinor, did, at);
                break;
            case NeighborIncidentKind.NoiseComplaint:
            {
                int night = (int)Math.Floor((GameClock.TotalHours - 12.0) / 24.0);
                if (BumpDaily(NoiseCounterPrefix + did, night, RepValues.NoiseComplaintsPerNight))
                    Ledger.Rep(RepIds.NoiseComplaint, RepValues.NoiseComplaint, did, at);
                break;
            }
            case NeighborIncidentKind.Neighborly:
                if (BumpDaily(NeighborlyCounter, GameClock.DayIndex, RepValues.NeighborlyPerDay))
                    Ledger.Rep(RepIds.Neighborly, RepValues.Neighborly, did, at);
                break;
        }
    }

    /// <summary>Count one more for <paramref name="day"/> under a packed (day, count) counter; false once the cap is reached.</summary>
    static bool BumpDaily(string key, int day, int cap)
    {
        int v = GameSession.GetCounter(key);
        int n = v != 0 && (v >> 10) == day ? v & 1023 : 0;
        if (n >= cap) return false;
        GameSession.SetCounter(key, (day << 10) | (n + 1));
        return true;
    }

    static void OnEbaeSold(EbaeSale sale)
    {
        if (Ignore || sale == null) return;
        WorkOrders.OnEbaeSale(sale);
    }

    static void OnRepChanged(float delta, string repId, string districtId, Vector3? pos)
    {
        if (Ignore) return; // a guest's local Rep is thrown away when their own save reloads: no stats, no promotion letters
        DailyStats.OnRep(delta);
        if (delta > 0f) CheckPromotion();
    }

    static void OnPromoted(int oldRank, int newRank)
    {
        Milestone(Milestones.RankUp);
        HeadOffice.Send($"Promotion: {RankTitle(newRank)}", HeadOfficeLine(newRank), popup: false);
        if (newRank >= MaxRank) TryGrantKeyToCity();
    }

    // ------------------------------------------------------------------ midnight (§9.3 step 4)

    /// <summary>
    /// The midnight review (host): Clean Day, clean-streets / neglect / shift / trophy Rep, Trust, the Key to the City
    /// streak, letters, midnight Work Orders, the promotion check, then the day roll (stats + new Work Orders).
    /// Runs once per day; a missing report (D's inspection not running) is synthesized by the runner's fallback.
    /// </summary>
    public static void OnMidnight(MidnightReport r)
    {
        if (Ignore || r == null) return;
        int today = Today;
        if (lastMidnightDay == today) return;
        lastMidnightDay = today;

        float repBefore = GameSession.Rep;
        var top = Districts.Top;
        int bags = Mathf.Max(r.bagsIntoTruckToday, DailyStats.Today.bags);

        // 1. Clean Day first, so a promotion that waited on it can land in step 7 (§4.2).
        bool cleanDay = Clean(r, top) >= CleanDayTopDistrictMin && bags >= CleanDayMinBags;
        if (cleanDay) GameSession.AddCleanDay();

        // 2. Rep (§4.1)
        foreach (var d in Districts.Unlocked)
        {
            if (!r.clean01.TryGetValue(d.id, out float c)) continue;
            c = Mathf.Clamp01(c);
            Ledger.Rep(RepIds.CleanStreets, RepValues.CleanStreets * c * c, d.id);
        }
        foreach (var kv in r.neglectedCurbs)
            if (kv.Value > 0) Ledger.Rep(RepIds.NeglectedCurb, RepValues.NeglectedCurb * kv.Value, kv.Key);
        if (bags >= RepValues.ShiftMinBags) Ledger.RepTop(RepIds.Shift, RepValues.Shift);
        float trophy = UpgradeEffects.TrophyRepPerDay;
        if (trophy > 0f) Ledger.Rep(RepIds.TrophyShelf, trophy);

        // 3. Trust (§5.2) and what the Career page shows as "last midnight"
        foreach (var d in Districts.Unlocked)
        {
            if (!r.clean01.TryGetValue(d.id, out float c)) continue;
            float t = Trust01(d);
            t += c >= TrustGreatClean ? TrustGreatGain : c >= TrustGoodClean ? TrustGoodGain : -TrustDirtyLoss;
            GameSession.SetDistrictTrust(d.id, Mathf.Clamp01(t));
            GameSession.SetCounter("clean_last_" + d.id, Mathf.RoundToInt(Mathf.Clamp01(c) * 1000f) + 1);
        }

        // 4. Key to the City streak (§7.1)
        bool allSparkling = true;
        foreach (var d in Districts.All)
            if (!Districts.IsUnlocked(d) || Clean(r, d) < KeyCleanMin) { allSparkling = false; break; }
        GameSession.SetCounter(KeyStreakCounter, allSparkling ? GameSession.GetCounter(KeyStreakCounter) + 1 : 0);
        if (allSparkling) Milestone(Milestones.CityClean);

        // 5. Head Office letters: dirty districts + the Kevin nudge (§4.6, §2.2)
        DirtyLetters(r);

        // 6. midnight Work Orders (clean district, no crush) for the day that just ended
        WorkOrders.OnMidnight(r);

        // 7. promotion, the Key
        CheckPromotion();
        TryGrantKeyToCity();

        // 8. new day: stats roll, fresh Work Orders
        DailyStats.RollTo(today);
        WorkOrders.EnsureFor(today);
        TickGnomes();

        float earned = GameSession.Rep - repBefore;
        try { MidnightReviewed?.Invoke(cleanDay, earned, r); } catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>True once the midnight review has run for <see cref="Today"/> (this session).</summary>
    public static bool MidnightDoneToday => lastMidnightDay == Today;

    /// <summary>
    /// A stand-in report when D's MidnightInspection didn't raise one (city-wide cleanliness for every unlocked
    /// district, bags from today's stats). The runner calls this a few seconds after the day rolls over.
    /// </summary>
    public static MidnightReport SynthesizeMidnight()
    {
        var r = new MidnightReport { day = Mathf.Max(1, Today - 1), bagsIntoTruckToday = DailyStats.Today.bags };
        float city = CityCleanliness.Value01;
        foreach (var d in Districts.Unlocked) r.clean01[d.id] = city;
        return r;
    }

    static float Clean(MidnightReport r, DistrictDef d)
    {
        if (d == null) return 0f;
        if (r.clean01.TryGetValue(d.id, out float c)) return Mathf.Clamp01(c);
        return d == Districts.Starter ? CityCleanliness.Value01 : 0f;
    }

    static int lastDirtyLetterDay, lastKevinLetterDay;

    static void DirtyLetters(MidnightReport r)
    {
        DistrictDef dirtiest = null;
        float worst = 1f;
        bool nudge = false;
        foreach (var d in Districts.Unlocked)
        {
            string key = "dirty_nights_" + d.id;
            if (!r.clean01.TryGetValue(d.id, out float c)) continue;
            int n = c < DirtyLetterBelow ? GameSession.GetCounter(key) + 1 : 0;
            GameSession.SetCounter(key, n);
            if (c < DirtyLetterBelow && c < worst) { worst = c; dirtiest = d; }
            if (n >= 2) nudge = true;
        }
        int today = Today;
        if (dirtiest != null && lastDirtyLetterDay != today)
        {
            lastDirtyLetterDay = today;
            HeadOffice.Send($"Complaint: {dirtiest.name}",
                $"Mayor Bandit's office called. Twice. {dirtiest.name} was {Mathf.RoundToInt(worst * 100f)}% clean at midnight. Fewer houses will trust us tomorrow.");
        }
        var kevin = ScrapazonCatalog.Get(ShopIds.HireKevin);
        if (nudge && kevin != null && lastKevinLetterDay != today && !ScrapazonStore.IsMaxed(kevin)
            && !ScrapazonStore.IsLocked(kevin) && ScrapazonStore.CanAfford(kevin))
        {
            lastKevinLetterDay = today;
            HeadOffice.Send("Have you considered Kevin?",
                $"A district has been filthy two nights running. Kevin is available. Kevin is always available. {ScrapazonStore.ShortName(kevin)}: {Economy.Money(ScrapazonStore.NextPrice(kevin))} on {Scrapazon.Name}.");
        }
    }

    // ------------------------------------------------------------------ Key to the City (§7.1)

    /// <summary>Consecutive midnights with all 4 districts ≥ 90% clean.</summary>
    public static int KeyStreak => GameSession.GetCounter(KeyStreakCounter);

    static void TryGrantKeyToCity()
    {
        if (GameSession.KeyToCity || Rank < MaxRank || KeyStreak < KeyNightsNeeded) return;
        GameSession.GrantKeyToCity();
        Milestone(Milestones.KeyToCity);
        HeadOffice.Send("The Key to the City",
            "Mayor Bandit handed over a giant key and then took off his face. He was a raccoon in a suit the whole time. That explains the budget. The Golden Garbage Truck is now available on " + Scrapazon.Name + ".");
        try { KeyToCityGranted?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ purchases (called by ScrapazonStore.TryBuy)

    /// <summary>Progression side effects of a purchase: permits open districts, gnomes start, Kevin / Golden Truck milestones.</summary>
    public static void OnPurchased(ScrapazonItem it, int now)
    {
        if (it == null) return;
        foreach (var d in Districts.All)
            if (!string.IsNullOrEmpty(d.permitId) && d.permitId == it.id) { OpenDistrict(d); return; }
        switch (it.id)
        {
            case ShopIds.LuckyGnome:
            case ShopIds.GoldenGnome:
                TickGnomes();
                break;
            case ShopIds.HireKevin:
                if (Milestone(Milestones.KevinHired))
                    HeadOffice.Send("Kevin has been hired", "Kevin starts today. Kevin has already lost a shoe. He sweeps his houses at 8 AM, 2 PM and 8 PM, and his bags show up on the tipping floor.");
                break;
            case ShopIds.GoldenTruck:
                if (Milestone(Milestones.GoldenTruck))
                    HeadOffice.Send("The Golden Garbage Truck", "24 karats. Street legal-ish. The curbs will cheer for you. Head Office would like a ride. Head Office will not get a ride.");
                break;
        }
    }

    /// <summary>Unlock a district (permit level 1, Trust at its start value, events). Used by permits and the debug menu.</summary>
    public static void UnlockDistrict(DistrictDef d)
    {
        if (d == null || string.IsNullOrEmpty(d.permitId)) return;
        if (GameSession.GetUpgradeLevel(d.permitId) <= 0) GameSession.SetUpgradeLevel(d.permitId, 1);
        OpenDistrict(d);
    }

    static void OpenDistrict(DistrictDef d)
    {
        // Trust starts at the district's start value when its permit is bought (§5.2); a re-run keeps earned Trust.
        if (GameSession.GetDistrictTrust(d.id, -1f) < 0f) GameSession.SetDistrictTrust(d.id, d.trustStart);
        if (Milestone("district_open_" + d.id))
        {
            Milestone(Milestones.DistrictUnlocked);
            HeadOffice.Send($"Permit approved: {d.name}",
                $"Your collection permit for {d.name} is approved. Drive through the gate to cut the ribbon. {Subscribers(d)} of {Houses(d)} houses have signed up so far.");
        }
        ProgressionEvents.RaiseDistrictUnlocked(d.id);
        try { DistrictOpened?.Invoke(d); } catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ gnomes (§6.6)

    /// <summary>Hours left on a gnome (0 when inactive). id = ShopIds.LuckyGnome / GoldenGnome.</summary>
    public static float GnomeHoursLeft(string id)
    {
        double until = GameSession.GetStamp(id == ShopIds.GoldenGnome ? GoldenGnomeStamp : LuckyGnomeStamp);
        if (until < 0) return 0f;
        double left = until - GameClock.TotalHours;
        return left > 0 && left <= GnomeHours + 0.01 ? (float)left : 0f;
    }

    /// <summary>
    /// Keeps gnome timers honest: activates a pocketed gnome when none is running (buying one starts it at once; spares
    /// wait their turn), and survives a server restart (the clock restarts each session, so remaining hours are kept in
    /// a counter and the stamp is rebased). Called by the runner every couple of seconds and after purchases.
    /// </summary>
    public static void TickGnomes()
    {
        TickGnome(ShopIds.LuckyGnome, LuckyGnomeStamp, LuckyGnomeLeft, "Lucky Gnome");
        TickGnome(ShopIds.GoldenGnome, GoldenGnomeStamp, GoldenGnomeLeft, "GOLDEN Gnome");
    }

    static void TickGnome(string id, string stampKey, string leftKey, string label)
    {
        double now = GameClock.TotalHours;
        double until = GameSession.GetStamp(stampKey);
        int saved = GameSession.GetCounter(leftKey);
        double left = until < 0 ? -1 : until - now;

        if (until >= 0 && left > GnomeHours + 0.01)
        {
            // stamp from a previous server session: rebase from the remembered hours left
            GameSession.SetStamp(stampKey, saved > 0 ? now + saved / 100.0 : -1);
            return;
        }
        if (left > 0)
        {
            int l = Mathf.RoundToInt((float)left * 100f);
            if (Mathf.Abs(l - saved) >= 25 || saved == 0) GameSession.SetCounter(leftKey, l);
            return;
        }
        if (saved != 0) GameSession.SetCounter(leftKey, 0);
        if (GameSession.GetConsumable(id) <= 0) return;
        GameSession.AddConsumable(id, -1);
        GameSession.SetStamp(stampKey, now + GnomeHours);
        GameSession.SetCounter(leftKey, Mathf.RoundToInt((float)GnomeHours * 100f));
        GameHUD.Toast($"{label} is on duty for 24 h. Treasure odds up!", ToastStyle.Rare, 3f);
        Sfx.Play("sparkle", null, 0.8f, 1.1f);
    }

    // ------------------------------------------------------------------ Trash-dex (§7.4)

    static List<ItemDef> dexDefs;

    /// <summary>Every valuable the Trash-dex can hold (66 once D's district items land).</summary>
    public static IReadOnlyList<ItemDef> DexDefs
    {
        get
        {
            if (dexDefs == null || dexDefs.Count == 0) dexDefs = ItemCatalog.InCategory(ItemCategory.Valuable);
            return dexDefs;
        }
    }

    public static int DexTotal => DexDefs.Count;

    /// <summary>Day a valuable was first kept, or 0 when it hasn't been found yet.</summary>
    public static int DexDay(string defId) => string.IsNullOrEmpty(defId) ? 0 : GameSession.GetCounter("dex_" + defId);

    public static int DexFound
    {
        get
        {
            int n = 0;
            foreach (var d in DexDefs) if (DexDay(d.id) > 0) n++;
            return n;
        }
    }

    /// <summary>Record a find; true when it's a new Trash-dex entry.</summary>
    static bool RecordDex(string defId, int day)
    {
        if (string.IsNullOrEmpty(defId) || DexDay(defId) > 0) return false;
        var def = ItemCatalog.Get(defId);
        if (def == null || def.category != ItemCategory.Valuable) return false;
        GameSession.SetCounter("dex_" + defId, Mathf.Max(1, day));
        if (DexFound >= DexTotal && DexTotal > 0 && Milestone(Milestones.TrashdexComplete))
            HeadOffice.Send("Trash-dex complete", "Every valuable in the city, found. Title earned: Curator. Head Office is building a museum. Out of your trash. Again.");
        return true;
    }

    static void DexAnnounce(ItemDef def)
    {
        if (def == null) return;
        GameHUD.Toast($"New Trash-dex entry: {def.name}! ({DexFound}/{DexTotal})", ToastStyle.Rare, 2.6f);
    }

    /// <summary>Old saves: anything already in the inventory or sold on ebae counts as found (day found if known).</summary>
    public static void BackfillDex()
    {
        try
        {
            foreach (var it in GameSession.Inventory) if (it != null) RecordDex(it.defId, it.dayFound > 0 ? it.dayFound : Today);
            foreach (var l in EbaeMarket.Listings) if (l?.item != null) RecordDex(l.item.defId, l.item.dayFound > 0 ? l.item.dayFound : Today);
            foreach (var s in EbaeMarket.History) if (s?.item != null) RecordDex(s.item.defId, s.item.dayFound > 0 ? s.item.dayFound : s.day);
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ milestones

    /// <summary>Set a milestone counter to today if unset. True the first time.</summary>
    public static bool Milestone(string id)
    {
        if (GameSession.GetCounter(id) != 0) return false;
        GameSession.SetCounter(id, Today);
        return true;
    }

    // ------------------------------------------------------------------ district cleanliness for the Career page

    static bool cleanMethodLooked;
    static MethodInfo cleanMethod;

    /// <summary>
    /// How clean a district is right now (0..1). Uses D's per-district <c>CityCleanliness.District01(id)</c> when it
    /// exists (looked up by name, so A never breaks the build while D's version lands), else the city-wide value for
    /// the starter district, else last midnight's value. <paramref name="live"/> = false for last-midnight values.
    /// </summary>
    public static float CleanNow01(DistrictDef d, out bool live)
    {
        live = false;
        if (d == null) return 0f;
        try
        {
            if (!cleanMethodLooked)
            {
                cleanMethodLooked = true;
                var m = typeof(CityCleanliness).GetMethod("District01", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m != null && m.ReturnType == typeof(float)) cleanMethod = m;
            }
            if (cleanMethod != null)
            {
                live = true;
                return Mathf.Clamp01((float)cleanMethod.Invoke(null, new object[] { d.id }));
            }
        }
        catch (Exception) { cleanMethod = null; }
        if (d == Districts.Starter) { live = true; return CityCleanliness.Value01; }
        int last = GameSession.GetCounter("clean_last_" + d.id);
        return last > 0 ? (last - 1) / 1000f : 0f;
    }

    // ------------------------------------------------------------------ debug (Editor/Setup_Progression menu)

    /// <summary>Raw Rep (×1), then the promotion check.</summary>
    public static void DebugGrantRep(float rep)
    {
        GameSession.AddRep(rep, "debug");
        CheckPromotion();
    }

    /// <summary>Jump to a rank: tops up Rep and Clean Days to its thresholds (may also lower the stored rank).</summary>
    public static void DebugSetRank(int rank)
    {
        rank = Mathf.Clamp(rank, 1, MaxRank);
        // Clean Days first: the Rep top-up fires RepChanged -> CheckPromotion, so this order gives ONE promotion straight
        // to the target rank (Rep first used to promote 1 -> 3, then 3 -> 4: two PROMOTED cards).
        while (GameSession.CleanDays < RankCleanDays(rank)) GameSession.AddCleanDay();
        float needRep = RankRep(rank) - GameSession.Rep;
        if (needRep > 0f) GameSession.AddRep(needRep, "debug");
        if (rank < Rank) GameSession.SetCounter(RankReachedCounter, rank);
        CheckPromotion();
    }

    public static void DebugGrantCleanDays(int n)
    {
        for (int i = 0; i < n; i++) GameSession.AddCleanDay();
        CheckPromotion();
    }

    public static void DebugUnlockDistrict(string districtId) => UnlockDistrict(Districts.Get(districtId));

    public static void DebugAddCash(float amount) => Ledger.Credit(amount, "debug cash", "debug");

    /// <summary>Jump the clock to just past midnight (D's inspection, or the fallback, runs the review).</summary>
    public static void DebugSkipToMidnight() => GameClock.DebugSkipHours(24f - GameClock.Hours + 0.02f);

    public static void DebugGrantKeyToCity()
    {
        if (GameSession.KeyToCity) return;
        GameSession.SetCounter(KeyStreakCounter, Mathf.Max(KeyStreak, KeyNightsNeeded));
        if (Rank < MaxRank) DebugSetRank(MaxRank);
        TryGrantKeyToCity();
    }
}

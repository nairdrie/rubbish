using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Business Rank, Trust/subscribers and Scrapazon gating (Docs/Progression.md §4, §5.2). Company-scoped: every bit of
/// state lives in <see cref="GameSession"/> (counters, trust, Rep), so it can move into a CompanyState / save slot later.
/// STEP 0 CONTRACT (builder A): <see cref="Rank"/>, <see cref="Next"/>, <see cref="HasRank"/>, <see cref="RankTitle"/>,
/// <see cref="CanBuy"/>, <see cref="IsSubscribed"/>, <see cref="Subscribers"/>.
/// The fact → Rep rules, midnight, Trust, Clean Days, Key to the City, gnomes, the Trash-dex and the debug API live in
/// ProgressionRules.cs; Work Orders in WorkOrders.cs; daily stats in DailyStats.cs; letters in HeadOffice.cs.
/// </summary>
public static partial class Progression
{
    /// <summary>GameSession counter holding the highest rank ever reached (rank never drops).</summary>
    const string RankReachedCounter = "rank_reached";

    public const int MaxRank = 12;

    struct RankRow
    {
        public string id, title, headOffice;
        public float rep;
        public int cleanDays;
        public RankRow(string id, string title, float rep, int cleanDays, string headOffice)
        {
            this.id = id; this.title = title; this.rep = rep; this.cleanDays = cleanDays; this.headOffice = headOffice;
        }
    }

    // §4.3: a promotion needs Rep ≥ threshold AND Clean Days ≥ minimum. §4.4: one deadpan Head Office line per rank.
    static readonly RankRow[] ranks =
    {
        new RankRow("rank_trash_intern", "Trash Intern (Unpaid)", 0f, 0,
            "Welcome to Rubbish Co. Your desk is the truck. Your chair is also the truck."),
        new RankRow("rank_bag_wrangler", "Bag Wrangler", 450f, 0,
            "Congratulations. You may now touch the bags with both hands."),
        new RankRow("rank_curbside_associate", "Curbside Associate", 950f, 0,
            "Your business cards are on order. They say 'Curbside Associat'. We ran out of E."),
        new RankRow("rank_certified_bin_technician", "Certified Bin Technician", 1800f, 2,
            "Please stop telling residents you are a doctor."),
        new RankRow("rank_senior_sack_specialist", "Senior Sack Specialist", 3250f, 3,
            "Your salary is unchanged. Please stop feeding the raccoons."),
        new RankRow("rank_hopper_whisperer", "Hopper Whisperer", 4900f, 4,
            "The truck has asked HR about you. HR is a raccoon."),
        new RankRow("rank_regional_rubbish_manager", "Regional Rubbish Manager", 6650f, 6,
            "You now manage a region. The region is mostly hedges."),
        new RankRow("rank_garbage_guru", "Garbage Guru", 9600f, 8,
            "Staff report you meditating in the compactor. Please don't."),
        new RankRow("rank_duke_of_dumpsters", "Duke of Dumpsters", 13100f, 9,
            "Your title is legally binding in zero countries."),
        new RankRow("rank_trash_tycoon", "Trash Tycoon", 17400f, 12,
            "The billionaires have asked for you by name. They got it wrong."),
        new RankRow("rank_baron_of_bins", "Baron of Bins", 27550f, 15,
            "Magnus has requested a raise on your behalf. Denied."),
        new RankRow("rank_supreme_garbage_overlord", "Supreme Garbage Overlord", 43900f, 20,
            "There are no more promotions. We checked. We're a little scared of you."),
    };

    // ------------------------------------------------------------------ rank

    /// <summary>Current Business Rank, 1..12 (the highest reached; never drops).</summary>
    public static int Rank => Mathf.Clamp(GameSession.GetCounter(RankReachedCounter), 1, ranks.Length);

    /// <summary>The next rank number (Rank + 1), or 0 when already Supreme Garbage Overlord.</summary>
    public static int Next => Rank >= ranks.Length ? 0 : Rank + 1;

    public static bool HasRank(int rank) => Rank >= rank;

    /// <summary>"Hopper Whisperer" for 6; "???" outside 1..12.</summary>
    public static string RankTitle(int rank) => rank >= 1 && rank <= ranks.Length ? ranks[rank - 1].title : "???";

    /// <summary>"rank_hopper_whisperer" for 6; null outside 1..12.</summary>
    public static string RankId(int rank) => rank >= 1 && rank <= ranks.Length ? ranks[rank - 1].id : null;

    /// <summary>Rep threshold of a rank (0 for rank 1; +∞ above 12).</summary>
    public static float RankRep(int rank) =>
        rank <= 1 ? 0f : rank > ranks.Length ? float.PositiveInfinity : ranks[rank - 1].rep;

    /// <summary>Clean Days a rank needs (§4.2).</summary>
    public static int RankCleanDays(int rank) =>
        rank <= 1 ? 0 : rank > ranks.Length ? int.MaxValue : ranks[rank - 1].cleanDays;

    /// <summary>The deadpan Head Office line for reaching a rank (§4.4).</summary>
    public static string HeadOfficeLine(int rank) => rank >= 1 && rank <= ranks.Length ? ranks[rank - 1].headOffice : "";

    /// <summary>Highest rank whose Rep and Clean Day requirements are both met (thresholds rise with rank).</summary>
    public static int RankEarned(float rep, int cleanDays)
    {
        int r = 1;
        for (int i = 2; i <= ranks.Length; i++)
        {
            if (rep + 0.0001f < ranks[i - 1].rep || cleanDays < ranks[i - 1].cleanDays) break;
            r = i;
        }
        return r;
    }

    /// <summary>0..1 Rep progress from the current rank's threshold to the next one (1 at rank 12).</summary>
    public static float RankProgress01
    {
        get
        {
            int n = Next;
            if (n == 0) return 1f;
            float a = RankRep(Rank), b = RankRep(n);
            return b <= a ? 1f : Mathf.Clamp01((GameSession.Rep - a) / (b - a));
        }
    }

    /// <summary>Rep still missing for the next rank (0 when met or at rank 12).</summary>
    public static float RepToNext => Next == 0 ? 0f : Mathf.Max(0f, RankRep(Next) - GameSession.Rep);

    /// <summary>Clean Days still missing for the next rank.</summary>
    public static int CleanDaysToNext => Next == 0 ? 0 : Mathf.Max(0, RankCleanDays(Next) - GameSession.CleanDays);

    /// <summary>Enough Rep for the next rank, but not enough Clean Days: the promotion waits for a clean midnight.</summary>
    public static bool WaitingOnCleanDays => Next != 0 && RepToNext <= 0f && CleanDaysToNext > 0;

    /// <summary>
    /// Promote to the highest rank now earned (checked on every Rep change and at every midnight; idempotent). Rank
    /// never drops. Raises <see cref="ProgressionEvents.RankChanged"/> once per check with (old, new). True if promoted.
    /// </summary>
    public static bool CheckPromotion()
    {
        int old = Rank;
        int earned = RankEarned(GameSession.Rep, GameSession.CleanDays);
        if (earned <= old) return false;
        GameSession.SetCounter(RankReachedCounter, earned);
        OnPromoted(old, earned);
        ProgressionEvents.RaiseRankChanged(old, earned);
        return true;
    }

    // ------------------------------------------------------------------ what each rank unlocks

    /// <summary>Every (item, level) whose rank gate is exactly <paramref name="rank"/> (the §4.3 "Unlocks" column).</summary>
    public static List<(ScrapazonItem item, int level)> UnlocksAtRank(int rank)
    {
        var list = new List<(ScrapazonItem, int)>();
        foreach (var it in ScrapazonCatalog.All)
        {
            if (it.IsTeaser || it.rankRequired == null) continue;
            int levels = it.IsUpgrade ? it.maxLevel : 1;
            for (int lv = 1; lv <= levels && lv <= it.rankRequired.Length; lv++)
                if (it.rankRequired[lv - 1] == rank) list.Add((it, lv));
        }
        return list;
    }

    /// <summary>How many (item, level) pairs unlock in ranks (from, to].</summary>
    public static int UnlockCountBetween(int fromExclusive, int toInclusive)
    {
        int n = 0;
        for (int r = fromExclusive + 1; r <= toInclusive; r++) n += UnlocksAtRank(r).Count;
        return n;
    }

    // ------------------------------------------------------------------ Scrapazon gating

    /// <summary>
    /// Rank and prerequisite gate for buying <paramref name="level"/> (1-based: the level you'd own after buying) of
    /// <paramref name="item"/>. Cash is NOT checked here. whyNot: "Requires Rank 6: Hopper Whisperer",
    /// "Requires Grabby Arm Lv 1", "Requires the Key to the City". Items without rank/prereq data are never locked.
    /// </summary>
    public static bool CanBuy(ScrapazonItem item, int level, out string whyNot)
    {
        whyNot = null;
        if (item == null) { whyNot = "that product doesn't exist (yet)"; return false; }
        int i = level - 1;
        if (i < 0) return true;

        if (item.rankRequired != null && i < item.rankRequired.Length)
        {
            int need = item.rankRequired[i];
            if (need > Rank) { whyNot = $"Requires Rank {need}: {RankTitle(need)}"; return false; }
        }

        if (item.prereqs != null && i < item.prereqs.Length && item.prereqs[i] != null)
        {
            foreach (var p in item.prereqs[i])
            {
                if (string.IsNullOrEmpty(p)) continue;
                ParsePrereq(p, out string id, out int lvl);
                if (id == ShopIds.KeyToTheCity)
                {
                    if (!GameSession.KeyToCity) { whyNot = "Requires the Key to the City"; return false; }
                    continue;
                }
                if (GameSession.GetUpgradeLevel(id) < lvl)
                {
                    // Magnus's career steps read as steps ("Requires Step 4: X-Ray Googly Eyes"), not part levels.
                    string step = MagnusCareer.StepLabel(id, lvl);
                    whyNot = step != null ? $"Requires {step}" : $"Requires {PrereqName(id)} Lv {lvl}";
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>"truck_arm:2" -> ("truck_arm", 2); a bare id means level 1.</summary>
    static void ParsePrereq(string p, out string id, out int level)
    {
        int colon = p.IndexOf(':');
        if (colon < 0) { id = p.Trim(); level = 1; return; }
        id = p.Substring(0, colon).Trim();
        if (!int.TryParse(p.Substring(colon + 1).Trim(), out level)) level = 1;
    }

    static string PrereqName(string id)
    {
        var it = ScrapazonCatalog.Get(id);
        return it != null ? ScrapazonStore.ShortName(it) : id;
    }

    // ------------------------------------------------------------------ Trust and subscribers (§5.2)

    /// <summary>District Trust 0..1 (the district's trustStart until a midnight changes it).</summary>
    public static float Trust01(DistrictDef d) => d == null ? 0f : Mathf.Clamp01(GameSession.GetDistrictTrust(d.id, d.trustStart));

    /// <summary>Houses in the district (registered by the spawner, else the design count).</summary>
    public static int Houses(DistrictDef d)
    {
        if (d == null) return 0;
        EnsureSubscriberOrder();
        return subHouses.TryGetValue(d.id, out int n) && n > 0 ? n : d.houses;
    }

    /// <summary>
    /// Houses in <paramref name="d"/> that put trash out for you: round(houses × (0.25 + 0.75 × Trust)). 0 when the
    /// district is locked. Houses = the registered houses of that district (CurbTrashSpawner), else the design count.
    /// </summary>
    public static int Subscribers(DistrictDef d)
    {
        if (d == null || !Districts.IsUnlocked(d)) return 0;
        float trust = GameSession.GetDistrictTrust(d.id, d.trustStart);
        EnsureSubscriberOrder();
        int houses = subHouses.TryGetValue(d.id, out int n) && n > 0 ? n : d.houses;
        return Mathf.Clamp(Mathf.RoundToInt(houses * (0.25f + 0.75f * Mathf.Clamp01(trust))), 0, houses);
    }

    /// <summary>
    /// True when this house currently subscribes: its district is unlocked and it is among the first
    /// <see cref="Subscribers"/> houses of the district ordered by <c>PickupVisuals.StableHash(stopId)</c> (so the set grows
    /// and shrinks in a stable order). A house missing from the registry counts as subscribed in an unlocked district.
    /// </summary>
    public static bool IsSubscribed(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return false;
        var d = Districts.ForStopId(stopId);
        if (!Districts.IsUnlocked(d)) return false;
        EnsureSubscriberOrder();
        if (!subOrder.TryGetValue(stopId, out int index)) return true;
        return index < Subscribers(d);
    }

    /// <summary>Host curb population uses all online permit holders, independent of which business hosts the town.</summary>
    public static bool IsTownSubscribed(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return false;
        var district = Districts.ForStopId(stopId);
        if (!DistrictAccess.OpenInTown(district)) return false;
        EnsureSubscriberOrder();
        return !subOrder.TryGetValue(stopId, out int index) || index < DistrictAccess.TownSubscribers(district);
    }

    // stopId -> position within its district by StableHash; districtId -> registered house count. Rebuilt at most once
    // per frame, or when the house registry is replaced.
    static readonly Dictionary<string, int> subOrder = new Dictionary<string, int>(StringComparer.Ordinal);
    static readonly Dictionary<string, int> subHouses = new Dictionary<string, int>(StringComparer.Ordinal);
    static int subFrame = -1, subCount = -1;
    static object subFirst;

    static void EnsureSubscriberOrder()
    {
        var houses = CurbTrashSpawner.Houses;
        object first = houses.Count > 0 ? houses[0] : null;
        int frame = Time.frameCount;
        if (frame == subFrame && houses.Count == subCount && ReferenceEquals(first, subFirst)) return;
        subFrame = frame; subCount = houses.Count; subFirst = first;

        subOrder.Clear(); subHouses.Clear();
        var groups = new Dictionary<string, List<(int hash, string stopId)>>(StringComparer.Ordinal);
        for (int i = 0; i < houses.Count; i++)
        {
            var h = houses[i];
            if (h == null || string.IsNullOrEmpty(h.stopId)) continue;
            string did = Districts.ForStopId(h.stopId).id;
            if (!groups.TryGetValue(did, out var list)) groups[did] = list = new List<(int, string)>();
            list.Add((PickupVisuals.StableHash(h.stopId), h.stopId));
        }
        foreach (var kv in groups)
        {
            var list = kv.Value;
            list.Sort((a, b) => a.hash != b.hash ? a.hash.CompareTo(b.hash) : string.CompareOrdinal(a.stopId, b.stopId));
            for (int i = 0; i < list.Count; i++) subOrder[list[i].stopId] = i;
            subHouses[kv.Key] = list.Count;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        subOrder.Clear(); subHouses.Clear();
        subFrame = -1; subCount = -1; subFirst = null;
        ResetRuleStatics();
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

public enum WorkOrderKind
{
    None = 0,
    CollectOnTime = 1,   // wo_collect_ontime: "Collect {N} bags on time"
    CleanDistrict = 2,   // wo_clean_district: "Keep {district} ≥ 80% clean at midnight"
    FindRare = 3,        // wo_find_rare
    CleanSort = 4,       // wo_clean_sort: 40 in a row, no contamination
    Curbs = 5,           // wo_curbs: "Leave {N} curbs spotless"
    EbaeFair = 6,        // wo_ebae_fair
    NoCrush = 7,         // wo_no_crush (rank ≥ 3)
    Butler = 8,          // wo_butler (Bluffs unlocked)
    MagnusChess = 9,     // wo_magnus_chess (chess tier ≥ 1)
}

/// <summary>One of today's three Work Orders (Docs/Progression.md §4.5).</summary>
public sealed class WorkOrder
{
    public int slot;
    public WorkOrderKind kind;
    public int target, progress;
    public string districtId;
    public bool done, failed;

    public string Id => WorkOrders.IdOf(kind);
    public string Text => WorkOrders.TextOf(this);
    public float Progress01 => done ? 1f : target <= 0 ? 0f : Mathf.Clamp01(progress / (float)target);
    public bool Open => !done && !failed;
}

/// <summary>
/// Work Orders (§4.5): 3 per shift, rolled deterministically from (day, company seed) with
/// <c>PickupVisuals.StableHash</c> (no RNG state), progressed by the §9.3 facts, persisted in GameSession counters.
/// Reward each: max($25, 5% of yesterday's net) through <see cref="Ledger.Credit"/> + 40 Rep × the top district's
/// multiplier through <see cref="Ledger.RepTop"/>.
/// </summary>
public static class WorkOrders
{
    public const float CleanDistrictMin = 0.80f;
    public const int CleanSortRun = 40;
    public const int ChessSurvivePlies = 40;      // "survive 20 moves"
    public const double ButlerMaxAgeHours = 2.0;
    public const int ButlerBags = 5;

    const string DayCounter = "wo_day";

    static readonly List<WorkOrder> today = new List<WorkOrder>();
    static int loadedDay = -1;

    /// <summary>Any Work Order changed (progress, done, new day).</summary>
    public static event Action Changed;
    /// <summary>A Work Order was completed: (order, cash paid, Rep applied).</summary>
    public static event Action<WorkOrder, float, float> Completed;

    /// <summary>
    /// Today's orders (rolled on first use if the stored set is for another day). Between the clock's rollover and the
    /// midnight review (the inspection raises it a frame later, the runner's fallback up to 5 s later) this keeps showing
    /// the day that just ended: the review judges those orders, then rolls the new set together with the daily stats.
    /// </summary>
    public static IReadOnlyList<WorkOrder> Today
    {
        get
        {
            EnsureFor(ShownDay);
            return today;
        }
    }

    /// <summary>The day whose orders are live: Progression.Today, held back while that day's midnight review is pending
    /// (the daily stats roll in the same review step, so their day says whether it ran).</summary>
    static int ShownDay
    {
        get
        {
            int day = Progression.Today;
            int statsDay = DailyStats.Day;
            return statsDay > 0 && statsDay < day ? statsDay : day;
        }
    }

    /// <summary>The day the stored orders belong to.</summary>
    public static int Day => GameSession.GetCounter(DayCounter);

    /// <summary>Cash for completing one order right now: max($25, 5% of yesterday's net).</summary>
    public static float Pay => Mathf.Max(Economy.WorkOrderMinPay, Mathf.Round(Economy.WorkOrderPayFraction * DailyStats.Yesterday.Net * 100f) / 100f);

    /// <summary>Rep for completing one order right now (40 × top district multiplier).</summary>
    public static float RepReward
    {
        get
        {
            var top = Districts.Top;
            return RepValues.WorkOrder * (top != null ? top.repMult : 1f);
        }
    }

    // ------------------------------------------------------------------ rolling

    /// <summary>Make sure the stored orders are for <paramref name="day"/>: load them, or roll fresh ones.</summary>
    public static void EnsureFor(int day)
    {
        day = Mathf.Max(1, day);
        if (GameSession.GetCounter(DayCounter) == day)
        {
            if (loadedDay != day) Load(day);
            return;
        }
        var rolled = RollFor(day);
        today.Clear();
        today.AddRange(rolled);
        loadedDay = day;
        for (int i = 0; i < today.Count; i++) Save(today[i]);
        for (int i = today.Count; i < Economy.WorkOrdersPerDay; i++) GameSession.SetCounter($"wo_{i}_kind", 0);
        GameSession.SetCounter(DayCounter, day);
        Raise();
    }

    /// <summary>
    /// Pure, deterministic roll for a day with the company's current state (rank, districts, chess tier): the same
    /// day and state always give the same 3 orders.
    /// </summary>
    public static List<WorkOrder> RollFor(int day)
    {
        var pool = new List<WorkOrderKind>
        {
            WorkOrderKind.CollectOnTime, WorkOrderKind.CleanDistrict, WorkOrderKind.FindRare,
            WorkOrderKind.CleanSort, WorkOrderKind.Curbs, WorkOrderKind.EbaeFair,
        };
        if (Progression.Rank >= 3) pool.Add(WorkOrderKind.NoCrush);
        if (Districts.IsUnlocked(Districts.Get(Districts.BillionaireBluffs))) pool.Add(WorkOrderKind.Butler);
        if (UpgradeEffects.MagnusChessTier >= 1) pool.Add(WorkOrderKind.MagnusChess);

        int seed = Progression.CompanySeed;
        var list = new List<WorkOrder>();
        for (int i = 0; i < Economy.WorkOrdersPerDay && pool.Count > 0; i++)
        {
            int k = Hash($"wo/{seed}/{day}/{i}") % pool.Count;
            var kind = pool[k];
            pool.RemoveAt(k);
            var wo = new WorkOrder { slot = i, kind = kind };
            switch (kind)
            {
                case WorkOrderKind.CollectOnTime:
                    wo.target = Mathf.Max(3, Mathf.RoundToInt(0.4f * ExpectedBagsToday()));
                    break;
                case WorkOrderKind.CleanDistrict:
                {
                    var unlocked = Districts.Unlocked;
                    var d = unlocked[Hash($"wo/{seed}/{day}/{i}/district") % unlocked.Count];
                    wo.districtId = d.id;
                    wo.target = 1;
                    break;
                }
                case WorkOrderKind.CleanSort: wo.target = CleanSortRun; break;
                case WorkOrderKind.Curbs: wo.target = 8 + 2 * Districts.Unlocked.Count; break;
                case WorkOrderKind.Butler: wo.target = ButlerBags; break;
                default: wo.target = 1; break;
            }
            list.Add(wo);
        }
        return list;
    }

    /// <summary>Bags the unlocked districts should put out today at current Trust (§5.2: rate × (0.25 + 0.75 × Trust)).</summary>
    public static float ExpectedBagsToday()
    {
        float n = 0f;
        foreach (var d in Districts.Unlocked) n += d.bagsPerDay * (0.25f + 0.75f * Progression.Trust01(d));
        return n;
    }

    static int Hash(string s) => PickupVisuals.StableHash(s) & 0x7fffffff;

    // ------------------------------------------------------------------ text

    public static string IdOf(WorkOrderKind k) => k switch
    {
        WorkOrderKind.CollectOnTime => "wo_collect_ontime",
        WorkOrderKind.CleanDistrict => "wo_clean_district",
        WorkOrderKind.FindRare => "wo_find_rare",
        WorkOrderKind.CleanSort => "wo_clean_sort",
        WorkOrderKind.Curbs => "wo_curbs",
        WorkOrderKind.EbaeFair => "wo_ebae_fair",
        WorkOrderKind.NoCrush => "wo_no_crush",
        WorkOrderKind.Butler => "wo_butler",
        WorkOrderKind.MagnusChess => "wo_magnus_chess",
        _ => "wo_none",
    };

    public static string TextOf(WorkOrder w)
    {
        if (w == null) return "";
        switch (w.kind)
        {
            case WorkOrderKind.CollectOnTime: return $"Collect {w.target} bags on time";
            case WorkOrderKind.CleanDistrict:
            {
                var d = Districts.Get(w.districtId);
                return $"Keep {(d != null ? d.name : "the city")} ≥ {Mathf.RoundToInt(CleanDistrictMin * 100f)}% clean at midnight";
            }
            case WorkOrderKind.FindRare: return "Find a Rare or better";
            case WorkOrderKind.CleanSort: return $"Sort {w.target} items in a row without contamination";
            case WorkOrderKind.Curbs: return $"Leave {w.target} curbs spotless";
            case WorkOrderKind.EbaeFair: return "Sell something on ebag at Fair or better";
            case WorkOrderKind.NoCrush: return "No CRUSH ALL today";
            case WorkOrderKind.Butler: return "Clear a Butler Drop within 2 h";
            case WorkOrderKind.MagnusChess: return "Beat Magnus, or survive 20 moves";
            default: return "???";
        }
    }

    /// <summary>"3/10", "midnight", "DONE"...</summary>
    public static string ProgressText(WorkOrder w)
    {
        if (w == null) return "";
        if (w.done) return "DONE";
        if (w.failed) return "FAILED";
        switch (w.kind)
        {
            case WorkOrderKind.CleanDistrict:
            case WorkOrderKind.NoCrush: return "at midnight";
            case WorkOrderKind.FindRare:
            case WorkOrderKind.EbaeFair:
            case WorkOrderKind.MagnusChess: return "not yet";
            default: return $"{w.progress}/{w.target}";
        }
    }

    // ------------------------------------------------------------------ facts

    /// <summary>Does this bag count toward a Work Order at all? Never a raccoon crew's: Work Orders are about your own shift.</summary>
    public static bool CountsBag(BagCollectedFact f) => !f.byRaccoon;

    public static void OnBag(BagCollectedFact f, string districtId)
    {
        if (!CountsBag(f)) return;
        foreach (var w in Today)
        {
            if (!w.Open) continue;
            if (w.kind == WorkOrderKind.CollectOnTime && f.onTime) Advance(w, 1);
            else if (w.kind == WorkOrderKind.Butler && districtId == Districts.BillionaireBluffs && f.ageHours <= ButlerMaxAgeHours)
            {
                // count bags from the same drop (07:00 or 17:00 put-out); a new drop restarts the count
                double putOut = GameClock.TotalHours - f.ageHours;
                int dropKey = (int)Math.Floor(putOut / 24.0) * 2 + ((putOut % 24.0) < 12.0 ? 0 : 1) + 1;
                string auxKey = $"wo_{w.slot}_aux";
                if (GameSession.GetCounter(auxKey) != dropKey)
                {
                    GameSession.SetCounter(auxKey, dropKey);
                    w.progress = 0;
                }
                Advance(w, 1);
            }
        }
    }

    public static void OnCurb()
    {
        foreach (var w in Today) if (w.Open && w.kind == WorkOrderKind.Curbs) Advance(w, 1);
    }

    public static void OnSorted(bool correct, bool byMagnus)
    {
        if (byMagnus) return; // "Sort 40 in a row" is your streak; Magnus's misses don't break it
        foreach (var w in Today)
        {
            if (!w.Open || w.kind != WorkOrderKind.CleanSort) continue;
            if (correct) Advance(w, 1);
            else if (w.progress > 0) { w.progress = 0; Save(w); Raise(); }
        }
    }

    public static void OnTreasure(Rarity r)
    {
        if (r < Rarity.Rare) return;
        foreach (var w in Today) if (w.Open && w.kind == WorkOrderKind.FindRare) Complete(w);
    }

    public static void OnCrush()
    {
        foreach (var w in Today)
            if (w.Open && w.kind == WorkOrderKind.NoCrush) { w.failed = true; Save(w); Raise(); }
    }

    public static void OnEbaeSale(EbaeSale sale)
    {
        if (sale == null || sale.pawned) return;
        float appraisal = Mathf.Max(0.5f, EbaeMarket.Appraise(sale.item));
        if (EbaeMarket.GreedTier(sale.price / appraisal) < EbaeGreed.Fair) return;
        foreach (var w in Today) if (w.Open && w.kind == WorkOrderKind.EbaeFair) Complete(w);
    }

    public static void OnChess(bool humanWon, int plies)
    {
        if (!humanWon && plies < ChessSurvivePlies) return;
        foreach (var w in Today) if (w.Open && w.kind == WorkOrderKind.MagnusChess) Complete(w);
    }

    /// <summary>
    /// Midnight-judged orders for the day that just ended (before the new ones roll). Judges the STORED set: reading
    /// <see cref="Today"/> here used to roll the new day's orders first (Progression.Today has already moved on at the
    /// rollover) and then judge those, so a fresh "No CRUSH ALL today" paid out at 00:00 and the ended day's
    /// "Keep X clean" was never judged.
    /// </summary>
    public static void OnMidnight(MidnightReport r)
    {
        int ended = GameSession.GetCounter(DayCounter);
        if (ended <= 0 || ended >= Progression.Today) return;   // nothing stored for a past day (already rolled)
        if (loadedDay != ended) Load(ended);
        // A copy: Complete() raises Changed, and a listener may read Today.
        foreach (var w in new List<WorkOrder>(today))
        {
            if (!w.Open) continue;
            if (w.kind == WorkOrderKind.CleanDistrict)
            {
                if (r != null && r.clean01.TryGetValue(w.districtId ?? "", out float c) && c >= CleanDistrictMin) Complete(w);
                else { w.failed = true; Save(w); Raise(); }
            }
            else if (w.kind == WorkOrderKind.NoCrush) Complete(w);
        }
    }

    // ------------------------------------------------------------------ progress / reward

    static void Advance(WorkOrder w, int n)
    {
        w.progress = Mathf.Min(w.target, w.progress + n);
        if (w.progress >= w.target) { Complete(w); return; }
        Save(w);
        Raise();
    }

    static void Complete(WorkOrder w)
    {
        if (!w.Open) return;
        w.done = true;
        w.progress = Mathf.Max(w.progress, w.target);
        Save(w);
        float pay = Pay;
        Ledger.Credit(pay, $"Work Order: {w.Text}", LedgerIds.WorkOrder);
        float rep = Ledger.RepTop(RepIds.WorkOrder, RepValues.WorkOrder);
        try { Completed?.Invoke(w, pay, rep); } catch (Exception e) { Debug.LogException(e); }
        Raise();
    }

    // ------------------------------------------------------------------ persistence

    static void Save(WorkOrder w)
    {
        string p = $"wo_{w.slot}_";
        GameSession.SetCounter(p + "kind", (int)w.kind);
        GameSession.SetCounter(p + "target", w.target);
        GameSession.SetCounter(p + "prog", w.progress);
        GameSession.SetCounter(p + "state", w.done ? 1 : w.failed ? 2 : 0);
        GameSession.SetCounter(p + "district", DistrictIndex(w.districtId));
    }

    static void Load(int day)
    {
        today.Clear();
        loadedDay = day;
        for (int i = 0; i < Economy.WorkOrdersPerDay; i++)
        {
            string p = $"wo_{i}_";
            var kind = (WorkOrderKind)GameSession.GetCounter(p + "kind");
            if (kind == WorkOrderKind.None) continue;
            int state = GameSession.GetCounter(p + "state");
            today.Add(new WorkOrder
            {
                slot = i, kind = kind,
                target = GameSession.GetCounter(p + "target"),
                progress = GameSession.GetCounter(p + "prog"),
                done = state == 1, failed = state == 2,
                districtId = DistrictAt(GameSession.GetCounter(p + "district")),
            });
        }
    }

    // district stored as (index in Districts.All) + 1; 0 = none
    static int DistrictIndex(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        var all = Districts.All;
        for (int i = 0; i < all.Count; i++) if (all[i].id == id) return i + 1;
        return 0;
    }

    static string DistrictAt(int index1)
    {
        var all = Districts.All;
        return index1 > 0 && index1 <= all.Count ? all[index1 - 1].id : null;
    }

    static void Raise()
    {
        var h = Changed;
        if (h == null) return;
        try { h(); } catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ debug / tests

    /// <summary>Complete every open order now (debug menu).</summary>
    public static void DebugCompleteAll()
    {
        foreach (var w in Today) if (w.Open) Complete(w);
    }

    /// <summary>Forget the loaded set (statics reset / tests after GameSession.ResetAll).</summary>
    public static void ResetStatics()
    {
        today.Clear();
        loadedDay = -1;
        Changed = null; Completed = null;
    }

    /// <summary>
    /// Drop the cached orders so the next read comes from the counters again (keeps the listeners). SaveGuard.ReloadOwn calls
    /// it after a guest's own save is reloaded.
    /// </summary>
    public static void Reload()
    {
        today.Clear();
        loadedDay = -1;
    }
}

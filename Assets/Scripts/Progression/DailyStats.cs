using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One in-game day of company stats (Docs/Progression.md §9.5 A.2).</summary>
public sealed class DayStats
{
    public int day;
    /// <summary><see cref="bags"/> went into a truck (by hand or the arm); <see cref="crewBags"/> were collected by the
    /// business's raccoon crew instead and are counted apart (they never feed on-time %, the Shift bonus or a Clean Day).</summary>
    public int bags, onTime, crewBags, curbs, items, contamination, treasures, crushes;
    /// <summary>Cash in (credits) / out (charges: fines, wages, hospital...). Purchases are <see cref="spent"/>, not charges.</summary>
    public float credits, charges, spent, rep;
    /// <summary>A night out (Docs/Casino.md §4, §5): casino winnings paid, stakes put up, and the bar tab with the cab home.
    /// Kept apart from <see cref="credits"/> / <see cref="charges"/>: gambling is not the business's income, so it never
    /// prices Work Orders.</summary>
    public float casinoWon, casinoStaked, barTab;
    public float bestFindValue;
    public string bestFindDefId;

    /// <summary>"Net" income: credits − charges. Purchases (Ledger.Spend) are excluded.</summary>
    public float Net => credits - charges;
    public float OnTime01 => bags > 0 ? onTime / (float)bags : 0f;
    public string BestFindName
    {
        get
        {
            if (string.IsNullOrEmpty(bestFindDefId)) return null;
            var d = ItemCatalog.Get(bestFindDefId);
            return d != null ? d.name : bestFindDefId;
        }
    }
}

/// <summary>
/// Today's and yesterday's numbers: bags, on-time %, curbs, items, contamination, treasures, best find, credits,
/// charges and Rep. Persisted in <see cref="GameSession"/> counters ("st0_*" today, "st1_*" yesterday; money in cents,
/// Rep in tenths) so the company file carries them. Cash is counted from <see cref="GameSession.CashChanged"/> so even
/// not-yet-migrated AddCash call sites show up; <see cref="Ledger.Spend"/> postings are moved from "charges" to
/// "spent", which keeps purchases out of net. Yesterday's net prices Work Orders.
/// </summary>
public static class DailyStats
{
    const string DayCounter = "stats_day";
    const string T = "st0_", Y = "st1_";
    /// <summary>Counter suffix of <see cref="DayStats.crewBags"/>, and the suffix it had before save v6 (Kevin's Crew).</summary>
    const string CrewKey = "crew", LegacyCrewKey = "kevin";

    /// <summary>
    /// Save v6 renamed the background-crew bag counter. Pure load migration over a save's counters: today's and
    /// yesterday's old values move to the new keys (added to anything already there) and the old keys are dropped.
    /// Idempotent, and a save that never had them is untouched.
    /// </summary>
    public static void MigrateLegacyCrewCounters(IDictionary<string, int> counters)
    {
        if (counters == null) return;
        foreach (string day in new[] { T, Y })
        {
            if (!counters.TryGetValue(day + LegacyCrewKey, out int old)) continue;
            counters.Remove(day + LegacyCrewKey);
            if (old > 0) counters[day + CrewKey] = (counters.TryGetValue(day + CrewKey, out int now) ? now : 0) + old;
        }
    }

    static DayStats today, yesterday;

    /// <summary>The day the stats are counting (0 before the first roll). It moves on in the midnight review, so it lags
    /// Progression.Today between the clock's rollover and the review.</summary>
    public static int Day => GameSession.GetCounter(DayCounter);

    /// <summary>Today's stats (loaded from the counters on first use).</summary>
    public static DayStats Today => today ??= Read(T, GameSession.GetCounter(DayCounter));

    /// <summary>The previous in-game day's stats (zeros before the first midnight).</summary>
    public static DayStats Yesterday => yesterday ??= Read(Y, Mathf.Max(0, GameSession.GetCounter(DayCounter) - 1));

    /// <summary>Fired after any stat changes (Career page refresh).</summary>
    public static event Action Changed;

    /// <summary>Start a new day: today → yesterday, today cleared. No-op when already on that day.</summary>
    public static void RollTo(int day)
    {
        int cur = GameSession.GetCounter(DayCounter);
        if (cur == day) return;
        if (cur != 0)
        {
            var t = Today;
            yesterday = Copy(t);
            yesterday.day = cur;
            Write(Y, yesterday);
        }
        today = new DayStats { day = day };
        Write(T, today);
        GameSession.SetCounter(DayCounter, day);
        Raise();
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>
    /// The statics show someone else's world (a guest, or a guest who just left and whose own save isn't back yet): their
    /// local cash/Rep moves are thrown away by SaveGuard.ReloadOwn, so they must not reach today's stats (Multiplayer.md rule 15).
    /// Play mode only: a latch left over from a play session never mutes edit-mode evals.
    /// </summary>
    static bool NotOurs => GameSession.IsGuest || (Application.isPlaying && SaveGuard.Foreign);

    public static void OnCashChanged(float amount, string reason, Vector3? pos)
    {
        if (NotOurs) return;
        var t = Today;
        if (amount > 0f) { t.credits += amount; SetMoney("credits", t.credits); }
        else if (amount < 0f) { t.charges += -amount; SetMoney("charges", t.charges); }
        Raise();
    }

    /// <summary>
    /// A casino or bar posting (<see cref="LedgerIds.IsLeisure"/>) has already been counted into <see cref="DayStats.credits"/>
    /// or <see cref="DayStats.charges"/> by the cash change: move it into the night-out lines instead, so "Net" (and with it
    /// tomorrow's Work Order pay) is the business's own. Pure; returns false for any other posting.
    /// </summary>
    public static bool MoveLeisure(DayStats t, LedgerEntry e)
    {
        if (t == null || !LedgerIds.IsLeisure(e.id) || (e.kind != Ledger.KindCredit && e.kind != Ledger.KindCharge)) return false;
        float x = e.amount;
        if (x > 0f) t.credits = Mathf.Max(0f, t.credits - x);
        else if (x < 0f) t.charges = Mathf.Max(0f, t.charges + x);
        if (e.id == LedgerIds.CasinoPayout) t.casinoWon = Mathf.Max(0f, t.casinoWon + x);
        else if (e.id == LedgerIds.CasinoWager) t.casinoStaked = Mathf.Max(0f, t.casinoStaked - x); // a voided stake comes back as a credit
        else t.barTab = Mathf.Max(0f, t.barTab - x);
        return true;
    }

    public static void OnLedgerPosted(LedgerEntry e)
    {
        if (NotOurs) return;
        if (LedgerIds.IsLeisure(e.id))
        {
            var day = Today;
            if (!MoveLeisure(day, e)) return;
            if (e.amount > 0f) GameSession.ForgetEarned(e.amount); // winnings are not lifetime earnings
            SetMoney("credits", day.credits);
            SetMoney("charges", day.charges);
            SetMoney("casino_in", day.casinoWon);
            SetMoney("casino_out", day.casinoStaked);
            SetMoney("bar", day.barTab);
            Raise();
            return;
        }
        if (e.kind != Ledger.KindSpend) return;
        // The spend already arrived through CashChanged as a negative change: move it out of the charges.
        var t = Today;
        float x = -e.amount;
        t.charges = Mathf.Max(0f, t.charges - x);
        t.spent += x;
        SetMoney("charges", t.charges);
        SetMoney("spent", t.spent);
        Raise();
    }

    public static void OnRep(float delta)
    {
        if (NotOurs) return;
        var t = Today;
        t.rep += delta;
        GameSession.SetCounter(T + "rep", Mathf.RoundToInt(t.rep * 10f));
        Raise();
    }

    public static void OnBag(BagCollectedFact f)
    {
        var t = Today;
        if (f.byRaccoon) { t.crewBags++; Set(CrewKey, t.crewBags); }
        else
        {
            t.bags++;
            Set("bags", t.bags);
            if (f.onTime) { t.onTime++; Set("ontime", t.onTime); }
        }
        Raise();
    }

    public static void OnCurb() { var t = Today; t.curbs++; Set("curbs", t.curbs); Raise(); }

    public static void OnSorted(bool correct)
    {
        var t = Today;
        if (correct) { t.items++; Set("items", t.items); }
        else { t.contamination++; Set("contam", t.contamination); }
        Raise();
    }

    public static void OnTreasure(ItemInstance item)
    {
        var t = Today;
        t.treasures++;
        Set("treasures", t.treasures);
        float v = item != null ? Mathf.Max(item.value, 0f) : 0f;
        if (item != null && (string.IsNullOrEmpty(t.bestFindDefId) || v > t.bestFindValue))
        {
            t.bestFindValue = v;
            t.bestFindDefId = item.defId;
            SetMoney("best", v);
            Set("best_def", DefIndex(item.defId));
        }
        Raise();
    }

    public static void OnCrush() { var t = Today; t.crushes++; Set("crushes", t.crushes); Raise(); }

    // ------------------------------------------------------------------ persistence (counters)

    static void Set(string key, int v) => GameSession.SetCounter(T + key, v);
    static void SetMoney(string key, float v) => GameSession.SetCounter(T + key, Mathf.RoundToInt(v * 100f));

    static DayStats Read(string p, int day)
    {
        int Get(string k) => GameSession.GetCounter(p + k);
        float Money(string k) => GameSession.GetCounter(p + k) / 100f;
        int defIdx = Get("best_def");
        return new DayStats
        {
            day = day,
            bags = Get("bags"), onTime = Get("ontime"), crewBags = Get(CrewKey), curbs = Get("curbs"), items = Get("items"),
            contamination = Get("contam"), treasures = Get("treasures"), crushes = Get("crushes"),
            credits = Money("credits"), charges = Money("charges"), spent = Money("spent"), rep = Get("rep") / 10f,
            casinoWon = Money("casino_in"), casinoStaked = Money("casino_out"), barTab = Money("bar"),
            bestFindValue = Money("best"), bestFindDefId = DefAt(defIdx),
        };
    }

    static void Write(string p, DayStats s)
    {
        void W(string k, int v) => GameSession.SetCounter(p + k, v);
        void M(string k, float v) => GameSession.SetCounter(p + k, Mathf.RoundToInt(v * 100f));
        W("bags", s.bags); W("ontime", s.onTime); W(CrewKey, s.crewBags); W("curbs", s.curbs); W("items", s.items);
        W("contam", s.contamination); W("treasures", s.treasures); W("crushes", s.crushes);
        M("credits", s.credits); M("charges", s.charges); M("spent", s.spent); W("rep", Mathf.RoundToInt(s.rep * 10f));
        M("casino_in", s.casinoWon); M("casino_out", s.casinoStaked); M("bar", s.barTab);
        M("best", s.bestFindValue); W("best_def", DefIndex(s.bestFindDefId));
    }

    static DayStats Copy(DayStats s) => new DayStats
    {
        day = s.day, bags = s.bags, onTime = s.onTime, crewBags = s.crewBags, curbs = s.curbs, items = s.items,
        contamination = s.contamination, treasures = s.treasures, crushes = s.crushes, credits = s.credits,
        charges = s.charges, spent = s.spent, rep = s.rep, bestFindValue = s.bestFindValue, bestFindDefId = s.bestFindDefId,
        casinoWon = s.casinoWon, casinoStaked = s.casinoStaked, barTab = s.barTab,
    };

    // best find stored as (index into ItemCatalog.All) + 1; 0 = none
    static int DefIndex(string defId)
    {
        if (string.IsNullOrEmpty(defId)) return 0;
        var all = ItemCatalog.All;
        for (int i = 0; i < all.Count; i++) if (all[i].id == defId) return i + 1;
        return 0;
    }

    static string DefAt(int index1)
    {
        var all = ItemCatalog.All;
        return index1 > 0 && index1 <= all.Count ? all[index1 - 1].id : null;
    }

    static void Raise()
    {
        var h = Changed;
        if (h == null) return;
        try { h(); } catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Forget the cached days (statics reset / tests after GameSession.ResetAll).</summary>
    public static void ResetStatics()
    {
        today = null; yesterday = null;
        Changed = null;
    }

    /// <summary>Drop the cache so the next read comes from the counters again (tests).</summary>
    public static void Reload() { today = null; yesterday = null; }
}

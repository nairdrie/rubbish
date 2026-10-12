using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The Career page inside Scrapazon (Docs/Progression.md §9.5 A.5): rank card with the Rep bar and Clean Days, what the
/// next rank unlocks, today's Work Orders, per-district rows (locked/unlocked, Trust, subscribers, clean %), the Key to
/// the City checklist, Magnus's chess tier, today vs yesterday stats, the rank ladder, the Head Office inbox, the "Best
/// next buy" shelf and the Trash-dex grid. Built in code (UI Toolkit) into a container ScrapazonApp owns; styles in
/// Assets/UI/shop/scrapazon.uss ("sz-cr-*"). Read-only: every number comes from Progression / WorkOrders / DailyStats.
/// </summary>
public sealed class ScrapazonCareer
{
    readonly VisualElement root;
    readonly Action<string, string> toast;
    readonly Action<string> openDept;
    readonly Action<string> buyItem;
    float builtAt = -99f;

    public ScrapazonCareer(VisualElement container, Action<string, string> toast, Action<string> openDept, Action<string> buyItem)
    {
        root = container;
        this.toast = toast;
        this.openDept = openDept;
        this.buyItem = buyItem;
    }

    /// <summary>Rebuild everything (animate = pop the sections in).</summary>
    public void Build(bool animate)
    {
        if (root == null) return;
        builtAt = Time.unscaledTime;
        root.Clear();
        int i = 0;
        void Add(VisualElement e)
        {
            root.Add(e);
            if (!animate) return;
            e.AddToClassList("sz-card--pop");
            int delay = 30 + (i++) * 55;
            e.schedule.Execute(() => e.RemoveFromClassList("sz-card--pop")).ExecuteLater(delay);
        }

        try
        {
            Add(RankCard(animate));
            var row = V("sz-cr-row");
            row.Add(NextUnlocksCard());
            row.Add(WorkOrdersCard());
            Add(row);
            Add(DistrictsCard());
            var row2 = V("sz-cr-row");
            row2.Add(KeyCard());
            row2.Add(MagnusCard());
            row2.Add(StatsCard());
            Add(row2);
            Add(BestBuyCard());
            Add(LadderCard());
            Add(InboxCard());
            Add(DexCard());
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Refresh at most twice a second while shown (cash ticks a lot).</summary>
    public void RefreshThrottled()
    {
        if (Time.unscaledTime - builtAt < 0.5f) return;
        Build(false);
    }

    // ------------------------------------------------------------------ sections

    VisualElement RankCard(bool animate)
    {
        int rank = Progression.Rank, next = Progression.Next;
        var card = Section("sz-cr-hero");
        var left = V("sz-cr-badge");
        left.Add(L("RANK", "sz-cr-badge-word"));
        left.Add(L(rank.ToString(), "sz-cr-badge-num"));
        left.Add(L($"of {Progression.MaxRank}", "sz-cr-badge-of"));
        if (rank >= Progression.MaxRank) left.AddToClassList("sz-cr-badge--max");
        card.Add(left);

        var mid = V("sz-cr-hero-mid");
        mid.Add(L("Rubbish Co. Employee File", "sz-cr-kicker"));
        mid.Add(L(Progression.RankTitle(rank), "sz-cr-title"));
        mid.Add(L($"\"{Progression.HeadOfficeLine(rank)}\"", "sz-cr-quote"));

        // Rep bar
        float rep = GameSession.Rep;
        var barRow = V("sz-cr-bar-row");
        var bar = V("sz-cr-bar");
        var fill = V("sz-cr-bar-fill");
        float p = Progression.RankProgress01;
        if (animate)
        {
            fill.style.width = Length.Percent(0f);
            fill.schedule.Execute(() => fill.style.width = Length.Percent(p * 100f)).ExecuteLater(250);
        }
        else fill.style.width = Length.Percent(p * 100f);
        bar.Add(fill);
        barRow.Add(bar);
        string barText = next == 0 ? $"{rep:N0} Rep (max rank)" : $"{rep:N0} / {Progression.RankRep(next):N0} Rep";
        barRow.Add(L(barText, "sz-cr-bar-text"));
        mid.Add(barRow);

        string clean = next == 0 ? $"{GameSession.CleanDays} Clean Days"
                     : $"{GameSession.CleanDays} / {Progression.RankCleanDays(next)} Clean Days for {Progression.RankTitle(next)}";
        var chips = V("sz-cr-chip-row");
        chips.Add(Chip(clean, Progression.CleanDaysToNext > 0 ? "sz-cr-chip--warn" : "sz-cr-chip--ok"));
        if (Progression.WaitingOnCleanDays)
            chips.Add(Chip("Promotion waits for a Clean Day (top district ≥ 70% clean + 5 bags by midnight)", "sz-cr-chip--warn"));
        else if (next != 0)
            chips.Add(Chip($"{Mathf.CeilToInt(Progression.RepToNext):N0} Rep to go", "sz-cr-chip--rep"));
        chips.Add(Chip($"Day {Progression.Today}", null));
        mid.Add(chips);
        card.Add(mid);
        return card;
    }

    VisualElement NextUnlocksCard()
    {
        int next = Progression.Next;
        var card = Section("sz-cr-col");
        card.Add(H(next == 0 ? "You've unlocked everything" : $"Rank {next} unlocks"));
        if (next == 0)
        {
            card.Add(L(GameSession.KeyToCity ? "The Golden Garbage Truck awaits (if you can afford it)." : "Earn the Key to the City to unlock the Golden Garbage Truck.", "sz-cr-muted"));
            return card;
        }
        card.Add(L(Progression.RankTitle(next), "sz-cr-sub"));
        var list = V("sz-cr-chip-wrap");
        foreach (var (item, level) in Progression.UnlocksAtRank(next))
        {
            string step = MagnusCareer.StepLabel(item.id, level);
            string name = step != null ? "Magnus " + step : ScrapazonStore.ShortName(item) + (item.IsUpgrade && item.maxLevel > 1 ? $" L{level}" : "");
            var chip = Chip(name, item.dept == "permits" ? "sz-cr-chip--permit" : null);
            string dept = item.dept;
            Click(chip, () => openDept?.Invoke(dept));
            list.Add(chip);
        }
        card.Add(list);
        return card;
    }

    VisualElement WorkOrdersCard()
    {
        var card = Section("sz-cr-col");
        card.Add(H("Today's Work Orders"));
        card.Add(StarterContractRow());
        var wos = WorkOrders.Today;
        if (wos.Count == 0) { card.Add(L("New Work Orders arrive at midnight.", "sz-cr-muted")); return card; }
        card.Add(L($"Each pays {Economy.Money(WorkOrders.Pay)} + {Mathf.RoundToInt(WorkOrders.RepReward)} Rep", "sz-cr-sub"));
        foreach (var w in wos)
        {
            var row = V("sz-cr-wo" + (w.done ? " sz-cr-wo--done" : w.failed ? " sz-cr-wo--failed" : ""));
            var top = V("sz-cr-wo-top");
            top.Add(L(w.Text, "sz-cr-wo-text"));
            top.Add(L(WorkOrders.ProgressText(w), "sz-cr-wo-status"));
            row.Add(top);
            var bar = V("sz-cr-minibar");
            var fill = V("sz-cr-minibar-fill");
            fill.style.width = Length.Percent(w.Progress01 * 100f);
            bar.Add(fill);
            row.Add(bar);
            if (w.done) row.Add(L("DONE", "sz-cr-wo-stamp"));
            card.Add(row);
        }
        return card;
    }

    static VisualElement StarterContractRow()
    {
        int progress = StarterCollectionContract.LocalProgress;
        bool complete = StarterCollectionContract.Complete;
        var row = V("sz-cr-wo" + (complete ? " sz-cr-wo--done" : ""));
        var top = V("sz-cr-wo-top");
        top.Add(L("Starter collection contract", "sz-cr-wo-text"));
        top.Add(L(StarterCollectionContract.ProgressText(progress), "sz-cr-wo-status"));
        row.Add(top);
        var bar = V("sz-cr-minibar");
        var fill = V("sz-cr-minibar-fill");
        fill.style.width = Length.Percent(progress * 100f / StarterCollectionContract.RequiredBags);
        bar.Add(fill);
        row.Add(bar);
        if (!complete)
            row.Add(L("Get paid for 8 curb bags collected into your business's truck. Crew collections count for the truck's owner. One-time $100 bonus; no treasure sales needed. No deadline.", "sz-cr-muted"));
        else row.Add(L("COMPLETE · one-time starter contract", "sz-cr-wo-stamp"));
        return row;
    }

    VisualElement DistrictsCard()
    {
        var card = Section("sz-cr-wide");
        card.Add(H("Districts"));
        foreach (var d in Districts.ProgressionOrder)
        {
            bool open = Districts.IsUnlocked(d);
            var row = V("sz-cr-district" + (open ? "" : " sz-cr-district--locked"));
            var name = V("sz-cr-district-name");
            name.Add(L(d.name, "sz-cr-district-title"));
            name.Add(L($"Tier {d.tier} · {Economy.Money(d.fee)}/bag{(d.tip > 0f ? $" + {Economy.Money(d.tip)} tip" : "")} · Rep ×{d.repMult:0.#}", "sz-cr-district-meta"));
            row.Add(name);

            if (!open)
            {
                var permit = ScrapazonCatalog.Get(d.permitId);
                bool rankOk = Progression.HasRank(d.rankRequired);
                string contractLock = Campaign.LockedReason(d.id);   // opened by winning a contract, not by buying the permit
                string lockText = contractLock != null ? "LOCKED · " + contractLock
                    : $"LOCKED · Rank {d.rankRequired} ({Progression.RankTitle(d.rankRequired)}) + {Economy.Money(d.permitPrice)} permit";
                row.Add(L(lockText, "sz-cr-district-lock"));
                if (permit != null && rankOk && contractLock == null)
                {
                    var b = L(ScrapazonStore.CanAfford(permit) ? $"Buy permit · {Economy.Money(d.permitPrice)}" : $"{Economy.Money(d.permitPrice - GameSession.Cash)} short", "sz-cr-btn");
                    string pid = permit.id;
                    Click(b, () => buyItem?.Invoke(pid));
                    row.Add(b);
                }
                card.Add(row);
                continue;
            }

            float trust = Progression.Trust01(d);
            row.Add(Meter("Trust", trust, $"{Mathf.RoundToInt(trust * 100f)}%", trust >= 0.65f ? "sz-cr-meter--good" : trust >= 0.4f ? "sz-cr-meter--ok" : "sz-cr-meter--bad"));
            float clean = Progression.CleanNow01(d, out bool live);
            row.Add(Meter(live ? "Clean" : "Clean (last midnight)", clean, $"{Mathf.RoundToInt(clean * 100f)}%",
                          clean >= 0.85f ? "sz-cr-meter--good" : clean >= 0.5f ? "sz-cr-meter--ok" : "sz-cr-meter--bad"));
            row.Add(L($"{Progression.Subscribers(d)}/{Progression.Houses(d)} houses subscribe", "sz-cr-district-subs"));
            card.Add(row);
        }
        return card;
    }

    VisualElement KeyCard()
    {
        var card = Section("sz-cr-col");
        card.Add(H("Key to the City"));
        bool rank = Progression.Rank >= Progression.MaxRank;
        bool all = Districts.Unlocked.Count >= Districts.All.Count;
        int streak = Mathf.Min(Progression.KeyStreak, Progression.KeyNightsNeeded);
        card.Add(Check($"Rank 12: {Progression.RankTitle(Progression.MaxRank)}", rank));
        card.Add(Check("All 4 districts unlocked", all));
        card.Add(Check($"All 4 districts ≥ 90% clean at midnight, 3 nights in a row ({streak}/{Progression.KeyNightsNeeded})", streak >= Progression.KeyNightsNeeded));
        if (GameSession.KeyToCity) card.Add(L("KEY GRANTED", "sz-cr-key-stamp"));
        return card;
    }

    VisualElement MagnusCard()
    {
        var card = Section("sz-cr-col");
        card.Add(H("Magnus (career + chess)"));
        int tier = UpgradeEffects.MagnusChessTier;
        var ct = UpgradeEffects.Chess(tier);
        card.Add(L($"Tier {tier} / 11", "sz-cr-big"));
        card.Add(L(ct.label, tier >= 11 ? "sz-cr-prime" : "sz-cr-sub"));
        var pips = V("sz-cr-pips");
        for (int i = 1; i <= 11; i++) pips.Add(V("sz-cr-pip" + (i <= tier ? " sz-cr-pip--on" : "")));
        card.Add(pips);
        // His career path (MagnusCareer): every step raises the chess tier too.
        card.Add(L($"Career: {MagnusCareer.DoneCount} of {MagnusCareer.Count} steps", "sz-cr-sub"));
        int next = MagnusCareer.NextIndex;
        if (next >= 0)
        {
            var s = MagnusCareer.Steps[next];
            var link = L($"Next: Step {next + 1}: {s.name} ({Economy.Money(s.Price)}). See his career path →", "sz-cr-muted");
            Click(link, () => openDept?.Invoke("magnus"));
            card.Add(link);
        }
        if (GameSession.GetCounter(Progression.Milestones.MagnusPrimeDefeated) > 0) card.Add(L("Grandmaster of Garbage", "sz-cr-prime"));
        return card;
    }

    /// <summary>"+$120.00 / -$85.00": winnings paid out / stakes put up.</summary>
    public static string CasinoLine(DayStats d) =>
        d == null || (d.casinoWon <= 0f && d.casinoStaked <= 0f) ? "-" : "+" + Economy.Money(d.casinoWon) + " / -" + Economy.Money(d.casinoStaked);
    /// <summary>"-$30.00": drinks and the cab home.</summary>
    public static string BarLine(DayStats d) => d == null || d.barTab <= 0f ? "-" : "-" + Economy.Money(d.barTab);

    VisualElement StatsCard()
    {
        var card = Section("sz-cr-col");
        card.Add(H("Shift report"));
        var t = DailyStats.Today; var y = DailyStats.Yesterday;
        var grid = V("sz-cr-stats");
        void Row(string label, string a, string b)
        {
            var r = V("sz-cr-stat-row");
            r.Add(L(label, "sz-cr-stat-label"));
            r.Add(L(a, "sz-cr-stat-val"));
            r.Add(L(b, "sz-cr-stat-val sz-cr-stat-val--y"));
            grid.Add(r);
        }
        Row("", "Today", "Yesterday");
        Row("Bags (on time)", $"{t.bags} ({Mathf.RoundToInt(t.OnTime01 * 100f)}%)", $"{y.bags} ({Mathf.RoundToInt(y.OnTime01 * 100f)}%)");
        Row("Curbs cleared", t.curbs.ToString(), y.curbs.ToString());
        Row("Items sorted", t.items.ToString(), y.items.ToString());
        Row("Contamination", t.contamination.ToString(), y.contamination.ToString());
        Row("Treasures", t.treasures.ToString(), y.treasures.ToString());
        Row("Best find", t.BestFindName ?? "-", y.BestFindName ?? "-");
        Row("Net income", Economy.Money(t.Net), Economy.Money(y.Net));
        // A night out is listed apart: it is not business income and never prices Work Orders (Docs/Casino.md §4).
        // Two rows: "+$1,069 / -$1,000" in one cell runs into the next column.
        if (t.casinoWon > 0f || t.casinoStaked > 0f || y.casinoWon > 0f || y.casinoStaked > 0f)
        {
            string Cell(float v, string sign) => v > 0f ? sign + Economy.Money(v) : "-";
            Row("Casino paid out", Cell(t.casinoWon, "+"), Cell(y.casinoWon, "+"));
            Row("Casino stakes", Cell(t.casinoStaked, "-"), Cell(y.casinoStaked, "-"));
        }
        if (t.barTab > 0f || y.barTab > 0f) Row("Bar", BarLine(t), BarLine(y));
        Row("Rep", $"{t.rep:+0;-0;0}", $"{y.rep:+0;-0;0}");
        card.Add(grid);
        return card;
    }

    VisualElement BestBuyCard()
    {
        var card = Section("sz-cr-wide");
        card.Add(H("Best next buy"));
        var list = ScrapazonAdvisor.BestNextBuys(3);
        if (list.Count == 0) { card.Add(L("Nothing to buy right now. Earn Rep to unlock more.", "sz-cr-muted")); return card; }
        var row = V("sz-cr-row sz-cr-row--tight");
        foreach (var s in list) row.Add(BestBuyTile(s, buyItem));
        card.Add(row);
        return card;
    }

    /// <summary>One "Best next buy" tile (shared with the All department's shelf).</summary>
    public static VisualElement BestBuyTile(ScrapazonAdvisor.Suggestion s, Action<string> buy)
    {
        var tile = V("sz-best-tile" + (s.affordable ? " sz-best-tile--afford" : ""));
        string step = MagnusCareer.StepLabel(s.item.id, s.level);
        tile.Add(L(step != null ? "Magnus " + step : ScrapazonStore.ShortName(s.item) + (s.item.maxLevel > 1 ? $" Lv {s.level}" : ""), "sz-best-name"));
        tile.Add(L(s.item.TitleFor(s.level), "sz-best-title"));
        tile.Add(L(Economy.Money(s.price), "sz-best-price"));
        tile.Add(L(s.affordable ? "You can afford it!" : $"{Economy.Money(s.shortfall)} to go (~{Mathf.Max(0.1f, s.days):0.#} days)", "sz-best-note"));
        string id = s.item.id;
        var b = L(s.affordable ? "Buy" : "Show me", "sz-best-btn");
        Click(b, () => buy?.Invoke(id));
        tile.Add(b);
        return tile;
    }

    VisualElement LadderCard()
    {
        var card = Section("sz-cr-wide");
        card.Add(H("The ladder"));
        var row = V("sz-cr-ladder");
        int rank = Progression.Rank;
        for (int r = 1; r <= Progression.MaxRank; r++)
        {
            string cls = r < rank ? " sz-cr-rung--past" : r == rank ? " sz-cr-rung--now" : r == rank + 1 ? " sz-cr-rung--next" : "";
            var rung = V("sz-cr-rung" + cls);
            rung.Add(L(r.ToString(), "sz-cr-rung-num"));
            rung.Add(L(Progression.RankTitle(r), "sz-cr-rung-title"));
            rung.Add(L(r == 1 ? "start" : $"{Progression.RankRep(r):N0} Rep · {Progression.RankCleanDays(r)} CD", "sz-cr-rung-req"));
            int rr = r;
            Click(rung, () => toast?.Invoke($"Rank {rr}: \"{Progression.HeadOfficeLine(rr)}\"", "info"));
            row.Add(rung);
        }
        card.Add(row);
        return card;
    }

    VisualElement InboxCard()
    {
        var card = Section("sz-cr-wide");
        card.Add(H("Head Office inbox"));
        var inbox = HeadOffice.Inbox;
        if (inbox.Count == 0) { card.Add(L("No memos. Head Office is at lunch. Head Office is always at lunch.", "sz-cr-muted")); return card; }
        for (int i = 0; i < inbox.Count && i < 5; i++)
        {
            var l = inbox[i];
            var m = V("sz-cr-memo");
            m.Add(L($"Day {l.day} · {GameClock.FormatHours(l.hour)} · {l.subject}", "sz-cr-memo-subject"));
            m.Add(L(l.body, "sz-cr-memo-body"));
            card.Add(m);
        }
        return card;
    }

    VisualElement DexCard()
    {
        Progression.BackfillDex();
        var card = Section("sz-cr-wide");
        int found = Progression.DexFound, total = Progression.DexTotal;
        card.Add(H($"Trash-dex  {found} / {total}"));
        card.Add(L(found >= total && total > 0 ? "Complete! Title earned: Curator." : "Every valuable you've ever kept. Silhouettes are still out there.", "sz-cr-muted"));
        var grid = V("sz-cr-dex");
        var defs = new List<ItemDef>(Progression.DexDefs);
        defs.Sort((a, b) => a.rarity != b.rarity ? a.rarity.CompareTo(b.rarity) : string.CompareOrdinal(a.name, b.name));
        foreach (var d in defs)
        {
            int day = Progression.DexDay(d.id);
            var tile = V("sz-cr-dex-tile sz-cr-dex--" + d.rarity.ToString().ToLowerInvariant() + (day > 0 ? " sz-cr-dex-tile--found" : ""));
            tile.Add(L(day > 0 ? d.name : "???", "sz-cr-dex-name"));
            tile.Add(L(day > 0 ? $"Day {day}" : RarityInfo.Label(d.rarity), "sz-cr-dex-day"));
            var def = d;
            int dd = day;
            Click(tile, () =>
            {
                if (dd > 0) toast?.Invoke($"{def.name}: {def.flavor}", "info");
                else toast?.Invoke($"A {RarityInfo.Label(def.rarity).ToLowerInvariant()} something. Somebody out there owns one. For now.", "info");
            });
            grid.Add(tile);
        }
        card.Add(grid);
        return card;
    }

    // ------------------------------------------------------------------ element helpers

    static VisualElement Section(string extra)
    {
        var e = V("sz-cr-card " + extra);
        return e;
    }

    static Label H(string text) => L(text, "sz-cr-h");

    static VisualElement Chip(string text, string extra) => L(text, "sz-cr-chip" + (extra != null ? " " + extra : ""));

    static VisualElement Check(string text, bool ok)
    {
        var r = V("sz-cr-check" + (ok ? " sz-cr-check--ok" : ""));
        r.Add(L(ok ? "YES" : "NO", "sz-cr-check-box"));
        r.Add(L(text, "sz-cr-check-text"));
        return r;
    }

    static VisualElement Meter(string label, float v01, string value, string mood)
    {
        var m = V("sz-cr-meter " + mood);
        var top = V("sz-cr-meter-top");
        top.Add(L(label, "sz-cr-meter-label"));
        top.Add(L(value, "sz-cr-meter-value"));
        m.Add(top);
        var bar = V("sz-cr-minibar");
        var fill = V("sz-cr-minibar-fill");
        fill.style.width = Length.Percent(Mathf.Clamp01(v01) * 100f);
        bar.Add(fill);
        m.Add(bar);
        return m;
    }

    static void Click(VisualElement e, Action action)
    {
        if (e == null) return;
        e.AddManipulator(new Clickable(() =>
        {
            Sfx.Play("click", null, 0.5f);
            action();
        }));
    }

    static Label L(string text, string classes)
    {
        var l = new Label(text);
        AddClasses(l, classes);
        return l;
    }

    static VisualElement V(string classes)
    {
        var v = new VisualElement();
        AddClasses(v, classes);
        return v;
    }

    static void AddClasses(VisualElement e, string classes)
    {
        if (string.IsNullOrEmpty(classes)) return;
        foreach (var c in classes.Split(' '))
            if (c.Length > 0) e.AddToClassList(c);
    }
}

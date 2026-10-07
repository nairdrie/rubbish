using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PT = UiText.Pause;

/// <summary>Today's numbers for the Esc timesheet (read live, or a fixed sample for previews).</summary>
public sealed class ShiftData
{
    public float city = 100f;
    public int bags, finds;
    public float net;
    /// <summary>(text, progress, state) per Work Order; state 0 open, 1 done, 2 failed.</summary>
    public readonly List<(string text, string progress, int state)> orders = new List<(string, string, int)>();
    public int rank = 1, next;
    public string rankTitle = "";
    public float rank01, rep, repToNext;
    public int cleanDaysToNext;
    /// <summary>Today's best find (name, value), or null.</summary>
    public string bestFind;
    public float bestFindValue;
    /// <summary>A guest's card: their own company's numbers aren't on this shift's books.</summary>
    public bool guest;
    public string hostName = PT.TheBoss;
    /// <summary>Current employer for this session; null when not employed.</summary>
    public string employer;
    public int wageCents;
    public byte sharePercent;
    public long unpaidCents, paidCents, ownCashCents;
    public string ownBusiness, cargoText, municipalPolicy;

    /// <summary>Read today's numbers from the game (DailyStats, WorkOrders, Progression, FeelWorld).</summary>
    public static ShiftData Live(bool guest, string hostName)
    {
        var d = new ShiftData { guest = guest, hostName = string.IsNullOrEmpty(hostName) ? PT.TheBoss : hostName };
        d.ownCashCents = GameSession.CashCents;
        d.ownBusiness = GameSession.BusinessName;
        var cargo = TruckHudContext.Resolve(PlayerController.Local); // coop:allow(local) presentation-only cargo identity on the local pause page
        d.cargoText = cargo.title + " · " + cargo.context + (cargo.cargoKnown ? $" · {cargo.loadKg:0.#}/{cargo.capacityKg:0.#} kg" : " · syncing");
        d.municipalPolicy = MunicipalEconomy.LocalPolicyText;
        if (Payroll.TryGetLocalJob(out var job))
        {
            d.employer = Payroll.BusinessLabelOf(job.employerClientId);
            d.wageCents = job.wageCents;
            d.sharePercent = job.sharePercent;
            d.unpaidCents = job.unpaidCents;
            d.paidCents = job.paidCents;
        }
        try { d.city = FeelWorld.Cleanliness; }
        catch (Exception e) { Debug.LogException(e); }
        if (guest) return d; // a guest's statics aren't theirs (SaveGuard); don't roll or read their company's books here
        try
        {
            var t = DailyStats.Today;
            d.bags = t.bags;
            d.net = t.Net;
            d.finds = t.treasures;
            d.bestFind = t.BestFindName;
            d.bestFindValue = t.bestFindValue;
        }
        catch (Exception e) { Debug.LogException(e); }
        try
        {
            foreach (var w in WorkOrders.Today)
                d.orders.Add((w.Text, w.done ? "DONE" : w.failed ? "MISSED" : WorkOrders.ProgressText(w), w.done ? 1 : w.failed ? 2 : 0));
        }
        catch (Exception e) { Debug.LogException(e); }
        try
        {
            d.rank = Progression.Rank;
            d.next = Progression.Next;
            d.rankTitle = Progression.RankTitle(d.rank);
            d.rank01 = Progression.RankProgress01;
            d.rep = GameSession.Rep;
            d.repToNext = Progression.RepToNext;
            d.cleanDaysToNext = Progression.CleanDaysToNext;
        }
        catch (Exception e) { Debug.LogException(e); }
        return d;
    }

    /// <summary>A believable mid-game day for edit-mode previews.</summary>
    public static ShiftData Sample(bool guest = false)
    {
        var d = new ShiftData
        {
            city = 85f, bags = 23, net = 412.5f, finds = 2, rank = 7, next = 8, rankTitle = "Senior Bin Botherer",
            rank01 = 0.72f, rep = 1240f, repToNext = 160f, cleanDaysToNext = 1, guest = guest, hostName = "Nick",
            bestFind = "Grandma's Wedding Ring", bestFindValue = 340f,
            ownCashCents = 45250, ownBusiness = "RUBBISH CO.", cargoText = "YOUR TRUCK · OWN BUSINESS · 24/160 kg",
            municipalPolicy = "New-business grace: city fines are waived while you get started. Midnight inspections share city costs among independent businesses.",
        };
        d.orders.Add(("Collect 10 bags on time", "DONE", 1));
        d.orders.Add(("Find a Rare or better", "DONE", 1));
        d.orders.Add(("Keep Minivan Meadows ≥ 80% clean at midnight", "midnight", 0));
        return d;
    }
}

/// <summary>
/// The Esc timesheet's TODAY'S SHIFT page (Docs/UIPass.md §5.3): the city's cleanliness, three stat stickers (bags, net,
/// finds), today's Work Orders with ticks, and the rank bar with what the next rank still needs (progression clarity, user
/// item 2). A guest gets a friendly note instead (their own company stays safe at home).
/// </summary>
public sealed class ShiftCardView : MonoBehaviour
{
    TextMeshProUGUI cityText, bagsN, netN, findsN, rankTitle, rankLine, badgeN, bestText, jobText, unpaidText, financesText, municipalText;
    RectTransform bestRow, jobRow;
    Image cityDot, barFill;
    RectTransform barTrack, ordersRoot;
    readonly List<(Image box, Image mark, TextMeshProUGUI text, TextMeshProUGUI prog)> orderRows =
        new List<(Image, Image, TextMeshProUGUI, TextMeshProUGUI)>();
    float width;
    bool guest;
    public Selectable ScrollControl { get; private set; }

    /// <summary>Build the page into <paramref name="page"/> (top-left origin) and show <paramref name="d"/>.</summary>
    public static ShiftCardView Build(RectTransform page, ShiftData d)
    {
        var v = page.gameObject.AddComponent<ShiftCardView>();
        v.Make(page, d);
        v.Apply(d);
        return v;
    }

    void Make(RectTransform page, ShiftData d)
    {
        float pageWidth = Mathf.Max(400f, page.rect.width), pageHeight = Mathf.Max(200f, page.rect.height);
        var scroll = UiKit.ScrollList(page, new Vector2(pageWidth, pageHeight));
        PauseUi.TopLeft((RectTransform)scroll.transform, 0f, 0f, pageWidth, pageHeight);
        ScrollControl = scroll.verticalScrollbar;
        page = scroll.content;
        page.GetComponent<VerticalLayoutGroup>().enabled = false;
        page.GetComponent<ContentSizeFitter>().enabled = false;
        width = pageWidth - 24f;
        guest = d.guest;
        float y = 0f;

        // heading + the city chip on the right
        var head = PauseUi.Text(page, "Heading", PT.TodaysShift, 26f, TextAlignmentOptions.MidlineLeft, true, UiTheme.Ink);
        head.characterSpacing = 5f;
        PauseUi.TopLeft(head.rectTransform, 0f, y, 330f, 44f);
        var chip = PauseUi.TopLeft(FeelUi.Rect("CityChip", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), width - 380f, y + 2f, 380f, 42f);
        FeelUi.Img(chip, "Body", FeelUi.Rounded, UiTheme.Hex(0xFFFDF6), 0.3f);
        FeelUi.Img(chip, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.3f), 0.3f);
        cityDot = FeelUi.Img(chip, "Dot", FeelUi.Circle, Color.green);
        UiKit.Place(cityDot.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(24f, 0f), new Vector2(20f, 20f));
        cityText = PauseUi.Text(chip, "City", "", 22f, TextAlignmentOptions.MidlineLeft, false, UiTheme.Ink);
        UiKit.Inset(cityText.rectTransform, 42f, 0f, 10f, 0f);
        y += 56f;

        municipalText = PauseUi.Text(page, "City inspection policy", "", 21f, TextAlignmentOptions.TopLeft, false, UiTheme.InkSoft);
        municipalText.textWrappingMode = TextWrappingModes.Normal;
        municipalText.enableAutoSizing = true;
        municipalText.fontSizeMin = 18f;
        municipalText.fontSizeMax = 21f;
        PauseUi.TopLeft(municipalText.rectTransform, 4f, y, width - 8f, 102f);
        y += 110f;

        {
            jobRow = PauseUi.TopLeft(FeelUi.Rect("YourJob", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y, width, 142f);
            FeelUi.Img(jobRow, "Body", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Go, 0.15f), 0.35f);
            jobText = PauseUi.Text(jobRow, "Employer", "", 27f, TextAlignmentOptions.MidlineLeft, true, UiTheme.Ink);
            PauseUi.TopLeft(jobText.rectTransform, 18f, 5f, width - 36f, 37f);
            jobText.overflowMode = TextOverflowModes.Ellipsis;
            jobText.enableAutoSizing = true;
            jobText.fontSizeMin = 20f;
            jobText.fontSizeMax = 27f;
            unpaidText = PauseUi.Text(jobRow, "Unpaid", "", 22f, TextAlignmentOptions.MidlineLeft, true, UiTheme.StampRed);
            PauseUi.TopLeft(unpaidText.rectTransform, 18f, 43f, width - 36f, 34f);
            unpaidText.enableAutoSizing = true;
            unpaidText.fontSizeMin = 17f;
            unpaidText.fontSizeMax = 22f;
            financesText = PauseUi.Text(jobRow, "Your cash and working cargo", "", 20f, TextAlignmentOptions.TopLeft, false, UiTheme.Ink);
            financesText.textWrappingMode = TextWrappingModes.Normal;
            PauseUi.TopLeft(financesText.rectTransform, 18f, 83f, width - 36f, 54f);
            y += 152f;
        }

        if (d.guest)
        {
            var note = PauseUi.TopLeft(FeelUi.Rect("GuestNote", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y, width, 200f);
            note.localRotation = Quaternion.Euler(0f, 0f, -0.6f);
            FeelUi.Img(note, "Body", FeelUi.Rounded, UiTheme.PaperDark, 0.3f);
            FeelUi.Img(note, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.3f);
            var t = PauseUi.Text(note, "Text", string.Format(PT.GuestShiftNote, d.hostName), 24f, TextAlignmentOptions.MidlineLeft, false, UiTheme.Ink);
            t.textWrappingMode = TextWrappingModes.Normal;
            t.enableAutoSizing = true;
            t.fontSizeMin = 19f;
            t.fontSizeMax = 24f;
            UiKit.Inset(t.rectTransform, 110f, 12f, 24f, 12f);
            var bag = FeelUi.Img(note, "Bag", UiKit.Sprites.Bag, UiTheme.Ink);
            UiKit.Place(bag.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(56f, 0f), new Vector2(64f, 64f));
            page.sizeDelta = new Vector2(0f, y + 210f);
            return;
        }

        // three stat stickers
        float gap = 14f, bw = (width - gap * 2f) / 3f, bh = 92f;
        bagsN = StatBox(page, "Bags", PT.Bags, UiKit.Sprites.Bag, 0, bw, bh, y, -1.2f);
        netN = StatBox(page, "Net", PT.Net, null, 1, bw, bh, y, 0.8f);
        findsN = StatBox(page, "Finds", PT.Finds, FeelUi.Star, 2, bw, bh, y, -0.5f);
        y += bh + 14f;

        // Work Orders
        var woHead = PauseUi.Text(page, "WorkOrdersHeading", PT.WorkOrdersHeading, 22f, TextAlignmentOptions.BottomLeft, true, UiTheme.StampRed);
        woHead.characterSpacing = 4f;
        PauseUi.TopLeft(woHead.rectTransform, 0f, y, width, 32f);
        PauseUi.Rule(page, y + 36f, width);
        y += 42f;
        ordersRoot = PauseUi.TopLeft(FeelUi.Rect("Orders", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y, width, 3 * 42f);
        y += 3 * 42f + 10f;

        // rank: badge, title, bar, what's next
        var badge = PauseUi.TopLeft(FeelUi.Rect("Badge", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y + 4f, 76f, 76f);
        var bsh = FeelUi.Img(badge, "Shadow", FeelUi.Circle, UiTheme.Shadow);
        bsh.rectTransform.offsetMin = bsh.rectTransform.offsetMax = new Vector2(3f, -4f);
        FeelUi.Img(badge, "Disc", FeelUi.Circle, UiTheme.Gold);
        FeelUi.Img(badge, "Rim", UiKit.Sprites.Ring, UiTheme.Ink);
        badgeN = UiKit.InkLabel(badge, "N", "", 34f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        rankTitle = PauseUi.Text(page, "RankTitle", "", 24f, TextAlignmentOptions.MidlineLeft, true, UiTheme.Ink);
        PauseUi.TopLeft(rankTitle.rectTransform, 92f, y, width - 92f, 34f);
        barTrack = PauseUi.TopLeft(FeelUi.Rect("Bar", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 92f, y + 38f, width - 92f, 22f);
        FeelUi.Img(barTrack, "Track", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Ink, 0.16f), 0.25f);
        var fillArea = FeelUi.Stretch("Fill", barTrack);
        barFill = FeelUi.Img(fillArea, "Body", FeelUi.Rounded, UiTheme.Tangerine, 0.25f);
        FeelUi.Img(barTrack, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.5f), 0.25f);
        rankLine = PauseUi.Text(page, "RankLine", "", 20f, TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Normal);
        PauseUi.TopLeft(rankLine.rectTransform, 92f, y + 62f, width - 92f, 30f);
        y += 104f;

        // today's best find, as a little "KEEP" tag (only when there is one and the page has room)
        {
            bestRow = PauseUi.TopLeft(FeelUi.Rect("BestFind", page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y, width, 44f);
            bestRow.localRotation = Quaternion.Euler(0f, 0f, 0.5f);
            FeelUi.Img(bestRow, "Body", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Gold, 0.35f), 0.3f);
            FeelUi.Img(bestRow, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.3f), 0.3f);
            var star = FeelUi.Img(bestRow, "Star", FeelUi.Star, UiTheme.Hex(0xE0A800));
            UiKit.Place(star.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(26f, 0f), new Vector2(30f, 30f));
            bestText = PauseUi.Text(bestRow, "Text", "", 22f, TextAlignmentOptions.MidlineLeft, false, UiTheme.Ink);
            UiKit.Inset(bestText.rectTransform, 50f, 0f, 14f, 0f);
        }
        page.sizeDelta = new Vector2(0f, y + 52f);
    }

    TextMeshProUGUI StatBox(RectTransform page, string name, string caption, Sprite icon, int i, float w, float h, float y, float tilt)
    {
        var box = PauseUi.TopLeft(FeelUi.Rect(name, page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), i * (w + 14f), y, w, h);
        box.localRotation = Quaternion.Euler(0f, 0f, tilt);
        var sh = FeelUi.Img(box, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.2f), 0.3f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(3f, -4f);
        FeelUi.Img(box, "Body", FeelUi.Rounded, UiTheme.Hex(0xFFFDF6), 0.3f);
        FeelUi.Img(box, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.35f), 0.3f);
        float left = 18f;
        if (icon != null)
        {
            var ic = FeelUi.Img(box, "Icon", icon, i == 2 ? UiTheme.Gold : UiTheme.Ink);
            UiKit.Place(ic.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(38f, 0f), new Vector2(46f, 46f));
            if (i == 2)
            {
                var rim = FeelUi.Img(box, "IconRim", icon, UiTheme.Ink);
                UiKit.Place(rim.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(38f, 0f), new Vector2(52f, 52f));
                rim.transform.SetSiblingIndex(ic.transform.GetSiblingIndex());
            }
            left = 70f;
        }
        else
        {
            var dollar = UiKit.InkLabel(box, "Icon", "$", 46f, UiTheme.Go, TextAlignmentOptions.Center, true);
            UiKit.Place(dollar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(38f, 2f), new Vector2(46f, 56f));
            left = 70f;
        }
        var n = UiKit.InkLabel(box, "Number", "0", 40f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft, true);
        n.enableAutoSizing = true;
        n.fontSizeMax = 40f;
        n.fontSizeMin = 24f;
        UiKit.Inset(n.rectTransform, left, 30f, 10f, 8f);
        var c = UiKit.InkLabel(box, "Caption", caption, 18f, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, false);
        c.characterSpacing = 4f;
        UiKit.Inset(c.rectTransform, left, 6f, 10f, h - 34f);
        return n;
    }

    /// <summary>Show <paramref name="d"/> (cheap: texts, the bar and the order rows).</summary>
    public void Apply(ShiftData d)
    {
        if (d == null) return;
        if (jobRow != null)
        {
            if (d.employer != null)
            {
                jobText.text = $"EMPLOYER: {d.employer}";
                unpaidText.text = Payroll.PayText(d.wageCents, d.sharePercent) +
                    (d.unpaidCents > 0 ? $" · OWED {Economy.Money(d.unpaidCents / 100f)} — job still active" : $" · Paid to you: {Economy.Money(d.paidCents / 100f)}");
                unpaidText.color = d.unpaidCents > 0 ? UiTheme.StampRed : UiTheme.InkSoft;
            }
            else
            {
                jobText.text = "SELF-EMPLOYED · NO EMPLOYER";
                unpaidText.text = "Joining Town does not hire you. Accept a job offer to work for someone.";
                unpaidText.color = UiTheme.InkSoft;
            }
            financesText.text = $"YOUR CASH: {Economy.Money(d.ownCashCents / 100f)} · {d.ownBusiness}\nWORKING CARGO: {d.cargoText}";
        }
        if (municipalText != null) municipalText.text = d.municipalPolicy ?? "";
        if (cityText != null)
        {
            int pct = Mathf.RoundToInt(d.city);
            cityText.text = string.Format(PT.CityLine, pct, FeelWorld.CleanlinessWord(d.city));
            cityDot.color = FeelWorld.CleanlinessColor(d.city);
        }
        if (guest || bagsN == null) return;
        bagsN.text = d.bags.ToString("N0");
        netN.text = (d.net >= 0f ? "+" : "") + Economy.Money(d.net);
        netN.color = d.net < 0f ? UiTheme.StampRed : UiTheme.Ink;
        findsN.text = d.finds.ToString("N0");

        while (orderRows.Count < Mathf.Max(1, d.orders.Count) && orderRows.Count < 3) AddOrderRow(orderRows.Count);
        for (int i = 0; i < orderRows.Count; i++)
        {
            var r = orderRows[i];
            bool has = i < d.orders.Count;
            r.box.gameObject.SetActive(has);
            r.prog.gameObject.SetActive(has);
            if (!has)
            {
                r.text.text = i == 0 ? PT.NoWorkOrders : "";
                r.text.color = UiTheme.InkSoft;
                continue;
            }
            var (text, progress, state) = d.orders[i];
            r.mark.enabled = state != 0;
            r.mark.sprite = state == 2 ? UiKit.Sprites.Cross : UiKit.Sprites.Check;
            r.mark.color = state == 2 ? UiTheme.StampRed : UiTheme.Go;
            r.text.color = state == 0 ? UiTheme.Ink : UiTheme.InkSoft;
            float tw = r.text.rectTransform.rect.width > 10f ? r.text.rectTransform.rect.width : width - 48f - 130f;
            r.text.text = FeelUi.Fit(r.text, text, tw);
            r.prog.text = progress;
            r.prog.color = state == 1 ? UiTheme.Go : state == 2 ? UiTheme.StampRed : UiTheme.InkSoft;
        }

        badgeN.text = d.rank.ToString();
        rankTitle.text = FeelUi.Fit(rankTitle, string.Format(PT.RankLabel, d.rank) + " · " + d.rankTitle, width - 92f);
        var fill = barFill.rectTransform;
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(Mathf.Clamp01(d.next == 0 ? 1f : d.rank01), 1f);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        barFill.enabled = d.rank01 > 0.01f || d.next == 0;
        string rep = string.Format(PT.RepLine, Mathf.FloorToInt(d.rep).ToString("N0"));
        string need;
        if (d.next == 0) need = PT.TopRank;
        else
        {
            var parts = new List<string>(2);
            if (d.repToNext > 0f) parts.Add(Mathf.CeilToInt(d.repToNext).ToString("N0") + " Rep");
            if (d.cleanDaysToNext > 0) parts.Add(string.Format(PT.CleanDays, d.cleanDaysToNext, d.cleanDaysToNext == 1 ? "" : "s"));
            need = parts.Count == 0 ? PT.PromotionAtMidnight : string.Format(PT.NeedForRank, string.Join(" + ", parts), d.next);
        }
        rankLine.text = FeelUi.Fit(rankLine, rep + " · " + need, width - 92f);
        if (bestRow != null)
        {
            bool has = !string.IsNullOrEmpty(d.bestFind);
            bestRow.gameObject.SetActive(has);
            if (has) bestText.text = FeelUi.Fit(bestText, string.Format(PT.BestFind, d.bestFind, Economy.Money(d.bestFindValue)), width - 64f);
        }
    }

    void AddOrderRow(int i)
    {
        const float H = 42f;
        var row = PauseUi.TopLeft(FeelUi.Rect("Order " + i, ordersRoot, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, i * H, width, H);
        var box = FeelUi.Img(row, "Box", FeelUi.Rounded, UiTheme.Hex(0xFFFDF6), 0.2f);
        UiKit.Place(box.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(2f, 0f), new Vector2(30f, 30f));
        FeelUi.Img(box.transform, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.7f), 0.2f);
        var mark = FeelUi.Img(box.transform, "Mark", UiKit.Sprites.Check, UiTheme.Go);
        mark.rectTransform.offsetMin = new Vector2(-2f, -2f);
        mark.rectTransform.offsetMax = new Vector2(8f, 8f); // the tick overshoots the box, like a pen
        var text = PauseUi.Text(row, "Text", "", 22f, TextAlignmentOptions.MidlineLeft, false, UiTheme.Ink);
        UiKit.Inset(text.rectTransform, 48f, 0f, 130f, 0f);
        var prog = PauseUi.Text(row, "Progress", "", 20f, TextAlignmentOptions.MidlineRight, true, UiTheme.InkSoft);
        UiKit.Place(prog.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(120f, H));
        orderRows.Add((box, mark, text, prog));
    }
}

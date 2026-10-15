using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The contract explained (Docs/Campaign.md §14.8): a short stack of paper cards the first time a business meets a contract
/// (three cards: what the district is, the old trash and the bar, the hold and its countdown), and one card for every contract
/// after that. The two middle cards carry a picture of the HUD bar as it will look, because the real HUD is hidden behind any
/// screen that locks the game. Next / Back / Skip (Esc skips, Enter is Next). Shown by <see cref="Tutorial"/>.
/// </summary>
public sealed class ContractIntroScreen : UiScreen
{
    const float CardW = 1000f, CardH = 700f, X = 56f;
    static ContractIntroScreen current;
    public static bool IsShowing => current != null && current.IsOpen;

    enum Picture { None, OldTrash, Hold }
    struct Page { public string kicker, title, body; public Picture picture; }

    Page[] pages;
    int page;
    float openedAt;
    bool preview, destroying, finished;
    Action<bool> done;
    string contractId;

    RectTransform card, pictureOld, pictureHold;
    TextMeshProUGUI kicker, title, body, dots;
    UiButton next, back, skip;
    Image dim;

    /// <summary>Open the cards for <paramref name="c"/>. <paramref name="first"/>: the long version. <paramref name="onDone"/>(skipped).</summary>
    public static bool Show(CampaignContract c, bool first, Action<bool> onDone)
    {
        if (!Application.isPlaying || Application.isBatchMode || c == null) return false;
        if (current != null && current.IsOpen) return false;
        var screen = Make(c, first, false, 0);
        screen.done = onDone;
        current = screen;
        screen.Open(true);
        Metrics.Begin("tutorial_cards").Str("dist", c.districtId).Bool("first", first).Int("pages", screen.pages.Length).End();
        return true;
    }

    /// <summary>The cards laid out as they would be on <paramref name="pageIndex"/>, without opening anything (screenshots).</summary>
    public static ContractIntroScreen Preview(CampaignContract c, bool first, int pageIndex)
    {
        var screen = Make(c, first, true, pageIndex);
        screen.Refresh();
        return screen;
    }

    /// <summary>Size the card to the screen right now (also called by a preview whose canvas was just resized).</summary>
    public void Refresh()
    {
        var root = (RectTransform)transform;
        float size = Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / CardW, (root.rect.height - 36f) / CardH));
        card.localScale = Vector3.one * size;
    }

    /// <summary>Close any card on screen without answering (the contract ended, a scene change).</summary>
    public static void Dismiss()
    {
        if (current != null && current.IsOpen) current.Finish(true);
    }

    static ContractIntroScreen Make(CampaignContract c, bool first, bool preview, int pageIndex)
    {
        var canvas = UiKit.Canvas("[ContractIntro]", UiLayers.Dialog - 2);
        var screen = canvas.gameObject.AddComponent<ContractIntroScreen>();
        screen.preview = preview;
        screen.contractId = c.districtId;
        screen.pages = Pages(c, first);
        screen.Build(pageIndex, c);
        return screen;
    }

    /// <summary>The words on every card (tests).</summary>
    public static string[] Words(CampaignContract c, bool first)
    {
        var pages = Pages(c, first);
        var words = new string[pages.Length];
        for (int i = 0; i < pages.Length; i++) words[i] = pages[i].kicker + "\n" + pages[i].title + "\n" + pages[i].body;
        return words;
    }

    // ------------------------------------------------------------------ the words

    static Page[] Pages(CampaignContract c, bool first)
    {
        var d = c.District;
        var next = c.Unlocks;
        int line = CampaignTuning.ThresholdPct;
        string hold = CampaignRules.FormatLeft(CampaignTuning.HoldHours);
        int realMinutes = Mathf.RoundToInt((float)(CampaignTuning.HoldHours * GameClock.SecondsPerGameHour / 60f));
        if (!first)
        {
            string extra = c.rateMult > 1.01f ? $" Careful: its houses put trash out {Mathf.RoundToInt((c.rateMult - 1f) * 100f)}% more often." : "";
            if (!string.IsNullOrEmpty(c.note)) extra += " " + c.note;
            return new[]
            {
                new Page
                {
                    kicker = "NEW CONTRACT", title = d.name.ToUpperInvariant(),
                    body = $"{d.name} is a mess too: {c.backlog} old bags are piled on its curbs.{extra}\n\nSame rules. Clear the old trash, get the bar over {line}%, and hold it for {hold}. Then {next.name} is yours.",
                },
            };
        }
        return new[]
        {
            new Page
            {
                kicker = "YOUR FIRST CONTRACT", title = $"TAKE BACK {d.name.ToUpperInvariant()}",
                body = $"{d.name} is a MESS. GoldStar Premium Waste took the city's money, collected nothing, and let the raccoons move in.\n\nClean it up and the city will hand you {next.name}.",
            },
            new Page
            {
                kicker = "STEP 1", title = "CLEAR THE OLD TRASH",
                body = $"{c.backlog} old bags are piled on the curbs (look for the flies). Get in your truck, grab them, and haul them to your depot.\n\nThis is the CLEAN BAR in the corner of your screen. It fills as the heaps disappear.",
                picture = Picture.OldTrash,
            },
            new Page
            {
                kicker = "STEP 2", title = "THEN HOLD IT",
                body = $"With the old trash gone, get the bar past the {line}% line and KEEP it there. A countdown starts: {hold} of game time, about {realMinutes} real minutes, night included.\n\nHold it to zero and the contract is yours.",
                picture = Picture.Hold,
            },
        };
    }

    // ------------------------------------------------------------------ the cards

    void Build(int firstPage, CampaignContract c)
    {
        var root = (RectTransform)transform;
        dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.62f));
        dim.raycastTarget = true;                    // the dimmed world takes no clicks

        card = UiKit.Paper(root, new Vector2(CardW, CardH));
        card.localRotation = Quaternion.Euler(0f, 0f, 0.5f);

        const float W = CardW - 2f * X;
        kicker = Label(card, "Kicker", 26f, X, 36f, W, 32f, UiTheme.InkSoft, false);
        title = Label(card, "Title", 54f, X, 66f, W, 70f, UiTheme.Ink, true);
        body = Label(card, "Body", 30f, X, 148f, W, 250f, UiTheme.Ink, false);
        body.fontStyle = FontStyles.Normal;
        dots = Label(card, "Dots", 30f, X, CardH - 70f, 160f, 40f, UiTheme.InkSoft, false);

        pictureOld = HudMock(card, "OldTrash", false, c);
        pictureHold = HudMock(card, "Hold", true, c);

        next = UiKit.Button(card, "NEXT", Next, UiButtonStyle.Go);
        UiKit.Place(next.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-X, 36f), new Vector2(300f, 80f));
        next.Text.fontSizeMax = 38f;
        back = UiKit.Button(card, "Back", Back, UiButtonStyle.Paper);
        UiKit.Place(back.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-X - 320f, 44f), new Vector2(180f, 64f));
        back.Text.fontSizeMax = 26f;
        skip = UiKit.Button(card, "Skip", () => Finish(true), UiButtonStyle.Quiet);
        UiKit.Place(skip.Rect, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -20f), new Vector2(110f, 44f));
        skip.Text.fontSizeMax = 22f;
        UiKit.ChainVertical(new Selectable[] { next, back });
        openedAt = Time.unscaledTime;
        Show(firstPage);
        if (!preview) UiPop.On(card.gameObject).Play(true);
    }

    static TextMeshProUGUI Label(Transform parent, string name, float font, float x, float y, float w, float h, Color color, bool display)
    {
        var label = UiKit.InkLabel(parent, name, "", font, color, TextAlignmentOptions.TopLeft, display, FontStyles.Bold);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Min(UiTheme.MinText, font);
        label.fontSizeMax = font;
        UiKit.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
        return label;
    }

    /// <summary>
    /// A picture of the HUD's contract bar, drawn the way <see cref="FeelHudCity"/> draws it (the real one is hidden behind any
    /// screen that locks the game): the district and its percent, the bar with the goal zone and line, the line of instruction.
    /// <paramref name="hold"/>: the held state (over the line, a countdown and its thin bar) instead of the old-trash state.
    /// </summary>
    static RectTransform HudMock(Transform parent, string name, bool hold, CampaignContract c)
    {
        const float PanelW = 560f, PanelH = 118f, Inset = 14f, BarW = PanelW - 2f * Inset;
        var root = FeelUi.Rect(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(X + 6f, -438f), new Vector2(CardW - 2f * X, PanelH + 8f));
        var panel = FeelUi.Rect("Panel", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(PanelW, PanelH));
        panel.localRotation = Quaternion.Euler(0f, 0f, 0.8f);
        FeelUi.Panel(panel, FeelUi.PanelColor, 0.8f);

        float fill = hold ? 0.94f : 0.31f;
        int pct = Mathf.RoundToInt(fill * 100f);
        var fillColor = FeelWorld.CleanlinessColor(pct);
        var nameText = FeelUi.Text(panel, "Title", 27f, Color.white, TextAlignmentOptions.Left, FontStyles.Bold);
        nameText.textWrappingMode = TextWrappingModes.NoWrap;
        nameText.text = $"<color=#FFCC4D>{Campaign.HudName(c.District)}</color>  <color=#{ColorUtility.ToHtmlStringRGB(fillColor)}>{pct}%</color>";
        Place(nameText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -4f), new Vector2(PanelW - 32f, 32f));

        var barBg = FeelUi.Rect("Bar", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(Inset, -42f), new Vector2(BarW, 18f));
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.35f);
        var fillRt = FeelUi.Rect("Fill", barBg, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(3f, 3f), new Vector2((BarW - 6f) * fill, 12f));
        var fillImg = fillRt.gameObject.AddComponent<Image>();
        fillImg.sprite = FeelUi.Rounded; fillImg.color = fillColor; fillImg.raycastTarget = false;
        var zone = FeelUi.Rect("Zone", barBg, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(3f + (BarW - 6f) * CampaignTuning.Threshold, 3f),
                               new Vector2((BarW - 6f) * (1f - CampaignTuning.Threshold), 12f));
        var zi = zone.gameObject.AddComponent<Image>();
        zi.sprite = FeelUi.Rounded; zi.color = new Color(0.5f, 1f, 0.55f, hold ? 0.5f : 0.3f); zi.raycastTarget = false;
        var line = FeelUi.Rect("Line", barBg, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(3f + (BarW - 6f) * CampaignTuning.Threshold, 0f), new Vector2(3f, 28f));
        var li = line.gameObject.AddComponent<Image>();
        li.color = hold ? new Color(0.62f, 1f, 0.66f) : new Color(1f, 0.93f, 0.55f); li.raycastTarget = false;
        var goal = FeelUi.Text(barBg, "Goal", 15f, new Color(1f, 0.93f, 0.55f), TextAlignmentOptions.Center, FontStyles.Bold);
        goal.text = $"GOAL {CampaignTuning.ThresholdPct}%";
        goal.textWrappingMode = TextWrappingModes.NoWrap;
        Place(goal.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0f), new Vector2(3f + (BarW - 6f) * CampaignTuning.Threshold, 7f), new Vector2(120f, 18f));

        var instr = FeelUi.Text(panel, "Line", 20f, new Color(1f, 0.85f, 0.4f), TextAlignmentOptions.Left, FontStyles.Normal);
        instr.textWrappingMode = TextWrappingModes.NoWrap;
        instr.text = hold
            ? $"<b>HOLD</b>  <color=#7CFF8A>{CampaignRules.FormatLeft(CampaignTuning.HoldHours - 5.3)}</color> left"
            : $"<b>OLD TRASH</b>  <color=#FFB347>{Mathf.RoundToInt(c.backlog * 0.7f)}</color> of {c.backlog} bags left";
        Place(instr.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 8f), new Vector2(PanelW - 32f, 30f));
        if (hold)
        {
            var bg = FeelUi.Rect("HoldBg", panel, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 4f), new Vector2(PanelW - 32f, 4f));
            var bgi = bg.gameObject.AddComponent<Image>();
            bgi.color = new Color(0f, 0f, 0f, 0.5f); bgi.raycastTarget = false;
            var hf = FeelUi.Rect("HoldFill", bg, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2((PanelW - 32f) * (5.3f / (float)CampaignTuning.HoldHours), 4f));
            var hfi = hf.gameObject.AddComponent<Image>();
            hfi.color = new Color(0.49f, 1f, 0.54f); hfi.raycastTarget = false;
        }

        // what the picture is saying, to its right
        var note = UiKit.InkLabel(root, "Note", hold ? "Under the line,\nthe countdown\nstarts over." : "It stops short of the\nline until the last\nold bag is gone.",
                                  26f, UiTheme.InkSoft, TextAlignmentOptions.Left, false, FontStyles.Normal);
        note.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(note.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(PanelW + 40f, -2f), new Vector2(CardW - 2f * X - PanelW - 60f, PanelH));
        root.gameObject.SetActive(false);
        return root;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    void Show(int index)
    {
        page = Mathf.Clamp(index, 0, pages.Length - 1);
        var p = pages[page];
        kicker.text = p.kicker;
        title.text = p.title;
        body.text = p.body;
        pictureOld.gameObject.SetActive(p.picture == Picture.OldTrash);
        pictureHold.gameObject.SetActive(p.picture == Picture.Hold);
        bool last = page == pages.Length - 1;
        next.SetLabel(last ? (pages.Length == 1 ? "LET'S GO" : "LET'S CLEAN!") : "NEXT");
        back.gameObject.SetActive(page > 0);
        skip.gameObject.SetActive(!last);
        dots.text = pages.Length > 1 ? Dots() : "";
        if (preview) return;
        Metrics.Begin("tutorial_page").Str("dist", contractId).Int("page", page).Num("secs", Time.unscaledTime - openedAt, "0.0").End();
        UiTheme.Play(UiTheme.SfxOpen, 0.4f, 1.4f);
        if (next != null) UiKit.Focus(next.gameObject);
    }

    string Dots()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pages.Length; i++) sb.Append(i == page ? "●" : "○").Append(' ');
        return sb.ToString();
    }

    void Next() { if (page >= pages.Length - 1) Finish(false); else Show(page + 1); }
    void Back() { if (page > 0) Show(page - 1); }

    void Finish(bool skipped)
    {
        if (finished) return;
        finished = true;
        Metrics.Begin("tutorial_cards_end").Str("dist", contractId).Bool("skipped", skipped).Int("page", page).Num("secs", Time.unscaledTime - openedAt, "0.0").End();
        var cb = done; done = null;
        Close();
        try { cb?.Invoke(skipped); } catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ every frame

    void Update()
    {
        if (!IsOpen || preview) return;
        if (!Campaign.Display.Active || SceneController.IsTransitioning) { Finish(true); return; }
        Refresh();
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Next();
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.6f, 1.2f); }
    public override bool OnBack() { Finish(true); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }

    protected override void OnDestroy()
    {
        destroying = true;
        if (current == this) current = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => current = null;
}

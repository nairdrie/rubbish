using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Scavenger's own HUD pieces (uGUI on the UiKit / FeelUi kit, its own canvas just above the main HUD): the X-ray
/// plate, the casing card and the lock-pick bar. Presentation only: it shows what <see cref="ScavengerRunner"/> hands it.
/// Built once; also renders in an edit-mode preview (<see cref="CreatePreview"/>).
/// </summary>
public sealed class ScavengerHud
{
    static readonly Color Plate = new Color(0.04f, 0.13f, 0.17f, 0.93f), Scan = new Color(0.45f, 0.95f, 1f), Dim = new Color(1f, 1f, 1f, 0.28f);
    static readonly string[] CatNames = { "JUNK", "PLASTIC", "GLASS", "METAL", "PAPER", "HAZARD" };

    public Canvas Canvas { get; private set; }
    RectTransform root, xray, caseCard, pick;
    TextMeshProUGUI xrayTitle, xrayBest, xrayHint, caseTitle, caseBody, caseHint, pickCaption;
    readonly Image[] catTiles = new Image[ScavengerRules.XRayCats];
    readonly TextMeshProUGUI[] catCounts = new TextMeshProUGUI[ScavengerRules.XRayCats];
    Image star, pickZone, pickNeedle, pickTime;
    readonly Image[] pins = new Image[6];
    RectTransform pickBar;

    public void Build(Transform parent)
    {
        Canvas = UiKit.Canvas("[ScavengerHud]", UiLayers.Hud + 2, parent);
        var ray = Canvas.GetComponent<GraphicRaycaster>();
        if (ray != null) ray.enabled = false; // never eats a click
        root = (RectTransform)Canvas.transform;
        BuildXRay(); BuildCase(); BuildPick();
        HideAll();
    }

    /// <summary>
    /// Sit inside the main HUD's centre stage (HudLayout, top-down reference px; this canvas uses the same UI scale): the
    /// plate in its bottom-right corner, the casing card in its bottom-left, the pick bar across its lower middle. False
    /// when the main HUD is not in a walking-about mode (map, pause, laptop, sort station), where these stay hidden.
    /// </summary>
    public bool Layout()
    {
        var f = HudLayout.Layout;
        if (!f.valid || root == null) return true; // no HUD frame yet: keep the built positions
        if (f.mode != HudMode.Street && f.mode != HudMode.Depot) return false;
        LayoutIn(f.centerStage);
        return true;
    }

    /// <summary>Place the three pieces inside a centre-stage rect (top-down reference px).</summary>
    public void LayoutIn(Rect stage)
    {
        if (stage.width < 200f || stage.height < 200f) return;
        bool narrow = stage.width < xray.sizeDelta.x + caseCard.sizeDelta.x + 16f;
        PlaceTopDown(xray, new Vector2(Mathf.Max(stage.xMin, stage.xMax - xray.sizeDelta.x), stage.yMax - xray.sizeDelta.y));
        PlaceTopDown(caseCard, new Vector2(stage.xMin, stage.yMax - caseCard.sizeDelta.y - (narrow ? xray.sizeDelta.y + 12f : 0f)));
        PlaceTopDown(pick, new Vector2(stage.center.x - pick.sizeDelta.x * 0.5f, stage.yMin + stage.height * 0.45f));
    }

    static void PlaceTopDown(RectTransform rt, Vector2 topLeft)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
    }

    public void Destroy() { if (Canvas != null) FeelRunner.SafeDestroy(Canvas.gameObject); Canvas = null; }
    public void HideAll() { Show(xray, false); Show(caseCard, false); Show(pick, false); }
    static void Show(RectTransform rt, bool on) { if (rt != null && rt.gameObject.activeSelf != on) rt.gameObject.SetActive(on); }

    static TextMeshProUGUI Text(RectTransform parent, string name, float size, Color color, TextAlignmentOptions align, Vector2 pos, Vector2 box)
    {
        var t = FeelUi.Text(parent, name, size, color, align);
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(pos.x, -pos.y); rt.sizeDelta = box;
        t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = Mathf.Min(14f, size);
        return t;
    }

    // ------------------------------------------------------------------ x-ray plate

    void BuildXRay()
    {
        xray = FeelUi.Rect("XRay", root, new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(150f, 210f), new Vector2(452f, 158f));
        FeelUi.Panel(xray, Plate, 0.55f);
        var rim = FeelUi.Img(xray, "Rim", UiKit.Sprites.RoundedOutline, new Color(Scan.r, Scan.g, Scan.b, 0.55f), 0.55f);
        rim.rectTransform.offsetMin = new Vector2(3f, 3f); rim.rectTransform.offsetMax = new Vector2(-3f, -3f);
        xrayTitle = Text(xray, "Title", 22f, Scan, TextAlignmentOptions.TopLeft, new Vector2(16f, 9f), new Vector2(300f, 28f));
        xrayHint = Text(xray, "Hint", 19f, UiTheme.Gold, TextAlignmentOptions.TopRight, new Vector2(232f, 10f), new Vector2(206f, 26f));
        for (int i = 0; i < ScavengerRules.XRayCats; i++)
        {
            var tile = FeelUi.Rect("Cat " + CatNames[i], xray, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f + i * 70f, -42f), new Vector2(64f, 62f));
            catTiles[i] = FeelUi.Img(tile, "Tile", FeelUi.Rounded, ItemCategoryInfo.Color(ScavengerRules.XRayCategory(i)), 0.4f);
            catCounts[i] = Text(tile, "Count", 28f, Color.white, TextAlignmentOptions.Center, new Vector2(0f, 2f), new Vector2(64f, 36f));
            var label = Text(tile, "Name", 13f, Color.white, TextAlignmentOptions.Center, new Vector2(0f, 40f), new Vector2(64f, 18f));
            label.text = CatNames[i]; label.fontSizeMin = 9f;
        }
        var starRt = FeelUi.Rect("Star", xray, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -112f), new Vector2(34f, 34f));
        star = FeelUi.Img(starRt, "Icon", FeelUi.Star, UiTheme.Gold);
        xrayBest = Text(xray, "Best", 22f, Color.white, TextAlignmentOptions.MidlineLeft, new Vector2(58f, 112f), new Vector2(380f, 34f));
    }

    /// <summary>Show the plate. <paramref name="pending"/> = the host has not answered yet.</summary>
    public void ShowXRay(XRaySummary s, bool pending, string hint)
    {
        Show(xray, true);
        xrayTitle.text = pending ? "X-RAY  focusing..." : s.level >= 2 ? "HI-DEF X-RAY" : "X-RAY";
        xrayHint.text = hint ?? "";
        for (int i = 0; i < ScavengerRules.XRayCats; i++)
        {
            int n = pending ? 0 : s.Count(i);
            catCounts[i].text = pending ? "?" : n >= 15 ? "15+" : n.ToString();
            var c = ItemCategoryInfo.Color(ScavengerRules.XRayCategory(i));
            catTiles[i].color = n > 0 ? c : new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 0.75f);
            catCounts[i].color = n > 0 ? Color.white : Dim;
        }
        if (pending) { star.color = Dim; xrayBest.text = ""; return; }
        var def = s.level >= 2 && !string.IsNullOrEmpty(s.bestDefId) ? ItemCatalog.Get(s.bestDefId) : null;
        star.color = s.hasValuable ? (def != null ? RarityInfo.Color(def.rarity) : UiTheme.Gold) : Dim;
        if (!s.hasValuable) { xrayBest.text = "Just trash. Honest trash."; xrayBest.color = Dim; }
        else if (def != null) { xrayBest.text = RarityInfo.Label(def.rarity) + "  " + def.name; xrayBest.color = RarityInfo.Color(def.rarity); }
        else { xrayBest.text = "Something GOOD is in there."; xrayBest.color = UiTheme.Gold; }
        if (s.wanted) // a part this business pinned on the Callings tree is in the bag
        {
            xrayBest.text = s.hasValuable ? xrayBest.text + "  +  WANTED PART!" : "WANTED PART inside!";
            if (!s.hasValuable) xrayBest.color = UiTheme.Gold;
        }
    }
    public void HideXRay() => Show(xray, false);

    // ------------------------------------------------------------------ casing card

    void BuildCase()
    {
        caseCard = FeelUi.Rect("Casing", root, new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(120f, 210f), new Vector2(640f, 214f));
        FeelUi.Panel(caseCard, UiTheme.Plum, 0.55f);
        caseTitle = Text(caseCard, "Title", 26f, UiTheme.Gold, TextAlignmentOptions.TopLeft, new Vector2(20f, 12f), new Vector2(600f, 32f));
        caseBody = Text(caseCard, "Body", 20f, Color.white, TextAlignmentOptions.TopLeft, new Vector2(20f, 50f), new Vector2(600f, 116f));
        caseBody.textWrappingMode = TextWrappingModes.Normal; caseBody.lineSpacing = -6f;
        caseHint = Text(caseCard, "Hint", 21f, Scan, TextAlignmentOptions.BottomLeft, new Vector2(20f, 172f), new Vector2(600f, 30f));
    }

    public void ShowCase(string title, string body, string hint)
    {
        Show(caseCard, true);
        caseTitle.text = title ?? ""; caseBody.text = body ?? ""; caseHint.text = hint ?? "";
    }
    public void HideCase() => Show(caseCard, false);

    /// <summary>The casing in words. Counts are what you can make out from the doorstep, never who.</summary>
    public static string CaseText(CaseInfo info)
    {
        string home = info.awake > 0 ? $"{info.awake} up and about indoors" : info.asleep > 0 ? $"{info.asleep} in bed" : "nobody home";
        if (info.awake > 0 && info.asleep > 0) home += $", {info.asleep} in bed";
        string outside = info.street + info.players > 0 ? $"{info.street + info.players} in sight of the door" : "street's empty";
        if (info.dogs > 0) outside += info.dogs == 1 ? ", a dog in earshot" : $", {info.dogs} dogs in earshot";
        string loot = info.richTier >= 3 ? "Loaded." : info.richTier == 2 ? "Well off." : info.richTier == 1 ? "Comfortable." : "Not much to it.";
        string jumpy = info.suspicion >= ScavengerHost.WatchLetterAt ? "  (The street is jumpy.)" : "";
        return $"Inside: {home}.\nOutside: {outside}.\nLock gives: {info.lockOdds}%.   Seen: {info.risk}%.{jumpy}\nThe house: {loot}";
    }

    public static string CaseRefusal(CaseInfo info) => info.status switch
    {
        CaseStatus.CleanedOut => info.readyInDays <= 1 ? "Cleaned out. Give it a day to restock." : $"Cleaned out. Give it {info.readyInDays} days to restock.",
        CaseStatus.OnAlert => "They're on alert after last time. Try tomorrow.",
        CaseStatus.TooFar => "Get right up to the door.",
        CaseStatus.DistrictClosed => "Nobody in the Town has a permit for this district.",
        CaseStatus.NotAHouse => "Not that door. Houses only: never a shop, a depot or a truck.",
        CaseStatus.NoItch => "You don't have The Itch. Good for you.",
        _ => "Not right now.",
    };

    // ------------------------------------------------------------------ the pick bar

    void BuildPick()
    {
        pick = FeelUi.Rect("Pick", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(560f, 132f));
        FeelUi.Panel(pick, UiTheme.Plum, 0.55f);
        pickCaption = Text(pick, "Caption", 23f, Color.white, TextAlignmentOptions.Top, new Vector2(10f, 10f), new Vector2(540f, 30f));
        pickBar = FeelUi.Rect("Bar", pick, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(500f, 30f));
        FeelUi.Img(pickBar, "Track", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.35f);
        var zone = FeelUi.Rect("Zone", pickBar, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(100f, 30f));
        pickZone = FeelUi.Img(zone, "Fill", FeelUi.Rounded, UiTheme.Go, 0.35f);
        var needle = FeelUi.Rect("Needle", pickBar, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(8f, 44f));
        pickNeedle = FeelUi.Img(needle, "Fill", FeelUi.Rounded, Color.white, 0.3f);
        var time = FeelUi.Rect("Time", pick, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(500f, 8f));
        FeelUi.Img(time, "Track", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.45f), 0.3f);
        var fill = FeelUi.Rect("Fill", time, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(500f, 8f));
        pickTime = FeelUi.Img(fill, "Bar", FeelUi.Rounded, UiTheme.Gold, 0.3f);
        for (int i = 0; i < pins.Length; i++)
        {
            var pin = FeelUi.Rect("Pin " + i, pick, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2((i - (pins.Length - 1) * 0.5f) * 30f, 10f), new Vector2(22f, 22f));
            pins[i] = FeelUi.Img(pin, "Dot", FeelUi.Circle, Dim);
        }
    }

    /// <summary>needle / zone are 0..1 along the bar; time01 = how much of the attempt is left.</summary>
    public void ShowPick(float needle01, float zoneCenter01, float zoneWidth01, int pinsSet, int pinCount, int misses, float time01, string caption)
    {
        Show(pick, true);
        float w = pickBar.sizeDelta.x;
        var zone = (RectTransform)pickZone.transform.parent;
        zone.sizeDelta = new Vector2(Mathf.Max(12f, zoneWidth01 * w), 30f);
        zone.anchoredPosition = new Vector2(Mathf.Clamp01(zoneCenter01 - zoneWidth01 * 0.5f) * w, 0f);
        ((RectTransform)pickNeedle.transform.parent).anchoredPosition = new Vector2(Mathf.Clamp01(needle01) * w, 0f);
        ((RectTransform)pickTime.transform.parent).sizeDelta = new Vector2(Mathf.Clamp01(time01) * w, 8f);
        pickTime.color = time01 < 0.25f ? UiTheme.StampRed : UiTheme.Gold;
        pickCaption.text = caption ?? "";
        for (int i = 0; i < pins.Length; i++)
        {
            bool used = i < pinCount;
            pins[i].transform.parent.gameObject.SetActive(used);
            if (!used) continue;
            ((RectTransform)pins[i].transform.parent).anchoredPosition = new Vector2((i - (pinCount - 1) * 0.5f) * 30f, 10f);
            pins[i].color = i < pinsSet ? UiTheme.Go : Dim;
        }
        pickCaption.color = misses > 0 ? new Color(1f, 0.8f, 0.6f) : Color.white;
    }
    public void HidePick() => Show(pick, false);

    // ------------------------------------------------------------------ edit-mode preview (screenshots)

    /// <summary>All three pieces at once over a flat background, with sample data. No play mode, no session.</summary>
    public static ScavengerHud CreatePreview(Transform parent, Camera cam, int w, int h, XRaySummary sample, CaseInfo casing, float uiScale = -1f)
    {
        var hud = new ScavengerHud();
        hud.Build(parent);
        UiKit.PreviewCanvas(hud.Canvas, cam, w, h, uiScale);
        hud.ShowXRay(sample, false, "[N] CALL IT");
        hud.ShowCase("CASING  14 Maple St", CaseText(casing), "[E] Break in     walk away to bail");
        hud.ShowPick(0.62f, 0.55f, 0.22f, 1, 3, 1, 0.6f, "PICKING...  [E] on the green");
        // the main HUD's centre stage, roughly, for this screen: between the side columns, under the top bar, over the dock
        var size = UiScale.ReferenceSize(w, h, uiScale);
        hud.LayoutIn(Rect.MinMaxRect(Mathf.Min(430f, size.x * 0.22f), 144f, size.x - Mathf.Min(460f, size.x * 0.24f), size.y - 150f));
        return hud;
    }
}

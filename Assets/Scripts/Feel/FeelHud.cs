using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The runtime-built gameplay HUD (uGUI + TextMeshPro, ScreenSpaceOverlay). Every widget sits in a <see cref="HudLayout"/> zone
/// computed each frame from the canvas size in reference units (UI scale and aspect ratio included; Docs/UIPass.md §8):
/// top bar = cash, the day/night clock, the rank chip (ProgressionHud) and the truck gauge; left column = City Cleanliness,
/// finds and the rabies widget; right column = the route panel (and Head Office memos under it); the centre lane = toasts;
/// the bottom dock = the interaction prompt (<see cref="FeelHudPrompt"/>, TooltipUI forwards to it) over the hint chips
/// (<see cref="FeelHudHints"/>); bottom-left = the minimap (a nested canvas). Plus floating world text and the big treasure
/// reveal card. Modes (§8.3): Station (the first-person sort station) collapses the top bar to a slim pill and keeps the
/// bin signs clear; Laptop keeps cash and rabies; Paused (solo) and Map keep only toasts. Hidden while there is no local
/// player (menus). Never takes raycasts, so it can't block input. Built entirely in code: no prefab, no assets.
/// </summary>
public class FeelHud : MonoBehaviour
{
    /// <summary>Above the scene's TooltipUI canvas (0), below LoadingSystem (short.MaxValue).</summary>
    public const int SortingOrder = 100;

    /// <summary>Everything the HUD shows, gathered once per frame (live, or faked for the edit-mode preview).</summary>
    public struct HudData
    {
        public bool visible, blocked;
        public float cash;
        public int day, finds;
        public float load, cap;
        public string truckTitle, truckContext, truckBusinessId;
        public int truckDepotLot;
        public bool hasTruckContext, truckSyncing;
        public bool hasRoute, complete, hasTarget;
        public string routeName, nextLabel;
        public int cleared, total;
        public float targetDist, arrowAngle;

        // ---- living world (all optional: default values = legacy look)
        /// <summary>Hour of day 0..24 for the clock widget.</summary>
        public float hours;
        /// <summary>Route panel runs as the live planner over random curb trash (else legacy "Stops x/y").</summary>
        public bool live;
        public bool truckFull, allClean, targetIsDepot;
        public int curbBags, lateBags, houses, targetBags;
        public float targetAge;
        /// <summary>Planned stops, [0] = the arrow's house (max 3).</summary>
        public RouteRow[] rows;
        /// <summary>City Cleanliness meter (0..100).</summary>
        public bool hasCity;
        public float city;
        /// <summary>Rabies widget: infected, hours since infection, hours left before passing out, pass-out hour of day.</summary>
        public bool rabies;
        public float rabiesAge, rabiesHoursLeft, rabiesPassOutHour;

        // ---- UI pass (§8.3): the mode, and whether a pause would freeze (solo) or not (co-op)
        public HudMode mode;
        public bool solo;
        /// <summary>The town map anchor is active (the Neighborhood): the minimap may show.</summary>
        public bool hasMap;
    }

    Canvas canvas;
    CanvasScaler scaler;
    RectTransform root, gameplay, top, rabiesHolder, dock;
    CanvasGroup gameplayGroup, topGroup, rabiesGroup, dockGroup, pillGroup;
    bool built;
    float previewUiScale = -1f;

    // cash
    RectTransform cashPanel, cashTextRt, deltaPill;
    TextMeshProUGUI cashText, deltaText;
    Image deltaBody;
    float shownCash, cashScale = 1f, cashScaleVel, cashFlash, deltaAge = 99f, deltaSum, tickTimer, countStart;
    Color cashFlashColor = Color.white;
    bool cashInit, countTicks;
    string cashShownStr;

    /// <summary>Left column stacking gap (city, finds, rabies).</summary>
    const float ColumnGap = 12f, FindsHeight = 46f, FindsWidth = 330f;

    // finds
    RectTransform findsPanel;
    CanvasGroup findsGroup;
    TextMeshProUGUI findsText;
    int lastFinds = -1;
    float findsScale = 1f, findsScaleVel, findsAlpha, findsY = -1f, rabiesY = -1f;
    bool findsAllowed = true;

    // Small shared-world notices, separate from big reveal cards and centre-screen toasts.
    struct ActivityLine { public string text; public float until; }
    readonly List<ActivityLine> activityLines = new List<ActivityLine>(3);
    RectTransform activityPanel;
    CanvasGroup activityGroup;
    TextMeshProUGUI activityText;
    float activityAlpha;

    // truck gauge
    RectTransform truckPanel, barFill;
    Image barFillImg;
    TextMeshProUGUI truckKgText, truckTitleText, truckContextText, fullText;
    string shownTruckBusiness;
    bool shownTruckSyncing;
    float fillShown, fillVel, lastLoad = -1f, lastCap = -1f, wobble, truckScale = 1f, truckScaleVel;

    // station pill (cash + time)
    RectTransform pillPanel;
    TextMeshProUGUI pillText;
    string pillShown;

    // the bottom dock
    string hintWanted;
    FeelHudPrompt prompt;
    FeelHudHints hints;
    FeelHudHotbar hotbar;
    RectTransform heldItemCard;
    CanvasGroup heldItemGroup;
    Image heldItemAccent;
    TextMeshProUGUI heldItemName, heldItemDestination, heldItemBuilding;

    // widgets
    FeelHudToasts toasts;
    FeelHudFloaters floaters;
    FeelHudReveal reveal;
    FeelHudClock clock;
    FeelHudCity city;
    FeelHudRoute route;
    FeelHudRabies rabies;
    Minimap minimap;

    // layout memory (last frame's content, for this frame's layout)
    float lastDockWanted;
    HudLayout.Frame layout;

    /// <summary>The route panel (live planner): exposes the arrow's current house for debugging/tests.</summary>
    public FeelHudRoute Route => route;
    /// <summary>The interaction prompt (TooltipUI forwards here).</summary>
    public FeelHudPrompt Prompt => prompt;
    public FeelHudHints Hints => hints;
    public FeelHudToasts Toasts => toasts;
    public Minimap Minimap => minimap;

    /// <summary>The big treasure reveal card is up (other overlays, e.g. the promotion stamp, wait for it).</summary>
    public bool RevealShowing => reveal != null && reveal.Showing;

    // edit-mode preview
    bool preview;
    HudData previewData;
    Camera cameraOverride;

    // ------------------------------------------------------------------ creation

    /// <summary>Create the HUD canvas under parent (the Feel runner) and build its hierarchy.</summary>
    public static FeelHud Create(Transform parent)
    {
        var go = new GameObject("FeelHUD", typeof(RectTransform));
        go.layer = 5;
        if (parent != null) go.transform.SetParent(parent, false);
        var hud = go.AddComponent<FeelHud>();
        hud.Build();
        return hud;
    }

    /// <summary>Build the canvas hierarchy (idempotent). Works in edit mode for previews.</summary>
    public void Build()
    {
        if (built) return;
        built = true;

        canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 |
                                           AdditionalCanvasShaderChannels.Normal |
                                           AdditionalCanvasShaderChannels.Tangent;
        scaler = gameObject.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        if (Application.isPlaying) UiScale.Register(scaler); // Settings → Interface → UI scale
        // deliberately no GraphicRaycaster: the HUD never eats clicks

        root = (RectTransform)transform;

        floaters = new FeelHudFloaters();
        floaters.Build(FeelUi.Stretch("Floaters", root));

        // the minimap sits under everything else (its own nested canvas: its per-frame rebatch stays out of the HUD's)
        minimap = Minimap.Create(root);

        top = FeelUi.Stretch("Top", root);
        topGroup = Group(top);
        BuildCash();
        clock = new FeelHudClock();
        clock.Build(top, new Vector2(HudLayout.Margin + HudLayout.CashW + HudLayout.TopGap, -HudLayout.TopBarTop));
        BuildPill();

        gameplay = FeelUi.Stretch("Gameplay", root);
        gameplayGroup = Group(gameplay);
        city = new FeelHudCity();
        city.Build(gameplay);
        BuildFinds();
        BuildActivity();
        BuildTruck();
        route = new FeelHudRoute();
        route.Build(gameplay);

        dock = FeelUi.Stretch("Dock", root);
        dockGroup = Group(dock);
        hints = new FeelHudHints();
        hints.Build(dock);
        prompt = new FeelHudPrompt();
        prompt.Build(dock);
        hotbar = new FeelHudHotbar();
        hotbar.Build(dock);
        BuildHeldItemCard();

        // on its own holder (not gameplay) so it stays up while browsing the shop laptop for a cure
        rabiesHolder = FeelUi.Stretch("RabiesHolder", root);
        rabiesGroup = Group(rabiesHolder);
        rabies = new FeelHudRabies();
        rabies.Build(rabiesHolder);

        toasts = new FeelHudToasts();
        toasts.Build(FeelUi.Stretch("Toasts", root));

        reveal = new FeelHudReveal();
        reveal.Build(FeelUi.Rect("Reveal", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(760f, 420f)));

        if (Application.isPlaying)
        {
            GameSession.CashChanged += OnCashChanged;
            GameSession.DayStarted += OnDayStarted;
        }
        canvas.enabled = false;
    }

    static CanvasGroup Group(RectTransform rt)
    {
        var g = rt.gameObject.AddComponent<CanvasGroup>();
        g.interactable = false;
        g.blocksRaycasts = false;
        return g;
    }

    void BuildHeldItemCard()
    {
        heldItemCard = FeelUi.Rect("StationHeldItem", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(540f, 112f));
        heldItemGroup = Group(heldItemCard);
        heldItemGroup.alpha = 0f;
        FeelUi.Panel(heldItemCard, new Color(0.10f, 0.09f, 0.15f, 0.96f), 0.55f);
        var accent = FeelUi.Rect("CategoryStripe", heldItemCard, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(9f, 10f), new Vector2(8f, 92f));
        heldItemAccent = FeelUi.Img(accent, "Color", FeelUi.Rounded, FeelUi.Gold, 0.2f);
        heldItemName = FeelUi.Text(heldItemCard, "ItemName", 34f, Color.white, TextAlignmentOptions.Left);
        Place(heldItemName.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(31f, -7f), new Vector2(488f, 50f));
        heldItemName.textWrappingMode = TextWrappingModes.NoWrap;
        heldItemName.overflowMode = TextOverflowModes.Ellipsis;
        heldItemDestination = FeelUi.Text(heldItemCard, "Destination", 27f, FeelUi.Gold, TextAlignmentOptions.Left);
        Place(heldItemDestination.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(31f, -55f), new Vector2(488f, 44f));
        heldItemBuilding = FeelUi.Text(heldItemCard, "BuildMaterialsAlternative", 23f, FeelUi.Gold, TextAlignmentOptions.Left);
        heldItemBuilding.enableAutoSizing = true;
        heldItemBuilding.fontSizeMin = 18f;
        heldItemBuilding.fontSizeMax = 23f;
        Place(heldItemBuilding.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(31f, -101f), new Vector2(488f, 34f));
    }

    static string HeldDestination(ItemCategory category) => category switch
    {
        ItemCategory.Valuable => "KEEP CRATE  •  TREASURE",
        ItemCategory.Junk => "COMPACTOR  •  JUNK",
        ItemCategory.Hazard => "HAZARD BARREL",
        _ => ItemCategoryInfo.DisplayName(category).ToUpperInvariant() + " BIN"
    };

    void TickHeldItem(HudMode mode, Rect dockRect, Vector2 screenSize, float dt, bool preview)
    {
        var held = !preview && mode == HudMode.Station ? SortingKit.LocalHeld as LooseItem : null;
        bool show = held != null && held.Revealed;
        heldItemGroup.alpha = Mathf.MoveTowards(heldItemGroup.alpha, show ? 1f : 0f, dt * 10f);
        if (!show) return;
        float width = Mathf.Min(540f, screenSize.x - 2f * HudLayout.Margin);
        bool building = WorkshopMenu.CanSaveHeld(held);
        float height = building ? 148f : 112f;
        heldItemCard.sizeDelta = new Vector2(width, height);
        // The first-person item model sits low-right; keep the label to its left instead of covering the object.
        float x = Mathf.Max(HudLayout.Margin, (screenSize.x - width) * 0.5f - 110f);
        float y = Mathf.Max(HudLayout.BinBandBottom + 18f, dockRect.yMin - height - 12f);
        HudLayout.PlaceAt(heldItemCard, new Vector2(x, y));
        Color color = ItemCategoryInfo.Color(held.Category);
        heldItemAccent.color = color;
        heldItemName.text = held.DisplayName;
        heldItemDestination.text = "GOES IN: " + HeldDestination(held.Category);
        heldItemDestination.color = color;
        heldItemBuilding.gameObject.SetActive(building);
        if (building) heldItemBuilding.text = WorkshopMenu.BuildMaterialsPrompt + " · save instead of sell";
    }

    void OnDestroy()
    {
        GameSession.CashChanged -= OnCashChanged;
        GameSession.DayStarted -= OnDayStarted;
        if (scaler != null) UiScale.Unregister(scaler);
        minimap?.Dispose();
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    void BuildCash()
    {
        // H7: 360×104 (was 390×122, mostly for an empty delta line); the delta is a pill that pops out of the panel
        cashPanel = FeelUi.Rect("Cash", top, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(HudLayout.Margin, -HudLayout.TopBarTop),
                                new Vector2(HudLayout.CashW, HudLayout.CashH));
        cashPanel.localRotation = Quaternion.Euler(0f, 0f, 1.5f);
        FeelUi.Panel(cashPanel, FeelUi.PanelColor);

        cashText = FeelUi.Text(cashPanel, "CashText", 64f, Color.white, TextAlignmentOptions.Left);
        cashTextRt = cashText.rectTransform;
        Place(cashTextRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(22f, 2f), new Vector2(HudLayout.CashW - 40f, 72f));

        deltaPill = FeelUi.Rect("Delta", cashPanel, new Vector2(1f, 0f), new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(160f, 34f));
        deltaBody = FeelUi.Img(deltaPill, "Body", FeelUi.Rounded, new Color(0.11f, 0.09f, 0.17f, 0.95f), 0.5f);
        deltaText = FeelUi.Text(deltaPill, "DeltaText", 24f, FeelUi.GoodGreen, TextAlignmentOptions.Center);
        deltaText.text = "";
        deltaPill.gameObject.SetActive(false);
    }

    void BuildPill()
    {
        pillPanel = FeelUi.Rect("StationPill", top, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(HudLayout.PillW, HudLayout.PillH));
        pillGroup = Group(pillPanel);
        pillGroup.alpha = 0f;
        FeelUi.Panel(pillPanel, FeelUi.PanelColor, 0.6f, new Vector2(4f, -5f));
        pillText = FeelUi.Text(pillPanel, "Text", 32f, Color.white, TextAlignmentOptions.Center);
    }

    void BuildFinds()
    {
        findsPanel = FeelUi.Rect("Finds", gameplay, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(FindsWidth, FindsHeight));
        findsPanel.localRotation = Quaternion.Euler(0f, 0f, -1f);
        findsGroup = findsPanel.gameObject.AddComponent<CanvasGroup>();
        findsGroup.alpha = 0f;
        FeelUi.Panel(findsPanel, new Color(0.35f, 0.16f, 0.55f, 0.92f), 0.7f, new Vector2(4f, -4f));
        findsText = FeelUi.Text(findsPanel, "FindsText", 25f, Color.white, TextAlignmentOptions.Center);
    }

    void BuildActivity()
    {
        activityPanel = FeelUi.Rect("ActivityFeed", gameplay, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(330f, 46f));
        activityGroup = Group(activityPanel);
        activityGroup.alpha = 0f;
        FeelUi.Panel(activityPanel, new Color(0.11f, 0.13f, 0.16f, 0.82f), 0.35f);
        activityText = FeelUi.Text(activityPanel, "ActivityText", 17f, new Color(0.86f, 0.9f, 0.95f), TextAlignmentOptions.TopLeft, FontStyles.Normal);
        activityText.rectTransform.offsetMin = new Vector2(12f, 6f);
        activityText.rectTransform.offsetMax = new Vector2(-12f, -7f);
        activityText.textWrappingMode = TextWrappingModes.NoWrap;
        activityText.overflowMode = TextOverflowModes.Truncate;
    }

    void BuildTruck()
    {
        truckPanel = FeelUi.Rect("Truck", gameplay, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(HudLayout.TruckW, HudLayout.TruckH));
        truckPanel.localRotation = Quaternion.Euler(0f, 0f, -1.2f);
        FeelUi.Panel(truckPanel, FeelUi.PanelColor);

        truckTitleText = FeelUi.Text(truckPanel, "Title", 25f, new Color(1f, 0.8f, 0.3f), TextAlignmentOptions.Left);
        truckTitleText.richText = false;
        truckTitleText.textWrappingMode = TextWrappingModes.NoWrap;
        truckTitleText.overflowMode = TextOverflowModes.Ellipsis;
        truckTitleText.rectTransform.anchorMin = new Vector2(0f, 1f);
        truckTitleText.rectTransform.anchorMax = Vector2.one;
        truckTitleText.rectTransform.offsetMin = new Vector2(20f, -36f);
        truckTitleText.rectTransform.offsetMax = new Vector2(-20f, -3f);
        truckTitleText.text = "YOUR TRUCK";

        truckContextText = FeelUi.Text(truckPanel, "Context", 18f, new Color(0.8f, 0.82f, 0.88f), TextAlignmentOptions.Left);
        truckContextText.textWrappingMode = TextWrappingModes.NoWrap;
        truckContextText.overflowMode = TextOverflowModes.Ellipsis;
        truckContextText.enableAutoSizing = true;
        truckContextText.fontSizeMin = 14f;
        truckContextText.fontSizeMax = 18f;
        truckContextText.rectTransform.anchorMin = new Vector2(0f, 1f);
        truckContextText.rectTransform.anchorMax = new Vector2(0.4f, 1f);
        truckContextText.rectTransform.offsetMin = new Vector2(20f, -64f);
        truckContextText.rectTransform.offsetMax = new Vector2(0f, -36f);

        truckKgText = FeelUi.Text(truckPanel, "Kg", 26f, Color.white, TextAlignmentOptions.Right);
        truckKgText.enableAutoSizing = true;
        truckKgText.textWrappingMode = TextWrappingModes.NoWrap;
        truckKgText.overflowMode = TextOverflowModes.Ellipsis;
        truckKgText.fontSizeMin = 18f;
        truckKgText.fontSizeMax = 26f;
        truckKgText.rectTransform.anchorMin = new Vector2(0.4f, 1f);
        truckKgText.rectTransform.anchorMax = Vector2.one;
        truckKgText.rectTransform.offsetMin = new Vector2(4f, -64f);
        truckKgText.rectTransform.offsetMax = new Vector2(-20f, -36f);

        var barBg = FeelUi.Rect("Bar", truckPanel, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(20f, 14f), new Vector2(HudLayout.TruckW - 40f, 30f));
        barBg.anchorMax = new Vector2(1f, 0f);
        barBg.offsetMin = new Vector2(20f, 14f);
        barBg.offsetMax = new Vector2(-20f, 30f);
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.5f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(4f, 4f);
        fillArea.offsetMax = new Vector2(-4f, -4f);
        barFillImg = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, new Color(0.3f, 0.9f, 0.35f), 0.4f);
        barFill = barFillImg.rectTransform;
        barFill.anchorMax = new Vector2(0f, 1f);

        fullText = FeelUi.Text(barBg, "Full", 24f, new Color(1f, 0.92f, 0.2f), TextAlignmentOptions.Center);
        fullText.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -6f);
        fullText.text = "FULL!";
        fullText.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ public API (via GameHUD)

    public void Toast(string text, ToastStyle style, float duration)
    {
        if (!built) Build();
        toasts.Show(text, style, duration);
    }

    public void FloatingText(Vector3 worldPos, string text, Color color, float scale)
    {
        if (!built) Build();
        floaters.Show(worldPos, text, color, scale);
    }

    public void SetContextHint(string text) => hintWanted = string.IsNullOrEmpty(text) ? null : text;

    /// <summary>The hint the dock is showing (tests).</summary>
    public string ContextHint => hintWanted;

    public void BigReveal(ItemInstance item)
    {
        if (!built) Build();
        reveal.Show(item);
    }

    public void Activity(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!built) Build();
        if (text.Length > 72) text = text.Substring(0, 71) + "…";
        if (activityLines.Count >= 3) activityLines.RemoveAt(0);
        activityLines.Add(new ActivityLine { text = text, until = Time.unscaledTime + 5f });
    }

    public void OnSceneChanged()
    {
        route?.OnSceneChanged();
        floaters?.Clear();
        activityLines.Clear();
        hintWanted = null;
        FeelHudPrompt.Hide();
        minimap?.OnSceneChanged();
    }

    /// <summary>
    /// Edit-mode preview for verification screenshots: renders through the given camera (ScreenSpaceCamera)
    /// with fake data instead of live game state, at the scale a real screen of the camera's size would use
    /// (match 0.5 × <paramref name="uiScale"/>; ≤ 0 = the setting).
    /// </summary>
    public void EnablePreview(Camera cam, HudData data, float uiScale = -1f)
    {
        if (!built) Build();
        preview = true;
        previewData = data;
        previewUiScale = uiScale;
        cameraOverride = cam;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 1f;
        scaler.enabled = false;
        UiScale.Unregister(scaler);
        canvas.scaleFactor = cam != null && cam.pixelHeight > 0 ? UiScale.PreviewScaleFactor(cam.pixelWidth, cam.pixelHeight, uiScale) : 1f;
    }

    /// <summary>Preview: replace the fake data (e.g. switch modes between shots).</summary>
    public void SetPreviewData(HudData data) => previewData = data;

    /// <summary>Preview: the prompt and alt row to show (bypasses TooltipUI).</summary>
    public void PreviewPrompt(string key, string text, string altKey = null, string altText = null)
    {
        if (!built) Build();
        FeelHudPrompt.Show(key, text);
        if (!string.IsNullOrEmpty(altText)) FeelHudPrompt.Alt(altKey, altText);
        if (string.IsNullOrEmpty(text)) FeelHudPrompt.Hide();
    }

    // ------------------------------------------------------------------ events

    void OnCashChanged(float delta, string reason, Vector3? worldPos)
    {
        bool up = delta > 0f;
        bool calm = GameSettings.ReduceFlashing;
        cashFlashColor = up ? FeelUi.GoodGreen : FeelUi.BadRed;
        cashFlash = calm ? 0.35f : 1f;
        cashScaleVel += up ? 5f + Mathf.Min(8f, Mathf.Log10(1f + Mathf.Abs(delta)) * 4f) : -6f;

        // stack rapid same-sign deltas into one "+$1.24" label
        if (deltaAge < 1.2f && Mathf.Sign(deltaSum) == Mathf.Sign(delta)) deltaSum += delta;
        else deltaSum = delta;
        deltaAge = 0f;
        string label = FeelUi.Delta(deltaSum);
        // pickup pay carries its lateness in the reason ("On time!", "A bit late…", "Late…", "Super late…",
        // "Curb clean!"): tint the reason so on-time pickups read green and late ones orange/red
        var lateness = up ? FeelWorld.ParseLateness(reason) : FeelWorld.Lateness.None;
        string reasonHex = lateness != FeelWorld.Lateness.None ? FeelUi.Hex(FeelWorld.LatenessColor(lateness)) : "DDDDEE";
        if (!string.IsNullOrEmpty(reason) && reason.Length <= 22) label += $" <size=80%><color=#{reasonHex}>{reason}</color></size>";
        deltaText.text = label;
        deltaText.color = up ? FeelUi.GoodGreen : FeelUi.BadRed;
        float pw = Mathf.Min(HudLayout.CashW - 20f, deltaText.GetPreferredValues(label, 100000f, 0f).x + 26f);
        deltaPill.sizeDelta = new Vector2(pw, 34f);
        if (lateness == FeelWorld.Lateness.OnTime || lateness == FeelWorld.Lateness.CurbClean) cashScaleVel += 3f; // extra happy bounce
        else if (lateness == FeelWorld.Lateness.Late || lateness == FeelWorld.Lateness.SuperLate) cashScaleVel -= 3f; // deflated

        if (Mathf.Abs(delta) >= 1f) { countTicks = true; countStart = shownCash; tickTimer = 0f; }

        if (worldPos.HasValue)
        {
            // pickup pay popups carry their lateness: "+$4.00 / On time!" (green) … "+$1.00 / Super late…" (red)
            var late = up ? FeelWorld.ParseLateness(reason) : FeelWorld.Lateness.None;
            if (late != FeelWorld.Lateness.None)
            {
                var lc = FeelWorld.LatenessColor(late);
                floaters.Show(worldPos.Value + Vector3.up * 0.3f,
                              $"{FeelUi.Delta(delta)}\n<size=55%>{FeelWorld.LatenessText(late)}</size>", lc,
                              late == FeelWorld.Lateness.OnTime || late == FeelWorld.Lateness.CurbClean ? 1.15f : 0.95f);
                if (late == FeelWorld.Lateness.OnTime) Fx.Burst(worldPos.Value + Vector3.up * 0.4f, lc, 8, 2.5f);
            }
            else
                floaters.Show(worldPos.Value + Vector3.up * 0.3f, FeelUi.Delta(delta), up ? FeelUi.GoodGreen : FeelUi.BadRed,
                              Mathf.Abs(delta) >= 20f ? 1.4f : 1f);
        }
    }

    void OnDayStarted(int day)
    {
        // the clock widget pops its DAY label when the day number changes
    }

    // ------------------------------------------------------------------ per frame

    void LateUpdate()
    {
        if (!preview) Tick(Time.unscaledDeltaTime);
    }

    /// <summary>Advance all HUD animation by dt (unscaled seconds).</summary>
    public void Tick(float dt)
    {
        if (!built) return;
        dt = Mathf.Clamp(dt, 0f, 0.1f);
        var d = preview ? previewData : GatherLive();
        canvas.enabled = d.visible;
        if (!d.visible) { HudLayout.Publish(new HudLayout.Frame { mode = HudMode.Hidden }); return; }
        if (!preview) GameSettings.FlushDamagedToast();

        float time = Time.unscaledTime;
        var mode = d.mode;
        bool solo = d.solo;
        bool pausedHidden = mode == HudMode.Paused && solo;
        bool full = mode == HudMode.Driving || mode == HudMode.Street || mode == HudMode.Depot || (mode == HudMode.Paused && !solo);
        bool station = mode == HudMode.Station;

        // ---- layout inputs (last frame's content)
        Vector2 size = root.rect.size;
        float minimapCfg = preview ? 250f : GameSettings.MinimapDiameter;
        bool mapLabel = preview || GameSettings.MinimapStreetLabel;
        bool minimapWanted = d.hasMap && full && (preview || !FullMap.IsShown); // hidden while the full map (or its peek) is up
        bool rabiesUp = d.rabies || rabies.Presence > 0.01f;
        bool cityDue = d.hasCity;
        bool showSub = !d.hasRoute;
        activityLines.RemoveAll(line => time >= line.until);
        float leftWanted = 0f;
        int leftItems = 0;
        void AddLeft(float h) { leftWanted += (leftItems > 0 ? ColumnGap : 0f) + h; leftItems++; }
        if (cityDue) AddLeft(city.WantedHeight(showSub));
        if (d.finds > 0) AddLeft(FindsHeight);
        if (activityLines.Count > 0) AddLeft(12f + activityLines.Count * 24f);
        if (rabiesUp) AddLeft(FeelHudRabies.Height);

        // the dock's contents decide its height (its width depends only on the screen and the minimap setting)
        float dockW = DockWidth(size.x, minimapCfg);
        bool dockOn = full || station;
        hints.SetHintsEnabled(preview || GameSettings.ControlHints);
        hints.SetText(dockOn ? hintWanted : null);
        prompt.Sync(dockW);
        hints.SetPrompt(prompt.HasPrompt ? prompt.ShownKey : null, prompt.HasPrompt ? FeelHudPrompt.WantedText : null);
        var hs = hints.Sync(dockW);
        float promptH = prompt.HasPrompt && dockOn ? prompt.Size.y : 0f;
        float hotbarH = dockOn ? hotbar.Sync(dockW, mode, preview) : 0f;
        float dockWanted = (promptH > 0f ? promptH : 0f) + (promptH > 0f && hs.y > 0f ? 10f : 0f) + hs.y +
                           (hotbarH > 0f ? hotbarH + 8f : 0f);
        lastDockWanted = dockWanted;

        var inputs = new HudLayout.Inputs
        {
            size = size, mode = mode, minimapWanted = minimapWanted, minimapDiameter = minimapCfg, minimapLabel = mapLabel,
            rabiesUp = rabiesUp, leftWanted = leftWanted, dockWanted = dockWanted,
            businessContext = !solo && (full || mode == HudMode.Laptop),
        };
        if (CenterCard(size, out var card)) { inputs.hasCenterCard = true; inputs.centerCard = card; }
        layout = HudLayout.Compute(inputs);
        HudLayout.Publish(layout);

        // ---- group visibility by mode
        float topWant = (full || station || mode == HudMode.Laptop) ? 1f : 0f; // Station: only the slim pill (cash/clock panels are off)
        float gameplayWant = full && !d.blocked ? 1f : 0f;
        topGroup.alpha = Mathf.MoveTowards(topGroup.alpha, topWant, dt * 6f);
        gameplayGroup.alpha = Mathf.MoveTowards(gameplayGroup.alpha, gameplayWant, dt * 6f);
        rabiesGroup.alpha = Mathf.MoveTowards(rabiesGroup.alpha, (pausedHidden || mode == HudMode.Map) ? 0f : 1f, dt * 6f);
        dockGroup.alpha = Mathf.MoveTowards(dockGroup.alpha, dockOn && !d.blocked ? 1f : 0f, dt * 8f);
        pillGroup.alpha = Mathf.MoveTowards(pillGroup.alpha, station ? 1f : 0f, dt * 6f);
        cashPanel.gameObject.SetActive(!station);
        clock.Panel.gameObject.SetActive(!station);

        // ---- top bar
        HudLayout.PlaceAt(cashPanel, layout.cash.position);
        clock.SetCompact(layout.clockCompact);
        clock.SetTopLeft(layout.clock.position);
        truckPanel.sizeDelta = layout.truck.size;
        HudLayout.PlaceAt(truckPanel, layout.truck.position);
        HudLayout.PlaceAt(pillPanel, layout.pill.position);

        TickCash(d, dt);
        clock.Tick(d.hours, d.day, dt, time);
        TickPill(d);

        // ---- left column (priority when short on room: rabies > city > city subline > finds)
        TickLeftColumn(d, dt, time, showSub, station);

        // ---- right column: the route panel (its rows clamp to the room); Head Office memos go under it (ProgressionHud)
        route.SetWidth(layout.rightWidth);
        route.SetTopLeft(layout.rightColumn.position);
        int rowsFit = Mathf.FloorToInt((layout.rightColumn.height - FeelHudRoute.HeaderHeight - 8f) / FeelHudRoute.RowHeight);
        route.SetRowLimit(Mathf.Clamp(rowsFit, 0, FeelHudRoute.MaxRows));
        TickTruck(d, dt, time);
        route.Tick(d, dt, time);
        HudLayout.ReportRightUsed(layout.rightColumn.yMin + route.CurrentHeight * route.Presence * gameplayGroup.alpha);

        // ---- bottom dock: prompt over the hint chips, centred
        var dockR = layout.bottomDock;
        float cx = dockR.center.x;
        float hy = size.y - HudLayout.DockBottom - hs.y;
        hints.Tick(new Vector2(cx - hs.x * 0.5f, hy), dockOn, dt);
        float py = (hs.y > 0f ? hy - 10f : size.y - HudLayout.DockBottom) - prompt.Size.y;
        prompt.Tick(new Vector2(cx - prompt.Size.x * 0.5f, py), dockOn, dt);
        float hotbarBottom = (hs.y > 0f ? hy - 10f : size.y - HudLayout.DockBottom) - promptH - 8f;
        hotbar.Tick(new Vector2(cx - hotbar.Size.x * 0.5f, hotbarBottom - hotbarH), dockOn && !d.blocked, dt);
        TickHeldItem(mode, dockR, size, dt, preview);

        // ---- minimap (bottom-left)
        minimap.Tick(layout, layout.minimapVisible && minimapWanted && !d.blocked, mapLabel, dt, preview);

        // ---- toasts: the centre lane (Station: the bottom lane above the dock, growing upward)
        toasts.SetLane(station ? layout.bottomLane : layout.toastLane, station);
        toasts.Tick(dt, time);
        floaters.Tick(dt, cameraOverride != null ? cameraOverride : Camera.main, canvas);
        reveal.Tick(dt, time);
    }

    /// <summary>The dock width for a screen <paramref name="refW"/> wide (HudLayout's formula).</summary>
    static float DockWidth(float refW, float minimapCfg)
    {
        float dCfg = Mathf.Max(HudLayout.MinimapSmallest, minimapCfg);
        return Mathf.Clamp(Mathf.Min(HudLayout.DockMaxWidth, refW - 2f * (HudLayout.Margin + dCfg + HudLayout.Gap)),
                           Mathf.Min(HudLayout.DockMinWidth, refW - 2f * HudLayout.Margin), HudLayout.DockMaxWidth);
    }

    /// <summary>A centre card is up (the treasure reveal, or ProgressionHud's stamp cards): its top-down rect.</summary>
    bool CenterCard(Vector2 size, out Rect r)
    {
        r = default;
        if (reveal != null && reveal.Showing)
        {
            r = new Rect(size.x * 0.5f - 380f, size.y * 0.5f + 70f - 210f, 760f, 420f);
            return true;
        }
        var ph = ProgressionRunner.Instance != null ? ProgressionRunner.Instance.Hud : null;
        return ph != null && ph.CardRect(size, out r);
    }

    void TickLeftColumn(HudData d, float dt, float time, bool showSub, bool station)
    {
        var col = layout.leftColumn;
        float w = layout.leftWidth;
        city.SetWidth(w);
        rabies.SetWidth(w);
        findsPanel.sizeDelta = new Vector2(Mathf.Min(FindsWidth, w), FindsHeight);

        bool rabiesUp = d.rabies || rabies.Presence > 0.01f;
        float budget = col.height;
        float cityH = d.hasCity ? city.WantedHeight(showSub) : 0f;
        float cityBase = d.hasCity ? FeelHudCity.BaseHeight : 0f;
        float rab = rabiesUp ? FeelHudRabies.Height : 0f;
        float finds = d.finds > 0 ? FindsHeight : 0f;
        float activityH = activityLines.Count > 0 ? 12f + activityLines.Count * 24f : 0f;
        bool allowFinds = true, allowSub = true, allowCity = true;
        float Need(float c, float f, float r)
        {
            float n = 0f; int k = 0;
            if (c > 0f) { n += c; k++; }
            if (f > 0f) { n += f; k++; }
            if (r > 0f) { n += r; k++; }
            return n + Mathf.Max(0, k - 1) * ColumnGap;
        }
        if (station) { allowCity = false; allowFinds = false; }
        else if (Need(cityH, finds, rab) > budget + 0.5f)
        {
            allowFinds = false;
            if (Need(cityH, 0f, rab) > budget + 0.5f)
            {
                allowSub = false;
                if (Need(cityBase, 0f, rab) > budget + 0.5f) allowCity = false;
            }
        }
        findsAllowed = allowFinds;
        city.AllowSub(allowSub);
        bool allowActivity = !station && activityH > 0f && Need(allowCity ? (allowSub ? cityH : cityBase) : 0f,
                                                                    allowFinds ? finds : 0f, rab) + activityH + ColumnGap <= budget;

        city.Tick(d.hasCity && allowCity, d.city, d.curbBags, d.lateBags, showSub, dt, time);
        TickFinds(d, dt);
        rabies.Tick(d.rabies, d.rabiesAge, d.rabiesHoursLeft, d.rabiesPassOutHour, dt, time);

        float gameplayA = gameplayGroup.alpha;
        float y = col.yMin;
        city.SetTopLeft(new Vector2(col.xMin, y));
        y += (city.CurrentHeight + ColumnGap) * city.Presence * gameplayA;
        findsY = findsY < 0f ? y : Mathf.Lerp(findsY, y, 1f - Mathf.Exp(-12f * dt));
        findsPanel.anchoredPosition = new Vector2(col.xMin, -findsY);
        y += (FindsHeight + ColumnGap) * findsAlpha * gameplayA;
        activityPanel.sizeDelta = new Vector2(Mathf.Min(330f, w), activityH);
        HudLayout.PlaceAt(activityPanel, new Vector2(col.xMin, y));
        activityGroup.alpha = activityAlpha = Mathf.MoveTowards(activityAlpha, allowActivity ? 1f : 0f, dt * 6f);
        if (allowActivity)
        {
            var lines = new System.Text.StringBuilder();
            foreach (var line in activityLines) lines.Append(lines.Length > 0 ? "\n" : "").Append("• ").Append(line.text);
            activityText.text = lines.ToString();
            y += (activityH + ColumnGap) * activityAlpha * gameplayA;
        }
        float ry = station ? col.yMax - FeelHudRabies.Height : y;
        rabiesY = rabiesY < 0f || station ? ry : Mathf.Lerp(rabiesY, ry, 1f - Mathf.Exp(-12f * dt));
        rabies.SetTopLeft(new Vector2(col.xMin, rabiesY));
    }

    HudData GatherLive()
    {
        var d = new HudData();
        var pc = PlayerController.Local;
        d.visible = pc != null;
        if (!d.visible) return d;
        d.blocked = PlayerController.IsUIBlocking;
        d.cash = GameSession.Cash;
        d.day = GameSession.Day;
        d.finds = GameSession.Inventory.Count;
        var truck = TruckHudContext.Resolve(pc);
        d.load = truck.loadKg;
        d.cap = truck.capacityKg;
        d.truckTitle = truck.title;
        d.truckContext = truck.context;
        d.truckBusinessId = truck.businessId;
        d.truckDepotLot = truck.lot;
        d.hasTruckContext = true;
        d.truckSyncing = !truck.cargoKnown;

        d.hours = GameClock.Hours;
        d.solo = NetSession.IsSolo || !NetSession.InSession;
        d.hasMap = TownMapAnchor.Active != null && !DepotRowWorld.LocalIndoors; // Depot Row shops: indoors, no minimap
        d.mode = LiveMode(pc, d.hasMap);
        // a UI-pass screen holding the lock (pause, settings, map) isn't "the laptop": its own mode decides what shows
        if (d.mode == HudMode.Paused || d.mode == HudMode.Map) d.blocked = false;

        // living world: city meter + curb counts (garage too), rabies
        d.hasCity = FeelWorld.IsLive;
        if (d.hasCity)
        {
            d.city = FeelWorld.Cleanliness;
            d.curbBags = FeelWorld.TotalCurbBags(TrashRoute.Current);
            d.lateBags = FeelWorld.LateBags(TrashRoute.Current);
        }
        d.rabies = GameSession.HasRabies;
        if (d.rabies)
        {
            double at = GameSession.RabiesInfectedAtHours;
            double passOut = at + Economy.RabiesPassOutHours;
            d.rabiesAge = (float)GameClock.AgeHours(at);
            d.rabiesHoursLeft = Mathf.Max(0f, (float)(passOut - GameClock.TotalHours));
            d.rabiesPassOutHour = (float)(passOut % 24.0);
        }

        route.Gather(ref d, pc, Time.unscaledDeltaTime);
        return d;
    }

    /// <summary>§8.3, derived each frame: Map → Paused → Station → Laptop → Driving → Street / Depot.</summary>
    static HudMode LiveMode(PlayerController pc, bool hasMap)
    {
        if (pc == null) return HudMode.Hidden;
        if (FullMap.IsOpenInteractive) return HudMode.Map;
        if (UiStack.LocksHeld > 0) return HudMode.Paused;
        bool focus = false;
        if (PlayerController.InteractionFocusOverride != null)
        {
            try { focus = PlayerController.InteractionFocusOverride(pc); }
            catch (System.Exception) { focus = false; }
        }
        if (focus) return HudMode.Station;
        if (PlayerController.IsUIBlocking) return HudMode.Laptop;
        if (pc.IsDriving) return HudMode.Driving;
        return hasMap ? HudMode.Street : HudMode.Depot;
    }

    void TickCash(HudData d, float dt)
    {
        if (!cashInit) { shownCash = d.cash; cashInit = true; }
        float diff = d.cash - shownCash;
        if (Mathf.Abs(diff) > 0.0001f)
        {
            float step = Mathf.Max(Mathf.Abs(diff) * 8f, 1.5f) * dt;
            shownCash = Mathf.MoveTowards(shownCash, d.cash, step);
            if (countTicks)
            {
                tickTimer -= dt;
                if (tickTimer <= 0f)
                {
                    tickTimer = 0.06f;
                    float total = d.cash - countStart;
                    float prog = Mathf.Abs(total) > 0.001f ? Mathf.Clamp01((shownCash - countStart) / total) : 1f;
                    Sfx.Play("click", null, 0.18f, 1f + prog * 0.6f);
                }
            }
        }
        else countTicks = false;

        string s = Economy.Money(shownCash);
        if (s != cashShownStr) { cashShownStr = s; cashText.text = s; }

        cashFlash = Mathf.MoveTowards(cashFlash, 0f, dt * 1.6f);
        cashText.color = Color.Lerp(Color.white, cashFlashColor, cashFlash);
        cashScale = FeelUi.Spring(cashScale, ref cashScaleVel, 1f, dt, 260f, 14f);
        cashTextRt.localScale = Vector3.one * Mathf.Clamp(cashScale, 0.6f, 1.6f);

        // the delta pill pops out of the panel's bottom-right corner and floats up over the panel as it fades
        deltaAge += dt;
        bool on = deltaAge < 1.7f && !string.IsNullOrEmpty(deltaText.text);
        if (deltaPill.gameObject.activeSelf != on) deltaPill.gameObject.SetActive(on);
        if (on)
        {
            float pop = deltaAge < 0.18f ? FeelEase.OutBack(deltaAge / 0.18f, 2.4f) : 1f;
            float a = deltaAge < 1.2f ? 1f : Mathf.Clamp01(1f - (deltaAge - 1.2f) / 0.5f);
            deltaPill.localScale = Vector3.one * pop;
            deltaPill.anchoredPosition = new Vector2(-14f, Mathf.Max(0f, deltaAge - 1.0f) * 40f);
            deltaText.alpha = a;
            deltaBody.color = new Color(0.11f, 0.09f, 0.17f, 0.95f * a);
        }
    }

    void TickPill(HudData d)
    {
        if (pillGroup.alpha <= 0.001f) return;
        string s = $"{Economy.Money(shownCash)}   <color=#C9C3E6>{GameClock.FormatHours(d.hours)}</color>";
        if (s != pillShown) { pillShown = s; pillText.text = s; }
    }

    void TickFinds(HudData d, float dt)
    {
        if (d.finds != lastFinds)
        {
            if (lastFinds >= 0 && d.finds > lastFinds) findsScaleVel += 9f;
            lastFinds = d.finds;
            findsText.text = $"Finds: {d.finds}  <size=80%><color=#FFD84A>sell on ebae!</color></size>";
        }
        findsAlpha = Mathf.MoveTowards(findsAlpha, d.finds > 0 && findsAllowed ? 1f : 0f, dt * 4f);
        findsGroup.alpha = findsAlpha;
        findsScale = FeelUi.Spring(findsScale, ref findsScaleVel, 1f, dt);
        findsPanel.localScale = Vector3.one * Mathf.Clamp(findsScale, 0.6f, 1.8f);
    }

    void TickTruck(HudData d, float dt, float time)
    {
        truckTitleText.text = d.truckTitle ?? "YOUR TRUCK";
        truckContextText.text = d.truckContext ?? "OWN BUSINESS";
        float cap = Mathf.Max(0.001f, d.cap);
        float fill = d.truckSyncing ? 0f : Mathf.Clamp01(d.load / cap);
        bool contextChanged = shownTruckBusiness != d.truckBusinessId || shownTruckSyncing != d.truckSyncing;
        if (contextChanged)
        {
            shownTruckBusiness = d.truckBusinessId;
            shownTruckSyncing = d.truckSyncing;
            lastLoad = lastCap = -1f;
            fillShown = fill;
            fillVel = wobble = truckScaleVel = 0f;
            truckScale = 1f;
        }
        bool calm = !preview && GameSettings.ReduceFlashing;
        if (!Mathf.Approximately(d.load, lastLoad) || !Mathf.Approximately(d.cap, lastCap))
        {
            if (lastLoad >= 0f)
            {
                wobble = 1f;
                truckScaleVel += d.load > lastLoad ? 4f : -3f;
            }
            else fillShown = fill;
            lastLoad = d.load;
            lastCap = d.cap;
            truckKgText.text = d.truckSyncing ? "Syncing cargo…" : $"{d.load:0} / {d.cap:0} kg";
        }

        fillShown = FeelUi.Spring(fillShown, ref fillVel, fill, dt, 120f, 9f);
        float shown = Mathf.Clamp01(fillShown);
        barFill.anchorMax = new Vector2(shown, 1f);
        barFill.gameObject.SetActive(shown > 0.005f);

        Color c = fill < 0.6f
            ? Color.Lerp(new Color(0.3f, 0.9f, 0.35f), new Color(1f, 0.85f, 0.2f), fill / 0.6f)
            : Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(1f, 0.28f, 0.22f), (fill - 0.6f) / 0.3f);
        bool full = fill >= 0.98f;
        if (full && !calm) c = Color.Lerp(c, Color.white, 0.25f + 0.25f * Mathf.Sin(time * 16f));
        barFillImg.color = c;
        truckKgText.color = fill >= 0.85f ? new Color(1f, 0.6f, 0.3f) : Color.white;

        fullText.gameObject.SetActive(full);
        if (full)
        {
            // Reduce flashing: FULL! stays steady
            fullText.alpha = calm || Mathf.Sin(time * 12f) > -0.3f ? 1f : 0.15f;
            fullText.rectTransform.localScale = Vector3.one * (calm ? 1f : 1f + 0.08f * Mathf.Sin(time * 9f));
        }

        wobble = Mathf.MoveTowards(wobble, 0f, dt * 1.8f);
        truckPanel.localRotation = Quaternion.Euler(0f, 0f, -1.2f + wobble * wobble * 7f * Mathf.Sin(time * 26f));
        truckScale = FeelUi.Spring(truckScale, ref truckScaleVel, 1f, dt);
        truckPanel.localScale = Vector3.one * Mathf.Clamp(truckScale, 0.7f, 1.4f);
    }

    // ------------------------------------------------------------------ verification (edit-mode audits)

    /// <summary>
    /// Top-down rects (reference units) of every zone-owned widget on screen right now, for the overlap matrix (UIPass §11
    /// UP1-1). Rotated panels report their axis-aligned bounds. Includes ProgressionHud's rank chip and memo.
    /// </summary>
    public void CollectZoneRects(List<(string name, Rect r)> into)
    {
        void Add(string n, RectTransform rt, float alpha)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy || alpha <= 0.01f) return;
            into.Add((n, Aabb(rt)));
        }
        bool station = layout.mode == HudMode.Station;
        Add("Cash", cashPanel, topGroup.alpha);
        Add("Clock", clock.Panel, topGroup.alpha);
        Add("Pill", pillPanel, pillGroup.alpha);
        Add("Truck", truckPanel, gameplayGroup.alpha);
        Add("City", city.Panel, city.Presence * gameplayGroup.alpha);
        Add("Finds", findsPanel, findsAlpha * gameplayGroup.alpha);
        Add("Rabies", rabies.Panel, rabies.Presence * rabiesGroup.alpha);
        Add("Route", route.Panel, route.Presence * gameplayGroup.alpha);
        if (prompt.Alpha > 0.01f && dockGroup.alpha > 0.01f) into.Add(("Prompt", Aabb(prompt.Panel)));
        if (hints.Alpha > 0.01f && dockGroup.alpha > 0.01f && hints.Size.x > 0f) into.Add(("Hints", Aabb(hints.Root)));
        if (minimap.Visible)
        {
            // the lid and the pill as drawn (each must also stay inside the minimap zone)
            into.Add(("Minimap", Aabb(minimap.LidRect)));
            if (minimap.PillRect != null) into.Add(("MinimapPill", Aabb(minimap.PillRect)));
        }
        toasts.CollectRects(into);
        var ph = ProgressionRunner.Instance != null ? ProgressionRunner.Instance.Hud : PreviewProgression;
        ph?.CollectZoneRects(root, into);
        if (station) { /* the bin-sign band check is done by the caller */ }
    }

    /// <summary>Preview only: the ProgressionHud rendered alongside (no runner in edit mode).</summary>
    public ProgressionHud PreviewProgression { get; set; }

    /// <summary>The axis-aligned top-down rect of a (possibly rotated/scaled) RectTransform inside the HUD root.</summary>
    public Rect Aabb(RectTransform rt)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        var rs = root.rect.size;
        for (int i = 0; i < 4; i++)
        {
            var lp = root.InverseTransformPoint(corners[i]);
            // root pivot is centred: convert to top-down
            float x = lp.x + rs.x * root.pivot.x;
            float y = rs.y * (1f - root.pivot.y) - lp.y;
            x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x);
            y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
        }
        return Rect.MinMaxRect(x0, y0, x1, y1);
    }

    /// <summary>The HUD root size in reference units.</summary>
    public Vector2 RootSize => root != null ? root.rect.size : Vector2.zero;
    public HudLayout.Frame LastLayout => layout;

    /// <summary>Every visible TMP text's smallest effective size (base size × any &lt;size=NN%&gt;), for the 18 px floor.</summary>
    public float SmallestText(out string which)
    {
        which = null;
        float min = float.MaxValue;
        foreach (var t in GetComponentsInChildren<TMP_Text>(false))
        {
            if (!t.isActiveAndEnabled || string.IsNullOrEmpty(t.text)) continue;
            var g = t.GetComponentInParent<CanvasGroup>();
            if (g != null && g.alpha <= 0.01f) continue;
            float s = t.fontSize * SmallestPercent(t.text) * t.rectTransform.lossyScale.y / Mathf.Max(1e-5f, root.lossyScale.y);
            if (s < min) { min = s; which = t.name + " '" + t.text + "'"; }
        }
        return min;
    }

    static float SmallestPercent(string s)
    {
        float m = 1f;
        int i = 0;
        while ((i = s.IndexOf("<size=", i, System.StringComparison.Ordinal)) >= 0)
        {
            int e = s.IndexOf('>', i);
            if (e < 0) break;
            string v = s.Substring(i + 6, e - i - 6);
            if (v.EndsWith("%") && float.TryParse(v.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p))
                m = Mathf.Min(m, p / 100f);
            i = e;
        }
        return m;
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The guided first run's on-screen parts (Docs/Campaign.md §14.8), a pure view that <see cref="Tutorial"/> drives:
/// <list type="bullet">
/// <item>One <b>objective banner</b> above the bottom dock: a step badge, a title, one line of help and (optionally) a key cap.
/// The "urgent" kind (the truck is full) is bigger and pulses.</item>
/// <item>A <b>pointer</b> at the thing to go to: a bobbing arrow over it with its name and the distance, or, when it is out of view,
/// an arrow on the edge of the screen pointing the way.</item>
/// <item>A quick green <b>done</b> flash between steps.</item>
/// </list>
/// It has its own overlay canvas scaled exactly like the HUD's (1920x1080 reference, the UI-scale setting), so
/// <see cref="HudLayout"/>'s rects apply as they are. It never takes a click.
/// </summary>
public sealed class TutorialHud : MonoBehaviour
{
    /// <summary>Over the HUD (100) and the depot's crosshair (90), under every full screen (maps, menus, dialogs).</summary>
    public const int SortingOrder = 108;
    public const float BannerWidth = 800f, UrgentWidth = 940f;
    const float Pad = 18f, Badge = 62f, PointerSize = 74f;
    static readonly Color Plum = new Color(0.13f, 0.1f, 0.2f, 0.93f);
    static readonly Color Gold = new Color(1f, 0.84f, 0.25f);
    static readonly Color Green = new Color(0.4f, 1f, 0.5f);

    /// <summary>What the banner says.</summary>
    public struct Card
    {
        /// <summary>"3/9" in the badge (empty = a star).</summary>
        public string badge;
        public string title;
        /// <summary>Rich text, one or two lines.</summary>
        public string body;
        /// <summary>A key cap on the right ("E", "Hold G", "LMB"), or empty.</summary>
        public string key;
        /// <summary>Bigger and pulsing: the thing that must not be missed.</summary>
        public bool urgent;

        public bool Same(Card o) => badge == o.badge && title == o.title && body == o.body && key == o.key && urgent == o.urgent;
    }

    static TutorialHud instance;
    public static TutorialHud Instance => instance;

    /// <summary>The overlay, created on first use (it survives scene loads).</summary>
    public static TutorialHud Get()
    {
        if (instance != null) return instance;
        if (!Application.isPlaying) return null;
        var go = new GameObject("[TutorialHud]", typeof(RectTransform));
        go.layer = 5;
        DontDestroyOnLoad(go);
        var hud = go.AddComponent<TutorialHud>();
        hud.Build();
        instance = hud;
        return hud;
    }

    /// <summary>A copy that is not the game's (screenshots, tests): built, never registered, driven by <see cref="Tick"/>.</summary>
    public static TutorialHud BuildForPreview(Transform parent)
    {
        var go = new GameObject("[TutorialHud preview]", typeof(RectTransform));
        go.layer = 5;
        if (parent != null) go.transform.SetParent(parent, false);
        var hud = go.AddComponent<TutorialHud>();
        hud.preview = true;
        hud.Build();
        return hud;
    }

    /// <summary>The camera the pointer projects through (null = the game's main camera).</summary>
    public Camera cameraOverride;

    bool preview;
    Canvas canvas;
    CanvasScaler scaler;
    RectTransform root, banner, badgeRt, textCol, capHolder, pointer, pointerArrow, pointerShadow, labelRt, flashRt, skipRt, skipBarRt;
    CanvasGroup bannerGroup, pointerGroup, flashGroup;
    Image bannerBody, edge, glow, badgeDisc, badgeIcon, flashBody;
    TextMeshProUGUI badgeText, title, body, label, flashText, skipText;
    FeelKeyCap cap;

    // what the driver wants
    bool wantCard;
    Card card;
    Card shownCard;
    bool hasTarget;
    Vector3 targetWorld;
    string targetName;
    float skipProgress;
    string skipLabel;

    // animation
    float alpha, slide = 1f, slideVel, punch = 1f, punchVel, pointerAlpha, flash, flashAge;
    float bannerW = BannerWidth, bannerH = 100f;
    bool urgentShown;

    /// <summary>The banner's top-down rect in the HUD's reference units (valid while it is shown).</summary>
    public Rect BannerRect { get; private set; }
    public bool BannerShown => alpha > 0.02f;

    // ------------------------------------------------------------------ the driver's side

    /// <summary>Show this card (null/empty title hides the banner).</summary>
    public void SetCard(Card? c)
    {
        wantCard = c.HasValue && !string.IsNullOrEmpty(c.Value.title);
        if (wantCard) card = c.Value;
    }

    /// <summary>Point at <paramref name="world"/> (null hides the pointer). <paramref name="name"/> is the label ("Your truck").</summary>
    public void SetTarget(Vector3? world, string name)
    {
        hasTarget = world.HasValue;
        if (hasTarget) { targetWorld = world.Value; targetName = name; }
    }

    /// <summary>The green "done" flash (the banner steps aside for a moment).</summary>
    public void Celebrate(string text)
    {
        flashText.text = text;
        flash = 1f;
        flashAge = 0f;
        if (!preview) Sfx.Play("sparkle", null, 0.7f, 1.05f);
    }

    /// <summary>The "hold U to skip" line under the banner: <paramref name="label"/> (null hides it) and how far the hold is (0..1).</summary>
    public void SetSkip(string label, float progress)
    {
        skipLabel = label;
        skipProgress = Mathf.Clamp01(progress);
    }

    /// <summary>Drop everything (the tutorial ended or was skipped).</summary>
    public void Clear()
    {
        wantCard = false;
        hasTarget = false;
    }

    // ------------------------------------------------------------------ building

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        if (!preview) UiScale.Register(scaler);       // Settings → Interface → UI scale, like the HUD's own
        root = (RectTransform)transform;
        // deliberately no GraphicRaycaster: the tutorial never eats a click

        // ---- the pointer (under the banner)
        pointer = FeelUi.Rect("Pointer", root, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PointerSize, PointerSize));
        pointerGroup = pointer.gameObject.AddComponent<CanvasGroup>();
        pointerGroup.alpha = 0f;
        pointerShadow = FeelUi.Rect("Shadow", pointer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(4f, -5f), new Vector2(PointerSize, PointerSize));
        var sh = pointerShadow.gameObject.AddComponent<Image>();
        sh.sprite = FeelUi.Arrow; sh.color = FeelUi.ShadowColor; sh.raycastTarget = false;
        pointerArrow = FeelUi.Rect("Arrow", pointer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(PointerSize, PointerSize));
        var ar = pointerArrow.gameObject.AddComponent<Image>();
        ar.sprite = FeelUi.Arrow; ar.color = Gold; ar.raycastTarget = false;
        labelRt = FeelUi.Rect("Label", pointer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, PointerSize * 0.5f + 6f), new Vector2(260f, 38f));
        FeelUi.Panel(labelRt, Plum, 0.6f, new Vector2(3f, -4f));
        label = FeelUi.Text(labelRt, "Text", 22f, Color.white, TextAlignmentOptions.Center);
        label.textWrappingMode = TextWrappingModes.NoWrap;

        // ---- the banner
        banner = FeelUi.Rect("Banner", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(BannerWidth, 100f));
        bannerGroup = banner.gameObject.AddComponent<CanvasGroup>();
        bannerGroup.alpha = 0f;
        glow = FeelUi.Img(banner, "Glow", FeelUi.Rounded, new Color(1f, 0.84f, 0.25f, 0f), 0.8f);
        glow.rectTransform.offsetMin = new Vector2(-10f, -10f);
        glow.rectTransform.offsetMax = new Vector2(10f, 10f);
        bannerBody = FeelUi.Panel(banner, Plum, 0.7f, new Vector2(5f, -6f));
        edge = FeelUi.Img(banner, "Edge", FeelUi.Rounded, Gold, 0.7f);
        edge.rectTransform.anchorMin = Vector2.zero; edge.rectTransform.anchorMax = new Vector2(0f, 1f);
        edge.rectTransform.offsetMin = new Vector2(0f, 6f); edge.rectTransform.offsetMax = new Vector2(9f, -6f);

        badgeRt = FeelUi.Rect("Badge", banner, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(Pad + 6f, 0f), new Vector2(Badge, Badge));
        badgeDisc = FeelUi.Img(badgeRt, "Disc", FeelUi.Circle, Gold);
        badgeText = FeelUi.Text(badgeRt, "Text", 22f, new Color(0.18f, 0.12f, 0.02f), TextAlignmentOptions.Center);
        badgeText.textWrappingMode = TextWrappingModes.NoWrap;
        badgeText.overflowMode = TextOverflowModes.Overflow;
        badgeIcon = FeelUi.Img(badgeRt, "Star", FeelUi.Star, new Color(0.18f, 0.12f, 0.02f));
        badgeIcon.rectTransform.offsetMin = new Vector2(10f, 10f); badgeIcon.rectTransform.offsetMax = new Vector2(-10f, -10f);

        textCol = FeelUi.Rect("Text", banner, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(Pad + 6f + Badge + 16f, -Pad), new Vector2(400f, 60f));
        // Overflow, not Ellipsis: TMP drops a line that is taller than its rect, and these rects are sized from the font
        title = FeelUi.Text(textCol, "Title", 31f, Gold, TextAlignmentOptions.TopLeft);
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.overflowMode = TextOverflowModes.Overflow;
        title.enableAutoSizing = true;
        title.fontSizeMin = 20f;
        body = FeelUi.Text(textCol, "Body", 25f, Color.white, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        body.textWrappingMode = TextWrappingModes.Normal;
        body.overflowMode = TextOverflowModes.Overflow;
        capHolder = FeelUi.Rect("Cap", banner, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-Pad - 4f, 0f), new Vector2(60f, 46f));
        cap = new FeelKeyCap(capHolder, 46f);
        cap.Root.anchorMin = cap.Root.anchorMax = new Vector2(1f, 0.5f);
        cap.Root.pivot = new Vector2(1f, 0.5f);
        cap.Root.anchoredPosition = Vector2.zero;

        // ---- the skip line, under the banner
        skipRt = FeelUi.Rect("Skip", root, new Vector2(0f, 1f), new Vector2(1f, 0f), Vector2.zero, new Vector2(360f, 28f));
        skipText = FeelUi.Text(skipRt, "Text", 19f, new Color(1f, 1f, 1f, 0.62f), TextAlignmentOptions.MidlineRight, FontStyles.Normal);
        skipText.textWrappingMode = TextWrappingModes.NoWrap;
        skipBarRt = FeelUi.Rect("Bar", skipRt, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(0f, 3f));
        skipBarRt.anchorMin = new Vector2(0f, 0f); skipBarRt.anchorMax = new Vector2(0f, 0f);
        var sb = skipBarRt.gameObject.AddComponent<Image>();
        sb.color = Gold; sb.raycastTarget = false;
        skipRt.gameObject.SetActive(false);

        // ---- the done flash (on top of the banner's place)
        flashRt = FeelUi.Rect("Flash", root, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 84f));
        flashGroup = flashRt.gameObject.AddComponent<CanvasGroup>();
        flashGroup.alpha = 0f;
        flashBody = FeelUi.Panel(flashRt, new Color(0.08f, 0.3f, 0.14f, 0.94f), 0.7f, new Vector2(5f, -6f));
        var chk = FeelUi.Rect("Check", flashRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(50f, 50f));
        var ci = chk.gameObject.AddComponent<Image>();
        ci.sprite = UiKit.Sprites.Check; ci.color = Green; ci.raycastTarget = false;
        flashText = FeelUi.Text(flashRt, "Text", 32f, Color.white, TextAlignmentOptions.MidlineLeft);
        flashText.rectTransform.offsetMin = new Vector2(82f, 0f);
        flashText.rectTransform.offsetMax = new Vector2(-14f, 0f);
        flashText.textWrappingMode = TextWrappingModes.NoWrap;
        flashText.overflowMode = TextOverflowModes.Ellipsis;
    }

    void OnDestroy()
    {
        if (scaler != null && !preview) UiScale.Unregister(scaler);
        if (instance == this) instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    // ------------------------------------------------------------------ every frame

    bool faulted;

    void LateUpdate()
    {
        if (preview || faulted) return;
        try { Tick(Time.unscaledDeltaTime, Time.unscaledTime); }
        catch (System.Exception e)
        {
            // an overlay must never break the game: hide it and say so once
            faulted = true;
            Debug.LogException(e);
            if (banner != null) banner.gameObject.SetActive(false);
            if (pointer != null) pointer.gameObject.SetActive(false);
        }
    }

    /// <summary>One frame of animation and placement (the game calls it from LateUpdate; a preview calls it by hand).</summary>
    public void Tick(float dt, float time)
    {
        var lay = HudLayout.Layout;
        var size = root.rect.size;
        bool hudUp = lay.valid && size.x > 10f && size.y > 10f;
        // the HUD is not on screen in these (the map, a menu, the laptop): nothing of ours is either
        bool modeOk = hudUp && lay.mode != HudMode.Hidden && lay.mode != HudMode.Map && lay.mode != HudMode.Paused && lay.mode != HudMode.Laptop;

        TickBanner(dt, time, lay, size, wantCard && modeOk);
        TickPointer(dt, time, lay, size, hasTarget && modeOk);
        TickFlash(dt, time, lay, size, modeOk);
    }

    void TickBanner(float dt, float time, HudLayout.Frame lay, Vector2 size, bool show)
    {
        if (show && !card.Same(shownCard)) ApplyCard(lay, size);
        alpha = Mathf.MoveTowards(alpha, show && flash <= 0.01f ? 1f : 0f, dt * (show ? 6f : 9f));
        slide = FeelUi.Spring(slide, ref slideVel, show ? 0f : 1f, dt, 220f, 17f);
        punch = FeelUi.Spring(punch, ref punchVel, 1f, dt, 260f, 14f);
        bannerGroup.alpha = alpha;
        banner.gameObject.SetActive(alpha > 0.005f);
        if (alpha <= 0.005f) return;

        // above the bottom dock, centred in the middle lane (or on the screen when the lane is narrow)
        float dockTop = lay.bottomDock.height > 0.5f ? lay.bottomDock.yMin : size.y - HudLayout.DockBottom;
        var lane = lay.centerStage;
        float cx = lane.width > bannerW + 20f ? lane.center.x : size.x * 0.5f;
        float w = Mathf.Min(bannerW, size.x - 2f * HudLayout.Margin);
        if (Mathf.Abs(w - banner.sizeDelta.x) > 0.5f || Mathf.Abs(bannerH - banner.sizeDelta.y) > 0.5f) Layout(w);
        float y = dockTop - 16f - bannerH + slide * 40f;
        banner.anchoredPosition = new Vector2(cx - w * 0.5f, -y);
        banner.localScale = Vector3.one * Mathf.Clamp(punch, 0.85f, 1.2f);
        BannerRect = new Rect(cx - w * 0.5f, dockTop - 16f - bannerH, w, bannerH);

        // the skip line hangs under the banner's right end
        bool skipOn = !string.IsNullOrEmpty(skipLabel) && alpha > 0.5f;
        skipRt.gameObject.SetActive(skipOn);
        if (skipOn)
        {
            if (skipText.text != skipLabel) skipText.text = skipLabel;
            skipRt.anchoredPosition = new Vector2(cx + w * 0.5f - 6f, -(dockTop - 16f - bannerH - 4f + slide * 40f));
            skipBarRt.sizeDelta = new Vector2(360f * skipProgress, 3f);
        }

        float pulse = urgentShown ? 0.5f + 0.5f * Mathf.Sin(time * 6f) : 0f;
        glow.color = new Color(1f, 0.84f, 0.25f, urgentShown ? 0.12f + 0.3f * pulse : 0f);
        edge.color = urgentShown ? Color.Lerp(Gold, new Color(1f, 0.45f, 0.2f), pulse) : Gold;
        badgeRt.localScale = Vector3.one * (urgentShown ? 1f + 0.08f * pulse : 1f);
    }

    /// <summary>Put the wanted card on the banner and size it to its text.</summary>
    void ApplyCard(HudLayout.Frame lay, Vector2 size)
    {
        bool first = !shownCard.Same(default);
        shownCard = card;
        urgentShown = card.urgent;
        bannerW = card.urgent ? UrgentWidth : BannerWidth;
        float w = Mathf.Min(bannerW, size.x - 2f * HudLayout.Margin);
        title.fontSizeMax = card.urgent ? 40f : 31f;
        title.fontSize = title.fontSizeMax;
        body.fontSize = card.urgent ? 28f : 25f;
        title.text = card.title;
        body.text = card.body ?? "";
        bool hasBadge = !string.IsNullOrEmpty(card.badge);
        badgeText.gameObject.SetActive(hasBadge);
        badgeIcon.gameObject.SetActive(!hasBadge);
        badgeText.text = card.badge ?? "";
        bool hasKey = !string.IsNullOrEmpty(card.key);
        capHolder.gameObject.SetActive(hasKey);
        if (hasKey) cap.Set(card.key);
        Layout(w);
        punchVel += first ? 4f : 7f;
        if (first && !preview) Sfx.Play("pop", null, 0.35f, card.urgent ? 0.8f : 1.2f);
    }

    void Layout(float w)
    {
        float capW = capHolder.gameObject.activeSelf ? cap.Width + 20f : 0f;
        float textX = Pad + 6f + Badge + 16f;
        float textW = Mathf.Max(120f, w - textX - Pad - capW);
        textCol.sizeDelta = new Vector2(textW, 10f);
        float titleH = title.fontSizeMax * 1.3f;
        float bodyH = string.IsNullOrEmpty(body.text) ? 0f : Mathf.Min(body.GetPreferredValues(body.text, textW, 0f).y + 2f, body.fontSize * 3.6f);
        title.rectTransform.anchorMin = new Vector2(0f, 1f); title.rectTransform.anchorMax = new Vector2(1f, 1f);
        title.rectTransform.pivot = new Vector2(0f, 1f);
        title.rectTransform.offsetMin = new Vector2(0f, -titleH); title.rectTransform.offsetMax = Vector2.zero;
        body.rectTransform.anchorMin = new Vector2(0f, 1f); body.rectTransform.anchorMax = new Vector2(1f, 1f);
        body.rectTransform.pivot = new Vector2(0f, 1f);
        body.rectTransform.offsetMin = new Vector2(0f, -(titleH + 2f + bodyH)); body.rectTransform.offsetMax = new Vector2(0f, -(titleH + 2f));
        bannerH = Mathf.Max(Badge + 2f * Pad, titleH + 2f + bodyH + 2f * Pad);
        textCol.sizeDelta = new Vector2(textW, titleH + 2f + bodyH);
        banner.sizeDelta = new Vector2(w, bannerH);
        capHolder.sizeDelta = new Vector2(cap.Width, 46f);
    }

    void TickPointer(float dt, float time, HudLayout.Frame lay, Vector2 size, bool show)
    {
        var cam = cameraOverride != null ? cameraOverride : PointerCamera();
        bool ok = show && cam != null;
        pointerAlpha = Mathf.MoveTowards(pointerAlpha, ok ? 1f : 0f, dt * 6f);
        pointerGroup.alpha = pointerAlpha;
        pointer.gameObject.SetActive(pointerAlpha > 0.005f);
        if (!ok || pointerAlpha <= 0.005f) return;

        Vector3 sp = cam.WorldToScreenPoint(targetWorld);
        bool behind = sp.z <= 0.01f;
        float pw = Mathf.Max(1f, cam.pixelWidth), ph = Mathf.Max(1f, cam.pixelHeight);
        Vector2 p = new Vector2(sp.x * (size.x / pw), (ph - sp.y) * (size.y / ph));        // top-down reference units
        if (behind) p = new Vector2(size.x - p.x, size.y - p.y);

        // the room the pointer may use: the middle lane (clear of the side columns), under the top bar, above the banner or dock
        float top = HudLayout.ColumnTop + 24f;
        float bottom = (BannerShown ? BannerRect.yMin : size.y - 120f) - 24f;
        float left = 70f, right = size.x - 70f;
        var stage = lay.centerStage;
        if (stage.width > 300f) { left = stage.xMin + 50f; right = stage.xMax - 50f; }
        bool onScreen = !behind && p.x > left && p.x < right && p.y > top + 60f;
        if (onScreen) p.y = Mathf.Min(p.y, bottom + 60f);          // a target low on the screen keeps its arrow above the banner
        float dist = Vector3.Distance(cam.transform.position, targetWorld);

        float bob = Mathf.Sin(time * 4.2f) * 9f;
        if (onScreen)
        {
            pointer.anchoredPosition = new Vector2(p.x, -(Mathf.Min(p.y - 78f, bottom - 50f) + bob));
            pointerArrow.localRotation = Quaternion.Euler(0f, 0f, 180f);          // points down at it
            pointerShadow.localRotation = pointerArrow.localRotation;
            labelRt.anchoredPosition = new Vector2(0f, PointerSize * 0.5f + 8f);
        }
        else
        {
            // on the edge, pointing the way it is
            Vector2 c = new Vector2(size.x * 0.5f, (top + bottom) * 0.5f);
            Vector2 d = p - c;
            if (d.sqrMagnitude < 1f) d = Vector2.down;
            float hx = (right - left) * 0.5f, hy = (bottom - top) * 0.5f;
            float k = Mathf.Min(hx / Mathf.Max(0.001f, Mathf.Abs(d.x)), hy / Mathf.Max(0.001f, Mathf.Abs(d.y)));
            Vector2 e = c + d * k;
            Vector2 dir = new Vector2(d.x, -d.y).normalized;                       // screen up = +y
            pointer.anchoredPosition = new Vector2(e.x + dir.x * bob * 0.6f, -(e.y - dir.y * bob * 0.6f));
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            pointerArrow.localRotation = Quaternion.Euler(0f, 0f, ang);
            pointerShadow.localRotation = pointerArrow.localRotation;
            // the label goes to the inner side of the arrow
            labelRt.anchoredPosition = new Vector2(-dir.x * 120f, dir.y * 70f - 19f);
            labelRt.pivot = new Vector2(0.5f, 0.5f);
        }
        if (onScreen) labelRt.pivot = new Vector2(0.5f, 0f);
        string txt = string.IsNullOrEmpty(targetName) ? $"{Mathf.RoundToInt(dist)} m" : $"{targetName}  <color=#FFD740>{Mathf.RoundToInt(dist)} m</color>";
        if (label.text != txt) { label.text = txt; labelRt.sizeDelta = new Vector2(Mathf.Clamp(label.GetPreferredValues(txt).x + 40f, 120f, 460f), 38f); }
    }

    static Camera PointerCamera()
    {
        var c = Camera.main;
        if (c != null && c.isActiveAndEnabled) return c;
        Camera best = null;
        foreach (var cam in Camera.allCameras)
            if (cam != null && cam.isActiveAndEnabled && cam.targetTexture == null && (best == null || cam.depth > best.depth)) best = cam;
        return best;
    }

    void TickFlash(float dt, float time, HudLayout.Frame lay, Vector2 size, bool modeOk)
    {
        if (flash > 0f)
        {
            flashAge += dt;
            if (flashAge > 1.7f) flash = Mathf.MoveTowards(flash, 0f, dt * 4f);
        }
        flashGroup.alpha = modeOk ? Mathf.Clamp01(flash) : 0f;
        flashRt.gameObject.SetActive(flashGroup.alpha > 0.005f);
        if (flashGroup.alpha <= 0.005f) return;
        float dockTop = lay.bottomDock.height > 0.5f ? lay.bottomDock.yMin : size.y - HudLayout.DockBottom;
        var lane = lay.centerStage;
        float cx = lane.width > 540f ? lane.center.x : size.x * 0.5f;
        float pop = Mathf.Clamp01(flashAge * 7f);
        flashRt.anchoredPosition = new Vector2(cx, -(dockTop - 16f - 50f - 10f * (1f - pop)));
        flashRt.localScale = Vector3.one * Mathf.Lerp(0.7f, 1f, 1f - (1f - pop) * (1f - pop));
    }
}

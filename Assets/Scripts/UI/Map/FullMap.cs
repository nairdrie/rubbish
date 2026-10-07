using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The full map (Docs/UIPass.md §7.4): a folded <b>paper map</b> titled "RUBBISH CO. ROUTE MAP · property of Chuck" (two fold
/// lines, a coffee ring, a raccoon doodle in the margin) with a plum legend on the right: City %, bags out, late count, a row
/// per district (Trust %, subscribers x/y, bags out), the freshness legend with shapes and the controls.
/// <list type="bullet">
/// <item><b>M</b> toggles the interactive map (<see cref="UiStack.Push"/> with the gameplay lock, free cursor): wheel zoom 1×–4×,
/// drag to pan, <b>Space</b> recentres on you, click a house for its curb card, click a locked district for what unlocks it.</item>
/// <item><b>Holding Tab</b> peeks at 85% opacity with no input lock (you can keep driving); releasing Tab closes it, and
/// Shift+Tab (the Steam overlay) never triggers it.</item>
/// <item><b>Esc</b> closes either (it's the top UiStack screen). Works in the garage too: the depot is "You are here (garage)".</item>
/// </list>
/// Settings → Interface → Map key turns M / Tab off. Co-op: local only; <see cref="PingRequested"/> is the M3 navigator hook.
/// Ticks under the <c>FullMap.Tick</c> profiler marker.
/// </summary>
public sealed class FullMap : UiScreen
{
    public const float MinZoom = 1f, MaxZoom = 4f, OpenZoom = 1.5f, PeekAlpha = 0.85f, StreetLabelZoom = 1.5f;
    static readonly Unity.Profiling.ProfilerMarker TickMarker = new Unity.Profiling.ProfilerMarker("FullMap.Tick");

    static FullMap instance;

    /// <summary>The interactive map (M) is open.</summary>
    public static bool IsOpenInteractive => instance != null && instance.open && !instance.peek;
    /// <summary>The map is up (interactive or the Tab peek).</summary>
    public static bool IsShown => instance != null && instance.open;
    /// <summary>Holding Tab.</summary>
    public static bool IsPeeking => instance != null && instance.open && instance.peek;
    public static FullMap Instance => instance;

#pragma warning disable CS0067, CS0414 // M3 hook (§9 "Shotgun navigator"): not raised in this pass
    /// <summary>A map ping was requested at a world position (M3: the navigator's pings ride WorldFx). Not raised yet.</summary>
    public static event Action<Vector3> PingRequested;
#pragma warning restore CS0067, CS0414

    // ---- view
    Canvas canvas;
    RectTransform root, paper, viewportRt, legend, cardRt, youPin, goblinNote;
    CanvasGroup group;
    Image dim;
    MapView view;
    TextMeshProUGUI cardText, legendCity, legendBags, legendLate, youPinText, noMapText;
    readonly List<(DistrictDef d, TextMeshProUGUI name, TextMeshProUGUI stats, Image swatch)> districtRows = new List<(DistrictDef, TextMeshProUGUI, TextMeshProUGUI, Image)>(4);
    readonly List<TextMeshProUGUI> freshnessLabels = new List<TextMeshProUGUI>(4);
    readonly List<Image> freshnessGlyphs = new List<Image>(4);
    bool built, open, peek, preview, dragging;
    float zoom = OpenZoom, cardAge = 99f, nextLegend;
    Vector2 center, viewSize;
    float baseK = 1f;
    int routeShownCount = -1;
    Vector2 routeShownFirst;
    readonly List<Vector2> routeCopy = new List<Vector2>(64);

    // ---- preview (edit mode)
    public struct PreviewState
    {
        public bool youKnown;
        public Vector3 you;
        public float yaw;
        public string nextStop;
        public List<Vector3> route;
        public bool garage;
    }
    PreviewState previewState;

    public MapView View => view;
    public float Zoom => zoom;
    public Vector2 CenterXZ => center;

    // ------------------------------------------------------------------ boot and keys

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode || instance != null) return;
        var go = new GameObject("[FullMap]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<FullMap>();
        SceneManager.activeSceneChanged += instance.OnSceneChanged;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        PingRequested = null;
    }

    void OnSceneChanged(Scene a, Scene b) => CloseAll();

    protected override void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnSceneChanged;
        view?.Dispose();
        if (instance == this) instance = null;
        base.OnDestroy();
    }

    void Update()
    {
        if (preview) return;
        try { PollKeys(); }
        catch (Exception e) { Debug.LogException(e); }
        if (open)
        {
            try { Tick(Time.unscaledDeltaTime); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    void PollKeys()
    {
        var keys = new KeyState
        {
            m = GameControls.Down(KeyCode.M, GameControls.Action.Map), tabDown = Input.GetKeyDown(KeyCode.Tab), tabHeld = Input.GetKey(KeyCode.Tab),
            shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt), typing = TypingInField(),
            toggleOn = GameSettings.MapKeyToggle, peekOn = GameSettings.MapKeyPeek,
            canOpen = CanOpen(), canStay = CanStayOpen(), open = open, peek = peek,
        };
        switch (Decide(keys))
        {
            case KeyAction.OpenMap: OpenMap(false); break;
            case KeyAction.OpenPeek: OpenMap(true); break;
            case KeyAction.Close: Close(); break;
            case KeyAction.CloseAll: CloseAll(); break;
        }
    }

    /// <summary>The key state one frame of <see cref="PollKeys"/> looks at.</summary>
    public struct KeyState
    {
        public bool m, tabDown, tabHeld, shift, alt, typing, toggleOn, peekOn, canOpen, canStay, open, peek;
    }

    public enum KeyAction { None, OpenMap, OpenPeek, Close, CloseAll }

    /// <summary>
    /// What the map keys do this frame (pure, for the checks): M toggles the interactive map (and turns a peek into it); Tab
    /// pressed alone opens the peek, which closes the moment Tab is released or Shift is held; Shift+Tab (the Steam overlay)
    /// never peeks; nothing opens while typing, while another screen or the laptop owns input, or when the setting is off.
    /// </summary>
    public static KeyAction Decide(KeyState k)
    {
        if (k.open && k.peek && (!k.tabHeld || k.shift)) return KeyAction.Close;
        if (!k.canOpen) return k.open && !k.canStay ? KeyAction.CloseAll : KeyAction.None;
        if (k.toggleOn && k.m && !k.typing) return k.open && !k.peek ? KeyAction.Close : KeyAction.OpenMap;
        if (k.peekOn && !k.open && k.tabDown && !k.shift && !k.alt && !k.typing) return KeyAction.OpenPeek;
        return KeyAction.None;
    }

    /// <summary>A player in a game, nothing else holding the keyboard (laptop, sort station, pause, scene change).</summary>
    static bool CanOpen()
    {
        var pc = PlayerController.Local; // coop:allow(local) input routing for the local screen only
        if (pc == null || SceneController.IsTransitioning) return false;
        if (PlayerController.InteractionFocusOverride != null)
        {
            bool focus = false;
            try { focus = PlayerController.InteractionFocusOverride(pc); } catch (Exception) { focus = false; }
            if (focus) return false;
        }
        var top = UiStack.Top;
        if (top != null && !(top is FullMap)) return false;            // the pause menu, settings, a dialog
        if (PlayerController.IsUIBlocking && UiStack.LocksHeld == 0) return false; // the laptop (a foreign lock)
        return true;
    }

    static bool CanStayOpen() => PlayerController.Local != null && !SceneController.IsTransitioning; // coop:allow(local) input routing

    static bool TypingInField()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        return sel != null && (sel.GetComponent<TMP_InputField>() != null || sel.GetComponent<InputField>() != null);
    }

    // ------------------------------------------------------------------ open / close

    /// <summary>Open the map: interactive (M) or the Tab peek.</summary>
    public void OpenMap(bool asPeek)
    {
        if (!built) Build(null);
        if (open && peek && !asPeek)
        {
            // M while peeking: become the interactive map
            UiStack.Pop(this);
            open = false;
        }
        if (open) return;
        peek = asPeek;
        open = true;
        zoom = asPeek ? MinZoom : OpenZoom;
        Recentre();
        canvas.gameObject.SetActive(true);
        group.alpha = asPeek ? PeekAlpha : 1f;
        group.blocksRaycasts = !asPeek;
        group.interactable = !asPeek;
        dim.color = new Color(0.07f, 0.05f, 0.1f, asPeek ? 0.2f : 0.5f);
        cardRt.gameObject.SetActive(false);
        UiStack.Push(this, lockGameplay: !asPeek);
        UiTheme.Play(asPeek ? "rustle" : UiTheme.SfxOpen, 0.5f, asPeek ? 1.3f : 1f);
        Tick(0f);
    }

    /// <summary>Close (through the stack, so the lock is released once).</summary>
    public new void Close()
    {
        if (!open) return;
        UiStack.Pop(this);
    }

    void CloseAll()
    {
        if (open) UiStack.Pop(this);
    }

    public override void OnHide()
    {
        if (!open) return;
        open = false;
        dragging = false;
        if (canvas != null) canvas.gameObject.SetActive(false);
        if (!peek) UiTheme.Play(UiTheme.SfxBack, 0.4f, 1.1f);
    }

    public override bool OnBack() => false; // Esc: the stack closes us

    void Recentre()
    {
        var pc = PlayerController.Local; // coop:allow(local) HUD/camera only: centre on you
        bool inTown = TownMapAnchor.Active != null;
        var data = TownMapData.Load();
        if (preview) center = previewState.youKnown ? new Vector2(previewState.you.x, previewState.you.z) : DepotXZ(data);
        else if (pc != null && inTown) { var p = pc.IsDriving && pc.currentTruck != null ? pc.currentTruck.transform.position : pc.transform.position; center = new Vector2(p.x, p.z); }
        else center = DepotXZ(data);
        if (zoom <= MinZoom + 0.001f && data != null) center = data.bounds.center;
        ClampCenter();
    }

    static Vector2 DepotXZ(TownMapData d)
    {
        if (d != null) foreach (var p in d.pois) if (p.kind == MapPoiKind.GarageDoor) return p.pos;
        return Vector2.zero;
    }

    // ------------------------------------------------------------------ build

    void Build(Transform parent)
    {
        built = true;
        canvas = UiKit.Canvas("[FullMap Canvas]", UiLayers.Map, parent != null ? parent : null);
        if (parent == null && Application.isPlaying) canvas.transform.SetParent(transform, false);
        root = (RectTransform)canvas.transform;
        group = root.gameObject.AddComponent<CanvasGroup>();
        dim = FeelUi.Img(root, "Dim", null, new Color(0.07f, 0.05f, 0.1f, 0.5f));
        dim.raycastTarget = true;

        paper = FeelUi.Rect("Paper", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200f, 960f));
        paper.localRotation = Quaternion.Euler(0f, 0f, -0.6f);
        var sh = FeelUi.Img(paper, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.35f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(9f, -12f);
        FeelUi.Img(paper, "Body", FeelUi.Rounded, UiTheme.Paper, 0.35f);

        // fold lines (two vertical, one horizontal), a coffee ring and a raccoon doodle in the margin
        Fold(new Vector2(1f / 3f, 0f), new Vector2(1f / 3f, 1f), true);
        Fold(new Vector2(2f / 3f, 0f), new Vector2(2f / 3f, 1f), true);
        Fold(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), false);
        var coffee = FeelUi.Rect("Coffee", paper, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-70f, -40f), new Vector2(150f, 150f));
        var ci = coffee.gameObject.AddComponent<Image>();
        ci.sprite = UiKit.Sprites.CoffeeRing;
        ci.color = new Color(0.55f, 0.36f, 0.2f, 0.28f);
        ci.raycastTarget = false;
        var doodle = FeelUi.Rect("Raccoon", paper, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), new Vector2(34f, 30f), new Vector2(46f, 46f));
        doodle.localRotation = Quaternion.Euler(0f, 0f, 12f);
        var di = doodle.gameObject.AddComponent<Image>();
        di.sprite = MapIcons.Goblin;
        di.color = new Color(0.12f, 0.1f, 0.18f, 0.4f);
        di.raycastTarget = false;

        var title = UiKit.InkLabel(paper, "Title", UiText.MapTitle, 40f, UiTheme.Ink, TextAlignmentOptions.TopLeft, true);
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -20f), new Vector2(760f, 50f));
        title.characterSpacing = 4f;
        var sub = UiKit.InkLabel(paper, "Subtitle", UiText.MapSubtitle, 20f, UiTheme.InkSoft, TextAlignmentOptions.TopLeft, false, FontStyles.Italic);
        UiKit.Place(sub.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -66f), new Vector2(760f, 28f));

        // the map viewport (rect-masked), framed in ink
        var frame = FeelUi.Rect("Frame", paper, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -100f), new Vector2(640f, 830f));
        var fi = FeelUi.Img(frame, "Ink", UiKit.Sprites.RoundedOutline, new Color(0.12f, 0.1f, 0.18f, 0.7f), 0.25f);
        fi.rectTransform.offsetMin = new Vector2(-3f, -3f);
        fi.rectTransform.offsetMax = new Vector2(3f, 3f);
        viewportRt = FeelUi.Rect("Viewport", frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 830f));
        viewportRt.anchorMin = Vector2.zero; viewportRt.anchorMax = Vector2.one; viewportRt.offsetMin = viewportRt.offsetMax = Vector2.zero;
        viewportRt.gameObject.AddComponent<RectMask2D>();
        var bg = viewportRt.gameObject.AddComponent<Image>();
        bg.color = MapRaster.Wild;
        bg.raycastTarget = true;
        var input = viewportRt.gameObject.AddComponent<MapInput>();
        input.map = this;
        view = new MapView(viewportRt, new Vector2(640f, 830f), false);

        noMapText = UiKit.InkLabel(viewportRt, "NoMap", UiText.MapNoMap, 22f, Color.white, TextAlignmentOptions.Center, false, FontStyles.Italic);
        noMapText.textWrappingMode = TextWrappingModes.Normal;
        noMapText.gameObject.SetActive(false);

        // "You are here (garage)"
        youPin = FeelUi.Rect("YouPin", view.Pan, Vector2.zero, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(34f, 34f));
        var pinImg = youPin.gameObject.AddComponent<Image>();
        pinImg.sprite = MapIcons.Pin;
        pinImg.color = UiTheme.StampRed;
        pinImg.raycastTarget = false;
        var pinTag = FeelUi.Rect("Tag", youPin, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(260f, 32f));
        FeelUi.Panel(pinTag, UiTheme.Plum, 0.5f, new Vector2(3f, -4f));
        youPinText = FeelUi.Text(pinTag, "Text", 20f, Color.white, TextAlignmentOptions.Center);
        youPinText.text = UiText.MapYouAreHereGarage;
        pinTag.sizeDelta = new Vector2(youPinText.GetPreferredValues(UiText.MapYouAreHereGarage).x + 24f, 32f);
        youPin.gameObject.SetActive(false);

        // "here be goblins": a hand-lettered note by a neglected pile
        goblinNote = FeelUi.Rect("Goblins", view.Pan, Vector2.zero, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(200f, 30f));
        var gn = UiKit.InkLabel(goblinNote, "Text", UiText.MapGoblins, 22f, new Color(0.55f, 0.1f, 0.12f, 0.9f), TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic | FontStyles.Bold);
        gn.rectTransform.anchorMin = Vector2.zero; gn.rectTransform.anchorMax = Vector2.one; gn.rectTransform.offsetMin = gn.rectTransform.offsetMax = Vector2.zero;
        goblinNote.localRotation = Quaternion.Euler(0f, 0f, 8f);
        goblinNote.gameObject.SetActive(false);

        BuildLegend();

        // the click card
        cardRt = FeelUi.Rect("Card", viewportRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(300f, 60f));
        FeelUi.Panel(cardRt, UiTheme.Plum, 0.6f, new Vector2(4f, -5f));
        cardText = FeelUi.Text(cardRt, "Text", 21f, Color.white, TextAlignmentOptions.Center);
        cardText.textWrappingMode = TextWrappingModes.Normal;
        cardText.rectTransform.offsetMin = new Vector2(14f, 8f);
        cardText.rectTransform.offsetMax = new Vector2(-14f, -8f);
        cardRt.gameObject.SetActive(false);

        canvas.gameObject.SetActive(false);
    }

    void Fold(Vector2 a, Vector2 b, bool vertical)
    {
        var rt = FeelUi.Rect("Fold", paper, a, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        rt.anchorMin = vertical ? new Vector2(a.x, 0.02f) : new Vector2(0.02f, a.y);
        rt.anchorMax = vertical ? new Vector2(a.x, 0.98f) : new Vector2(0.98f, a.y);
        rt.sizeDelta = vertical ? new Vector2(3f, 0f) : new Vector2(0f, 3f);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.12f, 0.1f, 0.18f, 0.07f);
        img.raycastTarget = false;
    }

    void BuildLegend()
    {
        legend = FeelUi.Rect("Legend", paper, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -100f), new Vector2(470f, 830f));
        legend.localRotation = Quaternion.Euler(0f, 0f, 0.8f);
        FeelUi.Panel(legend, UiTheme.Plum);
        float y = -18f;
        TextMeshProUGUI Line(string name, string text, float size, Color c, float h, FontStyles st = FontStyles.Bold, float x = 22f)
        {
            var t = FeelUi.Text(legend, name, size, c, TextAlignmentOptions.MidlineLeft, st);
            UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(470f - x - 22f, h));
            t.text = text;
            y -= h;
            return t;
        }
        Line("Head", UiText.MapLegendTitle, 24f, UiTheme.Gold, 36f).characterSpacing = 3f;
        legendCity = Line("City", "", 26f, Color.white, 36f);
        legendBags = Line("Bags", "", 22f, new Color(0.88f, 0.86f, 0.96f), 30f, FontStyles.Normal);
        legendLate = Line("Late", "", 22f, new Color(1f, 0.66f, 0.3f), 30f, FontStyles.Normal);
        y -= 10f;
        Line("DistrictsHead", UiText.MapDistricts, 22f, UiTheme.Gold, 32f).characterSpacing = 3f;
        foreach (var d in Districts.All)
        {
            var sw = FeelUi.Rect("Swatch", legend, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, y - 8f), new Vector2(18f, 18f));
            var swImg = sw.gameObject.AddComponent<Image>();
            swImg.sprite = FeelUi.Rounded;
            swImg.type = Image.Type.Sliced;
            swImg.pixelsPerUnitMultiplier = 4f;
            swImg.color = MapRaster.DistrictTint(d.id);
            swImg.raycastTarget = false;
            var name = Line("Name", d.name, 22f, Color.white, 30f, FontStyles.Bold, 50f);
            var stats = Line("Stats", "", 19f, new Color(0.82f, 0.8f, 0.92f), 28f, FontStyles.Normal, 50f);
            districtRows.Add((d, name, stats, swImg));
            y -= 6f;
        }
        y -= 8f;
        Line("FreshHead", UiText.MapFreshness, 22f, UiTheme.Gold, 32f).characterSpacing = 3f;
        string[] names = { UiText.MapFresh, UiText.MapLateSoon, UiText.MapLateStage, UiText.MapStinky };
        for (int i = 0; i < 4; i++)
        {
            var g = FeelUi.Rect("Glyph", legend, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(34f + (i % 2) * 210f, y - 16f), new Vector2(22f, 22f));
            var gi = g.gameObject.AddComponent<Image>();
            gi.raycastTarget = false;
            freshnessGlyphs.Add(gi);
            var t = FeelUi.Text(legend, "Fresh" + i, 20f, Color.white, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
            UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(54f + (i % 2) * 210f, y), new Vector2(150f, 32f));
            t.text = names[i];
            freshnessLabels.Add(t);
            if (i % 2 == 1) y -= 34f;
        }
        y -= 10f;
        // controls
        var controls = FeelUi.Rect("Controls", legend, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(22f, 18f), new Vector2(426f, 96f));
        ControlChip(controls, "MMB", UiText.MapControlsZoom, new Vector2(0f, 52f));
        ControlChip(controls, "LMB", UiText.MapControlsDrag, new Vector2(150f, 52f));
        ControlChip(controls, "Space", UiText.MapControlsRecentre, new Vector2(0f, 4f));
        ControlChip(controls, "M/Esc", UiText.MapControlsClose, new Vector2(196f, 4f));
    }

    void ControlChip(RectTransform parent, string key, string label, Vector2 pos)
    {
        var rt = FeelUi.Rect("Ctl " + label, parent, Vector2.zero, Vector2.zero, pos, new Vector2(220f, 44f));
        var cap = new FeelKeyCap(rt, 38f);
        float w = cap.Set(key);
        cap.Root.anchorMin = cap.Root.anchorMax = new Vector2(0f, 0.5f);
        cap.Root.pivot = new Vector2(0f, 0.5f);
        cap.Root.anchoredPosition = Vector2.zero;
        var t = FeelUi.Text(rt, "Label", 20f, Color.white, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        UiKit.Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(w + 8f, 0f), new Vector2(120f, 30f));
        t.text = label;
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>Checks: bytes allocated inside the last Tick (only counted while <see cref="Minimap.MeasureAllocs"/> is on).</summary>
    public static long LastTickAlloc { get; private set; }

    void Tick(float dt)
    {
        long a0 = Minimap.MeasureAllocs ? GC.GetAllocatedBytesForCurrentThread() : 0;
        TickInner(dt);
        if (Minimap.MeasureAllocs) LastTickAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
    }

    void TickInner(float dt)
    {
        using (TickMarker.Auto())
        {
            var data = TownMapData.Load();
            bool hasMap = data != null && data.texture != null;
            noMapText.gameObject.SetActive(!hasMap);
            view.Bind(data);
            if (!hasMap) return;
            LayoutPaper();

            float k = baseK * zoom;
            view.SetView(center, k, 0f, viewSize);
            view.SetLabelVisibility(zoom >= StreetLabelZoom - 0.001f);
            float time = Time.unscaledTime;

            // you, the next stop, the planned route (the same plan the minimap and HUD show)
            bool youKnown;
            Vector3 you;
            float yaw;
            string next;
            bool garage;
            if (preview)
            {
                youKnown = previewState.youKnown; you = previewState.you; yaw = previewState.yaw; next = previewState.nextStop; garage = previewState.garage;
                if (previewState.route != null && routeShownCount != previewState.route.Count)
                {
                    routeCopy.Clear();
                    foreach (var v in previewState.route) routeCopy.Add(new Vector2(v.x, v.z));
                    view.SetRoute(routeCopy);
                    routeShownCount = previewState.route.Count;
                }
            }
            else
            {
                var pc = PlayerController.Local; // coop:allow(local) HUD/camera only
                garage = TownMapAnchor.Active == null || DepotRowWorld.LocalIndoors;
                youKnown = pc != null && !garage;
                var body = pc != null && pc.IsDriving && pc.currentTruck != null ? pc.currentTruck.transform : pc != null ? pc.transform : null;
                you = body != null ? body.position : Vector3.zero;
                var cam = Camera.main;
                yaw = body == null ? 0f : pc.IsDriving ? body.eulerAngles.y : cam != null ? cam.transform.eulerAngles.y : body.eulerAngles.y;
                var hud = FeelRunner.Get() != null ? FeelRunner.Get().Hud : null;
                var t = hud != null && hud.Route != null ? hud.Route.Target : null;
                next = t != null ? t.stopId : null;
                var mm = hud != null ? hud.Minimap : null;
                if (mm != null)
                {
                    var rp = mm.RoutePoints;
                    Vector2 first = rp.Count > 0 ? rp[0] : Vector2.zero;
                    if (rp.Count != routeShownCount || first != routeShownFirst)
                    {
                        routeShownCount = rp.Count;
                        routeShownFirst = first;
                        routeCopy.Clear();
                        for (int i = 0; i < rp.Count; i++) routeCopy.Add(rp[i]);
                        view.SetRoute(garage ? null : routeCopy);
                    }
                }
            }
            view.Tick(time, youKnown, you, yaw, next, garage);

            // "You are here (garage)" at the depot
            youPin.gameObject.SetActive(garage);
            if (garage) youPin.anchoredPosition = view.ToPan(DepotXZ(data)) + new Vector2(0f, 4f);

            // "here be goblins" by the first neglected pile
            PlaceGoblins(data);

            // the legend (a few times a second)
            if (!Application.isPlaying || Time.unscaledTime >= nextLegend)
            {
                nextLegend = Time.unscaledTime + 0.25f;
                RefreshLegend(data);
            }

            if (cardRt.gameObject.activeSelf)
            {
                cardAge += dt;
                if (cardAge > 5f) cardRt.gameObject.SetActive(false);
            }
        }
    }

    void LayoutPaper()
    {
        // fit the paper to the screen (reference units), the map viewport at the town's aspect
        var rs = root.rect.size;
        float maxH = Mathf.Min(980f, rs.y - 60f), legendW = Mathf.Clamp(rs.x * 0.24f, 360f, 470f);
        float vh = Mathf.Max(300f, maxH - 130f);
        var data = view.Data;
        float aspect = data != null ? data.bounds.width / data.bounds.height : 0.735f;
        float vw = vh * aspect;
        float paperW = 30f + vw + 30f + legendW + 28f;
        if (paperW > rs.x - 40f)
        {
            float s = (rs.x - 40f) / paperW;
            vw *= s; vh *= s; legendW = Mathf.Max(320f, legendW * s);
            paperW = 30f + vw + 30f + legendW + 28f;
        }
        paper.sizeDelta = new Vector2(paperW, vh + 130f);
        var frame = (RectTransform)viewportRt.parent;
        frame.sizeDelta = new Vector2(vw, vh);
        legend.sizeDelta = new Vector2(legendW, vh);
        viewSize = new Vector2(vw, vh);
        baseK = data != null ? Mathf.Min(vw / data.bounds.width, vh / data.bounds.height) : 1f;
        ClampCenter();
    }

    void ClampCenter()
    {
        var data = view != null ? view.Data : TownMapData.Load();
        if (data == null) return;
        float k = baseK * zoom;
        Vector2 half = viewSize * 0.5f / Mathf.Max(0.0001f, k);
        var b = data.bounds;
        center.x = half.x * 2f >= b.width ? b.center.x : Mathf.Clamp(center.x, b.xMin + half.x, b.xMax - half.x);
        center.y = half.y * 2f >= b.height ? b.center.y : Mathf.Clamp(center.y, b.yMin + half.y, b.yMax - half.y);
    }

    void PlaceGoblins(TownMapData data)
    {
        int found = -1;
        if (Application.isPlaying)
        {
            for (int i = 0; i < data.houses.Count; i++)
                if (FeelWorld.At(data.houses[i].stopId).Neglected) { found = i; break; }
        }
        else if (data.houses.Count > 5) found = 5;
        bool on = found >= 0;
        if (goblinNote.gameObject.activeSelf != on) goblinNote.gameObject.SetActive(on);
        if (on) goblinNote.anchoredPosition = view.ToPan(data.houses[found].curb) + new Vector2(14f, 18f);
    }

    int legCity = -1, legBags = -1, legLate = -1;
    readonly int[] legRowKey = new int[8];
    bool legCb, legInit;

    void RefreshLegend(TownMapData data)
    {
        // strings are rebuilt only when a number changes (no garbage while the map just sits open)
        bool live = Application.isPlaying;
        var route = live ? TrashRoute.Current : null; // coop:allow(current) presentation: the HUD's own counts
        float city = live ? (FeelWorld.IsLive ? FeelWorld.Cleanliness : 100f) : 87f;
        int cityI = Mathf.RoundToInt(city);
        if (cityI != legCity)
        {
            legCity = cityI;
            legendCity.text = $"{UiText.MapCity}  <color=#{FeelUi.Hex(FeelWorld.CleanlinessColor(city))}>{cityI}% {FeelWorld.CleanlinessWord(city)}</color>";
        }
        int bags = live ? FeelWorld.TotalCurbBags(route) : 23, late = live ? FeelWorld.LateBags(route) : 4;
        if (bags != legBags) { legBags = bags; legendBags.SetText(UiText.MapBagsOut + ": {0}", bags); }
        if (late != legLate) { legLate = late; legendLate.SetText(UiText.MapLate + ": {0}", late); }
        for (int r = 0; r < districtRows.Count; r++)
        {
            var row = districtRows[r];
            bool unlocked = Districts.IsUnlocked(row.d);
            int out1 = 0;
            for (int i = 0; i < data.houses.Count; i++)
                if (data.houses[i].districtId == row.d.id) out1 += live ? FeelWorld.At(data.houses[i].stopId).bags : 0;
            int trust = Mathf.RoundToInt(Progression.Trust01(row.d) * 100f), subs = Progression.Subscribers(row.d), houses = Progression.Houses(row.d);
            int key = unlocked ? ((trust * 1000 + subs) * 1000 + houses) * 1000 + out1 + 1 : -1;
            if (legInit && legRowKey[r] == key) continue;
            legRowKey[r] = key;
            row.stats.text = unlocked
                ? $"{UiText.MapTrust} {trust}% · {subs}/{houses} houses · {out1} out"
                : $"<color=#FFD45A>{UiText.MapLocked}</color> · Rank {row.d.rankRequired} + {Economy.Money(row.d.permitPrice)} permit";
            row.name.color = unlocked ? Color.white : new Color(0.7f, 0.68f, 0.78f);
        }
        bool cb = !live || GameSettings.ColorblindMarkers;
        if (!legInit || cb != legCb)
        {
            legCb = cb;
            for (int i = 0; i < 4; i++)
            {
                var stage = (FreshnessStage)i;
                freshnessGlyphs[i].sprite = cb ? FeelUi.Glyph(stage) : FeelUi.Circle;
                freshnessGlyphs[i].color = CurbTrash.StageColor(stage);
            }
        }
        legInit = true;
    }

    // ------------------------------------------------------------------ input (interactive only)

    internal void OnDrag(Vector2 deltaRef)
    {
        if (peek) return;
        dragging = true;
        center -= deltaRef / Mathf.Max(0.0001f, baseK * zoom);
        ClampCenter();
    }

    internal void OnScroll(float wheel, Vector2 localFromCentre)
    {
        if (peek || Mathf.Abs(wheel) < 0.01f) return;
        var before = view.FromViewport(localFromCentre);
        zoom = Mathf.Clamp(zoom * (wheel > 0f ? 1.15f : 1f / 1.15f), MinZoom, MaxZoom);
        view.SetView(center, baseK * zoom, 0f, viewSize);
        var after = view.FromViewport(localFromCentre);
        center += before - after; // keep the point under the cursor still
        ClampCenter();
    }

    internal void OnClick(Vector2 localFromCentre)
    {
        if (peek) return;
        if (dragging) { dragging = false; return; }
        var data = view.Data;
        if (data == null) return;
        var xz = view.FromViewport(localFromCentre);
        float k = baseK * zoom;
        int hi = data.HouseNear(new Vector3(xz.x, 0f, xz.y), Mathf.Max(6f, 16f / k));
        string text = null;
        if (hi >= 0)
        {
            var h = data.houses[hi];
            var d = Districts.ForStopId(h.stopId);
            var st = Application.isPlaying ? FeelWorld.At(h.stopId) : default;
            string fresh = st.bags > 0 ? FeelWorld.FreshnessShort(st.oldestAge) : "";
            text = UiText.MapHouseCard(h.address, st.bags, fresh, d.name);
        }
        else
        {
            var d = Districts.AtPosition(new Vector3(xz.x, 0f, xz.y));
            if (d != null && !Districts.IsUnlocked(d))
                text = UiText.MapLockedCard(d.name, d.rankRequired, Economy.Money(d.permitPrice), Progression.Rank);
        }
        if (text == null) { cardRt.gameObject.SetActive(false); return; }
        cardText.text = text;
        float w = Mathf.Min(viewSize.x - 40f, cardText.GetPreferredValues(text, 100000f, 0f).x + 34f);
        float ch = cardText.GetPreferredValues(text, w - 28f, 0f).y + 18f;
        cardRt.sizeDelta = new Vector2(w, ch);
        var p = localFromCentre + new Vector2(0f, 14f);
        p.x = Mathf.Clamp(p.x, -viewSize.x * 0.5f + w * 0.5f + 8f, viewSize.x * 0.5f - w * 0.5f - 8f);
        p.y = Mathf.Clamp(p.y, -viewSize.y * 0.5f + 8f, viewSize.y * 0.5f - ch - 8f);
        cardRt.anchoredPosition = p;
        cardRt.gameObject.SetActive(true);
        cardRt.SetAsLastSibling();
        cardAge = 0f;
        UiTheme.Play(UiTheme.SfxPress, 0.35f, 1.2f);
    }

    internal void OnKeySpace() => Recentre();

    void LateUpdate()
    {
        if (preview || !open || peek) return;
        if (GameControls.Down(KeyCode.Space, GameControls.Action.Jump)) { zoom = Mathf.Max(zoom, OpenZoom); Recentre(); }
    }

    // ------------------------------------------------------------------ preview (edit mode screenshots)

    /// <summary>Build a full map under <paramref name="parent"/> rendered through <paramref name="cam"/> (edit mode).</summary>
    public static FullMap CreatePreview(Transform parent, Camera cam, int w, int h, float uiScale, bool asPeek, PreviewState state, float zoomLevel)
    {
        var go = new GameObject("FullMap Preview");
        go.transform.SetParent(parent, false);
        var m = go.AddComponent<FullMap>();
        m.preview = true;
        m.previewState = state;
        m.Build(go.transform);
        UiKit.PreviewCanvas(m.canvas, cam, w, h, uiScale);
        m.canvas.gameObject.SetActive(true);
        m.open = true;
        m.peek = asPeek;
        m.zoom = Mathf.Clamp(zoomLevel, MinZoom, MaxZoom);
        m.group.alpha = asPeek ? PeekAlpha : 1f;
        m.dim.color = new Color(0.07f, 0.05f, 0.1f, asPeek ? 0.2f : 0.5f);
        Canvas.ForceUpdateCanvases();
        m.view.Bind(TownMapData.Load());
        m.LayoutPaper();
        m.Recentre();
        if (m.zoom > MinZoom && state.youKnown) { m.center = new Vector2(state.you.x, state.you.z); m.ClampCenter(); }
        m.Tick(0f);
        Canvas.ForceUpdateCanvases();
        m.Tick(0f);
        return m;
    }

    /// <summary>Preview: show the click card for a world point.</summary>
    public void PreviewClick(Vector3 world)
    {
        var local = view.ToViewport(new Vector2(world.x, world.z));
        dragging = false;
        OnClick(local);
    }

    /// <summary>The input catcher on the viewport (drag, wheel, click).</summary>
    sealed class MapInput : MonoBehaviour, IDragHandler, IScrollHandler, IPointerClickHandler, IBeginDragHandler
    {
        public FullMap map;
        RectTransform rt;
        void Awake() => rt = (RectTransform)transform;

        Vector2 Local(PointerEventData e)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var lp);
            return lp - rt.rect.center;
        }

        float Scale => map.canvas != null ? Mathf.Max(0.0001f, map.canvas.scaleFactor) : 1f;
        public void OnBeginDrag(PointerEventData e) => map.dragging = true;
        public void OnDrag(PointerEventData e) => map.OnDrag(e.delta / Scale);
        public void OnScroll(PointerEventData e) => map.OnScroll(e.scrollDelta.y, Local(e));
        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging) { map.dragging = false; return; }
            map.dragging = false;
            map.OnClick(Local(e));
        }
    }
}

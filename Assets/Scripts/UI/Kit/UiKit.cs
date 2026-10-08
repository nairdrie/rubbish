using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Sticker button looks (Docs/UIPass.md §3).</summary>
public enum UiButtonStyle
{
    /// <summary>Plum sticker, white outlined text (the default "game" button).</summary>
    Sticker,
    /// <summary>Gold sticker, ink text (the main action on a screen: Back/Save, Play).</summary>
    Primary,
    /// <summary>Green sticker (confirm, Join).</summary>
    Go,
    /// <summary>Stamp-red sticker (Quit, Kick, Delete).</summary>
    Danger,
    /// <summary>Manila sticker with ink text, for paper surfaces (Cancel, Reset tab).</summary>
    Paper,
    /// <summary>A folder tab (used by <see cref="UiTabs"/>).</summary>
    Tab,
    /// <summary>A small flat key (cycler arrows, Show/Copy).</summary>
    Quiet,
}

/// <summary>
/// The UI pass's widget kit (Docs/UIPass.md D1, §10.2): runtime-built uGUI on top of <see cref="FeelUi"/>'s procedural sprites
/// and outlined TMP text. No prefabs, no asset files. Everything animates on unscaled time, so it works during the solo
/// freeze, and everything is usable in edit mode for preview screenshots (no Update needed for the resting look).
/// <para>Forms (sliders, toggles, cyclers) are built as <b>rows</b>: a label (+ optional description) on the left, the
/// control on the right, sized for a vertical list. Text colours follow the surface: ink on paper (the default) or white
/// outlined on plum (<see cref="OnPlum"/>).</para>
/// </summary>
public static partial class UiKit
{
    // ------------------------------------------------------------------ surface context

    static int plumDepth;

    /// <summary>Build rows and labels for a plum surface (white outlined text) until the scope is disposed.</summary>
    public static IDisposable OnPlum() { plumDepth++; return new PlumScope(); }

    sealed class PlumScope : IDisposable
    {
        bool done;
        public void Dispose() { if (done) return; done = true; plumDepth = Mathf.Max(0, plumDepth - 1); }
    }

    /// <summary>True while building for paper (ink text).</summary>
    public static bool InkSurface => plumDepth == 0;

    /// <summary>Row geometry (reference px).</summary>
    public const float RowHeight = 76f, RowHeightWithDesc = 96f, ControlWidth = 520f, RowPadX = 28f;

    // ------------------------------------------------------------------ canvases and surfaces

    /// <summary>
    /// A screen-space overlay canvas at <paramref name="layer"/> (<see cref="UiLayers"/>) with a scaler that follows the UI
    /// scale setting and a raycaster. Without a parent it survives scene loads (play mode).
    /// </summary>
    public static Canvas Canvas(string name, int layer, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
        if (parent != null) go.transform.SetParent(parent, false);
        else if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
        var c = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = layer;
        c.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal |
                                      AdditionalCanvasShaderChannels.Tangent;
        var s = go.AddComponent<CanvasScaler>();
        UiScale.Register(s);
        go.AddComponent<GraphicRaycaster>();
        return c;
    }

    /// <summary>
    /// Edit-mode preview: render <paramref name="c"/> through <paramref name="cam"/> (ScreenSpaceCamera) at the scale a real
    /// <paramref name="w"/>×<paramref name="h"/> screen would use (match 0.5 × the UI scale setting).
    /// </summary>
    public static void PreviewCanvas(Canvas c, Camera cam, int w, int h, float uiScale = -1f)
    {
        if (c == null) return;
        c.renderMode = RenderMode.ScreenSpaceCamera;
        c.worldCamera = cam;
        c.planeDistance = 1f;
        var s = c.GetComponent<CanvasScaler>();
        if (s != null)
        {
            UiScale.Unregister(s);
            s.enabled = false;
        }
        c.scaleFactor = UiScale.PreviewScaleFactor(w, h, uiScale);
    }

    /// <summary>A paper sheet (document surface) centred in <paramref name="p"/>: offset shadow + paper body. Returns the body rect.</summary>
    public static RectTransform Paper(Transform p, Vector2 size)
    {
        var rt = FeelUi.Rect("Paper", p, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var shadow = FeelUi.Img(rt, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.35f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(8f, -10f);
        FeelUi.Img(rt, "Body", FeelUi.Rounded, UiTheme.Paper, 0.35f);
        return rt;
    }

    /// <summary>A plum panel (game surface) centred in <paramref name="p"/>. Returns its rect.</summary>
    public static RectTransform Plum(Transform p, Vector2 size)
    {
        var rt = FeelUi.Rect("Plum", p, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        FeelUi.Panel(rt, UiTheme.Plum);
        return rt;
    }

    /// <summary>A text label for the current surface (ink on paper / white outlined on plum).</summary>
    public static TextMeshProUGUI Label(Transform p, string name, string text, float size, TextAlignmentOptions align,
                                        bool display = false, FontStyles style = FontStyles.Bold, Color? color = null)
    {
        var t = FeelUi.Text(p, name, Mathf.Max(UiTheme.MinText, size), color ?? (InkSurface ? UiTheme.Ink : UiTheme.TextOnPlum), align, style);
        var font = display ? UiTheme.DisplayFont : UiTheme.BodyFont;
        if (font != null) t.font = font;
        var mat = InkSurface ? UiTheme.InkMaterial(display) : UiTheme.OutlineMaterial(display);
        if (mat != null) t.fontSharedMaterial = mat;
        t.text = text ?? "";
        return t;
    }

    /// <summary>Ink text regardless of the surface (stamps, key caps, tape).</summary>
    public static TextMeshProUGUI InkLabel(Transform p, string name, string text, float size, Color color, TextAlignmentOptions align,
                                           bool display = true, FontStyles style = FontStyles.Bold)
    {
        var t = FeelUi.Text(p, name, Mathf.Max(UiTheme.MinText, size), color, align, style);
        var font = display ? UiTheme.DisplayFont : UiTheme.BodyFont;
        if (font != null) t.font = font;
        var mat = UiTheme.InkMaterial(display);
        if (mat != null) t.fontSharedMaterial = mat;
        t.text = text ?? "";
        return t;
    }

    /// <summary>White outlined text regardless of the surface.</summary>
    public static TextMeshProUGUI OutlineLabel(Transform p, string name, string text, float size, Color color, TextAlignmentOptions align,
                                               bool display = true, FontStyles style = FontStyles.Bold)
    {
        var t = FeelUi.Text(p, name, Mathf.Max(UiTheme.MinText, size), color, align, style);
        var font = display ? UiTheme.DisplayFont : UiTheme.BodyFont;
        if (font != null) t.font = font;
        var mat = UiTheme.OutlineMaterial(display);
        if (mat != null) t.fontSharedMaterial = mat;
        t.text = text ?? "";
        return t;
    }

    /// <summary>Anchor <paramref name="rt"/> to its parent's edges with insets (left, bottom, right, top).</summary>
    public static RectTransform Inset(RectTransform rt, float left, float bottom, float right, float top)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
        return rt;
    }

    /// <summary>Place <paramref name="rt"/> by anchor/pivot/position/size in one call.</summary>
    public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    static LayoutElement Layout(GameObject go, float height, float width = -1f)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = height;
        if (width > 0f) le.minWidth = le.preferredWidth = width;
        return le;
    }

    // ------------------------------------------------------------------ buttons

    /// <summary>A sticker button (§3): tilt, hover spring + wobble + squeak, press squash + pop, focus stripe + bag cursor.</summary>
    public static UiButton Button(Transform p, string label, Action click, UiButtonStyle style = UiButtonStyle.Sticker)
    {
        Vector2 size = style switch
        {
            UiButtonStyle.Quiet => new Vector2(56f, 56f),
            UiButtonStyle.Tab => new Vector2(220f, 70f),
            UiButtonStyle.Paper => new Vector2(240f, 68f),
            _ => new Vector2(420f, 80f),
        };
        var rt = FeelUi.Rect("Button " + label, p, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var b = rt.gameObject.AddComponent<UiButton>();
        b.Build(label, style, click);
        Layout(rt.gameObject, size.y, size.x);
        return b;
    }

    // ------------------------------------------------------------------ rows

    /// <summary>
    /// A form row: label (+ description) on the left, a control slot on the right. Height 76 (96 with a description).
    /// Highlights and shows the focus stripe while its control is selected or hovered.
    /// </summary>
    public static UiRow Row(Transform p, string label, string description = null)
    {
        bool desc = !string.IsNullOrEmpty(description);
        float h = desc ? RowHeightWithDesc : RowHeight;
        var rt = FeelUi.Rect("Row " + label, p, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(900f, h));
        Layout(rt.gameObject, h);
        var row = rt.gameObject.AddComponent<UiRow>();
        row.Build(label, description);
        return row;
    }

    /// <summary>A labelled slider row. <paramref name="fmt"/> formats the value readout ("80", "1.00×").</summary>
    public static UiSlider Slider(Transform p, string label, float min, float max, float v, Func<float, string> fmt, Action<float> changed)
    {
        var row = Row(p, label);
        var s = row.Slot.gameObject.AddComponent<UiSlider>();
        s.Build(row, min, max, v, fmt, changed);
        return s;
    }

    /// <summary>A labelled on/off pill row.</summary>
    public static UiToggle Toggle(Transform p, string label, bool v, Action<bool> changed)
    {
        var row = Row(p, label);
        var t = row.Slot.gameObject.AddComponent<UiToggle>();
        t.Build(row, v, changed);
        return t;
    }

    /// <summary>A labelled ◀ value ▶ row (Left/Right cycle it when it's selected).</summary>
    public static UiCycler Cycler(Transform p, string label, IReadOnlyList<string> options, int index, Action<int> changed)
    {
        var row = Row(p, label);
        var c = row.Slot.gameObject.AddComponent<UiCycler>();
        c.Build(row, options, index, changed);
        return c;
    }

    /// <summary>Folder tabs in a row (Left/Right move between them).</summary>
    public static UiTabs Tabs(Transform p, IReadOnlyList<string> names, Action<int> changed)
    {
        var rt = FeelUi.Rect("Tabs", p, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(1100f, 70f));
        var t = rt.gameObject.AddComponent<UiTabs>();
        t.Build(names, changed);
        return t;
    }

    /// <summary>A vertical scroll list: masked viewport, a content column with a VerticalLayoutGroup, and a slim ink bar.</summary>
    public static ScrollRect ScrollList(Transform p, Vector2 size)
    {
        var rt = FeelUi.Rect("ScrollList", p, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var sr = rt.gameObject.AddComponent<ScrollRect>();
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 40f;
        sr.inertia = true;
        sr.decelerationRate = 0.12f;

        var hit = FeelUi.Img(rt, "Hit", null, new Color(0f, 0f, 0f, 0f));
        hit.raycastTarget = true; // wheel anywhere over the list scrolls it

        var vp = FeelUi.Stretch("Viewport", rt);
        vp.offsetMax = new Vector2(-22f, 0f);
        vp.gameObject.AddComponent<RectMask2D>();
        sr.viewport = vp;

        var content = FeelUi.Rect("Content", vp, new Vector2(0f, 1f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.sizeDelta = new Vector2(0f, 0f);
        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4f;
        vlg.padding = new RectOffset(0, 0, 4, 12);
        var fit = content.gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        sr.content = content;

        // slim scrollbar: ink track, tangerine thumb
        var barRt = FeelUi.Rect("Scrollbar", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(12f, 0f));
        barRt.anchorMin = new Vector2(1f, 0f);
        barRt.anchorMax = new Vector2(1f, 1f);
        barRt.offsetMin = new Vector2(-12f, 6f);
        barRt.offsetMax = new Vector2(0f, -6f);
        var track = FeelUi.Img(barRt, "Track", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Ink, InkSurface ? 0.12f : 0.4f), 0.2f);
        track.raycastTarget = true;
        var slide = FeelUi.Stretch("Sliding", barRt);
        var thumb = FeelUi.Img(slide, "Thumb", FeelUi.Rounded, UiTheme.Tangerine, 0.2f);
        thumb.raycastTarget = true;
        var bar = barRt.gameObject.AddComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = thumb.rectTransform;
        bar.targetGraphic = thumb;
        var nav = bar.navigation;
        nav.mode = Navigation.Mode.None;
        bar.navigation = nav;
        sr.verticalScrollbar = bar;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        sr.verticalScrollbarSpacing = 0f;

        // "more below": the list fades into the paper at the bottom edge while there's more to scroll
        var fade = FeelUi.Img(rt, "MoreFade", Sprites.FadeUp, UiTheme.WithAlpha(InkSurface ? UiTheme.Paper : UiTheme.PlumSolid, 1f));
        fade.rectTransform.anchorMin = new Vector2(0f, 0f);
        fade.rectTransform.anchorMax = new Vector2(1f, 0f);
        fade.rectTransform.pivot = new Vector2(0.5f, 0f);
        fade.rectTransform.offsetMin = new Vector2(0f, 0f);
        fade.rectTransform.offsetMax = new Vector2(-22f, 44f);
        rt.gameObject.AddComponent<UiScrollHints>().Init(sr, fade);
        return sr;
    }

    /// <summary>A section heading inside a list (ink caps with a dashed rule).</summary>
    public static RectTransform Heading(Transform p, string text)
    {
        var rt = FeelUi.Rect("Heading " + text, p, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(900f, 56f));
        Layout(rt.gameObject, 56f);
        var t = Label(rt, "Text", text, 24f, TextAlignmentOptions.BottomLeft, true);
        t.characterSpacing = 6f;
        Inset(t.rectTransform, RowPadX, 14f, RowPadX, 0f);
        var rule = FeelUi.Img(rt, "Rule", Sprites.Dash, UiTheme.WithAlpha(InkSurface ? UiTheme.Ink : Color.white, 0.35f));
        rule.type = UnityEngine.UI.Image.Type.Tiled;
        rule.rectTransform.anchorMin = new Vector2(0f, 0f);
        rule.rectTransform.anchorMax = new Vector2(1f, 0f);
        rule.rectTransform.offsetMin = new Vector2(RowPadX, 4f);
        rule.rectTransform.offsetMax = new Vector2(-RowPadX, 8f);
        return rt;
    }

    /// <summary>A key cap, auto-sized: "E", "Space", "Esc", "LMB"/"RMB"/"MMB" (mouse icon), "Hold R", "E/LMB" (two caps).</summary>
    public static RectTransform KeyCap(Transform p, string key)
    {
        var root = FeelUi.Rect("Key " + key, p, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0f, 44f));
        var h = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.spacing = 6f;
        var fit = root.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        string k = (key ?? "").Trim();
        if (k.StartsWith("Hold ", StringComparison.OrdinalIgnoreCase))
        {
            var hold = Label(root, "Hold", "HOLD", 18f, TextAlignmentOptions.MidlineLeft, false, FontStyles.Bold,
                             InkSurface ? UiTheme.InkSoft : new Color(0.85f, 0.85f, 0.9f));
            Layout(hold.gameObject, 44f, hold.GetPreferredValues("HOLD").x + 2f);
            k = k.Substring(5).Trim();
        }
        // "E/LMB" = either key, "Shift+Tab" = together
        int start = 0;
        for (int i = 0; i <= k.Length; i++)
        {
            if (i < k.Length && k[i] != '/' && k[i] != '+') continue;
            string part = k.Substring(start, i - start).Trim();
            if (part.Length > 0) OneCap(root, part);
            if (i < k.Length)
            {
                string sep = k[i].ToString();
                var s = Label(root, "Sep", sep, 22f, TextAlignmentOptions.Center, false, FontStyles.Bold,
                              InkSurface ? UiTheme.InkSoft : Color.white);
                Layout(s.gameObject, 44f, 14f);
            }
            start = i + 1;
        }
        return root;
    }

    static void OneCap(Transform p, string k)
    {
        int mouse = k == "LMB" ? 0 : k == "RMB" ? 1 : k == "MMB" ? 2 : -1;
        float w;
        var cap = FeelUi.Rect("Cap " + k, p, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(44f, 44f));
        var edge = FeelUi.Img(cap, "Edge", FeelUi.Rounded, UiTheme.Hex(0x8F8576), 0.3f);
        edge.rectTransform.offsetMin = new Vector2(0f, -4f);
        edge.rectTransform.offsetMax = new Vector2(0f, -4f);
        FeelUi.Img(cap, "Top", FeelUi.Rounded, UiTheme.Hex(0xFBF7EE), 0.3f);
        var line = FeelUi.Img(cap, "Line", Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.6f), 0.3f);
        line.rectTransform.offsetMin = new Vector2(0f, -4f);
        if (mouse >= 0)
        {
            var m = FeelUi.Img(cap, "Mouse", Sprites.Mouse(mouse), UiTheme.Ink);
            Place(m.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(26f, 34f));
            w = 44f;
        }
        else
        {
            var t = InkLabel(cap, "Text", k, k.Length <= 1 ? 24f : 20f, UiTheme.Ink, TextAlignmentOptions.Center, false);
            w = Mathf.Max(44f, t.GetPreferredValues(k).x + 22f);
        }
        Layout(cap.gameObject, 44f, w);
    }

    // ------------------------------------------------------------------ text field

    /// <summary>A paper text field (ink text, dashed underline feel), e.g. the join code or a company name.</summary>
    public static TMP_InputField TextField(Transform p, string placeholder, int maxChars, TMP_InputField.CharacterValidation v)
    {
        var rt = FeelUi.Rect("TextField", p, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 72f));
        Layout(rt.gameObject, 72f, 520f);
        var bg = FeelUi.Img(rt, "Body", FeelUi.Rounded, UiTheme.Hex(0xFFFDF6), 0.3f);
        bg.raycastTarget = true;
        FeelUi.Img(rt, "Border", Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.7f), 0.3f);

        var area = FeelUi.Stretch("TextArea", rt);
        area.offsetMin = new Vector2(20f, 6f);
        area.offsetMax = new Vector2(-20f, -6f);
        area.gameObject.AddComponent<RectMask2D>();

        var ph = InkLabel(area, "Placeholder", placeholder, 28f, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        var text = InkLabel(area, "Text", "", 30f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft, false);

        var f = rt.gameObject.AddComponent<TMP_InputField>();
        f.textViewport = area;
        f.textComponent = text;
        f.placeholder = ph;
        f.targetGraphic = bg;
        f.characterLimit = Mathf.Max(0, maxChars);
        f.characterValidation = v;
        f.caretColor = UiTheme.Ink;
        f.customCaretColor = true;
        f.caretWidth = 3;
        f.selectionColor = UiTheme.WithAlpha(UiTheme.Gold, 0.6f);
        f.richText = false;
        var colors = f.colors;
        colors.highlightedColor = new Color(1f, 0.97f, 0.88f);
        colors.selectedColor = Color.white;
        f.colors = colors;
        return f;
    }

    // ------------------------------------------------------------------ dialogs

    /// <summary>
    /// A paper confirm dialog on top of everything (<see cref="UiLayers.Dialog"/>). <paramref name="done"/> gets true for
    /// <paramref name="yes"/>, false for <paramref name="no"/> / Esc / the countdown running out. With
    /// <paramref name="autoRevertSeconds"/> &gt; 0 the body's "{0}" counts down ("Reverting in 10…") and the default is No.
    /// </summary>
    public static void Confirm(string title, string body, string yes, string no, Action<bool> done, float autoRevertSeconds = 0f)
    {
        UiDialog.Show(title, body, yes, no, done, autoRevertSeconds);
    }

    // ------------------------------------------------------------------ misc helpers

    /// <summary>A stable small tilt (degrees) from a label, so a sticker always leans the same way (no RNG).</summary>
    public static float TiltFor(string label, float maxDeg = 1.5f)
    {
        uint h = 2166136261;
        if (label != null)
            for (int i = 0; i < label.Length; i++) { h ^= label[i]; h *= 16777619; }
        return ((h % 2001u) / 1000f - 1f) * maxDeg;
    }

    /// <summary>Select <paramref name="go"/> for keyboard focus (if an EventSystem exists).</summary>
    public static void Focus(GameObject go)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null || go == null || !go.activeInHierarchy) return;
        es.SetSelectedGameObject(go);
    }

    /// <summary>Link selectables top to bottom with explicit Up/Down navigation (Left/Right stay with the control).</summary>
    public static void ChainVertical(IReadOnlyList<Selectable> list, Selectable above = null, Selectable below = null)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null) continue;
            var n = s.navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnUp = i > 0 ? list[i - 1] : above;
            n.selectOnDown = i < list.Count - 1 ? list[i + 1] : below;
            s.navigation = n;
        }
    }

    /// <summary>Scroll a vertical list just far enough that <paramref name="row"/> is inside the viewport (keyboard / pad
    /// focus walking a long list).</summary>
    public static void EnsureVisible(ScrollRect scroll, RectTransform row)
    {
        if (scroll == null || row == null) return;
        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, row);
        Rect view = scroll.viewport.rect;
        float move = bounds.min.y < view.yMin ? view.yMin - bounds.min.y : bounds.max.y > view.yMax ? view.yMax - bounds.max.y : 0f;
        if (Mathf.Abs(move) < 0.5f) return;
        scroll.StopMovement();
        scroll.content.anchoredPosition += Vector2.up * move;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => plumDepth = 0;
}

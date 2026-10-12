using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum HintTone { Normal, Good, Bad, Gold }

/// <summary>
/// The only screen furniture of seat mode (Docs/Casino.md §9, §12): ONE thin uGUI strip along the bottom edge, no panel, only text with
/// the HUD's outline and a soft shadow behind it. Up to three short lines, bottom to top: the CONTEXT HINT (key chips: what you can do
/// right now), a MESSAGE line (an answer from the table, or what the cursor is over) and an INFO line (countdown, pending stake, how
/// you feel). Built with <see cref="UiKit"/>'s canvas (UI scale setting, <see cref="UiLayers"/>), shown only while a controller says so
/// and the seat camera is up, hidden whenever another UI holds the player (pause, laptop...). It never pushes <c>UiStack</c> and never
/// takes the gameplay lock: the seat camera already owns input. Respects the "control hints" interface setting (key line only).
/// <para><b>Markup</b> for hint and info lines: <c>[Q/E] chip   [Space] add chip   [Esc] leave</c>: <c>[...]</c> becomes a key chip, the
/// text after it its label. Chips named LMB / RMB / MMB show a mouse; A / B / X / Y are coloured like the pad's face buttons.</para>
/// </summary>
public sealed class CasinoHintStrip : MonoBehaviour
{
    public const int Layer = 96;

    public struct Seg
    {
        public bool chip;
        public string text;
    }

    public static CasinoHintStrip Instance { get; private set; }

    /// <summary>Reference-unit layout (the canvas scales with the screen, so the same numbers hold at 1080p and 4K): the stack's bottom margin, the pitch between its rows, the soft shadow's height.</summary>
    public const float BaseMargin = 14f, RowPitch = 38f, ShadeHeight = 210f;

    /// <summary>The strip, made on first use (a DontDestroyOnLoad canvas).</summary>
    public static CasinoHintStrip Ensure()
    {
        if (Instance != null) return Instance;
        var c = UiKit.Canvas("[CasinoHintStrip]", Layer);
        Instance = c.gameObject.AddComponent<CasinoHintStrip>();
        Instance.Build(c);
        return Instance;
    }

    // ------------------------------------------------------------------ pure markup

    /// <summary>Split <c>[key] label [key] label</c> into chips and plain text (whitespace trimmed, empty pieces dropped).</summary>
    public static List<Seg> Parse(string markup)
    {
        var list = new List<Seg>();
        if (string.IsNullOrEmpty(markup)) return list;
        int i = 0;
        while (i < markup.Length)
        {
            int open = markup.IndexOf('[', i);
            if (open < 0) { Add(list, false, markup.Substring(i)); break; }
            if (open > i) Add(list, false, markup.Substring(i, open - i));
            int close = markup.IndexOf(']', open + 1);
            if (close < 0) { Add(list, false, markup.Substring(open)); break; }
            Add(list, true, markup.Substring(open + 1, close - open - 1));
            i = close + 1;
        }
        return list;
    }

    static void Add(List<Seg> l, bool chip, string t)
    {
        t = t.Trim();
        if (t.Length == 0) return;
        l.Add(new Seg { chip = chip, text = t });
    }

    /// <summary>Key-chip markup helper: <c>Key("Q/E", "chip")</c> is <c>[Q/E] chip</c>.</summary>
    public static string Key(string key, string label) => "[" + key + "] " + label;

    // ------------------------------------------------------------------ state

    sealed class Row
    {
        public RectTransform root;
        public readonly List<RectTransform> chips = new List<RectTransform>();
        public readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
        public string shown = "\u0001";
        public float size;
    }

    Canvas canvas;
    CanvasGroup group;
    RectTransform column;
    Row hintRow, infoRow;
    TextMeshProUGUI messageText;
    string hint = "", info = "", say = "", hover = "";
    HintTone sayTone = HintTone.Normal;
    float sayUntil;
    bool wanted;
    float alpha, lift;

    static readonly Color Cream = new Color(0.97f, 0.945f, 0.87f), Gold = new Color(0.96f, 0.86f, 0.51f);
    static readonly Color GoodC = new Color(0.48f, 0.94f, 0.54f), BadC = new Color(1f, 0.48f, 0.44f);

    public bool IsShown => canvas != null && canvas.enabled && alpha > 0.01f;
    public string HintLine => hint;
    public string InfoLine => info;
    public string MessageLine => messageText != null ? messageText.text : "";

    /// <summary>The controller wants the strip (true while seated and playing).</summary>
    public void SetWanted(bool on)
    {
        wanted = on;
        if (!on) { hint = info = say = hover = ""; sayUntil = 0f; }
    }

    public void SetHint(string markup) => hint = markup ?? "";
    public void SetInfo(string markup) => info = markup ?? "";

    /// <summary>A message from the table, for a few seconds (replaces the hover text meanwhile). Empty clears it.</summary>
    public void Say(string text, HintTone tone = HintTone.Normal, float seconds = 3.2f)
    {
        say = text ?? "";
        sayTone = tone;
        sayUntil = string.IsNullOrEmpty(say) ? 0f : (seconds <= 0f ? float.PositiveInfinity : Time.unscaledTime + seconds);
    }

    /// <summary>What the cursor is over (shown while no message is up). Empty clears it.</summary>
    public void SetHover(string text)
    {
        text = text ?? "";
        // pointing at something is a newer answer than a polite remark ("What'll it be?"); errors and results stay up
        if (text.Length > 0 && text != hover && sayTone == HintTone.Normal) sayUntil = 0f;
        hover = text;
    }

    // ------------------------------------------------------------------ reticle (the cursor when the mouse is locked, or the pad's virtual cursor)

    RectTransform reticle;
    UnityEngine.UI.Image reticleDot, reticleRing;
    bool reticleOn;
    Vector2 reticleScreen;
    bool reticleHot;

    /// <summary>Show the cursor mark at a screen position (pixels, bottom-left origin): a dot, a gold ring when it is over something to click.</summary>
    public void SetReticle(bool show, Vector2 screen, bool hot)
    {
        reticleOn = show; reticleScreen = screen; reticleHot = hot;
    }

    void UpdateReticle()
    {
        bool vis = reticleOn && alpha > 0.5f && canvas != null;
        if (reticle == null) { if (!vis) return; BuildReticle(); }
        if (reticle.gameObject.activeSelf != vis) reticle.gameObject.SetActive(vis);
        if (!vis) return;
        var rect = (RectTransform)canvas.transform;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, reticleScreen, null, out var local)) reticle.anchoredPosition = local + rect.rect.size * rect.pivot;
        reticleRing.color = reticleHot ? new Color(1f, 0.84f, 0.3f, 0.95f) : new Color(1f, 1f, 1f, 0.35f);
        reticleDot.color = reticleHot ? new Color(1f, 0.9f, 0.5f, 1f) : new Color(1f, 1f, 1f, 0.9f);
        float s = reticleHot ? 1.25f : 1f;
        reticle.localScale = new Vector3(s, s, 1f);
    }

    void BuildReticle()
    {
        reticle = FeelUi.Rect("Reticle", canvas.transform, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f));
        reticleRing = FeelUi.Img(reticle, "Ring", UiKit.Sprites.RoundedOutline, new Color(1f, 1f, 1f, 0.35f), 0.5f);
        reticleDot = FeelUi.Img(reticle, "Dot", FeelUi.Circle, Color.white);
        reticleDot.rectTransform.anchorMin = reticleDot.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        reticleDot.rectTransform.sizeDelta = new Vector2(7f, 7f);
        reticle.SetAsFirstSibling();
        reticle.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ build

    void Build(Canvas c)
    {
        canvas = c;
        group = c.gameObject.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        var gr = c.GetComponent<GraphicRaycaster>();
        if (gr != null) Destroy(gr); // nothing on it is clickable

        // the soft shadow along the edge: a vertical ramp, no panel
        // (short and light: the chip rack and the lit plates sit just above it at the eye pose and must stay readable)
        var shade = FeelUi.Rect("Shade", c.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(0f, ShadeHeight));
        shade.anchorMin = new Vector2(0f, 0f); shade.anchorMax = new Vector2(1f, 0f);
        shade.offsetMin = shade.offsetMax = Vector2.zero; shade.sizeDelta = new Vector2(0f, ShadeHeight);
        var img = shade.gameObject.AddComponent<RawImage>();
        img.texture = Ramp();
        img.color = new Color(0f, 0f, 0f, 0.42f);
        img.raycastTarget = false;

        column = FeelUi.Rect("Column", c.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, BaseMargin), new Vector2(1700f, 130f));
        hintRow = MakeRow("Hint", 24f, 6f);
        infoRow = MakeRow("Info", 22f, 80f);
        messageText = FeelUi.Text(column, "Message", 28f, Cream, TextAlignmentOptions.Center);
        messageText.rectTransform.anchorMin = messageText.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        messageText.rectTransform.pivot = new Vector2(0.5f, 0f);
        messageText.rectTransform.sizeDelta = new Vector2(1700f, 40f);
        messageText.rectTransform.anchoredPosition = new Vector2(0f, 42f);
        messageText.text = "";
        canvas.enabled = false;
        group.alpha = 0f;
    }

    static Texture2D ramp;
    static Texture2D Ramp()
    {
        if (ramp != null) return ramp;
        const int h = 64;
        ramp = new Texture2D(1, h, TextureFormat.RGBA32, false) { name = "CasinoHintShade", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
        for (int y = 0; y < h; y++)
        {
            float t = y / (float)(h - 1);            // 0 = bottom edge
            float a = Mathf.Pow(1f - t, 1.8f);
            ramp.SetPixel(0, y, new Color(1f, 1f, 1f, a));
        }
        ramp.Apply(false, true);
        return ramp;
    }

    Row MakeRow(string name, float size, float y)
    {
        var rt = FeelUi.Rect(name, column, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, y), new Vector2(1700f, 40f));
        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childAlignment = TextAnchor.MiddleCenter;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        h.spacing = 8f;
        return new Row { root = rt, size = size };
    }

    // ------------------------------------------------------------------ per frame

    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        bool on = wanted && CasinoSeatCamera.IsSeated && !PlayerController.IsUIBlocking;
        alpha = Mathf.MoveTowards(alpha, on ? 1f : 0f, Time.unscaledDeltaTime * 5f);
        if (group != null) group.alpha = alpha;
        bool vis = alpha > 0.001f;
        if (canvas != null && canvas.enabled != vis) canvas.enabled = vis;
        if (!vis) return;
        UpdateReticle();

        Refill(hintRow, GameSettings.ControlHints ? hint : "");
        Refill(infoRow, info);

        string line; Color col;
        if (Time.unscaledTime < sayUntil && !string.IsNullOrEmpty(say))
        {
            line = say;
            col = sayTone == HintTone.Good ? GoodC : sayTone == HintTone.Bad ? BadC : sayTone == HintTone.Gold ? Gold : Cream;
        }
        else { line = hover; col = new Color(0.9f, 0.88f, 0.8f); }
        if (messageText.text != line) messageText.text = line;
        messageText.color = col;

        // the HUD's own bottom dock (prompt, hint chips) sits in the same place: ride above it
        var hl = HudLayout.Layout;
        float want = hl.valid && hl.bottomDock.height > 1f ? Mathf.Max(0f, hl.size.y - hl.bottomDock.yMin) + 4f - BaseMargin : 0f;
        lift = Mathf.MoveTowards(lift, want, Time.unscaledDeltaTime * 600f);
        column.anchoredPosition = new Vector2(0f, BaseMargin + lift);

        // keep the stack tight when a line is empty
        float y = 6f;
        bool hasHint = hintRow.shown.Length > 0, hasMsg = line.Length > 0, hasInfo = infoRow.shown.Length > 0;
        hintRow.root.anchoredPosition = new Vector2(0f, y);
        if (hasHint) y += RowPitch;
        messageText.rectTransform.anchoredPosition = new Vector2(0f, y);
        if (hasMsg) y += RowPitch - 2f;
        infoRow.root.anchoredPosition = new Vector2(0f, y);
    }

    // ------------------------------------------------------------------ rows

    void Refill(Row row, string markup)
    {
        if (row.shown == markup) return;
        row.shown = markup;
        var segs = Parse(markup);
        int chipN = 0, labN = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].chip) FillChip(row, chipN++, segs[i].text, i);
            else FillLabel(row, labN++, segs[i].text, i);
        }
        for (int i = chipN; i < row.chips.Count; i++) row.chips[i].gameObject.SetActive(false);
        for (int i = labN; i < row.labels.Count; i++) row.labels[i].gameObject.SetActive(false);
        // sibling order = segment order
        int ci = 0, li = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            Transform t = segs[i].chip ? row.chips[ci++] : row.labels[li++].transform;
            t.SetSiblingIndex(i);
        }
    }

    void FillLabel(Row row, int n, string text, int order)
    {
        while (row.labels.Count <= n)
        {
            var t = FeelUi.Text(row.root, "Label", row.size, Cream, TextAlignmentOptions.MidlineLeft);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            row.labels.Add(t);
        }
        var l = row.labels[n];
        l.gameObject.SetActive(true);
        l.fontSize = row.size;
        l.text = text;
        var le = l.gameObject.GetComponent<LayoutElement>() ?? l.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 36f;
        le.preferredWidth = Mathf.Ceil(l.GetPreferredValues(text).x) + 4f;
        // a label belongs to the chip before it: pull the chip and label a little closer than the gap between pairs
    }

    void FillChip(Row row, int n, string key, int order)
    {
        while (row.chips.Count <= n) row.chips.Add(MakeChip(row.root));
        var rt = row.chips[n];
        rt.gameObject.SetActive(true);
        int mouse = key == "LMB" ? 0 : key == "RMB" ? 1 : key == "MMB" ? 2 : -1;
        var top = rt.Find("Top").GetComponent<Image>();
        var label = rt.Find("Text").GetComponent<TextMeshProUGUI>();
        var mouseImg = rt.Find("Mouse").GetComponent<Image>();
        Color fill = new Color(0.984f, 0.969f, 0.933f);
        if (key == "A") fill = new Color(0.55f, 0.84f, 0.5f);
        else if (key == "B") fill = new Color(0.93f, 0.45f, 0.4f);
        else if (key == "X") fill = new Color(0.45f, 0.65f, 0.93f);
        else if (key == "Y") fill = new Color(0.95f, 0.82f, 0.35f);
        top.color = fill;
        float w;
        if (mouse >= 0)
        {
            mouseImg.gameObject.SetActive(true);
            mouseImg.sprite = UiKit.Sprites.Mouse(mouse);
            label.gameObject.SetActive(false);
            w = 34f;
        }
        else
        {
            mouseImg.gameObject.SetActive(false);
            label.gameObject.SetActive(true);
            label.text = key;
            label.fontSize = key.Length <= 1 ? 22f : 18f;
            w = Mathf.Max(34f, label.GetPreferredValues(key).x + 18f);
        }
        var le = rt.GetComponent<LayoutElement>();
        le.preferredWidth = w; le.minWidth = w;
    }

    static RectTransform MakeChip(Transform parent)
    {
        var cap = FeelUi.Rect("Chip", parent, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(34f, 32f));
        var le = cap.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 32f;
        var edge = FeelUi.Img(cap, "Edge", FeelUi.Rounded, new Color(0.56f, 0.52f, 0.46f), 0.3f);
        edge.rectTransform.offsetMin = new Vector2(0f, -3f);
        edge.rectTransform.offsetMax = new Vector2(0f, -3f);
        FeelUi.Img(cap, "Top", FeelUi.Rounded, new Color(0.984f, 0.969f, 0.933f), 0.3f);
        var line = FeelUi.Img(cap, "Line", UiKit.Sprites.RoundedOutline, new Color(0.08f, 0.07f, 0.11f, 0.6f), 0.3f);
        line.rectTransform.offsetMin = new Vector2(0f, -3f);
        var m = FeelUi.Img(cap, "Mouse", null, new Color(0.08f, 0.07f, 0.11f));
        UiKit.Place(m.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(18f, 24f));
        var t = UiKit.InkLabel(cap, "Text", "", 22f, new Color(0.08f, 0.07f, 0.11f), TextAlignmentOptions.Center, false);
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return cap;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; ramp = null; }
}

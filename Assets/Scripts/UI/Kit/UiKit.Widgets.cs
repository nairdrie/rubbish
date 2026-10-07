using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The kit's widget components (Docs/UIPass.md §3, §10.2). All motion runs on unscaled time; each widget's resting look is
// set when it's built, so edit-mode previews (no Update) look right.

/// <summary>
/// A sticker button: rounded body with a chunky offset shadow and a stable ±1.5° tilt; hover springs to 1.08× with a 2 Hz × 2°
/// wobble and a squeak; press squashes to 0.92× and pops; focus shows a hazard-stripe tab and a tiny trash-bag cursor;
/// disabled greys out under a strip of tape that says why ("SOON").
/// </summary>
public sealed class UiButton : Button
{
    public UiButtonStyle Style { get; private set; }
    /// <summary>The label text.</summary>
    public TextMeshProUGUI Text { get; private set; }
    public RectTransform Rect => (RectTransform)transform;
    /// <summary>The scaled/rotated visual (layout and raycasts use <see cref="Rect"/>).</summary>
    public RectTransform Visual { get; private set; }
    /// <summary>The tape's reason while disabled, else null.</summary>
    public string DisabledReason { get; private set; }

    Image body, shadow, stripe, cursor;
    RectTransform tape;
    TextMeshProUGUI tapeText;
    Color bodyColor;
    float tilt, scale = 1f, scaleVel, wobble, focusA, raise, raiseTarget;
    bool hover, down, selected, active;
    Action click;

    /// <summary>Tabs: the selected tab is raised and paper-coloured.</summary>
    public bool Active
    {
        get => active;
        set
        {
            active = value;
            ApplyColors();
            raiseTarget = active ? 6f : 0f;
            if (!Application.isPlaying) { raise = raiseTarget; ApplyPose(); }
        }
    }

    internal void Build(string label, UiButtonStyle style, Action onPress)
    {
        Style = style;
        click = onPress;
        transition = Transition.None;
        tilt = style == UiButtonStyle.Tab || style == UiButtonStyle.Quiet ? 0f : UiKit.TiltFor(label);

        Visual = FeelUi.Stretch("Visual", transform);
        bool flat = style == UiButtonStyle.Tab;
        if (!flat)
        {
            shadow = FeelUi.Img(Visual, "Shadow", FeelUi.Rounded, UiTheme.Shadow, style == UiButtonStyle.Quiet ? 0.35f : 0.6f);
            var off = style == UiButtonStyle.Quiet ? new Vector2(2f, -3f) : new Vector2(5f, -7f);
            shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = off;
        }
        bodyColor = UiTheme.StickerColor(style);
        body = FeelUi.Img(Visual, "Body", FeelUi.Rounded, bodyColor, style == UiButtonStyle.Quiet ? 0.35f : style == UiButtonStyle.Tab ? 0.45f : 0.6f);
        body.raycastTarget = true;
        targetGraphic = body;
        if (style == UiButtonStyle.Tab)
        {
            // folder tab: the bottom runs under the sheet, so only rounded top corners show
            body.rectTransform.offsetMin = new Vector2(0f, -24f);
            var edge = FeelUi.Img(Visual, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.45f);
            edge.rectTransform.offsetMin = new Vector2(0f, -24f);
        }
        else if (UiTheme.StickerInk(style))
        {
            var edge = FeelUi.Img(Visual, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.35f),
                                  style == UiButtonStyle.Quiet ? 0.35f : 0.6f);
            edge.raycastTarget = false;
        }

        // focus: a hazard-stripe tab on the left edge + a tiny trash bag that bobs beside the button
        stripe = FeelUi.Img(Visual, "FocusStripe", UiKit.Sprites.Stripes, Color.white);
        stripe.type = Image.Type.Tiled;
        stripe.pixelsPerUnitMultiplier = 2f;
        var srt = stripe.rectTransform;
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(0f, 1f);
        srt.pivot = new Vector2(0f, 0.5f);
        srt.offsetMin = new Vector2(6f, 10f);
        srt.offsetMax = new Vector2(18f, -10f);
        if (style == UiButtonStyle.Tab || style == UiButtonStyle.Quiet) srt.offsetMax = new Vector2(6f, -10f); // tabs: no stripe
        cursor = FeelUi.Img(Visual, "BagCursor", UiKit.Sprites.Bag, UiTheme.Ink);
        UiKit.Place(cursor.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(34f, 34f));
        if (style == UiButtonStyle.Tab || style == UiButtonStyle.Quiet) cursor.enabled = false;

        bool ink = UiTheme.StickerInk(style);
        float size = style switch
        {
            UiButtonStyle.Quiet => 24f,
            UiButtonStyle.Tab => 24f,
            UiButtonStyle.Paper => 28f,
            _ => UiTheme.Button,
        };
        // big stickers in the display font (Fredoka Bold); small keys, tabs and paper buttons in the label weight (SemiBold)
        bool display = size >= 30f;
        Text = ink
            ? UiKit.InkLabel(Visual, "Label", label, size, UiTheme.Ink, TextAlignmentOptions.Center, display)
            : UiKit.OutlineLabel(Visual, "Label", label, size, Color.white, TextAlignmentOptions.Center, display);
        Text.enableAutoSizing = true;
        Text.fontSizeMax = size;
        Text.fontSizeMin = UiTheme.MinText;
        UiKit.Inset(Text.rectTransform, style == UiButtonStyle.Quiet ? 4f : 22f, 2f, style == UiButtonStyle.Quiet ? 4f : 18f, 2f);
        if (style == UiButtonStyle.Tab) Text.characterSpacing = 3f;

        // tape (disabled reason)
        tape = FeelUi.Rect("Tape", Visual, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(10f, 12f), new Vector2(150f, 38f));
        tape.localRotation = Quaternion.Euler(0f, 0f, -9f);
        FeelUi.Img(tape, "Strip", UiKit.Sprites.Tape, UiTheme.Hex(0xF3E7B3, 0.96f));
        tapeText = UiKit.InkLabel(tape, "Reason", UiText.Soon, 22f, UiTheme.StampRed, TextAlignmentOptions.Center, true);
        tapeText.characterSpacing = 4f;
        tape.gameObject.SetActive(false);

        onClick.AddListener(OnClicked);
        focusA = 0f;
        ApplyColors();
        ApplyPose();
    }

    /// <summary>Resize (also updates the LayoutElement so layout groups respect it).</summary>
    public UiButton Size(float w, float h)
    {
        Rect.sizeDelta = new Vector2(w, h);
        var le = GetComponent<LayoutElement>();
        if (le != null) { le.minWidth = le.preferredWidth = w; le.minHeight = le.preferredHeight = h; }
        return this;
    }

    public void SetLabel(string label) { if (Text != null) Text.text = label ?? ""; }

    /// <summary>Disable with a strip of tape that reads <paramref name="reason"/> ("SOON", "Needs internet"). Null re-enables.
    /// <paramref name="showTape"/> false greys it out without the tape (small arrow keys).</summary>
    public void SetDisabled(string reason, bool showTape = true)
    {
        DisabledReason = string.IsNullOrEmpty(reason) ? null : reason;
        interactable = DisabledReason == null;
        if (tape != null)
        {
            tape.gameObject.SetActive(DisabledReason != null && showTape);
            if (DisabledReason != null && showTape)
            {
                tapeText.text = DisabledReason;
                float w = Mathf.Max(110f, tapeText.GetPreferredValues(DisabledReason).x + 50f);
                tape.sizeDelta = new Vector2(Mathf.Min(w, Rect.rect.width > 10f ? Rect.rect.width * 0.6f : w), 38f);
            }
        }
        // the label moves out from under the tape so it stays readable ("CREW CARD [SOON]")
        if (Text != null && Style != UiButtonStyle.Quiet && Style != UiButtonStyle.Tab)
            UiKit.Inset(Text.rectTransform, 22f, 2f, DisabledReason != null && showTape && tape != null ? tape.sizeDelta.x * 0.8f : 18f, 2f);
        ApplyColors();
    }

    void ApplyColors()
    {
        if (body == null) return;
        Color c = bodyColor;
        if (Style == UiButtonStyle.Tab) c = active ? UiTheme.Paper : UiTheme.PaperDark;
        if (!interactable) c = Color.Lerp(c, UiTheme.Disabled, 0.75f);
        body.color = c;
        if (Text != null)
        {
            Color tc = UiTheme.StickerInk(Style) ? UiTheme.Ink : Color.white;
            if (Style == UiButtonStyle.Tab && !active) tc = UiTheme.InkSoft;
            if (!interactable) tc.a = 0.7f;
            Text.color = tc;
        }
        if (shadow != null) shadow.color = UiTheme.WithAlpha(UiTheme.Shadow, interactable ? UiTheme.Shadow.a : 0.25f);
    }

    void OnClicked()
    {
        UiTheme.Play(UiTheme.SfxPress, 0.45f, 1f);
        scaleVel -= 4f;
        try { click?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public override void OnPointerEnter(PointerEventData e)
    {
        base.OnPointerEnter(e);
        if (!IsInteractable()) return;
        hover = true;
        UiTheme.PlayHover();
        scaleVel += 2.5f;
    }

    public override void OnPointerExit(PointerEventData e)
    {
        base.OnPointerExit(e);
        hover = false;
        down = false;
    }

    public override void OnPointerDown(PointerEventData e)
    {
        base.OnPointerDown(e);
        if (IsInteractable() && e.button == PointerEventData.InputButton.Left) down = true;
    }

    public override void OnPointerUp(PointerEventData e)
    {
        base.OnPointerUp(e);
        if (down) scaleVel += 5f; // rebound
        down = false;
    }

    public override void OnSelect(BaseEventData e)
    {
        base.OnSelect(e);
        if (!selected && e is AxisEventData) UiTheme.PlayHover();
        selected = true;
    }

    public override void OnDeselect(BaseEventData e)
    {
        base.OnDeselect(e);
        selected = false;
    }

    public override void OnSubmit(BaseEventData e)
    {
        scale = 0.92f;
        base.OnSubmit(e);
    }

    /// <summary>Preview hook: show the focus look without an EventSystem.</summary>
    public void PreviewFocus(bool on)
    {
        selected = on;
        focusA = on ? 1f : 0f;
        ApplyPose();
    }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        float t = Time.unscaledTime;
        bool live = IsInteractable();
        float target = !live ? 1f : down ? 0.92f : hover ? 1.08f : 1f;
        if (Style == UiButtonStyle.Tab) target = !live ? 1f : down ? 0.96f : hover ? 1.03f : 1f;
        scale = FeelUi.Spring(scale, ref scaleVel, target, dt, 320f, 16f);
        wobble = Mathf.MoveTowards(wobble, hover && live && Style != UiButtonStyle.Tab ? 1f : 0f, dt * 6f);
        focusA = Mathf.MoveTowards(focusA, (hover || selected) && live ? 1f : 0f, dt * 8f);
        raise = Mathf.Lerp(raise, raiseTarget + (hover && Style == UiButtonStyle.Tab ? 3f : 0f), 1f - Mathf.Exp(-14f * dt));
        ApplyPose(t);
    }

    void ApplyPose(float t = 0f)
    {
        if (Visual == null) return;
        Visual.localScale = Vector3.one * Mathf.Clamp(scale, 0.7f, 1.3f);
        float rot = tilt + wobble * 2f * Mathf.Sin(t * Mathf.PI * 2f * 2f);
        Visual.localRotation = Quaternion.Euler(0f, 0f, rot);
        Visual.anchoredPosition = new Vector2(0f, raise);
        if (stripe != null) stripe.color = new Color(1f, 1f, 1f, focusA);
        if (cursor != null && cursor.enabled)
        {
            cursor.color = UiTheme.WithAlpha(UiKit.InkSurface || UiTheme.StickerInk(Style) ? UiTheme.Ink : UiTheme.Ink, focusA);
            cursor.rectTransform.anchoredPosition = new Vector2(-8f - (1f - focusA) * 14f, Mathf.Sin(t * 6f) * 3f);
            cursor.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 3f) * 8f);
        }
    }
}

/// <summary>
/// A form row: label (+ description) on the left, a control slot on the right, a dashed rule underneath. Highlights with a
/// manila wash and the hazard focus stripe while its control is selected (keyboard) or hovered.
/// </summary>
public sealed class UiRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    /// <summary>The control area (right column).</summary>
    public RectTransform Slot { get; private set; }
    public TextMeshProUGUI Label { get; private set; }
    public TextMeshProUGUI Description { get; private set; }
    /// <summary>The control's selectable (keyboard navigation chains through these).</summary>
    public Selectable Control { get; internal set; }

    Image wash, stripe;
    bool hover;
    float a;
    float width;

    /// <summary>The control column's width: <see cref="UiKit.ControlWidth"/>, narrower on narrow lists (≥ 240).</summary>
    public float ControlW { get; private set; } = UiKit.ControlWidth;

    internal void Build(string label, string description)
    {
        var rt = (RectTransform)transform;
        var parent = rt.parent as RectTransform;
        width = parent != null && parent.rect.width > 100f ? parent.rect.width : 900f;
        ControlW = Mathf.Clamp(width * 0.46f, 240f, UiKit.ControlWidth);

        wash = FeelUi.Img(rt, "Wash", FeelUi.Rounded, UiTheme.WithAlpha(UiKit.InkSurface ? UiTheme.PaperDark : Color.white, 0f), 0.3f);
        wash.raycastTarget = true; // hover anywhere on the row
        stripe = FeelUi.Img(rt, "FocusStripe", UiKit.Sprites.Stripes, new Color(1f, 1f, 1f, 0f));
        stripe.type = Image.Type.Tiled;
        stripe.pixelsPerUnitMultiplier = 2f;
        var srt = stripe.rectTransform;
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(0f, 1f);
        srt.pivot = new Vector2(0f, 0.5f);
        srt.offsetMin = new Vector2(0f, 8f);
        srt.offsetMax = new Vector2(10f, -8f);

        var rule = FeelUi.Img(rt, "Rule", UiKit.Sprites.Dash, UiTheme.WithAlpha(UiKit.InkSurface ? UiTheme.Ink : Color.white, 0.16f));
        rule.type = Image.Type.Tiled;
        rule.rectTransform.anchorMin = new Vector2(0f, 0f);
        rule.rectTransform.anchorMax = new Vector2(1f, 0f);
        rule.rectTransform.offsetMin = new Vector2(UiKit.RowPadX, -2f);
        rule.rectTransform.offsetMax = new Vector2(-UiKit.RowPadX, 2f);

        Label = UiKit.Label(rt, "Label", label, UiTheme.Body, TextAlignmentOptions.TopLeft);
        Label.enableAutoSizing = true; // narrow forms (big UI scale, 5:4) shrink long labels a little, never below 20
        Label.fontSizeMax = UiTheme.Body;
        Label.fontSizeMin = 20f;
        Slot = FeelUi.Rect("Slot", rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-UiKit.RowPadX, 0f),
                           new Vector2(ControlW, 56f));
        Slot.anchorMin = new Vector2(1f, 0f);
        Slot.anchorMax = new Vector2(1f, 1f);
        Slot.offsetMin = new Vector2(-UiKit.RowPadX - ControlW, 0f);
        Slot.offsetMax = new Vector2(-UiKit.RowPadX, 0f);
        if (!string.IsNullOrEmpty(description)) Describe(description);
        else Relayout();
    }

    /// <summary>Width of the label column.</summary>
    public float LabelWidth => Mathf.Max(160f, width - ControlW - UiKit.RowPadX * 2f - 28f);

    /// <summary>Add or change the grey one-liner under the label (wraps to a second line if it must).</summary>
    public UiRow Describe(string description)
    {
        if (Description == null)
        {
            Description = UiKit.Label(transform, "Description", "", UiTheme.Small, TextAlignmentOptions.TopLeft, false,
                                      FontStyles.Normal, UiKit.InkSurface ? UiTheme.InkSoft : new Color(0.82f, 0.8f, 0.88f));
            Description.textWrappingMode = TextWrappingModes.Normal;
            Description.lineSpacing = -2f;
        }
        Description.text = description ?? "";
        Relayout();
        return this;
    }

    void Relayout()
    {
        float lw = LabelWidth;
        bool hasDesc = Description != null && !string.IsNullOrEmpty(Description.text);
        float labelH = 34f;
        float descH = hasDesc ? Mathf.Ceil(Description.GetPreferredValues(Description.text, lw, 0f).y) + 2f : 0f;
        float h = hasDesc ? Mathf.Max(UiKit.RowHeightWithDesc, 16f + labelH + descH + 14f) : UiKit.RowHeight;

        var le = GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = h;
        ((RectTransform)transform).sizeDelta = new Vector2(((RectTransform)transform).sizeDelta.x, h);

        var lr = Label.rectTransform;
        lr.anchorMin = lr.anchorMax = new Vector2(0f, 1f);
        lr.pivot = new Vector2(0f, 1f);
        lr.anchoredPosition = new Vector2(UiKit.RowPadX + 14f, hasDesc ? -14f : -(h - labelH) * 0.5f);
        lr.sizeDelta = new Vector2(lw, labelH);
        if (hasDesc)
        {
            var dr = Description.rectTransform;
            dr.anchorMin = dr.anchorMax = new Vector2(0f, 1f);
            dr.pivot = new Vector2(0f, 1f);
            dr.anchoredPosition = new Vector2(UiKit.RowPadX + 14f, -14f - labelH - 2f);
            dr.sizeDelta = new Vector2(lw, descH);
        }
    }

    public void OnPointerEnter(PointerEventData e) => hover = true;
    public void OnPointerExit(PointerEventData e) => hover = false;

    /// <summary>The row's control (or anything inside the row) has keyboard focus.</summary>
    public bool Focused
    {
        get
        {
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            return sel != null && sel.transform.IsChildOf(transform);
        }
    }

    /// <summary>Preview hook: show the focused look without an EventSystem.</summary>
    public void PreviewFocus(bool on) { a = on ? 1f : 0f; Apply(); }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        bool focus = Focused;
        a = Mathf.MoveTowards(a, focus ? 1f : hover ? 0.55f : 0f, dt * 7f);
        Apply(focus ? 1f : 0f);
    }

    void Apply(float stripeA = -1f)
    {
        if (wash == null) return;
        Color w = wash.color;
        w.a = (UiKit.InkSurface ? 0.75f : 0.08f) * a;
        wash.color = w;
        stripe.color = new Color(1f, 1f, 1f, stripeA >= 0f ? Mathf.Min(a, stripeA) : a);
    }
}

/// <summary>A slider whose Left/Right keys step by <see cref="keyStep"/> instead of uGUI's 10% / 1.</summary>
public sealed class UiSliderControl : Slider
{
    public float keyStep = 1f;

    public override void OnMove(AxisEventData e)
    {
        if (!IsActive() || !IsInteractable()) { base.OnMove(e); return; }
        if (e.moveDir == MoveDirection.Left) { value -= keyStep; e.Use(); return; }
        if (e.moveDir == MoveDirection.Right) { value += keyStep; e.Use(); return; }
        base.OnMove(e);
    }
}

/// <summary>A labelled slider: ink track, tangerine fill, a bottle-cap knob and a value readout.</summary>
public sealed class UiSlider : MonoBehaviour
{
    public UiRow Row { get; private set; }
    public UiSliderControl Slider { get; private set; }
    public TextMeshProUGUI ValueText { get; private set; }

    Func<float, string> fmt;
    Action<float> changed;
    float step;
    RectTransform knob;
    float knobScale = 1f, knobVel;
    bool dragging;

    /// <summary>Snap values to multiples of this (0 = none). Also the keyboard step.</summary>
    public float Step
    {
        get => step;
        set
        {
            step = Mathf.Max(0f, value);
            if (Slider != null) Slider.keyStep = step > 0f ? step : (Slider.maxValue - Slider.minValue) / 20f;
        }
    }

    public float Value => Slider != null ? Slider.value : 0f;

    internal void Build(UiRow row, float min, float max, float v, Func<float, string> format, Action<float> onChanged)
    {
        Row = row;
        fmt = format;
        changed = onChanged;
        var slot = (RectTransform)transform;

        float TrackW = Mathf.Max(140f, row.ControlW - 104f - 26f);
        var area = FeelUi.Rect("Slider", slot, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(TrackW, 48f));
        var hit = FeelUi.Img(area, "Hit", null, new Color(0f, 0f, 0f, 0f));
        hit.raycastTarget = true;
        var track = FeelUi.Img(area, "Track", FeelUi.Rounded, UiKit.InkSurface ? UiTheme.WithAlpha(UiTheme.Ink, 0.18f) : UiTheme.WithAlpha(Color.white, 0.2f), 0.25f);
        UiKit.Inset(track.rectTransform, 0f, 16f, 0f, 16f);
        var fillArea = FeelUi.Stretch("FillArea", area);
        UiKit.Inset(fillArea, 0f, 16f, 0f, 16f);
        var fill = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, UiTheme.Tangerine, 0.25f);
        var handleArea = FeelUi.Stretch("HandleArea", area);
        UiKit.Inset(handleArea, 20f, 0f, 20f, 0f);
        knob = FeelUi.Rect("Knob", handleArea, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44f, 44f));
        var knobShadow = FeelUi.Img(knob, "Shadow", FeelUi.Circle, UiTheme.Shadow);
        knobShadow.rectTransform.offsetMin = knobShadow.rectTransform.offsetMax = new Vector2(2f, -3f);
        var cap = FeelUi.Img(knob, "Cap", FeelUi.Circle, UiTheme.Gold);
        cap.raycastTarget = true;
        FeelUi.Img(knob, "Rim", UiKit.Sprites.Ring, UiTheme.Ink);
        var dot = FeelUi.Img(knob, "Dot", FeelUi.Circle, UiTheme.WithAlpha(UiTheme.Ink, 0.75f));
        UiKit.Inset(dot.rectTransform, 17f, 17f, 17f, 17f);

        Slider = area.gameObject.AddComponent<UiSliderControl>();
        Slider.transition = Selectable.Transition.None;
        Slider.targetGraphic = cap;
        Slider.fillRect = fill.rectTransform;
        Slider.handleRect = knob;
        knob.sizeDelta = new Vector2(44f, -4f); // the slider stretches the handle vertically: keep it a 44 px circle
        Slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        Slider.minValue = min;
        Slider.maxValue = max;
        Slider.wholeNumbers = false;
        Slider.SetValueWithoutNotify(v);
        Step = Mathf.Approximately(min, Mathf.Round(min)) && Mathf.Approximately(max, Mathf.Round(max)) && max - min >= 10f ? 1f : 0f;
        Slider.onValueChanged.AddListener(OnValue);
        row.Control = Slider;

        // value readout (a little paper chit)
        var box = FeelUi.Rect("Value", slot, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(104f, 46f));
        FeelUi.Img(box, "Body", FeelUi.Rounded, UiKit.InkSurface ? UiTheme.Hex(0xFFFDF6) : UiTheme.WithAlpha(Color.black, 0.35f), 0.3f);
        FeelUi.Img(box, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiKit.InkSurface ? UiTheme.Ink : Color.white, 0.3f), 0.3f);
        ValueText = UiKit.Label(box, "Text", "", 24f, TextAlignmentOptions.Center);
        ValueText.enableAutoSizing = true;
        ValueText.fontSizeMax = 24f;
        ValueText.fontSizeMin = UiTheme.MinText;
        UiKit.Inset(ValueText.rectTransform, 6f, 0f, 6f, 0f);
        Refresh();
    }

    void OnValue(float v)
    {
        if (step > 0f)
        {
            float snapped = Slider.minValue + Mathf.Round((v - Slider.minValue) / step) * step;
            snapped = Mathf.Clamp(snapped, Slider.minValue, Slider.maxValue);
            if (!Mathf.Approximately(snapped, v)) { Slider.SetValueWithoutNotify(snapped); v = snapped; }
        }
        Refresh();
        knobVel += 3f;
        try { changed?.Invoke(v); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Set without raising the change callback (reloading a tab).</summary>
    public void SetValueWithoutNotify(float v)
    {
        Slider.SetValueWithoutNotify(v);
        Refresh();
    }

    void Refresh()
    {
        if (ValueText != null) ValueText.text = fmt != null ? fmt(Slider.value) : Slider.value.ToString("0");
    }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        var es = EventSystem.current;
        bool sel = es != null && es.currentSelectedGameObject == Slider.gameObject;
        dragging = Input.GetMouseButton(0) && sel;
        knobScale = FeelUi.Spring(knobScale, ref knobVel, dragging ? 1.2f : sel ? 1.1f : 1f, dt, 260f, 14f);
        knob.localScale = Vector3.one * Mathf.Clamp(knobScale, 0.7f, 1.5f);
        knob.localRotation = Quaternion.Euler(0f, 0f, -Slider.normalizedValue * 270f); // the cap spins as it slides
    }
}

/// <summary>An on/off pill: green with the knob right ("ON"), grey with it left ("OFF").</summary>
public sealed class UiToggle : MonoBehaviour
{
    public UiRow Row { get; private set; }
    public Toggle Toggle { get; private set; }
    public bool Value => Toggle != null && Toggle.isOn;

    Image pill;
    RectTransform knob;
    TextMeshProUGUI word;
    Action<bool> changed;
    float pos, vel;

    internal void Build(UiRow row, bool v, Action<bool> onChanged)
    {
        Row = row;
        changed = onChanged;
        var slot = (RectTransform)transform;
        var area = FeelUi.Rect("Pill", slot, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(128f, 50f));
        var shadow = FeelUi.Img(area, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.5f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(3f, -4f);
        pill = FeelUi.Img(area, "Body", FeelUi.Rounded, UiTheme.Go, 0.5f);
        pill.raycastTarget = true;
        FeelUi.Img(area, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.45f), 0.5f);
        word = UiKit.OutlineLabel(area, "Word", UiText.On, 22f, Color.white, TextAlignmentOptions.Center, true);
        knob = FeelUi.Rect("Knob", area, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40f, 40f));
        FeelUi.Img(knob, "Cap", FeelUi.Circle, UiTheme.Paper);
        FeelUi.Img(knob, "Rim", UiKit.Sprites.Ring, UiTheme.WithAlpha(UiTheme.Ink, 0.8f));

        Toggle = area.gameObject.AddComponent<Toggle>();
        Toggle.transition = Selectable.Transition.None;
        Toggle.targetGraphic = pill;
        Toggle.SetIsOnWithoutNotify(v);
        Toggle.onValueChanged.AddListener(OnValue);
        row.Control = Toggle;
        pos = v ? 1f : 0f;
        Apply();
    }

    void OnValue(bool v)
    {
        UiTheme.Play(UiTheme.SfxPress, 0.4f, v ? 1.25f : 0.9f);
        vel += v ? 2f : -2f;
        Apply();
        try { changed?.Invoke(v); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public void SetValueWithoutNotify(bool v)
    {
        Toggle.SetIsOnWithoutNotify(v);
        pos = v ? 1f : 0f;
        Apply();
    }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        pos = FeelUi.Spring(pos, ref vel, Toggle.isOn ? 1f : 0f, dt, 300f, 18f);
        Apply();
    }

    void Apply()
    {
        bool on = Toggle != null && Toggle.isOn;
        pill.color = on ? UiTheme.Go : UiTheme.Hex(0x9A94A3);
        word.text = on ? UiText.On : UiText.Off;
        // the word sits on the side away from the knob
        var wr = word.rectTransform;
        wr.offsetMin = new Vector2(on ? 8f : 48f, 0f);
        wr.offsetMax = new Vector2(on ? -48f : -8f, 0f);
        knob.anchoredPosition = new Vector2(Mathf.LerpUnclamped(26f, 102f, pos), 0f);
        var es = EventSystem.current;
        bool sel = es != null && es.currentSelectedGameObject == Toggle.gameObject;
        knob.localScale = Vector3.one * (sel ? 1.08f : 1f);
    }
}

/// <summary>The cycler's centre: Left/Right/Submit cycle the value; Up/Down navigate.</summary>
public sealed class UiCyclerControl : Selectable, ISubmitHandler, IPointerClickHandler
{
    internal UiCycler owner;

    public override void OnMove(AxisEventData e)
    {
        if (owner != null && IsInteractable())
        {
            if (e.moveDir == MoveDirection.Left) { owner.Step(-1); e.Use(); return; }
            if (e.moveDir == MoveDirection.Right) { owner.Step(1); e.Use(); return; }
        }
        base.OnMove(e);
    }

    public void OnSubmit(BaseEventData e) { if (owner != null && IsInteractable()) owner.Step(1); }

    public void OnPointerClick(PointerEventData e)
    {
        if (owner == null || !IsInteractable()) return;
        owner.Step(e.button == PointerEventData.InputButton.Right ? -1 : 1);
    }
}

/// <summary>◀ value ▶. Wraps around. Left/Right while selected, or the arrow buttons.</summary>
public sealed class UiCycler : MonoBehaviour
{
    public UiRow Row { get; private set; }
    public UiCyclerControl Control { get; private set; }
    public TextMeshProUGUI ValueText { get; private set; }
    public int Index { get; private set; }
    public IReadOnlyList<string> Options => options;

    IReadOnlyList<string> options;
    Action<int> changed;
    RectTransform valueRt;
    float bump, bumpVel;
    UiButton left, right;

    internal void Build(UiRow row, IReadOnlyList<string> opts, int index, Action<int> onChanged)
    {
        Row = row;
        options = opts ?? Array.Empty<string>();
        changed = onChanged;
        var slot = (RectTransform)transform;

        float valueW = Mathf.Clamp(row.ControlW - 52f * 2f - 16f - 60f, 120f, 340f);
        left = UiKit.Button(slot, "<", () => Step(-1), UiButtonStyle.Quiet);
        UiKit.Place(left.Rect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(52f, 52f));
        right = UiKit.Button(slot, ">", () => Step(1), UiButtonStyle.Quiet);
        UiKit.Place(right.Rect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(52f + 8f + valueW + 8f, 0f), new Vector2(52f, 52f));
        Arrow(left, 180f);
        Arrow(right, 0f);
        foreach (var b in new[] { left, right })
        {
            var n = b.navigation;
            n.mode = Navigation.Mode.None;
            b.navigation = n;
        }

        valueRt = FeelUi.Rect("Value", slot, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(60f, 0f), new Vector2(valueW, 50f));
        var bg = FeelUi.Img(valueRt, "Body", FeelUi.Rounded, UiKit.InkSurface ? UiTheme.Hex(0xFFFDF6) : UiTheme.WithAlpha(Color.black, 0.35f), 0.3f);
        bg.raycastTarget = true;
        FeelUi.Img(valueRt, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiKit.InkSurface ? UiTheme.Ink : Color.white, 0.3f), 0.3f);
        ValueText = UiKit.Label(valueRt, "Text", "", 24f, TextAlignmentOptions.Center);
        ValueText.enableAutoSizing = true;
        ValueText.fontSizeMax = 24f;
        ValueText.fontSizeMin = UiTheme.MinText;
        UiKit.Inset(ValueText.rectTransform, 12f, 0f, 12f, 0f);

        Control = valueRt.gameObject.AddComponent<UiCyclerControl>();
        Control.owner = this;
        Control.transition = Selectable.Transition.None;
        Control.targetGraphic = bg;
        row.Control = Control;
        SetIndexWithoutNotify(index);
    }

    static void Arrow(UiButton b, float rot)
    {
        b.SetLabel("");
        var a = FeelUi.Img(b.Visual, "Arrow", UiKit.Sprites.Chevron, UiTheme.Ink);
        UiKit.Place(a.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
        a.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rot);
    }

    /// <summary>Move by <paramref name="d"/> (wraps) and notify.</summary>
    public void Step(int d)
    {
        if (options.Count == 0) return;
        Index = ((Index + d) % options.Count + options.Count) % options.Count;
        ValueText.text = options[Index];
        bumpVel += d > 0 ? 6f : -6f;
        UiTheme.Play(UiTheme.SfxPress, 0.35f, d > 0 ? 1.15f : 0.95f);
        try { changed?.Invoke(Index); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public void SetIndexWithoutNotify(int i)
    {
        Index = options.Count == 0 ? 0 : Mathf.Clamp(i, 0, options.Count - 1);
        ValueText.text = options.Count > 0 ? options[Index] : "";
    }

    /// <summary>Replace the options (e.g. resolutions after a display change).</summary>
    public void SetOptions(IReadOnlyList<string> opts, int index)
    {
        options = opts ?? Array.Empty<string>();
        SetIndexWithoutNotify(index);
    }

    /// <summary>Grey the whole control out with a reason tape on the value (null re-enables).</summary>
    public void SetDisabled(string reason)
    {
        bool on = string.IsNullOrEmpty(reason);
        Control.interactable = on;
        left.SetDisabled(on ? null : reason, false);
        right.SetDisabled(on ? null : reason, false);
        ValueText.color = on ? (UiKit.InkSurface ? UiTheme.Ink : Color.white) : UiTheme.InkSoft;
    }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        bump = FeelUi.Spring(bump, ref bumpVel, 0f, dt, 300f, 16f);
        valueRt.anchoredPosition = new Vector2(60f + bump * 3f, 0f);
    }
}

/// <summary>Folder tabs. The active one is paper-coloured and raised so it joins the sheet below.</summary>
public sealed class UiTabs : MonoBehaviour
{
    public int Index { get; private set; }
    public IReadOnlyList<UiButton> Buttons => buttons;
    readonly List<UiButton> buttons = new List<UiButton>();
    Action<int> changed;

    internal void Build(IReadOnlyList<string> names, Action<int> onChanged)
    {
        changed = onChanged;
        var row = (RectTransform)transform;
        var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        h.childAlignment = TextAnchor.LowerLeft;
        h.spacing = 6f;
        for (int i = 0; i < names.Count; i++)
        {
            int k = i;
            var b = UiKit.Button(row, names[i], () => Select(k, true), UiButtonStyle.Tab);
            float w = Mathf.Max(150f, b.Text.GetPreferredValues(names[i]).x + 64f);
            b.Size(w, 64f);
            buttons.Add(b);
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            var n = buttons[i].navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnLeft = i > 0 ? buttons[i - 1] : null;
            n.selectOnRight = i < buttons.Count - 1 ? buttons[i + 1] : null;
            buttons[i].navigation = n;
        }
        Select(0, false);
    }

    /// <summary>Squeeze the tabs to fit <paramref name="width"/> (narrow folders); labels auto-size down, never below 18.</summary>
    public void FitTo(float width)
    {
        if (buttons.Count == 0) return;
        float total = 0f;
        foreach (var b in buttons) total += b.Rect.sizeDelta.x;
        total += 6f * (buttons.Count - 1);
        if (total <= width) return;
        float k = Mathf.Max(0.4f, (width - 6f * (buttons.Count - 1)) / (total - 6f * (buttons.Count - 1)));
        foreach (var b in buttons)
        {
            b.Size(Mathf.Floor(b.Rect.sizeDelta.x * k), b.Rect.sizeDelta.y);
            b.Text.characterSpacing = 0f;
        }
    }

    /// <summary>Make tab <paramref name="i"/> active (optionally raising the change callback).</summary>
    public void Select(int i, bool notify)
    {
        if (buttons.Count == 0) return;
        i = Mathf.Clamp(i, 0, buttons.Count - 1);
        bool same = i == Index && buttons[i].Active;
        Index = i;
        for (int k = 0; k < buttons.Count; k++) buttons[k].Active = k == i;
        if (!notify || same) return;
        UiTheme.Play(UiTheme.SfxOpen, 0.4f, 1.1f);
        try { changed?.Invoke(i); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Set every tab's Down target (the first row of the page).</summary>
    public void SetDown(Selectable down)
    {
        foreach (var b in buttons)
        {
            var n = b.navigation;
            n.selectOnDown = down;
            b.navigation = n;
        }
    }
}

/// <summary>Enter/leave animation for a panel: OutBack pop-in over 0.25 s, quick shrink-out over 0.15 s (unscaled).</summary>
public sealed class UiPop : MonoBehaviour
{
    CanvasGroup group;
    float t = 1f, dur = 0.25f;
    bool showing = true;
    Action after;

    public static UiPop On(GameObject go)
    {
        var p = go.GetComponent<UiPop>() ?? go.AddComponent<UiPop>();
        p.group = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
        return p;
    }

    /// <summary>Animate in (<paramref name="show"/>) or out; <paramref name="done"/> runs at the end. Instant in edit mode.</summary>
    public void Play(bool show, Action done = null)
    {
        showing = show;
        after = done;
        dur = show ? UiTheme.EnterSeconds : UiTheme.LeaveSeconds;
        t = 0f;
        if (!Application.isPlaying) { t = 1f; Apply(); Finish(); return; }
        Apply();
    }

    void Update()
    {
        if (t >= 1f) return;
        t = Mathf.Min(1f, t + Time.unscaledDeltaTime / Mathf.Max(0.01f, dur));
        Apply();
        if (t >= 1f) Finish();
    }

    void Apply()
    {
        float s = showing ? Mathf.LerpUnclamped(0.85f, 1f, FeelEase.OutBack(t, 2.2f)) : Mathf.Lerp(1f, 0.9f, FeelEase.InQuad(t));
        transform.localScale = Vector3.one * s;
        if (group != null)
        {
            group.alpha = showing ? Mathf.Clamp01(t * 3f) : 1f - t;
            group.interactable = showing;
            group.blocksRaycasts = showing;
        }
    }

    void Finish()
    {
        var a = after;
        after = null;
        try { a?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }
}

/// <summary>A paper confirm card (see <see cref="UiKit.Confirm"/>). One at a time; a new one answers the old one with No.</summary>
public sealed class UiDialog : UiScreen
{
    static UiDialog current;

    Canvas canvas;
    RectTransform card;
    TextMeshProUGUI title, body;
    UiButton yesB, noB;
    Action<bool> done;
    string bodyFormat;
    float revertLeft;
    int shownSeconds = -1;
    bool answered;

    /// <summary>A dialog is up.</summary>
    public static bool IsShowing => current != null && current.IsOpen;

    internal static UiDialog Show(string title, string body, string yes, string no, Action<bool> done, float autoRevertSeconds)
    {
        if (current != null) current.Answer(false);
        var c = UiKit.Canvas("[UiDialog]", UiLayers.Dialog);
        var d = c.gameObject.AddComponent<UiDialog>();
        d.canvas = c;
        d.Build(title, body, yes, no, done, autoRevertSeconds);
        current = d;
        d.Open(true);
        return d;
    }

    /// <summary>Edit-mode preview: built under <paramref name="parent"/> and drawn through <paramref name="cam"/>; never opened
    /// (no stack, no lock).</summary>
    public static UiDialog CreatePreview(Transform parent, Camera cam, int w, int h, string title, string body, string yes, string no,
                                         float autoRevertSeconds = 0f)
    {
        var c = UiKit.Canvas("[UiDialog Preview]", UiLayers.Dialog, parent);
        UiKit.PreviewCanvas(c, cam, w, h);
        var d = c.gameObject.AddComponent<UiDialog>();
        d.canvas = c;
        d.Build(title, body, yes, no, null, autoRevertSeconds);
        return d;
    }

    void Build(string t, string b, string yes, string no, Action<bool> cb, float revert)
    {
        done = cb;
        bodyFormat = b ?? "";
        revertLeft = revert;
        var root = (RectTransform)transform;
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.55f));
        dim.raycastTarget = true;

        card = UiKit.Paper(root, new Vector2(860f, 400f));
        card.localRotation = Quaternion.Euler(0f, 0f, -0.8f);
        var clip = FeelUi.Img(card, "Clip", UiKit.Sprites.Paperclip, UiTheme.Hex(0xA7AFB8));
        UiKit.Place(clip.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(70f, 6f), new Vector2(46f, 92f));
        clip.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 8f);

        title = UiKit.InkLabel(card, "Title", t, 46f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        title.enableAutoSizing = true;
        title.fontSizeMax = 46f;
        title.fontSizeMin = 28f;
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(760f, 64f));
        var rule = FeelUi.Img(card, "Rule", FeelUi.Rounded, UiTheme.StampRed, 0.1f);
        UiKit.Place(rule.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -106f), new Vector2(220f, 6f));

        body = UiKit.InkLabel(card, "Body", "", 28f, UiTheme.InkSoft, TextAlignmentOptions.Top, false, FontStyles.Normal);
        body.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(body.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -130f), new Vector2(740f, 120f));

        bool hasNo = !string.IsNullOrEmpty(no);
        yesB = UiKit.Button(card, string.IsNullOrEmpty(yes) ? UiText.Yes : yes, () => Answer(true), UiButtonStyle.Primary);
        yesB.Size(330f, 76f);
        UiKit.Place(yesB.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(hasNo ? 185f : 0f, 34f), new Vector2(330f, 76f));
        if (hasNo)
        {
            noB = UiKit.Button(card, no, () => Answer(false), UiButtonStyle.Paper);
            noB.Size(330f, 76f);
            UiKit.Place(noB.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-185f, 34f), new Vector2(330f, 76f));
            var ny = noB.navigation; ny.mode = Navigation.Mode.Explicit; ny.selectOnRight = yesB; noB.navigation = ny;
            var yn = yesB.navigation; yn.mode = Navigation.Mode.Explicit; yn.selectOnLeft = noB; yesB.navigation = yn;
        }
        Tick();
        // fit the card to the body (one-liners don't leave a big empty gap)
        float bodyH = Mathf.Clamp(body.GetPreferredValues(body.text, 740f, 0f).y + 8f, 40f, 220f);
        body.rectTransform.sizeDelta = new Vector2(740f, bodyH);
        card.sizeDelta = new Vector2(860f, 130f + bodyH + 140f);
        UiPop.On(card.gameObject).Play(true);
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.9f);
        UiKit.Focus((revertLeft > 0f && noB != null ? noB : yesB).gameObject);
    }

    public override bool OnBack()
    {
        Answer(false);
        return true;
    }

    void Answer(bool yes)
    {
        if (answered) return;
        answered = true;
        if (current == this) current = null;
        UiTheme.Play(yes ? UiTheme.SfxConfirm : UiTheme.SfxBack, 0.45f, 1f);
        Close();
        var cb = done;
        done = null;
        if (canvas != null) UiPop.On(card.gameObject).Play(false, () => { if (this != null) Destroy(gameObject); });
        try { cb?.Invoke(yes); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Tick()
    {
        if (revertLeft > 0f)
        {
            int s = Mathf.CeilToInt(revertLeft);
            if (s != shownSeconds) { shownSeconds = s; body.text = string.Format(bodyFormat.Contains("{0}") ? bodyFormat : bodyFormat + " {0}", s); }
        }
        else if (shownSeconds == -1) { shownSeconds = 0; body.text = bodyFormat.Replace("{0}", ""); }
    }

    void Update()
    {
        if (answered) return;
        if (revertLeft > 0f)
        {
            revertLeft -= Time.unscaledDeltaTime;
            if (revertLeft <= 0f) { Answer(false); return; }
        }
        Tick();
    }

    protected override void OnDestroy()
    {
        if (current == this) current = null;
        base.OnDestroy();
    }
}

/// <summary>Keeps a <see cref="UiKit.ScrollList"/>'s scrollbar and "more below" fade honest (also callable from previews).</summary>
public sealed class UiScrollHints : MonoBehaviour
{
    ScrollRect sr;
    Image fade;

    internal void Init(ScrollRect s, Image f) { sr = s; fade = f; Refresh(); }

    void LateUpdate() => Refresh();

    /// <summary>Update the scrollbar size/visibility and the fade now (edit-mode previews call this; play mode does it every frame).</summary>
    public void Refresh()
    {
        if (sr == null || sr.content == null || sr.viewport == null) return;
        float contentH = sr.content.rect.height, viewH = sr.viewport.rect.height;
        bool scrolls = contentH > viewH + 1f;
        if (!Application.isPlaying)
        {
            sr.Rebuild(CanvasUpdate.PostLayout);
            if (sr.verticalScrollbar != null)
            {
                sr.verticalScrollbar.gameObject.SetActive(scrolls);
                sr.verticalScrollbar.size = scrolls ? Mathf.Clamp01(viewH / contentH) : 1f;
                sr.verticalScrollbar.SetValueWithoutNotify(sr.verticalNormalizedPosition);
            }
        }
        if (fade != null)
        {
            float below = scrolls ? Mathf.Clamp01(sr.verticalNormalizedPosition * (contentH - viewH) / 40f) : 0f;
            var c = fade.color;
            c.a = below;
            fade.color = c;
        }
    }
}

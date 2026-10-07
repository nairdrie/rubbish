using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The interaction prompt (Docs/UIPass.md §8.1 H1): [key cap][text] on a plum panel at the bottom dock, auto-sized. The cap fits
/// "E", "LMB" (a mouse icon), "E/LMB", "Space" and "Hold R". The text is at most 640 px wide and at most 2 lines (the 2nd
/// ends in "…"), so "Grab Saggy Trash Bag (1.2 kg)" never spills out of its box any more. An optional alt row ([F] option)
/// sits under it at 22 px. Replaces TooltipUI's fixed scene box: <see cref="TooltipUI"/> keeps its API and forwards here
/// through <see cref="Show"/>, <see cref="Alt"/> and <see cref="Hide"/> (no call site changes).
/// <para>State is "last write wins this frame": PlayerController calls Show then AppendAltHint every frame, so the view only
/// rebuilds when the wanted text actually changes.</para>
/// </summary>
public sealed class FeelHudPrompt
{
    public const float MaxTextWidth = 640f, TextSize = 30f, AltSize = 22f, CapH = 44f, AltCapH = 32f;
    const float PadX = 16f, PadY = 9f, CapGap = 14f, AltGap = 6f;

    // ---- static façade (TooltipUI and the sort station write here; the live HUD reads the final state each LateUpdate,
    //      so PlayerController's per-frame Show + AppendAltHint costs nothing while the prompt stays the same)
    static string wantKey, wantText, wantAltKey, wantAltText;

    /// <summary>Show the prompt "[key] text" (clears the alt row; call <see cref="Alt"/> after it for one).</summary>
    public static void Show(string key, string text)
    {
        wantKey = key; wantText = text; wantAltKey = null; wantAltText = null;
    }

    /// <summary>The alt row under the prompt ("[F] Other option").</summary>
    public static void Alt(string key, string text)
    {
        wantAltKey = key; wantAltText = text;
    }

    public static void Hide()
    {
        wantKey = wantText = wantAltKey = wantAltText = null;
    }

    /// <summary>The prompt the HUD should be showing (tests, de-duplication with the hint chips).</summary>
    public static string WantedKey => wantKey;
    public static string WantedText => wantText;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        wantKey = wantText = wantAltKey = wantAltText = null;
    }

    // ---- view
    RectTransform panel, inner, altRow;
    CanvasGroup group;
    TextMeshProUGUI text, altText;
    FeelKeyCap cap, altCap;
    string key, msg, aKey, aMsg;
    bool direct;
    float maxWidth = MaxTextWidth, builtFor = -1f, alpha, scale = 1f, scaleVel;
    Vector2 size;

    public RectTransform Panel => panel;
    /// <summary>The panel's size this frame (0 when hidden).</summary>
    public Vector2 Size => alpha > 0.001f || HasPrompt ? size : Vector2.zero;
    public bool HasPrompt => !string.IsNullOrEmpty(msg);
    public float Alpha => alpha;
    /// <summary>The text shown (after fitting) and the key: for the H1 acceptance checks.</summary>
    public TextMeshProUGUI TextLabel => text;
    public RectTransform CapRect => cap != null ? cap.Root : null;
    public RectTransform TextRect => text != null ? text.rectTransform : null;
    public string ShownKey => key;

    public void Build(RectTransform parent)
    {
        panel = FeelUi.Rect("Prompt", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(200f, 60f));
        group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        inner = FeelUi.Stretch("Inner", panel);
        FeelUi.Panel(inner, FeelUi.PanelColor, 0.7f, new Vector2(4f, -5f));
        cap = new FeelKeyCap(inner, CapH);
        text = FeelUi.Text(inner, "Text", TextSize, Color.white, TextAlignmentOptions.MidlineLeft);
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0f, 1f);
        text.rectTransform.pivot = new Vector2(0f, 1f);
        text.textWrappingMode = TextWrappingModes.Normal;
        text.lineSpacing = -8f;

        altRow = FeelUi.Rect("Alt", inner, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(200f, AltCapH));
        altCap = new FeelKeyCap(altRow, AltCapH);
        altText = FeelUi.Text(altRow, "Text", AltSize, new Color(0.86f, 0.86f, 0.95f), TextAlignmentOptions.MidlineLeft);
        altText.rectTransform.anchorMin = altText.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        altText.rectTransform.pivot = new Vector2(0f, 0.5f);
        altRow.gameObject.SetActive(false);
    }

    /// <summary>Pull the wanted state (live) and rebuild the layout if it or the width limit changed.</summary>
    public void Sync(float maxPanelWidth)
    {
        float mw = Mathf.Min(MaxTextWidth, Mathf.Max(160f, maxPanelWidth - (PadX * 2f + CapH + CapGap)));
        if (direct) return;
        if (wantKey == key && wantText == msg && wantAltKey == aKey && wantAltText == aMsg && Mathf.Abs(mw - builtFor) < 0.5f) return;
        Apply(wantKey, wantText, wantAltKey, wantAltText, mw);
    }

    /// <summary>Show exactly this (previews and tests), bypassing the static state.</summary>
    public void SetDirect(string k, string t, string ak, string at, float maxPanelWidth)
    {
        float mw = Mathf.Min(MaxTextWidth, Mathf.Max(160f, maxPanelWidth - (PadX * 2f + CapH + CapGap)));
        direct = true;
        Apply(k, t, ak, at, mw);
        alpha = HasPrompt ? 1f : 0f;
        if (group != null) group.alpha = alpha;
    }

    void Apply(string k, string t, string ak, string at, float mw)
    {
        bool changed = k != key || t != msg;
        key = k; msg = t; aKey = ak; aMsg = at;
        builtFor = mw;
        maxWidth = mw;
        if (string.IsNullOrEmpty(msg)) return; // fade out with the old layout
        if (changed) scaleVel += 5f;

        float capW = cap.Set(string.IsNullOrEmpty(key) ? "E" : key);
        cap.Root.gameObject.SetActive(!string.IsNullOrEmpty(key));
        if (string.IsNullOrEmpty(key)) capW = -CapGap;

        string fitted = FeelUi.FitLines(text, msg, maxWidth, 2);
        text.text = fitted;
        float oneLine = text.GetPreferredValues(fitted, 100000f, 0f).x;
        float tw = Mathf.Min(maxWidth, oneLine + 2f);
        int lines = FeelUi.LineCount(text, fitted, tw);
        float th = text.GetPreferredValues(fitted, tw, 0f).y;
        float rowH = Mathf.Max(CapH, th);

        bool alt = !string.IsNullOrEmpty(aMsg);
        float altW = 0f;
        altRow.gameObject.SetActive(alt);
        if (alt)
        {
            float acw = altCap.Set(string.IsNullOrEmpty(aKey) ? "F" : aKey);
            float maxAlt = Mathf.Max(80f, capW + CapGap + maxWidth - acw - 10f);
            string af = FeelUi.Fit(altText, aMsg, maxAlt);
            altText.text = af;
            float aw = Mathf.Min(maxAlt, altText.GetPreferredValues(af, 100000f, 0f).x + 2f);
            altText.rectTransform.anchoredPosition = new Vector2(acw + 10f, 0f);
            altText.rectTransform.sizeDelta = new Vector2(aw, AltCapH);
            altW = acw + 10f + aw;
        }

        float contentW = Mathf.Max(capW + CapGap + tw, altW);
        float h = PadY + rowH + (alt ? AltGap + AltCapH : 0f) + PadY;
        size = new Vector2(PadX * 2f + contentW, h);
        panel.sizeDelta = size;

        // the cap sits on the first line's centre (two-line prompts keep the key next to the start of the sentence)
        float firstLineCentre = lines >= 2 ? PadY + Mathf.Min(CapH, th) * 0.5f : PadY + rowH * 0.5f;
        cap.Root.anchorMin = cap.Root.anchorMax = new Vector2(0f, 1f);
        cap.Root.pivot = new Vector2(0f, 0.5f);
        cap.Root.anchoredPosition = new Vector2(PadX, -(lines >= 2 ? PadY + CapH * 0.5f : firstLineCentre));
        text.rectTransform.anchoredPosition = new Vector2(PadX + capW + CapGap, -(PadY + (rowH - th) * 0.5f));
        text.rectTransform.sizeDelta = new Vector2(tw, th);
        altRow.anchoredPosition = new Vector2(PadX, PadY);
        altRow.sizeDelta = new Vector2(contentW, AltCapH);
    }

    /// <summary>Place the panel (top-down position of its top-left corner) and animate it.</summary>
    public void Tick(Vector2 topLeft, bool visible, float dt)
    {
        if (panel == null) return;
        HudLayout.PlaceAt(panel, topLeft);
        panel.sizeDelta = size;
        bool on = visible && HasPrompt;
        alpha = Mathf.MoveTowards(alpha, on ? 1f : 0f, dt * 8f);
        group.alpha = alpha;
        scale = FeelUi.Spring(scale, ref scaleVel, 1f, dt, 260f, 14f);
        inner.localScale = Vector3.one * Mathf.Clamp(scale, 0.8f, 1.25f);
    }

    /// <summary>The panel's top-down rect inside the HUD root (for the overlap audit).</summary>
    public Rect TopDownRect => HudLayout.TopDownRect(panel);
}

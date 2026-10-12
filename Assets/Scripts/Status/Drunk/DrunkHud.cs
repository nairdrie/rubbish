using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drunk status widget (Docs/Casino.md §5): the stage ("TIPSY" / "DRUNK" / "WASTED"), a meter of the level with a notch
/// where Wasted starts, and roughly how long until sober. Lives in FeelHud's left column under the rabies widget (same
/// holder, so it stays up over the laptop), built from FeelUi like every HUD panel (plum, 18 px floor, UIPass §8), and
/// placed through HudLayout. Reads <see cref="DrunkStatus"/> itself; hidden while sober.
/// </summary>
public sealed class DrunkHud
{
    public const float Width = 390f, Height = 70f;
    static readonly Color Amber = new Color(1f, 0.8f, 0.35f);

    RectTransform panel, inner, barFillRt;
    CanvasGroup group;
    Image barFill;
    TextMeshProUGUI title, info;
    float alpha, fill, fillVel, scale = 1f, scaleVel, width = Width;
    DrunkStage lastStage;
    string lastTitle, lastInfo;
    int lastInfoKey = int.MinValue;
    bool init;

    public RectTransform Panel => panel;
    /// <summary>0..1: how much of its slot the widget currently takes (fades in and out).</summary>
    public float Presence => alpha;

    public void Build(RectTransform parent)
    {
        panel = FeelUi.Rect("Drunk", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -380f), new Vector2(Width, Height));
        group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        inner = FeelUi.Stretch("Inner", panel);
        FeelUi.Panel(inner, FeelUi.PanelColor, 0.8f);

        title = FeelUi.Text(inner, "Title", 26f, Amber, TextAlignmentOptions.Left);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -6f), new Vector2(150f, 32f));
        info = FeelUi.Text(inner, "Info", 18f, Color.white, TextAlignmentOptions.Right, FontStyles.Normal);
        Place(info.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -10f), new Vector2(210f, 26f));

        var barBg = FeelUi.Rect("Bar", inner, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -44f), new Vector2(Width - 28f, 14f));
        barBg.anchorMin = new Vector2(0f, 1f);
        barBg.anchorMax = new Vector2(1f, 1f);
        barBg.pivot = new Vector2(0f, 1f);
        barBg.offsetMin = new Vector2(14f, -58f);
        barBg.offsetMax = new Vector2(-14f, -44f);
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.3f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(2f, 2f);
        fillArea.offsetMax = new Vector2(-2f, -2f);
        barFill = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, Amber, 0.2f);
        barFillRt = barFill.rectTransform;
        barFillRt.anchorMax = new Vector2(0f, 1f);
        // the notch where Wasted (and the risk of passing out) starts
        var notch = FeelUi.Rect("Notch", fillArea, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        float at = DrunkRules.WastedAt / DrunkRules.MaxLevel;
        notch.anchorMin = new Vector2(at, 0f);
        notch.anchorMax = new Vector2(at, 1f);
        notch.sizeDelta = new Vector2(3f, 0f);
        var notchImg = notch.gameObject.AddComponent<Image>();
        notchImg.color = new Color(1f, 1f, 1f, 0.8f);
        notchImg.raycastTarget = false;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>Top-left corner (top-down reference units).</summary>
    public void SetTopLeft(Vector2 p) { if (panel != null) HudLayout.PlaceAt(panel, p); }

    public void SetWidth(float w)
    {
        if (panel == null || Mathf.Approximately(w, width)) return;
        width = w;
        panel.sizeDelta = new Vector2(width, Height);
        lastInfo = null;
    }

    public void Tick(float dt, float time)
    {
        if (panel == null) return;
        float level = DrunkStatus.Level;
        var stage = DrunkRules.Stage(level);
        bool passedOut = DrunkStatus.PassingOut;
        bool show = stage != DrunkStage.Sober || passedOut;

        if (init && show && stage > lastStage) scaleVel += 9f; // a drink took you up a stage: the panel pops
        if (show) lastStage = stage;
        else lastStage = DrunkStage.Sober;
        init = true;

        alpha = Mathf.MoveTowards(alpha, show ? 1f : 0f, dt * (show ? 5f : 2.5f));
        group.alpha = alpha;
        if (alpha <= 0f || !show) return; // fading out: keep the last text

        string t = passedOut ? "OUT COLD" : stage == DrunkStage.Wasted ? "WASTED" : stage == DrunkStage.Drunk ? "DRUNK" : "TIPSY";
        if (t != lastTitle) { lastTitle = t; title.text = t; }
        bool wasted = stage == DrunkStage.Wasted;
        title.color = wasted ? Color.Lerp(Amber, FeelUi.BadRed, 0.5f + 0.5f * Mathf.Sin(time * 6f)) : Amber;
        if (stage >= DrunkStage.Drunk) FeelUi.Wave(title, time, wasted ? 4f : 2f, false, wasted ? 6f : 4f);

        float realMin = level / DrunkRules.DecayPerGameHour * GameClock.SecondsPerGameHour / 60f;
        // -1 / -2 / 0 / minutes: the line is only rebuilt when what it says changes (no string per frame)
        int infKey = passedOut ? -1 : wasted ? -2 : realMin >= 1f ? Mathf.CeilToInt(realMin) : 0;
        if (infKey != lastInfoKey || lastInfo == null)
        {
            lastInfoKey = infKey;
            lastInfo = passedOut ? "zzz..." : wasted ? "about to pass out!" : realMin >= 1f ? $"sober in ~{infKey} min" : "nearly sober";
            info.text = FeelUi.Fit(info, lastInfo, Mathf.Max(60f, width - 190f));
        }
        info.color = wasted ? FeelUi.BadRed : Color.white;

        fill = FeelUi.Spring(fill, ref fillVel, Mathf.Clamp01(level / DrunkRules.MaxLevel), dt, 90f, 12f);
        float f = Mathf.Clamp01(fill);
        barFillRt.anchorMax = new Vector2(f, 1f);
        barFill.gameObject.SetActive(f > 0.01f);
        barFill.color = Color.Lerp(Amber, FeelUi.BadRed, Mathf.InverseLerp(DrunkRules.DrunkAt, DrunkRules.MaxLevel, level));

        scale = FeelUi.Spring(scale, ref scaleVel, 1f, dt, 260f, 13f);
        inner.localScale = Vector3.one * Mathf.Clamp(scale, 0.6f, 1.6f);
        // the panel itself is a little unsteady
        float lean = DrunkRules.Walk01(level) * 2.5f * Mathf.Sin(time * 1.3f) * GameSettings.ScreenFx01;
        inner.localRotation = Quaternion.Euler(0f, 0f, lean);
    }
}

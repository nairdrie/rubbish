using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Rabies status widget (left column, on the root canvas so it stays up while browsing the shop laptop).
/// Incubating: "RABIES — incubating", "Symptoms at 8:12 AM". Symptomatic: wavy green title, flashing
/// "SYMPTOMS!" chip, foam bubbles, a bar filling toward passing out and "Pass out at 7:42 PM (~10 min)".
/// Always: "Buy a Rabies Shot on Scrapazon!". Cured: a happy "CURED!" flash, then it bows out.
/// Timings come from GameSession.RabiesInfectedAtHours + Economy.RabiesIncubationHours / RabiesPassOutHours.
/// </summary>
public sealed class FeelHudRabies
{
    /// <summary>Shop app name shown in the hint (the Shop system's single const, so a rename follows automatically).</summary>
    public const string ShopName = Scrapazon.Name;
    public const float Width = 390f, Height = 124f;
    const float CuredShowSeconds = 2.2f;

    RectTransform panel, inner, barFillRt, chipRt;
    CanvasGroup group;
    Image body, barFill, chip;
    Image[] bubbles;
    float[] bubblePhase;
    TextMeshProUGUI title, chipText, info, hint;

    float alpha, scale = 1f, scaleVel, shake, curedTimer, fill, fillVel, width = Width;
    bool wasInfected, wasSymptomatic, init;
    string lastInfo, lastChip, lastTitle;

    public RectTransform Panel => panel;
    public float Presence => alpha;

    public void Build(RectTransform parent)
    {
        panel = FeelUi.Rect("Rabies", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -240f), new Vector2(Width, Height));
        group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        inner = FeelUi.Stretch("Inner", panel);
        body = FeelUi.Panel(inner, new Color(0.2f, 0.36f, 0.1f, 0.94f), 0.8f);

        bubbles = new Image[7];
        bubblePhase = new float[bubbles.Length];
        for (int i = 0; i < bubbles.Length; i++)
        {
            var b = FeelUi.Rect("Bubble" + i, inner, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.one * (8f + (i % 3) * 5f));
            bubbles[i] = b.gameObject.AddComponent<Image>();
            bubbles[i].sprite = FeelUi.Circle;
            bubbles[i].raycastTarget = false;
            bubblePhase[i] = i / (float)bubbles.Length;
        }

        title = FeelUi.Text(inner, "Title", 30f, new Color(0.7f, 1f, 0.35f), TextAlignmentOptions.Left);
        Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -6f), new Vector2(200f, 36f));

        chipRt = FeelUi.Rect("Chip", inner, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -10f), new Vector2(150f, 28f));
        chip = FeelUi.Img(chipRt, "Body", FeelUi.Rounded, new Color(1f, 0.8f, 0.2f), 0.5f);
        chipText = FeelUi.Text(chipRt, "Text", 18f, new Color(0.12f, 0.09f, 0.18f), TextAlignmentOptions.Center);
        // plain dark text on the bright chip (no chunky outline)
        var chipInk = UiTheme.InkFor(chipText.font); // the material must match the text's font atlas
        if (chipInk != null) chipText.fontSharedMaterial = chipInk;

        var barBg = FeelUi.Rect("Bar", inner, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -46f), new Vector2(Width - 28f, 14f));
        StretchX(barBg, 14f, 14f);
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.3f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(2f, 2f);
        fillArea.offsetMax = new Vector2(-2f, -2f);
        barFill = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, new Color(0.7f, 1f, 0.35f), 0.2f);
        barFillRt = barFill.rectTransform;
        barFillRt.anchorMax = new Vector2(0f, 1f);

        info = FeelUi.Text(inner, "Info", 21f, Color.white, TextAlignmentOptions.Left, FontStyles.Normal);
        Place(info.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -64f), new Vector2(Width - 32f, 26f));
        StretchX(info.rectTransform, 16f, 16f);
        hint = FeelUi.Text(inner, "Hint", 21f, FeelUi.Gold, TextAlignmentOptions.Left);
        Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -92f), new Vector2(Width - 32f, 26f));
        StretchX(hint.rectTransform, 16f, 16f);
        hint.text = $"Buy a Rabies Shot on {ShopName}!";
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    static void StretchX(RectTransform rt, float left, float right)
    {
        float y = rt.anchoredPosition.y, h = rt.sizeDelta.y;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(left, y - h);
        rt.offsetMax = new Vector2(-right, y);
    }

    /// <summary>Shrink from <paramref name="max"/> toward the 18 px floor to fit one line, then trim with "…".</summary>
    static string FitShrink(TextMeshProUGUI t, string s, float w, float max)
    {
        for (float size = max; size >= 18f; size -= 1f)
        {
            t.fontSize = size;
            if (t.GetPreferredValues(s, 100000f, 0f).x <= w) return s;
        }
        t.fontSize = 18f;
        return FeelUi.Fit(t, s, w);
    }

    /// <summary>Top-left corner (top-down reference units): the left column, or bottom-left in Station mode.</summary>
    public void SetTopLeft(Vector2 p) => HudLayout.PlaceAt(panel, p);

    public void SetWidth(float w)
    {
        if (Mathf.Approximately(w, width)) return;
        width = w;
        panel.sizeDelta = new Vector2(width, Height);
        lastInfo = null; // refit the lines to the new width
        hint.text = FitShrink(hint, $"Buy a Rabies Shot on {ShopName}!", width - 32f, 21f);
    }

    /// <param name="infected">GameSession.HasRabies</param>
    /// <param name="ageHours">in-game hours since infection</param>
    /// <param name="hoursLeft">in-game hours until passing out</param>
    /// <param name="passOutHour">hour of day (0..24) of passing out</param>
    public void Tick(bool infected, float ageHours, float hoursLeft, float passOutHour, float dt, float time)
    {
        if (panel == null) return;
        bool symptomatic = infected && ageHours >= Economy.RabiesIncubationHours;

        if (init)
        {
            if (infected && !wasInfected) { scaleVel += 12f; shake = 1f; Sfx.Play("pop", null, 0.6f, 0.8f); }
            if (symptomatic && !wasSymptomatic && wasInfected) { scaleVel += 8f; shake = 1f; Sfx.Play("hiss", null, 0.35f, 1.1f); }
            if (!infected && wasInfected) { curedTimer = CuredShowSeconds; scaleVel += 14f; Sfx.Play("sparkle", null, 0.6f, 1.1f); }
        }
        init = true;
        wasInfected = infected;
        wasSymptomatic = symptomatic;
        if (curedTimer > 0f) curedTimer -= dt;
        bool cured = !infected && curedTimer > 0f;

        bool show = infected || cured;
        alpha = Mathf.MoveTowards(alpha, show ? 1f : 0f, dt * (show ? 5f : 2.5f));
        group.alpha = alpha;
        if (alpha <= 0f) return;
        // Fading out after the CURED! card: keep showing it. (Re-deriving the text here would flash
        // "RABIES / incubating…" for the length of the fade, since we're neither infected nor "cured" any more.)
        if (!show) return;

        string t, ch, inf;
        Color chipC;
        float target;
        if (cured)
        {
            t = "CURED!"; ch = "all better"; chipC = FeelUi.GoodGreen; target = 0f;
            inf = "Phew. No more foaming at the mouth.";
            body.color = new Color(0.16f, 0.55f, 0.28f, 0.95f);
        }
        else if (!symptomatic)
        {
            t = "RABIES"; ch = "incubating…"; chipC = new Color(1f, 0.85f, 0.25f);
            target = Mathf.Clamp01(ageHours / Economy.RabiesPassOutHours);
            float symptomsAt = Mathf.Repeat(passOutHour - (Economy.RabiesPassOutHours - Economy.RabiesIncubationHours), 24f);
            inf = $"Feeling… fine? Symptoms at {GameClock.FormatHours(symptomsAt)}";
            body.color = new Color(0.2f, 0.36f, 0.1f, 0.94f);
        }
        else
        {
            bool urgent = hoursLeft < 3f;
            t = "RABIES!"; ch = urgent ? "PASSING OUT!" : "SYMPTOMS!";
            chipC = Mathf.Sin(time * (urgent ? 14f : 7f)) > 0f ? new Color(1f, 0.35f, 0.3f) : new Color(1f, 0.85f, 0.25f);
            target = Mathf.Clamp01(ageHours / Economy.RabiesPassOutHours);
            float realMin = hoursLeft * GameClock.SecondsPerGameHour / 60f;
            string when = realMin >= 1f ? $"~{Mathf.CeilToInt(realMin)} min" : "any second";
            inf = $"Pass out at {GameClock.FormatHours(passOutHour)} ({when})";
            body.color = Color.Lerp(new Color(0.2f, 0.36f, 0.1f, 0.94f), new Color(0.35f, 0.42f, 0.08f, 0.94f), 0.5f + 0.5f * Mathf.Sin(time * 3f));
            if (urgent) shake = Mathf.Max(shake, 0.25f);
        }
        if (t != lastTitle) { lastTitle = t; title.text = t; }
        if (ch != lastChip) { lastChip = ch; chipText.text = ch; }
        if (inf != lastInfo) { lastInfo = inf; info.text = FitShrink(info, inf, width - 32f, 21f); }
        chip.color = chipC;
        title.color = cured ? Color.white : new Color(0.7f, 1f, 0.35f);
        if (symptomatic) FeelUi.Wave(title, time, 3.5f, false, 6f);
        hint.gameObject.SetActive(!cured);
        hint.alpha = 0.65f + 0.35f * Mathf.Sin(time * 4f);

        fill = FeelUi.Spring(fill, ref fillVel, cured ? 1f : target, dt, 90f, 12f);
        float f = Mathf.Clamp01(fill);
        barFillRt.anchorMax = new Vector2(f, 1f);
        barFill.gameObject.SetActive(f > 0.01f);
        barFill.color = cured ? FeelUi.GoodGreen : Color.Lerp(new Color(0.7f, 1f, 0.35f), new Color(1f, 0.3f, 0.25f), f);

        // foam bubbles drifting up out of the panel while symptomatic
        for (int i = 0; i < bubbles.Length; i++)
        {
            var b = bubbles[i];
            if (!symptomatic) { b.color = Color.clear; continue; }
            bubblePhase[i] = Mathf.Repeat(bubblePhase[i] + dt * (0.35f + 0.07f * i), 1f);
            float p = bubblePhase[i];
            float x = 30f + Mathf.Repeat(i * 53.7f, width - 60f) + 6f * Mathf.Sin(time * 3f + i);
            b.rectTransform.anchoredPosition = new Vector2(x, Mathf.Lerp(8f, Height + 14f, p));
            b.rectTransform.localScale = Vector3.one * (p > 0.9f ? Mathf.Lerp(1f, 1.6f, (p - 0.9f) / 0.1f) : 1f);
            b.color = new Color(0.92f, 1f, 0.85f, 0.55f * (1f - FeelEase.InQuad(p)));
        }

        scale = FeelUi.Spring(scale, ref scaleVel, 1f, dt, 260f, 13f);
        inner.localScale = Vector3.one * Mathf.Clamp(scale, 0.6f, 1.6f);
        shake = Mathf.MoveTowards(shake, 0f, dt * 1.2f);
        float sway = symptomatic ? 2f * Mathf.Sin(time * 1.7f) : 0f;
        inner.localRotation = Quaternion.Euler(0f, 0f, -1f + sway + shake * shake * 6f * Mathf.Sin(time * 38f));
    }
}

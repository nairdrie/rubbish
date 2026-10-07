using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Top-left clock widget next to the cash: a little sky window whose color follows the day (blue day, orange dusk,
/// navy night with twinkling stars), a sun or moon riding a dashed arc from the left horizon to the right one,
/// the time ("7:42 AM"), the day counter ("DAY 3") and the phase ("Morning" … "Night"). Punches on every in-game
/// hour, bounces the sun up at morning and the moon at nightfall, pops the day label at midnight.
/// Pure view: fed an hour-of-day and day number, so it also renders in the edit-mode preview.
/// </summary>
public sealed class FeelHudClock
{
    public const float Width = 300f, Height = 104f, CompactWidth = 200f;
    const float SkyScale = 0.85f, TextX = 128f, CompactTextX = 14f;
    const float ArcRadius = 47f, HorizonY = 26f;

    RectTransform panel, sky, sun, moon, stars;
    Image skyImg, sunImg, moonImg, groundImg, arcImg;
    Image[] starImgs;
    TextMeshProUGUI timeText, dayText, phaseText;

    string lastTime, lastPhase;
    int lastHour = -1, lastDay = -1;
    bool lastNight, lastFullDay, init;
    float timeScale = 1f, timeVel, dayScale = 1f, dayVel, sunScale = 1f, sunVel, moonScale = 1f, moonVel, wobble;
    bool compact;

    public RectTransform Panel => panel;

    public void Build(RectTransform parent, Vector2 pos)
    {
        panel = FeelUi.Rect("Clock", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), pos, new Vector2(Width, Height));
        panel.localRotation = Quaternion.Euler(0f, 0f, -1.2f);
        FeelUi.Panel(panel, FeelUi.PanelColor);

        // sky window (RectMask2D clips the sun/moon when they dip below the frame)
        sky = FeelUi.Rect("Sky", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, -10f), new Vector2(128f, 98f));
        sky.localScale = Vector3.one * SkyScale; // the window is drawn at 128×98 and shown at 109×83 (the 104-tall top bar)
        skyImg = FeelUi.Img(sky, "SkyBody", FeelUi.Rounded, new Color(0.45f, 0.72f, 1f), 0.5f);
        sky.gameObject.AddComponent<RectMask2D>();

        stars = FeelUi.Stretch("Stars", sky);
        starImgs = new Image[7];
        Vector2[] sp = { new Vector2(16, 78), new Vector2(38, 60), new Vector2(60, 84), new Vector2(84, 66), new Vector2(108, 82), new Vector2(24, 44), new Vector2(112, 48) };
        for (int i = 0; i < starImgs.Length; i++)
        {
            var srt = FeelUi.Rect("Star" + i, stars, Vector2.zero, new Vector2(0.5f, 0.5f), sp[i], Vector2.one * (i % 3 == 0 ? 11f : 8f));
            starImgs[i] = srt.gameObject.AddComponent<Image>();
            starImgs[i].sprite = FeelUi.Star;
            starImgs[i].raycastTarget = false;
        }

        var arc = FeelUi.Rect("Arc", sky, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, HorizonY), Vector2.one * (ArcRadius * 2f + 4f));
        arcImg = arc.gameObject.AddComponent<Image>();
        arcImg.sprite = FeelUi.DashedRing;
        arcImg.color = new Color(1f, 1f, 1f, 0.35f);
        arcImg.raycastTarget = false;

        sun = FeelUi.Rect("Sun", sky, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, HorizonY), new Vector2(40f, 40f));
        sunImg = sun.gameObject.AddComponent<Image>();
        sunImg.sprite = FeelUi.Sun;
        sunImg.raycastTarget = false;

        moon = FeelUi.Rect("Moon", sky, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, HorizonY), new Vector2(34f, 34f));
        moonImg = moon.gameObject.AddComponent<Image>();
        moonImg.sprite = FeelUi.Moon;
        moonImg.color = new Color(1f, 0.95f, 0.7f);
        moonImg.raycastTarget = false;

        var ground = FeelUi.Rect("Ground", sky, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(128f, HorizonY + 2f));
        groundImg = FeelUi.Img(ground, "Hill", FeelUi.Rounded, new Color(0.25f, 0.55f, 0.25f), 0.5f);

        timeText = FeelUi.Text(panel, "Time", 40f, Color.white, TextAlignmentOptions.Left);
        dayText = FeelUi.Text(panel, "Day", 26f, new Color(1f, 0.82f, 0.35f), TextAlignmentOptions.Left);
        phaseText = FeelUi.Text(panel, "Phase", 19f, new Color(0.78f, 0.78f, 0.95f), TextAlignmentOptions.Left, FontStyles.Normal);
        LayoutTexts();
    }

    void LayoutTexts()
    {
        float x = compact ? CompactTextX : TextX;
        float w = (compact ? CompactWidth : Width) - x - 8f;
        Place(timeText.rectTransform, new Vector2(x, -6f), new Vector2(w, 46f));
        Place(dayText.rectTransform, new Vector2(x + 1f, -50f), new Vector2(w, 30f));
        Place(phaseText.rectTransform, new Vector2(x + 2f, -78f), new Vector2(w, 24f));
    }

    /// <summary>Tight density (§8.2): 200 wide, time and day only (the sky window hides).</summary>
    public void SetCompact(bool c)
    {
        if (c == compact || panel == null) return;
        compact = c;
        sky.gameObject.SetActive(!compact);
        panel.sizeDelta = new Vector2(compact ? CompactWidth : Width, Height);
        LayoutTexts();
    }

    /// <summary>Top-left corner (top-down reference units).</summary>
    public void SetTopLeft(Vector2 p) => HudLayout.PlaceAt(panel, p);

    static void Place(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>GameClock.Daylight01 for an arbitrary hour (same phase constants).</summary>
    public static float Daylight(float h)
    {
        h = Mathf.Repeat(h, 24f);
        if (h >= GameClock.DayStart && h <= GameClock.DuskStart) return 1f;
        if (h >= GameClock.NightStart || h < GameClock.DawnStart) return 0f;
        if (h < GameClock.DayStart) return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GameClock.DawnStart, GameClock.DayStart, h));
        return Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(GameClock.DuskStart, GameClock.NightStart, h));
    }

    public static bool IsNightHour(float h)
    {
        h = Mathf.Repeat(h, 24f);
        return h >= GameClock.NightStart || h < GameClock.DawnStart;
    }

    public static string Phase(float h, out Color color)
    {
        h = Mathf.Repeat(h, 24f);
        if (IsNightHour(h)) { color = new Color(0.62f, 0.7f, 1f); return "Night"; }
        if (h < GameClock.DayStart) { color = new Color(1f, 0.7f, 0.55f); return "Dawn"; }
        if (h < 12f) { color = new Color(1f, 0.92f, 0.55f); return "Morning"; }
        if (h < 17f) { color = new Color(0.95f, 0.95f, 0.8f); return "Afternoon"; }
        if (h < GameClock.DuskStart) { color = new Color(1f, 0.8f, 0.5f); return "Evening"; }
        color = new Color(1f, 0.6f, 0.5f); return "Dusk";
    }

    public void Tick(float hours, int day, float dt, float time)
    {
        if (panel == null) return;
        hours = Mathf.Repeat(hours, 24f);
        int hour = Mathf.FloorToInt(hours);
        bool night = IsNightHour(hours);
        bool fullDay = hours >= GameClock.DayStart && hours < GameClock.DuskStart;

        if (init)
        {
            if (hour != lastHour) timeVel += 4.5f;                        // tick-tock punch every in-game hour
            if (night && !lastNight) { moonVel += 11f; wobble = 1f; }     // nightfall: the moon bounces up
            if (fullDay && !lastFullDay) { sunVel += 11f; wobble = 1f; }  // morning: the sun bounces up
            if (day != lastDay) dayVel += 9f;                             // midnight: new day
        }
        init = true;
        lastHour = hour; lastNight = night; lastFullDay = fullDay;
        if (day != lastDay) { lastDay = day; dayText.text = "DAY " + Mathf.Max(1, day); }

        string ts = GameClock.FormatHours(hours);
        if (ts != lastTime)
        {
            lastTime = ts;
            int sp = ts.LastIndexOf(' ');
            timeText.text = sp > 0 ? $"{ts.Substring(0, sp)}<size=55%> {ts.Substring(sp + 1)}</size>" : ts;
        }
        string ph = Phase(hours, out var phColor);
        if (ph != lastPhase) { lastPhase = ph; phaseText.text = ph; phaseText.color = phColor; }

        // sky color: navy night -> blue day, warm tint around dawn/dusk
        float day01 = Daylight(hours);
        float twilight = 1f - Mathf.Abs(day01 * 2f - 1f);
        Color skyC = Color.Lerp(new Color(0.09f, 0.11f, 0.3f), new Color(0.42f, 0.7f, 1f), day01);
        skyC = Color.Lerp(skyC, new Color(1f, 0.55f, 0.42f), twilight * 0.65f);
        skyImg.color = skyC;
        groundImg.color = Color.Lerp(new Color(0.08f, 0.2f, 0.12f), new Color(0.28f, 0.6f, 0.26f), day01);
        arcImg.color = new Color(1f, 1f, 1f, Mathf.Lerp(0.22f, 0.38f, day01));
        for (int i = 0; i < starImgs.Length; i++)
        {
            float tw = 0.55f + 0.45f * Mathf.Sin(time * (2.2f + i * 0.37f) + i * 1.7f);
            starImgs[i].color = new Color(1f, 1f, 0.85f, (1f - day01) * tw);
        }

        // sun rides the arc from 5:00 (left horizon) to 20:30 (right); the moon from 20:30 to 5:00
        float sunT = (hours - GameClock.DawnStart) / (GameClock.NightStart - GameClock.DawnStart);
        float moonT = Mathf.Repeat(hours - GameClock.NightStart, 24f) / (24f - GameClock.NightStart + GameClock.DawnStart);
        float alt = Mathf.Clamp01(Mathf.Sin(Mathf.PI * Mathf.Clamp01(sunT)));
        sunImg.color = Color.Lerp(new Color(1f, 0.55f, 0.2f), new Color(1f, 0.9f, 0.3f), alt); // low sun = orange
        PlaceOnArc(sun, sunImg, sunT, ref sunScale, ref sunVel, dt);
        PlaceOnArc(moon, moonImg, moonT, ref moonScale, ref moonVel, dt);
        sun.localRotation = Quaternion.Euler(0f, 0f, -time * 25f);
        moon.localRotation = Quaternion.Euler(0f, 0f, 12f * Mathf.Sin(time * 1.3f));

        timeScale = FeelUi.Spring(timeScale, ref timeVel, 1f, dt, 240f, 13f);
        timeText.rectTransform.localScale = Vector3.one * Mathf.Clamp(timeScale, 0.7f, 1.5f);
        dayScale = FeelUi.Spring(dayScale, ref dayVel, 1f, dt);
        dayText.rectTransform.localScale = Vector3.one * Mathf.Clamp(dayScale, 0.6f, 1.8f);
        wobble = Mathf.MoveTowards(wobble, 0f, dt * 1.4f);
        panel.localRotation = Quaternion.Euler(0f, 0f, -1.2f + wobble * wobble * 6f * Mathf.Sin(time * 22f));
    }

    static void PlaceOnArc(RectTransform body, Image img, float t, ref float scale, ref float vel, float dt)
    {
        bool up = t > -0.12f && t < 1.12f;
        float tc = Mathf.Clamp(t, -0.12f, 1.12f);
        float ang = Mathf.PI * (1f - tc);
        body.anchoredPosition = new Vector2(Mathf.Cos(ang) * ArcRadius, HorizonY + Mathf.Sin(ang) * ArcRadius);
        scale = FeelUi.Spring(scale, ref vel, 1f, dt, 220f, 11f);
        body.localScale = Vector3.one * Mathf.Clamp(scale, 0.5f, 1.8f);
        var c = img.color;
        c.a = up ? 1f : 0f;
        img.color = c;
    }
}

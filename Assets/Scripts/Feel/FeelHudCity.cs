using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// City Cleanliness meter (left column): "CITY  87% clean" over a springy bar that runs red → orange → yellow →
/// green, twinkling sparkles when the streets are sparkling, wobbly stink lines + a shaking red label when STINKY.
/// A subline shows the curb situation ("12 bags out · 3 late") when the route panel isn't on screen (garage).
/// Announces crossing into STINKY / sparkling with a toast + sound (with hysteresis so it never spams).
/// <para><b>Contract view (Docs/Campaign.md §14).</b> While a district is under contract the bar IS the contract: it shows that
/// district's clean percent under its name, with the goal drawn on it (a shaded zone and a line at the threshold, labelled), and a
/// line underneath that says what to do right now: clear the old trash (how many bags are left), get over the line, or, once
/// held, how long is left, counting down live in game minutes. Dropping under the line wipes the countdown and the bar says so.</para>
/// </summary>
public sealed class FeelHudCity
{
    public const float Width = 390f;
    public const float BaseHeight = 66f, SubHeight = 22f, ObjHeight = 30f;
    const float StinkyBelow = 35f, StinkyExit = 45f, SparkleAbove = 92f, SparkleExit = 85f;
    const float LostFlashSeconds = 5f;

    RectTransform panel, barFillRt, stinkRt, subRt, objRt, goalRoot, goalZoneRt, holdFillRt, holdBgRt, pulseRt;
    CanvasGroup group, subGroup, objGroup;
    Image barFill, stinkImg, goalZoneImg, goalLineImg, holdFillImg, holdBgImg, pulseImg;
    Image[] sparkles;
    TextMeshProUGUI label, valueText, subText, objText, cTitle, goalLabel;

    float shown = -1f, shownVel, alpha, subAlpha, objAlpha, valueScale = 1f, valueVel, shake, height = BaseHeight, width = Width;
    bool subAllowed = true;
    int lastRounded = -1;
    string lastValueStr, lastSubStr, lastObjStr, lastTitleStr;
    enum Mood { Normal, Stinky, Sparkling }
    Mood mood;
    bool moodInit;
    float moodArmTime;

    // the contract view
    bool contractMode;
    float lostFlash;
    ushort lostSeen;
    bool lostInit;

    /// <summary>The first-run walk-through lights the bar up while it explains it.</summary>
    public static bool Highlight;
    /// <summary>Where the meter is right now, top-down reference units (the walk-through spotlights it).</summary>
    public static Rect LastRect;

    public RectTransform Panel => panel;
    /// <summary>0..1 how much of the meter is on screen (for stacking the left column).</summary>
    public float Presence => alpha;
    public float CurrentHeight => height;

    public void Build(RectTransform parent)
    {
        panel = FeelUi.Rect("City", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -160f), new Vector2(Width, BaseHeight));
        panel.localRotation = Quaternion.Euler(0f, 0f, 0.8f);
        group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        FeelUi.Panel(panel, FeelUi.PanelColor, 0.8f);

        // behind everything: the glow the walk-through switches on
        pulseRt = FeelUi.Rect("Pulse", panel, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        pulseRt.anchorMin = Vector2.zero; pulseRt.anchorMax = Vector2.one;
        pulseRt.offsetMin = new Vector2(-9f, -9f); pulseRt.offsetMax = new Vector2(9f, 9f);
        pulseImg = FeelUi.Img(pulseRt, "Img", FeelUi.Rounded, new Color(1f, 0.85f, 0.25f, 0f), 0.8f);
        pulseImg.raycastTarget = false;
        pulseRt.SetAsFirstSibling();

        label = FeelUi.Text(panel, "Label", 22f, new Color(1f, 0.8f, 0.3f), TextAlignmentOptions.Left);
        Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -6f), new Vector2(120f, 30f));
        label.text = "CITY";

        stinkRt = FeelUi.Rect("Stink", panel, new Vector2(0f, 1f), new Vector2(0.5f, 0f), new Vector2(92f, -22f), new Vector2(26f, 30f));
        stinkImg = stinkRt.gameObject.AddComponent<Image>();
        stinkImg.sprite = FeelUi.Stink;
        stinkImg.color = new Color(0.6f, 0.85f, 0.25f, 0.9f);
        stinkImg.raycastTarget = false;

        valueText = FeelUi.Text(panel, "Value", 26f, Color.white, TextAlignmentOptions.Right);
        Place(valueText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -4f), new Vector2(250f, 32f));

        // the contract's title: its district and its percent, on the left where the eye starts
        cTitle = FeelUi.Text(panel, "ContractTitle", 27f, Color.white, TextAlignmentOptions.Left, FontStyles.Bold);
        Place(cTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -4f), new Vector2(Width - 32f, 32f));
        StretchX(cTitle.rectTransform, 16f, 16f);
        cTitle.textWrappingMode = TextWrappingModes.NoWrap;
        cTitle.gameObject.SetActive(false);

        sparkles = new Image[3];
        Vector2[] sp = { new Vector2(-266f, -8f), new Vector2(-8f, -4f), new Vector2(-150f, -40f) };
        for (int i = 0; i < sparkles.Length; i++)
        {
            var srt = FeelUi.Rect("Sparkle" + i, panel, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), sp[i], Vector2.one * (i == 1 ? 20f : 15f));
            sparkles[i] = srt.gameObject.AddComponent<Image>();
            sparkles[i].sprite = FeelUi.Star;
            sparkles[i].raycastTarget = false;
        }

        var barBg = FeelUi.Rect("Bar", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -40f), new Vector2(Width - 28f, 18f));
        StretchX(barBg, 14f, 14f);
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.35f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(3f, 3f);
        fillArea.offsetMax = new Vector2(-3f, -3f);
        barFill = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, Color.green, 0.25f);
        barFillRt = barFill.rectTransform;
        barFillRt.anchorMax = new Vector2(0f, 1f);

        // The goal, drawn on the bar (contract view): the zone from the line to the end, the line itself, its label.
        goalRoot = FeelUi.Stretch("Goal", barBg);
        goalZoneRt = FeelUi.Rect("Zone", goalRoot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        goalZoneRt.anchorMin = new Vector2(CampaignTuning.Threshold, 0f); goalZoneRt.anchorMax = new Vector2(1f, 1f);
        goalZoneRt.offsetMin = new Vector2(0f, 3f); goalZoneRt.offsetMax = new Vector2(-3f, -3f);
        goalZoneImg = FeelUi.Img(goalZoneRt, "Img", FeelUi.Rounded, new Color(0.5f, 1f, 0.55f, 0.3f), 0.25f);
        goalZoneImg.raycastTarget = false;
        var lineRt = FeelUi.Rect("Line", goalRoot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        lineRt.anchorMin = new Vector2(CampaignTuning.Threshold, 0f); lineRt.anchorMax = new Vector2(CampaignTuning.Threshold, 1f);
        lineRt.offsetMin = new Vector2(-1.5f, -5f); lineRt.offsetMax = new Vector2(1.5f, 5f);
        goalLineImg = lineRt.gameObject.AddComponent<Image>();
        goalLineImg.color = new Color(1f, 0.93f, 0.55f, 1f);
        goalLineImg.raycastTarget = false;
        goalLabel = FeelUi.Text(goalRoot, "GoalLabel", 15f, new Color(1f, 0.93f, 0.55f), TextAlignmentOptions.Center, FontStyles.Bold);
        Place(goalLabel.rectTransform, new Vector2(CampaignTuning.Threshold, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 7f), new Vector2(120f, 18f));
        goalLabel.textWrappingMode = TextWrappingModes.NoWrap;
        goalLabel.text = $"GOAL {CampaignTuning.ThresholdPct}%";
        goalRoot.gameObject.SetActive(false);

        subRt = FeelUi.Rect("Sub", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -60f), new Vector2(Width - 32f, 24f));
        StretchX(subRt, 16f, 16f);
        subGroup = subRt.gameObject.AddComponent<CanvasGroup>();
        subText = FeelUi.Text(subRt, "Text", 19f, new Color(0.85f, 0.85f, 0.95f), TextAlignmentOptions.Left, FontStyles.Normal);

        // The contract's instruction line (Campaign.cs): on the panel's bottom edge, shown only while a contract is running,
        // with a thin bar under it that fills as the hold is held.
        objRt = FeelUi.Rect("Contract", panel, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 4f), new Vector2(Width - 32f, 24f));
        objRt.anchorMin = new Vector2(0f, 0f);
        objRt.anchorMax = new Vector2(1f, 0f);
        objRt.pivot = new Vector2(0f, 0f);
        objRt.offsetMin = new Vector2(16f, 4f);
        objRt.offsetMax = new Vector2(-16f, 4f + ObjHeight - 6f);
        objGroup = objRt.gameObject.AddComponent<CanvasGroup>();
        objGroup.alpha = 0f;
        objText = FeelUi.Text(objRt, "Text", 20f, new Color(1f, 0.85f, 0.4f), TextAlignmentOptions.Left, FontStyles.Normal);
        objText.textWrappingMode = TextWrappingModes.NoWrap;
        // Overflow, not Ellipsis: TMP drops a line that is taller than its rect, and this one is 24 px tall
        objText.overflowMode = TextOverflowModes.Overflow;
        objText.enableAutoSizing = true;
        objText.fontSizeMin = 14f;
        objText.fontSizeMax = 20f;
        objText.rectTransform.offsetMin = new Vector2(0f, 4f);
        holdBgRt = FeelUi.Rect("HoldBg", objRt, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        holdBgRt.anchorMin = new Vector2(0f, 0f); holdBgRt.anchorMax = new Vector2(1f, 0f);
        holdBgRt.offsetMin = new Vector2(0f, 0f); holdBgRt.offsetMax = new Vector2(0f, 4f);
        holdBgImg = holdBgRt.gameObject.AddComponent<Image>();
        holdBgImg.color = new Color(0f, 0f, 0f, 0.5f);
        holdBgImg.raycastTarget = false;
        holdFillRt = FeelUi.Rect("HoldFill", holdBgRt, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        holdFillRt.anchorMin = new Vector2(0f, 0f); holdFillRt.anchorMax = new Vector2(0f, 1f);
        holdFillRt.offsetMin = Vector2.zero; holdFillRt.offsetMax = Vector2.zero;
        holdFillImg = holdFillRt.gameObject.AddComponent<Image>();
        holdFillImg.color = new Color(0.49f, 1f, 0.54f, 1f);
        holdFillImg.raycastTarget = false;
        holdBgRt.gameObject.SetActive(false);
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>Horizontal stretch inside the panel with left/right insets (so <see cref="SetWidth"/> just resizes the panel).</summary>
    static void StretchX(RectTransform rt, float left, float right)
    {
        float y = rt.anchoredPosition.y, h = rt.sizeDelta.y;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(left, y - h);
        rt.offsetMax = new Vector2(-right, y);
    }

    /// <summary>Top-left corner (top-down reference units, HudLayout's left column).</summary>
    public void SetTopLeft(Vector2 p) => HudLayout.PlaceAt(panel, p);

    public void SetWidth(float w) { width = w; panel.sizeDelta = new Vector2(width, height); }

    /// <summary>The curb subline may show (false when the left column is short on room).</summary>
    public void AllowSub(bool allow) => subAllowed = allow;

    /// <summary>Height the meter wants (with its subline when it's due), for the left column's budget.</summary>
    public float WantedHeight(bool showSub) => BaseHeight + (showSub ? SubHeight : 0f) + (Campaign.Display.Active ? ObjHeight : 0f);

    /// <summary>Show the contract's elements or the city's (the title, the goal marker and the instruction line, or the label and the value).</summary>
    void ApplyMode(bool contract)
    {
        contractMode = contract;
        label.gameObject.SetActive(!contract);
        valueText.gameObject.SetActive(!contract);
        cTitle.gameObject.SetActive(contract);
        goalRoot.gameObject.SetActive(contract);
        lastTitleStr = lastObjStr = null;
        moodInit = false; moodArmTime = 0f;
    }

    /// <param name="visible">show the meter at all (living world running)</param>
    /// <param name="city">cleanliness 0..100</param>
    /// <param name="showSub">show the curb-bag subline (route panel not on screen)</param>
    public void Tick(bool visible, float city, int curbBags, int lateBags, bool showSub, float dt, float time)
    {
        if (panel == null) return;
        alpha = Mathf.MoveTowards(alpha, visible ? 1f : 0f, dt * 4f);
        group.alpha = alpha;
        if (!visible && alpha <= 0f) { moodInit = false; moodArmTime = 0f; return; }

        var camp = Campaign.Display;
        if (camp.Active != contractMode) ApplyMode(camp.Active);
        if (camp.Active) city = camp.cleanPct;           // the bar is the contract's district, not the town's average

        city = Mathf.Clamp(city, 0f, 100f);
        if (shown < 0f) shown = city;
        int rounded = Mathf.RoundToInt(city);
        if (lastRounded >= 0 && rounded != lastRounded)
        {
            if (rounded > lastRounded) valueVel += 4f;       // got cleaner: happy punch
            else shake = Mathf.Max(shake, 0.6f);             // got dirtier: little shudder
        }
        lastRounded = rounded;
        shown = FeelUi.Spring(shown, ref shownVel, city, dt, 90f, 11f);
        float s01 = Mathf.Clamp01(shown / 100f);
        barFillRt.anchorMax = new Vector2(s01, 1f);
        barFill.gameObject.SetActive(s01 > 0.01f);
        var c = FeelWorld.CleanlinessColor(shown);
        barFill.color = c;

        bool stinky = city < StinkyBelow;
        if (!contractMode)
        {
            string word = FeelWorld.CleanlinessWord(city);
            string vs = stinky ? $"{rounded}% — STINKY" : $"{rounded}% {word}";
            if (vs != lastValueStr) { lastValueStr = vs; valueText.text = vs; }
            valueText.color = stinky ? Color.Lerp(new Color(1f, 0.35f, 0.3f), new Color(0.75f, 1f, 0.3f), 0.5f + 0.5f * Mathf.Sin(time * 5f)) : c;

            // mood announcements (armed a couple of seconds after the meter appears, so loading in isn't a "change")
            moodArmTime += dt;
            UpdateMood(city);
        }
        else
        {
            string title = $"<color=#FFCC4D>{Campaign.HudName(Districts.All[Mathf.Clamp(camp.district, 0, Districts.All.Count - 1)])}</color>  <color=#{ColorUtility.ToHtmlStringRGB(c)}>{rounded}%</color>";
            if (title != lastTitleStr) { lastTitleStr = title; cTitle.text = title; }
        }

        // sparkles when clean, stink lines when dirty
        float sparkleA = Mathf.InverseLerp(78f, 92f, shown);
        for (int i = 0; i < sparkles.Length; i++)
        {
            float tw = Mathf.Max(0f, Mathf.Sin(time * (3.1f + i * 0.9f) + i * 2.1f));
            sparkles[i].color = new Color(1f, 1f, 0.8f, sparkleA * tw);
            sparkles[i].rectTransform.localScale = Vector3.one * (0.6f + 0.6f * tw);
            sparkles[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, time * 40f * (i % 2 == 0 ? 1f : -1f));
        }
        float stinkA = contractMode ? 0f : Mathf.InverseLerp(55f, 30f, shown);
        stinkRt.gameObject.SetActive(stinkA > 0.01f);
        if (stinkA > 0.01f)
        {
            stinkRt.anchoredPosition = new Vector2(92f + 2f * Mathf.Sin(time * 3f), -22f + 3f * Mathf.Sin(time * 2.3f));
            stinkRt.localScale = new Vector3(1f + 0.1f * Mathf.Sin(time * 4f), 1f + 0.15f * Mathf.Sin(time * 3.3f), 1f) * (0.6f + 0.4f * stinkA);
            stinkImg.color = new Color(0.6f, 0.85f, 0.25f, 0.9f * stinkA);
        }

        // subline: the curb situation (garage view)
        string ss = curbBags <= 0 ? "Curbs are empty. Nice."
                  : $"{curbBags} bag{(curbBags == 1 ? "" : "s")} out" + (lateBags > 0 ? $" · <color=#FF9A3C>{lateBags} late</color>" : " · all fresh");
        if (ss != lastSubStr) { lastSubStr = ss; subText.text = ss; }
        subAlpha = Mathf.MoveTowards(subAlpha, showSub && subAllowed ? 1f : 0f, dt * 5f);
        subGroup.alpha = subAlpha;

        // the contract: the instruction line, the goal glow and the hold bar
        objAlpha = Mathf.MoveTowards(objAlpha, camp.Active ? 1f : 0f, dt * 5f);
        objGroup.alpha = objAlpha;
        if (camp.Active) TickContract(camp, dt, time);
        height = BaseHeight + SubHeight * subAlpha + ObjHeight * objAlpha;
        panel.sizeDelta = new Vector2(width, height);

        // the walk-through's glow
        float glow = Highlight ? 0.28f + 0.22f * Mathf.Sin(time * 5f) : 0f;
        pulseImg.color = new Color(1f, 0.85f, 0.25f, glow);

        valueScale = FeelUi.Spring(valueScale, ref valueVel, 1f, dt, 260f, 14f);
        var punch = Vector3.one * Mathf.Clamp(valueScale, 0.7f, 1.5f);
        valueText.rectTransform.localScale = punch;
        cTitle.rectTransform.localScale = punch;
        shake = Mathf.MoveTowards(shake, 0f, dt * 1.8f);
        float wob = stinky && !contractMode ? 1.5f * Mathf.Sin(time * 9f) : 0f;
        panel.localRotation = Quaternion.Euler(0f, 0f, 0.8f + wob + shake * shake * 5f * Mathf.Sin(time * 40f));
        LastRect = HudLayout.TopDownRect(panel);
    }

    /// <summary>The contract's part of a tick: what to do now, the countdown, the hold bar, the zone glow, the lost-hold flash.</summary>
    void TickContract(CampaignDisplay camp, float dt, float time)
    {
        double now = GameClock.TotalHours;
        bool holding = camp.Holding && camp.qualifying != 0;

        if (lostInit && camp.lostSerial != lostSeen) { lostFlash = LostFlashSeconds; shake = Mathf.Max(shake, 1f); }
        lostSeen = camp.lostSerial;
        lostInit = true;
        lostFlash = Mathf.MoveTowards(lostFlash, 0f, dt);

        // the zone glows while you are in it
        float zoneA = holding ? 0.34f + 0.16f * Mathf.Sin(time * 4f) : camp.cleanPct >= CampaignTuning.ThresholdPct ? 0.34f : 0.2f;
        goalZoneImg.color = new Color(0.5f, 1f, 0.55f, zoneA);
        goalLineImg.color = holding ? new Color(0.62f, 1f, 0.66f) : new Color(1f, 0.93f, 0.55f);

        string line;
        if (lostFlash > 0f) line = $"<color=#FF5A4A><b>HOLD LOST!</b></color> back to {CampaignRules.FormatLeft(CampaignTuning.HoldHours)}";
        else if (camp.backlog > 0) line = $"<b>OLD TRASH</b>  <color=#FFB347>{camp.backlog}</color> of {camp.backlogStart} bags left";
        else if (holding) line = $"<b>HOLD</b>  <color=#7CFF8A>{CampaignRules.FormatLeft(camp.HoursLeftAt(now))}</color> left";
        else line = "<b>GET OVER THE LINE</b> to start the hold";
        if (line != lastObjStr) { lastObjStr = line; objText.text = line; }

        bool showHold = holding && lostFlash <= 0f;
        holdBgRt.gameObject.SetActive(showHold);
        if (showHold) holdFillRt.anchorMax = new Vector2(Mathf.Clamp01((float)(camp.HoldHoursAt(now) / CampaignTuning.HoldHours)), 1f);
    }

    void UpdateMood(float city)
    {
        Mood want = mood;
        if (city < StinkyBelow) want = Mood.Stinky;
        else if (city >= SparkleAbove) want = Mood.Sparkling;
        else if ((mood == Mood.Stinky && city > StinkyExit) || (mood == Mood.Sparkling && city < SparkleExit)) want = Mood.Normal;

        if (!moodInit) { mood = want; moodInit = true; return; }
        if (want == mood) return;
        mood = want;
        if (moodArmTime < 2f) return;
        if (want == Mood.Stinky)
        {
            GameHUD.Toast("The city is getting STINKY! Go grab some trash!", ToastStyle.Bad, 3.5f);
            Sfx.Play("splat", null, 0.45f, 0.8f);
            shake = 1f;
        }
        else if (want == Mood.Sparkling)
        {
            GameHUD.Toast("City sparkling clean! The neighbors love you", ToastStyle.Good, 3f);
            Sfx.Play("sparkle", null, 0.7f, 1f);
            valueVel += 10f;
        }
    }
}

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The toast lane (Docs/UIPass.md §8.1 H2, §8.2): toasts live in <see cref="HudZone.ToastLane"/> (the centre lane under the top
/// bar, between the side columns), centred, newest on top; at most <see cref="MaxVisible"/> at once and at most
/// <see cref="MaxLines"/> lines each (the full text goes to the message log, <see cref="Log"/>). If the lane is shorter than the
/// stack (a centre card clamps its bottom, small screens), the extra toasts wait in a queue instead of overlapping anything.
/// In Station mode the lane is <see cref="HudZone.BottomLane"/> above the dock and the stack grows upward.
/// Toasts punch in with overshoot, older ones slide, identical toasts merge into "text x3". Styles: Info/Good/Bad (red +
/// shake)/Rare (pulsing blue-purple, wavy text)/Legendary (gold, rainbow wavy text, wobble). Settings: Toast time scales
/// the duration; Reduce flashing turns off the rainbow, the wobble and the Bad shake.
/// </summary>
public sealed class FeelHudToasts
{
    public const int MaxVisible = 3;
    public const int MaxLines = 2;
    /// <summary>Widest a toast gets; narrower lanes wrap (then trim to 2 lines with "…").</summary>
    public const float MaxWidth = 1000f;
    /// <summary>Messages kept for the Esc menu's Messages panel (this session only, never saved).</summary>
    public const int LogSize = 30;
    const int MaxQueued = 8;
    const float PadX = 26f;
    const float Gap = 10f;
    const float DieTime = 0.25f;

    // ------------------------------------------------------------------ message log (UP-3 reads it; never saved)

    /// <summary>One logged toast (full text, untrimmed).</summary>
    public struct LogEntry
    {
        public string text;
        public ToastStyle style;
        public int day;
        public float hour;
        public float realTime;
    }

    static readonly List<LogEntry> log = new List<LogEntry>(LogSize);

    /// <summary>The last <see cref="LogSize"/> toasts, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Log => log;

    /// <summary>Raised for every new toast (not for merges).</summary>
    public static event Action<LogEntry> Logged;

    static void AddLog(string text, ToastStyle style)
    {
        var e = new LogEntry
        {
            text = text, style = style, realTime = Time.realtimeSinceStartup,
            day = Application.isPlaying ? GameSession.Day : 0, hour = Application.isPlaying ? GameClock.Hours : 0f,
        };
        if (log.Count >= LogSize) log.RemoveAt(0);
        log.Add(e);
        try { Logged?.Invoke(e); }
        catch (Exception ex) { FeelRunner.Report(ex); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        log.Clear();
        Logged = null;
    }

    // ------------------------------------------------------------------ items

    sealed class Item
    {
        public RectTransform rt, inner;
        public CanvasGroup group;
        public Image body;
        public TextMeshProUGUI text;
        public ToastStyle style;
        public string baseText;
        public int count;
        public float age, life, y, height, width, dieAge = -1f, punch = 1f, punchVel, laidFor = -1f;
        public bool Dying => dieAge >= 0f;
        public bool shown;
    }

    struct Pending { public string text; public ToastStyle style; public float duration; }

    RectTransform root;
    readonly List<Item> live = new List<Item>();
    readonly Stack<Item> pool = new Stack<Item>();
    readonly List<Pending> queue = new List<Pending>();
    Rect lane;
    bool upward;
    float fontSize = 30f;

    public int LiveCount => live.Count;
    public int QueuedCount => queue.Count;

    public void Build(RectTransform parent) => root = parent;

    /// <summary>The lane for this frame (top-down rect in the HUD root) and the stacking direction.</summary>
    public void SetLane(Rect r, bool growUpward)
    {
        lane = r;
        upward = growUpward;
        float fs = r.width >= 700f ? 30f : 26f;
        if (!Mathf.Approximately(fs, fontSize)) { fontSize = fs; foreach (var it in live) it.laidFor = -1f; }
    }

    float Width => Mathf.Clamp(lane.width, 160f, MaxWidth);

    public void Show(string text, ToastStyle style, float duration)
    {
        if (root == null || string.IsNullOrEmpty(text)) return;
        duration = Mathf.Clamp(duration, 0.8f, 12f);
        if (style == ToastStyle.Legendary) duration = Mathf.Max(duration, 4f);
        else if (style == ToastStyle.Rare) duration = Mathf.Max(duration, 3f);
        if (Application.isPlaying) duration *= Mathf.Clamp(GameSettings.ToastTime, 1f, 2f);

        // same toast already up (or waiting)? bump its counter instead of stacking a copy
        foreach (var it in live)
        {
            if (it.Dying || it.style != style || it.baseText != text) continue;
            it.count++;
            SetText(it);
            it.life = it.age + duration;
            it.punchVel += 7f;
            return;
        }
        for (int i = 0; i < queue.Count; i++)
            if (queue[i].style == style && queue[i].text == text) return;

        AddLog(text, style);
        if (queue.Count >= MaxQueued) queue.RemoveAt(0);
        queue.Add(new Pending { text = text, style = style, duration = duration });
        Admit();
    }

    /// <summary>Move queued toasts into the lane while they fit (≤ MaxVisible, and the stack fits the lane height).</summary>
    void Admit()
    {
        while (queue.Count > 0)
        {
            int alive = 0;
            float used = 0f;
            foreach (var it in live)
            {
                if (it.Dying) continue;
                alive++;
                used += it.height + Gap;
            }
            if (alive >= MaxVisible)
            {
                // the oldest living toast leaves quickly to make room (it's in the log)
                for (int i = live.Count - 1; i >= 0; i--)
                    if (!live[i].Dying) { live[i].dieAge = 0f; live[i].life = live[i].age; break; }
                return;
            }
            var p = queue[0];
            var item = pool.Count > 0 ? pool.Pop() : Create();
            item.style = p.style;
            item.baseText = p.text;
            item.count = 1;
            StyleItem(item);
            SetText(item);
            if (alive > 0 && used + item.height > lane.height + 0.5f)
            {
                // doesn't fit under the ones already up: wait (they'll leave on their own)
                item.rt.gameObject.SetActive(false);
                pool.Push(item);
                return;
            }
            queue.RemoveAt(0);
            item.rt.gameObject.SetActive(true);
            item.age = 0f;
            item.life = p.duration;
            item.dieAge = -1f;
            item.punch = 1f;
            item.punchVel = 0f;
            item.group.alpha = 1f;
            item.y = 0f;
            item.shown = false;
            item.rt.SetAsLastSibling();
            live.Insert(0, item);
        }
    }

    Item Create()
    {
        var it = new Item();
        it.rt = FeelUi.Rect("Toast", root, new Vector2(0f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(300f, 60f));
        it.group = it.rt.gameObject.AddComponent<CanvasGroup>();
        it.group.interactable = false;
        it.group.blocksRaycasts = false;
        it.inner = FeelUi.Stretch("Inner", it.rt);
        it.inner.pivot = new Vector2(0.5f, 0.5f);
        it.body = FeelUi.Panel(it.inner, FeelUi.PanelColor, 0.8f);
        it.text = FeelUi.Text(it.inner, "Text", 30f, Color.white, TextAlignmentOptions.Center);
        // inset so wrapped lines stay inside the coloured body
        it.text.rectTransform.offsetMin = new Vector2(PadX, 0f);
        it.text.rectTransform.offsetMax = new Vector2(-PadX, 0f);
        it.text.lineSpacing = -6f;
        return it;
    }

    void StyleItem(Item it)
    {
        float size = fontSize;
        Color body = FeelUi.PanelColor;
        switch (it.style)
        {
            case ToastStyle.Good: body = new Color(0.16f, 0.6f, 0.28f, 0.95f); break;
            case ToastStyle.Bad: body = new Color(0.8f, 0.16f, 0.2f, 0.95f); break;
            case ToastStyle.Rare: body = new Color(0.28f, 0.4f, 1f, 0.95f); size += 2f; break;
            case ToastStyle.Legendary: body = new Color(1f, 0.72f, 0.12f, 0.97f); size += 6f; break;
        }
        it.body.color = body;
        it.text.fontSize = size;
        it.text.color = Color.white;
        it.inner.localRotation = Quaternion.identity;
        it.inner.anchoredPosition = Vector2.zero;
        it.inner.localScale = Vector3.zero;
    }

    /// <summary>Fit the text (merged counter included) into ≤ 2 lines of the lane width and size the body.</summary>
    void SetText(Item it)
    {
        float maxText = Width - 2f * PadX;
        string counter = it.count > 1 ? $"  <color=#FFE066>x{it.count}</color>" : "";
        string body = it.baseText;
        it.text.textWrappingMode = TextWrappingModes.Normal;
        // the counter must survive trimming: fit the base text into what's left of the last line
        string full = body + counter;
        if (FeelUi.LineCount(it.text, full, maxText) > MaxLines)
        {
            float counterW = it.count > 1 ? it.text.GetPreferredValues($"  x{it.count}", 100000f, 0f).x + 4f : 0f;
            body = FeelUi.FitLines(it.text, FeelUi.StripTags(body), maxText - counterW, MaxLines);
            full = body + counter;
        }
        it.text.text = full;
        var pref = it.text.GetPreferredValues(full, 100000f, 0f);
        float textW = pref.x, textH;
        if (textW > maxText)
        {
            textW = maxText;
            textH = it.text.GetPreferredValues(full, maxText, 0f).y;
        }
        else
        {
            it.text.textWrappingMode = TextWrappingModes.NoWrap;
            textH = pref.y;
        }
        it.width = Mathf.Clamp(textW + 2f * PadX, 180f, Width);
        it.height = Mathf.Max(50f, textH + 18f);
        it.rt.sizeDelta = new Vector2(it.width, it.height);
        it.laidFor = Width;
    }

    public void Tick(float dt, float time)
    {
        bool calm = Application.isPlaying && GameSettings.ReduceFlashing;
        Admit();
        float cursor = 0f;
        float cx = lane.center.x;
        for (int i = 0; i < live.Count; i++)
        {
            var it = live[i];
            if (Mathf.Abs(it.laidFor - Width) > 0.5f) SetText(it);
            it.age += dt;
            if (!it.Dying && it.age >= it.life) it.dieAge = 0f;
            if (it.Dying) it.dieAge += dt;
            if (it.Dying && it.dieAge >= DieTime)
            {
                it.rt.gameObject.SetActive(false);
                live.RemoveAt(i--);
                pool.Push(it);
                continue;
            }

            // layout: slide toward the stacked slot (top-down y inside the lane; upward lanes stack from the bottom)
            float targetY = upward ? lane.yMax - cursor - it.height : lane.yMin + cursor;
            it.y = !it.shown ? targetY : Mathf.Lerp(it.y, targetY, 1f - Mathf.Exp(-14f * dt));
            it.shown = true;
            float dieP = it.Dying ? it.dieAge / DieTime : 0f;
            it.rt.anchoredPosition = new Vector2(cx, -(it.y - dieP * 24f * (upward ? -1f : 1f)));
            cursor += (it.height + Gap) * (1f - dieP);

            // scale: punch-in with overshoot (legendary: elastic), then springy punch on merges
            float appear = it.style == ToastStyle.Legendary && !calm
                ? FeelEase.OutElastic(Mathf.Clamp01(it.age / 0.7f))
                : FeelEase.OutBack(Mathf.Clamp01(it.age / 0.3f), calm ? 1.2f : 2.6f);
            it.punch = FeelUi.Spring(it.punch, ref it.punchVel, 1f, dt, 300f, 14f);
            float s = appear * Mathf.Clamp(it.punch, 0.5f, 1.8f) * (1f - FeelEase.InQuad(dieP) * 0.4f);
            it.inner.localScale = new Vector3(s, s, 1f);
            it.group.alpha = 1f - dieP;

            switch (it.style)
            {
                case ToastStyle.Bad:
                {
                    float k = calm ? 0f : Mathf.Clamp01(1f - it.age / 0.4f);
                    it.inner.anchoredPosition = new Vector2(12f * Mathf.Sin(it.age * 70f) * k, 0f);
                    break;
                }
                case ToastStyle.Rare:
                    it.body.color = calm ? new Color(0.4f, 0.36f, 0.98f, 0.95f)
                                         : Color.Lerp(new Color(0.28f, 0.4f, 1f, 0.95f), new Color(0.62f, 0.3f, 0.95f, 0.95f), 0.5f + 0.5f * Mathf.Sin(time * 5f));
                    if (!calm) FeelUi.Wave(it.text, time, 2.5f, false, 7f);
                    break;
                case ToastStyle.Legendary:
                    it.inner.localRotation = calm ? Quaternion.identity : Quaternion.Euler(0f, 0f, 3f * Mathf.Sin(time * 5f));
                    it.body.color = calm ? new Color(1f, 0.66f, 0.12f, 0.97f)
                                         : Color.Lerp(new Color(1f, 0.72f, 0.12f, 0.97f), new Color(1f, 0.55f, 0.1f, 0.97f), 0.5f + 0.5f * Mathf.Sin(time * 7f));
                    if (!calm) FeelUi.Wave(it.text, time, 5f, true, 9f);
                    break;
            }
        }
    }

    /// <summary>Top-down rects of the toasts on screen (for the overlap audit).</summary>
    public void CollectRects(List<(string name, Rect r)> into)
    {
        for (int i = 0; i < live.Count; i++)
        {
            var it = live[i];
            if (it.Dying) continue;
            into.Add(("Toast" + i, new Rect(it.rt.anchoredPosition.x - it.width * 0.5f, -it.rt.anchoredPosition.y, it.width, it.height)));
        }
    }

    /// <summary>The longest line count among the toasts on screen (≤ MaxLines; for the checks).</summary>
    public int MaxLinesShown(float maxText)
    {
        int m = 0;
        foreach (var it in live) m = Mathf.Max(m, FeelUi.LineCount(it.text, it.text.text, it.width - 2f * PadX));
        return m;
    }
}

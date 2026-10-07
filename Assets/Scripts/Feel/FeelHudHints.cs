using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The context hint as key-cap chips (Docs/UIPass.md §8.4, H4). The text callers already pass to
/// <see cref="GameHUD.SetContextHint"/> is split into an optional <b>title</b> (before " · "), <b>chips</b> ("[key] label"
/// pairs) and an optional <b>tip</b> (a trailing parenthetical):
/// <c>"Suspicious Trash Bag · [E/LMB] Slam it open [RMB] Chuck [Q] Step away (aim at the compactor to crush it unopened)"</c>
/// → title "Suspicious Trash Bag" (18 px grey) · chips [E/LMB] Slam it open · [RMB] Chuck · [Q] Step away (24 px bold on a
/// 72%-opaque backing) · tip "aim at the compactor…" (18 px italic). Bracket-free key lists ("W/S drive · A/D steer · H honk")
/// become chips too; anything else renders as plain text, so callers never change.
/// <para>Chips wrap to at most 2 rows inside the dock width; when they don't fit, the tip and title drop, then the chips go
/// compact (20 px labels), then a 3rd row is allowed: never smaller than the 18 px floor (§3, §8.2 rule 3). A chip
/// whose key and label equal the prompt's is dropped ("E Slam it open!" isn't shown twice). Settings → Control hints: off hides
/// the chips but keeps plain status lines and the prompt.</para>
/// </summary>
public sealed class FeelHudHints
{
    public const float ChipH = 44f, RowGap = 8f, ChipGap = 10f, ChipText = 24f, SmallText = 18f;
    public const int MaxRows = 2;
    const float ChipPadX = 10f, CapToLabel = 8f, CapH = 34f;

    public struct Chip
    {
        public string key, label;
        public override string ToString() => $"[{key}] {label}";
    }

    /// <summary>A parsed hint.</summary>
    public sealed class Parsed
    {
        public string title, tip, plain;
        public readonly List<Chip> chips = new List<Chip>(8);
        public bool IsPlain => plain != null;
        public void Clear() { title = tip = plain = null; chips.Clear(); }
    }

    // ------------------------------------------------------------------ parsing

    static readonly Regex KeyToken = new Regex(@"^(?:hold\s+)?[A-Za-z0-9]{1,6}(?:\s*[/+]\s*[A-Za-z0-9]{1,6})*$",
                                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PlainSegment = new Regex(
        @"^(?:(?<hold>[Hh]old)\s+)?(?<key>[A-Z][A-Z0-9]?(?:/[A-Z0-9]{1,3})*|Space|Shift|Ctrl|Tab|Esc|Enter|LMB|RMB):?\s+(?<label>\S.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Split <paramref name="s"/> into title / chips / tip (or plain). Returns false for empty text.</summary>
    public static bool Parse(string s, Parsed into)
    {
        into.Clear();
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.IndexOf('[') >= 0 && ParseBrackets(s, into)) return true;
        into.Clear();
        if (s.IndexOf('[') < 0 && ParsePlainKeys(s, into)) return true;
        into.Clear();
        into.plain = s;
        return true;
    }

    static bool ParseBrackets(string s, Parsed into)
    {
        int first = s.IndexOf('[');
        string head = s.Substring(0, first).Trim();
        head = head.TrimEnd('·', ' ', '-', ':').Trim();
        if (head.Length > 0) into.title = head;

        int lastClose = s.LastIndexOf(']');
        if (lastClose < first) return false;
        // the trailing parenthetical after the last chip is the tip
        string tail = s.Substring(lastClose + 1);
        string tip = null;
        int tipAt = -1;
        string tt = tail.TrimEnd();
        if (tt.EndsWith(")"))
        {
            int open = FindTipOpen(tt);
            if (open > 0) { tip = tt.Substring(open + 1, tt.Length - open - 2).Trim(); tipAt = lastClose + 1 + open; }
        }
        int i = first;
        while (i >= 0 && i < s.Length)
        {
            int close = s.IndexOf(']', i);
            if (close < 0) return false;
            string key = s.Substring(i + 1, close - i - 1).Trim();
            if (!KeyToken.IsMatch(key)) return false;
            int next = s.IndexOf('[', close + 1);
            int end = next >= 0 ? next : tipAt >= 0 ? tipAt : s.Length;
            string label = s.Substring(close + 1, Mathf.Max(0, end - close - 1)).Trim().TrimEnd('·', ' ').Trim();
            into.chips.Add(new Chip { key = NormalizeKey(key), label = label });
            i = next;
        }
        into.tip = string.IsNullOrEmpty(tip) ? null : tip;
        return into.chips.Count > 0;
    }

    static int FindTipOpen(string t)
    {
        int depth = 0;
        for (int i = t.Length - 1; i >= 0; i--)
        {
            if (t[i] == ')') depth++;
            else if (t[i] == '(') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    static bool ParsePlainKeys(string s, Parsed into)
    {
        var segs = s.Split(new[] { " · " }, StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 1 && !s.StartsWith("Hold ", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var raw in segs)
        {
            var m = PlainSegment.Match(raw.Trim());
            if (!m.Success) return false;
            string key = m.Groups["key"].Value;
            if (m.Groups["hold"].Success) key = "Hold " + key;
            into.chips.Add(new Chip { key = key, label = m.Groups["label"].Value.Trim() });
        }
        return into.chips.Count > 0;
    }

    static string NormalizeKey(string k)
    {
        k = Regex.Replace(k.Trim(), @"\s*([/+])\s*", "$1");
        if (k.StartsWith("hold ", StringComparison.OrdinalIgnoreCase)) k = "Hold " + k.Substring(5).Trim();
        return k;
    }

    /// <summary>Same action as the prompt? (key alternatives include it and the labels match, ignoring case and punctuation).</summary>
    public static bool SameAsPrompt(Chip c, string promptKey, string promptText)
    {
        if (string.IsNullOrEmpty(promptKey) || string.IsNullOrEmpty(promptText)) return false;
        string ck = c.key.StartsWith("Hold ", StringComparison.Ordinal) ? c.key.Substring(5) : c.key;
        bool keyHit = false;
        foreach (var part in ck.Split('/', '+'))
            if (string.Equals(part.Trim(), promptKey.Trim(), StringComparison.OrdinalIgnoreCase)) { keyHit = true; break; }
        return keyHit && Norm(c.label) == Norm(promptText);
    }

    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch) || ch == ' ') sb.Append(ch);
        return Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
    }

    // ------------------------------------------------------------------ view

    sealed class ChipView
    {
        public RectTransform rt;
        public Image back;
        public FeelKeyCap cap;
        public TextMeshProUGUI label;
        public float width;
    }

    RectTransform root;
    CanvasGroup group;
    readonly List<ChipView> pool = new List<ChipView>(8);
    TextMeshProUGUI title, tip, plainText;
    readonly Parsed parsed = new Parsed();
    string wanted, shown, promptKey, promptText;
    float shownWidth = -1f, alpha;
    bool hintsOn = true, shownHintsOn = true;
    Vector2 size;
    int visibleChips, rows;
    float contentScale = 1f;
    bool compactChips;
    bool layoutDirty = true;

    /// <summary>The chips went compact (20 px labels) to fit 2 rows.</summary>
    public bool Compact => compactChips;

    public RectTransform Root => root;
    public Vector2 Size => size;
    public int VisibleChips => visibleChips;
    public int Rows => rows;
    public bool HasTitle => title != null && title.gameObject.activeSelf;
    public bool HasTip => tip != null && tip.gameObject.activeSelf;
    public Parsed Last => parsed;
    public float Alpha => alpha;

    public void Build(RectTransform parent)
    {
        root = FeelUi.Rect("Hints", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(10f, ChipH));
        group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        title = Small("Title", new Color(0.78f, 0.78f, 0.86f), FontStyles.Bold);
        tip = Small("Tip", new Color(0.86f, 0.86f, 0.94f), FontStyles.Italic);
        plainText = FeelUi.Text(root, "Plain", 22f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
        TopLeft(plainText.rectTransform);
        plainText.textWrappingMode = TextWrappingModes.Normal;
        title.gameObject.SetActive(false);
        tip.gameObject.SetActive(false);
        plainText.gameObject.SetActive(false);
    }

    TextMeshProUGUI Small(string name, Color c, FontStyles style)
    {
        var t = FeelUi.Text(root, name, SmallText, c, TextAlignmentOptions.MidlineLeft, style);
        TopLeft(t.rectTransform);
        return t;
    }

    static void TopLeft(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
    }

    /// <summary>The caller's hint text (null clears).</summary>
    public void SetText(string s) => wanted = string.IsNullOrEmpty(s) ? null : s;

    /// <summary>The prompt currently up (chips that repeat it are dropped).</summary>
    public void SetPrompt(string key, string text)
    {
        if (key == promptKey && text == promptText) return;
        promptKey = key; promptText = text;
        // null is a real, cleared hint. Don't use it as the cache-invalid sentinel: when
        // exiting a truck both text and prompt change, and null == null used to skip
        // Layout, leaving the previous driving chips visibly stuck on screen.
        layoutDirty = true;
    }

    public void SetHintsEnabled(bool on) => hintsOn = on;

    /// <summary>Rebuild the chips if the text, prompt, setting or width changed. Returns the block size.</summary>
    public Vector2 Sync(float maxWidth)
    {
        if (!layoutDirty && wanted == shown && Mathf.Abs(maxWidth - shownWidth) < 0.5f && hintsOn == shownHintsOn) return size;
        shown = wanted;
        shownWidth = maxWidth;
        shownHintsOn = hintsOn;
        Layout(maxWidth);
        layoutDirty = false;
        return size;
    }

    void Layout(float maxWidth)
    {
        foreach (var c in pool) c.rt.gameObject.SetActive(false);
        title.gameObject.SetActive(false);
        tip.gameObject.SetActive(false);
        plainText.gameObject.SetActive(false);
        visibleChips = 0;
        rows = 0;
        size = Vector2.zero;
        contentScale = 1f;
        compactChips = false;
        root.localScale = Vector3.one;
        if (!Parse(shown, parsed)) return;

        if (parsed.IsPlain)
        {
            plainText.gameObject.SetActive(true);
            string p = FeelUi.FitLines(plainText, parsed.plain, maxWidth - 24f, 2);
            plainText.text = p;
            float w = Mathf.Min(maxWidth - 24f, plainText.GetPreferredValues(p, 100000f, 0f).x + 2f);
            float h = plainText.GetPreferredValues(p, w, 0f).y;
            plainText.rectTransform.anchoredPosition = new Vector2(12f, -6f);
            plainText.rectTransform.sizeDelta = new Vector2(w, h);
            size = new Vector2(w + 24f, h + 12f);
            rows = 1;
            root.sizeDelta = size;
            return;
        }
        if (!hintsOn) { root.sizeDelta = Vector2.zero; return; }

        // chips (minus the one repeating the prompt)
        var items = new List<(int kind, int chip)>(parsed.chips.Count + 2); // kind 0 title, 1 chip, 2 tip
        if (parsed.title != null) items.Add((0, -1));
        int ci = 0;
        var shownChips = new List<Chip>(parsed.chips.Count);
        for (int i = 0; i < parsed.chips.Count; i++)
        {
            if (SameAsPrompt(parsed.chips[i], promptKey, promptText)) continue;
            Chip c = parsed.chips[i];
            var v = ChipAt(ci++);
            v.rt.gameObject.SetActive(true); // TMP measures right only once it has been active (built under an inactive parent it can't)
            SetChip(v, c, false);
            shownChips.Add(c);
            items.Add((1, ci - 1));
        }
        for (int i = ci; i < pool.Count; i++) pool[i].rt.gameObject.SetActive(false);
        if (parsed.tip != null) items.Add((2, -1));
        if (ci == 0 && parsed.title == null && parsed.tip == null) return;

        title.gameObject.SetActive(true);
        tip.gameObject.SetActive(true);
        title.text = parsed.title ?? "";
        tip.text = parsed.tip ?? "";

        // ≤ 2 rows; else drop the tip, then the title, then compact chips, then allow a 3rd row (never below 18 px)
        bool showTitle = parsed.title != null, showTip = parsed.tip != null;
        int rowsAllowed = MaxRows;
        if (Flow(items, maxWidth, showTitle, showTip, false) > rowsAllowed && showTip) showTip = false;
        if (Flow(items, maxWidth, showTitle, showTip, false) > rowsAllowed && showTitle) showTitle = false;
        if (Flow(items, maxWidth, showTitle, showTip, false) > rowsAllowed)
        {
            for (int i = 0; i < shownChips.Count; i++) SetChip(pool[i], shownChips[i], true);
            compactChips = true;
        }
        if (Flow(items, maxWidth, showTitle, showTip, false) > rowsAllowed) rowsAllowed = MaxRows + 1;
        Flow(items, maxWidth, showTitle, showTip, true);
        visibleChips = ci;
    }

    /// <summary>Lay items into rows (centred); returns the row count. <paramref name="apply"/> = move the views.</summary>
    int Flow(List<(int kind, int chip)> items, float maxWidth, bool showTitle, bool showTip, bool apply)
    {
        var rowsW = new List<float>(4) { 0f };
        var rowOf = new int[items.Count];
        var xOf = new float[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            float w = ItemWidth(items[i], showTitle, showTip);
            if (w <= 0f) { rowOf[i] = -1; continue; }
            int r = rowsW.Count - 1;
            float cur = rowsW[r];
            float need = cur > 0f ? cur + ChipGap + w : w;
            if (need > maxWidth && cur > 0f) { rowsW.Add(0f); r++; cur = 0f; need = w; }
            xOf[i] = cur > 0f ? cur + ChipGap : 0f;
            rowsW[r] = need;
            rowOf[i] = r;
        }
        int count = rowsW.Count;
        if (!apply) return count;

        float blockW = 0f;
        foreach (var w in rowsW) blockW = Mathf.Max(blockW, w);
        rows = count;
        size = new Vector2(blockW * contentScale, (count * ChipH + (count - 1) * RowGap) * contentScale);
        root.sizeDelta = new Vector2(blockW, count * ChipH + (count - 1) * RowGap);
        title.gameObject.SetActive(false);
        tip.gameObject.SetActive(false);
        for (int i = 0; i < items.Count; i++)
        {
            int r = rowOf[i];
            if (r < 0) continue;
            float x = (blockW - rowsW[r]) * 0.5f + xOf[i];
            float y = -(r * (ChipH + RowGap));
            var it = items[i];
            RectTransform rt;
            if (it.kind == 0) { title.gameObject.SetActive(true); rt = title.rectTransform; rt.sizeDelta = new Vector2(ItemWidth(it, true, true), ChipH); }
            else if (it.kind == 2) { tip.gameObject.SetActive(true); rt = tip.rectTransform; rt.sizeDelta = new Vector2(ItemWidth(it, true, true), ChipH); }
            else { var v = pool[it.chip]; v.rt.gameObject.SetActive(true); rt = v.rt; }
            rt.anchoredPosition = new Vector2(x, y);
        }
        return count;
    }

    float ItemWidth((int kind, int chip) it, bool showTitle, bool showTip)
    {
        if (it.kind == 0) return showTitle ? title.GetPreferredValues(title.text, 100000f, 0f).x + 4f : 0f;
        if (it.kind == 2) return showTip ? tip.GetPreferredValues(tip.text, 100000f, 0f).x + 4f : 0f;
        return pool[it.chip].width;
    }

    ChipView ChipAt(int i)
    {
        while (pool.Count <= i)
        {
            var v = new ChipView();
            v.rt = FeelUi.Rect("Chip", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(100f, ChipH));
            v.back = FeelUi.Img(v.rt, "Back", FeelUi.Rounded, new Color(0.11f, 0.09f, 0.17f, 0.72f), 0.5f);
            v.cap = new FeelKeyCap(v.rt, CapH);
            v.label = FeelUi.Text(v.rt, "Label", ChipText, Color.white, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            v.label.rectTransform.anchorMin = v.label.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            v.label.rectTransform.pivot = new Vector2(0f, 0.5f);
            pool.Add(v);
        }
        return pool[i];
    }

    void SetChip(ChipView v, Chip c, bool compact)
    {
        v.label.fontSize = compact ? 20f : ChipText;
        float cw = v.cap.Set(c.key);
        v.cap.Root.anchoredPosition = new Vector2(ChipPadX * 0.6f, 0f);
        v.label.text = c.label ?? "";
        float lw = string.IsNullOrEmpty(c.label) ? 0f : v.label.GetPreferredValues(v.label.text, 100000f, 0f).x + 2f;
        v.label.rectTransform.anchoredPosition = new Vector2(ChipPadX * 0.6f + cw + CapToLabel, 0f);
        v.label.rectTransform.sizeDelta = new Vector2(lw, ChipH);
        v.width = ChipPadX * 0.6f + cw + (lw > 0f ? CapToLabel + lw : 0f) + ChipPadX;
        v.rt.sizeDelta = new Vector2(v.width, ChipH);
    }

    /// <summary>Place the block (top-down top-left) and fade it.</summary>
    public void Tick(Vector2 topLeft, bool visible, float dt)
    {
        if (root == null) return;
        HudLayout.PlaceAt(root, topLeft);
        bool on = visible && size.x > 0f;
        alpha = Mathf.MoveTowards(alpha, on ? 1f : 0f, dt * 7f);
        group.alpha = alpha;
    }

    /// <summary>Top-down rect of the block in the HUD root (scaled), for the overlap audit.</summary>
    public Rect TopDownRect
    {
        get
        {
            var p = root.anchoredPosition;
            return new Rect(p.x, -p.y, size.x, size.y);
        }
    }
}

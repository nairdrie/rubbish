using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime UI building blocks for the HUD: procedurally generated sprites (rounded panel, circle, arrow,
/// sunburst), a chunky outlined TMP material, and small factory helpers. No asset files needed.
/// </summary>
public static class FeelUi
{
    static Sprite rounded, circle, arrow, rays;

    public static readonly Color PanelColor = new Color(0.11f, 0.09f, 0.17f, 0.88f);
    public static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.45f);
    public static readonly Color Gold = new Color(1f, 0.84f, 0.25f);
    public static readonly Color GoodGreen = new Color(0.35f, 1f, 0.45f);
    public static readonly Color BadRed = new Color(1f, 0.35f, 0.35f);

    /// <summary>White rounded rectangle, 9-sliced.</summary>
    public static Sprite Rounded => rounded != null ? rounded : rounded = MakeRounded();
    /// <summary>White anti-aliased disk.</summary>
    public static Sprite Circle => circle != null ? circle : circle = MakeCircle();
    /// <summary>Chunky arrow pointing up.</summary>
    public static Sprite Arrow => arrow != null ? arrow : arrow = MakeArrow();
    /// <summary>Soft sunburst rays for the big reveal.</summary>
    public static Sprite Rays => rays != null ? rays : rays = MakeRays();

    static Sprite sun, moon, star, dashedRing, stink;
    /// <summary>Cartoon sun: disk + 8 chunky triangular rays.</summary>
    public static Sprite Sun => sun != null ? sun : sun = MakeShape("FeelSun", 96, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y);
        if (r <= 0.3f) return true;
        if (r > 0.49f) return false;
        float a = Mathf.Repeat(Mathf.Atan2(y, x) / (2f * Mathf.PI) * 8f, 1f); // 0..1 within each ray sector
        float half = Mathf.Lerp(0.42f, 0f, Mathf.InverseLerp(0.33f, 0.49f, r)) * 0.5f;
        return r >= 0.35f && Mathf.Abs(a - 0.5f) <= half;
    });
    /// <summary>Crescent moon (opening to the right).</summary>
    public static Sprite Moon => moon != null ? moon : moon = MakeShape("FeelMoon", 96, (x, y) =>
    {
        bool outer = x * x + y * y <= 0.44f * 0.44f;
        float bx = x - 0.2f, by = y - 0.1f;
        return outer && bx * bx + by * by > 0.36f * 0.36f;
    });
    /// <summary>Four-point sparkle star.</summary>
    public static Sprite Star => star != null ? star : star = MakeShape("FeelStar", 64, (x, y) =>
        Mathf.Sqrt(Mathf.Abs(x)) + Mathf.Sqrt(Mathf.Abs(y)) <= Mathf.Sqrt(0.47f));
    /// <summary>Thin dashed ring (the sun/moon's path on the clock).</summary>
    public static Sprite DashedRing => dashedRing != null ? dashedRing : dashedRing = MakeShape("FeelDashRing", 128, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y);
        if (Mathf.Abs(r - 0.47f) > 0.018f) return false;
        return Mathf.Sin(Mathf.Atan2(y, x) * 18f) > -0.1f;
    });
    /// <summary>Three wavy stink lines.</summary>
    public static Sprite Stink => stink != null ? stink : stink = MakeShape("FeelStinkLines", 64, (x, y) =>
    {
        if (y < -0.42f || y > 0.42f) return false;
        for (int k = -1; k <= 1; k++)
        {
            float cx = k * 0.27f + 0.07f * Mathf.Sin(y * 13f + k * 1.3f);
            float w = 0.05f * (1f - Mathf.Abs(y) * 0.9f);
            if (Mathf.Abs(x - cx) <= w) return true;
        }
        return false;
    });

    /// <summary>Supersampled shape from a predicate over centered coords x,y in [-0.5, 0.5].</summary>
    static Sprite MakeShape(string name, int size, Func<float, float, bool> inside)
    {
        var tex = NewTex(size, size, name);
        return Finish(tex, Supersample(size, (u, v) => inside(u - 0.5f, v - 0.5f)), Vector4.zero);
    }

    // ------------------------------------------------------------------ sprites

    static Sprite MakeRounded()
    {
        const int S = 64; const float R = 24f;
        var tex = NewTex(S, S, "FeelRounded");
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float qx = Mathf.Abs(x + 0.5f - S * 0.5f) - (S * 0.5f - R);
                float qy = Mathf.Abs(y + 0.5f - S * 0.5f) - (S * 0.5f - R);
                float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - R;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255f));
            }
        return Finish(tex, px, new Vector4(28, 28, 28, 28));
    }

    static Sprite MakeCircle()
    {
        const int S = 64;
        var tex = NewTex(S, S, "FeelCircle");
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = new Vector2(x + 0.5f - S * 0.5f, y + 0.5f - S * 0.5f).magnitude - (S * 0.5f - 1f);
                px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255f));
            }
        return Finish(tex, px, Vector4.zero);
    }

    static Sprite MakeArrow()
    {
        const int S = 64;
        var tex = NewTex(S, S, "FeelArrow");
        // triangle head + stem, supersampled 4x4
        Func<float, float, bool> inside = (u, v) =>
        {
            if (v >= 0.5f && v <= 0.95f)
            {
                float half = (0.95f - v) / 0.45f * 0.42f;
                return Mathf.Abs(u - 0.5f) <= half;
            }
            return v >= 0.08f && v < 0.5f && Mathf.Abs(u - 0.5f) <= 0.15f;
        };
        return Finish(tex, Supersample(S, inside), Vector4.zero);
    }

    static Sprite MakeRays()
    {
        const int S = 256;
        var tex = NewTex(S, S, "FeelRays");
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S * 2f - 1f, v = (y + 0.5f) / S * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);
                float ang = Mathf.Atan2(v, u);
                float ray = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Sin(ang * 12f) * 3f + 0.2f));
                float fall = Mathf.Clamp01(1f - r);
                float a = ray * fall * fall * 0.9f + Mathf.Clamp01(1f - r * 1.6f) * 0.35f;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
        return Finish(tex, px, Vector4.zero);
    }

    static Color32[] Supersample(int S, Func<float, float, bool> inside)
    {
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        if (inside((x + (sx + 0.5f) / 4f) / S, (y + (sy + 0.5f) / 4f) / S)) hits++;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
            }
        return px;
    }

    static Texture2D NewTex(int w, int h, string name) =>
        new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
        };

    static Sprite Finish(Texture2D tex, Color32[] px, Vector4 border)
    {
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0,
                               SpriteMeshType.FullRect, border);
        sp.name = tex.name;
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Shared TMP material for <see cref="Text"/>: the theme's body font (Fredoka once Setup_Fonts has run, else LiberationSans)
    /// + thick dark outline + drop shadow (cartoony). The same instance UiKit uses for outlined labels, so they batch. Only valid
    /// for texts in <see cref="UiTheme.BodyFont"/>; other fonts get theirs from <see cref="UiTheme.OutlineFor"/>.
    /// </summary>
    public static Material TextMaterial => UiTheme.OutlineFor(UiTheme.BodyFont);

    // ------------------------------------------------------------------ factories

    public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5; // UI
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    public static RectTransform Stretch(string name, Transform parent)
    {
        var rt = Rect(name, parent, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    public static Image Img(Transform parent, string name, Sprite sprite, Color color, float cornerScale = 1f)
    {
        var rt = Stretch(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        if (sprite != null && sprite.border.sqrMagnitude > 0f)
        {
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f / Mathf.Max(0.05f, cornerScale);
        }
        return img;
    }

    /// <summary>Rounded panel with a chunky offset drop shadow behind it. Returns the body image.</summary>
    public static Image Panel(Transform parent, Color color, float cornerScale = 1f, Vector2? shadowOffset = null)
    {
        var shadow = Img(parent, "Shadow", Rounded, ShadowColor, cornerScale);
        var off = shadowOffset ?? new Vector2(5f, -6f);
        shadow.rectTransform.offsetMin = off;
        shadow.rectTransform.offsetMax = off;
        return Img(parent, "Body", Rounded, color, cornerScale);
    }

    public static TextMeshProUGUI Text(Transform parent, string name, float size, Color color,
                                       TextAlignmentOptions align, FontStyles style = FontStyles.Bold)
    {
        var rt = Stretch(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        // the theme's body font (Bold style = Fredoka SemiBold through its weight table) and its outlined material
        var font = UiTheme.BodyFont;
        if (font != null) t.font = font;
        var mat = TextMaterial;
        if (mat != null) t.fontSharedMaterial = mat;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.fontStyle = style;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.richText = true;
        return t;
    }

    /// <summary>Per-character wave (and optional rainbow) on a TMP text. Call every frame after setting text.</summary>
    public static void Wave(TMP_Text t, float time, float amp, bool rainbow, float speed = 8f)
    {
        if (t == null || !t.isActiveAndEnabled) return;
        t.ForceMeshUpdate();
        var ti = t.textInfo;
        if (ti == null) return;
        for (int i = 0; i < ti.characterCount; i++)
        {
            var ci = ti.characterInfo[i];
            if (!ci.isVisible) continue;
            int mi = ci.materialReferenceIndex, vi = ci.vertexIndex;
            if (mi >= ti.meshInfo.Length) continue;
            var verts = ti.meshInfo[mi].vertices;
            var cols = ti.meshInfo[mi].colors32;
            if (verts == null || vi + 3 >= verts.Length) continue;
            float off = Mathf.Sin(time * speed + i * 0.55f) * amp;
            for (int k = 0; k < 4; k++) verts[vi + k].y += off;
            if (rainbow && cols != null)
            {
                Color32 c = Color.HSVToRGB(Mathf.Repeat(time * 0.6f + i * 0.07f, 1f), 0.7f, 1f);
                c.a = cols[vi].a;
                for (int k = 0; k < 4; k++) cols[vi + k] = c;
            }
        }
        t.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
    }

    /// <summary>"#RRGGBB" for rich text.</summary>
    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    /// <summary>Damped spring step (for bouncy bars/scales). Returns new value, updates velocity.</summary>
    public static float Spring(float value, ref float velocity, float target, float dt, float stiffness = 180f, float damping = 12f)
    {
        dt = Mathf.Min(dt, 0.05f);
        float a = (target - value) * stiffness - velocity * damping;
        velocity += a * dt;
        return value + velocity * dt;
    }

    /// <summary>Money with sign for deltas: "+$0.42" / "-$10.00".</summary>
    public static string Delta(float v) => (v >= 0 ? "+" : "") + Economy.Money(v);

    /// <summary>
    /// Plain one-line text trimmed with "…" so it fits maxWidth in t's font/size. Use instead of
    /// TextOverflowModes.Ellipsis: TMP's special-character lookup fails for our default font (it logs a warning and
    /// silently switches to Truncate), while a literal "…" in the text renders fine. Rich-text strings are returned as-is.
    /// </summary>
    public static string Fit(TMP_Text t, string s, float maxWidth)
    {
        if (t == null || string.IsNullOrEmpty(s) || maxWidth <= 0f || s.IndexOf('<') >= 0) return s ?? "";
        if (t.GetPreferredValues(s, 100000f, 0f).x <= maxWidth) return s;
        int lo = 0, hi = s.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (t.GetPreferredValues(s.Substring(0, mid).TrimEnd() + "…", 100000f, 0f).x <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo <= 0 ? "…" : s.Substring(0, lo).TrimEnd() + "…";
    }

    /// <summary>Lines <paramref name="s"/> takes when word-wrapped at <paramref name="maxWidth"/> in t's font/size.</summary>
    public static int LineCount(TMP_Text t, string s, float maxWidth)
    {
        if (t == null || string.IsNullOrEmpty(s)) return 0;
        float one = t.GetPreferredValues("Ag", 100000f, 0f).y;
        if (one <= 0.01f) return 1;
        float h = t.GetPreferredValues(s, Mathf.Max(1f, maxWidth), 0f).y;
        // line spacing makes n lines a touch shorter than n × one line
        return Mathf.Max(1, Mathf.RoundToInt(h / one + 0.15f));
    }

    /// <summary>
    /// The multi-line <see cref="Fit"/> (UIPass §8.1 H1): <paramref name="s"/> word-wrapped at <paramref name="maxWidth"/> in at most
    /// <paramref name="maxLines"/> lines; if it needs more, it's cut at a word and the last line ends in "…". Rich-text tags
    /// are kept when the text already fits, otherwise they're stripped before trimming (so a cut can't break a tag).
    /// </summary>
    public static string FitLines(TMP_Text t, string s, float maxWidth, int maxLines)
    {
        if (t == null || string.IsNullOrEmpty(s) || maxWidth <= 0f) return s ?? "";
        if (maxLines <= 1)
        {
            string one = s.IndexOf('<') >= 0 ? StripTags(s) : s;
            if (t.GetPreferredValues(s, 100000f, 0f).x <= maxWidth) return s;
            return Fit(t, one, maxWidth);
        }
        if (LineCount(t, s, maxWidth) <= maxLines) return s;
        string p = s.IndexOf('<') >= 0 ? StripTags(s) : s;
        int lo = 0, hi = p.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (LineCount(t, p.Substring(0, mid).TrimEnd() + "…", maxWidth) <= maxLines) lo = mid;
            else hi = mid - 1;
        }
        if (lo <= 0) return "…";
        // prefer a word boundary if one is close
        int cut = lo;
        int sp = p.LastIndexOf(' ', Mathf.Max(0, lo - 1));
        if (sp > 0 && lo - sp <= 14) cut = sp;
        return p.Substring(0, cut).TrimEnd(' ', ',', '·', '-', ':') + "…";
    }

    /// <summary>Rich text without its tags ("&lt;color=#fff&gt;x3&lt;/color&gt;" → "x3").</summary>
    public static string StripTags(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>' && inTag) { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ glyphs and icons (shared with the UI kit)

    /// <summary>The colour-blind freshness shape (● fresh, ◆ late soon, ▲ late, ✖ stinky): sprites, never font glyphs (D10).</summary>
    public static Sprite Glyph(FreshnessStage stage) => UiKit.Sprites.Glyph((int)stage);

    /// <summary>A mouse with the left (0), right (1) or middle (2) button filled.</summary>
    public static Sprite Mouse(int button) => UiKit.Sprites.Mouse(button);

    /// <summary>"E", "Mouse0" → "LMB", "Space", "LeftShift" → "Shift": the label a key cap shows for a KeyCode.</summary>
    public static string KeyName(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.None: return "";
            case KeyCode.Mouse0: return "LMB";
            case KeyCode.Mouse1: return "RMB";
            case KeyCode.Mouse2: return "MMB";
            case KeyCode.Escape: return "Esc";
            case KeyCode.Return: case KeyCode.KeypadEnter: return "Enter";
            case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
            case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
            case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
            case KeyCode.Backspace: return "Bksp";
        }
        if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
        return k.ToString();
    }
}

/// <summary>
/// A HUD key cap (Docs/UIPass.md §8.1 H1, §8.4): "E", "LMB" (a mouse icon), "E/LMB" (two caps), "Hold R" (a HOLD label + cap),
/// "Space". Same look as <see cref="UiKit.KeyCap"/> (cream cap, darker edge, ink letter) but pooled and sized by hand, so the
/// prompt and the hint chips can change keys every frame without layout groups or garbage. <see cref="Set"/> returns the width.
/// </summary>
public sealed class FeelKeyCap
{
    sealed class Cap
    {
        public RectTransform rt;
        public Image mouse;
        public TextMeshProUGUI text;
    }

    public readonly RectTransform Root;
    readonly float height;
    readonly List<Cap> caps = new List<Cap>(2);
    readonly List<TextMeshProUGUI> seps = new List<TextMeshProUGUI>(1);
    TextMeshProUGUI hold;
    string shown;
    float width;

    static readonly Color CapTop = new Color(0.984f, 0.969f, 0.933f);
    static readonly Color CapEdge = new Color(0.561f, 0.522f, 0.463f);
    static readonly Color Ink = new Color(0.12f, 0.1f, 0.18f);

    public FeelKeyCap(Transform parent, float height = 40f)
    {
        this.height = height;
        Root = FeelUi.Rect("KeyCap", parent, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(height, height));
    }

    public float Width => width;
    public float Height => height;

    /// <summary>Show <paramref name="key"/> (no-op if unchanged). Returns the total width in reference px.</summary>
    public float Set(string key)
    {
        key = (key ?? "").Trim();
        if (key == shown) return width;
        shown = key;
        string k = key;
        float x = 0f;
        bool isHold = k.StartsWith("Hold ", StringComparison.OrdinalIgnoreCase) || k.StartsWith("hold ", StringComparison.Ordinal);
        if (isHold)
        {
            if (hold == null)
            {
                hold = FeelUi.Text(Root, "Hold", 18f, new Color(0.88f, 0.86f, 0.95f), TextAlignmentOptions.MidlineLeft);
                Anchor(hold.rectTransform);
            }
            hold.gameObject.SetActive(true);
            hold.text = "HOLD";
            float hw = hold.GetPreferredValues("HOLD").x + 2f;
            hold.rectTransform.sizeDelta = new Vector2(hw, height);
            hold.rectTransform.anchoredPosition = new Vector2(0f, 0f);
            x = hw + 6f;
            k = k.Substring(5).Trim();
        }
        else if (hold != null) hold.gameObject.SetActive(false);

        int ci = 0, si = 0, start = 0;
        for (int i = 0; i <= k.Length; i++)
        {
            if (i < k.Length && k[i] != '/' && k[i] != '+') continue;
            string part = k.Substring(start, i - start).Trim();
            if (part.Length > 0) x += SetCap(ci++, part, x) + 4f;
            if (i < k.Length && ci > 0)
            {
                var s = Sep(si++);
                s.text = k[i].ToString();
                s.rectTransform.anchoredPosition = new Vector2(x, 0f);
                s.rectTransform.sizeDelta = new Vector2(12f, height);
                x += 16f;
            }
            start = i + 1;
        }
        for (int i = ci; i < caps.Count; i++) caps[i].rt.gameObject.SetActive(false);
        for (int i = si; i < seps.Count; i++) seps[i].gameObject.SetActive(false);
        width = Mathf.Max(0f, x - 4f);
        Root.sizeDelta = new Vector2(width, height);
        return width;
    }

    void Anchor(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
    }

    TextMeshProUGUI Sep(int i)
    {
        while (seps.Count <= i)
        {
            var t = FeelUi.Text(Root, "Sep", 22f, Color.white, TextAlignmentOptions.Center);
            Anchor(t.rectTransform);
            seps.Add(t);
        }
        seps[i].gameObject.SetActive(true);
        return seps[i];
    }

    float SetCap(int i, string k, float x)
    {
        while (caps.Count <= i) caps.Add(MakeCap());
        var c = caps[i];
        c.rt.gameObject.SetActive(true);
        int mouse = k == "LMB" ? 0 : k == "RMB" ? 1 : k == "MMB" ? 2 : -1;
        float w;
        if (mouse >= 0)
        {
            c.mouse.gameObject.SetActive(true);
            c.mouse.sprite = FeelUi.Mouse(mouse);
            c.text.gameObject.SetActive(false);
            w = height;
        }
        else
        {
            c.mouse.gameObject.SetActive(false);
            c.text.gameObject.SetActive(true);
            c.text.fontSize = k.Length <= 1 ? Mathf.Max(18f, height * 0.55f) : Mathf.Max(18f, height * 0.46f);
            c.text.text = k;
            w = Mathf.Max(height, c.text.GetPreferredValues(k).x + 20f);
        }
        c.rt.anchoredPosition = new Vector2(x, 2f);
        c.rt.sizeDelta = new Vector2(w, height - 4f);
        return w;
    }

    Cap MakeCap()
    {
        var c = new Cap();
        c.rt = FeelUi.Rect("Cap", Root, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(height, height));
        var edge = FeelUi.Img(c.rt, "Edge", FeelUi.Rounded, CapEdge, 0.3f);
        edge.rectTransform.offsetMin = new Vector2(0f, -4f);
        edge.rectTransform.offsetMax = new Vector2(0f, -4f);
        FeelUi.Img(c.rt, "Top", FeelUi.Rounded, CapTop, 0.3f);
        var line = FeelUi.Img(c.rt, "Line", UiKit.Sprites.RoundedOutline, new Color(Ink.r, Ink.g, Ink.b, 0.6f), 0.3f);
        line.rectTransform.offsetMin = new Vector2(0f, -4f);
        var mrt = FeelUi.Rect("Mouse", c.rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(height * 0.6f, height * 0.78f));
        c.mouse = mrt.gameObject.AddComponent<Image>();
        c.mouse.color = Ink;
        c.mouse.raycastTarget = false;
        c.text = FeelUi.Text(c.rt, "Text", 22f, Ink, TextAlignmentOptions.Center);
        var mat = UiTheme.InkMaterial(false);
        if (mat != null) c.text.fontSharedMaterial = mat;
        return c;
    }
}

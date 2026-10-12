using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The pictures the 3D casino props wear (Docs/Casino.md §11), painted at run time into a few small shared textures (no asset
/// files, everything DontSave): the card face atlas (52 faces, the back, a blank), the chip atlas (blackjack denominations and
/// the four roulette owner colours with the amount printed on the top), and the slot drum strips. Cartoon style: thick ink on
/// cream, a single-stroke font (no TextMeshPro, so nothing can dirty the font atlas). Pure drawing; touches no scene objects.
/// <para>The signed-distance painter and the slot symbol art live in <c>CasinoPaint.Art.cs</c> (copied from the retired uGUI art).</para>
/// </summary>
public static partial class CasinoPaint
{
    // ------------------------------------------------------------------ palette

    public static Color Hex(int rgb, float a = 1f) =>
        new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    public static readonly Color Cream = Hex(0xF8F1DE), CardRed = Hex(0xC8202B), CardBlack = Hex(0x1B1B22), CardBack = Hex(0x8E1B24);
    public static readonly Color Brass = Hex(0xC9A227), BrassLight = Hex(0xF4DC82), Win = Hex(0x7BF08A), Lose = Hex(0xFF7A70), Ink = Hex(0x15121C);

    /// <summary>Chip colour by denomination in cents (the same palette as the chip racks the room was built with).</summary>
    public static Color ChipColour(int cents)
    {
        if (cents >= 10000) return new Color(0.12f, 0.12f, 0.15f);
        if (cents >= 5000) return new Color(0.88f, 0.48f, 0.10f);
        if (cents >= 2500) return new Color(0.12f, 0.54f, 0.29f);
        if (cents >= 1000) return new Color(0.14f, 0.34f, 0.77f);
        if (cents >= 500) return new Color(0.78f, 0.125f, 0.17f);
        return new Color(0.93f, 0.90f, 0.84f);
    }

    /// <summary>Roulette chips take the colour of their owner (the participant's colour index 0..3): blue, orange, purple, yellow. None of them is the felt's red, black or green.</summary>
    public static readonly Color[] OwnerColours =
    {
        new Color(0.20f, 0.45f, 0.95f), new Color(0.96f, 0.52f, 0.12f), new Color(0.64f, 0.32f, 0.88f), new Color(0.98f, 0.82f, 0.15f),
    };

    public static Color OwnerColour(int owner) => OwnerColours[((owner % OwnerColours.Length) + OwnerColours.Length) % OwnerColours.Length];

    // ------------------------------------------------------------------ the canvas: a pixel buffer painted in card-width units

    /// <summary>A pixel buffer (y up) that shapes are composited onto "over"; coordinates are in units of the canvas WIDTH, origin at the lower left.</summary>
    sealed class Canvas
    {
        public readonly int w, h;
        public readonly Color[] px;
        /// <summary>Everything drawn while this is set lands rotated by 180 degrees (the card's second corner index).</summary>
        public bool rot180;

        public Canvas(int w, int h, Color bg)
        {
            this.w = w; this.h = h;
            px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = bg;
        }

        public float Height => h / (float)w;

        void Blend(int x, int y, Color c, float a)
        {
            if (rot180) { x = w - 1 - x; y = h - 1 - y; }
            int i = y * w + x;
            Color dst = px[i];
            float oa = a + dst.a * (1f - a);
            if (oa <= 0f) return;
            px[i] = new Color((c.r * a + dst.r * dst.a * (1f - a)) / oa, (c.g * a + dst.g * dst.a * (1f - a)) / oa, (c.b * a + dst.b * dst.a * (1f - a)) / oa, oa);
        }

        /// <summary>Composite a shape (signed distance in canvas units) inside the unit-space box (u0, v0)-(u1, v1).</summary>
        public void Fill(Func<float, float, float> sdf, Color c, float u0, float v0, float u1, float v1, float grow = 0f)
        {
            float s = w;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((u0 - grow) * s) - 1), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((u1 + grow) * s) + 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt((v0 - grow) * s) - 1), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((v1 + grow) * s) + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = (sdf((x + 0.5f) / s, (y + 0.5f) / s) - grow) * s;
                    float a = Mathf.Clamp01(0.5f - d) * c.a;
                    if (a > 0f) Blend(x, y, c, a);
                }
        }

        public void Disc(float cx, float cy, float r, Color c) =>
            Fill(Painter.Circle(cx, cy, r), c, cx - r, cy - r, cx + r, cy + r);

        public void Ring(float cx, float cy, float r, float halfWidth, Color c) =>
            Fill(Painter.Ring(cx, cy, r, halfWidth), c, cx - r - halfWidth, cy - r - halfWidth, cx + r + halfWidth, cy + r + halfWidth);

        public void RoundBox(float cx, float cy, float hw, float hh, float round, Color c, float rotDeg = 0f) =>
            Fill(Painter.Box(cx, cy, hw, hh, round, rotDeg), c, cx - Mathf.Max(hw, hh) * 1.45f, cy - Mathf.Max(hw, hh) * 1.45f, cx + Mathf.Max(hw, hh) * 1.45f, cy + Mathf.Max(hw, hh) * 1.45f);

        public void Seg(float ax, float ay, float bx, float by, float hw, Color c) =>
            Fill(Painter.Seg(ax, ay, bx, by, hw), c, Mathf.Min(ax, bx) - hw, Mathf.Min(ay, by) - hw, Mathf.Max(ax, bx) + hw, Mathf.Max(ay, by) + hw);

        public void Polygon(float[] xy, Color c)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < xy.Length; i += 2) { x0 = Mathf.Min(x0, xy[i]); x1 = Mathf.Max(x1, xy[i]); y0 = Mathf.Min(y0, xy[i + 1]); y1 = Mathf.Max(y1, xy[i + 1]); }
            Fill(Painter.Poly(xy), c, x0, y0, x1, y1);
        }

        /// <summary>A single-stroke glyph: <paramref name="x"/>, <paramref name="y"/> is its lower left, <paramref name="height"/> its height.</summary>
        public void Glyph(char ch, float x, float y, float height, Color c)
        {
            if (!Strokes.TryGetValue(ch, out var lines)) return;
            float wd = height * GlyphAspect, hw = height * 0.085f;
            for (int l = 0; l < lines.Length; l++)
            {
                var p = lines[l];
                for (int i = 0; i + 3 < p.Length; i += 2)
                    Seg(x + p[i] * wd, y + p[i + 1] * height, x + p[i + 2] * wd, y + p[i + 3] * height, hw, c);
            }
        }

        public static float TextWidth(string s, float height) => s.Length * height * GlyphAdvance;

        /// <summary>Draw a string; <paramref name="x"/> is the left of the first glyph, <paramref name="y"/> its baseline.</summary>
        public void Text(string s, float x, float y, float height, Color c)
        {
            for (int i = 0; i < s.Length; i++) Glyph(s[i], x + i * height * GlyphAdvance, y, height, c);
        }

        /// <summary>Draw a string centred on (cx, cy).</summary>
        public void TextCentred(string s, float cx, float cy, float height, Color c) =>
            Text(s, cx - (TextWidth(s, height) - height * (GlyphAdvance - GlyphAspect)) * 0.5f, cy - height * 0.5f, height, c);

        /// <summary>Bilinear-sampled blit of a square pixel array (alpha-over, tinted) centred on (cx, cy), <paramref name="size"/> units wide.</summary>
        public void Blit(Color[] src, int srcSize, float cx, float cy, float size, Color tint, bool flip180 = false)
        {
            float s = w;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((cx - size * 0.5f) * s)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((cx + size * 0.5f) * s));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((cy - size * 0.5f) * s)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((cy + size * 0.5f) * s));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float fu = ((x + 0.5f) / s - (cx - size * 0.5f)) / size, fv = ((y + 0.5f) / s - (cy - size * 0.5f)) / size;
                    if (fu < 0f || fu > 1f || fv < 0f || fv > 1f) continue;
                    if (flip180) { fu = 1f - fu; fv = 1f - fv; }
                    float sx = fu * (srcSize - 1), sy = fv * (srcSize - 1);
                    int ix = Mathf.Min(srcSize - 2, (int)sx), iy = Mathf.Min(srcSize - 2, (int)sy);
                    float tx = sx - ix, ty = sy - iy;
                    float a = Mathf.Lerp(Mathf.Lerp(src[iy * srcSize + ix].a, src[iy * srcSize + ix + 1].a, tx),
                                         Mathf.Lerp(src[(iy + 1) * srcSize + ix].a, src[(iy + 1) * srcSize + ix + 1].a, tx), ty) * tint.a;
                    if (a > 0.003f) Blend(x, y, tint, a);
                }
        }
    }

    // ------------------------------------------------------------------ the single-stroke font (digits, A J Q K, $)

    const float GlyphAspect = 0.62f, GlyphAdvance = 0.74f;

    static float[] L(params float[] xy) => xy;

    static readonly Dictionary<char, float[][]> Strokes = BuildStrokes();

    static Dictionary<char, float[][]> BuildStrokes()
    {
        var d = new Dictionary<char, float[][]>
        {
            ['A'] = new[] { L(0.05f, 0f, 0.5f, 1f, 0.95f, 0f), L(0.22f, 0.36f, 0.78f, 0.36f) },
            ['K'] = new[] { L(0.12f, 0f, 0.12f, 1f), L(0.9f, 1f, 0.12f, 0.42f), L(0.38f, 0.62f, 0.94f, 0f) },
            ['Q'] = new[] { L(0.5f, 1f, 0.15f, 0.82f, 0.08f, 0.5f, 0.15f, 0.18f, 0.5f, 0f, 0.85f, 0.18f, 0.92f, 0.5f, 0.85f, 0.82f, 0.5f, 1f), L(0.58f, 0.28f, 0.98f, -0.04f) },
            ['J'] = new[] { L(0.8f, 1f, 0.8f, 0.25f, 0.62f, 0.04f, 0.34f, 0.04f, 0.14f, 0.22f) },
            ['0'] = new[] { L(0.5f, 1f, 0.18f, 0.84f, 0.1f, 0.5f, 0.18f, 0.16f, 0.5f, 0f, 0.82f, 0.16f, 0.9f, 0.5f, 0.82f, 0.84f, 0.5f, 1f) },
            ['1'] = new[] { L(0.2f, 0.78f, 0.58f, 1f, 0.58f, 0f), L(0.22f, 0f, 0.94f, 0f) },
            ['2'] = new[] { L(0.08f, 0.78f, 0.25f, 0.95f, 0.5f, 1f, 0.78f, 0.93f, 0.9f, 0.72f, 0.78f, 0.5f, 0.08f, 0f, 0.95f, 0f) },
            ['3'] = new[] { L(0.08f, 0.9f, 0.5f, 1f, 0.9f, 0.84f, 0.82f, 0.6f, 0.42f, 0.52f), L(0.42f, 0.52f, 0.9f, 0.42f, 0.94f, 0.16f, 0.5f, 0f, 0.06f, 0.1f) },
            ['4'] = new[] { L(0.74f, 0f, 0.74f, 1f, 0.04f, 0.3f, 0.96f, 0.3f) },
            ['5'] = new[] { L(0.9f, 1f, 0.18f, 1f, 0.1f, 0.55f, 0.5f, 0.62f, 0.88f, 0.46f, 0.94f, 0.2f, 0.55f, 0f, 0.06f, 0.1f) },
            ['6'] = new[] { L(0.88f, 0.94f, 0.5f, 1f, 0.14f, 0.7f, 0.06f, 0.3f, 0.3f, 0.02f, 0.72f, 0.02f, 0.94f, 0.26f, 0.82f, 0.52f, 0.42f, 0.56f, 0.1f, 0.38f) },
            ['7'] = new[] { L(0.06f, 1f, 0.94f, 1f, 0.36f, 0f) },
            ['8'] = new[] { L(0.5f, 0.52f, 0.12f, 0.74f, 0.26f, 0.98f, 0.74f, 0.98f, 0.88f, 0.74f, 0.5f, 0.52f, 0.06f, 0.28f, 0.22f, 0.02f, 0.78f, 0.02f, 0.94f, 0.28f, 0.5f, 0.52f) },
        };
        var six = d['6'];
        var nine = new float[six.Length][];
        for (int i = 0; i < six.Length; i++)
        {
            nine[i] = new float[six[i].Length];
            for (int k = 0; k < six[i].Length; k += 2) { nine[i][k] = 1f - six[i][k]; nine[i][k + 1] = 1f - six[i][k + 1]; }
        }
        d['9'] = nine;
        return d;
    }

    static readonly string[] RankText = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    /// <summary>The printed rank of a card byte ("A", "2" .. "10", "J", "Q", "K").</summary>
    public static string RankOf(int card) => RankText[((card % 13) + 13) % 13];

    // ------------------------------------------------------------------ suits

    static readonly Color[][] suitPixels = new Color[4][];
    const int SuitSize = 96;

    /// <summary>A white suit pip (alpha shape) on a 96 x 96 pixel array: 0 spade, 1 heart, 2 diamond, 3 club.</summary>
    static Color[] SuitPixels(int suit)
    {
        suit = ((suit % 4) + 4) % 4;
        if (suitPixels[suit] != null) return suitPixels[suit];
        var p = new Painter(SuitSize) { outline = 0f };
        Color w = Color.white;
        switch (suit)
        {
            case 0: // spade
                p.Shape(Circle(-0.3f, -0.12f, 0.33f), w);
                p.Shape(Circle(0.3f, -0.12f, 0.33f), w);
                p.Shape(Poly(-0.6f, -0.02f, 0.6f, -0.02f, 0f, 0.82f), w);
                p.Shape(Poly(0f, 0f, -0.22f, -0.8f, 0.22f, -0.8f), w);
                break;
            case 1: // heart
                p.Shape(Circle(-0.3f, 0.26f, 0.34f), w);
                p.Shape(Circle(0.3f, 0.26f, 0.34f), w);
                p.Shape(Poly(-0.62f, 0.14f, 0.62f, 0.14f, 0f, -0.78f), w);
                break;
            case 2: // diamond
                p.Shape(Poly(0f, 0.84f, 0.56f, 0f, 0f, -0.84f, -0.56f, 0f), w);
                break;
            default: // club
                p.Shape(Circle(0f, 0.36f, 0.31f), w);
                p.Shape(Circle(-0.35f, -0.1f, 0.31f), w);
                p.Shape(Circle(0.35f, -0.1f, 0.31f), w);
                p.Shape(Circle(0f, 0.02f, 0.2f), w);
                p.Shape(Poly(0f, 0.05f, -0.2f, -0.8f, 0.2f, -0.8f), w);
                break;
        }
        return suitPixels[suit] = p.Pixels;
    }

    public static Color SuitColour(int suit) => suit == 1 || suit == 2 ? CardRed : CardBlack;

    // ------------------------------------------------------------------ the card atlas

    public const int CardCellW = 128, CardCellH = 180, CardCols = 13, CardRows = 5;
    /// <summary>Atlas cell of the back design and of a plain cream cell (used by the card's edges).</summary>
    public const int CardBackCell = 52, CardBlankCell = 53;

    static Texture2D cardAtlas;

    /// <summary>The 13 x 5 atlas: cell c = rank + 13 * suit for the 52 faces, 52 = the back, 53 = blank paper.</summary>
    public static Texture2D CardAtlas
    {
        get
        {
            if (cardAtlas != null) return cardAtlas;
            int aw = CardCols * CardCellW, ah = CardRows * CardCellH;
            var data = new Color32[aw * ah];
            for (int cell = 0; cell <= CardBlankCell; cell++)
            {
                Canvas cv;
                if (cell == CardBackCell) cv = PaintCardBack();
                else if (cell == CardBlankCell) cv = new Canvas(CardCellW, CardCellH, new Color(0.96f, 0.93f, 0.85f, 1f));
                else cv = PaintCardFace(cell);
                int cx = cell % CardCols * CardCellW, cy = cell / CardCols * CardCellH;
                for (int y = 0; y < CardCellH; y++)
                    for (int x = 0; x < CardCellW; x++)
                    {
                        Color c = cv.px[y * CardCellW + x];
                        data[(cy + y) * aw + cx + x] = new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), 255);
                    }
            }
            // the unused cells of the last row: paper
            for (int cell = CardBlankCell + 1; cell < CardCols * CardRows; cell++)
            {
                int cx = cell % CardCols * CardCellW, cy = cell / CardCols * CardCellH;
                for (int y = 0; y < CardCellH; y++)
                    for (int x = 0; x < CardCellW; x++) data[(cy + y) * aw + cx + x] = new Color32(245, 238, 217, 255);
            }
            var tex = new Texture2D(aw, ah, TextureFormat.RGBA32, true)
            {
                name = "CasinoCards", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontSave,
            };
            tex.SetPixels32(data);
            tex.Apply(true, true);
            return cardAtlas = tex;
        }
    }

    /// <summary>Atlas UV rectangle of a card cell, inset by a texel and a half so mip levels never bleed a neighbour in.</summary>
    public static Rect CardUv(int cell)
    {
        float aw = CardCols * CardCellW, ah = CardRows * CardCellH;
        float x = cell % CardCols * CardCellW + 1.5f, y = cell / CardCols * CardCellH + 1.5f;
        return new Rect(x / aw, y / ah, (CardCellW - 3f) / aw, (CardCellH - 3f) / ah);
    }

    static Canvas PaintCardFace(int card)
    {
        int rank = card % 13, suit = card / 13 % 4;
        Color ink = SuitColour(suit);
        var cv = new Canvas(CardCellW, CardCellH, new Color(0.985f, 0.965f, 0.90f, 1f));
        float H = cv.Height;
        // a thin printed border
        Func<float, float, float> frame = Painter.Box(0.5f, H * 0.5f, 0.5f - 0.03f, H * 0.5f - 0.03f, 0.07f);
        cv.Fill((x, y) => Mathf.Abs(frame(x, y)) - 0.006f, new Color(0.62f, 0.55f, 0.46f, 1f), 0f, 0f, 1f, H);
        var pip = SuitPixels(suit);
        string rankText = RankText[rank];
        for (int side = 0; side < 2; side++)
        {
            cv.rot180 = side == 1;
            cv.Text(rankText, rank == 9 ? 0.05f : 0.09f, H - 0.235f, 0.19f, ink);
            cv.Blit(pip, SuitSize, 0.135f, H - 0.31f, 0.13f, ink);
        }
        cv.rot180 = false;
        if (rank >= 10)
        {
            // a court card: a framed panel, a big letter, a crown or a jester's cap, a pip below
            cv.Fill(Painter.Box(0.5f, H * 0.5f, 0.27f, H * 0.5f - 0.25f, 0.05f), new Color(ink.r, ink.g, ink.b, 0.09f), 0.2f, 0.2f, 0.8f, H - 0.2f);
            Func<float, float, float> panel = Painter.Box(0.5f, H * 0.5f, 0.27f, H * 0.5f - 0.25f, 0.05f);
            cv.Fill((x, y) => Mathf.Abs(panel(x, y)) - 0.008f, new Color(ink.r, ink.g, ink.b, 0.55f), 0.2f, 0.2f, 0.8f, H - 0.2f);
            cv.TextCentred(rankText, 0.5f, H * 0.5f - 0.02f, 0.44f, ink);
            if (rank == 12 || rank == 11)
            {
                float by = H - 0.43f;
                cv.Polygon(new[] { 0.36f, by, 0.36f, by + 0.07f, 0.43f, by + 0.035f, 0.5f, by + 0.09f, 0.57f, by + 0.035f, 0.64f, by + 0.07f, 0.64f, by }, Hex(0xF2C230));
            }
            else cv.Polygon(new[] { 0.38f, H - 0.43f, 0.5f, H - 0.33f, 0.62f, H - 0.43f }, CardRed);
            cv.Blit(pip, SuitSize, 0.5f, 0.43f, 0.12f, ink, true);
            return cv;
        }
        if (rank == 0) { cv.Blit(pip, SuitSize, 0.5f, H * 0.5f, 0.44f, ink); return cv; }
        float lo = 0.30f, span = H - 0.60f, pipSize = rank >= 8 ? 0.17f : 0.19f;
        foreach (var at in PipLayout(rank))
        {
            float py = lo + at.y * span;
            cv.Blit(pip, SuitSize, 0.5f + at.x * 0.205f, py, pipSize, ink, at.y < 0.499f);
        }
        return cv;
    }

    static IEnumerable<Vector2> PipLayout(int rank)
    {
        switch (rank)
        {
            case 1: yield return new Vector2(0, 0); yield return new Vector2(0, 1); break;
            case 2: yield return new Vector2(0, 0); yield return new Vector2(0, 0.5f); yield return new Vector2(0, 1); break;
            case 3: foreach (var v in Cols(0, 1)) yield return v; break;
            case 4: foreach (var v in Cols(0, 1)) yield return v; yield return new Vector2(0, 0.5f); break;
            case 5: foreach (var v in Cols(0, 0.5f, 1)) yield return v; break;
            case 6: foreach (var v in Cols(0, 0.5f, 1)) yield return v; yield return new Vector2(0, 0.75f); break;
            case 7: foreach (var v in Cols(0, 0.5f, 1)) yield return v; yield return new Vector2(0, 0.75f); yield return new Vector2(0, 0.25f); break;
            case 8: foreach (var v in Cols(0, 1f / 3f, 2f / 3f, 1)) yield return v; yield return new Vector2(0, 0.5f); break;
            case 9: foreach (var v in Cols(0, 1f / 3f, 2f / 3f, 1)) yield return v; yield return new Vector2(0, 0.8333f); yield return new Vector2(0, 0.1667f); break;
        }
    }

    static IEnumerable<Vector2> Cols(params float[] ys)
    {
        foreach (float y in ys) { yield return new Vector2(-1f, y); yield return new Vector2(1f, y); }
    }

    static Canvas PaintCardBack()
    {
        var cv = new Canvas(CardCellW, CardCellH, Cream);
        float H = cv.Height;
        cv.RoundBox(0.5f, H * 0.5f, 0.5f - 0.055f, H * 0.5f - 0.055f, 0.06f, CardBack);
        // the diamond lattice
        for (int y = 0; y < CardCellH; y++)
            for (int x = 0; x < CardCellW; x++)
            {
                float u = (x + 0.5f) / CardCellW, v = (y + 0.5f) / CardCellW;
                if (u < 0.1f || u > 0.9f || v < 0.1f || v > H - 0.1f) continue;
                float a = (u + v) * 6.5f, b = (u - v) * 6.5f;
                float da = Mathf.Abs(a - Mathf.Round(a)), db = Mathf.Abs(b - Mathf.Round(b));
                if (Mathf.Min(da, db) < 0.085f) cv.px[y * CardCellW + x] = Color.Lerp(cv.px[y * CardCellW + x], Hex(0xD8606A), 0.55f);
            }
        cv.Disc(0.5f, H * 0.5f, 0.2f, Hex(0xF2C230));
        cv.Disc(0.5f, H * 0.5f, 0.17f, Hex(0x5E1018));
        var mask = SymbolPixels(SlotRules.TrashPandaBandit, 5);
        cv.Blit(mask, DrumCell, 0.5f, H * 0.5f, 0.27f, Color.white);
        return cv;
    }

    // ------------------------------------------------------------------ the chip atlas

    public const int ChipCellW = 96, ChipCellH = 112, ChipSide = 16, ChipCols = 6, ChipRows = 4;
    /// <summary>Blackjack denominations (colour by value) take styles 0..5; roulette chips are 6 + owner * 4 + denomination.</summary>
    public const int ChipStyles = 22;
    public static readonly int[] BlackjackDenoms = { 100, 500, 1000, 2500, 5000, 10000 };
    public static readonly int[] RouletteDenoms = { 100, 500, 2500, 10000 };

    static Texture2D chipAtlas;

    public static int BlackjackStyle(int cents)
    {
        int best = 0;
        for (int i = 0; i < BlackjackDenoms.Length; i++) if (cents >= BlackjackDenoms[i]) best = i;
        return best;
    }

    public static int RouletteStyle(int owner, int cents)
    {
        int d = 0;
        for (int i = 0; i < RouletteDenoms.Length; i++) if (cents >= RouletteDenoms[i]) d = i;
        return 6 + Mathf.Clamp(owner, 0, 3) * 4 + d;
    }

    /// <summary>The denomination (cents) a chip style is printed with.</summary>
    public static int StyleCents(int style) => style < 6 ? BlackjackDenoms[Mathf.Clamp(style, 0, 5)] : RouletteDenoms[Mathf.Clamp((style - 6) % 4, 0, 3)];

    public static Texture2D ChipAtlas
    {
        get
        {
            if (chipAtlas != null) return chipAtlas;
            int aw = ChipCols * ChipCellW, ah = ChipRows * ChipCellH;
            var data = new Color32[aw * ah];
            for (int style = 0; style < ChipCols * ChipRows; style++)
            {
                int cents = style < ChipStyles ? StyleCents(style) : 100;
                Color body = style < 6 ? ChipColour(cents) : OwnerColour((style - 6) / 4);
                Color insert = style < 6 && cents == 100 ? Hex(0x2456C4) : Cream;
                var top = PaintChipTop(body, insert, cents);
                int cx = style % ChipCols * ChipCellW, cy = style / ChipCols * ChipCellH;
                for (int y = 0; y < ChipCellH; y++)
                    for (int x = 0; x < ChipCellW; x++)
                    {
                        Color c;
                        if (y >= ChipSide) c = top.px[(y - ChipSide) * ChipCellW + x];
                        else c = (x / 8) % 2 == 0 ? insert : Color.Lerp(body, Color.black, 0.12f); // the rim: six inserts round the edge
                        data[(cy + y) * aw + cx + x] = new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), 255);
                    }
            }
            var tex = new Texture2D(aw, ah, TextureFormat.RGBA32, true)
            {
                name = "CasinoChips", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontSave,
            };
            tex.SetPixels32(data);
            tex.Apply(true, true);
            return chipAtlas = tex;
        }
    }

    /// <summary>UV rectangle of a chip style's top (the disc) and rim stripe.</summary>
    public static Rect ChipTopUv(int style)
    {
        float aw = ChipCols * ChipCellW, ah = ChipRows * ChipCellH;
        float x = style % ChipCols * ChipCellW, y = style / ChipCols * ChipCellH + ChipSide;
        return new Rect((x + 1f) / aw, (y + 1f) / ah, (ChipCellW - 2f) / aw, (ChipCellH - ChipSide - 2f) / ah);
    }

    public static Rect ChipRimUv(int style)
    {
        float aw = ChipCols * ChipCellW, ah = ChipRows * ChipCellH;
        float x = style % ChipCols * ChipCellW, y = style / ChipCols * ChipCellH;
        return new Rect(x / aw, (y + 4f) / ah, ChipCellW / (float)aw, (ChipSide - 8f) / ah);
    }

    static Canvas PaintChipTop(Color body, Color insert, int cents)
    {
        var cv = new Canvas(ChipCellW, ChipCellW, new Color(0f, 0f, 0f, 0f));
        Color edge = Color.Lerp(body, Color.black, 0.3f);
        cv.Disc(0.5f, 0.5f, 0.5f, edge);
        cv.Disc(0.5f, 0.5f, 0.485f, body);
        for (int i = 0; i < 6; i++)
        {
            float a = (i * 60f + 30f) * Mathf.Deg2Rad;
            cv.RoundBox(0.5f + Mathf.Cos(a) * 0.42f, 0.5f + Mathf.Sin(a) * 0.42f, 0.10f, 0.075f, 0.02f, insert, i * 60f + 120f);
        }
        cv.Ring(0.5f, 0.5f, 0.33f, 0.014f, insert);
        cv.Disc(0.5f, 0.5f, 0.30f, Color.Lerp(body, Color.white, 0.12f));
        cv.Disc(0.5f, 0.5f, 0.26f, insert);
        string label = (cents / 100).ToString();
        float th = label.Length >= 3 ? 0.22f : 0.3f;
        Color ink = Color.Lerp(body, Color.black, 0.45f);
        cv.TextCentred(label, 0.5f, 0.5f, th, ink);
        // the rim colour of the corners outside the disc stays transparent: paint it as the edge so mips don't go dark
        for (int i = 0; i < cv.px.Length; i++) if (cv.px[i].a < 0.5f) cv.px[i] = edge;
        return cv;
    }

    // ------------------------------------------------------------------ slot symbols and drums

    public const int DrumCell = 64;
    static readonly Dictionary<int, Color[]> drumSymbols = new Dictionary<int, Color[]>();
    static readonly Dictionary<int, Texture2D> drumTextures = new Dictionary<int, Texture2D>();
    static readonly Color DrumPaper = new Color(0.96f, 0.95f, 0.9f, 1f);
    static MaterialPropertyBlock drumBlock;

    /// <summary>One slot symbol (machine, symbol id) as a 64 x 64 pixel array (the blank is empty). Cached.</summary>
    public static Color[] SymbolPixels(int machine, int symbol)
    {
        int key = machine * 16 + symbol;
        if (drumSymbols.TryGetValue(key, out var px)) return px;
        var p = new Painter(DrumCell);
        if (symbol > 0)
        {
            try { PaintSymbol(p, machine, symbol); }
            catch (Exception e) { Debug.LogException(e); }
        }
        return drumSymbols[key] = p.Pixels;
    }

    /// <summary>Which drum face carries strip stop <c>stop + j</c> when the reel rests at <paramref name="stop"/> (the QA'd mapping: face (-s) mod 12 shows stop s).</summary>
    public static int DrumFace(int stop, int j)
    {
        int faces = CasinoLayout.ReelFaces;
        return ((-(stop + j)) % faces + faces) % faces;
    }

    /// <summary>
    /// The strip texture for one drum, painted so the faces that will be in the window when the reel rests at <paramref name="stop"/>
    /// carry the strip's REAL symbols (<see cref="SlotRules.SymbolAt"/>). One column of cells, face 0 at the top. Cached per machine and reel.
    /// </summary>
    public static Texture2D ReelTexture(int machine, int reel, int stop)
    {
        int faces = CasinoLayout.ReelFaces, key = machine * 4 + reel;
        if (!drumTextures.TryGetValue(key, out var tex) || tex == null)
        {
            tex = new Texture2D(DrumCell, DrumCell * faces, TextureFormat.RGBA32, false)
            {
                name = "CasinoDrum_" + machine + "_" + reel, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            drumTextures[key] = tex;
        }
        var px = new Color[DrumCell * DrumCell * faces];
        for (int j = -(faces / 2 - 1); j <= faces / 2; j++)
        {
            int face = DrumFace(stop, j);
            var icon = SymbolPixels(machine, SlotRules.SymbolAt(machine, reel, stop, j));
            int y0 = (faces - 1 - face) * DrumCell;
            for (int y = 0; y < DrumCell; y++)
                for (int x = 0; x < DrumCell; x++)
                {
                    // the icon sits in the middle 80% of the cell, on paper that darkens towards the drum's edges
                    float sx = (x - DrumCell * 0.1f) / 0.8f, sy = (y - DrumCell * 0.1f) / 0.8f;
                    Color c = DrumPaper;
                    if (sx >= 0f && sy >= 0f && sx < DrumCell && sy < DrumCell)
                    {
                        Color i = icon[(int)sy * DrumCell + (int)sx];
                        c = Color.Lerp(DrumPaper, i, i.a);
                    }
                    if (y == 0) c = new Color(0.6f, 0.6f, 0.58f);
                    c *= Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(Mathf.Min(x, DrumCell - 1 - x) / 7f));
                    c.a = 1f;
                    px[(y0 + y) * DrumCell + x] = c;
                }
        }
        tex.SetPixels(px);
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>Put <see cref="ReelTexture"/> on a drum of the room's machine (this machine's view only). The strip material is the one whose name or texture starts with "reel".</summary>
    public static void SkinReel(Transform drum, int machine, int reel, int stop)
    {
        if (drum == null) return;
        try
        {
            var r = drum.GetComponentInChildren<MeshRenderer>(true);
            if (r == null) return;
            var mats = r.sharedMaterials;
            int at = -1;
            for (int i = 0; i < mats.Length && at < 0; i++)
            {
                var m = mats[i];
                if (m == null || !m.HasProperty("_BaseMap")) continue;
                var t = m.GetTexture("_BaseMap");
                if (m.name.StartsWith("reel", StringComparison.OrdinalIgnoreCase) || (t != null && t.name.StartsWith("reel", StringComparison.OrdinalIgnoreCase))) at = i;
            }
            for (int i = 0; i < mats.Length && at < 0; i++)
                if (mats[i] != null && mats[i].HasProperty("_BaseMap") && mats[i].GetTexture("_BaseMap") != null) at = i;
            if (at < 0) return;
            var tex = ReelTexture(machine, reel, stop);
            if (drumBlock == null) drumBlock = new MaterialPropertyBlock();
            r.GetPropertyBlock(drumBlock, at);
            drumBlock.SetTexture("_BaseMap", tex);
            drumBlock.SetTexture("_EmissionMap", tex);
            r.SetPropertyBlock(drumBlock, at);
        }
        catch (Exception e) { Debug.LogWarning("[Casino] drum faces not repainted: " + e.Message); }
    }

    /// <summary>The cabinet colour of a machine (accents).</summary>
    public static Color MachineColour(int machine)
    {
        switch (machine)
        {
            case SlotRules.LuckySevens: return Hex(0xB81F2A);
            case SlotRules.TrashPandaBandit: return Hex(0x4B5A78);
            case SlotRules.GoldenGnome: return Hex(0x2F7D46);
            case SlotRules.DumpsterFire: return Hex(0xC2551A);
            default: return Hex(0x6A3FA0);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // Textures are DontSave objects; a destroyed one compares equal to null, so the atlases rebuild themselves. The caches hold Unity objects too.
        drumTextures.Clear();
        drumSymbols.Clear();
        drumBlock = null;
    }
}

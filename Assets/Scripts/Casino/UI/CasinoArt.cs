using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Lucky Raccoon's look for the game screens (Docs/Casino.md §3; UIPass D1: runtime-built, no asset files): the
/// felt-and-brass palette, and every picture the screens need, painted once into small textures: card suits, chip
/// inserts, the card-back lattice, the bar's glasses and one icon per slot symbol. Everything is DontSave and cached.
/// </summary>
public static class CasinoArt
{
    // ------------------------------------------------------------------ palette

    public static readonly Color Felt = Hex(0x0F5A3C), FeltDark = Hex(0x0A3F2B), FeltLight = Hex(0x1A7A53), FeltLine = Hex(0xE9E2C4);
    public static readonly Color Brass = Hex(0xC9A227), BrassDark = Hex(0x7A5C12), BrassLight = Hex(0xF4DC82);
    public static readonly Color Wood = Hex(0x3B2416), WoodDark = Hex(0x24150C);
    public static readonly Color Cream = Hex(0xF8F1DE), CreamDark = Hex(0xD9CFB4);
    public static readonly Color CardRed = Hex(0xC8202B), CardBlack = Hex(0x1B1B22), CardBack = Hex(0x8E1B24);
    public static readonly Color WheelRed = Hex(0xB81F2A), WheelBlack = Hex(0x17171B), WheelGreen = Hex(0x0E7A3E);
    public static readonly Color Win = Hex(0x7BF08A), Lose = Hex(0xFF7A70), Ink = Hex(0x15121C);

    public static Color Hex(int rgb, float a = 1f) =>
        new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    public static Color PocketColor(int pocket) =>
        RouletteRules.IsRed(pocket) ? WheelRed : RouletteRules.IsBlack(pocket) ? WheelBlack : WheelGreen;

    /// <summary>Chip colours by denomination (cents): white $1, red $5, blue $10, green $25, orange $50, black $100, purple above.</summary>
    public static Color ChipColor(long cents)
    {
        if (cents >= 50000) return Hex(0x6A2FA8);
        if (cents >= 10000) return Hex(0x1E1E26);
        if (cents >= 5000) return Hex(0xE07A1A);
        if (cents >= 2500) return Hex(0x1F8A4A);
        if (cents >= 1000) return Hex(0x2456C4);
        if (cents >= 500) return Hex(0xC8202B);
        return Hex(0xECE6D6);
    }

    /// <summary>Ink for text on a chip of this colour.</summary>
    public static Color ChipInk(long cents) => cents >= 500 ? Color.white : Ink;

    // ------------------------------------------------------------------ the painter

    /// <summary>
    /// A tiny signed-distance painter: shapes in a -1..1 square (y up), each with an optional dark outline, composited
    /// "over" with analytic anti-aliasing. Enough for icons a few centimetres across; never used per frame.
    /// </summary>
    sealed class Painter
    {
        readonly int size;
        readonly Color[] px;
        /// <summary>Outline width in normalised units (0 = none) and its colour.</summary>
        public float outline = 0.07f;
        public Color ink = new Color(0.07f, 0.05f, 0.09f, 1f);

        public Painter(int size) { this.size = size; px = new Color[size * size]; }
        public Color[] Pixels => px;

        void Fill(Func<float, float, float> sdf, Color c, float grow)
        {
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / half - 1f, ny = (y + 0.5f) / half - 1f;
                    float d = (sdf(nx, ny) - grow) * half;
                    float a = Mathf.Clamp01(0.5f - d) * c.a;
                    if (a <= 0f) continue;
                    int i = y * size + x;
                    Color dst = px[i];
                    float oa = a + dst.a * (1f - a);
                    px[i] = new Color((c.r * a + dst.r * dst.a * (1f - a)) / oa, (c.g * a + dst.g * dst.a * (1f - a)) / oa,
                                      (c.b * a + dst.b * dst.a * (1f - a)) / oa, oa);
                }
        }

        public void Shape(Func<float, float, float> sdf, Color c)
        {
            if (outline > 0f) Fill(sdf, ink, outline);
            Fill(sdf, c, 0f);
        }

        /// <summary>The same shape with no outline (details drawn on top of something already outlined).</summary>
        public void Detail(Func<float, float, float> sdf, Color c) => Fill(sdf, c, 0f);

        public static Func<float, float, float> Circle(float cx, float cy, float r) =>
            (x, y) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

        public static Func<float, float, float> Ellipse(float cx, float cy, float rx, float ry) =>
            (x, y) =>
            {
                float px = (x - cx) / rx, py = (y - cy) / ry;
                return (Mathf.Sqrt(px * px + py * py) - 1f) * Mathf.Min(rx, ry);
            };

        public static Func<float, float, float> Ring(float cx, float cy, float r, float halfWidth) =>
            (x, y) => Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) - halfWidth;

        public static Func<float, float, float> Box(float cx, float cy, float hw, float hh, float round = 0f, float rotDeg = 0f)
        {
            float cs = Mathf.Cos(-rotDeg * Mathf.Deg2Rad), sn = Mathf.Sin(-rotDeg * Mathf.Deg2Rad);
            return (x, y) =>
            {
                float dx = x - cx, dy = y - cy;
                float lx = dx * cs - dy * sn, ly = dx * sn + dy * cs;
                float qx = Mathf.Abs(lx) - (hw - round), qy = Mathf.Abs(ly) - (hh - round);
                float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
                return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - round;
            };
        }

        public static Func<float, float, float> Seg(float ax, float ay, float bx, float by, float halfWidth) =>
            (x, y) =>
            {
                float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
                float len = dx * dx + dy * dy;
                float t = len > 1e-9f ? Mathf.Clamp01((px * dx + py * dy) / len) : 0f;
                float ex = px - dx * t, ey = py - dy * t;
                return Mathf.Sqrt(ex * ex + ey * ey) - halfWidth;
            };

        /// <summary>A closed polygon from x,y pairs (any winding, may be concave).</summary>
        public static Func<float, float, float> Poly(params float[] xy)
        {
            int n = xy.Length / 2;
            return (x, y) =>
            {
                float d = float.MaxValue;
                bool inside = false;
                for (int i = 0, j = n - 1; i < n; j = i, i++)
                {
                    float ax = xy[i * 2], ay = xy[i * 2 + 1], bx = xy[j * 2], by = xy[j * 2 + 1];
                    float ex = bx - ax, ey = by - ay, wx = x - ax, wy = y - ay;
                    float len = ex * ex + ey * ey;
                    float t = len > 1e-9f ? Mathf.Clamp01((wx * ex + wy * ey) / len) : 0f;
                    float qx = wx - ex * t, qy = wy - ey * t;
                    d = Mathf.Min(d, qx * qx + qy * qy);
                    if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
                }
                return (inside ? -1f : 1f) * Mathf.Sqrt(d);
            };
        }

        public static Func<float, float, float> Star(float cx, float cy, float outer, float inner, int points, float rotDeg = 0f)
        {
            var xy = new float[points * 4];
            for (int i = 0; i < points * 2; i++)
            {
                float a = (rotDeg + 90f + i * 180f / points) * Mathf.Deg2Rad, r = (i & 1) == 0 ? outer : inner;
                xy[i * 2] = cx + Mathf.Cos(a) * r;
                xy[i * 2 + 1] = cy + Mathf.Sin(a) * r;
            }
            return Poly(xy);
        }

        public Sprite Finish(string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            tex.SetPixels(px);
            tex.Apply(false, true);
            var sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sp.name = name;
            sp.hideFlags = HideFlags.DontSave;
            return sp;
        }
    }

    static Func<float, float, float> Circle(float cx, float cy, float r) => Painter.Circle(cx, cy, r);
    static Func<float, float, float> Ellipse(float cx, float cy, float rx, float ry) => Painter.Ellipse(cx, cy, rx, ry);
    static Func<float, float, float> Ring(float cx, float cy, float r, float hw) => Painter.Ring(cx, cy, r, hw);
    static Func<float, float, float> Box(float cx, float cy, float hw, float hh, float round = 0f, float rot = 0f) => Painter.Box(cx, cy, hw, hh, round, rot);
    static Func<float, float, float> Seg(float ax, float ay, float bx, float by, float hw) => Painter.Seg(ax, ay, bx, by, hw);
    static Func<float, float, float> Poly(params float[] xy) => Painter.Poly(xy);
    static Func<float, float, float> Star(float cx, float cy, float o, float i, int n, float rot = 0f) => Painter.Star(cx, cy, o, i, n, rot);

    // ------------------------------------------------------------------ cards and chips

    static readonly Sprite[] suits = new Sprite[4];
    static Sprite lattice, chipInserts, lever, pointer, glow;

    /// <summary>Suit pips in the rules' order (0 spade, 1 heart, 2 diamond, 3 club): white, tint them.</summary>
    public static Sprite Suit(int suit)
    {
        suit = ((suit % 4) + 4) % 4;
        if (suits[suit] != null) return suits[suit];
        var p = new Painter(96) { outline = 0f };
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
        return suits[suit] = p.Finish("CasinoSuit" + suit);
    }

    public static Color SuitColor(int suit) => suit == 1 || suit == 2 ? CardRed : CardBlack;

    /// <summary>A tileable diamond lattice for card backs (white, low alpha when used).</summary>
    public static Sprite Lattice
    {
        get
        {
            if (lattice != null) return lattice;
            var p = new Painter(32) { outline = 0f };
            p.Shape(Poly(0f, 0.7f, 0.7f, 0f, 0f, -0.7f, -0.7f, 0f), Color.white);
            lattice = p.Finish("CasinoLattice");
            lattice.texture.wrapMode = TextureWrapMode.Repeat;
            return lattice;
        }
    }

    /// <summary>The white edge inserts and inner ring of a casino chip (drawn over a disc tinted with <see cref="ChipColor"/>).</summary>
    public static Sprite ChipInserts
    {
        get
        {
            if (chipInserts != null) return chipInserts;
            var p = new Painter(96) { outline = 0f };
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f * Mathf.Deg2Rad;
                p.Shape(Box(Mathf.Cos(a) * 0.7f, Mathf.Sin(a) * 0.7f, 0.1f, 0.13f, 0.02f, i * 60f + 90f), Color.white);
            }
            p.Shape(Ring(0f, 0f, 0.5f, 0.03f), new Color(1f, 1f, 1f, 0.85f));
            return chipInserts = p.Finish("CasinoChipInserts");
        }
    }

    /// <summary>A slot machine's lever: ball on a stick, pivot at the bottom centre.</summary>
    public static Sprite Lever
    {
        get
        {
            if (lever != null) return lever;
            var p = new Painter(128);
            p.Shape(Seg(0f, -0.85f, 0f, 0.45f, 0.07f), Hex(0xB9BEC6));
            p.Shape(Circle(0f, 0.62f, 0.26f), Hex(0xD3222E));
            p.Detail(Circle(-0.08f, 0.7f, 0.07f), new Color(1f, 1f, 1f, 0.7f));
            return lever = p.Finish("CasinoLever");
        }
    }

    /// <summary>A brass pay-line pointer aimed right.</summary>
    public static Sprite Pointer
    {
        get
        {
            if (pointer != null) return pointer;
            var p = new Painter(64);
            p.Shape(Poly(-0.7f, 0.6f, 0.7f, 0f, -0.7f, -0.6f), Brass);
            return pointer = p.Finish("CasinoPointer");
        }
    }

    /// <summary>A soft round glow (white; tint and scale it).</summary>
    public static Sprite Glow
    {
        get
        {
            if (glow != null) return glow;
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { name = "CasinoGlow", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f, dy = (y + 0.5f) / S * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * S + x] = new Color(1f, 1f, 1f, a * a);
                }
            tex.SetPixels(px);
            tex.Apply(false, true);
            glow = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            glow.hideFlags = HideFlags.DontSave;
            return glow;
        }
    }

    // ------------------------------------------------------------------ the bar

    static readonly Sprite[] drinks = new Sprite[4];

    /// <summary>A glass for each drink on the menu (<see cref="CasinoRules.FlatBeer"/> .. <see cref="CasinoRules.GlassOfWater"/>).</summary>
    public static Sprite DrinkIcon(int drink)
    {
        if (drink < 0 || drink >= drinks.Length) drink = drinks.Length - 1;
        if (drinks[drink] != null) return drinks[drink];
        var p = new Painter(128);
        Color glass = new Color(0.86f, 0.95f, 0.97f, 0.75f);
        switch (drink)
        {
            case CasinoRules.FlatBeer: // a mug, not much head
                p.Shape(Ring(0.5f, -0.05f, 0.26f, 0.07f), glass);
                p.Shape(Box(-0.08f, -0.05f, 0.44f, 0.66f, 0.08f), glass);
                p.Detail(Box(-0.08f, -0.12f, 0.37f, 0.52f, 0.05f), Hex(0xD9941C));
                p.Detail(Box(-0.08f, 0.43f, 0.37f, 0.05f, 0.03f), Hex(0xF3E7C4));
                p.Detail(Seg(-0.3f, 0.2f, -0.3f, -0.45f, 0.035f), new Color(1f, 1f, 1f, 0.45f));
                break;
            case CasinoRules.HouseWhisky: // a tumbler with two fingers and a cube
                p.Shape(Poly(-0.5f, 0.4f, 0.5f, 0.4f, 0.42f, -0.55f, -0.42f, -0.55f), glass);
                p.Detail(Poly(-0.44f, -0.05f, 0.44f, -0.05f, 0.39f, -0.5f, -0.39f, -0.5f), Hex(0xB5651D));
                p.Detail(Box(0.05f, -0.1f, 0.15f, 0.15f, 0.03f, 18f), new Color(1f, 1f, 1f, 0.6f));
                break;
            case CasinoRules.DumpsterFireCocktail: // a coupe on fire
                p.Shape(Seg(0f, -0.1f, 0f, -0.62f, 0.045f), glass);
                p.Shape(Box(0f, -0.68f, 0.3f, 0.05f, 0.04f), glass);
                p.Shape(Poly(-0.58f, 0.32f, 0.58f, 0.32f, 0f, -0.18f), glass);
                p.Detail(Poly(-0.44f, 0.24f, 0.44f, 0.24f, 0f, -0.12f), Hex(0xE2452A));
                p.Shape(Poly(-0.2f, 0.3f, -0.26f, 0.58f, -0.08f, 0.5f, 0.02f, 0.86f, 0.14f, 0.52f, 0.28f, 0.62f, 0.2f, 0.3f), Hex(0xFF8A1E));
                p.Detail(Poly(-0.08f, 0.32f, -0.06f, 0.5f, 0.02f, 0.64f, 0.1f, 0.46f, 0.1f, 0.32f), Hex(0xFFE061));
                break;
            default: // water
                p.Shape(Poly(-0.4f, 0.62f, 0.4f, 0.62f, 0.32f, -0.64f, -0.32f, -0.64f), glass);
                p.Detail(Poly(-0.34f, 0.3f, 0.34f, 0.3f, 0.29f, -0.59f, -0.29f, -0.59f), new Color(0.45f, 0.75f, 0.95f, 0.8f));
                p.Detail(Seg(-0.2f, 0.15f, -0.18f, -0.4f, 0.03f), new Color(1f, 1f, 1f, 0.5f));
                break;
        }
        return drinks[drink] = p.Finish("CasinoDrink" + drink);
    }

    // ------------------------------------------------------------------ slot symbols

    static readonly Dictionary<int, Sprite> symbols = new Dictionary<int, Sprite>();

    /// <summary>The icon for symbol <paramref name="symbol"/> of machine <paramref name="machine"/> (null for the blank).</summary>
    public static Sprite SlotSymbol(int machine, int symbol)
    {
        if (symbol <= 0) return null;
        int key = machine * 16 + symbol;
        if (symbols.TryGetValue(key, out var sp) && sp != null) return sp;
        var p = new Painter(112);
        try { PaintSymbol(p, machine, symbol); }
        catch (Exception e) { Debug.LogException(e); }
        sp = p.Finish("CasinoSymbol_" + machine + "_" + symbol);
        symbols[key] = sp;
        return sp;
    }

    // ------------------------------------------------------------------ the drums of the machine in the room

    const int DrumCell = 64;
    static readonly Dictionary<int, Color[]> drumSymbols = new Dictionary<int, Color[]>();
    static readonly Dictionary<int, Texture2D> drumTextures = new Dictionary<int, Texture2D>();
    static readonly Color DrumPaper = new Color(0.96f, 0.95f, 0.9f, 1f);
    static MaterialPropertyBlock drumBlock;

    static Color[] DrumSymbol(int machine, int symbol)
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

    /// <summary>
    /// The strip texture for one drum of the 3D machine, repainted so that the faces that will be in the window when the
    /// reel rests at <paramref name="stop"/> carry the strip's real symbols: face k shows the stop s with s = -k (mod
    /// <see cref="CasinoLayout.ReelFaces"/>) nearest to the stop, which is exactly how <c>SlotScreen</c> turns the drum.
    /// One column of cells, face 0 at the top, like the baked placeholder it replaces. Cached per machine and reel.
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
            int face = ((-(stop + j)) % faces + faces) % faces;
            var icon = DrumSymbol(machine, SlotRules.SymbolAt(machine, reel, stop, j));
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

    /// <summary>Put <see cref="ReelTexture"/> on a drum of the room's machine (this machine's view only). Tolerates any
    /// renderer layout: the strip material is the one whose name or texture starts with "reel".</summary>
    public static void SkinReel(Transform drum, int machine, int reel, int stop)
    {
        if (drum == null) return;
        try
        {
            var r = drum.GetComponentInChildren<MeshRenderer>();
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

    /// <summary>The cabinet colour of a machine (header and reel frame).</summary>
    public static Color MachineColor(int machine)
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

    /// <summary>One line under a machine's name.</summary>
    public static string MachineFlavour(int machine)
    {
        switch (machine)
        {
            case SlotRules.LuckySevens: return "Bars, cherries and sevens. Small wins, often.";
            case SlotRules.TrashPandaBandit: return "The whole gang's in the bins tonight.";
            case SlotRules.GoldenGnome: return "Long dry spells. One very shiny gnome.";
            case SlotRules.DumpsterFire: return "Two fires come up a lot. The third one doesn't.";
            default: return "Twenty-five a pull. Nobody knows what's in the bag.";
        }
    }

    static readonly Color Gold = Hex(0xF2C230), GoldDark = Hex(0xB7860B), Silver = Hex(0xB9BEC6), Dark = Hex(0x23212B);

    static void Seven(Painter p, Color c)
    {
        p.Shape(Poly(-0.6f, 0.72f, 0.62f, 0.72f, 0.62f, 0.46f, 0.05f, -0.76f, -0.3f, -0.76f, 0.22f, 0.4f, -0.6f, 0.4f), c);
    }

    static void Bars(Painter p, int n)
    {
        float h = n == 1 ? 0.26f : n == 2 ? 0.2f : 0.16f, gap = n == 1 ? 0f : n == 2 ? 0.5f : 0.42f;
        for (int i = 0; i < n; i++)
        {
            float y = (i - (n - 1) * 0.5f) * gap;
            p.Shape(Box(0f, y, 0.72f, h, 0.06f), Gold);
            p.Detail(Box(0f, y, 0.64f, h - 0.07f, 0.04f), Dark);
            p.Detail(Box(0f, y, 0.5f, 0.035f, 0.02f), Gold);
        }
    }

    static void Gnome(Painter p, Color hat, Color skin, Color beard, Color coat)
    {
        p.Shape(Box(0f, -0.52f, 0.36f, 0.26f, 0.12f), coat);
        p.Shape(Circle(0f, -0.02f, 0.3f), skin);
        p.Shape(Poly(-0.34f, -0.06f, 0.34f, -0.06f, 0f, -0.72f), beard);
        p.Shape(Poly(-0.4f, 0.14f, 0.4f, 0.14f, 0.06f, 0.86f), hat);
        p.Detail(Circle(0f, -0.04f, 0.08f), Color.Lerp(skin, Color.red, 0.35f));
    }

    static void Flame(Painter p, float cx, float cy, float s)
    {
        p.Shape(Poly(cx - 0.34f * s, cy - 0.3f * s, cx - 0.42f * s, cy + 0.1f * s, cx - 0.2f * s, cy + 0.02f * s, cx - 0.06f * s, cy + 0.72f * s,
                     cx + 0.16f * s, cy + 0.16f * s, cx + 0.3f * s, cy + 0.36f * s, cx + 0.4f * s, cy - 0.18f * s, cx + 0.2f * s, cy - 0.5f * s, cx - 0.16f * s, cy - 0.5f * s),
                Hex(0xF5631A));
        p.Detail(Poly(cx - 0.14f * s, cy - 0.4f * s, cx - 0.18f * s, cy - 0.08f * s, cx - 0.02f * s, cy + 0.26f * s, cx + 0.14f * s, cy - 0.1f * s, cx + 0.12f * s, cy - 0.4f * s),
                 Hex(0xFFD84A));
    }

    static void Sack(Painter p, Color body, Color tie)
    {
        p.Shape(Ellipse(0f, -0.2f, 0.56f, 0.5f), body);
        p.Shape(Poly(-0.22f, 0.3f, 0.22f, 0.3f, 0.36f, 0.72f, -0.36f, 0.72f), body);
        p.Detail(Box(0f, 0.3f, 0.26f, 0.06f, 0.04f), tie);
    }

    static void Dollar(Painter p, float cx, float cy, float s, Color c)
    {
        p.Detail(Seg(cx + 0.14f * s, cy + 0.2f * s, cx - 0.1f * s, cy + 0.26f * s, 0.05f * s), c);
        p.Detail(Seg(cx - 0.1f * s, cy + 0.26f * s, cx - 0.14f * s, cy + 0.06f * s, 0.05f * s), c);
        p.Detail(Seg(cx - 0.14f * s, cy + 0.06f * s, cx + 0.14f * s, cy - 0.06f * s, 0.05f * s), c);
        p.Detail(Seg(cx + 0.14f * s, cy - 0.06f * s, cx + 0.1f * s, cy - 0.26f * s, 0.05f * s), c);
        p.Detail(Seg(cx + 0.1f * s, cy - 0.26f * s, cx - 0.14f * s, cy - 0.2f * s, 0.05f * s), c);
        p.Detail(Seg(cx, cy + 0.36f * s, cx, cy - 0.36f * s, 0.035f * s), c);
    }

    static void PaintSymbol(Painter p, int machine, int symbol)
    {
        switch (machine)
        {
            case SlotRules.LuckySevens:
                switch (symbol)
                {
                    case 1: // cherry
                        p.Shape(Seg(-0.3f, -0.25f, 0.2f, 0.7f, 0.035f), Hex(0x3E8E3A));
                        p.Shape(Seg(0.36f, -0.3f, 0.2f, 0.7f, 0.035f), Hex(0x3E8E3A));
                        p.Shape(Ellipse(0.46f, 0.62f, 0.26f, 0.12f), Hex(0x4CAF50));
                        p.Shape(Circle(-0.34f, -0.36f, 0.34f), Hex(0xD3222E));
                        p.Shape(Circle(0.38f, -0.42f, 0.34f), Hex(0xD3222E));
                        p.Detail(Circle(-0.44f, -0.26f, 0.08f), new Color(1f, 1f, 1f, 0.6f));
                        p.Detail(Circle(0.28f, -0.32f, 0.08f), new Color(1f, 1f, 1f, 0.6f));
                        break;
                    case 2: Bars(p, 1); break;
                    case 3: Bars(p, 2); break;
                    case 4: Bars(p, 3); break;
                    case 5: Seven(p, Hex(0xD3222E)); break;
                    default: // lucky seven: gold on a blue badge with a star
                        p.Shape(Circle(0f, 0f, 0.84f), Hex(0x2456C4));
                        p.Detail(Ring(0f, 0f, 0.74f, 0.03f), Gold);
                        Seven(p, Gold);
                        p.Shape(Star(-0.48f, -0.42f, 0.24f, 0.1f, 5), Color.white);
                        break;
                }
                break;

            case SlotRules.TrashPandaBandit:
                switch (symbol)
                {
                    case 1: // fish bone
                        p.Shape(Seg(-0.4f, 0f, 0.5f, 0f, 0.05f), Cream);
                        for (int i = 0; i < 4; i++) p.Shape(Seg(-0.22f + i * 0.2f, 0.34f - i * 0.05f, -0.22f + i * 0.2f, -0.34f + i * 0.05f, 0.04f), Cream);
                        p.Shape(Poly(-0.86f, 0.34f, -0.86f, -0.34f, -0.36f, 0f), Cream);
                        p.Shape(Poly(0.5f, 0f, 0.86f, 0.38f, 0.74f, 0f, 0.86f, -0.38f), Cream);
                        p.Detail(Circle(-0.68f, 0.06f, 0.06f), Dark);
                        break;
                    case 2: // tin can
                        p.Shape(Box(0f, -0.08f, 0.44f, 0.6f, 0.08f), Silver);
                        p.Detail(Box(0f, -0.1f, 0.44f, 0.28f, 0.02f), Hex(0xD3222E));
                        p.Detail(Box(0f, -0.1f, 0.3f, 0.06f, 0.03f), Cream);
                        p.Shape(Ellipse(0.12f, 0.62f, 0.44f, 0.16f), Hex(0xE2E5EA));
                        break;
                    case 3: // trash bag
                        p.Shape(Ellipse(0f, -0.22f, 0.6f, 0.54f), Hex(0x2B2B36));
                        p.Shape(Poly(-0.16f, 0.26f, 0.16f, 0.26f, 0.34f, 0.74f, 0f, 0.56f, -0.34f, 0.74f), Hex(0x2B2B36));
                        p.Detail(Seg(-0.34f, 0f, -0.26f, -0.4f, 0.05f), new Color(1f, 1f, 1f, 0.25f));
                        p.Detail(Box(0f, 0.28f, 0.2f, 0.05f, 0.03f), Hex(0xF2C230));
                        break;
                    case 4: // loot bag
                        Sack(p, Hex(0xC79A5B), Hex(0x6B4A22));
                        Dollar(p, 0f, -0.2f, 1.1f, Hex(0x2F6B2F));
                        break;
                    case 5: // bandit mask
                        p.Shape(Seg(-0.86f, 0.2f, -0.5f, 0.05f, 0.06f), Dark);
                        p.Shape(Seg(0.86f, 0.2f, 0.5f, 0.05f, 0.06f), Dark);
                        p.Shape(Box(0f, 0f, 0.62f, 0.3f, 0.26f), Dark);
                        p.Detail(Ellipse(-0.28f, 0.02f, 0.17f, 0.12f), Color.white);
                        p.Detail(Ellipse(0.28f, 0.02f, 0.17f, 0.12f), Color.white);
                        break;
                    case 6: // raccoon
                        p.Shape(Poly(-0.66f, 0.2f, -0.52f, 0.8f, -0.2f, 0.5f), Hex(0x8A8E99));
                        p.Shape(Poly(0.66f, 0.2f, 0.52f, 0.8f, 0.2f, 0.5f), Hex(0x8A8E99));
                        p.Shape(Ellipse(0f, -0.08f, 0.7f, 0.58f), Hex(0x9CA1AD));
                        p.Detail(Ellipse(-0.3f, 0f, 0.3f, 0.2f), Dark);
                        p.Detail(Ellipse(0.3f, 0f, 0.3f, 0.2f), Dark);
                        p.Detail(Ellipse(0f, -0.3f, 0.26f, 0.22f), Cream);
                        p.Detail(Circle(-0.28f, 0.02f, 0.08f), Color.white);
                        p.Detail(Circle(0.28f, 0.02f, 0.08f), Color.white);
                        p.Detail(Circle(0f, -0.26f, 0.09f), Dark);
                        break;
                    default: // the Raccoon King's crown
                        p.Shape(Poly(-0.7f, -0.5f, -0.76f, 0.44f, -0.36f, 0.02f, 0f, 0.66f, 0.36f, 0.02f, 0.76f, 0.44f, 0.7f, -0.5f), Gold);
                        p.Detail(Box(0f, -0.4f, 0.68f, 0.1f, 0.03f), GoldDark);
                        p.Detail(Circle(0f, -0.4f, 0.09f), Hex(0xD3222E));
                        p.Detail(Circle(-0.42f, -0.4f, 0.07f), Hex(0x2FA8D8));
                        p.Detail(Circle(0.42f, -0.4f, 0.07f), Hex(0x2FA8D8));
                        p.Shape(Circle(0f, 0.7f, 0.1f), Hex(0xD3222E));
                        break;
                }
                break;

            case SlotRules.GoldenGnome:
                switch (symbol)
                {
                    case 1: // toadstool
                        p.Shape(Box(0f, -0.38f, 0.2f, 0.38f, 0.1f), Cream);
                        p.Shape(Poly(-0.76f, 0f, -0.6f, 0.4f, -0.2f, 0.68f, 0.2f, 0.68f, 0.6f, 0.4f, 0.76f, 0f), Hex(0xD3222E));
                        p.Detail(Circle(-0.34f, 0.26f, 0.12f), Color.white);
                        p.Detail(Circle(0.16f, 0.42f, 0.1f), Color.white);
                        p.Detail(Circle(0.42f, 0.16f, 0.08f), Color.white);
                        break;
                    case 2: // lantern
                        p.Shape(Ring(0f, 0.62f, 0.2f, 0.045f), Dark);
                        p.Shape(Box(0f, -0.1f, 0.4f, 0.5f, 0.06f), Dark);
                        p.Detail(Box(0f, -0.1f, 0.3f, 0.38f, 0.04f), Hex(0xFFD84A));
                        p.Detail(Circle(0f, -0.14f, 0.14f), Hex(0xFFF3B0));
                        p.Shape(Box(0f, 0.44f, 0.46f, 0.07f, 0.03f), Dark);
                        p.Shape(Box(0f, -0.64f, 0.46f, 0.07f, 0.03f), Dark);
                        break;
                    case 3: // fishing rod
                        p.Shape(Seg(-0.7f, -0.7f, 0.6f, 0.7f, 0.05f), Hex(0x8A5A2B));
                        p.Detail(Seg(0.6f, 0.7f, 0.62f, -0.2f, 0.018f), Cream);
                        p.Shape(Circle(0.62f, -0.34f, 0.17f), Hex(0xD3222E));
                        p.Detail(Box(0.62f, -0.42f, 0.17f, 0.08f, 0.02f), Color.white);
                        p.Shape(Circle(-0.4f, -0.4f, 0.15f), Silver);
                        break;
                    case 4: Gnome(p, Hex(0xD3222E), Hex(0xF2C9A0), Color.white, Hex(0x2456C4)); break;
                    case 5: // pot of gold
                        p.Shape(Circle(-0.26f, 0.3f, 0.2f), Gold);
                        p.Shape(Circle(0.24f, 0.34f, 0.2f), Gold);
                        p.Shape(Circle(0f, 0.46f, 0.2f), Gold);
                        p.Shape(Ellipse(0f, -0.3f, 0.6f, 0.5f), Dark);
                        p.Shape(Box(0f, 0.16f, 0.62f, 0.09f, 0.06f), Hex(0x3A3846));
                        p.Detail(Seg(-0.36f, -0.2f, -0.3f, -0.5f, 0.045f), new Color(1f, 1f, 1f, 0.25f));
                        break;
                    default: // the golden gnome
                        p.Shape(Star(0f, 0f, 0.96f, 0.6f, 8), Hex(0xFFF3B0));
                        Gnome(p, Gold, Hex(0xF7DB6A), Hex(0xFFF3B0), GoldDark);
                        break;
                }
                break;

            case SlotRules.DumpsterFire:
                switch (symbol)
                {
                    case 1: // spark
                        p.Shape(Star(0f, 0f, 0.84f, 0.22f, 4), Hex(0xFFD84A));
                        p.Detail(Star(0f, 0f, 0.4f, 0.12f, 4, 45f), Color.white);
                        break;
                    case 2: // wheelie bin
                        p.Shape(Circle(0.3f, -0.66f, 0.16f), Dark);
                        p.Shape(Poly(-0.46f, 0.36f, 0.46f, 0.36f, 0.36f, -0.6f, -0.36f, -0.6f), Hex(0x2F8A4A));
                        p.Shape(Box(0f, 0.46f, 0.56f, 0.09f, 0.05f), Hex(0x24703B));
                        p.Detail(Seg(-0.14f, 0.2f, -0.12f, -0.4f, 0.03f), new Color(0f, 0f, 0f, 0.25f));
                        p.Detail(Seg(0.14f, 0.2f, 0.12f, -0.4f, 0.03f), new Color(0f, 0f, 0f, 0.25f));
                        break;
                    case 3: // hazard can
                        p.Shape(Box(0f, -0.06f, 0.46f, 0.62f, 0.1f), Hex(0xFFD640));
                        p.Shape(Box(0f, 0.64f, 0.2f, 0.08f, 0.03f), Dark);
                        p.Detail(Poly(-0.3f, -0.34f, 0.3f, -0.34f, 0f, 0.26f), Dark);
                        p.Detail(Seg(0f, 0.06f, 0f, -0.12f, 0.035f), Hex(0xFFD640));
                        p.Detail(Circle(0f, -0.24f, 0.04f), Hex(0xFFD640));
                        break;
                    case 4: // gas can
                        p.Shape(Poly(0.2f, 0.4f, 0.66f, 0.76f, 0.76f, 0.64f, 0.4f, 0.3f), Hex(0xE7B92A));
                        p.Shape(Box(-0.06f, -0.14f, 0.52f, 0.54f, 0.1f), Hex(0xD3222E));
                        p.Shape(Box(-0.24f, 0.5f, 0.24f, 0.08f, 0.04f), Hex(0x9E1820));
                        p.Detail(Seg(-0.36f, 0.12f, 0.24f, -0.42f, 0.04f), new Color(0f, 0f, 0f, 0.3f));
                        p.Detail(Seg(0.24f, 0.12f, -0.36f, -0.42f, 0.04f), new Color(0f, 0f, 0f, 0.3f));
                        break;
                    case 5: Flame(p, 0f, -0.1f, 1.3f); break;
                    default: // the dumpster fire
                        Flame(p, -0.3f, 0.3f, 0.75f);
                        Flame(p, 0.3f, 0.36f, 0.85f);
                        Flame(p, 0f, 0.3f, 0.6f);
                        p.Shape(Poly(-0.8f, 0.12f, 0.8f, 0.12f, 0.64f, -0.66f, -0.64f, -0.66f), Hex(0x3F6FA3));
                        p.Shape(Box(0f, 0.14f, 0.84f, 0.07f, 0.04f), Hex(0x2D5480));
                        p.Detail(Box(-0.3f, -0.26f, 0.05f, 0.3f, 0.02f), new Color(0f, 0f, 0f, 0.25f));
                        p.Detail(Box(0.3f, -0.26f, 0.05f, 0.3f, 0.02f), new Color(0f, 0f, 0f, 0.25f));
                        break;
                }
                break;

            default: // Mystery Bag
                switch (symbol)
                {
                    case 1: // question mark
                        p.Shape(Circle(0f, 0f, 0.8f), Hex(0x6A3FA0));
                        p.Detail(Seg(-0.26f, 0.3f, -0.12f, 0.5f, 0.07f), Color.white);
                        p.Detail(Seg(-0.12f, 0.5f, 0.16f, 0.5f, 0.07f), Color.white);
                        p.Detail(Seg(0.16f, 0.5f, 0.28f, 0.3f, 0.07f), Color.white);
                        p.Detail(Seg(0.28f, 0.3f, 0.02f, 0.02f, 0.07f), Color.white);
                        p.Detail(Seg(0.02f, 0.02f, 0.02f, -0.16f, 0.07f), Color.white);
                        p.Detail(Circle(0.02f, -0.44f, 0.09f), Color.white);
                        break;
                    case 2: // paper sack
                        p.Shape(Poly(-0.46f, 0.5f, -0.3f, 0.66f, -0.14f, 0.5f, 0.02f, 0.66f, 0.18f, 0.5f, 0.34f, 0.66f, 0.46f, 0.5f, 0.4f, -0.7f, -0.4f, -0.7f), Hex(0xC9A36B));
                        p.Detail(Seg(-0.2f, 0.3f, -0.16f, -0.6f, 0.025f), new Color(0f, 0f, 0f, 0.18f));
                        p.Detail(Seg(0.2f, 0.3f, 0.16f, -0.6f, 0.025f), new Color(0f, 0f, 0f, 0.18f));
                        break;
                    case 3: // burlap sack
                        Sack(p, Hex(0x9B7442), Hex(0x5C3D1C));
                        p.Detail(Seg(-0.3f, -0.05f, 0.1f, -0.5f, 0.025f), new Color(0f, 0f, 0f, 0.22f));
                        p.Detail(Seg(0.3f, -0.05f, -0.1f, -0.5f, 0.025f), new Color(0f, 0f, 0f, 0.22f));
                        break;
                    case 4: // money sack
                        Sack(p, Hex(0x4E8F4A), Hex(0x2C5C2A));
                        Dollar(p, 0f, -0.2f, 1.1f, Gold);
                        break;
                    case 5: // old coin
                        p.Shape(Circle(0f, 0f, 0.74f), Gold);
                        p.Detail(Ring(0f, 0f, 0.58f, 0.04f), GoldDark);
                        p.Detail(Star(0f, 0f, 0.36f, 0.16f, 5), GoldDark);
                        break;
                    case 6: // diamond ring
                        p.Shape(Ring(0f, -0.3f, 0.42f, 0.09f), Gold);
                        p.Shape(Poly(-0.34f, 0.4f, -0.18f, 0.62f, 0.18f, 0.62f, 0.34f, 0.4f, 0f, 0.04f), Hex(0x8FE3F5));
                        p.Detail(Poly(-0.34f, 0.4f, 0.34f, 0.4f, 0f, 0.04f), Hex(0x5CC7E0));
                        break;
                    default: // the golden trash bag
                        p.Shape(Star(0f, 0f, 0.96f, 0.62f, 8), Hex(0xFFF3B0));
                        p.Shape(Ellipse(0f, -0.22f, 0.56f, 0.5f), Gold);
                        p.Shape(Poly(-0.16f, 0.24f, 0.16f, 0.24f, 0.32f, 0.7f, 0f, 0.52f, -0.32f, 0.7f), Gold);
                        p.Detail(Box(0f, 0.26f, 0.2f, 0.05f, 0.03f), GoldDark);
                        p.Detail(Seg(-0.3f, 0f, -0.24f, -0.36f, 0.05f), new Color(1f, 1f, 1f, 0.55f));
                        break;
                }
                break;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // Sprites are DontSave and survive a domain-reload-free play start; nothing to clear.
    }
}

// Setup_Casino, part 3: every picture in the casino is painted here (carpet, wood, pressed tin, felt layouts, the
// roulette wheel, reel strips, machine glass, posters) and saved as a PNG under Assets/Materials/Generated/Casino/Tex.
// No text is ever painted: lettering is TextMeshPro in the scene (ASCII only, so no dynamic font atlas is touched).
using System;
using UnityEngine;

public static partial class Setup_Casino
{
    static readonly Color Gold = C(0.62f, 0.47f, 0.17f), Cream = C(0.86f, 0.80f, 0.64f), Ink = C(0.05f, 0.04f, 0.04f);
    static readonly Color RedInk = C(0.58f, 0.07f, 0.06f), FeltGreen = C(0.07f, 0.30f, 0.16f), FeltLine = C(0.80f, 0.72f, 0.38f);

    /// <summary>Six symbols per machine (Docs/Casino.md §4.3 flavours), and the order of the twelve faces of a drum.</summary>
    static readonly string[][] SlotSymbols =
    {
        new[] { "seven", "bar", "cherry", "bell", "diamond", "star" },
        new[] { "raccoon", "mask", "loot", "coin", "bag", "star" },
        new[] { "gnome", "toadstool", "coin", "gem", "bell", "horseshoe" },
        new[] { "flame", "bin", "hazard", "bag", "seven", "coin" },
        new[] { "question", "loot", "gem", "diamond", "coin", "star" },
    };
    static readonly int[] FaceOrder = { 0, 1, 2, 3, 4, 5, 0, 2, 1, 4, 3, 5 };
    static readonly Color[] SlotColor =
    {
        C(0.62f, 0.09f, 0.08f), C(0.20f, 0.24f, 0.30f), C(0.16f, 0.36f, 0.16f), C(0.78f, 0.36f, 0.06f), C(0.34f, 0.14f, 0.46f),
    };
    static readonly Color[] SlotAccent =
    {
        C(0.95f, 0.78f, 0.25f), C(0.75f, 0.80f, 0.85f), C(0.95f, 0.80f, 0.25f), C(1f, 0.85f, 0.2f), C(0.35f, 0.9f, 0.8f),
    };

    static void PaintTextures()
    {
        PaintWood(); PaintCarpet(); PaintTin(); PaintLeather(); PaintSoft();
        PaintBlackjackFelt(); PaintRouletteFelt(); PaintWheel(); PaintClock();
        for (int i = 0; i < CasinoLayout.SlotCount; i++) { PaintReel(i); PaintBelly(i); }
        for (int i = 0; i < PosterCount; i++) PaintPoster(i);
        UnityEditor.AssetDatabase.Refresh();
    }

    // ------------------------------------------------------------------------------------------------ surfaces

    static void PaintWood()
    {
        var c = new Canvas(512, 512, Color.white);
        c.Each((x, y, _) =>
        {
            float grain = Noise(x / 7f, y / 128f, 73, 4, 11);
            float fine = Noise(x / 2.2f, y / 46f, 233, 11, 5);
            float band = Noise(x / 60f, y / 256f, 8, 2, 3);
            float pore = Noise(x / 1.3f, y / 9f, 394, 57, 9);
            float v = 0.62f + 0.22f * grain + 0.10f * fine + 0.12f * band - (pore > 0.86f ? 0.10f : 0f);
            return new Color(v, v * 0.97f, v * 0.93f, 1f);
        });
        SaveTex("wood", c, true, false, 512);
    }

    static void PaintCarpet()
    {
        const int S = 512, P = 256;
        Color baseC = C(0.31f, 0.065f, 0.07f), wine = C(0.19f, 0.035f, 0.05f), gold = C(0.50f, 0.36f, 0.13f), dark = C(0.11f, 0.02f, 0.03f);
        var c = new Canvas(S, S, baseC) { wrap = true };
        float Tri(float t) { t = ((t % P) + P) % P; return Mathf.Min(t, P - t) * 0.7071f; }
        c.Each((x, y, col) =>
        {
            float d = Mathf.Min(Tri(x + y), Tri(x - y));
            if (d < 9f) col = Color.Lerp(wine, col, Mathf.Clamp01((d - 7f) / 2f));
            if (d > 11f && d < 14f) col = Color.Lerp(col, gold, 0.75f);
            return col;
        });
        // lozenge centres: a quatrefoil; nodes: a small rosette
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                float x = i * 128f, y = j * 128f;
                bool centre = ((i + j) & 1) == 1;
                if (centre)
                {
                    c.Ellipse(x, y + 30, 15, 26, gold); c.Ellipse(x, y - 30, 15, 26, gold); c.Ellipse(x + 30, y, 26, 15, gold); c.Ellipse(x - 30, y, 26, 15, gold);
                    c.Ellipse(x, y + 30, 6, 14, wine); c.Ellipse(x, y - 30, 6, 14, wine); c.Ellipse(x + 30, y, 14, 6, wine); c.Ellipse(x - 30, y, 14, 6, wine);
                    c.Disc(x, y, 13, dark); c.Disc(x, y, 8, gold); c.Disc(x, y, 3.5f, wine);
                    for (int k = 0; k < 4; k++) { float a = (45 + k * 90) * Mathf.Deg2Rad; c.Disc(x + Mathf.Cos(a) * 44, y + Mathf.Sin(a) * 44, 4.5f, gold); }
                }
                else if ((i & 1) == 0)
                {
                    c.Disc(x, y, 12, dark); c.Ring(x, y, 9, 3, gold); c.Disc(x, y, 3, gold);
                }
            }
        // decades of feet: blotchy wear, a dusty cast
        c.Each((x, y, col) =>
        {
            float wear = 0.74f + 0.34f * Noise(x / 64f, y / 64f, 8, 21) * (0.75f + 0.25f * Noise(x / 9f, y / 9f, 57, 4));
            float pile = 0.94f + 0.12f * Hash01(x * 7 + 3, y * 13 + 1);
            col *= wear * pile;
            col = Color.Lerp(col, C(0.20f, 0.16f, 0.13f), 0.10f); col.a = 1f;
            return col;
        });
        SaveTex("carpet", c, true, false, 512);
    }

    static void PaintTin()
    {
        const int S = 256;
        var c = new Canvas(S, S, Color.white);
        float H(float x, float y)
        {
            float u = (x / S - 0.5f) * 2f, v = (y / S - 0.5f) * 2f;
            float sq = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)), r = Mathf.Sqrt(u * u + v * v);
            float h = 0f;
            h += Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 0.9f, sq)) * 0.5f;               // raised field inside a border groove
            h += Mathf.Cos(Mathf.Clamp(r * 7f, 0f, Mathf.PI * 3f)) * 0.22f * Mathf.Clamp01(1.1f - r); // rosette rings
            float petal = Mathf.Cos(Mathf.Atan2(v, u) * 8f) * 0.12f * Mathf.Clamp01(1f - Mathf.Abs(r - 0.55f) * 5f);
            h += petal;
            h += Mathf.Max(0f, 0.2f - Mathf.Abs(sq - 0.78f)) * 1.2f;                               // inner frame bead
            return h;
        }
        c.Each((x, y, _) =>
        {
            float dx = H(x + 1, y) - H(x - 1, y), dy = H(x, y + 1) - H(x, y - 1);
            float shade = 0.72f + (dx * -0.9f + dy * 1.3f) * 2.4f;
            float grime = 0.86f + 0.2f * Noise(x / 20f, y / 20f, 13, 6);
            float v = Mathf.Clamp(shade * grime, 0.25f, 1.15f);
            return new Color(v, v * 0.96f, v * 0.88f, 1f);
        });
        SaveTex("tin", c, true, false, 256);
    }

    static void PaintLeather()
    {
        const int S = 256, P = 128;
        var c = new Canvas(S, S, Color.white) { wrap = true };
        float Tri(float t) { t = ((t % P) + P) % P; return Mathf.Min(t, P - t) * 0.7071f; }
        c.Each((x, y, _) =>
        {
            float d = Mathf.Min(Tri(x + y), Tri(x - y));              // 0 on the seams, about 45 in a cushion's middle
            float puff = Mathf.Clamp01(d / 30f);
            float v = 0.42f + 0.58f * Mathf.Sqrt(puff);
            v *= 0.94f + 0.1f * Noise(x / 3f, y / 3f, 85, 2);
            return new Color(v, v, v, 1f);
        });
        Color stud = C(1.5f, 1.25f, 0.6f);
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (((i + j) & 1) == 0) { c.Disc(i * 64f, j * 64f, 6f, C(0.25f, 0.2f, 0.1f)); c.Disc(i * 64f - 1, j * 64f + 1, 4f, stud); }
        SaveTex("leather", c, true, false, 256);
    }

    /// <summary>Soft alpha shapes: a floor stain, a haze puff, a pool of light.</summary>
    static void PaintSoft()
    {
        var s = new Canvas(128, 128, new Color(0, 0, 0, 0));
        s.Each((x, y, _) =>
        {
            float u = (x - 64) / 64f, v = (y - 64) / 64f, r = Mathf.Sqrt(u * u + v * v);
            float edge = 0.62f + 0.3f * Noise(x / 14f + 5, y / 14f, 9, 31);
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((edge - r) / 0.28f)) * (0.55f + 0.45f * Noise(x / 6f, y / 6f, 21, 8));
            return new Color(0.03f, 0.015f, 0.01f, a * 0.62f);
        });
        SaveTex("stain", s, false, true, 128);
        var h = new Canvas(128, 128, new Color(1, 1, 1, 0));
        h.Each((x, y, _) =>
        {
            float u = (x - 64) / 64f, v = (y - 64) / 64f, r = Mathf.Sqrt(u * u + v * v);
            float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f);
            return new Color(1f, 1f, 1f, a);
        });
        SaveTex("puff", h, false, true, 128);
    }

    // ------------------------------------------------------------------------------------------------ felt

    static Canvas Felt(int w, int h, int seed)
    {
        var c = new Canvas(w, h, FeltGreen);
        c.Each((x, y, col) =>
        {
            float v = 0.86f + 0.2f * Hash01(x + seed, y) * 0.6f + 0.14f * Noise(x / 90f, y / 90f, 64, seed);
            col *= v; col.a = 1f; return col;
        });
        return c;
    }

    /// <summary>Blackjack: the half-disc of felt (radius BjRF), dealer along the top edge. Canvas = 2 x 1 (RF per half), the arc
    /// centre at the middle of the top... of the dealer edge; canvas y grows toward the dealer. Printed: the insurance band, the
    /// betting arc, five bet circles (with a small insurance spot each) and six dealer card outlines. Lettering is TMP (BlackjackPrints).</summary>
    static void PaintBlackjackFelt()
    {
        const int W = 2048, H = 1024;
        var c = Felt(W, H, 3);
        float s = H / BjRF;                                    // pixels per metre
        float PX(float xm) => W * .5f + xm * s;
        float PY(float zFromC) => H + zFromC * s;              // zFromC < 0 toward the players
        void Arc(float r, float t, float from, float to, Color col)
        {
            int n = 160;
            for (int i = 0; i < n; i++)
            {
                float a0 = Mathf.Lerp(from, to, i / (float)n) * Mathf.Deg2Rad, a1 = Mathf.Lerp(from, to, (i + 1) / (float)n) * Mathf.Deg2Rad;
                c.Line(PX(Mathf.Sin(a0) * r), PY(-Mathf.Cos(a0) * r), PX(Mathf.Sin(a1) * r), PY(-Mathf.Cos(a1) * r), t, col);
            }
        }
        Color dim = new Color(FeltLine.r * .8f, FeltLine.g * .8f, FeltLine.b * .8f, 1f);
        Arc(0.52f, 4f, -80f, 80f, FeltLine); Arc(0.68f, 4f, -80f, 80f, FeltLine);       // the insurance band
        Arc(1.20f, 3f, -76f, 76f, FeltLine);                                              // the betting arc
        Arc(1.385f, 3f, -86f, 86f, dim);                                                  // the felt's edge line
        Color pad = C(0.045f, 0.22f, 0.115f);
        for (int k = 0; k < 5; k++)
        {
            float th = BjTheta[k] * Mathf.Deg2Rad;
            float bx = PX(Mathf.Sin(th) * BjBet), by = PY(-Mathf.Cos(th) * BjBet);
            c.Disc(bx, by, .088f * s, pad, 1.5f);
            c.Ring(bx, by, .088f * s, 5f, FeltLine); c.Ring(bx, by, .072f * s, 2f, FeltLine);
        }
        // the dealer's six card places (faint outlines, where the tilted cards will lie)
        for (int i = 0; i < 6; i++)
        {
            float cx = (i - 2.5f) * 0.082f, cz = -BjDealerRow;
            float hw = CasinoLayout.CardWidth * .5f * s, hh = CasinoLayout.CardHeight * .5f * s * 0.9f;
            c.Frame(PX(cx) - hw, PY(cz) - hh, PX(cx) + hw, PY(cz) + hh, 2f, dim);
        }
        // wear where forearms rest, a ring from a glass
        float R = H;
        c.Each((x, y, col) =>
        {
            float r = Mathf.Sqrt((x - W * .5f) * (x - W * .5f) + (y - H) * (y - H)) / R;
            float worn = Mathf.Clamp01((r - 0.86f) / 0.14f) * 0.22f * Noise(x / 40f, y / 40f, 26, 2);
            return Color.Lerp(col, C(0.2f, 0.34f, 0.24f), worn);
        });
        c.Ring(PX(.98f), PY(-.62f), 55f, 5f, C(0.03f, 0.16f, 0.08f, 0.6f));
        SaveTex("felt_blackjack", c, false, false, 2048);
    }

    // Roulette table-top metrics (metres, table-local; +X toward the far end from the wheel, +Z away from the player).
    const float RlFeltX = 1.35f, RlFeltZ = 0.55f;                        // half extents of the felt
    const float RlCellX = 0.125f, RlCellZ = 0.17f, RlGridX0 = -0.37f, RlGridZ0 = -0.22f, RlOutside = 0.7209f * RlCellZ; // RlOutside / RlCellZ = RouletteScreen.OutH / CH
    const float RlWheelX = -0.93f;

    static void PaintRouletteFelt()
    {
        const int W = 3072, H = 1252; // 2.7 x 1.1 m at about 1140 px/m
        var c = Felt(W, H, 7);
        float PX(float x) => (x + RlFeltX) / (2f * RlFeltX) * W;
        float PY(float z) => (z + RlFeltZ) / (2f * RlFeltZ) * H;
        const float T = 7f;
        void Cell(float x0, float z0, float x1, float z1) => c.Frame(PX(x0) - T * .5f, PY(z0) - T * .5f, PX(x1) + T * .5f, PY(z1) + T * .5f, T, FeltLine);
        Color red = C(0.56f, 0.07f, 0.06f), black = C(0.045f, 0.045f, 0.05f);
        float gx1 = RlGridX0 + 12 * RlCellX, gz1 = RlGridZ0 + 3 * RlCellZ;
        for (int n = 1; n <= 36; n++)
        {
            float x0 = RlGridX0 + (n - 1) / 3 * RlCellX, z0 = RlGridZ0 + (n - 1) % 3 * RlCellZ;
            float mx = PX(x0 + RlCellX * .5f), my = PY(z0 + RlCellZ * .5f);
            c.Ellipse(mx, my, (PX(RlCellX) - PX(0)) * 0.44f, (PY(RlCellZ) - PY(0)) * 0.44f, CasinoStation.IsRed(n) ? red : black);
            Cell(x0, z0, x0 + RlCellX, z0 + RlCellZ);
        }
        // 0 and 00: the two head cells, with the slanted outer corners of a real layout
        float hx0 = RlGridX0 - RlCellX, hm = RlGridZ0 + 1.5f * RlCellZ;
        Color zero = C(0.05f, 0.42f, 0.2f);
        c.Rect(PX(hx0), PY(RlGridZ0), PX(RlGridX0), PY(gz1), zero);
        Cell(hx0, RlGridZ0, RlGridX0, hm); Cell(hx0, hm, RlGridX0, gz1);
        // columns "2 to 1"
        for (int r = 0; r < 3; r++) Cell(gx1, RlGridZ0 + r * RlCellZ, gx1 + RlCellX, RlGridZ0 + (r + 1) * RlCellZ);
        // dozens, then the six even-money boxes
        for (int d = 0; d < 3; d++) Cell(RlGridX0 + d * 4 * RlCellX, RlGridZ0 - RlOutside, RlGridX0 + (d + 1) * 4 * RlCellX, RlGridZ0);
        for (int e = 0; e < 6; e++)
        {
            float x0 = RlGridX0 + e * 2 * RlCellX, z0 = RlGridZ0 - 2 * RlOutside;
            Cell(x0, z0, x0 + 2 * RlCellX, z0 + RlOutside);
            if (e == 2 || e == 3)
            {
                float mx = PX(x0 + RlCellX), my = PY(z0 + RlOutside * .5f), dx = (PX(RlCellX) - PX(0)) * 0.62f, dy = (PY(RlOutside) - PY(0)) * 0.36f;
                c.Poly(e == 2 ? red : black, mx - dx, my, mx, my + dy, mx + dx, my, mx, my - dy);
            }
        }
        SaveTex("felt_roulette", c, false, false, 4096);
    }

    /// <summary>The rotor seen from above: 38 pockets in American order, clockwise from the top (v+ = local +Z).</summary>
    static void PaintWheel()
    {
        const int S = 512;
        var c = new Canvas(S, S, C(0.2f, 0.1f, 0.05f));
        Color red = C(0.62f, 0.06f, 0.05f), black = C(0.04f, 0.04f, 0.045f), green = C(0.04f, 0.42f, 0.18f), brass = C(0.75f, 0.58f, 0.22f);
        int N = CasinoStation.WheelOrder.Length;
        c.Each((x, y, col) =>
        {
            float u = (x + .5f) / S * 2f - 1f, v = (y + .5f) / S * 2f - 1f, r = Mathf.Sqrt(u * u + v * v);
            if (r > 1f) return C(0.16f, 0.08f, 0.04f);
            float ang = Mathf.Atan2(u, v) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
            float step = 360f / N; int k = Mathf.RoundToInt(ang / step) % N;
            float off = Mathf.Abs(Mathf.DeltaAngle(ang, k * step));
            int num = CasinoStation.WheelOrder[k];
            Color pocket = num == 0 || num == 37 ? green : CasinoStation.IsRed(num) ? red : black;
            if (r > 0.59f)
            {
                bool fret = off > step * 0.5f - 0.55f / Mathf.Max(0.3f, r);
                Color pc = r < 0.80f ? pocket * 0.72f : pocket; pc.a = 1f;
                if (fret || Mathf.Abs(r - 0.80f) < 0.008f || r > 0.985f || r < 0.60f) return brass;
                return pc;
            }
            // the cone: polished wood with a radial grain and brass rings
            float g = 0.75f + 0.25f * Noise(ang / 3f, r * 6f, 120, 64, 4);
            Color wood = C(0.42f, 0.2f, 0.09f) * g; wood.a = 1f;
            if (Mathf.Abs(r - 0.30f) < 0.01f || Mathf.Abs(r - 0.56f) < 0.012f) return brass;
            return wood;
        });
        SaveTex("wheel", c, false, false, 512);
    }

    static void PaintClock()
    {
        var c = new Canvas(256, 256, C(0.80f, 0.74f, 0.58f));
        c.Each((x, y, col) => { float v = 0.9f + 0.14f * Noise(x / 30f, y / 30f, 9, 3); col *= v; col.a = 1; return col; });
        for (int i = 0; i < 60; i++)
        {
            float a = i * 6f * Mathf.Deg2Rad; bool hour = i % 5 == 0;
            float r0 = hour ? 92f : 104f, r1 = 112f;
            c.Line(128 + Mathf.Sin(a) * r0, 128 + Mathf.Cos(a) * r0, 128 + Mathf.Sin(a) * r1, 128 + Mathf.Cos(a) * r1, hour ? 6f : 2f, Ink);
        }
        c.Ring(128, 128, 120, 3, Ink);
        SaveTex("clock", c, false, false, 256);
    }

    // ------------------------------------------------------------------------------------------------ slot art

    /// <summary>One slot symbol centred at (cx, cy), about 2*s across. y is up.</summary>
    static void Icon(Canvas c, string id, float cx, float cy, float s)
    {
        Color gold = C(0.93f, 0.72f, 0.16f), goldD = C(0.62f, 0.42f, 0.06f), red = C(0.82f, 0.08f, 0.07f), white = C(0.96f, 0.95f, 0.9f);
        float X(float u) => cx + u * s; float Y(float v) => cy + v * s;
        switch (id)
        {
            case "seven":
                c.Poly(C(0.35f, 0.02f, 0.02f), X(-.72f), Y(.82f), X(.78f), Y(.82f), X(.78f), Y(.5f), X(.12f), Y(-.86f), X(-.42f), Y(-.86f), X(.26f), Y(.42f), X(-.72f), Y(.42f));
                c.Poly(red, X(-.64f), Y(.74f), X(.68f), Y(.74f), X(.68f), Y(.52f), X(.04f), Y(-.78f), X(-.3f), Y(-.78f), X(.36f), Y(.5f), X(-.64f), Y(.5f));
                break;
            case "bar":
                c.Rect(X(-.85f), Y(-.6f), X(.85f), Y(.6f), Ink);
                for (int i = -1; i <= 1; i++) c.Rect(X(-.7f), Y(i * .36f - .12f), X(.7f), Y(i * .36f + .12f), gold);
                break;
            case "cherry":
                c.Line(X(-.35f), Y(-.2f), X(.2f), Y(.8f), s * .09f, C(0.15f, 0.45f, 0.12f)); c.Line(X(.45f), Y(-.3f), X(.2f), Y(.8f), s * .09f, C(0.15f, 0.45f, 0.12f));
                c.Disc(X(-.38f), Y(-.38f), s * .42f, red); c.Disc(X(.46f), Y(-.46f), s * .4f, C(0.7f, 0.05f, 0.08f));
                c.Disc(X(-.5f), Y(-.24f), s * .1f, white);
                break;
            case "bell":
                c.Ellipse(X(0), Y(.1f), s * .55f, s * .68f, gold); c.Rect(X(-.75f), Y(-.56f), X(.75f), Y(-.34f), goldD);
                c.Disc(X(0), Y(-.7f), s * .16f, goldD); c.Disc(X(0), Y(.8f), s * .12f, goldD); c.Ellipse(X(-.2f), Y(.25f), s * .1f, s * .3f, C(1f, .95f, .7f, .7f));
                break;
            case "diamond":
                c.Poly(C(0.2f, 0.72f, 0.9f), X(0), Y(.85f), X(.72f), Y(.2f), X(0), Y(-.85f), X(-.72f), Y(.2f));
                c.Poly(C(0.65f, 0.93f, 1f), X(0), Y(.85f), X(.3f), Y(.2f), X(-.3f), Y(.2f)); c.Poly(C(0.1f, 0.5f, 0.75f), X(-.3f), Y(.2f), X(.3f), Y(.2f), X(0), Y(-.85f));
                break;
            case "star":
                {
                    var p = new float[20];
                    for (int i = 0; i < 10; i++) { float a = i * 36f * Mathf.Deg2Rad, r = (i & 1) == 0 ? .9f : .38f; p[i * 2] = X(Mathf.Sin(a) * r); p[i * 2 + 1] = Y(Mathf.Cos(a) * r); }
                    c.Poly(gold, p); c.Disc(X(0), Y(0), s * .2f, C(1f, .92f, .55f));
                }
                break;
            case "raccoon":
                c.Poly(C(0.3f, 0.3f, 0.32f), X(-.8f), Y(.9f), X(-.3f), Y(.55f), X(-.8f), Y(.3f)); c.Poly(C(0.3f, 0.3f, 0.32f), X(.8f), Y(.9f), X(.3f), Y(.55f), X(.8f), Y(.3f));
                c.Ellipse(X(0), Y(0), s * .82f, s * .68f, C(0.55f, 0.55f, 0.56f));
                c.Ellipse(X(-.36f), Y(.08f), s * .34f, s * .22f, Ink); c.Ellipse(X(.36f), Y(.08f), s * .34f, s * .22f, Ink);
                c.Ellipse(X(0), Y(-.3f), s * .3f, s * .26f, white); c.Disc(X(0), Y(-.2f), s * .1f, Ink);
                c.Disc(X(-.34f), Y(.1f), s * .09f, white); c.Disc(X(.34f), Y(.1f), s * .09f, white);
                break;
            case "mask":
                c.Ellipse(X(-.4f), Y(0), s * .5f, s * .36f, Ink); c.Ellipse(X(.4f), Y(0), s * .5f, s * .36f, Ink);
                c.Ellipse(X(-.4f), Y(.02f), s * .22f, s * .14f, white); c.Ellipse(X(.4f), Y(.02f), s * .22f, s * .14f, white);
                c.Line(X(-.85f), Y(.1f), X(-1f), Y(.4f), s * .08f, Ink); c.Line(X(.85f), Y(.1f), X(1f), Y(.4f), s * .08f, Ink);
                break;
            case "loot":
                c.Ellipse(X(0), Y(-.2f), s * .68f, s * .62f, C(0.62f, 0.48f, 0.26f)); c.Poly(C(0.62f, 0.48f, 0.26f), X(-.3f), Y(.3f), X(.3f), Y(.3f), X(.44f), Y(.85f), X(-.44f), Y(.85f));
                c.Rect(X(-.36f), Y(.3f), X(.36f), Y(.44f), C(0.3f, 0.2f, 0.08f)); c.Disc(X(0), Y(-.22f), s * .26f, gold); c.Disc(X(0), Y(-.22f), s * .15f, goldD);
                break;
            case "coin":
                c.Disc(X(0), Y(0), s * .78f, goldD); c.Disc(X(0), Y(0), s * .68f, gold); c.Ring(X(0), Y(0), s * .48f, s * .07f, goldD); c.Disc(X(-.2f), Y(.25f), s * .1f, C(1f, .95f, .6f));
                break;
            case "bag":
                c.Ellipse(X(0), Y(-.18f), s * .7f, s * .66f, C(0.08f, 0.08f, 0.09f)); c.Poly(C(0.08f, 0.08f, 0.09f), X(-.14f), Y(.4f), X(.14f), Y(.4f), X(.4f), Y(.9f), X(0), Y(.66f), X(-.4f), Y(.9f));
                c.Ellipse(X(-.24f), Y(.0f), s * .1f, s * .3f, C(0.5f, 0.5f, 0.55f, 0.6f)); c.Rect(X(-.2f), Y(.38f), X(.2f), Y(.48f), C(0.85f, 0.2f, 0.15f));
                break;
            case "gnome":
                c.Poly(white, X(-.5f), Y(.05f), X(.5f), Y(.05f), X(0), Y(-.9f));
                c.Disc(X(0), Y(.1f), s * .36f, C(0.93f, 0.72f, 0.58f)); c.Disc(X(0), Y(.02f), s * .12f, C(0.9f, 0.5f, 0.45f));
                c.Poly(red, X(-.52f), Y(.26f), X(.52f), Y(.26f), X(.08f), Y(.95f));
                c.Disc(X(-.16f), Y(.18f), s * .05f, Ink); c.Disc(X(.16f), Y(.18f), s * .05f, Ink);
                break;
            case "toadstool":
                c.Rect(X(-.2f), Y(-.85f), X(.2f), Y(.0f), C(0.92f, 0.88f, 0.76f));
                {
                    var cap = new float[26];
                    for (int i = 0; i <= 12; i++) { float a = i * 15f * Mathf.Deg2Rad; cap[i * 2] = X(Mathf.Cos(a) * .82f); cap[i * 2 + 1] = Y(.02f + Mathf.Sin(a) * .62f); }
                    c.Poly(red, cap);
                }
                c.Disc(X(-.36f), Y(.34f), s * .12f, white); c.Disc(X(.3f), Y(.4f), s * .1f, white); c.Disc(X(.02f), Y(.5f), s * .08f, white);
                break;
            case "gem":
                {
                    var p = new float[12];
                    for (int i = 0; i < 6; i++) { float a = i * 60f * Mathf.Deg2Rad; p[i * 2] = X(Mathf.Sin(a) * .8f); p[i * 2 + 1] = Y(Mathf.Cos(a) * .8f); }
                    c.Poly(C(0.8f, 0.15f, 0.6f), p); c.Poly(C(1f, 0.55f, 0.85f), X(0), Y(.8f), X(.69f), Y(.4f), X(0), Y(0), X(-.69f), Y(.4f));
                }
                break;
            case "horseshoe":
                c.Ring(X(0), Y(.05f), s * .56f, s * .28f, gold); c.Rect(X(-.24f), Y(-.95f), X(.24f), Y(-.2f), new Color(0, 0, 0, 0));
                c.Disc(X(0), Y(-.6f), s * .26f, C(0.96f, 0.95f, 0.9f)); // the gap (painted in the reel's paper colour)
                break;
            case "flame":
                c.Poly(C(0.95f, 0.35f, 0.05f), X(0), Y(.95f), X(.5f), Y(.15f), X(.62f), Y(-.4f), X(.3f), Y(-.85f), X(-.3f), Y(-.85f), X(-.62f), Y(-.4f), X(-.4f), Y(.1f), X(-.2f), Y(.45f));
                c.Poly(C(1f, 0.82f, 0.15f), X(.05f), Y(.4f), X(.3f), Y(-.2f), X(.2f), Y(-.7f), X(-.2f), Y(-.7f), X(-.32f), Y(-.3f), X(-.1f), Y(.0f));
                break;
            case "bin":
                c.Rect(X(-.5f), Y(-.8f), X(.5f), Y(.4f), C(0.12f, 0.45f, 0.2f)); c.Rect(X(-.64f), Y(.4f), X(.64f), Y(.6f), C(0.08f, 0.32f, 0.14f));
                for (int i = -1; i <= 1; i++) c.Rect(X(i * .28f - .05f), Y(-.66f), X(i * .28f + .05f), Y(.26f), C(0.08f, 0.32f, 0.14f));
                c.Disc(X(-.36f), Y(-.84f), s * .14f, Ink); c.Disc(X(.36f), Y(-.84f), s * .14f, Ink);
                break;
            case "hazard":
                c.Poly(Ink, X(0), Y(.9f), X(.9f), Y(-.7f), X(-.9f), Y(-.7f)); c.Poly(C(0.98f, 0.8f, 0.08f), X(0), Y(.66f), X(.7f), Y(-.58f), X(-.7f), Y(-.58f));
                c.Rect(X(-.08f), Y(-.2f), X(.08f), Y(.36f), Ink); c.Disc(X(0), Y(-.4f), s * .09f, Ink);
                break;
            case "question":
                c.Disc(X(0), Y(0), s * .85f, C(0.42f, 0.16f, 0.6f)); c.Ring(X(0), Y(.26f), s * .26f, s * .15f, white);
                c.Rect(X(-.45f), Y(-.1f), X(-.02f), Y(.24f), C(0.42f, 0.16f, 0.6f));
                c.Rect(X(-.07f), Y(-.24f), X(.08f), Y(.05f), white); c.Disc(X(0), Y(-.5f), s * .1f, white);
                break;
        }
    }

    /// <summary>A reel strip: one column of twelve cells, face 0 at the TOP of the picture.</summary>
    static void PaintReel(int machine)
    {
        const int W = 128, CH = 128, N = CasinoLayout.ReelFaces;
        Color paper = C(0.96f, 0.95f, 0.9f);
        var c = new Canvas(W, CH * N, paper);
        for (int k = 0; k < N; k++)
        {
            float cy = (N - 1 - k + 0.5f) * CH;
            Icon(c, SlotSymbols[machine][FaceOrder[k]], W * .5f, cy, CH * 0.36f);
            c.Rect(0, cy - CH * .5f, W, cy - CH * .5f + 1.5f, C(0.6f, 0.6f, 0.58f));
        }
        c.Each((x, y, col) => { float e = Mathf.Min(x, W - 1 - x) / 14f; col *= Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(e)); col.a = 1; return col; });
        SaveTex("reel_" + machine, c, true, false, 2048);
    }

    /// <summary>The lit belly glass of a machine: its colours, a sunburst and its lead symbols.</summary>
    static void PaintBelly(int machine)
    {
        const int S = 256;
        Color a = SlotColor[machine], b = SlotAccent[machine];
        var c = new Canvas(S, S, a);
        c.Each((x, y, col) =>
        {
            float u = x - 128f, v = y - 128f, ang = Mathf.Atan2(v, u), r = Mathf.Sqrt(u * u + v * v) / 128f;
            bool ray = Mathf.Sin(ang * 12f) > 0f;
            Color o = Color.Lerp(a * (ray ? 1.25f : 0.8f), b, Mathf.Clamp01(0.55f - r) * 0.9f);
            o *= 1.05f - 0.35f * r; o.a = 1; return o;
        });
        c.Frame(6, 6, S - 6, S - 6, 5, b);
        Icon(c, SlotSymbols[machine][0], 128, 132, 62);
        Icon(c, SlotSymbols[machine][1], 46, 52, 26); Icon(c, SlotSymbols[machine][2], 210, 52, 26);
        SaveTex("belly_" + machine, c, false, false, 256);
    }

    // ------------------------------------------------------------------------------------------------ posters

    const int PosterCount = 6;

    static void PaintPoster(int i)
    {
        const int W = 256, H = 384;
        Canvas c;
        switch (i)
        {
            case 0: // the strip at dusk
                c = new Canvas(W, H, Color.black);
                c.Each((x, y, _) => { float t = y / (float)H; var o = Color.Lerp(C(0.95f, 0.5f, 0.15f), C(0.22f, 0.1f, 0.35f), Mathf.Clamp01(t * 1.25f)); o.a = 1; return o; });
                c.Disc(128, 150, 54, C(1f, 0.82f, 0.4f));
                for (int b = 0; b < 9; b++)
                {
                    float x0 = b * 30f - 6f, hgt = 70f + Hash01(b, 4) * 130f, wdt = 22f + Hash01(b, 9) * 16f;
                    c.Rect(x0, 0, x0 + wdt, hgt, C(0.06f, 0.04f, 0.09f));
                    for (int wy = 0; wy < (int)(hgt / 14f) - 1; wy++) for (int wx = 0; wx < 3; wx++)
                        if (Hash01(b * 31 + wx, wy) > 0.45f) c.Rect(x0 + 4 + wx * 6f, 8 + wy * 14f, x0 + 7 + wx * 6f, 14 + wy * 14f, C(1f, 0.85f, 0.4f));
                }
                break;
            case 1: // the four suits
                c = new Canvas(W, H, C(0.84f, 0.78f, 0.62f));
                Suit(c, 0, 76, 280, 46); Suit(c, 1, 180, 280, 46); Suit(c, 2, 76, 110, 46); Suit(c, 3, 180, 110, 46);
                c.Frame(14, 14, W - 14, H - 14, 4, RedInk);
                break;
            case 2: // the founder
                c = new Canvas(W, H, C(0.36f, 0.26f, 0.16f));
                c.Ellipse(128, 200, 100, 150, C(0.62f, 0.5f, 0.34f));
                c.Ellipse(128, 60, 96, 80, C(0.14f, 0.1f, 0.08f));                // shoulders, a dark suit
                c.Poly(C(0.8f, 0.76f, 0.66f), 112, 130, 144, 130, 128, 90);         // shirt front
                c.Poly(C(0.5f, 0.06f, 0.06f), 110, 126, 128, 116, 110, 106); c.Poly(C(0.5f, 0.06f, 0.06f), 146, 126, 128, 116, 146, 106); // bow tie
                Icon(c, "raccoon", 128, 212, 84);
                c.Ellipse(128, 300, 62, 12, C(0.1f, 0.08f, 0.07f)); c.Rect(88, 300, 168, 336, C(0.1f, 0.08f, 0.07f)); // a bowler hat
                break;
            case 3: // the house emblem: a lucky seven in a wreath (lettering is TMP)
                c = new Canvas(W, H, C(0.07f, 0.2f, 0.13f));
                c.Ring(128, 222, 86, 8, Gold); c.Ring(128, 222, 72, 2, Gold);
                for (int k = 0; k < 24; k++) { float a = k * 15f * Mathf.Deg2Rad; c.Disc(128 + Mathf.Cos(a) * 98, 222 + Mathf.Sin(a) * 98, 4, Gold); }
                Icon(c, "seven", 128, 222, 50);
                c.Frame(12, 12, W - 12, H - 12, 3, Gold);
                break;
            case 4: // snake eyes
                c = new Canvas(W, H, C(0.5f, 0.07f, 0.07f));
                c.Each((x, y, col) => { float r = Mathf.Sqrt((x - 128f) * (x - 128f) + (y - 200f) * (y - 200f)) / 240f; col *= 1.2f - r; col.a = 1; return col; });
                Die(c, 92, 232, 50, -14f, 1); Die(c, 172, 160, 50, 18f, 1);
                c.Frame(12, 12, W - 12, H - 12, 3, Cream);
                break;
            default: // an old photograph: the first truck outside the front door
                c = new Canvas(W, H, C(0.7f, 0.62f, 0.46f));
                c.Rect(0, 0, W, 130, C(0.42f, 0.36f, 0.26f));
                c.Rect(30, 130, 226, 300, C(0.5f, 0.44f, 0.32f)); c.Rect(108, 130, 150, 210, C(0.2f, 0.16f, 0.12f)); c.Rect(40, 250, 216, 280, C(0.3f, 0.25f, 0.18f));
                c.Rect(60, 70, 170, 128, C(0.26f, 0.22f, 0.17f)); c.Rect(170, 70, 206, 106, C(0.26f, 0.22f, 0.17f)); c.Disc(86, 66, 16, Ink); c.Disc(180, 66, 16, Ink);
                break;
        }
        // age everything: sepia cast, foxing, a darker edge
        int seed = 40 + i;
        c.Each((x, y, col) =>
        {
            float g = col.r * 0.4f + col.g * 0.45f + col.b * 0.15f;
            col = Color.Lerp(col, new Color(g * 1.05f, g * 0.9f, g * 0.66f), i == 5 || i == 2 ? 0.85f : 0.35f);
            float fox = Noise(x / 26f, y / 26f, 16, seed);
            col *= 0.72f + 0.3f * fox;
            float ex = Mathf.Min(x, W - 1 - x) / 40f, ey = Mathf.Min(y, H - 1 - y) / 40f;
            col *= Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(Mathf.Min(ex, ey)));
            col.a = 1f; return col;
        });
        SaveTex("poster_" + i, c, false, false, 512);
    }

    static void Suit(Canvas c, int suit, float cx, float cy, float s)
    {
        Color col = suit == 0 || suit == 1 ? RedInk : Ink;
        float X(float u) => cx + u * s; float Y(float v) => cy + v * s;
        switch (suit)
        {
            case 0: // heart
                c.Disc(X(-.42f), Y(.3f), s * .46f, col); c.Disc(X(.42f), Y(.3f), s * .46f, col);
                c.Poly(col, X(-.84f), Y(.12f), X(.84f), Y(.12f), X(0), Y(-.9f));
                break;
            case 1: // diamond
                c.Poly(col, X(0), Y(.95f), X(.66f), Y(0), X(0), Y(-.95f), X(-.66f), Y(0));
                break;
            case 2: // spade
                c.Disc(X(-.4f), Y(-.12f), s * .44f, col); c.Disc(X(.4f), Y(-.12f), s * .44f, col);
                c.Poly(col, X(-.8f), Y(.05f), X(.8f), Y(.05f), X(0), Y(.95f));
                c.Poly(col, X(0), Y(-.1f), X(.3f), Y(-.9f), X(-.3f), Y(-.9f));
                break;
            default: // club
                c.Disc(X(0), Y(.42f), s * .4f, col); c.Disc(X(-.44f), Y(-.12f), s * .4f, col); c.Disc(X(.44f), Y(-.12f), s * .4f, col);
                c.Poly(col, X(0), Y(.1f), X(.28f), Y(-.9f), X(-.28f), Y(-.9f));
                break;
        }
    }

    static void Die(Canvas c, float cx, float cy, float s, float deg, int pips)
    {
        float a = deg * Mathf.Deg2Rad, co = Mathf.Cos(a), si = Mathf.Sin(a);
        float[] P(float u, float v) => new[] { cx + (u * co - v * si) * s, cy + (u * si + v * co) * s };
        var p0 = P(-1, -1); var p1 = P(1, -1); var p2 = P(1, 1); var p3 = P(-1, 1);
        c.Poly(C(0.15f, 0.02f, 0.02f), p0[0] + 5, p0[1] - 6, p1[0] + 5, p1[1] - 6, p2[0] + 5, p2[1] - 6, p3[0] + 5, p3[1] - 6);
        c.Poly(Cream, p0[0], p0[1], p1[0], p1[1], p2[0], p2[1], p3[0], p3[1]);
        c.Disc(cx, cy, s * .2f, Ink);
    }
}

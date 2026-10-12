using System;
using System.Collections.Generic;
using UnityEngine;

// The signed-distance painter and the slot symbol art, copied from the retired uGUI art (Casino/UI/CasinoArt.cs) so the 3D
// presenters stand on their own when the legacy screens are deleted. The QA'd drawings are unchanged.
public static partial class CasinoPaint
{
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
}

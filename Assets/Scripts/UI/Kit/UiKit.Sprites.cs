using System;
using UnityEngine;

public static partial class UiKit
{
    /// <summary>
    /// Procedural sprites for the kit (generated once, DontSave, no asset files): outlines, hazard stripes, a tick, a chevron,
    /// mouse icons, the tiny trash-bag cursor, tape, a coffee ring and the colour-blind freshness glyphs (D10: shapes are
    /// sprites, never font characters).
    /// </summary>
    public static class Sprites
    {
        static Sprite roundedOutline, dash, stripes, check, chevron, bag, tape, coffee, ring, disc, diamond, triangle, cross, paperclip;
        static readonly Sprite[] mice = new Sprite[3];

        /// <summary>A 3 px rounded-rect outline, 9-sliced like <see cref="FeelUi.Rounded"/>.</summary>
        public static Sprite RoundedOutline => roundedOutline != null ? roundedOutline : roundedOutline = MakeRoundedOutline();

        /// <summary>A short dash for tiled dashed rules (8 on, 8 off).</summary>
        public static Sprite Dash => dash != null ? dash : dash = MakeDash();

        /// <summary>Hazard stripes (yellow on ink, 45°), tileable. Use with Image.Type.Tiled and a white tint.</summary>
        public static Sprite Stripes => stripes != null ? stripes : stripes = MakeStripes();

        /// <summary>A chunky tick.</summary>
        public static Sprite Check => check != null ? check : check = Shape("UiCheck", 64, (x, y) =>
            Seg(x, y, -0.32f, 0.02f, -0.1f, -0.22f, 0.085f) || Seg(x, y, -0.1f, -0.22f, 0.34f, 0.3f, 0.085f));

        /// <summary>A chevron pointing right (rotate for other directions).</summary>
        public static Sprite Chevron => chevron != null ? chevron : chevron = Shape("UiChevron", 64, (x, y) =>
            Seg(x, y, -0.14f, 0.3f, 0.16f, 0f, 0.09f) || Seg(x, y, 0.16f, 0f, -0.14f, -0.3f, 0.09f));

        /// <summary>A mouse with the left (0), right (1) or middle (2) button filled.</summary>
        public static Sprite Mouse(int button)
        {
            button = Mathf.Clamp(button, 0, 2);
            if (mice[button] != null) return mice[button];
            int b = button;
            return mice[button] = Shape("UiMouse" + b, 64, (x, y) =>
            {
                // body: a rounded capsule 0.56 wide × 0.86 tall
                float hw = 0.27f, hh = 0.42f, r = 0.27f;
                float qx = Mathf.Abs(x) - (hw - r), qy = Mathf.Abs(y) - (hh - r);
                float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                if (d > 0f) return false;
                bool rim = d > -0.06f;
                if (rim) return true;
                bool topHalf = y > 0.02f;
                bool split = Mathf.Abs(x) < 0.03f && topHalf;
                bool seam = Mathf.Abs(y - 0.02f) < 0.03f;
                if (split || seam) return true;
                if (!topHalf) return false;
                if (b == 0) return x < 0f;
                if (b == 1) return x > 0f;
                return Mathf.Abs(x) < 0.09f && y > 0.12f && y < 0.3f;
            });
        }

        /// <summary>The tiny trash-bag focus cursor: a lumpy sack with a tied top (draw in ink, it reads as a silhouette).</summary>
        public static Sprite Bag => bag != null ? bag : bag = Shape("UiBag", 64, (x, y) =>
        {
            // sack: an egg wider at the bottom
            float sy = (y + 0.08f) / 0.36f;
            float sx = x / (0.34f + 0.06f * Mathf.Clamp(-y * 2f, -1f, 1f));
            if (sx * sx + sy * sy <= 1f && y < 0.24f) return true;
            // neck
            if (y >= 0.2f && y <= 0.3f && Mathf.Abs(x) <= 0.06f) return true;
            // two ear knots
            float ex = Mathf.Abs(x) - 0.11f, ey = y - 0.36f;
            return ex * ex / 0.012f + ey * ey / 0.004f <= 1f;
        });

        static Sprite fadeUp;
        /// <summary>A vertical alpha ramp: opaque at the bottom, clear at the top (list "more below" fades).</summary>
        public static Sprite FadeUp => fadeUp != null ? fadeUp : fadeUp = MakeFadeUp();

        static Sprite MakeFadeUp()
        {
            const int W = 4, H = 64;
            var tex = NewTex(W, H, "UiFadeUp");
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float t = 1f - y / (H - 1f);
                byte a = (byte)(Mathf.SmoothStep(0f, 1f, t) * 255f);
                for (int x = 0; x < W; x++) px[y * W + x] = new Color32(255, 255, 255, a);
            }
            return Finish(tex, px, Vector4.zero);
        }

        /// <summary>A strip of masking tape with torn zig-zag ends (white; tint it).</summary>
        public static Sprite Tape => tape != null ? tape : tape = MakeTape();

        /// <summary>A coffee-cup ring stain (white; tint it brown, low alpha).</summary>
        public static Sprite CoffeeRing => coffee != null ? coffee : coffee = Shape("UiCoffee", 128, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float wob = 0.012f * Mathf.Sin(Mathf.Atan2(y, x) * 5f) + 0.008f * Mathf.Sin(Mathf.Atan2(y, x) * 11f + 1f);
            float w = 0.022f + 0.012f * Mathf.Sin(Mathf.Atan2(y, x) * 2f + 0.5f);
            return Mathf.Abs(r - 0.42f - wob) < w;
        });

        /// <summary>A 4 px ring (knob rims, focus rings).</summary>
        public static Sprite Ring => ring != null ? ring : ring = Shape("UiRing", 64, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            return r <= 0.49f && r >= 0.42f;
        });

        /// <summary>A paperclip (white; tint it metal grey).</summary>
        public static Sprite Paperclip => paperclip != null ? paperclip : paperclip = Shape("UiClip", 96, (x, y) =>
        {
            // two nested rounded loops drawn as strokes
            return Loop(x, y, 0f, 0f, 0.13f, 0.42f, 0.035f) || Loop(x, y, 0.025f, -0.06f, 0.075f, 0.3f, 0.035f) && y < 0.2f;
        });

        // ---- freshness glyphs (● fresh, ◆ going late, ▲ late, ✖ stinky)

        public static Sprite Disc => disc != null ? disc : disc = Shape("UiDisc", 64, (x, y) => x * x + y * y <= 0.4f * 0.4f);
        public static Sprite Diamond => diamond != null ? diamond : diamond = Shape("UiDiamond", 64, (x, y) => Mathf.Abs(x) + Mathf.Abs(y) <= 0.46f);
        public static Sprite Triangle => triangle != null ? triangle : triangle = Shape("UiTriangle", 64, (x, y) =>
            y >= -0.36f && y <= 0.42f && Mathf.Abs(x) <= (0.42f - y) * 0.6f);
        public static Sprite Cross => cross != null ? cross : cross = Shape("UiCross", 64, (x, y) =>
            Seg(x, y, -0.32f, -0.32f, 0.32f, 0.32f, 0.11f) || Seg(x, y, -0.32f, 0.32f, 0.32f, -0.32f, 0.11f));

        /// <summary>The colour-blind shape for a freshness stage (0 fresh, 1 late soon, 2 late, 3 stinky).</summary>
        public static Sprite Glyph(int stage) => stage switch
        {
            1 => Diamond,
            2 => Triangle,
            3 => Cross,
            _ => Disc,
        };

        // ------------------------------------------------------------------ makers

        static bool Seg(float x, float y, float ax, float ay, float bx, float by, float halfWidth)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
            float ex = px - dx * t, ey = py - dy * t;
            return ex * ex + ey * ey <= halfWidth * halfWidth;
        }

        static bool Loop(float x, float y, float cx, float cy, float hw, float hh, float stroke)
        {
            float qx = Mathf.Abs(x - cx) - (hw - hw), qy = Mathf.Abs(y - cy) - (hh - hw);
            float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - hw;
            return Mathf.Abs(d) <= stroke * 0.5f;
        }

        static Sprite Shape(string name, int size, Func<float, float, bool> inside)
        {
            var tex = NewTex(size, size, name);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                            if (inside((x + (sx + 0.5f) / 4f) / size - 0.5f, (y + (sy + 0.5f) / 4f) / size - 0.5f)) hits++;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
                }
            return Finish(tex, px, Vector4.zero);
        }

        static Sprite MakeRoundedOutline()
        {
            const int S = 64; const float R = 24f, W = 3f;
            var tex = NewTex(S, S, "UiRoundedOutline");
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - S * 0.5f) - (S * 0.5f - R);
                    float qy = Mathf.Abs(y + 0.5f - S * 0.5f) - (S * 0.5f - R);
                    float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - R;
                    float outer = Mathf.Clamp01(0.5f - d), inner = Mathf.Clamp01(0.5f - (d + W));
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(outer - inner) * 255f));
                }
            return Finish(tex, px, new Vector4(28, 28, 28, 28));
        }

        static Sprite MakeDash()
        {
            const int W = 16, H = 4;
            var tex = NewTex(W, H, "UiDash");
            tex.wrapMode = TextureWrapMode.Repeat;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = new Color32(255, 255, 255, (byte)(x < 9 ? 255 : 0));
            return Finish(tex, px, Vector4.zero);
        }

        static Sprite MakeStripes()
        {
            const int S = 32;
            var tex = NewTex(S, S, "UiStripes");
            tex.wrapMode = TextureWrapMode.Repeat;
            var px = new Color32[S * S];
            Color32 yel = UiTheme.HazardYellow, ink = UiTheme.Ink;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // 4×4 supersampled diagonal bands, 16 px period
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float u = x + (sx + 0.5f) / 4f, v = y + (sy + 0.5f) / 4f;
                            if (Mathf.Repeat(u + v, 16f) < 8f) hits++;
                        }
                    px[y * S + x] = Color32.Lerp(ink, yel, hits / 16f);
                }
            return Finish(tex, px, Vector4.zero);
        }

        static Sprite MakeTape()
        {
            const int W = 128, H = 32;
            var tex = NewTex(W, H, "UiTape");
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float zig = Mathf.Abs(Mathf.Repeat(y / 5f, 2f) - 1f) * 5f; // 0..5 px teeth
                    bool inside = x >= zig && x <= W - 1 - zig;
                    // a faint lengthwise crinkle
                    byte a = (byte)(inside ? 235 + (int)(20f * Mathf.Sin(y * 0.9f + x * 0.05f) * 0.5f) : 0);
                    px[y * W + x] = new Color32(255, 255, 255, a);
                }
            return Finish(tex, px, new Vector4(12, 0, 12, 0));
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
    }
}

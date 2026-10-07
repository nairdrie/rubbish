using System;
using UnityEngine;

/// <summary>
/// Procedural sprites for the main menu and its cards (Docs/UIPass.md §3, §4.6): the spinning bin lid on busy cards, the sad
/// raccoon doodle on error stickers and the fly that buzzes round the title. Generated once (4× supersampled, DontSave), no
/// asset files, the same way <see cref="UiKit.Sprites"/> and <see cref="FeelUi"/> make theirs.
/// </summary>
public static class MenuArt
{
    static Sprite binLid, sadRaccoon, fly, plateBolt;

    /// <summary>A galvanised bin lid seen from above (grey with ring grooves and a handle): the busy spinner.</summary>
    public static Sprite BinLid => binLid != null ? binLid : binLid = Paint("MenuBinLid", 128, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y);
        if (r > 0.48f) return Clear;
        if (r > 0.44f) return Rim;                                   // rolled rim
        if (Mathf.Abs(r - 0.36f) < 0.018f || Mathf.Abs(r - 0.25f) < 0.016f) return Groove; // pressed rings
        if (Mathf.Abs(x) < 0.2f && Mathf.Abs(y) < 0.055f) return Mathf.Abs(y) < 0.03f && Mathf.Abs(x) < 0.17f ? HandleHi : Handle;
        // a soft highlight wedge so the spin reads
        float a = Mathf.Atan2(y, x);
        return Mathf.Repeat(a + 0.6f, Mathf.PI * 2f) < 0.7f ? Lid2 : Lid;
    });

    /// <summary>A sad raccoon doodle in ink with a blue tear (error stickers).</summary>
    public static Sprite SadRaccoon => sadRaccoon != null ? sadRaccoon : sadRaccoon = Paint("MenuSadRaccoon", 128, (x, y) =>
    {
        // y up. Head: an oval; ears: two triangles; mask: a band with eye holes; frown; tear.
        const float hw = 0.34f, hh = 0.28f, cy = -0.06f;
        float ex = x / hw, ey = (y - cy) / hh;
        float head = ex * ex + ey * ey;              // < 1 inside
        // ears
        bool earL = Tri(x, y, -0.33f, 0.1f, -0.2f, 0.22f, -0.3f, 0.36f);
        bool earR = Tri(x, y, 0.33f, 0.1f, 0.2f, 0.22f, 0.3f, 0.36f);
        bool earInL = Tri(x, y, -0.3f, 0.16f, -0.24f, 0.22f, -0.29f, 0.3f);
        bool earInR = Tri(x, y, 0.3f, 0.16f, 0.24f, 0.22f, 0.29f, 0.3f);
        if (head > 1f && !earL && !earR) return Clear;
        if (head > 1f) return earInL || earInR ? InkC : Fur;
        if (head > 0.86f) return InkC;                            // outline
        // eyes (white with a droopy lid) inside the mask
        float lx = Mathf.Abs(x) - 0.12f, ly = y - (cy + 0.06f);
        bool eye = lx * lx / 0.0036f + ly * ly / 0.0036f <= 1f;
        bool pupil = (lx + 0.005f) * (lx + 0.005f) / 0.0009f + (ly + 0.012f) * (ly + 0.012f) / 0.0012f <= 1f;
        bool lid = eye && ly > 0.012f - 0.35f * lx; // droopy outer corners: sad
        if (pupil && !lid) return InkC;
        if (eye) return lid ? Fur2 : White;
        // the bandit mask
        float my = y - (cy + 0.06f);
        if (Mathf.Abs(my) < 0.075f - 0.05f * Mathf.Abs(x) && Mathf.Abs(x) < 0.3f) return InkC;
        // nose
        float nx = x, ny = y - (cy - 0.07f);
        if (nx * nx / 0.0016f + ny * ny / 0.0009f <= 1f) return InkC;
        // frown: an arc opening downward
        float fx = x, fy = y - (cy - 0.2f);
        float fr = Mathf.Sqrt(fx * fx + fy * fy);
        if (fy > 0f && Mathf.Abs(fr - 0.07f) < 0.014f && Mathf.Abs(fx) < 0.065f) return InkC;
        // a light muzzle
        float mz = (x / 0.16f) * (x / 0.16f) + ((y - (cy - 0.12f)) / 0.11f) * ((y - (cy - 0.12f)) / 0.11f);
        if (mz <= 1f) return Muzzle;
        return Fur;
    }, overlay: (x, y) =>
    {
        // a fat blue tear under the left eye (drawn over everything)
        float tx = x + 0.17f, ty = y + 0.02f;
        float d = tx * tx / 0.0016f + (ty + 0.01f) * (ty + 0.01f) / 0.0025f;
        bool drop = d <= 1f || (ty > 0f && ty < 0.07f && Mathf.Abs(tx) < 0.04f * (1f - ty / 0.07f));
        return drop ? Tear : Clear;
    });

    /// <summary>A cartoon fly: ink body, two pale wings (draw it ~40 px; flap by scaling Y).</summary>
    public static Sprite Fly => fly != null ? fly : fly = Paint("MenuFly", 64, (x, y) =>
    {
        float bx = x / 0.13f, by = (y + 0.08f) / 0.17f;
        if (bx * bx + by * by <= 1f) return InkC;
        float hx = x / 0.09f, hy = (y + 0.29f) / 0.08f;
        if (hx * hx + hy * hy <= 1f) return InkC;
        float wl = (x + 0.17f) / 0.17f, wy = (y - 0.12f) / 0.24f;
        float wr = (x - 0.17f) / 0.17f;
        if (wl * wl + wy * wy <= 1f || wr * wr + wy * wy <= 1f) return Wing;
        return Clear;
    });

    /// <summary>A chrome licence-plate bolt.</summary>
    public static Sprite PlateBolt => plateBolt != null ? plateBolt : plateBolt = Paint("MenuBolt", 32, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + y * y);
        if (r > 0.46f) return Clear;
        if (Mathf.Abs(x - y) < 0.06f && r < 0.3f) return Groove;
        return r > 0.38f ? Rim : Lid2;
    });

    // ------------------------------------------------------------------ painter

    static readonly Color32 Clear = new Color32(0, 0, 0, 0);
    static readonly Color32 Rim = new Color32(0x5E, 0x66, 0x70, 255);
    static readonly Color32 Lid = new Color32(0x9A, 0xA3, 0xAE, 255);
    static readonly Color32 Lid2 = new Color32(0xB9, 0xC1, 0xCA, 255);
    static readonly Color32 Groove = new Color32(0x6E, 0x77, 0x82, 255);
    static readonly Color32 Handle = new Color32(0x3B, 0x40, 0x48, 255);
    static readonly Color32 HandleHi = new Color32(0x55, 0x5C, 0x66, 255);
    static readonly Color32 InkC = new Color32(0x1F, 0x1A, 0x2E, 255);
    static readonly Color32 Fur = new Color32(0xA9, 0xA7, 0xB0, 255);
    static readonly Color32 Fur2 = new Color32(0x86, 0x84, 0x8E, 255);
    static readonly Color32 Muzzle = new Color32(0xE8, 0xE4, 0xDC, 255);
    static readonly Color32 White = new Color32(0xFF, 0xFF, 0xFF, 255);
    static readonly Color32 Tear = new Color32(0x4F, 0xA8, 0xF0, 255);
    static readonly Color32 Wing = new Color32(0xD8, 0xEE, 0xFF, 170);

    static bool Tri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
        float d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
        float d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>A square sprite painted by <paramref name="color"/> (x, y in −0.5..0.5, y up), 4×4 supersampled.</summary>
    static Sprite Paint(string name, int size, Func<float, float, Color32> color, Func<float, float, Color32> overlay = null)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
        };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float u = (x + (sx + 0.5f) / 4f) / size - 0.5f, v = (y + (sy + 0.5f) / 4f) / size - 0.5f;
                        Color32 c = color(u, v);
                        if (overlay != null)
                        {
                            var o = overlay(u, v);
                            if (o.a > 0) c = o;
                        }
                        float ca = c.a / 255f;
                        r += c.r * ca; g += c.g * ca; b += c.b * ca; a += ca;
                    }
                // premultiplied average back to straight alpha
                Color32 outC = a > 0f ? new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)(a / 16f * 255f)) : Clear;
                px[y * size + x] = outC;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sp.name = name;
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }
}

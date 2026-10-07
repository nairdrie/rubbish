using System;
using UnityEngine;

/// <summary>
/// Procedural map icons (Docs/UIPass.md §7.3): generated once, DontSave, no asset files. White silhouettes are tinted by
/// the Image colour; the bin-lid frame is drawn in its own galvanised greys. Everything points UP (+y) at rest, so an icon's
/// rotation is its heading.
/// </summary>
public static class MapIcons
{
    static Sprite truck, arrow, arrowOutline, cone, padlock, gateOpen, hatch, lid, handle, fly, exitArrow, pin, tab, house, goblin;

    /// <summary>A top-down garbage truck (cab at the top), white: tint it with the company colour.</summary>
    public static Sprite Truck => truck != null ? truck : truck = Shape("MapTruck", 64, (x, y) =>
    {
        // body (hopper) and cab, with a little gap; wheels stick out
        if (Box(x, y, 0f, -0.08f, 0.2f, 0.3f, 0.05f)) return true;            // body
        if (Box(x, y, 0f, 0.31f, 0.17f, 0.11f, 0.05f)) return true;           // cab
        foreach (float wy in new[] { -0.26f, 0.08f, 0.3f })
            if (Box(x, y, -0.22f, wy, 0.04f, 0.06f, 0.01f) || Box(x, y, 0.22f, wy, 0.04f, 0.06f, 0.01f)) return true;
        return false;
    });

    /// <summary>A chunky arrowhead pointing up (players).</summary>
    public static Sprite Arrow => arrow != null ? arrow : arrow = Shape("MapArrow", 64, (x, y) => InArrow(x, y, 1f));

    /// <summary>A slightly bigger arrowhead for the white outline behind "you".</summary>
    public static Sprite ArrowOutline => arrowOutline != null ? arrowOutline : arrowOutline = Shape("MapArrowOutline", 64, (x, y) => InArrow(x, y, 1.18f));

    static bool InArrow(float x, float y, float s)
    {
        x /= s; y /= s;
        // tip at (0, 0.4), back corners at (±0.3, -0.32), a notch to (0, -0.14)
        if (y > 0.4f || y < -0.32f) return false;
        float half = (0.4f - y) / 0.72f * 0.3f;
        if (Mathf.Abs(x) > half) return false;
        float notchY = -0.14f - Mathf.Abs(x) / 0.3f * 0.18f;
        return y >= notchY;
    }

    /// <summary>A soft 60° wedge pointing up from the centre (the view cone; white, fades with distance).</summary>
    public static Sprite Cone => cone != null ? cone : cone = MakeCone();

    static Sprite MakeCone()
    {
        const int S = 128;
        var tex = NewTex(S, S, "MapCone");
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S - 0.5f, v = (y + 0.5f) / S - 0.5f;
                float r = Mathf.Sqrt(u * u + v * v);
                float ang = Mathf.Abs(Mathf.Atan2(u, v)) * Mathf.Rad2Deg; // 0 = up
                float a = v > 0f && r < 0.5f ? Mathf.Clamp01((32f - ang) / 4f) * Mathf.Clamp01(1f - r / 0.5f) : 0f;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        return Finish(tex, px);
    }

    /// <summary>A padlock (locked district gates).</summary>
    public static Sprite Padlock => padlock != null ? padlock : padlock = Shape("MapPadlock", 64, (x, y) =>
    {
        if (Box(x, y, 0f, -0.12f, 0.26f, 0.2f, 0.06f))
        {
            // keyhole
            float kx = x, ky = y + 0.1f;
            return !(kx * kx + (ky - 0.03f) * (ky - 0.03f) < 0.0025f || (Mathf.Abs(kx) < 0.025f && ky < 0.03f && ky > -0.08f));
        }
        float cy = 0.1f, r = Mathf.Sqrt(x * x + (y - cy) * (y - cy));
        return y >= 0.05f && r <= 0.2f && r >= 0.12f;
    });

    /// <summary>An open gate: two posts with the arm swung up.</summary>
    public static Sprite GateOpen => gateOpen != null ? gateOpen : gateOpen = Shape("MapGateOpen", 64, (x, y) =>
        Box(x, y, -0.28f, -0.05f, 0.06f, 0.32f, 0.02f) || Box(x, y, 0.28f, -0.05f, 0.06f, 0.32f, 0.02f) ||
        Seg(x, y, -0.22f, 0.12f, 0.12f, 0.4f, 0.05f));

    /// <summary>Diagonal ink hatch, tileable (locked districts at 35%).</summary>
    public static Sprite Hatch => hatch != null ? hatch : hatch = MakeHatch();

    static Sprite MakeHatch()
    {
        const int S = 32;
        var tex = NewTex(S, S, "MapHatch");
        tex.wrapMode = TextureWrapMode.Repeat;
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        if (Mathf.Repeat(x + (sx + 0.5f) / 4f - (y + (sy + 0.5f) / 4f), 16f) < 4f) hits++;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
            }
        return Finish(tex, px);
    }

    /// <summary>
    /// The galvanised bin-lid frame (§7.3): an outer rim (#5E6670), a corrugated grey ring (#9AA3AE) and 8 rivets, with a clear
    /// centre for the map. Drawn in colour (tint white).
    /// </summary>
    public static Sprite Lid => lid != null ? lid : lid = MakeLid();

    /// <summary>Inner radius of the lid ring as a fraction of the sprite (the map shows inside it).</summary>
    public const float LidInner = 0.43f;

    static Sprite MakeLid()
    {
        const int S = 256;
        var tex = NewTex(S, S, "MapLid");
        var px = new Color32[S * S];
        Color rim = new Color(0x5E / 255f, 0x66 / 255f, 0x70 / 255f), ring = new Color(0x9A / 255f, 0xA3 / 255f, 0xAE / 255f);
        Color rivet = new Color(0.82f, 0.85f, 0.88f), rivetShade = new Color(0.36f, 0.4f, 0.45f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                Color acc = Color.clear;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                    {
                        float u = (x + (sx + 0.5f) / 3f) / S - 0.5f, v = (y + (sy + 0.5f) / 3f) / S - 0.5f;
                        float r = Mathf.Sqrt(u * u + v * v);
                        Color c = Color.clear;
                        if (r <= 0.5f && r >= LidInner)
                        {
                            if (r > 0.475f) c = rim;
                            else
                            {
                                // corrugations + a light from the top-left
                                float corr = 0.08f * Mathf.Sin(r * 900f);
                                float light = 0.1f * (-u + v) / 0.5f;
                                c = ring * (1f + corr + light);
                                c.a = 1f;
                            }
                            // 8 rivets on the ring
                            float ang = Mathf.Atan2(v, u);
                            for (int k = 0; k < 8; k++)
                            {
                                float a = (k + 0.5f) * Mathf.PI / 4f;
                                float rx = Mathf.Cos(a) * 0.452f, ry = Mathf.Sin(a) * 0.452f;
                                float dr = Mathf.Sqrt((u - rx) * (u - rx) + (v - ry) * (v - ry));
                                if (dr < 0.012f) c = (u - rx) - (v - ry) < 0f ? rivet : Color.Lerp(rivet, rivetShade, 0.5f);
                            }
                            // a dark inner lip
                            if (r < LidInner + 0.006f) c = new Color(0.2f, 0.22f, 0.26f);
                        }
                        acc += c;
                    }
                acc /= 9f;
                px[y * S + x] = acc;
            }
        return Finish(tex, px);
    }

    /// <summary>The lid handle (a rounded bar with two feet): the heading notch at the top of the minimap.</summary>
    public static Sprite Handle => handle != null ? handle : handle = Shape("MapHandle", 64, (x, y) =>
        Box(x, y, 0f, 0.06f, 0.36f, 0.08f, 0.07f) || Box(x, y, -0.3f, -0.1f, 0.05f, 0.12f, 0.02f) || Box(x, y, 0.3f, -0.1f, 0.05f, 0.12f, 0.02f));

    /// <summary>A cartoon fly (body + two wings), for the lid when the city is under 35% clean.</summary>
    public static Sprite Fly => fly != null ? fly : fly = Shape("MapFly", 32, (x, y) =>
    {
        float bx = x / 0.13f, by = (y + 0.08f) / 0.2f;
        if (bx * bx + by * by <= 1f) return true;
        float wx1 = (x + 0.17f) / 0.15f, wy = (y - 0.12f) / 0.22f, wx2 = (x - 0.17f) / 0.15f;
        return wx1 * wx1 + wy * wy <= 1f || wx2 * wx2 + wy * wy <= 1f;
    });

    /// <summary>An "out of town" arrow (exits).</summary>
    public static Sprite ExitArrow => exitArrow != null ? exitArrow : exitArrow = Shape("MapExit", 64, (x, y) =>
        Seg(x, y, 0f, -0.32f, 0f, 0.22f, 0.07f) || Seg(x, y, 0f, 0.36f, -0.24f, 0.1f, 0.07f) || Seg(x, y, 0f, 0.36f, 0.24f, 0.1f, 0.07f));

    /// <summary>A map pin (house cards, "you are here").</summary>
    public static Sprite Pin => pin != null ? pin : pin = Shape("MapPin", 64, (x, y) =>
    {
        float r = Mathf.Sqrt(x * x + (y - 0.12f) * (y - 0.12f));
        if (r <= 0.26f) return r >= 0.1f;
        return y < 0.12f && y > -0.42f && Mathf.Abs(x) <= (y + 0.42f) / 0.54f * 0.24f;
    });

    /// <summary>The little "N" tab that orbits the lid rim.</summary>
    public static Sprite Tab => tab != null ? tab : tab = Shape("MapTab", 64, (x, y) => Box(x, y, 0f, 0f, 0.42f, 0.42f, 0.16f));

    /// <summary>A tiny raccoon face doodle (the paper map's margin; "here be goblins").</summary>
    public static Sprite Goblin => goblin != null ? goblin : goblin = Shape("MapRaccoon", 64, (x, y) =>
    {
        float head = (x / 0.36f) * (x / 0.36f) + ((y + 0.04f) / 0.28f) * ((y + 0.04f) / 0.28f);
        bool ears = Mathf.Sqrt((x + 0.24f) * (x + 0.24f) + (y - 0.22f) * (y - 0.22f)) < 0.1f ||
                    Mathf.Sqrt((x - 0.24f) * (x - 0.24f) + (y - 0.22f) * (y - 0.22f)) < 0.1f;
        if (!(head <= 1f || ears)) return false;
        // the eye mask is cut out as two holes so the tint reads as the mask colour on paper
        bool eyeL = Mathf.Sqrt((x + 0.13f) * (x + 0.13f) + (y - 0.0f) * (y - 0.0f)) < 0.05f;
        bool eyeR = Mathf.Sqrt((x - 0.13f) * (x - 0.13f) + (y - 0.0f) * (y - 0.0f)) < 0.05f;
        return !(eyeL || eyeR);
    });

    /// <summary>A little house silhouette (legend rows).</summary>
    public static Sprite House => house != null ? house : house = Shape("MapHouse", 64, (x, y) =>
        Box(x, y, 0f, -0.14f, 0.3f, 0.22f, 0.03f) || (y >= 0.06f && y <= 0.4f && Mathf.Abs(x) <= (0.4f - y) / 0.34f * 0.38f));

    // ------------------------------------------------------------------ helpers

    static bool Box(float x, float y, float cx, float cy, float hx, float hy, float r)
    {
        float qx = Mathf.Abs(x - cx) - (hx - r), qy = Mathf.Abs(y - cy) - (hy - r);
        return new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r <= 0f;
    }

    static bool Seg(float x, float y, float ax, float ay, float bx, float by, float hw)
    {
        float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
        float t = Mathf.Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
        float ex = px - dx * t, ey = py - dy * t;
        return ex * ex + ey * ey <= hw * hw;
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
        return Finish(tex, px);
    }

    static Texture2D NewTex(int w, int h, string name) =>
        new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
        };

    static Sprite Finish(Texture2D tex, Color32[] px)
    {
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sp.name = tex.name;
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }

    static Sprite Finish(Texture2D tex, Color[] px)
    {
        tex.SetPixels(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sp.name = tex.name;
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }
}

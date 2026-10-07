using UnityEngine;

/// <summary>
/// Pure functions describing the sky over one in-game day (hour 0..24): sun/moon paths, light, palette
/// (ambient, fog, horizon/sun glow) and which two skybox keyframes to crossfade. No state, no scene access —
/// DayNightSky (Neighborhood), GarageSkyLight (ShopScene) and anyone else who wants "what does the sky look
/// like at 19:30" call these. Hours use GameClock's phase constants (dawn 5:00–6:30, dusk 19:00–20:30).
/// </summary>
public static class SkyCycle
{
    // ---------------- sun & moon paths ----------------
    public const float Sunrise = 5.75f;      // sun crosses the horizon (east, +X)
    public const float Sunset = 19.75f;      // sun crosses the horizon (west, -X)
    public const float Moonrise = 19.9f;     // moon comes up in the east right after sunset
    public const float Moonset = 5.6f;       // ...and is gone just before sunrise
    const float SunTiltDeg = 28f;            // noon sun elevation = 90 - tilt = 62 deg (south)
    const float MoonTiltDeg = 57f;           // moon peaks low (33 deg) so it's actually in view while driving

    /// <summary>The skybox keyframes, in the order DayNightSky.skyMaterials is matched by name.</summary>
    public static readonly string[] SkyNames =
    {
        "SkyMidnight", "SkyNight", "SkyMorning", "SkyBrightMorning", "SkyNoon", "SkyAfterNoon", "SkySunset", "SkyEarlyDusk",
    };
    const int SkyMidnight = 0, SkyNight = 1, SkyMorning = 2, SkyBrightMorning = 3, SkyNoon = 4, SkyAfterNoon = 5, SkySunset = 6, SkyEarlyDusk = 7;

    struct SkyKey { public float hour; public int sky; public SkyKey(float h, int s) { hour = h; sky = s; } }

    // Crossfade schedule (wraps around midnight). Holding the same sky in two keys = no blending in between.
    static readonly SkyKey[] SkyKeys =
    {
        new SkyKey(1.0f, SkyMidnight),
        new SkyKey(4.2f, SkyMidnight),
        new SkyKey(5.1f, SkyNight),
        new SkyKey(5.9f, SkyMorning),
        new SkyKey(7.3f, SkyBrightMorning),
        new SkyKey(11.0f, SkyNoon),
        new SkyKey(15.0f, SkyAfterNoon),
        new SkyKey(18.2f, SkyAfterNoon),
        new SkyKey(19.35f, SkySunset),
        new SkyKey(20.05f, SkyEarlyDusk),
        new SkyKey(20.8f, SkyNight),
        new SkyKey(22.8f, SkyNight),
    };

    /// <summary>Which two skyboxes to show at <paramref name="hour"/> and how far to blend from A to B.</summary>
    public static void SkyPair(float hour, out int a, out int b, out float blend)
    {
        hour = Mathf.Repeat(hour, 24f);
        int n = SkyKeys.Length;
        for (int i = 0; i < n; i++)
        {
            var k0 = SkyKeys[i];
            var k1 = SkyKeys[(i + 1) % n];
            float h0 = k0.hour, h1 = k1.hour;
            if (h1 <= h0) h1 += 24f;
            float h = hour;
            if (h < h0) h += 24f;
            if (h >= h0 && h < h1)
            {
                a = k0.sky; b = k1.sky;
                blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(h0, h1, h));
                if (a == b) blend = 0f;
                return;
            }
        }
        a = b = SkyNoon; blend = 0f;
    }

    /// <summary>World direction pointing TO the sun (below the horizon at night).</summary>
    public static Vector3 SunDirection(float hour)
    {
        float t = (Mathf.Repeat(hour, 24f) - Sunrise) / (Sunset - Sunrise);
        return ArcDirection(t * Mathf.PI, SunTiltDeg);
    }

    /// <summary>World direction pointing TO the moon (below the horizon during the day).</summary>
    public static Vector3 MoonDirection(float hour)
    {
        float h = Mathf.Repeat(hour, 24f);
        if (h < 12f) h += 24f;
        float span = Moonset + 24f - Moonrise;
        float t = (h - Moonrise) / span;
        return ArcDirection(t * Mathf.PI, MoonTiltDeg);
    }

    static Vector3 ArcDirection(float theta, float tiltDeg)
    {
        float tilt = tiltDeg * Mathf.Deg2Rad;
        float s = Mathf.Sin(theta);
        // rises at +X (east), culminates toward -Z (south), sets at -X (west)
        return new Vector3(Mathf.Cos(theta), s * Mathf.Cos(tilt), -s * Mathf.Sin(tilt)).normalized;
    }

    public static float ElevationDeg(Vector3 dir) => Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    public static float SunElevationDeg(float hour) => ElevationDeg(SunDirection(hour));
    public static float MoonElevationDeg(float hour) => ElevationDeg(MoonDirection(hour));

    /// <summary>Same curve as GameClock.Daylight01, but for any hour (0 night, 1 day, smooth dawn/dusk).</summary>
    public static float Daylight01(float hour)
    {
        float h = Mathf.Repeat(hour, 24f);
        if (h >= GameClock.DayStart && h <= GameClock.DuskStart) return 1f;
        if (h >= GameClock.NightStart || h < GameClock.DawnStart) return 0f;
        if (h < GameClock.DayStart) return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GameClock.DawnStart, GameClock.DayStart, h));
        return Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(GameClock.DuskStart, GameClock.NightStart, h));
    }

    public static float Darkness01(float hour) => 1f - Daylight01(hour);

    /// <summary>0..1 "golden/blue hour" factor: peaks while the sun is just around the horizon.</summary>
    public static float Twilight01(float hour)
    {
        float e = SunElevationDeg(hour);
        return Smooth(-11f, -2f, e) * (1f - Smooth(5f, 22f, e));
    }

    // ---------------- the main (shadow-casting) directional light ----------------

    /// <summary>
    /// One directional light plays both sun and moon (keeps a single shadow-casting main light in URP): the sun
    /// by day, the moon by night; it swaps sides around 19:55 / 5:40 when both are nearly zero.
    /// </summary>
    public static void MainLight(float hour, out Vector3 dirToLight, out Color color, out float intensity, out bool isMoon)
    {
        Vector3 sd = SunDirection(hour), md = MoonDirection(hour);
        float se = ElevationDeg(sd), me = ElevationDeg(md);
        float sunI = 1.45f * Smooth(-3f, 9f, se);
        float moonI = 0.55f * Smooth(-1.5f, 12f, me) * Smooth(0.4f, 1f, Darkness01(hour));
        isMoon = moonI > sunI;
        if (!isMoon)
        {
            intensity = sunI;
            color = Color.Lerp(new Color(1f, 0.5f, 0.26f), new Color(1f, 0.93f, 0.8f), Smooth(1f, 32f, se));
            dirToLight = LiftDirection(sd, 9f);
        }
        else
        {
            intensity = moonI;
            color = new Color(0.56f, 0.68f, 1f);
            dirToLight = LiftDirection(md, 32f);
        }
    }

    /// <summary>Keeps the light at least <paramref name="minElevationDeg"/> up so shadows stay readable.</summary>
    static Vector3 LiftDirection(Vector3 d, float minElevationDeg)
    {
        float e = ElevationDeg(d);
        if (e >= minElevationDeg) return d;
        Vector3 flat = new Vector3(d.x, 0f, d.z);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.right;
        flat.Normalize();
        float r = minElevationDeg * Mathf.Deg2Rad;
        return (flat * Mathf.Cos(r) + Vector3.up * Mathf.Sin(r)).normalized;
    }

    // ---------------- palette ----------------

    /// <summary>Everything color-ish about the sky at one moment.</summary>
    public struct Look
    {
        public Color ambientSky, ambientEquator, ambientGround;
        public Color fog;
        public float fogStart, fogEnd;
        public Color horizon;      // rgb tint painted on the sky's horizon band, a = amount
        public Color zenith;       // painted gradient (horizon -> zenith) over the sky texture, a = amount
        public Color sunGlow;      // HDR glow around the sun in the sky
        public float exposure;     // skybox exposure
        public float reflection;   // RenderSettings.reflectionIntensity
        /// <summary>Colour of "outdoors" seen through a door/window (garage).</summary>
        public Color doorway;

        public static Look Lerp(in Look a, in Look b, float t)
        {
            return new Look
            {
                ambientSky = Color.Lerp(a.ambientSky, b.ambientSky, t),
                ambientEquator = Color.Lerp(a.ambientEquator, b.ambientEquator, t),
                ambientGround = Color.Lerp(a.ambientGround, b.ambientGround, t),
                fog = Color.Lerp(a.fog, b.fog, t),
                fogStart = Mathf.Lerp(a.fogStart, b.fogStart, t),
                fogEnd = Mathf.Lerp(a.fogEnd, b.fogEnd, t),
                horizon = Color.Lerp(a.horizon, b.horizon, t),
                zenith = Color.Lerp(a.zenith, b.zenith, t),
                sunGlow = Color.Lerp(a.sunGlow, b.sunGlow, t),
                exposure = Mathf.Lerp(a.exposure, b.exposure, t),
                reflection = Mathf.Lerp(a.reflection, b.reflection, t),
                doorway = Color.Lerp(a.doorway, b.doorway, t),
            };
        }
    }

    struct LookKey { public float hour; public Look look; }

    static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);

    static LookKey K(float hour, Color sky, Color eq, Color ground, Color fog, float fogStart, float fogEnd,
                     Color horizon, Color zenith, Color sunGlow, float exposure, float reflection, Color doorway)
    {
        return new LookKey
        {
            hour = hour,
            look = new Look
            {
                ambientSky = sky, ambientEquator = eq, ambientGround = ground, fog = fog, fogStart = fogStart, fogEnd = fogEnd,
                horizon = horizon, zenith = zenith, sunGlow = sunGlow, exposure = exposure, reflection = reflection, doorway = doorway,
            }
        };
    }

    // Night is deliberately a bright-ish cartoon blue so bags and the road stay readable.
    static readonly LookKey[] LookKeys =
    {
        //   hour    ambient sky              ambient equator          ambient ground           fog                      start  end    horizon (a = amount)             zenith gradient (a = amount)     sun glow (HDR)          exp    refl   doorway
        K(0.0f,  C(0.24f, 0.29f, 0.52f), C(0.16f, 0.19f, 0.34f), C(0.08f, 0.09f, 0.13f), C(0.06f, 0.08f, 0.19f), 45f, 330f, C(0.10f, 0.14f, 0.32f, 0.35f), C(0.02f, 0.03f, 0.12f, 0.00f), C(0f, 0f, 0f),          1.00f, 0.25f, C(0.28f, 0.36f, 0.75f)),
        K(4.6f,  C(0.24f, 0.29f, 0.52f), C(0.16f, 0.19f, 0.34f), C(0.08f, 0.09f, 0.13f), C(0.06f, 0.08f, 0.19f), 45f, 330f, C(0.10f, 0.14f, 0.32f, 0.35f), C(0.05f, 0.06f, 0.20f, 0.00f), C(0f, 0f, 0f),          1.00f, 0.25f, C(0.28f, 0.36f, 0.75f)),
        K(5.35f, C(0.34f, 0.33f, 0.54f), C(0.40f, 0.29f, 0.38f), C(0.11f, 0.10f, 0.12f), C(0.34f, 0.27f, 0.40f), 60f, 380f, C(0.95f, 0.50f, 0.42f, 0.45f), C(0.12f, 0.14f, 0.36f, 0.35f), C(0.9f, 0.35f, 0.15f),  1.00f, 0.40f, C(0.85f, 0.55f, 0.55f)),
        K(6.2f,  C(0.58f, 0.58f, 0.66f), C(0.80f, 0.60f, 0.46f), C(0.22f, 0.19f, 0.14f), C(0.88f, 0.70f, 0.56f), 90f, 460f, C(1.00f, 0.66f, 0.42f, 0.35f), C(0.40f, 0.55f, 0.85f, 0.25f), C(1.3f, 0.6f, 0.25f),  0.95f, 0.75f, C(1.15f, 0.85f, 0.60f)),
        K(7.6f,  C(0.66f, 0.76f, 0.92f), C(0.70f, 0.70f, 0.66f), C(0.27f, 0.26f, 0.21f), C(0.74f, 0.82f, 0.90f), 120f, 520f, C(1.00f, 0.85f, 0.70f, 0.05f), C(0.45f, 0.62f, 0.90f, 0.00f), C(0.35f, 0.30f, 0.2f), 1.00f, 1.00f, C(1.25f, 1.15f, 1.00f)),
        K(12.0f, C(0.68f, 0.78f, 0.95f), C(0.68f, 0.71f, 0.70f), C(0.27f, 0.27f, 0.23f), C(0.74f, 0.82f, 0.90f), 120f, 520f, C(1.00f, 1.00f, 1.00f, 0.00f), C(0.45f, 0.62f, 0.90f, 0.00f), C(0.25f, 0.24f, 0.2f), 1.00f, 1.00f, C(1.30f, 1.25f, 1.15f)),
        K(17.6f, C(0.68f, 0.75f, 0.90f), C(0.72f, 0.70f, 0.64f), C(0.27f, 0.25f, 0.21f), C(0.78f, 0.80f, 0.84f), 120f, 520f, C(1.00f, 0.80f, 0.60f, 0.08f), C(0.40f, 0.52f, 0.85f, 0.10f), C(0.45f, 0.32f, 0.2f), 1.00f, 1.00f, C(1.25f, 1.10f, 0.90f)),
        K(19.0f, C(0.62f, 0.54f, 0.64f), C(0.92f, 0.60f, 0.42f), C(0.25f, 0.19f, 0.13f), C(0.92f, 0.62f, 0.46f), 100f, 480f, C(1.00f, 0.58f, 0.30f, 0.45f), C(0.30f, 0.38f, 0.75f, 0.55f), C(1.7f, 0.70f, 0.25f), 1.00f, 0.85f, C(1.30f, 0.75f, 0.45f)),
        K(19.8f, C(0.46f, 0.38f, 0.58f), C(0.70f, 0.42f, 0.44f), C(0.15f, 0.12f, 0.13f), C(0.58f, 0.36f, 0.44f), 80f, 420f, C(1.00f, 0.45f, 0.38f, 0.60f), C(0.20f, 0.17f, 0.48f, 0.85f), C(1.4f, 0.45f, 0.2f),  0.95f, 0.60f, C(1.00f, 0.50f, 0.50f)),
        K(20.6f, C(0.28f, 0.30f, 0.56f), C(0.20f, 0.21f, 0.38f), C(0.09f, 0.09f, 0.14f), C(0.12f, 0.13f, 0.30f), 55f, 360f, C(0.34f, 0.20f, 0.46f, 0.45f), C(0.05f, 0.06f, 0.24f, 0.55f), C(0.2f, 0.06f, 0.08f), 1.00f, 0.35f, C(0.40f, 0.40f, 0.85f)),
        K(22.0f, C(0.24f, 0.29f, 0.52f), C(0.16f, 0.19f, 0.34f), C(0.08f, 0.09f, 0.13f), C(0.06f, 0.08f, 0.19f), 45f, 330f, C(0.10f, 0.14f, 0.32f, 0.35f), C(0.02f, 0.03f, 0.12f, 0.00f), C(0f, 0f, 0f),          1.00f, 0.25f, C(0.28f, 0.36f, 0.75f)),
    };

    /// <summary>The palette at <paramref name="hour"/> (smoothly interpolated between keys, wraps at midnight).</summary>
    public static Look LookAt(float hour)
    {
        hour = Mathf.Repeat(hour, 24f);
        int n = LookKeys.Length;
        for (int i = 0; i < n; i++)
        {
            var k0 = LookKeys[i];
            var k1 = LookKeys[(i + 1) % n];
            float h0 = k0.hour, h1 = k1.hour;
            if (h1 <= h0) h1 += 24f;
            float h = hour;
            if (h < h0) h += 24f;
            if (h >= h0 && h < h1)
                return Look.Lerp(k0.look, k1.look, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(h0, h1, h)));
        }
        return LookKeys[0].look;
    }

    /// <summary>Stars fade in once it's properly dark.</summary>
    public static float StarAmount(float hour) => Smooth(0.45f, 0.95f, Darkness01(hour));

    /// <summary>The moon disc fades in as it clears the horizon (it's visible at dusk too).</summary>
    public static float MoonAmount(float hour) => Smooth(-4f, 2f, MoonElevationDeg(hour));

    static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));
}

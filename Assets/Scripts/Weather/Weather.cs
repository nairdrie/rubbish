using System;
using UnityEngine;

/// <summary>The four weather states (Docs/Multiplayer.md §14.5). The host picks; everyone shows the same.</summary>
public enum WeatherKind : byte { Clear = 0, Windy = 1, Rain = 2, Storm = 3 }

/// <summary>
/// Package F5 weather (Docs/Multiplayer.md §14.5): the read-only facade every system uses. The host (or solo) chooses the
/// state in <see cref="WeatherNet"/>; this class turns (kind, change time, seed) into the numbers other packages read, the
/// same on every machine without streaming anything per frame:
/// <list type="bullet">
/// <item><see cref="Wetness"/> 0..1: eases in over ~40 s of rain and dries over ~90 s.</item>
/// <item><see cref="Wind"/>: horizontal wind (m/s) with gusts, a pure function of the server clock and the state's seed.</item>
/// <item><see cref="GripDrainMul"/>: F1 multiplies the rider's grip drain by this (1 dry, 1.35 rain, 1.7 storm).</item>
/// <item><see cref="Slip"/>: F3's slipperiness scale for players and trucks (0 dry, ~0.5 rain, ~0.8 storm).</item>
/// </list>
/// Nothing here touches gameplay state; it's all derived (the host's choice is the only input).
/// </summary>
public static class Weather
{
    // ------------------------------------------------------------------ tuning (one place, rule 12)
    public const float WetInSeconds = 40f, DryOutSeconds = 90f, WindRampSeconds = 12f;
    /// <summary>Base wind (m/s) and gust peak (m/s) by kind.</summary>
    static readonly float[] BaseWind = { 0.6f, 4.5f, 1.8f, 6.5f };
    static readonly float[] GustWind = { 0.6f, 9.0f, 3.0f, 13.0f };
    /// <summary>Rain intensity 0..1 by kind (drops on screen, wet target).</summary>
    static readonly float[] RainAmount = { 0f, 0f, 0.65f, 1f };
    /// <summary>Overcast 0..1 by kind (darker light, greyer fog).</summary>
    static readonly float[] OvercastAmount = { 0f, 0.15f, 0.55f, 0.85f };

    // ------------------------------------------------------------------ state (set by WeatherNet on every machine)
    static WeatherKind kind = WeatherKind.Clear, prevKind = WeatherKind.Clear;
    static double changedAt = -1e9;
    static uint seed = 1;
    static int serial;

    /// <summary>The current state (host-chosen).</summary>
    public static WeatherKind Kind => kind;
    /// <summary>Bumped each time the host changes the weather.</summary>
    public static int Serial => serial;
    /// <summary>Raised on every machine when the replicated state changes (old, new).</summary>
    public static event Action<WeatherKind, WeatherKind> Changed;

    /// <summary>Optional override of <see cref="IsLight"/> (true = light enough for the wind). Null = F3's weight class.</summary>
    public static Func<Carryable, bool> IsLightOverride;

    /// <summary>WeatherNet applies the host's word (also solo). Idempotent.</summary>
    internal static void Apply(WeatherKind k, double at, uint s, int ser)
    {
        if (k == kind && ser == serial && s == seed) return;
        var old = kind;
        prevKind = kind;
        kind = k;
        changedAt = at;
        seed = s == 0 ? 1u : s;
        serial = ser;
        if (old != k)
        {
            try { Changed?.Invoke(old, k); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    internal static void ResetLocal()
    {
        kind = prevKind = WeatherKind.Clear;
        changedAt = -1e9;
        seed = 1;
        serial = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        ResetLocal();
        Changed = null;
        IsLightOverride = null;
    }

    /// <summary>The shared clock (server time; seconds). Solo and offline: unscaled realtime.</summary>
    public static double Now => WeatherNet.ClockNow();

    static float Since => (float)Math.Max(0.0, Now - changedAt);

    /// <summary>0..1 how far the change to <see cref="Kind"/> has blended in (12 s).</summary>
    public static float Blend => Mathf.Clamp01(Since / WindRampSeconds);

    /// <summary>Rain intensity 0..1 right now (eases between states).</summary>
    public static float Rain => Mathf.Lerp(RainAmount[(int)prevKind], RainAmount[(int)kind], Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Since / 8f)));

    /// <summary>Overcast 0..1 right now.</summary>
    public static float Overcast => Mathf.Lerp(OvercastAmount[(int)prevKind], OvercastAmount[(int)kind], Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Since / 20f)));

    /// <summary>How wet the ground is, 0..1: up over ~40 s of rain, down over ~90 s after it stops.</summary>
    public static float Wetness
    {
        get
        {
            float tgt = RainAmount[(int)kind] > 0f ? 1f : 0f;
            float from = RainAmount[(int)prevKind] > 0f ? 1f : 0f;
            if (tgt == from) return tgt;
            float t = Since / (tgt > from ? WetInSeconds : DryOutSeconds);
            return Mathf.Lerp(from, tgt, Mathf.Clamp01(t));
        }
    }

    /// <summary>F1: multiply rider grip drain by this.</summary>
    public static float GripDrainMul => 1f + Wetness * (kind == WeatherKind.Storm ? 0.7f : 0.35f) + (kind == WeatherKind.Clear ? 0f : GustStrength01 * 0.25f);

    /// <summary>F3: slipperiness scale 0..1 for players and trucks.</summary>
    public static float Slip => Wetness * (kind == WeatherKind.Storm ? 0.8f : 0.5f);

    /// <summary>Weather applies here: the town is loaded and the point isn't inside a shop pocket (solo shop: never).</summary>
    public static bool IsOutdoors(Vector3 p) => WorldLifeRunner.IsAwake && p.y > -5f && DepotRowLayout.InteriorAt(p) < 0;

    /// <summary><see cref="Slip"/> at a point (0 indoors).</summary>
    public static float SlipAt(Vector3 p) => IsOutdoors(p) ? Slip : 0f;

    /// <summary><see cref="GripDrainMul"/> at a point (1 indoors).</summary>
    public static float GripDrainMulAt(Vector3 p) => IsOutdoors(p) ? GripDrainMul : 1f;

    /// <summary>Wind direction (unit, horizontal): slowly veers, same on every machine.</summary>
    public static Vector3 WindDir
    {
        get
        {
            double t = Now;
            float a = (seed % 360u) + 25f * Mathf.Sin((float)(t * 0.021 + seed * 0.37)) + 10f * Mathf.Sin((float)(t * 0.093));
            float r = a * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }
    }

    /// <summary>0..1 how hard it's gusting right now (shared: a function of the server clock and seed).</summary>
    public static float GustStrength01
    {
        get
        {
            double t = Now + (seed & 0xFFF) * 0.731;
            // Two slow waves and a sharper "gust" pulse every ~6–11 s.
            float w = 0.5f + 0.25f * Mathf.Sin((float)(t * 0.41)) + 0.25f * Mathf.Sin((float)(t * 0.173 + 1.3));
            float ph = (float)(t / 8.3 % 1.0);
            float pulse = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(ph - 0.25f) * 6f), 2f);
            return Mathf.Clamp01(w * 0.55f + pulse * 0.75f);
        }
    }

    /// <summary>Wind speed (m/s) right now.</summary>
    public static float WindSpeed
    {
        get
        {
            float k = Blend;
            float b = Mathf.Lerp(BaseWind[(int)prevKind], BaseWind[(int)kind], k);
            float g = Mathf.Lerp(GustWind[(int)prevKind], GustWind[(int)kind], k);
            return Mathf.Lerp(b, g, GustStrength01);
        }
    }

    /// <summary>Horizontal wind vector (m/s).</summary>
    public static Vector3 Wind => WindDir * WindSpeed;

    /// <summary>Light enough for the wind to push (F3's weight class when it's wired; else by mass).</summary>
    public static bool IsLight(Carryable c)
    {
        if (c == null) return false;
        var f = IsLightOverride;
        if (f != null)
        {
            try { return f(c); }
            catch (Exception e) { Debug.LogException(e); }
        }
        // F3's weight classes (Trash/Tells): heavy bags (the tell, or ≥ 15 kg) never blow; the wind's drag vs. mass does the rest.
        return BagTells.WeightClass(c) != BagWeightClass.Heavy;
    }

    public static string Label(WeatherKind k) => k switch
    {
        WeatherKind.Windy => "Windy",
        WeatherKind.Rain => "Rain",
        WeatherKind.Storm => "Storm",
        _ => "Clear",
    };

    /// <summary>A one-line HUD/bot summary.</summary>
    public static string Describe() =>
        $"{Label(kind)} #{serial} wet {Wetness:0.00} wind {WindSpeed:0.0} m/s gust {GustStrength01:0.00} grip×{GripDrainMul:0.00} slip {Slip:0.00}";
}

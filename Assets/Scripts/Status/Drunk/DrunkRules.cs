using UnityEngine;

/// <summary>How drunk a player is, in the steps the game reacts to (Docs/Casino.md §5).</summary>
public enum DrunkStage : byte { Sober = 0, Tipsy = 1, Drunk = 2, Wasted = 3 }

/// <summary>
/// The pure maths of being drunk (Docs/Casino.md §5). No scene access, no dice of its own: the pass-out roll is made by the
/// caller against <see cref="PassOutChance"/>. The level is drink units, 0 (sober) to <see cref="MaxLevel"/>.
/// </summary>
public static class DrunkRules
{
    // ---- the level
    /// <summary>The level cannot go above this; at it, passing out is certain.</summary>
    public const float MaxLevel = 1.2f;
    /// <summary>Drink units that wear off per in-game hour.</summary>
    public const float DecayPerGameHour = 0.25f;
    /// <summary>Stage thresholds (the level a stage starts at).</summary>
    public const float TipsyAt = 0.2f, DrunkAt = 0.5f, WastedAt = 0.8f;
    /// <summary>The level you wake at after passing out: hung over, not sober.</summary>
    public const float WakeLevel = 0.25f;
    /// <summary>Solo, passing out wakes you at the garage and the cab home costs this ($40).</summary>
    public const int CabFareCents = 4000;
    /// <summary>Co-op, you flop where you stand for at most this long (friends can carry you).</summary>
    public const float CoopFlopSeconds = 30f;

    public static float Clamp(float level) => float.IsNaN(level) ? 0f : Mathf.Clamp(level, 0f, MaxLevel);

    /// <summary>The level after a drink of <paramref name="units"/> (water is negative and sobers).</summary>
    public static float AfterDrink(float level, float units) => Clamp(Clamp(level) + units);

    /// <summary>The level after <paramref name="gameHoursElapsed"/> in-game hours without a drink. Time never runs backwards.</summary>
    public static float Decay(float level, float gameHoursElapsed) =>
        Clamp(Clamp(level) - DecayPerGameHour * Mathf.Max(0f, gameHoursElapsed));

    /// <summary>In-game hours until a level has worn off completely.</summary>
    public static float GameHoursToSober(float level) => Clamp(level) / DecayPerGameHour;

    public static DrunkStage Stage(float level)
    {
        level = Clamp(level);
        return level >= WastedAt ? DrunkStage.Wasted : level >= DrunkAt ? DrunkStage.Drunk : level >= TipsyAt ? DrunkStage.Tipsy : DrunkStage.Sober;
    }

    // ---- how strongly each effect shows, 0..1 (the presenters scale their own amplitudes by these)
    /// <summary>Camera sway, blur and double vision. Starts with the first sip's buzz, full when legless.</summary>
    public static float Vision01(float level) => Ramp(level, 0.1f, 1.0f);
    /// <summary>Drift, stumbles and the slower walk. Nothing until Tipsy.</summary>
    public static float Walk01(float level) => Ramp(level, TipsyAt, 1.0f);
    /// <summary>Steering wobble and lag. Driving goes wrong sooner than walking and is at its worst from Wasted.</summary>
    public static float Drive01(float level) => Ramp(level, 0.1f, WastedAt);

    /// <summary>Smoothstep from 0 at <paramref name="from"/> to 1 at <paramref name="to"/>: no pop at either end.</summary>
    static float Ramp(float level, float from, float to)
    {
        float t = Mathf.Clamp01((Clamp(level) - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    // ---- passing out
    /// <summary>Sets how fast the pass-out risk builds: the mean wait is this many seconds at x = 0.5 (level 1.0).</summary>
    public const float PassOutSeconds = 40f;

    /// <summary>
    /// Pass-outs per second at a level: 0 below Wasted, then x / (40 s × (1 − x)) with x = (level − 0.8) / 0.4, which has no
    /// ceiling as the level nears <see cref="MaxLevel"/>.
    /// </summary>
    public static float PassOutRate(float level)
    {
        float x = (Clamp(level) - WastedAt) / (MaxLevel - WastedAt);
        if (x <= 0f) return 0f;
        if (x >= 1f) return float.PositiveInfinity;
        return x / (PassOutSeconds * (1f - x));
    }

    /// <summary>
    /// The chance (0..1) that a check made every <paramref name="checkIntervalSeconds"/> real seconds knocks the player out:
    /// 1 − exp(−rate × interval), so the odds per minute do not depend on how often the caller checks. Zero below Wasted
    /// (0.8), rising, and exactly 1 at 1.2. Expected time to pass out if the level held still: about 120 s at 0.9, 40 s at
    /// 1.0 and 13 s at 1.1.
    /// </summary>
    public static float PassOutChance(float level, float checkIntervalSeconds)
    {
        float rate = PassOutRate(level);
        if (rate <= 0f || checkIntervalSeconds <= 0f) return 0f;
        if (float.IsPositiveInfinity(rate)) return 1f;
        return Mathf.Clamp01(1f - Mathf.Exp(-rate * checkIntervalSeconds));
    }

    /// <summary>Mean real seconds until a pass-out at a level that holds still (infinity below Wasted, 0 at the maximum).</summary>
    public static float ExpectedSecondsToPassOut(float level)
    {
        float rate = PassOutRate(level);
        return rate <= 0f ? float.PositiveInfinity : float.IsPositiveInfinity(rate) ? 0f : 1f / rate;
    }
}

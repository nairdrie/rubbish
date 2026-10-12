using UnityEngine;

/// <summary>
/// The sort-streak ladder (Docs/Progression.md §3.6): consecutive correct HAND sorts climb x1 → x1.5 → x2 → x3 → x4 → x5 → x10.
/// Pure data and maths (no state, no clock): <see cref="SortCombo"/> keeps the streak, SortStreakHud draws it and the tests pin it.
/// <para><b>Tiers.</b> Tier 0 is a cold streak (1-2 sorts, x1). Each later tier starts at a fixed streak and pays a fixed
/// multiplier; the last one (TRASH GOD, x10) needs 35 right bins in a row, about four bags without a single mistake.</para>
/// <para><b>Speed.</b> Every tier has a window: the seconds you may leave between two correct sorts. The hotter the streak the
/// tighter the window (3.0 s cold, 1.8 s at x10), so a slow sorter can hold x3 but only a fast one lives at x10. Wrong bin =
/// the streak is gone; running out of window only cools it one tier (<see cref="Cool"/>), so fetching the next bag costs you
/// a step, not the lot.</para>
/// </summary>
public static class SortLadder
{
    public const int MaxTier = 6;

    // streak at which each tier begins, the pay multiplier, and the seconds allowed between correct sorts
    static readonly int[] startsAt = { 0, 3, 6, 10, 15, 22, 35 };
    static readonly float[] multiplier = { 1f, 1.5f, 2f, 3f, 4f, 5f, 10f };
    static readonly float[] window = { Economy.ComboWindow, 2.8f, 2.6f, 2.4f, 2.2f, 2.0f, 1.8f };
    static readonly string[] names = { "", "NICE", "HOT", "ON FIRE", "UNSTOPPABLE", "LEGENDARY", "TRASH GOD" };

    /// <summary>Tier 0..<see cref="MaxTier"/> a streak of this length is in.</summary>
    public static int TierFor(int streak)
    {
        int tier = 0;
        for (int i = 1; i < startsAt.Length; i++)
            if (streak >= startsAt[i]) tier = i;
        return tier;
    }

    /// <summary>Pay multiplier of a streak (1 for a cold or empty streak).</summary>
    public static float MultiplierFor(int streak) => multiplier[TierFor(streak)];

    public static float Multiplier(int tier) => multiplier[Mathf.Clamp(tier, 0, MaxTier)];

    /// <summary>First streak of a tier (0 for tier 0).</summary>
    public static int StartOf(int tier) => startsAt[Mathf.Clamp(tier, 0, MaxTier)];

    /// <summary>Streak that reaches the next tier, or -1 at the top.</summary>
    public static int NextTierAt(int streak)
    {
        int tier = TierFor(streak);
        return tier >= MaxTier ? -1 : startsAt[tier + 1];
    }

    /// <summary>"NICE", "HOT", "ON FIRE", "UNSTOPPABLE", "LEGENDARY", "TRASH GOD" (empty for tier 0).</summary>
    public static string Name(int tier) => names[Mathf.Clamp(tier, 0, MaxTier)];

    /// <summary>Seconds a streak of this tier may sit between correct sorts before it cools a tier.</summary>
    public static float Window(int tier) => window[Mathf.Clamp(tier, 0, MaxTier)];

    /// <summary>Window of the tier a streak is in.</summary>
    public static float WindowForStreak(int streak) => window[TierFor(streak)];

    /// <summary>
    /// What a streak becomes after <paramref name="idleSeconds"/> without a correct sort: every full window that passes cools it
    /// to the start of the tier below (tier 1 and 0 fall to nothing). <paramref name="idleLeft"/> is the time already spent in the
    /// window of the streak that remains (0 when it is gone).
    /// </summary>
    public static int Cool(int streak, float idleSeconds, out float idleLeft)
    {
        idleLeft = Mathf.Max(0f, idleSeconds);
        while (streak > 0)
        {
            int tier = TierFor(streak);
            float w = window[tier];
            if (idleLeft <= w) return streak;
            idleLeft -= w;
            streak = tier == 0 ? 0 : startsAt[tier - 1];
        }
        idleLeft = 0f;
        return 0;
    }

    public static int Cool(int streak, float idleSeconds) => Cool(streak, idleSeconds, out _);
}

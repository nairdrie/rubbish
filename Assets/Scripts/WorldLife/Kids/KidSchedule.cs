using System;
using UnityEngine;

/// <summary>
/// WorldLife kids (Docs/WorldLife.md §12.K): when each kid is outside. A pure function of (Town seed, pen, kid, day), so
/// every machine agrees with no sync (§2.1). After school, ~15:00 to ~19:00 (a few minutes' jitter each end; about one
/// afternoon in eight a kid has piano lessons and stays in), plus the odd no-school morning (~2 days in 7; ~08:30 to
/// ~11:30, three kids in four come out). Never between 19:00 and 08:30: kids are indoors at night.
/// One in-game hour is 50 real seconds, so an afternoon outside is ~3 real minutes.
/// </summary>
public static class KidSchedule
{
    public const float AfternoonFrom = 15f, AfternoonTo = 19f, MorningFrom = 8.5f, MorningTo = 11.5f;
    /// <summary>Minutes of jitter at each end of a window (the start is late by up to this, the end early).</summary>
    public const int StartJitterMin = 45, EndJitterMin = 30;
    /// <summary>Integer odds: an afternoon out (22 in 25), a no-school morning for the whole town (2 in 7), a kid out on one (3 in 4).</summary>
    public const int AfternoonNum = 22, AfternoonDen = 25, MorningDayNum = 2, MorningDayDen = 7, MorningKidNum = 3, MorningKidDen = 4;

    /// <summary>Kid <paramref name="kid"/> of pen <paramref name="penKey"/> (LifeHash.Str(penId)) on <paramref name="day"/>:
    /// the morning and afternoon windows in hours of the day (from == to = none).</summary>
    public static void Windows(ulong townSeed, uint penKey, int kid, int day, out float morningFrom, out float morningTo, out float afternoonFrom, out float afternoonTo)
    {
        ulong state = LifeHash.U64(townSeed, LifeKind.Kid, kid, day, penKey);
        bool afternoon = LifeHash.Chance(LifeHash.Next(ref state), AfternoonNum, AfternoonDen);
        afternoonFrom = AfternoonFrom + LifeHash.Range(LifeHash.Next(ref state), 0, StartJitterMin + 1) / 60f;
        afternoonTo = AfternoonTo - LifeHash.Range(LifeHash.Next(ref state), 0, EndJitterMin + 1) / 60f;
        if (!afternoon) afternoonFrom = afternoonTo = 0f;
        bool morning = NoSchoolMorning(townSeed, day) && LifeHash.Chance(LifeHash.Next(ref state), MorningKidNum, MorningKidDen);
        morningFrom = MorningFrom + LifeHash.Range(LifeHash.Next(ref state), 0, StartJitterMin + 1) / 60f;
        morningTo = MorningTo - LifeHash.Range(LifeHash.Next(ref state), 0, EndJitterMin + 1) / 60f;
        if (!morning) morningFrom = morningTo = 0f;
    }

    /// <summary>Is <paramref name="day"/> a no-school morning in this Town (the same for every kid)?</summary>
    public static bool NoSchoolMorning(ulong townSeed, int day) =>
        LifeHash.Chance(LifeHash.U64(townSeed, LifeKind.Kid, 0xFFFF, day, 0x5C4Fu), MorningDayNum, MorningDayDen);

    /// <summary>Is the kid outside at GameClock.TotalHours = <paramref name="totalHours"/>?</summary>
    public static bool IsOut(ulong townSeed, uint penKey, int kid, double totalHours)
    {
        int day = (int)Math.Floor(totalHours / 24.0);
        float hod = (float)(totalHours - day * 24.0);
        Windows(townSeed, penKey, kid, day, out float m0, out float m1, out float a0, out float a1);
        return (hod >= m0 && hod < m1) || (hod >= a0 && hod < a1);
    }

    /// <summary>"15:12–18:41" style text for reports.</summary>
    public static string Describe(ulong townSeed, uint penKey, int kid, int day)
    {
        Windows(townSeed, penKey, kid, day, out float m0, out float m1, out float a0, out float a1);
        string W(float a, float b) => a >= b ? "-" : $"{Hm(a)}-{Hm(b)}";
        return $"am {W(m0, m1)} pm {W(a0, a1)}";
    }

    static string Hm(float h) { int m = Mathf.RoundToInt(h * 60f); return $"{m / 60:00}:{m % 60:00}"; }
}

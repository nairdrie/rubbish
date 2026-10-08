using System;
using UnityEngine;

/// <summary>Clock-derived crossing choreography. No RNG state, money, saves or network ownership.</summary>
public static class GuardedCrossingRules
{
    public const float CycleSeconds = 180f, GatherSeconds = 4f, GuardEnterSeconds = 3f,
        WalkSpeed = 1.7f, KidSpacingSeconds = .85f, GuardLeaveSeconds = 3f, DepartSeconds = 5f;
    public const float FirstHour = 7.3f, LastHour = 18.3f, StopMargin = 1.8f;
    public static float CrossEnd(float length, int children) => GatherSeconds + GuardEnterSeconds +
        Mathf.Max(1f, length) / WalkSpeed + (Mathf.Clamp(children, 3, 5) - 1) * KidSpacingSeconds;
    public static float Duration(float length, int children) => CrossEnd(length, children) + GuardLeaveSeconds + DepartSeconds;

    public static bool TryEvent(double hours, ulong seed, int site, int siteCount, float length,
        out long serial, out float age, out int children)
    {
        serial = -1; age = 0f; children = 0;
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0 || site < 0 || site >= siteCount) return false;
        int day = (int)Math.Floor(hours / 24.0);
        double hour = hours - day * 24.0;
        if (hour < FirstHour || hour >= LastHour) return false;
        ulong hash = LifeHash.Mix(seed ^ ((ulong)(uint)day << 32) ^ (uint)site ^ 0xC20551UL);
        double offset = site * (CycleSeconds / siteCount) + (hash % 1000) * .008;
        double elapsed = (hour - FirstHour) * GameClock.SecondsPerGameHour - offset;
        if (elapsed < 0) return false;
        int cycle = (int)Math.Floor(elapsed / CycleSeconds);
        serial = ((long)day << 16) | (uint)cycle;
        children = 3 + (int)(LifeHash.Mix(hash ^ (uint)cycle) % 3);
        age = (float)(elapsed - cycle * CycleSeconds);
        if (offset + cycle * CycleSeconds + Duration(length, children) >
            (LastHour - FirstHour) * GameClock.SecondsPerGameHour) return false;
        return age < Duration(length, children);
    }

    public static float ChildProgress(float age, float length, int child) => Mathf.Clamp01(
        (age - GatherSeconds - GuardEnterSeconds - Mathf.Max(0, child) * KidSpacingSeconds) /
        (Mathf.Max(1f, length) / WalkSpeed));

    /// <summary>Distance along a possibly turning road path to the near-side stop line, including this car's nose.
    /// Vehicles already committed past that line clear the crossing instead of being trapped on it.</summary>
    public static float SegmentStopGap(Vector3 from, Vector3 to, CarPath path, float arc, float halfLength, float lookAhead)
    {
        if (path == null || path.pts == null || path.s == null || path.pts.Length < 2 || lookAhead < 0) return float.PositiveInfinity;
        Vector2 q = new Vector2(from.x, from.z), cross = new Vector2(to.x - from.x, to.z - from.z);
        float best = float.PositiveInfinity;
        for (int i = path.SegmentAt(arc); i < path.pts.Length - 1; i++)
        {
            if (path.s[i] > arc + lookAhead + halfLength + StopMargin) break;
            var a = path.pts[i]; var b = path.pts[i + 1];
            Vector2 p = new Vector2(a.x, a.z), d = new Vector2(b.x - a.x, b.z - a.z);
            float det = Cross(d, cross);
            if (Mathf.Abs(det) < .0001f) continue;
            float u = Cross(q - p, cross) / det, v = Cross(q - p, d) / det;
            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
            if (Mathf.Abs(Mathf.Lerp(a.y, b.y, u) - Mathf.Lerp(from.y, to.y, v)) > 2f) continue;
            float stop = Mathf.Lerp(path.s[i], path.s[i + 1], u) - Mathf.Max(0f, halfLength) - StopMargin;
            float gap = stop - arc;
            if (gap >= -.08f && gap <= lookAhead) best = Mathf.Min(best, Mathf.Max(0f, gap));
        }
        return best;
    }
    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}

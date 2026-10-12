using UnityEditor;
using UnityEngine;

/// <summary>Play-mode shortcuts for the drunk status (Docs/Casino.md §5): Rubbish/Debug/Drunk/…</summary>
public static class DrunkDebugMenu
{
    const string M = "Rubbish/Debug/Drunk/";
    static bool Playing() => Application.isPlaying;

    static void Say() => Debug.Log($"[Drunk] level {DrunkStatus.Level:0.00} ({DrunkStatus.Stage})");

    [MenuItem(M + "Have a beer (+0.12)")]
    static void Beer() { DrunkStatus.Drink(0.12f); Say(); }
    [MenuItem(M + "Have a beer (+0.12)", true)] static bool V0() => Playing();

    [MenuItem(M + "+0.5")]
    static void Half() { DrunkStatus.Drink(0.5f); Say(); }
    [MenuItem(M + "+0.5", true)] static bool V1() => Playing();

    [MenuItem(M + "Set Wasted (0.9)")]
    static void Wasted() { DrunkStatus.SetLevelDirect(0.9f); Say(); }
    [MenuItem(M + "Set Wasted (0.9)", true)] static bool V2() => Playing();

    [MenuItem(M + "Sober up")]
    static void Sober() { DrunkStatus.SetLevelDirect(0f); Say(); }
    [MenuItem(M + "Sober up", true)] static bool V3() => Playing();

    [MenuItem(M + "Pass out now")]
    static void PassOut() => Debug.Log(DrunkStatus.PassOutNow() ? "[Drunk] Passing out." : "[Drunk] Cannot pass out right now (no player, a transition, or already out).");
    [MenuItem(M + "Pass out now", true)] static bool V4() => Playing();
}

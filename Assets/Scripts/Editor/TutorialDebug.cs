using UnityEditor;
using UnityEngine;

/// <summary>Rubbish > Debug > Tutorial: replay the first-run walk-through, switch it off, see where it is (play mode only).</summary>
public static class TutorialDebug
{
    const string Menu = "Rubbish/Debug/Tutorial/";

    [MenuItem(Menu + "Restart As A New Save", false, 10)]
    static void Restart()
    {
        if (!Playing()) return;
        Tutorial.DebugRestart();
        Debug.Log("[Tutorial] restarted: every lesson to do again, the contract cards too. " + Tutorial.DebugState());
    }

    [MenuItem(Menu + "Switch Off", false, 11)]
    static void Off()
    {
        if (!Playing()) return;
        Tutorial.DebugOff(true);
        Debug.Log("[Tutorial] off for this save.");
    }

    [MenuItem(Menu + "Switch Back On", false, 12)]
    static void On()
    {
        if (!Playing()) return;
        Tutorial.DebugOff(false);
        Debug.Log("[Tutorial] on again.");
    }

    [MenuItem(Menu + "Log Where It Is", false, 30)]
    static void Log()
    {
        if (!Playing()) return;
        Debug.Log("[Tutorial] " + Tutorial.DebugState());
    }

    static bool Playing()
    {
        if (Application.isPlaying) return true;
        EditorUtility.DisplayDialog("Tutorial", "Enter play mode in a game first (the walk-through lives in the save).", "OK");
        return false;
    }
}

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Start an isolated second Editor peer with -executeMethod SharedGripEditorProbe.Guest.
/// Uses command-line -dataroot/-playerid and the normal game bot. No player build is produced.</summary>
[InitializeOnLoad]
public static class SharedGripEditorProbe
{
    const string Armed = "Rubbish.SharedGripEditorProbe";
    static SharedGripEditorProbe() => EditorApplication.update += Watch;

    public static void Guest()
    {
        SessionState.SetBool(Armed, true);
        EditorSceneManager.OpenScene("Assets/Scenes/Bootstrap.unity");
        EditorApplication.isPlaying = true;
    }

    static void Watch()
    {
        if (!SessionState.GetBool(Armed, false) || !EditorApplication.isPlaying) return;
        if (BotDriver.Instance == null)
        {
            bool edges = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-gripedges") >= 0;
            BotDriver.Launch(new BotOptions { join = true, address = "127.0.0.1", port = 7793,
                minutes = edges ? 2f : 5f, brain = edges ? "sharedgrip-edges" : "sharedgrip",
                netsim = "crew_bad_wifi", label = "grip-editor-guest", quitWhenDone = false });
        }
        else if (BotDriver.Instance.Phase == "done" || BotDriver.Instance.Phase == "failed")
        {
            int expected = BotDriver.Options.brain == "sharedgrip-edges" ? 4 : SharedGripBot.Rounds;
            bool pass = SharedGripBot.Results.Count == expected && SharedGripBot.Results.TrueForAll(x => x.StartsWith("PASS"));
            SessionState.SetBool(Armed, false);
            EditorApplication.Exit(pass ? 0 : 1);
        }
    }
}

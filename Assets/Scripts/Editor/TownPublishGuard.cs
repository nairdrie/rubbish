using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>A saved scene is not necessarily a published town: graph, pickup directory and map must match.
/// Prevents Play/build with stale town data after Save, manual duplication, undo or an external blueprint edit.</summary>
[InitializeOnLoad]
public sealed class TownPublishGuard : IPreprocessBuildWithReport
{
    static bool publishing;

    /// <summary>SessionState key a tool can set ("1") to force the no-dialog path even while the Editor is focused.</summary>
    public const string AutomationKey = "Rubbish.TownPublishGuard.Automated";

    /// <summary>True when nobody can be assumed to be at the Editor to answer a dialog.</summary>
    public static bool Automated =>
        SessionState.GetBool(AutomationKey, false) || !UnityEditorInternal.InternalEditorUtility.isApplicationActive;
    static TownPublishGuard() { EditorApplication.playModeStateChanged += OnPlayModeStateChanged; }
    public int callbackOrder => -900;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (!TownWorldSignatureBuilder.IsCurrent(out string reason))
            throw new BuildFailedException("Town edits have not been baked: " + reason +
                " Open Rubbish > Map Editor and choose Save & bake before building.");
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode || publishing || TownWorldSignatureBuilder.IsCurrent(out string reason)) return;
        EditorApplication.isPlaying = false;
        // A modal dialog blocks the Editor's main thread until someone clicks it. When Play was requested by a tool
        // (the CLI pipeline, a script, a test runner) nobody is there to click, and every later command hangs. Only ask
        // when a person is at the Editor: it is the foreground app and the request was not flagged as automated.
        if (Application.isBatchMode || Automated)
        {
            Debug.LogError("[TownPublishGuard] Play Mode refused: the town is not published (" + reason +
                "). Bake it first: Rubbish > Map Editor > Save & bake, or Rubbish > City > Rebuild Town Life. " +
                "No dialog was shown because the request did not come from someone at the Editor.");
            return;
        }
        bool bake = EditorUtility.DisplayDialog("Town edits need baking", reason +
            "\n\nSaving the scene alone does not update garbage rules, resident paths or the map. Bake the town before playing?",
            "Bake and play", "Cancel");
        if (!bake) return;
        EditorApplication.delayCall += () =>
        {
            publishing = true;
            try
            {
                NeighborhoodBuilder.ApplyCoastalChanges(NeighborhoodBuilder.DefaultScenePath, true);
                if (!TownWorldSignatureBuilder.IsCurrent(out string failure)) throw new InvalidOperationException(failure);
                EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
            }
            catch (Exception e)
            {
                Debug.LogError("Town bake was not published: " + e.Message);
                EditorUtility.DisplayDialog("Town needs correction", e.Message + "\n\nYour editable draft has been kept. Fix the reported placement and bake again.", "OK");
            }
            finally { publishing = false; }
        };
    }
}

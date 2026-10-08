using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class CoastalCityAuthoring
{
    [MenuItem("Rubbish/City/Apply Residential Blocks")]
    public static void ApplyResidentialBlocks()
    {
        try { Debug.Log(NeighborhoodBuilder.ApplyResidentialBlocks()); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [MenuItem("Rubbish/City/Open Blueprint")]
    public static void OpenBlueprint()
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(CoastalCityLayout.BlueprintPath);
        if (asset == null) { Debug.LogError("Missing " + CoastalCityLayout.BlueprintPath); return; }
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
        AssetDatabase.OpenAsset(asset);
    }

    [MenuItem("Rubbish/City/Apply Blueprint Changes")]
    public static void ApplyBlueprint()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Exit Play mode before applying city edits."); return; }
        try { Debug.Log(NeighborhoodBuilder.ApplyCoastalChanges()); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [MenuItem("Rubbish/City/Validate Current City")]
    public static void ValidateCurrentCity()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Exit Play mode before validating authored city placements."); return; }
        try
        {
            // Do not open/save another scene or apply any transforms. Validation is a read-only inspection.
            CoastalCityLayout.Reload();
            Transform town = null;
            for (int i = 0; i < SceneManager.sceneCount && town == null; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var root = scene.GetRootGameObjects().FirstOrDefault(x => x.name == "NeighborhoodGen");
                if (root != null) town = root.transform;
            }
            var anchor = town != null ? town.GetComponentInChildren<WorldLifeAnchor>(true) : null;
            StreetGraph graph = anchor != null ? anchor.graph : null;
            if (graph == null && town != null)
                foreach (var root in town.gameObject.scene.GetRootGameObjects())
                {
                    anchor = root.GetComponentInChildren<WorldLifeAnchor>(true);
                    if (anchor != null && anchor.graph != null) { graph = anchor.graph; break; }
                }
            var errors = CoastalCityValidation.Validate(town, graph);
            if (errors.Count == 0) Debug.Log("Coastal city validation passed: blueprint, connected roads/bridges, stable stops, road anchors, household metadata and StreetGraph.", town);
            else Debug.LogError("Coastal city validation found " + errors.Count + " issue(s):\n- " + string.Join("\n- ", errors), town);
        }
        catch (Exception e) { Debug.LogException(e); }
    }
}

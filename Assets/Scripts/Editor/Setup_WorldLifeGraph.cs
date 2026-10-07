using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// WorldLife package A setup (Docs/WorldLife.md §12.2: the umbrella's "Graph check"). The street graph, exits, household
/// cars and knockables are BUILT by NeighborhoodBuilder (Rubbish/Setup/Neighborhood); this checks them, read-only and
/// idempotent: Resources/StreetGraph.asset's structure (§5.3), then — with the Neighborhood scene opened additively and
/// closed again, never saved — the anchor, the scene probes (lanes on road surfaces with headroom, sidewalks clear of
/// houses / hedges / walls / knockables), the knockables (≥ 700, unique ids, primitive rest colliders), household cars and
/// exit portals. Rubbish/WorldLife/Render Street Graph writes Docs/WorldLifeRefs/graph_top.png and the exit shots.
/// </summary>
public static class Setup_WorldLifeGraph
{
    public const string ScenePath = NeighborhoodBuilder.DefaultScenePath;
    public const string RefsDir = "Docs/WorldLifeRefs";

    [MenuItem("Rubbish/Setup/WorldLife Graph Check")]
    static void RunMenu()
    {
        string r = Run();
        if (r.Contains("FAIL")) Debug.LogWarning(r); else Debug.Log(r);
    }

    /// <summary>The package setup the Setup_WorldLife umbrella calls.</summary>
    public static string Run() => Check(ScenePath, StreetGraph.AssetPath);

    public static string Check(string scenePath, string graphPath)
    {
        var sb = new StringBuilder("[Setup_WorldLifeGraph]\n");
        if (EditorApplication.isPlayingOrWillChangePlaymode) return sb.Append("  FAIL exit play mode first\n").ToString();
        var g = AssetDatabase.LoadAssetAtPath<StreetGraph>(graphPath);
        if (g == null) return sb.Append($"  FAIL {graphPath} is missing: run Rubbish/Setup/Neighborhood\n").ToString();
        var errors = new List<string>();
        int ge = g.Validate(errors);
        sb.AppendLine($"  {graphPath}: {(ge == 0 ? "ok" : "FAIL " + ge + " errors")} · " + g.Describe().Replace("\n", "\n    "));
        for (int i = 0; i < Mathf.Min(15, errors.Count); i++) sb.AppendLine("    " + errors[i]);

        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        try
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                opened = true;
            }
            int anchors = 0, anchorsOnGraph = 0, cars = 0, parkedOnly = 0, portals = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var a in root.GetComponentsInChildren<WorldLifeAnchor>(true)) { anchors++; if (a.graph == g) anchorsOnGraph++; }
                foreach (var hc in root.GetComponentsInChildren<HouseholdCar>(true)) { cars++; if (hc.parkedOnly) parkedOnly++; }
                portals += root.GetComponentsInChildren<TownExitPortal>(true).Length;
            }
            sb.AppendLine($"  scene {scenePath}: anchors {anchors} (on this graph {anchorsOnGraph}), household cars {cars} ({parkedOnly} parked-only), exit portals {portals}");
            if (anchorsOnGraph != 1) sb.AppendLine("  FAIL want exactly one WorldLifeAnchor pointing at the graph");
            if (portals != g.exits.Length) sb.AppendLine($"  FAIL {portals} exit portals for {g.exits.Length} graph exits");
            int kf = StreetGraphProbes.CheckKnockables(scene, sb, out int knockables);
            if (kf > 0) sb.AppendLine($"  FAIL {kf} knockable problems");
            if (knockables < 700) sb.AppendLine($"  FAIL only {knockables} knockables (want >= 700)");
            int pf = StreetGraphProbes.Run(g, scene, sb);
            if (pf > 0) sb.AppendLine($"  FAIL {pf} bad probe samples");
        }
        finally
        {
            if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
        }
        sb.AppendLine(sb.ToString().Contains("FAIL") ? "  RESULT: FAIL" : "  RESULT: OK");
        return sb.ToString();
    }

    [MenuItem("Rubbish/WorldLife/Render Street Graph")]
    static void RenderMenu() => Debug.Log(Render(ScenePath, StreetGraph.AssetPath, Path.Combine(Directory.GetCurrentDirectory(), RefsDir)));

    /// <summary>
    /// graph_top.png (the whole town, top-down, the graph drawn over it with a legend) and exit_west / exit_east /
    /// exit_north.png (each tunnel from the road, lanes drawn) into <paramref name="outDir"/>. The scene is opened
    /// additively if needed and closed again (never saved); temporary cameras and labels are removed.
    /// </summary>
    public static string Render(string scenePath, string graphPath, string outDir, int widthPx = 1500)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "[Setup_WorldLifeGraph] exit play mode first";
        var g = AssetDatabase.LoadAssetAtPath<StreetGraph>(graphPath);
        if (g == null) return $"[Setup_WorldLifeGraph] {graphPath} is missing";
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        var sb = new StringBuilder("[Setup_WorldLifeGraph] renders\n");
        try
        {
            if (!scene.IsValid() || !scene.isLoaded) { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive); opened = true; }
            var area = new Rect(-64f, -44f, 508f, 688f);
            var labels = new List<(Vector3, string, float)>
            {
                (new Vector3(80f, 0f, 115f), "MILDLY PLEASANT ACRES", 9f), (new Vector3(320f, 0f, 120f), "MINIVAN MEADOWS", 9f),
                (new Vector3(100f, 0f, 330f), "SNOOTINGTON HILLS", 9f), (new Vector3(160f, 0f, 600f), "BILLIONAIRE BLUFFS", 9f),
                (new Vector3(-30f, 0f, 214f), "Old Highway", 6f), (new Vector3(420f, 0f, 12f), "Interstate", 6f),
                (new Vector3(30f, 0f, 620f), "Private Airport Rd", 6f), (new Vector3(0f, 0f, -28f), "depot", 6f),
            };
            sb.AppendLine("  " + StreetGraphRender.RenderTop(scene, g, area, widthPx, Path.Combine(outDir, "graph_top.png"), true, labels));
            foreach (var x in g.exits)
            {
                var portal = x.portal.position;
                Vector3 f = x.portal.rotation * Vector3.forward, r = new Vector3(f.z, 0f, -f.x);
                var cam = portal - f * 34f - r * 5f + Vector3.up * 7f;
                string name = x.id.Replace("exit-", "exit_") + ".png";
                sb.AppendLine("  " + StreetGraphRender.RenderPerspective(scene, g, cam, portal + Vector3.up * 2.5f, 55f, 1280, 720, Path.Combine(outDir, name)));
            }
        }
        finally
        {
            if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
        }
        return sb.ToString();
    }

    /// <summary>A zoomed top-down crop of the graph (for reviews): area = (x0, z0, width, depth).</summary>
    public static string RenderArea(string scenePath, string graphPath, Rect area, string outPath, int widthPx = 1400)
    {
        var g = AssetDatabase.LoadAssetAtPath<StreetGraph>(graphPath);
        if (g == null) return graphPath + " missing";
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        try
        {
            if (!scene.IsValid() || !scene.isLoaded) { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive); opened = true; }
            return StreetGraphRender.RenderTop(scene, g, area, widthPx, outPath, false, null, 1.6f);
        }
        finally
        {
            if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
        }
    }
}

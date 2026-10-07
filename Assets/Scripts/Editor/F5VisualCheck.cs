using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Isolated-copy visual and display-plan check for F4/F5 keepsakes and the production Depot Row Cleanup board.
/// Run with Unity -batchmode (with graphics) -executeMethod F5VisualCheck.Run. Saves no scene or prefab.
/// </summary>
public static class F5VisualCheck
{
    const int Width = 1280, Height = 900;

    public static void Run()
    {
        if (Application.productName != "RubbishBuildCopy" || !Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("[F5VisualCheck] Run only in batch mode in the isolated RubbishBuildCopy Editor.");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("[F5VisualCheck] A graphics device is required; omit -nographics.");

        for (int day = 1; day <= 40; day++)
        {
            float hour = BigCleanupNet.StartHourFor(day);
            if (hour < CleanupTuning.StartHourMin || hour >= CleanupTuning.StartHourMax)
                throw new InvalidOperationException($"[F5VisualCheck] Invalid natural Cleanup start on day {day}: {hour}.");
            if (BigCleanupNet.ScheduledDayAtSessionStart(day - 1, day, hour - 0.05f) != day - 2 ||
                BigCleanupNet.ScheduledDayAtSessionStart(day - 1, day, hour + 0.05f) != day - 1)
                throw new InvalidOperationException($"[F5VisualCheck] Session-start scheduling skips a future or repeats a past Cleanup on day {day}.");
        }
        if (CleanupTuning.RunSeconds != 600f || CleanupTuning.RateMul != 2f)
            throw new InvalidOperationException("[F5VisualCheck] The 10-minute/double-bag Cleanup contract changed.");
        if (BigCleanupNet.RewardUid("event-a", "business") == BigCleanupNet.RewardUid("event-b", "business") ||
            BigCleanupNet.RewardUid("event-a", "business") != BigCleanupNet.RewardUid("event-a", "business"))
            throw new InvalidOperationException("[F5VisualCheck] Trophy IDs must be stable within an event and unique across sessions.");
        if (!PoachingNet.Approaching(Vector3.zero, new Vector3(3f, 0f, 0f), new Vector3(45f, 0f, 0f)) ||
            PoachingNet.Approaching(Vector3.zero, Vector3.zero, new Vector3(45f, 0f, 0f)) ||
            PoachingNet.Approaching(Vector3.zero, new Vector3(-3f, 0f, 0f), new Vector3(45f, 0f, 0f)) ||
            PoachingNet.Approaching(Vector3.zero, new Vector3(3f, 0f, 0f), new Vector3(80f, 0f, 0f)))
            throw new InvalidOperationException("[F5VisualCheck] Before-arrival poaching approach rule failed.");

        var ids = new[] { CleanupTuning.CupDef, CleanupTuning.RibbonDef, RaccoonKing.TrophyDefId, RaccoonTrap.PetDefId };
        var items = new List<ItemInstance>(ids.Length);
        foreach (string id in ids)
        {
            var def = ItemCatalog.Get(id);
            if (def == null || def.category != ItemCategory.Valuable || string.IsNullOrEmpty(def.visual))
                throw new InvalidOperationException("[F5VisualCheck] Missing keepsake item definition/visual: " + id);
            items.Add(new ItemInstance { uid = "visual-check/" + id, defId = id, massKg = def.minMass, value = def.minValue, condition = 1f });
        }
        var plan = DisplayPlanner.Plan(items, id => id == ShopIds.TrophyShelf ? 1 : 0, _ => 0);
        foreach (var item in items)
            if (!plan.Find(item.uid, out string key, out _) || key != DisplayPlanner.TrophyShelfKey)
                throw new InvalidOperationException("[F5VisualCheck] Keepsake is not on an owned Trophy Shelf: " + item.defId);

        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "F5VisualCheck"));
        Directory.CreateDirectory(output);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        try
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.67f, 0.7f, 0.74f);
            var sun = new GameObject("F5 visual check sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.25f;
            sun.transform.rotation = Quaternion.Euler(35f, -35f, 0f);

            var cam = new GameObject("F5 visual check camera").AddComponent<Camera>();
            cam.scene = scene;
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.47f, 0.57f, 0.66f);
            cam.orthographic = true;
            cam.orthographicSize = 3.5f;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 100f;
            cam.allowHDR = false;

            var boardComponent = new GameObject("Production Cleanup board preview").AddComponent<CleanupBoard>();
            var build = typeof(CleanupBoard).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance);
            var fill = typeof(CleanupBoard).GetMethod("Fill", BindingFlags.NonPublic | BindingFlags.Instance);
            var boardField = typeof(CleanupBoard).GetField("board", BindingFlags.NonPublic | BindingFlags.Instance);
            var titleField = typeof(CleanupBoard).GetField("title", BindingFlags.NonPublic | BindingFlags.Instance);
            var bodyField = typeof(CleanupBoard).GetField("body", BindingFlags.NonPublic | BindingFlags.Instance);
            if (build == null || fill == null || boardField == null || titleField == null || bodyField == null)
                throw new MissingMethodException("[F5VisualCheck] CleanupBoard preview bridge needs updating.");
            build.Invoke(boardComponent, null);
            fill.Invoke(boardComponent, null);
            var board = boardField.GetValue(boardComponent) as GameObject;
            if (board == null || board.GetComponentsInChildren<Renderer>(true).Length < 6)
                throw new InvalidOperationException("[F5VisualCheck] Production Depot Row board did not build.");
            foreach (var label in board.GetComponentsInChildren<TextMeshPro>(true)) label.ForceMeshUpdate(true, true);
            Vector3 boardAt = board.transform.position + Vector3.up * 2.8f;
            cam.transform.SetPositionAndRotation(boardAt + new Vector3(0f, 0.4f, 12f),
                Quaternion.LookRotation(boardAt - (boardAt + new Vector3(0f, 0.4f, 12f)), Vector3.up));
            Save(cam, Path.Combine(output, "depot-cleanup-board.png"));
            var boardTitle = titleField.GetValue(boardComponent) as TextMeshPro;
            var boardBody = bodyField.GetValue(boardComponent) as TextMeshPro;
            if (boardTitle == null || boardBody == null) throw new InvalidOperationException("[F5VisualCheck] Missing physical board text.");
            var exampleRows = new string[4];
            for (int i = 0; i < exampleRows.Length; i++)
            {
                var row = new CleanupScore { color = (byte)i, bags = 12 - i * 2, grams = (12 - i * 2) * 7250 };
                row.name.CopyFromTruncated(i == 0 ? "The Very Long Neighborhood Cleanup Company" : "Business With A Long Name " + i);
                exampleRows[i] = CleanupBoard.ScoreRow(row, i + 1);
            }
            boardTitle.text = "BIG CLEANUP 9:59";
            boardBody.text = string.Join("\n", exampleRows);
            boardTitle.ForceMeshUpdate(true, true);
            boardBody.ForceMeshUpdate(true, true);
            Save(cam, Path.Combine(output, "depot-cleanup-running.png"));
            boardTitle.text = "TIME'S UP!";
            boardBody.text = "Winner:\n<color=#F8D64A>●</color> The Very Long Neighbor…\n+$148";
            boardTitle.ForceMeshUpdate(true, true);
            boardBody.ForceMeshUpdate(true, true);
            Save(cam, Path.Combine(output, "depot-cleanup-results.png"));
            UnityEngine.Object.DestroyImmediate(board);
            UnityEngine.Object.DestroyImmediate(boardComponent.gameObject);

            var shelf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shelf.name = "Keepsake model preview shelf";
            shelf.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            shelf.transform.localScale = new Vector3(6.6f, 0.14f, 0.9f);
            UnityEngine.Object.DestroyImmediate(shelf.GetComponent<Collider>());
            for (int i = 0; i < items.Count; i++)
            {
                var anchor = new GameObject("Keepsake " + items[i].defId).transform;
                anchor.position = new Vector3(-2.4f + i * 1.6f, 0f, 0f);
                var visual = ItemVisuals.Build(items[i].Def, anchor);
                var renderers = visual.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                    throw new InvalidOperationException("[F5VisualCheck] No mesh for " + items[i].defId);
                float bottom = float.PositiveInfinity;
                foreach (var renderer in renderers) bottom = Mathf.Min(bottom, renderer.bounds.min.y);
                anchor.position += Vector3.up * (0.13f - bottom);
            }
            cam.transform.SetPositionAndRotation(new Vector3(0f, 1.0f, 8f),
                Quaternion.LookRotation(new Vector3(0f, 0.55f, 0f) - new Vector3(0f, 1.0f, 8f), Vector3.up));
            cam.orthographicSize = 2.4f;
            Save(cam, Path.Combine(output, "keepsake-models.png"));
            Debug.Log("[F5VisualCheck] PASS: natural start window, 10-minute double rate, Cup/Ribbon/Crown/Pet on owned shelf, production board and four keepsake meshes. PNGs: " + output);
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    static void Save(Camera camera, string path)
    {
        var texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        Texture2D pixels = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            if (!texture.Create()) throw new InvalidOperationException("[F5VisualCheck] Could not create render texture.");
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}

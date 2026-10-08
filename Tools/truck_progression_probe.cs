// Editor-only QA harness, compiled in memory with `unity command run_script`.
// No scene assets or player saves are modified.
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class TruckProgressionProbe
{
    public static object Loot()
    {
        var results = new System.Collections.Generic.List<object>();
        foreach (float cap in new[] { 120f, 160f, 200f })
        {
            int lowTotal = 0, two = 0, bagTotal = 0;
            const int runs = 512;
            for (int run = 0; run < runs; run++)
            {
                float load = 0; int low = 0;
                for (int attempt = 0; attempt < 100 && load < cap - 4; attempt++)
                {
                    var bag = ItemCatalog.RollBag("truck-probe/" + run + "/" + attempt,
                        unchecked(17327 + run * 7919 + attempt * 104729 + 10000019), .5f, "audit", Districts.Starter.loot, .6f);
                    if (load + bag.TotalMassKg > cap) continue;
                    load += bag.TotalMassKg; bagTotal++;
                    low += bag.items.Count(i => i.Def != null && i.Def.category == ItemCategory.Valuable && (int)i.Def.rarity <= 1);
                }
                lowTotal += low;
                if (low >= 2) two++;
            }
            results.Add(new { cap, runs, averageBags = bagTotal / (float)runs, averageCommonUncommon = lowTotal / (float)runs, fractionWithTwo = two / (float)runs });
        }
        return results;
    }

    public static object Preview()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Preview runs outside play mode.");
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Truck progression preview");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        RenderTexture rt = null;
        Texture2D image = null;
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Truck V2.prefab");
            for (int model = 0; model <= 1; model++)
            {
                var go = UnityEngine.Object.Instantiate(prefab, root.transform);
                go.transform.position = new Vector3(model == 0 ? -2.1f : 2.2f, .05f, model == 0 ? -.7f : 1f);
                foreach (var camera in go.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
                foreach (var light in go.GetComponentsInChildren<Light>(true)) light.enabled = false;
                var truck = go.GetComponent<TruckControllerV2>();
                var visual = TruckModelVisuals.Ensure(truck);
                visual.ApplyModel(model);
                RideTruck.Ensure(truck);
                HitchStep.Ensure(truck);
                foreach (var wheel in go.GetComponentsInChildren<WheelControl>(true))
                {
                    if (wheel.wheelModel == null || !wheel.enabled) continue;
                    var wc = wheel.GetComponent<WheelCollider>();
                    wheel.wheelModel.position = wc.transform.position - go.transform.up * wc.suspensionDistance * .5f;
                    wheel.wheelModel.rotation = go.transform.rotation;
                }
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.SetParent(root.transform);
            floor.transform.position = new Vector3(0f, -.1f, 0f);
            floor.transform.localScale = new Vector3(30f, .2f, 30f);
            floor.GetComponent<Renderer>().sharedMaterial = ShopModKit.Lit("truck_preview_floor", new Color(.21f, .25f, .27f));
            var sun = new GameObject("Preview sun").AddComponent<Light>();
            sun.transform.SetParent(root.transform);
            sun.type = LightType.Directional; sun.intensity = 2.1f;
            sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var cam = new GameObject("Preview camera").AddComponent<Camera>();
            cam.transform.SetParent(root.transform);
            cam.transform.position = new Vector3(-12f, 9.5f, 13f);
            cam.transform.LookAt(new Vector3(0f, 1.5f, .7f));
            cam.orthographic = true; cam.orthographicSize = 6.3f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.15f, .20f, .24f);
            cam.scene = scene;
            rt = new RenderTexture(1600, 1000, 24);
            cam.targetTexture = rt;
            cam.Render();
            var old = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                image.Apply();
            }
            finally { RenderTexture.active = old; }
            string path = Path.GetFullPath("Logs/truck-progression-preview.png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return new { path };
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            if (image != null) UnityEngine.Object.DestroyImmediate(image);
            if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
        }
    }
}

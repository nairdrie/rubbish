using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// F4 light presentation check. Disposable edit-mode scene, real player/truck prefabs and production lamp builders;
/// writes light and shop-pet PNGs only in the isolated copy's Builds/NightLightPreview directory. Run batchmode with graphics.
/// </summary>
public static class NightLightPreview
{
    const string PlayerPath = "Assets/Prefabs/Player.prefab";
    const string TruckPath = "Assets/Prefabs/Truck V2.prefab";
    const int Width = 1280, Height = 840;

    public static void Run()
    {
        if (Application.productName != "RubbishBuildCopy" || !Application.isBatchMode || Application.isPlaying)
            throw new InvalidOperationException("[NightLightPreview] Run only in batch mode on the isolated build copy.");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("[NightLightPreview] Requires graphics; omit -nographics.");
        var playerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        var truckAsset = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPath);
        if (playerAsset == null || truckAsset == null)
            throw new InvalidOperationException("[NightLightPreview] Missing Player or Truck V2 prefab.");

        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "NightLightPreview"));
        Directory.CreateDirectory(output);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        try
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.055f, 0.075f, 0.11f);
            var camera = new GameObject("Night light camera").AddComponent<Camera>();
            camera.scene = scene;
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.015f, 0.025f, 0.055f);
            camera.fieldOfView = 48f;
            camera.nearClipPlane = 0.04f;
            camera.farClipPlane = 100f;
            camera.allowHDR = false;

            MakeGround();
            var target = MakeTarget();
            RenderHeadlamp(playerAsset, scene, camera, target.transform, Path.Combine(output, "headlamp.png"));
            RenderRoofSpotlight(truckAsset, scene, camera, target.transform, Path.Combine(output, "roof-spotlight.png"));
            RenderShopPet(camera, output);
            Debug.Log("[NightLightPreview] PASS: headlamp, roof spotlight, and living shelf pet previews at " + output);
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    static void RenderHeadlamp(GameObject asset, Scene scene, Camera camera, Transform target, string path)
    {
        var player = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
        if (player == null) throw new InvalidOperationException("[NightLightPreview] Player prefab could not be instantiated.");
        try
        {
            player.transform.SetPositionAndRotation(new Vector3(0f, 0f, -3f), Quaternion.identity);
            DisableCameras(player);
            var puppet = player.GetComponent<ChuckPuppet>();
            if (puppet != null) { puppet.PreviewTick(1f / 60f); puppet.PreviewMaterials(); }
            var lamp = player.GetComponent<PlayerHeadlamp>() ?? player.AddComponent<PlayerHeadlamp>();
            Invoke(lamp, "Build");
            Invoke(lamp, "Follow");
            var rig = lamp.transform.Find("Night Headlamp");
            var light = rig != null ? rig.GetComponentInChildren<Light>(true) : null;
            if (light == null || light.type != LightType.Spot)
                throw new InvalidOperationException("[NightLightPreview] Production headlamp has no spot light.");
            light.enabled = true;
            light.intensity = 7f;
            target.position = new Vector3(0f, 1.2f, 2.5f);
            CheckBeam("headlamp", light, target.position, 18f);
            camera.transform.position = new Vector3(5f, 2.6f, -6.8f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.25f, 1.3f) - camera.transform.position, Vector3.up);
            float dark = TargetLuminance(camera, target.position, light, false);
            float lit = TargetLuminance(camera, target.position, light, true);
            if (lit < dark + 0.04f)
                throw new InvalidOperationException($"[NightLightPreview] Headlamp does not visibly light target: off={dark:F3}, on={lit:F3}.");
            Save(camera, path);
            Debug.Log($"[NightLightPreview] headlamp light: range={light.range:F1}m, intensity={light.intensity:F1}, target luminance {dark:F3}->{lit:F3}");
        }
        finally { UnityEngine.Object.DestroyImmediate(player); }
    }

    static void RenderRoofSpotlight(GameObject asset, Scene scene, Camera camera, Transform target, string path)
    {
        var truck = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
        if (truck == null) throw new InvalidOperationException("[NightLightPreview] Truck prefab could not be instantiated.");
        try
        {
            truck.transform.SetPositionAndRotation(new Vector3(0f, 0f, -5f), Quaternion.identity);
            DisableCameras(truck);
            var controller = truck.GetComponent<TruckControllerV2>();
            if (controller == null) throw new InvalidOperationException("[NightLightPreview] Truck prefab has no controller.");
            RoofSpotlight.Ensure(controller);
            var roof = truck.GetComponentInChildren<RoofSpotlight>(true);
            var light = roof != null ? roof.GetComponentInChildren<Light>(true) : null;
            if (light == null || light.type != LightType.Spot)
                throw new InvalidOperationException("[NightLightPreview] Production roof spotlight has no spot light.");
            var head = roof.transform.Find("Pivot/Head");
            if (head == null) throw new InvalidOperationException("[NightLightPreview] Roof spotlight pivot/head missing.");
            head.localRotation = Quaternion.Euler(9f, 0f, 0f);
            light.enabled = true;
            light.intensity = 26f;
            target.position = new Vector3(0f, 1.2f, 6f);
            CheckBeam("roof spotlight", light, target.position, RoofSpotlight.Range);
            // See the target's lit -Z face, not only its unlit side; keep truck and target in one frame.
            camera.transform.position = new Vector3(8f, 5f, -7f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.8f, 0f) - camera.transform.position, Vector3.up);
            float dark = TargetLuminance(camera, target.position, light, false);
            float lit = TargetLuminance(camera, target.position, light, true);
            if (lit < dark + 0.04f)
                throw new InvalidOperationException($"[NightLightPreview] Roof spotlight does not visibly light target: off={dark:F3}, on={lit:F3}.");
            Save(camera, path);
            Debug.Log($"[NightLightPreview] roof light: range={light.range:F1}m, intensity={light.intensity:F1}, target luminance {dark:F3}->{lit:F3}");
        }
        finally { UnityEngine.Object.DestroyImmediate(truck); }
    }

    static void CheckBeam(string name, Light light, Vector3 target, float maxRange)
    {
        Vector3 to = target - light.transform.position;
        float angle = Vector3.Angle(light.transform.forward, to);
        Debug.Log($"[NightLightPreview] {name}: target distance={to.magnitude:F2} m, angle={angle:F1}°, cone={light.spotAngle:F1}°");
        if (to.magnitude > maxRange || angle > light.spotAngle * 0.5f)
            throw new InvalidOperationException($"[NightLightPreview] {name} beam misses the visible target.");
    }

    static float TargetLuminance(Camera camera, Vector3 target, Light light, bool on)
    {
        light.enabled = on;
        var view = camera.WorldToViewportPoint(target);
        if (view.z <= 0f || view.x < 0.05f || view.x > 0.95f || view.y < 0.05f || view.y > 0.95f)
            throw new InvalidOperationException("[NightLightPreview] Lit target is not framed in the screenshot.");
        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
        Texture2D pixel = null;
        var previous = RenderTexture.active;
        try
        {
            if (!rt.Create()) throw new InvalidOperationException("[NightLightPreview] Could not create light probe target.");
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            pixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
            int x = Mathf.Clamp(Mathf.RoundToInt(view.x * Width), 0, Width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(view.y * Height), 0, Height - 1);
            pixel.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
            pixel.Apply();
            Color c = pixel.GetPixel(0, 0);
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            if (pixel != null) UnityEngine.Object.DestroyImmediate(pixel);
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }

    static void RenderShopPet(Camera camera, string output)
    {
        var def = ItemCatalog.Get(RaccoonTrap.PetDefId);
        if (def == null) throw new InvalidOperationException("[NightLightPreview] Missing shop pet item definition.");
        var shelf = GameObject.CreatePrimitive(PrimitiveType.Cube);
        shelf.name = "Shop pet preview shelf";
        shelf.transform.position = new Vector3(30f, 0.76f, 0f);
        shelf.transform.localScale = new Vector3(1.4f, 0.08f, 0.65f);
        SetColor(shelf, new Color(0.45f, 0.3f, 0.17f));
        var piece = new GameObject("Shop pet display").AddComponent<DisplayPiece>();
        piece.plates = false;
        piece.maxPropSize = 0.7f;
        var slot = new GameObject("Pet slot").transform;
        slot.SetParent(piece.transform, false);
        slot.position = new Vector3(30f, 0.805f, 0f);
        piece.slots = new[] { slot };
        var item = new ItemInstance { uid = "preview/shop-pet", defId = def.id, massKg = def.minMass,
            value = def.minValue, condition = 1f };
        if (piece.Show(new[] { item }, false).Count != 1 || slot.childCount != 1)
            throw new InvalidOperationException("[NightLightPreview] Shop pet was not presented on the shelf.");
        var prop = slot.GetChild(0);
        if (prop.GetComponentsInChildren<Renderer>(true).Length < 8)
            throw new InvalidOperationException("[NightLightPreview] Shop pet model is incomplete.");
        var animate = typeof(DisplayPiece).GetMethod("AnimateProps", BindingFlags.NonPublic | BindingFlags.Instance);
        if (animate == null) throw new MissingMethodException("DisplayPiece", "AnimateProps");
        var key = new GameObject("Pet preview key").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.8f;
        key.transform.rotation = Quaternion.Euler(40f, -25f, 0f);
        camera.orthographic = true;
        camera.orthographicSize = 0.62f;
        camera.transform.position = new Vector3(30.8f, 1.45f, 2.5f);
        camera.transform.rotation = Quaternion.LookRotation(new Vector3(30f, 1.02f, 0f) - camera.transform.position, Vector3.up);
        animate.Invoke(piece, new object[] { 0f, 0f });
        Vector3 firstPosition = prop.localPosition;
        Quaternion firstRotation = prop.localRotation;
        Save(camera, Path.Combine(output, "shop-pet-idle-a.png"));
        animate.Invoke(piece, new object[] { 2.4f, 0f });
        float yawChange = Quaternion.Angle(firstRotation, prop.localRotation);
        float bobChange = Vector3.Distance(firstPosition, prop.localPosition);
        if (yawChange < 5f || bobChange < 0.002f)
            throw new InvalidOperationException($"[NightLightPreview] Shelf pet does not move enough: turn={yawChange:F1}°, bob={bobChange:F3}m.");
        Save(camera, Path.Combine(output, "shop-pet-idle-b.png"));
        Debug.Log($"[NightLightPreview] living shop pet: turn={yawChange:F1}°, bob={bobChange:F3}m, model renderers={prop.GetComponentsInChildren<Renderer>(true).Length}");
    }

    static void Invoke(object target, string method)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) throw new MissingMethodException(target.GetType().Name, method);
        m.Invoke(target, null);
    }

    static void DisableCameras(GameObject root)
    {
        foreach (var c in root.GetComponentsInChildren<Camera>(true)) c.enabled = false;
    }

    static void MakeGround()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Wet dark street";
        floor.transform.SetPositionAndRotation(new Vector3(0f, -0.15f, 3f), Quaternion.identity);
        floor.transform.localScale = new Vector3(18f, 0.25f, 42f);
        SetColor(floor, new Color(0.09f, 0.12f, 0.17f));
    }

    static GameObject MakeTarget()
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        target.name = "Beam target";
        target.transform.localScale = new Vector3(1.6f, 2.3f, 0.18f);
        SetColor(target, new Color(0.75f, 0.85f, 0.93f));
        return target;
    }

    static void SetColor(GameObject go, Color c)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) throw new InvalidOperationException("[NightLightPreview] No suitable lit shader.");
        var mat = new Material(shader);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static void Save(Camera camera, string path)
    {
        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        Texture2D pixels = null;
        var previous = RenderTexture.active;
        try
        {
            if (!rt.Create()) throw new InvalidOperationException("[NightLightPreview] Could not create render texture.");
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            Debug.Log("[NightLightPreview] wrote " + path);
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}

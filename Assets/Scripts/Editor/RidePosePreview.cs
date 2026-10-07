using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Disposable edit-mode visual check for all four riding holds. Run only on the isolated build copy:
/// Unity -batchmode -quit -projectPath /path/to/Rubbish-build -executeMethod RidePosePreview.Run.
/// No scenes or prefabs are saved; images go under the copy's Builds/RidePosePreview directory.
/// </summary>
public static class RidePosePreview
{
    const string TruckPath = "Assets/Prefabs/Truck V2.prefab";
    const string PlayerPath = "Assets/Prefabs/Player.prefab";
    const int Width = 1280, Height = 960;

    public static void Run()
    {
        if (Application.productName != "RubbishBuildCopy")
            throw new InvalidOperationException("[RidePosePreview] Refusing to render outside the isolated RubbishBuildCopy project.");
        if (!Application.isBatchMode)
            throw new InvalidOperationException("[RidePosePreview] Run in batch mode so no open Editor scene is replaced.");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("[RidePosePreview] Exit Play Mode first.");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("[RidePosePreview] A graphics device is required; run -batchmode WITHOUT -nographics.");

        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "RidePosePreview"));
        Directory.CreateDirectory(output);
        var truckAsset = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPath);
        var playerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        if (truckAsset == null || playerAsset == null)
            throw new InvalidOperationException("[RidePosePreview] Truck V2 or Player prefab was not found.");

        // Unity batch mode starts with an unsaved Untitled scene and refuses an additive NewScene beside it.
        // Replacing that disposable scene is safe here: the copy-only/batch-only guards above prevent user-scene loss.
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        try
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.66f, 0.71f);
            var sun = new GameObject("Ride pose preview sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.35f;
            sun.transform.rotation = Quaternion.Euler(42f, -32f, 0f);

            var truck = (GameObject)PrefabUtility.InstantiatePrefab(truckAsset, scene);
            if (truck == null) throw new InvalidOperationException("[RidePosePreview] Could not instantiate the truck prefab.");
            truck.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            DisableEmbeddedCameras(truck);
            BuildActualRideKit(truck);

            var cameraGo = new GameObject("Ride pose preview camera");
            var camera = cameraGo.AddComponent<Camera>();
            camera.scene = scene;
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.47f, 0.58f, 0.67f);
            camera.fieldOfView = 38f;
            camera.nearClipPlane = 0.04f;
            camera.farClipPlane = 100f;
            camera.allowHDR = false;

            for (int hold = 0; hold < RideHolds.Count; hold++)
            {
                string name = HoldName(hold);
                PositionCamera(camera, truck.transform, hold);
                if (hold == RideHolds.RearLeft)
                    RenderRider(playerAsset, scene, truck.transform, camera, hold,
                        Path.Combine(output, "rear-left-baseline.png"), false);
                RenderRider(playerAsset, scene, truck.transform, camera, hold,
                    Path.Combine(output, name + ".png"), true);
            }
            Debug.Log("[RidePosePreview] PASS: five images (neutral baseline + four holds) at " + output);
        }
        finally
        {
            // Throw away preview objects without saving; leave batchmode in another empty untitled scene for -quit.
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    static void RenderRider(GameObject asset, Scene scene, Transform truck, Camera camera, int hold, string path, bool posed)
    {
        var rider = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
        if (rider == null) throw new InvalidOperationException("[RidePosePreview] Could not instantiate the player prefab.");
        try
        {
            rider.name = posed ? "Posed rider " + HoldName(hold) : "Neutral rider";
            DisableEmbeddedCameras(rider);
            Pose step = RideHolds.PoseOn(truck, hold);
            rider.transform.SetPositionAndRotation(step.position, step.rotation);
            var puppet = rider.GetComponent<ChuckPuppet>();
            if (puppet == null) throw new InvalidOperationException("[RidePosePreview] Player prefab has no ChuckPuppet.");
            puppet.variantOverride = 0;
            puppet.previewRideTruck = posed ? truck : null;
            puppet.previewRideHold = posed ? hold : -1;
            var animator = rider.GetComponent<Animator>();
            Transform leftFoot = posed && animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            Transform rightFoot = posed && animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightFoot) : null;
            Vector3 leftFootBefore = Vector3.zero, rightFootBefore = Vector3.zero;
            for (int frame = 0; frame < 16; frame++)
            {
                // Re-sample the authored idle clip each frame, as gameplay does, before applying additive puppet pose.
                if (animator != null) animator.Update(0f);
                if (posed && frame == 15 && leftFoot != null && rightFoot != null)
                {
                    leftFootBefore = leftFoot.position;
                    rightFootBefore = rightFoot.position;
                }
                puppet.PreviewTick(1f / 60f);
            }
            puppet.PreviewMaterials();

            int visible = 0;
            foreach (var renderer in rider.GetComponentsInChildren<Renderer>(true))
                if (renderer.enabled && renderer.gameObject.activeInHierarchy) visible++;
            if (visible == 0) throw new InvalidOperationException("[RidePosePreview] Player prefab has no visible renderers.");

            if (posed)
            {
                if (leftFoot == null || rightFoot == null)
                    throw new InvalidOperationException("[RidePosePreview] Humanoid boot bones are missing.");
                float bootDriftL = Vector3.Distance(leftFootBefore, leftFoot.position);
                float bootDriftR = Vector3.Distance(rightFootBefore, rightFoot.position);
                Debug.Log($"[RidePosePreview] {HoldName(hold)} boot drift: L={bootDriftL:F3} m R={bootDriftR:F3} m");
                if (bootDriftL > 0.06f || bootDriftR > 0.06f)
                    throw new InvalidOperationException($"[RidePosePreview] {HoldName(hold)} boot slipped off its step.");
                var leftMitt = puppet.parts[ChuckPartsData.Glove_Mitt_L];
                var rightMitt = puppet.parts[ChuckPartsData.Glove_Mitt_R];
                if (leftMitt == null || rightMitt == null)
                    throw new InvalidOperationException("[RidePosePreview] The rig is missing a mitt pivot.");
                Vector3 leftGrip = RideHolds.Point(truck, RideHolds.GripMetres(hold, true));
                Vector3 rightGrip = RideHolds.Point(truck, RideHolds.GripMetres(hold, false));
                float left = Vector3.Distance(leftMitt.position, leftGrip);
                float right = Vector3.Distance(rightMitt.position, rightGrip);
                Debug.Log($"[RidePosePreview] {HoldName(hold)} mitt-to-grip: L={left:F3} m R={right:F3} m");
                if (left > 0.18f || right > 0.18f)
                    throw new InvalidOperationException($"[RidePosePreview] {HoldName(hold)} mitt is too far from the grip target.");
            }

            Save(camera, path);
            Debug.Log("[RidePosePreview] wrote " + path);
        }
        finally { UnityEngine.Object.DestroyImmediate(rider); }
    }

    static void DisableEmbeddedCameras(GameObject root)
    {
        foreach (var camera in root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
    }

    static void BuildActualRideKit(GameObject truck)
    {
        // The kit is normally created by RideTruck.KitTick in Play Mode; prefab-only previews omit its rails.
        // Invoke the production builder in this disposable scene, with no gameplay/network session or shop save access.
        var ride = truck.GetComponent<RideTruck>();
        bool temporaryComponent = ride == null;
        if (temporaryComponent) ride = truck.AddComponent<RideTruck>();
        var build = typeof(RideTruck).GetMethod("BuildKit", BindingFlags.NonPublic | BindingFlags.Instance);
        var kitField = typeof(RideTruck).GetField("kit", BindingFlags.NonPublic | BindingFlags.Instance);
        if (build == null || kitField == null)
            throw new MissingMethodException("[RidePosePreview] RideTruck.BuildKit/kit preview bridge needs updating.");
        try
        {
            build.Invoke(ride, new object[] { 0, 0, true });
            var kit = truck.transform.Find("F1 Ride Kit");
            if (kit == null || kit.Find("RearGripBar") == null ||
                kit.GetComponentsInChildren<Renderer>(true).Length < 13)
                throw new InvalidOperationException("[RidePosePreview] Actual F1 kit rails/bar were not built.");
            Debug.Log("[RidePosePreview] Production F1 ride kit rendered (rear grab bar + side ladders).");
        }
        finally
        {
            // RideTruck.OnDestroy uses runtime Destroy(kit); in Edit Mode the whole throwaway scene owns the kit.
            if (temporaryComponent)
            {
                kitField.SetValue(ride, null);
                UnityEngine.Object.DestroyImmediate(ride);
            }
        }
    }

    static void PositionCamera(Camera camera, Transform truck, int hold)
    {
        int side = RideHolds.Side(hold);
        bool rung = RideHolds.IsRung(hold);
        Vector3 look = RideHolds.PoseOn(truck, hold).position + Vector3.up * 0.75f;
        Vector3 offset = rung ? new Vector3(side * 4.7f, 2.25f, -3.0f)
                              : new Vector3(side * 3.2f, 2.15f, -5.6f);
        Vector3 position = look + truck.rotation * offset;
        camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(look - position, Vector3.up));
    }

    static string HoldName(int hold)
    {
        switch (hold)
        {
            case RideHolds.RearLeft: return "rear-left";
            case RideHolds.RearRight: return "rear-right";
            case RideHolds.RungLeft: return "rung-left";
            default: return "rung-right";
        }
    }

    static void Save(Camera camera, string path)
    {
        var texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        Texture2D pixels = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            if (!texture.Create()) throw new InvalidOperationException("[RidePosePreview] Could not create render texture.");
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

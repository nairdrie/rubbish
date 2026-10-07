using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the main menu's live backdrop (Docs/UIPass.md §4.7; package UP-4). Idempotent: menu <c>Rubbish/Setup/Menu
/// Diorama</c> compares a stamp (setup version + the source prefabs' sha256) and changes nothing when it matches;
/// <c>(force rebuild)</c> rebuilds everything.
/// <list type="number">
/// <item><b>MenuChuck.prefab</b> and <b>MenuTruck.prefab</b> (<c>Assets/Prefabs/UI/</c>): copies of <c>Player.prefab</c> and
/// <c>Truck V2.prefab</c> assembled in a preview scene and stripped with an <b>allow-list</b> (renderers, Animator,
/// ChuckPuppet + parts + googly eyes, lights, the FEED ME text), repeated until nothing more can be removed (RequireComponent
/// chains are respected, so Unity never logs a refused removal). No NetworkObject, Rigidbody, controller, input, Cinemachine
/// camera or AudioListener survives; each gets one click collider. <c>Player.prefab</c> and <c>Truck V2.prefab</c> are only
/// read, never saved.</item>
/// <item><b>MenuDiorama</b> root in <c>MainMenuScene.unity</c> (deleted and rebuilt): a 40 × 20 m golden-hour curb (road,
/// curb, sidewalk, lawn with the Neighborhood materials), a suburban house, fence, street lamp, mailbox, a procedural wheelie
/// bin with a hinged lid and a raccoon anchor, three bag spots, the truck, Chuck and three hidden crew copies (Dot, Ozzie,
/// Bert), a sun, and <see cref="MenuDiorama"/> wired to all of it. The scene's Main Camera gets the 3/4 view; the old
/// Directional Light is switched off. Only that scene is saved; Bootstrap is never touched.</item>
/// </list>
/// Run it after <c>Setup_Character</c> and <c>Setup_Coop</c> so the copies match (§10.8 step 4).
/// </summary>
public static class Setup_MenuDiorama
{
    public const string ScenePath = "Assets/Scenes/MainMenuScene.unity";
    public const string PrefabFolder = "Assets/Prefabs/UI";
    public const string ChuckPrefabPath = PrefabFolder + "/MenuChuck.prefab";
    public const string TruckPrefabPath = PrefabFolder + "/MenuTruck.prefab";
    const string PlayerSource = "Assets/Prefabs/Player.prefab";
    const string TruckSource = "Assets/Prefabs/Truck V2.prefab";
    const string RootName = "MenuDiorama";
    const int Version = 3;
    const string Tag = "[Setup_MenuDiorama]";

    static readonly string Mats = "Assets/Materials/Generated/Neighborhood/";

    [MenuItem("Rubbish/Setup/Menu Diorama")]
    public static void RunMenu() => Run(false);

    [MenuItem("Rubbish/Setup/Menu Diorama (force rebuild)")]
    public static void RunForce() => Run(true);

    /// <summary>Build or refresh everything; returns (and logs) a one-line summary.</summary>
    public static string Run(bool force)
    {
        var log = new StringBuilder(Tag + " ");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError(Tag + " Exit play mode first.");
            return Tag + " play mode";
        }
        var active = SceneManager.GetActiveScene();
        bool activeWasDirty = active.isDirty;
        int changes = 0;
        try
        {
            EnsureFolder(PrefabFolder);
            var chuck = EnsureCopy(PlayerSource, ChuckPrefabPath, StripChuck, force, log, ref changes);
            var truck = EnsureCopy(TruckSource, TruckPrefabPath, StripTruck, force, log, ref changes);
            if (chuck == null || truck == null) throw new InvalidOperationException("prefab copies missing");
            string stamp = $"v{Version}|chuck:{FileHash(ChuckPrefabPath)}|truck:{FileHash(TruckPrefabPath)}";
            BuildScene(chuck, truck, stamp, force, log, ref changes);
        }
        catch (Exception e)
        {
            log.Append("FAILED ").Append(e.Message).Append("; ");
            Debug.LogException(e);
        }
        if (!activeWasDirty && active.IsValid() && active.isDirty)
            log.Append($"WARNING the open scene {active.name} got marked dirty during the run; don't save it; ");
        log.Append(changes == 0 ? "no changes." : $"{changes} change(s) saved.");
        Debug.Log(log.ToString());
        return log.ToString();
    }

    // =====================================================================
    // prefab copies
    // =====================================================================

    static GameObject EnsureCopy(string sourcePath, string copyPath, Action<GameObject, StringBuilder> strip, bool force,
                                 StringBuilder log, ref int changes)
    {
        string srcHash = FileHash(sourcePath);
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(copyPath);
        if (!force && existing != null)
        {
            var st = existing.GetComponent<MenuPrefabStamp>();
            if (st != null && st.version == Version && st.sourceHash == srcHash && st.source == sourcePath)
            {
                log.Append($"{System.IO.Path.GetFileName(copyPath)} up to date; ");
                return existing;
            }
        }
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        if (src == null) throw new InvalidOperationException("missing " + sourcePath);

        var preview = EditorSceneManager.NewPreviewScene();
        GameObject inst = null;
        try
        {
            inst = (GameObject)PrefabUtility.InstantiatePrefab(src, preview);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = System.IO.Path.GetFileNameWithoutExtension(copyPath);
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            int removed = StripToAllowList(inst);
            strip(inst, log);
            var stamp = inst.GetComponent<MenuPrefabStamp>();
            if (stamp == null) stamp = inst.AddComponent<MenuPrefabStamp>();
            stamp.source = sourcePath;
            stamp.sourceHash = srcHash;
            stamp.version = Version;
            var saved = PrefabUtility.SaveAsPrefabAsset(inst, copyPath, out bool ok);
            if (!ok || saved == null) throw new InvalidOperationException("couldn't save " + copyPath);
            changes++;
            log.Append($"{System.IO.Path.GetFileName(copyPath)} built ({removed} components stripped, " +
                       $"{saved.GetComponentsInChildren<Transform>(true).Length} objects); ");
            return saved;
        }
        finally
        {
            if (inst != null) UnityEngine.Object.DestroyImmediate(inst);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    /// <summary>What a menu copy may keep (anything else is removed).</summary>
    static bool Allowed(Component c)
    {
        if (c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer || c is Animator || c is Light ||
            c is LODGroup || c is TMPro.TMP_Text || c is MenuPrefabStamp)
            return true;
        if (c is ChuckPuppet || c is ChuckPart || c is ChuckGooglyEye || c is DerpyWobble) return true;
        var n = c.GetType().FullName ?? "";
        return n == "UnityEngine.Rendering.Universal.UniversalAdditionalLightData";
    }

    /// <summary>
    /// Remove every component not on the allow-list, in passes: a component another one still requires
    /// ([RequireComponent]) waits for the next pass, so Unity never refuses (and logs) a removal. Returns how many went.
    /// </summary>
    static int StripToAllowList(GameObject root)
    {
        int removed = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        for (int pass = 0; pass < 12; pass++)
        {
            int before = removed;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || Allowed(c)) continue;
                if (RequiredByOthers(c)) continue;
                UnityEngine.Object.DestroyImmediate(c);
                removed++;
            }
            if (removed == before) break;
        }
        var left = root.GetComponentsInChildren<Component>(true).Where(c => c != null && !Allowed(c)).Select(c => c.GetType().Name).ToList();
        if (left.Count > 0) throw new InvalidOperationException("couldn't strip: " + string.Join(", ", left.Distinct()));
        return removed;
    }

    static bool RequiredByOthers(Component c)
    {
        var t = c.GetType();
        foreach (var other in c.GetComponents<Component>())
        {
            if (other == null || other == c) continue;
            foreach (RequireComponent rc in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
            {
                if (rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(t)) return true;
                if (rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(t)) return true;
                if (rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(t)) return true;
            }
        }
        return false;
    }

    /// <summary>Remove empty leaf objects (stripped camera rigs, wheel colliders, triggers) bottom-up.</summary>
    static int PruneEmpty(Transform root, Func<Transform, bool> keep)
    {
        int n = 0;
        for (int guard = 0; guard < 8; guard++)
        {
            bool any = false;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || t.childCount > 0 || keep(t)) continue;
                if (t.GetComponents<Component>().Length > 1) continue; // has something besides its Transform
                UnityEngine.Object.DestroyImmediate(t.gameObject);
                n++;
                any = true;
            }
            if (!any) break;
        }
        return n;
    }

    static void StripChuck(GameObject chuck, StringBuilder log)
    {
        // the follow-camera rig and camera root are empty now
        foreach (var name in new[] { "PlayerFollowCamera", "PlayerCameraRoot" })
        {
            var t = chuck.transform.Find(name);
            if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
        }
        var puppet = chuck.GetComponent<ChuckPuppet>();
        if (puppet == null) throw new InvalidOperationException("Player.prefab has no ChuckPuppet (run Rubbish/Setup/Character)");
        var an = chuck.GetComponent<Animator>();
        if (an != null)
        {
            an.applyRootMotion = false;
            an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        // one click collider (no Rigidbody: static)
        var cap = chuck.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 1.0f, 0f);
        cap.height = 2.0f;
        cap.radius = 0.45f;
        cap.isTrigger = true;
    }

    static void StripTruck(GameObject truck, StringBuilder log)
    {
        foreach (var name in new[] { "TruckFollowCamera", "TruckCameraRoot", "DriverEjectPosition" })
        {
            var t = truck.transform.Find(name);
            if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
        }
        int pruned = PruneEmpty(truck.transform, t => t.name == "Hopper" || t.name == "MouthVisual" || t.parent == null);
        foreach (var l in truck.GetComponentsInChildren<Light>(true)) l.enabled = false; // MenuDiorama flashes them
        // one click box around the body
        var b = LocalBounds(truck.transform);
        var box = truck.AddComponent<BoxCollider>();
        box.center = b.center;
        box.size = b.size;
        box.isTrigger = true;
        log.Append($"truck pruned {pruned} empty objects; ");
    }

    static Bounds LocalBounds(Transform root)
    {
        var rs = root.GetComponentsInChildren<Renderer>(true);
        var w2l = root.worldToLocalMatrix;
        bool any = false;
        var b = new Bounds();
        foreach (var r in rs)
        {
            if (r is SkinnedMeshRenderer) continue;
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = w2l.MultiplyPoint3x4(c);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    // =====================================================================
    // the scene
    // =====================================================================

    // Layout (world metres; road along X, curb at z 0, houses toward +Z, the camera on the street side looking +Z)
    static readonly Vector3 TruckPos = new Vector3(-2.1f, 0f, -1.75f);      // faces −X; rear (hopper) toward +X
    static readonly Vector3 ChuckPos = new Vector3(1.35f, 0f, -0.55f);
    const float ChuckYaw = -140f;                                            // 3/4 to the camera, toward the hopper
    static readonly Vector3 BinPos = new Vector3(3.55f, 0.15f, 1.2f);
    static readonly Vector3[] BagPos = { new Vector3(2.15f, 0.15f, 0.55f), new Vector3(2.75f, 0.15f, 0.78f), new Vector3(2.4f, 0.15f, 1.2f) };
    static readonly Vector3 FocusPos = new Vector3(1.6f, 1.0f, -0.3f);

    static void BuildScene(GameObject chuckPrefab, GameObject truckPrefab, string stamp, bool force, StringBuilder log, ref int changes)
    {
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool opened = false;
        if (!scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            opened = true;
        }
        else if (scene.isDirty) throw new InvalidOperationException("MainMenuScene is open with unsaved changes; save or close it first");
        var prevActive = SceneManager.GetActiveScene();
        try
        {
            var old = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
            if (!force && old != null)
            {
                var md = old.GetComponent<MenuDiorama>();
                if (md != null && md.stamp == stamp)
                {
                    log.Append($"scene up to date ({old.GetComponentsInChildren<Transform>(true).Length} objects under {RootName}); ");
                    return;
                }
            }
            // new objects (primitives, prefab instances) land in the menu scene, never in the open Bootstrap
            SceneManager.SetActiveScene(scene);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject(RootName);
            var d = root.AddComponent<MenuDiorama>();
            d.stamp = stamp;
            d.frame = new Vector2(8.2f, 4.4f);
            Build(root.transform, d, chuckPrefab, truckPrefab, scene);
            int count = root.GetComponentsInChildren<Transform>(true).Length;

            // camera + old light
            var camGo = scene.GetRootGameObjects().FirstOrDefault(g => g.CompareTag("MainCamera")) ??
                        scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<Camera>() != null);
            if (camGo != null)
            {
                var cam = camGo.GetComponent<Camera>();
                d.cam = cam;
                cam.fieldOfView = d.camFov;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.nearClipPlane = 0.2f;
                cam.farClipPlane = 400f;
                d.ComputeCameraPose(16f / 9f, 0.36f, 0f, 0f, 0f, out var pos, out var rot);
                camGo.transform.SetPositionAndRotation(pos, rot);
            }
            var oldLight = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Directional Light");
            if (oldLight != null && oldLight.activeSelf) oldLight.SetActive(false);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("couldn't save " + ScenePath);
            changes++;
            log.Append($"scene rebuilt ({count} objects under {RootName}, camera {(camGo != null ? "set" : "MISSING")}); ");
        }
        finally
        {
            if (prevActive.IsValid() && prevActive.isLoaded && SceneManager.GetActiveScene() != prevActive) SceneManager.SetActiveScene(prevActive);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    static void Build(Transform root, MenuDiorama d, GameObject chuckPrefab, GameObject truckPrefab, Scene scene)
    {
        var set = Node("Set", root, Vector3.zero);

        // ---- ground: road, curb, sidewalk, lawn
        Slab("Road", set, new Vector3(0f, -0.1f, -5f), new Vector3(44f, 0.2f, 10f), Mat("NB_Asphalt"));
        Slab("Curb", set, new Vector3(0f, -0.025f, 0.15f), new Vector3(44f, 0.35f, 0.3f), Mat("NB_Curb"));
        Slab("Sidewalk", set, new Vector3(0f, -0.025f, 1.3f), new Vector3(44f, 0.35f, 2f), Mat("NB_Concrete"));
        Slab("Lawn", set, new Vector3(0f, -0.06f, 9.15f), new Vector3(44f, 0.3f, 13.7f), Mat("NB_Grass"));
        Slab("Driveway", set, new Vector3(-6.5f, 0.1f, 6.5f), new Vector3(3.2f, 0.02f, 8.4f), Mat("NB_Driveway"));
        for (int i = 0; i < 11; i++)
            Slab("CentreDash", set, new Vector3(-20f + i * 4f, 0.005f, -6.2f), new Vector3(2f, 0.01f, 0.16f), Mat("NB_LineYellow"));

        // ---- props from the town pack
        Prop("House", "Assets/Suburbs Town Pack/PREFABS/Buildings/SM_house_suburb_003.prefab", set, new Vector3(1.5f, 0.1f, 9.5f), 180f, 1f);
        Prop("StreetLamp", "Assets/Suburbs Town Pack/PREFABS/Props/SM_streetlamp_01.prefab", set, new Vector3(-4.6f, 0.15f, 0.75f), 0f, 1f);
        Prop("Mailbox", "Assets/Suburbs Town Pack/PREFABS/Props/SM_mailbox_01.prefab", set, new Vector3(5.6f, 0.15f, 2.05f), 180f, 1f);
        for (int i = 0; i < 3; i++)
            Prop("Fence" + i, "Assets/Suburbs Town Pack/PREFABS/Modular/SM_garden_fence.prefab", set, new Vector3(6.2f + i * 2.4f, 0.1f, 2.7f), 0f, 1f);
        var lampLight = new GameObject("LampLight").AddComponent<Light>();
        lampLight.transform.SetParent(set, false);
        lampLight.transform.localPosition = new Vector3(-4.6f, 4.2f, 0.75f);
        lampLight.type = LightType.Point;
        lampLight.range = 9f;
        lampLight.intensity = 2.5f;
        lampLight.color = new Color(1f, 0.82f, 0.55f);
        lampLight.enabled = false; // M1: night in the Continue Town turns it on

        // ---- the wheelie bin (procedural: a hinged lid the raccoon can pop)
        var bin = Node("WheelieBin", set, BinPos);
        bin.localRotation = Quaternion.Euler(0f, -18f, 0f);
        var binBody = Prim("Body", PrimitiveType.Cube, bin, new Vector3(0f, 0.5f, 0f), new Vector3(0.62f, 1f, 0.7f), Mat("NB_SignGreen"), keepCollider: true);
        Prim("Rim", PrimitiveType.Cube, bin, new Vector3(0f, 0.97f, 0f), new Vector3(0.66f, 0.06f, 0.74f), Mat("NB_SignGreen"));
        Prim("Handle", PrimitiveType.Cube, bin, new Vector3(0f, 0.9f, 0.4f), new Vector3(0.5f, 0.05f, 0.08f), Mat("NB_Rubber"));
        for (int s = -1; s <= 1; s += 2)
        {
            var w = Prim("Wheel", PrimitiveType.Cylinder, bin, new Vector3(0.3f * s, 0.11f, 0.3f), new Vector3(0.2f, 0.04f, 0.2f), Mat("NB_Rubber"));
            w.localRotation = Quaternion.Euler(0f, 0f, 90f);
        }
        var hinge = Node("LidHinge", bin, new Vector3(0f, 1.0f, 0.37f));
        Prim("Lid", PrimitiveType.Cube, hinge, new Vector3(0f, 0.03f, -0.38f), new Vector3(0.7f, 0.06f, 0.78f), Mat("NB_SignGreen"));
        Prim("LidLip", PrimitiveType.Cube, hinge, new Vector3(0f, 0.0f, -0.76f), new Vector3(0.34f, 0.07f, 0.06f), Mat("NB_Rubber"));
        // the critter is built at runtime (CritterLook, +Z forward); the bin's front (toward the street) is its −Z
        var racc = Node("RaccoonAnchor", bin, new Vector3(0f, 0.78f, -0.02f));
        racc.localRotation = Quaternion.Euler(0f, 180f, 0f);

        // ---- bag spots on the curb
        var spots = new List<Transform>();
        for (int i = 0; i < BagPos.Length; i++)
        {
            var s = Node("BagSpot" + i, set, BagPos[i]);
            s.localRotation = Quaternion.Euler(0f, 20f + i * 47f, 0f);
            spots.Add(s);
        }

        // ---- sun: golden hour from the camera's right
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(root, false);
        sun.type = LightType.Directional;
        sun.transform.rotation = Quaternion.Euler(16f, -58f, 0f);
        sun.color = new Color(1f, 0.78f, 0.56f);
        sun.intensity = 1.7f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.75f;

        // ---- the truck and the crew
        var truck = (GameObject)PrefabUtility.InstantiatePrefab(truckPrefab, scene);
        truck.name = "MenuTruck";
        truck.transform.SetParent(root, false);
        truck.transform.localPosition = TruckPos;
        truck.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
        var hopper = truck.transform.Find("Hopper");
        var mouth = hopper != null ? hopper.Find("MouthVisual") : null;

        var chuck = (GameObject)PrefabUtility.InstantiatePrefab(chuckPrefab, scene);
        chuck.name = "MenuChuck";
        chuck.transform.SetParent(root, false);
        chuck.transform.localPosition = ChuckPos;
        chuck.transform.localRotation = Quaternion.Euler(0f, ChuckYaw, 0f);

        var crewRoot = Node("Crew", root, Vector3.zero);
        string[] names = { "Dot", "Ozzie", "Bert" };
        // Dot rides the street-side rear step (she slides along the truck's flank to get there); Ozzie and Bert run in from
        // the right and line up on the road beside Chuck, clear of him from the camera
        Vector3 rear = TruckPos + new Vector3(2.45f, 0f, 0f);
        Vector3[] spotsPos = { rear + new Vector3(0.15f, 0.42f, -1.05f), new Vector3(2.6f, 0f, -1.65f), new Vector3(3.45f, 0f, -0.75f) };
        Vector3[] homes = { spotsPos[0] + new Vector3(-3.4f, 0f, 0f), new Vector3(8.5f, 0f, -1.8f), new Vector3(8.5f, 0f, -0.8f) };
        float[] yaws = { -150f, -150f, -140f };
        var crew = new List<ChuckPuppet>();
        var crewSpots = new List<Transform>();
        for (int i = 0; i < 3; i++)
        {
            var c = (GameObject)PrefabUtility.InstantiatePrefab(chuckPrefab, scene);
            c.name = "Menu" + names[i];
            c.transform.SetParent(crewRoot, false);
            c.transform.localPosition = homes[i];
            c.transform.localRotation = Quaternion.Euler(0f, yaws[i], 0f);
            var p = c.GetComponent<ChuckPuppet>();
            p.variantOverride = i + 1;
            crew.Add(p);
            crewSpots.Add(Node(names[i] + "Spot", crewRoot, spotsPos[i]));
            c.SetActive(false);
        }

        // ---- wire the component
        d.focus = Node("Focus", root, FocusPos);
        d.sun = sun;
        d.skybox = AssetDatabase.LoadAssetAtPath<Material>("Assets/Day-Night Skyboxes/Materials/SkySunset.mat");
        d.truck = truck.transform;
        d.hopperMouth = mouth != null ? mouth : hopper;
        d.truckLights = truck.GetComponentsInChildren<Light>(true);
        d.tailLights = truck.GetComponentsInChildren<Renderer>(true).Where(r => r.name == "RL Light" || r.name == "RR Light").ToArray();
        d.chuck = chuck.GetComponent<ChuckPuppet>();
        d.crew = crew.ToArray();
        d.crewSpots = crewSpots.ToArray();
        d.bin = bin;
        d.binLid = hinge;
        d.raccoonAnchor = racc;
        d.bagSpots = spots.ToArray();
        d.chuckClick = chuck.GetComponent<Collider>();
        d.truckClick = truck.GetComponent<Collider>();
        d.binClick = binBody.GetComponent<Collider>();
    }

    // ------------------------------------------------------------------ helpers

    static Transform Node(string name, Transform parent, Vector3 localPos)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        return t;
    }

    static Transform Slab(string name, Transform parent, Vector3 center, Vector3 size, Material m) =>
        Prim(name, PrimitiveType.Cube, parent, center, size, m);

    static Transform Prim(string name, PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material m, bool keepCollider = false)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        if (!keepCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        else
        {
            var col = go.GetComponent<Collider>();
            col.isTrigger = true; // a click target, nothing to collide with
        }
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        if (m != null) r.sharedMaterial = m;
        return go.transform;
    }

    static Transform Prop(string name, string path, Transform parent, Vector3 pos, float yaw, float scale)
    {
        var p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (p == null)
        {
            Debug.LogWarning($"{Tag} missing prop {path} (skipped)");
            return null;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(p, parent.gameObject.scene);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale *= scale;
        // set dressing: nothing physical (the diorama holds no Rigidbodies, and its colliders are only click targets)
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
        return go.transform;
    }

    static Material Mat(string name)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(Mats + name + ".mat");
        if (m == null) Debug.LogWarning($"{Tag} missing material {name}");
        return m;
    }

    static string FileHash(string assetPath)
    {
        var full = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), assetPath);
        if (!System.IO.File.Exists(full)) return "none";
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(System.IO.File.ReadAllBytes(full));
            return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // =====================================================================
    // read-only checks (acceptance UP4-4)
    // =====================================================================

    /// <summary>Counts in the saved menu scene and the copies (opens the scene additively if needed, never saves).</summary>
    public static string Verify()
    {
        var sb = new StringBuilder();
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool opened = false;
        if (!scene.isLoaded) { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive); opened = true; }
        try
        {
            int netObjs = 0, bodies = 0, colliders = 0, objects = 0, cams = 0, listeners = 0;
            foreach (var g in scene.GetRootGameObjects())
            {
                netObjs += g.GetComponentsInChildren<Unity.Netcode.NetworkObject>(true).Length;
                bodies += g.GetComponentsInChildren<Rigidbody>(true).Length;
                colliders += g.GetComponentsInChildren<Collider>(true).Length;
                cams += g.GetComponentsInChildren<Camera>(true).Length;
                listeners += g.GetComponentsInChildren<AudioListener>(true).Length;
                if (g.name == RootName) objects = g.GetComponentsInChildren<Transform>(true).Length;
            }
            sb.Append($"scene: NetworkObjects {netObjs}, Rigidbodies {bodies}, colliders {colliders}, cameras {cams}, listeners {listeners}, objects under {RootName} {objects}; ");
            foreach (var path in new[] { ChuckPrefabPath, TruckPrefabPath })
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (p == null) { sb.Append(path + " MISSING; "); continue; }
                var bad = p.GetComponentsInChildren<Component>(true).Where(c => c != null && !Allowed(c) && !(c is Collider)).Select(c => c.GetType().Name).Distinct().ToList();
                sb.Append($"{System.IO.Path.GetFileName(path)} sha {FileHash(path)} objects {p.GetComponentsInChildren<Transform>(true).Length} " +
                          $"colliders {p.GetComponentsInChildren<Collider>(true).Length} disallowed [{string.Join(",", bad)}]; ");
            }
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        return sb.ToString();
    }
}

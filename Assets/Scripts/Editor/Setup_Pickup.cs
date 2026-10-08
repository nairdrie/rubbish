using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Pickup system setup (idempotent — safe to re-run):
///   1. Registers the trash-sack props as RubbishAssets "sack/0".."sack/2".
///   2. Creates the marker/beam/bag/mouth materials under Assets/Materials/Generated/Pickup/ and
///      registers them as "mat/pickup_*".
///   3. Adds TrashCarrier to Assets/Prefabs/Player.prefab if missing.
///   4. Adds/refreshes the "Hopper" child on Assets/Prefabs/Truck V2.prefab: TruckHopper receiver,
///      a small kinematic trigger (CarryReceiverTrigger) behind the tailgate for physical throws,
///      a dark "mouth" slot with a hazard-yellow frame, and a bobbing indicator arrow.
///   5. Writes Assets/Resources/HouseDirectory.asset from the Neighborhood scene's PickupStops (read-only scene
///      access) so the CurbTrashSpawner knows every house from the moment the server starts.
///      (NeighborhoodBuilder rewrites it whenever it rebuilds the town.)
/// Also: Rubbish/Debug/Trash/* menu items (play mode) to put out / age / clear curb trash.
/// </summary>
public static class Setup_Pickup
{
    const string MatFolder = "Assets/Materials/Generated/Pickup";
    const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    const string TruckPrefabPath = "Assets/Prefabs/Truck V2.prefab";
    const int IgnoreRaycastLayer = 2;

    static readonly string[] SackPrefabs = { "SM_trash_sack_01", "SM_trash_sack_02", "SM_trash_sack_03" };

    [MenuItem("Rubbish/Setup/Pickup")]
    public static void Run()
    {
        var log = new StringBuilder("[Setup_Pickup] ");
        RegisterSacks(log);
        var mats = CreateMaterials(log);
        SetupPlayer(log);
        SetupTruck(log, mats);
        log.Append(RefreshHouseDirectory(NeighborhoodBuilder.DefaultScenePath));
        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString());
    }

    // =====================================================================
    // 5. House directory (living-world curb trash)
    // =====================================================================

    /// <summary>Read the PickupStops of a scene (opened additively + closed again if needed, never saved) and write the directory.</summary>
    public static string RefreshHouseDirectory(string scenePath)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "house directory skipped (play mode); ";
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null) return $"MISSING scene {scenePath} (house directory not written); ";
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        try
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                opened = true;
            }
            var stops = new List<PickupStop>();
            foreach (var root in scene.GetRootGameObjects())
                stops.AddRange(root.GetComponentsInChildren<PickupStop>(false));
            if (stops.Count == 0) return $"no PickupStops in {scenePath} (house directory not written); ";
            return WriteHouseDirectory(stops, scene.name) + "; ";
        }
        finally
        {
            if (opened && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>Write Assets/Resources/HouseDirectory.asset (stopId, address, capacity, wealth, position per house). Idempotent.</summary>
    public static string WriteHouseDirectory(IEnumerable<PickupStop> stops, string sceneName)
    {
        EnsureFolder(System.IO.Path.GetDirectoryName(HouseDirectory.AssetPath).Replace('\\', '/'));
        var dir = AssetDatabase.LoadAssetAtPath<HouseDirectory>(HouseDirectory.AssetPath);
        bool created = false;
        if (dir == null)
        {
            dir = ScriptableObject.CreateInstance<HouseDirectory>();
            AssetDatabase.CreateAsset(dir, HouseDirectory.AssetPath);
            created = true;
        }
        dir.sceneName = sceneName;
        dir.houses.Clear();
        var ids = new HashSet<string>();
        foreach (var s in stops)
        {
            if (s == null || string.IsNullOrEmpty(s.stopId) || !ids.Add(s.stopId)) continue;
            dir.houses.Add(new HouseDirectory.Entry
            {
                stopId = s.stopId, address = s.address, capacity = s.Capacity, wealth = s.wealth, position = s.transform.position,
                districtId = s.District.id,
            });
        }
        dir.houses.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
        EditorUtility.SetDirty(dir);
        AssetDatabase.SaveAssetIfDirty(dir);
        var perDistrict = new Dictionary<string, int>();
        foreach (var e in dir.houses) perDistrict[e.districtId] = perDistrict.TryGetValue(e.districtId, out var n) ? n + 1 : 1;
        var parts = new List<string>();
        foreach (var d in Districts.All) parts.Add($"{d.id} {(perDistrict.TryGetValue(d.id, out var c) ? c : 0)}");
        return $"[Setup_Pickup] house directory {(created ? "created" : "updated")}: {dir.houses.Count} houses ({string.Join(", ", parts)}) from {sceneName} -> {HouseDirectory.AssetPath}";
    }

    // =====================================================================
    // Debug menu (play mode)
    // =====================================================================

    [MenuItem("Rubbish/Debug/Trash/Midnight inspection now (raises Midnight)")]
    static void DebugMidnight()
    {
        var r = CurbTrashSpawner.RunMidnight();
        var parts = new List<string>();
        foreach (var kv in r.clean01) parts.Add($"{kv.Key} {kv.Value:P0} bonus {Economy.Money(r.cleanBonusPaid[kv.Key])} neglected {r.neglectedCurbs[kv.Key]}");
        Debug.Log($"[Trash debug] midnight (day {r.day}): {string.Join("; ", parts)}; fines {Economy.Money(r.finesPaid)}; bags into truck {r.bagsIntoTruckToday}");
    }
    [MenuItem("Rubbish/Debug/Trash/Midnight inspection now (raises Midnight)", true)]
    static bool DebugMidnightValid() => Application.isPlaying;

    [MenuItem("Rubbish/Debug/Trash/Put out 10 bags")]
    static void DebugPutOut10() => Debug.Log($"[Trash debug] put out {CurbTrashSpawner.DebugPutOut(10)} bags");
    [MenuItem("Rubbish/Debug/Trash/Put out 10 bags", true)]
    static bool DebugPutOut10Valid() => Application.isPlaying;

    [MenuItem("Rubbish/Debug/Trash/Age all curb bags +4 h")]
    static void DebugAge4() { CurbTrashSpawner.DebugAgeAll(4); Debug.Log("[Trash debug] curb bags aged 4 h"); }
    [MenuItem("Rubbish/Debug/Trash/Age all curb bags +4 h", true)]
    static bool DebugAge4Valid() => Application.isPlaying;

    [MenuItem("Rubbish/Debug/Trash/Clear every curb")]
    static void DebugClear() { CurbTrashSpawner.DebugClearAll(); Debug.Log("[Trash debug] curbs cleared"); }
    [MenuItem("Rubbish/Debug/Trash/Clear every curb", true)]
    static bool DebugClearValid() => Application.isPlaying;

    [MenuItem("Rubbish/Debug/Trash/Log route + cleanliness")]
    static void DebugLog()
    {
        var r = TrashRoute.Current;
        var sb = new StringBuilder($"[Trash debug] {GameClock.ClockText} day {GameClock.Day}: {GameSession.CurbBags.Count} curb bags, late {CurbTrash.LateBagCount}, neglected stops {CurbTrash.NeglectedStopCount}, {CityCleanliness.Label}");
        if (r != null)
        {
            var from = PlayerController.Local != null ? PlayerController.Local.transform.position : Vector3.zero;
            foreach (var s in r.PlannedRoute(from, 5)) sb.Append($"\n  -> {s.address}: {s.StatusLabel} (score {r.Score(s, from):F2})");
        }
        Debug.Log(sb.ToString());
    }
    [MenuItem("Rubbish/Debug/Trash/Log route + cleanliness", true)]
    static bool DebugLogValid() => Application.isPlaying;

    // =====================================================================
    // 1. Sacks
    // =====================================================================

    static void RegisterSacks(StringBuilder log)
    {
        for (int i = 0; i < SackPrefabs.Length; i++)
        {
            string path = FindPrefab(SackPrefabs[i]);
            if (path == null)
            {
                log.Append($"MISSING sack prefab {SackPrefabs[i]}; ");
                Debug.LogWarning($"[Setup_Pickup] Could not find prefab {SackPrefabs[i]}");
                continue;
            }
            RubbishAssets.EditorRegisterPrefab("sack/" + i, path);
        }
        log.Append($"sacks registered ({SackPrefabs.Length}); ");
    }

    static string FindPrefab(string name)
    {
        string fallback = null;
        foreach (var guid in AssetDatabase.FindAssets(name + " t:Prefab"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(p) != name) continue;
            if (p.StartsWith("Assets/Suburbs Town Pack/PREFABS/")) return p;
            if (fallback == null) fallback = p;
        }
        return fallback;
    }

    // =====================================================================
    // 2. Materials
    // =====================================================================

    struct Mats { public Material marker, beam, bag, mouth, hazard; }

    static Mats CreateMaterials(StringBuilder log)
    {
        EnsureFolder(MatFolder);
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (lit == null || unlit == null)
        {
            Debug.LogError("[Setup_Pickup] URP Lit/Unlit shader not found; skipping materials.");
            return default;
        }

        var m = new Mats();

        m.marker = GetOrCreateMat("pickup_marker", unlit);
        m.marker.SetColor("_BaseColor", Color.white); // tinted per renderer via property blocks
        m.marker.enableInstancing = true;

        m.beam = GetOrCreateMat("pickup_beam", unlit);
        m.beam.SetColor("_BaseColor", new Color(1f, 0.6f, 0.1f, 0.22f));
        MakeTransparent(m.beam);

        m.bag = GetOrCreateMat("pickup_bag", lit);
        m.bag.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.06f));
        m.bag.SetFloat("_Smoothness", 0.62f);
        m.bag.SetFloat("_Metallic", 0f);

        m.mouth = GetOrCreateMat("pickup_mouth", unlit);
        m.mouth.SetColor("_BaseColor", new Color(0.012f, 0.012f, 0.012f));

        m.hazard = GetOrCreateMat("pickup_hazard", lit);
        m.hazard.SetColor("_BaseColor", new Color(1f, 0.74f, 0.05f));
        m.hazard.SetFloat("_Smoothness", 0.35f);
        m.hazard.SetFloat("_Metallic", 0f);

        foreach (var mat in new[] { m.marker, m.beam, m.bag, m.mouth, m.hazard }) EditorUtility.SetDirty(mat);

        RubbishAssets.EditorRegisterMaterial(PickupVisuals.MarkerMaterialKey, m.marker);
        RubbishAssets.EditorRegisterMaterial(PickupVisuals.BeamMaterialKey, m.beam);
        RubbishAssets.EditorRegisterMaterial(PickupVisuals.BagMaterialKey, m.bag);
        RubbishAssets.EditorRegisterMaterial("mat/pickup_mouth", m.mouth);
        RubbishAssets.EditorRegisterMaterial("mat/pickup_hazard", m.hazard);
        log.Append("materials ok (mat/pickup_marker, _beam, _bag, _mouth, _hazard); ");
        return m;
    }

    static Material GetOrCreateMat(string name, Shader shader)
    {
        string path = $"{MatFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
        }
        return mat;
    }

    static void MakeTransparent(Material m)
    {
        m.SetFloat("_Surface", 1f);         // Transparent
        m.SetFloat("_Blend", 0f);           // Alpha
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Back);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.SetShaderPassEnabled("DepthOnly", false);
        m.SetShaderPassEnabled("ShadowCaster", false);
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    // =====================================================================
    // 3. Player
    // =====================================================================

    static void SetupPlayer(StringBuilder log)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath) == null)
        {
            log.Append("MISSING Player.prefab; ");
            return;
        }
        var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (root.GetComponent<TrashCarrier>() == null)
            {
                root.AddComponent<TrashCarrier>();
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                log.Append("TrashCarrier added to Player; ");
            }
            else log.Append("Player already has TrashCarrier; ");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // =====================================================================
    // 4. Truck hopper
    // =====================================================================

    static void SetupTruck(StringBuilder log, Mats mats)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(TruckPrefabPath) == null)
        {
            log.Append("MISSING Truck V2.prefab; ");
            return;
        }
        var root = PrefabUtility.LoadPrefabContents(TruckPrefabPath);
        try
        {
            string info = BuildHopper(root.transform, mats.hazard, mats.mouth);
            PrefabUtility.SaveAsPrefabAsset(root, TruckPrefabPath);
            log.Append(info);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    /// <summary>
    /// Adds/refreshes the "Hopper" child (TruckHopper + trigger + mouth + indicator) under a truck root
    /// (prefab contents or a scratch instance). Returns a log line.
    /// </summary>
    public static string BuildHopper(Transform t, Material hazard, Material mouth)
    {
        float s = Mathf.Max(0.0001f, t.lossyScale.x);

        // Find the tailgate (root-local) so the mouth sits flush on it.
        Vector3 hopperLocal = new Vector3(0f, 1.9f, -3.0f);
        float rearTop = 3.73f, halfWidth = 1.35f;
        float faceBottomZ = float.NaN, faceTopZ = float.NaN;
        const float mouthHalfH = 0.27f; // world m (frame is 0.54 tall)
        var body = t.Find("TruckBody");
        var mf = body != null ? body.GetComponent<MeshFilter>() : null;
        if (mf != null && mf.sharedMesh != null)
        {
            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var toRoot = t.worldToLocalMatrix * body.localToWorldMatrix;
            var rv = new Vector3[verts.Length];
            float minZ = float.MaxValue;
            for (int i = 0; i < verts.Length; i++) { rv[i] = toRoot.MultiplyPoint3x4(verts[i]); minZ = Mathf.Min(minZ, rv[i].z); }
            float yl = float.MaxValue, yh = float.MinValue, xw = 0f;
            foreach (var p in rv)
            {
                if (p.z > minZ + 0.4f) continue;
                yl = Mathf.Min(yl, p.y); yh = Mathf.Max(yh, p.y); xw = Mathf.Max(xw, Mathf.Abs(p.x));
            }
            if (yl < yh)
            {
                rearTop = yh; halfWidth = xw;
                // Mouth centered ~0.35 m (world) above the tailgate's bottom lip.
                float yc = yl + 0.35f / s;
                // Outermost surface across the plate's width (straps/ribs stick out past the panel).
                float halfPlate = Mathf.Min(0.75f / s, xw * 0.8f);
                float zc = OutermostZ(rv, tris, halfPlate, yc);
                faceBottomZ = OutermostZ(rv, tris, halfPlate, yc - mouthHalfH / s);
                faceTopZ = OutermostZ(rv, tris, halfPlate, yc + mouthHalfH / s);
                if (float.IsNaN(zc)) zc = minZ;
                // The toss target sits 0.14 m (world) behind the tailgate.
                hopperLocal = new Vector3(0f, yc, zc - 0.14f / s);
            }
        }

        // Reuse the Hopper object if present (others find it by name); rebuild our children.
        var hopperT = t.Find("Hopper");
        GameObject hopper;
        if (hopperT == null)
        {
            hopper = new GameObject("Hopper");
            hopper.transform.SetParent(t, false);
        }
        else hopper = hopperT.gameObject;
        hopper.transform.localPosition = hopperLocal;
        hopper.transform.localRotation = Quaternion.identity;
        hopper.transform.localScale = Vector3.one / s; // children are authored in world meters
        foreach (var childName in new[] { "HopperTrigger", "MouthVisual", "HopperIndicator" })
        {
            var old = hopper.transform.Find(childName);
            while (old != null)
            {
                Object.DestroyImmediate(old.gameObject);
                old = hopper.transform.Find(childName);
            }
        }

        var th = hopper.GetComponent<TruckHopper>();
        if (th == null) th = hopper.AddComponent<TruckHopper>();
        th.tossRange = 2.8f;

        // Tailgate surface relative to the hopper (world m), and its lean over the mouth's height.
        float faceZ = 0.14f;
        float tilt = 0f;
        if (!float.IsNaN(faceBottomZ) && !float.IsNaN(faceTopZ))
        {
            float zb = (faceBottomZ - hopperLocal.z) * s, zt = (faceTopZ - hopperLocal.z) * s;
            faceZ = (zb + zt) * 0.5f;
            tilt = Mathf.Atan2(zb - zt, 2f * mouthHalfH) * Mathf.Rad2Deg; // top leaning out => negative X rotation
        }
        float faceWidth = Mathf.Max(1.0f, halfWidth * 2f * s);

        // --- trigger for physical throws: behind the tailgate, well above the wheels ---
        var trig = new GameObject("HopperTrigger");
        trig.layer = IgnoreRaycastLayer;
        trig.transform.SetParent(hopper.transform, false);
        trig.transform.localPosition = new Vector3(0f, 0.05f, faceZ - 0.08f - 0.5f);
        var box = trig.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(Mathf.Min(1.8f, faceWidth), 1.1f, 1.0f);
        // Own kinematic body keeps the trigger out of the truck's compound (mass/inertia/COM untouched).
        var rb = trig.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        var crt = trig.AddComponent<CarryReceiverTrigger>();
        crt.onlyPlayerReleased = true; // unloaded bags spawned at the hopper must not get re-swallowed

        // --- mouth: dark slot with a hazard frame on the tailgate ---
        var mouthRoot = new GameObject("MouthVisual");
        mouthRoot.transform.SetParent(hopper.transform, false);
        mouthRoot.transform.localPosition = new Vector3(0f, 0f, faceZ);
        mouthRoot.transform.localRotation = Quaternion.Euler(-tilt, 0f, 0f);
        float mouthW = Mathf.Min(1.35f, faceWidth * 0.75f);
        var frame = MakeCube("Frame", mouthRoot.transform, new Vector3(0f, 0f, -0.012f), new Vector3(mouthW + 0.14f, 0.54f, 0.02f), hazard);
        var slot = MakeCube("Slot", mouthRoot.transform, new Vector3(0f, 0f, -0.03f), new Vector3(mouthW, 0.4f, 0.02f), mouth);
        frame.layer = slot.layer = 0;
        var labelGo = new GameObject("MouthLabel");
        labelGo.transform.SetParent(mouthRoot.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, 0f, -0.045f);
        var label = labelGo.AddComponent<TMPro.TextMeshPro>();
        label.text = "FEED ME";
        label.fontSize = 2.1f;
        label.fontStyle = TMPro.FontStyles.Bold;
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        label.color = new Color(1f, 0.78f, 0.1f);
        label.rectTransform.sizeDelta = new Vector2(mouthW, 0.4f);
        var lr = labelGo.GetComponent<MeshRenderer>();
        if (lr != null) lr.shadowCastingMode = ShadowCastingMode.Off;

        // --- indicator arrow above the mouth (hidden until you carry something nearby) ---
        var ind = new GameObject("HopperIndicator");
        ind.transform.SetParent(hopper.transform, false);
        float aboveRoof = (rearTop - hopperLocal.y) * s + 0.3f;
        ind.transform.localPosition = new Vector3(0f, Mathf.Max(1.0f, aboveRoof), faceZ - 0.45f);
        var arrow = ind.AddComponent<BobbingArrow>();
        arrow.color = PickupVisuals.GoodColor;
        arrow.size = 0.6f;
        arrow.bobAmplitude = 0.12f;
        arrow.bobSpeed = 4f;
        arrow.spinSpeed = 150f;
        arrow.pulse = 0.06f;

        th.squashTarget = mouthRoot.transform;
        th.indicator = ind.transform;
        return $"Truck hopper at local {hopperLocal} (world offset {hopperLocal * s}), trigger+mouth+indicator rebuilt; ";
    }

    /// <summary>Smallest RearSurfaceZ over x in [-halfWidth, halfWidth] at height y (NaN if nothing hit).</summary>
    static float OutermostZ(Vector3[] v, int[] tris, float halfWidth, float y)
    {
        float best = float.NaN;
        const int samples = 13;
        for (int i = 0; i < samples; i++)
        {
            float x = Mathf.Lerp(-halfWidth, halfWidth, i / (float)(samples - 1));
            float z = RearSurfaceZ(v, tris, x, y);
            if (!float.IsNaN(z) && (float.IsNaN(best) || z < best)) best = z;
        }
        return best;
    }

    /// <summary>First hit (smallest z) of a +Z ray at (x, y) against the mesh triangles (root-local), or NaN.</summary>
    static float RearSurfaceZ(Vector3[] v, int[] tris, float x, float y)
    {
        float best = float.NaN;
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
            // Barycentric test in the XY plane, then interpolate z.
            float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(d) < 1e-9f) continue;
            float w1 = ((b.y - c.y) * (x - c.x) + (c.x - b.x) * (y - c.y)) / d;
            float w2 = ((c.y - a.y) * (x - c.x) + (a.x - c.x) * (y - c.y)) / d;
            float w3 = 1f - w1 - w2;
            if (w1 < 0f || w2 < 0f || w3 < 0f) continue;
            float z = w1 * a.z + w2 * b.z + w3 * c.z;
            if (float.IsNaN(best) || z < best) best = z;
        }
        return best;
    }

    static GameObject MakeCube(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        if (mat != null) r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        return go;
    }
}

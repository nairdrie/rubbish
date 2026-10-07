using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// WorldLife package B setup (Docs/WorldLife.md §6, §12.1). Idempotent; run by the umbrella <c>Rubbish/Setup/WorldLife</c>
/// after the Neighborhood build (A), or on its own:
/// <list type="number">
/// <item>Materials <c>Assets/Materials/Generated/WorldLife/Knock/worldlife_knock_*.mat</c> → RubbishAssets
/// <c>mat/worldlife_knock_*</c> (recipe colours, the unlit lamp bulb, geyser water + spray).</item>
/// <item>Proxy prefabs <c>Assets/Prefabs/WorldLife/Knock_&lt;prefab&gt;.prefab</c> for every pack prefab that is knockable
/// (the pack mesh + materials, one primitive BoxCollider, a Rigidbody with the §6.1 mass; a lamp's bulb is unlit) →
/// RubbishAssets <c>knock/&lt;prefab&gt;</c>, plus <c>knock/SM_lemon</c> for the lemonade stand's lemons.</item>
/// <item>Bakes <see cref="KnockableMeshes"/> onto every <see cref="Knockable"/> in Neighborhood.unity (the asset mesh of each
/// renderer, which static batching hides at runtime) and saves the scene only if something changed. NeighborhoodBuilder
/// may call <see cref="BakeScene"/> itself right before it saves.</item>
/// <item>A validation report: knockables per kind, how each will fly (prefab / baked / recipe / box), duplicate ids, rest
/// colliders that are still non-convex MeshColliders, knockables with no rest collider.</item>
/// </list>
/// Never saves Bootstrap. Never touches a scene other than Neighborhood.unity (or the scene passed to BakeScene).
/// </summary>
public static class Setup_WorldLifeKnock
{
    public const string PrefabDir = "Assets/Prefabs/WorldLife";
    public const string MatDir = "Assets/Materials/Generated/WorldLife/Knock";
    const string Pack = "Assets/Suburbs Town Pack/PREFABS/";

    /// <summary>The pack prefabs NeighborhoodBuilder places that are knockable (§6.1), and their kind.</summary>
    public static readonly (string path, KnockKind kind)[] PrefabProps =
    {
        (Pack + "Props/SM_streetlamp_01", KnockKind.Lamp),
        (Pack + "Props/SM_streetlamp_02", KnockKind.Lamp),
        (Pack + "Props/SM_streetlamp_03", KnockKind.Lamp),
        (Pack + "Props/SM_mailbox_01", KnockKind.Mailbox),
        (Pack + "Props/SM_mailbox_02", KnockKind.Mailbox),
        (Pack + "Props/SM_garbagecan_01", KnockKind.WheelieBin),
        (Pack + "Props/SM_garbagecan_02", KnockKind.WheelieBin),
        (Pack + "Props/SM_garbagecan_03", KnockKind.WheelieBin),
        (Pack + "Props/SM_lemonade_shop", KnockKind.LemonadeStand),
        (Pack + "Props/SM_clothes_drying_rack_01", KnockKind.DryingRack),
        (Pack + "Props/SM_clothes_drying_rack_02", KnockKind.DryingRack),
        (Pack + "Props/SM_clothes_drying_rack_03", KnockKind.DryingRack),
        (Pack + "Props/SM_clothes_drying_rack_04", KnockKind.DryingRack),
        (Pack + "Playground/SM_bench_playground", KnockKind.ParkBench),
        (Pack + "Playground/SM_trashbin_park", KnockKind.ParkBin),
    };
    /// <summary>Debris prefabs (not knockables themselves).</summary>
    public static readonly (string path, float mass)[] DebrisProps = { (Pack + "Props/SM_lemon", 0.15f) };

    [MenuItem("Rubbish/Setup/WorldLife Knockables")]
    public static void RunMenu() => Run();

    /// <summary>The umbrella (Setup_WorldLife) calls this. Returns the report (also logged).</summary>
    public static string Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Setup_WorldLifeKnock] Exit play mode first.");
            return "ERROR: play mode";
        }
        var sb = new StringBuilder("[Setup_WorldLifeKnock]\n");
        sb.Append(EnsureMaterials());
        sb.Append(BuildProxyPrefabs());
        AssetDatabase.SaveAssets();
        sb.Append(BakeNeighborhood());
        string report = sb.ToString();
        if (report.Contains("FAIL")) Debug.LogWarning(report); else Debug.Log(report);
        return report;
    }

    // ================================================================================================= materials

    public static string EnsureMaterials()
    {
        EnsureFolder(MatDir);
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) return "  materials: FAIL no URP/Lit shader\n";
        int made = 0;
        foreach (var name in KnockRecipes.MaterialNames)
        {
            var m = GetOrCreate(name, lit, ref made);
            m.SetColor("_BaseColor", KnockRecipes.ColorOf(name));
            m.SetFloat("_Smoothness", name == KnockRecipes.MatMetal ? 0.55f : 0.25f);
            m.SetFloat("_Metallic", name == KnockRecipes.MatMetal ? 0.6f : 0f);
            EditorUtility.SetDirty(m);
            RubbishAssets.EditorRegisterMaterial(KnockRecipes.MaterialKey(name), m);
        }
        var bulb = GetOrCreate("bulb_off", lit, ref made);
        bulb.SetColor("_BaseColor", new Color(0.82f, 0.8f, 0.7f));
        bulb.EnableKeyword("_EMISSION");
        bulb.SetColor("_EmissionColor", new Color(0.02f, 0.02f, 0.015f));
        bulb.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        EditorUtility.SetDirty(bulb);
        RubbishAssets.EditorRegisterMaterial(KnockRecipes.MaterialKey("bulb_off"), bulb);
        var water = GetOrCreate("water", lit, ref made);
        HydrantGeysers.MakeTransparent(water, new Color(0.45f, 0.72f, 1f, 0.78f));
        EditorUtility.SetDirty(water);
        RubbishAssets.EditorRegisterMaterial(KnockRecipes.MaterialKey("water"), water);
        var spray = GetOrCreate("spray", lit, ref made);
        HydrantGeysers.MakeTransparent(spray, new Color(0.8f, 0.92f, 1f, 0.42f));
        EditorUtility.SetDirty(spray);
        RubbishAssets.EditorRegisterMaterial(KnockRecipes.MaterialKey("spray"), spray);
        return $"  materials: {KnockRecipes.MaterialNames.Length + 3} registered as mat/worldlife_knock_* ({made} new)\n";
    }

    static Material GetOrCreate(string name, Shader sh, ref int made)
    {
        string path = $"{MatDir}/worldlife_knock_{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        m = new Material(sh) { name = "worldlife_knock_" + name };
        AssetDatabase.CreateAsset(m, path);
        made++;
        return m;
    }

    // ================================================================================================= proxy prefabs

    public static string BuildProxyPrefabs()
    {
        EnsureFolder(PrefabDir);
        var sb = new StringBuilder();
        int ok = 0;
        var bulbOff = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/worldlife_knock_bulb_off.mat");
        foreach (var (path, kind) in PrefabProps)
        {
            if (BuildProxy(path, WorldLifeTuning.Spec(kind).massKg, kind == KnockKind.Lamp ? bulbOff : null, out string msg)) ok++;
            else sb.AppendLine("    FAIL " + msg);
        }
        foreach (var (path, mass) in DebrisProps)
        {
            if (BuildProxy(path, mass, null, out string msg)) ok++;
            else sb.AppendLine("    FAIL " + msg);
        }
        return $"  proxy prefabs: {ok}/{PrefabProps.Length + DebrisProps.Length} in {PrefabDir} (knock/* keys)\n" + sb;
    }

    /// <summary>Knock_&lt;name&gt;.prefab from a pack prefab: its meshes + materials (no lights, no mesh colliders), one
    /// BoxCollider around them, a Rigidbody. Registered as knock/&lt;name&gt;.</summary>
    static bool BuildProxy(string srcPath, float mass, Material bulbOff, out string msg)
    {
        msg = "";
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(srcPath + ".prefab");
        if (src == null) { msg = "missing " + srcPath; return false; }
        string name = src.name;
        var root = new GameObject("Knock_" + name);
        try
        {
            root.layer = KnockProxyPool.ProxyLayer;
            var inv = src.transform.worldToLocalMatrix;
            Bounds b = default;
            bool has = false;
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;
                var part = new GameObject(mf.name);
                part.layer = KnockProxyPool.ProxyLayer;
                part.transform.SetParent(root.transform, false);
                Matrix4x4 m = inv * mf.transform.localToWorldMatrix;
                part.transform.localPosition = m.GetColumn(3);
                part.transform.localRotation = m.rotation;
                part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var nr = part.AddComponent<MeshRenderer>();
                var mats = mr.sharedMaterials;
                if (bulbOff != null)
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && mats[i].name.StartsWith("M_emissive")) mats[i] = bulbOff;
                nr.sharedMaterials = mats;
                nr.shadowCastingMode = mr.shadowCastingMode;
                var mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? mb.min.x : mb.max.x, (c & 2) == 0 ? mb.min.y : mb.max.y, (c & 4) == 0 ? mb.min.z : mb.max.z);
                    Vector3 w = m.MultiplyPoint3x4(corner);
                    if (!has) { b = new Bounds(w, Vector3.zero); has = true; } else b.Encapsulate(w);
                }
            }
            if (!has) { msg = "no meshes in " + srcPath; return false; }
            var box = root.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = Vector3.Max(b.size, Vector3.one * 0.05f);
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.3f;
            string path = $"{PrefabDir}/Knock_{name}.prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            if (!success || saved == null) { msg = "could not save " + path; return false; }
            RubbishAssets.EditorRegisterPrefab(WorldLifeIds.KnockPrefabPrefix + name, saved);
            return true;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ================================================================================================= bake

    /// <summary>Open Neighborhood.unity if needed, bake, save only if something changed, close it again if we opened it.</summary>
    public static string BakeNeighborhood()
    {
        string path = NeighborhoodBuilder.DefaultScenePath;
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            opened = true;
        }
        try
        {
            string report = BakeScene(scene, out int changed);
            if (changed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report += $"  saved {scene.path} ({changed} knockables re-baked)\n";
            }
            return report;
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>
    /// Bake <see cref="KnockableMeshes"/> onto every knockable in the scene (no save). Returns the validation report;
    /// <paramref name="changed"/> = knockables whose baked data changed. NeighborhoodBuilder can call this before it saves.
    /// </summary>
    public static string BakeScene(Scene scene, out int changed)
    {
        changed = 0;
        var sb = new StringBuilder();
        var all = new List<Knockable>();
        foreach (var r in scene.GetRootGameObjects()) all.AddRange(r.GetComponentsInChildren<Knockable>(true));
        var byKind = new int[Enum.GetValues(typeof(KnockKind)).Length];
        var sources = new Dictionary<string, int>();
        var ids = new Dictionary<ushort, Knockable>();
        int dup = 0, meshCols = 0, noCols = 0, triggers = 0;
        var missingKeys = new HashSet<string>();
        var rends = new List<Renderer>();
        foreach (var k in all)
        {
            byKind[Mathf.Clamp((int)k.kind, 0, byKind.Length - 1)]++;
            if (ids.TryGetValue(k.id, out var other) && other != k) { dup++; if (dup <= 5) sb.AppendLine($"    FAIL duplicate id {k.id}: '{k.name}' and '{other.name}'"); }
            ids[k.id] = k;

            // rest colliders (A replaces mesh colliders with primitives)
            var cols = (k.restColliders != null && k.restColliders.Length > 0) ? k.restColliders : k.GetComponentsInChildren<Collider>(true);
            if (cols.Length == 0) noCols++;
            foreach (var c in cols)
            {
                if (c is MeshCollider mc && !mc.convex) meshCols++;
                if (c != null && c.isTrigger) triggers++;
            }

            // bake
            rends.Clear();
            if (k.renderers != null) foreach (var r in k.renderers) if (r != null && !rends.Contains(r)) rends.Add(r);
            foreach (var r in k.GetComponentsInChildren<MeshRenderer>(true)) if (!rends.Contains(r)) rends.Add(r);
            var keepR = new List<Renderer>();
            var keepM = new List<Mesh>();
            foreach (var r in rends)
            {
                if (!(r is MeshRenderer) || r.GetComponent<TMP_Text>() != null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                keepR.Add(r);
                keepM.Add(mf.sharedMesh);
            }
            var km = k.GetComponent<KnockableMeshes>();
            bool same = km != null && km.renderers != null && km.meshes != null && km.renderers.SequenceEqual(keepR) && km.meshes.SequenceEqual(keepM);
            if (!same)
            {
                if (km == null) km = k.gameObject.AddComponent<KnockableMeshes>();
                km.renderers = keepR.ToArray();
                km.meshes = keepM.ToArray();
                EditorUtility.SetDirty(km);
                changed++;
            }

            // how it will fly
            string src;
            if (!string.IsNullOrEmpty(k.proxyKey) && k.proxyKey.StartsWith(WorldLifeIds.KnockPrefabPrefix))
            {
                if (RubbishAssets.Prefab(k.proxyKey) != null) src = "prefab";
                else { src = keepM.Count > 0 ? "baked (prefab key missing)" : "box (prefab key missing)"; missingKeys.Add(k.proxyKey); }
            }
            else if (keepM.Count > 0) src = "baked";
            else if (KnockRecipes.TryGet(k.proxyKey, out _)) src = "recipe";
            else src = "box";
            sources[src] = sources.TryGetValue(src, out int n) ? n + 1 : 1;
        }

        sb.Insert(0, $"  {scene.path}: {all.Count} knockables, {ids.Count} unique ids, {changed} re-baked\n" +
                     "    by kind: " + string.Join(", ", Enumerable.Range(0, byKind.Length).Where(i => byKind[i] > 0).Select(i => $"{(KnockKind)i} {byKind[i]}")) + "\n" +
                     "    proxies: " + string.Join(", ", sources.Select(kv => $"{kv.Key} {kv.Value}")) + "\n");
        if (all.Count == 0) sb.AppendLine("    (none yet: NeighborhoodBuilder (package A) adds Knockable components)");
        if (meshCols > 0) sb.AppendLine($"    note: {meshCols} rest colliders are non-convex MeshColliders (A should swap them for primitives; they still work)");
        if (triggers > 0) sb.AppendLine($"    note: {triggers} rest colliders are triggers (the sensor ignores triggers)");
        if (noCols > 0) sb.AppendLine($"    FAIL {noCols} knockables have no rest collider (the sensor can't see them)");
        foreach (var key in missingKeys) sb.AppendLine($"    FAIL proxy prefab missing for '{key}' (add it to Setup_WorldLifeKnock.PrefabProps)");
        return sb.ToString();
    }

    // ================================================================================================= self-test (edit mode)

    [MenuItem("Rubbish/WorldLife/Knock Self Test")]
    public static void SelfTestMenu()
    {
        string r = SelfTest();
        if (r.Contains("FAIL")) Debug.LogWarning(r); else Debug.Log(r);
    }

    /// <summary>Pure checks (no scene saved): recipes, shapes, formulas, the speed-loss cap, City Works timing, prefabs.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder("[WorldLife B self-test]\n");
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); }

        // recipes → at-rest props with a Knockable; the proxy shape rebuilds the same primitives
        foreach (var name in KnockRecipes.Names)
        {
            var go = KnockRecipes.Build(name, null, new Vector3(5f, 0f, 7f), Quaternion.Euler(0f, 30f, 0f), 900);
            var k = go.GetComponent<Knockable>();
            KnockRecipes.TryGet(name, out var rec);
            var shape = KnockShape.Resolve(k);
            bool sizeOk = shape.boxSize.y > 0.3f && shape.boxSize.y < 1.6f && Vector3.Distance(shape.boxSize, rec.colliderSize) < 0.6f;
            Check(k != null && k.kind == rec.kind && k.proxyKey == "recipe/" + name && k.RestColliders.Length == 1 && shape.PartCount == rec.parts.Length && sizeOk,
                  $"recipe '{name}': {rec.parts.Length} parts, kind {rec.kind}, shape '{shape.source}' {shape.PartCount} parts, box {shape.boxSize:F2} (rest {rec.colliderSize:F2})");
            UnityEngine.Object.DestroyImmediate(go);
        }

        // launch formula: v·1.1 + up·(1.5 + 0.1|v|) + a side push away from the truck's centre line
        var v = KnockableDirector.LaunchVelocity(new Vector3(2f, 0f, 5f), new Vector3(0f, 0f, 8f), Vector3.zero, Quaternion.identity, 0x123456789ABCDEFUL);
        Check(Mathf.Abs(v.y - 2.3f) < 0.001f && Mathf.Abs(v.z - 8.8f) < 0.001f && v.x > 0.6f * (KnockTuning.SidePush + KnockTuning.SidePushPerSpeed * 8f) - 0.01f,
              $"launch at 8 m/s = {v:F2} (y 2.30, z 8.80, x pushed right, away from the truck)");
        var vl = KnockableDirector.LaunchVelocity(new Vector3(-2f, 0f, 5f), new Vector3(0f, 0f, 8f), Vector3.zero, Quaternion.identity, 1);
        Check(vl.x < 0f, "a prop left of the truck flies left");

        // speed-loss cap: 5 lamps (10 %) 0.25 s apart from 8 m/s; props never take more than 35 % in any one second
        var times = new List<double>(); var losses = new List<float>();
        float speed = 8f, lostTotal = 0f, worstWindow = 0f;
        for (int i = 0; i < 5; i++)
        {
            double t = i * 0.25;
            float window = 0f;
            for (int j = 0; j < times.Count; j++) if (t - times[j] < 1.0) window += losses[j];
            float a = KnockableDirector.CappedLoss(WorldLifeTuning.Spec(KnockKind.Lamp).speedLoss, window);
            times.Add(t); losses.Add(a);
            speed *= 1f - a; lostTotal += a;
            worstWindow = Mathf.Max(worstWindow, window + a);
        }
        Check(speed >= 4.3f && worstWindow <= 0.35f + 1e-4f, $"5 lamps 0.25 s apart from 8 m/s (no drivetrain): {speed:F2} m/s after the props (≥ 4.3); Σ loss {lostTotal:F2}, worst 1 s window {worstWindow:F2} (cap 0.35)");
        Check(Mathf.Approximately(KnockableDirector.CappedLoss(0.1f, 0.3f), 0.05f) && KnockableDirector.CappedLoss(0.04f, 0.35f) == 0f, "CappedLoss clips to the 1 s window");

        // City Works at 06:00
        Check(Math.Abs(KnockableDirector.NextCityWorksAfter(5.9) - 6.0) < 1e-9 && Math.Abs(KnockableDirector.NextCityWorksAfter(6.0) - 30.0) < 1e-9 &&
              Math.Abs(KnockableDirector.NextCityWorksAfter(23.0) - 30.0) < 1e-9 && Math.Abs(KnockableDirector.NextCityWorksAfter(30.5) - 54.0) < 1e-9,
              "City Works: next 06:00 after 5.9 h = 6, 6 = 30, 23 = 30, 30.5 = 54");

        // prefabs: registered, Rigidbody + primitive colliders only, the kind's mass, an unlit bulb on lamps
        int good = 0;
        var problems = new List<string>();
        foreach (var (path, kind) in PrefabProps)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            var p = RubbishAssets.Prefab(WorldLifeIds.KnockPrefabPrefix + name);
            if (p == null) { problems.Add(name + " not registered"); continue; }
            var rb = p.GetComponent<Rigidbody>();
            var cols = p.GetComponentsInChildren<Collider>(true);
            bool lampOk = kind != KnockKind.Lamp || p.GetComponentsInChildren<MeshRenderer>(true).All(r => r.sharedMaterials.All(m => m == null || !m.name.StartsWith("M_emissive")));
            bool noLights = p.GetComponentsInChildren<Light>(true).Length == 0;
            if (rb != null && Mathf.Approximately(rb.mass, WorldLifeTuning.Spec(kind).massKg) && cols.Length > 0 && cols.All(c => !(c is MeshCollider)) && lampOk && noLights) good++;
            else problems.Add(name);
        }
        Check(good == PrefabProps.Length, $"proxy prefabs: {good}/{PrefabProps.Length} registered with a Rigidbody (§6.1 mass), primitive colliders, no lights, unlit bulbs" +
                                          (problems.Count > 0 ? " — " + string.Join(", ", problems) : ""));
        Check(RubbishAssets.Prefab("knock/SM_lemon") != null, "debris prefab knock/SM_lemon registered");
        Check(KnockRecipes.MaterialOf(KnockRecipes.MatRed) != null && RubbishAssets.Material(KnockRecipes.MaterialKey("water")) != null, "materials mat/worldlife_knock_* registered");

        sb.AppendLine(fails == 0 ? "ALL PASS" : $"{fails} FAIL");
        return sb.ToString();
    }

    // ================================================================================================= play-mode test kit

    /// <summary>
    /// Play-mode probe for the §12.3 B acceptance checks (editor only; install from an eval): drives a cruise speed until a
    /// point, records the first fixed step the truck's real body box overlaps each watched prop vs the step it went down,
    /// truck speed per step (NaN check), StreetLamps.ActiveLights per frame, and every knock / reset with times.
    /// </summary>
    public sealed class Probe : WorldLifeDirector
    {
        public static Probe Installed;
        public override WorldLifeSlot Slot => WorldLifeSlot.Rules;
        public TruckControllerV2 truck;
        public Vector3 cruiseDir, cruiseStop;
        public float cruiseSpeed;
        public bool cruising;
        public readonly List<Knockable> watch = new List<Knockable>();
        public readonly List<float> watchRadius = new List<float>();
        public readonly Dictionary<ushort, int> firstOverlap = new Dictionary<ushort, int>();
        public readonly Dictionary<ushort, int> downTick = new Dictionary<ushort, int>();
        public readonly Dictionary<ushort, double> downAt = new Dictionary<ushort, double>();
        public readonly List<string> resets = new List<string>();
        public readonly List<double> resetDelays = new List<double>();
        public readonly List<Vector2> speeds = new List<Vector2>();      // (tick, speed)
        public readonly List<Vector2> lights = new List<Vector2>();      // (time, ActiveLights)
        public int nan, lateTicks, fixedTicks;
        public bool trackLights;
        /// <summary>"Holding W": push the truck toward this forward speed with at most this acceleration (0 = off).</summary>
        public float throttleTarget, throttleAccel;

        public static Probe Install()
        {
            Uninstall();
            Installed = new Probe();
            WorldLifeRunner.Register(Installed);
            WorldLifeEvents.PropKnocked += Installed.OnKnock;
            WorldLifeEvents.PropReset += Installed.OnReset;
            return Installed;
        }

        public static void Uninstall()
        {
            if (Installed == null) return;
            WorldLifeRunner.Unregister(Installed);
            WorldLifeEvents.PropKnocked -= Installed.OnKnock;
            WorldLifeEvents.PropReset -= Installed.OnReset;
            Installed = null;
        }

        void OnKnock(KnockFact f) { downTick[f.knockId] = WorldLifeRunner.FixedTickCount; downAt[f.knockId] = Time.timeAsDouble; }

        void OnReset(ushort id, bool cw)
        {
            double d = downAt.TryGetValue(id, out var t) ? Time.timeAsDouble - t : -1;
            resets.Add($"{id}:{d:F2}s{(cw ? ":cw" : "")}");
            if (!cw) resetDelays.Add(d);
        }

        public override void FixedTick(WorldLifeContext ctx)
        {
            fixedTicks++;
            if (truck == null) return;
            var rb = truck.Body;
            var box = TruckControllerV2.BodyBoxMetres;
            if (cruising)
            {
                Vector3 nose = rb.position + rb.rotation * new Vector3(0f, 0f, box.max.z);
                if (Vector3.Dot(cruiseStop - nose, cruiseDir) > 0f) { var v = rb.linearVelocity; rb.linearVelocity = new Vector3(cruiseDir.x * cruiseSpeed, v.y, cruiseDir.z * cruiseSpeed); }
                else cruising = false;
            }
            if (throttleAccel > 0f)
            {
                Vector3 fwd = rb.rotation * Vector3.forward;
                float vf = Vector3.Dot(rb.linearVelocity, fwd);
                if (vf < throttleTarget) rb.AddForce(fwd * Mathf.Min(throttleAccel, (throttleTarget - vf) / Time.fixedDeltaTime), ForceMode.Acceleration);
            }
            float s = rb.linearVelocity.magnitude;
            if (float.IsNaN(s) || float.IsNaN(rb.position.x)) nan++;
            if (speeds.Count < 20000) speeds.Add(new Vector2(WorldLifeRunner.FixedTickCount, s));
            var inv = Quaternion.Inverse(rb.rotation);
            for (int i = 0; i < watch.Count; i++)
            {
                var k = watch[i];
                if (k == null || firstOverlap.ContainsKey(k.id)) continue;
                Vector3 l = inv * (k.transform.position - rb.position);
                float r = watchRadius[i];
                if (Mathf.Abs(l.x) <= box.extents.x + r && l.z >= box.min.z - r && l.z <= box.max.z + r) firstOverlap[k.id] = WorldLifeRunner.FixedTickCount;
            }
        }

        public override void LateTick(WorldLifeContext ctx)
        {
            lateTicks++;
            if (!trackLights || lights.Count >= 20000) return;
            var sl = StreetLamps.Current;
            if (sl != null) lights.Add(new Vector2(Time.time, sl.ActiveLights));
        }

        public void Cruise(Vector3 dir, float speed, Vector3 stopAt) { cruiseDir = dir.normalized; cruiseSpeed = speed; cruiseStop = stopAt; cruising = true; }
    }

    /// <summary>The scene the truck is in (test props go there, never into Bootstrap).</summary>
    static Scene TruckScene() => TruckControllerV2.All.Count > 0 ? TruckControllerV2.All[0].gameObject.scene : SceneManager.GetActiveScene();

    public static Transform TestRoot()
    {
        var s = TruckScene();
        foreach (var r in s.GetRootGameObjects()) if (r.name == "WL_B_TestProps") return r.transform;
        var go = new GameObject("WL_B_TestProps");
        SceneManager.MoveGameObjectToScene(go, s);
        return go.transform;
    }

    public static float GroundY(Vector3 p)
    {
        int mask = Physics.AllLayers & ~(1 << 2) & ~(1 << Carryable.InteractableLayer);
        return Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 60f, mask, QueryTriggerInteraction.Ignore) ? hit.point.y : p.y;
    }

    /// <summary>Make a test knockable from an inactive GameObject (fields set before OnEnable registers it).</summary>
    public static Knockable MakeKnockable(GameObject go, ushort id, KnockKind kind, string key, Collider[] rest)
    {
        go.SetActive(false);
        var k = go.GetComponent<Knockable>();
        if (k == null) k = go.AddComponent<Knockable>();
        k.id = id; k.kind = kind; k.proxyKey = key; k.restColliders = rest;
        go.SetActive(true);
        return k;
    }

    /// <summary>Pack lamps in a row like NeighborhoodBuilder places them, with the primitive rest collider package A
    /// gives them (a post capsule), optionally static-batched at runtime (so the proxy path is the real one).</summary>
    public static List<Knockable> SpawnLampRow(Vector3 start, Vector3 dir, int n, float spacing, ushort firstId, bool staticBatch, string prefab = "SM_streetlamp_01")
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>($"{Pack}Props/{prefab}.prefab");
        var root = new GameObject("LampRow_" + firstId).transform;
        root.SetParent(TestRoot(), false);
        var list = new List<Knockable>();
        dir.y = 0f; dir.Normalize();
        for (int i = 0; i < n; i++)
        {
            Vector3 p = start + dir * (spacing * i);
            p.y = GroundY(p);
            var go = UnityEngine.Object.Instantiate(src, p, Quaternion.LookRotation(Vector3.Cross(Vector3.up, dir)), root);
            go.name = prefab;
            foreach (var l in go.GetComponentsInChildren<Light>(true)) l.enabled = false;
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true)) UnityEngine.Object.Destroy(mc);
            var cap = go.AddComponent<CapsuleCollider>();
            cap.radius = 0.16f; cap.height = 5.4f; cap.center = new Vector3(0f, 2.7f, 0f);
            list.Add(MakeKnockable(go, (ushort)(firstId + i), KnockKind.Lamp, WorldLifeIds.KnockPrefabPrefix + prefab, new Collider[] { cap }));
        }
        if (staticBatch) CombineStatic(root.gameObject);
        StreetLamps.Current?.Rescan();
        return list;
    }

    /// <summary>Static-batch a test root at runtime like the builder's BatchingStatic flags do (TMP text stays dynamic).</summary>
    public static void CombineStatic(GameObject root)
    {
        var gos = new List<GameObject>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            if (mf.GetComponent<TMP_Text>() == null && mf.GetComponent<MeshRenderer>() != null) gos.Add(mf.gameObject);
        StaticBatchingUtility.Combine(gos.ToArray(), root);
    }

    /// <summary>A builder-style street sign (post, cap, two TMP boards), meshes baked like the setup does, then
    /// static-batched: the proxy must come from the bake, with the street names riding on it.</summary>
    public static Knockable SpawnSign(Vector3 p, float yaw, ushort id, bool bake, string a = "Maple St", string b = "Elm Ave")
    {
        var root = new GameObject("StreetSign_test_" + id);
        root.SetActive(false);
        root.transform.SetParent(TestRoot(), false);
        p.y = GroundY(p);
        root.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f));
        var metal = KnockRecipes.MaterialOf(KnockRecipes.MatMetal);
        var green = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Generated/Neighborhood/NB_SignGreen.mat");
        if (green == null) green = KnockRecipes.MaterialOf(KnockRecipes.MatWood);
        GameObject Box(string name, Vector3 lp, Vector3 size, Material m, Transform parent, float ry = 0f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = lp; go.transform.localRotation = Quaternion.Euler(0f, ry, 0f); go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            return go;
        }
        var post = Box("Post", new Vector3(0f, 1.31f, 0f), new Vector3(0.09f, 2.62f, 0.09f), metal, root.transform);
        var capGo = Box("Cap", new Vector3(0f, 2.64f, 0f), new Vector3(0.16f, 0.05f, 0.16f), metal, root.transform);
        UnityEngine.Object.Destroy(capGo.GetComponent<Collider>());
        for (int i = 0; i < 2; i++)
        {
            var pivot = new GameObject("Board_" + i).transform;
            pivot.SetParent(root.transform, false);
            pivot.localPosition = new Vector3(0f, i == 0 ? 2.85f : 3.22f, 0f);
            pivot.localRotation = Quaternion.Euler(0f, i == 0 ? 0f : 90f, 0f);
            var board = Box("Board", Vector3.zero, new Vector3(1.9f, 0.36f, 0.05f), green, pivot);
            UnityEngine.Object.Destroy(board.GetComponent<Collider>());
            var t = new GameObject("Text").AddComponent<TextMeshPro>();
            t.transform.SetParent(pivot, false);
            t.transform.localPosition = new Vector3(0f, 0f, -0.04f);
            t.text = i == 0 ? a : b; t.color = Color.white; t.fontStyle = FontStyles.Bold; t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = true; t.fontSizeMin = 0.5f; t.fontSizeMax = 72f; t.rectTransform.sizeDelta = new Vector2(1.75f, 0.31f);
        }
        if (bake)
        {
            var km = root.AddComponent<KnockableMeshes>();
            var rs = new List<Renderer>(); var ms = new List<Mesh>();
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.GetComponent<TMP_Text>() == null) { rs.Add(mr); ms.Add(mr.GetComponent<MeshFilter>().sharedMesh); }
            km.renderers = rs.ToArray(); km.meshes = ms.ToArray();
        }
        var k = MakeKnockable(root, id, KnockKind.Sign, "recipe/street_sign", new Collider[] { post.GetComponent<Collider>() });
        CombineStatic(root);
        return k;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}

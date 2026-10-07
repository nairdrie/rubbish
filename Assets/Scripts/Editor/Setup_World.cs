using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Sky system setup (day/night). Idempotent; run AFTER NeighborhoodBuilder (it never touches "NeighborhoodGen";
/// lamps and houses are found by name at runtime, so rebuilding the neighborhood later needs no re-run).
///  - Creates Assets/Materials/Generated/Sky/SkyBlend.mat (Rubbish/SkyBlend6Sided) and registers it plus the
///    Day-Night skybox keyframes in RubbishAssets ("mat/sky_blend", "mat/sky_&lt;SkyName&gt;", "mat/sky_window_glow",
///    "mat/sky_lamp_bulb").
///  - Neighborhood: a "WorldSky" root with DayNightSky + StreetLamps + HouseWindows, wired to the "Sun" light;
///    RenderSettings.skybox = SkyBlend.mat, trilight ambient + fog at a 9 AM look (runtime drives them anyway).
///  - ShopScene (garage): a "WorldSky" root with GarageSkyLight (door-gap glow + floor spill + ambient tint).
/// </summary>
public static class Setup_World
{
    public const string NeighborhoodScenePath = "Assets/Scenes/Neighborhood.unity";
    public const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    const string SkyMatDir = "Assets/Day-Night Skyboxes/Materials/";
    const string MatDir = "Assets/Materials/Generated/Sky";
    const string BlendMatPath = MatDir + "/SkyBlend.mat";
    const string ShaderPath = "Assets/Scripts/World/Shaders/RubbishSkyBlend.shader";
    const string ShaderName = "Rubbish/SkyBlend6Sided";
    const string WindowGlowPath = "Assets/Custom_Mats/Windows_glow.mat";
    const string LampBulbPath = "Assets/Suburbs Town Pack/MATERIALS/M_emissive.mat";
    public const string RootName = "WorldSky";

    [MenuItem("Rubbish/Setup/World")]
    public static void Run() => Build(NeighborhoodScenePath, ShopScenePath, true);

    /// <summary>
    /// Set up the given scenes (either path may be null). createAssets=false skips creating/registering assets
    /// (used for scratch-copy verification: the runtime then builds its blend material from the shader).
    /// </summary>
    public static string Build(string neighborhoodScenePath, string shopScenePath, bool createAssets = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Setup_World] Sky / day-night");

        var skies = LoadSkies(sb);
        Material blend = createAssets ? EnsureBlendMaterial(skies, sb) : AssetDatabase.LoadAssetAtPath<Material>(BlendMatPath);
        if (createAssets) RegisterAssets(blend, skies, sb);

        if (!string.IsNullOrEmpty(neighborhoodScenePath)) SetupNeighborhood(neighborhoodScenePath, blend, skies, createAssets, sb);
        if (!string.IsNullOrEmpty(shopScenePath)) SetupGarage(shopScenePath, sb);

        if (createAssets) AssetDatabase.SaveAssets();
        Debug.Log(sb.ToString());
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Assets
    // ------------------------------------------------------------------------------------------

    static Material[] LoadSkies(StringBuilder sb)
    {
        var list = new List<Material>();
        foreach (var n in SkyCycle.SkyNames)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(SkyMatDir + n + ".mat");
            if (m == null) sb.AppendLine("  WARN missing skybox " + SkyMatDir + n + ".mat");
            else list.Add(m);
        }
        sb.AppendLine($"  skyboxes: {list.Count}/{SkyCycle.SkyNames.Length}");
        return list.ToArray();
    }

    static Material EnsureBlendMaterial(Material[] skies, StringBuilder sb)
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null) shader = Shader.Find(ShaderName);
        if (shader == null) { sb.AppendLine("  FAIL shader " + ShaderName + " not found"); return null; }
        EnsureFolder(MatDir);
        var m = AssetDatabase.LoadAssetAtPath<Material>(BlendMatPath);
        bool created = m == null;
        if (created)
        {
            m = new Material(shader) { name = "SkyBlend" };
            AssetDatabase.CreateAsset(m, BlendMatPath);
        }
        else if (m.shader != shader) m.shader = shader;

        // Edit-mode look = a bright morning (both slots), exactly what the old static skybox was.
        Material morning = null;
        foreach (var s in skies) if (s != null && s.name == "SkyBrightMorning") morning = s;
        if (morning != null)
            foreach (var f in new[] { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" })
            {
                m.SetTexture(f, morning.GetTexture(f));
                m.SetTexture(f + "B", morning.GetTexture(f));
            }
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_Exposure", 1f);
        m.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        m.SetFloat("_StarAmount", 0f);
        m.SetFloat("_MoonAmount", 0f);
        EditorUtility.SetDirty(m);
        sb.AppendLine($"  {BlendMatPath} {(created ? "created" : "updated")}");
        return m;
    }

    static void RegisterAssets(Material blend, Material[] skies, StringBuilder sb)
    {
        if (blend != null) RubbishAssets.EditorRegisterMaterial("mat/sky_blend", blend);
        foreach (var s in skies) RubbishAssets.EditorRegisterMaterial("mat/sky_" + s.name, s);
        var glow = AssetDatabase.LoadAssetAtPath<Material>(WindowGlowPath);
        if (glow != null) RubbishAssets.EditorRegisterMaterial("mat/sky_window_glow", glow);
        var bulb = AssetDatabase.LoadAssetAtPath<Material>(LampBulbPath);
        if (bulb != null) RubbishAssets.EditorRegisterMaterial("mat/sky_lamp_bulb", bulb);
        sb.AppendLine($"  RubbishAssets: mat/sky_blend + {skies.Length} skies + window glow + lamp bulb");
    }

    // ------------------------------------------------------------------------------------------
    // Scenes
    // ------------------------------------------------------------------------------------------

    static void SetupNeighborhood(string path, Material blend, Material[] skies, bool setSkybox, StringBuilder sb)
    {
        WithScene(path, sb, scene =>
        {
            var root = FindOrCreateRoot(scene);
            var sky = GetOrAdd<DayNightSky>(root);
            GetOrAdd<StreetLamps>(root);
            GetOrAdd<HouseWindows>(root);
            sky.blendMaterial = blend;
            sky.skyMaterials = skies;

            Light sun = null;
            foreach (var r in scene.GetRootGameObjects())
                if (r.name == "Sun") { sun = r.GetComponent<Light>(); break; }
            if (sun == null)
                foreach (var r in scene.GetRootGameObjects())
                {
                    if (!r.activeInHierarchy) continue;
                    foreach (var l in r.GetComponentsInChildren<Light>(false))
                        if (l.type == LightType.Directional) { sun = l; break; }
                    if (sun != null) break;
                }
            sky.sun = sun;
            EditorUtility.SetDirty(sky);

            // Render settings (active scene = this one inside WithScene): a 9 AM look for the edit-mode view.
            var look = SkyCycle.LookAt(9f);
            if (setSkybox && blend != null) RenderSettings.skybox = blend;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = look.ambientSky;
            RenderSettings.ambientEquatorColor = look.ambientEquator;
            RenderSettings.ambientGroundColor = look.ambientGround;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = look.fog;
            RenderSettings.fogStartDistance = look.fogStart;
            RenderSettings.fogEndDistance = look.fogEnd;
            if (sun != null) RenderSettings.sun = sun;

            int lamps = 0, houses = 0;
            foreach (var r in scene.GetRootGameObjects())
            {
                if (!r.activeInHierarchy) continue;
                foreach (var mr in r.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (mr.name.StartsWith("SM_streetlamp")) lamps++;
                    foreach (var m in mr.sharedMaterials) if (m != null && m.name.StartsWith("M_windows")) { houses++; break; }
                }
            }
            sb.AppendLine($"  {path}: {RootName} (DayNightSky + StreetLamps + HouseWindows), sun={(sun != null ? sun.name : "MISSING")}, " +
                          $"lamps found {lamps}, houses with windows {houses}");
            if (sun == null) sb.AppendLine("  WARN no directional light — the sky will still run, but without sun/moon light.");
        });
    }

    static void SetupGarage(string path, StringBuilder sb)
    {
        WithScene(path, sb, scene =>
        {
            var root = FindOrCreateRoot(scene);
            var g = GetOrAdd<GarageSkyLight>(root);
            // Record the door placement as a fallback (runtime re-reads the door bounds anyway).
            foreach (var r in scene.GetRootGameObjects())
            {
                if (r.name != g.doorName) continue;
                var rends = r.GetComponentsInChildren<Renderer>(false);
                if (rends.Length == 0) break;
                Bounds b = rends[0].bounds;
                foreach (var x in rends) b.Encapsulate(x.bounds);
                bool thinX = b.size.x < b.size.z;
                g.doorWidth = thinX ? b.size.z : b.size.x;
                Vector3 toInside = -new Vector3(b.center.x, 0f, b.center.z);
                g.inward = thinX ? new Vector3(Mathf.Sign(toInside.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(toInside.z));
                g.doorBottomCenter = new Vector3(b.center.x, b.min.y, b.center.z) + g.inward * (thinX ? b.extents.x : b.extents.z);
                break;
            }
            EditorUtility.SetDirty(g);
            sb.AppendLine($"  {path}: {RootName} (GarageSkyLight) door at {g.doorBottomCenter} width {g.doorWidth:F2} inward {g.inward}");
        });
    }

    static void WithScene(string path, StringBuilder sb, System.Action<Scene> body)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) { sb.AppendLine("  WARN scene not found: " + path); return; }
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = false;
        if (!scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            opened = true;
        }
        var prevActive = SceneManager.GetActiveScene();
        try
        {
            SceneManager.SetActiveScene(scene);
            body(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        finally
        {
            if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene) SceneManager.SetActiveScene(prevActive);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    static GameObject FindOrCreateRoot(Scene scene)
    {
        GameObject root = null;
        foreach (var r in scene.GetRootGameObjects())
        {
            if (r.name == RootName) { if (root == null) root = r; else Object.DestroyImmediate(r); }
            else if (r.name == RootName + " (auto)") Object.DestroyImmediate(r);
        }
        if (root == null)
        {
            root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
        }
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        // Runtime-only children (lamp light pool, door glow) are recreated on play; drop stale ones.
        for (int i = root.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
        return root;
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Items system setup: creates the shared item base materials (URP Lit) under
/// Assets/Materials/Generated/Items/ and registers them + the asset-pack prop prefabs used by
/// "prefab:&lt;key&gt;" item visuals in RubbishAssets. Idempotent: re-running updates in place.
/// Also hosts the "Loot Table Report" debug menu.
/// </summary>
public static class Setup_Items
{
    const string MatFolder = "Assets/Materials/Generated/Items";

    /// <summary>item/&lt;key&gt; -> prefab file name (searched in the Garage + Suburbs packs).</summary>
    static readonly (string key, string prefab)[] PropPrefabs =
    {
        ("tire", "SM_tire_01"),
        ("car_battery", "SM_carbattery"),
        ("paint_bucket", "SM_paint_bucket_01"),
        ("paint_bucket_2", "SM_paint_bucket_02"),
        ("paint_bucket_3", "SM_paint_bucket_03"),
        ("jerrycan", "SM_container_fluid_02"),
        ("propane", "SM_gasoline_container"),
        ("extinguisher", "SM_fire_extinguisher"),
        ("toolbox", "SM_toolbox_01"),
        ("hammer", "SM_hammer"),
        ("drill", "SM_drill_machine"),
        ("duct_tape", "SM_tape_01"),
        ("spray_bottle", "SM_bottles_mat_01"),
        ("box_small", "SM_box_small_02"),
        ("lemon", "SM_lemon"),
    };

    static readonly string[] PackFolders = { "Assets/Garage (Low Poly)", "Assets/Suburbs Town Pack" };

    [MenuItem("Rubbish/Setup/Items")]
    public static void Run()
    {
        var log = new StringBuilder("[Setup_Items] ");
        EnsureFolder(MatFolder);

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[Setup_Items] URP Lit shader not found; is URP installed?");
            return;
        }

        var baseMat = GetOrCreateMat("item_base", lit);
        baseMat.SetColor("_BaseColor", Color.white);
        baseMat.SetFloat("_Metallic", 0f);
        baseMat.SetFloat("_Smoothness", 0.3f);
        baseMat.enableInstancing = true;

        var metal = GetOrCreateMat("item_metal", lit);
        metal.SetColor("_BaseColor", Color.white);
        metal.SetFloat("_Metallic", ItemVisuals.MetalMetallic);
        metal.SetFloat("_Smoothness", ItemVisuals.MetalSmoothness);
        metal.enableInstancing = true;

        var glass = GetOrCreateMat("item_glass", lit);
        glass.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.45f));
        glass.SetFloat("_Metallic", 0f);
        glass.SetFloat("_Smoothness", 0.92f);
        MakeTransparent(glass);

        var emissive = GetOrCreateMat("item_emissive", lit);
        emissive.SetColor("_BaseColor", Color.white);
        emissive.SetFloat("_Smoothness", 0.5f);
        emissive.EnableKeyword("_EMISSION");
        emissive.SetColor("_EmissionColor", Color.white);
        emissive.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        foreach (var m in new[] { baseMat, metal, glass, emissive }) EditorUtility.SetDirty(m);

        RubbishAssets.EditorRegisterMaterial("mat/item_base", baseMat);
        RubbishAssets.EditorRegisterMaterial("mat/item_metal", metal);
        RubbishAssets.EditorRegisterMaterial("mat/item_glass", glass);
        RubbishAssets.EditorRegisterMaterial("mat/item_emissive", emissive);
        log.Append("materials: 4 registered; ");

        int found = 0;
        var missing = new List<string>();
        foreach (var (key, prefabName) in PropPrefabs)
        {
            string path = FindPrefab(prefabName);
            if (path == null) { missing.Add(prefabName); continue; }
            RubbishAssets.EditorRegisterPrefab("item/" + key, path);
            found++;
        }
        log.Append($"prefabs: {found}/{PropPrefabs.Length} registered");
        if (missing.Count > 0) log.Append(" (missing: " + string.Join(", ", missing) + " -> hand-built fallback visuals)");

        EditorUtility.SetDirty(RubbishAssets.EditorGetOrCreate());
        AssetDatabase.SaveAssets();
        log.Append($"; catalog: {ItemCatalog.All.Count} items.");
        Debug.Log(log.ToString());
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    static Material GetOrCreateMat(string name, Shader shader)
    {
        string path = $"{MatFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader != shader) m.shader = shader;
        return m;
    }

    static void MakeTransparent(Material m)
    {
        m.SetFloat("_Surface", 1f);   // Transparent
        m.SetFloat("_Blend", 0f);     // Alpha
        m.SetFloat("_AlphaClip", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    static string FindPrefab(string fileName)
    {
        foreach (var guid in AssetDatabase.FindAssets(fileName + " t:Prefab", PackFolders))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(p) == fileName) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ debug report

    [MenuItem("Rubbish/Debug/Loot Table Report")]
    public static void LootTableReportMenu() => Debug.Log(LootTableReport(1000));

    [MenuItem("Rubbish/Debug/District Loot Report (Monte Carlo)")]
    public static void DistrictLootReportMenu() => Debug.Log(DistrictLootReport(10000, 1f));

    /// <summary>
    /// Docs/Progression.md §5.4 acceptance: per district, `bags` bags rolled with its LootProfile at the given Trust over the
    /// district's house-wealth bands (§5.3): valuables per bag, mean valuable, treasure EV per bag, rarity mix, bag mass.
    /// </summary>
    public static string DistrictLootReport(int bags, float trust01)
    {
        var mixes = new[]
        {
            new[] { new Vector3(0.24f, 0.20f, 0.35f), new Vector3(0.46f, 0.45f, 0.60f), new Vector3(0.30f, 0.75f, 0.90f) },
            new[] { new Vector3(0.30f, 0.30f, 0.45f), new Vector3(0.45f, 0.45f, 0.65f), new Vector3(0.25f, 0.65f, 0.80f) },
            new[] { new Vector3(0.30f, 0.40f, 0.60f), new Vector3(0.40f, 0.60f, 0.80f), new Vector3(0.30f, 0.80f, 0.90f) },
            new[] { new Vector3(0.50f, 0.60f, 0.85f), new Vector3(0.50f, 0.85f, 1.00f) },
        };
        float[] specVals = { 0.082f, 0.138f, 0.190f, 0.254f }, specEv = { 5f, 27f, 151f, 639f };
        var sb = new StringBuilder($"[District Loot Report] {bags} bags per district at Trust {trust01:0.##}\n");
        var all = Districts.All;
        for (int di = 0; di < all.Count && di < mixes.Length; di++)
        {
            var d = all[di];
            var rng = new System.Random(4242 + di);
            double vals = 0, ev = 0, mass = 0; var rar = new int[5];
            for (int b = 0; b < bags; b++)
            {
                double r = rng.NextDouble(); float w = 0f;
                foreach (var m in mixes[di]) { w = m.y + (m.z - m.y) * (float)rng.NextDouble(); if (r < m.x) break; r -= m.x; }
                string id = $"report/{d.id}/{b}";
                var bag = ItemCatalog.RollBag(id, PickupVisuals.StableHash(id), w, "1 Test St", d.loot, trust01);
                mass += bag.TotalMassKg;
                foreach (var it in bag.items)
                {
                    var def = it.Def;
                    if (def == null || def.category != ItemCategory.Valuable) continue;
                    vals++; ev += it.value; rar[(int)def.rarity]++;
                }
            }
            sb.AppendLine($"  {d.name,-22} valuables/bag {vals / bags:0.000} (spec {specVals[di]:0.000}) | EV/bag ${ev / bags:0.0} (spec ${specEv[di]}) | " +
                          $"mean ${(vals > 0 ? ev / vals : 0):0} | U/R/E/L {100 * rar[1] / System.Math.Max(1, vals):0}/{100 * rar[2] / System.Math.Max(1, vals):0}/" +
                          $"{100 * rar[3] / System.Math.Max(1, vals):0}/{100 * rar[4] / System.Math.Max(1, vals):0.0}% | {mass / bags:0.0} kg");
        }
        return sb.ToString();
    }

    /// <summary>Rolls `bags` bags at wealth 0 / 0.5 / 1 and summarises mass, category mix and treasure odds.</summary>
    public static string LootTableReport(int bags)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Loot Table Report] {ItemCatalog.All.Count} item defs, {bags} bags per wealth level");
        var perCat = new Dictionary<ItemCategory, int>();
        foreach (var d in ItemCatalog.All) perCat[d.category] = perCat.TryGetValue(d.category, out var n) ? n + 1 : 1;
        sb.Append("  defs per category:");
        foreach (var kv in perCat) sb.Append($" {kv.Key}={kv.Value}");
        sb.AppendLine();

        foreach (float wealth in new[] { 0f, 0.5f, 1f })
        {
            int items = 0, over15 = 0, under3 = 0, bagsWithValuable = 0;
            float massSum = 0f, minMass = float.MaxValue, maxMass = 0f, valueSum = 0f, recycleSum = 0f;
            var cat = new Dictionary<ItemCategory, int>();
            var catKg = new Dictionary<ItemCategory, float>();
            var rar = new Dictionary<Rarity, int>();
            var themes = new Dictionary<string, int>();
            for (int b = 0; b < bags; b++)
            {
                var bag = ItemCatalog.RollBag($"report/w{wealth}/b{b}", 1234567 + b * 7919, wealth, "1 Test St");
                float m = bag.TotalMassKg;
                massSum += m; minMass = Mathf.Min(minMass, m); maxMass = Mathf.Max(maxMass, m);
                if (m > 15f) over15++;
                if (m < 3f) under3++;
                string th = ItemCatalog.BagTheme(bag) ?? "plain";
                themes[th] = themes.TryGetValue(th, out var tn) ? tn + 1 : 1;
                bool hasVal = false;
                foreach (var it in bag.items)
                {
                    items++;
                    var d = it.Def;
                    cat[d.category] = cat.TryGetValue(d.category, out var cn) ? cn + 1 : 1;
                    catKg[d.category] = (catKg.TryGetValue(d.category, out var ck) ? ck : 0f) + it.massKg;
                    if (d.category == ItemCategory.Valuable)
                    {
                        hasVal = true;
                        rar[d.rarity] = rar.TryGetValue(d.rarity, out var rn) ? rn + 1 : 1;
                        valueSum += it.value;
                    }
                    else recycleSum += ItemCatalog.EstimatedValue(it);
                }
                if (hasVal) bagsWithValuable++;
            }
            sb.AppendLine($"-- wealth {wealth:0.0}: avg {massSum / bags:0.00} kg (min {minMass:0.0}, max {maxMass:0.0}, <3kg {under3}, >15kg {over15}), " +
                          $"avg {items / (float)bags:0.0} items/bag, bags w/ treasure {100f * bagsWithValuable / bags:0.0}%");
            sb.Append("   category share:");
            foreach (ItemCategory c in System.Enum.GetValues(typeof(ItemCategory)))
                sb.Append($" {c} {100f * (cat.TryGetValue(c, out var v) ? v : 0) / items:0.0}%");
            sb.AppendLine();
            sb.Append("   avg kg/item:");
            foreach (ItemCategory c in System.Enum.GetValues(typeof(ItemCategory)))
                if (cat.TryGetValue(c, out var v2) && v2 > 0) sb.Append($" {c} {catKg[c] / v2:0.00}");
            sb.AppendLine();
            int valCount = cat.TryGetValue(ItemCategory.Valuable, out var vc) ? vc : 0;
            sb.Append($"   valuables: {100f * valCount / items:0.00}% of items, per 100 bags:");
            foreach (Rarity r in new[] { Rarity.Uncommon, Rarity.Rare, Rarity.Epic, Rarity.Legendary })
                sb.Append($" {r} {100f * (rar.TryGetValue(r, out var rv) ? rv : 0) / bags:0.0}");
            sb.AppendLine($" | treasure ${valueSum / bags:0.00}/bag, scrap+recycle ${recycleSum / bags:0.00}/bag");
            sb.Append("   themes:");
            foreach (var kv in themes) sb.Append($" {kv.Key} {100f * kv.Value / bags:0.0}%");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

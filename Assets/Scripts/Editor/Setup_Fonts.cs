using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// Bakes the UI font, Fredoka (SIL OFL 1.1; <c>Assets/Fonts/Fredoka/OFL.txt</c> ships with it), into three <b>static</b> TMP SDF
/// assets and wires them for <see cref="UiTheme"/> (Docs/UIPass.md §13 Q1, approved by Nick on 1 Oct 2026):
/// <list type="bullet">
/// <item><c>Assets/Fonts/Fredoka/Fredoka-{Regular,SemiBold,Bold} SDF.asset</c>: a 1024² atlas each with ASCII, Latin-1 and the
/// punctuation the game prints (· — – … × ‘ ’ “ ” • € ™ −), at the biggest sampling size that fits (90 pt down). Static, so the
/// TTFs never ship and nothing is rasterised at runtime.</item>
/// <item>Each falls back to <b>LiberationSans SDF</b> (TMP's default, itself backed by a dynamic fallback), so a glyph Fredoka
/// lacks (→ ≥ ♪ ★) still renders instead of a box.</item>
/// <item>Weight tables: on Regular, the Bold style (and SemiBold) use the SemiBold asset and Heavy/Black use Bold; SemiBold and
/// Bold map their own Bold style to themselves. So <c>FontStyles.Bold</c> never applies TMP's faux bold to Fredoka.</item>
/// <item><c>Assets/Prefabs/UI/UiFonts.prefab</c> (a <see cref="UiFontSet"/>: display = Bold, label = SemiBold, body = Regular),
/// registered in <see cref="RubbishAssets"/> as <c>ui/fonts</c> so builds include it.</item>
/// </list>
/// Idempotent: a second run finds everything current and writes nothing. A font asset whose recipe (character set, atlas,
/// shader) differs is rebuilt in place, keeping its GUID. Touches no scene.
/// </summary>
public static class Setup_Fonts
{
    public const string FontDir = "Assets/Fonts/Fredoka";
    public const string PrefabPath = "Assets/Prefabs/UI/UiFonts.prefab";
    public const string LiberationPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
    const string ShaderName = "TextMeshPro/Distance Field";
    const int Atlas = 1024;
    static readonly int[] SamplingSizes = { 90, 84, 78, 72, 66, 60 };

    /// <summary>Bump to force a rebuild of all three atlases.</summary>
    const string RecipeVersion = "rubbish-ui-fonts v1";

    // ASCII, Latin-1 Supplement, and the general punctuation/currency the game's strings use.
    static readonly int[][] Ranges = { new[] { 32, 126 }, new[] { 160, 255 } };
    static readonly int[] Extra =
    {
        0x2013, 0x2014, 0x2018, 0x2019, 0x201A, 0x201C, 0x201D, 0x201E, 0x2020, 0x2021, 0x2022, 0x2026, 0x2030, 0x2039, 0x203A,
        0x2044, 0x20AC, 0x2122, 0x2212,
    };

    [MenuItem("Rubbish/Setup/Fonts")]
    public static void Run()
    {
        var log = new StringBuilder();
        bool ok;
        try { ok = Apply(log); }
        catch (System.Exception e) { ok = false; log.AppendLine("EXCEPTION " + e); }
        Debug.Log("[Setup_Fonts] " + (ok ? "OK" : "FAILED") + "\n" + log);
    }

    /// <summary>Bake/verify the three assets, the prefab and the registry entry. Returns false on failure (details in log).</summary>
    public static bool Apply(StringBuilder log)
    {
        if (Application.isPlaying) { log.AppendLine("exit play mode first"); return false; }
        var t0 = System.DateTime.UtcNow;
        var liberation = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(LiberationPath);
        if (liberation == null) liberation = TMP_Settings.defaultFontAsset;
        if (liberation == null) log.AppendLine("WARNING no LiberationSans SDF found: the Fredoka assets get no fallback");

        int changes = 0;
        var regular = Ensure("Regular", log, ref changes);
        var semi = Ensure("SemiBold", log, ref changes);
        var bold = Ensure("Bold", log, ref changes);
        if (regular == null || semi == null || bold == null) { log.AppendLine("a font asset is missing: nothing wired"); return false; }

        // fallbacks + weight tables (metadata; written only when different)
        changes += Wire(regular, liberation, new[] { (6, semi), (7, semi), (8, bold), (9, bold) }, log);
        changes += Wire(semi, liberation, new[] { (6, semi), (7, semi), (8, bold), (9, bold) }, log);
        changes += Wire(bold, liberation, new[] { (6, bold), (7, bold), (8, bold), (9, bold) }, log);
        // TMP rewrites a font material's _ScaleRatioA/B/C the first time a text uses it; doing it here keeps the assets byte-stable
        foreach (var fa in new[] { regular, semi, bold }) changes += Ratios(fa, log);
        foreach (var fa in new[] { regular, semi, bold }) AssetDatabase.SaveAssetIfDirty(fa);

        // the font set prefab + its registry entry
        var prefab = EnsurePrefab(bold, semi, regular, log, ref changes);
        if (prefab == null) return false;
        if (RubbishAssets.Prefab(UiFontSet.Key) != prefab)
        {
            RubbishAssets.EditorRegisterPrefab(UiFontSet.Key, prefab);
            AssetDatabase.SaveAssetIfDirty(RubbishAssets.EditorGetOrCreate());
            log.AppendLine($"registered RubbishAssets '{UiFontSet.Key}' -> {PrefabPath}");
            changes++;
        }
        else log.AppendLine($"RubbishAssets '{UiFontSet.Key}' already registered");

        UiTheme.ReloadFonts();
        log.AppendLine($"{(changes == 0 ? "no changes" : changes + " change(s)")} in {(System.DateTime.UtcNow - t0).TotalSeconds:0.00} s; " +
                       $"UiTheme body={UiTheme.BodyFont?.name} label={UiTheme.LabelFont?.name} display={UiTheme.DisplayFont?.name}");
        return true;
    }

    // ------------------------------------------------------------------ the atlases

    static string Recipe(out List<int> codepoints)
    {
        codepoints = new List<int>();
        var sb = new StringBuilder(RecipeVersion).Append(": ");
        foreach (var r in Ranges)
        {
            for (int c = r[0]; c <= r[1]; c++) codepoints.Add(c);
            sb.Append(r[0]).Append(" - ").Append(r[1]).Append(", ");
        }
        foreach (int c in Extra) { codepoints.Add(c); sb.Append(c).Append(", "); }
        return sb.ToString().TrimEnd(' ', ',');
    }

    static TMP_FontAsset Ensure(string weight, StringBuilder log, ref int changes)
    {
        string ttfPath = $"{FontDir}/Fredoka-{weight}.ttf";
        string assetPath = $"{FontDir}/Fredoka-{weight} SDF.asset";
        var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
        if (font == null) { log.AppendLine("missing " + ttfPath); return null; }
        string recipe = Recipe(out var wanted);

        // the characters this weight really has (the rest come from the LiberationSans fallback)
        FontEngine.InitializeFontEngine();
        if (FontEngine.LoadFontFace(font, 90) != FontEngineError.Success) { log.AppendLine("can't load " + ttfPath + " (Include Font Data off?)"); return null; }
        var have = new StringBuilder();
        int absent = 0;
        foreach (int c in wanted)
        {
            if (FontEngine.TryGetGlyphIndex((uint)c, out uint gi) && gi != 0) have.Append(char.ConvertFromUtf32(c));
            else absent++;
        }
        string chars = have.ToString();

        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        string why = "new";
        if (existing != null && Current(existing, recipe, chars, out why))
        {
            log.AppendLine($"{weight}: current ({existing.characterTable.Count} glyphs, sampling {existing.creationSettings.pointSize} pt, {existing.atlasTexture.width}x{existing.atlasTexture.height})");
            return existing;
        }

        // fit: the biggest sampling size whose glyphs all fit one 1024² atlas
        TMP_FontAsset fresh = null;
        int size = 0, pad = 0;
        foreach (int s in SamplingSizes)
        {
            int p = Mathf.Max(6, Mathf.RoundToInt(s * 0.11f)); // ≥ 10% padding: room for the HUD's outline + underlay
            var fa = TMP_FontAsset.CreateFontAsset(font, s, p, GlyphRenderMode.SDFAA, Atlas, Atlas, AtlasPopulationMode.Dynamic, false);
            if (fa == null) { log.AppendLine($"{weight}: CreateFontAsset failed at {s} pt"); return existing; }
            bool ok = fa.TryAddCharacters(chars, out string missing, true);
            if (ok && string.IsNullOrEmpty(missing)) { fresh = fa; size = s; pad = p; break; }
            Discard(fa);
        }
        if (fresh == null) { log.AppendLine($"{weight}: the character set doesn't fit a {Atlas}² atlas even at {SamplingSizes[SamplingSizes.Length - 1]} pt"); return existing; }

        string name = $"Fredoka-{weight} SDF";
        fresh.name = name;
        fresh.atlasTexture.name = name + " Atlas";
        var sh = Shader.Find(ShaderName);
        if (sh != null) fresh.material.shader = sh; // same shader as LiberationSans SDF (outline + underlay for the HUD look)
        fresh.material.name = name + " Material";
        fresh.atlasPopulationMode = AtlasPopulationMode.Static;
        fresh.creationSettings = new FontAssetCreationSettings
        {
            sourceFontFileName = System.IO.Path.GetFileName(ttfPath),
            sourceFontFileGUID = AssetDatabase.AssetPathToGUID(ttfPath),
            pointSize = size,
            padding = pad,
            packingMode = 0,
            atlasWidth = Atlas,
            atlasHeight = Atlas,
            characterSetSelectionMode = 5,
            characterSequence = recipe,
            renderMode = (int)GlyphRenderMode.SDFAA,
            includeFontFeatures = true,
        };

        TMP_FontAsset result;
        if (existing == null)
        {
            AssetDatabase.CreateAsset(fresh, assetPath);
            AssetDatabase.AddObjectToAsset(fresh.atlasTexture, fresh);
            AssetDatabase.AddObjectToAsset(fresh.material, fresh);
            result = fresh;
            log.Append($"{weight}: created");
        }
        else
        {
            // rebuild in place: same GUID, so every reference survives
            var tex = fresh.atlasTexture;
            var mat = fresh.material;
            foreach (var sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath))
                if (sub is Texture2D || sub is Material) AssetDatabase.RemoveObjectFromAsset(sub);
            EditorUtility.CopySerialized(fresh, existing);
            AssetDatabase.AddObjectToAsset(tex, existing);
            AssetDatabase.AddObjectToAsset(mat, existing);
            existing.ReadFontAssetDefinition();
            Object.DestroyImmediate(fresh);
            result = existing;
            log.Append($"{weight}: rebuilt ({why})");
        }
        EditorUtility.SetDirty(result.atlasTexture);
        EditorUtility.SetDirty(result.material);
        EditorUtility.SetDirty(result);
        AssetDatabase.SaveAssetIfDirty(result);
        changes++;
        log.AppendLine($" {result.characterTable.Count} glyphs ({absent} of the set not in the font -> LiberationSans), sampling {size} pt, padding {pad}, {Atlas}x{Atlas}, {result.glyphTable.Count} glyphs packed");
        return result;
    }

    static bool Current(TMP_FontAsset fa, string recipe, string chars, out string why)
    {
        why = null;
        if (fa.atlasPopulationMode != AtlasPopulationMode.Static) why = "not static";
        else if (fa.creationSettings.characterSequence != recipe) why = "recipe changed";
        else if (fa.atlasTexture == null || fa.atlasTexture.width != Atlas || fa.atlasTexture.height != Atlas) why = "atlas size";
        else if (fa.material == null || fa.material.shader == null || fa.material.shader.name != ShaderName) why = "shader";
        else if (fa.material.GetTexture(ShaderUtilities.ID_MainTex) != fa.atlasTexture) why = "material atlas";
        else
        {
            var lookup = fa.characterLookupTable;
            for (int i = 0; i < chars.Length; i++)
            {
                int c = char.ConvertToUtf32(chars, i);
                if (char.IsHighSurrogate(chars[i])) i++;
                if (lookup == null || !lookup.ContainsKey((uint)c)) { why = "missing U+" + c.ToString("X4"); break; }
            }
        }
        return why == null;
    }

    static void Discard(TMP_FontAsset fa)
    {
        if (fa == null) return;
        if (fa.atlasTextures != null) foreach (var t in fa.atlasTextures) if (t != null) Object.DestroyImmediate(t);
        if (fa.material != null) Object.DestroyImmediate(fa.material);
        Object.DestroyImmediate(fa);
    }

    /// <summary>Fallback = LiberationSans; weight slots as given. Returns 1 if anything changed.</summary>
    static int Wire(TMP_FontAsset fa, TMP_FontAsset liberation, (int slot, TMP_FontAsset font)[] weights, StringBuilder log)
    {
        bool changed = false;
        var so = new SerializedObject(fa);
        var fb = so.FindProperty("m_FallbackFontAssetTable");
        if (liberation != null && fb != null)
        {
            bool has = fb.arraySize == 1 && fb.GetArrayElementAtIndex(0).objectReferenceValue == liberation;
            if (!has)
            {
                fb.arraySize = 1;
                fb.GetArrayElementAtIndex(0).objectReferenceValue = liberation;
                changed = true;
            }
        }
        var wt = so.FindProperty("m_FontWeightTable");
        if (wt != null)
        {
            if (wt.arraySize != 10) { wt.arraySize = 10; changed = true; }
            for (int i = 0; i < 10; i++)
            {
                TMP_FontAsset want = null;
                foreach (var w in weights) if (w.slot == i) want = w.font;
                var reg = wt.GetArrayElementAtIndex(i).FindPropertyRelative("regularTypeface");
                if (reg != null && reg.objectReferenceValue != want) { reg.objectReferenceValue = want; changed = true; }
            }
        }
        if (!changed) return 0;
        so.ApplyModifiedPropertiesWithoutUndo();
        fa.ReadFontAssetDefinition();
        EditorUtility.SetDirty(fa);
        log.AppendLine($"{fa.name}: fallback + weight table wired");
        return 1;
    }

    /// <summary>Precompute the SDF shader ratios TMP would otherwise write on first use. Returns 1 if they changed.</summary>
    static int Ratios(TMP_FontAsset fa, StringBuilder log)
    {
        var m = fa.material;
        if (m == null || !m.HasProperty(ShaderUtilities.ID_ScaleRatio_A)) return 0;
        float a = m.GetFloat(ShaderUtilities.ID_ScaleRatio_A), b = m.GetFloat(ShaderUtilities.ID_ScaleRatio_B), c = m.GetFloat(ShaderUtilities.ID_ScaleRatio_C);
        ShaderUtilities.UpdateShaderRatios(m);
        bool same = Mathf.Approximately(a, m.GetFloat(ShaderUtilities.ID_ScaleRatio_A)) && Mathf.Approximately(b, m.GetFloat(ShaderUtilities.ID_ScaleRatio_B)) &&
                    Mathf.Approximately(c, m.GetFloat(ShaderUtilities.ID_ScaleRatio_C));
        if (same) return 0;
        EditorUtility.SetDirty(m);
        EditorUtility.SetDirty(fa);
        log.AppendLine($"{fa.name}: shader ratios set");
        return 1;
    }

    // ------------------------------------------------------------------ the font set prefab

    static GameObject EnsurePrefab(TMP_FontAsset display, TMP_FontAsset label, TMP_FontAsset body, StringBuilder log, ref int changes)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset != null)
        {
            var set = asset.GetComponent<UiFontSet>();
            if (set != null && set.display == display && set.label == label && set.body == body)
            {
                log.AppendLine("UiFonts.prefab current");
                return asset;
            }
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var s = root.GetComponent<UiFontSet>() ?? root.AddComponent<UiFontSet>();
                s.display = display; s.label = label; s.body = body;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool ok);
                if (!ok) { log.AppendLine("couldn't save " + PrefabPath); return null; }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            changes++;
            log.AppendLine("UiFonts.prefab updated");
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        // a brand-new prefab is assembled in a preview scene, so the open scene (Bootstrap) is never dirtied
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs/UI")) AssetDatabase.CreateFolder("Assets/Prefabs", "UI");
        var preview = EditorSceneManager.NewPreviewScene();
        GameObject go = null;
        try
        {
            go = new GameObject("UiFonts");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, preview);
            var s = go.AddComponent<UiFontSet>();
            s.display = display; s.label = label; s.body = body;
            var saved = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath, out bool ok);
            if (!ok || saved == null) { log.AppendLine("couldn't save " + PrefabPath); return null; }
            changes++;
            log.AppendLine("UiFonts.prefab created");
            return saved;
        }
        finally
        {
            if (go != null) Object.DestroyImmediate(go);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }
}

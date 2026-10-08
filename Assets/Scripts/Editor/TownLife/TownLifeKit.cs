// TownLife kit, part 1: session, palette materials, asset choke point, spawning, linear runs, ground geometry, text.
// Owner: kit agent (architect wrote the minimal working core; see CONTRACT.md). Part 2 (TownLifeKit.Props.cs) holds
// the prop recipe type, the primitive mesh generators and the stock prop library.
//
// Rules this file enforces centrally so content modules cannot get them wrong:
//  * every mesh and material is an asset under AssetDir (or an in-memory HideAndDontSave object in TRANSIENT mode);
//    meshes are SUB-ASSETS of a handful of container assets (Props/TL_Props.asset, Ground/TL_Ground_<module>.asset)
//    so a full build writes ~10 mesh files instead of a thousand; materials are one .mat each;
//  * every asset write goes through PrepareWrite (calls BeforeAssetWrite, records the path);
//  * unchanged content is never rewritten (no dirty flag, no GUID churn);
//  * one GameObject per prop, parts merged into one mesh with a submesh per material;
//  * static flags, layers and shadow settings are applied by Finish().
using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Materials, meshes, props, runs, ground pieces and labels for every TownLife content module.</summary>
public static partial class TownLifeKit
{
    /// <summary>Folder that owns every TownLife mesh and material asset (sub folders Mat/, Props/, Ground/).</summary>
    public const string AssetDir = "Assets/Materials/Generated/TownLife";
    /// <summary>The existing NB_* palette (read only; Mat("NB_Hedge") loads from here, never writes).</summary>
    public const string NeighborhoodMatDir = "Assets/Materials/Generated/Neighborhood";
    /// <summary>Unity layers used by scenery: decorative solids on Default, walkable surfaces on Ground.</summary>
    public const int LayerDefault = 0, LayerGround = 6;
    /// <summary>Name prefix of every object, mesh and material the kit creates.</summary>
    public const string Prefix = "TL_";

    /// <summary>True between Begin(true) and End(): nothing touches the AssetDatabase; meshes and materials are
    /// in-memory HideAndDontSave objects that DestroyTransient() removes.</summary>
    public static bool Transient { get; private set; }
    /// <summary>True between Begin and End.</summary>
    public static bool Active { get; private set; }
    /// <summary>Called with the asset path BEFORE any asset is created or modified (non-transient mode only). The
    /// builder points it at CityEditTransaction.Current.WatchAsset.</summary>
    public static Action<string> BeforeAssetWrite;

    static readonly List<string> usedAssets = new List<string>(), writtenAssets = new List<string>();
    static readonly HashSet<string> usedSet = new HashSet<string>();
    static readonly List<UnityEngine.Object> transientObjects = new List<UnityEngine.Object>();
    static readonly Dictionary<string, MatDef> matDefs = new Dictionary<string, MatDef>();
    static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
    static readonly HashSet<string> groundNames = new HashSet<string>();
    static readonly Dictionary<string, MeshContainer> containers = new Dictionary<string, MeshContainer>();
    static int meshesWritten, meshesReused;
    static int spawnCount;

    /// <summary>Every asset path the current session referenced (written or reused unchanged). The builder deletes
    /// TownLife assets that are not in this list after a successful real build.</summary>
    public static IReadOnlyList<string> UsedAssets => usedAssets;
    /// <summary>Asset paths actually created or modified in the current session.</summary>
    public static IReadOnlyList<string> WrittenAssets => writtenAssets;
    /// <summary>GameObjects created by the kit since Begin (budget tracking).</summary>
    public static int SpawnCount => spawnCount;
    /// <summary>Mesh sub-assets created or rewritten / found identical and left untouched in the current session.</summary>
    public static int MeshesWritten => meshesWritten;
    public static int MeshesReused => meshesReused;
    /// <summary>Live transient meshes/materials (0 after DestroyTransient).</summary>
    public static int TransientObjectCount => transientObjects.Count;

    // ------------------------------------------------------------------------------------------------ session

    /// <summary>Starts a kit session: destroys leftover transient objects, clears every cache and definition, sets the
    /// mode, then defines the stock palette and stock props. Call exactly once per build, before any module runs.</summary>
    public static void Begin(bool transient)
    {
        DestroyTransient();
        usedAssets.Clear(); writtenAssets.Clear(); usedSet.Clear(); matDefs.Clear(); mats.Clear(); groundNames.Clear();
        containers.Clear(); meshesWritten = 0; meshesReused = 0;
        ResetProps(); spawnCount = 0;
        Transient = transient; Active = true;
        DefineStockPalette(); DefineStockProps();
    }

    /// <summary>Ends the session. Does NOT save assets (the builder's transaction does) and does NOT destroy transient
    /// objects (the preview still has to photograph them; call DestroyTransient afterwards).</summary>
    public static void End() { Active = false; }

    /// <summary>Destroys every in-memory mesh and material created in transient mode. Safe to call repeatedly.</summary>
    public static void DestroyTransient()
    {
        foreach (var o in transientObjects) if (o) UnityEngine.Object.DestroyImmediate(o);
        transientObjects.Clear();
        if (Transient) { mats.Clear(); ResetBuiltProps(); }
    }

    static void RequireActive() { if (!Active) throw new InvalidOperationException("TownLifeKit.Begin(transient) has not been called."); }

    // ------------------------------------------------------------------------------------- asset choke point

    /// <summary>THE single choke point for asset writes: fires BeforeAssetWrite, records the path, creates folders.</summary>
    static void PrepareWrite(string path)
    {
        if (Transient) throw new InvalidOperationException("Asset write attempted in transient mode: " + path);
        BeforeAssetWrite?.Invoke(path);
        if (!writtenAssets.Contains(path)) writtenAssets.Add(path);
        EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
    }
    static void MarkUsed(string path) { if (usedSet.Add(path)) usedAssets.Add(path); }
    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
    /// <summary>Asset-safe file name fragment: letters, digits, '-', '_' kept, everything else becomes '_'.</summary>
    public static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_')) chars[i] = '_';
        return new string(chars);
    }

    /// <summary>Raw triangle soup with a submesh per material key. Positions are in the space of the object that will
    /// carry the mesh (prop-local for props, WORLD for ground pieces). Public so modules can build one-off shapes.</summary>
    public sealed class MeshData
    {
        public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
        public readonly List<string> materials = new List<string>();
        public readonly List<List<int>> triangles = new List<List<int>>();

        List<int> Sub(string mat)
        {
            int i = materials.IndexOf(mat);
            if (i < 0) { materials.Add(mat); triangles.Add(new List<int>()); i = materials.Count - 1; }
            return triangles[i];
        }
        /// <summary>Flat-shaded triangle, clockwise seen from outside.</summary>
        public void Tri(string mat, Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a); if (n.sqrMagnitude < 1e-12f) return; n.Normalize();
            Tri(mat, a, b, c, n, n, n);
        }
        /// <summary>Triangle with explicit normals (smooth shading).</summary>
        public void Tri(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
        {
            var t = Sub(mat); int k = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); normals.Add(na); normals.Add(nb); normals.Add(nc);
            t.Add(k); t.Add(k + 1); t.Add(k + 2);
        }
        /// <summary>Flat quad, corners clockwise seen from outside.</summary>
        public void Quad(string mat, Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(mat, a, b, c); Tri(mat, a, c, d); }
        /// <summary>Flat triangle whose winding is fixed so that it faces away from <paramref name="inside"/>.</summary>
        public void TriOut(string mat, Vector3 inside, Vector3 a, Vector3 b, Vector3 c)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - inside) < 0f) Tri(mat, a, c, b); else Tri(mat, a, b, c);
        }
        /// <summary>Flat quad facing away from <paramref name="inside"/>.</summary>
        public void QuadOut(string mat, Vector3 inside, Vector3 a, Vector3 b, Vector3 c, Vector3 d) { TriOut(mat, inside, a, b, c); TriOut(mat, inside, a, c, d); }
        /// <summary>Appends another soup transformed by a matrix (normals by its inverse transpose).</summary>
        public void Append(MeshData other, Matrix4x4 m)
        {
            var nm = m.inverse.transpose; bool flip = m.determinant < 0f;
            for (int s = 0; s < other.materials.Count; s++)
            {
                var src = other.triangles[s]; string mat = other.materials[s];
                for (int i = 0; i + 2 < src.Count; i += 3)
                {
                    int i0 = src[i], i1 = src[flip ? i + 2 : i + 1], i2 = src[flip ? i + 1 : i + 2];
                    Tri(mat, m.MultiplyPoint3x4(other.vertices[i0]), m.MultiplyPoint3x4(other.vertices[i1]), m.MultiplyPoint3x4(other.vertices[i2]),
                        nm.MultiplyVector(other.normals[i0]).normalized, nm.MultiplyVector(other.normals[i1]).normalized, nm.MultiplyVector(other.normals[i2]).normalized);
                }
            }
        }
        public Bounds Bounds()
        {
            if (vertices.Count == 0) return new Bounds();
            var b = new Bounds(vertices[0], Vector3.zero); foreach (var v in vertices) b.Encapsulate(v); return b;
        }
    }

    static void Fill(Mesh mesh, MeshData d)
    {
        mesh.Clear();
        mesh.indexFormat = d.vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(d.vertices); mesh.SetNormals(d.normals);
        mesh.subMeshCount = Mathf.Max(1, d.triangles.Count);
        for (int i = 0; i < d.triangles.Count; i++) mesh.SetTriangles(d.triangles[i], i, false);
        mesh.RecalculateBounds();
    }
    static bool SameContent(Mesh mesh, MeshData d)
    {
        if (mesh.vertexCount != d.vertices.Count || mesh.subMeshCount != Mathf.Max(1, d.triangles.Count)) return false;
        var v = mesh.vertices; var n = mesh.normals;
        if (n.Length != d.normals.Count) return false;
        for (int i = 0; i < v.Length; i++) if (v[i] != d.vertices[i] || n[i] != d.normals[i]) return false;
        for (int s = 0; s < d.triangles.Count; s++)
        {
            var t = mesh.GetTriangles(s); var want = d.triangles[s];
            if (t.Length != want.Count) return false;
            for (int i = 0; i < t.Length; i++) if (t[i] != want[i]) return false;
        }
        return true;
    }
    /// <summary>One container asset: an empty placeholder mesh as the main object, every real mesh a named sub-asset.
    /// Sub-asset identity (fileID) is stable for as long as the sub-asset lives, so scene references survive rebuilds.</summary>
    sealed class MeshContainer
    {
        public string path; public Mesh main;
        public readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        public readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>"Props/TL_P_x" -> Props/TL_Props.asset; "Ground/TL_G_yards_patio_a" and "Ground/TL_R_yards_..." ->
    /// Ground/TL_Ground_yards.asset (bucket = the module prefix of the deterministic name, see CONTRACT section 1).</summary>
    static string ContainerPath(string relative)
    {
        int slash = relative.IndexOf('/'); string folder = slash < 0 ? "Misc" : relative.Substring(0, slash), name = relative.Substring(slash + 1);
        if (folder == "Props") return AssetDir + "/Props/" + Prefix + "Props.asset";
        string rest = name.StartsWith(Prefix + "G_") || name.StartsWith(Prefix + "R_") ? name.Substring(Prefix.Length + 2) : name;
        int cut = rest.IndexOf('_'); string bucket = cut > 0 ? rest.Substring(0, cut) : "misc";
        return AssetDir + "/" + folder + "/" + Prefix + folder + "_" + Sanitize(bucket).ToLowerInvariant() + ".asset";
    }

    static MeshContainer ContainerFor(string path)
    {
        if (containers.TryGetValue(path, out var c)) return c;
        c = new MeshContainer { path = path };
        if (System.IO.File.Exists(path))
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = o as Mesh; if (m == null) continue;
                if (AssetDatabase.IsMainAsset(m)) c.main = m;
                else if (!c.meshes.ContainsKey(m.name)) c.meshes.Add(m.name, m);      // duplicates by name: first wins, the rest are pruned
            }
        containers.Add(path, c); return c;
    }

    /// <summary>Stores a mesh as the sub-asset named like the last path element of <paramref name="relative"/> inside
    /// its container asset (reused and overwritten in place; left completely untouched when the content is identical),
    /// or, in transient mode, returns an in-memory mesh. Every write is announced through PrepareWrite first.</summary>
    static Mesh StoreMesh(string relative, MeshData data)
    {
        string name = System.IO.Path.GetFileName(relative);
        if (Transient)
        {
            var temp = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            Fill(temp, data); transientObjects.Add(temp); return temp;
        }
        string path = ContainerPath(relative); MarkUsed(path);
        var c = ContainerFor(path); c.used.Add(name);
        c.meshes.TryGetValue(name, out var mesh);
        if (mesh != null && SameContent(mesh, data)) { meshesReused++; return mesh; }
        PrepareWrite(path);                                   // BEFORE the first byte changes (snapshot for rollback)
        if (c.main == null)
        {
            c.main = new Mesh { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(c.main, path);
        }
        if (mesh == null)
        {
            mesh = new Mesh { name = name }; Fill(mesh, data);
            AssetDatabase.AddObjectToAsset(mesh, c.main); c.meshes[name] = mesh;
        }
        else { Fill(mesh, data); mesh.name = name; }
        EditorUtility.SetDirty(mesh); EditorUtility.SetDirty(c.main);   // main too: SaveAssetIfDirty(main) is what the transaction calls
        meshesWritten++;
        return mesh;
    }

    /// <summary>Removes stale mesh sub-assets (runs / ground pieces / props of an earlier build) from every container
    /// THIS session used: a mesh goes when the session did not use it AND it is not in <paramref name="keep"/>.
    /// Pass in <paramref name="keep"/> every mesh the scene still references (MeshFilter / MeshCollider sharedMesh):
    /// kit sessions are partial (CoastalYardAuthoring runs its own session for the fences; a preview of one module
    /// uses a subset of props), so "not used by this session" alone is NOT proof of being stale. Containers the
    /// session never touched are left alone (the builder deletes whole unused files through UsedAssets). Goes
    /// through PrepareWrite, so a transaction can roll it back. Returns the number removed. No-op in transient mode.</summary>
    public static int PruneUnusedMeshes(ICollection<Mesh> keep = null)
    {
        if (Transient) return 0;
        int removed = 0;
        foreach (var c in containers.Values)
        {
            if (c.main == null) continue;
            var stale = new List<Mesh>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(c.path))
            {
                var m = o as Mesh; if (m == null || m == c.main) continue;
                Mesh mine; if (c.used.Contains(m.name) && c.meshes.TryGetValue(m.name, out mine) && mine == m) continue;
                if (keep != null && keep.Contains(m)) continue;
                stale.Add(m);
            }
            if (stale.Count == 0) continue;
            PrepareWrite(c.path);
            foreach (var m in stale)
            {
                Mesh known; if (c.meshes.TryGetValue(m.name, out known) && known == m) c.meshes.Remove(m.name);
                AssetDatabase.RemoveObjectFromAsset(m); UnityEngine.Object.DestroyImmediate(m, true); removed++;
            }
            EditorUtility.SetDirty(c.main);
        }
        return removed;
    }

    // ---------------------------------------------------------------------------------------------- materials

    sealed class MatDef { public Color color, emission; public float smoothness, metallic; public bool emissive; }

    /// <summary>Declares a palette colour. Idempotent for identical values; redefining a key with DIFFERENT values
    /// throws (prefix module-private keys with the module name, e.g. "park.pickle"). Nothing is created until Mat(key).</summary>
    public static void Define(string key, Color color, float smoothness = 0.08f, float metallic = 0f)
    {
        DefineInternal(key, new MatDef { color = color, smoothness = smoothness, metallic = metallic });
    }
    /// <summary>Declares a self-lit colour (lamp globes, glowing windows). Same rules as Define.</summary>
    public static void DefineEmissive(string key, Color color, Color emission, float smoothness = 0.3f)
    {
        DefineInternal(key, new MatDef { color = color, smoothness = smoothness, emissive = true, emission = emission });
    }
    static void DefineInternal(string key, MatDef def)
    {
        RequireActive();
        if (string.IsNullOrEmpty(key) || key.StartsWith("NB_")) throw new ArgumentException("TownLife material keys must be non-empty and must not start with NB_: " + key);
        if (matDefs.TryGetValue(key, out var old))
        {
            if (old.color != def.color || old.smoothness != def.smoothness || old.metallic != def.metallic || old.emissive != def.emissive || old.emission != def.emission)
                throw new InvalidOperationException("TownLife material '" + key + "' is already defined with different values.");
            return;
        }
        matDefs[key] = def;
    }
    /// <summary>True when the key was defined (or names an existing NB_* material).</summary>
    public static bool HasMat(string key) { return matDefs.ContainsKey(key) || (key != null && key.StartsWith("NB_") && AssetDatabase.LoadAssetAtPath<Material>(NeighborhoodMatDir + "/" + key + ".mat") != null); }

    /// <summary>The material for a palette key (URP Lit, flat colour, GPU instancing). "NB_*" keys return the existing
    /// Neighborhood material untouched. Throws KeyNotFoundException for an undefined key.</summary>
    public static Material Mat(string key)
    {
        RequireActive();
        if (mats.TryGetValue(key, out var cached) && cached) return cached;
        Material m;
        if (key.StartsWith("NB_"))
        {
            m = AssetDatabase.LoadAssetAtPath<Material>(NeighborhoodMatDir + "/" + key + ".mat");
            if (m == null) throw new KeyNotFoundException("No Neighborhood material " + key);
            mats[key] = m; return m;
        }
        if (!matDefs.TryGetValue(key, out var def)) throw new KeyNotFoundException("TownLife material '" + key + "' was never defined (TownLifeKit.Define).");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("Shader 'Universal Render Pipeline/Lit' not found.");
        string name = Prefix + Sanitize(key);
        if (Transient)
        {
            m = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
            ApplyDef(m, def); transientObjects.Add(m);
        }
        else
        {
            string path = AssetDir + "/Mat/" + name + ".mat"; MarkUsed(path);
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { PrepareWrite(path); m = new Material(shader) { name = name }; ApplyDef(m, def); AssetDatabase.CreateAsset(m, path); }
            else if (m.shader != shader || !MatMatches(m, def)) { PrepareWrite(path); if (m.shader != shader) m.shader = shader; ApplyDef(m, def); EditorUtility.SetDirty(m); }
        }
        mats[key] = m; return m;
    }
    static void ApplyDef(Material m, MatDef d)
    {
        m.SetColor("_BaseColor", d.color); m.color = d.color;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", d.smoothness);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", d.metallic);
        if (d.emissive) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", d.emission); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive; }
        else if (m.IsKeywordEnabled("_EMISSION")) { m.DisableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.black); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack; }
        m.enableInstancing = true;
    }
    static bool MatMatches(Material m, MatDef d)
    {
        if (!m.HasProperty("_BaseColor") || !m.HasProperty("_Smoothness") || !m.HasProperty("_Metallic")) return false;
        return m.GetColor("_BaseColor") == d.color && Mathf.Approximately(m.GetFloat("_Smoothness"), d.smoothness) &&
               Mathf.Approximately(m.GetFloat("_Metallic"), d.metallic) && m.enableInstancing &&
               m.IsKeywordEnabled("_EMISSION") == d.emissive && (!d.emissive || m.GetColor("_EmissionColor") == d.emission);
    }

    /// <summary>A recoloured COPY of an existing material (all other properties, textures and keywords kept), stored as
    /// TL_&lt;key&gt;.mat. For TownLifePaint: house wall / roof variants. The source is never modified.</summary>
    public static Material Variant(string key, Material source, Color color)
    {
        RequireActive();
        if (source == null) throw new ArgumentNullException(nameof(source));
        string ck = "variant:" + key;
        if (mats.TryGetValue(ck, out var cached) && cached) return cached;
        string name = Prefix + Sanitize(key);
        Material m;
        if (Transient)
        {
            m = new Material(source) { name = name, hideFlags = HideFlags.HideAndDontSave };
            SetBase(m, color); transientObjects.Add(m);
        }
        else
        {
            string path = AssetDir + "/Mat/" + name + ".mat"; MarkUsed(path);
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var want = new Material(source) { name = name }; SetBase(want, color);
            if (m == null) { PrepareWrite(path); AssetDatabase.CreateAsset(want, path); m = want; }
            else
            {
                // full serialized comparison: a variant follows every later change of its source, not only the colour
                // (tolerant of last-digit float noise: a colour that went through a save / import comes back up to 1 ULP off,
                // and an exact comparison would rewrite the material, and dirty the published town, on every publish)
                if (!SameSerialized(EditorJsonUtility.ToJson(m), EditorJsonUtility.ToJson(want))) { PrepareWrite(path); EditorUtility.CopySerialized(want, m); m.name = name; EditorUtility.SetDirty(m); }
                UnityEngine.Object.DestroyImmediate(want);
            }
        }
        mats[ck] = m; return m;
    }
    /// <summary>Sets the base colour. The legacy "_Color" mirror gets the value the material itself reports for
    /// "_BaseColor" (a colour round trip is 1 ULP lossy), NOT the exact input: that is the state the Editor settles on
    /// by itself after the next save / import, and writing the exact value made it re-dirty the saved asset AFTER a
    /// publish (stale town signature: found at integration).</summary>
    static void SetBase(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) { m.SetColor("_BaseColor", c); if (m.HasProperty("_Color")) m.SetColor("_Color", m.GetColor("_BaseColor")); }
        else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }
    static readonly System.Text.RegularExpressions.Regex NumberToken = new System.Text.RegularExpressions.Regex(@"-?\d+\.\d+(?:[eE][-+]?\d+)?");
    /// <summary>True when two EditorJsonUtility dumps are identical apart from float noise (relative 1e-5).</summary>
    static bool SameSerialized(string a, string b)
    {
        if (a == b) return true;
        var na = NumberToken.Matches(a); var nb = NumberToken.Matches(b);
        if (na.Count != nb.Count || NumberToken.Replace(a, "#") != NumberToken.Replace(b, "#")) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < na.Count; i++)
        {
            if (na[i].Value == nb[i].Value) continue;
            double x = double.Parse(na[i].Value, culture), y = double.Parse(nb[i].Value, culture);
            if (Math.Abs(x - y) > 1e-5 * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)))) return false;
        }
        return true;
    }
    static Color BaseOf(Material m) { return m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white; }

    // ------------------------------------------------------------------------------------------------ objects

    /// <summary>New empty child group named exactly <paramref name="name"/> (callers pass "TL_..." names), local identity.</summary>
    public static Transform Group(Transform parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); spawnCount++;
        return go.transform;
    }

    /// <summary>Central static-flag / shadow policy. Every kit object passes through here; call it yourself for objects
    /// you build by hand. castShadows false for small props and all ground pieces; occluder true only for big solids.</summary>
    public static void Finish(GameObject go, bool castShadows, bool occluder = false)
    {
        var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic;
        if (occluder) flags |= StaticEditorFlags.OccluderStatic;
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t.GetComponent<TMP_Text>() != null) continue;           // text stays non-static, like the rest of the town
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r.GetComponent<TMP_Text>() != null) { r.shadowCastingMode = ShadowCastingMode.Off; continue; }
            r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.receiveShadows = true;
        }
    }

    /// <summary>Instantiates a prop defined with DefineProp. Position is the WORLD position of the prop's ground contact
    /// point (recipe origin); yaw in degrees about +Y (0 = recipe +Z facing world +Z); uniform scale.</summary>
    public static GameObject Spawn(Transform parent, string propId, Vector3 position, float yawDeg = 0f, float scale = 1f)
    {
        return Spawn(parent, propId, position, yawDeg, new Vector3(scale, scale, scale));
    }
    /// <summary>Spawn with a non-uniform scale (x = width, y = height, z = depth multipliers).</summary>
    public static GameObject Spawn(Transform parent, string propId, Vector3 position, float yawDeg, Vector3 scale)
    {
        return SpawnRotated(parent, propId, position, Quaternion.Euler(0f, yawDeg, 0f), scale);
    }
    /// <summary>Spawn with a free rotation (leaning signs, fallen logs).</summary>
    public static GameObject SpawnRotated(Transform parent, string propId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var built = BuiltOf(propId);
        var go = new GameObject(Prefix + propId) { layer = built.recipe.layer };
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation); go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = built.mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = built.materials;
        foreach (var c in built.recipe.colliders)
        {
            if (c.capsule) { var cap = go.AddComponent<CapsuleCollider>(); cap.direction = 1; cap.radius = c.size.x; cap.height = c.size.y; cap.center = c.center; }
            else { var box = go.AddComponent<BoxCollider>(); box.center = c.center; box.size = c.size; }
        }
        Finish(go, built.recipe.castShadows, built.recipe.occluder);
        spawnCount++;
        return go;
    }

    /// <summary>Instantiates a project prefab (Suburbs Town Pack etc.). Real mode keeps the prefab link; transient mode
    /// makes a plain copy. Colliders are DISABLED unless <paramref name="colliders"/>. The object is renamed
    /// "TL_&lt;prefab name&gt;" unless <paramref name="keepName"/> (only for names the runtime must recognise, e.g. park
    /// lamps "SM_streetlamp*"). Returns null (and records nothing) when the prefab does not exist.</summary>
    public static GameObject SpawnPrefab(Transform parent, string prefabPath, Vector3 position, float yawDeg = 0f, float scale = 1f, bool colliders = false, bool keepName = false, bool castShadows = true)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath.EndsWith(".prefab") ? prefabPath : prefabPath + ".prefab");
        if (prefab == null) return null;
        var go = Transient ? UnityEngine.Object.Instantiate(prefab) : (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = keepName ? prefab.name : Prefix + prefab.name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDeg, 0f));
        go.transform.localScale = prefab.transform.localScale * scale;
        if (!colliders) foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LayerDefault;
        Finish(go, castShadows);
        spawnCount++;
        return go;
    }

    // --------------------------------------------------------------------------------------------- linear runs

    public enum RunCollider { None, Box }

    /// <summary>How to repeat a panel prop along a polyline. The PANEL prop must be modelled along local +Z from z = 0
    /// to z = panelLength, centred on x = 0, base at y = 0. The POST prop (optional) stands on its own origin.</summary>
    public sealed class RunSpec
    {
        public string panel;                    // prop id of one panel (required)
        public float panelLength = 3f;          // modelled length of the panel prop along +Z
        public string post;                     // prop id placed at joints (null = none)
        public bool postAtEveryJoint = true;    // false = posts only at polyline vertices and the two ends
        public bool postAtStart = true, postAtEnd = true;
        public RunCollider collider = RunCollider.None;
        public float colliderHeight = 1.4f, colliderThickness = 0.25f;
        public float y = TownLifeContext.GrassY;// base height of the run
        public bool merge = true;               // true: ONE GameObject + one unique mesh for the whole run; false: one GameObject per panel/post
        public bool castShadows = true;
        public float minSegment = 0.4f;         // straight pieces shorter than this are skipped
        /// <summary>Metres each straight piece is lengthened INTO a corner (both pieces at every interior vertex) so
        /// thick post-less runs (hedges, walls, kerbs, beds) close their corners instead of leaving a notch.
        /// Negative = automatic: half the panel width when there is no post prop, 0 when posts cover the joints.</summary>
        public float cornerOverlap = -1f;
    }

    /// <summary>Repeats a panel along a polyline with exact end fitting: each straight piece gets round(len / panelLength)
    /// panels (at least 1), stretched along the run to fill it exactly. Returns the run root (named "TL_" + name, at
    /// world identity). With collider = Box the root gets one child "Col" BoxCollider per straight piece (layer Default).
    /// <paramref name="name"/> must be unique and deterministic when merge is true (it names the mesh asset).</summary>
    public static GameObject Run(Transform parent, string name, IList<Vector3> polyline, RunSpec spec)
    {
        RequireActive();
        if (spec == null || string.IsNullOrEmpty(spec.panel)) throw new ArgumentException("RunSpec.panel is required");
        var root = new GameObject(Prefix + name); root.transform.SetParent(parent, false);
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); spawnCount++;
        var merged = spec.merge ? new MeshData() : null;
        var panelData = spec.merge ? BuiltOf(spec.panel).data : null;
        var postData = spec.merge && spec.post != null ? BuiltOf(spec.post).data : null;
        void Post(Vector3 p, Quaternion rot)
        {
            if (spec.post == null) return;
            if (spec.merge) merged.Append(postData, Matrix4x4.TRS(p, rot, Vector3.one));
            else SpawnRotated(root.transform, spec.post, p, rot, Vector3.one);
        }
        int pieces = 0;
        float overlap = spec.cornerOverlap >= 0f ? spec.cornerOverlap : spec.post != null ? 0f : Mathf.Min(0.5f, PropBounds(spec.panel).size.x * 0.5f);
        for (int i = 1; i < polyline.Count; i++)
        {
            Vector3 a = polyline[i - 1], b = polyline[i]; a.y = b.y = spec.y;
            float len = Vector3.Distance(a, b); if (len < spec.minSegment) continue;
            var rot = Quaternion.LookRotation((b - a) / len, Vector3.up);
            if (overlap > 0f)
            {
                // lengthen into real corners only (not into gentle bends of a curved run)
                Vector3 dir = (b - a) / len, a0 = a, b0 = b;
                if (i > 1 && Turns(polyline[i - 2], a0, b0)) a -= dir * overlap;
                if (i < polyline.Count - 1 && Turns(a0, b0, polyline[i + 1])) b += dir * overlap;
                len = Vector3.Distance(a, b);
            }
            int n = Mathf.Max(1, Mathf.RoundToInt(len / spec.panelLength));
            float step = len / n; var scale = new Vector3(1f, 1f, step / spec.panelLength);
            for (int k = 0; k < n; k++)
            {
                var p = a + (b - a) * (k / (float)n);
                if (spec.merge) merged.Append(panelData, Matrix4x4.TRS(p, rot, scale));
                else SpawnRotated(root.transform, spec.panel, p, rot, scale);
                bool first = i == 1 && k == 0;
                if (k == 0 ? (first ? spec.postAtStart : true) : spec.postAtEveryJoint) Post(p, rot);
            }
            if (i == polyline.Count - 1 && spec.postAtEnd) Post(b, rot);
            if (spec.collider == RunCollider.Box)
            {
                var col = new GameObject("Col") { layer = LayerDefault }; col.transform.SetParent(root.transform, false);
                col.transform.SetPositionAndRotation((a + b) * 0.5f + Vector3.up * (spec.colliderHeight * 0.5f), rot);
                col.AddComponent<BoxCollider>().size = new Vector3(spec.colliderThickness, spec.colliderHeight, len);
                spawnCount++;
            }
            pieces++;
        }
        if (spec.merge && pieces > 0)
        {
            var mesh = StoreMesh("Ground/" + Prefix + "R_" + Sanitize(UniqueGroundName(name)), merged);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>(); var arr = new Material[merged.materials.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = Mat(merged.materials[i]);
            mr.sharedMaterials = arr;
        }
        Finish(root, spec.castShadows);
        return root;
    }

    static bool Turns(Vector3 p, Vector3 q, Vector3 r)
    {
        Vector3 u = Flat(q - p), v = Flat(r - q); if (u.sqrMagnitude < 1e-6f || v.sqrMagnitude < 1e-6f) return false;
        return Vector3.Angle(u, v) > 25f;
    }

    // ------------------------------------------------------------------------------------------ ground geometry

    static string UniqueGroundName(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Ground / run pieces need a deterministic, unique name.");
        if (!groundNames.Add(name)) throw new InvalidOperationException("Duplicate TownLife ground/run name '" + name + "': names must be unique across the whole build.");
        return name;
    }

    /// <summary>Wraps world-space geometry in a GameObject ("TL_" + name, at world identity) with a unique mesh asset
    /// "Ground/TL_G_&lt;name&gt;". walkable = layer Ground + MeshCollider (players and NPCs stand on it); otherwise a
    /// collider-free decal on layer Default. Never casts shadows. Use for anything flat or terrain-like.</summary>
    public static GameObject Custom(Transform parent, string name, MeshData data, bool walkable = false, bool castShadows = false)
    {
        RequireActive();
        var mesh = StoreMesh("Ground/" + Prefix + "G_" + Sanitize(UniqueGroundName(name)), data);
        var go = new GameObject(Prefix + name) { layer = walkable ? LayerGround : LayerDefault };
        go.transform.SetParent(parent, false); go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var arr = new Material[data.materials.Count]; for (int i = 0; i < arr.Length; i++) arr[i] = Mat(data.materials[i]);
        go.AddComponent<MeshRenderer>().sharedMaterials = arr;
        if (walkable && data.vertices.Count > 0) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        Finish(go, castShadows);
        spawnCount++;
        return go;
    }

    /// <summary>Flat strip of constant width along a centre polyline (mitred corners) at height y. closed = loop.</summary>
    public static GameObject Ribbon(Transform parent, string name, IList<Vector3> centre, float width, string material, float y, bool walkable = false, bool closed = false)
    {
        var d = new MeshData(); int n = centre.Count; if (n < 2) throw new ArgumentException("Ribbon needs >= 2 points");
        var left = new Vector3[n]; var right = new Vector3[n]; float h = width * 0.5f;
        for (int i = 0; i < n; i++)
        {
            Vector3 prev = centre[closed ? (i - 1 + n) % n : Mathf.Max(i - 1, 0)], next = centre[closed ? (i + 1) % n : Mathf.Min(i + 1, n - 1)], p = centre[i];
            Vector3 d0 = Flat(p - prev), d1 = Flat(next - p);
            if (d0.sqrMagnitude < 1e-8f) d0 = d1; if (d1.sqrMagnitude < 1e-8f) d1 = d0;
            d0.Normalize(); d1.Normalize();
            Vector3 tangent = (d0 + d1).sqrMagnitude < 1e-6f ? d0 : (d0 + d1).normalized;
            var side = new Vector3(tangent.z, 0f, -tangent.x);               // right of travel
            float mitre = Mathf.Min(3f, 1f / Mathf.Max(0.3f, Vector3.Dot(tangent, d0)));
            left[i] = new Vector3(p.x, y, p.z) - side * (h * mitre); right[i] = new Vector3(p.x, y, p.z) + side * (h * mitre);
        }
        int count = closed ? n : n - 1;
        for (int i = 0; i < count; i++) { int j = (i + 1) % n; d.Quad(material, left[i], left[j], right[j], right[i]); }
        return Custom(parent, name, d, walkable);
    }

    /// <summary>Flat filled polygon (any winding, may be concave, no holes) at height y.</summary>
    public static GameObject Polygon(Transform parent, string name, IList<Vector3> outline, string material, float y, bool walkable = false)
    {
        var d = new MeshData(); AddPolygon(d, material, outline, y);
        return Custom(parent, name, d, walkable);
    }
    /// <summary>Flat disc.</summary>
    public static GameObject Disc(Transform parent, string name, Vector3 centre, float radius, string material, float y, bool walkable = false, int segments = 32)
    {
        return Polygon(parent, name, Circle(centre, radius, segments), material, y, walkable);
    }
    /// <summary>Flat annulus between two radii.</summary>
    public static GameObject Ring(Transform parent, string name, Vector3 centre, float innerRadius, float outerRadius, string material, float y, bool walkable = false, int segments = 40)
    {
        var d = new MeshData(); var a = Circle(centre, innerRadius, segments); var b = Circle(centre, outerRadius, segments);
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % segments;
            d.TriOut(material, Vector3.down * 1000f, At(a[i], y), At(b[i], y), At(b[j], y)); d.TriOut(material, Vector3.down * 1000f, At(a[i], y), At(b[j], y), At(a[j], y));
        }
        return Custom(parent, name, d, walkable);
    }
    /// <summary>Flat oriented rectangle: size.x across, size.y along the yaw direction.</summary>
    public static GameObject Rect(Transform parent, string name, Vector3 centre, Vector2 size, float yawDeg, string material, float y, bool walkable = false)
    {
        var q = Quaternion.Euler(0f, yawDeg, 0f); Vector3 r = q * Vector3.right * (size.x * 0.5f), f = q * Vector3.forward * (size.y * 0.5f);
        return Polygon(parent, name, new[] { centre - r - f, centre - r + f, centre + r + f, centre + r - f }, material, y, walkable);
    }
    /// <summary>Extruded polygon: flat top at yTop, vertical sides down to yBottom (plinths, kerbs, stages, raised beds,
    /// pond rims). solid = MeshCollider on layer Ground when walkable is wanted, layer Default otherwise.</summary>
    public static GameObject Extrude(Transform parent, string name, IList<Vector3> outline, float yBottom, float yTop, string topMaterial, string sideMaterial, bool walkable = false, bool castShadows = false)
    {
        var d = new MeshData(); AddPolygon(d, topMaterial, outline, yTop);
        Vector3 inside = Vector3.zero; foreach (var p in outline) inside += p; inside /= outline.Count; inside.y = (yTop + yBottom) * 0.5f;
        for (int i = 0; i < outline.Count; i++)
        {
            Vector3 a = outline[i], b = outline[(i + 1) % outline.Count];
            // winding from the polygon orientation, not from the centroid (correct for concave outlines too)
            bool ccw = SignedArea(outline) > 0f;
            if (ccw) d.Quad(sideMaterial, At(a, yTop), At(b, yTop), At(b, yBottom), At(a, yBottom));
            else d.Quad(sideMaterial, At(b, yTop), At(a, yTop), At(a, yBottom), At(b, yBottom));
        }
        return Custom(parent, name, d, walkable, castShadows);
    }
    /// <summary>Low faceted hill / mound: an elliptical dome with seeded lumpiness. Non-walkable decor unless walkable.</summary>
    public static GameObject Mound(Transform parent, string name, Vector3 centre, Vector2 radii, float height, string material, float baseY, int seed = 0, bool walkable = false, int segments = 14, int rings = 4)
    {
        var d = new MeshData(); var rng = new System.Random(seed);
        var grid = new Vector3[rings + 1, segments];
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s < segments; s++)
            {
                float t = r / (float)rings, ang = s * Mathf.PI * 2f / segments;
                float wobble = r == 0 || r == rings ? 1f : 1f + ((float)rng.NextDouble() - 0.5f) * 0.22f;
                float rad = Mathf.Sin(t * Mathf.PI * 0.5f) * wobble;
                grid[r, s] = new Vector3(centre.x + Mathf.Cos(ang) * radii.x * rad, baseY + height * Mathf.Cos(t * Mathf.PI * 0.5f) * (r == 0 ? 1f : wobble), centre.z + Mathf.Sin(ang) * radii.y * rad);
            }
        var inside = new Vector3(centre.x, baseY - height, centre.z);
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int j = (s + 1) % segments;
                if (r == 0) d.TriOut(material, inside, grid[0, 0], grid[1, s], grid[1, j]);
                else d.QuadOut(material, inside, grid[r, s], grid[r, j], grid[r + 1, j], grid[r + 1, s]);
            }
        return Custom(parent, name, d, walkable, false);
    }

    /// <summary>Points of a circle in the XZ plane (y of the centre kept), counter-clockwise seen from above.</summary>
    public static Vector3[] Circle(Vector3 centre, float radius, int segments = 32)
    {
        var pts = new Vector3[segments];
        for (int i = 0; i < segments; i++) { float a = i * Mathf.PI * 2f / segments; pts[i] = new Vector3(centre.x + Mathf.Cos(a) * radius, centre.y, centre.z + Mathf.Sin(a) * radius); }
        return pts;
    }
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static Vector3 At(Vector3 v, float y) { v.y = y; return v; }
    static float SignedArea(IList<Vector3> p)
    {
        float a = 0f; for (int i = 0; i < p.Count; i++) { var u = p[i]; var v = p[(i + 1) % p.Count]; a += u.x * v.z - v.x * u.z; }
        return a * 0.5f;
    }
    /// <summary>Ear-clipping triangulation of a simple polygon into an upward-facing fan of triangles at height y.</summary>
    public static void AddPolygon(MeshData d, string material, IList<Vector3> outline, float y)
    {
        var idx = new List<int>(); for (int i = 0; i < outline.Count; i++) idx.Add(i);
        if (idx.Count < 3) return;
        bool ccw = SignedArea(outline) > 0f;
        float Cross(Vector3 o, Vector3 a, Vector3 b) { return (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x); }
        int guard = outline.Count * outline.Count + 16;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i - 1 + idx.Count) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                Vector3 a = outline[i0], b = outline[i1], c = outline[i2];
                float cr = Cross(a, b, c); if (ccw ? cr <= 1e-7f : cr >= -1e-7f) continue;       // reflex or degenerate
                bool ear = true;
                foreach (int k in idx)
                {
                    if (k == i0 || k == i1 || k == i2) continue;
                    var p = outline[k]; float c1 = Cross(a, b, p), c2 = Cross(b, c, p), c3 = Cross(c, a, p);
                    if (ccw ? (c1 >= 0 && c2 >= 0 && c3 >= 0) : (c1 <= 0 && c2 <= 0 && c3 <= 0)) { ear = false; break; }
                }
                if (!ear) continue;
                d.TriOut(material, Vector3.down * 100000f, At(a, y), At(b, y), At(c, y));
                idx.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) idx.RemoveAt(0);          // degenerate input: drop a vertex rather than loop forever
        }
        if (idx.Count == 3) d.TriOut(material, Vector3.down * 100000f, At(outline[idx[0]], y), At(outline[idx[1]], y), At(outline[idx[2]], y));
    }

    // --------------------------------------------------------------------------------------------------- text

    /// <summary>World-space TextMeshPro label, same settings as NeighborhoodBuilder.Label: centred, bold, no wrap,
    /// auto-size 0.5..72, sized to <paramref name="size"/> metres. With identity local rotation it reads from local -Z.</summary>
    public static TextMeshPro Label(Transform parent, string name, string text, Vector3 localPos, Quaternion localRot, Vector2 size, Color color, FontStyles style = FontStyles.Bold)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos; go.transform.localRotation = localRot;
        var t = go.AddComponent<TextMeshPro>();
        t.text = text; t.color = color; t.fontStyle = style; t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap; t.enableAutoSizing = true; t.fontSizeMin = 0.5f; t.fontSizeMax = 72f;
        t.rectTransform.sizeDelta = size; t.ForceMeshUpdate();
        spawnCount++;
        return t;
    }

    /// <summary>A flat board with text on its +Z face (and on -Z when twoSided), like NeighborhoodBuilder.SignBoard:
    /// pivot "TL_&lt;name&gt;" at <paramref name="center"/> (board CENTRE, world), yaw degrees, size = (width, height,
    /// thickness). Text area = 92% x 86% of the face, 12 mm proud. Returns the pivot. Posts are up to the caller.</summary>
    public static GameObject SignBoard(Transform parent, string name, Vector3 center, float yawDeg, Vector3 size, string material, string text, Color textColor, bool twoSided = false, bool collider = false)
    {
        RequireActive();
        var pivot = new GameObject(Prefix + name); pivot.transform.SetParent(parent, false);
        pivot.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yawDeg, 0f)); spawnCount++;
        string boardId = "kit.board." + material;
        if (!HasProp(boardId)) DefineProp(boardId).Box(material, Vector3.zero, Vector3.one).NoShadows();
        var board = Spawn(pivot.transform, boardId, center, yawDeg, size); board.name = "Board";
        if (collider) board.AddComponent<BoxCollider>();
        float off = size.z * 0.5f + 0.012f; var textSize = new Vector2(size.x * 0.92f, size.y * 0.86f);
        Label(pivot.transform, "Text", text, new Vector3(0f, 0f, off), Quaternion.Euler(0f, 180f, 0f), textSize, textColor);
        if (twoSided) Label(pivot.transform, "TextBack", text, new Vector3(0f, 0f, -off), Quaternion.identity, textSize, textColor);
        return pivot;
    }
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the 3D look of an item. Owned by the Items system. FROZEN CONTRACT:
///   Build(def, parent) returns a child GameObject under parent, local pos/rot zero, containing only
///   renderers (NO colliders, NO rigidbodies). Callers (LooseItem) size their own collider from the
///   renderer bounds. Visual should be roughly real-world scale (a ring is tiny, a tire is big) but
///   never smaller than ~0.12 m across so it stays grabbable/visible — exaggerate tiny things.
///
/// Implementation: derpy low-poly models assembled from cached, flat-shaded procedural meshes
/// (cube, faceted sphere/cylinder/capsule/cone, tori and lathe profiles). No primitives are created
/// per call, so there are never colliders to strip. Parts share four base materials
/// (RubbishAssets "mat/item_base|metal|glass|emissive", created by Setup_Items) and are tinted with a
/// MaterialPropertyBlock. "prefab:&lt;key&gt;" visuals copy the meshes of RubbishAssets.Prefab("item/&lt;key&gt;")
/// (asset-pack props) and fall back to a hand-built look when the prefab isn't registered.
/// The result is centered on the parent origin.
/// </summary>
public static partial class ItemVisuals
{
    /// <summary>Which shared base material a part uses.</summary>
    public enum Surface { Base, Metal, Glass, Emissive }

    /// <summary>Smallest allowed largest-dimension of a visual (m).</summary>
    public const float MinSize = 0.12f;

    /// <summary>
    /// Metal look. Fully metallic (0.9) renders almost black in the dim garage (no bright reflections),
    /// so treasure gold would read as mud; 0.6 keeps a sheen while the tint still reads.
    /// </summary>
    public const float MetalMetallic = 0.6f, MetalSmoothness = 0.6f;

    public static GameObject Build(ItemDef def, Transform parent)
    {
        var root = new GameObject("Visual_" + (def != null ? def.id : "unknown"));
        if (parent != null) root.layer = parent.gameObject.layer;
        var model = new GameObject("Model").transform;
        model.gameObject.layer = root.layer;
        model.SetParent(root.transform, false);

        var kit = new Kit(model, def != null ? def.color : new Color(0.45f, 0.4f, 0.35f));
        string key = def != null && def.visual != null ? def.visual : "";
        bool built = false;
        if (key.StartsWith("prefab:"))
        {
            string pk = key.Substring(7);
            if (pk.StartsWith("item/")) pk = pk.Substring(5);
            var prefab = RubbishAssets.Prefab("item/" + pk);
            if (prefab != null) built = CopyPrefab(prefab, model, PrefabTargetSize(pk, def));
            if (!built) built = BuildKey(kit, pk);   // hand-built fallback of the same name
        }
        else built = BuildKey(kit, key);
        if (!built) BuildFallback(kit, def);

        Normalize(root.transform, model);
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        return root;
    }

    /// <summary>
    /// Build a visual from an arbitrary prefab (copies its meshes/materials only — no colliders or scripts),
    /// scaled so its largest dimension is targetSize and centered. Useful for previews/tools.
    /// </summary>
    public static GameObject BuildFromPrefab(GameObject prefab, float targetSize, Transform parent)
    {
        var root = new GameObject("Visual_" + (prefab != null ? prefab.name : "null"));
        var model = new GameObject("Model").transform;
        model.SetParent(root.transform, false);
        if (prefab == null || !CopyPrefab(prefab, model, targetSize)) BuildFallback(new Kit(model, Color.gray), null);
        Normalize(root.transform, model);
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        return root;
    }

    // ================================================================== materials

    static readonly Material[] mats = new Material[4];
    static Material defaultMat;
    static MaterialPropertyBlock mpb;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    static readonly bool[] matIsFallback = new bool[4];
    static int fallbackRecheckFrame = -1;

    /// <summary>Drop cached materials when entering play mode without a domain reload.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        for (int i = 0; i < mats.Length; i++) { mats[i] = null; matIsFallback[i] = false; }
        fallbackRecheckFrame = -1;
    }

    static Material GetMat(Surface s)
    {
        var m = mats[(int)s];
        // A fallback is only a stopgap: look for the real material again (at most once per frame).
        if (m != null && matIsFallback[(int)s] && fallbackRecheckFrame != Time.frameCount)
        {
            fallbackRecheckFrame = Time.frameCount;
            for (int i = 0; i < mats.Length; i++) if (matIsFallback[i]) mats[i] = null;
            m = null;
        }
        if (m != null) return m;
        string key = s switch
        {
            Surface.Metal => "mat/item_metal",
            Surface.Glass => "mat/item_glass",
            Surface.Emissive => "mat/item_emissive",
            _ => "mat/item_base",
        };
        m = RubbishAssets.Material(key);
        matIsFallback[(int)s] = m == null;
        if (m == null) m = FallbackMat(s);
        mats[(int)s] = m;
        return m;
    }

    static readonly Material[] fallbackMats = new Material[4];

    static Material FallbackMat(Surface s)
    {
        var m = fallbackMats[(int)s];
        if (m == null) m = fallbackMats[(int)s] = MakeFallbackMat(s);
        return m;
    }

    /// <summary>Only used when Setup_Items hasn't registered the base materials yet (no Shader.Find).</summary>
    static Material MakeFallbackMat(Surface s)
    {
        if (defaultMat == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            defaultMat = tmp.GetComponent<Renderer>().sharedMaterial;
            Object.DestroyImmediate(tmp);
        }
        var m = new Material(defaultMat) { name = "ItemFallback_" + s, hideFlags = HideFlags.DontSave };
        m.SetColor(BaseColorId, Color.white);
        switch (s)
        {
            case Surface.Base:
                m.SetFloat("_Smoothness", 0.3f);
                break;
            case Surface.Metal:
                m.SetFloat("_Metallic", MetalMetallic);
                m.SetFloat("_Smoothness", MetalSmoothness);
                break;
            case Surface.Glass:
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_Smoothness", 0.9f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                break;
            case Surface.Emissive:
                m.EnableKeyword("_EMISSION");
                m.SetColor(EmissionId, Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                break;
        }
        return m;
    }

    // ================================================================== meshes

    static readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();

    // Lathe profiles: (radius, y) pairs from bottom to top inside a unit box (r <= 0.5, y in -0.5..0.5,
    // except "capsule" which is 2 tall like Unity's). Segments per profile keep the faceted low-poly look.
    static readonly Dictionary<string, (float[] prof, int seg, float phase)> profiles = new Dictionary<string, (float[], int, float)>
    {
        { "cube", (new[] { 0.70710678f, -0.5f, 0.70710678f, 0.5f }, 4, 45f) },
        { "cyl", (new[] { 0.5f, -0.5f, 0.5f, 0.5f }, 10, 0f) },
        { "cyl16", (new[] { 0.5f, -0.5f, 0.5f, 0.5f }, 16, 0f) },
        { "sphere", (new[] { 0f, -0.5f, 0.25f, -0.433f, 0.433f, -0.25f, 0.5f, 0f, 0.433f, 0.25f, 0.25f, 0.433f, 0f, 0.5f }, 10, 0f) },
        { "capsule", (new[] { 0f, -1f, 0.25f, -0.933f, 0.433f, -0.75f, 0.5f, -0.5f, 0.5f, 0.5f, 0.433f, 0.75f, 0.25f, 0.933f, 0f, 1f }, 10, 0f) },
        { "cone", (new[] { 0.5f, -0.5f, 0f, 0.5f }, 10, 0f) },
        { "dome", (new[] { 0.5f, -0.5f, 0.46f, -0.15f, 0.35f, 0.15f, 0.2f, 0.38f, 0f, 0.5f }, 12, 0f) },
        { "bottle", (new[] { 0.46f, -0.5f, 0.5f, -0.44f, 0.5f, 0.05f, 0.42f, 0.15f, 0.2f, 0.3f, 0.17f, 0.5f }, 10, 0f) },
        { "wine", (new[] { 0.48f, -0.5f, 0.5f, -0.46f, 0.5f, 0.12f, 0.36f, 0.22f, 0.17f, 0.3f, 0.16f, 0.5f }, 10, 0f) },
        { "jar", (new[] { 0.44f, -0.5f, 0.5f, -0.42f, 0.5f, 0.3f, 0.42f, 0.4f, 0.42f, 0.5f }, 10, 0f) },
        { "cup", (new[] { 0.36f, -0.5f, 0.5f, 0.5f }, 10, 0f) },
        { "pot", (new[] { 0.44f, -0.5f, 0.5f, -0.44f, 0.5f, 0.5f }, 12, 0f) },
        { "trophy", (new[] { 0.14f, -0.5f, 0.12f, -0.3f, 0.05f, -0.2f, 0.08f, -0.1f, 0.3f, 0.02f, 0.44f, 0.25f, 0.5f, 0.5f }, 10, 0f) },
        { "lava", (new[] { 0.3f, -0.5f, 0.5f, -0.05f, 0.38f, 0.3f, 0.26f, 0.5f }, 10, 0f) },
        { "lavabase", (new[] { 0.5f, -0.5f, 0.3f, 0.5f }, 10, 0f) },
        { "egg", (new[] { 0f, -0.5f, 0.3f, -0.43f, 0.45f, -0.26f, 0.5f, -0.06f, 0.46f, 0.14f, 0.36f, 0.31f, 0.2f, 0.45f, 0f, 0.5f }, 10, 0f) },
        { "bulb", (new[] { 0.18f, -0.5f, 0.18f, -0.28f, 0.3f, -0.12f, 0.47f, 0.08f, 0.5f, 0.25f, 0.4f, 0.42f, 0.2f, 0.5f, 0f, 0.5f }, 10, 0f) },
        { "canopy", (new[] { 0.5f, -0.5f, 0.42f, -0.2f, 0.28f, 0.1f, 0.12f, 0.35f, 0f, 0.5f }, 8, 0f) },
        { "candle", (new[] { 0.5f, -0.5f, 0.5f, -0.42f, 0.2f, -0.34f, 0.12f, -0.1f, 0.2f, 0f, 0.1f, 0.1f, 0.1f, 0.33f, 0.3f, 0.42f, 0.3f, 0.5f }, 10, 0f) },
        { "gnomebody", (new[] { 0.5f, -0.5f, 0.48f, -0.2f, 0.4f, 0.2f, 0.28f, 0.5f }, 10, 0f) },
        { "perfume", (new[] { 0.46f, -0.5f, 0.5f, -0.4f, 0.5f, 0.1f, 0.2f, 0.25f, 0.15f, 0.35f, 0.15f, 0.5f }, 8, 22.5f) },
        { "dress", (new[] { 0.5f, -0.5f, 0.42f, -0.1f, 0.22f, 0.3f, 0.18f, 0.5f }, 10, 0f) },
    };

    static Mesh GetMesh(string key)
    {
        if (meshCache.TryGetValue(key, out var m) && m != null) return m;
        if (key.StartsWith("torus"))
        {
            int pct = int.Parse(key.Substring(5));
            m = MakeTorus(pct / 100f, 16, 6);
        }
        else
        {
            if (!profiles.TryGetValue(key, out var p)) p = profiles["cube"];
            m = MakeLathe(p.prof, p.seg, p.phase);
        }
        m.name = "ItemMesh_" + key;
        m.hideFlags = HideFlags.DontSave;
        meshCache[key] = m;
        return m;
    }

    /// <summary>Surface of revolution around Y, flat shaded (every triangle gets its own vertices).</summary>
    static Mesh MakeLathe(float[] prof, int seg, float phaseDeg)
    {
        var v = new List<Vector3>();
        int n = prof.Length / 2;
        float phase = phaseDeg * Mathf.Deg2Rad;
        for (int s = 0; s < seg; s++)
        {
            float a0 = phase + s * Mathf.PI * 2f / seg, a1 = phase + (s + 1) * Mathf.PI * 2f / seg;
            float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
            for (int i = 0; i < n - 1; i++)
            {
                float r0 = prof[i * 2], y0 = prof[i * 2 + 1], r1 = prof[i * 2 + 2], y1 = prof[i * 2 + 3];
                var p00 = new Vector3(r0 * c0, y0, r0 * s0);
                var p01 = new Vector3(r0 * c1, y0, r0 * s1);
                var p10 = new Vector3(r1 * c0, y1, r1 * s0);
                var p11 = new Vector3(r1 * c1, y1, r1 * s1);
                if (r1 > 1e-4f) Tri(v, p00, p10, p11);
                if (r0 > 1e-4f) Tri(v, p00, p11, p01);
            }
            float rb = prof[0], yb = prof[1], rt = prof[prof.Length - 2], yt = prof[prof.Length - 1];
            if (rb > 1e-4f) Tri(v, new Vector3(0, yb, 0), new Vector3(rb * c0, yb, rb * s0), new Vector3(rb * c1, yb, rb * s1));
            if (rt > 1e-4f) Tri(v, new Vector3(0, yt, 0), new Vector3(rt * c1, yt, rt * s1), new Vector3(rt * c0, yt, rt * s0));
        }
        return Finish(v);
    }

    /// <summary>Torus lying in the XZ plane, outer diameter 1, tube diameter = tubeFrac.</summary>
    static Mesh MakeTorus(float tubeFrac, int major, int minor)
    {
        float r = tubeFrac * 0.5f, R = 0.5f - r;
        var v = new List<Vector3>();
        System.Func<int, int, Vector3> P = (i, j) =>
        {
            float u = i * Mathf.PI * 2f / major, w = j * Mathf.PI * 2f / minor;
            float d = R + r * Mathf.Cos(w);
            return new Vector3(d * Mathf.Cos(u), r * Mathf.Sin(w), d * Mathf.Sin(u));
        };
        for (int i = 0; i < major; i++)
            for (int j = 0; j < minor; j++)
            {
                Vector3 a = P(i, j), b = P(i + 1, j), c = P(i + 1, j + 1), d = P(i, j + 1);
                Tri(v, a, d, c);
                Tri(v, a, c, b);
            }
        return Finish(v);
    }

    static void Tri(List<Vector3> v, Vector3 a, Vector3 b, Vector3 c) { v.Add(a); v.Add(b); v.Add(c); }

    static Mesh Finish(List<Vector3> v)
    {
        var m = new Mesh();
        var idx = new int[v.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        m.SetVertices(v);
        m.SetTriangles(idx, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // ================================================================== part kit

    /// <summary>Tiny helper for assembling a model out of tinted mesh parts under one transform.</summary>
    sealed class Kit
    {
        public readonly Transform t;
        public readonly Color c;
        public Kit(Transform t, Color c) { this.t = t; this.c = c; }

        /// <summary>Sub-group (for rotated/scaled composites).</summary>
        public Kit In(Vector3 pos, Vector3 euler, float scale = 1f)
        {
            var g = new GameObject("g").transform;
            g.gameObject.layer = t.gameObject.layer;
            g.SetParent(t, false);
            g.localPosition = pos;
            g.localRotation = Quaternion.Euler(euler);
            g.localScale = Vector3.one * scale;
            return new Kit(g, c);
        }

        public Transform P(string mesh, Vector3 pos, Vector3 scale, Color col, Vector3 euler, Surface s, float glow)
        {
            var go = new GameObject(mesh);
            go.layer = t.gameObject.layer;
            var tr = go.transform;
            tr.SetParent(t, false);
            tr.localPosition = pos;
            tr.localRotation = Quaternion.Euler(euler);
            tr.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = GetMesh(mesh);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = GetMat(s);
            if (mpb == null) mpb = new MaterialPropertyBlock();
            mpb.Clear();
            if (s == Surface.Glass && col.a > 0.99f) col.a = 0.6f;
            mpb.SetColor(BaseColorId, col);
            mpb.SetColor(ColorId, col);
            if (s == Surface.Emissive) mpb.SetColor(EmissionId, col * (glow > 0f ? glow : 1.2f));
            r.SetPropertyBlock(mpb);
            if (s == Surface.Glass) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return tr;
        }

        public Transform Box(Vector3 pos, Vector3 size, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
            => P("cube", pos, size, col, euler, s, glow);
        public Transform Ball(Vector3 pos, Vector3 size, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
            => P("sphere", pos, size, col, euler, s, glow);
        public Transform Ball(Vector3 pos, float d, Color col, Surface s = Surface.Base, float glow = 0f)
            => P("sphere", pos, Vector3.one * d, col, default, s, glow);
        /// <summary>Cylinder along local Y: diameter d, height h.</summary>
        public Transform Cyl(Vector3 pos, float d, float h, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
            => P("cyl", pos, new Vector3(d, h, d), col, euler, s, glow);
        /// <summary>Capsule along local Y: diameter d, total length len.</summary>
        public Transform Cap(Vector3 pos, float d, float len, Color col, Vector3 euler = default, Surface s = Surface.Base)
            => P("capsule", pos, new Vector3(d, len * 0.5f, d), col, euler, s, 0f);
        /// <summary>Cone along local Y (point up): base diameter d, height h.</summary>
        public Transform Cone(Vector3 pos, float d, float h, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
            => P("cone", pos, new Vector3(d, h, d), col, euler, s, glow);
        /// <summary>Torus lying flat (hole along local Y): outer diameter d, tube thickness thick.</summary>
        public Transform Ring(Vector3 pos, float d, float thick, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
        {
            int pct = Mathf.Clamp(Mathf.RoundToInt(thick / Mathf.Max(d, 1e-4f) * 20f) * 5, 5, 45);
            return P("torus" + pct, pos, Vector3.one * d, col, euler, s, glow);
        }
        /// <summary>Lathe profile scaled to (diameter x, height y, diameter z).</summary>
        public Transform Lathe(string prof, Vector3 pos, Vector3 size, Color col, Vector3 euler = default, Surface s = Surface.Base, float glow = 0f)
            => P(prof, pos, size, col, euler, s, glow);
    }

    // ================================================================== layout helpers

    static Bounds Measure(Transform space)
    {
        bool any = false;
        var b = new Bounds();
        var toSpace = space.worldToLocalMatrix;
        foreach (var mf in space.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var mb = mesh.bounds;
            var m = toSpace * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    /// <summary>Enforce the minimum size and center the model on the root origin.</summary>
    static void Normalize(Transform root, Transform model)
    {
        var b = Measure(root);
        float max = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (max > 1e-4f && max < MinSize)
        {
            model.localScale *= MinSize / max;
            b = Measure(root);
        }
        model.localPosition -= b.center;
    }

    static bool CopyPrefab(GameObject prefab, Transform model, float targetSize)
    {
        var rootInv = prefab.transform.worldToLocalMatrix;
        IList<Renderer> rends;
        var lod = prefab.GetComponentInChildren<LODGroup>(true);
        if (lod != null && lod.lodCount > 0) rends = lod.GetLODs()[0].renderers;
        else rends = prefab.GetComponentsInChildren<MeshRenderer>(true);

        int count = 0;
        foreach (var r in rends)
        {
            if (r == null || !r.enabled || !(r is MeshRenderer) || !r.gameObject.activeSelf) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var go = new GameObject(r.name);
            go.layer = model.gameObject.layer;
            var tr = go.transform;
            tr.SetParent(model, false);
            var m = rootInv * r.transform.localToWorldMatrix;
            tr.localPosition = m.GetColumn(3);
            tr.localRotation = m.rotation;
            tr.localScale = m.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
            count++;
        }
        if (count == 0) return false;

        var b = Measure(model);
        float max = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (max > 1e-4f) model.localScale = Vector3.one * (targetSize / max);
        return true;
    }

    /// <summary>Largest dimension (m) a pack prop ("prefab:&lt;key&gt;" visual) gets scaled to.</summary>
    public static float PrefabTargetSize(string key, ItemDef def)
    {
        switch (key)
        {
            case "tire": return 0.62f;
            case "car_battery": return 0.3f;
            case "paint_bucket":
            case "paint_bucket_2":
            case "paint_bucket_3": return 0.24f;
            case "jerrycan": return 0.34f;
            case "propane": return 0.36f;
            case "extinguisher": return 0.45f;
            case "toolbox": return 0.45f;
            case "hammer": return 0.32f;
            case "lemon": return 0.13f;
            case "duct_tape": return 0.15f;
            case "drill": return 0.28f;
            case "spray_bottle": return 0.26f;
            case "box_small": return 0.34f;
        }
        float kg = def != null ? Mathf.Max(def.maxMass, 0.01f) : 1f;
        return Mathf.Clamp(Mathf.Pow(kg, 1f / 3f) * 0.3f, 0.15f, 0.7f);
    }

    // ================================================================== palette

    static Color H(int hex) => new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);
    static Color Mul(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);
    static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, t);

    static readonly Color White = H(0xF4F2EC), OffWhite = H(0xE6E1D2), Black = H(0x1C1C20), DarkGrey = H(0x3A3A40),
        Grey = H(0x8A8C92), Silver = H(0xC8CCD2), Gold = H(0xFFC24A), Copper = H(0xC87533), Red = H(0xE0332E),
        Orange = H(0xFF8A1F), Yellow = H(0xFFD83A), Green = H(0x4CB84A), DarkGreen = H(0x2E6A2A), Blue = H(0x3C78D8),
        SkyBlue = H(0x9CD2FF), Pink = H(0xFF8FB1), Purple = H(0x8A4AD8), Brown = H(0x6B4423), Tan = H(0xC4935A),
        Skin = H(0xF2C9A0), Mold = H(0x9ACB3A);
    static readonly Color Clear = new Color(0.9f, 0.97f, 1f, 0.28f);

    // ================================================================== builders

    static void BuildFallback(Kit k, ItemDef def)
    {
        var cat = def != null ? def.category : ItemCategory.Junk;
        switch (cat)
        {
            case ItemCategory.Plastic: BuildKey(k, "bottle"); return;
            case ItemCategory.Glass: BuildKey(k, "jar"); return;
            case ItemCategory.Metal: BuildKey(k, "can"); return;
            case ItemCategory.Paper: BuildKey(k, "box"); return;
            case ItemCategory.Hazard: BuildKey(k, "battery"); return;
            case ItemCategory.Valuable: BuildKey(k, "gift"); return;
            default: BuildKey(k, "blob"); return;
        }
    }

    static bool BuildKey(Kit k, string key)
    {
        if (BuildCollectible(k, key)) return true;
        if (BuildPart(k, key)) return true;
        if (BuildStory(k, key)) return true;
        Color c = k.c;
        switch (key)
        {
            // ------------------------------------------------------------ F4 night shift (shop pet, the King's crown)
            case "critter_raccoon":
                k.Ball(new Vector3(0, 0.1f, -0.02f), new Vector3(0.26f, 0.2f, 0.32f), c);                     // body
                k.Ball(new Vector3(0, 0.08f, 0.02f), new Vector3(0.18f, 0.12f, 0.2f), H(0xDAD6CC));          // belly
                k.Ball(new Vector3(0, 0.2f, 0.16f), new Vector3(0.2f, 0.17f, 0.17f), c);                     // head
                k.Ball(new Vector3(0, 0.215f, 0.215f), new Vector3(0.19f, 0.055f, 0.08f), Black);           // bandit mask
                k.Ball(new Vector3(-0.045f, 0.225f, 0.245f), 0.045f, White);
                k.Ball(new Vector3(0.045f, 0.225f, 0.245f), 0.045f, White);
                k.Ball(new Vector3(-0.045f, 0.225f, 0.266f), 0.02f, Black);
                k.Ball(new Vector3(0.045f, 0.225f, 0.266f), 0.02f, Black);
                k.Ball(new Vector3(0, 0.18f, 0.25f), 0.035f, Black);                                          // nose
                k.Ball(new Vector3(-0.07f, 0.3f, 0.14f), new Vector3(0.06f, 0.07f, 0.03f), c);               // ears
                k.Ball(new Vector3(0.07f, 0.3f, 0.14f), new Vector3(0.06f, 0.07f, 0.03f), c);
                for (int i = 0; i < 4; i++)                                                                   // ringed tail
                    k.Ball(new Vector3(0.02f * i, 0.12f + 0.04f * i, -0.2f - 0.05f * i), 0.09f - 0.01f * i, i % 2 == 0 ? Mul(c, 0.55f) : H(0xD8D4CA));
                return true;
            case "critter_crown":
                k.Cyl(Vector3.zero, 0.24f, 0.06f, c, default, Surface.Metal);
                for (int i = 0; i < 5; i++)
                {
                    float a = i * 72f * Mathf.Deg2Rad;
                    k.Box(new Vector3(Mathf.Cos(a) * 0.1f, 0.07f, Mathf.Sin(a) * 0.1f), new Vector3(0.04f, 0.09f, 0.04f), c, new Vector3(0f, -i * 72f, 45f), Surface.Metal);
                }
                k.Ball(new Vector3(0f, 0.02f, 0.12f), 0.05f, Red, Surface.Emissive, 0.8f);
                return true;
            // ------------------------------------------------------------ generic
            case "blob":
                k.Ball(new Vector3(0, 0.05f, 0), new Vector3(0.16f, 0.1f, 0.14f), c);
                k.Ball(new Vector3(0.05f, 0.08f, 0.02f), 0.09f, Mul(c, 0.85f));
                k.Ball(new Vector3(-0.05f, 0.07f, -0.03f), 0.08f, Mul(c, 1.1f));
                return true;
            case "flat":
                k.Box(Vector3.zero, new Vector3(0.3f, 0.03f, 0.3f), c);
                return true;
            case "gift":
                k.Box(Vector3.zero, new Vector3(0.16f, 0.14f, 0.16f), c);
                k.Box(Vector3.zero, new Vector3(0.165f, 0.145f, 0.035f), Red);
                k.Box(Vector3.zero, new Vector3(0.035f, 0.145f, 0.165f), Red);
                k.Ball(new Vector3(-0.025f, 0.085f, 0), new Vector3(0.05f, 0.03f, 0.03f), Red);
                k.Ball(new Vector3(0.025f, 0.085f, 0), new Vector3(0.05f, 0.03f, 0.03f), Red);
                return true;

            // ------------------------------------------------------------ junk
            case "peel":
                k.Cyl(new Vector3(0, 0.075f, 0), 0.03f, 0.05f, Brown);
                k.Ball(new Vector3(0, 0.045f, 0), new Vector3(0.07f, 0.05f, 0.07f), Mix(c, White, 0.55f));
                for (int i = 0; i < 4; i++)
                {
                    float yaw = i * 90f + 20f + (i % 2) * 14f;
                    var dir = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                    // floppy flap: flat capsule leaning outward and drooping, top tucked under the stem
                    k.P("capsule", dir * 0.06f + new Vector3(0, 0.015f, 0), new Vector3(0.022f, 0.08f, 0.055f), c,
                        new Vector3(0, yaw, 50f + i * 5f), Surface.Base, 0f);
                    k.Ball(dir * 0.12f + new Vector3(0, -0.012f, 0), 0.022f, Brown);
                    k.Ball(dir * 0.045f + new Vector3(0, 0.03f, 0), new Vector3(0.03f, 0.02f, 0.03f), Mix(c, White, 0.55f));
                }
                return true;
            case "pizzabox":
                k.Box(Vector3.zero, new Vector3(0.36f, 0.05f, 0.36f), c);
                k.Box(new Vector3(0, 0.026f, 0), new Vector3(0.3f, 0.004f, 0.3f), Mul(c, 1.12f));
                k.Cyl(new Vector3(0.02f, 0.03f, 0.02f), 0.13f, 0.005f, Red);
                k.Ball(new Vector3(-0.1f, 0.027f, -0.09f), new Vector3(0.09f, 0.006f, 0.07f), H(0x7A5020));
                k.Ball(new Vector3(0.11f, 0.027f, 0.1f), new Vector3(0.06f, 0.006f, 0.05f), H(0x7A5020));
                k.Ball(new Vector3(0.12f, 0.028f, -0.11f), new Vector3(0.04f, 0.006f, 0.04f), H(0x7A5020));
                return true;
            case "diaper":
                k.Ball(Vector3.zero, new Vector3(0.22f, 0.12f, 0.17f), c);
                k.Ball(new Vector3(-0.035f, 0.045f, 0), new Vector3(0.13f, 0.08f, 0.15f), Mul(c, 0.96f));
                k.Box(new Vector3(-0.04f, 0.075f, 0), new Vector3(0.1f, 0.012f, 0.16f), SkyBlue);
                k.Box(new Vector3(-0.07f, 0.06f, 0.085f), new Vector3(0.05f, 0.03f, 0.02f), H(0x5AB0F0), new Vector3(0, 0, -20f));
                k.Box(new Vector3(-0.07f, 0.06f, -0.085f), new Vector3(0.05f, 0.03f, 0.02f), H(0x5AB0F0), new Vector3(0, 0, -20f));
                k.Ball(new Vector3(0.02f, 0.085f, 0.03f), 0.022f, Yellow);
                k.Ball(new Vector3(-0.07f, 0.085f, -0.03f), 0.022f, Pink);
                k.Ball(new Vector3(0.07f, 0.03f, 0.0f), new Vector3(0.05f, 0.04f, 0.07f), H(0xE8C860));
                return true;
            case "chips":
            {
                var cb = k.In(Vector3.zero, new Vector3(-90f, 0, 0));   // lying flat, label up
                cb.Box(Vector3.zero, new Vector3(0.19f, 0.24f, 0.035f), c);
                cb.Ball(Vector3.zero, new Vector3(0.19f, 0.22f, 0.07f), c);
                cb.Box(new Vector3(0, 0.125f, 0), new Vector3(0.2f, 0.025f, 0.012f), Silver, default, Surface.Metal);
                cb.Box(new Vector3(0, -0.125f, 0), new Vector3(0.2f, 0.025f, 0.012f), Silver, default, Surface.Metal);
                for (int side = 0; side < 2; side++)
                {
                    var f = cb.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Ball(new Vector3(0, 0.03f, 0.033f), new Vector3(0.14f, 0.07f, 0.012f), Yellow);
                    f.Box(new Vector3(0, 0.03f, 0.04f), new Vector3(0.1f, 0.02f, 0.004f), Red);
                    f.Ball(new Vector3(0.02f, -0.05f, 0.033f), new Vector3(0.06f, 0.05f, 0.012f), H(0xF2C050));
                    f.Ball(new Vector3(-0.03f, -0.06f, 0.031f), new Vector3(0.05f, 0.04f, 0.012f), H(0xE8B040));
                }
                return true;
            }
            case "grounds":
                k.Lathe("cup", Vector3.zero, new Vector3(0.14f, 0.09f, 0.14f), OffWhite, new Vector3(0, 0, 180f));
                k.Ball(new Vector3(0, 0.045f, 0), new Vector3(0.13f, 0.06f, 0.13f), c);
                k.Ball(new Vector3(0.03f, 0.065f, 0.01f), 0.04f, Mul(c, 0.8f));
                return true;
            case "umbrella":
            {
                var u = k.In(Vector3.zero, new Vector3(0, 0, 25f));
                u.Lathe("canopy", new Vector3(0, 0.1f, 0), new Vector3(0.55f, 0.2f, 0.55f), c);
                u.Cyl(new Vector3(0, 0.02f, 0), 0.016f, 0.52f, Silver, default, Surface.Metal);
                u.Ring(new Vector3(0.03f, -0.25f, 0), 0.08f, 0.018f, Brown, new Vector3(90f, 0, 0));
                u.Cyl(new Vector3(0.2f, 0.06f, 0.12f), 0.008f, 0.3f, Silver, new Vector3(20f, 0, -70f), Surface.Metal);
                u.Cyl(new Vector3(-0.18f, 0.12f, -0.1f), 0.008f, 0.26f, Silver, new Vector3(-40f, 0, 50f), Surface.Metal);
                return true;
            }
            case "sock":
                k.Cap(new Vector3(0, 0.08f, 0), 0.075f, 0.22f, c);
                k.Cap(new Vector3(0.05f, -0.03f, 0), 0.075f, 0.16f, c, new Vector3(0, 0, 90f));
                k.Ball(new Vector3(0.12f, -0.03f, 0), 0.07f, Mul(c, 0.75f));
                k.Ball(new Vector3(-0.005f, -0.035f, 0), 0.07f, Mul(c, 0.75f));
                k.Ring(new Vector3(0, 0.15f, 0), 0.085f, 0.02f, Red);
                k.Ring(new Vector3(0, 0.12f, 0), 0.085f, 0.02f, Blue);
                return true;
            case "leftovers":
                k.Box(Vector3.zero, new Vector3(0.17f, 0.08f, 0.13f), Clear, default, Surface.Glass);
                k.Box(new Vector3(0, -0.01f, 0), new Vector3(0.15f, 0.055f, 0.11f), H(0x8A5A2A));
                k.Box(new Vector3(0, 0.048f, 0), new Vector3(0.18f, 0.018f, 0.14f), H(0x2F8AD8));
                k.Ball(new Vector3(0.04f, 0.02f, 0.02f), 0.035f, c);
                k.Ball(new Vector3(-0.03f, 0.02f, -0.02f), 0.03f, Mold);
                k.Ball(new Vector3(0.05f, 0.064f, -0.03f), 0.035f, Mold);
                k.Ball(new Vector3(0.025f, 0.066f, -0.045f), 0.022f, c);
                return true;
            case "bones":
                k.Cap(Vector3.zero, 0.018f, 0.26f, c, new Vector3(0, 0, 90f));
                k.Cone(new Vector3(-0.15f, 0, 0), 0.09f, 0.09f, c, new Vector3(0, 0, 90f));
                k.Ball(new Vector3(-0.16f, 0.018f, 0.026f), 0.022f, Black);
                k.Ball(new Vector3(-0.16f, 0.018f, -0.026f), 0.022f, Black);
                k.Box(new Vector3(0.14f, 0.03f, 0), new Vector3(0.012f, 0.06f, 0.05f), c, new Vector3(0, 0, -30f));
                k.Box(new Vector3(0.14f, -0.03f, 0), new Vector3(0.012f, 0.06f, 0.05f), c, new Vector3(0, 0, 30f));
                for (int i = 0; i < 5; i++)
                    k.Cap(new Vector3(-0.08f + i * 0.04f, 0, 0), 0.01f, 0.1f - i * 0.01f, c, new Vector3(0, 0, -8f));
                return true;
            case "toothbrush":
                k.Cap(Vector3.zero, 0.022f, 0.19f, c, new Vector3(0, 0, 90f));
                k.Box(new Vector3(0.08f, 0.012f, 0), new Vector3(0.035f, 0.012f, 0.018f), c);
                k.Box(new Vector3(0.075f, 0.03f, -0.004f), new Vector3(0.02f, 0.03f, 0.016f), White, new Vector3(20f, 0, -25f));
                k.Box(new Vector3(0.092f, 0.03f, 0.004f), new Vector3(0.02f, 0.03f, 0.016f), SkyBlue, new Vector3(-25f, 0, 30f));
                return true;
            case "litter":
                k.Ball(Vector3.zero, new Vector3(0.14f, 0.1f, 0.12f), c);
                k.Ball(new Vector3(0.06f, 0.02f, 0.03f), 0.09f, Mul(c, 0.9f));
                k.Ball(new Vector3(-0.05f, 0.03f, -0.03f), 0.08f, Mul(c, 1.05f));
                k.Ball(new Vector3(0.01f, 0.05f, -0.04f), 0.07f, Mul(c, 0.95f));
                k.Ball(new Vector3(0.03f, 0.07f, 0.02f), 0.02f, H(0x5A4A30));
                k.Ball(new Vector3(-0.04f, 0.065f, 0.01f), 0.018f, H(0x5A4A30));
                return true;
            case "toy":
                k.Box(new Vector3(0, 0.04f, 0), new Vector3(0.06f, 0.08f, 0.04f), c);
                k.Ball(new Vector3(0, 0.1f, 0), 0.045f, Skin);
                k.Box(new Vector3(0, 0.118f, 0), new Vector3(0.048f, 0.014f, 0.048f), Black);
                k.Ball(new Vector3(0.012f, 0.104f, 0.02f), 0.01f, Black);
                k.Ball(new Vector3(-0.012f, 0.104f, 0.02f), 0.01f, Black);
                k.Cap(new Vector3(0.045f, 0.045f, 0), 0.022f, 0.07f, Skin, new Vector3(0, 0, 20f));
                k.Cap(new Vector3(0.016f, -0.04f, 0), 0.025f, 0.08f, Mul(c, 0.6f));
                k.Cap(new Vector3(-0.03f, -0.035f, 0.01f), 0.025f, 0.08f, Mul(c, 0.6f), new Vector3(35f, 0, -30f));
                k.Cyl(new Vector3(-0.032f, 0.06f, 0), 0.02f, 0.01f, White, new Vector3(0, 0, 90f));
                return true;
            case "tissue":
                k.Ball(Vector3.zero, new Vector3(0.1f, 0.08f, 0.09f), c, new Vector3(10, 30, 0));
                k.Ball(new Vector3(0.05f, 0.01f, 0.02f), new Vector3(0.08f, 0.07f, 0.08f), Mul(c, 0.95f), new Vector3(0, 60, 20));
                k.Ball(new Vector3(-0.03f, 0.03f, -0.03f), new Vector3(0.07f, 0.06f, 0.06f), Mul(c, 0.97f), new Vector3(30, 0, 0));
                k.Ball(new Vector3(0.01f, 0.045f, 0.03f), 0.05f, H(0xE6EEC8));
                return true;
            case "core":
                k.Ball(new Vector3(0, -0.035f, 0), new Vector3(0.075f, 0.035f, 0.075f), c);
                k.Ball(new Vector3(0, 0.035f, 0), new Vector3(0.07f, 0.035f, 0.07f), c);
                k.Cyl(Vector3.zero, 0.035f, 0.07f, H(0xF2E4A8));
                k.Ball(new Vector3(0.012f, 0, 0.012f), 0.012f, Brown);
                k.Cyl(new Vector3(0, 0.065f, 0), 0.008f, 0.03f, Brown, new Vector3(0, 0, 15f));
                k.Ball(new Vector3(0.018f, 0.07f, 0), new Vector3(0.03f, 0.006f, 0.016f), Green, new Vector3(0, 0, 20f));
                return true;
            case "toast":
                k.Box(Vector3.zero, new Vector3(0.12f, 0.02f, 0.11f), c);
                k.Cyl(new Vector3(0, 0, 0.055f), 0.12f, 0.02f, c, new Vector3(0, 0, 0));
                k.Ball(new Vector3(0.02f, 0.011f, 0.01f), new Vector3(0.05f, 0.004f, 0.04f), H(0x6B4020));
                k.Ball(new Vector3(-0.03f, 0.011f, -0.03f), new Vector3(0.03f, 0.004f, 0.03f), H(0x3A2A1A));
                return true;
            case "shoe":
                k.Box(new Vector3(0, -0.028f, 0), new Vector3(0.11f, 0.02f, 0.26f), Mul(c, 0.8f));
                k.Ball(Vector3.zero, new Vector3(0.11f, 0.08f, 0.26f), c);
                k.Ball(new Vector3(0, 0.03f, -0.05f), new Vector3(0.075f, 0.03f, 0.11f), Mul(c, 0.4f));
                k.Ring(new Vector3(0, 0.0f, -0.08f), 0.12f, 0.016f, Mul(c, 0.85f), new Vector3(0, 0, 90f));
                for (int i = 0; i < 5; i++)
                    k.Ball(new Vector3(-0.03f + (i % 3) * 0.03f, 0.036f, 0.05f + (i / 3) * 0.035f), 0.014f, Mul(c, 0.45f));
                return true;
            case "vhs":
                k.Box(Vector3.zero, new Vector3(0.19f, 0.026f, 0.105f), c);
                k.Box(new Vector3(0, 0.014f, 0.01f), new Vector3(0.12f, 0.004f, 0.05f), OffWhite);
                k.Cyl(new Vector3(-0.045f, 0.014f, 0.01f), 0.035f, 0.005f, DarkGrey);
                k.Cyl(new Vector3(0.045f, 0.014f, 0.01f), 0.035f, 0.005f, DarkGrey);
                k.Box(new Vector3(0.1f, -0.02f, -0.07f), new Vector3(0.012f, 0.004f, 0.16f), H(0x4A3020), new Vector3(0, 35f, 40f));
                k.Box(new Vector3(-0.08f, -0.018f, -0.08f), new Vector3(0.01f, 0.004f, 0.12f), H(0x4A3020), new Vector3(10f, -50f, -20f));
                return true;
            case "clippings":
                k.Ball(Vector3.zero, new Vector3(0.3f, 0.16f, 0.26f), c);
                k.Ball(new Vector3(0.08f, 0.05f, 0.04f), 0.14f, Mul(c, 0.85f));
                k.Ball(new Vector3(-0.08f, 0.06f, -0.03f), 0.13f, Mul(c, 1.1f));
                k.Ball(new Vector3(-0.02f, 0.09f, 0.06f), 0.1f, Mix(c, Yellow, 0.3f));
                for (int i = 0; i < 6; i++)
                    k.Cap(new Vector3(-0.1f + i * 0.04f, 0.1f, (i % 2 == 0 ? 0.03f : -0.04f)), 0.012f, 0.1f, Mul(c, 1.2f), new Vector3(i * 23f - 50f, i * 40f, i * 17f - 40f));
                return true;
            case "towel":
                k.Box(Vector3.zero, new Vector3(0.3f, 0.07f, 0.2f), c);
                k.Box(new Vector3(0.09f, 0, 0), new Vector3(0.025f, 0.072f, 0.202f), White);
                k.Box(new Vector3(-0.09f, 0, 0), new Vector3(0.025f, 0.072f, 0.202f), White);
                k.Box(new Vector3(0.18f, -0.03f, 0), new Vector3(0.1f, 0.018f, 0.2f), c, new Vector3(0, 0, -35f));
                k.Ball(new Vector3(-0.05f, 0.037f, 0.05f), new Vector3(0.06f, 0.006f, 0.05f), Mul(c, 0.7f));
                return true;
            case "lemon":
                k.Ball(Vector3.zero, new Vector3(0.12f, 0.085f, 0.085f), c);
                k.Cone(new Vector3(0.068f, 0, 0), 0.03f, 0.025f, c, new Vector3(0, 0, -90f));
                k.Cone(new Vector3(-0.068f, 0, 0), 0.03f, 0.025f, c, new Vector3(0, 0, 90f));
                k.Ball(new Vector3(0.02f, 0.035f, 0.01f), new Vector3(0.05f, 0.02f, 0.045f), Mold);
                k.Ball(new Vector3(-0.025f, 0.02f, 0.03f), new Vector3(0.03f, 0.02f, 0.03f), H(0x6A9A3A));
                return true;
            case "tire":
                k.Ring(Vector3.zero, 0.62f, 0.2f, c, new Vector3(90f, 0, 0));
                k.Cyl(Vector3.zero, 0.3f, 0.16f, Silver, new Vector3(90f, 0, 0), Surface.Metal);
                k.Cyl(Vector3.zero, 0.08f, 0.18f, DarkGrey, new Vector3(90f, 0, 0), Surface.Metal);
                return true;
            case "partyhat":
            {
                var h = k.In(Vector3.zero, new Vector3(0, 0, 25f));
                h.Cone(Vector3.zero, 0.12f, 0.18f, c);
                h.Ring(new Vector3(0, -0.04f, 0), 0.1f, 0.012f, Yellow);
                h.Ring(new Vector3(0, 0.01f, 0), 0.07f, 0.012f, SkyBlue);
                h.Ball(new Vector3(0, 0.095f, 0), 0.04f, Yellow);
                return true;
            }
            case "mug":
                k.Cyl(Vector3.zero, 0.09f, 0.1f, c);
                k.Cyl(new Vector3(0, 0.049f, 0), 0.075f, 0.004f, H(0x5A3A20));
                k.Ring(new Vector3(0.055f, 0, 0), 0.07f, 0.016f, c, new Vector3(90f, 0, 0));
                k.Cyl(new Vector3(0, 0.02f, 0), 0.092f, 0.025f, Red);
                k.Box(new Vector3(0, -0.02f, 0.045f), new Vector3(0.004f, 0.06f, 0.004f), Black, new Vector3(0, 0, 25f));
                k.Box(new Vector3(-0.035f, 0.045f, 0.03f), new Vector3(0.03f, 0.02f, 0.02f), c, new Vector3(30, 10, 40));
                return true;
            case "sponge":
                k.Box(Vector3.zero, new Vector3(0.11f, 0.04f, 0.07f), c);
                k.Box(new Vector3(0, 0.027f, 0), new Vector3(0.11f, 0.015f, 0.07f), H(0x2E8A3A));
                k.Ball(new Vector3(0.02f, 0.036f, 0.01f), 0.018f, H(0x6A4A20));
                k.Ball(new Vector3(-0.03f, 0.036f, -0.015f), 0.014f, H(0x8A6A30));
                return true;
            case "wipes":
                k.Box(Vector3.zero, new Vector3(0.2f, 0.05f, 0.12f), c);
                k.Box(new Vector3(0, 0.026f, 0), new Vector3(0.08f, 0.004f, 0.04f), White);
                k.Ball(new Vector3(0, 0.045f, 0), new Vector3(0.09f, 0.05f, 0.07f), White);
                k.Ball(new Vector3(0.05f, 0.05f, 0.02f), new Vector3(0.06f, 0.04f, 0.05f), OffWhite);
                return true;
            case "baggie":
                k.Ball(Vector3.zero, new Vector3(0.11f, 0.1f, 0.1f), c);
                k.Ball(new Vector3(0, 0.055f, 0), 0.035f, c);
                k.Cap(new Vector3(0.025f, 0.08f, 0), 0.022f, 0.06f, c, new Vector3(0, 0, -45f));
                k.Cap(new Vector3(-0.025f, 0.08f, 0), 0.022f, 0.06f, c, new Vector3(0, 0, 45f));
                return true;
            case "sludge":
                k.Lathe("cup", Vector3.zero, new Vector3(0.2f, 0.14f, 0.2f), H(0xE8E4DA), new Vector3(0, 0, 180f));
                k.Ball(new Vector3(0, 0.07f, 0), new Vector3(0.2f, 0.07f, 0.2f), c);
                k.Ball(new Vector3(0.1f, 0.02f, 0.02f), new Vector3(0.05f, 0.1f, 0.05f), c);
                k.Ball(new Vector3(-0.03f, 0.1f, 0.02f), 0.035f, Mold, Surface.Emissive, 0.25f);
                k.Ball(new Vector3(0.04f, 0.1f, -0.04f), 0.025f, Mold, Surface.Emissive, 0.25f);
                return true;
            case "pumpkin":
                for (int i = 0; i < 6; i++)
                {
                    var p = Quaternion.Euler(0, i * 60f, 0) * new Vector3(0.06f, 0, 0);
                    k.Ball(p, new Vector3(0.14f, 0.2f, 0.14f), i % 2 == 0 ? c : Mul(c, 0.88f), new Vector3(0, -i * 60f, 0));
                }
                k.Ball(new Vector3(0, -0.02f, 0), new Vector3(0.26f, 0.12f, 0.26f), Mul(c, 0.8f));
                k.Cyl(new Vector3(0, 0.105f, 0), 0.03f, 0.05f, H(0x5A6A2A), new Vector3(0, 0, 12f));
                // droopy jack-o-lantern face
                k.Cone(new Vector3(0.045f, 0.035f, 0.118f), 0.04f, 0.035f, Black, new Vector3(90f, 0, 0));
                k.Cone(new Vector3(-0.045f, 0.03f, 0.118f), 0.04f, 0.035f, Black, new Vector3(90f, 0, 0));
                k.Ball(new Vector3(0, -0.03f, 0.118f), new Vector3(0.1f, 0.03f, 0.02f), Black, new Vector3(0, 0, -8f));
                k.Ball(new Vector3(0.06f, -0.07f, 0.09f), new Vector3(0.06f, 0.03f, 0.05f), Mold);
                return true;
            case "flowerpot":
                k.Lathe("cup", new Vector3(0.02f, 0, 0), new Vector3(0.18f, 0.16f, 0.18f), c, new Vector3(0, 0, 8f));
                k.Ball(new Vector3(0.02f, 0.075f, 0), new Vector3(0.16f, 0.03f, 0.16f), Brown);
                k.Cyl(new Vector3(0.02f, 0.1f, 0), 0.01f, 0.08f, H(0x6A5A2A), new Vector3(0, 0, 50f));
                k.Ball(new Vector3(-0.02f, 0.12f, 0), new Vector3(0.05f, 0.01f, 0.025f), H(0x8A7A3A), new Vector3(0, 0, -30f));
                k.Box(new Vector3(-0.12f, -0.06f, 0.03f), new Vector3(0.08f, 0.012f, 0.06f), c, new Vector3(0, 30f, 10f));
                k.Box(new Vector3(-0.1f, -0.065f, -0.06f), new Vector3(0.05f, 0.012f, 0.04f), c, new Vector3(0, -20f, -5f));
                return true;
            case "peels":
                for (int i = 0; i < 9; i++)
                {
                    var p = new Vector3(((i * 37) % 9 - 4) * 0.02f, (i % 3) * 0.018f, ((i * 53) % 7 - 3) * 0.022f);
                    k.Ball(p, new Vector3(0.09f, 0.018f, 0.04f), i % 3 == 0 ? Mul(c, 0.8f) : c, new Vector3(i * 17f, i * 47f, i * 29f));
                }
                k.Ball(new Vector3(0, 0.035f, 0), new Vector3(0.05f, 0.03f, 0.04f), H(0xF2E6B0));
                return true;
            case "duct_tape":
                k.Ring(Vector3.zero, 0.14f, 0.035f, c);
                k.Ring(Vector3.zero, 0.09f, 0.012f, Tan);
                k.Box(new Vector3(0.07f, -0.01f, 0.03f), new Vector3(0.05f, 0.002f, 0.045f), c, new Vector3(0, 30f, -30f));
                return true;

            // ------------------------------------------------------------ plastic
            case "bottle":
                k.Lathe("bottle", Vector3.zero, new Vector3(0.075f, 0.24f, 0.075f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.078f, 0.07f, Mix(c, White, 0.6f));
                k.Cyl(new Vector3(0, 0.125f, 0), 0.03f, 0.018f, Mix(c, Blue, 0.5f));
                return true;
            case "bottle2l":
                k.Lathe("bottle", Vector3.zero, new Vector3(0.11f, 0.33f, 0.11f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, 0, 0), 0.114f, 0.09f, Red);
                k.Cyl(new Vector3(0, 0, 0), 0.115f, 0.03f, White);
                k.Cyl(new Vector3(0, 0.172f, 0), 0.04f, 0.022f, Red);
                return true;
            case "jug":
                k.Box(Vector3.zero, new Vector3(0.14f, 0.2f, 0.14f), c);
                k.Box(new Vector3(0, 0.1f, 0), new Vector3(0.12f, 0.02f, 0.12f), Mul(c, 0.95f));
                k.Cyl(new Vector3(-0.03f, 0.125f, -0.03f), 0.045f, 0.04f, c);
                k.Cyl(new Vector3(-0.03f, 0.15f, -0.03f), 0.05f, 0.02f, Mix(c, Blue, 0.5f));
                k.Ring(new Vector3(0.04f, 0.08f, 0.04f), 0.09f, 0.025f, c, new Vector3(90f, -45f, 0));
                k.Box(new Vector3(0, -0.01f, 0.071f), new Vector3(0.1f, 0.08f, 0.004f), Mix(c, Red, 0.4f));
                return true;
            case "tub":
                k.Lathe("cup", Vector3.zero, new Vector3(0.1f, 0.085f, 0.1f), c);
                k.Cyl(new Vector3(0, 0.044f, 0), 0.104f, 0.006f, Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.005f, 0), 0.094f, 0.035f, Pink);
                return true;
            case "cups":
                for (int i = 0; i < 4; i++)
                {
                    k.Lathe("cup", new Vector3(0, i * 0.025f, 0), new Vector3(0.095f, 0.12f, 0.095f), c, new Vector3(0, i * 20f, i * 2f));
                    k.Ring(new Vector3(0, i * 0.025f + 0.058f, 0), 0.1f, 0.008f, White, new Vector3(0, 0, i * 2f));
                }
                return true;
            case "shampoo":
                k.P("capsule", Vector3.zero, new Vector3(0.08f, 0.09f, 0.045f), c, default, Surface.Base, 0);
                k.Cyl(new Vector3(0, 0.095f, 0), 0.04f, 0.03f, White);
                k.Box(new Vector3(0, -0.01f, 0.02f), new Vector3(0.055f, 0.07f, 0.01f), White);
                k.Ball(new Vector3(0, -0.005f, 0.026f), 0.025f, Mul(c, 0.8f));
                return true;
            case "babybottle":
                k.Cyl(Vector3.zero, 0.06f, 0.13f, Clear, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.05f, 0.08f, H(0xFFFBF0));
                k.Cyl(new Vector3(0, 0.07f, 0), 0.064f, 0.02f, c);
                k.Lathe("dome", new Vector3(0, 0.09f, 0), new Vector3(0.035f, 0.035f, 0.035f), H(0xF0C890));
                return true;
            case "duck":
                k.Ball(Vector3.zero, new Vector3(0.12f, 0.09f, 0.15f), c);
                k.Ball(new Vector3(0, 0.065f, 0.045f), 0.075f, c);
                k.Ball(new Vector3(0, 0.058f, 0.09f), new Vector3(0.045f, 0.015f, 0.04f), Orange);
                k.Ball(new Vector3(0.022f, 0.08f, 0.075f), 0.016f, Black);
                k.Ball(new Vector3(-0.022f, 0.08f, 0.075f), 0.016f, Black);
                k.Cone(new Vector3(0, 0.02f, -0.075f), 0.04f, 0.05f, c, new Vector3(-50f, 0, 0));
                return true;
            case "clamshell":
                k.Box(Vector3.zero, new Vector3(0.15f, 0.035f, 0.15f), c);
                k.Box(new Vector3(0, 0.05f, -0.1f), new Vector3(0.15f, 0.035f, 0.15f), c, new Vector3(-55f, 0, 0));
                k.Box(new Vector3(0.02f, 0.024f, 0.02f), new Vector3(0.1f, 0.012f, 0.012f), Yellow, new Vector3(0, 25f, 0));
                return true;
            case "bagofbags":
                k.Ball(Vector3.zero, new Vector3(0.22f, 0.19f, 0.2f), c, default, Surface.Glass);
                k.Ball(new Vector3(0.03f, -0.01f, 0), 0.12f, H(0x3C78D8));
                k.Ball(new Vector3(-0.04f, 0.01f, 0.02f), 0.1f, H(0xF2F2F2));
                k.Ball(new Vector3(0.0f, 0.03f, -0.04f), 0.09f, Yellow);
                k.Ring(new Vector3(-0.04f, 0.11f, 0), 0.07f, 0.012f, c, new Vector3(90f, 0, 0));
                k.Ring(new Vector3(0.04f, 0.11f, 0), 0.07f, 0.012f, c, new Vector3(90f, 0, 0));
                return true;

            // ------------------------------------------------------------ glass
            case "wine":
                k.Lathe("wine", Vector3.zero, new Vector3(0.08f, 0.31f, 0.08f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.04f, 0), 0.082f, 0.08f, H(0xF2E8D0));
                k.Box(new Vector3(0, -0.04f, 0.04f), new Vector3(0.03f, 0.03f, 0.004f), H(0x8A1A2A));
                k.Cyl(new Vector3(0, 0.135f, 0), 0.03f, 0.045f, H(0x8A1A2A), default, Surface.Metal);
                return true;
            case "jar":
                k.Lathe("jar", Vector3.zero, new Vector3(0.1f, 0.13f, 0.1f), Clear, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.085f, 0.07f, c);
                k.Cyl(new Vector3(0, 0.068f, 0), 0.088f, 0.018f, Gold, default, Surface.Metal);
                k.Cap(new Vector3(0.01f, 0.01f, 0), 0.022f, 0.06f, Mul(c, 1.2f), new Vector3(0, 0, 20f));
                return true;
            case "jarsmall":
                k.Lathe("jar", Vector3.zero, new Vector3(0.07f, 0.075f, 0.07f), Clear, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.012f, 0), 0.06f, 0.045f, c);
                k.Cyl(new Vector3(0, 0.04f, 0), 0.066f, 0.014f, H(0x3C78D8), default, Surface.Metal);
                k.Cyl(new Vector3(0, 0, 0), 0.072f, 0.03f, White);
                return true;
            case "perfume":
                k.Lathe("perfume", Vector3.zero, new Vector3(0.09f, 0.11f, 0.07f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.015f, 0), 0.07f, 0.05f, Mul(c, 0.8f));
                k.Cyl(new Vector3(0, 0.06f, 0), 0.02f, 0.02f, Gold, default, Surface.Metal);
                k.Cap(new Vector3(0.035f, 0.07f, 0), 0.008f, 0.06f, Gold, new Vector3(0, 0, -90f), Surface.Metal);
                k.Ball(new Vector3(0.075f, 0.07f, 0), new Vector3(0.04f, 0.05f, 0.04f), Pink);
                return true;
            case "mirror":
            {
                var mr = k.In(Vector3.zero, new Vector3(-90f, 0, 0));
                mr.Box(Vector3.zero, new Vector3(0.4f, 0.5f, 0.03f), c);
                mr.Box(new Vector3(0, 0, 0.012f), new Vector3(0.34f, 0.44f, 0.012f), H(0xDDEBFF), default, Surface.Metal);
                mr.Box(new Vector3(0.02f, 0.05f, 0.02f), new Vector3(0.006f, 0.3f, 0.004f), DarkGrey, new Vector3(0, 0, 25f));
                mr.Box(new Vector3(0.06f, -0.06f, 0.02f), new Vector3(0.006f, 0.2f, 0.004f), DarkGrey, new Vector3(0, 0, -40f));
                mr.Box(new Vector3(-0.05f, 0.02f, 0.02f), new Vector3(0.006f, 0.18f, 0.004f), DarkGrey, new Vector3(0, 0, 70f));
                mr.Box(new Vector3(0, 0.1f, -0.017f), new Vector3(0.12f, 0.02f, 0.004f), Mul(c, 0.7f));
                return true;
            }
            case "sauce":
                k.Lathe("bottle", Vector3.zero, new Vector3(0.05f, 0.17f, 0.05f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.053f, 0.06f, White);
                k.Cone(new Vector3(0, -0.02f, 0.02f), 0.025f, 0.035f, Red, new Vector3(90f, 0, 0));
                k.Cyl(new Vector3(0, 0.09f, 0), 0.022f, 0.02f, Green);
                return true;
            case "bulb":
                k.Lathe("bulb", new Vector3(0, 0.02f, 0), new Vector3(0.07f, 0.1f, 0.07f), c, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.04f, 0), 0.03f, 0.03f, Silver, default, Surface.Metal);
                k.Ring(new Vector3(0, -0.04f, 0), 0.034f, 0.006f, Grey, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.058f, 0), 0.014f, 0.008f, Black);
                k.Ring(new Vector3(0, 0.03f, 0), 0.025f, 0.004f, DarkGrey, new Vector3(90f, 0, 0));
                return true;

            // ------------------------------------------------------------ metal
            case "can":
                k.Cyl(Vector3.zero, 0.066f, 0.12f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.063f, 0), 0.058f, 0.008f, Silver, default, Surface.Metal);
                k.Box(new Vector3(0.012f, 0.068f, 0), new Vector3(0.02f, 0.003f, 0.012f), Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.01f, 0), 0.068f, 0.035f, White);
                return true;
            case "can_crushed":
            {
                var cc = k.In(Vector3.zero, new Vector3(0, 0, 12f));
                cc.Cyl(new Vector3(0, 0.022f, 0), 0.066f, 0.02f, c, new Vector3(8f, 0, 0), Surface.Metal);
                cc.Cyl(new Vector3(0.008f, 0, 0), 0.085f, 0.025f, c, new Vector3(-6f, 0, 10f), Surface.Metal);
                cc.Cyl(new Vector3(-0.004f, -0.022f, 0), 0.07f, 0.02f, Mul(c, 0.85f), new Vector3(4f, 0, -8f), Surface.Metal);
                cc.Cyl(new Vector3(0, 0.034f, 0), 0.056f, 0.006f, Silver, default, Surface.Metal);
                cc.Box(new Vector3(0.01f, 0.004f, 0.04f), new Vector3(0.03f, 0.012f, 0.004f), White, new Vector3(0, 0, 15f));
                return true;
            }
            case "tuna":
                k.Cyl(Vector3.zero, 0.085f, 0.035f, Silver, default, Surface.Metal);
                k.Cyl(Vector3.zero, 0.087f, 0.022f, c);
                k.Cyl(new Vector3(-0.035f, 0.045f, 0), 0.082f, 0.003f, Silver, new Vector3(0, 0, -60f), Surface.Metal);
                k.Ball(new Vector3(0.01f, 0.016f, 0), new Vector3(0.05f, 0.01f, 0.05f), H(0xE8B8A0));
                return true;
            case "tincan":
                k.Cyl(Vector3.zero, 0.075f, 0.11f, Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.015f, 0), 0.077f, 0.05f, c);
                k.Cyl(new Vector3(0, -0.025f, 0), 0.077f, 0.035f, White);
                k.Ball(new Vector3(0, -0.025f, 0.035f), new Vector3(0.025f, 0.02f, 0.008f), Gold);
                return true;
            case "pan":
                k.Lathe("pot", Vector3.zero, new Vector3(0.26f, 0.04f, 0.26f), c, default, Surface.Metal);
                k.Cap(new Vector3(0.22f, 0.012f, 0), 0.028f, 0.2f, Black, new Vector3(0, 0, 80f));
                k.Ball(new Vector3(-0.02f, 0.021f, 0.01f), new Vector3(0.12f, 0.008f, 0.1f), White);
                k.Ball(new Vector3(-0.01f, 0.027f, 0.015f), new Vector3(0.04f, 0.015f, 0.04f), Yellow);
                return true;
            case "toaster":
                k.Box(Vector3.zero, new Vector3(0.26f, 0.17f, 0.15f), c, default, Surface.Metal);
                k.Box(new Vector3(0, 0.086f, 0.03f), new Vector3(0.18f, 0.004f, 0.025f), Black);
                k.Box(new Vector3(0, 0.086f, -0.03f), new Vector3(0.18f, 0.004f, 0.025f), Black);
                k.Box(new Vector3(0.02f, 0.11f, 0.03f), new Vector3(0.11f, 0.06f, 0.018f), H(0x3A2410), new Vector3(0, 0, 8f));
                k.Box(new Vector3(0.135f, 0.02f, 0), new Vector3(0.02f, 0.02f, 0.04f), Black);
                k.Cyl(new Vector3(-0.1f, -0.09f, 0.05f), 0.02f, 0.012f, Black);
                k.Cyl(new Vector3(0.1f, -0.09f, 0.05f), 0.02f, 0.012f, Black);
                k.Cyl(new Vector3(-0.1f, -0.09f, -0.05f), 0.02f, 0.012f, Black);
                k.Cyl(new Vector3(0.1f, -0.09f, -0.05f), 0.02f, 0.012f, Black);
                return true;
            case "hubcap":
                k.Lathe("dome", Vector3.zero, new Vector3(0.38f, 0.06f, 0.38f), c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.03f, 0), 0.08f, 0.02f, Silver, default, Surface.Metal);
                for (int i = 0; i < 5; i++)
                {
                    var p = Quaternion.Euler(0, i * 72f, 0) * new Vector3(0.1f, 0.012f, 0);
                    k.Ball(p, new Vector3(0.06f, 0.012f, 0.035f), DarkGrey, new Vector3(0, i * 72f, 0));
                }
                return true;
            case "bikechain":
                for (int i = 0; i < 14; i++)
                {
                    float a = i * Mathf.PI * 2f / 14f;
                    var p = new Vector3(Mathf.Cos(a) * 0.15f, 0, Mathf.Sin(a) * 0.08f);
                    float yaw = -Mathf.Atan2(Mathf.Cos(a) * 0.08f, -Mathf.Sin(a) * 0.15f) * Mathf.Rad2Deg;
                    k.P("torus25", p, new Vector3(0.05f, 0.05f, 0.03f), i % 2 == 0 ? c : Mul(c, 0.8f),
                        new Vector3(i % 2 == 0 ? 90f : 0f, yaw, 0), Surface.Metal, 0f);
                }
                return true;
            case "hangers":
                for (int i = 0; i < 3; i++)
                {
                    var h = k.In(new Vector3(i * 0.02f, i * 0.02f, 0), new Vector3(i * 30f, i * 50f, i * 25f));
                    h.Cyl(new Vector3(0, -0.07f, 0), 0.01f, 0.4f, c, new Vector3(0, 0, 90f), Surface.Metal);
                    h.Cyl(new Vector3(-0.1f, 0, 0), 0.01f, 0.23f, c, new Vector3(0, 0, -52f), Surface.Metal);
                    h.Cyl(new Vector3(0.1f, 0, 0), 0.01f, 0.23f, c, new Vector3(0, 0, 52f), Surface.Metal);
                    h.Ring(new Vector3(0, 0.09f, 0), 0.06f, 0.01f, c, new Vector3(90f, 0, 0), Surface.Metal);
                }
                return true;
            case "hammer":
                k.Cyl(Vector3.zero, 0.028f, 0.3f, Brown, new Vector3(0, 0, 90f));
                k.Box(new Vector3(0.15f, 0, 0), new Vector3(0.04f, 0.12f, 0.035f), c, default, Surface.Metal);
                k.Cone(new Vector3(0.15f, -0.075f, 0), 0.04f, 0.04f, c, new Vector3(180f, 0, 0), Surface.Metal);
                return true;
            case "pot":
                k.Lathe("pot", Vector3.zero, new Vector3(0.24f, 0.2f, 0.24f), c, default, Surface.Metal);
                k.Ring(new Vector3(0.13f, 0.06f, 0), 0.05f, 0.012f, Black, new Vector3(90f, 90f, 0));
                k.Ring(new Vector3(-0.13f, 0.06f, 0), 0.05f, 0.012f, Black, new Vector3(90f, 90f, 0));
                k.Lathe("dome", new Vector3(0.02f, 0.115f, 0), new Vector3(0.25f, 0.04f, 0.25f), Mul(c, 0.9f), new Vector3(0, 0, -8f), Surface.Metal);
                k.Ball(new Vector3(0.022f, 0.145f, 0), 0.03f, Black);
                k.Ball(new Vector3(0.1f, 0.0f, 0.06f), new Vector3(0.05f, 0.06f, 0.04f), Mul(c, 0.6f));
                return true;
            case "tin":
                k.Cyl(Vector3.zero, 0.12f, 0.17f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.09f, 0), 0.124f, 0.02f, White);
                k.Cyl(new Vector3(0, -0.01f, 0), 0.122f, 0.08f, White);
                k.Ball(new Vector3(0, -0.01f, 0.06f), new Vector3(0.05f, 0.05f, 0.01f), Yellow);
                return true;
            case "toolbox":
                k.Box(Vector3.zero, new Vector3(0.45f, 0.2f, 0.22f), c, default, Surface.Metal);
                k.Box(new Vector3(0, 0.105f, 0), new Vector3(0.46f, 0.03f, 0.23f), Mul(c, 0.85f), default, Surface.Metal);
                k.Ring(new Vector3(0, 0.13f, 0), 0.16f, 0.025f, Black, new Vector3(90f, 0, 0));
                k.Box(new Vector3(0, 0.08f, 0.115f), new Vector3(0.05f, 0.04f, 0.01f), Silver, default, Surface.Metal);
                return true;

            // ------------------------------------------------------------ paper
            case "newspaper":
                k.Box(new Vector3(-0.075f, 0, 0), new Vector3(0.16f, 0.025f, 0.22f), c, new Vector3(0, 0, 6f));
                k.Box(new Vector3(0.075f, 0, 0), new Vector3(0.16f, 0.025f, 0.22f), c, new Vector3(0, 0, -6f));
                k.Box(new Vector3(0.075f, 0.018f, -0.06f), new Vector3(0.13f, 0.004f, 0.03f), Black, new Vector3(0, 0, -6f));
                for (int i = 0; i < 4; i++)
                    k.Box(new Vector3(0.075f, 0.016f, -0.02f + i * 0.03f), new Vector3(0.12f, 0.003f, 0.01f), Grey, new Vector3(0, 0, -6f));
                k.Box(new Vector3(-0.075f, 0.016f, 0.02f), new Vector3(0.1f, 0.003f, 0.08f), H(0x9A9AA0), new Vector3(0, 0, 6f));
                return true;
            case "box":
                k.Box(Vector3.zero, new Vector3(0.3f, 0.22f, 0.24f), c);
                k.Box(new Vector3(0, 0.155f, 0.12f), new Vector3(0.3f, 0.11f, 0.008f), Mul(c, 0.92f), new Vector3(-25f, 0, 0));
                k.Box(new Vector3(0, 0.155f, -0.12f), new Vector3(0.3f, 0.11f, 0.008f), Mul(c, 0.92f), new Vector3(25f, 0, 0));
                k.Box(new Vector3(0, 0, 0.121f), new Vector3(0.05f, 0.22f, 0.004f), H(0xD8B880));
                k.Box(new Vector3(0.08f, 0.02f, 0.122f), new Vector3(0.06f, 0.05f, 0.002f), Black);
                return true;
            case "mail":
                k.Box(Vector3.zero, new Vector3(0.22f, 0.01f, 0.11f), c, new Vector3(0, 10f, 0));
                k.Box(new Vector3(0.02f, 0.011f, 0.01f), new Vector3(0.22f, 0.01f, 0.11f), H(0xE8D8A8), new Vector3(0, -15f, 0));
                k.Box(new Vector3(-0.01f, 0.022f, 0.02f), new Vector3(0.2f, 0.01f, 0.1f), Pink, new Vector3(0, 30f, 0));
                k.Box(new Vector3(-0.01f, 0.028f, 0.02f), new Vector3(0.12f, 0.003f, 0.03f), Yellow, new Vector3(0, 30f, 0));
                k.Box(new Vector3(0.1f, 0.018f, -0.02f), new Vector3(0.025f, 0.003f, 0.03f), Red, new Vector3(0, -15f, 0));
                return true;
            case "carton":
                k.Box(Vector3.zero, new Vector3(0.3f, 0.035f, 0.115f), c);
                for (int i = 0; i < 12; i++)
                    k.Ball(new Vector3(-0.125f + (i % 6) * 0.05f, 0.018f, i < 6 ? -0.027f : 0.027f), new Vector3(0.045f, 0.035f, 0.045f), Mul(c, 1.08f));
                k.Lathe("dome", new Vector3(0.075f, 0.04f, 0.027f), new Vector3(0.04f, 0.025f, 0.04f), White);
                return true;
            case "book":
                k.Box(Vector3.zero, new Vector3(0.21f, 0.07f, 0.28f), H(0xF4F0E0));
                k.Box(new Vector3(0, 0.037f, 0), new Vector3(0.215f, 0.008f, 0.285f), c);
                k.Box(new Vector3(0, -0.037f, 0), new Vector3(0.215f, 0.008f, 0.285f), c);
                k.Box(new Vector3(-0.106f, 0, 0), new Vector3(0.008f, 0.08f, 0.285f), c);
                k.Box(new Vector3(0, 0.042f, 0.04f), new Vector3(0.14f, 0.003f, 0.05f), Black);
                k.Box(new Vector3(0.04f, 0.042f, -0.06f), new Vector3(0.05f, 0.003f, 0.05f), Black);
                return true;
            case "homework":
            {
                k.Box(Vector3.zero, new Vector3(0.21f, 0.006f, 0.28f), c, new Vector3(0, 8f, 0));
                var s = k.In(new Vector3(0, 0.004f, 0), new Vector3(0, 8f, 0));
                for (int i = 0; i < 7; i++)
                    s.Box(new Vector3(0, 0, -0.06f + i * 0.025f), new Vector3(0.18f, 0.002f, 0.003f), SkyBlue);
                s.Box(new Vector3(-0.085f, 0, 0), new Vector3(0.003f, 0.002f, 0.27f), Red);
                // big red "A-"
                s.Box(new Vector3(0.02f, 0.003f, -0.09f), new Vector3(0.012f, 0.004f, 0.075f), Red, new Vector3(0, 20f, 0));
                s.Box(new Vector3(0.046f, 0.003f, -0.09f), new Vector3(0.012f, 0.004f, 0.075f), Red, new Vector3(0, -20f, 0));
                s.Box(new Vector3(0.033f, 0.003f, -0.08f), new Vector3(0.03f, 0.004f, 0.01f), Red);
                s.Box(new Vector3(0.08f, 0.003f, -0.09f), new Vector3(0.025f, 0.004f, 0.01f), Red);
                s.Box(new Vector3(-0.05f, 0.003f, 0.09f), new Vector3(0.03f, 0.004f, 0.03f), Yellow, new Vector3(0, 45f, 0));
                return true;
            }
            case "cerealbox":
                k.Box(Vector3.zero, new Vector3(0.19f, 0.28f, 0.065f), c);
                for (int side = 0; side < 2; side++)
                {
                    var f = k.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Box(new Vector3(0, 0.1f, 0.033f), new Vector3(0.16f, 0.05f, 0.004f), Yellow);
                    f.Cyl(new Vector3(0, -0.05f, 0.034f), 0.12f, 0.004f, White, new Vector3(90f, 0, 0));
                    f.Ball(new Vector3(-0.02f, -0.04f, 0.036f), 0.03f, Orange);
                    f.Ball(new Vector3(0.025f, -0.05f, 0.036f), 0.028f, H(0xF06FA8));
                    f.Ball(new Vector3(0.0f, -0.07f, 0.036f), 0.03f, SkyBlue);
                }
                return true;
            case "magazines":
            {
                Color[] cols = { c, Red, Yellow, Green, Pink, OffWhite };
                for (int i = 0; i < 6; i++)
                    k.Box(new Vector3(0, i * 0.014f, 0), new Vector3(0.21f, 0.013f, 0.28f), cols[i], new Vector3(0, (i * 37 % 20) - 10f, 0));
                k.Box(new Vector3(0, 0.035f, 0), new Vector3(0.006f, 0.09f, 0.29f), H(0xD8C8A0));
                k.Box(new Vector3(0, 0.035f, 0), new Vector3(0.22f, 0.09f, 0.006f), H(0xD8C8A0));
                return true;
            }
            case "paperbag":
                k.Box(Vector3.zero, new Vector3(0.15f, 0.2f, 0.1f), c, new Vector3(0, 0, 4f));
                k.Ball(new Vector3(0, 0.1f, 0), new Vector3(0.16f, 0.07f, 0.1f), c, new Vector3(0, 0, -10f));
                k.Ball(new Vector3(0.04f, 0.12f, 0.02f), 0.06f, Mul(c, 0.9f));
                k.Box(new Vector3(0, -0.02f, 0.051f), new Vector3(0.1f, 0.004f, 0.002f), Mul(c, 0.75f));
                return true;
            case "wrap":
                k.Ball(Vector3.zero, new Vector3(0.16f, 0.12f, 0.14f), c);
                k.Ball(new Vector3(0.06f, 0.02f, 0.03f), 0.1f, Green);
                k.Ball(new Vector3(-0.05f, 0.03f, -0.03f), 0.09f, SkyBlue);
                k.Ring(new Vector3(0.01f, 0.02f, 0), 0.16f, 0.012f, Gold, new Vector3(60f, 0, 20f));
                k.Ball(new Vector3(-0.02f, 0.08f, 0.0f), new Vector3(0.05f, 0.025f, 0.03f), Gold, new Vector3(0, 0, 30f));
                k.Ball(new Vector3(0.02f, 0.08f, 0.0f), new Vector3(0.05f, 0.025f, 0.03f), Gold, new Vector3(0, 0, -30f));
                return true;
            case "letters":
                for (int i = 0; i < 5; i++)
                    k.Box(new Vector3(0, i * 0.011f, 0), new Vector3(0.17f, 0.01f, 0.1f), i % 2 == 0 ? c : H(0xF8D8E0), new Vector3(0, (i * 13 % 14) - 7f, 0));
                k.Box(new Vector3(0, 0.025f, 0), new Vector3(0.012f, 0.062f, 0.11f), Red);
                k.Box(new Vector3(0, 0.025f, 0), new Vector3(0.18f, 0.062f, 0.012f), Red);
                k.Ball(new Vector3(-0.015f, 0.06f, 0), new Vector3(0.035f, 0.02f, 0.02f), Red, new Vector3(0, 0, 30f));
                k.Ball(new Vector3(0.015f, 0.06f, 0), new Vector3(0.035f, 0.02f, 0.02f), Red, new Vector3(0, 0, -30f));
                k.Ball(new Vector3(0.05f, 0.056f, 0.025f), new Vector3(0.022f, 0.008f, 0.022f), H(0xB01830));
                return true;

            // ------------------------------------------------------------ hazard
            case "battery":
                k.Cyl(new Vector3(0, 0.02f, 0), 0.045f, 0.09f, c);
                k.Cyl(new Vector3(0, -0.04f, 0), 0.045f, 0.035f, Black);
                k.Cyl(new Vector3(0, 0.07f, 0), 0.016f, 0.012f, Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.059f, 0), 0.04f, 0.004f, Silver, default, Surface.Metal);
                k.Box(new Vector3(0, 0.03f, 0.022f), new Vector3(0.012f, 0.03f, 0.004f), Black);
                return true;
            case "car_battery":
                k.Box(Vector3.zero, new Vector3(0.3f, 0.18f, 0.18f), c);
                k.Box(new Vector3(0, 0.095f, 0), new Vector3(0.29f, 0.012f, 0.17f), DarkGrey);
                k.Cyl(new Vector3(-0.1f, 0.11f, 0.05f), 0.03f, 0.03f, Red);
                k.Cyl(new Vector3(0.1f, 0.11f, 0.05f), 0.03f, 0.03f, Black);
                k.Box(new Vector3(0, 0.0f, 0.091f), new Vector3(0.18f, 0.07f, 0.004f), Yellow);
                return true;
            case "spray":
                k.Cyl(Vector3.zero, 0.065f, 0.16f, c, default, Surface.Metal);
                k.Lathe("dome", new Vector3(0, 0.09f, 0), new Vector3(0.065f, 0.025f, 0.065f), Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.11f, 0), 0.03f, 0.02f, White);
                k.Box(new Vector3(0.012f, 0.118f, 0), new Vector3(0.012f, 0.008f, 0.008f), Black);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.067f, 0.05f, Black);
                k.Ball(new Vector3(0.03f, 0.05f, 0.015f), new Vector3(0.012f, 0.03f, 0.012f), Mul(c, 1.1f));
                k.Ball(new Vector3(-0.02f, 0.04f, 0.027f), new Vector3(0.012f, 0.04f, 0.012f), Mul(c, 1.1f));
                return true;
            case "flipphone":
            {
                var ph = k.In(Vector3.zero, Vector3.zero, 1.4f);
                ph.Box(Vector3.zero, new Vector3(0.06f, 0.014f, 0.11f), c, default, Surface.Metal);
                for (int i = 0; i < 12; i++)
                    ph.Box(new Vector3(-0.016f + (i % 3) * 0.016f, 0.008f, -0.04f + (i / 3) * 0.018f), new Vector3(0.012f, 0.004f, 0.01f), DarkGrey);
                var lid = ph.In(new Vector3(0, 0.007f, 0.055f), new Vector3(-150f, 0, 0));
                lid.Box(new Vector3(0, 0.007f, 0.055f), new Vector3(0.06f, 0.012f, 0.11f), c, default, Surface.Metal);
                lid.Box(new Vector3(0, 0.0f, 0.05f), new Vector3(0.045f, 0.004f, 0.06f), H(0x2A6AC8), default, Surface.Emissive, 0.6f);
                lid.Cap(new Vector3(0.022f, 0.012f, 0.12f), 0.01f, 0.04f, DarkGrey, new Vector3(90f, 0, 0));
                return true;
            }
            case "paint_bucket":
                k.Cyl(Vector3.zero, 0.2f, 0.2f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.1f, 0), 0.19f, 0.01f, H(0xD8CFB0));
                k.Ring(new Vector3(0, 0.08f, 0), 0.22f, 0.012f, Silver, new Vector3(0, 0, 70f), Surface.Metal);
                k.Box(new Vector3(0, 0, 0.1f), new Vector3(0.12f, 0.1f, 0.004f), Blue);
                k.Ball(new Vector3(0.07f, 0.05f, 0.075f), new Vector3(0.03f, 0.08f, 0.02f), H(0xD8CFB0));
                return true;
            case "tube":
                k.Cyl(Vector3.zero, 0.035f, 0.56f, c, new Vector3(0, 0, 90f), Surface.Emissive, 0.35f);
                k.Cyl(new Vector3(0.285f, 0, 0), 0.038f, 0.02f, Silver, new Vector3(0, 0, 90f), Surface.Metal);
                k.Cyl(new Vector3(-0.285f, 0, 0), 0.038f, 0.02f, Silver, new Vector3(0, 0, 90f), Surface.Metal);
                k.Cyl(new Vector3(0.3f, 0, 0.008f), 0.005f, 0.015f, Silver, new Vector3(0, 0, 90f), Surface.Metal);
                k.Cyl(new Vector3(0.3f, 0, -0.008f), 0.005f, 0.015f, Silver, new Vector3(0, 0, 90f), Surface.Metal);
                return true;
            case "jerrycan":
                k.Box(Vector3.zero, new Vector3(0.22f, 0.3f, 0.12f), c);
                k.Ring(new Vector3(-0.04f, 0.16f, 0), 0.1f, 0.022f, c, new Vector3(90f, 0, 0));
                k.Cyl(new Vector3(0.07f, 0.17f, 0), 0.04f, 0.05f, Black, new Vector3(0, 0, -20f));
                k.Box(new Vector3(0, 0, 0.061f), new Vector3(0.14f, 0.12f, 0.004f), Yellow);
                return true;
            case "propane":
                k.Cap(Vector3.zero, 0.12f, 0.24f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.13f, 0), 0.03f, 0.04f, Gold, default, Surface.Metal);
                k.Ring(new Vector3(0, 0.155f, 0), 0.05f, 0.012f, Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.0f, 0), 0.122f, 0.05f, Red);
                return true;
            case "extinguisher":
                k.Cap(Vector3.zero, 0.13f, 0.4f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.21f, 0), 0.04f, 0.04f, Black);
                k.Box(new Vector3(0.03f, 0.24f, 0), new Vector3(0.09f, 0.015f, 0.03f), Black, new Vector3(0, 0, 15f));
                k.Cap(new Vector3(-0.07f, 0.1f, 0), 0.025f, 0.22f, Black, new Vector3(0, 0, -15f));
                k.Box(new Vector3(0, 0.02f, 0.065f), new Vector3(0.08f, 0.1f, 0.004f), White);
                return true;

            // ------------------------------------------------------------ valuables: uncommon
            case "gnome":
                k.Lathe("gnomebody", new Vector3(0, 0.06f, 0), new Vector3(0.2f, 0.18f, 0.2f), H(0x3C78D8));
                k.Ring(new Vector3(0, 0.07f, 0), 0.185f, 0.02f, Black);
                k.Ball(new Vector3(0.045f, -0.035f, 0.03f), new Vector3(0.07f, 0.035f, 0.09f), Brown);
                k.Ball(new Vector3(-0.045f, -0.035f, 0.03f), new Vector3(0.07f, 0.035f, 0.09f), Brown);
                k.Ball(new Vector3(0, 0.2f, 0), 0.11f, Skin);
                k.Cone(new Vector3(0, 0.15f, 0.045f), 0.12f, 0.14f, White, new Vector3(180f, 0, 0));
                k.Ball(new Vector3(0, 0.2f, 0.055f), 0.035f, Pink);
                k.Ball(new Vector3(0.025f, 0.225f, 0.045f), 0.015f, Black);
                k.Ball(new Vector3(-0.025f, 0.225f, 0.045f), 0.015f, Black);
                k.Cone(new Vector3(0, 0.33f, -0.01f), 0.13f, 0.22f, c, new Vector3(-12f, 0, 8f));
                return true;
            case "lavalamp":
                k.Lathe("lavabase", new Vector3(0, -0.12f, 0), new Vector3(0.14f, 0.1f, 0.14f), Silver, default, Surface.Metal);
                k.Lathe("lava", new Vector3(0, 0.05f, 0), new Vector3(0.12f, 0.24f, 0.12f), new Color(1f, 0.85f, 0.95f, 0.35f), default, Surface.Glass);
                k.Cone(new Vector3(0, 0.2f, 0), 0.065f, 0.06f, Silver, default, Surface.Metal);
                k.Ball(new Vector3(0, -0.02f, 0), new Vector3(0.07f, 0.05f, 0.07f), c, default, Surface.Emissive, 1.4f);
                k.Ball(new Vector3(0.01f, 0.06f, 0.005f), 0.045f, c, Surface.Emissive, 1.4f);
                k.Ball(new Vector3(-0.012f, 0.12f, 0), 0.03f, c, Surface.Emissive, 1.4f);
                return true;
            case "camera":
                k.Box(Vector3.zero, new Vector3(0.15f, 0.1f, 0.12f), c);
                k.Box(new Vector3(0, -0.035f, 0), new Vector3(0.152f, 0.03f, 0.122f), DarkGrey);
                Color[] rainbow = { Red, Orange, Yellow, Green, Blue };
                for (int i = 0; i < 5; i++)
                {
                    k.Box(new Vector3(-0.015f + i * 0.008f, 0.01f, 0.061f), new Vector3(0.008f, 0.08f, 0.004f), rainbow[i]);
                    k.Box(new Vector3(-0.015f + i * 0.008f, 0.051f, 0.0f), new Vector3(0.008f, 0.004f, 0.12f), rainbow[i]);
                }
                k.Cyl(new Vector3(0.035f, 0.005f, 0.07f), 0.06f, 0.03f, Black, new Vector3(90f, 0, 0));
                k.Ball(new Vector3(0.035f, 0.005f, 0.085f), new Vector3(0.04f, 0.04f, 0.015f), H(0x6AA8D8), default, Surface.Metal);
                k.Box(new Vector3(-0.045f, 0.035f, 0.061f), new Vector3(0.035f, 0.02f, 0.006f), White, default, Surface.Emissive, 0.3f);
                k.Box(new Vector3(0, -0.06f, 0.06f), new Vector3(0.09f, 0.004f, 0.08f), White, new Vector3(20f, 0, 0));
                k.Box(new Vector3(0, -0.058f, 0.065f), new Vector3(0.07f, 0.004f, 0.055f), H(0x3A3A48), new Vector3(20f, 0, 0));
                return true;
            case "trophy":
                k.Box(new Vector3(0, -0.11f, 0), new Vector3(0.1f, 0.05f, 0.1f), H(0x2A2A30));
                k.Box(new Vector3(0, -0.11f, 0.051f), new Vector3(0.06f, 0.025f, 0.004f), Gold, default, Surface.Metal);
                k.Lathe("trophy", new Vector3(0, 0.02f, 0), new Vector3(0.12f, 0.22f, 0.12f), c, default, Surface.Metal);
                k.Ring(new Vector3(0.07f, 0.08f, 0), 0.06f, 0.012f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ring(new Vector3(-0.07f, 0.08f, 0), 0.06f, 0.012f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Box(new Vector3(0, 0.07f, 0.058f), new Vector3(0.012f, 0.05f, 0.004f), Red);
                return true;
            case "coinjar":
                k.Lathe("jar", Vector3.zero, new Vector3(0.14f, 0.18f, 0.14f), Clear, default, Surface.Glass);
                k.Cyl(new Vector3(0, -0.02f, 0), 0.125f, 0.12f, c);
                for (int i = 0; i < 7; i++)
                {
                    var p = Quaternion.Euler(0, i * 51f, 0) * new Vector3(i == 0 ? 0f : 0.035f, 0.045f + (i % 2) * 0.008f, 0);
                    k.Cyl(p, 0.032f, 0.006f, i % 3 == 0 ? Silver : Mul(c, 1.15f), new Vector3(i * 12f, 0, 15f - i * 5f), Surface.Metal);
                }
                k.Cyl(new Vector3(0, 0.09f, 0), 0.12f, 0.02f, Gold, default, Surface.Metal);
                return true;
            case "cassette":
            {
                var ct = k.In(Vector3.zero, new Vector3(-90f, 0, 0), 1.3f);
                ct.Box(Vector3.zero, new Vector3(0.13f, 0.085f, 0.018f), c);
                ct.Box(new Vector3(0, -0.032f, 0), new Vector3(0.08f, 0.02f, 0.022f), Mul(c, 1.3f));
                for (int side = 0; side < 2; side++)
                {
                    var f = ct.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Box(new Vector3(0, 0.012f, 0.01f), new Vector3(0.11f, 0.045f, 0.004f), White);
                    f.Box(new Vector3(0, 0.028f, 0.0125f), new Vector3(0.11f, 0.008f, 0.002f), side == 0 ? Red : Blue);
                    f.Box(new Vector3(0, 0.012f, 0.0125f), new Vector3(0.05f, 0.018f, 0.002f), H(0x5A4A3A));
                    f.Cyl(new Vector3(-0.025f, 0.012f, 0.012f), 0.014f, 0.004f, White, new Vector3(90f, 0, 0));
                    f.Cyl(new Vector3(0.025f, 0.012f, 0.012f), 0.014f, 0.004f, White, new Vector3(90f, 0, 0));
                }
                return true;
            }
            case "tamagochu":
            {
                var tg = k.In(Vector3.zero, new Vector3(-70f, 0, 0), 1.2f);
                tg.Lathe("egg", Vector3.zero, new Vector3(0.12f, 0.14f, 0.07f), c);
                tg.Box(new Vector3(0, 0.012f, 0.03f), new Vector3(0.06f, 0.05f, 0.012f), H(0x2A2A30));
                tg.Box(new Vector3(0, 0.012f, 0.036f), new Vector3(0.048f, 0.04f, 0.004f), H(0xB8D8A0), default, Surface.Emissive, 0.35f);
                tg.Box(new Vector3(-0.005f, 0.014f, 0.039f), new Vector3(0.012f, 0.012f, 0.002f), Black);
                for (int i = 0; i < 3; i++)
                    tg.Ball(new Vector3(-0.022f + i * 0.022f, -0.035f, 0.03f), 0.016f, Yellow);
                tg.Ring(new Vector3(0, 0.08f, 0), 0.035f, 0.007f, Silver, new Vector3(90f, 0, 0), Surface.Metal);
                return true;
            }
            case "bowling":
                k.Ball(Vector3.zero, 0.22f, c, Surface.Metal);
                k.Ball(new Vector3(0.0f, 0.06f, 0.095f), new Vector3(0.03f, 0.03f, 0.02f), Black, new Vector3(-30f, 0, 0));
                k.Ball(new Vector3(0.035f, 0.075f, 0.08f), new Vector3(0.025f, 0.025f, 0.02f), Black, new Vector3(-40f, 20f, 0));
                k.Ball(new Vector3(-0.035f, 0.075f, 0.08f), new Vector3(0.025f, 0.025f, 0.02f), Black, new Vector3(-40f, -20f, 0));
                k.Ball(new Vector3(0.05f, -0.03f, 0.08f), new Vector3(0.06f, 0.08f, 0.04f), Mix(c, Pink, 0.5f), new Vector3(0, 30f, 20f), Surface.Metal);
                return true;
            case "fondue":
                k.Lathe("pot", new Vector3(0, 0.05f, 0), new Vector3(0.16f, 0.1f, 0.16f), c);
                k.Ball(new Vector3(0, 0.1f, 0), new Vector3(0.14f, 0.03f, 0.14f), Yellow);
                for (int i = 0; i < 3; i++)
                {
                    var q = Quaternion.Euler(0, i * 120f, 0);
                    k.Cyl(q * new Vector3(0.07f, -0.04f, 0), 0.01f, 0.1f, Black, (q * Quaternion.Euler(0, 0, 15f)).eulerAngles, Surface.Metal);
                    k.Cyl(q * new Vector3(0.05f, 0.16f, 0), 0.006f, 0.2f, Silver, (q * Quaternion.Euler(0, 0, -25f)).eulerAngles, Surface.Metal);
                    k.Ball(q * new Vector3(0.1f, 0.255f, 0), 0.02f, i == 0 ? Red : i == 1 ? Green : Blue);
                }
                k.Cyl(new Vector3(0, -0.06f, 0), 0.06f, 0.03f, Silver, default, Surface.Metal);
                return true;
            case "drill":
                k.Box(new Vector3(0, 0.03f, 0), new Vector3(0.06f, 0.08f, 0.18f), c);
                k.Box(new Vector3(0, -0.05f, -0.03f), new Vector3(0.05f, 0.12f, 0.05f), Black, new Vector3(-12f, 0, 0));
                k.Box(new Vector3(0, -0.12f, -0.04f), new Vector3(0.07f, 0.04f, 0.09f), DarkGrey);
                k.Cyl(new Vector3(0, 0.03f, 0.11f), 0.035f, 0.04f, Black, new Vector3(90f, 0, 0));
                k.Cyl(new Vector3(0, 0.03f, 0.16f), 0.008f, 0.07f, Silver, new Vector3(90f, 0, 0), Surface.Metal);
                return true;
            case "vinyl":
                k.Box(new Vector3(-0.06f, -0.006f, 0), new Vector3(0.31f, 0.008f, 0.31f), c);
                k.Ball(new Vector3(-0.06f, -0.001f, 0), new Vector3(0.14f, 0.002f, 0.14f), Pink);
                k.Cyl(new Vector3(0.03f, 0.004f, 0), 0.3f, 0.006f, Black, default, Surface.Metal);
                k.Cyl(new Vector3(0.03f, 0.0075f, 0), 0.1f, 0.002f, Red);
                k.Cyl(new Vector3(0.03f, 0.009f, 0), 0.012f, 0.002f, White);
                return true;
            case "snowglobe":
                k.Cyl(new Vector3(0, -0.05f, 0), 0.1f, 0.04f, c);
                k.Ball(new Vector3(0, 0.02f, 0), 0.12f, Clear, Surface.Glass);
                k.Lathe("dome", new Vector3(0, -0.02f, 0), new Vector3(0.1f, 0.025f, 0.1f), White);
                k.Box(new Vector3(0, 0.01f, 0), new Vector3(0.035f, 0.03f, 0.03f), Red);
                k.Cone(new Vector3(0, 0.035f, 0), 0.05f, 0.025f, Brown, new Vector3(0, 45f, 0));
                k.Ball(new Vector3(0.03f, 0.06f, 0.01f), 0.008f, White);
                k.Ball(new Vector3(-0.025f, 0.045f, -0.02f), 0.008f, White);
                k.Ball(new Vector3(0.0f, 0.07f, -0.02f), 0.008f, White);
                return true;
            case "catfig":
                k.Ball(new Vector3(0, 0, 0), new Vector3(0.09f, 0.12f, 0.08f), c);
                k.Ball(new Vector3(0, 0.08f, 0.01f), 0.075f, c);
                k.Cone(new Vector3(0.022f, 0.12f, 0.01f), 0.03f, 0.035f, c, new Vector3(0, 0, -15f));
                k.Cone(new Vector3(-0.022f, 0.12f, 0.01f), 0.03f, 0.035f, c, new Vector3(0, 0, 15f));
                k.Ball(new Vector3(0.016f, 0.085f, 0.043f), new Vector3(0.012f, 0.016f, 0.008f), H(0x3AA85A));
                k.Ball(new Vector3(-0.016f, 0.085f, 0.043f), new Vector3(0.012f, 0.016f, 0.008f), H(0x3AA85A));
                k.Ball(new Vector3(0, 0.07f, 0.047f), 0.01f, Pink);
                k.Cap(new Vector3(0.04f, -0.04f, -0.03f), 0.018f, 0.1f, c, new Vector3(70f, 60f, 0));
                k.Ring(new Vector3(0, 0.045f, 0.005f), 0.07f, 0.01f, Blue, new Vector3(15f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, -0.065f, 0), 0.11f, 0.015f, Gold, default, Surface.Metal);
                return true;

            // ------------------------------------------------------------ valuables: rare
            case "gameboi":
            {
                var g = k.In(Vector3.zero, new Vector3(-90f, 0, 0));   // lying face up
                g.Box(Vector3.zero, new Vector3(0.1f, 0.16f, 0.035f), c);
                g.Box(new Vector3(0, 0.04f, 0.018f), new Vector3(0.08f, 0.065f, 0.004f), H(0x4A4A58));
                g.Box(new Vector3(0, 0.042f, 0.021f), new Vector3(0.05f, 0.045f, 0.002f), H(0x9AB85A), default, Surface.Emissive, 0.35f);
                g.Box(new Vector3(-0.025f, -0.03f, 0.02f), new Vector3(0.03f, 0.009f, 0.008f), Black);
                g.Box(new Vector3(-0.025f, -0.03f, 0.02f), new Vector3(0.009f, 0.03f, 0.008f), Black);
                g.Ball(new Vector3(0.028f, -0.025f, 0.019f), new Vector3(0.016f, 0.016f, 0.01f), H(0xA0205A));
                g.Ball(new Vector3(0.012f, -0.035f, 0.019f), new Vector3(0.016f, 0.016f, 0.01f), H(0xA0205A));
                g.Cap(new Vector3(-0.008f, -0.058f, 0.019f), 0.006f, 0.018f, Grey, new Vector3(0, 0, 60f));
                g.Cap(new Vector3(0.012f, -0.058f, 0.019f), 0.006f, 0.018f, Grey, new Vector3(0, 0, 60f));
                g.Box(new Vector3(0, 0.07f, -0.02f), new Vector3(0.07f, 0.04f, 0.012f), H(0x5A5A60));
                g.Box(new Vector3(0, 0.072f, -0.027f), new Vector3(0.05f, 0.025f, 0.002f), Red);
                g.Box(new Vector3(0, -0.04f, -0.018f), new Vector3(0.06f, 0.05f, 0.004f), Mul(c, 0.85f));
                return true;
            }
            case "baseball":
                k.Box(new Vector3(0, -0.075f, 0), new Vector3(0.15f, 0.03f, 0.15f), Brown);
                k.Box(new Vector3(0, 0.005f, 0), new Vector3(0.14f, 0.13f, 0.14f), Clear, default, Surface.Glass);
                k.Ring(new Vector3(0, -0.055f, 0), 0.04f, 0.01f, Gold, default, Surface.Metal);
                k.Ball(new Vector3(0, -0.015f, 0), 0.08f, c);
                k.Ring(new Vector3(0, -0.015f, 0), 0.075f, 0.006f, Red, new Vector3(60f, 0, 20f));
                k.Ring(new Vector3(0, -0.015f, 0), 0.075f, 0.006f, Red, new Vector3(-60f, 90f, 20f));
                k.Box(new Vector3(0.01f, -0.005f, 0.039f), new Vector3(0.035f, 0.004f, 0.003f), Blue, new Vector3(0, 0, 18f));
                return true;
            case "comic":
                k.Box(Vector3.zero, new Vector3(0.17f, 0.012f, 0.26f), c);
                k.Box(new Vector3(0, 0.007f, -0.095f), new Vector3(0.16f, 0.003f, 0.05f), Red);
                k.Ball(new Vector3(0, 0.009f, 0.02f), new Vector3(0.09f, 0.006f, 0.09f), Grey);
                k.Box(new Vector3(0, 0.012f, 0.02f), new Vector3(0.08f, 0.004f, 0.022f), Black);
                k.Ball(new Vector3(0.018f, 0.014f, 0.02f), new Vector3(0.016f, 0.004f, 0.016f), White);
                k.Ball(new Vector3(-0.018f, 0.014f, 0.02f), new Vector3(0.016f, 0.004f, 0.016f), White);
                k.Cone(new Vector3(0.035f, 0.009f, -0.02f), 0.03f, 0.03f, Grey, new Vector3(90f, 0, -150f));
                k.Cone(new Vector3(-0.035f, 0.009f, -0.02f), 0.03f, 0.03f, Grey, new Vector3(90f, 0, 150f));
                k.Box(new Vector3(0, 0.0f, 0), new Vector3(0.18f, 0.016f, 0.275f), Clear, default, Surface.Glass);
                return true;
            case "pocketwatch":
                k.Cyl(Vector3.zero, 0.09f, 0.025f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.013f, 0), 0.075f, 0.003f, White);
                k.Box(new Vector3(0.01f, 0.016f, 0), new Vector3(0.025f, 0.002f, 0.004f), Black, new Vector3(0, 30f, 0));
                k.Box(new Vector3(0, 0.016f, -0.015f), new Vector3(0.004f, 0.002f, 0.032f), Black);
                k.Cyl(new Vector3(0, 0, -0.05f), 0.014f, 0.014f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ring(new Vector3(0, 0, -0.065f), 0.025f, 0.006f, c, new Vector3(0, 0, 90f), Surface.Metal);
                for (int i = 0; i < 6; i++)
                    k.Ball(new Vector3(0.015f + i * 0.012f, -0.005f, -0.08f - i * 0.009f + (i % 2) * 0.004f), 0.01f, c, Surface.Metal);
                return true;
            case "medal":
            {
                var md = k.In(Vector3.zero, new Vector3(-90f, 0, 0), 1.5f);
                md.Box(new Vector3(0, 0.05f, 0), new Vector3(0.05f, 0.07f, 0.008f), Red);
                md.Box(new Vector3(0, 0.05f, 0), new Vector3(0.016f, 0.071f, 0.01f), White);
                md.Box(new Vector3(0, 0.05f, 0), new Vector3(0.006f, 0.072f, 0.012f), Blue);
                md.Box(new Vector3(0, 0.088f, 0), new Vector3(0.056f, 0.01f, 0.014f), c, default, Surface.Metal);
                md.Cyl(new Vector3(0, -0.01f, 0), 0.06f, 0.008f, c, new Vector3(90f, 0, 0), Surface.Metal);
                for (int side = 0; side < 2; side++)
                    for (int i = 0; i < 5; i++)
                        md.Box(new Vector3(0, -0.01f, side == 0 ? 0.006f : -0.006f), new Vector3(0.01f, 0.05f, 0.004f), Mul(c, 1.15f), new Vector3(0, 0, i * 36f), Surface.Metal);
                return true;
            }
            case "ring":
                k.Ring(Vector3.zero, 0.13f, 0.03f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, 0.068f, 0), 0.035f, 0.02f, c, default, Surface.Metal);
                k.Box(new Vector3(0, 0.09f, 0), new Vector3(0.03f, 0.03f, 0.03f), H(0xE8F8FF), new Vector3(45f, 0, 45f), Surface.Emissive, 0.8f);
                return true;
            case "ring_class":
                k.Ring(Vector3.zero, 0.13f, 0.035f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, 0.07f, 0), 0.06f, 0.03f, c, default, Surface.Metal);
                k.Ball(new Vector3(0, 0.088f, 0), new Vector3(0.045f, 0.02f, 0.045f), H(0xC81E3A), default, Surface.Emissive, 0.5f);
                return true;
            case "candlestick":
                k.Lathe("candle", Vector3.zero, new Vector3(0.12f, 0.24f, 0.12f), c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.17f, 0), 0.03f, 0.1f, White);
                k.Cyl(new Vector3(0, 0.225f, 0), 0.004f, 0.012f, Black);
                k.Ball(new Vector3(0.015f, 0.2f, 0.005f), new Vector3(0.012f, 0.03f, 0.012f), White);
                k.Ball(new Vector3(-0.012f, 0.19f, -0.008f), new Vector3(0.012f, 0.025f, 0.012f), White);
                return true;
            case "cartridge":
            {
                var ca = k.In(Vector3.zero, new Vector3(-90f, 0, 0));
                ca.Box(Vector3.zero, new Vector3(0.12f, 0.13f, 0.025f), c);
                ca.Box(new Vector3(-0.045f, 0.06f, 0), new Vector3(0.03f, 0.012f, 0.026f), Mul(c, 0.85f));
                for (int side = 0; side < 2; side++)
                {
                    var f = ca.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Box(new Vector3(0, 0.01f, 0.013f), new Vector3(0.09f, 0.08f, 0.003f), Red);
                    f.Ball(new Vector3(0, 0.02f, 0.015f), new Vector3(0.03f, 0.03f, 0.004f), Skin);
                    f.Box(new Vector3(0, 0.009f, 0.017f), new Vector3(0.03f, 0.007f, 0.003f), Black);
                    f.Ball(new Vector3(0, 0.036f, 0.016f), new Vector3(0.036f, 0.012f, 0.004f), Red);
                    for (int i = 0; i < 4; i++)
                        f.Box(new Vector3(0, -0.045f - i * 0.006f, 0.013f), new Vector3(0.1f, 0.002f, 0.003f), DarkGrey);
                }
                return true;
            }
            case "doll":
                k.Lathe("dress", Vector3.zero, new Vector3(0.18f, 0.2f, 0.16f), c);
                k.Ring(new Vector3(0, -0.085f, 0), 0.18f, 0.02f, White);
                k.Ball(new Vector3(0, 0.15f, 0), 0.11f, H(0xF8E8E0));
                k.Ball(new Vector3(0, 0.17f, -0.015f), new Vector3(0.12f, 0.1f, 0.11f), H(0x6A3A1A));
                k.Cap(new Vector3(0.065f, 0.12f, -0.01f), 0.03f, 0.1f, H(0x6A3A1A), new Vector3(0, 0, 15f));
                k.Cap(new Vector3(-0.065f, 0.12f, -0.01f), 0.03f, 0.1f, H(0x6A3A1A), new Vector3(0, 0, -15f));
                k.Ball(new Vector3(0.022f, 0.155f, 0.048f), 0.03f, Black);
                k.Ball(new Vector3(-0.022f, 0.158f, 0.048f), 0.022f, Black);
                k.Ball(new Vector3(0.03f, 0.13f, 0.045f), 0.018f, Pink);
                k.Ball(new Vector3(-0.03f, 0.13f, 0.045f), 0.018f, Pink);
                k.Box(new Vector3(-0.01f, 0.18f, 0.05f), new Vector3(0.002f, 0.035f, 0.004f), Black, new Vector3(0, 0, 25f));
                k.Cap(new Vector3(0.08f, 0.04f, 0.01f), 0.025f, 0.09f, H(0xF8E8E0), new Vector3(0, 0, 30f));
                k.Cap(new Vector3(-0.08f, 0.04f, 0.01f), 0.025f, 0.09f, H(0xF8E8E0), new Vector3(0, 0, -30f));
                return true;

            // ------------------------------------------------------------ valuables: epic
            case "watch":
                k.Cyl(Vector3.zero, 0.08f, 0.02f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.011f, 0), 0.065f, 0.003f, H(0x1E5A3A));
                k.Box(new Vector3(0, 0.014f, 0.012f), new Vector3(0.004f, 0.002f, 0.026f), Gold, default, Surface.Metal);
                k.Box(new Vector3(0.01f, 0.014f, 0), new Vector3(0.02f, 0.002f, 0.004f), Gold, default, Surface.Metal);
                k.Box(new Vector3(0, 0.014f, -0.02f), new Vector3(0.018f, 0.002f, 0.006f), Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0.043f, 0, 0), 0.01f, 0.01f, c, new Vector3(0, 0, 90f), Surface.Metal);
                k.Box(new Vector3(0, -0.012f, 0.075f), new Vector3(0.045f, 0.012f, 0.08f), c, new Vector3(-25f, 0, 0), Surface.Metal);
                k.Box(new Vector3(0, -0.012f, -0.075f), new Vector3(0.045f, 0.012f, 0.08f), c, new Vector3(25f, 0, 0), Surface.Metal);
                return true;
            case "card":
                k.Box(Vector3.zero, new Vector3(0.12f, 0.006f, 0.17f), Yellow);
                k.Box(new Vector3(0, 0.004f, -0.025f), new Vector3(0.1f, 0.002f, 0.075f), c, default, Surface.Emissive, 0.9f);
                k.Box(new Vector3(0, 0.0055f, -0.035f), new Vector3(0.1f, 0.001f, 0.01f), H(0xFF60F0), new Vector3(0, 30f, 0), Surface.Emissive, 1.2f);
                k.Box(new Vector3(0, 0.0055f, -0.01f), new Vector3(0.1f, 0.001f, 0.01f), H(0x60F0FF), new Vector3(0, 30f, 0), Surface.Emissive, 1.2f);
                k.Ball(new Vector3(0.01f, 0.007f, -0.025f), new Vector3(0.04f, 0.004f, 0.035f), Red);
                k.Box(new Vector3(0, 0.004f, 0.045f), new Vector3(0.1f, 0.002f, 0.055f), OffWhite);
                k.Box(new Vector3(0, 0.0045f, -0.075f), new Vector3(0.08f, 0.002f, 0.01f), Black);
                k.Box(Vector3.zero, new Vector3(0.13f, 0.01f, 0.18f), Clear, default, Surface.Glass);
                return true;
            case "earring":
                k.Box(new Vector3(0, 0.02f, 0), new Vector3(0.05f, 0.05f, 0.05f), c, new Vector3(45f, 0, 45f), Surface.Emissive, 1.1f);
                k.Cyl(new Vector3(0, -0.012f, 0), 0.045f, 0.012f, Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.04f, 0), 0.008f, 0.05f, Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.062f, 0), 0.02f, 0.008f, Gold, default, Surface.Metal);
                return true;
            case "sockcash":
                k.Cap(new Vector3(0, 0.08f, 0), 0.1f, 0.24f, c);
                k.Ball(new Vector3(0.06f, -0.04f, 0), new Vector3(0.2f, 0.12f, 0.12f), c);
                k.Ball(new Vector3(0.15f, -0.04f, 0), 0.08f, Red);
                k.Ring(new Vector3(0, 0.17f, 0), 0.105f, 0.02f, Red);
                for (int i = 0; i < 4; i++)
                    k.Box(new Vector3(-0.02f + i * 0.014f, 0.22f, (i % 2) * 0.01f), new Vector3(0.06f, 0.1f, 0.003f), H(0x6AB86A), new Vector3(0, i * 25f, -20f + i * 14f));
                k.Ball(new Vector3(0.0f, 0.215f, 0.0f), new Vector3(0.02f, 0.01f, 0.02f), H(0x3A8A3A));
                return true;
            case "chain":
                for (int i = 0; i < 18; i++)
                {
                    float a = i * Mathf.PI * 2f / 18f;
                    var p = new Vector3(Mathf.Cos(a) * 0.1f, 0, Mathf.Sin(a) * 0.12f);
                    float yaw = -Mathf.Atan2(Mathf.Cos(a) * 0.12f, -Mathf.Sin(a) * 0.1f) * Mathf.Rad2Deg;
                    k.P("torus30", p, new Vector3(0.04f, 0.04f, 0.028f), c, new Vector3(i % 2 == 0 ? 90f : 0f, yaw, 0), Surface.Metal, 0f);
                }
                k.Cyl(new Vector3(0, 0, -0.16f), 0.075f, 0.012f, c, default, Surface.Metal);
                k.Box(new Vector3(0, 0.008f, -0.16f), new Vector3(0.01f, 0.004f, 0.045f), Mul(c, 0.7f), default, Surface.Metal);
                k.Box(new Vector3(0, 0.009f, -0.16f), new Vector3(0.03f, 0.004f, 0.008f), Mul(c, 0.7f), new Vector3(0, 30f, 0), Surface.Metal);
                return true;
            case "painting":
            {
                var pt = k.In(Vector3.zero, new Vector3(-90f, 0, 0));   // lying flat
                pt.Box(Vector3.zero, new Vector3(0.4f, 0.3f, 0.01f), OffWhite);
                for (int side = 0; side < 2; side++)
                {
                    var f = pt.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Box(new Vector3(0, 0.08f, 0.006f), new Vector3(0.38f, 0.13f, 0.004f), SkyBlue);
                    f.Box(new Vector3(0, -0.1f, 0.006f), new Vector3(0.38f, 0.08f, 0.004f), Green);
                    f.Ball(new Vector3(0.12f, 0.1f, 0.008f), new Vector3(0.06f, 0.06f, 0.004f), Yellow);
                    f.Ball(new Vector3(-0.03f, 0, 0.01f), new Vector3(0.14f, 0.12f, 0.01f), c);
                    f.Cone(new Vector3(-0.08f, 0.065f, 0.01f), 0.04f, 0.05f, c, new Vector3(0, 0, 25f));
                    f.Cone(new Vector3(0.02f, 0.065f, 0.01f), 0.04f, 0.05f, c, new Vector3(0, 0, -25f));
                    f.Cyl(new Vector3(-0.03f, -0.02f, 0.016f), 0.05f, 0.01f, Mul(c, 0.85f), new Vector3(90f, 0, 0));
                    f.Ball(new Vector3(-0.04f, -0.02f, 0.022f), 0.01f, Black);
                    f.Ball(new Vector3(-0.02f, -0.02f, 0.022f), 0.01f, Black);
                    f.Ball(new Vector3(-0.065f, 0.025f, 0.016f), 0.016f, Black);
                    f.Ball(new Vector3(0.01f, 0.03f, 0.016f), 0.012f, Black);
                    f.Box(new Vector3(0.14f, -0.12f, 0.009f), new Vector3(0.05f, 0.012f, 0.004f), Brown, new Vector3(0, 0, 10f));
                }
                pt.Box(new Vector3(0, 0.165f, 0), new Vector3(0.46f, 0.035f, 0.035f), Gold, default, Surface.Metal);
                pt.Box(new Vector3(0, -0.165f, 0), new Vector3(0.46f, 0.035f, 0.035f), Gold, default, Surface.Metal);
                pt.Box(new Vector3(0.215f, 0, 0), new Vector3(0.035f, 0.3f, 0.035f), Gold, default, Surface.Metal);
                pt.Box(new Vector3(-0.215f, 0, 0), new Vector3(0.035f, 0.3f, 0.035f), Gold, default, Surface.Metal);
                return true;
            }
            case "moonrock":
                k.Ball(Vector3.zero, new Vector3(0.14f, 0.1f, 0.12f), c, new Vector3(10, 20, 0));
                k.Ball(new Vector3(0.05f, 0.02f, 0.02f), 0.09f, Mul(c, 0.9f));
                k.Ball(new Vector3(-0.045f, 0.015f, -0.02f), 0.08f, Mul(c, 1.08f));
                k.Ball(new Vector3(0.0f, 0.05f, 0.015f), new Vector3(0.03f, 0.01f, 0.03f), Mul(c, 0.6f));
                k.Ball(new Vector3(0.06f, 0.045f, 0.0f), new Vector3(0.022f, 0.008f, 0.022f), Mul(c, 0.6f));
                k.Ball(new Vector3(-0.04f, 0.05f, -0.02f), new Vector3(0.025f, 0.008f, 0.025f), Mul(c, 0.6f));
                k.Cyl(new Vector3(0.01f, 0.1f, 0.0f), 0.005f, 0.1f, Brown);
                k.Box(new Vector3(0.032f, 0.13f, 0.0f), new Vector3(0.04f, 0.026f, 0.003f), White);
                k.Box(new Vector3(0.032f, 0.137f, 0.001f), new Vector3(0.04f, 0.006f, 0.003f), Red);
                k.Box(new Vector3(0.032f, 0.124f, 0.001f), new Vector3(0.04f, 0.006f, 0.003f), Red);
                k.Box(new Vector3(0.02f, 0.137f, 0.002f), new Vector3(0.016f, 0.012f, 0.003f), Blue);
                return true;
            case "fabergeegg":
                k.Lathe("trophy", new Vector3(0, -0.1f, 0), new Vector3(0.08f, 0.06f, 0.08f), Gold, new Vector3(180f, 0, 0), Surface.Metal);
                k.Lathe("egg", new Vector3(0, 0.02f, 0), new Vector3(0.13f, 0.17f, 0.13f), c, default, Surface.Metal);
                k.Ring(new Vector3(0, 0.0f, 0), 0.134f, 0.01f, Gold, default, Surface.Metal);
                k.Ring(new Vector3(0, 0.06f, 0), 0.11f, 0.01f, Gold, default, Surface.Metal);
                k.Ring(new Vector3(0, 0.02f, 0), 0.172f, 0.01f, Gold, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ring(new Vector3(0, 0.02f, 0), 0.172f, 0.01f, Gold, new Vector3(90f, 90f, 0), Surface.Metal);
                for (int i = 0; i < 4; i++)
                {
                    var p = Quaternion.Euler(0, i * 90f + 45f, 0) * new Vector3(0.066f, 0.0f, 0);
                    k.Ball(p, 0.018f, i % 2 == 0 ? H(0xE0305A) : H(0x30E0A0), Surface.Emissive, 0.9f);
                }
                k.Ball(new Vector3(0, 0.108f, 0), 0.025f, H(0xE8F8FF), Surface.Emissive, 1f);
                return true;
            case "ticket":
                k.Box(Vector3.zero, new Vector3(0.16f, 0.006f, 0.08f), OffWhite);
                k.Box(new Vector3(-0.055f, 0.004f, 0), new Vector3(0.045f, 0.002f, 0.075f), c);
                for (int i = 0; i < 6; i++)
                {
                    var p = new Vector3(-0.01f + (i % 3) * 0.035f, 0.004f, i < 3 ? -0.016f : 0.018f);
                    k.Cyl(p, 0.028f, 0.002f, i == 5 ? Silver : Gold, default, Surface.Metal);
                    if (i < 5) k.Box(p + new Vector3(0, 0.002f, 0), new Vector3(0.012f, 0.001f, 0.004f), Black);
                }
                k.Box(new Vector3(-0.055f, 0.0055f, 0), new Vector3(0.02f, 0.001f, 0.02f), Yellow, new Vector3(0, 45f, 0), Surface.Emissive, 0.8f);
                return true;
            case "teeth":
            {
                Color gum = H(0xE8788A);
                for (int jaw = 0; jaw < 2; jaw++)
                {
                    var jk = k.In(new Vector3(0, jaw == 0 ? -0.022f : 0.022f, jaw == 0 ? 0f : -0.012f), new Vector3(jaw == 0 ? 0f : -28f, 0, 0));
                    float dir = jaw == 0 ? 1f : -1f;
                    for (int i = 0; i < 9; i++)
                    {
                        float a = Mathf.Lerp(-95f, 95f, i / 8f) * Mathf.Deg2Rad;
                        jk.Ball(new Vector3(Mathf.Sin(a) * 0.05f, -dir * 0.01f, Mathf.Cos(a) * 0.045f), new Vector3(0.03f, 0.022f, 0.03f), gum);
                    }
                    for (int i = 0; i < 7; i++)
                    {
                        float a = Mathf.Lerp(-78f, 78f, i / 6f) * Mathf.Deg2Rad;
                        var p = new Vector3(Mathf.Sin(a) * 0.052f, dir * 0.009f, Mathf.Cos(a) * 0.047f);
                        jk.Box(p, new Vector3(0.022f, 0.026f, 0.016f), i == 3 ? White : c,
                            new Vector3(0, a * Mathf.Rad2Deg, 0), i == 3 ? Surface.Base : Surface.Metal);
                    }
                }
                return true;
            }

            // ============================================================ district items (Docs/Progression.md §5.7, §5.8)
            // ------------------------------------------------------------ legendaries
            case "deed":                                   // Pink Slip to a Minivan
            {
                var dk = k.In(Vector3.zero, new Vector3(0, 12f, 0));
                dk.Box(Vector3.zero, new Vector3(0.21f, 0.004f, 0.28f), c);
                dk.Box(new Vector3(0, 0.003f, 0.11f), new Vector3(0.17f, 0.002f, 0.025f), Mul(c, 0.75f));
                for (int i = 0; i < 6; i++)
                    dk.Box(new Vector3(-0.02f + (i % 2) * 0.03f, 0.003f, 0.06f - i * 0.03f), new Vector3(0.12f - (i % 3) * 0.02f, 0.002f, 0.007f), H(0x6A3A4A));
                // a tiny minivan drawing + a red "PAID" stamp + a folded corner
                dk.Box(new Vector3(0.05f, 0.004f, -0.1f), new Vector3(0.07f, 0.003f, 0.03f), H(0x3C78D8));
                dk.Box(new Vector3(0.04f, 0.0045f, -0.088f), new Vector3(0.04f, 0.003f, 0.015f), SkyBlue);
                dk.Box(new Vector3(-0.06f, 0.004f, -0.1f), new Vector3(0.06f, 0.003f, 0.035f), Red, new Vector3(0, -18f, 0));
                dk.Box(new Vector3(0.09f, 0.006f, 0.125f), new Vector3(0.04f, 0.003f, 0.04f), Mul(c, 0.9f), new Vector3(0, 45f, 25f));
                return true;
            }
            case "chandelier":                             // Crystal Chandelier (Slightly Dented)
            {
                var ch = k.In(Vector3.zero, new Vector3(0, 0, 9f));      // dented: hangs a bit crooked
                ch.Cyl(new Vector3(0, 0.22f, 0), 0.035f, 0.3f, c, default, Surface.Metal);
                ch.Ring(new Vector3(0, 0.38f, 0), 0.06f, 0.015f, c, new Vector3(90f, 0, 0), Surface.Metal);
                ch.Lathe("canopy", new Vector3(0, 0.05f, 0), new Vector3(0.22f, 0.14f, 0.22f), c, new Vector3(180f, 0, 0), Surface.Metal);
                ch.Ring(new Vector3(0, -0.02f, 0), 0.62f, 0.03f, c, default, Surface.Metal);
                ch.Ring(new Vector3(0, 0.12f, 0), 0.38f, 0.025f, c, default, Surface.Metal);
                for (int i = 0; i < 8; i++)
                {
                    var dir = Quaternion.Euler(0, i * 45f, 0) * Vector3.forward;
                    ch.Box(dir * 0.16f + new Vector3(0, 0.0f, 0), new Vector3(0.018f, 0.018f, 0.3f), c, new Vector3(0, i * 45f, 0), Surface.Metal);
                    ch.Cyl(dir * 0.3f + new Vector3(0, 0.02f, 0), 0.05f, 0.03f, c, default, Surface.Metal);
                    ch.Cyl(dir * 0.3f + new Vector3(0, 0.07f, 0), 0.022f, 0.07f, White, default, Surface.Emissive, 0.6f);
                    // crystal drops
                    ch.Box(dir * 0.31f + new Vector3(0, -0.09f, 0), new Vector3(0.03f, 0.06f, 0.03f), H(0xDDF4FF), new Vector3(0, 45f, 0), Surface.Emissive, 0.9f);
                    ch.Box(Quaternion.Euler(0, 22.5f, 0) * dir * 0.2f + new Vector3(0, 0.02f, 0), new Vector3(0.025f, 0.05f, 0.025f), H(0xDDF4FF), new Vector3(0, 45f, 0), Surface.Emissive, 0.9f);
                }
                ch.Box(new Vector3(0, -0.14f, 0), new Vector3(0.05f, 0.1f, 0.05f), H(0xDDF4FF), new Vector3(0, 45f, 0), Surface.Emissive, 1.1f);
                // the dent
                ch.Ball(new Vector3(0.29f, -0.02f, 0.05f), new Vector3(0.06f, 0.025f, 0.06f), Mul(c, 0.7f), default, Surface.Metal);
                return true;
            }
            case "tiara":                                  // Great-Grandmama's Tiara
            {
                var ti = k.In(Vector3.zero, new Vector3(0, 0, 0), 1f);
                ti.Ring(Vector3.zero, 0.16f, 0.012f, c, new Vector3(0, 0, 0), Surface.Metal);
                float[] heights = { 0.035f, 0.05f, 0.075f, 0.05f, 0.035f };
                for (int i = 0; i < 5; i++)
                {
                    float a = Mathf.Lerp(-60f, 60f, i / 4f);
                    var dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                    var p = dir * 0.078f;
                    ti.Cone(p + new Vector3(0, heights[i] * 0.5f, 0), 0.03f, heights[i], c, new Vector3(0, a, 0), Surface.Metal);
                    ti.Box(p + new Vector3(0, heights[i] * 0.45f, 0.004f), new Vector3(0.016f, 0.016f, 0.016f), i == 2 ? H(0x9AE8FF) : H(0xFF6AA8), new Vector3(45f, a, 45f), Surface.Emissive, 1.1f);
                }
                ti.Ball(new Vector3(0, 0.1f, 0.078f), 0.026f, H(0xDDF4FF), Surface.Emissive, 1.3f);
                return true;
            }
            case "harddrive":                              // Hard Drive Full of NFTs
                k.Box(Vector3.zero, new Vector3(0.12f, 0.03f, 0.18f), c);
                k.Box(new Vector3(0, 0.016f, 0), new Vector3(0.11f, 0.002f, 0.17f), Mul(c, 1.4f));
                k.Box(new Vector3(0.045f, 0.0f, 0.091f), new Vector3(0.012f, 0.008f, 0.004f), H(0x40C8FF), default, Surface.Emissive, 1.6f);
                k.Box(new Vector3(-0.03f, 0.0f, 0.091f), new Vector3(0.03f, 0.012f, 0.004f), DarkGrey);
                // a cartoon ape sticker (bored, obviously)
                k.Cyl(new Vector3(0, 0.018f, -0.01f), 0.075f, 0.003f, H(0x8A5A30));
                k.Cyl(new Vector3(0, 0.0195f, -0.02f), 0.045f, 0.002f, H(0xD8B080));
                k.Box(new Vector3(-0.012f, 0.021f, -0.008f), new Vector3(0.012f, 0.002f, 0.004f), Black);
                k.Box(new Vector3(0.012f, 0.021f, -0.008f), new Vector3(0.012f, 0.002f, 0.004f), Black);
                k.Box(new Vector3(0, 0.021f, -0.03f), new Vector3(0.025f, 0.002f, 0.004f), Black);
                k.Box(new Vector3(0, 0.0195f, 0.03f), new Vector3(0.06f, 0.002f, 0.012f), H(0x40C8FF));
                return true;
            case "toiletseat_gold":                        // Solid Gold Toilet Seat
                k.P("torus30", new Vector3(0, 0, 0), new Vector3(0.38f, 0.32f, 0.45f), c, default, Surface.Metal, 0f);
                k.Box(new Vector3(0, 0.01f, -0.24f), new Vector3(0.14f, 0.03f, 0.04f), Mul(c, 0.85f), default, Surface.Metal);
                // lid, flipped open
                k.Ball(new Vector3(0, 0.2f, -0.27f), new Vector3(0.38f, 0.44f, 0.03f), c, new Vector3(-12f, 0, 0), Surface.Metal);
                k.Box(new Vector3(0.0f, 0.2f, -0.255f), new Vector3(0.16f, 0.06f, 0.004f), White, new Vector3(-12f, 0, 0), Surface.Emissive, 0.5f);
                return true;
            case "keys_yacht":                             // Keys to a Yacht (Which Yacht?)
                k.Ring(Vector3.zero, 0.05f, 0.006f, Silver, default, Surface.Metal);
                for (int i = 0; i < 2; i++)
                {
                    var kk = k.In(new Vector3(0.02f + i * 0.012f, 0, 0.04f + i * 0.01f), new Vector3(0, 15f - i * 40f, 0));
                    kk.Cyl(Vector3.zero, 0.035f, 0.006f, Gold, default, Surface.Metal);
                    kk.Box(new Vector3(0, 0, 0.045f), new Vector3(0.012f, 0.004f, 0.06f), Silver, default, Surface.Metal);
                    kk.Box(new Vector3(0.006f, 0, 0.06f), new Vector3(0.008f, 0.004f, 0.006f), Silver, default, Surface.Metal);
                }
                // the foam float keychain
                k.Cap(new Vector3(-0.06f, 0.005f, -0.03f), 0.04f, 0.11f, c, new Vector3(90f, 30f, 0));
                k.Cap(new Vector3(-0.06f, 0.006f, -0.03f), 0.041f, 0.04f, White, new Vector3(90f, 30f, 0));
                return true;
            case "dinoegg":                                // Genuine* Dinosaur Egg
            {
                k.Lathe("egg", new Vector3(0, 0.04f, 0), new Vector3(0.24f, 0.32f, 0.24f), c);
                for (int i = 0; i < 14; i++)
                {
                    float a = i * 137.5f, y = -0.08f + (i % 7) * 0.035f;
                    float r = 0.112f * Mathf.Sqrt(Mathf.Max(0.1f, 1f - Mathf.Pow((y - 0.04f) / 0.17f, 2f)));
                    var p = Quaternion.Euler(0, a, 0) * new Vector3(r, 0, 0) + new Vector3(0, y + 0.04f, 0);
                    k.Ball(p, new Vector3(0.03f, 0.024f, 0.03f), Mul(c, 0.55f));
                }
                k.Cyl(new Vector3(0, -0.13f, 0), 0.18f, 0.03f, Brown);
                k.Box(new Vector3(0.1f, -0.12f, 0.06f), new Vector3(0.07f, 0.004f, 0.05f), OffWhite, new Vector3(0, -30f, 10f));   // the certificate
                return true;
            }
            case "ticket_space":                           // Unused Space Tourism Ticket
                k.Box(Vector3.zero, new Vector3(0.2f, 0.004f, 0.08f), c);
                for (int i = 0; i < 9; i++)
                    k.Box(new Vector3(-0.09f + (i * 0.023f) % 0.18f, 0.003f, -0.03f + ((i * 37) % 60) * 0.001f), new Vector3(0.005f, 0.002f, 0.005f), White, new Vector3(0, 45f, 0), Surface.Emissive, 1f);
                k.Box(new Vector3(0.03f, 0.003f, 0.0f), new Vector3(0.07f, 0.002f, 0.018f), White);
                k.Cone(new Vector3(0.075f, 0.003f, 0f), 0.018f, 0.025f, Red, new Vector3(0, 0, -90f));
                k.Box(new Vector3(-0.07f, 0.003f, 0.024f), new Vector3(0.04f, 0.002f, 0.022f), Gold, default, Surface.Metal);
                k.Box(new Vector3(-0.055f, 0.0f, 0f), new Vector3(0.002f, 0.0045f, 0.075f), Grey);    // perforation
                return true;
            case "deed_island":                            // Deed to a Private Island (Very Small)
                k.Cyl(Vector3.zero, 0.05f, 0.26f, c, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(0.135f, 0, 0), 0.06f, 0.012f, Mul(c, 0.85f), new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(-0.135f, 0, 0), 0.06f, 0.012f, Mul(c, 0.85f), new Vector3(0, 0, 90f));
                k.Box(new Vector3(0.04f, -0.02f, 0.05f), new Vector3(0.1f, 0.003f, 0.07f), c, new Vector3(0, 0, -10f));
                k.Cyl(new Vector3(0, 0, 0.026f), 0.035f, 0.008f, H(0xB01E1E), new Vector3(90f, 0, 0));     // wax seal
                k.Cyl(new Vector3(0, 0, 0), 0.052f, 0.02f, H(0xB01E1E), new Vector3(0, 0, 90f));            // ribbon
                // the island itself, on a tiny cardboard stand: sand, one palm tree, one flag
                k.Ball(new Vector3(0.07f, 0.04f, 0), new Vector3(0.07f, 0.025f, 0.07f), H(0xF2D898));
                k.Cyl(new Vector3(0.07f, 0.075f, 0), 0.007f, 0.06f, Brown, new Vector3(0, 0, -8f));
                k.Cone(new Vector3(0.075f, 0.11f, 0), 0.06f, 0.02f, Green);
                k.Box(new Vector3(0.05f, 0.075f, 0.02f), new Vector3(0.002f, 0.04f, 0.002f), Grey);
                k.Box(new Vector3(0.058f, 0.088f, 0.02f), new Vector3(0.016f, 0.012f, 0.001f), Red);
                return true;
            case "statue_face":                            // Golden Statue of the Owner's Face
                k.Box(new Vector3(0, -0.22f, 0), new Vector3(0.26f, 0.12f, 0.26f), H(0x2A2A30));
                k.Box(new Vector3(0, -0.22f, 0.131f), new Vector3(0.14f, 0.05f, 0.004f), c, default, Surface.Metal);
                k.Ball(new Vector3(0, -0.11f, 0), new Vector3(0.3f, 0.14f, 0.2f), c, default, Surface.Metal);          // shoulders
                k.Ball(new Vector3(0, 0.08f, 0), new Vector3(0.26f, 0.3f, 0.26f), c, default, Surface.Metal);           // head
                k.Ball(new Vector3(0, 0.07f, 0.13f), new Vector3(0.05f, 0.08f, 0.06f), Mul(c, 1.05f), default, Surface.Metal); // nose
                k.Ball(new Vector3(0, -0.02f, 0.1f), new Vector3(0.16f, 0.07f, 0.1f), c, default, Surface.Metal);        // chin
                k.Box(new Vector3(0, 0.0f, 0.13f), new Vector3(0.09f, 0.012f, 0.01f), Mul(c, 0.6f), new Vector3(0, 0, -6f), Surface.Metal); // smug smirk
                k.Ball(new Vector3(-0.05f, 0.12f, 0.115f), new Vector3(0.04f, 0.025f, 0.02f), Mul(c, 0.7f), default, Surface.Metal);
                k.Ball(new Vector3(0.05f, 0.12f, 0.115f), new Vector3(0.04f, 0.025f, 0.02f), Mul(c, 0.7f), default, Surface.Metal);
                k.Ball(new Vector3(0, 0.2f, -0.01f), new Vector3(0.27f, 0.1f, 0.26f), Mul(c, 0.95f), default, Surface.Metal); // the hair. it's also gold
                return true;

            // ------------------------------------------------------------ epics
            case "sneakers":                               // Air Jordumb Retro Sneakers
                for (int s = -1; s <= 1; s += 2)
                {
                    var sh = k.In(new Vector3(s * 0.065f, 0, s * 0.02f), new Vector3(0, s * 8f, 0));
                    sh.Box(new Vector3(0, -0.04f, 0), new Vector3(0.1f, 0.03f, 0.29f), White);
                    sh.Box(new Vector3(0, -0.045f, 0), new Vector3(0.102f, 0.012f, 0.292f), H(0xE8E0C8));
                    sh.Cap(new Vector3(0, 0.0f, 0.03f), 0.095f, 0.25f, c, new Vector3(90f, 0, 0));
                    sh.Box(new Vector3(0, 0.05f, -0.08f), new Vector3(0.096f, 0.14f, 0.11f), c);
                    sh.Box(new Vector3(0, 0.12f, -0.085f), new Vector3(0.1f, 0.025f, 0.115f), White);
                    sh.Box(new Vector3(0.049f, 0.01f, 0.0f), new Vector3(0.004f, 0.025f, 0.15f), Black, new Vector3(20f, 0, 0));   // the swoosh-ish
                    sh.Box(new Vector3(0, 0.055f, 0.0f), new Vector3(0.04f, 0.01f, 0.1f), White, new Vector3(-15f, 0, 0));        // laces
                    sh.Box(new Vector3(0, 0.0f, 0.13f), new Vector3(0.096f, 0.04f, 0.05f), White);
                }
                return true;
            case "guitar":                                 // Guitar Signed by 'Bon Jovial'
            {
                var gt = k.In(Vector3.zero, new Vector3(-90f, 0, 0));
                gt.Ball(new Vector3(0, -0.2f, 0), new Vector3(0.36f, 0.36f, 0.1f), c);
                gt.Ball(new Vector3(0, -0.02f, 0), new Vector3(0.27f, 0.25f, 0.1f), c);
                gt.Cyl(new Vector3(0, -0.07f, 0.045f), 0.09f, 0.012f, Black, new Vector3(90f, 0, 0));
                gt.Box(new Vector3(0, 0.27f, 0.03f), new Vector3(0.05f, 0.5f, 0.025f), Brown);
                gt.Box(new Vector3(0, 0.55f, 0.03f), new Vector3(0.075f, 0.11f, 0.02f), Black);
                gt.Box(new Vector3(0, -0.25f, 0.052f), new Vector3(0.09f, 0.02f, 0.01f), Black);
                for (int i = 0; i < 6; i++)
                    gt.Box(new Vector3(-0.015f + i * 0.006f, 0.1f, 0.047f), new Vector3(0.0015f, 0.7f, 0.002f), Silver, default, Surface.Metal);
                // the "signature" (ketchup?)
                gt.Box(new Vector3(0.06f, -0.22f, 0.052f), new Vector3(0.09f, 0.008f, 0.002f), H(0xD8231F), new Vector3(0, 0, 25f));
                gt.Box(new Vector3(0.05f, -0.25f, 0.052f), new Vector3(0.07f, 0.008f, 0.002f), H(0xD8231F), new Vector3(0, 0, -15f));
                return true;
            }
            case "handbag":                                // 'Louis Vuitto' Handbag
                k.Box(Vector3.zero, new Vector3(0.32f, 0.22f, 0.14f), c);
                k.Box(new Vector3(0, 0.115f, 0), new Vector3(0.3f, 0.02f, 0.13f), Mul(c, 0.8f));
                k.Ring(new Vector3(0, 0.17f, 0), 0.2f, 0.02f, Mul(c, 0.7f), new Vector3(90f, 0, 0));
                k.Box(new Vector3(0, 0.07f, 0.071f), new Vector3(0.05f, 0.035f, 0.006f), Gold, default, Surface.Metal);
                for (int i = 0; i < 12; i++)
                    k.Box(new Vector3(-0.13f + (i % 6) * 0.052f, -0.06f + (i / 6) * 0.08f, 0.0705f), new Vector3(0.018f, 0.018f, 0.002f), H(0xD8B060), new Vector3(0, 0, 45f));
                return true;
            case "wine_fancy":                             // Chateau Le Fance 1961
                k.Lathe("wine", Vector3.zero, new Vector3(0.085f, 0.33f, 0.085f), c, default, Surface.Metal);   // dark glass reads better opaque
                k.Cyl(new Vector3(0, -0.045f, 0), 0.087f, 0.09f, H(0xF2E6C8));
                k.Box(new Vector3(0, -0.035f, 0.044f), new Vector3(0.05f, 0.022f, 0.004f), H(0x6A1424));
                k.Box(new Vector3(0, -0.07f, 0.044f), new Vector3(0.04f, 0.008f, 0.004f), Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.145f, 0), 0.034f, 0.06f, Gold, default, Surface.Metal);
                k.Ball(new Vector3(0.025f, 0.07f, 0.03f), new Vector3(0.03f, 0.05f, 0.012f), new Color(0.75f, 0.72f, 0.66f), default, Surface.Base);   // a dust bunny
                return true;
            case "painting_poodle":                        // Oil Portrait of a Pampered Poodle
            {
                var pp = k.In(Vector3.zero, new Vector3(-90f, 0, 0));
                pp.Box(Vector3.zero, new Vector3(0.46f, 0.56f, 0.012f), H(0x6A3A8A));
                for (int side = 0; side < 2; side++)
                {
                    var f = pp.In(Vector3.zero, new Vector3(0, side * 180f, 0));
                    f.Box(new Vector3(0, -0.16f, 0.007f), new Vector3(0.44f, 0.22f, 0.003f), H(0x8A1E3A));       // velvet cushion
                    f.Ball(new Vector3(0, -0.06f, 0.012f), new Vector3(0.2f, 0.17f, 0.02f), c);                     // body
                    f.Ball(new Vector3(0, 0.08f, 0.014f), new Vector3(0.14f, 0.14f, 0.02f), c);                     // head
                    f.Ball(new Vector3(0, 0.17f, 0.014f), new Vector3(0.1f, 0.09f, 0.02f), c);                      // topknot
                    f.Ball(new Vector3(-0.08f, 0.06f, 0.014f), new Vector3(0.06f, 0.11f, 0.02f), c);                // ears
                    f.Ball(new Vector3(0.08f, 0.06f, 0.014f), new Vector3(0.06f, 0.11f, 0.02f), c);
                    f.Ball(new Vector3(-0.03f, 0.09f, 0.022f), 0.018f, Black);
                    f.Ball(new Vector3(0.03f, 0.09f, 0.022f), 0.018f, Black);
                    f.Ball(new Vector3(0, 0.05f, 0.024f), new Vector3(0.025f, 0.018f, 0.01f), Black);
                    f.Box(new Vector3(0, 0.205f, 0.024f), new Vector3(0.05f, 0.02f, 0.005f), Pink);                // bow
                    f.Ball(new Vector3(0, -0.02f, 0.024f), new Vector3(0.12f, 0.025f, 0.01f), H(0xFF8FC8));        // pearl collar
                }
                pp.Box(new Vector3(0, 0.3f, 0), new Vector3(0.56f, 0.05f, 0.045f), Gold, default, Surface.Metal);
                pp.Box(new Vector3(0, -0.3f, 0), new Vector3(0.56f, 0.05f, 0.045f), Gold, default, Surface.Metal);
                pp.Box(new Vector3(0.255f, 0, 0), new Vector3(0.05f, 0.56f, 0.045f), Gold, default, Surface.Metal);
                pp.Box(new Vector3(-0.255f, 0, 0), new Vector3(0.05f, 0.56f, 0.045f), Gold, default, Surface.Metal);
                for (int i = 0; i < 4; i++)
                    pp.Ball(new Vector3((i % 2 == 0 ? -1 : 1) * 0.255f, (i < 2 ? -1 : 1) * 0.3f, 0), 0.06f, Gold, Surface.Metal);
                return true;
            }
            case "putter":                                 // Gold-Plated Putter
            {
                var pt = k.In(Vector3.zero, new Vector3(0, 0, 90f));
                pt.Cyl(new Vector3(0, 0.05f, 0), 0.016f, 0.8f, c, default, Surface.Metal);
                pt.Cyl(new Vector3(0, 0.38f, 0), 0.03f, 0.18f, Black);
                pt.Box(new Vector3(0.02f, -0.37f, 0), new Vector3(0.05f, 0.04f, 0.12f), c, default, Surface.Metal);
                pt.Box(new Vector3(0.046f, -0.37f, 0), new Vector3(0.004f, 0.025f, 0.08f), White, default, Surface.Emissive, 0.6f);
                return true;
            }
            case "monocle":                                // Emerald Monocle
                k.Ring(Vector3.zero, 0.07f, 0.01f, Gold, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ball(Vector3.zero, new Vector3(0.058f, 0.058f, 0.012f), c, default, Surface.Emissive, 0.9f);
                k.Ball(new Vector3(0.012f, 0.012f, 0.006f), new Vector3(0.012f, 0.012f, 0.004f), White, default, Surface.Emissive, 1.5f);
                for (int i = 0; i < 6; i++)
                    k.Ring(new Vector3(0.04f + i * 0.012f, -0.03f - i * 0.008f, 0), 0.012f, 0.003f, Gold, new Vector3(i % 2 == 0 ? 0 : 90f, 0, 30f), Surface.Metal);
                return true;
            case "dogcollar":                              // Diamond Dog Collar
                k.Ring(Vector3.zero, 0.17f, 0.03f, c);
                for (int i = 0; i < 12; i++)
                {
                    var p = Quaternion.Euler(0, i * 30f, 0) * new Vector3(0.085f, 0.0f, 0);
                    k.Box(p + new Vector3(0, 0.012f, 0), new Vector3(0.014f, 0.014f, 0.014f), H(0xE8F8FF), new Vector3(45f, i * 30f, 45f), Surface.Emissive, 1.2f);
                }
                k.Cyl(new Vector3(0, -0.03f, 0.1f), 0.045f, 0.006f, Gold, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ring(new Vector3(0, -0.002f, 0.09f), 0.02f, 0.004f, Gold, new Vector3(90f, 0, 0), Surface.Metal);
                return true;
            case "tamagochu_plat":                         // Platinum Tamagochu (1 of 3)
            {
                var tg = k.In(Vector3.zero, new Vector3(-70f, 0, 0), 1.2f);
                tg.Lathe("egg", Vector3.zero, new Vector3(0.12f, 0.14f, 0.07f), c, default, Surface.Metal);
                tg.Box(new Vector3(0, 0.012f, 0.03f), new Vector3(0.06f, 0.05f, 0.012f), H(0x2A2A30));
                tg.Box(new Vector3(0, 0.012f, 0.036f), new Vector3(0.048f, 0.04f, 0.004f), H(0xD8C8FF), default, Surface.Emissive, 0.5f);
                tg.Box(new Vector3(-0.005f, 0.014f, 0.039f), new Vector3(0.012f, 0.012f, 0.002f), Black);
                tg.Box(new Vector3(0.008f, 0.026f, 0.039f), new Vector3(0.016f, 0.006f, 0.002f), Gold);           // its tiny crown
                for (int i = 0; i < 3; i++)
                    tg.Box(new Vector3(-0.022f + i * 0.022f, -0.035f, 0.032f), new Vector3(0.012f, 0.012f, 0.008f), H(0xDDF4FF), new Vector3(0, 0, 45f), Surface.Emissive, 1f);
                tg.Ring(new Vector3(0, 0.08f, 0), 0.035f, 0.007f, Gold, new Vector3(90f, 0, 0), Surface.Metal);
                return true;
            }
            case "toupee":                                 // A-List Toupee (Signed Inside)
                k.Lathe("dome", Vector3.zero, new Vector3(0.2f, 0.06f, 0.24f), c);
                for (int i = 0; i < 5; i++)
                    k.Cap(new Vector3(-0.06f + i * 0.03f, 0.025f, 0.08f), 0.035f, 0.12f, Mul(c, 1f + (i % 2) * 0.08f), new Vector3(-70f, 0, -35f + i * 6f));
                k.Ball(new Vector3(0.06f, 0.04f, 0.1f), new Vector3(0.09f, 0.04f, 0.06f), Mul(c, 1.1f));        // THE swoop
                k.Box(new Vector3(0, -0.03f, 0), new Vector3(0.05f, 0.004f, 0.03f), Gold, default, Surface.Emissive, 0.8f);
                return true;

            // ------------------------------------------------------------ rares
            case "bikeseat":                               // Pedaltron Smart-Bike Seat
                k.Ball(new Vector3(0, 0.03f, -0.03f), new Vector3(0.17f, 0.05f, 0.14f), c);
                k.Ball(new Vector3(0, 0.03f, 0.08f), new Vector3(0.07f, 0.045f, 0.16f), c);
                k.Box(new Vector3(0, 0.0f, 0.02f), new Vector3(0.05f, 0.02f, 0.2f), Silver, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.12f, 0.0f), 0.028f, 0.22f, Silver, default, Surface.Metal);
                k.Box(new Vector3(0.0f, 0.052f, -0.05f), new Vector3(0.04f, 0.004f, 0.03f), Red, default, Surface.Emissive, 0.8f);
                return true;
            case "drone":                                  // Crashed Camera Drone
                k.Box(Vector3.zero, new Vector3(0.14f, 0.05f, 0.18f), c);
                for (int i = 0; i < 4; i++)
                {
                    var dir = Quaternion.Euler(0, 45f + i * 90f, 0) * Vector3.forward;
                    k.Box(dir * 0.12f, new Vector3(0.025f, 0.02f, 0.14f), DarkGrey, new Vector3(i == 2 ? 15f : 0f, 45f + i * 90f, i == 2 ? 20f : 0f));
                    if (i != 3) k.Cyl(dir * 0.2f + new Vector3(0, 0.02f, 0), 0.13f, 0.004f, new Color(0.3f, 0.3f, 0.35f, 0.5f), new Vector3(i == 1 ? 25f : 0f, 0, 0), Surface.Glass);
                }
                k.Ball(new Vector3(0, -0.035f, 0.08f), 0.045f, Black);
                k.Ball(new Vector3(0, -0.035f, 0.1f), new Vector3(0.025f, 0.025f, 0.01f), H(0x6AA8D8), default, Surface.Emissive, 0.6f);
                k.Ball(new Vector3(0.03f, 0.03f, -0.05f), new Vector3(0.05f, 0.012f, 0.04f), Green);              // a leaf. from the tree.
                return true;
            case "vrheadset":                              // Occulus Wift VR Headset
                k.Box(Vector3.zero, new Vector3(0.19f, 0.11f, 0.1f), c);
                k.Box(new Vector3(0, 0, 0.051f), new Vector3(0.18f, 0.1f, 0.004f), Black, default, Surface.Metal);
                k.Box(new Vector3(0, 0, -0.06f), new Vector3(0.16f, 0.08f, 0.03f), DarkGrey);
                k.Ring(new Vector3(0, 0, -0.12f), 0.2f, 0.025f, DarkGrey, new Vector3(0, 0, 0));
                k.Box(new Vector3(0.06f, 0.03f, 0.054f), new Vector3(0.01f, 0.01f, 0.003f), H(0x40C8FF), default, Surface.Emissive, 1.2f);
                return true;
            case "caviar":                                 // Tin of Beluga-ish Caviar
                k.Cyl(Vector3.zero, 0.11f, 0.045f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.024f, 0), 0.112f, 0.008f, Gold, default, Surface.Metal);
                k.Box(new Vector3(0, 0.03f, 0), new Vector3(0.06f, 0.002f, 0.02f), White);
                k.Ball(new Vector3(0, 0.0f, 0.06f), new Vector3(0.06f, 0.02f, 0.012f), H(0xC8A040), default, Surface.Metal);   // little fish emblem
                return true;
            case "teaset":                                 // Sterling Silver Tea Set
                k.Box(new Vector3(0, -0.06f, 0), new Vector3(0.36f, 0.015f, 0.24f), c, default, Surface.Metal);       // tray
                k.Lathe("pot", new Vector3(-0.06f, 0.02f, 0), new Vector3(0.13f, 0.14f, 0.13f), c, default, Surface.Metal);
                k.Lathe("dome", new Vector3(-0.06f, 0.1f, 0), new Vector3(0.11f, 0.04f, 0.11f), c, default, Surface.Metal);
                k.Cone(new Vector3(0.025f, 0.03f, 0), 0.03f, 0.1f, c, new Vector3(0, 0, -55f), Surface.Metal);         // spout
                k.Ring(new Vector3(-0.14f, 0.03f, 0), 0.07f, 0.012f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Lathe("cup", new Vector3(0.1f, -0.025f, 0.06f), new Vector3(0.06f, 0.05f, 0.06f), c, default, Surface.Metal);
                k.Lathe("cup", new Vector3(0.1f, -0.025f, -0.06f), new Vector3(0.06f, 0.05f, 0.06f), c, default, Surface.Metal);
                k.Box(new Vector3(0.13f, -0.02f, 0.06f), new Vector3(0.02f, 0.005f, 0.008f), c, new Vector3(0, 0, 40f), Surface.Metal);   // the snapped handle
                return true;
            case "trophy_cup":                             // Polo Championship Cup
                k.Box(new Vector3(0, -0.17f, 0), new Vector3(0.14f, 0.07f, 0.14f), H(0x2A2A30));
                k.Lathe("trophy", new Vector3(0, 0.02f, 0), new Vector3(0.2f, 0.32f, 0.2f), c, default, Surface.Metal);
                k.Ring(new Vector3(0.12f, 0.1f, 0), 0.1f, 0.016f, c, new Vector3(90f, 0, 0), Surface.Metal);
                k.Ring(new Vector3(-0.12f, 0.1f, 0), 0.1f, 0.016f, c, new Vector3(90f, 0, 0), Surface.Metal);
                // a tiny horse on the lid (it won)
                k.Cap(new Vector3(0, 0.22f, 0), 0.03f, 0.07f, Brown, new Vector3(0, 0, 90f));
                k.Ball(new Vector3(0.04f, 0.24f, 0), new Vector3(0.025f, 0.03f, 0.02f), Brown);
                k.Box(new Vector3(0, -0.17f, 0.071f), new Vector3(0.08f, 0.03f, 0.004f), Gold, default, Surface.Metal);
                return true;

            // ------------------------------------------------------------ uncommons
            case "airfryer":                               // Air Fryer XXL (Used Once)
                k.Lathe("pot", Vector3.zero, new Vector3(0.28f, 0.32f, 0.28f), c);
                k.Box(new Vector3(0, -0.06f, 0.12f), new Vector3(0.18f, 0.12f, 0.06f), Mul(c, 0.8f));
                k.Box(new Vector3(0, -0.06f, 0.16f), new Vector3(0.08f, 0.025f, 0.04f), DarkGrey);
                k.Box(new Vector3(0, 0.1f, 0.128f), new Vector3(0.12f, 0.05f, 0.004f), H(0x40C8FF), new Vector3(-20f, 0, 0), Surface.Emissive, 0.8f);
                k.Ball(new Vector3(0.0f, 0.18f, 0.0f), new Vector3(0.05f, 0.03f, 0.04f), H(0xE8B050));                  // the one nugget
                return true;
            case "tumbler":                                // Yeet-i Mega Tumbler
                k.Lathe("cup", Vector3.zero, new Vector3(0.11f, 0.3f, 0.11f), c, new Vector3(180f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, 0.16f, 0), 0.115f, 0.03f, DarkGrey);
                k.Cyl(new Vector3(0.025f, 0.23f, 0), 0.012f, 0.14f, H(0xF2F2F2), new Vector3(0, 0, -8f));             // straw
                k.Ring(new Vector3(0.07f, 0.02f, 0), 0.1f, 0.016f, DarkGrey, new Vector3(90f, 0, 0));
                return true;
            case "oilcrate":                               // Crate of Essential Oils (MLM)
                k.Box(new Vector3(0, -0.04f, 0), new Vector3(0.28f, 0.08f, 0.2f), c);
                for (int i = 0; i < 6; i++)
                {
                    var p = new Vector3(-0.09f + (i % 3) * 0.09f, 0.03f, -0.05f + (i / 3) * 0.1f);
                    Color[] oils = { H(0x8A4AD8), H(0x2FB84A), H(0xE8B030), H(0xD8402A), H(0x3C78D8), H(0xF06FA8) };
                    k.Cyl(p, 0.045f, 0.09f, oils[i], default, Surface.Glass);
                    k.Cyl(p + new Vector3(0, 0.055f, 0), 0.03f, 0.025f, Black);
                }
                k.Box(new Vector3(0, -0.04f, 0.101f), new Vector3(0.14f, 0.04f, 0.004f), White);
                return true;
            case "trophy_big":                             // Fantasy Football League Trophy
                k.Box(new Vector3(0, -0.2f, 0), new Vector3(0.18f, 0.08f, 0.18f), H(0x1E1E24));
                k.Cyl(new Vector3(0, -0.1f, 0), 0.04f, 0.14f, c, default, Surface.Metal);
                k.Ball(new Vector3(0, 0.04f, 0), new Vector3(0.18f, 0.12f, 0.12f), H(0x8A4A22), new Vector3(0, 0, 30f));   // a football
                k.Box(new Vector3(0, 0.04f, 0.061f), new Vector3(0.06f, 0.008f, 0.004f), White, new Vector3(0, 0, 30f));
                k.Box(new Vector3(0, -0.2f, 0.091f), new Vector3(0.12f, 0.035f, 0.004f), Gold, default, Surface.Metal);
                k.Box(new Vector3(0.0f, 0.13f, 0.0f), new Vector3(0.12f, 0.05f, 0.01f), c, default, Surface.Metal);       // "CHAMP"
                return true;
            case "robe":                                   // Monogrammed Silk Robe
                k.Box(Vector3.zero, new Vector3(0.36f, 0.06f, 0.28f), c, new Vector3(0, 5f, 0));
                k.Box(new Vector3(0, 0.035f, 0.02f), new Vector3(0.34f, 0.03f, 0.26f), Mul(c, 1.15f), new Vector3(0, -6f, 0));
                k.Box(new Vector3(0, 0.055f, -0.02f), new Vector3(0.05f, 0.01f, 0.3f), H(0xE8C060), new Vector3(0, 30f, 0));   // the belt
                k.Box(new Vector3(0.08f, 0.054f, 0.07f), new Vector3(0.07f, 0.004f, 0.03f), H(0xE8C060));                       // "HWT III"
                return true;

            // ------------------------------------------------------------ district non-valuables (§5.8)
            case "kombucha":
                k.Lathe("bottle", Vector3.zero, new Vector3(0.08f, 0.26f, 0.08f), c, default, Surface.Glass);
                k.Ball(new Vector3(0, 0.04f, 0), new Vector3(0.06f, 0.05f, 0.06f), H(0xB8904A));      // the "mother" floating in there
                k.Cyl(new Vector3(0, -0.01f, 0), 0.082f, 0.07f, H(0xF2E8D0));
                k.Cyl(new Vector3(0, 0.135f, 0), 0.032f, 0.02f, Black);
                return true;
            case "milkcarton":
                k.Box(Vector3.zero, new Vector3(0.09f, 0.2f, 0.09f), c);
                k.Box(new Vector3(0, 0.12f, 0.022f), new Vector3(0.09f, 0.06f, 0.004f), Mul(c, 0.95f), new Vector3(-40f, 0, 0));
                k.Box(new Vector3(0, 0.12f, -0.022f), new Vector3(0.09f, 0.06f, 0.004f), Mul(c, 0.95f), new Vector3(40f, 0, 0));
                k.Box(new Vector3(0, 0.155f, 0), new Vector3(0.09f, 0.02f, 0.008f), Mul(c, 0.9f));
                k.Box(new Vector3(0, 0.0f, 0.046f), new Vector3(0.07f, 0.08f, 0.002f), H(0xB8945A));
                return true;
            case "pods":                                   // coffee pods (plastic) / espresso capsules (metal)
                for (int i = 0; i < 7; i++)
                {
                    var p = Quaternion.Euler(0, i * 51f, 0) * new Vector3(i == 0 ? 0f : 0.045f, 0.015f * (i % 3), 0);
                    k.Lathe("cup", p, new Vector3(0.04f, 0.04f, 0.04f), i % 3 == 0 ? Mul(c, 1.2f) : c, new Vector3(i * 30f, 0, 160f + i * 8f), Surface.Metal);
                    k.Cyl(p + Quaternion.Euler(i * 30f, 0, 160f + i * 8f) * new Vector3(0, -0.021f, 0), 0.045f, 0.003f, Silver, new Vector3(i * 30f, 0, 160f + i * 8f), Surface.Metal);
                }
                return true;
            case "boxpile":                                // Mountain of Scrapazon Boxes
                for (int i = 0; i < 4; i++)
                {
                    var p = new Vector3(-0.06f + (i % 2) * 0.13f, -0.05f + (i / 2) * 0.13f, (i % 3) * 0.02f);
                    var size = new Vector3(0.2f - i * 0.02f, 0.12f, 0.16f - i * 0.01f);
                    k.Box(p, size, Mul(c, 1f - i * 0.04f), new Vector3(0, i * 17f, 0));
                    k.Box(p + new Vector3(0, 0.0f, size.z * 0.5f + 0.001f), new Vector3(0.06f, 0.012f, 0.002f), H(0x1E1E24), new Vector3(0, i * 17f, 0));   // the smile
                }
                return true;
            case "spring":
                for (int i = 0; i < 7; i++)
                    k.Ring(new Vector3(0, -0.06f + i * 0.02f, 0), 0.04f, 0.008f, c, new Vector3(8f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, 0.1f, 0), 0.008f, 0.06f, c, new Vector3(0, 0, 30f), Surface.Metal);   // the snapped end
                return true;
            case "leggings":                               // 400 pairs of Galaxy Taco leggings
                for (int i = 0; i < 4; i++)
                {
                    k.Box(new Vector3(0, -0.04f + i * 0.03f, 0), new Vector3(0.26f, 0.028f, 0.18f), i % 2 == 0 ? c : Mul(c, 1.3f), new Vector3(0, i * 9f, 0));
                    k.Ball(new Vector3(-0.05f + i * 0.03f, -0.025f + i * 0.03f, 0.03f), new Vector3(0.03f, 0.004f, 0.03f), Yellow, default, Surface.Emissive, 0.6f);   // galaxy taco
                }
                return true;
            case "trophies":                               // Bag of Participation Trophies
                for (int i = 0; i < 3; i++)
                {
                    var tp = k.In(new Vector3(-0.07f + i * 0.07f, 0, (i % 2) * 0.03f), new Vector3(0, 0, -10f + i * 12f), 0.8f);
                    tp.Box(new Vector3(0, -0.09f, 0), new Vector3(0.07f, 0.03f, 0.07f), Blue);
                    tp.Cyl(new Vector3(0, -0.04f, 0), 0.015f, 0.07f, c);
                    tp.Ball(new Vector3(0, 0.02f, 0), new Vector3(0.05f, 0.07f, 0.03f), c);
                }
                return true;
            case "champagne":
                k.Lathe("wine", Vector3.zero, new Vector3(0.09f, 0.32f, 0.09f), c, default, Surface.Metal);
                k.Cyl(new Vector3(0, 0.12f, 0), 0.04f, 0.08f, Gold, default, Surface.Metal);
                k.Cyl(new Vector3(0, -0.04f, 0), 0.092f, 0.07f, H(0xF2EAD0));
                k.Box(new Vector3(0, -0.04f, 0.046f), new Vector3(0.04f, 0.02f, 0.004f), Gold, default, Surface.Metal);
                return true;
            case "shreds":                                 // Shredded Tax Documents
                for (int i = 0; i < 14; i++)
                {
                    float a = i * 47f;
                    k.Box(Quaternion.Euler(0, a, 0) * new Vector3(0.03f + (i % 4) * 0.02f, -0.06f + (i % 5) * 0.025f, 0), new Vector3(0.005f, 0.003f, 0.2f), i % 3 == 0 ? Mul(c, 0.9f) : c, new Vector3(i * 13f, a, i * 7f));
                }
                k.Ball(new Vector3(0, -0.04f, 0), new Vector3(0.18f, 0.08f, 0.16f), Mul(c, 0.95f));
                return true;
            case "fries":                                  // Truffle Fry Cone (1 Fry)
                k.Cone(new Vector3(0, -0.03f, 0), 0.08f, 0.14f, c, new Vector3(180f, 0, 0));
                k.Cyl(new Vector3(0, 0.04f, 0), 0.082f, 0.01f, Gold, default, Surface.Metal);
                k.Box(new Vector3(0.01f, 0.07f, 0.0f), new Vector3(0.012f, 0.08f, 0.012f), H(0xF2C860), new Vector3(0, 0, -12f));   // THE fry
                return true;
            case "steak":                                  // Wagyu Scraps
                k.Ball(Vector3.zero, new Vector3(0.18f, 0.04f, 0.12f), c);
                for (int i = 0; i < 5; i++)
                    k.Box(new Vector3(-0.05f + i * 0.025f, 0.019f, -0.02f + (i % 2) * 0.03f), new Vector3(0.03f, 0.003f, 0.006f), White, new Vector3(0, i * 35f, 0));
                k.Box(new Vector3(0, -0.03f, 0), new Vector3(0.2f, 0.01f, 0.14f), H(0xF2EEE6));      // butcher paper
                return true;
            case "topiary":                                // Topiary Clippings
                for (int i = 0; i < 6; i++)
                    k.Ball(new Vector3(-0.08f + (i % 3) * 0.08f, (i / 3) * 0.06f, (i % 2) * 0.04f), new Vector3(0.12f, 0.08f, 0.1f), Mul(c, 0.9f + (i % 3) * 0.08f));
                k.Ball(new Vector3(0.04f, 0.1f, 0), new Vector3(0.09f, 0.09f, 0.09f), c);                 // a tiny poodle head, snipped off
                return true;
            case "mallet":                                 // Broken Croquet Mallet
                k.Cyl(new Vector3(0, 0, 0), 0.07f, 0.2f, c, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(0.07f, 0, 0), 0.072f, 0.02f, Red, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(-0.07f, 0, 0), 0.072f, 0.02f, Blue, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(0, 0.0f, 0.13f), 0.022f, 0.22f, H(0xC8A06A), new Vector3(90f, 0, 0));
                k.Cyl(new Vector3(0.03f, 0.0f, 0.28f), 0.022f, 0.08f, H(0xC8A06A), new Vector3(90f, 30f, 0));   // snapped
                return true;
            case "caviar_empty":                           // Empty Caviar Tin (licked clean)
                k.Cyl(Vector3.zero, 0.11f, 0.035f, c, default, Surface.Metal);
                k.Cyl(new Vector3(0.0f, 0.03f, 0.06f), 0.11f, 0.006f, Gold, new Vector3(-70f, 0, 0), Surface.Metal);
                k.Cyl(new Vector3(0, 0.016f, 0), 0.095f, 0.004f, Silver, default, Surface.Metal);
                return true;
            case "puddle":                                 // Ice Sculpture Puddle (it was a swan)
                k.Ball(Vector3.zero, new Vector3(0.36f, 0.03f, 0.3f), c, default, Surface.Glass);
                k.Cap(new Vector3(0.05f, 0.06f, 0), 0.03f, 0.12f, c, new Vector3(0, 0, -30f), Surface.Glass);   // what's left of the neck
                k.Ball(new Vector3(0.09f, 0.11f, 0), new Vector3(0.04f, 0.03f, 0.03f), c, default, Surface.Glass);
                k.Box(new Vector3(-0.12f, 0.02f, 0.08f), new Vector3(0.1f, 0.02f, 0.06f), Silver, new Vector3(0, 30f, 0), Surface.Metal);   // the tray
                return true;
            case "shopbags":                               // Designer Shopping Bags
                for (int i = 0; i < 2; i++)
                {
                    var b = k.In(new Vector3(-0.06f + i * 0.12f, 0, i * 0.03f), new Vector3(0, i * 20f - 10f, i == 1 ? 12f : 0f));
                    Color bc = i == 0 ? c : H(0xE86AA8);
                    b.Box(Vector3.zero, new Vector3(0.16f, 0.2f, 0.07f), bc);
                    b.Ring(new Vector3(0, 0.12f, 0), 0.08f, 0.008f, Black, new Vector3(90f, 0, 0));
                    b.Box(new Vector3(0, 0.03f, 0.036f), new Vector3(0.07f, 0.02f, 0.002f), i == 0 ? Black : Gold);
                }
                return true;
            case "fridgescreen":                           // Smart Fridge Screen
                k.Box(Vector3.zero, new Vector3(0.36f, 0.24f, 0.03f), c);
                k.Box(new Vector3(0, 0, 0.016f), new Vector3(0.32f, 0.2f, 0.004f), H(0x2A6AD8), default, Surface.Emissive, 0.7f);
                k.Box(new Vector3(-0.06f, 0.04f, 0.019f), new Vector3(0.14f, 0.02f, 0.002f), White, default, Surface.Emissive, 1f);   // "OUT OF OAT MILK"
                k.Box(new Vector3(0.08f, -0.04f, 0.019f), new Vector3(0.06f, 0.06f, 0.002f), H(0xEADCB8), default, Surface.Emissive, 0.8f);
                k.Box(new Vector3(0.12f, 0.06f, 0.018f), new Vector3(0.05f, 0.003f, 0.004f), Black, new Vector3(0, 0, 35f));            // crack
                return true;
            case "ebattery":                               // E-Scooter Battery
                k.Box(Vector3.zero, new Vector3(0.1f, 0.08f, 0.36f), c);
                k.Box(new Vector3(0, 0.041f, 0), new Vector3(0.09f, 0.002f, 0.3f), DarkGrey);
                k.Box(new Vector3(0, 0.0f, 0.181f), new Vector3(0.06f, 0.04f, 0.004f), Yellow);
                k.Box(new Vector3(0.03f, 0.042f, -0.12f), new Vector3(0.012f, 0.004f, 0.012f), H(0x40FF60), default, Surface.Emissive, 1.6f);
                k.Cyl(new Vector3(0, 0.0f, -0.19f), 0.03f, 0.03f, Red, new Vector3(90f, 0, 0));
                k.Ball(new Vector3(0, 0.05f, 0.05f), new Vector3(0.06f, 0.02f, 0.08f), Mul(c, 1.3f));                                // it's swelling. grumpy.
                return true;
            case "flare":                                  // Expired Yacht Flare
                k.Cyl(Vector3.zero, 0.045f, 0.24f, c, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(0.13f, 0, 0), 0.05f, 0.03f, Black, new Vector3(0, 0, 90f));
                k.Cyl(new Vector3(-0.13f, 0, 0), 0.04f, 0.025f, H(0xF2F2F2), new Vector3(0, 0, 90f));
                k.Box(new Vector3(0.0f, 0.023f, 0), new Vector3(0.12f, 0.002f, 0.03f), Yellow);
                return true;
        }
        return false;
    }
}

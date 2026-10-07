using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural meshes + shared materials for the Pickup system's markers (stop arrows, hopper arrow,
/// ground rings, beacon beams). Everything is built at runtime so no mesh assets are needed; materials
/// come from RubbishAssets ("mat/pickup_*", created by Setup_Pickup) with shader-found fallbacks.
/// </summary>
public static class PickupVisuals
{
    public const string MarkerMaterialKey = "mat/pickup_marker"; // URP Unlit, white (tinted per renderer)
    public const string BeamMaterialKey = "mat/pickup_beam";     // URP Unlit transparent
    public const string BagMaterialKey = "mat/pickup_bag";       // URP Lit glossy black plastic (fallback sack)

    public static readonly Color MarkerColor = new Color(0.72f, 1f, 0.18f);  // radioactive lime
    public static readonly Color NextColor = new Color(1f, 0.55f, 0.08f);    // hot orange
    public static readonly Color GoodColor = new Color(0.3f, 1f, 0.4f);
    public static readonly Color BadColor = new Color(1f, 0.25f, 0.2f);

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock mpb;

    static Mesh arrow, ring, beam;
    static Material markerMat, beamMat, bagMat;
    static bool beamMatLooked;

    // ---------------- materials ----------------

    public static Material MarkerMaterial
    {
        get
        {
            if (markerMat != null) return markerMat;
            markerMat = RubbishAssets.Material(MarkerMaterialKey);
            if (markerMat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                markerMat = new Material(sh) { name = "pickup_marker (runtime)" };
                SetMatColor(markerMat, Color.white);
            }
            return markerMat;
        }
    }

    /// <summary>Transparent beam material, or null if Setup_Pickup hasn't created one (beams are skipped).</summary>
    public static Material BeamMaterial
    {
        get
        {
            if (beamMat != null || beamMatLooked) return beamMat;
            beamMatLooked = true;
            beamMat = RubbishAssets.Material(BeamMaterialKey);
            return beamMat;
        }
    }

    public static Material BagMaterial
    {
        get
        {
            if (bagMat != null) return bagMat;
            bagMat = RubbishAssets.Material(BagMaterialKey);
            if (bagMat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Standard");
                bagMat = new Material(sh) { name = "pickup_bag (runtime)" };
                SetMatColor(bagMat, new Color(0.05f, 0.05f, 0.06f));
            }
            return bagMat;
        }
    }

    static void SetMatColor(Material m, Color c)
    {
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
        if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
    }

    /// <summary>Tint a renderer via property block (_BaseColor and _Color).</summary>
    public static void SetColor(Renderer r, Color c)
    {
        if (r == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        r.GetPropertyBlock(mpb);
        mpb.SetColor(BaseColorId, c);
        mpb.SetColor(ColorId, c);
        r.SetPropertyBlock(mpb);
    }

    /// <summary>Make a child with a mesh + renderer (no collider, no shadows).</summary>
    public static MeshRenderer MakeMeshChild(Transform parent, string name, Mesh mesh, Material mat, Color color)
    {
        var go = new GameObject(name);
        go.layer = parent != null ? parent.gameObject.layer : 0;
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        SetColor(r, color);
        return r;
    }

    // ---------------- meshes ----------------

    /// <summary>Chunky downward-pointing arrow, 1 unit tall, tip at y=0.</summary>
    public static Mesh Arrow
    {
        get
        {
            if (arrow != null) return arrow;
            const int seg = 14;
            const float headH = 0.5f, headR = 0.38f, shaftR = 0.15f, top = 1f;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tris = new List<int>();

            // cone (tip at 0, base at headH)
            float slant = Mathf.Atan2(headR, headH);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg, am = (a0 + a1) * 0.5f;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                Vector3 dm = new Vector3(Mathf.Cos(am), 0, Mathf.Sin(am));
                Vector3 nm = (dm * Mathf.Cos(slant) - Vector3.up * Mathf.Sin(slant)).normalized;
                int b = v.Count;
                v.Add(Vector3.zero); v.Add(d0 * headR + Vector3.up * headH); v.Add(d1 * headR + Vector3.up * headH);
                n.Add(nm); n.Add((d0 * Mathf.Cos(slant) - Vector3.up * Mathf.Sin(slant)).normalized); n.Add((d1 * Mathf.Cos(slant) - Vector3.up * Mathf.Sin(slant)).normalized);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                // cone base annulus (facing up), from shaft radius to head radius
                b = v.Count;
                v.Add(d0 * shaftR + Vector3.up * headH); v.Add(d0 * headR + Vector3.up * headH);
                v.Add(d1 * headR + Vector3.up * headH); v.Add(d1 * shaftR + Vector3.up * headH);
                for (int k = 0; k < 4; k++) n.Add(Vector3.up);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                // shaft side
                b = v.Count;
                v.Add(d0 * shaftR + Vector3.up * headH); v.Add(d1 * shaftR + Vector3.up * headH);
                v.Add(d1 * shaftR + Vector3.up * top); v.Add(d0 * shaftR + Vector3.up * top);
                n.Add(d0); n.Add(d1); n.Add(d1); n.Add(d0);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                // shaft top cap
                b = v.Count;
                v.Add(Vector3.up * top); v.Add(d0 * shaftR + Vector3.up * top); v.Add(d1 * shaftR + Vector3.up * top);
                n.Add(Vector3.up); n.Add(Vector3.up); n.Add(Vector3.up);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            }
            arrow = Build("PickupArrow", v, n, tris);
            return arrow;
        }
    }

    /// <summary>Flat ring (annulus) in the XZ plane, outer radius 1, inner radius 0.82, facing up.</summary>
    public static Mesh Ring
    {
        get
        {
            if (ring != null) return ring;
            const int seg = 48; const float inner = 0.82f, outer = 1f;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v.Add(d * inner); v.Add(d * outer); n.Add(Vector3.up); n.Add(Vector3.up);
                if (i < seg)
                {
                    int b = i * 2;
                    tris.Add(b); tris.Add(b + 3); tris.Add(b + 1);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
            }
            ring = Build("PickupRing", v, n, tris);
            return ring;
        }
    }

    /// <summary>Open-ended cylinder, radius 1, from y=0 to y=1 (for beacon beams). Double-sided.</summary>
    public static Mesh Beam
    {
        get
        {
            if (beam != null) return beam;
            const int seg = 16;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tris = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v.Add(d); v.Add(d + Vector3.up); n.Add(d); n.Add(d);
                if (i < seg)
                {
                    int b = i * 2;
                    // outside
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 3);
                    tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                    // inside
                    tris.Add(b); tris.Add(b + 3); tris.Add(b + 1);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
            }
            beam = Build("PickupBeam", v, n, tris);
            return beam;
        }
    }

    static Mesh Build(string name, List<Vector3> v, List<Vector3> n, List<int> tris)
    {
        var m = new Mesh { name = name };
        m.SetVertices(v);
        m.SetNormals(n);
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    // ---------------- misc ----------------

    /// <summary>Deterministic 32-bit FNV-1a string hash (stable across runs/platforms, unlike string.GetHashCode).</summary>
    public static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            if (s != null)
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (int)h;
        }
    }

    /// <summary>"$8" for whole amounts, "$8.50" otherwise.</summary>
    public static string Cash(float v)
    {
        float a = Mathf.Abs(v);
        string s = Mathf.Approximately(a, Mathf.Round(a)) ? "$" + Mathf.RoundToInt(a) : Economy.Money(a);
        return v < 0 ? "-" + s : s;
    }

    public static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        foreach (Transform t in go.transform) SetLayerRecursively(t.gameObject, layer);
    }

    // Material lookups may have been cached before Setup_Pickup registered the real assets (or in a
    // previous play session with domain reload off): look them up fresh each play session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        markerMat = null;
        beamMat = null;
        bagMat = null;
        beamMatLooked = false;
    }
}

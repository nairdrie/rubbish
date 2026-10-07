using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime materials + primitive helpers for Shop props (headlamp, delivery boxes, truck mods).
/// Prefers materials registered in RubbishAssets by Setup_Shop ("mat/shop_*"), otherwise builds a URP Lit
/// material once and caches it.
/// </summary>
public static class ShopMaterials
{
    static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
    static Shader lit;

    public static Material Get(string key, Color color, float smoothness = 0.3f, float metallic = 0f)
    {
        if (cache.TryGetValue(key, out var m) && m != null) return m;
        m = RubbishAssets.Material(key);
        if (m == null)
        {
            if (lit == null) lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(lit) { name = key.Replace('/', '_') };
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
        }
        cache[key] = m;
        return m;
    }

    /// <summary>A renderer-only primitive (collider removed) parented under parent.</summary>
    public static Transform Primitive(PrimitiveType type, string name, Transform parent, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);
        go.transform.SetParent(parent, false);
        var r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
        if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => cache.Clear();
}

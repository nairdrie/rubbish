using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shared helpers for the Scrapazon truck + gear mods (Docs/Progression.md §6.1 / §6.4, builder B):
/// truck-metre conversions, renderer-only primitives (never colliders), emissive / glow materials, the host check,
/// and a tiny cosmetic-only random source (gameplay rolls use <see cref="UpgradeEffects.DetectRoll"/> or a seeded
/// System.Random elsewhere; nothing here decides an outcome).
/// </summary>
public static class ShopModKit
{
    // ------------------------------------------------------------------ host / authority

    /// <summary>Host-side decisions (arm target choice, litter vac) run on the server, or offline.</summary>
    public static bool IsHost
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }
    }

    // ------------------------------------------------------------------ truck metres

    /// <summary>
    /// Truck V2's root is scaled 0.67: offsets in these files are true metres from the root (like
    /// TruckControllerV2.TruckPoint). +Z forward, +X right; ground ≈ y 0.4; body x ±1.3, z -2.2 (rear) .. 3.8 (nose),
    /// hopper body top y ≈ 3.05, cab roof y ≈ 2.5, hopper mouth ≈ (0, 1.82, -2.32).
    /// </summary>
    public static Vector3 TruckPoint(Transform truck, Vector3 metres) => truck.position + truck.rotation * TruckModelVisuals.MapMetres(truck, metres);

    public static Vector3 ToTruckMetres(Transform truck, Vector3 world) => TruckModelVisuals.UnmapMetres(truck, Quaternion.Inverse(truck.rotation) * (world - truck.position));

    public const float BodyHalfWidth = 1.3f, BodyRearZ = -2.2f, BodyNoseZ = 3.8f, BodyTopY = 3.05f;

    static float SafeInv(float v) => Mathf.Abs(v) < 1e-4f ? 1f : 1f / v;

    /// <summary>(Re)create a child root under parent whose scale undoes the parent's, so its children work in metres.</summary>
    public static Transform MetreRoot(Transform parent, string name)
    {
        var old = parent.Find(name);
        if (old != null)
        {
            old.gameObject.SetActive(false);
            if (Application.isPlaying) Object.Destroy(old.gameObject); else Object.DestroyImmediate(old.gameObject);
        }
        var root = new GameObject(name).transform;
        root.SetParent(parent, false);
        Vector3 ls = parent.lossyScale;
        root.localScale = new Vector3(SafeInv(ls.x), SafeInv(ls.y), SafeInv(ls.z));
        TruckModelVisuals.RegisterMetreRoot(parent, root);
        return root;
    }

    /// <summary>A renderer-only primitive (collider removed), placed in parent's local space.</summary>
    public static Transform Prim(PrimitiveType type, string name, Transform parent, Material mat, Vector3 localPos, Vector3 localScale)
    {
        var t = ShopMaterials.Primitive(type, name, parent, mat);
        t.localPosition = localPos;
        t.localScale = localScale;
        return t;
    }

    public static Transform Prim(PrimitiveType type, string name, Transform parent, Material mat, Vector3 localPos, Vector3 localScale, Vector3 euler)
    {
        var t = Prim(type, name, parent, mat, localPos, localScale);
        t.localRotation = Quaternion.Euler(euler);
        return t;
    }

    /// <summary>An empty pivot transform.</summary>
    public static Transform Pivot(string name, Transform parent, Vector3 localPos)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        return t;
    }

    // ------------------------------------------------------------------ materials

    public static Material Lit(string key, Color c, float smooth = 0.3f, float metal = 0f) => ShopMaterials.Get(key, c, smooth, metal);

    static readonly Dictionary<string, Material> made = new Dictionary<string, Material>();

    /// <summary>URP Lit with emission (lenses, LEDs, the gold sparkle bits). Cached per key.</summary>
    public static Material Emissive(string key, Color c, float intensity = 2f)
    {
        if (made.TryGetValue(key, out var m) && m != null) return m;
        m = new Material(ShopMaterials.Get(key + "_base", c, 0.6f)) { name = key.Replace('/', '_') };
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        m.SetColor("_EmissionColor", c * intensity);
        made[key] = m;
        return m;
    }

    /// <summary>
    /// Transparent unlit glow (UV aura, beams). Clones the registered "mat/pickup_beam" (URP Unlit transparent, shipped
    /// by Setup_Pickup) so the shader variant is in builds; falls back to an opaque unlit tint. Cached per key; callers
    /// that animate alpha should instantiate their own copy.
    /// </summary>
    public static Material Glow(string key, Color c)
    {
        if (made.TryGetValue(key, out var m) && m != null) return m;
        var beam = RubbishAssets.Material("mat/pickup_beam");
        if (beam != null) m = new Material(beam);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            m = new Material(sh);
        }
        m.name = key.Replace('/', '_');
        m.SetColor("_BaseColor", c);
        m.SetColor("_Color", c);
        made[key] = m;
        return m;
    }

    // ------------------------------------------------------------------ cosmetic randomness

    static uint rng = 0x9E3779B9u;

    /// <summary>Cosmetic-only 0..1 (wobbles, which joke line to show). Never use it to decide an outcome.</summary>
    public static float Rand01()
    {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        return (rng & 0xFFFFFF) / 16777216f;
    }

    public static float Range(float a, float b) => a + (b - a) * Rand01();
    public static int Range(int a, int bExclusive) => bExclusive <= a ? a : a + (int)(Rand01() * (bExclusive - a)) % (bExclusive - a);
    public static T Pick<T>(T[] arr) => arr == null || arr.Length == 0 ? default : arr[Range(0, arr.Length)];

    /// <summary>Does this bag hold at least one valuable (treasure)?</summary>
    public static bool HasValuable(BagContents c)
    {
        if (c == null || c.items == null) return false;
        foreach (var it in c.items)
        {
            var d = it != null ? it.Def : null;
            if (d != null && d.category == ItemCategory.Valuable) return true;
        }
        return false;
    }

    /// <summary>The best rarity of the valuables in this bag (Common when none).</summary>
    public static Rarity BestValuable(BagContents c)
    {
        Rarity best = Rarity.Common;
        if (c == null || c.items == null) return best;
        foreach (var it in c.items)
        {
            var d = it != null ? it.Def : null;
            if (d != null && d.category == ItemCategory.Valuable && d.rarity > best) best = d.rarity;
        }
        return best;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        made.Clear();
        rng = 0x9E3779B9u;
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1): the shared kit the Neighbour Puppets are built from. Primitive meshes
/// (Unity's sphere / capsule / cube / cylinder plus a procedural cone and dome, each created once), one cached URP
/// material per colour (property-free, so the SRP Batcher keeps batching, the way <see cref="CritterLook.Mat"/> does it),
/// the eight skin tones, and <see cref="PuppetDice"/>, the cosmetic hash dice that keeps WorldLife free of
/// UnityEngine.Random (rule 2: looks only, never data).
/// Materials come from RubbishAssets <c>mat/worldlife_res_*</c> (registered by Setup_WorldLifeResidents, so builds keep
/// the shader variants) and fall back to runtime URP materials.
/// </summary>
public static class PuppetKit
{
    public enum Shape : byte { None = 0, Sphere, Capsule, Cube, Cylinder, Cone, Dome }

    public const string LitKey = WorldLifeIds.MaterialPrefix + "res_lit";
    public const string EmissiveKey = WorldLifeIds.MaterialPrefix + "res_emissive";
    public const string UnlitKey = WorldLifeIds.MaterialPrefix + "res_unlit";
    public const string BeamKey = WorldLifeIds.MaterialPrefix + "res_beam";

    /// <summary>Puppet parts live on Ignore Raycast (like critters): interaction scans and hold clamps never see them.</summary>
    public const int Layer = 2;

    // ------------------------------------------------------------------ meshes

    static readonly Mesh[] meshes = new Mesh[8];

    public static Mesh MeshOf(Shape m)
    {
        int i = (int)m;
        if (i <= 0 || i >= meshes.Length) return null;
        if (meshes[i] != null) return meshes[i];
        switch (m)
        {
            case Shape.Sphere: meshes[i] = Builtin(PrimitiveType.Sphere); break;
            case Shape.Capsule: meshes[i] = Builtin(PrimitiveType.Capsule); break;
            case Shape.Cube: meshes[i] = Builtin(PrimitiveType.Cube); break;
            case Shape.Cylinder: meshes[i] = Builtin(PrimitiveType.Cylinder); break;
            case Shape.Cone: meshes[i] = BuildCone(14); break;
            case Shape.Dome: meshes[i] = BuildDome(14, 6); break;
        }
        return meshes[i];
    }

    static Mesh Builtin(PrimitiveType t)
    {
        var go = GameObject.CreatePrimitive(t);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        return mesh;
    }

    /// <summary>An open cone: apex at the origin, base (radius 1) at z = 1. Scale (r, r, length). Flashlight beams.</summary>
    static Mesh BuildCone(int seg)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
        for (int s = 0; s <= seg; s++)
        {
            float a = s * Mathf.PI * 2f / seg;
            var rim = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 1f);
            var nn = new Vector3(Mathf.Cos(a), Mathf.Sin(a), -1f).normalized;
            v.Add(Vector3.zero); n.Add(nn);
            v.Add(rim); n.Add(nn);
        }
        for (int s = 0; s < seg; s++)
        {
            int a = s * 2, b = s * 2 + 2;
            t.Add(a); t.Add(b + 1); t.Add(a + 1);   // outside only (the beam material draws both sides: Cull Off)
        }
        var m = new Mesh { name = "PuppetCone" };
        m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
        m.RecalculateBounds();
        m.hideFlags = HideFlags.DontSave;
        return m;
    }

    /// <summary>A closed hemisphere (radius 0.5, flat side on y = 0, dome up): the butler's cloche, hats, the bun.</summary>
    static Mesh BuildDome(int seg, int rings)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            float phi = r * Mathf.PI * 0.5f / rings;
            float y = Mathf.Sin(phi) * 0.5f, rad = Mathf.Cos(phi) * 0.5f;
            for (int s = 0; s <= seg; s++)
            {
                float a = s * Mathf.PI * 2f / seg;
                v.Add(new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad));
            }
        }
        int row = seg + 1;
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                int a = r * row + s, b = a + row;
                t.Add(a); t.Add(b); t.Add(a + 1);
                t.Add(a + 1); t.Add(b); t.Add(b + 1);
            }
        int center = v.Count;
        v.Add(Vector3.zero);
        for (int s = 0; s < seg; s++) { t.Add(center); t.Add(s); t.Add(s + 1); }   // the flat bottom
        var m = new Mesh { name = "PuppetDome" };
        m.SetVertices(v); m.SetTriangles(t, 0);
        m.RecalculateNormals(); m.RecalculateBounds();
        m.hideFlags = HideFlags.DontSave;
        return m;
    }

    // ------------------------------------------------------------------ materials (one per colour)

    static readonly Dictionary<long, Material> litCache = new Dictionary<long, Material>(128);
    static readonly Dictionary<long, Material> unlitCache = new Dictionary<long, Material>(16);
    static readonly Dictionary<long, Material> beamCache = new Dictionary<long, Material>(4);

    static long Key(Color c, float smooth, float emission)
    {
        Color32 k = c;
        return k.r | ((long)k.g << 8) | ((long)k.b << 16) | ((long)k.a << 24)
             | ((long)Mathf.RoundToInt(Mathf.Clamp01(smooth) * 255f) << 32) | ((long)Mathf.RoundToInt(Mathf.Clamp(emission, 0f, 8f) * 31f) << 40);
    }

    /// <summary>A lit material of this colour (cached; never per puppet). emission &gt; 0 glows (flashlights, the gold robe).</summary>
    public static Material Lit(Color color, float smoothness = 0.25f, float emission = 0f)
    {
        long key = Key(color, smoothness, emission);
        if (litCache.TryGetValue(key, out var m) && m != null) return m;
        var src = RubbishAssets.Material(emission > 0f ? EmissiveKey : LitKey);
        if (src != null) m = new Material(src);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(sh != null ? sh : Shader.Find("Standard"));
        }
        m.name = "WL_Res_" + ColorUtility.ToHtmlStringRGB(color);
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", 0f);
        if (emission > 0f)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", color * emission);
        }
        m.hideFlags = HideFlags.DontSave;
        litCache[key] = m;
        return m;
    }

    /// <summary>A flat unlit colour (stars, steam, sign ink).</summary>
    public static Material Unlit(Color color)
    {
        long key = Key(color, 0f, 0f);
        if (unlitCache.TryGetValue(key, out var m) && m != null) return m;
        var src = RubbishAssets.Material(UnlitKey);
        if (src != null) m = new Material(src);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            m = new Material(sh != null ? sh : Shader.Find("Unlit/Color"));
        }
        m.name = "WL_ResUnlit_" + ColorUtility.ToHtmlStringRGB(color);
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.hideFlags = HideFlags.DontSave;
        unlitCache[key] = m;
        return m;
    }

    /// <summary>The see-through flashlight beam (transparent unlit; an emissive cone, never a real light).</summary>
    public static Material Beam(Color color)
    {
        long key = Key(color, 0f, 0f);
        if (beamCache.TryGetValue(key, out var m) && m != null) return m;
        var src = RubbishAssets.Material(BeamKey);
        if (src != null) m = new Material(src);
        else
        {
            m = new Material(Unlit(color));
            MakeTransparent(m);
        }
        m.name = "WL_ResBeam_" + ColorUtility.ToHtmlStringRGB(color);
        m.SetColor("_BaseColor", color);
        m.SetColor("_Color", color);
        m.hideFlags = HideFlags.DontSave;
        beamCache[key] = m;
        return m;
    }

    /// <summary>URP Unlit/Lit → alpha-blended transparent, no depth write (edit time: the beam material asset).</summary>
    public static void MakeTransparent(Material m)
    {
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_Cull", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        m.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
        m.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    /// <summary>How many distinct materials the kit has made (perf report: one per colour, never per puppet).</summary>
    public static int MaterialCount => litCache.Count + unlitCache.Count + beamCache.Count;

    // ------------------------------------------------------------------ palettes

    /// <summary>Eight skin tones, uniformly mixed (§3.1).</summary>
    public static readonly Color[] SkinTones =
    {
        new Color(1.00f, 0.87f, 0.77f), new Color(0.96f, 0.80f, 0.68f), new Color(0.91f, 0.72f, 0.57f), new Color(0.80f, 0.60f, 0.44f),
        new Color(0.68f, 0.48f, 0.33f), new Color(0.56f, 0.38f, 0.26f), new Color(0.44f, 0.29f, 0.20f), new Color(0.33f, 0.22f, 0.15f),
    };

    public static readonly Color EyeWhite = new Color(0.98f, 0.98f, 0.96f);
    public static readonly Color Pupil = new Color(0.03f, 0.03f, 0.03f);
    public static readonly Color MouthDark = new Color(0.32f, 0.07f, 0.09f);
    public static readonly Color AngryRed = new Color(0.95f, 0.22f, 0.16f);

    /// <summary>The skin colour pushed toward red: the angry face. Quantised to 4 steps so it stays a handful of shared
    /// materials (Character.md §3's strain face, done the batching-friendly way).</summary>
    public static Color AngryTint(Color skin, int step) => Color.Lerp(skin, AngryRed, Mathf.Clamp(step, 0, 3) * 0.2f);

    // ------------------------------------------------------------------ parts

    /// <summary>An empty pivot on the puppet layer.</summary>
    public static Transform Node(Transform parent, string name)
    {
        var t = new GameObject(name).transform;
        t.gameObject.layer = Layer;
        t.SetParent(parent, false);
        return t;
    }

    /// <summary>A renderer-only part (no collider) whose mesh and material are swapped by restyling.</summary>
    public static MeshRenderer Part(Transform parent, string name, out MeshFilter filter)
    {
        var go = new GameObject(name);
        go.layer = Layer;
        go.transform.SetParent(parent, false);
        filter = go.AddComponent<MeshFilter>();
        var r = go.AddComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = true;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.allowOcclusionWhenDynamic = false;
        return r;
    }

    // ------------------------------------------------------------------ maths

    public static float Damp(float current, float target, float sharpness, float dt) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    public static Vector3 Damp(Vector3 current, Vector3 target, float sharpness, float dt) =>
        Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    public static float DampAngle(float current, float target, float sharpness, float dt) =>
        Mathf.LerpAngle(current, target, 1f - Mathf.Exp(-sharpness * dt));

    /// <summary>A critically-damped-ish spring step (semi-implicit Euler).</summary>
    public static void Spring(ref float x, ref float v, float target, float omega, float zeta, float dt)
    {
        float f = -2f * zeta * omega * v - omega * omega * (x - target);
        v += f * dt;
        x += v * dt;
    }

    public static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        foreach (var m in litCache.Values) if (m != null) Object.DestroyImmediate(m);
        foreach (var m in unlitCache.Values) if (m != null) Object.DestroyImmediate(m);
        foreach (var m in beamCache.Values) if (m != null) Object.DestroyImmediate(m);
        litCache.Clear(); unlitCache.Clear(); beamCache.Clear();
        for (int i = 0; i < meshes.Length; i++)
        {
            if (meshes[i] != null && (meshes[i].hideFlags & HideFlags.DontSave) != 0) Object.DestroyImmediate(meshes[i]);
            meshes[i] = null;
        }
    }
}

/// <summary>
/// Cosmetic dice from <see cref="LifeHash"/> (no UnityEngine.Random, so coop_lint stays clean): limb-flail phases, chatter
/// picks, debris jitter. Never use it for anything that pays, hurts or must agree between machines.
/// </summary>
public struct PuppetDice
{
    ulong state;

    public PuppetDice(ulong seed) { state = seed ^ 0x5DEECE66DUL; }

    public static PuppetDice From(uint a, int b) => new PuppetDice(LifeHash.Mix(((ulong)a << 32) ^ (uint)b));

    public ulong NextU64() => LifeHash.Next(ref state);
    public float Value => LifeHash.Float01(LifeHash.Next(ref state));
    public float Range(float a, float b) => a + (b - a) * Value;
    public int Range(int a, int bExclusive) => LifeHash.Range(LifeHash.Next(ref state), a, bExclusive);
    public float Sign => (LifeHash.Next(ref state) & 1) == 0 ? -1f : 1f;
    public Vector2 InCircle
    {
        get
        {
            float a = Value * Mathf.PI * 2f, r = Mathf.Sqrt(Value);
            return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
    }
    public Vector3 OnSphere
    {
        get
        {
            float z = Range(-1f, 1f), a = Value * Mathf.PI * 2f, r = Mathf.Sqrt(1f - z * z);
            return new Vector3(Mathf.Cos(a) * r, z, Mathf.Sin(a) * r);
        }
    }
    public T Pick<T>(T[] arr) => arr == null || arr.Length == 0 ? default : arr[Range(0, arr.Length)];
}

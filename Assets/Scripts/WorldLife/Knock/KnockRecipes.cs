using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package B: code recipes for the NEW derp knockables of §3.4 / §6.1 (hydrant, cone, sawhorse, lawn flamingo),
/// built from Unity primitives in the derpy house style. One recipe serves both sides of a knock:
/// <list type="bullet">
/// <item><see cref="Build"/> makes the at-rest prop (NeighborhoodBuilder's post-pass may call it; the B sandbox does), with
/// its rest collider and a <see cref="Knockable"/> already set up when asked.</item>
/// <item><see cref="KnockProxyPool"/> uses <see cref="TryGet"/> to rebuild the same primitives as a proxy for a
/// <c>"recipe/&lt;name&gt;"</c> key when no baked <see cref="KnockableMeshes"/> exist.</item>
/// </list>
/// Materials come from RubbishAssets (<c>mat/worldlife_knock_*</c>, written by Setup_WorldLifeKnock) with a runtime URP/Lit
/// fallback, so a recipe never renders pink.
/// </summary>
public static class KnockRecipes
{
    public enum Prim : byte { Cube, Cylinder, Sphere }

    /// <summary>One primitive of a recipe, in the prop's local frame (metres; y = 0 is the ground).</summary>
    public readonly struct Part
    {
        public readonly Prim prim;
        public readonly Vector3 pos, euler, size;
        public readonly string mat;
        public Part(Prim prim, Vector3 pos, Vector3 euler, Vector3 size, string mat)
        { this.prim = prim; this.pos = pos; this.euler = euler; this.size = size; this.mat = mat; }
    }

    /// <summary>A recipe: parts, its rest collider (local box) and its knockable kind.</summary>
    public sealed class Recipe
    {
        public string name;
        public KnockKind kind;
        public Part[] parts;
        public Vector3 colliderCenter, colliderSize;
    }

    // Material keys (RubbishAssets "mat/worldlife_knock_<x>") and their colours (the runtime fallback uses the same).
    public const string MatRed = "red", MatDarkRed = "darkred", MatOrange = "orange", MatWhite = "white", MatPink = "pink",
        MatBlack = "black", MatYellow = "yellow", MatMetal = "metal", MatWood = "wood";
    public static readonly string[] MaterialNames = { MatRed, MatDarkRed, MatOrange, MatWhite, MatPink, MatBlack, MatYellow, MatMetal, MatWood };
    public static Color ColorOf(string mat) => mat switch
    {
        MatRed => new Color(0.86f, 0.13f, 0.1f),
        MatDarkRed => new Color(0.55f, 0.07f, 0.06f),
        MatOrange => new Color(1f, 0.45f, 0.08f),
        MatWhite => new Color(0.95f, 0.95f, 0.92f),
        MatPink => new Color(1f, 0.42f, 0.7f),
        MatBlack => new Color(0.08f, 0.08f, 0.09f),
        MatYellow => new Color(1f, 0.82f, 0.15f),
        MatMetal => new Color(0.62f, 0.64f, 0.66f),
        MatWood => new Color(0.62f, 0.45f, 0.28f),
        _ => Color.magenta,
    };
    public static string MaterialKey(string mat) => WorldLifeIds.MaterialPrefix + "knock_" + mat;

    static Part P(Prim p, float x, float y, float z, float sx, float sy, float sz, string mat, float ex = 0f, float ey = 0f, float ez = 0f) =>
        new Part(p, new Vector3(x, y, z), new Vector3(ex, ey, ez), new Vector3(sx, sy, sz), mat);

    // Cylinder primitives are 2 m tall at scale 1, so a cylinder of height h has scale.y = h / 2 (P() takes real sizes and
    // CylScale converts). Everything else is 1 m at scale 1.
    static Part Cyl(float x, float y, float z, float d, float h, string mat, float ex = 0f, float ey = 0f, float ez = 0f) =>
        new Part(Prim.Cylinder, new Vector3(x, y, z), new Vector3(ex, ey, ez), new Vector3(d, h * 0.5f, d), mat);

    static readonly Dictionary<string, Recipe> recipes = new Dictionary<string, Recipe>
    {
        ["hydrant"] = new Recipe
        {
            name = "hydrant", kind = KnockKind.Hydrant,
            parts = new[]
            {
                Cyl(0f, 0.03f, 0f, 0.4f, 0.06f, MatDarkRed),                 // flange
                Cyl(0f, 0.33f, 0f, 0.3f, 0.56f, MatRed),                     // barrel
                Cyl(0f, 0.6f, 0f, 0.36f, 0.05f, MatDarkRed),                 // collar
                P(Prim.Sphere, 0f, 0.66f, 0f, 0.31f, 0.2f, 0.31f, MatRed),   // dome cap
                Cyl(0f, 0.78f, 0f, 0.07f, 0.07f, MatMetal),                   // top nut
                Cyl(0.19f, 0.42f, 0f, 0.12f, 0.1f, MatMetal, 0f, 0f, 90f),    // side nub
                Cyl(-0.19f, 0.42f, 0f, 0.12f, 0.1f, MatMetal, 0f, 0f, 90f),   // side nub
                Cyl(0f, 0.38f, 0.18f, 0.15f, 0.1f, MatDarkRed, 90f, 0f, 0f),  // pumper nozzle
            },
            colliderCenter = new Vector3(0f, 0.41f, 0f), colliderSize = new Vector3(0.42f, 0.82f, 0.42f),
        },
        ["cone"] = new Recipe
        {
            name = "cone", kind = KnockKind.Cone,
            parts = new[]
            {
                P(Prim.Cube, 0f, 0.02f, 0f, 0.48f, 0.04f, 0.48f, MatBlack),
                Cyl(0f, 0.14f, 0f, 0.34f, 0.2f, MatOrange),
                Cyl(0f, 0.31f, 0f, 0.27f, 0.14f, MatWhite),
                Cyl(0f, 0.45f, 0f, 0.2f, 0.14f, MatOrange),
                Cyl(0f, 0.57f, 0f, 0.13f, 0.1f, MatWhite),
                Cyl(0f, 0.65f, 0f, 0.08f, 0.06f, MatOrange),
            },
            colliderCenter = new Vector3(0f, 0.34f, 0f), colliderSize = new Vector3(0.48f, 0.68f, 0.48f),
        },
        ["sawhorse"] = new Recipe
        {
            name = "sawhorse", kind = KnockKind.Sawhorse,
            parts = new[]
            {
                P(Prim.Cube, 0f, 0.86f, 0f, 1.5f, 0.24f, 0.05f, MatOrange),    // the board (striped below)
                P(Prim.Cube, -0.45f, 0.86f, -0.03f, 0.22f, 0.24f, 0.01f, MatWhite, 0f, 0f, 35f),
                P(Prim.Cube, 0f, 0.86f, -0.03f, 0.22f, 0.24f, 0.01f, MatWhite, 0f, 0f, 35f),
                P(Prim.Cube, 0.45f, 0.86f, -0.03f, 0.22f, 0.24f, 0.01f, MatWhite, 0f, 0f, 35f),
                P(Prim.Cube, -0.62f, 0.45f, 0.14f, 0.06f, 0.95f, 0.06f, MatWood, -17f),
                P(Prim.Cube, -0.62f, 0.45f, -0.14f, 0.06f, 0.95f, 0.06f, MatWood, 17f),
                P(Prim.Cube, 0.62f, 0.45f, 0.14f, 0.06f, 0.95f, 0.06f, MatWood, -17f),
                P(Prim.Cube, 0.62f, 0.45f, -0.14f, 0.06f, 0.95f, 0.06f, MatWood, 17f),
            },
            colliderCenter = new Vector3(0f, 0.5f, 0f), colliderSize = new Vector3(1.5f, 1f, 0.42f),
        },
        ["flamingo"] = new Recipe
        {
            name = "flamingo", kind = KnockKind.Flamingo,
            parts = new[]
            {
                Cyl(-0.04f, 0.24f, 0f, 0.02f, 0.48f, MatMetal),
                Cyl(0.04f, 0.24f, 0f, 0.02f, 0.48f, MatMetal),
                P(Prim.Sphere, 0f, 0.55f, 0f, 0.2f, 0.2f, 0.36f, MatPink),          // body
                P(Prim.Sphere, 0f, 0.58f, -0.17f, 0.08f, 0.12f, 0.16f, MatPink, -30f), // tail
                Cyl(0f, 0.74f, 0.13f, 0.045f, 0.3f, MatPink, 25f),                   // neck
                P(Prim.Sphere, 0f, 0.9f, 0.2f, 0.11f, 0.1f, 0.12f, MatPink),         // head
                P(Prim.Cube, 0f, 0.87f, 0.29f, 0.04f, 0.04f, 0.1f, MatBlack, 30f),   // beak
                P(Prim.Sphere, 0.05f, 0.92f, 0.23f, 0.025f, 0.025f, 0.025f, MatBlack), // googly-ish eye
                P(Prim.Sphere, -0.05f, 0.92f, 0.23f, 0.025f, 0.025f, 0.025f, MatBlack),
            },
            colliderCenter = new Vector3(0f, 0.48f, 0.05f), colliderSize = new Vector3(0.22f, 0.96f, 0.5f),
        },
    };

    public static IEnumerable<string> Names => recipes.Keys;

    /// <summary>The recipe for a name ("hydrant") or a key ("recipe/hydrant"); false when unknown.</summary>
    public static bool TryGet(string nameOrKey, out Recipe recipe)
    {
        recipe = null;
        if (string.IsNullOrEmpty(nameOrKey)) return false;
        string n = nameOrKey.StartsWith(WorldLifeIds.KnockRecipePrefix) ? nameOrKey.Substring(WorldLifeIds.KnockRecipePrefix.Length) : nameOrKey;
        return recipes.TryGetValue(n, out recipe);
    }

    // ------------------------------------------------------------------ shared meshes + materials

    static readonly Mesh[] primMeshes = new Mesh[3];
    static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

    /// <summary>Unity's built-in primitive mesh (Cube / Cylinder / Sphere), cached.</summary>
    public static Mesh MeshOf(Prim p)
    {
        int i = (int)p;
        if (primMeshes[i] != null) return primMeshes[i];
        var go = GameObject.CreatePrimitive(p == Prim.Cube ? PrimitiveType.Cube : p == Prim.Cylinder ? PrimitiveType.Cylinder : PrimitiveType.Sphere);
        primMeshes[i] = go.GetComponent<MeshFilter>().sharedMesh;
        if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        return primMeshes[i];
    }

    /// <summary>The recipe material: RubbishAssets "mat/worldlife_knock_&lt;name&gt;", else a cached runtime URP/Lit.</summary>
    public static Material MaterialOf(string mat)
    {
        if (mats.TryGetValue(mat, out var m) && m != null) return m;
        m = RubbishAssets.Material(MaterialKey(mat));
        if (m == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            m = sh != null ? new Material(sh) { name = "WorldLife_Knock_" + mat + " (runtime)", hideFlags = HideFlags.DontSave } : null;
            if (m != null)
            {
                m.SetColor("_BaseColor", ColorOf(mat));
                m.SetFloat("_Smoothness", mat == MatMetal ? 0.55f : 0.25f);
            }
        }
        mats[mat] = m;
        return m;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { mats.Clear(); }

    // ------------------------------------------------------------------ build an at-rest prop

    /// <summary>
    /// Build a recipe prop at a world pose: a root with one primitive child per part (no colliders on the parts) and one
    /// BoxCollider on the root (the rest collider). With <paramref name="knockId"/> ≥ 0 a <see cref="Knockable"/> is
    /// added (kind, proxyKey "recipe/&lt;name&gt;", rest collider, renderers) — the GameObject is built inactive and
    /// activated at the end so the knockable registers with its fields set. Returns null for an unknown recipe.
    /// </summary>
    public static GameObject Build(string name, Transform parent, Vector3 pos, Quaternion rot, int knockId = -1, byte district = 0, string stopId = "")
    {
        if (!TryGet(name, out var r)) return null;
        var root = new GameObject("Knock_" + r.name);
        root.SetActive(false);
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.SetPositionAndRotation(pos, rot);
        var rends = new Renderer[r.parts.Length];
        for (int i = 0; i < r.parts.Length; i++)
        {
            var p = r.parts[i];
            var go = new GameObject(r.name + "_" + i);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = p.pos;
            go.transform.localRotation = Quaternion.Euler(p.euler);
            go.transform.localScale = p.size;
            go.AddComponent<MeshFilter>().sharedMesh = MeshOf(p.prim);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MaterialOf(p.mat);
            if (p.size.y < 0.05f || p.size.x < 0.05f) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rends[i] = mr;
        }
        var box = root.AddComponent<BoxCollider>();
        box.center = r.colliderCenter;
        box.size = r.colliderSize;
        if (knockId >= 0)
        {
            var k = root.AddComponent<Knockable>();
            k.id = (ushort)Mathf.Clamp(knockId, 0, ushort.MaxValue);
            k.kind = r.kind;
            k.proxyKey = WorldLifeIds.KnockRecipePrefix + r.name;
            k.restColliders = new Collider[] { box };
            k.renderers = rends;
            k.district = district;
            k.stopId = stopId ?? "";
        }
        root.SetActive(true);
        return root;
    }
}

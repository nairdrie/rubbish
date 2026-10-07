using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// How a knocked prop's flying copy is built (Docs/WorldLife.md §6.3 step 3), resolved once per knockable and cached:
/// <list type="number">
/// <item><c>"knock/&lt;prefab&gt;"</c>: an instance of the proxy prefab Setup_WorldLifeKnock made from the pack prefab
/// (mesh + primitive colliders + Rigidbody), pooled per key.</item>
/// <item>Otherwise an <b>assembled</b> proxy (one pooled Rigidbody + BoxCollider + N mesh slots) rebuilt from the
/// knockable's own renderers: poses and materials from the live renderers, meshes from the baked
/// <see cref="KnockableMeshes"/> (static batching hides the asset mesh at runtime) or the renderer itself when it isn't
/// batched; then a <see cref="KnockRecipes"/> recipe for <c>"recipe/&lt;name&gt;"</c>; then a plain box the size of the
/// prop (logged once: run Rubbish/Setup/WorldLife).</item>
/// </list>
/// TMP text (street names, yard signs) isn't copied: the original text objects (never static) ride on the proxy while the
/// prop is down and go home on reset. Never Instantiate the scene object: its mesh may be the combined static batch.
/// </summary>
public sealed class KnockShape
{
    /// <summary>Prefab proxy (null = assembled).</summary>
    public GameObject prefab;
    public string prefabKey;
    /// <summary>Whose pose the proxy starts at (the prefab instance's root, else the knockable).</summary>
    public Transform poseSource;

    // assembled (proxy frame = the knockable's position + rotation, unscaled)
    public Mesh[] meshes;
    public Material[][] materials;
    public Vector3[] pos;
    public Quaternion[] rot;
    public Vector3[] scale;
    public ShadowCastingMode[] shadows;
    public Vector3 boxCenter, boxSize;

    /// <summary>How it was resolved: "prefab", "baked", "live", "recipe", "box".</summary>
    public string source;
    public int PartCount => meshes != null ? meshes.Length : 0;

    // ------------------------------------------------------------------ resolution

    static readonly List<Renderer> tmpRenderers = new List<Renderer>(16);
    static readonly List<Mesh> tmpMeshes = new List<Mesh>(16);
    static readonly List<Material[]> tmpMats = new List<Material[]>(16);
    static readonly List<Vector3> tmpPos = new List<Vector3>(16), tmpScale = new List<Vector3>(16);
    static readonly List<Quaternion> tmpRot = new List<Quaternion>(16);
    static readonly List<ShadowCastingMode> tmpShadow = new List<ShadowCastingMode>(16);
    static readonly HashSet<string> warned = new HashSet<string>();
    static readonly Dictionary<string, GameObject> prefabByKey = new Dictionary<string, GameObject>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { warned.Clear(); prefabByKey.Clear(); }

    /// <summary>The proxy prefab for a "knock/&lt;prefab&gt;" key (cached; null when Setup_WorldLifeKnock hasn't made it).</summary>
    public static GameObject PrefabFor(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(WorldLifeIds.KnockPrefabPrefix)) return null;
        if (prefabByKey.TryGetValue(key, out var p)) return p;
        p = RubbishAssets.Prefab(key);
        prefabByKey[key] = p;
        return p;
    }

    public static KnockShape Resolve(Knockable k)
    {
        var s = new KnockShape();
        var kt = k.transform;

        // 1) the prefab proxy
        var prefab = PrefabFor(k.proxyKey);
        if (prefab != null)
        {
            s.prefab = prefab;
            s.prefabKey = k.proxyKey;
            s.poseSource = kt;
            string pn = prefab.name.StartsWith("Knock_") ? prefab.name.Substring(6) : prefab.name;
            if (!kt.name.StartsWith(pn))
            {
                // The knockable may wrap the prefab instance: start at the instance's root.
                var rs = k.Renderers;
                for (int i = 0; i < rs.Length; i++)
                    if (rs[i] != null && rs[i].name.StartsWith(pn)) { s.poseSource = rs[i].transform; break; }
            }
            s.source = "prefab";
            return s;
        }

        // 2) assembled from the knockable's own renderers
        tmpMeshes.Clear(); tmpMats.Clear(); tmpPos.Clear(); tmpRot.Clear(); tmpScale.Clear(); tmpShadow.Clear();
        var baked = k.GetComponent<KnockableMeshes>();
        Quaternion inv = Quaternion.Inverse(kt.rotation);
        bool anyBaked = false, anyLive = false;
        CollectRenderers(k, tmpRenderers);
        for (int i = 0; i < tmpRenderers.Count; i++)
        {
            var r = tmpRenderers[i];
            if (!(r is MeshRenderer) || r.GetComponent<TMP_Text>() != null) continue;
            Mesh mesh = baked != null ? baked.MeshFor(r) : null;
            if (mesh != null) anyBaked = true;
            else if (!r.isPartOfStaticBatch && r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) { mesh = mf.sharedMesh; anyLive = true; }
            if (mesh == null) continue;
            var t = r.transform;
            tmpMeshes.Add(mesh);
            tmpMats.Add(r.sharedMaterials);
            tmpPos.Add(inv * (t.position - kt.position));
            tmpRot.Add(inv * t.rotation);
            tmpScale.Add(t.lossyScale);
            tmpShadow.Add(r.shadowCastingMode);
        }
        if (tmpMeshes.Count > 0)
        {
            s.source = anyBaked ? "baked" : anyLive ? "live" : "baked";
        }
        else if (KnockRecipes.TryGet(k.proxyKey, out var recipe))
        {
            // 3) the code recipe (the knockable is the recipe's root)
            Vector3 ls = kt.lossyScale;
            foreach (var p in recipe.parts)
            {
                tmpMeshes.Add(KnockRecipes.MeshOf(p.prim));
                tmpMats.Add(new[] { KnockRecipes.MaterialOf(p.mat) });
                tmpPos.Add(Vector3.Scale(p.pos, ls));
                tmpRot.Add(Quaternion.Euler(p.euler));
                tmpScale.Add(Vector3.Scale(p.size, ls));
                tmpShadow.Add(ShadowCastingMode.On);
            }
            s.source = "recipe";
        }
        else
        {
            // 4) a plain box the size of the prop, in the first renderer's material
            Bounds b = default;
            bool has = false;
            Material mat = null;
            for (int i = 0; i < tmpRenderers.Count; i++)
            {
                var r = tmpRenderers[i];
                if (r == null || r.GetComponent<TMP_Text>() != null) continue;
                if (!has) { b = r.bounds; has = true; mat = r.sharedMaterial; } else b.Encapsulate(r.bounds);
            }
            if (!has) b = new Bounds(kt.position + Vector3.up * 0.5f, Vector3.one * 0.5f);
            tmpMeshes.Add(KnockRecipes.MeshOf(KnockRecipes.Prim.Cube));
            tmpMats.Add(new[] { mat != null ? mat : KnockRecipes.MaterialOf(KnockRecipes.MatMetal) });
            tmpPos.Add(inv * (b.center - kt.position));
            tmpRot.Add(inv);   // world-axis box
            tmpScale.Add(Vector3.Max(b.size, Vector3.one * 0.05f));
            tmpShadow.Add(ShadowCastingMode.On);
            s.source = "box";
            if (warned.Add(k.proxyKey ?? ""))
                Debug.LogWarning($"[WorldLife] Knockable {k.id} ({k.kind}, '{k.proxyKey}') has no proxy prefab or baked meshes; " +
                                 "flying a plain box. Run Rubbish/Setup/WorldLife after the Neighborhood build.", k);
        }

        int n = tmpMeshes.Count;
        s.meshes = tmpMeshes.ToArray();
        s.materials = tmpMats.ToArray();
        s.pos = tmpPos.ToArray();
        s.rot = tmpRot.ToArray();
        s.scale = tmpScale.ToArray();
        s.shadows = tmpShadow.ToArray();
        s.poseSource = kt;

        // The proxy's collider: the parts' box in the proxy frame.
        Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var mb = s.meshes[i].bounds;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? mb.min.x : mb.max.x, (c & 2) == 0 ? mb.min.y : mb.max.y, (c & 4) == 0 ? mb.min.z : mb.max.z);
                Vector3 w = s.pos[i] + s.rot[i] * Vector3.Scale(corner, s.scale[i]);
                mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
            }
        }
        s.boxCenter = (mn + mx) * 0.5f;
        s.boxSize = Vector3.Max(mx - mn, Vector3.one * 0.06f);
        return s;
    }

    /// <summary>The renderers that make up a knockable: its assigned/auto list plus every active renderer under it.</summary>
    public static void CollectRenderers(Knockable k, List<Renderer> into)
    {
        into.Clear();
        var rs = k.Renderers;
        for (int i = 0; i < rs.Length; i++) if (rs[i] != null) into.Add(rs[i]);
        int before = into.Count;
        k.GetComponentsInChildren(false, extra);
        for (int i = 0; i < extra.Count; i++)
        {
            var r = extra[i];
            bool dup = false;
            for (int j = 0; j < before; j++) if (into[j] == r) { dup = true; break; }
            if (!dup) into.Add(r);
        }
        extra.Clear();
    }
    static readonly List<Renderer> extra = new List<Renderer>(16);
}

/// <summary>A pooled flying copy of a knocked prop (or a bit of debris). Plain C#: the director ticks them.</summary>
public sealed class KnockProxy
{
    public GameObject go;
    public Transform t;
    public Rigidbody rb;
    /// <summary>Prefab key (prefab proxies), "" (assembled) or "debris".</summary>
    public string poolKey;
    public Collider[] colliders;
    public BoxCollider box;
    public SphereCollider sphere;
    public readonly List<MeshFilter> filters = new List<MeshFilter>(8);
    public readonly List<MeshRenderer> renderers = new List<MeshRenderer>(8);
    public Vector3 restScale = Vector3.one;

    // ---- state the director owns
    public KnockableDirector.Down owner;
    public float restSeconds;
    public double ignoreUntil = -1;
    public TruckControllerV2 ignoreTruck;
    public double lastKick = -99;
    public bool frozen, parked, shrinking, inAwake;
    public float shrinkT;
    public double dieAt;                     // debris lifetime
    public TruckControllerV2 stuckTo;        // a cone on the bumper
    public double stuckUntil;
    public Vector3 stuckOffset;
    public Quaternion stuckRot;
}

/// <summary>Pools of <see cref="KnockProxy"/> under the WorldLife presentation root (they die with the anchor's scene).</summary>
public sealed class KnockProxyPool
{
    public const int ProxyLayer = 0;    // Default: the static world, trucks, players and each other all collide with it

    readonly Transform root;
    readonly Dictionary<string, Stack<KnockProxy>> freePrefab = new Dictionary<string, Stack<KnockProxy>>();
    readonly Stack<KnockProxy> freeAssembled = new Stack<KnockProxy>();
    readonly Stack<KnockProxy> freeDebris = new Stack<KnockProxy>();
    /// <summary>Every proxy collider → its proxy (the sensor re-kicks settled debris).</summary>
    public readonly Dictionary<Collider, KnockProxy> byCollider = new Dictionary<Collider, KnockProxy>(256);
    public int Created { get; private set; }

    public KnockProxyPool(Transform root) { this.root = root; }

    static void SetupBody(Rigidbody rb, float mass)
    {
        rb.mass = Mathf.Max(0.2f, mass);
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.3f;
        rb.maxAngularVelocity = 30f;
        rb.useGravity = true;
    }

    /// <summary>A proxy for this shape at the knockable's rest pose, active and dynamic (velocity zero).</summary>
    public KnockProxy Acquire(KnockShape s, Knockable k, float mass)
    {
        KnockProxy p;
        if (s.prefab != null)
        {
            if (!freePrefab.TryGetValue(s.prefabKey, out var st)) freePrefab[s.prefabKey] = st = new Stack<KnockProxy>();
            p = st.Count > 0 ? st.Pop() : CreatePrefab(s);
        }
        else
        {
            p = freeAssembled.Count > 0 ? freeAssembled.Pop() : CreateAssembled();
            Configure(p, s);
        }
        var src = s.poseSource != null ? s.poseSource : k.transform;
        p.t.SetPositionAndRotation(src.position, src.rotation);
        p.restScale = s.prefab != null ? src.lossyScale : Vector3.one;
        p.t.localScale = p.restScale;
        p.go.name = "KnockProxy_" + k.kind;
        Wake(p, mass);
        return p;
    }

    /// <summary>A bit of debris (a lemon, the hoop's ball): a sphere with this mesh and material.</summary>
    public KnockProxy AcquireDebris(Mesh mesh, Material mat, Vector3 pos, float diameter, float mass)
    {
        var p = freeDebris.Count > 0 ? freeDebris.Pop() : CreateDebris();
        p.filters[0].sharedMesh = mesh;
        p.renderers[0].sharedMaterial = mat;
        var mb = mesh != null ? mesh.bounds.size : Vector3.one;
        float m = Mathf.Max(0.01f, Mathf.Max(mb.x, Mathf.Max(mb.y, mb.z)));
        p.filters[0].transform.localScale = Vector3.one * (diameter / m);
        p.filters[0].transform.localPosition = mesh != null ? -mesh.bounds.center * (diameter / m) : Vector3.zero;
        p.sphere.radius = diameter * 0.5f;
        p.t.SetPositionAndRotation(pos, Quaternion.identity);
        p.restScale = Vector3.one;
        p.t.localScale = Vector3.one;
        Wake(p, mass);
        return p;
    }

    static void Wake(KnockProxy p, float mass)
    {
        p.owner = null;
        p.restSeconds = 0f;
        p.ignoreUntil = -1;
        p.ignoreTruck = null;
        p.frozen = p.parked = p.shrinking = p.inAwake = false;
        p.shrinkT = 0f;
        p.stuckTo = null;
        p.lastKick = -99;
        p.go.SetActive(true);
        for (int i = 0; i < p.colliders.Length; i++) if (p.colliders[i] != null) p.colliders[i].enabled = true;
        p.rb.isKinematic = false;
        p.rb.mass = Mathf.Max(0.2f, mass);
        p.rb.position = p.t.position;
        p.rb.rotation = p.t.rotation;
        p.rb.linearVelocity = Vector3.zero;
        p.rb.angularVelocity = Vector3.zero;
    }

    public void Release(KnockProxy p)
    {
        if (p == null || p.go == null) return;
        if (!p.rb.isKinematic) { p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; }
        p.rb.isKinematic = true;
        p.owner = null;
        p.stuckTo = null;
        p.t.localScale = p.restScale;
        p.go.SetActive(false);
        if (p.poolKey == "debris") freeDebris.Push(p);
        else if (string.IsNullOrEmpty(p.poolKey)) freeAssembled.Push(p);
        else
        {
            if (!freePrefab.TryGetValue(p.poolKey, out var st)) freePrefab[p.poolKey] = st = new Stack<KnockProxy>();
            st.Push(p);
        }
    }

    // ------------------------------------------------------------------ create

    KnockProxy CreatePrefab(KnockShape s)
    {
        var go = Object.Instantiate(s.prefab, root);
        go.SetActive(false);
        var p = new KnockProxy { go = go, t = go.transform, poolKey = s.prefabKey };
        p.rb = go.GetComponent<Rigidbody>();
        if (p.rb == null) p.rb = go.AddComponent<Rigidbody>();
        SetupBody(p.rb, p.rb.mass);
        p.colliders = go.GetComponentsInChildren<Collider>(true);
        if (p.colliders.Length == 0)
        {
            var b = go.AddComponent<BoxCollider>();   // a prefab without colliders: the renderer bounds
            p.colliders = new Collider[] { b };
        }
        Register(p);
        return p;
    }

    KnockProxy CreateAssembled()
    {
        var go = new GameObject("KnockProxy");
        go.SetActive(false);
        go.layer = ProxyLayer;
        go.transform.SetParent(root, false);
        var p = new KnockProxy { go = go, t = go.transform, poolKey = "" };
        p.rb = go.AddComponent<Rigidbody>();
        SetupBody(p.rb, 10f);
        p.box = go.AddComponent<BoxCollider>();
        p.colliders = new Collider[] { p.box };
        Register(p);
        return p;
    }

    KnockProxy CreateDebris()
    {
        var go = new GameObject("KnockDebris");
        go.SetActive(false);
        go.layer = ProxyLayer;
        go.transform.SetParent(root, false);
        var p = new KnockProxy { go = go, t = go.transform, poolKey = "debris" };
        p.rb = go.AddComponent<Rigidbody>();
        SetupBody(p.rb, 0.3f);
        p.rb.angularDamping = 0.8f;
        p.sphere = go.AddComponent<SphereCollider>();
        p.colliders = new Collider[] { p.sphere };
        AddSlot(p);
        Register(p);
        return p;
    }

    void Register(KnockProxy p)
    {
        Created++;
        for (int i = 0; i < p.colliders.Length; i++) if (p.colliders[i] != null) byCollider[p.colliders[i]] = p;
    }

    static void AddSlot(KnockProxy p)
    {
        var part = new GameObject("Part");
        part.layer = ProxyLayer;
        part.transform.SetParent(p.t, false);
        p.filters.Add(part.AddComponent<MeshFilter>());
        p.renderers.Add(part.AddComponent<MeshRenderer>());
    }

    static void Configure(KnockProxy p, KnockShape s)
    {
        int n = s.PartCount;
        while (p.filters.Count < n) AddSlot(p);
        for (int i = 0; i < p.filters.Count; i++)
        {
            var f = p.filters[i];
            bool use = i < n;
            if (f.gameObject.activeSelf != use) f.gameObject.SetActive(use);
            if (!use) continue;
            f.sharedMesh = s.meshes[i];
            var r = p.renderers[i];
            r.sharedMaterials = s.materials[i];
            r.shadowCastingMode = s.shadows[i];
            var ft = f.transform;
            ft.localPosition = s.pos[i];
            ft.localRotation = s.rot[i];
            ft.localScale = s.scale[i];
        }
        p.box.center = s.boxCenter;
        p.box.size = s.boxSize;
    }
}

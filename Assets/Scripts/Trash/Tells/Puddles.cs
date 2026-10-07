using System.Collections.Generic;
using UnityEngine;

/// <summary>What made a puddle (colour, and how slippery it is).</summary>
public enum PuddleKind : byte { Juice = 0, Splat = 1, Rain = 2 }

/// <summary>One puddle as the authority decided it. <see cref="born"/> is on <see cref="Puddles.Now"/> (server time in co-op).</summary>
public struct PuddleData
{
    public uint id;
    public Vector3 pos;
    public float radius;
    public double born;
    public float life;
    public PuddleKind kind;
}

/// <summary>
/// Juice puddles (F3, Docs/Multiplayer.md §14.3): leaky bags leave a slippery trail that lasts ~60 s, cannon misfires splat one.
/// Shared state: solo keeps them here; in co-op the host owns the list and <see cref="BagTellsNet"/> mirrors it to every guest
/// (a guest's <see cref="Spawn"/> is a request). Cheap by design: ≤ <see cref="Max"/> puddles, one shared material (they dry up by
/// shrinking, so no per-puddle material state and SRP batching stays on), one ground raycast per puddle per machine, and a
/// distance loop for queries.
/// <para><b>Hooks for other packages (F5 weather, F4):</b> <see cref="Spawn"/> (any machine), <see cref="SlipAt"/> (0..1 at a
/// point: puddles + rain), <see cref="RainWetness"/> (0..1 global slipperiness: ground everywhere gets a bit slippery and puddles
/// slipperier and longer-lived), <see cref="Count"/>.</para>
/// </summary>
public static class Puddles
{
    public const int Max = 96;
    public const float DefaultLife = 60f;
    // Rain should add a little slide, not behave like a leaky bag's juice slick.
    public const float RainPuddleSlipFactor = 0.37f;
    /// <summary>The last seconds of a puddle's life it spends drying up (shrinking).</summary>
    public const float DryTime = 12f;

    /// <summary>F5 weather: 0 dry … 1 downpour. Players slide a little everywhere (×0.45) and puddles get slipperier.</summary>
    public static float RainWetness;

    /// <summary>Co-op: set by <see cref="BagTellsNet"/> on a guest. Return true when the request went to the host.</summary>
    internal static System.Func<Vector3, float, PuddleKind, float, bool> NetRequest;
    /// <summary>Co-op host: a puddle was added / removed here (the net layer mirrors it).</summary>
    internal static System.Action<PuddleData> Added;
    internal static System.Action<uint> Removed;
    /// <summary>Co-op: true on a guest (the list is the host's mirror; nothing expires or spawns locally).</summary>
    internal static bool Mirror;
    /// <summary>Co-op: the shared clock (server time). Null = local time.</summary>
    internal static System.Func<double> Clock;

    static readonly List<PuddleData> list = new List<PuddleData>();
    static readonly List<PuddleView> views = new List<PuddleView>();
    static uint nextId = 1;

    public static int Count => list.Count;
    public static IReadOnlyList<PuddleData> All => list;
    public static double Now => Clock != null ? Clock() : Time.timeAsDouble;

    /// <summary>Rain puddles are scenery/mild slide, not the bag-dropping juice hazard.</summary>
    public static bool IsRain(uint puddleId)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].id == puddleId) return list[i].kind == PuddleKind.Rain;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        list.Clear();
        views.Clear();
        nextId = 1;
        RainWetness = 0f;
        NetRequest = null;
        Added = null;
        Removed = null;
        Mirror = false;
        Clock = null;
    }

    /// <summary>
    /// Drop a puddle at <paramref name="pos"/> (snapped to the ground below). Works on any machine: a guest asks the host.
    /// Returns false when the request was dropped (no ground).
    /// </summary>
    public static bool Spawn(Vector3 pos, float radius = 0.7f, PuddleKind kind = PuddleKind.Juice, float life = DefaultLife)
    {
        if (Mirror)
        {
            var f = NetRequest;
            return f != null && f(pos, radius, kind, life);
        }
        return AddLocal(pos, radius, kind, life) != 0;
    }

    /// <summary>Authority: add a puddle now (returns its id, 0 = none). Merges into a near puddle of the same kind instead of stacking.</summary>
    internal static uint AddLocal(Vector3 pos, float radius, PuddleKind kind, float life)
    {
        if (!Ground(pos, out var g)) return 0;
        radius = Mathf.Clamp(radius, 0.2f, 2.5f);
        if (RainWetness > 0.01f) life *= 1f + RainWetness;
        double now = Now;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p.kind != kind || (p.pos - g).sqrMagnitude > Sq(p.radius * 0.55f)) continue;
            // Close to an existing puddle: refresh it a little instead of stacking a new one on top.
            if (Age(p, now) < p.life * 0.5f) return p.id;
            RemoveAt(i);
            break;
        }
        while (list.Count >= Max) RemoveAt(0);
        var d = new PuddleData { id = nextId++, pos = g, radius = radius, born = now, life = Mathf.Max(5f, life), kind = kind };
        if (nextId == 0) nextId = 1;
        list.Add(d);
        try { Added?.Invoke(d); }
        catch (System.Exception e) { Debug.LogException(e); }
        return d.id;
    }

    static void RemoveAt(int i)
    {
        uint id = list[i].id;
        list.RemoveAt(i);
        try { Removed?.Invoke(id); }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    /// <summary>Co-op guest: replace the mirror with the host's list.</summary>
    internal static void SetMirror(List<PuddleData> from)
    {
        list.Clear();
        list.AddRange(from);
    }

    /// <summary>Authority: remove every puddle (mirrored to guests).</summary>
    public static void RemoveAll()
    {
        if (Mirror) return;
        for (int i = list.Count - 1; i >= 0; i--) RemoveAt(i);
    }

    /// <summary>Forget everything locally (session start/end; no callbacks).</summary>
    public static void Clear()
    {
        list.Clear();
        foreach (var v in views) if (v != null) v.Hide();
    }

    static float Sq(float x) => x * x;
    static float Age(in PuddleData p, double now) => (float)(now - p.born);

    /// <summary>Current visual/slip scale of a puddle (pops in over 0.25 s, dries by shrinking over the last <see cref="DryTime"/> s).</summary>
    public static float ScaleOf(in PuddleData p, double now)
    {
        float age = Age(p, now);
        if (age < 0f) age = 0f;
        float grow = Mathf.Clamp01(age / 0.25f);
        float dry = Mathf.Clamp01((p.life - age) / DryTime);
        return Mathf.Min(1f - (1f - grow) * (1f - grow), Mathf.Sqrt(dry));
    }

    /// <summary>Local slickness of one puddle; rain is deliberately below the knockdown threshold even in a storm.</summary>
    public static float LocalSlipStrength(PuddleKind kind, float scale, float rainWetness) =>
        Mathf.Clamp01(Mathf.Clamp01(scale * 1.2f) * (1f + Mathf.Clamp01(rainWetness) * 0.3f) *
                      (kind == PuddleKind.Rain ? RainPuddleSlipFactor : 1f));

    /// <summary>
    /// How slippery it is underfoot at <paramref name="pos"/>: 1 in a fresh puddle (fading as it dries), plus rain everywhere.
    /// </summary>
    public static float SlipAt(Vector3 pos) => SlipAt(pos, out _);

    public static float SlipAt(Vector3 pos, out uint puddleId)
    {
        puddleId = 0;
        // The rain wets the open town, while juice puddles can still be slippery inside a shop.
        float rain = Weather.IsOutdoors(pos) ? RainWetness : 0f;
        float best = rain * 0.45f;
        if (list.Count == 0) return best;
        double now = Now;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            float dy = pos.y - p.pos.y;
            if (dy < -0.6f || dy > 1.2f) continue;
            float dx = pos.x - p.pos.x, dz = pos.z - p.pos.z;
            float s = ScaleOf(p, now);
            float r = p.radius * s;
            float d2 = dx * dx + dz * dz;
            if (d2 > r * r) continue;
            float k = LocalSlipStrength(p.kind, s, rain);
            if (k > best) { best = k; puddleId = p.id; }
        }
        return Mathf.Clamp01(best);
    }

    // ==================================================================================== per frame (TellsRuntime)

    /// <summary>Authority: drop puddles that have dried up. Everyone: place / scale the views.</summary>
    internal static void Tick()
    {
        double now = Now;
        if (!Mirror)
            for (int i = list.Count - 1; i >= 0; i--)
                if (Age(list[i], now) >= list[i].life) RemoveAt(i);
        SyncViews(now);
    }

    static void SyncViews(double now)
    {
        while (views.Count < list.Count) views.Add(PuddleView.Create(views.Count));
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            if (v == null) { views[i] = v = PuddleView.Create(i); }
            if (i < list.Count) v.Show(list[i], ScaleOf(list[i], now));
            else v.Hide();
        }
    }

    // ==================================================================================== ground

    static readonly RaycastHit[] hits = new RaycastHit[12];
    const int GroundMask = (1 << 0) | (1 << 6) | (1 << 7);

    /// <summary>The ground point under <paramref name="pos"/> (static colliders only: not players, trucks or bags).</summary>
    public static bool Ground(Vector3 pos, out Vector3 point)
    {
        point = pos;
        int n = Physics.RaycastNonAlloc(pos + Vector3.up * 0.6f, Vector3.down, hits, 4f, GroundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            var col = h.collider;
            if (col == null || col is CharacterController) continue;
            var rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic) continue;
            if (h.normal.y < 0.6f) continue;
            if (h.distance < best) { best = h.distance; point = h.point; any = true; }
        }
        return any;
    }
}

/// <summary>A pooled puddle visual: two flat blobs (murky body + glossy highlight), one shared material each.</summary>
public sealed class PuddleView : MonoBehaviour
{
    static Material bodyMat, splatMat, rainMat, shineMat;
    static Mesh[] blobs;
    static Transform root;

    MeshRenderer body, shine;
    MeshFilter bodyFilter, shineFilter;
    uint shownId;
    PuddleKind shownKind = (PuddleKind)255;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { root = null; }

    internal static PuddleView Create(int index)
    {
        if (root == null)
        {
            var go = new GameObject("[Puddles]");
            Object.DontDestroyOnLoad(go);
            root = go.transform;
        }
        var v = new GameObject("Puddle " + index).AddComponent<PuddleView>();
        v.transform.SetParent(root, false);
        v.Build();
        v.gameObject.SetActive(false);
        return v;
    }

    void Build()
    {
        EnsureAssets();
        var b = new GameObject("Body");
        b.transform.SetParent(transform, false);
        bodyFilter = b.AddComponent<MeshFilter>();
        body = b.AddComponent<MeshRenderer>();
        body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        body.receiveShadows = false;
        var s = new GameObject("Shine");
        s.transform.SetParent(transform, false);
        s.transform.localPosition = new Vector3(0.12f, 0.004f, 0.08f);
        s.transform.localScale = new Vector3(0.45f, 1f, 0.3f);
        shineFilter = s.AddComponent<MeshFilter>();
        shine = s.AddComponent<MeshRenderer>();
        shine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shine.receiveShadows = false;
        shine.sharedMaterial = shineMat;
    }

    internal void Show(in PuddleData p, float scale)
    {
        if (!gameObject.activeSelf) gameObject.SetActive(true);
        if (shownId != p.id)
        {
            shownId = p.id;
            int k = (int)(p.id % (uint)blobs.Length);
            bodyFilter.sharedMesh = blobs[k];
            shineFilter.sharedMesh = blobs[(k + 1) % blobs.Length];
            transform.SetPositionAndRotation(p.pos + Vector3.up * 0.025f, Quaternion.Euler(0f, (p.id * 67u) % 360u, 0f));
        }
        if (shownKind != p.kind)
        {
            shownKind = p.kind;
            body.sharedMaterial = p.kind == PuddleKind.Splat ? splatMat : p.kind == PuddleKind.Rain ? rainMat : bodyMat;
        }
        float r = Mathf.Max(0.001f, p.radius * scale);
        transform.localScale = new Vector3(r, 1f, r);
    }

    internal void Hide()
    {
        shownId = 0;
        if (gameObject.activeSelf) gameObject.SetActive(false);
    }

    static void EnsureAssets()
    {
        if (blobs != null && bodyMat != null) return;
        bodyMat = Make("F3_PuddleJuice", new Color(0.30f, 0.33f, 0.10f, 0.82f));
        splatMat = Make("F3_PuddleSplat", new Color(0.36f, 0.27f, 0.10f, 0.86f));
        rainMat = Make("F3_PuddleRain", new Color(0.35f, 0.45f, 0.55f, 0.45f));
        shineMat = Make("F3_PuddleShine", new Color(0.85f, 0.95f, 0.6f, 0.35f));
        blobs = new Mesh[4];
        for (int i = 0; i < blobs.Length; i++) blobs[i] = BlobMesh(i);
    }

    static Material Make(string name, Color c)
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        var m = new Material(sh != null ? sh : Shader.Find("Sprites/Default")) { name = name, hideFlags = HideFlags.DontSave };
        PuppetKit.MakeTransparent(m);
        m.SetColor("_BaseColor", c);
        m.SetColor("_Color", c);
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 20; // under other transparents (smoke, text)
        m.enableInstancing = true;
        return m;
    }

    /// <summary>A lumpy unit disc (radius ≈ 1) lying flat in XZ, deterministic per variant.</summary>
    static Mesh BlobMesh(int variant)
    {
        const int N = 18;
        var v = new Vector3[N + 1];
        var t = new int[N * 3];
        v[0] = Vector3.zero;
        uint h = (uint)PickupVisuals.StableHash("puddle" + variant);
        for (int i = 0; i < N; i++)
        {
            float a = i / (float)N * Mathf.PI * 2f;
            h = h * 1664525u + 1013904223u;
            float wob = 0.78f + 0.22f * ((h >> 8) & 0xFFFF) / 65535f + 0.1f * Mathf.Sin(a * 3f + variant);
            v[i + 1] = new Vector3(Mathf.Cos(a) * wob, 0f, Mathf.Sin(a) * wob);
            t[i * 3] = 0;
            t[i * 3 + 1] = 1 + (i + 1) % N;
            t[i * 3 + 2] = 1 + i;
        }
        var m = new Mesh { name = "F3_PuddleBlob" + variant, hideFlags = HideFlags.DontSave };
        m.vertices = v;
        m.triangles = t;
        var n = new Vector3[v.Length];
        for (int i = 0; i < n.Length; i++) n[i] = Vector3.up;
        m.normals = n;
        m.RecalculateBounds();
        return m;
    }
}

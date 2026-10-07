using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The knocked hydrant's 20 s water geyser (Docs/WorldLife.md §6.1): a wobbling column, a crown, a puddle and spray, at
/// most <see cref="KnockTuning.MaxGeysers"/> at once (the oldest stops early). It launches what stands in it:
/// <list type="bullet">
/// <item>resting or thrown bags and loose items — on the HOST only (bags are host-owned: rule 15/16);</item>
/// <item>knock debris — on every machine (it's local cosmetics);</item>
/// <item>critters — host only (the host simulates them);</item>
/// <item>a player on foot gets a 3 m hop on THEIR OWN machine (the copy this machine owns, via CritterKnockback).</item>
/// </list>
/// Zero allocations per frame after warm-up (pooled visuals, NonAlloc overlaps, Fx bursts are pooled by Feel).
/// </summary>
public sealed class HydrantGeysers
{
    sealed class Geyser
    {
        public Vector3 at;
        public double start, until, nextBurst, nextSfx;
        public float height;
        public GameObject go;
        public Transform core, spray, crown, puddle;
    }

    readonly Transform root;
    readonly KnockableDirector dir;
    readonly List<Geyser> live = new List<Geyser>(KnockTuning.MaxGeysers);
    readonly Stack<Geyser> free = new Stack<Geyser>(KnockTuning.MaxGeysers);
    readonly Collider[] buf = new Collider[48];
    readonly Dictionary<int, double> cooldown = new Dictionary<int, double>(64);
    Material water, spray;
    static readonly Color WaterBurst = new Color(0.6f, 0.82f, 1f);

    /// <summary>Launches applied (host: bags/items/debris + critters; any machine: its own players) since wake.</summary>
    public int Launches { get; private set; }
    public int Active => live.Count;

    public HydrantGeysers(Transform root, KnockableDirector dir) { this.root = root; this.dir = dir; }

    /// <summary>Is a geyser running within <paramref name="radius"/> of this point?</summary>
    public bool AnyNear(Vector3 p, float radius)
    {
        for (int i = 0; i < live.Count; i++) if ((live[i].at - p).sqrMagnitude <= radius * radius) return true;
        return false;
    }

    public void Start(Vector3 at, double now)
    {
        if (live.Count >= KnockTuning.MaxGeysers) Stop(0);
        var g = free.Count > 0 ? free.Pop() : Create();
        g.at = at;
        g.start = now;
        g.until = now + WorldLifeTuning.Props.HydrantGeyserSeconds;
        g.nextBurst = now;
        g.nextSfx = now + 1.2;
        g.height = 0f;
        g.go.transform.position = at;
        g.go.SetActive(true);
        live.Add(g);
        Pose(g, now);
    }

    void Stop(int i)
    {
        var g = live[i];
        live.RemoveAt(i);
        if (g.go != null) g.go.SetActive(false);   // already destroyed when the scene was torn down first
        free.Push(g);
    }

    public void Clear()
    {
        for (int i = live.Count - 1; i >= 0; i--) Stop(i);
        cooldown.Clear();
    }

    /// <summary>Visuals, spray and sound (Update).</summary>
    public void Tick(double now)
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var g = live[i];
            if (now >= g.until) { Fx.Dust(g.at, 0.6f); Stop(i); continue; }
            Pose(g, now);
            if (now >= g.nextBurst)
            {
                g.nextBurst = now + 0.09;
                Fx.Burst(g.at + Vector3.up * g.height, WaterBurst, 5, 3.2f);
            }
            if (now >= g.nextSfx)
            {
                g.nextSfx = now + 1.6;
                KnockSfx.Play(WorldLifeIds.Sfx.Geyser, "hiss", g.at, 0.5f, 1f);
            }
        }
    }

    void Pose(Geyser g, double now)
    {
        float t = (float)(now - g.start), left = (float)(g.until - now);
        float ramp = Mathf.Clamp01(t / 0.35f) * Mathf.Clamp01(left / 1.2f);
        float h = (3.6f + 0.5f * Mathf.Sin(t * 9f) + 0.25f * Mathf.Sin(t * 23f)) * ramp;
        g.height = Mathf.Max(0.05f, h);
        g.core.localScale = new Vector3(0.3f, g.height * 0.5f, 0.3f);
        g.core.localPosition = new Vector3(0f, g.height * 0.5f, 0f);
        float sh = g.height * 0.82f;
        g.spray.localScale = new Vector3(0.6f + 0.06f * Mathf.Sin(t * 17f), sh * 0.5f, 0.6f + 0.06f * Mathf.Cos(t * 15f));
        g.spray.localPosition = new Vector3(0f, sh * 0.5f, 0f);
        float c = (0.8f + 0.15f * Mathf.Sin(t * 13f)) * Mathf.Max(0.2f, ramp);
        g.crown.localScale = new Vector3(c, c * 0.45f, c);
        g.crown.localPosition = new Vector3(0f, g.height, 0f);
        float pr = Mathf.Lerp(0.6f, 2.6f, Mathf.Clamp01(t / 6f));
        g.puddle.localScale = new Vector3(pr, 0.01f, pr);
    }

    /// <summary>Launch what stands in each column (FixedUpdate).</summary>
    public void FixedTick(bool isHost, double now)
    {
        if (live.Count == 0) return;
        int mask = Physics.AllLayers & ~(1 << 2);
        for (int gi = 0; gi < live.Count; gi++)
        {
            var g = live[gi];
            float top = Mathf.Max(0.6f, g.height);
            int n = Physics.OverlapCapsuleNonAlloc(g.at + Vector3.up * 0.15f, g.at + Vector3.up * top, KnockTuning.GeyserRadius, buf, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == null) continue;
                if (dir.TryGetProxy(c, out var proxy))
                {
                    // Debris is local cosmetics: every machine plays it.
                    if (Ready(proxy.go.GetInstanceID(), now, KnockTuning.GeyserCooldown)) { dir.GeyserLaunch(proxy, Jitter(g, c.transform.position, KnockTuning.GeyserLaunchUp)); Launches++; }
                    continue;
                }
                var carry = InParents<Carryable>(c.transform);
                if (carry != null)
                {
                    if (!isHost || carry.State == CarryState.Carried || carry.State == CarryState.Consumed || carry.IsInScriptedFlight) continue;
                    var rb = carry.Body;
                    if (rb == null || rb.isKinematic || !Ready(carry.GetInstanceID(), now, KnockTuning.GeyserCooldown)) continue;
                    rb.linearVelocity = Jitter(g, carry.transform.position, KnockTuning.GeyserLaunchUp);
                    rb.WakeUp();
                    Launches++;
                    continue;
                }
                var pc = InParents<PlayerController>(c.transform);
                if (pc != null && CoopPlayerHooks.IsLocalOwner(pc) && !pc.IsDriving && Ready(pc.GetInstanceID(), now, KnockTuning.GeyserPlayerCooldown))
                {
                    if (!pc.TryGetComponent<CritterKnockback>(out var kb)) kb = pc.gameObject.AddComponent<CritterKnockback>();
                    Vector3 away = pc.transform.position - g.at; away.y = 0f;
                    kb.Knock(away.sqrMagnitude > 0.001f ? away : Vector3.forward, 0.8f, WorldLifeTuning.Props.GeyserPlayerHop, 0.7f);
                    GameHUD.FloatingText(pc.transform.position + Vector3.up * 2.2f, "WHOOSH!", WaterBurst, 1.1f);
                    Sfx.Play("whoosh", pc.transform.position, 0.8f, 0.9f);
                    Launches++;
                }
            }
            if (!isHost) continue;
            var critters = Critter.All;
            for (int i = 0; i < critters.Count; i++)
            {
                var cr = critters[i];
                if (cr == null) continue;
                Vector3 d = cr.transform.position - g.at;
                if (d.y < -0.5f || d.y > top || d.x * d.x + d.z * d.z > (KnockTuning.GeyserRadius + 0.3f) * (KnockTuning.GeyserRadius + 0.3f)) continue;
                if (!Ready(cr.GetInstanceID(), now, 1.5f)) continue;
                cr.Launch(Jitter(g, cr.transform.position, KnockTuning.GeyserCritterUp));
                Launches++;
            }
        }
    }

    bool Ready(int id, double now, float cd)
    {
        if (cooldown.TryGetValue(id, out var next) && now < next) return false;
        cooldown[id] = now + cd;
        return true;
    }

    /// <summary>Straight up plus a little outward drift (deterministic from the position: looks only).</summary>
    static Vector3 Jitter(Geyser g, Vector3 p, float up)
    {
        Vector3 d = p - g.at; d.y = 0f;
        if (d.sqrMagnitude < 0.0004f) d = new Vector3(0.3f, 0f, 0.1f);
        return d.normalized * 0.8f + Vector3.up * up;
    }

    static T InParents<T>(Transform t) where T : Component
    {
        for (int i = 0; i < 8 && t != null; i++, t = t.parent)
            if (t.TryGetComponent<T>(out var c)) return c;
        return null;
    }

    Geyser Create()
    {
        if (water == null) water = Mat("water", new Color(0.45f, 0.72f, 1f, 0.78f));
        if (spray == null) spray = Mat("spray", new Color(0.8f, 0.92f, 1f, 0.42f));
        var g = new Geyser { go = new GameObject("HydrantGeyser") };
        g.go.transform.SetParent(root, false);
        g.core = Prim(g.go.transform, PrimitiveType.Cylinder, water);
        g.spray = Prim(g.go.transform, PrimitiveType.Cylinder, spray);
        g.crown = Prim(g.go.transform, PrimitiveType.Sphere, spray);
        g.puddle = Prim(g.go.transform, PrimitiveType.Cylinder, water);
        g.puddle.localPosition = new Vector3(0f, 0.02f, 0f);
        g.go.SetActive(false);
        return g;
    }

    static Transform Prim(Transform parent, PrimitiveType type, Material m)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = m;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    /// <summary>"mat/worldlife_knock_&lt;name&gt;" (Setup_WorldLifeKnock makes them transparent), else a runtime URP/Lit.</summary>
    static Material Mat(string name, Color c)
    {
        var m = RubbishAssets.Material(KnockRecipes.MaterialKey(name));
        if (m != null) return m;
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) return null;
        m = new Material(sh) { name = "WorldLife_Knock_" + name + " (runtime)", hideFlags = HideFlags.DontSave };
        MakeTransparent(m, c);
        return m;
    }

    /// <summary>URP/Lit set up as alpha-blended transparent water (used by the editor setup too).</summary>
    public static void MakeTransparent(Material m, Color c)
    {
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", 0.85f);
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b) * 0.25f);
        m.EnableKeyword("_EMISSION");
    }
}

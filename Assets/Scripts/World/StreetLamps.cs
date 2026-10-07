using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Street lamps for the day/night cycle (driven by <see cref="DayNightSky"/>). Finds the builder's
/// SM_streetlamp_0x props by name at runtime (so it survives NeighborhoodBuilder rebuilding "NeighborhoodGen"),
/// swaps their bulb submesh between a lit / unlit runtime material, and lights the road with a small pool of
/// realtime spot lights that always sit on the N lamps nearest the camera (the rest are emissive-only), fading
/// between lamps so nothing pops. At dusk the lamps flicker on in a wave rolling out from the depot
/// ("bzzt!"), little moths circle the lit lamps near you, and at dawn they click off again.
/// The pack's own baked spot lights on the lamp prefab are disabled.
/// </summary>
[DisallowMultipleComponent]
public class StreetLamps : MonoBehaviour
{
    [Header("Realtime lights (perf)")]
    [Tooltip("How many lamps (nearest the camera) get a real spot light. The rest only glow.")]
    [Range(0, 24)] public int realtimeLights = 8;
    [Tooltip("Seconds between nearest-lamp re-sorts.")]
    public float refreshInterval = 0.25f;
    [Tooltip("Seconds to fade a lamp's light in/out when it enters/leaves the nearest set.")]
    public float handoffFade = 0.6f;

    [Header("Look")]
    public Color lightColor = new Color(1f, 0.76f, 0.44f);
    public float lightIntensity = 20f;
    public float lightRange = 18f;
    public float spotAngle = 124f;
    public float innerSpotAngle = 50f;
    [ColorUsage(false, true)] public Color bulbEmission = new Color(5.5f, 3.6f, 1.3f);
    [Tooltip("Tilt of the light cone from straight down toward the lamp arm (the road), degrees.")]
    public float armTilt = 22f;

    [Header("Schedule (in-game hours)")]
    public float onHour = 19.3f;
    public float offHour = 6.0f;
    [Tooltip("Extra hours for the on/off wave to roll from the depot to the far end of town.")]
    public float waveSpread = 0.45f;

    [Header("Derp")]
    public bool moths = true;
    public int mothsPerLamp = 3;

    public static StreetLamps Current { get; private set; }

    /// <summary>Number of lamps found in the scene.</summary>
    public int LampCount => lamps.Count;
    /// <summary>Lamps currently switched on (by schedule).</summary>
    public int LitCount { get; private set; }
    /// <summary>Realtime lights currently shining.</summary>
    public int ActiveLights { get; private set; }

    class Lamp
    {
        public MeshRenderer renderer;
        public int slot = -1;
        public Material original;
        public Vector3 head;         // bulb position (world)
        public Vector3 lightDir;     // spot direction (world)
        public bool post;            // post-top lantern (wider, straight down)
        public float onHour, offHour;
        public bool scheduledOn;
        public bool shownLit;
        public bool down;            // knocked over (WorldLife): no light, no bulb, no moths
        public float flickerStart = -1f, flickerEnd = -1f;
        public float seed;
        public float weight;
        public bool targetNear;
        public Light light;
        public List<Light> builtIn = new List<Light>();
        public List<bool> builtInWasEnabled = new List<bool>();
    }

    class PoolLight
    {
        public Light light;
        public Lamp owner;
        public Transform[] moths;
        public Vector3[] mothPhase;
    }

    readonly List<Lamp> lamps = new List<Lamp>();
    readonly List<PoolLight> pool = new List<PoolLight>();
    Material litMat, unlitMat, mothMat;
    Transform poolRoot;
    float nextSort;
    float lastSfx, lastBzzt;
    bool scanned;
    static readonly Vector3 DepotOrigin = new Vector3(0f, 0f, -14f);

    // Bulb offsets (prefab-local) measured from the Suburbs pack meshes' emissive submesh.
    struct HeadDef { public string prefix; public Vector3 local; public Vector3 arm; public bool post; }
    static readonly HeadDef[] Heads =
    {
        new HeadDef { prefix = "SM_streetlamp_01", local = new Vector3(-1.54f, 5.29f, -0.01f), arm = Vector3.left, post = false },
        new HeadDef { prefix = "SM_streetlamp_02", local = new Vector3(0f, 3.95f, 0f), arm = Vector3.zero, post = true },
        new HeadDef { prefix = "SM_streetlamp_03", local = new Vector3(0f, 3.70f, 0f), arm = Vector3.zero, post = true },
    };

    void OnEnable() { Current = this; }
    void OnDisable() { if (Current == this) Current = null; }

    // ------------------------------------------------------------------------------------------
    // WorldLife hook (Docs/WorldLife.md §6.4; W0 API, package B behaviour). A down lamp is skipped by the nearest-N sort,
    // its pooled light goes dark at once (and fades its weight out), the bulb shows unlit and its moths leave; when it's
    // put back while scheduled on, it plays the usual flicker ("bzzt"). Static so callers need no StreetLamps reference;
    // Apply picks the change up on the next frame.
    // ------------------------------------------------------------------------------------------

    static readonly HashSet<Renderer> downLamps = new HashSet<Renderer>();

    /// <summary>A lamp's renderer (the SM_streetlamp_0x MeshRenderer) was knocked down (true) or put back (false).</summary>
    public static void SetLampDown(Renderer lampRenderer, bool down)
    {
        if (lampRenderer == null) return;
        if (down) downLamps.Add(lampRenderer);
        else downLamps.Remove(lampRenderer);
    }

    /// <summary>Is this lamp knocked down right now?</summary>
    public static bool IsLampDown(Renderer lampRenderer) => lampRenderer != null && downLamps.Contains(lampRenderer);

    /// <summary>Lamps knocked down right now.</summary>
    public static int DownCount => downLamps.Count;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetLampStatics() => downLamps.Clear();

    void OnDestroy()
    {
        RestoreOriginals();
        DestroyObj(litMat); DestroyObj(unlitMat); DestroyObj(mothMat);
    }

    // ------------------------------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------------------------------

    /// <summary>Find the lamps again (call after the neighborhood was rebuilt).</summary>
    public void Rescan()
    {
        RestoreOriginals();
        lamps.Clear();
        var scene = gameObject.scene;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (!root.activeInHierarchy) continue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.name.StartsWith("SM_streetlamp")) continue;
                var lamp = new Lamp { renderer = r };
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].name.StartsWith("M_emissive")) { lamp.slot = i; lamp.original = mats[i]; break; }

                HeadDef def = default;
                bool known = false;
                foreach (var h in Heads) if (r.name.StartsWith(h.prefix)) { def = h; known = true; break; }
                var t = r.transform;
                if (known)
                {
                    lamp.head = t.TransformPoint(def.local);
                    lamp.post = def.post;
                    Vector3 arm = def.arm == Vector3.zero ? Vector3.zero : t.TransformDirection(def.arm);
                    arm.y = 0f;
                    lamp.lightDir = arm.sqrMagnitude > 0.01f
                        ? (Vector3.down + arm.normalized * Mathf.Tan(armTilt * Mathf.Deg2Rad)).normalized
                        : Vector3.down;
                }
                else
                {
                    var b = r.bounds;
                    lamp.head = new Vector3(b.center.x, b.max.y - 0.25f, b.center.z);
                    lamp.post = true;
                    lamp.lightDir = Vector3.down;
                }

                // Deterministic per-lamp schedule: a wave from the depot outward + a little jitter.
                float h01 = Hash01(lamp.head);
                lamp.seed = h01 * 100f;
                float wave = Mathf.Clamp01(Vector3.Distance(Flat(lamp.head), DepotOrigin) / 260f);
                lamp.onHour = onHour + wave * waveSpread + h01 * 0.08f;
                lamp.offHour = offHour + wave * waveSpread * 0.6f + h01 * 0.08f;

                foreach (var l in r.GetComponentsInChildren<Light>(true))
                {
                    lamp.builtIn.Add(l);
                    lamp.builtInWasEnabled.Add(l.enabled);
                }
                lamps.Add(lamp);
            }
        }
        scanned = true;
        EnsureMaterials();
        EnsurePool();
        foreach (var l in lamps)
        {
            foreach (var b in l.builtIn) if (b != null) b.enabled = false;
            l.shownLit = false;
            SetBulb(l, false);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Drive every lamp for <paramref name="hour"/>. animate = play-mode juice (flicker, fades, sounds, moths);
    /// false = deterministic snapshot (edit-mode preview).
    /// </summary>
    public void Apply(float hour, bool animate, Vector3 viewer)
    {
        if (!scanned) Rescan();
        float now = Time.time;
        float dt = animate ? Time.deltaTime : 1f;
        hour = Mathf.Repeat(hour, 24f);

        int lit = 0;
        Lamp bzztLamp = null;
        float bzztDist = float.MaxValue;
        for (int i = 0; i < lamps.Count; i++)
        {
            var l = lamps[i];
            if (l.renderer == null) continue;
            bool on = hour >= l.onHour || hour < l.offHour;
            if (on != l.scheduledOn)
            {
                l.scheduledOn = on;
                if (animate)
                {
                    l.flickerStart = now;
                    l.flickerEnd = now + (on ? Random.Range(0.55f, 1.15f) : Random.Range(0.15f, 0.3f));
                    float d = Vector3.Distance(viewer, l.head);
                    if (d < 30f && now - lastSfx > 0.09f)
                    {
                        lastSfx = now;
                        Sfx.Play("click", l.head, on ? 0.3f : 0.2f, Random.Range(0.85f, 1.25f));
                    }
                    if (on && d < 26f && d < bzztDist) { bzztDist = d; bzztLamp = l; }
                }
            }
            if (on) lit++;

            // Knocked over (WorldLife): dark while down; flickers back on when City Works puts it back.
            bool isDown = downLamps.Count > 0 && downLamps.Contains(l.renderer);
            if (isDown != l.down)
            {
                l.down = isDown;
                if (!isDown && on && animate)
                {
                    l.flickerStart = now;
                    l.flickerEnd = now + Random.Range(0.55f, 1.15f);   // coop:allow(random) cosmetic flicker length
                }
            }

            bool visible = on;
            if (animate && now < l.flickerEnd)
            {
                // fluorescent-style stutter: mostly the new state, stuttering to the old one
                float n = Mathf.PerlinNoise(now * 23f, l.seed);
                float settle = Mathf.InverseLerp(l.flickerStart, l.flickerEnd, now);
                visible = on ? n > 0.62f - settle * 0.4f : n > 0.55f + settle * 0.4f;
            }
            if (l.down) visible = false;
            if (visible != l.shownLit)
            {
                l.shownLit = visible;
                SetBulb(l, visible);
            }
        }
        LitCount = lit;

        if (bzztLamp != null && now - lastBzzt > 2.5f)
        {
            lastBzzt = now;
            GameHUD.FloatingText(bzztLamp.head + Vector3.up * 0.5f, "bzzt!", new Color(1f, 0.85f, 0.35f), 0.8f);
        }

        // Nearest-N realtime lights.
        if (!animate || now >= nextSort)
        {
            nextSort = now + refreshInterval;
            SortNearest(viewer);
        }
        UpdateWeightsAndLights(animate ? dt : 1f, animate, now);
    }

    readonly List<Lamp> sortBuf = new List<Lamp>();
    Vector3 sortViewer;
    System.Comparison<Lamp> byDistance;

    void SortNearest(Vector3 viewer)
    {
        sortBuf.Clear();
        foreach (var l in lamps) if (l.renderer != null && l.scheduledOn && !l.down) sortBuf.Add(l);
        sortViewer = viewer;
        if (byDistance == null) byDistance = (a, b) => (a.head - sortViewer).sqrMagnitude.CompareTo((b.head - sortViewer).sqrMagnitude);
        sortBuf.Sort(byDistance);
        foreach (var l in lamps) l.targetNear = false;
        int n = Mathf.Min(realtimeLights, sortBuf.Count);
        for (int i = 0; i < n; i++) sortBuf[i].targetNear = true;
    }

    void UpdateWeightsAndLights(float dt, bool animate, float now)
    {
        float step = handoffFade > 0.01f ? dt / handoffFade : 1f;
        // 1) weights + release
        foreach (var l in lamps)
        {
            float target = l.targetNear && l.scheduledOn && !l.down ? 1f : 0f;
            l.weight = animate ? Mathf.MoveTowards(l.weight, target, step) : target;
            if (l.weight <= 0.001f && l.light != null) Release(l);
        }
        // 2) acquire for lamps that want light
        foreach (var l in lamps)
        {
            if (l.weight <= 0.001f || l.light != null) continue;
            var p = FreeLight();
            if (p == null) { l.weight = 0f; continue; }
            p.owner = l;
            l.light = p.light;
            p.light.transform.SetPositionAndRotation(l.head - Vector3.up * 0.15f, Quaternion.LookRotation(l.lightDir, Vector3.forward));
            p.light.spotAngle = l.post ? 150f : spotAngle;
            p.light.innerSpotAngle = l.post ? 70f : innerSpotAngle;
            p.light.range = l.post ? lightRange * 0.75f : lightRange;
            p.light.gameObject.SetActive(true);
        }
        // 3) drive intensities + moths
        int active = 0;
        foreach (var p in pool)
        {
            var l = p.owner;
            if (l == null) { if (p.light.gameObject.activeSelf) p.light.gameObject.SetActive(false); continue; }
            float k = l.weight * (l.shownLit ? 1f : 0f);
            p.light.intensity = lightIntensity * (l.post ? 0.7f : 1f) * k;
            p.light.color = lightColor;
            p.light.enabled = k > 0.001f;
            if (p.light.enabled) active++;
            if (p.moths != null)
            {
                bool show = moths && k > 0.3f;
                for (int m = 0; m < p.moths.Length; m++)
                {
                    var mt = p.moths[m];
                    if (mt.gameObject.activeSelf != show) mt.gameObject.SetActive(show);
                    if (!show) continue;
                    var ph = p.mothPhase[m];
                    float t = animate ? now : ph.x * 3f;
                    mt.localPosition = new Vector3(
                        Mathf.Sin(t * (2.1f + ph.y) + ph.x) * 0.38f,
                        Mathf.Sin(t * (3.3f + ph.z) + ph.y) * 0.16f,
                        Mathf.Cos(t * (1.7f + ph.z) + ph.z) * 0.38f) + new Vector3(0f, 0f, -0.15f);
                    mt.localRotation = Quaternion.Euler(0f, t * 400f + ph.x * 50f, Mathf.Sin(t * 30f) * 40f);
                }
            }
        }
        ActiveLights = active;
    }

    PoolLight FreeLight()
    {
        foreach (var p in pool) if (p.owner == null) return p;
        return null;
    }

    void Release(Lamp l)
    {
        foreach (var p in pool)
            if (p.owner == l)
            {
                p.owner = null;
                p.light.intensity = 0f;
                p.light.gameObject.SetActive(false);
            }
        l.light = null;
    }

    // ------------------------------------------------------------------------------------------
    // Materials / pool
    // ------------------------------------------------------------------------------------------

    void EnsureMaterials()
    {
        if (litMat != null) return;
        Material src = null;
        foreach (var l in lamps) if (l.original != null) { src = l.original; break; }
        if (src == null) src = RubbishAssets.Material("mat/sky_lamp_bulb");
        if (src == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) return;
            src = new Material(sh);
        }
        litMat = new Material(src) { name = "Sky_LampBulb_On", hideFlags = HideFlags.DontSave };
        litMat.EnableKeyword("_EMISSION");
        litMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        litMat.SetColor("_BaseColor", new Color(1f, 0.93f, 0.6f));
        litMat.SetColor("_EmissionColor", bulbEmission);

        unlitMat = new Material(src) { name = "Sky_LampBulb_Off", hideFlags = HideFlags.DontSave };
        unlitMat.EnableKeyword("_EMISSION");
        unlitMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        unlitMat.SetColor("_BaseColor", new Color(0.82f, 0.8f, 0.7f));
        unlitMat.SetColor("_EmissionColor", new Color(0.02f, 0.02f, 0.015f));

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit != null)
        {
            mothMat = new Material(lit) { name = "Sky_Moth", hideFlags = HideFlags.DontSave };
            mothMat.SetColor("_BaseColor", new Color(0.86f, 0.8f, 0.68f));
            mothMat.EnableKeyword("_EMISSION");
            mothMat.SetColor("_EmissionColor", new Color(0.35f, 0.3f, 0.2f));
        }
    }

    void EnsurePool()
    {
        int want = Mathf.Max(0, realtimeLights) + 3;
        if (poolRoot == null)
        {
            var existing = transform.Find("LampLights");
            if (existing != null) DestroyObj(existing.gameObject);
            var go = new GameObject("LampLights");
            if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
            poolRoot = go.transform;
            poolRoot.SetParent(transform, false);
            pool.Clear();
        }
        while (pool.Count < want)
        {
            var go = new GameObject("LampLight " + pool.Count);
            if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(poolRoot, false);
            var li = go.AddComponent<Light>();
            li.type = LightType.Spot;
            li.shadows = LightShadows.None;
            li.color = lightColor;
            li.intensity = 0f;
            li.range = lightRange;
            li.spotAngle = spotAngle;
            li.innerSpotAngle = innerSpotAngle;
            li.renderMode = LightRenderMode.Auto;
            var p = new PoolLight { light = li };
            if (mothMat != null && mothsPerLamp > 0)
            {
                p.moths = new Transform[mothsPerLamp];
                p.mothPhase = new Vector3[mothsPerLamp];
                for (int m = 0; m < mothsPerLamp; m++)
                {
                    var moth = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    DestroyObj(moth.GetComponent<Collider>());
                    moth.name = "Moth";
                    if (!Application.isPlaying) moth.hideFlags = HideFlags.DontSave;
                    moth.transform.SetParent(go.transform, false);
                    moth.transform.localScale = new Vector3(0.07f, 0.012f, 0.035f);
                    var mr = moth.GetComponent<MeshRenderer>();
                    mr.sharedMaterial = mothMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    p.moths[m] = moth.transform;
                    p.mothPhase[m] = new Vector3(Random.value * 6.28f, Random.value * 1.5f, Random.value * 1.5f);
                    moth.SetActive(false);
                }
            }
            go.SetActive(false);
            pool.Add(p);
        }
    }

    void SetBulb(Lamp l, bool lit)
    {
        if (l.slot < 0 || l.renderer == null) return;
        var m = lit ? litMat : unlitMat;
        if (m == null) return;
        var mats = l.renderer.sharedMaterials;
        if (l.slot >= mats.Length || mats[l.slot] == m) return;
        mats[l.slot] = m;
        l.renderer.sharedMaterials = mats;
    }

    /// <summary>Put the original bulb materials and the pack's own lights back (edit-mode preview cleanup).</summary>
    public void RestoreOriginals()
    {
        foreach (var l in lamps)
        {
            if (l.renderer != null && l.slot >= 0 && l.original != null)
            {
                var mats = l.renderer.sharedMaterials;
                if (l.slot < mats.Length && mats[l.slot] != l.original)
                {
                    mats[l.slot] = l.original;
                    l.renderer.sharedMaterials = mats;
                }
            }
            for (int i = 0; i < l.builtIn.Count; i++)
                if (l.builtIn[i] != null) l.builtIn[i].enabled = l.builtInWasEnabled[i];
            l.light = null;
            l.shownLit = false;
        }
        foreach (var p in pool) { p.owner = null; if (p.light != null) p.light.gameObject.SetActive(false); }
        scanned = false;
    }

    // ------------------------------------------------------------------------------------------

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    static float Hash01(Vector3 p)
    {
        unchecked
        {
            int h = Mathf.RoundToInt(p.x * 10f) * 73856093 ^ Mathf.RoundToInt(p.z * 10f) * 19349663 ^ Mathf.RoundToInt(p.y * 10f) * 83492791;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }

    static void DestroyObj(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}

using System.Diagnostics;
using UnityEngine;

/// <summary>Creates the weather's runtime objects once (physics everywhere; visuals and sound only with graphics).</summary>
public static class WeatherBoot
{
    static GameObject root;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => root = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Ensure()
    {
        if (root != null || !Application.isPlaying) return;
        root = new GameObject("[Weather]");
        Object.DontDestroyOnLoad(root);
        root.AddComponent<WeatherWind>();
        if (!Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            root.AddComponent<WeatherFx>();
    }
}

/// <summary>
/// F5 weather, presentation (Docs/Multiplayer.md §14.5). Local and cosmetic, driven by <see cref="Weather"/>:
/// <list type="bullet">
/// <item><b>Rain</b>: up to 900 streaks in a box that follows the camera, drawn with one instanced call (no ParticleSystem),
/// slanted by the wind.</item>
/// <item><b>Overcast</b>: after <c>DayNightSky</c> writes the light each frame, dims the sun, greys the ambient, fog and
/// sky (never compounding: DayNightSky rewrites them every Update). Storms flash lightning and roll thunder.</item>
/// <item><b>Wet look</b>: the global shader value <c>_RubbishWetness</c> (0..1) for any shader that wants it.</item>
/// <item><b>Sound</b>: a rain hiss and a wind whoosh (procedural noise loops) scaled by the weather; toasts on changes.</item>
/// <item><b>Sway</b>: street lamps' heads and trees near the camera rock with the gusts (only non-static objects).</item>
/// </list>
/// Only outdoors in the town (not in a shop pocket, not in the solo shop scene). Cost is measured into
/// <see cref="CostMsPerFrame"/> (budget 0.5 ms for all weather).
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class WeatherFx : MonoBehaviour
{
    public const int MaxDrops = 900;
    public const float RainLoopMaxVolume = 0.10f;
    public const float WindLoopMaxVolume = 0.18f;
    const float BoxHalf = 18f, BoxUp = 14f, BoxDown = 4f, DropFall = 15f;

    static readonly int WetId = Shader.PropertyToID("_RubbishWetness");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    static readonly int HorizonId = Shader.PropertyToID("_HorizonColor");
    static readonly int ZenithId = Shader.PropertyToID("_ZenithColor");

    /// <summary>Average weather cost per rendered frame (ms) over the last second: rain, light, sound, sway + wind physics.</summary>
    public static float CostMsPerFrame { get; private set; }
    public static int DropsDrawn { get; private set; }

    readonly Vector3[] drops = new Vector3[MaxDrops];
    readonly Matrix4x4[] mats = new Matrix4x4[MaxDrops];
    Mesh dropMesh;
    Material dropMat;
    bool dropsSeeded;
    uint rng = 0xC0FFEEu;
    Vector3 lastCam;

    AudioSource rainSrc, windSrc;
    float flash, nextFlash = 10f, thunderAt = -1f, thunderVol;
    WeatherKind shownKind;
    int shownSerial = -1;

    readonly Stopwatch watch = new Stopwatch();
    double costAcc;
    int costFrames;
    float costT;

    void Start()
    {
        BuildDropAssets();
        rainSrc = MakeLoop("RainHiss", NoiseClip("weather_rain", 3f, 0.0f), 0f);
        windSrc = MakeLoop("WindWhoosh", NoiseClip("weather_wind", 4f, 0.985f), 0f);
        Weather.Changed += OnChanged;
    }

    void OnDestroy()
    {
        Weather.Changed -= OnChanged;
        Shader.SetGlobalFloat(WetId, 0f);
    }

    void OnChanged(WeatherKind from, WeatherKind to)
    {
        if (!Outdoors()) return;
        string line = to switch
        {
            WeatherKind.Windy => "Wind's picking up! Loose trash on the move.",
            WeatherKind.Rain => "Rain! Slippery streets, slippery grips.",
            WeatherKind.Storm => "STORM! Hold on to your bags!",
            _ => from == WeatherKind.Rain || from == WeatherKind.Storm ? "Skies clearing up." : "Calm and clear.",
        };
        GameHUD.Toast(line, to == WeatherKind.Storm ? ToastStyle.Bad : ToastStyle.Info, 3f);
    }

    static bool Outdoors()
    {
        if (!WorldLifeRunner.IsAwake) return false;
        return !DepotRowWorld.LocalIndoors;
    }

    void LateUpdate()
    {
        watch.Restart();
        bool outdoors = Outdoors();
        var cam = Camera.main;
        float wet = Weather.Wetness;
        Shader.SetGlobalFloat(WetId, outdoors ? wet : 0f);
        if (outdoors && cam != null)
        {
            ApplyOvercast(Weather.Overcast);
            DrawRain(cam, Weather.Rain);
            Sway(cam.transform.position);
        }
        else { DropsDrawn = 0; if (swayers.Count > 0) RestoreSway(); }
        Sound(outdoors);
        watch.Stop();
        costAcc += watch.Elapsed.TotalMilliseconds;
        costFrames++;
        costT += Time.unscaledDeltaTime;
        if (costT >= 1f)
        {
            costAcc += WeatherWind.TakeCostMs();
            CostMsPerFrame = (float)(costAcc / Mathf.Max(1, costFrames));
            costAcc = 0;
            costFrames = 0;
            costT = 0f;
        }
    }

    // ------------------------------------------------------------------ overcast + lightning

    void ApplyOvercast(float o)
    {
        float storm = Weather.Kind == WeatherKind.Storm ? Weather.Blend : 0f;
        // Lightning: a white flash every 7–18 s in a storm, thunder 0.6–2.2 s later.
        float now = Time.time;
        if (storm > 0.5f && now >= nextFlash)
        {
            nextFlash = now + 7f + Rand() * 11f;
            flash = 1f;
            thunderAt = now + 0.6f + Rand() * 1.6f;
            thunderVol = 0.6f + Rand() * 0.4f;
        }
        if (thunderAt > 0f && now >= thunderAt)
        {
            thunderAt = -1f;
            Sfx.Play("boom", null, thunderVol, 0.35f + Rand() * 0.1f);
            Juice.Shake(0.08f, 0.35f);
        }
        flash = Mathf.MoveTowards(flash, 0f, Time.deltaTime * 5f);
        if (o <= 0.001f && flash <= 0f) return;

        var sun = RenderSettings.sun;
        if (sun != null) sun.intensity = sun.intensity * (1f - 0.72f * o) + flash * 1.6f;
        Color grey(Color c, float k) { float l = c.grayscale; return Color.Lerp(c, new Color(l, l, l * 1.05f, c.a), k); }
        RenderSettings.ambientSkyColor = grey(RenderSettings.ambientSkyColor, o * 0.75f) * (1f - 0.4f * o) + Color.white * (flash * 0.5f);
        RenderSettings.ambientEquatorColor = grey(RenderSettings.ambientEquatorColor, o * 0.75f) * (1f - 0.35f * o) + Color.white * (flash * 0.3f);
        RenderSettings.ambientGroundColor = grey(RenderSettings.ambientGroundColor, o * 0.6f) * (1f - 0.3f * o);
        var fogC = RenderSettings.fogColor;
        float dim = 1f - 0.45f * o;
        var stormFog = new Color(0.47f * dim, 0.5f * dim, 0.56f * dim);
        RenderSettings.fogColor = Color.Lerp(fogC, stormFog, o * 0.85f) + Color.white * (flash * 0.2f);
        RenderSettings.fogEndDistance *= 1f - 0.55f * o;
        RenderSettings.fogStartDistance *= 1f - 0.7f * o;
        var sky = RenderSettings.skybox;
        if (sky != null)
        {
            // The sky shader blends toward its gradient by _ZenithColor.a: push that toward a flat grey storm gradient.
            if (sky.HasProperty(ExposureId)) sky.SetFloat(ExposureId, sky.GetFloat(ExposureId) * (1f - 0.5f * o) + flash * 0.8f);
            if (sky.HasProperty(ZenithId))
            {
                var z = sky.GetColor(ZenithId);
                var zg = new Color(0.30f * dim, 0.33f * dim, 0.38f * dim, 1f) + Color.white * (flash * 0.4f);
                sky.SetColor(ZenithId, new Color(Mathf.Lerp(z.r, zg.r, o), Mathf.Lerp(z.g, zg.g, o), Mathf.Lerp(z.b, zg.b, o), Mathf.Max(z.a, 0.9f * o)));
            }
            if (sky.HasProperty(HorizonId))
            {
                var h = sky.GetColor(HorizonId);
                var hg = new Color(0.55f * dim, 0.58f * dim, 0.62f * dim, 1f) + Color.white * (flash * 0.4f);
                sky.SetColor(HorizonId, new Color(Mathf.Lerp(h.r, hg.r, o), Mathf.Lerp(h.g, hg.g, o), Mathf.Lerp(h.b, hg.b, o), Mathf.Max(h.a, 0.5f * o)));
            }
        }
    }

    // ------------------------------------------------------------------ rain

    void BuildDropAssets()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        dropMesh = cube.GetComponent<MeshFilter>().sharedMesh;
        var baseMat = RubbishAssets.Material("mat/fx_additive") ?? RubbishAssets.Material("mat/fx_unlit") ?? cube.GetComponent<MeshRenderer>().sharedMaterial;
        Destroy(cube);
        if (baseMat == null) return;
        dropMat = new Material(baseMat) { name = "WeatherRain", hideFlags = HideFlags.DontSave, enableInstancing = true };
        var c = new Color(0.62f, 0.72f, 0.9f, 0.45f);
        if (dropMat.HasProperty(BaseColorId)) dropMat.SetColor(BaseColorId, c);
        if (dropMat.HasProperty(ColorId)) dropMat.SetColor(ColorId, c);
    }

    void DrawRain(Camera cam, float rain)
    {
        DropsDrawn = 0;
        if (rain <= 0.01f || dropMat == null || dropMesh == null) return;
        Vector3 c = cam.transform.position;
        if (!dropsSeeded || (c - lastCam).sqrMagnitude > 60f * 60f)
        {
            dropsSeeded = true;
            for (int i = 0; i < MaxDrops; i++)
                drops[i] = c + new Vector3((Rand() * 2f - 1f) * BoxHalf, Rand() * (BoxUp + BoxDown) - BoxDown, (Rand() * 2f - 1f) * BoxHalf);
        }
        lastCam = c;
        int n = Mathf.Clamp(Mathf.RoundToInt(MaxDrops * rain), 0, MaxDrops);
        Vector3 vel = new Vector3(0f, -DropFall, 0f) + Weather.Wind * 0.7f;
        float dt = Time.deltaTime;
        Vector3 step = vel * dt;
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, -vel.normalized);
        Vector3 scale = new Vector3(0.018f, 0.55f + rain * 0.25f, 0.018f);
        float ground = c.y - BoxDown;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = drops[i] + step;
            // Wrap inside the camera box (falls through the bottom → reappears at the top, drifts out a side → the other side).
            if (p.y < ground) p.y += BoxUp + BoxDown;
            float dx = p.x - c.x, dz = p.z - c.z;
            if (dx > BoxHalf) p.x -= 2f * BoxHalf; else if (dx < -BoxHalf) p.x += 2f * BoxHalf;
            if (dz > BoxHalf) p.z -= 2f * BoxHalf; else if (dz < -BoxHalf) p.z += 2f * BoxHalf;
            drops[i] = p;
            mats[i] = Matrix4x4.TRS(p, rot, scale);
        }
        var rp = new RenderParams(dropMat)
        {
            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
            receiveShadows = false,
            worldBounds = new Bounds(c, new Vector3(BoxHalf * 2f + 4f, BoxUp + BoxDown + 4f, BoxHalf * 2f + 4f)),
        };
        Graphics.RenderMeshInstanced(rp, dropMesh, 0, mats, n);
        DropsDrawn = n;
    }

    // ------------------------------------------------------------------ sway (lamps + trees near the camera)

    readonly System.Collections.Generic.List<Transform> swayers = new System.Collections.Generic.List<Transform>(48);
    readonly System.Collections.Generic.List<Quaternion> swayBase = new System.Collections.Generic.List<Quaternion>(48);
    float nextSwayScan;
    Vector3 swayScanAt;

    void Sway(Vector3 cam)
    {
        float wind = Weather.WindSpeed;
        if (Time.time >= nextSwayScan || (cam - swayScanAt).sqrMagnitude > 20f * 20f) RescanSway(cam);
        if (swayers.Count == 0) return;
        Vector3 axis = Vector3.Cross(Vector3.up, Weather.WindDir);
        float gust = Weather.GustStrength01;
        float baseDeg = Mathf.Clamp01((wind - 2f) / 11f) * 5.5f;
        float t = Time.time;
        for (int i = 0; i < swayers.Count; i++)
        {
            var tr = swayers[i];
            if (tr == null) continue;
            float wob = Mathf.Sin(t * (1.7f + (i % 5) * 0.23f) + i) * (0.35f + gust * 0.65f);
            float deg = baseDeg * (0.6f + 0.4f * gust) + baseDeg * 0.5f * wob;
            tr.rotation = Quaternion.AngleAxis(deg, axis) * swayBase[i];
        }
    }

    void RestoreSway()
    {
        for (int i = 0; i < swayers.Count; i++) if (swayers[i] != null) swayers[i].rotation = swayBase[i];
        swayers.Clear();
        swayBase.Clear();
    }

    void RescanSway(Vector3 cam)
    {
        // Put back what we moved, then pick the nearest movable trees (≤ 40 within 35 m).
        RestoreSway();
        nextSwayScan = Time.time + 2f;
        swayScanAt = cam;
        if (Weather.WindSpeed < 2.5f) return;
        int n = Physics.OverlapSphereNonAlloc(cam, 35f, scan, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n && swayers.Count < 40; i++)
        {
            var col = scan[i];
            if (col == null || !(col is CapsuleCollider)) continue; // trees carry capsule colliders (Docs/WorldLife.md §5)
            var tr = col.transform;
            if (tr.gameObject.isStatic || tr.GetComponentInParent<Rigidbody>() != null) continue;
            if (tr.name.IndexOf("tree", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (swayers.Contains(tr) || tr.GetComponentInChildren<Renderer>() == null) continue;
            swayers.Add(tr);
            swayBase.Add(tr.rotation);
        }
    }

    readonly Collider[] scan = new Collider[256];

    void OnDisable() => RestoreSway();

    // ------------------------------------------------------------------ sound

    void Sound(bool outdoors)
    {
        // These are 2D AudioSources, so route their level through the same
        // ambience setting as the rest of the world's ambient sounds.
        float ambience = GameSettings.AmbienceVolume;
        float rainV = outdoors ? Weather.Rain * RainLoopMaxVolume * ambience : 0f;
        float windV = outdoors ? Mathf.Clamp01((Weather.WindSpeed - 1.5f) / 11f) * WindLoopMaxVolume * ambience : 0f;
        Fade(rainSrc, rainV, 1f);
        Fade(windSrc, windV, 0.85f + Weather.GustStrength01 * 0.35f);
    }

    static void Fade(AudioSource s, float target, float pitch)
    {
        if (s == null) return;
        s.volume = Mathf.MoveTowards(s.volume, target, Time.unscaledDeltaTime * 0.4f);
        s.pitch = Mathf.MoveTowards(s.pitch, pitch, Time.unscaledDeltaTime * 0.3f);
        bool want = s.volume > 0.001f;
        if (want && !s.isPlaying) s.Play();
        else if (!want && s.isPlaying) s.Stop();
    }

    AudioSource MakeLoop(string name, AudioClip clip, float vol)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = true;
        s.spatialBlend = 0f;
        s.volume = vol;
        s.playOnAwake = false;
        return s;
    }

    /// <summary>A seamless noise loop. <paramref name="lowpass"/> 0 = hiss (rain), ~0.98 = rumbly whoosh (wind).</summary>
    AudioClip NoiseClip(string name, float seconds, float lowpass)
    {
        int rate = 22050;
        int n = Mathf.RoundToInt(rate * seconds);
        var data = new float[n];
        float y = 0f, peak = 0.0001f;
        for (int i = 0; i < n; i++)
        {
            float w = Rand() * 2f - 1f;
            y = lowpass > 0f ? y * lowpass + w * (1f - lowpass) : w * 0.5f + y * 0.2f;
            data[i] = y;
            peak = Mathf.Max(peak, Mathf.Abs(y));
        }
        // Normalise and cross-fade the ends so the loop has no click.
        int fade = rate / 10;
        for (int i = 0; i < n; i++) data[i] /= peak;
        for (int i = 0; i < fade; i++)
        {
            float k = i / (float)fade;
            data[i] = data[i] * k + data[n - fade + i] * (1f - k);
        }
        var clip = AudioClip.Create(name, n - fade, 1, rate, false);
        var trimmed = new float[n - fade];
        System.Array.Copy(data, trimmed, n - fade);
        clip.SetData(trimmed, 0);
        return clip;
    }

    float Rand()
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return (rng & 0xFFFFFF) / 16777216f;
    }
}

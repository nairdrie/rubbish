using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Day/night for the Neighborhood, driven by <see cref="GameClock"/> (20 real minutes per day). Every frame it
/// crossfades the skybox (two "Day-Night Skyboxes" keyframes blended by the Rubbish/SkyBlend6Sided shader, with
/// twinkly stars, shooting stars and a derpy smiling moon painted on top), moves one directional light that
/// plays the sun by day and the moon by night, sets ambient (trilight) + fog + reflection strength from the
/// <see cref="SkyCycle"/> palette, and drives <see cref="StreetLamps"/> and <see cref="HouseWindows"/>.
///
/// Placed by Setup_World on a "WorldSky" root (outside NeighborhoodGen, so NeighborhoodBuilder rebuilds don't
/// delete it); NeighborhoodAtmosphere also creates one on the fly if a scene is missing it. Purely cosmetic and
/// local: every client runs its own from the shared server clock.
/// Edit mode: call <see cref="Preview(float)"/> to render any hour, then <see cref="EndPreview"/>.
/// </summary>
[DisallowMultipleComponent]
public class DayNightSky : MonoBehaviour
{
    [Header("Assets (Setup_World fills these; runtime falls back to RubbishAssets \"mat/sky_*\")")]
    [Tooltip("Material using Rubbish/SkyBlend6Sided (instanced at runtime, the asset is never modified).")]
    public Material blendMaterial;
    [Tooltip("The 6-sided skybox keyframes, matched by name to SkyCycle.SkyNames.")]
    public Material[] skyMaterials = new Material[0];
    [Tooltip("The scene's directional light (plays sun and moon). Found by name \"Sun\" / RenderSettings.sun if empty.")]
    public Light sun;

    [Header("Debug")]
    [Tooltip("Force an hour (0-24). Negative = follow GameClock.")]
    public float overrideHour = -1f;

    public static DayNightSky Current { get; private set; }

    /// <summary>The hour currently shown (GameClock.Hours, or the preview/override hour).</summary>
    public float Hour { get; private set; }
    public bool IsPreviewing { get; private set; }
    public StreetLamps Lamps => lamps;
    public HouseWindows Windows => windows;

    static readonly string[] Faces = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };

    StreetLamps lamps;
    HouseWindows windows;
    Material skyInstance;
    bool skyIsBlend;
    Material[] resolved;
    int curA = -1, curB = -1, curFallback = -1;
    bool initialized;
    bool envDirty;
    float lastEnvRefresh = -999f;
    bool ownedRender;

    // juice state
    float nextBlink, blinkStart = -1f;
    Vector4 look, lookTarget;
    float nextLook;
    float nextShoot, shootStart = -1f, shootDur;
    Vector3 shootFrom, shootTo;
    float lastMoonAmount = -1f;
    bool wishToastShown;

    // preview snapshot (so EndPreview leaves the scene as it was)
    bool snapped;
    Material snapSkybox;
    AmbientMode snapAmbientMode;
    Color snapAmbSky, snapAmbEq, snapAmbGround, snapFogColor, snapSunColor;
    bool snapFog, snapSunEnabled;
    FogMode snapFogMode;
    float snapFogStart, snapFogEnd, snapRefl, snapSunIntensity;
    Light snapRenderSun;
    Quaternion snapSunRot;

    void Awake()
    {
        Current = this;
        lamps = GetComponent<StreetLamps>();
        if (lamps == null) lamps = gameObject.AddComponent<StreetLamps>();
        windows = GetComponent<HouseWindows>();
        if (windows == null) windows = gameObject.AddComponent<HouseWindows>();
    }

    void OnEnable() { Current = this; }
    void OnDisable() { if (Current == this) Current = null; }

    void OnDestroy()
    {
        if (skyInstance != null)
        {
            if (RenderSettings.skybox == skyInstance && blendMaterial != null) RenderSettings.skybox = blendMaterial;
            if (Application.isPlaying) Destroy(skyInstance); else DestroyImmediate(skyInstance);
        }
    }

    // Start, not Awake: during an NGO Single load this scene may not be the active one yet in Awake, and
    // RenderSettings always belong to the active scene.
    void Start()
    {
        if (!Application.isPlaying) return;
        Init();
        Hour = ReadHour();
        lastMoonAmount = SkyCycle.MoonAmount(Hour);
        Apply(Hour, animate: false, Viewer());
        if (OwnsRenderSettings()) RefreshEnvironment();
    }

    /// <summary>RenderSettings belong to the active scene: only write them while ours is active (edit mode: always).</summary>
    bool OwnsRenderSettings() => !Application.isPlaying || SceneManager.GetActiveScene() == gameObject.scene;

    void Update()
    {
        if (!Application.isPlaying || IsPreviewing) return;
        if (!initialized) Init();
        Apply(ReadHour(), animate: true, Viewer());
    }

    float ReadHour() => overrideHour >= 0f ? Mathf.Repeat(overrideHour, 24f) : GameClock.Hours;

    // ------------------------------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------------------------------

    /// <summary>Edit-mode (or debug) snapshot of the whole sky at <paramref name="hour"/> (no animation).</summary>
    public void Preview(float hour) => Preview(hour, Viewer());

    /// <summary>Same, choosing which point counts as "the camera" for the nearest-lamp lights.</summary>
    public void Preview(float hour, Vector3 viewer)
    {
        if (lamps == null || windows == null) Awake();
        if (!snapped) Snapshot();
        IsPreviewing = true;
        Init();
        lamps.Rescan();
        windows.Rescan();
        Apply(Mathf.Repeat(hour, 24f), animate: false, viewer);
        RefreshEnvironment();
    }

    /// <summary>Undo a Preview: original materials, lights and render settings come back.</summary>
    public void EndPreview()
    {
        IsPreviewing = false;
        if (lamps != null) lamps.RestoreOriginals();
        if (windows != null) windows.RestoreOriginals();
        if (!snapped) return;
        snapped = false;
        RenderSettings.skybox = snapSkybox;
        RenderSettings.ambientMode = snapAmbientMode;
        RenderSettings.ambientSkyColor = snapAmbSky;
        RenderSettings.ambientEquatorColor = snapAmbEq;
        RenderSettings.ambientGroundColor = snapAmbGround;
        RenderSettings.fog = snapFog;
        RenderSettings.fogMode = snapFogMode;
        RenderSettings.fogColor = snapFogColor;
        RenderSettings.fogStartDistance = snapFogStart;
        RenderSettings.fogEndDistance = snapFogEnd;
        RenderSettings.reflectionIntensity = snapRefl;
        RenderSettings.sun = snapRenderSun;
        if (sun != null)
        {
            sun.transform.rotation = snapSunRot;
            sun.color = snapSunColor;
            sun.intensity = snapSunIntensity;
            sun.enabled = snapSunEnabled;
        }
    }

    /// <summary>Re-find lamps and houses (e.g. after the neighborhood was rebuilt while playing).</summary>
    public void Rescan()
    {
        if (lamps != null) lamps.Rescan();
        if (windows != null) windows.Rescan();
    }

    // ------------------------------------------------------------------------------------------
    // Setup
    // ------------------------------------------------------------------------------------------

    void Init()
    {
        if (sun == null) sun = FindSun();

        resolved = new Material[SkyCycle.SkyNames.Length];
        for (int i = 0; i < resolved.Length; i++)
        {
            string n = SkyCycle.SkyNames[i];
            if (skyMaterials != null)
                foreach (var m in skyMaterials) if (m != null && m.name == n) { resolved[i] = m; break; }
            if (resolved[i] == null) resolved[i] = RubbishAssets.Material("mat/sky_" + n);
#if UNITY_EDITOR
            if (resolved[i] == null)
                resolved[i] = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Day-Night Skyboxes/Materials/" + n + ".mat");
#endif
        }

        if (skyInstance == null)
        {
            Material src = blendMaterial != null ? blendMaterial : RubbishAssets.Material("mat/sky_blend");
#if UNITY_EDITOR
            if (src == null) src = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Generated/Sky/SkyBlend.mat");
#endif
            if (src == null)
            {
                var sh = Shader.Find("Rubbish/SkyBlend6Sided");
                if (sh != null) src = new Material(sh);
            }
            if (src != null)
            {
                skyInstance = new Material(src) { name = "Sky (runtime blend)", hideFlags = HideFlags.DontSave };
                skyIsBlend = true;
            }
            else
            {
                // No blend shader available: fall back to plain 6-sided skies swapped under an exposure dip.
                Material any = null;
                foreach (var m in resolved) if (m != null) { any = m; break; }
                if (any != null)
                {
                    skyInstance = new Material(any) { name = "Sky (runtime fallback)", hideFlags = HideFlags.DontSave };
                    skyIsBlend = false;
                }
            }
            curA = curB = curFallback = -1;
        }
        initialized = true;
    }

    Light FindSun()
    {
        if (RenderSettings.sun != null && RenderSettings.sun.gameObject.scene == gameObject.scene) return RenderSettings.sun;
        foreach (var root in gameObject.scene.GetRootGameObjects())
            if (root.name == "Sun") { var l = root.GetComponent<Light>(); if (l != null) return l; }
        foreach (var root in gameObject.scene.GetRootGameObjects())
            foreach (var l in root.GetComponentsInChildren<Light>(false))
                if (l.type == LightType.Directional) return l;
        return null;
    }

    void Snapshot()
    {
        snapped = true;
        snapSkybox = RenderSettings.skybox;
        snapAmbientMode = RenderSettings.ambientMode;
        snapAmbSky = RenderSettings.ambientSkyColor;
        snapAmbEq = RenderSettings.ambientEquatorColor;
        snapAmbGround = RenderSettings.ambientGroundColor;
        snapFog = RenderSettings.fog;
        snapFogMode = RenderSettings.fogMode;
        snapFogColor = RenderSettings.fogColor;
        snapFogStart = RenderSettings.fogStartDistance;
        snapFogEnd = RenderSettings.fogEndDistance;
        snapRefl = RenderSettings.reflectionIntensity;
        snapRenderSun = RenderSettings.sun;
        if (sun == null) sun = FindSun();
        if (sun != null)
        {
            snapSunRot = sun.transform.rotation;
            snapSunColor = sun.color;
            snapSunIntensity = sun.intensity;
            snapSunEnabled = sun.enabled;
        }
    }

    Vector3 Viewer()
    {
        var cam = Camera.main;
        if (cam != null && cam.gameObject.scene.IsValid()) return cam.transform.position;
        return transform.position;
    }

    void RefreshEnvironment()
    {
        lastEnvRefresh = Time.realtimeSinceStartup;
        envDirty = false;
        try { DynamicGI.UpdateEnvironment(); } catch (System.Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------------------------------

    void Apply(float hour, bool animate, Vector3 viewer)
    {
        Hour = hour;
        var look = SkyCycle.LookAt(hour);
        float darkness = SkyCycle.Darkness01(hour);
        Vector3 sunDir = SkyCycle.SunDirection(hour);
        Vector3 moonDir = SkyCycle.MoonDirection(hour);
        float moonAmount = SkyCycle.MoonAmount(hour);
        float starAmount = SkyCycle.StarAmount(hour);

        // --- skybox ---
        if (skyInstance != null)
        {
            SkyCycle.SkyPair(hour, out int a, out int b, out float blend);
            float exposure = look.exposure;
            if (skyIsBlend)
            {
                if (a != curA) { CopyFaces(resolved[a], ""); curA = a; envDirty = true; }
                if (b != curB) { CopyFaces(resolved[b], "B"); curB = b; }
                skyInstance.SetFloat("_Blend", blend);
                float sunUp = Smooth(-12f, -1f, SkyCycle.ElevationDeg(sunDir));
                skyInstance.SetColor("_HorizonColor", look.horizon);
                skyInstance.SetColor("_ZenithColor", look.zenith);
                skyInstance.SetColor("_SunGlowColor", look.sunGlow * sunUp);
                skyInstance.SetVector("_SunDir", sunDir);
                skyInstance.SetFloat("_StarAmount", starAmount);
                skyInstance.SetFloat("_MoonAmount", moonAmount);
                skyInstance.SetVector("_MoonDir", moonDir);
                AnimateMoonAndStars(animate, starAmount, moonAmount, viewer);
            }
            else
            {
                int pick = blend < 0.5f ? a : b;
                if (pick != curFallback && resolved[pick] != null) { CopyFaces(resolved[pick], ""); curFallback = pick; envDirty = true; }
                exposure *= 1f - 0.65f * (1f - Mathf.Abs(blend - 0.5f) * 2f) * (a != b ? 1f : 0f);
            }
            skyInstance.SetFloat("_Exposure", exposure);
        }

        // Our scene just became the active one (e.g. after an NGO scene load): refresh reflections right away.
        bool owns = OwnsRenderSettings();
        if (owns && !ownedRender) { envDirty = true; lastEnvRefresh = -999f; }
        ownedRender = owns;

        // --- sun / moon light (a scene object: always ours to drive) ---
        if (sun != null)
        {
            SkyCycle.MainLight(hour, out Vector3 toLight, out Color col, out float intensity, out bool isMoon);
            sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
            sun.color = col;
            sun.intensity = intensity;
            sun.shadowStrength = isMoon ? 0.6f : 0.82f;
            sun.enabled = intensity > 0.002f;
        }

        if (owns)
        {
            if (skyInstance != null && RenderSettings.skybox != skyInstance) RenderSettings.skybox = skyInstance;
            if (sun != null && RenderSettings.sun != sun) RenderSettings.sun = sun;

            // --- ambient / fog / reflections ---
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = look.ambientSky;
            RenderSettings.ambientEquatorColor = look.ambientEquator;
            RenderSettings.ambientGroundColor = look.ambientGround;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = look.fog;
            RenderSettings.fogStartDistance = look.fogStart;
            RenderSettings.fogEndDistance = look.fogEnd;
            RenderSettings.reflectionIntensity = look.reflection;
        }

        // --- lamps + windows ---
        if (lamps != null) lamps.Apply(hour, animate, viewer);
        if (windows != null) windows.Apply(hour, darkness, animate, viewer);

        // Reflections follow the sky a few times a day (cheap, but not every frame).
        if (animate && owns && envDirty && Time.realtimeSinceStartup - lastEnvRefresh > 6f) RefreshEnvironment();
    }

    void CopyFaces(Material from, string suffix)
    {
        if (from == null || skyInstance == null) return;
        foreach (var f in Faces)
        {
            if (!from.HasProperty(f)) continue;
            skyInstance.SetTexture(f + suffix, from.GetTexture(f));
        }
        if (!skyIsBlend && from.HasProperty("_Tint")) skyInstance.SetColor("_Tint", from.GetColor("_Tint"));
    }

    // Googly moon eyes, blinks, shooting stars and the odd toast. Deterministic when not animating.
    void AnimateMoonAndStars(bool animate, float starAmount, float moonAmount, Vector3 viewer)
    {
        if (!animate)
        {
            skyInstance.SetVector("_MoonLook", new Vector4(0.35f, -0.25f, -0.2f, -0.35f)); // a little cross-eyed
            skyInstance.SetFloat("_MoonBlink", 0f);
            skyInstance.SetVector("_ShootA", Vector4.zero);
            return;
        }

        float now = Time.time;
        float dt = Time.deltaTime;

        // wandering pupils (the right eye never quite agrees with the left)
        if (now >= nextLook)
        {
            nextLook = now + Random.Range(1.2f, 3.8f);
            Vector2 l = Random.insideUnitCircle * 0.95f;
            Vector2 r = Vector2.Lerp(l, Random.insideUnitCircle, 0.35f);
            lookTarget = new Vector4(l.x, l.y, r.x, r.y);
        }
        look = Vector4.Lerp(look, lookTarget, 1f - Mathf.Exp(-dt * 7f));
        skyInstance.SetVector("_MoonLook", look);

        // blink every few seconds (sometimes a double blink)
        if (blinkStart < 0f && now >= nextBlink)
        {
            blinkStart = now;
            nextBlink = now + (Random.value < 0.2f ? 0.35f : Random.Range(3f, 7f));
        }
        float blink = 0f;
        if (blinkStart >= 0f)
        {
            float t = (now - blinkStart) / 0.18f;
            if (t >= 1f) blinkStart = -1f; else blink = Mathf.Sin(t * Mathf.PI);
        }
        skyInstance.SetFloat("_MoonBlink", blink);

        // moonrise toast (only when it actually happens while you're here, not on scene load)
        if (lastMoonAmount >= 0f && lastMoonAmount < 0.5f && moonAmount >= 0.5f && SkyCycle.Darkness01(Hour) > 0.3f)
        {
            GameHUD.Toast("Moonrise! The moon looks... suspiciously happy.", ToastStyle.Info, 3f);
            Sfx.Play("sparkle", null, 0.35f, 0.8f);
        }
        lastMoonAmount = moonAmount;

        // shooting stars on properly dark nights
        if (starAmount > 0.8f)
        {
            if (nextShoot <= 0f) nextShoot = now + Random.Range(8f, 20f);
            if (shootStart < 0f && now >= nextShoot)
            {
                shootStart = now;
                shootDur = Random.Range(0.7f, 1.1f);
                nextShoot = now + Random.Range(14f, 38f);
                // start somewhere in front of the camera-ish, streak sideways and down
                float az = Random.Range(0f, 360f);
                float el = Random.Range(28f, 58f);
                shootFrom = Quaternion.Euler(-el, az, 0f) * Vector3.forward;
                shootTo = Quaternion.Euler(-el + Random.Range(8f, 16f), az + Random.Range(22f, 38f) * (Random.value < 0.5f ? -1f : 1f), 0f) * Vector3.forward;
                Sfx.Play("sparkle", null, 0.15f, 1.7f);
                if (!wishToastShown && Random.value < 0.35f)
                {
                    wishToastShown = true;
                    GameHUD.Toast("Shooting star! Make a wish (for fewer raccoons).", ToastStyle.Info, 2.6f);
                }
            }
        }
        else if (SkyCycle.Daylight01(Hour) > 0.9f) wishToastShown = false; // one wish toast per night

        if (shootStart >= 0f)
        {
            float p = (now - shootStart) / Mathf.Max(0.1f, shootDur);
            if (p >= 1f) { shootStart = -1f; skyInstance.SetVector("_ShootA", Vector4.zero); }
            else
            {
                skyInstance.SetVector("_ShootA", new Vector4(shootFrom.x, shootFrom.y, shootFrom.z, starAmount));
                skyInstance.SetVector("_ShootB", new Vector4(shootTo.x, shootTo.y, shootTo.z, p));
            }
        }
    }

    static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Root of <c>Casino.unity</c> (built by <c>Setup_Casino</c>, Docs/Casino.md §2). The scene is loaded next to the town on
/// every machine and holds no NetworkObjects; this component does this machine's local bits:
/// <list type="bullet">
/// <item>the whole interior is off unless the local player is inside (or on their way in), so it costs nothing from the
/// street;</item>
/// <item>indoor lighting while inside (a dim warm ambient, no fog, no sun), restored on the way out, exactly like
/// <see cref="DepotRowScene"/> does for the shops;</item>
/// <item>the indoor grade: a runtime global Volume (warm, slightly crushed blacks, bloom for the neon) whose weight is
/// 1 only while inside.</item>
/// </list>
/// Everything is keyed off where the player IS (<see cref="CasinoWorld.LocalInside"/>), not off a door having been
/// used, so leaving by any other means (a pass-out, Unstuck, a scene change) cleans up by itself.
/// </summary>
[DefaultExecutionOrder(900)] // after DayNightSky's Update so indoor lighting wins this frame
public class CasinoScene : MonoBehaviour
{
    public static CasinoScene Instance { get; private set; }

    [Tooltip("Everything in the room. Active only while the local player is inside.")]
    public GameObject interior;
    [Tooltip("Where you arrive (feet, facing into the room).")]
    public Transform entrance;
    public CasinoStation[] stations = new CasinoStation[0];

    [Header("Indoor lighting")]
    public Color ambientSky = new Color(0.30f, 0.235f, 0.19f);
    public Color ambientEquator = new Color(0.22f, 0.17f, 0.14f);
    public Color ambientGround = new Color(0.13f, 0.095f, 0.08f);

    public const float VolumePriority = 300f; // under the rabies / drunk looks (500)

    bool indoorLit;
    AmbientMode savedMode;
    Color savedSky, savedEq, savedGround;
    bool savedFog;
    float savedRefl;
    Light savedSun;
    bool savedSunEnabled;

    GameObject volumeGo;
    Volume volume;
    VolumeProfile profile;

    void OnEnable()
    {
        Instance = this;
        if (Application.isPlaying && interior != null) interior.SetActive(false);
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        EndIndoorLighting();
        DestroyGrade();
    }

    void Update()
    {
        if (!Application.isPlaying || interior == null) return;
        bool want = CasinoWorld.LocalInside;
        if (interior.activeSelf != want) interior.SetActive(want);
    }

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        if (CasinoWorld.LocalInside)
        {
            ApplyIndoorLighting();
            if (volume == null) BuildGrade();
            volume.weight = Mathf.MoveTowards(volume.weight, 1f, Time.unscaledDeltaTime * 4f);
        }
        else
        {
            EndIndoorLighting();
            if (volume != null) volume.weight = 0f;
        }
    }

    // ------------------------------------------------------------------ lighting

    void ApplyIndoorLighting()
    {
        if (!indoorLit)
        {
            indoorLit = true;
            savedMode = RenderSettings.ambientMode;
            savedSky = RenderSettings.ambientSkyColor;
            savedEq = RenderSettings.ambientEquatorColor;
            savedGround = RenderSettings.ambientGroundColor;
            savedFog = RenderSettings.fog;
            savedRefl = RenderSettings.reflectionIntensity;
            savedSun = RenderSettings.sun;
            savedSunEnabled = savedSun != null && savedSun.enabled;
        }
        // DayNightSky writes these every Update; we run later in the frame, so the room's look wins while inside.
        ApplyRoomAmbient(this);
        if (RenderSettings.sun != null && RenderSettings.sun.enabled) RenderSettings.sun.enabled = false;
    }

    /// <summary>The room's ambient (also used by the edit-mode preview renders).</summary>
    public static void ApplyRoomAmbient(CasinoScene s)
    {
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = s.ambientSky;
        RenderSettings.ambientEquatorColor = s.ambientEquator;
        RenderSettings.ambientGroundColor = s.ambientGround;
        RenderSettings.fog = false;
        RenderSettings.reflectionIntensity = 0.15f;
    }

    void EndIndoorLighting()
    {
        if (!indoorLit) return;
        indoorLit = false;
        RenderSettings.ambientMode = savedMode;
        RenderSettings.ambientSkyColor = savedSky;
        RenderSettings.ambientEquatorColor = savedEq;
        RenderSettings.ambientGroundColor = savedGround;
        RenderSettings.fog = savedFog;
        RenderSettings.reflectionIntensity = savedRefl;
        if (savedSun != null) savedSun.enabled = savedSunEnabled || savedSun.intensity > 0.002f;
    }

    // ------------------------------------------------------------------ grade

    void BuildGrade()
    {
        volumeGo = new GameObject("[CasinoVolume]") { layer = 0, hideFlags = HideFlags.DontSave };
        volumeGo.transform.SetParent(transform, false);
        profile = BuildProfile();
        volume = volumeGo.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = VolumePriority;
        volume.weight = 0f;
        volume.sharedProfile = profile;
    }

    void DestroyGrade()
    {
        if (profile != null) { if (Application.isPlaying) Destroy(profile); else DestroyImmediate(profile); }
        if (volumeGo != null) { if (Application.isPlaying) Destroy(volumeGo); else DestroyImmediate(volumeGo); }
        profile = null; volume = null; volumeGo = null;
    }

    /// <summary>The indoor look: warm, a little crushed, neon blooms. Also used by the edit-mode preview renders.</summary>
    public static VolumeProfile BuildProfile()
    {
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        p.name = "CasinoProfile (runtime)";
        p.hideFlags = HideFlags.DontSave;

        var color = p.Add<ColorAdjustments>(false);
        color.postExposure.Override(0.2f);
        color.contrast.Override(8f);
        color.saturation.Override(-10f);
        color.colorFilter.Override(new Color(1f, 0.96f, 0.9f));

        var tone = p.Add<Tonemapping>(false);
        tone.mode.Override(TonemappingMode.Neutral);

        var wb = p.Add<WhiteBalance>(false);
        wb.temperature.Override(5f);
        wb.tint.Override(2f);

        var lgg = p.Add<LiftGammaGain>(false);
        lgg.lift.Override(new Vector4(1f, 0.97f, 0.96f, -0.012f));   // slightly crushed, faintly warm blacks
        lgg.gamma.Override(new Vector4(1f, 0.98f, 0.95f, 0.02f));

        var bloom = p.Add<Bloom>(false);
        bloom.threshold.Override(1.0f);
        bloom.intensity.Override(1.0f);
        bloom.scatter.Override(0.72f);
        bloom.tint.Override(new Color(1f, 0.9f, 0.82f));

        var vig = p.Add<Vignette>(false);
        vig.intensity.Override(0.3f);
        vig.smoothness.Override(0.45f);
        vig.color.Override(new Color(0.08f, 0.03f, 0.02f));

        var grain = p.Add<FilmGrain>(false);
        grain.type.Override(FilmGrainLookup.Thin1);
        grain.intensity.Override(0.22f);
        grain.response.Override(0.8f);
        return p;
    }
}

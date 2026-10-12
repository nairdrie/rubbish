using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The drunk look (Docs/Casino.md §5): our own global URP Volume with a runtime profile, so restoring is just destroying it.
/// A whole-frame blur that slowly pulses (Gaussian Depth of Field with the focus pulled to the lens, the mode every URP
/// renderer path supports), a little camera Motion Blur, double vision (Chromatic Aberration and a breathing Lens
/// Distortion) and a slightly warm, washed-out grade. No hue shift and no vignette: those are rabies'.
///
/// Priority 490 sits under <see cref="RabiesPostFx"/> (500): rabid and drunk at once, rabies' lens, colour and vignette win
/// where the two overlap and the blur (which rabies does not touch) stays on top of the trip.
/// Owned by DrunkStatus; call <see cref="Apply"/> every frame and <see cref="Dispose"/> to restore.
/// </summary>
public sealed class DrunkPostFx
{
    public const float Priority = 490f;

    GameObject go;
    Volume volume;
    VolumeProfile profile;
    DepthOfField dof;
    MotionBlur motion;
    LensDistortion lens;
    ChromaticAberration chroma;
    ColorAdjustments color;
    UniversalAdditionalCameraData camData;
    bool camPpForced;

    public bool Alive => go != null;

    void Build()
    {
        go = new GameObject("[DrunkVolume]");
        go.layer = 0; // cameras use volume mask = Default; lives in the active scene
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "DrunkProfile (runtime)";
        profile.hideFlags = HideFlags.DontSave;

        dof = profile.Add<DepthOfField>(false);
        dof.mode.overrideState = true;
        dof.mode.value = DepthOfFieldMode.Gaussian;
        dof.gaussianStart.overrideState = true;
        dof.gaussianEnd.overrideState = true;
        dof.gaussianMaxRadius.overrideState = true;
        dof.highQualitySampling.overrideState = true;
        dof.highQualitySampling.value = false;

        motion = profile.Add<MotionBlur>(false);
        motion.intensity.overrideState = true;

        lens = profile.Add<LensDistortion>(false);
        lens.intensity.overrideState = true;
        lens.scale.overrideState = true;
        lens.xMultiplier.overrideState = true;
        lens.yMultiplier.overrideState = true;

        chroma = profile.Add<ChromaticAberration>(false);
        chroma.intensity.overrideState = true;

        color = profile.Add<ColorAdjustments>(false);
        color.saturation.overrideState = true;
        color.colorFilter.overrideState = true;
        color.postExposure.overrideState = true;

        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = Priority;
        volume.weight = 0f;
        volume.sharedProfile = profile;
    }

    /// <summary>Drive the effect. intensity 0..1 (already scaled by the Screen effects setting), t = time for the pulses.</summary>
    public void Apply(float intensity, float t)
    {
        if (intensity <= 0.001f)
        {
            if (volume != null) volume.weight = 0f;
            RestoreCamera();
            return;
        }
        if (go == null) Build();
        EnsureCameraPostProcessing();

        float i = Mathf.Clamp01(intensity);
        // Full weight and our own scaling: the blur's focus distances must not be blended against the scene's defaults.
        volume.weight = 1f;

        // The blur breathes: a slow swell (about 10 s) with a shorter one across it, never fully still and never fully gone.
        float pulse = 0.5f + 0.5f * (Mathf.Sin(t * 0.62f) * 0.7f + Mathf.Sin(t * 1.37f + 0.8f) * 0.3f);
        float blur = i * Mathf.Lerp(0.45f, 1f, pulse);
        dof.active = blur > 0.02f;
        dof.gaussianStart.value = 0f;
        // Everything beyond "end" gets the full radius: pulled in from far away (only the distance is soft) to under a metre.
        dof.gaussianEnd.value = Mathf.Lerp(45f, 0.6f, Mathf.Pow(blur, 0.55f));
        dof.gaussianMaxRadius.value = Mathf.Lerp(0.5f, 1.5f, blur);

        motion.intensity.value = Mathf.Lerp(0.15f, 0.7f, i);

        // Double vision: the two "eyes" drift apart and back.
        float wob = Mathf.Sin(t * 0.5f) * 0.8f + Mathf.Sin(t * 1.3f + 2.1f) * 0.2f;
        lens.intensity.value = wob * Mathf.Lerp(0.02f, 0.16f, i);
        lens.scale.value = 1f + Mathf.Abs(lens.intensity.value) * 0.25f; // hide the black corners
        lens.xMultiplier.value = 0.85f + Mathf.Sin(t * 0.41f) * 0.15f;
        lens.yMultiplier.value = 0.85f + Mathf.Cos(t * 0.47f) * 0.15f;
        chroma.intensity.value = Mathf.Clamp01(Mathf.Lerp(0.08f, 0.6f, i) * (0.75f + 0.25f * Mathf.Sin(t * 0.9f + 1f)));

        // A warm, slightly washed-out pub glow.
        color.saturation.value = -Mathf.Lerp(3f, 22f, i);
        color.colorFilter.value = Color.Lerp(Color.white, new Color(1f, 0.93f, 0.82f), i);
        color.postExposure.value = -0.12f * i;
    }

    void EnsureCameraPostProcessing()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var d = cam.GetComponent<UniversalAdditionalCameraData>();
        if (d != camData) { RestoreCamera(); camData = d; }
        // Drunk again on a camera whose flag we still owe back (see RestoreCamera): it is ours to restore once more.
        if (camData != null && ReferenceEquals(camData, owedCam)) { owedCam = null; camPpForced = true; }
        // Checked every frame: RabiesPostFx may have forced the flag before us and then put it back when rabies ended.
        if (camData != null && !camData.renderPostProcessing)
        {
            camData.renderPostProcessing = true;
            camPpForced = true;
        }
    }

    /// <summary>A camera whose post-processing flag we forced on and could not put back because rabies was still showing
    /// through it. RabiesPostFx found the flag already on, so it will never clear it: we do, in <see cref="SettleCamera"/>.</summary>
    static UniversalAdditionalCameraData owedCam;

    void RestoreCamera()
    {
        if (camData != null && camPpForced)
        {
            // While rabies is showing it needs the flag too (it only checks when the camera changes): leave it on and
            // remember that it is still ours to put back.
            if (RabiesStatus.Intensity01 <= 0f) camData.renderPostProcessing = false;
            else owedCam = camData;
        }
        camPpForced = false;
        camData = null;
    }

    /// <summary>Every frame while the drunk look is off: hand back a flag left on for rabies, once rabies has ended too.</summary>
    public static void SettleCamera()
    {
        if (ReferenceEquals(owedCam, null)) return;
        if (owedCam == null) { owedCam = null; return; } // the camera is gone
        if (RabiesStatus.Intensity01 > 0f) return;
        owedCam.renderPostProcessing = false;
        owedCam = null;
    }

    /// <summary>Remove every trace (volume, profile, camera flag).</summary>
    public void Dispose()
    {
        RestoreCamera();
        if (go != null)
        {
            if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        }
        if (profile != null)
        {
            if (Application.isPlaying) Object.Destroy(profile); else Object.DestroyImmediate(profile);
        }
        go = null; volume = null; profile = null; dof = null; motion = null; lens = null; chroma = null; color = null;
    }
}

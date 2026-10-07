using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The trippy rabies look: our own global URP Volume (priority 500, layer Default, runtime profile) layered on
/// top of the scene's volume, so restoring is just destroying it. Overrides: Lens Distortion wobble, Chromatic
/// Aberration, Color Adjustments hue drift + saturation, and a sickly green Vignette. Also makes sure the main
/// camera renders post-processing while active (and puts the flag back afterwards).
/// Owned by RabiesStatus; call <see cref="Apply"/> every frame and <see cref="Dispose"/> to restore.
/// </summary>
public sealed class RabiesPostFx
{
    public const float Priority = 500f;

    GameObject go;
    Volume volume;
    VolumeProfile profile;
    LensDistortion lens;
    ChromaticAberration chroma;
    ColorAdjustments color;
    Vignette vignette;
    UniversalAdditionalCameraData camData;
    bool camPpForced;

    public bool Alive => go != null;

    void Build(Transform owner)
    {
        go = new GameObject("[RabiesVolume]");
        go.layer = 0; // cameras use volume mask = Default; lives in the active scene
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "RabiesProfile (runtime)";
        profile.hideFlags = HideFlags.DontSave;

        lens = profile.Add<LensDistortion>(false);
        lens.intensity.overrideState = true;
        lens.scale.overrideState = true;
        lens.xMultiplier.overrideState = true;
        lens.yMultiplier.overrideState = true;

        chroma = profile.Add<ChromaticAberration>(false);
        chroma.intensity.overrideState = true;

        color = profile.Add<ColorAdjustments>(false);
        color.hueShift.overrideState = true;
        color.saturation.overrideState = true;
        color.contrast.overrideState = true;

        vignette = profile.Add<Vignette>(false);
        vignette.color.overrideState = true;
        vignette.intensity.overrideState = true;
        vignette.smoothness.overrideState = true;
        vignette.color.value = new Color(0.25f, 0.95f, 0.2f);

        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = Priority;
        volume.weight = 0f;
        volume.sharedProfile = profile;
    }

    /// <summary>Drive the effect. intensity 0..1 (0 destroys nothing, just weights to 0), t = time for wobbles.</summary>
    public void Apply(float intensity, float t, Transform owner)
    {
        if (intensity <= 0.001f)
        {
            if (volume != null) volume.weight = 0f;
            return;
        }
        if (go == null) Build(owner);
        EnsureCameraPostProcessing();

        float i = Mathf.Clamp01(intensity);
        volume.weight = Mathf.Clamp01(i * 1.6f);

        // Slow, woozy lens breathing with a faster wobble on top.
        float wob = Mathf.Sin(t * 0.9f) * 0.75f + Mathf.Sin(t * 2.3f + 1.3f) * 0.25f;
        lens.intensity.value = wob * Mathf.Lerp(0.12f, 0.45f, i);
        lens.scale.value = 1.02f + Mathf.Abs(lens.intensity.value) * 0.25f; // hide the black corners
        lens.xMultiplier.value = 0.8f + Mathf.Sin(t * 0.63f) * 0.2f;
        lens.yMultiplier.value = 0.8f + Mathf.Cos(t * 0.71f) * 0.2f;

        chroma.intensity.value = Mathf.Clamp01(Mathf.Lerp(0.25f, 0.9f, i) + Mathf.Sin(t * 3.1f) * 0.1f);

        color.hueShift.value = Mathf.Clamp(Mathf.Sin(t * 0.37f) * Mathf.Lerp(20f, 110f, i), -180f, 180f);
        color.saturation.value = Mathf.Lerp(10f, 45f, i);
        color.contrast.value = Mathf.Lerp(5f, 20f, i);

        vignette.intensity.value = Mathf.Lerp(0.22f, 0.5f, i) + Mathf.Sin(t * 1.7f) * 0.05f * i;
        vignette.smoothness.value = 0.55f;
    }

    void EnsureCameraPostProcessing()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var d = cam.GetComponent<UniversalAdditionalCameraData>();
        if (d == camData) return;
        RestoreCamera();
        camData = d;
        if (camData != null && !camData.renderPostProcessing)
        {
            camData.renderPostProcessing = true;
            camPpForced = true;
        }
    }

    void RestoreCamera()
    {
        if (camData != null && camPpForced) camData.renderPostProcessing = false;
        camPpForced = false;
        camData = null;
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
        go = null; volume = null; profile = null; lens = null; chroma = null; color = null; vignette = null;
    }
}

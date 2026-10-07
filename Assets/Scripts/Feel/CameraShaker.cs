using UnityEngine;

/// <summary>
/// Added to Camera.main on the first Juice.Shake. Runs after the CinemachineBrain has positioned the camera
/// (late execution order, LateUpdate) and applies a noisy position/roll offset on top. Each frame it first
/// removes the previous frame's offset (only if nothing else moved the camera since), so the shake never
/// accumulates and the brain stays in charge of the real pose.
/// </summary>
[DefaultExecutionOrder(10000)]
[DisallowMultipleComponent]
public class CameraShaker : MonoBehaviour
{
    /// <summary>Meters of offset per unit of intensity.</summary>
    public const float PositionScale = 0.3f;
    /// <summary>Degrees of roll/pitch per unit of intensity.</summary>
    public const float RotationScale = 2.5f;
    const float Frequency = 22f;

    float amplitude, startTime, duration;
    float seed;

    bool applied;
    Vector3 basePos, setPos;
    Quaternion baseRot, setRot;

    /// <summary>Shake Camera.main (adds the component if missing). No-op without a main camera.</summary>
    public static void ShakeMain(float intensity, float duration)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var s = cam.GetComponent<CameraShaker>();
        if (s == null) s = cam.gameObject.AddComponent<CameraShaker>();
        s.Shake(intensity, duration);
    }

    /// <summary>Start a shake. A weaker shake while a stronger one is still ringing is ignored. Scaled by Settings →
    /// Accessibility → Screen effects (0 = no shake at all).</summary>
    public void Shake(float intensity, float dur)
    {
        intensity = Mathf.Clamp(intensity, 0f, 2f) * (Application.isPlaying ? Mathf.Clamp01(GameSettings.ScreenFx01) : 1f);
        dur = Mathf.Clamp(dur, 0.02f, 3f);
        if (intensity <= 0f) return;
        if (intensity < CurrentAmplitude()) return;
        amplitude = intensity;
        duration = dur;
        startTime = Time.unscaledTime;
        if (seed == 0f) seed = Random.Range(1f, 1000f);
    }

    float CurrentAmplitude()
    {
        if (amplitude <= 0f) return 0f;
        float p = (Time.unscaledTime - startTime) / duration;
        if (p >= 1f) return 0f;
        float k = 1f - p;
        return amplitude * k * k;
    }

    void LateUpdate()
    {
        var t = transform;
        if (applied)
        {
            // Undo last frame's offset unless something (the CinemachineBrain) already rewrote the pose.
            if (t.position == setPos && t.rotation == setRot) t.SetPositionAndRotation(basePos, baseRot);
            applied = false;
        }

        float a = CurrentAmplitude();
        if (a <= 0f) { amplitude = 0f; return; }

        float time = (Time.unscaledTime - startTime) * Frequency;
        float nx = Mathf.PerlinNoise(seed, time) * 2f - 1f;
        float ny = Mathf.PerlinNoise(seed + 17.3f, time) * 2f - 1f;
        float nr = Mathf.PerlinNoise(seed + 41.7f, time) * 2f - 1f;
        float np = Mathf.PerlinNoise(seed + 83.1f, time) * 2f - 1f;

        basePos = t.position;
        baseRot = t.rotation;
        setPos = basePos + (t.right * nx + t.up * ny) * a * PositionScale;
        setRot = baseRot * Quaternion.Euler(np * a * RotationScale * 0.5f, 0f, nr * a * RotationScale);
        t.SetPositionAndRotation(setPos, setRot);
        applied = true;
    }

    void OnDisable()
    {
        if (applied && transform.position == setPos && transform.rotation == setRot)
            transform.SetPositionAndRotation(basePos, baseRot);
        applied = false;
        amplitude = 0f;
    }
}

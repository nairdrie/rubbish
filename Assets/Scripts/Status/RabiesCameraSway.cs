using UnityEngine;

/// <summary>
/// Drunk-camera sway for rabies. Added to Camera.main by RabiesStatus. Runs after the CinemachineBrain has placed
/// the camera (LateUpdate, late execution order, just before CameraShaker) and layers a slow roll / pitch / bob on
/// top. Like CameraShaker it first removes last frame's offset (only if nothing else rewrote the pose), so the
/// brain stays in charge and nothing accumulates. <see cref="Remove"/> puts the camera back and destroys itself.
/// </summary>
[DefaultExecutionOrder(9990)]
[DisallowMultipleComponent]
public class RabiesCameraSway : MonoBehaviour
{
    /// <summary>0..1 sway strength (set every frame by RabiesStatus).</summary>
    public float amount;
    /// <summary>Extra roll in degrees (passing out: the world tips over).</summary>
    public float extraRoll;
    /// <summary>Extra downward pitch in degrees (passing out: faceplant).</summary>
    public float extraPitch;

    bool applied;
    Vector3 basePos, setPos;
    Quaternion baseRot, setRot;
    float seed;

    void Awake() => seed = Random.Range(0f, 100f);

    void LateUpdate()
    {
        var t = transform;
        Undo();

        if (amount <= 0.0001f && Mathf.Abs(extraRoll) < 0.01f && Mathf.Abs(extraPitch) < 0.01f) return;

        float time = Time.time + seed;
        float a = Mathf.Clamp01(amount) * GameSettings.ScreenFx01; // Settings → Accessibility → Screen effects
        float roll = (Mathf.Sin(time * 0.7f) * 6f + Mathf.Sin(time * 1.9f) * 1.6f) * a + extraRoll;
        float pitch = (Mathf.Sin(time * 1.1f) * 1.8f + Mathf.Sin(time * 0.43f) * 1.2f) * a + extraPitch;
        float yaw = Mathf.Sin(time * 0.53f) * 1.5f * a;
        Vector3 offset = (t.right * Mathf.Sin(time * 0.9f) * 0.07f + t.up * Mathf.Sin(time * 1.7f) * 0.035f) * a;

        basePos = t.position;
        baseRot = t.rotation;
        setPos = basePos + offset;
        setRot = baseRot * Quaternion.Euler(pitch, yaw, roll);
        t.SetPositionAndRotation(setPos, setRot);
        applied = true;
    }

    void Undo()
    {
        if (!applied) return;
        var t = transform;
        // Only restore if nobody (the brain) moved the camera since we did.
        if (t.position == setPos && t.rotation == setRot) t.SetPositionAndRotation(basePos, baseRot);
        applied = false;
    }

    /// <summary>Put the camera back exactly and remove the component.</summary>
    public void Remove()
    {
        Undo();
        amount = 0f; extraRoll = 0f; extraPitch = 0f;
        Destroy(this);
    }

    void OnDisable() => Undo();
}

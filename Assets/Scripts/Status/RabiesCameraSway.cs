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

    // A second, independent contributor (DrunkStatus, Docs/Casino.md §5). Rabid and drunk at once: the stronger sway wins,
    // the tips add, and the component lives until both have let go.
    /// <summary>0..1 sway strength from being drunk.</summary>
    public float drunkAmount;
    /// <summary>0..1 slow "the room is turning" swirl from being drunk.</summary>
    public float drunkSpin;
    /// <summary>Extra roll / pitch in degrees from a drunk pass-out.</summary>
    public float drunkRoll, drunkPitch;

    bool RabiesIdle => amount <= 0.0001f && Mathf.Abs(extraRoll) < 0.01f && Mathf.Abs(extraPitch) < 0.01f;
    bool DrunkIdle => drunkAmount <= 0.0001f && drunkSpin <= 0.0001f && Mathf.Abs(drunkRoll) < 0.01f && Mathf.Abs(drunkPitch) < 0.01f;

    bool applied;
    Vector3 basePos, setPos;
    Quaternion baseRot, setRot;
    float seed;

    void Awake() => seed = Random.Range(0f, 100f);

    void LateUpdate()
    {
        var t = transform;
        Undo();

        if (RabiesIdle && DrunkIdle) return;

        float time = Time.time + seed;
        float fx = GameSettings.ScreenFx01; // Settings → Accessibility → Screen effects
        float a = Mathf.Clamp01(Mathf.Max(amount, drunkAmount)) * fx;
        float spin = Mathf.Clamp01(drunkSpin) * fx;
        float roll = (Mathf.Sin(time * 0.7f) * 6f + Mathf.Sin(time * 1.9f) * 1.6f) * a + extraRoll + drunkRoll;
        float pitch = (Mathf.Sin(time * 1.1f) * 1.8f + Mathf.Sin(time * 0.43f) * 1.2f) * a + extraPitch + drunkPitch;
        float yaw = Mathf.Sin(time * 0.53f) * 1.5f * a;
        // drunk: the horizon slowly circles (a lazy 20 s orbit of roll against yaw)
        roll += Mathf.Sin(time * 0.31f) * 5f * spin;
        yaw += Mathf.Cos(time * 0.31f) * 4f * spin;
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
        amount = 0f; extraRoll = 0f; extraPitch = 0f;
        if (!DrunkIdle) return; // DrunkStatus still needs it
        Undo();
        Destroy(this);
    }

    /// <summary>The drunk contributor lets go; the component goes once rabies has let go too.</summary>
    public void RemoveDrunk()
    {
        drunkAmount = 0f; drunkSpin = 0f; drunkRoll = 0f; drunkPitch = 0f;
        if (!RabiesIdle) return; // RabiesStatus still needs it
        Undo();
        Destroy(this);
    }

    void OnDisable() => Undo();
}

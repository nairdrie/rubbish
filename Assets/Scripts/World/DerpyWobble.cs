using UnityEngine;

/// <summary>
/// Idle squash-and-stretch bob + lazy sway for derpy set dressing (e.g. the giant trash-bag mascot on
/// the depot roof). Purely cosmetic and local; keeps the object's authored scale/rotation as the rest pose.
/// </summary>
public class DerpyWobble : MonoBehaviour
{
    [Tooltip("Seconds per squash cycle.")]
    public float period = 1.8f;
    [Tooltip("Fractional squash (0.06 = 6% shorter / wider at the bottom of the bob).")]
    public float squash = 0.06f;
    [Tooltip("Side-to-side sway in degrees (half speed of the squash).")]
    public float swayDegrees = 4f;
    [Tooltip("Cycle offset so several wobblers don't move in lockstep.")]
    [Range(0f, 1f)] public float phase;

    Vector3 restScale;
    Quaternion restRotation;

    void Awake()
    {
        restScale = transform.localScale;
        restRotation = transform.localRotation;
    }

    void Update()
    {
        float t = (Time.time / Mathf.Max(0.05f, period) + phase) * Mathf.PI * 2f;
        float s = Mathf.Sin(t);
        transform.localScale = new Vector3(
            restScale.x * (1f + squash * s),
            restScale.y * (1f - squash * s),
            restScale.z * (1f + squash * s));
        transform.localRotation = restRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.5f) * swayDegrees);
    }
}

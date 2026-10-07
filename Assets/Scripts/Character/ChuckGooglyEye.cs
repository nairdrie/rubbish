using UnityEngine;

/// <summary>
/// Chuck's googly eye (and the googly "Company Eye" pin on his cap). Same idea as <see cref="GooglyPupil"/> — the pupil
/// sags under gravity and rattles when the eye moves — but tuned for rig-metre units, damped, blink-aware and scriptable:
/// <list type="bullet">
/// <item>Runs in LateUpdate after <see cref="ChuckPuppet"/> (execution order 1001), so it reacts to this frame's pose.</item>
/// <item>The puppet blinks/squints by scaling this EyeRoot's localScale.y; the motion kick is compensated for that scale so
/// a blink never flings the pupil (InverseTransformVector would otherwise amplify it 10x).</item>
/// <item><see cref="Kick"/> adds a velocity; <see cref="Pull"/> is a one-frame extra acceleration (cross-eyes, free-fall float).</item>
/// </list>
/// Units: eye-local = rig metres (the Player root's 0.8 scale is divided out by InverseTransformVector). No allocations.
/// </summary>
[DefaultExecutionOrder(1001)]
[DisallowMultipleComponent]
public class ChuckGooglyEye : MonoBehaviour
{
    [Tooltip("The pupil transform (child of this eye root, local +Z = out of the face).")]
    public Transform pupil;
    [Tooltip("How far (rig metres) the pupil can roll from the eye centre.")]
    public float maxOffset = 0.028f;
    [Tooltip("Gravity pull on the pupil (rig m/s^2).")]
    public float gravity = 0.8f;
    [Tooltip("How much the eye's own motion flings the pupil. Tune between 6 and 20.")]
    public float motionKick = 10f;
    [Range(0f, 1f)] public float bounciness = 0.55f;
    [Tooltip("Random twitch strength every few seconds, for extra derp.")]
    public float twitch = 0.12f;
    [Tooltip("Velocity damping per second.")]
    public float drag = 1.2f;
    [Tooltip("Largest velocity change one frame of eye motion may add (teleports, snap turns).")]
    public float maxKickPerFrame = 0.35f;

    Vector2 offset, velocity, pull;
    float depth;
    Vector3 lastPos;
    bool primed, haveRest;
    float nextTwitch;

    /// <summary>Current pupil offset from the eye centre (rig metres, eye-local).</summary>
    public Vector2 Offset => offset;

    /// <summary>Add a velocity (rig m/s, eye-local x/y) to the pupil.</summary>
    public void Kick(Vector2 localVelocity) => velocity += localVelocity;

    /// <summary>Extra acceleration (rig m/s^2, eye-local) for the next update only; call every frame to hold it.</summary>
    public void Pull(Vector2 localAccel) => pull += localAccel;

    void OnEnable()
    {
        // Re-prime after being hidden/disabled so the time away doesn't turn into one giant kick.
        primed = false;
    }

    void LateUpdate() => Tick(Time.deltaTime);

    public void Tick(float dt)
    {
        if (pupil == null || dt <= 0f) return;
        if (!haveRest)
        {
            haveRest = true;
            depth = pupil.localPosition.z;
            offset = new Vector2(pupil.localPosition.x, pupil.localPosition.y);
            nextTwitch = Time.time + Random.Range(1.5f, 4f);
        }
        if (!primed)
        {
            primed = true;
            lastPos = transform.position;
            pull = Vector2.zero;
            return;
        }
        dt = Mathf.Min(dt, 1f / 20f);

        float open = Mathf.Clamp(transform.localScale.y, 0.05f, 2f);
        Vector3 g = transform.InverseTransformDirection(Vector3.down) * gravity;
        Vector3 m = transform.InverseTransformVector(lastPos - transform.position) * motionKick;
        lastPos = transform.position;
        m.y *= open; // a squashed (blinking) eye must not amplify the vertical kick
        Vector2 kick = new Vector2(m.x, m.y);
        float km = kick.magnitude;
        if (km > maxKickPerFrame) kick *= maxKickPerFrame / km;

        velocity += (new Vector2(g.x, g.y) + pull) * dt + kick;
        pull = Vector2.zero;

        if (Time.time >= nextTwitch)
        {
            velocity += Random.insideUnitCircle * twitch;
            nextTwitch = Time.time + Random.Range(2f, 5f);
        }

        velocity *= 1f / (1f + drag * dt);
        offset += velocity * dt;
        float mag = offset.magnitude;
        if (mag > maxOffset)
        {
            Vector2 n = offset / mag;
            offset = n * maxOffset;
            float into = Vector2.Dot(velocity, n);
            if (into > 0f) velocity -= (1f + bounciness) * into * n;
        }
        pupil.localPosition = new Vector3(offset.x, offset.y, depth);
    }
}

using UnityEngine;

/// <summary>
/// Cheap googly-eye pupil: a spring that sags under gravity and rattles around inside the eye when the
/// eye moves or rotates. Put it on the eye root; <see cref="pupil"/> is a child offset along local +Z.
/// </summary>
public class GooglyPupil : MonoBehaviour
{
    [Tooltip("The pupil transform (child of this eye).")]
    public Transform pupil;
    [Tooltip("How far (local units) the pupil can roll from the eye center.")]
    public float maxOffset = 0.22f;
    [Tooltip("Gravity pull on the pupil (local units / s^2).")]
    public float gravity = 6f;
    [Tooltip("How much the eye's own motion flings the pupil.")]
    public float motionKick = 40f;
    [Range(0f, 1f)] public float bounciness = 0.45f;
    [Tooltip("Random twitch every few seconds, for extra derp.")]
    public float twitch = 0.8f;

    Vector2 offset, velocity;
    float depth;
    Vector3 lastPos;
    float nextTwitch;

    void Start()
    {
        if (pupil == null) { enabled = false; return; }
        depth = pupil.localPosition.z;
        offset = new Vector2(pupil.localPosition.x, pupil.localPosition.y);
        lastPos = transform.position;
        nextTwitch = Time.time + Random.Range(1.5f, 4f);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 localGravity = transform.InverseTransformDirection(Vector3.down) * gravity;
        Vector3 localMotion = transform.InverseTransformVector(lastPos - transform.position) * motionKick;
        velocity += new Vector2(localGravity.x, localGravity.y) * dt + new Vector2(localMotion.x, localMotion.y);
        lastPos = transform.position;

        if (Time.time >= nextTwitch)
        {
            velocity += Random.insideUnitCircle * twitch;
            nextTwitch = Time.time + Random.Range(2f, 5f);
        }

        offset += velocity * dt;
        if (offset.magnitude > maxOffset)
        {
            Vector2 n = offset.normalized;
            offset = n * maxOffset;
            velocity = Vector2.Reflect(velocity, -n) * bounciness;
        }
        pupil.localPosition = new Vector3(offset.x, offset.y, depth);
    }
}

using UnityEngine;

/// <summary>
/// A googly eye for critters: the pupil is a spring that sags under gravity and rattles around the eye whenever
/// the critter hops, turns or gets bonked. Put it on the eye (a sphere, local +Z = out of the face) with the
/// pupil as a child. <see cref="Kick"/> flings the pupil (hits, wake-ups). <see cref="Closed"/> squints the eye
/// shut (sleeping goblins) by flattening it.
/// </summary>
public class CritterGooglyEye : MonoBehaviour
{
    public Transform pupil;
    [Tooltip("How far (eye-local units) the pupil can roll from the center.")]
    public float maxOffset = 0.24f;
    public float gravity = 7f;
    public float motionKick = 45f;
    [Range(0f, 1f)] public float bounciness = 0.5f;
    public float twitch = 0.9f;

    /// <summary>Eye shut (flattened) — e.g. asleep. Animates smoothly.</summary>
    public bool Closed;

    Vector2 offset, velocity;
    float depth;
    Vector3 lastPos;
    Quaternion lastRot;
    float nextTwitch;
    Vector3 restScale;
    float open = 1f;

    void Awake()
    {
        restScale = transform.localScale;
    }

    void Start()
    {
        if (pupil == null) { enabled = false; return; }
        depth = pupil.localPosition.z;
        offset = new Vector2(pupil.localPosition.x, pupil.localPosition.y);
        lastPos = transform.position;
        lastRot = transform.rotation;
        nextTwitch = Time.time + Random.Range(1f, 3f);
    }

    /// <summary>Rattle the pupil (bonks, surprises).</summary>
    public void Kick(float strength = 3f)
    {
        velocity += Random.insideUnitCircle.normalized * strength;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Open / close by flattening the eye vertically.
        float targetOpen = Closed ? 0.12f : 1f;
        open = Mathf.MoveTowards(open, targetOpen, dt * (Closed ? 3f : 9f));
        transform.localScale = new Vector3(restScale.x, restScale.y * open, restScale.z);
        if (pupil.gameObject.activeSelf != (open > 0.35f)) pupil.gameObject.SetActive(open > 0.35f);

        Vector3 localGravity = transform.InverseTransformDirection(Vector3.down) * gravity;
        Vector3 localMotion = transform.InverseTransformVector(lastPos - transform.position) * motionKick;
        float turn = Quaternion.Angle(lastRot, transform.rotation);
        velocity += new Vector2(localGravity.x, localGravity.y) * dt + new Vector2(localMotion.x, localMotion.y);
        if (turn > 2f) velocity += Random.insideUnitCircle * (turn * 0.01f);
        lastPos = transform.position;
        lastRot = transform.rotation;

        if (Time.time >= nextTwitch)
        {
            velocity += Random.insideUnitCircle * twitch;
            nextTwitch = Time.time + Random.Range(1.5f, 4f);
        }

        velocity *= Mathf.Clamp01(1f - dt * 1.5f);
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

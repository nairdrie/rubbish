using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives Magnus's joints with an exact analytic IK and plays his chess pick-and-place motions.
///
/// The arm is a yaw base (BasePivot) plus three joints that pitch about their local X axis
/// (Shoulder, Elbow, Wrist), with every link extending along local -Z. Given where the Claw Center
/// should be and how the claw should be pitched, the solve lands it there exactly, so pieces
/// carried in the claw end up precisely on their squares.
///
/// This replaces MagnusIKController for chess: keep that one disabled or both will fight over the joints.
/// </summary>
[DefaultExecutionOrder(100)]
public class MagnusArm : MonoBehaviour
{
    public enum IdlePose { Think, Watch, Work }
    public enum Gesture { Sweep, Celebrate, Sulk, Shrug }

    /// <summary>One pick-and-place: carry <see cref="piece"/> from where it stands to <see cref="to"/>.</summary>
    public struct Step
    {
        public Transform piece;
        public Vector3 to;          // where the piece's base should end up
        public float gripHeight;    // height above the piece's base to grip it
        public float gripWidth;     // how wide the piece is at the grip height
        public Action onPlaced;     // runs right after the piece is set down (e.g. promotion)
        public Action onGrab;       // runs the moment the claw closes on it (e.g. LooseItem.SetInFlight)
        public Func<bool> canGrab;  // checked when the claw arrives: false = it moved / someone took it, skip this step
    }

    [Header("Joints")]
    [SerializeField] Transform basePivot;
    [SerializeField] Transform shoulder;
    [SerializeField] Transform elbow;
    [SerializeField] Transform wrist;
    [SerializeField] Transform clawCenter;
    [SerializeField] Transform pointer;
    [SerializeField] Transform thumb;

    [Header("Board")]
    [Tooltip("Centre of the chess board's surface; the thinking pose hovers over it.")]
    [SerializeField] Transform boardCenter;
    [Tooltip("Where the player's head is (e.g. the White chess camera). Magnus turns his googly eyes this way while waiting.")]
    [SerializeField] Transform watchTarget;

    [Header("Motion")]
    [Tooltip("Peak claw speed (m/s) when swinging between squares.")]
    [SerializeField] float travelSpeed = 0.8f;
    [Tooltip("Peak claw speed (m/s) for the straight-down approach and the lift.")]
    [SerializeField] float approachSpeed = 0.45f;
    [Tooltip("Extra height (m) the claw lifts a piece so it clears every other piece on the board.")]
    [SerializeField] float carryClearance = 0.2f;
    [Tooltip("How far (m) the fingers open beyond the piece's width before gripping.")]
    [SerializeField] float approachGap = 0.035f;
    [Tooltip("Finger speed in degrees per second.")]
    [SerializeField] float fingerSpeed = 30f;
    [Tooltip("Seconds for the idle pose to catch up with its target.")]
    [SerializeField] float idleSmoothTime = 0.6f;

    [Header("Idle poses")]
    // Between moves Magnus leans out to the side of the board so the googly eyes on his head face the
    // player. His head sits a claw-length above the Claw Center, so at full size these heights (relative
    // to the board surface) are negative: the claw hangs below the counter edge and his head peeks over it.
    [Tooltip("How far out from his base (m) the claw hangs while watching the player.")]
    [SerializeField] float watchReach = 0.66f;
    [Tooltip("Claw height relative to the board surface while watching the player.")]
    [SerializeField] float watchHeight = -0.32f;
    [SerializeField] float watchPitch = -78f;
    [Tooltip("How far out from his base (m) the claw hangs while thinking.")]
    [SerializeField] float thinkReach = 0.6f;
    [Tooltip("Claw height relative to the board surface while thinking.")]
    [SerializeField] float thinkHeight = -0.36f;
    [SerializeField] float thinkPitch = -84f;
    [Tooltip("Degrees he sways side to side while thinking, as if scanning the board.")]
    [SerializeField] float thinkSway = 8f;

    // The finger gap at the Claw Center, measured from the claw mesh at Magnus scale 60: fully closed
    // (touching) around 19 degrees, then opening about 2 cm per degree. It scales with Magnus.
    const float GapPerDegreeAtScale60 = 0.0205f, GapAt18DegreesAtScale60 = -0.030f;

    float upperArm, forearm, shoulderHeight;
    Vector3 clawOffset;              // Claw Center in wrist space
    Vector3 clawTarget;              // where the Claw Center is being driven (world)
    float clawPitch;                 // claw pitch in degrees (-90 = pointing straight down)
    float fingerAngle, fingerTarget;
    Vector3 idleVelocity;
    float idlePitchVelocity;

    Transform heldPiece;
    Vector3 heldOffset;

    readonly Queue<(int id, List<Step> steps)> planQueue = new();
    readonly Queue<Gesture> gestureQueue = new();
    List<Step> currentSteps;
    int currentStepIndex;
    Coroutine runner;

    public IdlePose Idle { get; set; } = IdlePose.Think;

    /// <summary>
    /// Multiplies every motion (travel, approach, fingers, gestures). 1 = the measured chess pace; the sort job raises it
    /// to meet its items/min contract (Docs/Progression.md §6.3). Clamped 0.25..16.
    /// </summary>
    public float SpeedMultiplier { get => speedMult; set => speedMult = Mathf.Clamp(value, 0.25f, 16f); }
    float speedMult = 1f;

    /// <summary>
    /// What he hovers over in <see cref="IdlePose.Work"/> (e.g. the middle of the sorting table, world space). Null =
    /// straight out in front of the cart.
    /// </summary>
    public Vector3? WorkFocus { get; set; }

    /// <summary>World position of his "head" (the wrist block with the googly eyes) for speech bubbles.</summary>
    public Vector3 HeadPosition => wrist ? wrist.position : transform.position + Vector3.up * 2f;

    /// <summary>The claw centre (world) right now.</summary>
    public Vector3 ClawPosition => clawCenter ? clawCenter.position : transform.position;

    /// <summary>
    /// Can the claw reach <paramref name="world"/> pointing straight down (the pick pose)? Uses the same two-link maths
    /// as the solve, with a small safety margin.
    /// </summary>
    public bool CanReach(Vector3 world, float margin = 0.03f)
    {
        if (basePivot == null || shoulder == null) return false;
        if (upperArm <= 0f) InitLengths();
        Transform root = basePivot.parent;
        const float p = -90f * Mathf.Deg2Rad;
        float offForward = -clawOffset.z * Mathf.Cos(p) - clawOffset.y * Mathf.Sin(p);
        float offUp = -clawOffset.z * Mathf.Sin(p) + clawOffset.y * Mathf.Cos(p);
        Vector3 t = root.InverseTransformPoint(world) - basePivot.localPosition;
        float reach = new Vector2(t.x, t.z).magnitude;
        Vector2 w = new Vector2(reach - offForward, t.y - shoulderHeight - offUp);
        float scale = Mathf.Max(1e-4f, root.lossyScale.x);
        float m = margin / scale;
        float d = w.magnitude;
        return d <= upperArm + forearm - m && d >= Mathf.Abs(upperArm - forearm) + m;
    }

    /// <summary>The nearest point to <paramref name="world"/> (same height) the claw can reach, pulled in toward his base.</summary>
    public Vector3 ClampToReach(Vector3 world)
    {
        if (CanReach(world)) return world;
        Vector3 b = basePivot ? basePivot.position : transform.position;
        Vector3 flat = world - b; flat.y = 0f;
        for (float k = 0.95f; k > 0.1f; k -= 0.05f)
        {
            Vector3 p = b + flat * k; p.y = world.y;
            if (CanReach(p)) return p;
        }
        return b + Vector3.up * (world.y - b.y);
    }

    /// <summary>Id of the last plan this arm finished (or skipped to the end of).</summary>
    public int LastCompletedPlanId { get; private set; }

    public bool IsBusy => runner != null;

    public bool HasPendingPlans => currentSteps != null || planQueue.Count > 0;

    public bool IsHolding(Transform piece) => heldPiece != null && heldPiece == piece;

    Vector3 heldStartPosition;

    void OnDisable() => CancelPlans();

    /// <summary>Abort interrupted work without running delivery callbacks or leaving a dead coroutine handle.</summary>
    public void CancelPlans()
    {
        StopAllCoroutines();
        runner = null;
        planQueue.Clear();
        gestureQueue.Clear();
        currentSteps = null;
        currentStepIndex = 0;
        if (heldPiece != null)
        {
            var carry = heldPiece.GetComponent<Carryable>();
            if (carry != null)
            {
                // Guests only display the authoritative pose; never drop another machine's item here.
                if (!carry.NetRemote && carry.State != CarryState.Consumed && carry.Holder == null)
                    carry.EndCarry(Vector3.zero, true);
            }
            else heldPiece.position = heldStartPosition; // Uncommitted chess move: leave its piece on its old square.
        }
        heldPiece = null;
        idleVelocity = Vector3.zero;
        idlePitchVelocity = 0f;
        if (clawCenter != null) clawTarget = clawCenter.position;
    }

    void Awake()
    {
        InitLengths();

        clawPitch = Signed(shoulder.localEulerAngles.x) + Signed(elbow.localEulerAngles.x) + Signed(wrist.localEulerAngles.x);
        clawTarget = clawCenter.position;
        fingerAngle = fingerTarget = Signed(pointer.localEulerAngles.x);
        MagnusArmVisuals.Ensure(this);
    }

    void InitLengths()
    {
        upperArm = elbow.localPosition.magnitude;
        forearm = wrist.localPosition.magnitude;
        shoulderHeight = shoulder.localPosition.y;
        clawOffset = clawCenter.localPosition;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Queue a sequence of pick-and-place steps. Plans run one after another.</summary>
    public void PlayPlan(int planId, List<Step> steps)
    {
        if (steps == null || steps.Count == 0) { LastCompletedPlanId = Mathf.Max(LastCompletedPlanId, planId); return; }
        planQueue.Enqueue((planId, steps));
        runner ??= StartCoroutine(RunQueue());
    }

    public void PlayGesture(Gesture gesture)
    {
        gestureQueue.Enqueue(gesture);
        runner ??= StartCoroutine(RunQueue());
    }

    /// <summary>
    /// Finish every queued move instantly: pieces jump to where they were going, and the arm carries
    /// on from wherever it is. Queued gestures still play.
    /// </summary>
    public void FastForward()
    {
        if (runner != null) StopCoroutine(runner);
        runner = null;
        heldPiece = null;

        if (currentSteps != null)
        {
            for (int i = currentStepIndex; i < currentSteps.Count; i++) Place(currentSteps[i]);
            currentSteps = null;
        }
        while (planQueue.Count > 0)
        {
            var (id, steps) = planQueue.Dequeue();
            foreach (var step in steps) Place(step);
            LastCompletedPlanId = Mathf.Max(LastCompletedPlanId, id);
        }
        idleVelocity = Vector3.zero;
        if (gestureQueue.Count > 0) runner = StartCoroutine(RunQueue());
    }

    // ------------------------------------------------------------------ frame update

    void LateUpdate()
    {
        if (runner == null) FollowIdlePose();

        Solve(clawTarget, clawPitch);

        fingerAngle = Mathf.MoveTowards(fingerAngle, fingerTarget, fingerSpeed * speedMult * Time.deltaTime);
        pointer.localRotation = Quaternion.Euler(fingerAngle, 0f, 0f);
        thumb.localRotation = Quaternion.Euler(-fingerAngle, 0f, 0f);

        if (heldPiece) heldPiece.position = clawCenter.position + heldOffset;
    }

    void FollowIdlePose()
    {
        if (Idle == IdlePose.Work) { FollowWorkPose(); return; }
        float t = Time.time;
        bool watching = Idle == IdlePose.Watch;
        float reach = watching ? watchReach : thinkReach;
        float height = watching ? watchHeight + Mathf.Sin(t * 1.3f) * 0.012f : thinkHeight + Mathf.Sin(t * 0.9f) * 0.02f;
        float sway = watching ? 0f : Mathf.Sin(t * 0.5f) * thinkSway;
        float goalPitch = (watching ? watchPitch : thinkPitch) + Mathf.Sin(t * 0.7f) * 5f;

        // Lean out to the side so the googly eyes (on the head's -X face) look toward the player.
        Vector3 toPlayer = watchTarget ? watchTarget.position - basePivot.position : basePivot.parent.forward;
        float yaw = Mathf.Atan2(toPlayer.z, -toPlayer.x) * Mathf.Rad2Deg + sway;
        Vector3 goal = basePivot.position + Quaternion.Euler(0f, yaw, 0f) * Vector3.back * reach;
        goal.y = BoardY + height;

        fingerTarget = AngleForGap(watching ? 0.06f + Mathf.Max(0f, Mathf.Sin(t * 0.45f)) * 0.03f : 0.07f);
        clawTarget = Vector3.SmoothDamp(clawTarget, goal, ref idleVelocity, idleSmoothTime);
        clawPitch = Mathf.SmoothDamp(clawPitch, goalPitch, ref idlePitchVelocity, idleSmoothTime);
    }

    /// <summary>
    /// On shift at the sort station: hover over the work focus, scanning back and forth like he's reading labels, claw
    /// half open. Stays within reach.
    /// </summary>
    void FollowWorkPose()
    {
        float t = Time.time;
        Vector3 b = basePivot.position;
        Vector3 focus = WorkFocus ?? (b + basePivot.parent.forward * 1.2f);
        Vector3 flat = focus - b; flat.y = 0f;
        float dist = Mathf.Clamp(flat.magnitude * 0.75f, 0.6f, 1.6f);
        Vector3 dir = flat.sqrMagnitude > 1e-4f ? flat.normalized : basePivot.parent.forward;
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        Vector3 goal = b + dir * dist + side * (Mathf.Sin(t * 0.8f) * 0.35f);
        goal.y = focus.y + 0.55f + Mathf.Sin(t * 1.7f) * 0.03f;
        goal = ClampToReach(goal);
        fingerTarget = AngleForGap(0.08f + Mathf.Max(0f, Mathf.Sin(t * 1.1f)) * 0.04f);
        float goalPitch = -82f + Mathf.Sin(t * 0.9f) * 5f;
        clawTarget = Vector3.SmoothDamp(clawTarget, goal, ref idleVelocity, idleSmoothTime);
        clawPitch = Mathf.SmoothDamp(clawPitch, goalPitch, ref idlePitchVelocity, idleSmoothTime);
    }

    /// <summary>Move the claw's goal along with the base (MagnusTrack calls this while the cart drives).</summary>
    public void ShiftTarget(Vector3 delta) => clawTarget += delta;

    float BoardY => boardCenter ? boardCenter.position.y : clawCenter.position.y;

    // ------------------------------------------------------------------ kinematics

    /// <summary>Set the joints so the Claw Center sits at <paramref name="target"/> with the given pitch.</summary>
    void Solve(Vector3 target, float pitchDeg)
    {
        Transform root = basePivot.parent;
        float p = pitchDeg * Mathf.Deg2Rad;
        // The Claw Center's offset from the wrist, expressed along the arm (forward) and up.
        float offForward = -clawOffset.z * Mathf.Cos(p) - clawOffset.y * Mathf.Sin(p);
        float offUp = -clawOffset.z * Mathf.Sin(p) + clawOffset.y * Mathf.Cos(p);

        Vector3 aim = target;
        for (int i = 0; i < 3; i++)
        {
            Vector3 t = root.InverseTransformPoint(aim) - basePivot.localPosition;
            float yaw = Mathf.Atan2(-t.x, -t.z) * Mathf.Rad2Deg;
            float reach = new Vector2(t.x, t.z).magnitude;
            Vector2 w = new Vector2(reach - offForward, t.y - shoulderHeight - offUp);

            float d = Mathf.Clamp(w.magnitude, Mathf.Abs(upperArm - forearm) + 1e-5f, upperArm + forearm - 1e-5f);
            float lift = Mathf.Atan2(w.y, w.x);
            float atShoulder = Mathf.Acos(Mathf.Clamp((upperArm * upperArm + d * d - forearm * forearm) / (2f * upperArm * d), -1f, 1f));
            float atElbow = Mathf.Acos(Mathf.Clamp((upperArm * upperArm + forearm * forearm - d * d) / (2f * upperArm * forearm), -1f, 1f));
            float shoulderPitch = (lift + atShoulder) * Mathf.Rad2Deg;   // elbow-up solution
            float elbowPitch = atElbow * Mathf.Rad2Deg - 180f;

            basePivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
            shoulder.localRotation = Quaternion.Euler(shoulderPitch, 0f, 0f);
            elbow.localRotation = Quaternion.Euler(elbowPitch, 0f, 0f);
            wrist.localRotation = Quaternion.Euler(pitchDeg - shoulderPitch - elbowPitch, 0f, 0f);

            // The claw sits slightly off the arm's plane; nudge the aim point to cancel what's left.
            aim += target - clawCenter.position;
        }
    }

    float AngleForGap(float gap)
    {
        float scale = basePivot.lossyScale.x / 60f;
        return Mathf.Clamp(18f + (gap / scale - GapAt18DegreesAtScale60) / GapPerDegreeAtScale60, 16f, 58f);
    }

    static float Signed(float degrees) => Mathf.DeltaAngle(0f, degrees);

    static float MinJerk(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * x * (10f + x * (-15f + 6f * x));
    }

    // Moves are interpolated around the base (angle, radius, height) like a real arm swinging,
    // with a minimum-jerk speed profile so every move starts and stops smoothly.
    IEnumerator MoveClaw(Vector3 to, float pitch, float peakSpeed)
    {
        Vector3 b = basePivot.position;
        Vector3 from = clawTarget;
        float yawA = Mathf.Atan2(from.x - b.x, from.z - b.z) * Mathf.Rad2Deg;
        float yawB = Mathf.Atan2(to.x - b.x, to.z - b.z) * Mathf.Rad2Deg;
        float dYaw = Mathf.DeltaAngle(yawA, yawB);
        float rA = new Vector2(from.x - b.x, from.z - b.z).magnitude;
        float rB = new Vector2(to.x - b.x, to.z - b.z).magnitude;
        float arc = Mathf.Abs(dYaw) * Mathf.Deg2Rad * Mathf.Max(rA, rB);
        float dist = Mathf.Sqrt(arc * arc + (rB - rA) * (rB - rA) + (to.y - from.y) * (to.y - from.y));
        float duration = Mathf.Max(0.2f, 1.875f * dist / Mathf.Max(0.01f, peakSpeed)) / speedMult;
        float startPitch = clawPitch;

        for (float time = 0f; time < duration; time += Time.deltaTime)
        {
            float s = MinJerk(time / duration);
            float yaw = (yawA + dYaw * s) * Mathf.Deg2Rad;
            float r = Mathf.Lerp(rA, rB, s);
            clawTarget = new Vector3(b.x + Mathf.Sin(yaw) * r, Mathf.Lerp(from.y, to.y, s), b.z + Mathf.Cos(yaw) * r);
            clawPitch = Mathf.Lerp(startPitch, pitch, s);
            yield return null;
        }
        clawTarget = to;
        clawPitch = pitch;
    }

    IEnumerator Fingers(float angle)
    {
        fingerTarget = angle;
        while (!Mathf.Approximately(fingerAngle, fingerTarget)) yield return null;
    }

    // ------------------------------------------------------------------ plans & gestures

    IEnumerator RunQueue()
    {
        // Ensure StartCoroutine returns/assigns runner even when all queued pieces have disappeared.
        yield return null;
        idleVelocity = Vector3.zero;
        while (planQueue.Count > 0 || gestureQueue.Count > 0)
        {
            if (planQueue.Count > 0)
            {
                var (id, steps) = planQueue.Dequeue();
                currentSteps = steps;
                for (currentStepIndex = 0; currentStepIndex < steps.Count; currentStepIndex++)
                    yield return PickAndPlace(steps[currentStepIndex]);
                currentSteps = null;
                LastCompletedPlanId = Mathf.Max(LastCompletedPlanId, id);
            }
            else
            {
                yield return PlayGestureRoutine(gestureQueue.Dequeue());
            }
        }
        runner = null;
    }

    IEnumerator PickAndPlace(Step step)
    {
        if (!step.piece) yield break;
        Vector3 up = Vector3.up;
        Vector3 pickBase = step.piece.position;
        float hover = step.gripHeight + carryClearance;

        fingerTarget = AngleForGap(step.gripWidth + approachGap);
        yield return MoveClaw(pickBase + up * hover, -90f, travelSpeed);
        yield return MoveClaw(pickBase + up * step.gripHeight, -90f, approachSpeed);
        if (!step.piece || (step.canGrab != null && !step.canGrab()))
        {
            // It's gone (grabbed, rolled off, eaten): close on air, look confused, lift back out.
            yield return Fingers(AngleForGap(0f));
            yield return MoveClaw(pickBase + up * hover, -90f, approachSpeed);
            yield break;
        }
        yield return Fingers(AngleForGap(step.gripWidth));

        heldPiece = step.piece;
        heldStartPosition = step.piece.position;
        heldOffset = step.piece.position - clawCenter.position;
        if (step.onGrab != null)
        {
            try { step.onGrab(); } catch (Exception e) { Debug.LogException(e); }
            if (!step.piece) { heldPiece = null; yield break; }
        }

        yield return MoveClaw(pickBase + up * hover, -90f, approachSpeed);
        yield return MoveClaw(step.to + up * hover, -90f, travelSpeed * 0.85f);
        yield return MoveClaw(step.to + up * step.gripHeight, -90f, approachSpeed * 0.8f);

        heldPiece = null;
        Place(step);
        yield return Fingers(AngleForGap(step.gripWidth + approachGap));
        yield return MoveClaw(step.to + up * hover, -90f, approachSpeed);
    }

    void Place(Step step)
    {
        if (!step.piece) return;
        step.piece.position = step.to;
        step.onPlaced?.Invoke();
    }

    IEnumerator PlayGestureRoutine(Gesture gesture)
    {
        Vector3 b = basePivot.position;
        Vector3 center = boardCenter ? boardCenter.position : b + basePivot.parent.forward * 0.6f;
        Vector3 toCenter = Vector3.ProjectOnPlane(center - b, Vector3.up);
        float baseYaw = Mathf.Atan2(toCenter.x, toCenter.z) * Mathf.Rad2Deg;
        float r = toCenter.magnitude * 0.8f;
        Vector3 Around(float yawOffset, float radius, float height) =>
            b + Quaternion.Euler(0f, baseYaw + yawOffset, 0f) * Vector3.forward * radius + Vector3.up * (BoardY + height - b.y);

        switch (gesture)
        {
            case Gesture.Sweep:
                // A tidy-up wave over the board while the pieces slide home.
                fingerTarget = AngleForGap(0.12f);
                yield return MoveClaw(Around(-35f, r, 0.3f), -80f, travelSpeed);
                yield return MoveClaw(Around(35f, r, 0.26f), -80f, travelSpeed * 0.9f);
                yield return MoveClaw(Around(-10f, r * 0.9f, 0.32f), -85f, travelSpeed);
                break;

            case Gesture.Celebrate:
                yield return MoveClaw(Around(0f, r * 0.7f, 0.55f), -45f, travelSpeed);
                for (int i = 0; i < 3; i++)
                {
                    fingerTarget = AngleForGap(0.2f);
                    yield return MoveClaw(Around(18f, r * 0.7f, 0.58f), -40f, travelSpeed * 1.4f);
                    fingerTarget = AngleForGap(0.0f);
                    yield return MoveClaw(Around(-18f, r * 0.7f, 0.52f), -50f, travelSpeed * 1.4f);
                }
                break;

            case Gesture.Sulk:
                fingerTarget = AngleForGap(0.0f);
                yield return MoveClaw(Around(-60f, r * 0.55f, 0.12f), -90f, travelSpeed * 0.4f);
                yield return new WaitForSeconds(2.5f / speedMult);
                break;

            case Gesture.Shrug:
                yield return MoveClaw(Around(0f, r * 0.8f, 0.42f), -70f, travelSpeed);
                fingerTarget = AngleForGap(0.15f);
                yield return MoveClaw(Around(0f, r * 0.8f, 0.36f), -80f, travelSpeed * 0.5f);
                break;
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// CONTRACT (owned by the Pickup system). Public API below is FROZEN — implementations may be
// rewritten/juiced but keep these member names/signatures so Sorting, Truck and HUD code compiles.
// (CarryReceiverTrigger lives in Assets/Scripts/Trash/CarryReceiverTrigger.cs so it can be saved
// on prefabs — Unity needs a MonoBehaviour's file name to match its class name.)

public enum CarryState { Resting, Carried, Thrown, Consumed }

/// <summary>
/// A physics object the on-foot player can grab, carry, drop, throw, or toss into an ICarryReceiver.
/// Subclasses: TrashBag (curbside sacks), LooseItem (individual pieces of trash / treasure).
/// Must live on the Interactable layer (3) so PlayerController's scans find it.
///
/// While Carried the body is kinematic with every collider disabled, so it can never fight the
/// player's CharacterController; the TrashCarrier drives its pose every frame (spring-lagged around
/// the carrier's HoldPoint). Thrown objects use continuous collision until they settle.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public abstract class Carryable : MonoBehaviour, IInteractable
{
    public const int InteractableLayer = 3;

    /// <summary>Thrown objects below this height are teleported back to their last safe spot.</summary>
    public const float KillPlaneY = -40f;

    Rigidbody body;
    /// <summary>This object's Rigidbody (looked up lazily, so it's valid even before/without Awake).</summary>
    public Rigidbody Body
    {
        get { if (body == null) body = GetComponent<Rigidbody>(); return body; }
        private set => body = value;
    }
    public CarryState State { get; protected set; } = CarryState.Resting;
    /// <summary>The carrier currently holding this, or null.</summary>
    public TrashCarrier Holder { get; private set; }
    /// <summary>The carrier that last threw/dropped this (for crediting physics throws), or null.</summary>
    public TrashCarrier LastHolder { get; private set; }

    public abstract float MassKg { get; }
    public abstract string DisplayName { get; }
    public virtual string PickupVerb => "Grab";

    /// <summary>True while a scripted toss arc (TossArcInto) is moving this object.</summary>
    public bool IsInScriptedFlight { get; private set; }
    /// <summary>True while the interaction highlight is showing.</summary>
    public bool IsHighlighted => highlighter != null && highlighter.IsOn;
    /// <summary>Time.time of the last physics impact while free (for rate-limiting effects).</summary>
    public float LastImpactTime { get; private set; } = -999f;
    /// <summary>Time.time this was last released by a carrier (dropped / thrown / tossed).</summary>
    public float LastReleaseTime { get; private set; } = -999f;

    /// <summary>
    /// Child transform squash/punch effects (Juice.*) should animate. Never the physics root.
    /// Default: the first child that has a renderer (falls back to this transform).
    /// </summary>
    public virtual Transform JuiceTarget
    {
        get
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var ch = transform.GetChild(i);
                if (ch.GetComponentInChildren<Renderer>() != null) return ch;
            }
            return transform;
        }
    }

    /// <summary>
    /// Child transform the highlight gently scale-pulses, or null for tint-only highlighting.
    /// Must not be the physics root and should not be the JuiceTarget (so the two never fight).
    /// </summary>
    public virtual Transform HighlightScaleTarget => null;

    float settleTimer;
    float thrownSince;
    Vector3 lastSafePosition;
    bool hasSafePosition;
    Coroutine flightRoutine;
    CarryHighlight highlighter;
    internal float lastRejectFeedbackTime = -999f; // used by CarryReceiverTrigger to rate-limit "nope" feedback

    static readonly Collider[] overlapBuffer = new Collider[32];

    // ---------------- co-op (Docs/Multiplayer.md §4.5–4.6, package N2: Net/Hands) ----------------
    static readonly List<Carryable> all = new List<Carryable>();
    /// <summary>Every enabled Carryable (the host publishes them to guests; Net/Hands).</summary>
    public static IReadOnlyList<Carryable> All => all;

    /// <summary>Networked id (0 = not networked: solo, or a local-only object).</summary>
    public uint NetId { get; set; }
    /// <summary>True while ANOTHER machine simulates this object: this copy only follows the network (no impacts, bursts,
    /// settling or receiver hits here).</summary>
    public bool NetRemote { get; set; }

    /// <summary>
    /// Co-op: a networked item reached <c>receiver</c> on this machine (a throw entered its trigger, or a toss arc landed).
    /// Return true when the network layer took it over (the receiver must not Accept it here: a guest asks the host).
    /// </summary>
    public static System.Func<Carryable, ICarryReceiver, bool> NetDeliver;
    /// <summary>Co-op: an item was consumed on this machine (MarkConsumed). The host tells everyone.</summary>
    public static System.Action<Carryable> NetConsumed;
    /// <summary>Co-op: a local script took a networked copy this machine doesn't simulate (SetInFlight: a station fetch, a
    /// compactor vacuum...). The network layer claims it for this machine.</summary>
    public static System.Action<Carryable> NetScriptTake;

    protected virtual void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    protected virtual void OnDisable() { all.Remove(this); }

    /// <summary>Net/Hands: force the carry state of a network copy (it follows another machine).</summary>
    public void NetSetState(CarryState s) => State = s;

    /// <summary>Net/Hands: kinematic (a copy posed by the network; colliders optional) or back to normal physics.</summary>
    public void NetSetKinematic(bool kinematic, bool colliders = true)
    {
        if (kinematic) { MakeKinematic(); SetCollidersEnabled(colliders); }
        else { SetCollidersEnabled(true); MakeDynamic(State == CarryState.Thrown); }
    }

    /// <summary>Net/Hands: true when the network layer handled this delivery (see <see cref="NetDeliver"/>).</summary>
    public static bool TryNetDeliver(Carryable c, ICarryReceiver r)
    {
        var f = NetDeliver;
        if (f == null || c == null || c.NetId == 0) return false;
        try { return f(c, r); }
        catch (System.Exception e) { Debug.LogException(e); return false; }
    }

    protected virtual void Awake()
    {
        Body = GetComponent<Rigidbody>();
    }

    // ---------------- IInteractable ----------------
    public virtual bool CanInteract()
    {
        if (State == CarryState.Carried || State == CarryState.Consumed || IsInScriptedFlight) return false;
        var local = TrashCarrier.Local;
        return local == null || !local.IsCarrying;
    }

    public virtual string GetInteractionPrompt() => $"{PickupVerb} {DisplayName} ({MassKg:0.#} kg)";

    public virtual void OnInteract(GameObject interactor)
    {
        // Depot Row (§13.5): trash inside someone else's shop is theirs: hands pass through.
        if (!Permissions.LocalCanWorkAt(transform.position)) { Permissions.Deny(transform.position); return; }
        var carrier = interactor ? interactor.GetComponent<TrashCarrier>() : null;
        if (carrier != null) carrier.PickUp(this);
    }

    /// <summary>
    /// Focus feedback: warm tint via MaterialPropertyBlock on _BaseColor (restored exactly on
    /// unhighlight) plus a subtle scale pulse on HighlightScaleTarget. PlayerController calls
    /// Highlight(true) every frame while focused; if it stops calling, the highlight fades by itself.
    /// </summary>
    public virtual void Highlight(bool highlight)
    {
        if (highlight)
        {
            if (State == CarryState.Carried || State == CarryState.Consumed) return;
            if (highlighter == null)
            {
                highlighter = gameObject.AddComponent<CarryHighlight>();
                highlighter.hideFlags = HideFlags.HideInInspector;
                highlighter.Init(this);
            }
            highlighter.Refresh();
        }
        else if (highlighter != null)
        {
            highlighter.TurnOff();
        }
    }

    // ---------------- called by TrashCarrier ----------------
    /// <summary>
    /// Attach to a carrier: makes the body kinematic and disables all collisions. The carrier then
    /// drives this object's pose every frame around its hold point (holdPoint is informational —
    /// the object is not parented, so vehicle/renderer toggles on the player never touch it).
    /// </summary>
    public virtual void BeginCarry(TrashCarrier carrier, Transform holdPoint)
    {
        StopFlight();
        Highlight(false);
        Holder = carrier;
        LastHolder = carrier;
        State = CarryState.Carried;
        MakeKinematic();
        SetCollidersEnabled(false);
        if (transform.parent != null) transform.SetParent(null, true);
        OnPickedUp(carrier);
    }

    /// <summary>Detach and hand back to physics with the given velocity. thrown=false for a gentle drop.</summary>
    public virtual void EndCarry(Vector3 velocity, bool thrown)
    {
        StopFlight();
        var prev = Holder;
        Holder = null;
        if (transform.parent != null) transform.SetParent(null, true);
        SetCollidersEnabled(true);
        ResolvePenetration();
        MakeDynamic(thrown);
        Body.linearVelocity = velocity;
        State = thrown ? CarryState.Thrown : CarryState.Resting;
        settleTimer = 0f;
        thrownSince = Time.time;
        LastReleaseTime = Time.time;
        RememberSafePosition();
        OnReleased(prev, thrown);
    }

    /// <summary>
    /// Called by an ICarryReceiver when it accepts this object. Default: mark consumed and destroy.
    /// Receivers that animate the object in (arc into the truck, etc.) call this at the end, or call
    /// MarkConsumed() first and destroy it themselves after the animation.
    /// </summary>
    public virtual void Consume()
    {
        if (State == CarryState.Consumed) return;
        MarkConsumed();
        Destroy(gameObject);
    }

    /// <summary>
    /// Mark consumed without destroying: frees the holder, disables collisions and physics so a
    /// receiver can play a suck-in/shrink animation and destroy it afterwards. Idempotent.
    /// </summary>
    public void MarkConsumed()
    {
        if (State == CarryState.Consumed) return;
        StopFlight();
        Highlight(false);
        State = CarryState.Consumed;
        var h = Holder;
        Holder = null;
        if (h != null) h.ForgetHeld(this);
        SetCollidersEnabled(false);
        MakeKinematic();
        if (NetId != 0 && NetConsumed != null)
        {
            try { NetConsumed(this); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>For kinematic scripted motion (auto-toss arcs). Disables physics without a holder.</summary>
    public void SetInFlight()
    {
        if (NetId != 0 && NetRemote && NetScriptTake != null)
        {
            try { NetScriptTake(this); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        Highlight(false);
        Holder = null;
        transform.SetParent(null, true);
        SetCollidersEnabled(false);
        MakeKinematic();
        State = CarryState.Thrown;
        LastReleaseTime = Time.time;
    }

    /// <summary>
    /// Scripted parabolic toss (spin + stretch) from where this is now to receiver.TossTargetPosition,
    /// then receiver.Accept(this). If the receiver vanished or no longer accepts on arrival, the object
    /// is handed back to physics and bounces off (receivers implementing ICarryReceiverFeedback are told).
    /// Call SetInFlight() first or let this do it.
    /// </summary>
    public void TossArcInto(ICarryReceiver receiver, float duration = 0.45f)
    {
        if (receiver == null || State == CarryState.Consumed) return;
        if (State != CarryState.Thrown || !Body.isKinematic) SetInFlight();
        StopFlight();
        flightRoutine = StartCoroutine(TossArcRoutine(receiver, Mathf.Max(0.08f, duration)));
    }

    IEnumerator TossArcRoutine(ICarryReceiver receiver, float duration)
    {
        IsInScriptedFlight = true;
        Vector3 centerLocal = LocalBounds().center;
        Vector3 start = transform.TransformPoint(centerLocal);
        Vector3 end = receiver.TossTargetPosition;
        Quaternion startRot = transform.rotation;
        Vector3 baseScale = transform.localScale;

        Vector3 flat = end - start; flat.y = 0f;
        Vector3 travelDir = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.forward;
        Vector3 spinAxis = Vector3.Cross(Vector3.up, travelDir);
        if (spinAxis.sqrMagnitude < 0.001f) spinAxis = Vector3.right;
        spinAxis = (spinAxis.normalized + Random.insideUnitSphere * 0.35f).normalized;
        float spinDeg = Random.Range(300f, 560f);

        Vector3 prev = start;
        Vector3 vel = Vector3.zero;
        float t = 0f;
        while (t < 1f)
        {
            if (IsDead(receiver)) break;
            end = receiver.TossTargetPosition;
            float dt = Time.deltaTime;
            t = Mathf.Min(1f, t + dt / duration);

            float horiz = new Vector2(end.x - start.x, end.z - start.z).magnitude;
            float apex = Mathf.Max(start.y, end.y) + 0.45f + horiz * 0.12f;
            float ctrlY = 2f * apex - 0.5f * (start.y + end.y);
            Vector3 p = Vector3.Lerp(start, end, t);
            float u = 1f - t;
            p.y = u * u * start.y + 2f * u * t * ctrlY + t * t * end.y;

            Quaternion rot = Quaternion.AngleAxis(spinDeg * t, spinAxis) * startRot;
            // Stretch in the middle of the flight, back to normal on arrival.
            float stretch = Mathf.Sin(t * Mathf.PI) * 0.14f;
            Vector3 scale = new Vector3(baseScale.x * (1f - stretch * 0.5f), baseScale.y * (1f + stretch), baseScale.z * (1f - stretch * 0.5f));
            transform.localScale = scale;
            transform.rotation = rot;
            transform.position = p - rot * Vector3.Scale(centerLocal, scale);

            if (dt > 0f) vel = (p - prev) / dt;
            prev = p;
            yield return null;
        }

        transform.localScale = baseScale;
        IsInScriptedFlight = false;
        flightRoutine = null;
        if (State == CarryState.Consumed) yield break;

        if (IsDead(receiver))
        {
            EndCarry(vel * 0.6f, true);
            yield break;
        }

        if (!receiver.CanAccept(this, out string reason))
        {
            // Bounce back the way it came.
            Vector3 back = -vel; back.y = 0f;
            back = back.sqrMagnitude > 0.01f ? back.normalized : -travelDir;
            EndCarry(back * 2.5f + Vector3.up * 3f, true);
            Body.angularVelocity = Random.insideUnitSphere * 6f;
            if (receiver is ICarryReceiverFeedback fb) fb.OnRejected(this, reason, true);
            else if (!string.IsNullOrEmpty(reason)) GameHUD.Toast(reason, ToastStyle.Bad);
            yield break;
        }

        if (TryNetDeliver(this, receiver)) yield break;
        receiver.Accept(this);
    }

    static bool IsDead(ICarryReceiver r) => r == null || (r is Object o && o == null);

    void StopFlight()
    {
        if (flightRoutine != null)
        {
            StopCoroutine(flightRoutine);
            flightRoutine = null;
        }
        IsInScriptedFlight = false;
    }

    // ---------------- hooks for subclasses ----------------
    protected virtual void OnPickedUp(TrashCarrier carrier) { }
    protected virtual void OnReleased(TrashCarrier carrier, bool thrown) { }
    /// <summary>Physics impact while free (Resting or Thrown). speed = relative impact speed.</summary>
    protected virtual void OnImpact(Collision collision, float speed) { }
    /// <summary>A thrown object came to rest (State just went Thrown → Resting).</summary>
    protected virtual void OnSettled() { }

    protected virtual void OnCollisionEnter(Collision collision)
    {
        if (NetRemote) return; // co-op: impacts are decided by the machine that simulates it (§5.2)
        if (State == CarryState.Carried || State == CarryState.Consumed || IsInScriptedFlight) return;
        LastImpactTime = Time.time;
        OnImpact(collision, collision.relativeVelocity.magnitude);
    }

    protected virtual void FixedUpdate()
    {
        if (Body == null || State == CarryState.Carried || State == CarryState.Consumed || IsInScriptedFlight) return;
        if (Body.isKinematic || NetRemote) return;

        // Fell out of the world (through a seam, off the map): put it back where it last sat still.
        if (transform.position.y < KillPlaneY)
        {
            Vector3 back = hasSafePosition ? lastSafePosition : Vector3.up * 2f;
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Body.position = back + Vector3.up * 0.5f;
            transform.position = back + Vector3.up * 0.5f;
            State = CarryState.Resting;
            return;
        }

        if (State == CarryState.Thrown)
        {
            // A thrown object that has settled becomes grabbable-at-rest again.
            bool still = Body.linearVelocity.sqrMagnitude < 0.05f && Body.angularVelocity.sqrMagnitude < 0.5f;
            settleTimer = still ? settleTimer + Time.fixedDeltaTime : 0f;
            if (settleTimer >= 0.15f || Time.time - thrownSince > 8f)
            {
                State = CarryState.Resting;
                Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                RememberSafePosition();
                OnSettled();
            }
        }
        else if (Body.IsSleeping() && !hasSafePosition)
        {
            RememberSafePosition();
        }
    }

    protected void SetCollidersEnabled(bool on)
    {
        foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = on;
        if (on && this is LooseItem) GetComponent<LooseTrashCollisions>()?.Apply();
    }

    // ---------------- helpers ----------------
    /// <summary>World-space center of this object's renderers (falls back to the transform position).</summary>
    public Vector3 WorldCenter => transform.TransformPoint(LocalBounds().center);

    /// <summary>Renderer bounds in this object's local space (unscaled local units), independent of how it's lying.</summary>
    public Bounds LocalBounds()
    {
        var rends = GetComponentsInChildren<Renderer>();
        bool any = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.one * 0.2f);
        var inv = transform.worldToLocalMatrix;
        foreach (var r in rends)
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            if (r.GetComponent<TMPro.TMP_Text>() != null) continue; // floating labels aren't part of the object
            Bounds src; Matrix4x4 toLocal;
            var mf = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
            if (mf != null && mf.sharedMesh != null)
            {
                src = mf.sharedMesh.bounds;                       // mesh space → our local space (exact)
                toLocal = inv * r.transform.localToWorldMatrix;
            }
            else
            {
                src = r.bounds;                                   // world AABB → our local space (a bit loose)
                toLocal = inv;
            }
            if (src.size == Vector3.zero) continue;
            Vector3 c = src.center, e = src.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 lp = toLocal.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(lp, Vector3.zero); any = true; }
                else b.Encapsulate(lp);
            }
        }
        if (!any)
        {
            var col = GetComponent<Collider>();
            if (col is BoxCollider bc) b = new Bounds(bc.center, bc.size);
        }
        return b;
    }

    void MakeKinematic()
    {
        if (Body == null) return;
        // Kinematic bodies only support discrete/speculative CCD — switch first to avoid warnings.
        Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        if (!Body.isKinematic)
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
        }
        Body.isKinematic = true;
        Body.interpolation = RigidbodyInterpolation.None;
    }

    void MakeDynamic(bool thrown)
    {
        if (Body == null) return;
        Body.isKinematic = false;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = thrown ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
        Body.WakeUp();
    }

    void RememberSafePosition()
    {
        if (transform.position.y < KillPlaneY + 5f) return;
        lastSafePosition = transform.position;
        hasSafePosition = true;
    }

    /// <summary>
    /// Push this object out of solid geometry it overlaps after being placed by script (e.g. dropped
    /// while pressed against a wall or the truck). Ignores the player, triggers and light dynamic
    /// props (physics sorts those out gently). Call with colliders enabled.
    /// </summary>
    public void ResolvePenetration(int iterations = 3)
    {
        var cols = GetComponentsInChildren<Collider>();
        if (cols.Length == 0) return;
        Physics.SyncTransforms();
        for (int it = 0; it < iterations; it++)
        {
            bool moved = false;
            foreach (var col in cols)
            {
                if (col == null || !col.enabled || col.isTrigger) continue;
                Bounds b = col.bounds;
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.01f, overlapBuffer, Quaternion.identity,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int k = 0; k < n; k++)
                {
                    var other = overlapBuffer[k];
                    if (other == null || other.isTrigger || other is CharacterController) continue;
                    if (other.transform.IsChildOf(transform)) continue;
                    var orb = other.attachedRigidbody;
                    if (orb != null && !orb.isKinematic && orb.mass < 200f) continue;
                    if (Physics.ComputePenetration(col, col.transform.position, col.transform.rotation,
                            other, other.transform.position, other.transform.rotation, out Vector3 dir, out float dist))
                    {
                        if (dist <= 0.001f) continue;
                        transform.position += dir * (dist + 0.01f);
                        Physics.SyncTransforms();
                        moved = true;
                    }
                }
            }
            if (!moved) break;
        }
        if (Body != null) Body.position = transform.position;
    }
}

/// <summary>Something carryables can be tossed/thrown into: truck hopper, sorting table, bins, compactor, keep crate.</summary>
public interface ICarryReceiver
{
    /// <summary>World point auto-tosses arc to (e.g. the mouth of the hopper/bin).</summary>
    Vector3 TossTargetPosition { get; }
    /// <summary>Max horizontal distance from the player to TossTargetPosition for the "toss" prompt.</summary>
    float TossRange { get; }
    /// <summary>Whether this receiver will take the item. reason is shown to the player when false ("Truck full!").</summary>
    bool CanAccept(Carryable item, out string reason);
    /// <summary>Prompt text for the toss action, e.g. "Toss into truck".</summary>
    string GetTossPrompt(Carryable item);
    /// <summary>Take the item (it has arrived via auto-toss or entered the receiver's trigger). Receiver owns it now.</summary>
    void Accept(Carryable item);
}

/// <summary>
/// Optional extra for receivers that want to react when an item is refused (truck-full buzz, bin wobble,
/// bouncing the item back out). Called by TrashCarrier.TossInto (physical=false: the player still holds it
/// and has already been shown the reason), by toss arcs that arrive at a receiver that no longer accepts,
/// and by CarryReceiverTrigger (physical=true: the item is a free physics object the receiver may bounce).
/// </summary>
public interface ICarryReceiverFeedback
{
    void OnRejected(Carryable item, string reason, bool physical);
}

/// <summary>Global registry of receivers so the carrier can find the one it's standing at.</summary>
public static class CarryReceivers
{
    static readonly List<ICarryReceiver> all = new List<ICarryReceiver>();
    public static IReadOnlyList<ICarryReceiver> All => all;

    public static void Register(ICarryReceiver r) { if (r != null && !all.Contains(r)) all.Add(r); }
    public static void Unregister(ICarryReceiver r) { all.Remove(r); }

    /// <summary>Closest receiver in range that the player is roughly facing (any receiver within 1m counts regardless of facing).</summary>
    public static ICarryReceiver FindBest(Vector3 playerPos, Vector3 playerForward, Carryable item)
    {
        ICarryReceiver best = null;
        float bestScore = float.MinValue;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var r = all[i];
            if (r == null || (r is Object o && o == null)) { all.RemoveAt(i); continue; }
            if (r is Behaviour b && !b.isActiveAndEnabled) continue;
            Vector3 to = r.TossTargetPosition - playerPos; to.y = 0;
            float d = to.magnitude;
            if (d > r.TossRange) continue;
            float facing = d < 1f ? 1f : Vector3.Dot(playerForward.normalized, to / Mathf.Max(d, 0.001f));
            if (facing < 0.2f) continue;
            float score = facing * 2f - d / Mathf.Max(r.TossRange, 0.01f);
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}

static class CarryableStatics
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Carryable.NetDeliver = null;
        Carryable.NetConsumed = null;
        Carryable.NetScriptTake = null;
    }
}

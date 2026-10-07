using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lives on the Player prefab. Holds at most one Carryable. Owned by the Pickup system — the public
/// API is a FROZEN CONTRACT.
///
/// Controls (owner only, on foot, suppressed while PlayerController.IsUIBlocking):
///   E              : pick up (via Carryable IInteractable) / toss into the receiver you're at (via CollectCandidates)
///   Hold left mouse: charge a throw (0.1–1.0 s → 5–14 m/s), release to yeet along the camera direction
///   G              : drop
///
/// Feel: the held thing hangs in front of the chest on an under-damped spring, so it lags, swings and
/// bobs as you walk (heavier = floppier). It never passes through walls (sphere-cast clamp) and the
/// player walks slower the heavier it is (speeds always restored on release / destroy / driving).
///
/// Additive hooks for the first-person sort station: SetExternalControl (mute own input + hint),
/// SetHoldOverride (hold in first-person hands instead of at the chest), ReleaseToScript (hand the item to a
/// scripted animation).
/// </summary>
public class TrashCarrier : MonoBehaviour, IInteractionProvider
{
    public static TrashCarrier Local { get; private set; }

    public const string CarryHint = "[E] Toss  [Hold LMB] Throw  [G] Drop";
    const string PadCarryHint = "[X] Toss  [Hold RT] Throw  [B] Drop";

    /// <summary>
    /// Optional replacement for the third-person carry hint (e.g. the sort station's "[E] Step up" while you stand in
    /// its entry zone, where E doesn't toss). Return null for the default. Must not allocate (called every frame).
    /// </summary>
    public static Func<TrashCarrier, string> CarryHintOverride;

    string CurrentCarryHint() => CarryHintOverride?.Invoke(this) ?? (GameControls.UsingGamepad ? PadCarryHint : CarryHint);

    /// <summary>
    /// Co-op (N2, Docs/Multiplayer.md §4.6): false while the host hasn't confirmed this carrier holds its item yet (the heave).
    /// Throws pressed meanwhile are buffered and fire once it says yes; drops and tosses wait. Null = always free (solo).
    /// </summary>
    public static Func<TrashCarrier, bool> NetCanRelease;

    /// <summary>Co-op: extra E actions while holding something (PASS to a teammate). Called from CollectCandidates.</summary>
    public static Action<TrashCarrier, List<InteractionCandidate>> NetCandidates;

    bool ReleaseAllowed()
    {
        var f = NetCanRelease;
        if (f == null) return true;
        try { return f(this); }
        catch (Exception e) { Debug.LogException(e); return true; }
    }

    // a throw released during the heave (fires when the host confirms the grab)
    bool throwBuffered;
    float bufferedCharge;

    public KeyCode dropKey = KeyCode.G;
    public int throwMouseButton = 0;
    [Tooltip("Speed used by the parameterless Throw() (e.g. called from code).")]
    public float throwSpeed = 9f;
    [Tooltip("Legacy: extra upward speed for the parameterless Throw().")]
    public float throwUpSpeed = 3.5f;
    [Tooltip("Rest position of the carried object's center, in player-local space (feet pivot).")]
    public Vector3 holdLocalPosition = new Vector3(0f, 1.05f, 0.55f);

    [Header("Throw")]
    public float minThrowSpeed = 5f;
    public float maxThrowSpeed = 14f;
    public float minChargeTime = 0.1f;
    public float maxChargeTime = 1.0f;
    [Tooltip("Upward component added to the flattened camera forward.")]
    public float throwArcUp = 0.32f;
    [Tooltip("Seconds for the E auto-toss arc into a receiver.")]
    public float tossDuration = 0.45f;
    [Tooltip("How far (world m) a full throw charge pulls the held thing back toward the chest.")]
    public float chargePullIn = 0.22f;
    [Tooltip("How far (world m) a full throw charge lifts the held thing.")]
    public float chargeLift = 0.14f;

    [Header("Floppy hold")]
    [Tooltip("Spring angular frequency (rad/s). Lower = laggier.")]
    public float holdStiffness = 15f;
    [Tooltip("Damping ratio. < 1 = swings past and wobbles back.")]
    [Range(0.1f, 1.5f)] public float holdDamping = 0.42f;
    [Tooltip("How much heavier items loosen the spring (per 20 kg).")]
    public float heavyFlop = 0.45f;
    [Tooltip("Max tilt (degrees) from the swing.")]
    public float maxSwingTilt = 28f;
    [Tooltip("Chest hold: how far (world m) the held thing may sag back toward the body behind its rest reach (starts, " +
             "turns). The spring damps against the moving hold target, so walking itself leaves no lag. Negative = no stop.")]
    public float bellyStop = 0.04f;

    [Header("Aim preview")]
    [Tooltip("Show the dotted arc (turns green when it'll land in a receiver) while charging a throw.")]
    public bool showAimPreview = true;
    public Color aimColor = new Color(1f, 1f, 1f, 1f);
    public Color aimRiskyColor = new Color(1f, 0.45f, 0.15f, 1f);
    public Color aimScoreColor = new Color(0.35f, 1f, 0.4f, 1f);

    [Header("Carry slowdown")]
    [Tooltip("Speed factor = 1 - clamp(mass / slowdownMassKg, 0, maxSlowdown).")]
    public float slowdownMassKg = 40f;
    public float maxSlowdown = 0.45f;

    public Carryable Held { get; private set; }
    public bool IsCarrying => Held != null;
    /// <summary>True while the throw button is held down charging.</summary>
    public bool IsCharging => charging;
    /// <summary>0..1 throw charge while charging (0 otherwise).</summary>
    public float Charge01 => charging ? ChargeFromTime(Time.unscaledTime - chargeStart) : 0f;

    public event Action<Carryable> PickedUp;
    public event Action<Carryable> Released;

    PlayerController player;
    Transform holdPoint;
    TossAction tossAction;
    StarterAssets.ThirdPersonController tpc;
    CharacterController cc;

    public bool IsLocalOwner => player != null && player.IsOwner;
    public PlayerController Player => player;
    /// <summary>
    /// Where the held object's center is right now (spring-lagged). With nothing held it rests at
    /// holdLocalPosition in front of the chest.
    /// </summary>
    public Transform HoldPoint => holdPoint;
    /// <summary>Half-extents (world m, in the held item's own axes) of what we're holding; zero when empty.</summary>
    public Vector3 HeldExtents => Held != null ? heldExtents : Vector3.zero;

    // --- held-object bookkeeping ---
    Carryable heldRef;              // raw reference so we can notice a destroyed Held
    Vector3 heldCenterLocal;        // renderer-bounds center in the item's local space
    Vector3 heldExtents;
    Vector3 heldBaseScale;
    bool scaleTouched;

    // --- spring ---
    Vector3 springPos, springVel;
    Vector3 lastTarget;             // chest-hold target last frame (for the target's velocity)
    bool hasLastTarget, lastWasOverride;
    float heldAge;
    Quaternion holdRot;
    float walkPhase;
    Vector3 lastSafeCenter;

    // --- speed ---
    bool speedApplied;
    float baseMoveSpeed, baseSprintSpeed;

    // --- throw charge ---
    bool charging;
    float chargeStart;
    bool fullChargePinged;

    // --- hint ---
    string lastHint;
    bool hintOwned;

    // --- external control / hold override (first-person sort station) ---
    bool externalControl;
    Transform holdOverride, holdOverrideCastFrom;
    float holdOverrideStiffness = 1f;

    // --- aim preview ---
    LineRenderer aimLine;
    int aimColorState = -1;
    static readonly Vector3[] aimPoints = new Vector3[48];

    static readonly RaycastHit[] castBuffer = new RaycastHit[16];
    static readonly string[] YeetWords = { "YEET!", "HNNNG!", "FWOOMP!", "BYE!", "SKRRT!" };
    static readonly string[] OofWords = { "oof", "hnngh", "urk", "oof!", "*grunt*" };

    void Awake()
    {
        player = GetComponent<PlayerController>();
        tpc = GetComponent<StarterAssets.ThirdPersonController>();
        cc = GetComponent<CharacterController>();
        tossAction = new TossAction(this);
        var hp = new GameObject("CarryHoldPoint");
        holdPoint = hp.transform;
        holdPoint.SetParent(transform, false);
        holdPoint.localPosition = holdLocalPosition;
    }

    void Update()
    {
        if (!IsLocalOwner) return;
        if (Local != this) Local = this;

        // Held object destroyed out from under us (scene unload, someone Destroy()ed it)?
        if (!ReferenceEquals(heldRef, null) && heldRef == null) LoseHeld();

        if (Held == null)
        {
            SetHint(null);
            return;
        }

        // Somebody else took it (Consume, another carrier, a receiver) without telling us.
        if (Held.Holder != this || Held.State != CarryState.Carried)
        {
            LoseHeld();
            return;
        }

        if (player.IsDriving || player.IsPlayingChess)
        {
            // Hands full of garbage is no way to drive (or play chess): set it down where we stood.
            ForceDrop();
            return;
        }

        if (PlayerController.IsUIBlocking)
        {
            CancelCharge();
            return;
        }

        // Someone else (the first-person sort station) owns the controls and the hint line right now.
        if (externalControl)
        {
            CancelCharge();
            return;
        }

        if (throwBuffered)
        {
            if (ReleaseAllowed())
            {
                throwBuffered = false;
                ThrowWithCharge(bufferedCharge);
                return;
            }
            SetHint("Heave!");
            return;
        }

        if (GameControls.Down(dropKey, GameControls.Action.Drop))
        {
            if (!ReleaseAllowed()) return;
            CancelCharge();
            Drop();
            return;
        }

        if (!charging && (Input.GetMouseButtonDown(throwMouseButton) || GameControls.PadDown(GameControls.Action.Throw)))
        {
            charging = true;
            fullChargePinged = false;
            chargeStart = Time.unscaledTime; // unscaled: hit-stops mustn't stall the wind-up
            Sfx.Play("squeak", Held.WorldCenter, 0.35f, 0.8f);
        }
        else if (charging)
        {
            float c = Charge01;
            if (c >= 1f && !fullChargePinged)
            {
                fullChargePinged = true;
                Sfx.Play("pop", Held.WorldCenter, 0.5f, 1.5f);
                Juice.Punch(Held.JuiceTarget, 0.12f, 0.15f);
            }
            if (!Input.GetMouseButton(throwMouseButton) && !GameControls.PadHeld(GameControls.Action.Throw))
            {
                charging = false;
                if (!ReleaseAllowed())
                {
                    // Still heaving (the host hasn't said it's ours yet): the throw fires the moment it does (§4.6).
                    throwBuffered = true;
                    bufferedCharge = c;
                    return;
                }
                ThrowWithCharge(c);
                return;
            }
        }

        SetHint(charging ? ChargeHint(Charge01) : CurrentCarryHint());
    }

    void LateUpdate()
    {
        if (Held == null || Held.State != CarryState.Carried || Held.Holder != this)
        {
            UpdateAimPreview(false);
            return;
        }
        if (player != null && (player.IsDriving || player.IsPlayingChess)) { ForceDrop(); return; }
        if (charging) TurnTowardAim(Time.deltaTime);
        PoseHeld(Time.deltaTime);
        UpdateAimPreview(charging && IsLocalOwner && showAimPreview);
    }

    void OnDisable()
    {
        // Player despawned / destroyed / disabled: never leave a kinematic, collider-less ghost behind.
        if (Held != null) ForceDrop(quiet: true);
        else if (!ReferenceEquals(heldRef, null)) LoseHeld();
        CancelCharge();
        RestoreSpeed();
        SetHint(null);
    }

    void OnDestroy()
    {
        if (Local == this) Local = null;
        if (aimLine != null) Destroy(aimLine.gameObject);
    }

    // =====================================================================
    // Public API
    // =====================================================================

    public bool PickUp(Carryable c)
    {
        if (c == null || Held != null || player == null || player.IsDriving || player.IsPlayingChess) return false;
        var hands = HandsNet.Instance;
        if (hands != null && hands.Active && hands.applying == 0 && hands.TugOf(player.OwnerClientId).HasValue) return false;
        if (c.State == CarryState.Carried || c.State == CarryState.Consumed || c.IsInScriptedFlight) return false;

        Vector3 oldCenter = c.WorldCenter;
        var lb = c.LocalBounds();
        heldCenterLocal = lb.center;
        heldExtents = Vector3.Scale(lb.extents, c.transform.lossyScale);
        heldBaseScale = c.transform.localScale;
        scaleTouched = false;

        Held = c;
        heldRef = c;
        c.BeginCarry(this, holdPoint);

        // Start the spring where the thing was so it swoops up into our arms.
        springPos = oldCenter;
        springVel = Vector3.up * 2.5f;
        hasLastTarget = false;
        heldAge = 0f;
        holdRot = c.transform.rotation;
        lastSafeCenter = oldCenter;

        ApplySlowdown(c.MassKg);

        // --- juice ---
        float mass = c.MassKg;
        float heavy01 = Mathf.InverseLerp(1f, 20f, mass);
        Sfx.Play("pickup", oldCenter, 0.9f, Mathf.Lerp(1.2f, 0.8f, heavy01) * UnityEngine.Random.Range(0.95f, 1.05f));
        if (c is TrashBag) Sfx.Play("rustle", oldCenter, 0.7f, UnityEngine.Random.Range(0.9f, 1.15f));
        Fx.Dust(oldCenter - Vector3.up * heldExtents.y, Mathf.Lerp(0.25f, 0.6f, heavy01));
        Juice.Squash(c.JuiceTarget, -0.22f, 0.22f); // negative = stretch as it's yanked up
        if (mass > 12f && IsLocalOwner)
            GameHUD.FloatingText(transform.position + Vector3.up * 2.1f, OofWords[UnityEngine.Random.Range(0, OofWords.Length)], new Color(1f, 0.85f, 0.6f), 0.8f);

        if (IsLocalOwner) SetHint(CurrentCarryHint());
        PickedUp?.Invoke(c);
        return true;
    }

    public void Drop()
    {
        if (Held == null) return;
        var c = Held;
        Vector3 v = PlayerVelocity() * 0.6f + Vector3.down * 0.5f;
        ReleaseBookkeeping(c);
        c.EndCarry(v, false);
        c.Body.angularVelocity = UnityEngine.Random.insideUnitSphere * 1.5f;
        IgnorePlayerBriefly(c, 0.5f);
        Sfx.Play("drop", c.WorldCenter, 0.8f, UnityEngine.Random.Range(0.9f, 1.1f));
        Juice.Squash(c.JuiceTarget, 0.15f, 0.2f);
        Released?.Invoke(c);
    }

    /// <summary>Throw with the default (medium) power. The charged throw goes through ThrowWithCharge.</summary>
    public void Throw()
    {
        if (Held == null) return;
        float c01 = Mathf.InverseLerp(minThrowSpeed, maxThrowSpeed, throwSpeed);
        ThrowWithCharge(c01);
    }

    /// <summary>Throw the held item along the camera's (flattened) forward with an upward arc. charge01 0..1 maps to minThrowSpeed..maxThrowSpeed.</summary>
    public void ThrowWithCharge(float charge01)
    {
        if (Held == null) return;
        charging = false;
        var c = Held;
        charge01 = Mathf.Clamp01(charge01);
        ComputeThrow(charge01, out Vector3 aimFwd, out Vector3 launchCenter, out Vector3 v);

        // Face the throw, and put the item in front of us in that direction before letting go.
        transform.rotation = Quaternion.LookRotation(aimFwd, Vector3.up);
        PlaceHeldAt(launchCenter, Quaternion.LookRotation(aimFwd, Vector3.up));

        ReleaseBookkeeping(c);
        c.EndCarry(v, true);
        c.Body.angularVelocity = (Vector3.Cross(Vector3.up, aimFwd) * UnityEngine.Random.Range(3f, 7f)) + UnityEngine.Random.insideUnitSphere * 2f;
        IgnorePlayerBriefly(c, 0.5f);

        // --- juice ---
        Sfx.Play("throw", launchCenter, 0.8f + 0.2f * charge01, Mathf.Lerp(1.1f, 0.85f, charge01));
        Sfx.Play("whoosh", launchCenter, 0.5f + 0.5f * charge01, Mathf.Lerp(0.9f, 1.3f, charge01));
        Juice.Squash(c.JuiceTarget, -0.3f * (0.5f + charge01), 0.25f);
        if (charge01 >= 0.85f)
        {
            Juice.Shake(0.12f, 0.12f);
            GameHUD.FloatingText(launchCenter + Vector3.up * 0.6f, YeetWords[UnityEngine.Random.Range(0, YeetWords.Length)], new Color(1f, 0.55f, 0.15f), 1.2f);
        }
        Released?.Invoke(c);
    }

    /// <summary>
    /// Auto-toss the held item into a receiver: animated arc (~0.45 s, spin, whoosh) to its
    /// TossTargetPosition, then receiver.Accept. If it won't take it: "nope" feedback and keep holding.
    /// </summary>
    public void TossInto(ICarryReceiver receiver)
    {
        if (Held == null || receiver == null) return;
        if (receiver is UnityEngine.Object o && o == null) return;
        var c = Held;
        if (!receiver.CanAccept(c, out string reason))
        {
            Nope(c, reason);
            if (receiver is ICarryReceiverFeedback fb) fb.OnRejected(c, reason, false);
            return;
        }

        // Turn to face where it's going.
        Vector3 to = receiver.TossTargetPosition - transform.position; to.y = 0f;
        if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);

        Vector3 from = c.WorldCenter;
        ReleaseBookkeeping(c);
        c.SetInFlight();
        c.TossArcInto(receiver, tossDuration);
        Sfx.Play("whoosh", from, 0.8f, UnityEngine.Random.Range(1.0f, 1.2f));
        Sfx.Play("throw", from, 0.5f, 1.15f);
        Juice.Squash(c.JuiceTarget, -0.2f, 0.2f);
        Released?.Invoke(c);
    }

    /// <summary>Called by Carryable.Consume if the item gets consumed out from under us.</summary>
    public void ForgetHeld(Carryable c)
    {
        if (c == null || Held != c) return;
        ReleaseBookkeeping(c);
        Released?.Invoke(c);
    }

    // ---------------- additive hooks (first-person sort station) ----------------

    /// <summary>
    /// True while another system (the first-person sort station) drives this carrier: its own input (G drop,
    /// hold-LMB throw) and its context hint are switched off. Holding, posing and the public API work as usual.
    /// </summary>
    public bool ExternalControl => externalControl;

    public void SetExternalControl(bool on)
    {
        if (externalControl == on) return;
        externalControl = on;
        CancelCharge();
        UpdateAimPreview(false);
        // Hand the hint line over without clearing it (the new owner sets its own); Update takes it back later.
        hintOwned = false;
        lastHint = null;
    }

    /// <summary>True while a hold-point override (SetHoldOverride) is active.</summary>
    public bool HasHoldOverride => holdOverride != null;

    /// <summary>
    /// While set, the held item springs to <paramref name="hand"/>'s position and faces its rotation instead of
    /// hanging in front of the chest (e.g. first-person hands). Walls are checked from <paramref name="castFrom"/>
    /// (e.g. the camera; null = no wall check). stiffnessScale tightens the floppy spring. Everything else
    /// (carrying, dropping, tossing, throwing) is unchanged. A destroyed hand transform falls back to the chest.
    /// </summary>
    public void SetHoldOverride(Transform hand, Transform castFrom = null, float stiffnessScale = 1.6f)
    {
        holdOverride = hand;
        holdOverrideCastFrom = castFrom;
        holdOverrideStiffness = Mathf.Max(0.1f, stiffnessScale);
    }

    public void ClearHoldOverride()
    {
        holdOverride = null;
        holdOverrideCastFrom = null;
        holdOverrideStiffness = 1f;
    }

    /// <summary>
    /// Let go of the held item and hand it to a script, exactly where it is: it becomes kinematic, collider-less
    /// and "in flight" (Carryable.SetInFlight). The caller animates it and later calls EndCarry or a receiver's
    /// Accept. Returns the item, or null when nothing was held.
    /// </summary>
    public Carryable ReleaseToScript()
    {
        var c = Held;
        if (c == null) return null;
        ReleaseBookkeeping(c);
        c.SetInFlight();
        Released?.Invoke(c);
        return c;
    }

    // ---------------- IInteractionProvider ----------------
    public void CollectCandidates(PlayerController p, List<InteractionCandidate> results)
    {
        if (Held == null || p == null || p.IsDriving) return;
        if (NetCandidates != null)
        {
            try { NetCandidates(this, results); }
            catch (Exception e) { Debug.LogException(e); }
        }
        var r = CarryReceivers.FindBest(transform.position, transform.forward, Held);
        if (r == null) return;
        tossAction.receiver = r;
        results.Add(new InteractionCandidate { Target = tossAction, Score = 3f });
    }

    /// <summary>IInteractable adapter so "toss into X" shows up as the E prompt.</summary>
    class TossAction : IInteractable
    {
        readonly TrashCarrier carrier;
        public ICarryReceiver receiver;
        public TossAction(TrashCarrier c) { carrier = c; }

        bool Valid => carrier.Held != null && receiver != null && !(receiver is UnityEngine.Object o && o == null);
        public bool CanInteract() => Valid && carrier.ReleaseAllowed();
        public string GetInteractionPrompt()
        {
            if (!Valid) return "";
            return receiver.CanAccept(carrier.Held, out string reason) ? receiver.GetTossPrompt(carrier.Held) : reason;
        }
        public void OnInteract(GameObject interactor) { if (Valid) carrier.TossInto(receiver); }
        public void Highlight(bool highlight) { }
    }

    // =====================================================================
    // Internals
    // =====================================================================

    float ChargeFromTime(float held)
    {
        if (held <= minChargeTime) return 0f;
        return Mathf.Clamp01((held - minChargeTime) / Mathf.Max(0.01f, maxChargeTime - minChargeTime));
    }

    static string ChargeHint(float c)
    {
        int n = Mathf.RoundToInt(c * 10f);
        string bar = new string('|', n) + new string('.', 10 - n);
        return c >= 0.85f ? $"Throw power [{bar}]  RISKY!" : $"Throw power [{bar}]  release to throw";
    }

    void CancelCharge()
    {
        charging = false;
        fullChargePinged = false;
        throwBuffered = false;
    }

    /// <summary>Clears all "I'm holding something" state. Does not touch the item's physics.</summary>
    void ReleaseBookkeeping(Carryable c)
    {
        CancelCharge();
        UpdateAimPreview(false);
        if (c != null && scaleTouched) c.transform.localScale = heldBaseScale;
        scaleTouched = false;
        Held = null;
        heldRef = null;
        RestoreSpeed();
        SetHint(null);
        if (holdPoint != null) holdPoint.localPosition = holdLocalPosition;
    }

    /// <summary>Held item vanished (destroyed) or was taken without ForgetHeld.</summary>
    void LoseHeld()
    {
        var c = heldRef; // may be a destroyed object (Unity-null) — listeners should null-check
        ReleaseBookkeeping(c != null ? c : null);
        Released?.Invoke(c);
    }

    /// <summary>Drop in place, safely — used when we start driving or get disabled/destroyed.</summary>
    void ForceDrop(bool quiet = false)
    {
        var c = Held;
        if (c == null) return;
        // Put it back where it last was legitimately (not wherever the player just teleported to).
        PlaceHeldAt(lastSafeCenter, c.transform.rotation);
        ReleaseBookkeeping(c);
        c.EndCarry(Vector3.zero, false);
        if (isActiveAndEnabled) IgnorePlayerBriefly(c, 0.5f);
        if (!quiet) Sfx.Play("drop", c.WorldCenter, 0.6f, 1f);
        Released?.Invoke(c);
    }

    void Nope(Carryable c, string reason)
    {
        Sfx.Play("buzz", c.WorldCenter, 0.8f, 1f);
        Juice.Punch(c.JuiceTarget, 0.25f, 0.25f);
        // Shake the held thing side to side ("nuh-uh").
        springVel += transform.right * (UnityEngine.Random.value < 0.5f ? -3f : 3f);
        if (!string.IsNullOrEmpty(reason)) GameHUD.Toast(reason, ToastStyle.Bad);
    }

    void ApplySlowdown(float massKg)
    {
        if (tpc == null) tpc = GetComponent<StarterAssets.ThirdPersonController>();
        if (tpc == null) return;
        if (!speedApplied)
        {
            baseMoveSpeed = tpc.MoveSpeed;
            baseSprintSpeed = tpc.SprintSpeed;
            speedApplied = true;
        }
        float f = 1f - Mathf.Clamp(massKg / Mathf.Max(1f, slowdownMassKg), 0f, maxSlowdown);
        tpc.MoveSpeed = baseMoveSpeed * f;
        tpc.SprintSpeed = baseSprintSpeed * f;
    }

    void RestoreSpeed()
    {
        if (!speedApplied) return;
        speedApplied = false;
        if (tpc == null) return;
        tpc.MoveSpeed = baseMoveSpeed;
        tpc.SprintSpeed = baseSprintSpeed;
    }

    void SetHint(string text)
    {
        if (externalControl) return; // the controlling system owns the hint line
        if (!IsLocalOwner && !hintOwned) return;
        if (string.IsNullOrEmpty(text))
        {
            if (!hintOwned) return;
            hintOwned = false;
            lastHint = null;
            GameHUD.SetContextHint(null);
            return;
        }
        if (hintOwned && text == lastHint) return;
        hintOwned = true;
        lastHint = text;
        GameHUD.SetContextHint(text);
    }

    Vector3 PlayerVelocity() => cc != null && cc.enabled ? cc.velocity : Vector3.zero;

    /// <summary>Where the held object's center wants to be this frame (before spring & wall clamp).</summary>
    Vector3 HoldTarget(Vector3 forward, float charge)
    {
        // Bigger things are held further out so they don't clip into the chest.
        float reach = holdLocalPosition.z + Mathf.Max(0f, heldExtents.z - 0.28f) - chargePullIn * charge;
        float height = holdLocalPosition.y + chargeLift * charge;
        return transform.position + Vector3.up * height + forward * reach;
    }

    /// <summary>Clamp a desired center so the held thing never pokes through walls/the truck.</summary>
    Vector3 SafeHoldCenter(Vector3 desired)
    {
        Vector3 origin = transform.position + Vector3.up * Mathf.Clamp(desired.y - transform.position.y, 0.5f, 1.6f);
        return SafeHoldCenterFrom(origin, desired);
    }

    /// <summary>Same wall clamp, cast from an explicit origin (the camera, for a hold-point override).</summary>
    Vector3 SafeHoldCenterFrom(Vector3 origin, Vector3 desired)
    {
        Vector3 delta = desired - origin;
        float dist = delta.magnitude;
        if (dist < 0.001f) return desired;
        float radius = Mathf.Clamp(Mathf.Min(heldExtents.x, heldExtents.z), 0.06f, 0.26f);
        int n = Physics.SphereCastNonAlloc(origin, radius, delta / dist, castBuffer, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = dist;
        for (int i = 0; i < n; i++)
        {
            var h = castBuffer[i];
            if (h.collider == null) continue;
            if (h.collider.transform.IsChildOf(transform)) continue;
            if (Held != null && h.collider.transform.IsChildOf(Held.transform)) continue;
            if (h.collider is CharacterController) continue;
            var rb = h.collider.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < 50f) continue; // light props get shoved, not respected
            if (h.distance <= 0f) { best = 0f; break; } // already overlapping at the chest: hug in close
            if (h.distance < best) best = h.distance;
        }
        return best < dist ? origin + delta / dist * Mathf.Max(0f, best - 0.02f) : desired;
    }

    void PoseHeld(float dt)
    {
        if (dt <= 0f) return;
        var c = Held;
        float charge = charging ? Charge01 : 0f;
        bool ov = holdOverride != null; // first-person hands (sort station) instead of the chest
        Vector3 fwd = ov ? holdOverride.forward : transform.forward; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;

        // Walk bob.
        Vector3 pv = PlayerVelocity(); pv.y = 0f;
        float speed = pv.magnitude;
        walkPhase += dt * (4f + speed * 2.6f);
        float bob = Mathf.Sin(walkPhase * 2f) * 0.025f * Mathf.Clamp01(speed / 3f);

        Vector3 target = ov ? holdOverride.position : HoldTarget(fwd, charge) + Vector3.up * bob;
        heldAge += dt;

        // Chest hold: the spring damps against the TARGET's velocity (and the target is swept across the frame), so
        // walking at a steady speed leaves no lag; starts, stops and turns still swing and wobble. Damping against
        // the absolute velocity used to trail the bag 2ζV/ω behind the target (12-30 cm), i.e. into the carrier.
        Vector3 targetVel = Vector3.zero;
        if (!ov && hasLastTarget && !lastWasOverride)
        {
            targetVel = (target - lastTarget) / dt;
            if (targetVel.sqrMagnitude > 400f) targetVel = Vector3.zero; // > 20 m/s: teleport, not motion
        }
        lastTarget = target;
        hasLastTarget = true;
        lastWasOverride = ov;

        // Teleported (respawn/scene) or first frame after pickup far away: don't sling across the map.
        if ((springPos - target).sqrMagnitude > 9f) { springPos = target; springVel = Vector3.zero; targetVel = Vector3.zero; }

        // Under-damped spring, sub-stepped for stability. Heavier = looser = floppier.
        float omega = holdStiffness * (ov ? holdOverrideStiffness : 1f) / (1f + heavyFlop * (c.MassKg / 20f));
        float zeta = ov ? Mathf.Max(holdDamping, 0.6f) : holdDamping;
        float span = Mathf.Min(dt, 0.1f);
        float remaining = span;
        while (remaining > 0f)
        {
            float h = Mathf.Min(remaining, 1f / 120f);
            Vector3 tgt = target - targetVel * remaining; // where the target was at the start of this sub-step
            Vector3 accel = -2f * zeta * omega * (springVel - targetVel) - omega * omega * (springPos - tgt);
            springVel += accel * h;
            springPos += springVel * h;
            remaining -= h;
        }

        // Belly stop: never sag back into the carrier more than bellyStop behind the rest reach (eased in over the
        // pick-up swoop, which may start beside or below him).
        if (!ov && bellyStop >= 0f)
        {
            float slack = bellyStop + Mathf.Max(0f, 0.45f - heldAge) * 3f;
            float minFwd = Vector3.Dot(target - transform.position, fwd) - slack;
            float f = Vector3.Dot(springPos - transform.position, fwd);
            if (f < minFwd)
            {
                springPos += fwd * (minFwd - f);
                float vIn = Vector3.Dot(springVel - targetVel, fwd);
                if (vIn < 0f) springVel -= fwd * vIn;
            }
        }

        // Never through walls.
        Vector3 safe = !ov ? SafeHoldCenter(springPos)
            : holdOverrideCastFrom != null ? SafeHoldCenterFrom(holdOverrideCastFrom.position, springPos)
            : springPos;
        if ((safe - springPos).sqrMagnitude > 0.0001f)
        {
            Vector3 push = (safe - springPos).normalized;
            float into = Vector3.Dot(springVel, push);
            if (into < 0f) springVel -= push * into; // kill velocity into the wall
            springPos = safe;
        }

        // Swing tilt from how far the spring lags behind its target (in player space).
        Vector3 lag = springPos - target;
        float lagFwd = Vector3.Dot(lag, fwd);
        float lagSide = Vector3.Dot(lag, Vector3.Cross(Vector3.up, fwd));
        float pitch = Mathf.Clamp(lagFwd * 110f, -maxSwingTilt, maxSwingTilt) - 12f * charge; // lean back while winding up
        float roll = Mathf.Clamp(-lagSide * 110f, -maxSwingTilt, maxSwingTilt);
        Quaternion baseRot = ov ? holdOverride.rotation : Quaternion.LookRotation(fwd, Vector3.up);
        Quaternion targetRot = baseRot * Quaternion.Euler(pitch, 0f, roll);
        holdRot = Quaternion.Slerp(holdRot, targetRot, 1f - Mathf.Exp(-14f * dt));

        // Wind-up squash (and a nervous jitter at full power).
        if (charge > 0f)
        {
            float jitter = charge >= 1f ? Mathf.Sin(Time.time * 70f) * 0.02f : 0f;
            c.transform.localScale = new Vector3(
                heldBaseScale.x * (1f + 0.1f * charge + jitter),
                heldBaseScale.y * (1f - 0.14f * charge - jitter),
                heldBaseScale.z * (1f + 0.1f * charge + jitter));
            scaleTouched = true;
        }
        else if (scaleTouched)
        {
            c.transform.localScale = heldBaseScale;
            scaleTouched = false;
        }

        PlaceHeldAt(springPos, holdRot);
        lastSafeCenter = springPos;
        if (holdPoint != null) holdPoint.position = springPos;
    }

    /// <summary>Put the held item's visual center at worldCenter with the given rotation.</summary>
    void PlaceHeldAt(Vector3 worldCenter, Quaternion rot)
    {
        var c = Held;
        if (c == null) return;
        var t = c.transform;
        t.rotation = rot;
        t.position = worldCenter - rot * Vector3.Scale(heldCenterLocal, t.lossyScale);
    }

    /// <summary>Where a throw with this charge would start and how fast it would go (camera-aimed).</summary>
    void ComputeThrow(float charge01, out Vector3 aimFwd, out Vector3 launchCenter, out Vector3 velocity)
    {
        float speed = Mathf.Lerp(minThrowSpeed, maxThrowSpeed, Mathf.Clamp01(charge01));
        aimFwd = transform.forward;
        float pitchUp = 0f;
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 f = cam.transform.forward;
            Vector3 flat = new Vector3(f.x, 0f, f.z);
            if (flat.sqrMagnitude > 0.01f) aimFwd = flat.normalized;
            pitchUp = Mathf.Clamp(f.y, -0.15f, 0.5f);
        }
        aimFwd.y = 0f;
        aimFwd = aimFwd.sqrMagnitude > 0.01f ? aimFwd.normalized : Vector3.forward;

        launchCenter = SafeHoldCenter(HoldTarget(aimFwd, 0f));
        Vector3 dir = (aimFwd + Vector3.up * (throwArcUp + pitchUp)).normalized;
        Vector3 pv = PlayerVelocity(); pv.y = 0f;
        velocity = dir * speed + pv * 0.4f;
    }

    /// <summary>While winding up and standing still, swivel to face where the camera aims.</summary>
    void TurnTowardAim(float dt)
    {
        Vector3 pv = PlayerVelocity(); pv.y = 0f;
        if (pv.sqrMagnitude > 0.04f) return; // moving: the locomotion controller owns facing
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 f = cam.transform.forward; f.y = 0f;
        if (f.sqrMagnitude < 0.01f) return;
        var target = Quaternion.LookRotation(f.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-12f * dt));
    }

    /// <summary>Dotted-ish arc of where the throw will go. Green when it ends in a receiver that will take it.</summary>
    void UpdateAimPreview(bool show)
    {
        if (!show || Held == null)
        {
            if (aimLine != null && aimLine.enabled) aimLine.enabled = false;
            return;
        }
        if (aimLine == null)
        {
            // Not parented to the player: vehicle/visibility toggles on the player's renderers must not touch it.
            var go = new GameObject("ThrowAimPreview");
            aimLine = go.AddComponent<LineRenderer>();
            aimLine.useWorldSpace = true;
            aimLine.sharedMaterial = PickupVisuals.MarkerMaterial;
            aimLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            aimLine.receiveShadows = false;
            aimLine.numCapVertices = 4;
            aimLine.textureMode = LineTextureMode.Stretch;
            aimLine.widthCurve = new AnimationCurve(new Keyframe(0f, 0.02f), new Keyframe(0.15f, 0.06f), new Keyframe(1f, 0.09f));
            aimColorState = -1;
        }
        if (!aimLine.enabled) aimLine.enabled = true;

        float c01 = Charge01;
        ComputeThrow(c01, out _, out Vector3 p, out Vector3 v);
        float drag = Held.Body != null ? Held.Body.linearDamping : 0f;
        Vector3 g = Physics.gravity;
        const float step = 0.035f;
        int count = 0;
        bool scores = false;
        aimPoints[count++] = p;
        int solidMask = Physics.DefaultRaycastLayers;
        for (int i = 0; i < aimPoints.Length - 1; i++)
        {
            v += g * step;
            v *= Mathf.Clamp01(1f - drag * step);
            Vector3 next = p + v * step;
            // Will it drop into a receiver's mouth?
            if (Physics.Linecast(p, next, out RaycastHit th, 1 << 2, QueryTriggerInteraction.Collide))
            {
                var crt = th.collider.GetComponent<CarryReceiverTrigger>();
                var r = crt != null ? crt.Receiver : null;
                if (r != null && r.CanAccept(Held, out _))
                {
                    aimPoints[count++] = th.point;
                    scores = true;
                    break;
                }
            }
            if (Physics.Linecast(p, next, out RaycastHit hit, solidMask, QueryTriggerInteraction.Ignore) &&
                !hit.collider.transform.IsChildOf(transform) && !hit.collider.transform.IsChildOf(Held.transform))
            {
                aimPoints[count++] = hit.point;
                break;
            }
            aimPoints[count++] = next;
            p = next;
        }
        aimLine.positionCount = count;
        aimLine.SetPositions(aimPoints);

        int state = scores ? 2 : c01 >= 0.85f ? 1 : 0;
        if (state != aimColorState)
        {
            aimColorState = state;
            PickupVisuals.SetColor(aimLine, state == 2 ? aimScoreColor : state == 1 ? aimRiskyColor : aimColor);
        }
    }

    void IgnorePlayerBriefly(Carryable c, float seconds)
    {
        if (c == null || cc == null || !cc.enabled || !isActiveAndEnabled) return;
        var cols = c.GetComponentsInChildren<Collider>();
        foreach (var col in cols) if (col != null && col.enabled && col.gameObject.activeInHierarchy) Physics.IgnoreCollision(col, cc, true);
        StartCoroutine(UnignoreLater(cols, seconds));
    }

    IEnumerator UnignoreLater(Collider[] cols, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        // (Ignore state resets by itself if either collider was disabled meanwhile.)
        if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) yield break;
        foreach (var col in cols) if (col != null && col.enabled && col.gameObject.activeInHierarchy) Physics.IgnoreCollision(col, cc, false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Local = null;
        CarryHintOverride = null;
        NetCanRelease = null;
        NetCandidates = null;
    }
}

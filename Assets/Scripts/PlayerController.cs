using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using System.Collections.Generic;

public class PlayerController : NetworkBehaviour
{
    [Header("Interaction")]
    public float interactDistance = 2f;
    public float drivingInteractDistance = 8f;
    public float playingChessInteractDistance = 5f;
    public LayerMask interactLayer;


    [Header("Proximity (on foot)")]
    [Tooltip("Radius of the sphere in front of the player that finds nearby interactables (bags on the ground etc).")]
    public float proximityRadius = 1.3f;

    [Header("Cameras")]
    public CinemachineVirtualCamera playerCamVCam;

    /// <summary>The locally-owned player (null in menus / before spawn).</summary>
    public static PlayerController Local { get; private set; }

    TrashCarrier carrier;
    readonly Collider[] overlapBuffer = new Collider[32];


    IInteractable currentInteractable;
    IInteractable lastHighlighted;
    readonly List<IInteractable> options = new();

    private NetworkVariable<bool> isDriving = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<bool> isPlayingChess = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner); // New


    public TruckControllerV2 currentTruck;
    public ChessController currentChessController;
    public DepotChessBoard currentDepotChessBoard;

    public override void OnNetworkSpawn()
    {
        // Co-op (MP §8.7.4 B2): input belongs to the owner only. A remote copy turns PlayerInput + StarterAssetsInputs off
        // before their Start runs (else the host's copy pairs the guest's keyboard); the owner re-pairs a frame later.
        CoopPlayerHooks.OnSpawned(this);

        // only the local client keeps their own vcam enabled
        carrier = GetComponent<TrashCarrier>();
        if (IsOwner)
        {
            Local = this;
            OnFootTruckCamera.Ensure(playerCamVCam, this);
            playerCamVCam.Priority = 11;
            var gate = Camera.main ? Camera.main.GetComponent<BlackoutUntilLocalPlayer>() : null;
            gate?.Unlock();
        }
        else
        {
            // remote players turn their camera off immediately
            playerCamVCam.Priority = 0;
            // enabled = false;      // disable this script entirely on non‐owners
        }

        isDriving.OnValueChanged += OnDriveStateChanged;
        isPlayingChess.OnValueChanged += OnPlayChessStateChanged; // New

        OnDriveStateChanged(false, isDriving.Value);
        OnPlayChessStateChanged(false, isPlayingChess.Value); // New
    }

    public override void OnDestroy()
    {
        if (Local == this) Local = null;
        base.OnDestroy();
        if (isDriving != null) isDriving.OnValueChanged -= OnDriveStateChanged;
        if (isPlayingChess != null) isPlayingChess.OnValueChanged -= OnPlayChessStateChanged; // New
    }

    void Update()
    {
        if (!IsOwner) return; // ensure this script runs only for the local player

        if (IsUIBlocking)
        {
            if (lastHighlighted != null) { lastHighlighted.Highlight(false); lastHighlighted = null; }
            TooltipUI.Instance?.Hide();
            return;
        }

        // Another system (the first-person sort station) owns focus, prompt and E/F right now.
        if (InteractionFocusOverride != null && InteractionFocusOverride(this))
        {
            if (lastHighlighted != null) { lastHighlighted.Highlight(false); lastHighlighted = null; }
            currentInteractable = null;
            options.Clear();
            return;
        }

        GatherOptions(options);
        // E → primary, F → alt. An IAltKeyPinned option (the garage door) always takes F; the best other option gets E.
        InteractionKeys.Resolve(options, out var primary, out var alt);
        currentInteractable = primary ?? alt;
        // clean highlight swap
        if (lastHighlighted != null && lastHighlighted != currentInteractable)
            lastHighlighted.Highlight(false);
        lastHighlighted = currentInteractable;

        if (currentInteractable != null)
        {
            currentInteractable.Highlight(true);
            if (primary != null)
            {
                TooltipUI.Instance.Show(IInteractable.InteractKey, primary.GetInteractionPrompt());
                if (alt != null) TooltipUI.Instance.AppendAltHint(IInteractable.AltInteractKey, alt.GetInteractionPrompt());
            }
            else
            {
                // Only the pinned option is in range: it still sits on F.
                TooltipUI.Instance.Show(IInteractable.AltInteractKey, alt.GetInteractionPrompt());
            }
        }
        else
        {
            TooltipUI.Instance.Hide();
        }

        if (primary != null && GameControls.Down(IInteractable.InteractKey, GameControls.Action.Interact))
        {
            // E → primary
            primary.OnInteract(gameObject);
        }
        else if (alt != null && GameControls.Down(IInteractable.AltInteractKey, GameControls.Action.Alternative))
        {
            // F → alt (the garage door whenever it is in range, else the runner-up)
            alt.OnInteract(gameObject);
        }
    }

    void GatherOptions(List<IInteractable> outList)
    {
        outList.Clear();
        var candidates = new List<InteractionCandidate>(8);

        // 1) World scan (LOS). Use camera when driving, player forward when on foot.
        var origin = transform.position + Vector3.up;
        var forward = transform.forward;
        var maxDist = IsDriving ? drivingInteractDistance : interactDistance;

        // SphereCast is more forgiving than a thin ray
        var hits = Physics.SphereCastAll(
            new Ray(origin, forward),
            0.2f,
            maxDist,
            interactLayer,
            QueryTriggerInteraction.Collide);

        foreach (var h in hits)
        {
            var i = h.collider.GetComponentInParent<IInteractable>();
            if (i != null && i.CanInteract())
            {
                // Simple scoring: closer + centered = better
                float distScore = 1f - Mathf.Clamp01(h.distance / maxDist);     // 0..1
                float aimScore = Vector3.Dot(forward.normalized, (h.point - origin).normalized); // -1..1
                float score = distScore + Mathf.Max(0f, aimScore);          // 0..2

                candidates.Add(new InteractionCandidate { Target = i, Score = score, HitCollider = h.collider });
            }
        }

        // 1b) Proximity scan on foot: things right in front of you (bags at your feet) that the
        //     chest-height ray passes over.
        if (!IsDriving)
            CollectProximity(candidates);

        // 2) Provider-based contextual actions (no LOS required)
        // Example: the truck you’re currently driving publishes “Exit Truck”
        if (currentTruck != null)
        {
            currentTruck.CollectCandidates(this, candidates);
        }

        // A seated player's CharacterController is disabled, so retain the leave-board action without LOS.
        if (currentDepotChessBoard != null)
            currentDepotChessBoard.CollectCandidates(this, candidates);

        // 2b) Volume-based actions (garage doors you drive through, unload levers, ...)
        InteractionZone.CollectCandidates(this, candidates);

        // 2c) Carrying something: "Toss into truck / bin" when standing at a receiver
        if (carrier != null)
            carrier.CollectCandidates(this, candidates);

        // Owned gear extends the ordinary E/X interaction list; V remains a dedicated shortcut.
        GetComponent<GrabberPole>()?.CollectCandidates(candidates);

        // 3) De-dup & order by score
        // A scoopable player should not disappear behind several loose pieces of trash in
        // the two-button E/F interaction list. This only boosts someone already found by
        // the normal range/facing/CanInteract scan (voluntary flop or punch-combo KO).
        // Bags and trash you can reach come before everything ordinary (see PickUpScore), but only on foot: a driver's E is
        // "Exit Truck" and a rider's is "Hop off", and a bag along the road ahead must not take either.
        bool onFoot = !IsDriving && !(TryGetComponent(out RideRider rider) && rider.IsRiding);
        for (int k = 0; k < candidates.Count; k++)
        {
            var c = candidates[k];
            if (c.Target is PlayerBody body &&
                (body.State == BodyState.Flopped || body.State == BodyState.KnockedOut))
            {
                c.Score += 3f;
                candidates[k] = c;
            }
            else if (onFoot && c.Target is Carryable item && Permissions.LocalCanWorkAt(item.transform.position))
            {
                c.Score = PickUpScore(c.Score);
                candidates[k] = c;
            }
        }
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var seen = new HashSet<IInteractable>();
        foreach (var c in candidates)
        {
            if (seen.Add(c.Target))
                outList.Add(c.Target);
        }
        // Only one thing fits in your hands: with a bag on E, F is better spent on the best thing that is NOT another bag (the
        // ride, the truck, a door) than on its neighbour, so trash coming first never shuts everything else out of reach.
        if (onFoot && outList.Count > 2 && outList[0] is Carryable && outList[1] is Carryable)
            for (int i = 2; i < outList.Count; i++)
                if (!(outList[i] is Carryable)) { var other = outList[i]; outList.RemoveAt(i); outList.Insert(1, other); break; }
        // HashSetPool is optional; just use a new HashSet if you prefer.
    }

    /// <summary>
    /// Where a bag or piece of trash you can reach sits in the E/F list (on foot: a driver's and a rider's E stay "Exit Truck" /
    /// "Hop off"). A look-at or nearby scan scores 0..2 and the zone actions
    /// around a truck (the ride on its back: <see cref="HitchStep.ZoneScore"/>, "Enter Truck", "Not your truck") score up to 2.05, so
    /// standing at the back of a truck with a pile in reach used to put "Hitch a ride" on E and leave the pile to F (Nick, 12 Oct
    /// 2026: "prioritize bags and trash over boarding"). The scan score is squeezed into a thin band just above all of those, so
    /// the nearer, better aimed piece still comes first among trash, and just under the zone actions that are meant to dominate
    /// (2.2+: workshop, heavy lift, raccoon shooing, the hutch door, a resident handing you a bag) and a downed friend (3+).
    /// </summary>
    public const float PickUpBandFloor = 2.1f, PickUpBandSpan = 0.09f;

    public static float PickUpScore(float scanScore) => PickUpBandFloor + PickUpBandSpan * Mathf.Clamp01(scanScore * 0.5f);

    void CollectProximity(List<InteractionCandidate> candidates)
    {
        Vector3 fwd = transform.forward;
        Vector3 feet = transform.position;
        Vector3 center = feet + Vector3.up * 0.6f + fwd * (proximityRadius * 0.7f);
        int n = Physics.OverlapSphereNonAlloc(center, proximityRadius, overlapBuffer, interactLayer, QueryTriggerInteraction.Collide);
        for (int k = 0; k < n; k++)
        {
            var col = overlapBuffer[k];
            if (col == null || col.transform.IsChildOf(transform)) continue;
            var i = col.GetComponentInParent<IInteractable>();
            if (i == null || !i.CanInteract()) continue;
            Vector3 p = col.ClosestPoint(center);
            Vector3 flat = p - feet; flat.y = 0f;
            float dist = flat.magnitude;
            float facing = dist < 0.3f ? 1f : Vector3.Dot(fwd, flat / dist);
            if (facing < -0.2f) continue;
            float score = 0.8f * (1f - Mathf.Clamp01(dist / (proximityRadius * 2f))) + 0.7f * Mathf.Max(0f, facing);
            candidates.Add(new InteractionCandidate { Target = i, Score = score, HitCollider = col });
        }
    }

    // ---------------- interaction focus override (first-person sort station) ----------------
    /// <summary>
    /// Generic takeover of the local player's interaction scan. While it returns true for this player, Update
    /// skips the scan, highlights, the tooltip and E/F handling — the provider owns them. Set it back to null
    /// (only if it is still yours) when done.
    /// </summary>
    public static System.Func<PlayerController, bool> InteractionFocusOverride;

    // ---------------- UI input lock (ebag laptop, menus) ----------------
    static int uiLocks;
    /// <summary>True while a UI (e.g. the ebag laptop) has taken over mouse/keyboard.</summary>
    public static bool IsUIBlocking => uiLocks > 0;

    /// <summary>Free the cursor and freeze player look/move/interact. Pair every Push with a Pop.</summary>
    public static void PushUILock()
    {
        uiLocks++;
        if (uiLocks == 1) ApplyUILock(true);
    }

    public static void PopUILock()
    {
        if (uiLocks == 0) return;
        uiLocks--;
        if (uiLocks == 0) ApplyUILock(false);
    }

    static void ApplyUILock(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = locked;
        var p = Local;
        if (p == null) return;
        var inputs = p.GetComponent<StarterAssets.StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.move = Vector2.zero;
            inputs.look = Vector2.zero;
            inputs.jump = false;
            inputs.sprint = false;
            inputs.cursorLocked = !locked;
            inputs.cursorInputForLook = !locked;
        }
        var pi = p.GetComponent<UnityEngine.InputSystem.PlayerInput>();
        if (pi != null)
        {
            if (locked) pi.DeactivateInput();
            else pi.ActivateInput();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        uiLocks = 0;
        Local = null;
        InteractionFocusOverride = null;
    }

    private void OnDriveStateChanged(bool oldValue, bool newValue)
    {
        // show/hide your mesh (or root) across ALL clients
        foreach (var rend in GetComponentsInChildren<Renderer>())
            rend.enabled = !newValue;
    }

    // New: Handles visuals for the chess state on all clients
    private void OnPlayChessStateChanged(bool oldValue, bool newValue)
    {
        foreach (var rend in GetComponentsInChildren<Renderer>())
            rend.enabled = !newValue;
    }

    public bool IsDriving
    {
        get => isDriving.Value;
        set => isDriving.Value = value;
    }
    
    // New: Public property for chess state
    public bool IsPlayingChess
    {
        get => isPlayingChess.Value;
        set => isPlayingChess.Value = value;
    }
}

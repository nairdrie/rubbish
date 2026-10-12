// using System.Numerics;
using StarterAssets;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The garbage truck. E to hop in (hands must be empty), WASD to drive, H to honk, E/F to hop out.
/// The local player is parented under the truck while seated (hidden, locomotion off, truck camera on).
///
/// Flow hooks: <see cref="ForceEnter"/> seats a player (auto-seat on arrival), <see cref="ForceExit"/> /
/// <see cref="PrepareForSceneTransition"/> cleanly unseat them (nothing NGO-owned may stay parented under
/// the truck when it's destroyed with its scene). With no driver whoever simulates the truck holds a parking brake:
/// the host, or a guest who has just got out, who keeps the truck until it stands still (<see cref="TruckHandbackWatch"/>).
/// </summary>
public class TruckControllerV2 : NetworkBehaviour, IInteractable
{

    [Header("Car Properties")]
    public float motorTorque = 2000f;
    public float brakeTorque = 2000f;
    public float maxSpeed = 20f;
    public float steeringRange = 30f;
    public float steeringRangeAtMaxSpeed = 10f;
    public float centreOfGravityOffset = -1f;
    [Tooltip("Cornering limit (m/s²): steering is clamped to atan(wheelbase · a / v²). Infinity = no limit (stock). " +
             "Set by the Scrapazon engine tiers (Docs/Progression.md §6.1, turbo L3 = 7).")]
    public float maxLateralAccel = float.PositiveInfinity;

    [Header("Parking & Feel")]
    [Tooltip("Brake torque per wheel while nobody is driving, so a parked truck never rolls away.")]
    public float parkingBrakeTorque = 6000f;
    public KeyCode hornKey = KeyCode.H;
    [Tooltip("Seconds between reverse-beeper beeps.")]
    public float reverseBeepInterval = 0.5f;
    [Tooltip("Backwards speed (m/s) above which the reverse beeper sounds.")]
    public float reverseBeepSpeed = 0.4f;

    private WheelControl[] wheels;
    private Rigidbody rigidBody;

    [SerializeField] private GameObject driverEjectPosition;

    private NetworkVariable<ulong> driverClientId = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    [SerializeField] private Light[] headlights;

    [SerializeField] private Light[] brakeLights;

    public bool HasDriver => driverClientId.Value != ulong.MaxValue;
    /// <summary>The driving client (ulong.MaxValue = nobody). N4: a fired employee is put out of the employer's truck.</summary>
    public ulong DriverClientId => driverClientId.Value;
    public bool IsLocalDriver => NetworkManager.Singleton != null && driverClientId.Value == NetworkManager.Singleton.LocalClientId;
    /// <summary>True while the local player is sitting in this truck (set immediately on enter, before the server confirms).</summary>
    public bool IsLocalPlayerSeated => seated && player != null;

    private NetworkObject networkObject;

    Cinemachine.CinemachineVirtualCamera truckCamVCam;
    Cinemachine.CinemachineTransposer truckCamFollow;
    Transform truckCamTarget;
    Quaternion truckCamRestRotation;
    Vector3 truckCamRestOffset;
    StarterAssetsInputs driverLookInput;
#if ENABLE_INPUT_SYSTEM
    PlayerInput driverPlayerInput;
#endif
    float driverCamYaw, driverCamHeight, lastDriverLookAt;

    // Orbit is local presentation only. The camera stays clear of the truck and recentres gently after idle input.
    const float DriverCamMinHeight = 4f, DriverCamMaxHeight = 13f;
    const float DriverCamMouseHeight = 0.055f, DriverCamStickHeightPerSecond = 5.5f;
    const float DriverCamStickYawPerSecond = 120f, DriverCamRecenterDelay = 3.5f, DriverCamRecenterDegreesPerSecond = 45f;
    GameObject player;
    PlayerController playerController;
    ThirdPersonController thirdPersonController;
    CharacterController charController;
    Animator animator;
    Cinemachine.CinemachineVirtualCamera playerCamVCam;

    bool seated;
    bool despawning;
    float nextBeepTime;
    float nextHornTime;
    // Cornering clamp (maxLateralAccel): wheelbase in metres; substeps are configured the first time the clamp is active.
    const float LateralWheelbase = 2.64f;
    bool vehicleSubstepsSet;

    /// <summary>
    /// F1 test rig / bots (Docs/Multiplayer.md §14.1): when set on the machine that simulates this truck, the truck drives
    /// with this input (x = steer −1..1, y = throttle/brake −1..1) instead of the keyboard, with or without a seated driver
    /// (a ghost driver, so four players can ride at once). Null = normal driving.
    /// </summary>
    public System.Func<Vector2> InputOverride;

    /// <summary>
    /// Someone drives this truck, as this machine's physics should see it (else it's parked). A guest who has just got out
    /// still shows in <see cref="DriverClientId"/> until the server's answer lands, a round trip later; on their machine,
    /// which keeps simulating the truck until it stands still, it is parked at once, as the host's is when the host gets out.
    /// Offline (not network-spawned, e.g. a preview scene): the seated local player drives.
    /// </summary>
    public bool AnyDriver => InputOverride != null || (IsSpawned ? HasDriver && (seated || !IsLocalDriver) : seated);
    bool LocalDrives => InputOverride != null ? (!IsSpawned || IsOwner) : (IsSpawned ? IsLocalDriver : seated);

    // ---------------------------------------------------------------- registry + horn event (WorldLife W0, additive;
    // Docs/WorldLife.md §11). Lets new code find trucks without FindFirstObjectByType (rule 3) and react to the horn.

    static readonly System.Collections.Generic.List<TruckControllerV2> all = new System.Collections.Generic.List<TruckControllerV2>(4);
    /// <summary>Every enabled truck in loaded scenes (registered in OnEnable / OnDisable).</summary>
    public static System.Collections.Generic.IReadOnlyList<TruckControllerV2> All => all;
    /// <summary>Raised on the driver's machine when this truck honks (M1/M2 relay it through the host).</summary>
    public static event System.Action<TruckControllerV2> Honked;
    /// <summary>The truck's Rigidbody (simulated on this machine when it isn't kinematic).</summary>
    public Rigidbody Body => rigidBody != null ? rigidBody : (rigidBody = GetComponent<Rigidbody>());
    /// <summary>
    /// The body box in metres relative to the root (rotation only; the root's 0.67 scale is ignored, like TruckPoint):
    /// x ±1.3, y 0.41..3.31, z −2.2 (rear) .. 3.8 (nose). WorldLife's predictive sensor stretches it along the velocity.
    /// </summary>
    public static Bounds BodyBoxMetres => new Bounds(
        new Vector3(0f, (BodyBottomY + BodyTopY) * 0.5f, (BodyRearZ + BodyNoseZ) * 0.5f),
        new Vector3(BodyHalfWidth * 2f, BodyTopY - BodyBottomY, BodyNoseZ - BodyRearZ));
    /// <summary>Physical envelope of this truck's selected body; static BodyBoxMetres is the rear-loader template.</summary>
    public Bounds ModelBodyBoxMetres => GetComponent<TruckModelVisuals>() is TruckModelVisuals model ? model.BodyBounds : BodyBoxMetres;
    const float BodyBottomY = 0.41f;

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() { all.Remove(this); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetTruckStatics() { all.Clear(); Honked = null; }

    public bool CanInteract()
    {
        if (IsLocalPlayerSeated) return true;                 // "Exit Truck"
        var rider = RideRider.LocalRider;                     // F1: no "Enter Truck" from the step or mid-fling
        if (rider != null && (rider.IsRiding || rider.IsFlying)) return false;
        if (HasDriver) return IsLocalDriver;
        if (SceneController.IsTransitioning) return false;
        // Hands full: the truck isn't offered at all (the hopper's "Toss into truck" handles bags).
        var carrier = TrashCarrier.Local;
        if (carrier != null && carrier.IsCarrying) return false;
        return true;
    }
    public string GetInteractionPrompt()
    {
        if (IsLocalPlayerSeated || (HasDriver && IsLocalDriver)) return "Exit Truck";
        return Permissions.CanDrive(Permissions.LocalClientId, this) ? "Enter Truck" : "Not your truck";
    }
    public bool IsInteracting() {
        return IsLocalPlayerSeated || (HasDriver && IsLocalDriver);
    }
    public void OnInteract(GameObject interactor)
    {
        if (IsLocalPlayerSeated || (HasDriver && IsLocalDriver))
        {
            ExitTruck();
            return;
        }
        if (HasDriver) return;
        if (!Permissions.CanDrive(Permissions.LocalClientId, this))
        {
            Permissions.Deny(transform.position, Permissions.NotYourTruck);
            return;
        }
        var carrier = interactor != null ? interactor.GetComponent<TrashCarrier>() : null;
        if (carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Hands full!", ToastStyle.Bad, 1.2f);
            Sfx.Play("buzz", transform.position, 0.6f);
            return;
        }
        EnterTruck(interactor);
    }

    public void Highlight(bool highlight)
    {
        // Implement highlight logic here, e.g., change color or show an outline
    }

    // This lets the truck offer “Exit Truck” even when the player is seated at local (0,0,0)
    public void CollectCandidates(PlayerController player, System.Collections.Generic.List<InteractionCandidate> results)
    {
        // Only the local driver should see this candidate
        if ((IsLocalDriver || IsLocalPlayerSeated) && CanInteract())
        {
            results.Add(new InteractionCandidate {
                Target = this,
                // Give it a modest score so LOS hits (e.g., a garage door you’re looking at)
                // beat it. If nothing else is hit, this wins by default.
                Score = 0.25f
            });
        }
    }

    // ---------------------------------------------------------------- flow API

    /// <summary>
    /// Seat <paramref name="playerObject"/> (the local player) in this truck right now, e.g. when arriving
    /// in a scene driving. Drops anything they carry. Safe to call right after the truck was spawned.
    /// </summary>
    public void ForceEnter(GameObject playerObject)
    {
        if (playerObject == null) return;
        if (IsLocalPlayerSeated)
        {
            if (player == playerObject) return;
            ExitInternal(true);
        }
        EnterInternal(playerObject, true);
        // Arrival feel (the screen is usually still fading in).
        Sfx.Play("engine_start", transform.position, 0.8f);
    }

    /// <summary>Unseat the local player (safe eject position, everything restored). No-op if nobody is seated here.</summary>
    public void ForceExit()
    {
        if (!IsLocalPlayerSeated) { CleanupDanglingSeat(); return; }
        ExitInternal(true);
    }

    /// <summary>
    /// Call before any scene load: unparents and restores the local driver so nothing NGO-owned stays
    /// under this truck (which is destroyed with the scene), and stops the truck.
    /// </summary>
    public void PrepareForSceneTransition()
    {
        ForceExit();
        ApplyAllBrakes(parkingBrakeTorque);
        TurnOffBrakeLights();
    }

    // ---------------------------------------------------------------- enter / exit

    void EnterTruck(GameObject interactor)
    {
        EnterInternal(interactor, false);
    }

    void EnterInternal(GameObject interactor, bool silent)
    {
        if (interactor == null) return;
        var pc = interactor.GetComponent<PlayerController>();
        if (pc == null) return;

        // Never drive off with a bag glued to your hands.
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying) carrier.Drop();

        player = interactor;
        // grab the player and its components
        playerController = pc;
        thirdPersonController = player.GetComponent<ThirdPersonController>();
        charController = player.GetComponent<CharacterController>();
        animator = player.GetComponent<Animator>();
        playerCamVCam = playerController.playerCamVCam; // expose this as public
        driverLookInput = player.GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM
        driverPlayerInput = player.GetComponent<PlayerInput>();
#endif
        if (driverLookInput != null) driverLookInput.look = Vector2.zero;
        ResetDriverCamera();
        lastDriverLookAt = Time.unscaledTime;

        // disable locomotion
        if (thirdPersonController != null) thirdPersonController.enabled = false;
        if (charController != null) charController.enabled = false;
        if (animator != null) animator.enabled = false;

        // hide player body
        foreach (var rend in player.GetComponentsInChildren<Renderer>())
            rend.enabled = false;

        // swap camera priorities
        if (playerCamVCam != null) playerCamVCam.Priority = 5;
        if (truckCamVCam != null) truckCamVCam.Priority = 11;

        // update state
        if (playerController.IsSpawned) playerController.IsDriving = true;
        playerController.currentTruck = this;

        player.transform.parent = this.transform;
        player.transform.localPosition = Vector3.zero;
        player.transform.localRotation = Quaternion.identity;
        seated = true;

        TurnOnHeadlights();
        ClaimDriver();

        GameHUD.SetContextHint("W/S drive · A/D steer · Mouse/stick look · H honk");
        if (!silent)
        {
            Sfx.Play("door", transform.position);
            Sfx.Play("engine_start", transform.position);
            Juice.Shake(0.12f, 0.2f);
            Fx.Dust(TruckPoint(new Vector3(0.9f, 0.5f, BodyRearZ - 0.1f)), 0.8f);
            GameHUD.FloatingText(TruckPoint(new Vector3(0f, BodyTopY + 0.6f, 1.5f)), "VROOM!", new Color(1f, 0.85f, 0.2f), 1.2f);
        }
    }

    void ExitTruck()
    {
        ExitInternal(false);
    }

    void ExitInternal(bool silent)
    {
        // hide tooltip
        if (TooltipUI.Instance != null) TooltipUI.Instance.Hide();

        if (player == null)
        {
            CleanupDanglingSeat();
            return;
        }

        player.transform.SetParent(null, true);

        Vector3 ejectPos = FindSafeEjectPosition();   // world
        Quaternion ejectRot = FlowUtil.Upright(transform.rotation);
        player.transform.SetPositionAndRotation(ejectPos, ejectRot);
        // Other machines must see a teleport, not a move: their copy of this player (CharacterController on) would otherwise
        // slide from the truck's root out through its body, a capsule shoving whichever copy of the truck is dynamic there.
        var playerNt = player.GetComponent<NetworkTransform>();
        if (playerNt != null && playerNt.IsSpawned && playerNt.CanCommitToTransform)
        {
            try { playerNt.Teleport(ejectPos, ejectRot, player.transform.localScale); }
            catch (System.Exception e) { Debug.LogWarning("[TruckControllerV2] exit teleport: " + e.Message); }
        }

        // unhide player body
        foreach (var rend in player.GetComponentsInChildren<Renderer>())
            rend.enabled = true;

        // Space while seated (the drift key) latches StarterAssets' jump, which only clears once airborne: drop it, or the
        // player hops the moment their legs come back.
        if (driverLookInput != null) driverLookInput.jump = false;

        // restore player components
        if (thirdPersonController != null) thirdPersonController.enabled = true;
        if (charController != null) charController.enabled = true;
        if (animator != null) animator.enabled = true;

        // swap camera priorities
        if (playerCamVCam != null) playerCamVCam.Priority = 11;
        if (truckCamVCam != null) truckCamVCam.Priority = 5;
        ResetDriverCamera();

        // update state
        if (playerController != null)
        {
            if (playerController.IsSpawned) playerController.IsDriving = false;
            if (playerController.currentTruck == this) playerController.currentTruck = null;
        }

        seated = false;
        player = null;
        playerController = null;
        thirdPersonController = null;
        charController = null;
        animator = null;
        playerCamVCam = null;
        driverLookInput = null;
#if ENABLE_INPUT_SYSTEM
        driverPlayerInput = null;
#endif

        TurnOffLights();
        TurnOffBrakeLights();
        ReleaseDriver();
        ApplyAllBrakes(parkingBrakeTorque);
        GameHUD.SetContextHint(null);

        // Remember where the truck is parked (scene flow re-spawns it here later).
        if (!despawning) FlowUtil.RecordTruck(this, gameObject.scene.name);

        if (!silent)
        {
            Sfx.Play("door", ejectPos);
            Fx.Dust(ejectPos + Vector3.up * 0.1f, 0.5f);
        }
    }

    /// <summary>Seat flag set but the player object is gone (despawned/destroyed): reset truck-side state.</summary>
    void CleanupDanglingSeat()
    {
        bool wasSeated = seated;
        seated = false;
        player = null;
        playerController = null;
        driverLookInput = null;
#if ENABLE_INPUT_SYSTEM
        driverPlayerInput = null;
#endif
        if (wasSeated)
        {
            if (truckCamVCam != null) truckCamVCam.Priority = 5;
            ResetDriverCamera();
            TurnOffLights();
            TurnOffBrakeLights();
            GameHUD.SetContextHint(null);
        }
        // The server may still think we're driving (e.g. our player object was replaced): free the seat.
        if (wasSeated || (IsSpawned && IsLocalDriver)) ReleaseDriver();
    }

    void ClaimDriver()
    {
        if (!IsSpawned || despawning) return;
        if (IsServer) ServerSetDriver(NetworkManager.LocalClientId);
        else RequestDriveServerRpc();
    }

    void ReleaseDriver()
    {
        if (!IsSpawned || despawning) return;
        if (IsServer) ServerClearDriver();
        else RequestEjectServerRpc();
    }

    // ---------------------------------------------------------------- safe eject

    // Truck body in metres relative to the root (root is scaled 0.67; measured renderer bounds):
    // x ±1.3, y 0.41..3.31 (root sits ~0.4 m below the ground), z -2.2 (rear) .. 3.8 (nose).
    const float BodyRearZ = -2.2f, BodyNoseZ = 3.8f, BodyHalfWidth = 1.3f, BodyTopY = 3.31f;

    /// <summary>World point at an offset in metres from the truck root (ignores the root's scale).</summary>
    Vector3 TruckPoint(Vector3 metres) => ShopModKit.TruckPoint(transform, metres);

    /// <summary>
    /// DriverEjectPosition if a player capsule fits there (on real ground, not through a wall); else the
    /// passenger side; else behind the hopper; else mid-truck on either side; else (derpy last resort) the roof.
    /// </summary>
    Vector3 FindSafeEjectPosition()
    {
        Vector3 eject = driverEjectPosition != null
            ? ShopModKit.ToTruckMetres(transform, driverEjectPosition.transform.position)
            : new Vector3(-1.61f, 0.85f, 3.04f);
        float sideX = Mathf.Max(Mathf.Abs(eject.x), BodyHalfWidth + 0.35f);
        float driverSign = eject.x <= 0f ? -1f : 1f;
        Vector3[] candidates =
        {
            eject,                                                    // driver door
            new Vector3(-eject.x, eject.y, eject.z),                  // passenger door
            new Vector3(0f, eject.y, BodyRearZ - 1.0f),               // behind the hopper
            new Vector3(driverSign * sideX, eject.y, 0.3f),           // driver side, mid-truck
            new Vector3(-driverSign * sideX, eject.y, 0.3f),          // passenger side, mid-truck
            new Vector3(0f, eject.y, BodyNoseZ + 1.0f),               // in front of the nose
        };
        foreach (var c in candidates)
        {
            if (TryEjectSpot(c, out Vector3 feet)) return feet;
        }
        Vector3 roof = TruckPoint(new Vector3(0f, BodyTopY + 0.05f, 0.8f));
        if (!IsCapsuleBlocked(roof)) return roof;
        return TruckPoint(eject);
    }

    bool TryEjectSpot(Vector3 metres, out Vector3 feet)
    {
        Vector3 p = TruckPoint(metres);
        if (!SnapToGround(p, out feet)) return false;   // nothing to stand on (outside the map / off a ledge)
        if (IsCapsuleBlocked(feet)) return false;        // something in the way right there
        // The way out from the cab must be clear too (no stepping through a wall the truck is parked against).
        Vector3 from = TruckPoint(new Vector3(0f, 0f, Mathf.Clamp(metres.z, BodyRearZ + 0.3f, BodyNoseZ - 0.3f)));
        from.y = feet.y + 1.0f;
        return !IsPathBlocked(from, feet + Vector3.up * 1.0f);
    }

    bool SnapToGround(Vector3 p, out Vector3 feet)
    {
        // Ignore the truck itself, bags/items (Interactable) and triggers; land on real ground.
        int mask = Physics.AllLayers & ~(1 << 2) & ~(1 << Carryable.InteractableLayer);
        var hits = Physics.RaycastAll(p + Vector3.up * 1.5f, Vector3.down, 4.5f, mask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        feet = p;
        foreach (var h in hits)
        {
            if (IsOwnOrPlayer(h.collider)) continue;
            if (h.distance < best) { best = h.distance; feet = h.point + Vector3.up * 0.05f; }
        }
        return best < float.MaxValue;
    }

    bool IsPathBlocked(Vector3 from, Vector3 to)
    {
        int mask = Physics.AllLayers & ~(1 << 2) & ~(1 << Carryable.InteractableLayer);
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.01f) return false;
        foreach (var h in Physics.RaycastAll(from, d / len, len, mask, QueryTriggerInteraction.Ignore))
            if (!IsOwnOrPlayer(h.collider)) return true;
        return false;
    }

    bool IsOwnOrPlayer(Collider c)
    {
        if (c == null) return true;
        if (c.transform.IsChildOf(transform)) return true;
        return player != null && c.transform.IsChildOf(player.transform);
    }

    readonly Collider[] overlapBuffer = new Collider[16];

    bool IsCapsuleBlocked(Vector3 feet)
    {
        float r = 0.28f, h = 1.8f;
        if (charController != null)
        {
            // radius/height are local: scale by the player's root scale (Chuck is 0.8).
            Vector3 s = charController.transform.lossyScale;
            r = charController.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            h = charController.height * Mathf.Abs(s.y);
        }
        Vector3 a = feet + Vector3.up * (r + 0.1f);
        Vector3 b = feet + Vector3.up * Mathf.Max(r + 0.1f, h - r);
        int mask = Physics.AllLayers & ~(1 << 2);
        int n = Physics.OverlapCapsuleNonAlloc(a, b, r, overlapBuffer, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = overlapBuffer[i];
            if (col == null) continue;
            if (player != null && col.transform.IsChildOf(player.transform)) continue;
            return true; // includes this truck's own body: the spot must be clear of it
        }
        return false;
    }

    // ---------------------------------------------------------------- lifecycle

    void Awake()
    {
        truckCamVCam = GetComponentInChildren<Cinemachine.CinemachineVirtualCamera>();
        if (truckCamVCam != null)
        {
            truckCamTarget = truckCamVCam.Follow;
            truckCamFollow = truckCamVCam.GetCinemachineComponent<Cinemachine.CinemachineTransposer>();
            if (truckCamFollow == null)
                truckCamFollow = truckCamVCam.GetComponentInChildren<Cinemachine.CinemachineTransposer>(true);
            if (truckCamTarget != null) truckCamRestRotation = truckCamTarget.localRotation;
            if (truckCamFollow != null) truckCamRestOffset = truckCamFollow.m_FollowOffset;
        }
        networkObject = GetComponent<NetworkObject>();
        if (networkObject == null)
        {
            Debug.LogError("TruckControllerV2 requires a NetworkObject component.");
        }
    }
    void Start()
    {
        rigidBody = GetComponent<Rigidbody>();
        TruckCabVisuals.Ensure(this);
        TruckImpact.Ensure(this);

        // adjust CoM
        rigidBody.ResetCenterOfMass(); // a model may already have installed its smaller collider set before Start
        var com = rigidBody.centerOfMass;
        com.y += centreOfGravityOffset;
        rigidBody.centerOfMass = com;

        wheels = GetComponentsInChildren<WheelControl>();
    }

    public override void OnNetworkSpawn()
    {
        despawning = false;
        base.OnNetworkSpawn();
    }

    public override void OnNetworkDespawn()
    {
        // The truck is going away (scene unload / despawn) with someone still inside: put them back on their feet.
        despawning = true;
        handback.Cancel();
        if (IsLocalPlayerSeated)
        {
            Debug.LogWarning("[TruckControllerV2] Despawned with the local player seated; unseating.");
            var seatedPlayer = player;
            ExitInternal(true);
            // Don't let the player be destroyed along with this truck's scene (NGO keeps players in DDOL
            // across loads; PlayerSpawner respawns them in the next scene anyway).
            if (seatedPlayer != null && seatedPlayer.transform.parent == null && seatedPlayer.scene == gameObject.scene)
                DontDestroyOnLoad(seatedPlayer);
        }
        base.OnNetworkDespawn();
    }

    void Update()
    {
        if (handback.Active) ServerHandbackTick();
        if (seated && player == null)
        {
            CleanupDanglingSeat();
            return;
        }
        if (!IsLocalPlayerSeated) return;
        if ((PlayerController.IsUIBlocking && !FullMap.DrivingAllowed) || SceneController.IsTransitioning) return;

        if (GameControls.Down(hornKey, GameControls.Action.Horn) && Time.time >= nextHornTime)
        {
            nextHornTime = Time.time + 0.3f;
            Honk();
        }
    }

    void LateUpdate()
    {
        if (!IsLocalPlayerSeated || truckCamTarget == null || truckCamFollow == null || driverLookInput == null) return;
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning || Cursor.lockState != CursorLockMode.Locked) return;

        Vector2 look = driverLookInput.look;
        float dt = Time.unscaledDeltaTime;
        bool mouse = true;
#if ENABLE_INPUT_SYSTEM
        if (driverPlayerInput != null && driverPlayerInput.currentControlScheme == "Gamepad" && Gamepad.current != null)
        {
            mouse = false;
            look = Gamepad.current.rightStick.ReadValue() * GameSettings.LookSensitivity;
            if (GameSettings.InvertY) look.y = -look.y;
        }
#endif
        if (look.sqrMagnitude > 0.0001f)
        {
            driverCamYaw = Mathf.DeltaAngle(0f, driverCamYaw + look.x * (mouse ? 1f : DriverCamStickYawPerSecond * dt));
            driverCamHeight = Mathf.Clamp(driverCamHeight + look.y *
                (mouse ? DriverCamMouseHeight : DriverCamStickHeightPerSecond * dt), DriverCamMinHeight, DriverCamMaxHeight);
            lastDriverLookAt = Time.unscaledTime;
        }
        else if (Time.unscaledTime - lastDriverLookAt > DriverCamRecenterDelay)
        {
            driverCamYaw = Mathf.MoveTowardsAngle(driverCamYaw, 0f, DriverCamRecenterDegreesPerSecond * dt);
        }

        // The transposer's WorldUp binding uses target yaw for horizontal orbit and its height for vertical orbit.
        // Neither pivot nor offset is networked, and the truck's steering still reads its own movement axes.
        truckCamTarget.localRotation = truckCamRestRotation * Quaternion.Euler(0f, driverCamYaw, 0f);
        Vector3 offset = truckCamRestOffset;
        offset.y = driverCamHeight;
        truckCamFollow.m_FollowOffset = offset;
    }

    void ResetDriverCamera()
    {
        driverCamYaw = 0f;
        driverCamHeight = truckCamRestOffset.y;
        if (truckCamTarget != null) truckCamTarget.localRotation = truckCamRestRotation;
        if (truckCamFollow != null) truckCamFollow.m_FollowOffset = truckCamRestOffset;
    }

    void Honk()
    {
        Vector3 front = TruckPoint(new Vector3(0f, 1.8f, BodyNoseZ));
        Sfx.Play("horn", front, 1f, Random.Range(0.9f, 1.15f));
        GameHUD.FloatingText(TruckPoint(new Vector3(0f, BodyTopY + 0.7f, 2.8f)), Random.value < 0.15f ? "HONK?!" : "HONK!", new Color(1f, 0.92f, 0.3f), 1.3f);
        Juice.Shake(0.05f, 0.1f);
        var honked = Honked;
        if (honked != null)
        {
            foreach (var d in honked.GetInvocationList())
                try { ((System.Action<TruckControllerV2>)d)(this); } catch (System.Exception e) { Debug.LogException(e); }
        }
    }

    void FixedUpdate()
    {
        if (wheels == null || rigidBody == null) return;

        // Parked: hold a handbrake on the authority side so it never rolls away. That includes a guest who has just got
        // out: they keep simulating the truck until it stands still, then the host takes it back (ServerHandbackTick).
        if (!AnyDriver)
        {
            if (!IsSpawned || IsServer || IsOwner) ApplyAllBrakes(parkingBrakeTorque * TruckImpact.ParkingBrakeScale(this));
            return;
        }

        if (!LocalDrives) return;

        // Laptop / menus / scene fade: ignore drive input and hold the truck.
        // Laptop / menus / scene fade: ignore drive input and hold the truck. The full map alone does not: you can drive with it open.
        if ((PlayerController.IsUIBlocking && InputOverride == null && !FullMap.DrivingAllowed) || SceneController.IsTransitioning)
        {
            ApplyAllBrakes(brakeTorque);
            return;
        }

        Vector2 driveInput = GameControls.DriveAxes();
        float vInput = driveInput.y;
        float hInput = driveInput.x;
        if (InputOverride != null)
        {
            Vector2 o = InputOverride();
            hInput = Mathf.Clamp(o.x, -1f, 1f);
            vInput = Mathf.Clamp(o.y, -1f, 1f);
        }

        float forwardSpeed = Vector3.Dot(transform.forward, rigidBody.linearVelocity);
        float speedFactor = Mathf.InverseLerp(0, maxSpeed, Mathf.Abs(forwardSpeed));

        float currentMotorTorque = Mathf.Lerp(motorTorque, 0, speedFactor);
        float currentSteerRange = Mathf.Lerp(steeringRange, steeringRangeAtMaxSpeed, speedFactor);
        // Additive cornering clamp (Progression §6.1): δ = h·min(range, atan(L·aLatMax/v²)). No-op while aLatMax is infinite.
        if (!float.IsPositiveInfinity(maxLateralAccel) && maxLateralAccel > 0f)
        {
            if (!vehicleSubstepsSet)
            {
                vehicleSubstepsSet = true;
                var wc = GetComponentInChildren<WheelCollider>();
                if (wc != null) wc.ConfigureVehicleSubsteps(5f, 12, 15);
            }
            float v2 = forwardSpeed * forwardSpeed;
            if (v2 > 0.01f)
                currentSteerRange = Mathf.Min(currentSteerRange, Mathf.Atan(LateralWheelbase * TruckModelVisuals.ScaleFor(transform).z * maxLateralAccel / v2) * Mathf.Rad2Deg);
        }

        bool neutral = Mathf.Abs(vInput) <= TruckCoastRules.InputDeadZone;
        if (neutral) vInput = 0f;
        bool isStopped = Mathf.Abs(forwardSpeed) < 0.1f;
        bool sameDir = Mathf.Sign(vInput) == Mathf.Sign(forwardSpeed);
        bool isAccelerating = isStopped || sameDir;

        foreach (var wheel in wheels)
        {
            if (wheel == null || wheel.WheelCollider == null || !wheel.WheelCollider.enabled) continue;
            if (wheel.steerable)
                wheel.WheelCollider.steerAngle = hInput * currentSteerRange;

            if (isAccelerating)
            {
                wheel.WheelCollider.motorTorque = vInput * currentMotorTorque;
                wheel.WheelCollider.brakeTorque = 0f;
            }
            else
            {
                wheel.WheelCollider.motorTorque = 0f;
                wheel.WheelCollider.brakeTorque = Mathf.Abs(vInput) * brakeTorque;
            }
        }

        if (neutral && !rigidBody.isKinematic)
        {
            bool grounded = false;
            foreach (var wheel in wheels)
                if (wheel != null && wheel.WheelCollider != null && wheel.WheelCollider.enabled && wheel.WheelCollider.isGrounded) { grounded = true; break; }
            Vector3 planar = Vector3.ProjectOnPlane(rigidBody.linearVelocity, Vector3.up);
            float loss = TruckCoastRules.SpeedLoss(planar.magnitude, vInput, grounded, Time.fixedDeltaTime);
            if (loss > 0f) rigidBody.AddForce(-planar.normalized * loss, ForceMode.VelocityChange);
        }

        // if im braking, turn on rear lights
        if (!isAccelerating && vInput < 0)
        {
            TurnOnBrakeLights();
        }
        else
        {
            TurnOffBrakeLights();
        }

        // Reverse beeper: beep... beep... beep...
        if (forwardSpeed < -reverseBeepSpeed && Time.time >= nextBeepTime)
        {
            nextBeepTime = Time.time + reverseBeepInterval;
            Sfx.Play("beep", TruckPoint(new Vector3(0f, 2.0f, BodyRearZ)), 0.7f);
        }
    }

    void ApplyAllBrakes(float torque)
    {
        if (wheels == null) return;
        foreach (var wheel in wheels)
        {
            if (wheel == null || wheel.WheelCollider == null || !wheel.WheelCollider.enabled) continue;
            wheel.WheelCollider.motorTorque = 0f;
            wheel.WheelCollider.brakeTorque = torque;
        }
    }

    // called by PlayerController when you hit “enter truck”
    public void RequestEnter()
    {
        ClaimDriver();
        TurnOnHeadlights();
    }

    // called by PlayerController when you hit “exit truck”
    public void RequestExit()
    {
        ReleaseDriver();
        TurnOffLights();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestDriveServerRpc(RpcParams rpcParams = default)
    {
        ServerSetDriver(rpcParams.Receive.SenderClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestEjectServerRpc(RpcParams rpcParams = default)
    {
        // Only the current driver can give up the seat.
        if (driverClientId.Value != rpcParams.Receive.SenderClientId) return;
        ServerClearDriver();
    }

    void ServerSetDriver(ulong clientId)
    {
        // Only claim if nobody’s driving
        if (driverClientId.Value != ulong.MaxValue) return;
        // Depot Row: only the business (owner, or an on-shift employee through Permissions.DriverHook, N4) may drive its truck.
        if (!Permissions.CanDrive(clientId, this)) return;

        driverClientId.Value = clientId;
        // A guest who gets back in before the host took the truck back still owns it: then nothing changes hands at all.
        handback.Cancel();

        if (networkObject != null && networkObject.OwnerClientId != clientId)
            networkObject.ChangeOwnership(clientId);
    }

    void ServerClearDriver()
    {
        if (driverClientId.Value == ulong.MaxValue) return;  // nobody to eject

        driverClientId.Value = ulong.MaxValue;  // back to “no driver”
        // brake on eject
        ApplyAllBrakes(parkingBrakeTorque);

        // A guest who got out keeps simulating the truck (their machine parks it) until it stands still; only then does the
        // host take it back (ServerHandbackTick, Docs/Multiplayer.md §4.4). Taking it here restarted the physics on the
        // host's trailing copy, from a standstill and with the guest's player capsule still inside it: the truck jumped and
        // bounced, and the guest's copy snapped to wherever the host's ended up.
        if (networkObject != null && networkObject.OwnerClientId != NetworkManager.ServerClientId)
            handback.Begin(Time.unscaledTime, transform.position, transform.rotation);
    }

    readonly TruckHandbackWatch handback = new TruckHandbackWatch();

    /// <summary>Host: take the truck back from the guest who got out, once this copy of it has held still (or after a while).</summary>
    void ServerHandbackTick()
    {
        if (!IsSpawned || !IsServer || HasDriver || networkObject == null || networkObject.OwnerClientId == NetworkManager.ServerClientId)
        {
            handback.Cancel();   // someone got in, or the guest left the game (NGO already gave the truck to the host)
            return;
        }
        if (!handback.Tick(Time.unscaledTime, transform.position, transform.rotation)) return;
        ServerTakeBack();
        // It stands still on the guest's machine: start the host's physics at rest too.
        if (rigidBody != null && !rigidBody.isKinematic)
        {
            rigidBody.linearVelocity = Vector3.zero;
            rigidBody.angularVelocity = Vector3.zero;
        }
    }

    void ServerTakeBack()
    {
        handback.Cancel();
        if (networkObject != null && networkObject.OwnerClientId != NetworkManager.ServerClientId)
            networkObject.ChangeOwnership(NetworkManager.ServerClientId);
    }

    /// <summary>
    /// Host: free the seat no matter who holds it (the driver left the game, or Depot Row parks the truck). The truck goes
    /// back to the host, braked, straight away (the callers move or despawn it next).
    /// </summary>
    public void ServerForceReleaseDriver()
    {
        if (!IsSpawned || !IsServer) return;
        if (IsLocalPlayerSeated) { ForceExit(); }
        if (driverClientId.Value != ulong.MaxValue) ServerClearDriver();
        ServerTakeBack();
    }

    public Vector3 GetDriverEjectPosition()
    {
        return driverEjectPosition.transform.localPosition;
    }

    void TurnOnHeadlights()
    {
        if (headlights == null) return;
        foreach (var light in headlights)
        {
            if (light != null) light.enabled = true;
        }
    }

    void TurnOffLights()
    {
        if (headlights == null) return;
        foreach (var light in headlights)
        {
            if (light != null) light.enabled = false;
        }
    }

    void TurnOnBrakeLights()
    {
        if (brakeLights == null) return;
        foreach (var light in brakeLights)
        {
            if (light != null) light.enabled = true;
        }
    }

    void TurnOffBrakeLights()
    {
        if (brakeLights == null) return;
        foreach (var light in brakeLights)
        {
            if (light != null) light.enabled = false;
        }
    }

}

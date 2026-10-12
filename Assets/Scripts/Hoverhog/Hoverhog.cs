using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The HOVERHOG H-1 (Docs/Progression.md §6.10): a business's second vehicle, a heavy garbage drone that lives on the
/// depot's rooftop helipad. Built at runtime on every machine, one per lot that owns it (like the walk-up hatch); its
/// truth is <see cref="Hogs"/> / <see cref="HogNet"/>, its body is <see cref="HogModel"/>.
/// <list type="bullet">
/// <item><b>Flying</b> is simulated by the pilot's machine (arcade hover, no rigidbody). The pilot's player rides in the
/// cockpit, so everybody else draws the Hoverhog at that player.</item>
/// <item><b>The Claw</b> (X), <b>the beam</b> that replaces it (hold X), <b>the bomb bay</b> (G) and <b>the reactor</b>
/// (hold F) are decided by the host. A collected curb bag is paid exactly like a bag tossed in a truck
/// (<see cref="TruckHopper.CollectCurbBag"/>) and joins the belly, not the truck.</item>
/// <item><b>Battery</b> drains in the air and charges on a pad the business owns. Empty, it sinks where it is.</item>
/// </list>
/// </summary>
[DisallowMultipleComponent]
public class Hoverhog : MonoBehaviour, IInteractable
{
    const float SeatUp = 1.2f;
    const KeyCode ClawKey = KeyCode.X, DumpKey = KeyCode.G, TowKey = KeyCode.R, BurnKey = KeyCode.F, BoostKey = KeyCode.LeftShift;
    static int FlyMask => (1 << SortingKit.GroundLayer) | (1 << SortingKit.WallsLayer) | 1;

    static readonly Hoverhog[] byLot = new Hoverhog[DepotRowLayout.LotCount];
    public static Hoverhog ForLot(int lot) => lot >= 0 && lot < byLot.Length ? byLot[lot] : null;
    /// <summary>The Hoverhog this machine's player is flying, or null.</summary>
    public static Hoverhog LocalSeat { get; private set; }
    /// <summary>Bots and probes fly with this instead of the keyboard: x turn, y throttle, z climb (each −1..1).</summary>
    public static System.Func<Vector3> PilotInputOverride;
    /// <summary>Bots and probes hold pilot keys with this: <see cref="Hogs.PilotBeam"/>, <see cref="Hogs.PilotBoosting"/>.</summary>
    public static System.Func<byte> PilotKeysOverride;

    int lot;
    public int Lot => lot;
    HogData D => Hogs.Of(lot);
    HogLevels levels;

    HogModel model;
    Transform camPivot;

    // ---- the local pilot
    PlayerController pilot;
    ThirdPersonController pilotMotor;
    CharacterController pilotBody;
    Animator pilotAnimator;
    StarterAssetsInputs pilotLook;
    CinemachineVirtualCamera vcam;
    CinemachineTransposer vcamFollow;
    Vector3 velocity;
    float yaw, camYaw, camHeight = 7f, nextReport, nextHint, dumpHeld, towHold, lastLandToast, seatedAt;
    float boostUntil = -100f, boostReadyAt, nextBurn;
    bool landed = true, deadToastShown, dumpSent;
    byte sentFlags;

    // ---- claw and beam (presentation on every machine; the bags themselves only on the host)
    float clawStart = -1f, clawSeconds, shownLoad = -1f;
    Vector3 clawTarget;
    Coroutine clawRoutine, dumpRoutine;
    readonly List<TrashBag> heldBags = new List<TrashBag>();
    readonly HashSet<Carryable> floating = new HashSet<Carryable>();
    int clawCount;
    float nextBeamAt, lastBeamToast;
    Vector3 lastCircle = new Vector3(0f, -999f, 0f);

    // =====================================================================
    // Lifecycle: one per lot that owns the upgrade
    // =====================================================================

    /// <summary>Every machine, about twice a second: build / show / hide each lot's Hoverhog from the replicated truth.</summary>
    public static void Sync()
    {
        for (int i = 0; i < byLot.Length; i++)
        {
            var depot = DepotRowScene.Lot(i);
            bool want = depot != null && depot.facade != null && depot.facade.gameObject.activeInHierarchy && Hogs.Level(i) > 0;
            var hog = byLot[i];
            if (hog == null)
            {
                if (!want) continue;
                if (Hogs.IsHost) Hogs.EnsurePlaced(i);
                if (!Hogs.Of(i).placed) continue; // a guest waits for the host's pose
                hog = byLot[i] = Build(i, depot);
            }
            bool show = want || hog == LocalSeat;
            if (hog.gameObject.activeSelf != show) hog.gameObject.SetActive(show);
            if (show) hog.RefitIfChanged();
        }
    }

    static Hoverhog Build(int lot, DepotLot depot)
    {
        var go = new GameObject($"Hoverhog (lot {lot})");
        go.transform.SetParent(depot.transform, true);
        var d = Hogs.Of(lot);
        go.transform.SetPositionAndRotation(d.pos, Quaternion.Euler(0f, d.yaw, 0f));
        var hog = go.AddComponent<Hoverhog>();
        hog.lot = lot;
        hog.yaw = d.yaw;
        hog.camPivot = ShopModKit.Pivot("Camera pivot", go.transform, new Vector3(0f, 2.3f, 0f));
        hog.levels = Hogs.LevelsOf(lot);
        hog.model = HogModel.Build(go.transform, hog.levels);

        var body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        var solid = new GameObject("Body collider");
        solid.transform.SetParent(go.transform, false);
        var box = solid.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 1.7f, 0f);
        box.size = new Vector3(2.4f, 2.9f, 4f);
        var reach = new GameObject("Board trigger") { layer = Carryable.InteractableLayer };
        reach.transform.SetParent(go.transform, false);
        var trigger = reach.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 1.5f, 0f);
        trigger.size = new Vector3(5.6f, 3.2f, 6.4f);
        return hog;
    }

    /// <summary>The business bought (or lost) an upgrade: bolt the new parts on.</summary>
    void RefitIfChanged()
    {
        var now = Hogs.LevelsOf(lot);
        if (now.Equals(levels) && model != null) return;
        if (clawRoutine != null || heldBags.Count > 0) return; // not mid-grab: the next sync refits
        bool grew = model != null;
        levels = now;
        model?.Destroy();
        model = HogModel.Build(transform, levels);
        if (grew && Application.isPlaying)
        {
            Fx.Dust(transform.position + Vector3.up * 2f, 1.6f);
            Sfx.Play("slam", transform.position, 0.5f, 1.3f);
        }
    }

    void OnDestroy()
    {
        if (LocalSeat == this) LeaveSeat(false);
        if (ForLot(lot) == this) byLot[lot] = null;
        if (vcam != null) Destroy(vcam.gameObject);
        model?.Destroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        for (int i = 0; i < byLot.Length; i++) byLot[i] = null;
        LocalSeat = null;
        PilotInputOverride = null;
        PilotKeysOverride = null;
        leftSeatAt = -10f;
        circles.Clear();
    }

    // =====================================================================
    // Boarding (IInteractable). While seated the pilot's PlayerController is switched off, so its E/F scan cannot
    // find doors and bags under the Hoverhog; E to hop out is read here (PilotKeys).
    // =====================================================================

    static float leftSeatAt = -10f;

    public bool CanInteract()
    {
        if (LocalSeat == this || Time.time - leftSeatAt < 0.4f) return false;
        var me = PlayerController.Local; // coop:allow(local) the board prompt is for this machine's own player
        return LocalSeat == null && me != null && !me.IsDriving && D.pilot == Hogs.Nobody;
    }

    public string GetInteractionPrompt() => Hogs.MayFly(Hogs.LocalClient, lot) ? "Fly the Hoverhog" : "Not your Hoverhog";

    public void Highlight(bool highlight) { }

    public void OnInteract(GameObject interactor)
    {
        var pc = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (pc == null || !pc.IsOwner) return;
        if (!CanInteract()) return;
        if (!Hogs.MayFly(Hogs.LocalClient, lot)) { Permissions.Deny(transform.position, "Not your Hoverhog!"); return; }
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying) { GameHUD.Toast("Hands full! Drop it first (G)", ToastStyle.Bad, 2f); return; }
        TakeSeat(pc);
    }

    void TakeSeat(PlayerController pc)
    {
        pilot = pc;
        pilotMotor = pc.GetComponent<ThirdPersonController>();
        pilotBody = pc.GetComponent<CharacterController>();
        pilotAnimator = pc.GetComponent<Animator>();
        pilotLook = pc.GetComponent<StarterAssetsInputs>();
        if (pilotLook != null) pilotLook.look = Vector2.zero;
        if (pilotMotor != null) pilotMotor.enabled = false;
        if (pilotBody != null) pilotBody.enabled = false;
        if (pilotAnimator != null) pilotAnimator.enabled = false;
        foreach (var r in pc.GetComponentsInChildren<Renderer>()) r.enabled = false;
        if (pc.IsSpawned) pc.IsDriving = true;
        pc.enabled = false;
        if (TooltipUI.Instance != null) TooltipUI.Instance.Hide();

        var d = D;
        yaw = transform.eulerAngles.y;
        velocity = Vector3.zero;
        landed = true;
        deadToastShown = false;
        camYaw = 0f;
        camHeight = 7f;
        seatedAt = Time.time;
        sentFlags = 0;
        dumpHeld = 0f;
        d.pilot = Hogs.LocalClient;
        LocalSeat = this;
        Hogs.Claim(lot);
        EnsureCamera();
        vcam.Priority = 20;
        SeatPilot();

        Sfx.Play("door", transform.position);
        Sfx.Play("engine_start", transform.position, 0.8f, 1.5f);
        GameHUD.FloatingText(transform.position + Vector3.up * 4.2f, "OINK!", new Color(1f, 0.6f, 0.75f), 1.3f);
        if (d.battery <= HogTuning.TowBelow)
            GameHUD.Toast("The Hoverhog's battery is flat. It charges on your helipad.", ToastStyle.Bad, 3f);
    }

    public void TryHopOut()
    {
        if (!landed)
        {
            if (Time.time - lastLandToast > 2f)
            {
                lastLandToast = Time.time;
                GameHUD.Toast("Land first! (hold Ctrl to come down)", ToastStyle.Info, 2f);
            }
            return;
        }
        LeaveSeat(true);
    }

    /// <summary>Put the local pilot back on their feet beside the Hoverhog and free the seat.</summary>
    void LeaveSeat(bool feedback)
    {
        var pc = pilot;
        var d = D;
        if (LocalSeat == this) LocalSeat = null;
        if (vcam != null) vcam.Priority = 0;
        if (d != null)
        {
            d.pos = transform.position;
            d.yaw = yaw;
            d.boosting = false;
            if (d.pilot == Hogs.LocalClient)
            {
                Hogs.Report(lot, d.pos, d.yaw, d.battery, 0, true);
                d.pilot = Hogs.Nobody;
            }
        }
        pilot = null;
        GameHUD.SetContextHint(null);
        if (TooltipUI.Instance != null) TooltipUI.Instance.Hide();
        if (pc == null) return;
        pc.enabled = true;
        leftSeatAt = Time.time;

        Vector3 side = transform.position + transform.right * 2.9f;
        if (Physics.Raycast(side + Vector3.up * 2.5f, Vector3.down, out var hit, 8f, FlyMask, QueryTriggerInteraction.Ignore))
            side.y = hit.point.y + 0.05f;
        else side = transform.position + Vector3.up * 3.4f; // nothing to stand on beside it: out through the roof
        if (pilotMotor != null) pilotMotor.enabled = true;
        if (pilotBody != null) pilotBody.enabled = true;
        if (pilotAnimator != null) pilotAnimator.enabled = true;
        foreach (var r in pc.GetComponentsInChildren<Renderer>()) r.enabled = true;
        if (pc.IsSpawned) pc.IsDriving = false;
        DepotRowWorld.TeleportPlayer(pc, side, Quaternion.Euler(0f, yaw, 0f));
        pilotMotor = null; pilotBody = null; pilotAnimator = null; pilotLook = null;
        if (feedback)
        {
            Sfx.Play("door", side);
            Fx.Dust(side + Vector3.up * 0.1f, 0.5f);
        }
    }

    void SeatPilot()
    {
        if (pilot != null) pilot.transform.SetPositionAndRotation(transform.position + Vector3.up * SeatUp, Quaternion.Euler(0f, yaw, 0f));
    }

    void EnsureCamera()
    {
        if (vcam != null) return;
        var go = new GameObject("Hoverhog camera");
        vcam = go.AddComponent<CinemachineVirtualCamera>();
        vcam.Priority = 0;
        vcam.Follow = camPivot;
        vcam.LookAt = camPivot;
        vcam.m_Lens.FieldOfView = 55f;
        vcamFollow = vcam.AddCinemachineComponent<CinemachineTransposer>();
        vcamFollow.m_BindingMode = CinemachineTransposer.BindingMode.LockToTargetWithWorldUp;
        vcamFollow.m_FollowOffset = new Vector3(0f, camHeight, -14f);
        vcamFollow.m_XDamping = 0.4f; vcamFollow.m_YDamping = 0.4f; vcamFollow.m_ZDamping = 0.4f;
        vcamFollow.m_YawDamping = 1.2f;
        vcam.AddCinemachineComponent<CinemachineHardLookAt>();
        WorldCameraOcclusion.Ensure(vcam, WorldCameraRuntime.HoverhogMin, transform);
    }

    // =====================================================================
    // Update
    // =====================================================================

    void Update()
    {
        var d = D;
        if (d == null) return;
        float dt = Time.deltaTime;
        if (LocalSeat == this)
        {
            if (pilot == null) { LeaveSeat(false); return; }
            // The host gave the seat to somebody else (or refused): out, once it has had time to answer.
            if (d.pilot != Hogs.LocalClient && Time.time - seatedAt > 1.6f) { LeaveSeat(true); return; }
            Fly(d, dt);
        }
        else Follow(d, dt);
        if (Hogs.IsHost) HostBeam(d);
        Present(d, dt);
    }

    void LateUpdate()
    {
        for (int i = heldBags.Count - 1; i >= 0; i--)
        {
            var bag = heldBags[i];
            if (bag == null || bag.State == CarryState.Consumed) { heldBags.RemoveAt(i); continue; }
            bag.transform.position = model.ClawTip + Quaternion.Euler(0f, i * 120f, 0f) * new Vector3(heldBags.Count > 1 ? 0.35f : 0f, -0.1f, 0f);
        }
        if (LocalSeat != this || pilotLook == null || vcamFollow == null) return;
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning || Cursor.lockState != CursorLockMode.Locked) return;
        Vector2 look = pilotLook.look;
        if (look.sqrMagnitude < 0.0001f && Gamepad.current != null)
            look = Gamepad.current.rightStick.ReadValue() * (140f * Time.unscaledDeltaTime);
        if (look.sqrMagnitude > 0.0001f)
        {
            camYaw = Mathf.DeltaAngle(0f, camYaw + look.x);
            camHeight = Mathf.Clamp(camHeight + look.y * 0.05f, 2f, 20f);
        }
        camPivot.localRotation = Quaternion.Euler(0f, camYaw, 0f);
        vcamFollow.m_FollowOffset = new Vector3(0f, camHeight, -14f);
    }

    /// <summary>Everybody but the pilot: at the pilot's player while flown, at its parked pose otherwise. On the host
    /// an abandoned Hoverhog that is still in the air comes down by itself.</summary>
    void Follow(HogData d, float dt)
    {
        Vector3 pos = d.pos;
        float y = d.yaw;
        bool flown = false;
        if (d.pilot != Hogs.Nobody)
        {
            var tells = PlayerTells.For(d.pilot);
            if (tells != null && tells.Player != null)
            {
                pos = tells.Player.transform.position - Vector3.up * SeatUp;
                y = tells.Player.transform.eulerAngles.y;
                flown = true;
            }
        }
        else if (Hogs.IsHost)
        {
            // Nobody aboard and not on its skids (a pilot dropped out of the session mid-air): it settles by itself.
            float height = Height(d.pos, out float groundY);
            if (!float.IsInfinity(height) && Mathf.Abs(height) > 0.005f)
            {
                d.pos = new Vector3(d.pos.x, height < 0f ? groundY : Mathf.Max(groundY, d.pos.y - HogTuning.DeadSink * dt), d.pos.z);
                pos = d.pos;
            }
        }
        float k = 1f - Mathf.Exp(-14f * dt);
        Vector3 before = transform.position;
        transform.SetPositionAndRotation(Vector3.Lerp(before, pos, k),
            Quaternion.Euler(0f, Mathf.LerpAngle(transform.eulerAngles.y, y, k), 0f));
        velocity = dt > 0f ? (transform.position - before) / dt : Vector3.zero;
        landed = !flown || Height(transform.position, out _) < 0.15f;
        yaw = transform.eulerAngles.y;
    }

    // =====================================================================
    // Flying (the pilot's machine)
    // =====================================================================

    void Fly(HogData d, float dt)
    {
        bool blocked = (PlayerController.IsUIBlocking && !FullMap.DrivingAllowed) || SceneController.IsTransitioning;
        Vector2 axes = blocked ? Vector2.zero : GameControls.DriveAxes();
        float up = 0f;
        if (!blocked)
        {
            if (GameControls.Held(KeyCode.Space, GameControls.Action.Jump)) up += 1f;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C) || GameControls.PadHeld(GameControls.Action.Flop)) up -= 1f;
        }
        if (PilotInputOverride != null)
        {
            Vector3 o = PilotInputOverride();
            axes = new Vector2(Mathf.Clamp(o.x, -1f, 1f), Mathf.Clamp(o.y, -1f, 1f));
            up = Mathf.Clamp(o.z, -1f, 1f);
        }
        byte keys = PilotKeysOverride != null ? PilotKeysOverride() : (byte)0;
        bool dead = d.battery <= 0f;
        if (dead) { up = landed ? 0f : -HogTuning.DeadSink / HogTuning.Climb; axes *= 0.35f; }

        // Rotors 3: the jets
        bool boostKey = !blocked && (GameControls.Held(BoostKey, GameControls.Action.Shove) || (keys & Hogs.PilotBoosting) != 0);
        if (HogTuning.HasBoost(levels.rotors) && boostKey && !landed && !dead && Time.time >= boostReadyAt && Time.time > boostUntil)
        {
            boostUntil = Time.time + HogTuning.BoostSeconds;
            boostReadyAt = boostUntil + HogTuning.BoostCooldown;
            Sfx.Play("engine_start", transform.position, 1f, 2f);
            Juice.Shake(0.15f, 0.3f);
            GameHUD.FloatingText(transform.position + Vector3.up * 4f, "FWOOSH!", new Color(1f, 0.6f, 0.2f), 1.3f);
        }
        bool boosting = Time.time < boostUntil && !landed && !dead;
        d.boosting = boosting;
        float top = HogTuning.SpeedAt(levels.rotors) + (boosting ? HogTuning.BoostSpeed : 0f);

        if (landed && up <= 0f)
        {
            // On its skids: it turns on the spot and goes nowhere until it lifts off.
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, HogTuning.Accel * 3f * dt);
            yaw += axes.x * HogTuning.TurnRate * 0.5f * dt;
        }
        else
        {
            yaw += axes.x * HogTuning.TurnRate * dt;
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            float throttle = boosting ? Mathf.Max(axes.y, 0.6f) : axes.y;
            Vector3 want = fwd * (throttle >= 0f ? throttle * top : throttle * HogTuning.ReverseSpeed);
            want.y = up * HogTuning.Climb;
            // It stops faster than it gets going, so letting go over a curb leaves it over that curb.
            float rate = want.sqrMagnitude < velocity.sqrMagnitude ? HogTuning.Brake : HogTuning.Accel * (boosting ? 2f : 1f);
            velocity = Vector3.MoveTowards(velocity, want, rate * dt);
        }

        Vector3 pos = Move(transform.position, velocity * dt);
        var town = CoastalCityLayout.BoundsXZ;
        pos.x = Mathf.Clamp(pos.x, town.xMin + 4f, town.xMax - 4f);
        pos.z = Mathf.Clamp(pos.z, town.yMin + 4f, town.yMax - 4f);
        float ceiling = HogTuning.CeilingAt(levels.rotors) + TownRelief.Height(pos.x, pos.z);      // above the land, not above sea level
        if (pos.y > ceiling) { pos.y = ceiling; velocity.y = Mathf.Min(0f, velocity.y); }
        float height = Height(pos, out float groundY);
        if (height <= 0.12f && up <= 0f)
        {
            // Skids down.
            pos.y = groundY;
            if (!landed && velocity.y < -1.5f) { Fx.Dust(pos, 1.2f); Sfx.Play("slam", pos, 0.5f, 0.8f); Juice.Shake(0.1f, 0.15f); }
            velocity.y = 0f;
            landed = true;
        }
        else
        {
            if (height < 0f) pos.y = groundY;
            landed = false;
        }
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        SeatPilot();

        bool onPad = landed && Hogs.OnChargingPad(lot, pos);
        float before = d.battery;
        d.battery = HogRules.BatteryAfter(d.battery, dt, !landed, onPad, Hogs.FlightSeconds(lot));
        d.pos = pos;
        d.yaw = yaw;
        if (before > 0f && d.battery <= 0f && !deadToastShown)
        {
            deadToastShown = true;
            GameHUD.Toast("Battery's flat! The Hoverhog is coming down. Wherever this is.", ToastStyle.Bad, 3.5f);
            Sfx.Play("buzz", pos, 0.8f, 0.7f);
        }
        if (d.battery > 0.05f) deadToastShown = false;

        byte flags = (byte)(boosting ? Hogs.PilotBoosting : 0);
        if (!blocked) flags |= PilotKeys(d, onPad, keys);
        if (Time.unscaledTime >= nextReport || flags != sentFlags)
        {
            nextReport = Time.unscaledTime + 0.5f;
            sentFlags = flags;
            Hogs.Report(lot, pos, yaw, d.battery, flags, false);
        }
        if (Time.unscaledTime >= nextHint)
        {
            nextHint = Time.unscaledTime + 0.25f;
            GameHUD.SetContextHint(Hint(d, onPad));
        }
    }

    /// <summary>The pilot's keys this frame. Returns the held-key flags the host needs (the beam).</summary>
    byte PilotKeys(HogData d, bool onPad, byte overrideKeys)
    {
        byte held = 0;
        if (GameControls.Down(IInteractable.InteractKey, GameControls.Action.Interact) && Time.time - seatedAt > 0.3f) { TryHopOut(); return 0; }

        if (HogTuning.HasBeam(levels.claw))
        {
            if (GameControls.Held(ClawKey, GameControls.Action.Arm) || (overrideKeys & Hogs.PilotBeam) != 0) held |= Hogs.PilotBeam;
        }
        else if (GameControls.Down(ClawKey, GameControls.Action.Arm)) Hogs.RequestClaw(lot);

        // The bomb bay: hold G for the lot. With Belly 3 a tap drops one bag.
        if (GameControls.Held(DumpKey, GameControls.Action.TipIt))
        {
            dumpHeld += Time.deltaTime;
            if (dumpHeld >= HogTuning.DumpHoldSeconds && !dumpSent) { dumpSent = true; Hogs.RequestDump(lot, false); }
        }
        else
        {
            if (dumpHeld > 0f && !dumpSent && HogTuning.HasPrecisionDrop(levels.belly)) Hogs.RequestDump(lot, true);
            dumpHeld = 0f;
            dumpSent = false;
        }

        // Battery 3: feed the reactor
        if (HogTuning.HasReactor(levels.battery) && GameControls.Held(BurnKey, GameControls.Action.Alternative) && Time.time >= nextBurn)
        {
            nextBurn = Time.time + HogTuning.ReactorInterval;
            if (d.battery < 0.995f && d.loadKg > 0.01f) Hogs.RequestBurn(lot);
        }

        // Stranded with a flat battery: pay for a ride home.
        bool stranded = landed && !onPad && d.battery <= HogTuning.TowBelow;
        if (stranded && GameControls.Held(TowKey, GameControls.Action.Recover))
        {
            towHold += Time.deltaTime;
            if (towHold >= HogTuning.TowHoldSeconds)
            {
                towHold = 0f;
                if (Ledger.Spend(HogTuning.TowFee, $"{Scrapazon.Name} Prime Air Tow", LedgerIds.Tow))
                {
                    var pad = Hogs.PadPose(lot);
                    Fx.Dust(transform.position, 1.5f);
                    yaw = pad.rotation.eulerAngles.y;
                    velocity = Vector3.zero;
                    transform.SetPositionAndRotation(pad.position, pad.rotation);
                    SeatPilot();
                    Fx.Dust(pad.position, 1.5f);
                    Sfx.Play("slam", pad.position, 0.7f, 1.1f);
                    GameHUD.Toast("Towed home. It's charging. It's also judging you.", ToastStyle.Good, 3f);
                }
                else GameHUD.Toast($"Can't afford the tow ({Economy.Money(HogTuning.TowFee)}). It'll sulk back to 20% in a few minutes.", ToastStyle.Bad, 3f);
            }
        }
        else towHold = 0f;
        return held;
    }

    string Hint(HogData d, bool onPad)
    {
        string battery = $"Battery {Mathf.CeilToInt(d.battery * 100f)}%{(onPad && d.battery < 1f ? " (charging)" : "")}";
        string belly = $"Belly {d.loadKg:0}/{Hogs.BellyKg(lot):0} kg";
        if (landed && !onPad && d.battery <= HogTuning.TowBelow)
            return $"HOVERHOG · {battery} · Hold R: {Scrapazon.Name} Prime Air Tow (−{Economy.Money(HogTuning.TowFee)}) · E hop out";
        if (landed) return $"HOVERHOG · {battery} · {belly} · Space take off · E hop out";
        string grab = HogTuning.HasBeam(levels.claw) ? "hold X beam" : "X claw";
        string drop = HogTuning.HasPrecisionDrop(levels.belly) ? "G drop one / hold all" : "hold G bomb bay";
        string extra = "";
        if (HogTuning.HasBoost(levels.rotors)) extra += Time.time >= boostReadyAt ? " · Shift jets" : $" · jets {Mathf.CeilToInt(boostReadyAt - Time.time)}s";
        if (HogTuning.HasReactor(levels.battery)) extra += " · hold F burn";
        return $"HOVERHOG · {battery} · {belly} · Space/Ctrl up/down · {grab} · {drop}{extra}";
    }

    /// <summary>Slide a body-sized sphere along <paramref name="delta"/>; walls and roofs stop it, nothing pushes it.</summary>
    Vector3 Move(Vector3 pos, Vector3 delta)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            float dist = delta.magnitude;
            if (dist < 0.0001f) break;
            Vector3 dir = delta / dist;
            Vector3 centre = pos + Vector3.up * HogTuning.BodyCentreY;
            if (!NearestHit(centre, dir, dist + HogTuning.Skin, out var hit)) { pos += delta; break; }
            float travel = Mathf.Max(0f, hit.distance - HogTuning.Skin);
            pos += dir * travel;
            delta = Vector3.ProjectOnPlane(delta - dir * travel, hit.normal);
            velocity = Vector3.ProjectOnPlane(velocity, hit.normal);
        }
        return pos;
    }

    static readonly RaycastHit[] hits = new RaycastHit[16];

    bool NearestHit(Vector3 centre, Vector3 dir, float dist, out RaycastHit best)
    {
        best = default;
        int n = Physics.SphereCastNonAlloc(centre, HogTuning.BodyRadius, dir, hits, dist, FlyMask, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            if (h.collider == null || h.distance <= 0f || h.collider.transform.IsChildOf(transform)) continue;
            if (h.distance < nearest) { nearest = h.distance; best = h; }
        }
        return nearest < float.MaxValue;
    }

    /// <summary>Metres between the skids at <paramref name="pos"/> and whatever is under them (∞ over nothing).</summary>
    float Height(Vector3 pos, out float groundY)
    {
        groundY = float.NegativeInfinity;
        int n = Physics.RaycastNonAlloc(pos + Vector3.up * 1.2f, Vector3.down, hits, 90f, FlyMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;
            if (h.point.y > groundY) groundY = h.point.y;
        }
        return groundY > float.NegativeInfinity ? pos.y - groundY : float.PositiveInfinity;
    }

    // =====================================================================
    // Collecting (host): the Claw, then the beam that replaces it
    // =====================================================================

    Vector3 BellyPoint => transform.position + Vector3.up * 1.2f;

    static bool Grabbable(TrashBag b) =>
        b != null && !b.HasBurst && b.State == CarryState.Resting && b.Holder == null && !b.IsInScriptedFlight;

    bool Permitted(string business, Vector3 at)
    {
        var district = Districts.AtPosition(at);
        if (district == null || district.tier <= 1) return true;
        return Hogs.IsOwnBusiness(business) ? Districts.IsUnlocked(district) : BusinessUpgradeNet.HasPermitForBusiness(business, district);
    }

    /// <summary>Host: the resting bags under the Hoverhog within <paramref name="radius"/>, nearest first, that it
    /// may take and that fit together. The out flags say why a short list is short.</summary>
    void CurbBagsBelow(float radius, int max, List<TrashBag> into, out bool anyBag, out bool anyPermitted, out bool anyFits)
    {
        into.Clear();
        anyBag = anyPermitted = anyFits = false;
        string business = Hogs.BusinessOf(lot);
        Vector3 here = transform.position;
        // Curb bags (paid like a truck pickup) and any other bag lying about outdoors: a bombed load, a dropped sack.
        // Those pay nothing, exactly as when they are tossed back into a truck.
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            var b = all[i] as TrashBag;
            if (!Grabbable(b) || floating.Contains(b)) continue;
            Vector3 at = b.WorldCenter;
            if (at.y < -5f) continue; // a shop's pocket interior
            Vector3 to = at - here;
            if (to.y > 1f) continue;
            to.y = 0f;
            if (to.sqrMagnitude > radius * radius) continue;
            anyBag = true;
            if (b.IsCurbBag && !Permitted(business, at)) continue;
            anyPermitted = true;
            into.Add(b);
        }
        into.Sort((a, b) => Flat(a.WorldCenter - here).CompareTo(Flat(b.WorldCenter - here)));
        // Keep the nearest ones that fit in the belly together.
        float room = Hogs.BellyKg(lot) - D.loadKg - FloatingKg();
        for (int i = 0; i < into.Count;)
        {
            if (i < max && into[i].MassKg <= room + 0.001f) { room -= into[i].MassKg; anyFits = true; i++; }
            else into.RemoveAt(i);
        }
    }

    static float Flat(Vector3 v) => v.x * v.x + v.z * v.z;

    float FloatingKg()
    {
        float kg = 0f;
        foreach (var c in floating) if (c != null) kg += c.MassKg;
        for (int i = 0; i < heldBags.Count; i++) if (heldBags[i] != null) kg += heldBags[i].MassKg;
        return kg;
    }

    static readonly List<TrashBag> picked = new List<TrashBag>();

    /// <summary>Host: the pilot pressed X. Grabs the nearest resting curb bag(s) under the Hoverhog, if the rules allow.</summary>
    public void HostClaw(ulong client)
    {
        var d = D;
        if (!Hogs.IsHost || d == null) return;
        var lv = Hogs.LevelsOf(lot);
        if (HogTuning.HasBeam(lv.claw)) return; // the beam is held, not pressed
        float height = Height(transform.position, out _);
        CurbBagsBelow(HogTuning.ClawRadius, HogTuning.ClawBagsAt(lv.claw), picked, out bool anyBag, out bool anyPermitted, out bool anyFits);
        var verdict = HogRules.CheckClaw(d.pilot == client, clawRoutine != null, height, anyBag, anyFits, anyPermitted);
        if (verdict != HogRules.Claw.Ok)
        {
            if (verdict != HogRules.Claw.NotPilot && verdict != HogRules.Claw.Busy)
                Hogs.Notify(client, HogRules.Message(verdict), ToastStyle.Bad);
            return;
        }
        clawRoutine = StartCoroutine(ClawCycle(new List<TrashBag>(picked), client, lv.claw));
    }

    IEnumerator ClawCycle(List<TrashBag> bags, ulong client, int clawLevel)
    {
        float total = HogTuning.ClawSecondsAt(clawLevel);
        var first = bags[0];
        Vector3 at = first.WorldCenter;
        bool slip = UpgradeEffects.DetectRoll(first.Contents != null ? first.Contents.bagId : "bag", "hog_claw_" + (++clawCount), HogTuning.ClawSlipAt(clawLevel));
        Hogs.ClawFx(lot, at, total);
        yield return new WaitForSeconds(total * (HogTuning.ClawDrop + HogTuning.ClawGrip * 0.5f));

        Vector3 off = first != null ? first.WorldCenter - transform.position : Vector3.zero;
        float room = Hogs.BellyKg(lot) - D.loadKg;
        for (int i = bags.Count - 1; i >= 0; i--)
        {
            if (!Grabbable(bags[i]) || bags[i].MassKg > room + 0.001f) { bags.RemoveAt(i); continue; }
            room -= bags[i].MassKg;
        }
        if (bags.Count == 0 || Flat(off) > HogTuning.ClawDrift * HogTuning.ClawDrift)
        {
            Hogs.Notify(client, "The Claw missed. Hold still over the bag!", ToastStyle.Bad);
            yield return new WaitForSeconds(total * 0.3f);
            clawRoutine = null;
            yield break;
        }
        foreach (var bag in bags)
        {
            bag.SetInFlight();
            bag.ByArm = true;
            heldBags.Add(bag);
        }
        Sfx.Play("crunch", at, 0.5f, 1.3f);

        float lift = total * (HogTuning.ClawGrip * 0.5f + HogTuning.ClawLift);
        if (slip)
        {
            yield return new WaitForSeconds(lift * 0.55f);
            heldBags.Clear();
            foreach (var bag in bags)
            {
                if (bag == null || bag.State == CarryState.Consumed) continue;
                bag.ByArm = false;
                bag.NoBurstUntil = Time.time + 4f;
                bag.EndCarry(Vector3.zero, false);
            }
            Hogs.Notify(client, "The Claw slipped! It's only mostly rigged.", ToastStyle.Bad);
            yield return new WaitForSeconds(lift * 0.45f);
            clawRoutine = null;
            yield break;
        }
        yield return new WaitForSeconds(lift);
        heldBags.Clear();
        foreach (var bag in bags)
            if (bag != null && bag.State != CarryState.Consumed) Swallow(bag, client);
        yield return new WaitForSeconds(total * (1f - HogTuning.ClawDrop - HogTuning.ClawGrip - HogTuning.ClawLift));
        clawRoutine = null;
    }

    /// <summary>Host: the bag is in the belly. Pay, Rep, curb record and stop bookkeeping are the truck hopper's own path.</summary>
    void Swallow(TrashBag bag, ulong client)
    {
        var contents = bag.Contents;
        if (contents == null || !Hogs.CanFit(lot, bag.MassKg))
        {
            bag.ByArm = false;
            bag.NoBurstUntil = Time.time + 4f;
            bag.EndCarry(Vector3.zero, false);
            return;
        }
        string business = Hogs.BusinessOf(lot);
        Vector3 at = BellyPoint;
        string stopId = bag.SourceStop != null ? bag.SourceStop.stopId : null;
        bool actorFeedback = !NetSession.InSession || client == Hogs.LocalClient;
        // The hopper's collect path files the bag under the business's truck; it belongs in the belly, so move it across.
        TruckHopper.CollectCurbBag(contents, business, at, true, stopId, actorFeedback, out var feedback);
        TakeBackFromTruck(business, contents);
        Hogs.AddCargo(lot, contents);
        if (WorldState.Instance != null) WorldState.Instance.BroadcastCollectionFx(at, feedback, client);
        var stop = bag.SourceStop;
        if (stop != null) stop.NotifyBagCollected(bag);
        bag.SourceStop = null;
        bag.MarkConsumed();
        Juice.ShrinkAndDestroy(bag.gameObject, 0.15f);
        NearlyFull(client);
    }

    void NearlyFull(ulong client)
    {
        var d = D;
        float cap = Hogs.BellyKg(lot);
        if (d.loadKg >= cap * 0.9f && Time.time - lastFullToast > 12f)
        {
            lastFullToast = Time.time;
            Hogs.Notify(client, $"Belly's nearly full ({d.loadKg:0}/{cap:0} kg). Drop it on your roof!", ToastStyle.Info);
        }
    }
    float lastFullToast = -100f;

    static void TakeBackFromTruck(string business, BagContents contents)
    {
        if (Hogs.IsOwnBusiness(business))
        {
            if (GameSession.TruckBags.Remove(contents)) GameSession.NotifyChanged();
            return;
        }
        var cargo = Businesses.HostCargo(business);
        if (cargo == null || !cargo.Remove(contents)) return;
        float kg = 0f;
        foreach (var b in cargo) if (b != null) kg += b.TotalMassKg;
        Businesses.TryGetTruckLoad(business, out _, out float cap);
        Businesses.SetTruckLoad(business, kg, cap);
    }

    /// <summary>Every machine: the claw drops to <paramref name="target"/> and comes back up over <paramref name="seconds"/>.</summary>
    public void PresentClaw(Vector3 target, float seconds)
    {
        clawTarget = target;
        clawSeconds = Mathf.Max(0.2f, seconds);
        clawStart = Time.time;
        Sfx.Play("whoosh", transform.position, 0.5f, 0.8f);
    }

    /// <summary>Host, every frame: while the pilot holds the beam key at a low, slow hover, one more curb bag or loose
    /// bit of litter under the Hoverhog starts floating up every beat.</summary>
    void HostBeam(HogData d)
    {
        var lv = Hogs.LevelsOf(lot);
        if (!d.beamHeld || d.pilot == Hogs.Nobody || !HogTuning.HasBeam(lv.claw)) { d.beaming = false; return; }
        float height = Height(transform.position, out _);
        var verdict = HogRules.CheckBeam(true, lv.claw, height, Mathf.Sqrt(Flat(velocity)));
        d.beaming = verdict == HogRules.Beam.Ok;
        if (!d.beaming)
        {
            if (Time.time - lastBeamToast > 3f) { lastBeamToast = Time.time; Hogs.Notify(d.pilot, HogRules.Message(verdict), ToastStyle.Bad); }
            return;
        }
        if (Time.time < nextBeamAt) return;
        nextBeamAt = Time.time + HogTuning.BeamIntervalAt(lv.claw);
        float radius = HogTuning.BeamRadiusAt(lv.claw);
        CurbBagsBelow(radius, 1, picked, out bool anyBag, out bool anyPermitted, out bool anyFits);
        if (picked.Count > 0) { StartCoroutine(FloatUp(picked[0], d.pilot)); return; }
        var loose = LooseBelow(radius);
        if (loose != null) { StartCoroutine(FloatUp(loose, d.pilot)); return; }
        if (anyBag && Time.time - lastBeamToast > 4f)
        {
            lastBeamToast = Time.time;
            Hogs.Notify(d.pilot, HogRules.Message(!anyPermitted ? HogRules.Claw.NoPermit : HogRules.Claw.Full), ToastStyle.Bad);
        }
    }

    /// <summary>Host: the nearest loose item lying on the ground under the beam that fits (litter, a burst bag's spill).</summary>
    LooseItem LooseBelow(float radius)
    {
        Vector3 here = transform.position;
        float room = Hogs.BellyKg(lot) - D.loadKg - FloatingKg();
        LooseItem best = null;
        float bestSq = radius * radius;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null || li.Item == null || li.State != CarryState.Resting || li.Holder != null || li.IsInScriptedFlight) continue;
            if (floating.Contains(li) || li.MassKg > room + 0.001f) continue;
            Vector3 to = li.transform.position - here;
            if (to.y > 1f || to.y < -HogTuning.BeamMaxHeight - 4f) continue;
            float sq = Flat(to);
            if (sq < bestSq) { bestSq = sq; best = li; }
        }
        return best;
    }

    IEnumerator FloatUp(Carryable thing, ulong client)
    {
        floating.Add(thing);
        thing.SetInFlight();
        var bag = thing as TrashBag;
        if (bag != null) bag.ByArm = true;
        Vector3 from = thing.transform.position;
        float spin = ShopModKit.Range(-240f, 240f);
        Sfx.Play("whoosh", from, 0.35f, 1.5f);
        for (float t = 0f; t < HogTuning.BeamLiftSeconds; t += Time.deltaTime)
        {
            if (thing == null || thing.State == CarryState.Consumed) break;
            float k = t / HogTuning.BeamLiftSeconds;
            Vector3 to = BellyPoint;
            Vector3 p = Vector3.Lerp(from, to, k * k);
            p += new Vector3(Mathf.Sin(k * 9f), 0f, Mathf.Cos(k * 9f)) * (0.4f * (1f - k));
            thing.transform.SetPositionAndRotation(p, Quaternion.Euler(k * spin, k * spin * 0.7f, 0f));
            yield return null;
        }
        floating.Remove(thing);
        if (thing == null || thing.State == CarryState.Consumed) yield break;
        if (bag != null) { Swallow(bag, client); yield break; }
        var loose = thing as LooseItem;
        if (loose == null || loose.Item == null || !Hogs.CanFit(lot, loose.MassKg)) { thing.EndCarry(Vector3.zero, false); yield break; }
        Hogs.AddLoose(lot, loose.Item);
        loose.MarkConsumed();
        Juice.ShrinkAndDestroy(loose.gameObject, 0.12f);
        NearlyFull(client);
    }

    // =====================================================================
    // The bomb bay (host)
    // =====================================================================

    /// <summary>Host: the pilot held G (or, with Belly 3, tapped it for one bag). Over the owner's roof the load goes
    /// down the hatch into the shop; anywhere else it falls out as litter. A second request stops a running drop.</summary>
    public void HostDump(ulong client, bool single)
    {
        var d = D;
        if (!Hogs.IsHost || d == null || d.pilot != client) return;
        if (dumpRoutine != null)
        {
            StopCoroutine(dumpRoutine);
            dumpRoutine = null;
            d.dumping = false;
            return;
        }
        if (single && !HogTuning.HasPrecisionDrop(Hogs.LevelsOf(lot).belly)) return;
        var verdict = CheckDump(client, out _);
        if (verdict != HogRules.Dump.Ok) { Hogs.Notify(client, HogRules.Message(verdict), ToastStyle.Bad); return; }
        dumpRoutine = StartCoroutine(DumpCycle(client, single));
    }

    HogRules.Dump CheckDump(ulong client, out bool overRoof)
    {
        var d = D;
        var next = Hogs.PeekCargo(lot);
        overRoof = Hogs.OverOwnRoof(lot, transform.position);
        float height = Height(transform.position, out float groundY);
        bool room = !overRoof || next == null || !DepotRowWorld.Active || DepotBacklog.CanFit(lot, next.TotalMassKg);
        return HogRules.CheckDump(d.pilot == client, next != null, overRoof, !float.IsInfinity(height), groundY, room);
    }

    IEnumerator DumpCycle(ulong client, bool single)
    {
        var d = D;
        d.dumping = true;
        Sfx.Play("beep", transform.position, 0.7f, 0.8f);
        yield return new WaitForSeconds(single ? 0.2f : 0.35f);
        float interval = HogRules.DumpInterval(Hogs.LevelsOf(lot).belly, d.cargo.Count);
        int hatch = 0, litter = 0;
        HogRules.Dump verdict;
        while ((verdict = CheckDump(client, out bool overRoof)) == HogRules.Dump.Ok)
        {
            var contents = Hogs.TakeCargo(lot);
            if (contents == null) break;
            if (overRoof) { DownTheHatch(contents); hatch++; }
            else { DropOut(contents, hatch + litter); litter++; }
            if (single) break;
            yield return new WaitForSeconds(interval);
        }
        if (single) yield return new WaitForSeconds(0.3f);
        d.dumping = false;
        dumpRoutine = null;
        if (verdict != HogRules.Dump.Empty && verdict != HogRules.Dump.Ok && verdict != HogRules.Dump.NotPilot)
            Hogs.Notify(client, HogRules.Message(verdict), ToastStyle.Bad);
        if (single) yield break;
        if (hatch > 0) Hogs.Notify(client, $"Down the hatch: {hatch} {SortingKit.Plural(hatch, "bag", "bags")} on your tipping floor.", ToastStyle.Good);
        if (litter > 0) Hogs.Notify(client, $"Bombs away: {litter} {SortingKit.Plural(litter, "bag", "bags")}. That's litter now.", ToastStyle.Info);
        NetAudit.Event("hog_dump", new Dictionary<string, object> { { "lot", lot }, { "hatch", hatch }, { "litter", litter } });
    }

    /// <summary>Host: one bag through the roof hatch. With the shop's interior up it lands on the tipping floor now;
    /// in the solo scene flow it waits in the depot's delivery queue and drops when you walk in.</summary>
    void DownTheHatch(BagContents contents)
    {
        Vector3 hatchAt = DepotRowLayout.StreetPoint(lot, HogTuning.HatchLocal);
        Sfx.Play("whoosh", hatchAt, 0.6f, 0.9f);
        Fx.Dust(hatchAt + Vector3.up * 0.2f, 0.5f);
        var depot = DepotRowScene.Lot(lot);
        var floor = DepotRowWorld.Active && depot != null && depot.interior != null && depot.InteriorOn
            ? depot.interior.GetComponentInChildren<TippingFloor>(true) : null;
        if (floor != null)
        {
            float y = SortingKit.FloorY(floor.pileCenter, floor.transform.position.y);
            int h = PickupVisuals.StableHash(contents.bagId ?? "hog");
            Vector3 from = floor.ClampToPile(floor.PilePoint(y + 3.1f, contents.bagId ?? "hog"), y + 3.1f);
            if (contents.sackVariant < 0)
            {
                foreach (var item in contents.items)
                    if (item != null) LooseItem.Spawn(item, from, Quaternion.Euler(0f, h % 360, 0f), Vector3.down * 1.5f, false);
            }
            else
            {
                var bag = TrashBag.Spawn(contents, from, Quaternion.Euler(0f, h % 360, 0f), Vector3.down * 1.5f);
                if (bag != null) bag.NoBurstUntil = Time.time + 3f;
            }
            floor.EnsureStink();
            return;
        }
        string business = Hogs.BusinessOf(lot);
        if (Hogs.IsOwnBusiness(business)) { GameSession.AddDepotDelivery(contents); return; }
        // Another business whose interior is not up on the host: the bag waits in that business's truck.
        var cargo = Businesses.HostCargo(business);
        if (cargo == null) { Hogs.AddCargo(lot, contents); return; }
        cargo.Add(contents);
        float kg = 0f;
        foreach (var b in cargo) if (b != null) kg += b.TotalMassKg;
        Businesses.TryGetTruckLoad(business, out _, out float cap);
        Businesses.SetTruckLoad(business, kg, cap);
    }

    /// <summary>Host: one bag out of the belly doors (or out the back when it is sitting on the ground).</summary>
    void DropOut(BagContents contents, int n)
    {
        bool sitting = Height(transform.position, out _) < 1.6f;
        Vector3 from = sitting ? transform.position - transform.forward * 3.1f + Vector3.up * 0.7f : transform.position - Vector3.up * 0.5f;
        Vector3 fling = transform.right * ((n % 3) - 1) * 0.9f + (sitting ? -transform.forward * 1.5f : Vector3.down * 2f);
        fling += new Vector3(velocity.x, 0f, velocity.z) * 0.6f;
        int h = PickupVisuals.StableHash(contents.bagId ?? n.ToString());
        if (contents.sackVariant < 0)
        {
            foreach (var item in contents.items)
                if (item != null) LooseItem.Spawn(item, from, Quaternion.Euler(0f, h % 360, 0f), fling, false);
        }
        else
        {
            var bag = TrashBag.Spawn(contents, from, Quaternion.Euler(0f, h % 360, 0f), fling);
            // Low drops land whole; from up high a bag may well burst. That is what the height is for.
            if (bag != null) bag.NoBurstUntil = Time.time + (sitting ? 3f : 0.5f);
        }
        Sfx.Play("whoosh", from, 0.5f, 1.1f + (n % 5) * 0.05f);
    }

    // =====================================================================
    // Presentation (every machine)
    // =====================================================================

    void Present(HogData d, float dt)
    {
        if (model == null) return;
        float reach = 0f;
        if (clawStart >= 0f)
        {
            float t01 = (Time.time - clawStart) / clawSeconds;
            if (t01 >= 1f) clawStart = -1f;
            else reach = HogRules.ClawReach01(t01);
        }
        float height = d.beaming ? Height(transform.position, out _) : 0f;
        var look = new HogLook
        {
            flown = d.pilot != Hogs.Nobody, landed = landed, dumping = d.dumping, beaming = d.beaming, boosting = d.boosting,
            gripping = heldBags.Count > 0, battery = d.battery, yaw = yaw,
            localVelocity = Quaternion.Inverse(transform.rotation) * velocity,
            clawReach = reach, clawTarget = clawTarget,
            beamLength = float.IsInfinity(height) ? HogTuning.BeamMaxHeight : height + 1.2f,
            beamRadius = HogTuning.BeamRadiusAt(levels.claw),
        };
        model.Animate(dt, look);

        // The belly thumps when something lands in it (Belly 2 chews)
        if (shownLoad >= 0f && d.loadKg > shownLoad + 0.05f)
        {
            Sfx.Play("toss_in", BellyPoint, 0.7f, 0.9f);
            if (levels.belly >= 2) Sfx.Play("crunch", BellyPoint, 0.5f, 0.8f);
            Juice.Punch(model.Root, levels.belly >= 2 ? 0.14f : 0.07f, 0.22f);
        }
        shownLoad = d.loadKg;

        if (d.beaming && levels.claw >= 3) CropCircle();
    }

    static readonly List<GameObject> circles = new List<GameObject>();

    /// <summary>Claw 3: a ring pressed into the grass under the beam every few metres. Cosmetic; gone in a few minutes.</summary>
    void CropCircle()
    {
        Vector3 here = transform.position;
        if (Flat(here - lastCircle) < HogTuning.CropCircleEvery * HogTuning.CropCircleEvery) return;
        if (!Physics.Raycast(here + Vector3.up, Vector3.down, out var hit, 40f, 1 << SortingKit.GroundLayer, QueryTriggerInteraction.Ignore)) return;
        if (TownRelief.Above(hit.point) > 1.5f || CoastalCityLayout.DistanceToRoad(hit.point) < HogTuning.CropCircleOffRoad) return;
        lastCircle = here;
        var mat = ShopModKit.Lit("mat/hog_crop_circle", new Color(0.2f, 0.42f, 0.16f), 0.05f);
        var go = new GameObject("Crop circle") { layer = 2 };
        go.transform.position = hit.point + Vector3.up * 0.03f;
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal) * Quaternion.Euler(0f, yaw, 0f);
        go.transform.localScale = new Vector3(1f, 0.08f, 1f);
        var ring = HogMesh.Torus("hog_crop_ring", 2.4f, 0.22f, 20, 4);
        var dot = HogMesh.Torus("hog_crop_dot", 0.8f, 0.18f, 12, 4);
        foreach (var mesh in new[] { ring, dot })
        {
            var part = new GameObject(mesh.name) { layer = 2 };
            part.transform.SetParent(go.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = part.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(mesh, HogTuning.CropCircleSeconds + 1f);
        }
        Destroy(go, HogTuning.CropCircleSeconds);
        circles.Add(go);
        circles.RemoveAll(c => c == null);
        if (circles.Count > 30) { Destroy(circles[0]); circles.RemoveAt(0); }
    }
}

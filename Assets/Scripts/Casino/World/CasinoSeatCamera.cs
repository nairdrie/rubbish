using System;
using System.Collections.Generic;
using System.Reflection;
using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Seat mode's camera and body (Docs/Casino.md §9 / §9.1): puts the LOCAL player at a <see cref="CasinoSpot"/> of a
/// <see cref="CasinoStation"/>, glides the camera from the third-person rig to the spot's first-person eye pose (about 0.6 s) and
/// back, lets the mouse / right stick look within the spot's limits, and leans on request. Nothing here knows about the old uGUI
/// screens: use <see cref="Enter"/> / <see cref="Exit"/> from a controller.
/// <para><b>How the takeover works (it follows <c>SortStation</c>, the project's existing first-person takeover; no edits to
/// PlayerController / TruckHopper):</b></para>
/// <list type="bullet">
/// <item><b>Camera:</b> a runtime Cinemachine virtual camera (priority 60, above the on-foot rig's 11) is enabled; the main
/// camera's brain blends to it through a pass-through <c>CinemachineCore.GetBlendOverride</c> hook (EaseInOut, 0.6 s in and out).
/// The brain stays in charge, so everything that rides AFTER it still applies on top of the seat pose: the drunk / rabies sway
/// (<c>RabiesCameraSway</c>, order 9990), the camera shaker, post-processing and the fade. A cut (no blend) is used when an exit
/// is forced (pass-out, teleport, session end).</item>
/// <item><b>Body:</b> the third-person controller and CharacterController are disabled (like the truck and the sort table), the
/// player's transform slides to <see cref="CasinoSpot.stand"/> and faces the table (the owner-authoritative NetworkTransform
/// replicates it, so everyone sees the occupant standing there), the local renderers are hidden (other machines still see the body),
/// and the stool slides away.</item>
/// <item><b>Input arbitration:</b> <c>PlayerController.InteractionFocusOverride</c> claims the player (no interaction scan, no E / F,
/// no prompt), and that same claim makes <c>UiStack</c> ignore Esc for the frame, so Esc reaches THIS component first and leaves the
/// seat instead of opening the pause menu. (<c>PushUILock</c> is not used: it frees the cursor and switches the PlayerInput off.)</item>
/// <item><b>Forced exits</b> (immediate, with a cut): passing out, a door / scene transition, being teleported or moved from the spot,
/// being shoved / flopped / carried, driving or chess, the session ending, the player despawning, the station being destroyed
/// (casino scene unloaded), leaving the casino.</item>
/// </list>
/// Look limits: yaw is +/- <c>lookYaw</c> degrees around the pose's own facing; pitch is absolute degrees above the horizon
/// (<c>lookPitchMin</c>..<c>lookPitchMax</c>). The lean and watch poses blend position, field of view and aim over ~0.25 s; the
/// player's look offsets carry over.
/// </summary>
[DefaultExecutionOrder(50)] // before the CinemachineBrain's LateUpdate and before UiStackRunner (+500)
public sealed class CasinoSeatCamera : MonoBehaviour
{
    public enum ExitReason { User, Requested, PassOut, Transition, Teleported, Body, Vehicle, Session, SceneGone, LeftCasino }
    public enum CursorMode
    {
        /// <summary>The OS cursor is locked: mouse delta looks, a crosshair sits at the screen centre (what the sort table does).</summary>
        Locked,
        /// <summary>The OS cursor is free (confined): it points at the table; look with the middle mouse button, the screen edges or the right stick.</summary>
        Free,
    }

    public const int LivePriority = 60;

    public static CasinoSeatCamera Instance { get; private set; }

    /// <summary>The local player is seated (including the glides).</summary>
    public static bool IsSeated => Instance != null && Instance.seated;

    /// <summary>Make the singleton (a DontDestroyOnLoad object) if it isn't there.</summary>
    public static CasinoSeatCamera Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("[CasinoSeatCamera]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CasinoSeatCamera>();
        return Instance;
    }

    [Header("Tuning")]
    public float glideSeconds = 0.6f;
    public float leanSeconds = 0.25f;
    [Tooltip("Extra multiplier on top of the player's look sensitivity setting.")]
    public float lookScale = 1f;
    [Tooltip("Free cursor mode: fraction of the screen (each edge) that pans the view, and the pan speed in degrees per second.")]
    public float edgeBand = 0.05f, edgeSpeed = 70f;
    [Tooltip("Esc and the pad's B / Start leave. The station key (Q) would clash with Q / E chip selection, so it is off.")]
    public bool exitOnStationKey;

    /// <summary>How the mouse behaves. Set it any time; takes effect at once while seated.</summary>
    public CursorMode Mode
    {
        get => mode;
        set { mode = value; if (seated) ApplyCursor(); }
    }

    public CasinoStation Station { get; private set; }
    public int SpotIndex { get; private set; } = -1;
    public CasinoSpot Spot => Station != null ? Station.Spot(SpotIndex) : null;
    public bool Seated => seated;
    public bool Leaning => leanOn;
    public bool Watching => watchOn;
    /// <summary>The glide in is still under way (clicks should wait).</summary>
    public bool Gliding => seated && Time.unscaledTime - enteredAt < glideSeconds;
    /// <summary>Yaw offset (degrees) and pitch (absolute degrees above the horizon) of the current view.</summary>
    public float ViewYawOffset => lookYaw;
    public float ViewPitch => FinalPitch;
    /// <summary>The camera pose the seat is asking for right now (what the virtual camera is set to).</summary>
    public Pose CurrentPose => new Pose(cam != null ? cam.transform.position : Vector3.zero, cam != null ? cam.transform.rotation : Quaternion.identity);

    /// <summary>Raised after Enter succeeded / after the seat was left (with the reason).</summary>
    public event Action<CasinoStation, int> Entered;
    public event Action<CasinoStation, int, ExitReason> Exited;

    CursorMode mode = CursorMode.Locked;
    bool seated, leanOn, watchOn, exitImmediate;
    float wLean, wWatch, enteredAt, exitedAt = -10f;
    float lookYaw, lookPitchOff;
    float baseYaw, basePitch;
    CinemachineVirtualCamera cam;

    // the local player's parts
    PlayerController player;
    StarterAssets.ThirdPersonController tpc;
    CharacterController cc;
    Animator animator;
    StarterAssets.StarterAssetsInputs inputs;
    PlayerInput playerInput;
    bool tpcWasEnabled, ccWasEnabled;
    readonly List<Renderer> hidden = new List<Renderer>();
    bool bodyHidden;
    float pendingShowAt = -1f, pendingCamOffAt = -1f;
    Vector3 slideFrom, standPos;
    Quaternion slideFromRot, standRot;
    float slideTime;
    bool ownsBusy;
    CasinoStation busyStation;
    Func<PlayerController, bool> prevFocus;
    bool focusInstalled;

    // delayed stool restore after leaving
    CasinoStation restoreStation; int restoreSpot = -1; float restoreDeadline;

    // ------------------------------------------------------------------ enter / exit

    /// <summary>Sit down: place the local player at the spot and glide the camera to its eye pose. False when refused.</summary>
    public bool Enter(CasinoStation station, int spotIndex)
    {
        if (seated) return false;
        if (station == null) return false;
        var spot = station.Spot(spotIndex);
        if (spot == null || spot.eye == null || spot.stand == null) { Debug.LogWarning("[CasinoSeat] " + station.name + " has no usable spot " + spotIndex + " (rebuild with Rubbish/Setup/Casino)."); return false; }
        var p = PlayerController.Local; // coop:allow(local) the seat camera is this machine's own player's view
        if (p == null || !p.IsOwner || p.IsDriving || p.IsPlayingChess) return false;
        if (CasinoWorld.Transitioning || DepotRowWorld.Transitioning || SceneController.IsTransitioning || DrunkStatus.PassingOut) return false;
        if (CasinoStation.LocalBusy != null && CasinoStation.LocalBusy != station) return false;
        if (PlayerController.IsUIBlocking) return false;
        var carrier = p.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Hands full! Drop it first (G)", ToastStyle.Bad, 2f);
            return false;
        }
        var body = p.GetComponent<PlayerBody>();
        if (body != null && body.State != BodyState.Normal) return false;

        FinishPendingRestore();
        player = p;
        tpc = p.GetComponent<StarterAssets.ThirdPersonController>();
        cc = p.GetComponent<CharacterController>();
        animator = p.GetComponent<Animator>();
        inputs = p.GetComponent<StarterAssets.StarterAssetsInputs>();
        playerInput = p.GetComponent<PlayerInput>();

        Station = station; SpotIndex = spotIndex;
        EnsureCamera();
        if (cam == null) return false;

        tpcWasEnabled = tpc != null && tpc.enabled;
        if (tpc != null) tpc.enabled = false;
        ccWasEnabled = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        IdleAnimator();
        ClearInputs();

        // Where the body goes: slide there (or jump when it is far away: through walls looks wrong to others).
        slideFrom = p.transform.position; slideFromRot = p.transform.rotation;
        standPos = spot.stand.position; standRot = FlowUtil.Upright(spot.stand.rotation);
        if ((slideFrom - standPos).sqrMagnitude > 36f)
        {
            DepotRowWorld.TeleportPlayer(p, standPos, standRot);
            slideFrom = standPos; slideFromRot = standRot;
        }

        leanOn = watchOn = false; wLean = wWatch = 0f;
        baseYaw = spot.eye.eulerAngles.y;
        lookYaw = 0f; lookPitchOff = 0f;
        basePitch = PitchOf(spot.eye.rotation);
        PoseCamera();
        cam.enabled = true;
        cam.Priority = LivePriority;
        exitImmediate = false;
        InstallBlendHook();

        ApplyCursor();

        if (CasinoStation.LocalBusy == null) { CasinoStation.LocalBusy = station; ownsBusy = true; busyStation = station; }
        InstallFocus();
        var tt = TooltipUI.Instance;
        if (tt != null) tt.Hide();
        GameHUD.SetContextHint(null);

        spot.SetStoolAway(true);
        seated = true;
        enteredAt = Time.unscaledTime;
        slideTime = Mathf.Max(0.2f, glideSeconds * 0.85f);
        Sfx.Play("whoosh", spot.eye.position, 0.3f, 1.3f);
        try { Entered?.Invoke(station, spotIndex); }
        catch (Exception e) { Debug.LogException(e); }
        return true;
    }

    /// <summary>Stand up: the camera glides back to the third-person rig (a cut when the exit is forced).</summary>
    public void Exit(ExitReason reason = ExitReason.User)
    {
        if (!seated) return;
        bool immediate = reason != ExitReason.User && reason != ExitReason.Requested;
        seated = false;
        exitedAt = Time.unscaledTime;
        exitImmediate = immediate;
        var station = Station; int spotIndex = SpotIndex; var spot = Spot;

        // Face where the body faces and put the third-person camera behind it.
        bool vehicle = player != null && (player.IsDriving || player.IsPlayingChess);
        if (player != null && !vehicle && reason != ExitReason.Teleported && reason != ExitReason.Session) AlignThirdPerson();

        if (cam != null)
        {
            cam.Priority = 0;
            if (immediate) { cam.enabled = false; pendingCamOffAt = -1f; }
            else pendingCamOffAt = Time.unscaledTime + glideSeconds + 0.25f;
        }
        if (!vehicle && player != null)
        {
            if (cc != null) cc.enabled = ccWasEnabled;
            if (tpc != null) tpc.enabled = tpcWasEnabled;
        }
        ClearInputs();
        if (immediate) ShowBody(); else pendingShowAt = Time.unscaledTime + 0.12f;

        if (!PlayerController.IsUIBlocking && reason != ExitReason.Session)
        {
            Cursor.lockState = CursorLockMode.Locked; // the game's normal state
            Cursor.visible = false;
        }
        UninstallFocus();
        if (ownsBusy && CasinoStation.LocalBusy == busyStation) CasinoStation.LocalBusy = null;
        ownsBusy = false; busyStation = null;
        var tt = TooltipUI.Instance;
        if (tt != null) tt.Hide();
        if (!immediate && spot != null && spot.eye != null) Sfx.Play("whoosh", spot.eye.position, 0.25f, 1.1f);

        // The stool comes back once the player has walked clear of it (never into their legs).
        if (spot != null && spot.stool != null) { restoreStation = station; restoreSpot = spotIndex; restoreDeadline = Time.unscaledTime + 120f; } // (a player who stays put keeps the stool away; the controller drives it from the seat state anyway)

        try { Exited?.Invoke(station, spotIndex, reason); }
        catch (Exception e) { Debug.LogException(e); }
        Station = null; SpotIndex = -1;
    }

    // ------------------------------------------------------------------ lean / watch

    /// <summary>Lean in (closer / lower pose, for your own cards or the layout) or back out. No-op when the spot has no lean pose.</summary>
    public void Lean(bool on)
    {
        var s = Spot;
        if (!seated || s == null) return;
        leanOn = on && s.HasLean;
        if (leanOn) watchOn = false;
    }

    public void ToggleLean() => Lean(!leanOn);

    /// <summary>The spot's third pose (roulette: the wheel, blackjack: the dealer, bar: the menu) or back. No-op without one.</summary>
    public void Watch(bool on)
    {
        var s = Spot;
        if (!seated || s == null) return;
        watchOn = on && s.HasWatch;
        if (watchOn) leanOn = false;
    }

    /// <summary>Aim the view: yaw offset from the pose's facing and absolute pitch above the horizon (both clamped to the spot's limits).</summary>
    public void SetLook(float yawOffset, float pitchAbs)
    {
        lookYaw = yawOffset;
        lookPitchOff = pitchAbs - BlendedAimPitch();
        ClampLook();
        if (seated) PoseCamera();
    }

    /// <summary>Back to the pose's own aim.</summary>
    public void ResetLook() { lookYaw = 0f; lookPitchOff = 0f; }

    // ------------------------------------------------------------------ per frame

    void OnEnable() { if (Instance == null) Instance = this; }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (seated) { seated = false; CleanUpHard(); }
    }

    void Update()
    {
        UpdateRestore();
        if (!seated) return;

        if (!StillValid(out ExitReason why)) { Exit(why); return; }

        float udt = Time.unscaledDeltaTime;
        UpdateSlide();
        UpdateBodyHide();
        bool blocked = PlayerController.IsUIBlocking;
        bool inputOk = !blocked && Time.unscaledTime - enteredAt > 0.15f;

        if (inputOk && (CasinoInput.Down(KeyCode.Escape) || GameControls.PadDown(GameControls.Action.StationExit) || GameControls.PadDown(GameControls.Action.Pause)
                        || (exitOnStationKey && Input.GetKeyDown(KeyCode.Q))))
        {
            Exit(ExitReason.User);
            return;
        }
        if (inputOk) ReadLook(udt);

        wLean = Mathf.MoveTowards(wLean, leanOn ? 1f : 0f, udt / Mathf.Max(0.05f, leanSeconds));
        wWatch = Mathf.MoveTowards(wWatch, watchOn ? 1f : 0f, udt / Mathf.Max(0.05f, leanSeconds));
        ClampLook();
        PoseCamera();
    }

    bool StillValid(out ExitReason why)
    {
        why = ExitReason.Requested;
        if (Station == null || Spot == null || Spot.eye == null) { why = ExitReason.SceneGone; return false; }
        if (cam == null) { why = ExitReason.Session; return false; }
        if (player == null || !player.isActiveAndEnabled) { why = ExitReason.Session; return false; }
        if (player != PlayerController.Local || (player.NetworkManager != null && !player.IsSpawned)) { why = ExitReason.Session; return false; } // coop:allow(local) camera and input only: is the seated player still this machine's own player
        if (DrunkStatus.PassingOut) { why = ExitReason.PassOut; return false; }
        if (player.IsDriving || player.IsPlayingChess) { why = ExitReason.Vehicle; return false; }
        if (CasinoWorld.Transitioning || DepotRowWorld.Transitioning || SceneController.IsTransitioning) { why = ExitReason.Transition; return false; }
        if (!CasinoWorld.LocalInside) { why = ExitReason.LeftCasino; return false; }
        var body = player.GetComponent<PlayerBody>();
        if (body != null && body.State != BodyState.Normal) { why = ExitReason.Body; return false; }
        // Moved off the spot by something else (a teleport, a tow): once the slide is done the body must still be there.
        if (Time.unscaledTime - enteredAt > slideTime + 0.3f && (player.transform.position - standPos).sqrMagnitude > 1.2f * 1.2f) { why = ExitReason.Teleported; return false; }
        return true;
    }

    // ------------------------------------------------------------------ body

    void UpdateSlide()
    {
        if (player == null) return;
        float k = slideTime <= 0f ? 1f : Mathf.Clamp01((Time.unscaledTime - enteredAt) / slideTime);
        float s = k * k * (3f - 2f * k);
        if (k >= 1f && (player.transform.position - standPos).sqrMagnitude < 1e-6f) return;
        player.transform.SetPositionAndRotation(Vector3.Lerp(slideFrom, standPos, s), Quaternion.Slerp(slideFromRot, standRot, s));
    }

    void IdleAnimator()
    {
        if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return;
        foreach (var prm in animator.parameters)
        {
            if (prm.type == AnimatorControllerParameterType.Float)
            {
                if (prm.name == "Speed") animator.SetFloat(prm.nameHash, 0f);
                else if (prm.name == "MotionSpeed") animator.SetFloat(prm.nameHash, 1f);
            }
            else if (prm.type == AnimatorControllerParameterType.Bool)
            {
                if (prm.name == "Grounded") animator.SetBool(prm.nameHash, true);
                else if (prm.name == "Jump" || prm.name == "FreeFall") animator.SetBool(prm.nameHash, false);
            }
        }
    }

    void ClearInputs()
    {
        if (inputs == null) return;
        inputs.look = Vector2.zero;
        inputs.move = Vector2.zero;
        inputs.jump = false;
        inputs.sprint = false;
    }

    /// <summary>Hide the local body once the camera has left the third-person rig (so the glide doesn't pass through the head).</summary>
    void UpdateBodyHide()
    {
        if (bodyHidden || player == null) return;
        if (Time.unscaledTime - enteredAt < glideSeconds * 0.35f) return;
        hidden.Clear();
        foreach (var r in player.GetComponentsInChildren<Renderer>(true))
            if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
        bodyHidden = true;
    }

    void ShowBody()
    {
        pendingShowAt = -1f;
        bool stayHidden = player != null && (player.IsDriving || player.IsPlayingChess);
        for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null && !stayHidden) hidden[i].enabled = true;
        hidden.Clear();
        bodyHidden = false;
    }

    void FinishPendingRestore()
    {
        if (pendingShowAt > 0f || hidden.Count > 0) ShowBody();
        if (pendingCamOffAt > 0f) { pendingCamOffAt = -1f; if (cam != null && !seated) cam.enabled = false; }
    }

    void UpdateRestore()
    {
        if (pendingShowAt > 0f && Time.unscaledTime >= pendingShowAt) ShowBody();
        if (pendingCamOffAt > 0f && Time.unscaledTime >= pendingCamOffAt)
        {
            pendingCamOffAt = -1f;
            if (!seated && cam != null) cam.enabled = false;
        }
        if (restoreStation != null || restoreSpot >= 0)
        {
            var st = restoreStation;
            var sp = st != null ? st.Spot(restoreSpot) : null;
            var p = PlayerController.Local; // coop:allow(local) camera and input only: where this machine's own player stands (stool restore)
            bool clear = sp == null || sp.stand == null || p == null || (p.transform.position - sp.stand.position).sqrMagnitude > 0.9f * 0.9f;
            if (seated && Station == st && SpotIndex == restoreSpot) clear = false;
            if (clear || Time.unscaledTime > restoreDeadline)
            {
                if (sp != null && !(seated && Station == st && SpotIndex == restoreSpot)) sp.SetStoolAway(false);
                restoreStation = null; restoreSpot = -1;
            }
        }
    }

    // ------------------------------------------------------------------ look

    float FinalPitch
    {
        get
        {
            var s = Spot;
            float lo = s != null ? s.lookPitchMin : -70f, hi = s != null ? s.lookPitchMax : 30f;
            return Mathf.Clamp(BlendedAimPitch() + lookPitchOff, lo, hi);
        }
    }

    float BlendedAimPitch()
    {
        var s = Spot;
        if (s == null) return basePitch;
        float p = basePitch;
        if (wLean > 0f && s.lean != null) p = Mathf.Lerp(p, PitchOf(s.lean.rotation), Smooth(wLean));
        if (wWatch > 0f && s.watch != null) p = Mathf.Lerp(p, PitchOf(s.watch.rotation), Smooth(wWatch));
        return p;
    }

    float BlendedAimYaw()
    {
        var s = Spot;
        if (s == null) return baseYaw;
        float y = baseYaw;
        if (wLean > 0f && s.lean != null) y = Mathf.LerpAngle(y, s.lean.eulerAngles.y, Smooth(wLean));
        if (wWatch > 0f && s.watch != null) y = Mathf.LerpAngle(y, s.watch.eulerAngles.y, Smooth(wWatch));
        return y;
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);

    /// <summary>Degrees above the horizon (up positive) of a rotation's forward.</summary>
    static float PitchOf(Quaternion q)
    {
        Vector3 f = q * Vector3.forward;
        return Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    void ClampLook()
    {
        var s = Spot;
        if (s == null) return;
        lookYaw = Mathf.Clamp(lookYaw, -s.lookYaw, s.lookYaw);
        float aim = BlendedAimPitch();
        float p = Mathf.Clamp(aim + lookPitchOff, s.lookPitchMin, s.lookPitchMax);
        lookPitchOff = p - aim;
    }

    void ReadLook(float udt)
    {
        Vector2 d = Vector2.zero; // degrees: x = yaw (right positive), y = pitch (down positive)
        var pad = Gamepad.current;
        bool padScheme = playerInput != null && playerInput.currentControlScheme == "Gamepad" && pad != null;
        if (padScheme)
        {
            d = pad.rightStick.ReadValue() * (130f * udt * GameSettings.LookSensitivity);
            if (GameSettings.InvertY) d.y = -d.y;
        }
        else if (mode == CursorMode.Locked)
        {
            if (inputs != null && inputs.cursorInputForLook) d = inputs.look;
            else d = new Vector2(Input.GetAxis("Mouse X"), -Input.GetAxis("Mouse Y") * (GameSettings.InvertY ? -1f : 1f)) * (0.5f * GameSettings.LookSensitivity);
        }
        else
        {
            if (Input.GetMouseButton(2))
                d = new Vector2(Input.GetAxis("Mouse X"), -Input.GetAxis("Mouse Y") * (GameSettings.InvertY ? -1f : 1f)) * (0.5f * GameSettings.LookSensitivity);
            else
            {
                // Pan toward whichever edge the cursor is pushed against.
                Vector2 m = Input.mousePosition;
                float bx = Screen.width * edgeBand, by = Screen.height * edgeBand;
                float ex = m.x < bx ? -(bx - m.x) / bx : m.x > Screen.width - bx ? (m.x - (Screen.width - bx)) / bx : 0f;
                float ey = m.y < by ? (by - m.y) / by : m.y > Screen.height - by ? -(m.y - (Screen.height - by)) / by : 0f;
                d = new Vector2(Mathf.Clamp(ex, -1f, 1f), Mathf.Clamp(ey, -1f, 1f)) * (edgeSpeed * udt * GameSettings.LookSensitivity); // a cursor outside the window pans at full speed, not faster
            }
            if (pad != null)
            {
                Vector2 st = pad.rightStick.ReadValue();
                if (st.sqrMagnitude > 0.04f) d += new Vector2(st.x, GameSettings.InvertY ? st.y : -st.y) * (130f * udt * GameSettings.LookSensitivity);
            }
        }
        if (d.sqrMagnitude < 1e-8f) return;
        d *= lookScale;
        lookYaw += d.x;
        lookPitchOff -= d.y;
    }

    // ------------------------------------------------------------------ camera

    void EnsureCamera()
    {
        if (cam != null) return;
        var go = new GameObject("CasinoSeatCam");
        go.transform.SetParent(transform, false);
        cam = go.AddComponent<CinemachineVirtualCamera>();
        cam.Priority = 0;
        cam.enabled = false;
    }

    void PoseCamera()
    {
        var s = Spot;
        if (cam == null || s == null || s.eye == null) return;
        Vector3 pos = s.eye.position;
        float fov = s.eyeFov;
        if (wLean > 0f && s.lean != null) { float k = Smooth(wLean); pos = Vector3.Lerp(pos, s.lean.position, k); fov = Mathf.Lerp(fov, s.leanFov, k); }
        if (wWatch > 0f && s.watch != null) { float k = Smooth(wWatch); pos = Vector3.Lerp(pos, s.watch.position, k); fov = Mathf.Lerp(fov, s.watchFov, k); }
        float yaw = BlendedAimYaw() + lookYaw;
        float pitch = FinalPitch;
        cam.transform.SetPositionAndRotation(pos, Quaternion.Euler(-pitch, yaw, 0f));
        var lens = cam.m_Lens;
        lens.FieldOfView = Mathf.Clamp(fov + GameSettings.FovDelta, 30f, 100f);
        lens.NearClipPlane = 0.04f;
        cam.m_Lens = lens;
    }

    // ------------------------------------------------------------------ cursor, focus, busy

    void ApplyCursor()
    {
        if (mode == CursorMode.Locked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        else { Cursor.lockState = CursorLockMode.Confined; Cursor.visible = true; }
    }

    void InstallFocus()
    {
        if (focusInstalled) return;
        prevFocus = PlayerController.InteractionFocusOverride;
        PlayerController.InteractionFocusOverride = p => (seated && p == player) || (prevFocus != null && prevFocus(p));
        focusInstalled = true;
        installedFocus = PlayerController.InteractionFocusOverride;
    }

    Func<PlayerController, bool> installedFocus;

    void UninstallFocus()
    {
        if (!focusInstalled) return;
        focusInstalled = false;
        if (PlayerController.InteractionFocusOverride == installedFocus) PlayerController.InteractionFocusOverride = prevFocus;
        prevFocus = null; installedFocus = null;
    }

    /// <summary>Last-ditch clean up when this object dies while seated.</summary>
    void CleanUpHard()
    {
        UninstallFocus();
        ShowBody();
        if (ownsBusy && CasinoStation.LocalBusy == busyStation) CasinoStation.LocalBusy = null;
        if (cc != null) cc.enabled = ccWasEnabled;
        if (tpc != null) tpc.enabled = tpcWasEnabled;
        if (!PlayerController.IsUIBlocking) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    }

    // ------------------------------------------------------------------ third-person camera alignment

    static FieldInfo tpcYawField, tpcPitchField;
    static bool tpcFieldsLooked;

    /// <summary>Face the body toward the table (already so) and put the third-person camera behind it, looking the same way.</summary>
    void AlignThirdPerson()
    {
        if (player == null) return;
        float worldYaw = player.transform.eulerAngles.y;
        if (tpc == null) return;
        if (!tpcFieldsLooked)
        {
            tpcFieldsLooked = true;
            var t = typeof(StarterAssets.ThirdPersonController);
            tpcYawField = t.GetField("_cinemachineTargetYaw", BindingFlags.NonPublic | BindingFlags.Instance);
            tpcPitchField = t.GetField("_cinemachineTargetPitch", BindingFlags.NonPublic | BindingFlags.Instance);
        }
        const float pitch3P = 12f;
        try
        {
            if (tpcYawField != null && tpcYawField.FieldType == typeof(float)) tpcYawField.SetValue(tpc, worldYaw);
            if (tpcPitchField != null && tpcPitchField.FieldType == typeof(float)) tpcPitchField.SetValue(tpc, pitch3P);
        }
        catch (Exception e) { Debug.LogWarning("[CasinoSeat] Couldn't align the third-person camera: " + e.Message); }
        if (tpc.CinemachineCameraTarget != null)
            tpc.CinemachineCameraTarget.transform.rotation = Quaternion.Euler(pitch3P + tpc.CameraAngleOverride, worldYaw, 0f);
    }

    // ------------------------------------------------------------------ blend hook (this camera only)

    static CinemachineCore.GetBlendOverrideDelegate prevBlendOverride;
    static bool blendHookInstalled;

    static void InstallBlendHook()
    {
        if (blendHookInstalled) return;
        prevBlendOverride = CinemachineCore.GetBlendOverride;
        CinemachineCore.GetBlendOverride = BlendOverride;
        blendHookInstalled = true;
    }

    static CinemachineBlendDefinition BlendOverride(ICinemachineCamera from, ICinemachineCamera to, CinemachineBlendDefinition blend, MonoBehaviour owner)
    {
        if (prevBlendOverride != null) blend = prevBlendOverride(from, to, blend, owner);
        var s = Instance;
        if (s == null || s.cam == null || !(owner is CinemachineBrain)) return blend;
        ICinemachineCamera mine = s.cam;
        if (to == mine) return new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, s.glideSeconds);
        if (from == mine) return s.exitImmediate
            ? new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.Cut, 0f)
            : new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, s.glideSeconds);
        return blend;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        if (blendHookInstalled && CinemachineCore.GetBlendOverride == (CinemachineCore.GetBlendOverrideDelegate)BlendOverride)
            CinemachineCore.GetBlendOverride = prevBlendOverride;
        prevBlendOverride = null;
        blendHookInstalled = false;
        tpcFieldsLooked = false;
    }
}

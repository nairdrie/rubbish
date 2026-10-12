using System.Collections.Generic;
using System.Reflection;
using StarterAssets;
using UnityEngine;

/// <summary>
/// A player riding a truck (package F1, Docs/Multiplayer.md §14.1, §4.4), added at runtime to every player copy (solo
/// and co-op; no prefab edit). Riders stay on solidly (Nick, 12 Oct 2026: no grip meter, no lean, no HOLD ON, no shoving):
/// <list type="bullet">
/// <item><b>Crash fling</b>: only a heavy hit throws you off. <see cref="RideTruck"/>'s sensor reports it from the machine
/// that simulates the truck; the rider's OWN machine applies it when the truck it shows reaches that tick (the truck's Safety
/// Bar and Padded Step raise the Δv it takes) and decides the fling; every other machine plays the fling from the same truck
/// tick and owner's world start (<see cref="HandsNet"/> carries the event). Launched along the truck's momentum, flailing;
/// AIR TIME readout and a personal best (<see cref="RideStats"/>). Hedges catch, trampolines bounce, props and pedestrians
/// go over.</item>
/// <item><b>Switch spot</b> (Shift / RB): shuffle round the back to the next free hold (left ladder → rear-left step →
/// rear-right step → right ladder → round again). Every machine walks the rider along the back of its own copy of the truck
/// (<see cref="HoldPose"/>).</item>
/// <item><b>Drive-by grabs</b>: under 2.5 m/s, LMB snags a curb bag within reach and lobs it into the hopper; snags in a
/// row build a STREAK (small pay bonus to the truck's business).</item>
/// <item><b>Solo</b> (no co-op session): this component pins, locks and flies the local player itself (in co-op
/// <see cref="PlayerBody"/> does the pinning). [C] starts a short, low-speed road cruise from an ordinary parked truck.</item>
/// </list>
/// </summary>
[DefaultExecutionOrder(10)]
[DisallowMultipleComponent]
public class RideRider : MonoBehaviour
{
    static readonly List<RideRider> all = new List<RideRider>();
    static readonly Dictionary<ulong, double> seenFlingTicks = new Dictionary<ulong, double>();
    public static IReadOnlyList<RideRider> All => all;
    /// <summary>This machine's own rider (camera, HUD, input only).</summary>
    public static RideRider LocalRider { get; private set; }

    public static RideRider For(ulong client)
    {
        for (int i = 0; i < all.Count; i++)
        {
            var r = all[i];
            if (r != null && r.Player != null && r.ClientId == client) return r;
        }
        return null;
    }

    public PlayerController Player { get; private set; }
    public ulong ClientId => Player != null && Player.IsSpawned ? Player.OwnerClientId : 0;
    public bool IsLocal => Player != null && CoopPlayerHooks.IsLocalOwner(Player);

    TrashCarrier carrier;
    CharacterController cc;
    ThirdPersonController tpc;
    StarterAssetsInputs input;
    Animator anim;
    Transform skeleton;
    Quaternion skelRot;
    Vector3 skelPos;
    bool skelTouched;
    PlayerBody body;

    static HandsNet Net => HandsNet.Instance;
    static bool NetMode => HandsNet.Instance != null && HandsNet.Instance.Active;
    /// <summary>Co-op's body (added at runtime by HandsNet, maybe after this): its control lock owns the camera there.</summary>
    PlayerBody Body => body != null ? body : (body = GetComponent<PlayerBody>());

    void Awake()
    {
        Player = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
        cc = GetComponent<CharacterController>();
        tpc = GetComponent<ThirdPersonController>();
        input = GetComponent<StarterAssetsInputs>();
        anim = GetComponent<Animator>();
        skeleton = transform.Find("Skeleton");
        if (skeleton != null) { skelRot = skeleton.localRotation; skelPos = skeleton.localPosition; }
        if (yawField == null)
        {
            var t = typeof(ThirdPersonController);
            yawField = t.GetField("_cinemachineTargetYaw", BindingFlags.NonPublic | BindingFlags.Instance);
            pitchField = t.GetField("_cinemachineTargetPitch", BindingFlags.NonPublic | BindingFlags.Instance);
        }
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }

    void OnDisable()
    {
        RestoreRideCamera();
        all.Remove(this);
        if (LocalRider == this) LocalRider = null;
        if (soloTruck != null) soloTruck.StopSoloCruise(this);
        if (soloLocked) SoloLock(false);
        RestoreSkeleton();
        SetHint(null);
    }

    // ==================================================================================== where am I

    /// <summary>The truck and hold this player rides (this machine's view). False while flying.</summary>
    public bool TryGetRide(out RideTruck rt, out int hold)
    {
        rt = null;
        hold = -1;
        if (Player == null || flying) return false;
        var net = Net;
        if (net != null && net.Active)
        {
            var b = net.BodyOf(ClientId);
            if (b.State != BodyState.Riding) return false;
            var hs = HitchStep.Find(b.other);
            if (hs == null) return false;
            rt = RideTruck.For(hs.Truck);
            hold = b.step;
            return rt != null;
        }
        if (soloRiding && soloTruck != null) { rt = soloTruck; hold = soloHold; return true; }
        return false;
    }

    public bool IsRiding => TryGetRide(out _, out _);
    public bool IsFlying => flying;
    /// <summary>Riding <paramref name="hs"/>'s truck.</summary>
    public bool IsRidingTruck(HitchStep hs) => hs != null && TryGetRide(out var rt, out _) && rt.Truck == hs.Truck;

    /// <summary>Who rides hold <paramref name="hold"/> of <paramref name="truck"/> (None = free).</summary>
    public static ulong RiderAt(RideTruck truck, int hold)
    {
        var net = Net;
        if (net != null && net.Active) return truck != null && truck.NetId != 0 ? net.RiderOn(truck.NetId, hold) : HandsNet.None;
        var me = LocalRider;
        return me != null && me.soloRiding && me.soloTruck == truck && me.soloHold == hold ? me.ClientId : HandsNet.None;
    }

    /// <summary>The holds of <paramref name="truck"/> somebody stands on (a bit per hold), leaving out <paramref name="except"/>.</summary>
    public static int TakenHolds(RideTruck truck, int except = -1)
    {
        int mask = 0;
        for (int k = 0; k < RideHolds.Count; k++)
            if (k != except && RiderAt(truck, k) != HandsNet.None) mask |= 1 << k;
        return mask;
    }

    /// <summary>The local player may climb on (hands free of friends, on their feet, not driving or flying).</summary>
    public bool CanBoard
    {
        get
        {
            if (Player == null || Player.IsDriving || Player.IsPlayingChess || flying || soloKoUntil > 0f) return false;
            var net = Net;
            if (net != null && net.Active)
            {
                var b = net.BodyOf(ClientId);
                return b.State == BodyState.Normal && b.carrying == HandsNet.None;
            }
            return !soloRiding;
        }
    }

    // ==================================================================================== solo hitching

    bool soloRiding;
    RideTruck soloTruck;
    int soloHold;
    float soloKoUntil;

    public void Hitch(HitchStep hs, int hold)
    {
        if (hs == null || hold < 0 || hold >= RideHolds.Count) return;
        AimCameraFor(hs.Truck != null ? hs.Truck.transform : null, hold);
        var net = Net;
        if (net != null && net.Active) { net.LocalHitch(hs, hold); return; }
        var rt = RideTruck.Ensure(hs.Truck);
        if (rt == null) return;
        soloRiding = true;
        soloTruck = rt;
        soloHold = hold;
        SoloLock(true);
        Sfx.Play("clang", transform.position, 0.6f, 1.2f);
        GameHUD.FloatingText(transform.position + Vector3.up * 2.1f, "All aboard!", new Color(1f, 0.85f, 0.4f), 1f);
        Audit("hitch", rt, hold, null);
    }

    public void Unhitch()
    {
        var net = Net;
        if (net != null && net.Active) { net.LocalUnhitch(); return; }
        if (!soloRiding) return;
        var rt = soloTruck;
        int hold = soloHold;
        if (rt != null) rt.StopSoloCruise(this);
        soloRiding = false;
        soloTruck = null;
        if (rt != null)
        {
            var pose = RideHolds.PoseOn(rt.transform, hold);
            Vector3 right = pose.rotation * Vector3.right;
            Vector3 off = RideHolds.IsRung(hold) ? right * (RideHolds.Side(hold) * 0.75f) : -(pose.rotation * Vector3.forward) * 0.7f;
            transform.SetPositionAndRotation(pose.position + off + Vector3.up * 0.1f, pose.rotation);
            float sp = rt.Speed;
            if (sp > HandsTuning.HopOffSafeSpeed)
                GameHUD.FloatingText(transform.position + Vector3.up * 2f, sp > 6f ? "TUMBLE!" : "whoa", new Color(1f, 0.7f, 0.3f), 1f);
        }
        SoloLock(false);
        Sfx.Play("thud", transform.position, 0.5f, 1.2f);
    }

    /// <summary>Stop a fling's residual flight when a host-authorized compactor takes over this player.</summary>
    internal void ResetForCompactor()
    {
        if (IsLocal) Unhitch();
        flying = false;
        remotePending = false;
        remoteRt = null;
        flight = null;
        flightLandedAt = -9f;
        flingAwaitUntil = 0f;
        soloKoUntil = 0f;
        shuffleHold = -1;
        ShuffleWeight = 0f;
        cameraSwing = 0f;
        RestoreRideCamera();
        RestoreSkeleton();
        if (soloLocked) SoloLock(false);
    }

    /// <summary>Point the camera along the truck, angled a little toward its body on a side rung so the camera swings
    /// out beside the truck instead of into its flank (the lock picks this yaw up when it engages).</summary>
    void AimCameraFor(Transform truck, int hold)
    {
        if (!IsLocal || truck == null || tpc == null || yawField == null) return;
        float yaw = truck.eulerAngles.y + RideHolds.ViewYaw(hold);
        yawField.SetValue(tpc, yaw);
        lockYaw = yaw;
    }

    // ---- solo control lock (keeps mouse look), the same trick PlayerBody uses in co-op

    static FieldInfo yawField, pitchField;
    bool soloLocked;
    float lockYaw, lockPitch;

    // The riding camera follows a smoothed world-space proxy, so interpolation jitter from a remote truck does not
    // become an equally sharp camera jolt. This only affects the local view; it never changes a player/truck pose.
    Transform rideCameraProxy, rideCameraOriginalFollow, rideCameraOriginalLookAt;

    void RideCameraTick(float dt)
    {
        if (!IsLocal || Player == null || Player.playerCamVCam == null || tpc == null ||
            tpc.CinemachineCameraTarget == null || !TryGetRide(out _, out _))
        {
            RestoreRideCamera();
            return;
        }
        var vcam = Player.playerCamVCam;
        var target = tpc.CinemachineCameraTarget.transform;
        if (rideCameraProxy == null)
        {
            rideCameraOriginalFollow = vcam.Follow;
            rideCameraOriginalLookAt = vcam.LookAt;
            rideCameraProxy = new GameObject("Ride Camera Damping (local)").transform;
            rideCameraProxy.SetPositionAndRotation(target.position, target.rotation);
            vcam.Follow = rideCameraProxy;
            vcam.LookAt = rideCameraProxy;
        }
        if ((rideCameraProxy.position - target.position).sqrMagnitude > 12.25f)
            rideCameraProxy.SetPositionAndRotation(target.position, target.rotation);
        else
        {
            rideCameraProxy.position = Vector3.Lerp(rideCameraProxy.position, target.position,
                1f - Mathf.Exp(-dt / 0.08f));
            rideCameraProxy.rotation = Quaternion.Slerp(rideCameraProxy.rotation, target.rotation,
                1f - Mathf.Exp(-dt / 0.055f));
        }
    }

    void RestoreRideCamera()
    {
        if (rideCameraProxy == null) return;
        if (Player != null && Player.playerCamVCam != null)
        {
            var vcam = Player.playerCamVCam;
            if (vcam.Follow == rideCameraProxy) vcam.Follow = rideCameraOriginalFollow;
            if (vcam.LookAt == rideCameraProxy) vcam.LookAt = rideCameraOriginalLookAt;
        }
        Destroy(rideCameraProxy.gameObject);
        rideCameraProxy = rideCameraOriginalFollow = rideCameraOriginalLookAt = null;
    }

    void SoloLock(bool on)
    {
        if (on == soloLocked) return;
        soloLocked = on;
        if (on)
        {
            if (tpc != null)
            {
                lockYaw = yawField != null ? (float)yawField.GetValue(tpc) : transform.eulerAngles.y;
                lockPitch = pitchField != null ? (float)pitchField.GetValue(tpc) : 0f;
                tpc.enabled = false;
            }
            if (cc != null) cc.enabled = false;
            if (input != null) { input.move = Vector2.zero; input.jump = false; input.sprint = false; }
            if (anim != null)
            {
                anim.SetFloat("Speed", 0f);
                anim.SetFloat("MotionSpeed", 1f);
                anim.SetBool("Grounded", true);
                anim.SetBool("Jump", false);
                anim.SetBool("FreeFall", false);
            }
            return;
        }
        if (cc != null) cc.enabled = true;
        if (tpc != null)
        {
            if (yawField != null) yawField.SetValue(tpc, lockYaw);
            if (pitchField != null) pitchField.SetValue(tpc, lockPitch);
            tpc.enabled = true;
        }
    }

    void SoloLockedLook()
    {
        if (!soloLocked || tpc == null || input == null || tpc.CinemachineCameraTarget == null) return;
        if (input.look.sqrMagnitude >= 0.01f && !PlayerController.IsUIBlocking)
        {
            lockYaw += input.look.x;
            lockPitch = Mathf.Clamp(lockPitch + input.look.y, tpc.BottomClamp, tpc.TopClamp);
        }
        tpc.CinemachineCameraTarget.transform.rotation = Quaternion.Euler(lockPitch + tpc.CameraAngleOverride, lockYaw, 0f);
        input.move = Vector2.zero;
        input.jump = false;
    }

    // ==================================================================================== the rider's own machine

    /// <summary>The position of this truck's shared Safety Bar (not a per-rider switch).</summary>
    public bool BarDown => TryGetRide(out var rt, out _) && rt != null ? rt.BarDown : true;

    RideTruck crashTruck;
    int crashCursor;
    readonly HashSet<int> consumedCrashes = new HashSet<int>();
    readonly List<RideTruck.Crash> tmpCrashes = new List<RideTruck.Crash>(4);

    // feedback for the HUD
    public string Feedback { get; private set; }
    public Color FeedbackColor { get; private set; }
    public float FeedbackUntil { get; private set; }

    // bots
    bool botSnag;
    public void BotSnag() => botSnag = true;

    public const byte FlagCrash = 4, FlagKnock = 8;

    void Update()
    {
        if (Player == null) return;
        if (!IsLocal) return;
        if (LocalRider != this) LocalRider = this;
        OwnerUpdate();
    }

    void OwnerUpdate()
    {
        bool ui = PlayerController.IsUIBlocking;
        var netNow = Net;
        if (flingAwaitUntil > 0f && netNow != null && netNow.Active && netNow.BodyOf(ClientId).State != BodyState.Riding) flingAwaitUntil = 0f;
        if (soloRiding && (soloTruck == null || NetMode))
        {
            if (soloTruck != null) soloTruck.StopSoloCruise(this);
            soloRiding = false; soloTruck = null;
            if (!flying) SoloLock(false);
        }
        StepCameraSwing(Time.deltaTime);
        if (flying) { SetHint(null); return; }
        if (soloKoUntil > 0f)
        {
            SetHint(null);
            if (Time.time >= soloKoUntil) { soloKoUntil = 0f; SoloLock(false); RestoreSkeleton(); }
            return;
        }
        if (!TryGetRide(out var rt, out int hold))
        {
            if (crashTruck != null) { crashTruck = null; consumedCrashes.Clear(); }
            switchTo = -1;
            SnagReset();
            SetHint(null);
            return;
        }
        if (rt != crashTruck)
        {
            // Only crashes after climbing on count.
            crashTruck = rt;
            crashCursor = rt.LastCrashSerial;
            consumedCrashes.Clear();
        }

        // ---- crashes the truck took, when this machine shows it taking them
        tmpCrashes.Clear();
        rt.CrashesSince(crashCursor, rt.DisplayTime, consumedCrashes, tmpCrashes);
        for (int i = 0; i < tmpCrashes.Count; i++)
        {
            var c = tmpCrashes[i];
            if (c.dv >= RideTuning.CrashFlingDv(rt.PaddedLevel, rt.BarLevel, rt.BarDown))
            {
                Fling(rt, hold, Vector3.zero, HandsNet.None, FlagCrash);
                return;
            }
            HeldOn(rt, hold, c.dv);
        }

        // ---- keys
        if (!ui && SwitchPressed()) SwitchSpot();
        if (!ui && rt.BarLevel > 0 && GameControls.Down(KeyCode.B, GameControls.Action.SafetyBar)) ToggleBar();
        if (!ui && !NetMode && GameControls.Down(KeyCode.C, GameControls.Action.Cruise))
        {
            if (rt.SoloCruising)
            {
                rt.StopSoloCruise(this);
                Say("Cruise parked", new Color(0.75f, 0.9f, 1f), 1.2f);
            }
            else if (rt.StartSoloCruise(this, out string why))
                Say("SOLO CRUISE!", new Color(0.55f, 1f, 0.75f), 1.2f);
            else GameHUD.Toast(why, ToastStyle.Info, 1.8f);
        }
        // Co-op's Space hop-off is PlayerBody's (E is HitchStep's "Hop off" in both).
        if (!ui && !NetMode && GameControls.Down(KeyCode.Space, GameControls.Action.Jump)) { Unhitch(); return; }

        SnagTick(rt, hold, ui);
        if (!NetMode) SetHint(HintText(rt));
    }

    /// <summary>Request the whole crew's Safety Bar to fold (B; bots and tests call it directly).</summary>
    public void ToggleBar()
    {
        if (!TryGetRide(out var rt, out _) || rt == null) return;
        bool down = !rt.BarDown;
        if (!rt.ToggleBar(this)) return;
        Say(down ? "Safety bar DOWN" : "Safety bar UP (risky!)", down ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.5f, 0.3f), 1.2f);
        Sfx.Play("clang", transform.position, 0.5f, down ? 0.9f : 1.3f);
    }

    /// <summary>A crash this truck's Safety Bar or Padded Step soaked up: a jolt and a word, and the rider stays on.</summary>
    void HeldOn(RideTruck rt, int hold, float dv)
    {
        bool bar = rt.BarLevel > 0 && rt.BarDown;
        Say(bar ? "The Safety Bar held!" : "The padding held!", new Color(1f, 0.85f, 0.2f), 1.1f);
        GameHUD.FloatingText(transform.position + Vector3.up * 2.2f, "WHAM!", new Color(1f, 0.75f, 0.3f), 0.9f);
        Juice.Shake(0.22f, 0.2f);
        Sfx.Play("clang", transform.position, 0.6f, 0.8f);
        Audit("crashheld", rt, hold, new Dictionary<string, object>
        {
            { "dv", System.Math.Round(dv, 2) }, { "bar", bar }, { "pad", rt.PaddedLevel },
        });
    }

    void Say(string text, Color c, float secs)
    {
        Feedback = text;
        FeedbackColor = c;
        FeedbackUntil = Time.unscaledTime + secs;
    }

    /// <summary>A knock-off from outside (<see cref="Riding.KnockOff"/>). Only for this machine's own rider.</summary>
    public void ApplyKnock(Vector3 push, string word, ulong cause)
    {
        if (!IsLocal || !TryGetRide(out var rt, out int hold)) return;
        if (!string.IsNullOrEmpty(word)) GameHUD.FloatingText(transform.position + Vector3.up * 2.3f, word, new Color(1f, 0.6f, 0.3f), 1f);
        Fling(rt, hold, push, cause, FlagKnock);
    }

    // ==================================================================================== switching spots

    float lastSwitchAt = -9f, switchAskedAt = -9f;
    int switchTo = -1;

    static bool SwitchPressed() =>
        GameControls.Down(Keybinds.Defaults.SwitchSpot, GameControls.Action.SwitchSpot) || Input.GetKeyDown(KeyCode.RightShift);

    /// <summary>Move to the next free hold round the back (Shift / RB; bots and tests call it directly): left ladder →
    /// rear-left step → rear-right step → right ladder → round again, skipping taken holds. Co-op asks the host, which checks
    /// the hold is still free (two riders never share one); solo moves at once. False when nothing is free.</summary>
    public bool SwitchSpot()
    {
        if (!IsLocal || !TryGetRide(out var rt, out int hold) || rt == null) return false;
        if (Time.time - lastSwitchAt < RideTuning.SwitchCooldown) return false;
        // Pressed again before the host's answer arrived: carry on round from the hold already asked for (which may lead
        // back to the one we still stand on: that's free once we've moved).
        if (switchTo >= 0 && (switchTo == hold || Time.time - switchAskedAt > 1f)) switchTo = -1;
        int from = switchTo >= 0 ? switchTo : hold;
        int next = RideHolds.NextFreeHold(from, TakenHolds(rt, hold));
        if (next < 0)
        {
            Say("No free spot!", new Color(1f, 0.6f, 0.4f), 0.9f);
            Sfx.Play("buzz", transform.position, 0.4f, 1.2f);
            return false;
        }
        lastSwitchAt = Time.time;
        var net = Net;
        if (net != null && net.Active)
        {
            net.LocalRideSwitch(rt.NetId, (byte)next);
            switchTo = next;
            switchAskedAt = Time.time;
        }
        else soloHold = next;
        Audit("switch", rt, next, new Dictionary<string, object> { { "from", hold } });
        return true;
    }

    // Every machine walks a switching rider along the back of its own copy of the truck (truck metres, so they ride the
    // truck exactly as when pinned); the replicated hold (BodyNet.step / soloHold) only says where they're going.
    Transform shuffleTruck;
    int shuffleHold = -1, shuffleFrame = -9;
    float shuffleFromS, shuffleToS, shuffleStart, shuffleDur, cameraSwing;

    /// <summary>0 settled on a hold, up to 1 halfway through a switch (the puppet lets go of the rails meanwhile).</summary>
    public float ShuffleWeight { get; private set; }

    /// <summary>Where this rider stands on <paramref name="hold"/> of <paramref name="truck"/> right now (PlayerBody pins
    /// co-op riders here, this component solo ones): the hold's pose, or a point on the short shuffle round the back after a
    /// switch. Boarding, a new truck, or a gap in calls snaps straight to the hold.</summary>
    public Pose HoldPose(Transform truck, int hold)
    {
        var settled = RideHolds.PoseOn(truck, hold);
        bool fresh = truck != shuffleTruck || shuffleHold < 0 || Time.frameCount - shuffleFrame > 2;
        shuffleFrame = Time.frameCount;
        if (fresh)
        {
            shuffleTruck = truck;
            shuffleHold = hold;
            shuffleDur = 0f;
            ShuffleWeight = 0f;
            return settled;
        }
        if (hold != shuffleHold)
        {
            float from = ShuffleS(), to = RideHolds.PathS(hold);
            // The camera swings out beside the truck for a side ladder, as boarding aims it (your own look offset stays).
            if (IsLocal) cameraSwing += Mathf.DeltaAngle(RideHolds.ViewYaw(shuffleHold), RideHolds.ViewYaw(hold));
            shuffleFromS = from;
            shuffleToS = to;
            shuffleStart = Time.time;
            shuffleDur = Mathf.Min(RideTuning.ShuffleMax, RideTuning.ShuffleMin + RideTuning.ShufflePerMetre * Mathf.Abs(to - from));
            shuffleHold = hold;
        }
        if (shuffleDur <= 0f) return settled;
        float u = (Time.time - shuffleStart) / shuffleDur;
        if (u >= 1f)
        {
            shuffleDur = 0f;
            ShuffleWeight = 0f;
            return settled;
        }
        ShuffleWeight = Mathf.Sin(u * Mathf.PI);
        Vector3 at = RideHolds.PathPoint(Mathf.Lerp(shuffleFromS, shuffleToS, u * u * (3f - 2f * u))) +
                     Vector3.up * (ShuffleWeight * RideTuning.ShuffleHop);
        return new Pose(RideHolds.Point(truck, at), settled.rotation);
    }

    float ShuffleS()
    {
        if (shuffleDur <= 0f) return RideHolds.PathS(shuffleHold);
        float u = Mathf.Clamp01((Time.time - shuffleStart) / shuffleDur);
        return Mathf.Lerp(shuffleFromS, shuffleToS, u * u * (3f - 2f * u));
    }

    /// <summary>Ease the local camera through a switch's yaw change (solo: our lock; co-op: PlayerBody's).</summary>
    void StepCameraSwing(float dt)
    {
        if (Mathf.Abs(cameraSwing) < 0.01f) { cameraSwing = 0f; return; }
        float step = cameraSwing * Mathf.Min(1f, dt * 6f);
        cameraSwing -= step;
        if (soloLocked) lockYaw += step;
        else if (Body != null) Body.AddLockYaw(step);
    }

    // ==================================================================================== the fling

    bool flying, flightMine, flightSoft;
    RideFlight flight;
    float flightAcc;
    Vector3 flightFrom;
    ulong flightCause = HandsNet.None;
    byte flightFlags;
    float flightLandedAt = -9f;

    // a fling another machine announced, waiting for this machine's truck to reach its tick
    bool remotePending;
    RideTruck remoteRt;
    int remoteHold;
    double remoteTF;
    Vector3 remoteStart, remoteVel;
    ulong remoteCause;
    byte remoteFlags;

    void Fling(RideTruck rt, int hold, Vector3 push, ulong cause, byte flags)
    {
        double tF = rt.DisplayTime;
        Transform tt = rt.transform;
        // From where this rider stands right now (mid-switch too); the host bounds it against the hold.
        Vector3 start = HoldPose(tt, hold).position + Vector3.up * 0.15f;
        Vector3 right = tt.right; right.y = 0f; right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
        Vector3 flatPush = new Vector3(push.x, 0f, push.z);
        Vector3 outward = flatPush.sqrMagnitude > 0.01f ? flatPush.normalized : right * RideHolds.Side(hold);
        Vector3 tv = rt.Velocity;
        Vector3 vel = tv * RideTuning.FlingKeep + outward * (RideTuning.FlingOut + flatPush.magnitude * 0.5f) + Vector3.up * RideTuning.FlingUp;
        if (vel.magnitude > 28f) vel = vel.normalized * 28f;

        // Whatever we were holding flies too.
        if (carrier != null && carrier.IsCarrying && !carrier.ExternalControl)
        {
            var held = carrier.Held;
            carrier.Drop();
            if (held != null && held.Body != null && !held.Body.isKinematic) held.Body.linearVelocity = vel * 0.9f;
        }
        SnagReset();
        switchTo = -1;

        var net = Net;
        if (net != null && net.Active) { net.LocalRideFling(rt.NetId, (byte)hold, tF, start, vel, flags, cause); flingAwaitUntil = Time.time + 3f; }
        else { rt.StopSoloCruise(this); soloRiding = false; soloTruck = null; }
        rt.NoteFling();
        StartFlight(start, vel, true, flags, cause);
        GameHUD.FloatingText(start + Vector3.up * 2.2f, "WAAAAH!", new Color(1f, 0.5f, 0.25f), 1.3f);
        Juice.Shake(0.3f, 0.3f);
        Audit("fling", rt, hold, new Dictionary<string, object>
        {
            { "tF", System.Math.Round(tF, 4) }, { "start", V(start) }, { "truckPos", V(tt.position) }, { "vel", V(vel) },
            { "flags", flags }, { "cause", cause == HandsNet.None ? -1L : (long)cause }, { "lag", System.Math.Round(rt.Lag, 3) }, { "matches", rt.LagMatches },
        });
    }

    void StartFlight(Vector3 start, Vector3 vel, bool mine, byte flags, ulong cause)
    {
        flight = new RideFlight(start, vel, true);
        flying = true;
        flightMine = mine;
        flightSoft = false;
        flightAcc = 0f;
        flightFrom = start;
        flightFlags = flags;
        flightCause = cause;
        if (mine)
        {
            if (cc != null) cc.enabled = false;
            if (!NetMode) SoloLock(true);
            if (cc != null) cc.enabled = false;
        }
        Sfx.Play("whoosh", start, 0.9f, 0.75f);
    }

    /// <summary>A fling event (every machine, from the host): present another player's fling.</summary>
    public static void OnFlungEvent(ulong rider, ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        var rt = RideTruck.Find(truckId);
        var r = For(rider);
        if (r == null) return;
        if (seenFlingTicks.TryGetValue(rider, out double last) && System.Math.Abs(last - tF) < 0.0001) return;
        seenFlingTicks[rider] = tF;
        if (r.IsLocal) return;
        if (rt != null) rt.NoteFling();
        r.ScheduleRemoteFling(rt, hold, tF, worldStart, vel, flags, cause);
    }

    void ScheduleRemoteFling(RideTruck rt, int hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        remotePending = true;
        remoteRt = rt;
        remoteHold = hold;
        remoteTF = tF;
        remoteStart = worldStart;
        remoteVel = vel;
        remoteFlags = flags;
        remoteCause = cause;
        if (rt == null) StartRemoteFlight();
    }

    void StartRemoteFlight()
    {
        remotePending = false;
        Vector3 start = remoteStart;
        double late = 0;
        Vector3 truckAt = Vector3.zero;
        if (remoteRt != null)
        {
            if (remoteRt.TryPoseAtDriver(remoteTF, out var p, out _)) truckAt = p;
            else truckAt = remoteRt.transform.position;
            late = remoteRt.DisplayTime - remoteTF;
        }
        StartFlight(start, remoteVel, false, remoteFlags, remoteCause);
        if (late > 0.0) flight.FastForward((float)late);
        Audit("flingseen", remoteRt, remoteHold, new Dictionary<string, object>
        {
            { "rider", (long)ClientId }, { "tF", System.Math.Round(remoteTF, 4) }, { "start", V(start) }, { "truckPos", V(truckAt) },
            { "late", System.Math.Round(late, 3) }, { "lag", remoteRt != null ? System.Math.Round(remoteRt.Lag, 3) : -1.0 },
            { "matches", remoteRt != null ? remoteRt.LagMatches : -1 }, { "flags", remoteFlags },
        });
    }

    void LateUpdate()
    {
        if (Player == null) return;
        float dt = Time.deltaTime;

        // Solo: stand on the hold of this machine's copy of the truck.
        if (IsLocal && soloRiding && soloTruck != null && !flying)
        {
            var pose = HoldPose(soloTruck.transform, soloHold);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (cc != null && cc.enabled) cc.enabled = false;
        }
        if (IsLocal && soloLocked) SoloLockedLook();

        // Someone else's fling: stay on the step until this machine shows the truck at the fling's tick.
        if (remotePending)
        {
            if (remoteRt == null) StartRemoteFlight();
            else if (remoteRt.DisplayTime < remoteTF)
            {
                var pose = HoldPose(remoteRt.transform, remoteHold);
                transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
            else StartRemoteFlight();
        }

        if (flying) StepFlight(dt);
        else if (flightLandedAt > 0f && !flightMine && Time.time - flightLandedAt < 0.2f && flight != null)
        {
            // Hold the landing spot a moment while the network catches up.
            transform.position = Vector3.Lerp(flight.Pos, transform.position, (Time.time - flightLandedAt) / 0.2f);
        }

        UpdateSkeleton();
        RideCameraTick(dt);
    }

    void StepFlight(float dt)
    {
        flightAcc += dt;
        int guard = 0;
        while (flightAcc >= RideTuning.FlightDt && !flight.Landed && guard++ < 12)
        {
            flightAcc -= RideTuning.FlightDt;
            flight.Step();
            if (flight.Event != null)
            {
                GameHUD.FloatingText(flight.Pos + Vector3.up * 2f, flight.Event, new Color(0.6f, 1f, 0.6f), 1.1f);
                Sfx.Play(flight.Event == "BOING!" ? "pop" : flight.Event == "SPLAT!" ? "splat" : "rustle", flight.Pos, 0.8f, 1f);
            }
        }
        float f = Mathf.Clamp01(flightAcc / RideTuning.FlightDt);
        Vector3 p = flight.Landed ? flight.Pos : Vector3.Lerp(flight.PrevPos, flight.Pos, f);
        Vector3 flat = new Vector3(flight.Vel.x, 0f, flight.Vel.z);
        Quaternion rot = flat.sqrMagnitude > 0.05f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : transform.rotation;
        transform.SetPositionAndRotation(p, rot);
        if (flight.Landed) Land();
    }

    void Land()
    {
        flying = false;
        flightLandedAt = Time.time;
        flightSoft = flight.Soft;
        Vector3 d = flight.Pos - flightFrom; d.y = 0f;
        float metres = d.magnitude, secs = flight.T;
        Sfx.Play(flightSoft ? "rustle" : "thud", flight.Pos, 0.9f, 0.85f);
        Fx.Dust(flight.Pos, 1.3f);
        RestoreSkeleton();
        if (flightMine)
        {
            if (cc != null) cc.enabled = true;
            Juice.Shake(0.25f, 0.2f);
            bool best = RideStats.RecordFling(metres, secs);
            LastAirM = metres;
            LastAirS = secs;
            LastAirBest = best;
            AirShownAt = Time.unscaledTime;
            var net = Net;
            if (net != null && net.Active) net.LocalRideLanded(flightSoft);
            else if (!flightSoft) soloKoUntil = Time.time + RideTuning.KnockedOut;
            else SoloLock(false);
            Audit("land", null, -1, new Dictionary<string, object>
            {
                { "metres", System.Math.Round(metres, 2) }, { "secs", System.Math.Round(secs, 2) }, { "soft", flightSoft }, { "best", best },
                { "knocks", flight.Knocks }, { "people", flight.People }, { "bounces", flight.Bounces }, { "at", V(flight.Pos) },
            });
        }
        GameHUD.FloatingText(flight.Pos + Vector3.up * 2.4f, $"AIR TIME {metres:0.0} m!", new Color(0.55f, 0.9f, 1f), flightMine ? 1.4f : 1.1f);
        RideEvents.RaiseFlung(Player, metres, secs);
    }

    public float LastAirM { get; private set; }
    public float LastAirS { get; private set; }
    public bool LastAirBest { get; private set; }
    public float AirShownAt { get; private set; } = -99f;

    // ==================================================================================== look: flail and knocked out (the riding brace and rail IK live in ChuckPuppet)

    void UpdateSkeleton()
    {
        if (skeleton == null) return;
        if (flying)
        {
            float t = flight != null ? flight.T : 0f;
            var r = Quaternion.Euler(-70f + Mathf.Sin(t * 9f) * 25f + t * 600f, Mathf.Sin(t * 13f) * 30f, Mathf.Sin(t * 17f) * 35f);
            skeleton.localRotation = r * skelRot;
            skeleton.localPosition = skelPos + Vector3.up * 0.3f;
            skelTouched = true;
            return;
        }
        if (IsLocal && soloKoUntil > 0f)
        {
            skeleton.localRotation = Quaternion.Euler(-82f, 0f, 0f) * skelRot;
            skeleton.localPosition = skelPos + Vector3.up * 0.22f;
            skelTouched = true;
            return;
        }
        RestoreSkeleton();
    }

    void RestoreSkeleton()
    {
        if (!skelTouched || skeleton == null) return;
        skelTouched = false;
        skeleton.localRotation = skelRot;
        skeleton.localPosition = skelPos;
    }

    // ==================================================================================== drive-by grabs

    Carryable snag;
    float snagAt, snagTossAt, lastSnagDoneAt = -99f;
    bool snagTossed;
    /// <summary>A bag in reach right now (HUD prompt).</summary>
    public Carryable SnagCandidate { get; private set; }
    public int Streak { get; private set; }
    public float StreakShownAt { get; private set; } = -99f;
    public float StreakPay { get; private set; }

    void SnagReset()
    {
        snag = null;
        snagTossed = false;
        SnagCandidate = null;
    }

    void SnagTick(RideTruck rt, int hold, bool ui)
    {
        if (Streak > 0 && Time.time - lastSnagDoneAt > RideTuning.StreakGap) Streak = 0;
        if (snag != null || snagTossed) { SnagProgress(rt); return; }
        SnagCandidate = null;
        bool want = botSnag || (!ui && (Input.GetMouseButtonDown(0) || GameControls.PadDown(GameControls.Action.Snag)));
        botSnag = false;
        if (rt.Speed > RideTuning.SnagMaxSpeed || carrier == null || carrier.IsCarrying || ShuffleWeight > 0f) return;
        Vector3 hand = RideHolds.Point(rt.transform, RideHolds.HandMetres(hold));
        float reach = RideTuning.SnagReach;
        Carryable best = null;
        float bestD = reach * reach;
        var list = Carryable.All;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (!(c is TrashBag tb) || c.State != CarryState.Resting || c.Holder != null || c.IsInScriptedFlight || !c.isActiveAndEnabled) continue;
            if (tb.HasBurst) continue;
            Vector3 w = c.WorldCenter;
            if (Mathf.Abs(w.y - hand.y) > 2.2f) continue;
            Vector3 dd = w - hand; dd.y = 0f;
            float sq = dd.sqrMagnitude;
            if (sq >= bestD) continue;
            if (!Permissions.LocalCanWorkAt(c.transform.position)) continue;
            bestD = sq;
            best = c;
        }
        SnagCandidate = best;
        if (best != null && want) StartSnag(best, rt);
    }

    void StartSnag(Carryable c, RideTruck rt)
    {
        if (carrier == null || !carrier.PickUp(c)) return;
        snag = c;
        snagAt = Time.time;
        snagTossed = false;
        GameHUD.FloatingText(c.WorldCenter + Vector3.up * 0.8f, "SNAG!", new Color(1f, 0.9f, 0.4f), 0.9f);
    }

    void SnagProgress(RideTruck rt)
    {
        SnagCandidate = null;
        if (!snagTossed)
        {
            if (snag == null || carrier == null || carrier.Held != snag) { SnagReset(); return; }
            var canRelease = TrashCarrier.NetCanRelease;
            bool ok = canRelease == null || canRelease(carrier);
            if (Time.time - snagAt >= 0.25f && ok)
            {
                var hopper = rt.Truck != null ? rt.Truck.GetComponentInChildren<TruckHopper>(true) : null;
                if (hopper == null) { SnagReset(); return; }
                carrier.TossInto(hopper);
                if (carrier.Held == snag) { SnagReset(); return; } // refused (truck full): keep it, the "nope" said why
                snagTossed = true;
                snagTossAt = Time.time;
            }
            else if (Time.time - snagAt > 2.5f) SnagReset();
            return;
        }
        if (snag == null || snag.State == CarryState.Consumed)
        {
            Streak = Time.time - lastSnagDoneAt <= RideTuning.StreakGap ? Streak + 1 : 1;
            lastSnagDoneAt = Time.time;
            SnagReset();
            StreakPay = 0f;
            if (Streak >= 2)
            {
                StreakPay = Mathf.Min(RideTuning.StreakBonusCap, RideTuning.StreakBonusPer * (Streak - 1));
                PayStreak(rt, Streak);
                GameHUD.FloatingText(transform.position + Vector3.up * 2.5f, $"STREAK x{Streak}!", new Color(1f, 0.8f, 0.2f), 1.2f + Mathf.Min(0.6f, Streak * 0.08f));
                Sfx.Play("combo", transform.position, 0.8f, 0.9f + Mathf.Min(0.6f, Streak * 0.08f));
            }
            StreakShownAt = Time.unscaledTime;
            RideStats.RecordStreak(Streak);
            RideEvents.RaiseSnagged(Streak);
            Audit("snag", rt, -1, new Dictionary<string, object> { { "streak", Streak }, { "pay", System.Math.Round(StreakPay, 2) } });
            return;
        }
        if (Time.time - snagTossAt > 3f) SnagReset();
    }

    void PayStreak(RideTruck rt, int n)
    {
        var net = Net;
        if (net != null && net.Active) { net.LocalRidePay(HandsNet.RidePayStreak, rt.NetId, n); return; }
        float amount = Mathf.Min(RideTuning.StreakBonusCap, RideTuning.StreakBonusPer * (n - 1));
        var hopper = rt.Truck != null ? rt.Truck.GetComponentInChildren<TruckHopper>(true) : null;
        string biz = hopper != null ? hopper.ResolveBusiness() : null;
        if (string.IsNullOrEmpty(biz) || biz == Businesses.LocalId) Ledger.Credit(amount, $"Drive-by STREAK x{n}", RideTuning.LedgerStreak, transform.position);
    }

    // ==================================================================================== hints

    string lastHint;
    bool hintOwned;

    /// <summary>The riding hint line (PlayerBody shows it in co-op).</summary>
    int hintKey = -1;
    string hintCached;

    public string HintText(RideTruck rt)
    {
        bool pad = GameControls.UsingGamepad;
        bool canSwitch = rt != null && TryGetRide(out _, out int hold) && RideHolds.NextFreeHold(hold, TakenHolds(rt, hold)) >= 0;
        int key = (SnagCandidate != null ? 1 : 0) | (NetMode ? 2 : 0) | (rt != null && rt.BarLevel > 0 ? 4 : 0) | (BarDown ? 8 : 0) |
                  (rt != null && rt.SoloCruising ? 16 : 0) | (pad ? 32 : 0) | (canSwitch ? 64 : 0);
        if (key == hintKey && hintCached != null) return hintCached;
        hintKey = key;
        var sb = new System.Text.StringBuilder(96);
        sb.Append(pad ? "[X/A] Hop off" : $"[{Keybinds.Cap(Keybinds.Defaults.Interact)}/Space] Hop off");
        if (canSwitch) sb.Append(pad ? "  [RB] Switch spot" : $"  [{Keybinds.Cap(Keybinds.Defaults.SwitchSpot)}] Switch spot");
        if (SnagCandidate != null) sb.Append(pad ? "  [RT] Snag bag" : "  [LMB] Snag bag");
        if (!NetMode)
            sb.Append(rt != null && rt.SoloCruising ? (pad ? "  [LB] Park cruise" : "  [C] Park cruise") :
                      (pad ? "  [LB] Solo cruise" : "  [C] Solo cruise"));
        if (rt != null && rt.BarLevel > 0) sb.Append(BarDown ? (pad ? "  [Y] Bar: DOWN" : "  [B] Bar: DOWN") :
                                                      (pad ? "  [Y] Bar: UP" : "  [B] Bar: UP"));
        return hintCached = sb.ToString();
    }

    /// <summary>PlayerBody's riding hint (null = its default).</summary>
    public static string HintFor(PlayerBody b)
    {
        var r = b != null ? b.GetComponent<RideRider>() : null;
        if (r == null || !r.TryGetRide(out var rt, out _)) return null;
        return r.HintText(rt);
    }

    /// <summary>This player's fling owns their pose (PlayerBody mustn't pin them to the step or run its hop-off): flying,
    /// or landed before the host's word that they're off the truck arrived.</summary>
    public static bool IsFlingFlying(PlayerBody b)
    {
        var r = b != null ? b.GetComponent<RideRider>() : null;
        return r != null && (r.flying || Time.time < r.flingAwaitUntil);
    }

    float flingAwaitUntil;

    void SetHint(string text)
    {
        if (!IsLocal) return;
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

    // ==================================================================================== audit

    static string V(Vector3 v) => $"{v.x:0.000},{v.y:0.000},{v.z:0.000}";

    void Audit(string op, RideTruck rt, int hold, Dictionary<string, object> extra)
    {
        var d = new Dictionary<string, object> { { "op", op }, { "rider", (long)ClientId }, { "local", IsLocal } };
        if (rt != null) d["truck"] = (long)rt.NetId;
        if (hold >= 0) d["hold"] = hold;
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        NetAudit.Event("ride", d);
    }

    // ==================================================================================== boot

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
        RideRunner.EnsureExists();
    }

    static void OnPlayerSpawned(PlayerController pc)
    {
        if (pc != null && pc.GetComponent<RideRider>() == null) pc.gameObject.AddComponent<RideRider>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        seenFlingTicks.Clear();
        LocalRider = null;
        yawField = null;
        pitchField = null;
    }
}

/// <summary>Keeps every truck rideable (HitchStep + RideTruck, solo too) and draws the riding HUD.</summary>
public class RideRunner : MonoBehaviour
{
    static RideRunner instance;
    float nextScan;

    public static void EnsureExists()
    {
        if (instance != null || !Application.isPlaying) return;
        var go = new GameObject("[RideRunner]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<RideRunner>();
        go.AddComponent<RideHud>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null || t.GetComponentInChildren<TruckHopper>(true) == null) continue;
            HitchStep.Ensure(t);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}

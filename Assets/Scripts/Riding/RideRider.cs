using System.Collections.Generic;
using System.Reflection;
using StarterAssets;
using UnityEngine;

/// <summary>
/// A player riding a truck (package F1, Docs/Multiplayer.md §14.1, §4.4), added at runtime to every player copy (solo
/// and co-op; no prefab edit). The rider's OWN machine simulates their grip against the truck it shows (stress and
/// jolts from <see cref="RideTruck"/>, applied at the displayed truck tick) and decides the fling; every other machine
/// plays the fling from the same truck tick and owner's world start (<see cref="HandsNet"/> carries the event).
/// <list type="bullet">
/// <item><b>Grip</b> 0–100 drains with sideways g, hard braking and drift; refills when it's calm. <b>Lean</b> (A/D) into
/// the turn cuts the drain to 30% (leaning the wrong way: 140%). Grip Gloves, rain (<see cref="Riding.GripDrainScale"/>).</item>
/// <item><b>HOLD ON!</b> Jolts costing ≥ 15 open a 0.55 s window: Space in it = no loss, a miss = double loss.</item>
/// <item><b>Fling</b> at 0 grip: launched along the truck's momentum, out of the turn, flailing; AIR TIME readout and a
/// personal best (<see cref="RideStats"/>). Hedges catch, trampolines bounce, props and pedestrians go over.</item>
/// <item><b>Dominoes</b>: a flung rider heading at your hold costs you 55 grip (HOLD ON saves it). <b>Shove</b> (RMB) a
/// neighbouring rider: they get a HOLD ON.</item>
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

    static HandsNet Net => HandsNet.Instance;
    static bool NetMode => HandsNet.Instance != null && HandsNet.Instance.Active;

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
        GameHUD.FloatingText(transform.position + Vector3.up * 2.1f, "Hold on!", new Color(1f, 0.85f, 0.4f), 1f);
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
        pendingHits.Clear();
        HoldOnActive = false;
        soloKoUntil = 0f;
        RestoreRideCamera();
        RestoreSkeleton();
        if (soloLocked) SoloLock(false);
    }

    /// <summary>Point the camera along the truck, angled a little toward its body on a side rung so the camera swings
    /// out beside the truck instead of into its flank (the lock picks this yaw up when it engages).</summary>
    void AimCameraFor(Transform truck, int hold)
    {
        if (!IsLocal || truck == null || tpc == null || yawField == null) return;
        float yaw = truck.eulerAngles.y - (RideHolds.IsRung(hold) ? RideHolds.Side(hold) * 28f : 0f);
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

    // ==================================================================================== the rider's own simulation

    public float Grip { get; private set; } = RideTuning.GripMax;
    public int Lean { get; private set; }
    public float GripForHud => IsLocal ? Grip : Time.unscaledTime - remoteGripAt < 1f ? remoteGrip : -1f;
    float remoteGrip = -1f, remoteGripAt = -9f, lastGripSentAt = -9f;

    public void ReceiveRemoteGrip(byte grip)
    {
        remoteGrip = Mathf.Min(grip, (byte)100);
        remoteGripAt = Time.unscaledTime;
    }
    /// <summary>Owner's last lean choice on remote copies, refreshed while riding for late joiners.</summary>
    public int RemoteLean => Time.unscaledTime - remoteLeanAt < 1.5f ? remoteLean : 0;
    int remoteLean;
    float remoteLeanAt = -9f, lastLeanSentAt = -9f;
    int lastLeanSent = 9;

    public void ReceiveRemoteLean(sbyte lean)
    {
        remoteLean = Mathf.Clamp(lean, -1, 1);
        remoteLeanAt = Time.unscaledTime;
    }
    /// <summary>Grip per second being lost right now (HUD).</summary>
    public float DrainNow { get; private set; }
    /// <summary>The position of this truck's shared Safety Bar (not a per-rider switch).</summary>
    public bool BarDown => TryGetRide(out var rt, out _) && rt != null ? rt.BarDown : true;

    RideTruck cursorTruck;
    int joltCursor;
    readonly HashSet<int> consumedJolts = new HashSet<int>();
    readonly List<RideTruck.Jolt> tmpJolts = new List<RideTruck.Jolt>(8);

    // HOLD ON!
    public bool HoldOnActive { get; private set; }
    public float HoldOnStart { get; private set; }
    float holdOnCost, spaceGraceUntil;
    Vector3 holdOnPush;
    ulong holdOnCause = HandsNet.None;
    byte holdOnFlags;

    // feedback for the HUD
    public string Feedback { get; private set; }
    public Color FeedbackColor { get; private set; }
    public float FeedbackUntil { get; private set; }

    // bots
    /// <summary>Bots: lean −1/0/+1 instead of A/D (null = keys).</summary>
    public int? BotLean;
    bool botSpace, botSnag, botShove;
    public void BotPressSpace() => botSpace = true;
    public void BotSnag() => botSnag = true;
    public void BotShove() => botShove = true;

    struct PendingHit { public double t; public RideTruck rt; public float cost; public Vector3 push; public ulong cause; public bool forced; public byte flags; public string word; }
    readonly List<PendingHit> pendingHits = new List<PendingHit>(4);

    public const byte FlagDomino = 1, FlagShove = 2, FlagCrash = 4, FlagKnock = 8;

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
        bool space = (!ui && GameControls.Down(KeyCode.Space, GameControls.Action.Jump)) || botSpace;
        botSpace = false;
        if (flying) { SetHint(null); return; }
        if (soloKoUntil > 0f)
        {
            SetHint(null);
            if (Time.time >= soloKoUntil) { soloKoUntil = 0f; SoloLock(false); RestoreSkeleton(); }
            return;
        }
        if (!TryGetRide(out var rt, out int hold))
        {
            if (cursorTruck != null) { cursorTruck = null; HoldOnActive = false; pendingHits.Clear(); consumedJolts.Clear(); }
            Grip = RideTuning.GripMax;
            Lean = 0;
            lastLeanSent = 9;
            lastGripSentAt = -9f;
            DrainNow = 0f;
            SnagReset();
            SetHint(null);
            return;
        }
        if (rt != cursorTruck)
        {
            cursorTruck = rt;
            joltCursor = rt.LastJoltSerial;
            consumedJolts.Clear();
            Grip = RideTuning.GripMax;
            HoldOnActive = false;
            pendingHits.Clear();
        }
        float dt = Time.deltaTime;
        double D = rt.DisplayTime;

        // ---- lean
        int lean = 0;
        if (!ui)
        {
            if (Input.GetKey(KeyCode.A)) lean -= 1;
            if (Input.GetKey(KeyCode.D)) lean += 1;
            float stick = GameControls.RideLean;
            if (Mathf.Abs(stick) > 0.35f) lean = stick < 0f ? -1 : 1;
        }
        if (BotLean.HasValue) lean = Mathf.Clamp(BotLean.Value, -1, 1);
        Lean = lean;
        var leanNet = Net;
        if (leanNet != null && leanNet.Active && (lean != lastLeanSent || Time.unscaledTime - lastLeanSentAt > 0.75f))
        {
            leanNet.LocalRideLean((sbyte)lean);
            lastLeanSent = lean;
            lastLeanSentAt = Time.unscaledTime;
        }

        // ---- steady drain (what the truck is doing at the moment this machine shows)
        rt.StressAt(D, out float lat, out float brake, out float drift, out float speed);
        float drain = RideTruck.Drain(lat, brake, drift);
        if (drain > 0.01f)
        {
            float leanMult = 1f;
            if (Mathf.Abs(lat) > RideTuning.LatFree * 0.6f && lean != 0)
                leanMult = lean == (int)Mathf.Sign(lat) ? RideTuning.LeanInto : RideTuning.LeanWrong;
            drain *= leanMult * RideShop.GloveDrainMult(RideShop.Local(RideShop.GripGloves)) * Mathf.Max(0f, Riding.GripDrainScale);
            Grip -= drain * dt;
        }
        else Grip = Mathf.Min(RideTuning.GripMax, Grip + (speed < RideTuning.StoppedSpeed ? RideTuning.RegainStopped : RideTuning.Regain) * dt);
        DrainNow = drain;

        // ---- jolts the truck took, when this machine shows it taking them
        tmpJolts.Clear();
        rt.JoltsSince(joltCursor, D, consumedJolts, tmpJolts);
        for (int i = 0; i < tmpJolts.Count; i++)
        {
            var j = tmpJolts[i];
            if (j.flingAll)
            {
                if (BarSaves(rt, speed)) { Hit(RideTuning.MaxJoltCost, Vector3.zero, HandsNet.None, true, FlagCrash, "CRASH!"); continue; }
                Fling(rt, hold, Vector3.zero, HandsNet.None, FlagCrash, lat);
                return;
            }
            float cost = j.cost * RideShop.PadJoltMult(rt.PaddedLevel);
            Hit(cost, Vector3.zero, HandsNet.None, true, 0, j.kind == RideTruck.Jolt.KindBump ? "BUMP!" : "WHAM!");
        }

        // ---- dominoes and shoves stamped at a truck moment
        for (int i = pendingHits.Count - 1; i >= 0; i--)
        {
            var p = pendingHits[i];
            if (p.rt != rt) { pendingHits.RemoveAt(i); continue; }
            if (D < p.t) continue;
            pendingHits.RemoveAt(i);
            if (p.forced) { Fling(rt, hold, p.push, p.cause, p.flags, lat); return; }
            Hit(p.cost, p.push, p.cause, true, p.flags, p.word);
        }

        // ---- HOLD ON! window
        if (HoldOnActive)
        {
            if (space)
            {
                HoldOnActive = false;
                space = false;
                spaceGraceUntil = Time.time + 0.35f;
                Say("SAVED!", new Color(0.45f, 1f, 0.5f), 0.9f);
                Sfx.Play("ding", transform.position, 0.6f, 1.3f);
                Audit("holdon", rt, hold, new Dictionary<string, object> { { "saved", true }, { "cost", System.Math.Round(holdOnCost, 1) } });
            }
            else if (Time.time - HoldOnStart >= RideTuning.HoldOnWindow)
            {
                HoldOnActive = false;
                spaceGraceUntil = Time.time + 0.25f;
                Grip -= holdOnCost * RideTuning.MissMult;
                Say("MISSED!", new Color(1f, 0.4f, 0.3f), 0.9f);
                Sfx.Play("oof", transform.position, 0.7f, 1.1f);
                Juice.Shake(0.12f, 0.15f);
                Audit("holdon", rt, hold, new Dictionary<string, object> { { "saved", false }, { "cost", System.Math.Round(holdOnCost, 1) } });
            }
        }

        // ---- 0 grip: FLUNG (unless the Safety Bar holds you)
        if (Grip <= 0f)
        {
            if (BarSaves(rt, speed)) Grip = 1f;
            else { Fling(rt, hold, holdOnPush, holdOnCause, holdOnFlags, lat); return; }
        }

        if (leanNet != null && leanNet.Active && Time.unscaledTime - lastGripSentAt >= 0.2f)
        {
            leanNet.LocalRideGrip((byte)Mathf.Clamp(Mathf.RoundToInt(Grip), 0, 100));
            lastGripSentAt = Time.unscaledTime;
        }

        // ---- keys
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
        bool shove = (!ui && (Input.GetMouseButtonDown(1) || GameControls.PadDown(GameControls.Action.Shove))) || botShove;
        botShove = false;
        if (shove) TryRideShove(rt, hold);
        if (space && !NetMode && Time.time >= spaceGraceUntil) { Unhitch(); return; }

        SnagTick(rt, hold, speed, ui);
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

    bool BarSaves(RideTruck rt, float speed) => RideTuning.BarSaves(rt.BarLevel, rt.BarDown, speed);

    /// <summary>Lose <paramref name="cost"/> grip, or open a HOLD ON window for it.</summary>
    void Hit(float cost, Vector3 push, ulong cause, bool savable, byte flags, string word)
    {
        if (cost <= 0f) return;
        if (!string.IsNullOrEmpty(word) && cost >= 10f) GameHUD.FloatingText(transform.position + Vector3.up * 2.2f, word, new Color(1f, 0.75f, 0.3f), 0.8f);
        Juice.Shake(Mathf.Clamp(cost * 0.004f, 0.04f, 0.3f), 0.15f);
        if (savable && cost >= RideTuning.PromptCost)
        {
            if (HoldOnActive)
            {
                holdOnCost = Mathf.Max(holdOnCost, cost);
                if (push.sqrMagnitude > holdOnPush.sqrMagnitude) { holdOnPush = push; holdOnCause = cause; holdOnFlags = flags; }
                return;
            }
            HoldOnActive = true;
            HoldOnStart = Time.time;
            holdOnCost = cost;
            holdOnPush = push;
            holdOnCause = cause;
            holdOnFlags = flags;
            Sfx.Play("squeak", transform.position, 0.7f, 0.8f);
            return;
        }
        Grip -= cost;
        if (Grip <= 0f) { holdOnPush = push; holdOnCause = cause; holdOnFlags = flags; }
    }

    void Say(string text, Color c, float secs)
    {
        Feedback = text;
        FeedbackColor = c;
        FeedbackUntil = Time.unscaledTime + secs;
    }

    /// <summary>A hit from outside (dogs, raccoons, Riding.KnockOff / GripHit). Only for this machine's own rider.</summary>
    public void ApplyHit(Vector3 push, float cost, bool forced, bool savable, string word, ulong cause)
    {
        if (!IsLocal || !TryGetRide(out var rt, out int hold)) return;
        if (!string.IsNullOrEmpty(word)) GameHUD.FloatingText(transform.position + Vector3.up * 2.3f, word, new Color(1f, 0.6f, 0.3f), 1f);
        if (forced)
        {
            rt.StressAt(rt.DisplayTime, out float lat, out _, out _, out _);
            Fling(rt, hold, push, cause, FlagKnock, lat);
            return;
        }
        Hit(cost, push, cause, savable, FlagKnock, null);
    }

    // ==================================================================================== shove and dominoes

    float lastShoveAt = -9f, shoveImmuneUntil;

    void TryRideShove(RideTruck rt, int hold)
    {
        var net = Net;
        if (net == null || !net.Active || Time.time - lastShoveAt < RideTuning.ShoveCooldown) return;
        lastShoveAt = Time.time;
        RideRider best = null;
        float bestD = RideTuning.ShoveRange * RideTuning.ShoveRange;
        foreach (var o in all)
        {
            if (o == null || o == this || !o.TryGetRide(out var ort, out _) || ort != rt) continue;
            float d = (o.transform.position - transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = o; }
        }
        Sfx.Play("whoosh", transform.position + Vector3.up, 0.6f, 1.2f);
        if (best == null) return;
        net.LocalRideShove(best.ClientId);
        Audit("shove", rt, hold, new Dictionary<string, object> { { "target", (long)best.ClientId } });
    }

    /// <summary>Host's word: <paramref name="attacker"/> shoved this rider (every machine; the victim's own one applies it).</summary>
    public void OnShoved(ulong attacker, Vector3 dir)
    {
        GameHUD.FloatingText(transform.position + Vector3.up * 2.2f, "SHOVE!", new Color(1f, 0.55f, 0.2f), 1.1f);
        if (!IsLocal || !TryGetRide(out var rt, out int hold)) return;
        if (Time.time < shoveImmuneUntil) return;
        shoveImmuneUntil = Time.time + RideTuning.ShoveImmune;
        dir.y = 0f;
        Hit(RideTuning.ShoveCost, dir.sqrMagnitude > 0.01f ? dir.normalized * 3f : Vector3.zero, attacker, true, FlagShove, null);
        Audit("shoved", rt, hold, new Dictionary<string, object> { { "by", (long)attacker } });
    }

    /// <summary>Another rider was flung (every machine): if they're coming at my hold, I may go too.</summary>
    void OnOtherFlung(ulong rider, RideTruck rt, int theirHold, double tF, Vector3 vel)
    {
        if (!IsLocal || rt == null || !TryGetRide(out var myRt, out int myHold) || myRt != rt) return;
        if (!RideHolds.Adjacent(theirHold, myHold)) return;
        Vector3 toMe = RideHolds.Point(rt.transform, RideHolds.FeetMetres(myHold)) - RideHolds.Point(rt.transform, RideHolds.FeetMetres(theirHold));
        toMe.y = 0f;
        Vector3 rel = vel - rt.Velocity; rel.y = 0f;
        if (Vector3.Dot(rel, toMe) <= 0f) return;
        pendingHits.Add(new PendingHit
        {
            t = tF + 0.08, rt = rt, cost = RideTuning.DominoCost, push = toMe.normalized * 3f, cause = rider, flags = FlagDomino, word = "DOMINO!",
        });
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

    void Fling(RideTruck rt, int hold, Vector3 push, ulong cause, byte flags, float lat)
    {
        double tF = rt.DisplayTime;
        Transform tt = rt.transform;
        Vector3 start = RideHolds.Point(tt, RideHolds.FeetMetres(hold)) + Vector3.up * 0.15f;
        Vector3 right = tt.right; right.y = 0f; right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
        Vector3 outward;
        Vector3 flatPush = new Vector3(push.x, 0f, push.z);
        if (flatPush.sqrMagnitude > 0.01f) outward = flatPush.normalized;
        else if (Mathf.Abs(lat) > 2f) outward = right * -Mathf.Sign(lat);   // out of the turn
        else outward = right * RideHolds.Side(hold);
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
        HoldOnActive = false;
        pendingHits.Clear();
        Grip = RideTuning.GripMax;

        var net = Net;
        if (net != null && net.Active) { net.LocalRideFling(rt.NetId, (byte)hold, tF, start, vel, flags, cause); flingAwaitUntil = Time.time + 3f; }
        else { rt.StopSoloCruise(this); soloRiding = false; soloTruck = null; }
        rt.NoteFling();
        StartFlight(start, vel, true, flags, cause);
        GameHUD.FloatingText(start + Vector3.up * 2.2f, (flags & FlagDomino) != 0 ? "DOMINO!" : "WAAAAH!", new Color(1f, 0.5f, 0.25f), 1.3f);
        Juice.Shake(0.3f, 0.3f);
        Audit("fling", rt, hold, new Dictionary<string, object>
        {
            { "tF", System.Math.Round(tF, 4) }, { "start", V(start) }, { "truckPos", V(tt.position) }, { "vel", V(vel) },
            { "flags", flags }, { "cause", cause == HandsNet.None ? -1L : (long)cause }, { "lag", System.Math.Round(rt.Lag, 3) }, { "matches", rt.LagMatches },
        });
        if ((flags & FlagDomino) != 0 && cause != HandsNet.None)
        {
            var c = For(cause);
            RideEvents.RaiseDomino(c != null ? c.Player : null, Player);
        }
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

    /// <summary>A fling event (every machine, from the host): present another player's fling, or check for dominoes.</summary>
    public static void OnFlungEvent(ulong rider, ulong truckId, byte hold, double tF, Vector3 worldStart, Vector3 vel, byte flags, ulong cause)
    {
        var rt = RideTruck.Find(truckId);
        var r = For(rider);
        if (r == null) return;
        if (seenFlingTicks.TryGetValue(rider, out double last) && System.Math.Abs(last - tF) < 0.0001) return;
        seenFlingTicks[rider] = tF;
        bool mine = r != null && r.IsLocal;
        if (rt != null && !mine) rt.NoteFling();
        if (r != null && !mine) r.ScheduleRemoteFling(rt, hold, tF, worldStart, vel, flags, cause);
        var me = LocalRider;
        if (me != null && me != r) me.OnOtherFlung(rider, rt, hold, tF, vel);
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
        if ((remoteFlags & FlagDomino) != 0) GameHUD.FloatingText(start + Vector3.up * 2.2f, "DOMINO!", new Color(1f, 0.5f, 0.25f), 1.3f);
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
            var pose = RideHolds.PoseOn(soloTruck.transform, soloHold);
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
                var pose = RideHolds.PoseOn(remoteRt.transform, remoteHold);
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

    void SnagTick(RideTruck rt, int hold, float speed, bool ui)
    {
        if (Streak > 0 && Time.time - lastSnagDoneAt > RideTuning.StreakGap) Streak = 0;
        if (snag != null || snagTossed) { SnagProgress(rt); return; }
        SnagCandidate = null;
        bool want = botSnag || (!ui && (Input.GetMouseButtonDown(0) || GameControls.PadDown(GameControls.Action.Snag)));
        botSnag = false;
        if (speed > RideTuning.SnagMaxSpeed || rt.Speed > RideTuning.SnagMaxSpeed + 0.5f || carrier == null || carrier.IsCarrying) return;
        Vector3 hand = RideHolds.Point(rt.transform, RideHolds.HandMetres(hold));
        float reach = RideTuning.SnagReach + (Lean != 0 ? RideTuning.SnagLeanReach : 0f);
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
        int key = (HoldOnActive ? 1 : 0) | (SnagCandidate != null ? 2 : 0) | (NetMode ? 4 : 0) | (rt != null && rt.BarLevel > 0 ? 8 : 0) | (BarDown ? 16 : 0) | (rt != null && rt.SoloCruising ? 32 : 0) | (pad ? 64 : 0);
        if (key == hintKey && hintCached != null) return hintCached;
        hintKey = key;
        var sb = new System.Text.StringBuilder(96);
        sb.Append(HoldOnActive ? (pad ? "HOLD ON!  [A]" : "HOLD ON!  [Space]") :
                  (pad ? "[Left stick] Lean  [A] Hop off" : "[A/D] Lean  [Space] Hop off"));
        if (SnagCandidate != null) sb.Append(pad ? "  [RT] Snag bag" : "  [LMB] Snag bag");
        if (NetMode) sb.Append(pad ? "  [RB] Shove" : "  [RMB] Shove");
        else sb.Append(rt != null && rt.SoloCruising ? (pad ? "  [LB] Park cruise" : "  [C] Park cruise") :
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

    /// <summary>Space belongs to HOLD ON right now (PlayerBody mustn't hop the rider off).</summary>
    public static bool ConsumesSpace(PlayerBody b)
    {
        var r = b != null ? b.GetComponent<RideRider>() : null;
        return r != null && (r.HoldOnActive || r.flying || Time.time < r.spaceGraceUntil);
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

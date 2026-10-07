using System.Collections.Generic;
using System.Reflection;
using StarterAssets;
using UnityEngine;

/// <summary>
/// A player's body in co-op (package N2, Docs/Multiplayer.md §4.4–4.5, §13.5b), added at runtime to every player copy by
/// <see cref="HandsNet"/> (no prefab edit). The host decides the state (<see cref="BodyNet"/>); this applies it on every
/// machine:
/// <list type="bullet">
/// <item><b>Look</b>: lying flat when flopped, knocked out, carried or flung; dizzy stars when knocked out.</item>
/// <item><b>Pinning</b>: a carried player is held in the carrier's arms, a rider stands on the truck's rear step, on each
/// machine's own copy of the carrier or truck (no parenting through NGO, §4.4).</item>
/// <item><b>The owner</b> plays knockbacks (CharacterController moves, so walls still block), loses control while stunned,
/// out, flopped, carried, flung or riding (mouse-look keeps working), and has the keys: RMB punch / swing, Z flop, Space
/// get up / wriggle / hop off; while carrying a friend LMB throws them and G puts them down.</item>
/// </list>
/// It's also the E target on other players: "Pick up Dot" (flopped or knocked down) or "Grab Dot's bag" (shared grip).
/// </summary>
[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public class PlayerBody : MonoBehaviour, IInteractable
{
    static readonly List<PlayerBody> all = new List<PlayerBody>();
    public static IReadOnlyList<PlayerBody> All => all;

    public static PlayerBody For(ulong client)
    {
        for (int i = 0; i < all.Count; i++)
        {
            var b = all[i];
            if (b != null && b.Player != null && b.Player.IsSpawned && b.Player.OwnerClientId == client) return b;
        }
        return null;
    }

    public static KeyCode ShoveKey = KeyCode.Mouse1, FlopKey = KeyCode.Z, GetUpKey = KeyCode.Space;

    public PlayerController Player { get; private set; }
    public TrashCarrier Carrier { get; private set; }
    CharacterController cc;
    ThirdPersonController tpc;
    StarterAssetsInputs input;
    Animator anim;
    Transform skeleton;
    Quaternion skeletonRot;
    Vector3 skeletonPos;

    public ulong ClientId => Player != null ? Player.OwnerClientId : HandsNet.None;
    public bool IsLocal => Player != null && Player.IsSpawned && Player.IsOwner;
    public string DisplayName => HandsNet.NameOf(ClientId);
    /// <summary>Where this player's hands hold things (world).</summary>
    public Vector3 HandPoint => transform.position + transform.rotation * new Vector3(0f, 1.05f, 0.55f);

    HandsNet Net => HandsNet.Instance;
    public BodyNet Body => Net != null ? Net.BodyOf(ClientId) : new BodyNet { client = ClientId, other = HandsNet.None, carrying = HandsNet.None };
    public BodyState State => Body.State;

    /// <summary>Empty hands, on their feet, facing things: can catch a throw or receive a pass.</summary>
    public bool CanCatch
    {
        get
        {
            if (Player == null || Player.IsDriving || Player.IsPlayingChess) return false;
            var b = Body;
            return (b.State == BodyState.Normal || b.State == BodyState.Riding) && b.carrying == HandsNet.None &&
                (Net == null || !Net.TugOf(ClientId).HasValue) && (Carrier == null || !Carrier.IsCarrying);
        }
    }

    // ---- owner: knockback and flight
    Vector3 knockDir;
    float knockDist, knockT = 1f, knockDur = 0.25f, knockHop;
    float localStunUntil;
    bool flying;
    Vector3 flyVel;
    float flyStart;
    float vy;

    // ---- owner: control lock
    bool locked;
    float lockYaw, lockPitch;
    static FieldInfo yawField, pitchField;

    // ---- owner: input
    float lastShove = -10f;
    bool charging;
    float chargeStart;
    bool hintOwned;
    string lastHint;
    float carrySlow = 1f, baseMove, baseSprint;
    bool slowApplied;

    // ---- look
    float lie;
    DizzyStars stars;
    BodyState lastState;
    bool riding, carried;
    Vector3 pinVel;

    void Awake()
    {
        Player = GetComponent<PlayerController>();
        Carrier = GetComponent<TrashCarrier>();
        cc = GetComponent<CharacterController>();
        tpc = GetComponent<ThirdPersonController>();
        input = GetComponent<StarterAssetsInputs>();
        anim = GetComponent<Animator>();
        skeleton = transform.Find("Skeleton");
        if (skeleton != null) { skeletonRot = skeleton.localRotation; skeletonPos = skeleton.localPosition; }
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
        all.Remove(this);
        if (IsLocal) Unlock();
        SetHint(null);
        RestoreSpeed();
    }

    // ==================================================================================== IInteractable (others' E on me)

    public bool CanInteract()
    {
        var net = Net;
        if (net == null || !net.Active || IsLocal || Player == null || !Player.IsSpawned) return false;
        var me = PlayerBody.For(net.LocalId);
        if (me == null || me.State != BodyState.Normal || me.Body.carrying != HandsNet.None) return false;
        if (me.Player == null || me.Player.IsDriving || me.Player.IsPlayingChess) return false;
        if (me.Carrier != null && me.Carrier.IsCarrying) return false;
        if (net.TugOf(net.LocalId).HasValue) return false;
        var b = Body;
        double now = net.ServerNow;
        // Hit immunity stops repeat punches, not scooping up somebody who is already down.
        if (b.State == BodyState.Flopped || b.State == BodyState.KnockedOut) return b.noGrabUntil <= now;
        if (HandsNet.TugsEnabled && b.State == BodyState.Normal && HeldItem() != null && !net.TugOf(ClientId).HasValue)
            return (transform.position - me.transform.position).sqrMagnitude < 2.2f * 2.2f;
        return false;
    }

    public string GetInteractionPrompt()
    {
        var b = Body;
        if (b.State == BodyState.Flopped) return $"Pick up {DisplayName} (flopped)";
        if (b.State == BodyState.KnockedOut) return $"Pick up {DisplayName} (downed)";
        var h = HeldItem();
        return h != null ? $"Grab the other side of {DisplayName}'s {(h.Item is TrashBag ? "bag" : "item")}" : "";
    }

    public void OnInteract(GameObject interactor)
    {
        var net = Net;
        if (net == null || !CanInteract()) return;
        if (Body.State == BodyState.Flopped || Body.State == BodyState.KnockedOut) { net.LocalCarry(ClientId); return; }
        var h = HeldItem();
        if (h != null) net.LocalSnatch(h);
    }

    public void Highlight(bool highlight) { }

    NetCarry HeldItem()
    {
        var net = Net;
        if (net == null) return null;
        foreach (var nc in net.Items.Values) if (nc != null && nc.HeldBy(ClientId)) return nc;
        return null;
    }

    // ==================================================================================== host's word arriving

    /// <summary>A knock landed on this player (every machine; only the owner moves).</summary>
    public void OnKnocked(Vector3 dir, float dist, float stun, HitKind kind, bool drop, string word)
    {
        Vector3 head = transform.position + Vector3.up * 1.8f;
        if (!string.IsNullOrEmpty(word)) GameHUD.FloatingText(head + Vector3.up * 0.3f, word, new Color(1f, 0.55f, 0.2f), 1.3f);
        Sfx.Play(kind == HitKind.KnockOut ? "thud" : "oof", head, 0.9f, Random01(transform.position) * 0.2f + 0.9f);
        if (kind == HitKind.KnockOut) Fx.Burst(head - Vector3.up * 0.3f, new Color(1f, 0.9f, 0.35f), 12, 3.5f);
        if (!IsLocal) return;
        if (drop && Carrier != null && Carrier.IsCarrying && !Carrier.ExternalControl)
        {
            var held = Carrier.Held;
            Carrier.Drop();
            if (held != null && held.Body != null && !held.Body.isKinematic) held.Body.linearVelocity += dir * 1.5f + Vector3.up * 3f;
        }
        if (charging) { charging = false; SetHint(null); }
        knockDir = dir;
        knockDist = dist;
        knockHop = kind == HitKind.KnockOut ? 0.35f : 0.15f;
        knockDur = kind == HitKind.KnockOut ? 0.35f : 0.25f;
        knockT = 0f;
        localStunUntil = Time.time + stun;
        Juice.Shake(kind == HitKind.KnockOut ? 0.35f : 0.18f, 0.22f);
        Juice.HitStop(0.06f, 0.1f);
    }

    /// <summary>This player was thrown by a friend (every machine; the owner flies).</summary>
    public void OnThrown(Vector3 from, Vector3 vel)
    {
        Sfx.Play("whoosh", from, 0.9f, 0.8f);
        GameHUD.FloatingText(from + Vector3.up * 0.8f, "WHEEE!", new Color(0.6f, 0.9f, 1f), 1.2f);
        if (!IsLocal) return;
        SetPose(from - Vector3.up * 0.9f, transform.rotation);
        if (cc != null) cc.enabled = true;
        flying = true;
        flyVel = vel;
        flyStart = Time.time;
    }

    /// <summary>A host-authorized compactor takes over motion; old throws/knocks must not resume after ejection.</summary>
    internal void ResetForCompactor()
    {
        flying = false;
        flyVel = Vector3.zero;
        knockT = 1f;
        localStunUntil = 0f;
        charging = false;
        lie = 0f;
        riding = carried = false;
        lastState = BodyState.Normal;
        Unlock();
        ApplyLie(0f, 0f);
        SetHint(null);
    }

    // ==================================================================================== per frame

    void Update()
    {
        var net = Net;
        if (net == null || !net.Active || Player == null || !Player.IsSpawned) return;
        var b = Body;
        var st = b.State;
        if (st != lastState) OnStateChanged(lastState, st);
        lastState = st;
        if (IsLocal) OwnerUpdate(net, b);
        UpdateLook(b);
    }

    void OnStateChanged(BodyState from, BodyState to)
    {
        if (to == BodyState.KnockedOut) { if (stars == null) stars = DizzyStars.Attach(transform, 1.2f, 0.35f, 2.15f); }
        if (to == BodyState.Riding)
        {
            var hs = HitchStep.Find(Body.other);
            Sfx.Play("clang", transform.position, 0.6f, 1.2f);
            if (hs != null && IsLocal) GameHUD.FloatingText(transform.position + Vector3.up * 2.1f, "Hold on!", new Color(1f, 0.85f, 0.4f), 1f);
        }
        if (from == BodyState.Riding && IsLocal && !RideRider.IsFlingFlying(this)) HopOff(); // F1: a fling flies itself
        if (from == BodyState.Carried && IsLocal && to != BodyState.Thrown)
        {
            // Put down or escaped: on your feet (or the floor) where the carrier stood.
            if (cc != null) cc.enabled = true;
            if (to == BodyState.Normal) GameHUD.FloatingText(transform.position + Vector3.up * 2f, "Free!", new Color(0.5f, 1f, 0.5f), 1f);
        }
        if (to == BodyState.Flopped && IsLocal && from == BodyState.Normal) Sfx.Play("thud", transform.position, 0.5f, 1.1f);
    }

    void OwnerUpdate(HandsNet net, BodyNet b)
    {
        var st = b.State;
        bool tug = net.TugOf(ClientId).HasValue;
        bool wantLock = st != BodyState.Normal || Time.time < localStunUntil || knockT < 1f || flying;
        if (wantLock) Lock(); else Unlock();
        if (locked) LockedLook();

        StepKnock();
        StepFlight(net, st);

        if (PlayerController.IsUIBlocking || Player.IsDriving || Player.IsPlayingChess) { SetHint(null); return; }
        if (Carrier != null && Carrier.ExternalControl) { SetHint(null); return; }

        switch (st)
        {
            case BodyState.Flopped:
                SetHint(GameControls.UsingGamepad
                    ? "Playing dead · [A] Get up (another player can press X to pick you up)"
                    : "Playing dead · [Space] Get up (another player can press E to pick you up)");
                if (GameControls.Down(GetUpKey, GameControls.Action.Jump) || GameControls.Down(FlopKey, GameControls.Action.Flop)) net.LocalFlop(false);
                return;
            case BodyState.KnockedOut:
                SetHint(GameControls.UsingGamepad
                    ? "Knocked down (another player can press X to pick you up)"
                    : "Knocked down (another player can press E to pick you up)");
                return;
            case BodyState.Carried:
                SetHint($"Carried by {HandsNet.NameOf(b.other)}!  Mash {(GameControls.UsingGamepad ? "[A]" : "[Space]")} to wriggle free");
                if (GameControls.Down(GetUpKey, GameControls.Action.Jump)) { net.LocalWriggle(); Juice.Shake(0.05f, 0.08f); }
                return;
            case BodyState.Riding:
                SetHint(RideRider.HintFor(this) ?? (GameControls.UsingGamepad ? "Riding the back.  [A] Hop off" : "Riding the back.  [Space] Hop off"));
                // F1: during a HOLD ON! window Space is the save, not the exit.
                if (GameControls.Down(GetUpKey, GameControls.Action.Jump) && !RideRider.ConsumesSpace(this)) net.LocalUnhitch();
                break;
            case BodyState.Normal:
                break;
            default:
                SetHint(null);
                return;
        }

        if (b.carrying != HandsNet.None)
        {
            ApplySpeed(0.55f);
            CarryInput(net, b);
            return;
        }
        RestoreSpeed();
        if (st == BodyState.Normal && !tug)
        {
            // A voluntary flop or a full punch-combo knockdown opens a pickup window.
            var nearby = Carrier != null && Carrier.IsCarrying ? null : NearbyTeammate(2.6f);
            if (nearby != null)
            {
                string punch = GameControls.UsingGamepad ? "RB" : "RMB";
                string flop = GameControls.UsingGamepad ? "R3" : "Z";
                string pickup = GameControls.UsingGamepad ? "X" : "E";
                bool scoopable = (nearby.State == BodyState.Flopped || nearby.State == BodyState.KnockedOut) && nearby.CanInteract();
                if (scoopable)
                    SetHint($"{nearby.DisplayName} is down · [{pickup}] {nearby.GetInteractionPrompt()}");
                else if ((nearby.transform.position - transform.position).sqrMagnitude <=
                         (HandsTuning.ShoveRange + 0.3f) * (HandsTuning.ShoveRange + 0.3f))
                    SetHint($"Close quarters · [{punch}] Punch [{flop}] Flop to be carried");
                else SetHint(null);
            }
            else SetHint(null);
            if (GameControls.Down(FlopKey, GameControls.Action.Flop) && (Carrier == null || !Carrier.IsCarrying)) { net.LocalFlop(true); return; }
        }
        if (GameControls.Down(ShoveKey, GameControls.Action.Shove) && !tug && st != BodyState.Riding) TryShove(net); // riders shove riders: RideRider
    }

    // ---- shove / swing

    /// <summary>The nearest other player in front of us within reach (null if none).</summary>
    public PlayerBody ShoveTarget(float range)
    {
        PlayerBody best = null;
        float bestD = range * range;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        foreach (var o in all)
        {
            if (o == null || o == this || o.Player == null || !o.Player.IsSpawned) continue;
            Vector3 d = o.transform.position - transform.position; d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq > bestD) continue;
            if (sq > 0.04f && Vector3.Angle(fwd, d) > HandsTuning.ShoveCone) continue;
            if (!PoleTargeting.ClearSegment(transform.position + Vector3.up * 1.1f,
                o.transform.position + Vector3.up * 1.1f, transform, o.transform)) continue;
            bestD = sq;
            best = o;
        }
        return best;
    }

    public bool TryShove(HandsNet net)
    {
        bool swing = Carrier != null && Carrier.IsCarrying;
        if (Time.time - lastShove < (swing ? HandsTuning.SwingCooldown : HandsTuning.ShoveCooldown)) return false;
        lastShove = Time.time;
        var target = ShoveTarget(swing ? HandsTuning.SwingRange : HandsTuning.ShoveRange);
        var critter = CritterMelee.Target(Player, swing ? HandsTuning.SwingRange : HandsTuning.ShoveRange);
        if (target != null && critter != null && (target.transform.position - transform.position).sqrMagnitude <=
            (critter.transform.position - transform.position).sqrMagnitude + 0.2f) critter = null;
        // Even a miss is a real swing on every screen. The old CharacterController lunge felt like an accidental dash.
        if (swing && Carrier.Held != null) Juice.Punch(Carrier.Held.JuiceTarget, 0.35f, 0.25f);
        net.LocalShove(critter == null && target != null ? target.ClientId : HandsNet.None, swing, critter != null ? critter.NetId : (ushort)0);
        return target != null || critter != null;
    }

    // ---- carrying a friend

    void CarryInput(HandsNet net, BodyNet b)
    {
        string name = HandsNet.NameOf(b.carrying);
        if (GameControls.Down(KeyCode.G, GameControls.Action.Drop)) { charging = false; net.LocalDropCarried(); SetHint(null); return; }
        if (!charging && (Input.GetMouseButtonDown(0) || GameControls.PadDown(GameControls.Action.Throw))) { charging = true; chargeStart = Time.unscaledTime; }
        if (charging)
        {
            float c = Mathf.Clamp01((Time.unscaledTime - chargeStart) / 0.9f);
            string throwKey = GameControls.UsingGamepad ? "RT" : "LMB";
            string dropKey = GameControls.UsingGamepad ? "B" : "G";
            SetHint($"Carrying {name} · [{throwKey}] Release to throw [{dropKey}] Put down (power {Mathf.RoundToInt(c * 100f)}%)");
            if (!Input.GetMouseButton(0) && !GameControls.PadHeld(GameControls.Action.Throw))
            {
                charging = false;
                ThrowCarried(net, c);
            }
            return;
        }
        SetHint(GameControls.UsingGamepad
            ? $"Carrying {name} · [Hold RT] Charge throw [B] Put down"
            : $"Carrying {name} · [Hold LMB] Charge throw [G] Put down");
    }

    /// <summary>Throw the friend we carry (charge 0..1 → 3–6 m).</summary>
    public void ThrowCarried(HandsNet net, float charge01)
    {
        Vector3 fwd = transform.forward;
        var cam = Camera.main;
        if (cam != null) { Vector3 f = cam.transform.forward; f.y = 0f; if (f.sqrMagnitude > 0.01f) fwd = f.normalized; }
        float speed = Mathf.Lerp(5.5f, 8f, charge01);
        Vector3 vel = (fwd + Vector3.up * 0.55f).normalized * speed;
        net.LocalThrowCarried(vel);
        Sfx.Play("throw", HandPoint, 1f, 0.8f);
        GameHUD.FloatingText(HandPoint + Vector3.up * 0.8f, "HNNNG!", new Color(1f, 0.55f, 0.15f), 1.1f);
        SetHint(null);
    }

    void ApplySpeed(float f)
    {
        if (tpc == null) return;
        if (!slowApplied) { baseMove = tpc.MoveSpeed; baseSprint = tpc.SprintSpeed; slowApplied = true; }
        carrySlow = f;
        tpc.MoveSpeed = baseMove * f;
        tpc.SprintSpeed = baseSprint * f;
    }

    void RestoreSpeed()
    {
        if (!slowApplied || tpc == null) return;
        slowApplied = false;
        tpc.MoveSpeed = baseMove;
        tpc.SprintSpeed = baseSprint;
    }

    // ---- control lock (keeps mouse look)

    void Lock()
    {
        if (locked) return;
        locked = true;
        if (tpc != null)
        {
            lockYaw = yawField != null ? (float)yawField.GetValue(tpc) : transform.eulerAngles.y;
            lockPitch = pitchField != null ? (float)pitchField.GetValue(tpc) : 0f;
            tpc.enabled = false;
        }
        if (input != null) { input.move = Vector2.zero; input.jump = false; input.sprint = false; }
        if (anim != null)
        {
            anim.SetFloat("Speed", 0f);
            anim.SetFloat("MotionSpeed", 1f);
            anim.SetBool("Grounded", true);
            anim.SetBool("Jump", false);
            anim.SetBool("FreeFall", false);
        }
        if (Carrier != null && Carrier.IsCarrying && State != BodyState.Riding && State != BodyState.Normal && !Carrier.ExternalControl && !flying)
            Carrier.Drop(); // you can't hold a bag while flat on your back
    }

    void Unlock()
    {
        if (!locked) return;
        locked = false;
        if (cc != null && !cc.enabled && !riding && !carried) cc.enabled = true;
        if (tpc != null)
        {
            if (yawField != null) yawField.SetValue(tpc, lockYaw);
            if (pitchField != null) pitchField.SetValue(tpc, lockPitch);
            tpc.enabled = true;
        }
    }

    void LockedLook()
    {
        if (tpc == null || input == null || tpc.CinemachineCameraTarget == null) return;
        if (input.look.sqrMagnitude >= 0.01f && !PlayerController.IsUIBlocking)
        {
            lockYaw += input.look.x;
            lockPitch = Mathf.Clamp(lockPitch + input.look.y, tpc.BottomClamp, tpc.TopClamp);
        }
        tpc.CinemachineCameraTarget.transform.rotation = Quaternion.Euler(lockPitch + tpc.CameraAngleOverride, lockYaw, 0f);
        if (input != null) { input.move = Vector2.zero; input.jump = false; }
    }

    // ---- owner physics: knockback, flight, gravity while locked

    void StepKnock()
    {
        if (cc == null || !cc.enabled || riding || carried) return;
        float dt = Time.deltaTime;
        Vector3 step = Vector3.zero;
        if (knockT < 1f)
        {
            float t0 = knockT;
            knockT = Mathf.Min(1f, knockT + dt / Mathf.Max(0.05f, knockDur));
            float s0 = 2f * t0 - t0 * t0, s1 = 2f * knockT - knockT * knockT;
            step += knockDir * knockDist * (s1 - s0);
            if (t0 < 0.5f) step += Vector3.up * knockHop * (Mathf.Min(knockT, 0.5f) - t0) * 2f;
        }
        if (locked && !flying)
        {
            // TPC is off: our own gravity.
            vy = cc.isGrounded ? -2f : Mathf.Max(vy - 15f * dt, -20f);
            step += Vector3.up * vy * dt;
        }
        if (step.sqrMagnitude > 0f) cc.Move(step);
    }

    void StepFlight(HandsNet net, BodyState st)
    {
        if (!flying || cc == null) return;
        if (!cc.enabled) cc.enabled = true;
        float dt = Time.deltaTime;
        flyVel += Vector3.up * -15f * dt;
        var flags = cc.Move(flyVel * dt);
        bool landed = Time.time - flyStart > 0.2f && ((flags & CollisionFlags.Below) != 0 || cc.isGrounded);
        if ((flags & CollisionFlags.Sides) != 0) { flyVel.x *= 0.3f; flyVel.z *= 0.3f; }
        if (landed || Time.time - flyStart > 2.5f || transform.position.y < Carryable.KillPlaneY)
        {
            flying = false;
            vy = -2f;
            Sfx.Play("thud", transform.position, 0.9f, 0.85f);
            Fx.Dust(transform.position, 1.2f);
            Juice.Shake(0.25f, 0.2f);
            net.LocalLanded();
        }
    }

    // ==================================================================================== everyone: look and pinning

    void UpdateLook(BodyNet b)
    {
        var st = b.State;
        bool flat = st == BodyState.Flopped || st == BodyState.KnockedOut || st == BodyState.Carried || st == BodyState.Thrown || (IsLocal && flying);
        float target = flat ? 1f : 0f;
        lie = Mathf.MoveTowards(lie, target, Time.deltaTime * (flat ? 6f : 4f));
        if (stars != null && st != BodyState.KnockedOut) { Destroy(stars.gameObject); stars = null; }
    }

    void LateUpdate()
    {
        var net = Net;
        if (net == null || !net.Active || Player == null || !Player.IsSpawned) { ApplyLie(0f, 0f); return; }
        var b = Body;
        riding = b.State == BodyState.Riding;
        carried = b.State == BodyState.Carried;
        if (riding && !RideRider.IsFlingFlying(this)) // F1: a flung rider flies, not pinned to the step
        {
            var hs = HitchStep.Find(b.other);
            if (hs != null)
            {
                var pose = hs.StepPose(b.step);
                if (IsLocal && cc != null && cc.enabled) cc.enabled = false;
                SetPose(pose.position, pose.rotation);
                pinVel = hs.Velocity;
            }
        }
        else if (carried)
        {
            var c = PlayerBody.For(b.other);
            if (c != null)
            {
                if (IsLocal && cc != null && cc.enabled) cc.enabled = false;
                // Across the carrier's arms, sideways.
                Vector3 p = c.transform.position + c.transform.forward * 0.45f + Vector3.up * 0.75f - c.transform.right * 0.8f;
                SetPose(p, c.transform.rotation * Quaternion.Euler(0f, 90f, 0f));
            }
        }
        float spin = b.State == BodyState.Thrown || (IsLocal && flying) ? Time.time * 720f : 0f;
        ApplyLie(lie, spin);
    }

    void SetPose(Vector3 p, Quaternion r)
    {
        transform.SetPositionAndRotation(p, r);
    }

    /// <summary>Tip the skeleton (Chuck's parts ride its bones) onto his back; a spin while flying.</summary>
    bool lieApplied;

    void ApplyLie(float amount, float spin)
    {
        if (skeleton == null) return;
        if (amount <= 0.001f && spin == 0f)
        {
            if (!lieApplied) return;
            lieApplied = false;
            skeleton.localRotation = skeletonRot;
            skeleton.localPosition = skeletonPos;
            return;
        }
        lieApplied = true;
        var r = Quaternion.Euler(-82f * amount + spin, 0f, 0f);
        skeleton.localRotation = r * skeletonRot;
        skeleton.localPosition = skeletonPos + Vector3.up * (0.22f * amount);
    }

    void HopOff()
    {
        if (cc == null) return;
        var hs = HitchStep.Find(Body.other);
        Vector3 v = pinVel;
        Vector3 p = transform.position - transform.forward * 0.6f + Vector3.up * 0.1f;
        SetPose(p, Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized, Vector3.up));
        cc.enabled = true;
        float speed = new Vector2(v.x, v.z).magnitude;
        if (speed > HandsTuning.HopOffSafeSpeed)
        {
            // Off a moving truck: a stumble along its velocity (§4.4).
            knockDir = new Vector3(v.x, 0f, v.z).normalized;
            knockDist = Mathf.Min(3f, speed * 0.3f);
            knockHop = 0.1f;
            knockDur = 0.3f;
            knockT = 0f;
            localStunUntil = Time.time + (speed > 6f ? 1.2f : 0.5f);
            GameHUD.FloatingText(transform.position + Vector3.up * 2f, speed > 6f ? "TUMBLE!" : "whoa", new Color(1f, 0.7f, 0.3f), 1f);
        }
        Sfx.Play("thud", transform.position, 0.5f, 1.2f);
    }

    // ==================================================================================== hints

    /// <summary>A nearby crew member in front of the owner, for the optional crew-action hint.</summary>
    PlayerBody NearbyTeammate(float range)
    {
        PlayerBody best = null;
        float bestSq = range * range;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        foreach (var other in all)
        {
            if (other == null || other == this || other.Player == null || !other.Player.IsSpawned) continue;
            if (other.Player.IsDriving || other.Player.IsPlayingChess) continue;
            Vector3 to = other.transform.position - transform.position;
            to.y = 0f;
            float sq = to.sqrMagnitude;
            if (sq > bestSq) continue;
            if (sq > 0.09f && Vector3.Dot(fwd.normalized, to.normalized) < 0.15f) continue;
            best = other;
            bestSq = sq;
        }
        return best;
    }

    void SetHint(string text)
    {
        if (!IsLocal) return;
        if (string.IsNullOrEmpty(text))
        {
            if (!hintOwned) return;
            hintOwned = false;
            lastHint = null;
            if (Carrier == null || !Carrier.IsCarrying) GameHUD.SetContextHint(null);
            return;
        }
        if (hintOwned && text == lastHint) return;
        hintOwned = true;
        lastHint = text;
        GameHUD.SetContextHint(text);
    }

    static float Random01(Vector3 p) => Mathf.Abs(Mathf.Sin(p.x * 12.9898f + p.z * 78.233f) * 43758.5453f) % 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        yawField = null;
        pitchField = null;
    }
}

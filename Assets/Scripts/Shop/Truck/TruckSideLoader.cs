using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Grabby Arm™ side-loader (truck_arm 1-3, Docs/Progression.md §6.1), numbers from <see cref="UpgradeEffects.Arm"/>:
///   L1  hold X while stopped (&lt; 0.5 m/s): grabs resting curb bags on the RIGHT within 2.4 m of the truck's side;
///       deploy 3 s, then 2.5 s per bag; 5% fumble (the bag bonks off the roof onto the road, never bursts).
///   L2  X toggles AUTO: both sides, while ≤ 3 m/s, reach 3.2 m, deploy 1.2 s, 1.6 s per bag, no fumbles.
///   L3  Octo-Arm: AUTO, both sides, ≤ 4 m/s, reach 5 m, deploy 0.5 s, 0.9 s per bag, plus the Litter Vac (loose items
///       within 3 m are sucked into the hopper).
/// Rules: only Resting curb bags with no Holder, not in scripted flight, that fit (GameSession.CanFitInTruck). A grab is
/// bag.SetInFlight() → the arm carries it → bag.TossArcInto(hopper, 0.35) so the fee, curb bonus, record removal,
/// "Curb clean!" and FULL toasts all flow through TruckHopper.Accept exactly like a hand toss (TrashBag.ByArm is set
/// first so the BagCollected fact says byArm). Target choice runs on the host. A cycle aborts if a player grabs the bag.
/// The arm is renderer-only primitives (no colliders): a turret, boom, telescoping forearm and a googly-eyed claw.
/// One bag at a time (the two arms alternate), so it stays slower than a skilled player on foot until L3.
/// Co-op (Multiplayer.md §4.3, later): the shotgun rider operates it and AUTO is off; there is no shotgun seat yet.
/// Added by <see cref="ShopEffectsRunner"/> to every truck.
/// </summary>
[DisallowMultipleComponent]
public class TruckSideLoader : MonoBehaviour
{
    public KeyCode armKey = KeyCode.X;
    [Tooltip("Tests / debug: behave as if the local driver were holding X (L1) or AUTO were on (L2+), seated or not.")]
    public bool debugHold;

    public const string VisualRootName = "Grabby Arm";

    enum Phase { Idle, Reach, Grab, Lift, Release }

    // Truck metres (see ShopModKit): shoulders sit high on the hopper body's flanks, a bit behind the cab.
    static readonly Vector3 ShoulderR = new Vector3(1.24f, 2.45f, 0.35f);
    static readonly Vector3 LiftPointR = new Vector3(0.9f, 3.85f, -1.95f);   // above the rear corner, then toss into the mouth
    static readonly Vector3 ReadyR = new Vector3(2.35f, 1.75f, 0.6f);
    static readonly Vector3 StowClawR = new Vector3(1.32f, 2.05f, 1.05f);
    static readonly Vector3 StowElbowR = new Vector3(1.3f, 2.85f, -0.75f);

    class ArmRig
    {
        public int side;                          // +1 right, -1 left
        public float boomLen = 1.5f;
        public Transform root, turret, boom, piston, elbow, fore, foreInner, palm;
        public Transform[] fingers;
        public Transform[] pupils;
        public Vector3[] pupilBase;
        public Transform[] miniClaws;
        public Vector3 clawLocal, clawVel;        // truck metres + spring velocity
        public Vector3 desired;                   // where the claw wants to be (truck metres)
        public float open01 = 0.4f;
        public Vector3 pupilOffset, pupilVel;
        public Vector3 lastClawWorld;
    }

    TruckControllerV2 truck;
    Rigidbody body;
    ShopTruckMods mods;
    TruckHopper hopper;
    Transform visualRoot;
    ArmRig right, left;
    int builtLevel = -1;

    bool autoOn;
    float deploy01;                 // 0 stowed .. 1 deployed (both arms)
    bool wasDeployed;
    Phase phase = Phase.Idle;
    float phaseT, phaseDur;
    ArmRig activeArm;
    TrashBag target;
    Vector3 reachStartLocal, grabLocal, grabOffset;
    Quaternion grabRotLocal = Quaternion.identity;
    bool holding;                   // the bag is in the claw (SetInFlight done)
    float lastWorkWanted = -99f, nextScanAt, nextVacAt, tryCooldown;
    bool candidateNear, lastTryEmpty;
    float lastTooFastFx = -99f, lastFullToast = -99f, lastStopToast = -99f, lastNoBagsHint = -99f;
    static bool onlineShown, fumbleToastShown;
    readonly Dictionary<string, int> attempts = new Dictionary<string, int>();

    /// <summary>L2+: AUTO is on (X toggles it).</summary>
    public bool AutoOn => autoOn;
    /// <summary>0..1 how far the arms are unfolded.</summary>
    public float Deploy01 => deploy01;
    /// <summary>Bags this arm has tossed into the hopper this session (tests / stats).</summary>
    public int BagsTossed { get; private set; }
    /// <summary>Loose items the Litter Vac has sucked up this session.</summary>
    public int ItemsVacuumed { get; private set; }
    /// <summary>Bags fumbled onto the road this session.</summary>
    public int Fumbles { get; private set; }
    /// <summary>"Idle", "Reach", "Grab", "Lift", "Release" (tests / debug).</summary>
    public string PhaseName => phase.ToString();

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        var h = transform.Find("Hopper");
        if (h != null) hopper = h.GetComponent<TruckHopper>();
    }

    void OnEnable() => GameSession.UpgradeChanged += OnUpgradeChanged;

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        LetGo(Vector3.zero);
        phase = Phase.Idle;
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id != ShopIds.TruckArm) return;
        builtLevel = -1; // rebuild (L3 claws)
        if (!UpgradeEffects.Arm.auto) autoOn = false;
    }

    // =====================================================================
    // Frame
    // =====================================================================

    void Update() => Tick(Time.deltaTime);

    /// <summary>One arm step of dt seconds (Update; edit-mode tests step it by hand). Input is read only when seated.</summary>
    public void Tick(float dt)
    {
        var tier = UpgradeEffects.Arm;
        int level = Mathf.Clamp(UpgradeEffects.Level(ShopIds.TruckArm), 0, 3);
        if (!tier.present)
        {
            if (phase != Phase.Idle) AbortCycle();
            deploy01 = 0f;
            if (visualRoot != null) { Destroy(visualRoot.gameObject); visualRoot = null; right = left = null; activeArm = null; }
            builtLevel = 0;
            return;
        }
        if (builtLevel != level || visualRoot == null) Build(level, tier);
        if (mods == null) mods = GetComponent<ShopTruckMods>();
        if (hopper == null) { var h = transform.Find("Hopper"); if (h != null) hopper = h.GetComponent<TruckHopper>(); }

        float speed = body != null ? body.linearVelocity.magnitude : 0f;
        bool seated = truck != null && truck.IsLocalPlayerSeated && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning;

        // ---- input
        bool xHeld = false;
        if (seated)
        {
            if (tier.auto)
            {
                if (GameControls.Down(armKey, GameControls.Action.Arm)) ToggleAuto(tier);
            }
            else
            {
                xHeld = GameControls.Held(armKey, GameControls.Action.Arm);
                if (GameControls.Down(armKey, GameControls.Action.Arm) && speed > tier.maxSpeed && Time.time - lastStopToast > 2f)
                {
                    lastStopToast = Time.time;
                    GameHUD.Toast($"Stop the truck first! The Grabby Arm only works under {tier.maxSpeed:0.#} m/s.", ToastStyle.Info, 2f);
                    Sfx.Play("buzz", transform.position, 0.5f, 1.2f);
                }
            }
        }
        bool operating = debugHold || (seated && (tier.auto ? autoOn : xHeld));
        bool speedOk = speed <= tier.maxSpeed + (tier.auto ? 0.25f : 0.05f);
        bool host = ShopModKit.IsHost;

        // ---- AUTO only unfolds when there's work in reach (or it's mid-cycle)
        if (operating && host && Time.time >= nextScanAt)
        {
            nextScanAt = Time.time + 0.2f;
            candidateNear = phase != Phase.Idle || PickTarget(tier, 1.5f, out _, out _) != null;
            if (candidateNear) lastWorkWanted = Time.time;
        }
        bool wantDeployed = operating && speedOk && host && (!tier.auto || Time.time - lastWorkWanted < 2.5f || phase != Phase.Idle);
        if (operating && !speedOk && tier.auto && candidateNear && Time.time - lastTooFastFx > 4f)
        {
            lastTooFastFx = Time.time;
            GameHUD.FloatingText(Tp(new Vector3(0f, 4.4f, 0f)), $"too fast for the arm (≤ {tier.maxSpeed:0} m/s)", new Color(1f, 0.75f, 0.3f), 0.9f);
        }

        // ---- deploy / stow
        bool midCarry = holding && (phase == Phase.Lift || phase == Phase.Release);
        float before = deploy01;
        if (wantDeployed || midCarry) deploy01 = Mathf.MoveTowards(deploy01, 1f, dt / Mathf.Max(0.05f, tier.deploySeconds));
        else deploy01 = Mathf.MoveTowards(deploy01, 0f, dt / 0.6f);
        if (before <= 0f && deploy01 > 0f) OnDeployStart(tier);
        if (before > 0f && deploy01 <= 0f) OnStowed();
        if (deploy01 >= 1f && !wasDeployed) { wasDeployed = true; OnDeployed(); }
        if (deploy01 < 1f) wasDeployed = false;

        // ---- cycle
        if (phase != Phase.Idle && (!wantDeployed && !midCarry))
            AbortCycle();
        tryCooldown -= dt;
        if (phase == Phase.Idle && deploy01 >= 1f && wantDeployed && host && tryCooldown <= 0f)
        {
            tryCooldown = 0.1f; // looking for the next bag ten times a second is plenty
            TryStartCycle(tier);
        }
        if (phase != Phase.Idle) TickCycle(tier, dt);

        // ---- Litter Vac (L3)
        if (tier.litterVac && wantDeployed && deploy01 >= 1f && host && Time.time >= nextVacAt)
        {
            nextVacAt = Time.time + 0.25f;
            Vacuum(tier);
        }

        // ---- pose
        PoseArm(right, dt);
        PoseArm(left, dt);

        if (seated && !tier.auto && xHeld && deploy01 >= 1f && phase == Phase.Idle && lastTryEmpty && Time.time - lastNoBagsHint > 4f)
        {
            lastNoBagsHint = Time.time;
            GameHUD.FloatingText(Tp(new Vector3(2.4f, 3.2f, 0.6f)), "no bags in reach. hug the curb (right side)!", new Color(1f, 0.85f, 0.4f), 0.85f);
        }
    }

    void LateUpdate() => FollowBag();

    /// <summary>The bag rides the claw (after the spring moved it this frame).</summary>
    public void FollowBag()
    {
        if (holding && target != null && activeArm != null && (phase == Phase.Lift || phase == Phase.Grab || phase == Phase.Release))
        {
            Vector3 claw = Tp(activeArm.clawLocal);
            float sway = Mathf.Sin(Time.time * 9f) * 6f;
            target.transform.position = claw + grabOffset;
            target.transform.rotation = Quaternion.AngleAxis(sway, transform.forward) * transform.rotation * grabRotLocal;
        }
    }

    // =====================================================================
    // Input / state juice
    // =====================================================================

    void ToggleAuto(UpgradeEffects.ArmTier tier)
    {
        autoOn = !autoOn;
        string name = tier.litterVac ? "OCTO-ARM 9000" : "GRABBY ARM";
        GameHUD.Toast(autoOn ? $"{name}: AUTO ON. Crawl past the curbs (≤ {tier.maxSpeed:0} m/s)." : $"{name}: AUTO OFF. Arms folded. Sulking.",
            autoOn ? ToastStyle.Good : ToastStyle.Info, 2.2f);
        Sfx.Play(autoOn ? "notify" : "click", transform.position, 0.7f, autoOn ? 1.2f : 0.8f);
        GameHUD.FloatingText(Tp(new Vector3(0f, 4.3f, 0.5f)), autoOn ? "AUTO-GRAB: ON" : "auto-grab: off", autoOn ? new Color(0.5f, 1f, 0.5f) : new Color(0.8f, 0.8f, 0.8f), 1.1f);
        if (mods != null) mods.RefreshHint();
    }

    void OnDeployStart(UpgradeEffects.ArmTier tier)
    {
        Vector3 p = Tp(ShoulderR);
        Sfx.Play("compactor", p, 0.45f, 1.6f);
        Sfx.Play("squeak", p, 0.5f, 0.7f);
        if (!onlineShown)
        {
            onlineShown = true;
            GameHUD.FloatingText(Tp(new Vector3(0f, 4.6f, 0.4f)), tier.litterVac ? "OCTO-ARM 9000: ONLINE" : "GRABBY ARM™: ONLINE", new Color(1f, 0.8f, 0.2f), 1.3f);
        }
    }

    void OnDeployed()
    {
        Sfx.Play("click", Tp(ShoulderR), 0.6f, 1.3f);
        foreach (var a in Arms()) if (a != null && a.palm != null) Juice.Punch(a.palm, 0.3f, 0.2f);
    }

    void OnStowed()
    {
        Sfx.Play("click", Tp(ShoulderR), 0.5f, 0.8f);
    }

    IEnumerable<ArmRig> Arms() { yield return right; yield return left; }

    // =====================================================================
    // Targeting (host)
    // =====================================================================

    /// <summary>Best grabbable curb bag in reach (nearest to its arm's claw), or null. slack widens the reach (AUTO's look-ahead).</summary>
    TrashBag PickTarget(UpgradeEffects.ArmTier tier, float slack, out int side, out string why)
    {
        side = 1; why = null;
        TrashBag best = null;
        float bestScore = float.MaxValue;
        bool sawFull = false;
        foreach (var stop in PickupStop.All)
        {
            if (stop == null) continue;
            // Cheap reject: stops far from the truck.
            if ((stop.transform.position - transform.position).sqrMagnitude > 18f * 18f) continue;
            var bags = stop.SpawnedBags;
            for (int i = 0; i < bags.Count; i++)
            {
                var b = bags[i];
                if (!Grabbable(b)) continue;
                Vector3 local = ShopModKit.ToTruckMetres(transform, b.WorldCenter);
                int s = local.x >= 0f ? 1 : -1;
                if (!tier.bothSides && s < 0) continue;
                float lateral = Mathf.Abs(local.x) - ShopModKit.BodyHalfWidth;
                if (lateral > tier.reach + slack || lateral < -0.4f) continue;
                if (local.z < ShopModKit.BodyRearZ - 0.8f || local.z > ShopModKit.BodyNoseZ + 0.5f) continue;
                if (local.y < -1.5f || local.y > 2.8f) continue;
                if (!GameSession.CanFitInTruck(b.MassKg)) { sawFull = true; continue; }
                var arm = s > 0 ? right : left;
                Vector3 from = arm != null ? arm.clawLocal : new Vector3(s * ReadyR.x, ReadyR.y, ReadyR.z);
                float score = (local - from).sqrMagnitude + (lateral > tier.reach ? 100f : 0f);
                if (score < bestScore) { bestScore = score; best = b; side = s; }
            }
        }
        if (best == null && sawFull) why = "full";
        return best;
    }

    static bool Grabbable(TrashBag b) =>
        b != null && !b.HasBurst && b.State == CarryState.Resting && b.Holder == null && !b.IsInScriptedFlight && b.IsCurbBag;

    void TryStartCycle(UpgradeEffects.ArmTier tier)
    {
        var bag = PickTarget(tier, 0f, out int side, out string why);
        lastTryEmpty = bag == null;
        if (bag == null)
        {
            if (why == "full" && Time.time - lastFullToast > 10f)
            {
                lastFullToast = Time.time;
                GameHUD.Toast($"Truck's full ({GameSession.TruckLoadKg:0}/{GameSession.TruckCapacityKg:0} kg)! The Grabby Arm is on strike.", ToastStyle.Bad, 2.5f);
                Sfx.Play("truck_full", transform.position, 0.7f, 1.1f);
            }
            return;
        }
        target = bag;
        activeArm = side > 0 ? right : left;
        if (activeArm == null) { target = null; return; }
        phase = Phase.Reach;
        phaseT = 0f;
        phaseDur = tier.secondsPerBag * 0.36f;
        reachStartLocal = activeArm.clawLocal;
        holding = false;
    }

    void AbortCycle()
    {
        if (holding) LetGo(Vector3.zero);
        target = null;
        holding = false;
        phase = Phase.Idle;
    }

    /// <summary>Drop whatever is in the claw back to physics (gently: never a burst).</summary>
    void LetGo(Vector3 velocity)
    {
        if (holding && target != null && target.State != CarryState.Consumed && target.Holder == null)
        {
            target.ByArm = false;
            target.EndCarry(velocity, false);
        }
        holding = false;
    }

    // =====================================================================
    // The grab cycle
    // =====================================================================

    void TickCycle(UpgradeEffects.ArmTier tier, float dt)
    {
        var arm = activeArm;
        if (arm == null) { AbortCycle(); return; }
        phaseT += dt / Mathf.Max(0.05f, phaseDur);
        float T = tier.secondsPerBag;

        // A player (or anything else) took the bag: let it go, quietly.
        if (target == null || target.State == CarryState.Consumed || target.HasBurst || (holding && target.Holder != null))
        {
            target = null;
            holding = false;
            phase = Phase.Idle;
            return;
        }

        switch (phase)
        {
            case Phase.Reach:
            {
                if (!Grabbable(target) || OutOfReach(tier, target)) { AbortCycle(); return; }
                Vector3 top = BagTopLocal(target);
                float k = Mathf.Clamp01(phaseT);
                Vector3 hover = top + Vector3.up * 0.65f;
                arm.open01 = Mathf.MoveTowards(arm.open01, 1f, dt * 4f);
                if (k < 0.7f) SetDesired(arm, Vector3.Lerp(reachStartLocal, hover, Mathf.SmoothStep(0f, 1f, k / 0.7f)));
                else SetDesired(arm, Vector3.Lerp(hover, top + Vector3.up * 0.12f, Mathf.SmoothStep(0f, 1f, (k - 0.7f) / 0.3f)));
                if (phaseT >= 1f) NextPhase(Phase.Grab, T * 0.12f);
                break;
            }
            case Phase.Grab:
            {
                if (!holding && (!Grabbable(target) || OutOfReach(tier, target))) { AbortCycle(); return; }
                Vector3 top = BagTopLocal(target);
                SetDesired(arm, top + Vector3.up * 0.08f);
                arm.open01 = Mathf.MoveTowards(arm.open01, 0f, dt / Mathf.Max(0.05f, phaseDur * 0.8f));
                if (phaseT >= 1f)
                {
                    if (!GameSession.CanFitInTruck(target.MassKg)) { AbortCycle(); return; }
                    Vector3 claw = Tp(arm.clawLocal);
                    if ((target.WorldCenter - claw).sqrMagnitude > 1.6f * 1.6f) { AbortCycle(); return; }
                    target.SetInFlight();
                    target.ByArm = true;
                    holding = true;
                    grabOffset = target.transform.position - claw;
                    // Hang the bag just under the claw.
                    Vector3 c = target.WorldCenter - target.transform.position;
                    grabOffset = -c + Vector3.down * 0.25f;
                    grabRotLocal = Quaternion.Inverse(transform.rotation) * target.transform.rotation;
                    grabLocal = arm.clawLocal;
                    Sfx.Play("squeak", claw, 0.55f, 1.3f);
                    Sfx.Play("rustle", claw, 0.7f, 1.1f);
                    if (arm.palm != null) Juice.Punch(arm.palm, 0.35f, 0.2f);
                    Juice.Squash(target.JuiceTarget, -0.25f, 0.25f);
                    GameHUD.FloatingText(claw + Vector3.up * 0.8f, ShopModKit.Pick(GrabWords), new Color(1f, 0.85f, 0.3f), 0.85f);
                    NextPhase(Phase.Lift, T * 0.37f);
                }
                break;
            }
            case Phase.Lift:
            {
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(phaseT));
                Vector3 lift = new Vector3(arm.side * LiftPointR.x, LiftPointR.y, LiftPointR.z);
                Vector3 ctrl = new Vector3(Mathf.Lerp(grabLocal.x, lift.x, 0.3f), Mathf.Max(grabLocal.y, lift.y) + 1.4f, Mathf.Lerp(grabLocal.z, lift.z, 0.4f));
                float u = 1f - k;
                SetDesired(arm, u * u * grabLocal + 2f * u * k * ctrl + k * k * lift);
                if (phaseT >= 1f) { Release(tier, arm); NextPhase(Phase.Release, T * 0.15f); }
                break;
            }
            case Phase.Release:
            {
                arm.open01 = Mathf.MoveTowards(arm.open01, 1f, dt * 6f);
                Vector3 lift = new Vector3(arm.side * LiftPointR.x, LiftPointR.y + 0.25f, LiftPointR.z - 0.3f);
                SetDesired(arm, lift);
                if (phaseT >= 1f) { phase = Phase.Idle; target = null; }
                break;
            }
        }
    }

    void NextPhase(Phase p, float dur)
    {
        phase = p;
        phaseT = 0f;
        phaseDur = Mathf.Max(0.05f, dur);
    }

    bool OutOfReach(UpgradeEffects.ArmTier tier, TrashBag b)
    {
        Vector3 local = ShopModKit.ToTruckMetres(transform, b.WorldCenter);
        float lateral = Mathf.Abs(local.x) - ShopModKit.BodyHalfWidth;
        return lateral > tier.reach + 0.8f || local.z < ShopModKit.BodyRearZ - 1.8f || local.z > ShopModKit.BodyNoseZ + 1.5f;
    }

    Vector3 BagTopLocal(TrashBag b)
    {
        Vector3 top = b.WorldCenter + Vector3.up * 0.3f;
        return ShopModKit.ToTruckMetres(transform, top);
    }

    /// <summary>Over the rear corner: toss it into the hopper (or fumble it onto the roof, L1).</summary>
    void Release(UpgradeEffects.ArmTier tier, ArmRig arm)
    {
        if (!holding || target == null) return;
        var bag = target;
        Vector3 claw = Tp(arm.clawLocal);
        string bagId = bag.Contents != null ? bag.Contents.bagId : bag.name;
        attempts.TryGetValue(bagId, out int n);
        attempts[bagId] = n + 1;
        bool fumble = tier.fumbleChance > 0f && UpgradeEffects.DetectRoll(bagId, "arm_fumble_" + n, tier.fumbleChance);

        if (hopper == null || fumble || !hopper.CanAccept(bag, out _))
        {
            holding = false;
            bag.ByArm = false;
            Vector3 v = body != null ? body.linearVelocity : Vector3.zero;
            // Bonk: up onto the roof and off the far side (never Thrown, so it can't burst).
            Vector3 across = -transform.right * arm.side;
            bag.EndCarry(v + across * 2.6f + Vector3.up * 3.4f, false);
            if (bag.Body != null) bag.Body.angularVelocity = transform.forward * 6f * arm.side;
            Fumbles++;
            Sfx.Play("thud", claw, 0.9f, 1.15f);
            Sfx.Play("squeak", claw, 0.6f, 0.6f);
            GameHUD.FloatingText(claw + Vector3.up * 0.9f, fumble ? "BONK! *fumble*" : "...where does it go?", new Color(1f, 0.55f, 0.3f), 1.2f);
            if (arm.palm != null) Juice.Punch(arm.palm, 0.4f, 0.3f);
            if (fumble && !fumbleToastShown)
            {
                fumbleToastShown = true;
                GameHUD.Toast("Fumble! The Grabby Arm has a 95% success rate. That's an A.", ToastStyle.Info, 3f);
            }
            if (hopper != null && !fumble) GameHUD.Toast("Truck's full! The arm dropped it.", ToastStyle.Bad, 2f);
            return;
        }

        holding = false;
        bag.TossArcInto(hopper, 0.35f);
        BagsTossed++;
        Sfx.Play("whoosh", claw, 0.8f, 1.15f);
        if (arm.palm != null) Juice.Punch(arm.palm, 0.3f, 0.2f);
    }

    static readonly string[] GrabWords = { "GRAB!", "MINE.", "*grab*", "GOT IT", "yoink", "clamp!" };

    // =====================================================================
    // Litter Vac (L3)
    // =====================================================================

    void Vacuum(UpgradeEffects.ArmTier tier)
    {
        if (hopper == null) return;
        var all = LooseItem.All;
        float groundY = transform.position.y + 0.4f;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null || li.State != CarryState.Resting || li.Holder != null || li.IsInScriptedFlight) continue;
            Vector3 p = li.transform.position;
            if (Mathf.Abs(p.y - groundY) > 0.9f) continue;          // on the street, not on a table
            Vector3 local = ShopModKit.ToTruckMetres(transform, p);
            float lateral = Mathf.Abs(local.x) - ShopModKit.BodyHalfWidth;
            if (lateral > tier.vacRadius || local.z < ShopModKit.BodyRearZ - tier.vacRadius || local.z > ShopModKit.BodyNoseZ + 1f) continue;
            if (!hopper.CanAccept(li, out _)) continue;
            li.SetInFlight();
            li.TossArcInto(hopper, 0.45f);
            ItemsVacuumed++;
            Sfx.Play("whoosh", p, 0.6f, 1.6f);
            Sfx.Play("pop", p, 0.4f, 1.5f);
            GameHUD.FloatingText(p + Vector3.up * 0.7f, ShopModKit.Pick(VacWords), new Color(0.6f, 0.9f, 1f), 0.8f);
            Fx.Dust(p, 0.4f);
            return; // one per tick: a satisfying slurp-slurp-slurp
        }
    }

    static readonly string[] VacWords = { "SHLUUURP", "*slurp*", "VWOOP", "litter? GONE." };

    // =====================================================================
    // Pose
    // =====================================================================

    void SetDesired(ArmRig arm, Vector3 local) => arm.desired = local;

    void PoseArm(ArmRig arm, float dt)
    {
        if (arm == null || arm.root == null) return;
        int s = arm.side;
        bool cycling = arm == activeArm && phase != Phase.Idle;
        Vector3 stowC = Mirror(StowClawR, s), readyC = Mirror(ReadyR, s);
        float d = cycling ? 1f : Mathf.SmoothStep(0f, 1f, deploy01);
        if (!cycling)
        {
            // Idle: hang at the ready pose with a little breathing bob, or fold up.
            Vector3 bob = new Vector3(0f, Mathf.Sin(Time.time * 2.1f + s) * 0.06f, Mathf.Sin(Time.time * 1.3f) * 0.05f) * d;
            arm.desired = Vector3.Lerp(stowC, readyC, d) + bob;
            arm.open01 = Mathf.MoveTowards(arm.open01, d > 0.9f ? 0.55f : 0.1f, dt * 3f);
        }

        // Underdamped spring in truck space: snappy, with a derpy wobble.
        const float K = 240f, C = 18f;
        Vector3 acc = (arm.desired - arm.clawLocal) * K - arm.clawVel * C;
        arm.clawVel += acc * dt;
        arm.clawLocal += arm.clawVel * dt;

        Vector3 S = Tp(Mirror(ShoulderR, s));
        Vector3 Cw = Tp(arm.clawLocal);
        Vector3 up = transform.up;
        Vector3 flat = Vector3.ProjectOnPlane(Cw - S, up);
        float hd = flat.magnitude;
        Vector3 sideDir = transform.right * s;
        flat = hd > 0.05f ? flat / hd : sideDir;
        float theta = Mathf.Lerp(68f, 18f, Mathf.Clamp01(hd / 4.5f)) * Mathf.Deg2Rad;
        Vector3 eIk = S + (flat * Mathf.Cos(theta) + up * Mathf.Sin(theta)) * arm.boomLen;
        Vector3 eStow = Tp(Mirror(StowElbowR, s));
        Vector3 E = Vector3.Lerp(eStow, eIk, d);

        Segment(arm.boom, S, E, 0.2f);
        if (arm.piston != null) Segment(arm.piston, S - up * 0.32f + sideDir * 0.05f, Vector3.Lerp(S, E, 0.55f), 0.09f);
        // Telescoping forearm: an outer sleeve of fixed length and an inner rod that slides out.
        Vector3 fe = Cw + (E - Cw).normalized * 0.22f; // stop above the palm
        float foreLen = (fe - E).magnitude;
        float sleeve = Mathf.Min(foreLen, arm.boomLen * 0.75f);
        Vector3 dirF = foreLen > 1e-3f ? (fe - E) / foreLen : -up;
        Segment(arm.fore, E, E + dirF * sleeve, 0.16f);
        Segment(arm.foreInner, E + dirF * Mathf.Max(0.05f, sleeve - 0.1f), fe, 0.1f);
        arm.turret.position = S;
        arm.turret.rotation = Quaternion.LookRotation(sideDir, up);
        if (arm.elbow != null) arm.elbow.position = E;

        // Claw: palm faces outward, "down" points along the forearm.
        Vector3 down = dirF;
        Vector3 fwd = Vector3.ProjectOnPlane(sideDir, down);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(transform.forward, down);
        arm.palm.position = Cw - down * 0.12f;
        arm.palm.rotation = Quaternion.LookRotation(fwd.normalized, -down);
        float ang = Mathf.Lerp(4f, 42f, arm.open01);
        for (int i = 0; i < arm.fingers.Length; i++)
        {
            var f = arm.fingers[i];
            if (f == null) continue;
            float around = 360f * i / arm.fingers.Length;
            f.localRotation = Quaternion.Euler(0f, around, 0f) * Quaternion.Euler(0f, 0f, ang);
        }

        // Googly pupils lag behind the claw's motion.
        Vector3 vel = dt > 0f ? (Cw - arm.lastClawWorld) / dt : Vector3.zero;
        arm.lastClawWorld = Cw;
        Vector3 lv = arm.palm.InverseTransformDirection(vel);
        Vector3 goal = Vector3.ClampMagnitude(new Vector3(-lv.x, -lv.y - 2f, 0f) * 0.012f, 0.035f);
        if (cycling && target != null && phase == Phase.Reach)
        {
            Vector3 look = arm.palm.InverseTransformDirection((target.WorldCenter - arm.palm.position).normalized);
            goal = new Vector3(look.x, look.y, 0f) * 0.035f;
        }
        arm.pupilVel += ((goal - arm.pupilOffset) * 220f - arm.pupilVel * 9f) * dt;
        arm.pupilOffset += arm.pupilVel * dt;
        arm.pupilOffset = Vector3.ClampMagnitude(arm.pupilOffset, 0.04f);
        for (int i = 0; i < arm.pupils.Length; i++)
            if (arm.pupils[i] != null) arm.pupils[i].localPosition = arm.pupilBase[i] + arm.pupilOffset;

        // Octo-Arm: little extra claws along the forearm wiggle for no reason.
        if (arm.miniClaws != null)
            for (int i = 0; i < arm.miniClaws.Length; i++)
            {
                var m = arm.miniClaws[i];
                if (m == null) continue;
                float k = 0.25f + 0.25f * i;
                m.position = Vector3.Lerp(E, fe, k);
                float w = Mathf.Sin(Time.time * (5f + i) + i * 1.7f) * 35f;
                m.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(w + 120f * i, dirF) * sideDir, dirF) * Quaternion.Euler(90f, 0f, 0f);
            }
    }

    static Vector3 Mirror(Vector3 v, int side) => new Vector3(v.x * side, v.y, v.z);

    void Segment(Transform seg, Vector3 a, Vector3 b, float thick)
    {
        if (seg == null) return;
        Vector3 d = b - a;
        float len = Mathf.Max(0.02f, d.magnitude);
        seg.position = (a + b) * 0.5f;
        Vector3 hint = Mathf.Abs(Vector3.Dot(d.normalized, transform.up)) > 0.95f ? transform.forward : transform.up;
        seg.rotation = Quaternion.LookRotation(d / len, hint);
        seg.localScale = new Vector3(thick, thick, len);
    }

    Vector3 Tp(Vector3 metres) => transform.position + transform.rotation * metres;

    // =====================================================================
    // Visuals (renderer-only)
    // =====================================================================

    void Build(int level, UpgradeEffects.ArmTier tier)
    {
        builtLevel = level;
        visualRoot = ShopModKit.MetreRoot(transform, VisualRootName);
        right = BuildArm(1, level);
        left = tier.bothSides ? BuildArm(-1, level) : null;
        if (activeArm != null && activeArm.side < 0 && left == null) AbortCycle();
        activeArm = activeArm == null ? null : activeArm.side > 0 ? right : left;
        if (Application.isPlaying && deploy01 <= 0f)
        {
            Juice.PopIn(visualRoot, 0.35f);
            Fx.Sparkle(visualRoot, new Color(1f, 0.8f, 0.2f), 1.2f);
        }
    }

    ArmRig BuildArm(int side, int level)
    {
        var yellow = ShopMaterials.Get("mat/shop_hazard_yellow", new Color(1f, 0.78f, 0.1f), 0.45f);
        var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var chrome = ShopMaterials.Get("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
        var red = ShopMaterials.Get("mat/shop_red", new Color(0.9f, 0.18f, 0.15f), 0.5f);
        var white = ShopMaterials.Get("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);

        var a = new ArmRig { side = side };
        a.boomLen = level >= 3 ? 2.1f : level == 2 ? 1.75f : 1.5f;
        a.root = new GameObject(side > 0 ? "Arm R" : "Arm L").transform;
        a.root.SetParent(visualRoot, false);

        a.turret = ShopModKit.Pivot("Turret", a.root, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Base", a.turret, black, Vector3.zero, new Vector3(0.46f, 0.08f, 0.46f), new Vector3(90f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Hub", a.turret, yellow, new Vector3(0f, 0f, 0.1f), new Vector3(0.3f, 0.08f, 0.3f), new Vector3(90f, 0f, 0f));
        a.boom = Seg("Boom", a.root, yellow, black, true);
        a.piston = Seg("Piston", a.root, chrome, null, false);
        a.elbow = ShopModKit.Prim(PrimitiveType.Sphere, "Elbow", a.root, black, Vector3.zero, Vector3.one * 0.26f);
        a.fore = Seg("Forearm", a.root, yellow, black, true);
        a.foreInner = Seg("Telescope", a.root, chrome, null, false);

        a.palm = ShopModKit.Pivot("Claw", a.root, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cube, "Palm", a.palm, black, new Vector3(0f, 0.02f, 0f), new Vector3(0.36f, 0.1f, 0.3f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Wrist", a.palm, red, new Vector3(0f, 0.1f, 0f), new Vector3(0.14f, 0.06f, 0.14f));
        int nf = level >= 3 ? 4 : 2;
        a.fingers = new Transform[nf];
        for (int i = 0; i < nf; i++)
        {
            float around = 360f * i / nf;
            var hinge = ShopModKit.Pivot("Finger Hinge", a.palm, Quaternion.Euler(0f, around, 0f) * new Vector3(0.15f, -0.03f, 0f));
            ShopModKit.Prim(PrimitiveType.Cube, "Finger", hinge, chrome, new Vector3(0.02f, -0.17f, 0f), new Vector3(0.05f, 0.32f, 0.12f));
            ShopModKit.Prim(PrimitiveType.Cube, "Tip", hinge, red, new Vector3(-0.03f, -0.33f, 0f), new Vector3(0.09f, 0.06f, 0.12f));
            a.fingers[i] = hinge;
        }
        // Googly eyes on the claw's outward face (it's watching you work).
        a.pupils = new Transform[2];
        a.pupilBase = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -0.09f : 0.09f;
            ShopModKit.Prim(PrimitiveType.Sphere, "Eye", a.palm, white, new Vector3(x, 0.07f, 0.16f), new Vector3(0.13f, 0.15f, 0.05f));
            a.pupilBase[i] = new Vector3(x, 0.06f, 0.19f);
            a.pupils[i] = ShopModKit.Prim(PrimitiveType.Sphere, "Pupil", a.palm, black, a.pupilBase[i], new Vector3(0.06f, 0.07f, 0.025f));
        }

        if (level >= 3)
        {
            a.miniClaws = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                var m = ShopModKit.Pivot("Mini Claw", a.root, Vector3.zero);
                ShopModKit.Prim(PrimitiveType.Cylinder, "Stalk", m, chrome, new Vector3(0f, 0.12f, 0f), new Vector3(0.04f, 0.12f, 0.04f));
                ShopModKit.Prim(PrimitiveType.Cube, "Pincer", m, red, new Vector3(0.03f, 0.27f, 0f), new Vector3(0.03f, 0.1f, 0.05f), new Vector3(0f, 0f, -18f));
                ShopModKit.Prim(PrimitiveType.Cube, "Pincer", m, red, new Vector3(-0.03f, 0.27f, 0f), new Vector3(0.03f, 0.1f, 0.05f), new Vector3(0f, 0f, 18f));
                a.miniClaws[i] = m;
            }
            // The Litter Vac nozzle hangs under the turret.
            ShopModKit.Prim(PrimitiveType.Cylinder, "Vac Nozzle", a.turret, black, new Vector3(0f, -0.35f, 0.15f), new Vector3(0.16f, 0.2f, 0.16f));
        }

        // Start folded, wherever we are.
        a.clawLocal = a.desired = Mirror(StowClawR, side);
        a.lastClawWorld = Tp(a.clawLocal);
        return a;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        onlineShown = false;
        fumbleToastShown = false;
    }

    /// <summary>A box segment (unit cube scaled along z by Segment()), optionally with a black hazard band.</summary>
    static Transform Seg(string name, Transform parent, Material mat, Material band, bool striped)
    {
        var t = ShopModKit.Pivot(name, parent, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cube, "Body", t, mat, Vector3.zero, Vector3.one);
        if (striped && band != null)
            for (int i = 0; i < 3; i++)
                ShopModKit.Prim(PrimitiveType.Cube, "Band", t, band, new Vector3(0f, 0f, -0.3f + i * 0.3f), new Vector3(1.04f, 1.04f, 0.07f));
        return t;
    }
}

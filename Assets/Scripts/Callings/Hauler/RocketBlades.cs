using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rocket Blades (Hauler calling, <see cref="CallingIds.RocketBlades"/>; numbers in <see cref="HaulerTuning"/>), on every
/// player copy (added by <see cref="HaulerRuntime"/>).
/// <list type="bullet">
/// <item><b>Owner, level 1 - skates.</b> Sprinting is skating: after the third-person controller has moved you, the frame is
/// corrected with <c>CharacterController.Move</c> to a velocity this component owns (the PlayerTells precedent), so you build
/// speed, carve wide at pace, glide when you let go of the stick and toe-stop when you let go of sprint. Top speed is a
/// multiple of the controller's own sprint speed, so boots, juice, carry weight and the pack all still count; the
/// controller's speed fields are never written. No skating indoors, on a puddle slide, as a cube, dragging a heavy bag,
/// riding a truck step or while flopped.</item>
/// <item><b>Level 2 - rocket boost</b> (<see cref="HaulerTuning.BoostKey"/>): a burst on a cooldown. Hit something fast and
/// square and you wipe out: the usual flop (<see cref="PlayerTells.SlipFlop"/>: drop what you hold, slide, ~1.3 s down)
/// and the backpack spills. Rule 12: cooldown 6 s; counter = steer; max helpless ~1.4 s; owner's machine detects, and it
/// only ever affects the skater.</item>
/// <item><b>Level 3 - afterburner and grinds.</b> Hold to keep boosting against a heat meter (full = flame-out); glancing
/// hits on walls, kerbs and fences become grinds that keep your speed and cool the rockets.</item>
/// <item><b>Everyone.</b> Skates on the boots, rockets at the heels, flames, trails and sparks on every copy: the level is the
/// replicated calling level, and the moments are relayed through <see cref="BagTellsNet.Send"/>. Movement itself is the
/// owner's (owner-authoritative transform), so there is no movement netcode here.</item>
/// </list>
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-90)]
public class RocketBlades : MonoBehaviour
{
    /// <summary>This machine's own player's blades (HUD only).</summary>
    public static RocketBlades Local { get; private set; }
    /// <summary>Stats (tests, bots).</summary>
    public static int Boosts, Wipeouts, Grinds;

    PlayerController pc;
    CharacterController cc;
    StarterAssets.ThirdPersonController tpc;
    StarterAssets.StarterAssetsInputs input;
    TrashCarrier carrier;
    PlayerTells tells;
    RideRider rider;
    CritterKnockback knock;

    int level;
    float nextLevelCheck;

    // ---- owner: momentum
    Vector3 beforeMove, vel;
    bool haveEarly, skating;
    float noSkateUntil, nextBumpFx, nextGrindFx, nextNope;
    HaulerRules.Boost boost;

    // ---- every copy: looks
    readonly List<GameObject> skates = new List<GameObject>(2);
    readonly List<GameObject> rockets = new List<GameObject>(2);
    readonly List<Transform> flames = new List<Transform>(2);
    readonly List<TrailRenderer> trails = new List<TrailRenderer>(2);
    bool built, buildFailed;
    int shownLevel = -1;
    float boostFxUntil, nextClack, nextPuff, grindFxUntil;
    Vector3 lastPos, seenVel;
    bool haveLastPos;

    public ulong ClientId => pc != null && pc.IsSpawned ? pc.OwnerClientId : 0;
    public bool IsLocal => pc != null && CoopPlayerHooks.IsLocalOwner(pc);
    public int Level => level;
    public bool Skating => skating;
    public bool Boosting => boost.active;
    /// <summary>Flat speed (m/s): the owner's own velocity while skating, else what this copy was seen to move.</summary>
    public float Speed => skating ? vel.magnitude : seenVel.magnitude;
    public float Heat => boost.heat;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        cc = GetComponent<CharacterController>();
        tpc = GetComponent<StarterAssets.ThirdPersonController>();
        input = GetComponent<StarterAssets.StarterAssetsInputs>();
        carrier = GetComponent<TrashCarrier>();
    }

    void OnEnable() => BagTellsNet.FxPlayed += OnFx;

    void OnDisable()
    {
        BagTellsNet.FxPlayed -= OnFx;
        if (Local == this) Local = null;
        skating = false;
        vel = Vector3.zero;
        boost = default;
        boostFxUntil = 0f;
        ShowFlames(false);
        foreach (var t in trails) if (t != null) t.emitting = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Local = null; Boosts = Wipeouts = Grinds = 0; }

    // ==================================================================================== level

    void RefreshLevel()
    {
        if (Time.unscaledTime < nextLevelCheck) return;
        nextLevelCheck = Time.unscaledTime + 0.5f;
        int now = IsLocal ? Mathf.Max(GameSession.GetUpgradeLevel(CallingIds.RocketBlades), Workshop.CallingLevelForClient(ClientId, CallingIds.RocketBlades))
                          : Workshop.CallingLevelForClient(ClientId, CallingIds.RocketBlades);
        level = Mathf.Clamp(now, 0, 3);
    }

    // ==================================================================================== owner: before the controller

    void Update()
    {
        RefreshLevel();
        if (!IsLocal) return;
        Local = this;
        if (tells == null) tells = GetComponent<PlayerTells>();
        if (rider == null) rider = GetComponent<RideRider>();
        if (knock == null) knock = GetComponent<CritterKnockback>();

        bool onFoot = pc != null && cc != null && cc.enabled && !pc.IsDriving && !pc.IsPlayingChess && !SceneController.IsTransitioning &&
                      !CompactorCube.IsSuctioning(ClientId);
        haveEarly = onFoot && level > 0;
        beforeMove = transform.position;

        bool allowed = haveEarly && CanRoll();
        bool keys = allowed && !PlayerController.IsUIBlocking;
        bool pressed = keys && GameControls.Down(HaulerTuning.BoostKey, GameControls.Action.Boost);
        bool held = keys && GameControls.Held(HaulerTuning.BoostKey, GameControls.Action.Boost);
        bool wasReady = boost.Ready(level);
        boost = HaulerRules.BoostTick(boost, level, pressed, held, allowed, Time.deltaTime);
        if (boost.started) OnBoostStart();
        if (boost.ended && !boost.overheated) OnBoostEnd();
        if (boost.overheated) FlameOut();
        if (pressed && !wasReady && !boost.started && level >= HaulerTuning.BoostLevel && Time.time >= nextNope)
        {
            nextNope = Time.time + 0.8f;
            string text = boost.lockLeft > 0f ? $"rockets cooling... {boost.lockLeft:0.0}s" : boost.cooldownLeft > 0f ? $"rockets recharging... {boost.cooldownLeft:0.0}s" : "TOO HOT!";
            GameHUD.FloatingText(transform.position + Vector3.up * 2f, text, new Color(1f, 0.7f, 0.4f), 0.8f);
            Sfx.Play("click", transform.position, 0.4f, 0.7f);
        }
        else if (level >= HaulerTuning.BoostLevel && haveEarly && !allowed && !PlayerController.IsUIBlocking &&
                 GameControls.Down(HaulerTuning.BoostKey, GameControls.Action.Boost) && Time.time >= nextNope && NightPlayers.IsIndoors(pc))
        {
            nextNope = Time.time + 1.2f;
            GameHUD.FloatingText(transform.position + Vector3.up * 2f, "no rockets indoors!", new Color(1f, 0.7f, 0.4f), 0.8f);
        }
    }

    /// <summary>May the wheels roll right now? (On foot is checked by the caller.)</summary>
    bool CanRoll()
    {
        if (level <= 0 || Time.time < noSkateUntil || NightPlayers.IsIndoors(pc)) return false;
        if (carrier != null && carrier.ExternalControl) return false;
        if (tells != null && (tells.IsCube || tells.Dragging || tells.LiftedTogether || tells.Helping || tells.Slip > 0.05f)) return false;
        if (rider != null && rider.IsRiding) return false;
        if (knock != null && knock.IsKnocking) return false;
        var hands = HandsNet.Instance;
        if (hands != null && hands.Active && hands.BodyOf(ClientId).State != BodyState.Normal) return false;
        return true;
    }

    // ==================================================================================== owner: after the controller

    void LateUpdate()
    {
        if (IsLocal) OwnerLate();
        else SeeMotion();
        UpdateLook();
    }

    void StopRolling()
    {
        skating = false;
        vel = Vector3.zero;
    }

    void OwnerLate()
    {
        float dt = Time.deltaTime;
        if (!haveEarly || cc == null || !cc.enabled || dt <= 0f) { StopRolling(); SeeMotion(); return; }
        haveEarly = false;
        Vector3 d = transform.position - beforeMove;
        d.y = 0f;
        if (d.sqrMagnitude > 1f) { StopRolling(); haveLastPos = false; return; } // a teleport (door, pop-out, tow)
        Vector3 v = d / dt; // what the controller (and knockbacks, puddles) moved us this frame
        seenVel = v;
        if (!CanRoll()) { StopRolling(); return; }

        bool hasInput = input != null && input.move.sqrMagnitude > 0.01f;
        bool sprint = input != null && input.sprint;
        float walkSpeed = tpc != null ? tpc.MoveSpeed : 3f;
        float sprintSpeed = tpc != null ? tpc.SprintSpeed : 5.3f;

        if (!skating)
        {
            bool push = hasInput && sprint && v.magnitude >= Mathf.Min(HaulerTuning.SkateStartSpeed, walkSpeed * 0.7f);
            if (!push && !boost.active) return;
            skating = true;
            vel = v;
        }

        Vector3 facing = transform.forward; facing.y = 0f;
        Vector3 wish = hasInput ? (v.sqrMagnitude > 0.09f ? v.normalized : facing.normalized) : boost.active ? facing.normalized : Vector3.zero;
        var mode = boost.active ? HaulerRules.SkateMode.Power : !hasInput ? HaulerRules.SkateMode.Glide
                 : sprint ? HaulerRules.SkateMode.Power : HaulerRules.SkateMode.Brake;
        float top = HaulerRules.SkateTop(sprintSpeed, boost.active);
        vel = HaulerRules.SkateStep(vel, wish, top, mode, boost.active, dt);
        float speed = vel.magnitude;

        // Step off the wheels: the toe stop got you down to a walk, or the glide ran out.
        if (!boost.active && (mode == HaulerRules.SkateMode.Brake && speed <= walkSpeed || mode == HaulerRules.SkateMode.Glide && speed < HaulerTuning.SkateStopSpeed))
        { StopRolling(); return; }

        Vector3 desired = vel * dt;
        Vector3 move = desired - d;
        move.y = 0f;
        if (move.sqrMagnitude > 1e-8f) cc.Move(move);
        Vector3 actual = transform.position - beforeMove;
        actual.y = 0f;
        seenVel = actual / dt;

        float headOn = HaulerRules.HeadOn(desired, actual);
        switch (HaulerRules.ImpactOf(speed, headOn, level))
        {
            case HaulerRules.Impact.Wipeout:
                Wipeout(desired.normalized, "SPLAT!");
                break;
            case HaulerRules.Impact.Grind:
                // Keep the speed and follow whatever we are scraping along.
                vel = (actual.sqrMagnitude > 1e-8f ? actual.normalized : vel.normalized) * speed;
                boost.heat = Mathf.Max(0f, boost.heat - HaulerTuning.GrindCoolPerSecond * dt);
                if (Time.time >= nextGrindFx)
                {
                    nextGrindFx = Time.time + 0.25f;
                    Grinds++;
                    BagTellsNet.Send(HaulerTuning.FxGrind, HandsNet.None, transform.position, vel);
                }
                break;
            case HaulerRules.Impact.Bump:
                vel = actual / dt; // the wall took its share
                if (headOn > 0.5f && speed > 3.5f && Time.time >= nextBumpFx)
                {
                    nextBumpFx = Time.time + 0.6f;
                    Sfx.Play("thud", transform.position, 0.5f, 1.3f);
                    Fx.Dust(transform.position + Vector3.up * 0.1f, 0.6f);
                    GameHUD.FloatingText(transform.position + Vector3.up * 1.9f, "oof", new Color(1f, 0.85f, 0.6f), 0.7f);
                }
                break;
        }
    }

    // ==================================================================================== owner: rockets

    void OnBoostStart()
    {
        Boosts++;
        Vector3 facing = transform.forward; facing.y = 0f;
        Vector3 dir = vel.sqrMagnitude > 1f ? vel.normalized : facing.normalized;
        if (!skating) { skating = true; vel = seenVel; }
        vel += dir * HaulerTuning.BoostKick;
        // How long everyone else should draw the flames for (level 3 ends it explicitly; this is the safety net).
        float seconds = level >= HaulerTuning.AfterburnerLevel ? 1f / HaulerTuning.HeatPerSecond + 0.5f : HaulerTuning.BoostSeconds;
        BagTellsNet.Send(HaulerTuning.FxBoost, HandsNet.None, transform.position, new Vector3(seconds, level, 0f));
        Juice.Shake(0.14f, 0.18f);
        GameHUD.FloatingText(transform.position + Vector3.up * 2.1f, ShopModKit.Pick(BoostWords), new Color(1f, 0.6f, 0.15f), 1.2f);
    }

    void OnBoostEnd() => BagTellsNet.Send(HaulerTuning.FxBoostEnd, HandsNet.None, transform.position, Vector3.zero);

    void FlameOut()
    {
        BagTellsNet.Send(HaulerTuning.FxBoostEnd, HandsNet.None, transform.position, Vector3.zero);
        Vector3 facing = transform.forward; facing.y = 0f;
        Wipeout(vel.sqrMagnitude > 0.5f ? vel.normalized : facing.normalized, "FLAME OUT!");
    }

    static readonly string[] BoostWords = { "FWOOOSH!", "ROCKETS!", "WHEEEE!", "KA-FWOOM!" };

    /// <summary>Owner: the silly ending. The existing slip-flop (drop, slide, down ~1.3 s), back the way you came; the pack
    /// spills; the rockets need a moment.</summary>
    void Wipeout(Vector3 into, string word)
    {
        Wipeouts++;
        float speed = vel.magnitude;
        StopRolling();
        noSkateUntil = Time.time + 1.8f;
        boost.active = false;
        boost.burstLeft = 0f;
        boost.cooldownLeft = Mathf.Max(boost.cooldownLeft, 2f);
        Vector3 at = transform.position;
        BagTellsNet.Send(HaulerTuning.FxWipeout, HandsNet.None, at, into);
        HandsNet.Instance?.LocalPackSpill(true);
        if (tells == null) tells = GetComponent<PlayerTells>();
        if (tells != null) tells.SlipFlop(-into * Mathf.Max(3f, speed * 0.4f));
        Juice.Shake(0.4f, 0.3f);
        GameHUD.FloatingText(at + Vector3.up * 2.4f, word, new Color(1f, 0.45f, 0.2f), 1.6f);
        NetAudit.Event("hauler", new Dictionary<string, object> { { "op", "wipeout" }, { "client", (long)ClientId }, { "speed", Mathf.Round(speed * 10f) / 10f } });
    }

    // ==================================================================================== everyone

    /// <summary>A copy we don't simulate (or an owner not rolling): how fast it was seen to move.</summary>
    void SeeMotion()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        Vector3 p = transform.position;
        if (haveLastPos)
        {
            Vector3 d = p - lastPos; d.y = 0f;
            Vector3 v = d.sqrMagnitude > 4f ? Vector3.zero : d / dt;
            seenVel = Vector3.Lerp(seenVel, v, 1f - Mathf.Exp(-10f * dt));
        }
        lastPos = p;
        haveLastPos = true;
    }

    void OnFx(byte kind, ulong who, ulong other, Vector3 a, Vector3 b)
    {
        if (pc == null || who != ClientId) return;
        switch (kind)
        {
            case HaulerTuning.FxBoost:
                boostFxUntil = Time.time + Mathf.Clamp(b.x, 0.2f, 6f);
                Sfx.Play("whoosh", a, 0.9f, 0.7f);
                Sfx.Play("pop", a, 0.7f, 0.6f);
                Fx.Burst(a + Vector3.up * 0.15f, new Color(1f, 0.55f, 0.1f), 18, 4f);
                Fx.Dust(a, 1.2f);
                break;
            case HaulerTuning.FxBoostEnd:
                boostFxUntil = 0f;
                Sfx.Play("pop", a, 0.35f, 1.4f);
                break;
            case HaulerTuning.FxWipeout:
                boostFxUntil = 0f;
                Sfx.Play("slam", a, 0.9f, 1.1f);
                Sfx.Play("squeak", a, 0.8f, 0.6f);
                Fx.Dust(a + Vector3.up * 0.2f, 1.8f);
                Fx.Burst(a + Vector3.up * 0.8f, new Color(1f, 0.9f, 0.35f), 16, 4f);
                if (!IsLocal) GameHUD.FloatingText(a + Vector3.up * 2.2f, "SPLAT!", new Color(1f, 0.45f, 0.2f), 1.3f);
                break;
            case HaulerTuning.FxGrind:
                grindFxUntil = Time.time + 0.3f;
                Sfx.Play("squeak", a, 0.35f, 1.9f);
                Fx.Burst(a + Vector3.up * 0.08f, new Color(1f, 0.85f, 0.4f), 8, 3.5f);
                if (IsLocal) GameHUD.FloatingText(a + Vector3.up * 1.9f, "GRIIIND", new Color(1f, 0.85f, 0.4f), 0.8f);
                break;
        }
    }

    void UpdateLook()
    {
        if (level != shownLevel)
        {
            if (level > 0 && !built && !buildFailed) BuildLook();
            if (built)
            {
                foreach (var s in skates) if (s != null) s.SetActive(level > 0);
                foreach (var r in rockets) if (r != null) r.SetActive(level >= HaulerTuning.BoostLevel);
                if (shownLevel >= 0 && level > shownLevel && IsLocal)
                {
                    Vector3 feet = transform.position + Vector3.up * 0.1f;
                    Fx.Burst(feet, new Color(1f, 0.55f, 0.1f), 16, 3f);
                    Sfx.Play("click", feet, 0.8f, 1.2f);
                    GameHUD.FloatingText(feet + Vector3.up * 2f, level >= 3 ? "AFTERBURNERS!" : level == 2 ? "ROCKETS FITTED! (Q)" : "WHEELS! (sprint to skate)", new Color(1f, 0.7f, 0.25f), 1.1f);
                }
            }
            shownLevel = level;
        }
        if (!built || level <= 0) return;

        bool hidden = pc != null && (pc.IsDriving || pc.IsPlayingChess);
        float speed = Speed;
        bool rolling = !hidden && (IsLocal ? skating : speed > 3.6f);
        bool flame = !hidden && level >= HaulerTuning.BoostLevel && (IsLocal ? boost.active : Time.time < boostFxUntil);
        ShowFlames(flame);
        foreach (var t in trails) if (t != null) t.emitting = rolling && speed > 4.5f || flame;

        Vector3 feet2 = transform.position;
        if (rolling && speed > 2f && Time.time >= nextClack)
        {
            nextClack = Time.time + Mathf.Clamp(1.1f / speed, 0.09f, 0.5f);
            Sfx.Play("click", feet2, IsLocal ? 0.1f : 0.07f, 0.7f + speed * 0.05f);
        }
        if (flame)
        {
            float flick = 0.8f + 0.35f * Mathf.Sin(Time.time * 47f) + 0.15f * Mathf.Sin(Time.time * 91f);
            foreach (var f in flames) if (f != null) f.localScale = new Vector3(0.07f, 0.07f, 0.24f * flick);
            if (Time.time >= nextPuff)
            {
                nextPuff = Time.time + 0.09f;
                Fx.Burst(feet2 + Vector3.up * 0.12f - transform.forward * 0.3f, new Color(1f, 0.5f + 0.3f * ShopModKit.Rand01(), 0.1f), 3, 2f);
            }
        }
        if (Time.time < grindFxUntil && Time.time >= nextPuff)
        {
            nextPuff = Time.time + 0.06f;
            Fx.Burst(feet2 + Vector3.up * 0.05f, new Color(1f, 0.9f, 0.5f), 2, 3f);
        }
    }

    void ShowFlames(bool on)
    {
        foreach (var f in flames) if (f != null && f.gameObject.activeSelf != on) f.gameObject.SetActive(on);
    }

    // ==================================================================================== build (Docs/Character.md: parts hang under
    // the boot soles, are shown with SetActive only, and no bone is ever written)

    void BuildLook()
    {
        var left = FindDeep(transform, "Boot_Sole_L");
        var right = FindDeep(transform, "Boot_Sole_R");
        if (left == null || right == null) { buildFailed = true; return; }
        built = true;
        BuildSkate(left);
        BuildSkate(right);
    }

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    void BuildSkate(Transform sole)
    {
        // The sole is a scaled box in the foot's frame (x across, y up, z toe): undo its scale so the parts are in rig metres.
        Vector3 s = sole.localScale;
        var root = ShopModKit.Pivot("Rocket Blade", sole, Vector3.zero);
        root.localScale = new Vector3(Inv(s.x), Inv(s.y), Inv(s.z));
        var steel = ShopModKit.Lit("mat/hauler_blade_steel", new Color(0.78f, 0.8f, 0.85f), 0.7f, 0.7f);
        var wheel = ShopModKit.Lit("mat/hauler_blade_wheel", new Color(1f, 0.5f, 0.08f), 0.5f);
        var red = ShopModKit.Lit("mat/hauler_blade_rocket", new Color(0.85f, 0.14f, 0.12f), 0.55f, 0.3f);
        var fire = ShopModKit.Emissive("mat/hauler_blade_flame", new Color(1f, 0.55f, 0.1f), 3f);

        ShopModKit.Prim(PrimitiveType.Cube, "Truck", root, steel, new Vector3(0f, -0.005f, 0f), new Vector3(0.25f, 0.025f, 0.3f));
        foreach (float z in new[] { -0.09f, 0.1f })
        {
            ShopModKit.Prim(PrimitiveType.Cylinder, "Axle", root, steel, new Vector3(0f, 0f, z), new Vector3(0.022f, 0.15f, 0.022f), new Vector3(0f, 0f, 90f));
            foreach (float x in new[] { -0.145f, 0.145f })
                ShopModKit.Prim(PrimitiveType.Cylinder, "Wheel", root, wheel, new Vector3(x, 0f, z), new Vector3(0.095f, 0.017f, 0.095f), new Vector3(0f, 0f, 90f));
        }
        skates.Add(root.gameObject);

        var rocket = ShopModKit.Pivot("Heel Rocket", root, new Vector3(0f, 0.085f, -0.2f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Tube", rocket, red, Vector3.zero, new Vector3(0.075f, 0.075f, 0.075f), new Vector3(90f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Nose", rocket, steel, new Vector3(0f, 0f, 0.085f), new Vector3(0.075f, 0.075f, 0.09f));
        ShopModKit.Prim(PrimitiveType.Cube, "Fin", rocket, steel, new Vector3(0f, 0.045f, -0.05f), new Vector3(0.012f, 0.06f, 0.07f));
        var flame = ShopModKit.Prim(PrimitiveType.Sphere, "Flame", rocket, fire, new Vector3(0f, 0f, -0.17f), new Vector3(0.07f, 0.07f, 0.24f));
        flame.gameObject.SetActive(false);
        flames.Add(flame);
        rockets.Add(rocket.gameObject);

        var trailGo = new GameObject("Blade Trail");
        trailGo.transform.SetParent(root, false);
        trailGo.transform.localPosition = new Vector3(0f, -0.02f, -0.1f);
        var trail = trailGo.AddComponent<TrailRenderer>();
        trail.sharedMaterial = SortingKit.RuntimeMaterial("mat/hauler_blade_trail", new Color(1f, 0.75f, 0.3f), true);
        trail.time = 0.32f;
        trail.widthMultiplier = 0.05f;
        trail.minVertexDistance = 0.08f;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.receiveShadows = false;
        trail.emitting = false;
        trails.Add(trail);
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            if (!(r is TrailRenderer)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static float Inv(float v) => Mathf.Abs(v) > 1e-4f ? 1f / v : 1f;

    // ==================================================================================== HUD (the local hotbar asks)

    /// <summary>The local player's boost line for the pocket bar ("READY", "3.2s", "HEAT 60%"); false = no rockets fitted.</summary>
    public static bool Hotbar(out string detail, out bool ready)
    {
        detail = ""; ready = false;
        var b = Local;
        if (b == null || b.level < HaulerTuning.BoostLevel) return false;
        ready = b.boost.Ready(b.level) || b.boost.active;
        if (b.boost.lockLeft > 0f) detail = $"COOLING {b.boost.lockLeft:0.0}s";
        else if (b.level >= HaulerTuning.AfterburnerLevel) detail = b.boost.heat > 0.02f ? $"HEAT {Mathf.RoundToInt(b.boost.heat * 100f)}%" : "READY";
        else detail = b.boost.active ? "FWOOSH" : b.boost.cooldownLeft > 0f ? $"{b.boost.cooldownLeft:0.0}s" : "READY";
        return true;
    }
}

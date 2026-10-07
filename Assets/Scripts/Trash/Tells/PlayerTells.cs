using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// F3 on every player copy (added at runtime by <see cref="TellsRuntime"/>; Docs/Multiplayer.md §14.3).
/// <list type="bullet">
/// <item><b>Owner — feet:</b> a locomotion filter that runs before the third-person controller: slipping on juice puddles (your
/// momentum carries, steering goes mushy; hit one at a run and there's a 45% chance you flop: the bag drops, you slide, you get
/// back up after ~1.3 s), and speed scaling (dragging a heavy bag alone, waddling as a compactor cube). It corrects the next frame
/// with <c>CharacterController.Move</c> (walls still block) and never touches the controller's speed fields.</item>
/// <item><b>Owner — heavy bags:</b> alone you drag it at half speed and can't throw it (it plops). Another player can take
/// the other end via the shared-grip rules; both steer independently at 85% speed. Moving together, including sprinting,
/// is stable. Opposing pulls use the same takeover/tear rules as any other bag.</item>
/// <item><b>Everyone — cube:</b> while <see cref="CompactorCube.IsCubed"/>, the body is hidden (<c>forceRenderingOff</c>, which nothing
/// else touches) and a cardboard cube with googly eyes waddles in its place.</item>
/// </list>
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
public class PlayerTells : MonoBehaviour
{
    static readonly List<PlayerTells> all = new List<PlayerTells>();
    public static IReadOnlyList<PlayerTells> All => all;
    /// <summary>This machine's own player's copy (null until it spawns).</summary>
    public static PlayerTells LocalTells { get; private set; }

    /// <summary>Stats (bots, tests).</summary>
    public static int Slips, Flops, OutOfSteps;

    // ---- tuning
    public const float DragSpeed = 0.5f, CubeSpeed = 0.55f;
    /// <summary>Lifting together you walk at this fraction of your unloaded speed (the two of you cancel the bag's weight).</summary>
    public const float LiftSpeed = 0.85f;
    public const float FlopMinSpeed = 3.6f, FlopChancePct = 45f;

    public PlayerController Player { get; private set; }
    CharacterController cc;
    TrashCarrier carrier;
    Transform skeleton, dragPoint;
    Quaternion skeletonRot;
    Vector3 skeletonPos;

    public ulong ClientId => Player != null && Player.IsSpawned ? Player.OwnerClientId : 0;
    public bool IsLocal => Player != null && CoopPlayerHooks.IsLocalOwner(Player);

    /// <summary>0..1 how slippery it is underfoot (owner only).</summary>
    public float Slip { get; private set; }
    /// <summary>Holding a heavy bag alone (owner only).</summary>
    public bool Dragging { get; private set; }
    /// <summary>Holding a heavy bag with a helper on the other end (owner only).</summary>
    public bool LiftedTogether { get; private set; }
    /// <summary>Carrying the other end of somebody's heavy bag (owner only).</summary>
    public bool Helping { get; private set; }
    public bool IsCube => CompactorCube.IsCubed(ClientId);

    // ---- feet
    Vector3 beforeMove, lastVel, slideVel;
    Vector2 savedMove, scaledMove;
    bool savedAnalog, inputScaled;
    bool haveEarly, slideOn, wasOnPuddle;
    StarterAssets.StarterAssetsInputs input;
    uint lastPuddle;
    int puddleEntries;
    float flopCooldownUntil, soloFlopUntil = -1f, soloLie, coopGetUpAt = -1f;
    bool soloLieApplied;

    // ---- heavy
    bool myOverride;
    PlayerTells ghostPartner;
    string hint;

    // ---- cube
    Transform cube;
    bool cubeShown;
    readonly List<Renderer> hidden = new List<Renderer>();
    float nextHideRefresh, cubeShownAt;

    static readonly string[] WhoaWords = { "WHOA-OA!", "WHOOPS!", "SPLOOSH!", "yikes!", "*squelch*" };
    static readonly Color JuiceColor = new Color(0.42f, 0.46f, 0.14f);

    // ==================================================================================== lifecycle

    void Awake()
    {
        Player = GetComponent<PlayerController>();
        cc = GetComponent<CharacterController>();
        carrier = GetComponent<TrashCarrier>();
        input = GetComponent<StarterAssets.StarterAssetsInputs>();
        skeleton = transform.Find("Skeleton");
        if (skeleton != null) { skeletonRot = skeleton.localRotation; skeletonPos = skeleton.localPosition; }
        var dp = new GameObject("F3DragPoint").transform;
        dp.SetParent(transform, false);
        dp.localPosition = new Vector3(0.1f, 0.42f, 0.8f);
        dragPoint = dp;
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }

    void OnDisable()
    {
        all.Remove(this);
        if (LocalTells == this) LocalTells = null;
        if (inputScaled && input != null) { if (input.move == scaledMove) input.move = savedMove; input.analogMovement = savedAnalog; inputScaled = false; }
        ShowCube(false);
        ClearDragOverride();
        SetHint(null);
        ApplySoloLie(0f);
        SetGhost(ghostPartner, false);
        ghostPartner = null;
    }

    public static PlayerTells For(ulong client)
    {
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && p.Player != null && p.ClientId == client) return p;
        }
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        LocalTells = null;
        Slips = Flops = OutOfSteps = 0;
    }

    static BagTellsNet Net => BagTellsNet.Instance != null && BagTellsNet.Instance.IsSpawned ? BagTellsNet.Instance : null;
    static HandsNet Hands => HandsNet.Instance != null && HandsNet.Instance.IsSpawned && HandsNet.Instance.Active ? HandsNet.Instance : null;

    // ==================================================================================== per frame

    void Update()
    {
        if (!IsLocal) { RemoteDragPose(); return; }
        LocalTells = this;
        OwnerEarly();
    }

    /// <summary>Someone else's copy: show their heavy bag dragging on the floor too (everyone poses held things locally).</summary>
    void RemoteDragPose()
    {
        var held = carrier != null ? carrier.Held : null;
        bool drag = false;
        if (held != null && BagTells.Of(held) == BagTell.Heavy)
        {
            var net = Net;
            drag = !(held.NetId != 0 && net != null && net.TryLift(held.NetId, out var l) && l.holder == ClientId && l.state == LiftNet.Lifting);
        }
        ApplyDragOverride(drag);
    }

    void ApplyDragOverride(bool drag)
    {
        if (drag && carrier != null && !carrier.ExternalControl && (!carrier.HasHoldOverride || myOverride))
        {
            if (!myOverride) carrier.SetHoldOverride(dragPoint, null, 1.3f);
            myOverride = true;
        }
        else ClearDragOverride();
    }

    void LateUpdate()
    {
        if (IsLocal) OwnerLate();
        UpdateCubeLook();
    }

    void OwnerEarly()
    {
        if (CompactorCube.IsSuctioning(ClientId)) { haveEarly = false; return; }
        bool onFoot = Player != null && cc != null && cc.enabled && !Player.IsDriving && !Player.IsPlayingChess && !SceneController.IsTransitioning;
        UpdateHeavy(onFoot);
        if (coopGetUpAt > 0f && Time.time >= coopGetUpAt)
        {
            coopGetUpAt = -1f;
            var h = Hands;
            var pb = h != null ? PlayerBody.For(h.LocalId) : null;
            if (pb != null && pb.State == BodyState.Flopped) h.LocalFlop(false);
        }
        inputScaled = false;
        if (!onFoot)
        {
            haveEarly = false;
            slideOn = false;
            slideVel = Vector3.zero;
            Slip = 0f;
            return;
        }
        // Speed: scale the stick before the third-person controller reads it (it then walks, animates and reads its own
        // velocity consistently); restored in LateUpdate so the input system's next value is untouched.
        float scale = SpeedScale();
        if (input != null && Mathf.Abs(scale - 1f) > 0.001f)
        {
            savedMove = input.move;
            savedAnalog = input.analogMovement;
            float mag = Mathf.Min(1f, input.move.magnitude);
            if (mag > 0.001f) input.move = input.move / input.move.magnitude * (mag * scale);
            if (scale <= 0.001f) input.move = Vector2.zero;
            input.analogMovement = true;
            inputScaled = true;
            scaledMove = input.move;
        }
        beforeMove = transform.position;
        haveEarly = true;
    }

    void OwnerLate()
    {
        if (inputScaled && input != null)
        {
            // Put the stick back (unless someone wrote a new value meanwhile: a bot, a UI lock).
            if (input.move == scaledMove) input.move = savedMove;
            input.analogMovement = savedAnalog;
            inputScaled = false;
        }
        // Solo slip flop: lie flat for a moment (co-op uses N2's flop state, which every machine draws).
        float want = Time.time < soloFlopUntil ? 1f : 0f;
        soloLie = Mathf.MoveTowards(soloLie, want, Time.deltaTime * (want > 0f ? 7f : 4f));
        ApplySoloLie(soloLie);
        if (!haveEarly || cc == null || !cc.enabled) return;
        haveEarly = false;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        // What the controller (and knockbacks) moved us this frame.
        Vector3 d = transform.position - beforeMove;
        d.y = 0f;
        if (d.sqrMagnitude > 1f) { slideOn = false; slideVel = Vector3.zero; lastVel = Vector3.zero; return; } // a teleport (or a pop-out)
        Vector3 v = d / dt;

        // ---- slip: momentum carries, steering goes mushy
        uint pid = 0;
        float k = 0f;
        if (cc.isGrounded) k = Puddles.SlipAt(transform.position, out pid);
        Slip = k;
        Vector3 desired = d;
        if (k > 0.05f)
        {
            if (!slideOn) { slideOn = true; slideVel = lastVel; }
            float grip = Mathf.Lerp(14f, 1.3f, k);
            slideVel = Vector3.Lerp(slideVel, v, 1f - Mathf.Exp(-grip * dt));
            desired = slideVel * dt;
        }
        else slideOn = false;
        bool onPuddle = pid != 0 && k > 0.5f;
        if (onPuddle && (!wasOnPuddle || pid != lastPuddle)) OnEnterPuddle(pid, slideOn ? slideVel : v);
        wasOnPuddle = onPuddle;
        lastPuddle = pid;

        Vector3 move = desired - d;
        move.y = 0f;
        if (move.sqrMagnitude > 1e-8f) cc.Move(move);
        Vector3 total = transform.position - beforeMove; total.y = 0f;
        lastVel = total / dt;
    }

    float SpeedScale()
    {
        if (Time.time < soloFlopUntil) return 0f;
        float s = 1f;
        if (Dragging) s *= DragSpeed;
        else if (LiftedTogether)
        {
            // Two of you: cancel the carry slowdown TrashCarrier applied for the bag's weight (and a bit more).
            var held = carrier != null ? carrier.Held : null;
            float f = held != null ? 1f - Mathf.Clamp(held.MassKg / Mathf.Max(1f, carrier.slowdownMassKg), 0f, carrier.maxSlowdown) : 1f;
            s *= Mathf.Min(1.8f, LiftSpeed / Mathf.Max(0.3f, f));
        }
        if (IsCube) s *= CubeSpeed;
        return s;
    }

    // ==================================================================================== slipping

    void OnEnterPuddle(uint pid, Vector3 v)
    {
        // Weather puddles can affect steering, but only a bag leak/splat can
        // knock a player down and force them to drop what they are carrying.
        if (Puddles.IsRain(pid)) return;
        Slips++;
        Vector3 feet = transform.position + Vector3.up * 0.05f;
        float speed = new Vector2(v.x, v.z).magnitude;
        Fx.Burst(feet, JuiceColor, speed > 2.5f ? 10 : 5, 2.2f);
        Sfx.Play("splat", feet, Mathf.Lerp(0.15f, 0.45f, speed / 5f), ShopModKit.Range(1.3f, 1.6f));
        if (speed < FlopMinSpeed || Time.time < flopCooldownUntil || IsCube) return;
        puddleEntries++;
        uint h = (uint)PickupVisuals.StableHash(pid + "/" + ClientId + "/" + puddleEntries);
        if (h % 100u >= (uint)FlopChancePct)
        {
            GameHUD.FloatingText(transform.position + Vector3.up * 2f, "whoa!", new Color(0.8f, 0.95f, 0.4f), 0.8f);
            return;
        }
        SlipFlop(v);
    }

    /// <summary>Owner: slip and flop (puddle at a run, or a bot test). Drops what you hold, slides you along, back up in ~1.3 s.</summary>
    public void SlipFlop(Vector3 v)
    {
        if (!IsLocal) return;
        flopCooldownUntil = Time.time + 5f;
        Flops++;
        Vector3 dir = new Vector3(v.x, 0f, v.z);
        if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
        dir.Normalize();
        if (carrier != null && carrier.IsCarrying && !carrier.ExternalControl)
        {
            var held = carrier.Held;
            carrier.Drop();
            if (held != null && held.Body != null && !held.Body.isKinematic) held.Body.linearVelocity += dir * 2.5f + Vector3.up * 2.5f;
        }
        var kb = GetComponent<CritterKnockback>() ?? gameObject.AddComponent<CritterKnockback>();
        kb.Knock(dir, 1.7f, 0.35f, 0.5f);
        var h = Hands;
        if (h != null)
        {
            h.LocalFlop(true);
            coopGetUpAt = Time.time + 1.4f;
        }
        else
        {
            soloFlopUntil = Time.time + 1.3f;
            DizzyStars.Attach(transform, 1.3f, 0.35f, 2.15f);
        }
        Vector3 head = transform.position + Vector3.up * 1.8f;
        GameHUD.FloatingText(head, ShopModKit.Pick(WhoaWords), new Color(0.75f, 0.95f, 0.35f), 1.4f);
        Sfx.Play("squeak", transform.position, 0.9f, 0.7f);
        Sfx.Play("thud", transform.position, 0.6f, 1.1f);
        Juice.Shake(0.18f, 0.2f);
        Net?.Fx(BagTellsNet.FxSlip, ClientId, transform.position, dir);
        NetAudit.Event("tells", new Dictionary<string, object> { { "op", "slip-flop" }, { "client", (long)ClientId } });
    }

    void ApplySoloLie(float amount)
    {
        if (skeleton == null) return;
        if (amount <= 0.001f)
        {
            if (!soloLieApplied) return;
            soloLieApplied = false;
            skeleton.localRotation = skeletonRot;
            skeleton.localPosition = skeletonPos;
            return;
        }
        soloLieApplied = true;
        skeleton.localRotation = Quaternion.Euler(-82f * amount, 0f, 0f) * skeletonRot;
        skeleton.localPosition = skeletonPos + Vector3.up * (0.22f * amount);
    }

    // ==================================================================================== heavy bags

    void UpdateHeavy(bool onFoot)
    {
        var held = carrier != null ? carrier.Held : null;
        var shared = Hands != null ? Hands.TugOf(ClientId) : null;
        var nc = shared.HasValue ? Hands.Get(shared.Value.item) : null;
        bool sharedHeavy = nc != null && nc.Item != null && BagTells.Of(nc.Item) == BagTell.Heavy;
        LiftedTogether = onFoot && sharedHeavy;
        Helping = LiftedTogether && shared.Value.challenger == ClientId;
        Dragging = onFoot && held != null && BagTells.Of(held) == BagTell.Heavy && !LiftedTogether;
        ApplyDragOverride(Dragging);

        // Both people steer their own feet. No helper auto-follow, sprint punishment or out-of-step drop.
        PlayerTells partner = shared.HasValue ? For(shared.Value.holder == ClientId ? shared.Value.challenger : shared.Value.holder) : null;
        if (partner != ghostPartner)
        {
            SetGhost(ghostPartner, false);
            ghostPartner = partner;
            SetGhost(ghostPartner, true);
        }
        SetHint(null); // shared-grip input owns the ordinary G-to-release hint
    }

    void SetGhost(PlayerTells other, bool ignore)
    {
        if (other == null || other.cc == null || cc == null || other == this) return;
        Physics.IgnoreCollision(cc, other.cc, ignore);
    }

    void ClearDragOverride()
    {
        if (!myOverride) return;
        myOverride = false;
        if (carrier != null && carrier.HasHoldOverride) carrier.ClearHoldOverride();
    }

    void SetHint(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (hint == null) return;
            hint = null;
            GameHUD.SetContextHint(null);
            return;
        }
        if (text == hint) return;
        hint = text;
        GameHUD.SetContextHint(text);
    }

    // ==================================================================================== cube

    /// <summary>Only the owner releases their cameras/UI/seat and held prop before a harmless forced machine ride.</summary>
    internal void LeaveActivitiesForCompactor()
    {
        if (!IsLocal || Player == null) return;
        GetComponent<WorkshopLounge>()?.Stand();
        SortStation.Current?.Exit(); // coop:allow(current) release only this owner's local station camera/input
        EbaeUI.Instance?.Hide();
        UiStack.PopAll();
        if (Player.currentDepotChessBoard != null) Player.currentDepotChessBoard.OnInteract(gameObject);
        if (Player.currentChessController != null) Player.currentChessController.OnInteract(gameObject);
        if (Player.currentTruck != null) Player.currentTruck.ForceExit();
        GetComponent<RideRider>()?.ResetForCompactor();
        GetComponent<PlayerBody>()?.ResetForCompactor();
        if (carrier != null)
        {
            carrier.SetExternalControl(false);
            carrier.ClearHoldOverride();
            if (carrier.IsCarrying) carrier.Drop();
        }
        ClearDragOverride();
        soloFlopUntil = -1f;
        coopGetUpAt = -1f;
        soloLie = 0f;
        ApplySoloLie(0f);
        haveEarly = false;
        slideVel = lastVel = Vector3.zero;
    }

    /// <summary>Owner: pop out of the compactor as a cube (teleport onto the floor outside: never trapped).</summary>
    internal void PopOutAsCube(Vector3 at)
    {
        if (!IsLocal) return;
        LeaveActivitiesForCompactor();
        PlaceOutsideCompactor(at);
        Juice.Shake(0.35f, 0.3f);
        GameHUD.FloatingText(transform.position + Vector3.up * 1.6f, "*squish* I'm a CUBE", new Color(1f, 0.6f, 0.9f), 1.3f);
        GameHUD.Toast($"You're a box for {CompactorCube.Seconds:0}s. Waddle with pride!", ToastStyle.Rare, 3f);
    }

    internal void PlaceOutsideCompactor(Vector3 at)
    {
        if (!IsLocal) return;
        Vector3 to = SafeCompactorLanding(at);
        bool was = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        transform.position = to;
        if (cc != null) cc.enabled = was;
        haveEarly = false;
        slideVel = Vector3.zero;
        lastVel = Vector3.zero;
    }

    Vector3 SafeCompactorLanding(Vector3 at)
    {
        int lot = DepotRowLayout.InteriorAt(at);
        Vector3 fallback = at + Vector3.up * 0.05f;
        for (int i = 0; i < 17; i++)
        {
            float angle = (i - 1) * Mathf.PI * 0.25f;
            float radius = i == 0 ? 0f : i <= 8 ? 0.8f : 1.6f;
            Vector3 test = at + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            if (lot >= 0 && DepotRowLayout.InteriorAt(test) != lot) continue;
            var hits = Physics.RaycastAll(test + Vector3.up * 2f, Vector3.down, 5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.normal.y < 0.7f || hit.collider.GetComponentInParent<PlayerController>() != null ||
                    hit.collider.GetComponentInParent<Carryable>() != null || hit.collider.GetComponentInParent<TruckControllerV2>() != null) continue;
                Vector3 p = hit.point + Vector3.up * 0.05f;
                if (i == 0) fallback = p;
                bool blocked = false;
                foreach (var col in Physics.OverlapCapsule(p + Vector3.up * 0.35f, p + Vector3.up * 1.25f,
                    0.27f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (col.transform.IsChildOf(transform) || col.GetComponentInParent<Carryable>() != null) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) return p;
            }
        }
        return fallback;
    }

    void UpdateCubeLook()
    {
        bool want = IsCube;
        if (want != cubeShown) ShowCube(want);
        if (!cubeShown || cube == null) return;
        if (Time.time >= nextHideRefresh) { nextHideRefresh = Time.time + 0.5f; HideBody(true); }
        // Waddle: rock side to side faster when moving, and a little hop on every step.
        float t = Time.time - cubeShownAt;
        Vector3 v = IsLocal ? lastVel : Vector3.zero;
        float moving = IsLocal ? Mathf.Clamp01(new Vector2(v.x, v.z).magnitude / 1.5f) : 0.6f;
        float rate = Mathf.Lerp(4f, 11f, moving);
        float rock = Mathf.Sin(t * rate) * Mathf.Lerp(5f, 16f, moving);
        float bob = Mathf.Abs(Mathf.Sin(t * rate)) * 0.12f * moving;
        float pop = Mathf.Clamp01(t / 0.25f);
        float squish = 1f + Mathf.Sin(t * 22f) * 0.25f * (1f - pop);
        cube.localRotation = Quaternion.Euler(0f, 0f, rock);
        cube.localPosition = new Vector3(0f, bob, 0f);
        cube.localScale = new Vector3(squish, 1f / squish, squish) * Mathf.Lerp(0.2f, 1f, pop);
        // Last 1.5 s: shake about, about to pop back.
        float left = CompactorCube.Remaining(ClientId);
        if (left < 1.5f) cube.localRotation *= Quaternion.Euler(Mathf.Sin(t * 50f) * 6f, 0f, Mathf.Cos(t * 47f) * 6f);
    }

    void ShowCube(bool on)
    {
        if (on == cubeShown) return;
        cubeShown = on;
        if (on)
        {
            if (cube == null) BuildCube();
            cube.gameObject.SetActive(true);
            cubeShownAt = Time.time;
            HideBody(true);
            Vector3 p = transform.position + Vector3.up * 0.6f;
            Fx.Burst(p, new Color(0.85f, 0.7f, 0.45f), 18, 3.5f);
            Sfx.Play("pop", p, 0.9f, 0.7f);
        }
        else
        {
            if (cube != null) cube.gameObject.SetActive(false);
            HideBody(false);
            Vector3 p = transform.position + Vector3.up * 0.9f;
            Fx.Burst(p, new Color(1f, 0.75f, 0.9f), 22, 4f);
            Sfx.Play("pop", p, 1f, 1.3f);
            Sfx.Play("squeak", p, 0.7f, 1.5f);
            GameHUD.FloatingText(p + Vector3.up * 0.9f, "*POP* back to normal!", new Color(1f, 0.75f, 0.9f), 1.1f);
        }
    }

    void HideBody(bool hide)
    {
        if (hide)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || (cube != null && r.transform.IsChildOf(cube)) || r.forceRenderingOff) continue;
                if (r.GetComponent<TMPro.TMP_Text>() != null) continue; // keep name tags
                r.forceRenderingOff = true;
                hidden.Add(r);
            }
        }
        else
        {
            foreach (var r in hidden) if (r != null) r.forceRenderingOff = false;
            hidden.Clear();
        }
    }

    void BuildCube()
    {
        var root = new GameObject("F3 Cube").transform;
        root.SetParent(transform, false);
        // Root scale is Chuck's 0.8: these are pre-scale numbers (≈ 0.8 m world cube).
        var card = ShopModKit.Lit("mat/f3_cube_card", new Color(0.78f, 0.62f, 0.38f), 0.25f);
        var vest = ShopModKit.Lit("mat/f3_cube_vest", new Color(1f, 0.55f, 0.1f), 0.4f);
        var white = ShopModKit.Lit("mat/f3_cube_eye", Color.white, 0.6f);
        var black = ShopModKit.Lit("mat/f3_cube_pupil", new Color(0.05f, 0.05f, 0.06f), 0.6f);
        var body = ShopModKit.Prim(PrimitiveType.Cube, "Body", root, card, new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f));
        ShopModKit.Prim(PrimitiveType.Cube, "Stripe", root, vest, new Vector3(0f, 0.28f, 0f), new Vector3(1.03f, 0.16f, 1.03f));
        ShopModKit.Prim(PrimitiveType.Cube, "Tape", root, vest, new Vector3(0f, 1.0f, 0f), new Vector3(0.18f, 0.03f, 1.02f));
        for (int s = -1; s <= 1; s += 2)
        {
            ShopModKit.Prim(PrimitiveType.Sphere, "Eye", root, white, new Vector3(0.2f * s, 0.7f, 0.5f), new Vector3(0.26f, 0.26f, 0.14f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Pupil", root, black, new Vector3(0.2f * s + 0.03f, 0.68f, 0.57f), new Vector3(0.11f, 0.11f, 0.06f));
        }
        ShopModKit.Prim(PrimitiveType.Cube, "Mouth", root, black, new Vector3(0f, 0.45f, 0.505f), new Vector3(0.3f, 0.05f, 0.02f));
        foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        _ = body;
        cube = root;
    }
}

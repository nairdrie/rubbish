using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Chuck Dumpling's runtime puppet layer (Docs/Character.md sections 11-15). Lives on the Player root next to the
/// Animator and runs in LateUpdate AFTER it (execution order 1000), on every client. Purely cosmetic: it never
/// writes NetworkVariables and never touches gameplay state.
///
/// What it does each frame, on top of the unchanged Starter Assets animation:
/// <list type="bullet">
/// <item>senses motion (root/hips velocity, replicated Animator params) and turns takeoffs, landings and footfalls into
/// spring kicks: bobble head, cap pop/float/spin, belly + seat jiggle, mustache (two lobes out of sync), cowlick,
/// rag, Admiral the duck, pigtails (variant P2);</item>
/// <item>leans the chest (speed, carry, throw charge), splays the arms around the belly (never a clipping frame),
/// windmills them in free fall, and bear-hugs carried bags with two-bone IK (local owner only);</item>
/// <item>drives the face (<see cref="ChuckPuppet"/>.Face.cs): mismatched blinks, squints, brows, mouth shapes, strain
/// tint, idle fidgets;</item>
/// <item>applies the co-op variant (OwnerClientId % 4), keeps the root at 0.8 scale, and hides the cap / head when the
/// camera clips into them.</item>
/// </list>
/// Rules: every pivot write is rest ∘ offset (never accumulated); nothing touches bones while the Animator is disabled
/// (seated in the truck), so additive rotations can't pile up; visibility changes use SetActive only (the truck and
/// chess code toggle renderer.enabled on every child). No per-frame allocations.
/// </summary>
[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public partial class ChuckPuppet : MonoBehaviour
{
    // ------------------------------------------------------------------ serialized (Setup_Character fills these)

    [Tooltip("Puppet parts by ChuckPartsData index (filled by Setup_Character).")]
    public Transform[] parts;

    [Header("Bones (filled by Setup_Character; humanoid lookup is the fallback)")]
    public Transform hips;
    public Transform spine, chest, upperChest, head;
    public Transform upperArmL, lowerArmL, handL;
    public Transform upperArmR, lowerArmR, handR;

    [Header("Body")]
    [Tooltip("Uniform Player root scale (re-asserted after the truck un-parents him).")]
    public float rootScale = 0.8f;
    [Tooltip("Animator playback speed: shorter stride at the same MoveSpeed, so play the clips faster (stubby scurry).")]
    public float animatorSpeed = 1.2f;
    [Tooltip("Co-op look: -1 = OwnerClientId % 4 (host is Chuck). 0 Chuck, 1 Dot, 2 Ozzie, 3 Bert.")]
    public int variantOverride = -1;
    [Tooltip("Deactivate the cap when the camera is closer than this to his head (world m).")]
    public float hideCapDistance = 0.9f;
    [Tooltip("Deactivate the whole head when the camera is closer than this (world m).")]
    public float hideHeadDistance = 0.6f;

    [Header("Arms")]
    [Tooltip("Base outward splay of the upper arms (deg) on top of the animation.")]
    public float splayBase = 18f;
    [Tooltip("Largest extra splay the belly-clearance search may add (deg).")]
    public float splayMaxExtra = 27f;
    [Tooltip("Clearance margins (rig m) for the elbow/wrist and the mitt.")]
    public float elbowMargin = 0.07f, mittMargin = 0.08f;

    // ------------------------------------------------------------------ public read-outs

    /// <summary>Where the Scrapazon headlamp goes (crown surface above the badge; rides with the cap pop).</summary>
    public Transform HeadlampMount => P(ChuckPartsData.HeadlampMount);
    /// <summary>His mouth (under the mustache; Dot's visible smile): where rabies foam and the like come out.</summary>
    public Transform MouthPoint => P(ChuckPartsData.Mouth);
    /// <summary>Applied co-op variant (0..3), -1 before the first LateUpdate.</summary>
    public int Variant { get; private set; } = -1;
    /// <summary>0..1 bear-hug weight (local owner only).</summary>
    public float CarryWeight => carryW;
    /// <summary>True while windmilling in free fall.</summary>
    public bool IsFlailing => flailing;

    // ------------------------------------------------------------------ cached refs

    static readonly int HSpeed = Animator.StringToHash("Speed");
    static readonly int HGrounded = Animator.StringToHash("Grounded");
    static readonly int HJump = Animator.StringToHash("Jump");
    static readonly int HFreeFall = Animator.StringToHash("FreeFall");

    bool inited, valid;
    Animator animator;
    TrashCarrier carrier;
    NetworkObject netObj;
    StarterAssets.StarterAssetsInputs inputs;
    CharacterController cc;
    bool hasParams, paramsChecked;

    Vector3[] restPos, restScale;
    Quaternion[] restRot;
    bool[] defaultActive;
    Vector3[] defaultPos, defaultScale;
    Quaternion[] defaultRot;

    Renderer refRenderer;
    Renderer[] capRenderers, headRenderers;
    GameObject capGo, headGo;
    bool capHidden, headHidden;

    ChuckGooglyEye eyeL, eyeR, eyeBadge;

    Material instPlayer, instHair, instSkin;
    Material sharedPlayer, sharedHair, sharedSkin;
    Color skinBase;
    float shownStrain = -1f;

    // ------------------------------------------------------------------ motion state

    float clock;
    bool motionPrimed;
    Vector3 lastPos, lastHips, hipsVel, prevHipsVel, vel;
    float lastYaw;
    float planarSpeed, vy, yawRate, accFwd, accSide;
    float speedParam;
    bool grounded = true, wasGrounded = true, jumpParam, lastJumpParam, freeFallParam;
    float airTime, minAirVy, freeFallTime, settleTime;
    bool jumpedThisAir, flailing;
    float flailW;
    int footParity;

    // springs (x = offset from rest; units in the comments)
    ChuckSpring sHeadPitch, sHeadRoll;          // deg
    ChuckSpring sCapY, sCapPitch, sCapYaw;      // rig m, deg, deg
    ChuckSpring sBelly, sSeat;                  // squash s
    ChuckSpring sStache, sLobeL, sLobeR;        // deg
    ChuckSpring sCowX, sCowZ;                   // deg
    ChuckSpring sRagPitch, sRagRoll;            // deg
    ChuckSpring sDuck;                          // deg
    ChuckSpring sPigLP, sPigLR, sPigRP, sPigRR; // deg
    ChuckSpring sBrowY, sBrowInner;             // rig m, deg
    ChuckSpring sSplayKick, sChestKick;         // deg
    float lobeTwitchL, lobeTwitchR;
    float waddlePhase;

    // spring constants (Docs/Character.md section 11 starting values)
    const float WHead = 11f, ZHead = 0.35f;
    const float WCap = 14f, ZCap = 0.35f, WCapYaw = 10f, ZCapYaw = 0.3f;
    const float WBelly = 16f, WSeat = 13f, ZBelly = 0.25f;
    const float WStache = 18f, WLobeL = 18f, WLobeR = 21f, ZStache = 0.2f;
    const float WCow = 20f, ZCow = 0.15f;
    const float WRag = 7f, ZRag = 0.25f;
    const float WDuck = 16f, ZDuck = 0.3f;
    const float WPig = 8f, ZPig = 0.25f;
    const float WBrow = 20f, ZBrow = 0.5f;
    const float WSplayKick = 9f, ZSplayKick = 0.35f;
    const float WChestKick = 12f, ZChestKick = 0.35f;

    // ------------------------------------------------------------------ preview hooks (edit mode only)

    [System.NonSerialized] public bool previewMode;
    [System.NonSerialized] public float previewSpeed;
    [System.NonSerialized] public Transform previewHold;
    [System.NonSerialized] public Vector3 previewHoldExtents = new Vector3(0.27f, 0.33f, 0.26f);
    [System.NonSerialized] public float previewMass = 6f, previewCharge;
    [System.NonSerialized] public bool previewFlail;
    [System.NonSerialized] public Transform previewRideTruck;
    [System.NonSerialized] public int previewRideHold = -1;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    void Awake()
    {
        Init();
        if (!valid) return;
        if (animator != null)
        {
            animator.speed = animatorSpeed;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // the skinned mannequin is inactive
        }
        CreateMaterialInstances();
    }

    void OnEnable()
    {
        if (carrier == null) carrier = GetComponent<TrashCarrier>();
        if (carrier != null)
        {
            carrier.PickedUp -= OnCarrierPickedUp;
            carrier.Released -= OnCarrierReleased;
            carrier.PickedUp += OnCarrierPickedUp;
            carrier.Released += OnCarrierReleased;
        }
        motionPrimed = false;
    }

    void OnDisable()
    {
        if (carrier != null)
        {
            carrier.PickedUp -= OnCarrierPickedUp;
            carrier.Released -= OnCarrierReleased;
        }
    }

    void OnDestroy() => ReleaseMaterialInstances();

    void LateUpdate()
    {
        if (!inited) Init();
        if (!valid) return;
        ScaleGuard();
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Seated in the truck (or chess): the Animator is off, so the bones keep last frame's pose. Writing additive
        // rotations now would accumulate every frame and spin the arms. Hands off until it's back.
        if (animator == null || !animator.isActiveAndEnabled)
        {
            motionPrimed = false;
            return;
        }

        int want = DesiredVariant();
        if (want != Variant) ApplyVariant(want);

        Tick(Mathf.Min(dt, 0.1f));
    }

    /// <summary>
    /// Edit-mode preview: initialise without Awake (no material instances unless asked) and run one frame of the
    /// puppet on whatever pose the bones currently have. Used by the Setup_Character preview renders.
    /// </summary>
    public void PreviewTick(float dt, int frames = 1)
    {
        previewMode = true;
        if (!inited) Init();
        if (!valid) return;
        int want = DesiredVariant();
        if (want != Variant) ApplyVariant(want);
        for (int i = 0; i < frames; i++) Tick(dt);
    }

    /// <summary>Edit-mode preview: make the per-player material instances so variants show their colours.</summary>
    public void PreviewMaterials() { if (!inited) Init(); CreateMaterialInstances(); if (Variant >= 0) ApplyVariantColors(Variant); }

    /// <summary>Destroy the per-player material instances and restore the shared materials.</summary>
    public void ReleaseMaterialInstances()
    {
        if (instPlayer == null && instHair == null && instSkin == null) return;
        if (parts != null)
            for (int i = 0; i < parts.Length && i < ChuckPartsData.Count; i++)
            {
                var t = parts[i];
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                var m = r.sharedMaterial;
                if (m == null) continue;
                if (m == instPlayer) r.sharedMaterial = sharedPlayer;
                else if (m == instHair) r.sharedMaterial = sharedHair;
                else if (m == instSkin) r.sharedMaterial = sharedSkin;
            }
        DestroyMat(ref instPlayer);
        DestroyMat(ref instHair);
        DestroyMat(ref instSkin);
    }

    static void DestroyMat(ref Material m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
        m = null;
    }

    // =====================================================================
    // Init
    // =====================================================================

    Transform P(int i) => parts != null && i >= 0 && i < parts.Length ? parts[i] : null;

    void Init()
    {
        if (inited) return;
        inited = true;
        animator = GetComponent<Animator>();
        carrier = GetComponent<TrashCarrier>();
        netObj = GetComponent<NetworkObject>();
        inputs = GetComponent<StarterAssets.StarterAssetsInputs>();
        cc = GetComponent<CharacterController>();

        int n = ChuckPartsData.Count;
        if (parts == null || parts.Length != n || parts[ChuckPartsData.HeadBobble] == null)
        {
            // Fallback: find the markers the builder left.
            parts = new Transform[n];
            foreach (var p in GetComponentsInChildren<ChuckPart>(true))
                if (p.index >= 0 && p.index < n) parts[p.index] = p.transform;
        }
        ResolveBones();

        valid = hips != null && chest != null && upperChest != null && head != null && upperArmL != null && lowerArmL != null
             && handL != null && upperArmR != null && lowerArmR != null && handR != null;
        for (int i = 0; i < n && valid; i++)
            if (parts[i] == null) valid = false;
        if (!valid)
        {
            Debug.LogWarning("[ChuckPuppet] Rig incomplete (run Rubbish/Setup/Character). Puppet disabled.", this);
            return;
        }

        restPos = new Vector3[n]; restScale = new Vector3[n]; restRot = new Quaternion[n];
        defaultPos = new Vector3[n]; defaultScale = new Vector3[n]; defaultRot = new Quaternion[n]; defaultActive = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var t = parts[i];
            restPos[i] = defaultPos[i] = t.localPosition;
            restRot[i] = defaultRot[i] = t.localRotation;
            restScale[i] = defaultScale[i] = t.localScale;
            defaultActive[i] = t.gameObject.activeSelf;
        }

        refRenderer = P(ChuckPartsData.Overalls_Belly).GetComponent<Renderer>();
        capGo = P(ChuckPartsData.CapPivot).gameObject;
        headGo = P(ChuckPartsData.HeadBobble).gameObject;
        capRenderers = capGo.GetComponentsInChildren<Renderer>(true);
        headRenderers = headGo.GetComponentsInChildren<Renderer>(true);

        eyeL = P(ChuckPartsData.EyeRoot_L).GetComponent<ChuckGooglyEye>();
        eyeR = P(ChuckPartsData.EyeRoot_R).GetComponent<ChuckGooglyEye>();
        eyeBadge = P(ChuckPartsData.BadgeEyeRoot).GetComponent<ChuckGooglyEye>();

        hasParams = false;
        paramsChecked = false;

        InitArms();
        InitFace();
    }

    void ResolveBones()
    {
        if (animator != null && animator.isHuman && animator.avatar != null)
        {
            if (hips == null) hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (spine == null) spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            if (upperChest == null) upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (head == null) head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (upperArmL == null) upperArmL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            if (lowerArmL == null) lowerArmL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            if (handL == null) handL = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            if (upperArmR == null) upperArmR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (lowerArmR == null) lowerArmR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            if (handR == null) handR = animator.GetBoneTransform(HumanBodyBones.RightHand);
        }
        if (upperChest == null) upperChest = chest;
    }

    void CreateMaterialInstances()
    {
        if (!valid || instPlayer != null) return;
        for (int i = 0; i < ChuckPartsData.Count; i++)
        {
            var role = ChuckPartsData.Parts[i].role;
            if (role != ChuckRole.PlayerColor && role != ChuckRole.Hair && role != ChuckRole.Skin) continue;
            var r = parts[i].GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) continue;
            ref Material inst = ref instPlayer;
            ref Material shared = ref sharedPlayer;
            if (role == ChuckRole.Hair) { inst = ref instHair; shared = ref sharedHair; }
            else if (role == ChuckRole.Skin) { inst = ref instSkin; shared = ref sharedSkin; }
            if (inst == null)
            {
                shared = r.sharedMaterial;
                inst = new Material(shared) { name = shared.name + " (Chuck)" };
            }
            r.sharedMaterial = inst;
        }
        if (instSkin != null) skinBase = instSkin.GetColor(BaseColorId);
        shownStrain = -1f;
    }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    // =====================================================================
    // Variant (Docs/Character.md section 14)
    // =====================================================================

    int DesiredVariant()
    {
        int n = ChuckPartsData.Variants.Length;
        if (variantOverride >= 0) return variantOverride % n;
        if (netObj != null && netObj.IsSpawned) return (int)(netObj.OwnerClientId % (ulong)n);
        return 0;
    }

    void ApplyVariant(int v)
    {
        v = Mathf.Clamp(v, 0, ChuckPartsData.Variants.Length - 1);
        var def = ChuckPartsData.Variants[v];

        // Back to the built (P1) state first.
        for (int i = 0; i < ChuckPartsData.Count; i++)
        {
            var t = parts[i];
            t.localPosition = restPos[i] = defaultPos[i];
            t.localRotation = restRot[i] = defaultRot[i];
            t.localScale = restScale[i] = defaultScale[i];
            if (i == ChuckPartsData.CapPivot || i == ChuckPartsData.HeadBobble) continue; // owned by the near-camera hide
            if (t.gameObject.activeSelf != defaultActive[i]) SetActiveSynced(t.gameObject, defaultActive[i]);
        }
        foreach (int i in def.off) SetActiveSynced(parts[i].gameObject, false);
        foreach (int i in def.on) SetActiveSynced(parts[i].gameObject, true);
        foreach (var o in def.overrides)
        {
            var t = parts[o.part];
            if (o.hasPos) t.localPosition = restPos[o.part] = o.localPos;
            if (o.hasEuler) t.localRotation = restRot[o.part] = Quaternion.Euler(o.localEuler);
            if (o.hasScale) t.localScale = restScale[o.part] = o.scale;
        }
        int cap = ChuckPartsData.CapPivot;
        restRot[cap] = defaultRot[cap] * Quaternion.Euler(0f, def.capYaw, 0f);
        parts[cap].localRotation = restRot[cap];
        int lamp = ChuckPartsData.HeadlampMount;
        var lp = defaultPos[lamp]; lp.z = def.headlampZ;
        parts[lamp].localPosition = restPos[lamp] = lp;

        SetMouthShapes(def);
        ApplyVariantColors(v);
        RefreshArmShapes();
        Variant = v;
    }

    // Business colour (Docs/Multiplayer.md §13.3: Depot Row tints cap, shirt and sleeves). Null = the variant's own colour.
    Color? playerColorOverride;
    // N4 payroll (§13.6): an employee wears the employer's colour over their own while on the job. Wins over the above.
    Color? vestColorOverride;

    /// <summary>The employer's colour while on the job (null = off the clock: back to the player's own colour).</summary>
    public void SetVestColor(Color? c)
    {
        if (vestColorOverride == c) return;
        vestColorOverride = c;
        if (Variant >= 0) ApplyVariantColors(Variant);
    }

    /// <summary>Tint the player-colour parts (cap, shirt, sleeves) in a business colour; null restores the variant colour.</summary>
    public void SetPlayerColor(Color? c)
    {
        if (playerColorOverride == c) return;
        playerColorOverride = c;
        if (Variant >= 0) ApplyVariantColors(Variant);
    }

    void ApplyVariantColors(int v)
    {
        var def = ChuckPartsData.Variants[v];
        if (instPlayer != null) instPlayer.SetColor(BaseColorId, vestColorOverride ?? playerColorOverride ?? def.playerColor);
        if (instHair != null) instHair.SetColor(BaseColorId, def.hair);
    }

    /// <summary>SetActive, and when turning on, match the child renderers' enabled state to the body (hidden in the truck, etc).</summary>
    void SetActiveSynced(GameObject go, bool on)
    {
        if (go.activeSelf == on) return;
        go.SetActive(on);
        if (!on || refRenderer == null) return;
        bool vis = refRenderer.enabled;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = vis; // rare (variant switch): allocation ok
    }

    // =====================================================================
    // Frame
    // =====================================================================

    void Tick(float dt)
    {
        clock += dt;
        Sense(dt);
        SenseCarry(dt);
        UpdateFidget(dt);
        UpdateFace(dt);
        StepSprings(dt);
        WritePivots();
        PoseBody(dt);
        PoseRide(dt);
        PoseArms(dt);
        PosePunch(dt);
        PoseRideGrip();
        ApplyStrainTint();
        UpdateNearCamera();
    }

    void ScaleGuard()
    {
        // The truck parents him with `parent =` and un-parents with SetParent(null, true), which keeps the scale;
        // this is only a safety net.
        if (previewMode || rootScale <= 0f || transform.parent != null) return;
        if (Mathf.Abs(transform.localScale.x - rootScale) > 0.001f || Mathf.Abs(transform.localScale.y - rootScale) > 0.001f)
            transform.localScale = Vector3.one * rootScale;
    }

    // ------------------------------------------------------------------ sensing

    void Sense(float dt)
    {
        Vector3 pos = transform.position;
        Vector3 hp = hips.position;
        if (!motionPrimed || (pos - lastPos).sqrMagnitude > 9f)
        {
            motionPrimed = true;
            lastPos = pos; lastHips = hp;
            vel = hipsVel = prevHipsVel = Vector3.zero;
            lastYaw = transform.eulerAngles.y;
        }
        vel = (pos - lastPos) / dt;
        lastPos = pos;
        Vector3 hv = (hp - lastHips) / dt;
        lastHips = hp;
        hipsVel = Vector3.Lerp(hipsVel, hv, 1f - Mathf.Exp(-20f * dt));
        Vector3 acc = Vector3.ClampMagnitude((hipsVel - prevHipsVel) / dt, 30f);
        prevHipsVel = hipsVel;

        Vector3 f = transform.forward; f.y = 0f;
        f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        Vector3 r = new Vector3(f.z, 0f, -f.x);
        accFwd = Vector3.Dot(acc, f);
        accSide = Vector3.Dot(acc, r);
        planarSpeed = Mathf.Min(new Vector2(vel.x, vel.z).magnitude, 12f);
        vy = Mathf.Clamp(vel.y, -40f, 40f);
        float yaw = transform.eulerAngles.y;
        yawRate = Mathf.Clamp(Mathf.DeltaAngle(lastYaw, yaw) / dt, -720f, 720f);
        lastYaw = yaw;

        if (!paramsChecked && !previewMode && animator != null && animator.isInitialized)
        {
            // Checked once the Animator is up (not in Awake): Starter Assets' Speed/Grounded/Jump/FreeFall.
            paramsChecked = true;
            int found = 0;
            if (animator.runtimeAnimatorController != null)
                foreach (var prm in animator.parameters)
                    if (prm.nameHash == HSpeed || prm.nameHash == HGrounded || prm.nameHash == HJump || prm.nameHash == HFreeFall) found++;
            hasParams = found == 4;
        }
        if (previewMode || !hasParams)
        {
            grounded = !previewFlail;
            jumpParam = false;
            freeFallParam = previewFlail;
            speedParam = previewMode ? previewSpeed : planarSpeed;
        }
        else
        {
            grounded = animator.GetBool(HGrounded);
            jumpParam = animator.GetBool(HJump);
            freeFallParam = animator.GetBool(HFreeFall);
            speedParam = animator.GetFloat(HSpeed);

            // The Starter Assets ground check only sees the Ground layer, so on a sandbox, tyre, bag or the truck roof
            // the Animator says "falling" forever while he stands still. Trust the capsule (owner only: remote copies
            // are moved by the network) and, for everyone, a root that has stopped moving vertically.
            settleTime = Mathf.Abs(vy) < 0.35f ? settleTime + dt : 0f;
            if (!grounded)
            {
                bool local = netObj == null || !netObj.IsSpawned || netObj.IsOwner;
                if ((local && cc != null && cc.enabled && cc.isGrounded) || settleTime > 0.25f) grounded = true;
            }
        }

        if (jumpParam && !lastJumpParam) OnTakeoff();
        lastJumpParam = jumpParam;

        if (!grounded)
        {
            if (wasGrounded)
            {
                airTime = 0f;
                minAirVy = 0f;
                jumpedThisAir = jumpParam || vy > 1.5f;
            }
            airTime += dt;
            minAirVy = Mathf.Min(minAirVy, vy);
            freeFallTime = freeFallParam ? freeFallTime + dt : 0f;
            // Walked off a ledge: flail after 0.35 s of free fall, and only while actually dropping. A normal jump only
            // flails if it drops further than a flat jump would (vy past -6.5 m/s means he jumped down off something).
            if (!flailing && freeFallTime > 0.35f && vy < -2f && (!jumpedThisAir || vy < -6.5f)) StartFlail();
            if (previewMode && previewFlail) flailing = true;
        }
        else
        {
            if (!wasGrounded && airTime > 0.08f) OnLanded(-minAirVy);
            airTime = 0f;
            freeFallTime = 0f;
            if (flailing) flailing = false;
        }
        wasGrounded = grounded;
        flailW = Mathf.MoveTowards(flailW, flailing ? 1f : 0f, dt / (flailing ? 0.15f : 0.1f));
    }

    void OnTakeoff()
    {
        sCapY.KickPeak(0.06f, WCap, ZCap);
        sCapPitch.KickPeak(-10f, WCap, ZCap);
        sBelly.KickPeak(0.06f, WBelly, ZBelly);
        sSeat.KickPeak(0.06f, WSeat, ZBelly);
        sCowX.KickPeak(-14f, WCow, ZCow);
        sStache.KickPeak(-8f, WStache, ZStache);
        Yell(0.22f);
        AbortFidget();
    }

    void OnLanded(float fallSpeed)
    {
        float k = Mathf.Clamp01(fallSpeed / 6f);
        sBelly.KickPeak(-0.12f * k, WBelly, ZBelly);
        sSeat.KickPeak(-0.12f * k, WSeat, ZBelly);
        sHeadPitch.KickPeak(4f * k, WHead, ZHead);
        sDuck.KickPeak(-12f * k, WDuck, ZDuck);
        sCowX.KickPeak(25f * k, WCow, ZCow);
        sLobeL.KickPeak(-6f * k, WLobeL, ZStache);
        sLobeR.KickPeak(6f * k, WLobeR, ZStache);
        if (fallSpeed > 3f)
        {
            // Cap slams down, then pops back up with a spin.
            sCapY.x = -0.02f; sCapY.v = 0f;
            sCapY.KickPeak(0.06f, WCap, ZCap);
            sCapYaw.KickPeak(Random.value < 0.5f ? -25f : 25f, WCapYaw, ZCapYaw);
            sCapPitch.KickPeak(6f, WCap, ZCap);
            ForceBlink();
        }
        if (flailing || flailW > 0.2f)
        {
            flailing = false;
            Oof(0.25f);
            SnapMouthShut();
        }
    }

    void StartFlail()
    {
        flailing = true;
        sCapY.KickPeak(0.05f, WCap, ZCap);
    }

    /// <summary>Animation event (Walk_N / Run_N): Unity calls it on every component of the Animator's GameObject.</summary>
    void OnFootstep(AnimationEvent e)
    {
        if (!valid || e.animatorClipInfo.weight <= 0.5f) return;
        float s = Mathf.Clamp01(planarSpeed / 5f);
        footParity ^= 1;
        float side = footParity == 0 ? 1f : -1f;
        sHeadPitch.KickPeak(1.5f * s, WHead, ZHead);
        sBelly.KickPeak(-0.025f * s, WBelly, ZBelly);
        sSeat.KickPeak(-0.025f * s, WSeat, ZBelly);
        sDuck.KickPeak(-6f * s, WDuck, ZDuck);
        float ls = 1.5f * Mathf.Clamp01(planarSpeed / 3f);
        sLobeL.KickPeak(side * ls, WLobeL, ZStache);
        sLobeR.KickPeak(-side * ls, WLobeR, ZStache);
        sHeadRoll.KickPeak(side * 0.8f * s, WHead, ZHead);
    }

    // ------------------------------------------------------------------ carry sensing

    bool carrying, carryHasTarget, charging, sharedGripPose;
    Vector3 sharedGripTarget;
    float carryLin, carryW, releaseDur = 0.12f;
    float heldMass, charge;
    Vector3 holdLocal, holdExtents;
    Carryable watchItem;
    float watchUntil;

    void SenseCarry(float dt)
    {
        carrying = false;
        sharedGripPose = false;
        heldMass = 0f;
        charge = 0f;
        charging = false;
        if (previewMode)
        {
            if (previewHold != null)
            {
                carrying = true;
                holdLocal = transform.InverseTransformPoint(previewHold.position);
                holdExtents = previewHoldExtents;
                heldMass = previewMass;
                charge = previewCharge;
                charging = charge > 0f;
                carryLin = 1f;
            }
        }
        else if (carrier != null && carrier.IsLocalOwner && carrier.IsCarrying)
        {
            var held = carrier.Held;
            if (held != null && held.State == CarryState.Carried)
            {
                heldMass = held.MassKg;
                charging = carrier.IsCharging;
                charge = carrier.Charge01;
                var hp = carrier.HoldPoint;
                if (!carrier.HasHoldOverride && hp != null && (hp.position - chest.position).sqrMagnitude < 0.81f)
                {
                    carrying = true;
                    holdLocal = transform.InverseTransformPoint(hp.position);
                    holdExtents = carrier.HeldExtents;
                }
            }
        }
        // Shared grips pose both players' hands on every machine, including the helper with no single-owner carrier.
        if (!previewMode && netObj != null && netObj.IsSpawned && HandsNet.Instance != null)
        {
            var shared = HandsNet.Instance.TugOf(netObj.OwnerClientId);
            var item = shared.HasValue ? HandsNet.Instance.Get(shared.Value.item)?.Item : null;
            if (item != null)
            {
                Vector3 toward = item.WorldCenter - transform.position; toward.y = 0f;
                toward = toward.sqrMagnitude > 0.01f ? toward.normalized : transform.forward;
                Vector3 gripAt = item.WorldCenter - toward * 0.22f;
                sharedGripPose = true;
                sharedGripTarget = gripAt + Vector3.up * 0.08f;
                carrying = true;
                heldMass = item.MassKg * 0.5f;
                holdLocal = transform.InverseTransformPoint(gripAt);
                holdExtents = new Vector3(0.18f, 0.18f, 0.15f);
            }
        }
        if (carrying) carryHasTarget = true;
        carryLin = Mathf.MoveTowards(carryLin, carrying ? 1f : 0f, dt / (carrying ? 0.15f : Mathf.Max(0.05f, releaseDur)));
        carryW = carryLin * (2f - carryLin); // ease-out
        if (carryLin <= 0f) carryHasTarget = false;

        // Did the thing we tossed/threw get eaten by the truck (or a bin)? Om-nom.
        if (!ReferenceEquals(watchItem, null))
        {
            if (watchItem == null || watchItem.State == CarryState.Consumed)
            {
                watchItem = null;
                Happy();
            }
            else if (clock > watchUntil) watchItem = null;
        }
    }

    void OnCarrierPickedUp(Carryable c)
    {
        if (!valid) return;
        releaseDur = 0.12f;
        Hup();
        sBelly.KickPeak(-0.06f, WBelly, ZBelly);
        sSeat.KickPeak(-0.04f, WSeat, ZBelly);
        sCowX.KickPeak(18f, WCow, ZCow);
        sStache.KickPeak(-8f, WStache, ZStache);
        sLobeL.KickPeak(5f, WLobeL, ZStache);
        sLobeR.KickPeak(-5f, WLobeR, ZStache);
        AbortFidget();
    }

    void OnCarrierReleased(Carryable c)
    {
        if (!valid) return;
        releaseDur = 0.12f;
        if (c == null || c.State == CarryState.Consumed) return;
        if (c.State == CarryState.Resting)
        {
            // Drop (G) or knocked out of his arms: arms flop, small "oof".
            releaseDur = 0.2f;
            Oof(0.25f);
            return;
        }
        bool scripted = c.Body == null || c.Body.isKinematic;
        if (!scripted)
        {
            // Thrown: fling the arms up and out, HWAAH!, cap pop, mustache flutter, blink, chest snaps forward.
            sSplayKick.KickPeak(37f, WSplayKick, ZSplayKick); // 18 -> 55 -> 18
            Yell(0.3f);
            sCapY.KickPeak(0.04f, WCap, ZCap);
            sStache.KickPeak(-8f, WStache, ZStache);
            sLobeL.KickPeak(10f, WLobeL, ZStache);
            sLobeR.KickPeak(-10f, WLobeR, ZStache);
            sChestKick.KickPeak(8f, WChestKick, ZChestKick);
            ForceBlink();
        }
        else
        {
            // Toss (E) / handed to a script: the same, gentler.
            sSplayKick.KickPeak(22f, WSplayKick, ZSplayKick); // 18 -> 40 -> 18
            Yell(0.2f);
            sLobeL.KickPeak(5f, WLobeL, ZStache);
            sLobeR.KickPeak(-5f, WLobeR, ZStache);
            sChestKick.KickPeak(4f, WChestKick, ZChestKick);
        }
        watchItem = c;
        watchUntil = clock + 3f;
    }

    // ------------------------------------------------------------------ springs

    void StepSprings(float dt)
    {
        bool air = !grounded;

        sHeadPitch.Step(Mathf.Clamp(-0.8f * accFwd, -5f, 5f) + fidgetHeadPitch, WHead, ZHead, dt);
        sHeadPitch.Clamp(-7f, 7f + Mathf.Max(0f, fidgetHeadPitch));
        sHeadRoll.Step(Mathf.Clamp(0.6f * accSide, -4f, 4f), WHead, ZHead, dt);
        sHeadRoll.Clamp(-5f, 5f);

        float capYT = (air && Mathf.Abs(vy) < 2f ? 0.06f : 0f) + 0.08f * flailW + fidgetCapY;
        sCapY.Step(capYT, WCap, ZCap, dt);
        sCapY.Clamp(-0.02f, 0.11f);
        sCapPitch.Step(Mathf.Clamp(-0.6f * accFwd, -5f, 5f) - 5f * charge, WCap, ZCap, dt);
        sCapPitch.Clamp(-14f, 10f);
        sCapYaw.Step(0f, WCapYaw, ZCapYaw, dt);
        sCapYaw.Clamp(-30f, 30f);

        float breath = 0.015f * Mathf.Sin(clock * (2f * Mathf.PI * 0.3f));
        sBelly.Step(breath, WBelly, ZBelly, dt);
        sBelly.Clamp(-0.12f, 0.08f);
        sSeat.Step(breath * 0.6f, WSeat, ZBelly, dt);
        sSeat.Clamp(-0.12f, 0.08f);

        sStache.Step(0f, WStache, ZStache, dt);
        sStache.Clamp(-10f, 10f);
        float lobeT = Mathf.Clamp(yawRate * 0.02f, -6f, 6f);
        if (clock > lobeTwitchL) { lobeTwitchL = clock + Random.Range(3f, 6f); sLobeL.KickPeak(Random.Range(-6f, 6f), WLobeL, ZStache); }
        if (clock > lobeTwitchR) { lobeTwitchR = clock + Random.Range(3f, 6f); sLobeR.KickPeak(Random.Range(-6f, 6f), WLobeR, ZStache); }
        sLobeL.Step(lobeT, WLobeL, ZStache, dt);
        sLobeL.Clamp(-12f, 12f);
        sLobeR.Step(lobeT, WLobeR, ZStache, dt);
        sLobeR.Clamp(-12f, 12f);

        sCowX.Step(Mathf.Clamp(accFwd * 1.2f, -20f, 20f), WCow, ZCow, dt);
        sCowX.Clamp(-35f, 35f);
        sCowZ.Step(Mathf.Clamp(-accSide * 1.2f, -20f, 20f), WCow, ZCow, dt);
        sCowZ.Clamp(-35f, 35f);

        float run01 = Mathf.Clamp01(planarSpeed / 5.3f);
        float flap = Mathf.Sin(clock * 11f) * 4f * Mathf.Clamp01(planarSpeed / 4f);
        sRagPitch.Step(20f * run01 + Mathf.Clamp(accFwd * 2.5f, -10f, 20f) + flap + 12f * flailW, WRag, ZRag, dt);
        sRagPitch.Clamp(0f, 60f, 0.3f);
        sRagRoll.Step(Mathf.Clamp(-yawRate * 0.04f - accSide * 2f, -25f, 25f), WRag, ZRag, dt);
        sRagRoll.Clamp(-30f, 30f);

        sDuck.Step(0f, WDuck, ZDuck, dt);
        sDuck.Clamp(-15f, 15f);

        if (Variant == 1)
        {
            float pp = Mathf.Clamp(accFwd * 2.5f, -15f, 15f) + 8f * run01;
            float pr = Mathf.Clamp(-yawRate * 0.04f - accSide * 2f, -25f, 25f);
            sPigLP.Step(pp, WPig, ZPig, dt); sPigLP.Clamp(-30f, 30f);
            sPigRP.Step(pp, WPig * 1.1f, ZPig, dt); sPigRP.Clamp(-30f, 30f);
            sPigLR.Step(pr, WPig, ZPig, dt); sPigLR.Clamp(-30f, 30f);
            sPigRR.Step(pr, WPig * 1.1f, ZPig, dt); sPigRR.Clamp(-30f, 30f);
        }

        sBrowY.Step(browYT, WBrow, ZBrow, dt);
        sBrowY.Clamp(-0.02f, 0.025f);
        sBrowInner.Step(browInnerT, WBrow, ZBrow, dt);
        sBrowInner.Clamp(-20f, 20f);

        sSplayKick.Step(0f, WSplayKick, ZSplayKick, dt);
        sSplayKick.Clamp(-10f, 60f);
        sChestKick.Step(0f, WChestKick, ZChestKick, dt);
        sChestKick.Clamp(-12f, 12f);
    }

    static Vector3 Jiggle(float s)
    {
        float xz = 1f / Mathf.Sqrt(1f + s);
        return new Vector3(xz, 1f + s, xz);
    }

    void WritePivots()
    {
        // Heavy bag: head trembles with the effort.
        float tremP = 0f, tremR = 0f;
        if (heldMass > 12f && carryW > 0.5f)
        {
            float k = Mathf.Clamp01((heldMass - 12f) / 6f + 0.5f);
            tremR = (Mathf.Sin(clock * (2f * Mathf.PI * 2f)) * 1.5f + Mathf.Sin(clock * 71f) * 0.5f) * k;
            tremP = Mathf.Sin(clock * 59f) * 0.4f * k;
        }
        int i = ChuckPartsData.HeadBobble;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sHeadPitch.x + tremP, fidgetHeadYaw, sHeadRoll.x + tremR);

        i = ChuckPartsData.CapPivot;
        parts[i].localPosition = restPos[i] + new Vector3(0f, sCapY.x, 0f);
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sCapPitch.x, sCapYaw.x, 0f);

        parts[ChuckPartsData.BellyPivot].localScale = Jiggle(sBelly.x);
        i = ChuckPartsData.Overalls_Seat;
        parts[i].localScale = Vector3.Scale(restScale[i], Jiggle(sSeat.x));

        i = ChuckPartsData.StachePivot;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sStache.x, 0f, 0f);
        parts[i].localScale = new Vector3(1f + 0.1f * charge, 1f, 1f);
        i = ChuckPartsData.StachePivot_L;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(0f, 0f, sLobeL.x);
        i = ChuckPartsData.StachePivot_R;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(0f, 0f, sLobeR.x);

        i = ChuckPartsData.CowlickPivot;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sCowX.x, 0f, sCowZ.x);
        i = ChuckPartsData.RagPivot;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sRagPitch.x, 0f, sRagRoll.x);
        i = ChuckPartsData.DuckPivot;
        parts[i].localRotation = restRot[i] * Quaternion.Euler(sDuck.x, 0f, 0f);

        if (Variant == 1)
        {
            i = ChuckPartsData.PigtailPivot_L;
            parts[i].localRotation = restRot[i] * Quaternion.Euler(sPigLP.x, 0f, sPigLR.x);
            i = ChuckPartsData.PigtailPivot_R;
            parts[i].localRotation = restRot[i] * Quaternion.Euler(sPigRP.x, 0f, sPigRR.x);
        }

        WriteFace();
    }

    // ------------------------------------------------------------------ body lean

    void PoseBody(float dt)
    {
        Vector3 right = transform.right;
        Vector3 fwd = transform.forward;
        float run01 = Mathf.Clamp01(planarSpeed / 5.3f);
        float speedLean = 4f * run01 * (grounded ? 1f : 0.5f);
        float massK = Mathf.Clamp01(heldMass / 20f);
        float carryLean = (4f + 6f * massK) * carryW;
        float chargeLean = 10f * charge * carryW;

        float chestAng = speedLean - 0.6f * carryLean - chargeLean + sChestKick.x;
        float upperAng = -0.4f * carryLean;
        float headAng = -2f * run01 + 4f * carryW;

        // Heavy bag: waddle (hips roll at step frequency, chest counter-rolls).
        float waddle = 0f;
        if (heldMass > 10f && carryW > 0.01f)
        {
            float moving = Mathf.Clamp01(planarSpeed / 1.2f);
            waddlePhase += dt * (5f + planarSpeed * 2.2f);
            waddle = 3f * Mathf.Sin(waddlePhase) * moving * carryW;
        }

        if (Mathf.Abs(waddle) > 0.01f) hips.rotation = Quaternion.AngleAxis(waddle, fwd) * hips.rotation;
        if (Mathf.Abs(chestAng) > 0.01f || Mathf.Abs(waddle) > 0.01f)
            chest.rotation = Quaternion.AngleAxis(-waddle * 1.3f, fwd) * Quaternion.AngleAxis(chestAng, right) * chest.rotation;
        if (upperChest != chest && Mathf.Abs(upperAng) > 0.01f) upperChest.rotation = Quaternion.AngleAxis(upperAng, right) * upperChest.rotation;
        if (Mathf.Abs(headAng) > 0.01f) head.rotation = Quaternion.AngleAxis(headAng, right) * head.rotation;
    }

    // ------------------------------------------------------------------ camera clipping (section 15.13)

    void UpdateNearCamera()
    {
        if (previewMode) return;
        var cam = Camera.main;
        bool hideCap = false, hideHead = false;
        if (cam != null && cam.isActiveAndEnabled)
        {
            float d = (cam.transform.position - head.position).magnitude;
            hideHead = headHidden ? d < hideHeadDistance + 0.05f : d < hideHeadDistance;
            hideCap = hideHead || (capHidden ? d < hideCapDistance + 0.05f : d < hideCapDistance);
        }
        if (hideCap != capHidden)
        {
            capHidden = hideCap;
            capGo.SetActive(!hideCap);
            if (!hideCap) SyncRenderers(capRenderers);
        }
        if (hideHead != headHidden)
        {
            headHidden = hideHead;
            headGo.SetActive(!hideHead);
            if (!hideHead) SyncRenderers(headRenderers);
        }
    }

    /// <summary>After re-activating hidden parts, match their renderers to the body's (the truck/sort station may have hidden it meanwhile).</summary>
    void SyncRenderers(Renderer[] rs)
    {
        if (refRenderer == null) return;
        bool vis = refRenderer.enabled;
        for (int i = 0; i < rs.Length; i++)
            if (rs[i] != null && rs[i].enabled != vis) rs[i].enabled = vis;
    }
}

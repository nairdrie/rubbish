using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The live 3D backdrop of the main menu (Docs/UIPass.md §4.7): Gertrude (the FEED ME truck) idling at a golden-hour curb,
/// Chuck doing his fidgets and chucking bags into the hopper, and a raccoon in the bin. Built by
/// <c>Editor/Setup_MenuDiorama</c> into <c>MainMenuScene</c> (stripped <c>MenuChuck</c> / <c>MenuTruck</c> copies; 0
/// NetworkObjects, 0 Rigidbodies); this component only animates it, with existing APIs and cosmetic randomness.
/// <list type="bullet">
/// <item><b>Beats</b> in random order, never the same twice running: idle fidgets (6–10 s), the Chuck (4 s: bear-hug,
/// strain, HWAAH, the bag arcs into the hopper, compactor), Raccoon! (3 s) and Honk (1.5 s). Each one is logged and counted
/// (<see cref="BeatCounts"/>).</item>
/// <item><b>Hover reactions</b> from the menu: Play solo → Chuck looks at you; Host with friends → Dot, Ozzie and Bert slide
/// onto the rear steps and wave; Join → the lights double-flash; Quit → Chuck yawns.</item>
/// <item><b>Clicks</b> on the set (not over UI): Chuck pats his tummy, the truck honks, the bin hisses.</item>
/// <item><b>Camera</b>: the scene's Main Camera, a 3/4 view at FOV 35 with a 12 s ±1.5° sway and ±1° mouse parallax, framed
/// into the space right of the menu column (<see cref="SetUiInsetLeft"/>) at any aspect ratio.</item>
/// </list>
/// The look (skybox, fog, ambient, sun) is applied to the menu scene at runtime only; the scene file's lighting is untouched.
/// </summary>
public sealed class MenuDiorama : MonoBehaviour
{
    public enum Beat { Idle, Chuck, Raccoon, Honk }

    [Header("Filled by Setup_MenuDiorama")]
    public Camera cam;
    public Transform focus;
    public float camYaw = -32f, camPitch = 11f, camFov = 35f;
    [Tooltip("Metres of the set the camera keeps in view (width × height around the focus).")]
    public Vector2 frame = new Vector2(7.2f, 4.2f);
    public Light sun;
    public Material skybox;
    public Color fogColor = new Color(0.98f, 0.78f, 0.6f);
    public float fogDensity = 0.012f;
    public Color ambientSky = new Color(0.95f, 0.78f, 0.62f), ambientEquator = new Color(0.82f, 0.68f, 0.6f), ambientGround = new Color(0.35f, 0.3f, 0.28f);
    public Transform truck;
    public Transform hopperMouth;
    public Light[] truckLights;
    public Renderer[] tailLights;
    public ChuckPuppet chuck;
    public ChuckPuppet[] crew;
    public Transform[] crewSpots;
    public Transform bin, binLid, raccoonAnchor;
    public Transform[] bagSpots;
    public Collider chuckClick, truckClick, binClick;
    [Tooltip("Setup stamp (version + source prefab hashes): a re-run with the same stamp changes nothing.")]
    public string stamp;

    /// <summary>The diorama in the open menu (null elsewhere).</summary>
    public static MenuDiorama Active { get; private set; }
    /// <summary>How many times each beat has played this run (acceptance check UP4-4).</summary>
    public static readonly int[] BeatCounts = new int[4];
    /// <summary>Rigidbodies stripped at runtime (the raccoon builder adds one in play mode).</summary>
    public static int StrippedBodies { get; private set; }
    /// <summary>Seconds Awake spent on the look and the runtime bits (bags, raccoon).</summary>
    public static float BuildSeconds { get; private set; }

    float insetLeft = 0.36f;
    Vector3 truckHome;
    Quaternion chuckHomeRot;
    Vector3 chuckHomePos;
    readonly List<Transform> bags = new List<Transform>();       // per spot: a holder whose origin is the bag's bottom-centre
    readonly List<Transform> bagCenters = new List<Transform>(); // the hug target (middle of the sack)
    readonly List<Vector3> crewHome = new List<Vector3>();
    CritterRig raccoon;
    Vector3 raccoonDown, raccoonUp;
    Quaternion lidClosed;
    Animator chuckAnim;
    readonly List<Animator> crewAnims = new List<Animator>();
    float[] lightBase;
    MaterialPropertyBlock mpb;

    // beat state
    Beat beat = Beat.Idle, lastBeat = Beat.Idle;
    float beatT, beatLen = 6f, gapLeft = 1.5f;
    bool inBeat;
    int bagIndex;
    bool thrown;
    bool camSnapped;
    Transform flying;
    Vector3 flyFrom, flyTo;
    float flyT = -1f, respawnAt = -1f;
    int respawnBag = -1;

    // reactions
    MainMenuCoopUI.HoverKind hover;
    float crewOut, flashT = 9f, lookW, quitYawnAt = -1f;
    bool built;

    static readonly int HGrounded = Animator.StringToHash("Grounded");
    static readonly int HMotion = Animator.StringToHash("MotionSpeed");
    static readonly int HSpeed = Animator.StringToHash("Speed");

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (!Application.isPlaying) return;
        float t0 = Time.realtimeSinceStartup;
        ApplyLook();
        BuildRuntimeBits();
        BuildSeconds = Time.realtimeSinceStartup - t0;
    }

    void OnEnable() { if (Application.isPlaying) Active = this; }
    void OnDisable() { if (Active == this) Active = null; }

    /// <summary>Golden hour on the menu scene (runtime only: the scene's saved lighting never changes).</summary>
    public void ApplyLook()
    {
        if (skybox != null) RenderSettings.skybox = skybox;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogDensity = fogDensity;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = ambientSky;
        RenderSettings.ambientEquatorColor = ambientEquator;
        RenderSettings.ambientGroundColor = ambientGround;
        if (sun != null) RenderSettings.sun = sun;
        if (cam != null)
        {
            cam.clearFlags = skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            cam.fieldOfView = camFov;
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 400f;
        }
    }

    /// <summary>
    /// Things made at runtime from code that only exists at runtime: the curb bags (TrashBag's sack visual), the raccoon
    /// (CritterLook) and the animator flags. Edit-mode previews call it too (in a scene they close without saving).
    /// </summary>
    public void BuildRuntimeBits()
    {
        if (built) return;
        built = true;
        mpb = new MaterialPropertyBlock();
        if (truck != null) truckHome = truck.localPosition;
        if (chuck != null)
        {
            chuckHomePos = chuck.transform.position;
            chuckHomeRot = chuck.transform.rotation;
            chuckAnim = chuck.GetComponent<Animator>();
            GroundAnimator(chuckAnim);
        }
        crewAnims.Clear();
        crewHome.Clear();
        if (crew != null)
            for (int i = 0; i < crew.Length; i++)
            {
                var c = crew[i];
                crewHome.Add(c != null ? c.transform.position : Vector3.zero);
                var a = c != null ? c.GetComponent<Animator>() : null;
                crewAnims.Add(a);
                GroundAnimator(a);
                if (c != null) c.gameObject.SetActive(false);
            }
        if (bagSpots != null)
            for (int i = 0; i < bagSpots.Length; i++)
            {
                var spot = bagSpots[i];
                if (spot == null) { bags.Add(null); bagCenters.Add(null); continue; }
                var holder = new GameObject("Bag" + i).transform;
                holder.SetParent(spot, false);
                Transform center = null;
                try
                {
                    TrashBag.BuildSackVisual(new BagContents { bagId = "menu/bag" + i, sackVariant = i % 3 }, holder);
                    center = new GameObject("Center").transform;
                    center.SetParent(holder, false);
                    center.localPosition = new Vector3(0f, 0.32f, 0f);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MenuDiorama] no bag visual: " + e.Message);
                    Destroy(holder.gameObject);
                    holder = null;
                }
                bags.Add(holder);
                bagCenters.Add(center);
            }
        if (raccoonAnchor != null)
        {
            try
            {
                var go = new GameObject("Raccoon");
                go.transform.SetParent(raccoonAnchor, false);
                raccoon = CritterLook.BuildRaccoon(go, false);
                go.transform.localRotation = Quaternion.Euler(-55f, 0f, 0f); // standing up in the bin, nose over the rim
                // the critter rig adds a trigger body in play mode; the diorama keeps 0 Rigidbodies and 0 colliders on it
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { DestroyImmediate(rb); StrippedBodies++; }
                foreach (var col in go.GetComponentsInChildren<Collider>(true)) DestroyImmediate(col);
                go.transform.localScale = Vector3.one * 0.85f;
                raccoonUp = raccoonAnchor.localPosition;                     // peeking over the rim
                raccoonDown = raccoonUp + new Vector3(0f, -0.55f, 0f);       // hidden inside the bin
                raccoonAnchor.localPosition = raccoonDown;
            }
            catch (Exception e)
            {
                raccoon = null;
                Debug.LogWarning("[MenuDiorama] no raccoon visual (beat skipped): " + e.Message);
            }
        }
        if (binLid != null) lidClosed = binLid.localRotation;
        if (truckLights != null)
        {
            lightBase = new float[truckLights.Length];
            for (int i = 0; i < truckLights.Length; i++)
            {
                if (truckLights[i] == null) continue;
                lightBase[i] = truckLights[i].intensity;
                truckLights[i].enabled = false;
            }
        }
    }

    static void GroundAnimator(Animator a)
    {
        if (a == null || a.runtimeAnimatorController == null || !a.isActiveAndEnabled || !a.isInitialized) return;
        a.fireEvents = false; // the Starter Assets clips' OnLand/OnFootstep receivers were stripped
        a.SetBool(HGrounded, true);
        a.SetFloat(HMotion, 1f);
        a.SetFloat(HSpeed, 0f);
    }

    // ------------------------------------------------------------------ menu hooks

    /// <summary>The menu column covers this fraction of the screen width; the camera frames the set in the rest.</summary>
    public void SetUiInsetLeft(float fraction) => insetLeft = Mathf.Clamp(fraction, 0f, 0.6f);

    /// <summary>A menu button is hovered or focused (None = nothing).</summary>
    public void SetHover(MainMenuCoopUI.HoverKind k)
    {
        if (k == hover) return;
        hover = k;
        switch (k)
        {
            case MainMenuCoopUI.HoverKind.Join:
                flashT = 0f;
                break;
            case MainMenuCoopUI.HoverKind.Quit:
                quitYawnAt = Time.unscaledTime;
                if (chuck != null) chuck.DebugFidget(5); // StretchYawn: "Yes, I'm tired"
                break;
            case MainMenuCoopUI.HoverKind.Host:
                if (crew != null) foreach (var c in crew) if (c != null) c.gameObject.SetActive(true);
                break;
        }
    }

    /// <summary>Play / Continue / Host pressed: a honk and a hat pop as the menu leaves.</summary>
    public void OnPlayPressed()
    {
        Sfx.Play("horn", truck != null ? truck.position : (Vector3?)null, 0.8f, 1.05f);
        flashT = 0f;
        if (chuck != null) chuck.DebugFidget(3);
        if (truck != null) Juice.Squash(truck, 0.12f, 0.3f);
    }

    // ------------------------------------------------------------------ frame

    void Update()
    {
        if (!Application.isPlaying) return;
        float dt = Time.deltaTime, t = Time.time;
        if (chuckAnim != null) GroundAnimator(chuckAnim);
        UpdateCamera(t);
        UpdateTruck(t);
        UpdateBeats(dt);
        UpdateFlying(dt);
        UpdateReactions(dt, t);
        UpdateClicks();
    }

    /// <summary>Where the camera goes for a screen of <paramref name="aspect"/> with the menu covering <paramref name="inset"/>
    /// of the width (also used by edit-mode previews).</summary>
    public void ComputeCameraPose(float aspect, float inset, float swayYaw, float parallaxYaw, float parallaxPitch,
                                  out Vector3 pos, out Quaternion rot)
    {
        Vector3 f = focus != null ? focus.position : transform.position + Vector3.up;
        float vfov = camFov;
        float hfov = Camera.VerticalToHorizontalFieldOfView(vfov, Mathf.Max(0.5f, aspect));
        float tanH = Mathf.Tan(hfov * 0.5f * Mathf.Deg2Rad), tanV = Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);
        float region = Mathf.Clamp(1f - inset, 0.35f, 1f);
        float d = Mathf.Max(frame.x / (2f * tanH * region * 0.92f), frame.y / (2f * tanV * 0.9f));
        d = Mathf.Clamp(d, 5f, 40f);
        var orbit = Quaternion.Euler(camPitch + parallaxPitch, camYaw + swayYaw, 0f);
        pos = f - orbit * Vector3.forward * d;
        // turn the camera so the focus sits in the middle of the free space (right of the menu column)
        float vx = inset + region * 0.5f;
        float off = Mathf.Atan((2f * vx - 1f) * tanH) * Mathf.Rad2Deg;
        rot = orbit * Quaternion.Euler(0f, -off + parallaxYaw, 0f);
    }

    void UpdateCamera(float t)
    {
        if (cam == null) return;
        var menu = MainMenuCoopUI.Instance;
        if (menu != null) insetLeft = menu.DioramaInsetLeft;
        float sway = 1.5f * Mathf.Sin(t * Mathf.PI * 2f / 12f);
        Vector2 m = Vector2.zero;
        if (Screen.width > 0 && Screen.height > 0)
        {
            m = new Vector2(Input.mousePosition.x / Screen.width * 2f - 1f, Input.mousePosition.y / Screen.height * 2f - 1f);
            m = Vector2.ClampMagnitude(m, 1.5f);
        }
        ComputeCameraPose(cam.aspect, insetLeft, sway, m.x * 1f, -m.y * 0.6f, out var pos, out var rot);
        float k = camSnapped ? 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime) : 1f;
        camSnapped = true;
        cam.transform.position = Vector3.Lerp(cam.transform.position, pos, k);
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, rot, k);
    }

    void UpdateTruck(float t)
    {
        if (truck == null) return;
        // diesel idle: a fast shiver plus a lazy breath
        float shiver = Mathf.Sin(t * 47f) * 0.004f + Mathf.Sin(t * 31f) * 0.003f;
        truck.localPosition = truckHome + new Vector3(0f, shiver + Mathf.Sin(t * 1.3f) * 0.006f, 0f);
    }

    // ------------------------------------------------------------------ beats

    void UpdateBeats(float dt)
    {
        if (!inBeat)
        {
            gapLeft -= dt;
            if (gapLeft <= 0f) StartBeat(PickBeat());
            return;
        }
        beatT += dt;
        switch (beat)
        {
            case Beat.Chuck: TickChuckBeat(); break;
            case Beat.Raccoon: TickRaccoonBeat(); break;
            case Beat.Honk: TickHonkBeat(); break;
        }
        if (beatT >= beatLen) EndBeat();
    }

    /// <summary>Relative odds: calm fidgets most, the Chuck often, the raccoon now and then, a honk rarely (it's loud).</summary>
    static readonly int[] BeatWeight = { 40, 30, 18, 12 };
    /// <summary>A beat that hasn't played for this long goes next (every beat shows within ~3 minutes).</summary>
    public const float StarveSeconds = 70f;
    readonly float[] lastPlayedAt = new float[4];

    bool Available(Beat b) =>
        !(b == Beat.Raccoon && raccoon == null) && !(b == Beat.Chuck && (bags.Count == 0 || chuck == null));

    Beat PickBeat()
    {
        // never the same one twice running; a starved beat jumps the queue; else weighted random (cosmetic)
        float now = Time.time;
        for (int i = 0; i < 4; i++)
        {
            var b = (Beat)i;
            if (b != lastBeat && Available(b) && now - lastPlayedAt[i] > StarveSeconds) return b;
        }
        int total = 0;
        for (int i = 0; i < 4; i++) if ((Beat)i != lastBeat && Available((Beat)i)) total += BeatWeight[i];
        int roll = UnityEngine.Random.Range(0, Mathf.Max(1, total)); // coop:allow(random) cosmetic: menu diorama beat order
        for (int i = 0; i < 4; i++)
        {
            if ((Beat)i == lastBeat || !Available((Beat)i)) continue;
            roll -= BeatWeight[i];
            if (roll < 0) return (Beat)i;
        }
        return lastBeat == Beat.Idle ? Beat.Honk : Beat.Idle;
    }

    /// <summary>Start a beat now (tests and the click reactions).</summary>
    public void StartBeat(Beat b)
    {
        if (inBeat) EndBeat();
        beat = b;
        inBeat = true;
        beatT = 0f;
        BeatCounts[(int)b]++;
        lastPlayedAt[(int)b] = Time.time;
        Debug.Log($"[MenuDiorama] beat {b} (#{BeatCounts[(int)b]})");
        switch (b)
        {
            case Beat.Idle:
                beatLen = UnityEngine.Random.Range(6f, 10f); // coop:allow(random) cosmetic: idle beat length
                if (chuck != null) chuck.DebugFidget(new[] { 1, 2, 4, 6 }[UnityEngine.Random.Range(0, 4)]); // coop:allow(random) cosmetic: which fidget
                break;
            case Beat.Chuck:
                beatLen = 4f;
                bagIndex = PickBag();
                thrown = false;
                break;
            case Beat.Raccoon:
                beatLen = 3f;
                break;
            case Beat.Honk:
                beatLen = 1.5f;
                Sfx.Play("horn", truck != null ? truck.position : (Vector3?)null, 0.45f, 1f);
                flashT = 0f;
                if (chuck != null) chuck.DebugFidget(3); // HatPop
                if (truck != null) Juice.Punch(truck, 0.06f, 0.3f);
                break;
        }
    }

    void EndBeat()
    {
        if (beat == Beat.Chuck) ReleaseHold();
        if (beat == Beat.Raccoon) { SetRaccoon(0f); SetLid(0f); if (chuck != null) chuck.previewFlail = false; }
        if (chuck != null && hover != MainMenuCoopUI.HoverKind.Host) chuck.previewMode = false;
        lastBeat = beat;
        inBeat = false;
        gapLeft = beat == Beat.Idle ? 0.8f : UnityEngine.Random.Range(2f, 4f); // coop:allow(random) cosmetic: pause between beats
    }

    int PickBag()
    {
        for (int i = 0; i < bags.Count; i++)
        {
            int k = (bagIndex + 1 + i) % bags.Count;
            if (bags[k] != null && bags[k].gameObject.activeSelf) return k;
        }
        return -1;
    }

    Vector3 HoldPoint() => chuck.transform.TransformPoint(new Vector3(0f, 0.9f, 0.48f));

    void TickChuckBeat()
    {
        if (chuck == null || bagIndex < 0 || bagIndex >= bags.Count || bags[bagIndex] == null || thrown) return;
        var bag = bags[bagIndex];
        var spot = bagSpots[bagIndex];
        // the holder's origin is the sack's bottom: hold it so the sack's middle is at the hug point
        Vector3 hold = HoldPoint() - Vector3.up * 0.32f * bag.lossyScale.y;
        if (beatT < 0.4f)
        {
            // hup: the bag hops from the curb into a bear hug
            float p = beatT / 0.4f;
            bag.position = Vector3.Lerp(spot.position, hold, FeelEase.OutQuad(p)) + Vector3.up * Mathf.Sin(p * Mathf.PI) * 0.35f;
            if (p > 0.6f)
            {
                chuck.previewMode = true;
                chuck.previewHold = bagCenters[bagIndex] != null ? bagCenters[bagIndex] : bag;
                chuck.previewMass = 8f;
                chuck.previewHoldExtents = new Vector3(0.3f, 0.36f, 0.28f);
            }
        }
        else if (beatT < 1.3f)
        {
            // strain: red face, a tremble
            float p = (beatT - 0.4f) / 0.9f;
            chuck.previewMode = true;
            chuck.previewHold = bagCenters[bagIndex] != null ? bagCenters[bagIndex] : bag;
            chuck.previewCharge = Mathf.Clamp01(p * 1.15f);
            bag.position = hold + UnityEngine.Random.insideUnitSphere * 0.012f * p; // coop:allow(random) cosmetic: strain tremble
        }
        else
        {
            // HWAAH: let go and the bag arcs into the hopper
            thrown = true;
            ReleaseHold();
            Sfx.Play("throw", chuck.transform.position, 0.8f, 0.9f);
            Sfx.Play("whoosh", chuck.transform.position, 0.5f, 0.8f);
            flying = bag;
            flyFrom = bag.position;
            flyTo = hopperMouth != null ? hopperMouth.position - Vector3.up * 0.3f : truck.position + Vector3.up * 2f;
            flyT = 0f;
        }
    }

    void ReleaseHold()
    {
        if (chuck == null) return;
        chuck.previewHold = null;
        chuck.previewCharge = 0f;
        chuck.previewMode = false;
    }

    void UpdateFlying(float dt)
    {
        if (flying != null && flyT >= 0f)
        {
            flyT += dt / 0.55f;
            float p = Mathf.Clamp01(flyT);
            var pos = Vector3.Lerp(flyFrom, flyTo, p) + Vector3.up * Mathf.Sin(p * Mathf.PI) * 1.4f;
            flying.position = pos;
            flying.Rotate(new Vector3(260f, 120f, 80f) * dt, Space.Self);
            if (p >= 1f)
            {
                Sfx.Play("toss_in", flyTo, 0.8f, 1f);
                Sfx.Play("compactor", flyTo, 0.55f, 1.05f);
                if (truck != null) Juice.Squash(truck, 0.1f, 0.35f);
                flying.gameObject.SetActive(false);
                respawnBag = bags.IndexOf(flying);
                respawnAt = Time.time + 1.2f;
                flying = null;
                flyT = -1f;
            }
        }
        if (respawnBag >= 0 && Time.time >= respawnAt)
        {
            var b = bags[respawnBag];
            if (b != null)
            {
                b.localPosition = Vector3.zero;
                b.localRotation = Quaternion.identity;
                b.gameObject.SetActive(true);
                Juice.PopIn(b, 0.3f);
            }
            respawnBag = -1;
        }
    }

    void TickRaccoonBeat()
    {
        // lid pops (0–0.25 s), raccoon peeks (0.15–0.45 s), glints + hiss, ducks back (2.2–2.5 s), lid slams (2.5 s)
        float t = beatT;
        SetLid(t < 0.25f ? FeelEase.OutBack(t / 0.25f) : t < 2.45f ? 1f : 1f - Mathf.Clamp01((t - 2.45f) / 0.12f));
        SetRaccoon(t < 0.15f ? 0f : t < 0.45f ? FeelEase.OutBack((t - 0.15f) / 0.3f) : t < 2.2f ? 1f : 1f - Mathf.Clamp01((t - 2.2f) / 0.25f));
        if (Cross(t, 0.3f))
        {
            Sfx.Play("hiss", bin != null ? bin.position : (Vector3?)null, 0.7f, 1.1f);
            if (raccoon != null) raccoon.KickEyes(2f);
            if (chuck != null) { chuck.previewMode = true; chuck.previewFlail = true; }
        }
        if (Cross(t, 1.3f) && chuck != null) { chuck.previewFlail = false; chuck.previewMode = false; }
        if (Cross(t, 1.6f) && raccoon != null) raccoon.KickEyes(1f);
        if (Cross(t, 2.55f))
        {
            Sfx.Play("thud", bin != null ? bin.position : (Vector3?)null, 0.6f, 1.2f);
            if (bin != null) Juice.Squash(bin, 0.15f, 0.25f);
        }
    }

    bool Cross(float t, float mark) => t >= mark && t - Time.deltaTime < mark;

    void SetLid(float open01)
    {
        if (binLid == null) return;
        binLid.localRotation = lidClosed * Quaternion.Euler(80f * open01, 0f, 0f); // +X lifts the front edge (hinge at the back)
    }

    void SetRaccoon(float up01)
    {
        if (raccoonAnchor == null) return;
        raccoonAnchor.localPosition = Vector3.LerpUnclamped(raccoonDown, raccoonUp, up01);
    }

    void TickHonkBeat() { }

    // ------------------------------------------------------------------ reactions

    void UpdateReactions(float dt, float t)
    {
        // Play solo: Chuck turns to look at you
        if (chuck != null && cam != null)
        {
            lookW = Mathf.MoveTowards(lookW, hover == MainMenuCoopUI.HoverKind.PlaySolo && !(inBeat && beat == Beat.Chuck) ? 1f : 0f, dt * 4f);
            Vector3 toCam = cam.transform.position - chuck.transform.position;
            toCam.y = 0f;
            var look = toCam.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toCam.normalized, Vector3.up) : chuckHomeRot;
            chuck.transform.rotation = Quaternion.Slerp(chuckHomeRot, look, FeelEase.OutQuad(lookW));
        }

        // Host with friends: the crew slides onto the rear steps and waves (windmill arms in bursts)
        crewOut = Mathf.MoveTowards(crewOut, hover == MainMenuCoopUI.HoverKind.Host ? 1f : 0f, dt * 2.5f);
        if (crew != null && crewSpots != null)
            for (int i = 0; i < crew.Length && i < crewSpots.Length; i++)
            {
                var c = crew[i];
                if (c == null || crewSpots[i] == null) continue;
                float p = Mathf.Clamp01(crewOut * 1.4f - i * 0.2f);
                if (p <= 0f && crewOut <= 0f) { if (c.gameObject.activeSelf) c.gameObject.SetActive(false); continue; }
                if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                if (i < crewAnims.Count) GroundAnimator(crewAnims[i]);
                c.transform.position = Vector3.LerpUnclamped(crewHome[i], crewSpots[i].position, FeelEase.OutBack(p, 1.4f));
                bool wave = p >= 1f && Mathf.Repeat(t * 0.9f + i * 0.37f, 1f) < 0.55f;
                c.previewMode = true;
                c.previewFlail = wave;
            }

        // lights: two quick flashes (Join hover, honks, Play)
        if (flashT < 0.9f) flashT += dt;
        bool on = flashT < 0.9f && (flashT < 0.18f || (flashT > 0.34f && flashT < 0.52f));
        if (truckLights != null)
            for (int i = 0; i < truckLights.Length; i++)
            {
                var l = truckLights[i];
                if (l == null) continue;
                l.enabled = on;
                if (on) l.intensity = (lightBase != null && i < lightBase.Length ? lightBase[i] : 1f) * (l.type == LightType.Point ? 30f : 1f);
            }
        if (tailLights != null)
            foreach (var r in tailLights)
            {
                if (r == null) continue;
                r.GetPropertyBlock(mpb);
                Color c = on ? new Color(1f, 0.25f, 0.15f) : new Color(0.55f, 0.08f, 0.06f);
                mpb.SetColor("_BaseColor", c);
                mpb.SetColor("_EmissionColor", on ? c * 3f : Color.black);
                r.SetPropertyBlock(mpb);
            }
    }

    // ------------------------------------------------------------------ clicks

    void UpdateClicks()
    {
        if (!Input.GetMouseButtonDown(0) || cam == null) return;
        var es = EventSystem.current;
        if (es != null && es.IsPointerOverGameObject()) return;
        if (UiStack.AnyOpen || NetErrorPanel.IsShowing) return;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out var hit, 200f, ~0, QueryTriggerInteraction.Collide)) return;
        var c = hit.collider;
        if (c == chuckClick && chuck != null)
        {
            chuck.DebugFidget(2); // TummyPat: "hoo"
            Sfx.Play("oof", chuck.transform.position, 0.5f, 1.5f);
        }
        else if (c == truckClick)
        {
            Sfx.Play("horn", truck.position, 0.8f, 1.1f);
            flashT = 0f;
            Juice.Punch(truck, 0.05f, 0.25f);
        }
        else if (c == binClick)
        {
            if (raccoon != null && !(inBeat && beat == Beat.Raccoon)) StartBeat(Beat.Raccoon);
            else Sfx.Play("hiss", bin.position, 0.6f, 1.2f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Active = null;
        Array.Clear(BeatCounts, 0, BeatCounts.Length);
        StrippedBodies = 0;
        BuildSeconds = 0f;
    }
}

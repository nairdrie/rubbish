using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scrapazon truck upgrades, applied at runtime to a TruckControllerV2 from <see cref="UpgradeEffects"/>
/// (Docs/Progression.md §6.1). Flow code only gains the additive maxLateralAccel field; everything else is the
/// truck's existing public fields:
///   truck_turbo 0-3   -> ABSOLUTE engine tiers (UpgradeEffects.Engine): motorTorque, maxSpeed, steeringRangeAtMaxSpeed,
///                        brakeTorque (stock is now 1,000, not the prefab's 2,000), maxLateralAccel (L3: 7 m/s²),
///                        ContinuousSpeculative collision at L3. Visuals: L1 chrome stacks that spit flames, L2 a
///                        "Stinkmaster" blower scoop, L3 rear jet thrusters that roar at speed.
///   air_horn 0-2      -> L1 roof air horns + a louder HOOOONK; L2 the Foghorn of the Gods: 40 m startle (critters beyond
///                        the Critters system's own horn radius react here), trees within range shed leaves.
///   truck_headlights  -> range ×1.6 / intensity ×1.5; raccoons in the beam within 20 m (L2: 30 m + anything within 8 m
///                        of the truck) briefly flinch once per 10 s if calm; angry/foamy ones ignore it. L2 adds a roof light bar.
///   truck_capacity    -> duct-taped sideboards (one plank row per level 1-3); L4 the Hopper Mk II compaction ram that
///                        thumps every bag that goes in (the kg are GameSession's, via ShopEffects.ApplyTruckCapacity).
/// Also owns the driving hint line (other truck mods publish their prompt through <see cref="HintOverride"/>).
/// Added by <see cref="ShopEffectsRunner"/> to every truck it finds (scene load / truck spawn).
/// </summary>
[DisallowMultipleComponent]
public class ShopTruckMods : MonoBehaviour
{
    public const string VisualRootName = "Scrapazon Mods";
    public const string BaseDriveHint = "W/S drive · A/D steer · H honk";

    // Anchors in truck metres (the Truck V2 root is scaled ~0.67, so these ignore scale, like TruckControllerV2's
    // TruckPoint): +Z forward, ground ≈ y 0.4, cab z 2.55..3.8 with its roof at y ≈ 2.5, hopper body z -2.2..2.2 with
    // its top at y ≈ 3.05, body half-width ≈ 1.0.
    static readonly Vector3 HornAnchor = new Vector3(0f, 2.64f, 2.95f);
    static readonly Vector3[] StackTops = { new Vector3(-0.93f, 3.3f, 2.36f), new Vector3(0.93f, 3.3f, 2.36f) };
    static readonly Vector3[] JetMouths = { new Vector3(-1.08f, 2.55f, -2.62f), new Vector3(1.08f, 2.55f, -2.62f) };
    static readonly Vector3 LightBarAnchor = new Vector3(0f, 2.66f, 2.62f);
    static readonly Vector3 RamAnchor = new Vector3(0f, 2.36f, -2.3f);

    TruckControllerV2 truck;
    Rigidbody body;
    float baseTorque, baseMaxSpeed, baseSteerAtMax, baseBrake;
    CollisionDetectionMode baseCcd;
    int shownTurbo = -1, shownHorn = -1, shownCap = -1, shownLights = -1;
    Transform visuals, hornGroup, ramPlate, lightBar, scoop;
    Light barLight;
    float nextHornAt, nextTurboFxAt, nextFlameAt, nextJetAt, nextBeamScan, nextLightOwnerCheck;
    float lastThrottle;
    float lastLoadKg = -1f;
    float ramT = 1f;
    bool wasSeated;
    string hintShown;

    // headlights
    struct LightBase { public Light light; public float range, intensity; }
    readonly List<LightBase> headlights = new List<LightBase>();
    readonly Dictionary<Critter, float> beamScaredAt = new Dictionary<Critter, float>();

    public float BaseMotorTorque => baseTorque;
    public float BaseMaxSpeed => baseMaxSpeed;
    int Level(string id) => BusinessUpgradeNet.LevelForTruck(truck, id);

    /// <summary>A truck mod's prompt that replaces the driving hint while set (recovery "Hold R…", arm status). null = none.</summary>
    public string HintOverride { get; set; }

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        if (truck != null)
        {
            baseTorque = truck.motorTorque;
            baseMaxSpeed = truck.maxSpeed;
            baseSteerAtMax = truck.steeringRangeAtMaxSpeed;
            baseBrake = truck.brakeTorque;
        }
        if (body != null) baseCcd = body.collisionDetectionMode;
        FindHeadlights();
    }

    void OnEnable()
    {
        GameSession.UpgradeChanged += OnUpgradeChanged;
        BusinessUpgradeNet.Changed += Apply;
        Apply();
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        BusinessUpgradeNet.Changed -= Apply;
    }

    void OnDestroy()
    {
        // Removed from a live truck (not destroyed with it): hand back the stock (prefab) engine and lights.
        if (truck != null && truck.gameObject.activeInHierarchy)
        {
            truck.motorTorque = baseTorque;
            truck.maxSpeed = baseMaxSpeed;
            truck.steeringRangeAtMaxSpeed = baseSteerAtMax;
            truck.brakeTorque = baseBrake;
            truck.maxLateralAccel = float.PositiveInfinity;
            if (body != null && !body.isKinematic) body.collisionDetectionMode = baseCcd;
            foreach (var h in headlights)
                if (h.light != null) { h.light.range = h.range; h.light.intensity = h.intensity; }
        }
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.TruckTurbo || id == ShopIds.AirHorn || id == ShopIds.TruckCapacity || id == ShopIds.TruckHeadlights
            || id == ShopIds.GoldenTruck || id == ShopIds.TruckArm || id == ShopIds.TruckHandling)
            Apply();
        hintShown = null; // re-publish the hint (new keys to learn)
    }

    /// <summary>Re-apply engine numbers, lights + visuals from the current upgrade levels. Idempotent.</summary>
    public void Apply()
    {
        if (truck == null) return;
        int turbo = Mathf.Clamp(Level(ShopIds.TruckTurbo), 0, 3);
        int horn = Mathf.Clamp(Level(ShopIds.AirHorn), 0, 2);
        int cap = Mathf.Clamp(Level(ShopIds.TruckCapacity), 0, 4);
        int lights = BusinessUpgradeNet.HeadlightLevelForTruck(truck);

        // ---- engine (absolute tiers; golden_truck lifts top speed to the 18 m/s cap inside UpgradeEffects)
        var e = UpgradeEffects.EngineAtLevel(turbo, Level(ShopIds.GoldenTruck) > 0);
        truck.motorTorque = e.motorTorque;
        truck.maxSpeed = e.maxSpeed;
        truck.steeringRangeAtMaxSpeed = e.steerAtMaxSpeed;
        truck.brakeTorque = e.brakeTorque;
        truck.maxLateralAccel = e.maxLateralAccel;
        if (body != null)
        {
            var want = turbo >= 3 ? CollisionDetectionMode.ContinuousSpeculative : baseCcd;
            // Kinematic bodies (remote copies) only take discrete / speculative; both are fine here.
            if (body.collisionDetectionMode != want) body.collisionDetectionMode = want;
        }

        // ---- headlights
        float rangeMult = lights >= 1 ? 1.6f : 1f;
        float intensityMult = lights >= 1 ? 1.5f : 1f;
        foreach (var h in headlights)
        {
            if (h.light == null) continue;
            h.light.range = h.range * rangeMult;
            h.light.intensity = h.intensity * intensityMult;
        }

        if (turbo == shownTurbo && horn == shownHorn && cap == shownCap && lights == shownLights && visuals != null) return;
        bool fresh = shownTurbo >= 0; // not the first build: celebrate the new part
        shownTurbo = turbo; shownHorn = horn; shownCap = cap; shownLights = lights;
        visuals = BuildVisuals(transform, turbo, horn, cap, lights);
        hornGroup = visuals != null ? visuals.Find("Air Horns") : null;
        ramPlate = visuals != null ? FindDeep(visuals, "Ram Plate") : null;
        lightBar = visuals != null ? visuals.Find("Light Bar") : null;
        scoop = visuals != null ? FindDeep(visuals, "Blower") : null;
        barLight = lightBar != null ? lightBar.GetComponentInChildren<Light>(true) : null;
        if (fresh && visuals != null && Application.isPlaying)
        {
            Juice.PopIn(visuals, 0.35f);
            Fx.Sparkle(visuals, new Color(1f, 0.6f, 0.1f), 1.2f);
        }
    }

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    void FindHeadlights()
    {
        headlights.Clear();
        foreach (var l in GetComponentsInChildren<Light>(true))
        {
            if (l == null || l.type != LightType.Spot) continue;
            if (l.GetComponentInParent<RoofSpotlight>() != null) continue; // independently aimed/illuminated night light
            if (ShopModKit.ToTruckMetres(transform, l.transform.position).z < 1f) continue; // the front ones
            if (l.name == "Bar Beam") continue;                                             // our own light bar
            headlights.Add(new LightBase { light = l, range = l.range, intensity = l.intensity });
        }
    }

    // ---------------------------------------------------------------- per frame

    void Update()
    {
        if (truck == null) return;
        float now = Time.time;

        if (now >= nextLightOwnerCheck)
        {
            nextLightOwnerCheck = now + 0.25f;
            Apply(); // also resolves a truck whose lot was assigned after its spawn
        }

        bool driven = truck.HasDriver || truck.IsLocalPlayerSeated;
        // TruckController's enter/exit light calls run only on the driver; mirror the cab state for remote observers.
        if (truck.IsSpawned)
            foreach (var h in headlights) if (h.light != null) h.light.enabled = driven;
        UpdateRam();
        UpdateLightBar();

        // Host brains react to every driven truck, including a guest/employee in somebody else's cab.
        if (ShopModKit.IsHost && driven && shownLights >= 1 && now >= nextBeamScan)
        {
            nextBeamScan = now + 0.2f;
            BeamScare();
        }

        bool seated = truck.IsLocalPlayerSeated;
        if (seated != wasSeated)
        {
            wasSeated = seated;
            hintShown = null;
            if (!seated) HintOverride = null;
        }
        if (!seated) { lastThrottle = 0f; return; }
        PublishHint();
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning) { lastThrottle = 0f; return; }

        if (shownHorn > 0 && GameControls.Down(truck.hornKey, GameControls.Action.Horn) && now >= nextHornAt)
        {
            nextHornAt = now + 0.3f;
            if (shownHorn >= 2) Foghorn(); else AirHorn();
        }

        if (shownTurbo > 0)
        {
            float v = GameControls.DriveAxes().y;
            float speed = body != null ? body.linearVelocity.magnitude : 0f;
            if (v > 0.5f && lastThrottle <= 0.5f && speed < 3f && now >= nextTurboFxAt)
            {
                nextTurboFxAt = now + 2.5f;
                TurboKick();
            }
            else if (v > 0.5f && speed > truck.maxSpeed * 0.75f && now >= nextFlameAt)
            {
                nextFlameAt = now + 0.35f;
                foreach (var top in StackTops)
                    Fx.Burst(TruckPoint(top), new Color(1f, 0.55f, 0.1f), 3, 1.5f);
            }
            if (shownTurbo >= 3 && v > 0.5f && speed > 8f && now >= nextJetAt)
            {
                nextJetAt = now + 0.12f;
                foreach (var m in JetMouths)
                {
                    Vector3 p = TruckPoint(m);
                    Fx.Burst(p, new Color(0.45f, 0.75f, 1f), 3, 2.2f);
                    Fx.Burst(p, new Color(1f, 0.6f, 0.15f), 2, 1.6f);
                }
                if (speed > truck.maxSpeed * 0.92f && ShopModKit.Rand01() < 0.08f)
                    GameHUD.FloatingText(TruckPoint(new Vector3(0f, 4.2f, -2.4f)), ShopModKit.Pick(JetWords), new Color(0.5f, 0.85f, 1f), 1.1f);
            }
            if (shownTurbo >= 2 && scoop != null && v > 0.5f && lastThrottle <= 0.5f) Juice.Punch(scoop, 0.3f, 0.25f);
            lastThrottle = v;
        }
    }

    static readonly string[] JetWords = { "WHOOOSH", "THE GARBAGE IS WARM", "VWOOOM", "JET-ASSISTED!", "SPEED OF SMELL" };

    // ---------------------------------------------------------------- hint line

    float nextHintAt;

    void PublishHint()
    {
        if (hintShown != null && HintOverride == null && Time.unscaledTime < nextHintAt) return;
        nextHintAt = Time.unscaledTime + 0.25f;
        string want = HintOverride ?? DriveHint();
        if (want == hintShown) return;
        hintShown = want;
        GameHUD.SetContextHint(want);
    }

    /// <summary>Force the hint line to re-publish next frame (after a mod's prompt changed).</summary>
    public void RefreshHint() => hintShown = null;

    string DriveHint()
    {
        var sb = new System.Text.StringBuilder(BaseDriveHint);
        var tipper = GetComponent<TruckTipper>();
        string tip = tipper != null ? tipper.DriveHintChip() : null;
        if (tip != null) sb.Append(" · ").Append(tip);
        var arm = UpgradeEffects.ArmAtLevel(Level(ShopIds.TruckArm));
        if (arm.present)
        {
            var loader = GetComponent<TruckSideLoader>();
            if (arm.auto) sb.Append(loader != null && loader.AutoOn ? " · X arm AUTO: ON" : " · X arm AUTO: off");
            else sb.Append(" · hold X: Grabby Arm (stopped)");
        }
        if (UpgradeEffects.HandlingAtLevel(Level(ShopIds.TruckHandling)).drift) sb.Append(" · Space drift");
        int juice = GameSession.GetConsumable(ShopIds.GarbageJuice);
        if (juice > 0) sb.Append($" · J chug juice ({juice})");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- horn

    void AirHorn()
    {
        Vector3 at = TruckPoint(HornAnchor + new Vector3(0f, 0.15f, 0.6f));
        Sfx.Play("horn", at, 1f, ShopModKit.Range(0.6f, 0.72f));
        Sfx.Play("squeak", at, 0.45f, 0.55f);
        // Well above the stock truck's yellow "HONK!" (TruckControllerV2 puts it at roof + 0.7 m ≈ 4.0 m): the two
        // stack as a comic "HONK! / HOOOOONK!!" instead of printing over each other.
        GameHUD.FloatingText(TruckPoint(new Vector3(0f, 5.7f, 2.8f)),
            ShopModKit.Rand01() < 0.2f ? "HOOOOOOONK?!" : "HOOOOONK!!", new Color(1f, 0.55f, 0.2f), 1.9f);
        Fx.Burst(at, new Color(1f, 1f, 0.9f), 14, 6f);
        Juice.Shake(0.14f, 0.2f);
        if (hornGroup != null) Juice.Punch(hornGroup, 0.35f, 0.25f);
    }

    static readonly string[] FogWords = { "BWAAAAAAAMP!!!", "FOGHORN OF THE GODS!!", "BWOOOOOOOOM", "*the sky apologises*" };

    /// <summary>air_horn L2: louder, deeper, 40 m. Critters past the Critters system's own horn radius are scared here.</summary>
    void Foghorn()
    {
        Vector3 at = TruckPoint(HornAnchor + new Vector3(0f, 0.2f, 0.6f));
        Sfx.Play("horn", at, 1f, ShopModKit.Range(0.42f, 0.5f));
        Sfx.Play("horn", at, 0.8f, 0.31f);
        Sfx.Play("boom", at, 0.35f, 0.6f);
        GameHUD.FloatingText(TruckPoint(new Vector3(0f, 6.1f, 2.8f)), ShopModKit.Pick(FogWords), new Color(1f, 0.45f, 0.15f), 2.3f);
        Fx.Burst(at, new Color(1f, 1f, 0.9f), 22, 9f);
        Juice.Shake(0.26f, 0.35f);
        if (hornGroup != null) Juice.Punch(hornGroup, 0.5f, 0.35f);

        float radius = Level(ShopIds.AirHorn) >= 2 ? 40f : Level(ShopIds.AirHorn) >= 1 ? 25f : 12f;
        Vector3 pos = transform.position;
        if (ShopModKit.IsHost)
        {
            float handled = CritterSpawner.CurrentHornRadius; // the Critters system already scatters everything inside this
            int scared = 0;
            tmpCritters.Clear();
            tmpCritters.AddRange(Critter.All);
            foreach (var c in tmpCritters)
            {
                if (c == null || c.Despawning) continue;
                Vector3 d = c.transform.position - pos; d.y = 0f;
                float dist = d.magnitude;
                if (dist <= handled || dist > radius) continue;
                c.Scare(pos, true);
                scared++;
            }
            if (scared > 0)
                GameHUD.FloatingText(pos + Vector3.up * 7f, scared > 1 ? $"{scared} critters, {radius:0} m away, flee" : "a distant critter flees", new Color(0.7f, 1f, 0.5f), 1.1f);
        }
        ShakeTrees(pos, radius);
    }

    static readonly List<Critter> tmpCritters = new List<Critter>();
    static readonly Collider[] treeBuffer = new Collider[96];
    static readonly List<Collider> treeHits = new List<Collider>();

    /// <summary>Cosmetic: trees/hedges within range shed a burst of leaves (they're static-batched, so no transform wobble).</summary>
    static void ShakeTrees(Vector3 pos, float radius)
    {
        int n = Physics.OverlapSphereNonAlloc(pos, radius, treeBuffer, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        treeHits.Clear();
        for (int i = 0; i < n; i++)
        {
            var c = treeBuffer[i];
            if (c == null) continue;
            string nm = c.gameObject.name.ToLowerInvariant();
            if (nm.Contains("tree") || nm.Contains("hedge") || nm.Contains("bush")) treeHits.Add(c);
        }
        treeHits.Sort((a, b) => (a.transform.position - pos).sqrMagnitude.CompareTo((b.transform.position - pos).sqrMagnitude));
        int shown = Mathf.Min(14, treeHits.Count);
        for (int i = 0; i < shown; i++)
        {
            var b = treeHits[i].bounds;
            Vector3 top = new Vector3(b.center.x, b.max.y + 0.5f, b.center.z);
            Fx.Burst(top, new Color(0.35f, 0.75f, 0.25f), 10, 3f);
            Fx.Burst(top, new Color(0.6f, 0.85f, 0.3f), 5, 2f);
            if (i < 3) Sfx.Play("rustle", top, 0.5f, ShopModKit.Range(0.7f, 0.9f));
        }
    }

    // ---------------------------------------------------------------- headlights

    void UpdateLightBar()
    {
        if (barLight == null) return;
        bool on = false;
        foreach (var h in headlights) if (h.light != null && h.light.enabled) { on = true; break; }
        if (barLight.enabled != on) barLight.enabled = on;
    }

    /// <summary>Upgraded beams briefly flinch calm raccoons; angry/foamy ones ignore them. Never a flee/despawn.</summary>
    void BeamScare()
    {
        if (!ShopModKit.IsHost) return;
        bool lit = false;
        foreach (var h in headlights) if (h.light != null && h.light.enabled) { lit = true; break; }
        if (!lit) return;
        int ownerLights = BusinessUpgradeNet.HeadlightLevelForTruck(truck);
        float beam = UpgradeEffects.BeamScareRadiusAtLevel(ownerLights);
        bool bubble = ownerLights >= 2;
        if (beam <= 0f) return;
        Vector3 nose = TruckPoint(new Vector3(0f, 1f, ShopModKit.BodyNoseZ));
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();
        float now = Time.time;
        foreach (var c in Critter.All)
        {
            if (c == null || c.Despawning || c is PileGoblin) continue; // light doesn't scare goblins (they just squint)
            Vector3 d = c.transform.position - nose; d.y = 0f;
            float dist = d.magnitude;
            bool inBeam = dist <= beam && dist > 0.01f && Vector3.Angle(fwd, d) <= 32f;
            bool close = bubble && (c.transform.position - transform.position).sqrMagnitude <= 8f * 8f + 9f;
            if (!inBeam && !close) continue;
            if (beamScaredAt.TryGetValue(c, out float t) && now - t < 10f) continue;
            beamScaredAt[c] = now;
            if (c is Raccoon r) { if (!r.Dazzle(transform.position)) continue; }
            else c.Scare(transform.position, false);
            Vector3 head = c.transform.position + Vector3.up * 0.9f;
            GameHUD.FloatingText(head, ShopModKit.Pick(BeamWords), new Color(1f, 0.95f, 0.6f), 0.9f);
            Fx.Burst(head, new Color(1f, 1f, 0.75f), 6, 2f);
        }
        if (beamScaredAt.Count > 64) beamScaredAt.Clear();
    }

    static readonly string[] BeamWords = { "*squints*", "MY EYES!", "*hiss* too bright", "rude headlights", "I'm STILL HERE" };

    // ---------------------------------------------------------------- capacity L4 ram

    void UpdateRam()
    {
        float kg = GameSession.TruckLoadKg;
        if (lastLoadKg >= 0f && kg > lastLoadKg + 0.05f && ramPlate != null)
        {
            ramT = 0f;
            Vector3 p = ramPlate.position;
            Sfx.Play("slam", p, 0.45f, 1.35f);
            Sfx.Play("crunch", p, 0.4f, 0.8f);
            Fx.Dust(p, 0.5f);
        }
        lastLoadKg = kg;
        if (ramPlate == null) return;
        if (ramT < 1f)
        {
            ramT = Mathf.Min(1f, ramT + Time.deltaTime / 0.45f);
            // down hard, hold, ease back up
            float k = ramT < 0.3f ? Mathf.SmoothStep(0f, 1f, ramT / 0.3f) : ramT < 0.45f ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (ramT - 0.45f) / 0.55f);
            ramPlate.localPosition = new Vector3(0f, -0.34f * k, 0.18f * k);
        }
    }

    // ---------------------------------------------------------------- turbo juice

    void TurboKick()
    {
        foreach (var top in StackTops)
        {
            Vector3 p = TruckPoint(top);
            Fx.Burst(p, new Color(1f, 0.5f, 0.08f), 10, 2.8f);
            Fx.Dust(p + Vector3.up * 0.2f, 0.6f);
        }
        Sfx.Play("pop", transform.position, 0.9f, 0.7f);
        Sfx.Play("whoosh", transform.position, 0.8f, 0.6f);
        string word = shownTurbo >= 3 ? "JET ASSIST!" : shownTurbo == 2 ? "STINKMASTER V8!" : "TURBO!";
        GameHUD.FloatingText(TruckPoint(new Vector3(0f, 4.0f, 1.8f)), word, new Color(1f, 0.45f, 0.1f), 1.4f);
        Juice.Shake(0.07f + 0.03f * shownTurbo, 0.15f);
    }

    /// <summary>Truck-metre offset → world (ignores the prefab's root scale, like TruckControllerV2.TruckPoint).</summary>
    Vector3 TruckPoint(Vector3 metres) => transform.position + transform.rotation * metres;

    // ---------------------------------------------------------------- visuals

    /// <summary>Legacy overload (turbo/horn as on/off).</summary>
    public static Transform BuildVisuals(Transform truck, bool turbo, bool horn, int capacityLevel) =>
        BuildVisuals(truck, turbo ? 1 : 0, horn ? 1 : 0, capacityLevel, 0);

    /// <summary>
    /// (Re)build the renderer-only upgrade props under truck/"Scrapazon Mods" (no colliders: the truck's physics
    /// are untouched). Edit-mode safe (used by previews). Returns null when nothing is owned.
    /// </summary>
    public static Transform BuildVisuals(Transform truck, int turbo, int horn, int capacityLevel, int lights)
    {
        if (truck == null) return null;
        if (turbo <= 0 && horn <= 0 && capacityLevel <= 0 && lights < 2)
        {
            var old = truck.Find(VisualRootName);
            if (old != null)
            {
                old.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(old.gameObject); else DestroyImmediate(old.gameObject);
            }
            return null;
        }

        // Work in metres: the root undoes the truck root's scale so the anchors above are true distances.
        var root = ShopModKit.MetreRoot(truck, VisualRootName);

        var chrome = ShopMaterials.Get("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
        var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var red = ShopMaterials.Get("mat/shop_red", new Color(0.9f, 0.18f, 0.15f), 0.5f);
        var wood = ShopMaterials.Get("mat/shop_wood", new Color(0.66f, 0.46f, 0.26f), 0.2f);
        var tape = ShopMaterials.Get("mat/shop_tape", new Color(0.72f, 0.74f, 0.78f), 0.55f, 0.3f);
        var brass = ShopMaterials.Get("mat/shop_brass", new Color(0.86f, 0.66f, 0.25f), 0.8f, 0.85f);
        var yellow = ShopMaterials.Get("mat/shop_hazard_yellow", new Color(1f, 0.78f, 0.1f), 0.45f);

        if (horn > 0)
        {
            var g = new GameObject("Air Horns").transform;
            g.SetParent(root, false);
            g.localPosition = HornAnchor;
            var baseBlock = ShopMaterials.Primitive(PrimitiveType.Cube, "Mount", g, red);
            baseBlock.localScale = new Vector3(0.8f, 0.1f, 0.26f);
            for (int s = -1; s <= 1; s += 2)
            {
                float len = s < 0 ? 0.7f : 0.55f;
                var tube = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Tube", g, chrome);
                tube.localRotation = Quaternion.Euler(90f, 0f, 0f);
                tube.localPosition = new Vector3(s * 0.24f, 0.13f, len * 0.5f - 0.1f);
                tube.localScale = new Vector3(0.08f, len * 0.5f, 0.08f);
                var bell = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Bell", g, chrome);
                bell.localRotation = Quaternion.Euler(90f, 0f, 0f);
                bell.localPosition = new Vector3(s * 0.24f, 0.13f, len - 0.08f);
                bell.localScale = new Vector3(0.24f, 0.05f, 0.24f);
                var mouth = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Mouth", g, black);
                mouth.localRotation = Quaternion.Euler(90f, 0f, 0f);
                mouth.localPosition = new Vector3(s * 0.24f, 0.13f, len - 0.03f);
                mouth.localScale = new Vector3(0.17f, 0.012f, 0.17f);
            }
            if (horn >= 2)
            {
                // Foghorn of the Gods: one absurd brass trumpet in the middle, bell bigger than your head.
                var fog = new GameObject("Foghorn").transform;
                fog.SetParent(g, false);
                fog.localPosition = new Vector3(0f, 0.28f, -0.05f);
                ShopModKit.Prim(PrimitiveType.Cylinder, "Throat", fog, brass, new Vector3(0f, 0f, 0.35f), new Vector3(0.12f, 0.45f, 0.12f), new Vector3(90f, 0f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Flare", fog, brass, new Vector3(0f, 0f, 0.86f), new Vector3(0.3f, 0.08f, 0.3f), new Vector3(90f, 0f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Bell", fog, brass, new Vector3(0f, 0f, 0.98f), new Vector3(0.52f, 0.05f, 0.52f), new Vector3(90f, 0f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Mouth", fog, black, new Vector3(0f, 0f, 1.01f), new Vector3(0.4f, 0.01f, 0.4f), new Vector3(90f, 0f, 0f));
                ShopModKit.Prim(PrimitiveType.Sphere, "Bulb", fog, red, new Vector3(0f, 0.02f, -0.12f), new Vector3(0.26f, 0.22f, 0.3f));
            }
        }

        if (turbo > 0)
        {
            var g = new GameObject("Turbo Stacks").transform;
            g.SetParent(root, false);
            float fat = turbo >= 2 ? 1.25f : 1f;
            foreach (var top in StackTops)
            {
                var pipe = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Stack", g, chrome);
                pipe.localPosition = top + Vector3.down * 0.65f;
                pipe.localScale = new Vector3(0.2f * fat, 0.65f, 0.2f * fat);
                var cap = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Cap", g, black);
                cap.localPosition = top + Vector3.up * 0.02f;
                cap.localRotation = Quaternion.Euler(0f, 0f, 20f * Mathf.Sign(top.x));
                cap.localScale = new Vector3(0.24f * fat, 0.03f, 0.24f * fat);
                var band = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Band", g, red);
                band.localPosition = top + Vector3.down * 0.35f;
                band.localScale = new Vector3(0.23f * fat, 0.05f, 0.23f * fat);
            }
            if (turbo >= 2)
            {
                // "Stinkmaster" V8 blower: a chrome supercharger poking out of the cab roof, with a red intake.
                var blower = new GameObject("Blower").transform;
                blower.SetParent(g, false);
                blower.localPosition = new Vector3(0f, 2.58f, 3.45f);
                ShopModKit.Prim(PrimitiveType.Cube, "Case", blower, chrome, new Vector3(0f, 0.12f, 0f), new Vector3(0.55f, 0.24f, 0.4f));
                ShopModKit.Prim(PrimitiveType.Cube, "Scoop", blower, black, new Vector3(0f, 0.3f, 0.02f), new Vector3(0.4f, 0.14f, 0.32f));
                ShopModKit.Prim(PrimitiveType.Cube, "Intake", blower, red, new Vector3(0f, 0.31f, 0.18f), new Vector3(0.34f, 0.1f, 0.02f));
                for (int i = 0; i < 4; i++)
                    ShopModKit.Prim(PrimitiveType.Cube, "Rib", blower, black, new Vector3(-0.21f + i * 0.14f, 0.12f, 0.205f), new Vector3(0.03f, 0.2f, 0.01f));
            }
            if (turbo >= 3)
            {
                // Jet-Assisted Hopper Thrusters: rear-mounted nozzles beside the hopper mouth, glowing blue.
                var glow = ShopModKit.Emissive("mat/shop_jet_glow", new Color(0.35f, 0.7f, 1f), 3f);
                foreach (var m in JetMouths)
                {
                    var jet = new GameObject("Jet").transform;
                    jet.SetParent(g, false);
                    jet.localPosition = m;
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Nacelle", jet, chrome, new Vector3(0f, 0f, 0.4f), new Vector3(0.36f, 0.4f, 0.36f), new Vector3(90f, 0f, 0f));
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Ring", jet, red, new Vector3(0f, 0f, 0.62f), new Vector3(0.39f, 0.04f, 0.39f), new Vector3(90f, 0f, 0f));
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Nozzle", jet, black, new Vector3(0f, 0f, 0.02f), new Vector3(0.3f, 0.06f, 0.3f), new Vector3(90f, 0f, 0f));
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Afterglow", jet, glow, new Vector3(0f, 0f, -0.02f), new Vector3(0.22f, 0.02f, 0.22f), new Vector3(90f, 0f, 0f));
                }
            }
        }

        if (capacityLevel > 0)
        {
            var g = new GameObject("Sideboards").transform;
            g.SetParent(root, false);
            int rows = Mathf.Clamp(capacityLevel, 1, 3);
            const float bodyTop = 3.04f, halfW = 0.97f, zMid = -0.45f, len = 3.1f, plankH = 0.18f;
            for (int r = 0; r < rows; r++)
            {
                float y = bodyTop + plankH * 0.5f + r * (plankH + 0.03f);
                for (int s = -1; s <= 1; s += 2)
                {
                    var plank = ShopMaterials.Primitive(PrimitiveType.Cube, "Plank", g, wood);
                    plank.localPosition = new Vector3(s * halfW, y, zMid);
                    plank.localRotation = Quaternion.Euler(0f, 0f, s * (r % 2 == 0 ? 1.5f : -1f)); // a bit wonky
                    plank.localScale = new Vector3(0.07f, plankH, len - r * 0.15f);
                }
                var back = ShopMaterials.Primitive(PrimitiveType.Cube, "Plank Front", g, wood);
                back.localPosition = new Vector3(0f, y, zMid + len * 0.5f - 0.05f);
                back.localScale = new Vector3(halfW * 2f, plankH, 0.07f);
            }
            // duct tape wraps holding it all together
            float tapeH = rows * (plankH + 0.03f) + 0.06f;
            foreach (float z in new[] { zMid - 1.2f, zMid + 0.05f, zMid + 1.3f })
                for (int s = -1; s <= 1; s += 2)
                {
                    var t = ShopMaterials.Primitive(PrimitiveType.Cube, "Duct Tape", g, tape);
                    t.localPosition = new Vector3(s * (halfW + 0.01f), bodyTop + tapeH * 0.5f - 0.03f, z);
                    t.localRotation = Quaternion.Euler(0f, 0f, s * 4f);
                    t.localScale = new Vector3(0.1f, tapeH, 0.16f);
                }

            if (capacityLevel >= 4)
            {
                // Hopper Mk II: a packer blade over the rear mouth on two hydraulic rams. It thumps every bag in.
                var ram = new GameObject("Compaction Ram").transform;
                ram.SetParent(root, false);
                ram.localPosition = RamAnchor;
                var housing = ShopModKit.Prim(PrimitiveType.Cube, "Housing", ram, yellow, new Vector3(0f, 0.36f, 0.12f), new Vector3(1.5f, 0.2f, 0.34f));
                for (int i = 0; i < 6; i++)
                    ShopModKit.Prim(PrimitiveType.Cube, "Stripe", housing, black, new Vector3(-0.42f + i * 0.17f, 0f, -0.51f), new Vector3(0.05f, 1.02f, 0.02f), new Vector3(0f, 0f, 35f));
                for (int s = -1; s <= 1; s += 2)
                {
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Ram Cylinder", ram, chrome, new Vector3(s * 0.55f, 0.66f, 0.95f), new Vector3(0.14f, 0.5f, 0.14f), new Vector3(90f, 0f, 0f));
                    ShopModKit.Prim(PrimitiveType.Cylinder, "Ram Rod", ram, black, new Vector3(s * 0.55f, 0.66f, 0.3f), new Vector3(0.07f, 0.3f, 0.07f), new Vector3(90f, 0f, 0f));
                }
                var plateRoot = ShopModKit.Pivot("Ram Plate", ram, Vector3.zero);
                ShopModKit.Prim(PrimitiveType.Cube, "Blade", plateRoot, chrome, new Vector3(0f, 0.05f, 0f), new Vector3(1.3f, 0.36f, 0.06f));
                ShopModKit.Prim(PrimitiveType.Cube, "Lip", plateRoot, red, new Vector3(0f, -0.14f, -0.03f), new Vector3(1.32f, 0.05f, 0.08f));
            }
        }

        if (lights >= 2)
        {
            // Stadium Light Bar: six pods on a black bar across the cab roof, plus one wide real light.
            var bar = new GameObject("Light Bar").transform;
            bar.SetParent(root, false);
            bar.localPosition = LightBarAnchor;
            var lens = ShopModKit.Emissive("mat/shop_lightbar_lens", new Color(1f, 0.97f, 0.85f), 4f);
            ShopModKit.Prim(PrimitiveType.Cube, "Bar", bar, black, Vector3.zero, new Vector3(1.85f, 0.1f, 0.12f));
            for (int s = -1; s <= 1; s += 2)
                ShopModKit.Prim(PrimitiveType.Cube, "Leg", bar, black, new Vector3(s * 0.8f, -0.1f, 0f), new Vector3(0.05f, 0.2f, 0.05f));
            for (int i = 0; i < 6; i++)
            {
                float x = -0.75f + i * 0.3f;
                ShopModKit.Prim(PrimitiveType.Cube, "Pod", bar, black, new Vector3(x, 0.1f, 0.02f), new Vector3(0.22f, 0.16f, 0.14f));
                ShopModKit.Prim(PrimitiveType.Cube, "Lens", bar, lens, new Vector3(x, 0.1f, 0.095f), new Vector3(0.18f, 0.12f, 0.01f));
            }
            var beamGo = new GameObject("Bar Beam");
            beamGo.transform.SetParent(bar, false);
            beamGo.transform.localPosition = new Vector3(0f, 0.1f, 0.2f);
            beamGo.transform.localRotation = Quaternion.Euler(9f, 0f, 0f);
            var l = beamGo.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = new Color(1f, 0.97f, 0.88f);
            l.range = 34f;
            l.spotAngle = 95f;
            l.innerSpotAngle = 60f;
            l.intensity = 60f;
            l.shadows = LightShadows.None;
            l.enabled = false;
        }
        return root;
    }
}

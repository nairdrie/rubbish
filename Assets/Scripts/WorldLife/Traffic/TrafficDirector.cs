using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package D (Docs/WorldLife.md §3.2, §4.2, §7.3, §8.3): household cars leaving home and coming back.
///
/// <para><b>Data</b>: each car's trips are today's timetable (<see cref="LifeSim"/>): reverse out of the driveway, drive the
/// right-hand lanes to a town-exit tunnel, come back later and park nose-in. Where a car IS comes from the canonical
/// motion profile at (now − host delay), identical on every machine.</para>
///
/// <para><b>Presentation</b> (local, may differ by a few metres between machines and re-converges): up to 16 moving cars
/// nearest the camera are shown as pooled <see cref="CarAgent"/>s (≤ 160 m; full LOD ≤ 35 m with springs, driver and
/// lights; 15 Hz beyond 90 m). A shown car keeps ≥ 6 m behind the car ahead, slows to 3 m/s into an intersection and
/// waits while another car is in it, stops for people in the road, brakes 8 m short of a truck and stops 3 m short,
/// honks after 3 s and overtakes in the other lane when it's clear for 30 m. A blocked car falls behind its timetable and
/// catches up at up to 1.4× speed (never ahead of it). Parked cars stay the builder's static-batched objects; a car
/// that leaves hides its static twin and drives a pooled copy.</para>
///
/// <para><b>Rules</b> (§8.3): shown cars and parked cars are <see cref="ILifeBody"/>s; package E's sensor decides shunts
/// and the horn, and this director presents them (the cartoon shunt, pulling over, honking back). NPC cars boop a player
/// on foot who steps in front of them at the last moment (0.4 s stumble, no bag drop, 5 s immunity; detected on that
/// player's own machine). Nothing here pays or hurts. Budget: WorldLife.Cars ≤ 0.20 ms, 0 B per frame.</para>
/// </summary>
public sealed class TrafficDirector : WorldLifeDirector, ITruckSensorClient
{
    public static TrafficDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Traffic;
    public override string Name => "TrafficDirector";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Instance = new TrafficDirector();
        WorldLifeRunner.Register(Instance);
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null; players.Clear(); DriverPoppedOut = null; ParkedCarShunted = null; CarHonked = null;
        boopImmuneUntil = 0f; InterimSensor = true;
    }

    /// <summary>C: the driver of a shunted car pops out (show a full puppet there for ~3 s: yell, fist, back in).
    /// Without a listener the driver's lines float over the car.</summary>
    public static event Action<CarAgent, LifeId, Vector3> DriverPoppedOut;
    /// <summary>C: a PARKED car was shunted: (car, owner, stopId, car position): the owner storms out of the front door.</summary>
    public static event Action<LifeId, LifeId, string, Vector3> ParkedCarShunted;
    /// <summary>A car honked (position, why: 0 truck, 1 person, 2 honk-back).</summary>
    public static event Action<CarAgent, int> CarHonked;

    /// <summary>Until package E sets the real authority, claim truck-vs-car overlaps here (like C's interim sensor for
    /// people) so a car hit at speed shunts instead of being a wall. Switches itself off once E's authority is in.</summary>
    public static bool InterimSensor = true;

    static readonly List<PlayerController> players = new List<PlayerController>(8);
    static void OnPlayerSpawned(PlayerController p) { if (p != null && !players.Contains(p)) players.Add(p); }
    static float boopImmuneUntil;

    // ------------------------------------------------------------------ tuning (presentation)

    public const float FollowStopGap = 5f, FollowTimeGap = 0.2f, TruckStopGap = WorldLifeTuning.Traffic.StopShort,
        PersonStopGap = 2.5f, BrakeDecel = 3.5f, EmergencyDecel = 7f, LaneWidth = 5f, PullOverOffset = 1.4f,
        OvertakeRamp = 11f, OvertakePastTruck = 6f, BoxConflictDistance = 3f, SnapLagSeconds = 25f, HideBeyond = 170f;
    public const int Shells = WorldLifeTuning.Lod.MaxMovingCars, ExtraParkedShells = 4;
    /// <summary>A stuck car honks this long after stopping (§7.3 "after 3 s"; the acceptance asks for "within 3 s").</summary>
    public const float HonkAfterStop = 2.6f;
    /// <summary>Fixer pass. Courtesy: after this long stuck behind a truck a car asks the oncoming lane to hold back beyond its
    /// overtake corridor (CourtesyHoldBeyond m past the merge), so a rush-hour stream can't keep it honking for 20 s; a car
    /// that has already held for CourtesyMaxHold s ignores new asks (the lanes take turns).</summary>
    public const float CourtesyAfter = 5f, CourtesyHoldBeyond = 3f, CourtesyMaxHold = 12f;
    /// <summary>Fixer pass. The overtake swings out only as far as it must to clear the truck by OvertakeSideClear m (at least
    /// OvertakeMinLateral, at most a lane), over gap + 3.5 m (≥ 5.5 m): ~35° of yaw from 3 m behind a truck at the kerb
    /// instead of the ~55° full-lane swerve. The planned line is checked against the truck's box before it starts.</summary>
    public const float OvertakeSideClear = 0.6f, OvertakeMinLateral = 2.4f;
    /// <summary>A car that bats a fallen prop aside loses this fraction of its speed per kg (2–30 %).</summary>
    public const float DebrisSpeedLossPerKg = 0.004f;

    // ------------------------------------------------------------------ state

    WorldLifeContext ctx;
    LifeSim sim;
    LifeDay day;
    int dayIndex = int.MinValue, dayMask;
    int overridesVersion = -1;

    CarPhase[] phase = new CarPhase[0];
    int[] tripOf = new int[0];
    double[] nextChange = new double[0];
    int[] delayMs = new int[0];
    CarAgent[] agentOf = new CarAgent[0];
    HouseholdCar[] hcOf = new HouseholdCar[0];
    bool[] staticHidden = new bool[0];
    ParkedCarBody[] parked = new ParkedCarBody[0];
    float[] parkedFarSince = new float[0];
    int householdCarsSeen = -1;

    readonly List<CarAgent> agents = new List<CarAgent>(Shells + ExtraParkedShells);
    readonly Stack<CarAgent> free = new Stack<CarAgent>(Shells + ExtraParkedShells);
    int shellsMade;
    readonly List<int> movingCars = new List<int>(64);
    readonly List<int> candidates = new List<int>(64);
    readonly List<float> candDist = new List<float>(64);
    readonly List<ILifeBody> bodyScratch = new List<ILifeBody>(32);

    // Debug / test cars (not in the roster).
    sealed class DebugCar { public CarAgent agent; public double start; public CarPath path; }
    readonly List<DebugCar> debugCars = new List<DebugCar>(4);
    int debugSerial;

    // Stats (tests, profiler report).
    public int ShownMoving { get; private set; }
    public int MovingNow { get; private set; }
    public int Honks { get; private set; }
    public int Overtakes { get; private set; }
    public int Boops { get; private set; }
    public int Shunts { get; private set; }
    public int MaxShown { get; private set; }
    public IReadOnlyList<CarAgent> Agents => agents;
    public LifeSim Sim => sim;

    // ------------------------------------------------------------------ wake / sleep

    public override void Wake(WorldLifeContext c)
    {
        ctx = c;
        sim = LifeSim.Active;
        int n = sim != null ? sim.Roster.CarCount : 0;
        phase = new CarPhase[n]; tripOf = new int[n]; nextChange = new double[n]; delayMs = new int[n];
        agentOf = new CarAgent[n]; hcOf = new HouseholdCar[n]; staticHidden = new bool[n];
        parked = new ParkedCarBody[n]; parkedFarSince = new float[n];
        for (int i = 0; i < n; i++) { tripOf[i] = -1; nextChange[i] = double.MinValue; }
        agents.Clear(); free.Clear(); shellsMade = 0; debugCars.Clear();
        householdCarsSeen = -1;
        dayIndex = int.MinValue; overridesVersion = -1;
        Honks = Overtakes = Boops = Shunts = MaxShown = 0;
        WorldLifeRunner.AddSensorClient(this);
    }

    public override void Sleep()
    {
        WorldLifeRunner.RemoveSensorClient(this);
        for (int i = 0; i < hcOf.Length; i++)
        {
            ShowStatic(i, true);
            if (parked[i] != null && parked[i].registered) { LifeBodies.Unregister(parked[i]); parked[i].registered = false; }
        }
        for (int i = 0; i < agents.Count; i++) LifeBodies.Unregister(agents[i]);
        agents.Clear(); free.Clear(); debugCars.Clear();
        RestoreParkedLights();   // a destroyed renderer (the scene is leaving) compares equal to null and is skipped
        sim = null; day = null; ctx = null;
    }

    // ------------------------------------------------------------------ tick

    public override void Tick(WorldLifeContext c)
    {
        ctx = c;
        if (sim == null) { sim = LifeSim.Active; if (sim == null) return; Wake(c); }
        if (sim != LifeSim.Active) { Sleep(); sim = LifeSim.Active; if (sim == null) return; Wake(c); }
        EnsureDay();
        MapHouseholdCars();
        RefreshDelays();

        double t = day.MsOf(c.Hours);
        float now = Time.time;
        float dt = Mathf.Min(c.DeltaTime, 0.1f);
        Vector3 cam = c.Viewer;

        // 1. Phases (cheap: one compare per car unless something changed).
        movingCars.Clear();
        int n = phase.Length;
        for (int i = 0; i < n; i++)
        {
            double te = t - delayMs[i];
            if (te >= nextChange[i])
            {
                var ph = day.CarAt(i, te, out int trip, out _, out _);
                phase[i] = ph; tripOf[i] = trip;
                nextChange[i] = day.CarNextChange(i, te);
            }
            if (phase[i] == CarPhase.Out || phase[i] == CarPhase.In) movingCars.Add(i);
        }
        MovingNow = movingCars.Count;

        // 2. Candidates to show: moving cars without a shell, nearest first, ≤ 160 m.
        candidates.Clear(); candDist.Clear();
        float maxD = WorldLifeTuning.Lod.MovingCarDistance;
        for (int k = 0; k < movingCars.Count; k++)
        {
            int ci = movingCars[k];
            if (agentOf[ci] != null) continue;
            if (!DensityKeeps(ci)) continue;
            var tr = day.trips[tripOf[ci]];
            bool outbound = phase[ci] == CarPhase.Out;
            var path = outbound ? tr.outPath : tr.inPath;
            double legT = (t - delayMs[ci] - (outbound ? tr.depart : tr.ret)) * 0.001;
            float arc = path.ArcAtTime((float)legT, out _, out _);
            Vector3 p = path.PointAt(arc);
            float d = FlatDist(p, cam);
            if (d > maxD) continue;
            int at = candDist.Count;
            while (at > 0 && candDist[at - 1] > d) at--;
            candidates.Insert(at, ci); candDist.Insert(at, d);
        }
        int movingShown = 0;
        for (int i = 0; i < agents.Count; i++) if (!agents[i].parkedShell) movingShown++;
        for (int k = 0; k < candidates.Count && movingShown < Shells; k++)
        {
            int ci = candidates[k];
            if (Spawn(ci, t) != null) movingShown++;
        }

        // A player on foot who stepped out right in front of a moving car (before this frame's braking zeroes its speed).
        BoopLocalPlayer(now);

        // 3. Drive every shown car.
        bool headlights = GameClock.Daylight01 < 0.5f;
        for (int i = agents.Count - 1; i >= 0; i--)
        {
            var a = agents[i];
            if (a.parkedShell) { TickParkedShell(a, now, dt, cam); continue; }
            a.distance = FlatDist(a.position, cam);
            bool done = Drive(a, t, now, dt, cam, headlights);
            if (done) { Finish(a); continue; }
            if (a.distance > HideBeyond && a.shuntT < 0f) Release(a, false);
        }
        ShownMoving = 0;
        for (int i = 0; i < agents.Count; i++) if (!agents[i].parkedShell) ShownMoving++;
        if (ShownMoving > MaxShown) MaxShown = ShownMoving;

        // 4. Static twins: shown only while the car is home and no shell stands in for it.
        for (int i = 0; i < n; i++)
        {
            bool home = phase[i] == CarPhase.Home && agentOf[i] == null;
            ShowStatic(i, home);
            UpdateParkedBody(i, home);
        }
    }

    void EnsureDay()
    {
        var d = sim.Day(ctx.Day, ctx.UnlockedMask);
        if (d == day && d.day == dayIndex && d.mask == dayMask) return;
        day = d; dayIndex = d.day; dayMask = d.mask;
        for (int i = 0; i < nextChange.Length; i++) nextChange[i] = double.MinValue;
        // Shells keep driving their old paths (CarPath objects are immutable); trip indexes are re-read at the next leg.
        for (int i = 0; i < agents.Count; i++) agents[i].trip = -1;
    }

    void RefreshDelays()
    {
        if (overridesVersion == LifeOverrides.Version) return;
        overridesVersion = LifeOverrides.Version;
        var cars = sim.Roster.cars;
        for (int i = 0; i < cars.Length; i++)
        {
            float d = LifeOverrides.DelaySeconds(LifeId.Car(i), ctx.Day) + LifeOverrides.DelaySeconds(LifeId.Resident(cars[i].driver), ctx.Day);
            int ms = Mathf.RoundToInt(d * 1000f);
            if (ms != delayMs[i]) { delayMs[i] = ms; nextChange[i] = double.MinValue; }
        }
    }

    bool DensityKeeps(int car)
    {
        float density = WorldLifeTuning.Density;
        return density >= 0.999f || LifeHash.Float01(LifeHash.U64(sim.Seed, LifeKind.Car, car, -1, 77)) < density;
    }

    static float FlatDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

    // ------------------------------------------------------------------ static twins

    void MapHouseholdCars()
    {
        var all = HouseholdCar.All;
        if (all.Count == householdCarsSeen) return;
        householdCarsSeen = all.Count;
        for (int i = 0; i < all.Count; i++)
        {
            var hc = all[i];
            ParkedLightsOff(hc);
            if (hc == null || hc.parkedOnly) continue;
            int ci = sim.Roster.CarAt(hc.stopId, hc.slot);
            if (ci < 0 || hcOf[ci] == hc) continue;
            hcOf[ci] = hc;
            staticHidden[ci] = false;
        }
    }

    // Parked cars have their lights off (fixer pass). The pack's car material set has always-on emissive head- and
    // tail-lights, so every parked car glowed day and night. The static twin's renderer gets the off copies (a material
    // swap is fine on a batching-static renderer: the batch keeps its geometry); Sleep puts the originals back.
    readonly List<Renderer> litOff = new List<Renderer>(96);
    readonly List<Material[]> litOffOriginal = new List<Material[]>(96);

    void ParkedLightsOff(HouseholdCar hc)
    {
        var r = hc != null ? hc.body : null;
        if (r == null || litOff.Contains(r)) return;
        var mats = r.sharedMaterials;
        EnsureLightMats(mats);
        Material[] off = null;
        for (int m = 0; m < mats.Length; m++)
        {
            var mm = mats[m];
            if (mm == null) continue;
            Material rep = mm.name.StartsWith("M_emissive", StringComparison.Ordinal) ? headOffMat
                         : mm.name.StartsWith("M_backlights", StringComparison.Ordinal) ? tailOffMat : null;
            if (rep == null) continue;
            if (off == null) off = (Material[])mats.Clone();
            off[m] = rep;
        }
        if (off == null) return;
        litOff.Add(r); litOffOriginal.Add(mats);
        r.sharedMaterials = off;
    }

    void RestoreParkedLights()
    {
        for (int i = 0; i < litOff.Count; i++) if (litOff[i] != null) litOff[i].sharedMaterials = litOffOriginal[i];
        litOff.Clear(); litOffOriginal.Clear();
    }

    /// <summary>How many parked cars have their lights switched off (tests).</summary>
    public int ParkedLightsOffCount => litOff.Count;

    void ShowStatic(int car, bool show)
    {
        var hc = car < hcOf.Length ? hcOf[car] : null;
        if (hc == null) return;
        if (staticHidden[car] == !show) return;
        staticHidden[car] = !show;
        if (hc.body != null) hc.body.enabled = show;
        if (hc.restCollider != null) hc.restCollider.enabled = show;
    }

    void UpdateParkedBody(int car, bool home)
    {
        var hc = hcOf[car];
        bool want = home && hc != null;
        var pb = parked[car];
        if (want && pb == null)
        {
            var b = hc.restCollider != null ? hc.restCollider.bounds : new Bounds(hc.transform.position, new Vector3(2f, 1.5f, 4.4f));
            var pose = hc.SlotPose;
            pb = parked[car] = new ParkedCarBody
            {
                id = LifeId.Car(car), car = car, household = hc, position = new Vector3(pose.position.x, b.min.y, pose.position.z),
                rotation = pose.rotation, stopId = hc.stopId,
                halfExtents = new Vector3(Mathf.Min(b.extents.x, b.extents.z), b.extents.y, Mathf.Max(b.extents.x, b.extents.z)),
                onHit = OnParkedHit,
            };
        }
        if (pb == null) return;
        if (want && !pb.registered) { LifeBodies.Register(pb); pb.registered = true; }
        else if (!want && pb.registered) { LifeBodies.Unregister(pb); pb.registered = false; }
    }

    // ------------------------------------------------------------------ shells

    static Material headOffMat, brakeOnMat, tailOffMat;
    readonly Dictionary<long, Material[][]> looks = new Dictionary<long, Material[][]>(16);

    CarAgent TakeShell(bool parkedUse)
    {
        int moving = 0, parkedN = 0;
        for (int i = 0; i < agents.Count; i++) if (agents[i].parkedShell) parkedN++; else moving++;
        if (!parkedUse && moving >= Shells) return null;
        if (parkedUse && parkedN >= ExtraParkedShells) return null;
        if (free.Count > 0) return free.Pop();
        if (shellsMade >= Shells + ExtraParkedShells) return null;
        var a = CarAgent.CreateShell(ctx.Root, shellsMade++);
        a.onHit = OnAgentHit;
        return a;
    }

    Material[][] LookFor(HouseholdCar hc, Material[] baseMats)
    {
        long key = hc != null ? ((long)(hc.prefab != null ? hc.prefab.GetInstanceID() : 0) << 32) ^ (uint)(hc.tint != null ? hc.tint.GetInstanceID() : 0) : 0;
        if (looks.TryGetValue(key, out var v)) return v;
        EnsureLightMats(baseMats);
        v = new Material[5][];
        for (int k = 0; k < 5; k++)
        {
            var arr = (Material[])baseMats.Clone();
            bool headOff = k >= 2, brake = k < 4 && (k & 1) != 0, tailOff = k == 4;
            for (int m = 0; m < arr.Length; m++)
            {
                if (arr[m] == null) continue;
                if (headOff && arr[m].name.StartsWith("M_emissive", StringComparison.Ordinal) && headOffMat != null) arr[m] = headOffMat;
                if (brake && arr[m].name.StartsWith("M_backlights", StringComparison.Ordinal) && brakeOnMat != null) arr[m] = brakeOnMat;
                if (tailOff && arr[m].name.StartsWith("M_backlights", StringComparison.Ordinal) && tailOffMat != null) arr[m] = tailOffMat;
            }
            v[k] = arr;
        }
        looks[key] = v;
        return v;
    }

    static void EnsureLightMats(Material[] mats)
    {
        if (headOffMat != null && brakeOnMat != null && tailOffMat != null) return;
        foreach (var m in mats)
        {
            if (m == null) continue;
            if (headOffMat == null && m.name.StartsWith("M_emissive", StringComparison.Ordinal))
            {
                headOffMat = new Material(m) { name = "WL_Headlight_Off", hideFlags = HideFlags.DontSave };
                headOffMat.SetColor("_EmissionColor", Color.black);
                headOffMat.SetColor("_BaseColor", new Color(0.86f, 0.84f, 0.72f));
            }
            if (brakeOnMat == null && m.name.StartsWith("M_backlights", StringComparison.Ordinal))
            {
                brakeOnMat = new Material(m) { name = "WL_Brake_On", hideFlags = HideFlags.DontSave };
                brakeOnMat.SetColor("_EmissionColor", new Color(9f, 0.35f, 0.1f));
                brakeOnMat.SetColor("_BaseColor", new Color(1f, 0.1f, 0.08f));
            }
            if (tailOffMat == null && m.name.StartsWith("M_backlights", StringComparison.Ordinal))
            {
                tailOffMat = new Material(m) { name = "WL_Tail_Off", hideFlags = HideFlags.DontSave };
                tailOffMat.SetColor("_EmissionColor", Color.black);
                tailOffMat.SetColor("_BaseColor", new Color(0.42f, 0.05f, 0.04f));
            }
        }
    }

    static Mesh fallbackMesh;
    static Material[] fallbackMats;

    /// <summary>Dress a shell as roster car <paramref name="car"/> (or as a debug car when car &lt; 0).</summary>
    void Dress(CarAgent a, int car, LifeId id, HouseholdCar hc, LifeId driverId, string stopId)
    {
        if (hc == null) hc = car >= 0 && car < hcOf.Length && hcOf[car] != null ? hcOf[car] : FirstHouseholdCar();
        Mesh mesh = null; Material[] mats = null; Bounds mb = new Bounds(new Vector3(0f, 0.75f, 0f), new Vector3(4.2f, 1.5f, 2f));
        Vector3 scale = Vector3.one; float noseYaw = -90f;
        if (hc != null && hc.prefab != null)
        {
            var pmf = hc.prefab.GetComponentInChildren<MeshFilter>();
            if (pmf != null && pmf.sharedMesh != null) { mesh = pmf.sharedMesh; mb = mesh.bounds; }
            mats = hc.body != null ? hc.body.sharedMaterials : hc.prefab.GetComponentInChildren<MeshRenderer>()?.sharedMaterials;
            scale = hc.scale; noseYaw = hc.noseYaw;
        }
        if (mesh == null)
        {
            if (fallbackMesh == null) fallbackMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (fallbackMats == null) fallbackMats = new[] { CritterLook.Mat(new Color(0.75f, 0.2f, 0.2f), 0.5f) };
            mesh = fallbackMesh; mats = fallbackMats; mb = new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one);
            scale = new Vector3(4.2f, 1.4f, 1.9f); noseYaw = -90f;
        }
        a.Assign(id, car, hc, mesh, LookFor(hc, mats ?? fallbackMats), scale, noseYaw, mb, driverId, stopId);
    }

    HouseholdCar FirstHouseholdCar()
    {
        var all = HouseholdCar.All;
        for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].prefab != null) return all[i];
        return null;
    }

    CarAgent Spawn(int car, double t)
    {
        // Don't pop a car in on top of (or right behind) a shown car: wait until its timetable spot is clear.
        {
            var tr0 = day.trips[tripOf[car]];
            bool ob = phase[car] == CarPhase.Out;
            var p0 = ob ? tr0.outPath : tr0.inPath;
            float arc0 = p0.ArcAtTime((float)((t - delayMs[car] - (ob ? tr0.depart : tr0.ret)) * 0.001), out _, out _);
            Vector3 at = p0.PointAt(arc0);
            for (int i = 0; i < agents.Count; i++)
            {
                var o = agents[i];
                float dx = o.position.x - at.x, dz = o.position.z - at.z;
                float clear = o.halfExtents.z + 2.4f + FollowStopGap;
                if (dx * dx + dz * dz < clear * clear) return null;
            }
        }
        var a = TakeShell(false);
        if (a == null) return null;
        var rc = sim.Roster.cars[car];
        Dress(a, car, LifeId.Car(car), hcOf[car], LifeId.Resident(rc.driver), sim.Roster.households[rc.household].stopId);
        a.trip = tripOf[car];
        var tr = day.trips[a.trip];
        a.outbound = phase[car] == CarPhase.Out;
        a.path = a.outbound ? tr.outPath : tr.inPath;
        double legT = (t - delayMs[car] - (a.outbound ? tr.depart : tr.ret)) * 0.001;
        a.s = a.path.ArcAtTime((float)legT, out float v, out int seg);
        a.v = v;
        a.yaw = HeadingAt(a, a.s, true);
        a.position = a.path.PointAt(a.s);
        a.lodTier = 0;
        a.Show(true);
        a.Pose(a.position, a.yaw, 0f, false);
        a.SetDriverVisible(true);
        agents.Add(a);
        agentOf[car] = a;
        // The parked body of this car hands over to the shell before the shell registers, so the two never share
        // the id Car#N in LifeBodies (Find would return the wrong one for a frame; G integration).
        UpdateParkedBody(car, false);
        LifeBodies.Register(a);
        return a;
    }

    void Release(CarAgent a, bool showStatic)
    {
        LifeBodies.Unregister(a);
        a.Show(false);
        a.SetCollider(true);
        agents.Remove(a);
        if (a.car >= 0 && a.car < agentOf.Length && agentOf[a.car] == a) agentOf[a.car] = null;
        for (int i = debugCars.Count - 1; i >= 0; i--) if (debugCars[i].agent == a) debugCars.RemoveAt(i);
        a.parkedShell = false;
        a.car = -1;
        free.Push(a);
    }

    /// <summary>The car reached the end of its leg: vanish in the tunnel, or park (its static twin takes over).</summary>
    void Finish(CarAgent a) => Release(a, !a.outbound);

    // ------------------------------------------------------------------ driving

    public const byte BlockNone = 0, BlockCar = 1, BlockTruck = 2, BlockPerson = 3, BlockPlayer = 4, BlockBox = 5;

    /// <summary>Canonical (timetable) arc and speed of a shown car's leg at day-ms t.</summary>
    bool Canonical(CarAgent a, double t, out float sCanon, out float vCanon)
    {
        sCanon = a.s; vCanon = a.v;
        if (a.car < 0)
        {
            DebugCar dc = null;
            for (int i = 0; i < debugCars.Count; i++) if (debugCars[i].agent == a) { dc = debugCars[i]; break; }
            if (dc == null) return false;
            sCanon = dc.path.ArcAtTime((float)(Time.timeAsDouble - dc.start), out vCanon, out _);
            return true;
        }
        // Find the trip this leg belongs to (index may have changed at a day rebuild).
        int ci = a.car;
        double te = t - delayMs[ci];
        if (a.trip == -1 || a.trip >= day.trips.Count || (a.trip >= 0 && day.trips[a.trip].car != ci)
            || (a.trip >= 0 && (a.outbound ? day.trips[a.trip].outPath : day.trips[a.trip].inPath) != a.path))
        {
            a.trip = -1;
            int s0 = day.tripStart[ci], s1 = s0 + day.tripCount[ci];
            for (int k = s0; k < s1; k++)
                if ((a.outbound ? day.trips[k].outPath : day.trips[k].inPath) == a.path) { a.trip = k; break; }
            if (a.trip < 0) { sCanon = a.path.Length; vCanon = 0f; return true; }   // the trip is gone: finish the leg
            // Re-attached (a new day, a clock jump): if that trip's leg is behind this car, this car's own leg is over —
            // let it drive to the end and park / vanish on its own.
            var rt = day.trips[a.trip];
            double lt = (te - (a.outbound ? rt.depart : rt.ret)) * 0.001;
            if (lt < 0.0 || a.path.ArcAtTime((float)lt, out _, out _) < a.s - 2f) { a.trip = -2; }
        }
        if (a.trip == -2) { sCanon = a.path.Length; vCanon = a.path.SpeedAtArc(a.s); return true; }
        var tr = day.trips[a.trip];
        double legT = (te - (a.outbound ? tr.depart : tr.ret)) * 0.001;
        sCanon = a.path.ArcAtTime((float)legT, out vCanon, out _);
        return true;
    }

    /// <summary>One frame of a shown car. Returns true when it reached the end of its leg.</summary>
    bool Drive(CarAgent a, double t, float now, float dt, Vector3 cam, bool headlights)
    {
        var path = a.path;
        if (path == null) return true;
        Canonical(a, t, out float sCanon, out float vCanon);

        // LOD: full ≤ 35 m, lite ≤ 90 m, far (15 Hz) beyond.
        a.lodTier = a.distance <= WorldLifeTuning.Lod.FullDistance ? 0 : a.distance <= WorldLifeTuning.Lod.LiteDistance ? 1 : 2;
        if (a.lodTier == 2)
        {
            if (now < a.nextFarUpdate && a.shuntT < 0f && now >= a.holdUntil)
            { a.s = Mathf.Min(a.s + a.v * dt, Mathf.Max(a.s, sCanon)); return a.s >= path.Length - 0.01f && sCanon >= path.Length - 0.01f; }
            dt = Mathf.Max(dt, now - (a.nextFarUpdate - 1f / WorldLifeTuning.Lod.LiteHz));
            a.nextFarUpdate = now + 1f / WorldLifeTuning.Lod.LiteHz;
        }

        // A shunt adds a timetable delay: that is NOT permission to teleport the visible car backwards.
        // Only reconcile unseen, settled traffic. Nearby cars recover by driving, including at day changes.
        bool hold = now < a.holdUntil;
        float lagS = SecondsBehind(a, sCanon);
        if (VehicleCrashRules.MaySnapTraffic(a.shuntT >= 0f, hold, a.slide.sqrMagnitude, a.distance) &&
            (lagS > SnapLagSeconds || a.s - sCanon > 30f))
        { a.s = sCanon; a.v = vCanon; a.overtake = 0; a.lateral = 0f; }

        // Shunt / pull-over holds.
        if (a.shuntT >= 0f) TickShunt(a, now, dt);
        hold |= a.shuntT >= 0f;

        float speedLimit = path.SpeedAtArc(a.s);
        float want;
        if (hold) want = 0f;
        else if (a.s < sCanon - 0.3f) want = Mathf.Max(speedLimit * WorldLifeTuning.Traffic.CatchUpFactor, 0.6f);
        else if (a.s > sCanon + 0.05f) want = 0f;                     // the timetable moved back (a host delay): wait
        else want = vCanon;

        // Obstacles along the path (other cars, trucks, people, players) and intersection boxes.
        float look = Mathf.Clamp(a.v * a.v / (2f * BrakeDecel) + 12f, 14f, 42f);
        float gap = ScanAhead(a, look, out byte kind, out TruckControllerV2 truck, out Vector3 obsVel);
        float boxGap = BoxGate(a, look);
        if (boxGap < gap) { gap = boxGap; kind = BlockBox; }
        float laneGap = ReverseOutGate(a);
        if (laneGap < gap) { gap = laneGap; kind = BlockBox; }
        float courtesyGap = CourtesyGate(a, look, now, dt);
        if (courtesyGap < gap) { gap = courtesyGap; kind = BlockBox; }
        a.gapAhead = gap; a.blockKind = kind;
        if (kind != BlockNone)
        {
            float stop = kind == BlockCar ? FollowStopGap + FollowTimeGap * a.v : kind == BlockTruck ? TruckStopGap : kind == BlockBox ? 0.2f : PersonStopGap;
            float allow = Mathf.Sqrt(2f * BrakeDecel * Mathf.Max(0f, gap - stop));
            if (kind == BlockCar) allow = Mathf.Max(allow, Mathf.Min(want, Vector3.Dot(obsVel, ForwardOf(a)) * (gap > stop ? 1f : 0f)));
            want = Mathf.Min(want, allow);
        }
        if (now < a.pullOverUntil) want = 0f;

        // Stuck behind a truck: honk after 3 s, then overtake in the other lane when it's clear.
        bool stuck = kind != BlockNone && a.v < 0.6f && !hold;
        if (stuck) a.stuckTime += dt; else if (a.v > 2f) { a.stuckTime = 0f; a.honkCount = 0; }
        if (stuck && a.stuckTime >= HonkAfterStop && now - a.lastHonk > (a.honkCount == 0 ? 0f : 6f)
            && (kind == BlockTruck || kind == BlockPlayer || kind == BlockPerson))
            Honk(a, kind == BlockTruck ? 0 : 1, now);
        if (kind == BlockTruck && a.overtake == 0 && a.honkCount > 0 && stuck && truck != null && a.stuckTime >= HonkAfterStop + 0.4f)
            TryStartOvertake(a, truck, gap);
        // Courtesy: still stuck after CourtesyAfter s → ask the oncoming lane to hold back (re-planned 4× a second); keep the
        // ask up while passing.
        if (kind == BlockTruck && a.overtake == 0 && stuck && truck != null && a.stuckTime >= CourtesyAfter && now >= a.courtesyUntil - 0.25f
            && (truck.Body == null || truck.Body.linearVelocity.sqrMagnitude <= 0.25f))   // only round a stopped truck, like the overtake
        {
            if (OvertakePlan(a, truck, gap, out float cs0, out _, out float cs1, out _, out _))
            {
                a.courtesyUntil = now + 0.5f;
                a.courtesyFrom = path.PointAt(cs0); a.courtesyTo = path.PointAt(cs1);
            }
        }
        if (a.overtake != 0) a.courtesyUntil = Mathf.Max(a.courtesyUntil, now + 0.3f);

        // Integrate (comfortable accel; hard braking allowed).
        float accel = want > a.v ? CarPath.Accel * (a.s < sCanon - 0.3f ? WorldLifeTuning.Traffic.CatchUpFactor : 1f) : (gap < 3f ? EmergencyDecel : BrakeDecel * 1.4f);
        // A panic stop for a truck, a person or a player right ahead: the tyres squeal (G polish; cosmetic, local).
        if (accel >= EmergencyDecel && a.v > 5f && want < a.v - 2.5f && a.lodTier <= 1 && now - a.lastScreech > 2.5f
            && (kind == BlockTruck || kind == BlockPlayer || kind == BlockPerson))
        {
            a.lastScreech = now;
            KnockSfx.Play(WorldLifeIds.Sfx.TireScreech, "squeak", a.position, 0.5f, 0.95f + 0.02f * (a.car & 7));
        }
        a.v = Mathf.MoveTowards(a.v, want, accel * dt);
        float ns = a.s + a.v * dt;
        if (ns > sCanon && a.s <= sCanon + 0.05f) { ns = Mathf.Max(a.s, sCanon); a.v = Mathf.Min(a.v, Mathf.Max(vCanon, 0f)); }
        // Close to the timetable and nothing in the way: ride it exactly (every machine then draws the same spot, and a leg
        // that ends at rest — the nose-in to the slot — really reaches its end).
        if (kind == BlockNone && !hold && a.shuntT < 0f && a.s >= sCanon - 0.3f && a.s <= sCanon + 0.05f) { ns = sCanon; a.v = vCanon; }
        // Never drive into the thing ahead.
        if (kind != BlockNone && gap < 99f)
        {
            float stop = kind == BlockCar ? FollowStopGap - 0.5f : kind == BlockTruck ? TruckStopGap - 0.2f : kind == BlockBox ? 0f : PersonStopGap - 1f;
            float maxAdvance = Mathf.Max(0f, gap - stop);
            if (ns - a.s > maxAdvance) { ns = a.s + maxAdvance; a.v = Mathf.Min(a.v, maxAdvance / Mathf.Max(dt, 1e-3f)); }
        }
        a.s = Mathf.Min(ns, path.Length);
        a.yielding = kind != BlockNone && a.v < 1f;
        if (a.overtake != 0 && a.s >= a.otEnd + OvertakeRamp) a.overtake = 0;
        a.lateral = LateralAt(a, a.s, now);

        // Pose.
        Vector3 p = path.PointAt(a.s, out int seg);
        float yaw = HeadingAt(a, a.s, false);
        if (a.overtake != 0 || now < a.pullOverUntil + 1.5f)
        {
            float slope = (LateralAt(a, a.s + 1f, now) - LateralAt(a, a.s - 1f, now)) * 0.5f;
            yaw += Mathf.Atan(slope) * Mathf.Rad2Deg;
        }
        if (a.lateral != 0f)
        {
            Vector3 tan = path.Tangent(seg);
            Vector3 right = new Vector3(tan.z, 0f, -tan.x);                    // the lane's right (+ = toward the kerb)
            if ((path.flags[seg] & CarSegFlags.Reverse) != 0) right = -right;
            p += right * a.lateral;
        }
        if (a.slide.sqrMagnitude > 1e-6f)
        {
            // The shunt's slide eases back to the lane, but not while a truck stands on the car's lane spot (fixer pass:
            // a shoved car used to drift back into the truck that shoved it and sit inside its bodywork).
            if (!hold && a.shuntT < 0f && !TruckOverlapsAt(p, yaw, a.halfExtents, 0.3f)) a.slide = Vector3.MoveTowards(a.slide, Vector3.zero, dt * 1.2f);
            p += a.slide;
        }
        float drawYaw = a.yaw;
        float k = 1f - Mathf.Exp(-12f * dt);
        drawYaw = Mathf.LerpAngle(drawYaw, yaw + (a.shuntT >= 0f ? a.shuntYaw : 0f), a.shuntT >= 0f ? 1f : k);
        a.Pose(p, drawYaw, dt, a.lodTier == 0);
        a.SetDriverVisible(a.lodTier == 0 && !a.driverOut);
        // Fallen props in the way are batted aside (fixer pass; cars used to drive through debris). Heavy ones cost speed.
        var kd = KnockableDirector.Instance;
        if (kd != null && kd.DownCount > 0 && a.lodTier <= 1 && a.v > 0.5f && a.shuntT < 0f)
        {
            Quaternion q = Quaternion.Euler(0f, a.yaw, 0f);
            float kg = kd.BatForCar(p + q * a.box.center, q, a.halfExtents, 0.5f + a.v * 0.12f, ForwardOf(a) * a.v, a.car);
            if (kg > 0f)
            {
                a.v *= 1f - Mathf.Clamp(kg * DebrisSpeedLossPerKg, 0.02f, 0.3f);
                if (kg >= 25f && now - a.lastScreech > 2.5f && a.lodTier == 0) { a.lastScreech = now; KnockSfx.Play(WorldLifeIds.Sfx.TireScreech, "squeak", a.position, 0.35f, 1.1f); }
            }
        }
        bool braking = a.yielding ? (Mathf.Repeat(now, 0.5f) < 0.25f || kind == BlockTruck) : want < a.v - 0.3f || (a.v < 0.2f && kind != BlockNone);
        bool alarm = now < a.alarmUntil;
        if (alarm) braking = Mathf.Repeat(now, 0.4f) < 0.2f;
        a.SetLights(alarm ? Mathf.Repeat(now, 0.4f) < 0.2f : headlights, braking);
        if (now >= a.colliderOffUntil && !a.box.enabled && !TruckOverlaps(a)) a.SetCollider(true);

        return a.s >= path.Length - 0.01f && sCanon >= path.Length - 0.01f && a.shuntT < 0f && !hold;
    }

    /// <summary>How many seconds of timetable the shown car is behind (≈ lag / the speed it should be doing).</summary>
    static float SecondsBehind(CarAgent a, float sCanon)
    {
        float lag = sCanon - a.s;
        if (lag <= 0f) return 0f;
        return lag / Mathf.Max(2f, a.path.SpeedAtArc(a.s));
    }

    static Vector3 ForwardOf(CarAgent a) => Quaternion.Euler(0f, a.yaw, 0f) * Vector3.forward;

    /// <summary>Heading (yaw, degrees) at arc s: the chord over ±1.5 m (smooth through polyline corners), reversed on
    /// reverse segments, and blended into the slot pose at the driveway ends.</summary>
    float HeadingAt(CarAgent a, float s, bool snap)
    {
        var path = a.path;
        int segHere = path.SegmentAt(s);
        bool reverse = (path.flags[segHere] & CarSegFlags.Reverse) != 0;
        // A chord over ±1.5 m (smooth through polyline corners) that never reaches across the reverse cusp.
        float sb = Mathf.Max(0f, s - 1.2f), sf = Mathf.Min(path.Length, s + 1.5f);
        if (((path.flags[path.SegmentAt(sb)] & CarSegFlags.Reverse) != 0) != reverse) sb = path.s[segHere];
        if (((path.flags[path.SegmentAt(sf)] & CarSegFlags.Reverse) != 0) != reverse) sf = path.s[Mathf.Min(segHere + 1, path.Count - 1)];
        Vector3 d = path.PointAt(sf) - path.PointAt(sb); d.y = 0f;
        if (d.sqrMagnitude < 1e-4f) d = path.Tangent(segHere);
        if (reverse) d = -d;
        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        // Ends at a driveway slot: hold the slot's exact pose so the static twin swaps in without a twitch.
        if (a.car >= 0 && a.car < hcOf.Length)
        {
            var rc = sim.Roster.cars[a.car];
            var slot = sim.Graph.houses[rc.house].CarSlot(rc.slot);
            float slotYaw = slot.rotation.eulerAngles.y;
            if (a.outbound && s < 2.5f) yaw = Mathf.LerpAngle(slotYaw, yaw, s / 2.5f);
            else if (!a.outbound && path.Length - s < 3f) yaw = Mathf.LerpAngle(slotYaw, yaw, (path.Length - s) / 3f);
        }
        return yaw;
    }

    // ---- obstacles ----

    struct Obstacle
    {
        public byte kind;
        public Vector3 center;
        public Quaternion inv;      // inverse yaw rotation
        public Vector3 he;          // half extents (x, z used)
        public Vector3 vel;
        public CarAgent car;
        public TruckControllerV2 truck;
        public float yaw;
    }

    readonly List<Obstacle> obs = new List<Obstacle>(32);

    void GatherObstacles(CarAgent a, float reach)
    {
        obs.Clear();
        Vector3 me = a.position;
        float r2 = (reach + 8f) * (reach + 8f);
        Vector3 myFwd = ForwardOf(a);
        // Other shown cars.
        for (int i = 0; i < agents.Count; i++)
        {
            var o = agents[i];
            if (o == a || !o.Shown) continue;
            if ((o.position - me).sqrMagnitude > r2) continue;
            Vector3 of = ForwardOf(o);
            if (a.overtake != 0 && Vector3.Dot(of, myFwd) < -0.5f) continue;   // oncoming cars wait for an overtaker
            // Overlapping (timetables met): only the car behind yields.
            Vector3 rel = o.position - me; rel.y = 0f;
            if (rel.sqrMagnitude < 25f && Vector3.Dot(rel, myFwd) < 0f) continue;
            obs.Add(new Obstacle
            {
                kind = BlockCar, center = o.position, inv = Quaternion.Euler(0f, -o.yaw, 0f), he = o.halfExtents,
                vel = of * o.v, car = o, yaw = o.yaw,
            });
        }
        // Trucks (every truck, whoever drives it).
        var trucks = TruckControllerV2.All;
        var bb = TruckControllerV2.BodyBoxMetres;
        for (int i = 0; i < trucks.Count; i++)
        {
            var tk = trucks[i];
            if (tk == null || !tk.isActiveAndEnabled) continue;
            if (a.overtake != 0 && tk == a.otTruck) continue;   // the one we're passing in the other lane
            var rb = tk.Body;
            Vector3 tp = rb != null ? rb.position : tk.transform.position;
            Quaternion tr = rb != null ? rb.rotation : tk.transform.rotation;
            Vector3 c = tp + tr * bb.center;
            if ((c - me).sqrMagnitude > r2 + 100f) continue;
            float ty = tr.eulerAngles.y;
            obs.Add(new Obstacle
            {
                kind = BlockTruck, center = c, inv = Quaternion.Euler(0f, -ty, 0f), he = bb.extents,
                vel = rb != null && !rb.isKinematic ? rb.linearVelocity : Vector3.zero, truck = tk, yaw = ty,
            });
        }
        // Players on foot.
        for (int i = players.Count - 1; i >= 0; i--)
        {
            var pc = players[i];
            if (pc == null) { players.RemoveAt(i); continue; }
            if (!pc.isActiveAndEnabled || pc.IsDriving) continue;
            Vector3 pp = pc.transform.position;
            if ((pp - me).sqrMagnitude > r2) continue;
            obs.Add(new Obstacle { kind = BlockPlayer, center = pp, inv = Quaternion.identity, he = new Vector3(0.45f, 0.9f, 0.45f) });
        }
        // People and dogs shown by package C, plus timetable pedestrians near the road.
        bodyScratch.Clear();
        LifeBodies.Query(me, reach + 4f, bodyScratch, LifeBodyKind.Person | LifeBodyKind.Dog);
        for (int i = 0; i < bodyScratch.Count; i++)
            obs.Add(new Obstacle { kind = BlockPerson, center = bodyScratch[i].Position, inv = Quaternion.identity, he = new Vector3(0.4f, 0.9f, 0.4f) });
        var peds = PedestrianDirector.Instance;
        if (peds != null)
        {
            var list = peds.Out;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (!p.relevant || p.lastFrame != ctx.Frame) continue;
                if ((p.position - me).sqrMagnitude > r2) continue;
                obs.Add(new Obstacle { kind = BlockPerson, center = p.position, inv = Quaternion.identity, he = new Vector3(0.4f, 0.9f, 0.4f) });
            }
        }
    }

    /// <summary>Bumper distance (m) along the path to the first obstacle that would touch the car, or +∞.</summary>
    float ScanAhead(CarAgent a, float look, out byte kind, out TruckControllerV2 truck, out Vector3 vel)
    {
        kind = BlockNone; truck = null; vel = Vector3.zero;
        GatherObstacles(a, look + a.halfExtents.z);
        if (obs.Count == 0) return float.PositiveInfinity;
        var path = a.path;
        float halfW = a.halfExtents.x + 0.25f, halfL = a.halfExtents.z;
        float now = Time.time;
        const float Step = 1.25f;
        float start = a.s + halfL * 0.6f;   // from the car's nose region forward
        int n = Mathf.CeilToInt((look + halfL * 0.4f) / Step);
        for (int k = 0; k <= n; k++)
        {
            float s = Mathf.Min(start + k * Step, path.Length);
            Vector3 q = path.PointAt(s, out int seg);
            float lat = LateralAt(a, s, now);
            if (Mathf.Abs(lat) > 0.01f)
            {
                Vector3 tan = path.Tangent(seg);
                Vector3 rt = new Vector3(tan.z, 0f, -tan.x);
                if ((path.flags[seg] & CarSegFlags.Reverse) != 0) rt = -rt;
                q += rt * lat;
            }
            for (int i = 0; i < obs.Count; i++)
            {
                var o = obs[i];
                Vector3 l = o.inv * (q - o.center);
                float mx = o.he.x + halfW, mz = o.he.z + halfW;
                if (o.kind == BlockPerson || o.kind == BlockPlayer) { mx = o.he.x + halfW; mz = o.he.x + halfW; }
                if (Mathf.Abs(l.x) <= mx && Mathf.Abs(l.z) <= mz)
                {
                    kind = o.kind; truck = o.truck; vel = o.vel;
                    // Refine where the path first touches it (the samples are 1.25 m apart).
                    float lo = k == 0 ? a.s : s - Step, hi = s;
                    for (int it = 0; it < 4 && k > 0; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        Vector3 qm = path.PointAt(mid, out int sm);
                        float lm = LateralAt(a, mid, now);
                        if (Mathf.Abs(lm) > 0.01f)
                        {
                            Vector3 tm = path.Tangent(sm);
                            Vector3 rm = new Vector3(tm.z, 0f, -tm.x);
                            if ((path.flags[sm] & CarSegFlags.Reverse) != 0) rm = -rm;
                            qm += rm * lm;
                        }
                        Vector3 lq = o.inv * (qm - o.center);
                        if (Mathf.Abs(lq.x) <= mx && Mathf.Abs(lq.z) <= mz) hi = mid; else lo = mid;
                    }
                    return Mathf.Max(0f, hi - (a.s + halfL));
                }
            }
            if (s >= path.Length) break;
        }
        return float.PositiveInfinity;
    }

    /// <summary>Intersection boxes (§7.3): slow to 3 m/s entering (the profile does), and wait at the line while another
    /// shown car is inside a box whose path comes within 3 m of ours. Returns the bumper distance to the line or +∞.</summary>
    float BoxGate(CarAgent a, float look)
    {
        var path = a.path;
        var boxes = path.boxes;
        if (boxes == null || boxes.Length == 0) { a.inBox = false; return float.PositiveInfinity; }
        float halfL = a.halfExtents.z;
        // Inside a box?
        a.inBox = false;
        for (int b = 0; b < boxes.Length; b++)
        {
            if (a.s + halfL >= boxes[b].x && a.s - halfL <= boxes[b].y) { a.inBox = true; a.boxEnd = boxes[b].y; return float.PositiveInfinity; }
        }
        // The next box ahead within reach.
        for (int b = 0; b < boxes.Length; b++)
        {
            float entry = boxes[b].x;
            if (entry < a.s + halfL) continue;
            float gap = entry - (a.s + halfL);
            if (gap > look) return float.PositiveInfinity;
            if (BoxConflict(a, boxes[b])) return gap;
            // Free: claim it once we're right at the line, so a car behind us in the same frame sees it taken.
            if (gap < 1.5f) { a.inBox = true; a.boxEnd = boxes[b].y; }
            return float.PositiveInfinity;
        }
        return float.PositiveInfinity;
    }

    /// <summary>Backing out of a driveway (§7.3): hold on the driveway while a shown car is coming along the lane toward
    /// the spot the reverse swings into (or is just past it). Bumper distance to the lane edge, or +∞.</summary>
    float ReverseOutGate(CarAgent a)
    {
        var path = a.path;
        int cusp = path.cusp;
        if (cusp < 0 || !a.outbound) return float.PositiveInfinity;
        float sCusp = path.s[cusp];
        float edge = sCusp - 4.5f;                     // the car's tail reaches the lane about here
        if (a.s >= edge - 0.05f) return float.PositiveInfinity;   // committed
        Vector3 p3 = path.pts[cusp];
        Vector3 dir = path.Tangent(cusp);
        Vector3 rt = new Vector3(dir.z, 0f, -dir.x);
        for (int i = 0; i < agents.Count; i++)
        {
            var o = agents[i];
            if (o == a || o.parkedShell || o.path == null) continue;
            Vector3 d = o.position - p3; d.y = 0f;
            float along = Vector3.Dot(d, dir), lat = Mathf.Abs(Vector3.Dot(d, rt));
            if (lat > 2.6f || along < -28f || along > 10f) continue;
            if (Vector3.Dot(ForwardOf(o), dir) < 0.5f && o.v > 0.3f) continue;   // the other lane / turning away
            return Mathf.Max(0f, edge - a.s);
        }
        return float.PositiveInfinity;
    }

    bool BoxConflict(CarAgent a, Vector2 box)
    {
        for (int i = 0; i < agents.Count; i++)
        {
            var o = agents[i];
            if (o == a || !o.inBox || o.path == null) continue;
            if (FlatDist(o.position, a.position) > 45f) continue;
            // Their remaining box path vs ours.
            float oFrom = Mathf.Max(o.s - o.halfExtents.z, 0f), oTo = Mathf.Min(o.boxEnd + o.halfExtents.z, o.path.Length);
            for (float sa = box.x; sa <= box.y + 0.01f; sa += 2f)
            {
                Vector3 pa = a.path.PointAt(sa);
                for (float sb = oFrom; sb <= oTo + 0.01f; sb += 2f)
                {
                    Vector3 pb = o.path.PointAt(sb);
                    float dx = pa.x - pb.x, dz = pa.z - pb.z;
                    if (dx * dx + dz * dz < BoxConflictDistance * BoxConflictDistance) return true;
                }
            }
        }
        return false;
    }

    // ---- overtakes ----

    float LateralAt(CarAgent a, float s, float now)
    {
        float lat = 0f;
        if (a.overtake != 0)
        {
            float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a.otStart, a.otStart + a.otRampIn, s));
            float down = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a.otEnd, a.otEnd + OvertakeRamp, s));
            lat = -a.otLateral * Mathf.Min(up, down);
        }
        if (now < a.pullOverUntil + 1.5f)
        {
            float k = now < a.pullOverUntil ? Mathf.Clamp01(1f - (a.pullOverUntil - now - (WorldLifeTuning.Horn.PullOverSeconds - 1f)))
                                            : Mathf.Clamp01((a.pullOverUntil + 1.5f - now) / 1.5f);
            lat += PullOverOffset * Mathf.SmoothStep(0f, 1f, k);
        }
        return lat;
    }

    /// <summary>Overtake a stopped truck in the other lane when the road is straight there and the other lane is clear for
    /// 30 m past it (§7.3).</summary>
    void TryStartOvertake(CarAgent a, TruckControllerV2 truck, float gap)
    {
        var rb = truck.Body;
        if (rb != null && rb.linearVelocity.magnitude > 0.5f) return;
        var path = a.path;
        if (path == null) return;
        // The other lane must be clear from here to 30 m beyond the merge (cars holding back for us don't count): the cheap
        // check first, then the planned line against the truck.
        float start = a.s, end = a.s + a.halfExtents.z + gap + TruckControllerV2.BodyBoxMetres.size.z + OvertakePastTruck + OvertakeRamp;
        if (end > path.Length - 2f) return;
        Vector3 t0 = path.Tangent(path.SegmentAt(start));
        Vector3 rt = new Vector3(t0.z, 0f, -t0.x);
        float clearTo = end + WorldLifeTuning.Traffic.OvertakeClear;
        for (float s = start; s <= clearTo; s += 1.5f)
        {
            Vector3 q = path.PointAt(Mathf.Min(s, path.Length)) - rt * LaneWidth;
            for (int i = 0; i < agents.Count; i++)
            {
                var o = agents[i];
                if (o == a || !o.Shown || o.yieldingTo == a) continue;
                float dx = o.position.x - q.x, dz = o.position.z - q.z;
                if (dx * dx + dz * dz < 9f) return;
            }
            for (int i = 0; i < players.Count; i++)
            {
                var pc = players[i];
                if (pc == null || pc.IsDriving) continue;
                Vector3 pp = pc.transform.position;
                float dx = pp.x - q.x, dz = pp.z - q.z;
                if (dx * dx + dz * dz < 4f) return;
            }
        }
        if (!OvertakePlan(a, truck, gap, out start, out float passEnd, out end, out float lateral, out float rampIn)) return;
        a.overtake = 1;
        a.otStart = start;
        a.otEnd = passEnd;
        a.otRampIn = rampIn;
        a.otLateral = lateral;
        a.otTruck = truck;
        a.courtesyFrom = path.PointAt(start); a.courtesyTo = path.PointAt(end);
        Overtakes++;
    }

    /// <summary>
    /// The line of an overtake round a stopped truck from where this car stands (§7.3): a straight lane with no junction,
    /// driveway or tunnel from here to the merge, and a swing out just far enough to clear the truck (fixer pass: it used to
    /// be a full lane over ~5 m, a ~55° swerve from 3 m behind). Out: the arc where it starts, passes the truck's nose
    /// (+ OvertakePastTruck) and has merged back; how far left it goes and over how long it swings out. The planned line is
    /// checked against the truck's box (a kinematic car shell clipping the truck would shove it): wider, then shorter, until
    /// it's clear. False when the truck can't be passed here.
    /// </summary>
    bool OvertakePlan(CarAgent a, TruckControllerV2 truck, float gap, out float start, out float passEnd, out float end, out float lateral, out float rampIn)
    {
        var path = a.path;
        var bb = TruckControllerV2.BodyBoxMetres;
        start = a.s;
        passEnd = a.s + a.halfExtents.z + gap + bb.size.z + OvertakePastTruck;
        end = passEnd + OvertakeRamp;
        lateral = LaneWidth; rampIn = OvertakeRamp;
        if (path == null || end > path.Length - 2f) return false;
        // Straight lane only (no junctions, driveways or tunnels on the way).
        int s0 = path.SegmentAt(start), s1 = path.SegmentAt(end);
        Vector3 t0 = path.Tangent(s0);
        for (int k = s0; k <= s1; k++)
        {
            var f = path.flags[k];
            if ((f & CarSegFlags.Lane) == 0 || (f & (CarSegFlags.Yield | CarSegFlags.Turn | CarSegFlags.Driveway | CarSegFlags.Tunnel | CarSegFlags.Reverse)) != 0) return false;
            if (Vector3.Dot(path.Tangent(k), t0) < 0.985f) return false;
        }
        var rb = truck.Body;
        Quaternion trot = rb != null ? rb.rotation : truck.transform.rotation;
        Vector3 tc = (rb != null ? rb.position : truck.transform.position) + trot * bb.center;
        Vector3 rt = new Vector3(t0.z, 0f, -t0.x);
        Vector3 tf = trot * Vector3.forward; tf.y = 0f; tf = tf.sqrMagnitude > 1e-6f ? tf.normalized : t0;
        // Where the truck stands across our lane (+ = toward the kerb) and how wide it is across it (it may be at an angle).
        float truckLat = Vector3.Dot(tc - path.PointAt(Mathf.Min(a.s + a.halfExtents.z + gap + bb.extents.z, path.Length)), rt);
        float truckHalf = Mathf.Abs(Vector3.Dot(tf, t0)) * bb.extents.x + Mathf.Abs(Vector3.Dot(tf, rt)) * bb.extents.z;
        float need = truckHalf + a.halfExtents.x + OvertakeSideClear - truckLat;
        if (need > LaneWidth + 0.8f) return false;   // the truck blocks the other lane too
        lateral = Mathf.Clamp(need, OvertakeMinLateral, LaneWidth);
        rampIn = Mathf.Clamp(gap + 3.5f, 5.5f, OvertakeRamp);
        Vector2 tz = new Vector2(tf.x, tf.z), tx = new Vector2(tf.z, -tf.x);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (PlanClear(a, path, start, passEnd, lateral, rampIn, new Vector2(tc.x, tc.z), tx, tz, bb.extents.x + 0.15f, bb.extents.z + 0.15f)) return true;
            if (lateral < LaneWidth - 0.01f) lateral = Mathf.Min(LaneWidth, lateral + 0.5f);
            else if (rampIn > 4.5f) rampIn = Mathf.Max(4.5f, rampIn - 1f);
            else return false;
        }
        return false;
    }

    /// <summary>Does the car's box, driven along the planned overtake line, stay off the truck's (padded) box?</summary>
    static bool PlanClear(CarAgent a, CarPath path, float start, float passEnd, float lateral, float rampIn, Vector2 tc, Vector2 tx, Vector2 tz, float thx, float thz)
    {
        float to = Mathf.Min(passEnd + OvertakeRamp * 0.5f, path.Length);
        for (float s = start; s <= to; s += 0.5f)
        {
            float lat = PlanLateral(s, start, passEnd, lateral, rampIn);
            float slope = (PlanLateral(s + 0.5f, start, passEnd, lateral, rampIn) - PlanLateral(s - 0.5f, start, passEnd, lateral, rampIn));
            Vector3 q = path.PointAt(s, out int seg);
            Vector3 tan = path.Tangent(seg);
            Vector3 rt = new Vector3(tan.z, 0f, -tan.x);
            q += rt * lat;
            // Heading: the lane's plus the swing's slope (lateral per metre).
            Vector3 fw = (tan + rt * slope); fw.y = 0f; fw.Normalize();
            Vector2 az = new Vector2(fw.x, fw.z), ax = new Vector2(fw.z, -fw.x);
            Vector2 d = tc - new Vector2(q.x, q.z);
            float ahx = a.halfExtents.x, ahz = a.halfExtents.z;
            if (Sep(ax, d, ax, az, ahx, ahz, tx, tz, thx, thz) || Sep(az, d, ax, az, ahx, ahz, tx, tz, thx, thz)
                || Sep(tx, d, ax, az, ahx, ahz, tx, tz, thx, thz) || Sep(tz, d, ax, az, ahx, ahz, tx, tz, thx, thz)) continue;
            return false;
        }
        return true;
    }

    static float PlanLateral(float s, float start, float passEnd, float lateral, float rampIn)
    {
        float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, start + rampIn, s));
        float down = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(passEnd, passEnd + OvertakeRamp, s));
        return -lateral * Mathf.Min(up, down);
    }

    /// <summary>
    /// Courtesy (fixer pass): an oncoming car holds back at the far end of a stuck car's overtake corridor while that car
    /// asks for it (<see cref="CarAgent.courtesyUntil"/>), and carries on if it's already inside the corridor. Bumper distance
    /// to the hold line, or +∞. A car that has held for <see cref="CourtesyMaxHold"/> s ignores new asks until it's moving.
    /// </summary>
    float CourtesyGate(CarAgent a, float look, float now, float dt)
    {
        var was = a.yieldingTo;
        a.yieldingTo = null;
        if (a.v > 2f) a.courtesyHeld = 0f;
        if (a.overtake != 0 || a.parkedShell || a.courtesyHeld > CourtesyMaxHold) return float.PositiveInfinity;
        Vector3 af = ForwardOf(a);
        float best = float.PositiveInfinity;
        for (int i = 0; i < agents.Count; i++)
        {
            var o = agents[i];
            if (o == a || now > o.courtesyUntil) continue;
            Vector3 d = o.courtesyTo - o.courtesyFrom; d.y = 0f;
            float len = d.magnitude;
            if (len < 1f) continue;
            d /= len;
            if (Vector3.Dot(af, d) > -0.7f) continue;                       // not coming the other way
            Vector3 rel = a.position - o.courtesyFrom; rel.y = 0f;
            float lat = Vector3.Dot(rel, new Vector3(d.z, 0f, -d.x));
            if (lat > -2f || lat < -8.5f) continue;                          // not in the other lane (the asker's left)
            float nose = Vector3.Dot(rel, d) - a.halfExtents.z;              // we drive toward −d
            float line = len + CourtesyHoldBeyond;
            if (nose < line - 0.5f) continue;                                // already inside the corridor: carry on through
            float g = nose - line;
            if (g > look + 6f || g >= best) continue;
            best = g; a.yieldingTo = o;
        }
        if (a.yieldingTo != null && a.v < 0.6f) a.courtesyHeld += dt;
        if (a.yieldingTo == null && was != null) a.courtesyHeld = 0f;
        return best;
    }

    void Honk(CarAgent a, int why, float now)
    {
        a.lastHonk = now;
        a.honkCount++;
        Honks++;
        if (a.distance < 120f)
        {
            Vector3 at = a.position + Vector3.up * (a.halfExtents.y * 2f + 0.6f);
            KnockSfx.Play(WorldLifeIds.Sfx.CarHorn, "horn", at, 0.6f, 0.9f + 0.05f * (a.car & 7));
            var hl = ResidentLines.Honk;
            string line = why == 0 ? hl[(a.honkCount + (a.car & 7)) % hl.Length] : why == 1 ? "BEEP BEEP!" : "HONK!";
            if (a.distance < 60f) GameHUD.FloatingText(at, line, new Color(1f, 0.85f, 0.35f), 0.9f);
        }
        var h = CarHonked;
        if (h != null) try { h(a, why); } catch (Exception e) { Debug.LogException(e); }
    }


    // ------------------------------------------------------------------ hits (presentation of E's decisions)

    /// <summary>Resolve a stable car on the host, even when its visual shell is outside the host's LOD radius.</summary>
    public bool TryImpactBody(LifeId id, out ILifeBody body)
    {
        body = LifeBodies.Find(id);
        if (body != null && body.BodyKind == LifeBodyKind.Car) return true;
        body = null;
        int ci = id.index;
        if (id.kind != LifeKind.Car || sim == null || day == null || ctx == null || ci >= phase.Length) return false;
        if (phase[ci] == CarPhase.Home)
        {
            UpdateParkedBody(ci, true);
            body = parked[ci];
            return body != null;
        }
        if ((phase[ci] != CarPhase.Out && phase[ci] != CarPhase.In) || tripOf[ci] < 0 || tripOf[ci] >= day.trips.Count) return false;
        var tr = day.trips[tripOf[ci]];
        bool outbound = phase[ci] == CarPhase.Out;
        var path = outbound ? tr.outPath : tr.inPath;
        if (path == null) return false;
        float elapsed = (float)((day.MsOf(ctx.Hours) - delayMs[ci] - (outbound ? tr.depart : tr.ret)) * .001);
        float arc = path.ArcAtTime(elapsed, out _, out _);
        Vector3 position = path.PointAt(arc, out int segment);
        body = new ImpactCarBody(id, position, Quaternion.LookRotation(path.Tangent(segment)),
            sim.Roster.households[sim.Roster.cars[ci].household].stopId);
        return true;
    }

    /// <summary>
    /// A guest can be delayed by traffic that is outside the host's visual radius. Accept its observed car position
    /// only along that stable car's real scheduled route in a 35-second lag window (plus 3m prediction), never an
    /// arbitrary town coordinate. Parked cars stay tied to their actual driveway. Host immunity still decides once.
    /// </summary>
    public bool TryImpactBodyAt(LifeId id, Vector3 seenAt, out ILifeBody body)
    {
        body = null;
        if (!VehicleCrashRules.Finite(seenAt) || id.kind != LifeKind.Car) return false;
        var existing = LifeBodies.Find(id);
        if (existing != null && existing.BodyKind == LifeBodyKind.Car &&
            (!existing.Hittable || (existing.Position - seenAt).sqrMagnitude <= 9f))
        { body = existing; return true; }
        int ci = id.index;
        if (sim == null || day == null || ctx == null || ci >= phase.Length || ci >= day.tripStart.Length) return false;
        if (phase[ci] == CarPhase.Home && hcOf[ci] != null && (hcOf[ci].SlotPose.position - seenAt).sqrMagnitude <= 9f)
        { UpdateParkedBody(ci, true); body = parked[ci]; return body != null; }
        double t = day.MsOf(ctx.Hours) - delayMs[ci];
        const float lagSeconds = 35f;
        for (int i = day.tripStart[ci]; i < day.tripStart[ci] + day.tripCount[ci]; i++)
        {
            var tr = day.trips[i];
            for (int leg = 0; leg < 2; leg++)
            {
                var path = leg == 0 ? tr.outPath : tr.inPath;
                double start = leg == 0 ? tr.depart : tr.ret, end = leg == 0 ? tr.awayFrom : tr.park;
                if (path == null || t < start || t > end + lagSeconds * 1000f) continue;
                float elapsed = (float)((t - start) * .001);
                float min = path.ArcAtTime(Mathf.Max(0f, elapsed - lagSeconds), out _, out _);
                float max = Mathf.Min(path.Length, path.ArcAtTime(elapsed, out _, out _) + 3f);
                if (!VehicleCrashRules.TryRoutePoint(seenAt, path.pts, path.s, min, max, out float arc)) continue;
                Vector3 direction = path.Tangent(path.SegmentAt(arc));
                body = new ImpactCarBody(id, seenAt, Quaternion.LookRotation(direction),
                    sim.Roster.households[sim.Roster.cars[ci].household].stopId);
                return true;
            }
        }
        return false;
    }

    sealed class ImpactCarBody : ILifeBody
    {
        public LifeId Id { get; }
        public LifeBodyKind BodyKind => LifeBodyKind.Car;
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 HalfExtents => new Vector3(1.1f, .9f, 2.5f);
        public bool Hittable => true;
        public string StopId { get; }
        public ImpactCarBody(LifeId id, Vector3 position, Quaternion rotation, string stop)
        { Id = id; Position = position; Rotation = rotation; StopId = stop; }
        public void PlayHit(in NpcHit hit) { } // host validation data only; presentation is resolved separately below
    }

    /// <summary>One host-confirmed hit on this machine. Offscreen cars need no shell, but still receive the delay.</summary>
    public void PresentNetworkImpact(in NpcHit hit)
    {
        var body = LifeBodies.Find(hit.target);
        if (body != null && body.BodyKind == LifeBodyKind.Car)
        {
            if (body is CarAgent car && car.path != null && (car.position - hit.point).sqrMagnitude > .25f &&
                VehicleCrashRules.TryRoutePoint(hit.point, car.path.pts, car.path.s, 0f, car.path.Length, out float arc))
            {
                car.s = arc;
                car.slide = hit.point - car.path.PointAt(arc);
                car.position = hit.point;
            }
            body.PlayHit(in hit); return;
        }
        int ci = hit.target.index;
        if (ctx == null || day == null || ci >= phase.Length || FlatDist(hit.point, ctx.Viewer) > HideBeyond) return;
        if (phase[ci] == CarPhase.Home)
        {
            UpdateParkedBody(ci, true);
            if (parked[ci] != null) OnParkedHit(parked[ci], hit);
        }
        else if ((phase[ci] == CarPhase.Out || phase[ci] == CarPhase.In) && tripOf[ci] >= 0)
        {
            var a = Spawn(ci, day.MsOf(ctx.Hours));
            if (a != null) StartShunt(a, hit, Time.time, false);
        }
    }

    void OnAgentHit(CarAgent a, NpcHit hit)
    {
        float now = Time.time;
        switch (hit.kind)
        {
            case NpcHitKind.Shunt: StartShunt(a, hit, now, false); break;
            case NpcHitKind.PullOver:
                if (a.parkedShell) break;
                a.pullOverUntil = now + WorldLifeTuning.Horn.PullOverSeconds;
                break;
            case NpcHitKind.Startle:
                if (now - a.lastHonk > 1.5f) Honk(a, 2, now);
                break;
        }
    }

    /// <summary>
    /// G: the sideways part of a shunt's slide, so the car ends up beside the truck's path instead of under it. The
    /// §8.3 slide (≤ 2 m along the truck's velocity) left a car that was hit square-on (rear-ended in its lane, or nosed
    /// into in its driveway) inside the truck's footprint, and with its collider off the truck drove straight through it.
    /// Pushed toward the side of the truck's line the car is already on (the seed's side when dead centre), just far
    /// enough to clear the truck's half-width (≤ 3 m). Presentation only; the truck may differ a little between machines.
    /// </summary>
    static Vector3 ShoveAside(CarAgent a, in NpcHit hit, float seedSign)
    {
        Vector3 tv = hit.truckVelocity; tv.y = 0f;
        if (tv.sqrMagnitude < 0.01f) return Vector3.zero;
        Vector3 tf = tv.normalized, tr = new Vector3(tf.z, 0f, -tf.x);
        float lateral = 0f, side = seedSign;
        if (hit.truck != null && hit.truck.Body != null)
        {
            Vector3 rel = a.position - hit.truck.Body.position; rel.y = 0f;
            lateral = Vector3.Dot(rel, tr);
            if (Mathf.Abs(lateral) > 0.15f) side = Mathf.Sign(lateral);
        }
        Vector3 cf = Quaternion.Euler(0f, a.yaw, 0f) * Vector3.forward, cr = new Vector3(cf.z, 0f, -cf.x);
        float carHalfLat = Mathf.Abs(Vector3.Dot(cr, tr)) * a.halfExtents.x + Mathf.Abs(Vector3.Dot(cf, tr)) * a.halfExtents.z;
        float clear = TruckControllerV2.BodyBoxMetres.extents.x + carHalfLat + 0.25f - Mathf.Abs(lateral);
        return clear > 0f ? tr * (side * Mathf.Min(clear, 3f)) : Vector3.zero;
    }

    void StartShunt(CarAgent a, in NpcHit hit, float now, bool parkedCar)
    {
        Shunts++;
        ulong h = LifeHash.Mix((ulong)hit.seed * 0x9E3779B97F4A7C15UL ^ a.id.Packed);
        float sign = (h & 1) == 0 ? 1f : -1f;
        a.shuntT = 0f;
        a.shuntImpactSpeed = new Vector2(hit.truckVelocity.x, hit.truckVelocity.z).magnitude;
        a.shuntYawKick = sign * VehicleCrashRules.CarYawKick(a.shuntImpactSpeed);
        a.shuntRoll = -sign * Mathf.Lerp(4f, 11f, Mathf.InverseLerp(2.5f, 22f, a.shuntImpactSpeed));
        a.crooked = true;
        Vector3 slide = hit.truckVelocity; slide.y = 0f;
        if (slide.sqrMagnitude > .01f) slide = slide.normalized * VehicleCrashRules.CarSlideDistance(a.shuntImpactSpeed);
        slide += ShoveAside(a, hit, sign);
        slide = Vector3.ClampMagnitude(slide, 7f);
        a.shuntSlide = slide;
        a.shuntSlideFrom = a.slide;
        a.SetCollider(false);
        a.colliderOffUntil = now + WorldLifeTuning.Hits.ShuntColliderOffSeconds;
        a.immuneUntil = now + WorldLifeTuning.Hits.CarImmunitySeconds;
        a.alarmUntil = now + WorldLifeTuning.Hits.CarAlarmSeconds;
        a.holdUntil = now + WorldLifeTuning.Hits.DriverPopOutDelay + WorldLifeTuning.Hits.DriverFistSeconds + 1.2f;
        a.v = 0f;
        a.overtake = 0;
        a.driverPopped = false;
        a.parkedShell = parkedCar;
        a.nextAlarmBeep = now + 0.35f;   // the skid first, then the alarm
        if (ctx != null && FlatDist(a.position, ctx.Viewer) < 80f) KnockSfx.Play(WorldLifeIds.Sfx.TireScreech, "squeak", a.position, 0.45f, 1.15f);
    }

    void TickShunt(CarAgent a, float now, float dt)
    {
        a.shuntT += dt;
        float t = a.shuntT;
        // Momentum-driven skid, visible spin and suspension hop, then regain the lane without disappearing.
        a.shuntYaw = VehicleCrashRules.CarYaw(t, a.shuntYawKick);
        float hop = Mathf.Lerp(.16f, .55f, Mathf.InverseLerp(2.5f, 22f, a.shuntImpactSpeed));
        a.shuntHop = t < .65f ? hop * Mathf.Sin(t / .65f * Mathf.PI) : 0f;
        a.shuntPitchTarget = t < .5f ? -9f * Mathf.Sin(t / .5f * Mathf.PI) : 0f;
        a.slide = a.shuntSlideFrom + a.shuntSlide * VehicleCrashRules.CarSlideFraction(t);
        // Car alarm: whoop-whoop for 6 s.
        if (now < a.alarmUntil && now >= a.nextAlarmBeep && a.distance < 80f)
        {
            a.nextAlarmBeep = now + 0.45f;
            KnockSfx.Play(WorldLifeIds.Sfx.CarAlarm, "beep", a.position + Vector3.up, 0.42f, Mathf.Repeat(now, 0.9f) < 0.45f ? 1f : 0.8f);
        }
        // The driver pops out after 0.5 s, yells, shakes a fist for 3 s and gets back in. Fixer pass: not while a truck is
        // still rolling past their door (they'd be flattened getting out, which reads as unfair): they wait in the car until
        // it has slowed or gone (at most DriverWaitMax s), then get out and shake a fist at it. The car holds meanwhile.
        if (!a.driverPopped && t >= WorldLifeTuning.Hits.DriverPopOutDelay && !a.parkedShell)
        {
            Vector3 side = a.position + Quaternion.Euler(0f, a.yaw, 0f) * new Vector3(-(a.halfExtents.x + 0.7f), 0f, 0.3f);
            bool wait = t < WorldLifeTuning.Hits.DriverPopOutDelay + DriverWaitMax && TruckRollingNear(side, DriverWaitRadius, DriverWaitSpeed);
            a.holdUntil = Mathf.Max(a.holdUntil, now + WorldLifeTuning.Hits.DriverFistSeconds + 1.2f);
            if (!wait)
            {
                a.driverPopped = true;
                a.driverOut = true;
                a.driverPopT = t;
                var h = DriverPoppedOut;
                if (h != null) { try { h(a, a.driver, side); } catch (Exception e) { Debug.LogException(e); } }
                else if (a.distance < 60f)
                {
                    var sl = ResidentLines.DriverFor(ResidentCast.IsValid(a.driver) ? ResidentCast.Style(a.driver).district : (byte)0);
                    GameHUD.FloatingText(side + Vector3.up * 2.1f, sl[(int)(LifeHash.Mix(a.id.Packed + (ulong)Shunts) % (ulong)sl.Length)], new Color(1f, 0.45f, 0.35f), 1.1f);
                }
            }
        }
        if (a.driverOut && t >= a.driverPopT + WorldLifeTuning.Hits.DriverFistSeconds) a.driverOut = false;
        if (t > 1.6f && now >= a.holdUntil)
        {
            a.shuntT = -1f; a.shuntYaw = 0f; a.shuntHop = 0f; a.shuntPitchTarget = 0f;
        }
    }


    /// <summary>A shunted car's driver (or a parked car's owner, package C) waits to get out while a truck moving faster
    /// than <see cref="DriverWaitSpeed"/> is within <see cref="DriverWaitRadius"/> m of where they'd stand, for at most
    /// <see cref="DriverWaitMax"/> s (fixer pass: the owner of a parked car could be flattened by the same truck still
    /// rolling up the driveway).</summary>
    public const float DriverWaitRadius = 7f, DriverWaitSpeed = 1.5f, DriverWaitMax = 6f;

    /// <summary>Is any truck moving at ≥ <paramref name="minSpeed"/> m/s with its body box within <paramref name="radius"/> m
    /// of <paramref name="p"/> (flat)? Presentation helper (C's car scenes use it too).</summary>
    public static bool TruckRollingNear(Vector3 p, float radius, float minSpeed)
    {
        var trucks = TruckControllerV2.All;
        var bb = TruckControllerV2.BodyBoxMetres;
        for (int i = 0; i < trucks.Count; i++)
        {
            var tk = trucks[i];
            if (tk == null || !tk.isActiveAndEnabled) continue;
            var rb = tk.Body;
            if (rb == null) continue;
            Vector3 v = rb.linearVelocity; v.y = 0f;
            if (v.sqrMagnitude < minSpeed * minSpeed) continue;
            Vector3 l = Quaternion.Inverse(rb.rotation) * (p - rb.position) - bb.center;
            float dx = Mathf.Max(0f, Mathf.Abs(l.x) - bb.extents.x), dz = Mathf.Max(0f, Mathf.Abs(l.z) - bb.extents.z);
            if (dx * dx + dz * dz <= radius * radius) return true;
        }
        return false;
    }

    /// <summary>A parked car was shunted: a shell stands in for the static twin (crooked now), the owner storms out.</summary>
    void OnParkedHit(ParkedCarBody pb, NpcHit hit)
    {
        if (hit.kind != NpcHitKind.Shunt) return;
        int car = pb.car;
        if (agentOf[car] != null) return;
        var a = TakeShell(true);
        if (a == null) return;
        var rc = sim.Roster.cars[car];
        Dress(a, car, LifeId.Car(car), hcOf[car], LifeId.Resident(rc.driver), pb.stopId);
        var hc = pb.household;
        var pose = hc.SlotPose;
        a.path = null; a.trip = -1; a.outbound = false; a.s = 0f; a.v = 0f;
        a.position = pose.position; a.yaw = pose.rotation.eulerAngles.y;
        a.Show(true);
        a.SetDriverVisible(false);
        a.Pose(a.position, a.yaw, 0f, false);
        agents.Add(a);
        agentOf[car] = a;
        UpdateParkedBody(car, false);     // hand the id over before the shell registers (see Spawn)
        LifeBodies.Register(a);
        StartShunt(a, hit, Time.time, true);
        pb.immuneUntil = Time.time + WorldLifeTuning.Hits.CarImmunitySeconds;
        ShowStatic(car, false);
        var owner = LifeId.Resident(rc.driver);
        var h = ParkedCarShunted;
        if (h != null) { try { h(LifeId.Car(car), owner, pb.stopId, pose.position); } catch (Exception e) { Debug.LogException(e); } }
        else
        {
            var g = sim.Graph;
            Vector3 door = g.nodes[g.houses[rc.house].door].pos;
            GameHUD.FloatingText(door + Vector3.up * 2.2f, "HEY!! THAT'S MY CAR!", new Color(1f, 0.4f, 0.3f), 1.1f);
        }
    }

    /// <summary>A parked car's stand-in shell: plays the shunt, stays crooked; goes back to the static twin when the camera
    /// has been far for 10 s, and becomes the moving car if its trip starts.</summary>
    void TickParkedShell(CarAgent a, float now, float dt, Vector3 cam)
    {
        a.distance = FlatDist(a.position, cam);
        if (a.shuntT >= 0f) TickShunt(a, now, dt);
        Vector3 basePos = a.household != null ? a.household.SlotPose.position : a.position - a.slide;
        float baseYaw = a.household != null ? a.household.SlotPose.rotation.eulerAngles.y : a.yaw;
        a.v = 0f;
        a.Pose(basePos + a.slide, baseYaw + (a.shuntT >= 0f ? a.shuntYaw : 0f), dt, a.distance <= WorldLifeTuning.Lod.FullDistance);
        bool alarm = now < a.alarmUntil;
        a.SetLights(alarm && Mathf.Repeat(now, 0.4f) < 0.2f, alarm && Mathf.Repeat(now, 0.4f) >= 0.2f, true);
        if (now >= a.colliderOffUntil && !a.box.enabled && !TruckOverlaps(a)) a.SetCollider(true);
        int car = a.car;
        bool leaving = car >= 0 && phase[car] != CarPhase.Home;
        if (a.distance > 120f) parkedFarSince[car] += dt; else parkedFarSince[car] = 0f;
        if (VehicleCrashRules.MayReleaseParkedCrash(leaving, parkedFarSince[car], a.shuntT >= 0f, now < a.holdUntil))
        {
            parkedFarSince[car] = 0f;
            Release(a, true);
        }
    }

    /// <summary>Does any truck's body box (padded 0.4 m) overlap this car's box? A shunted car's collider only comes back
    /// once the truck is clear, so PhysX never has to shove a truck out of a car (yaw-only boxes, separating axes).</summary>
    static bool TruckOverlaps(CarAgent a) => TruckOverlapsAt(a.position, a.yaw, a.halfExtents, 0.4f);

    /// <summary>Would a car box at <paramref name="pos"/> / <paramref name="yaw"/> (padded) overlap any truck's body box?</summary>
    static bool TruckOverlapsAt(Vector3 pos, float yaw, Vector3 halfExtents, float pad)
    {
        var trucks = TruckControllerV2.All;
        var bb = TruckControllerV2.BodyBoxMetres;
        Vector3 af = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector2 aZ = new Vector2(af.x, af.z), aX = new Vector2(af.z, -af.x);
        Vector2 ac = new Vector2(pos.x, pos.z);
        for (int i = 0; i < trucks.Count; i++)
        {
            var tk = trucks[i];
            if (tk == null || !tk.isActiveAndEnabled) continue;
            var rb = tk.Body;
            Vector3 tp = rb != null ? rb.position : tk.transform.position;
            Quaternion tr = rb != null ? rb.rotation : tk.transform.rotation;
            Vector3 c3 = tp + tr * bb.center;
            Vector3 tf = tr * Vector3.forward; tf.y = 0f; tf = tf.sqrMagnitude > 1e-6f ? tf.normalized : Vector3.forward;
            Vector2 bZ = new Vector2(tf.x, tf.z), bX = new Vector2(tf.z, -tf.x);
            Vector2 d = new Vector2(c3.x, c3.z) - ac;
            float ahx = halfExtents.x + pad, ahz = halfExtents.z + pad, bhx = bb.extents.x, bhz = bb.extents.z;
            if (Sep(aX, d, aX, aZ, ahx, ahz, bX, bZ, bhx, bhz) || Sep(aZ, d, aX, aZ, ahx, ahz, bX, bZ, bhx, bhz)
                || Sep(bX, d, aX, aZ, ahx, ahz, bX, bZ, bhx, bhz) || Sep(bZ, d, aX, aZ, ahx, ahz, bX, bZ, bhx, bhz)) continue;
            return true;
        }
        return false;
    }

    static bool Sep(Vector2 axis, Vector2 d, Vector2 aX, Vector2 aZ, float ahx, float ahz, Vector2 bX, Vector2 bZ, float bhx, float bhz)
    {
        float ra = Mathf.Abs(Vector2.Dot(aX, axis)) * ahx + Mathf.Abs(Vector2.Dot(aZ, axis)) * ahz;
        float rb = Mathf.Abs(Vector2.Dot(bX, axis)) * bhx + Mathf.Abs(Vector2.Dot(bZ, axis)) * bhz;
        return Mathf.Abs(Vector2.Dot(d, axis)) > ra + rb;
    }

    // ------------------------------------------------------------------ interim sensor (until package E)

    readonly List<ILifeBody> sensed = new List<ILifeBody>(8);
    readonly Dictionary<uint, int> lastClaimTick = new Dictionary<uint, int>(32);

    /// <summary>The truck's predictive box this FixedUpdate (before physics): claim the cars in it. The no-op authority
    /// decides a shunt at ≥ 2.5 m/s and presents it (the car's collider goes off this step); the truck gets the §8.3 bump.</summary>
    public void Sense(in TruckSweep sweep)
    {
        if (!InterimSensor || !(WorldLifeRunner.Authority is NullAuthority)) return;
        sensed.Clear();
        LifeBodies.QueryBox(sweep.center, sweep.rotation, sweep.halfExtents, sensed, LifeBodyKind.Car, WorldLifeTuning.Hits.TruckBoxPad, true);
        for (int i = 0; i < sensed.Count; i++)
        {
            var b = sensed[i];
            uint key = b.Id.Packed;
            if (lastClaimTick.TryGetValue(key, out int last) && sweep.tick - last < 8) continue;
            lastClaimTick[key] = sweep.tick;
            var claim = new NpcHitClaim { target = b.Id, tick = sweep.tick, truckVelocity = sweep.velocity, actorSlot = 0, truck = sweep.truck };
            var hit = WorldLifeRunner.Authority.ClaimNpcHit(in claim);
            if (hit.kind != NpcHitKind.Shunt || sweep.body == null) continue;
            // §8.3 truck side (E owns this once it lands): −35 % speed, a 6°/s yaw kick, a shake and "CRUNCH!".
            var rb = sweep.body;
            Vector3 v = rb.linearVelocity;
            rb.linearVelocity = new Vector3(v.x * (1f - WorldLifeTuning.Hits.ShuntTruckSpeedLoss), v.y, v.z * (1f - WorldLifeTuning.Hits.ShuntTruckSpeedLoss));
            float sign = Vector3.Dot(b.Position - rb.position, rb.rotation * Vector3.right) >= 0f ? -1f : 1f;
            rb.angularVelocity += Vector3.up * (sign * WorldLifeTuning.Hits.ShuntTruckYawKickDegPerSec * Mathf.Deg2Rad);
            Juice.Shake(0.22f, 0.2f);
            Vector3 at = b.Position + Vector3.up * 2.2f;
            KnockSfx.Play("crunch", "crunch", at, 0.9f, 1f);
            GameHUD.FloatingText(at, "CRUNCH!", new Color(1f, 0.6f, 0.3f), 1.2f);
        }
        sensed.Clear();
    }

    // ------------------------------------------------------------------ the boop (§7.3, NPC car vs a player on foot)

    void BoopLocalPlayer(float now)
    {
        if (now < boopImmuneUntil) return;
        for (int i = 0; i < players.Count; i++)
        {
            var pc = players[i];
            if (pc == null || !pc.IsOwner || pc.IsDriving || !pc.isActiveAndEnabled) continue;   // the player's own machine only
            Vector3 pp = pc.transform.position;
            for (int k = 0; k < agents.Count; k++)
            {
                var a = agents[k];
                if (a.parkedShell || a.path == null || Mathf.Abs(a.v) < 1f || FlatDist(a.position, pp) > 14f) continue;
                // Inside the car's box, or in front of it closer than it can stop: "stepped out at the last moment".
                float reach = a.v * a.v / (2f * EmergencyDecel) + 0.6f;
                Vector3 l = Quaternion.Euler(0f, -a.yaw, 0f) * (pp - a.position);
                bool reversing = (a.path.flags[a.path.SegmentAt(a.s)] & CarSegFlags.Reverse) != 0;
                float front = a.halfExtents.z + 0.3f + (reversing ? 0f : reach), back = a.halfExtents.z + 0.3f + (reversing ? reach : 0f);
                if (Mathf.Abs(l.x) > a.halfExtents.x + 0.35f || l.z > front || l.z < -back || l.y > 2.2f || l.y < -0.6f) continue;
                var kb = pc.GetComponent<CritterKnockback>();
                if (kb == null) kb = pc.gameObject.AddComponent<CritterKnockback>();
                Vector3 side = Quaternion.Euler(0f, a.yaw, 0f) * new Vector3(l.x >= 0f ? 1f : -1f, 0f, reversing ? -0.6f : 0.6f);
                kb.Knock(side, 1.1f, 0.2f, WorldLifeTuning.Hits.BoopStumbleSeconds);
                boopImmuneUntil = now + WorldLifeTuning.Hits.BoopImmunitySeconds;
                Boops++;
                GameHUD.FloatingText(pp + Vector3.up * 2.2f, "BOOP!", new Color(1f, 0.8f, 0.4f), 1f);
                Juice.Shake(0.08f, 0.15f);
                Honk(a, 1, now);
                a.v = 0f;
                return;
            }
        }
    }

    // ------------------------------------------------------------------ debug / tests

    /// <summary>Tests: a one-off car from lane node <paramref name="from"/> to <paramref name="to"/> starting now (at rest),
    /// driven by the same rules as the timetable cars. Returns the shell, or null when no route / no free shell.</summary>
    public CarAgent DebugTrip(int from, int to, int mask = -1)
    {
        if (sim == null || ctx == null) return null;
        var nodes = sim.Route(from, to, LifeRouteMode.Car, mask, -1);
        if (nodes == null || nodes.Length < 2) return null;
        var p = new List<Vector3>(); var f = new List<CarSegFlags>(); var e = new List<int>(); var l = new List<float>(); var c = new List<float>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int edge = -1;
            if (i + 1 < nodes.Length)
                for (int k = sim.Graph.OutBegin(nodes[i]); k < sim.Graph.OutEnd(nodes[i]); k++)
                    if (sim.Graph.OutTo(k) == nodes[i + 1] && sim.Graph.edges[sim.Graph.OutEdge(k)].kind == StreetGraph.EdgeKind.Road) { edge = sim.Graph.OutEdge(k); break; }
            var fl = CarSegFlags.Lane; float lim = LifeSim.LaneLimit;
            bool entry = false;
            if (edge >= 0)
            {
                var ef = sim.Graph.edges[edge].flags;
                if ((ef & StreetGraph.EdgeFlags.Yield) != 0) { fl |= CarSegFlags.Yield; lim = LifeSim.YieldLimit; entry = i == 0 || !(f.Count > 0 && (f[f.Count - 1] & CarSegFlags.Yield) != 0); }
                if ((ef & StreetGraph.EdgeFlags.Turn) != 0) { fl |= CarSegFlags.Turn; lim = Mathf.Min(lim, LifeSim.TurnLimit); }
                if ((ef & StreetGraph.EdgeFlags.Pad) != 0) { fl |= CarSegFlags.Pad; lim = Mathf.Min(lim, WorldLifeTuning.Traffic.PadSpeed); }
            }
            p.Add(sim.Graph.nodes[nodes[i]].pos); f.Add(fl); e.Add(edge); l.Add(lim); c.Add(entry ? LifeSim.BoxEntryLimit : float.MaxValue);
        }
        var path = CarPath.Build(p, f, e, l, c, 0f, 0f);
        var a = TakeShell(false);
        if (a == null) return null;
        // A fresh id per test car (fixer pass: 60000 + the live count reused an id still driving after an earlier one
        // finished, so LifeBodies warned "Two bodies share id" for every test stream).
        Dress(a, -1, LifeId.Car(60000 + (debugSerial++ % 5000)), null, LifeId.None, "");
        a.path = path; a.trip = -1; a.outbound = true; a.s = 0f; a.v = 0f;
        a.yaw = HeadingAt(a, 0f, true);
        a.position = path.PointAt(0f);
        a.Show(true);
        a.Pose(a.position, a.yaw, 0f, false);
        agents.Add(a);
        LifeBodies.Register(a);
        debugCars.Add(new DebugCar { agent = a, start = Time.timeAsDouble, path = path });
        return a;
    }

    /// <summary>"cars 86 · moving 11 · shown 9 (max 12) · honks 3 · overtakes 1 · boops 0 · shunts 0".</summary>
    public string Describe() =>
        $"cars {phase.Length} · moving {MovingNow} · shown {ShownMoving} (max {MaxShown}) · shells {shellsMade} · honks {Honks} · overtakes {Overtakes} · boops {Boops} · shunts {Shunts}";
}

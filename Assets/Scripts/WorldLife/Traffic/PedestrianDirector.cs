using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package D (Docs/WorldLife.md §4.2, §7.1): the Schedule director. It owns <see cref="LifeSim.Active"/> (the
/// roster, routes and today's timetable, rebuilt at the day roll or when a district unlocks) and works out, every frame,
/// which residents are out on foot and where their timetable puts them. Package C draws them: it reads
/// <see cref="Out"/> (or listens to <see cref="Appeared"/> / <see cref="Left"/>) and drives an External puppet from each
/// <see cref="Pedestrian"/>'s pose. Interruptions (flops) are C's; the host's delay (<see cref="LifeOverrides"/>) shifts the
/// timetable here, so the drawn position resumes where the person was.
///
/// <para>Cost (§4.4, WorldLife.Schedule ≤ 0.15 ms): one integer compare per resident per frame; positions only for
/// people out on foot whose excursion comes within <see cref="WorldLifeTuning.Lod.RelevanceDistance"/> of the camera.
/// 0 B per frame (pooled records, no LINQ).</para>
/// </summary>
public sealed class PedestrianDirector : WorldLifeDirector
{
    public static PedestrianDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Schedule;
    public override string Name => "PedestrianDirector";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Instance = new PedestrianDirector();
        WorldLifeRunner.Register(Instance);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; Appeared = null; Left = null; }

    /// <summary>A resident out on foot right now (pooled; valid until <see cref="Left"/>).</summary>
    public sealed class Pedestrian
    {
        public LifeId id;
        public int resident;
        public int excursion;
        public ExcursionKind kind;
        /// <summary>This frame's timetable pose (feet on the path) — only refreshed while <see cref="relevant"/>.</summary>
        public Vector3 position;
        public float yaw;
        public float speed;
        public LifeGait gait;
        /// <summary>Within <see cref="WorldLifeTuning.Lod.RelevanceDistance"/> of the camera (pose is fresh).</summary>
        public bool relevant;
        public float distance;
        /// <summary>Their dog is with them (LifeId.Dog), else None.</summary>
        public LifeId dog;
        /// <summary>Gossip partner (LifeId.Resident), else None.</summary>
        public LifeId partner;
        /// <summary>Walks to the car and gets in / got out of the car (C: pop into / out of the car, not the door).</summary>
        public bool intoCar, fromCar;
        /// <summary>0..1: squashed into the door (or car) at the very start and end of the excursion, 1 = fully out.</summary>
        public float pop;
        public string stopId = "";
        public int lastFrame;
    }

    public static event Action<Pedestrian> Appeared;
    public static event Action<Pedestrian> Left;

    /// <summary>Everyone out on foot town-wide (unordered).</summary>
    public IReadOnlyList<Pedestrian> Out => outList;
    public int RelevantCount { get; private set; }

    /// <summary>The record for a resident who is out, or null.</summary>
    public Pedestrian Find(LifeId id) => id.kind == LifeKind.Resident && id.index < byResident.Length ? byResident[id.index] : null;

    /// <summary>C (walk-outs): don't present this resident from the timetable until this GameClock.TotalHours (the
    /// commuter who carries the bag out on the way to the car is C's puppet for that walk).</summary>
    public void Suppress(LifeId resident, double untilHours)
    {
        if (resident.kind != LifeKind.Resident || resident.index >= suppressUntil.Length) return;
        suppressUntil[resident.index] = untilHours;
    }

    // ------------------------------------------------------------------ state

    LifeSim sim;
    LifeDay day;
    int dayIndex = int.MinValue, mask;
    readonly List<Pedestrian> outList = new List<Pedestrian>(64);
    readonly Stack<Pedestrian> pool = new Stack<Pedestrian>(64);
    Pedestrian[] byResident = new Pedestrian[0];
    double[] nextCheck = new double[0];
    double[] suppressUntil = new double[0];
    int[] delayMs = new int[0];
    int overridesVersion = -1;
    public float LastBuildMs { get; private set; }

    public LifeSim Sim => sim;

    public override void Wake(WorldLifeContext ctx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        sim = LifeSim.Create(ctx.Graph, ctx.TownSeed, LineBlocked);
        LifeSim.Active = sim;
        int n = sim.Roster.ResidentCount;
        byResident = new Pedestrian[n];
        nextCheck = new double[n];
        suppressUntil = new double[n];
        delayMs = new int[n];
        overridesVersion = -1;
        dayIndex = int.MinValue;
        EnsureDay(ctx);
        WalkOutPresenter.CarrierPicker = PickCarrier;
        sw.Stop();
        LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
        Debug.Log($"[WorldLife] Schedules: {sim.Roster.Describe().Replace("\n", ";")}; day {ctx.Day} built in {LastBuildMs:F1} ms " +
                  $"({day.trips.Count} car trips, {day.excursions.Count} walks).");
    }

    public override void Sleep()
    {
        for (int i = outList.Count - 1; i >= 0; i--) Remove(outList[i]);
        outList.Clear();
        if (WalkOutPresenter.CarrierPicker == (Func<int, double, int>)PickCarrier) WalkOutPresenter.CarrierPicker = null;
        if (LifeSim.Active == sim) LifeSim.Active = null;
        sim = null; day = null;
    }

    /// <summary>Is a straight walk between two points blocked by scenery (houses, hedges, fences, trees)? Parked cars and
    /// the ground (layer Ground) don't count. Static colliders only, so every machine agrees.</summary>
    static bool LineBlocked(Vector3 a, Vector3 b)
    {
        int ground = LayerMask.NameToLayer("Ground");
        int mask = Physics.DefaultRaycastLayers & ~(ground >= 0 ? 1 << ground : 0);
        Vector3 o = a + Vector3.up * 0.9f, d = (b + Vector3.up * 0.9f) - o;
        float len = d.magnitude;
        if (len < 0.05f) return false;
        return Physics.SphereCast(o, 0.3f, d / len, out _, len, mask, QueryTriggerInteraction.Ignore);
    }

    void EnsureDay(WorldLifeContext ctx)
    {
        if (sim == null) return;
        if (ctx.Day == dayIndex && ctx.UnlockedMask == mask && day != null) return;
        bool newDay = ctx.Day != dayIndex;
        dayIndex = ctx.Day; mask = ctx.UnlockedMask;
        day = sim.Day(dayIndex, mask);
        // A new timetable: everyone re-evaluates (people out on foot keep their record if still out).
        for (int i = 0; i < nextCheck.Length; i++) nextCheck[i] = double.MinValue;
        if (newDay) for (int i = 0; i < suppressUntil.Length; i++) suppressUntil[i] = 0;
        overridesVersion = -1;
    }

    // ------------------------------------------------------------------ tick

    public override void Tick(WorldLifeContext ctx)
    {
        if (sim == null) return;
        EnsureDay(ctx);
        RefreshDelays(ctx.Day);
        double t = day.MsOf(ctx.Hours);
        var rs = sim.Roster.residents;
        for (int r = 0; r < rs.Length; r++)
        {
            double te = t - delayMs[r];
            if (te < nextCheck[r]) continue;
            int ex = day.ExcursionAt(r, te);
            if (ex >= 0 && suppressUntil[r] > ctx.Hours) ex = -1;
            var p = byResident[r];
            if (ex >= 0)
            {
                if (p == null) p = Add(r);
                else if (p.excursion != ex) { Remove(p); p = Add(r); }
                SetExcursion(p, ex);
                nextCheck[r] = day.excursions[ex].t1;
            }
            else
            {
                if (p != null) Remove(p);
                int next = day.NextExcursion(r, te);
                nextCheck[r] = next >= 0 ? day.excursions[next].t0 : double.MaxValue;
                if (suppressUntil[r] > ctx.Hours) nextCheck[r] = Math.Min(nextCheck[r], te + 1000.0);
            }
        }

        // Poses for the people out whose walk passes near the camera.
        Vector3 cam = ctx.Viewer;
        float rel = WorldLifeTuning.Lod.RelevanceDistance;
        int relevant = 0;
        for (int i = 0; i < outList.Count; i++)
        {
            var p = outList[i];
            var e = day.excursions[p.excursion];
            p.relevant = day.walk.BoundsDistance(e.path, cam) <= rel;
            if (!p.relevant) continue;
            double te = t - delayMs[p.resident];
            if (!day.Evaluate(p.excursion, Math.Min(te, e.t1 - 1), out var pos, out var dir, out float speed, out var gait, out _)) continue;
            p.position = pos;
            p.yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            p.speed = speed;
            p.gait = gait;
            float dx = pos.x - cam.x, dz = pos.z - cam.z;
            p.distance = Mathf.Sqrt(dx * dx + dz * dz);
            const float PopMs = 400f;
            p.pop = Mathf.Clamp01((float)Math.Min(te - e.t0, e.t1 - te) / PopMs);
            p.lastFrame = ctx.Frame;
            relevant++;
            // Package C draws them (it caps and LODs; a walk-out or a flop owns them meanwhile and ignores this).
            if (DrawWithPuppets) ResidentDirector.Show(p.id, pos, p.yaw, PoseOf(p, gait), 0f, p.dog.IsValid);
        }
        RelevantCount = relevant;
    }

    /// <summary>Hand the poses to package C's puppets (tests can switch it off to measure the schedules alone).</summary>
    public static bool DrawWithPuppets = true;

    static PersonPose PoseOf(Pedestrian p, LifeGait gait)
    {
        switch (gait)
        {
            case LifeGait.Jog: return PersonPose.Jog;
            case LifeGait.Segway: return PersonPose.Segway;
            case LifeGait.Stand: return p.kind == ExcursionKind.Gossip ? PersonPose.Talk : PersonPose.Stand;
            default: return PersonPose.Walk;
        }
    }

    /// <summary>C's walk-outs (§7.2): the household member who is home over the walk-out window (T plus about 45 real
    /// seconds), ties broken by hash; −1 when nobody is home (C falls back to today's drop-in). Returns a member index.</summary>
    int PickCarrier(int house, double T)
    {
        if (sim == null || house < 0 || house >= sim.Graph.houses.Length) return -1;
        int h = sim.Roster.householdOfHouse[house];
        int r = sim.PickCarrier(sim.Roster.households[h].stopId, T, 0.45f, out _);
        return r < 0 ? -1 : r - sim.Roster.households[h].firstResident;
    }

    void RefreshDelays(int dayIdx)
    {
        if (overridesVersion == LifeOverrides.Version) return;
        overridesVersion = LifeOverrides.Version;
        var rs = sim.Roster.residents;
        for (int r = 0; r < rs.Length; r++)
        {
            float d = LifeOverrides.DelaySeconds(LifeId.Resident(r), dayIdx);
            if (rs[r].car >= 0) d += LifeOverrides.DelaySeconds(LifeId.Car(rs[r].car), dayIdx);   // a driver and their car wait for each other
            int ms = Mathf.RoundToInt(d * 1000f);
            if (ms != delayMs[r]) { delayMs[r] = ms; nextCheck[r] = double.MinValue; }
        }
    }

    /// <summary>The combined timetable delay (ms) the schedules apply to a resident today.</summary>
    public int DelayMs(int resident) => resident >= 0 && resident < delayMs.Length ? delayMs[resident] : 0;

    Pedestrian Add(int r)
    {
        var p = pool.Count > 0 ? pool.Pop() : new Pedestrian();
        p.id = LifeId.Resident(r);
        p.resident = r;
        p.excursion = -1;
        p.lastFrame = 0;
        p.relevant = false;
        p.distance = float.MaxValue;
        p.stopId = sim.Roster.households[sim.Roster.residents[r].household].stopId;
        byResident[r] = p;
        outList.Add(p);
        return p;
    }

    void SetExcursion(Pedestrian p, int ex)
    {
        bool fresh = p.excursion != ex;
        var e = day.excursions[ex];
        p.excursion = ex;
        p.kind = e.kind;
        p.intoCar = e.intoCar;
        p.fromCar = e.fromCar;
        var res = sim.Roster.residents[p.resident];
        p.dog = e.withDog && res.dog >= 0 ? LifeId.Dog(res.dog) : LifeId.None;
        p.partner = e.partner >= 0 ? LifeId.Resident(e.partner) : LifeId.None;
        if (fresh)
        {
            p.lastFrame = 0;
            var h = Appeared;
            if (h != null) foreach (var d in h.GetInvocationList()) try { ((Action<Pedestrian>)d)(p); } catch (Exception x) { Debug.LogException(x); }
        }
    }

    void Remove(Pedestrian p)
    {
        var h = Left;
        if (h != null) foreach (var d in h.GetInvocationList()) try { ((Action<Pedestrian>)d)(p); } catch (Exception x) { Debug.LogException(x); }
        if (p.resident >= 0 && p.resident < byResident.Length && byResident[p.resident] == p) byResident[p.resident] = null;
        outList.Remove(p);
        p.excursion = -1;
        p.lastFrame = 0;
        pool.Push(p);
    }

    /// <summary>People out on foot in a district right now (−1 = town-wide): the §4.3 count.</summary>
    public int CountOut(int district)
    {
        if (sim == null) return 0;
        int n = 0;
        for (int i = 0; i < outList.Count; i++)
            if (district < 0 || sim.Roster.residents[outList[i].resident].district == district) n++;
        return n;
    }
}

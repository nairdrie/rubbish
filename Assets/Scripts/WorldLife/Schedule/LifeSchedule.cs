using System;
using System.Collections.Generic;
using UnityEngine;

// =====================================================================================================================
// WorldLife package D (Docs/WorldLife.md §2.1, §4.3, §7.1, §7.3): the daily timetables. Everything here is DATA: a pure
// function of (street graph, Town seed, day index, unlocked-district mask), so every machine computes the same street
// life without syncing a single position. Time is "day-ms": real milliseconds of game clock since in-game midnight
// (0 .. 1,200,000; one in-game hour = 50,000 ms), so walking speeds stay in real m/s. Host interruptions (NpcOverride
// delays) shift an agent's timetable: it is drawn at s(t − Σ delay) (the directors apply that).
// =====================================================================================================================

/// <summary>One piece of an excursion: walk (or jog / Segway / stand) from arc s0 to s1 on the excursion's path.</summary>
[Serializable]
public struct LifeSegment
{
    public int t0, t1;
    public float s0, s1;
    public LifeGait gait;
    /// <summary>Stand: the yaw to face (degrees); NaN = keep the walking direction.</summary>
    public float faceYaw;
}

/// <summary>A resident outside on foot, from the door and back (or door → car / car → door).</summary>
[Serializable]
public struct LifeExcursion
{
    public int t0, t1;
    public int seg0, segCount;
    /// <summary>The day's <see cref="WalkPathBuffer"/> path id.</summary>
    public int path;
    public ExcursionKind kind;
    public ushort resident;
    /// <summary>The car trip this walk belongs to (index into the day's trips), or −1.</summary>
    public short trip;
    /// <summary>Gossip partner resident, or −1.</summary>
    public short partner;
    /// <summary>Walks to the car and gets in (the car leaves right after).</summary>
    public bool intoCar;
    /// <summary>Gets out of the parked car and walks to the door.</summary>
    public bool fromCar;
    public bool withDog;
}

/// <summary>A household car's trip (§7.3): reverse out → lanes → exit tunnel; away; tunnel → lanes → nose-in.</summary>
[Serializable]
public struct LifeTrip
{
    public ushort car;
    public ushort driver;
    public byte exit;
    public TripKind kind;
    /// <summary>Day-ms: the reverse-out starts; the car vanishes in the tunnel; reappears in the tunnel; is parked.</summary>
    public int depart, awayFrom, ret, park;
    [NonSerialized] public CarPath outPath, inPath;
}

/// <summary>What a car is doing at a moment.</summary>
public enum CarPhase : byte { Home = 0, Out = 1, Away = 2, In = 3 }

/// <summary>
/// One day's timetable (§7.1): every car trip and every resident excursion, built in one go by <see cref="LifeSim"/>.
/// Lists keep their capacity across days (a new day reuses the same object), so a day roll allocates almost nothing.
/// </summary>
public sealed class LifeDay
{
    public int day = int.MinValue, mask;
    public ulong seed;
    public readonly List<LifeTrip> trips = new List<LifeTrip>(256);
    public readonly List<LifeExcursion> excursions = new List<LifeExcursion>(1024);
    public readonly List<LifeSegment> segments = new List<LifeSegment>(4096);
    public readonly WalkPathBuffer walk = new WalkPathBuffer();
    /// <summary>Per resident: first excursion index and count (sorted by t0); per car: first trip and count.</summary>
    public int[] exStart = new int[0], exCount = new int[0], tripStart = new int[0], tripCount = new int[0];
    /// <summary>Per resident: away (in a car) intervals as trips they drive; busy = excursions + car absences.</summary>
    public float BuildMs { get; internal set; }

    public const int MsPerHour = 50000;
    public const int MsPerDay = 24 * MsPerHour;

    public static int HourToMs(float h) => (int)Math.Round(h * MsPerHour);
    /// <summary>Day-ms for a GameClock.TotalHours value on this day (may be &lt; 0 or ≥ a day for other days).</summary>
    public double MsOf(double totalHours) => (totalHours - day * 24.0) * MsPerHour;

    // ------------------------------------------------------------------ queries (no allocations)

    /// <summary>The excursion resident r is on at day-ms t, or −1 (at home or in a car).</summary>
    public int ExcursionAt(int r, double t)
    {
        if (r < 0 || r >= exStart.Length) return -1;
        int a = exStart[r], b = a + exCount[r];
        for (int i = a; i < b; i++)
        {
            var e = excursions[i];
            if (t < e.t0) return -1;
            if (t < e.t1) return i;
        }
        return -1;
    }

    /// <summary>The next excursion of resident r that starts after day-ms t, or −1.</summary>
    public int NextExcursion(int r, double t)
    {
        if (r < 0 || r >= exStart.Length) return -1;
        int a = exStart[r], b = a + exCount[r];
        for (int i = a; i < b; i++) if (excursions[i].t0 > t) return i;
        return -1;
    }

    /// <summary>Where a resident on excursion <paramref name="ex"/> is at day-ms t. False outside it.</summary>
    public bool Evaluate(int ex, double t, out Vector3 pos, out Vector3 dir, out float speed, out LifeGait gait, out int segIndex)
    {
        pos = default; dir = Vector3.forward; speed = 0f; gait = LifeGait.Stand; segIndex = -1;
        if (ex < 0 || ex >= excursions.Count) return false;
        var e = excursions[ex];
        if (t < e.t0 || t >= e.t1) return false;
        int last = e.seg0 + e.segCount - 1;
        int k = e.seg0;
        while (k < last && t >= segments[k].t1) k++;
        var sg = segments[k];
        segIndex = k;
        float span = Mathf.Max(1, sg.t1 - sg.t0);
        float u = Mathf.Clamp01((float)((t - sg.t0) / span));
        float s = Mathf.LerpUnclamped(sg.s0, sg.s1, u);
        pos = walk.Sample(e.path, s, out dir);
        gait = sg.gait;
        if (sg.gait == LifeGait.Stand)
        {
            if (!float.IsNaN(sg.faceYaw)) dir = Quaternion.Euler(0f, sg.faceYaw, 0f) * Vector3.forward;
            else if (k > e.seg0 && segments[k - 1].s1 < segments[k - 1].s0) dir = -dir;
            speed = 0f;
        }
        else
        {
            if (sg.s1 < sg.s0) dir = -dir;
            speed = Mathf.Abs(sg.s1 - sg.s0) / (span * 0.001f);
        }
        return true;
    }

    /// <summary>A car's phase at day-ms t, the trip index and the canonical arc on that trip's path.</summary>
    public CarPhase CarAt(int car, double t, out int trip, out float arc, out float speed)
    {
        trip = -1; arc = 0f; speed = 0f;
        if (car < 0 || car >= tripStart.Length) return CarPhase.Home;
        int a = tripStart[car], b = a + tripCount[car];
        for (int i = a; i < b; i++)
        {
            var tr = trips[i];
            if (t < tr.depart) return CarPhase.Home;
            if (t < tr.awayFrom) { trip = i; arc = tr.outPath.ArcAtTime((float)((t - tr.depart) * 0.001), out speed, out _); return CarPhase.Out; }
            if (t < tr.ret) { trip = i; return CarPhase.Away; }
            if (t < tr.park) { trip = i; arc = tr.inPath.ArcAtTime((float)((t - tr.ret) * 0.001), out speed, out _); return CarPhase.In; }
        }
        return CarPhase.Home;
    }

    /// <summary>The day-ms at which car c's phase next changes after t (int.MaxValue when none).</summary>
    public int CarNextChange(int car, double t)
    {
        if (car < 0 || car >= tripStart.Length) return int.MaxValue;
        int a = tripStart[car], b = a + tripCount[car];
        for (int i = a; i < b; i++)
        {
            var tr = trips[i];
            if (t < tr.depart) return tr.depart;
            if (t < tr.awayFrom) return tr.awayFrom;
            if (t < tr.ret) return tr.ret;
            if (t < tr.park) return tr.park;
        }
        return int.MaxValue;
    }

    /// <summary>Is resident r at home over [t0, t1] (day-ms): not on an excursion and not away in a car?</summary>
    public bool IsHomeDuring(int r, double t0, double t1)
    {
        if (r < 0 || r >= exStart.Length) return false;
        int a = exStart[r], b = a + exCount[r];
        for (int i = a; i < b; i++)
        {
            var e = excursions[i];
            if (e.t0 < t1 && e.t1 > t0) return false;
            // Away in a car between the walk to the car and the walk back.
            if (e.intoCar && e.trip >= 0 && e.trip < trips.Count)
            {
                var tr = trips[e.trip];
                if (e.t1 < t1 && tr.park + 1000 > t0) return false;
            }
        }
        return true;
    }
}

/// <summary>
/// The neighbourhood simulation's data side for one street graph + Town seed: the roster, the router and route caches,
/// static per-house geometry, and the current day's timetable. <see cref="Active"/> is set while WorldLife is awake (by
/// <see cref="PedestrianDirector"/>); tests make their own with <see cref="Create"/>. Main thread only.
/// </summary>
public sealed class LifeSim
{
    /// <summary>The sim for the awake anchor (null while WorldLife sleeps). Packages C and E read it.</summary>
    public static LifeSim Active { get; internal set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Active = null; }

    public readonly StreetGraph Graph;
    public readonly ulong Seed;
    public readonly LifeRoster Roster;
    public readonly LifeAStar AStar;
    readonly Func<Vector3, Vector3, bool> lineBlocked;

    /// <summary>Per-district loop indices, Park nodes and walk nodes.</summary>
    readonly List<int>[] loopsByDistrict = new List<int>[Districts.All.Count];
    readonly List<int>[] parksByDistrict = new List<int>[Districts.All.Count];
    readonly float[][] loopCum;     // per loop: cumulative length at each node (closed: the last entry = full length)

    // Per house: the ramp node (Drive) and the walk join → loop entry index cache.
    readonly int[] rampOf;
    readonly Dictionary<long, int[]> routeCache = new Dictionary<long, int[]>(1024);
    readonly Dictionary<long, CarRoute> carRouteCache = new Dictionary<long, CarRoute>(256);
    readonly Dictionary<int, Vector3[]> carWalkCache = new Dictionary<int, Vector3[]>(128);

    public struct GossipPair { public ushort a, b; public int meet; public byte district; }
    public readonly GossipPair[] gossipPairs;

    /// <summary>A car's chosen exit and its two paths under a gate mask (null paths = it stays home).</summary>
    public sealed class CarRoute { public int exit = -1; public CarPath outPath, inPath; public int costCm; }

    readonly LifeDay day = new LifeDay();
    readonly LifeDay aux = new LifeDay();
    /// <summary>The day the sim last built (see <see cref="Day"/>).</summary>
    public LifeDay Today => day;
    public int DaysBuilt { get; private set; }

    LifeSim(StreetGraph g, ulong seed, Func<Vector3, Vector3, bool> blocked)
    {
        Graph = g; Seed = seed; lineBlocked = blocked;
        Roster = LifeRoster.Build(g, seed);
        AStar = new LifeAStar(g);
        for (int d = 0; d < loopsByDistrict.Length; d++) { loopsByDistrict[d] = new List<int>(); parksByDistrict[d] = new List<int>(); }
        int nl = g.loops?.Length ?? 0;
        loopCum = new float[nl][];
        for (int l = 0; l < nl; l++)
        {
            var lp = g.loops[l].nodes;
            if (lp == null || lp.Length < 3) continue;
            loopsByDistrict[Mathf.Clamp(g.loops[l].district, 0, loopsByDistrict.Length - 1)].Add(l);
            var c = new float[lp.Length + 1];
            for (int i = 0; i < lp.Length; i++) c[i + 1] = c[i] + Vector3.Distance(g.nodes[lp[i]].pos, g.nodes[lp[(i + 1) % lp.Length]].pos);
            loopCum[l] = c;
        }
        for (int i = 0; i < g.NodeCount; i++)
            if (g.nodes[i].kind == StreetGraph.NodeKind.Park) parksByDistrict[Mathf.Clamp(g.nodes[i].district, 0, parksByDistrict.Length - 1)].Add(i);

        rampOf = new int[g.houses.Length];
        for (int h = 0; h < g.houses.Length; h++)
        {
            rampOf[h] = -1;
            var hs = g.houses[h];
            if (!hs.HasDriveway) continue;
            for (int i = g.OutBegin(hs.driveIn); i < g.OutEnd(hs.driveIn); i++)
            {
                int e = g.OutEdge(i), v = g.OutTo(i);
                if (g.edges[e].kind == StreetGraph.EdgeKind.Driveway && g.nodes[v].kind == StreetGraph.NodeKind.Drive) { rampOf[h] = v; break; }
            }
        }
        gossipPairs = BuildGossipPairs();
    }

    /// <summary>A sim for a graph and seed. <paramref name="lineBlocked"/> (optional) says whether a straight walk between
    /// two points is blocked by scenery; without it every door → car walk is a straight line.</summary>
    public static LifeSim Create(StreetGraph g, ulong seed, Func<Vector3, Vector3, bool> lineBlocked = null) =>
        new LifeSim(g, seed, lineBlocked);

    // =================================================================================================================
    // routes (cached)
    // =================================================================================================================

    static long RouteKey(int from, int to, LifeRouteMode mode, int mask, int district) =>
        ((long)from << 40) ^ ((long)to << 24) ^ ((long)mode << 20) ^ ((long)(mask & 0xFF) << 12) ^ (long)(district + 1);

    readonly List<int> scratchNodes = new List<int>(512);

    /// <summary>Node route (cached; null when unreachable).</summary>
    public int[] Route(int from, int to, LifeRouteMode mode, int mask, int district)
    {
        long k = RouteKey(from, to, mode, mask, district);
        if (routeCache.TryGetValue(k, out var r)) return r;
        scratchNodes.Clear();
        int cost = AStar.Route(from, to, mode, mask, district, scratchNodes, null);
        r = cost < 0 ? null : scratchNodes.ToArray();
        routeCache[k] = r;
        return r;
    }

    /// <summary>The car route for a car under a gate mask: the exit with the shortest out + back (null paths = none reachable).</summary>
    public CarRoute CarRouteFor(int car, int mask)
    {
        long key = ((long)car << 32) | (uint)mask;
        if (carRouteCache.TryGetValue(key, out var cr)) return cr;
        cr = new CarRoute();
        var c = Roster.cars[car];
        var hs = Graph.houses[c.house];
        if (hs.HasDriveway && rampOf[c.house] >= 0)
        {
            int best = int.MaxValue;
            for (int x = 0; x < (Graph.exits?.Length ?? 0); x++)
            {
                int xn = Graph.exits[x].node;
                var outR = Route(hs.driveOut, xn, LifeRouteMode.Car, mask, -1);
                var inR = Route(xn, hs.driveIn, LifeRouteMode.Car, mask, -1);
                if (outR == null || inR == null) continue;
                int cost = RouteCost(outR) + RouteCost(inR);
                if (cost < best) { best = cost; cr.exit = x; }
            }
            if (cr.exit >= 0)
            {
                cr.costCm = best;
                cr.outPath = BuildOutPath(c, cr.exit, mask);
                cr.inPath = BuildInPath(c, cr.exit, mask);
                if (cr.outPath == null || cr.inPath == null) { cr.exit = -1; cr.outPath = cr.inPath = null; }
            }
        }
        carRouteCache[key] = cr;
        return cr;
    }

    int RouteCost(int[] nodes)
    {
        int sum = 0;
        for (int i = 0; i + 1 < nodes.Length; i++)
        {
            int a = nodes[i], b = nodes[i + 1];
            for (int k = Graph.OutBegin(a); k < Graph.OutEnd(a); k++)
                if (Graph.OutTo(k) == b) { sum += Mathf.Max(1, (int)Math.Round(Graph.edges[Graph.OutEdge(k)].len * 100.0)); break; }
        }
        return sum;
    }

    int EdgeBetween(int a, int b, bool roadOnly)
    {
        for (int k = Graph.OutBegin(a); k < Graph.OutEnd(a); k++)
            if (Graph.OutTo(k) == b && (!roadOnly || Graph.edges[Graph.OutEdge(k)].kind == StreetGraph.EdgeKind.Road)) return Graph.OutEdge(k);
        return -1;
    }

    // ---- car path building (§7.3) ----

    public const float LaneLimit = 9f, TurnLimit = 4.5f, YieldLimit = 5f, BoxEntryLimit = 3f, DrivewayLimit = 2.5f,
        ReverseLimit = 2.2f, ReverseSwingLimit = 1.5f, TunnelLimit = 7f, PullOutLimit = 4f;
    /// <summary>Cars appear / vanish this far inside the tunnel mouth.</summary>
    public const float TunnelDepth = 5f;

    readonly List<Vector3> bp = new List<Vector3>(256);
    readonly List<CarSegFlags> bf = new List<CarSegFlags>(256);
    readonly List<int> be = new List<int>(256);
    readonly List<float> bl = new List<float>(256);
    readonly List<float> bc = new List<float>(256);

    void BStart() { bp.Clear(); bf.Clear(); be.Clear(); bl.Clear(); bc.Clear(); }

    /// <summary>Add a vertex; the segment that STARTS at it gets (flags, edge, limit).</summary>
    void BAdd(Vector3 p, CarSegFlags f, int e, float limit, float cap = float.MaxValue)
    {
        if (bp.Count > 0 && (bp[bp.Count - 1] - p).sqrMagnitude < 1e-4f)
        {
            // Same point: keep the stricter cap, and the new segment's data.
            int i = bp.Count - 1;
            bf[i] = f; be[i] = e; bl[i] = limit; bc[i] = Mathf.Min(bc[i], cap);
            return;
        }
        bp.Add(p); bf.Add(f); be.Add(e); bl.Add(limit); bc.Add(cap);
    }

    void BLaneRoute(int[] nodes, int startIndex)
    {
        for (int i = startIndex; i < nodes.Length; i++)
        {
            int a = nodes[i];
            int e = i + 1 < nodes.Length ? EdgeBetween(a, nodes[i + 1], true) : -1;
            var f = CarSegFlags.Lane;
            float lim = LaneLimit;
            if (e >= 0)
            {
                var fl = Graph.edges[e].flags;
                if ((fl & StreetGraph.EdgeFlags.Yield) != 0) { f |= CarSegFlags.Yield; lim = YieldLimit; }
                if ((fl & StreetGraph.EdgeFlags.Turn) != 0) { f |= CarSegFlags.Turn; lim = Mathf.Min(lim, TurnLimit); }
                if ((fl & StreetGraph.EdgeFlags.Pad) != 0) { f |= CarSegFlags.Pad; lim = Mathf.Min(lim, WorldLifeTuning.Traffic.PadSpeed); }
            }
            // Entering an intersection box: slow to the intersection speed at its first vertex.
            bool entry = e >= 0 && (Graph.edges[e].flags & StreetGraph.EdgeFlags.Yield) != 0
                         && (i == 0 || !EdgeIsYield(nodes[i - 1], a));
            BAdd(Graph.nodes[a].pos, f, e, lim, entry ? BoxEntryLimit : float.MaxValue);
        }
    }

    bool EdgeIsYield(int a, int b)
    {
        int e = EdgeBetween(a, b, true);
        return e >= 0 && (Graph.edges[e].flags & StreetGraph.EdgeFlags.Yield) != 0;
    }

    /// <summary>Reverse-out spline + lanes + into the exit tunnel.</summary>
    CarPath BuildOutPath(in LifeCar c, int exit, int mask)
    {
        var hs = Graph.houses[c.house];
        var x = Graph.exits[exit];
        var route = Route(hs.driveOut, x.node, LifeRouteMode.Car, mask, -1);
        if (route == null) return null;
        Pose slot = hs.CarSlot(c.slot);
        Vector3 laneA = Graph.nodes[hs.driveIn].pos, laneB = Graph.nodes[hs.driveOut].pos;
        Vector3 laneDir = Flat(laneB - laneA);
        float laneLen = Vector3.Distance(Flat3(laneA), Flat3(laneB));
        Vector3 ramp = Graph.nodes[rampOf[c.house]].pos;
        float tMouth = Vector3.Dot(ramp - laneA, laneDir);
        // The reverse ends with the car on the lane, nose downstream, its centre 2.5 m upstream of the driveway mouth.
        float tEnd = Mathf.Clamp(tMouth - 2.5f, 0.5f, Mathf.Max(0.6f, laneLen - 0.5f));
        Vector3 p3 = laneA + laneDir * tEnd; p3.y = Mathf.Lerp(laneA.y, laneB.y, laneLen > 0.01f ? tEnd / laneLen : 0f);
        Vector3 p0 = slot.position;
        Vector3 fwd = Flat(slot.rotation * Vector3.forward);
        float dist = Vector3.Distance(Flat3(p0), Flat3(p3));
        Vector3 p1 = p0 - fwd * (dist * 0.55f);
        Vector3 p2 = p3 + laneDir * Mathf.Min(4f, dist * 0.45f);

        BStart();
        const int N = 12;
        float swingFrom = 0.6f;
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)N;
            float u = 1f - t;
            Vector3 p = u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
            BAdd(p, CarSegFlags.Reverse | CarSegFlags.Driveway, -1, t < swingFrom ? ReverseLimit : ReverseSwingLimit);
        }
        // The cusp: stop, then drive forward to the driveOut lane node and on along the route.
        int laneEdge = EdgeBetween(hs.driveIn, hs.driveOut, true);
        BAdd(p3, CarSegFlags.Lane, laneEdge, PullOutLimit, 0f);
        BLaneRoute(route, 0);
        // Into the tunnel: the exit node is the last route node; go TunnelDepth further along the portal's forward.
        int last = bp.Count - 1;
        bf[last] = CarSegFlags.Tunnel; be[last] = -1; bl[last] = TunnelLimit;
        Vector3 deep = x.portal.position + Flat(x.portal.rotation * Vector3.forward) * TunnelDepth;
        deep.y = Graph.nodes[x.node].pos.y;
        BAdd(deep, CarSegFlags.Tunnel, -1, TunnelLimit);
        return CarPath.Build(bp, bf, be, bl, bc, 0f, TunnelLimit);
    }

    /// <summary>Out of the exit tunnel + lanes + nose-in to the slot.</summary>
    CarPath BuildInPath(in LifeCar c, int exit, int mask)
    {
        var hs = Graph.houses[c.house];
        var x = Graph.exits[exit];
        var route = Route(x.node, hs.driveIn, LifeRouteMode.Car, mask, -1);
        if (route == null) return null;
        BStart();
        Vector3 deep = x.portal.position + Flat(x.portal.rotation * Vector3.forward) * TunnelDepth;
        deep.y = Graph.nodes[x.node].pos.y;
        BAdd(deep, CarSegFlags.Tunnel, -1, TunnelLimit);
        BLaneRoute(route, 0);
        // The last route node is driveIn: turn into the driveway (ramp), then up to the slot.
        int last = bp.Count - 1;
        int ramp = rampOf[c.house];
        bf[last] = CarSegFlags.Driveway; be[last] = EdgeBetween(hs.driveIn, ramp, false); bl[last] = DrivewayLimit;
        BAdd(Graph.nodes[ramp].pos, CarSegFlags.Driveway, -1, DrivewayLimit);
        Pose slot = hs.CarSlot(c.slot);
        BAdd(slot.position, CarSegFlags.Driveway, -1, DrivewayLimit);
        return CarPath.Build(bp, bf, be, bl, bc, TunnelLimit, 0f);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }
    static Vector3 Flat3(Vector3 v) { v.y = 0f; return v; }

    // ---- walking geometry ----

    /// <summary>Where the driver stands to get in: beside the car on the side nearer the door.</summary>
    public Vector3 CarSidePoint(int car)
    {
        var c = Roster.cars[car];
        var hs = Graph.houses[c.house];
        Pose slot = hs.CarSlot(c.slot);
        Vector3 right = slot.rotation * Vector3.right;
        Vector3 door = Graph.nodes[hs.door].pos;
        float side = Vector3.Dot(door - slot.position, right) >= 0f ? 1f : -1f;
        Vector3 p = slot.position + right * (side * 1.45f) + slot.rotation * Vector3.forward * 0.3f;
        p.y = slot.position.y;
        return p;
    }

    /// <summary>The door → car-side walk (straight across the lawn when it's clear, else garden path → sidewalk → up the
    /// driveway). Cached per car.</summary>
    public Vector3[] CarWalk(int car)
    {
        if (carWalkCache.TryGetValue(car, out var w)) return w;
        var c = Roster.cars[car];
        var hs = Graph.houses[c.house];
        Vector3 door = Graph.nodes[hs.door].pos;
        Vector3 side = CarSidePoint(car);
        var pts = new List<Vector3>(16);
        bool straight = (Flat3(side - door)).magnitude < 30f && (lineBlocked == null || !lineBlocked(door, side));
        if (straight) { pts.Add(door); pts.Add(side); }
        else
        {
            var r = Route(hs.door, hs.walkJoin, LifeRouteMode.Walk, -1, -1);
            if (r != null) foreach (int n in r) pts.Add(Graph.nodes[n].pos); else pts.Add(door);
            int ramp = rampOf[c.house];
            Pose slot = hs.CarSlot(c.slot);
            if (ramp >= 0)
            {
                Vector3 rp = Graph.nodes[ramp].pos;
                Vector3 up = Flat(slot.position - rp);
                Vector3 join = Graph.nodes[hs.walkJoin].pos;
                Vector3 cross = rp + up * 2.1f; cross.y = join.y;              // where the driveway meets the sidewalk line
                pts.Add(cross);
                Vector3 behind = slot.position - Flat(slot.rotation * Vector3.forward) * 3.2f; behind.y = slot.position.y;
                pts.Add(behind);
            }
            pts.Add(side);
        }
        w = pts.ToArray();
        carWalkCache[car] = w;
        return w;
    }

    // =================================================================================================================
    // the day
    // =================================================================================================================

    /// <summary>The timetable for (day, mask), building it if the cached one is for another day or mask.</summary>
    public LifeDay Day(int dayIndex, int mask) => Build(day, dayIndex, mask);

    /// <summary>Another day than the current one (a put-out from before midnight, tests) without disturbing it.</summary>
    public LifeDay OtherDay(int dayIndex, int mask) =>
        day.day == dayIndex && day.mask == mask ? day : Build(aux, dayIndex, mask);

    LifeDay Build(LifeDay target, int dayIndex, int mask)
    {
        if (target.day == dayIndex && target.mask == mask && target.seed == Seed) return target;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BuildDay(target, dayIndex, mask);
        sw.Stop();
        target.BuildMs = (float)sw.Elapsed.TotalMilliseconds;
        DaysBuilt++;
        return target;
    }

    // ---- scratch for the build ----
    struct Busy { public int t0, t1; }
    List<Busy>[] busy;
    readonly List<LifeExcursion> exTmp = new List<LifeExcursion>(1024);
    readonly List<int> order = new List<int>(1024);

    void BuildDay(LifeDay d, int dayIndex, int mask)
    {
        d.day = dayIndex; d.mask = mask; d.seed = Seed;
        d.trips.Clear(); d.excursions.Clear(); d.segments.Clear(); d.walk.Clear();
        int nr = Roster.residents.Length, nc = Roster.cars.Length;
        if (d.exStart.Length != nr) { d.exStart = new int[nr]; d.exCount = new int[nr]; }
        if (d.tripStart.Length != nc) { d.tripStart = new int[nc]; d.tripCount = new int[nc]; }
        if (busy == null || busy.Length != nr) { busy = new List<Busy>[nr]; for (int i = 0; i < nr; i++) busy[i] = new List<Busy>(8); }
        for (int i = 0; i < nr; i++) busy[i].Clear();
        exTmp.Clear();

        // 1. Car trips (households in order; a two-car house plans its street-side car first).
        for (int h = 0; h < Roster.households.Length; h++)
        {
            var hold = Roster.households[h];
            if (hold.firstCar < 0) continue;
            if (hold.carCount >= 2)
            {
                int outer = hold.firstCar + 1, inner = hold.firstCar;
                PlanPrimaryTrips(d, outer, dayIndex, mask);
                PlanNestedTrips(d, inner, outer, dayIndex, mask);
            }
            else PlanPrimaryTrips(d, hold.firstCar, dayIndex, mask);
        }
        // Trips are appended per car in car-index order? Two-car houses add the outer first: fix the order per car.
        SortTripsByCar(d, nc);

        // 2. Walks linked to the trips (door → car, car → door).
        for (int i = 0; i < d.trips.Count; i++) AddCarWalks(d, i);

        // 3. Gossip pairs, then each resident's routine rows.
        for (int p = 0; p < gossipPairs.Length; p++) TryGossip(d, p, dayIndex);
        for (int r = 0; r < nr; r++) PlanRoutines(d, r, dayIndex);

        // 4. Excursions sorted per resident by start time.
        order.Clear();
        for (int i = 0; i < exTmp.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var ea = exTmp[a]; var eb = exTmp[b];
            if (ea.resident != eb.resident) return ea.resident.CompareTo(eb.resident);
            if (ea.t0 != eb.t0) return ea.t0.CompareTo(eb.t0);
            return a.CompareTo(b);
        });
        for (int r = 0; r < nr; r++) { d.exStart[r] = 0; d.exCount[r] = 0; }
        for (int i = 0; i < order.Count; i++)
        {
            var e = exTmp[order[i]];
            if (d.exCount[e.resident] == 0) d.exStart[e.resident] = d.excursions.Count;
            d.exCount[e.resident]++;
            d.excursions.Add(e);
        }
    }

    readonly List<LifeTrip> tripTmp = new List<LifeTrip>(256);

    void SortTripsByCar(LifeDay d, int nc)
    {
        tripTmp.Clear();
        tripTmp.AddRange(d.trips);
        tripTmp.Sort((a, b) => a.car != b.car ? a.car.CompareTo(b.car) : a.depart.CompareTo(b.depart));
        d.trips.Clear();
        for (int c = 0; c < nc; c++) { d.tripStart[c] = 0; d.tripCount[c] = 0; }
        for (int i = 0; i < tripTmp.Count; i++)
        {
            var t = tripTmp[i];
            if (d.tripCount[t.car] == 0) d.tripStart[t.car] = d.trips.Count;
            d.tripCount[t.car]++;
            d.trips.Add(t);
        }
    }

    // ---- car trips ----

    ulong H(LifeKind k, int id, int dayIndex, uint salt) => LifeHash.U64(Seed, k, id, dayIndex, salt);

    /// <summary>A time in [from, to) hours as day-ms on a trapezoid: a uniform plateau with ramps of <paramref name="ramp"/> ×
    /// the window at each end (the sum of U(0, 1 − ramp) and U(0, ramp)), so a rush hour is a broad plateau, not a peak.</summary>
    static int Trap(ulong h, float from, float to, float ramp)
    {
        ramp = Mathf.Clamp(ramp, 0f, 0.5f);
        int a = LifeHash.Range(h, 0, 10001), b = LifeHash.Range(h >> 21, 0, 10001);
        float u = a / 10000f * (1f - ramp) + b / 10000f * ramp;
        return LifeDay.HourToMs(Mathf.Lerp(from, to, u));
    }

    static int Uni(ulong h, float from, float to) => LifeDay.HourToMs(Mathf.Lerp(from, to, LifeHash.Range(h, 0, 10001) / 10000f));

    bool TryAddTrip(LifeDay d, int car, int depart, int ret, TripKind kind, CarRoute cr, int notBefore = 0, int notAfter = int.MaxValue)
    {
        if (cr == null || cr.exit < 0) return false;
        int outMs = Mathf.CeilToInt(cr.outPath.Duration * 1000f), inMs = Mathf.CeilToInt(cr.inPath.Duration * 1000f);
        int walkMs = WalkMs(car);
        int awayFrom = depart + outMs;
        if (ret < awayFrom + 4000) ret = awayFrom + 4000;
        int park = ret + inMs;
        int dayEnd = LifeDay.HourToMs(LifeRoutines.DayEndHour);
        if (depart - walkMs - 3000 < notBefore || park + walkMs + 3000 > Mathf.Min(dayEnd, notAfter)) return false;
        // No overlap with this car's other trips (and a margin for the walks).
        for (int i = 0; i < d.trips.Count; i++)
        {
            var o = d.trips[i];
            if (o.car != car) continue;
            if (depart - walkMs - 8000 < o.park + walkMs + 8000 && park + walkMs + 8000 > o.depart - walkMs - 8000) return false;
        }
        var rc = Roster.cars[car];
        d.trips.Add(new LifeTrip
        {
            car = (ushort)car, driver = rc.driver, exit = (byte)cr.exit, kind = kind,
            depart = depart, awayFrom = awayFrom, ret = ret, park = park, outPath = cr.outPath, inPath = cr.inPath,
        });
        return true;
    }

    int WalkMs(int car)
    {
        var w = CarWalk(car);
        float len = 0f;
        for (int i = 0; i + 1 < w.Length; i++) len += Vector3.Distance(w[i], w[i + 1]);
        float speed = Roster.residents[Roster.cars[car].driver].walkSpeed;
        return Mathf.CeilToInt(len / Mathf.Max(0.5f, speed) * 1000f) + 1500;
    }

    void PlanPrimaryTrips(LifeDay d, int car, int dayIndex, int mask)
    {
        var cr = CarRouteFor(car, mask);
        if (cr.exit < 0) return;   // a locked gate in the way: this car stays home today (§7.3 gate rule)
        int district = Roster.cars[car].district;
        ulong h = H(LifeKind.Car, car, dayIndex, 11);
        bool commuting = false;
        if (LifeHash.Chance(H(LifeKind.Car, car, dayIndex, 12), LifeRoutines.DawnShiftChance, 1000))
            commuting = TryAddTrip(d, car, Uni(h, LifeRoutines.DawnFrom, LifeRoutines.DawnTo),
                                   Uni(h >> 17, LifeRoutines.DawnBackFrom, LifeRoutines.DawnBackTo), TripKind.DawnShift, cr);
        int commuteChance = LifeRoutines.CommuteChance[district] + (Roster.households[Roster.cars[car].household].carCount >= 2 ? LifeRoutines.CommuteTwoCarBonus : 0);
        if (!commuting && LifeHash.Chance(H(LifeKind.Car, car, dayIndex, 13), commuteChance, 1000))
            commuting = TryAddTrip(d, car, Trap(H(LifeKind.Car, car, dayIndex, 14), LifeRoutines.CommuteFrom, LifeRoutines.CommuteTo, LifeRoutines.CommuteRamp),
                                   Trap(H(LifeKind.Car, car, dayIndex, 15), LifeRoutines.CommuteBackFrom, LifeRoutines.CommuteBackTo, LifeRoutines.CommuteRamp), TripKind.Commute, cr);
        int errand = commuting ? LifeRoutines.ErrandWhenCommuting : LifeRoutines.ErrandChance[district];
        if (LifeHash.Chance(H(LifeKind.Car, car, dayIndex, 16), errand, 1000))
        {
            ulong he = H(LifeKind.Car, car, dayIndex, 17);
            int dep = Uni(he, LifeRoutines.ErrandFrom, LifeRoutines.ErrandTo);
            int away = Uni(he >> 19, LifeRoutines.ErrandAwayMin, LifeRoutines.ErrandAwayMax);
            int outMs = Mathf.CeilToInt(cr.outPath.Duration * 1000f);
            TryAddTrip(d, car, dep, dep + outMs + away, TripKind.Errand, cr);
        }
        if (LifeHash.Chance(H(LifeKind.Car, car, dayIndex, 18), LifeRoutines.OutingChance[district], 1000))
        {
            ulong ho = H(LifeKind.Car, car, dayIndex, 19);
            int dep = Uni(ho, LifeRoutines.OutingFrom, LifeRoutines.OutingTo);
            int away = Uni(ho >> 19, LifeRoutines.OutingAwayMin, LifeRoutines.OutingAwayMax);
            int outMs = Mathf.CeilToInt(cr.outPath.Duration * 1000f);
            TryAddTrip(d, car, dep, dep + outMs + away, TripKind.Outing, cr);
        }
        if (LifeHash.Chance(H(LifeKind.Car, car, dayIndex, 20), LifeRoutines.LateChance, 1000))
        {
            ulong hl = H(LifeKind.Car, car, dayIndex, 21);
            TryAddTrip(d, car, Uni(hl, LifeRoutines.LateFrom, LifeRoutines.LateTo), Uni(hl >> 19, LifeRoutines.LateBackFrom, LifeRoutines.LateBackTo), TripKind.Late, cr);
        }
    }

    /// <summary>The deeper car of a two-car driveway drives only while the street-side car is away: it leaves after the
    /// outer car has cleared the driveway and is parked again before the outer car comes back.</summary>
    void PlanNestedTrips(LifeDay d, int inner, int outer, int dayIndex, int mask)
    {
        var cr = CarRouteFor(inner, mask);
        if (cr.exit < 0) return;
        int count = d.trips.Count;
        for (int i = 0; i < count; i++)
        {
            var o = d.trips[i];
            if (o.car != outer) continue;
            if (!LifeHash.Chance(H(LifeKind.Car, inner, dayIndex, (uint)(30 + i)), LifeRoutines.NestedErrandChance, 1000)) continue;
            int open = o.depart + Mathf.CeilToInt(o.outPath.Duration * 1000f * 0.4f) + 6000;   // the outer car is off the driveway
            int close = o.ret - 2000;                                                           // parked before it reappears
            int outMs = Mathf.CeilToInt(cr.outPath.Duration * 1000f), inMs = Mathf.CeilToInt(cr.inPath.Duration * 1000f);
            int walkMs = WalkMs(inner);
            int room = close - open - outMs - inMs - 2 * walkMs - 10000;
            if (room < 20000) continue;
            ulong h = H(LifeKind.Car, inner, dayIndex, (uint)(40 + i));
            int away = Mathf.Min(room - 1000, Uni(h, LifeRoutines.ErrandAwayMin, LifeRoutines.ErrandAwayMax));
            if (away < 15000) away = 15000;
            int slack = room - away;
            int dep = open + walkMs + 4000 + LifeHash.Range(h >> 23, 0, Mathf.Max(1, slack));
            TryAddTrip(d, inner, dep, dep + outMs + away, d.trips[i].kind == TripKind.Commute ? TripKind.Errand : d.trips[i].kind, cr,
                       open, close);
        }
    }

    // ---- excursions ----

    bool Free(int r, int t0, int t1, int margin = 6000)
    {
        var list = busy[r];
        for (int i = 0; i < list.Count; i++) if (t0 - margin < list[i].t1 && t1 + margin > list[i].t0) return false;
        return true;
    }

    /// <summary>The earliest start ≥ t0 (≤ latest) at which a [start, start + dur] block is free, or −1.</summary>
    int FirstFree(int r, int t0, int dur, int latest, int margin = 6000)
    {
        int t = t0;
        for (int guard = 0; guard < 12; guard++)
        {
            if (t > latest) return -1;
            bool clash = false;
            var list = busy[r];
            for (int i = 0; i < list.Count; i++)
            {
                if (t - margin < list[i].t1 && t + dur + margin > list[i].t0) { t = list[i].t1 + margin + 1; clash = true; break; }
            }
            if (!clash) return t;
        }
        return -1;
    }

    void MarkBusy(int r, int t0, int t1) => busy[r].Add(new Busy { t0 = t0, t1 = t1 });

    void AddExcursion(LifeDay d, in LifeExcursion e) => exTmp.Add(e);

    int AddSegment(LifeDay d, int t0, int t1, float s0, float s1, LifeGait gait, float faceYaw = float.NaN)
    {
        d.segments.Add(new LifeSegment { t0 = t0, t1 = t1, s0 = s0, s1 = s1, gait = gait, faceYaw = faceYaw });
        return d.segments.Count - 1;
    }

    void AddCarWalks(LifeDay d, int tripIndex)
    {
        var tr = d.trips[tripIndex];
        int r = tr.driver;
        var w = CarWalk(tr.car);
        d.walk.Begin();
        for (int i = 0; i < w.Length; i++) d.walk.Add(w[i]);
        int path = d.walk.End();
        float len = d.walk.Length(path);
        float speed = Roster.residents[r].walkSpeed;
        int walkMs = Mathf.CeilToInt(len / Mathf.Max(0.5f, speed) * 1000f);
        int getIn = Mathf.RoundToInt(LifeRoutines.GetInSeconds * 1000f), getOut = Mathf.RoundToInt(LifeRoutines.GetOutSeconds * 1000f);
        var kind = tr.kind == TripKind.Commute || tr.kind == TripKind.DawnShift ? ExcursionKind.Commute : ExcursionKind.Errand;

        int outEnd = tr.depart - getIn, outStart = outEnd - walkMs;
        int seg = AddSegment(d, outStart, outEnd, 0f, len, LifeGait.Walk);
        AddExcursion(d, new LifeExcursion
        {
            t0 = outStart, t1 = outEnd, seg0 = seg, segCount = 1, path = path, kind = kind, resident = (ushort)r,
            trip = (short)tripIndex, partner = -1, intoCar = true,
        });
        int backStart = tr.park + getOut, backEnd = backStart + walkMs;
        seg = AddSegment(d, backStart, backEnd, len, 0f, LifeGait.Walk);
        AddExcursion(d, new LifeExcursion
        {
            t0 = backStart, t1 = backEnd, seg0 = seg, segCount = 1, path = path, kind = kind, resident = (ushort)r,
            trip = (short)tripIndex, partner = -1, fromCar = true,
        });
        MarkBusy(r, outStart, backEnd);
    }

    // Gossip pairs: neighbouring houses (stopId order) in one district whose walk joins are close.
    GossipPair[] BuildGossipPairs()
    {
        var list = new List<GossipPair>();
        var hh = Roster.households;
        int walkMask = StreetGraph.KindMask(StreetGraph.NodeKind.Walk);
        for (int i = 0; i + 1 < hh.Length; i++)
        {
            var a = hh[i]; var b = hh[i + 1];
            if (a.district != b.district) continue;
            var ha = Graph.houses[a.house]; var hb = Graph.houses[b.house];
            Vector3 ja = Graph.nodes[ha.walkJoin].pos, jb = Graph.nodes[hb.walkJoin].pos;
            if (Vector3.Distance(ja, jb) > LifeRoutines.GossipPairDistance) continue;
            int meet = Graph.Nearest((ja + jb) * 0.5f, walkMask, a.district);
            if (meet < 0) continue;
            list.Add(new GossipPair { a = (ushort)i, b = (ushort)(i + 1), meet = meet, district = a.district });
        }
        return list.ToArray();
    }

    void TryGossip(LifeDay d, int p, int dayIndex)
    {
        var gp = gossipPairs[p];
        ulong h = H(LifeKind.Town, 1000 + p, dayIndex, 51);
        int chance = Mathf.RoundToInt(LifeRoutines.GossipChance * LifeRoutines.DistrictScale[gp.district]);
        if (!LifeHash.Chance(h, chance, 1000)) return;
        var ha = Roster.households[gp.a]; var hb = Roster.households[gp.b];
        int ra = ha.firstResident + LifeHash.Range(h >> 7, 0, ha.residentCount);
        int rb = hb.firstResident + LifeHash.Range(h >> 13, 0, hb.residentCount);
        if (Roster.residents[ra].archetype == ResidentArchetype.Butler || Roster.residents[rb].archetype == ResidentArchetype.Butler) return;
        var routeA = Route(Graph.houses[ha.house].door, gp.meet, LifeRouteMode.Walk, -1, gp.district);
        var routeB = Route(Graph.houses[hb.house].door, gp.meet, LifeRouteMode.Walk, -1, gp.district);
        if (routeA == null || routeB == null) return;
        int pa = PathFromNodes(d, routeA), pb = PathFromNodes(d, routeB);
        float la = d.walk.Length(pa), lb = d.walk.Length(pb);
        int wa = Mathf.CeilToInt(la / Roster.residents[ra].walkSpeed * 1000f), wb = Mathf.CeilToInt(lb / Roster.residents[rb].walkSpeed * 1000f);
        int dur = Mathf.RoundToInt(Mathf.Lerp(LifeRoutines.GossipMinSeconds, LifeRoutines.GossipMaxSeconds, LifeHash.Range(h >> 19, 0, 1001) / 1000f) * 1000f);
        int meetAt = Uni(h >> 29, LifeRoutines.GossipFrom, LifeRoutines.GossipTo);
        if (!Free(ra, meetAt - wa, meetAt + dur + wa) || !Free(rb, meetAt - wb, meetAt + dur + wb)) return;
        Vector3 posA = d.walk.Sample(pa, la, out _), posB = d.walk.Sample(pb, lb, out _);
        // Stand half a metre apart, facing each other.
        Vector3 ab = posB - posA; ab.y = 0f;
        if (ab.sqrMagnitude < 0.01f) ab = Vector3.right;
        float yawA = Quaternion.LookRotation(ab.normalized).eulerAngles.y, yawB = Mathf.Repeat(yawA + 180f, 360f);
        AddStandWalk(d, ra, pa, la, meetAt, dur, wa, yawA, (short)rb, ExcursionKind.Gossip);
        AddStandWalk(d, rb, pb, lb, meetAt, dur, wb, yawB, (short)ra, ExcursionKind.Gossip);
    }

    void AddStandWalk(LifeDay d, int r, int path, float len, int standAt, int dur, int walkMs, float yaw, short gb, ExcursionKind kind)
    {
        int t0 = standAt - walkMs;
        int s0 = AddSegment(d, t0, standAt, 0f, len, LifeGait.Walk);
        AddSegment(d, standAt, standAt + dur, len, len, LifeGait.Stand, yaw);
        AddSegment(d, standAt + dur, standAt + dur + walkMs, len, 0f, LifeGait.Walk);
        AddExcursion(d, new LifeExcursion
        {
            t0 = t0, t1 = standAt + dur + walkMs, seg0 = s0, segCount = 3, path = path, kind = kind, resident = (ushort)r,
            trip = -1, partner = gb,
        });
        MarkBusy(r, t0, standAt + dur + walkMs);
    }

    int PathFromNodes(LifeDay d, int[] nodes)
    {
        d.walk.Begin();
        for (int i = 0; i < nodes.Length; i++) d.walk.Add(Graph.nodes[nodes[i]].pos);
        return d.walk.End();
    }

    readonly List<LifeRoutines.Row> rowTmp = new List<LifeRoutines.Row>(8);

    void PlanRoutines(LifeDay d, int r, int dayIndex)
    {
        var res = Roster.residents[r];
        var rows = LifeRoutines.ByArchetype[(int)res.archetype];
        rowTmp.Clear();
        bool dogOwner = res.dog >= 0;
        bool dogArch = res.archetype == ResidentArchetype.DogPerson || res.archetype == ResidentArchetype.PearlLady;
        if (dogOwner && !dogArch) { rowTmp.Add(LifeRoutines.ExtraDogMorning); rowTmp.Add(LifeRoutines.ExtraDogEvening); }
        for (int i = 0; i < rows.Length; i++) if (rows[i].chance > 0) rowTmp.Add(rows[i]);
        float scale = LifeRoutines.DistrictScale[res.district];
        for (int i = 0; i < rowTmp.Count; i++)
        {
            var row = rowTmp[i];
            ulong h = H(LifeKind.Resident, r, dayIndex, (uint)(100 + i));
            if (!LifeHash.Chance(h, Mathf.RoundToInt(row.chance * scale), 1000)) continue;
            int start = Uni(h >> 11, row.from, row.to);
            PlanExcursion(d, r, row.kind, start, LifeDay.HourToMs(row.to), h, dayIndex, i);
        }
    }

    readonly List<Vector3> ptsTmp = new List<Vector3>(512);

    void PlanExcursion(LifeDay d, int r, ExcursionKind kind, int start, int latest, ulong h, int dayIndex, int row)
    {
        var res = Roster.residents[r];
        var hold = Roster.households[res.household];
        var hs = Graph.houses[hold.house];
        int district = res.district;
        float speed = LifeRoutines.Speed(kind, res.walkSpeed);
        var gait = LifeRoutines.Gait(kind);
        bool jay = (kind == ExcursionKind.Stroll || kind == ExcursionKind.DogWalk) && LifeHash.Chance(h >> 3, 1, LifeRoutines.JaywalkOneIn);
        var mode = jay ? LifeRouteMode.WalkJaywalk : LifeRouteMode.Walk;
        bool withDog = res.dog >= 0 && (kind == ExcursionKind.DogWalk || kind == ExcursionKind.NightOwl
                                         || (kind == ExcursionKind.Segway && res.archetype == ResidentArchetype.Billionaire));

        int segBefore = d.segments.Count;
        int path; float len;
        float pauseAt = -1f, pauseMs = 0f;

        switch (kind)
        {
            case ExcursionKind.Mail:
            {
                var route = Route(hs.door, hs.pathEnd, LifeRouteMode.Walk, -1, -1);
                if (route == null) return;
                ptsTmp.Clear();
                foreach (int n in route) ptsTmp.Add(Graph.nodes[n].pos);
                Vector3 pe = Graph.nodes[hs.pathEnd].pos;
                if (hs.mailbox.sqrMagnitude > 1f && Vector3.Distance(hs.mailbox, pe) < 4f)
                {
                    Vector3 m = hs.mailbox; m.y = pe.y;
                    // Stand beside the mailbox, not inside it.
                    Vector3 off = pe - m; off.y = 0f;
                    ptsTmp.Add(m + (off.sqrMagnitude > 0.01f ? off.normalized * 0.55f : Vector3.zero));
                }
                path = AddPoints(d, ptsTmp);
                len = d.walk.Length(path);
                pauseAt = len; pauseMs = LifeRoutines.MailPause * 1000f;
                break;
            }
            case ExcursionKind.Park:
            {
                var parks = parksByDistrict[district];
                if (parks.Count == 0) { kind = ExcursionKind.Stroll; goto default; }
                int target = parks[LifeHash.Range(h >> 5, 0, parks.Count)];
                var route = Route(hs.door, target, mode, -1, district);
                if (route == null) return;
                path = PathFromNodes(d, route);
                len = d.walk.Length(path);
                pauseAt = len;
                pauseMs = Mathf.Lerp(LifeRoutines.ParkPauseMin, LifeRoutines.ParkPauseMax, LifeHash.Range(h >> 9, 0, 1001) / 1000f) * 1000f;
                break;
            }
            default:
            {
                // Round the nearest block loop when it fits the walk's length, else out along the sidewalk and back.
                if (!BuildWalk(d, r, kind, h, mode, out path, out len, out bool oab)) return;
                if (oab)
                {
                    pauseAt = len;
                    bool dog = kind == ExcursionKind.DogWalk || kind == ExcursionKind.NightOwl;
                    pauseMs = Mathf.Lerp(dog ? LifeRoutines.SniffMin : LifeRoutines.TurnPauseMin, dog ? LifeRoutines.SniffMax : LifeRoutines.TurnPauseMax,
                                         LifeHash.Range(h >> 9, 0, 1001) / 1000f) * 1000f;
                }
                break;
            }
        }

        // Timing: out-and-back kinds pause at the far end and walk back the same way; loops go once round.
        bool outAndBack = pauseAt >= 0f;
        int walkMs = Mathf.CeilToInt(len / Mathf.Max(0.3f, speed) * 1000f);
        int sniffs = (kind == ExcursionKind.DogWalk || kind == ExcursionKind.NightOwl) && !outAndBack ? 1 + LifeHash.Range(h >> 27, 0, 2) : 0;
        int sniffMs = 0;
        var sniffAt = new float[0];
        if (sniffs > 0)
        {
            sniffAt = new float[sniffs];
            for (int i = 0; i < sniffs; i++)
            {
                sniffAt[i] = len * (i + 1) / (sniffs + 1) + (LifeHash.Range(h >> (30 + i * 3), 0, 1001) / 1000f - 0.5f) * len * 0.2f;
                sniffMs += Mathf.RoundToInt(Mathf.Lerp(LifeRoutines.SniffMin, LifeRoutines.SniffMax, LifeHash.Range(h >> (33 + i * 3), 0, 1001) / 1000f) * 1000f);
            }
        }
        int total = outAndBack ? 2 * walkMs + Mathf.RoundToInt(pauseMs) : walkMs + sniffMs;
        int dayEnd = LifeDay.HourToMs(LifeRoutines.DayEndHour);
        int t0 = FirstFree(r, start, total, latest);
        if (t0 < 0 || t0 + total > dayEnd) { RollbackWalk(d, segBefore); return; }

        int seg0 = d.segments.Count;
        int t = t0;
        if (outAndBack)
        {
            AddSegment(d, t, t + walkMs, 0f, len, gait); t += walkMs;
            AddSegment(d, t, t + Mathf.RoundToInt(pauseMs), len, len, LifeGait.Stand); t += Mathf.RoundToInt(pauseMs);
            AddSegment(d, t, t + walkMs, len, 0f, gait); t += walkMs;
        }
        else
        {
            float s = 0f;
            for (int i = 0; i <= sniffAt.Length; i++)
            {
                float sEnd = i < sniffAt.Length ? Mathf.Clamp(sniffAt[i], s, len) : len;
                int ms = Mathf.CeilToInt((sEnd - s) / Mathf.Max(0.3f, speed) * 1000f);
                if (ms > 0) { AddSegment(d, t, t + ms, s, sEnd, gait); t += ms; }
                if (i < sniffAt.Length)
                {
                    int sm = Mathf.RoundToInt(Mathf.Lerp(LifeRoutines.SniffMin, LifeRoutines.SniffMax, LifeHash.Range(h >> (33 + i * 3), 0, 1001) / 1000f) * 1000f);
                    AddSegment(d, t, t + sm, sEnd, sEnd, LifeGait.Stand); t += sm;
                }
                s = sEnd;
            }
        }
        AddExcursion(d, new LifeExcursion
        {
            t0 = t0, t1 = t, seg0 = seg0, segCount = d.segments.Count - seg0, path = path, kind = kind, resident = (ushort)r,
            trip = -1, partner = -1, withDog = withDog,
        });
        MarkBusy(r, t0, t);
    }

    void RollbackWalk(LifeDay d, int segCount)
    {
        // The path stays in the buffer unused (harmless); segments are trimmed.
        if (d.segments.Count > segCount) d.segments.RemoveRange(segCount, d.segments.Count - segCount);
    }

    int AddPoints(LifeDay d, List<Vector3> pts)
    {
        d.walk.Begin();
        for (int i = 0; i < pts.Count; i++) d.walk.Add(pts[i]);
        return d.walk.End();
    }

    /// <summary>
    /// A walk of the kind's whole length (<see cref="LifeRoutines.WalkLength"/>): door → the nearest loop node → round the
    /// block loop → home, when the way to the loop leaves room for it; otherwise out along the sidewalk to a node half the
    /// length away and back (<paramref name="outAndBack"/>; <paramref name="len"/> is then one way).
    /// </summary>
    bool BuildWalk(LifeDay d, int r, ExcursionKind kind, ulong h, LifeRouteMode mode, out int path, out float len, out bool outAndBack)
    {
        path = -1; len = 0f; outAndBack = false;
        var res = Roster.residents[r];
        int house = Roster.households[res.household].house;
        var hs = Graph.houses[house];
        int district = res.district;
        var loops = loopsByDistrict[district];
        Vector3 join = Graph.nodes[hs.walkJoin].pos;
        Vector2 wr = LifeRoutines.WalkLength(kind);
        float total = LifeHash.Range(h >> 21, (int)wr.x, (int)wr.y + 1);
        if (loops.Count > 0)
        {
            // Nearest loop two times in three, else any loop of the district.
            int li;
            if (LifeHash.Chance(h >> 15, 2, 3))
            {
                li = loops[0]; float bestD = float.MaxValue;
                foreach (int l in loops)
                {
                    int k = NearestOnLoop(l, join, out float dd);
                    if (k >= 0 && dd < bestD) { bestD = dd; li = l; }
                }
            }
            else li = loops[LifeHash.Range(h >> 17, 0, loops.Count)];
            var lp = Graph.loops[li].nodes;
            int entry = NearestOnLoop(li, join, out _);
            var toLoop = entry >= 0 ? Route(hs.door, lp[entry], mode, -1, district) : null;
            float approach = toLoop != null ? NodesLength(toLoop) : float.MaxValue;
            if (toLoop != null && approach * 2f + 25f <= total)
            {
                int dir = LifeHash.Chance(h >> 25, 1, 2) ? 1 : -1;
                ptsTmp.Clear();
                foreach (int n in toLoop) ptsTmp.Add(Graph.nodes[n].pos);
                // Round the loop until approach + walked + the way home (≈ 1.3 × straight line) reaches the budget.
                Vector3 doorPos = Graph.nodes[hs.door].pos;
                float walked = 0f;
                int i = entry, guard = 0;
                while (guard++ < lp.Length * 2)
                {
                    int j = ((i + dir) % lp.Length + lp.Length) % lp.Length;
                    walked += Vector3.Distance(Graph.nodes[lp[i]].pos, Graph.nodes[lp[j]].pos);
                    ptsTmp.Add(Graph.nodes[lp[j]].pos);
                    i = j;
                    Vector3 q = Graph.nodes[lp[j]].pos;
                    float back = Mathf.Sqrt((q.x - doorPos.x) * (q.x - doorPos.x) + (q.z - doorPos.z) * (q.z - doorPos.z)) * 1.3f;
                    if (approach + walked + back >= total && walked >= 20f) break;
                }
                var home = Route(lp[i], hs.door, mode, -1, district);
                if (home != null)
                {
                    for (int k = 1; k < home.Length; k++) ptsTmp.Add(Graph.nodes[home[k]].pos);
                    path = AddPoints(d, ptsTmp);
                    len = d.walk.Length(path);
                    return true;
                }
            }
        }
        // Out along the sidewalk and back.
        outAndBack = true;
        return OutAndBack(d, house, total * 0.5f, h, out path, out len);
    }

    float NodesLength(int[] nodes)
    {
        float l = 0f;
        for (int i = 0; i + 1 < nodes.Length; i++) l += Vector3.Distance(Graph.nodes[nodes[i]].pos, Graph.nodes[nodes[i + 1]].pos);
        return l;
    }

    sealed class HouseReach { public int[] nodes, cost, parent; public Dictionary<int, int> index; }
    readonly Dictionary<int, HouseReach> reachCache = new Dictionary<int, HouseReach>(128);
    readonly List<int> rn = new List<int>(512), rc = new List<int>(512), rp = new List<int>(512);

    /// <summary>Every sidewalk node within 300 m of a house's walk join (its own district), nearest first. Cached.</summary>
    HouseReach ReachOf(int house)
    {
        if (reachCache.TryGetValue(house, out var hr)) return hr;
        var hs = Graph.houses[house];
        rn.Clear(); rc.Clear(); rp.Clear();
        AStar.Reach(hs.walkJoin, LifeRouteMode.Walk, -1, Graph.nodes[hs.walkJoin].district, 30000, rn, rc, rp);
        hr = new HouseReach { nodes = rn.ToArray(), cost = rc.ToArray(), parent = rp.ToArray(), index = new Dictionary<int, int>(rn.Count) };
        for (int i = 0; i < hr.nodes.Length; i++) hr.index[hr.nodes[i]] = i;
        reachCache[house] = hr;
        return hr;
    }

    bool OutAndBack(LifeDay d, int house, float oneWay, ulong h, out int path, out float len)
    {
        path = -1; len = 0f;
        var hs = Graph.houses[house];
        var toJoin = Route(hs.door, hs.walkJoin, LifeRouteMode.Walk, -1, -1);
        if (toJoin == null) return false;
        var reach = ReachOf(house);
        if (reach.nodes.Length < 2) return false;
        int target = Mathf.Max(800, Mathf.RoundToInt((oneWay - NodesLength(toJoin)) * 100f));
        int lo = -1, hi = -1;
        for (int i = 1; i < reach.nodes.Length; i++)
        {
            int c = reach.cost[i];
            if (c < target * 8 / 10) continue;
            if (c > target * 12 / 10) break;
            if (lo < 0) lo = i;
            hi = i;
        }
        int pick;
        if (lo < 0)
        {
            pick = 1;
            for (int i = 1; i < reach.nodes.Length; i++) if (reach.cost[i] <= target) pick = i;
        }
        else pick = lo + LifeHash.Range(h >> 29, 0, hi - lo + 1);
        // door → walk join → … → the turn-around node (the Dijkstra tree).
        ptsTmp.Clear();
        foreach (int n in toJoin) ptsTmp.Add(Graph.nodes[n].pos);
        int start = ptsTmp.Count;
        for (int k = pick, guard = 0; k > 0 && guard < 4096; guard++)
        {
            ptsTmp.Add(Graph.nodes[reach.nodes[k]].pos);
            int parent = reach.parent[k];
            if (parent < 0 || !reach.index.TryGetValue(parent, out k)) break;
        }
        ptsTmp.Reverse(start, ptsTmp.Count - start);
        path = AddPoints(d, ptsTmp);
        len = d.walk.Length(path);
        return len > 1f;
    }

    int NearestOnLoop(int loop, Vector3 p, out float dist)
    {
        var lp = Graph.loops[loop].nodes;
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < lp.Length; i++)
        {
            Vector3 q = Graph.nodes[lp[i]].pos;
            float dx = q.x - p.x, dz = q.z - p.z, dd = dx * dx + dz * dz;
            if (dd < bd) { bd = dd; best = i; }
        }
        dist = Mathf.Sqrt(bd);
        return best;
    }

    // =================================================================================================================
    // queries for other packages (C: walk-outs; E: letters) and tests
    // =================================================================================================================

    /// <summary>
    /// §7.2 "who carries it": a household member who is home over the walk-out (put-out hour T to T + 2 × walkHours),
    /// ties broken by a hash of (stopId, day, minute of T); never the butler. −1 when nobody is home (C falls back to the
    /// drop-in). <paramref name="commuter"/> is reserved for the "carries it on the way to the car" variant (not used: C's
    /// walk-out goes back to the door, so the commuter would be in two places).
    /// </summary>
    public int PickCarrier(string stopId, double putOutHours, float walkHours, out bool commuter)
    {
        commuter = false;
        int h = Roster.HouseholdIndex(stopId);
        if (h < 0) return -1;
        int dayIndex = (int)Math.Floor(putOutHours / 24.0);
        var dd = OtherDay(dayIndex, day.day == dayIndex ? day.mask : WorldLifeIds.UnlockedDistrictMask());
        double t = dd.MsOf(putOutHours);
        var hold = Roster.households[h];
        double t1 = t + Math.Max(0.05, walkHours) * 2.0 * LifeDay.MsPerHour;
        int start = LifeHash.Range(LifeHash.U64(Seed, LifeKind.Town, (int)LifeHash.Str(stopId), dayIndex, (uint)Math.Floor(putOutHours * 60.0)), 0, Math.Max(1, (int)hold.residentCount));
        for (int k = 0; k < hold.residentCount; k++)
        {
            int r = hold.firstResident + (start + k) % hold.residentCount;
            if (Roster.residents[r].archetype == ResidentArchetype.Butler) continue;
            if (dd.IsHomeDuring(r, t, t1)) return r;
        }
        return -1;
    }

    /// <summary>Is the resident home at this GameClock.TotalHours (not out walking, not away in a car)?</summary>
    public bool IsHome(int resident, double totalHours)
    {
        int dayIndex = (int)Math.Floor(totalHours / 24.0);
        var dd = OtherDay(dayIndex, day.day == dayIndex ? day.mask : WorldLifeIds.UnlockedDistrictMask());
        double t = dd.MsOf(totalHours);
        return dd.IsHomeDuring(resident, t, t + 1);
    }

    /// <summary>Residents of a district out on foot at day-ms t (tests, the population table).</summary>
    public int CountOnFoot(LifeDay dd, int district, double t)
    {
        int n = 0;
        var rs = Roster.residents;
        for (int r = 0; r < rs.Length; r++)
        {
            if (district >= 0 && rs[r].district != district) continue;
            if (dd.ExcursionAt(r, t) >= 0) n++;
        }
        return n;
    }

    /// <summary>Cars moving (out or in) at day-ms t, town-wide or in one district.</summary>
    public int CountMovingCars(LifeDay dd, int district, double t)
    {
        int n = 0;
        var cs = Roster.cars;
        for (int c = 0; c < cs.Length; c++)
        {
            if (district >= 0 && cs[c].district != district) continue;
            var ph = dd.CarAt(c, t, out _, out _, out _);
            if (ph == CarPhase.Out || ph == CarPhase.In) n++;
        }
        return n;
    }
}

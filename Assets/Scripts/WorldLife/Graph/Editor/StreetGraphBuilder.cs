using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NodeKind = StreetGraph.NodeKind;
using EdgeKind = StreetGraph.EdgeKind;
using EdgeFlags = StreetGraph.EdgeFlags;

/// <summary>
/// WorldLife package A (Docs/WorldLife.md §5.2): builds a real town's <see cref="StreetGraph"/> from a road description.
/// Editor only: NeighborhoodBuilder describes its roads (axis-aligned centre lines between junctions), houses, gates, exits,
/// park paths and loops, and this turns them into nodes and edges with the W0 schema's conventions:
/// <list type="bullet">
/// <item>Right-hand lanes 2.5 m either side of the centre line, sampled every ≤ 15 m, stopping 9 m before a junction
/// (behind the crosswalk). Junctions get a straight connector or a 4-point turn arc (quadratic Bézier through the point
/// where the two lane lines cross) for every movement except U-turns. Square cul-de-sac bulbs get a counter-clockwise
/// loop lane at 70 % of the bulb's half size. Exits end in an Exit node at the tunnel portal.</item>
/// <item>Sidewalk lanes 7.4 m from the centre line, joined round every junction by its four corner nodes (a crosswalk
/// across every road arm, a plain walk edge round a corner without one), a square ring round each bulb, jaywalks every
/// ~60 m on long blocks.</item>
/// <item>Houses: door → (bends) → path end (GardenPath) → walk join (Walk); the sidewalk sample abeam the curb stop →
/// slot (Walk); a driveway spur lane → ramp → lane (one-way Driveway edges) plus ramp ↔ top.</item>
/// <item>Gates: lane and turnstile-side walk edges across the gate line are GateLocked; the other sidewalk is cut
/// (the gate's sidewalk block stands there).</item>
/// </list>
/// Deterministic: everything is built in input order; no randomness.
/// </summary>
public sealed class StreetGraphBuilder
{
    // ------------------------------------------------------------------ tuning (Docs/WorldLife.md §5.2)

    public const float LaneOffset = StreetGraph.LaneOffset, WalkOffset = StreetGraph.WalkOffset;
    /// <summary>Lanes stop / start this far from a junction centre, an exit portal, an open (pad) end.</summary>
    public const float JunctionInset = 9f, ExitInset = 2f, OpenInset = 0.5f, OpenWalkInset = 0.8f;
    public const float LaneSample = 15f, WalkSample = 15f, RingSample = 9f, LoopStepDeg = 30f;
    public const float RoadY = 0f, WalkY = 0.15f;
    /// <summary>A driveway's lane nodes sit this far before / after its ramp (bulbs: this many degrees).</summary>
    public const float DriveSpan = 4f, BulbDriveSpanDeg = 22f;
    public const float BulbLaneFraction = 0.7f, BulbWalkMargin = 2.4f;
    /// <summary>Gates: lane samples ±3 m, turnstile-side walk samples ±2 m at <see cref="GateWalkOffset"/> from the centre
    /// line (the gap between the pillar and the turnstile post), the other sidewalk cut ±1.5 m.</summary>
    public const float GateLaneSpan = 3f, GateWalkSpan = 2f, GateCutHalf = 1.5f, GateWalkOffset = 6.855f;
    public const float JaywalkMinLength = 70f, JaywalkSpacing = 60f;

    const ushort None = StreetGraph.None;

    public enum EndKind { Junction, Bulb, Exit, Open }

    // ------------------------------------------------------------------ inputs

    public sealed class Junction
    {
        public Vector3 p;
        public EndKind kind;
        /// <summary>Bulb: half size of the square road bulb.</summary>
        public float bulbHalf;
        /// <summary>Exit: the exit id ("exit-west").</summary>
        public string exitId;
        public string name = "";

        internal readonly Dictionary<int, ushort> corners = new Dictionary<int, ushort>();
        internal int bulbSeg = -1;
        internal Vector3[] ringPoly;
        internal float[] ringLen;
        internal readonly List<float> ringExtra = new List<float>();
        internal List<KeyValuePair<float, ushort>> ring;            // arc length → node
        internal readonly List<float> loopExtraDeg = new List<float>();
        internal List<KeyValuePair<float, ushort>> loop;            // φ (deg) → node, entry .. exit
        internal float phiEntry, phiExit, loopRadius;
        internal ushort exitNode = None;
    }

    public sealed class Segment
    {
        public int a, b;
        public string name = "";
        public bool walkRight = true, walkLeft = true, jaywalks = true;
        /// <summary>A DistrictGate on this road at <see cref="gateT"/> m from <see cref="a"/> (null = none). The turnstile
        /// (walkable) side is <see cref="gateWalkSide"/> (+1 = right of a → b).</summary>
        public string gateDistrict;
        public float gateT = -1f;
        public int gateWalkSide = +1;

        internal Vector3 A, B, d, r;
        internal float len, ls, le;
        internal readonly float[] w0 = new float[2], w1 = new float[2];
        internal readonly List<float> fwdExtra = new List<float>(), backExtra = new List<float>();
        internal readonly List<float>[] walkExtra = { new List<float>(), new List<float>() };
        internal List<KeyValuePair<float, ushort>> fwd, back;      // fwd ascending t; back descending t (travel order)
        internal readonly List<KeyValuePair<float, ushort>>[] walk = new List<KeyValuePair<float, ushort>>[2];
        internal readonly float[] cutFrom = { 1f, 1f }, cutTo = { 0f, 0f };
        internal int gateIndex = -1;

        public bool HasWalk(int side) => side > 0 ? walkRight : walkLeft;
        public bool HasGate => !string.IsNullOrEmpty(gateDistrict) && gateT >= 0f;
    }

    /// <summary>A sidewalk point to attach to: a segment side at t, a bulb ring point, or the nearest walk node.</summary>
    public struct WalkRef
    {
        internal int mode;          // 0 none, 1 segment, 2 ring, 3 nearest
        internal int seg, side, junction;
        internal float t;
        internal Vector3 p;
        public static WalkRef Seg(int seg, int side, float t) => new WalkRef { mode = 1, seg = seg, side = side, t = t };
        public static WalkRef Ring(int bulbJunction, Vector3 p) => new WalkRef { mode = 2, junction = bulbJunction, p = p };
        public static WalkRef Near(Vector3 p) => new WalkRef { mode = 3, p = p };
        public static readonly WalkRef Free = new WalkRef { mode = 0 };
    }

    public sealed class HouseSpec
    {
        public string stopId;
        public byte size;
        /// <summary>A point on the house's frontage centre line (the road it faces) and the frontage normal (road → lot).</summary>
        public Vector3 frontPoint, normal;
        public Vector3 door, pathEnd, slot;
        /// <summary>Garden-path bends between the door and the path end (estates walk round their drive), or null.</summary>
        public Vector3[] bends;
        public bool hasDrive;
        public Vector3 ramp, top;
        public Pose car0, car1;
        public byte cars;
        public Pose butlerPost = new Pose(Vector3.zero, Quaternion.identity);
        public Vector3 mailbox;

        // results
        internal int seg = -1, side, bulb = -1;
        internal float tJoin, tSlot, tDrive, uJoin, uSlot, phiDrive;
        public bool driveConnected;
        public string note = "";
    }

    sealed class PathSpec { public WalkRef from, to; public Vector3[] mids; public EdgeKind kind; public NodeKind midKind; }
    sealed class CrossSpec { public int seg; public float t; public EdgeKind kind; }
    sealed class LaneLink { public int fromSeg, toSeg; public bool fromFwd, toFwd; public Vector3[] mids; public EdgeFlags flags; }
    sealed class LoopSpec { public byte district; public Vector3[] waypoints; public string name; }

    public readonly List<Junction> junctions = new List<Junction>();
    public readonly List<Segment> segments = new List<Segment>();
    public readonly List<HouseSpec> houseSpecs = new List<HouseSpec>();
    readonly List<PathSpec> paths = new List<PathSpec>();
    readonly List<CrossSpec> crossings = new List<CrossSpec>();
    readonly List<LaneLink> laneLinks = new List<LaneLink>();
    readonly List<LoopSpec> loopSpecs = new List<LoopSpec>();

    /// <summary>Node district (district byte from a world position). Required.</summary>
    public Func<Vector3, byte> districtAt;
    /// <summary>No jaywalk within r of these points (road-works sites; curb stops with their bins). A blocked jaywalk
    /// slides along its block (±3 / 6 / 9 m) to the first clear spot, or is dropped.</summary>
    public readonly List<(Vector3 p, float r)> jaywalkKeepOut = new List<(Vector3, float)>();
    public int builderSeed = 1234;
    /// <summary>Human-readable notes (unconnected driveways, loop problems...).</summary>
    public readonly StringBuilder notes = new StringBuilder();
    public int warnings;

    // ------------------------------------------------------------------ outputs (while building)

    readonly List<StreetGraph.Node> nodes = new List<StreetGraph.Node>();
    readonly List<StreetGraph.Edge> edges = new List<StreetGraph.Edge>();
    readonly List<StreetGraph.House> houses = new List<StreetGraph.House>();
    readonly List<StreetGraph.Exit> exits = new List<StreetGraph.Exit>();
    readonly List<StreetGraph.Gate> gates = new List<StreetGraph.Gate>();
    readonly List<StreetGraph.Loop> loops = new List<StreetGraph.Loop>();

    // ------------------------------------------------------------------ input API

    public int AddJunction(Vector3 p, EndKind kind = EndKind.Junction, float bulbHalf = 0f, string exitId = null, string name = "")
    {
        junctions.Add(new Junction { p = new Vector3(p.x, 0f, p.z), kind = kind, bulbHalf = bulbHalf, exitId = exitId, name = name });
        return junctions.Count - 1;
    }

    public int AddSegment(int a, int b, string name = "", bool walks = true)
    {
        segments.Add(new Segment { a = a, b = b, name = name, walkLeft = walks, walkRight = walks });
        return segments.Count - 1;
    }

    public void SetGate(int seg, string districtId, float t, int walkSide = +1)
    {
        var s = segments[seg];
        s.gateDistrict = districtId; s.gateT = t; s.gateWalkSide = walkSide;
    }

    public void AddHouse(HouseSpec h) => houseSpecs.Add(h);

    /// <summary>A path between two sidewalk points through <paramref name="mids"/> (a free end when <paramref name="to"/>
    /// is <see cref="WalkRef.Free"/>: the last mid is the end).</summary>
    public void AddPath(WalkRef from, Vector3[] mids, WalkRef to, EdgeKind kind = EdgeKind.ParkPath, NodeKind midKind = NodeKind.Park) =>
        paths.Add(new PathSpec { from = from, to = to, mids = mids ?? new Vector3[0], kind = kind, midKind = midKind });

    /// <summary>A crossing between the two sidewalks of a segment at t (mid-block crosswalk / jaywalk).</summary>
    public void AddCrossing(int seg, float t, EdgeKind kind = EdgeKind.Crosswalk) => crossings.Add(new CrossSpec { seg = seg, t = t, kind = kind });

    /// <summary>A hand-made lane from the last node of one lane to the first node of another (the depot pad).</summary>
    public void AddLaneLink(int fromSeg, bool fromFwd, Vector3[] mids, int toSeg, bool toFwd, EdgeFlags flags) =>
        laneLinks.Add(new LaneLink { fromSeg = fromSeg, fromFwd = fromFwd, mids = mids ?? new Vector3[0], toSeg = toSeg, toFwd = toFwd, flags = flags });

    /// <summary>A precomputed stroll cycle: the shortest sidewalk route through the waypoints and back to the first.</summary>
    public void AddLoop(byte district, string name, params Vector3[] waypoints) =>
        loopSpecs.Add(new LoopSpec { district = district, waypoints = waypoints, name = name });

    /// <summary>Where jaywalks go on a segment of this length (also used to keep road works off them).</summary>
    public static float[] JaywalkTs(float len)
    {
        if (len < JaywalkMinLength) return new float[0];
        int n = Mathf.Max(1, Mathf.FloorToInt(len / JaywalkSpacing));
        var ts = new float[n];
        for (int k = 0; k < n; k++) ts[k] = len * (k + 1) / (n + 1);
        return ts;
    }

    // =================================================================================================================
    // Build
    // =================================================================================================================

    public StreetGraph Build()
    {
        if (districtAt == null) throw new InvalidOperationException("StreetGraphBuilder: districtAt is required");
        PrepareSegments();
        ResolveHouses();
        PlanGatesCrossingsAndRefs();
        for (int i = 0; i < segments.Count; i++) BuildLanes(i);
        for (int j = 0; j < junctions.Count; j++) BuildJunctionLanes(j);
        for (int i = 0; i < segments.Count; i++) { BuildSidewalk(i, +1); BuildSidewalk(i, -1); }
        for (int j = 0; j < junctions.Count; j++) { BuildCornerRing(j); BuildBulbRing(j); }
        for (int h = 0; h < houseSpecs.Count; h++) BuildHouse(houseSpecs[h]);
        BuildCrossings();
        BuildPaths();
        BuildLaneLinks();
        AssignDistricts();
        BuildLoops();
        return Finish();
    }

    // ------------------------------------------------------------------ geometry

    static Vector3 Right(Vector3 d) => new Vector3(d.z, 0f, -d.x);
    static int SI(int side) => side > 0 ? 0 : 1;

    float StartInset(Segment s)
    {
        var j = junctions[s.a];
        switch (j.kind)
        {
            case EndKind.Junction: return JunctionInset;
            case EndKind.Open: return OpenInset;
            default: throw new InvalidOperationException($"segment {s.name}: a {j.kind} must be the segment's b end");
        }
    }

    float EndInset(Segment s)
    {
        var j = junctions[s.b];
        switch (j.kind)
        {
            case EndKind.Junction: return JunctionInset;
            case EndKind.Exit: return ExitInset;
            case EndKind.Open: return OpenInset;
            default:
                float R = j.bulbHalf * BulbLaneFraction;
                return Mathf.Sqrt(R * R - LaneOffset * LaneOffset);
        }
    }

    float WalkStart(Segment s) => junctions[s.a].kind == EndKind.Junction ? WalkOffset : OpenWalkInset;

    float WalkEnd(Segment s)
    {
        var j = junctions[s.b];
        switch (j.kind)
        {
            case EndKind.Junction: return s.len - WalkOffset;
            case EndKind.Bulb: return s.len - (j.bulbHalf + BulbWalkMargin);
            default: return s.len - OpenWalkInset;
        }
    }

    void PrepareSegments()
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            s.A = junctions[s.a].p; s.B = junctions[s.b].p;
            Vector3 v = s.B - s.A; v.y = 0f;
            s.len = v.magnitude;
            s.d = v / s.len;
            s.r = Right(s.d);
            s.ls = StartInset(s);
            s.le = s.len - EndInset(s);
            var jb = junctions[s.b];
            if (jb.kind == EndKind.Exit) s.walkLeft = s.walkRight = false;
            if (jb.kind == EndKind.Bulb)
            {
                if (jb.bulbSeg >= 0) throw new InvalidOperationException($"bulb {jb.name} has two segments");
                jb.bulbSeg = i;
                jb.loopRadius = jb.bulbHalf * BulbLaneFraction;
                float c = Mathf.Acos(LaneOffset / jb.loopRadius) * Mathf.Rad2Deg;
                jb.phiEntry = -c; jb.phiExit = 180f + c;
                float W = jb.bulbHalf + BulbWalkMargin;
                Vector3 din = s.d, rin = s.r, cen = jb.p;
                jb.ringPoly = new[]
                {
                    cen - din * W + rin * WalkOffset, cen - din * W + rin * W, cen + din * W + rin * W,
                    cen + din * W - rin * W, cen - din * W - rin * W, cen - din * W - rin * WalkOffset,
                };
                jb.ringLen = new float[jb.ringPoly.Length];
                for (int k = 1; k < jb.ringPoly.Length; k++) jb.ringLen[k] = jb.ringLen[k - 1] + Vector3.Distance(jb.ringPoly[k - 1], jb.ringPoly[k]);
            }
            for (int si = 0; si < 2; si++) { s.w0[si] = WalkStart(s); s.w1[si] = WalkEnd(s); }
        }
    }

    // ------------------------------------------------------------------ houses: attach + plan samples

    void ResolveHouses()
    {
        foreach (var h in houseSpecs)
        {
            int best = -1; float bestLat = 0.75f; int bestSide = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (junctions[s.b].kind == EndKind.Exit) continue;
                if (Mathf.Abs(Vector3.Dot(h.normal, s.d)) > 0.1f) continue;
                Vector3 rel = h.frontPoint - s.A;
                float t = Vector3.Dot(rel, s.d), lat = Mathf.Abs(Vector3.Dot(rel, s.r));
                if (t < -14f || t > s.len + 14f) continue;
                // prefer the segment that actually contains t (corner houses sit past a segment end)
                float score = lat + (t < 0f ? -t : t > s.len ? t - s.len : 0f) * 0.1f;
                if (score < bestLat) { bestLat = score; best = i; bestSide = Vector3.Dot(h.normal, s.r) > 0f ? +1 : -1; }
            }
            if (best >= 0)
            {
                var s = segments[best];
                h.seg = best; h.side = bestSide;
                int si = SI(bestSide);
                h.tJoin = Vector3.Dot(h.pathEnd - s.A, s.d);
                h.tSlot = Vector3.Dot(h.slot - s.A, s.d);
                s.walkExtra[si].Add(h.tJoin);
                s.walkExtra[si].Add(h.tSlot);
                if (h.hasDrive)
                {
                    h.tDrive = Vector3.Dot(h.ramp - s.A, s.d);
                    h.driveConnected = h.tDrive >= s.ls + 0.5f && h.tDrive <= s.le - 0.5f;
                    if (h.driveConnected)
                    {
                        float tIn = Mathf.Clamp(bestSide > 0 ? h.tDrive - DriveSpan : h.tDrive + DriveSpan, s.ls, s.le);
                        float tOut = Mathf.Clamp(bestSide > 0 ? h.tDrive + DriveSpan : h.tDrive - DriveSpan, s.ls, s.le);
                        var list = bestSide > 0 ? s.fwdExtra : s.backExtra;
                        list.Add(tIn); list.Add(tOut);
                    }
                    else h.note = $"driveway at t {h.tDrive:F1} on {s.name} is outside its lanes ({s.ls:F1}..{s.le:F1}): parked only";
                }
                continue;
            }
            // bulbs (the nearest one: two cul-de-sacs can almost touch)
            int bestBulb = -1; float bestBulbD = float.MaxValue;
            for (int j = 0; j < junctions.Count; j++)
            {
                var jb = junctions[j];
                if (jb.kind != EndKind.Bulb) continue;
                float dist = Vector3.Distance(Flat(h.frontPoint), jb.p);
                if (dist <= jb.bulbHalf + 10f && dist < bestBulbD) { bestBulbD = dist; bestBulb = j; }
            }
            if (bestBulb >= 0)
            {
                int j = bestBulb;
                var jn = junctions[j];
                h.bulb = j;
                h.uJoin = RingParam(jn, h.pathEnd);
                h.uSlot = RingParam(jn, h.slot);
                jn.ringExtra.Add(h.uJoin);
                jn.ringExtra.Add(h.uSlot);
                if (h.hasDrive)
                {
                    h.phiDrive = LoopAngle(jn, h.ramp);
                    h.driveConnected = h.phiDrive > jn.phiEntry + BulbDriveSpanDeg * 0.5f && h.phiDrive < jn.phiExit - BulbDriveSpanDeg * 0.5f;
                    if (h.driveConnected)
                    {
                        jn.loopExtraDeg.Add(Mathf.Max(jn.phiEntry + 3f, h.phiDrive - BulbDriveSpanDeg));
                        jn.loopExtraDeg.Add(Mathf.Min(jn.phiExit - 3f, h.phiDrive + BulbDriveSpanDeg));
                    }
                    else h.note = $"driveway at {h.phiDrive:F0}° on bulb {jn.name} is outside its loop: parked only";
                }
            }
            if (h.seg < 0 && h.bulb < 0) { h.note = "no road found for this house"; warnings++; notes.AppendLine($"    WARN house {h.stopId}: {h.note}"); }
        }
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>Arc-length parameter of the nearest point on a bulb's walk ring.</summary>
    static float RingParam(Junction j, Vector3 p)
    {
        p = Flat(p);
        float best = float.MaxValue, bestU = 0f;
        for (int k = 0; k + 1 < j.ringPoly.Length; k++)
        {
            Vector3 a = Flat(j.ringPoly[k]), b = Flat(j.ringPoly[k + 1]);
            Vector3 ab = b - a;
            float L = ab.magnitude;
            float t = Mathf.Clamp(Vector3.Dot(p - a, ab / L), 0f, L);
            float dd = (a + ab / L * t - p).sqrMagnitude;
            if (dd < best) { best = dd; bestU = j.ringLen[k] + t; }
        }
        return bestU;
    }

    static Vector3 RingPos(Junction j, float u)
    {
        for (int k = 0; k + 1 < j.ringPoly.Length; k++)
        {
            if (u <= j.ringLen[k + 1] + 1e-4f || k + 2 == j.ringPoly.Length)
            {
                float L = j.ringLen[k + 1] - j.ringLen[k];
                float t = L > 1e-4f ? Mathf.Clamp01((u - j.ringLen[k]) / L) : 0f;
                return Vector3.Lerp(j.ringPoly[k], j.ringPoly[k + 1], t);
            }
        }
        return j.ringPoly[j.ringPoly.Length - 1];
    }

    /// <summary>Angle (deg) of a point round a bulb in its (right, in) basis, unwrapped into the loop's range.</summary>
    float LoopAngle(Junction j, Vector3 p)
    {
        var s = segments[j.bulbSeg];
        Vector3 rel = Flat(p) - j.p;
        float phi = Mathf.Atan2(Vector3.Dot(rel, s.d), Vector3.Dot(rel, s.r)) * Mathf.Rad2Deg;   // (-180, 180]
        if (phi < j.phiEntry) phi += 360f;
        return phi;
    }

    Vector3 LoopPos(Junction j, float phiDeg)
    {
        var s = segments[j.bulbSeg];
        float a = phiDeg * Mathf.Deg2Rad;
        return j.p + j.loopRadius * (Mathf.Cos(a) * s.r + Mathf.Sin(a) * s.d);
    }

    void PlanGatesCrossingsAndRefs()
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.HasGate)
            {
                float t = s.gateT;
                s.fwdExtra.Add(t - GateLaneSpan); s.fwdExtra.Add(Mathf.Min(t + GateLaneSpan, s.le));
                s.backExtra.Add(t - GateLaneSpan); s.backExtra.Add(Mathf.Min(t + GateLaneSpan, s.le));
                int ws = SI(s.gateWalkSide), cs = 1 - ws;
                s.walkExtra[ws].Add(t - GateWalkSpan);
                if (t + GateWalkSpan < s.w1[ws] - 0.4f) s.walkExtra[ws].Add(t + GateWalkSpan);
                s.cutFrom[cs] = t - GateCutHalf; s.cutTo[cs] = t + GateCutHalf;
                s.walkExtra[cs].Add(t - GateCutHalf);
                if (t + GateCutHalf < s.w1[cs] - 2f) s.walkExtra[cs].Add(t + GateCutHalf);
            }
            if (s.jaywalks && s.walkLeft && s.walkRight)
                foreach (float t0 in JaywalkTs(s.len))
                {
                    float lo = Mathf.Max(s.w0[0], s.w0[1]) + 3f, hi = Mathf.Min(s.w1[0], s.w1[1]) - 3f;
                    foreach (float shift in new[] { 0f, 3f, -3f, 6f, -6f, 9f, -9f })
                    {
                        float t = t0 + shift;
                        if (t < lo || t > hi) continue;
                        if (s.HasGate && Mathf.Abs(t - s.gateT) < 12f) continue;
                        Vector3 at = s.A + s.d * t;
                        bool blocked = false;
                        foreach (var k in jaywalkKeepOut)
                            if (DistToSegment(Flat(k.p), at - s.r * WalkOffset, at + s.r * WalkOffset) < k.r) { blocked = true; break; }
                        if (blocked) continue;
                        crossings.Add(new CrossSpec { seg = i, t = t, kind = EdgeKind.Jaywalk });
                        break;
                    }
                }
        }
        foreach (var c in crossings)
        {
            var s = segments[c.seg];
            s.walkExtra[0].Add(c.t); s.walkExtra[1].Add(c.t);
        }
        foreach (var p in paths) { PlanRef(p.from); PlanRef(p.to); }
    }

    void PlanRef(WalkRef w)
    {
        if (w.mode == 1) segments[w.seg].walkExtra[SI(w.side)].Add(w.t);
        else if (w.mode == 2) junctions[w.junction].ringExtra.Add(RingParam(junctions[w.junction], w.p));
    }

    // ------------------------------------------------------------------ lanes

    void BuildLanes(int i)
    {
        var s = segments[i];
        float exFrom = 1f, exTo = 0f;
        if (s.HasGate) { exFrom = s.gateT - GateLaneSpan - 0.01f; exTo = s.gateT + GateLaneSpan + 0.01f; }
        var tf = Samples(s.ls, s.le, LaneSample, s.fwdExtra, exFrom, exTo);
        var tb = Samples(s.ls, s.le, LaneSample, s.backExtra, exFrom, exTo);
        s.fwd = new List<KeyValuePair<float, ushort>>(tf.Count);
        foreach (float t in tf) s.fwd.Add(new KeyValuePair<float, ushort>(t, AddNode(s.A + s.d * t + s.r * LaneOffset, RoadY, NodeKind.Lane)));
        s.back = new List<KeyValuePair<float, ushort>>(tb.Count);
        for (int k = tb.Count - 1; k >= 0; k--) s.back.Add(new KeyValuePair<float, ushort>(tb[k], AddNode(s.A + s.d * tb[k] - s.r * LaneOffset, RoadY, NodeKind.Lane)));

        if (s.HasGate)
        {
            gates.Add(new StreetGraph.Gate { districtId = s.gateDistrict });
            s.gateIndex = gates.Count - 1;
        }
        ChainLane(s, s.fwd);
        ChainLane(s, s.back);
        if (s.HasGate)
        {
            // Inbound lane: the one heading into the gated district.
            bool insideTowardB = districtAt(s.A + s.d * (s.gateT + GateLaneSpan)) == WorldLifeIds.DistrictIndex(s.gateDistrict);
            var lane = insideTowardB ? s.fwd : s.back;
            ushort nIn = None, nOut = None;
            for (int k = 0; k + 1 < lane.Count; k++)
                if (Crosses(lane[k].Key, lane[k + 1].Key, s.gateT)) { nIn = lane[k].Value; nOut = lane[k + 1].Value; break; }
            var g = gates[s.gateIndex];
            g.nodeIn = nIn; g.nodeOut = nOut;
            gates[s.gateIndex] = g;
        }
    }

    static bool Crosses(float ta, float tb, float t) => Mathf.Min(ta, tb) < t && Mathf.Max(ta, tb) > t;

    void ChainLane(Segment s, List<KeyValuePair<float, ushort>> list)
    {
        for (int k = 0; k + 1 < list.Count; k++)
        {
            var flags = EdgeFlags.None;
            if (s.HasGate && Crosses(list[k].Key, list[k + 1].Key, s.gateT))
            {
                flags |= EdgeFlags.GateLocked;
                SetGate(list[k].Value, s.gateIndex); SetGate(list[k + 1].Value, s.gateIndex);
            }
            AddEdge(list[k].Value, list[k + 1].Value, EdgeKind.Road, flags);
        }
    }

    void BuildJunctionLanes(int j)
    {
        var jn = junctions[j];
        switch (jn.kind)
        {
            case EndKind.Open: return;
            case EndKind.Exit: BuildExit(j); return;
            case EndKind.Bulb: BuildBulbLoop(j); return;
        }
        var inEnds = new List<(ushort node, Vector3 dir, int seg)>();
        var outStarts = new List<(ushort node, Vector3 dir, int seg)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.b == j) { inEnds.Add((s.fwd[s.fwd.Count - 1].Value, s.d, i)); outStarts.Add((s.back[0].Value, -s.d, i)); }
            if (s.a == j) { inEnds.Add((s.back[s.back.Count - 1].Value, -s.d, i)); outStarts.Add((s.fwd[0].Value, s.d, i)); }
        }
        foreach (var ie in inEnds)
            foreach (var os in outStarts)
            {
                if (ie.seg == os.seg) continue;                       // no U-turns
                float dot = Vector3.Dot(ie.dir, os.dir);
                if (dot < -0.9f) continue;
                if (dot > 0.9f) { AddEdge(ie.node, os.node, EdgeKind.Road, EdgeFlags.Yield); continue; }
                Vector3 c = jn.p + Right(ie.dir) * LaneOffset + Right(os.dir) * LaneOffset;
                Vector3 p0 = nodes[ie.node].pos, p3 = nodes[os.node].pos;
                ushort prev = ie.node;
                for (int k = 1; k <= 2; k++)
                {
                    float t = k / 3f;
                    Vector3 p = (1 - t) * (1 - t) * p0 + 2 * (1 - t) * t * c + t * t * p3;
                    ushort n = AddNode(p, RoadY, NodeKind.Lane);
                    AddEdge(prev, n, EdgeKind.Road, EdgeFlags.Turn | EdgeFlags.Yield);
                    prev = n;
                }
                AddEdge(prev, os.node, EdgeKind.Road, EdgeFlags.Turn | EdgeFlags.Yield);
            }
    }

    void BuildExit(int j)
    {
        var jn = junctions[j];
        Segment s = null;
        foreach (var sg in segments) if (sg.b == j) { s = sg; break; }
        if (s == null) throw new InvalidOperationException($"exit {jn.exitId} has no road");
        ushort x = AddNode(jn.p, RoadY, NodeKind.Exit);
        jn.exitNode = x;
        AddEdge(s.fwd[s.fwd.Count - 1].Value, x, EdgeKind.Road, EdgeFlags.None);
        AddEdge(x, s.back[0].Value, EdgeKind.Road, EdgeFlags.None);
        exits.Add(new StreetGraph.Exit { id = jn.exitId, node = x, portal = new Pose(jn.p, Quaternion.LookRotation(s.d, Vector3.up)) });
    }

    void BuildBulbLoop(int j)
    {
        var jn = junctions[j];
        var s = segments[jn.bulbSeg];
        var phis = new List<float> { jn.phiEntry, jn.phiExit };
        foreach (float e in jn.loopExtraDeg) if (e > jn.phiEntry + 1f && e < jn.phiExit - 1f && !Near(phis, e, 1f)) phis.Add(e);
        int count = Mathf.CeilToInt((jn.phiExit - jn.phiEntry) / LoopStepDeg);
        for (int k = 1; k < count; k++)
        {
            float f = jn.phiEntry + (jn.phiExit - jn.phiEntry) * k / count;
            if (!Near(phis, f, 8f)) phis.Add(f);
        }
        phis.Sort();
        jn.loop = new List<KeyValuePair<float, ushort>>(phis.Count);
        for (int k = 0; k < phis.Count; k++)
        {
            ushort n;
            if (k == 0) n = s.fwd[s.fwd.Count - 1].Value;
            else if (k == phis.Count - 1) n = s.back[0].Value;
            else n = AddNode(LoopPos(jn, phis[k]), RoadY, NodeKind.Lane);
            jn.loop.Add(new KeyValuePair<float, ushort>(phis[k], n));
            if (k > 0) AddEdge(jn.loop[k - 1].Value, n, EdgeKind.Road, EdgeFlags.Turn);
        }
    }

    // ------------------------------------------------------------------ sidewalks

    void BuildSidewalk(int i, int side)
    {
        var s = segments[i];
        int si = SI(side);
        if (!s.HasWalk(side)) return;
        float t0 = s.w0[si], t1 = s.w1[si];
        bool gateSide = s.HasGate && s.gateWalkSide == side;
        bool cutSide = s.HasGate && s.gateWalkSide != side;
        float exFrom = 1f, exTo = 0f;
        if (gateSide) { exFrom = s.gateT - GateWalkSpan - 0.01f; exTo = s.gateT + GateWalkSpan + 0.01f; }
        if (cutSide) { exFrom = s.cutFrom[si] - 0.01f; exTo = s.cutTo[si] + 0.01f; }
        var ts = Samples(t0, t1, WalkSample, s.walkExtra[si], exFrom, exTo);
        if (cutSide) ts.RemoveAll(t => t > s.cutFrom[si] + 0.01f && t < s.cutTo[si] - 0.01f);
        var list = new List<KeyValuePair<float, ushort>>(ts.Count);
        Vector3 n = s.r * side;
        for (int k = 0; k < ts.Count; k++)
        {
            float t = ts[k];
            ushort node;
            if (k == 0 && junctions[s.a].kind == EndKind.Junction) node = Corner(s.a, n + s.d);
            else if (k == ts.Count - 1 && junctions[s.b].kind == EndKind.Junction) node = Corner(s.b, n - s.d);
            else
            {
                float lat = WalkOffset;
                if (gateSide && (Mathf.Abs(t - (s.gateT - GateWalkSpan)) < 0.01f || Mathf.Abs(t - (s.gateT + GateWalkSpan)) < 0.01f)) lat = GateWalkOffset;
                node = AddNode(s.A + s.d * t + n * lat, WalkY, NodeKind.Walk);
            }
            list.Add(new KeyValuePair<float, ushort>(t, node));
        }
        s.walk[si] = list;
        for (int k = 0; k + 1 < list.Count; k++)
        {
            float ta = list[k].Key, tb = list[k + 1].Key;
            if (cutSide && ta <= s.cutFrom[si] + 0.02f && tb >= s.cutTo[si] - 0.02f) continue;   // the gate's sidewalk block
            var flags = EdgeFlags.None;
            if (gateSide && Crosses(ta, tb, s.gateT))
            {
                flags |= EdgeFlags.GateLocked;
                SetGate(list[k].Value, s.gateIndex); SetGate(list[k + 1].Value, s.gateIndex);
            }
            AddEdge(list[k].Value, list[k + 1].Value, EdgeKind.Walk, flags);
        }
    }

    /// <summary>The sidewalk corner of junction j in the quadrant of <paramref name="diag"/> (axis-aligned roads).</summary>
    ushort Corner(int j, Vector3 diag)
    {
        int qx = diag.x >= 0f ? 1 : -1, qz = diag.z >= 0f ? 1 : -1;
        int key = (qx + 1) * 4 + (qz + 1);
        var jn = junctions[j];
        if (jn.corners.TryGetValue(key, out var n)) return n;
        n = AddNode(jn.p + new Vector3(qx * WalkOffset, 0f, qz * WalkOffset), WalkY, NodeKind.Cross);
        jn.corners[key] = n;
        return n;
    }

    void BuildCornerRing(int j)
    {
        var jn = junctions[j];
        if (jn.kind != EndKind.Junction) return;
        Vector3[] quads = { new Vector3(1, 0, 1), new Vector3(-1, 0, 1), new Vector3(-1, 0, -1), new Vector3(1, 0, -1) };
        Vector3[] arms = { Vector3.forward, Vector3.left, Vector3.back, Vector3.right };
        for (int k = 0; k < 4; k++)
        {
            ushort a = Corner(j, quads[k]), b = Corner(j, quads[(k + 1) % 4]);
            AddEdge(a, b, HasArm(j, arms[k]) ? EdgeKind.Crosswalk : EdgeKind.Walk, EdgeFlags.None);
        }
    }

    bool HasArm(int j, Vector3 dir)
    {
        foreach (var s in segments)
        {
            if (s.a == j && Vector3.Dot(s.d, dir) > 0.9f) return true;
            if (s.b == j && Vector3.Dot(-s.d, dir) > 0.9f) return true;
        }
        return false;
    }

    void BuildBulbRing(int j)
    {
        var jn = junctions[j];
        if (jn.kind != EndKind.Bulb) return;
        var s = segments[jn.bulbSeg];
        float total = jn.ringLen[jn.ringLen.Length - 1];
        var us = new List<float> { 0f, total };
        for (int k = 1; k + 1 < jn.ringLen.Length; k++) us.Add(jn.ringLen[k]);          // corners
        foreach (float e in jn.ringExtra) if (e > 0.3f && e < total - 0.3f && !Near(us, e, 0.25f)) us.Add(e);
        for (int k = 0; k + 1 < jn.ringLen.Length; k++)
        {
            float a = jn.ringLen[k], b = jn.ringLen[k + 1];
            int cnt = Mathf.CeilToInt((b - a) / RingSample);
            for (int q = 1; q < cnt; q++) { float u = a + (b - a) * q / cnt; if (!Near(us, u, 1f)) us.Add(u); }
        }
        us.Sort();
        ushort start = s.walk[0] != null ? s.walk[0][s.walk[0].Count - 1].Value : None;
        ushort end = s.walk[1] != null ? s.walk[1][s.walk[1].Count - 1].Value : None;
        jn.ring = new List<KeyValuePair<float, ushort>>(us.Count);
        for (int k = 0; k < us.Count; k++)
        {
            ushort n;
            if (k == 0 && start != None) n = start;
            else if (k == us.Count - 1 && end != None) n = end;
            else n = AddNode(RingPos(jn, us[k]), WalkY, NodeKind.Walk);
            jn.ring.Add(new KeyValuePair<float, ushort>(us[k], n));
            if (k > 0) AddEdge(jn.ring[k - 1].Value, n, EdgeKind.Walk, EdgeFlags.None);
        }
    }

    // ------------------------------------------------------------------ houses

    void BuildHouse(HouseSpec h)
    {
        if (h.seg < 0 && h.bulb < 0) return;
        ushort door = AddNode(h.door, h.door.y, NodeKind.Door);
        ushort prev = door;
        if (h.bends != null)
            foreach (var b in h.bends)
            {
                ushort n = AddNode(b, WalkY, NodeKind.Walk);
                AddEdge(prev, n, EdgeKind.GardenPath, EdgeFlags.None);
                prev = n;
            }
        ushort pathEnd = AddNode(h.pathEnd, WalkY, NodeKind.PathEnd);
        AddEdge(prev, pathEnd, EdgeKind.GardenPath, EdgeFlags.None);

        ushort join, slotJoin;
        if (h.seg >= 0)
        {
            join = WalkNear(segments[h.seg], SI(h.side), h.tJoin);
            slotJoin = WalkNear(segments[h.seg], SI(h.side), h.tSlot);
        }
        else
        {
            join = Nearest(junctions[h.bulb].ring, h.uJoin);
            slotJoin = Nearest(junctions[h.bulb].ring, h.uSlot);
        }
        AddEdge(pathEnd, join, EdgeKind.Walk, EdgeFlags.None);
        ushort slot = AddNode(h.slot, WalkY, NodeKind.Slot);
        AddEdge(slotJoin, slot, EdgeKind.Walk, EdgeFlags.None);

        var house = new StreetGraph.House
        {
            stopId = h.stopId, door = door, pathEnd = pathEnd, walkJoin = join, slot = slot,
            driveIn = None, driveOut = None, cars = 0, size = h.size, district = districtAt(h.slot),
            carSlot0 = h.car0, carSlot1 = h.car1, butlerPost = h.butlerPost, mailbox = h.mailbox,
        };
        if (h.hasDrive && h.driveConnected)
        {
            ushort dIn, dOut;
            if (h.seg >= 0)
            {
                var s = segments[h.seg];
                var lane = h.side > 0 ? s.fwd : s.back;
                float tIn = Mathf.Clamp(h.side > 0 ? h.tDrive - DriveSpan : h.tDrive + DriveSpan, s.ls, s.le);
                float tOut = Mathf.Clamp(h.side > 0 ? h.tDrive + DriveSpan : h.tDrive - DriveSpan, s.ls, s.le);
                dIn = Nearest(lane, tIn); dOut = Nearest(lane, tOut);
            }
            else
            {
                var jn = junctions[h.bulb];
                dIn = Nearest(jn.loop, Mathf.Max(jn.phiEntry + 3f, h.phiDrive - BulbDriveSpanDeg));
                dOut = Nearest(jn.loop, Mathf.Min(jn.phiExit - 3f, h.phiDrive + BulbDriveSpanDeg));
            }
            if (dIn != dOut)
            {
                ushort ramp = AddNode(h.ramp, WalkY, NodeKind.Drive);
                ushort top = AddNode(h.top, WalkY, NodeKind.Drive);
                AddEdge(dIn, ramp, EdgeKind.Driveway, EdgeFlags.OneWay);
                AddEdge(ramp, dOut, EdgeKind.Driveway, EdgeFlags.OneWay);
                AddEdge(ramp, top, EdgeKind.Driveway, EdgeFlags.None);
                house.driveIn = dIn; house.driveOut = dOut; house.cars = h.cars;
            }
            else { h.driveConnected = false; h.note = "driveway lane nodes collapsed: parked only"; }
        }
        if (!string.IsNullOrEmpty(h.note) && h.hasDrive && !h.driveConnected && h.cars > 0)
            notes.AppendLine($"    house {h.stopId}: {h.note} ({h.cars} car{(h.cars == 1 ? "" : "s")})");
        houses.Add(house);
    }

    static ushort Nearest(List<KeyValuePair<float, ushort>> list, float key)
    {
        ushort best = None; float bd = float.MaxValue;
        foreach (var kv in list) { float d = Mathf.Abs(kv.Key - key); if (d < bd) { bd = d; best = kv.Value; } }
        return best;
    }

    ushort WalkNear(Segment s, int si, float t) => s.walk[si] != null && s.walk[si].Count > 0 ? Nearest(s.walk[si], t) : None;

    // ------------------------------------------------------------------ crossings, paths, links

    void BuildCrossings()
    {
        foreach (var c in crossings)
        {
            var s = segments[c.seg];
            if (s.walk[0] == null || s.walk[1] == null) continue;
            ushort a = WalkNear(s, 1, c.t), b = WalkNear(s, 0, c.t);
            if (a == None || b == None || a == b) continue;
            AddEdge(a, b, c.kind, EdgeFlags.None);
        }
    }

    ushort Resolve(WalkRef w)
    {
        switch (w.mode)
        {
            case 1: return WalkNear(segments[w.seg], SI(w.side), w.t);
            case 2: { var j = junctions[w.junction]; return Nearest(j.ring, RingParam(j, w.p)); }
            case 3:
            {
                ushort best = None; float bd = 4f;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var k = nodes[i].kind;
                    if (k != NodeKind.Walk && k != NodeKind.Cross && k != NodeKind.Park) continue;
                    float dx = nodes[i].pos.x - w.p.x, dz = nodes[i].pos.z - w.p.z, d = dx * dx + dz * dz;
                    if (d < bd) { bd = d; best = (ushort)i; }
                }
                return best;
            }
        }
        return None;
    }

    void BuildPaths()
    {
        foreach (var p in paths)
        {
            ushort prev = Resolve(p.from);
            if (prev == None) { warnings++; notes.AppendLine("    WARN a path's start didn't resolve"); continue; }
            foreach (var m in p.mids)
            {
                ushort n = AddNode(m, WalkY, p.midKind);
                AddEdge(prev, n, p.kind, EdgeFlags.None);
                prev = n;
            }
            if (p.to.mode == 0) continue;
            ushort end = Resolve(p.to);
            if (end == None) { warnings++; notes.AppendLine("    WARN a path's end didn't resolve"); continue; }
            if (end != prev) AddEdge(prev, end, p.kind, EdgeFlags.None);
        }
    }

    void BuildLaneLinks()
    {
        foreach (var l in laneLinks)
        {
            var fs = segments[l.fromSeg]; var ts = segments[l.toSeg];
            var fl = l.fromFwd ? fs.fwd : fs.back;
            var tl = l.toFwd ? ts.fwd : ts.back;
            ushort prev = fl[fl.Count - 1].Value;
            foreach (var m in l.mids)
            {
                ushort n = AddNode(m, RoadY, NodeKind.Lane);
                AddEdge(prev, n, EdgeKind.Road, l.flags);
                prev = n;
            }
            AddEdge(prev, tl[0].Value, EdgeKind.Road, l.flags);
        }
    }

    // ------------------------------------------------------------------ districts + loops

    void AssignDistricts()
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            n.district = districtAt(n.pos);
            nodes[i] = n;
        }
        for (int x = 0; x < exits.Count; x++)
        {
            var e = exits[x];
            e.district = nodes[e.node].district;
            exits[x] = e;
        }
    }

    void BuildLoops()
    {
        // Adjacency over stroll edges (Walk, Crosswalk, ParkPath), both directions.
        int n = nodes.Count;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>(4);
        for (int e = 0; e < edges.Count; e++)
        {
            var ed = edges[e];
            if (ed.kind != EdgeKind.Walk && ed.kind != EdgeKind.Crosswalk && ed.kind != EdgeKind.ParkPath) continue;
            if ((ed.flags & EdgeFlags.GateLocked) != 0) continue;
            adj[ed.a].Add(e); adj[ed.b].Add(e);
        }
        foreach (var spec in loopSpecs)
        {
            var wp = new List<int>();
            foreach (var p in spec.waypoints)
            {
                int best = -1; float bd = 9f;
                for (int i = 0; i < n; i++)
                {
                    var k = nodes[i].kind;
                    if ((k != NodeKind.Walk && k != NodeKind.Cross && k != NodeKind.Park) || nodes[i].district != spec.district) continue;
                    float dx = nodes[i].pos.x - p.x, dz = nodes[i].pos.z - p.z, d = dx * dx + dz * dz;
                    if (d < bd) { bd = d; best = i; }
                }
                if (best < 0) { warnings++; notes.AppendLine($"    WARN loop '{spec.name}': no walk node near {p}"); wp = null; break; }
                wp.Add(best);
            }
            if (wp == null) continue;
            var ring = new List<ushort>();
            bool ok = true;
            for (int k = 0; k < wp.Count && ok; k++)
            {
                var leg = ShortestWalk(adj, wp[k], wp[(k + 1) % wp.Count], spec.district);
                if (leg == null) { ok = false; break; }
                for (int q = 0; q < leg.Count - 1; q++) ring.Add((ushort)leg[q]);    // the leg's last node starts the next leg
            }
            var seen = new HashSet<ushort>();
            foreach (var x in ring) if (!seen.Add(x)) { ok = false; break; }
            if (!ok || ring.Count < 3) { warnings++; notes.AppendLine($"    WARN loop '{spec.name}': not a simple cycle ({ring.Count} nodes)"); continue; }
            loops.Add(new StreetGraph.Loop { nodes = ring.ToArray(), district = spec.district });
        }
    }

    /// <summary>Dijkstra over stroll edges inside one district (ties by node index → deterministic).</summary>
    List<int> ShortestWalk(List<int>[] adj, int from, int to, byte district)
    {
        int n = nodes.Count;
        var dist = new float[n];
        var prev = new int[n];
        var done = new bool[n];
        for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
        dist[from] = 0f;
        var open = new SortedSet<(float, int)>();
        open.Add((0f, from));
        while (open.Count > 0)
        {
            var top = open.Min; open.Remove(top);
            int u = top.Item2;
            if (done[u]) continue;
            done[u] = true;
            if (u == to) break;
            foreach (int e in adj[u])
            {
                var ed = edges[e];
                int v = ed.a == u ? ed.b : ed.a;
                if (nodes[v].district != district || done[v]) continue;
                float nd = dist[u] + ed.len;
                if (nd < dist[v] - 1e-4f) { if (dist[v] < float.MaxValue) open.Remove((dist[v], v)); dist[v] = nd; prev[v] = u; open.Add((nd, v)); }
            }
        }
        if (!done[to]) return null;
        var path = new List<int>();
        for (int v = to; v >= 0; v = prev[v]) path.Add(v);
        path.Reverse();
        return path;
    }

    // ------------------------------------------------------------------ primitives

    ushort AddNode(Vector3 p, float y, NodeKind kind)
    {
        if (nodes.Count >= None - 1) throw new InvalidOperationException("StreetGraphBuilder: more nodes than ushort ids allow");
        p.y = y;
        nodes.Add(new StreetGraph.Node { pos = p, kind = kind, district = 0, gate = None });
        return (ushort)(nodes.Count - 1);
    }

    void SetGate(ushort node, int gate)
    {
        var nd = nodes[node];
        nd.gate = (ushort)gate;
        nodes[node] = nd;
    }

    void AddEdge(ushort a, ushort b, EdgeKind kind, EdgeFlags flags)
    {
        if (a == None || b == None) throw new InvalidOperationException($"StreetGraphBuilder: missing node for a {kind} edge");
        if (a == b) return;
        edges.Add(new StreetGraph.Edge { a = a, b = b, kind = kind, len = Vector3.Distance(nodes[a].pos, nodes[b].pos), flags = flags });
    }

    /// <summary>t0, t1, the extras inside (t0, t1), and even samples ≤ step apart that aren't within 1 m of an extra or
    /// inside the excluded interval (exFrom, exTo).</summary>
    static List<float> Samples(float t0, float t1, float step, List<float> extras, float exFrom, float exTo)
    {
        var ts = new List<float> { t0 };
        if (t1 > t0 + 0.05f) ts.Add(t1);
        var ex = new List<float>(extras);
        ex.Sort();
        foreach (float e in ex) if (e > t0 + 0.3f && e < t1 - 0.3f && !Near(ts, e, 0.25f)) ts.Add(e);
        int count = Mathf.Max(1, Mathf.CeilToInt((t1 - t0) / step));
        for (int i = 1; i < count; i++)
        {
            float t = t0 + (t1 - t0) * i / count;
            if (t > exFrom && t < exTo) continue;
            if (!Near(ts, t, 1f)) ts.Add(t);
        }
        ts.Sort();
        return ts;
    }

    static bool Near(List<float> ts, float t, float eps)
    {
        foreach (float x in ts) if (Mathf.Abs(x - t) < eps) return true;
        return false;
    }

    StreetGraph Finish()
    {
        var g = ScriptableObject.CreateInstance<StreetGraph>();
        g.name = "StreetGraph";
        g.version = StreetGraph.SchemaVersion;
        g.builderSeed = builderSeed;
        g.nodes = nodes.ToArray();
        g.edges = edges.ToArray();
        g.houses = houses.ToArray();
        g.exits = exits.ToArray();
        g.gates = gates.ToArray();
        g.loops = loops.ToArray();
        return g;
    }
}

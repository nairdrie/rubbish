using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT: <see cref="Test"/>, the hand-made 2-block street graph packages B, C and D sandbox against
/// before package A's real Resources/StreetGraph.asset exists (Docs/WorldLife.md §12.2). It exercises every schema
/// feature: right-hand lanes, straight + turn connectors at L, T and + junctions, a cul-de-sac turnaround, a town exit
/// with a pad (yield zone), a DistrictGate into a second district, sidewalks with corner crosswalks, a jaywalk, a park
/// path, 8 houses (doors, garden paths, slots, driveways, car slots, mailboxes) and 2 sidewalk loops.
///
/// Layout (XZ metres; roads y = 0, sidewalks y = 0.15; flat ground anywhere works):
/// <code>
///                     (60,100) cul-de-sac  ┐ district 1 "mm-test-*"
///                         |                │ (Minivan Meadows)
///                       gate z = 75 ───────┘
///   (0,60)──────────(60,60)──────────(120,60)
///     |    block 1     |    block 2  park |
///  (0,30)──exit west   |     (90,30)      |
///     |   (−40,30)     |                  |
///   (0,0)───────────(60,0)───────────(120,0)
/// </code>
/// Houses: test-1..test-6 (district 0), mm-test-1..2 (district 1). Exit "test-exit-west" at (−40, 0, 30).
/// </summary>
public sealed partial class StreetGraph
{
    static StreetGraph testGraph;

    /// <summary>The cached hand-made test graph (runtime only, never saved). Identical on every call and machine.</summary>
    public static StreetGraph Test()
    {
        if (testGraph == null) testGraph = BuildTest();
        return testGraph;
    }

    /// <summary>A fresh copy of the test graph.</summary>
    public static StreetGraph BuildTest()
    {
        var b = new TestBuilder();
        b.Build();
        return b.Finish();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetTestGraph() => testGraph = null;

    // =================================================================================================================

    sealed class TestBuilder
    {
        const float JunctionInset = 9f;     // lanes stop / start this far from a junction centre (behind the crosswalk)
        const float DeadEndInset = 3f;
        const float ExitInset = 2f;
        const float LaneSample = 15f, WalkSample = 15f;
        const float RoadY = 0f, WalkY = 0.15f;
        const float BulbLaneRadius = 6.3f;  // 70% of a 9 m bulb
        const float BulbWalkRadius = 12f;
        const float GateZ = 75f;

        enum End { Junction, DeadEnd, Exit }

        struct J { public Vector3 p; public End end; public ushort exitNode; }

        sealed class Seg
        {
            public int a, b;
            public bool walk = true;
            public Vector3 A, B, d, r;
            public float len;
            public readonly List<float> laneExtra = new List<float>();
            public readonly List<float> walkExtra = new List<float>();
            public float gateFrom = -1f, gateTo = -1f;      // a gate between these t values (lanes and sidewalks)
            public readonly List<KeyValuePair<float, ushort>> fwd = new List<KeyValuePair<float, ushort>>();   // ascending t
            public readonly List<KeyValuePair<float, ushort>> back = new List<KeyValuePair<float, ushort>>();  // descending t
            public readonly List<KeyValuePair<float, ushort>> right = new List<KeyValuePair<float, ushort>>(); // ascending t
            public readonly List<KeyValuePair<float, ushort>> left = new List<KeyValuePair<float, ushort>>();  // ascending t
        }

        struct HouseDef { public int seg; public float t; public int side; public string stopId; public byte size, cars; }

        readonly List<Node> nodes = new List<Node>();
        readonly List<Edge> edges = new List<Edge>();
        readonly List<House> houses = new List<House>();
        readonly List<Exit> exits = new List<Exit>();
        readonly List<Gate> gates = new List<Gate>();
        readonly List<Loop> loops = new List<Loop>();
        readonly Dictionary<long, ushort> corners = new Dictionary<long, ushort>();
        J[] js;
        Seg[] segs;

        // ------------------------------------------------------------------ layout

        void Define()
        {
            js = new[]
            {
                new J { p = new Vector3(0f, 0f, 0f) },      // 0  L
                new J { p = new Vector3(60f, 0f, 0f) },     // 1  T
                new J { p = new Vector3(120f, 0f, 0f) },    // 2  L
                new J { p = new Vector3(0f, 0f, 60f) },     // 3  L
                new J { p = new Vector3(60f, 0f, 60f) },    // 4  +
                new J { p = new Vector3(120f, 0f, 60f) },   // 5  L
                new J { p = new Vector3(60f, 0f, 100f), end = End.DeadEnd },  // 6  cul-de-sac (district 1)
                new J { p = new Vector3(-40f, 0f, 30f), end = End.Exit },     // 7  town exit
                new J { p = new Vector3(0f, 0f, 30f) },     // 8  T (exit road junction)
            };
            segs = new[]
            {
                S(0, 1), S(1, 2), S(3, 4), S(4, 5),          // 0 z=0 west, 1 z=0 east, 2 z=60 west, 3 z=60 east
                S(0, 8), S(8, 3),                            // 4, 5: x=0
                S(1, 4), S(2, 5),                            // 6: x=60, 7: x=120
                S(8, 7), S(4, 6),                            // 8: exit road (no sidewalks), 9: to the gated cul-de-sac
            };
            segs[8].walk = false;
            segs[8].laneExtra.Add(12f); segs[8].laneExtra.Add(28f);              // pad edges between x −12 and −28
            var s9 = segs[9];
            s9.gateFrom = 12f; s9.gateTo = 18f;                                  // the gate at z = 75
            s9.laneExtra.Add(12f); s9.laneExtra.Add(18f);
            s9.walkExtra.Add(12f); s9.walkExtra.Add(18f);
            segs[0].walkExtra.Add(50f);                                          // jaywalk
            segs[1].walkExtra.Add(30f); segs[3].walkExtra.Add(30f);              // park path ends
        }

        Seg S(int a, int b)
        {
            var s = new Seg { a = a, b = b, A = js[a].p, B = js[b].p };
            Vector3 v = s.B - s.A;
            s.len = v.magnitude;
            s.d = v / s.len;
            s.r = Right(s.d);
            return s;
        }

        static readonly HouseDef[] HouseDefs =
        {
            new HouseDef { seg = 0, t = 20f, side = -1, stopId = "test-1", size = 1, cars = 1 },
            new HouseDef { seg = 0, t = 42f, side = -1, stopId = "test-2", size = 0, cars = 0 },
            new HouseDef { seg = 0, t = 30f, side = +1, stopId = "test-3", size = 2, cars = 2 },
            new HouseDef { seg = 6, t = 30f, side = -1, stopId = "test-4", size = 1, cars = 1 },
            new HouseDef { seg = 6, t = 40f, side = +1, stopId = "test-5", size = 1, cars = 1 },
            new HouseDef { seg = 7, t = 20f, side = -1, stopId = "test-6", size = 0, cars = 0 },
            new HouseDef { seg = 9, t = 26f, side = +1, stopId = "mm-test-1", size = 1, cars = 1 },
            new HouseDef { seg = 9, t = 30f, side = -1, stopId = "mm-test-2", size = 0, cars = 0 },
        };

        // ------------------------------------------------------------------ build

        public void Build()
        {
            Define();
            // Houses add the lane / sidewalk samples they attach to.
            foreach (var h in HouseDefs)
            {
                var s = segs[h.seg];
                s.walkExtra.Add(h.t);
                if (h.cars > 0)
                {
                    float td = h.t + 6f;
                    s.laneExtra.Add(td - 4f); s.laneExtra.Add(td + 4f);
                }
            }
            for (int i = 0; i < segs.Length; i++) BuildLanes(segs[i]);
            for (int j = 0; j < js.Length; j++) BuildJunction(j);
            for (int i = 0; i < segs.Length; i++) if (segs[i].walk) { BuildSidewalk(segs[i], +1); BuildSidewalk(segs[i], -1); }
            for (int j = 0; j < js.Length; j++) BuildCornerRing(j);
            BuildBulbWalk(segs[9]);
            foreach (var h in HouseDefs) BuildHouse(h);
            BuildExtras();
            BuildLoops();
        }

        public StreetGraph Finish()
        {
            var g = CreateInstance<StreetGraph>();
            g.name = "StreetGraph (test)";
            g.hideFlags = HideFlags.DontSave;
            g.version = SchemaVersion;
            g.builderSeed = 1234;
            g.nodes = nodes.ToArray();
            g.edges = edges.ToArray();
            g.houses = houses.ToArray();
            g.exits = exits.ToArray();
            g.gates = gates.ToArray();
            g.loops = loops.ToArray();
            return g;
        }

        // ------------------------------------------------------------------ lanes

        float StartInset(Seg s) => js[s.a].end == End.Junction ? JunctionInset : 0f;
        float EndInset(Seg s) => js[s.b].end switch { End.DeadEnd => DeadEndInset, End.Exit => ExitInset, _ => JunctionInset };

        void BuildLanes(Seg s)
        {
            float t0 = StartInset(s), t1 = s.len - EndInset(s);
            var ts = Samples(t0, t1, LaneSample, s.laneExtra);
            foreach (float t in ts) s.fwd.Add(new KeyValuePair<float, ushort>(t, AddNode(s.A + s.d * t + s.r * LaneOffset, RoadY, NodeKind.Lane)));
            for (int i = ts.Count - 1; i >= 0; i--) s.back.Add(new KeyValuePair<float, ushort>(ts[i], AddNode(s.A + s.d * ts[i] - s.r * LaneOffset, RoadY, NodeKind.Lane)));
            if (s.gateFrom >= 0f)
            {
                gates.Add(new Gate { districtId = Districts.MinivanMeadows, nodeIn = LaneAt(s.fwd, s.gateFrom), nodeOut = LaneAt(s.fwd, s.gateTo) });
                ushort gi = (ushort)(gates.Count - 1);
                foreach (var list in new[] { s.fwd, s.back })
                    foreach (var kv in list)
                        if (Mathf.Approximately(kv.Key, s.gateFrom) || Mathf.Approximately(kv.Key, s.gateTo)) SetGate(kv.Value, gi);
            }
            Chain(s.fwd, EdgeKind.Road, s);
            Chain(s.back, EdgeKind.Road, s);
        }

        void Chain(List<KeyValuePair<float, ushort>> list, EdgeKind kind, Seg s)
        {
            for (int i = 0; i + 1 < list.Count; i++)
            {
                float ta = list[i].Key, tb = list[i + 1].Key;
                var flags = EdgeFlags.None;
                if (s.gateFrom >= 0f && Mathf.Min(ta, tb) <= s.gateFrom + 0.01f && Mathf.Max(ta, tb) >= s.gateTo - 0.01f) flags |= EdgeFlags.GateLocked;
                Vector3 pa = nodes[list[i].Value].pos, pb = nodes[list[i + 1].Value].pos;
                if (kind == EdgeKind.Road && InPad(pa) && InPad(pb)) flags |= EdgeFlags.Pad;
                AddEdge(list[i].Value, list[i + 1].Value, kind, flags);
            }
        }

        static bool InPad(Vector3 p) => p.x <= -10f && p.x >= -30f && Mathf.Abs(p.z - 30f) < 6f;

        static ushort LaneAt(List<KeyValuePair<float, ushort>> list, float t)
        {
            foreach (var kv in list) if (Mathf.Abs(kv.Key - t) < 0.01f) return kv.Value;
            return None;
        }

        void BuildJunction(int j)
        {
            var jn = js[j];
            // Incoming lane ends and outgoing lane starts at this junction, with their travel directions.
            var inEnds = new List<(ushort node, Vector3 dir, int seg)>();
            var outStarts = new List<(ushort node, Vector3 dir, int seg)>();
            for (int i = 0; i < segs.Length; i++)
            {
                var s = segs[i];
                if (s.b == j) { inEnds.Add((s.fwd[s.fwd.Count - 1].Value, s.d, i)); outStarts.Add((s.back[0].Value, -s.d, i)); }
                if (s.a == j) { inEnds.Add((s.back[s.back.Count - 1].Value, -s.d, i)); outStarts.Add((s.fwd[0].Value, s.d, i)); }
            }

            if (jn.end == End.Exit)
            {
                ushort x = AddNode(jn.p, RoadY, NodeKind.Exit);
                js[j].exitNode = x;
                foreach (var ie in inEnds) AddEdge(ie.node, x, EdgeKind.Road, EdgeFlags.None);
                foreach (var os in outStarts) AddEdge(x, os.node, EdgeKind.Road, EdgeFlags.None);
                exits.Add(new Exit { id = "test-exit-west", node = x, portal = new Pose(jn.p, Quaternion.LookRotation(Vector3.left)), district = DistrictAt(jn.p) });
                return;
            }

            if (jn.end == End.DeadEnd)
            {
                // Turnaround loop lane at 70% of the bulb radius: a left U-turn around the bulb centre.
                var ie = inEnds[0]; var os = outStarts[0];
                Vector3 rin = Right(ie.dir);
                ushort prev = ie.node;
                for (int k = 0; k <= 3; k++)
                {
                    float th = k * 60f * Mathf.Deg2Rad;
                    ushort n = AddNode(jn.p + BulbLaneRadius * (Mathf.Cos(th) * rin + Mathf.Sin(th) * ie.dir), RoadY, NodeKind.Lane);
                    AddEdge(prev, n, EdgeKind.Road, EdgeFlags.Turn);
                    prev = n;
                }
                AddEdge(prev, os.node, EdgeKind.Road, EdgeFlags.Turn);
                return;
            }

            foreach (var ie in inEnds)
            foreach (var os in outStarts)
            {
                if (ie.seg == os.seg) continue;                          // no U-turns at junctions
                float dot = Vector3.Dot(ie.dir, os.dir);
                if (dot < -0.9f) continue;
                if (dot > 0.9f) { AddEdge(ie.node, os.node, EdgeKind.Road, EdgeFlags.Yield); continue; }
                // Turn: quadratic Bézier through the corner where the two lane lines cross (4 points, 3 edges).
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

        // ------------------------------------------------------------------ sidewalks

        void BuildSidewalk(Seg s, int side)
        {
            Vector3 n = s.r * side;
            var list = side > 0 ? s.right : s.left;
            ushort start = Corner(s.a, n + s.d);
            ushort end = js[s.b].end == End.DeadEnd ? AddNode(s.B + n * WalkOffset, WalkY, NodeKind.Walk) : Corner(s.b, n - s.d);
            float t0 = WalkOffset, t1 = js[s.b].end == End.DeadEnd ? s.len : s.len - WalkOffset;
            var ts = Samples(t0, t1, WalkSample, s.walkExtra);
            for (int i = 0; i < ts.Count; i++)
            {
                ushort node = i == 0 ? start : i == ts.Count - 1 ? end : AddNode(s.A + s.d * ts[i] + n * WalkOffset, WalkY, NodeKind.Walk);
                list.Add(new KeyValuePair<float, ushort>(ts[i], node));
                if (s.gateFrom >= 0f && (Mathf.Approximately(ts[i], s.gateFrom) || Mathf.Approximately(ts[i], s.gateTo))) SetGate(node, (ushort)(gates.Count - 1));
            }
            Chain(list, EdgeKind.Walk, s);
        }

        /// <summary>The sidewalk corner of junction j in the quadrant of diagonal <paramref name="diag"/> (shared node).</summary>
        ushort Corner(int j, Vector3 diag)
        {
            int qx = diag.x >= 0f ? 1 : -1, qz = diag.z >= 0f ? 1 : -1;
            long key = (long)j * 16 + (qx + 1) * 4 + (qz + 1);
            if (corners.TryGetValue(key, out var n)) return n;
            n = AddNode(js[j].p + new Vector3(qx * WalkOffset, 0f, qz * WalkOffset), WalkY, NodeKind.Cross);
            corners[key] = n;
            return n;
        }

        /// <summary>Around a junction's four corners (counter-clockwise): a crosswalk across every road arm, a plain walk
        /// edge round every corner with no road.</summary>
        void BuildCornerRing(int j)
        {
            if (js[j].end != End.Junction) return;
            Vector3[] quads = { new Vector3(1, 0, 1), new Vector3(-1, 0, 1), new Vector3(-1, 0, -1), new Vector3(1, 0, -1) };
            Vector3[] arms = { Vector3.forward, Vector3.left, Vector3.back, Vector3.right };   // between quad k and k+1
            for (int k = 0; k < 4; k++)
            {
                ushort a = Corner(j, quads[k]), b = Corner(j, quads[(k + 1) % 4]);
                bool road = HasArm(j, arms[k]);
                AddEdge(a, b, road ? EdgeKind.Crosswalk : EdgeKind.Walk, EdgeFlags.None);
            }
        }

        bool HasArm(int j, Vector3 dir)
        {
            foreach (var s in segs)
            {
                if (s.a == j && Vector3.Dot(s.d, dir) > 0.9f) return true;
                if (s.b == j && Vector3.Dot(-s.d, dir) > 0.9f) return true;
            }
            return false;
        }

        void BuildBulbWalk(Seg s)
        {
            // Wrap the two sidewalk ends around the far side of the bulb.
            ushort rightEnd = s.right[s.right.Count - 1].Value, leftEnd = s.left[s.left.Count - 1].Value;
            ushort prev = rightEnd;
            for (int k = 0; k < 3; k++)
            {
                float th = (30f + 60f * k) * Mathf.Deg2Rad;
                ushort n = AddNode(s.B + BulbWalkRadius * (Mathf.Cos(th) * s.r + Mathf.Sin(th) * s.d), WalkY, NodeKind.Walk);
                AddEdge(prev, n, EdgeKind.Walk, EdgeFlags.None);
                prev = n;
            }
            AddEdge(prev, leftEnd, EdgeKind.Walk, EdgeFlags.None);
        }

        // ------------------------------------------------------------------ houses

        void BuildHouse(HouseDef h)
        {
            var s = segs[h.seg];
            Vector3 n = s.r * h.side;
            Vector3 at = s.A + s.d * h.t;
            var sidewalk = h.side > 0 ? s.right : s.left;
            ushort join = LaneAt(sidewalk, h.t);
            ushort slot = AddNode(at + n * 5.8f, WalkY, NodeKind.Slot);
            ushort pathEnd = AddNode(at + n * 8.2f, WalkY, NodeKind.PathEnd);
            ushort door = AddNode(at + n * 15f, WalkY, NodeKind.Door);
            AddEdge(door, pathEnd, EdgeKind.GardenPath, EdgeFlags.None);
            AddEdge(pathEnd, join, EdgeKind.Walk, EdgeFlags.None);
            AddEdge(join, slot, EdgeKind.Walk, EdgeFlags.None);

            var house = new House
            {
                stopId = h.stopId, door = door, pathEnd = pathEnd, walkJoin = join, slot = slot,
                driveIn = None, driveOut = None, cars = h.cars, size = h.size, district = DistrictAt(nodes[door].pos),
                carSlot0 = new Pose(Vector3.zero, Quaternion.identity), carSlot1 = new Pose(Vector3.zero, Quaternion.identity),
                butlerPost = new Pose(Vector3.zero, Quaternion.identity),
                mailbox = at + n * 8.45f + s.d * 1.2f + Vector3.up * WalkY,
            };
            if (h.cars > 0)
            {
                float td = h.t + 6f;
                var lane = h.side > 0 ? s.fwd : s.back;   // the near lane (drive on the right)
                float tIn = h.side > 0 ? td - 4f : td + 4f, tOut = h.side > 0 ? td + 4f : td - 4f;
                house.driveIn = LaneAt(lane, tIn);
                house.driveOut = LaneAt(lane, tOut);
                ushort ramp = AddNode(s.A + s.d * td + n * 5.3f, WalkY, NodeKind.Drive);
                ushort top = AddNode(s.A + s.d * td + n * 13f, WalkY, NodeKind.Drive);
                AddEdge(house.driveIn, ramp, EdgeKind.Driveway, EdgeFlags.OneWay);
                AddEdge(ramp, house.driveOut, EdgeKind.Driveway, EdgeFlags.OneWay);
                AddEdge(ramp, top, EdgeKind.Driveway, EdgeFlags.None);
                Quaternion faceHouse = Quaternion.LookRotation(n, Vector3.up);
                house.carSlot0 = new Pose(s.A + s.d * td + n * 13f + Vector3.up * WalkY, faceHouse);
                house.carSlot1 = new Pose(s.A + s.d * td + n * 18f + Vector3.up * WalkY, faceHouse);
            }
            houses.Add(house);
        }

        // ------------------------------------------------------------------ jaywalk, park, loops

        void BuildExtras()
        {
            // Jaywalk across the z = 0 road mid-block of block 1 ("headphones guy").
            var s0 = segs[0];
            AddEdge(LaneAt(s0.left, 50f), LaneAt(s0.right, 50f), EdgeKind.Jaywalk, EdgeFlags.None);
            // A park path through block 2: (90, 7.4) -> park (90, 30) -> (90, 52.6).
            ushort park = AddNode(new Vector3(90f, 0f, 30f), 0.1f, NodeKind.Park);
            AddEdge(LaneAt(segs[1].left, 30f), park, EdgeKind.ParkPath, EdgeFlags.None);
            AddEdge(park, LaneAt(segs[3].right, 30f), EdgeKind.ParkPath, EdgeFlags.None);
        }

        void BuildLoops()
        {
            // Block 1 inner ring: S0 north → S6 west → S2 south (back) → S5 east (back) → round J8 → S4 east (back).
            AddLoop(Ring((0, -1, false), (6, -1, false), (2, +1, true), (5, +1, true), (4, +1, true)));
            // Block 2 inner ring: S1 north → S7 west → S3 south (back) → S6 east (back).
            AddLoop(Ring((1, -1, false), (7, -1, false), (3, +1, true), (6, +1, true)));
        }

        List<ushort> Ring(params (int seg, int side, bool reversed)[] parts)
        {
            var ring = new List<ushort>();
            foreach (var p in parts)
            {
                var list = p.side > 0 ? segs[p.seg].right : segs[p.seg].left;
                for (int i = 0; i < list.Count; i++)
                {
                    ushort n = list[p.reversed ? list.Count - 1 - i : i].Value;
                    if (ring.Count > 0 && ring[ring.Count - 1] == n) continue;
                    ring.Add(n);
                }
            }
            if (ring.Count > 1 && ring[0] == ring[ring.Count - 1]) ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        void AddLoop(List<ushort> ring) => loops.Add(new Loop { nodes = ring.ToArray(), district = nodes[ring[0]].district });

        // ------------------------------------------------------------------ primitives

        ushort AddNode(Vector3 p, float y, NodeKind kind)
        {
            p.y = y;
            nodes.Add(new Node { pos = p, kind = kind, district = DistrictAt(p), gate = None });
            return (ushort)(nodes.Count - 1);
        }

        void SetGate(ushort node, ushort gate)
        {
            var nd = nodes[node];
            nd.gate = gate;
            nodes[node] = nd;
        }

        void AddEdge(ushort a, ushort b, EdgeKind kind, EdgeFlags flags)
        {
            if (a == None || b == None) throw new System.InvalidOperationException($"test graph: missing node for a {kind} edge");
            edges.Add(new Edge { a = a, b = b, kind = kind, len = Vector3.Distance(nodes[a].pos, nodes[b].pos), flags = flags });
        }

        static byte DistrictAt(Vector3 p) => p.z > GateZ ? (byte)1 : (byte)0;

        static Vector3 Right(Vector3 d) => new Vector3(d.z, 0f, -d.x);

        /// <summary>t0, t1, the extras inside (t0, t1), and even samples ≤ step apart that aren't within 1 m of an extra.</summary>
        static List<float> Samples(float t0, float t1, float step, List<float> extras)
        {
            var ts = new List<float> { t0, t1 };
            foreach (float e in extras) if (e > t0 + 0.5f && e < t1 - 0.5f && !Near(ts, e, 0.01f)) ts.Add(e);
            int count = Mathf.Max(1, Mathf.CeilToInt((t1 - t0) / step));
            for (int i = 1; i < count; i++)
            {
                float t = t0 + (t1 - t0) * i / count;
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
    }
}

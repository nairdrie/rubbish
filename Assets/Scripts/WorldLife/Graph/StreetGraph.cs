using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// WorldLife W0 CONTRACT (Docs/WorldLife.md §5.2): the town's street graph. NeighborhoodBuilder (package A) fills it and
/// writes Assets/Resources/StreetGraph.asset next to HouseDirectory.asset; at runtime it is read-only data, identical on
/// every machine (no NavMesh, no bake). Sandboxes use <see cref="Test"/>, a hand-made 2-block graph.
///
/// Conventions (metres, world space):
///  - Road lanes drive on the RIGHT, 2.5 m either side of the centre line. Road edges are DIRECTED (a → b). Turn arcs
///    through an intersection are short Road edges flagged <see cref="EdgeFlags.Turn"/> (and <see cref="EdgeFlags.Yield"/>).
///  - Sidewalk lanes sit 7.4 m from the centre line. Every non-Road edge is two-way unless flagged
///    <see cref="EdgeFlags.OneWay"/> (the driveway in / out ramps). Cars use Road + Driveway edges; people use the rest.
///  - District byte = tier − 1 (<see cref="WorldLifeIds.DistrictIndex(DistrictDef)"/>). An edge flagged
///    <see cref="EdgeFlags.GateLocked"/> passes through a DistrictGate: usable only while that gate's district is unlocked
///    (the gate is <see cref="Node.gate"/> of either end).
///  - <see cref="None"/> (0xFFFF) = no node / no gate.
/// The schema is W0's; everything that builds or routes on it (A's builder, D's A*) lives in their own files.
/// </summary>
public sealed partial class StreetGraph : ScriptableObject
{
    public const int SchemaVersion = 1;
    public const ushort None = ushort.MaxValue;
    public const string ResourcePath = "StreetGraph";
    public const string AssetPath = "Assets/Resources/StreetGraph.asset";

    /// <summary>Road-lane offset from the centre line (drive on the right) and the sidewalk-lane offset.</summary>
    public const float LaneOffset = 2.5f, WalkOffset = 7.4f;

    public enum NodeKind : byte
    {
        /// <summary>A road-lane sample (cars).</summary>
        Lane = 0,
        /// <summary>A sidewalk sample.</summary>
        Walk = 1,
        /// <summary>A sidewalk corner at an intersection, where crosswalks start.</summary>
        Cross = 2,
        /// <summary>A house's front door.</summary>
        Door = 3,
        /// <summary>Where a garden path meets the sidewalk.</summary>
        PathEnd = 4,
        /// <summary>A driveway point (curb ramp, driveway top).</summary>
        Drive = 5,
        /// <summary>A house's curb stop (where its bags sit; PickupStop's position).</summary>
        Slot = 6,
        /// <summary>A town exit (tunnel portal).</summary>
        Exit = 7,
        /// <summary>A park-path point.</summary>
        Park = 8,
    }

    public enum EdgeKind : byte
    {
        /// <summary>A road lane (DIRECTED a → b).</summary>
        Road = 0,
        Walk = 1,
        Crosswalk = 2,
        /// <summary>A mid-block crossing (~1 stroll in 6 takes it: "headphones guy").</summary>
        Jaywalk = 3,
        GardenPath = 4,
        Driveway = 5,
        ParkPath = 6,
    }

    [Flags]
    public enum EdgeFlags : byte
    {
        None = 0,
        /// <summary>Enters a shared box (intersection): slow to 3 m/s and wait if it's occupied.</summary>
        Yield = 1,
        /// <summary>A yield zone like the depot pad: cars ≤ 2.5 m/s.</summary>
        Pad = 2,
        /// <summary>Through a DistrictGate: only while that district is unlocked.</summary>
        GateLocked = 4,
        /// <summary>Part of a turn arc.</summary>
        Turn = 8,
        /// <summary>One-way a → b even for a walk-type kind (driveway in / out ramps). Road edges are always one-way.</summary>
        OneWay = 16,
    }

    [Serializable]
    public struct Node
    {
        public Vector3 pos;
        public NodeKind kind;
        public byte district;
        /// <summary>Gate index when this node is the end of a GateLocked edge, else <see cref="None"/>.</summary>
        public ushort gate;
    }

    [Serializable]
    public struct Edge
    {
        public ushort a, b;
        public EdgeKind kind;
        /// <summary>Length along the path (m); ≥ the straight distance.</summary>
        public float len;
        public EdgeFlags flags;
        public bool OneWay => kind == EdgeKind.Road || (flags & EdgeFlags.OneWay) != 0;
        public bool IsWalkKind => kind != EdgeKind.Road && kind != EdgeKind.Driveway;
    }

    [Serializable]
    public struct House
    {
        public string stopId;
        /// <summary>Door → path end (GardenPath) → walk join (a sidewalk node) → slot (the curb stop).</summary>
        public ushort door, pathEnd, walkJoin, slot;
        /// <summary>Lane nodes where a car turns in / rejoins traffic (<see cref="None"/> without a driveway).</summary>
        public ushort driveIn, driveOut;
        /// <summary>Parked car poses (nose toward the house); <see cref="cars"/> says how many are used.</summary>
        public Pose carSlot0, carSlot1;
        public byte cars;
        /// <summary>0 small, 1 medium, 2 big (NeighborhoodBuilder's house size).</summary>
        public byte size;
        public byte district;
        /// <summary>Bluffs: where the butler stands (identity rotation, zero position elsewhere).</summary>
        public Pose butlerPost;
        public Vector3 mailbox;

        public Pose CarSlot(int i) => i <= 0 ? carSlot0 : carSlot1;
        public bool HasDriveway => driveIn != None && driveOut != None;
    }

    [Serializable]
    public struct Exit
    {
        public string id;
        public ushort node;
        /// <summary>The tunnel mouth (forward points out of town).</summary>
        public Pose portal;
        public byte district;
    }

    [Serializable]
    public struct Gate
    {
        public string districtId;
        /// <summary>Lane nodes just outside (in) and inside (out) the gated district.</summary>
        public ushort nodeIn, nodeOut;
    }

    [Serializable]
    public struct Loop
    {
        /// <summary>A closed sidewalk cycle (last → first is an edge too): block rings, park figure-eights.</summary>
        public ushort[] nodes;
        public byte district;
    }

    // ------------------------------------------------------------------ data (serialized)

    public int version = SchemaVersion;
    public int builderSeed;
    public Node[] nodes = new Node[0];
    public Edge[] edges = new Edge[0];
    public House[] houses = new House[0];
    public Exit[] exits = new Exit[0];
    public Gate[] gates = new Gate[0];
    public Loop[] loops = new Loop[0];

    public int NodeCount => nodes != null ? nodes.Length : 0;
    public int EdgeCount => edges != null ? edges.Length : 0;

    // ------------------------------------------------------------------ loading

    /// <summary>Resources/StreetGraph.asset, or null before package A has written it.</summary>
    public static StreetGraph Load()
    {
        try { return Resources.Load<StreetGraph>(ResourcePath); }
        catch (Exception e) { Debug.LogWarning("[StreetGraph] load failed: " + e.Message); return null; }
    }

    /// <summary>A valid graph with nothing in it (runtime only, not saved).</summary>
    public static StreetGraph Empty()
    {
        var g = CreateInstance<StreetGraph>();
        g.name = "StreetGraph (empty)";
        g.hideFlags = HideFlags.DontSave;
        return g;
    }

    // ------------------------------------------------------------------ adjacency (runtime cache)

    [NonSerialized] int[] outStart;   // per node: first traversal index (nodes + 1 entries)
    [NonSerialized] int[] outEdge;    // traversal -> edge index
    [NonSerialized] ushort[] outTo;   // traversal -> the node it reaches
    [NonSerialized] Dictionary<string, int> houseByStop;
    [NonSerialized] House[] houseByStopFor;
    [NonSerialized] Node[] cachedNodes;
    [NonSerialized] Edge[] cachedEdges;

    /// <summary>Drop the runtime caches. Replacing an array rebuilds them automatically; call this after editing an
    /// array's elements in place.</summary>
    public void Invalidate() { cachedNodes = null; cachedEdges = null; outStart = null; houseByStop = null; }

    void EnsureAdjacency()
    {
        if (outStart != null && ReferenceEquals(cachedNodes, nodes) && ReferenceEquals(cachedEdges, edges)) return;
        int n = NodeCount, m = EdgeCount;
        cachedNodes = nodes; cachedEdges = edges;
        var count = new int[n + 1];
        for (int e = 0; e < m; e++)
        {
            var ed = edges[e];
            if (ed.a >= n || ed.b >= n) continue;
            count[ed.a]++;
            if (!ed.OneWay) count[ed.b]++;
        }
        outStart = new int[n + 1];
        for (int i = 0; i < n; i++) outStart[i + 1] = outStart[i] + count[i];
        int total = outStart[n];
        outEdge = new int[total];
        outTo = new ushort[total];
        var fill = new int[n];
        for (int e = 0; e < m; e++)   // edge order = deterministic tie-breaks for routing
        {
            var ed = edges[e];
            if (ed.a >= n || ed.b >= n) continue;
            int k = outStart[ed.a] + fill[ed.a]++;
            outEdge[k] = e; outTo[k] = ed.b;
            if (!ed.OneWay)
            {
                k = outStart[ed.b] + fill[ed.b]++;
                outEdge[k] = e; outTo[k] = ed.a;
            }
        }
    }

    /// <summary>Traversals leaving <paramref name="node"/>: for (int i = g.OutBegin(n); i &lt; g.OutEnd(n); i++) { g.OutEdge(i); g.OutTo(i); }.
    /// Ordered by edge index (deterministic). One-way edges only appear from their <c>a</c> end.</summary>
    public int OutBegin(int node) { EnsureAdjacency(); return outStart[node]; }
    public int OutEnd(int node) { EnsureAdjacency(); return outStart[node + 1]; }
    public int OutEdge(int traversal) => outEdge[traversal];
    public int OutTo(int traversal) => outTo[traversal];

    /// <summary>The other end of an edge.</summary>
    public int Other(int edge, int node) => edges[edge].a == node ? edges[edge].b : edges[edge].a;

    /// <summary>The gate an edge passes through (or −1).</summary>
    public int GateOf(int edge)
    {
        var ed = edges[edge];
        if ((ed.flags & EdgeFlags.GateLocked) == 0) return -1;
        ushort g = nodes[ed.a].gate != None ? nodes[ed.a].gate : nodes[ed.b].gate;
        return g != None && g < gates.Length ? g : -1;
    }

    /// <summary>District byte of a gate.</summary>
    public byte GateDistrict(int gate) => gate >= 0 && gate < gates.Length ? WorldLifeIds.DistrictIndex(gates[gate].districtId) : (byte)0;

    /// <summary>Is the edge usable with this unlocked-district mask (<see cref="WorldLifeIds.UnlockedDistrictMask"/>)?</summary>
    public bool EdgeOpen(int edge, int unlockedMask)
    {
        int g = GateOf(edge);
        if (g < 0) return (edges[edge].flags & EdgeFlags.GateLocked) == 0 || unlockedMask == -1;
        return unlockedMask == -1 || (unlockedMask & (1 << GateDistrict(g))) != 0;
    }

    /// <summary>Index into <see cref="houses"/> for a stopId, or −1.</summary>
    public int HouseIndex(string stopId)
    {
        if (string.IsNullOrEmpty(stopId) || houses == null) return -1;
        if (houseByStop == null || !ReferenceEquals(houseByStopFor, houses))
        {
            houseByStopFor = houses;
            houseByStop = new Dictionary<string, int>(houses.Length, StringComparer.Ordinal);
            for (int i = 0; i < houses.Length; i++)
                if (!string.IsNullOrEmpty(houses[i].stopId)) houseByStop[houses[i].stopId] = i;
        }
        return houseByStop.TryGetValue(stopId, out int h) ? h : -1;
    }

    public int ExitIndex(string id)
    {
        if (exits != null) for (int i = 0; i < exits.Length; i++) if (exits[i].id == id) return i;
        return -1;
    }

    /// <summary>The nearest node (XZ) whose kind bit is in <paramref name="kindMask"/> (1 &lt;&lt; (int)NodeKind), or −1.
    /// Linear: for setup and tests, not per frame.</summary>
    public int Nearest(Vector3 p, int kindMask = ~0, int district = -1)
    {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < NodeCount; i++)
        {
            var nd = nodes[i];
            if ((kindMask & (1 << (int)nd.kind)) == 0) continue;
            if (district >= 0 && nd.district != district) continue;
            float dx = nd.pos.x - p.x, dz = nd.pos.z - p.z, d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public static int KindMask(params NodeKind[] kinds)
    {
        int m = 0;
        foreach (var k in kinds) m |= 1 << (int)k;
        return m;
    }

    // ------------------------------------------------------------------ validation (structure; A adds the scene probes)

    /// <summary>
    /// Structural checks (Docs/WorldLife.md §5.3, the parts that need no scene): indices and kinds, edge lengths, gates,
    /// every door reaching its own slot on foot, every car household reaching an exit by road (gates open), one walk
    /// component per district, loops closed. Appends human-readable errors; returns how many it found.
    /// </summary>
    public int Validate(List<string> errors)
    {
        int before = errors.Count;
        int n = NodeCount;
        if (version != SchemaVersion) errors.Add($"version {version} != schema {SchemaVersion}");
        if (n >= None) errors.Add($"{n} nodes: more than ushort ids allow");

        for (int i = 0; i < n; i++)
        {
            var nd = nodes[i];
            if (!IsFinite(nd.pos)) errors.Add($"node {i}: position not finite");
            if (nd.gate != None && nd.gate >= gates.Length) errors.Add($"node {i}: gate {nd.gate} out of range");
            if (nd.district > 3) errors.Add($"node {i}: district {nd.district} > 3");
        }

        for (int e = 0; e < EdgeCount; e++)
        {
            var ed = edges[e];
            if (ed.a >= n || ed.b >= n) { errors.Add($"edge {e}: node out of range ({ed.a}, {ed.b})"); continue; }
            if (ed.a == ed.b) errors.Add($"edge {e}: a == b ({ed.a})");
            float d = Vector3.Distance(nodes[ed.a].pos, nodes[ed.b].pos);
            if (!(ed.len >= d - 0.01f) || ed.len > d * 1.6f + 0.5f) errors.Add($"edge {e}: len {ed.len:F2} vs distance {d:F2}");
            bool roadEnds = IsRoadNode(nodes[ed.a].kind) && IsRoadNode(nodes[ed.b].kind);
            if (ed.kind == EdgeKind.Road && !roadEnds) errors.Add($"edge {e}: Road between {nodes[ed.a].kind} and {nodes[ed.b].kind}");
            if (ed.IsWalkKind && (!IsWalkNode(nodes[ed.a].kind) || !IsWalkNode(nodes[ed.b].kind)))
                errors.Add($"edge {e}: {ed.kind} between {nodes[ed.a].kind} and {nodes[ed.b].kind}");
            if ((ed.flags & EdgeFlags.GateLocked) != 0 && GateOf(e) < 0) errors.Add($"edge {e}: GateLocked but neither end names a gate");
        }

        for (int g = 0; g < (gates?.Length ?? 0); g++)
        {
            if (gates[g].nodeIn >= n || gates[g].nodeOut >= n) errors.Add($"gate {g}: node out of range");
            if (string.IsNullOrEmpty(gates[g].districtId)) errors.Add($"gate {g}: no districtId");
        }

        var exitNodes = new List<int>();
        for (int x = 0; x < (exits?.Length ?? 0); x++)
        {
            if (exits[x].node >= n || nodes[exits[x].node].kind != NodeKind.Exit) errors.Add($"exit {x} '{exits[x].id}': node is not an Exit node");
            else exitNodes.Add(exits[x].node);
        }

        var seenStops = new HashSet<string>(StringComparer.Ordinal);
        var reach = new bool[n];
        var queue = new Queue<int>();
        for (int h = 0; h < (houses?.Length ?? 0); h++)
        {
            var hs = houses[h];
            string tag = $"house {h} '{hs.stopId}'";
            if (string.IsNullOrEmpty(hs.stopId)) errors.Add($"house {h}: empty stopId");
            else if (!seenStops.Add(hs.stopId)) errors.Add($"{tag}: duplicate stopId");
            if (!KindIs(hs.door, NodeKind.Door)) { errors.Add($"{tag}: door is not a Door node"); continue; }
            if (!KindIs(hs.pathEnd, NodeKind.PathEnd)) errors.Add($"{tag}: pathEnd is not a PathEnd node");
            if (!KindIs(hs.walkJoin, NodeKind.Walk) && !KindIs(hs.walkJoin, NodeKind.Cross)) errors.Add($"{tag}: walkJoin is not a sidewalk node");
            if (!KindIs(hs.slot, NodeKind.Slot)) { errors.Add($"{tag}: slot is not a Slot node"); continue; }
            Flood(hs.door, reach, queue, walk: true, unlockedMask: -1, district: -1);
            if (!reach[hs.slot]) errors.Add($"{tag}: the door doesn't reach its own slot on foot");
            if (hs.cars > 0)
            {
                if (!KindIs(hs.driveIn, NodeKind.Lane) || !KindIs(hs.driveOut, NodeKind.Lane)) { errors.Add($"{tag}: has cars but driveIn/driveOut aren't lane nodes"); continue; }
                Flood(hs.driveOut, reach, queue, walk: false, unlockedMask: -1, district: -1);
                bool anyExit = false;
                foreach (int x in exitNodes) if (reach[x]) { anyExit = true; break; }
                if (!anyExit) errors.Add($"{tag}: has cars but no exit is reachable by road");
                else
                {
                    // ...and back home: some exit reaches driveIn.
                    bool home = false;
                    foreach (int x in exitNodes)
                    {
                        Flood(x, reach, queue, walk: false, unlockedMask: -1, district: -1);
                        if (reach[hs.driveIn]) { home = true; break; }
                    }
                    if (!home) errors.Add($"{tag}: no exit reaches its driveway by road");
                }
            }
        }

        // One walk component per district (moving only through that district's own nodes).
        for (int d = 0; d < 4; d++)
        {
            int start = -1, total = 0;
            for (int i = 0; i < n; i++) if (nodes[i].district == d && IsWalkNode(nodes[i].kind)) { total++; if (start < 0) start = i; }
            if (start < 0) continue;
            Flood(start, reach, queue, walk: true, unlockedMask: -1, district: d);
            int got = 0;
            for (int i = 0; i < n; i++) if (reach[i] && nodes[i].district == d && IsWalkNode(nodes[i].kind)) got++;
            if (got != total) errors.Add($"district {d}: walk network has {total - got} of {total} nodes cut off from the rest");
        }

        for (int l = 0; l < (loops?.Length ?? 0); l++)
        {
            var lp = loops[l].nodes;
            if (lp == null || lp.Length < 3) { errors.Add($"loop {l}: fewer than 3 nodes"); continue; }
            for (int i = 0; i < lp.Length; i++)
            {
                int a = lp[i], b = lp[(i + 1) % lp.Length];
                if (a >= n || b >= n || FindWalkEdge(a, b) < 0) { errors.Add($"loop {l}: no walk edge {a} -> {b}"); break; }
            }
        }
        return errors.Count - before;
    }

    /// <summary>The walk-type edge joining a and b (either direction), or −1.</summary>
    public int FindWalkEdge(int a, int b)
    {
        for (int i = OutBegin(a); i < OutEnd(a); i++)
            if (OutTo(i) == b && edges[OutEdge(i)].IsWalkKind) return OutEdge(i);
        return -1;
    }

    /// <summary>Breadth-first reach from <paramref name="start"/> over walk-type edges (walk) or Road / Driveway edges
    /// (!walk), honouring the gate mask (−1 = all open) and, when district ≥ 0, staying inside that district.</summary>
    public void Flood(int start, bool[] reach, Queue<int> queue, bool walk, int unlockedMask, int district)
    {
        Array.Clear(reach, 0, reach.Length);
        queue.Clear();
        if (start < 0 || start >= NodeCount) return;
        reach[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            for (int i = OutBegin(u); i < OutEnd(u); i++)
            {
                int e = OutEdge(i), v = OutTo(i);
                if (reach[v]) continue;
                bool kindOk = walk ? edges[e].IsWalkKind : !edges[e].IsWalkKind;
                if (!kindOk || !EdgeOpen(e, unlockedMask)) continue;
                if (district >= 0 && nodes[v].district != district) continue;
                reach[v] = true;
                queue.Enqueue(v);
            }
        }
    }

    bool KindIs(ushort node, NodeKind k) => node != None && node < NodeCount && nodes[node].kind == k;
    static bool IsRoadNode(NodeKind k) => k == NodeKind.Lane || k == NodeKind.Exit || k == NodeKind.Drive;
    static bool IsWalkNode(NodeKind k) => k != NodeKind.Lane && k != NodeKind.Exit && k != NodeKind.Drive;
    static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

    /// <summary>"nodes 312 (d0 270, d1 42) · edges 405 · houses 8 · exits 1 · gates 1 · loops 2" plus per-kind counts.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        var perDistrict = new int[4];
        var perKind = new int[Enum.GetValues(typeof(NodeKind)).Length];
        for (int i = 0; i < NodeCount; i++) { perDistrict[Mathf.Min(3, (int)nodes[i].district)]++; perKind[(int)nodes[i].kind]++; }
        var edgeKinds = new int[Enum.GetValues(typeof(EdgeKind)).Length];
        var edgeDistrict = new int[4];
        for (int e = 0; e < EdgeCount; e++) { edgeKinds[(int)edges[e].kind]++; edgeDistrict[Mathf.Min(3, (int)nodes[edges[e].a].district)]++; }
        var loopDistrict = new int[4];
        for (int l = 0; l < (loops?.Length ?? 0); l++) loopDistrict[Mathf.Min(3, (int)loops[l].district)]++;
        sb.Append($"nodes {NodeCount} · edges {EdgeCount} · houses {houses?.Length ?? 0} · exits {exits?.Length ?? 0} · gates {gates?.Length ?? 0} · loops {loops?.Length ?? 0}\n");
        for (int d = 0; d < 4; d++)
            if (perDistrict[d] > 0) sb.Append($"  district {d}: nodes {perDistrict[d]}, edges {edgeDistrict[d]}, loops {loopDistrict[d]}\n");
        sb.Append("  node kinds:");
        for (int k = 0; k < perKind.Length; k++) if (perKind[k] > 0) sb.Append($" {(NodeKind)k} {perKind[k]}");
        sb.Append("\n  edge kinds:");
        for (int k = 0; k < edgeKinds.Length; k++) if (edgeKinds[k] > 0) sb.Append($" {(EdgeKind)k} {edgeKinds[k]}");
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using NK = StreetGraph.NodeKind;
using EK = StreetGraph.EdgeKind;
using EF = StreetGraph.EdgeFlags;

/// <summary>Sampled coastal streets, including curved roads. Consumes the same polylines as the scene and map.</summary>
public static class CoastalCityGraphBuilder
{
    const ushort None = StreetGraph.None;
    sealed class Line
    {
        public Vector3 a, b;
        public CoastalCityLayout.RoadDef road;
        public readonly List<float> cuts = new List<float> { 0, 1 };
    }
    sealed class Vertex
    {
        public Vector3 p;
        public readonly List<Port> ports = new List<Port>();
    }
    sealed class Port
    {
        public Vector3 direction;
        public ushort incoming, outgoing, left, right;
        public int segment;
    }
    sealed class Segment
    {
        public int a, b;
        public CoastalCityLayout.RoadDef road;
    }
    sealed class BuildState
    {
        public readonly List<Vertex> vertices = new List<Vertex>();
        public readonly List<Segment> segments = new List<Segment>();
        public readonly List<StreetGraph.Node> nodes = new List<StreetGraph.Node>();
        public readonly List<StreetGraph.Edge> edges = new List<StreetGraph.Edge>();
        public readonly List<StreetGraph.House> houses = new List<StreetGraph.House>();
        public readonly List<StreetGraph.Exit> exits = new List<StreetGraph.Exit>();
        public readonly List<StreetGraph.Gate> gates = new List<StreetGraph.Gate>();
        public ushort Node(Vector3 p, NK kind, byte district)
        {
            if (nodes.Count >= None) throw new InvalidOperationException("Coastal graph exceeds 65,534 nodes");
            nodes.Add(new StreetGraph.Node { pos = p, kind = kind, district = district, gate = None });
            return (ushort)(nodes.Count - 1);
        }
        public void Edge(ushort a, ushort b, EK kind, EF flags = EF.None)
        {
            if (a == b) return;
            float length = Vector3.Distance(nodes[a].pos, nodes[b].pos);
            if (length < .001f) return;
            edges.Add(new StreetGraph.Edge { a = a, b = b, len = length, kind = kind, flags = flags });
        }
        public int VertexAt(Vector3 p)
        {
            for (int i = 0; i < vertices.Count; i++) if ((vertices[i].p - p).sqrMagnitude < .04f) return i;
            vertices.Add(new Vertex { p = p }); return vertices.Count - 1;
        }
    }

    public static StreetGraph Build(Transform town, IReadOnlyList<PickupStop> stops)
    {
        var s = new BuildState();
        SplitRoads(s);
        for (int i = 0; i < s.segments.Count; i++) BuildSegment(s, i);
        foreach (var v in s.vertices) ConnectJunction(s, v);
        AddPark(s);
        AddExit(s);
        AddHouses(s, stops);
        AddGates(s, town);
        var graph = ScriptableObject.CreateInstance<StreetGraph>();
        graph.name = "StreetGraph";
        graph.layoutId = "coastal-v4";
        graph.builderSeed = 1234;
        graph.nodes = s.nodes.ToArray(); graph.edges = s.edges.ToArray(); graph.houses = s.houses.ToArray();
        graph.exits = s.exits.ToArray(); graph.gates = s.gates.ToArray();
        graph.loops = BuildLoops(graph);
        graph.Invalidate();
        return graph;
    }

    static float Cross(Vector3 a, Vector3 b) => a.x * b.z - a.z * b.x;
    static Vector3 Right(Vector3 d) => new Vector3(d.z, 0, -d.x);
    static byte District(string id) => (byte)Districts.IndexOf(id);

    static void SplitRoads(BuildState s)
    {
        var lines = new List<Line>();
        foreach (var road in CoastalCityLayout.Roads)
            for (int i = 1; i < road.points.Length; i++)
                if ((road.points[i] - road.points[i - 1]).sqrMagnitude > .04f)
                    lines.Add(new Line { a = road.points[i - 1], b = road.points[i], road = road });
        // T junctions can land in the middle of a long road. Split intersections before creating lane offsets.
        for (int i = 0; i < lines.Count; i++) for (int j = i + 1; j < lines.Count; j++)
        {
            var a = lines[i]; var b = lines[j]; var u = a.b - a.a; var v = b.b - b.a;
            float denominator = Cross(u, v);
            if (Mathf.Abs(denominator) > .0001f)
            {
                float t = Cross(b.a - a.a, v) / denominator, q = Cross(b.a - a.a, u) / denominator;
                if (t >= -.0001f && t <= 1.0001f && q >= -.0001f && q <= 1.0001f)
                { a.cuts.Add(Mathf.Clamp01(t)); b.cuts.Add(Mathf.Clamp01(q)); }
            }
            else
            {
                CutAt(a, b.a); CutAt(a, b.b); CutAt(b, a.a); CutAt(b, a.b);
            }
        }
        var seen = new HashSet<ulong>();
        foreach (var line in lines)
        {
            line.cuts.Sort();
            for (int i = 1; i < line.cuts.Count; i++)
            {
                var a = Vector3.Lerp(line.a, line.b, line.cuts[i - 1]); var b = Vector3.Lerp(line.a, line.b, line.cuts[i]);
                if ((a - b).sqrMagnitude < .04f) continue;
                int ai = s.VertexAt(a), bi = s.VertexAt(b); if (ai == bi) continue;
                ulong key = ((ulong)Mathf.Min(ai, bi) << 32) | (uint)Mathf.Max(ai, bi);
                if (!seen.Add(key)) continue;
                s.segments.Add(new Segment { a = ai, b = bi, road = line.road });
            }
        }
    }
    static void CutAt(Line line, Vector3 p)
    {
        var d = line.b - line.a; float t = Vector3.Dot(p - line.a, d) / d.sqrMagnitude;
        if (t > 0 && t < 1 && (line.a + d * t - p).sqrMagnitude < .04f) line.cuts.Add(t);
    }

    static void BuildSegment(BuildState s, int index)
    {
        var seg = s.segments[index]; var av = s.vertices[seg.a]; var bv = s.vertices[seg.b];
        var delta = bv.p - av.p; float length = delta.magnitude; var dir = delta / length; var right = Right(dir);
        float inset = Mathf.Min(5f, length * .18f), lane = Mathf.Min(StreetGraph.LaneOffset, seg.road.halfWidth * .5f);
        float walk = seg.road.halfWidth + 2.4f;
        int count = Mathf.Max(1, Mathf.CeilToInt((length - 2 * inset) / 12f));
        byte district = District(seg.road.districtId);
        ushort[] fwd = new ushort[count + 1], back = new ushort[count + 1], wr = new ushort[count + 1], wl = new ushort[count + 1];
        for (int i = 0; i <= count; i++)
        {
            var p = av.p + dir * Mathf.Lerp(inset, length - inset, i / (float)count);
            fwd[i] = s.Node(p + right * lane, NK.Lane, district);
            back[i] = s.Node(p - right * lane, NK.Lane, district);
            wr[i] = s.Node(p + right * walk + Vector3.up * .15f, NK.Walk, district);
            wl[i] = s.Node(p - right * walk + Vector3.up * .15f, NK.Walk, district);
            if (i > 0)
            {
                s.Edge(fwd[i - 1], fwd[i], EK.Road); s.Edge(back[i], back[i - 1], EK.Road);
                s.Edge(wr[i - 1], wr[i], EK.Walk); s.Edge(wl[i - 1], wl[i], EK.Walk);
            }
        }
        av.ports.Add(new Port { direction = dir, outgoing = fwd[0], incoming = back[0], right = wr[0], left = wl[0], segment = index });
        bv.ports.Add(new Port { direction = -dir, outgoing = back[count], incoming = fwd[count], right = wl[count], left = wr[count], segment = index });
    }

    static void ConnectJunction(BuildState s, Vertex v)
    {
        int count = v.ports.Count;
        foreach (var from in v.ports) foreach (var to in v.ports)
        {
            if (from == to && count > 1) continue;
            Vector3 a = s.nodes[from.incoming].pos, b = s.nodes[to.outgoing].pos;
            // Short quadratic connectors round the bend without putting a stop/yield at every curve sample.
            Vector3 control = (a + b) * .5f;
            Vector3 incomingDirection = -from.direction, outgoingDirection = to.direction;
            float dot = Vector3.Dot(incomingDirection, outgoingDirection);
            float denominator = Cross(incomingDirection, outgoingDirection);
            if (count == 1) control = v.p - from.direction * 3f;
            else if (Mathf.Abs(denominator) > .001f)
            {
                float along = Cross(b - a, outgoingDirection) / denominator;
                var intersection = a + incomingDirection * along;
                if ((intersection - v.p).sqrMagnitude < 100f) control = intersection;
            }
            ushort previous = from.incoming;
            int samples = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(a, b) / 3f));
            EF flags = (dot < .94f ? EF.Turn : EF.None) | (count > 2 ? EF.Yield : EF.None);
            for (int k = 1; k <= samples; k++)
            {
                float t = k / (float)samples, u = 1 - t;
                ushort next = k == samples ? to.outgoing : s.Node(u * u * a + 2 * u * t * control + t * t * b, NK.Lane, s.nodes[from.incoming].district);
                s.Edge(previous, next, EK.Road, flags); previous = next;
            }
        }
        var walk = new List<ushort>();
        foreach (var p in v.ports) { walk.Add(p.left); walk.Add(p.right); }
        walk.Sort((a, b) => Mathf.Atan2(s.nodes[a].pos.z - v.p.z, s.nodes[a].pos.x - v.p.x)
            .CompareTo(Mathf.Atan2(s.nodes[b].pos.z - v.p.z, s.nodes[b].pos.x - v.p.x)));
        for (int i = 0; i < walk.Count; i++)
        {
            ushort a = walk[i], b = walk[(i + 1) % walk.Count];
            bool crossing = false;
            foreach (var p in v.ports) if ((p.left == a && p.right == b) || (p.right == a && p.left == b)) crossing = true;
            s.Edge(a, b, crossing ? EK.Crosswalk : EK.Walk);
        }
    }

    static void AddExit(BuildState s)
    {
        var target = CoastalCityLayout.Road("home").points[0];
        foreach (var v in s.vertices)
        {
            if ((v.p - target).sqrMagnitude > .1f || v.ports.Count == 0) continue;
            var port = v.ports[0]; byte district = s.nodes[port.incoming].district;
            ushort node = s.Node(v.p, NK.Exit, district);
            s.Edge(port.incoming, node, EK.Road); s.Edge(node, port.outgoing, EK.Road);
            s.exits.Add(new StreetGraph.Exit { id = "exit-west", node = node,
                portal = new Pose(v.p, Quaternion.LookRotation(-port.direction)), district = district });
            return;
        }
        throw new InvalidOperationException("Coastal graph: Home Run Road exit not found");
    }

    static int Nearest(BuildState s, Vector3 p, NK kind)
    {
        int found = -1; float best = float.MaxValue;
        for (int i = 0; i < s.nodes.Count; i++) if (s.nodes[i].kind == kind)
        { float d = (s.nodes[i].pos - p).sqrMagnitude; if (d < best) { best = d; found = i; } }
        return found;
    }
    static void AddPark(BuildState s)
    {
        Vector3 centre = CoastalCityLayout.Landmark("pickle-park") + Vector3.up * .15f;
        byte district = District(Districts.MildlyPleasant);
        ushort park = s.Node(centre, NK.Park, district);
        foreach (var crossing in CoastalParkRoutes.Crossings())
        {
            var right = Right(crossing.forward); float width = crossing.halfWidth + 2.4f;
            var center = crossing.position + Vector3.up * .15f;
            var a = center + right * width; var b = center - right * width;
            // Capture the pre-existing sidewalk before adding the new crossing nodes.
            int nearA = Nearest(s, a, NK.Walk), nearB = Nearest(s, b, NK.Walk);
            ushort na = s.Node(a, NK.Cross, district), nb = s.Node(b, NK.Cross, district);
            if (nearA >= 0) s.Edge((ushort)nearA, na, EK.Walk);
            if (nearB >= 0) s.Edge((ushort)nearB, nb, EK.Walk);
            s.Edge(na, nb, EK.Crosswalk);
            ushort side = (a - centre).sqrMagnitude <= (b - centre).sqrMagnitude ? na : nb;
            Vector3 end = s.nodes[side].pos; int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(centre, end) / 6));
            ushort previous = park;
            for (int i = 1; i <= count; i++)
            {
                ushort next = i == count ? side : s.Node(Vector3.Lerp(centre, end, i / (float)count), NK.Park, district);
                s.Edge(previous, next, EK.ParkPath); previous = next;
            }
        }
    }
    static void AddHouses(BuildState s, IReadOnlyList<PickupStop> stops)
    {
        if (stops == null) return;
        var ordered = new List<PickupStop>(); foreach (var stop in stops) if (stop != null) ordered.Add(stop);
        ordered.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
        foreach (var stop in ordered)
        {
            var meta = stop.GetComponent<CoastalHouseRoute>();
            var p = stop.transform.position; var back = -stop.transform.forward;
            byte district = District(stop.District.id);
            int near = Nearest(s, p, NK.Walk); if (near < 0) continue;
            var doorP = meta != null ? meta.door : p + back * 9;
            var pathP = meta != null ? meta.pathEnd : p + back * 2;
            ushort door = s.Node(doorP, NK.Door, district), path = s.Node(pathP, NK.PathEnd, district), slot = s.Node(p, NK.Slot, district);
            // A real side-walk join remains in the household's district even at a boundary street.
            ushort join = s.Node(s.nodes[near].pos + Vector3.up * .01f, NK.Walk, district);
            s.Edge((ushort)near, join, EK.Walk); s.Edge(join, path, EK.Walk); s.Edge(join, slot, EK.Walk);
            ushort garden = door;
            if (meta != null && meta.bends != null) foreach (var bend in meta.bends)
            {
                ushort next = s.Node(bend, NK.PathEnd, district); s.Edge(garden, next, EK.GardenPath); garden = next;
            }
            s.Edge(garden, path, EK.GardenPath);
            var house = new StreetGraph.House { stopId = stop.stopId, district = district, door = door, pathEnd = path, slot = slot,
                walkJoin = join, driveIn = None, driveOut = None, size = meta != null ? meta.size : (byte)Mathf.Clamp(stop.Capacity - 3, 0, 2),
                mailbox = meta != null ? meta.mailbox : pathP, butlerPost = meta != null ? meta.butlerPost : new Pose(doorP, stop.transform.rotation) };
            if (meta != null && meta.hasDrive && meta.cars > 0)
            {
                Vector3 tangent = stop.transform.right;
                int lane = Nearest(s, meta.ramp, NK.Lane);
                if (lane >= 0)
                {
                    // Infer this lane's travel direction from its authored outgoing road edge.
                    foreach (var edge in s.edges) if (edge.a == lane && edge.kind == EK.Road)
                    { tangent = (s.nodes[edge.b].pos - s.nodes[lane].pos).normalized; break; }
                    house.driveIn = (ushort)Nearest(s, s.nodes[lane].pos - tangent * 4, NK.Lane);
                    house.driveOut = (ushort)Nearest(s, s.nodes[lane].pos + tangent * 4, NK.Lane);
                    ushort ramp = s.Node(meta.ramp, NK.Drive, district), top = s.Node(meta.top, NK.Drive, district);
                    s.Edge(house.driveIn, ramp, EK.Driveway, EF.OneWay); s.Edge(ramp, house.driveOut, EK.Driveway, EF.OneWay);
                    s.Edge(ramp, top, EK.Driveway); house.cars = meta.cars; house.carSlot0 = meta.car0; house.carSlot1 = meta.car1;
                }
            }
            s.houses.Add(house);
        }
    }

    static void AddGates(BuildState s, Transform town)
    {
        var actual = town != null ? town.GetComponentsInChildren<DistrictGate>(true) : Array.Empty<DistrictGate>();
        foreach (var gate in CoastalCityLayout.Gates)
        {
            Vector3 p = gate.position, normal = gate.forward;
            foreach (var g in actual) if (g.districtId == gate.districtId && (g.transform.position - p).sqrMagnitude < 400)
            { p = g.transform.position; normal = g.transform.forward; break; }
            // Even pure-data tests use the exact closest road centre; curved approaches need their own tangent.
            float nearest = float.MaxValue;
            foreach (var road in CoastalCityLayout.Roads) for (int i = 1; i < road.points.Length; i++)
            {
                Vector3 delta = road.points[i] - road.points[i - 1];
                var q = road.points[i - 1] + delta * Mathf.Clamp01(Vector3.Dot(p - road.points[i - 1], delta) / delta.sqrMagnitude);
                float d = (q - p).sqrMagnitude;
                if (d >= nearest) continue; nearest = d;
                normal = delta.normalized * (Vector3.Dot(delta, gate.forward) >= 0 ? 1 : -1);
            }
            ushort gi = (ushort)s.gates.Count, inside = None, outside = None;
            for (int i = 0; i < s.edges.Count; i++)
            {
                var e = s.edges[i]; var a = s.nodes[e.a]; var b = s.nodes[e.b];
                float da = Vector3.Dot(a.pos - p, normal), db = Vector3.Dot(b.pos - p, normal);
                if (da * db > .0001f || Mathf.Abs(da - db) < .0001f) continue;
                var cross = Vector3.Lerp(a.pos, b.pos, Mathf.Clamp01(da / (da - db)));
                if ((cross - p).sqrMagnitude > 15f * 15f) continue;
                e.flags |= EF.GateLocked; s.edges[i] = e; a.gate = b.gate = gi; s.nodes[e.a] = a; s.nodes[e.b] = b;
                if (e.kind == EK.Road && outside == None)
                { outside = da < db ? e.a : e.b; inside = da < db ? e.b : e.a; }
            }
            if (outside == None) throw new InvalidOperationException("Coastal graph: no lane crosses gate " + gate.id);
            s.gates.Add(new StreetGraph.Gate { districtId = gate.districtId, nodeIn = outside, nodeOut = inside });
        }
    }

    static StreetGraph.Loop[] BuildLoops(StreetGraph graph)
    {
        var loops = new List<StreetGraph.Loop>();
        void Loop(string district, params Vector3[] points)
        {
            var sequence = new List<ushort>(); int first = -1, previous = -1;
            for (int i = 0; i <= points.Length; i++)
            {
                int next = i == points.Length ? first : graph.Nearest(points[i], StreetGraph.KindMask(NK.Walk), District(district));
                if (next < 0) return;
                if (previous < 0) { first = next; previous = next; sequence.Add((ushort)next); continue; }
                var path = WalkPath(graph, previous, next);
                if (path == null) return;
                for (int j = 1; j < path.Count; j++) sequence.Add((ushort)path[j]); previous = next;
            }
            if (sequence.Count > 3 && sequence[sequence.Count - 1] == sequence[0]) sequence.RemoveAt(sequence.Count - 1);
            if (sequence.Count >= 3) loops.Add(new StreetGraph.Loop { district = District(district), nodes = sequence.ToArray() });
        }
        foreach (var district in Districts.All)
        {
            CoastalCityLayout.RoadDef closed = null;
            foreach (var road in CoastalCityLayout.Roads)
                if (road.districtId == district.id && road.points.Length > 3 &&
                    (road.points[0] - road.points[road.points.Length - 1]).sqrMagnitude < .1f &&
                    (closed == null || road.Length > closed.Length)) closed = road;
            if (closed != null)
            {
                var p = closed.points;
                Loop(district.id, p[0], p[p.Length / 4], p[p.Length / 2], p[p.Length * 3 / 4]);
            }
            else
            {
                var r = CoastalCityLayout.DistrictBounds(district.id);
                Loop(district.id, new Vector3(r.xMin, 0, r.yMin), new Vector3(r.xMax, 0, r.yMin),
                    new Vector3(r.xMax, 0, r.yMax), new Vector3(r.xMin, 0, r.yMax));
            }
        }
        return loops.ToArray();
    }
    static List<int> WalkPath(StreetGraph g, int from, int to)
    {
        var previous = new int[g.NodeCount]; for (int i = 0; i < previous.Length; i++) previous[i] = -1;
        var queue = new Queue<int>(); queue.Enqueue(from); previous[from] = from;
        while (queue.Count > 0 && previous[to] < 0)
        {
            int u = queue.Dequeue();
            for (int i = g.OutBegin(u); i < g.OutEnd(u); i++)
            {
                int v = g.OutTo(i); if (previous[v] >= 0 || !g.edges[g.OutEdge(i)].IsWalkKind) continue;
                previous[v] = u; queue.Enqueue(v);
            }
        }
        if (previous[to] < 0) return null;
        var result = new List<int>(); for (int u = to; ; u = previous[u]) { result.Add(u); if (u == from) break; }
        result.Reverse(); return result;
    }
}

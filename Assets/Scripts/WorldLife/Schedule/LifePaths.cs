using System;
using System.Collections.Generic;
using UnityEngine;

// =====================================================================================================================
// WorldLife package D (Docs/WorldLife.md §5.1, §7.3): routing on the street graph. Deterministic everywhere: A* runs on
// INTEGER costs (edge length in cm, rounded once) and an integer heuristic from integer node coordinates, with the open
// set ordered by (f, node index), so every machine gets the same route without floating-point ties. Car trips get a
// polyline with a closed-form motion profile (piecewise constant acceleration), so "where is car c at time t" is a binary
// search plus a quadratic, the same on every machine.
// =====================================================================================================================

/// <summary>Which edges a route may use.</summary>
public enum LifeRouteMode : byte
{
    /// <summary>Road edges only (driveways are entered and left explicitly by the trip builder).</summary>
    Car = 0,
    /// <summary>Walk, Crosswalk, GardenPath, ParkPath (no jaywalks).</summary>
    Walk = 1,
    /// <summary>As Walk, plus Jaywalk edges ("headphones guy", ~1 stroll in 6).</summary>
    WalkJaywalk = 2,
}

/// <summary>
/// Deterministic A* over a <see cref="StreetGraph"/> (one instance per graph; main thread). No allocations per query
/// after construction except the result list the caller passes.
/// </summary>
public sealed class LifeAStar
{
    readonly StreetGraph g;
    readonly int n;
    readonly int[] cost;      // per edge, cm
    readonly int[] nx, nz;    // per node, cm
    readonly int[] gScore;
    readonly int[] stamp;
    readonly int[] closedStamp;
    readonly int[] cameFrom;  // node
    readonly int[] cameEdge;  // edge
    long[] heap = new long[256];
    int heapCount, gen;

    public int Expanded { get; private set; }

    public LifeAStar(StreetGraph graph)
    {
        g = graph;
        n = graph.NodeCount;
        cost = new int[graph.EdgeCount];
        for (int e = 0; e < cost.Length; e++) cost[e] = Mathf.Max(1, (int)Math.Round(graph.edges[e].len * 100.0));
        nx = new int[n]; nz = new int[n];
        for (int i = 0; i < n; i++) { nx[i] = (int)Math.Round(graph.nodes[i].pos.x * 100.0); nz[i] = (int)Math.Round(graph.nodes[i].pos.z * 100.0); }
        gScore = new int[n]; stamp = new int[n]; closedStamp = new int[n]; cameFrom = new int[n]; cameEdge = new int[n];
    }

    /// <summary>Is the edge usable in this mode with this unlocked mask (and inside <paramref name="district"/> when ≥ 0)?</summary>
    public bool Allowed(int e, LifeRouteMode mode, int mask)
    {
        var ed = g.edges[e];
        switch (mode)
        {
            case LifeRouteMode.Car: if (ed.kind != StreetGraph.EdgeKind.Road) return false; break;
            case LifeRouteMode.Walk: if (!ed.IsWalkKind || ed.kind == StreetGraph.EdgeKind.Jaywalk) return false; break;
            default: if (!ed.IsWalkKind) return false; break;
        }
        return g.EdgeOpen(e, mask);
    }

    int H(int a, int b)
    {
        long dx = nx[a] - nx[b], dz = nz[a] - nz[b];
        return (int)(Math.Sqrt((double)(dx * dx + dz * dz)) * 0.995);
    }

    /// <summary>
    /// Shortest route from <paramref name="from"/> to <paramref name="to"/>. Appends the node sequence (from … to) to
    /// <paramref name="nodes"/> and the edge between each pair to <paramref name="edgesOut"/> (may be null). Returns the
    /// cost in cm, or −1 when unreachable. <paramref name="district"/> ≥ 0 keeps a walk inside that district.
    /// </summary>
    public int Route(int from, int to, LifeRouteMode mode, int mask, int district, List<int> nodes, List<int> edgesOut)
    {
        Expanded = 0;
        if (from < 0 || to < 0 || from >= n || to >= n) return -1;
        if (from == to) { nodes?.Add(from); return 0; }
        gen++;
        if (gen == int.MaxValue) { Array.Clear(stamp, 0, n); Array.Clear(closedStamp, 0, n); gen = 1; }
        heapCount = 0;
        stamp[from] = gen; gScore[from] = 0; cameFrom[from] = -1; cameEdge[from] = -1;
        Push(((long)H(from, to) << 16) | (uint)from);
        while (heapCount > 0)
        {
            long top = Pop();
            int u = (int)(top & 0xFFFF);
            if (closedStamp[u] == gen) continue;
            closedStamp[u] = gen;
            Expanded++;
            if (u == to) break;
            int gu = gScore[u];
            for (int i = g.OutBegin(u); i < g.OutEnd(u); i++)
            {
                int e = g.OutEdge(i), v = g.OutTo(i);
                if (closedStamp[v] == gen || !Allowed(e, mode, mask)) continue;
                if (district >= 0 && g.nodes[v].district != district && v != to) continue;
                int ng = gu + cost[e];
                if (stamp[v] == gen && ng >= gScore[v]) continue;
                stamp[v] = gen; gScore[v] = ng; cameFrom[v] = u; cameEdge[v] = e;
                Push(((long)(ng + H(v, to)) << 16) | (uint)v);
            }
        }
        if (closedStamp[to] != gen) return -1;
        if (nodes != null || edgesOut != null)
        {
            int start = nodes?.Count ?? 0, estart = edgesOut?.Count ?? 0;
            for (int v = to; v != -1; v = cameFrom[v])
            {
                nodes?.Add(v);
                if (cameEdge[v] >= 0) edgesOut?.Add(cameEdge[v]);
            }
            if (nodes != null) nodes.Reverse(start, nodes.Count - start);
            if (edgesOut != null) edgesOut.Reverse(estart, edgesOut.Count - estart);
        }
        return gScore[to];
    }

    /// <summary>The integer cost (cm) of an edge.</summary>
    public int EdgeCost(int e) => cost[e];

    /// <summary>
    /// Bounded single-source Dijkstra (the same integer costs and (cost, node) ordering as <see cref="Route"/>): every
    /// node reachable from <paramref name="from"/> within <paramref name="maxCost"/> cm, appended in settled order (nearest
    /// first, ties by node index) with its cost and its parent node (−1 for the root).
    /// </summary>
    public void Reach(int from, LifeRouteMode mode, int mask, int district, int maxCost, List<int> nodesOut, List<int> costOut, List<int> parentOut)
    {
        if (from < 0 || from >= n) return;
        gen++;
        if (gen == int.MaxValue) { Array.Clear(stamp, 0, n); Array.Clear(closedStamp, 0, n); gen = 1; }
        heapCount = 0;
        stamp[from] = gen; gScore[from] = 0; cameFrom[from] = -1;
        Push((long)from);
        while (heapCount > 0)
        {
            long top = Pop();
            int u = (int)(top & 0xFFFF);
            if (closedStamp[u] == gen) continue;
            closedStamp[u] = gen;
            nodesOut.Add(u); costOut.Add(gScore[u]); parentOut.Add(cameFrom[u]);
            for (int i = g.OutBegin(u); i < g.OutEnd(u); i++)
            {
                int e = g.OutEdge(i), v = g.OutTo(i);
                if (closedStamp[v] == gen || !Allowed(e, mode, mask)) continue;
                if (district >= 0 && g.nodes[v].district != district) continue;
                int ng = gScore[u] + cost[e];
                if (ng > maxCost || (stamp[v] == gen && ng >= gScore[v])) continue;
                stamp[v] = gen; gScore[v] = ng; cameFrom[v] = u;
                Push(((long)ng << 16) | (uint)v);
            }
        }
    }

    void Push(long k)
    {
        if (heapCount == heap.Length) Array.Resize(ref heap, heap.Length * 2);
        int i = heapCount++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (heap[p] <= k) break;
            heap[i] = heap[p];
            i = p;
        }
        heap[i] = k;
    }

    long Pop()
    {
        long top = heap[0];
        long last = heap[--heapCount];
        int i = 0;
        while (true)
        {
            int l = 2 * i + 1;
            if (l >= heapCount) break;
            int c = l + 1 < heapCount && heap[l + 1] < heap[l] ? l + 1 : l;
            if (heap[c] >= last) break;
            heap[i] = heap[c];
            i = c;
        }
        if (heapCount > 0) heap[i] = last;
        return top;
    }
}

/// <summary>Per-vertex flags of a <see cref="CarPath"/> (the segment that starts at the vertex).</summary>
[Flags]
public enum CarSegFlags : ushort
{
    None = 0,
    Yield = 1,
    Turn = 2,
    Pad = 4,
    /// <summary>The car moves backwards (driveway reverse-out): heading = −tangent.</summary>
    Reverse = 8,
    Driveway = 16,
    /// <summary>Inside a town-exit tunnel (the car fades out / appears here).</summary>
    Tunnel = 32,
    /// <summary>On a graph Road edge (lane-deviation checks apply).</summary>
    Lane = 64,
    /// <summary>The first vertex of a run of Yield segments: slow to the intersection speed and check the box.</summary>
    BoxEntry = 128,
}

/// <summary>
/// A car's route for one direction of a trip (§7.3): reverse-out spline + lanes to the exit tunnel, or tunnel + lanes +
/// nose-in to the slot. Points with cumulative arc length, a speed limit per segment, and the canonical motion profile
/// (speed at each vertex, time at each vertex). Immutable once built; cached per (car, exit, direction, gate mask).
/// </summary>
public sealed class CarPath
{
    public Vector3[] pts;
    public float[] s;          // cumulative arc length at each vertex
    public float[] v;          // canonical speed at each vertex (m/s)
    public float[] tau;        // canonical time at each vertex (s from the trip start)
    public CarSegFlags[] flags;
    public int[] edge;         // graph edge of the segment starting at the vertex (−1 = synthetic)
    /// <summary>Yield chains (intersection boxes) as arc ranges [start, end].</summary>
    public Vector2[] boxes;
    public float Length;
    public float Duration;
    /// <summary>Vertex index where a reverse-out ends and the car drives forward on the lane (−1 = none).</summary>
    public int cusp = -1;
    public int Count => pts.Length;
    public Bounds bounds;
    /// <summary>The graph edges it uses (gate checks, tests).</summary>
    public int[] graphEdges;

    // ------------------------------------------------------------------ sampling

    /// <summary>Segment index containing arc s (binary search).</summary>
    public int SegmentAt(float arc)
    {
        int lo = 0, hi = pts.Length - 2;
        if (hi <= 0) return 0;
        if (arc <= s[0]) return 0;
        if (arc >= s[hi]) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (s[mid] <= arc) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Position at arc length s.</summary>
    public Vector3 PointAt(float arc, out int seg)
    {
        seg = SegmentAt(arc);
        if (pts.Length == 1) return pts[0];
        float len = s[seg + 1] - s[seg];
        float u = len > 1e-5f ? Mathf.Clamp01((arc - s[seg]) / len) : 0f;
        return Vector3.LerpUnclamped(pts[seg], pts[seg + 1], u);
    }

    public Vector3 PointAt(float arc) => PointAt(arc, out _);

    /// <summary>Unit tangent (direction of increasing s) of a segment.</summary>
    public Vector3 Tangent(int seg)
    {
        seg = Mathf.Clamp(seg, 0, pts.Length - 2);
        Vector3 d = pts[seg + 1] - pts[seg];
        d.y = 0f;
        return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
    }

    /// <summary>The canonical arc length at trip time t (seconds from the trip start), and the speed there.</summary>
    public float ArcAtTime(float t, out float speed, out int seg)
    {
        int last = pts.Length - 1;
        if (t <= 0f) { seg = 0; speed = v[0]; return 0f; }
        if (t >= tau[last]) { seg = Mathf.Max(0, last - 1); speed = v[last]; return s[last]; }
        int lo = 0, hi = last - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (tau[mid] <= t) lo = mid; else hi = mid - 1;
        }
        seg = lo;
        float dt = t - tau[lo];
        float len = s[lo + 1] - s[lo];
        float v0 = v[lo], v1 = v[lo + 1];
        float a = len > 1e-5f ? (v1 * v1 - v0 * v0) / (2f * len) : 0f;
        float ds = v0 * dt + 0.5f * a * dt * dt;
        speed = v0 + a * dt;
        return s[lo] + Mathf.Clamp(ds, 0f, len);
    }

    /// <summary>The speed limit / profile speed at arc s (linear in v² inside a segment).</summary>
    public float SpeedAtArc(float arc)
    {
        int seg = SegmentAt(arc);
        float len = s[seg + 1] - s[seg];
        float u = len > 1e-5f ? Mathf.Clamp01((arc - s[seg]) / len) : 0f;
        float v0 = v[seg], v1 = v[seg + 1];
        return Mathf.Sqrt(Mathf.Max(0f, v0 * v0 + (v1 * v1 - v0 * v0) * u));
    }

    // ------------------------------------------------------------------ build

    public const float Accel = 2.0f, Decel = 3.0f;

    /// <summary>Finish a path from points, per-segment flags, edges and speed limits (limits per vertex = the segment
    /// that starts there; <paramref name="vertexCap"/> caps the speed AT a vertex, e.g. 0 at a reverse cusp).</summary>
    public static CarPath Build(List<Vector3> p, List<CarSegFlags> f, List<int> e, List<float> segLimit, List<float> vertexCap, float startSpeed, float endSpeed)
    {
        int n = p.Count;
        var cp = new CarPath
        {
            pts = p.ToArray(), flags = f.ToArray(), edge = e.ToArray(),
            s = new float[n], v = new float[n], tau = new float[n],
        };
        for (int i = 1; i < n; i++) cp.s[i] = cp.s[i - 1] + Vector3.Distance(cp.pts[i - 1], cp.pts[i]);
        cp.Length = cp.s[n - 1];
        // Vertex limits: min of the segments touching it and the vertex cap.
        var lim = new float[n];
        for (int i = 0; i < n; i++)
        {
            float a = i > 0 ? segLimit[i - 1] : segLimit[0];
            float b = i < n - 1 ? segLimit[i] : segLimit[Mathf.Max(0, n - 2)];
            lim[i] = Mathf.Min(Mathf.Min(a, b), vertexCap[i]);
        }
        lim[0] = Mathf.Min(lim[0], startSpeed);
        lim[n - 1] = Mathf.Min(lim[n - 1], endSpeed);
        // Forward (accelerate) then backward (brake) passes.
        cp.v[0] = lim[0];
        for (int i = 1; i < n; i++)
        {
            float len = cp.s[i] - cp.s[i - 1];
            cp.v[i] = Mathf.Min(lim[i], Mathf.Sqrt(cp.v[i - 1] * cp.v[i - 1] + 2f * Accel * len));
        }
        for (int i = n - 2; i >= 0; i--)
        {
            float len = cp.s[i + 1] - cp.s[i];
            cp.v[i] = Mathf.Min(cp.v[i], Mathf.Sqrt(cp.v[i + 1] * cp.v[i + 1] + 2f * Decel * len));
        }
        // A segment needs some speed to be crossed: creep through zero-zero pairs (a cusp next to a stop).
        for (int i = 0; i < n - 1; i++)
        {
            float len = cp.s[i + 1] - cp.s[i];
            if (len > 1e-4f && cp.v[i] + cp.v[i + 1] < 0.2f)
            {
                if (cp.v[i] < cp.v[i + 1]) cp.v[i] = 0.2f; else cp.v[i + 1] = Mathf.Max(cp.v[i + 1], 0.2f);
            }
        }
        for (int i = 1; i < n; i++)
        {
            float len = cp.s[i] - cp.s[i - 1];
            float vs = cp.v[i - 1] + cp.v[i];
            cp.tau[i] = cp.tau[i - 1] + (len > 1e-5f ? 2f * len / Mathf.Max(0.05f, vs) : 0f);
        }
        cp.Duration = cp.tau[n - 1];
        var b2 = new Bounds(cp.pts[0], Vector3.zero);
        for (int i = 1; i < n; i++) b2.Encapsulate(cp.pts[i]);
        cp.bounds = b2;
        // Yield chains.
        var boxes = new List<Vector2>();
        for (int i = 0; i < n - 1; i++)
        {
            if ((cp.flags[i] & CarSegFlags.Yield) == 0) continue;
            int j = i;
            while (j < n - 1 && (cp.flags[j] & CarSegFlags.Yield) != 0) j++;
            boxes.Add(new Vector2(cp.s[i], cp.s[j]));
            cp.flags[i] |= CarSegFlags.BoxEntry;
            i = j;
        }
        cp.boxes = boxes.ToArray();
        // The reverse cusp (driveway reverse-out): the first vertex after a Reverse segment.
        cp.cusp = -1;
        for (int i = 0; i < n - 1; i++)
            if ((cp.flags[i] & CarSegFlags.Reverse) != 0 && (i + 1 >= n - 1 || (cp.flags[i + 1] & CarSegFlags.Reverse) == 0)) { cp.cusp = i + 1; break; }
        var ge = new List<int>();
        for (int i = 0; i < cp.edge.Length; i++) if (cp.edge[i] >= 0 && (ge.Count == 0 || ge[ge.Count - 1] != cp.edge[i])) ge.Add(cp.edge[i]);
        cp.graphEdges = ge.ToArray();
        return cp;
    }
}

/// <summary>
/// A day's walking polylines in one shared buffer (pedestrian excursions): appended while a day is built, truncated at
/// the next day. No per-frame allocations; Lists keep their capacity across days.
/// </summary>
public sealed class WalkPathBuffer
{
    public struct PathRec
    {
        public int start, count;
        public float length;
        public Vector2 min, max;   // XZ bounds
    }

    public readonly List<Vector3> pts = new List<Vector3>(8192);
    public readonly List<float> cum = new List<float>(8192);
    public readonly List<PathRec> paths = new List<PathRec>(512);

    public int PathCount => paths.Count;

    public void Clear() { pts.Clear(); cum.Clear(); paths.Clear(); }

    int open = -1;

    /// <summary>Start a path; add points with <see cref="Add"/>; finish with <see cref="End"/> (returns its id).</summary>
    public void Begin() { open = pts.Count; }

    public void Add(Vector3 p)
    {
        if (open < 0) Begin();
        if (pts.Count > open)
        {
            Vector3 last = pts[pts.Count - 1];
            float d = Vector3.Distance(last, p);
            if (d < 0.02f) return;   // drop duplicates (joins between route pieces)
            cum.Add(cum[cum.Count - 1] + d);
        }
        else cum.Add(0f);
        pts.Add(p);
    }

    public int End()
    {
        if (open < 0) Begin();
        var r = new PathRec { start = open, count = pts.Count - open };
        if (r.count == 0) { pts.Add(Vector3.zero); cum.Add(0f); r.count = 1; }
        r.length = cum[r.start + r.count - 1];
        Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
        for (int i = r.start; i < r.start + r.count; i++)
        {
            var q = pts[i];
            if (q.x < mn.x) mn.x = q.x; if (q.z < mn.y) mn.y = q.z;
            if (q.x > mx.x) mx.x = q.x; if (q.z > mx.y) mx.y = q.z;
        }
        r.min = mn; r.max = mx;
        paths.Add(r);
        open = -1;
        return paths.Count - 1;
    }

    public float Length(int path) => paths[path].length;

    /// <summary>Position and unit direction (of increasing s) at arc s on a path.</summary>
    public Vector3 Sample(int path, float s, out Vector3 dir)
    {
        var r = paths[path];
        int lo = r.start, hi = r.start + r.count - 1;
        if (r.count == 1) { dir = Vector3.forward; return pts[lo]; }
        if (s <= 0f) { dir = Dir(lo); return pts[lo]; }
        if (s >= r.length) { dir = Dir(hi - 1); return pts[hi]; }
        int a = lo, b = hi - 1;
        while (a < b)
        {
            int mid = (a + b + 1) >> 1;
            if (cum[mid] <= s) a = mid; else b = mid - 1;
        }
        float len = cum[a + 1] - cum[a];
        float u = len > 1e-5f ? (s - cum[a]) / len : 0f;
        dir = Dir(a);
        return Vector3.LerpUnclamped(pts[a], pts[a + 1], u);
    }

    Vector3 Dir(int i)
    {
        Vector3 d = pts[i + 1] - pts[i];
        d.y = 0f;
        return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
    }

    /// <summary>Distance (XZ) from a point to the path's bounds (0 inside).</summary>
    public float BoundsDistance(int path, Vector3 p)
    {
        var r = paths[path];
        float dx = Mathf.Max(0f, Mathf.Max(r.min.x - p.x, p.x - r.max.x));
        float dz = Mathf.Max(0f, Mathf.Max(r.min.y - p.z, p.z - r.max.y));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}

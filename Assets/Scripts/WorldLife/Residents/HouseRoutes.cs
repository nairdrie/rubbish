using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C: a house's front-door → curb walk on the street graph (Docs/WorldLife.md §5.2, §7.2). Bounded
/// Dijkstra over walk-type edges inside the house's own district (ties broken by edge order, so every machine draws the
/// same walk), cached per house: door → path end → walk join → … → the sidewalk node before the curb Slot. The walk-out
/// appends its own stand point behind the bag's slot. Small (a handful of nodes), so C carries its own instead of
/// waiting for package D's A*.
/// </summary>
public static class HouseRoutes
{
    /// <summary>Walk-outs longer than this fall back to today's drop-in (estates use the butler anyway).</summary>
    public const float MaxRouteMetres = 45f;

    static StreetGraph forGraph;
    static readonly Dictionary<int, int[]> cache = new Dictionary<int, int[]>(96);
    static float[] dist = Array.Empty<float>();
    static int[] prev = Array.Empty<int>();
    static int[] stamp = Array.Empty<int>();
    static int stampNow;
    static readonly List<int> heap = new List<int>(64);

    /// <summary>Graph nodes from the door up to (not including) the Slot node; null when the house can't walk to its curb.</summary>
    public static int[] DoorToCurb(StreetGraph g, int house)
    {
        if (g == null || house < 0 || g.houses == null || house >= g.houses.Length) return null;
        if (!ReferenceEquals(forGraph, g)) { forGraph = g; cache.Clear(); }
        if (cache.TryGetValue(house, out var path)) return path;
        path = Solve(g, g.houses[house].door, g.houses[house].slot, g.houses[house].district)
            ?? Solve(g, g.houses[house].door, g.houses[house].slot, -1);
        if (path != null && path.Length > 0 && g.nodes[path[path.Length - 1]].kind == StreetGraph.NodeKind.Slot)
        {
            var trimmed = new int[path.Length - 1];
            Array.Copy(path, trimmed, trimmed.Length);
            path = trimmed;
        }
        cache[house] = path;
        return path;
    }

    static int[] Solve(StreetGraph g, int from, int to, int district)
    {
        int n = g.NodeCount;
        if (from < 0 || to < 0 || from >= n || to >= n) return null;
        if (dist.Length < n) { dist = new float[n]; prev = new int[n]; stamp = new int[n]; }
        stampNow++;
        heap.Clear();
        Touch(from); dist[from] = 0f; prev[from] = -1;
        heap.Add(from);
        int expansions = 0;
        while (heap.Count > 0 && expansions++ < 600)
        {
            int u = PopMin();
            if (u == to) break;
            for (int i = g.OutBegin(u); i < g.OutEnd(u); i++)
            {
                int e = g.OutEdge(i), v = g.OutTo(i);
                var ed = g.edges[e];
                if (!ed.IsWalkKind || ed.kind == StreetGraph.EdgeKind.Jaywalk) continue;
                if (district >= 0 && g.nodes[v].district != district && v != to) continue;
                float nd = dist[u] + ed.len;
                if (stamp[v] != stampNow) { Touch(v); dist[v] = nd; prev[v] = u; heap.Add(v); }
                else if (nd < dist[v] - 1e-4f) { dist[v] = nd; prev[v] = u; if (!heap.Contains(v)) heap.Add(v); }
            }
        }
        if (stamp[to] != stampNow) return null;
        var list = new List<int>(12);
        for (int v = to; v >= 0; v = prev[v]) { list.Add(v); if (list.Count > 256) return null; }
        list.Reverse();
        return list.ToArray();
    }

    static void Touch(int v) { stamp[v] = stampNow; dist[v] = float.MaxValue; prev[v] = -1; }

    static int PopMin()
    {
        int best = 0;
        for (int i = 1; i < heap.Count; i++)
            if (dist[heap[i]] < dist[heap[best]] || (dist[heap[i]] == dist[heap[best]] && heap[i] < heap[best])) best = i;
        int v = heap[best];
        heap[best] = heap[heap.Count - 1];
        heap.RemoveAt(heap.Count - 1);
        return v;
    }

    /// <summary>Length of a node path (m), using node positions.</summary>
    public static float Length(StreetGraph g, int[] nodes)
    {
        if (nodes == null) return 0f;
        float l = 0f;
        for (int i = 1; i < nodes.Length; i++) l += Vector3.Distance(g.nodes[nodes[i - 1]].pos, g.nodes[nodes[i]].pos);
        return l;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { forGraph = null; cache.Clear(); heap.Clear(); stampNow = 0; }
}

/// <summary>A polyline walked at constant speed (closed-form position by arc length). Reused; no allocation once grown.</summary>
public sealed class WalkPath
{
    readonly List<Vector3> pts = new List<Vector3>(16);
    readonly List<float> cum = new List<float>(16);
    public float Length { get; private set; }
    public int Count => pts.Count;
    public Vector3 this[int i] => pts[i];
    public Vector3 Start => pts.Count > 0 ? pts[0] : Vector3.zero;
    public Vector3 End => pts.Count > 0 ? pts[pts.Count - 1] : Vector3.zero;

    public void Clear() { pts.Clear(); cum.Clear(); Length = 0f; }

    public void Add(Vector3 p)
    {
        if (pts.Count > 0)
        {
            float d = Vector3.Distance(pts[pts.Count - 1], p);
            if (d < 0.02f) return;
            Length += d;
        }
        pts.Add(p);
        cum.Add(Length);
    }

    /// <summary>Point and travel direction at arc length s (clamped).</summary>
    public Vector3 At(float s, out Vector3 dir)
    {
        dir = Vector3.forward;
        int n = pts.Count;
        if (n == 0) return Vector3.zero;
        if (n == 1) return pts[0];
        if (s <= 0f) { dir = pts[1] - pts[0]; return pts[0]; }
        if (s >= Length) { dir = pts[n - 1] - pts[n - 2]; return pts[n - 1]; }
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cum[mid] <= s) lo = mid; else hi = mid; }
        float seg = cum[hi] - cum[lo];
        float k = seg > 1e-5f ? (s - cum[lo]) / seg : 0f;
        dir = pts[hi] - pts[lo];
        return Vector3.LerpUnclamped(pts[lo], pts[hi], k);
    }

    /// <summary>Arc length of the closest point on the path to p (XZ).</summary>
    public float Project(Vector3 p)
    {
        float best = float.MaxValue, bestS = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector3 a = pts[i - 1], b = pts[i];
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = p - a; ap.y = 0f;
            float l2 = ab.sqrMagnitude;
            float k = l2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / l2) : 0f;
            Vector3 q = a + (b - a) * k; q.y = p.y;
            float d = (q - p).sqrMagnitude;
            if (d < best) { best = d; bestS = cum[i - 1] + (cum[i] - cum[i - 1]) * k; }
        }
        return bestS;
    }

    /// <summary>Copy <paramref name="src"/> reversed, from arc length <paramref name="fromS"/> back to its start, starting
    /// at <paramref name="start"/> (a walk home from wherever we are).</summary>
    public void BuildReturn(WalkPath src, float fromS, Vector3 start)
    {
        Clear();
        Add(start);
        for (int i = src.pts.Count - 1; i >= 0; i--)
            if (src.cum[i] < fromS - 0.05f) Add(src.pts[i]);
    }
}

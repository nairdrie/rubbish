using System.Collections.Generic;
using UnityEngine;

public sealed partial class NeighborhoodConnection
{
    static readonly Dictionary<StreetGraph, StreetGraph> graphCopies = new Dictionary<StreetGraph, StreetGraph>();

    /// <summary>Append-only runtime copy: existing house indices, nodes, cars and saved identities never move.</summary>
    public static StreetGraph WithConnection(StreetGraph source)
    {
        if (source == null || source.nodes == null || source.nodes.Length == 0) return source;
        if (graphCopies.TryGetValue(source, out var cached) && cached != null) return cached;
        foreach (var gate in source.gates)
            if (gate.districtId == Districts.MinivanMeadows && gate.nodeIn < source.NodeCount &&
                (source.nodes[gate.nodeIn].pos - GatePosition).sqrMagnitude < 100f) return source;
        int Find(Vector3 point, bool walk)
        {
            int result = -1; float best = 8f * 8f;
            for (int i = 0; i < source.NodeCount; i++)
            {
                var n = source.nodes[i];
                if (walk ? n.kind != StreetGraph.NodeKind.Walk && n.kind != StreetGraph.NodeKind.Cross : n.kind != StreetGraph.NodeKind.Lane) continue;
                float distance = (n.pos - point).sqrMagnitude;
                if (distance < best) { best = distance; result = i; }
            }
            return result;
        }
        int westIn = Find(new Vector3(150f, 0, 167.5f), false), westOut = Find(new Vector3(150f, 0, 172.5f), false);
        int eastOut = Find(new Vector3(237.5f, 0, 140f), false), eastIn = Find(new Vector3(242.5f, 0, 140f), false);
        int walkWest = Find(new Vector3(167.4f, 0, 177.4f), true), walkEast = Find(new Vector3(232.6f, 0, 140f), true);
        if (westIn < 0 || westOut < 0 || eastIn < 0 || eastOut < 0 || walkWest < 0 || walkEast < 0) return source;

        var graph = Instantiate(source); graph.name = source.name + " + Birch Link"; graph.hideFlags = HideFlags.DontSave;
        var nodes = new List<StreetGraph.Node>(source.nodes); var edges = new List<StreetGraph.Edge>(source.edges);
        var gates = new List<StreetGraph.Gate>(source.gates);
        ushort gateIndex = (ushort)gates.Count, gateIn = StreetGraph.None, gateOut = StreetGraph.None;
        void Path(int from, int to, bool walk, params Vector2[] points)
        {
            int previous = from;
            for (int i = 0; i <= points.Length; i++)
            {
                int next = to;
                if (i < points.Length)
                {
                    next = nodes.Count;
                    var p = points[i];
                    nodes.Add(new StreetGraph.Node { pos = new Vector3(p.x, walk ? .15f : .03f, p.y),
                        kind = walk ? StreetGraph.NodeKind.Walk : StreetGraph.NodeKind.Lane,
                        district = (byte)(p.x >= GatePosition.x ? 1 : 0), gate = StreetGraph.None });
                }
                Vector3 a = nodes[previous].pos, b = nodes[next].pos;
                bool crossing = Mathf.Min(a.x, b.x) < GatePosition.x && Mathf.Max(a.x, b.x) > GatePosition.x;
                var flags = StreetGraph.EdgeFlags.None;
                if (crossing)
                {
                    flags |= StreetGraph.EdgeFlags.GateLocked;
                    var na = nodes[previous]; na.gate = gateIndex; nodes[previous] = na;
                    var nb = nodes[next]; nb.gate = gateIndex; nodes[next] = nb;
                    if (!walk && a.x < b.x) { gateIn = (ushort)previous; gateOut = (ushort)next; }
                }
                if (!walk && (a.x < 168f || b.x < 168f || a.x > 227f || b.x > 227f))
                    flags |= StreetGraph.EdgeFlags.Turn | StreetGraph.EdgeFlags.Yield;
                edges.Add(new StreetGraph.Edge { a = (ushort)previous, b = (ushort)next,
                    kind = walk ? StreetGraph.EdgeKind.Walk : StreetGraph.EdgeKind.Road,
                    len = Vector3.Distance(a, b), flags = flags });
                previous = next;
            }
        }
        Path(westIn, eastOut, false, new Vector2(162.5f, 177), new Vector2(162.5f, 187), new Vector2(164, 193),
            new Vector2(168, 195.5f), new Vector2(187, 195.5f), new Vector2(203, 195.5f), new Vector2(209, 195.5f),
            new Vector2(226, 195.5f), new Vector2(232, 195.5f), new Vector2(236, 193.5f),
            new Vector2(237.5f, 189), new Vector2(237.5f, 174), new Vector2(237.5f, 159));
        Path(eastIn, westOut, false, new Vector2(242.5f, 155), new Vector2(242.5f, 175), new Vector2(242.5f, 195),
            new Vector2(242, 198), new Vector2(238, 200.5f), new Vector2(226, 200.5f), new Vector2(209, 200.5f),
            new Vector2(203, 200.5f), new Vector2(187, 200.5f), new Vector2(168, 200.5f), new Vector2(160, 200),
            new Vector2(157.5f, 196), new Vector2(157.5f, 180), new Vector2(155, 174));
        // Use the south turnstile sidewalk. The north sidewalk is blocked by the original gate design.
        Path(walkWest, walkEast, true, new Vector2(167.4f, 187), new Vector2(169, 190), new Vector2(175, 190.6f),
            new Vector2(188, 190.6f), new Vector2(204, 191.145f), new Vector2(208, 191.145f),
            new Vector2(226, 190.6f), new Vector2(230, 189.5f), new Vector2(232.6f, 186), new Vector2(232.6f, 168), new Vector2(232.6f, 151));
        gates.Add(new StreetGraph.Gate { districtId = Districts.MinivanMeadows, nodeIn = gateIn, nodeOut = gateOut });
        graph.nodes = nodes.ToArray(); graph.edges = edges.ToArray(); graph.gates = gates.ToArray(); graph.Invalidate();
        graphCopies[source] = graph;
        return graph;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetConnections()
    {
        foreach (var pair in graphCopies) if (pair.Value != null) Destroy(pair.Value);
        graphCopies.Clear();
    }
}

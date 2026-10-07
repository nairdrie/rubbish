using UnityEngine;

/// <summary>
/// WorldLife package A (Docs/WorldLife.md §5.3): draws the anchor's street graph in the Scene view (select the WorldLife
/// anchor under NeighborhoodGen, or tick <see cref="drawAlways"/>). Editor-only drawing; nothing runs in a build.
/// Colours: lanes amber (turns orange), gates red, depot pad yellow, sidewalks cyan, crosswalks white, jaywalks magenta,
/// garden paths green, driveways tan, park paths lime; doors red, slots black, exits red.
/// </summary>
[DisallowMultipleComponent]
public sealed class StreetGraphGizmos : MonoBehaviour
{
    public bool drawAlways;
    public bool lanes = true, walks = true, houses = true;

#if UNITY_EDITOR
    void OnDrawGizmos() { if (drawAlways) Draw(); }
    void OnDrawGizmosSelected() { if (!drawAlways) Draw(); }

    void Draw()
    {
        var anchor = GetComponent<WorldLifeAnchor>();
        StreetGraph g = anchor != null && anchor.graph != null ? anchor.graph : StreetGraph.Load();
        if (g == null || g.nodes == null || g.edges == null) return;
        var up = Vector3.up * 0.2f;
        for (int e = 0; e < g.EdgeCount; e++)
        {
            var ed = g.edges[e];
            if (ed.a >= g.NodeCount || ed.b >= g.NodeCount) continue;
            bool road = ed.kind == StreetGraph.EdgeKind.Road || ed.kind == StreetGraph.EdgeKind.Driveway;
            if ((road && !lanes) || (!road && !walks)) continue;
            Gizmos.color = ColorOf(ed);
            Vector3 a = g.nodes[ed.a].pos + up, b = g.nodes[ed.b].pos + up;
            Gizmos.DrawLine(a, b);
            if (ed.kind == StreetGraph.EdgeKind.Road && (b - a).sqrMagnitude > 16f)
            {
                Vector3 m = Vector3.Lerp(a, b, 0.6f), d = (b - a).normalized, r = new Vector3(d.z, 0f, -d.x);
                Gizmos.DrawLine(m, m - d * 1.2f + r * 0.6f);
                Gizmos.DrawLine(m, m - d * 1.2f - r * 0.6f);
            }
        }
        if (!houses) return;
        for (int i = 0; i < g.NodeCount; i++)
        {
            var k = g.nodes[i].kind;
            if (k == StreetGraph.NodeKind.Door) { Gizmos.color = Color.red; Gizmos.DrawSphere(g.nodes[i].pos + up, 0.35f); }
            else if (k == StreetGraph.NodeKind.Slot) { Gizmos.color = Color.black; Gizmos.DrawSphere(g.nodes[i].pos + up, 0.25f); }
            else if (k == StreetGraph.NodeKind.Exit) { Gizmos.color = Color.red; Gizmos.DrawWireSphere(g.nodes[i].pos + up, 3f); }
        }
    }

    static Color ColorOf(StreetGraph.Edge e)
    {
        if ((e.flags & StreetGraph.EdgeFlags.GateLocked) != 0) return Color.red;
        if ((e.flags & StreetGraph.EdgeFlags.Pad) != 0) return Color.yellow;
        switch (e.kind)
        {
            case StreetGraph.EdgeKind.Road: return (e.flags & StreetGraph.EdgeFlags.Turn) != 0 ? new Color(1f, 0.47f, 0.08f) : new Color(1f, 0.77f, 0f);
            case StreetGraph.EdgeKind.Walk: return new Color(0f, 0.78f, 1f);
            case StreetGraph.EdgeKind.Crosswalk: return Color.white;
            case StreetGraph.EdgeKind.Jaywalk: return Color.magenta;
            case StreetGraph.EdgeKind.GardenPath: return new Color(0.16f, 0.9f, 0.24f);
            case StreetGraph.EdgeKind.Driveway: return new Color(0.8f, 0.55f, 0.27f);
            default: return new Color(0.67f, 1f, 0.47f);
        }
    }
#endif
}

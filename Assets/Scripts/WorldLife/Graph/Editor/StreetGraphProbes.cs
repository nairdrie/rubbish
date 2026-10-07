using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using EdgeKind = StreetGraph.EdgeKind;

/// <summary>
/// WorldLife package A: the street-graph checks of Docs/WorldLife.md §5.3 that need the built scene, plus the knockable
/// checks of §12.3 (A). Read-only (Physics queries against the loaded scene). Used by NeighborhoodBuilder.Validate and
/// Setup_WorldLifeGraph.Run.
/// </summary>
public static class StreetGraphProbes
{
    public const int LayerGround = 6;
    /// <summary>A person: a 0.35 m radius capsule from 0.15 to 1.9 m above the walk surface.</summary>
    public const float PersonRadius = 0.35f, PersonBottom = 0.5f, PersonTop = 1.55f;
    public const float LaneStep = 2f, WalkStep = 1f;

    static readonly string[] RoadSurfaces = { "Road_", "DepotPad_" };
    static readonly string[] DriveSurfaces = { "Road_", "DepotPad_", "CurbRamp", "Curb_", "Sidewalk_", "Driveway", "Drive", "DriveLink" };

    static readonly RaycastHit[] rayHits = new RaycastHit[32];
    static readonly Collider[] overlap = new Collider[64];

    /// <summary>
    /// Lane samples (every 2 m along Road and Driveway edges) raycast down onto a road / pad / ramp / driveway surface,
    /// and Road samples have car headroom (nothing solid in a 1.7 × 1.6 × 1.8 m box); walk samples (every 1 m along every
    /// walk-type edge) are clear of solid colliders for a 0.35 m radius person. Returns the number of failures.
    /// </summary>
    public static int Run(StreetGraph g, Scene scene, StringBuilder sb, int maxLines = 14)
    {
        Physics.SyncTransforms();
        // Parked household cars sit on their own driveways by design; a gate's barrier stands across its (GateLocked) lanes.
        var ignore = new HashSet<Collider>();
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var hc in root.GetComponentsInChildren<HouseholdCar>(true))
                foreach (var col in hc.GetComponentsInChildren<Collider>(true)) ignore.Add(col);
            foreach (var gate in root.GetComponentsInChildren<DistrictGate>(true))
                if (gate.barrierCollider != null) ignore.Add(gate.barrierCollider);
        }

        int laneSamples = 0, surfaceBad = 0, headBad = 0, walkSamples = 0, walkBad = 0, lines = 0;
        var badEdges = new HashSet<int>();
        void Line(string s) { if (lines++ < maxLines) sb.AppendLine("    " + s); }

        for (int e = 0; e < g.EdgeCount; e++)
        {
            var ed = g.edges[e];
            Vector3 a = g.nodes[ed.a].pos, b = g.nodes[ed.b].pos;
            bool road = ed.kind == EdgeKind.Road, drive = ed.kind == EdgeKind.Driveway;
            if (road || drive)
            {
                foreach (var p in Samples(a, b, LaneStep))
                {
                    laneSamples++;
                    string surf = SurfaceUnder(p, scene, ignore);
                    if (!StartsWithAny(surf, road ? RoadSurfaces : DriveSurfaces))
                    {
                        surfaceBad++; badEdges.Add(e);
                        Line($"lane {ed.kind} edge {e} ({ed.a}->{ed.b}) @ {p.ToString("F1")}: surface '{surf}'");
                    }
                    else if (road)
                    {
                        Vector3 dir = b - a; dir.y = 0f;
                        var rot = dir.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(dir) : Quaternion.identity;
                        int n = Physics.OverlapBoxNonAlloc(new Vector3(p.x, 0f, p.z) + Vector3.up * 1.05f, new Vector3(0.85f, 0.8f, 0.9f), overlap, rot, ~0, QueryTriggerInteraction.Ignore);
                        string blocker = FirstBlocker(n, scene, ignore, 0.3f);
                        if (blocker != null) { headBad++; badEdges.Add(e); Line($"lane edge {e} @ {p.ToString("F1")}: blocked by '{blocker}'"); }
                    }
                }
            }
            else
            {
                foreach (var p in Samples(a, b, WalkStep))
                {
                    walkSamples++;
                    float y = Mathf.Max(p.y, 0.1f);
                    var q = new Vector3(p.x, y, p.z);
                    int n = Physics.OverlapCapsuleNonAlloc(q + Vector3.up * PersonBottom, q + Vector3.up * PersonTop, PersonRadius, overlap, ~0, QueryTriggerInteraction.Ignore);
                    string blocker = FirstBlocker(n, scene, null, y + 0.3f);
                    if (blocker != null) { walkBad++; badEdges.Add(e); Line($"walk {ed.kind} edge {e} ({g.nodes[ed.a].kind}->{g.nodes[ed.b].kind}) @ {q.ToString("F1")}: inside '{blocker}'"); }
                }
            }
        }
        if (lines > maxLines) sb.AppendLine($"    ... {lines - maxLines} more");
        sb.AppendLine($"  street graph probes: lane samples {laneSamples} (off-road {surfaceBad}, blocked {headBad}); walk samples {walkSamples} (blocked {walkBad}); bad edges {badEdges.Count}");
        return surfaceBad + headBad + walkBad;
    }

    /// <summary>Points every <paramref name="step"/> m from a to b (both ends included).</summary>
    public static IEnumerable<Vector3> Samples(Vector3 a, Vector3 b, float step)
    {
        float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
        int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
        for (int i = 0; i <= n; i++) yield return Vector3.Lerp(a, b, i / (float)n);
    }

    /// <summary>Name of the first (highest) collider under a point, ignoring triggers and <paramref name="ignore"/>.</summary>
    public static string SurfaceUnder(Vector3 p, Scene scene, HashSet<Collider> ignore)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(p.x, 2f, p.z), Vector3.down, rayHits, 4f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Collider bc = null;
        for (int i = 0; i < n; i++)
        {
            var c = rayHits[i].collider;
            if (c == null || c.gameObject.scene != scene || (ignore != null && ignore.Contains(c))) continue;
            if (rayHits[i].distance < best) { best = rayHits[i].distance; bc = c; }
        }
        if (bc == null) return "(nothing)";
        return bc.gameObject.layer == LayerGround ? bc.name : bc.name + " [layer " + LayerMask.LayerToName(bc.gameObject.layer) + "]";
    }

    static string FirstBlocker(int n, Scene scene, HashSet<Collider> ignore, float floorTop)
    {
        for (int i = 0; i < n; i++)
        {
            var c = overlap[i];
            if (c == null || c.isTrigger || c.gameObject.scene != scene || (ignore != null && ignore.Contains(c))) continue;
            if (c.bounds.max.y <= floorTop) continue;          // the ground / a slab / a low curb under the probe
            return c.transform.parent != null ? c.transform.parent.name + "/" + c.name : c.name;
        }
        return null;
    }

    static bool StartsWithAny(string s, string[] prefixes)
    {
        foreach (var p in prefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Is a person standing at p (walk surface) clear of solid colliders?</summary>
    public static bool PersonClear(Vector3 p, Scene scene, out string blocker)
    {
        int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * PersonBottom, p + Vector3.up * PersonTop, PersonRadius, overlap, ~0, QueryTriggerInteraction.Ignore);
        blocker = FirstBlocker(n, scene, null, p.y + 0.3f);
        return blocker == null;
    }

    // ================================================================================================================
    // Knockables (Docs/WorldLife.md §12.3 A: ≥ 700, unique ids, non-mesh rest colliders)
    // ================================================================================================================

    public static int CheckKnockables(Scene scene, StringBuilder sb, out int count)
    {
        var all = new List<Knockable>();
        foreach (var root in scene.GetRootGameObjects()) all.AddRange(root.GetComponentsInChildren<Knockable>(true));
        count = all.Count;
        int fails = 0, lines = 0;
        void Fail(string s) { fails++; if (lines++ < 10) sb.AppendLine("    FAIL knockable " + s); }
        var ids = new HashSet<ushort>();
        var perKind = new int[Enum.GetValues(typeof(KnockKind)).Length];
        var perDistrict = new int[4];
        int restCols = 0;
        foreach (var k in all)
        {
            if (!ids.Add(k.id)) Fail($"duplicate id {k.id} ({k.name})");
            perKind[Mathf.Clamp((int)k.kind, 0, perKind.Length - 1)]++;
            perDistrict[Mathf.Min(3, (int)k.district)]++;
            if (k.restColliders == null || k.restColliders.Length == 0) { Fail($"{k.id} '{k.name}' has no rest colliders"); continue; }
            foreach (var c in k.restColliders)
            {
                if (c == null) { Fail($"{k.id} '{k.name}': null rest collider"); continue; }
                if (c is MeshCollider) Fail($"{k.id} '{k.name}': rest collider '{c.name}' is a MeshCollider");
                if (!c.enabled || c.isTrigger) Fail($"{k.id} '{k.name}': rest collider '{c.name}' disabled or a trigger");
                restCols++;
            }
            foreach (var mc in k.GetComponentsInChildren<MeshCollider>(true))
                if (mc.enabled) Fail($"{k.id} '{k.name}': enabled MeshCollider '{mc.name}' left on it");
            if (k.renderers == null || k.renderers.Length == 0) Fail($"{k.id} '{k.name}' has no renderers");
            if (string.IsNullOrEmpty(k.proxyKey)) Fail($"{k.id} '{k.name}' has no proxyKey");
            byte want = WorldLifeIds.DistrictIndex(Districts.AtPosition(k.transform.position));
            if (k.district != want) Fail($"{k.id} '{k.name}' district {k.district} but stands in {want}");
        }
        if (lines > 10) sb.AppendLine($"    ... {lines - 10} more");
        int maxId = -1; foreach (var k in all) maxId = Mathf.Max(maxId, k.id);
        var kinds = new StringBuilder();
        for (int i = 0; i < perKind.Length; i++) if (perKind[i] > 0) kinds.Append($" {(KnockKind)i} {perKind[i]},");
        sb.AppendLine($"  knockables: {count} (ids 0..{maxId}, unique {ids.Count}), rest colliders {restCols} (all primitive), per district {perDistrict[0]}/{perDistrict[1]}/{perDistrict[2]}/{perDistrict[3]}");
        sb.AppendLine("    by kind:" + kinds.ToString().TrimEnd(','));
        return fails;
    }
}

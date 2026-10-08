// Owner: fences agent. Contract: design/CONTRACT.md section 7.
//
// Role: turn TownLifePlan edges into the BOUNDARY NETWORK of the two starter districts (DESIGN decision 5, section 8
// "Fences"). Three consumers:
//   1. CoastalYardAuthoring.Rebuild (real pipeline) asks PlanForTown(town) and realises each Chain as ONE
//      CoastalYardBoundary marker (visual from BuildRunVisual, one BoxCollider per Run, boundaryPoints = run end pairs).
//   2. The preview draws the same runs through BuildPreview(ctx) (pseudo-module "fences").
//   3. Content modules read ctx.fences (yards plant borders along runs, streets hedge the streetFacing ones) and the
//      builder reserves every run in ctx.occupancy before any module runs (Reserve).
//
// How the network is planned (Plan):
//   A. every non-frontage plan edge becomes a candidate polyline: parcel-parcel and parcel-common edges as they are,
//      parcel-road edges pushed into the parcel until they sit StreetSetback behind the sidewalk, parcel-reserved edges
//      pushed ReservedInset into the parcel;
//   B. every candidate is sampled and clipped against the keep-outs (driveways, garden paths, buildings, pens, pools,
//      infill, depot pads, road reserve, water) and against the "front line" rule (nothing forward of the building front
//      of the parcels it bounds);
//   C. loose ends are resolved: an end that overshoots a junction is trimmed to it, an end that stops short of another
//      run is extended to it, two ends that miss each other at a corner are both moved to the corner;
//   D. each house gets two wing runs from its side walls out to the first boundary they meet, at the shallowest depth
//      from the front wall that is clear, one of them with the open gate gap;
//   E. fragments are dropped (both ends loose, or one loose end on a short piece).
// Everything is plain XZ geometry on typed data: no Physics, no RNG, no dictionary iteration order.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>Planned boundary runs per frontage chain, their styles, and the code that draws one run.</summary>
public static class TownLifeFences
{
    /// <summary>Style ids. A style fixes panel/post props, height and collider size. Consistent per chain.</summary>
    public const string StyleAcresBoard = "acres-board", StyleMeadowsPicket = "meadows-picket", StyleRearSpine = "rear-spine";
    /// <summary>Width of the open gate gap every fenced back yard keeps in one wing fence (posts, no leaf).</summary>
    public const float GateGap = 1.8f;
    /// <summary>Runs shorter than this are never emitted.</summary>
    public const float MinRun = 1.5f;

    // ---- planning constants (metres) ----
    public const float StreetSetback = 1.0f;      // street-facing runs: behind the sidewalk back edge
    public const float ReservedInset = 0.6f;      // runs along an infill garden: inside the parcel
    public const float DriveClear = 2.6f, PathClear = 1.4f, PenClear = 0.6f, KeepInClear = 0.5f, BuildingClear = 0.3f, InfillClear = 0.5f, DepotClear = 0.5f;
    public const float RoadClear = 3.6f;          // metres beyond the asphalt edge (sidewalk is 0..3)
    public const float PanelLength = 2f;          // modelled panel length of every style
    const float SidewalkWidth = 3f, Step = 0.2f, Eps = 0.05f, Touch = 0.22f, StubLength = 3f, MaxExtend = 2.6f;
    const int MaxPanels = 45;
    const float Straighten = 1.5f;                // lot lines are straightened by up to this much (plan edges follow the 0.5 m grid)

    public enum Role { Side, Rear, Wing, StreetSide, StreetRear }

    /// <summary>One straight fence piece. A run never bends: polylines are split into runs at every vertex.</summary>
    public sealed class Run
    {
        public Vector3 a, b;              // world end points, y = 0 (the consumer adds its own base height)
        public string style;              // one of the Style* ids
        public Role role;
        public string chain;              // owning chain id (TownLifePlan.Parcel.chain): the marker that carries this run
        public string roadId;             // frontage road of the owning chain
        public string[] stopIds;          // the 1 or 2 households this run bounds; the owner chain's household first
        public bool streetFacing;         // faces a street from 1 m behind the sidewalk: the Streets module hedges it
        public bool gatePostA, gatePostB; // this end is one side of the 1.8 m gate gap (the Gate draws the tall posts)
        public float height;              // visual height in metres (collider uses the style's)
        public float Length => TownLifeGeo.Dist(a, b);
        // ---- additions ----
        public bool looseA, looseB;       // this end meets nothing (ends in a post at an opening); informational
    }

    /// <summary>The open gate gap of one back yard: two tall posts and a lintel, no leaf, no collider.</summary>
    public sealed class Gate
    {
        public string stopId, chain, style;
        public Vector3 a, b;              // post positions (a = house side), y = 0
        public bool secondary;            // a second, narrower passage where a wing would have been shorter than MinRun
        public Vector3 Centre => (a + b) * 0.5f;
        public float Width => TownLifeGeo.Dist(a, b);
    }

    /// <summary>One frontage chain = one CoastalYardBoundary marker.</summary>
    public sealed class Chain
    {
        public string id;                 // == TownLifePlan.Parcel.chain
        public string roadId; public int side;
        public string[] stopIds;          // households in station order; stopIds[0]'s holder parents the marker
        public string style;              // dominant style of the chain
        public Run[] runs;                // every run this chain owns (each run belongs to exactly one chain)
        public Gate[] gates = new Gate[0];// gate gaps of this chain's households
    }

    /// <summary>The whole network.</summary>
    public sealed class Result
    {
        public Chain[] chains = new Chain[0];
        public Run[] runs = new Run[0];                              // all runs of all chains
        public Gate[] gates = new Gate[0];                           // all gate gaps
        public List<string> skipped = new List<string>();            // human-readable reasons for stretches left open
        public List<string> openYards = new List<string>();          // stopIds whose back yard is deliberately left open on a side
        /// <summary>The chain that owns a household's marker, or null.</summary>
        public Chain ChainOf(string stopId) { foreach (var c in chains) foreach (var s in c.stopIds) if (s == stopId) return c; return null; }
        /// <summary>Every run that bounds a household (its own chain's and neighbours').</summary>
        public List<Run> RunsOf(string stopId)
        {
            var list = new List<Run>(); foreach (var r in runs) foreach (var s in r.stopIds) if (s == stopId) { list.Add(r); break; }
            return list;
        }
        /// <summary>The gate gap of a household's back yard, or null.</summary>
        public Gate GateOf(string stopId) { foreach (var g in gates) if (g.stopId == stopId && !g.secondary) return g; return null; }
    }

    // =================================================================================================== planning

    sealed class Seg
    {
        public Vector3 a, b; public int pa, pb;                     // owner parcel; other parcel or a negative plan code
        public TownLifePlan.EdgeKind kind; public Role role; public string chain; public bool street, wing, dead;
        public bool frontA, frontB;                                 // this end was cut by the front-line rule (a legitimate free end)
        public bool wallA, gateA, gateB;                            // wing only: a = house wall; gate flags
        public bool looseA, looseB; public int edge;
        public float Length => TownLifeGeo.Dist(a, b);
        public Vector3 Dir => (b - a).normalized;
    }

    /// <summary>Why a point may not carry a fence, or null. <paramref name="ownBuilding"/> = house index whose building
    /// is ignored (wings start at their own wall); <paramref name="soft"/> also avoids loose lot props (wing search).</summary>
    sealed class KeepOut
    {
        readonly TownLifeScene scene; readonly TownLifePlan.Result plan;
        public readonly bool[] hasDrive; public readonly Vector3[] driveA, driveB; public readonly float[] driveHalf;
        readonly Vector3[] centre; readonly float[] reach2;          // bounding disc of everything a household can block with
        public KeepOut(TownLifeScene scene, TownLifePlan.Result plan)
        {
            this.scene = scene; this.plan = plan; int n = scene.houses.Length;
            hasDrive = new bool[n]; driveA = new Vector3[n]; driveB = new Vector3[n]; driveHalf = new float[n];
            centre = new Vector3[n]; reach2 = new float[n];
            for (int i = 0; i < n; i++)
            {
                // the slab itself (TownLifePlan.House.driveA/B may describe the curb ramp, which is also a "Driveway" item)
                var h = scene.houses[i]; float best = 0f;
                if (h.items != null) foreach (var item in h.items)
                {
                    if (item.kind != TownLifeScene.LotItemKind.Driveway || item.name == "CurbRamp") continue;
                    Vector3 a = (item.rect.a + item.rect.b) * 0.5f, b = (item.rect.c + item.rect.d) * 0.5f; float len = TownLifeGeo.Dist(a, b);
                    if (len <= best) continue;
                    best = len; hasDrive[i] = true; driveA[i] = TownLifeGeo.Flat(a); driveB[i] = TownLifeGeo.Flat(b); driveHalf[i] = TownLifeGeo.Dist(item.rect.a, item.rect.b) * 0.5f;
                }
                if (!hasDrive[i] && h.route != null && h.route.hasDrive) { hasDrive[i] = true; driveA[i] = TownLifeGeo.Flat(h.route.ramp); driveB[i] = TownLifeGeo.Flat(h.route.top); driveHalf[i] = 1.6f; }
                Vector3 c = h.rearDepth > h.frontDepth ? TownLifeGeo.Centre(h.plan.footprint) : TownLifeGeo.Flat(h.plan.origin); float r = 0f;
                void Grow(Vector3 q) { r = Mathf.Max(r, TownLifeGeo.Dist(c, q)); }
                void GrowQuad(TownLifePlan.Quad q) { Grow(q.a); Grow(q.b); Grow(q.c); Grow(q.d); }
                Grow(h.plan.origin); if (h.rearDepth > h.frontDepth) GrowQuad(h.plan.footprint);
                if (hasDrive[i]) { Grow(driveA[i]); Grow(driveB[i]); }
                if (h.route != null && h.route.hasDrive) { Grow(h.route.ramp); Grow(h.route.top); }
                if (h.plan.walk != null) foreach (var w in h.plan.walk) Grow(w);
                if (h.plan.keepIn != null) foreach (var q in h.plan.keepIn) GrowQuad(q);
                if (h.items != null) foreach (var item in h.items) GrowQuad(item.rect);
                centre[i] = c; r += Mathf.Max(DriveClear, driveHalf[i] + 0.3f) + 1f; reach2[i] = r * r;
            }
        }

        public string Blocked(Vector3 p, int ownBuilding, bool soft, float slack = 0f)
        {
            if (plan.RoadEdgeDistance(p) < RoadClear - slack) return "road reserve";
            if (plan.WaterDistance(p) < 0f - slack) return "water";
            var houses = scene.houses;
            for (int i = 0; i < houses.Length; i++)
            {
                var h = houses[i]; var hp = h.plan;
                float dx = p.x - centre[i].x, dz = p.z - centre[i].z; if (dx * dx + dz * dz > reach2[i]) continue;
                if (hasDrive[i] && TownLifeGeo.DistToSegment(p, driveA[i], driveB[i]) < Mathf.Max(DriveClear, driveHalf[i] + 0.3f) + Eps - slack) return "driveway " + hp.stopId;
                if (h.route != null && h.route.hasDrive && TownLifeGeo.DistToSegment(p, h.route.ramp, h.route.top) < DriveClear + Eps - slack) return "driveway " + hp.stopId;
                if (hp.walk != null) for (int k = 1; k < hp.walk.Length; k++)
                    if (TownLifeGeo.DistToSegment(p, hp.walk[k - 1], hp.walk[k]) < PathClear + Eps - slack) return "garden path " + hp.stopId;
                if (i != ownBuilding && h.rearDepth > h.frontDepth && TownLifeGeo.QuadContains(hp.footprint, p, BuildingClear - slack)) return "building " + hp.stopId;
                if (hp.keepIn != null) foreach (var q in hp.keepIn) if (TownLifeGeo.QuadContains(q, p, KeepInClear - slack)) return "yard item " + hp.stopId;
                if (h.items != null) foreach (var item in h.items)
                {
                    switch (item.kind)
                    {
                        case TownLifeScene.LotItemKind.Driveway: if (item.name != "CurbRamp" && TownLifeGeo.QuadContains(item.rect, p, 0.25f - slack)) return "driveway slab " + hp.stopId; break;
                        case TownLifeScene.LotItemKind.KidPen: if (TownLifeGeo.QuadContains(item.rect, p, PenClear - slack)) return "kid pen " + hp.stopId; break;
                        case TownLifeScene.LotItemKind.Pool: case TownLifeScene.LotItemKind.Trampoline:
                            if (TownLifeGeo.QuadContains(item.rect, p, KeepInClear - slack)) return item.name + " " + hp.stopId; break;
                        case TownLifeScene.LotItemKind.Picket: if (TownLifeGeo.QuadContains(item.rect, p, 0.3f - slack)) return "front picket " + hp.stopId; break;
                        case TownLifeScene.LotItemKind.Path: break;
                        default:
                            if (soft && item.hasCollider && item.kind != TownLifeScene.LotItemKind.Other && TownLifeGeo.QuadContains(item.rect, p, 0.35f)) return item.name + " " + hp.stopId;
                            break;
                    }
                }
            }
            foreach (var pen in scene.kidPens) if (TownLifeGeo.QuadContains(pen.quad, p, PenClear - slack)) return "kid pen " + pen.penId;
            foreach (var site in scene.infill) if (TownLifeGeo.QuadContains(site.quad, p, InfillClear - slack)) return "infill " + site.id;
            foreach (var pad in scene.depotPads) if (TownLifeGeo.RectContains(pad, p, DepotClear - slack)) return "depot pad";
            foreach (var q in scene.authoredObstacles) if (TownLifeGeo.QuadContains(q, p, 0.5f - slack)) return "authored object";
            return null;
        }
    }

    /// <summary>Plans the network from a scene snapshot and its parcel plan. Pure and deterministic.</summary>
    public static Result Plan(TownLifeScene scene, TownLifePlan.Result plan)
    {
        var result = new Result();
        if (scene == null || plan == null || plan.parcels == null || plan.parcels.Length == 0 || plan.edges == null) return result;
        var parcels = plan.parcels; var keep = new KeepOut(scene, plan); var segs = new List<Seg>();

        // ---- A + B: candidate polylines from plan edges, clipped ----
        for (int ei = 0; ei < plan.edges.Length; ei++)
        {
            var e = plan.edges[ei];
            if (e.frontage || e.points == null || e.points.Length < 2 || e.a < 0 || e.a >= parcels.Length) continue;
            if (e.kind == TownLifePlan.EdgeKind.ParcelWater) continue;
            var pa = parcels[e.a]; int pb = e.kind == TownLifePlan.EdgeKind.ParcelParcel ? e.b : (e.b < 0 ? e.b : TownLifePlan.Common);
            var pts = new List<Vector3>(); foreach (var p in e.points) pts.Add(TownLifeGeo.Flat(p));
            bool street = e.kind == TownLifePlan.EdgeKind.ParcelRoad;
            if (street) pts = BehindSidewalk(plan, pts, true);
            else
            {
                // straighten the grid-following lot line, unless that drags it into something it cleared before
                var straight = new List<Vector3>(pts); Simplify(straight, Straighten);
                if (straight.Count < pts.Count && BlockedSamples(keep, straight) > BlockedSamples(keep, pts)) { straight = new List<Vector3>(pts); Simplify(straight, 0.35f); }
                pts = BehindSidewalk(plan, straight, false);
                if (e.kind == TownLifePlan.EdgeKind.ParcelReserved)
                {
                    // which side of the polyline is the parcel on? (majority vote on the plan grid)
                    int vote = 0;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        var mid = (pts[i - 1] + pts[i]) * 0.5f; var right = TownLifeGeo.Right((pts[i] - pts[i - 1]).normalized);
                        if (plan.LabelAt(mid + right * 1.1f) == e.a) vote++; if (plan.LabelAt(mid - right * 1.1f) == e.a) vote--;
                    }
                    if (vote == 0) result.skipped.Add(pa.house.stopId + ": cannot tell the parcel side of a " + e.kind + " edge; left on the lot line");
                    else pts = new List<Vector3>(TownLifeGeo.Offset(pts, vote > 0 ? ReservedInset : -ReservedInset));
                }
            }
            Tidy(pts);
            string chain = e.kind == TownLifePlan.EdgeKind.ParcelParcel && !string.IsNullOrEmpty(e.ownerChain) ? e.ownerChain : pa.chain;
            int owner = e.a, other = pb;
            if (e.kind == TownLifePlan.EdgeKind.ParcelParcel && pb >= 0 && pb < parcels.Length && parcels[pb].chain == chain && pa.chain != chain) { owner = pb; other = e.a; }
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 a = pts[i - 1], b = pts[i]; float len = TownLifeGeo.Dist(a, b); if (len < 0.3f) continue;
                Role role = RoleOf(parcels, e, a, b);
                bool facing = street;
                if (!facing)
                {
                    // a lot line that was pushed back from a street and now runs along it
                    Vector3 mid = (a + b) * 0.5f; float d0 = plan.RoadEdgeDistance(a), d1 = plan.RoadEdgeDistance(b), dm = plan.RoadEdgeDistance(mid);
                    facing = len > 2f && dm < SidewalkWidth + StreetSetback + 0.4f && d0 < SidewalkWidth + StreetSetback + 0.6f && d1 < SidewalkWidth + StreetSetback + 0.6f;
                }
                // sample, find the clear intervals
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / Step)); int start = -1; bool startFront = false; string lastReason = null;
                for (int s = 0; s <= steps + 1; s++)
                {
                    string reason = "end";
                    if (s <= steps)
                    {
                        var p = Vector3.Lerp(a, b, s / (float)steps);
                        reason = Forward(plan, e, p) ? "front" : keep.Blocked(p, -1, false);
                    }
                    if (reason == null) { if (start < 0) { start = s; startFront = lastReason == "front"; } }
                    else if (start >= 0)
                    {
                        int s0 = start, s1 = s - 1;
                        // one step of safety next to a keep-out (not next to the natural ends or the front line)
                        var seg = new Seg { a = Vector3.Lerp(a, b, s0 / (float)steps), b = Vector3.Lerp(a, b, s1 / (float)steps), pa = owner, pb = other, kind = e.kind, role = role, chain = chain, street = facing, edge = ei,
                            frontA = startFront, frontB = reason == "front" };
                        if (seg.Length >= 0.6f) segs.Add(seg);
                        if (reason != "end" && reason != "front" && reason != "road reserve" && seg.Length >= MinRun) Note(result, parcels[owner].house.stopId + ": " + role + " run interrupted by " + reason);
                        start = -1;
                    }
                    else if (s == 0 && reason != "front" && reason != "end" && reason != "road reserve") Note(result, parcels[owner].house.stopId + ": " + role + " run starts behind " + reason);
                    lastReason = reason;
                }
            }
        }

        // ---- C: resolve loose ends ----
        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i]; if (s.dead) continue;
                for (int end = 0; end < 2; end++)
                {
                    if (end == 0 ? s.frontA : s.frontB) continue;
                    if (s.Length < 0.3f) { s.dead = true; break; }
                    Vector3 e = end == 0 ? s.a : s.b, o = end == 0 ? s.b : s.a, outward = (e - o).normalized; float len = s.Length;
                    if (EndConnected(segs, s, e)) continue;
                    // 1. overshoot: the nearest junction on the body within reach of the end
                    float bestT = float.MaxValue; Vector3 bestP = e; Seg mutual = null; int mutualEnd = 0;
                    for (int j = 0; j < segs.Count; j++)
                    {
                        var t = segs[j]; if (t == s || t.dead) continue;
                        if (!LineHit(e, outward, t.a, t.b, out float along, out float u, out Vector3 x)) continue;
                        float tl = t.Length; float over = u < 0f ? -u * tl : (u > 1f ? (u - 1f) * tl : 0f);
                        if (along < -Mathf.Min(MaxExtend, len - 0.2f) || along > MaxExtend) continue;
                        if (over <= Touch)
                        {
                            if (Mathf.Abs(along) < Mathf.Abs(bestT)) { bestT = along; bestP = x; mutual = null; }
                        }
                        else if (over <= MaxExtend)
                        {
                            // corner where both runs stop short / overshoot: move both ends to the corner
                            int te = u < 0f ? 0 : 1; if (te == 0 ? t.frontA : t.frontB) continue;
                            if (EndConnected(segs, t, te == 0 ? t.a : t.b)) continue;
                            float cost = Mathf.Abs(along) + over;
                            if (cost < Mathf.Abs(bestT)) { bestT = cost; bestP = x; mutual = t; mutualEnd = te; }
                        }
                    }
                    if (bestT == float.MaxValue) continue;
                    if (!ClearStretch(keep, e, bestP) || (mutual != null && !ClearStretch(keep, mutualEnd == 0 ? mutual.a : mutual.b, bestP))) continue;
                    if (end == 0) s.a = bestP; else s.b = bestP;
                    if (mutual != null) { if (mutualEnd == 0) mutual.a = bestP; else mutual.b = bestP; }
                }
            }
        }
        segs.RemoveAll(s => s.dead || s.Length < 0.3f);
        // two loose ends that face each other across a small gap (a lot line that stepped sideways inside the street
        // band, a corner both runs were trimmed from): bridge them, or pull the nearer end across
        for (int i = 0, count = segs.Count; i < count; i++)
        {
            var s = segs[i];
            for (int end = 0; end < 2; end++)
            {
                if (end == 0 ? s.frontA : s.frontB) continue;
                Vector3 e = end == 0 ? s.a : s.b; if (EndConnected(segs, s, e)) continue;
                float best = 4.5f; Vector3 target = e; bool found = false;
                for (int j = 0; j < count; j++)
                {
                    var t = segs[j]; if (t == s) continue;
                    for (int te = 0; te < 2; te++)
                    {
                        if (te == 0 ? t.frontA : t.frontB) continue;
                        Vector3 q = te == 0 ? t.a : t.b; float d = TownLifeGeo.Dist(e, q);
                        if (d < best && d > Touch && !EndConnected(segs, t, q)) { best = d; target = q; found = true; }
                    }
                }
                if (!found || !ClearStretch(keep, e, target)) continue;
                if (best >= MinRun) segs.Add(new Seg { a = e, b = target, pa = s.pa, pb = s.pb, kind = s.kind, role = s.role, chain = s.chain, street = false, edge = s.edge });
                else if (end == 0) s.a = target; else s.b = target;
            }
        }
        segs.RemoveAll(s => s.dead || s.Length < MinRun);

        // ---- D: wings and gates ----
        var gates = new List<Gate>();
        for (int pi = 0; pi < parcels.Length && pi < scene.houses.Length; pi++)
        {
            var parcel = parcels[pi]; var h = scene.houses[pi]; var hp = h.plan;
            if (h.rearDepth - h.frontDepth < 2f) { result.skipped.Add(hp.stopId + ": no building footprint, no wings"); result.openYards.Add(hp.stopId); continue; }
            var wing = new Seg[2];
            for (int k = 0; k < 2; k++)
            {
                int sign = k == 0 ? -1 : 1; Vector3 dir = hp.right * sign; string why = "no boundary beside the house";
                float last = h.rearDepth + 0.6f;                                  // one extra try: from the rear corner, any direction that does not come forward
                for (float d = h.frontDepth + 0.1f; d <= last && wing[k] == null; d += 0.5f)
                {
                    bool corner = d > h.rearDepth + 0.01f; if (corner) d = last;
                    float depth = Mathf.Min(d, h.rearDepth - 0.05f);
                    Vector3 wall = h.LotPoint(sign < 0 ? h.leftExtent : h.rightExtent, depth); wall.y = 0f;
                    // the shortest hop from the wall to a boundary of this lot on that side (square to straight dividers)
                    float best = float.MaxValue; Vector3 hit = wall;
                    // first choice: straight out, flush with the wall line (reads as one street wall: house, fence, house)
                    if (!corner) foreach (var t in segs)
                    {
                        if (t.wing || t.dead || (t.pa != pi && t.pb != pi) || !LineHit(wall, dir, t.a, t.b, out float along, out float u, out Vector3 x)) continue;
                        float tl = t.Length; if (u * tl < -Touch || (u - 1f) * tl > Touch) continue;
                        if (along > 0.4f && along < best) { best = along; hit = x; }
                    }
                    // else the shortest hop to a boundary of this lot on that side that does not come forward
                    if (best > 40f) foreach (var t in segs)
                    {
                        if (t.wing || t.dead || (t.pa != pi && t.pb != pi)) continue;
                        Vector3 x = TownLifeGeo.ClosestOnSegment(wall, t.a, t.b, out _); float dist = TownLifeGeo.Dist(wall, x);
                        if (dist < 0.4f || dist >= best) continue;
                        Vector3 toward = (x - wall) / dist;
                        if (Vector3.Dot(toward, hp.inward) < -0.2f || Vector3.Dot(toward, dir) < (corner ? -0.1f : 0.5f)) continue;
                        best = dist; hit = x;
                    }
                    if (best > 40f) { why = "no boundary beside the house"; continue; }
                    string blocked = null; int steps = Mathf.Max(1, Mathf.CeilToInt(best / Step));
                    for (int s = 0; s <= steps && blocked == null; s++) blocked = keep.Blocked(Vector3.Lerp(wall, hit, s / (float)steps), pi, true);
                    if (blocked == null)
                    {
                        // the wing must stay on the house's own land
                        for (int s = 1; s < steps - 2 && blocked == null; s += 3) { int label = plan.LabelAt(Vector3.Lerp(wall, hit, s / (float)steps)); if (label >= 0 && label != pi) blocked = "neighbouring lot"; }
                    }
                    if (blocked != null) { why = blocked; continue; }
                    wing[k] = new Seg { a = wall, b = hit, pa = pi, pb = TownLifePlan.Common, kind = TownLifePlan.EdgeKind.ParcelParcel, role = Role.Wing, chain = parcel.chain, wing = true, wallA = true, edge = -1 };
                }
                if (wing[k] == null) { result.skipped.Add(hp.stopId + ": " + (sign < 0 ? "left" : "right") + " wing left open (" + why + ")"); if (!result.openYards.Contains(hp.stopId)) result.openYards.Add(hp.stopId); }
            }
            // gate side: the driveway side when there is one, else the door side
            float centre = (h.leftExtent + h.rightExtent) * 0.5f; int gateSide;
            if (keep.hasDrive[pi]) gateSide = plan.AcrossInParcel((keep.driveA[pi] + keep.driveB[pi]) * 0.5f, pi) < centre ? 0 : 1;
            else gateSide = hp.walk != null && hp.walk.Length > 0 && plan.AcrossInParcel(hp.walk[0], pi) < centre ? 0 : 1;
            if (wing[0] != null && wing[1] != null)
            {
                // a gap needs room for itself and a real run beside it; otherwise use the other wing; failing that the
                // whole (short) wing is the opening
                int g = gateSide; float room = GateGap + MinRun;
                if (wing[g].Length < room && wing[1 - g].Length >= room) g = 1 - g;
                else if (wing[g].Length < room && wing[1 - g].Length < room) g = wing[0].Length >= GateGap && (wing[1].Length < GateGap || gateSide == 0) ? 0 : 1;
                var w = wing[g]; Vector3 dir = w.Dir; var gate = new Gate { stopId = hp.stopId, chain = parcel.chain, a = w.a };
                if (w.Length >= room) { gate.b = w.a + dir * GateGap; w.a = gate.b; w.wallA = false; w.gateA = true; }
                else { gate.b = w.b; wing[g] = null; if (gate.Width > GateGap + 0.05f || gate.Width < GateGap - 0.05f) Note(result, hp.stopId + ": the whole " + gate.Width.ToString("0.0") + " m wing is the gate opening"); }
                gates.Add(gate);
            }
            foreach (var w in wing)
            {
                if (w == null) continue;
                if (w.Length >= MinRun) { segs.Add(w); continue; }
                // too short for a run: a framed side passage instead of a stub
                gates.Add(new Gate { stopId = hp.stopId, chain = parcel.chain, a = w.a, b = w.b, secondary = true });
                Note(result, hp.stopId + ": " + w.Length.ToString("0.0") + " m wing is a framed side passage (shorter than a run)");
            }
        }

        // ---- E: drop fragments ----
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (var s in segs)
            {
                if (s.dead) continue;
                s.looseA = !(s.wing && (s.wallA || s.gateA)) && !EndConnected(segs, s, s.a);
                s.looseB = !EndConnected(segs, s, s.b);
                bool fa = s.looseA && !s.frontA, fb = s.looseB && !s.frontB;      // loose ends that are not the planned front ends
                bool drop = (s.looseA && s.looseB) || ((fa || fb) && s.Length < StubLength);
                if (drop) { s.dead = true; changed = true; Note(result, parcels[s.pa].house.stopId + ": dropped a loose " + s.Length.ToString("0.0") + " m " + s.role + " fragment"); }
            }
            if (!changed) break;
        }
        segs.RemoveAll(s => s.dead || s.Length < MinRun);

        // ---- runs, chains ----
        var chainRuns = new Dictionary<string, List<Run>>(); var chainGates = new Dictionary<string, List<Gate>>();
        var chainStyle = new Dictionary<string, string>(); var all = new List<Run>();
        string[] chainIds = plan.chains ?? new string[0];
        for (int c = 0; c < chainIds.Length; c++)
        {
            chainRuns[chainIds[c]] = new List<Run>(); chainGates[chainIds[c]] = new List<Gate>();
            var first = parcels[plan.chainParcels[c][0]].house;
            chainStyle[chainIds[c]] = first.districtId == TownLifeScene.Meadows ? StyleMeadowsPicket : StyleAcresBoard;
        }
        foreach (var s in segs)
        {
            if (!chainRuns.TryGetValue(s.chain, out var list)) { result.skipped.Add("run with unknown chain " + s.chain); continue; }
            var ownerHouse = parcels[s.pa].house;
            // the tall plain spine only deep inside a block or along open land, never where it would face a street
            string style = s.role == Role.Rear && !s.street && plan.RoadEdgeDistance((s.a + s.b) * 0.5f) > SidewalkWidth + 5f ? StyleRearSpine : chainStyle[s.chain];
            var run = new Run { a = s.a, b = s.b, style = style, role = s.role, chain = s.chain, roadId = ownerHouse.roadId, streetFacing = s.street,
                gatePostA = s.gateA, gatePostB = s.gateB, height = StyleHeight(style), looseA = s.looseA, looseB = s.looseB,
                stopIds = s.pb >= 0 && s.pb < parcels.Length ? new[] { ownerHouse.stopId, parcels[s.pb].house.stopId } : new[] { ownerHouse.stopId } };
            list.Add(run); all.Add(run);
        }
        foreach (var g in gates) { g.style = chainStyle.TryGetValue(g.chain, out var st) ? st : StyleAcresBoard; if (chainGates.TryGetValue(g.chain, out var gl)) gl.Add(g); }
        var chains = new List<Chain>();
        for (int c = 0; c < chainIds.Length; c++)
        {
            var members = plan.chainParcels[c]; var first = parcels[members[0]].house; var ids = new List<string>(); bool oneRoad = true;
            foreach (int m in members) { ids.Add(parcels[m].house.stopId); if (parcels[m].house.roadId != first.roadId) oneRoad = false; }
            if (!oneRoad) { result.skipped.Add("chain " + chainIds[c] + " spans two roads; left to the legacy generator"); foreach (var r in chainRuns[chainIds[c]]) all.Remove(r); continue; }
            if (chainRuns[chainIds[c]].Count == 0) { result.skipped.Add("chain " + chainIds[c] + " owns no run"); continue; }
            chains.Add(new Chain { id = chainIds[c], roadId = first.roadId, side = first.side, stopIds = ids.ToArray(), style = chainStyle[chainIds[c]], runs = chainRuns[chainIds[c]].ToArray(), gates = chainGates[chainIds[c]].ToArray() });
        }
        result.chains = chains.ToArray(); result.runs = all.ToArray();
        var keptGates = new List<Gate>(); foreach (var c in chains) keptGates.AddRange(c.gates); result.gates = keptGates.ToArray();
        return result;
    }

    static void Note(Result result, string message) { if (!result.skipped.Contains(message)) result.skipped.Add(message); }

    /// <summary>A parcel-road edge moved to StreetSetback behind the sidewalk: every sample walks along the gradient of
    /// the road-edge distance until it sits exactly there, then the line is simplified again.</summary>
    static List<Vector3> BehindSidewalk(TownLifePlan.Result plan, List<Vector3> pts, bool always)
    {
        float target = SidewalkWidth + StreetSetback;
        if (!always)
        {
            // any other lot line: only where it strays into the street band (then it turns and runs along the street)
            bool near = false;
            for (int i = 1; i < pts.Count && !near; i++)
            {
                float len = TownLifeGeo.Dist(pts[i - 1], pts[i]); int n = Mathf.Max(1, Mathf.CeilToInt(len / 1f));
                for (int k = 0; k <= n && !near; k++) near = plan.RoadEdgeDistance(Vector3.Lerp(pts[i - 1], pts[i], k / (float)n)) < target - 0.02f;
            }
            if (!near) return pts;
        }
        var dense = new List<Vector3>();
        for (int i = 1; i < pts.Count; i++)
        {
            float len = TownLifeGeo.Dist(pts[i - 1], pts[i]); int n = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
            for (int k = i == 1 ? 0 : 1; k <= n; k++) dense.Add(Vector3.Lerp(pts[i - 1], pts[i], k / (float)n));
        }
        for (int i = 0; i < dense.Count; i++)
        {
            var p = dense[i];
            for (int it = 0; it < 6; it++)
            {
                const float h = 0.3f; float d = plan.RoadEdgeDistance(p);
                if (!always && d >= target - 0.02f) break;
                var g = new Vector3(plan.RoadEdgeDistance(p + Vector3.right * h) - plan.RoadEdgeDistance(p - Vector3.right * h), 0f, plan.RoadEdgeDistance(p + Vector3.forward * h) - plan.RoadEdgeDistance(p - Vector3.forward * h));
                if (g.sqrMagnitude < 1e-6f) break;
                p += g.normalized * Mathf.Clamp(target - d, -1.5f, 1.5f);
            }
            dense[i] = p;
        }
        for (int i = dense.Count - 1; i > 0; i--) if (TownLifeGeo.Dist(dense[i], dense[i - 1]) < 0.05f && dense.Count > 2) dense.RemoveAt(i < dense.Count - 1 ? i : i - 1);
        Simplify(dense, 0.3f);
        return dense;
    }

    static int BlockedSamples(KeepOut keep, List<Vector3> pts)
    {
        int n = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            float len = TownLifeGeo.Dist(pts[i - 1], pts[i]); int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f));
            for (int k = 0; k <= steps; k++) { string why = keep.Blocked(Vector3.Lerp(pts[i - 1], pts[i], k / (float)steps), -1, false); if (why != null && why != "road reserve") n++; }
        }
        return n;
    }

    /// <summary>Douglas-Peucker on an open polyline (end points stay).</summary>
    static void Simplify(List<Vector3> pts, float tolerance)
    {
        if (pts.Count < 3) return;
        var keepPoint = new bool[pts.Count]; keepPoint[0] = keepPoint[pts.Count - 1] = true;
        var stack = new Stack<Vector2Int>(); stack.Push(new Vector2Int(0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var range = stack.Pop(); float worst = tolerance; int at = -1;
            for (int i = range.x + 1; i < range.y; i++) { float d = TownLifeGeo.DistToSegment(pts[i], pts[range.x], pts[range.y]); if (d > worst) { worst = d; at = i; } }
            if (at < 0) continue;
            keepPoint[at] = true; stack.Push(new Vector2Int(range.x, at)); stack.Push(new Vector2Int(at, range.y));
        }
        for (int i = pts.Count - 1; i >= 0; i--) if (!keepPoint[i]) pts.RemoveAt(i);
    }

    /// <summary>Removes interior vertices that would leave a straight piece shorter than a sensible run.</summary>
    static void Tidy(List<Vector3> pts)
    {
        while (pts.Count > 2)
        {
            int worst = -1; float shortest = MinRun + 0.5f;
            for (int i = 1; i < pts.Count; i++) { float d = TownLifeGeo.Dist(pts[i - 1], pts[i]); if (d < shortest) { shortest = d; worst = i; } }
            if (worst < 0) break;
            pts.RemoveAt(worst == pts.Count - 1 ? worst - 1 : worst);      // never an end point
        }
    }

    /// <summary>True when the point lies forward of the front line of every parcel the edge bounds.</summary>
    static bool Forward(TownLifePlan.Result plan, TownLifePlan.Edge e, Vector3 p)
    {
        var pa = plan.parcels[e.a]; bool fa = plan.DepthInParcel(p, e.a) < pa.frontLine - 0.01f;
        if (e.kind != TownLifePlan.EdgeKind.ParcelParcel || e.b < 0 || e.b >= plan.parcels.Length) return fa;
        return fa && plan.DepthInParcel(p, e.b) < plan.parcels[e.b].frontLine - 0.01f;
    }

    static Role RoleOf(TownLifePlan.Parcel[] parcels, TownLifePlan.Edge e, Vector3 a, Vector3 b)
    {
        Vector3 dir = (b - a).normalized; bool alongA = Mathf.Abs(Vector3.Dot(dir, parcels[e.a].house.right)) > 0.707f;
        switch (e.kind)
        {
            case TownLifePlan.EdgeKind.ParcelRoad: return alongA ? Role.StreetRear : Role.StreetSide;
            case TownLifePlan.EdgeKind.ParcelParcel:
                if (e.b < 0 || e.b >= parcels.Length || parcels[e.a].chain == parcels[e.b].chain) return Role.Side;
                return alongA || Mathf.Abs(Vector3.Dot(dir, parcels[e.b].house.right)) > 0.707f ? Role.Rear : Role.Side;
            default: return alongA ? Role.Rear : Role.Side;
        }
    }

    /// <summary>Intersection of the line through <paramref name="o"/> along <paramref name="dir"/> with the line a-b:
    /// along = signed metres from o, u = parameter on a-b (0..1 inside). False when nearly parallel.</summary>
    static bool LineHit(Vector3 o, Vector3 dir, Vector3 a, Vector3 b, out float along, out float u, out Vector3 x)
    {
        along = u = 0f; x = o; float ex = b.x - a.x, ez = b.z - a.z; float den = dir.x * ez - dir.z * ex;
        float el = Mathf.Sqrt(ex * ex + ez * ez); if (el < 1e-4f || Mathf.Abs(den) < 0.2f * el) return false;      // < ~11.5 degrees apart
        float wx = a.x - o.x, wz = a.z - o.z;
        along = (wx * ez - wz * ex) / den; u = (wx * dir.z - wz * dir.x) / den;
        x = new Vector3(o.x + dir.x * along, 0f, o.z + dir.z * along); return true;
    }

    static bool EndConnected(List<Seg> segs, Seg self, Vector3 e)
    {
        foreach (var t in segs) if (t != self && !t.dead && TownLifeGeo.DistToSegment(e, t.a, t.b) <= Touch) return true;
        return false;
    }

    static bool ClearStretch(KeepOut keep, Vector3 from, Vector3 to)
    {
        float len = TownLifeGeo.Dist(from, to); int steps = Mathf.Max(1, Mathf.CeilToInt(len / Step));
        for (int s = 1; s <= steps; s++) if (keep.Blocked(Vector3.Lerp(from, to, s / (float)steps), -1, false) != null) return false;
        return true;
    }

    /// <summary>Convenience for CoastalYardAuthoring.Rebuild: capture the town, build the parcel plan, plan the
    /// network. Returns null when the town has no starter-district household with road context (test fixtures, a
    /// household whose road is unknown or far away) or when planning fails, in which case the caller keeps the legacy
    /// algorithm for everything. No caching: capture + plan cost ~1.5 s and a stale plan would be worse than the wait.</summary>
    public static Result PlanForTown(Transform town)
    {
        if (town == null || !CoastalCityLayout.IsCoastal(town)) return null;
        try
        {
            var scene = TownLifeScene.Capture(town);
            if (scene.houses.Length == 0) return null;
            // a full build plans yards BEFORE the infill gardens exist; without their footprints the parcels would run
            // straight through them, so that pass keeps the legacy yards (the incremental publish builds the network)
            var blueprintInfill = CoastalCityLayout.Data.infillSites;
            if (blueprintInfill != null && blueprintInfill.Length > 0 && town.GetComponentsInChildren<CoastalInfillSite>(true).Length == 0) return null;
            foreach (var h in scene.houses)
            {
                if (h.route == null || h.building == null || scene.Road(h.plan.roadId) == null || !TownLifeGeo.RectContains(scene.area, h.plan.origin)) return null;
                if (scene.RoadEdgeDistance(h.plan.origin, out _) > 20f) return null;
            }
            var plan = TownLifePlan.Build(scene.ToPlanInput());
            if (plan == null || plan.parcels == null || plan.parcels.Length != scene.houses.Length) return null;
            var result = Plan(scene, plan);
            return result.chains.Length == 0 ? null : result;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[TownLifeFences] planning failed; yards fall back to the legacy generator: " + e);
            return null;
        }
    }

    // ==================================================================================================== styles

    static float StyleHeight(string style) { return style == StyleRearSpine ? 1.8f : style == StyleMeadowsPicket ? 1.2f : 1.5f; }

    /// <summary>Collider box size for a style: x = thickness, y = height (z comes from the run length).</summary>
    public static Vector2 ColliderSize(string style) { return new Vector2(0.2f, StyleHeight(style)); }

    static string RunProp(string style, int panels) { return "fence." + style + "." + panels; }
    static string GateProp(string style) { return style == StyleMeadowsPicket ? "fence.gate.picket" : "fence.gate.board"; }

    /// <summary>Defines the fence panel / post props and colours of every style in the kit (idempotent per session).
    /// One prop per style and panel count ("fence.&lt;style&gt;.&lt;n&gt;", n panels of 2 m along +Z with posts at every
    /// joint), so runs share meshes and a run is ONE object stretched by at most a quarter.</summary>
    public static void DefineStyles()
    {
        if (TownLifeKit.HasProp(RunProp(StyleAcresBoard, 1))) return;
        TownLifeKit.Define("fence.board.a", new Color(0.86f, 0.60f, 0.33f));
        TownLifeKit.Define("fence.board.b", new Color(0.78f, 0.52f, 0.27f));
        TownLifeKit.Define("fence.board.cap", new Color(0.52f, 0.31f, 0.16f));
        TownLifeKit.Define("fence.board.rail", new Color(0.70f, 0.43f, 0.20f));
        TownLifeKit.Define("fence.cream", new Color(0.98f, 0.93f, 0.78f));
        TownLifeKit.Define("fence.white", new Color(0.97f, 0.97f, 0.94f));
        TownLifeKit.Define("fence.spine", new Color(0.80f, 0.60f, 0.40f));
        TownLifeKit.Define("fence.spine.dark", new Color(0.56f, 0.36f, 0.20f));
        for (int n = 1; n <= MaxPanels; n++)
        {
            int count = n;
            TownLifeKit.DefineProp(RunProp(StyleAcresBoard, n)).Geometry(d => BoardRun(d, count));
            TownLifeKit.DefineProp(RunProp(StyleMeadowsPicket, n)).Geometry(d => PicketRun(d, count));
            TownLifeKit.DefineProp(RunProp(StyleRearSpine, n)).Geometry(d => SpineRun(d, count));
        }
        // gate: two tall posts 1.8 m apart along +Z with a lintel and a little cream keystone; collider-free
        TownLifeKit.DefineProp("fence.gate.board").Geometry(d => GateArch(d, "fence.board.cap", "fence.board.a", "fence.cream"));
        TownLifeKit.DefineProp("fence.gate.picket").Geometry(d => GateArch(d, "fence.white", "fence.white", "fence.board.a"));
    }

    static void Box(TownLifeKit.MeshData d, string mat, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        Vector3 a = new Vector3(x0, y0, z0), b = new Vector3(x1, y0, z0), c = new Vector3(x1, y0, z1), e = new Vector3(x0, y0, z1);
        Vector3 a1 = new Vector3(x0, y1, z0), b1 = new Vector3(x1, y1, z0), c1 = new Vector3(x1, y1, z1), e1 = new Vector3(x0, y1, z1);
        var o = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        d.QuadOut(mat, o, a, b, b1, a1); d.QuadOut(mat, o, e, c, c1, e1); d.QuadOut(mat, o, a, e, e1, a1); d.QuadOut(mat, o, b, c, c1, b1);
        d.QuadOut(mat, o, a1, b1, c1, e1);                                 // no bottom face: it stands on the ground
    }
    static void Pyramid(TownLifeKit.MeshData d, string mat, float half, float y0, float y1, float z)
    {
        Vector3 a = new Vector3(-half, y0, z - half), b = new Vector3(half, y0, z - half), c = new Vector3(half, y0, z + half), e = new Vector3(-half, y0, z + half), top = new Vector3(0f, y1, z);
        var o = new Vector3(0f, y0, z);
        d.TriOut(mat, o, a, b, top); d.TriOut(mat, o, b, c, top); d.TriOut(mat, o, c, e, top); d.TriOut(mat, o, e, a, top);
    }

    static void BoardRun(TownLifeKit.MeshData d, int panels)
    {
        const int boards = 5; float w = PanelLength / boards;
        for (int p = 0; p < panels; p++)
        {
            float z0 = p * PanelLength;
            for (int k = 0; k < boards; k++)
            {
                bool odd = ((k + p * boards) & 1) == 1;
                Box(d, odd ? "fence.board.b" : "fence.board.a", -0.05f, 0.05f, 0f, odd ? 1.36f : 1.40f, z0 + k * w + 0.012f, z0 + (k + 1) * w - 0.012f);
            }
            Box(d, "fence.board.rail", -0.13f, 0.13f, 1.38f, 1.48f, z0, z0 + PanelLength);           // capped top rail
            Box(d, "fence.board.cap", -0.08f, 0.08f, 0.22f, 0.34f, z0, z0 + PanelLength);           // bottom rail, proud both sides
        }
        for (int p = 0; p <= panels; p++)
        {
            float z = p * PanelLength;
            Box(d, "fence.board.cap", -0.14f, 0.14f, 0f, 1.60f, z - 0.14f, z + 0.14f);
            Box(d, "fence.cream", -0.18f, 0.18f, 1.60f, 1.67f, z - 0.18f, z + 0.18f);
            Pyramid(d, "fence.cream", 0.15f, 1.67f, 1.84f, z);
        }
    }

    static void PicketRun(TownLifeKit.MeshData d, int panels)
    {
        const int pickets = 7; float pitch = PanelLength / pickets; const float half = 0.09f, t = 0.035f;
        for (int p = 0; p < panels; p++)
        {
            float z0 = p * PanelLength;
            for (int k = 0; k < pickets; k++)
            {
                float z = z0 + (k + 0.5f) * pitch;
                Box(d, "fence.white", -t, t, 0.06f, 1.02f, z - half, z + half);
                // pointed top
                Vector3 a = new Vector3(-t, 1.02f, z - half), b = new Vector3(-t, 1.02f, z + half), c = new Vector3(-t, 1.16f, z);
                Vector3 a2 = new Vector3(t, 1.02f, z - half), b2 = new Vector3(t, 1.02f, z + half), c2 = new Vector3(t, 1.16f, z);
                var o = new Vector3(0f, 1.05f, z);
                d.TriOut("fence.white", o, a, b, c); d.TriOut("fence.white", o, a2, b2, c2);
                d.QuadOut("fence.white", o, a, a2, c2, c); d.QuadOut("fence.white", o, b, b2, c2, c);
            }
            Box(d, "fence.white", -0.07f, 0.07f, 0.28f, 0.38f, z0, z0 + PanelLength);
            Box(d, "fence.white", -0.07f, 0.07f, 0.78f, 0.88f, z0, z0 + PanelLength);
        }
        for (int p = 0; p <= panels; p++)
        {
            float z = p * PanelLength;
            Box(d, "fence.white", -0.12f, 0.12f, 0f, 1.24f, z - 0.12f, z + 0.12f);
            Box(d, "fence.white", -0.15f, 0.15f, 1.24f, 1.30f, z - 0.15f, z + 0.15f);
            Pyramid(d, "fence.white", 0.13f, 1.30f, 1.44f, z);
        }
    }

    static void SpineRun(TownLifeKit.MeshData d, int panels)
    {
        for (int p = 0; p < panels; p++)
        {
            float z0 = p * PanelLength;
            Box(d, "fence.spine", -0.06f, 0.06f, 0f, 1.72f, z0, z0 + PanelLength);
            Box(d, "fence.spine.dark", -0.13f, 0.13f, 1.70f, 1.79f, z0, z0 + PanelLength);
            Box(d, "fence.spine.dark", -0.085f, 0.085f, 0.80f, 0.90f, z0, z0 + PanelLength);
        }
        for (int p = 0; p <= panels; p++) { float z = p * PanelLength; Box(d, "fence.spine.dark", -0.15f, 0.15f, 0f, 1.92f, z - 0.15f, z + 0.15f); }
    }

    static void GateArch(TownLifeKit.MeshData d, string post, string beam, string accent)
    {
        for (int k = 0; k < 2; k++)
        {
            float z = k * GateGap;
            Box(d, post, -0.13f, 0.13f, 0f, 2.30f, z - 0.13f, z + 0.13f);
            Box(d, accent, -0.16f, 0.16f, 2.30f, 2.36f, z - 0.16f, z + 0.16f);
            Pyramid(d, accent, 0.14f, 2.36f, 2.52f, z);
        }
        Box(d, beam, -0.07f, 0.07f, 2.02f, 2.18f, -0.42f, GateGap + 0.42f);
        Box(d, post, -0.10f, 0.10f, 2.18f, 2.24f, -0.50f, GateGap + 0.50f);
        Box(d, accent, -0.09f, 0.09f, 1.98f, 2.22f, GateGap * 0.5f - 0.13f, GateGap * 0.5f + 0.13f);      // keystone
    }

    /// <summary>Draws ONE run with the kit under <paramref name="parent"/> at base height <paramref name="baseY"/>:
    /// visual only, NO collider (the yard builder owns the collider inventory). Returns the run's GameObject, or null
    /// when nothing was drawn. <paramref name="uniqueName"/> names the object (meshes are shared per style and length).</summary>
    public static GameObject BuildRunVisual(Transform parent, Run run, float baseY, string uniqueName)
    {
        if (run == null || run.Length < 0.3f) return null;
        DefineStyles();
        float len = run.Length; int n = Mathf.Clamp(Mathf.RoundToInt(len / PanelLength), 1, MaxPanels);
        var at = new Vector3(run.a.x, baseY, run.a.z);
        var go = TownLifeKit.Spawn(parent, RunProp(run.style ?? StyleAcresBoard, n), at, TownLifeGeo.Yaw(run.b - run.a), new Vector3(1f, 1f, len / (n * PanelLength)));
        if (go != null && !string.IsNullOrEmpty(uniqueName)) go.name = TownLifeKit.Prefix + uniqueName;
        return go;
    }

    /// <summary>Draws the posts-and-lintel frame of one gate gap (collider-free, one object).</summary>
    public static GameObject BuildGateVisual(Transform parent, Gate gate, float baseY, string uniqueName)
    {
        if (gate == null || gate.Width < 0.5f) return null;
        DefineStyles();
        var at = new Vector3(gate.a.x, baseY, gate.a.z);
        var go = TownLifeKit.Spawn(parent, GateProp(gate.style), at, TownLifeGeo.Yaw(gate.b - gate.a), new Vector3(1f, 1f, gate.Width / GateGap));
        if (go != null && !string.IsNullOrEmpty(uniqueName)) go.name = TownLifeKit.Prefix + uniqueName;
        return go;
    }

    /// <summary>Preview pseudo-module "fences": draws every planned run (with colliders, as the real markers will have)
    /// under ctx.Group("Fences"). Never called by the real pipeline.</summary>
    public static void BuildPreview(TownLifeContext ctx)
    {
        var fences = ctx.fences ?? new Result(); var group = ctx.Group("Fences"); DefineStyles();
        int index = 0; float total = 0f; var byRole = new int[5]; var byStyle = new Dictionary<string, float>();
        foreach (var chain in fences.chains)
        {
            var parent = TownLifeKit.Group(group, "TL_Fences_" + TownLifeKit.Sanitize(chain.id));
            foreach (var run in chain.runs)
            {
                var go = BuildRunVisual(parent, run, TownLifeContext.GrassY, "Fence_" + run.role + "_" + index++);
                if (go == null) continue;
                var size = ColliderSize(run.style); var box = go.AddComponent<BoxCollider>(); float local = run.Length / go.transform.localScale.z;
                box.center = new Vector3(0f, size.y * 0.5f, local * 0.5f); box.size = new Vector3(size.x, size.y, local);
                total += run.Length; byRole[(int)run.role]++;
                byStyle[run.style] = (byStyle.TryGetValue(run.style, out float sum) ? sum : 0f) + run.Length;
            }
            foreach (var gate in chain.gates) BuildGateVisual(parent, gate, TownLifeContext.GrassY, "Gate_" + gate.stopId);
        }
        ctx.Log("fences", fences.chains.Length + " chains, " + fences.runs.Length + " runs, " + fences.gates.Length + " gates, " + total.ToString("0") + " m; side " + byRole[0] + ", rear " + byRole[1] +
            ", wing " + byRole[2] + ", street side " + byRole[3] + ", street rear " + byRole[4] + "; board " + Len(byStyle, StyleAcresBoard) + " m, picket " + Len(byStyle, StyleMeadowsPicket) + " m, spine " + Len(byStyle, StyleRearSpine) + " m");
        foreach (var chain in fences.chains) ctx.Log("fences", "chain " + chain.id + " road " + chain.roadId + " [" + string.Join(",", chain.stopIds) + "] " + chain.runs.Length + " runs, " + chain.gates.Length + " gates");
        foreach (var note in fences.skipped) ctx.Log("fences", "open: " + note);
        if (ctx.options.Flag("fencesDump", false))
        {
            var sb = new StringBuilder(); var dk = new KeepOut(ctx.scene, ctx.plan);
            foreach (var r in fences.runs) sb.Append("DUMP run ").Append(r.role).Append(' ').Append(r.style).Append(' ').Append(r.stopIds[0]).Append(' ').Append(r.a.x.ToString("0.00")).Append(' ').Append(r.a.z.ToString("0.00")).Append(' ').Append(r.b.x.ToString("0.00")).Append(' ').Append(r.b.z.ToString("0.00")).Append('\n');
            foreach (var g in fences.gates) sb.Append("DUMP gate ").Append(g.stopId).Append(' ').Append(g.a.x.ToString("0.00")).Append(' ').Append(g.a.z.ToString("0.00")).Append(' ').Append(g.b.x.ToString("0.00")).Append(' ').Append(g.b.z.ToString("0.00")).Append('\n');
            for (int i = 0; i < ctx.scene.houses.Length; i++)
            {
                var h = ctx.scene.houses[i]; var f = TownLifeGeo.Corners(h.plan.footprint);
                sb.Append("DUMP house ").Append(h.stopId); foreach (var c in f) sb.Append(' ').Append(c.x.ToString("0.00")).Append(' ').Append(c.z.ToString("0.00")); sb.Append('\n');
                if (dk.hasDrive[i]) sb.Append("DUMP drive ").Append(h.stopId).Append(' ').Append(dk.driveA[i].x.ToString("0.00")).Append(' ').Append(dk.driveA[i].z.ToString("0.00")).Append(' ').Append(dk.driveB[i].x.ToString("0.00")).Append(' ').Append(dk.driveB[i].z.ToString("0.00")).Append(' ').Append(dk.driveHalf[i].ToString("0.00")).Append('\n');
                if (h.plan.walk != null && h.plan.walk.Length > 1) sb.Append("DUMP walk ").Append(h.stopId).Append(' ').Append(h.plan.walk[0].x.ToString("0.00")).Append(' ').Append(h.plan.walk[0].z.ToString("0.00")).Append(' ').Append(h.plan.walk[h.plan.walk.Length - 1].x.ToString("0.00")).Append(' ').Append(h.plan.walk[h.plan.walk.Length - 1].z.ToString("0.00")).Append('\n');
                if (i < ctx.plan.parcels.Length) { sb.Append("DUMP parcel ").Append(h.stopId); foreach (var c in ctx.plan.parcels[i].outline) sb.Append(' ').Append(c.x.ToString("0.00")).Append(' ').Append(c.z.ToString("0.00")); sb.Append('\n'); }
                foreach (var item in h.items) { if (item.kind == TownLifeScene.LotItemKind.Driveway || item.kind == TownLifeScene.LotItemKind.Path) continue; var q = TownLifeGeo.Corners(item.rect); sb.Append("DUMP item ").Append(item.kind).Append(item.hasCollider ? "*" : ""); foreach (var c in q) sb.Append(' ').Append(c.x.ToString("0.00")).Append(' ').Append(c.z.ToString("0.00")); sb.Append('\n'); }
            }
            foreach (var e in ctx.plan.edges) { sb.Append("DUMP edge ").Append(e.kind).Append(e.frontage ? "F" : ""); foreach (var c in e.points) sb.Append(' ').Append(c.x.ToString("0.00")).Append(' ').Append(c.z.ToString("0.00")); sb.Append('\n'); }
            foreach (var site in ctx.scene.infill) { var q = TownLifeGeo.Corners(site.quad); sb.Append("DUMP infill ").Append(site.id); foreach (var c in q) sb.Append(' ').Append(c.x.ToString("0.00")).Append(' ').Append(c.z.ToString("0.00")); sb.Append('\n'); }
            ctx.report.Append(sb);
        }
        // planning cost and determinism (the real pipeline plans twice per publish)
        var clock = System.Diagnostics.Stopwatch.StartNew(); var again = Plan(ctx.scene, ctx.plan); long planMs = clock.ElapsedMilliseconds;
        bool same = again.runs.Length == fences.runs.Length && again.gates.Length == fences.gates.Length;
        for (int i = 0; same && i < again.runs.Length; i++) same = again.runs[i].a == fences.runs[i].a && again.runs[i].b == fences.runs[i].b && again.runs[i].style == fences.runs[i].style && again.runs[i].chain == fences.runs[i].chain;
        ctx.Log("fences", "re-plan " + planMs + " ms, " + (same ? "identical" : "DIFFERENT (not deterministic)"));
        if (!same) ctx.Error("fences", "planning is not deterministic");
        clock.Restart();
        var problems = SelfCheck(fences, ctx.scene, ctx.plan);
        ctx.Log("fences", "self check " + clock.ElapsedMilliseconds + " ms");
        ctx.Log("fences", "SelfCheck: " + problems.Count + " violations");
        foreach (var p in problems) ctx.Log("fences", "VIOLATION " + p);
    }
    static string Len(Dictionary<string, float> map, string key) { return (map.TryGetValue(key, out float v) ? v : 0f).ToString("0"); }

    /// <summary>Reserves every run (tag TownLifeTags.Fence, solids forbidden within 0.6 m) and every gate gap approach
    /// (tag TownLifeTags.FenceGap) so content modules keep clear. Called by the builder before the modules.</summary>
    public static void Reserve(Result fences, TownLifeOccupancy occupancy)
    {
        if (fences == null) return;
        MarkAssetsUsed(fences);
        foreach (var r in fences.runs) occupancy.ReserveLine(r.a, r.b, 0.6f, TownLifeTags.Fence, TownLifeOccupancy.Block.Solids);
        foreach (var g in fences.gates)
        {
            // the gap itself plus a walk-through on both sides
            Vector3 n = TownLifeGeo.Right((g.b - g.a).normalized);
            occupancy.ReserveLine(g.Centre - n * 1.6f, g.Centre + n * 1.6f, Mathf.Max(1.2f, g.Width * 0.5f + 0.2f), TownLifeTags.FenceGap, TownLifeOccupancy.Block.Solids);
        }
    }

    /// <summary>Real builds only: touches the shared mesh of every run length and gate in use, so a kit session that
    /// did not draw the fences itself (they are drawn by CoastalYardAuthoring in an earlier session) still lists the
    /// fence assets in TownLifeKit.UsedAssets and no stale-asset sweep deletes them. No objects are created.</summary>
    public static void MarkAssetsUsed(Result fences)
    {
        if (fences == null || !TownLifeKit.Active || TownLifeKit.Transient) return;
        DefineStyles(); var seen = new HashSet<string>();
        foreach (var r in fences.runs)
        {
            string id = RunProp(r.style ?? StyleAcresBoard, Mathf.Clamp(Mathf.RoundToInt(r.Length / PanelLength), 1, MaxPanels));
            if (seen.Add(id)) TownLifeKit.PropBounds(id);
        }
        foreach (var g in fences.gates) if (seen.Add(GateProp(g.style))) TownLifeKit.PropBounds(GateProp(g.style));
    }

    // ================================================================================================== self check

    /// <summary>Every rule the planner promises, re-measured on the finished result. Returns one line per violation
    /// (empty = clean). Yards the planner itself declared open are reported as "(declared open)".</summary>
    public static List<string> SelfCheck(Result result, TownLifeScene scene, TownLifePlan.Result plan)
    {
        var bad = new List<string>();
        if (result == null || scene == null || plan == null) { bad.Add("nothing to check"); return bad; }
        var keep = new KeepOut(scene, plan); var runs = result.runs;
        // ownership
        var seenStop = new Dictionary<string, string>(); var seenRun = new HashSet<Run>();
        foreach (var c in result.chains)
        {
            if (c.stopIds == null || c.stopIds.Length == 0) bad.Add("chain " + c.id + " has no household");
            else foreach (var s in c.stopIds)
            {
                if (seenStop.TryGetValue(s, out var other)) bad.Add(s + " belongs to two chains: " + other + " and " + c.id); else seenStop[s] = c.id;
                var h = scene.House(s); if (h == null) bad.Add("chain " + c.id + " names unknown household " + s); else if (h.plan.roadId != c.roadId) bad.Add("chain " + c.id + " (road " + c.roadId + ") contains " + s + " on road " + h.plan.roadId);
            }
            foreach (var r in c.runs) { if (!seenRun.Add(r)) bad.Add("run owned twice at " + At(r.a)); if (r.chain != c.id) bad.Add("run at " + At(r.a) + " carries chain " + r.chain + " inside " + c.id); }
        }
        foreach (var r in runs) if (!seenRun.Contains(r)) bad.Add("run at " + At(r.a) + " belongs to no chain");
        // per run
        for (int i = 0; i < runs.Length; i++)
        {
            var r = runs[i]; float len = r.Length;
            if (len < MinRun - 0.01f) bad.Add("run shorter than MinRun (" + len.ToString("0.00") + " m) at " + At(r.a));
            int own = -1;
            if (r.role == Role.Wing) for (int h = 0; h < scene.houses.Length; h++) if (scene.houses[h].stopId == r.stopIds[0]) own = h;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f)); string reason = null; Vector3 where = r.a;
            for (int s = 0; s <= steps && reason == null; s++) { where = Vector3.Lerp(r.a, r.b, s / (float)steps); reason = keep.Blocked(where, own, false, 0.06f); }
            if (reason != null) bad.Add(r.role + " run of " + r.stopIds[0] + " violates keep-out: " + reason + " at " + At(where));
            if (r.streetFacing && len >= 3f)
            {
                float d = plan.RoadEdgeDistance((r.a + r.b) * 0.5f);
                if (d < SidewalkWidth + StreetSetback - 0.35f || d > SidewalkWidth + StreetSetback + 0.6f) bad.Add("street-facing run of " + r.stopIds[0] + " sits " + (d - SidewalkWidth).ToString("0.00") + " m behind the sidewalk at " + At((r.a + r.b) * 0.5f));
            }
            for (int j = i + 1; j < runs.Length; j++)
            {
                var q = runs[j];
                if (TownLifeGeo.SegmentsCross(r.a, r.b, q.a, q.b))
                {
                    // a proper crossing well away from both runs' ends
                    LineHit(r.a, (r.b - r.a).normalized, q.a, q.b, out float along, out float u, out Vector3 x);
                    if (Mathf.Min(along, len - along) > 0.3f && Mathf.Min(u, 1f - u) * q.Length > 0.3f) bad.Add("runs cross at " + At(x) + " (" + r.stopIds[0] + " / " + q.stopIds[0] + ")");
                }
                else if (Mathf.Abs(Vector3.Dot((r.b - r.a).normalized, (q.b - q.a).normalized)) > 0.995f)
                {
                    // collinear overlap = the same boundary built twice
                    Vector3 m = (q.a + q.b) * 0.5f;
                    if (TownLifeGeo.DistToSegment(m, r.a, r.b) < 0.12f && TownLifeGeo.DistToSegment(q.a, r.a, r.b) < 0.12f && TownLifeGeo.DistToSegment(q.b, r.a, r.b) < 0.12f) bad.Add("boundary built twice at " + At(m));
                }
            }
            // loose ends
            for (int end = 0; end < 2; end++)
            {
                Vector3 e = end == 0 ? r.a : r.b; if (end == 0 ? r.gatePostA : r.gatePostB) continue;
                bool met = false;
                foreach (var q in runs) if (q != r && TownLifeGeo.DistToSegment(e, q.a, q.b) <= Touch + 0.05f) { met = true; break; }
                if (!met) foreach (var h in scene.houses) if (TownLifeGeo.QuadContains(h.plan.footprint, e, 0.4f)) { met = true; break; }
                if (!met && len < StubLength) bad.Add("dangling " + len.ToString("0.0") + " m stub of " + r.stopIds[0] + " at " + At(e));
            }
        }
        // one gate per fenced yard, exactly GateGap wide where a wing carries it
        foreach (var g in result.gates) if (!g.secondary && g.Width < GateGap - 0.05f) bad.Add("gate of " + g.stopId + " is only " + g.Width.ToString("0.00") + " m wide");
        // enclosure
        for (int pi = 0; pi < plan.parcels.Length && pi < scene.houses.Length; pi++)
        {
            var parcel = plan.parcels[pi]; string stop = parcel.house.stopId;
            string leak = Leak(result, scene, plan, pi);
            bool declared = result.openYards.Contains(stop);
            if (leak != null) bad.Add("yard not enclosed" + (declared ? " (declared open)" : "") + ": " + stop + " " + leak);
            else if (result.GateOf(stop) == null) bad.Add("yard sealed without a gate gap: " + stop);
        }
        return bad;
    }

    static string At(Vector3 p) { return "(" + p.x.ToString("0.0") + ", " + p.z.ToString("0.0") + ")"; }

    /// <summary>Flood fill on the plan grid from the back-yard centroid. Runs, buildings and the gate gap are walls; the
    /// fill may not reach the front yard, nor wander more than 2 m off the parcel. Returns where it got out, or null.</summary>
    static string Leak(Result result, TownLifeScene scene, TownLifePlan.Result plan, int pi)
    {
        var parcel = plan.parcels[pi]; var house = scene.houses[pi]; float cell = plan.cell > 0f ? plan.cell : 0.5f;
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in parcel.outline) { x0 = Mathf.Min(x0, p.x); z0 = Mathf.Min(z0, p.z); x1 = Mathf.Max(x1, p.x); z1 = Mathf.Max(z1, p.z); }
        x0 -= 4f; z0 -= 4f; x1 += 4f; z1 += 4f;
        int w = Mathf.CeilToInt((x1 - x0) / cell), hgt = Mathf.CeilToInt((z1 - z0) / cell); if (w <= 0 || hgt <= 0 || w * hgt > 4000000) return null;
        var wall = new bool[w * hgt];
        void Stamp(Vector3 a, Vector3 b, float radius)
        {
            int ia = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - radius - x0) / cell)), ib = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x) + radius - x0) / cell));
            int ja = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - radius - z0) / cell)), jb = Mathf.Min(hgt - 1, Mathf.FloorToInt((Mathf.Max(a.z, b.z) + radius - z0) / cell));
            for (int j = ja; j <= jb; j++) for (int i = ia; i <= ib; i++)
                if (TownLifeGeo.DistToSegment(new Vector3(x0 + (i + 0.5f) * cell, 0f, z0 + (j + 0.5f) * cell), a, b) <= radius) wall[j * w + i] = true;
        }
        foreach (var r in result.runs) Stamp(r.a, r.b, 0.5f);
        foreach (var g in result.gates) Stamp(g.a, g.b, 0.5f);
        foreach (var h in scene.houses)
        {
            if (TownLifeGeo.Dist(h.plan.origin, parcel.house.origin) > 120f) continue;
            var c = TownLifeGeo.Corners(h.plan.footprint);
            int ia = w, ib = -1, ja = hgt, jb = -1;
            foreach (var p in c) { ia = Mathf.Min(ia, Mathf.FloorToInt((p.x - x0) / cell)); ib = Mathf.Max(ib, Mathf.FloorToInt((p.x - x0) / cell)); ja = Mathf.Min(ja, Mathf.FloorToInt((p.z - z0) / cell)); jb = Mathf.Max(jb, Mathf.FloorToInt((p.z - z0) / cell)); }
            for (int j = Mathf.Max(0, ja - 1); j <= Mathf.Min(hgt - 1, jb + 1); j++) for (int i = Mathf.Max(0, ia - 1); i <= Mathf.Min(w - 1, ib + 1); i++)
                if (TownLifeGeo.QuadContains(h.plan.footprint, new Vector3(x0 + (i + 0.5f) * cell, 0f, z0 + (j + 0.5f) * cell), 0.3f)) wall[j * w + i] = true;
        }
        // start: the back-yard centroid, or the first free cell behind the rear wall
        // start: just behind the middle of the rear wall (always inside the fenced core), else the back-yard centroid
        Vector3 start = house.LotPoint((house.leftExtent + house.rightExtent) * 0.5f, house.rearDepth + 2f); start.y = 0f;
        int si = Mathf.FloorToInt((start.x - x0) / cell), sj = Mathf.FloorToInt((start.z - z0) / cell);
        if (si < 0 || sj < 0 || si >= w || sj >= hgt || wall[sj * w + si] || plan.LabelAt(start) != pi)
        {
            start = parcel.backYardCentroid;
            si = Mathf.FloorToInt((start.x - x0) / cell); sj = Mathf.FloorToInt((start.z - z0) / cell);
            if (si < 0 || sj < 0 || si >= w || sj >= hgt || wall[sj * w + si]) return null;      // no back yard to speak of
        }
        var seen = new bool[w * hgt]; var queue = new Queue<int>(); queue.Enqueue(sj * w + si); seen[sj * w + si] = true;
        var outline = parcel.outline;
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue(); int i = idx % w, j = idx / w; var p = new Vector3(x0 + (i + 0.5f) * cell, 0f, z0 + (j + 0.5f) * cell);
            int label = plan.LabelAt(p);
            if (plan.RoadEdgeDistance(p) < SidewalkWidth + 0.3f) return "leaks to the street at " + At(p);
            if (label != pi)
            {
                float d = float.MaxValue; for (int k = 0; k < outline.Length; k++) d = Mathf.Min(d, TownLifeGeo.DistToSegment(p, outline[k], outline[(k + 1) % outline.Length]));
                if (d > 2f && !TownLifeGeo.PolygonContains(outline, p)) return "leaks off the lot at " + At(p) + (label >= 0 ? " into " + plan.parcels[label].house.stopId : " (label " + label + ")");
            }
            if (i > 0) Visit(idx - 1); if (i < w - 1) Visit(idx + 1); if (j > 0) Visit(idx - w); if (j < hgt - 1) Visit(idx + w);
        }
        return null;
        void Visit(int n) { if (!seen[n] && !wall[n]) { seen[n] = true; queue.Enqueue(n); } }
    }
}

// Owner: streets agent. Contract: design/CONTRACT.md.
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>Street trees, verge and corner planting, hedges in front of street-facing boundaries (DESIGN section 8 'Streets').
/// Group: ctx.Group("Streets") with one sub-group per road (TL_Streets_&lt;roadId&gt;) plus TL_Streets_Corners.
/// Order inside the module: hedges in front of street-facing fences, corner beds / name posts, tree avenues, verge strips.
/// Everything is planned from ctx.scene / ctx.plan / ctx.fences / ctx.occupancy; no physics, RNG from ctx.Rng only.
/// Tuning switches (preview options): streets.spacing (scale on every spacing, default 1), streets.parkSide (plant the
/// free 2.4 m band the park leaves along its four sidewalks, default 1), streets.signs, streets.strips, streets.hedges.</summary>
public static class TownLifeStreets
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "streets";

    const float Sidewalk = 3f;            // sidewalk band beyond the asphalt edge
    const float LineA = 1.2f;             // tree line behind the sidewalk back edge
    const float LineB = 3.6f;             // fallback line (behind a street-facing fence / a Meadows front picket)
    const float TrunkTest = 0.7f;         // free radius tested for a trunk (the reservation is 1.2)
    const float Step = 0.5f;              // sampling step along a street
    const float JunctionClear = 12.7f;    // 12 m sight circle + trunk test radius
    const string TagTree = "tree.street", TagHedge = "hedge.street", TagBed = "bed.street", TagFeature = "feature.street";

    sealed class Species
    {
        public string[] props;            // cycled by index; props[i % n]
        public float spacing, scaleMin, scaleMax;
        public int everyOther;            // > 0: every n-th tree takes `other`
        public string other; public float otherScaleMin, otherScaleMax;
    }

    sealed class Node { public Vector3 p; public List<TownLifeScene.RoadInfo> roads = new List<TownLifeScene.RoadInfo>(); public List<Vector3> arms = new List<Vector3>(); public List<TownLifeScene.RoadInfo> armRoads = new List<TownLifeScene.RoadInfo>(); }

    struct ParcelBox { public Vector3[] outline; public float minX, maxX, minZ, maxZ; }

    sealed class State
    {
        public TownLifeContext ctx; public TownLifeScene scene; public TownLifeOccupancy occ; public Transform root, corners;
        public List<Node> nodes = new List<Node>();
        public Dictionary<string, Transform> groups = new Dictionary<string, Transform>();
        public Rect parkRect; public bool hasPark, parkSide;
        /// <summary>Every parcel outline with its XZ bounds: the Yards module paints a lawn on each at LawnY, and the verge must not overlap it.</summary>
        public readonly List<ParcelBox> parcels = new List<ParcelBox>();
        public float spacingScale;
        public int trees, treesB, nudged, hedges, beds, seats, signs, strips, gaps; public float hedgeMetres, longestGap;
        public List<string> gapList = new List<string>();
        public SortedDictionary<string, int> why = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
        public void Why(string k) { why.TryGetValue(k, out int n); why[k] = n + 1; }
        public Transform RoadGroup(string id)
        {
            if (!groups.TryGetValue(id, out var t)) { t = TownLifeKit.Group(root, "TL_Streets_" + TownLifeKit.Sanitize(id)); groups[id] = t; }
            return t;
        }
    }

    /// <summary>Builds this module into ctx. Deterministic, physics-free, reads and feeds ctx.occupancy.</summary>
    public static void Build(TownLifeContext ctx)
    {
        var s = new State { ctx = ctx, scene = ctx.scene, occ = ctx.occupancy };
        s.root = ctx.Group("Streets");
        s.spacingScale = Mathf.Clamp(ctx.options.Number("streets.spacing", 1f), 0.5f, 2f);
        s.parkSide = ctx.options.Flag("streets.parkSide", true);
        if (s.scene.park != null) { s.hasPark = true; s.parkRect = s.scene.park.block; }
        if (ctx.plan != null && ctx.plan.parcels != null)
            foreach (var par in ctx.plan.parcels)
            {
                if (par == null || par.outline == null || par.outline.Length < 3) continue;
                var b = new ParcelBox { outline = par.outline, minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue };
                foreach (var v in par.outline) { b.minX = Mathf.Min(b.minX, v.x); b.maxX = Mathf.Max(b.maxX, v.x); b.minZ = Mathf.Min(b.minZ, v.z); b.maxZ = Mathf.Max(b.maxZ, v.z); }
                s.parcels.Add(b);
            }

        DefineProps();
        FindNodes(s);
        if (ctx.options.Flag("streets.hedges", true)) Hedges(s);
        s.corners = TownLifeKit.Group(s.root, "TL_Streets_Corners");
        if (ctx.options.Flag("streets.corners", true)) Corners(s);

        // main streets first (they win the corners), then the service lanes and court rings
        var roads = new List<TownLifeScene.RoadInfo>(s.scene.roads);
        roads.Sort((a, b) => a.service != b.service ? (a.service ? 1 : -1) : string.CompareOrdinal(a.id, b.id));
        foreach (var road in roads)
        {
            if (road.bridge || road.points == null || road.points.Length < 2) continue;
            if (!ctx.options.District(road.districtId)) continue;
            foreach (int side in new[] { 1, -1 })
            {
                if (road.id == "depot" && side < 0) continue;                    // west side = depot pads: the Edges module plants it
                var court = s.scene.Court(road.id);
                if (court != null && !OuterSide(road, court, side)) continue;    // the island belongs to Meadows
                Avenue(s, road, side);
            }
        }

        ctx.Log(Name, s.trees + " street trees (" + s.treesB + " on the back line, " + s.nudged + " nudged into gaps; " + s.gaps + " gaps over 20 m remain, longest "
            + s.longestGap.ToString("0") + " m), " + s.hedges + " hedge runs (" + s.hedgeMetres.ToString("0") + " m), " + s.beds + " corner beds, " + s.seats + " corner seats, "
            + s.signs + " name posts, " + s.strips + " verge strips, " + s.nodes.Count + " junctions; corners skipped: " + string.Join(", ", SkipList(s)) + "; gaps: " + string.Join(" ", s.gapList));
    }

    static List<string> SkipList(State s) { var l = new List<string>(); foreach (var kv in s.why) l.Add(kv.Key + " " + kv.Value); return l; }

    // ================================================================================================= props

    static string P(string id) { return TownLifeKit.HasProp(id) ? id : (id.StartsWith("kit.tree", System.StringComparison.Ordinal) ? "kit.tree.round" : "kit.shrub.round"); }

    static void DefineTree(string id, string kitId, float trunkRadius, float trunkHeight, float pit)
    {
        if (TownLifeKit.HasProp(id)) return;
        // the stock tree standing in a mulched tree pit with a pale stone edging (grass is at -0.08 = prop y 0)
        TownLifeKit.DefineProp(id)
            .Cyl("stone.light", Vector3.zero, pit, 0.12f, 12)
            .Cyl("mulch", new Vector3(0f, 0.02f, 0f), pit - 0.16f, 0.12f, 12)
            .Add(P(kitId), Vector3.zero)
            .ColliderTrunk(trunkRadius, trunkHeight);
    }

    static void DefineProps()
    {
        TownLifeKit.Define("streets.verge", new Color(0.29f, 0.53f, 0.22f), 0.02f);
        TownLifeKit.Define("streets.blade", new Color(0.13f, 0.50f, 0.33f), 0.25f);

        DefineTree("streets.tree.elm", "kit.tree.elm.l", 0.38f, 3.4f, 1.05f);
        DefineTree("streets.tree.maple", "kit.tree.maple.l", 0.36f, 3.2f, 1.0f);
        DefineTree("streets.tree.oak", "kit.tree.oak.l", 0.58f, 3f, 1.1f);
        DefineTree("streets.tree.birch", "kit.tree.birch.l", 0.28f, 3.4f, 0.9f);
        DefineTree("streets.tree.blossom", "kit.tree.blossom.m", 0.28f, 2.4f, 0.8f);
        DefineTree("streets.tree.round", "kit.tree.round.l", 0.36f, 3.2f, 1.0f);
        DefineTree("streets.tree.pickle", "kit.tree.pickle.l", 0.3f, 3f, 0.95f);
        DefineTree("streets.tree.blossom.s", "kit.tree.blossom.s", 0.2f, 1.6f, 0.6f);

        // corner bed: a round, stone-edged bed, everything under 0.6 m (sight lines), no collider
        string[] cols = { "red", "yellow", "pink", "purple", "white", "mixed" };
        for (int v = 0; v < 3; v++)
        {
            string id = "streets.cornerbed." + v; if (TownLifeKit.HasProp(id)) continue;
            var r = TownLifeKit.DefineProp(id).Cyl("stone.light", Vector3.zero, 2.0f, 0.2f, 14).Cyl("soil", new Vector3(0f, 0.02f, 0f), 1.8f, 0.2f, 14);
            Planting(r, cols, v, 0f);
            r.NoShadows();
        }
        // corner seat: the same bed opened at the front for a bench with two tubs (bench faces +Z)
        if (!TownLifeKit.HasProp("streets.cornerseat"))
        {
            var r = TownLifeKit.DefineProp("streets.cornerseat").Cyl("stone.light", Vector3.zero, 2.0f, 0.2f, 14).Cyl("soil", new Vector3(0f, 0.02f, 0f), 1.8f, 0.2f, 14)
                .Box("paving.warm", new Vector3(0f, 0.12f, 1.0f), new Vector3(2.8f, 0.22f, 1.7f));
            for (int i = 0; i < 5; i++)
            {
                float a = (200f + i * 35f) * Mathf.Deg2Rad;
                r.Add(P("kit.flowers." + cols[(i * 2) % cols.Length]), new Vector3(Mathf.Cos(a) * 1.25f, 0.2f, Mathf.Sin(a) * 1.25f), i * 70f, 0.95f);
            }
            r.Add(P("kit.shrub.round.s"), new Vector3(0f, 0.2f, -0.55f), 20f, 0.8f).Add(P("kit.shrub.round.s"), new Vector3(-0.95f, 0.2f, -0.2f), 140f, 0.65f).Add(P("kit.shrub.round.s"), new Vector3(0.95f, 0.2f, -0.2f), 260f, 0.65f);
            r.Add(P("kit.bench.park"), new Vector3(0f, 0.23f, 0.85f), 0f, 1f).Add(P("kit.planter.tub"), new Vector3(-1.75f, 0f, 1.75f), 0f, 1f).Add(P("kit.planter.tub"), new Vector3(1.75f, 0f, 1.75f), 90f, 1f);
            r.ColliderBox(new Vector3(0f, 0.55f, 0.8f), new Vector3(1.8f, 0.9f, 0.6f));
        }
        // name post: bed + a white post with two crossed green blades (labels are added per junction)
        if (!TownLifeKit.HasProp("streets.namepost"))
        {
            var r = TownLifeKit.DefineProp("streets.namepost").Cyl("stone.light", Vector3.zero, 2.0f, 0.2f, 14).Cyl("soil", new Vector3(0f, 0.02f, 0f), 1.8f, 0.2f, 14);
            Planting(r, cols, 1, 0.75f);
            r.Cyl("stone.light", new Vector3(0f, 0.2f, 0f), 0.22f, 0.16f, 8)
             .Cyl("paint.white", new Vector3(0f, 0.2f, 0f), 0.07f, 3.15f, 8)
             .Sphere("paint.yellow", new Vector3(0f, 3.42f, 0f), Vector3.one * 0.13f, 8, 5)
             .Box("streets.blade", new Vector3(BladeMid, BladeA, 0f), new Vector3(BladeLen, BladeH, 0.05f))
             .Box("paint.white", new Vector3(BladeMid, BladeA, 0f), new Vector3(BladeLen + 0.08f, BladeH + 0.08f, 0.03f))
             .Box("streets.blade", new Vector3(0f, BladeB, BladeMid), new Vector3(0.05f, BladeH, BladeLen))
             .Box("paint.white", new Vector3(0f, BladeB, BladeMid), new Vector3(0.03f, BladeH + 0.08f, BladeLen + 0.08f));
        }
    }
    const float BladeA = 3.12f, BladeB = 2.74f, BladeLen = 1.9f, BladeH = 0.3f, BladeMid = 0.07f + 1.9f * 0.5f;

    static void Planting(TownLifeKit.Recipe r, string[] cols, int v, float hole)
    {
        for (int i = 0; i < 7; i++)
        {
            float a = (i * 360f / 7f + v * 23f) * Mathf.Deg2Rad;
            r.Add(P("kit.flowers." + cols[(i + v * 2) % cols.Length]), new Vector3(Mathf.Cos(a) * 1.22f, 0.2f, Mathf.Sin(a) * 1.22f), i * 70f, 0.95f);
        }
        if (hole <= 0f) r.Add(P("kit.shrub.round.s"), new Vector3(0f, 0.2f, 0f), v * 80f, 0.8f);
        for (int i = 0; i < 3; i++)
        {
            float a = (i * 120f + 40f + v * 31f) * Mathf.Deg2Rad; float rad = 0.55f + hole * 0.25f;
            r.Add(P(i == 1 ? "kit.flowers.mixed" : "kit.shrub.round.s"), new Vector3(Mathf.Cos(a) * rad, 0.2f, Mathf.Sin(a) * rad), i * 110f, i == 1 ? 0.9f : 0.55f);
        }
    }

    static Species SpeciesFor(TownLifeScene.RoadInfo road)
    {
        if (road.service) return new Species { props = new[] { "streets.tree.blossom.s" }, spacing = 16f, scaleMin = 1.45f, scaleMax = 1.7f };
        switch (road.id)
        {
            case "elm": return new Species { props = new[] { "streets.tree.elm" }, spacing = 12.5f, scaleMin = 1.02f, scaleMax = 1.2f };
            case "maple": return new Species { props = new[] { "streets.tree.maple" }, spacing = 12f, scaleMin = 1.05f, scaleMax = 1.22f };
            case "oak": return new Species { props = new[] { "streets.tree.oak" }, spacing = 13.5f, scaleMin = 1.1f, scaleMax = 1.28f };
            case "birch": return new Species { props = new[] { "streets.tree.birch" }, spacing = 10.5f, scaleMin = 1.08f, scaleMax = 1.28f };
            case "home": return new Species { props = new[] { "streets.tree.blossom" }, spacing = 11f, scaleMin = 1.3f, scaleMax = 1.5f, everyOther = 4, other = "streets.tree.round", otherScaleMin = 1f, otherScaleMax = 1.15f };
            case "park": return new Species { props = new[] { "streets.tree.pickle" }, spacing = 9.5f, scaleMin = 1.02f, scaleMax = 1.2f };
            case "meadow-loop": return new Species { props = new[] { "streets.tree.blossom" }, spacing = 12f, scaleMin = 1.3f, scaleMax = 1.5f, everyOther = 2, other = "streets.tree.round", otherScaleMin = 1f, otherScaleMax = 1.15f };
            default: return new Species { props = new[] { "streets.tree.round" }, spacing = 12.5f, scaleMin = 1f, scaleMax = 1.18f };
        }
    }

    // ================================================================================================= junctions

    static void FindNodes(State s)
    {
        var roads = s.scene.roads;
        for (int i = 0; i < roads.Length; i++)
            for (int j = 0; j < roads.Length; j++)
            {
                if (i == j) continue;
                foreach (var raw in roads[i].points)
                {
                    if (TownLifeGeo.Project(roads[j].points, raw, out _, out _) > 0.5f) continue;
                    var p = TownLifeGeo.Flat(raw); Node node = null;
                    foreach (var n in s.nodes) if (TownLifeGeo.Dist(n.p, p) < 6f) { node = n; break; }
                    if (node == null) { node = new Node { p = p }; s.nodes.Add(node); }
                    if (!node.roads.Contains(roads[i])) node.roads.Add(roads[i]);
                    if (!node.roads.Contains(roads[j])) node.roads.Add(roads[j]);
                }
            }
        foreach (var n in s.nodes)
        {
            n.roads.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            foreach (var r in n.roads)
            {
                if (TownLifeGeo.Project(r.points, n.p, out float st, out _) > 1f) continue;
                var cands = new List<float>();
                if (st > 3f) cands.Add(st - 10f); else if (r.loop) cands.Add(r.length - 10f);
                if (st < r.length - 3f) cands.Add(st + 10f); else if (r.loop) cands.Add(10f);
                foreach (float c in cands)
                {
                    var q = TownLifeGeo.PointAt(r.points, Mathf.Clamp(c, 0f, r.length), out _);
                    var d = TownLifeGeo.Flat(q - n.p); if (d.magnitude < 3f) continue; d.Normalize();
                    bool dup = false; foreach (var a in n.arms) if (Vector3.Dot(a, d) > 0.94f) { dup = true; break; }
                    if (!dup) { n.arms.Add(d); n.armRoads.Add(r); }
                }
            }
        }
    }

    static float NodeDistance(State s, Vector3 p)
    {
        float best = float.MaxValue;
        foreach (var n in s.nodes) { float d = TownLifeGeo.Dist(n.p, p); if (d < best) best = d; }
        return best;
    }

    static bool InPark(State s, Vector3 p, float margin) { return s.hasPark && TownLifeGeo.RectContains(s.parkRect, p, margin); }

    // ================================================================================================= hedges

    static void Hedges(State s)
    {
        if (s.ctx.fences == null || s.ctx.fences.runs == null) return;
        // chain the straight runs into polylines (runs never bend; consecutive runs share an end point)
        var lines = new List<List<Vector3>>(); var roles = new List<TownLifeFences.Role>(); var owners = new List<string>();
        foreach (var run in s.ctx.fences.runs)
        {
            if (!run.streetFacing || run.Length < 0.5f) continue;
            bool joined = false;
            for (int i = 0; i < lines.Count && !joined; i++)
            {
                if (roles[i] != run.role) continue;
                var l = lines[i];
                if (TownLifeGeo.Dist(l[l.Count - 1], run.a) < 0.05f) { l.Add(TownLifeGeo.Flat(run.b)); joined = true; }
                else if (TownLifeGeo.Dist(l[l.Count - 1], run.b) < 0.05f) { l.Add(TownLifeGeo.Flat(run.a)); joined = true; }
                else if (TownLifeGeo.Dist(l[0], run.b) < 0.05f) { l.Insert(0, TownLifeGeo.Flat(run.a)); joined = true; }
                else if (TownLifeGeo.Dist(l[0], run.a) < 0.05f) { l.Insert(0, TownLifeGeo.Flat(run.b)); joined = true; }
            }
            if (joined) continue;
            lines.Add(new List<Vector3> { TownLifeGeo.Flat(run.a), TownLifeGeo.Flat(run.b) }); roles.Add(run.role);
            owners.Add(run.stopIds != null && run.stopIds.Length > 0 ? run.stopIds[0] : "x");
        }
        var used = new Dictionary<string, int>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // which side is the street? probe both sides of the first piece
            var mid = (line[0] + line[1]) * 0.5f; var n = TownLifeGeo.Right((line[1] - line[0]).normalized);
            float dPlus = s.scene.RoadEdgeDistance(mid + n, out var roadPlus), dMinus = s.scene.RoadEdgeDistance(mid - n, out var roadMinus);
            float sign = dPlus < dMinus ? 1f : -1f; var road = dPlus < dMinus ? roadPlus : roadMinus;
            if (road == null || !s.ctx.options.District(s.scene.DistrictAt(mid) ?? road.districtId)) continue;
            bool rear = roles[i] == TownLifeFences.Role.StreetRear;
            string panel = P(rear && s.ctx.options.Flag("streets.tallHedge", false) ? "kit.panel.hedge.tall" : "kit.panel.hedge.mid");
            var hedge = TownLifeGeo.Offset(line, sign * 0.56f);          // hedge centre line: its back touches the boards
            var probe = TownLifeGeo.Offset(line, sign * 0.80f);          // tested between the sidewalk edge and the fence reservation
            // walk the pieces, keep the free stretches
            var current = new List<Vector3>();
            for (int k = 1; k < hedge.Length; k++)
            {
                float len = TownLifeGeo.Dist(hedge[k - 1], hedge[k]); int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f));
                for (int t = 0; t <= steps; t++)
                {
                    float u = t / (float)steps; var hp = Vector3.Lerp(hedge[k - 1], hedge[k], u); var pp = Vector3.Lerp(probe[k - 1], probe[k], u);
                    bool free = s.occ.Free(pp, 0.12f) && s.scene.RoadEdgeDistance(hp, out _) >= Sidewalk + 0.38f && !InPark(s, hp, 0f);
                    if (free)
                    {
                        if (current.Count >= 2 && t > 1 && t <= steps) current[current.Count - 1] = hp;    // extend the straight piece
                        else current.Add(hp);
                    }
                    else { Flush(s, current, panel, rear, road, owners[i], used); current.Clear(); }
                }
            }
            Flush(s, current, panel, rear, road, owners[i], used);
        }
    }

    static void Flush(State s, List<Vector3> pts, string panel, bool rear, TownLifeScene.RoadInfo road, string owner, Dictionary<string, int> used)
    {
        if (pts.Count < 2) return;
        float len = TownLifeGeo.PolylineLength(pts); if (len < 2f) return;
        used.TryGetValue(owner, out int n); used[owner] = n + 1;
        TownLifeKit.Run(s.RoadGroup(road.id), "streets_hedge_" + TownLifeKit.Sanitize(owner) + "_" + n, new List<Vector3>(pts),
            new TownLifeKit.RunSpec { panel = panel, panelLength = 2f, castShadows = rear, y = TownLifeContext.GrassY });
        s.occ.ReservePolyline(pts, 0.42f, TagHedge, TownLifeOccupancy.Block.Solids);
        s.hedges++; s.hedgeMetres += len;
    }

    // ================================================================================================= corners

    static void Corners(State s)
    {
        bool signs = s.ctx.options.Flag("streets.signs", true);
        int ni = 0;
        var nodes = new List<Node>(s.nodes); nodes.Sort((a, b) => a.p.x != b.p.x ? a.p.x.CompareTo(b.p.x) : a.p.z.CompareTo(b.p.z));
        foreach (var node in nodes)
        {
            ni++;
            if (node.arms.Count < 2) continue;
            // arms by angle
            var order = new List<int>(); for (int i = 0; i < node.arms.Count; i++) order.Add(i);
            order.Sort((a, b) => TownLifeGeo.Yaw(node.arms[a]).CompareTo(TownLifeGeo.Yaw(node.arms[b])));
            bool signDone = !signs; int main = 0; foreach (var r in node.roads) if (!r.bridge) main++;
            var rng = s.ctx.Rng("streets/corner/" + ni);
            for (int k = 0; k < order.Count; k++)
            {
                int ia = order[k], ib = order[(k + 1) % order.Count];
                float ya = TownLifeGeo.Yaw(node.arms[ia]), yb = TownLifeGeo.Yaw(node.arms[ib]);
                float gap = Mathf.Repeat(yb - ya, 360f); if (order.Count == 2 && k == 1) gap = 360f - Mathf.Repeat(ya - yb, 360f);
                if (gap < 55f || gap > 150f) { s.Why("angle"); continue; }
                var bis = TownLifeGeo.Dir(ya + gap * 0.5f);
                float d = (node.armRoads[ia].halfWidth + Sidewalk + 2.6f) / Mathf.Sin(gap * 0.5f * Mathf.Deg2Rad);
                d = Mathf.Max(d, 14.3f);
                var c = node.p + bis * d;
                string district = s.scene.DistrictAt(c); if (district == null || !s.ctx.options.District(district)) { s.Why("district"); continue; }
                if (InPark(s, c, 1.5f)) { s.Why("park"); continue; }
                if (s.occ.Within(c, 2.15f, TownLifeTags.Fence)) { s.Why("fenced corner"); continue; }     // the corner belongs to a fenced yard: its hedge is the corner planting
                if (!s.occ.Free(c, 2.15f))
                {
                    // slide outwards along the bisector, then give up
                    bool ok = false;
                    for (float e = 1f; e <= 4f && !ok; e += 1f) { var q = node.p + bis * (d + e); if (s.occ.Free(q, 2.15f)) { c = q; ok = true; } }
                    if (!ok) { s.Why(s.occ.Blocker(c, 2.15f) ?? "?"); continue; }
                }
                if (!s.scene.IsFlat(c, 2.2f) || s.occ.Within(c, 2.6f, "tree") || s.occ.Within(c, 2.3f, "bed") || s.occ.Within(c, 2.3f, "paving")) { s.Why("flat/tree/bed"); continue; }
                int label = s.ctx.plan != null ? s.ctx.plan.LabelAt(c) : TownLifePlan.Common;
                bool common = label == TownLifePlan.Common;
                float faceYaw = TownLifeGeo.Yaw(-bis);                       // towards the junction
                c.y = s.scene.GroundY(c);
                GameObject go;
                if (!signDone && main >= 2 && !node.armRoads[ia].bridge && !node.armRoads[ib].bridge && node.armRoads[ia] != node.armRoads[ib])
                {
                    // blade A (local X) runs along road A, so its text is read by people travelling on road B and vice versa
                    float yaw = TownLifeGeo.Yaw(node.arms[ia]) - 90f;
                    go = TownLifeKit.Spawn(s.corners, "streets.namepost", c, yaw);
                    NameLabels(go.transform, node.armRoads[ia].name, node.armRoads[ib].name);
                    signDone = true; s.signs++;
                    s.occ.ReserveCircle(c, 0.5f, TagFeature, TownLifeOccupancy.Block.Solids);
                }
                else if (common && rng.Next(3) != 0)
                {
                    go = TownLifeKit.Spawn(s.corners, "streets.cornerseat", c, faceYaw);
                    s.occ.ReserveCircle(c, 2.3f, TagFeature, TownLifeOccupancy.Block.Solids); s.seats++;
                }
                else
                {
                    go = TownLifeKit.Spawn(s.corners, "streets.cornerbed." + rng.Next(3), c, rng.Next(360));
                    s.beds++;
                }
                s.occ.ReserveCircle(c, 2.0f, TagBed, TownLifeOccupancy.Block.Marker);
                s.occ.ReserveCircle(c, 1.5f, TagBed + ".core", TownLifeOccupancy.Block.Solids);     // keeps trunks and yard props out of the bed
            }
        }
    }

    static void NameLabels(Transform post, string nameA, string nameB)
    {
        var size = new Vector2(BladeLen * 0.92f, BladeH * 0.8f); var white = Color.white;
        string a = (nameA ?? "").ToUpperInvariant(), b = (nameB ?? "").ToUpperInvariant();
        TownLifeKit.Label(post, "NameA", a, new Vector3(BladeMid, BladeA, -0.032f), Quaternion.identity, size, white);
        TownLifeKit.Label(post, "NameA_Back", a, new Vector3(BladeMid, BladeA, 0.032f), Quaternion.Euler(0f, 180f, 0f), size, white);
        TownLifeKit.Label(post, "NameB", b, new Vector3(-0.032f, BladeB, BladeMid), Quaternion.Euler(0f, 90f, 0f), size, white);
        TownLifeKit.Label(post, "NameB_Back", b, new Vector3(0.032f, BladeB, BladeMid), Quaternion.Euler(0f, 270f, 0f), size, white);
    }

    // ================================================================================================= avenues

    static bool OuterSide(TownLifeScene.RoadInfo road, TownLifeScene.CourtInfo court, int side)
    {
        var c = TownLifeGeo.PointAt(road.points, road.length * 0.37f, out var t);
        var p = c + TownLifeGeo.Right(t) * side * 6f;
        return TownLifeGeo.Dist(p, court.centre) > TownLifeGeo.Dist(c, court.centre);
    }

    static Vector3 LinePoint(TownLifeScene.RoadInfo road, int side, float station, float behindSidewalk)
    {
        var c = TownLifeGeo.PointAt(road.points, station, out var t);
        return c + TownLifeGeo.Right(t) * side * (road.halfWidth + Sidewalk + behindSidewalk);
    }

    /// <summary>May a trunk stand here? Occupancy plus the things occupancy does not know: crown room next to buildings,
    /// neighbouring trees, other roads' sidewalks on inner bends, sloping banks, the park's own ground.</summary>
    static bool TrunkFree(State s, Vector3 p, bool lineB)
    {
        if (!s.occ.Free(p, TrunkTest)) return false;
        if (s.occ.Within(p, 4.5f, "tree")) return false;
        if (s.occ.Within(p, 5f, TagBed)) return false;
        if (s.occ.Within(p, 2.6f, "building")) return false;
        if (s.scene.RoadEdgeDistance(p, out _) < Sidewalk + 0.9f) return false;
        if (!s.scene.IsFlat(p, 1.2f)) return false;
        if (InPark(s, p, 0f) && (!s.parkSide || lineB)) return false;
        return true;
    }

    static void Avenue(State s, TownLifeScene.RoadInfo road, int side)
    {
        var sp = SpeciesFor(road); float spacing = sp.spacing * s.spacingScale;
        int n = Mathf.FloorToInt(road.length / Step) + 1;
        var hard = new bool[n]; var freeA = new bool[n]; var freeB = new bool[n];
        for (int i = 0; i < n; i++)
        {
            float st = i * Step; var a = LinePoint(road, side, st, LineA);
            hard[i] = !TownLifeGeo.RectContains(s.occ.area, a) || NodeDistance(s, a) < JunctionClear || s.scene.RoadEdgeDistance(a, out _) < Sidewalk + 0.6f;
            if (hard[i]) continue;
            freeA[i] = TrunkFree(s, a, false);
            if (!freeA[i]) freeB[i] = TrunkFree(s, LinePoint(road, side, st, LineB), true);
        }
        var group = s.RoadGroup(road.id); string key = "streets/" + road.id + "/" + side; int serial = 0;
        var placed = new List<float>();                                   // stations, kept sorted

        // spans between junctions
        int i0 = 0;
        while (i0 < n)
        {
            while (i0 < n && hard[i0]) i0++;
            if (i0 >= n) break;
            int i1 = i0; while (i1 + 1 < n && !hard[i1 + 1]) i1++;
            float a = i0 * Step, b = i1 * Step, len = b - a;
            if (len >= 5f)
            {
                var span = new List<float>();
                float margin = Mathf.Min(2.5f, len * 0.25f);
                int count = Mathf.Max(1, Mathf.RoundToInt((len - 2f * margin) / spacing) + 1);
                float step = count > 1 ? (len - 2f * margin) / (count - 1) : 0f;
                float window = count > 1 ? Mathf.Min(step * 0.5f - 0.5f, 5.5f) : len * 0.5f;
                for (int k = 0; k < count; k++)
                {
                    float ideal = count > 1 ? a + margin + k * step : a + len * 0.5f;
                    float lo = Mathf.Max(a, ideal - window), hi = Mathf.Min(b, ideal + window);
                    if (span.Count > 0) lo = Mathf.Max(lo, span[span.Count - 1] + Mathf.Min(7f, spacing * 0.6f));
                    if (TryPlace(s, road, side, sp, group, key, ref serial, ideal, lo, hi, freeA, freeB, false)) span.Add(lastStation);
                }
                // rhythm repair: nudge a tree into every gap over 20 m (or over 13 m at a span end)
                for (int guard = 0; guard < 40; guard++)
                {
                    span.Sort(); bool any = false;
                    for (int k = 0; k <= span.Count && !any; k++)
                    {
                        float g0 = k == 0 ? a : span[k - 1], g1 = k == span.Count ? b : span[k];
                        bool endGap = k == 0 || k == span.Count; float limit = endGap ? 13f : 20f;
                        if (g1 - g0 <= limit) continue;
                        float keep = Mathf.Min(6.5f, spacing * 0.55f);
                        float lo = k == 0 ? g0 : g0 + keep, hi = k == span.Count ? g1 : g1 - keep;
                        if (hi <= lo) continue;
                        float target = endGap && span.Count > 0 ? (k == 0 ? Mathf.Max(lo, g1 - spacing) : Mathf.Min(hi, g0 + spacing)) : (g0 + g1) * 0.5f;
                        if (TryPlace(s, road, side, sp, group, key, ref serial, target, lo, hi, freeA, freeB, true)) { span.Add(lastStation); any = true; }
                    }
                    if (!any) break;
                }
                span.Sort();
                for (int k = 1; k < span.Count; k++) { float g = span[k] - span[k - 1]; if (g > 20f) { s.gaps++; if (g > s.longestGap) s.longestGap = g; var gp = LinePoint(road, side, (span[k] + span[k - 1]) * 0.5f, LineA); s.gapList.Add(road.id + (side > 0 ? "+" : "-") + "@" + gp.x.ToString("0") + "," + gp.z.ToString("0") + ":" + g.ToString("0")); } }
                placed.AddRange(span);
            }
            i0 = i1 + 1;
        }

        if (s.ctx.options.Flag("streets.strips", true)) VergeStrip(s, road, side, hard, group);
    }

    static float lastStation;

    static bool TryPlace(State s, TownLifeScene.RoadInfo road, int side, Species sp, Transform group, string key, ref int serial,
        float target, float lo, float hi, bool[] freeA, bool[] freeB, bool nudge)
    {
        int it = Mathf.RoundToInt(target / Step), ilo = Mathf.CeilToInt(lo / Step), ihi = Mathf.FloorToInt(hi / Step);
        if (ihi < ilo) return false;
        it = Mathf.Clamp(it, ilo, ihi);
        for (int pass = 0; pass < 2; pass++)
        {
            var mask = pass == 0 ? freeA : freeB; float behind = pass == 0 ? LineA : LineB;
            for (int off = 0; off <= ihi - ilo; off++)
            {
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    int i = it + off * sgn; if (i < ilo || i > ihi || i < 0 || i >= mask.Length || !mask[i]) continue;
                    var p = LinePoint(road, side, i * Step, behind);
                    if (!TrunkFree(s, p, pass == 1)) { mask[i] = false; continue; }      // something was placed since the mask was taken
                    var rng = s.ctx.Rng(key + "/" + serial);
                    bool other = sp.everyOther > 0 && serial % sp.everyOther == sp.everyOther - 1;
                    string prop = other ? sp.other : sp.props[serial % sp.props.Length];
                    float scale = other ? TownLifeContext.Range(rng, sp.otherScaleMin, sp.otherScaleMax) : TownLifeContext.Range(rng, sp.scaleMin, sp.scaleMax);
                    p.y = s.scene.GroundY(p);
                    TownLifeKit.Spawn(group, prop, p, rng.Next(360), scale);
                    s.occ.ReserveCircle(p, 1.2f, TagTree, TownLifeOccupancy.Block.Solids);
                    serial++; s.trees++; if (pass == 1) s.treesB++; if (nudge) s.nudged++;
                    lastStation = i * Step; return true;
                }
            }
        }
        return false;
    }

    // ================================================================================================= verge strip

    static int EmitStrip(TownLifeKit.MeshData data, TownLifeScene.RoadInfo road, int side, List<int> pieces, List<float> outers, int stride, float inner, float y)
    {
        for (int k = 0; k < pieces.Count; k++)
        {
            int i = pieces[k]; float outer = outers[k];
            float st0 = i * Step, st1 = (i + stride) * Step;
            Vector3 a = LinePoint(road, side, st0, inner), b = LinePoint(road, side, st1, inner), c = LinePoint(road, side, st1, outer), d = LinePoint(road, side, st0, outer);
            a.y = b.y = c.y = d.y = y;
            if (Vector3.Cross(b - a, c - a).y < 0f) data.Quad("streets.verge", a, d, c, b); else data.Quad("streets.verge", a, b, c, d);
        }
        return pieces.Count;
    }

    static bool InParcel(State s, Vector3 p)
    {
        foreach (var b in s.parcels)
            if (p.x >= b.minX && p.x <= b.maxX && p.z >= b.minZ && p.z <= b.maxZ && TownLifeGeo.PolygonContains(b.outline, p)) return true;
        return false;
    }

    /// <summary>How far behind the sidewalk this verge piece may reach before it runs into a parcel (whose yard lawn is drawn by
    /// Yards at LawnY): the last depth, sampled every 0.15 m at both ends and the middle, that is still outside every parcel.
    /// Returns less than inner when the piece starts inside one.</summary>
    static float ClipToParcels(State s, TownLifeScene.RoadInfo road, int side, float st0, float st1, float inner, float outer)
    {
        if (s.parcels.Count == 0) return outer;
        float mid = (st0 + st1) * 0.5f;
        for (float d = inner; d <= outer + 0.001f; d += 0.15f)
            if (InParcel(s, LinePoint(road, side, st0, d)) || InParcel(s, LinePoint(road, side, mid, d)) || InParcel(s, LinePoint(road, side, st1, d))) return d - 0.15f;
        return outer;
    }

    /// <summary>A slightly deeper green mown strip under the tree line, one merged decal per road side, broken wherever a
    /// driveway, garden path or anything else flat-forbidden crosses it.</summary>
    static void VergeStrip(State s, TownLifeScene.RoadInfo road, int side, bool[] hard, Transform group)
    {
        if (road.service) return;
        const float inner = 0.3f, outer = 2.1f, y = TownLifeContext.VergeY; const int stride = 2;    // 1 m pieces
        var data = new TownLifeKit.MeshData(); int quads = 0;
        var ok = new List<int>(); var okOuter = new List<float>();
        for (int i = 0; i + stride < hard.Length; i += stride)
        {
            bool free = !hard[i] && !hard[i + stride];
            float reach = outer;
            if (free)
            {
                var mid = LinePoint(road, side, (i + stride * 0.5f) * Step, LineA);
                free = s.occ.Free(mid, 1.0f, TownLifeOccupancy.What.Flat) && !InPark(s, mid, 0.2f) && s.scene.IsFlat(mid, 1f)
                    && !s.occ.Within(mid, 1.0f, "paving") && !s.occ.Within(mid, 2.4f, "bed") && !s.occ.Within(mid, 1.0f, "hedge") && !s.occ.Within(mid, 0.9f, "fence");
                // Never overlap a yard lawn (same plane, different green = flicker): stop at the parcel edge, drop the piece if it starts inside one.
                if (free) { reach = ClipToParcels(s, road, side, i * Step, (i + stride) * Step, inner, outer); free = reach >= inner + 0.25f; }
            }
            if (free) { ok.Add(i); okOuter.Add(reach); continue; }
            if (ok.Count >= 5) quads += EmitStrip(data, road, side, ok, okOuter, stride, inner, y);     // nothing shorter than 5 m: stray squares read as glitches
            ok.Clear(); okOuter.Clear();
        }
        if (ok.Count >= 5) quads += EmitStrip(data, road, side, ok, okOuter, stride, inner, y);
        if (quads == 0) return;
        TownLifeKit.Custom(group, "streets_verge_" + TownLifeKit.Sanitize(road.id) + (side > 0 ? "_r" : "_l"), data, false, false);
        s.strips++;
    }
}

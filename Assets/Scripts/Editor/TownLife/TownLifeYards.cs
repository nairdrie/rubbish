// Owner: yards agent. Contract: design/CONTRACT.md.
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Back and front yard dressing, outbuildings (DESIGN section 8 'Yards'). Group: ctx.Group("Yards").
/// Every household gets a sub-group TL_Yard_&lt;stopId&gt;: a lawn tint, an optional garage at the head of the drive,
/// one or more themed back-yard zones sized to the real yard, trees toward the rear, planting along the fence runs,
/// and a front garden (foundation planting, path beds, a feature tree, a front hedge in Acres).
/// Deterministic (ctx.Rng per lot), physics-free, reads and feeds ctx.occupancy.</summary>
public static class TownLifeYards
{
    /// <summary>Module name used in options, reports and RNG keys.</summary>
    public const string Name = "yards";

    const string Pack = "Assets/Suburbs Town Pack/PREFABS/";
    const string TagStructure = "structure.yards", TagPaving = "paving.yards", TagTree = "tree.yard", TagBed = "bed.yards", TagHedge = "hedge.yards", TagRoute = "path.yardroute";

    static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }

    sealed class Cell { public float x, d, edge; public Vector3 w; }
    sealed class Strip { public List<Vector3> pts; public string panel, post, kind; public bool shadows; }

    sealed class Lot
    {
        public TownLifeScene.HouseInfo h; public TownLifePlan.Parcel par; public Transform group; public System.Random rng;
        public string id, key; public bool acres; public Vector3[] outline; public List<Cell> back = new List<Cell>();
        public float front, rear, left, right, yawIn, yawOut, maxD, minX, maxX; public Vector2 backCentre; public float backArea;
        public List<TownLifeFences.Run> runs; public int seq; public Vector2 last; public string themes = "";
        public bool hasNear, hasRear; public Vector2 nearAt, rearAt; public List<Strip> strips = new List<Strip>();
        public Vector3 W(float x, float d) { return h.LotPoint(x, d); }
        public Vector3 W(Vector2 v) { return h.LotPoint(v.x, v.y); }
        public Vector2 L(Vector3 w) { var v = w - h.plan.origin; return new Vector2(v.x * h.plan.right.x + v.z * h.plan.right.z, v.x * h.plan.inward.x + v.z * h.plan.inward.z); }
        public string Uid(string kind) { return "yards_" + kind + "_" + key + "_" + (seq++); }
    }

    static TownLifeContext ctx; static TownLifeOccupancy occ;
    static int nObjects, nGarages, nTrees, nRuns, nItems;

    // ================================================================================================= entry

    /// <summary>Builds this module into ctx.</summary>
    public static void Build(TownLifeContext context)
    {
        ctx = context; occ = ctx.occupancy; nObjects = nGarages = nTrees = nRuns = nItems = 0;
        try
        {
            DefineAll();
            var top = ctx.Group("Yards");
            // one open patch of lawn mid-block (the fixed "backyard-void-eye" survey point stands here): no trunk or shed on it
            var clearing = new Vector3(250f, 0f, 110f);
            if (ctx.plan != null && ctx.plan.LabelAt(clearing) >= 0) occ.ReserveCircle(clearing, 3.2f, TownLifeTags.Lawn + ".yards", TownLifeOccupancy.Block.Solids);
            var nearTheme = new Dictionary<int, string>(); var rearTheme = new Dictionary<int, string>(); var lawnTone = new Dictionary<int, int>(); var hedgeKind = new Dictionary<int, int>();
            int lots = 0; var themeCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < ctx.scene.houses.Length; i++)
            {
                var h = ctx.scene.houses[i];
                if (!ctx.options.District(h.districtId)) continue;
                if (ctx.plan == null || ctx.plan.parcels == null || i >= ctx.plan.parcels.Length) continue;
                var par = ctx.plan.parcels[i];
                if (par == null || par.outline == null || par.outline.Length < 3) continue;
                var lot = MakeLot(h, par, top);
                ReserveRoute(lot);
                if (ctx.options.Flag("yards.lawns", true)) Lawn(lot, lawnTone);
                if (ctx.options.Flag("yards.garages", true)) Outbuilding(lot);
                if (ctx.options.Flag("yards.back", true)) BackYard(lot, nearTheme, rearTheme);
                if (ctx.options.Flag("yards.front", true)) FrontYard(lot, hedgeKind);
                if (ctx.options.Flag("yards.borders", true)) Borders(lot);
                Flush(lot);
                lots++;
                foreach (var t in lot.themes.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)) { themeCount.TryGetValue(t, out int n); themeCount[t] = n + 1; }
                int count = lot.group.GetComponentsInChildren<Transform>(true).Length;
                nObjects += count;
                var bc = lot.W(lot.backCentre);
                ctx.report.Append("  yard ").Append(lot.id).Append(" back=").Append(lot.backArea.ToString("0")).Append("m2 centre=(").Append(bc.x.ToString("0.0")).Append(",").Append(bc.z.ToString("0.0"))
                    .Append(") themes=").Append(lot.themes).Append(" objects=").Append(count).Append('\n');
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in themeCount) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            ctx.Log(Name, lots + " lots, " + nObjects + " GameObjects, " + nItems + " yard items, " + nTrees + " trees, " + nRuns + " planted runs, " + nGarages + " garages; themes:" + sb);
        }
        finally { ctx = null; occ = null; }
    }

    // ================================================================================================= lot model

    static Lot MakeLot(TownLifeScene.HouseInfo h, TownLifePlan.Parcel par, Transform top)
    {
        var lot = new Lot { h = h, par = par, id = h.stopId, key = TownLifeKit.Sanitize(h.stopId), acres = h.districtId == TownLifeScene.Acres, outline = par.outline };
        lot.group = TownLifeKit.Group(top, "TL_Yard_" + h.stopId);
        lot.rng = ctx.Rng("yards/lot/" + h.stopId);
        lot.front = h.frontDepth; lot.rear = h.rearDepth; lot.left = h.leftExtent; lot.right = h.rightExtent;
        lot.yawIn = TownLifeGeo.Yaw(h.plan.inward); lot.yawOut = TownLifeGeo.Yaw(-h.plan.inward);
        lot.runs = ctx.fences != null ? ctx.fences.RunsOf(h.stopId) : new List<TownLifeFences.Run>();
        float minX = 1e9f, maxX = -1e9f, maxD = -1e9f;
        foreach (var p in par.outline) { var l = lot.L(p); minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x); maxD = Mathf.Max(maxD, l.y); }
        lot.minX = minX; lot.maxX = maxX; lot.maxD = maxD;
        float sx = 0f, sd = 0f;
        for (float d = lot.rear + 0.75f; d <= maxD; d += 1f)
            for (float x = Mathf.Floor(minX) + 0.5f; x <= maxX; x += 1f)
            {
                var w = lot.W(x, d);
                if (!TownLifeGeo.PolygonContains(par.outline, w)) continue;
                lot.back.Add(new Cell { x = x, d = d, w = w, edge = EdgeDist(par.outline, w) }); sx += x; sd += d;
            }
        lot.backArea = lot.back.Count;
        lot.backCentre = lot.back.Count > 0 ? new Vector2(sx / lot.back.Count, sd / lot.back.Count) : new Vector2((lot.left + lot.right) * 0.5f, lot.rear + 3f);
        return lot;
    }

    static float EdgeDist(Vector3[] outline, Vector3 p)
    {
        float best = 1e9f;
        for (int i = 0; i < outline.Length; i++) best = Mathf.Min(best, TownLifeGeo.DistToSegment(p, outline[i], outline[(i + 1) % outline.Length]));
        return best;
    }

    static float R(System.Random rng, float a, float b) { return a + (float)rng.NextDouble() * (b - a); }
    static string Tree(string id) { return TownLifeKit.HasProp(id) ? id : TownLifeKit.HasProp("kit.tree.round.m") ? "kit.tree.round.m" : "kit.tree.round"; }
    static string Prop(string id, string fallback) { return TownLifeKit.HasProp(id) ? id : fallback; }
    static Vector3 G(Vector3 p) { p.y = ctx.scene.GroundY(p); return p; }

    /// <summary>Keeps a walkable route from the gate gap past the house corner into the middle of the yard.</summary>
    static void ReserveRoute(Lot lot)
    {
        if (ctx.fences == null) return;
        var gate = ctx.fences.GateOf(lot.id); if (gate == null) return;
        var g = lot.L(gate.Centre);
        var p1 = lot.W(g.x, Mathf.Max(g.y, lot.rear + 1.6f)); var p2 = lot.W(Vector2.Lerp(new Vector2(g.x, lot.rear + 1.6f), lot.backCentre, 0.6f));
        occ.ReserveLine(TownLifeGeo.Flat(gate.Centre), p1, 0.6f, TagRoute, TownLifeOccupancy.Block.Solids);
        occ.ReserveLine(p1, p2, 0.6f, TagRoute, TownLifeOccupancy.Block.Solids);
    }

    // ================================================================================================= placement

    static bool Fits(Lot lot, Vector3 c, float yaw, float hw, float hd, float edge, float margin = 0.3f)
    {
        var q = TownLifeGeo.Quad(c, yaw, hw, hd);
        foreach (var k in TownLifeGeo.Corners(q)) { if (!TownLifeGeo.PolygonContains(lot.outline, k) || EdgeDist(lot.outline, k) < edge) return false; }
        if (!occ.Free(c, Mathf.Min(hw, hd), TownLifeOccupancy.What.Solid)) return false;
        return occ.FreeQuad(q, TownLifeOccupancy.What.Solid, margin);
    }

    static float YawFor(Lot lot, Cell c, int face, float free)
    {
        switch (face)
        {
            case 0: return lot.yawOut;
            case 1: return lot.yawIn;
            case 2:
            {
                var v = lot.backCentre - new Vector2(c.x, c.d);
                if (Mathf.Abs(v.x) > Mathf.Abs(v.y) * 1.3f) return TownLifeGeo.Yaw(lot.h.plan.right * Mathf.Sign(v.x));
                return v.y >= 0f ? lot.yawIn : lot.yawOut;
            }
            case 4: return lot.yawIn + 90f;
            default: return free;
        }
    }

    /// <summary>Finds the back-yard cell nearest to <paramref name="target"/> (lot frame; wEdge > 0 pulls toward the
    /// boundary) where an hw x hd box fits inside the parcel, clear of everything reserved.</summary>
    static bool Spot(Lot lot, Vector2 target, float hw, float hd, int face, float wEdge, float edge, float maxDist, out Vector3 w, out float yaw)
    {
        w = Vector3.zero; yaw = 0f; int n = lot.back.Count; if (n == 0) return false;
        var keys = new float[n]; var idx = new int[n]; float free = lot.yawIn + R(lot.rng, -180f, 180f);
        for (int i = 0; i < n; i++)
        {
            var c = lot.back[i]; float dist = Mathf.Sqrt((c.x - target.x) * (c.x - target.x) + (c.d - target.y) * (c.d - target.y));
            keys[i] = dist > maxDist ? 1e6f + i : dist + wEdge * c.edge + i * 1e-4f; idx[i] = i;
        }
        Array.Sort(keys, idx);
        int tries = 0; float need = edge + Mathf.Min(hw, hd) * 0.7f;
        for (int k = 0; k < n; k++)
        {
            if (keys[k] >= 1e6f) break;
            var c = lot.back[idx[k]]; if (c.edge < need) continue;
            yaw = YawFor(lot, c, face, free);
            if (Fits(lot, c.w, yaw, hw, hd, edge)) { w = G(c.w); lot.last = new Vector2(c.x, c.d); return true; }
            if (++tries > 260) break;
        }
        return false;
    }

    static void Reserve(Vector3 w, float yaw, float hw, float hd, string tag, bool everything)
    {
        occ.ReserveBox(w, yaw, hw, hd, tag, everything ? TownLifeOccupancy.Block.Everything : TownLifeOccupancy.Block.Solids, 0.25f);
    }

    static GameObject Item(Lot lot, string prop, Vector2 target, float hw, float hd, int face = 2, float wEdge = 0f, float edge = 0.9f, float maxDist = 12f, bool everything = true, string tag = TagStructure, float scale = 1f)
    {
        if (!TownLifeKit.HasProp(prop)) return null;
        if (!Spot(lot, target, hw, hd, face, wEdge, edge, maxDist, out var w, out float yaw)) return null;
        var go = TownLifeKit.Spawn(lot.group, prop, w, yaw, scale);
        Reserve(w, yaw, hw, hd, tag, everything); nItems++;
        return go;
    }

    static GameObject Prefab(Lot lot, string path, Vector2 target, float hw, float hd, int face = 2, float wEdge = 0f, float edge = 0.9f, float maxDist = 12f, float scale = 1f)
    {
        if (!Spot(lot, target, hw, hd, face, wEdge, edge, maxDist, out var w, out float yaw)) return null;
        var go = TownLifeKit.SpawnPrefab(lot.group, Pack + path, w, yaw, scale);          // renamed TL_<prefab>, colliders off
        if (go == null) return null;
        Reserve(w, yaw, hw, hd, TagStructure, false); nItems++;
        return go;
    }

    /// <summary>Flat planting test: off buildings, slabs, paths, pools, pens, existing lot props and curb stops.</summary>
    static bool FlatOk(Vector3 p, float r)
    {
        return occ.Free(p, r, TownLifeOccupancy.What.Flat) && !occ.Within(p, r, TownLifeTags.LotItem) && !occ.Within(p, r, TownLifeTags.KidPen) && !occ.Within(p, r + 0.3f, TownLifeTags.Driveway);
    }

    /// <summary>Splits a-b into the stretches whose samples all pass <paramref name="ok"/>.</summary>
    static List<Vector3[]> Pieces(Vector3 a, Vector3 b, float step, float minLen, Func<Vector3, bool> ok)
    {
        var list = new List<Vector3[]>(); float len = TownLifeGeo.Dist(a, b); if (len < minLen) return list;
        int n = Mathf.Max(1, Mathf.RoundToInt(len / step)), start = -1;
        for (int i = 0; i <= n; i++)
        {
            bool good = ok(Vector3.Lerp(a, b, i / (float)n));
            if (good && start < 0) start = i;
            if (start >= 0 && (!good || i == n))
            {
                int end = good ? i : i - 1;
                if ((end - start) * len / n >= minLen) list.Add(new[] { Vector3.Lerp(a, b, start / (float)n), Vector3.Lerp(a, b, end / (float)n) });
                start = -1;
            }
        }
        return list;
    }

    static void PlantRun(Lot lot, string kind, Vector3[] line, string panel, bool shadows, string post = null)
    {
        if (!TownLifeKit.HasProp(panel)) return;
        lot.strips.Add(new Strip { pts = new List<Vector3>(line), panel = panel, post = post, kind = kind, shadows = shadows });
    }

    /// <summary>Joins queued planting strips of the same style that meet at a corner (one GameObject per joined run) and builds them.</summary>
    static void Flush(Lot lot)
    {
        var st = lot.strips; bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < st.Count && !merged; i++)
                for (int j = i + 1; j < st.Count && !merged; j++)
                {
                    Strip a = st[i], b = st[j];
                    if (a.panel != b.panel || a.post != b.post || a.post != null || a.kind == "pathbed" || b.kind == "pathbed" || a.kind == "fronthedge" || b.kind == "fronthedge") continue;
                    for (int combo = 0; combo < 4 && !merged; combo++)
                    {
                        Vector3 ea = (combo & 1) == 0 ? a.pts[a.pts.Count - 1] : a.pts[0], eb = (combo & 2) == 0 ? b.pts[0] : b.pts[b.pts.Count - 1];
                        if (TownLifeGeo.Dist(ea, eb) > 1.9f) continue;
                        if ((combo & 1) != 0) a.pts.Reverse();
                        if ((combo & 2) != 0) b.pts.Reverse();
                        Vector3 a0 = a.pts[a.pts.Count - 2], a1 = a.pts[a.pts.Count - 1], b0 = b.pts[0], b1 = b.pts[1];
                        Vector3 da = (a1 - a0).normalized, db = (b1 - b0).normalized; float cross = da.x * db.z - da.z * db.x;
                        if (Mathf.Abs(cross) > 0.3f)
                        {
                            float t = ((b0.x - a1.x) * db.z - (b0.z - a1.z) * db.x) / cross; var x = a1 + da * t;
                            if (TownLifeGeo.Dist(x, a1) < 2.4f && TownLifeGeo.Dist(x, b0) < 2.4f) { a.pts[a.pts.Count - 1] = x; a.pts.AddRange(b.pts.GetRange(1, b.pts.Count - 1)); }
                            else a.pts.AddRange(b.pts);
                        }
                        else a.pts.AddRange(b.pts);
                        a.shadows |= b.shadows; st.RemoveAt(j); merged = true;
                    }
                }
        }
        foreach (var strip in st)
        {
            var spec = new TownLifeKit.RunSpec { panel = strip.panel, panelLength = 2f, post = strip.post, postAtEveryJoint = strip.post != null, postAtStart = strip.post != null, postAtEnd = strip.post != null, castShadows = strip.shadows, y = TownLifeContext.GrassY };
            TownLifeKit.Run(lot.group, lot.Uid(strip.kind), strip.pts, spec); nRuns++;
        }
        st.Clear();
    }

    // ================================================================================================= lawn

    static void Lawn(Lot lot, Dictionary<int, int> tones)
    {
        var used = new HashSet<int>();
        foreach (int nb in lot.par.neighbours) if (tones.TryGetValue(nb, out int t)) used.Add(t);
        int tone = lot.rng.Next(4);
        for (int k = 0; k < 4 && used.Contains(tone); k++) tone = (tone + 1) % 4;
        tones[lot.par.index] = tone;
        if (tone == 3) return;                                   // plain town grass
        TownLifeKit.Polygon(lot.group, "yards_lawn_" + lot.key, lot.outline, "yards.lawn." + tone, TownLifeContext.LawnY);
    }

    // ================================================================================================= outbuildings

    static void Outbuilding(Lot lot)
    {
        var p = lot.h.plan; if (!p.hasDrive) return;
        Vector2 la = lot.L(p.driveA), lb = lot.L(p.driveB);
        Vector3 top = la.y > lb.y ? p.driveA : p.driveB, foot = la.y > lb.y ? p.driveB : p.driveA;
        top = TownLifeGeo.Flat(top); foot = TownLifeGeo.Flat(foot);
        if (TownLifeGeo.Dist(top, foot) < 3f) return;
        var dir = (top - foot).normalized; float yaw = TownLifeGeo.Yaw(-dir);
        var rng = ctx.Rng("yards/garage/" + lot.id);
        const float hw = 2.15f, hd = 3.15f;
        if (rng.Next(100) < 12) return;
        var side = TownLifeGeo.Right(dir); float houseMid = (lot.left + lot.right) * 0.5f, outward = lot.L(top).x >= houseMid ? 1f : -1f;
        if (Vector3.Dot(side, lot.h.plan.right) < 0f) outward = -outward;
        string[] kinds = { "yards.garage.cream", "yards.garage.barn", "yards.garage.wood", "yards.carport", "yards.garage.cream", "yards.garage.wood" };
        string kind = kinds[rng.Next(kinds.Length)];
        for (float off = 3.0f; off <= 13f; off += 0.75f)
            foreach (float lat in new[] { 0f, 1f, 2f, 3f, -1f })
            {
                var c = top + dir * (off + hd) + side * (lat * outward);
                if (lot.L(c).y < lot.front + 1f) continue;
                if (!Fits(lot, c, yaw, hw, hd, 0.9f, 0.3f)) continue;
                bool crosses = false;
                foreach (var run in lot.runs) if (TownLifeGeo.SegmentsCross(top, c, run.a, run.b)) { crosses = true; break; }
                TownLifeKit.Spawn(lot.group, kind, G(c), yaw);
                Reserve(c, yaw, hw, hd, TagStructure, true);
                if (!crosses && lat == 0f)
                {
                    float width = Mathf.Clamp(p.driveHalfWidth * 2f, 2.8f, 3.6f);
                    var mid = top + dir * (off * 0.5f - 0.1f);
                    TownLifeKit.Rect(lot.group, "yards_apron_" + lot.key, mid, new Vector2(width, off + 0.5f), TownLifeGeo.Yaw(dir), "yards.drive", TownLifeContext.PavingY);
                    occ.ReserveLine(top, c, width * 0.5f, TagPaving, TownLifeOccupancy.Block.Everything);
                }
                else occ.ReserveBox(c + dir * -(hd + 1.3f), yaw, 1.4f, 1.2f, TagRoute, TownLifeOccupancy.Block.Solids);     // keep the door approach open
                nGarages++; lot.themes += (crosses ? "yardgarage+" : "garage+");
                return;
            }
    }

    // ================================================================================================= back yard

    static readonly string[] NearThemes = { "patio", "lazy", "washing", "dog", "kids", "deck" };
    static readonly string[] RearThemes = { "kitchen", "orchard", "greenhouse", "camp", "kitchen", "orchard" };

    static string PickTheme(System.Random rng, string[] pool, Lot lot, Dictionary<int, string> chosen, string not = null)
    {
        var used = new HashSet<string>();
        foreach (int nb in lot.par.neighbours) if (chosen.TryGetValue(nb, out string t)) used.Add(t);
        int start = rng.Next(pool.Length);
        for (int k = 0; k < pool.Length; k++) { string t = pool[(start + k) % pool.Length]; if (!used.Contains(t) && t != not) return t; }
        return pool[start];
    }

    static void BackYard(Lot lot, Dictionary<int, string> nearTheme, Dictionary<int, string> rearTheme)
    {
        if (lot.back.Count < 25) { lot.themes += "none+"; return; }
        var rng = ctx.Rng("yards/theme/" + lot.id);
        float area = lot.backArea, bcx = Mathf.Clamp((lot.left + lot.right) * 0.5f, lot.minX + 3f, lot.maxX - 3f);
        var nearA = new Vector2(bcx, lot.rear + 4f);

        // rear anchors: centroid(s) of the deeper half of the yard
        float split = lot.rear + (lot.maxD - lot.rear) * 0.5f; var rearCells = new List<Cell>();
        foreach (var c in lot.back) if (c.d >= split) rearCells.Add(c);
        if (rearCells.Count == 0) rearCells = lot.back;
        float mx = 0f; foreach (var c in rearCells) mx += c.x; mx /= rearCells.Count;
        Vector2 all = Vector2.zero, lft = Vector2.zero, rgt = Vector2.zero; int nl = 0, nr = 0;
        foreach (var c in rearCells) { all += new Vector2(c.x, c.d); if (c.x < mx) { lft += new Vector2(c.x, c.d); nl++; } else { rgt += new Vector2(c.x, c.d); nr++; } }
        all /= rearCells.Count; lft = nl > 0 ? lft / nl : all; rgt = nr > 0 ? rgt / nr : all;

        // heroes already on the lot
        TownLifeScene.LotItem pool = null, tramp = null, rack = null;
        foreach (var it in lot.h.items)
        {
            if (!it.rear) continue;
            if (it.kind == TownLifeScene.LotItemKind.Pool) pool = it; else if (it.kind == TownLifeScene.LotItemKind.Trampoline) tramp = it; else if (it.kind == TownLifeScene.LotItemKind.DryingRack) rack = it;
        }

        string near = pool != null ? "poolside" : tramp != null ? "kids" : rack != null ? "washing" : PickTheme(rng, NearThemes, lot, nearTheme);
        nearTheme[lot.par.index] = near;
        int nearItems = nItems;
        switch (near)
        {
            case "poolside": Poolside(lot, lot.L(TownLifeGeo.Centre(pool.rect)), rng); break;
            case "patio": Patio(lot, nearA, rng); break;
            case "deck": Deck(lot, nearA, rng); break;
            case "lazy": Lazy(lot, nearA + new Vector2(0f, 2f), rng); break;
            case "washing": Washing(lot, rack != null ? lot.L(TownLifeGeo.Centre(rack.rect)) : nearA + new Vector2(0f, 2.5f), rack != null, rng); break;
            case "dog": Dog(lot, nearA, rng); break;
            case "kids": Kids(lot, tramp != null ? lot.L(TownLifeGeo.Centre(tramp.rect)) : nearA + new Vector2(0f, 3f), rng, area); break;
        }
        if (nItems == nearItems) { Small(lot, nearA, rng); near = "small"; }
        if (nItems > nearItems) { lot.hasNear = true; lot.nearAt = lot.last; }
        lot.themes += near + "+";

        if (area >= 150f)
        {
            string r1 = PickTheme(rng, RearThemes, lot, rearTheme, near == "kids" ? "camp" : null);
            rearTheme[lot.par.index] = r1;
            bool two = area >= 700f; int scale = area >= 420f ? 2 : area >= 260f ? 1 : 0;
            bool leftFirst = rng.Next(2) == 0;
            int before = nItems + nTrees;
            Rear(lot, r1, two ? (leftFirst ? lft : rgt) : all, scale, rng);
            if (nItems + nTrees > before) { lot.hasRear = true; lot.rearAt = lot.last; }
            lot.themes += r1 + "+";
            if (two)
            {
                string r2 = r1; for (int k = 0; k < 6 && r2 == r1; k++) r2 = RearThemes[rng.Next(RearThemes.Length)];
                if (near != "kids" && rng.Next(3) == 0) r2 = "play";
                Rear(lot, r2, leftFirst ? rgt : lft, scale, rng);
                lot.themes += r2 + "+";
            }
        }

        if (area >= 330f) Centrepiece(lot, rng, tramp != null);
        Trees(lot, rng, area);
        Ornaments(lot, rng, area);
        Stones(lot, rng);
    }

    static void Rear(Lot lot, string theme, Vector2 a, int scale, System.Random rng)
    {
        switch (theme)
        {
            case "kitchen": Kitchen(lot, a, scale, rng); break;
            case "orchard": Orchard(lot, a, scale, rng); break;
            case "greenhouse": Greenhouse(lot, a, scale, rng); break;
            case "camp": Camp(lot, a, rng); break;
            case "play": Kids(lot, a, rng, 600f); break;
        }
    }

    static readonly string[] Cols = { "red", "blue", "yellow", "teal" };

    static void Centrepiece(Lot lot, System.Random rng, bool noBouncer)
    {
        string[] kinds = { "yards.pond", "yards.roundbed.a", "yards.roundbed.b", "yards.goal", "kit.picnic.table", "yards.bouncer", "yards.pond", "yards.roundbed.a" };
        string kind = kinds[rng.Next(kinds.Length)]; if (noBouncer && kind == "yards.bouncer") kind = "yards.pond";
        float hw = kind == "yards.pond" ? 2.2f : kind == "yards.goal" ? 1.7f : kind == "kit.picnic.table" ? 1.2f : 1.7f, hd = kind == "yards.goal" ? 2.2f : kind == "kit.picnic.table" ? 1.1f : 1.7f;
        var at = lot.backCentre + new Vector2(R(rng, -2f, 2f), R(rng, -1.5f, 1.5f));
        Item(lot, kind, at, hw, hd, kind == "yards.goal" ? 2 : 3, 0f, 1.6f, 9f, kind == "yards.pond" || kind.StartsWith("yards.roundbed"), TagStructure);
    }

    /// <summary>Stepping stones from the back door to whatever stands at the far end of the yard.</summary>
    static void Stones(Lot lot, System.Random rng)
    {
        if (!lot.hasRear) return;
        Vector2 a = new Vector2(Mathf.Clamp(lot.hasNear ? lot.nearAt.x : (lot.left + lot.right) * 0.5f, lot.left + 0.8f, lot.right - 0.8f), lot.rear + 1.0f), b = lot.rearAt;
        if ((b - a).magnitude < 7f) return;
        var dir = (b - a).normalized; var c = (a + b) * 0.5f + new Vector2(-dir.y, dir.x) * R(rng, -3f, 3f);
        float len = (b - a).magnitude; int n = Mathf.CeilToInt(len / 0.95f); var d = new TownLifeKit.MeshData(); int stones = 0;
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n; var l = (1 - t) * (1 - t) * a + 2 * t * (1 - t) * c + t * t * b;
            var w = lot.W(l) + lot.h.plan.right * (i % 2 == 0 ? 0.12f : -0.12f);
            if (!TownLifeGeo.PolygonContains(lot.outline, w) || !FlatOk(w, 0.4f) || occ.Within(w, 0.5f, TagStructure) || occ.Within(w, 0.45f, TagTree)) continue;
            var ring = TownLifeKit.Circle(w, 0.3f + (i % 3) * 0.03f, 7);
            TownLifeKit.AddPolygon(d, i % 4 == 0 ? "stone.warm" : "stone.light", ring, TownLifeContext.PavingY); stones++;
        }
        if (stones >= 4) TownLifeKit.Custom(lot.group, lot.Uid("stones"), d);
    }

    static void Patio(Lot lot, Vector2 a, System.Random rng)
    {
        string col = Cols[rng.Next(Cols.Length)];
        if (rng.Next(3) == 0)
        {
            if (Item(lot, "yards.patio.round", new Vector2(a.x, lot.rear + 3.2f), 2.4f, 2.4f, 0, 0f, 0.9f, 9f, true, TagPaving) == null) Item(lot, "yards.loungers." + col, a, 2.2f, 1.3f, 0);
        }
        else if (Item(lot, "yards.patio.dining." + col, new Vector2(a.x, lot.rear + 2.9f), 2.6f, 2.1f, 0, 0f, 0.9f, 9f, true, TagPaving) == null)
            Item(lot, "yards.patio.round", new Vector2(a.x, lot.rear + 3.2f), 2.4f, 2.4f, 0, 0f, 0.9f, 9f, true, TagPaving);
        Item(lot, "kit.planter.tub", lot.last + new Vector2(rng.Next(2) == 0 ? -3.6f : 3.6f, 0.5f), 0.5f, 0.5f, 0, 0f, 0.8f, 5f, false);
    }

    static void Deck(Lot lot, Vector2 a, System.Random rng)
    {
        if (Item(lot, "yards.deck", new Vector2(a.x, lot.rear + 2.6f), 2.5f, 1.9f, 0, 0f, 0.9f, 9f, true, TagPaving) == null) { Patio(lot, a, rng); return; }
        Item(lot, "yards.hammock", lot.last + new Vector2(rng.Next(2) == 0 ? -5.5f : 5.5f, 3f), 2.1f, 0.7f, 4, 0f, 0.9f, 8f, false);
    }

    static void Lazy(Lot lot, Vector2 a, System.Random rng)
    {
        string col = Cols[rng.Next(Cols.Length)];
        Item(lot, "yards.paddling", a, 2.0f, 1.5f, 3, 0f, 0.9f, 10f, false);
        Item(lot, "yards.loungers." + col, lot.last + new Vector2(rng.Next(2) == 0 ? -4.2f : 4.2f, 0.5f), 2.2f, 1.3f, 2, 0f, 0.9f, 8f, false);
        Item(lot, "yards.hammock", a + new Vector2(0f, 6f), 2.1f, 0.7f, 4, 0.3f, 0.9f, 9f, false);
    }

    static void Washing(Lot lot, Vector2 a, bool hasRack, System.Random rng)
    {
        if (!hasRack) Prefab(lot, "Props/SM_clothes_drying_rack_0" + (1 + rng.Next(4)), a, 2.1f, 0.7f, rng.Next(2) == 0 ? 0 : 4);
        Item(lot, "yards.washline", a + new Vector2(rng.Next(2) == 0 ? -1f : 1f, 3.4f), 2.9f, 0.9f, rng.Next(2) == 0 ? 0 : 4, 0f, 0.9f, 10f, false);
        Item(lot, "yards.patio.small", new Vector2(a.x, lot.rear + 2f), 1.6f, 1.3f, 0, 0f, 0.8f, 8f, true, TagPaving);
    }

    static void Dog(Lot lot, Vector2 a, System.Random rng)
    {
        float side = rng.Next(2) == 0 ? -1f : 1f;
        Item(lot, "yards.kennel", a + new Vector2(side * 4.5f, 0.5f), 1.4f, 1.4f, 2, 0f, 0.9f, 10f, false);
        Item(lot, "yards.dogholes", lot.last + new Vector2(-side * 3.5f, 3.5f), 1.6f, 1.3f, 3, 0f, 0.9f, 8f, false);
        Item(lot, "yards.patio.small", new Vector2(a.x - side * 2f, lot.rear + 2f), 1.6f, 1.3f, 0, 0f, 0.8f, 8f, true, TagPaving);
    }

    static void Kids(Lot lot, Vector2 a, System.Random rng, float area)
    {
        float side = rng.Next(2) == 0 ? -1f : 1f;
        Prefab(lot, "Playground/SM_swing", a + new Vector2(side * 0.5f, 1f), 2.4f, 1.6f, rng.Next(2) == 0 ? 0 : 4, 0f, 1.0f, 10f);
        var sw = lot.last;
        Item(lot, "yards.sandbox", sw + new Vector2(-side * 4.6f, -0.5f), 1.4f, 1.4f, 0, 0f, 0.9f, 8f, false);
        Item(lot, "yards.playhouse", sw + new Vector2(side * 4.5f, 2.5f), 1.3f, 1.3f, 2, 0.4f, 0.9f, 9f);
        if (area > 300f) Prefab(lot, "Playground/SM_slide", sw + new Vector2(-side * 1f, 4.5f), 1.8f, 0.8f, 4, 0f, 0.9f, 8f);
    }

    static void Poolside(Lot lot, Vector2 pool, System.Random rng)
    {
        string col = Cols[rng.Next(Cols.Length)];
        Item(lot, "yards.loungers." + col, pool, 2.2f, 1.3f, 2, 0f, 0.9f, 12f, false);
        Item(lot, "yards.loungers." + Cols[(Array.IndexOf(Cols, col) + 1) % Cols.Length], pool + (pool - lot.last), 2.2f, 1.3f, 2, 0f, 0.9f, 12f, false);
        Item(lot, "yards.patio.dining." + col, new Vector2((lot.left + lot.right) * 0.5f, lot.rear + 2.9f), 2.6f, 2.1f, 0, 0f, 0.9f, 9f, true, TagPaving);
    }

    static void Small(Lot lot, Vector2 a, System.Random rng)
    {
        string col = Cols[rng.Next(Cols.Length)];
        if (Item(lot, "yards.patio.small", new Vector2(a.x, lot.rear + 2f), 1.6f, 1.3f, 0, 0f, 0.7f, 10f, true, TagPaving) == null)
            Item(lot, "yards.loungers." + col, a, 2.2f, 1.3f, 2, 0f, 0.7f, 14f, false);
    }

    static readonly string[] ShedCols = { "red", "blue", "green", "yellow", "white" };

    static void Kitchen(Lot lot, Vector2 a, int scale, System.Random rng)
    {
        var shed = Item(lot, "yards.shed." + ShedCols[rng.Next(ShedCols.Length)], a, 1.5f, 1.3f, 2, 1.1f, 0.9f, 14f);
        var s = shed != null ? lot.last : a;
        var toC = (lot.backCentre - s); toC = toC.sqrMagnitude > 0.01f ? toC.normalized : Vector2.up;
        if (scale >= 1) Item(lot, rng.Next(2) == 0 ? "yards.vegplot.a" : "yards.vegplot.b", s + toC * 4.2f, 2.3f, 1.8f, rng.Next(2) == 0 ? 1 : 4, 0f, 1.0f, 9f, true, TagBed);
        else Item(lot, "yards.vegplot.b", s + toC * 3.8f, 1.9f, 1.8f, 1, 0f, 1.0f, 8f, true, TagBed);
        if (scale >= 2) Item(lot, "yards.vegplot.a", lot.last + new Vector2(toC.y, -toC.x) * 5.2f, 2.3f, 1.8f, 1, 0f, 1.0f, 8f, true, TagBed);
        Item(lot, rng.Next(2) == 0 ? "yards.gardenstuff.a" : "yards.gardenstuff.b", s + new Vector2(toC.y, -toC.x) * 3f, 1.3f, 0.9f, 2, 0.6f, 0.8f, 7f, false);
    }

    static void Greenhouse(Lot lot, Vector2 a, int scale, System.Random rng)
    {
        var gh = Item(lot, "yards.greenhouse", a, 1.5f, 2.1f, 2, 0.9f, 1.0f, 14f);
        if (gh == null) { Kitchen(lot, a, 0, rng); return; }
        var s = lot.last; var toC = (lot.backCentre - s); toC = toC.sqrMagnitude > 0.01f ? toC.normalized : Vector2.up;
        Item(lot, "yards.vegplot.b", s + new Vector2(toC.y, -toC.x) * 4.2f, 1.9f, 1.8f, 1, 0.3f, 1.0f, 8f, true, TagBed);
        if (scale >= 1) Item(lot, "yards.gardenstuff.a", s + new Vector2(-toC.y, toC.x) * 3.2f, 1.3f, 0.9f, 2, 0.5f, 0.8f, 7f, false);
    }

    static void Orchard(Lot lot, Vector2 a, int scale, System.Random rng)
    {
        int nx = scale >= 2 ? 3 : 2, nz = scale >= 1 ? 2 : 1; float pitch = 4.4f; int placed = 0;
        string fruit = Tree("kit.tree.fruit.s");
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                var l = a + new Vector2((i - (nx - 1) * 0.5f) * pitch, (j - (nz - 1) * 0.5f) * pitch);
                var w = lot.W(l);
                if (!TownLifeGeo.PolygonContains(lot.outline, w) || EdgeDist(lot.outline, w) < 2f || lot.L(w).y < lot.rear + 2.5f) continue;
                if (!occ.Free(w, 1.3f) || occ.Within(w, 3.6f, TownLifeTags.Tree)) continue;
                occ.ReserveCircle(w, 1.1f, TagTree, TownLifeOccupancy.Block.Solids);
                TownLifeKit.Spawn(lot.group, fruit, G(w), R(rng, 0f, 360f), R(rng, 0.95f, 1.25f)); placed++; nTrees++;
            }
        if (placed > 0) Item(lot, "yards.orchardkit", a + new Vector2(0.6f, 0.3f), 1.1f, 0.9f, 3, 0f, 0.9f, 7f, false);
        else Kitchen(lot, a, 0, rng);
    }

    static void Camp(Lot lot, Vector2 a, System.Random rng)
    {
        if (Item(lot, "yards.camp." + (rng.Next(2) == 0 ? "orange" : "blue"), a, 1.5f, 2.5f, 2, 0f, 1.0f, 10f, false) == null) Kitchen(lot, a, 0, rng);
    }

    static readonly string[] TreesSmall = { "kit.tree.round.m", "kit.tree.birch.m", "kit.tree.blossom.m", "kit.tree.maple.m", "kit.tree.pine.m", "kit.tree.fruit.s", "kit.tree.round.s", "kit.tree.blossom.s" };
    static readonly string[] TreesBig = { "kit.tree.round.l", "kit.tree.birch.l", "kit.tree.maple.l", "kit.tree.elm.m", "kit.tree.oak.l", "kit.tree.pine.l", "kit.tree.round.m", "kit.tree.willow.l", "kit.tree.elm.l", "kit.tree.blossom.m" };

    static void Trees(Lot lot, System.Random rng, float area)
    {
        int want = Mathf.Clamp(Mathf.RoundToInt(area / 115f), area >= 130f ? 2 : 1, 7); float gap = area < 350f ? 5.5f : 7f;
        int n = lot.back.Count; var keys = new float[n]; var idx = new int[n];
        for (int i = 0; i < n; i++) { var c = lot.back[i]; keys[i] = -(c.d * 0.8f + R(rng, 0f, 12f)) + Mathf.Abs(c.edge - 3.5f) * 0.9f; idx[i] = i; }
        Array.Sort(keys, idx);
        int placed = 0;
        for (int k = 0; k < n && placed < want; k++)
        {
            var c = lot.back[idx[k]];
            if (c.edge < (area < 350f ? 1.7f : 2.2f) || c.d < lot.rear + 3.5f) continue;
            if (!occ.Free(c.w, 1.3f) || occ.Within(c.w, placed == 0 ? 4.5f : gap, TownLifeTags.Tree) || occ.Within(c.w, 4.2f, TownLifeTags.Building) || occ.Within(c.w, 2.6f, TownLifeTags.LotItem)) continue;
            bool big = area > 240f && c.edge > 2.6f && rng.Next(100) < (area > 500f ? 65 : 40);
            string id = Tree(big ? TreesBig[rng.Next(TreesBig.Length)] : TreesSmall[rng.Next(TreesSmall.Length)]);
            occ.ReserveCircle(c.w, 1.2f, TagTree, TownLifeOccupancy.Block.Solids);
            TownLifeKit.Spawn(lot.group, id, G(c.w), R(rng, 0f, 360f), R(rng, 0.88f, 1.15f)); placed++; nTrees++;
        }
    }

    static void Ornaments(Lot lot, System.Random rng, float area)
    {
        // planted corners at the back of the yard
        int corners = area < 160f ? 1 : 2;
        for (int k = 0; k < corners; k++)
        {
            float sx = (k == 0) == (rng.Next(2) == 0) ? -1f : 1f; Cell best = null; float bs = -1e9f;
            foreach (var c in lot.back) { float s = c.d + sx * c.x * 0.9f - Mathf.Abs(c.edge - 2.2f) * 3f; if (c.edge >= 2f && s > bs) { bs = s; best = c; } }
            if (best == null) continue;
            string[] beds = { "yards.cornerbed.a", "yards.cornerbed.b", "yards.cornerbed.c", "yards.sunflowers" };
            string bed = beds[rng.Next(beds.Length)];
            Item(lot, bed, new Vector2(best.x, best.d), 1.4f, bed == "yards.sunflowers" ? 0.6f : 1.4f, 2, 0.5f, 0.75f, 7f, false, TagBed);
        }
        // one small personal touch (two in the big yards)
        for (int touch = 0; touch < (area > 520f ? 2 : 1); touch++) Touch(lot, rng);
    }

    static void Touch(Lot lot, System.Random rng)
    {
        string[] bits = { "kit.birdbath", "kit.gnome", "yards.birdtable", "kit.bench.simple", "kit.planter.tub", "yards.sunflowers", "kit.gnome", "yards.gardenstuff.b", "yards.birdtable", "kit.picnic.table" };
        string bit = bits[rng.Next(bits.Length)];
        var at = Vector2.Lerp(new Vector2((lot.left + lot.right) * 0.5f, lot.rear + 2f), lot.backCentre, R(rng, 0.5f, 1.1f)) + new Vector2(R(rng, -4f, 4f), R(rng, -2f, 2f));
        if (bit == "kit.gnome")
        {
            if (Spot(lot, at, 0.5f, 0.5f, 0, 0f, 0.8f, 9f, out var w, out _)) { TownLifeKit.Spawn(lot.group, "kit.gnome", w, lot.yawOut + R(rng, -30f, 30f), 1.5f); nItems++; occ.ReserveCircle(w, 0.4f, TagStructure, TownLifeOccupancy.Block.Solids); }
        }
        else Item(lot, bit, at, bit == "yards.sunflowers" ? 1.4f : 0.9f, 0.6f, bit == "kit.bench.simple" ? 0 : 3, bit == "yards.sunflowers" ? 1f : 0f, 0.8f, 9f, false);
    }

    // ================================================================================================= fence borders

    static void Borders(Lot lot)
    {
        var rng = ctx.Rng("yards/borders/" + lot.id);
        string[] flowers = { "red", "yellow", "pink", "purple", "white", "mixed" };
        int lotRoll = 14 + rng.Next(86), lotColour = rng.Next(flowers.Length);
        foreach (var run in lot.runs)
        {
            if (run.role == TownLifeFences.Role.Wing) { rng.Next(); continue; }
            Vector3 a = TownLifeGeo.Flat(run.a), b = TownLifeGeo.Flat(run.b); float len = TownLifeGeo.Dist(a, b); int roll = rng.Next(100), colour = rng.Next(flowers.Length);
            if (rng.Next(100) < 85) { roll = lotRoll; colour = lotColour; }
            if (len < 3.5f) continue;
            var dir = (b - a) / len; var nrm = TownLifeGeo.Right(dir); var mid = (a + b) * 0.5f;
            bool plus = TownLifeGeo.PolygonContains(lot.outline, mid + nrm * 0.8f), minus = TownLifeGeo.PolygonContains(lot.outline, mid - nrm * 0.8f);
            if (plus == minus) { plus = TownLifeGeo.PolygonContains(lot.outline, a + dir * 1.5f + nrm * 0.8f); minus = TownLifeGeo.PolygonContains(lot.outline, a + dir * 1.5f - nrm * 0.8f); if (plus == minus) continue; }
            bool street = run.role == TownLifeFences.Role.StreetSide || run.role == TownLifeFences.Role.StreetRear, rear = run.role == TownLifeFences.Role.Rear;
            string panel; bool shadows = false; float inset = 0.72f;
            if (street) { panel = roll < 60 ? "yards.panel.shrubs." + "abc"[colour % 3] : "kit.panel.flowerborder." + flowers[colour]; }
            else if (roll < 14) continue;
            else if (roll < 44) panel = "kit.panel.flowerborder." + flowers[colour];
            else if (roll < 76) panel = "yards.panel.shrubs." + "abc"[colour % 3];
            else if (roll < 90 || !rear) { panel = "kit.panel.hedge.low"; shadows = true; }
            else { panel = "kit.panel.hedge.mid"; shadows = true; inset = 0.8f; }
            var off = nrm * (plus ? inset : -inset);
            foreach (var piece in Pieces(a + dir * 0.7f + off, b - dir * 0.7f + off, 0.5f, 2.4f, p => TownLifeGeo.PolygonContains(lot.outline, p) && FlatOk(p, 0.4f) && !occ.Within(p, 0.7f, TownLifeTags.FenceGap) && !occ.Within(p, 0.45f, TagRoute)))
            {
                PlantRun(lot, "border", piece, panel, shadows);
                occ.ReserveLine(piece[0], piece[1], 0.35f, TagBed, TownLifeOccupancy.Block.Marker);
            }
        }
    }

    // ================================================================================================= front yard

    static float PolylineDist(Vector3[] pts, Vector3 p)
    {
        if (pts == null || pts.Length == 0) return 1e9f;
        if (pts.Length == 1) return TownLifeGeo.Dist(pts[0], p);
        float best = 1e9f; for (int i = 1; i < pts.Length; i++) best = Mathf.Min(best, TownLifeGeo.DistToSegment(p, pts[i - 1], pts[i]));
        return best;
    }

    static void FrontYard(Lot lot, Dictionary<int, int> hedgeKind)
    {
        var rng = ctx.Rng("yards/front/" + lot.id); var plan = lot.h.plan; var walk = plan.walk;
        string[] flowers = { "red", "yellow", "pink", "purple", "white", "mixed" };
        string flower = flowers[rng.Next(flowers.Length)];
        Func<Vector3, float> driveDist = p => plan.hasDrive ? TownLifeGeo.DistToSegment(p, plan.driveA, plan.driveB) - plan.driveHalfWidth : 1e9f;

        // ---- feature tree
        {
            string[] kinds = { "kit.tree.blossom.s", "kit.tree.birch.m", "kit.tree.fruit.s", "kit.tree.round.s", "kit.tree.blossom.s", "kit.tree.fruit.s", "kit.tree.round.s", "kit.tree.pine.m", "kit.tree.maple.m" };
            string kind = Tree(kinds[rng.Next(kinds.Length)]); float yaw = R(rng, 0f, 360f), scale = R(rng, 0.9f, 1.15f), jitter = R(rng, 0f, 1f);
            Vector3 best = Vector3.zero; float bs = -1e9f;
            for (float d = 7.4f; d <= lot.front - 2.4f; d += 0.8f)
                for (float x = Mathf.Floor(lot.minX) + 0.5f; x <= lot.maxX; x += 1f)
                {
                    var w = lot.W(x, d);
                    if (!TownLifeGeo.PolygonContains(lot.outline, w) || EdgeDist(lot.outline, w) < 1.6f) continue;
                    if (!occ.Free(w, 1.3f) || occ.Within(w, 4.6f, TownLifeTags.Tree) || occ.Within(w, 2.6f, TownLifeTags.Building)) continue;
                    float s = Mathf.Min(4f, PolylineDist(walk, w)) + Mathf.Min(3f, driveDist(w)) + Mathf.Min(3f, occ.DistanceTo(w, TownLifeTags.Tree, 9f) - 4.6f) * 0.6f + Mathf.Sin(x * 1.7f + jitter * 6f) * 0.3f;
                    if (s > bs) { bs = s; best = w; }
                }
            if (bs > -1e8f)
            {
                occ.ReserveCircle(best, 1.2f, TagTree, TownLifeOccupancy.Block.Solids);
                TownLifeKit.Spawn(lot.group, kind, G(best), yaw, scale); nTrees++;
                if (rng.Next(3) != 0 && TownLifeKit.HasProp("yards.treering")) TownLifeKit.Spawn(lot.group, "yards.treering." + flowers[rng.Next(4)], G(best), yaw);
            }
        }

        // ---- a pair of pots at the front door
        if (walk != null && walk.Length >= 2 && rng.Next(100) < 70)
        {
            Vector3 door = TownLifeGeo.Flat(walk[0]), toStreet = (TownLifeGeo.Flat(walk[1]) - door); 
            if (toStreet.sqrMagnitude > 0.01f)
            {
                toStreet.Normalize(); var at = door + toStreet * 0.9f; string pots = rng.Next(2) == 0 ? "yards.doorpots.a" : "yards.doorpots.b";
                bool clear = true; var sideways = TownLifeGeo.Right(toStreet);
                foreach (float sx in new[] { -1.25f, 1.25f }) { var q = at + sideways * sx; if (occ.Within(q, 0.45f, TownLifeTags.Building) || occ.Within(q, 0.4f, TownLifeTags.LotItem) || driveDist(q) < 0.5f) clear = false; }
                if (clear) { TownLifeKit.Spawn(lot.group, pots, G(at), TownLifeGeo.Yaw(toStreet)); nItems++; }
            }
        }

        // ---- foundation planting: front wall and the two side walls
        {
            var fp = TownLifeGeo.Corners(plan.footprint); var centre = TownLifeGeo.Centre(plan.footprint);
            string[] founds = { "yards.panel.found.a", "yards.panel.found.b", "yards.panel.found.c" };
            string panel = founds[rng.Next(founds.Length)];
            int rearEdge = 0; float rearD = -1e9f;
            for (int e = 0; e < 4; e++) { float d = lot.L((fp[e] + fp[(e + 1) % 4]) * 0.5f).y; if (d > rearD) { rearD = d; rearEdge = e; } }
            for (int e = 0; e < 4; e++)
            {
                if (e == rearEdge) continue;
                Vector3 a = fp[e], b = fp[(e + 1) % 4]; float len = TownLifeGeo.Dist(a, b); if (len < 2.5f) continue;
                var dir = (b - a) / len; var nrm = TownLifeGeo.Right(dir); var mid = (a + b) * 0.5f;
                if (Vector3.Dot(nrm, mid - centre) < 0f) nrm = -nrm;
                bool frontEdge = e == (rearEdge + 2) % 4;
                foreach (var piece in Pieces(a + dir * 0.35f + nrm * 0.8f, b - dir * 0.35f + nrm * 0.8f, 0.5f, 1.4f,
                             p => TownLifeGeo.PolygonContains(lot.outline, p) && EdgeDist(lot.outline, p) > 1.1f && FlatOk(p, 0.42f) && PolylineDist(walk, p) > 1.35f && driveDist(p) > 0.7f
                                  && !occ.Within(p, 0.6f, TownLifeTags.Fence) && !occ.Within(p, 0.9f, TownLifeTags.FenceGap) && !occ.Within(p, 0.5f, TagRoute) && !occ.Within(p, 0.5f, TagStructure) && !occ.Within(p, 0.3f, TagPaving)))
                {
                    PlantRun(lot, frontEdge ? "found" : "foundside", piece, panel, false);
                    occ.ReserveLine(piece[0], piece[1], 0.4f, TagBed, TownLifeOccupancy.Block.Marker);
                }
            }
        }

        // ---- flower beds flanking the garden path
        if (walk != null && walk.Length >= 2)
        {
            foreach (float side in new[] { -1.3f, 1.3f })
                for (int i = 1; i < walk.Length; i++)
                {
                    Vector3 a = TownLifeGeo.Flat(walk[i - 1]), b = TownLifeGeo.Flat(walk[i]); float len = TownLifeGeo.Dist(a, b); if (len < 2.5f) continue;
                    var dir = (b - a) / len; var off = TownLifeGeo.Right(dir) * side;
                    foreach (var piece in Pieces(a + off, b + off, 0.5f, 1.8f,
                                 p => { float d = lot.L(p).y; return d > 5.6f && d < lot.front - 1.5f && TownLifeGeo.PolygonContains(lot.outline, p) && FlatOk(p, 0.4f) && driveDist(p) > 0.6f && !occ.Within(p, 0.4f, TownLifeTags.CurbStop) && PolylineDist(walk, p) > 1.15f; }))
                    {
                        PlantRun(lot, "pathbed", piece, "kit.panel.flowerborder." + flower, false);
                        occ.ReserveLine(piece[0], piece[1], 0.35f, TagBed, TownLifeOccupancy.Block.Marker);
                    }
                }
        }

        // ---- front hedge / border along the back of the public tree strip (Acres; Meadows keeps its pickets)
        if (lot.acres)
        {
            var used = new HashSet<int>();
            foreach (int nb in lot.par.neighbours) if (hedgeKind.TryGetValue(nb, out int t)) used.Add(t);
            int roll = rng.Next(100); int kind = roll < 50 ? 0 : roll < 72 ? 1 : roll < 88 ? 2 : 3;
            if (used.Contains(kind) && kind != 0) kind = 0;
            hedgeKind[lot.par.index] = kind;
            if (kind != 3)
            {
                string panel = kind == 0 ? "kit.panel.hedge.low" : kind == 1 ? "kit.panel.flowerborder." + flower : "kit.panel.picket.white";
                const float depth = 5.0f;
                foreach (var piece in Pieces(lot.W(lot.minX - 1f, depth), lot.W(lot.maxX + 1f, depth), 0.5f, 2f,
                             p => TownLifeGeo.PolygonContains(lot.outline, p) && occ.Free(p, 0.36f) && !occ.Within(p, 0.4f, TownLifeTags.LotItem) && driveDist(p) > 0.9f))
                {
                    PlantRun(lot, "fronthedge", piece, panel, kind == 0, kind == 2 ? "kit.post.white" : null);
                    occ.ReserveLine(piece[0], piece[1], 0.4f, TagHedge, TownLifeOccupancy.Block.Marker);
                }
            }
        }
    }

    // ================================================================================================= props

    static void Roof(TownLifeKit.Recipe r, string mat, float y, float span, float rise, float zlen, float thick = 0.1f)
    {
        float half = span * 0.5f, slope = Mathf.Sqrt(half * half + rise * rise), ang = Mathf.Atan2(rise, half) * Mathf.Rad2Deg;
        r.Box(mat, V(-half * 0.5f, y + rise * 0.5f + thick * 0.5f, 0f), V(slope + 0.08f, thick, zlen), V(0f, 0f, ang));
        r.Box(mat, V(half * 0.5f, y + rise * 0.5f + thick * 0.5f, 0f), V(slope + 0.08f, thick, zlen), V(0f, 0f, -ang));
    }

    static void DefineAll()
    {
        var K = (Func<string, TownLifeKit.Recipe>)TownLifeKit.DefineProp;
        TownLifeKit.Define("yards.lawn.0", new Color(0.43f, 0.62f, 0.28f), 0.02f);
        TownLifeKit.Define("yards.lawn.1", new Color(0.38f, 0.58f, 0.29f), 0.02f);
        TownLifeKit.Define("yards.lawn.2", new Color(0.46f, 0.63f, 0.31f), 0.02f);
        TownLifeKit.Define("yards.drive", new Color(0.70f, 0.65f, 0.59f), 0.05f);
        TownLifeKit.Define("yards.glass", new Color(0.70f, 0.90f, 0.93f), 0.8f);
        TownLifeKit.Define("yards.shed.red", new Color(0.80f, 0.32f, 0.27f));
        TownLifeKit.Define("yards.shed.blue", new Color(0.38f, 0.60f, 0.80f));
        TownLifeKit.Define("yards.shed.green", new Color(0.44f, 0.68f, 0.47f));
        TownLifeKit.Define("yards.shed.yellow", new Color(0.97f, 0.82f, 0.40f));
        TownLifeKit.Define("yards.roof.dark", new Color(0.31f, 0.31f, 0.36f));
        TownLifeKit.Define("yards.roof.red", new Color(0.70f, 0.32f, 0.26f));
        TownLifeKit.Define("yards.veg.light", new Color(0.62f, 0.83f, 0.45f));
        TownLifeKit.Define("yards.dog", new Color(0.86f, 0.63f, 0.36f));
        if (TownLifeKit.HasProp("yards.shed.red")) return;

        // ---- small parts baked into the composites
        K("yards.chair").Box("paint.white", V(0f, 0.43f, 0f), V(0.44f, 0.05f, 0.44f)).Box("paint.white", V(0f, 0.72f, -0.2f), V(0.44f, 0.5f, 0.05f))
            .Box("paint.white", V(-0.18f, 0.21f, 0f), V(0.05f, 0.42f, 0.4f)).Box("paint.white", V(0.18f, 0.21f, 0f), V(0.05f, 0.42f, 0.4f)).NoShadows();
        foreach (var col in Cols)
        {
            string m = "paint." + col;
            K("yards.parasol." + col).Cyl("wood.pale", Vector3.zero, 0.04f, 2.35f, 6).Cone(m, V(0f, 1.95f, 0f), 1.5f, 0.55f, 8).Cone("paint.white", V(0f, 1.94f, 0f), 1.5f, 0.5f, 8, V(0f, 22.5f, 0f))
                .Sphere("paint.white", V(0f, 2.5f, 0f), V(0.07f, 0.07f, 0.07f), 6, 4);
            K("yards.deckchair." + col).Bar("wood.pale", V(-0.32f, 0.02f, 0.55f), V(-0.32f, 1.0f, -0.5f), 0.05f).Bar("wood.pale", V(0.32f, 0.02f, 0.55f), V(0.32f, 1.0f, -0.5f), 0.05f)
                .Bar("wood.pale", V(-0.32f, 0.02f, -0.45f), V(-0.32f, 0.55f, 0.25f), 0.05f).Bar("wood.pale", V(0.32f, 0.02f, -0.45f), V(0.32f, 0.55f, 0.25f), 0.05f)
                .Bar("wood.pale", V(-0.32f, 1.0f, -0.5f), V(0.32f, 1.0f, -0.5f), 0.05f).Bar("wood.pale", V(-0.32f, 0.3f, 0.3f), V(0.32f, 0.3f, 0.3f), 0.05f)
                .Sheet(m, V(-0.28f, 0.98f, -0.47f), V(0.28f, 0.98f, -0.47f), V(0.28f, 0.33f, 0.28f), V(-0.28f, 0.33f, 0.28f))
                .Sheet("paint.white", V(-0.08f, 0.985f, -0.465f), V(0.08f, 0.985f, -0.465f), V(0.08f, 0.335f, 0.285f), V(-0.08f, 0.335f, 0.285f)).NoShadows();
        }
        K("yards.bbq").Bar("metal.dark", V(-0.22f, 0f, -0.2f), V(-0.1f, 0.72f, -0.08f), 0.035f).Bar("metal.dark", V(0.22f, 0f, -0.2f), V(0.1f, 0.72f, -0.08f), 0.035f).Bar("metal.dark", V(0f, 0f, 0.28f), V(0f, 0.72f, 0.1f), 0.035f)
            .Sphere("paint.red", V(0f, 0.9f, 0f), V(0.34f, 0.26f, 0.34f), 10, 6).Cyl("metal.dark", V(0f, 0.88f, 0f), 0.345f, 0.03f, 10).Sphere("metal.dark", V(0f, 1.18f, 0f), V(0.05f, 0.04f, 0.05f), 6, 4)
            .Box("wood.pale", V(0.5f, 0.82f, 0f), V(0.34f, 0.03f, 0.3f)).NoShadows();
        K("yards.dog").Sphere("yards.dog", V(0f, 0.3f, -0.05f), V(0.2f, 0.27f, 0.27f), 8, 5).Sphere("yards.dog", V(0f, 0.68f, 0.14f), V(0.2f, 0.18f, 0.2f), 8, 5)
            .Sphere("paint.cream", V(0f, 0.62f, 0.32f), V(0.11f, 0.09f, 0.12f), 6, 4).Sphere("eye.black", V(0f, 0.66f, 0.43f), V(0.04f, 0.035f, 0.035f), 5, 3)
            .Sphere("bark.dark", V(-0.2f, 0.66f, 0.1f), V(0.05f, 0.15f, 0.09f), 5, 3).Sphere("bark.dark", V(0.2f, 0.66f, 0.1f), V(0.05f, 0.15f, 0.09f), 5, 3)
            .Bar("yards.dog", V(0f, 0.2f, -0.28f), V(0.06f, 0.5f, -0.42f), 0.06f).Cyl("yards.dog", V(-0.1f, 0f, 0.16f), 0.05f, 0.34f, 6).Cyl("yards.dog", V(0.1f, 0f, 0.16f), 0.05f, 0.34f, 6)
            .Box("flower.pink", V(0f, 0.56f, 0.4f), V(0.06f, 0.02f, 0.1f)).Box("paint.red", V(0f, 0.5f, 0.1f), V(0.3f, 0.05f, 0.3f)).Add("kit.googly", V(0f, 0.75f, 0.3f), 0f, 0.45f).NoShadows();
        {
            var r = K("yards.woodstack");
            for (int row = 0; row < 3; row++) for (int c = 0; c < 5 - row; c++) r.Cyl(c % 2 == 0 ? "wood.warm" : "wood.pale", V(-0.64f + 0.16f * row + 0.32f * c, 0.15f + 0.27f * row, -0.3f), 0.14f, 0.6f, 6, -1f, V(90f, 0f, 0f));
            r.NoShadows();
        }
        K("yards.bike").Torus("metal.dark", V(0f, 0.33f, -0.5f), 0.3f, 0.035f, 12, 4, V(0f, 0f, 90f)).Torus("metal.dark", V(0f, 0.33f, 0.5f), 0.3f, 0.035f, 12, 4, V(0f, 0f, 90f))
            .Bar("paint.red", V(0f, 0.33f, -0.5f), V(0f, 0.8f, -0.2f), 0.045f).Bar("paint.red", V(0f, 0.8f, -0.2f), V(0f, 0.76f, 0.35f), 0.045f).Bar("paint.red", V(0f, 0.76f, 0.35f), V(0f, 0.33f, 0.5f), 0.045f)
            .Bar("paint.red", V(0f, 0.33f, 0f), V(0f, 0.8f, -0.2f), 0.045f).Bar("paint.red", V(0f, 0.33f, 0f), V(0f, 0.76f, 0.35f), 0.045f).Bar("paint.red", V(0f, 0.33f, 0f), V(0f, 0.33f, -0.5f), 0.04f)
            .Box("metal.dark", V(0f, 0.88f, -0.25f), V(0.12f, 0.05f, 0.26f)).Bar("metal.dark", V(-0.24f, 0.98f, 0.36f), V(0.24f, 0.98f, 0.36f), 0.04f).Bar("metal.dark", V(0f, 0.76f, 0.35f), V(0f, 0.98f, 0.36f), 0.04f).NoShadows();

        // ---- sheds
        foreach (var col in ShedCols)
        {
            string wall = col == "white" ? "paint.cream" : "yards.shed." + col, roof = col == "red" || col == "white" ? "yards.roof.dark" : col == "yellow" ? "yards.roof.red" : "yards.roof.dark";
            var r = K("yards.shed." + col).Box(wall, V(0f, 0.95f, 0f), V(2.4f, 1.9f, 2.0f)).Gable(wall, V(0f, 1.9f, 0f), V(2.4f, 0.68f, 2.0f));
            Roof(r, roof, 1.86f, 2.9f, 0.82f, 2.5f);
            r.Box("paint.white", V(-0.45f, 0.86f, 1.0f), V(0.8f, 1.72f, 0.07f)).Box("wood.dark", V(-0.15f, 0.9f, 1.04f), V(0.06f, 0.14f, 0.03f))
                .Box("paint.white", V(0.62f, 1.2f, 1.0f), V(0.72f, 0.66f, 0.05f)).Box("yards.glass", V(0.62f, 1.2f, 1.0f), V(0.6f, 0.54f, 0.08f)).Box("paint.white", V(0.62f, 0.86f, 1.05f), V(0.78f, 0.06f, 0.14f))
                .Box("paint.white", V(-1.2f, 0.95f, 1.0f), V(0.08f, 1.9f, 0.08f)).Box("paint.white", V(1.2f, 0.95f, 1.0f), V(0.08f, 1.9f, 0.08f))
                .Add("kit.planter.tub", V(0.75f, 0f, 1.5f), 0f, 0.7f).Bar("wood.pale", V(1.34f, 0f, 0.6f), V(1.24f, 1.7f, 0.3f), 0.05f).Box("metal.dark", V(1.34f, 0.06f, 0.62f), V(0.05f, 0.12f, 0.36f))
                .ColliderBox(V(0f, 1f, 0f), V(2.4f, 2f, 2f));
        }

        // ---- greenhouse
        {
            var r = K("yards.greenhouse").Box("brick", V(0f, 0.2f, 0f), V(2.6f, 0.4f, 3.8f)).Box("yards.glass", V(0f, 1.05f, 0f), V(2.5f, 1.3f, 3.7f)).Gable("yards.glass", V(0f, 1.7f, 0f), V(2.56f, 0.8f, 3.76f));
            foreach (float x in new[] { -1.27f, 1.27f })
            {
                foreach (float z in new[] { -1.87f, -0.62f, 0.62f, 1.87f }) { r.Box("paint.white", V(x, 1.05f, z), V(0.08f, 1.3f, 0.08f)); r.Bar("paint.white", V(x, 1.7f, z), V(0f, 2.52f, z), 0.07f); }
                r.Box("paint.white", V(x, 1.7f, 0f), V(0.09f, 0.09f, 3.84f));
            }
            r.Box("paint.white", V(0f, 2.52f, 0f), V(0.09f, 0.09f, 3.86f)).Box("paint.white", V(0f, 1.02f, 1.86f), V(0.9f, 1.64f, 0.06f)).Box("yards.glass", V(0f, 1.02f, 1.88f), V(0.74f, 1.48f, 0.06f))
                .Blob("leaf.mid", V(-0.6f, 0.75f, 1.2f), V(0.35f, 0.4f, 0.35f), 5).Blob("leaf.lime", V(0.6f, 0.7f, -0.6f), V(0.35f, 0.4f, 0.4f), 6)
                .Add("kit.shrub.cone", V(0.95f, 0f, 2.3f), 0f, 0.7f).Cyl("terracotta", V(-0.9f, 0f, 2.25f), 0.2f, 0.3f, 8, 0.26f).Blob("flower.red", V(-0.9f, 0.42f, 2.25f), V(0.24f, 0.18f, 0.24f), 7)
                .ColliderBox(V(0f, 1.1f, 0f), V(2.6f, 2.2f, 3.8f));
        }

        // ---- vegetable plots
        {
            var r = K("yards.vegplot.a");
            for (int i = 0; i < 3; i++)
            {
                float x = -1.5f + 1.5f * i;
                r.Box("wood.warm", V(x, 0.14f, 0f), V(1.25f, 0.28f, 3.1f)).Box("soil", V(x, 0.29f, 0f), V(1.05f, 0.04f, 2.9f));
                if (i == 0) for (int k = 0; k < 5; k++) r.Sphere(k % 2 == 0 ? "yards.veg.light" : "leaf.lime", V(x, 0.42f, -1.15f + k * 0.58f), V(0.27f, 0.19f, 0.27f), 6, 3);
                else if (i == 1) for (int k = 0; k < 8; k++) { r.Cone("leaf.lime", V(x - 0.24f, 0.3f, -1.26f + k * 0.36f), 0.11f, 0.36f, 5); r.Cone("leaf.mid", V(x + 0.24f, 0.3f, -1.26f + k * 0.36f), 0.12f, 0.3f, 5); if (k % 3 == 0) r.Cone("flower.orange", V(x + 0.24f, 0.3f, -1.08f + k * 0.36f), 0.05f, 0.14f, 5); }
                else for (int k = 0; k < 4; k++)
                {
                    float z = -1.1f + k * 0.73f;
                    r.Cyl("wood.pale", V(x, 0.3f, z), 0.025f, 1.05f, 4).Blob("leaf.mid", V(x, 0.82f, z), V(0.3f, 0.42f, 0.3f), 11 + k)
                        .Sphere("fruit.red", V(x + 0.24f, 0.75f, z + 0.1f), V(0.08f, 0.08f, 0.08f), 5, 3).Sphere("fruit.red", V(x - 0.2f, 0.98f, z - 0.12f), V(0.08f, 0.08f, 0.08f), 5, 3).Sphere("fruit.red", V(x + 0.05f, 0.6f, z - 0.26f), V(0.07f, 0.07f, 0.07f), 5, 3);
                }
            }
            r.NoShadows();
            r = K("yards.vegplot.b");
            r.Box("wood.warm", V(-1.0f, 0.14f, 0f), V(1.25f, 0.28f, 3.1f)).Box("soil", V(-1.0f, 0.29f, 0f), V(1.05f, 0.04f, 2.9f));
            for (int k = 0; k < 5; k++) { r.Sphere("yards.veg.light", V(-1.24f, 0.4f, -1.15f + k * 0.58f), V(0.2f, 0.15f, 0.2f), 6, 3); r.Sphere("flower.purple", V(-0.76f, 0.42f, -1.15f + k * 0.58f), V(0.2f, 0.17f, 0.2f), 6, 3); }
            r.Box("wood.warm", V(0.4f, 0.14f, 0f), V(1.25f, 0.28f, 3.1f)).Box("soil", V(0.4f, 0.29f, 0f), V(1.05f, 0.04f, 2.9f));
            foreach (float z in new[] { -0.8f, 0.8f })
            {
                for (int k = 0; k < 4; k++) { float ang = k * 90f * Mathf.Deg2Rad + 0.6f; r.Bar("wood.pale", V(0.4f + Mathf.Cos(ang) * 0.42f, 0.3f, z + Mathf.Sin(ang) * 0.42f), V(0.4f, 1.95f, z), 0.04f); }
                r.Blob("leaf.mid", V(0.4f, 0.85f, z), V(0.36f, 0.55f, 0.36f), z > 0f ? 21 : 22).Sphere("flower.red", V(0.62f, 1.0f, z + 0.2f), V(0.07f, 0.07f, 0.07f), 5, 3).Sphere("flower.red", V(0.2f, 1.2f, z - 0.15f), V(0.07f, 0.07f, 0.07f), 5, 3);
            }
            r.Cyl("soil", V(1.42f, 0f, 0.2f), 0.55f, 0.07f, 8).Sphere("flower.orange", V(1.4f, 0.3f, 0.45f), V(0.32f, 0.25f, 0.32f), 8, 5).Sphere("flower.orange", V(1.55f, 0.22f, -0.15f), V(0.22f, 0.17f, 0.22f), 8, 5)
                .Blob("leaf.mid", V(1.2f, 0.12f, -0.1f), V(0.4f, 0.1f, 0.4f), 23).Cyl("leaf.dark", V(1.4f, 0.52f, 0.45f), 0.03f, 0.1f, 5).NoShadows();
        }

        // ---- patios, deck
        foreach (var col in Cols)
        {
            var r = K("yards.patio.dining." + col).Box("paving.warm", V(0f, 0.045f, 0f), V(5.0f, 0.09f, 4.0f));
            for (int i = 1; i < 5; i++) r.Box("stone.light", V(-2.5f + i * 1.0f, 0.092f, 0f), V(0.04f, 0.004f, 4.0f));
            for (int i = 1; i < 4; i++) r.Box("stone.light", V(0f, 0.092f, -2f + i * 1.0f), V(5.0f, 0.004f, 0.04f));
            r.Cyl("wood.pale", V(-0.7f, 0.8f, 0f), 0.72f, 0.06f, 10).Cyl("wood.dark", V(-0.7f, 0.09f, 0f), 0.07f, 0.72f, 6)
                .Add("yards.chair", V(0.4f, 0.09f, 0f), -90f).Add("yards.chair", V(-1.8f, 0.09f, 0f), 90f).Add("yards.chair", V(-0.7f, 0.09f, 1.1f), 180f).Add("yards.chair", V(-0.7f, 0.09f, -1.1f), 0f)
                .Add("yards.parasol." + col, V(-0.7f, 0.09f, 0f)).Add("yards.bbq", V(1.75f, 0.09f, -1.2f), -30f).Add("kit.planter.tub", V(2.0f, 0.09f, 1.45f)).Add("kit.planter.tub", V(-2.05f, 0.09f, -1.5f), 0f, 0.8f)
                .Cyl("paint.white", V(-0.9f, 0.86f, 0.25f), 0.12f, 0.02f, 8).Cyl("paint." + col, V(-0.4f, 0.86f, -0.2f), 0.05f, 0.13f, 6);
            r = K("yards.loungers." + col).Add("yards.deckchair." + col, V(-0.85f, 0f, 0.1f), 12f).Add("yards.deckchair." + col, V(0.85f, 0f, 0.1f), -12f).Add("yards.parasol." + col, V(0f, 0f, -0.6f))
                .Cyl("paint.white", V(0f, 0.36f, 0.35f), 0.26f, 0.04f, 8).Cyl("paint.white", V(0f, 0f, 0.35f), 0.04f, 0.36f, 6).Cyl("paint.orange", V(0.06f, 0.4f, 0.33f), 0.05f, 0.15f, 6)
                .Box(col == "teal" ? "paint.pink" : "paint.teal", V(1.85f, 0.012f, 0.2f), V(0.7f, 0.024f, 1.5f), V(0f, 12f, 0f));
        }
        {
            var r = K("yards.patio.round").Cyl("paving.red", Vector3.zero, 2.35f, 0.09f, 16).Cyl("paving.warm", V(0f, 0.004f, 0f), 1.2f, 0.09f, 12)
                .Cyl("metal.dark", V(0f, 0.09f, 0f), 0.4f, 0.32f, 8, 0.52f).Cone("paint.orange", V(0f, 0.36f, 0f), 0.26f, 0.6f, 6).Cone("paint.yellow", V(0.05f, 0.36f, 0.04f), 0.14f, 0.4f, 5);
            string[] cc = { "red", "blue", "yellow" };
            for (int i = 0; i < 3; i++) { float ang = (30f + i * 120f) * Mathf.Deg2Rad; var p = V(Mathf.Cos(ang) * 1.55f, 0.09f, Mathf.Sin(ang) * 1.55f); r.Add("yards.deckchair." + cc[i], p, TownLifeGeo.Yaw(-p)); }
            r.Add("kit.planter.tub", V(-1.7f, 0.09f, -1.0f), 0f, 0.8f);
            r = K("yards.deck").Box("wood.warm", V(0f, 0.07f, 0f), V(4.8f, 0.14f, 3.6f));
            for (int i = 1; i < 8; i++) r.Box("wood.dark", V(-2.4f + 0.6f * i, 0.142f, 0f), V(0.035f, 0.006f, 3.6f));
            r.Box("wood.dark", V(0f, 0.05f, 1.83f), V(4.8f, 0.1f, 0.06f)).Add("kit.bench.simple", V(-1.1f, 0.14f, -1.4f)).Add("yards.parasol.blue", V(1.3f, 0.14f, 0.1f)).Add("yards.deckchair.yellow", V(1.2f, 0.14f, 0.9f), -160f)
                .Add("yards.deckchair.red", V(0.3f, 0.14f, 0.4f), 150f).Add("kit.shrub.cone", V(-2.1f, 0.14f, 1.45f)).Add("kit.shrub.cone", V(2.1f, 0.14f, 1.45f)).Add("yards.bbq", V(-1.9f, 0.14f, 0.2f), 60f);
            // a little paved stoop with two chairs for yards that have room for nothing else
            K("yards.patio.small").Box("paving.grey", V(0f, 0.045f, 0f), V(3.0f, 0.09f, 2.4f)).Cyl("paint.white", V(0f, 0.6f, 0f), 0.35f, 0.04f, 8).Cyl("paint.white", V(0f, 0.09f, 0f), 0.04f, 0.52f, 6)
                .Add("yards.chair", V(-0.8f, 0.09f, 0f), 80f).Add("yards.chair", V(0.8f, 0.09f, 0.1f), -100f).Add("kit.planter.tub", V(1.1f, 0.09f, -0.8f), 0f, 0.75f).Cyl("flower.yellow", V(0.05f, 0.64f, 0f), 0.06f, 0.14f, 6);
        }

        // ---- lazy lawn
        K("yards.paddling").Cyl("water", Vector3.zero, 1.15f, 0.26f, 14).Torus("paint.blue", V(0f, 0.14f, 0f), 1.2f, 0.16f, 14, 6).Torus("paint.yellow", V(0f, 0.36f, 0f), 1.2f, 0.13f, 14, 6)
            .Add("kit.duck", V(0.3f, 0.26f, 0.2f), 40f, 0.9f).Torus("paint.pink", V(-0.45f, 0.3f, -0.3f), 0.32f, 0.1f, 10, 5)
            .Sphere("paint.red", V(1.75f, 0.24f, 0.6f), V(0.24f, 0.24f, 0.24f), 8, 5).Torus("paint.white", V(1.75f, 0.24f, 0.6f), 0.225f, 0.05f, 8, 4)
            .Box("paint.yellow", V(-1.75f, 0.012f, 0.5f), V(0.7f, 0.024f, 1.4f), V(0f, -20f, 0f)).NoShadows();
        K("yards.hammock").Cyl("wood.dark", V(-1.8f, 0f, 0f), 0.07f, 1.5f, 6).Cyl("wood.dark", V(1.8f, 0f, 0f), 0.07f, 1.5f, 6)
            .Sheet("paint.red", V(-1.75f, 1.4f, -0.05f), V(-1.75f, 1.4f, 0.05f), V(-1.0f, 0.82f, 0.42f), V(-1.0f, 0.82f, -0.42f))
            .Sheet("paint.white", V(-1.0f, 0.82f, -0.42f), V(-1.0f, 0.82f, 0.42f), V(-0.35f, 0.62f, 0.45f), V(-0.35f, 0.62f, -0.45f))
            .Sheet("paint.red", V(-0.35f, 0.62f, -0.45f), V(-0.35f, 0.62f, 0.45f), V(0.35f, 0.62f, 0.45f), V(0.35f, 0.62f, -0.45f))
            .Sheet("paint.white", V(0.35f, 0.62f, -0.45f), V(0.35f, 0.62f, 0.45f), V(1.0f, 0.82f, 0.42f), V(1.0f, 0.82f, -0.42f))
            .Sheet("paint.red", V(1.0f, 0.82f, -0.42f), V(1.0f, 0.82f, 0.42f), V(1.75f, 1.4f, 0.05f), V(1.75f, 1.4f, -0.05f))
            .Sphere("paint.cream", V(-0.75f, 0.84f, 0f), V(0.26f, 0.1f, 0.3f), 6, 3);

        // ---- kids
        K("yards.sandbox").Box("wood.warm", V(0f, 0.12f, 1.05f), V(2.3f, 0.24f, 0.2f)).Box("wood.warm", V(0f, 0.12f, -1.05f), V(2.3f, 0.24f, 0.2f)).Box("wood.warm", V(1.05f, 0.12f, 0f), V(0.2f, 0.24f, 2.3f)).Box("wood.warm", V(-1.05f, 0.12f, 0f), V(0.2f, 0.24f, 2.3f))
            .Box("sand", V(0f, 0.09f, 0f), V(1.95f, 0.18f, 1.95f)).Cyl("sand", V(-0.3f, 0.18f, 0.2f), 0.26f, 0.22f, 6, 0.19f).Cone("sand", V(-0.3f, 0.4f, 0.2f), 0.14f, 0.2f, 6)
            .Cyl("wood.pale", V(-0.3f, 0.55f, 0.2f), 0.012f, 0.3f, 4).Sheet("paint.red", V(-0.3f, 0.85f, 0.2f), V(-0.3f, 0.72f, 0.2f), V(-0.08f, 0.78f, 0.2f), V(-0.08f, 0.78f, 0.2f))
            .Cyl("paint.red", V(0.5f, 0.18f, -0.3f), 0.11f, 0.22f, 8, 0.15f).Bar("paint.blue", V(0.2f, 0.2f, -0.6f), V(0.6f, 0.32f, -0.7f), 0.04f).Box("paint.yellow", V(0.95f, 0.26f, 0.95f), V(0.5f, 0.04f, 0.5f), V(0f, 45f, 0f)).NoShadows();
        {
            var r = K("yards.playhouse").Box("paint.yellow", V(0f, 0.75f, 0f), V(1.8f, 1.5f, 1.8f)).Gable("paint.yellow", V(0f, 1.5f, 0f), V(1.8f, 0.72f, 1.8f));
            Roof(r, "paint.red", 1.47f, 2.3f, 0.9f, 2.2f, 0.09f);
            r.Box("paint.blue", V(-0.4f, 0.6f, 0.9f), V(0.6f, 1.2f, 0.06f)).Box("paint.white", V(0.42f, 0.95f, 0.9f), V(0.6f, 0.6f, 0.05f)).Box("yards.glass", V(0.42f, 0.95f, 0.9f), V(0.48f, 0.48f, 0.08f))
                .Box("wood.warm", V(0.42f, 0.6f, 0.98f), V(0.62f, 0.12f, 0.14f)).Sphere("flower.pink", V(0.25f, 0.72f, 0.98f), V(0.1f, 0.09f, 0.1f), 5, 3).Sphere("flower.white", V(0.45f, 0.74f, 0.98f), V(0.1f, 0.09f, 0.1f), 5, 3).Sphere("flower.pink", V(0.62f, 0.72f, 0.98f), V(0.1f, 0.09f, 0.1f), 5, 3)
                .Box("brick", V(0.5f, 2.05f, -0.4f), V(0.26f, 0.5f, 0.26f)).Cyl("wood.pale", V(-0.6f, 2.2f, 0f), 0.015f, 0.6f, 4).Sheet("paint.teal", V(-0.6f, 2.8f, 0f), V(-0.6f, 2.55f, 0f), V(-0.2f, 2.67f, 0f), V(-0.2f, 2.67f, 0f))
                .ColliderBox(V(0f, 0.8f, 0f), V(1.8f, 1.6f, 1.8f));
        }

        // ---- dog yard
        {
            var r = K("yards.kennel").Box("wood.pale", V(-0.5f, 0.4f, -0.3f), V(0.9f, 0.8f, 1.1f)).Gable("wood.pale", V(-0.5f, 0.8f, -0.3f), V(0.9f, 0.36f, 1.1f));
            r.Box("paint.red", V(-0.76f, 1.0f, -0.3f), V(0.72f, 0.07f, 1.34f), V(0f, 0f, 38.6f)).Box("paint.red", V(-0.24f, 1.0f, -0.3f), V(0.72f, 0.07f, 1.34f), V(0f, 0f, -38.6f));
            r.Box("bark.dark", V(-0.5f, 0.3f, 0.255f), V(0.46f, 0.6f, 0.03f)).Box("paint.white", V(-0.5f, 0.72f, 0.26f), V(0.4f, 0.12f, 0.03f))
                .Add("yards.dog", V(0.5f, 0f, 0.15f), 20f).Cyl("paint.blue", V(0.25f, 0f, 0.95f), 0.2f, 0.1f, 8, 0.16f).Cyl("bark.dark", V(0.25f, 0.09f, 0.95f), 0.14f, 0.02f, 8)
                .Bar("paint.white", V(0.85f, 0.05f, 0.95f), V(1.15f, 0.05f, 0.85f), 0.07f).Sphere("paint.white", V(0.83f, 0.05f, 0.96f), V(0.07f, 0.06f, 0.07f), 5, 3).Sphere("paint.white", V(1.17f, 0.05f, 0.84f), V(0.07f, 0.06f, 0.07f), 5, 3)
                .Sphere("paint.yellow", V(-1.0f, 0.13f, 0.9f), V(0.13f, 0.13f, 0.13f), 7, 4).ColliderBox(V(-0.5f, 0.5f, -0.3f), V(0.9f, 1f, 1.1f));
            r = K("yards.dogholes");
            float[] hx = { -0.9f, 0.3f, 0.9f }, hz = { -0.3f, 0.5f, -0.6f };
            for (int i = 0; i < 3; i++) r.Cyl("soil", V(hx[i], 0f, hz[i]), 0.38f, 0.07f, 8).Blob("mulch", V(hx[i] + 0.45f, 0.08f, hz[i] + 0.1f), V(0.36f, 0.2f, 0.3f), 31 + i);
            r.Bar("paint.white", V(-0.9f, 0.05f, -0.3f), V(-0.8f, 0.4f, -0.2f), 0.07f).Sphere("paint.white", V(-0.79f, 0.42f, -0.19f), V(0.08f, 0.07f, 0.08f), 5, 3)
                .Sphere("paint.red", V(-0.2f, 0.12f, 0.9f), V(0.12f, 0.12f, 0.12f), 7, 4).Bar("bark", V(0.9f, 0.05f, 0.6f), V(1.4f, 0.05f, 0.9f), 0.07f).NoShadows();
        }

        // ---- washing line
        {
            var r = K("yards.washline");
            foreach (float x in new[] { -2.5f, 2.5f }) r.Cyl("wood.pale", V(x, 0f, 0f), 0.055f, 2.15f, 6).Bar("wood.pale", V(x, 2.08f, -0.45f), V(x, 2.08f, 0.45f), 0.07f);
            foreach (float z in new[] { -0.38f, 0.38f }) r.Bar("paint.white", V(-2.5f, 2.05f, z), V(2.5f, 2.05f, z), 0.025f);
            // x0, width, drop, material, line
            object[][] wash = {
                new object[] { -2.1f, 0.6f, 0.7f, "paint.red", 0.38f }, new object[] { -1.3f, 0.85f, 1.1f, "paint.white", 0.38f }, new object[] { -0.2f, 0.5f, 0.95f, "paint.blue", 0.38f }, new object[] { 0.5f, 0.6f, 0.7f, "paint.yellow", 0.38f },
                new object[] { 1.35f, 0.16f, 0.36f, "paint.pink", 0.38f }, new object[] { 1.62f, 0.16f, 0.36f, "paint.pink", 0.38f }, new object[] { 1.95f, 0.36f, 0.5f, "paint.teal", 0.38f },
                new object[] { -2.2f, 1.7f, 1.3f, "paint.teal", -0.38f }, new object[] { -0.2f, 0.75f, 1.0f, "paint.orange", -0.38f }, new object[] { 0.8f, 0.6f, 0.72f, "paint.purple", -0.38f }, new object[] { 1.6f, 0.6f, 0.72f, "paint.white", -0.38f } };
            foreach (var wsh in wash)
            {
                float x0 = (float)wsh[0], wd = (float)wsh[1], drop = (float)wsh[2], z = (float)wsh[4]; string m = (string)wsh[3];
                r.Sheet(m, V(x0, 2.05f, z), V(x0 + wd, 2.05f, z), V(x0 + wd, 2.05f - drop, z + 0.06f), V(x0, 2.05f - drop, z + 0.06f));
            }
            r.Cyl("wood.pale", V(1.3f, 0f, 1.1f), 0.3f, 0.3f, 8, 0.36f).Blob("paint.white", V(1.3f, 0.34f, 1.1f), V(0.3f, 0.14f, 0.3f), 41);
        }

        // ---- camp
        foreach (var col in new[] { "orange", "blue" })
        {
            var r = K("yards.camp." + col).Gable("paint." + col, V(0f, 0f, -1.1f), V(2.3f, 1.5f, 2.6f)).Sheet("bark.dark", V(-0.52f, 0f, 0.21f), V(0f, 1.05f, 0.21f), V(0.52f, 0f, 0.21f), V(0.52f, 0f, 0.21f))
                .Bar("wood.pale", V(0f, 0f, 0.22f), V(0f, 1.62f, 0.22f), 0.05f).Sheet("paint.white", V(0f, 1.9f, 0.22f), V(0f, 1.68f, 0.22f), V(0.34f, 1.79f, 0.22f), V(0.34f, 1.79f, 0.22f)).Bar("wood.pale", V(0f, 1.6f, 0.22f), V(0f, 1.92f, 0.22f), 0.03f)
                .Torus("stone.dark", V(0f, 0.1f, 1.6f), 0.48f, 0.13f, 8, 5).Bar("bark", V(-0.3f, 0.08f, 1.45f), V(0.3f, 0.16f, 1.75f), 0.12f).Bar("bark", V(0.3f, 0.08f, 1.45f), V(-0.3f, 0.16f, 1.75f), 0.12f)
                .Cone("paint.orange", V(0f, 0.12f, 1.6f), 0.24f, 0.6f, 6).Cone("paint.yellow", V(0.04f, 0.12f, 1.63f), 0.13f, 0.4f, 5)
                .Bar("bark", V(-1.3f, 0.2f, 1.2f), V(-1.0f, 0.2f, 2.3f), 0.38f).Bar("bark", V(1.0f, 0.2f, 2.3f), V(1.35f, 0.2f, 1.25f), 0.38f)
                .Box("paint.red", V(-0.9f, 0.012f, 0.7f), V(0.6f, 0.024f, 0.9f), V(0f, 25f, 0f));
        }

        // ---- orchard kit, garden stuff, bird table, sunflowers
        {
            var r = K("yards.orchardkit");
            foreach (float x in new[] { -0.28f, 0.28f }) r.Bar("wood.pale", V(x, 0f, 0.55f), V(x * 0.7f, 2.2f, 0f), 0.055f).Bar("wood.pale", V(x, 0f, -0.6f), V(x * 0.7f, 2.2f, 0f), 0.05f);
            for (int i = 1; i < 6; i++) { float t = i / 6.5f; r.Bar("wood.pale", V(-0.28f + 0.084f * t, 2.2f * t, 0.55f * (1f - t)), V(0.28f - 0.084f * t, 2.2f * t, 0.55f * (1f - t)), 0.045f); }
            r.Box("wood.warm", V(0.95f, 0.16f, 0.3f), V(0.62f, 0.32f, 0.46f));
            for (int i = 0; i < 6; i++) r.Sphere("fruit.red", V(0.76f + (i % 3) * 0.19f, 0.36f, 0.2f + (i / 3) * 0.2f), V(0.1f, 0.1f, 0.1f), 6, 4);
            r.Cyl("wood.pale", V(-0.95f, 0f, 0.5f), 0.22f, 0.24f, 8, 0.28f).Sphere("fruit.red", V(-0.95f, 0.26f, 0.5f), V(0.2f, 0.1f, 0.2f), 6, 3).Sphere("fruit.red", V(-0.5f, 0.09f, 0.95f), V(0.1f, 0.1f, 0.1f), 6, 4).NoShadows();

            r = K("yards.gardenstuff.a").Box("wood.dark", V(-0.7f, 0.45f, 0f), V(1.1f, 0.9f, 1.1f)).Box("mulch", V(-0.7f, 0.91f, 0f), V(0.96f, 0.04f, 0.96f)).Blob("leaf.lime", V(-0.6f, 0.98f, 0.1f), V(0.3f, 0.14f, 0.3f), 51);
            for (int i = 0; i < 3; i++) r.Box("wood.warm", V(-0.7f, 0.2f + i * 0.28f, 0.56f), V(1.14f, 0.16f, 0.03f));
            // wheelbarrow
            r.Box("paint.green", V(0.7f, 0.5f, 0.1f), V(0.6f, 0.26f, 0.9f), V(-8f, 0f, 0f)).Blob("soil", V(0.7f, 0.66f, 0.1f), V(0.26f, 0.14f, 0.38f), 52)
                .Cyl("metal.dark", V(0.62f, 0.2f, 0.7f), 0.2f, 0.16f, 10, -1f, V(0f, 0f, -90f)).Bar("wood.pale", V(0.45f, 0.55f, -0.3f), V(0.42f, 0.62f, -1.0f), 0.05f).Bar("wood.pale", V(0.95f, 0.55f, -0.3f), V(0.98f, 0.62f, -1.0f), 0.05f)
                .Bar("metal.dark", V(0.5f, 0f, -0.3f), V(0.5f, 0.4f, -0.2f), 0.04f).Bar("metal.dark", V(0.9f, 0f, -0.3f), V(0.9f, 0.4f, -0.2f), 0.04f)
                .Cyl("paint.blue", V(0.1f, 0f, 0.75f), 0.14f, 0.3f, 8).Bar("paint.blue", V(0.2f, 0.18f, 0.75f), V(0.48f, 0.34f, 0.78f), 0.05f).NoShadows();
            r = K("yards.gardenstuff.b").Add("yards.woodstack", V(-0.45f, 0f, 0f)).Cyl("bark", V(0.75f, 0f, 0.3f), 0.26f, 0.42f, 8).Cyl("wood.pale", V(0.75f, 0.42f, 0.3f), 0.25f, 0.015f, 8)
                .Bar("wood.pale", V(0.72f, 0.45f, 0.3f), V(1.05f, 1.0f, 0.2f), 0.045f).Box("metal.dark", V(0.72f, 0.5f, 0.3f), V(0.06f, 0.16f, 0.24f), V(0f, 0f, 30f))
                .Cyl("paint.green", V(1.0f, 0f, -0.45f), 0.3f, 0.85f, 10).Cyl("metal.dark", V(1.0f, 0.85f, -0.45f), 0.32f, 0.04f, 10).NoShadows();

            r = K("yards.birdtable").Cyl("wood.dark", Vector3.zero, 0.05f, 1.5f, 4).Box("wood.warm", V(0f, 1.52f, 0f), V(0.62f, 0.04f, 0.62f));
            foreach (float x in new[] { -0.26f, 0.26f }) foreach (float z in new[] { -0.26f, 0.26f }) r.Cyl("wood.dark", V(x, 1.54f, z), 0.02f, 0.26f, 4);
            r.Gable("paint.red", V(0f, 1.8f, 0f), V(0.8f, 0.28f, 0.8f)).Sphere("paint.blue", V(0.1f, 1.61f, 0.12f), V(0.07f, 0.07f, 0.1f), 6, 4).Sphere("paint.blue", V(0.1f, 1.69f, 0.19f), V(0.055f, 0.055f, 0.055f), 6, 4)
                .Add("kit.googly", V(0.1f, 1.71f, 0.235f), 0f, 0.18f).NoShadows();

            r = K("yards.sunflowers");
            float[] sh = { 1.7f, 2.0f, 1.55f, 1.9f, 1.65f };
            for (int i = 0; i < 5; i++)
            {
                float x = -1.1f + i * 0.55f, hgt = sh[i], z = (i % 2) * 0.12f;
                r.Spike("leaf.mid", V(x, 0f, z), V(x, hgt + 0.05f, z + 0.03f), 0.05f).Cyl("flower.yellow", V(x, hgt, z + 0.05f), 0.24f, 0.05f, 9, -1f, V(78f, 0f, 0f)).Cyl("bark.dark", V(x, hgt, z + 0.08f), 0.11f, 0.05f, 7, -1f, V(78f, 0f, 0f))
                    .Blob("leaf.mid", V(x + 0.14f, hgt * 0.5f, z), V(0.2f, 0.07f, 0.12f), 61 + i).Blob("leaf.light", V(x - 0.14f, hgt * 0.32f, z), V(0.2f, 0.07f, 0.12f), 71 + i);
            }
            r.Box("soil", V(0f, 0.03f, 0.05f), V(2.8f, 0.06f, 0.6f)).NoShadows();
        }

        K("yards.doorpots.a").Add("kit.shrub.cone", V(-1.25f, 0f, 0f)).Add("kit.shrub.cone", V(1.25f, 0f, 0f)).NoShadows();
        K("yards.doorpots.b").Add("kit.planter.tub", V(-1.25f, 0f, 0f), 0f, 0.85f).Add("kit.planter.tub", V(1.25f, 0f, 0f), 40f, 0.85f).NoShadows();

        // ---- centrepieces
        {
            var r = K("yards.pond").Cyl("water", Vector3.zero, 1.45f, 0.05f, 12).Cyl("water", V(1.0f, 0f, 0.5f), 0.95f, 0.05f, 10);
            for (int i = 0; i < 11; i++) { float ang = i * 0.571f + 0.2f; float rad = i >= 2 && i <= 4 ? 0f : 1.5f; var q = rad > 0f ? V(Mathf.Cos(ang) * rad, 0.06f, Mathf.Sin(ang) * rad) : V(1.0f + Mathf.Cos(ang - 0.9f) * 1.0f, 0.06f, 0.5f + Mathf.Sin(ang - 0.9f) * 1.0f); r.Blob(i % 3 == 0 ? "stone.dark" : i % 3 == 1 ? "stone.light" : "stone.moss", q, V(0.26f, 0.15f, 0.22f), 80 + i); }
            r.Add("kit.lilypad", V(-0.5f, 0.05f, 0.3f)).Add("kit.lilypad", V(0.9f, 0.05f, 0.7f), 60f, 0.8f).Add("kit.lilypad", V(0.1f, 0.05f, -0.7f), 130f, 0.9f).Add("kit.reeds", V(-1.3f, 0f, -0.9f), 0f, 0.8f).Add("kit.duck", V(0.4f, 0.05f, -0.1f), 200f, 0.7f)
                .Add("kit.gnome", V(-1.75f, 0f, 0.7f), 110f, 1.5f).Bar("wood.pale", V(-1.5f, 0.55f, 0.6f), V(-0.5f, 1.25f, 0.3f), 0.03f).Bar("paint.white", V(-0.5f, 1.25f, 0.3f), V(-0.45f, 0.08f, 0.28f), 0.012f).Sphere("paint.red", V(-0.45f, 0.08f, 0.28f), V(0.05f, 0.05f, 0.05f), 5, 3).NoShadows();
            foreach (var v in new[] { "a", "b" })
            {
                r = K("yards.roundbed." + v).Cyl("soil", Vector3.zero, 1.5f, 0.1f, 12).Torus("stone.light", V(0f, 0.08f, 0f), 1.5f, 0.11f, 12, 4);
                for (int i = 0; i < 6; i++) { float ang = i * Mathf.PI / 3f; r.Add(Prop("kit.flowers." + (v == "a" ? (i % 2 == 0 ? "red" : "yellow") : (i % 2 == 0 ? "pink" : "purple")), "kit.shrub.round"), V(Mathf.Cos(ang) * 0.95f, 0.1f, Mathf.Sin(ang) * 0.95f), i * 40f, 0.9f); }
                if (v == "a") r.Add("kit.birdbath", V(0f, 0.1f, 0f)); else r.Add("kit.shrub.cone", V(0f, 0.1f, 0f), 0f, 1.4f);
                r.NoShadows();
            }
            r = K("yards.goal");
            foreach (float x in new[] { -1.3f, 1.3f }) r.Cyl("paint.white", V(x, 0f, -1.2f), 0.045f, 1.5f, 6).Bar("paint.white", V(x, 1.5f, -1.2f), V(x, 0f, -2.1f), 0.05f).Bar("paint.white", V(x, 0.75f, -1.2f), V(x, 0.75f, -1.65f), 0.025f);
            r.Bar("paint.white", V(-1.3f, 1.5f, -1.2f), V(1.3f, 1.5f, -1.2f), 0.08f).Bar("paint.white", V(-1.3f, 0.03f, -2.1f), V(1.3f, 0.03f, -2.1f), 0.05f);
            for (int i = 1; i < 6; i++) r.Bar("paint.white", V(-1.3f + i * 0.433f, 1.5f, -1.2f), V(-1.3f + i * 0.433f, 0f, -2.1f), 0.02f);
            for (int i = 1; i < 4; i++) r.Bar("paint.white", V(-1.3f, 1.5f - i * 0.375f, -1.2f - i * 0.225f), V(1.3f, 1.5f - i * 0.375f, -1.2f - i * 0.225f), 0.02f);
            r.Sphere("paint.white", V(0.4f, 0.17f, 1.2f), V(0.17f, 0.17f, 0.17f), 8, 5).Sphere("eye.black", V(0.4f, 0.3f, 1.25f), V(0.07f, 0.04f, 0.07f), 5, 2).Sphere("eye.black", V(0.52f, 0.17f, 1.3f), V(0.05f, 0.07f, 0.05f), 5, 2)
                .Cone("paint.orange", V(-1.0f, 0f, 1.6f), 0.14f, 0.34f, 8).Cone("paint.orange", V(1.2f, 0f, 1.9f), 0.14f, 0.34f, 8).NoShadows();
            r = K("yards.bouncer");
            for (int i = 0; i < 6; i++) { float ang = i * Mathf.PI / 3f; r.Cyl("metal.dark", V(Mathf.Cos(ang) * 1.4f, 0f, Mathf.Sin(ang) * 1.4f), 0.045f, 0.62f, 5); if (i % 2 == 0) r.Cyl("metal.dark", V(Mathf.Cos(ang) * 1.55f, 0.6f, Mathf.Sin(ang) * 1.55f), 0.035f, 1.6f, 5).Sphere("paint.yellow", V(Mathf.Cos(ang) * 1.55f, 2.22f, Mathf.Sin(ang) * 1.55f), V(0.07f, 0.07f, 0.07f), 5, 3); }
            r.Cyl("metal.dark", V(0f, 0.58f, 0f), 1.32f, 0.04f, 14).Torus("paint.blue", V(0f, 0.63f, 0f), 1.45f, 0.15f, 14, 5).ColliderBox(V(0f, 0.35f, 0f), V(2.4f, 0.7f, 2.4f));
        }

        // ---- planted corners and tree rings
        K("yards.cornerbed.a").Cyl("mulch", Vector3.zero, 1.45f, 0.06f, 9).Add(Prop("kit.shrub.flower.pink", "kit.shrub.round"), V(-0.4f, 0.05f, 0.25f)).Add(Prop("kit.shrub.round.m", "kit.shrub.round"), V(0.55f, 0.05f, -0.45f), 40f, 0.85f)
            .Add(Prop("kit.flowers.yellow", "kit.shrub.round"), V(0.5f, 0.05f, 0.8f)).Add(Prop("kit.flowers.white", "kit.shrub.round"), V(-0.9f, 0.05f, -0.5f)).Add("kit.boulder.s", V(-0.2f, 0.05f, -1.0f)).NoShadows();
        K("yards.cornerbed.b").Cyl("mulch", Vector3.zero, 1.45f, 0.06f, 9).Add("kit.shrub.cone", V(-0.6f, 0.05f, -0.3f)).Add("kit.shrub.cone", V(0.6f, 0.05f, -0.3f), 0f, 0.85f).Add(Prop("kit.flowers.mixed", "kit.shrub.round"), V(0f, 0.05f, 0.6f), 0f, 1.2f)
            .Add(Prop("kit.flowers.red", "kit.shrub.round"), V(-0.9f, 0.05f, 0.5f)).Add(Prop("kit.flowers.purple", "kit.shrub.round"), V(0.9f, 0.05f, 0.5f)).NoShadows();
        K("yards.cornerbed.c").Cyl("mulch", Vector3.zero, 1.45f, 0.06f, 9).Add(Prop("kit.shrub.flower.purple", "kit.shrub.round"), V(0.3f, 0.05f, -0.3f), 0f, 1.1f).Add("kit.grass.tuft", V(-0.7f, 0.05f, 0.2f), 0f, 1.6f).Add("kit.grass.tuft", V(-0.3f, 0.05f, 0.9f), 80f, 1.3f)
            .Add("kit.grass.tuft", V(0.9f, 0.05f, 0.6f), 30f, 1.5f).Add("kit.boulder.m", V(-0.7f, 0.05f, -0.7f), 0f, 0.8f).Add(Prop("kit.flowers.red", "kit.shrub.round"), V(0.5f, 0.05f, 0.85f)).NoShadows();
        K("yards.treering").Cyl("mulch", Vector3.zero, 0.1f, 0.01f, 3).NoShadows();   // marker: tree rings available
        string[] ringCols = { "red", "yellow", "pink", "purple" };
        foreach (var col in ringCols)
        {
            var r = K("yards.treering." + col).Cyl("mulch", Vector3.zero, 1.35f, 0.06f, 10);
            for (int i = 0; i < 7; i++) { float ang = i * 0.9f + 0.3f; r.Add(Prop("kit.flowers." + col, "kit.shrub.round"), V(Mathf.Cos(ang) * 0.95f, 0.05f, Mathf.Sin(ang) * 0.95f), i * 50f, 0.8f); }
            r.NoShadows();
        }

        // ---- run panels (2 m along +Z)
        string[][] shrubMix = { new[] { "kit.shrub.round.s", "kit.shrub.flower.pink" }, new[] { "kit.shrub.flower.yellow", "kit.shrub.round.s" }, new[] { "kit.shrub.flower.purple", "kit.shrub.flower.white" } };
        for (int i = 0; i < 3; i++)
            K("yards.panel.shrubs." + "abc"[i]).Prism("mulch", new[] { new Vector2(-0.4f, 0f), new Vector2(-0.34f, 0.07f), new Vector2(0.34f, 0.07f), new Vector2(0.4f, 0f) }, 0f, 2f)
                .Add(Prop(shrubMix[i][0], "kit.shrub.round"), V(0f, 0.05f, 0.5f), 20f * i, 0.8f).Add(Prop(shrubMix[i][1], "kit.shrub.round"), V(0.03f, 0.05f, 1.5f), 70f, 0.72f).NoShadows();
        K("yards.panel.found.a").Prism("mulch", new[] { new Vector2(-0.5f, 0f), new Vector2(-0.42f, 0.07f), new Vector2(0.42f, 0.07f), new Vector2(0.5f, 0f) }, 0f, 2f)
            .Add(Prop("kit.shrub.round.m", "kit.shrub.round"), V(0f, 0.05f, 0.55f), 0f, 0.62f).Add(Prop("kit.flowers.pink", "kit.shrub.round"), V(0.05f, 0.05f, 1.5f), 0f, 0.8f).NoShadows();
        K("yards.panel.found.b").Prism("mulch", new[] { new Vector2(-0.5f, 0f), new Vector2(-0.42f, 0.07f), new Vector2(0.42f, 0.07f), new Vector2(0.5f, 0f) }, 0f, 2f)
            .Lathe("hedge", V(0f, 0.05f, 0.5f), new[] { new Vector2(0.3f, 0f), new Vector2(0.45f, 0.3f), new Vector2(0.34f, 0.85f), new Vector2(0.14f, 1.3f), new Vector2(0.03f, 1.5f) }, 8)
            .Add(Prop("kit.shrub.flower.white", "kit.shrub.round"), V(0f, 0.05f, 1.45f), 0f, 0.7f).NoShadows();
        K("yards.panel.found.c").Prism("mulch", new[] { new Vector2(-0.5f, 0f), new Vector2(-0.42f, 0.07f), new Vector2(0.42f, 0.07f), new Vector2(0.5f, 0f) }, 0f, 2f)
            .Add(Prop("kit.shrub.flower.purple", "kit.shrub.round"), V(0f, 0.05f, 0.55f), 0f, 0.8f).Add(Prop("kit.shrub.round.s", "kit.shrub.round"), V(0f, 0.05f, 1.45f), 30f, 1.0f).Add(Prop("kit.flowers.yellow", "kit.shrub.round"), V(0.15f, 0.05f, 1.0f), 0f, 0.6f).NoShadows();

        // ---- garages and carport (4 x 6 m, door on +Z)
        string[][] garages = { new[] { "cream", "paint.cream", "yards.roof.dark", "paint.white" }, new[] { "barn", "yards.shed.red", "yards.roof.dark", "paint.white" }, new[] { "wood", "wood.warm", "yards.roof.red", "paint.green" } };
        foreach (var gdef in garages)
        {
            var r = K("yards.garage." + gdef[0]).Box(gdef[1], V(0f, 1.25f, 0f), V(4f, 2.5f, 6f)).Gable(gdef[1], V(0f, 2.5f, 0f), V(4f, 1.1f, 6f));
            Roof(r, gdef[2], 2.44f, 4.6f, 1.27f, 6.5f, 0.12f);
            r.Box("paint.white", V(0f, 1.1f, 3.0f), V(3.1f, 2.2f, 0.06f)).Box(gdef[3], V(0f, 1.05f, 3.0f), V(2.9f, 2.1f, 0.1f));
            for (int k = 0; k < 3; k++) r.Box("stone.dark", V(0f, 0.55f + 0.52f * k, 3.052f), V(2.9f, 0.035f, 0.01f));
            if (gdef[0] == "barn") r.Bar("paint.white", V(-1.4f, 0.05f, 3.06f), V(1.4f, 2.05f, 3.06f), 0.1f).Bar("paint.white", V(1.4f, 0.05f, 3.06f), V(-1.4f, 2.05f, 3.06f), 0.1f);
            foreach (float x in new[] { -2.0f, 2.0f }) r.Box("paint.white", V(x, 1.5f, 0.3f), V(0.06f, 0.84f, 1.34f)).Box("yards.glass", V(x, 1.5f, 0.3f), V(0.09f, 0.7f, 1.2f));
            r.Box("paint.white", V(-2f, 1.25f, 3f), V(0.12f, 2.5f, 0.12f)).Box("paint.white", V(2f, 1.25f, 3f), V(0.12f, 2.5f, 0.12f)).Sphere("lamp.glow", V(0f, 2.75f, 3.08f), V(0.14f, 0.14f, 0.1f), 6, 4)
                .ColliderBox(V(0f, 1.5f, 0f), V(4f, 3f, 6f)).Occluder();
        }
        {
            var r = K("yards.carport");
            foreach (float x in new[] { -1.85f, 1.85f }) foreach (float z in new[] { -2.8f, 0f, 2.8f }) r.Cyl("paint.white", V(x, 0f, z), 0.09f, 2.35f, 4);
            r.Box("paint.white", V(0f, 2.4f, 0f), V(4.3f, 0.2f, 6.3f)).Box("yards.roof.dark", V(0f, 2.53f, 0f), V(4.5f, 0.08f, 6.5f)).Box("paving.grey", V(0f, 0.045f, 0f), V(4f, 0.09f, 6f))
                .Add("yards.woodstack", V(-1.2f, 0.09f, -2.4f), 90f).Add("yards.bike", V(1.1f, 0.09f, -1.6f), 15f).Add("yards.bike", V(0.6f, 0.09f, -1.9f), 8f, 0.7f).Add("yards.gardenstuff.a", V(-0.9f, 0.09f, 0.3f), 90f, 0.9f)
                .ColliderBox(V(0f, 1.3f, 0f), V(4f, 2.6f, 6f));
        }
    }
}

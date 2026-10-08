// TownLife scene snapshot + occupancy. Owner: core agent (architect wrote the signatures and a working first cut;
// TODO(core agent) marks what is thin). Everything here is PHYSICS-FREE: typed data read off scene components and the
// blueprint, so the full build, the incremental build and the preview all see the same thing.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Pure XZ geometry helpers shared by all TownLife code. Every function ignores y unless it says otherwise.</summary>
public static class TownLifeGeo
{
    public static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    /// <summary>Unit vector to the right of a (flat) direction: Right(+Z) = +X.</summary>
    public static Vector3 Right(Vector3 dir) { return new Vector3(dir.z, 0f, -dir.x); }
    /// <summary>Yaw in degrees such that Quaternion.Euler(0, yaw, 0) * Vector3.forward == dir (flattened).</summary>
    public static float Yaw(Vector3 dir) { return Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg; }
    /// <summary>Unit direction for a yaw in degrees (inverse of Yaw).</summary>
    public static Vector3 Dir(float yawDeg) { float r = yawDeg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)); }
    public static float Dist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }
    public static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b, out float t)
    {
        float dx = b.x - a.x, dz = b.z - a.z, len = dx * dx + dz * dz;
        t = len < 1e-10f ? 0f : Mathf.Clamp01(((p.x - a.x) * dx + (p.z - a.z) * dz) / len);
        return new Vector3(a.x + dx * t, 0f, a.z + dz * t);
    }
    public static float DistToSegment(Vector3 p, Vector3 a, Vector3 b) { return Dist(p, ClosestOnSegment(p, a, b, out _)); }
    public static float PolylineLength(IList<Vector3> pts) { float n = 0f; for (int i = 1; i < pts.Count; i++) n += Dist(pts[i - 1], pts[i]); return n; }
    /// <summary>Point at <paramref name="station"/> metres along a polyline (clamped), y = 0, with the unit tangent.</summary>
    public static Vector3 PointAt(IList<Vector3> pts, float station, out Vector3 tangent)
    {
        tangent = Vector3.forward;
        for (int i = 1; i < pts.Count; i++)
        {
            float len = Dist(pts[i - 1], pts[i]); if (len < 1e-6f) continue;
            tangent = Flat(pts[i] - pts[i - 1]) / len;
            if (station <= len || i == pts.Count - 1) return Flat(pts[i - 1]) + tangent * Mathf.Clamp(station, 0f, len);
            station -= len;
        }
        return pts.Count > 0 ? Flat(pts[0]) : Vector3.zero;
    }
    /// <summary>Projects a point onto a polyline. Returns the distance; station = metres along; lateral = signed offset
    /// (+ = right of travel).</summary>
    public static float Project(IList<Vector3> pts, Vector3 p, out float station, out float lateral)
    {
        float best = float.MaxValue, run = 0f; station = 0f; lateral = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float len = Dist(pts[i - 1], pts[i]);
            var c = ClosestOnSegment(p, pts[i - 1], pts[i], out float t); float d = Dist(p, c);
            if (d < best)
            {
                best = d; station = run + len * t;
                var dir = Flat(pts[i] - pts[i - 1]); lateral = Mathf.Sign(Vector3.Dot(Flat(p) - c, Right(dir))) * d;
            }
            run += len;
        }
        return best;
    }
    /// <summary>Polyline shifted sideways by <paramref name="lateral"/> metres (+ = right of travel), mitred.</summary>
    public static Vector3[] Offset(IList<Vector3> pts, float lateral)
    {
        var res = new Vector3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 d0 = Flat(pts[i] - pts[Mathf.Max(i - 1, 0)]), d1 = Flat(pts[Mathf.Min(i + 1, pts.Count - 1)] - pts[i]);
            if (d0.sqrMagnitude < 1e-8f) d0 = d1; if (d1.sqrMagnitude < 1e-8f) d1 = d0;
            d0.Normalize(); d1.Normalize();
            var tan = (d0 + d1).sqrMagnitude < 1e-6f ? d0 : (d0 + d1).normalized;
            float mitre = Mathf.Min(3f, 1f / Mathf.Max(0.3f, Vector3.Dot(tan, d0)));
            res[i] = Flat(pts[i]) + Right(tan) * (lateral * mitre);
        }
        return res;
    }
    /// <summary>Evenly spaced stations along a polyline: from <paramref name="start"/> to length - <paramref name="end"/>,
    /// about <paramref name="spacing"/> apart (adjusted so the first and last land exactly on the limits).</summary>
    public static float[] Stations(float length, float spacing, float start = 0f, float end = 0f)
    {
        float span = length - start - end; if (span < 0f) return new float[0];
        int n = Mathf.Max(1, Mathf.RoundToInt(span / Mathf.Max(0.01f, spacing)));
        var res = new float[n + 1]; for (int i = 0; i <= n; i++) res[i] = start + span * i / n;
        return res;
    }
    /// <summary>Oriented quad from a centre, a unit right vector, a unit forward vector and half sizes.</summary>
    public static TownLifePlan.Quad Quad(Vector3 centre, Vector3 right, Vector3 forward, float halfWidth, float halfDepth, string tag = null)
    {
        Vector3 r = Flat(right) * halfWidth, f = Flat(forward) * halfDepth; centre = Flat(centre);
        return new TownLifePlan.Quad { a = centre - r - f, b = centre + r - f, c = centre + r + f, d = centre - r + f, tag = tag };
    }
    public static TownLifePlan.Quad Quad(Vector3 centre, float yawDeg, float halfWidth, float halfDepth, string tag = null) { var f = Dir(yawDeg); return Quad(centre, Right(f), f, halfWidth, halfDepth, tag); }
    /// <summary>Rect.x/y = world x/z.</summary>
    public static TownLifePlan.Quad Quad(Rect r, string tag = null)
    {
        return new TownLifePlan.Quad { a = new Vector3(r.xMin, 0, r.yMin), b = new Vector3(r.xMax, 0, r.yMin), c = new Vector3(r.xMax, 0, r.yMax), d = new Vector3(r.xMin, 0, r.yMax), tag = tag };
    }
    public static Vector3 Centre(TownLifePlan.Quad q) { return Flat((q.a + q.b + q.c + q.d) * 0.25f); }
    public static Vector3[] Corners(TownLifePlan.Quad q) { return new[] { Flat(q.a), Flat(q.b), Flat(q.c), Flat(q.d) }; }
    /// <summary>Distance from a point to a convex quad: 0 inside, else distance to the nearest edge.</summary>
    public static float QuadDistance(TownLifePlan.Quad q, Vector3 p)
    {
        float s0 = Side(q.a, q.b, p), s1 = Side(q.b, q.c, p), s2 = Side(q.c, q.d, p), s3 = Side(q.d, q.a, p);
        if ((s0 >= 0 && s1 >= 0 && s2 >= 0 && s3 >= 0) || (s0 <= 0 && s1 <= 0 && s2 <= 0 && s3 <= 0)) return 0f;
        return Mathf.Min(Mathf.Min(DistToSegment(p, q.a, q.b), DistToSegment(p, q.b, q.c)), Mathf.Min(DistToSegment(p, q.c, q.d), DistToSegment(p, q.d, q.a)));
    }
    public static bool QuadContains(TownLifePlan.Quad q, Vector3 p, float margin = 0f) { return QuadDistance(q, p) <= margin; }
    static float Side(Vector3 a, Vector3 b, Vector3 p) { return (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x); }
    /// <summary>Even-odd point-in-polygon test for a closed outline.</summary>
    public static bool PolygonContains(IList<Vector3> outline, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            Vector3 a = outline[i], b = outline[j];
            if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }
    /// <summary>Absolute area of a closed outline.</summary>
    public static float PolygonArea(IList<Vector3> outline)
    {
        float a = 0f; for (int i = 0; i < outline.Count; i++) { var u = outline[i]; var v = outline[(i + 1) % outline.Count]; a += u.x * v.z - v.x * u.z; }
        return Mathf.Abs(a) * 0.5f;
    }
    public static bool RectContains(Rect r, Vector3 p, float margin = 0f) { return p.x >= r.xMin - margin && p.x <= r.xMax + margin && p.z >= r.yMin - margin && p.z <= r.yMax + margin; }
    /// <summary>True when the open segments a-b and c-d properly cross.</summary>
    public static bool SegmentsCross(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}

/// <summary>Reservation tags. Keep-out tags are seeded by TownLifeOccupancy.SeedKeepOuts; module tags are what modules
/// pass when reserving. A query for tag "tree" also matches "tree.street", "tree.yard" ... (dot-prefix match).</summary>
public static class TownLifeTags
{
    // seeded keep-outs
    public const string Road = "road", Sidewalk = "sidewalk", Driveway = "driveway", DriveClear = "driveway.clear", GardenPath = "path", PathClear = "path.clear",
        CurbStop = "stop", Building = "building", LotItem = "lotitem", KidPen = "kidpen", Infill = "infill", DepotPad = "depot", Junction = "junction",
        Crossing = "crossing", ParkCorridor = "corridor", Water = "water", Lamp = "lamp", Gate = "gate", Authored = "authored", NatureTree = "tree.nature";
    /// <summary>More seeded keep-outs (core agent). Bank = sloping terrain cells beside the river / sea (forbids solids; "water" queries match it);
    /// LotTree = the trunk of a tree that already stands on a lot ("tree" queries match it); Foreign = buildings and props of other
    /// districts or the depot scene inside the working area ("building" queries match it); ParkKept = legacy park objects that survive
    /// (lemonade stand); RiverWalk = the pedestrian river walk ribbon; Slab = beyond the ground slab / city wall.</summary>
    public const string Bank = "water.bank", LotTree = "tree.lot", Foreign = "building.foreign", ParkKept = "parkkept", RiverWalk = "riverwalk", Slab = "slab", CarSlot = "lotitem.car";
    // reserved by TownLifeBuilder before any module runs (from TownLifeFences)
    public const string Fence = "fence", FenceGap = "fence.gap";
    // module vocabulary (use these so modules can ask about each other's work)
    public const string Tree = "tree", Shrub = "shrub", Hedge = "hedge", Bed = "bed", PathSurface = "paving", Structure = "structure", Furniture = "furniture",
        Water2 = "pond", Lawn = "lawn", Feature = "feature";
}

/// <summary>Typed, physics-free snapshot of the live town for Acres + Meadows. Read-only for modules.</summary>
public sealed class TownLifeScene
{
    public const string Acres = "mildly_pleasant", Meadows = "minivan_meadows";
    public const string TownScenePath = "Assets/Scenes/Neighborhood.unity", TownRootName = "NeighborhoodGen";

    /// <summary>A road centre line (world polyline, y = 0).</summary>
    public sealed class RoadInfo
    {
        public string id, name, districtId; public Vector3[] points; public float halfWidth, length;
        public bool bridge, service, loop;                 // loop = closed ring (cul-de-sac bulb or the Mile)
        public TownLifePlan.Road plan;                     // the same road in planner form
        public Transform group;                            // NeighborhoodGen/CoastalRoads/<id> (may be null)
    }
    public enum LotItemKind { Other, Driveway, Path, Picket, KidPen, Pool, Trampoline, DryingRack, Car, Sign, Hoop, Flamingo, Shrub, Tree, Mailbox, Bin, Lemonade }
    /// <summary>One thing that already stands on a lot (everything under the holder except the building and yard fences).</summary>
    public sealed class LotItem
    {
        public string name; public LotItemKind kind; public Transform transform;
        public TownLifePlan.Quad rect;                     // oriented footprint, world
        public float height, depth;                        // top above the holder; metres along `inward` of the footprint centre
        public bool hasCollider, rear;                     // rear = centre lies behind the building's rear wall line
    }
    /// <summary>One household.</summary>
    public sealed class HouseInfo
    {
        public TownLifePlan.House plan;                    // stopId, roadId, districtId, side, station, origin, right, inward, footprint, drive, walk ...
        public Transform holder, building; public PickupStop stop; public CoastalHouseRoute route; public CityRoadAnchor anchor;
        public Bounds buildingBounds;                      // world AABB of the building renderers
        public Renderer[] buildingRenderers;               // for TownLifePaint
        public string model;                               // e.g. "SM_house_suburb_004"
        public float frontDepth, rearDepth, leftExtent, rightExtent, height;   // building in the lot frame: metres along inward (front/rear wall) and along right (min/max); roof top above origin
        public Vector3 door, mailbox, bin;
        public LotItem[] items;
        public string stopId => plan.stopId;
        public string districtId => plan.districtId;
        /// <summary>World point from lot coordinates: x metres along `right`, depth metres along `inward`.</summary>
        public Vector3 LotPoint(float x, float depth) { return plan.origin + plan.right * x + plan.inward * depth; }
    }
    public sealed class InfillInfo { public string id, kind; public TownLifePlan.Quad quad; public Transform transform; }
    public sealed class GateInfo { public string id, districtId; public Vector3 position, forward; }
    public sealed class CrossingInfo { public string roadId; public Vector3 position, forward; public float halfWidth; }
    public sealed class KidPenInfo { public string penId, stopId; public Vector3 center; public float yaw; public Vector2 half; public TownLifePlan.Quad quad; public Transform transform; }
    public struct Segment { public Vector3 a, b; public float halfWidth; }
    /// <summary>Pickle Park.</summary>
    /// <remarks>scene.park is null only when the blueprint has no "pickle-park" landmark (synthetic test towns).</remarks>
    public sealed class ParkInfo
    {
        public Rect block;                                 // inside the surrounding sidewalks (x 192..304, z 174..252.25); Rect.x/y = world x/z
        public Rect kerb;                                  // kerb to kerb (asphalt edge)
        public Vector3 landmark;                           // (256.75, 0, 213.2)
        public Segment[] corridors;                        // landmark -> each crossing kerb point; 3 m wide, must stay solid-free
        public KidPenInfo playground;                      // the sealed KidPen that stays where it is (may be null)
        public Transform group;                            // NeighborhoodGen/Park
        public Transform[] kept;                           // ParkPath_NS, the two ParkSign, SM_lemonade_shop*: keep name + semantics
    }
    /// <summary>A cul-de-sac bulb.</summary>
    public sealed class CourtInfo
    {
        public string roadId, name; public Vector3 centre;
        public float radius;                               // centre line (22)
        public float islandRadius;                         // free planted island = radius - halfWidth - sidewalk (14)
        public float innerKerb, outerKerb;                 // asphalt inner / outer radius (17 / 27)
        public float outerRadius;                          // outer sidewalk edge (30)
        public Vector3 entrance;                           // unit vector from the centre toward the lane that enters the court
    }

    public Transform town;                                 // NeighborhoodGen
    public Rect acresRect, meadowsRect, area;              // district rects and the working area (union + 60 m); Rect.x/y = world x/z
    public RoadInfo[] roads;                               // every road with a point inside `area`
    public HouseInfo[] houses;                             // the 59 households of the two districts, sorted by stopId (ordinal)
    public Vector3[] river, coast, riverWalk;              // smoothed blueprint polylines
    public InfillInfo[] infill; public Rect[] depotPads; public Vector3[] depotDoors;
    public GateInfo[] gates; public CrossingInfo[] crossings; public Vector3[] lamps;
    public ParkInfo park; public CourtInfo[] courts; public KidPenInfo[] kidPens;
    /// <summary>The ground slab (terrain mesh + invisible city wall): x -39..903.5, z -52..955.5. Nothing may be placed outside it.</summary>
    public Rect slab;
    /// <summary>Top of the river's SOUTH (town-side) bank as the terrain mesh has it (stepped with the 8 m grid): one point every
    /// 4 m of x, west to east, each the northernmost FLAT point (y = GrassY) on that x. Keep solids south of it (see BankMargin).</summary>
    public Vector3[] riverBankTop = new Vector3[0];
    /// <summary>Top of the coastal bank as the terrain mesh has it: one point every 4 m of z, south to north, each the easternmost
    /// FLAT point on that z. Keep solids west of it.</summary>
    public Vector3[] coastBankTop = new Vector3[0];
    /// <summary>Things inside the working area that belong to nobody TownLife dresses: houses / collection buildings of other
    /// districts and DepotRow.unity objects off the pads (world AABBs). Seeded as keep-outs.</summary>
    public TownLifePlan.Quad[] foreignObstacles = new TownLifePlan.Quad[0];
    public Vector3[] natureTrees;                          // existing trees of the Nature group inside `area`
    public TownLifePlan.Quad[] authoredObstacles;          // TownAuthored renderers (the owner's hand-placed layer)
    public List<string> notes = new List<string>();        // capture warnings

    readonly Dictionary<string, RoadInfo> roadById = new Dictionary<string, RoadInfo>();
    readonly Dictionary<string, HouseInfo> houseById = new Dictionary<string, HouseInfo>();

    public RoadInfo Road(string id) { return id != null && roadById.TryGetValue(id, out var r) ? r : null; }
    public HouseInfo House(string stopId) { return stopId != null && houseById.TryGetValue(stopId, out var h) ? h : null; }
    public CourtInfo Court(string roadId) { foreach (var c in courts) if (c.roadId == roadId) return c; return null; }
    public Rect DistrictRect(string districtId) { return districtId == Meadows ? meadowsRect : acresRect; }
    /// <summary>District of a point: Meadows east of the Acres/Meadows seam, else Acres; null outside `area`.</summary>
    public string DistrictAt(Vector3 p)
    {
        if (!TownLifeGeo.RectContains(area, p)) return null;
        return p.x >= (acresRect.xMax + meadowsRect.xMin) * 0.5f ? Meadows : Acres;
    }
    /// <summary>Metres beyond the nearest carriageway edge (negative on asphalt), and that road.</summary>
    public float RoadEdgeDistance(Vector3 p, out RoadInfo nearest)
    {
        float best = float.MaxValue; nearest = null;
        foreach (var r in roads)
            for (int i = 1; i < r.points.Length; i++)
            {
                float d = TownLifeGeo.DistToSegment(p, r.points[i - 1], r.points[i]) - r.halfWidth;
                if (d < best) { best = d; nearest = r; }
            }
        return best;
    }
    // ------------------------------------------------------------------------------------------------ terrain
    // Exact replica of NeighborhoodBuilder.BuildCoastalGround: an 8 m vertex grid over the slab, grass at -0.08, a river
    // bank that falls from 24 m to 15 m off the river centre line (-0.08 -> -5) and a shore that falls from 12 m west of
    // the coast line to 16 m east of it (-0.08 -> -7). Heights between vertices are the mesh's own triangles.
    Rect terrainBounds; int terrainNx, terrainNz; float[] terrainHeights; Vector3[] terrainRiver, terrainCoast;

    void InitTerrain()
    {
        terrainBounds = slab; terrainRiver = river ?? new Vector3[0]; terrainCoast = coast ?? new Vector3[0];
        terrainNx = Mathf.Max(1, Mathf.CeilToInt(slab.width / 8f)); terrainNz = Mathf.Max(1, Mathf.CeilToInt(slab.height / 8f));
        terrainHeights = new float[(terrainNx + 1) * (terrainNz + 1)];
        for (int i = 0; i < terrainHeights.Length; i++) terrainHeights[i] = float.NaN;
    }
    /// <summary>Shoreline x at a z (same rule as the terrain builder: first coast segment whose z range holds z).</summary>
    public float CoastX(float z)
    {
        var c = terrainCoast; if (c == null || c.Length == 0) return float.MaxValue;
        for (int i = 1; i < c.Length; i++)
        {
            Vector3 a = c[i - 1], b = c[i];
            if (z >= Mathf.Min(a.z, b.z) && z <= Mathf.Max(a.z, b.z)) return Mathf.Lerp(a.x, b.x, Mathf.InverseLerp(a.z, b.z, z));
        }
        return c[c.Length - 1].x;
    }
    /// <summary>XZ distance to the river centre line (water ribbon half-width 19.5; bank drops inside 24).</summary>
    public float RiverDistance(Vector3 p)
    {
        var r = terrainRiver; float d = float.MaxValue; if (r == null) return d;
        for (int i = 1; i < r.Length; i++) d = Mathf.Min(d, TownLifeGeo.DistToSegment(p, r[i - 1], r[i]));
        return d;
    }
    float TerrainVertex(int ix, int iz)
    {
        int n = iz * (terrainNx + 1) + ix; float y = terrainHeights[n];
        if (!float.IsNaN(y)) return y;
        var p = new Vector3(Mathf.Lerp(terrainBounds.xMin, terrainBounds.xMax, ix / (float)terrainNx), 0f, Mathf.Lerp(terrainBounds.yMin, terrainBounds.yMax, iz / (float)terrainNz));
        y = TownLifeContext.GrassY;
        float bank = RiverDistance(p);
        if (bank < 24f) y = Mathf.Lerp(-5f, -0.08f, Mathf.InverseLerp(15f, 24f, bank));
        float shore = p.x - CoastX(p.z);
        if (shore > -12f) y = Mathf.Min(y, Mathf.Lerp(-0.08f, -7f, Mathf.InverseLerp(-12f, 16f, shore)));
        terrainHeights[n] = y; return y;
    }
    /// <summary>Height of the terrain surface at a point: GrassY (-0.08) everywhere in town, the true sloping mesh height on
    /// the river bank and the shore (down to -5 / -7 under water; the water surface is WaterY). Outside the slab: GrassY.</summary>
    public float GroundY(Vector3 p)
    {
        if (terrainHeights == null || !TownLifeGeo.RectContains(terrainBounds, p)) return TownLifeContext.GrassY;
        float fx = (p.x - terrainBounds.xMin) / terrainBounds.width * terrainNx, fz = (p.z - terrainBounds.yMin) / terrainBounds.height * terrainNz;
        int ix = Mathf.Clamp(Mathf.FloorToInt(fx), 0, terrainNx - 1), iz = Mathf.Clamp(Mathf.FloorToInt(fz), 0, terrainNz - 1);
        float u = Mathf.Clamp01(fx - ix), v = Mathf.Clamp01(fz - iz);
        // cell corners A(ix, iz) B(ix, iz+1) C(ix+1, iz) D(ix+1, iz+1); the mesh splits each cell along B-C
        float a = TerrainVertex(ix, iz), b = TerrainVertex(ix, iz + 1), c = TerrainVertex(ix + 1, iz), d = TerrainVertex(ix + 1, iz + 1);
        return u + v <= 1f ? a + u * (c - a) + v * (b - a) : d + (1f - u) * (b - d) + (1f - v) * (c - d);
    }
    /// <summary>True when the terrain is level grass (GrassY) at the point and on a ring of <paramref name="radius"/> around
    /// it: not on the river bank, the shore or in the water, and inside the slab.</summary>
    public bool IsFlat(Vector3 p, float radius = 0f)
    {
        if (terrainHeights == null) return true;
        if (!TownLifeGeo.RectContains(slab, p, -radius - 1f)) return false;
        const float eps = 0.004f;
        if (GroundY(p) < TownLifeContext.GrassY - eps) return false;
        if (radius > 0f) for (int i = 0; i < 8; i++)
        {
            float ang = i * Mathf.PI / 4f;
            if (GroundY(new Vector3(p.x + Mathf.Cos(ang) * radius, 0f, p.z + Mathf.Sin(ang) * radius)) < TownLifeContext.GrassY - eps) return false;
        }
        return true;
    }
    /// <summary>z of the river's south bank top at an x (interpolated from riverBankTop; float.MaxValue when there is none).</summary>
    public float RiverBankZ(float x)
    {
        var b = riverBankTop; if (b.Length == 0) return float.MaxValue;
        if (x <= b[0].x) return b[0].z; if (x >= b[b.Length - 1].x) return b[b.Length - 1].z;
        for (int i = 1; i < b.Length; i++) if (x <= b[i].x) return Mathf.Min(b[i - 1].z, b[i].z);
        return b[b.Length - 1].z;
    }
    /// <summary>x of the coastal bank top at a z (the more westerly of the two neighbouring samples; float.MaxValue when none).</summary>
    public float CoastBankX(float z)
    {
        var b = coastBankTop; if (b.Length == 0) return float.MaxValue;
        if (z <= b[0].z) return b[0].x; if (z >= b[b.Length - 1].z) return b[b.Length - 1].x;
        for (int i = 1; i < b.Length; i++) if (z <= b[i].z) return Mathf.Min(b[i - 1].x, b[i].x);
        return b[b.Length - 1].x;
    }
    /// <summary>Terrain grid cells (world rects, merged along x) inside <paramref name="within"/> that are not level grass:
    /// the river bank, the shore and everything under water. Used to seed the "water.bank" keep-out.</summary>
    public List<Rect> SlopingCells(Rect within)
    {
        var list = new List<Rect>(); if (terrainHeights == null) return list;
        float dx = terrainBounds.width / terrainNx, dz = terrainBounds.height / terrainNz;
        int x0 = Mathf.Clamp(Mathf.FloorToInt((within.xMin - terrainBounds.xMin) / dx), 0, terrainNx - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((within.xMax - terrainBounds.xMin) / dx), 0, terrainNx - 1);
        int z0 = Mathf.Clamp(Mathf.FloorToInt((within.yMin - terrainBounds.yMin) / dz), 0, terrainNz - 1), z1 = Mathf.Clamp(Mathf.FloorToInt((within.yMax - terrainBounds.yMin) / dz), 0, terrainNz - 1);
        const float eps = 0.004f; float g = TownLifeContext.GrassY - eps;
        for (int iz = z0; iz <= z1; iz++)
        {
            int runStart = -1;
            for (int ix = x0; ix <= x1 + 1; ix++)
            {
                bool slope = ix <= x1 && (TerrainVertex(ix, iz) < g || TerrainVertex(ix + 1, iz) < g || TerrainVertex(ix, iz + 1) < g || TerrainVertex(ix + 1, iz + 1) < g);
                if (slope && runStart < 0) runStart = ix;
                if (!slope && runStart >= 0)
                {
                    list.Add(Rect.MinMaxRect(terrainBounds.xMin + runStart * dx, terrainBounds.yMin + iz * dz, terrainBounds.xMin + ix * dx, terrainBounds.yMin + (iz + 1) * dz));
                    runStart = -1;
                }
            }
        }
        return list;
    }
    void CaptureBanks()
    {
        var riverTop = new List<Vector3>(); var coastTop = new List<Vector3>();
        if (terrainRiver.Length > 1)
            for (float x = Mathf.Ceil(area.xMin / 4f) * 4f; x <= area.xMax; x += 4f)
            {
                // river centre z at this x (first river segment spanning x), then walk south to the first level point
                float zc = float.NaN;
                for (int i = 1; i < terrainRiver.Length; i++)
                {
                    Vector3 a = terrainRiver[i - 1], b = terrainRiver[i];
                    if (x >= Mathf.Min(a.x, b.x) && x <= Mathf.Max(a.x, b.x) && Mathf.Abs(b.x - a.x) > 1e-4f) { zc = Mathf.Lerp(a.z, b.z, Mathf.InverseLerp(a.x, b.x, x)); break; }
                }
                if (float.IsNaN(zc)) continue;
                // more than 45 m south of the centre line is no longer the river bank (the coast takes over at the mouth)
                for (float z = zc; z > zc - 45f; z -= 0.25f)
                    if (IsFlat(new Vector3(x, 0f, z))) { riverTop.Add(new Vector3(x, 0f, z)); break; }
            }
        if (terrainCoast.Length > 1)
            for (float z = Mathf.Ceil(area.yMin / 4f) * 4f; z <= area.yMax; z += 4f)
            {
                float xc = CoastX(z); if (xc > slab.xMax + 100f) continue;
                bool found = false;
                for (float x = xc; x > xc - 45f; x -= 0.25f)
                    if (IsFlat(new Vector3(x, 0f, z))) { coastTop.Add(new Vector3(x, 0f, z)); found = true; break; }
                if (!found && coastTop.Count > 0) break;      // the river mouth: the town-side coast ends here (one continuous polyline)
            }
        riverBankTop = riverTop.ToArray(); coastBankTop = coastTop.ToArray();
    }

    /// <summary>The Neighborhood scene's generated root if the scene is loaded, else null. Never opens a scene.</summary>
    public static Transform FindTown()
    {
        var scene = SceneManager.GetSceneByPath(TownScenePath);
        if (!scene.IsValid() || !scene.isLoaded) return null;
        foreach (var go in scene.GetRootGameObjects()) if (go.name == TownRootName) return go.transform;
        return null;
    }

    /// <summary>Planner input for TownLifePlan.Build: roads, houses, water, reserved quads (infill, depot pads, park
    /// block, KidPens outside lots) and bounds.</summary>
    public TownLifePlan.Input ToPlanInput()
    {
        var reserved = new List<TownLifePlan.Quad>();
        foreach (var i in infill) { var q = i.quad; q.tag = "infill:" + i.id; reserved.Add(q); }
        for (int i = 0; i < depotPads.Length; i++) reserved.Add(TownLifeGeo.Quad(depotPads[i], "depot:" + i));
        if (park != null) reserved.Add(TownLifeGeo.Quad(park.block, "park"));
        var planRoads = new List<TownLifePlan.Road>(); foreach (var r in roads) planRoads.Add(r.plan);
        var planHouses = new List<TownLifePlan.House>(); foreach (var h in houses) planHouses.Add(h.plan);
        return new TownLifePlan.Input { roads = planRoads.ToArray(), houses = planHouses.ToArray(), river = river, coast = coast, reserved = reserved.ToArray(), bounds = area };
    }

    // ---------------------------------------------------------------------------------------------- capture

    /// <summary>Reads the live town. No physics, no scene writes, no asset access beyond the blueprint caches.</summary>
    public static TownLifeScene Capture(Transform town)
    {
        if (town == null) throw new ArgumentNullException(nameof(town));
        var s = new TownLifeScene { town = town };
        s.acresRect = CoastalCityLayout.DistrictBounds(Acres); s.meadowsRect = CoastalCityLayout.DistrictBounds(Meadows);
        s.area = Rect.MinMaxRect(Mathf.Min(s.acresRect.xMin, s.meadowsRect.xMin) - 60f, Mathf.Min(s.acresRect.yMin, s.meadowsRect.yMin) - 60f,
                                 Mathf.Max(s.acresRect.xMax, s.meadowsRect.xMax) + 60f, Mathf.Max(s.acresRect.yMax, s.meadowsRect.yMax) + 60f);
        s.river = CoastalCityLayout.River; s.coast = CoastalCityLayout.Coast; s.riverWalk = CoastalCityLayout.RiverWalk;
        s.slab = CoastalCityLayout.BoundsXZ; s.InitTerrain(); s.CaptureBanks();

        var roadsGroup = town.Find("CoastalRoads");
        var roads = new List<RoadInfo>();
        foreach (var def in CoastalCityLayout.Roads)
        {
            bool inside = false; foreach (var p in def.points) if (TownLifeGeo.RectContains(s.area, p)) { inside = true; break; }
            if (!inside) continue;
            var info = new RoadInfo { id = def.id, name = def.name, districtId = def.districtId, points = def.points, halfWidth = def.halfWidth, length = def.Length, bridge = def.bridge, service = def.service };
            info.loop = def.points.Length > 3 && TownLifeGeo.Dist(def.points[0], def.points[def.points.Length - 1]) < 0.5f;
            info.plan = new TownLifePlan.Road { id = def.id, districtId = def.districtId, points = def.points, halfWidth = def.halfWidth, bridge = def.bridge, service = def.service };
            info.group = roadsGroup != null ? roadsGroup.Find(def.id) : null;
            roads.Add(info); s.roadById[def.id] = info;
        }
        s.roads = roads.ToArray();

        // courts: closed service loops in Meadows
        var courts = new List<CourtInfo>();
        foreach (var r in s.roads)
        {
            if (!r.loop || !r.service) continue;
            Vector3 c = Vector3.zero; int n = r.points.Length - 1; for (int i = 0; i < n; i++) c += r.points[i]; c /= n;
            float rad = 0f; for (int i = 0; i < n; i++) rad += TownLifeGeo.Dist(c, r.points[i]); rad /= n;
            var court = new CourtInfo { roadId = r.id, name = r.name, centre = TownLifeGeo.Flat(c), radius = rad, innerKerb = rad - r.halfWidth, outerKerb = rad + r.halfWidth, islandRadius = rad - r.halfWidth - 3f, outerRadius = rad + r.halfWidth + 3f };
            court.entrance = TownLifeGeo.Flat(r.points[0] - c).normalized;      // the loop starts at its entrance node
            courts.Add(court);
        }
        s.courts = courts.ToArray();

        // households
        var houses = new List<HouseInfo>();
        foreach (var stop in town.GetComponentsInChildren<PickupStop>(true))
        {
            if (stop.districtId != Acres && stop.districtId != Meadows) continue;
            var anchor = stop.GetComponentInParent<CityRoadAnchor>(true);
            if (anchor == null) { s.notes.Add("stop without CityRoadAnchor: " + stop.stopId); continue; }
            var h = CaptureHouse(s, stop, anchor); if (h == null) continue;
            houses.Add(h); s.houseById[h.stopId] = h;
        }
        houses.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
        s.houses = houses.ToArray();

        // infill, depot, gates, crossings, lamps, pens, nature, authored
        var infill = new List<InfillInfo>();
        foreach (var site in town.GetComponentsInChildren<CoastalInfillSite>(true))
        {
            var c = site.Corners();
            bool touches = TownLifeGeo.RectContains(s.area, site.transform.position);
            foreach (var corner in c) if (TownLifeGeo.RectContains(s.area, corner)) touches = true;      // a footprint that only pokes into the area still counts
            if (!touches) continue;
            infill.Add(new InfillInfo { id = site.siteId, kind = site.kind, transform = site.transform, quad = new TownLifePlan.Quad { a = TownLifeGeo.Flat(c[0]), b = TownLifeGeo.Flat(c[1]), c = TownLifeGeo.Flat(c[2]), d = TownLifeGeo.Flat(c[3]), tag = site.siteId } });
        }
        infill.Sort((a, b) => string.CompareOrdinal(a.id, b.id)); s.infill = infill.ToArray();
        s.depotPads = new Rect[4]; s.depotDoors = new Vector3[4];
        for (int i = 0; i < 4; i++) { s.depotPads[i] = CoastalCityLayout.DepotPad(i); s.depotDoors[i] = CoastalCityLayout.DepotDoor(i); }
        var gates = new List<GateInfo>();
        foreach (var g in CoastalCityLayout.Gates) if (TownLifeGeo.RectContains(s.area, g.position)) gates.Add(new GateInfo { id = g.id, districtId = g.districtId, position = g.position, forward = g.forward });
        s.gates = gates.ToArray();
        var crossings = new List<CrossingInfo>();
        foreach (var c in CoastalParkRoutes.Crossings()) crossings.Add(new CrossingInfo { roadId = c.roadId, position = c.position, forward = c.forward, halfWidth = c.halfWidth });
        s.crossings = crossings.ToArray();
        var lamps = new List<Vector3>();
        if (roadsGroup != null) foreach (var t in roadsGroup.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("SM_streetlamp") && TownLifeGeo.RectContains(s.area, t.position)) lamps.Add(t.position);
        s.lamps = lamps.ToArray();
        var pens = new List<KidPenInfo>();
        foreach (var pen in town.GetComponentsInChildren<KidPen>(true))
        {
            if (!TownLifeGeo.RectContains(s.area, pen.center)) continue;
            pens.Add(new KidPenInfo { penId = pen.penId, stopId = pen.stopId, center = pen.center, yaw = pen.yaw, half = pen.half, transform = pen.transform, quad = TownLifeGeo.Quad(pen.center, pen.yaw, pen.half.x, pen.half.y, "kidpen:" + pen.penId) });
        }
        pens.Sort((a, b) => string.CompareOrdinal(a.penId, b.penId)); s.kidPens = pens.ToArray();
        var trees = new List<Vector3>(); var nature = town.Find("Nature");
        if (nature != null) foreach (Transform t in nature) if (t.name.StartsWith("SM_tree") && TownLifeGeo.RectContains(s.area, t.position)) trees.Add(t.position);
        s.natureTrees = trees.ToArray();
        var authored = new List<TownLifePlan.Quad>(); var authoredRoot = TownAuthoringScope.AuthoredRoot(town);
        if (authoredRoot != null) foreach (var r in authoredRoot.GetComponentsInChildren<Renderer>(true))
            if (r.enabled && TownLifeGeo.RectContains(s.area, r.bounds.center)) authored.Add(TownLifeGeo.Quad(Rect.MinMaxRect(r.bounds.min.x, r.bounds.min.z, r.bounds.max.x, r.bounds.max.z), "authored"));
        s.authoredObstacles = authored.ToArray();
        s.foreignObstacles = CaptureForeign(s);

        // a blueprint without the pickle-park landmark (synthetic test towns) simply has no park
        try { s.park = CapturePark(s); }
        catch (ArgumentException e) { s.park = null; s.notes.Add("no park: " + e.Message); }
        return s;
    }

    /// <summary>World AABBs of everything inside the working area that TownLife neither dresses nor already models: lots of
    /// other districts and collection buildings (with their yard fences), district gate structures, and DepotRow.unity
    /// objects that are not on a depot pad. One quad per renderer; flat ground pieces (under 0.25 m tall) are skipped.</summary>
    static TownLifePlan.Quad[] CaptureForeign(TownLifeScene s)
    {
        var list = new List<TownLifePlan.Quad>();
        void Add(Renderer r, string tag)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) return;
            var b = r.bounds; if (b.size.y < 0.25f && b.max.y < 0.4f) return;
            var rect = Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
            if (!rect.Overlaps(s.area)) return;
            list.Add(TownLifeGeo.Quad(rect, tag));
        }
        var lots = s.town.Find("CoastalLots");
        if (lots != null) foreach (Transform holder in lots)
        {
            var anchor = holder.GetComponent<CityRoadAnchor>();
            if (anchor != null && anchor.stableId != null && s.houseById.ContainsKey(anchor.stableId)) continue;
            var hb = new Bounds(holder.position, Vector3.one * 160f);
            if (!Rect.MinMaxRect(hb.min.x, hb.min.z, hb.max.x, hb.max.z).Overlaps(s.area)) continue;
            foreach (var r in holder.GetComponentsInChildren<Renderer>(true)) Add(r, "lot:" + holder.name);
        }
        var gatesGroup = s.town.Find("CoastalGates");
        if (gatesGroup != null) foreach (var r in gatesGroup.GetComponentsInChildren<Renderer>(true)) Add(r, "gate:" + r.name);
        var depot = SceneManager.GetSceneByPath("Assets/Scenes/DepotRow.unity");
        if (depot.IsValid() && depot.isLoaded)
            foreach (var root in depot.GetRootGameObjects()) foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds; bool onPad = false;
                foreach (var pad in s.depotPads) if (b.min.x >= pad.xMin - 0.5f && b.max.x <= pad.xMax + 0.5f && b.min.z >= pad.yMin - 0.5f && b.max.z <= pad.yMax + 0.5f) { onPad = true; break; }
                if (!onPad) Add(r, "depot:" + r.name);
            }
        return list.ToArray();
    }

    static HouseInfo CaptureHouse(TownLifeScene s, PickupStop stop, CityRoadAnchor anchor)
    {
        var holder = anchor.transform; var route = stop.GetComponent<CoastalHouseRoute>();
        if (route == null) route = holder.GetComponentInChildren<CoastalHouseRoute>(true);
        var road = s.Road(anchor.roadId);
        var plan = new TownLifePlan.House
        {
            stopId = stop.stopId, roadId = anchor.roadId, districtId = stop.districtId, side = anchor.lateral >= 0f ? 1 : -1,
            station = road != null ? anchor.u * road.length : 0f, origin = holder.position,
            right = TownLifeGeo.Flat(holder.right).normalized, inward = TownLifeGeo.Flat(-holder.forward).normalized,
        };
        var h = new HouseInfo { plan = plan, holder = holder, stop = stop, route = route, anchor = anchor };
        if (road == null) s.notes.Add(stop.stopId + ": road '" + anchor.roadId + "' not in area");
        h.building = route != null ? route.buildingRoot : null;
        // the route may point at the whole Lot_ group (or at nothing): the building is then the house model inside it
        if (h.building == null || h.building == holder || h.building.name.StartsWith("Lot_", StringComparison.Ordinal))
        {
            Transform found = null;
            foreach (var t in holder.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("SM_house", StringComparison.Ordinal) || t.name == "Building body") { found = t; break; }
            if (found != null) { if (h.building != null) s.notes.Add(stop.stopId + ": buildingRoot was '" + h.building.name + "', using '" + found.name + "'"); h.building = found; }
        }
        if (h.building == null) { s.notes.Add(stop.stopId + ": no buildingRoot"); h.buildingRenderers = new Renderer[0]; }
        else h.buildingRenderers = h.building.GetComponentsInChildren<Renderer>(true);
        // building in the lot frame (x along right, z along inward)
        if (LotBounds(h.buildingRenderers, plan, out float x0, out float x1, out float d0, out float d1, out float top, out Bounds world))
        {
            h.leftExtent = x0; h.rightExtent = x1; h.frontDepth = d0; h.rearDepth = d1; h.height = top; h.buildingBounds = world;
            plan.footprint = LotQuad(plan, x0, x1, d0, d1, "building");
        }
        h.model = h.building != null ? h.building.name : "";
        if (route != null)
        {
            h.door = route.door; h.mailbox = route.mailbox;
            var walk = new List<Vector3> { route.door }; if (route.bends != null) walk.AddRange(route.bends); walk.Add(route.pathEnd);
            plan.walk = walk.ToArray();
        }
        else plan.walk = new Vector3[0];
        h.bin = stop.bin != null ? stop.bin.position : holder.position;

        // lot items: every renderer group under the holder that is not the building or a yard boundary
        var items = new List<LotItem>(); var keepIn = new List<TownLifePlan.Quad>(); var front = new List<TownLifePlan.Quad>();
        var roots = new List<Transform>();
        foreach (Transform child in holder)
        {
            if (child.GetComponent<CoastalYardBoundary>() != null || child.GetComponent<PickupStop>() != null) continue;
            if (h.building != null && (child == h.building || h.building.IsChildOf(child)) && !child.name.StartsWith("Lot_", StringComparison.Ordinal)) continue;
            if (child.name.StartsWith("Lot_", StringComparison.Ordinal)) { foreach (Transform g in child) if (h.building == null || (g != h.building && !h.building.IsChildOf(g))) roots.Add(g); }
            else roots.Add(child);
        }
        bool haveSlab = false;
        foreach (var root in roots)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true);
            if (!LotBounds(rs, plan, out float ix0, out float ix1, out float id0, out float id1, out float itop, out _)) continue;
            var item = new LotItem { name = root.name, transform = root, kind = Classify(root), rect = LotQuad(plan, ix0, ix1, id0, id1, root.name), height = itop, depth = (id0 + id1) * 0.5f };
            item.hasCollider = root.GetComponentInChildren<Collider>(true) != null;
            item.rear = item.depth > h.rearDepth && h.rearDepth > 0f;
            items.Add(item);
            if (item.kind == LotItemKind.Driveway)
            {
                // The drive line is the "Driveway" SLAB (sidewalk back edge -> top), also when route.hasDrive is false
                // (parked-only lots). The CurbRamp on the kerb and other drive pieces never override a slab.
                bool slab = root.name == "Driveway";
                if (slab || !haveSlab)
                {
                    plan.hasDrive = true; plan.driveHalfWidth = (ix1 - ix0) * 0.5f;
                    float cx = (ix0 + ix1) * 0.5f; Vector3 o = TownLifeGeo.Flat(plan.origin);
                    plan.driveA = o + plan.right * cx + plan.inward * id0; plan.driveB = o + plan.right * cx + plan.inward * id1;
                    if (slab) haveSlab = true;
                }
            }
            bool keep = item.kind == LotItemKind.Pool || item.kind == LotItemKind.Trampoline || item.kind == LotItemKind.KidPen || item.kind == LotItemKind.DryingRack;
            // "rear yard" for keepIn = behind the building's FRONT wall line: side-yard pools and racks sit inside the fenced
            // yard too (the dividers start at the front line); item.rear stays the stricter "behind the rear wall".
            if (keep && item.depth > h.frontDepth && h.rearDepth > 0f) keepIn.Add(item.rect);
            else if (item.kind == LotItemKind.Picket || item.kind == LotItemKind.Sign || item.kind == LotItemKind.KidPen || item.kind == LotItemKind.Hoop) front.Add(item.rect);
        }
        h.items = items.ToArray(); plan.keepIn = keepIn.ToArray(); plan.frontItems = front.ToArray();
        return h;
    }

    static LotItemKind Classify(Transform t)
    {
        string n = t.name;
        if (t.GetComponent<KidPen>() != null || n.StartsWith("KidPen")) return LotItemKind.KidPen;
        if (n == "Driveway" || n == "Drive" || n == "DriveLink" || n == "Turnaround" || n == "CurbRamp") return LotItemKind.Driveway;
        if (n == "Path" || n.StartsWith("SM_path_garden")) return LotItemKind.Path;
        if (n == "Picket") return LotItemKind.Picket;
        if (n.StartsWith("SM_swimming_pool") || n == "InfinityPool") return LotItemKind.Pool;
        if (n == "Trampoline") return LotItemKind.Trampoline;
        if (n.StartsWith("SM_clothes_drying")) return LotItemKind.DryingRack;
        if (n.StartsWith("Car_")) return LotItemKind.Car;
        if (n == "YardSign") return LotItemKind.Sign;
        if (n == "Hoop") return LotItemKind.Hoop;
        if (n == "Flamingo") return LotItemKind.Flamingo;
        if (n.StartsWith("SM_bush")) return LotItemKind.Shrub;
        if (n.StartsWith("SM_tree")) return LotItemKind.Tree;
        if (n.StartsWith("SM_mailbox")) return LotItemKind.Mailbox;
        if (n.StartsWith("SM_garbagecan")) return LotItemKind.Bin;
        if (n.StartsWith("SM_lemonade_shop")) return LotItemKind.Lemonade;
        return LotItemKind.Other;
    }

    /// <summary>Oriented bounds of renderers in a lot frame (exact corners of each renderer's local bounds).</summary>
    static bool LotBounds(Renderer[] renderers, TownLifePlan.House plan, out float x0, out float x1, out float d0, out float d1, out float top, out Bounds world)
    {
        x0 = d0 = float.MaxValue; x1 = d1 = top = float.MinValue; world = default; bool any = false;
        if (renderers == null) return false;
        foreach (var r in renderers)
        {
            if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
            var lb = r.localBounds;
            for (int i = 0; i < 8; i++)
            {
                var p = r.transform.TransformPoint(new Vector3((i & 1) == 0 ? lb.min.x : lb.max.x, (i & 2) == 0 ? lb.min.y : lb.max.y, (i & 4) == 0 ? lb.min.z : lb.max.z));
                var rel = p - plan.origin; float x = Vector3.Dot(rel, plan.right), d = Vector3.Dot(rel, plan.inward);
                x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x); d0 = Mathf.Min(d0, d); d1 = Mathf.Max(d1, d); top = Mathf.Max(top, rel.y);
            }
            if (!any) { world = r.bounds; any = true; } else world.Encapsulate(r.bounds);
        }
        return any;
    }
    static TownLifePlan.Quad LotQuad(TownLifePlan.House plan, float x0, float x1, float d0, float d1, string tag)
    {
        Vector3 o = TownLifeGeo.Flat(plan.origin);
        return new TownLifePlan.Quad { a = o + plan.right * x0 + plan.inward * d0, b = o + plan.right * x1 + plan.inward * d0, c = o + plan.right * x1 + plan.inward * d1, d = o + plan.right * x0 + plan.inward * d1, tag = tag };
    }

    /// <summary>The Pickle Park block from the BLUEPRINT alone (no scene): walks outwards from the landmark along the axes
    /// until a road's sidewalk (block) / kerb is reached. Used by the capture and by the full build's scenery exclusion.</summary>
    public static Rect ParkBlockFromBlueprint(out Rect kerb, out Vector3 landmark)
    {
        Vector3 lm = CoastalCityLayout.Landmark("pickle-park"); landmark = lm;
        var roads = CoastalCityLayout.Roads;
        float Edge(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var r in roads) for (int i = 1; i < r.points.Length; i++) best = Mathf.Min(best, TownLifeGeo.DistToSegment(p, r.points[i - 1], r.points[i]) - r.halfWidth);
            return best;
        }
        float Reach(Vector3 dir, float limit)
        {
            float lo = 0f, hi = 0f;
            for (; hi < 200f; hi += 0.5f) { if (Edge(lm + dir * hi) < limit) break; lo = hi; }
            for (int i = 0; i < 20; i++) { float mid = (lo + hi) * 0.5f; if (Edge(lm + dir * mid) < limit) hi = mid; else lo = mid; }
            return Mathf.Round(lo * 100f) / 100f;
        }
        kerb = Rect.MinMaxRect(lm.x - Reach(Vector3.left, 0f), lm.z - Reach(Vector3.back, 0f), lm.x + Reach(Vector3.right, 0f), lm.z + Reach(Vector3.forward, 0f));
        return Rect.MinMaxRect(lm.x - Reach(Vector3.left, 3f), lm.z - Reach(Vector3.back, 3f), lm.x + Reach(Vector3.right, 3f), lm.z + Reach(Vector3.forward, 3f));
    }

    static ParkInfo CapturePark(TownLifeScene s)
    {
        var park = new ParkInfo { group = s.town.Find("Park") };
        park.block = ParkBlockFromBlueprint(out park.kerb, out park.landmark);
        var corridors = new List<Segment>();
        foreach (var c in s.crossings) corridors.Add(new Segment { a = TownLifeGeo.Flat(park.landmark), b = TownLifeGeo.Flat(c.position), halfWidth = 1.5f });
        park.corridors = corridors.ToArray();
        foreach (var pen in s.kidPens) if (pen.penId == "playground" || (string.IsNullOrEmpty(pen.stopId) && TownLifeGeo.RectContains(park.block, pen.center))) { park.playground = pen; break; }
        var kept = new List<Transform>();
        if (park.group != null) foreach (var t in park.group.GetComponentsInChildren<Transform>(true))
            if (t.name == "ParkPath_NS" || t.name == "ParkSign" || t.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)) kept.Add(t);
        park.kept = kept.ToArray();
        return park;
    }
}

/// <summary>Where things may go. A list of tagged shapes (circles, capsules, convex quads) with a bucket index.
/// Seeded with the hard keep-outs of DESIGN section 4; every module reserves what it places so later modules avoid it.
/// All tests are in XZ.</summary>
public sealed class TownLifeOccupancy
{
    /// <summary>What you want to place. Solid = anything with height or a collider (trees, props, fences, furniture).
    /// Flat = ground paint and very low planting (paving decals, lawn stripes, flower carpets).</summary>
    public enum What { Solid, Flat }
    /// <summary>What a reserved shape forbids. Marker = nothing (only answers Within / DistanceTo queries).</summary>
    public enum Block { Marker, Solids, Everything }

    struct Shape { public byte kind; public Vector3 a, b, c, d; public float radius; public int tag; public Block block; }   // kind 0 circle(a), 1 capsule(a,b), 2 quad(a,b,c,d) inflated by radius
    const float Bucket = 8f;
    readonly List<Shape> shapes = new List<Shape>();
    readonly Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();
    readonly List<string> tags = new List<string>();
    readonly Dictionary<string, int> tagIndex = new Dictionary<string, int>();
    readonly Dictionary<string, int> counts = new Dictionary<string, int>();

    public readonly Rect area;
    public TownLifeOccupancy(Rect area) { this.area = area; }
    /// <summary>Number of shapes reserved so far.</summary>
    public int Count => shapes.Count;
    /// <summary>Number of shapes reserved with exactly this tag.</summary>
    public int CountOf(string tag) { return counts.TryGetValue(tag, out int n) ? n : 0; }

    int Tag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) throw new ArgumentException("A reservation needs a tag.");
        if (!tagIndex.TryGetValue(tag, out int i)) { i = tags.Count; tags.Add(tag); tagIndex[tag] = i; }
        counts[tag] = CountOf(tag) + 1;
        return i;
    }
    static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }
    void Add(Shape s, float minX, float minZ, float maxX, float maxZ)
    {
        int id = shapes.Count; shapes.Add(s);
        for (int x = Mathf.FloorToInt(minX / Bucket); x <= Mathf.FloorToInt(maxX / Bucket); x++)
            for (int z = Mathf.FloorToInt(minZ / Bucket); z <= Mathf.FloorToInt(maxZ / Bucket); z++)
            {
                long k = Key(x, z); if (!buckets.TryGetValue(k, out var list)) { list = new List<int>(); buckets[k] = list; }
                list.Add(id);
            }
    }

    /// <summary>Reserves a disc.</summary>
    public void ReserveCircle(Vector3 centre, float radius, string tag, Block block = Block.Solids)
    {
        Add(new Shape { kind = 0, a = centre, radius = radius, tag = Tag(tag), block = block }, centre.x - radius, centre.z - radius, centre.x + radius, centre.z + radius);
    }
    /// <summary>Reserves a capsule: every point within halfWidth of the segment a-b.</summary>
    public void ReserveLine(Vector3 a, Vector3 b, float halfWidth, string tag, Block block = Block.Solids)
    {
        Add(new Shape { kind = 1, a = a, b = b, radius = halfWidth, tag = Tag(tag), block = block },
            Mathf.Min(a.x, b.x) - halfWidth, Mathf.Min(a.z, b.z) - halfWidth, Mathf.Max(a.x, b.x) + halfWidth, Mathf.Max(a.z, b.z) + halfWidth);
    }
    /// <summary>Reserves a capsule per segment of a polyline (closed = also last -> first).</summary>
    public void ReservePolyline(IList<Vector3> points, float halfWidth, string tag, Block block = Block.Solids, bool closed = false)
    {
        for (int i = 1; i < points.Count; i++) ReserveLine(points[i - 1], points[i], halfWidth, tag, block);
        if (closed && points.Count > 2) ReserveLine(points[points.Count - 1], points[0], halfWidth, tag, block);
    }
    /// <summary>Reserves a convex quad grown by <paramref name="margin"/> metres.</summary>
    public void ReserveQuad(TownLifePlan.Quad q, string tag, Block block = Block.Solids, float margin = 0f)
    {
        float minX = Mathf.Min(Mathf.Min(q.a.x, q.b.x), Mathf.Min(q.c.x, q.d.x)) - margin, maxX = Mathf.Max(Mathf.Max(q.a.x, q.b.x), Mathf.Max(q.c.x, q.d.x)) + margin;
        float minZ = Mathf.Min(Mathf.Min(q.a.z, q.b.z), Mathf.Min(q.c.z, q.d.z)) - margin, maxZ = Mathf.Max(Mathf.Max(q.a.z, q.b.z), Mathf.Max(q.c.z, q.d.z)) + margin;
        Add(new Shape { kind = 2, a = q.a, b = q.b, c = q.c, d = q.d, radius = margin, tag = Tag(tag), block = block }, minX, minZ, maxX, maxZ);
    }
    /// <summary>Reserves an axis-aligned rect (Rect.x/y = world x/z).</summary>
    public void ReserveRect(Rect r, string tag, Block block = Block.Solids, float margin = 0f) { ReserveQuad(TownLifeGeo.Quad(r), tag, block, margin); }
    /// <summary>Reserves an oriented box footprint (centre, yaw degrees, half sizes).</summary>
    public void ReserveBox(Vector3 centre, float yawDeg, float halfWidth, float halfDepth, string tag, Block block = Block.Solids, float margin = 0f) { ReserveQuad(TownLifeGeo.Quad(centre, yawDeg, halfWidth, halfDepth), tag, block, margin); }

    static float Distance(in Shape s, Vector3 p)
    {
        switch (s.kind)
        {
            case 0: return TownLifeGeo.Dist(p, s.a) - s.radius;
            case 1: return TownLifeGeo.DistToSegment(p, s.a, s.b) - s.radius;
            default: return TownLifeGeo.QuadDistance(new TownLifePlan.Quad { a = s.a, b = s.b, c = s.c, d = s.d }, p) - s.radius;
        }
    }
    static bool Blocks(Block block, What what) { return block == Block.Everything || (block == Block.Solids && what == What.Solid); }
    bool TagMatches(int index, string tag) { string t = tags[index]; return t == tag || (t.Length > tag.Length && t[tag.Length] == '.' && t.StartsWith(tag, StringComparison.Ordinal)); }

    readonly HashSet<int> seen = new HashSet<int>();
    /// <summary>Visits every shape whose bucket range touches the query disc, once.</summary>
    bool Any(Vector3 p, float reach, Func<Shape, bool> test)
    {
        seen.Clear();
        for (int x = Mathf.FloorToInt((p.x - reach) / Bucket); x <= Mathf.FloorToInt((p.x + reach) / Bucket); x++)
            for (int z = Mathf.FloorToInt((p.z - reach) / Bucket); z <= Mathf.FloorToInt((p.z + reach) / Bucket); z++)
                if (buckets.TryGetValue(Key(x, z), out var list))
                    foreach (int id in list) if (seen.Add(id) && test(shapes[id])) return true;
        return false;
    }

    /// <summary>True when a disc of <paramref name="radius"/> at <paramref name="point"/> touches nothing that forbids
    /// <paramref name="what"/> and lies inside the working area.</summary>
    public bool Free(Vector3 point, float radius, What what = What.Solid)
    {
        if (!TownLifeGeo.RectContains(area, point)) return false;
        return !Any(point, radius, s => Blocks(s.block, what) && Distance(s, point) < radius);
    }
    /// <summary>The tag of something that blocks the disc, or null when it is free (for reports and debugging).</summary>
    public string Blocker(Vector3 point, float radius, What what = What.Solid)
    {
        if (!TownLifeGeo.RectContains(area, point)) return "outside";
        string hit = null;
        Any(point, radius, s => { if (Blocks(s.block, what) && Distance(s, point) < radius) { hit = tags[s.tag]; return true; } return false; });
        return hit;
    }
    /// <summary>Free test for a capsule (sampled every min(halfWidth, 0.5) m along the segment; exact enough for runs).</summary>
    public bool FreeLine(Vector3 a, Vector3 b, float halfWidth, What what = What.Solid)
    {
        float len = TownLifeGeo.Dist(a, b); int n = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.25f, Mathf.Min(halfWidth, 0.5f))));
        for (int i = 0; i <= n; i++) if (!Free(Vector3.Lerp(a, b, i / (float)n), halfWidth, what)) return false;
        return true;
    }
    /// <summary>Free test for a convex quad grown by margin (sampled on a ~0.5 m lattice plus the corners).</summary>
    public bool FreeQuad(TownLifePlan.Quad q, What what = What.Solid, float margin = 0f)
    {
        float w = Mathf.Max(TownLifeGeo.Dist(q.a, q.b), TownLifeGeo.Dist(q.d, q.c)), d = Mathf.Max(TownLifeGeo.Dist(q.a, q.d), TownLifeGeo.Dist(q.b, q.c));
        int nu = Mathf.Max(1, Mathf.CeilToInt(w / 0.5f)), nv = Mathf.Max(1, Mathf.CeilToInt(d / 0.5f));
        float r = Mathf.Max(margin, 0.36f);
        for (int u = 0; u <= nu; u++)
            for (int v = 0; v <= nv; v++)
            {
                var p = Vector3.Lerp(Vector3.Lerp(q.a, q.b, u / (float)nu), Vector3.Lerp(q.d, q.c, u / (float)nu), v / (float)nv);
                if (!Free(p, r, what)) return false;
            }
        return true;
    }
    /// <summary>Free test for an oriented box footprint.</summary>
    public bool FreeBox(Vector3 centre, float yawDeg, float halfWidth, float halfDepth, What what = What.Solid, float margin = 0f) { return FreeQuad(TownLifeGeo.Quad(centre, yawDeg, halfWidth, halfDepth), what, margin); }

    /// <summary>True when the point is within <paramref name="distance"/> of any shape tagged <paramref name="tag"/>
    /// (or "tag.something"), whatever that shape blocks. Use for spacing rules ("no tree within 6 m of a tree").</summary>
    public bool Within(Vector3 point, float distance, string tag)
    {
        return Any(point, distance, s => TagMatches(s.tag, tag) && Distance(s, point) < distance);
    }
    /// <summary>Distance to the nearest shape with the tag, searching up to <paramref name="max"/> (returns max if none).</summary>
    public float DistanceTo(Vector3 point, string tag, float max = 50f)
    {
        float best = max;
        Any(point, max, s => { if (TagMatches(s.tag, tag)) best = Mathf.Min(best, Mathf.Max(0f, Distance(s, point))); return false; });
        return best;
    }
    /// <summary>Free(point) and then ReserveCircle(point) in one call. The usual way to place a prop.</summary>
    public bool TryReserve(Vector3 point, float radius, string tag, What what = What.Solid, Block block = Block.Solids)
    {
        if (!Free(point, radius, what)) return false;
        ReserveCircle(point, radius, tag, block); return true;
    }
    /// <summary>Deterministic nearest-free search: tests <paramref name="near"/>, then rings of growing radius
    /// (<paramref name="step"/> apart, 8+ points per ring, fixed order) out to <paramref name="searchRadius"/>.</summary>
    public bool TryFindFree(Vector3 near, float radius, What what, float searchRadius, out Vector3 found, float step = 1f)
    {
        found = near; if (Free(near, radius, what)) return true;
        for (float r = step; r <= searchRadius + 1e-4f; r += step)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * r / step));
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n; var p = new Vector3(near.x + Mathf.Cos(a) * r, near.y, near.z + Mathf.Sin(a) * r);
                if (Free(p, radius, what)) { found = p; return true; }
            }
        }
        return false;
    }

    /// <summary>Read-only view of one reserved shape (debug painting, tests). kind: 0 circle (a, radius), 1 capsule
    /// (a-b, radius), 2 convex quad (a, b, c, d) grown by radius.</summary>
    public struct ShapeView { public int kind; public Vector3 a, b, c, d; public float radius; public string tag; public Block block; }
    /// <summary>Calls <paramref name="visit"/> for every reserved shape in reservation order.</summary>
    public void Visit(Action<ShapeView> visit)
    {
        foreach (var s in shapes) visit(new ShapeView { kind = s.kind, a = s.a, b = s.b, c = s.c, d = s.d, radius = s.radius, tag = tags[s.tag], block = s.block });
    }
    /// <summary>Every distinct tag reserved so far with its shape count, sorted by tag.</summary>
    public List<KeyValuePair<string, int>> TagCounts()
    {
        var list = new List<KeyValuePair<string, int>>(counts); list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key)); return list;
    }

    /// <summary>Seeds every hard keep-out of DESIGN section 4 from the scene snapshot. Called once by the builder.</summary>
    public void SeedKeepOuts(TownLifeScene scene, TownLifePlan.Result plan)
    {
        // ---- roads: carriageway + the 3 m sidewalk band (bridges too: their approaches carry a sidewalk)
        foreach (var r in scene.roads)
        {
            ReservePolyline(r.points, r.halfWidth, TownLifeTags.Road, Block.Everything);
            ReservePolyline(r.points, r.halfWidth + 3f, TownLifeTags.Sidewalk, Block.Everything);
        }
        // ---- junction sight circles: every node shared by two roads (ends, T-junctions, crossings, court entrances)
        var nodes = new List<Vector3>();
        for (int i = 0; i < scene.roads.Length; i++)
            for (int j = 0; j < scene.roads.Length; j++)
            {
                if (i == j) continue;
                foreach (var p in scene.roads[i].points)
                {
                    if (TownLifeGeo.Project(scene.roads[j].points, p, out _, out _) > 0.5f) continue;
                    bool dup = false; foreach (var n in nodes) if (TownLifeGeo.Dist(n, p) < 6f) { dup = true; break; }
                    if (!dup) nodes.Add(TownLifeGeo.Flat(p));
                }
            }
        foreach (var n in nodes) ReserveCircle(n, 12f, TownLifeTags.Junction, Block.Solids);
        // ---- households
        foreach (var h in scene.houses)
        {
            var p = h.plan;
            ReserveCircle(p.origin, 3f, TownLifeTags.CurbStop); ReserveCircle(h.bin, 3f, TownLifeTags.CurbStop); ReserveCircle(h.mailbox, 3f, TownLifeTags.CurbStop);
            ReserveQuad(p.footprint, TownLifeTags.Building, Block.Everything, 0.3f);
            // driveway: the route's ramp -> top line and, where the slab is longer or offset, the slab's own centre line
            if (h.route != null) ReserveLine(h.route.ramp, h.route.top, 2.6f, TownLifeTags.DriveClear, Block.Solids);
            if (p.hasDrive && (h.route == null || TownLifeGeo.Dist(p.driveA, h.route.ramp) > 0.5f || TownLifeGeo.Dist(p.driveB, h.route.top) > 0.5f))
                ReserveLine(p.driveA, p.driveB, 2.6f, TownLifeTags.DriveClear, Block.Solids);
            if (h.route != null)
            {
                // parked household cars: 2.4 x 5.4 m slots (cars drive off and come back; the slots must stay clear)
                foreach (var slot in new[] { h.route.car0, h.route.car1 })
                {
                    if (TownLifeGeo.Dist(slot.position, p.origin) > 80f || slot.position == Vector3.zero) continue;
                    ReserveBox(slot.position, slot.rotation.eulerAngles.y, 1.2f, 2.7f, TownLifeTags.CarSlot, Block.Solids, 0.3f);
                }
            }
            if (p.walk != null && p.walk.Length > 1) { ReservePolyline(p.walk, 1.4f, TownLifeTags.PathClear, Block.Solids); ReservePolyline(p.walk, 0.8f, TownLifeTags.GardenPath, Block.Everything); }
            foreach (var item in h.items)
            {
                switch (item.kind)
                {
                    case TownLifeScene.LotItemKind.Driveway: ReserveQuad(item.rect, TownLifeTags.Driveway, Block.Everything, 0.2f); break;
                    case TownLifeScene.LotItemKind.KidPen: break;                                  // scene.kidPens below
                    case TownLifeScene.LotItemKind.Path: ReserveQuad(item.rect, TownLifeTags.GardenPath, Block.Everything, 0.1f); break;   // the slab itself (the walk line may run beside it)
                    case TownLifeScene.LotItemKind.Tree:                                           // trunk only: planting under the crown is fine
                        ReserveCircle(item.transform != null ? item.transform.position : TownLifeGeo.Centre(item.rect), 1.5f, TownLifeTags.LotTree, Block.Solids); break;
                    case TownLifeScene.LotItemKind.Pool:
                    case TownLifeScene.LotItemKind.Trampoline: ReserveQuad(item.rect, TownLifeTags.LotItem, Block.Everything, 0.4f); break;
                    default: ReserveQuad(item.rect, TownLifeTags.LotItem, Block.Solids, 0.4f); break;
                }
            }
        }
        foreach (var pen in scene.kidPens) ReserveQuad(pen.quad, TownLifeTags.KidPen, Block.Everything, 0.6f);
        foreach (var i in scene.infill) ReserveQuad(i.quad, TownLifeTags.Infill, Block.Everything, 0.5f);
        // ---- depot: pads, and the truck's way out from each bay door to the street
        for (int i = 0; i < scene.depotPads.Length; i++)
        {
            ReserveRect(scene.depotPads[i], TownLifeTags.DepotPad, Block.Everything, 0.5f);
            var depotRoad = scene.Road("depot");
            if (depotRoad != null && i < scene.depotDoors.Length)
            {
                TownLifeGeo.Project(depotRoad.points, scene.depotDoors[i], out float st, out _);
                ReserveLine(scene.depotDoors[i], TownLifeGeo.PointAt(depotRoad.points, st, out _), 4f, TownLifeTags.DepotPad, Block.Solids);
            }
        }
        // ---- park: zebra crossings with their forecourts, the walkable corridor, what survives of the old park
        foreach (var c in scene.crossings)
        {
            var across = TownLifeGeo.Right(TownLifeGeo.Flat(c.forward).normalized) * (c.halfWidth + 6f);
            ReserveLine(c.position - across, c.position + across, 3.5f, TownLifeTags.Crossing, Block.Solids);
        }
        if (scene.park != null)
        {
            foreach (var seg in scene.park.corridors) ReserveLine(seg.a, seg.b, seg.halfWidth, TownLifeTags.ParkCorridor, Block.Solids);
            foreach (var t in scene.park.kept)
            {
                if (t == null || !t.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)) continue;     // the path and signs are retired with the legacy park
                // FitCoastalParkEntrances re-poses the stand on every publish: south entrance + 3.4 m inward, 2.75 m east
                ReserveCircle(TownLifeGeo.Flat(t.position), 1.6f, TownLifeTags.ParkKept, Block.Solids);
            }
        }
        // ---- water and banks
        if (scene.river != null && scene.river.Length > 1) ReservePolyline(scene.river, 24f, TownLifeTags.Water, Block.Everything);
        if (scene.coast != null && scene.coast.Length > 1)
        {
            // the sea: everything east of 8 m west of the coast line (where the water surface meets the shore)
            float far = Mathf.Max(area.xMax, scene.slab.xMax) + 200f;
            for (int i = 1; i < scene.coast.Length; i++)
            {
                Vector3 a = scene.coast[i - 1], b = scene.coast[i];
                if (Mathf.Max(a.z, b.z) < area.yMin - 20f || Mathf.Min(a.z, b.z) > area.yMax + 20f) continue;
                ReserveQuad(new TownLifePlan.Quad { a = new Vector3(a.x - 8f, 0f, a.z), b = new Vector3(far, 0f, a.z), c = new Vector3(far, 0f, b.z), d = new Vector3(b.x - 8f, 0f, b.z), tag = "sea" }, TownLifeTags.Water, Block.Everything);
            }
        }
        // sloping terrain (river bank, shore): no solids; flat dressing (reeds, dune grass) is allowed and uses scene.GroundY
        foreach (var cell in scene.SlopingCells(area)) ReserveRect(cell, TownLifeTags.Bank, Block.Solids, 0.25f);
        // beyond the ground slab / city wall (only matters if the working area ever grows past it)
        var slab = scene.slab;
        if (slab.width > 0f)
        {
            if (area.xMin < slab.xMin + 1f) ReserveRect(Rect.MinMaxRect(area.xMin - 50f, area.yMin - 50f, slab.xMin + 1f, area.yMax + 50f), TownLifeTags.Slab, Block.Everything);
            if (area.xMax > slab.xMax - 1f) ReserveRect(Rect.MinMaxRect(slab.xMax - 1f, area.yMin - 50f, area.xMax + 50f, area.yMax + 50f), TownLifeTags.Slab, Block.Everything);
            if (area.yMin < slab.yMin + 1f) ReserveRect(Rect.MinMaxRect(area.xMin - 50f, area.yMin - 50f, area.xMax + 50f, slab.yMin + 1f), TownLifeTags.Slab, Block.Everything);
            if (area.yMax > slab.yMax - 1f) ReserveRect(Rect.MinMaxRect(area.xMin - 50f, slab.yMax - 1f, area.xMax + 50f, area.yMax + 50f), TownLifeTags.Slab, Block.Everything);
        }
        if (scene.riverWalk != null) for (int i = 1; i < scene.riverWalk.Length; i++)
        {
            Vector3 a = scene.riverWalk[i - 1], b = scene.riverWalk[i];
            if (!TownLifeGeo.RectContains(area, a, 3f) && !TownLifeGeo.RectContains(area, b, 3f)) continue;
            ReserveLine(a, b, 1.6f, TownLifeTags.RiverWalk, Block.Everything);
        }
        // ---- street furniture, gates, existing trees, other people's things
        foreach (var l in scene.lamps) ReserveCircle(l, 1.2f, TownLifeTags.Lamp);
        foreach (var g in scene.gates) ReserveCircle(g.position, 9f, TownLifeTags.Gate);
        foreach (var t in scene.natureTrees) ReserveCircle(t, 2f, TownLifeTags.NatureTree);
        foreach (var q in scene.authoredObstacles) ReserveQuad(q, TownLifeTags.Authored, Block.Everything, 0.5f);
        foreach (var q in scene.foreignObstacles) ReserveQuad(q, TownLifeTags.Foreign, Block.Solids, 0.4f);
    }
}

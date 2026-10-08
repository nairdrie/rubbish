using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Town Life parcel plan ("frontage Voronoi"). Pure geometry: no UnityEditor, no Physics, no scene access, no statics
/// that survive a call. Deterministic: the same Input always gives the same Result (no Random, no hash-order dependence).
///
/// Pipeline (all on a raster of `settings.cell` metres, x = world X, y of the bounds Rect = world Z):
///  1. masks: carriageway + sidewalk reserve of EVERY road, river / coast setbacks (+ everything seaward of the coast),
///     reserved quads (+ margin);
///  2. houses are grouped into frontage chains (same road + side, station gaps below 2*endCap + endSnap; closed roads wrap);
///  3. every chain claims the cells on its side of its road, nearer frontage wins (straight rear spine between
///     back-to-back rows, mitre at corners); inside a chain cells go to houses by divider lines placed at the midpoint
///     of the free gap between the two buildings' facing side walls;
///  4. footprints (+ margin) and keepIn quads are forced into their parcel and only the component touching the house is
///     kept; leftover land thinner than thinStrip behind a rear cap or past a row end is absorbed along the chain's own
///     rays (straight cuts), small notches and pockets are filled, and tails narrower than spikeWidth are trimmed off
///     (a trimmed tail beside a road becomes verge: label RoadReserve, although it is not masked);
///  5. the label grid is vectorised as a planar graph: junction vertices, one polyline per (label pair, run), simplified
///     ONCE, so the shared edge of two neighbours is the identical polyline; outlines are assembled from those edges.
///
/// parcels[i] always belongs to input.houses[i] (same index). Outlines run counter-clockwise seen from above
/// (+X right, +Z up). Edges of kind ParcelCommon with b == Outside mark a parcel that touches the plan bounds.
/// </summary>
public static class TownLifePlan
{
    public sealed class Road { public string id, districtId; public Vector3[] points; public float halfWidth; public bool bridge, service; }
    /// World-space convex quad, corners in order, y ignored.
    public struct Quad { public Vector3 a, b, c, d; public string tag; }
    public sealed class House
    {
        public string stopId, roadId, districtId;
        public int side;                 // +1 = right of the road's travel direction, -1 = left (sign of CityRoadAnchor.lateral)
        public float station;            // metres along the road polyline of the lot origin
        public Vector3 origin;           // holder world position (the curb stop)
        public Vector3 right, inward;    // unit, y = 0: along the street (holder +X) and from the street into the lot (holder -Z)
        public Quad footprint;           // building footprint
        public bool hasDrive; public Vector3 driveA, driveB; public float driveHalfWidth;  // driveway centre line; set whenever a slab exists
        public Vector3[] walk;           // door, bends..., pathEnd
        public Quad[] keepIn;            // rear-yard items that must end up inside this parcel (pool, trampoline, kid pen, drying rack)
        public Quad[] frontItems;        // front-yard items (pickets, signs, pens); informational
    }
    public sealed class Settings
    {
        public float cell = 0.5f, sidewalk = 3.3f;
        public float depthAcres = 46f, depthMeadows = 36f, depthCourt = 30f;   // max parcel depth behind the sidewalk
        public float endCap = 20f, endSnap = 13f;        // row ends: extend up to endCap past the last house, snap to the corner if less than endSnap would be left
        public float riverSetback = 27f, coastSetback = 15f, reservedMargin = 1f;
        public float thinStrip = 12f;                    // unassigned land narrower than this is absorbed by the adjacent parcel
        public float simplify = 0.8f;                    // Douglas-Peucker tolerance for edges and outlines
        // ---- additions (not part of the original frozen list) ----
        public float courtMaxLength = 300f;              // a closed road shorter than this is a cul-de-sac bulb (court depth applies)
        public float footprintMargin = 1f;               // the footprint grown by this is forced into its own parcel
        public float keepInMargin = 0.5f;                // same for keepIn quads
        public float endSideMin = 6f;                    // a row end is never closer than this to the last building's outer side wall
        public float dividerChord = 4f;                  // half length of the chord that defines a divider's direction on curved frontage
        public float minArea = 150f, maxArea = 5000f;    // SelfCheck parcel area window (m2)
        public float minCommon = 80f;                    // leftover pockets smaller than this (m2) are merged into the neighbouring parcel
        public float notchWidth = 5f;                    // notches of common land narrower than this that bite into the parcel fabric are filled
        public float notchMinArea = 3f;                  // ...when at least this big (m2)
        public float spikeWidth = 4f;                    // parcel tails narrower than this are trimmed off (they become verge or common land)
        public float spikeMinArea = 6f;                  // ...when the trimmed piece is at least this big (m2); smaller crumbs are left alone
        public string meadowsDistrictId = "minivan_meadows";
    }
    public sealed class Input { public Road[] roads; public House[] houses; public Vector3[] river, coast; public Quad[] reserved; public Rect bounds; public Settings settings = new Settings(); }

    public const int Common = -1, RoadReserve = -2, Water = -3, Reserved = -4, Outside = -5;

    public sealed class Parcel
    {
        public int index; public House house;
        public string chain;             // roadId + "|" + side + "|" + groupIndex : one frontage chain = one future yard marker
        public bool court;               // frontage road is a closed loop (cul-de-sac bulb)
        public Vector3[] outline;        // simplified closed polygon, y = 0 (counter-clockwise from above, last point != first)
        public float area, backYardArea;
        public float frontLine, rearLine;// metres from origin along `inward` to the building's front wall / rear wall
        public Vector3 centroid, backYardCentroid;
        public int[] neighbours;
    }
    public enum EdgeKind { ParcelParcel, ParcelCommon, ParcelRoad, ParcelWater, ParcelReserved }
    public sealed class Edge
    {
        public int a, b;                 // a = parcel index; b = other parcel index or a negative code
        public EdgeKind kind;
        public Vector3[] points;         // open polyline, y = 0, simplified, >= 2 points
        public string ownerChain;        // which chain's marker owns it (cross-chain parcel-parcel edges: ordinal-smaller chain)
        public bool frontage;            // parcel a's own street frontage
        // ---- additions ----
        public string roadId;            // ParcelRoad only: the road whose reserve this edge runs along
        public int common = -1;          // ParcelCommon only: id of the common region on the other side (-1 for Outside)
        public float length;             // metres
        public bool closed;              // the edge is a closed ring (first point == last point): an isolated boundary with no junction
    }
    public sealed class CommonRegion
    {
        public int id; public float area; public Rect bounds; public Vector3 centroid;
        public bool touchesWater, touchesBounds;
        public int[] adjacentParcels; public string[] adjacentRoads;
        public Vector3[] outline;        // simplified outer ring
    }
    public sealed class Result
    {
        public Input input; public Parcel[] parcels; public Edge[] edges; public CommonRegion[] commons;

        // ---- additions ----
        /// Non-fatal observations made while building (dividers that fell back to the origin midpoint, dropped stamps...).
        public List<string> notes = new List<string>();
        /// Chain names in ordinal order, and for each the parcel indices in station order.
        public string[] chains; public int[][] chainParcels;
        public int gridWidth, gridHeight; public float cell; public float originX, originZ;
        /// Raw label of a grid cell (parcel index or negative code); Outside when out of range.
        public int LabelAtCell(int ix, int iz) { return ix < 0 || iz < 0 || ix >= gridWidth || iz >= gridHeight ? Outside : label[iz * gridWidth + ix]; }
        public Vector3 CellCentre(int ix, int iz) { return new Vector3(originX + (ix + .5f) * cell, 0f, originZ + (iz + .5f) * cell); }

        internal int[] label, commonGrid; internal sbyte[] mask;
        internal SegIndex roadIndex, riverIndex, coastIndex; internal int seaSign;
        internal Dictionary<string, Parcel> byStop;

        int CellIndex(Vector3 w)
        {
            int ix = Mathf.FloorToInt((w.x - originX) / cell), iz = Mathf.FloorToInt((w.z - originZ) / cell);
            return ix < 0 || iz < 0 || ix >= gridWidth || iz >= gridHeight ? -1 : iz * gridWidth + ix;
        }
        public int LabelAt(Vector3 world) { int i = CellIndex(world); return i < 0 ? Outside : label[i]; }             // parcel index or negative code
        public int CommonAt(Vector3 world) { int i = CellIndex(world); return i < 0 ? -1 : commonGrid[i]; }              // common region id or -1
        /// Metres beyond the nearest carriageway edge (negative on asphalt; sidewalk is 0..3). Exact, not grid-sampled.
        public float RoadEdgeDistance(Vector3 world) { int seg; return roadIndex == null ? float.PositiveInfinity : roadIndex.Query(world.x, world.z, out seg); }
        /// Id of the road whose carriageway edge is nearest (null when the plan has no roads).
        public string NearestRoad(Vector3 world) { int seg; if (roadIndex == null) return null; roadIndex.Query(world.x, world.z, out seg); return seg < 0 ? null : input.roads[roadIndex.owner[seg]].id; }
        /// Metres to the river/sea reserve (<= 0 inside it). Exact, not grid-sampled.
        public float WaterDistance(Vector3 world)
        {
            float best = float.PositiveInfinity; int seg; var s = input.settings;
            if (riverIndex != null) best = riverIndex.Query(world.x, world.z, out seg) - s.riverSetback;
            if (coastIndex != null)
            {
                float d = coastIndex.Query(world.x, world.z, out seg);
                if (seaSign != 0 && seg >= 0 && coastIndex.Side(seg, world.x, world.z) == seaSign) d = -d;
                best = Mathf.Min(best, d - s.coastSetback);
            }
            return best;
        }
        public float DepthInParcel(Vector3 world, int parcel)                                                           // metres along the parcel's `inward` from its origin
        {
            if (parcel < 0 || parcel >= parcels.Length) return 0f;
            var h = parcels[parcel].house; return (world.x - h.origin.x) * h.inward.x + (world.z - h.origin.z) * h.inward.z;
        }
        /// Metres along the parcel's `right` (holder +X) from its origin.
        public float AcrossInParcel(Vector3 world, int parcel)
        {
            if (parcel < 0 || parcel >= parcels.Length) return 0f;
            var h = parcels[parcel].house; return (world.x - h.origin.x) * h.right.x + (world.z - h.origin.z) * h.right.z;
        }
        public bool InBackYard(Vector3 world, out int parcel)                                                           // in a parcel and behind its building's rear wall line
        {
            parcel = LabelAt(world);
            if (parcel < 0) { parcel = -1; return false; }
            return DepthInParcel(world, parcel) > parcels[parcel].rearLine;
        }
        public Parcel ParcelOf(string stopId) { Parcel p; return stopId != null && byStop.TryGetValue(stopId, out p) ? p : null; }
        public CommonRegion CommonOf(int id) { return id >= 0 && id < commons.Length ? commons[id] : null; }
    }

    // =====================================================================================================================
    // Geometry helpers
    // =====================================================================================================================

    sealed class RoadGeo
    {
        public Road road; public int index; public double[] x, z, cum; public double length; public bool closed; public float hw;
        public RoadGeo(Road r, int index)
        {
            road = r; this.index = index; hw = r.halfWidth;
            var p = r.points ?? new Vector3[0];
            var xs = new List<double>(); var zs = new List<double>();
            for (int i = 0; i < p.Length; i++)
            {
                if (xs.Count > 0 && Math.Abs(p[i].x - xs[xs.Count - 1]) < 1e-4 && Math.Abs(p[i].z - zs[zs.Count - 1]) < 1e-4) continue;
                xs.Add(p[i].x); zs.Add(p[i].z);
            }
            // The first/last points of a closed road coincide (the duplicate is kept so every segment is i -> i+1).
            x = xs.ToArray(); z = zs.ToArray(); cum = new double[x.Length];
            for (int i = 1; i < x.Length; i++) cum[i] = cum[i - 1] + Math.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1]));
            length = x.Length > 0 ? cum[x.Length - 1] : 0;
            closed = x.Length > 3 && Math.Abs(x[0] - x[x.Length - 1]) < 0.05 && Math.Abs(z[0] - z[x.Length - 1]) < 0.05;
        }
        public int Segments { get { return Math.Max(0, x.Length - 1); } }
        public double Wrap(double s) { if (!closed) return s < 0 ? 0 : (s > length ? length : s); s %= length; return s < 0 ? s + length : s; }
        public void PointAt(double s, out double px, out double pz)
        {
            s = Wrap(s);
            int lo = 0, hi = x.Length - 2;
            while (lo < hi) { int m = (lo + hi + 1) >> 1; if (cum[m] <= s) lo = m; else hi = m - 1; }
            double l = cum[lo + 1] - cum[lo], t = l > 1e-9 ? (s - cum[lo]) / l : 0;
            px = x[lo] + (x[lo + 1] - x[lo]) * t; pz = z[lo] + (z[lo + 1] - z[lo]) * t;
        }
        /// Unit tangent of the chord s-w .. s+w (a smoothed travel direction; exact on straight stretches).
        public void TangentAt(double s, double w, out double tx, out double tz)
        {
            double a = s - w, b = s + w;
            if (!closed) { if (a < 0) a = 0; if (b > length) b = length; }
            double ax, az, bx, bz; PointAt(a, out ax, out az); PointAt(b, out bx, out bz);
            double l = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (l < 1e-6) { int i = Math.Max(0, Math.Min(x.Length - 2, SegmentAt(s))); bx = x[i + 1]; ax = x[i]; bz = z[i + 1]; az = z[i]; l = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)); }
            tx = (bx - ax) / l; tz = (bz - az) / l;
        }
        int SegmentAt(double s) { s = Wrap(s); for (int i = 0; i < x.Length - 1; i++) if (s <= cum[i + 1]) return i; return x.Length - 2; }
        /// True point-to-polyline nearest: distance, station of the nearest point, side (+1 right of travel, -1 left, 0 = past an open end).
        public double Nearest(double px, double pz, out double station, out int side)
        {
            double best = double.MaxValue; station = 0; side = 0; int n = x.Length - 1;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i + 1] - x[i], dz = z[i + 1] - z[i], l2 = dx * dx + dz * dz;
                double t = ((px - x[i]) * dx + (pz - z[i]) * dz) / l2, tc = t < 0 ? 0 : (t > 1 ? 1 : t);
                double ex = px - (x[i] + dx * tc), ez = pz - (z[i] + dz * tc), d2 = ex * ex + ez * ez;
                if (d2 < best)
                {
                    best = d2; station = cum[i] + (cum[i + 1] - cum[i]) * tc;
                    side = (px - x[i]) * dz - (pz - z[i]) * dx > 0 ? 1 : -1;
                    if (!closed && ((i == 0 && t < -1e-6) || (i == n - 1 && t > 1 + 1e-6))) side = 0;
                }
            }
            return Math.Sqrt(best);
        }
    }

    /// Bucketed segment set for exact nearest-distance queries (value = distance - per-segment offset).
    internal sealed class SegIndex
    {
        public float[] ax, az, bx, bz, off; public int[] owner; public int count;
        float x0, z0, size, radius; int nx, nz; int[][] buckets;
        public SegIndex(List<float[]> segs, List<int> owners, Rect bounds, float radius)
        {
            count = segs.Count; ax = new float[count]; az = new float[count]; bx = new float[count]; bz = new float[count]; off = new float[count]; owner = owners.ToArray();
            for (int i = 0; i < count; i++) { ax[i] = segs[i][0]; az[i] = segs[i][1]; bx[i] = segs[i][2]; bz[i] = segs[i][3]; off[i] = segs[i][4]; }
            this.radius = radius; size = 24f; x0 = bounds.xMin; z0 = bounds.yMin;
            nx = Mathf.Max(1, Mathf.CeilToInt(bounds.width / size)); nz = Mathf.Max(1, Mathf.CeilToInt(bounds.height / size));
            var lists = new List<int>[nx * nz];
            for (int i = 0; i < count; i++)
            {
                float minX = Mathf.Min(ax[i], bx[i]) - radius, maxX = Mathf.Max(ax[i], bx[i]) + radius, minZ = Mathf.Min(az[i], bz[i]) - radius, maxZ = Mathf.Max(az[i], bz[i]) + radius;
                int i0 = Mathf.Max(0, Mathf.FloorToInt((minX - x0) / size)), i1 = Mathf.Min(nx - 1, Mathf.FloorToInt((maxX - x0) / size));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - z0) / size)), j1 = Mathf.Min(nz - 1, Mathf.FloorToInt((maxZ - z0) / size));
                for (int j = j0; j <= j1; j++) for (int k = i0; k <= i1; k++) { var l = lists[j * nx + k]; if (l == null) lists[j * nx + k] = l = new List<int>(); l.Add(i); }
            }
            buckets = new int[nx * nz][];
            for (int i = 0; i < lists.Length; i++) buckets[i] = lists[i] == null ? null : lists[i].ToArray();
        }
        float Dist(int i, float px, float pz)
        {
            float dx = bx[i] - ax[i], dz = bz[i] - az[i], l2 = dx * dx + dz * dz;
            float t = l2 > 1e-9f ? ((px - ax[i]) * dx + (pz - az[i]) * dz) / l2 : 0f; t = t < 0 ? 0 : (t > 1 ? 1 : t);
            float ex = px - (ax[i] + dx * t), ez = pz - (az[i] + dz * t);
            return Mathf.Sqrt(ex * ex + ez * ez) - off[i];
        }
        public int Side(int i, float px, float pz) { return (px - ax[i]) * (bz[i] - az[i]) - (pz - az[i]) * (bx[i] - ax[i]) > 0 ? 1 : -1; }
        public float Query(float px, float pz, out int seg)
        {
            seg = -1; float best = float.PositiveInfinity;
            int k = Mathf.FloorToInt((px - x0) / size), j = Mathf.FloorToInt((pz - z0) / size);
            if (k >= 0 && j >= 0 && k < nx && j < nz)
            {
                var b = buckets[j * nx + k];
                if (b != null) for (int n = 0; n < b.Length; n++) { float d = Dist(b[n], px, pz); if (d < best) { best = d; seg = b[n]; } }
                if (seg >= 0 && best + off[seg] <= radius - 2f) return best;
            }
            for (int i = 0; i < count; i++) { float d = Dist(i, px, pz); if (d < best) { best = d; seg = i; } }
            return best;
        }
    }

    /// Nearest-point field of one road over its reach: squared distance, station and side per cell.
    sealed class RoadField { public RoadGeo road; public int i0, j0, i1, j1, fw; public float reach2; public float[] d2, s; public sbyte[] side; }

    sealed class Chain
    {
        public string name; public RoadGeo road; public int side; public int[] houses; public double[] u;   // unwrapped stations, increasing
        public bool full; public double lo, hi; public float maxDepth; public bool court;
        public double[] divS, divQx, divQz, divTx, divTz;   // divider k sits between house k-1 and k (k = 0 only for a full ring)
    }

    static double Mod(double a, double m) { a %= m; return a < 0 ? a + m : a; }
    static double WrapSigned(double a, double m) { a = Mod(a, m); return a > m * .5 ? a - m : a; }

    static bool InQuad(ref Quad q, float px, float pz, float margin)
    {
        // Convex quad, either winding: inside when the point is within `margin` of all four half planes.
        float area = (q.b.x - q.a.x) * (q.c.z - q.a.z) - (q.b.z - q.a.z) * (q.c.x - q.a.x) + (q.c.x - q.a.x) * (q.d.z - q.a.z) - (q.c.z - q.a.z) * (q.d.x - q.a.x);
        float sign = area >= 0 ? 1f : -1f;
        return Half(q.a, q.b, px, pz, sign) >= -margin && Half(q.b, q.c, px, pz, sign) >= -margin && Half(q.c, q.d, px, pz, sign) >= -margin && Half(q.d, q.a, px, pz, sign) >= -margin;
    }
    static float Half(Vector3 a, Vector3 b, float px, float pz, float sign)
    {
        float dx = b.x - a.x, dz = b.z - a.z, l = Mathf.Sqrt(dx * dx + dz * dz);
        if (l < 1e-6f) return float.PositiveInfinity;
        return sign * (dx * (pz - a.z) - dz * (px - a.x)) / l;
    }
    static Vector3 QuadCorner(ref Quad q, int i) { return i == 0 ? q.a : i == 1 ? q.b : i == 2 ? q.c : q.d; }

    // =====================================================================================================================
    // Build
    // =====================================================================================================================

    public static Result Build(Input input)
    {
        if (input == null) throw new ArgumentNullException("input");
        var st = input.settings ?? (input.settings = new Settings());
        var houses = input.houses ?? new House[0];
        var roadsIn = input.roads ?? new Road[0];
        float cell = Mathf.Max(0.1f, st.cell);
        int W = Mathf.Max(1, Mathf.CeilToInt(input.bounds.width / cell)), H = Mathf.Max(1, Mathf.CeilToInt(input.bounds.height / cell));
        float x0 = input.bounds.xMin, z0 = input.bounds.yMin;
        int N = W * H;
        var res = new Result { input = input, gridWidth = W, gridHeight = H, cell = cell, originX = x0, originZ = z0 };
        var label = new int[N]; var nearRoad = new short[N];
        for (int i = 0; i < N; i++) { label[i] = Common; nearRoad[i] = -1; }

        var roads = new RoadGeo[roadsIn.Length];
        var roadById = new Dictionary<string, RoadGeo>();
        for (int i = 0; i < roadsIn.Length; i++) { roads[i] = new RoadGeo(roadsIn[i], i); if (roadsIn[i].id != null && !roadById.ContainsKey(roadsIn[i].id)) roadById[roadsIn[i].id] = roads[i]; }

        // ---- exact query indices -------------------------------------------------------------------------------------
        {
            var segs = new List<float[]>(); var own = new List<int>();
            foreach (var r in roads) for (int i = 0; i < r.Segments; i++) { segs.Add(new[] { (float)r.x[i], (float)r.z[i], (float)r.x[i + 1], (float)r.z[i + 1], r.hw }); own.Add(r.index); }
            if (segs.Count > 0) res.roadIndex = new SegIndex(segs, own, input.bounds, 40f);
            res.riverIndex = PolyIndex(input.river, input.bounds, st.riverSetback + 24f);
            res.coastIndex = PolyIndex(input.coast, input.bounds, st.coastSetback + 24f);
        }

        // ---- 1. masks ------------------------------------------------------------------------------------------------
        // Water first, then reserved quads, then road reserve on top (a bridge deck is road, not water).
        if (input.river != null && input.river.Length > 1) StampPolyline(input.river, st.riverSetback, label, Water, W, H, x0, z0, cell);
        if (input.coast != null && input.coast.Length > 1)
        {
            var band = new bool[N];
            StampBand(input.coast, st.coastSetback, band, W, H, x0, z0, cell);
            bool any = false; for (int i = 0; i < N && !any; i++) any = band[i];
            if (any)
            {
                // Everything the houses cannot reach without crossing the coast band is sea.
                var reach = new bool[N]; var queue = new int[N]; int qh = 0, qt = 0;
                foreach (var h in houses)
                {
                    int ix = Mathf.FloorToInt((h.origin.x - x0) / cell), iz = Mathf.FloorToInt((h.origin.z - z0) / cell);
                    if (ix < 0 || iz < 0 || ix >= W || iz >= H) continue;
                    int c = iz * W + ix; if (band[c] || reach[c]) continue; reach[c] = true; queue[qt++] = c;
                }
                if (qt > 0)
                {
                    while (qh < qt)
                    {
                        int c = queue[qh++], cx = c % W, cz = c / W;
                        if (cx > 0 && !reach[c - 1] && !band[c - 1]) { reach[c - 1] = true; queue[qt++] = c - 1; }
                        if (cx < W - 1 && !reach[c + 1] && !band[c + 1]) { reach[c + 1] = true; queue[qt++] = c + 1; }
                        if (cz > 0 && !reach[c - W] && !band[c - W]) { reach[c - W] = true; queue[qt++] = c - W; }
                        if (cz < H - 1 && !reach[c + W] && !band[c + W]) { reach[c + W] = true; queue[qt++] = c + W; }
                    }
                    for (int i = 0; i < N; i++)
                    {
                        if (band[i]) { label[i] = Water; continue; }
                        if (reach[i]) continue;
                        label[i] = Water;
                        if (res.seaSign == 0) { int seg; float px = x0 + (i % W + .5f) * cell, pz = z0 + (i / W + .5f) * cell; res.coastIndex.Query(px, pz, out seg); if (seg >= 0) res.seaSign = res.coastIndex.Side(seg, px, pz); }
                    }
                    if (res.seaSign == 0 && houses.Length > 0)
                    {
                        // The sea itself is outside the bounds: take the side opposite the first house.
                        int seg; res.coastIndex.Query(houses[0].origin.x, houses[0].origin.z, out seg);
                        if (seg >= 0) res.seaSign = -res.coastIndex.Side(seg, houses[0].origin.x, houses[0].origin.z);
                    }
                }
                else for (int i = 0; i < N; i++) if (band[i]) label[i] = Water;
            }
        }
        if (input.reserved != null)
            for (int q = 0; q < input.reserved.Length; q++) StampQuad(ref input.reserved[q], st.reservedMargin, W, H, x0, z0, cell, (c) => { label[c] = Reserved; });
        {
            // Road reserve with the nearest road per cell (the edge graph splits a parcel's road boundary per road).
            var edge = new float[N]; for (int i = 0; i < N; i++) edge[i] = float.MaxValue;
            foreach (var r in roads)
            {
                float reach = r.hw + st.sidewalk;
                for (int s = 0; s < r.Segments; s++)
                {
                    double ax = r.x[s], az = r.z[s], bx = r.x[s + 1], bz = r.z[s + 1], dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
                    int i0 = Mathf.Max(0, Mathf.FloorToInt((float)((Math.Min(ax, bx) - reach - x0) / cell))), i1 = Mathf.Min(W - 1, Mathf.FloorToInt((float)((Math.Max(ax, bx) + reach - x0) / cell)));
                    int j0 = Mathf.Max(0, Mathf.FloorToInt((float)((Math.Min(az, bz) - reach - z0) / cell))), j1 = Mathf.Min(H - 1, Mathf.FloorToInt((float)((Math.Max(az, bz) + reach - z0) / cell)));
                    for (int j = j0; j <= j1; j++)
                    {
                        double pz = z0 + (j + .5) * cell;
                        for (int i = i0; i <= i1; i++)
                        {
                            double px = x0 + (i + .5) * cell;
                            double t = ((px - ax) * dx + (pz - az) * dz) / l2; t = t < 0 ? 0 : (t > 1 ? 1 : t);
                            double ex = px - (ax + dx * t), ez = pz - (az + dz * t);
                            float d = (float)Math.Sqrt(ex * ex + ez * ez) - r.hw;
                            if (d >= st.sidewalk) continue;
                            int c = j * W + i;
                            if (d < edge[c]) { edge[c] = d; nearRoad[c] = (short)r.index; label[c] = RoadReserve; }
                        }
                    }
                }
            }
        }
        var mask = new sbyte[N];
        for (int i = 0; i < N; i++) mask[i] = label[i] < Common ? (sbyte)label[i] : (sbyte)0;
        res.mask = mask;

        // ---- 2. chains -----------------------------------------------------------------------------------------------
        var chainOf = new int[houses.Length]; for (int i = 0; i < chainOf.Length; i++) chainOf[i] = -1;
        var chains = BuildChains(input, st, roads, roadById, res, chainOf, mask, W, H, x0, z0, cell);

        // ---- 3. claims: nearer frontage wins -------------------------------------------------------------------------
        var best = new float[N]; var bestR = new float[N]; var labelR = new int[N];
        for (int i = 0; i < N; i++) { best[i] = float.MaxValue; bestR[i] = float.MaxValue; labelR[i] = -1; }
        float thin = Mathf.Max(0f, st.thinStrip) * 2f;   // relaxed claims reach twice the strip width (see AbsorbBehind)
        // One nearest-point field per road that carries chains, shared by its chains.
        var roadsWithChains = new List<RoadGeo>();
        foreach (var c in chains) if (!roadsWithChains.Contains(c.road)) roadsWithChains.Add(c.road);
        var fields = new List<RoadField>();
        foreach (var r in roadsWithChains)
        {
            float maxD = 0; foreach (var c in chains) if (c.road == r) maxD = Mathf.Max(maxD, c.maxDepth);
            var fld = BuildField(r, r.hw + st.sidewalk + maxD + thin + cell, W, H, x0, z0, cell);
            if (fld == null) continue;
            fields.Add(fld);
            int fi0 = fld.i0, fi1 = fld.i1, fj0 = fld.j0, fj1 = fld.j1, fw = fld.fw; float reach2 = fld.reach2;
            var fd2 = fld.d2; var fs = fld.s; var fside = fld.side;
            foreach (var c in chains)
            {
                if (c.road != r) continue;
                int n = c.houses.Length; double L = r.length;
                float maxd = c.maxDepth, maxdR = c.maxDepth + thin; float baseOff = r.hw + st.sidewalk;
                bool wrapRoad = r.closed;
                for (int j = fj0; j <= fj1; j++)
                {
                    int row = (j - fj0) * fw - fi0; float pz = z0 + (j + .5f) * cell;
                    for (int i = fi0; i <= fi1; i++)
                    {
                        int f = row + i;
                        if (fside[f] != c.side || fd2[f] >= reach2) continue;
                        int cidx = j * W + i;
                        if (mask[cidx] != 0) continue;
                        float depth = Mathf.Sqrt(fd2[f]) - baseOff;
                        if (depth <= 0f || depth >= maxdR) continue;
                        bool canStrict = depth < maxd && depth < best[cidx], canRelax = depth < bestR[cidx];
                        if (!canStrict && !canRelax) continue;
                        double u = fs[f];
                        if (c.full) u = c.lo + Mod(u - c.lo, L);
                        else if (wrapRoad) { double b0 = c.lo - st.thinStrip; u = b0 + Mod(u - b0, L); }
                        if (!c.full && (u < c.lo - st.thinStrip || u > c.hi + st.thinStrip)) continue;
                        bool inExtent = c.full || (u >= c.lo && u <= c.hi);
                        int who = c.houses[HouseAt(c, u, x0 + (i + .5f) * cell, pz)];
                        if (canStrict && inExtent) { best[cidx] = depth; label[cidx] = who; }
                        if (canRelax) { bestR[cidx] = depth; labelR[cidx] = who; }
                    }
                }
            }
        }
        best = null; bestR = null;

        // ---- 4. forced cells: footprints and keepIn quads ------------------------------------------------------------
        var stamp = new int[N]; for (int i = 0; i < N; i++) stamp[i] = -1;
        int[] stampBlocked = new int[houses.Length], stampClash = new int[houses.Length];
        for (int h = 0; h < houses.Length; h++)
        {
            int hh = h;
            Action<int> put = (c) =>
            {
                if (mask[c] != 0) { stampBlocked[hh]++; return; }
                if (stamp[c] >= 0 && stamp[c] != hh) { stampClash[hh]++; return; }
                stamp[c] = hh; label[c] = hh;
            };
            StampQuad(ref houses[h].footprint, st.footprintMargin, W, H, x0, z0, cell, put);
        }
        for (int h = 0; h < houses.Length; h++)
        {
            if (houses[h].keepIn == null) continue;
            int hh = h;
            Action<int> put = (c) =>
            {
                if (mask[c] != 0) { stampBlocked[hh]++; return; }
                if (stamp[c] >= 0 && stamp[c] != hh) { stampClash[hh]++; return; }
                stamp[c] = hh; label[c] = hh;
            };
            for (int q = 0; q < houses[h].keepIn.Length; q++) StampQuad(ref houses[h].keepIn[q], st.keepInMargin, W, H, x0, z0, cell, put);
        }
        for (int h = 0; h < houses.Length; h++)
        {
            if (stampBlocked[h] > 0) res.notes.Add(houses[h].stopId + ": " + (stampBlocked[h] * cell * cell).ToString("0.#") + " m2 of its footprint/keepIn (with margin) lies on road reserve, water or reserved land");
            if (stampClash[h] > 0) res.notes.Add(houses[h].stopId + ": " + (stampClash[h] * cell * cell).ToString("0.#") + " m2 of its footprint/keepIn (with margin) overlaps another house's forced cells");
        }
        var forced = new bool[N]; for (int i = 0; i < N; i++) forced[i] = stamp[i] >= 0;
        stamp = null;

        // ---- 5. connectivity: keep the component touching the house --------------------------------------------------
        var stack = new int[N];
        KeepHouseComponents(houses, label, W, H, x0, z0, cell, stack);

        // ---- 6. thin strips, tiny pockets, spikes --------------------------------------------------------------------
        for (int ci = 0; ci < chains.Count; ci++)
            foreach (var fld in fields) if (fld.road == chains[ci].road) AbsorbBehind(chains[ci], ci, chainOf, fld, label, labelR, st, W, H, x0, z0, cell);
        labelR = null; fields = null;
        FillNotches(label, W, H, cell, st, stack);
        MergeTinyCommons(label, nearRoad, res.roadIndex, W, H, x0, z0, cell, st, stack);
        TrimSpikes(label, forced, nearRoad, res.roadIndex, W, H, x0, z0, cell, st, stack, houses.Length);
        // Forced cells and trimming can have cut a parcel in two: keep the piece with the house.
        KeepHouseComponents(houses, label, W, H, x0, z0, cell, stack);
        MergeTinyCommons(label, nearRoad, res.roadIndex, W, H, x0, z0, cell, st, stack);

        res.label = label;

        // ---- 7. commons ----------------------------------------------------------------------------------------------
        var commonGrid = new int[N]; for (int i = 0; i < N; i++) commonGrid[i] = -1;
        var commons = new List<CommonRegion>(); var commonSeed = new List<int>();
        for (int i = 0; i < N; i++)
        {
            if (label[i] != Common || commonGrid[i] >= 0) continue;
            int id = commons.Count; int sp = 0; stack[sp++] = i; commonGrid[i] = id;
            double sx = 0, sz = 0; int cnt = 0, minX = W, maxX = -1, minZ = H, maxZ = -1; bool tb = false, tw = false;
            while (sp > 0)
            {
                int c = stack[--sp], cx = c % W, cz = c / W; cnt++; sx += cx; sz += cz;
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx; if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
                if (cx == 0 || cz == 0 || cx == W - 1 || cz == H - 1) tb = true;
                if (cx > 0) { int o = c - 1; if (label[o] == Common) { if (commonGrid[o] < 0) { commonGrid[o] = id; stack[sp++] = o; } } else if (label[o] == Water) tw = true; }
                if (cx < W - 1) { int o = c + 1; if (label[o] == Common) { if (commonGrid[o] < 0) { commonGrid[o] = id; stack[sp++] = o; } } else if (label[o] == Water) tw = true; }
                if (cz > 0) { int o = c - W; if (label[o] == Common) { if (commonGrid[o] < 0) { commonGrid[o] = id; stack[sp++] = o; } } else if (label[o] == Water) tw = true; }
                if (cz < H - 1) { int o = c + W; if (label[o] == Common) { if (commonGrid[o] < 0) { commonGrid[o] = id; stack[sp++] = o; } } else if (label[o] == Water) tw = true; }
            }
            commons.Add(new CommonRegion
            {
                id = id, area = cnt * cell * cell, touchesBounds = tb, touchesWater = tw,
                bounds = Rect.MinMaxRect(x0 + minX * cell, z0 + minZ * cell, x0 + (maxX + 1) * cell, z0 + (maxZ + 1) * cell),
                centroid = new Vector3(x0 + ((float)(sx / cnt) + .5f) * cell, 0f, z0 + ((float)(sz / cnt) + .5f) * cell)
            });
            commonSeed.Add(i);
        }
        res.commonGrid = commonGrid; res.commons = commons.ToArray();

        // ---- 8. parcels (raster metrics) -----------------------------------------------------------------------------
        var parcels = new Parcel[houses.Length];
        res.byStop = new Dictionary<string, Parcel>();
        var firstCell = new int[houses.Length]; for (int i = 0; i < firstCell.Length; i++) firstCell[i] = -1;
        {
            var cnt = new int[houses.Length]; var sx = new double[houses.Length]; var sz = new double[houses.Length];
            var bcnt = new int[houses.Length]; var bx = new double[houses.Length]; var bz = new double[houses.Length];
            for (int h = 0; h < houses.Length; h++)
            {
                var hs = houses[h]; float fl = float.MaxValue, rl = float.MinValue;
                for (int k = 0; k < 4; k++) { var c = QuadCorner(ref hs.footprint, k); float d = (c.x - hs.origin.x) * hs.inward.x + (c.z - hs.origin.z) * hs.inward.z; fl = Mathf.Min(fl, d); rl = Mathf.Max(rl, d); }
                var ch = chainOf[h] >= 0 ? chains[chainOf[h]] : null;
                parcels[h] = new Parcel { index = h, house = hs, frontLine = fl, rearLine = rl, chain = ch != null ? ch.name : (hs.roadId + "|" + hs.side + "|x"), court = ch != null && ch.court };
                if (hs.stopId != null && !res.byStop.ContainsKey(hs.stopId)) res.byStop[hs.stopId] = parcels[h];
            }
            for (int j = 0; j < H; j++)
            {
                float pz = z0 + (j + .5f) * cell;
                for (int i = 0; i < W; i++)
                {
                    int l = label[j * W + i]; if (l < 0) continue;
                    if (firstCell[l] < 0) firstCell[l] = j * W + i;
                    float px = x0 + (i + .5f) * cell; cnt[l]++; sx[l] += px; sz[l] += pz;
                    var hs = houses[l];
                    if ((px - hs.origin.x) * hs.inward.x + (pz - hs.origin.z) * hs.inward.z > parcels[l].rearLine) { bcnt[l]++; bx[l] += px; bz[l] += pz; }
                }
            }
            for (int h = 0; h < houses.Length; h++)
            {
                var p = parcels[h]; p.area = cnt[h] * cell * cell; p.backYardArea = bcnt[h] * cell * cell;
                p.centroid = cnt[h] > 0 ? new Vector3((float)(sx[h] / cnt[h]), 0f, (float)(sz[h] / cnt[h])) : houses[h].origin;
                p.backYardCentroid = bcnt[h] > 0 ? new Vector3((float)(bx[h] / bcnt[h]), 0f, (float)(bz[h] / bcnt[h])) : p.centroid;
                p.outline = new Vector3[0]; p.neighbours = new int[0];
            }
        }
        res.parcels = parcels;
        {
            var names = new List<string>(); foreach (var c in chains) names.Add(c.name);
            res.chains = names.ToArray(); res.chainParcels = new int[chains.Count][];
            for (int i = 0; i < chains.Count; i++) res.chainParcels[i] = (int[])chains[i].houses.Clone();
        }

        // ---- 9. vectorise --------------------------------------------------------------------------------------------
        Vectorise(res, roads, nearRoad, firstCell, commonSeed);
        return res;
    }

    static SegIndex PolyIndex(Vector3[] pts, Rect bounds, float radius)
    {
        if (pts == null || pts.Length < 2) return null;
        var segs = new List<float[]>(); var own = new List<int>();
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            if ((pts[i + 1] - pts[i]).sqrMagnitude < 1e-8f) continue;
            segs.Add(new[] { pts[i].x, pts[i].z, pts[i + 1].x, pts[i + 1].z, 0f }); own.Add(0);
        }
        return segs.Count == 0 ? null : new SegIndex(segs, own, bounds, radius);
    }

    static void StampPolyline(Vector3[] pts, float radius, int[] label, int code, int W, int H, float x0, float z0, float cell)
    {
        for (int s = 0; s + 1 < pts.Length; s++)
        {
            float ax = pts[s].x, az = pts[s].z, bx = pts[s + 1].x, bz = pts[s + 1].z, dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            if (l2 < 1e-8f) continue;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(ax, bx) - radius - x0) / cell)), i1 = Mathf.Min(W - 1, Mathf.FloorToInt((Mathf.Max(ax, bx) + radius - x0) / cell));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(az, bz) - radius - z0) / cell)), j1 = Mathf.Min(H - 1, Mathf.FloorToInt((Mathf.Max(az, bz) + radius - z0) / cell));
            float r2 = radius * radius;
            for (int j = j0; j <= j1; j++)
            {
                float pz = z0 + (j + .5f) * cell;
                for (int i = i0; i <= i1; i++)
                {
                    float px = x0 + (i + .5f) * cell;
                    float t = ((px - ax) * dx + (pz - az) * dz) / l2; t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    float ex = px - (ax + dx * t), ez = pz - (az + dz * t);
                    if (ex * ex + ez * ez < r2) label[j * W + i] = code;
                }
            }
        }
    }
    static void StampBand(Vector3[] pts, float radius, bool[] band, int W, int H, float x0, float z0, float cell)
    {
        for (int s = 0; s + 1 < pts.Length; s++)
        {
            float ax = pts[s].x, az = pts[s].z, bx = pts[s + 1].x, bz = pts[s + 1].z, dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            if (l2 < 1e-8f) continue;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(ax, bx) - radius - x0) / cell)), i1 = Mathf.Min(W - 1, Mathf.FloorToInt((Mathf.Max(ax, bx) + radius - x0) / cell));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(az, bz) - radius - z0) / cell)), j1 = Mathf.Min(H - 1, Mathf.FloorToInt((Mathf.Max(az, bz) + radius - z0) / cell));
            float r2 = radius * radius;
            for (int j = j0; j <= j1; j++)
            {
                float pz = z0 + (j + .5f) * cell;
                for (int i = i0; i <= i1; i++)
                {
                    float px = x0 + (i + .5f) * cell;
                    float t = ((px - ax) * dx + (pz - az) * dz) / l2; t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    float ex = px - (ax + dx * t), ez = pz - (az + dz * t);
                    if (ex * ex + ez * ez < r2) band[j * W + i] = true;
                }
            }
        }
    }
    static void StampQuad(ref Quad q, float margin, int W, int H, float x0, float z0, float cell, Action<int> put)
    {
        float minX = Mathf.Min(Mathf.Min(q.a.x, q.b.x), Mathf.Min(q.c.x, q.d.x)) - margin, maxX = Mathf.Max(Mathf.Max(q.a.x, q.b.x), Mathf.Max(q.c.x, q.d.x)) + margin;
        float minZ = Mathf.Min(Mathf.Min(q.a.z, q.b.z), Mathf.Min(q.c.z, q.d.z)) - margin, maxZ = Mathf.Max(Mathf.Max(q.a.z, q.b.z), Mathf.Max(q.c.z, q.d.z)) + margin;
        if (!(maxX > minX) || !(maxZ > minZ)) return;
        int i0 = Mathf.Max(0, Mathf.FloorToInt((minX - x0) / cell)), i1 = Mathf.Min(W - 1, Mathf.FloorToInt((maxX - x0) / cell));
        int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - z0) / cell)), j1 = Mathf.Min(H - 1, Mathf.FloorToInt((maxZ - z0) / cell));
        for (int j = j0; j <= j1; j++)
        {
            float pz = z0 + (j + .5f) * cell;
            for (int i = i0; i <= i1; i++) if (InQuad(ref q, x0 + (i + .5f) * cell, pz, margin)) put(j * W + i);
        }
    }

    // ---- chains ------------------------------------------------------------------------------------------------------

    static List<Chain> BuildChains(Input input, Settings st, RoadGeo[] roads, Dictionary<string, RoadGeo> roadById, Result res, int[] chainOf,
        sbyte[] mask, int W, int H, float x0, float z0, float cell)
    {
        var houses = input.houses ?? new House[0];
        // rows keyed "roadId|side", processed in ordinal order
        var rowKeys = new List<string>(); var rows = new Dictionary<string, List<int>>();
        for (int h = 0; h < houses.Length; h++)
        {
            var hs = houses[h];
            if (hs.roadId == null || !roadById.ContainsKey(hs.roadId) || roadById[hs.roadId].Segments < 1) { res.notes.Add((hs.stopId ?? ("house " + h)) + ": road '" + hs.roadId + "' is not in the plan; no parcel"); continue; }
            string key = hs.roadId + "|" + (hs.side >= 0 ? 1 : -1);
            List<int> l; if (!rows.TryGetValue(key, out l)) { rows[key] = l = new List<int>(); rowKeys.Add(key); }
            l.Add(h);
        }
        rowKeys.Sort(string.CompareOrdinal);
        var chains = new List<Chain>();
        double threshold = 2.0 * st.endCap + st.endSnap;
        foreach (var key in rowKeys)
        {
            var list = rows[key]; var r = roadById[houses[list[0]].roadId]; int side = houses[list[0]].side >= 0 ? 1 : -1; double L = r.length;
            var stn = new double[houses.Length];
            foreach (int h in list) stn[h] = r.Wrap(houses[h].station);
            list.Sort((a, b) => { int c = stn[a].CompareTo(stn[b]); return c != 0 ? c : string.CompareOrdinal(houses[a].stopId, houses[b].stopId); });
            int m = list.Count;
            // split into groups at big gaps
            var groups = new List<List<int>>(); var fullRing = false;
            if (!r.closed)
            {
                groups.Add(new List<int> { list[0] });
                for (int i = 1; i < m; i++) { if (stn[list[i]] - stn[list[i - 1]] > threshold) groups.Add(new List<int>()); groups[groups.Count - 1].Add(list[i]); }
            }
            else
            {
                int start = -1; double biggest = -1; int biggestAt = 0;
                for (int i = 0; i < m; i++)
                {
                    double gap = i < m - 1 ? stn[list[i + 1]] - stn[list[i]] : stn[list[0]] + L - stn[list[m - 1]];
                    if (gap > biggest) { biggest = gap; biggestAt = i; }
                    if (gap > threshold && start < 0) start = (i + 1) % m;
                }
                if (start < 0) { fullRing = true; start = (biggestAt + 1) % m; }
                groups.Add(new List<int>());
                for (int n = 0; n < m; n++)
                {
                    int i = (start + n) % m;
                    if (n > 0) { int prev = (i + m - 1) % m; double gap = Mod(stn[list[i]] - stn[list[prev]], L); if (gap > threshold) groups.Add(new List<int>()); }
                    groups[groups.Count - 1].Add(list[i]);
                }
            }
            string district = r.road.districtId ?? houses[list[0]].districtId;
            bool court = r.closed && L < st.courtMaxLength;
            float maxDepth = court ? st.depthCourt : (district == st.meadowsDistrictId ? st.depthMeadows : st.depthAcres);
            for (int g = 0; g < groups.Count; g++)
            {
                var grp = groups[g]; int n = grp.Count;
                var c = new Chain { name = key + "|" + g, road = r, side = side, houses = grp.ToArray(), u = new double[n], full = fullRing, maxDepth = maxDepth, court = court };
                for (int k = 0; k < n; k++)
                {
                    double s = stn[grp[k]];
                    c.u[k] = k == 0 ? s : c.u[k - 1] + (r.closed ? Mod(s - stn[grp[k - 1]], L) : s - stn[grp[k - 1]]);
                    chainOf[grp[k]] = chains.Count;
                }
                // footprint extents along the frontage (unwrapped, relative to each house's own station)
                // (kmin/kmax: the same including the keepIn quads, preferred whenever they leave a free gap)
                var fmin = new double[n]; var fmax = new double[n]; var kmin = new double[n]; var kmax = new double[n];
                for (int k = 0; k < n; k++)
                {
                    fmin[k] = double.MaxValue; fmax[k] = double.MinValue; var hs = houses[grp[k]];
                    for (int q = 0; q < 4; q++)
                    {
                        var p = QuadCorner(ref hs.footprint, q); double s; int sd; r.Nearest(p.x, p.z, out s, out sd);
                        double uu = r.closed ? c.u[k] + WrapSigned(s - c.u[k], L) : s;
                        fmin[k] = Math.Min(fmin[k], uu); fmax[k] = Math.Max(fmax[k], uu);
                    }
                    kmin[k] = fmin[k]; kmax[k] = fmax[k];
                    if (hs.keepIn != null) for (int qi = 0; qi < hs.keepIn.Length; qi++) for (int q = 0; q < 4; q++)
                            {
                                var p = QuadCorner(ref hs.keepIn[qi], q); double s; int sd; r.Nearest(p.x, p.z, out s, out sd);
                                double uu = r.closed ? c.u[k] + WrapSigned(s - c.u[k], L) : s;
                                kmin[k] = Math.Min(kmin[k], uu); kmax[k] = Math.Max(kmax[k], uu);
                            }
                    double os; int osd; double od = r.Nearest(hs.origin.x, hs.origin.z, out os, out osd);
                    double du = r.closed ? WrapSigned(os - c.u[k], L) : os - c.u[k];
                    if (Math.Abs(du) > 2.5) res.notes.Add(hs.stopId + ": station " + houses[grp[k]].station.ToString("0.0") + " is " + du.ToString("0.0") + " m away from where its origin projects on '" + r.road.id + "'");
                    if (osd != 0 && osd != side) res.notes.Add(hs.stopId + ": origin lies on side " + osd + " of '" + r.road.id + "' but side is " + side);
                }
                c.divS = new double[n]; c.divQx = new double[n]; c.divQz = new double[n]; c.divTx = new double[n]; c.divTz = new double[n];
                for (int k = c.full ? 0 : 1; k < n; k++)
                {
                    if (n < 2) break;
                    int a = k > 0 ? k - 1 : n - 1, b = k;                // divider between house a (lower station) and house b
                    double wrapL = k > 0 ? 0 : L;
                    double ua = c.u[a] - wrapL, amax = fmax[a] - wrapL, akmax = kmax[a] - wrapL;
                    double s0;
                    if (akmax + 1.0 < kmin[b]) s0 = (akmax + kmin[b]) * .5;
                    else if (amax < fmin[b]) s0 = (amax + fmin[b]) * .5;
                    else s0 = (ua + c.u[b]) * .5;
                    double px, pz, tx, tz; r.PointAt(s0, out px, out pz); r.TangentAt(s0, st.dividerChord, out tx, out tz);
                    // refine across the chord direction: midpoint of the free gap between the two facing side walls
                    double ga = double.MinValue, gb = double.MaxValue, gka, gkb;
                    for (int q = 0; q < 4; q++)
                    {
                        var pa = QuadCorner(ref houses[grp[a]].footprint, q); ga = Math.Max(ga, (pa.x - px) * tx + (pa.z - pz) * tz);
                        var pb = QuadCorner(ref houses[grp[b]].footprint, q); gb = Math.Min(gb, (pb.x - px) * tx + (pb.z - pz) * tz);
                    }
                    gka = ga; gkb = gb;
                    var ka = houses[grp[a]].keepIn; var kb = houses[grp[b]].keepIn;
                    if (ka != null) for (int qi = 0; qi < ka.Length; qi++) for (int q = 0; q < 4; q++) { var pa = QuadCorner(ref ka[qi], q); gka = Math.Max(gka, (pa.x - px) * tx + (pa.z - pz) * tz); }
                    if (kb != null) for (int qi = 0; qi < kb.Length; qi++) for (int q = 0; q < 4; q++) { var pb = QuadCorner(ref kb[qi], q); gkb = Math.Min(gkb, (pb.x - px) * tx + (pb.z - pz) * tz); }
                    double off = 0;
                    if (gka + 1.0 < gkb) off = (gka + gkb) * .5;
                    else if (ga < gb) off = (ga + gb) * .5;
                    else res.notes.Add(houses[grp[a]].stopId + " / " + houses[grp[b]].stopId + ": buildings overlap along the frontage by " + (ga - gb).ToString("0.0") + " m; divider falls back to the origin midpoint");
                    c.divS[k] = s0 + off; c.divQx[k] = px + tx * off; c.divQz[k] = pz + tz * off; c.divTx[k] = tx; c.divTz[k] = tz;
                }
                for (int k = 2; k < n; k++) if (c.divS[k] < c.divS[k - 1]) c.divS[k] = c.divS[k - 1];
                if (c.full) { c.lo = n > 1 ? c.divS[0] : c.u[0] - L * .5; c.hi = c.lo + L; }
                else
                {
                    c.lo = c.u[0] - ExtendEnd(c, -1, c.u[0], c.u[0] - fmin[0], roads, st, mask, W, H, x0, z0, cell);
                    c.hi = c.u[n - 1] + ExtendEnd(c, 1, c.u[n - 1], fmax[n - 1] - c.u[n - 1], roads, st, mask, W, H, x0, z0, cell);
                    if (r.closed && c.hi - c.lo + 2 * st.thinStrip >= L) { double over = (c.hi - c.lo + 2 * st.thinStrip - L) * .5 + .5; c.lo += over; c.hi -= over; }
                }
                chains.Add(c);
            }
        }
        return chains;
    }

    /// How far a row end runs past its last house (metres of station): up to endCap, snapped to the corner when the
    /// frontage runs into another road's reserve (or water / reserved land, or the end of the road) before
    /// endCap + endSnap, and never closer than endSideMin to the building's outer side wall where the frontage allows.
    static double ExtendEnd(Chain c, int dir, double from, double footOuter, RoadGeo[] roads, Settings st, sbyte[] mask, int W, int H, float x0, float z0, float cell)
    {
        var r = c.road; double need = Math.Max(0, footOuter) + st.endSideMin;
        double want = Math.Max(st.endCap, need), limit = want + st.endSnap, step = 0.5, reach = 0;
        double lat = r.hw + st.sidewalk + 1.0;
        while (reach + step <= limit)
        {
            double s = from + dir * (reach + step);
            if (!r.closed && (s < 0 || s > r.length)) break;
            double px, pz, tx, tz; r.PointAt(s, out px, out pz); r.TangentAt(s, 0.75, out tx, out tz);
            double nx = tz * c.side, nz = -tx * c.side;       // right of travel = (tz, -tx)
            bool free = true;
            for (int probe = 0; probe < 2 && free; probe++)
            {
                double qx = px + nx * (lat + probe * 4.0), qz = pz + nz * (lat + probe * 4.0);
                int ix = (int)Math.Floor((qx - x0) / cell), iz = (int)Math.Floor((qz - z0) / cell);
                if (ix < 0 || iz < 0 || ix >= W || iz >= H) { free = false; break; }
                if (mask[iz * W + ix] != 0) free = false;
            }
            if (!free) break;
            reach += step;
        }
        return reach + step > limit ? want : reach;
    }

    // ---- connectivity ------------------------------------------------------------------------------------------------

    static void KeepHouseComponents(House[] houses, int[] label, int W, int H, float x0, float z0, float cell, int[] stack)
    {
        int N = W * H; var seen = new bool[N];
        // seed search window per parcel: bounding boxes
        var minX = new int[houses.Length]; var maxX = new int[houses.Length]; var minZ = new int[houses.Length]; var maxZ = new int[houses.Length];
        for (int h = 0; h < houses.Length; h++) { minX[h] = W; minZ[h] = H; maxX[h] = -1; maxZ[h] = -1; }
        for (int j = 0; j < H; j++) for (int i = 0; i < W; i++)
            {
                int l = label[j * W + i]; if (l < 0) continue;
                if (i < minX[l]) minX[l] = i; if (i > maxX[l]) maxX[l] = i; if (j < minZ[l]) minZ[l] = j; if (j > maxZ[l]) maxZ[l] = j;
            }
        for (int h = 0; h < houses.Length; h++)
        {
            if (maxX[h] < 0) continue;
            var hs = houses[h];
            float cx = (hs.footprint.a.x + hs.footprint.b.x + hs.footprint.c.x + hs.footprint.d.x) * .25f, cz = (hs.footprint.a.z + hs.footprint.b.z + hs.footprint.c.z + hs.footprint.d.z) * .25f;
            int sx = Mathf.FloorToInt((cx - x0) / cell), sz = Mathf.FloorToInt((cz - z0) / cell), seed = -1;
            if (sx >= 0 && sz >= 0 && sx < W && sz < H && label[sz * W + sx] == h) seed = sz * W + sx;
            else
            {
                // nearest own cell to the building centre (ties: scan order)
                long bestD = long.MaxValue;
                for (int j = minZ[h]; j <= maxZ[h]; j++) for (int i = minX[h]; i <= maxX[h]; i++)
                    {
                        if (label[j * W + i] != h) continue;
                        long d = (long)(i - sx) * (i - sx) + (long)(j - sz) * (j - sz);
                        if (d < bestD) { bestD = d; seed = j * W + i; }
                    }
            }
            if (seed < 0) continue;
            int sp = 0; stack[sp++] = seed; seen[seed] = true;
            while (sp > 0)
            {
                int c = stack[--sp], x = c % W, z = c / W;
                if (x > 0 && !seen[c - 1] && label[c - 1] == h) { seen[c - 1] = true; stack[sp++] = c - 1; }
                if (x < W - 1 && !seen[c + 1] && label[c + 1] == h) { seen[c + 1] = true; stack[sp++] = c + 1; }
                if (z > 0 && !seen[c - W] && label[c - W] == h) { seen[c - W] = true; stack[sp++] = c - W; }
                if (z < H - 1 && !seen[c + W] && label[c + W] == h) { seen[c + W] = true; stack[sp++] = c + W; }
            }
        }
        for (int i = 0; i < N; i++) if (label[i] >= 0 && !seen[i]) label[i] = Common;
    }

    // ---- nearest-point field ---------------------------------------------------------------------------------------

    static RoadField BuildField(RoadGeo r, float reach, int W, int H, float x0, float z0, float cell)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < r.x.Length; i++) { minX = Math.Min(minX, r.x[i]); maxX = Math.Max(maxX, r.x[i]); minZ = Math.Min(minZ, r.z[i]); maxZ = Math.Max(maxZ, r.z[i]); }
        int fi0 = Mathf.Max(0, Mathf.FloorToInt((float)((minX - reach - x0) / cell))), fi1 = Mathf.Min(W - 1, Mathf.FloorToInt((float)((maxX + reach - x0) / cell)));
        int fj0 = Mathf.Max(0, Mathf.FloorToInt((float)((minZ - reach - z0) / cell))), fj1 = Mathf.Min(H - 1, Mathf.FloorToInt((float)((maxZ + reach - z0) / cell)));
        if (fi1 < fi0 || fj1 < fj0) return null;
        int fw = fi1 - fi0 + 1, fh = fj1 - fj0 + 1;
        var fld = new RoadField { road = r, i0 = fi0, j0 = fj0, i1 = fi1, j1 = fj1, fw = fw, reach2 = reach * reach, d2 = new float[fw * fh], s = new float[fw * fh], side = new sbyte[fw * fh] };
        var fd2 = fld.d2; var fs = fld.s; var fside = fld.side;
        for (int i = 0; i < fd2.Length; i++) fd2[i] = fld.reach2;
        int nseg = r.Segments;
        for (int s = 0; s < nseg; s++)
        {
            double ax = r.x[s], az = r.z[s], bx = r.x[s + 1], bz = r.z[s + 1], dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz, sl = r.cum[s + 1] - r.cum[s];
            int i0 = Mathf.Max(fi0, Mathf.FloorToInt((float)((Math.Min(ax, bx) - reach - x0) / cell))), i1 = Mathf.Min(fi1, Mathf.FloorToInt((float)((Math.Max(ax, bx) + reach - x0) / cell)));
            int j0 = Mathf.Max(fj0, Mathf.FloorToInt((float)((Math.Min(az, bz) - reach - z0) / cell))), j1 = Mathf.Min(fj1, Mathf.FloorToInt((float)((Math.Max(az, bz) + reach - z0) / cell)));
            bool first = !r.closed && s == 0, last = !r.closed && s == nseg - 1;
            for (int j = j0; j <= j1; j++)
            {
                double pz = z0 + (j + .5) * cell; int row = (j - fj0) * fw - fi0;
                for (int i = i0; i <= i1; i++)
                {
                    double px = x0 + (i + .5) * cell;
                    double t = ((px - ax) * dx + (pz - az) * dz) / l2, tc = t < 0 ? 0 : (t > 1 ? 1 : t);
                    double ex = px - (ax + dx * tc), ez = pz - (az + dz * tc);
                    float d2 = (float)(ex * ex + ez * ez);
                    int f = row + i;
                    if (d2 >= fd2[f]) continue;
                    fd2[f] = d2; fs[f] = (float)(r.cum[s] + sl * tc);
                    fside[f] = (sbyte)((first && t < -1e-6) || (last && t > 1 + 1e-6) ? 0 : ((px - ax) * dz - (pz - az) * dx > 0 ? 1 : -1));
                }
            }
        }
        return fld;
    }

    /// Which house of the chain owns the point (px, pz) whose unwrapped station is u: by station first, then corrected
    /// by the straight divider lines on either side.
    static int HouseAt(Chain c, double u, float px, float pz)
    {
        int n = c.houses.Length, k = 0;
        if (n < 2) return 0;
        double L = c.road.length;
        for (int m = 1; m < n; m++) if (u >= c.divS[m]) k = m; else break;
        int lower = k > 0 ? k : (c.full ? 0 : -1);           // divider on the low-station side of house k
        int upper = k < n - 1 ? k + 1 : (c.full ? 0 : -1);   // divider on the high-station side
        double sLower = lower < 0 ? 0 : c.divS[lower], sUpper = upper < 0 ? 0 : (upper == 0 ? c.divS[0] + L : c.divS[upper]);
        if (lower >= 0 && u - sLower < 16.0 && (px - c.divQx[lower]) * c.divTx[lower] + (pz - c.divQz[lower]) * c.divTz[lower] < 0) k = k > 0 ? k - 1 : n - 1;
        else if (upper >= 0 && sUpper - u < 16.0 && (px - c.divQx[upper]) * c.divTx[upper] + (pz - c.divQz[upper]) * c.divTz[upper] >= 0) k = k < n - 1 ? k + 1 : 0;
        return k;
    }

    // ---- thin strips ------------------------------------------------------------------------------------------------

    /// Thin leftover land is absorbed along the chain's own rays so the cuts stay straight:
    ///  - behind the rear cap: for every metre of station, if the land beyond the max depth runs into something
    ///    (another parcel, a road reserve, water, reserved land) within thinStrip, the parcel runs up to it;
    ///  - past each row end: the same along the frontage, per metre of depth.
    static void AbsorbBehind(Chain c, int chainIndex, int[] chainOf, RoadField fld, int[] label, int[] labelR, Settings st, int W, int H, float x0, float z0, float cell)
    {
        float thin = st.thinStrip; if (thin <= 0f) return;
        var r = c.road; double L = r.length; float baseOff = r.hw + st.sidewalk, maxd = c.maxDepth;
        int nd = Math.Max(1, (int)Math.Ceiling(maxd));
        const float NONE = float.MaxValue;
        var endLo = new float[nd]; var endHi = new float[nd];
        for (int i = 0; i < nd; i++) { endLo[i] = NONE; endHi[i] = NONE; }
        // Rear: rays along the (chord-smoothed) frontage normal, dense enough to cover every cell at full depth.
        // A ray is absorbed when it runs into something within thinStrip. Short runs of rays (under jog metres of
        // frontage) that just miss, wedged between absorbed rays and/or a lot line, follow suit when they hit within
        // twice that, so the rear line does not jog for a few metres.
        {
            double ds = 0.12, o0 = -1.5, stepO = cell * 0.5, jog = 6.0; int maxSteps = (int)Math.Ceiling((2 * thin - o0) / stepO) + 1;
            int nr = (int)Math.Floor((c.hi - c.lo) / ds) + 1;
            var dist = new float[nr]; var owner = new int[nr]; var take = new bool[nr]; var rx = new double[nr]; var rz = new double[nr];
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    for (int i = 0; i < nr; i++) take[i] = dist[i] < thin;
                    int i0 = 0;
                    while (i0 < nr)
                    {
                        int i1 = i0; while (i1 + 1 < nr && owner[i1 + 1] == owner[i0] && take[i1 + 1] == take[i0]) i1++;
                        if (!take[i0] && owner[i0] >= 0 && Math.Sqrt((rx[i1] - rx[i0]) * (rx[i1] - rx[i0]) + (rz[i1] - rz[i0]) * (rz[i1] - rz[i0])) < jog)
                        {
                            bool all = true; for (int i = i0; i <= i1 && all; i++) all = dist[i] < 2 * thin;
                            int pa = i0 > 0 ? i0 - 1 : (c.full ? nr - 1 : -1), pb = i1 < nr - 1 ? i1 + 1 : (c.full ? 0 : -1);
                            bool ta = pa >= 0 && owner[pa] == owner[i0] && take[pa], tb = pb >= 0 && owner[pb] == owner[i0] && take[pb];
                            bool ea = pa < 0 || owner[pa] != owner[i0], eb = pb < 0 || owner[pb] != owner[i0];
                            if (all && (ta || tb) && (ta || ea) && (tb || eb)) for (int i = i0; i <= i1; i++) take[i] = true;
                        }
                        i0 = i1 + 1;
                    }
                }
                var hit = new int[maxSteps];
                for (int ri = 0; ri < nr; ri++)
                {
                    if (pass == 1 && !take[ri]) continue;
                    double sv = c.lo + ri * ds;
                    double px, pz, tx, tz; r.PointAt(sv, out px, out pz); r.TangentAt(sv, st.dividerChord, out tx, out tz);
                    double nx = tz * c.side, nz = -tx * c.side; int n = 0; bool stop = false; int own = -1; float at = NONE;
                    for (int k = 0; k < maxSteps; k++)
                    {
                        double o = baseOff + maxd + o0 + k * stepO;
                        int ix = (int)Math.Floor((px + nx * o - x0) / cell), iz = (int)Math.Floor((pz + nz * o - z0) / cell);
                        if (ix < 0 || iz < 0 || ix >= W || iz >= H) { n = 0; break; }
                        int cidx = iz * W + ix, l = label[cidx];
                        if (l == Common) { if (own < 0) { n = 0; break; } if (n == 0 || hit[n - 1] != cidx) hit[n++] = cidx; continue; }
                        if (n == 0 && l >= 0 && chainOf[l] == chainIndex) { own = l; continue; }
                        stop = true; at = (float)(o0 + k * stepO); break;
                    }
                    if (pass == 0) { rx[ri] = px + nx * (baseOff + maxd); rz[ri] = pz + nz * (baseOff + maxd); owner[ri] = own; dist[ri] = stop && n > 0 ? at : NONE; continue; }
                    if (!stop || n == 0) continue;
                    for (int k = 0; k < n; k++) { int lr = labelR[hit[k]]; if (lr >= 0 && chainOf[lr] == chainIndex) label[hit[k]] = lr; }
                }
            }
        }
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                float[] t;
                t = (float[])endLo.Clone(); for (int i = 0; i < nd; i++) endLo[i] = Mathf.Min(t[i], Mathf.Min(t[Math.Max(0, i - 1)], t[Math.Min(nd - 1, i + 1)]));
                t = (float[])endHi.Clone(); for (int i = 0; i < nd; i++) endHi[i] = Mathf.Min(t[i], Mathf.Min(t[Math.Max(0, i - 1)], t[Math.Min(nd - 1, i + 1)]));
            }
            for (int j = fld.j0; j <= fld.j1; j++)
            {
                int row = (j - fld.j0) * fld.fw - fld.i0;
                for (int i = fld.i0; i <= fld.i1; i++)
                {
                    int f = row + i;
                    if (fld.side[f] != c.side || fld.d2[f] >= fld.reach2) continue;
                    float depth = Mathf.Sqrt(fld.d2[f]) - baseOff;
                    if (depth <= 0f || depth >= maxd) continue;
                    double u = fld.s[f];
                    if (c.full) u = c.lo + Mod(u - c.lo, L);
                    else if (r.closed) { double b0 = c.lo - thin; u = b0 + Mod(u - b0, L); }
                    int cidx = j * W + i, l = label[cidx];
                    bool inExtent = c.full || (u >= c.lo && u <= c.hi);
                    if (inExtent) continue;
                                        {
                        bool low = u < c.lo; float beyond = (float)(low ? c.lo - u : u - c.hi);
                        if (beyond >= thin) continue;
                        var arr = low ? endLo : endHi; int d = Math.Min(nd - 1, (int)depth);
                        if (pass == 0) { if (l != Common && beyond < arr[d]) arr[d] = beyond; }
                        else if (l == Common && labelR[cidx] >= 0 && chainOf[labelR[cidx]] == chainIndex && arr[d] != NONE && beyond < arr[d]) label[cidx] = labelR[cidx];
                    }
                }
            }
        }
    }

    /// Chamfer (1, sqrt 2) distance in cells from every cell to the nearest `seed` cell.
    static float[] Chamfer(bool[] seed, int W, int H)
    {
        int N = W * H; var d = new float[N]; const float BIG = 1e9f, D = 1.41421356f;
        for (int i = 0; i < N; i++) d[i] = seed[i] ? 0f : BIG;
        for (int j = 0; j < H; j++) for (int i = 0; i < W; i++)
            {
                int c = j * W + i; float v = d[c]; if (v == 0f) continue;
                if (i > 0 && d[c - 1] + 1f < v) v = d[c - 1] + 1f;
                if (j > 0)
                {
                    if (d[c - W] + 1f < v) v = d[c - W] + 1f;
                    if (i > 0 && d[c - W - 1] + D < v) v = d[c - W - 1] + D;
                    if (i < W - 1 && d[c - W + 1] + D < v) v = d[c - W + 1] + D;
                }
                d[c] = v;
            }
        for (int j = H - 1; j >= 0; j--) for (int i = W - 1; i >= 0; i--)
            {
                int c = j * W + i; float v = d[c]; if (v == 0f) continue;
                if (i < W - 1 && d[c + 1] + 1f < v) v = d[c + 1] + 1f;
                if (j < H - 1)
                {
                    if (d[c + W] + 1f < v) v = d[c + W] + 1f;
                    if (i < W - 1 && d[c + W + 1] + D < v) v = d[c + W + 1] + D;
                    if (i > 0 && d[c + W - 1] + D < v) v = d[c + W - 1] + D;
                }
                d[c] = v;
            }
        return d;
    }

    /// Small-scale closing of the parcel fabric: bits of common land that a disc of notchWidth cannot enter (teeth
    /// where two absorbed strips end at different rays, slots between two rear lines) join the parcel they touch most.
    static void FillNotches(int[] label, int W, int H, float cell, Settings st, int[] stack)
    {
        if (st.notchWidth <= cell) return;
        int N = W * H; float r = st.notchWidth * .5f / cell;
        var flag = new bool[N];
        for (int i = 0; i < N; i++) flag[i] = label[i] != Common;
        var d = Chamfer(flag, W, H);
        for (int i = 0; i < N; i++) flag[i] = d[i] > r;
        d = Chamfer(flag, W, H);
        var thin = new byte[N]; float open = r * 1.06f + 0.75f;
        for (int i = 0; i < N; i++) thin[i] = (byte)(label[i] == Common && d[i] > open ? 1 : 0);
        d = null; flag = null;
        var keys = new List<int>(); var vals = new List<int>();
        for (int i = 0; i < N; i++)
        {
            if (thin[i] != 1) continue;
            int sp = 0, head = 0; stack[sp++] = i; thin[i] = 2; keys.Clear(); vals.Clear();
            while (head < sp)
            {
                int c = stack[head++], x = c % W, z = c / W;
                for (int k = 0; k < 4; k++)
                {
                    int o = k == 0 ? (x > 0 ? c - 1 : -1) : k == 1 ? (x < W - 1 ? c + 1 : -1) : k == 2 ? (z > 0 ? c - W : -1) : (z < H - 1 ? c + W : -1);
                    if (o < 0) continue;
                    if (thin[o] == 1) { thin[o] = 2; stack[sp++] = o; }
                    else if (label[o] >= 0 && thin[o] == 0) { int at = keys.IndexOf(label[o]); if (at < 0) { keys.Add(label[o]); vals.Add(1); } else vals[at]++; }
                }
            }
            if (keys.Count == 0 || sp * cell * cell < st.notchMinArea) continue;
            int pick = -1, most = 0;
            for (int k = 0; k < keys.Count; k++) if (vals[k] > most || (vals[k] == most && keys[k] < pick)) { most = vals[k]; pick = keys[k]; }
            for (int k = 0; k < sp; k++) label[stack[k]] = pick;
        }
    }

    /// Common pockets below minCommon are dissolved: into the parcel they share the longest boundary with, or, when
    /// they touch no parcel, into the surrounding road reserve / water / reserved land.
    static void MergeTinyCommons(int[] label, short[] nearRoad, SegIndex roadIndex, int W, int H, float x0, float z0, float cell, Settings st, int[] stack)
    {
        if (st.minCommon <= 0f) return;
        int N = W * H; var seen = new bool[N]; int maxCells = Mathf.CeilToInt(st.minCommon / (cell * cell));
        var keys = new List<int>(); var vals = new List<int>();
        for (int i = 0; i < N; i++)
        {
            if (label[i] != Common || seen[i]) continue;
            int sp = 0, head = 0; stack[sp++] = i; seen[i] = true; keys.Clear(); vals.Clear();
            while (head < sp)
            {
                int c = stack[head++], x = c % W, z = c / W;
                for (int k = 0; k < 4; k++)
                {
                    int o = k == 0 ? (x > 0 ? c - 1 : -1) : k == 1 ? (x < W - 1 ? c + 1 : -1) : k == 2 ? (z > 0 ? c - W : -1) : (z < H - 1 ? c + W : -1);
                    if (o < 0) continue;
                    int l = label[o];
                    if (l == Common) { if (!seen[o]) { seen[o] = true; stack[sp++] = o; } }
                    else { int at = keys.IndexOf(l); if (at < 0) { keys.Add(l); vals.Add(1); } else vals[at]++; }
                }
            }
            if (sp >= maxCells || keys.Count == 0) continue;
            int pick = int.MinValue, most = 0; bool parcel = false;
            for (int k = 0; k < keys.Count; k++)
            {
                bool isParcel = keys[k] >= 0;
                if (parcel && !isParcel) continue;
                if ((isParcel && !parcel) || vals[k] > most || (vals[k] == most && keys[k] < pick)) { most = vals[k]; pick = keys[k]; parcel = isParcel; }
            }
            for (int k = 0; k < sp; k++) { label[stack[k]] = pick; if (pick == RoadReserve) SetNearRoad(stack[k], nearRoad, roadIndex, W, x0, z0, cell); }
        }
    }

    static void SetNearRoad(int c, short[] nearRoad, SegIndex roadIndex, int W, float x0, float z0, float cell)
    {
        int seg; if (roadIndex == null) return;
        roadIndex.Query(x0 + (c % W + .5f) * cell, z0 + (c / W + .5f) * cell, out seg);
        if (seg >= 0) nearRoad[c] = (short)roadIndex.owner[seg];
    }

    /// Chamfer (1, sqrt 2) distance in cells, propagated only between cells of the same label. Seeds are d == 0... the
    /// caller pre-fills `d` (seed value or BIG) and the passes relax it in place.
    static void ChamferSameLabel(float[] d, int[] label, int W, int H, float D)
    {
        for (int j = 0; j < H; j++) for (int i = 0; i < W; i++)
            {
                int c = j * W + i, l = label[c]; if (l < 0) continue; float v = d[c];
                if (i > 0 && label[c - 1] == l && d[c - 1] + 1f < v) v = d[c - 1] + 1f;
                if (j > 0)
                {
                    if (label[c - W] == l && d[c - W] + 1f < v) v = d[c - W] + 1f;
                    if (i > 0 && label[c - W - 1] == l && d[c - W - 1] + D < v) v = d[c - W - 1] + D;
                    if (i < W - 1 && label[c - W + 1] == l && d[c - W + 1] + D < v) v = d[c - W + 1] + D;
                }
                d[c] = v;
            }
        for (int j = H - 1; j >= 0; j--) for (int i = W - 1; i >= 0; i--)
            {
                int c = j * W + i, l = label[c]; if (l < 0) continue; float v = d[c];
                if (i < W - 1 && label[c + 1] == l && d[c + 1] + 1f < v) v = d[c + 1] + 1f;
                if (j < H - 1)
                {
                    if (label[c + W] == l && d[c + W] + 1f < v) v = d[c + W] + 1f;
                    if (i < W - 1 && label[c + W + 1] == l && d[c + W + 1] + D < v) v = d[c + W + 1] + D;
                    if (i > 0 && label[c + W - 1] == l && d[c + W - 1] + D < v) v = d[c + W - 1] + D;
                }
                d[c] = v;
            }
    }

    /// Morphological opening per parcel: tails narrower than spikeWidth are cut off. A trimmed piece that touches a road
    /// reserve becomes verge (RoadReserve), anything else becomes Common. Forced cells are never trimmed.
    static void TrimSpikes(int[] label, bool[] forced, short[] nearRoad, SegIndex roadIndex, int W, int H, float x0, float z0, float cell, Settings st, int[] stack, int parcelCount)
    {
        if (st.spikeWidth <= cell || parcelCount == 0) return;
        int N = W * H; float r = st.spikeWidth * .5f / cell; const float BIG = 1e9f;
        var d = new float[N];
        for (int c = 0; c < N; c++)
        {
            int l = label[c]; if (l < 0) { d[c] = 0; continue; }
            int x = c % W, z = c / W;
            bool edge = x == 0 || z == 0 || x == W - 1 || z == H - 1 || label[c - 1] != l || label[c + 1] != l || label[c - W] != l || label[c + W] != l;
            d[c] = edge ? 0.5f : BIG;
        }
        ChamferSameLabel(d, label, W, H, 1f);     // square element: tails are cut square, right-angled corners survive
        var hasCore = new bool[parcelCount];
        for (int c = 0; c < N; c++) { int l = label[c]; if (l < 0) continue; if (d[c] > r) { d[c] = 0f; hasCore[l] = true; } else d[c] = BIG; }
        ChamferSameLabel(d, label, W, H, 1f);
        float open = r + 0.5f;
        var cut = new byte[N];     // 1 = to trim, 2 = visited
        for (int c = 0; c < N; c++) { int l = label[c]; cut[c] = (byte)(l >= 0 && hasCore[l] && d[c] > open && !forced[c] ? 1 : 0); }
        d = null;
        for (int i = 0; i < N; i++)
        {
            if (cut[i] != 1) continue;
            int l = label[i], sp = 0, head = 0, front = 0; bool road = false; stack[sp++] = i; cut[i] = 2;
            while (head < sp)
            {
                int c = stack[head++], x = c % W, z = c / W;
                for (int k = 0; k < 4; k++)
                {
                    int o = k == 0 ? (x > 0 ? c - 1 : -1) : k == 1 ? (x < W - 1 ? c + 1 : -1) : k == 2 ? (z > 0 ? c - W : -1) : (z < H - 1 ? c + W : -1);
                    if (o < 0) continue;
                    if (label[o] == l && cut[o] != 0) { if (cut[o] == 1) { cut[o] = 2; stack[sp++] = o; } }
                    else if (label[o] == l) front++;
                    else if (label[o] == RoadReserve) road = true;
                }
            }
            // members are stack[0..sp); `front` counts their contacts with the kept body
            float area = sp * cell * cell;
            if (area < st.spikeMinArea || (front > 0 && area / (front * cell) < 1.5f)) continue;
            for (int k = 0; k < sp; k++)
            {
                int c = stack[k]; cut[c] = 0;
                if (road) { label[c] = RoadReserve; SetNearRoad(c, nearRoad, roadIndex, W, x0, z0, cell); } else label[c] = Common;
            }
        }
    }

    // =====================================================================================================================
    // Vectorisation (planar graph on the vertex grid)
    // =====================================================================================================================

    sealed class Run { public int la, lb; public List<int> verts = new List<int>(); public bool loop; public Vector3[] points; public int edge = -1; }

    static void Vectorise(Result res, RoadGeo[] roads, short[] nearRoad, int[] firstCell, List<int> commonSeed)
    {
        int W = res.gridWidth, H = res.gridHeight, N = W * H; float cell = res.cell, x0 = res.originX, z0 = res.originZ;
        var label = res.label; var st = res.input.settings;
        // edge label: parcels and Common keep their value; road reserve is split per road
        var el = new int[N];
        for (int i = 0; i < N; i++) el[i] = label[i] == RoadReserve ? -100 - Math.Max(0, (int)nearRoad[i]) : label[i];
        int VW = W + 1;
        var hRun = new int[(H + 1) * W]; var vRun = new int[H * VW];
        // -2 = not a boundary, -1 = boundary not yet in a run
        for (int z = 0; z <= H; z++) for (int x = 0; x < W; x++)
            {
                int la = z > 0 ? el[(z - 1) * W + x] : Outside, lb = z < H ? el[z * W + x] : Outside;
                hRun[z * W + x] = la != lb && (la >= Common || lb >= Common) ? -1 : -2;
            }
        for (int z = 0; z < H; z++) for (int x = 0; x <= W; x++)
            {
                int la = x > 0 ? el[z * W + x - 1] : Outside, lb = x < W ? el[z * W + x] : Outside;
                vRun[z * VW + x] = la != lb && (la >= Common || lb >= Common) ? -1 : -2;
            }
        Func<int, int, int, long> keyOf = (x, z, dir) =>
        {
            // key of the boundary edge leaving vertex (x,z) in direction dir (0 E, 1 N, 2 W, 3 S); 0 when none
            int la, lb;
            switch (dir)
            {
                case 0: if (x >= W || hRun[z * W + x] == -2) return 0; la = z > 0 ? el[(z - 1) * W + x] : Outside; lb = z < H ? el[z * W + x] : Outside; break;
                case 2: if (x <= 0 || hRun[z * W + x - 1] == -2) return 0; la = z > 0 ? el[(z - 1) * W + x - 1] : Outside; lb = z < H ? el[z * W + x - 1] : Outside; break;
                case 1: if (z >= H || vRun[z * VW + x] == -2) return 0; la = x > 0 ? el[z * W + x - 1] : Outside; lb = x < W ? el[z * W + x] : Outside; break;
                default: if (z <= 0 || vRun[(z - 1) * VW + x] == -2) return 0; la = x > 0 ? el[(z - 1) * W + x - 1] : Outside; lb = x < W ? el[(z - 1) * W + x] : Outside; break;
            }
            int lo = Math.Min(la, lb), hi = Math.Max(la, lb);
            return ((long)(lo + 100000) << 24) | (long)(uint)(hi + 100000);
        };
        Func<int, int, bool> isJunction = (x, z) =>
        {
            int n = 0; long k0 = 0; bool same = true;
            for (int d = 0; d < 4; d++) { long k = keyOf(x, z, d); if (k == 0) continue; if (n == 0) k0 = k; else if (k != k0) same = false; n++; }
            return n != 2 || !same;
        };
        var runs = new List<Run>();
        int[] ddx = { 1, 0, -1, 0 }, ddz = { 0, 1, 0, -1 };
        Func<int, int, int, int> slotOf = (x, z, dir) => dir == 0 ? z * W + x : dir == 2 ? z * W + x - 1 : dir == 1 ? z * VW + x : (z - 1) * VW + x;
        Action<int, int, int> trace = (sx, sz, sdir) =>
        {
            int x = sx, z = sz, dir = sdir; long key = keyOf(x, z, dir);
            var run = new Run(); int id = runs.Count; runs.Add(run);
            long lo = (key >> 24) - 100000, hi = (key & 0xFFFFFF) - 100000; run.la = (int)lo; run.lb = (int)hi;
            run.verts.Add(z * VW + x);
            while (true)
            {
                int slot = slotOf(x, z, dir);
                if ((dir & 1) == 0) hRun[slot] = id; else vRun[slot] = id;
                x += ddx[dir]; z += ddz[dir]; run.verts.Add(z * VW + x);
                if (x == sx && z == sz) { run.loop = !isJunction(x, z); break; }
                if (isJunction(x, z)) break;
                int nd = -1;
                for (int d = 0; d < 4; d++) { if (d == ((dir + 2) & 3)) continue; if (keyOf(x, z, d) != 0) { nd = d; break; } }
                if (nd < 0) break;
                dir = nd;
            }
        };
        // runs between junctions, in vertex scan order
        for (int z = 0; z <= H; z++) for (int x = 0; x <= W; x++)
            {
                bool any = false;
                for (int d = 0; d < 4 && !any; d++) any = keyOf(x, z, d) != 0;
                if (!any || !isJunction(x, z)) continue;
                for (int d = 0; d < 4; d++)
                {
                    if (keyOf(x, z, d) == 0) continue;
                    int slot = slotOf(x, z, d);
                    if (((d & 1) == 0 ? hRun[slot] : vRun[slot]) != -1) continue;
                    trace(x, z, d);
                }
            }
        // junction-free rings
        for (int z = 0; z <= H; z++) for (int x = 0; x < W; x++) if (hRun[z * W + x] == -1) trace(x, z, 0);
        for (int z = 0; z < H; z++) for (int x = 0; x <= W; x++) if (vRun[z * VW + x] == -1) trace(x, z, 1);

        // simplify every run ONCE
        float tol = Mathf.Max(0f, st.simplify) / cell;
        foreach (var run in runs)
        {
            var keep = SimplifyRun(run.verts, VW, tol, run.loop || run.verts[0] == run.verts[run.verts.Count - 1]);
            run.points = new Vector3[keep.Count];
            for (int i = 0; i < keep.Count; i++) { int v = run.verts[keep[i]]; run.points[i] = new Vector3(x0 + (v % VW) * cell, 0f, z0 + (v / VW) * cell); }
        }

        // edges
        var parcels = res.parcels; var edges = new List<Edge>();
        var neigh = new List<int>[parcels.Length];
        var comParcels = new List<int>[res.commons.Length]; var comRoads = new List<string>[res.commons.Length];
        foreach (var run in runs)
        {
            int a = run.la, b = run.lb;      // a <= b
            int v0 = run.verts[0], v1 = run.verts[1];
            if (b == Common || a == Common)
            {
                // which common region: look at the cells either side of the first grid edge
                int cid = CommonBeside(res, v0, v1, VW);
                int other = a == Common ? b : a;
                if (cid >= 0)
                {
                    if (other >= 0) { if (comParcels[cid] == null) comParcels[cid] = new List<int>(); if (!comParcels[cid].Contains(other)) comParcels[cid].Add(other); }
                    else if (other <= -100) { string rid = roads[-100 - other].road.id; if (comRoads[cid] == null) comRoads[cid] = new List<string>(); if (!comRoads[cid].Contains(rid)) comRoads[cid].Add(rid); }
                }
            }
            if (b < 0) continue;             // no parcel on either side
            var e = new Edge { points = run.points, closed = run.loop };
            float len = 0; for (int i = 1; i < run.points.Length; i++) len += Vector3.Distance(run.points[i - 1], run.points[i]);
            e.length = len;
            if (a >= 0)
            {
                e.a = a; e.b = b; e.kind = EdgeKind.ParcelParcel;
                string ca = parcels[a].chain, cb = parcels[b].chain;
                e.ownerChain = string.CompareOrdinal(ca, cb) <= 0 ? ca : cb;
                if (neigh[a] == null) neigh[a] = new List<int>(); if (!neigh[a].Contains(b)) neigh[a].Add(b);
                if (neigh[b] == null) neigh[b] = new List<int>(); if (!neigh[b].Contains(a)) neigh[b].Add(a);
            }
            else
            {
                e.a = b; e.ownerChain = parcels[b].chain;
                if (a <= -100)
                {
                    e.b = RoadReserve; e.kind = EdgeKind.ParcelRoad; e.roadId = roads[-100 - a].road.id;
                    e.frontage = e.roadId == parcels[b].house.roadId;
                }
                else if (a == Water) { e.b = Water; e.kind = EdgeKind.ParcelWater; }
                else if (a == Reserved) { e.b = Reserved; e.kind = EdgeKind.ParcelReserved; }
                else if (a == Outside) { e.b = Outside; e.kind = EdgeKind.ParcelCommon; }
                else { e.b = Common; e.kind = EdgeKind.ParcelCommon; e.common = CommonBeside(res, v0, v1, VW); }
            }
            run.edge = edges.Count; edges.Add(e);
        }
        res.edges = edges.ToArray();
        for (int p = 0; p < parcels.Length; p++) { if (neigh[p] != null) { neigh[p].Sort(); parcels[p].neighbours = neigh[p].ToArray(); } }
        for (int c = 0; c < res.commons.Length; c++)
        {
            if (comParcels[c] != null) comParcels[c].Sort(); if (comRoads[c] != null) comRoads[c].Sort(string.CompareOrdinal);
            res.commons[c].adjacentParcels = comParcels[c] != null ? comParcels[c].ToArray() : new int[0];
            res.commons[c].adjacentRoads = comRoads[c] != null ? comRoads[c].ToArray() : new string[0];
        }

        // outlines from the shared runs
        for (int p = 0; p < parcels.Length; p++)
            if (firstCell[p] >= 0) { int want = p; parcels[p].outline = TraceOutline(firstCell[p], W, H, VW, x0, z0, cell, hRun, vRun, runs, (c) => label[c] == want); }
        var cg = res.commonGrid;
        for (int c = 0; c < res.commons.Length; c++)
        { int want = c; res.commons[c].outline = TraceOutline(commonSeed[c], W, H, VW, x0, z0, cell, hRun, vRun, runs, (i) => cg[i] == want); }
    }

    static int CommonBeside(Result res, int v0, int v1, int VW)
    {
        int W = res.gridWidth, H = res.gridHeight;
        int x = Math.Min(v0 % VW, v1 % VW), z = Math.Min(v0 / VW, v1 / VW);
        bool horizontal = v0 / VW == v1 / VW;
        // horizontal edge at vertex row z between cells (x,z-1) and (x,z); vertical between (x-1,z) and (x,z)
        int c1 = horizontal ? (z > 0 ? (z - 1) * W + x : -1) : (x > 0 ? z * W + x - 1 : -1);
        int c2 = horizontal ? (z < H ? z * W + x : -1) : (x < W ? z * W + x : -1);
        if (c1 >= 0 && res.commonGrid[c1] >= 0) return res.commonGrid[c1];
        if (c2 >= 0 && res.commonGrid[c2] >= 0) return res.commonGrid[c2];
        return -1;
    }

    /// Douglas-Peucker on grid vertices; returns kept indices (always first and last). Closed rings are split at the
    /// vertex farthest from the start so they never collapse.
    static List<int> SimplifyRun(List<int> verts, int VW, float tol, bool ring)
    {
        int n = verts.Count; var keep = new bool[n]; keep[0] = true; keep[n - 1] = true;
        var st = new Stack<int>();
        if (ring && n > 3)
        {
            int far = 0; long best = -1; int ax = verts[0] % VW, az = verts[0] / VW;
            for (int i = 1; i < n - 1; i++) { long dx = verts[i] % VW - ax, dz = verts[i] / VW - az, d = dx * dx + dz * dz; if (d > best) { best = d; far = i; } }
            keep[far] = true; st.Push(0); st.Push(far); st.Push(far); st.Push(n - 1);
        }
        else { st.Push(0); st.Push(n - 1); }
        while (st.Count > 0)
        {
            int b = st.Pop(), a = st.Pop(); if (b - a < 2) continue;
            float ax = verts[a] % VW, az = verts[a] / VW, bx = verts[b] % VW, bz = verts[b] / VW, dx = bx - ax, dz = bz - az, l = Mathf.Sqrt(dx * dx + dz * dz);
            float worst = -1f; int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                float px = verts[i] % VW - ax, pz = verts[i] / VW - az;
                float d = l < 1e-6f ? Mathf.Sqrt(px * px + pz * pz) : Mathf.Abs(px * dz - pz * dx) / l;
                if (d > worst) { worst = d; at = i; }
            }
            if (worst > tol) { keep[at] = true; st.Push(a); st.Push(at); st.Push(at); st.Push(b); }
        }
        var list = new List<int>(); for (int i = 0; i < n; i++) if (keep[i]) list.Add(i);
        if (ring && list.Count < 4) { list.Clear(); for (int i = 0; i < n; i++) list.Add(i); }   // a ring keeps at least a triangle
        return list;
    }

    /// Walks the outer boundary of a 4-connected cell set (interior on the left, counter-clockwise) and stitches the
    /// simplified runs it passes over, so an outline shares its vertices exactly with the edges.
    static Vector3[] TraceOutline(int startCell, int W, int H, int VW, float x0, float z0, float cell, int[] hRun, int[] vRun, List<Run> runs, Func<int, bool> inside)
    {
        int sx = startCell % W, sz = startCell / W;
        Func<int, int, bool> inC = (x, z) => x >= 0 && z >= 0 && x < W && z < H && inside(z * W + x);
        var stepRun = new List<int>(); var stepFrom = new List<int>(); var stepTo = new List<int>();
        int vx = sx, vz = sz, dir = 0; int guard = 0, limit = 4 * (W + 1) * (H + 1);
        while (guard++ < limit)
        {
            int slot = dir == 0 ? vz * W + vx : dir == 2 ? vz * W + vx - 1 : dir == 1 ? vz * VW + vx : (vz - 1) * VW + vx;
            int rid = (dir & 1) == 0 ? hRun[slot] : vRun[slot];
            int from = vz * VW + vx;
            vx += dir == 0 ? 1 : dir == 2 ? -1 : 0; vz += dir == 1 ? 1 : dir == 3 ? -1 : 0;
            stepRun.Add(rid); stepFrom.Add(from); stepTo.Add(vz * VW + vx);
            bool l, r;
            switch (dir)
            {
                case 0: l = inC(vx, vz); r = inC(vx, vz - 1); break;
                case 1: l = inC(vx - 1, vz); r = inC(vx, vz); break;
                case 2: l = inC(vx - 1, vz - 1); r = inC(vx - 1, vz); break;
                default: l = inC(vx, vz - 1); r = inC(vx - 1, vz - 1); break;
            }
            if (!l) dir = (dir + 1) & 3; else if (r) dir = (dir + 3) & 3;
            if (vx == sx && vz == sz && dir == 0) break;
        }
        int n = stepRun.Count; if (n == 0) return new Vector3[0];
        int start = -1;
        for (int i = 0; i < n; i++) if (stepRun[i] != stepRun[(i + n - 1) % n]) { start = i; break; }
        var pts = new List<Vector3>();
        if (start < 0)
        {
            // one ring run all the way round
            int rid = stepRun[0]; if (rid < 0) return new Vector3[0];
            var rp = runs[rid].points; for (int i = 0; i < rp.Length - 1; i++) pts.Add(rp[i]);
        }
        else
        {
            int i = 0;
            while (i < n)
            {
                int a = (start + i) % n, rid = stepRun[a], cnt = 1;
                while (i + cnt < n && stepRun[(start + i + cnt) % n] == rid) cnt++;
                if (rid >= 0)
                {
                    var run = runs[rid]; var rp = run.points;
                    bool forward = run.verts[0] == stepFrom[a] && run.verts[1] == stepTo[a];
                    if (forward) for (int k = 0; k < rp.Length - 1; k++) pts.Add(rp[k]);
                    else for (int k = rp.Length - 1; k > 0; k--) pts.Add(rp[k]);
                }
                i += cnt;
            }
        }
        // drop exact duplicates and make sure the ring is counter-clockwise
        var o = new List<Vector3>();
        for (int i = 0; i < pts.Count; i++) if (o.Count == 0 || (pts[i] - o[o.Count - 1]).sqrMagnitude > 1e-8f) o.Add(pts[i]);
        if (o.Count > 1 && (o[0] - o[o.Count - 1]).sqrMagnitude <= 1e-8f) o.RemoveAt(o.Count - 1);
        if (o.Count < 3)
        {
            // a sliver whose runs all simplified to a line: fall back to the raw cell ring
            o.Clear();
            for (int i = 0; i < n; i++) { int v = stepFrom[i]; o.Add(new Vector3(x0 + (v % VW) * cell, 0f, z0 + (v / VW) * cell)); }
        }
        double area2 = 0; for (int i = 0; i < o.Count; i++) { var p = o[i]; var q = o[(i + 1) % o.Count]; area2 += (double)p.x * q.z - (double)q.x * p.z; }
        if (area2 < 0) o.Reverse();
        return o.ToArray();
    }

    // =====================================================================================================================
    // SelfCheck
    // =====================================================================================================================

    /// Returns a list of problems (empty = sound). Builds the plan a second time to prove determinism.
    public static List<string> SelfCheck(Result result) { return SelfCheck(result, true); }

    public static List<string> SelfCheck(Result r, bool rebuild)
    {
        var err = new List<string>();
        if (r == null || r.input == null || r.label == null) { err.Add("no result"); return err; }
        var st = r.input.settings; int W = r.gridWidth, H = r.gridHeight, N = W * H; float cell = r.cell; var label = r.label;
        var houses = r.input.houses ?? new House[0];
        if (r.parcels.Length != houses.Length) err.Add("parcel count " + r.parcels.Length + " != house count " + houses.Length);
        // stop ids unique
        var ids = new List<string>(); foreach (var h in houses) ids.Add(h.stopId ?? ""); ids.Sort(string.CompareOrdinal);
        for (int i = 1; i < ids.Count; i++) if (ids[i] == ids[i - 1]) err.Add("duplicate stopId '" + ids[i] + "'");
        // labels valid, never on masked land
        int onMask = 0, bad = 0; var count = new int[houses.Length];
        for (int i = 0; i < N; i++)
        {
            int l = label[i];
            if (l >= houses.Length || l < Outside) { bad++; continue; }
            if (l >= 0) { count[l]++; if (r.mask[i] != 0) onMask++; }
            else if (r.mask[i] != 0 && l != r.mask[i]) bad++;
        }
        if (bad > 0) err.Add(bad + " cells carry an invalid label");
        if (onMask > 0) err.Add(onMask + " parcel cells lie on road reserve, water or reserved land");
        // 4-connected (disjointness is structural: one label per cell)
        {
            var seen = new bool[N]; var stack = new int[N]; var comps = new int[houses.Length];
            for (int i = 0; i < N; i++)
            {
                int l = label[i]; if (l < 0 || l >= houses.Length || seen[i]) continue;
                comps[l]++; int sp = 0; stack[sp++] = i; seen[i] = true;
                while (sp > 0)
                {
                    int c = stack[--sp], x = c % W, z = c / W;
                    if (x > 0 && !seen[c - 1] && label[c - 1] == l) { seen[c - 1] = true; stack[sp++] = c - 1; }
                    if (x < W - 1 && !seen[c + 1] && label[c + 1] == l) { seen[c + 1] = true; stack[sp++] = c + 1; }
                    if (z > 0 && !seen[c - W] && label[c - W] == l) { seen[c - W] = true; stack[sp++] = c - W; }
                    if (z < H - 1 && !seen[c + W] && label[c + W] == l) { seen[c + W] = true; stack[sp++] = c + W; }
                }
            }
            for (int h = 0; h < houses.Length; h++) if (comps[h] > 1) err.Add(houses[h].stopId + ": parcel is in " + comps[h] + " pieces");
        }
        // every house: exactly one parcel, containing its footprint; keepIn inside; area window
        var frontLen = new float[houses.Length]; var edgeLen = new float[houses.Length];
        for (int h = 0; h < houses.Length && h < r.parcels.Length; h++)
        {
            var hs = houses[h]; var p = r.parcels[h];
            if (p.house != hs || p.index != h) err.Add(hs.stopId + ": parcel/house index mismatch");
            if (r.ParcelOf(hs.stopId) != p) err.Add(hs.stopId + ": ParcelOf does not return its parcel");
            if (count[h] == 0) { err.Add(hs.stopId + ": NO PARCEL (" + Why(r, hs) + ")"); continue; }
            if (p.chain.EndsWith("|x")) err.Add(hs.stopId + ": its road '" + hs.roadId + "' is not in the plan, the parcel is only the forced footprint");
            int inside, outside; string where;
            QuadCoverage(r, ref hs.footprint, h, 0f, false, out inside, out outside, out where);
            if (outside > 0) err.Add(hs.stopId + ": " + (outside * cell * cell).ToString("0.#") + " m2 of the building footprint is outside its parcel (" + where + ")");
            else
            {
                // the margin band only has to be clear of OTHER parcels (it may touch a road reserve)
                QuadCoverage(r, ref hs.footprint, h, st.footprintMargin, true, out inside, out outside, out where);
                if (outside > 0) err.Add(hs.stopId + ": the " + st.footprintMargin.ToString("0.#") + " m band around the building is not wholly inside its parcel (" + where + ")");
            }
            if (hs.keepIn != null)
                for (int q = 0; q < hs.keepIn.Length; q++)
                {
                    QuadCoverage(r, ref hs.keepIn[q], h, 0f, false, out inside, out outside, out where);
                    if (outside > 0) err.Add(hs.stopId + ": " + (outside * cell * cell).ToString("0.#") + " m2 of keepIn '" + hs.keepIn[q].tag + "' is outside its parcel (" + where + ")");
                }
            if (p.area < st.minArea) err.Add(hs.stopId + ": parcel area " + p.area.ToString("0") + " m2 is below " + st.minArea.ToString("0") + " (" + Why(r, hs) + ")");
            if (p.area > st.maxArea) err.Add(hs.stopId + ": parcel area " + p.area.ToString("0") + " m2 is above " + st.maxArea.ToString("0"));
            if (p.outline == null || p.outline.Length < 3) err.Add(hs.stopId + ": parcel has no outline");
            else
            {
                float pa = Mathf.Abs(PolyArea(p.outline));
                if (Mathf.Abs(pa - p.area) > Mathf.Max(25f, p.area * 0.08f)) err.Add(hs.stopId + ": outline area " + pa.ToString("0") + " m2 differs from raster area " + p.area.ToString("0") + " m2");
            }
        }
        // edges
        float total = 0;
        for (int i = 0; i < r.edges.Length; i++)
        {
            var e = r.edges[i]; string tag = "edge " + i;
            if (e.a < 0 || e.a >= r.parcels.Length) { err.Add(tag + ": a = " + e.a + " is not a parcel"); continue; }
            if (e.points == null || e.points.Length < 2) { err.Add(tag + ": fewer than 2 points"); continue; }
            if (e.kind == EdgeKind.ParcelParcel)
            {
                if (e.b < 0 || e.b >= r.parcels.Length || e.b == e.a) { err.Add(tag + ": parcel-parcel edge with b = " + e.b); continue; }
                if (e.a > e.b) err.Add(tag + ": parcel-parcel edge is not ordered a < b");
                string ca = r.parcels[e.a].chain, cb = r.parcels[e.b].chain;
                if (e.ownerChain != (string.CompareOrdinal(ca, cb) <= 0 ? ca : cb)) err.Add(tag + ": wrong ownerChain");
                if (Array.IndexOf(r.parcels[e.a].neighbours, e.b) < 0 || Array.IndexOf(r.parcels[e.b].neighbours, e.a) < 0) err.Add(tag + ": neighbours lists do not match");
                edgeLen[e.b] += e.length;
            }
            else
            {
                bool ok = (e.kind == EdgeKind.ParcelRoad && e.b == RoadReserve && e.roadId != null) || (e.kind == EdgeKind.ParcelWater && e.b == Water) ||
                          (e.kind == EdgeKind.ParcelReserved && e.b == Reserved) || (e.kind == EdgeKind.ParcelCommon && (e.b == Common || e.b == Outside));
                if (!ok) err.Add(tag + ": kind " + e.kind + " does not match b = " + e.b);
                if (e.ownerChain != r.parcels[e.a].chain) err.Add(tag + ": wrong ownerChain");
                if (e.kind == EdgeKind.ParcelCommon && e.b == Common && (e.common < 0 || e.common >= r.commons.Length)) err.Add(tag + ": common id missing");
            }
            if (e.frontage) { if (e.kind != EdgeKind.ParcelRoad) err.Add(tag + ": frontage flag on a non-road edge"); frontLen[e.a] += e.length; }
            if (!(e.length > 0f) || float.IsNaN(e.length)) err.Add(tag + ": zero length");
            edgeLen[e.a] += e.length; total += e.length;
        }
        float perimeter = 0;
        for (int h = 0; h < r.parcels.Length; h++)
        {
            if (count[h] == 0) continue; var p = r.parcels[h];
            float per = 0; if (p.outline != null) for (int i = 0; i < p.outline.Length; i++) per += Vector3.Distance(p.outline[i], p.outline[(i + 1) % p.outline.Length]);
            perimeter += per;
            if (edgeLen[h] < per - 0.5f) err.Add(houses[h].stopId + ": its edges add up to " + edgeLen[h].ToString("0.0") + " m but its outline is " + per.ToString("0.0") + " m");
            if (frontLen[h] < 3f) err.Add(houses[h].stopId + ": parcel has no street frontage (" + frontLen[h].ToString("0.0") + " m)");
        }
        if (r.parcels.Length > 0 && (total <= 0 || total > perimeter + 1f)) err.Add("total edge length " + total.ToString("0") + " m is not within the summed outline length " + perimeter.ToString("0") + " m");
        // commons
        for (int c = 0; c < r.commons.Length; c++)
        {
            var cr = r.commons[c];
            if (cr.id != c) err.Add("common " + c + ": id mismatch");
            if (cr.outline == null || cr.outline.Length < 3) err.Add("common " + c + ": no outline");
            foreach (int p in cr.adjacentParcels) if (p < 0 || p >= r.parcels.Length) err.Add("common " + c + ": invalid adjacent parcel " + p);
        }
        // determinism
        if (rebuild)
        {
            var again = Build(r.input); bool same = again.gridWidth == W && again.gridHeight == H && again.edges.Length == r.edges.Length && again.commons.Length == r.commons.Length;
            if (same) for (int i = 0; i < N; i++) if (again.label[i] != label[i]) { same = false; break; }
            if (same) for (int i = 0; i < r.edges.Length && same; i++)
                {
                    var a = r.edges[i]; var b = again.edges[i];
                    if (a.a != b.a || a.b != b.b || a.kind != b.kind || a.points.Length != b.points.Length || a.ownerChain != b.ownerChain) { same = false; break; }
                    for (int k = 0; k < a.points.Length; k++) if (a.points[k] != b.points[k]) { same = false; break; }
                }
            if (same) for (int i = 0; i < r.parcels.Length && same; i++)
                {
                    var a = r.parcels[i].outline; var b = again.parcels[i].outline;
                    if (a.Length != b.Length) { same = false; break; }
                    for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) { same = false; break; }
                }
            if (!same) err.Add("not deterministic: a second Build of the same input differs");
        }
        return err;
    }

    static float PolyArea(Vector3[] p)
    {
        double a = 0; for (int i = 0; i < p.Length; i++) { var u = p[i]; var v = p[(i + 1) % p.Length]; a += (double)u.x * v.z - (double)v.x * u.z; }
        return (float)(a * .5);
    }

    static void QuadCoverage(Result r, ref Quad q, int want, float margin, bool otherParcelsOnly, out int inside, out int outside, out string where)
    {
        inside = 0; outside = 0; int road = 0, water = 0, reserved = 0, common = 0, other = 0, off = 0;
        float cell = r.cell, x0 = r.originX, z0 = r.originZ; int W = r.gridWidth, H = r.gridHeight;
        float minX = Mathf.Min(Mathf.Min(q.a.x, q.b.x), Mathf.Min(q.c.x, q.d.x)) - margin, maxX = Mathf.Max(Mathf.Max(q.a.x, q.b.x), Mathf.Max(q.c.x, q.d.x)) + margin;
        float minZ = Mathf.Min(Mathf.Min(q.a.z, q.b.z), Mathf.Min(q.c.z, q.d.z)) - margin, maxZ = Mathf.Max(Mathf.Max(q.a.z, q.b.z), Mathf.Max(q.c.z, q.d.z)) + margin;
        int i0 = Mathf.FloorToInt((minX - x0) / cell), i1 = Mathf.FloorToInt((maxX - x0) / cell), j0 = Mathf.FloorToInt((minZ - z0) / cell), j1 = Mathf.FloorToInt((maxZ - z0) / cell);
        string otherName = null;
        for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
            {
                if (!InQuad(ref q, x0 + (i + .5f) * cell, z0 + (j + .5f) * cell, margin)) continue;
                int l = r.LabelAtCell(i, j);
                if (l == want || (otherParcelsOnly && l < 0)) { inside++; continue; }
                outside++;
                if (l == RoadReserve) road++; else if (l == Water) water++; else if (l == Reserved) reserved++; else if (l == Common) common++; else if (l == Outside) off++;
                else { other++; otherName = r.parcels[l].house.stopId; }
            }
        var parts = new List<string>(); float a = cell * cell;
        if (road > 0) parts.Add((road * a).ToString("0.#") + " on road reserve");
        if (water > 0) parts.Add((water * a).ToString("0.#") + " in the water setback");
        if (reserved > 0) parts.Add((reserved * a).ToString("0.#") + " on reserved land");
        if (common > 0) parts.Add((common * a).ToString("0.#") + " on common land");
        if (other > 0) parts.Add((other * a).ToString("0.#") + " in the parcel of " + otherName);
        if (off > 0) parts.Add((off * a).ToString("0.#") + " outside the bounds");
        where = string.Join(", ", parts.ToArray());
    }

    static string Why(Result r, House hs)
    {
        var c = (hs.footprint.a + hs.footprint.b + hs.footprint.c + hs.footprint.d) * .25f;
        return "building centre " + c.x.ToString("0.0") + ", " + c.z.ToString("0.0") + ": water reserve " + r.WaterDistance(c).ToString("0.0") + " m, road edge " + r.RoadEdgeDistance(c).ToString("0.0") + " m, label " + r.LabelAt(c);
    }
}

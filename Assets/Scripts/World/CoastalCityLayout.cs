using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>Runtime view of the editable city blueprint. Coordinates live in one JSON asset, not gameplay code.</summary>
public static class CoastalCityLayout
{
    public const int Revision = 4;
    public const string ResourcePath = "City/CoastalCity";
    public const string BlueprintPath = "Assets/Resources/City/CoastalCity.json";
    public const string MarkerName = "Coastal City Layout v4";
    public const string LayoutId = "coastal-v4";

    [Serializable] public sealed class Blueprint
    {
        public int schema;
        public float scale;
        public float[] bounds;
        public DistrictSpec[] districts;
        public NodeSpec[] nodes;
        public RoadSpec[] roads;
        public GateSpec[] gates;
        public DepotSpec depot;
        public TerrainSpec terrain;
        public LandmarkSpec[] landmarks;
        public LotStrip[] lotStrips;
        public CollectionSite[] collectionSites;
        public InfillSite[] infillSites;
    }
    [Serializable] public sealed class NodeSpec { public string id; public float x, z; }
    [Serializable] public sealed class DistrictSpec { public string id; public float[] bounds; }
    [Serializable] public sealed class RoadSpec { public string id, name, districtId; public string[] nodes; public bool curve, bridge, service; public float halfWidth; }
    [Serializable] public sealed class GateSpec { public string id, districtId, roadId; public float u; public int forward = 1; }
    [Serializable] public sealed class DepotSpec { public string roadId; public float[] stations; public float lateral, yawOffset, padHalfWidth, padBack; }
    [Serializable] public sealed class TerrainSpec { public string[] river, coast, riverWalk; }
    [Serializable] public sealed class LandmarkSpec { public string id; public float x, z; }
    [Serializable] public sealed class LotStrip { public string districtId, roadId; public int side, count; public float start, end, spacing; }
    [Serializable] public sealed class CollectionSite { public string id, districtId, roadId, type, name; public float u; public int side; }
    /// <summary>Non-collecting street frontage. Dimensions are life-sized metres; setback starts beyond the 3 m sidewalk.</summary>
    [Serializable] public sealed class InfillSite
    {
        public string id, roadId, type;
        public float u, width, depth, setback;
        public int side;
    }
    static Blueprint data;
    static List<RoadDef> roads;
    static GateDef[] gates;
    static Dictionary<string, Vector3> nodes;
    public static Blueprint Data { get { Ensure(); return data; } }
    public static float Scale => Data.scale;
    public static Rect BoundsXZ { get { var b = Data.bounds; return Rect.MinMaxRect(b[0] * Scale, b[1] * Scale, b[2] * Scale, b[3] * Scale); } }
    public static Vector3 Point(float x, float z, float y = 0) => new Vector3(x * Scale, y, z * Scale);
    public static Rect PlanRect(float x, float z, float w, float h) => new Rect(x * Scale, z * Scale, w * Scale, h * Scale);
    public static Rect DistrictBounds(string id)
    {
        foreach (var d in Data.districts) if (d.id == id) return PlanRect(d.bounds[0], d.bounds[1], d.bounds[2], d.bounds[3]);
        var starter = Data.districts[0]; return PlanRect(starter.bounds[0], starter.bounds[1], starter.bounds[2], starter.bounds[3]);
    }
    public static Vector3 Landmark(string id)
    {
        foreach (var p in Data.landmarks) if (p.id == id) return Point(p.x, p.z);
        throw new ArgumentException("Unknown city landmark: " + id);
    }
    public static void Reload() { data = null; roads = null; gates = null; nodes = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() => Reload();
    static void Ensure()
    {
        if (data != null) return;
        var asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null) throw new InvalidOperationException("Missing city blueprint: " + BlueprintPath);
        var parsed = JsonConvert.DeserializeObject<Blueprint>(asset.text);
        var errors = ValidateBlueprint(parsed);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid city blueprint: " + string.Join("; ", errors));
        data = parsed;
        nodes = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var n in data.nodes) nodes.Add(n.id, new Vector3(n.x * data.scale, 0, n.z * data.scale));
        roads = new List<RoadDef>();
        foreach (var r in data.roads)
            roads.Add(ResolveRoad(data, r.id));
        gates = new GateDef[data.gates.Length];
        for (int i = 0; i < gates.Length; i++)
        {
            var g = data.gates[i]; var pose = Road(g.roadId).AtNormalized(g.u);
            gates[i] = new GateDef { id = g.id, districtId = g.districtId, position = pose.position, forward = pose.rotation * Vector3.forward * g.forward };
        }
    }

    /// <summary>Pure blueprint resolver used by the loader and edit-preview tests. Shared junction IDs ensure every
    /// incident road sees a moved node; does not read or replace the live cached layout.</summary>
    public static RoadDef ResolveRoad(Blueprint blueprint, string roadId)
    {
        if (blueprint == null || blueprint.roads == null || blueprint.nodes == null || !Finite(blueprint.scale) || blueprint.scale <= 0)
            throw new ArgumentException("A valid blueprint is required.", nameof(blueprint));
        RoadSpec spec = Array.Find(blueprint.roads, r => r != null && string.Equals(r.id, roadId, StringComparison.Ordinal));
        if (spec == null || spec.nodes == null || spec.nodes.Length < 2) throw new ArgumentException("Unknown or empty road: " + roadId, nameof(roadId));
        var lookup = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
        foreach (var node in blueprint.nodes)
        {
            if (node == null || string.IsNullOrEmpty(node.id) || !Finite(node.x) || !Finite(node.z) || lookup.ContainsKey(node.id))
                throw new ArgumentException("Invalid or duplicate blueprint junction.", nameof(blueprint));
            lookup.Add(node.id, node);
        }
        var points = new Vector3[spec.nodes.Length];
        for (int i = 0; i < points.Length; i++)
        {
            if (!lookup.TryGetValue(spec.nodes[i], out var node)) throw new ArgumentException(roadId + " references missing junction " + spec.nodes[i]);
            points[i] = new Vector3(node.x * blueprint.scale, 0f, node.z * blueprint.scale);
            if (i > 0 && (points[i] - points[i - 1]).sqrMagnitude < .000001f) throw new ArgumentException(roadId + " contains a zero-length segment.");
        }
        return new RoadDef { id = spec.id, name = spec.name, districtId = spec.districtId, points = spec.curve ? Smooth(points) : points,
            halfWidth = spec.halfWidth, bridge = spec.bridge, service = spec.service };
    }
    public static List<string> ValidateBlueprint(Blueprint b)
    {
        var errors = new List<string>();
        if (b == null) { errors.Add("JSON could not be read"); return errors; }
        if (b.schema != Revision) errors.Add("Unsupported schema " + b.schema);
        if (!(b.scale > .05f && b.scale < 10f)) errors.Add("Invalid scale");
        if (b.bounds == null || b.bounds.Length != 4 || b.bounds[2] <= b.bounds[0] || b.bounds[3] <= b.bounds[1]) errors.Add("Invalid map bounds");
        var nodeIds = new HashSet<string>();
        foreach (var n in b.nodes ?? Array.Empty<NodeSpec>()) if (string.IsNullOrEmpty(n.id) || !nodeIds.Add(n.id) || !Finite(n.x) || !Finite(n.z)) errors.Add("Invalid/duplicate node " + n.id);
        var districtIds = new HashSet<string>();
        foreach (var d in b.districts ?? Array.Empty<DistrictSpec>())
            if (string.IsNullOrEmpty(d.id) || !districtIds.Add(d.id) || d.bounds == null || d.bounds.Length != 4 || d.bounds[2] <= 0 || d.bounds[3] <= 0) errors.Add("Invalid district " + d.id);
        var roadIds = new HashSet<string>();
        foreach (var r in b.roads ?? Array.Empty<RoadSpec>())
        {
            if (string.IsNullOrEmpty(r.id) || !roadIds.Add(r.id) || r.halfWidth < 2f || !districtIds.Contains(r.districtId)) errors.Add("Invalid road " + r.id);
            if (r.nodes == null || r.nodes.Length < 2) { errors.Add("Road has fewer than two nodes: " + r.id); continue; }
            foreach (var id in r.nodes) if (!nodeIds.Contains(id)) errors.Add(r.id + " references missing junction " + id);
            for (int i = 1; i < r.nodes.Length; i++) if (r.nodes[i] == r.nodes[i - 1]) errors.Add(r.id + " repeats adjacent junction");
        }
        var gateIds = new HashSet<string>();
        foreach (var g in b.gates ?? Array.Empty<GateSpec>())
            if (!gateIds.Add(g.id) || !roadIds.Contains(g.roadId) || !districtIds.Contains(g.districtId) || g.u < 0 || g.u > 1 || Math.Abs(g.forward) != 1) errors.Add("Invalid gate " + g.id);
        if (b.depot == null || !roadIds.Contains(b.depot.roadId) || b.depot.stations == null || b.depot.stations.Length != 4) errors.Add("Depot must reference a road and four stations");
        foreach (var s in b.lotStrips ?? Array.Empty<LotStrip>())
            if (!roadIds.Contains(s.roadId) || !districtIds.Contains(s.districtId) || s.count < 0 || s.spacing < 10 || s.start < 0 || s.end > 1 || s.start >= s.end || Math.Abs(s.side) != 1) errors.Add("Invalid lot strip on " + s.roadId);
        var siteIds = new HashSet<string>();
        foreach (var s in b.collectionSites ?? Array.Empty<CollectionSite>())
            if (!siteIds.Add(s.id) || !roadIds.Contains(s.roadId) || !districtIds.Contains(s.districtId) || s.u < 0 || s.u > 1 || Math.Abs(s.side) != 1) errors.Add("Invalid collection site " + s.id);
        var infillIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in b.infillSites ?? Array.Empty<InfillSite>())
        {
            if (s == null) { errors.Add("Null infill site"); continue; }
            if (string.IsNullOrEmpty(s.id) || !infillIds.Add(s.id) || !roadIds.Contains(s.roadId) ||
                !CoastalInfillSite.KnownType(s.type) || !Finite(s.u) || s.u < 0 || s.u > 1 || Math.Abs(s.side) != 1 ||
                !Finite(s.width) || s.width < 8 || s.width > 160 || !Finite(s.depth) || s.depth < 8 || s.depth > 100 ||
                !Finite(s.setback) || s.setback < .25f || s.setback > 100 ||
                s.type == "storefront" && s.depth < 12 || s.type == "workyard" && (s.width < 12 || s.depth < 12) ||
                s.type == "courtyard" && (s.width < 20 || s.depth < 16) ||
                s.type == "grove" && (s.width < 20 || s.depth < 20))
                errors.Add("Invalid infill site " + s.id + " (road/type, position, or metre-sized footprint)");
        }
        if (b.terrain == null) errors.Add("Missing terrain paths");
        else foreach (var path in new[] { b.terrain.river, b.terrain.coast, b.terrain.riverWalk })
        {
            if (path == null || path.Length < 2) { errors.Add("Terrain path has fewer than two nodes"); continue; }
            foreach (var n in path) if (!nodeIds.Contains(n)) errors.Add("Terrain references missing node " + n);
        }
        return errors;
    }
    static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    static Vector3[] Resolve(string[] ids) { var p = new Vector3[ids.Length]; for (int i = 0; i < ids.Length; i++) p[i] = nodes[ids[i]]; return p; }
    public sealed class RoadDef
    {
        public string id, name, districtId;
        public Vector3[] points;
        public float halfWidth;
        public bool bridge, service;
        public float Length { get { float n = 0; for (int i = 1; i < points.Length; i++) n += Vector3.Distance(points[i - 1], points[i]); return n; } }
        public Pose AtNormalized(float u) => At(Mathf.Clamp01(u) * Length);
        public Pose At(float metres)
        {
            for (int i = 1; i < points.Length; i++)
            {
                Vector3 delta = points[i] - points[i - 1]; float length = delta.magnitude;
                // Adding a collinear junction can change accumulated float lengths by micrometres.
                // At an existing corner, keep the incoming tangent deterministic for household anchors.
                if (metres <= length + .0001f || i == points.Length - 1)
                    return new Pose(Vector3.Lerp(points[i - 1], points[i], Mathf.Clamp01(metres / Mathf.Max(.001f, length))), Quaternion.LookRotation(delta.normalized));
                metres -= length;
            }
            return new Pose(points[0], Quaternion.identity);
        }
        public string Fingerprint
        {
            get
            {
                var s = new System.Text.StringBuilder(id).Append('|').Append(name).Append('|').Append(districtId).Append('|').Append(halfWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(bridge).Append('|').Append(service);
                foreach (var p in points) s.Append('|').Append(p.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return Hash128.Compute(s.ToString()).ToString();
            }
        }
    }
    public sealed class GateDef { public string id, districtId; public Vector3 position, forward; }
    public static IReadOnlyList<RoadDef> Roads { get { Ensure(); return roads; } }
    public static RoadDef Road(string id) { Ensure(); return roads.Find(r => r.id == id); }
    public static GateDef[] Gates { get { Ensure(); return gates; } }
    public static Vector3[] River { get { Ensure(); return Smooth(Resolve(data.terrain.river)); } }
    public static Vector3[] Coast { get { Ensure(); return Smooth(Resolve(data.terrain.coast)); } }
    public static Vector3[] RiverWalk { get { Ensure(); return Smooth(Resolve(data.terrain.riverWalk)); } }
    public static bool IsCoastal(Transform root) => root != null && root.Find(MarkerName) != null;
    public static Pose DepotRoadPose(int lot) => Road(Data.depot.roadId).AtNormalized(Data.depot.stations[Mathf.Clamp(lot, 0, 3)]);
    public static Vector3 DepotRoadPosition(int lot) => DepotRoadPose(lot).position;
    public static Vector3 DepotDoor(int lot) { var pose = DepotRoadPose(lot); return pose.position + pose.rotation * Vector3.right * Data.depot.lateral; }
    public static Quaternion DepotFacing => DepotRoadPose(0).rotation * Quaternion.Euler(0, Data.depot.yawOffset, 0);
    public static Rect DepotPad(int lot)
    {
        var p = DepotDoor(lot); var rot = DepotRoadPose(lot).rotation * Quaternion.Euler(0, Data.depot.yawOffset, 0);
        float front = Mathf.Abs(Data.depot.lateral) - Road(Data.depot.roadId).halfWidth;
        var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
        foreach (float x in new[] { -Data.depot.padHalfWidth, Data.depot.padHalfWidth }) foreach (float z in new[] { -Data.depot.padBack, front })
        { var v = p + rot * new Vector3(x, 0, z); min = Vector2.Min(min, new Vector2(v.x, v.z)); max = Vector2.Max(max, new Vector2(v.x, v.z)); }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }
    public static float DistanceToRoad(Vector3 p)
    {
        float nearest = float.MaxValue;
        foreach (var road in Roads) for (int i = 1; i < road.points.Length; i++) nearest = Mathf.Min(nearest, DistanceToSegment(p, road.points[i - 1], road.points[i]) - road.halfWidth);
        return nearest;
    }
    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    { p.y = a.y = b.y = 0; var d = b - a; return Vector3.Distance(p, a + d * Mathf.Clamp01(Vector3.Dot(p - a, d) / Mathf.Max(.0001f, d.sqrMagnitude))); }
    public static Vector3[] P(params float[] xz)
    { var result = new Vector3[xz.Length / 2]; for (int i = 0; i < result.Length; i++) result[i] = Point(xz[i * 2], xz[i * 2 + 1]); return result; }
    public static Vector3[] Smooth(Vector3[] p)
    {
        var result = new List<Vector3> { p[0] };
        for (int i = 0; i < p.Length - 1; i++)
        {
            var a = p[i]; var b = p[i + 1];
            var c = a + (b - p[Mathf.Max(0, i - 1)]) * .13f; var d = b - (p[Mathf.Min(p.Length - 1, i + 2)] - a) * .13f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / 6f));
            for (int j = 1; j <= steps; j++)
            { float t = j / (float)steps, u = 1 - t; result.Add(u * u * u * a + 3 * u * u * t * c + 3 * u * t * t * d + t * t * t * b); }
        }
        return result.ToArray();
    }
}

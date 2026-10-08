using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What a map area is (the raster draws each kind in its own layer and colour).</summary>
public enum MapAreaKind : byte
{
    Lot, Park, ParkPath, Sidewalk, Curb, Driveway, GardenPath, DepotPad, Building, House, BigHouse, Water, Court, Hedge, Sand,
    Helipad, Tunnel, Green, Fence, StoneWall,
}

public enum MapLabelKind : byte { Street, District, Park, Depot, Exit, Poi }

public enum MapPoiKind : byte { Depot, GarageDoor, Gate, Exit, TruckParking, Fountain, Helipad }

/// <summary>
/// The baked town map (Docs/UIPass.md §7.2): every road, sidewalk, lot, park, pad and house of the Neighborhood as
/// axis-aligned world rects, plus street names, district gates, POIs and the house list, written by <c>Setup_TownMap</c> to
/// <c>Assets/Resources/TownMap.asset</c> next to the cartoon PNG it rasterised (<see cref="texture"/>). One texture serves
/// the minimap and the full map; icons and labels are drawn live on top from this data.
/// <para><b>Map space</b> = pixels of the PNG, origin bottom-left (z0), +x east, +y north. World ↔ map is a scale and an offset
/// (<see cref="WorldToMap"/>, <see cref="MapToWorld"/>), so a round trip is exact to float precision.</para>
/// </summary>
public sealed class TownMapData : ScriptableObject
{
    public const int SchemaVersion = 1;
    public const string ResourcePath = "TownMap";
    public const string AssetPath = "Assets/Resources/TownMap.asset";
    public const string TexturePath = "Assets/UI/map/town_map.png";

    [Serializable]
    public struct Road
    {
        /// <summary>World XZ rect (x = x0, y = z0).</summary>
        public Rect rect;
        public string name;
        /// <summary>0 = along X, 1 = along Z, 2 = a square plaza / bulb, 3 = sampled polyline.</summary>
        public byte axis;
        public string districtId;
        /// <summary>A town exit road (Old Highway, Interstate On-Ramp, Private Airport Rd).</summary>
        public bool exit;
        /// <summary>Optional sampled centreline; when present the rect is broad-phase bounds only.</summary>
        public Vector2[] points;
        public float halfWidth;
    }

    [Serializable]
    public struct Area
    {
        public Rect rect;
        public MapAreaKind kind;
        /// <summary>Optional polygon, or a stroked centreline when halfWidth is positive.</summary>
        public Vector2[] points;
        public float halfWidth;
    }

    [Serializable]
    public struct District
    {
        public string id;
        public string name;
        public Rect rect;
        public bool hasGate;
        /// <summary>Gate position (world XZ) and the direction INTO the district.</summary>
        public Vector2 gate, gateForward;
        /// <summary>Where the big district label goes (world XZ).</summary>
        public Vector2 labelPos;
    }

    [Serializable]
    public struct Label
    {
        public string text;
        /// <summary>World XZ.</summary>
        public Vector2 pos;
        /// <summary>Degrees, counter-clockwise from +X on the map.</summary>
        public float angle;
        /// <summary>Reference font size at 1× full-map zoom.</summary>
        public float size;
        public MapLabelKind kind;
    }

    [Serializable]
    public struct Poi
    {
        public MapPoiKind kind;
        public Vector2 pos;
        public string id;
        /// <summary>Degrees (world yaw) where it matters (gates, exits).</summary>
        public float heading;
    }

    [Serializable]
    public struct House
    {
        public string stopId;
        public string address;
        /// <summary>The curb stop (world XZ): where its bags (and pip) are.</summary>
        public Vector2 curb;
        public Rect footprint;
        public string districtId;
    }

    public int schema = SchemaVersion;
    public string layoutId = "";
    /// <summary>World XZ bounds of the map (x = x0, y = z0): the town's PlayBounds.</summary>
    public Rect bounds = new Rect(-60f, -40f, 500f, 680f);
    public float pxPerMeter = 2f;
    public int width = 1000, height = 1360;
    /// <summary>The cartoon raster (Assets/UI/map/town_map.png).</summary>
    public Texture2D texture;
    /// <summary>A hash of the scene inputs at bake time (ground rects, stops, gates): <see cref="TownMapAnchor"/> compares it.</summary>
    public string sourceHash = "";

    public List<Road> roads = new List<Road>();
    public List<Area> areas = new List<Area>();
    public List<District> districts = new List<District>();
    public List<Label> labels = new List<Label>();
    public List<Poi> pois = new List<Poi>();
    public List<House> houses = new List<House>();

    // ------------------------------------------------------------------ loading

    static TownMapData cached;
    static bool looked;

    /// <summary>The baked map (Resources/TownMap), or null before the first bake.</summary>
    public static TownMapData Load()
    {
        if (cached != null) return cached;
        if (looked && Application.isPlaying) return null;
        looked = true;
        try { cached = Resources.Load<TownMapData>(ResourcePath); }
        catch (Exception e) { Debug.LogWarning("[TownMap] load failed: " + e.Message); }
        if (cached != null && Application.isPlaying) cached = NeighborhoodConnection.WithConnection(cached);
        return cached;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cached = null;
        looked = false;
    }

    /// <summary>Forget the cached asset (the bake replaced it).</summary>
    public static void Unload() { cached = null; looked = false; }

    // ------------------------------------------------------------------ coordinates

    /// <summary>World → map pixels (origin bottom-left).</summary>
    public Vector2 WorldToMap(Vector3 w) => new Vector2((w.x - bounds.x) * pxPerMeter, (w.z - bounds.y) * pxPerMeter);

    /// <summary>Map pixels → world (y = 0).</summary>
    public Vector3 MapToWorld(Vector2 m) => new Vector3(bounds.x + m.x / pxPerMeter, 0f, bounds.y + m.y / pxPerMeter);

    /// <summary>World → texture UV (0..1).</summary>
    public Vector2 WorldToUv(Vector3 w) => new Vector2((w.x - bounds.x) / bounds.width, (w.z - bounds.y) / bounds.height);

    /// <summary>World XZ (as a Vector2) → map pixels.</summary>
    public Vector2 XZToMap(Vector2 xz) => new Vector2((xz.x - bounds.x) * pxPerMeter, (xz.y - bounds.y) * pxPerMeter);

    public Vector2 SizeMeters => bounds.size;

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// The closest point on any road to <paramref name="w"/> (at least 1 m inside the road's edges) and its street name.
    /// Used by Unstuck (UP-3) and the street-label pill. False only when the map has no roads.
    /// </summary>
    public bool TryNearestRoad(Vector3 w, out Vector3 onRoad, out string streetName)
    {
        onRoad = w;
        streetName = null;
        float best = float.MaxValue;
        int bi = -1;
        for (int i = 0; i < roads.Count; i++)
        {
            var r = roads[i].rect;
            var road = roads[i];
            var p = road.points != null && road.points.Length >= 2
                ? NearestOnStrip(road.points, Mathf.Max(0, road.halfWidth - 1f), new Vector2(w.x, w.z))
                : ClampInside(r, new Vector2(w.x, w.z), 1f);
            float d = (p.x - w.x) * (p.x - w.x) + (p.y - w.z) * (p.y - w.z);
            if (d < best) { best = d; bi = i; onRoad = new Vector3(p.x, w.y, p.y); }
        }
        if (bi < 0) return false;
        streetName = roads[bi].name;
        return true;
    }

    /// <summary>The closest point inside an area of <paramref name="kind"/> (e.g. a sidewalk for Unstuck), at least 0.5 m in.</summary>
    public bool TryNearestArea(Vector3 w, MapAreaKind kind, out Vector3 p)
    {
        p = w;
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < areas.Count; i++)
        {
            if (areas[i].kind != kind) continue;
            var area = areas[i];
            var q = area.points != null && area.points.Length >= 2 && area.halfWidth > 0
                ? NearestOnStrip(area.points, Mathf.Max(0, area.halfWidth - .5f), new Vector2(w.x, w.z))
                : ClampInside(area.rect, new Vector2(w.x, w.z), 0.5f);
            float d = (q.x - w.x) * (q.x - w.x) + (q.y - w.z) * (q.y - w.z);
            if (d < best) { best = d; p = new Vector3(q.x, w.y, q.y); found = true; }
        }
        return found;
    }

    static Vector2 ClampInside(Rect r, Vector2 p, float inset)
    {
        float ix = Mathf.Min(inset, r.width * 0.5f), iz = Mathf.Min(inset, r.height * 0.5f);
        return new Vector2(Mathf.Clamp(p.x, r.xMin + ix, r.xMax - ix), Mathf.Clamp(p.y, r.yMin + iz, r.yMax - iz));
    }

    public static Vector2 NearestOnStrip(Vector2[] points, float halfWidth, Vector2 p)
    {
        var nearest = p; float best = float.MaxValue;
        for (int i = 1; i < points.Length; i++)
        {
            var d = points[i] - points[i - 1];
            var centre = points[i - 1] + d * Mathf.Clamp01(Vector2.Dot(p - points[i - 1], d) / Mathf.Max(.00001f, d.sqrMagnitude));
            var q = centre + Vector2.ClampMagnitude(p - centre, halfWidth);
            float distance = (q - p).sqrMagnitude;
            if (distance < best) { best = distance; nearest = q; }
        }
        return nearest;
    }

    /// <summary>The street at a position: the road under it, else the nearest road within <paramref name="maxDist"/> m.</summary>
    public string StreetAt(Vector3 w, float maxDist = 12f)
    {
        float best = maxDist * maxDist;
        string name = null;
        var p = new Vector2(w.x, w.z);
        for (int i = 0; i < roads.Count; i++)
        {
            var r = roads[i].rect;
            float dx = Mathf.Max(0f, Mathf.Max(r.xMin - p.x, p.x - r.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(r.yMin - p.y, p.y - r.yMax));
            float d = dx * dx + dz * dz;
            if (roads[i].points != null && roads[i].points.Length >= 2)
                d = (NearestOnStrip(roads[i].points, roads[i].halfWidth, p) - p).sqrMagnitude;
            // inside two roads (an intersection): prefer the one whose long axis we're further along
            if (d < best || (d == 0f && best == 0f && Inner(r, roads[i].axis, p) > 0f))
            {
                best = d;
                name = roads[i].name;
            }
        }
        return name;
    }

    static float Inner(Rect r, byte axis, Vector2 p) =>
        axis == 0 ? Mathf.Min(p.x - r.xMin, r.xMax - p.x) - r.height * 0.5f :
        axis == 1 ? Mathf.Min(p.y - r.yMin, r.yMax - p.y) - r.width * 0.5f : 0f;

    Dictionary<string, int> houseIndex;

    /// <summary>Index into <see cref="houses"/> for a stopId, or −1.</summary>
    public int HouseIndex(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return -1;
        if (houseIndex == null || houseIndex.Count != houses.Count)
        {
            houseIndex = new Dictionary<string, int>(houses.Count, StringComparer.Ordinal);
            for (int i = 0; i < houses.Count; i++)
                if (!string.IsNullOrEmpty(houses[i].stopId)) houseIndex[houses[i].stopId] = i;
        }
        return houseIndex.TryGetValue(stopId, out int k) ? k : -1;
    }

    /// <summary>The house whose curb or footprint is nearest <paramref name="w"/> within <paramref name="radius"/> m, or −1.</summary>
    public int HouseNear(Vector3 w, float radius)
    {
        var p = new Vector2(w.x, w.z);
        float best = radius * radius;
        int bi = -1;
        for (int i = 0; i < houses.Count; i++)
        {
            var h = houses[i];
            float d = (h.curb - p).sqrMagnitude;
            var f = h.footprint;
            if (f.width > 0f && f.Contains(p)) d = 0f;
            if (d < best) { best = d; bi = i; }
        }
        return bi;
    }

    /// <summary>The district rect containing a position, richest first (like <see cref="Districts.AtPosition"/>), or −1.</summary>
    public int DistrictAt(Vector2 xz)
    {
        for (int i = districts.Count - 1; i >= 0; i--)
            if (districts[i].rect.Contains(xz)) return i;
        return -1;
    }

    public int DistrictIndex(string id)
    {
        for (int i = 0; i < districts.Count; i++) if (districts[i].id == id) return i;
        return -1;
    }

    // ------------------------------------------------------------------ source hash (bake ↔ scene staleness)

    /// <summary>
    /// A stable hash of the scene inputs the map depends on: the Ground rects (roads, sidewalks, curbs, pads), every stop's id and
    /// curb position, and every district gate. Rounded to centimetres, FNV-1a 64, hex. The bake stores it; the anchor's dev
    /// check recomputes it from the live scene to spot a town rebuilt without a re-bake.
    /// </summary>
    public static string HashSources(List<Rect> groundRects, List<KeyValuePair<string, Vector3>> stops, List<KeyValuePair<string, Vector3>> gates)
    {
        ulong h = 14695981039346656037UL;
        void Int(long v)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; }
            }
        }
        void Str(string s)
        {
            unchecked
            {
                if (s == null) { Int(-1); return; }
                foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
                Int(s.Length);
            }
        }
        long Cm(float f) => (long)Math.Round(f * 100.0);
        var g = new List<Rect>(groundRects);
        g.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y != b.y ? a.y.CompareTo(b.y) : a.width != b.width ? a.width.CompareTo(b.width) : a.height.CompareTo(b.height));
        Int(g.Count);
        foreach (var r in g) { Int(Cm(r.x)); Int(Cm(r.y)); Int(Cm(r.width)); Int(Cm(r.height)); }
        var s2 = new List<KeyValuePair<string, Vector3>>(stops);
        s2.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Int(s2.Count);
        foreach (var kv in s2) { Str(kv.Key); Int(Cm(kv.Value.x)); Int(Cm(kv.Value.z)); }
        var g2 = new List<KeyValuePair<string, Vector3>>(gates);
        g2.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Int(g2.Count);
        foreach (var kv in g2) { Str(kv.Key); Int(Cm(kv.Value.x)); Int(Cm(kv.Value.z)); }
        return h.ToString("x16");
    }

    /// <summary>Collect the hash inputs from a built town root (NeighborhoodGen). Shared by the bake and the dev check.</summary>
    public static string HashScene(Transform neighborhoodGen)
    {
        var ground = new List<Rect>();
        var stops = new List<KeyValuePair<string, Vector3>>();
        var gates = new List<KeyValuePair<string, Vector3>>();
        if (neighborhoodGen == null) return HashSources(ground, stops, gates);
        var g = neighborhoodGen.Find("Ground");
        if (g != null)
            foreach (Transform c in g)
            {
                string n = c.name;
                if (!(n.StartsWith("Road_") || n.StartsWith("Sidewalk_") || n.StartsWith("Curb_") || n.StartsWith("DepotPad_"))) continue;
                var bc = c.GetComponent<BoxCollider>();
                if (bc == null) continue;
                var b = WorldBounds(bc);
                ground.Add(new Rect(b.min.x, b.min.z, b.size.x, b.size.z));
            }
        foreach (var s in TownAuthoringScope.Components<PickupStop>(neighborhoodGen))
            stops.Add(new KeyValuePair<string, Vector3>(s.stopId, s.transform.position));
        foreach (var gt in neighborhoodGen.GetComponentsInChildren<DistrictGate>(true))
        {
            // This known runtime extension has its own map overlay; retain stale detection for the authored base town.
            if (gt.GetComponentInParent<NeighborhoodConnection>() != null) continue;
            gates.Add(new KeyValuePair<string, Vector3>(gt.districtId, gt.transform.position));
        }
        return HashSources(ground, stops, gates);
    }

    /// <summary>A BoxCollider's world AABB computed from its transform (works with the collider or its object disabled).</summary>
    public static Bounds WorldBounds(BoxCollider bc)
    {
        var t = bc.transform;
        var c = bc.center;
        var e = bc.size * 0.5f;
        var b = new Bounds(t.TransformPoint(c), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            b.Encapsulate(t.TransformPoint(corner));
        }
        return b;
    }
}

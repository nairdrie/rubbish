// READ ONLY. Run with `unity command eval_file --file Tools/bots/eval/coastal_physical_audit.cs`.
// The generated Neighborhood must already be loaded in Edit Mode. No scene opening/saving, temporary colliders,
// transforms, asset writes, physics stepping, save access or blueprint reloads are performed.
if (UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Physical authoring audit requires Edit Mode (static authored geometry only).");
var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity");
if (!scene.IsValid() || !scene.isLoaded)
    throw new System.InvalidOperationException("Open the generated Neighborhood scene before running this read-only audit.");
UnityEngine.Transform town = null;
foreach (var root in scene.GetRootGameObjects()) if (root.name == "NeighborhoodGen") town = root.transform;
if (!CoastalCityLayout.IsCoastal(town))
    throw new System.InvalidOperationException("Loaded Neighborhood is not the generated coastal city.");

string Path(UnityEngine.Transform t)
{
    var names = new System.Collections.Generic.List<string>();
    for (; t != null; t = t.parent) names.Add(t.name);
    names.Reverse(); return string.Join("/", names);
}
float[] V(UnityEngine.Vector3 p) => new[] { p.x, p.y, p.z };
object Box(UnityEngine.Bounds b) => new { center = V(b.center), size = V(b.size) };
string Lot(UnityEngine.Component component)
{
    var anchor = component.GetComponentInParent<CityRoadAnchor>();
    return anchor != null ? anchor.stableId : "";
}
bool IsSceneCollider(UnityEngine.Collider collider)
{
    if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) return false;
    var name = collider.gameObject.scene.name;
    return collider.transform.IsChildOf(town) || name == DepotRowLayout.SceneName;
}
string IgnoreReason(UnityEngine.Collider collider, float groundY)
{
    if (!IsSceneCollider(collider)) return "unrelated/inactive/trigger";
    if (collider.bounds.max.y <= groundY + .24f) return "road/terrain/low-curb";
    if (collider.GetComponentInParent<DistrictGate>() != null) return "permit-gate";
    var knock = collider.GetComponentInParent<Knockable>();
    if (knock != null && knock.kind == KnockKind.Lamp) return "lamp";
    return null;
}
string Kind(UnityEngine.Collider c)
{
    if (c.GetComponentInParent<HouseholdCar>() != null) return "parked-car";
    if (c.GetComponentInParent<KidPen>() != null) return "playground-fence";
    string path = Path(c.transform).ToLowerInvariant();
    if (path.Contains("fence") || path.Contains("wall") || path.Contains("hedge")) return "fence/wall/hedge";
    if (path.Contains("house") || path.Contains("hall") || path.Contains("building") || path.Contains("condo") || path.Contains("shopfront")) return "building/parcel";
    if (path.Contains("park")) return "park-object";
    return "object";
}

// A narrow envelope in each channel distinguishes an actually blocked traffic lane from an overhanging roof AABB.
// Bottom .25m avoids road surfaces; top 3.35m also catches obstacles in the cab's headroom.
const float step = 2.5f;
var buffer = new UnityEngine.Collider[512];
var obstruction = new System.Collections.Generic.Dictionary<string,
    (string road, string roadName, string path, string lot, string kind, UnityEngine.Bounds bounds,
    System.Collections.Generic.HashSet<string> channels, int samples, int trafficSamples, float first, float last, UnityEngine.Vector3 firstPoint)>();
int sampledPositions = 0, queries = 0, saturatedQueries = 0;
var ignored = new System.Collections.Generic.Dictionary<string, int>();
foreach (var road in CoastalCityLayout.Roads)
{
    int count = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.CeilToInt(road.Length / step));
    var channels = new[] { "centre", "lane-right", "lane-left", "inside-curb-right", "inside-curb-left" };
    float lane = UnityEngine.Mathf.Min(StreetGraph.LaneOffset, road.halfWidth * .5f);
    var offsets = new[] { 0f, lane, -lane, road.halfWidth - .45f, -road.halfWidth + .45f };
    for (int i = 0; i <= count; i++)
    {
        float metres = road.Length * i / count;
        var pose = road.At(metres); sampledPositions++;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            float halfSide = channel == 0 ? 1.1f : channel < 3 ? .85f : .3f;
            var point = pose.position + pose.rotation * UnityEngine.Vector3.right * offsets[channel];
            var center = point + UnityEngine.Vector3.up * 1.8f;
            int found = UnityEngine.Physics.OverlapBoxNonAlloc(center, new UnityEngine.Vector3(halfSide, 1.55f, 1.2f),
                buffer, pose.rotation, ~0, UnityEngine.QueryTriggerInteraction.Ignore);
            queries++; if (found == buffer.Length) saturatedQueries++;
            for (int k = 0; k < found; k++)
            {
                var collider = buffer[k]; string reason = IgnoreReason(collider, pose.position.y);
                if (reason != null)
                { ignored[reason] = ignored.TryGetValue(reason, out int previous) ? previous + 1 : 1; continue; }
                string key = road.id + "|" + collider.GetInstanceID();
                if (!obstruction.TryGetValue(key, out var row))
                    row = (road.id, road.name, Path(collider.transform), Lot(collider), Kind(collider), collider.bounds,
                        new System.Collections.Generic.HashSet<string>(), 0, 0, metres, metres, point);
                row.channels.Add(channels[channel]); row.samples++; if (channel < 3) row.trafficSamples++;
                row.first = UnityEngine.Mathf.Min(row.first, metres); row.last = UnityEngine.Mathf.Max(row.last, metres);
                obstruction[key] = row;
            }
        }
    }
}

// Broad-phase footprint overlap is independent of collider setup, and catches a visible building above the road
// even when its generated collider is missing. It is intentionally reported separately from confirmed lane blockers.
var models = new System.Collections.Generic.List<(string stopId, UnityEngine.Transform root, UnityEngine.Bounds bounds, UnityEngine.Collider[] colliders)>();
var seenModels = new System.Collections.Generic.HashSet<int>();
foreach (var stop in town.GetComponentsInChildren<PickupStop>(true))
{
    var route = stop.GetComponent<CoastalHouseRoute>();
    var root = route != null ? route.buildingRoot : null;
    if (root == null || !seenModels.Add(root.GetInstanceID())) continue;
    bool any = false; UnityEngine.Bounds bounds = default;
    foreach (var renderer in root.GetComponentsInChildren<UnityEngine.Renderer>(true))
    {
        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer is UnityEngine.ParticleSystemRenderer) continue;
        if (!any) { bounds = renderer.bounds; any = true; } else bounds.Encapsulate(renderer.bounds);
    }
    if (any) models.Add((stop.stopId, root, bounds, root.GetComponentsInChildren<UnityEngine.Collider>(true)));
}
float PointRect(UnityEngine.Vector2 p, UnityEngine.Rect r)
{
    float x = UnityEngine.Mathf.Max(r.xMin - p.x, 0, p.x - r.xMax), z = UnityEngine.Mathf.Max(r.yMin - p.y, 0, p.y - r.yMax);
    return x * x + z * z;
}
float PointSegment(UnityEngine.Vector2 p, UnityEngine.Vector2 a, UnityEngine.Vector2 b)
{
    var d = b - a;
    return (p - a - d * UnityEngine.Mathf.Clamp01(UnityEngine.Vector2.Dot(p - a, d) / UnityEngine.Mathf.Max(.000001f, d.sqrMagnitude))).sqrMagnitude;
}
bool Clip(float p, float q, ref float low, ref float high)
{
    if (UnityEngine.Mathf.Abs(p) < .000001f) return q >= 0;
    float t = q / p;
    if (p < 0) { if (t > high) return false; low = UnityEngine.Mathf.Max(low, t); }
    else { if (t < low) return false; high = UnityEngine.Mathf.Min(high, t); }
    return true;
}
float SegmentRect(UnityEngine.Vector2 a, UnityEngine.Vector2 b, UnityEngine.Rect r)
{
    var delta = b - a; float low = 0, high = 1;
    if (Clip(-delta.x, a.x - r.xMin, ref low, ref high) && Clip(delta.x, r.xMax - a.x, ref low, ref high) &&
        Clip(-delta.y, a.y - r.yMin, ref low, ref high) && Clip(delta.y, r.yMax - a.y, ref low, ref high)) return 0;
    float best = UnityEngine.Mathf.Min(PointRect(a, r), PointRect(b, r));
    foreach (var corner in new[] { new UnityEngine.Vector2(r.xMin,r.yMin), new UnityEngine.Vector2(r.xMin,r.yMax),
        new UnityEngine.Vector2(r.xMax,r.yMin), new UnityEngine.Vector2(r.xMax,r.yMax) }) best = UnityEngine.Mathf.Min(best, PointSegment(corner, a, b));
    return best;
}
var buildingRoadOverlaps = new System.Collections.Generic.List<object>();
foreach (var model in models)
{
    var r = UnityEngine.Rect.MinMaxRect(model.bounds.min.x, model.bounds.min.z, model.bounds.max.x, model.bounds.max.z);
    foreach (var road in CoastalCityLayout.Roads)
    {
        float distance = float.MaxValue; int segment = -1;
        for (int i = 1; i < road.points.Length; i++)
        {
            var a = road.points[i - 1]; var b = road.points[i];
            float d = SegmentRect(new UnityEngine.Vector2(a.x,a.z), new UnityEngine.Vector2(b.x,b.z), r);
            if (d < distance) { distance = d; segment = i - 1; }
        }
        if (distance >= road.halfWidth * road.halfWidth) continue;
        buildingRoadOverlaps.Add(new { model.stopId, road = road.id, roadName = road.name, model = Path(model.root),
            bounds = Box(model.bounds), closestSegment = segment, roadEdgeIntrusion = road.halfWidth - UnityEngine.Mathf.Sqrt(distance), broadPhaseOnly = true });
    }
}
bool SupportedShape(UnityEngine.Collider c) => c is UnityEngine.BoxCollider || c is UnityEngine.SphereCollider ||
    c is UnityEngine.CapsuleCollider || c is UnityEngine.MeshCollider mesh && mesh.convex;
var parcelOverlaps = new System.Collections.Generic.List<object>();
int confirmedParcelOverlaps = 0;
for (int i = 0; i < models.Count; i++) for (int j = i + 1; j < models.Count; j++)
{
    var a = models[i]; var b = models[j];
    var min = UnityEngine.Vector3.Max(a.bounds.min, b.bounds.min); var max = UnityEngine.Vector3.Min(a.bounds.max, b.bounds.max);
    if (max.x - min.x <= .2f || max.z - min.z <= .2f || max.y - min.y <= .2f) continue;
    int tested = 0, overlaps = 0; float maximumDepth = 0; string hitA = null, hitB = null;
    foreach (var ca in a.colliders) foreach (var cb in b.colliders)
    {
        if (!IsSceneCollider(ca) || !IsSceneCollider(cb) || !ca.bounds.Intersects(cb.bounds) || (!SupportedShape(ca) && !SupportedShape(cb))) continue;
        tested++;
        if (!UnityEngine.Physics.ComputePenetration(ca, ca.transform.position, ca.transform.rotation,
            cb, cb.transform.position, cb.transform.rotation, out var direction, out float depth) || depth <= .02f) continue;
        overlaps++; if (depth > maximumDepth) { maximumDepth = depth; hitA = Path(ca.transform); hitB = Path(cb.transform); }
    }
    if (overlaps > 0) confirmedParcelOverlaps++;
    parcelOverlaps.Add(new { first = a.stopId, second = b.stopId, firstModel = Path(a.root), secondModel = Path(b.root),
        overlapXZ = new[] { max.x - min.x, max.z - min.z }, overlapSquareMetres = (max.x - min.x) * (max.z - min.z),
        firstBounds = Box(a.bounds), secondBounds = Box(b.bounds), testedColliderPairs = tested,
        confirmedColliderPairs = overlaps, maximumPenetrationMetres = maximumDepth, colliderA = hitA, colliderB = hitB,
        broadPhaseOnly = overlaps == 0 });
}

var rows = new System.Collections.Generic.List<object>();
var ordered = new System.Collections.Generic.List<(string road, string roadName, string path, string lot, string kind, UnityEngine.Bounds bounds,
    System.Collections.Generic.HashSet<string> channels, int samples, int trafficSamples, float first, float last, UnityEngine.Vector3 firstPoint)>(obstruction.Values);
ordered.Sort((a,b) => b.trafficSamples != a.trafficSamples ? b.trafficSamples.CompareTo(a.trafficSamples) : string.CompareOrdinal(a.path,b.path));
foreach (var row in ordered)
{
    var channels = new System.Collections.Generic.List<string>(row.channels); channels.Sort(System.StringComparer.Ordinal);
    rows.Add(new { road = row.road, roadName = row.roadName, collider = row.path, lot = row.lot, kind = row.kind,
        bounds = Box(row.bounds), channels, sampledHits = row.samples, trafficLaneHits = row.trafficSamples,
        firstAlongMetres = row.first, lastAlongMetres = row.last, firstWorldPoint = V(row.firstPoint) });
}
var liveGraph = town.GetComponentInChildren<WorldLifeAnchor>(true)?.graph;
var assetGraph = UnityEditor.AssetDatabase.LoadAssetAtPath<StreetGraph>(StreetGraph.AssetPath);
return Newtonsoft.Json.JsonConvert.SerializeObject(new {
    ready = true, readOnly = true, scene = scene.path, sceneWasDirty = scene.isDirty,
    roadCount = CoastalCityLayout.Roads.Count, models = models.Count, sampledPositions, queries, saturatedQueries,
    graph = new { liveLayoutId = liveGraph != null ? liveGraph.layoutId : null,
        savedLayoutId = assetGraph != null ? assetGraph.layoutId : null, expectedLayoutId = CoastalCityLayout.LayoutId,
        sameAsset = liveGraph == assetGraph, nodes = liveGraph != null ? liveGraph.NodeCount : 0 },
    summary = new { roadColliderPairs = rows.Count, buildingRoadBoundsPairs = buildingRoadOverlaps.Count,
        parcelBoundsPairs = parcelOverlaps.Count, confirmedParcelOverlaps },
    ignoredHitCounts = ignored, roadObstructions = rows, buildingRoadBoundsOverlaps = buildingRoadOverlaps, parcelModelOverlaps = parcelOverlaps,
    interpretation = "roadObstructions are Physics overlap hits in drivable envelopes, with gate/lamp/ground exclusions. " +
        "Building-road and parcel AABB rows are broad-phase warnings (rotated roofs may overestimate); parcel rows with confirmedColliderPairs > 0 have real collider penetrations. " +
        "Zero saturatedQueries is required for complete collider sampling. Street geometry is never changed by this audit."
}, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore });

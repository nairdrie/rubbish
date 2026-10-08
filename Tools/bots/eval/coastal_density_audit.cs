// READ ONLY: run after a coordinated full build with Neighborhood already open in Edit Mode.
// unity command eval_file --file Tools/bots/eval/coastal_density_audit.cs
// Returns JSON only. Does not open/save scenes, reload layout caches, move objects, sync/step physics or write files.
if (UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Density audit requires Edit Mode after a completed city build.");
var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity");
if (!scene.IsValid() || !scene.isLoaded)
    throw new System.InvalidOperationException("Open the generated Neighborhood before the density audit.");
var town = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "NeighborhoodGen")?.transform;
if (!CoastalCityLayout.IsCoastal(town)) throw new System.InvalidOperationException("Generated coastal town not found.");
var blueprint = Newtonsoft.Json.JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(
    System.IO.File.ReadAllText(CoastalCityLayout.BlueprintPath));
var roads = blueprint.roads.ToDictionary(r => r.id, r => CoastalCityLayout.ResolveRoad(blueprint, r.id), System.StringComparer.Ordinal);
var stops = town.GetComponentsInChildren<PickupStop>(true).OrderBy(s => s.stopId, System.StringComparer.Ordinal).ToArray();
var anchors = town.GetComponentsInChildren<CityRoadAnchor>(true);
var graph = town.GetComponentInChildren<WorldLifeAnchor>(true)?.graph;
var errors = new System.Collections.Generic.List<string>();
var warnings = new System.Collections.Generic.List<string>();
var expected = @"
bb-offshore-106 bb-offshore-107 bb-offshore-20 bb-offshore-21 bb-offshore-48 bb-offshore-49 bb-offshore-76 bb-offshore-77
birch-18 birch-19 birch-27 birch-28 birch-36 birch-39 birch-46 birch-47 birch-56 birch-57 birch-8 birch-9
cc-dining-1 cc-dining-2 cc-high-1 cc-high-2 cc-loading-1 cc-loading-2 cc-market-1 cc-market-2 cc-market-3
depot-9 elm-13 elm-22 elm-23 elm-3 elm-33 elm-43
maple-18 maple-19 maple-26 maple-29 maple-38 maple-39 maple-46 maple-49 maple-56 maple-59 maple-8 maple-9
mm-carpool-10 mm-carpool-22 mm-carpool-42 mm-carpool-54 mm-glutenfree-102 mm-glutenfree-103
mm-juicebox-11 mm-juicebox-210 mm-juicebox-224 mm-juicebox-23 mm-juicebox-240 mm-juicebox-254 mm-juicebox-43 mm-juicebox-55
mm-playdate-102 mm-playdate-103 mm-soccermom-21 mm-soccermom-33 mm-soccermom-43 mm-trampoline-20 mm-trampoline-32 mm-trampoline-42
oak-12 oak-2 oak-22 oak-23 oak-32 oak-42
sh-hedgerow-12 sh-hedgerow-32 sh-hedgerow-48 sh-hedgerow-68 sh-monocle-13 sh-monocle-33 sh-monocle-49 sh-monocle-69
sh-oldmoney-328 sh-oldmoney-329 sh-oldmoney-344 sh-oldmoney-345 sh-pinkyup-29 sh-pinkyup-45
st-skyline-1 st-skyline-2 st-skyline-3 st-skyline-4 st-tower-1 st-tower-2 st-tower-3 st-tower-4
".Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
var expectedSet = new System.Collections.Generic.HashSet<string>(expected, System.StringComparer.Ordinal);
var actualSet = new System.Collections.Generic.HashSet<string>(stops.Select(s => s.stopId), System.StringComparer.Ordinal);
var missing = expectedSet.Except(actualSet).OrderBy(s => s, System.StringComparer.Ordinal).ToArray();
var added = actualSet.Except(expectedSet).OrderBy(s => s, System.StringComparer.Ordinal).ToArray();
var duplicate = stops.GroupBy(s => s.stopId).Where(g => g.Count() != 1).Select(g => g.Key).ToArray();
if (stops.Length != 98 || expectedSet.Count != 98 || missing.Length != 0 || added.Length != 0 || duplicate.Length != 0)
    errors.Add("Stable collection-stop set differs from the 98-stop pre-density baseline.");
if (graph == null || graph.layoutId != CoastalCityLayout.LayoutId) errors.Add("Missing or non-coastal scene graph.");
else if (!expectedSet.SetEquals(graph.houses.Select(h => h.stopId))) errors.Add("Baked graph does not preserve the same 98 household identities.");

float Flat(UnityEngine.Vector3 a, UnityEngine.Vector3 b) => UnityEngine.Vector2.Distance(new UnityEngine.Vector2(a.x,a.z), new UnityEngine.Vector2(b.x,b.z));
float[] V(UnityEngine.Vector3 p) => new[] { p.x, p.y, p.z };
object Field(UnityEngine.Component c, string name) => c.GetType().GetField(name)?.GetValue(c);
var components = town.GetComponentsInChildren<UnityEngine.Component>(true).Where(c => c != null).ToArray();
var infill = components.Where(c => c.GetType().Name == "CoastalInfillSite").ToArray();
var yards = components.Where(c => c.GetType().Name == "CoastalYardBoundary").ToArray();
var yardIds = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
foreach (var yard in yards)
    foreach (string id in (Field(yard,"stopIds") as string[]) ?? System.Array.Empty<string>()) yardIds.Add(id);

// The exact pre-repair seed capacity used by the full builder. Safe frontage may be lower after geometric checks.
var capacities = new System.Collections.Generic.List<object>();
foreach (var district in new[] { Districts.MildlyPleasant, Districts.MinivanMeadows, Districts.SnootingtonHills, Districts.BillionaireBluffs })
{
    int needed = district == Districts.MildlyPleasant ? 37 : district == Districts.MinivanMeadows ? 22 : district == Districts.SnootingtonHills ? 14 : 8;
    var strips = blueprint.lotStrips.Where(s => s.districtId == district).ToArray();
    int seedSlots = 0, candidateEstimate = 0;
    var details = new System.Collections.Generic.List<object>();
    foreach (var strip in strips)
    {
        var road = roads[strip.roadId]; float frontage = road.Length * (strip.end - strip.start);
        int authoredCount = strip.GetType().GetField("count")?.GetValue(strip) is int explicitCount ? explicitCount : 0;
        int slots = authoredCount > 0 ? authoredCount : UnityEngine.Mathf.Max(1,UnityEngine.Mathf.FloorToInt(frontage / strip.spacing)); seedSlots += slots;
        int candidates = UnityEngine.Mathf.CeilToInt(frontage / 4f) + 1; candidateEstimate += candidates;
        details.Add(new {road=strip.roadId,side=strip.side,frontageMetres=frontage,spacingMetres=strip.spacing,
            authoredCount,capacityMode=authoredCount>0?"explicit count":"spacing",seedSlots=slots});
    }
    if (seedSlots < needed) errors.Add(district + " has " + seedSlots + " seed slots for " + needed + " preserved homes; full build fails before repair.");
    if (candidateEstimate > 512) warnings.Add(district + " has more than 512 authored 4m candidates; current greedy parcel search can truncate later strips.");
    capacities.Add(new {district,needed,seedSlots,spareSeedSlots=seedSlots-needed,authoredRepairCandidateEstimate=candidateEstimate,strips=details});
}

var occupancy = new System.Collections.Generic.List<object>();
foreach (var road in roads.Values.OrderBy(r => r.id, System.StringComparer.Ordinal))
{
    var households = stops.Where(s => s.GetComponentInParent<CityRoadAnchor>()?.roadId == road.id).ToArray();
    var decor = infill.Where(c => c.GetComponent<CityRoadAnchor>()?.roadId == road.id).ToArray();
    var positions = households.Select(s => s.GetComponentInParent<CityRoadAnchor>().u * road.Length)
        .Concat(decor.Select(c => c.GetComponent<CityRoadAnchor>().u * road.Length)).OrderBy(s => s).ToArray();
    float gap = positions.Length == 0 ? road.Length : UnityEngine.Mathf.Max(positions[0],road.Length-positions[positions.Length-1]);
    for (int i=1;i<positions.Length;i++) gap=UnityEngine.Mathf.Max(gap,positions[i]-positions[i-1]);
    occupancy.Add(new {road=road.id,lengthMetres=road.Length,households=households.Length,decorativeSites=decor.Length,
        largestUnpopulatedStationGapMetres=gap,stopIds=households.Select(s=>s.stopId).ToArray(),
        infillIds=decor.Select(c=>Field(c,"siteId") as string).ToArray()});
}
var innerRoads = new[]{"carpool","juicebox","monocle","estate-spur","offshore","mansion-spur"};
var emptyInner = innerRoads.Where(id => roads.ContainsKey(id) && !stops.Any(s=>s.GetComponentInParent<CityRoadAnchor>()?.roadId==id) &&
    !infill.Any(c=>c.GetComponent<CityRoadAnchor>()?.roadId==id)).ToArray();
foreach(string id in emptyInner) warnings.Add("Interior street has no household or decorative frontage: " + id);

// Both crow-flight and approximate directed starter-only lane-route distance. A departing player can choose
// either nearby lane; snapping only to the single closest junction arc can force an artificial block loop.
// These are local apron-to-lane connector estimates, not collision-tested steering paths or timed truck tests.
var depotRows = new System.Collections.Generic.List<object>();
var starter = stops.Where(s => s.District.id == Districts.MildlyPleasant).ToArray();
var search = graph != null && graph.NodeCount > 0 ? new LifeAStar(graph) : null;
int starterDistrict = WorldLifeIds.DistrictIndex(Districts.MildlyPleasant);
int starterMask = 1 << starterDistrict;
const float departureRadius = 8f;
bool AccessibleStarterLane(int node)
{
    var n = graph.nodes[node];
    if (n.kind != StreetGraph.NodeKind.Lane || n.district != starterDistrict) return false;
    if (n.gate != StreetGraph.None && (n.gate >= graph.gates.Length || (starterMask & (1 << graph.GateDistrict(n.gate))) == 0)) return false;
    for (int t = graph.OutBegin(node); t < graph.OutEnd(node); t++)
        if (search.Allowed(graph.OutEdge(t), LifeRouteMode.Car, starterMask) && graph.nodes[graph.OutTo(t)].district == starterDistrict) return true;
    return false;
}
for(int lot=0;lot<blueprint.depot.stations.Length;lot++)
{
    var pose=roads[blueprint.depot.roadId].AtNormalized(blueprint.depot.stations[lot]);
    var door=pose.position+pose.rotation*UnityEngine.Vector3.right*blueprint.depot.lateral;
    var closest=starter.OrderBy(s=>Flat(door,s.transform.position)).FirstOrDefault();
    float routeBest=float.PositiveInfinity; string routeId=null; int selectedStart=-1;
    var departures = new System.Collections.Generic.List<int>();
    if(search != null)
        for(int i=0;i<graph.NodeCount;i++)
        {
            if(!AccessibleStarterLane(i) || Flat(pose.position,graph.nodes[i].pos)>departureRadius)continue;
            var local=UnityEngine.Quaternion.Inverse(pose.rotation)*(graph.nodes[i].pos-pose.position);
            // Do not jump to an unrelated parallel road or across a sidewalk just because it is close.
            if(UnityEngine.Mathf.Abs(local.x)>roads[blueprint.depot.roadId].halfWidth+.25f)continue;
            departures.Add(i);
        }
    departures=departures.OrderBy(i=>Flat(pose.position,graph.nodes[i].pos)).ThenBy(i=>i).Take(32).ToList();
    foreach(var stop in starter.OrderBy(s=>Flat(door,s.transform.position)))
    {
        if(search==null)break;
        if(Flat(door,stop.transform.position)>routeBest+.1f)continue;
        int end=graph.Nearest(stop.transform.position,StreetGraph.KindMask(StreetGraph.NodeKind.Lane),starterDistrict);
        foreach(int start in departures)
        {
            int cost=search.Route(start,end,LifeRouteMode.Car,starterMask,starterDistrict,null,null);
            if(cost<0)continue;
            float metres=cost*.01f+Flat(door,graph.nodes[start].pos)+Flat(stop.transform.position,graph.nodes[end].pos);
            if(metres<routeBest){routeBest=metres;routeId=stop.stopId;selectedStart=start;}
        }
    }
    depotRows.Add(new {lot,door=V(door),nearestStop=closest != null?closest.stopId:null,
        straightLineMetres=closest != null?(float?)Flat(door,closest.transform.position):null,
        shortestDirectedRouteStop=routeId,approximateStarterOnlyDriveMetres=float.IsInfinity(routeBest)?(float?)null:routeBest,
        departureLaneCandidates=departures.Count,departureLaneSearchRadiusMetres=departureRadius,
        selectedDepartureLaneNode=selectedStart>=0?(int?)selectedStart:null,
        selectedDepartureLanePosition=selectedStart>=0?V(graph.nodes[selectedStart].pos):null,
        starterStopsWithin80m=starter.Count(s=>Flat(door,s.transform.position)<=80),starterStopsWithin120m=starter.Count(s=>Flat(door,s.transform.position)<=120)});
    if(closest==null)errors.Add("Depot " + lot + " has no starter household.");
    if(search!=null&&routeId==null)errors.Add("Depot " + lot + " cannot reach a starter household without a permit.");
}

// Compare live lot poses with the file on disk without calling Reload or moving anything.
foreach(var anchor in anchors)
{
    if(!roads.TryGetValue(anchor.roadId,out var road)){errors.Add(anchor.stableId+": missing road");continue;}
    float lateral=anchor.lateralFromRoadEdge?UnityEngine.Mathf.Sign(anchor.lateral)*(road.halfWidth+UnityEngine.Mathf.Abs(anchor.lateral)):anchor.lateral;
    var pose=CityRoadAnchor.Resolve(road,anchor.u,lateral,anchor.height,anchor.yawOffset);
    if(UnityEngine.Vector3.Distance(anchor.transform.position,pose.position)>.05f||UnityEngine.Quaternion.Angle(anchor.transform.rotation,pose.rotation)>.15f)
        errors.Add(anchor.stableId+": scene anchor is stale relative to the blueprint file.");
}
errors.AddRange(CityRoadClearance.Validate(town));
return Newtonsoft.Json.JsonConvert.SerializeObject(new {
    ready=true,readOnly=true,scene=scene.path,sceneWasDirty=scene.isDirty,
    identities=new {expected=98,actual=stops.Length,missing,added,duplicate},
    depotFirstJobs=depotRows,parcelCapacity=capacities,streetOccupancy=occupancy,emptyInteriorStreets=emptyInner,
    yards=new {boundaryGroups=yards.Length,coveredStopCount=yardIds.Count,uncoveredResidential=stops.Where(s=>!s.stopId.StartsWith("cc-")&&!s.stopId.StartsWith("st-")&&!yardIds.Contains(s.stopId)).Select(s=>s.stopId).ToArray()},
    infillSiteCount=infill.Length,errors,warnings,
    interpretation="Driving distances are approximations: shortest directed starter-only lane path from up to 32 permitted departure nodes within 8m of each depot's road entrance, plus straight apron/curb endpoint offsets. This allows a driver to choose their departure lane instead of being snapped onto an arbitrary committed junction turn. Gate masks and starter-district restrictions still apply. Connector offsets are not collision-tested steering manoeuvres or a timed truck test. Street gaps use anchor stations, not visible frontage widths; branch endpoints and intentional parks may remain open. Yard absence is descriptive, not a required fence for every home. Run coastal_physical_audit.cs for detailed collision locations and inspect rendered views."
},Newtonsoft.Json.Formatting.Indented);

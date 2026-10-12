using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static partial class NeighborhoodBuilder
{
    const string CoastalMeshDir = "Assets/Materials/Generated/CoastalCity";

    // Full migration is a one-time use of the original deterministic lot recipes, NOT the tweak workflow.
    static string ApplyCoastalPlan(Ctx c)
    {
        try { TownReliefBaker.PipelineBegin(c.root); return ApplyCoastalPlanFlat(c); }
        finally { TownReliefBaker.PipelineEnd(); }
    }

    static string ApplyCoastalPlanFlat(Ctx c)
    {
        CoastalCityLayout.Reload();
        Districts.RefreshLayoutBounds();
        Group(c.root, CoastalCityLayout.MarkerName);
        c.root.gameObject.AddComponent<CityBlueprintStamp>().structuralFingerprint = CityBlueprintStamp.StructuralFingerprint(CoastalCityLayout.Data);
        var authoredLots = Group(c.root, "CoastalLots");
        var oldGraph = c.graph;
        var residential = CoastalResidentialLayout.ResolveLive();
        ValidateResidentialHouseholds(residential, c.stops);
        var pens = c.root.GetComponentsInChildren<KidPen>(true);
        var anchors = new List<CityRoadAnchor>();
        foreach (string district in new[] { Districts.MildlyPleasant, Districts.MinivanMeadows, Districts.SnootingtonHills, Districts.BillionaireBluffs })
        {
            var lots = c.lots.Where(l => (l.district?.id ?? Districts.MildlyPleasant) == district).OrderBy(l => StopIdFor(l), StringComparer.Ordinal).ToList();
            var sites = CoastalLotSites(district);
            int site = 0;
            foreach (var lot in lots)
            {
                var stop = c.stops.Find(s => s.stopId == StopIdFor(lot));
                var model = (lot.district == null ? c.gLots : lot.district.gLots).Find(lot.name);
                if (stop == null || model == null) throw new InvalidOperationException("Cannot preserve lot " + StopIdFor(lot));
                bool inBlock = residential.TryGetValue(stop.stopId, out var blockPlacement);
                if (!inBlock && site >= sites.Count) throw new InvalidOperationException("Not enough blueprint lot anchors in " + district + " (need " + lots.Count + ")");
                var placement = inBlock ? new LotSite { road = blockPlacement.roadId, u = blockPlacement.u, side = blockPlacement.side } : sites[site];
                site++;
                var holder = Group(authoredLots, "House_" + stop.stopId);
                holder.SetPositionAndRotation(stop.transform.position, stop.transform.rotation);
                model.SetParent(holder, true);
                stop.transform.SetParent(holder, true);
                // Parked twins are authored under WorldLife, not the lot. Keep their actual look/slot sources.
                foreach (var car in c.householdCars) if (car != null && car.stopId == stop.stopId) car.transform.SetParent(holder, true);
                foreach (var prop in c.knock) if (prop.go != null && prop.stopId == stop.stopId && !prop.go.transform.IsChildOf(holder)) prop.go.transform.SetParent(holder, true);
                foreach (var pen in pens) if (pen != null && pen.stopId == stop.stopId) pen.transform.SetParent(holder, true);
                var info = stop.gameObject.AddComponent<CoastalHouseRoute>();
                int oldIndex = oldGraph != null ? oldGraph.HouseIndex(stop.stopId) : -1;
                if (oldIndex >= 0)
                {
                    var h = oldGraph.houses[oldIndex];
                    info.door = oldGraph.nodes[h.door].pos; info.pathEnd = oldGraph.nodes[h.pathEnd].pos;
                    info.mailbox = h.mailbox; info.car0 = h.carSlot0; info.car1 = h.carSlot1; info.butlerPost = h.butlerPost;
                    info.cars = h.cars; info.size = h.size; info.hasDrive = h.HasDriveway;
                    info.bends = CoastalGardenBends(oldGraph, h.door, h.pathEnd);
                }
                else
                {
                    info.door = lot.f.P(lot.sc + lot.doorA, lot.doorD, .15f);
                    info.pathEnd = lot.f.P(lot.sc + lot.doorA, 8f, .15f);
                    info.mailbox = lot.mailboxPos; info.car0 = lot.carPose0; info.car1 = lot.carPose1;
                    info.butlerPost = lot.butlerPost; info.cars = (byte)lot.cars.Count; info.size = (byte)lot.house.size; info.hasDrive = lot.hasDrive;
                }
                info.ramp = lot.f.P(lot.sc + lot.dwC, 8f, .15f);
                info.top = lot.f.P(lot.sc + lot.dwC, lot.dwEnd, .15f);
                info.buildingRoot = lot.houseGo != null ? lot.houseGo.transform : model;
                var anchor = holder.gameObject.AddComponent<CityRoadAnchor>();
                anchor.stableId = stop.stopId; anchor.roadId = placement.road;
                anchor.u = placement.u; anchor.lateralFromRoadEdge = true; anchor.lateral = placement.side * .8f;
                anchor.height = .15f; anchor.yawOffset = placement.side > 0 ? -90 : 90;
                if (inBlock)
                    CoastalResidentialLayout.Configure(anchor, blockPlacement);
                anchor.Apply(); anchors.Add(anchor);
                foreach (var k in holder.GetComponentsInChildren<Knockable>(true)) k.district = WorldLifeIds.DistrictIndex(district);
                foreach (var car in holder.GetComponentsInChildren<HouseholdCar>(true)) car.district = WorldLifeIds.DistrictIndex(district);
                foreach (var pen in holder.GetComponentsInChildren<KidPen>(true)) pen.district = WorldLifeIds.DistrictIndex(district);
            }
        }
        // Retain the authored park/playground. World-coordinate kid anchors move with its visible fence.
        Vector3 parkDelta = CoastalCityLayout.Landmark("pickle-park") - new Vector3(80, 0, 115);
        c.gPark.position += parkDelta;
        foreach (var pen in pens) if (pen != null && string.IsNullOrEmpty(pen.stopId))
        {
            pen.transform.position += parkDelta; pen.center += parkDelta;
            for (int i = 0; i < pen.spots.Length; i++) pen.spots[i] += parkDelta;
            pen.transform.SetParent(c.gPark, true);
            pen.district = WorldLifeIds.DistrictIndex(Districts.MildlyPleasant);
        }
        FitCoastalParkEntrances(c);
        // Everything below is derived geometry, safe to replace without touching authored houses or player work.
        foreach (var group in new[] { c.gGround, c.gMarkings, c.gLots, c.gStreet, c.gDepot, c.gNature, c.gBounds, c.gDistricts, c.gWorldLife })
            if (group != null) Object.DestroyImmediate(group.gameObject);
        c.gGround = Group(c.root, "Ground"); c.gMarkings = Group(c.root, "Markings");
        c.gStreet = Group(c.root, "StreetFurniture"); c.gNature = Group(c.root, "Nature");
        c.gBounds = Group(c.root, "Bounds"); c.gDistricts = Group(c.root, "Districts");
        c.gWorldLife = Group(c.root, "WorldLife"); c.gates.Clear();
        BuildCoastalGround(c); BuildCoastalRoads(c, null); BuildCoastalCollections(c);
        // Hand-painted objects live outside NeighborhoodGen and survive its replacement. Their houses are
        // first-class collection stops in every downstream bake, not scenery silently omitted on rebuild.
        var handAuthored = TownAuthoringScope.AuthoredRoot(c.root);
        if (handAuthored != null)
        {
            foreach (var authoredAnchor in handAuthored.GetComponentsInChildren<CityRoadAnchor>(true))
                if (!authoredAnchor.Apply()) throw new InvalidOperationException("Cannot resolve authored property " + authoredAnchor.stableId);
            c.stops.AddRange(handAuthored.GetComponentsInChildren<PickupStop>(true));
        }
        BuildCoastalGates(c); BuildCoastalSpawns(c);
        // Only a full recipe build may search for room around newly arranged junctions. Incremental editing
        // preserves chosen attachments and rejects collisions instead of silently reassigning anyone's lot.
        int oldFences = CoastalYardAuthoring.RemoveLegacyBackFences(c.root);
        // TownLife (parcel fences, park, planting): cleared before any yard planning sees its colliders.
        TownLifeBuilder.PipelineClear(c.root);
        string placementReport = CoastalParcelPlacement.RepairWithFixedLots(c.root, new HashSet<string>(residential.Keys));
        placementReport += "[Coastal yards] Replaced " + oldFences + " old random fence pieces.\n" + CoastalYardAuthoring.Rebuild(c.root) + "\n";
        placementReport += BuildCoastalInfill(c) + "\n";
        BuildCoastalScenery(c);
        // After yards, infill and the scenery trees it must avoid; it adds no Knockables.
        placementReport += TownLifeBuilder.PipelineBuild(c.root) + "\n";
        BuildAnchor(c);
        c.graph = CoastalCityGraphBuilder.Build(c.root, c.stops);
        c.anchor.graph = c.graph;
        if (oldGraph != null && !AssetDatabase.Contains(oldGraph)) Object.DestroyImmediate(oldGraph);
        Physics.SyncTransforms();
        var errors = CoastalCityValidation.Validate(c.root, c.graph);
        errors.AddRange(TownGameplayAuthoring.ValidateAuthored(c.root));
        if (errors.Count > 0) throw new InvalidOperationException("Coastal city validation: " + string.Join("\n", errors) + "\n" + placementReport);
        placementReport += TownReliefBaker.PipelineApply(c.root, c.graph) + "\n";
        return $"[Coastal City] revision {CoastalCityLayout.Revision}: {CoastalCityLayout.Roads.Count} road groups, {anchors.Count} preserved homes, {c.stops.Count} collection stops, {c.gates.Count} gates.\n" + placementReport + "\n";
    }

    struct LotSite { public string road; public float u; public int side; }

    static void ValidateResidentialHouseholds(Dictionary<string, CoastalResidentialLayout.Placement> placements, IEnumerable<PickupStop> stops)
    {
        var lookup = stops.ToLookup(s => s.stopId, StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            var matches = lookup[placement.Key].ToArray();
            if (matches.Length != 1 || matches[0].District.id != placement.Value.districtId)
                throw new InvalidOperationException("Residential block references missing, duplicate or wrong-district household " + placement.Key);
        }
    }

    // The preserved playground is life-sized, but its old street-to-street path and entrance signs must
    // terminate at today's curb. Moving the whole park alone leaves signs or long path slabs in a new road.
    static void FitCoastalParkEntrances(Ctx c) => FitCoastalParkEntrances(c.gPark,
        CoastalCityLayout.Landmark("pickle-park"), CoastalParkRoutes.Crossings());

    // Explicit inputs keep the same fitting operation testable without reloading/editing the live blueprint.
    // This only adjusts preserved transforms; the caller's full-hierarchy transaction owns rollback.
    static void FitCoastalParkEntrances(Transform parkRoot, Vector3 park, CoastalParkRoutes.Crossing[] crossings)
    {
        if (parkRoot == null) throw new InvalidOperationException("The town is missing its preserved Park group.");
        if (crossings == null || crossings.Length != 2) throw new InvalidOperationException("Pickle Park requires two street entrances.");
        var ends = crossings.Select(x => x.position + (park - x.position).normalized * (x.halfWidth + 3f)).OrderBy(p => p.z).ToArray();
        if ((ends[1] - ends[0]).sqrMagnitude < .01f) throw new InvalidOperationException("Pickle Park entrance paths overlap; move the adjacent road away from the park.");
        var path = parkRoot.Find("ParkPath_NS");
        if (path != null)
        {
            var centre = (ends[0] + ends[1]) * .5f;
            path.position = new Vector3(centre.x, path.position.y, centre.z);
            path.rotation = Quaternion.LookRotation(ends[1] - ends[0]);
            path.localScale = new Vector3(2.5f, path.localScale.y, Vector3.Distance(ends[0], ends[1]));
        }
        var signs = parkRoot.GetComponentsInChildren<Transform>(true).Where(t => t.name == "ParkSign").OrderBy(t => t.position.z).ToArray();
        for (int i = 0; i < signs.Length && i < 2; i++)
        {
            var p = ends[i] + (park - ends[i]).normalized * 3f + Vector3.right * (i == 0 ? -2.65f : 2.65f);
            signs[i].position = new Vector3(p.x, signs[i].position.y, p.z);
        }
        foreach (var t in parkRoot.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal))
            {
                var p = ends[0] + (park - ends[0]).normalized * 3.4f + Vector3.right * 2.75f;
                t.position = new Vector3(p.x, t.position.y, p.z);
            }
    }
    static Vector3[] CoastalGardenBends(StreetGraph graph, int door, int end)
    {
        var previous = new Dictionary<int,int> { [door] = -1 }; var queue = new Queue<int>(); queue.Enqueue(door);
        while(queue.Count > 0)
        {
            int node = queue.Dequeue(); if(node == end) break;
            for(int i=graph.OutBegin(node);i<graph.OutEnd(node);i++)
            {
                var edge=graph.edges[graph.OutEdge(i)]; int next=graph.OutTo(i);
                if(edge.kind!=StreetGraph.EdgeKind.GardenPath||previous.ContainsKey(next))continue;
                previous[next]=node;queue.Enqueue(next);
            }
        }
        var result=new List<Vector3>();if(!previous.ContainsKey(end))return result.ToArray();
        for(int n=previous[end];n>=0&&n!=door;n=previous[n])result.Add(graph.nodes[n].pos);
        result.Reverse();return result.ToArray();
    }
    static List<LotSite> CoastalLotSites(string district)
    {
        var result = new List<LotSite>();
        foreach (var s in CoastalCityLayout.Data.lotStrips.Where(s => s.districtId == district))
        {
            var r = CoastalCityLayout.Road(s.roadId);
            // Explicit frontage quotas ensure inner crescents and the depot-facing block receive homes.
            // Extra strips after the district's quota remain authored repair candidates, not new households.
            int count = s.count > 0 ? s.count : Mathf.Max(1, Mathf.FloorToInt(r.Length * (s.end - s.start) / s.spacing));
            for (int i = 0; i < count; i++)
                result.Add(new LotSite { road = r.id, u = Mathf.Lerp(s.start, s.end, (i + .5f) / count), side = s.side });
        }
        return result;
    }

    static Mesh CoastalMesh(string id, List<Vector3> vertices, List<int> triangles)
    {
        EnsureFolder(CoastalMeshDir);
        string path = CoastalMeshDir + "/" + id + ".asset";
        CityEditTransaction.Current?.WatchAsset(path);
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null) { mesh = new Mesh { name = id }; AssetDatabase.CreateAsset(mesh, path); }
        mesh.Clear(); mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh); return mesh;
    }
    static GameObject CoastalSurface(Ctx c, Transform parent, string id, Mesh mesh, Material mat, bool solid)
    {
        var go = new GameObject(id) { layer = LayerGround }; go.transform.SetParent(parent, false);
        CityEditTransaction.RegisterCreated(go);
        go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        if (solid && mesh.vertexCount > 0 && mesh.GetIndexCount(0) > 0) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return go;
    }
    static Mesh Ribbon(string id, Vector3[] p, float left, float right, float y)
    {
        var v = new List<Vector3>(); var tris = new List<int>();
        for (int i = 0; i < p.Length; i++)
        {
            var tangent = (p[Mathf.Min(i + 1, p.Length - 1)] - p[Mathf.Max(i - 1, 0)]).normalized;
            var side = Vector3.Cross(Vector3.up, tangent);
            var a = p[i] + side * left; var b = p[i] + side * right; a.y = b.y = y; v.Add(a); v.Add(b);
            if (i > 0) { int k = i * 2; tris.AddRange(new[] { k - 2, k, k - 1, k - 1, k, k + 1 }); }
        }
        return CoastalMesh(id, v, tris);
    }
    static float CoastX(Vector3[] coast, float z)
    {
        for (int i = 1; i < coast.Length; i++)
        {
            var a = coast[i - 1]; var b = coast[i];
            if (z >= Mathf.Min(a.z,b.z) && z <= Mathf.Max(a.z,b.z)) return Mathf.Lerp(a.x,b.x,Mathf.InverseLerp(a.z,b.z,z));
        }
        return coast[coast.Length - 1].x;
    }
    static float RiverDistance(Vector3[] river, Vector3 p)
    { float d = float.MaxValue; for (int i = 1; i < river.Length; i++) d = Mathf.Min(d, CoastalCityLayout.DistanceToSegment(p, river[i - 1], river[i])); return d; }
    static void BuildCoastalGround(Ctx c)
    {
        var bounds = CoastalCityLayout.BoundsXZ; var coast = CoastalCityLayout.Coast; var river = CoastalCityLayout.River;
        var v = new List<Vector3>(); var triangles = new List<int>();
        int nx = Mathf.CeilToInt(bounds.width / 8f), nz = Mathf.CeilToInt(bounds.height / 8f);
        for (int z = 0; z <= nz; z++) for (int x = 0; x <= nx; x++)
        {
            var p = new Vector3(Mathf.Lerp(bounds.xMin,bounds.xMax,x/(float)nx), -.08f, Mathf.Lerp(bounds.yMin,bounds.yMax,z/(float)nz));
            float bank = RiverDistance(river,p);
            if (bank < 24) p.y = Mathf.Lerp(-5f,-.08f,Mathf.InverseLerp(15,24,bank));
            float shore = p.x - CoastX(coast,p.z);
            if (shore > -12) p.y = Mathf.Min(p.y,Mathf.Lerp(-.08f,-7f,Mathf.InverseLerp(-12,16,shore)));
            v.Add(p);
            if (x > 0 && z > 0) { int n = z*(nx+1)+x; triangles.AddRange(new[]{n-nx-2,n-1,n-nx-1,n-nx-1,n-1,n}); }
        }
        CoastalSurface(c,c.gGround,"Coastal terrain",CoastalMesh("Coastal_Terrain",v,triangles),c.M("NB_GrassFar"),true);
        CoastalSurface(c,c.gGround,"Bandit River",Ribbon("Coastal_River",river,-19.5f,19.5f,-1.1f),c.M("NB_Water"),false);
        var seaV = new List<Vector3>(); var seaT = new List<int>();
        for(int i=0;i<coast.Length;i++)
        {
            seaV.Add(new Vector3(coast[i].x-7,-1.1f,coast[i].z)); seaV.Add(new Vector3(bounds.xMax+500,-1.1f,coast[i].z));
            if(i>0){int k=i*2;seaT.AddRange(new[]{k-2,k-1,k,k-1,k+1,k});}
        }
        CoastalSurface(c,c.gGround,"Eastern Sea",CoastalMesh("Coastal_Sea",seaV,seaT),c.M("NB_Water"),false);
        CoastalSurface(c,c.gGround,"River walk (pedestrian)",Ribbon("Coastal_RiverWalk",CoastalCityLayout.RiverWalk,-1.3f,1.3f,.015f),c.M("NB_ParkPath"),true);
        for(int lot=0;lot<4;lot++)
        {
            var r=DepotRowLayout.PadRect(lot);
            Slab(c,c.gGround,"DepotPad_"+lot,new Rect2(r.xMin,r.yMin,r.xMax,r.yMax),.025f,c.M("NB_AsphaltLot"));
        }
        // Invisible outer safety perimeter remains far behind the visible coastline/wooded ridge.
        foreach(var pair in new[]{(new Vector3(bounds.center.x,8,bounds.yMin),new Vector3(bounds.width,20,2)),(new Vector3(bounds.center.x,8,bounds.yMax),new Vector3(bounds.width,20,2)),(new Vector3(bounds.xMin,8,bounds.center.y),new Vector3(2,20,bounds.height)),(new Vector3(bounds.xMax,8,bounds.center.y),new Vector3(2,20,bounds.height))})
        {
            var g=new GameObject("City limit"){layer=LayerWalls};g.transform.SetParent(c.gBounds,false);g.transform.position=pair.Item1;g.AddComponent<BoxCollider>().size=pair.Item2;
        }
    }

    static int BuildCoastalRoads(Ctx c, HashSet<string> only)
    {
        var group = c.root.Find("CoastalRoads") ?? Group(c.root,"CoastalRoads");
        string surfaceFingerprint = CityGeneratedRoad.SurfaceFingerprint(CoastalCityLayout.Roads);
        Dictionary<string, CoastalRoadSurfaceBuilder.RoadSurface> surfaces = null;
        int refreshed = 0;
        foreach(var r in CoastalCityLayout.Roads)
        {
            var previous = group.Find(r.id);
            var existingStamp = previous != null ? previous.GetComponent<CityGeneratedRoad>() : null;
            bool rebuildFixtures = only == null || only.Contains(r.id) || existingStamp == null;
            if (!rebuildFixtures && existingStamp.surfaceFingerprint == surfaceFingerprint) continue;
            if (surfaces == null) surfaces = CoastalRoadSurfaceBuilder.Build(CoastalCityLayout.Roads).ToDictionary(s => s.roadId, StringComparer.Ordinal);
            Transform root;
            CityGeneratedRoad stamp;
            if (rebuildFixtures)
            {
                if (previous != null) CityEditTransaction.DestroyGenerated(previous.gameObject);
                root = Group(group, r.id); stamp = root.gameObject.AddComponent<CityGeneratedRoad>();
                stamp.roadId = r.id; stamp.fingerprint = r.Fingerprint;
            }
            else { root = previous; stamp = existingStamp; }
            foreach (string child in new[] { "Road_" + r.id, "Sidewalk_-1", "Sidewalk_1" })
                if (root.Find(child) is Transform oldSurface) CityEditTransaction.DestroyGenerated(oldSurface.gameObject);
            var surface = surfaces[r.id];
            CoastalSurface(c, root, "Road_" + r.id, CoastalMesh("Road_" + r.id, surface.asphalt.vertices, surface.asphalt.triangles), c.M("NB_Asphalt"), true);
            CoastalSurface(c, root, "Sidewalk_-1", CoastalMesh("Walk_" + r.id + "_-1", surface.leftWalk.vertices, surface.leftWalk.triangles), c.M("NB_Concrete"), true);
            CoastalSurface(c, root, "Sidewalk_1", CoastalMesh("Walk_" + r.id + "_1", surface.rightWalk.vertices, surface.rightWalk.triangles), c.M("NB_Concrete"), true);
            stamp.surfaceFingerprint = surfaceFingerprint;
            refreshed++;
            // A new neighbouring branch must interrupt the old main road's painted centre line too.
            foreach (var dash in root.Cast<Transform>().Where(t => t.name == "Centre dash").ToArray())
                CityEditTransaction.DestroyGenerated(dash.gameObject);
            if (!r.service) for (float s = 8; s < r.Length - 8; s += 9)
            {
                var pose = r.At(s);
                if (CoastalCityLayout.Roads.Any(other => other.id != r.id && NearPolyline(other.points, pose.position) < other.halfWidth + 5)) continue;
                CityEditTransaction.RegisterCreated(OBox(c, root, "Centre dash", pose.position + Vector3.up * .027f, pose.rotation, new Vector3(.15f, .012f, 3.5f), c.M("NB_LineYellow"), LayerDefault, false));
            }
            if (!rebuildFixtures)
            {
                // Only generated lamps newly swallowed by a joining carriageway are removed. Untouched
                // poles retain their existing identity; hand-placed scenery is never handled here.
                foreach (var lamp in root.GetComponentsInChildren<Knockable>(true).Where(k => k.kind == KnockKind.Lamp).ToArray())
                    if (CoastalCityLayout.Roads.Any(other => other.id != r.id && NearPolyline(other.points, lamp.transform.position) < other.halfWidth + .65f))
                        CityEditTransaction.DestroyGenerated(lamp.gameObject);
                continue;
            }
            if(r.bridge)
            {
                // Guardrails only over the water, leaving both bridge approaches and permit sidewalks open.
                var river=CoastalCityLayout.River;
                for(float s=0;s<r.Length;s+=5f)
                {
                    var pose=r.At(s);if(RiverDistance(river,pose.position)>29f)continue;
                    foreach(int side in new[]{-1,1})
                    {
                        var p=pose.position+pose.rotation*Vector3.right*(r.halfWidth+2.7f)*side;
                        OBox(c,root,"Bridge parapet",p+Vector3.up*.58f,pose.rotation,new Vector3(.38f,1.16f,5.05f),c.M("NB_Stone"),LayerWalls,true);
                        OBox(c,root,"Bridge coping",p+Vector3.up*1.2f,pose.rotation,new Vector3(.5f,.12f,5.08f),c.M("NB_White"),LayerDefault,false);
                    }
                }
            }
            if(!r.service&&!r.bridge)for(float s=18;s<r.Length-16;s+=42)
            {
                var pose=r.At(s);
                if(CoastalCityLayout.Roads.Any(other=>other.id!=r.id&&NearPolyline(other.points,pose.position)<other.halfWidth+12))continue;
                foreach(int side in new[]{-1,1})
                {
                    var p=pose.position+pose.rotation*Vector3.right*(r.halfWidth+2.4f)*side;
                    var lamp=Place(c,Sub+"Props/SM_streetlamp_01",root,p,pose.rotation,1,false,LayerDefault);
                    if(lamp!=null)KnockPrefab(c,lamp,KnockKind.Lamp,"",ColMode.Pole);
                }
            }
        }
        BuildCoastalCrossings(c);
        return refreshed;
    }

    static void BuildCoastalCrossings(Ctx c)
    {
        var previous=c.root.Find("CoastalCrossings");
        if(previous!=null)CityEditTransaction.DestroyGenerated(previous.gameObject);
        var group=Group(c.root,"CoastalCrossings");
        // The same park-road projection drives zebra paint, NPC paths and crossing-guard spawn points.
        foreach(var crossing in CoastalParkRoutes.Crossings())
        {
            var rotation=Quaternion.LookRotation(crossing.forward,Vector3.up);
            for(float x=-crossing.halfWidth+.7f;x<crossing.halfWidth-.2f;x+=1.4f)
                OBox(c,group,"Zebra_"+crossing.roadId,crossing.position+rotation*new Vector3(x,.039f,0),rotation,new Vector3(.8f,.016f,4),c.M("NB_LineWhite"),LayerDefault,false);
        }
    }
    static float NearPolyline(Vector3[] points,Vector3 p)
    {float best=float.MaxValue;for(int i=1;i<points.Length;i++)best=Mathf.Min(best,CoastalCityLayout.DistanceToSegment(p,points[i-1],points[i]));return best;}

    static void BuildCoastalGates(Ctx c)
    {
        var old=c.root.Find("CoastalGates");if(old!=null)CityEditTransaction.DestroyGenerated(old.gameObject);
        var parent=Group(c.root,"CoastalGates");var seen=new HashSet<string>();
        foreach(var g in CoastalCityLayout.Gates)
        {
            var district=Districts.Get(g.districtId);
            var spec=new DistrictSpec{id=g.districtId,tier=district.tier,root=parent,gatePos=g.position,gateYaw=Quaternion.LookRotation(g.forward).eulerAngles.y};
            BuildGate(c,spec);var gate=c.gates[c.gates.Count-1];gate.name=g.id;gate.secondaryEntrance=!seen.Add(g.districtId);
            var definition=Array.Find(CoastalCityLayout.Data.gates,x=>x.id==g.id);
            var anchor=gate.gameObject.AddComponent<CityRoadAnchor>();anchor.stableId="gate/"+g.id;anchor.roadId=definition.roadId;anchor.u=definition.u;anchor.yawOffset=definition.forward<0?180:0;
            // Style is inherited; all names derive from the actual district rather than the old style's neighbourhood.
            foreach(var label in gate.GetComponentsInChildren<TMP_Text>(true))
                if(label.text.Contains("MINIVAN MEADOWS")||label.text.Contains("SNOOTINGTON")||label.text.Contains("BILLIONAIRE"))
                    label.text=district.name.ToUpperInvariant()+"\n<size=60%>COLLECTION PERMIT</size>";
        }
    }

    static void BuildCoastalCollections(Ctx c)
    {
        var parent=c.root.Find("CoastalLots");
        foreach(var site in CoastalCityLayout.Data.collectionSites)
        {
            var road=CoastalCityLayout.Road(site.roadId);var pose=road.AtNormalized(site.u);
            var rot=pose.rotation*Yaw(site.side>0?-90:90);
            var curb=pose.position+pose.rotation*Vector3.right*site.side*(road.halfWidth+.8f)+Vector3.up*.15f;
            var root=Group(parent,"House_"+site.id);root.SetPositionAndRotation(curb,rot);
            var anchor=root.gameObject.AddComponent<CityRoadAnchor>();anchor.stableId=site.id;anchor.roadId=site.roadId;anchor.u=site.u;anchor.lateralFromRoadEdge=true;anchor.lateral=site.side*.8f;anchor.height=.15f;anchor.yawOffset=site.side>0?-90:90;
            var model=Group(root,site.type=="tower"?"Condo":"Shopfront");
            Vector3 At(float x,float y,float z)=>root.TransformPoint(new Vector3(x,y,z));
            uint variation=(uint)PickupVisuals.StableHash(site.id);
            Material wall=c.M(site.type=="tower"?new[]{"NB_WallIvory","NB_WallSage","NB_WallSky"}[variation%3]:site.type=="restaurant"?"NB_WallPeach":new[]{"NB_WallMint","NB_WallLemon","NB_WallLilac"}[variation%3]);
            float width=site.type=="tower"?21:25, depth=site.type=="tower"?20:15;
            int floors=site.type=="tower"?8+(int)((uint)PickupVisuals.StableHash(site.id)%7):2;
            float height=floors*3.1f;
            OBox(c,model,"Building body",At(0,height*.5f,-depth*.5f-10),rot,new Vector3(width,height,depth),wall,LayerWalls,true);
            OBox(c,model,"Stone plinth",At(0,.55f,-depth*.5f-9.8f),rot,new Vector3(width+.4f,1.1f,depth+.4f),c.M("NB_Stone"),LayerDefault,false);
            OBox(c,model,"Roof coping",At(0,height+.16f,-depth*.5f-10),rot,new Vector3(width+.7f,.4f,depth+.7f),c.M("NB_White"),LayerDefault,false);
            OBox(c,model,"Recessed roof",At(0,height+.38f,-depth*.5f-10),rot,new Vector3(width-1.2f,.08f,depth-1.2f),c.M("NB_DoorNavy"),LayerDefault,false);
            OBox(c,model,"Roof plant room",At(width*.22f,height+1.25f,-depth*.55f-10),rot,new Vector3(4,1.8f,5),c.M("NB_Metal"),LayerDefault,false);
            for(int f=0;f<floors;f++)for(float x=-width*.5f+2;x<width*.5f-1;x+=3.3f)
            {
                if(f==0&&Mathf.Abs(x)<3)continue;
                OBox(c,model,"Window",At(x,1.6f+f*3.1f,-9.95f),rot,new Vector3(2.05f,1.8f,.12f),c.M("NB_Glass"),LayerDefault,false);
                if(site.type=="tower"&&f>0&&f%2==0)OBox(c,model,"Balcony",At(x,.8f+f*3.1f,-9.3f),rot,new Vector3(2.4f,.2f,1.5f),c.M("NB_White"),LayerDefault,false);
                OBox(c,model,"Rear window",At(x,1.6f+f*3.1f,-10-depth-.06f),rot,new Vector3(2.05f,1.8f,.12f),c.M("NB_Glass"),LayerDefault,false);
            }
            for(int f=0;f<floors;f++)for(float z=-12;z>-depth-9;z-=3.3f)
                foreach(int side in new[]{-1,1})
                    OBox(c,model,"Side window",At(side*(width*.5f+.06f),1.6f+f*3.1f,z),rot,new Vector3(.12f,1.8f,2.05f),c.M("NB_Glass"),LayerDefault,false);
            if(site.type=="tower")for(int f=1;f<floors;f+=3)
                OBox(c,model,"Floor band",At(0,f*3.1f,-depth*.5f-10),rot,new Vector3(width+.24f,.16f,depth+.24f),c.M("NB_Stone"),LayerDefault,false);
            OBox(c,model,"Entry",At(0,1.5f,-9.88f),rot,new Vector3(2.5f,3,.16f),c.M("NB_DoorNavy"),LayerDefault,false);
            OBox(c,model,"Entry path",At(0,-.06f,-4.9f),rot,new Vector3(3,.16f,10),c.M("NB_ParkPath"),LayerGround,true);
            if(site.type!="tower")
            {
                OBox(c,model,"Awning",At(0,3.3f,-8.5f),rot,new Vector3(width-1,.3f,3.1f),c.M(site.type=="restaurant"?"NB_Ribbon":"NB_TrampBlue"),LayerDefault,false);
                SignBoard(c,model,"Shop sign",At(0,4.5f,-9.7f),rot,new Vector3(width-2,1.2f,.2f),c.M("NB_SignGreen"),site.type=="restaurant"?"LAST BITE · TAKEAWAY":"COUPON COMMONS · LOCAL GOODS",Color.white,false);
            }
            else SignBoard(c,model,"Tower sign",At(0,4,-9.7f),rot,new Vector3(10,1,.2f),c.M("NB_DoorNavy"),"STACKINGTON\nRESIDENCES",Color.white,false);
            var bin=Place(c,Sub+"Props/SM_garbagecan_01",root,At(-3,.02f,-.35f),rot*Yaw(180),1.35f,false,LayerDefault);
            var go=new GameObject("Stop_"+site.id);go.transform.SetParent(root,false);var stop=go.AddComponent<PickupStop>();
            stop.stopId=site.id;stop.address=site.name;stop.districtId=site.districtId;stop.wealth=site.type=="tower"?.7f:.52f;stop.bagCount=6;stop.onRoute=true;stop.bin=bin!=null?bin.transform:null;c.stops.Add(stop);
            var data=go.AddComponent<CoastalHouseRoute>();data.door=At(0,0,-9.5f);data.pathEnd=At(0,0,-2);data.mailbox=At(2,0,-2);data.buildingRoot=model;data.size=(byte)(site.type=="tower"?2:1);
            if(bin!=null)KnockPrefab(c,bin,KnockKind.WheelieBin,site.id,ColMode.Box);
        }
    }

    static void BuildCoastalScenery(Ctx c)
    {
        var random=new System.Random(c.seed^92713);var bounds=CoastalCityLayout.BoundsXZ;
        // Forested edges and wooded hills sit outside road and lot envelopes.
        var lots=c.root.Find("CoastalLots").GetComponentsInChildren<Renderer>(true);
        var footprints=new List<Bounds>();foreach(var r in lots)if(r.name.StartsWith("SM_house")||r.name=="Building body")footprints.Add(r.bounds);
        var painted = TownAuthoringScope.AuthoredRoot(c.root);
        if (painted != null) foreach (var renderer in painted.GetComponentsInChildren<Renderer>(true)) footprints.Add(renderer.bounds);
        foreach(var site in c.root.GetComponentsInChildren<CoastalInfillSite>(true)) footprints.Add(site.WorldBounds);
        foreach(var yard in c.root.GetComponentsInChildren<CoastalYardBoundary>(true))
            foreach(var renderer in yard.GetComponentsInChildren<Renderer>(true)) footprints.Add(renderer.bounds);
        // The Pickle Park block and anything TownLife already built are designed space, not random woodland.
        footprints.AddRange(TownLifeBuilder.SceneryExclusions(c.root));
        for(int i=0;i<550;i++)
        {
            float x=Mathf.Lerp(bounds.xMin+12,bounds.xMax-160,(float)random.NextDouble()),z=Mathf.Lerp(bounds.yMin+20,bounds.yMax-15,(float)random.NextDouble());
            var p=new Vector3(x,0,z);
            bool fringe=x<bounds.xMin+65||z>bounds.yMax-75;
            if(!fringe&&random.NextDouble()<.74)continue;
            if(NearPolyline(CoastalCityLayout.River,p)<32||x>CoastX(CoastalCityLayout.Coast,z)-20||CoastalCityLayout.DistanceToRoad(p)<15)continue;
            if(footprints.Any(b=>Mathf.Abs(b.center.x-x)<b.extents.x+14&&Mathf.Abs(b.center.z-z)<b.extents.z+14))continue;
            Tree(c,c.gNature,"SM_tree_town_03",p,.9f+(float)random.NextDouble()*.9f);
        }
        // The centre is not the hill's southern edge. Reserve the actual mesh half-depth so a full build
        // cannot put a new hillside through a backyard that incremental validation would later reject.
        float backdropNorth = CoastalBackdropNorthStart(bounds, footprints);
        // Rolling backdrop, clear of all drivable roads and the parcel layer.
        for(int i=0;i<12;i++)
        {
            var p=new Vector3(Mathf.Lerp(bounds.xMin+35,CoastX(CoastalCityLayout.Coast,bounds.yMax)-45,i/11f),-14,backdropNorth+(i%3)*12);
            var hill=OPrim(c,c.gNature,PrimitiveType.Sphere,"Wooded ridgeline",p,Quaternion.identity,new Vector3(180,65+i%4*9,95),c.M("NB_Berm"),false);
            // A nonuniform SphereCollider stays spherical at the largest axis: it used to create an invisible
            // barrier across the northern road. The static mesh collider follows the visible ellipsoid exactly.
            hill.AddComponent<MeshCollider>().sharedMesh=hill.GetComponent<MeshFilter>().sharedMesh;
        }
        FinishKnockables(c);
    }
    static float CoastalBackdropNorthStart(Rect townBounds, IEnumerable<Bounds> footprints)
    {
        float north = townBounds.yMax;
        foreach (var footprint in footprints) north = Mathf.Max(north, footprint.max.z);
        return north + 47.5f + 12f;
    }
    static void BuildCoastalSpawns(Ctx c)
    {
        if (c.gSpawns == null) return;
        foreach(Transform spawn in c.gSpawns)
        {
            bool truck=spawn.name.IndexOf("Truck",StringComparison.OrdinalIgnoreCase)>=0;
            var pose=truck?DepotRowLayout.StreetTruck(0):DepotRowLayout.StreetSpawn(0);
            spawn.SetPositionAndRotation(pose.position,pose.rotation);
        }
    }

    static void LoadCoastalMaterials(Ctx c)
    {
        // A road adjustment must retain the artist's materials, not reset their colours to recipe defaults.
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { MatDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) c.mats[System.IO.Path.GetFileNameWithoutExtension(path)] = material;
        }
    }

    static void RefreshCoastalDepotPads(Ctx c)
    {
        if (c.gGround == null) throw new InvalidOperationException("Coastal ground group is missing.");
        for (int lot = 0; lot < DepotRowLayout.LotCount; lot++)
        {
            string name = "DepotPad_" + lot;
            var old = c.gGround.Find(name);
            if (old != null) CityEditTransaction.DestroyGenerated(old.gameObject);
            var r = DepotRowLayout.PadRect(lot);
            var pad = Slab(c, c.gGround, name, new Rect2(r.xMin, r.yMin, r.xMax, r.yMax), .025f, c.M("NB_AsphaltLot"));
            CityEditTransaction.RegisterCreated(pad);
        }
    }

    static void FinishChangedCoastalKnockables(Ctx c)
    {
        // Incremental road furniture must not restart IDs at zero or renumber untouched businesses' props.
        var used = new HashSet<ushort>();
        foreach (var k in TownAuthoringScope.Components<Knockable>(c.root)) used.Add(k.id);
        int next = 0;
        foreach (var record in c.knock)
        {
            if (record.go == null) continue;
            while (next <= ushort.MaxValue && used.Contains((ushort)next)) next++;
            if (next > ushort.MaxValue) throw new InvalidOperationException("City has no free knockable IDs.");
            var k = record.go.GetComponent<Knockable>() ?? record.go.AddComponent<Knockable>();
            k.id = (ushort)next++; used.Add(k.id);
            k.kind = record.kind; k.proxyKey = record.proxyKey; k.stopId = record.stopId ?? "";
            k.district = DistrictByte(record.go.transform.position);
            k.restColliders = record.go.GetComponentsInChildren<Collider>(true).Where(x => x.enabled && !x.isTrigger && !(x is MeshCollider)).ToArray();
            k.renderers = record.go.GetComponentsInChildren<Renderer>(true);
        }
    }

    /// <summary>Update changed generated road groups and their dependent anchors. Never regenerates homes/interiors.</summary>
    public static string ApplyCoastalChanges(string scenePath = null, bool allowDirtyDraft = false)
        => ApplyCoastalChangesInternal(scenePath, allowDirtyDraft, false);

    /// <summary>Explicitly reapply the residential block recipe. Ordinary map publishing preserves hand edits.</summary>
    public static string ApplyResidentialBlocks() => ApplyCoastalChangesInternal(null, false, true);

    static string ApplyCoastalChangesInternal(string scenePath, bool allowDirtyDraft, bool applyResidentialBlocks)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before editing the city.");
        scenePath = scenePath ?? DefaultScenePath;
        if (scenePath != DefaultScenePath) throw new ArgumentException("Incremental city publishing targets the canonical Neighborhood scene only.");
        var scene = SceneManager.GetSceneByPath(scenePath);
        var depot = SceneManager.GetSceneByPath(DepotRowLayout.ScenePath);
        if ((!allowDirtyDraft && scene.isLoaded && scene.isDirty) || depot.isLoaded && depot.isDirty)
            throw new InvalidOperationException("Save or revert unsaved Neighborhood/DepotRow changes before applying the city blueprint.");
        bool opened = !scene.isLoaded, openedDepot = !depot.isLoaded;
        var previousActive = SceneManager.GetActiveScene();
        Ctx c = null;
        try
        {
            if (opened) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            if (openedDepot) depot = EditorSceneManager.OpenScene(DepotRowLayout.ScenePath, OpenSceneMode.Additive);
            var root = FindRoot(scene, RootName)?.transform;
            if (!CoastalCityLayout.IsCoastal(root)) throw new InvalidOperationException("First build the coastal city with Rubbish/Setup/Neighborhood.");
            CoastalCityLayout.Reload(); Districts.RefreshLayoutBounds();
            var preflight = CoastalCityValidation.ValidateBlueprintTopology(CoastalCityLayout.Data);
            if (preflight.Count > 0) throw new InvalidOperationException("Invalid city blueprint:\n" + string.Join("\n", preflight));
            var stamp = root.GetComponent<CityBlueprintStamp>();
            if (stamp == null || !stamp.Matches(CoastalCityLayout.Data))
                throw new InvalidOperationException("Terrain, scale, landmarks or collection recipes changed. This broader edit needs Rubbish/Setup/Neighborhood; road-only apply was cancelled before changing anything.");
            var changed = new HashSet<string>();
            var existing = root.GetComponentsInChildren<CityGeneratedRoad>(true).ToDictionary(r => r.roadId, StringComparer.Ordinal);
            foreach (var road in CoastalCityLayout.Roads)
                if (!existing.TryGetValue(road.id, out var old) || old.fingerprint != road.Fingerprint) changed.Add(road.id);
            c = new Ctx { scene = scene, root = root, seed = DefaultSeed, rng = new System.Random(DefaultSeed) };
            c.gGround = root.Find("Ground"); c.gBounds = root.Find("Bounds"); c.gNature = root.Find("Nature");
            c.gStreet = root.Find("StreetFurniture"); c.gDistricts = root.Find("Districts"); c.gWorldLife = root.Find("WorldLife"); c.gSpawns = root.Find("Spawns");
            c.gPark = root.Find("Park");
            if (c.gPark == null) throw new InvalidOperationException("The town is missing its preserved Park group.");
            c.anchor = root.GetComponentInChildren<WorldLifeAnchor>(true);
            if (c.anchor == null) throw new InvalidOperationException("The town is missing its WorldLifeAnchor.");
            c.stops.AddRange(TownAuthoringScope.Components<PickupStop>(root)); LoadCoastalMaterials(c);
            var anchors = TownAuthoringScope.Components<CityRoadAnchor>(root);
            var residential = applyResidentialBlocks ? CoastalResidentialLayout.ResolveLive() : null;
            var residentialRoads = new HashSet<string>(changed, StringComparer.Ordinal);
            if (residential != null)
            {
                ValidateResidentialHouseholds(residential, c.stops);
                foreach (var placement in residential)
                    if (anchors.Count(a => a.stableId == placement.Key && a.GetComponentInChildren<PickupStop>(true) != null) != 1)
                        throw new InvalidOperationException("Residential block requires exactly one property anchor for " + placement.Key);
                foreach (var anchor in anchors)
                    if (residential.TryGetValue(anchor.stableId, out var placement) &&
                        (anchor.roadId != placement.roadId || Mathf.Abs(anchor.u - placement.u) > .000001f ||
                         !anchor.lateralFromRoadEdge || Mathf.Abs(anchor.lateral - placement.side * .8f) > .000001f ||
                         Mathf.Abs(anchor.height - .15f) > .000001f || Mathf.Abs(anchor.yawOffset - (placement.side > 0 ? -90f : 90f)) > .000001f))
                    { residentialRoads.Add(anchor.roadId); residentialRoads.Add(placement.roadId); }
            }
            // Validate references before mutating any scene objects.
            foreach (var anchor in anchors)
            {
                var road = CoastalCityLayout.Road(anchor.roadId);
                if (road == null) throw new InvalidOperationException("Cannot remove road " + anchor.roadId + ": " + anchor.stableId + " still depends on it.");
                anchor.Resolve(road);
            }
            using (var transaction = new CityEditTransaction(allowDirtyDraft, scene, depot))
            {
                // Terrain relief: every step below lays out a FLAT town; the relief is put back as the last step.
                TownReliefBaker.PipelineBegin(root);
                if (residential != null)
                    foreach (var anchor in anchors)
                        if (residential.TryGetValue(anchor.stableId, out var placement)) CoastalResidentialLayout.Configure(anchor, placement);
                foreach (var anchor in anchors)
                    if (!anchor.Apply()) throw new InvalidOperationException("Could not move " + anchor.stableId);
                int refreshedRoadSurfaces = BuildCoastalRoads(c, changed); BuildCoastalGates(c); FinishChangedCoastalKnockables(c);
                // Zebra paint and navigation follow changed roads; keep their preserved signs/path at the
                // same curb projection too. This is inside the transaction, never an untracked preflight edit.
                FitCoastalParkEntrances(c);
                // TownLife is regenerated on every publish: clear it before the yard planner and the infill
                // conflict test look at colliders, rebuild it once yards, infill and depot pads are final.
                TownLifeBuilder.PipelineClear(root);
                if (residential != null) ClearCoastalInfill(c, residentialRoads);
                CoastalYardAuthoring.Rebuild(root);
                // Explicit neighborhood replanning also refreshes recipe-owned decorative parcels. Full
                // generation already omits parcels that conflict with households/roads; use the same rule
                // here, inside Undo, rather than leaving old woodland/courtyard props across a new court.
                string infillReport = residential != null ? BuildCoastalInfill(c, residentialRoads) : "";
                if (residential != null) CoastalYardAuthoring.Rebuild(root);
                foreach (var old in existing.Values)
                    if (CoastalCityLayout.Road(old.roadId) == null) CityEditTransaction.DestroyGenerated(old.gameObject);
                RefreshCoastalDepotPads(c); BuildCoastalSpawns(c);
                foreach (var depotRoot in depot.GetRootGameObjects())
                    foreach (var lot in depotRoot.GetComponentsInChildren<DepotLot>(true)) lot.ApplyStreetLayout();
                string townLifeReport = TownLifeBuilder.PipelineBuild(root);
                Physics.SyncTransforms();
                c.graph = CoastalCityGraphBuilder.Build(root, c.stops);
                var errors = CoastalCityValidation.Validate(root, c.graph);
                errors.AddRange(TownGameplayAuthoring.ValidateAuthored(root));
                if (errors.Count > 0) throw new InvalidOperationException("City changes need correction before saving:\n" + string.Join("\n", errors));

                // The flat town is valid: shape the land and lay the town (and its navigation graph) onto it.
                transaction.WatchAsset(TownReliefBaker.MeshPath); transaction.WatchAsset(TownReliefBaker.TerrainPath); transaction.WatchAsset(TownRelief.AssetPath);
                string reliefReport = TownReliefBaker.PipelineApply(root, c.graph);
                Setup_WorldLifeKnock.BakeScene(scene, out _);

                // Validation succeeded. Protect every published output before any disk writer is allowed to run;
                // a map/import/save error below rolls back both open scenes AND already-written derived assets.
                transaction.WatchAsset(StreetGraph.AssetPath); transaction.WatchAsset(HouseDirectory.AssetPath);
                transaction.WatchAsset(TownMapData.AssetPath); transaction.WatchAsset(TownMapData.TexturePath);
                transaction.WatchAsset(TownWorldSignatureBuilder.AssetPath);
                c.anchor.graph = SaveGraphAsset(c.graph, StreetGraph.AssetPath);
                Setup_Pickup.WriteHouseDirectory(c.stops, scene.name);
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.MarkSceneDirty(depot);
                if (!EditorSceneManager.SaveScene(scene) || !EditorSceneManager.SaveScene(depot)) throw new InvalidOperationException("City scene save failed.");
                var report = new StringBuilder();
                if (!Setup_TownMap.Bake(report)) throw new InvalidOperationException("Town map bake failed:\n" + report);
                transaction.SaveWatchedAssets();
                TownWorldSignatureBuilder.Publish();
                transaction.Commit();
                return "Updated " + changed.Count + " road layouts and dependent lots; refreshed " + refreshedRoadSurfaces + " connected road surfaces, depots, navigation, pickup directory and map.\n" + infillReport + report + "\n" + reliefReport + "\n" + townLifeReport;
            }
        }
        finally
        {
            TownReliefBaker.PipelineEnd();
            if (c?.graph != null && !AssetDatabase.Contains(c.graph)) Object.DestroyImmediate(c.graph);
            if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
            if (openedDepot && depot.IsValid() && depot.isLoaded) EditorSceneManager.CloseScene(depot, true);
            if (opened && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
        }
    }
}

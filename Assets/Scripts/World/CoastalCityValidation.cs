using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Read-only city authoring checks. All relationships come from blueprint identities or scene anchors,
/// never yesterday's road coordinates. Safe to run before applying an edit or baking navigation.</summary>
public static class CoastalCityValidation
{
    public static List<string> Validate(Transform town, StreetGraph graph)
    {
        var errors = new List<string>();
        try { errors.AddRange(ValidateBlueprintTopology(CoastalCityLayout.Data)); }
        catch (Exception e) { errors.Add("Blueprint could not load: " + e.Message); return errors; }
        if (town == null) { errors.Add("Open the Neighborhood scene and locate NeighborhoodGen before validating scene placements."); return errors; }
        if (!CoastalCityLayout.IsCoastal(town)) errors.Add("Town is not marked as the coastal layout. Apply the city layout before validating it.");
        else
        {
            errors.AddRange(ValidateGeneratedRoads(town));
            errors.AddRange(CityRoadClearance.Validate(town));
            errors.AddRange(CoastalInfillSite.Validate(town));
            errors.AddRange(CoastalYardBoundary.Validate(town));
        }

        var anchorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in TownAuthoringScope.Components<CityRoadAnchor>(town))
        {
            string label = string.IsNullOrEmpty(anchor.stableId) ? anchor.name : anchor.stableId;
            if (string.IsNullOrEmpty(anchor.stableId) || !anchorIds.Add(anchor.stableId)) errors.Add("Missing/duplicate road-anchor identity: " + label);
            var road = CoastalCityLayout.Road(anchor.roadId);
            if (road == null) { errors.Add(label + ": unknown road " + anchor.roadId); continue; }
            if (!Finite(anchor.u) || anchor.u < 0 || anchor.u > 1 || !Finite(anchor.lateral) || !Finite(anchor.height) || !Finite(anchor.yawOffset))
            { errors.Add(label + ": invalid road-anchor offsets"); continue; }
            try
            {
                var expected = anchor.Resolve(road);
                if (!Finite(anchor.transform.position) || Vector3.Distance(anchor.transform.position, expected.position) > .05f ||
                    !Finite(anchor.transform.rotation) || Quaternion.Angle(anchor.transform.rotation, expected.rotation) > .15f)
                    errors.Add(label + ": placement differs from its road anchor; Apply Blueprint Changes to reconcile it.");
            }
            catch (ArgumentException e) { errors.Add(label + ": " + e.Message); }
        }

        var stopIds = new HashSet<string>(StringComparer.Ordinal);
        var stops = TownAuthoringScope.Components<PickupStop>(town);
        foreach (var stop in stops)
        {
            if (string.IsNullOrEmpty(stop.stopId) || !stopIds.Add(stop.stopId)) errors.Add("Missing/duplicate collection stopId: " + stop.stopId);
            if (stop.GetComponentInParent<CityRoadAnchor>() == null) errors.Add(stop.stopId + ": collection lot has no CityRoadAnchor");
            if (!Finite(stop.transform.position)) errors.Add(stop.stopId + ": non-finite collection position");
            else if (CoastalCityLayout.DistanceToRoad(stop.transform.position) > 15f)
                errors.Add(stop.stopId + ": curb lies more than 15 m from a road edge (including service roads)");
            var route = stop.GetComponent<CoastalHouseRoute>();
            if (route == null) { errors.Add(stop.stopId + ": missing CoastalHouseRoute metadata"); continue; }
            if (route.buildingRoot == null) errors.Add(stop.stopId + ": household building anchor is missing");
            if (!Finite(route.door) || !Finite(route.pathEnd) || !Finite(route.mailbox) || !Finite(route.ramp) || !Finite(route.top) ||
                !Finite(route.car0) || !Finite(route.car1) || !Finite(route.butlerPost))
                errors.Add(stop.stopId + ": non-finite household route metadata");
            foreach (var bend in route.bends ?? Array.Empty<Vector3>())
                if (!Finite(bend)) { errors.Add(stop.stopId + ": non-finite household path bend"); break; }
        }
        foreach (var gate in town.GetComponentsInChildren<DistrictGate>(true))
            if (gate.GetComponentInParent<CityRoadAnchor>() == null) errors.Add("Gate " + gate.districtId + ": missing CityRoadAnchor");

        if (graph == null) errors.Add("No baked StreetGraph assigned to this town.");
        else
        {
            if (graph.layoutId != CoastalCityLayout.LayoutId) errors.Add("StreetGraph layout differs from the city blueprint.");
            try { graph.Validate(errors); }
            catch (Exception e) { errors.Add("StreetGraph validation failed: " + e.Message); }
            var graphStops = new HashSet<string>(StringComparer.Ordinal);
            foreach (var house in graph.houses ?? Array.Empty<StreetGraph.House>())
            {
                if (!graphStops.Add(house.stopId ?? "")) errors.Add("StreetGraph duplicates stopId " + house.stopId);
                if (!stopIds.Contains(house.stopId ?? "")) errors.Add("StreetGraph still references removed stop " + house.stopId);
            }
            foreach (string stop in stopIds) if (!graphStops.Contains(stop)) errors.Add("StreetGraph has no household for " + stop);
        }
        return errors;
    }

    /// <summary>Read-only generated-road inventory and revision check, including roads with no lot anchors.
    /// An optional resolved blueprint view supports previews/tests without mutating the live layout cache.</summary>
    public static List<string> ValidateGeneratedRoads(Transform town, IReadOnlyList<CoastalCityLayout.RoadDef> expectedRoads = null)
    {
        var errors = new List<string>();
        if (!CoastalCityLayout.IsCoastal(town)) return errors;
        expectedRoads = expectedRoads ?? CoastalCityLayout.Roads;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var road in expectedRoads) expected.Add(road.id, road.Fingerprint);
        string surfaceFingerprint = CityGeneratedRoad.SurfaceFingerprint(expectedRoads);
        bool staleSurfaces = false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generated in town.GetComponentsInChildren<CityGeneratedRoad>(true))
        {
            string id = generated.roadId ?? "";
            if (!expected.TryGetValue(id, out string fingerprint))
            {
                errors.Add("Unexpected generated road '" + (id.Length == 0 ? "<empty>" : id) + "' at " + generated.name +
                    "; reconcile removed or renamed roads with Apply Blueprint Changes.");
                continue;
            }
            if (!found.Add(id)) errors.Add("Duplicate generated road '" + id + "'; keep exactly one generated group per blueprint road.");
            staleSurfaces |= generated.surfaceFingerprint != surfaceFingerprint;
            if (!string.Equals(generated.fingerprint, fingerprint, StringComparison.Ordinal))
                errors.Add("Outdated generated road '" + id + "'; its geometry or road metadata differs from the blueprint. Apply Blueprint Changes.");
        }
        // Blueprint order makes diagnostics stable even when the hierarchy was rearranged by an author.
        foreach (var road in expectedRoads)
            if (!found.Contains(road.id)) errors.Add("Missing generated road '" + road.id + "'; Apply Blueprint Changes to generate it.");
        if (staleSurfaces) errors.Add("Outdated connected road surfaces; junction sidewalks depend on adjoining roads. Apply Blueprint Changes.");
        return errors;
    }

    /// <summary>Pure reference/topology checks. Crossings are shared junction IDs; a visible overlap of two roads is
    /// not enough for durable authoring. Bridges must connect to land roads at both ends and span the river.</summary>
    public static List<string> ValidateBlueprintTopology(CoastalCityLayout.Blueprint blueprint)
    {
        List<string> errors;
        try { errors = CoastalCityLayout.ValidateBlueprint(blueprint); }
        catch (Exception e) { return new List<string> { "Malformed blueprint: " + e.Message }; }
        if (errors.Count > 0) return errors;
        var nodes = new Dictionary<string, CoastalCityLayout.NodeSpec>(StringComparer.Ordinal);
        foreach (var node in blueprint.nodes) nodes.Add(node.id, node);
        var knownDistricts = new HashSet<string>(StringComparer.Ordinal) {
            Districts.MildlyPleasant, Districts.MinivanMeadows, Districts.CouponCommons,
            Districts.SnootingtonHills, Districts.StackingtonHeights, Districts.BillionaireBluffs,
        };
        foreach (var district in blueprint.districts)
        {
            if (!knownDistricts.Remove(district.id)) errors.Add("Unknown district identity " + district.id);
            foreach (float number in district.bounds) if (!Finite(number)) errors.Add(district.id + ": non-finite district bounds");
        }
        foreach (string missing in knownDistricts) errors.Add("Missing district identity " + missing);
        foreach (float number in blueprint.bounds) if (!Finite(number)) errors.Add("Non-finite map bounds");
        var landmarks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var landmark in blueprint.landmarks ?? Array.Empty<CoastalCityLayout.LandmarkSpec>())
            if (landmark == null || string.IsNullOrEmpty(landmark.id) || !landmarks.Add(landmark.id) || !Finite(landmark.x) || !Finite(landmark.z))
                errors.Add("Invalid or duplicate landmark identity");

        var adjacent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var roadAt = new Dictionary<string, List<CoastalCityLayout.RoadSpec>>(StringComparer.Ordinal);
        CoastalCityLayout.RoadSpec depot = null;
        foreach (var road in blueprint.roads)
        {
            if (road.id == blueprint.depot.roadId) depot = road;
            if (!Finite(road.halfWidth)) errors.Add(road.id + ": non-finite road width");
            foreach (string id in road.nodes)
            {
                if (!adjacent.ContainsKey(id)) adjacent[id] = new HashSet<string>(StringComparer.Ordinal);
                if (!roadAt.TryGetValue(id, out var at)) roadAt[id] = at = new List<CoastalCityLayout.RoadSpec>();
                if (!at.Contains(road)) at.Add(road);
            }
            for (int i = 1; i < road.nodes.Length; i++)
            {
                string a = road.nodes[i - 1], b = road.nodes[i];
                adjacent[a].Add(b); adjacent[b].Add(a);
                if ((PlanPoint(nodes[a]) - PlanPoint(nodes[b])).sqrMagnitude < .000001f)
                    errors.Add(road.id + ": distinct junctions share the same position (zero-length segment)");
            }
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (depot != null) { queue.Enqueue(depot.nodes[0]); visited.Add(depot.nodes[0]); }
        while (queue.Count > 0)
            foreach (string next in adjacent[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
        foreach (var road in blueprint.roads)
            foreach (string node in road.nodes) if (!visited.Contains(node)) { errors.Add("Road " + road.id + " is disconnected from the depot road."); break; }

        foreach (float station in blueprint.depot.stations)
            if (!Finite(station) || station < 0 || station > 1) errors.Add("Depot station must lie within 0..1 of its road.");
        if (!Finite(blueprint.depot.lateral) || !Finite(blueprint.depot.yawOffset)) errors.Add("Non-finite depot placement");
        foreach (var gate in blueprint.gates) if (!Finite(gate.u)) errors.Add("Gate " + gate.id + " has non-finite road position");
        foreach (var road in blueprint.roads)
        {
            if (!road.bridge) continue;
            foreach (string end in new[] { road.nodes[0], road.nodes[road.nodes.Length - 1] })
            {
                bool connected = false;
                foreach (var other in roadAt[end]) if (other != road && !other.bridge) { connected = true; break; }
                if (!connected) errors.Add("Bridge " + road.id + " ends without a connected land road at junction " + end);
            }
            bool crosses = false;
            for (int a = 1; a < road.nodes.Length && !crosses; a++)
                for (int b = 1; b < blueprint.terrain.river.Length; b++)
                    if (SegmentsCross(PlanPoint(nodes[road.nodes[a - 1]]), PlanPoint(nodes[road.nodes[a]]),
                        PlanPoint(nodes[blueprint.terrain.river[b - 1]]), PlanPoint(nodes[blueprint.terrain.river[b]])))
                    { crosses = true; break; }
            if (!crosses) errors.Add("Bridge " + road.id + " no longer spans the river path.");
        }
        return errors;
    }

    static Vector2 PlanPoint(CoastalCityLayout.NodeSpec n) => new Vector2(n.x, n.z);
    static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        Vector2 u = b - a, v = d - c;
        float cross = u.x * v.y - u.y * v.x;
        if (Mathf.Abs(cross) < .000001f) return false;
        Vector2 delta = c - a;
        float t = (delta.x * v.y - delta.y * v.x) / cross;
        float q = (delta.x * u.y - delta.y * u.x) / cross;
        return t >= 0 && t <= 1 && q >= 0 && q <= 1;
    }
    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);
    static bool Finite(Pose p) => Finite(p.position) && Finite(p.rotation);
}

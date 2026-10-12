using System.Collections.Generic;
using UnityEngine;

/// <summary>Checks authored driving envelopes after a layout edit. Live bags, people and closed permit gates are
/// not layout errors; fixed houses, fences and other street furniture across a lane are.</summary>
public static class CityRoadClearance
{
    public static List<string> Validate(Transform town)
    {
        var errors = new List<string>();
        if (town == null) return errors;
        var authoredRoot = TownAuthoringScope.AuthoredRoot(town);
        var seen = new HashSet<string>();
        var hits = new Collider[256];
        foreach (var road in CoastalCityLayout.Roads)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(road.Length / 2.5f));
            float lane = Mathf.Min(StreetGraph.LaneOffset, road.halfWidth * .5f);
            for (int i = 0; i <= count; i++)
            {
                var pose = road.At(road.Length * i / count);
                for (int channel = -1; channel <= 1; channel++)
                {
                    // on the relief the probe rides the road; the surfaces the town stands on are not obstacles
                    var p = TownRelief.Lift(pose.position + pose.rotation * Vector3.right * (channel * lane)) + Vector3.up * 1.8f;
                    int found = Physics.OverlapBoxNonAlloc(p, new Vector3(.85f, 1.5f, 1.2f), hits, pose.rotation, ~0, QueryTriggerInteraction.Ignore);
                    if (found == hits.Length && seen.Add(road.id + "/overflow")) errors.Add(road.name + ": too many colliders to verify road clearance.");
                    for (int h = 0; h < found; h++)
                    {
                        var collider = hits[h];
                        if (!collider || !(collider.transform.IsChildOf(town) || authoredRoot != null && collider.transform.IsChildOf(authoredRoot)) || TownRelief.IsGroundSurface(collider) || TownRelief.TopAboveGround(collider) <= pose.position.y + .3f ||
                            collider.GetComponentInParent<DistrictGate>() != null || collider.attachedRigidbody != null) continue;
                        var anchor = collider.GetComponentInParent<CityRoadAnchor>();
                        string identity = anchor != null ? anchor.stableId : collider.name;
                        if (seen.Add(road.id + "/" + identity))
                            errors.Add(road.name + ": driving lane blocked by " + identity + " (" + collider.name + "). Move its road anchor or adjust the junction before publishing.");
                    }
                }
            }
        }
        errors.AddRange(ValidateBuildings(town));
        return errors;
    }

    /// <summary>Conservative structural world-AABB overlap check. Includes an estate's sibling house wings,
    /// not just its Hall; no route metadata means no parcel to check (e.g. collider-only authoring probes).</summary>
    public static List<string> ValidateBuildings(Transform town)
    {
        var errors = new List<string>();
        if (town == null) return errors;
        var parcels = new List<(string id, Transform root, Bounds bounds)>();
        var seenRoots = new HashSet<Transform>();
        foreach (var route in TownAuthoringScope.Components<CoastalHouseRoute>(town))
        {
            var root = route.buildingRoot;
            if (root == null || !seenRoots.Add(root)) continue;
            var anchor = root.GetComponentInParent<CityRoadAnchor>();
            var structures = new List<Transform> { root };
            if (anchor != null)
                foreach (var child in anchor.GetComponentsInChildren<Transform>(true))
                    if (child != root && (child.name.StartsWith("SM_house", System.StringComparison.Ordinal) || child.name == "Hall"))
                        structures.Add(child);
            bool any = false; Bounds bounds = default;
            var seenRenderers = new HashSet<Renderer>();
            foreach (var structure in structures)
            foreach (var renderer in structure.GetComponentsInChildren<Renderer>(true))
            {
                if (!seenRenderers.Add(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer is ParticleSystemRenderer) continue;
                if (!any) { bounds = renderer.bounds; any = true; } else bounds.Encapsulate(renderer.bounds);
            }
            if (!any) continue;
            var stop = route.GetComponent<PickupStop>();
            string identity = anchor != null ? anchor.stableId : stop != null ? stop.stopId : root.name;
            parcels.Add((identity, root, bounds));
        }
        for (int i = 0; i < parcels.Count; i++) for (int j = i + 1; j < parcels.Count; j++)
        {
            var a = parcels[i]; var b = parcels[j];
            var low = Vector3.Max(a.bounds.min, b.bounds.min); var high = Vector3.Min(a.bounds.max, b.bounds.max);
            if (high.x - low.x <= .15f || high.z - low.z <= .15f || high.y - low.y <= .15f) continue;
            errors.Add("Building footprint overlap: " + a.id + " and " + b.id +
                ". Their conservative structural bounds intersect; move a road anchor or adjust parcel spacing before publishing.");
        }
        return errors;
    }
}

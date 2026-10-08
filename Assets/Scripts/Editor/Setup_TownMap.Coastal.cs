using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static partial class Setup_TownMap
{
    /// <summary>Read-only coastal scan. Geometry and names come from the current blueprint, never old coordinate tables.</summary>
    public static TownMapData ScanCoastal(Transform town, StringBuilder log)
    {
        var data = ScriptableObject.CreateInstance<TownMapData>();
        data.hideFlags = HideFlags.DontSave; data.layoutId = "coastal-v4";
        data.bounds = CoastalCityLayout.BoundsXZ;
        data.pxPerMeter = Mathf.Min(2f, 2048f / Mathf.Max(data.bounds.width, data.bounds.height));
        data.width = Mathf.CeilToInt(data.bounds.width * data.pxPerMeter);
        data.height = Mathf.CeilToInt(data.bounds.height * data.pxPerMeter);
        foreach (var road in CoastalCityLayout.Roads)
        {
            var points = XZ(road.points);
            var bounds = PolyBounds(points, road.halfWidth);
            data.roads.Add(new TownMapData.Road { name = road.name, districtId = road.districtId, rect = bounds,
                axis = 3, points = points, halfWidth = road.halfWidth, exit = road.id == "home" });
            // Separate sidewalk strips keep Unstuck from choosing the road centre through a broad rectangle.
            foreach (int side in new[] { -1, 1 })
            {
                var walk = Offset(points, (road.halfWidth + 2.4f) * side);
                data.areas.Add(new TownMapData.Area { kind = MapAreaKind.Sidewalk, points = walk,
                    halfWidth = 1.2f, rect = PolyBounds(walk, 1.2f) });
            }
            if (road.Length >= 30)
            {
                var pose = road.At(road.Length * .5f); var dir = pose.rotation * Vector3.forward;
                float angle = Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
                if (angle > 90) angle -= 180; if (angle < -90) angle += 180;
                data.labels.Add(new TownMapData.Label { text = road.name, pos = new Vector2(pose.position.x, pose.position.z),
                    angle = angle, size = road.service ? 12 : 17, kind = MapLabelKind.Street });
            }
        }
        foreach (var district in Districts.All)
        {
            var bounds = CoastalCityLayout.DistrictBounds(district.id);
            var row = new TownMapData.District { id = district.id, name = district.name, rect = bounds, labelPos = bounds.center };
            foreach (var gate in CoastalCityLayout.Gates) if (gate.districtId == district.id)
            { row.hasGate = true; row.gate = new Vector2(gate.position.x, gate.position.z); row.gateForward = new Vector2(gate.forward.x, gate.forward.z); break; }
            data.districts.Add(row);
            data.labels.Add(new TownMapData.Label { text = district.name.ToUpperInvariant(), pos = bounds.center,
                size = 24, kind = MapLabelKind.District });
        }
        foreach (var gate in CoastalCityLayout.Gates)
            data.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Gate, id = gate.districtId,
                pos = new Vector2(gate.position.x, gate.position.z), heading = Quaternion.LookRotation(gate.forward).eulerAngles.y });
        for (int i = 0; i < DepotRowLayout.LotCount; i++)
        {
            Vector3 p = DepotRowLayout.DoorPoint(i); string id = "depot_lot_" + i;
            data.pois.Add(new TownMapData.Poi { kind = MapPoiKind.GarageDoor, id = id, pos = new Vector2(p.x, p.z), heading = DepotRowLayout.StreetFacing.eulerAngles.y });
            data.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Depot, id = id, pos = new Vector2(p.x, p.z) });
            data.areas.Add(new TownMapData.Area { kind = MapAreaKind.DepotPad, rect = DepotRowLayout.PadRect(i) });
        }
        var footprints = new Dictionary<string, Rect>();
        if (town != null)
        {
            foreach (var t in TownAuthoringScope.Components<Transform>(town))
                if (t.name.StartsWith("Lot_")) ScanLot(data, t, footprints);
            var stops = new List<PickupStop>(TownAuthoringScope.Components<PickupStop>(town));
            stops.Sort((a, b) => string.CompareOrdinal(a.stopId, b.stopId));
            foreach (var stop in stops)
            {
                var p = stop.transform.position; footprints.TryGetValue(stop.stopId, out var footprint);
                var meta = stop.GetComponent<CoastalHouseRoute>();
                if (meta != null && meta.buildingRoot != null && TryRenderer(meta.buildingRoot, out var authored))
                {
                    footprint = authored;
                    // Existing lots already supplied their roof. New commercial/condo models are metadata-driven.
                    if (!footprints.ContainsKey(stop.stopId)) Area(data, footprint, MapAreaKind.BigHouse);
                }
                data.houses.Add(new TownMapData.House { stopId = stop.stopId, address = stop.address,
                    districtId = stop.District.id, curb = new Vector2(p.x, p.z), footprint = footprint });
            }
            foreach (var exit in town.GetComponentsInChildren<TownExitPortal>(true))
                data.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Exit, id = exit.exitId,
                    pos = new Vector2(exit.transform.position.x, exit.transform.position.z), heading = exit.transform.eulerAngles.y });
            var park = town.Find("Park");
            if (park != null && TryRenderer(park, out var parkBounds))
            {
                Area(data, parkBounds, MapAreaKind.Park);
                data.labels.Add(new TownMapData.Label { text = "PICKLE PARK", kind = MapLabelKind.Park, pos = parkBounds.center, size = 20 });
            }
            data.sourceHash = TownMapData.HashScene(town);
        }
        var river = XZ(CoastalCityLayout.River);
        data.areas.Add(new TownMapData.Area { kind = MapAreaKind.Water, points = river, halfWidth = 23 * CoastalCityLayout.Scale, rect = PolyBounds(river, 23 * CoastalCityLayout.Scale) });
        var coast = new List<Vector2>(XZ(CoastalCityLayout.Coast));
        coast.Add(new Vector2(data.bounds.xMax + 1, coast[coast.Count - 1].y));
        coast.Add(new Vector2(data.bounds.xMax + 1, coast[0].y));
        data.areas.Add(new TownMapData.Area { kind = MapAreaKind.Water, points = coast.ToArray(), rect = PolyBounds(coast.ToArray(), 0) });
        var riverWalk = XZ(CoastalCityLayout.RiverWalk);
        data.areas.Add(new TownMapData.Area { kind = MapAreaKind.ParkPath, points = riverWalk, halfWidth = 1.5f, rect = PolyBounds(riverWalk, 1.5f) });
        log?.AppendLine($"Coastal v4: {data.roads.Count} sampled roads, {data.houses.Count} stable household ids, six districts, four depots");
        return data;
    }

    static Vector2[] XZ(Vector3[] points)
    {
        var result = new Vector2[points.Length]; for (int i = 0; i < points.Length; i++) result[i] = new Vector2(points[i].x, points[i].z); return result;
    }
    static Rect PolyBounds(Vector2[] points, float margin)
    {
        var low = points[0]; var high = low;
        foreach (var p in points) { low = Vector2.Min(low, p); high = Vector2.Max(high, p); }
        return Rect.MinMaxRect(low.x - margin, low.y - margin, high.x + margin, high.y + margin);
    }
    static Vector2[] Offset(Vector2[] points, float distance)
    {
        var result = new Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            var direction = (points[Mathf.Min(i + 1, points.Length - 1)] - points[Mathf.Max(0, i - 1)]).normalized;
            result[i] = points[i] + new Vector2(direction.y, -direction.x) * distance;
        }
        return result;
    }
}

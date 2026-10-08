using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Explicit, identity-preserving residential block recipe. Pure resolution is shared by full generation and
/// an intentional reapply operation; normal street publishing must not reset hand-edited house attachments.
/// Insets and pitch are world metres, not blueprint coordinates. This is editor authoring, never save loading.
/// </summary>
public static class CoastalResidentialLayout
{
    public const string RecipePath = "Assets/Resources/City/ResidentialBlocks.json";
    public const string ResourcePath = "City/ResidentialBlocks";
    public const float MinimumPitch = 18f;
    const int MaximumRows = 512, MaximumHomes = 2000;

    [Serializable] public sealed class Recipe { public int schema; public Row[] rows; public ExplicitHouse[] placements; }
    [Serializable] public sealed class Row
    {
        public string id, districtId, roadId, fromNode, toNode;
        public int side;
        public float firstInset, lastInset;
        public string[] homes;
    }
    /// <summary>Manual curb station for a curved court or irregular parcel. No linear pitch assumption is
    /// valid around a bulb: callers must validate the actual house, driveway and yard footprints before publishing.</summary>
    [Serializable] public sealed class ExplicitHouse
    {
        public string stopId, districtId, roadId;
        public float u;
        public int side;
    }
    public struct Placement { public string roadId, districtId; public float u; public int side; }
    struct Occupancy { public string id, road; public int side; public float first, last; }

    public static Recipe Load()
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(RecipePath) ?? Resources.Load<TextAsset>(ResourcePath);
        if (asset == null) throw Failure("Missing recipe at " + RecipePath + ". Restore it before applying residential rows.");
        try
        {
            var recipe = JsonUtility.FromJson<Recipe>(asset.text);
            if (recipe == null) throw Failure("The recipe contains no object.");
            return recipe;
        }
        catch (ArgumentException e) { throw Failure("Could not parse " + RecipePath + ": " + e.Message); }
    }

    public static Dictionary<string, Placement> ResolveLive() => Resolve(Load(), CoastalCityLayout.Data);

    /// <summary>Returns stable stop-ID attachments without modifying the recipe, blueprint or any scene object.</summary>
    public static Dictionary<string, Placement> Resolve(Recipe recipe, CoastalCityLayout.Blueprint blueprint)
    {
        if (recipe == null || recipe.schema != 1) throw Failure("Expected residential recipe schema 1.");
        int rowCount = recipe.rows?.Length ?? 0, explicitCount = recipe.placements?.Length ?? 0;
        if (rowCount > MaximumRows || rowCount + explicitCount == 0)
            throw Failure("Specify at least one residential row or explicit placement, with at most " + MaximumRows + " rows.");
        if (explicitCount > MaximumHomes)
            throw Failure("Specify at most " + MaximumHomes + " homes across rows and explicit placements.");
        if (blueprint == null || !Finite(blueprint.scale) || blueprint.scale <= 0 ||
            blueprint.nodes == null || blueprint.roads == null || blueprint.districts == null)
            throw Failure("A blueprint with finite positive scale, nodes, roads and districts is required.");

        var nodes = new Dictionary<string, CoastalCityLayout.NodeSpec>(StringComparer.Ordinal);
        foreach (var node in blueprint.nodes)
        {
            if (node == null || !Identity(node.id) || !Finite(node.x) || !Finite(node.z) || nodes.ContainsKey(node.id))
                throw Failure("The blueprint contains an invalid, non-finite or duplicate node.");
            nodes.Add(node.id, node);
        }
        var roads = new Dictionary<string, CoastalCityLayout.RoadSpec>(StringComparer.Ordinal);
        foreach (var road in blueprint.roads)
        {
            if (road == null || !Identity(road.id) || roads.ContainsKey(road.id))
                throw Failure("The blueprint contains an invalid or duplicate road ID.");
            roads.Add(road.id, road);
        }
        var districts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var district in blueprint.districts)
            if (district == null || !Identity(district.id) || !districts.Add(district.id))
                throw Failure("The blueprint contains an invalid or duplicate district ID.");

        var result = new Dictionary<string, Placement>(StringComparer.Ordinal);
        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var occupied = new List<Occupancy>();
        foreach (var row in recipe.rows ?? Array.Empty<Row>())
        {
            if (row == null || !Identity(row.id) || !rowIds.Add(row.id))
                throw Failure("Every row needs a unique, nonempty ID.");
            string context = "Row '" + row.id + "': ";
            if (!Identity(row.roadId) || !roads.TryGetValue(row.roadId, out var spec))
                throw Failure(context + "unknown road '" + row.roadId + "'.");
            if (!Identity(row.districtId) || !districts.Contains(row.districtId) || spec.districtId != row.districtId)
                throw Failure(context + "district must exist and match road '" + row.roadId + "'.");
            if (spec.curve || spec.bridge)
                throw Failure(context + "use a straight, non-bridge road; curved rows need explicit property authoring.");
            if (row.side != -1 && row.side != 1) throw Failure(context + "side must be -1 or 1.");
            if (!Finite(row.firstInset) || !Finite(row.lastInset) || row.firstInset <= 0 || row.lastInset <= 0)
                throw Failure(context + "firstInset and lastInset must be finite positive world-metre clearances.");
            if (row.homes == null || row.homes.Length == 0 || row.homes.Length > MaximumHomes || result.Count + row.homes.Length + explicitCount > MaximumHomes)
                throw Failure(context + "specify at least one home, with at most " + MaximumHomes + " homes in the recipe.");
            if (!Identity(row.fromNode) || !Identity(row.toNode) || !nodes.ContainsKey(row.fromNode) || !nodes.ContainsKey(row.toNode))
                throw Failure(context + "unknown fromNode/toNode; use existing blueprint junction IDs.");
            int segment = -1;
            if (spec.nodes != null)
                for (int i = 0; i + 1 < spec.nodes.Length; i++)
                    if (spec.nodes[i] == row.fromNode && spec.nodes[i + 1] == row.toNode) { segment = i; break; }
            if (segment < 0)
                throw Failure(context + "fromNode and toNode must be consecutive on '" + row.roadId + "' in forward road order; reversed or cross-junction rows are not supported.");

            CoastalCityLayout.RoadDef road;
            try { road = CoastalCityLayout.ResolveRoad(blueprint, row.roadId); }
            catch (ArgumentException e) { throw Failure(context + e.Message); }
            float length = road.Length, before = 0;
            for (int i = 0; i < segment; i++) before += Vector3.Distance(road.points[i], road.points[i + 1]);
            float segmentLength = Vector3.Distance(road.points[segment], road.points[segment + 1]);
            if (!Finite(length) || !Finite(segmentLength) || length <= 0 || segmentLength <= 0)
                throw Failure(context + "road geometry must resolve to finite, nonzero metre lengths.");
            float span = segmentLength - row.firstInset - row.lastInset;
            if (span < 0) throw Failure(context + "insets exceed the " + segmentLength.ToString("0.##") + " m block length; reduce clearances or lengthen the block.");
            float pitch = row.homes.Length > 1 ? span / (row.homes.Length - 1) : 0;
            if (row.homes.Length > 1 && pitch + .0001f < MinimumPitch)
                throw Failure(context + "home pitch is " + pitch.ToString("0.##") + " m, below the " + MinimumPitch + " m minimum; remove a home or lengthen the block.");
            float first = before + row.firstInset + (row.homes.Length == 1 ? span * .5f : 0);
            float last = first + pitch * (row.homes.Length - 1);
            foreach (var old in occupied)
                if (old.road == row.roadId && old.side == row.side &&
                    Mathf.Max(first, old.first) - Mathf.Min(last, old.last) < MinimumPitch - .0001f)
                    throw Failure(context + "overlaps or sits within " + MinimumPitch + " m of row '" + old.id + "' on the same curb; separate their frontage ranges.");
            occupied.Add(new Occupancy { id = row.id, road = row.roadId, side = row.side, first = first, last = last });
            for (int i = 0; i < row.homes.Length; i++)
            {
                string id = row.homes[i];
                if (!Identity(id) || result.ContainsKey(id))
                    throw Failure(context + "home IDs must be nonempty and unique across the recipe; duplicate/invalid stop '" + id + "'.");
                result.Add(id, new Placement { roadId = row.roadId, districtId = row.districtId,
                    u = (first + pitch * i) / length, side = row.side });
            }
        }
        foreach (var house in recipe.placements ?? Array.Empty<ExplicitHouse>())
        {
            if (house == null) throw Failure("Explicit placement must not be null.");
            string context = "Explicit home '" + house.stopId + "': ";
            if (!Identity(house.stopId) || result.ContainsKey(house.stopId))
                throw Failure(context + "home IDs must be nonempty and unique across rows and explicit placements; duplicate/invalid stop.");
            if (!Identity(house.roadId) || !roads.TryGetValue(house.roadId, out var spec))
                throw Failure(context + "unknown road '" + house.roadId + "'.");
            if (!Identity(house.districtId) || !districts.Contains(house.districtId) || spec.districtId != house.districtId)
                throw Failure(context + "district must exist and match road '" + house.roadId + "'.");
            if (spec.bridge) throw Failure(context + "homes cannot be placed on a bridge.");
            if (!Finite(house.u) || house.u < 0 || house.u > 1)
                throw Failure(context + "u must be a finite normalized road station within 0..1.");
            if (house.side != -1 && house.side != 1) throw Failure(context + "side must be -1 or 1.");
            CoastalCityLayout.RoadDef road;
            try { road = CoastalCityLayout.ResolveRoad(blueprint, house.roadId); }
            catch (ArgumentException e) { throw Failure(context + e.Message); }
            if (!Finite(road.Length) || road.Length <= 0)
                throw Failure(context + "road geometry must resolve to a finite, nonzero metre length.");
            result.Add(house.stopId, new Placement { roadId = house.roadId, districtId = house.districtId, u = house.u, side = house.side });
        }
        return result;
    }

    /// <summary>Configure only. The caller owns Undo/transactions and must explicitly Apply the complete lot.</summary>
    public static void Configure(CityRoadAnchor anchor, Placement placement)
    {
        if (anchor == null) throw Failure("Cannot configure a missing house anchor.");
        if (!Identity(placement.roadId) || !Identity(placement.districtId) || !Finite(placement.u) || placement.u < 0 || placement.u > 1 ||
            placement.side != -1 && placement.side != 1) throw Failure("Cannot configure an invalid residential placement.");
        anchor.roadId = placement.roadId; anchor.u = placement.u;
        anchor.lateral = placement.side * .8f; anchor.lateralFromRoadEdge = true;
        anchor.yawOffset = placement.side > 0 ? -90f : 90f; anchor.height = .15f;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    static InvalidOperationException Failure(string message) => new InvalidOperationException("[Residential blocks] " + message);
}

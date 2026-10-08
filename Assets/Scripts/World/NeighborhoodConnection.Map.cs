using System.Collections.Generic;
using UnityEngine;

public sealed partial class NeighborhoodConnection
{
    /// <summary>Copy only: never changes or saves the imported TownMap asset or its original PNG.</summary>
    public static TownMapData WithConnection(TownMapData source, bool renderTexture = true)
    {
        if (source == null) return null;
        if (source.layoutId == "coastal-v4") return source;
        foreach (var road in source.roads) if (road.name == RoadName) return source;
        bool originalGate = false;
        foreach (var district in source.districts)
            if (district.id == Districts.MinivanMeadows && district.hasGate) originalGate = true;
        if (!originalGate) return source;
        var data = Instantiate(source); data.name = source.name + " + Birch Link"; data.hideFlags = HideFlags.DontSave;
        data.roads = new List<TownMapData.Road>(source.roads);
        data.areas = new List<TownMapData.Area>();
        data.labels = new List<TownMapData.Label>(source.labels);
        data.pois = new List<TownMapData.Poi>(source.pois);
        data.roads.Add(new TownMapData.Road { rect = Rect.MinMaxRect(155, 193, 206, 203), name = RoadName, axis = 0, districtId = Districts.MildlyPleasant });
        data.roads.Add(new TownMapData.Road { rect = Rect.MinMaxRect(206, 193, 245, 203), name = RoadName, axis = 0, districtId = Districts.MinivanMeadows });
        data.roads.Add(new TownMapData.Road { rect = WestRoad, name = RoadName, axis = 1, districtId = Districts.MildlyPleasant });
        data.roads.Add(new TownMapData.Road { rect = SouthRoad, name = RoadName, axis = 1, districtId = Districts.MinivanMeadows });
        foreach (var area in source.areas)
        {
            if (area.kind != MapAreaKind.Fence || Mathf.Abs(area.rect.center.x - GatePosition.x) > 1f ||
                area.rect.yMax <= GatePosition.z - GateHalfWidth || area.rect.yMin >= GatePosition.z + GateHalfWidth)
                { data.areas.Add(area); continue; }
            foreach (var piece in OutsideGate(area.rect)) data.areas.Add(new TownMapData.Area { rect = piece, kind = area.kind });
        }
        foreach (var road in Roads) data.areas.Add(new TownMapData.Area { rect = Expand(road, 3f), kind = MapAreaKind.Sidewalk });
        data.labels.Add(new TownMapData.Label { text = RoadName, pos = new Vector2(187, 198), angle = 0, size = 13, kind = MapLabelKind.Street });
        data.pois.Add(new TownMapData.Poi { kind = MapPoiKind.Gate, pos = new Vector2(206, 198), id = Districts.MinivanMeadows, heading = 90 });
        if (renderTexture)
        {
            var texture = new Texture2D(data.width, data.height, TextureFormat.RGBA32, false)
                { name = "Town map + Birch Link", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(MapRaster.Render(data)); texture.Apply(false, true); data.texture = texture;
        }
        return data;
    }

    static IEnumerable<Rect> OutsideGate(Rect source)
    {
        float low = GatePosition.z - GateHalfWidth, high = GatePosition.z + GateHalfWidth;
        if (source.yMin < low) yield return Rect.MinMaxRect(source.xMin, source.yMin, source.xMax, Mathf.Min(low, source.yMax));
        if (source.yMax > high) yield return Rect.MinMaxRect(source.xMin, Mathf.Max(high, source.yMin), source.xMax, source.yMax);
    }
}

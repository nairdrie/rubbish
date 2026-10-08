using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Blueprint-derived navigation/map contracts; no scene rebuild, gameplay, save or network changes.</summary>
public class CoastalCityGraphMapTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Editor(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static object Get(object o, string name) => o.GetType().GetField(name).GetValue(o);
    static void Set(object o, string name, object v) => o.GetType().GetField(name).SetValue(o, v);
    readonly List<Object> owned = new List<Object>();
    [TearDown] public void Cleanup() { foreach (var obj in owned) if (obj != null) Object.DestroyImmediate(obj); owned.Clear(); }

    ScriptableObject Graph()
    {
        var g = (ScriptableObject)Editor("CoastalCityGraphBuilder").GetMethod("Build").Invoke(null,
            new object[] { null, Array.CreateInstance(Runtime("PickupStop"), 0) }); owned.Add(g); return g;
    }
    ScriptableObject Map()
    {
        var map = (ScriptableObject)Editor("Setup_TownMap").GetMethod("ScanCoastal").Invoke(null, new object[] { null, new StringBuilder() });
        owned.Add(map); return map;
    }

    [Test]
    public void SampledCityGraphIsValidDeterministicAndHasSixConnectedStrollLoops()
    {
        var a = Graph(); var b = Graph(); var errors = new List<string>();
        a.GetType().GetMethod("Validate").Invoke(a, new object[] { errors }); Assert.IsEmpty(errors, string.Join("\n", errors));
        Assert.AreEqual("coastal-v4", Get(a, "layoutId"));
        Assert.AreEqual(JsonUtility.ToJson(a), JsonUtility.ToJson(b));
        Assert.AreEqual(6, ((Array)Get(a, "loops")).Length);
        Assert.AreEqual(1, ((Array)Get(a, "exits")).Length, "Only the authored Home Run Road endpoint is a traffic exit");
        var districtIds = new HashSet<byte>(); foreach (var node in (Array)Get(a, "nodes")) districtIds.Add((byte)Get(node, "district"));
        CollectionAssert.AreEquivalent(new byte[] { 0, 1, 2, 3, 4, 5 }, districtIds);
    }

    [Test]
    public void TwoParkCrossingsResolveForGuardsAndParkPathsRemainConnected()
    {
        var graph = Graph(); var nodes = (Array)Get(graph, "nodes"); var edges = (Array)Get(graph, "edges");
        var crossings = (Array)Runtime("CoastalParkRoutes").GetMethod("Crossings").Invoke(null, null);
        Assert.AreEqual(2, crossings.Length);
        var director = Activator.CreateInstance(Runtime("CrossingGuardDirector"));
        var add = director.GetType().GetMethod("AddSite", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var crossing in crossings)
            add.Invoke(director, new object[] { graph, (Vector3)Get(crossing, "position") + Vector3.up * .15f });
        Assert.AreEqual(2, director.GetType().GetProperty("SiteCount").GetValue(director));
        int parks = 0, paths = 0;
        foreach (var node in nodes) if (Get(node, "kind").ToString() == "Park") parks++;
        foreach (var edge in edges) if (Get(edge, "kind").ToString() == "ParkPath") paths++;
        Assert.Greater(parks, 2); Assert.Greater(paths, 2);
        var errors = new List<string>(); graph.GetType().GetMethod("Validate").Invoke(graph, new object[] { errors });
        Assert.IsEmpty(errors, string.Join("\n", errors));
    }

    [Test]
    public void EveryBlueprintGateLocksRoadAndWalkingEdgesUntilItsPermitIsAvailable()
    {
        var graph = Graph(); var gates = (Array)Get(graph, "gates"); var edges = (Array)Get(graph, "edges");
        var gateDefs = Runtime("CoastalCityLayout").GetField("Gates")?.GetValue(null)
            ?? Runtime("CoastalCityLayout").GetProperty("Gates").GetValue(null);
        Assert.AreEqual(((ICollection)gateDefs).Count, gates.Length);
        var roads = new int[gates.Length]; var walks = new int[gates.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            int gate = (int)graph.GetType().GetMethod("GateOf").Invoke(graph, new object[] { i }); if (gate < 0) continue;
            if (Get(edges.GetValue(i), "kind").ToString() == "Road") roads[gate]++; else walks[gate]++;
            Assert.IsFalse((bool)graph.GetType().GetMethod("EdgeOpen").Invoke(graph, new object[] { i, 1 }));
            Assert.IsTrue((bool)graph.GetType().GetMethod("EdgeOpen").Invoke(graph, new object[] { i, -1 }));
        }
        for (int i = 0; i < gates.Length; i++)
        { Assert.GreaterOrEqual(roads[i], 2, "Both traffic directions cross gate " + i); Assert.GreaterOrEqual(walks[i], 2, "Both sidewalks cross gate " + i); }
    }

    [Test]
    public void CoastalMapUsesBlueprintBoundsSixDistrictsAndFourDistinctGaragesWithoutLegacyPatches()
    {
        var map = Map(); Assert.AreEqual("coastal-v4", Get(map, "layoutId"));
        Assert.AreEqual(6, ((IList)Get(map, "districts")).Count);
        var ids = new HashSet<string>();
        foreach (var poi in (IList)Get(map, "pois")) if (Get(poi, "kind").ToString() == "GarageDoor") ids.Add((string)Get(poi, "id"));
        CollectionAssert.AreEquivalent(new[] { "depot_lot_0", "depot_lot_1", "depot_lot_2", "depot_lot_3" }, ids);
        Assert.LessOrEqual((int)Get(map, "width"), 2048); Assert.LessOrEqual((int)Get(map, "height"), 2048);
        var patched = Runtime("NeighborhoodConnection").GetMethod("WithConnection", new[] { Runtime("TownMapData"), typeof(bool) }).Invoke(null, new object[] { map, false });
        Assert.AreSame(map, patched);
        var graph = Graph();
        Assert.AreSame(graph, Runtime("NeighborhoodConnection").GetMethod("WithConnection", new[] { Runtime("StreetGraph") }).Invoke(null, new object[] { graph }));
    }

    [Test]
    public void DiagonalRoadQueryAndRasterDoNotFillItsWholeBoundingRectangle()
    {
        var type = Runtime("TownMapData"); var map = ScriptableObject.CreateInstance(type); owned.Add(map);
        Set(map, "bounds", new Rect(0,0,64,64)); Set(map, "width", 64); Set(map, "height", 64); Set(map, "pxPerMeter", 1f);
        var road = Activator.CreateInstance(type.GetNestedType("Road")); Set(road, "rect", new Rect(2,2,60,60));
        Set(road, "axis", (byte)3); Set(road, "name", "Diagonal"); Set(road, "halfWidth", 2f);
        Set(road, "points", new[] { new Vector2(4,4), new Vector2(60,60) }); ((IList)Get(map, "roads")).Add(road);
        object[] nearest = { new Vector3(4,0,60), Vector3.zero, null };
        Assert.IsTrue((bool)type.GetMethod("TryNearestRoad").Invoke(map, nearest));
        Assert.Greater(Vector3.Distance((Vector3)nearest[1], (Vector3)nearest[0]), 30);
        Assert.IsNull(type.GetMethod("StreetAt").Invoke(map, new object[] { nearest[0], 3f }));
        var pixels = (Color32[])Runtime("MapRaster").GetMethod("Render").Invoke(null, new object[] { map });
        var wild = (Color32)Runtime("MapRaster").GetField("Wild").GetValue(null);
        Assert.AreEqual(wild, pixels[60 * 64 + 4]); Assert.AreNotEqual(wild, pixels[32 * 64 + 32]);
    }

    [Test]
    public void RiverRasterIsUnderTheBridgeAndNotOverItsRoadSurface()
    {
        var type = Runtime("TownMapData"); var map = ScriptableObject.CreateInstance(type); owned.Add(map);
        Set(map, "bounds", new Rect(0,0,64,64)); Set(map, "width", 64); Set(map, "height", 64); Set(map, "pxPerMeter", 1f);
        var road = Activator.CreateInstance(type.GetNestedType("Road")); Set(road, "axis", (byte)3); Set(road, "halfWidth", 5f);
        Set(road, "points", new[] { new Vector2(32,0), new Vector2(32,64) }); ((IList)Get(map, "roads")).Add(road);
        var water = Activator.CreateInstance(type.GetNestedType("Area")); Set(water, "kind", Enum.Parse(Runtime("MapAreaKind"), "Water"));
        Set(water, "halfWidth", 10f); Set(water, "points", new[] { new Vector2(0,32), new Vector2(64,32) }); ((IList)Get(map, "areas")).Add(water);
        var pixels = (Color32[])Runtime("MapRaster").GetMethod("Render").Invoke(null, new object[] { map });
        var blue = (Color32)Runtime("MapRaster").GetField("Water").GetValue(null);
        Assert.AreEqual(blue, pixels[32 * 64 + 10]); Assert.AreNotEqual(blue, pixels[32 * 64 + 32]);
    }
}

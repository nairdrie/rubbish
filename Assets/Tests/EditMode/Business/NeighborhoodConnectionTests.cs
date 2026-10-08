using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Source-only town extension contracts; does not load gameplay or edit any save/scene/map asset.</summary>
public class NeighborhoodConnectionTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Get(object value, string field) => value.GetType().GetField(field).GetValue(value);
    static void Set(object value, string field, object next) => value.GetType().GetField(field).SetValue(value, next);
    readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void TearDown() { foreach (var item in owned) if (item != null) Object.DestroyImmediate(item); owned.Clear(); }

    [Test]
    public void GroundSubdivisionConservesAreaAndDoesNotOverlapOrLoseOutsideTerrain()
    {
        var original = Rect.MinMaxRect(120f, 130f, 270f, 220f);
        var pieces = (List<Rect>)Runtime("NeighborhoodConnection").GetMethod("SplitGround", Static).Invoke(null, new object[] { original });
        float area = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            Assert.Greater(pieces[i].width, 0); Assert.Greater(pieces[i].height, 0);
            Assert.IsTrue(original.Contains(pieces[i].center));
            area += pieces[i].width * pieces[i].height;
            for (int j = i + 1; j < pieces.Count; j++) Assert.IsFalse(pieces[i].Overlaps(pieces[j]));
        }
        Assert.AreEqual(original.width * original.height, area, .01f);
    }

    [Test]
    public void FullRoadAndSidewalkFootprintAvoidsEveryExistingAuthoredHouse()
    {
        var source = (ScriptableObject)Resources.Load("TownMap", Runtime("TownMapData"));
        Assert.NotNull(source);
        if ((string)Get(source, "layoutId") == "coastal-v4")
        {
            Assert.AreSame(source, Runtime("NeighborhoodConnection").GetMethod("WithConnection", Static,
                null, new[] { Runtime("TownMapData"), typeof(bool) }, null).Invoke(null, new object[] { source, false }),
                "The legacy fixed-coordinate footprint must not be inserted into the new town");
            return;
        }
        foreach (string field in new[] { "EastRoad", "WestRoad", "SouthRoad" })
        {
            var road = (Rect)Runtime("NeighborhoodConnection").GetField(field, Static).GetValue(null);
            var footprint = (Rect)Runtime("NeighborhoodConnection").GetMethod("Expand", Static).Invoke(null, new object[] { road, 3f });
            foreach (var house in (IList)Get(source, "houses"))
                Assert.IsFalse(footprint.Overlaps((Rect)Get(house, "footprint")), "Road/sidewalk must not remove or pass through " + Get(house, "stopId"));
        }
    }

    [Test]
    public void MapExtensionIsCopiedConnectedAndKeepsTheExistingDistrictPermit()
    {
        var type = Runtime("TownMapData");
        var source = LegacyMap();
        Assert.NotNull(source);
        int roadsBefore = ((IList)Get(source, "roads")).Count, poisBefore = ((IList)Get(source, "pois")).Count;
        var copy = (ScriptableObject)Runtime("NeighborhoodConnection").GetMethod("WithConnection", Static,
            null, new[] { type, typeof(bool) }, null).Invoke(null, new object[] { source, false });
        Assert.AreNotSame(source, copy); owned.Add(copy);
        Assert.AreEqual(roadsBefore, ((IList)Get(source, "roads")).Count);
        Assert.AreEqual(poisBefore, ((IList)Get(source, "pois")).Count);
        Assert.AreEqual(roadsBefore + 4, ((IList)Get(copy, "roads")).Count);
        Assert.AreSame(Get(source, "texture"), Get(copy, "texture"));
        foreach (var point in new[] { new Vector3(160, 0, 180), new Vector3(180, 0, 198), new Vector3(225, 0, 198), new Vector3(240, 0, 160) })
            Assert.AreEqual("Birch Link", type.GetMethod("StreetAt").Invoke(copy, new object[] { point, 1f }));
        int gates = 0;
        foreach (var poi in (IList)Get(copy, "pois")) if ((string)Get(poi, "id") == "minivan_meadows" && Get(poi, "kind").ToString() == "Gate") gates++;
        Assert.AreEqual(2, gates);
        Assert.AreSame(copy, Runtime("NeighborhoodConnection").GetMethod("WithConnection", Static,
            null, new[] { type, typeof(bool) }, null).Invoke(null, new object[] { copy, false }));
    }

    [Test]
    public void NavigationAddsBothLanesAndFootpathWithoutChangingExistingNodesOrBypassingPermit()
    {
        var type = Runtime("StreetGraph");
        var source = LegacyGraph();
        Assert.NotNull(source);
        var oldNodes = (Array)Get(source, "nodes"); var oldEdges = (Array)Get(source, "edges"); var oldGates = (Array)Get(source, "gates");
        var graph = (ScriptableObject)Runtime("NeighborhoodConnection").GetMethod("WithConnection", Static,
            null, new[] { type }, null).Invoke(null, new object[] { source });
        Assert.AreNotSame(source, graph); owned.Add(graph);
        var nodes = (Array)Get(graph, "nodes"); var edges = (Array)Get(graph, "edges"); var gates = (Array)Get(graph, "gates");
        Assert.AreEqual(oldGates.Length + 1, gates.Length);
        Assert.AreEqual("minivan_meadows", Get(gates.GetValue(gates.Length - 1), "districtId"));
        for (int i = 0; i < oldNodes.Length; i++) Assert.AreEqual(oldNodes.GetValue(i), nodes.GetValue(i));
        for (int i = 0; i < oldEdges.Length; i++) Assert.AreEqual(oldEdges.GetValue(i), edges.GetValue(i));
        Assert.AreSame(oldNodes, Get(source, "nodes")); Assert.AreSame(oldEdges, Get(source, "edges"));
        int crossings = 0, roads = 0, walks = 0;
        for (int i = oldEdges.Length; i < edges.Length; i++)
        {
            var edge = edges.GetValue(i);
            if ((Convert.ToInt32(Get(edge, "flags")) & 4) == 0) continue;
            crossings++;
            if (Get(edge, "kind").ToString() == "Road") roads++; else walks++;
            Assert.IsFalse((bool)type.GetMethod("EdgeOpen").Invoke(graph, new object[] { i, 1 }), "Starter-only NPCs cannot bypass the district permit");
            Assert.IsTrue((bool)type.GetMethod("EdgeOpen").Invoke(graph, new object[] { i, 3 }));
        }
        Assert.AreEqual(3, crossings); Assert.AreEqual(2, roads); Assert.AreEqual(1, walks);
        Assert.AreSame(graph, Runtime("NeighborhoodConnection").GetMethod("WithConnection", Static,
            null, new[] { type }, null).Invoke(null, new object[] { graph }));
    }

    ScriptableObject LegacyMap()
    {
        var type = Runtime("TownMapData"); var data = ScriptableObject.CreateInstance(type); owned.Add(data);
        var texture = new Texture2D(2, 2); owned.Add(texture); Set(data, "texture", texture);
        var district = Activator.CreateInstance(type.GetNestedType("District"));
        Set(district, "id", "minivan_meadows"); Set(district, "hasGate", true);
        ((IList)Get(data, "districts")).Add(district);
        var poi = Activator.CreateInstance(type.GetNestedType("Poi")); Set(poi, "id", "minivan_meadows");
        Set(poi, "kind", Enum.Parse(Runtime("MapPoiKind"), "Gate")); ((IList)Get(data, "pois")).Add(poi);
        return data;
    }

    ScriptableObject LegacyGraph()
    {
        var type = Runtime("StreetGraph"); var data = ScriptableObject.CreateInstance(type); owned.Add(data);
        var nodeType = type.GetNestedType("Node"); var kindType = type.GetNestedType("NodeKind");
        var points = new[] { new Vector3(150,0,167.5f), new Vector3(150,0,172.5f), new Vector3(237.5f,0,140),
            new Vector3(242.5f,0,140), new Vector3(167.4f,.15f,177.4f), new Vector3(232.6f,.15f,140) };
        var nodes = Array.CreateInstance(nodeType, points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            var node = Activator.CreateInstance(nodeType); Set(node, "pos", points[i]); Set(node, "gate", ushort.MaxValue);
            Set(node, "kind", Enum.Parse(kindType, i < 4 ? "Lane" : "Walk")); nodes.SetValue(node, i);
        }
        Set(data, "nodes", nodes); return data;
    }

    GameObject Cube(string name, Transform parent, Vector3 at, Vector3 size, int layer = 6)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.layer = layer;
        go.transform.SetParent(parent, false); go.transform.position = at; go.transform.localScale = size;
        return go;
    }

    [Test]
    public void PhysicalConnectionOpensOnlyThePerimeterAndKeepsExistingGroundAndHouses()
    {
        var town = new GameObject("Connection test town"); owned.Add(town);
        var ground = new GameObject("Ground").transform; ground.SetParent(town.transform, false);
        Cube("Grass_Test", ground, new Vector3(195, -.15f, 175f), new Vector3(150, .5f, 90));
        // Existing asphalt in an intersecting road must stay asphalt, rather than becoming the new sidewalk.
        var road = Cube("Road_Test", ground, new Vector3(160, -.2f, 160), new Vector3(10, .4f, 10));
        var district = new GameObject("Meadows").transform; district.SetParent(town.transform, false);
        var primary = new GameObject("Gate_minivan_meadows"); primary.transform.SetParent(district, false);
        primary.transform.SetPositionAndRotation(new Vector3(206, 0, -6), Quaternion.Euler(0, 90, 0));
        var gate = primary.AddComponent(Runtime("DistrictGate"));
        var barrier = Cube("GateBarrier", primary.transform, primary.transform.position + Vector3.up * 2, Vector3.one, 7);
        barrier.transform.localRotation = Quaternion.identity;
        barrier.transform.localScale = new Vector3(11.2f, 4, .8f);
        Set(gate, "barrierCollider", barrier.GetComponent<BoxCollider>());
        var wall = new GameObject("Wall").transform; wall.SetParent(district, false);
        var fence = Cube("WallCollider", wall, new Vector3(206, 1.6f, 198), new Vector3(.4f, 3.2f, 60), 7);
        var lots = new GameObject("Lots").transform; lots.SetParent(town.transform, false);
        var house = Cube("Do not remove a house", lots, new Vector3(185, 4, 188), new Vector3(10, 8, 10), 7);
        var nature = new GameObject("Nature").transform; nature.SetParent(town.transform, false);
        var tree = Cube("Tree in new road", nature, new Vector3(190, 3, 198), new Vector3(1, 6, 1), 7);
        var ensure = Runtime("NeighborhoodConnection").GetMethod("Ensure", Static, null, new[] { typeof(Transform) }, null);
        var link = (Component)ensure.Invoke(null, new object[] { town.transform });
        Assert.NotNull(link);
        Assert.AreSame(link, ensure.Invoke(null, new object[] { town.transform }));
        Assert.IsTrue(house.activeSelf); Assert.IsFalse(tree.activeSelf);
        Assert.IsFalse(fence.GetComponent<Collider>().enabled);
        Assert.Greater((int)link.GetType().GetProperty("OpenedFenceColliders").GetValue(link), 0);
        var newGate = (Component)link.GetType().GetProperty("Gate").GetValue(link);
        Assert.AreEqual("minivan_meadows", Get(newGate, "districtId"));
        Assert.AreEqual(gate.GetType().GetProperty("RibbonKey").GetValue(gate), newGate.GetType().GetProperty("RibbonKey").GetValue(newGate));
        var newBarrier = (Collider)Get(newGate, "barrierCollider");
        Physics.SyncTransforms();
        foreach (var point in new[] { new Vector3(160, 4, 180), new Vector3(180, 4, 198), new Vector3(225, 4, 198), new Vector3(240, 4, 160) })
        {
            Assert.IsTrue(Physics.Raycast(point, Vector3.down, out var hit, 10f, 1 << 6));
            Assert.AreEqual(0f, hit.point.y, .002f);
        }
        Assert.IsTrue(Physics.Raycast(new Vector3(220, 4, 218), Vector3.down, out var outside, 10f, 1 << 6));
        Assert.AreEqual(.1f, outside.point.y, .002f);
        newBarrier.enabled = true; Physics.SyncTransforms();
        Assert.IsTrue(Physics.Raycast(new Vector3(200, 1, 198), Vector3.right, 12f, 1 << 7));
        Assert.IsFalse(Physics.Raycast(new Vector3(200, 1, 191.145f), Vector3.right, 12f, 1 << 7), "Pedestrian turnstile remains available while locked");
        newBarrier.enabled = false; Physics.SyncTransforms();
        Assert.IsFalse(Physics.Raycast(new Vector3(200, 1, 198), Vector3.right, 12f, 1 << 7));
        Assert.IsTrue(Physics.Raycast(new Vector3(200, 1, 187), Vector3.right, 12f, 1 << 7));
        Assert.IsTrue(Physics.Raycast(new Vector3(200, 1, 209), Vector3.right, 12f, 1 << 7));
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class CoastalYardBoundaryTests
{
    readonly List<GameObject> created = new List<GameObject>();
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Editor(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static object Call(string method, params object[] args) => Runtime("CoastalYardRules").GetMethod(method).Invoke(null, args);
    static Vector3 V(float x, float z) => new Vector3(x, .15f, z);
    GameObject Go(string name, Transform parent = null)
    {
        var go = new GameObject(name); created.Add(go); if (parent != null) go.transform.SetParent(parent, false); return go;
    }
    [TearDown]
    public void TearDown()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    [Test]
    public void SharedDividerIsEmittedOnlyOnceRegardlessOfDirection()
    {
        var a = V(3, 4); var b = V(3, 15);
        var result = (Vector3[])Call("UncoveredSegments", a, b, new[] { b, a });
        Assert.IsEmpty(result);
    }

    [Test]
    public void LongerExistingFenceCoversShorterDivider()
    {
        var result = (Vector3[])Call("UncoveredSegments", V(0, 4), V(0, 9), new[] { V(0, 1), V(0, 12) });
        Assert.IsEmpty(result);
    }

    [Test]
    public void PartialSharedRearBoundaryKeepsBothUncoveredTails()
    {
        var result = (Vector3[])Call("UncoveredSegments", V(0, 0), V(12, 0), new[] { V(3, 0), V(9, 0) });
        CollectionAssert.AreEqual(new[] { V(0, 0), V(3, 0), V(9, 0), V(12, 0) }, result);
    }

    [Test]
    public void CrossingBoundaryDoesNotPretendToCoverFence()
    {
        var result = (Vector3[])Call("UncoveredSegments", V(0, 0), V(12, 0), new[] { V(6, -5), V(6, 5) });
        CollectionAssert.AreEqual(new[] { V(0, 0), V(12, 0) }, result);
        Assert.IsTrue((bool)Call("CrossesBoundary", V(0, 0), V(12, 0), V(6, -5), V(6, 5)));
    }

    [Test]
    public void ConnectedCornersAndDividersAreNotCrossingPlots()
    {
        Assert.IsFalse((bool)Call("CrossesBoundary", V(0, 0), V(12, 0), V(6, -5), V(6, 0)));
        Assert.IsFalse((bool)Call("CrossesBoundary", V(0, 0), V(12, 0), V(12, 0), V(12, 5)));
        Assert.IsFalse((bool)Call("CrossesBoundary", V(0, 0), V(12, 0), V(3, 0), V(9, 0)));
    }

    [Test]
    public void SidewalkDistanceUsesRoadSegmentEndsAndIgnoresElevation()
    {
        Assert.AreEqual(5f, (float)Call("DistanceToSegment", new Vector3(13, 99, 4), V(0, 0), V(10, 0)), .0001f);
        Assert.AreEqual(4f, (float)Call("DistanceToSegment", V(5, 4), V(0, 0), V(10, 0)), .0001f);
        Assert.AreEqual(5f, (float)Call("DistanceToSegment", V(3, 4), V(0, 0), V(0, 0)), .0001f);
    }

    [Test]
    public void PathCorridorDetectsCrossingsAndClearParallelAccess()
    {
        Assert.AreEqual(0, (float)Call("SegmentDistance", V(0, 0), V(10, 0), V(5, -4), V(5, 4)), .0001f);
        Assert.AreEqual(3, (float)Call("SegmentDistance", V(0, 0), V(10, 0), V(0, 3), V(10, 3)), .0001f);
        Assert.AreEqual(5, (float)Call("SegmentDistance", V(0, 0), V(2, 0), V(5, 4), V(8, 4)), .0001f);
    }

    [Test]
    public void BentWalkingPathKeepsDoorToSidewalkOrderWithoutDiagonalShortcuts()
    {
        // A U-shaped garden path. Starting at the sidewalk while keeping door-ordered bends would invent
        // two diagonals: reject a clear middle garden and miss the real vertical path by the door.
        var bends = new[] { V(0, 10), V(10, 10) };
        Assert.AreEqual(4, (float)Call("PathDistance", V(4, 5), V(6, 5), V(0, 0), bends, V(10, 0)), .0001f);
        Assert.AreEqual(0, (float)Call("PathDistance", V(-1, 5), V(1, 5), V(0, 0), bends, V(10, 0)), .0001f);
    }

    [Test]
    public void LegacyRemovalTargetsOnlyOldRandomBackyardPrefab()
    {
        var method = Editor("CoastalYardAuthoring").GetMethod("IsLegacyBackFence");
        Assert.IsTrue((bool)method.Invoke(null, new object[] { Go("SM_garden_fence_long").transform }));
        foreach (var name in new[] { "Picket", "Fence", "EstateWall", "Hedge", "KidPen_playground", "SM_fence_01" })
            Assert.IsFalse((bool)method.Invoke(null, new object[] { Go(name).transform }), name);
    }

    [Test]
    public void EvenLegacyNamedPrefabInsideChildSafetyPenIsPreserved()
    {
        var pen = Go("KidPen_home"); pen.AddComponent(Runtime("KidPen"));
        var child = Go("SM_garden_fence_long", pen.transform);
        Assert.IsFalse((bool)Editor("CoastalYardAuthoring").GetMethod("IsLegacyBackFence").Invoke(null, new object[] { child.transform }));
    }

    [Test]
    public void PreflightLeavesPathsFrontPicketsAndPenColliderMetadataUntouched()
    {
        var town = Go("TestTown");
        string marker = (string)Runtime("CoastalCityLayout").GetField("MarkerName").GetRawConstantValue();
        Go(marker, town.transform);
        var home = Go("House_stable-10", town.transform); home.AddComponent(Runtime("CityRoadAnchor"));
        home.AddComponent(Runtime("CoastalHouseRoute"));
        var old = Go("SM_garden_fence_long", home.transform);
        var picket = Go("Picket", home.transform); picket.AddComponent<BoxCollider>();
        var path = Go("SM_path_garden", home.transform); path.AddComponent<BoxCollider>();
        var pen = Go("KidPen_stable-10", home.transform); pen.AddComponent(Runtime("KidPen"));
        var fence = Go("SM_garden_fence_long", pen.transform); fence.AddComponent<BoxCollider>();
        int removed = (int)Editor("CoastalYardAuthoring").GetMethod("RemoveLegacyBackFences").Invoke(null, new object[] { town.transform });
        Assert.AreEqual(1, removed); Assert.IsTrue(old == null);
        Assert.IsTrue(picket != null && path != null && fence != null);
        Assert.AreEqual(3, home.GetComponentsInChildren<Collider>().Length);
        Assert.NotNull(pen.GetComponent(Runtime("KidPen")));
        Assert.NotNull(home.GetComponent(Runtime("CoastalHouseRoute")));
    }

    [Test]
    public void ShippedSettingsKeepRoadSidewalkAndPedestrianAccessClear()
    {
        var settings = Editor("CoastalYardAuthoring").GetMethod("LoadSettings").Invoke(null, null);
        float Value(string name) => (float)settings.GetType().GetField(name).GetValue(settings);
        Assert.GreaterOrEqual(Value("sidewalkClearance"), 3.1f);
        Assert.GreaterOrEqual(Value("sideGateWidth"), 1.8f);
        Assert.GreaterOrEqual(Value("sideClearance"), 2);
        Assert.GreaterOrEqual(Value("rearClearance"), Value("minimumRearClearance"));
    }
}

using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CoastalTruckRecoveryTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type RoadType => Runtime("CoastalCityLayout+RoadDef");
    static object Road(params Vector3[] points)
    {
        var road = Activator.CreateInstance(RoadType);
        RoadType.GetField("points").SetValue(road, points);
        RoadType.GetField("halfWidth").SetValue(road, 5f);
        return road;
    }
    static bool Project(Vector3 from, float radius, out Vector3 point, params object[] roads)
    {
        var array = Array.CreateInstance(RoadType, roads.Length);
        for (int i = 0; i < roads.Length; i++) array.SetValue(roads[i], i);
        var args = new object[] { from, array, radius, Vector3.zero };
        bool found = (bool)Runtime("TruckRecovery").GetMethod("TryRoadSetDownPoint", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        point = (Vector3)args[3]; return found;
    }
    static void Near(Vector3 a, Vector3 b) => Assert.That(Vector3.Distance(a, b), Is.LessThan(.001f));

    [Test]
    public void TowOnABentStreetProjectsOntoAsphaltNotWholeRoadBoundsCentre()
    {
        var road = Road(Vector3.zero, new Vector3(0, 0, 40), new Vector3(40, 0, 40));
        Assert.IsTrue(Project(new Vector3(20, 2, 10), 60, out var p, road));
        Near(new Vector3(0, 0, 10), p); // AABB centre (20,20) is not on either segment.
    }

    [Test]
    public void MovingARoadMovesItsRecoveryDestinationByTheSameAmount()
    {
        var a = new Vector3(600, 0, 1200); var b = new Vector3(640, 0, 1260);
        var from = new Vector3(633, 4, 1230); var delta = new Vector3(-320, 0, 540);
        Assert.IsTrue(Project(from, 60, out var before, Road(a, b)));
        Assert.IsTrue(Project(from + delta, 60, out var after, Road(a + delta, b + delta)));
        Near(before + delta, after);
    }

    [Test]
    public void ProjectionClampsToEndpointsAndChoosesTheNearestSeparateRoad()
    {
        var far = Road(new Vector3(30, 0, 0), new Vector3(30, 0, 20));
        var near = Road(Vector3.zero, Vector3.forward * 20);
        Assert.IsTrue(Project(new Vector3(3, 1, -6), 60, out var p, far, near));
        Near(Vector3.zero, p);
    }

    [Test]
    public void OutOfReachRoadsDoNotTeleportTheTruckAcrossTheCity()
    {
        var from = new Vector3(200, 2, 200);
        Assert.IsFalse(Project(from, 60, out var p, Road(Vector3.zero, Vector3.forward * 20)));
        Near(from, p);
    }

    [Test]
    public void DegenerateAndNonfiniteSegmentsAreSkippedAndExactRadiusIsAllowed()
    {
        var invalid = Road(Vector3.zero, Vector3.zero, new Vector3(float.NaN, 0, 1));
        Assert.IsTrue(Project(new Vector3(6, 0, 10), 6, out var p, invalid, Road(Vector3.zero, Vector3.forward * 20)));
        Near(new Vector3(0, 0, 10), p);
        Assert.IsFalse(Project(Vector3.zero, 60, out _, invalid));
        Assert.IsFalse(Project(Vector3.zero, float.NaN, out _, Road(Vector3.zero, Vector3.forward)));
        Assert.IsFalse(Project(new Vector3(float.PositiveInfinity, 0, 0), 60, out _, Road(Vector3.zero, Vector3.forward)));
    }
}

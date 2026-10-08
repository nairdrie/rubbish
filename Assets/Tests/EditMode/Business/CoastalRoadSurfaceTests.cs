using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>Pure mesh contracts: no active-scene changes, material creation, bakes or player saves.</summary>
public class CoastalRoadSurfaceTests
{
    static Type RoadType => Type.GetType("CoastalCityLayout+RoadDef, Assembly-CSharp", true);
    static Type Builder => Type.GetType("CoastalRoadSurfaceBuilder, Assembly-CSharp-Editor", true);
    static T Get<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);
    static void Set(object o, string field, object value) => o.GetType().GetField(field).SetValue(o, value);
    static object Road(string id, float width, params float[] coordinates)
    {
        var road = Activator.CreateInstance(RoadType); Set(road, "id", id); Set(road, "halfWidth", width);
        var points = new Vector3[coordinates.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new Vector3(coordinates[i * 2], 0, coordinates[i * 2 + 1]);
        Set(road, "points", points); return road;
    }
    static object[] Build(params object[] roads)
    {
        var array = Array.CreateInstance(RoadType, roads.Length);
        for (int i = 0; i < roads.Length; i++) array.SetValue(roads[i], i);
        return ((IEnumerable)Builder.GetMethod("Build").Invoke(null, new object[] { array })).Cast<object>().ToArray();
    }
    struct Triangle
    {
        public Vector3 a, b, c;
        public Vector2[] XZ => new[] { new Vector2(a.x, a.z), new Vector2(c.x, c.z), new Vector2(b.x, b.z) };
    }
    static Triangle[] Triangles(object[] result, params string[] fields)
    {
        var output = new List<Triangle>();
        foreach (var road in result) foreach (var field in fields)
        {
            var surface = Get<object>(road, field); var vertices = Get<List<Vector3>>(surface, "vertices"); var indices = Get<List<int>>(surface, "triangles");
            Assert.AreEqual(0, indices.Count % 3);
            for (int i = 0; i < indices.Count; i += 3) output.Add(new Triangle { a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]] });
        }
        return output.ToArray();
    }
    static bool Contains(Triangle t, float x, float z)
    {
        var p = new Vector2(x, z); var v = t.XZ;
        return Cross(v[1] - v[0], p - v[0]) >= -.00001 && Cross(v[2] - v[1], p - v[1]) >= -.00001 && Cross(v[0] - v[2], p - v[2]) >= -.00001;
    }
    static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;
    static void Covered(Triangle[] triangles, float x, float z, bool expected = true) => Assert.AreEqual(expected, triangles.Any(t => Contains(t, x, z)), "coverage at " + x + ", " + z);
    static double IntersectionArea(Triangle one, Triangle two)
    {
        var output = one.XZ.ToList(); var clip = two.XZ;
        for (int i = 0; i < 3 && output.Count > 0; i++)
        {
            var input = output; output = new List<Vector2>(); var a = clip[i]; var b = clip[(i + 1) % 3];
            var previous = input[input.Count - 1]; double old = Cross(b - a, previous - a);
            foreach (var point in input)
            {
                double distance = Cross(b - a, point - a);
                if ((distance >= 0) != (old >= 0)) output.Add(previous + (point - previous) * (float)(old / (old - distance)));
                if (distance >= 0) output.Add(point);
                previous = point; old = distance;
            }
        }
        double area = 0; for (int i = 1; i < output.Count - 1; i++) area += Cross(output[i] - output[0], output[i + 1] - output[0]);
        return Math.Abs(area) * .5;
    }
    static void ValidAndDisjoint(object[] result)
    {
        var triangles = Triangles(result, "asphalt", "leftWalk", "rightWalk"); Assert.IsNotEmpty(triangles);
        foreach (var triangle in triangles)
        {
            foreach (var point in new[] { triangle.a, triangle.b, triangle.c }) Assert.IsTrue(float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z));
            Assert.Greater(Vector3.Cross(triangle.b - triangle.a, triangle.c - triangle.a).y, .000001f, "Each collider triangle faces upwards and has positive area.");
        }
        for (int i = 0; i < triangles.Length; i++) for (int j = i + 1; j < triangles.Length; j++)
            Assert.Less(IntersectionArea(triangles[i], triangles[j]), .0001, "Planar surfaces overlap: triangle " + i + " / " + j);
    }

    [Test]
    public void TJunctionHasOpenAsphaltWithPavementOnlyAroundItsOutside()
    {
        var result = Build(Road("main", 5, -30, 0, 0, 0, 30, 0), Road("branch", 5, 0, 0, 0, 30));
        var road = Triangles(result, "asphalt"); var walk = Triangles(result, "leftWalk", "rightWalk");
        foreach (float z in new[] { -4f, 0f, 4f, 6f, 9f, 20f }) { Covered(road, 0, z); Covered(walk, 0, z, false); }
        Covered(walk, 10, 6); Covered(walk, -10, 6); Covered(walk, 0, -6); Covered(walk, 6, 15);
        ValidAndDisjoint(result);
    }

    [Test]
    public void CrossroadsContainNoRaisedSidewalkStripesOrDuplicateAsphaltFaces()
    {
        var result = Build(Road("east-west", 5, -30, 0, 30, 0), Road("north-south", 5, 0, -30, 0, 30));
        var road = Triangles(result, "asphalt"); var walk = Triangles(result, "leftWalk", "rightWalk");
        foreach (float x in new[] { -20f, -6f, -3f, 0f, 3f, 6f, 20f }) { Covered(road, x, 0); Covered(walk, x, 0, false); }
        Covered(walk, 6, 6); ValidAndDisjoint(result);
    }

    [Test]
    public void RightAngleAcrossTwoRoadIdsGetsTheSameBevelAsOnePolyline()
    {
        var split = Build(Road("first", 5, -30, 0, 0, 0), Road("second", 5, 0, 0, 0, 30));
        var single = Build(Road("single", 5, -30, 0, 0, 0, 0, 30));
        var a = Triangles(split, "asphalt"); var b = Triangles(single, "asphalt");
        for (float x = -7.75f; x < 8; x += .5f) for (float z = -7.75f; z < 8; z += .5f)
            Assert.AreEqual(a.Any(t => Contains(t, x, z)), b.Any(t => Contains(t, x, z)), "split corner at " + x + ", " + z);
        Covered(a, 2, -2); Covered(a, -15, 4.9f); Covered(a, 4.9f, 15);
        ValidAndDisjoint(split); ValidAndDisjoint(single);
    }

    [Test]
    public void ObliqueUnequalWidthBranchesAndCloselySpacedJunctionsRemainDisjoint()
    {
        var result = Build(Road("main", 6, -40, 0, 0, 0, 40, 0), Road("narrow", 3.4f, 0, 0, 25, 30), Road("nearby", 5, 8.775f, 0, 8.775f, -30));
        ValidAndDisjoint(result);
        var walk = Triangles(result, "leftWalk", "rightWalk");
        Covered(walk, 0, 0, false); Covered(walk, 8.775f, -7, false);
    }

    [Test]
    public void ClosedLoopConnectsItsLastAndFirstSegmentsWithoutAnOpenSeam()
    {
        var result = Build(Road("loop", 5, 0, 0, 40, 0, 40, 40, 0, 40, 0, 0));
        var road = Triangles(result, "asphalt"); var walk = Triangles(result, "leftWalk", "rightWalk");
        Covered(road, -2, -2); Covered(walk, -2, -2, false); Covered(walk, -4, -4);
        ValidAndDisjoint(result);
    }

    [Test]
    public void RoundedPolylineKeepsItsFullApproachWidthAndFiniteBevels()
    {
        var points = new List<float>();
        for (int i = 0; i <= 20; i++) { float a = i * Mathf.PI / 40; points.Add(Mathf.Cos(a) * 35); points.Add(Mathf.Sin(a) * 35); }
        var result = Build(Road("curve", 5, points.ToArray())); ValidAndDisjoint(result);
        var asphalt = Triangles(result, "asphalt"); var walk = Triangles(result, "leftWalk", "rightWalk");
        Covered(asphalt, 28 * .70710678f, 28 * .70710678f, false); Covered(asphalt, 35 * .70710678f, 35 * .70710678f);
        Covered(walk, 42 * .70710678f, 42 * .70710678f);
    }

    [Test]
    public void WiderTwoLeggedBridgeTapersIntoTheNarrowApproachWithoutMovingItsCentreline()
    {
        var narrow = Road("approach", 5, -30, 0, 0, 0); var wide = Road("bridge", 6, 0, 0, 30, 0);
        var before = (Vector3[])Get<Vector3[]>(narrow, "points").Clone(); var result = Build(narrow, wide);
        var road = Triangles(result, "asphalt"); var walk = Triangles(result, "leftWalk", "rightWalk");
        Covered(road, -.1f, 5.9f); Covered(road, -4, 5.45f); Covered(road, -4, -5.45f);
        Covered(road, -9, 5.1f, false); Covered(road, -4, 5.6f, false);
        Covered(walk, -4, 6.5f); Covered(walk, -4, 5.45f, false); Covered(walk, -.1f, 8.9f);
        CollectionAssert.AreEqual(before, Get<Vector3[]>(narrow, "points")); ValidAndDisjoint(result);
    }

    [Test]
    public void WidthTapersStayWithinShortApproachesAndDoNotInflateThreeWayJunctions()
    {
        var shortApproach = Build(Road("approach", 5, -6, 0, 0, 0), Road("wide", 6, 0, 0, 30, 0));
        var road = Triangles(shortApproach, "asphalt");
        Covered(road, -1.5f, 5.45f); Covered(road, -4, 5.1f, false); ValidAndDisjoint(shortApproach);
        var junction = Build(Road("approach", 5, -30, 0, 0, 0), Road("wide", 6, 0, 0, 30, 0), Road("branch", 3, 0, 0, 0, 30));
        Covered(Triangles(junction, "asphalt"), -4, -5.45f, false); ValidAndDisjoint(junction);
    }

    [Test]
    public void OutputOrderingAndMeshCoordinatesDoNotDependOnRoadInputOrder()
    {
        var first = Road("z-road", 5, -20, 0, 20, 0); var second = Road("a-road", 6, 0, -20, 0, 20);
        var a = Build(first, second); var b = Build(second, first);
        CollectionAssert.AreEqual(a.Select(r => Get<string>(r, "roadId")), b.Select(r => Get<string>(r, "roadId")));
        for (int i = 0; i < a.Length; i++) foreach (string field in new[] { "asphalt", "leftWalk", "rightWalk" })
        {
            CollectionAssert.AreEqual(Get<List<Vector3>>(Get<object>(a[i], field), "vertices"), Get<List<Vector3>>(Get<object>(b[i], field), "vertices"));
            CollectionAssert.AreEqual(Get<List<int>>(Get<object>(a[i], field), "triangles"), Get<List<int>>(Get<object>(b[i], field), "triangles"));
        }
    }

    [Test]
    public void CurrentTownBuildStaysBoundedAndDoesNotMutateBlueprintRoads()
    {
        var layout = Type.GetType("CoastalCityLayout, Assembly-CSharp", true);
        var roads = ((IEnumerable)layout.GetProperty("Roads").GetValue(null)).Cast<object>().ToArray();
        var before = roads.Select(r => (Vector3[])Get<Vector3[]>(r, "points").Clone()).ToArray();
        var timer = Stopwatch.StartNew(); var result = Build(roads); timer.Stop();
        Assert.AreEqual(roads.Length, result.Length);
        var triangles = Triangles(result, "asphalt", "leftWalk", "rightWalk");
        Assert.That(triangles.Length, Is.GreaterThan(100).And.LessThan(100000), "Convex subtraction must not explode the authored town into tiny fragments.");
        Assert.Less(timer.Elapsed.TotalSeconds, 10, "This is a bounded geometry bake, not an unbounded all-pairs polygon union.");
        for (int i = 0; i < roads.Length; i++) CollectionAssert.AreEqual(before[i], Get<Vector3[]>(roads[i], "points"));
        foreach (var triangle in triangles) Assert.Greater(Vector3.Cross(triangle.b - triangle.a, triangle.c - triangle.a).y, .000001f);
        TestContext.WriteLine("Town surface mesh: " + triangles.Length + " triangles in " + timer.Elapsed.TotalMilliseconds.ToString("F0") + " ms.");
    }
}

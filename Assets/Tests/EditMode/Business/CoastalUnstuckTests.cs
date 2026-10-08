using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Map-space candidate selection; never teleports a player or opens a gameplay/save session.</summary>
public class CoastalUnstuckTests
{
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Get(object o, string name) => o.GetType().GetField(name).GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetField(name).SetValue(o, value);
    ScriptableObject map;

    [SetUp]
    public void Setup()
    {
        var type = R("TownMapData"); map = ScriptableObject.CreateInstance(type); Set(map, "layoutId", "coastal-v4");
        var walk = Activator.CreateInstance(type.GetNestedType("Area"));
        Set(walk, "kind", Enum.Parse(R("MapAreaKind"), "Sidewalk"));
        Set(walk, "rect", new Rect(-1,-1,62,62)); Set(walk, "halfWidth", 1.2f);
        Set(walk, "points", new[] { Vector2.zero, new Vector2(60,60) });
        ((IList)Get(map, "areas")).Add(walk);
        var road = Activator.CreateInstance(type.GetNestedType("Road"));
        Set(road, "rect", new Rect(-2,-7,64,64)); Set(road, "halfWidth", 2f); Set(road, "axis", (byte)3);
        Set(road, "points", new[] { new Vector2(0,-5), new Vector2(60,55) });
        ((IList)Get(map, "roads")).Add(road);
    }
    [TearDown] public void Cleanup() { if (map != null) Object.DestroyImmediate(map); }
    Vector3[] Candidates(Vector3 from) => (Vector3[])R("Unstuck").GetMethod("CoastalCandidates").Invoke(null, new object[] { map, from });

    [Test]
    public void CurvedRibbonUsesItsPolylineInsteadOfTheEmptyAabbCornerAndPrefersSidewalk()
    {
        var from = new Vector3(0,0,50); var points = Candidates(from);
        Assert.AreEqual(2, points.Length);
        Assert.Greater(Vector3.Distance(points[0], from), 30);
        Assert.Less(Mathf.Abs(points[0].x - points[0].z), 1.1f, "First candidate lies within the diagonal sidewalk strip");
        Assert.That(points[1].x - points[1].z, Is.EqualTo(5f).Within(1.5f), "Second candidate is the nearby road, not the AABB centre");
    }
    [Test]
    public void RecoveryBelowGroundProjectsFromStreetHeightRatherThanBelowItsCollider()
    {
        var points = Candidates(new Vector3(0,-30,50)); Assert.AreEqual(2, points.Length);
        foreach (var p in points) Assert.AreEqual(0, p.y);
    }
    [Test]
    public void ARemoteTownDoesNotReplaceTheLocalSpawnerFallback()
    {
        Assert.IsEmpty(Candidates(new Vector3(1000,0,1000)));
    }
    [Test]
    public void MissingSidewalkStillAllowsAPhysicallyVerifiedRoadFallback()
    {
        ((IList)Get(map, "areas")).Clear(); Assert.AreEqual(1, Candidates(new Vector3(0,0,50)).Length);
    }
    [Test]
    public void LegacyMapsRetainTheirExistingColliderBasedBehavior()
    {
        Set(map, "layoutId", ""); Assert.IsEmpty(Candidates(new Vector3(0,0,50)));
    }
}

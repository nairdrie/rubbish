using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Esc &gt; Unstuck on foot calls your truck (Shop/Truck/TruckCall.cs, Unstuck.cs; Docs/UIPass.md §5.5): the pure rules.
/// Where the truck may land (distance to its body box, which way a road runs, what a collider's name says it is) and
/// when a player counts as having fallen out of the world. The search itself (<c>TruckCall.Pick</c>) and the physics
/// probes need a scene; <c>Tools/truckcall_check/check.py</c> runs the search against made-up streets without Unity.
/// Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like CoastalTruckRecoveryTests.
/// </summary>
public class TruckCallTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    /// <summary>The standard body box: x ±1.3, z -2.2 (rear) .. 3.8 (nose), y 0.41 .. 3.31 over the root.</summary>
    static object StandardBox() => Activator.CreateInstance(T("TruckCall+DropBox"), 0f, 0.8f, 1.3f, 3.0f, 0.41f, 3.31f);
    static float Gap(Vector2 point, Vector2 root, float yaw) => (float)Static("TruckCall", "GapToBox", point, root, yaw, StandardBox());
    static object Surface(string name) => Enum.Parse(T("TruckCall+DropSurface"), name);
    static void Near(float actual, float expected, float tolerance = 0.001f) => Assert.That(actual, Is.EqualTo(expected).Within(tolerance));

    [Test]
    public void TheGapIsMeasuredFromTheBodyBoxNotTheRoot()
    {
        Near(Gap(new Vector2(0f, 10f), Vector2.zero, 0f), 6.2f);   // the nose is 3.8 m ahead of the root
        Near(Gap(new Vector2(5f, 0f), Vector2.zero, 0f), 3.7f);    // 1.3 m of body beside it
        Near(Gap(new Vector2(0f, -4f), Vector2.zero, 0f), 1.8f);   // the tail is 2.2 m behind the root
        Near(Gap(new Vector2(0.5f, 1f), Vector2.zero, 0f), 0f);    // standing inside it
    }

    [Test]
    public void TheGapTurnsWithTheTruck()
    {
        Near(Gap(new Vector2(10f, 0f), Vector2.zero, 90f), 6.2f);  // facing +X: the nose points at the player
        Near(Gap(new Vector2(0f, 5f), Vector2.zero, 90f), 3.7f);   // and its sides are now north and south
        Near(Gap(new Vector2(-10f, 0f), Vector2.zero, 270f), 6.2f);
    }

    [Test]
    public void TheNearestPointOnTheBodyIsOnItsFace()
    {
        var p = (Vector2)Static("TruckCall", "NearestOnBox", new Vector2(5f, 0f), Vector2.zero, 0f, StandardBox());
        Near(p.x, 1.3f); Near(p.y, 0f);
        p = (Vector2)Static("TruckCall", "NearestOnBox", new Vector2(0f, 20f), Vector2.zero, 0f, StandardBox());
        Near(p.x, 0f); Near(p.y, 3.8f);
    }

    [Test]
    public void HeadingsMatchUnityYaw()
    {
        Near((float)Static("TruckCall", "YawOf", new Vector2(0f, 1f)), 0f);
        Near((float)Static("TruckCall", "YawOf", new Vector2(1f, 0f)), 90f);
        Near((float)Static("TruckCall", "YawOf", new Vector2(0f, -1f)), 180f);
        Near((float)Static("TruckCall", "YawOf", new Vector2(-1f, 0f)), -90f);
    }

    [Test]
    public void RoadsBeatPavementBeatGrassBeatFootpaths()
    {
        float Cost(string kind) => (float)Static("TruckCall", "SurfaceCost", Surface(kind));
        Assert.Less(Cost("Road"), Cost("Drive"));
        Assert.Less(Cost("Drive"), Cost("Walk"));
        Assert.Less(Cost("Walk"), Cost("Ground"));
        Assert.Less(Cost("Ground"), Cost("Path"));
        Assert.AreEqual(0f, Cost("Road"));
    }

    [Test]
    public void AColliderIsKnownByTheNameTheTownBuildersGiveIt()
    {
        Assert.AreEqual(Surface("Road"), Static("TruckCall", "Classify", "Road_depot-main"));
        Assert.AreEqual(Surface("Road"), Static("TruckCall", "Classify", "DepotPad_2"));
        Assert.AreEqual(Surface("Walk"), Static("TruckCall", "Classify", "Sidewalk_-1"));
        Assert.AreEqual(Surface("Path"), Static("TruckCall", "Classify", "River walk (pedestrian)"));
        Assert.AreEqual(Surface("Path"), Static("TruckCall", "Classify", "SM_path_garden"));
        Assert.AreEqual(Surface("Ground"), Static("TruckCall", "Classify", "SM_pathlight_01"), "a lamp is not a footpath");
        Assert.AreEqual(Surface("Ground"), Static("TruckCall", "Classify", "Relief terrain 3_4"));
        Assert.AreEqual(Surface("Ground"), Static("TruckCall", "Classify", ""));
    }

    [Test]
    public void GroundIsNeverInTheWayButHousesAre()
    {
        foreach (var ground in new[] { "Road_main", "Sidewalk_1", "DepotPad_0", "Relief terrain 12_7", "Coastal terrain", "Grass_3", "River walk (pedestrian)" })
            Assert.IsTrue((bool)Static("TruckCall", "IsSurfaceName", ground), ground);
        foreach (var solid in new[] { "SM_house_03", "Building body", "Hall", "Bridge parapet", "SM_streetlamp_01" })
            Assert.IsFalse((bool)Static("TruckCall", "IsSurfaceName", solid), solid);
    }

    // ---- which way a road runs (the coastal blueprint's centrelines, in plan)

    static Type RoadType => T("CoastalCityLayout+RoadDef");
    static object Road(float halfWidth, params Vector3[] points)
    {
        var road = Activator.CreateInstance(RoadType);
        RoadType.GetField("points").SetValue(road, points);
        RoadType.GetField("halfWidth").SetValue(road, halfWidth);
        return road;
    }
    static bool Frame(Vector3 from, float reach, out Vector3 point, out Vector2 along, out float half, out float distance, params object[] roads)
    {
        var array = Array.CreateInstance(RoadType, roads.Length);
        for (int i = 0; i < roads.Length; i++) array.SetValue(roads[i], i);
        var args = new object[] { from, array, reach, Vector3.zero, Vector2.zero, 0f, 0f };
        bool found = (bool)T("TruckCall").GetMethod("TryRoadFrame", Flags).Invoke(null, args);
        point = (Vector3)args[3]; along = (Vector2)args[4]; half = (float)args[5]; distance = (float)args[6];
        return found;
    }

    [Test]
    public void ARoadSaysWhichWayItRunsAtTheNearestPoint()
    {
        var bend = Road(5f, Vector3.zero, new Vector3(0f, 0f, 40f), new Vector3(40f, 0f, 40f));
        Assert.IsTrue(Frame(new Vector3(20f, 3f, 10f), 60f, out var p, out var along, out var half, out var d, bend));
        Near(p.x, 0f); Near(p.z, 10f);                 // the first leg, not the bend's bounding-box centre
        Near(along.x, 0f); Near(along.y, 1f);          // it runs north there
        Near(half, 5f); Near(d, 20f);
        Assert.IsTrue(Frame(new Vector3(30f, 0f, 43f), 60f, out p, out along, out _, out _, bend));
        Near(along.x, 1f); Near(along.y, 0f);          // and east on the second leg
    }

    [Test]
    public void ARoadOutOfReachGivesNothingAndTheNearerOneWins()
    {
        var far = Road(4f, new Vector3(30f, 0f, 0f), new Vector3(30f, 0f, 20f));
        var near = Road(6f, Vector3.zero, new Vector3(0f, 0f, 20f));
        Assert.IsTrue(Frame(new Vector3(3f, 1f, 5f), 60f, out var p, out _, out var half, out _, far, near));
        Near(p.x, 0f); Near(half, 6f);
        Assert.IsFalse(Frame(new Vector3(300f, 0f, 300f), 60f, out _, out _, out _, out _, far, near));
        Assert.IsFalse(Frame(new Vector3(float.NaN, 0f, 0f), 60f, out _, out _, out _, out _, near));
    }

    // ---- falling out of the world (Unstuck.FellOutOfWorld)

    [Test]
    public void OnlyAPlayerFarUnderTheTownHasFallenThroughIt()
    {
        bool Fell(Vector3 p) => (bool)Static("Unstuck", "FellOutOfWorld", p);
        Assert.IsFalse(Fell(new Vector3(120f, 0.1f, 60f)), "on the street");
        Assert.IsFalse(Fell(new Vector3(400f, -7f, 100f)), "on the sea bed, which is the lowest ground there is");
        Assert.IsTrue(Fell(new Vector3(120f, -40f, 60f)), "under the town");
        Assert.IsTrue(Fell(new Vector3(120f, -13f, 60f)));
    }

    [Test]
    public void TheShopAndCasinoPocketsSitUnderTheTownOnPurpose()
    {
        bool Fell(Vector3 p) => (bool)Static("Unstuck", "FellOutOfWorld", p);
        var bay = (Vector3)Static("DepotRowLayout", "InteriorOrigin", 0) + new Vector3(0f, 2f, 0f);
        Assert.IsFalse(Fell(bay), "inside a shop");
        var casino = (Vector3)T("CasinoLayout").GetField("Origin", Flags).GetValue(null) + new Vector3(0f, 1f, 0f);
        Assert.IsFalse(Fell(casino), "inside the casino");
        Assert.IsTrue(Fell(casino + new Vector3(0f, -30f, 0f)), "through the casino floor");
    }
}

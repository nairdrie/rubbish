using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Layout/save-boundary contracts. No gameplay scene, editor rebuild, network transport or save is started.</summary>
public class DepotRowLayoutTests
{
    const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => Runtime(type).GetMethod(method, PublicStatic).Invoke(null, args);
    static object Get(object value, string field) => value.GetType().GetField(field, Instance).GetValue(value);
    static void Set(object value, string field, object next) => value.GetType().GetField(field, Instance).SetValue(value, next);
    static T Layout<T>(string method, params object[] args) => (T)Call("DepotRowLayout", method, args);
    static Quaternion Facing => (Quaternion)Runtime("DepotRowLayout").GetProperty("StreetFacing").GetValue(null);
    readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
        owned.Clear();
    }

    static void Near(Vector3 a, Vector3 b, string why = null) => Assert.That(Vector3.Distance(a, b), Is.LessThan(.001f), why);
    static void SameRotation(Quaternion a, Quaternion b) => Assert.That(Quaternion.Angle(a, b), Is.LessThan(.01f));

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void DoorRoadAndPadReadTheSharedBlueprint(int lot)
    {
        Near((Vector3)Call("CoastalCityLayout", "DepotDoor", lot), Layout<Vector3>("DoorPoint", lot));
        Near((Vector3)Call("CoastalCityLayout", "DepotRoadPosition", lot), Layout<Vector3>("RoadPoint", lot));
        Assert.AreEqual(Call("CoastalCityLayout", "DepotPad", lot), Layout<Rect>("PadRect", lot));
        SameRotation((Quaternion)Runtime("CoastalCityLayout").GetProperty("DepotFacing").GetValue(null), Facing);
        var roadDirection = (Layout<Vector3>("RoadPoint", lot) - Layout<Vector3>("DoorPoint", lot)).normalized;
        Assert.That(Vector3.Dot(roadDirection, Facing * Vector3.forward), Is.GreaterThan(.99f));
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void EverySpawnAndExitUsesLifeSizedDoorOffsetsInsideItsOwnApron(int lot)
    {
        var door = Layout<Vector3>("DoorPoint", lot);
        string[] methods = { "StreetTruck", "StreetTruckRollout", "StreetOnFoot" };
        Vector3[] offsets = { new Vector3(0, .15f, 5.5f), new Vector3(0, .35f, 3), new Vector3(5.8f, .05f, 2.2f) };
        for (int i = 0; i < methods.Length; i++)
        {
            var pose = Layout<Pose>(methods[i], lot);
            Near(door + Facing * offsets[i], pose.position, methods[i]);
            SameRotation(Facing, pose.rotation);
            Assert.AreEqual(lot, Layout<int>("PadAt", pose.position), methods[i]);
        }
        for (int player = 0; player < 4; player++)
        {
            var spawn = Layout<Pose>("StreetSpawn", lot, player);
            Near(door + Facing * new Vector3(-6 + 1.2f * player, .05f, 9), spawn.position);
            SameRotation(Facing * Quaternion.Euler(0, 160, 0), spawn.rotation);
            Assert.AreEqual(lot, Layout<int>("PadAt", spawn.position));
        }
    }

    [Test]
    public void FourApronsDoNotOverlapAndStreetNeverCountsAsAnInterior()
    {
        for (int lot = 0; lot < 4; lot++)
        {
            var pad = Layout<Rect>("PadRect", lot);
            Assert.Greater(pad.width, 10); Assert.Greater(pad.height, 10);
            Assert.AreEqual(-1, Layout<int>("InteriorAt", Layout<Vector3>("DoorPoint", lot)));
            Assert.AreEqual(-1, Layout<int>("PadAt", Layout<Vector3>("RoadPoint", lot)));
            for (int other = lot + 1; other < 4; other++) Assert.IsFalse(pad.Overlaps(Layout<Rect>("PadRect", other)));
        }
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void InteriorCoordinatesRemainCompatibleWithExistingBusinessStock(int lot)
    {
        var expected = new Vector3(100 + lot * 150, -20, -400);
        Near(expected, Layout<Vector3>("InteriorOrigin", lot));
        Assert.AreEqual(lot, Layout<int>("InteriorAt", expected));
        var bounds = Layout<Bounds>("InteriorBounds", lot);
        Near(expected + new Vector3(.5f, 4, 1), bounds.center);
        Near(new Vector3(30, 16, 30), bounds.size);
    }

    GameObject Child(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void IncrementalStreetApplyMovesOnlyFacadeAndLeaseNotPocketOrRestCollider(int lot)
    {
        var root = new GameObject("Layout test"); owned.Add(root); root.SetActive(false);
        var component = root.AddComponent(Runtime("DepotLot")); Set(component, "lot", lot);
        var facade = Child("Facade", root.transform); var lease = Child("ForLease", root.transform);
        Set(component, "facade", facade.transform); Set(component, "forLeaseSign", lease);
        var door = Child("StreetDoor", facade.transform); door.transform.localPosition = new Vector3(0, 0, .2f);
        var trigger = door.AddComponent<BoxCollider>(); trigger.center = new Vector3(0, 2, 2.5f); trigger.size = new Vector3(10, 6, 5);
        var inside = Child("Interior", root.transform); var floor = Child("PocketFloor", root.transform);
        var rest = Child("SortingTableRestCollider", root.transform);
        inside.transform.position = Layout<Vector3>("InteriorOrigin", lot);
        floor.transform.position = inside.transform.position + new Vector3(.5f, -.25f, .5f);
        rest.transform.position = inside.transform.position + new Vector3(1, 1.8f, 2);
        var positions = new[] { inside.transform.position, floor.transform.position, rest.transform.position };
        var children = new[] { inside, floor, rest };
        component.GetType().GetMethod("ApplyStreetLayout").Invoke(component, null);
        component.GetType().GetMethod("ApplyStreetLayout").Invoke(component, null); // idempotent, no accumulated rotation
        Near(Layout<Vector3>("DoorPoint", lot), facade.transform.position); SameRotation(Facing, facade.transform.rotation);
        Near(Layout<Vector3>("StreetPoint", lot, new Vector3(7, 0, 8)), lease.transform.position);
        Near(Layout<Vector3>("StreetPoint", lot, new Vector3(0, 2, 2.7f)), trigger.transform.TransformPoint(trigger.center));
        SameRotation(Facing, trigger.transform.rotation);
        Near(new Vector3(10, 6, 5), trigger.size);
        Near(facade.transform.localPosition, (Vector3)Get(component, "facadeHome"));
        Near(Vector3.zero, root.transform.position); SameRotation(Quaternion.identity, root.transform.rotation);
        Near(Vector3.one, facade.transform.localScale);
        for (int i = 0; i < children.Length; i++) Near(positions[i], children[i].transform.position, children[i].name);
        Assert.AreEqual(lot, Get(component, "lot"));
    }

    [TestCase("business_sign", 8.3f, 2.2f)] [TestCase("doorbell", 4.9f, .35f)]
    public void StreetWorkshopAuthorityUsesTheSameRotatedFrame(string recipe, float x, float z)
    {
        for (int lot = 0; lot < 4; lot++)
        {
            var pose = (Pose)Call("WorkshopWorld", "PlacementFor", lot, recipe);
            Near(Layout<Vector3>("StreetPoint", lot, new Vector3(x, 0, z)), pose.position);
            SameRotation(Facing, pose.rotation);
        }
    }

    /// <summary>The Monument (which replaced the three sculpture slots) and the two bag toys on the forecourt: their slots are
    /// read from WorkshopWorld, so they may be moved, but they are always door-local offsets in the lot's rotated street frame.</summary>
    [Test]
    public void TheMonumentTheHoopAndTheLauncherUseTheSameRotatedFrame()
    {
        var world = Runtime("WorkshopWorld");
        var monument = (Vector3)world.GetField("MonumentLocal").GetValue(null); float monumentYaw = (float)world.GetField("MonumentYaw").GetRawConstantValue();
        var hoop = (Vector3)world.GetField("HoopLocal").GetValue(null); float launch = (float)world.GetField("LaunchDistance").GetRawConstantValue();
        Vector3 launcherLocal = Vector3.zero; Quaternion launcherTurn = Quaternion.identity, hoopTurn = Quaternion.identity;
        for (int lot = 0; lot < 4; lot++)
        {
            var pose = (Pose)Call("WorkshopWorld", "MonumentPose", lot);
            Near(Layout<Vector3>("StreetPoint", lot, monument), pose.position); SameRotation(Facing * Quaternion.Euler(0, monumentYaw, 0), pose.rotation);
            Assert.AreEqual(lot, Layout<int>("PadAt", pose.position), "the Monument stands on its own lot");
            var hoopPose = (Pose)Call("WorkshopWorld", "PlacementFor", lot, "bag_hoop"); var launcherPose = (Pose)Call("WorkshopWorld", "PlacementFor", lot, "spring_launcher");
            Near(Layout<Vector3>("StreetPoint", lot, hoop), hoopPose.position);
            // the launcher's slot is a door-local offset too: one launch from the hoop, the same offset and the same turn on every lot
            Vector3 local = Quaternion.Inverse(Facing) * (launcherPose.position - Layout<Vector3>("DoorPoint", lot));
            Assert.AreEqual(launch, Vector3.Distance(local, hoop), .001f); Assert.AreEqual(hoop.y, local.y, .001f);
            if (lot == 0) { launcherLocal = local; launcherTurn = Quaternion.Inverse(Facing) * launcherPose.rotation; hoopTurn = Quaternion.Inverse(Facing) * hoopPose.rotation; }
            Near(launcherLocal, local, "lot " + lot); Near(Layout<Vector3>("StreetPoint", lot, launcherLocal), launcherPose.position);
            SameRotation(launcherTurn, Quaternion.Inverse(Facing) * launcherPose.rotation); SameRotation(hoopTurn, Quaternion.Inverse(Facing) * hoopPose.rotation);
            Assert.AreEqual(lot, Layout<int>("PadAt", hoopPose.position)); Assert.AreEqual(lot, Layout<int>("PadAt", launcherPose.position));
        }
        // the retired sculpture ids have no street slot of their own any more: an unknown id is nowhere special
        foreach (string gone in new[] { "junk_sculpture", "sculpture_tower", "sculpture_monument" })
            Assert.IsFalse((bool)Call("WorkshopWorld", "StreetRecipe", gone), gone);
    }

    [Test]
    public void CleanupNoticeSitsOutsideTheEndPadAndFacesTheRoad()
    {
        var spot = (Vector3)Runtime("CleanupBoard").GetProperty("Spot").GetValue(null);
        var local = Quaternion.Inverse(Facing) * (spot - Layout<Vector3>("DoorPoint", 3));
        var edge = Layout<Vector2>("PadLocalEdge", 3);
        Assert.AreEqual(edge.x - 6, local.x, .001f);
        Assert.AreEqual(edge.y - 1.5f, local.z, .001f);
        Assert.AreEqual(-1, Layout<int>("PadAt", spot));
    }

    ScriptableObject Map()
    {
        var map = ScriptableObject.CreateInstance(Runtime("TownMapData")); owned.Add(map); return map;
    }
    static void AddDoor(ScriptableObject map, string id, Vector2 position)
    {
        var poi = Activator.CreateInstance(Runtime("TownMapData+Poi"));
        Set(poi, "kind", Enum.Parse(Runtime("MapPoiKind"), "GarageDoor"));
        Set(poi, "id", id); Set(poi, "pos", position);
        ((IList)Get(map, "pois")).Add(poi);
    }
    static Vector2 MapPosition(ScriptableObject map, int lot)
    {
        object[] args = { map, lot, null };
        Assert.IsTrue((bool)Call("DepotMapLocations", "TryPosition", args));
        return (Vector2)args[2];
    }

    [TestCase(0, -1, 0)] [TestCase(3, -1, 3)] [TestCase(3, 1, 1)] [TestCase(-1, 2, 2)] [TestCase(-1, -1, -1)]
    public void MapChoosesTheCurrentInteriorOtherwiseTheOwnedLot(int own, int interior, int expected) =>
        Assert.AreEqual(expected, Call("DepotMapLocations", "SelectLot", own, interior));

    [Test]
    public void MapFindsEachGuestDepotRegardlessOfPoiOrderAndIgnoresLegacyFirstDoor()
    {
        var map = Map(); AddDoor(map, "old municipal yard", new Vector2(-100, -100));
        for (int lot = 3; lot >= 0; lot--) AddDoor(map, (string)Call("DepotMapLocations", "PoiId", lot), new Vector2(400, 100 + 42 * lot));
        for (int lot = 0; lot < 4; lot++) Assert.AreEqual(new Vector2(400, 100 + 42 * lot), MapPosition(map, lot));
        Assert.AreEqual(new Vector2(400, 100), MapPosition(map, -1));
    }

    [Test]
    public void KnownLotNeverFallsBackToAnotherBusinessWhenPoiIsAbsent()
    {
        var map = Map(); AddDoor(map, "old yard", new Vector2(-100, -100));
        var door = Layout<Vector3>("DoorPoint", 2);
        Assert.AreEqual(new Vector2(door.x, door.z), MapPosition(map, 2));
        Assert.AreEqual(new Vector2(-100, -100), MapPosition(map, -1), "Legacy solo maps still work.");
    }

    [Test]
    public void LiveMapBuildsFourDistinctGarageBadgesInsteadOfOnlyTheFirstPoi()
    {
        var map = Map(); AddDoor(map, "legacy door", Vector2.zero);
        for (int lot = 0; lot < 4; lot++) AddDoor(map, (string)Call("DepotMapLocations", "PoiId", lot), new Vector2(100, 100 + 42 * lot));
        var root = new GameObject("Map layout test", typeof(RectTransform)); owned.Add(root);
        var view = Activator.CreateInstance(Runtime("MapView"), root.transform, new Vector2(500, 500), false);
        try
        {
            view.GetType().GetMethod("Bind").Invoke(view, new object[] { map });
            var icons = (IList)Get(view, "depots"); Assert.AreEqual(4, icons.Count);
            for (int lot = 0; lot < 4; lot++) Assert.AreEqual(lot, Get(icons[lot], "lot"));
        }
        finally { view.GetType().GetMethod("Dispose").Invoke(view, null); }
    }
}

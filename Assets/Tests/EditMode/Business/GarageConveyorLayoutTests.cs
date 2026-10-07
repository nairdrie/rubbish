using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Isolated geometry and reference checks. Never loads a game, upgrades a business or changes a save.</summary>
public class GarageConveyorLayoutTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Get(object value, string field) => value.GetType().GetField(field).GetValue(value);
    static void Set(object value, string field, object next) => value.GetType().GetField(field).SetValue(value, next);
    static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
    static Vector3[] Route(int lane) => (Vector3[])Runtime("GarageConveyorLayout").GetMethod("Route", Static).Invoke(null, new object[] { lane });
    static Component Ensure(Component rig) => (Component)Runtime("GarageConveyorLayout").GetMethod("Ensure", Static).Invoke(null, new object[] { rig });
    readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void TearDown() { foreach (var item in owned) if (item != null) Object.DestroyImmediate(item); owned.Clear(); }

    static Transform Node(Transform parent, string name, Vector3 position = default)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; return go.transform;
    }

    sealed class Fixture
    {
        public Transform root, elevator, chute, kevin, mouth, tray, queue;
        public Component rig, feeder, dropper;
        public readonly Component[] lines = new Component[2];
        public readonly Transform[] visuals = new Transform[2];
        public BoxCollider elevatorCollider;
    }

    Fixture Build(int lot)
    {
        var f = new Fixture();
        var go = new GameObject("Conveyor layout test " + lot); owned.Add(go); go.SetActive(false); f.root = go.transform;
        if (lot >= 0) go.transform.position = (Vector3)Runtime("DepotRowLayout").GetMethod("InteriorOrigin", Static).Invoke(null, new object[] { lot });
        f.rig = go.AddComponent(Runtime("GarageRig"));
        var conveyor = Node(f.root, "Conveyor");
        f.feeder = conveyor.gameObject.AddComponent(Runtime("ConveyorFeeder")); Set(f.feeder, "rig", f.rig);
        f.elevator = Node(conveyor, "BagOVator", new Vector3(5.3f, -.066f, -.6f));
        f.elevatorCollider = Node(f.elevator, "BaseCollider", Vector3.up * .5f).gameObject.AddComponent<BoxCollider>();
        f.elevatorCollider.size = new Vector3(1.1f, 1f, .7f);
        f.chute = Node(conveyor, "DirectFeedChute", new Vector3(5.7f, -.066f, -1.35f));
        f.queue = Node(f.chute, "QueueSpot", Vector3.up * .08f); Set(f.feeder, "chute", f.queue);
        f.kevin = Node(f.root, "KevinChute", new Vector3(6.35f, 3f, -3.2f));
        f.mouth = Node(f.kevin, "Mouth", new Vector3(-.2f, -.1f, 0));
        f.dropper = f.kevin.gameObject.AddComponent(Runtime("DepotDeliveryDropper"));
        Set(f.dropper, "rig", f.rig); Set(f.dropper, "chute", f.mouth);
        f.tray = Node(conveyor, "Tray", new Vector3(-1.2f, .934f, 2.55f));
        var tray = f.tray.gameObject.AddComponent(Runtime("ConveyorTray"));
        Set(tray, "rig", f.rig); Set(tray, "slots", new[] { Node(f.tray, "Slot0"), Node(f.tray, "Slot1", Vector3.right * .6f) });
        // Supply authored materials so no shared runtime-material cache is touched by these tests.
        foreach (string name in new[] { "Garage_Rubber", "Garage_Yellow", "Garage_Steel", "Garage_Frame" })
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name }; owned.Add(m);
            Node(f.root, name).gameObject.AddComponent<MeshRenderer>().sharedMaterial = m;
        }
        var lines = Array.CreateInstance(Runtime("ConveyorLine"), 2);
        for (int lane = 0; lane < 2; lane++)
        {
            var t = Node(conveyor, "Lane" + lane);
            var line = t.gameObject.AddComponent(Runtime("ConveyorLine")); f.lines[lane] = line; lines.SetValue(line, lane);
            Set(line, "lane", lane); Set(line, "tray", tray);
            var points = new Transform[9];
            for (int i = 0; i < points.Length; i++) points[i] = Node(t, "WP" + i, new Vector3(i, 0, lane));
            Set(line, "waypoints", points);
            f.visuals[lane] = Node(t, "Visual");
            Node(f.visuals[lane], "Obsolete centre-room belt").gameObject.AddComponent<BoxCollider>();
            f.visuals[lane].gameObject.SetActive(lane == 1);
        }
        Set(f.feeder, "lanes", lines);
        return f;
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void SoloAndEveryDepotUseTheSameLocalRouteWithoutReplacingTheirMachines(int lot)
    {
        var f = Build(lot);
        Vector3 trayBefore = f.tray.position;
        var lineArray = Get(f.feeder, "lanes");
        var oldPoints = (Transform[])Get(f.lines[0], "waypoints");
        var layout = Ensure(f.rig);
        Assert.NotNull(layout); Assert.IsTrue((bool)Property(layout, "Applied"));
        Assert.AreSame(lineArray, Get(f.feeder, "lanes"));
        Assert.AreSame(f.queue, Get(f.feeder, "chute"));
        Assert.AreSame(f.mouth, Get(f.dropper, "chute"));
        Assert.AreSame(f.rig, Get(f.feeder, "rig")); Assert.AreSame(f.rig, Get(f.dropper, "rig"));
        Assert.AreEqual(trayBefore, f.tray.position, "The sorting tray and station must not move");
        Assert.Less(Vector3.Distance(new Vector3(6.25f, -.066f, -2.55f), f.root.InverseTransformPoint(f.elevator.position)), .0001f);
        Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 90, 0), f.elevator.localRotation), .01f);
        Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 180, 0), f.chute.localRotation), .01f,
            "Direct Feed's queue sign faces into the room, not the right wall");
        Assert.That(f.root.InverseTransformPoint(f.mouth.position).z, Is.EqualTo(-4.36f).Within(.0001f), "Kevin now delivers inward from the right/south wall");
        Assert.That(f.root.InverseTransformPoint(f.mouth.position).x, Is.EqualTo(5.55f).Within(.0001f));
        for (int lane = 0; lane < 2; lane++)
        {
            var expected = Route(lane); var points = (Transform[])Get(f.lines[lane], "waypoints");
            Assert.AreEqual(expected.Length, points.Length);
            for (int i = 0; i < points.Length; i++)
                Assert.Less(Vector3.Distance(expected[i] + Vector3.down * .066f, f.root.InverseTransformPoint(points[i].position)), .0001f);
            Assert.AreSame(f.visuals[lane], f.lines[lane].transform.Find("Visual"));
            Assert.AreEqual(lane == 1, f.visuals[lane].gameObject.activeSelf, "Upgrade visibility remains controlled by the existing gate");
            Assert.IsEmpty(f.visuals[lane].GetComponentsInChildren<Collider>(true), "No obsolete low belt collider or new overhead camera blocker");
            Assert.Greater(f.visuals[lane].GetComponentsInChildren<Renderer>(true).Length, 10);
            Assert.Greater((float)Property(f.lines[lane], "Length"), 17f);
            var args = new object[] { (float)Property(f.lines[lane], "Length"), Vector3.zero };
            var end = (Vector3)f.lines[lane].GetType().GetMethod("PointAt").Invoke(f.lines[lane], args);
            Assert.Less(Vector3.Distance(points[points.Length - 1].position, end), .0001f, "Item movement uses the rebuilt physical route");
        }
        Assert.AreSame(oldPoints[0], ((Transform[])Get(f.lines[0], "waypoints"))[0]);
        int children = f.root.GetComponentsInChildren<Transform>(true).Length;
        Assert.AreSame(layout, Ensure(f.rig));
        Assert.AreEqual(children, f.root.GetComponentsInChildren<Transform>(true).Length, "Repeated Resolve cannot duplicate geometry");
    }

    [Test]
    public void RouteLeavesDoorAndWalkwayClearAndStaysInTheRoom()
    {
        for (int lane = 0; lane < 2; lane++)
        {
            var route = Route(lane); bool aboveDoor = false;
            for (int i = 1; i < route.Length; i++)
                for (int sample = 0; sample <= 40; sample++)
                {
                    Vector3 p = Vector3.Lerp(route[i - 1], route[i], sample / 40f);
                    Assert.That(p.x, Is.InRange(-1.3f, 6.4f)); Assert.That(p.z, Is.InRange(-3f, 4.45f));
                    if (p.z < -1.5f || p.z > 1.2f) continue;
                    aboveDoor = true;
                    Assert.Greater(p.y - .16f, 2.95f + .4f, "Belt underside clears workshop portal, sign and the walking route");
                    Assert.Greater(p.x, 5f, "No belt crosses the central garage walkway");
                }
            Assert.IsTrue(aboveDoor);
            Assert.AreEqual(new Vector3(lane == 0 ? -1.2f : -.6f, 1.2f, 2.95f), route[route.Length - 1]);
        }
        // The existing 1.1m-wide elevator rotates to face the truck. Its north edge is safely south of the portal.
        var elevator = (Vector3)Runtime("GarageConveyorLayout").GetField("Elevator", Static).GetValue(null);
        Assert.Less(elevator.z + .55f, -1.5f - .35f);
        Assert.Less(elevator.x + .35f, 6.76f);
    }

    [Test]
    public void TwinOverheadLanesAndPillarDoglegDoNotIntersect()
    {
        var a = Route(0); var b = Route(1);
        float closest = float.MaxValue;
        for (int i = 2; i + 1 < a.Length; i++)
            for (int j = 2; j + 1 < b.Length; j++)
                for (int x = 0; x <= 20; x++)
                    for (int y = 0; y <= 20; y++)
                        closest = Mathf.Min(closest, Vector3.Distance(Vector3.Lerp(a[i], a[i + 1], x / 20f), Vector3.Lerp(b[j], b[j + 1], y / 20f)));
        Assert.Greater(closest, .57f, "Half-metre troughs plus rails need separation around both corners");
        foreach (var route in new[] { a, b })
            for (int i = 1; i < route.Length; i++)
                for (int sample = 0; sample <= 40; sample++)
                {
                    var p = Vector3.Lerp(route[i - 1], route[i], sample / 40f);
                    if (p.x > .8f || p.x < -.9f) continue;
                    Assert.Less(p.z + .30f, 4.28f, "Route jogs inside the north-wall pillar rather than clipping through it");
                }
    }

    [Test]
    public void MalformedRigCannotBorrowAnotherBusinessConveyor()
    {
        var first = Build(0); var second = Build(1);
        Set(first.feeder, "lanes", Get(second.feeder, "lanes"));
        Vector3 before = second.elevator.position;
        Assert.IsNull(Ensure(first.rig));
        Assert.AreEqual(before, second.elevator.position);
        Assert.IsNull(second.rig.GetComponent(Runtime("GarageConveyorLayout")));
    }
}

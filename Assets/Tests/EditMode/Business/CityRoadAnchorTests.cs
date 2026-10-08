using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Explicit road edits preserve complete lots, NPC world metadata and persistent household identities.</summary>
public class CityRoadAnchorTests
{
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type RoadType => Runtime("CoastalCityLayout+RoadDef");
    static Type AnchorType => Runtime("CityRoadAnchor");
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Instance).SetValue(obj, value);
    static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Instance).GetValue(obj);
    readonly List<GameObject> roots = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear();
    }

    static object Road(string id, params Vector3[] points)
    {
        var road = Activator.CreateInstance(RoadType);
        Set(road, "id", id); Set(road, "points", points); Set(road, "halfWidth", 5f);
        return road;
    }

    static Pose Resolve(object road, float u, float lateral, float height, float yaw) =>
        (Pose)AnchorType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { road, (object)u, lateral, height, yaw });

    static bool Apply(Component anchor, object road) =>
        (bool)AnchorType.GetMethod("Apply", new[] { RoadType }).Invoke(anchor, new[] { road });

    Component Anchor(string roadId = "test-road")
    {
        var root = new GameObject("Complete anchored lot"); roots.Add(root); root.SetActive(false);
        var anchor = root.AddComponent(AnchorType);
        Set(anchor, "stableId", "house-unchanged-17"); Set(anchor, "roadId", roadId);
        Set(anchor, "u", .5f); Set(anchor, "lateral", -12f); Set(anchor, "height", .2f); Set(anchor, "yawOffset", 90f);
        return anchor;
    }

    static GameObject Child(string name, Transform parent)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); return go;
    }
    static void Near(Vector3 expected, Vector3 actual, string why = null) => Assert.That(Vector3.Distance(expected, actual), Is.LessThan(.001f), why);
    static void SameRotation(Quaternion expected, Quaternion actual, string why = null) => Assert.That(Quaternion.Angle(expected, actual), Is.LessThan(.01f), why);

    [TestCase(-.5f, 0f)] [TestCase(0f, 0f)] [TestCase(.25f, 5f)] [TestCase(.75f, 15f)] [TestCase(1.5f, 20f)]
    public void PlacementUsesNormalizedRoadDistanceAndLifeSizedOffsets(float u, float expectedZ)
    {
        var road = Road("test-road", new Vector3(10, 0, 0), new Vector3(10, 0, 20));
        var pose = Resolve(road, u, -3, .6f, 90);
        Near(new Vector3(7, .6f, expectedZ), pose.position);
        SameRotation(Quaternion.Euler(0, 90, 0), pose.rotation);
    }

    [Test]
    public void PlacementFollowsTheLocalTangentAroundABendNotAnEndpointLerp()
    {
        var road = Road("test-road", Vector3.zero, new Vector3(0, 0, 10), new Vector3(20, 0, 10));
        var pose = Resolve(road, .5f, 4, 1, 90);
        Near(new Vector3(5, 1, 6), pose.position);
        SameRotation(Quaternion.Euler(0, 180, 0), pose.rotation);
    }

    [TestCase(-1f)] [TestCase(1f)]
    public void CurbRelativeLotsKeepTheirClearanceWhenRoadWidthChanges(float side)
    {
        var anchor = Anchor(); Set(anchor, "lateralFromRoadEdge", true); Set(anchor, "lateral", side * .8f);
        var road = Road("test-road", new Vector3(10, 0, 0), new Vector3(10, 0, 20));
        Assert.IsTrue(Apply(anchor, road));
        Near(new Vector3(10 + side * 5.8f, .2f, 10), anchor.transform.position);
        var stop = Child("Curb and metadata", anchor.transform);
        var route = stop.AddComponent(Runtime("CoastalHouseRoute"));
        var beforeDoor = anchor.transform.position + Vector3.forward * 3;
        Set(route, "door", beforeDoor);
        Set(road, "halfWidth", 9f);
        Assert.IsTrue(Apply(anchor, road));
        Near(new Vector3(10 + side * 9.8f, .2f, 10), anchor.transform.position);
        Near(beforeDoor + Vector3.right * (side * 4), Get<Vector3>(route, "door"));
        Assert.IsTrue(Apply(anchor, road));
        Near(beforeDoor + Vector3.right * (side * 4), Get<Vector3>(route, "door"), "Repeated width reconciliation must not drift metadata.");
        Assert.AreEqual(side * .8f, Get<float>(anchor, "lateral"));
        Near(Vector3.one, anchor.transform.localScale);
    }

    [Test]
    public void LegacyAbsoluteAndGateAnchorsDoNotMoveWhenRoadWidens()
    {
        var anchor = Anchor();
        Assert.IsFalse(Get<bool>(anchor, "lateralFromRoadEdge"), "Existing scenes retain centreline-relative placement by default.");
        var road = Road("test-road", Vector3.zero, Vector3.forward * 20);
        Assert.IsTrue(Apply(anchor, road)); var before = anchor.transform.position;
        Set(road, "halfWidth", 9f); Assert.IsTrue(Apply(anchor, road)); Near(before, anchor.transform.position);
        Set(anchor, "lateral", 0f); Assert.IsTrue(Apply(anchor, road)); Near(new Vector3(0, .2f, 10), anchor.transform.position);
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(0f)] [TestCase(-1f)]
    public void InvalidWidthCannotMoveACurbRelativeAnchor(float halfWidth)
    {
        var anchor = Anchor(); Set(anchor, "lateralFromRoadEdge", true); anchor.transform.position = Vector3.one;
        var road = Road("test-road", Vector3.zero, Vector3.forward * 20); Set(road, "halfWidth", halfWidth);
        Assert.IsFalse(Apply(anchor, road)); Near(Vector3.one, anchor.transform.position);
        var resolve = AnchorType.GetMethod("Resolve", new[] { RoadType });
        var thrown = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(anchor, new[] { road }));
        Assert.IsInstanceOf<ArgumentException>(thrown.InnerException);
    }

    [Test]
    public void CurbRelativeDesiredPoseAndExplicitResolveAgreeWithoutMovingTheAnchor()
    {
        var anchor = Anchor("depot"); Set(anchor, "lateralFromRoadEdge", true); Set(anchor, "lateral", -.8f);
        anchor.transform.position = new Vector3(8, 9, 10);
        var road = Runtime("CoastalCityLayout").GetMethod("Road").Invoke(null, new object[] { "depot" });
        var expected = Resolve(road, .5f, -Get<float>(road, "halfWidth") - .8f, .2f, 90);
        var resolved = (Pose)AnchorType.GetMethod("Resolve", new[] { RoadType }).Invoke(anchor, new[] { road });
        var desired = (Pose)AnchorType.GetProperty("DesiredPose").GetValue(anchor);
        Near(expected.position, desired.position); Near(expected.position, resolved.position);
        SameRotation(expected.rotation, desired.rotation); Near(new Vector3(8, 9, 10), anchor.transform.position);
    }

    [Test]
    public void SceneValidationUsesCurbRelativePoseAndReportsAnUnreconciledCentrelinePose()
    {
        var anchor = Anchor("depot"); Set(anchor, "lateralFromRoadEdge", true); Set(anchor, "lateral", -.8f);
        var road = Runtime("CoastalCityLayout").GetMethod("Road").Invoke(null, new object[] { "depot" });
        Assert.IsTrue(Apply(anchor, road));
        var validate = Runtime("CoastalCityValidation").GetMethod("Validate");
        var errors = (List<string>)validate.Invoke(null, new object[] { anchor.transform, null });
        Assert.IsFalse(errors.Exists(e => e.Contains("placement differs")), string.Join("\n", errors));
        anchor.transform.position = Resolve(road, .5f, -.8f, .2f, 90).position;
        errors = (List<string>)validate.Invoke(null, new object[] { anchor.transform, null });
        Assert.IsTrue(errors.Exists(e => e.Contains("placement differs")), string.Join("\n", errors));
    }

    [Test]
    public void ApplyingAMovedRoadRigidlyMovesAllHouseMetadataAndPreservesStableIdsAndPointers()
    {
        var anchor = Anchor(); var root = anchor.transform;
        root.SetPositionAndRotation(new Vector3(17, 2, -8), Quaternion.Euler(0, 27, 0));
        root.localScale = new Vector3(1.2f, .8f, .9f);
        var oldRoot = new Pose(root.position, root.rotation);
        var house = Child("Building", root); house.transform.localPosition = new Vector3(3, 1, -4);
        var stopGo = Child("Curb stop", root); stopGo.transform.localPosition = new Vector3(2, 0, 7);
        var stop = stopGo.AddComponent(Runtime("PickupStop")); Set(stop, "stopId", "mp-017"); Set(stop, "address", "17 Maple Loop");
        Set(stop, "districtId", "mildly_pleasant"); Set(stop, "bagCount", 5); Set(stop, "wealth", .35f);
        var route = stopGo.AddComponent(Runtime("CoastalHouseRoute")); Set(route, "buildingRoot", house.transform);
        Set(route, "size", (byte)2); Set(route, "cars", (byte)2); Set(route, "hasDrive", true);
        string[] pointNames = { "door", "pathEnd", "mailbox", "ramp", "top" };
        var initialPoints = new Vector3[pointNames.Length];
        for (int i = 0; i < pointNames.Length; i++)
        { initialPoints[i] = oldRoot.position + new Vector3(i + 1, i * .4f, 3 - i); Set(route, pointNames[i], initialPoints[i]); }
        string[] poseNames = { "car0", "car1", "butlerPost" };
        var initialPoses = new Pose[poseNames.Length];
        for (int i = 0; i < poseNames.Length; i++)
        { initialPoses[i] = new Pose(oldRoot.position + new Vector3(2 + i, .2f, -3), Quaternion.Euler(0, 40 + i * 90, 0)); Set(route, poseNames[i], initialPoses[i]); }
        var oldHouse = house.transform.position; var oldStop = stopGo.transform.position;
        var road = Road("test-road", new Vector3(100, 0, 100), new Vector3(140, 0, 100));
        var desired = Resolve(road, .5f, -12, .2f, 90);
        var delta = desired.rotation * Quaternion.Inverse(oldRoot.rotation);
        Vector3 Moved(Vector3 p) => desired.position + delta * (p - oldRoot.position);
        Assert.IsTrue(Apply(anchor, road));
        Near(desired.position, root.position); SameRotation(desired.rotation, root.rotation);
        Near(Moved(oldHouse), house.transform.position); Near(Moved(oldStop), stopGo.transform.position);
        for (int i = 0; i < pointNames.Length; i++) Near(Moved(initialPoints[i]), Get<Vector3>(route, pointNames[i]), pointNames[i]);
        for (int i = 0; i < poseNames.Length; i++)
        {
            var pose = Get<Pose>(route, poseNames[i]); Near(Moved(initialPoses[i].position), pose.position, poseNames[i]);
            SameRotation(delta * initialPoses[i].rotation, pose.rotation, poseNames[i]);
        }
        Assert.AreSame(house.transform, Get<Transform>(route, "buildingRoot"));
        Assert.AreEqual("house-unchanged-17", Get<string>(anchor, "stableId")); Assert.AreEqual("test-road", Get<string>(anchor, "roadId"));
        Assert.AreEqual("mp-017", Get<string>(stop, "stopId")); Assert.AreEqual("17 Maple Loop", Get<string>(stop, "address"));
        Assert.AreEqual("mildly_pleasant", Get<string>(stop, "districtId")); Assert.AreEqual(5, Get<int>(stop, "bagCount")); Assert.AreEqual(.35f, Get<float>(stop, "wealth"));
        Assert.AreEqual(2, Get<byte>(route, "size")); Assert.AreEqual(2, Get<byte>(route, "cars")); Assert.IsTrue(Get<bool>(route, "hasDrive"));
        Near(new Vector3(1.2f, .8f, .9f), root.localScale);
        Assert.IsTrue(Apply(anchor, road)); // repeat the incremental pass: metadata must not drift/rotate twice
        for (int i = 0; i < pointNames.Length; i++) Near(Moved(initialPoints[i]), Get<Vector3>(route, pointNames[i]));
    }

    [TestCase(false)] [TestCase(true)]
    public void KidPenCenterSpotsYawAndFenceMoveTogetherIncludingInactivePens(bool nullSpots)
    {
        var anchor = Anchor(); var root = anchor.transform;
        root.SetPositionAndRotation(new Vector3(4, 0, 9), Quaternion.Euler(0, -30, 0));
        var old = new Pose(root.position, root.rotation);
        var penGo = Child("Inactive yard", root); penGo.SetActive(false);
        var pen = penGo.AddComponent(Runtime("KidPen"));
        Set(pen, "penId", "yard-17"); Set(pen, "stopId", "mp-017"); Set(pen, "center", new Vector3(8, .1f, 5)); Set(pen, "yaw", 15f);
        var spots = new[] { new Vector3(7, .1f, 5), new Vector3(9, .1f, 6) };
        Set(pen, "spots", nullSpots ? null : (Vector3[])spots.Clone()); Set(pen, "half", new Vector2(3, 2));
        var beforeCenter = Get<Vector3>(pen, "center");
        var fence = Child("Fence corner", penGo.transform); fence.transform.position = beforeCenter + Quaternion.Euler(0, 15, 0) * new Vector3(3, 0, 2);
        var road = Road("test-road", new Vector3(20, 0, 30), new Vector3(20, 0, 70));
        var desired = Resolve(road, .5f, -12, .2f, 90); var turn = desired.rotation * Quaternion.Inverse(old.rotation);
        Vector3 Moved(Vector3 p) => desired.position + turn * (p - old.position);
        Assert.IsTrue(Apply(anchor, road)); Near(Moved(beforeCenter), Get<Vector3>(pen, "center"));
        SameRotation(turn * Quaternion.Euler(0, 15, 0), Quaternion.Euler(0, Get<float>(pen, "yaw"), 0));
        var transformedSpots = Get<Vector3[]>(pen, "spots");
        if (nullSpots) Assert.IsNull(transformedSpots);
        else for (int i = 0; i < spots.Length; i++) Near(Moved(spots[i]), transformedSpots[i]);
        var dataFence = (Vector3)pen.GetType().GetMethod("ToWorld").Invoke(pen, new object[] { 3f, 2f });
        Near(dataFence, fence.transform.position, "The physical fence must still match the kid's world-space pen.");
        Assert.AreEqual("yard-17", Get<string>(pen, "penId")); Assert.AreEqual("mp-017", Get<string>(pen, "stopId"));
        Assert.AreEqual(new Vector2(3, 2), Get<Vector2>(pen, "half")); Assert.IsFalse(penGo.activeSelf);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)]
    public void OptionalGardenPathBendsFollowTheLotWithoutMovingTwice(int count)
    {
        var anchor = Anchor();
        anchor.transform.SetPositionAndRotation(new Vector3(12, 1, 8), Quaternion.Euler(0, 40, 0));
        var before = new Pose(anchor.transform.position, anchor.transform.rotation);
        var route = Child("Estate path metadata", anchor.transform).AddComponent(Runtime("CoastalHouseRoute"));
        var bends = count == 0 ? null : count == 1 ? Array.Empty<Vector3>() : new[] { new Vector3(14, 1, 18), new Vector3(18, 1, 18) };
        Set(route, "bends", bends == null ? null : (Vector3[])bends.Clone());
        var road = Road("test-road", new Vector3(60, 0, 30), new Vector3(90, 0, 30));
        var next = Resolve(road, .5f, -12, .2f, 90);
        var turn = next.rotation * Quaternion.Inverse(before.rotation);
        Assert.IsTrue(Apply(anchor, road));
        Assert.IsTrue(Apply(anchor, road));
        var moved = Get<Vector3[]>(route, "bends");
        if (bends == null) Assert.IsNull(moved);
        else
        {
            Assert.AreEqual(bends.Length, moved.Length);
            for (int i = 0; i < bends.Length; i++) Near(next.position + turn * (bends[i] - before.position), moved[i]);
        }
    }

    [Test]
    public void MissingOrDifferentRoadNeverMovesOrReassignsAnAnchor()
    {
        var anchor = Anchor(); anchor.transform.position = new Vector3(3, 4, 5);
        Assert.IsFalse(Apply(anchor, null));
        Assert.IsFalse(Apply(anchor, Road("other-road", Vector3.zero, Vector3.forward * 20)));
        Near(new Vector3(3, 4, 5), anchor.transform.position);
        Assert.AreEqual("test-road", Get<string>(anchor, "roadId")); Assert.AreEqual("house-unchanged-17", Get<string>(anchor, "stableId"));
    }

    [Test]
    public void DegenerateOrNonfiniteRoadCannotCorruptAuthoredGeometry()
    {
        var anchor = Anchor(); anchor.transform.position = Vector3.one;
        Assert.IsFalse(Apply(anchor, Road("test-road", Vector3.zero, Vector3.zero)));
        Assert.IsFalse(Apply(anchor, Road("test-road", Vector3.zero, new Vector3(float.NaN, 0, 3))));
        Set(anchor, "lateral", float.PositiveInfinity);
        Assert.IsFalse(Apply(anchor, Road("test-road", Vector3.zero, Vector3.forward * 20)));
        Near(Vector3.one, anchor.transform.position);
    }

    [Test]
    public void ReadingDesiredPoseOrEnablingAnAnchorNeverMovesIt()
    {
        var anchor = Anchor("depot"); anchor.transform.position = new Vector3(8, 9, 10);
        var road = Runtime("CoastalCityLayout").GetMethod("Road").Invoke(null, new object[] { "depot" });
        var expected = Resolve(road, .5f, -12, .2f, 90);
        var desired = (Pose)AnchorType.GetProperty("DesiredPose").GetValue(anchor);
        Near(expected.position, desired.position); SameRotation(expected.rotation, desired.rotation);
        anchor.gameObject.SetActive(true);
        Near(new Vector3(8, 9, 10), anchor.transform.position);
        Assert.IsNull(AnchorType.GetMethod("OnEnable", Instance)); Assert.IsNull(AnchorType.GetMethod("Update", Instance));
        Assert.IsTrue((bool)AnchorType.GetMethod("Apply", Type.EmptyTypes).Invoke(anchor, null));
        Near(expected.position, anchor.transform.position);
    }

    [Test]
    public void GeneratedRoadMarkerStoresFingerprintWithoutChangingIdentityOrRegenerating()
    {
        var root = new GameObject("Generated road group"); roots.Add(root);
        var marker = root.AddComponent(Runtime("CityGeneratedRoad")); Set(marker, "roadId", "test-road");
        var road = Road("test-road", Vector3.zero, Vector3.forward * 20);
        var before = (string)RoadType.GetProperty("Fingerprint").GetValue(road);
        Set(marker, "fingerprint", before); root.transform.position = new Vector3(7, 0, 8);
        Set(road, "points", new[] { Vector3.zero, Vector3.forward * 30 });
        var changed = (string)RoadType.GetProperty("Fingerprint").GetValue(road);
        Assert.AreNotEqual(before, changed); Assert.AreEqual(before, Get<string>(marker, "fingerprint"));
        Assert.AreEqual("test-road", Get<string>(marker, "roadId")); Near(new Vector3(7, 0, 8), root.transform.position);
        Assert.IsNull(marker.GetType().GetMethod("OnEnable", Instance)); Assert.IsNull(marker.GetType().GetMethod("Update", Instance));
    }
}

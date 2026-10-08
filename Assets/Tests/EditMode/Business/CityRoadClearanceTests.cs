using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Scratch colliders in the runner's existing scene; no scene creation, asset edits or saved town changes.</summary>
public class CityRoadClearanceTests
{
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    GameObject town;
    Pose roadPose;
    float halfWidth;

    [SetUp]
    public void Setup()
    {
        town = new GameObject("Road-clearance scratch fixture");
        var road = R("CoastalCityLayout").GetMethod("Road").Invoke(null, new object[] { "home" });
        // The far western Home Run approach has no nearby side streets or authored parcels.
        roadPose = (Pose)road.GetType().GetMethod("AtNormalized").Invoke(road, new object[] { .05f });
        halfWidth = (float)road.GetType().GetField("halfWidth").GetValue(road);
    }
    [TearDown]
    public void Cleanup()
    {
        if (town != null) Object.DestroyImmediate(town);
        Physics.SyncTransforms();
    }
    GameObject Box(string name, Vector3 localPosition, Vector3 size, Transform parent = null)
    {
        var go = new GameObject(name); go.transform.SetParent(parent != null ? parent : town.transform, false);
        go.transform.SetPositionAndRotation(roadPose.position + roadPose.rotation * localPosition, roadPose.rotation);
        go.AddComponent<BoxCollider>().size = size; return go;
    }
    string[] Errors()
    {
        Physics.SyncTransforms();
        return ((IEnumerable)R("CityRoadClearance").GetMethod("Validate").Invoke(null, new object[] { town.transform })).Cast<string>().ToArray();
    }

    [Test]
    public void AHouseAcrossTheActualLaneIsReportedWithStableLotIdentityWithoutBeingMoved()
    {
        var house = Box("House collision", new Vector3(0,1.8f,0), new Vector3(3,3,4));
        var anchor = house.AddComponent(R("CityRoadAnchor")); anchor.GetType().GetField("stableId").SetValue(anchor, "fixture-house");
        Vector3 before = house.transform.position; Quaternion rotation = house.transform.rotation;
        string report = string.Join("\n", Errors());
        StringAssert.Contains("driving lane blocked by fixture-house", report);
        StringAssert.Contains("House collision", report);
        Assert.AreEqual(before, house.transform.position); Assert.AreEqual(rotation, house.transform.rotation);
        Assert.IsTrue(house.GetComponent<BoxCollider>().enabled);
    }

    [Test]
    public void AClosedPermitGateIsNotTreatedAsAnAuthoringObstruction()
    {
        var gate = new GameObject("Intentionally closed gate"); gate.SetActive(false); gate.transform.SetParent(town.transform, false);
        var component = (Behaviour)gate.AddComponent(R("DistrictGate")); component.enabled = false;
        Box("Gate barrier", new Vector3(0,1.8f,0), new Vector3(7,3,3), gate.transform);
        gate.SetActive(true);
        Assert.IsEmpty(Errors());
    }

    [Test]
    public void GroundAtTheClearanceThresholdIsExcluded()
    {
        Box("Road ground", new Vector3(0,.1f,0), new Vector3(8,.4f,8));
        Assert.IsEmpty(Errors());
    }

    [Test]
    public void AnObjectWellOutsideTheDrivableEnvelopeDoesNotBlockTheRoad()
    {
        Box("Off-road building", new Vector3(halfWidth + 12f,1.8f,0), new Vector3(2,3,3));
        Assert.IsEmpty(Errors());
    }

    [Test]
    public void DynamicCargoAndOtherSceneRootsDoNotBecomePermanentLayoutErrors()
    {
        var cargo = Box("Temporary cargo", new Vector3(0,1.8f,0), new Vector3(3,3,3));
        var body = cargo.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
        var unrelated = Box("Other root", new Vector3(0,1.8f,0), new Vector3(3,3,3));
        unrelated.transform.SetParent(null, true);
        try { Assert.IsEmpty(Errors()); }
        finally { Object.DestroyImmediate(unrelated); }
    }

    GameObject StructuralParcel(string id, Vector3 position, bool wing)
    {
        var root = new GameObject(id); root.transform.SetParent(town.transform, false); root.transform.position = position;
        var anchor = root.AddComponent(R("CityRoadAnchor")); anchor.GetType().GetField("stableId").SetValue(anchor, id);
        GameObject Model(string name, Vector3 at, Vector3 size)
        {
            var model = GameObject.CreatePrimitive(PrimitiveType.Cube); model.name = name;
            model.transform.SetParent(root.transform, false); model.transform.localPosition = at; model.transform.localScale = size;
            Object.DestroyImmediate(model.GetComponent<Collider>()); return model;
        }
        var hall = Model("Hall", Vector3.up, new Vector3(2,2,2));
        if (wing) Model("SM_house_suburb_test_wing", new Vector3(5,1,0), new Vector3(4,2,4));
        // Decorative grounds must not expand the structural footprint.
        Model("Pool deck", new Vector3(10,.1f,0), new Vector3(5,.2f,5));
        var stop = new GameObject("Route metadata"); stop.transform.SetParent(root.transform, false);
        var route = stop.AddComponent(R("CoastalHouseRoute")); route.GetType().GetField("buildingRoot").SetValue(route, hall.transform);
        return root;
    }

    [Test]
    public void StructuralSiblingWingOverlapIsRejectedEvenWhenTheTwoHallBoundsAreSeparate()
    {
        var origin = new Vector3(10000,0,10000);
        var a = StructuralParcel("estate-a", origin, true);
        var b = StructuralParcel("estate-b", origin + Vector3.right * 6, false);
        string report = string.Join("\n", Errors());
        StringAssert.Contains("Building footprint overlap: estate-a and estate-b", report);
        Assert.AreEqual(origin, a.transform.position); Assert.AreEqual(origin + Vector3.right * 6, b.transform.position);
    }

    [Test]
    public void SeparatedStructuralBuildingsPassEvenWhenDecorativePoolGroundsOverlap()
    {
        var origin = new Vector3(10000,0,10000);
        StructuralParcel("estate-a", origin, true);
        StructuralParcel("estate-b", origin + Vector3.right * 10, false);
        Assert.IsEmpty(Errors());
    }
}

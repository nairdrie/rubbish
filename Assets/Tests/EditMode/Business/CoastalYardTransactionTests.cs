using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Regression: a rejected road edit restores removed/generated yard hierarchies and their metadata.</summary>
public class CoastalYardTransactionTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Editor(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    string folder;
    Scene scene;
    SceneSetup[] prior;
    GameObject town;

    [SetUp]
    public void SetUp()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var loaded = SceneManager.GetSceneAt(i);
            Assert.IsFalse(!string.IsNullOrEmpty(loaded.path) && loaded.isDirty,
                "Yard rollback test will not replace dirty saved scenes: " + loaded.path);
        }
        prior = EditorSceneManager.GetSceneManagerSetup();
        string name = "__CoastalYardTransaction_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
        scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        town = new GameObject("NeighborhoodGen");
        string markerName = (string)Runtime("CoastalCityLayout").GetField("MarkerName").GetRawConstantValue();
        new GameObject(markerName).transform.SetParent(town.transform, false);
    }

    [TearDown]
    public void TearDown()
    {
        if (prior != null && prior.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(prior);
        else if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    string Rebuild() => (string)Editor("CoastalYardAuthoring").GetMethod("Rebuild").Invoke(null, new object[] { town.transform });

    GameObject House(string id = "test-yard", float x = 5000)
    {
        var home = new GameObject("House_" + id); home.transform.SetParent(town.transform, false);
        // Isolated synthetic parcel: no live town or real save is moved; road identity is still real.
        home.transform.position = new Vector3(x, .15f, 5000);
        var anchor = home.AddComponent(Runtime("CityRoadAnchor"));
        Set(anchor, "stableId", id); Set(anchor, "roadId", "home"); Set(anchor, "lateral", 1f);
        var stop = home.AddComponent(Runtime("PickupStop")); Set(stop, "stopId", id);
        string district = (string)Runtime("Districts").GetField("MildlyPleasant").GetRawConstantValue();
        Set(stop, "districtId", district);
        var route = home.AddComponent(Runtime("CoastalHouseRoute"));
        var building = GameObject.CreatePrimitive(PrimitiveType.Cube); building.name = "HouseModel";
        building.transform.SetParent(home.transform, false); building.transform.localPosition = new Vector3(0, 2, -20);
        building.transform.localScale = new Vector3(10, 4, 10); Set(route, "buildingRoot", building.transform);
        return home;
    }

    [Test]
    public void FailedIncrementalEditRestoresReplacedBoundaryGeometryAndStableOwners()
    {
        var home = House();
        StringAssert.Contains("1 homes", Rebuild());
        var markerType = Runtime("CoastalYardBoundary");
        var before = home.GetComponentInChildren(markerType);
        Assert.NotNull(before);
        string originalJson = EditorJsonUtility.ToJson(before);
        int originalPanels = before.GetComponentsInChildren<MeshFilter>().Length;
        Assert.Greater(originalPanels, 0);
        Assert.IsTrue(EditorSceneManager.SaveScene(scene, folder + "/Yard.unity"));
        var transactionType = Editor("CityEditTransaction");
        using (var transaction = (IDisposable)Activator.CreateInstance(transactionType, new object[] { new[] { scene } }))
        {
            home.transform.position += Vector3.right * 12;
            StringAssert.Contains("1 homes", Rebuild());
            Assert.IsFalse(before != null, "The old generated boundary was actually replaced during the edit.");
        }
        Assert.AreEqual(new Vector3(5000, .15f, 5000), home.transform.position);
        var restored = home.GetComponentInChildren(markerType);
        Assert.NotNull(restored);
        Assert.AreEqual(originalJson, EditorJsonUtility.ToJson(restored));
        Assert.AreEqual(originalPanels, restored.GetComponentsInChildren<MeshFilter>().Length);
        Assert.AreEqual(1, home.GetComponentsInChildren(markerType).Length, "No duplicate generated yard survives rollback.");
    }

    [Test]
    public void YardStartsBehindDrivewayEvenWhenNoCarOrDrivewayColliderExists()
    {
        var home = House();
        var route = home.GetComponent(Runtime("CoastalHouseRoute"));
        Set(route, "hasDrive", true);
        Vector3 ramp = home.transform.position + new Vector3(7, 0, -5);
        Vector3 top = home.transform.position + new Vector3(7, 0, -25);
        Set(route, "ramp", ramp); Set(route, "top", top);
        StringAssert.Contains("1 homes", Rebuild());
        var marker = home.GetComponentInChildren(Runtime("CoastalYardBoundary"));
        var points = (Vector3[])marker.GetType().GetField("boundaryPoints").GetValue(marker);
        for (int i = 0; i < points.Length; i += 2)
        {
            var a = marker.transform.TransformPoint(points[i]); var b = marker.transform.TransformPoint(points[i + 1]);
            float distance = (float)Runtime("CoastalYardRules").GetMethod("SegmentDistance").Invoke(null, new object[] { a, b, ramp, top });
            Assert.GreaterOrEqual(distance, 2.6f, "An empty driveway must remain as accessible as an occupied driveway.");
        }
    }

    [Test]
    public void ReadOnlyValidationDetectsOwnersOrGeometryMovedWithoutRegeneration()
    {
        var home = House(); Rebuild();
        var type = Runtime("CoastalYardBoundary");
        string Errors() => string.Join("\n", (System.Collections.Generic.List<string>)type.GetMethod("Validate").Invoke(null, new object[] { town.transform }));
        Assert.IsEmpty(Errors());
        home.transform.position += Vector3.right;
        StringAssert.Contains("moved after boundary generation", Errors());
        home.transform.position -= Vector3.right;
        var marker = home.GetComponentInChildren(type);
        marker.GetComponentInChildren<BoxCollider>().enabled = false;
        StringAssert.Contains("missing, moved or disabled", Errors());
        marker.transform.localPosition += Vector3.forward;
        StringAssert.Contains("manually moved/scaled", Errors());
    }

    [Test]
    public void SharedDividerStartsBehindBothNeighborsDrivewayAccess()
    {
        House("short-drive", 5000);
        var second = House("long-drive", 5018);
        var route = second.GetComponent(Runtime("CoastalHouseRoute"));
        Set(route, "hasDrive", true);
        var ramp = second.transform.position + new Vector3(-9.5f, 0, -5);
        var top = second.transform.position + new Vector3(-9.5f, 0, -25);
        Set(route, "ramp", ramp); Set(route, "top", top);
        var report = Rebuild();
        StringAssert.Contains("2 homes", report);
        StringAssert.Contains("1 connected garden rows", report);
        Assert.AreEqual(1, town.GetComponentsInChildren(Runtime("CoastalYardBoundary")).Length,
            "The shorter neighbor's side fence must not force a connected row to fall back to isolated yards.");
    }
}

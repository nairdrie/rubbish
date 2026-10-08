using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public class TownGameplayAuthoringTests
{
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type E(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static object Call(string method, params object[] args) => E("TownGameplayAuthoring").GetMethod(method).Invoke(null, args);
    static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
    static T Get<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static Component Add(GameObject root, string type) => root.AddComponent(R(type));
    GameObject parent;
    Scene scene;
    SceneSetup[] previousSetup;

    [SetUp] public void SetUp()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var loaded = SceneManager.GetSceneAt(i);
            Assert.IsFalse(!string.IsNullOrEmpty(loaded.path) && loaded.isDirty,
                "Town authoring tests will not replace dirty saved scenes: " + loaded.path);
        }
        previousSetup = EditorSceneManager.GetSceneManagerSetup();
        scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        parent = new GameObject("TownAuthoringTest"); SceneManager.MoveGameObjectToScene(parent, scene);
    }
    [TearDown] public void TearDown()
    {
        if (parent != null) Object.DestroyImmediate(parent);
        if (previousSetup != null && previousSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
        else if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }
    GameObject Child(string name) { var child = new GameObject(name); child.transform.SetParent(parent.transform); return child; }

    [TestCase("House", true)]
    [TestCase("CollectionBuilding", true)]
    [TestCase("Decoration", false)]
    [TestCase("StreetProp", false)]
    [TestCase("ParkedCar", false)]
    public void OnlyWorkingPropertiesCreateGarbageRules(string role, bool expected)
    {
        var entry = Activator.CreateInstance(E("TownAssetEntry")); Set(entry, "role", Enum.Parse(R("TownAssetRole"), role));
        Assert.AreEqual(expected, Call("IsProperty", entry));
    }

    [Test] public void ReidentificationReplacesEveryHouseholdConsumerButNotRoutes()
    {
        var anchor = Add(parent, "CityRoadAnchor"); Set(anchor, "stableId", "old");
        var stop = Add(Child("Curb"), "PickupStop"); Set(stop, "stopId", "old");
        var lot = Child("Lot_old_1");
        var route = Add(stop.gameObject, "CoastalHouseRoute"); Set(route, "door", new Vector3(3, 4, 5));
        var car = Add(Child("Car"), "HouseholdCar"); Set(car, "stopId", "old");
        var pen = Add(Child("Yard"), "KidPen"); Set(pen, "stopId", "old"); Set(pen, "penId", "old");
        var prop = Add(Child("Mailbox"), "Knockable"); Set(prop, "stopId", "old");
        Call("RewriteHouseholdIdentity", parent, "user-test-house", "minivan_meadows");
        Assert.AreEqual("user-test-house", Get<string>(anchor, "stableId"));
        foreach (var component in new[] { stop, car, pen, prop }) Assert.AreEqual("user-test-house", Get<string>(component, "stopId"));
        Assert.AreEqual("user-test-house", Get<string>(pen, "penId"));
        Assert.AreEqual("minivan_meadows", Get<string>(stop, "districtId"));
        Assert.AreEqual("Lot_user-test-house", lot.name);
        Assert.AreEqual("Stop_user-test-house", stop.name);
        Assert.AreEqual(Get<byte>(car, "district"), Get<byte>(pen, "district"));
        Assert.AreEqual(Get<byte>(car, "district"), Get<byte>(prop, "district"));
        Assert.AreEqual(new Vector3(3, 4, 5), Get<Vector3>(route, "door"));
        Assert.IsTrue(Get<bool>(stop, "onRoute"));
    }

    [Test] public void NewHouseholdIdsFitTheGuestCurbWireFormatAndRemainUnique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => (string)Call("NewStopId")).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct().Count());
        foreach (var id in ids)
        {
            Assert.IsTrue(id.StartsWith("user-", StringComparison.Ordinal));
            Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(id), 29,
                "WorldState FixedString32Bytes truncates longer IDs, so guests would lose the curb mapping.");
        }
    }

    [Test] public void MovingAnAuthoredHouseChangesDistrictRulesWithoutChangingIdentity()
    {
        var rowType = R("HouseDirectory+Entry"); var row = Activator.CreateInstance(rowType);
        Set(row, "stopId", "user-stable-test"); Set(row, "districtId", "minivan_meadows");
        var rows = Array.CreateInstance(rowType, 1); rows.SetValue(row, 0);
        var method = R("Districts").GetMethod("AuthoredDistrictFor");
        Assert.AreEqual("minivan_meadows", method.Invoke(null, new object[] { "user-stable-test", rows }));
        Set(row, "districtId", "snootington_hills");
        Assert.AreEqual("snootington_hills", method.Invoke(null, new object[] { "user-stable-test", rows }));
        Assert.IsNull(method.Invoke(null, new object[] { "user-missing", rows }));
    }

    [Test] public void InvalidMultiStopRecipeFailsInsteadOfCopyingAmbiguousIdentity()
    {
        Add(Child("A"), "PickupStop"); Add(Child("B"), "PickupStop");
        var ex = Assert.Throws<TargetInvocationException>(() => Call("RewriteHouseholdIdentity", parent, "new", "mildly_pleasant"));
        Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);
    }

    [Test] public void NewKnockIdsAreUniqueWithoutRenumberingExistingProps()
    {
        var existing = Add(Child("Existing"), "Knockable"); Set(existing, "id", (ushort)17341);
        var added = Child("New property");
        var a = Add(added, "Knockable"); Set(a, "id", (ushort)17341);
        var child = new GameObject("Other copied prop"); child.transform.SetParent(added.transform);
        var b = Add(child, "Knockable"); Set(b, "id", (ushort)17341);
        Call("AssignUnusedKnockIds", added);
        Assert.AreEqual((ushort)17341, Get<ushort>(existing, "id"));
        Assert.AreNotEqual(Get<ushort>(a, "id"), Get<ushort>(b, "id"));
        Assert.AreNotEqual((ushort)17341, Get<ushort>(a, "id"));
        Assert.AreNotEqual((ushort)17341, Get<ushort>(b, "id"));
    }

    [Test] public void VisualBoundsIncludeChildRotationAndDoNotRequirePrefabRootAtGround()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(parent.transform);
        cube.transform.localPosition = new Vector3(5, 4, -3); cube.transform.localScale = new Vector3(2, 6, 4);
        cube.transform.localRotation = Quaternion.Euler(0, 90, 0);
        var bounds = (Bounds)Call("LocalVisualBounds", parent.transform);
        Assert.That(bounds.center.x, Is.EqualTo(5).Within(.001));
        Assert.That(bounds.min.y, Is.EqualTo(1).Within(.001));
        Assert.That(bounds.size.x, Is.EqualTo(4).Within(.001));
        Assert.That(bounds.size.z, Is.EqualTo(2).Within(.001));
    }

    [Test] public void NoVisibleGeometryIsNotASilentInvisibleStamp()
    {
        var ex = Assert.Throws<TargetInvocationException>(() => Call("LocalVisualBounds", parent.transform));
        Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);
    }

    [Test] public void HousePreviewDoesNotIncludeTheSharedNeighbourhoodFenceParentedToItsAnchor()
    {
        var house = GameObject.CreatePrimitive(PrimitiveType.Cube); house.transform.SetParent(parent.transform, false);
        house.transform.localScale = new Vector3(10, 8, 12);
        var yard = Child("Shared boundary for six homes"); Add(yard, "CoastalYardBoundary");
        var fence = GameObject.CreatePrimitive(PrimitiveType.Cube); fence.transform.SetParent(yard.transform, false);
        fence.transform.localPosition = new Vector3(100, 0, 0); fence.transform.localScale = new Vector3(60, 1, 1);
        var bounds = (Bounds)Call("LocalVisualBounds", parent.transform);
        Assert.AreEqual(new Vector3(10, 8, 12), bounds.size);
        var drawGhost = E("TownEditorWindow").GetMethod("ShouldDrawGhostMesh");
        Assert.IsTrue((bool)drawGhost.Invoke(null, new object[] { house.GetComponent<MeshFilter>() }));
        Assert.IsFalse((bool)drawGhost.Invoke(null, new object[] { fence.GetComponent<MeshFilter>() }),
            "The ghost must not draw the shared fence even though the template hierarchy contains it.");
        house.GetComponent<Renderer>().enabled = false;
        Assert.IsFalse((bool)drawGhost.Invoke(null, new object[] { house.GetComponent<MeshFilter>() }),
            "Hidden collision-only geometry is not a visible preview.");
    }

    [Test] public void DepotPadsStayReservedWhenTheDepotSceneIsNotLoaded()
    {
        var depot = R("DepotRowLayout");
        for (int i = 0; i < 4; i++)
        {
            var rect = (Rect)depot.GetMethod("PadRect").Invoke(null, new object[] { i });
            var inside = new Bounds(new Vector3(rect.center.x, 1, rect.center.y), new Vector3(2, 2, 2));
            Assert.AreEqual(i, Call("ReservedDepotOverlap", inside));
        }
        Assert.AreEqual(-1, Call("ReservedDepotOverlap", new Bounds(new Vector3(5000, 0, 5000), Vector3.one)));
    }

    [Test] public void RoadAttachmentRejectsDistantLandAndNonFinitePoints()
    {
        foreach (var point in new[] { new Vector3(100000, 0, 100000), new Vector3(float.NaN, 0, 0) })
        {
            object[] args = { point, null, 0f, 0, null };
            Assert.IsFalse((bool)Call("TryRoadAttachment", args)); Assert.IsNotEmpty((string)args[4]);
        }
    }

    [Test] public void RoadAttachmentSnapsToLandRoadAndFacesBackTowardItsCentre()
    {
        var road = R("CoastalCityLayout").GetMethod("Road").Invoke(null, new object[] { "home" });
        var pose = (Pose)road.GetType().GetMethod("AtNormalized").Invoke(road, new object[] { .5f });
        var point = pose.position + pose.rotation * Vector3.right * 14;
        object[] args = { point, null, 0f, 0, null };
        Assert.IsTrue((bool)Call("TryRoadAttachment", args));
        Assert.IsFalse(Get<bool>(args[1], "bridge"));
        Assert.That((float)args[2], Is.InRange(0f, 1f));
        Assert.That(Mathf.Abs((int)args[3]), Is.EqualTo(1));
        var entry = Activator.CreateInstance(E("TownAssetEntry")); Set(entry, "role", Enum.Parse(R("TownAssetRole"), "House"));
        object[] preview = { entry, point, default(Pose), null };
        Assert.IsTrue((bool)Call("PreviewHousePose", preview));
        var resolved = (Pose)preview[2];
        var roadPose = (Pose)args[1].GetType().GetMethod("AtNormalized").Invoke(args[1], new object[] { (float)args[2] });
        Assert.Greater(Vector3.Dot(resolved.rotation * Vector3.forward, (roadPose.position - resolved.position).normalized), .99f);
    }

    [Test] public void AuthoredLayerIsIncludedWithoutAbsorbingUnrelatedSceneRoots()
    {
        var generated = Child("Fake generated town"); Add(generated, "PickupStop");
        var authored = new GameObject("TownAuthored");
        SceneManager.MoveGameObjectToScene(authored, scene);
        try
        {
            Add(authored, "PickupStop"); Add(Child("Unrelated"), "PickupStop");
            var method = R("TownAuthoringScope").GetMethod("Components").MakeGenericMethod(R("PickupStop"));
            var all = (Array)method.Invoke(null, new object[] { generated.transform });
            Assert.AreEqual(2, all.Length);
        }
        finally { Object.DestroyImmediate(authored); }
    }

    [Test] public void FailedPublicationKeepsTheUnsavedDraftAtItsOriginalPose()
    {
        parent.transform.position = new Vector3(12, 3, 8);
        EditorSceneManager.MarkSceneDirty(scene);
        var constructor = E("CityEditTransaction").GetConstructor(new[] { typeof(bool), typeof(Scene[]) });
        Assert.NotNull(constructor);
        using ((IDisposable)constructor.Invoke(new object[] { true, new[] { scene } }))
            parent.transform.position = new Vector3(200, 0, 500);
        Assert.AreEqual(new Vector3(12, 3, 8), parent.transform.position,
            "Rollback restores the user's editable draft, not the last on-disk scene or an empty generated root.");
    }
}

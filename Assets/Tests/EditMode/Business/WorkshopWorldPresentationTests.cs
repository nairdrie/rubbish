using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Isolated workshop geometry only: never creates a world, session or player save.</summary>
public class WorkshopWorldPresentationTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    readonly List<GameObject> roots = new List<GameObject>();
    readonly Dictionary<object, object> priorMaterials = new Dictionary<object, object>();
    IDictionary materials;

    [SetUp]
    public void SetUp()
    {
        materials = (IDictionary)Runtime("SortingKit").GetField("matCache", Static).GetValue(null);
        foreach (DictionaryEntry entry in materials) priorMaterials.Add(entry.Key, entry.Value);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
        roots.Clear();
        var added = new List<object>();
        foreach (DictionaryEntry entry in materials)
        {
            if (priorMaterials.TryGetValue(entry.Key, out object old) && ReferenceEquals(old, entry.Value)) continue;
            if (entry.Value is Material material && material != null) Object.DestroyImmediate(material);
            added.Add(entry.Key);
        }
        foreach (object key in added) materials.Remove(key);
        foreach (var entry in priorMaterials) materials[entry.Key] = entry.Value;
        priorMaterials.Clear();
    }

    Component Build(string id)
    {
        var root = new GameObject("Workshop presentation test");
        root.SetActive(false);
        roots.Add(root);
        return (Component)Runtime("WorkshopModels").GetMethod("Build", Static).Invoke(null,
            new object[] { id, root.transform, new Color(.24f, .48f, .65f), "TEST BUSINESS" });
    }

    static Transform Anchor(Component model, string name) =>
        (Transform)model.GetType().GetMethod("Anchor").Invoke(model, new object[] { name });
    static Pose Placement(int lot, string id) =>
        (Pose)Runtime("WorkshopWorld").GetMethod("PlacementFor", Static).Invoke(null, new object[] { lot, id });

    [Test]
    public void LabelsInitializeFontAndRendererWithoutActivatingTheirHiddenDepot()
    {
        var root = new GameObject("Inactive depot label test");
        root.SetActive(false);
        roots.Add(root);
        var label = (Component)Runtime("WorkshopModels").GetMethod("MakeLabel", Static).Invoke(null,
            new object[] { root.transform, "Project label", "IN PROGRESS", .35f, Color.white });
        Assert.AreSame(root.transform, label.transform.parent);
        Assert.IsFalse(root.activeSelf);
        Assert.IsFalse(label.gameObject.activeInHierarchy);
        var font = label.GetType().GetProperty("font").GetValue(label);
        Assert.NotNull(font);
        var sharedFontMaterial = font.GetType().GetProperty("material").GetValue(font);
        Assert.AreSame(sharedFontMaterial, label.GetType().GetProperty("fontSharedMaterial").GetValue(label));
        Assert.NotNull(label.GetComponent<Renderer>().sharedMaterial);
        root.SetActive(true);
        label.GetType().GetMethod("ForceMeshUpdate").Invoke(label, new object[] { true, true });
        Assert.IsTrue(label.gameObject.activeInHierarchy);
        Assert.AreEqual("IN PROGRESS", label.GetType().GetProperty("text").GetValue(label));
        Assert.NotNull(label.GetComponent<MeshFilter>().sharedMesh, "TMP deliberately detaches this mesh while disabled and restores it on activation");
        Assert.That(label.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));
        Assert.AreSame(sharedFontMaterial, label.GetComponent<Renderer>().sharedMaterial, "labels must not instantiate font materials");
    }

    [TestCase("bottle_cabinet")]
    [TestCase("rotating_pedestal")]
    [TestCase("wall_display")]
    [TestCase("grand_display")]
    [TestCase("business_sign")]
    [TestCase("junk_sculpture")]
    [TestCase("sculpture_tower")]
    [TestCase("sculpture_monument")]
    [TestCase("bag_hoop")]
    [TestCase("sorting_chute")]
    [TestCase("break_sofa")]
    [TestCase("scrap_radio")]
    [TestCase("spring_launcher")]
    [TestCase("doorbell")]
    public void EveryRecipeBuildsDistinctMultipartVisualWithoutPhysicsOrGameplay(string id)
    {
        var model = Build(id);
        Assert.NotNull(model);
        Assert.AreEqual(id, model.GetType().GetField("recipeId").GetValue(model));
        Assert.That(model.GetComponentsInChildren<Renderer>(true).Length, Is.GreaterThanOrEqualTo(8));
        Assert.IsEmpty(model.GetComponentsInChildren<Collider>(true));
        Assert.IsEmpty(model.GetComponentsInChildren<Rigidbody>(true));
        Assert.IsEmpty(model.GetComponentsInChildren(Runtime("Carryable"), true));
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            // TMP deliberately clears its renderer mesh in OnDisable; these are not built-in prop meshes.
            if (filter.GetComponent("TextMeshPro") != null || filter.GetComponent("TMP_SubMesh") != null) continue;
            Assert.NotNull(filter.sharedMesh, filter.name + " has a built-in visual mesh");
        }
        Assert.NotNull(Anchor(model, "Action"));
        bool display = (bool)Runtime("WorkshopModels").GetMethod("IsDisplay", Static).Invoke(null, new object[] { id });
        var slots = (Transform[])model.GetType().GetField("DisplaySlots").GetValue(model);
        Assert.AreEqual(display ? 1 : 0, slots.Length);
        if (display) Assert.AreSame(slots[0], Anchor(model, "DisplaySlot0"));
    }

    [TestCase("bag_hoop", "RingCenter")]
    [TestCase("sorting_chute", "Input")]
    [TestCase("sorting_chute", "Output")]
    [TestCase("break_sofa", "Seat")]
    [TestCase("spring_launcher", "Muzzle")]
    [TestCase("spring_launcher", "Landing")]
    public void FunctionalModelsExposeTheirCanonicalAnchors(string id, string anchor)
    {
        Assert.NotNull(Anchor(Build(id), anchor));
    }

    [Test]
    public void PartialBuildProgressIsVisibleAndReversibleWithoutNewParts()
    {
        var model = Build("sculpture_monument");
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        var set = model.GetType().GetMethod("SetProgress");
        set.Invoke(model, new object[] { 0f });
        Assert.AreEqual(1, Array.FindAll(renderers, r => r.enabled).Length);
        set.Invoke(model, new object[] { .5f });
        Assert.AreEqual(Mathf.CeilToInt(renderers.Length * .5f), Array.FindAll(renderers, r => r.enabled).Length);
        set.Invoke(model, new object[] { 1f });
        Assert.AreEqual(renderers.Length, Array.FindAll(renderers, r => r.enabled).Length);
        set.Invoke(model, new object[] { float.NaN });
        Assert.AreEqual(1, Array.FindAll(renderers, r => r.enabled).Length);
        CollectionAssert.AreEqual(renderers, model.GetComponentsInChildren<Renderer>(true));
    }

    [Test]
    public void SelectiveOpeningRecognizesOnlyTheThinAuthoredEastWall()
    {
        var check = Runtime("WorkshopAnnex").GetMethod("IsEastWall", Static);
        Assert.IsTrue((bool)check.Invoke(null, new object[] { new Bounds(new Vector3(6.76f, 4.84f, 0), new Vector3(0, 10, 10)) }));
        foreach (var bounds in new[] {
            new Bounds(new Vector3(-6.76f, 4.84f, 0), new Vector3(0, 10, 10)),
            new Bounds(new Vector3(0, 4.84f, 4.91f), new Vector3(14, 10, 0)),
            new Bounds(new Vector3(6.72f, 3.97f, -.43f), new Vector3(1.26f, 8.22f, 1.26f)),
            new Bounds(new Vector3(6.45f, 1.18f, 3.21f), new Vector3(.7f, 1.68f, 3.46f)) })
            Assert.IsFalse((bool)check.Invoke(null, new object[] { bounds }), "unrelated garage structure must remain untouched");
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(3)]
    public void AnnexShellBlocksFollowCamerasWithoutClosingThePortalOrBreakingFloorQueries(int lot)
    {
        int walls = LayerMask.NameToLayer("Walls"), ground = LayerMask.NameToLayer("Ground");
        var origin = (Vector3)Runtime("WorkshopAnnex").GetMethod("Origin", Static).Invoke(null, new object[] { lot });
        var scope = new GameObject("Camera collision test depot");
        roots.Add(scope);
        scope.transform.position = origin;
        var mask = new GameObject("Mask"); mask.transform.SetParent(scope.transform, false);
        var original = GameObject.CreatePrimitive(PrimitiveType.Cube);
        original.name = "wall"; original.layer = walls;
        original.transform.SetParent(mask.transform, false);
        original.transform.localPosition = new Vector3(6.76f, 4.84f, 0);
        original.transform.localScale = new Vector3(.05f, 10f, 10f);
        var annex = (Component)Runtime("WorkshopAnnex").GetMethod("Ensure", Static).Invoke(null,
            new object[] { scope.transform, scope.scene, lot });
        Assert.NotNull(annex);
        Assert.IsFalse(original.GetComponent<Collider>().enabled);
        foreach (var collider in annex.GetComponentsInChildren<Collider>())
            Assert.AreEqual(collider.name == "Annex floor" ? ground : walls, collider.gameObject.layer,
                collider.name + " must use the same collision layer as the authored garage shell");

        Physics.SyncTransforms();
        int cameraMask = 1 << walls;
        foreach (var direction in new[] { Vector3.right, Vector3.forward, Vector3.back, Vector3.up })
        {
            bool blocked = Physics.SphereCast(origin + new Vector3(10f, 1.6f, 0f), .2f, direction,
                out var hit, 6f, cameraMask, QueryTriggerInteraction.Ignore);
            Assert.IsTrue(blocked, "Camera must retract before crossing " + direction);
            Assert.IsTrue(hit.collider.transform.IsChildOf(annex.transform), hit.collider.name);
        }
        Assert.IsTrue(Physics.SphereCast(origin + new Vector3(5f, 1.6f, -3f), .2f, Vector3.right,
            out var oldWallHit, 3f, cameraMask, QueryTriggerInteraction.Ignore), "Retained original wall section must still block the camera");
        Assert.AreEqual("Original east wall south", oldWallHit.collider.name);
        var doorway = origin + new Vector3(5.8f, 0f, -.15f);
        Assert.IsFalse(Physics.CapsuleCast(doorway + Vector3.up * .35f, doorway + Vector3.up * 1.55f,
            .28f, Vector3.right, 2.6f, cameraMask | (1 << ground), QueryTriggerInteraction.Ignore), "Walkthrough stays open");
        Assert.IsTrue(Physics.Raycast(origin + new Vector3(11f, 1f, -.15f), Vector3.down,
            out var floorHit, 2f, 1 << ground, QueryTriggerInteraction.Ignore));
        Assert.AreEqual("Annex floor", floorHit.collider.name);
        Assert.That(floorHit.point.y - origin.y, Is.EqualTo(-.066f).Within(.005f));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void AnnexAndBenchKeepTheExistingBusinessOwnershipBounds(int lot)
    {
        var pocket = (Bounds)Runtime("DepotRowLayout").GetMethod("InteriorBounds", Static).Invoke(null, new object[] { lot });
        var annex = (Bounds)Runtime("WorkshopAnnex").GetMethod("BoundsForLot", Static).Invoke(null, new object[] { lot });
        var bench = (Vector3)Runtime("WorkshopWorld").GetMethod("WorkbenchPosition", Static).Invoke(null, new object[] { lot });
        Assert.IsTrue(pocket.Contains(annex.min));
        Assert.IsTrue(pocket.Contains(annex.max));
        Assert.IsTrue(pocket.Contains(bench));
        foreach (string id in new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display", "break_sofa", "scrap_radio", "bag_hoop", "sorting_chute", "spring_launcher" })
            Assert.IsTrue(pocket.Contains(Placement(lot, id).position), id);
    }

    [Test]
    public void LauncherArcDescendsThroughHoopBelowTheCeiling()
    {
        var launcher = Build("spring_launcher");
        var hoop = Build("bag_hoop");
        var launcherPose = Placement(-1, "spring_launcher");
        var hoopPose = Placement(-1, "bag_hoop");
        launcher.transform.SetPositionAndRotation(launcherPose.position, launcherPose.rotation);
        hoop.transform.SetPositionAndRotation(hoopPose.position, hoopPose.rotation);
        Vector3 from = Anchor(launcher, "Muzzle").position, to = Anchor(launcher, "Landing").position;
        Vector3 ring = Anchor(hoop, "RingCenter").position, gravity = new Vector3(0, -9.81f, 0);
        const float seconds = 1.1f;
        Vector3 velocity = (to - from) / seconds - gravity * (seconds * .5f);
        float apex = -velocity.y / gravity.y;
        Assert.That((from + velocity * apex + .5f * gravity * apex * apex).y, Is.LessThan(3.5f), "headroom below the 4.3 m roof");
        float crossing = (velocity.y + Mathf.Sqrt(velocity.y * velocity.y + 2f * gravity.y * (ring.y - from.y))) / -gravity.y;
        Vector3 atRing = from + velocity * crossing + .5f * gravity * crossing * crossing;
        Assert.That(crossing, Is.GreaterThan(apex));
        Assert.That(Vector2.Distance(new Vector2(atRing.x, atRing.z), new Vector2(ring.x, ring.z)), Is.LessThan(.15f));
        Assert.That(to.x, Is.LessThan(12.7f), "the endpoint clears the east wall");
    }
}

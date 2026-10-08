using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Isolated decor / Monument geometry and the canonical places they stand in (WorkshopModels, MonumentModels,
/// WorkshopWorld.PlacementFor): never creates a world, session or player save. Placements are pinned as RULES (inside the
/// walls, clear of the garage's fixtures, on the lot's pad, clear of each other and of the way out), never as coordinates.</summary>
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
    static T World<T>(string member)
    {
        var type = Runtime("WorkshopWorld"); var field = type.GetField(member, Static);
        return (T)(field != null ? field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null) : type.GetProperty(member, Static).GetValue(null));
    }
    static T Layout<T>(string method, params object[] args) => (T)Runtime("DepotRowLayout").GetMethod(method, Static).Invoke(null, args);
    static bool StreetRecipe(string id) => (bool)Runtime("WorkshopWorld").GetMethod("StreetRecipe", Static).Invoke(null, new object[] { id });
    static string[] RecipeIds => (string[])Runtime("WorkshopModels").GetField("RecipeIds", Static).GetValue(null);
    static string[] Indoor => RecipeIds.Where(id => !StreetRecipe(id)).ToArray();
    static string[] Street => RecipeIds.Where(StreetRecipe).ToArray();
    static int LegacyShopLot => (int)Runtime("DepotFlowRules").GetField("LegacyShopLot").GetRawConstantValue();
    static int StreetLot(int lot) => lot >= 0 ? lot : LegacyShopLot;
    static Vector3 Origin(int lot) => (Vector3)Runtime("WorkshopWorld").GetMethod("Origin", Static).Invoke(null, new object[] { lot });
    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

    Component Posed(int lot, string id)
    {
        var model = Build(id); var pose = Placement(lot, id);
        model.transform.SetPositionAndRotation(pose.position, pose.rotation);
        return model;
    }

    /// <summary>World box of a model's built-in part meshes (labels left out: TMP has no mesh while its depot is hidden).
    /// Works on an inactive model, which is how every hidden depot interior holds its decor.</summary>
    static Bounds PartBounds(Component model)
    {
        bool any = false; var bounds = new Bounds();
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || filter.GetComponent("TextMeshPro") != null || filter.GetComponent("TMP_SubMesh") != null) continue;
            var local = filter.sharedMesh.bounds; var matrix = filter.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var world = matrix.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(world, Vector3.zero); any = true; } else bounds.Encapsulate(world);
            }
        }
        Assert.IsTrue(any, "a model with no parts");
        return bounds;
    }

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

    [Test]
    public void TheModelsAreExactlyTheElevenDecorPiecesAndARetiredSculptureBuildsNothing()
    {
        var catalog = ((IEnumerable)Runtime("WorkshopCatalog").GetProperty("All").GetValue(null)).Cast<object>().ToArray();
        string IdOf(object recipe) => (string)recipe.GetType().GetField("id").GetValue(recipe);
        bool Decor(object recipe) => (bool)recipe.GetType().GetProperty("isDecor").GetValue(recipe);
        CollectionAssert.AreEquivalent(catalog.Where(Decor).Select(IdOf), RecipeIds, "one model per decor piece Scrapazon sells");
        Assert.AreEqual(11, RecipeIds.Length); Assert.AreEqual(11, RecipeIds.Distinct().Count());
        var root = new GameObject("Workshop retired-model test"); root.SetActive(false); roots.Add(root);
        foreach (string id in catalog.Where(r => !Decor(r)).Select(IdOf).Concat(new[] { "monument", "workbench", "made_up", "", null }))
        {
            Assert.IsNull(Runtime("WorkshopModels").GetMethod("Build", Static).Invoke(null, new object[] { id, root.transform, Color.white, "TEST" }), id + " has no decor model");
            Assert.AreEqual(0, root.transform.childCount, id + " left parts behind");
        }
        // which piece is which kind: four take a find, six do something, the sign is the sign
        bool Is(string kind, string id) => (bool)Runtime("WorkshopModels").GetMethod(kind, Static).Invoke(null, new object[] { id });
        CollectionAssert.AreEquivalent(new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" }, RecipeIds.Where(id => Is("IsDisplay", id)));
        CollectionAssert.AreEquivalent(new[] { "bag_hoop", "sorting_chute", "break_sofa", "scrap_radio", "spring_launcher", "doorbell" }, RecipeIds.Where(id => Is("IsGadget", id)));
        foreach (string id in RecipeIds) Assert.IsTrue(Is("IsDisplay", id) ^ Is("IsGadget", id) ^ id == "business_sign", id + " is exactly one kind of piece");
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
    public void OnlyTheFourDisplayPiecesBecomePressEDisplaysOnlyGadgetsBecomeGadgetsAndTheSignGetsItsMenu()
    {
        var zoneType = Runtime("InteractionZone");
        foreach (string id in RecipeIds)
        {
            var model = Build(id);
            bool display = (bool)Runtime("WorkshopModels").GetMethod("IsDisplay", Static).Invoke(null, new object[] { id });
            bool gadget = (bool)Runtime("WorkshopModels").GetMethod("IsGadget", Static).Invoke(null, new object[] { id });
            for (int twice = 0; twice < 2; twice++) // WorkshopWorld attaches on every refresh that builds a model: attaching again adds nothing
            {
                Runtime("WorkshopDisplay").GetMethod("Attach").Invoke(null, new object[] { model });
                Runtime("WorkshopGadget").GetMethod("Attach").Invoke(null, new object[] { model });
                if (id == "business_sign") Runtime("WorkshopSign").GetMethod("Attach").Invoke(null, new object[] { model });
            }
            var action = Anchor(model, "Action");
            var displays = model.GetComponents(Runtime("WorkshopDisplay")); var gadgets = model.GetComponents(Runtime("WorkshopGadget")); var signs = model.GetComponents(Runtime("WorkshopSign"));
            Assert.AreEqual(display ? 1 : 0, displays.Length, id); Assert.AreEqual(gadget ? 1 : 0, gadgets.Length, id); Assert.AreEqual(id == "business_sign" ? 1 : 0, signs.Length, id);
            var zones = model.GetComponentsInChildren(zoneType, true);
            // the hoop is scored by throwing a bag through it: nothing to press
            Assert.AreEqual(id == "bag_hoop" ? 0 : 1, zones.Length, id + ": one deliberate press-E volume, never one per part");
            if (zones.Length == 0) continue;
            Assert.AreSame(action, zones[0].transform, id + ": the prompt belongs where the player stands");
            var target = (MonoBehaviour)zoneType.GetField("targetOverride").GetValue(zones[0]);
            Assert.AreSame(display ? displays[0] : gadget ? gadgets[0] : signs[0], target, id);
            Assert.IsTrue(Runtime("IInteractable").IsInstanceOfType(target), id);
            Assert.AreEqual("OnFoot", zoneType.GetField("mode").GetValue(zones[0]).ToString(), id);
            Assert.IsEmpty(model.GetComponentsInChildren<Collider>(true), id + ": a zone is a volume test, not a collider");
            if (display) Assert.NotNull(Anchor(model, "DisplaySlot0"), id + " needs somewhere to put the find");
        }
    }

    [Test]
    public void AHalfPrintedPieceShowsEverythingAgainWhenItIsSwitchedOff()
    {
        var model = Build("grand_display");
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        Assert.IsTrue(renderers.All(r => r.enabled));
        // a hidden depot's piece is never "printing": asking it to print in leaves it whole
        model.GetType().GetMethod("PrintIn").Invoke(model, new object[] { 3.2f });
        model.GetType().GetMethod("PrintIn").Invoke(model, new object[] { 0f });
        Assert.IsTrue(renderers.All(r => r.enabled), "PrintIn on an inactive piece must not hide anything");
        // interrupted half way (the player walked off, the lot was hidden): everything shows
        for (int i = 0; i < renderers.Length; i += 2) renderers[i].enabled = false;
        model.GetType().GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(model, null);
        Assert.IsTrue(renderers.All(r => r.enabled), "never leave a half-printed piece");
        CollectionAssert.AreEqual(renderers, model.GetComponentsInChildren<Renderer>(true), "printing in shows parts, it never makes any");
    }

    // ------------------------------------------------------------------ where the pieces stand: inside

    // The garage as authored (garage-local metres): its walls, and what already stands along them.
    const float WallWest = -6.92f, WallEast = 6.76f, WallSouth = -5.0f, WallNorth = 4.91f;
    static readonly (string name, float x0, float x1, float z0, float z1)[] Fixtures =
    {
        ("the south wall's west pillar", -3.81f, -2.55f, WallSouth, -4.38f),
        ("the south wall's east pillar", 2.12f, 3.38f, WallSouth, -4.38f),
        ("the east wall's pillar", 6.09f, WallEast, -1.06f, 0.20f),
        ("the north wall's pillar", -1.89f, -0.63f, 4.29f, WallNorth),
        ("the tipping floor's box", 4.14f, 6.66f, -4.66f, -1.54f),
        ("the raised office step", 2.84f, WallEast, 1.06f, WallNorth),
        ("Magnus's cart", 2.55f, 4.00f, 0.00f, 1.99f),
    };
    const float FixtureMargin = 0.2f;

    [Test]
    public void FourPiecesStandOnTheStreetAndTheOtherSevenInside()
    {
        CollectionAssert.AreEquivalent(new[] { "business_sign", "doorbell", "bag_hoop", "spring_launcher" }, Street);
        CollectionAssert.AreEquivalent(new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display", "sorting_chute", "break_sofa", "scrap_radio" }, Indoor);
        foreach (string other in new[] { "junk_sculpture", "sculpture_tower", "sculpture_monument", "monument", "made_up", "", null }) Assert.IsFalse(StreetRecipe(other), "'" + other + "'");
    }

    [TestCase(-1)] [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void TheSevenIndoorPiecesStandInsideTheWallsOnTheFloorClearOfTheGaragesFixtures(int lot)
    {
        Vector3 origin = Origin(lot); float floor = World<float>("FloorY");
        if (lot < 0) Assert.AreEqual(Vector3.zero, origin, "the solo garage sits at the origin"); else Assert.AreEqual(Layout<Vector3>("InteriorOrigin", lot), origin);
        foreach (string id in Indoor)
        {
            Vector3 at = Placement(lot, id).position - origin; // garage-local
            Assert.That(at.x, Is.InRange(WallWest, WallEast), id + " is outside the east / west walls");
            Assert.That(at.z, Is.InRange(WallSouth, WallNorth), id + " is outside the north / south walls");
            if (id == "wall_display") Assert.That(at.y, Is.GreaterThan(floor + 1f).And.LessThan(floor + 2.5f), "the wall display hangs on the wall, at eye height");
            else Assert.AreEqual(floor, at.y, 0.001f, id + " stands on the garage floor");
            foreach (var box in Fixtures)
            {
                bool inside = at.x > box.x0 - FixtureMargin && at.x < box.x1 + FixtureMargin && at.z > box.z0 - FixtureMargin && at.z < box.z1 + FixtureMargin;
                Assert.IsFalse(inside, id + " at (" + at.x.ToString("0.00") + ", " + at.z.ToString("0.00") + ") stands in " + box.name);
            }
            // the same piece stands in the same place in every garage: only the garage moves
            Vector3 solo = Placement(-1, id).position;
            Assert.That(Vector3.Distance(solo, at), Is.LessThan(0.001f), id);
            Assert.That(Quaternion.Angle(Placement(-1, id).rotation, Placement(lot, id).rotation), Is.LessThan(0.01f), id);
        }
    }

    [TestCase(-1)] [TestCase(0)] [TestCase(3)]
    public void NoIndoorPieceStandsInAnotherSinksIntoTheFloorOrLeavesNowhereToStand(int lot)
    {
        Vector3 origin = Origin(lot);
        var boxes = new Dictionary<string, Bounds>();
        foreach (string id in Indoor)
        {
            // (how far a piece's parts reach into the wall it leans on is a matter for screenshots, not for this test: the walls are
            // pinned for the pivots above; the legacy round primitives make some discs wider than their authored diameter)
            var bounds = PartBounds(Posed(lot, id)); boxes[id] = bounds;
            Assert.GreaterOrEqual(bounds.min.y - origin.y, World<float>("FloorY") - 0.02f, id + " sinks into the floor");
            Assert.LessOrEqual(bounds.max.y - origin.y, 4.3f, id + " goes through the roof");
            // where the player stands to use it is inside the garage too
            Vector3 action = Anchor(Posed(lot, id), "Action").position - origin;
            Assert.That(action.x, Is.InRange(WallWest + 0.3f, WallEast - 0.3f), id + ": nowhere to stand"); Assert.That(action.z, Is.InRange(WallSouth + 0.3f, WallNorth - 0.3f), id + ": nowhere to stand");
        }
        var ids = boxes.Keys.ToArray();
        for (int a = 0; a < ids.Length; a++) for (int b = a + 1; b < ids.Length; b++)
        {
            var shrunk = boxes[ids[a]]; shrunk.Expand(-0.004f); // touching is fine, overlapping is not
            Assert.IsFalse(shrunk.Intersects(boxes[ids[b]]), ids[a] + " stands in " + ids[b]);
        }
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void IndoorPiecesKeepTheExistingBusinessOwnershipBounds(int lot)
    {
        var pocket = Layout<Bounds>("InteriorBounds", lot);
        foreach (string id in Indoor)
        {
            Vector3 at = Placement(lot, id).position;
            Assert.IsTrue(pocket.Contains(at), id);
            Assert.AreEqual(lot, Layout<int>("InteriorAt", at), id + " must count as inside its own business's shop");
            Assert.AreEqual(-1, Layout<int>("PadAt", at), id + " is not on anybody's street pad");
        }
        foreach (string id in Street) Assert.AreEqual(-1, Layout<int>("InteriorAt", Placement(lot, id).position), id + " is out on the street, in nobody's interior");
    }

    // ------------------------------------------------------------------ where the pieces stand: the street

    static Pose MonumentPose(int streetLot) => (Pose)Runtime("WorkshopWorld").GetMethod("MonumentPose", Static).Invoke(null, new object[] { streetLot });
    static Pose HutchPose(int lot) => (Pose)Runtime("HutchWorld").GetMethod("PoseFor", Static).Invoke(null, new object[] { lot });

    [TestCase(-1)] [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void StreetPiecesTheMonumentAndTheHutchShareTheLotsPadClearOfEachOtherAndOfTheWayOut(int lot)
    {
        int street = StreetLot(lot); var pad = Layout<Rect>("PadRect", street);
        var slots = new Dictionary<string, Vector3>();
        foreach (string id in Street) slots[id] = Placement(lot, id).position;
        slots["the Monument"] = MonumentPose(lot).position; slots["the Critter Hutch"] = HutchPose(lot).position;
        foreach (var slot in slots)
        {
            Assert.IsTrue(pad.Contains(new Vector2(slot.Value.x, slot.Value.z)), slot.Key + " is off lot " + street + "'s pad");
            Assert.AreEqual(street, Layout<int>("PadAt", slot.Value), slot.Key + " stands on the wrong lot");
        }
        // the legacy solo garage (-1) borrows the legacy shop's lot for its street side
        if (lot < 0) foreach (string id in Street) Assert.That(Vector3.Distance(Placement(street, id).position, slots[id]), Is.LessThan(0.001f), id);
        var names = slots.Keys.ToArray();
        for (int a = 0; a < names.Length; a++) for (int b = a + 1; b < names.Length; b++)
        {
            float apart = names[a] == "the Monument" || names[b] == "the Monument" ? 2.5f : 1.5f;
            Assert.GreaterOrEqual(Flat(slots[names[a]], slots[names[b]]), apart, names[a] + " and " + names[b] + " are on top of each other");
        }
        // the ways in and out of the shop
        var ways = new Dictionary<string, Vector3>
        {
            { "where you walk out", Layout<Pose>("StreetOnFoot", street).position }, { "where the truck stands", Layout<Pose>("StreetTruck", street).position },
            { "where a new truck rolls out", Layout<Pose>("StreetTruckRollout", street).position },
        };
        for (int nth = 0; nth < 4; nth++) ways["spawn point " + nth] = Layout<Pose>("StreetSpawn", street, nth).position;
        foreach (var way in ways)
        {
            Assert.GreaterOrEqual(Flat(slots["bag_hoop"], way.Value), 2f, "the hoop blocks " + way.Key);
            Assert.GreaterOrEqual(Flat(slots["spring_launcher"], way.Value), 2f, "the launcher blocks " + way.Key);
            Assert.GreaterOrEqual(Flat(slots["the Monument"], way.Value), 2.5f, "the Monument blocks " + way.Key);
        }
        // the Monument's plinth is up to 1.8 m in radius: all of it on the pad, and clear of the walk-up hatch
        Vector3 hatch = Layout<Pose>("StreetPose", street, Runtime("HaulerTuning").GetField("HatchLocal").GetValue(null), 0f).position;
        Assert.GreaterOrEqual(Flat(slots["the Monument"], hatch), 2.5f, "the Monument blocks the walk-up hatch");
        for (int i = 0; i < 12; i++)
        {
            Vector3 rim = slots["the Monument"] + Quaternion.Euler(0, i * 30f, 0) * Vector3.forward * 1.8f;
            Assert.IsTrue(pad.Contains(new Vector2(rim.x, rim.z)), "the Monument's plinth hangs over the edge of the pad");
        }
        // every piece faces a way that reads from the street frame: upright, never tipped
        foreach (string id in Street) Assert.That(Vector3.Angle(Placement(lot, id).rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f), id);
        Assert.That(Vector3.Angle(MonumentPose(lot).rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f));
    }

    [TestCase(-1)] [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void TheLauncherFacesTheHoopExactlyOneLaunchAway(int lot)
    {
        float launch = World<float>("LaunchDistance"); Assert.Greater(launch, 2f);
        Pose launcher = Placement(lot, "spring_launcher"), hoop = Placement(lot, "bag_hoop");
        Assert.AreEqual(launch, Flat(launcher.position, hoop.position), 0.02f, "the hoop stands exactly one launch from the launcher");
        Assert.AreEqual(launcher.position.y, hoop.position.y, 0.001f, "on the same ground");
        Vector3 toHoop = (hoop.position - launcher.position).normalized;
        Assert.Greater(Vector3.Dot(launcher.rotation * Vector3.forward, toHoop), 0.999f, "the launcher's forward points at the hoop");
        Assert.Greater(Vector3.Dot(hoop.rotation * Vector3.forward, -toHoop), 0.999f, "the hoop faces back at the launcher");
        // the model's aiming point is the hoop: the same number in the model and in the placement
        var model = Posed(lot, "spring_launcher");
        Assert.AreEqual(launch, Anchor(model, "Landing").localPosition.z, 0.0001f, "the Landing anchor is one launch in front of the muzzle's foot");
        Assert.That(Flat(Anchor(model, "Landing").position, hoop.position), Is.LessThan(0.02f), "the bag lands where the hoop stands");
        Assert.AreEqual(Placement(lot, "bag_hoop").position, Layout<Pose>("StreetPose", StreetLot(lot), World<Vector3>("HoopLocal"), 0f).position, "the hoop's slot is the one place both are measured from");
    }

    [TestCase(-1)] [TestCase(0)] [TestCase(2)]
    public void LauncherArcDescendsThroughTheHoop(int lot)
    {
        var launcher = Posed(lot, "spring_launcher");
        var hoop = Posed(lot, "bag_hoop");
        Vector3 from = Anchor(launcher, "Muzzle").position, to = Anchor(launcher, "Landing").position;
        Vector3 ring = Anchor(hoop, "RingCenter").position, gravity = new Vector3(0, -9.81f, 0);
        const float seconds = 1.1f; // the host's flight time (HandsNet.HostWorkshopGadget)
        Vector3 velocity = (to - from) / seconds - gravity * (seconds * .5f);
        float apex = -velocity.y / gravity.y;
        float top = (from + velocity * apex + .5f * gravity * apex * apex).y - launcher.transform.position.y;
        Assert.That(top, Is.InRange(ring.y - launcher.transform.position.y + 0.3f, 3.5f), "the bag rises clear over the rim, and not into orbit");
        float crossing = (velocity.y + Mathf.Sqrt(velocity.y * velocity.y + 2f * gravity.y * (ring.y - from.y))) / -gravity.y;
        Vector3 atRing = from + velocity * crossing + .5f * gravity * crossing * crossing;
        Assert.That(crossing, Is.GreaterThan(apex), "it comes DOWN through the hoop");
        Assert.That(Vector2.Distance(new Vector2(atRing.x, atRing.z), new Vector2(ring.x, ring.z)), Is.LessThan(.15f), "through the ring, not past it");
        Assert.AreEqual(StreetLot(lot), Layout<int>("PadAt", to), "and lands on its own lot");
    }

    // ------------------------------------------------------------------ the Monument's ten models

    // (max height, max footprint radius) in metres, per tier.
    static readonly (float height, float radius)[] MonumentLimits =
    {
        (1.2f, 0.5f), (1.9f, 0.55f), (2.5f, 0.8f), (2.9f, 1.0f), (3.4f, 1.2f), (4.0f, 1.25f), (4.6f, 1.35f), (5.2f, 1.7f), (6.0f, 1.7f), (7.0f, 1.8f),
    };

    /// <summary>A Monument tier under an ACTIVE root standing at <paramref name="at"/>, measured the way the first rendered frame
    /// shows it: the plaque's labels are laid out first. (As BUILT, a label's renderer still holds the mesh MakeLabel made
    /// before Kit.Text gave it its real box and auto-size, which is far wider than the plaque; TMP re-lays it out before it renders.)</summary>
    Component BuildMonument(int tier, Vector3 at = default)
    {
        var root = new GameObject("Monument presentation test"); roots.Add(root);
        root.transform.position = at;
        var model = (Component)Runtime("WorkshopModels").GetMethod("BuildMonument", Static).Invoke(null, new object[] { tier, root.transform, new Color(.24f, .48f, .65f), "TEST BUSINESS" });
        if (model != null) LayOutLabels(model);
        return model;
    }
    static void LayOutLabels(Component model)
    {
        foreach (var label in model.GetComponentsInChildren<Component>(true))
            if (label != null && label.GetType().Name == "TextMeshPro") label.GetType().GetMethod("ForceMeshUpdate").Invoke(label, new object[] { true, true });
    }
    static Bounds RendererBounds(Component model)
    {
        var renderers = model.GetComponentsInChildren<Renderer>(true); Assert.Greater(renderers.Length, 0);
        var bounds = renderers[0].bounds; foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)] [TestCase(10)]
    public void EveryMonumentTierIsARendererOnlyModelInsideItsHeightAndFootprint(int tier)
    {
        var model = BuildMonument(tier);
        Assert.NotNull(model, "WorkshopModels.BuildMonument(" + tier + ") built nothing");
        Assert.AreEqual("monument", model.GetType().GetField("recipeId").GetValue(model), "the Monument's model id is MonumentCatalog.NodeId");
        Assert.AreEqual(Runtime("MonumentCatalog").GetField("NodeId").GetRawConstantValue(), model.GetType().GetField("recipeId").GetValue(model));
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        Assert.That(renderers.Length, Is.GreaterThanOrEqualTo(8), "a trophy, not a placeholder");
        Assert.IsEmpty(model.GetComponentsInChildren<Collider>(true), "every part is visual only");
        Assert.IsEmpty(model.GetComponentsInChildren<Rigidbody>(true));
        Assert.IsEmpty(model.GetComponentsInChildren(Runtime("Carryable"), true));
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true)) Assert.NotNull(filter.sharedMesh, filter.name + " has no mesh");
        foreach (var renderer in renderers) Assert.NotNull(renderer.sharedMaterial, renderer.name + " has no material");
        Assert.NotNull(Anchor(model, "Action"), "where the player stands to look at it"); Assert.NotNull(Anchor(model, "Top"), "where the floating text goes");
        var (maxHeight, maxRadius) = MonumentLimits[tier - 1];
        var bounds = RendererBounds(model); Vector3 foot = model.transform.position;
        float height = bounds.max.y - foot.y;
        float radius = Mathf.Max(Mathf.Max(foot.x - bounds.min.x, bounds.max.x - foot.x), Mathf.Max(foot.z - bounds.min.z, bounds.max.z - foot.z));
        Assert.That(height, Is.GreaterThan(0.5f).And.LessThanOrEqualTo(maxHeight * 1.05f), "tier " + tier + " is " + height.ToString("0.00") + " m tall; its limit is " + maxHeight + " m");
        Assert.That(radius, Is.GreaterThan(0.2f).And.LessThanOrEqualTo(maxRadius * 1.05f), "tier " + tier + " reaches " + radius.ToString("0.00") + " m from its centre; its limit is " + maxRadius + " m");
        Assert.GreaterOrEqual(bounds.min.y - foot.y, -0.05f, "tier " + tier + " stands ON the ground");
        float top = Anchor(model, "Top").position.y - foot.y;
        Assert.That(top, Is.GreaterThan(0.5f).And.LessThanOrEqualTo(maxHeight * 1.05f), "the Top anchor is on the model");
        Assert.That(Flat(Anchor(model, "Action").position, foot), Is.GreaterThan(0.3f).And.LessThan(maxRadius + 1.5f), "the Action anchor is beside it, not inside or miles off");
    }

    [Test]
    public void TheMonumentGrowsMoreIntricateAndOutOfRangeTiersClampInsteadOfThrowing()
    {
        int Parts(int tier) { var model = BuildMonument(tier); Assert.NotNull(model, "tier " + tier); return model.GetComponentsInChildren<Renderer>(true).Length; }
        int first = Parts(1), last = Parts(10);
        Assert.Greater(last, first, "tier 10 must be made of strictly more parts than tier 1");
        Assert.AreEqual(first, Parts(0), "tier 0 is drawn as tier 1"); Assert.AreEqual(first, Parts(-4)); Assert.AreEqual(last, Parts(11), "tier 11 is drawn as tier 10"); Assert.AreEqual(last, Parts(999));
        Assert.Greater(RendererBounds(BuildMonument(10)).size.y, RendererBounds(BuildMonument(1)).size.y, "and it is the tallest thing on the street");
        // a model stands where it is put: the same shape around any foot (bounds are measured from the pivot, not from the world origin)
        var here = RendererBounds(BuildMonument(4)); var there = RendererBounds(BuildMonument(4, new Vector3(120f, -3f, 45f)));
        Assert.That(Vector3.Distance(here.size, there.size), Is.LessThan(0.01f)); Assert.That(Vector3.Distance(here.center + new Vector3(120f, -3f, 45f), there.center), Is.LessThan(0.01f));
        // a business name that is missing or silly never breaks the plaque
        foreach (string name in new[] { null, "", "A", new string('W', 60) })
        {
            var root = new GameObject("Monument name test"); roots.Add(root);
            Assert.NotNull(Runtime("WorkshopModels").GetMethod("BuildMonument", Static).Invoke(null, new object[] { 3, root.transform, Color.white, name }), "name '" + name + "'");
        }
    }

    [TestCase(-1)] [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void TheMonumentStandsAtOneCanonicalStreetSlotPerLot(int lot)
    {
        int street = StreetLot(lot);
        var expected = Layout<Pose>("StreetPose", street, World<Vector3>("MonumentLocal"), World<float>("MonumentYaw"));
        var pose = MonumentPose(lot);
        Assert.That(Vector3.Distance(expected.position, pose.position), Is.LessThan(0.001f), "authority never takes a client's word for where a Monument stands");
        Assert.That(Quaternion.Angle(expected.rotation, pose.rotation), Is.LessThan(0.01f));
        Assert.AreEqual(Layout<Vector3>("DoorPoint", street).y, pose.position.y, 0.001f, "on the ground of its lot");
    }
}

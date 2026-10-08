using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// TownLife layer contracts (parcel plan, boundary network, published scene). Read-only: the live-town tests capture the
/// loaded Neighborhood scene and plan in memory; nothing is built, moved, saved or published. They are skipped
/// (Assert.Ignore) when the town scene is not loaded, so the fixture is safe in any Editor state.
/// Reflection-only access, like every other city test (this assembly cannot reference the Editor assembly).
/// </summary>
public class TownLifeTests
{
    const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type E(string name) => Type.GetType(name + ", Assembly-CSharp-Editor", true);
    static object Get(object o, string member)
    {
        var type = o.GetType();
        var field = type.GetField(member); if (field != null) return field.GetValue(o);
        var property = type.GetProperty(member); Assert.NotNull(property, type.Name + "." + member + " is missing");
        return property.GetValue(o);
    }
    static T Get<T>(object o, string member) => (T)Get(o, member);
    static object[] Items(object array) => ((IEnumerable)array).Cast<object>().ToArray();
    static List<string> Problems(object result) => ((IEnumerable)result).Cast<object>().Select(x => x?.ToString()).ToList();

    // One capture + plan per fixture run (the plan takes about two seconds).
    static Transform town; static object scene, plan, fences; static bool captured;

    static void RequireTown()
    {
        if (!captured)
        {
            captured = true; town = null; scene = plan = fences = null;
            town = (Transform)E("TownLifeScene").GetMethod("FindTown", Pub).Invoke(null, null);
            if (town == null) town = OpenPublishedTown();
            if (town != null)
            {
                scene = E("TownLifeScene").GetMethod("Capture", Pub).Invoke(null, new object[] { town });
                var input = scene.GetType().GetMethod("ToPlanInput").Invoke(scene, null);
                plan = E("TownLifePlan").GetMethod("Build", Pub, null, new[] { input.GetType() }, null).Invoke(null, new[] { input });
                fences = E("TownLifeFences").GetMethod("Plan", Pub, null, new[] { scene.GetType(), plan.GetType() }, null).Invoke(null, new[] { scene, plan });
            }
        }
        if (town == null) Assert.Ignore("Neighborhood.unity is not loaded; live-town TownLife checks skipped.");
    }

    // The EditMode runner replaces every open scene by an empty untitled one for the whole run (CreateBootstrapSceneTask),
    // so the town is NEVER loaded when a test starts: without this the five live-town tests below were always skipped
    // (found at integration). Load the saved, published Neighborhood.unity read-only for the fixture, exactly the way
    // CityEditTransactionTests swaps scenes (Single mode; additive scenes are prohibited beside the runner's untitled
    // one), and put the previous setup back afterwards. Never replaces a dirty saved scene.
    const string TownScenePath = "Assets/Scenes/Neighborhood.unity";
    static UnityEditor.SceneManagement.SceneSetup[] previousSetup; static bool openedTown;

    static Transform OpenPublishedTown()
    {
        if (!System.IO.File.Exists(TownScenePath)) return null;
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var loaded = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (!string.IsNullOrEmpty(loaded.path) && loaded.isDirty) return null;
        }
        previousSetup = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(TownScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
        openedTown = true;
        return (Transform)E("TownLifeScene").GetMethod("FindTown", Pub).Invoke(null, null);
    }

    [OneTimeSetUp] public void ResetCapture() { captured = false; openedTown = false; previousSetup = null; }
    [OneTimeTearDown]
    public void DropCapture()
    {
        captured = false; town = null; scene = plan = fences = null;
        if (!openedTown) return;
        openedTown = false;
        if (previousSetup != null && previousSetup.Length > 0) UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
        else UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        previousSetup = null;
    }

    // ---------------------------------------------------------------------------------------- pure contracts

    [Test]
    public void StableHashIsFnv1aAndIndependentOfTheProcess()
    {
        var hash = E("TownLifeContext").GetMethod("StableHash", Pub);
        Assert.AreEqual(unchecked((int)2166136261), (int)hash.Invoke(null, new object[] { "" }));
        Assert.AreEqual(unchecked((int)0xE40C292C), (int)hash.Invoke(null, new object[] { "a" }));
        Assert.AreEqual((int)hash.Invoke(null, new object[] { "yards/theme/maple-56" }), (int)hash.Invoke(null, new object[] { "yards/theme/maple-56" }));
        Assert.AreNotEqual((int)hash.Invoke(null, new object[] { "yards/theme/maple-56" }), (int)hash.Invoke(null, new object[] { "yards/theme/maple-58" }));
    }

    [Test]
    public void ModuleAliasesSelectTheDocumentedSets()
    {
        var parse = E("TownLifeOptions").GetMethod("Parse", Pub);
        object all = parse.Invoke(null, new object[] { "all", "" }), everything = parse.Invoke(null, new object[] { "everything", "districts=acres;hidePark=0" });
        object none = parse.Invoke(null, new object[] { "none", null });
        var module = all.GetType().GetMethod("Module"); var district = all.GetType().GetMethod("District");
        foreach (string name in new[] { "paint", "park", "meadows", "streets", "yards", "edges" })
        {
            Assert.IsTrue((bool)module.Invoke(all, new object[] { name }), "'all' must build " + name);
            Assert.IsTrue((bool)module.Invoke(everything, new object[] { name }), "'everything' must build " + name);
            Assert.IsFalse((bool)module.Invoke(none, new object[] { name }), "'none' must build nothing");
        }
        Assert.IsTrue(((IEnumerable)Get(everything, "modules")).Cast<string>().Contains("fences"), "'everything' also draws the planned fences");
        Assert.IsFalse(((IEnumerable)Get(all, "modules")).Cast<string>().Contains("fences"));
        Assert.IsTrue((bool)district.Invoke(everything, new object[] { "mildly_pleasant" }));
        Assert.IsFalse((bool)district.Invoke(everything, new object[] { "minivan_meadows" }));
        Assert.IsTrue((bool)district.Invoke(all, new object[] { "minivan_meadows" }));
    }

    [Test]
    public void OccupancyReservationsBlockByKindAndAnswerSpacingQueries()
    {
        var type = E("TownLifeOccupancy");
        var occupancy = Activator.CreateInstance(type, Rect.MinMaxRect(4900, 4900, 5100, 5100));
        Type what = type.GetNestedType("What"), block = type.GetNestedType("Block");
        object solid = Enum.Parse(what, "Solid"), flat = Enum.Parse(what, "Flat");
        var free = type.GetMethod("Free"); var within = type.GetMethod("Within");
        var centre = new Vector3(5000, 0, 5000);
        Assert.IsTrue((bool)free.Invoke(occupancy, new[] { centre, 1f, solid }));
        Assert.IsFalse((bool)free.Invoke(occupancy, new[] { (object)new Vector3(4000, 0, 5000), 1f, solid }), "Outside the working area is never free.");
        type.GetMethod("ReserveCircle").Invoke(occupancy, new[] { centre, 2f, "tree.test", Enum.Parse(block, "Solids") });
        Assert.IsFalse((bool)free.Invoke(occupancy, new[] { (object)new Vector3(5002.5f, 0, 5000), 1f, solid }));
        Assert.IsTrue((bool)free.Invoke(occupancy, new[] { (object)new Vector3(5002.5f, 0, 5000), 1f, flat }), "A Solids reservation leaves flat dressing free.");
        Assert.IsTrue((bool)free.Invoke(occupancy, new[] { (object)new Vector3(5003.5f, 0, 5000), 1f, solid }));
        Assert.IsTrue((bool)within.Invoke(occupancy, new object[] { new Vector3(5006, 0, 5000), 5f, "tree" }), "'tree' must match 'tree.test'.");
        Assert.IsFalse((bool)within.Invoke(occupancy, new object[] { new Vector3(5006, 0, 5000), 5f, "tre" }));
        type.GetMethod("ReserveLine").Invoke(occupancy, new[] { (object)new Vector3(5010, 0, 4990), new Vector3(5010, 0, 5010), 1f, "paving.test", Enum.Parse(block, "Everything") });
        Assert.IsFalse((bool)free.Invoke(occupancy, new[] { (object)new Vector3(5010.5f, 0, 5003), 0.2f, flat }));
        Assert.AreEqual(2, (int)type.GetProperty("Count").GetValue(occupancy));
    }

    // -------------------------------------------------------------------------------------------- live town

    [Test]
    public void CaptureReadsEveryStarterHouseholdWithACompleteLotFrame()
    {
        RequireTown();
        var houses = Items(Get(scene, "houses"));
        Assert.AreEqual(59, houses.Length, "Acres (37) + Meadows (22) households.");
        var seen = new HashSet<string>(); string previous = null;
        foreach (var house in houses)
        {
            var lot = Get(house, "plan"); string id = Get<string>(lot, "stopId");
            Assert.IsTrue(seen.Add(id), "duplicate " + id);
            if (previous != null) Assert.Less(string.CompareOrdinal(previous, id), 0, "houses are sorted by stopId");
            previous = id;
            Vector3 right = Get<Vector3>(lot, "right"), inward = Get<Vector3>(lot, "inward");
            Assert.That(right.magnitude, Is.EqualTo(1f).Within(.001f), id); Assert.That(inward.magnitude, Is.EqualTo(1f).Within(.001f), id);
            Assert.That(Vector3.Dot(right, inward), Is.EqualTo(0f).Within(.001f), id);
            float front = Get<float>(house, "frontDepth"), rear = Get<float>(house, "rearDepth");
            Assert.Greater(front, 5f, id + ": the building stands behind the sidewalk"); Assert.Greater(rear, front + 4f, id);
            Assert.Less(Get<float>(house, "leftExtent"), Get<float>(house, "rightExtent"), id);
            Assert.IsTrue(Get<bool>(lot, "hasDrive"), id + ": every lot has a driveway slab, also where the route says hasDrive = false");
            Assert.Greater(Vector3.Distance(Get<Vector3>(lot, "driveA"), Get<Vector3>(lot, "driveB")), 6f, id + ": the drive line is the slab, not the kerb ramp");
            Assert.GreaterOrEqual(Get<Vector3[]>(lot, "walk").Length, 2, id);
        }
        Assert.AreEqual(6, Items(Get(scene, "kidPens")).Length);
        Assert.AreEqual(2, Items(Get(scene, "courts")).Length);
        Assert.AreEqual(2, Items(Get(scene, "crossings")).Length);
    }

    [Test]
    public void GroundHeightFollowsTheRiverBankAndTheShore()
    {
        RequireTown();
        var ground = scene.GetType().GetMethod("GroundY"); var flat = scene.GetType().GetMethod("IsFlat");
        var park = Get(scene, "park"); var landmark = Get<Vector3>(park, "landmark");
        Assert.That((float)ground.Invoke(scene, new object[] { landmark }), Is.EqualTo(-.08f).Within(.0001f));
        Assert.IsTrue((bool)flat.Invoke(scene, new object[] { landmark, 5f }));
        var river = Get<Vector3[]>(scene, "river"); var middle = river[river.Length / 2];
        Assert.Less((float)ground.Invoke(scene, new object[] { middle }), -4f, "The river bed is about 5 m down.");
        Assert.IsFalse((bool)flat.Invoke(scene, new object[] { middle, 0f }));
        var riverTop = Get<Vector3[]>(scene, "riverBankTop"); var coastTop = Get<Vector3[]>(scene, "coastBankTop");
        Assert.Greater(riverTop.Length, 50); Assert.Greater(coastTop.Length, 30);
        foreach (var p in riverTop.Concat(coastTop))
            Assert.That((float)ground.Invoke(scene, new object[] { p }), Is.EqualTo(-.08f).Within(.01f), "bank-top points lie on level grass: " + p);
    }

    [Test]
    public void ParcelPlanFromTheLiveTownPassesSelfCheckAndTilesEveryLot()
    {
        RequireTown();
        // SelfCheck has two overloads: bind the one-argument form by its parameter type.
        var check = E("TownLifePlan").GetMethod("SelfCheck", Pub, null, new[] { plan.GetType() }, null);
        Assert.NotNull(check, "TownLifePlan.SelfCheck(Result)");
        var problems = Problems(check.Invoke(null, new[] { plan }));
        Assert.IsEmpty(problems, string.Join("\n", problems));
        var parcels = Items(Get(plan, "parcels")); var houses = Items(Get(scene, "houses"));
        Assert.AreEqual(59, parcels.Length, "One parcel per household, nothing left over.");
        Assert.AreEqual(houses.Length, parcels.Length);
        var chains = new HashSet<string>();
        for (int i = 0; i < parcels.Length; i++)
        {
            string id = Get<string>(Get(houses[i], "plan"), "stopId");
            Assert.AreEqual(i, Get<int>(parcels[i], "index"));
            Assert.AreEqual(id, Get<string>(Get(parcels[i], "house"), "stopId"), "parcels[i] belongs to scene.houses[i]");
            Assert.GreaterOrEqual(Get<Vector3[]>(parcels[i], "outline").Length, 3, id);
            Assert.Greater(Get<float>(parcels[i], "area"), 100f, id);
            chains.Add(Get<string>(parcels[i], "chain"));
        }
        Assert.GreaterOrEqual(chains.Count, 8, "Parcels are grouped into frontage chains (one future yard marker each).");
    }

    [Test]
    public void FencePlanPassesItsSelfCheck()
    {
        RequireTown();
        Assert.NotNull(fences, "TownLifeFences.Plan returned null");
        var runs = Items(Get(fences, "runs")); var chains = Items(Get(fences, "chains"));
        if (runs.Length == 0) Assert.Ignore("TownLifeFences.Plan is still the empty placeholder.");
        float minRun = (float)E("TownLifeFences").GetField("MinRun").GetRawConstantValue();
        var owned = new HashSet<string>();
        foreach (var chain in chains)
            foreach (string id in Get<string[]>(chain, "stopIds")) Assert.IsTrue(owned.Add(id), "Household " + id + " is in two fence chains (two yard markers would claim it).");
        foreach (var run in runs)
        {
            Vector3 a = Get<Vector3>(run, "a"), b = Get<Vector3>(run, "b");
            Assert.GreaterOrEqual(Vector3.Distance(a, b), minRun - .01f, "A fence run is shorter than MinRun at " + a);
            Assert.IsNotEmpty(Get<string[]>(run, "stopIds")); Assert.IsFalse(string.IsNullOrEmpty(Get<string>(run, "style")));
        }
        // The fences agent's own SelfCheck, whatever arguments it takes from (network, scene, plan).
        var check = E("TownLifeFences").GetMethods(Pub).Where(m => m.Name == "SelfCheck").OrderByDescending(m => m.GetParameters().Length).FirstOrDefault(m =>
            m.GetParameters().All(p => p.ParameterType.IsInstanceOfType(fences) || p.ParameterType.IsInstanceOfType(scene) || p.ParameterType.IsInstanceOfType(plan)));
        if (check == null) Assert.Ignore("TownLifeFences has no SelfCheck(network[, scene][, plan]); structural checks above passed.");
        var args = check.GetParameters().Select(p => p.ParameterType.IsInstanceOfType(fences) ? fences : p.ParameterType.IsInstanceOfType(scene) ? scene : plan).ToArray();
        var problems = Problems(check.Invoke(null, args));
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }

    [Test]
    public void PublishedTownCarriesTheTownLifeLayerAndValidYardMarkers()
    {
        RequireTown();
        var builder = E("TownLifeBuilder");
        string groupName = (string)builder.GetField("GroupName").GetRawConstantValue(), parkGroupName = (string)builder.GetField("ParkGroupName").GetRawConstantValue();
        int budget = (int)builder.GetField("ObjectBudget").GetRawConstantValue();
        var life = town.Find(groupName);
        Assert.NotNull(life, "NeighborhoodGen/" + groupName + " is missing: run Rubbish/City/Rebuild Town Life (or any city publish).");
        var park = town.Find("Park"); Assert.NotNull(park, "The Park group must survive (map bake and incremental publish need it).");
        var parkLife = park.Find(parkGroupName);
        Assert.NotNull(parkLife, "The rebuilt park lives under NeighborhoodGen/Park/" + parkGroupName + ".");
        int objects = life.GetComponentsInChildren<Transform>(true).Length - 1 + parkLife.GetComponentsInChildren<Transform>(true).Length;
        Assert.Greater(objects, 500, "A dressed town has far more than a handful of objects.");
        Assert.LessOrEqual(objects, budget, "TownLife GameObject budget.");
        // the legacy park is retired except the playground pen, what stands in it and the lemonade stand
        var retired = builder.GetMethod("IsRetiredParkLegacy", Pub);
        foreach (Transform child in park)
            Assert.IsFalse((bool)retired.Invoke(null, new object[] { child, scene }), "Legacy park object '" + child.name + "' should have been retired.");
        Assert.IsTrue(park.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)), "The lemonade stand stays by the south gate.");
        // every module group is a TL_ group, nothing is named like a lot (the map scanner draws every Lot_* as a lot),
        // and nothing sits under a house holder
        foreach (Transform group in life) Assert.IsTrue(group.name.StartsWith("TL_", StringComparison.Ordinal), "Unexpected group under TownLife: " + group.name);
        foreach (var root in new[] { life, parkLife })
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                Assert.IsFalse(t.name.StartsWith("Lot_", StringComparison.Ordinal), "A TownLife object is named like a lot: " + t.name);
        var lots = town.Find("CoastalLots");
        if (lots != null) foreach (Transform holder in lots)
            foreach (Transform child in holder)
                Assert.IsFalse(child.name.StartsWith("TL_", StringComparison.Ordinal), "TownLife object parented under " + holder.name);
        // every yard marker (the connected boundary network included) passes the game's own validation
        var errors = Problems(R("CoastalYardBoundary").GetMethod("Validate", Pub).Invoke(null, new object[] { town }));
        Assert.IsEmpty(errors, string.Join("\n", errors));
        var markers = town.GetComponentsInChildren(R("CoastalYardBoundary"), true);
        var claimed = new HashSet<string>();
        foreach (var marker in markers) foreach (string id in Get<string[]>(marker, "stopIds")) Assert.IsTrue(claimed.Add(id), "Two yard markers claim " + id);
        int starter = Items(Get(scene, "houses")).Count(h => claimed.Contains(Get<string>(Get(h, "plan"), "stopId")));
        Assert.GreaterOrEqual(starter, 40, "Most Acres / Meadows homes belong to a boundary marker.");
    }
}

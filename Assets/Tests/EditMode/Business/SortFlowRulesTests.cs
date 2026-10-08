using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// The garage sorting-flow rework's pure rules (Docs/Progression.md §6.1-§6.3): which bags the belt takes, what the
/// Junk Diverter sheds, Magnus's reach, the truck's TIP IT, street litter, the Auto-Tipper refund, the two sorting
/// positions and the career path's integrity. Reflection only (game code lives in Assembly-CSharp); no session, no saves.
/// </summary>
public class SortFlowRulesTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Missing(string name) => Type.GetType(name + ", Assembly-CSharp", false);
    static object Call(string type, string method, params object[] args)
    {
        foreach (var m in Runtime(type).GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            bool fits = true;
            var ps = m.GetParameters();
            for (int i = 0; i < ps.Length && fits; i++)
                fits = args[i] == null || ps[i].ParameterType.IsInstanceOfType(args[i]) || ps[i].ParameterType.IsByRef;
            if (fits) return m.Invoke(null, args);
        }
        Assert.Fail(type + "." + method + "/" + args.Length + " not found");
        return null;
    }
    static T Const<T>(string type, string field) => (T)Runtime(type).GetField(field, Static).GetValue(null);
    static object Enum(string type, string value) => System.Enum.Parse(Runtime(type), value);
    readonly List<Object> made = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
        made.Clear();
    }

    // ------------------------------------------------------------------ fixtures

    static object Item(string category, float kg, string uid)
    {
        var defs = (IList)Call("ItemCatalog", "InCategory", Enum("ItemCategory", category));
        Assert.Greater(defs.Count, 0, "catalog has no " + category);
        var def = defs[0];
        var item = Activator.CreateInstance(Runtime("ItemInstance"));
        item.GetType().GetField("defId").SetValue(item, def.GetType().GetField("id").GetValue(def));
        item.GetType().GetField("massKg").SetValue(item, kg);
        item.GetType().GetField("uid").SetValue(item, uid);
        return item;
    }

    static object Bag(string id, params object[] items)
    {
        var bag = Activator.CreateInstance(Runtime("BagContents"));
        bag.GetType().GetField("bagId").SetValue(bag, id);
        var list = (IList)bag.GetType().GetField("items").GetValue(bag);
        foreach (var i in items) list.Add(i);
        return bag;
    }

    static IList Items(object bag) => (IList)bag.GetType().GetField("items").GetValue(bag);
    static float Kg(object item) => (float)item.GetType().GetField("massKg").GetValue(item);
    static string Uid(object item) => (string)item.GetType().GetField("uid").GetValue(item);
    static string Category(object item)
    {
        var def = item.GetType().GetProperty("Def").GetValue(item);
        return def.GetType().GetField("category").GetValue(def).ToString();
    }

    static IList Split(object bag, out object kept)
    {
        var args = new[] { bag, null };
        var fell = (IList)Runtime("JunkDiverter").GetMethod("Split", Static).Invoke(null, args);
        kept = args[1];
        return fell;
    }

    // ------------------------------------------------------------------ the belt

    [TestCase(0, false, false)]
    [TestCase(0, true, false)]
    [TestCase(1, false, true)]
    [TestCase(1, true, false)]
    [TestCase(2, false, true)]
    [TestCase(2, true, true)]
    [TestCase(3, false, true)]
    [TestCase(3, true, true)]
    public void BeltTakesNormalBagsFromLevelOneAndHeavyBagsFromLevelTwo(int level, bool heavy, bool expected)
    {
        Assert.AreEqual(expected, (bool)Call("ConveyorRules", "Accepts", level, heavy));
    }

    [Test]
    public void BeltJudgesRealBagsByTheirHeavyTellAndNeverTakesLoosePiles()
    {
        object normal = Bag("sortflow/normal#tell=none", Item("Paper", 1f, "a"));
        object heavy = Bag("sortflow/big#tell=heavy", Item("Junk", 12f, "b"));
        Assert.IsFalse((bool)Call("ConveyorRules", "IsHeavy", normal));
        Assert.IsTrue((bool)Call("ConveyorRules", "IsHeavy", heavy));
        Assert.IsTrue((bool)Call("ConveyorRules", "Accepts", 1, normal));
        Assert.IsFalse((bool)Call("ConveyorRules", "Accepts", 1, heavy), "L1 leaves heavy bags on the pile");
        Assert.IsTrue((bool)Call("ConveyorRules", "Accepts", 2, heavy), "the Heavy-Duty Motor carries them");
        object loose = Bag("loose/1", Item("Paper", 1f, "c"));
        loose.GetType().GetField("sackVariant").SetValue(loose, -1);
        Assert.IsFalse((bool)Call("ConveyorRules", "Accepts", 3, loose), "loose cargo is not a bag");
        Assert.IsFalse((bool)Call("ConveyorRules", "Diverts", 2));
        Assert.IsTrue((bool)Call("ConveyorRules", "Diverts", 3));
        Assert.AreEqual(2, Const<int>("ConveyorRules", "QueueSlots"));
    }

    [Test]
    public void TheConveyorHasNoSlitterNoDirectFeedAndNoSortSpeedBonus()
    {
        Assert.IsNull(Runtime("ConveyorTray").GetMethod("CanStartAutoSlit", Instance));
        Assert.IsNull(Runtime("ConveyorTray").GetField("slitterBlade", Instance));
        Assert.IsNull(Runtime("ConveyorFeeder").GetMethod("TryTakeFromTruck", Instance));
        Assert.IsNull(Runtime("UpgradeEffects").GetProperty("DirectFeed", Static));
        Assert.IsNull(Runtime("UpgradeEffects").GetField("TwinBeltMagnusSpeed", Static), "the twin-belt Magnus speed factor went with the twin belt");
        Assert.AreEqual(1.25f * 1.4f, (float)Call("UpgradeEffects", "MagnusSpeedMultAt", 1, 2), 1e-5f, "rail motor × quad-claw is the whole speed contract");
    }

    // ------------------------------------------------------------------ junk diverter

    [Test]
    public void DiverterShedsOnlySmallJunkAndNeverTreasureHazardsOrRecyclables()
    {
        object smallJunk = Item("Junk", 0.8f, "j1"), edgeJunk = Item("Junk", 4f, "j2"), bigJunk = Item("Junk", 4.01f, "j3");
        object ring = Item("Valuable", 0.02f, "v1"), battery = Item("Hazard", 0.3f, "h1"), can = Item("Metal", 0.2f, "m1");
        object paper = Item("Paper", 0.1f, "p1"), bottle = Item("Glass", 0.4f, "g1"), jug = Item("Plastic", 0.1f, "pl1");
        foreach (var it in new[] { smallJunk, edgeJunk })
            Assert.IsTrue((bool)Call("JunkDiverter", "Sheds", it));
        foreach (var it in new[] { bigJunk, ring, battery, can, paper, bottle, jug })
            Assert.IsFalse((bool)Call("JunkDiverter", "Sheds", it), Category(it) + " " + Kg(it) + " kg must stay in the bag");

        object bag = Bag("sortflow/mixed", smallJunk, ring, edgeJunk, battery, bigJunk, can, paper, bottle, jug);
        var fell = Split(bag, out object kept);
        Assert.AreEqual(2, fell.Count);
        foreach (var it in fell) Assert.AreEqual("Junk", Category(it));
        CollectionAssert.AreEqual(new[] { "j1", "j2" }, UidsOf(fell));
        CollectionAssert.AreEqual(new[] { "v1", "h1", "j3", "m1", "p1", "g1", "pl1" }, UidsOf(Items(kept)), "order of what stays is unchanged");
        Assert.AreEqual(9, Items(bag).Count, "the original bag is not modified");
    }

    static List<string> UidsOf(IList items)
    {
        var list = new List<string>();
        foreach (var it in items) list.Add(Uid(it));
        return list;
    }

    [Test]
    public void ShedJunkIsTheSameInstancesSoItIsCreditedExactlyLikeSortedJunk()
    {
        object j1 = Item("Junk", 1.5f, "j1"), j2 = Item("Junk", 2.25f, "j2"), ring = Item("Valuable", 0.02f, "v1"), can = Item("Metal", 0.3f, "m1");
        object bag = Bag("sortflow/credit", j1, ring, j2, can);
        float before = (float)bag.GetType().GetProperty("TotalMassKg").GetValue(bag);
        var fell = Split(bag, out object kept);
        // Same ItemInstance objects (uid, mass, def): what the compactor pays per kg for them is what hand-sorted junk pays.
        Assert.AreSame(j1, fell[0]);
        Assert.AreSame(j2, fell[1]);
        Assert.AreSame(ring, Items(kept)[0]);
        Assert.AreSame(can, Items(kept)[1]);
        float fellKg = 0f;
        foreach (var it in fell) fellKg += Kg(it);
        float keptKg = (float)kept.GetType().GetProperty("TotalMassKg").GetValue(kept);
        Assert.AreEqual(before, fellKg + keptKg, 1e-4f, "no mass appears or vanishes: the bag is lighter by exactly what fell out");
        Assert.AreEqual(fell.Count + Items(kept).Count, Items(bag).Count, "no item is duplicated or lost");
        foreach (string field in new[] { "bagId", "foundAt", "bagMassKg", "sackVariant" })
            Assert.AreEqual(bag.GetType().GetField(field).GetValue(bag), kept.GetType().GetField(field).GetValue(kept), field + " (same id: same tell, same vision flag)");
        // The junk goes in through the compactor's robot door, like Magnus's junk: paid when crushed, never a player combo.
        Assert.NotNull(Runtime("Compactor").GetMethod("AcceptFromRobot", Instance));
    }

    [Test]
    public void DiverterIsDeterministicIdempotentAndNeverEmptiesABag()
    {
        object a = Bag("sortflow/det", Item("Junk", 1f, "j1"), Item("Paper", 0.2f, "p1"), Item("Junk", 3f, "j2"));
        var first = UidsOf(Split(a, out object keptA));
        var second = UidsOf(Split(a, out _));
        CollectionAssert.AreEqual(first, second, "the same bag sheds the same items every time, on every machine");
        Assert.AreEqual(0, Split(keptA, out object again).Count, "a second pass sheds nothing");
        Assert.AreSame(keptA, again);

        object allJunk = Bag("sortflow/alljunk", Item("Junk", 1f, "j1"), Item("Junk", 3.5f, "j2"), Item("Junk", 2f, "j3"));
        var fell = Split(allJunk, out object kept);
        CollectionAssert.AreEqual(new[] { "j1", "j3" }, UidsOf(fell));
        CollectionAssert.AreEqual(new[] { "j2" }, UidsOf(Items(kept)), "the heaviest piece jams the grate and stays in");
        Assert.AreEqual(0, Split(kept, out _).Count);

        object treasureOnly = Bag("sortflow/treasure", Item("Valuable", 0.1f, "v1"));
        Assert.AreEqual(0, Split(treasureOnly, out object same).Count);
        Assert.AreSame(treasureOnly, same, "nothing shed = the very same contents (no tell about what is inside)");
        Assert.AreEqual(4f, Const<float>("JunkDiverter", "GrateMaxKg"));
    }

    // ------------------------------------------------------------------ Magnus

    static readonly Vector2 TableHalf = new Vector2(1.6f, 0.45f);   // the garage's 3.2 x 0.9 m table top

    [TestCase(0f, 0f, false, true)]            // on the table
    [TestCase(0f, -2.4f, false, true)]         // on the floor in front, 1.95 m from the front edge
    [TestCase(0f, -2.5f, false, false)]        // 2.05 m: out of reach
    [TestCase(2.15f, 0f, false, true)]         // queue rack bay 1 (x −1.1 in the garage)
    [TestCase(2.9f, 0f, false, true)]          // queue rack bay 2 (x −0.35)
    [TestCase(3.7f, 0f, false, false)]         // 2.1 m past the table's end
    [TestCase(9f, -5.75f, false, false)]       // the dump pile's centre, in table space: far out of reach
    [TestCase(0f, 0f, true, false)]            // and never anything lying on the pile, wherever the table is
    public void MagnusReachIsTheTableAndTwoMetresAroundItNeverTheDumpPile(float x, float z, bool onPile, bool expected)
    {
        Assert.AreEqual(2f, Const<float>("MagnusReach", "ReachMetres"));
        Assert.AreEqual(expected, (bool)Call("MagnusReach", "InReach", new Vector2(x, z), TableHalf, onPile));
    }

    [TestCase(0, 0, false, false)]   // no job: he plays chess
    [TestCase(0, 2, false, false)]
    [TestCase(1, 0, false, true)]    // career step 1: he rips normal bags
    [TestCase(1, 0, true, false)]    // ... but leaves heavy ones for you
    [TestCase(1, 1, true, true)]     // Lobster Pincer: heavy bags too
    [TestCase(4, 0, true, false)]    // no brain level replaces the pincer
    [TestCase(4, 2, true, true)]
    public void MagnusRipsBagsFromHisFirstStepAndHeavyOnesWithThePincer(int brain, int gripper, bool heavy, bool expected)
    {
        Assert.AreEqual(expected, (bool)Call("MagnusReach", "MayRip", brain, gripper, heavy));
        Assert.AreEqual(expected, (bool)Call("MagnusReach", "MayTake", brain, gripper, heavy, new Vector2(2.15f, 0f), TableHalf, false));
        Assert.IsFalse((bool)Call("MagnusReach", "MayTake", brain, gripper, heavy, new Vector2(9f, -5.75f), TableHalf, true), "never off the dump pile");
    }

    [Test]
    public void EveryCareerStepChangesWhatMagnusCanDo()
    {
        var steps = (Array)Runtime("MagnusCareer").GetField("Steps", Static).GetValue(null);
        Assert.AreEqual(9, steps.Length);
        for (int i = 0; i < steps.Length; i++)
        {
            object s = steps.GetValue(i);
            string name = (string)s.GetType().GetField("name").GetValue(s);
            Assert.IsTrue((bool)Call("MagnusCareer", "StepAdds", i), $"step {i + 1} ({name}) is a dead purchase");
            object item = s.GetType().GetProperty("Item").GetValue(s);
            Assert.NotNull(item, name + " is not sold");
            Assert.LessOrEqual((int)s.GetType().GetField("level").GetValue(s), (int)item.GetType().GetField("maxLevel").GetValue(item));
        }
        Assert.IsFalse((bool)Call("MagnusCareer", "StepAdds", 9));

        string Adds(int i) => (string)steps.GetValue(i).GetType().GetField("adds").GetValue(steps.GetValue(i));
        StringAssert.Contains("rips the bags within his reach", Adds(0), "the job itself makes him rip and sort");
        StringAssert.DoesNotContain("rips bags", Adds(2), "the pincer no longer brings bag ripping");
        StringAssert.Contains("HEAVY bags", Adds(2));
    }

    [Test]
    public void CareerCapabilitiesMatchTheSteps()
    {
        object Caps(int brain, int rail, int gripper, int vision) => Call("MagnusCareer", "CapabilitiesAt", brain, rail, gripper, vision);
        T F<T>(object caps, string field) => (T)caps.GetType().GetField(field).GetValue(caps);

        var none = Caps(0, 1, 2, 2);
        Assert.IsFalse(F<bool>(none, "employed"));
        Assert.IsFalse(F<bool>(none, "ripsBags"));
        Assert.AreEqual(0f, F<float>(none, "itemsPerMin"));

        var job = Caps(1, 0, 0, 0);
        Assert.IsTrue(F<bool>(job, "ripsBags"), "career step 1 rips bags, with or without a conveyor");
        Assert.IsFalse(F<bool>(job, "heavyBags"));
        Assert.AreEqual(8f, F<float>(job, "itemsPerMin"));
        Assert.AreEqual(5f, F<float>(job, "maxItemKg"));
        Assert.AreEqual(16f, F<float>(job, "hoursPerDay"));

        var pincer = Caps(1, 0, 1, 0);
        Assert.IsTrue(F<bool>(pincer, "heavyBags"));
        Assert.AreEqual(25f, F<float>(pincer, "maxItemKg"));

        var top = Caps(4, 1, 2, 2);
        Assert.AreEqual(30f * 1.25f * 1.4f, F<float>(top, "itemsPerMin"), 1e-3f, "52.5 items/min at the very top (no conveyor factor)");
        Assert.AreEqual(24f, F<float>(top, "hoursPerDay"));
        Assert.IsTrue(F<bool>(top, "valuables") && F<bool>(top, "hazards"));
        Assert.AreEqual(1f, F<float>(top, "flagShare"));
        Assert.AreEqual(0.03f, F<float>(top, "ebaeBonus"));
    }

    [Test]
    public void TheTrackOnlyGoesBetweenTheChessBoardAndTheSortStop()
    {
        var type = Runtime("MagnusTrack");
        Assert.IsNull(type.GetField("truckStop", Instance), "the branch toward the dump area is gone");
        Assert.IsNull(type.GetMethod("DriveToTruck", Instance));
        var cart = new GameObject("SortFlow cart"); made.Add(cart);
        var track = cart.AddComponent(type);
        var chess = new GameObject("SortFlow chess"); made.Add(chess);
        var sort = new GameObject("SortFlow sort"); made.Add(sort);
        var pile = new GameObject("SortFlow pile"); made.Add(pile);
        chess.transform.position = new Vector3(3.275f, 0f, 0.72f);
        sort.transform.position = new Vector3(-3.4f, 0f, 1.2f);
        pile.transform.position = new Vector3(5.75f, 0f, -3.2f);
        type.GetField("chessStop", Instance).SetValue(track, chess.transform);
        type.GetProperty("SortStop").SetValue(track, sort.transform);

        Assert.IsTrue((bool)type.GetMethod("IsStop").Invoke(track, new object[] { chess.transform }));
        Assert.IsTrue((bool)type.GetMethod("IsStop").Invoke(track, new object[] { sort.transform }));
        Assert.IsFalse((bool)type.GetMethod("IsStop").Invoke(track, new object[] { pile.transform }));
        var refused = (IEnumerator)type.GetMethod("DriveRoute").Invoke(track, new object[] { pile.transform });
        Assert.IsFalse(refused.MoveNext(), "a route to anywhere else is refused outright");
        Assert.IsFalse((bool)type.GetProperty("IsDriving").GetValue(track));
        Assert.AreEqual(Vector3.zero, cart.transform.position);
        var allowed = (IEnumerator)type.GetMethod("DriveRoute").Invoke(track, new object[] { sort.transform });
        Assert.IsTrue(allowed.MoveNext(), "a route to the Sort Stop hands over to the drive itself");
        var drive = (IEnumerator)allowed.Current;   // no coroutine runner in edit mode: step the nested drive by hand
        Assert.IsTrue(drive.MoveNext());
        Assert.IsTrue((bool)type.GetProperty("IsDriving").GetValue(track));
        ((IDisposable)drive).Dispose();
        Assert.IsFalse((bool)type.GetProperty("IsDriving").GetValue(track));
    }

    // ------------------------------------------------------------------ TIP IT on the truck

    static object Check(bool driver, bool busy, bool cargo, float speed, string where, bool ground, float groundY, bool room) =>
        Call("DumpRules", "Check", driver, busy, cargo, speed, Enum("DumpRules+Where", where), ground, groundY, room);

    [TestCase(true, false, true, 0f, "Outdoors", true, 0.02f, true, "Ok")]
    [TestCase(true, false, true, 0.9f, "Outdoors", true, 0.02f, false, "Ok")]            // outdoors there is no depot cap
    [TestCase(false, false, true, 0f, "Outdoors", true, 0.02f, true, "NotDriver")]
    [TestCase(true, true, true, 0f, "Outdoors", true, 0.02f, true, "Busy")]
    [TestCase(true, false, false, 0f, "Outdoors", true, 0.02f, true, "Empty")]
    [TestCase(true, false, true, 1.01f, "Outdoors", true, 0.02f, true, "Moving")]
    [TestCase(true, false, true, float.NaN, "Outdoors", true, 0.02f, true, "Moving")]
    [TestCase(true, false, true, 0f, "Outdoors", false, 0f, true, "NoGround")]            // off a bridge, out of bounds
    [TestCase(true, false, true, 0f, "Outdoors", true, -1.1f, true, "Water")]             // the river bed / the sea
    [TestCase(true, false, true, 0f, "OwnBay", false, 0f, true, "Ok")]                    // indoors the floor is the tipping floor
    [TestCase(true, false, true, 0f, "OwnBay", true, 0f, false, "DepotFull")]
    [TestCase(true, false, true, 0f, "ForeignBay", true, 0f, true, "NotYourShop")]
    public void DumpRefusalsAreChecked(bool driver, bool busy, bool cargo, float speed, string where, bool ground, float groundY, bool room, string expected)
    {
        object verdict = Check(driver, busy, cargo, speed, where, ground, groundY, room);
        Assert.AreEqual(expected, verdict.ToString());
        string message = (string)Call("DumpRules", "Message", verdict);
        if (expected == "Ok" || expected == "NotDriver") Assert.IsNull(message);
        else Assert.IsNotEmpty(message, "every refusal the driver can cause gets a clear toast");
    }

    [Test]
    public void DumpTakesTenSecondsOrThreeWithTheAutoTipperWhateverTheLoad()
    {
        Assert.AreEqual(10f, (float)Call("DumpRules", "Seconds", 0));
        Assert.AreEqual(3f, (float)Call("DumpRules", "Seconds", 1));
        Assert.AreEqual(3f, (float)Call("DumpRules", "Seconds", 2), "an unmigrated level can't be faster than the one level there is");
        float lead = Const<float>("DumpRules", "LeadSeconds");
        foreach (int level in new[] { 0, 1 })
            foreach (int bags in new[] { 1, 5, 20, 60 })
            {
                float interval = (float)Call("DumpRules", "BagInterval", level, bags);
                Assert.AreEqual((float)Call("DumpRules", "Seconds", level), lead + interval * bags, 1e-3f, $"level {level}, {bags} bags");
            }
        Assert.AreEqual(Const<float>("DumpRules", "MinBagInterval"), (float)Call("DumpRules", "BagInterval", 1, 500), 1e-6f, "never faster than physics can take");
        Assert.AreEqual(1f, Const<float>("DumpRules", "MaxSpeed"));
    }

    [Test]
    public void TheFloorLeverAndItsAutomationAreGoneAndTheTruckTipsInstead()
    {
        Assert.IsNull(Missing("TruckUnloader"));
        Assert.IsNull(Missing("TruckUnloaderZoneTarget"));
        Assert.IsNull(Missing("AutoTipper"));
        Assert.NotNull(Runtime("TruckTipper").GetMethod("HostBegin"));
        Assert.NotNull(Runtime("DepotTruckCargoNet").GetMethod("RequestDump"));
        Assert.IsNull(Runtime("DepotTruckCargoNet").GetMethod("RequestTip"), "nobody on foot can tip a truck any more");
        Assert.AreEqual(KeyCode.G, (KeyCode)Runtime("TruckTipper").GetField("Key").GetRawConstantValue());
    }

    [Test]
    public void ATruckTipsOntoTheNearestTippingFloorInAStandAloneGarageAndOutdoorsOtherwise()
    {
        var go = new GameObject("SortFlow floor"); made.Add(go);
        go.SetActive(false);
        go.transform.position = new Vector3(5.75f, 0f, -3.2f);
        var floor = go.AddComponent(Runtime("TippingFloor"));
        var type = floor.GetType();
        type.GetMethod("OnEnable", Instance).Invoke(floor, null);
        try
        {
            var args = new object[] { new Vector3(-0.1f, 0f, -2.5f), false };   // the bay's TruckSpawner
            Assert.AreSame(floor, type.GetMethod("At", Static).Invoke(null, args));
            Assert.IsTrue((bool)args[1]);
            args = new object[] { new Vector3(120f, 0f, 40f), false };          // a street far away
            Assert.IsNull(type.GetMethod("At", Static).Invoke(null, args));
            Assert.IsFalse((bool)args[1], "outdoors: the load lands on the ground behind the truck");
        }
        finally { type.GetMethod("OnDisable", Instance).Invoke(floor, null); }
    }

    // ------------------------------------------------------------------ litter

    [TestCase(false, false, false, false, true)]    // a tipped bag lying in the street
    [TestCase(true, false, false, false, false)]    // a curb bag: its record already counts by age
    [TestCase(false, true, false, false, false)]    // still at its stop
    [TestCase(false, false, true, false, false)]    // in somebody's hands / consumed
    [TestCase(false, false, false, true, false)]    // inside a shop
    public void StrayStreetBagsAreLitter(bool curbRecord, bool atStop, bool held, bool inShop, bool expected)
    {
        Assert.AreEqual(expected, (bool)Call("StreetLitter", "Counts", curbRecord, atStop, held, inShop));
    }

    [Test]
    public void ATippedBagDirtiesItsDistrictLikeItsContentsLyingLooseAndWrecksTheMidnightBonus()
    {
        float perItem = Const<float>("CityCleanliness", "LitterDirt");
        Assert.AreEqual(7 * perItem, (float)Call("StreetLitter", "BagDirt", 7), 1e-6f, "a bag = its items as loose litter (bursting it changes nothing)");
        Assert.AreEqual(perItem, (float)Call("StreetLitter", "BagDirt", 0), 1e-6f, "an empty sack is still one piece of litter");

        // The midnight maths (CityCleanliness): clean = 1 − dirt / DirtForZero; the bonus pays above 50%, the streak needs 90%.
        float dirtForZero = Const<float>("CityCleanliness", "DirtForZero"), streak = Const<float>("CityCleanliness", "StreakThreshold");
        float load = 20 * (float)Call("StreetLitter", "BagDirt", 7);           // a 20-bag truckload left in the starter suburb
        float clean = Mathf.Clamp01(1f - load / dirtForZero);
        Assert.Less(clean, 0.5f, "a tipped truckload still there at midnight zeroes that district's Clean Streets bonus");
        Assert.Less(Mathf.Clamp01(1f - 3 * (float)Call("StreetLitter", "BagDirt", 7) / dirtForZero), streak, "three stray bags already break a clean streak");
        Assert.AreEqual(1f, Mathf.Clamp01(1f - 0f / dirtForZero), "collect them again and the district is clean");
    }

    // ------------------------------------------------------------------ refund migration

    [Test]
    public void AutoTipperLevelTwoBecomesLevelOneWithAnExactOneTimeRefund()
    {
        var item = Call("ScrapazonCatalog", "Get", "auto_tipper");
        Assert.AreEqual(1, (int)item.GetType().GetField("maxLevel").GetValue(item), "Auto-Tipper is one level");
        Assert.AreEqual("truck", (string)item.GetType().GetField("dept").GetValue(item), "and it is a truck upgrade now");
        long refund = Const<long>("UpgradeMigrations", "AutoTipperL2PriceCents");
        Assert.AreEqual(250000L, refund, "what level 2 cost ($2,500)");

        var owned = new Dictionary<string, int> { { "auto_tipper", 2 }, { "conveyor_belt", 3 }, { "sorting_table", 2 } };
        Assert.AreEqual(refund, (long)Call("UpgradeMigrations", "MigrateAutoTipper", owned));
        Assert.AreEqual(1, owned["auto_tipper"], "level 1 is kept");
        Assert.AreEqual(3, owned["conveyor_belt"], "other levels keep their number");
        Assert.AreEqual(2, owned["sorting_table"]);
        Assert.AreEqual(0L, (long)Call("UpgradeMigrations", "MigrateAutoTipper", owned), "idempotent: the second load refunds nothing");

        foreach (int level in new[] { 0, 1 })
        {
            var levels = new Dictionary<string, int> { { "auto_tipper", level } };
            Assert.AreEqual(0L, (long)Call("UpgradeMigrations", "MigrateAutoTipper", levels));
            Assert.AreEqual(level, levels["auto_tipper"]);
        }
        var fresh = new Dictionary<string, int>();
        Assert.AreEqual(0L, (long)Call("UpgradeMigrations", "MigrateAutoTipper", fresh), "a new business gets nothing");
        Assert.AreEqual(0, fresh.Count, "and no level is invented for it");
        Assert.AreEqual(0L, (long)Call("UpgradeMigrations", "MigrateAutoTipper", new object[] { null }));
    }

    [Test]
    public void ConveyorKeepsThreeLevelsWithTheNewPricesAndRanks()
    {
        var item = Call("ScrapazonCatalog", "Get", "conveyor_belt");
        CollectionAssert.AreEqual(new[] { 900f, 1200f, 14000f }, (float[])item.GetType().GetField("prices").GetValue(item));
        CollectionAssert.AreEqual(new[] { 3, 5, 8 }, (int[])item.GetType().GetField("rankRequired").GetValue(item));
        var effects = (string[])item.GetType().GetField("levelEffects").GetValue(item);
        StringAssert.Contains("never opens a bag", effects[0]);
        StringAssert.Contains("HEAVY", effects[1]);
        StringAssert.Contains("4 kg", effects[2]);
        foreach (string text in effects)
        {
            StringAssert.DoesNotContain("slit", text.ToLowerInvariant());
            StringAssert.DoesNotContain("the time", text, "no sorting-time multiplier is tied to the conveyor");
        }
    }

    // ------------------------------------------------------------------ two sorting positions

    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    public void TheSecondSortingSpotComesWithTheDoubleWideTable(int tableLevel, int seats)
    {
        Assert.AreEqual(seats, (int)Call("SortSeats", "Count", tableLevel));
        Assert.IsTrue((bool)Call("SortSeats", "SeatExists", 0, tableLevel));
        Assert.AreEqual(seats == 2, (bool)Call("SortSeats", "SeatExists", 1, tableLevel));
    }

    // preferred, table level, player at 1, Magnus at 1, player at 2 → seat (−1 = none)
    [TestCase(0, 1, false, false, false, 0)]    // no upgrade: exactly today's one-at-a-time
    [TestCase(0, 1, true, false, false, -1)]
    [TestCase(0, 1, false, true, false, -1)]    // Magnus on shift: shoo him
    [TestCase(1, 1, false, false, false, 0)]    // spot 2 does not exist yet
    [TestCase(0, 2, false, false, false, 0)]
    [TestCase(1, 2, false, false, false, 1)]    // walked up to the left-hand spot
    [TestCase(0, 2, false, true, false, 1)]     // beside Magnus
    [TestCase(0, 2, true, false, false, 1)]     // beside another player
    [TestCase(1, 2, false, false, true, 0)]
    [TestCase(0, 2, true, false, true, -1)]     // both taken
    [TestCase(0, 2, false, true, true, -1)]
    public void SteppingUpPicksAFreeSpot(int preferred, int tableLevel, bool player0, bool magnus0, bool player1, int expected)
    {
        Assert.AreEqual(expected, (int)Call("SortSeats", "Assign", preferred, tableLevel, player0, magnus0, player1));
    }

    [Test]
    public void MagnusKeepsOutOfTheSecondSortersHalfAndNeverTakesAnAimedItem()
    {
        const float split = -0.775f, seat2 = -1.4f;   // the line between the two stand points and spot 2, in table space
        Assert.IsTrue((bool)Call("SortSeats", "MagnusMayTake", -1.2f, false, split, seat2, false), "alone, he works the whole table");
        Assert.IsTrue((bool)Call("SortSeats", "MagnusMayTake", 0.8f, true, split, seat2, false), "his own side");
        Assert.IsFalse((bool)Call("SortSeats", "MagnusMayTake", -1.2f, true, split, seat2, false), "the second sorter's end is theirs");
        Assert.IsTrue((bool)Call("SortSeats", "MagnusMayTake", -1.2f, true, 0.45f, 1.05f, false), "whichever side spot 2 is on");
        Assert.IsFalse((bool)Call("SortSeats", "MagnusMayTake", 0.8f, false, split, seat2, true), "never the item a player is pointing at");
        var station = Runtime("SortStation");
        Assert.NotNull(station.GetField("seat"));
        Assert.NotNull(station.GetMethod("AssignSeat"));
        Assert.NotNull(station.GetMethod("AnyPlayerAimsAt", Static));
    }

    // ------------------------------------------------------------------ controls

    [Test]
    public void NextBagIsGoneAndTipItIsATruckKey()
    {
        var actions = System.Enum.GetNames(Runtime("GameControls").GetNestedType("Action"));
        CollectionAssert.DoesNotContain(actions, "NextBag");
        CollectionAssert.Contains(actions, "TipIt");
        var station = Runtime("SortStation");
        Assert.IsNull(station.GetMethod("NextBag", Instance));
        Assert.IsNull(station.GetField("nextBagKey", Instance));
        var defaults = Runtime("Keybinds").GetNestedType("Defaults");
        Assert.IsNull(defaults.GetField("NextBag"));
        Assert.AreEqual(KeyCode.G, (KeyCode)defaults.GetField("TipIt").GetRawConstantValue());

        foreach (string sheet in new[] { "Groups", "ControllerGroups" })
        {
            bool tipInTruck = false;
            foreach (var group in (IEnumerable)Runtime("Keybinds").GetProperty(sheet).GetValue(null))
            {
                string title = (string)group.GetType().GetField("title").GetValue(group);
                foreach (var bind in (Array)group.GetType().GetField("binds").GetValue(group))
                {
                    string action = (string)bind.GetType().GetField("action").GetValue(bind);
                    string keys = (string)bind.GetType().GetField("keys").GetValue(bind);
                    StringAssert.DoesNotContain("next bag", action.ToLowerInvariant(), sheet + " / " + title);
                    if (!action.StartsWith("TIP IT")) continue;
                    Assert.AreEqual("Truck", title);
                    Assert.AreEqual(sheet == "Groups" ? "Hold G" : "Hold B", keys);
                    tipInTruck = true;
                }
            }
            Assert.IsTrue(tipInTruck, sheet + " lists TIP IT under the truck");
        }
    }
}

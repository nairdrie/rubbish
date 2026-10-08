using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The SolidWaste 3D floppy disk (Workshop/DiskItem.cs, WorkshopState.disk / diskBags / diskDrops, Workshop.ApplyShopEvent):
/// the one thing that unlocks printing. Covers when a business's bags hide one (never before the third bag, always within
/// the first ten, a replacement within three), that keeping it works once, that nothing prints without it, that a save
/// which already owns gear is handed its disk on load, and the item itself (a story item no loot table ever rolls).
/// Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like WorkshopStateTests.
/// </summary>
public class DiskTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Val(object value, string member)
    {
        var type = value as Type ?? value.GetType(); object target = value is Type ? null : value;
        var field = type.GetField(member, Flags); if (field != null) return field.GetValue(target);
        var property = type.GetProperty(member, Flags); Assert.NotNull(property, type.Name + "." + member);
        return property.GetValue(target);
    }
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static TValue Const<TValue>(string type, string field) => (TValue)T(type).GetField(field, Flags).GetRawConstantValue();

    static int FirstWindow => Const<int>("WorkshopState", "DiskFirstWindow");
    static int Earliest => Const<int>("WorkshopState", "DiskEarliestBag");
    static int RetryWindow => Const<int>("WorkshopState", "DiskRetryWindow");
    static string Json(object state) => JsonUtility.ToJson(state);
    static bool Valid(object state) => (bool)Call(state, "Valid");
    static int Disk(object state) => (int)Val(state, "disk");
    static int Bags(object state) => (int)Val(state, "diskBags");
    static int Drops(object state) => (int)Val(state, "diskDrops");
    static bool Rip(object state, double roll, bool diskIsOut = false) => (bool)Call(state, "BagRipped", roll, diskIsOut);
    static double RealRoll(string businessId, object state) => (double)Static("Workshop", "DiskRoll", businessId, state);
    /// <summary>Rip bags until one has the disk in it; the 1-based number of that bag in its window (−1: none in <paramref name="limit"/>).</summary>
    static int BagWithTheDisk(object state, Func<object, double> roll, int limit = 60)
    {
        for (int bag = 1; bag <= limit; bag++) {
            int before = Bags(state);
            if (Rip(state, roll(state))) return bag;
            Assert.AreEqual(before + 1, Bags(state), "a bag with no disk in it still counts toward the window");
        }
        return -1;
    }
    static object AfterDrops(int drops) { var state = New("WorkshopState"); Set(state, "diskDrops", drops); return state; }

    // ------------------------------------------------------------------ the window

    [Test]
    public void TheFirstDiskNeverComesBeforeTheThirdBagAndAlwaysByTheTenth()
    {
        Assert.AreEqual(10, FirstWindow); Assert.AreEqual(3, Earliest); Assert.AreEqual(3, RetryWindow);
        // bags one and two: learn to sort first, whatever the dice say
        foreach (double roll in new[] { 0.0, 0.01, 0.5, 0.999999 }) {
            var early = New("WorkshopState");
            Assert.IsFalse(Rip(early, roll), "bag 1"); Assert.AreEqual(1, Bags(early)); Assert.IsFalse(Rip(early, roll), "bag 2"); Assert.AreEqual(2, Bags(early));
            Assert.AreEqual(0, Drops(early)); Assert.AreEqual(0, Disk(early)); Assert.IsTrue(Valid(early));
        }
        // the luckiest possible dice find it in bag 3, the unluckiest in bag 10: never outside
        Assert.AreEqual(Earliest, BagWithTheDisk(New("WorkshopState"), _ => 0.0));
        Assert.AreEqual(FirstWindow, BagWithTheDisk(New("WorkshopState"), _ => 0.999999));
        // a roll that is not a roll at all never breaks the guarantee: it is a "no" until the last bag, which always has it
        foreach (double broken in new[] { 1.0, 1.5, -0.25, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.AreEqual(FirstWindow, BagWithTheDisk(New("WorkshopState"), _ => broken), "roll " + broken);
        // the dice the game really uses (a hash of the business, the window and the bag): thousands of businesses, every one served
        var found = new int[FirstWindow + 1];
        for (int business = 0; business < 4000; business++) {
            string id = business % 2 == 0 ? "biz-" + business : (business * 2654435761L).ToString("x16") + (business * 40503L).ToString("x16");
            var state = New("WorkshopState");
            int bag = BagWithTheDisk(state, s => RealRoll(id, s));
            Assert.That(bag, Is.InRange(Earliest, FirstWindow), "business " + id + " found its first disk in bag " + bag);
            Assert.AreEqual(0, Bags(state), "a new window starts after a drop"); Assert.AreEqual(1, Drops(state)); Assert.AreEqual(0, Disk(state), "found is not kept"); Assert.IsTrue(Valid(state));
            found[bag]++;
        }
        TestContext.WriteLine("first disk by bag (4000 businesses, the game's own rolls): " + string.Join(" ", Enumerable.Range(Earliest, FirstWindow - Earliest + 1).Select(b => b + ":" + found[b])));
        for (int bag = Earliest; bag <= FirstWindow; bag++)
            Assert.That(found[bag], Is.InRange(350, 650), "bag " + bag + " should hide the disk for about one business in eight (500 of 4000) with the game's own rolls");
        // the roll is a pure function of (business, window, bag): the host and a solo game decide alike, and a retry cannot re-roll
        var probe = New("WorkshopState"); Set(probe, "diskBags", 4);
        double first = RealRoll("biz-7", probe); Assert.AreEqual(first, RealRoll("biz-7", probe)); Assert.That(first, Is.InRange(0.0, 1.0));
        Assert.AreNotEqual(first, RealRoll("biz-8", probe), "another business rolls its own dice");
        Set(probe, "diskBags", 5); Assert.AreNotEqual(first, RealRoll("biz-7", probe), "every bag is its own roll");
        Set(probe, "diskBags", 4); Set(probe, "diskDrops", 1); Assert.AreNotEqual(first, RealRoll("biz-7", probe), "and so is every window");
    }

    [Test]
    public void EveryBagOfAWindowIsEquallyLikelyToHideTheDisk()
    {
        var random = new System.Random(20261008);
        const int Businesses = 16000;
        var first = new int[FirstWindow + 1];
        for (int i = 0; i < Businesses; i++) first[BagWithTheDisk(New("WorkshopState"), _ => random.NextDouble())]++;
        int bags = FirstWindow - Earliest + 1; Assert.AreEqual(8, bags);
        Assert.AreEqual(0, first[1] + first[2]); Assert.AreEqual(Businesses, first.Sum());
        for (int bag = Earliest; bag <= FirstWindow; bag++)
            Assert.That(first[bag], Is.InRange(Businesses / bags - 300, Businesses / bags + 300), "bag " + bag + " of the first window (a fair share is " + Businesses / bags + ")");
        // the replacement window: three bags, a third each
        var retry = new int[RetryWindow + 1];
        for (int i = 0; i < 9000; i++) retry[BagWithTheDisk(AfterDrops(1 + i % 5), _ => random.NextDouble())]++;
        Assert.AreEqual(9000, retry.Sum());
        for (int bag = 1; bag <= RetryWindow; bag++) Assert.That(retry[bag], Is.InRange(2700, 3300), "bag " + bag + " of a replacement window");
    }

    [Test]
    public void AReplacementForALostDiskTurnsUpWithinThreeBags()
    {
        Assert.AreEqual(1, BagWithTheDisk(AfterDrops(1), _ => 0.0), "no waiting period the second time");
        Assert.AreEqual(RetryWindow, BagWithTheDisk(AfterDrops(1), _ => 0.999999));
        Assert.AreEqual(RetryWindow, BagWithTheDisk(AfterDrops(3), _ => double.NaN));
        // a business that keeps losing them: the first within ten, every one after that within three, for as long as it takes
        for (int business = 0; business < 300; business++) {
            string id = "butterfingers-" + business; var state = New("WorkshopState");
            Assert.That(BagWithTheDisk(state, s => RealRoll(id, s)), Is.InRange(Earliest, FirstWindow));
            for (int lost = 1; lost <= 12; lost++) {
                Assert.AreEqual(lost, Drops(state));
                Assert.That(BagWithTheDisk(state, s => RealRoll(id, s)), Is.InRange(1, RetryWindow), id + ", disk " + (lost + 1));
                Assert.AreEqual(0, Bags(state)); Assert.IsTrue(Valid(state));
            }
        }
        // the counter of disks handed out stops at a number that still fits the save
        var careless = AfterDrops(9999); Assert.IsTrue(Valid(careless));
        Assert.That(BagWithTheDisk(careless, _ => 0.999999), Is.EqualTo(RetryWindow)); Assert.AreEqual(9999, Drops(careless)); Assert.IsTrue(Valid(careless));
    }

    [Test]
    public void NoBagCountsWhileADiskIsLyingAroundAndNoneAtAllOnceItIsInstalled()
    {
        // one at a time: while a disk is out, the window waits (no drop, no counter movement), whatever the roll
        foreach (var (bags, drops) in new[] { (0, 0), (2, 0), (9, 0), (0, 1), (2, 4) }) {
            var state = New("WorkshopState"); Set(state, "diskBags", bags); Set(state, "diskDrops", drops); string before = Json(state);
            foreach (double roll in new[] { 0.0, 0.3, 0.999999, double.NaN }) { Assert.IsFalse(Rip(state, roll, true)); Assert.AreEqual(before, Json(state), "a disk is already out"); }
            // and it carries on exactly where it was once that disk is gone
            Rip(state, 0.999999); Assert.AreNotEqual(before, Json(state));
        }
        var waiting = New("WorkshopState"); Set(waiting, "diskBags", FirstWindow - 1);
        Assert.IsFalse(Rip(waiting, 0.0, true), "even the guaranteed last bag waits"); Assert.IsTrue(Rip(waiting, 0.999999), "and delivers as soon as the shop is clear");
        // installed: the hunt is over for good
        foreach (var (bags, drops) in new[] { (0, 0), (0, 1), (5, 2) }) {
            var state = New("WorkshopState"); Set(state, "disk", 1); Set(state, "diskBags", bags); Set(state, "diskDrops", drops); string before = Json(state);
            for (int bag = 0; bag < 25; bag++) { Assert.IsFalse(Rip(state, bag % 2 == 0 ? 0.0 : 0.999999, bag % 3 == 0)); Assert.AreEqual(before, Json(state), "nothing at all once the disk is in the laptop"); }
        }
    }

    [Test]
    public void KeepingTheDiskInstallsItOnce()
    {
        var state = New("WorkshopState"); Set(state, "diskBags", 6); Set(state, "diskDrops", 1);
        Assert.IsFalse((bool)Val(state, "DiskInstalled"));
        Assert.IsTrue((bool)Call(state, "InsertDisk")); Assert.AreEqual(1, Disk(state)); Assert.IsTrue((bool)Val(state, "DiskInstalled"));
        Assert.AreEqual(0, Bags(state), "the hunt's counter is cleared"); Assert.AreEqual(1, Drops(state)); Assert.IsTrue(Valid(state));
        string once = Json(state);
        Assert.IsFalse((bool)Call(state, "InsertDisk"), "a second disk goes nowhere"); Assert.AreEqual(once, Json(state));
        // it survives the save and the wire, and a clone carries it
        var restored = Static("BusinessRegistry", "ParseWorkshop", once); Assert.NotNull(restored); Assert.AreEqual(once, Json(restored)); Assert.AreEqual(1, Disk(restored));
        Assert.AreEqual(1, Disk(Call(state, "Clone")));
        var hunting = New("WorkshopState"); Set(hunting, "diskBags", 7); Set(hunting, "diskDrops", 2);
        var copy = Call(hunting, "Clone"); Assert.AreEqual(7, Bags(copy)); Assert.AreEqual(2, Drops(copy)); Assert.AreEqual(Json(hunting), Json(copy));
        Rip(copy, 0.999999); Assert.AreEqual(7, Bags(hunting), "a clone's hunt is its own");
    }

    // ------------------------------------------------------------------ shop events (what the host applies)

    [Test]
    public void AShopEventChangesTheStateOnlyWhenTheRulesSaySo()
    {
        byte ripped = Const<byte>("Workshop", "EventBagRipped"), kept = Const<byte>("Workshop", "EventDiskKept");
        Assert.AreEqual(1, ripped, "wire value"); Assert.AreEqual(2, kept, "wire value");
        var apply = T("Workshop").GetMethod("ApplyShopEvent", Flags);
        bool Apply(object state, byte kind, double roll, bool diskIsOut, out bool drop)
        {
            var args = new object[] { state, kind, roll, diskIsOut, false };
            bool changed = (bool)apply.Invoke(null, args); drop = (bool)args[4]; return changed;
        }
        var state = New("WorkshopState");
        // a disk cannot be kept before one was ever handed out
        string before = Json(state);
        Assert.IsFalse(Apply(state, kept, 0.0, false, out bool drop)); Assert.IsFalse(drop); Assert.AreEqual(before, Json(state)); Assert.AreEqual(0, Disk(state));
        // bags 1 and 2: the state changed (the count moved, so the host publishes it), nothing dropped
        Assert.IsTrue(Apply(state, ripped, 0.0, false, out drop)); Assert.IsFalse(drop); Assert.AreEqual(1, Bags(state));
        Assert.IsTrue(Apply(state, ripped, 0.0, false, out drop)); Assert.IsFalse(drop); Assert.AreEqual(2, Bags(state));
        // a disk already lying around: nothing changes, nothing to publish
        before = Json(state);
        Assert.IsFalse(Apply(state, ripped, 0.0, true, out drop)); Assert.IsFalse(drop); Assert.AreEqual(before, Json(state));
        // still nothing to keep
        Assert.IsFalse(Apply(state, kept, 0.0, false, out drop)); Assert.AreEqual(before, Json(state));
        // unknown kinds change nothing
        foreach (byte unknown in new byte[] { 0, 3, 4, 7, 8, 200, 255 }) { Assert.IsFalse(Apply(state, unknown, 0.0, false, out drop), "kind " + unknown); Assert.IsFalse(drop); Assert.AreEqual(before, Json(state)); }
        // bag 3 with the luckiest roll: changed, and this bag carries the disk
        Assert.IsTrue(Apply(state, ripped, 0.0, false, out drop)); Assert.IsTrue(drop); Assert.AreEqual(0, Bags(state)); Assert.AreEqual(1, Drops(state)); Assert.AreEqual(0, Disk(state));
        // now it can be kept, exactly once
        Assert.IsTrue(Apply(state, kept, 0.0, false, out drop)); Assert.IsFalse(drop, "keeping never drops another"); Assert.AreEqual(1, Disk(state)); Assert.IsTrue(Valid(state));
        before = Json(state);
        Assert.IsFalse(Apply(state, kept, 0.0, false, out drop), "kept once only"); Assert.AreEqual(before, Json(state));
        // and bags are just bags from here on
        for (int bag = 0; bag < 12; bag++) { Assert.IsFalse(Apply(state, ripped, 0.0, false, out drop)); Assert.IsFalse(drop); Assert.AreEqual(before, Json(state)); }
        // no state (a business that is still syncing): nothing
        Assert.IsFalse(Apply(null, ripped, 0.0, false, out drop)); Assert.IsFalse(drop); Assert.IsFalse(Apply(null, kept, 0.0, false, out drop));
        // a replacement that is crushed again can still be kept when the next one turns up
        var again = AfterDrops(1);
        Assert.IsTrue(Apply(again, ripped, 0.999999, false, out drop)); Assert.IsFalse(drop);
        Assert.IsTrue(Apply(again, ripped, 0.999999, false, out drop)); Assert.IsFalse(drop);
        Assert.IsTrue(Apply(again, ripped, 0.999999, false, out drop)); Assert.IsTrue(drop, "the third bag of a replacement window always has it"); Assert.AreEqual(2, Drops(again));
        Assert.IsTrue(Apply(again, kept, 0.5, true, out drop), "the disk being kept is the one lying around"); Assert.AreEqual(1, Disk(again));
    }

    // ------------------------------------------------------------------ the gate, and the saves that were already printing

    static object[] Nodes => ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>().ToArray();
    static List<string> Parts(object node, int level) => (List<string>)Call(Call(node, "RecipeOf", level), "Pick", (Func<string, int>)(id => 99));
    static bool Build(object state, string id, List<string> offered, out string why)
    {
        var args = new object[] { id, offered, null };
        bool ok = (bool)state.GetType().GetMethod("BuildCalling", Flags).Invoke(state, args); why = (string)args[2]; return ok;
    }

    [Test]
    public void NothingIsPrintedWithoutTheDiskAndEverythingWorksAsBeforeWithIt()
    {
        string noDisk = Const<string>("WorkshopState", "NoDiskText");
        StringAssert.Contains(Const<string>("SolidWaste", "Name"), noDisk); StringAssert.Contains("KEEP", noDisk);
        foreach (var node in Nodes) {
            string id = (string)Val(node, "id");
            for (int level = 0; level < (int)Val(node, "maxLevel"); level++) {
                // everything else in order (prerequisites, the level before, the exact parts): only the disk is missing
                var state = New("WorkshopState");
                foreach (var other in Nodes) Call(state, "SetCallingLevel", Val(other, "index"), other == node ? level : Val(other, "implementedLevels"));
                string before = Json(state);
                Assert.IsFalse(Build(state, id, Parts(node, level + 1), out string why), id + " v" + (level + 1) + " without the disk");
                Assert.AreEqual(noDisk, why, id + ": the refusal says what is missing"); Assert.AreEqual(before, Json(state), id);
            }
            var fresh = New("WorkshopState"); string untouched = Json(fresh);
            Assert.IsFalse(Build(fresh, id, Parts(node, 1), out string freshWhy)); Assert.AreEqual(noDisk, freshWhy); Assert.AreEqual(untouched, Json(fresh));
            Assert.IsFalse(Build(fresh, id, null, out freshWhy)); Assert.AreEqual(noDisk, freshWhy);
        }
        // an id that is not a blueprint is still "unknown", disk or no disk
        var none = New("WorkshopState");
        Assert.IsFalse(Build(none, "made_up", new List<string>(), out string unknown)); Assert.AreNotEqual(noDisk, unknown); Assert.IsNotEmpty(unknown);
        // the same business, the same parts, one floppy later: the old rules, unchanged
        var gloves = Static("CallingCatalog", "Get", "work_gloves"); var headlamp = Static("CallingCatalog", "Get", "headlamp");
        var shop = New("WorkshopState");
        Assert.IsFalse(Build(shop, "work_gloves", Parts(gloves, 1), out _));
        Assert.IsTrue((bool)Call(shop, "InsertDisk"));
        Assert.IsFalse(Build(shop, "headlamp", Parts(headlamp, 1), out string locked), "the trunk still comes first"); Assert.AreNotEqual(noDisk, locked);
        Assert.IsFalse(Build(shop, "work_gloves", Parts(gloves, 1).Skip(1).ToList(), out string shortBy), "one part short is still short"); Assert.AreNotEqual(noDisk, shortBy);
        Assert.IsTrue(Build(shop, "work_gloves", Parts(gloves, 1), out string built)); Assert.AreEqual("", built); Assert.AreEqual(1, (int)Call(shop, "CallingLevel", "work_gloves"));
        Assert.IsTrue(Build(shop, "headlamp", Parts(headlamp, 1), out _)); Assert.IsTrue(Valid(shop));
        // the host's request path says the same thing (Workshop.ApplyBuild, action 4), and so does the Monument (action 8: MonumentTests)
        var apply = T("Workshop").GetMethod("ApplyBuild", Flags);
        var offer = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ItemInstance"))); int n = 0;
        foreach (string defId in Parts(gloves, 1)) {
            var def = Static("ItemCatalog", "Get", defId); var item = New("ItemInstance");
            Set(item, "uid", "gate-" + n++); Set(item, "defId", defId); Set(item, "massKg", Val(def, "minMass")); Set(item, "condition", 1f); offer.Add(item);
        }
        var request = new object[] { New("WorkshopState"), Const<byte>("Workshop", "ActionBuildCalling"), "work_gloves", offer, null };
        Assert.IsFalse((bool)apply.Invoke(null, request)); Assert.AreEqual(noDisk, request[4]);
        var withDisk = New("WorkshopState"); Set(withDisk, "disk", 1); request[0] = withDisk; request[4] = null;
        Assert.IsTrue((bool)apply.Invoke(null, request)); Assert.IsNotEmpty((string)request[4]);
    }

    [Test]
    public void ASaveThatAlreadyOwnsGearIsHandedItsDiskAndABrandNewBusinessIsNot()
    {
        Func<string, int> nothing = id => 0, gloves = id => id == "work_gloves" ? 2 : 0, truckOnly = id => id == "truck_turbo" || id == "decor_doorbell" ? 3 : 0;
        // a brand-new business: seeding and repairing leave it exactly as it was, still hunting
        var fresh = New("WorkshopState"); string untouched = Json(fresh);
        Assert.IsFalse((bool)Call(fresh, "SeedCallings", nothing)); Call(fresh, "NormalizePrinter");
        Assert.AreEqual(untouched, Json(fresh)); Assert.AreEqual(0, Disk(fresh)); Assert.IsTrue(Valid(fresh));
        // gear that is not on the tree (truck upgrades, decor) is not a print
        var driver = New("WorkshopState"); Assert.IsFalse((bool)Call(driver, "SeedCallings", truckOnly)); Call(driver, "NormalizePrinter"); Assert.AreEqual(0, Disk(driver));
        // a save that bought gloves before the disk existed: they show as printed, and a printing business has its disk
        var veteran = New("WorkshopState"); Set(veteran, "diskBags", 4);
        Assert.IsTrue((bool)Call(veteran, "SeedCallings", gloves)); Assert.AreEqual(2, (int)Call(veteran, "CallingLevel", "work_gloves"));
        Assert.IsFalse(Valid(veteran), "seeded but not yet repaired: a print without a disk is not a state anyone may upload");
        Call(veteran, "NormalizePrinter");
        Assert.AreEqual(1, Disk(veteran)); Assert.AreEqual(0, Bags(veteran), "its hunt is over before it began"); Assert.IsTrue(Valid(veteran));
        Assert.IsFalse(Rip(veteran, 0.0), "no floppy ever drops for a business that has the software");
        var gloveNode = Static("CallingCatalog", "Get", "work_gloves");
        Assert.IsTrue(Build(veteran, "work_gloves", Parts(gloveNode, 3), out _), "and it keeps printing");

        // the real load path, end to end (GameSession.LoadWorkshop: the silent one, no events, no save)
        var session = T("GameSession"); var workshopField = session.GetField("workshop", Flags);
        var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        object previousWorkshop = workshopField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels);
        try {
            levels.Clear(); levels["headlamp"] = 1; levels["truck_turbo"] = 2;
            Static("GameSession", "LoadWorkshop", New("WorkshopState"));
            var loaded = workshopField.GetValue(null);
            Assert.AreEqual(1, Disk(loaded), "a save with a headlamp is grandfathered in"); Assert.AreEqual(1, (int)Call(loaded, "CallingLevel", "headlamp")); Assert.IsTrue(Valid(loaded));
            Assert.IsTrue((bool)Val(session, "WorkshopDisk")); Assert.IsTrue((bool)Val(T("Workshop"), "HasPrinter"), "so its laptop has the third tab");
            // a save with nothing printable keeps hunting, and its window survives the load
            levels.Clear(); levels["truck_turbo"] = 2; levels["decor_doorbell"] = 1;
            var hunting = New("WorkshopState"); Set(hunting, "diskBags", 6); Set(hunting, "diskDrops", 1);
            Static("GameSession", "LoadWorkshop", hunting);
            loaded = workshopField.GetValue(null);
            Assert.AreEqual(0, Disk(loaded)); Assert.AreEqual(6, Bags(loaded)); Assert.AreEqual(1, Drops(loaded)); Assert.IsTrue((bool)Call(loaded, "Completed", "doorbell"));
            Assert.IsFalse((bool)Val(session, "WorkshopDisk")); Assert.IsFalse((bool)Val(T("Workshop"), "HasPrinter"));
            // a kept disk is saved with the business
            levels.Clear(); var owner = New("WorkshopState"); Set(owner, "disk", 1); Set(owner, "diskDrops", 1);
            Static("GameSession", "LoadWorkshop", owner);
            Assert.AreEqual(1, Disk(workshopField.GetValue(null))); Assert.IsTrue((bool)Val(session, "WorkshopDisk"));
            // a brand-new business and a save with no workshop at all: no disk
            levels.Clear(); Static("GameSession", "LoadWorkshop", new object[] { null });
            loaded = workshopField.GetValue(null);
            Assert.AreEqual(0, Disk(loaded)); Assert.AreEqual(0, Bags(loaded)); Assert.AreEqual(0, Drops(loaded)); Assert.IsFalse((bool)Val(session, "WorkshopDisk")); Assert.IsEmpty(levels);
        }
        finally { workshopField.SetValue(null, previousWorkshop); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted"); }
        // This test never saves, switches profiles or raises game events: LoadWorkshop is the silent load path.
    }

    // ------------------------------------------------------------------ the item

    static object[] Catalog => ((IEnumerable)Val(T("ItemCatalog"), "All")).Cast<object>().ToArray();
    static object Floppy { get { var def = Static("ItemCatalog", "Get", "floppy_disk"); Assert.NotNull(def, "floppy_disk must be in the item catalog"); return def; } }

    [Test]
    public void TheFloppyIsAStoryItemAppendedLastThatNoLootTableCanRoll()
    {
        Assert.AreEqual("floppy_disk", Const<string>("DiskItem", "DefId"));
        var def = Floppy; var catalog = Catalog;
        Assert.AreSame(def, catalog[catalog.Length - 1], "story items come after every other def: saved catalog indices (DailyStats, wanted pins) must not move");
        Assert.AreEqual(1, catalog.Count(d => (string)Val(d, "id") == "floppy_disk"));
        Assert.AreEqual(catalog.Length, (int)Static("Workshop", "CatalogNumber", "floppy_disk"));
        Assert.AreEqual("Valuable", Val(def, "category").ToString(), "the KEEP crate is where it goes"); Assert.AreEqual("Rare", Val(def, "rarity").ToString());
        Assert.AreEqual(0f, (float)Static("ItemCatalog", "WeightOf", def), "weight 0: the loot tables never pick it");
        Assert.IsTrue((bool)Static("ItemCatalog", "HasTag", def, "story"));
        foreach (string lootTag in new[] { "bulk", "garage_only", "tell_only", "night_only" }) Assert.IsFalse((bool)Static("ItemCatalog", "HasTag", def, lootTag), lootTag);
        Assert.AreEqual("story_floppy", Val(def, "visual")); Assert.IsNotEmpty((string)Val(def, "name")); Assert.IsNotEmpty((string)Val(def, "flavor"));
        Assert.That((float)Val(def, "minMass"), Is.GreaterThan(0f).And.LessThanOrEqualTo((float)Val(def, "maxMass")));
        // nobody can steal one out of a house either, and the host's catalog check accepts the one the game hands out
        for (int tier = 1; tier <= 4; tier++) Assert.IsFalse((bool)Static("ScavengerRules", "Stealable", def, tier), "district tier " + tier);
        Assert.IsFalse((bool)T("Workshop").GetMethod("IsPart", new[] { T("ItemDef") }).Invoke(null, new[] { def }), "it is not a part for a blueprint");

        // DiskItem.Is: by def, nothing else
        var disk = New("ItemInstance"); Set(disk, "uid", "disk-0-0-1f-1"); Set(disk, "defId", "floppy_disk"); Set(disk, "massKg", Val(def, "minMass")); Set(disk, "condition", 1f); Set(disk, "value", Val(def, "minValue"));
        Assert.IsTrue((bool)Static("DiskItem", "Is", disk));
        var plausible = new object[] { disk, null }; Assert.IsTrue((bool)T("EbaeItemValidation").GetMethod("IsPlausible").Invoke(null, plausible), (string)plausible[1]);
        var can = New("ItemInstance"); Set(can, "uid", "can"); Set(can, "defId", "soda_can");
        Assert.IsFalse((bool)Static("DiskItem", "Is", can)); Assert.IsFalse((bool)Static("DiskItem", "Is", New("ItemInstance"))); Assert.IsFalse((bool)Static("DiskItem", "Is", new object[] { null }));
        // the KEEP crate has its own path for it (never the inventory), and the laptop's third app is the printer
        Assert.NotNull(T("SortBin").GetMethod("KeepDisk", Flags)); Assert.NotNull(T("DiskItem").GetMethod("Kept", Flags)); Assert.NotNull(T("DiskFoundPopup"));
        CollectionAssert.AreEqual(new[] { "Ebae", "Scrapazon", "Printer" }, Enum.GetNames(T("LaptopApp")), "SolidWaste 3D is the THIRD tab");
    }

    /// <summary>The floppy never enters an inventory (KEEP sends it to the laptop: SortBin.KeepDisk, DiskItem.Kept), and the
    /// Trash-dex only records what is kept into one (Progression.ApplyTreasureKept / BackfillDex). A dex entry for it is a
    /// silhouette nobody can ever fill, and "Trash-dex complete" (DexFound >= DexTotal, the Curator title) becomes unreachable.</summary>
    [Test]
    public void TheFloppyIsNotATrashDexEntryBecauseItNeverEntersAnInventory()
    {
        var dex = ((IEnumerable)Val(T("Progression"), "DexDefs")).Cast<object>().Select(d => (string)Val(d, "id")).ToArray();
        Assert.Greater(dex.Length, 50, "the dex lists the game's valuables");
        CollectionAssert.DoesNotContain(dex, "floppy_disk", "the Trash-dex counts the floppy (" + dex.Length + " entries), but keeping it never records a dex entry: the dex can no longer be completed");
        Assert.AreEqual(dex.Length, (int)Val(T("Progression"), "DexTotal"));
    }

    [Test]
    public void TheFloppyHasItsOwnHandBuiltModel()
    {
        var parent = new GameObject("Floppy visual audit");
        try {
            var model = (GameObject)T("ItemVisuals").GetMethod("Build").Invoke(null, new object[] { Floppy, parent.transform });
            Assert.NotNull(model);
            var renderers = model.GetComponentsInChildren<Renderer>(true); Assert.GreaterOrEqual(renderers.Length, 6, "a shell, a shutter, a label: not a category placeholder");
            Assert.Zero(model.GetComponentsInChildren<Collider>(true).Length); Assert.Zero(model.GetComponentsInChildren<Rigidbody>(true).Length);
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true)) Assert.NotNull(filter.sharedMesh, filter.name);
            foreach (var renderer in renderers) Assert.NotNull(renderer.sharedMaterial, renderer.name);
            var bounds = new Bounds(model.transform.position, Vector3.zero);
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            Assert.That(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), Is.InRange(0.05f, 1f), "a disk-sized thing");
            Assert.Less(bounds.size.y, Mathf.Min(bounds.size.x, bounds.size.z), "flat, like a floppy");
            // no other item shares its model
            Assert.AreEqual(1, Catalog.Count(d => (string)Val(d, "visual") == "story_floppy"));
        }
        finally { UnityEngine.Object.DestroyImmediate(parent); }
    }

    [Test]
    public void NoRolledBagEverContainsAFloppy()
    {
        var catalog = T("ItemCatalog");
        var roll = catalog.GetMethod("RollBag", new[] { typeof(string), typeof(int), typeof(float), typeof(string), T("LootProfile"), typeof(float) });
        var core = catalog.GetMethod("RollBagCore", Flags);
        var districts = ((IEnumerable)Val(T("Districts"), "All")).Cast<object>().ToArray(); Assert.GreaterOrEqual(districts.Length, 4);
        int bags = 0, items = 0, valuables = 0;
        void Audit(object bag, string what)
        {
            bags++;
            foreach (var item in (IList)Val(bag, "items")) {
                items++; string defId = (string)Val(item, "defId");
                Assert.AreNotEqual("floppy_disk", defId, what + ": the disk is handed out by the print shop's own rule, never by a loot table");
                if (Val(Val(item, "Def"), "category").ToString() == "Valuable") valuables++;
            }
        }
        for (int d = 0; d < districts.Length; d++) {
            var profile = Val(districts[d], "loot");
            for (int seed = 0; seed < 900; seed++) {
                string id = "disk-audit/" + d + "/" + seed; float wealth = (seed % 5) * 0.25f;
                Audit(roll.Invoke(null, new object[] { id, seed * 7919 + 17327, wealth, "audit", profile, 0.6f }), id);
                // the everyday-finds pass on and off, at full trust (rich themes and guarantees included)
                Audit(core.Invoke(null, new object[] { id + "/core", seed * 104729 + 3, wealth, "audit", profile, 1f, seed % 2 == 0 }), id + "/core");
            }
        }
        Assert.Greater(bags, 7000); Assert.Greater(items, 20000); Assert.Greater(valuables, 100, "the audit must have rolled real treasure, not only junk");
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The Monument (Workshop/MonumentCatalog.cs + WorkshopState.monument / fed + Workshop.SelectForMonument): ten tiers of
/// one trophy, each paid in kept items of the four sorted types and loaded a parts shelf at a time. Covers the cost table,
/// the load rules (all or nothing, never more than a tier still needs, the load that fills it prints it), what a valid
/// state looks like and how a local save is repaired, how the owner's machine picks a load, and the request on the wire.
/// Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like WorkshopStateTests.
/// </summary>
public class MonumentTests
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

    static readonly int[] Each = { 10, 20, 35, 60, 100, 160, 250, 400, 650, 1000 };
    /// <summary>One plain catalog item of each sorted type, in WorkshopMaterial order: metal, plastic, glass, paper.</summary>
    static readonly string[] Sorted = { "soda_can", "water_bottle", "beer_bottle", "newspaper" };
    static readonly string[] Materials = { "metal", "plastic", "glass", "paper" };
    static int MaxLevel => Const<int>("MonumentCatalog", "MaxLevel");
    static object Mats(int metal, int plastic, int glass, int paper) => Activator.CreateInstance(T("WorkshopMaterials"), metal, plastic, glass, paper);
    static int[] Four(object materials) => Materials.Select(m => (int)Val(materials, m)).ToArray();
    static int[] Fed(object state) => Four(Val(state, "fed"));
    static int[] Need(object state) => Four(Call(state, "MonumentNeed"));
    static int Tier(object state) => (int)Val(state, "monument");
    static bool Valid(object state) => (bool)Call(state, "Valid");
    static string Json(object state) => JsonUtility.ToJson(state);
    static string Category(string defId) { var def = Static("ItemCatalog", "Get", defId); Assert.NotNull(def, defId); return Val(def, "category").ToString(); }
    /// <summary>A business that has its SolidWaste 3D disk, with <paramref name="tier"/> tiers already printed.</summary>
    static object Printer(int tier = 0) { var state = New("WorkshopState"); Set(state, "disk", 1); Set(state, "monument", tier); return state; }
    static List<string> Load(int metal, int plastic, int glass, int paper)
    {
        var list = new List<string>(); int[] counts = { metal, plastic, glass, paper };
        for (int m = 0; m < 4; m++) list.AddRange(Enumerable.Repeat(Sorted[m], counts[m]));
        return list;
    }
    static bool Feed(object state, IEnumerable<string> offered, out bool printed, out string why)
    {
        var args = new object[] { offered?.ToList(), false, null };
        bool ok = (bool)state.GetType().GetMethod("FeedMonument", Flags).Invoke(state, args);
        printed = (bool)args[1]; why = (string)args[2]; return ok;
    }
    static void Refused(object state, IEnumerable<string> offered, string because, string exactly = null)
    {
        string before = Json(state);
        Assert.IsFalse(Feed(state, offered, out bool printed, out string why), because);
        Assert.IsFalse(printed, because); Assert.IsNotEmpty(why, because + ": a refusal says why");
        if (exactly != null) Assert.AreEqual(exactly, why, because);
        Assert.AreEqual(before, Json(state), because + ": a refused load leaves the state byte for byte as it was");
    }

    // ------------------------------------------------------------------ the cost table

    [Test]
    public void TenTiersCostStrictlyMoreEachTimeAndTheLastIsAThousandOfEachType()
    {
        Assert.AreEqual(10, MaxLevel); Assert.AreEqual("monument", Const<string>("MonumentCatalog", "NodeId"));
        var tiers = ((IEnumerable)Val(T("MonumentCatalog"), "All")).Cast<object>().ToArray();
        Assert.AreEqual(10, tiers.Length);
        for (int i = 0; i < tiers.Length; i++) {
            int level = i + 1; string where = "tier " + level;
            Assert.AreEqual(level, (int)Val(tiers[i], "level")); Assert.AreSame(tiers[i], Static("MonumentCatalog", "Tier", level), where);
            Assert.AreEqual(Each[i], (int)Val(tiers[i], "each"), where);
            CollectionAssert.AreEqual(Enumerable.Repeat(Each[i], 4), Four(Val(tiers[i], "cost")), where + " takes the same number of every sorted type");
            CollectionAssert.AreEqual(Enumerable.Repeat(Each[i], 4), Four(Static("MonumentCatalog", "Cost", level)), where);
            if (i > 0) Assert.Greater((int)Val(tiers[i], "each"), (int)Val(tiers[i - 1], "each"), where + " must cost more than the one before");
            foreach (string text in new[] { "name", "plaque", "description" }) Assert.IsNotEmpty((string)Val(tiers[i], text), where + "." + text);
        }
        Assert.AreEqual(1000, (int)Val(tiers[9], "each"), "tier 10 is the absurd one: 1,000 of each type");
        Assert.AreEqual(10, tiers.Select(t => (string)Val(t, "name")).Distinct().Count(), "ten different trophies");
        Assert.AreEqual(4 * 2685, tiers.Sum(t => (int)Val(Val(t, "cost"), "Total")), "the whole Monument is 10,740 kept items");
        foreach (int outside in new[] { 0, -1, 11, 99 }) {
            Assert.IsNull(Static("MonumentCatalog", "Tier", outside), "tier " + outside);
            Assert.AreEqual(0, (int)Val(Static("MonumentCatalog", "Cost", outside), "Total"), "tier " + outside + " costs nothing: it does not exist");
        }
        // the Monument is a spine of tiers, not a blueprint and not a decor piece
        Assert.IsNull(Static("CallingCatalog", "Get", "monument")); Assert.IsNull(Static("WorkshopCatalog", "Get", "monument"));
        CollectionAssert.AreEqual(new[] { "Metal", "Plastic", "Glass", "Paper" }, Sorted.Select(Category), "the four sorted types these tests feed it");
    }

    // ------------------------------------------------------------------ loading and printing

    [Test]
    public void ALoadIsRefusedWithoutTheDiskWhenFinishedEmptyUnsortedOrTooMuchAndChangesNothing()
    {
        Assert.AreEqual("Junk", Category("banana_peel")); Assert.AreEqual("Hazard", Category("aa_battery")); Assert.AreEqual("Valuable", Category("garden_gnome"));
        string noDisk = Const<string>("WorkshopState", "NoDiskText");
        StringAssert.Contains(Const<string>("SolidWaste", "Name"), noDisk); StringAssert.Contains("floppy", noDisk);

        // no disk: nothing prints, whatever is offered (and a load already on the books does not help)
        var diskless = New("WorkshopState");
        Refused(diskless, Load(1, 1, 1, 1), "no disk", noDisk); Refused(diskless, Load(10, 10, 10, 10), "no disk, the exact tier", noDisk);
        Refused(diskless, null, "no disk and nothing offered", noDisk);
        // finished: there is no eleventh tier
        var finished = Printer(MaxLevel);
        Refused(finished, Load(1, 0, 0, 0), "finished"); Refused(finished, Load(1000, 1000, 1000, 1000), "finished");
        // an empty offer
        var state = Printer();
        Refused(state, null, "nothing offered"); Refused(state, new List<string>(), "an empty offer");
        // anything that is not sorted metal, plastic, glass or paper spoils the whole offer
        foreach (string bad in new[] { "banana_peel", "aa_battery", "garden_gnome", "floppy_disk", "not_an_item", "", null }) {
            Refused(state, new List<string> { bad }, "only '" + bad + "'");
            Refused(state, Load(2, 2, 2, 2).Concat(new[] { bad }), "a good load with '" + bad + "' in it");
        }
        // more of a type than the tier still needs, even when the rest of the offer is fine
        for (int m = 0; m < 4; m++) {
            int[] over = { 1, 1, 1, 1 }; over[m] = Each[0] + 1;
            Refused(state, Load(over[0], over[1], over[2], over[3]), "eleven " + Materials[m] + " for a tier of ten");
        }
        Assert.IsTrue(Feed(state, Load(9, 0, 4, 0), out _, out _));
        Refused(state, Load(2, 0, 0, 0), "two metal when one is left"); Refused(state, Load(1, 1, 7, 1), "seven glass when six are left");
        Refused(state, Load(0, 11, 0, 0), "eleven plastic");
        CollectionAssert.AreEqual(new[] { 9, 0, 4, 0 }, Fed(state)); Assert.AreEqual(0, Tier(state)); Assert.IsTrue(Valid(state));
    }

    [Test]
    public void PartialLoadsAreRememberedAndTheLoadThatFillsTheFourthTypePrintsTheTier()
    {
        var state = Printer();
        CollectionAssert.AreEqual(new[] { 10, 10, 10, 10 }, Need(state));
        Assert.IsTrue(Feed(state, Load(4, 0, 0, 0), out bool printed, out string why)); Assert.IsFalse(printed); Assert.AreEqual("", why);
        CollectionAssert.AreEqual(new[] { 4, 0, 0, 0 }, Fed(state)); CollectionAssert.AreEqual(new[] { 6, 10, 10, 10 }, Need(state)); Assert.IsTrue(Valid(state));
        // any order, any mix, any real item of the type
        Assert.AreEqual("Metal", Category("door_hinge")); Assert.AreEqual("Plastic", Category("bucket"));
        Assert.IsTrue(Feed(state, new[] { "beer_bottle", "door_hinge", "bucket", "soda_can", "beer_bottle", "bucket" }, out printed, out _)); Assert.IsFalse(printed);
        CollectionAssert.AreEqual(new[] { 6, 2, 2, 0 }, Fed(state));
        Assert.IsTrue(Feed(state, Load(4, 8, 8, 0), out printed, out _)); Assert.IsFalse(printed, "three types full, the fourth empty: not printed yet");
        CollectionAssert.AreEqual(new[] { 10, 10, 10, 0 }, Fed(state)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 10 }, Need(state)); Assert.AreEqual(0, Tier(state));
        Assert.IsTrue(Feed(state, Load(0, 0, 0, 9), out printed, out _)); Assert.IsFalse(printed, "one sheet of paper short");
        Assert.AreEqual(0, Tier(state)); Assert.IsTrue(Valid(state));
        // the progress survives a save / the wire, and a clone is independent
        string json = Json(state); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, Json(restored)); CollectionAssert.AreEqual(new[] { 10, 10, 10, 9 }, Fed(restored));
        var copy = Call(state, "Clone"); Assert.AreEqual(json, Json(copy));
        Assert.IsTrue(Feed(copy, Load(0, 0, 0, 1), out printed, out _)); Assert.IsTrue(printed); Assert.AreEqual(json, Json(state), "feeding a copy never touches the original");
        // the last piece prints it: the tier goes up, the load starts again from nothing
        Assert.IsTrue(Feed(state, Load(0, 0, 0, 1), out printed, out why)); Assert.IsTrue(printed, "the load that fills the fourth type prints the tier"); Assert.AreEqual("", why);
        Assert.AreEqual(1, Tier(state)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Fed(state)); CollectionAssert.AreEqual(new[] { 20, 20, 20, 20 }, Need(state));
        Assert.IsTrue(Valid(state));
        // a whole tier in one load prints at once
        Assert.IsTrue(Feed(state, Load(20, 20, 20, 20), out printed, out _)); Assert.IsTrue(printed); Assert.AreEqual(2, Tier(state)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Fed(state));
    }

    [Test]
    public void EveryTierPrintsInOrderFromExactlyItsCostAndTheTenthEndsIt()
    {
        var state = Printer(); long consumed = 0;
        for (int tier = 1; tier <= MaxLevel; tier++) {
            int each = Each[tier - 1], half = each / 2;
            CollectionAssert.AreEqual(Enumerable.Repeat(each, 4), Need(state), "tier " + tier);
            Assert.IsTrue(Feed(state, Load(half, half, half, half), out bool printed, out _), "tier " + tier); Assert.IsFalse(printed); Assert.AreEqual(tier - 1, Tier(state));
            Assert.IsTrue(Valid(state), "half-loaded toward tier " + tier);
            Refused(state, Load(each - half + 1, 0, 0, 0), "one more than tier " + tier + " still needs");
            Assert.IsTrue(Feed(state, Load(each - half, each - half, each - half, each - half), out printed, out _), "tier " + tier); Assert.IsTrue(printed, "tier " + tier);
            Assert.AreEqual(tier, Tier(state)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Fed(state)); Assert.IsTrue(Valid(state), "tier " + tier);
            consumed += 4L * each;
        }
        Assert.AreEqual(10740, consumed, "exactly what the ten tiers cost was taken, and nothing else");
        Assert.AreEqual(MaxLevel, Tier(state)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Need(state), "the last tier ends it");
        Refused(state, Load(1, 1, 1, 1), "the Monument is finished"); Refused(state, Load(0, 0, 0, 1), "the Monument is finished");
        Assert.NotNull(Static("BusinessRegistry", "ParseWorkshop", Json(state)), "a finished Monument replicates");
    }

    [Test]
    public void MonumentNeedIsTheNextTiersCostLessWhatIsLoadedAndNeverNegative()
    {
        for (int tier = 0; tier < MaxLevel; tier++)
            CollectionAssert.AreEqual(Enumerable.Repeat(Each[tier], 4), Need(Printer(tier)), tier + " tiers printed");
        CollectionAssert.AreEqual(new[] { 10, 10, 10, 10 }, Need(New("WorkshopState")), "the need is the same before the disk is found");
        var state = Printer(4); Set(state, "fed", Mats(100, 37, 0, 99));
        CollectionAssert.AreEqual(new[] { 0, 63, 100, 1 }, Need(state));
        Set(state, "fed", Mats(500, -4, 100, 101)); // a corrupt load never turns into a negative need
        CollectionAssert.AreEqual(new[] { 0, 104, 0, 0 }, Need(state));
        var done = Printer(MaxLevel); Set(done, "fed", Mats(3, 0, 0, 0));
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Need(done));
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Need(Printer(MaxLevel + 1)), "past the top needs nothing");
    }

    // ------------------------------------------------------------------ valid states, repaired saves

    [Test]
    public void AStateWithAnImpossibleTierLoadOrDiskIsNotValid()
    {
        void Bad(object state, string why) { Assert.IsFalse(Valid(state), why); Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", Json(state)), why + ": it cannot be uploaded either"); }
        void Good(object state, string why) { Assert.IsTrue(Valid(state), why); Assert.NotNull(Static("BusinessRegistry", "ParseWorkshop", Json(state)), why); }
        object With(int tier, object fed, int disk = 1) { var s = Printer(tier); Set(s, "fed", fed); Set(s, "disk", disk); return s; }

        Good(New("WorkshopState"), "a brand-new business"); Good(Printer(), "the disk and nothing printed");
        for (int tier = 0; tier <= MaxLevel; tier++) Good(Printer(tier), tier + " tiers");
        // tiers out of range
        foreach (int tier in new[] { -1, -100, 11, 12, 9999 }) Bad(Printer(tier), "tier " + tier);
        // the load: negative, more than the next tier costs, or exactly full (a full load is a printed tier, never a resting state)
        Good(With(0, Mats(10, 10, 10, 9)), "one short of tier 1"); Good(With(0, Mats(0, 0, 10, 0)), "one type full");
        Good(With(9, Mats(999, 1000, 1000, 1000)), "one short of tier 10");
        for (int m = 0; m < 4; m++) {
            int[] negative = { 1, 1, 1, 1 }; negative[m] = -1; Bad(With(0, Mats(negative[0], negative[1], negative[2], negative[3])), "negative " + Materials[m]);
            int[] over = { 0, 0, 0, 0 }; over[m] = 11; Bad(With(0, Mats(over[0], over[1], over[2], over[3])), "over-full " + Materials[m]);
            int[] next = { 0, 0, 0, 0 }; next[m] = 21; Bad(With(1, Mats(next[0], next[1], next[2], next[3])), "over-full " + Materials[m] + " for tier 2");
        }
        Bad(With(0, Mats(10, 10, 10, 10)), "exactly full for tier 1"); Bad(With(4, Mats(100, 100, 100, 100)), "exactly full for tier 5");
        Bad(With(9, Mats(1000, 1000, 1000, 1000)), "exactly full for tier 10");
        Bad(With(MaxLevel, Mats(1, 0, 0, 0)), "anything loaded after the last tier"); Bad(With(MaxLevel, Mats(0, 0, 0, -1)), "a negative load after the last tier");
        // printed things without the software that prints them
        for (int tier = 1; tier <= MaxLevel; tier++) Bad(With(tier, Mats(0, 0, 0, 0), 0), "tier " + tier + " with no disk");
        var gloves = New("WorkshopState"); Call(gloves, "SetCallingLevel", Val(Static("CallingCatalog", "Get", "work_gloves"), "index"), 1);
        Bad(gloves, "a printed blueprint with no disk"); Set(gloves, "disk", 1); Good(gloves, "the same with its disk");
        // the disk fields themselves
        foreach (int disk in new[] { -1, 2, 7 }) { var s = New("WorkshopState"); Set(s, "disk", disk); Bad(s, "disk = " + disk); }
        int window = Const<int>("WorkshopState", "DiskFirstWindow");
        foreach (int bags in new[] { -1, window + 1, 500 }) { var s = New("WorkshopState"); Set(s, "diskBags", bags); Bad(s, "diskBags = " + bags); }
        foreach (int drops in new[] { -1, 10000 }) { var s = New("WorkshopState"); Set(s, "diskDrops", drops); Bad(s, "diskDrops = " + drops); }
        var hunting = New("WorkshopState"); Set(hunting, "diskBags", window); Set(hunting, "diskDrops", 9999); Good(hunting, "the largest counters the hunt can reach");
    }

    [Test]
    public void ALocalSaveIsRepairedWithoutEverLoweringATier()
    {
        object Repaired(int tier, object fed, int disk, int bags = 0, int drops = 0)
        {
            var s = New("WorkshopState"); Set(s, "monument", tier); Set(s, "fed", fed); Set(s, "disk", disk); Set(s, "diskBags", bags); Set(s, "diskDrops", drops);
            Set(s, "completed", (ushort)(1 << 4)); Set(s, "colorIndex", 6); Assert.IsTrue((bool)Call(s, "SetPin", 12, true, null));
            Call(s, "NormalizePrinter");
            Assert.IsTrue(Valid(s), $"tier {tier}, disk {disk}: repaired states are valid");
            Assert.AreEqual((ushort)(1 << 4), Val(s, "completed")); Assert.AreEqual(6, Val(s, "colorIndex")); Assert.AreEqual("c", Val(s, "pins"), "the repair touches nothing else");
            string once = Json(s); Call(s, "NormalizePrinter"); Assert.AreEqual(once, Json(s), "repairing twice changes nothing");
            return s;
        }
        // out-of-range tiers clamp into 0..10; a tier in range is never lowered
        Assert.AreEqual(0, Tier(Repaired(-5, Mats(0, 0, 0, 0), 0))); Assert.AreEqual(MaxLevel, Tier(Repaired(99, Mats(0, 0, 0, 0), 1)));
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Fed(Repaired(MaxLevel, Mats(5, 5, 5, 5), 1)), "nothing can be loaded after the last tier");
        // a broken load clamps into what the next tier costs
        CollectionAssert.AreEqual(new[] { 0, 10, 4, 10 }, Fed(Repaired(0, Mats(-3, 50, 4, 10), 1))); Assert.AreEqual(0, Tier(Repaired(0, Mats(-3, 50, 4, 10), 1)));
        // a load that is somehow full is a printed tier
        var full = Repaired(2, Mats(99, 99, 99, 99), 1); Assert.AreEqual(3, Tier(full)); CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Fed(full));
        var exact = Repaired(0, Mats(10, 10, 10, 10), 0); Assert.AreEqual(1, Tier(exact)); Assert.AreEqual(1, Val(exact, "disk"), "and what it printed implies the disk");
        Assert.AreEqual(MaxLevel, Tier(Repaired(9, Mats(1000, 1000, 2000, 1000), 1)));
        // a save that printed anything before the disk existed has its disk; a save that printed nothing does not get one
        Assert.AreEqual(1, Val(Repaired(1, Mats(0, 0, 0, 0), 0), "disk")); Assert.AreEqual(1, Val(Repaired(7, Mats(1, 2, 3, 4), 0), "disk"));
        Assert.AreEqual(0, Val(Repaired(0, Mats(0, 0, 0, 0), 0), "disk")); Assert.AreEqual(1, Val(Repaired(0, Mats(0, 0, 0, 0), 5), "disk"), "a nonsense disk value is a disk");
        var seeded = New("WorkshopState"); Call(seeded, "SetCallingLevel", Val(Static("CallingCatalog", "Get", "hard_hat"), "index"), 1);
        Call(seeded, "NormalizePrinter"); Assert.AreEqual(1, Val(seeded, "disk"), "a printed blueprint implies the disk"); Assert.IsTrue(Valid(seeded));
        // the hunt counters: cleared once the disk is in, clamped while it is not
        var installed = Repaired(0, Mats(0, 0, 0, 0), 1, 7, 2); Assert.AreEqual(0, Val(installed, "diskBags")); Assert.AreEqual(2, Val(installed, "diskDrops"));
        var hunting = Repaired(0, Mats(0, 0, 0, 0), 0, 55, 123456); Assert.AreEqual(Const<int>("WorkshopState", "DiskFirstWindow"), Val(hunting, "diskBags")); Assert.AreEqual(9999, Val(hunting, "diskDrops"));
        var negative = Repaired(0, Mats(0, 0, 0, 0), 0, -4, -9); Assert.AreEqual(0, Val(negative, "diskBags")); Assert.AreEqual(0, Val(negative, "diskDrops"));

        // never lowers a tier: every tier in range, with every kind of nonsense loaded on top
        var random = new System.Random(8675309);
        for (int i = 0; i < 2000; i++) {
            int tier = random.Next(0, MaxLevel + 1);
            var s = New("WorkshopState"); Set(s, "monument", tier); Set(s, "disk", random.Next(0, 2));
            Set(s, "fed", Mats(random.Next(-50, 1500), random.Next(-50, 1500), random.Next(-50, 1500), random.Next(-50, 1500)));
            Call(s, "NormalizePrinter");
            Assert.That(Tier(s), Is.InRange(tier, Math.Min(MaxLevel, tier + 1)), "a repair never lowers a tier (and prints at most the one that was full)");
            Assert.IsTrue(Valid(s)); if (Tier(s) > 0) Assert.AreEqual(1, Val(s, "disk"));
        }
        // NormalizeCallings (the load path's repair) includes it
        var viaLoad = New("WorkshopState"); Set(viaLoad, "monument", 44); Set(viaLoad, "fed", Mats(-1, -1, -1, -1));
        Assert.IsFalse(Valid(viaLoad)); Assert.IsTrue((bool)Call(viaLoad, "ValidIgnoringCallings"), "a broken Monument is repaired, not a reason to throw the save's decor away");
        Call(viaLoad, "NormalizeCallings"); Assert.IsTrue(Valid(viaLoad)); Assert.AreEqual(MaxLevel, Tier(viaLoad)); Assert.AreEqual(1, Val(viaLoad, "disk"));
    }

    // ------------------------------------------------------------------ how the owner's machine picks a load

    static object Item(string defId, string uid, float massKg = -1f, float value = -1f, bool hot = false)
    {
        var def = Static("ItemCatalog", "Get", defId); Assert.NotNull(def, defId);
        var item = New("ItemInstance");
        Set(item, "uid", uid); Set(item, "defId", defId); Set(item, "condition", 1f); Set(item, "hot", hot);
        Set(item, "massKg", massKg >= 0f ? massKg : (float)Val(def, "minMass"));
        Set(item, "value", value >= 0f ? value : Val(def, "category").ToString() == "Valuable" ? (float)Val(def, "minValue") : 0f);
        return item;
    }
    static IList ItemList(IEnumerable<object> items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ItemInstance")));
        foreach (var item in items) list.Add(item);
        return list;
    }
    static object Stock(IEnumerable<object> inventory, Func<object, string> blocked = null)
    {
        var type = typeof(Func<,>).MakeGenericType(T("ItemInstance"), typeof(string));
        Delegate reason = blocked == null ? null : Delegate.CreateDelegate(type, blocked.Target, blocked.Method);
        return Static("Workshop", "StockOf", ItemList(inventory), reason);
    }
    static int Cap => Const<int>("Workshop", "MaxFeedItems");
    static object[] Select(object state, object stock, Func<string, bool> skip = null, int cap = -1) =>
        ((IList)Static("Workshop", "SelectForMonument", state, stock, skip, cap < 0 ? Cap : cap)).Cast<object>().ToArray();
    static string Uid(object item) => (string)Val(item, "uid");
    static string DefOf(object item) => (string)Val(item, "defId");
    static int MaterialOf(object item) => Array.IndexOf(new[] { "Metal", "Plastic", "Glass", "Paper" }, Category(DefOf(item)));
    static int[] Counts(IEnumerable<object> items) { var counts = new int[4]; foreach (var item in items) if (MaterialOf(item) >= 0) counts[MaterialOf(item)]++; return counts; }
    /// <summary><paramref name="count"/> items of a def, lightest first by index: "<prefix>-000" is the lightest.</summary>
    static IEnumerable<object> Pile(string defId, string prefix, int count, float lightest = 0.010f, float step = 0.001f) =>
        Enumerable.Range(0, count).Select(i => Item(defId, prefix + "-" + i.ToString("000"), lightest + step * i));

    [Test]
    public void ALoadTakesTheLightestFreePiecesOfEachTypeAndNeverMoreThanTheTierStillNeeds()
    {
        var state = Printer(); // tier 1: ten of each
        var inventory = new List<object>();
        inventory.AddRange(Pile("soda_can", "can", 14));
        inventory.AddRange(Pile("water_bottle", "bottle", 3));
        inventory.AddRange(Pile("beer_bottle", "beer", 12).Reverse()); // the order they were kept in is not the order they are taken in
        inventory.Add(Item("banana_peel", "junk")); inventory.Add(Item("aa_battery", "hazard")); inventory.Add(Item("garden_gnome", "gnome", -1f, 30f));
        // can-000 (the lightest of all) is on a display: spoken for, never taken
        var stock = Stock(inventory, item => Uid(item) == "can-000" ? "on a display" : null);
        var picked = Select(state, stock);
        Assert.IsFalse(picked.Any(i => MaterialOf(i) < 0), "junk, hazards and valuables are never Monument food");
        Assert.AreEqual(23, picked.Length);
        CollectionAssert.AreEqual(new[] { 10, 3, 10, 0 }, Counts(picked), "ten of a type at most, and only what there is");
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 10).Select(i => "can-" + i.ToString("000")), picked.Where(i => MaterialOf(i) == 0).Select(Uid), "the ten lightest FREE cans");
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 10).Select(i => "beer-" + i.ToString("000")), picked.Where(i => MaterialOf(i) == 2).Select(Uid), "the ten lightest bottles");
        Assert.AreEqual(picked.Length, picked.Select(Uid).Distinct().Count(), "no instance twice");
        // what the selection offers is exactly a load the rules accept
        Assert.IsTrue(Feed(Call(state, "Clone"), picked.Select(DefOf), out bool printed, out _)); Assert.IsFalse(printed);
        // what is already loaded counts against the need
        Set(state, "fed", Mats(4, 0, 9, 0));
        picked = Select(state, stock);
        CollectionAssert.AreEqual(new[] { 6, 3, 1, 0 }, Counts(picked));
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 6).Select(i => "can-" + i.ToString("000")), picked.Where(i => MaterialOf(i) == 0).Select(Uid));
        CollectionAssert.AreEqual(new[] { "beer-000" }, picked.Where(i => MaterialOf(i) == 2).Select(Uid));
        // equal weights: the uid breaks the tie, so the owner and a retry pick the same instances
        var twins = Stock(new[] { Item("soda_can", "twin-b", 0.02f), Item("soda_can", "twin-a", 0.02f), Item("soda_can", "twin-c", 0.02f) });
        var lone = Printer(); Set(lone, "fed", Mats(8, 0, 0, 0));
        CollectionAssert.AreEqual(new[] { "twin-a", "twin-b" }, Select(lone, twins).Select(Uid));
        // nothing to load: a full type, a finished Monument, no state, no stock
        Set(lone, "fed", Mats(10, 0, 0, 0)); Assert.IsEmpty(Select(lone, twins));
        Assert.IsEmpty(Select(Printer(MaxLevel), stock)); Assert.IsEmpty(Select(null, stock)); Assert.IsEmpty(Select(state, null));
        Assert.IsEmpty(Select(state, Stock(new[] { Item("banana_peel", "only-junk"), Item("garden_gnome", "only-gnome", -1f, 30f) })));
    }

    [Test]
    public void ACappedLoadNeverExceedsOneRequestAndTheFourTypesTakeTurns()
    {
        Assert.AreEqual(100, Cap, "one request carries at most a hundred pieces");
        Assert.LessOrEqual(Cap, Const<int>("Workshop", "MaxOfferItems"), "a full load must pass the host's own item limit");
        Assert.GreaterOrEqual(Cap * 2, Const<int>("GameSession", "PartsCap"), "a full parts shelf is two requests");
        var inventory = new List<object>();
        for (int m = 0; m < 4; m++) inventory.AddRange(Pile(Sorted[m], Materials[m], 150, 0.010f * (m + 1)));
        var stock = Stock(inventory);
        var state = Printer(4); // tier 5 takes a hundred of each: more than one request
        var picked = Select(state, stock);
        Assert.AreEqual(Cap, picked.Length); CollectionAssert.AreEqual(new[] { 25, 25, 25, 25 }, Counts(picked), "a capped load moves all four types along");
        for (int i = 0; i < picked.Length; i++) Assert.AreEqual(i % 4, MaterialOf(picked[i]), "metal, plastic, glass, paper, and round again (item " + i + ")");
        for (int m = 0; m < 4; m++)
            CollectionAssert.AreEqual(Enumerable.Range(0, 25).Select(i => Materials[m] + "-" + i.ToString("000")), picked.Where(i => MaterialOf(i) == m).Select(Uid), "the lightest " + Materials[m] + " first");
        Assert.IsTrue(Feed(Call(state, "Clone"), picked.Select(DefOf), out bool printed, out _)); Assert.IsFalse(printed);
        // smaller caps, the same turn-taking
        CollectionAssert.AreEqual(new[] { 2, 2, 2, 1 }, Counts(Select(state, stock, null, 7))); CollectionAssert.AreEqual(new[] { 1, 0, 0, 0 }, Counts(Select(state, stock, null, 1)));
        Assert.IsEmpty(Select(state, stock, null, 0));
        // an uncapped tier takes exactly what it needs
        CollectionAssert.AreEqual(new[] { 100, 100, 100, 100 }, Counts(Select(state, stock, null, 1000)));
        // a type that runs out gives its turns to the others; the load still stops at the cap
        var thin = Stock(inventory.Where(i => MaterialOf(i) != 0 || string.CompareOrdinal(Uid(i), "metal-003") < 0));
        var uneven = Select(state, thin); var counts = Counts(uneven);
        Assert.AreEqual(Cap, uneven.Length); Assert.AreEqual(3, counts[0]); Assert.AreEqual(97, counts[1] + counts[2] + counts[3]);
        Assert.LessOrEqual(counts.Skip(1).Max() - counts.Skip(1).Min(), 1, "the other three still share the load evenly");
        // the biggest tier and the fullest shelf: never above the cap
        Assert.AreEqual(Cap, Select(Printer(9), stock).Length);
        // nearly done: the cap is not a quota
        var nearly = Printer(4); Set(nearly, "fed", Mats(99, 100, 98, 100));
        CollectionAssert.AreEqual(new[] { 1, 0, 2, 0 }, Counts(Select(nearly, stock)));
    }

    [Test]
    public void TheMonumentNeverEatsAHuntedPartOrOneAPrintableBlueprintStillNeeds()
    {
        Func<string, bool> Skip(object s) => (Func<string, bool>)Static("Workshop", "MonumentSkip", s);
        var nodes = ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>().ToArray();
        var allIds = ((IEnumerable)Val(T("ItemCatalog"), "All")).Cast<object>().Select(d => (string)Val(d, "id")).ToArray();
        // the rule, spelled out: every option of every slot of the NEXT level of a blueprint that is neither locked nor coming soon
        HashSet<string> Needed(object s)
        {
            var keep = new HashSet<string>();
            foreach (var node in nodes) {
                var recipe = Call(s, "NextRecipe", node); string status = Call(s, "StatusOf", node, null).ToString();
                if (recipe == null || status == "Locked" || status == "ComingSoon") continue;
                foreach (var slot in (IEnumerable)Val(recipe, "slots")) foreach (string option in (IEnumerable)Val(slot, "anyOf")) keep.Add(option);
            }
            return keep;
        }
        void Check(object s, string when)
        {
            var keep = Needed(s); var skip = Skip(s);
            foreach (string id in allIds) Assert.AreEqual(keep.Contains(id), skip(id), when + ": " + id);
            Assert.IsFalse(skip("not_an_item"), when); Assert.IsFalse(skip(null), when);
        }
        var fresh = Printer(); Check(fresh, "a new print shop");
        var skipFresh = Skip(fresh);
        // (recipes as pinned by CallingsTests: gloves v1 = 2 old_work_boot + 1 oven_mitt; hard hat = bike_helmet + dented_pot + duct_tape; headlamp v1 needs a dead_lightbulb)
        Assert.IsTrue(skipFresh("old_work_boot")); Assert.IsTrue(skipFresh("oven_mitt")); Assert.IsTrue(skipFresh("bike_helmet"), "the hard hat hangs off nothing: printable from the start");
        Assert.IsFalse(skipFresh("dead_lightbulb"), "the headlamp is locked behind the gloves: its parts are not held back yet");
        foreach (string plain in Sorted) Assert.IsFalse(skipFresh(plain), plain + " is plain scrap");
        var gloved = Printer(); Call(gloved, "SetCallingLevel", Val(Static("CallingCatalog", "Get", "work_gloves"), "index"), 1); Check(gloved, "after the gloves");
        Assert.IsTrue(Skip(gloved)("dead_lightbulb"), "unlocked by the trunk"); Assert.IsFalse(Skip(gloved)("oven_mitt"), "gloves v2 takes other parts, and nothing else printable wants a mitt");
        var hatted = Printer(); Call(hatted, "SetCallingLevel", Val(Static("CallingCatalog", "Get", "hard_hat"), "index"), 1); Check(hatted, "after the hard hat");
        Assert.IsFalse(Skip(hatted)("bike_helmet"), "a fully printed blueprint holds nothing back");
        var everything = Printer(); foreach (var node in nodes) Call(everything, "SetCallingLevel", Val(node, "index"), Val(node, "implementedLevels"));
        Check(everything, "everything printed");
        // hunted parts ("pins"), whatever the tree says
        var hunter = Printer(); Assert.IsTrue((bool)Call(hunter, "SetPin", Static("Workshop", "CatalogNumber", "soda_can"), true, null));
        Assert.IsTrue(Skip(hunter)("soda_can")); Assert.IsFalse(Skip(hunter)("beer_bottle")); Assert.IsFalse(Skip(null)("soda_can"), "no state: nothing is held back");

        // the selection honours it: a hunted def is left on the shelf however light it is
        var cans = Pile("soda_can", "can", 4, 0.001f).ToList(); var hinges = Pile("door_hinge", "hinge", 12, 0.5f).ToList();
        var stock = Stock(cans.Concat(hinges));
        var open = Select(Printer(), stock, Skip(Printer()));
        CollectionAssert.IsSubsetOf(cans.Select(Uid), open.Select(Uid), "nobody is hunting cans: the lightest metal goes first");
        Assert.AreEqual(10, open.Length);
        var guarded = Select(hunter, stock, Skip(hunter));
        Assert.AreEqual(10, guarded.Length); Assert.IsFalse(guarded.Any(i => DefOf(i) == "soda_can"), "a hunted part is never fed to the Monument");
        CollectionAssert.AreEqual(new[] { 12, 0, 0, 0 }, Four(Static("Workshop", "MonumentStock", hunter, stock)), "and it is not counted as loadable");
        CollectionAssert.AreEqual(new[] { 16, 0, 0, 0 }, Four(Static("Workshop", "MonumentStock", Printer(), stock)));
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, Four(Static("Workshop", "MonumentStock", Printer(), null)));
        // and so is a part a printable blueprint still needs, when it happens to be a sorted type
        string needed = Needed(fresh).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault(id => Array.IndexOf(new[] { "Metal", "Plastic", "Glass", "Paper" }, Category(id)) >= 0);
        Assert.NotNull(needed, "no blueprint a new business can print takes a sorted-type part: pick another state for this check");
        int material = Array.IndexOf(new[] { "Metal", "Plastic", "Glass", "Paper" }, Category(needed));
        var parts = Pile(needed, "needed", 3, 0.0001f, 0.0001f).ToList(); var scrap = Pile(Sorted[material], "scrap", 12, 0.5f).ToList();
        var mixed = Stock(parts.Concat(scrap));
        var careful = Select(fresh, mixed, skipFresh);
        Assert.AreEqual(10, careful.Length); Assert.IsFalse(careful.Any(i => DefOf(i) == needed), needed + " is still needed by a blueprint: the Monument leaves it alone");
        CollectionAssert.IsSubsetOf(parts.Select(Uid), Select(fresh, mixed).Select(Uid), "without the guard they would have been the first to go");
    }

    // ------------------------------------------------------------------ the request (Workshop.ApplyBuild, action 8)

    [Test]
    public void AMonumentLoadIsAHostCheckedRequestOnItsOwnByteAndTheReplySaysLoadedOrPrinted()
    {
        byte feed = Const<byte>("Workshop", "ActionFeedMonument"); Assert.AreEqual(8, feed, "wire value");
        var apply = T("Workshop").GetMethod("ApplyBuild", Flags);
        var state = Printer(); int n = 0;
        IList Items(IEnumerable<string> defIds) => ItemList(defIds.Select(id => Item(id, "feed-" + n++)));
        object[] Args(object s, IList items, byte kind = 8, string id = "monument") => new object[] { s, kind, id, items, null };

        string before = Json(state);
        // the rules' own refusals come through with their reason, and change nothing
        var args = Args(state, Items(new string[0])); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.IsNotEmpty((string)args[4]); Assert.AreEqual(before, Json(state));
        args = Args(state, Items(Load(11, 0, 0, 0))); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.IsNotEmpty((string)args[4]); Assert.AreEqual(before, Json(state));
        args = Args(state, Items(Load(1, 1, 1, 1).Concat(new[] { "garden_gnome" }))); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.AreEqual(before, Json(state));
        var diskless = New("WorkshopState"); args = Args(diskless, Items(Load(1, 1, 1, 1))); Assert.IsFalse((bool)apply.Invoke(null, args));
        Assert.AreEqual(Const<string>("WorkshopState", "NoDiskText"), args[4]); Assert.AreEqual(Json(New("WorkshopState")), Json(diskless));
        // a doctored item (heavier than the catalog allows) or the same uid twice refuses the whole load
        var heavy = Items(Load(2, 2, 2, 2)); Set(heavy[0], "massKg", 400f);
        args = Args(state, heavy); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.AreEqual(before, Json(state));
        var twice = Items(Load(2, 2, 2, 2)); Set(twice[5], "uid", Val(twice[1], "uid"));
        args = Args(state, twice); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.AreEqual(before, Json(state));
        // the dead byte 7 ("build sculpture") and a calling byte with the Monument's id do nothing
        foreach (byte other in new byte[] { 0, 1, 2, 3, 5, 6, 7, 9, 255 }) {
            args = Args(state, Items(Load(1, 1, 1, 1)), other); Assert.IsFalse((bool)apply.Invoke(null, args), "byte " + other); Assert.AreEqual(before, Json(state), "byte " + other);
        }
        args = Args(state, Items(Load(1, 1, 1, 1)), 4); Assert.IsFalse((bool)apply.Invoke(null, args), "the Monument is not a blueprint"); Assert.AreEqual(before, Json(state));

        // a partial load: accepted, and the reply counts what went in and what is left
        args = Args(state, Items(Load(3, 0, 0, 0))); Assert.IsTrue((bool)apply.Invoke(null, args));
        Assert.AreEqual(Static("Workshop", "MonumentLoadedText", 3, 37), args[4]); StringAssert.Contains("3 pieces", (string)args[4]); StringAssert.Contains("37 to go", (string)args[4]);
        CollectionAssert.AreEqual(new[] { 3, 0, 0, 0 }, Fed(state));
        StringAssert.Contains("1 piece.", (string)Static("Workshop", "MonumentLoadedText", 1, 5), "one piece is not 'pieces'");
        // stolen scrap is scrap: the Monument does not ask where a can came from
        var hot = Items(Load(0, 2, 0, 0)); foreach (var item in hot) Set(item, "hot", true);
        args = Args(state, hot); Assert.IsTrue((bool)apply.Invoke(null, args)); CollectionAssert.AreEqual(new[] { 3, 2, 0, 0 }, Fed(state));
        // the load that finishes the tier: the reply announces the tier by name
        args = Args(state, Items(Load(7, 8, 10, 10))); Assert.IsTrue((bool)apply.Invoke(null, args)); Assert.AreEqual(1, Tier(state));
        Assert.AreEqual(Static("Workshop", "MonumentPrintedText", 1), args[4]);
        StringAssert.Contains((string)Val(Static("MonumentCatalog", "Tier", 1), "name"), (string)args[4]); Assert.IsTrue(Valid(state));
        // SolidWaste 3D tells a print from a load by how the reply starts, and the reply rides in a 512-byte message
        for (int tier = 1; tier <= MaxLevel; tier++) {
            string text = (string)Static("Workshop", "MonumentPrintedText", tier);
            StringAssert.StartsWith("MONUMENT TIER " + tier, text); StringAssert.Contains((string)Val(Static("MonumentCatalog", "Tier", tier), "name"), text); Assert.LessOrEqual(text.Length, 200);
        }
        foreach (var (loaded, left) in new[] { (1, 0), (100, 3900), (40, 1) })
            StringAssert.DoesNotStartWith("MONUMENT TIER", (string)Static("Workshop", "MonumentLoadedText", loaded, left));
    }

    [Test]
    public void TheBuildBytesAreStableAndTheThreeRetiredOnesAreNeverReused()
    {
        var actions = T("Workshop").GetFields(Flags).Where(f => f.IsLiteral && f.Name.StartsWith("Action", StringComparison.Ordinal) && f.FieldType == typeof(byte))
            .ToDictionary(f => f.Name, f => (byte)f.GetRawConstantValue());
        CollectionAssert.AreEquivalent(new[] { "ActionCustomize:2", "ActionPinDisplay:3", "ActionBuildCalling:4", "ActionWanted:5", "ActionInstallDecor:6", "ActionFeedMonument:8" },
            actions.Select(kv => kv.Key + ":" + kv.Value), "action bytes are wire values: append, never renumber");
        foreach (byte dead in new byte[] { 0, 1, 7 })
            CollectionAssert.DoesNotContain(actions.Values, dead, "0 / 1 were the gram bank's and 7 was 'build sculpture': dead on the wire for good");
        Assert.AreEqual(actions.Count, actions.Values.Distinct().Count());
        Assert.IsEmpty(T("Workshop").GetMember("ActionBuildSculpture", Flags)); Assert.IsEmpty(T("Workshop").GetMember("BuildSculpture", Flags));
        Assert.IsEmpty(T("WorkshopState").GetMember("BuildSculpture", Flags));
        // printing needs the owner standing in the shop; the sign's colour (it is out on the street), a hunt, a delivery and clearing a display do not
        var away = T("Workshop").GetMethod("AllowedAway", Flags);
        var gnome = Item("garden_gnome", "away-gnome", -1f, 30f); var pin = Static("WorkshopDisplayData", "From", "bottle_cabinet", gnome);
        foreach (byte anywhere in new byte[] { 2, 5, 6 }) Assert.IsTrue((bool)away.Invoke(null, new object[] { anywhere, null }), "action " + anywhere);
        Assert.IsTrue((bool)away.Invoke(null, new object[] { (byte)3, null }), "clearing a display"); Assert.IsFalse((bool)away.Invoke(null, new object[] { (byte)3, pin }), "choosing a find for a display");
        foreach (byte inShop in new byte[] { 4, 8 }) Assert.IsFalse((bool)away.Invoke(null, new object[] { inShop, null }), "a print is made at the laptop: action " + inShop);
        Assert.GreaterOrEqual(NetVersion.Protocol, 17, "the wire changed shape with SolidWaste 3D (build action 8, the shop-event and disk-drop RPCs, five new state fields); later bumps: 18-19 the Hoverhog (HogNet)");
    }

    [Test]
    public void AFullLoadFitsOneMessageWithRealisticItemIdsAndArrivesAsItLeft()
    {
        int maxChars = Const<int>("Workshop", "MaxOfferChars");
        var decode = T("Workshop").GetMethod("TryDecodeOffer", Flags);
        // the worst record a real load can hold: the sorted-type def whose catalog number and heaviest weight take the most digits,
        // a worn condition, the rescue grate's $1, stolen
        var sortedDefs = ((IEnumerable)Val(T("ItemCatalog"), "All")).Cast<object>()
            .Where(d => Array.IndexOf(new[] { "Metal", "Plastic", "Glass", "Paper" }, Val(d, "category").ToString()) >= 0).ToArray();
        Assert.Greater(sortedDefs.Length, 20);
        int Digits(object def) => ((int)Static("Workshop", "CatalogNumber", Val(def, "id"))).ToString().Length + Mathf.RoundToInt((float)Val(def, "maxMass") * 1000f).ToString().Length;
        var worst = sortedDefs.OrderByDescending(Digits).First(); string worstId = (string)Val(worst, "id");
        object Worst(string uid) { var item = Item(worstId, uid, (float)Val(worst, "maxMass"), 1f, true); Set(item, "condition", 0.987f); return item; }
        void RoundTrips(IList items, string what)
        {
            string wire = (string)Static("Workshop", "EncodeOffer", items);
            Assert.NotNull(wire, what + ": a full load must be sendable"); Assert.LessOrEqual(wire.Length, maxChars, what);
            var args = new object[] { wire, null }; Assert.IsTrue((bool)decode.Invoke(null, args), what);
            var back = (IList)args[1]; Assert.AreEqual(items.Count, back.Count, what);
            for (int i = 0; i < items.Count; i++) {
                Assert.AreEqual(Val(items[i], "uid"), Val(back[i], "uid"), what); Assert.AreEqual(Val(items[i], "defId"), Val(back[i], "defId"), what); Assert.AreEqual(Val(items[i], "hot"), Val(back[i], "hot"), what);
                Assert.AreEqual((float)Val(items[i], "massKg"), (float)Val(back[i], "massKg"), 0.0006f, what); Assert.AreEqual((float)Val(items[i], "condition"), (float)Val(back[i], "condition"), 0.0006f, what);
                Assert.AreEqual((float)Val(items[i], "value"), (float)Val(back[i], "value"), 0.006f, what);
                var plausible = new object[] { back[i], null }; Assert.IsTrue((bool)T("EbaeItemValidation").GetMethod("IsPlausible").Invoke(null, plausible), what + ": what arrives still passes the host's catalog check");
            }
            // and what arrives is a load the host's rules accept as-is
            var state = Printer(9); var apply = new object[] { state, (byte)8, "monument", back, null };
            Assert.IsTrue((bool)T("Workshop").GetMethod("ApplyBuild", Flags).Invoke(null, apply), what + ": " + apply[4]); Assert.AreEqual(items.Count, Fed(state).Sum(), what);
        }
        // ids as the game mints them: GameSession.NewUid is twelve hex characters
        string minted = (string)Static("GameSession", "NewUid"); Assert.AreEqual(12, minted.Length);
        var rolled = Enumerable.Range(0, Cap).Select(i => Worst((0xA1B2C3D4E5F0L + i * 7919L).ToString("x12"))).ToList();
        Assert.IsTrue(rolled.All(i => Uid(i).Length == 12));
        RoundTrips(ItemList(rolled), "a hundred rolled items");
        // the debug drawer's scrap after a long session ("dbs<frame>-<batch>-<catalog number>-<n>"), one type after the other
        var drawer = new List<object>();
        for (int i = 0; i < Cap; i++) drawer.Add(Item(Sorted[i % 4], $"dbs{9876543:x}-{12:x}-{(int)Static("Workshop", "CatalogNumber", Sorted[i % 4]):x}-{900 + i}"));
        RoundTrips(ItemList(drawer), "a hundred debug-drawer items");
        // a real mixed shelf picked by the game itself: every sorted def, lightest first, a request's worth
        var shelf = new List<object>(); int serial = 0;
        foreach (var def in sortedDefs) for (int copy = 0; copy < 3; copy++)
            shelf.Add(Item((string)Val(def, "id"), (0xC0FFEE000000L + serial++ * 104729L).ToString("x12"), Mathf.Lerp((float)Val(def, "minMass"), (float)Val(def, "maxMass"), copy / 2f)));
        var picked = Select(Printer(9), Stock(shelf));
        Assert.AreEqual(Cap, picked.Length); RoundTrips(ItemList(picked), "the game's own pick from a mixed shelf");
        // one item over the request limit is not something the game ever builds, and the host would refuse it
        var tooMany = ItemList(Enumerable.Range(0, Const<int>("Workshop", "MaxOfferItems") + 1).Select(i => Item("soda_can", "x" + i)));
        string big = (string)Static("Workshop", "EncodeOffer", tooMany);
        if (big != null) { var args = new object[] { big, null }; Assert.IsFalse((bool)decode.Invoke(null, args), "more items than one request may carry"); }
    }

    // ------------------------------------------------------------------ a damaged save

    [Test]
    public void ADamagedDecorFieldIsRepairedOnLoadAndNeverCostsTheMonument()
    {
        // A bad sign colour, an unknown piece bit or a stale display pin used to reset the whole workshop state on load.
        // The Monument (thousands of kept pieces), its load, the disk and the printed blueprints cannot be re-derived.
        var broken = Printer(6); Set(broken, "fed", Mats(30, 20, 10, 5)); Set(broken, "colorIndex", 99);
        Set(broken, "completed", (ushort)((1 << 13) | (1 << 15))); // the doorbell, and a bit no piece owns
        Call(broken, "SetCallingLevel", (int)Val(Static("CallingCatalog", "Get", "work_gloves"), "index"), 2);
        var pin = New("WorkshopDisplayData"); Set(pin, "recipeId", "grand_display"); Set(pin, "uid", "gone"); Set(pin, "defId", "garden_gnome"); Set(pin, "massKg", 1f); Set(pin, "condition", 1f);
        ((IList)Val(broken, "displays")).Add(pin); // a pin on a display this business never bought
        Assert.IsFalse((bool)Call(broken, "ValidIgnoringCallings"), "the fixture really is a state the old load path threw away");

        var repaired = Call(broken, "Clone"); Call(repaired, "RepairDecor");
        Assert.IsTrue(Valid(repaired), "repaired in place");
        Assert.AreEqual(0, (int)Val(repaired, "colorIndex")); Assert.IsEmpty((IList)Val(repaired, "displays"));
        Assert.IsTrue((bool)Call(repaired, "Completed", "doorbell"), "a real piece stays installed");
        Assert.AreEqual(6, Tier(repaired)); CollectionAssert.AreEqual(new[] { 30, 20, 10, 5 }, Fed(repaired)); Assert.AreEqual(1, (int)Val(repaired, "disk"));
        string once = Json(repaired); Call(repaired, "RepairDecor"); Assert.AreEqual(once, Json(repaired), "repairing twice changes nothing");
        var fine = Printer(2); string before = Json(fine); Call(fine, "RepairDecor"); Assert.AreEqual(before, Json(fine), "a healthy state is untouched");

        // and the real load path (GameSession.LoadWorkshop: the silent one, no events, no save) keeps it all
        var session = T("GameSession"); var workshopField = session.GetField("workshop", Flags);
        var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        object previousWorkshop = workshopField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels);
        try {
            levels.Clear();
            Static("GameSession", "LoadWorkshop", broken);
            var loaded = workshopField.GetValue(null);
            Assert.IsTrue(Valid(loaded)); Assert.AreEqual(6, Tier(loaded)); CollectionAssert.AreEqual(new[] { 30, 20, 10, 5 }, Fed(loaded));
            Assert.AreEqual(1, (int)Val(loaded, "disk")); Assert.AreEqual(2, (int)Call(loaded, "CallingLevel", "work_gloves"));
            Assert.IsTrue((bool)Call(loaded, "Completed", "doorbell"));
        }
        finally { workshopField.SetValue(null, previousWorkshop); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted"); }
    }

    [Test]
    public void ALoadWithoutTheDiskIsNotAValidState()
    {
        var state = New("WorkshopState"); Set(state, "fed", Mats(1, 0, 0, 0));
        Assert.IsFalse(Valid(state), "pieces cannot be loaded into a printer the business does not have");
        Call(state, "NormalizePrinter");
        Assert.IsTrue(Valid(state)); Assert.AreEqual(1, (int)Val(state, "disk"), "a local save that loaded anything is handed its disk");
    }
}

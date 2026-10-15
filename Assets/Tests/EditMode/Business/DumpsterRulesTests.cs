using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Dumpsters (Trash/Dumpsters, Docs/Progression.md §6.11): which records are dumpster bags, where a new bag
/// goes, when a truck is backed up to one, and the Dumpster Dunker's shop and replication contract.</summary>
public class DumpsterRulesTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args)
    {
        foreach (var m in Runtime(type).GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            return m.Invoke(null, args);
        }
        Assert.Fail(type + "." + method + "/" + args.Length + " not found");
        return null;
    }
    static T Const<T>(string field) => (T)Runtime("DumpsterRules").GetField(field, Static).GetValue(null);
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static string Check(bool driver, int level, bool rear, bool busy, float speed, bool reach, int bags, bool permit, bool fits) =>
        Call("DumpsterRules", "Check", driver, level, rear, busy, speed, reach, bags, permit, fits).ToString();

    [Test]
    public void DumpsterBagsLiveInSlotsTheCurbNeverUses()
    {
        int first = Const<int>("FirstSlot"), cap = Const<int>("Capacity");
        Assert.Greater(first, 5, "curb slots are 0..5 (PickupStop.MaxSlots)");
        Assert.Less(first + cap, 31, "the stop's slot mask covers 0..30");
        for (int slot = 0; slot < 6; slot++) Assert.IsFalse((bool)Call("DumpsterRules", "IsDumpsterSlot", slot));
        Assert.IsTrue((bool)Call("DumpsterRules", "IsDumpsterSlot", first));
        Assert.IsTrue((bool)Call("DumpsterRules", "IsDumpsterSlot", first + cap - 1));
        Assert.IsFalse((bool)Call("DumpsterRules", "IsDumpsterSlot", first + cap));
    }

    [Test]
    public void TheSlotMaskCountsAndFindsRoom()
    {
        int first = Const<int>("FirstSlot"), cap = Const<int>("Capacity");
        int curbOnly = 0b111111;
        Assert.AreEqual(0, (int)Call("DumpsterRules", "Count", curbOnly), "curb bags are not in the dumpster");
        Assert.AreEqual(first, (int)Call("DumpsterRules", "FreeSlot", curbOnly));
        int two = curbOnly | 1 << first | 1 << (first + 2);
        Assert.AreEqual(2, (int)Call("DumpsterRules", "Count", two));
        Assert.AreEqual(first + 1, (int)Call("DumpsterRules", "FreeSlot", two));
        int full = 0;
        for (int i = 0; i < cap; i++) full |= 1 << (first + i);
        Assert.AreEqual(cap, (int)Call("DumpsterRules", "Count", full));
        Assert.AreEqual(-1, (int)Call("DumpsterRules", "FreeSlot", full));
    }

    [Test]
    public void ANewBagGoesWhereThereIsRoomAndMostlyInTheDumpster()
    {
        Assert.IsFalse((bool)Call("DumpsterRules", "GoesInDumpster", "cc-1/a", true, false), "a full dumpster takes nothing");
        Assert.IsTrue((bool)Call("DumpsterRules", "GoesInDumpster", "cc-1/a", false, true), "a full curb sends it to the dumpster");
        int inDumpster = 0;
        for (int i = 0; i < 400; i++) if ((bool)Call("DumpsterRules", "GoesInDumpster", "cc-market-2/" + i, true, true)) inDumpster++;
        Assert.That(inDumpster / 400f, Is.EqualTo(Const<float>("PutOutShare")).Within(0.08f));
        Assert.AreEqual(Call("DumpsterRules", "GoesInDumpster", "cc-1/same", true, true), Call("DumpsterRules", "GoesInDumpster", "cc-1/same", true, true));
    }

    [Test]
    public void OnlyCouponCommonsHasDumpsters()
    {
        Assert.IsTrue((bool)Call("DumpsterRules", "HasDumpster", "coupon_commons"));
        foreach (var id in new[] { "mildly_pleasant", "minivan_meadows", "stackington_heights", "snootington_hills", "billionaire_bluffs", "", null })
            Assert.IsFalse((bool)Call("DumpsterRules", "HasDumpster", id), id);
    }

    [Test]
    public void TheTruckMustBeBackedUpSquareAndClose()
    {
        float reach = Const<float>("Reach"), half = Const<float>("HalfWidth"), yaw = Const<float>("MaxYawOff");
        const float rear = -2.2f; // ShopModKit.BodyRearZ
        Assert.IsTrue((bool)Call("DumpsterRules", "InReach", new Vector3(0f, 1f, rear - 1.5f), 0f));
        Assert.IsTrue((bool)Call("DumpsterRules", "InReach", new Vector3(half - 0.1f, 1f, rear - reach + 0.1f), yaw - 1f));
        Assert.IsFalse((bool)Call("DumpsterRules", "InReach", new Vector3(0f, 1f, rear - reach - 0.5f), 0f), "too far back");
        Assert.IsFalse((bool)Call("DumpsterRules", "InReach", new Vector3(half + 0.3f, 1f, rear - 1.5f), 0f), "off to the side");
        Assert.IsFalse((bool)Call("DumpsterRules", "InReach", new Vector3(0f, 1f, rear - 1.5f), yaw + 5f), "at an angle");
        Assert.IsFalse((bool)Call("DumpsterRules", "InReach", new Vector3(0f, 1f, 3f), 0f), "in front of the truck");
        Assert.IsFalse((bool)Call("DumpsterRules", "InReach", new Vector3(0f, 1f, rear - 1.5f), 180f), "nose-in: it is the hopper that has to face it");
    }

    [Test]
    public void TheLiftRefusesInAFixedOrder()
    {
        float max = Const<float>("MaxSpeed");
        Assert.AreEqual("Ok", Check(true, 1, true, false, 0f, true, 3, true, true));
        Assert.AreEqual("NotDriver", Check(false, 0, false, true, 9f, false, 0, false, false));
        Assert.AreEqual("WrongTruck", Check(true, 2, false, false, 0f, true, 3, true, true));
        Assert.AreEqual("NoLift", Check(true, 0, true, false, 0f, true, 3, true, true));
        Assert.AreEqual("Busy", Check(true, 1, true, true, 0f, true, 3, true, true));
        Assert.AreEqual("NoDumpster", Check(true, 1, true, false, 0f, false, 3, true, true));
        Assert.AreEqual("Moving", Check(true, 1, true, false, max + 0.5f, true, 3, true, true));
        Assert.AreEqual("Empty", Check(true, 1, true, false, 0f, true, 0, true, true));
        Assert.AreEqual("NoPermit", Check(true, 1, true, false, 0f, true, 3, false, true));
        Assert.AreEqual("Full", Check(true, 1, true, false, 0f, true, 3, true, false));
    }

    [Test]
    public void TheSecondLevelTipsMoreThanTwiceAsFast()
    {
        float one = (float)Call("DumpsterRules", "SecondsPerBag", 1), two = (float)Call("DumpsterRules", "SecondsPerBag", 2);
        Assert.Less(two * 2f, one);
        Assert.AreEqual(one, (float)Call("DumpsterRules", "SecondsPerBag", 0));
    }

    [Test]
    public void ScrapazonSellsTheDunkerForTheRearLoaderWithTheCommonsPermit()
    {
        var item = Call("ScrapazonCatalog", "Get", "truck_dumpster_lift");
        Assert.NotNull(item);
        Assert.AreEqual("truck", Field<string>(item, "dept"));
        Assert.AreEqual(2, Field<int>(item, "maxLevel"));
        CollectionAssert.AreEqual(new[] { 2400f, 9000f }, Field<float[]>(item, "prices"));
        CollectionAssert.AreEqual(new[] { 4, 7 }, Field<int[]>(item, "rankRequired"), "rank 4: on sale the moment the Meadows contract opens Commons");
        CollectionAssert.AreEquivalent(new[] { "truck_model:1", "permit_coupon_commons" }, Field<string[][]>(item, "prereqs")[0]);
    }

    [Test]
    public void ItsLevelReplicatesInTruckNibbleFourteen()
    {
        ulong packed = 2UL << (14 * 4);
        Assert.IsTrue((bool)Call("BusinessUpgradeNet", "ValidTruckLevels", packed));
        Assert.AreEqual(2, (int)Call("BusinessUpgradeNet", "DecodeTruckLevel", packed, "truck_dumpster_lift"));
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "ValidTruckLevels", 3UL << (14 * 4)), "it has two levels");
    }
}

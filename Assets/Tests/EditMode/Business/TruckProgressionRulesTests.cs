using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>Truck progression contracts are pure; no save, scene or network session is needed.</summary>
public class TruckProgressionRulesTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => Runtime(type).GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static float Capacity(int model, int level) => (float)Call("TruckProgression", "CapacityKg", model, level);
    static object Product(string id) => Call("ScrapazonCatalog", "Get", id);
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);

    [TestCase(0, 0, 120f)]
    [TestCase(0, 1, 160f)]
    [TestCase(0, 2, 200f)]
    [TestCase(0, 3, 240f)]
    [TestCase(0, 4, 300f)]
    [TestCase(1, 0, 200f)]
    [TestCase(1, 1, 240f)]
    [TestCase(1, 2, 280f)]
    [TestCase(1, 3, 320f)]
    [TestCase(1, 4, 380f)]
    [TestCase(-10, -10, 120f)]
    [TestCase(99, 99, 380f)]
    public void CapacityCombinesChassisAndTransferableUpgrade(int model, int level, float expected)
    {
        Assert.AreEqual(expected, Capacity(model, level));
        Assert.AreEqual(expected, Call("UpgradeEffects", "CapacityKgFor", model, level));
    }

    [Test]
    public void EveryHopperInvestmentCarriesOverInFull()
    {
        for (int level = 0; level <= 4; level++)
        {
            Assert.AreEqual(80f, Capacity(1, level) - Capacity(0, level));
            Assert.AreEqual(Capacity(0, level) - 120f, Capacity(1, level) - 200f);
        }
    }

    [TestCase(120f, 320f)]
    [TestCase(160f, 320f)]
    [TestCase(200f, 400f)]
    [TestCase(380f, 760f)]
    [TestCase(-1f, 320f)]
    [TestCase(float.NaN, 320f)]
    [TestCase(float.PositiveInfinity, 320f)]
    public void MiniTruckDoesNotAlsoNerfDepotSpace(float payload, float expected) =>
        Assert.AreEqual(expected, Call("DepotBacklog", "CapacityForTruck", payload));

    [Test]
    public void StarterImprovementAndTruckHaveEarlyRankOnePrices()
    {
        var truck = Product("truck_model");
        var hopper = Product("truck_capacity");
        Assert.NotNull(truck);
        Assert.AreEqual("truck", Field<string>(truck, "dept"));
        Assert.AreEqual(1, Field<int>(truck, "maxLevel"));
        Assert.AreEqual(600f, Field<float[]>(truck, "prices")[0]);
        Assert.AreEqual(1, Field<int[]>(truck, "rankRequired")[0]);
        Assert.AreEqual(100f, Field<float[]>(hopper, "prices")[0]);
        Assert.AreEqual(1, Field<int[]>(hopper, "rankRequired")[0]);
        StringAssert.Contains("carry over", Field<string[]>(truck, "levelEffects")[0]);
    }

    [TestCase(false, false, false, false, 0f, false, "not available")]
    [TestCase(true, false, false, false, 0f, false, "own depot")]
    [TestCase(true, true, true, false, 0f, false, "get out")]
    [TestCase(true, true, false, true, 0f, false, "riding steps")]
    [TestCase(true, true, false, false, 1f, false, "complete stop")]
    [TestCase(true, true, false, false, float.NaN, false, "complete stop")]
    [TestCase(true, true, false, false, 0.05f, true, null)]
    public void TradeInRequiresOwnParkedUnoccupiedTruck(bool found, bool ownDepot, bool driver, bool riders, float speed, bool expected, string reason)
    {
        object[] args = { found, ownDepot, driver, riders, speed, null };
        Assert.AreEqual(expected, Call("TruckProgression", "TradeInReady", args));
        if (reason != null) StringAssert.Contains(reason, (string)args[5]);
        else Assert.IsNull(args[5]);
    }

    [Test]
    public void ExplicitOwnerEffectsClampWithoutReadingLocalSave()
    {
        var stock = Call("UpgradeEffects", "EngineAtLevel", -1, false);
        var fast = Call("UpgradeEffects", "EngineAtLevel", 99, false);
        var gold = Call("UpgradeEffects", "EngineAtLevel", 0, true);
        Assert.AreEqual(10f, Field<float>(stock, "maxSpeed"));
        Assert.AreEqual(17f, Field<float>(fast, "maxSpeed"));
        Assert.AreEqual(18f, Field<float>(gold, "maxSpeed"));
        Assert.IsFalse(Field<bool>(Call("UpgradeEffects", "HandlingAtLevel", -1), "drift"));
        Assert.IsTrue(Field<bool>(Call("UpgradeEffects", "HandlingAtLevel", 99), "drift"));
        Assert.IsFalse(Field<bool>(Call("UpgradeEffects", "ArmAtLevel", -1), "present"));
        Assert.IsTrue(Field<bool>(Call("UpgradeEffects", "ArmAtLevel", 99), "litterVac"));
    }

    [Test]
    public void TradeInDeliveryExplainsTransferAndSafeParking()
    {
        string copy = (string)Call("ScrapazonApp", "DeliveryDescription", Product("truck_model"));
        StringAssert.Contains("stop", copy);
        StringAssert.Contains("riders", copy);
        StringAssert.Contains("Cargo and upgrades transfer", copy);
    }
}

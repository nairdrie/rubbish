using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>The Hoverhog's pure rules and its shop / replication contract (Docs/Progression.md §6.10).</summary>
public class HogRulesTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args)
    {
        foreach (var m in Runtime(type).GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            var ps = m.GetParameters();
            bool fits = true;
            for (int i = 0; i < ps.Length && fits; i++) fits = args[i] == null || ps[i].ParameterType.IsInstanceOfType(args[i]);
            if (fits) return m.Invoke(null, args);
        }
        Assert.Fail(type + "." + method + "/" + args.Length + " not found");
        return null;
    }
    static T Const<T>(string type, string field) => (T)Runtime(type).GetField(field, Static).GetValue(null);
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static float Battery(float b, float dt, bool airborne, bool onPad, float flight = 300f) => (float)Call("HogRules", "BatteryAfter", b, dt, airborne, onPad, flight);
    static string Claw(bool pilot, bool busy, float height, bool any, bool fits, bool permit) =>
        Call("HogRules", "CheckClaw", pilot, busy, height, any, fits, permit).ToString();
    static string Dump(bool pilot, bool cargo, bool roof, bool ground, float y, bool room) =>
        Call("HogRules", "CheckDump", pilot, cargo, roof, ground, y, room).ToString();

    [Test]
    public void AFullBatteryLastsExactlyTheAdvertisedFlight()
    {
        float flight = Const<float>("HogTuning", "FlightSeconds");
        Assert.AreEqual(0.5f, Battery(1f, flight * 0.5f, true, false), 1e-4f);
        Assert.AreEqual(0f, Battery(1f, flight * 2f, true, false));
        // Being over the pad does not charge it: only sitting on it does.
        Assert.Less(Battery(0.5f, 1f, true, true), 0.5f);
    }

    [Test]
    public void ItChargesOnlyOnItsOwnPad()
    {
        float charge = Const<float>("HogTuning", "ChargeSeconds");
        Assert.AreEqual(1f, Battery(0f, charge, false, true), 1e-4f);
        Assert.AreEqual(1f, Battery(0.9f, charge, false, true));
        Assert.AreEqual(0.6f, Battery(0.6f, 1000f, false, false));
    }

    [Test]
    public void AFlatBatterySulksBackToAFifthAndNoFurther()
    {
        float sulk = Const<float>("HogTuning", "SulkSeconds"), to = Const<float>("HogTuning", "SulkCharge");
        Assert.AreEqual(to * 0.5f, Battery(0f, sulk * 0.5f, false, false), 1e-4f);
        Assert.AreEqual(to, Battery(0f, sulk * 10f, false, false), 1e-4f);
    }

    [Test]
    public void TheClawRefusesInAFixedOrder()
    {
        float max = Const<float>("HogTuning", "ClawMaxHeight");
        Assert.AreEqual("Ok", Claw(true, false, max, true, true, true));
        Assert.AreEqual("NotPilot", Claw(false, true, 99f, false, false, false));
        Assert.AreEqual("Busy", Claw(true, true, 99f, false, false, false));
        Assert.AreEqual("TooHigh", Claw(true, false, max + 0.1f, true, true, true));
        Assert.AreEqual("NoBag", Claw(true, false, 2f, false, false, false));
        Assert.AreEqual("NoPermit", Claw(true, false, 2f, true, false, false));
        Assert.AreEqual("Full", Claw(true, false, 2f, true, false, true));
    }

    [Test]
    public void TheBombBayUnloadsOnTheRoofAndLittersElsewhere()
    {
        float water = Const<float>("HogTuning", "WaterlineY");
        Assert.AreEqual("Ok", Dump(true, true, true, true, 7.8f, true));
        Assert.AreEqual("DepotFull", Dump(true, true, true, true, 7.8f, false));
        Assert.AreEqual("Ok", Dump(true, true, false, true, 0f, false), "depot room only matters over the roof");
        Assert.AreEqual("NoGround", Dump(true, true, false, false, 0f, true));
        Assert.AreEqual("Water", Dump(true, true, false, true, water - 0.1f, true));
        Assert.AreEqual("Empty", Dump(true, false, true, true, 0f, true));
        Assert.AreEqual("NotPilot", Dump(false, true, true, true, 0f, true));
    }

    [Test]
    public void TheRoofAndThePadAreWhereTheHelipadIsBuilt()
    {
        var pad = Const<Vector3>("HogTuning", "PadLocal");
        var hatch = Const<Vector3>("HogTuning", "HatchLocal");
        var hut = Const<Vector3>("HogTuning", "StairHutLocal");
        var arrive = Const<Vector3>("HogTuning", "RoofArrivalLocal");
        foreach (var p in new[] { pad, hatch, hut, arrive })
            Assert.IsTrue((bool)Call("HogRules", "OverRoof", p + Vector3.up), p + " is on the roof");
        Assert.IsTrue((bool)Call("HogRules", "OnPad", pad));
        Assert.IsFalse((bool)Call("HogRules", "OnPad", hatch), "the hatch is beside the pad, not on it");
        Assert.IsFalse((bool)Call("HogRules", "OnPad", pad + Vector3.up * 5f), "hovering over the pad is not on it");
        Assert.IsFalse((bool)Call("HogRules", "OverRoof", new Vector3(0f, 1f, 5f)), "the apron is not the roof");
        float above = Const<float>("HogTuning", "UnloadAbove");
        Assert.IsFalse((bool)Call("HogRules", "OverRoof", pad + Vector3.up * (above + 1f)));
    }

    [Test]
    public void TheClawGoesDownHoldsAndComesBack()
    {
        Assert.AreEqual(0f, (float)Call("HogRules", "ClawReach01", 0f));
        float drop = Const<float>("HogTuning", "ClawDrop"), grip = Const<float>("HogTuning", "ClawGrip");
        Assert.AreEqual(1f, (float)Call("HogRules", "ClawReach01", drop + grip * 0.5f));
        Assert.AreEqual(0f, (float)Call("HogRules", "ClawReach01", 1f));
        Assert.Less(drop + grip + Const<float>("HogTuning", "ClawLift"), 1f);
    }

    [Test]
    public void BatteryPacksIntoAByte()
    {
        foreach (float b in new[] { 0f, 0.2f, 0.5f, 1f })
            Assert.AreEqual(b, (float)Call("HogRules", "UnpackBattery", Call("HogRules", "PackBattery", b)), 0.003f);
    }

    [Test]
    public void ScrapazonSellsItInTheAirDepartmentAsALateGamePurchase()
    {
        var item = Call("ScrapazonCatalog", "Get", "hoverhog");
        Assert.NotNull(item);
        Assert.AreEqual("air", Field<string>(item, "dept"));
        Assert.AreEqual(1, Field<int>(item, "maxLevel"));
        Assert.AreEqual(85000f, Field<float[]>(item, "prices")[0]);
        Assert.AreEqual(11, Field<int[]>(item, "rankRequired")[0]);
        CollectionAssert.AreEquivalent(new[] { "truck_arm:2", "permit_billionaire_bluffs" }, Field<string[][]>(item, "prereqs")[0]);
        bool listed = false;
        foreach (var d in (Array)Runtime("ScrapazonCatalog").GetField("Departments", Static).GetValue(null))
            listed |= d.ToString().Contains("air,");
        Assert.IsTrue(listed, "the Air department has a nav button");
    }

    // ------------------------------------------------------------------ stage 2: the upgrade lines

    [Test]
    public void EveryLineHasThreeLevelsInTheAirDepartmentAndNeedsTheHoverhog()
    {
        var want = new (string id, float[] prices)[]
        {
            ("hog_belly", new[] { 15000f, 35000f, 70000f }), ("hog_claw", new[] { 20000f, 75000f, 160000f }),
            ("hog_rotors", new[] { 12000f, 30000f, 60000f }), ("hog_battery", new[] { 10000f, 25000f, 55000f }),
            ("hog_sat_pad", new[] { 10000f, 10000f, 10000f }),
        };
        foreach (var (id, prices) in want)
        {
            var item = Call("ScrapazonCatalog", "Get", id);
            Assert.NotNull(item, id);
            Assert.AreEqual("air", Field<string>(item, "dept"), id);
            CollectionAssert.AreEqual(prices, Field<float[]>(item, "prices"), id);
            Assert.AreEqual(3, Field<string[]>(item, "levelEffects").Length, id);
            CollectionAssert.Contains(Field<string[][]>(item, "prereqs")[0], "hoverhog", id);
            foreach (int rank in Field<int[]>(item, "rankRequired")) Assert.GreaterOrEqual(rank, 11, id);
        }
        CollectionAssert.Contains(Field<string[][]>(Call("ScrapazonCatalog", "Get", "hog_claw"), "prereqs")[2], "key_to_the_city");
        CollectionAssert.AreEquivalent(new[] { "hog_battery:2", "hog_belly:2" }, Field<string[][]>(Call("ScrapazonCatalog", "Get", "hog_battery"), "prereqs")[2]);
    }

    [Test]
    public void LevelsPackIntoTenBitsAndBack()
    {
        var levels = Activator.CreateInstance(Runtime("HogLevels"));
        var t = levels.GetType();
        t.GetField("belly").SetValue(levels, 3); t.GetField("claw").SetValue(levels, 2); t.GetField("rotors").SetValue(levels, 1);
        t.GetField("battery").SetValue(levels, 0); t.GetField("pads").SetValue(levels, 3);
        ushort packed = (ushort)t.GetMethod("Pack").Invoke(levels, null);
        Assert.AreEqual(3 | 2 << 2 | 1 << 4 | 0 << 6 | 3 << 8, packed);
        var back = Call("HogLevels", "Unpack", packed);
        Assert.AreEqual(2, Field<int>(back, "claw"));
        Assert.AreEqual(3, Field<int>(back, "pads"));
        Assert.IsTrue((bool)Call("HogLevels", "Valid", packed));
        Assert.IsFalse((bool)Call("HogLevels", "Valid", (ushort)(1 << 10)));
    }

    [Test]
    public void TheTablesAreTheShopsNumbers()
    {
        CollectionAssert.AreEqual(new[] { 300f, 450f, 600f, 850f }, new[] { 0, 1, 2, 3 }.Select(l => (float)Call("HogTuning", "BellyKgAt", l)));
        CollectionAssert.AreEqual(new[] { 12f, 15f, 18f, 22f }, new[] { 0, 1, 2, 3 }.Select(l => (float)Call("HogTuning", "SpeedAt", l)));
        CollectionAssert.AreEqual(new[] { 300f, 480f, 720f, 720f }, new[] { 0, 1, 2, 3 }.Select(l => (float)Call("HogTuning", "FlightSecondsAt", l)));
        Assert.AreEqual(40f, (float)Call("HogTuning", "CeilingAt", 2));
        Assert.AreEqual(60f, (float)Call("HogTuning", "CeilingAt", 3));
        Assert.AreEqual(1, (int)Call("HogTuning", "ClawBagsAt", 0));
        Assert.AreEqual(3, (int)Call("HogTuning", "ClawBagsAt", 1));
        Assert.AreEqual(0f, (float)Call("HogTuning", "ClawSlipAt", 1));
        Assert.AreEqual(850f, (float)Call("HogTuning", "BellyKgAt", 99), "levels clamp");
    }

    [Test]
    public void ABiggerBatteryFliesLonger()
    {
        Assert.AreEqual(0.5f, Battery(1f, 360f, true, false, 720f), 1e-4f);
        Assert.AreEqual(0f, Battery(1f, 360f, true, false, 300f));
    }

    [Test]
    public void TheBeamNeedsALowSlowHoverAndClawThreeMayCrawl()
    {
        string Beam(int claw, float height, float speed) => Call("HogRules", "CheckBeam", true, claw, height, speed).ToString();
        Assert.AreEqual("NoBeam", Beam(1, 5f, 0f));
        Assert.AreEqual("Ok", Beam(2, 12f, 2f));
        Assert.AreEqual("TooHigh", Beam(2, 12.5f, 0f));
        Assert.AreEqual("TooFast", Beam(2, 5f, 4f));
        Assert.AreEqual("Ok", Beam(3, 5f, 6f));
        Assert.AreEqual("TooFast", Beam(3, 5f, 8f));
        Assert.AreEqual("NotPilot", Call("HogRules", "CheckBeam", false, 3, 5f, 0f).ToString());
    }

    [Test]
    public void TheReactorTurnsAKilogramIntoThreeSecondsAndBellyThreeEmptiesInThree()
    {
        Assert.AreEqual(30f / 720f, (float)Call("HogRules", "ReactorCredit", 10f, 720f), 1e-5f);
        Assert.AreEqual(0.3f, (float)Call("HogRules", "DumpInterval", 2, 100));
        Assert.AreEqual(0.03f, (float)Call("HogRules", "DumpInterval", 3, 100), 1e-5f);
        Assert.AreEqual(0.3f, (float)Call("HogRules", "DumpInterval", 3, 4), "a small load is not slowed down");
    }

    [Test]
    public void ThereIsOneSatellitePadPerRicherDistrictInBuyingOrder()
    {
        var pads = (Array)Runtime("HogTuning").GetField("SatPads", Static).GetValue(null);
        Assert.AreEqual(3, pads.Length);
        CollectionAssert.AreEqual(new[] { "minivan_meadows", "snootington_hills", "billionaire_bluffs" },
            pads.Cast<object>().Select(p => Field<string>(p, "districtId")));
        foreach (var p in pads) Assert.AreNotEqual(Vector3.zero, Field<Vector3>(p, "pos"));
    }

    [Test]
    public void OwningOneReplicatesInTheTruckNibbles()
    {
        // Nibble 13, after the thirteen ids that were there before (TruckUpgradeReplicationTests pins the first five).
        ulong packed = 1UL << (13 * 4);
        Assert.IsTrue((bool)Call("BusinessUpgradeNet", "ValidTruckLevels", packed));
        Assert.AreEqual(1, (int)Call("BusinessUpgradeNet", "DecodeTruckLevel", packed, "hoverhog"));
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "ValidTruckLevels", 2UL << (13 * 4)), "it has one level");
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "ValidTruckLevels", 1UL << (15 * 4)), "nibble 15 is still unused");
    }
}

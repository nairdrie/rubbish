using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The Hauler calling's pure rules (Assets/Scripts/Callings/Hauler/HaulerTuning.cs: HaulerTuning + HaulerRules): Grabby Hand
/// reach by level, backpack room / weight / crush, on-foot pay against a hopper's, the boost and heat machines, skating
/// momentum and what a wall does. Game code is reached by reflection (this assembly cannot reference Assembly-CSharp).
/// </summary>
public class HaulerTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Rule(string method, params object[] args) => T("HaulerRules").GetMethod(method, Flags).Invoke(null, args);
    static object Tune(string field)
    {
        var f = T("HaulerTuning").GetField(field, Flags); Assert.NotNull(f, "HaulerTuning." + field);
        return f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
    }
    static float F(string field) => Convert.ToSingle(Tune(field));
    static object Field(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    static object With(object boxed, string field, object data) { boxed.GetType().GetField(field, Flags).SetValue(boxed, data); return boxed; }
    static object Node(string id) => T("CallingCatalog").GetMethod("Get", Flags).Invoke(null, new object[] { id });
    static int Buildable(string id) => (int)Node(id).GetType().GetField("implementedLevels", Flags).GetValue(Node(id));

    // ------------------------------------------------------------------ the tree offers exactly what has rules here

    [Test]
    public void EveryHaulerLevelOnTheTreeIsBuildableAndHasItsHowTo()
    {
        foreach (var (id, levels) in new[] { ("grabber_pole", 3), ("calling_rocket_blades", 3), ("calling_compactor_backpack", 3), ("calling_one_man_route", 1) }) {
            var node = Node(id); Assert.NotNull(node, id);
            Assert.AreEqual("Hauler", node.GetType().GetField("calling", Flags).GetValue(node).ToString());
            Assert.AreEqual(levels, Buildable(id), id); Assert.AreEqual(levels, (int)node.GetType().GetProperty("maxLevel", Flags).GetValue(node));
            for (int level = 1; level <= levels; level++) {
                var data = node.GetType().GetMethod("Level", Flags).Invoke(node, new object[] { level });
                Assert.IsNotEmpty((string)Field(data, "howTo"), id + " Lv " + level);
            }
        }
        Assert.AreEqual(3, (int)T("UpgradeEffects").GetMethod("GearMaxLevel", Flags).Invoke(null, new object[] { "grabber_pole" }));
        Assert.AreEqual("calling_rocket_blades", T("CallingIds").GetField("RocketBlades").GetRawConstantValue());
        Assert.AreEqual("calling_compactor_backpack", T("CallingIds").GetField("CompactorBackpack").GetRawConstantValue());
        Assert.AreEqual("calling_one_man_route", T("CallingIds").GetField("OneManRoute").GetRawConstantValue());
    }

    // ------------------------------------------------------------------ Grabby Hand

    [Test]
    public void GrabbyHandReachGrowsWithLevelAndLevelOneIsTheOldPole()
    {
        Assert.AreEqual(0f, (float)Rule("PoleReach", 0), "no pole, no reach");
        Assert.AreEqual(4.5f, (float)Rule("PoleReach", 1), 1e-4f, "level 1 is the pole everyone already owns");
        Assert.AreEqual((float)T("GrabberPole").GetField("ReachGrab").GetRawConstantValue(), (float)Rule("PoleReach", 1), 1e-4f);
        float two = (float)Rule("PoleReach", 2), three = (float)Rule("PoleReach", 3);
        Assert.GreaterOrEqual(two, 4.5f + 1.5f, "level 2 is CLEARLY longer"); Assert.GreaterOrEqual(three, two + 1.5f);
        Assert.AreEqual(three, (float)Rule("PoleReach", 9), "levels above the top clamp");
        Assert.AreEqual(4.5f, (float)T("GrabberPole").GetMethod("ReachFor", Flags).Invoke(null, new object[] { 0 }), 1e-4f, "the host floors an owned pole at level 1");
        Assert.AreEqual(three, (float)T("GrabberPole").GetMethod("ReachFor", Flags).Invoke(null, new object[] { 3 }), 1e-4f);
        // The host stretches a pole grab's claim along the pole by ClaimSlack - 0.5: the rest must fit the ordinary grab range.
        float claim = (float)T("HandsTuning").GetField("ClaimSlack").GetRawConstantValue() - 0.5f;
        float grab = (float)T("HandsTuning").GetField("GrabRange").GetRawConstantValue() + (float)T("HandsTuning").GetField("LagSlack").GetRawConstantValue() + 2f;
        Assert.Less(three + 0.6f - claim, grab, "the longest pole must still pass HostGrab's range check");

        Assert.IsFalse((bool)Rule("PoleYanks", 1)); Assert.IsTrue((bool)Rule("PoleYanks", 2)); Assert.IsTrue((bool)Rule("PoleYanks", 3));
        Assert.IsFalse((bool)Rule("PoleGrabsTwo", 2)); Assert.IsTrue((bool)Rule("PoleGrabsTwo", 3));
        Assert.Greater(F("YankStiffnessMult"), 1.5f); Assert.Less(F("TwoBagWindow"), 1.5f);
    }

    // ------------------------------------------------------------------ Compactor Backpack

    [Test]
    public void PackSlotsAreTwoFourSixAndGlovesKeepTheirOldBackSlots()
    {
        CollectionAssert.AreEqual(new[] { 0, 2, 4, 6 }, Enumerable.Range(0, 4).Select(l => (int)Rule("PackSlots", l, 0)).ToArray());
        // Work Gloves 2 / 3 gave a 1 / 2 slot Back Sack before the pack existed: nobody who owns them loses a slot.
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 2 }, Enumerable.Range(0, 4).Select(g => (int)Rule("PackSlots", 0, g)).ToArray());
        Assert.AreEqual(8, (int)Rule("PackSlots", 3, 3), "the two add up");
        Assert.AreEqual(6, (int)Rule("PackSlots", 99, 0)); Assert.AreEqual(0, (int)Rule("PackSlots", -1, -1));
    }

    [Test]
    public void ACrushedBagTakesHalfASlotSoCrushingRoughlyDoublesTheLoad()
    {
        bool Room(int slots, int plain, int crushed) => (bool)Rule("PackHasRoom", slots, plain, crushed);
        Assert.IsFalse(Room(0, 0, 0)); Assert.IsTrue(Room(2, 0, 0)); Assert.IsTrue(Room(2, 1, 0)); Assert.IsFalse(Room(2, 2, 0));
        Assert.IsTrue(Room(2, 0, 2), "two crushed bags leave a whole slot"); Assert.IsFalse(Room(2, 0, 3), "a bag goes in plain: it needs a whole slot");
        // Fill, crush, fill... the honest way a player does it.
        foreach (var (slots, max) in new[] { (2, 3), (4, 7), (6, 11), (8, 15) }) {
            int plain = 0, crushed = 0;
            while (true) {
                if (Room(slots, plain, crushed)) plain++;
                else if (plain > 0) { plain--; crushed++; }
                else break;
            }
            Assert.AreEqual(max, plain + crushed, slots + " slots"); Assert.AreEqual(max, (int)Rule("PackMaxBags", slots, true));
            Assert.AreEqual(slots, (int)Rule("PackMaxBags", slots, false));
            Assert.LessOrEqual((int)Rule("PackHalvesUsed", plain, crushed), slots * 2);
        }
        Assert.IsFalse((bool)Rule("PackCanCrush", 1)); Assert.IsTrue((bool)Rule("PackCanCrush", 2));
        Assert.IsFalse((bool)Rule("PackEjectsBale", 2)); Assert.IsTrue((bool)Rule("PackEjectsBale", 3));
        Assert.IsTrue((bool)T("HaulerRules").GetField("CrushKeepsContents").GetRawConstantValue(), "crushing never destroys what is in the bag");
        Assert.Less((int)Rule("PackMaxBags", (int)Rule("PackSlots", 3, 0), true) + 1, 160 / 6, "a full level-3 pack plus hands stays well under a starter truck (160 kg of ~6 kg bags)");
    }

    [Test]
    public void PackWeightSlowsALittleCrushedBagsWeighHalfAndGlovesScaleIt()
    {
        float Mult(float kg, float gloves) => (float)Rule("PackSpeedMult", kg, gloves);
        Assert.AreEqual(1f, Mult(0f, 1f)); Assert.Less(Mult(20f, 1f), 1f);
        Assert.AreEqual(1f - F("PackMaxSlow"), Mult(500f, 1f), 1e-4f, "capped");
        Assert.GreaterOrEqual(Mult(500f, 1f), 0.7f, "'a little': a held bag slows up to 45%, the back at most " + F("PackMaxSlow"));
        Assert.Greater(Mult(30f, 0.5f), Mult(30f, 1f)); Assert.AreEqual(1f, Mult(500f, 0f), "Gorilla Grip carries it for free");
        Assert.AreEqual(30f, (float)Rule("PackCarryKg", 30f, 0f)); Assert.AreEqual(15f, (float)Rule("PackCarryKg", 0f, 30f), 1e-4f);
        Assert.Greater(Mult((float)Rule("PackCarryKg", 0f, 40f), 1f), Mult((float)Rule("PackCarryKg", 40f, 0f), 1f), "the same bags slow less once crushed");
        Assert.Less((float)Rule("UnloadInterval", 3), (float)Rule("UnloadInterval", 1) * 0.5f, "level 3 unloads as a bale");
    }

    // ------------------------------------------------------------------ One-Man Route

    [Test]
    public void AnOnFootDeliveryPaysExactlyTheHopperPayPlusTheOneTunableBonus()
    {
        float pct = F("FootBonusPct");
        Assert.That(pct, Is.InRange(0.1f, 0.5f), "viable without a truck, never better than one");
        var districts = ((IEnumerable)T("Districts").GetProperty("All", Flags)?.GetValue(null) ?? new object[0]).Cast<object>().ToArray();
        var pay = T("Districts").GetMethod("PickupPay", Flags);
        Assert.NotNull(pay, "Districts.PickupPay is what a hopper pays");
        Assert.IsNotEmpty(districts);
        foreach (var d in districts)
            foreach (double age in new[] { 0.0, 1.0, 3.9, 6.0, 30.0 }) {
                long hopper = (long)Math.Round((float)pay.Invoke(null, new[] { d, age }) * 100.0, MidpointRounding.AwayFromZero);
                long foot = (long)Rule("FootDeliveryCents", hopper), bonus = (long)Rule("FootBonusCents", hopper);
                Assert.AreEqual(hopper + bonus, foot, "base pay is the hopper's, to the cent");
                Assert.AreEqual((long)Math.Round(hopper * (double)pct, MidpointRounding.AwayFromZero), bonus);
                Assert.GreaterOrEqual(bonus, 0); Assert.LessOrEqual(foot, hopper * 3 / 2 + 1);
            }
        Assert.AreEqual(0, (long)Rule("FootBonusCents", 0L)); Assert.AreEqual(0, (long)Rule("FootBonusCents", -500L));
        Assert.AreEqual(100, (long)Rule("FootBonusCents", 400L), "a $4 starter bag walks in for $5 at 25%");
        Assert.AreNotEqual("pickup_fee", T("FootHatch").GetField("FootBonusLedgerId").GetRawConstantValue(), "the bonus must not count as a second pickup");
    }

    [Test]
    public void TheHatchStandsClearOfTheDoorAndBothTruckPoses()
    {
        var hatch = (Vector3)Tune("HatchLocal");
        // DepotRowLayout: the truck stands at door-local (0, 5.5) and rolls out from (0, 3); the bay door is ~7.5 m wide.
        foreach (var truck in new[] { new Vector2(0f, 5.5f), new Vector2(0f, 3f) })
            Assert.Greater(Vector2.Distance(new Vector2(hatch.x, hatch.z), truck), 4.5f, "never on a truck parking pose");
        Assert.Greater(Mathf.Abs(hatch.x), 3.75f + 0.65f, "beside the bay door, not in it");
        Assert.That(hatch.z, Is.InRange(0f, 2.5f), "against the shop front, off the road");
    }

    // ------------------------------------------------------------------ Rocket Blades: boost, heat

    object Boost() => Activator.CreateInstance(T("HaulerRules").GetNestedType("Boost", Flags));
    object Tick(object state, int level, bool pressed, bool held, bool allowed, float dt) => Rule("BoostTick", state, level, pressed, held, allowed, dt);
    static bool B(object state, string field) => (bool)Field(state, field);
    static float N(object state, string field) => (float)Field(state, field);

    [Test]
    public void LevelTwoBoostIsAFixedBurstThenACooldown()
    {
        var s = Tick(Boost(), 1, true, true, true, 0.02f);
        Assert.IsFalse(B(s, "active"), "skates alone have no rockets");
        s = Tick(Boost(), 2, true, true, true, 0.02f);
        Assert.IsTrue(B(s, "active")); Assert.IsTrue(B(s, "started"));
        float lit = 0f; int guard = 0;
        while (B(s, "active") && guard++ < 1000) { s = Tick(s, 2, false, false, true, 0.02f); lit += 0.02f; Assert.IsFalse(B(s, "started")); }
        Assert.AreEqual(F("BoostSeconds"), lit, 0.05f, "letting go does not cut a level-2 burst short"); Assert.IsTrue(B(s, "ended"));
        s = Tick(s, 2, true, true, true, 0.02f); Assert.IsFalse(B(s, "active"), "cooling down");
        for (float t = 0f; t < F("BoostCooldown") - 0.2f; t += 0.02f) s = Tick(s, 2, false, false, true, 0.02f);
        s = Tick(s, 2, true, true, true, 0.02f); Assert.IsFalse(B(s, "active"), "still cooling down just before the cooldown ends");
        for (float t = 0f; t < 0.4f; t += 0.02f) s = Tick(s, 2, false, false, true, 0.02f);
        s = Tick(s, 2, true, true, true, 0.02f); Assert.IsTrue(B(s, "active"), "ready again after the cooldown");
        s = Tick(s, 2, false, true, false, 0.02f); Assert.IsFalse(B(s, "active"), "indoors / driving puts the rockets out"); Assert.IsTrue(B(s, "ended"));
        Assert.GreaterOrEqual(5.335f * F("SkateTopMult") + F("BoostAdd"), 7.2f + 2f, "a boost comfortably outruns a 7.2 m/s yard dog");
        Assert.Less(5.335f * F("SkateTopMult"), 7.2f, "skates alone do not");
    }

    [Test]
    public void LevelThreeBoostHoldsAgainstAHeatMeterAndFlamesOutWhenFull()
    {
        var s = Tick(Boost(), 3, true, true, true, 0.02f);
        Assert.IsTrue(B(s, "active"));
        float lit = 0f; bool overheated = false; int guard = 0;
        while (guard++ < 2000) {
            float before = N(s, "heat");
            s = Tick(s, 3, false, true, true, 0.02f); lit += 0.02f;
            if (B(s, "overheated")) { overheated = true; break; }
            Assert.Greater(N(s, "heat"), before, "heat only rises while lit");
        }
        Assert.IsTrue(overheated); Assert.AreEqual(1f / F("HeatPerSecond"), lit, 0.1f); Assert.IsFalse(B(s, "active"));
        Assert.Greater(1f / F("HeatPerSecond"), F("BoostSeconds") * 1.5f, "sustained means longer than a level-2 burst");
        s = Tick(s, 3, true, true, true, 0.02f); Assert.IsFalse(B(s, "active"), "locked out after a flame-out");
        for (float t = 0f; t < F("OverheatLockSeconds") + 0.1f; t += 0.02f) s = Tick(s, 3, false, false, true, 0.02f);
        Assert.Less(N(s, "heat"), 0.95f, "it cooled while locked"); Assert.AreEqual(0f, N(s, "lockLeft"));
        s = Tick(s, 3, true, true, true, 0.02f); Assert.IsTrue(B(s, "active"));
        s = Tick(s, 3, false, false, true, 0.02f); Assert.IsFalse(B(s, "active"), "letting go stops it at once"); Assert.IsFalse(B(s, "overheated"));
        float hot = N(s, "heat"); for (int i = 0; i < 50; i++) s = Tick(s, 3, false, false, true, 0.02f);
        Assert.Less(N(s, "heat"), hot, "and it cools"); Assert.GreaterOrEqual(N(s, "heat"), 0f);
        s = With(s, "heat", 0.97f); s = Tick(s, 3, true, true, true, 0.02f); Assert.IsFalse(B(s, "active"), "too hot to light");
    }

    // ------------------------------------------------------------------ Rocket Blades: momentum and walls

    static object Mode(string name) => Enum.Parse(T("HaulerRules").GetNestedType("SkateMode", Flags), name);
    static Vector3 Step(Vector3 vel, Vector3 wish, float top, string mode, bool boosting, float dt) => (Vector3)Rule("SkateStep", vel, wish, top, Mode(mode), boosting, dt);

    [Test]
    public void SkatesBuildSpeedGlideBrakeAndCarveWideWhenFast()
    {
        float top = (float)Rule("SkateTop", 5.335f, false);
        Assert.AreEqual(5.335f * F("SkateTopMult"), top, 1e-3f); Assert.AreEqual(top + F("BoostAdd"), (float)Rule("SkateTop", 5.335f, true), 1e-3f);

        Vector3 v = Vector3.zero; float time = 0f;
        while (v.magnitude < top - 0.01f && time < 10f) { v = Step(v, Vector3.forward, top, "Power", false, 0.02f); time += 0.02f; Assert.LessOrEqual(v.magnitude, top + 1e-3f); }
        Assert.That(time, Is.InRange(0.6f, 3f), "real momentum: it takes a moment to get going, but not forever");
        Assert.AreEqual(0f, v.x, 1e-3f);

        Vector3 glide = v; for (int i = 0; i < 50; i++) glide = Step(glide, Vector3.zero, top, "Glide", false, 0.02f);
        Assert.Greater(glide.magnitude, top * 0.7f, "a second of gliding keeps most of the speed"); Assert.Less(glide.magnitude, top);
        Vector3 brake = v; for (int i = 0; i < 50; i++) brake = Step(brake, Vector3.forward, top, "Brake", false, 0.02f);
        Assert.Less(brake.magnitude, 0.5f, "the toe stop stops you in under a second");

        // Carving: the faster you go, the wider the turn.
        float Turned(float speed) { Vector3 t = Step(Vector3.forward * speed, Vector3.right, speed, "Power", false, 0.1f); return Vector3.Angle(Vector3.forward, t); }
        Assert.Greater(Turned(2f), Turned(top)); Assert.Greater(Turned(top), Turned(top + F("BoostAdd")));
        Assert.That(Turned(top), Is.InRange(5f, 45f)); Assert.AreEqual(0f, Step(v, Vector3.right, top, "Power", false, 0.1f).y);

        // A boost that just ended bleeds off instead of snapping back.
        Vector3 fast = Vector3.forward * (top + F("BoostAdd"));
        Vector3 after = Step(fast, Vector3.forward, top, "Power", false, 0.1f);
        Assert.Less(after.magnitude, fast.magnitude); Assert.Greater(after.magnitude, top);
        Assert.AreEqual(Vector3.zero, Step(Vector3.zero, Vector3.zero, top, "Glide", false, 0.02f));
    }

    [Test]
    public void HittingAWallFastAndSquareWipesOutGlancingHitsGrindAtLevelThree()
    {
        float HeadOn(Vector3 intended, Vector3 actual) => (float)Rule("HeadOn", intended, actual);
        string Impact(float speed, float headOn, int level) => Rule("ImpactOf", speed, headOn, level).ToString();
        Assert.AreEqual(0f, HeadOn(Vector3.forward, Vector3.forward), 1e-4f); Assert.AreEqual(1f, HeadOn(Vector3.forward, Vector3.zero), 1e-4f);
        Assert.AreEqual(0f, HeadOn(Vector3.zero, Vector3.zero));
        // Sliding along a wall met at 30 degrees from its face keeps cos(30) of the move.
        Vector3 into = new Vector3(Mathf.Sin(30f * Mathf.Deg2Rad), 0f, Mathf.Cos(30f * Mathf.Deg2Rad));
        Assert.AreEqual(0.5f, HeadOn(into, new Vector3(0f, 0f, into.z)), 0.01f);

        float wipe = F("WipeoutSpeed"), skates = 5.335f * F("SkateTopMult");
        Assert.Greater(wipe, skates, "plain skating can never wipe out");
        Assert.Less(wipe, skates + F("BoostAdd"), "a boost can");
        Assert.AreEqual("None", Impact(20f, 0.01f, 3), "no hit, no impact");
        Assert.AreEqual("Wipeout", Impact(wipe + 1f, 1f, 2)); Assert.AreEqual("Wipeout", Impact(wipe + 1f, 0.9f, 3), "level 3 still wipes out head-on");
        Assert.AreEqual("Bump", Impact(skates, 1f, 2), "below the wipe-out speed a wall is just a wall");
        Assert.AreEqual("Bump", Impact(wipe + 1f, 0.3f, 2), "a glancing hit below level 3 only costs speed");
        Assert.AreEqual("Grind", Impact(wipe + 1f, 0.3f, 3)); Assert.AreEqual("Grind", Impact(F("GrindMinSpeed") + 0.1f, 0.2f, 3));
        Assert.AreEqual("Bump", Impact(F("GrindMinSpeed") - 0.5f, 0.2f, 3), "too slow to grind");
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The Scavenger calling's rules (Assets/Scripts/Callings/Scavenger): being seen, the lock, the loot, house cooldowns, hot
/// goods, the Trust penalty, the fence's prices, Call It and what the X-ray may show. Pure functions and catalog data
/// only: no session, no save, no scene. Game code is reached by reflection, like CallingsTests.
/// </summary>
public class ScavengerTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Rules(string method, params object[] args) => Static("ScavengerRules", method, args);
    static object Static(string type, string method, params object[] args)
    {
        var m = T(type).GetMethods(Flags).Single(x => x.Name == method && x.GetParameters().Length == args.Length);
        return m.Invoke(null, args);
    }
    static object Field(object target, string name)
    {
        var type = target as Type ?? target.GetType(); object instance = target is Type ? null : target;
        var f = type.GetField(name, Flags); if (f != null) return f.GetValue(instance);
        var p = type.GetProperty(name, Flags); Assert.NotNull(p, type.Name + "." + name);
        return p.GetValue(instance);
    }
    static object Tune(string name) => Field(T("ScavengerTuning"), name);
    static object With(object target, string field, object value) { target.GetType().GetField(field, Flags).SetValue(target, value); return target; }
    static float F(object value) => Convert.ToSingle(value);
    static int I(object value) => Convert.ToInt32(value);

    // ------------------------------------------------------------------ being seen

    static object Sense(float hour = 12f, int awake = 0, int asleep = 0, float street = 0f, float players = 0f, float dogs = 0f,
        int soles = 0, int picks = 0, int misses = 0, int suspicion = 0)
    {
        object d = Activator.CreateInstance(T("DetectionInput"));
        With(d, "hour", hour); With(d, "awakeHome", awake); With(d, "asleepHome", asleep); With(d, "streetWitness", street);
        With(d, "playerWitness", players); With(d, "dogs", dogs); With(d, "softSoles", soles); With(d, "lockpicks", picks);
        With(d, "misses", misses); With(d, "suspicion", suspicion);
        return d;
    }
    static float Seen(object input) => F(Rules("DetectionChance", input));

    [Test]
    public void AnEmptyHouseOnAnEmptyStreetIsAsSafeAsItGetsAndOneAwakeResidentIsTwentyPercent()
    {
        Assert.AreEqual(F(Tune("DetectMin")), Seen(Sense()), 1e-5f, "nobody home, nobody about: the floor");
        Assert.AreEqual(0.20f, Seen(Sense(awake: 1)), 1e-4f);
        Assert.AreEqual(1f - 0.8f * 0.8f, Seen(Sense(awake: 2)), 1e-4f, "independent sources combine as 1 - product(1 - p)");
        Assert.AreEqual(Seen(Sense(awake: 3)), Seen(Sense(awake: 9)), 1e-5f, "only three people indoors are counted");
        Assert.LessOrEqual(Seen(Sense(awake: 3, street: 9f, players: 9f, dogs: 9f, misses: 6, suspicion: 10)), F(Tune("DetectMax")));
        Assert.AreEqual(F(Tune("DetectMax")), Seen(Sense(awake: 3, street: 9f, players: 9f, dogs: 9f, misses: 6, suspicion: 10)), 1e-5f);
    }

    [Test]
    public void NightIsSaferAndSoftSolesLockpicksAndCleanPicksAllHelp()
    {
        // the same household: up and about at 2 pm, in bed at 2 am
        Assert.Greater(Seen(Sense(14f, awake: 2)), Seen(Sense(2f, asleep: 2)) * 3f, "sleepers hear far less");
        // the same street: daylight against dark
        float day = Seen(Sense(14f, street: 1f, players: 1f)), night = Seen(Sense(23f, street: 1f, players: 1f));
        Assert.Less(night, day * 0.6f, "outdoor eyes are worth less than half in the dark");
        Assert.IsTrue((bool)Rules("IsDark", 21f)); Assert.IsTrue((bool)Rules("IsDark", 3f)); Assert.IsFalse((bool)Rules("IsDark", 12f));
        Assert.IsTrue((bool)Rules("IsBedtime", 22f)); Assert.IsTrue((bool)Rules("IsBedtime", 5.9f)); Assert.IsFalse((bool)Rules("IsBedtime", 6f));
        Assert.IsFalse((bool)Rules("IsBedtime", 21.9f)); Assert.IsTrue((bool)Rules("IsBedtime", 26f), "hours wrap");

        float soles0 = Seen(Sense(awake: 1, dogs: 1f)), soles1 = Seen(Sense(awake: 1, dogs: 1f, soles: 1)), soles2 = Seen(Sense(awake: 1, dogs: 1f, soles: 2));
        Assert.Greater(soles0, soles1); Assert.Greater(soles1, soles2);
        Assert.AreEqual(0.20f * 0.5f, Seen(Sense(awake: 1, soles: 2)), 1e-4f, "Cat Feet: people indoors hear half as much");

        Assert.Greater(Seen(Sense(awake: 1)), Seen(Sense(awake: 1, picks: 1)), "a quicker pick is less time on the doorstep");
        Assert.Greater(Seen(Sense(awake: 1, picks: 1)), Seen(Sense(awake: 1, picks: 2)));
        Assert.AreEqual(0.20f * 1.3f, Seen(Sense(awake: 1, misses: 1)), 1e-4f, "each fumbled pin is +30%");
        Assert.AreEqual(0.20f * 1.25f, Seen(Sense(awake: 1, suspicion: 5)), 1e-4f, "a jumpy district: +5% a point");
        Assert.AreEqual(Seen(Sense(awake: 1, suspicion: 10)), Seen(Sense(awake: 1, suspicion: 99)), 1e-5f);

        // every extra witness of any kind can only raise the chance
        float last = Seen(Sense());
        foreach (var more in new[] { Sense(asleep: 1), Sense(awake: 1), Sense(awake: 1, street: 0.5f), Sense(awake: 1, street: 1f), Sense(awake: 1, street: 1f, players: 1f), Sense(awake: 1, street: 1f, players: 1f, dogs: 1f) })
        { float p = Seen(more); Assert.GreaterOrEqual(p, last); last = p; }
    }

    [Test]
    public void WitnessesCountByDistanceAndSoftSolesShrinkTheRadius()
    {
        float radius = F(Tune("WitnessRadius"));
        Assert.AreEqual(1f, F(Rules("SightWeight", 0f, radius, 0)), 1e-5f);
        Assert.AreEqual(0.5f, F(Rules("SightWeight", radius * 0.5f, radius, 0)), 1e-4f);
        Assert.AreEqual(0f, F(Rules("SightWeight", radius, radius, 0)));
        Assert.AreEqual(0f, F(Rules("SightWeight", radius * 0.75f, radius, 1)), "Soft Soles: 30% less far");
        Assert.AreEqual(0f, F(Rules("SightWeight", radius * 0.5f, radius, 2)), "Cat Feet: 55% less far");
        Assert.Greater(F(Rules("SightWeight", radius * 0.4f, radius, 2)), 0f);
        // the host's stand-in household when the live schedule is asleep
        var args = new object[] { 23f, 0, 0 }; T("ScavengerRules").GetMethod("FallbackHousehold", Flags).Invoke(null, args);
        Assert.AreEqual(0, args[1]); Assert.AreEqual(2, args[2]);
        args = new object[] { 13f, 0, 0 }; T("ScavengerRules").GetMethod("FallbackHousehold", Flags).Invoke(null, args);
        Assert.AreEqual(1, args[1]); Assert.AreEqual(0, args[2]);
    }

    // ------------------------------------------------------------------ the lock

    [Test]
    public void LockpicksShortenTheJobAndImproveTheOdds()
    {
        CollectionAssert.AreEqual(new[] { 9f, 6.5f, 4.5f }, new[] { 0, 1, 2 }.Select(l => F(Rules("PickSeconds", l))));
        CollectionAssert.AreEqual(new[] { 4, 3, 3 }, new[] { 0, 1, 2 }.Select(l => I(Rules("PickPins", l))));
        Assert.Less(F(Rules("PickSweetSpot", 0)), F(Rules("PickSweetSpot", 1))); Assert.Less(F(Rules("PickSweetSpot", 1)), F(Rules("PickSweetSpot", 2)));
        Assert.AreEqual(4.5f, F(Rules("PickSeconds", 7)), "levels clamp"); Assert.AreEqual(9f, F(Rules("PickSeconds", -1)));

        Assert.AreEqual(0.60f, F(Rules("LockOdds", 0, 0)), 1e-4f, "bare hands, clean pick: 0.55 + 0.05");
        Assert.AreEqual(0.83f, F(Rules("LockOdds", 1, 0)), 1e-4f);
        Assert.AreEqual(0.98f, F(Rules("LockOdds", 2, 0)), 1e-4f, "never a certainty");
        Assert.AreEqual(0.43f, F(Rules("LockOdds", 0, 1)), 1e-4f); Assert.AreEqual(0.66f, F(Rules("LockOdds", 1, 1)), 1e-4f); Assert.AreEqual(0.82f, F(Rules("LockOdds", 2, 1)), 1e-4f);
        Assert.AreEqual(0.10f, F(Rules("LockOdds", 0, 6)), 1e-4f, "never hopeless either");
        for (int level = 0; level <= 2; level++)
            for (int misses = 0; misses < 6; misses++) Assert.Greater(F(Rules("LockOdds", level, misses)) + 1e-6f, F(Rules("LockOdds", level, misses + 1)));

        Assert.AreEqual("Clean", Rules("Outcome", true, false).ToString()); Assert.AreEqual("Spotted", Rules("Outcome", true, true).ToString());
        Assert.AreEqual("Fumbled", Rules("Outcome", false, false).ToString()); Assert.AreEqual("Caught", Rules("Outcome", false, true).ToString());
    }

    // ------------------------------------------------------------------ loot, the sack, cooldowns

    static Func<float> Dice(uint seed) => () => { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / 16777216f; };
    static object District(string id) => Static("Districts", "Get", id);
    static IList Loot(float wealth, string district, int count, uint seed) =>
        (IList)T("ScavengerHost").GetMethod("RollLoot", Flags).Invoke(null, new object[] { wealth, District(district), count, Dice(seed), "14 Test St", 7, "hot7-t" + seed });
    static object DefOf(object item) => Field(item, "Def");
    static bool Plausible(object item) => (bool)T("EbaeItemValidation").GetMethod("IsPlausible", Flags).Invoke(null, new[] { item, null });
    static bool Sellable(object item) => (bool)T("EbaeItemValidation").GetMethod("IsValid", Flags).Invoke(null, new[] { item, null });

    [Test]
    public void TheSackAndTheHouseBoundWhatYouLeaveWith()
    {
        CollectionAssert.AreEqual(new[] { 2, 4, 8 }, new[] { 0, 1, 2 }.Select(l => I(Rules("SackCapacity", l))));
        Assert.AreEqual(2, I(Rules("LootOnOffer", 0f))); Assert.AreEqual(4, I(Rules("LootOnOffer", 0.5f))); Assert.AreEqual(5, I(Rules("LootOnOffer", 1f)));
        Assert.AreEqual(5, I(Rules("LootOnOffer", 3f)), "wealth clamps");
        Assert.AreEqual(2, I(Rules("LootCount", 1f, 0, 0, false)), "pockets hold two");
        Assert.AreEqual(4, I(Rules("LootCount", 1f, 1, 0, false))); Assert.AreEqual(5, I(Rules("LootCount", 1f, 2, 0, false)), "a rich house has five things, however deep the sack");
        Assert.AreEqual(1, I(Rules("LootCount", 1f, 1, 3, false)), "only the free room"); Assert.AreEqual(0, I(Rules("LootCount", 1f, 1, 4, false)));
        Assert.AreEqual(0, I(Rules("LootCount", 1f, 1, 9, false)));
        Assert.AreEqual(2, I(Rules("LootCount", 1f, 2, 0, true)), "seen: you bolt with half, rounded down"); Assert.AreEqual(0, I(Rules("LootCount", 1f, 1, 3, true)));
        for (int sack = 0; sack <= 2; sack++)
            for (int held = 0; held <= 9; held++)
                foreach (float wealth in new[] { 0f, 0.3f, 0.7f, 1f })
                {
                    int n = I(Rules("LootCount", wealth, sack, held, false));
                    Assert.That(n, Is.InRange(0, Math.Max(0, I(Rules("SackCapacity", sack)) - held)));
                    Assert.LessOrEqual(I(Rules("LootCount", wealth, sack, held, true)), n);
                }
    }

    [Test]
    public void StolenGoodsAreRealCatalogValuablesOfTheirDistrictAndAlwaysHot()
    {
        var never = new[] { "pet_raccoon", "raccoon_king_crown", "big_cleanup_cup", "big_cleanup_ribbon", "ticking_alarm_clock", "singing_bass", "haunted_tamagochu" };
        var uids = new HashSet<string>();
        foreach (string id in new[] { "mildly_pleasant", "minivan_meadows", "snootington_hills", "billionaire_bluffs", "coupon_commons", "stackington_heights" })
        {
            int tier = I(Field(District(id), "tier"));
            foreach (float wealth in new[] { 0f, 0.5f, 1f })
                for (uint seed = 1; seed <= 6; seed++)
                {
                    var loot = Loot(wealth, id, 8, seed * 977u + (uint)tier);
                    Assert.AreEqual(8, loot.Count, id);
                    foreach (var item in loot)
                    {
                        var def = DefOf(item); Assert.NotNull(def, "every stolen thing is in the catalog");
                        string defId = (string)Field(def, "id");
                        Assert.AreEqual("Valuable", Field(def, "category").ToString()); CollectionAssert.DoesNotContain(never, defId);
                        Assert.LessOrEqual(I(Static("ItemCatalog", "TierOf", def)), tier, defId + " does not belong in " + id);
                        Assert.IsTrue((bool)Field(item, "hot")); Assert.AreEqual("14 Test St", Field(item, "foundAt")); Assert.AreEqual(7, Field(item, "dayFound"));
                        Assert.That(F(Field(item, "value")), Is.InRange(1f, F(Field(def, "maxValue"))), defId);
                        Assert.That(F(Field(item, "massKg")), Is.InRange(F(Field(def, "minMass")) - 1e-4f, F(Field(def, "maxMass")) + 1e-4f), defId);
                        Assert.That(F(Field(item, "condition")), Is.InRange(0.6f, 1f));
                        string uid = (string)Field(item, "uid");
                        Assert.IsTrue(uids.Add(id + wealth + seed + uid), "uids are unique inside one haul"); Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(uid), 60);
                        Assert.IsTrue(Plausible(item), defId + " must pass ebag's catalog bounds (so the fence's host check accepts it)");
                        Assert.IsFalse(Sellable(item), "and must still be refused as hot");
                    }
                }
        }
        Assert.AreEqual(0, Loot(1f, "billionaire_bluffs", 0, 3).Count); Assert.AreEqual(0, Loot(1f, "billionaire_bluffs", -2, 3).Count);
        Assert.AreEqual(0, ((IList)T("ScavengerHost").GetMethod("RollLoot", Flags).Invoke(null, new object[] { 1f, District("mildly_pleasant"), 3, null, "x", 1, "p" })).Count, "no dice, no loot");
    }

    [Test]
    public void RicherHousesInRicherDistrictsYieldBetterLoot()
    {
        float Average(string district, float wealth)
        {
            double total = 0; int n = 0;
            for (uint seed = 1; seed <= 60; seed++)
                foreach (var item in Loot(wealth, district, 5, seed * 7919u)) { total += F(Field(item, "value")); n++; }
            return (float)(total / n);
        }
        float poorAcres = Average("mildly_pleasant", 0f), richAcres = Average("mildly_pleasant", 1f), richBluffs = Average("billionaire_bluffs", 1f);
        Assert.Greater(richAcres, poorAcres, "wealth matters inside a district");
        Assert.Greater(richBluffs, richAcres * 1.5f, "and the Bluffs beat the Acres");
        // the rarity table is read a little richer than the same house's bin, and never past the top
        Assert.AreEqual(0.25f, F(Rules("LootWealth", 0f)), 1e-5f); Assert.AreEqual(1f, F(Rules("LootWealth", 0.9f)), 1e-5f);
        Assert.AreEqual(0, I(Rules("PickWeighted", new[] { 80f, 15f, 4f, 1f }, 0f))); Assert.AreEqual(1, I(Rules("PickWeighted", new[] { 80f, 15f, 4f, 1f }, 0.81f)));
        Assert.AreEqual(3, I(Rules("PickWeighted", new[] { 80f, 15f, 4f, 1f }, 0.9999f))); Assert.AreEqual(2, I(Rules("PickWeighted", new[] { 0f, 0f, 4f, 0f }, 0.5f)));
        Assert.AreEqual(0, I(Rules("PickWeighted", new[] { 0f, 0f, 0f, 0f }, 0.5f)));
    }

    [Test]
    public void AHouseStaysCleanedOutForThreeDaysAndOnAlertForOne()
    {
        Assert.AreEqual(13, I(Rules("ReadyDayAfter", 10, true, false))); Assert.AreEqual(13, I(Rules("ReadyDayAfter", 10, true, true)));
        Assert.AreEqual(11, I(Rules("ReadyDayAfter", 10, false, true)), "caught at the door: on alert until tomorrow");
        Assert.AreEqual(10, I(Rules("ReadyDayAfter", 10, false, false)), "a quiet fumble costs no days");
        Assert.IsFalse((bool)Rules("HouseReady", 13, 10)); Assert.IsFalse((bool)Rules("HouseReady", 13, 12)); Assert.IsTrue((bool)Rules("HouseReady", 13, 13));
        Assert.IsTrue((bool)Rules("HouseReady", 0, 1), "a house nobody has touched");
        Assert.AreEqual("CleanedOut", Rules("CooldownStatus", 13, 10).ToString()); Assert.AreEqual("CleanedOut", Rules("CooldownStatus", 13, 11).ToString());
        Assert.AreEqual("OnAlert", Rules("CooldownStatus", 13, 12).ToString(), "one day to go reads as on alert");
        Assert.AreEqual("OnAlert", Rules("CooldownStatus", 11, 10).ToString()); Assert.AreEqual("Ok", Rules("CooldownStatus", 11, 11).ToString());
        Assert.AreEqual("Ok", Rules("CooldownStatus", 0, 4).ToString());
        // the counter is keyed by house, and the day is the Town's persistent day, never a TotalHours stamp
        Assert.AreEqual("scav_ready:mm-juicebox-210", Static("ScavengerIds", "HouseReadyDay", "mm-juicebox-210"));
    }

    // ------------------------------------------------------------------ hot goods

    static object Item(string defId, float value, bool hot, string uid = "test-hot-1", float condition = 1f)
    {
        var def = Static("ItemCatalog", "Get", defId); Assert.NotNull(def, defId);
        object item = Activator.CreateInstance(T("ItemInstance"));
        With(item, "uid", uid); With(item, "defId", defId); With(item, "massKg", F(Field(def, "minMass"))); With(item, "value", value);
        With(item, "condition", condition); With(item, "foundAt", "9 Test Ave"); With(item, "dayFound", 3); With(item, "note", ""); With(item, "hot", hot);
        return item;
    }

    [Test]
    public void TheHotFlagSurvivesJsonAndOldSavesLoadAsClean()
    {
        var hot = Item("gold_chain", 700f, true);
        string json = JsonUtility.ToJson(hot); StringAssert.Contains("\"hot\":true", json);
        var back = JsonUtility.FromJson(json, T("ItemInstance")); Assert.IsTrue((bool)Field(back, "hot")); Assert.AreEqual(json, JsonUtility.ToJson(back));
        var old = JsonUtility.FromJson("{\"uid\":\"a\",\"defId\":\"gold_chain\",\"massKg\":0.1,\"value\":500,\"condition\":1}", T("ItemInstance"));
        Assert.IsFalse((bool)Field(old, "hot"), "an item saved before hot goods existed is clean");
        Assert.IsFalse((bool)Rules("IsHot", old)); Assert.IsTrue((bool)Rules("IsHot", hot)); Assert.IsFalse((bool)Rules("IsHot", new object[] { null }));

        // the haul as it crosses the wire
        object loot = Activator.CreateInstance(T("ScavLoot")); ((IList)Field(loot, "items")).Add(hot); ((IList)Field(loot, "items")).Add(Item("lava_lamp", 30f, true, "test-hot-2"));
        var wire = JsonUtility.FromJson(JsonUtility.ToJson(loot), T("ScavLoot"));
        Assert.AreEqual(2, ((IList)Field(wire, "items")).Count);
        foreach (var item in (IList)Field(wire, "items")) Assert.IsTrue((bool)Field(item, "hot"));

        var mixed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ItemInstance")));
        mixed.Add(hot); mixed.Add(Item("lava_lamp", 30f, false, "clean-1")); mixed.Add(null); mixed.Add(Item("mixtape", 12f, true, "test-hot-3"));
        Assert.AreEqual(2, I(Rules("HotCount", mixed))); Assert.AreEqual(0, I(Rules("HotCount", new object[] { null })));
    }

    [Test]
    public void HotGoodsAreRefusedByEbaePawnPeteTradesAndDisplays()
    {
        var hot = Item("gold_chain", 700f, true, "scav-test-hot"); var clean = Item("gold_chain", 700f, false, "scav-test-clean");
        Assert.IsTrue(Sellable(clean), "the control: the very same item, not stolen, is fine");
        var args = new[] { hot, null }; Assert.IsFalse((bool)T("EbaeItemValidation").GetMethod("IsValid", Flags).Invoke(null, args));
        StringAssert.Contains("fence", ((string)args[1]).ToLowerInvariant(), "the refusal says where to go instead");
        Assert.IsTrue(Plausible(hot), "it is still a real item: the fence's check passes");
        Assert.IsFalse((bool)Static("EbaeItemValidation", "FieldsMatch", hot, clean), "a hot item can never stand in for a clean one in a held trade");
        Assert.IsTrue((bool)Static("EbaeItemValidation", "FieldsMatch", hot, Item("gold_chain", 700f, true, "scav-test-hot")));

        var inventory = (IList)Field(T("GameSession"), "Inventory");
        inventory.Add(hot); inventory.Add(clean);
        try
        {
            Assert.IsFalse((bool)T("EbaeMarket").GetMethod("List", Flags).Invoke(null, new object[] { "scav-test-hot", 50f }), "cannot be listed");
            Assert.IsFalse((bool)T("EbaeMarket").GetMethod("PawnNow", Flags, null, new[] { typeof(string) }, null).Invoke(null, new object[] { "scav-test-hot" }), "cannot be pawned");
            Assert.IsTrue(inventory.Contains(hot), "a refusal leaves it in the keep crate");
            Assert.IsNull(Static("Workshop", "OwnedItem", "scav-test-hot"), "cannot be pinned to a workshop display");
            Assert.AreSame(clean, Static("Workshop", "OwnedItem", "scav-test-clean"));
            var ranked = (IList)Static("DisplayPlanner", "Ranked", inventory);
            CollectionAssert.DoesNotContain(ranked, hot, "never on a shelf, so never trophy Rep"); CollectionAssert.Contains(ranked, clean);
        }
        finally { inventory.Remove(hot); inventory.Remove(clean); }
    }

    // ------------------------------------------------------------------ consequences

    [Test]
    public void FinesRepAndTrustAreScaledAgainstTheExistingNeighbourFines()
    {
        var shunts = (float[])Field(T("WorldLifeTuning").GetNestedType("Fines", Flags), "CarShunted");
        CollectionAssert.AreEqual(new[] { 20f, 80f, 240f, 800f, 120f, 360f }, shunts.Select(s => F(Rules("Fine", s, 1f))), "four car shunts, district by district");
        Assert.AreEqual(100f, F(Rules("Fine", 20f, 1.25f)), 1e-4f, "the second burglar caught that day pays x1.25");
        Assert.AreEqual(60f, F(Rules("Fine", 5f, 3f)), 1e-4f); Assert.AreEqual(20f, F(Rules("Fine", 5f, 0.2f)), 1e-4f, "never below the base");
        Assert.AreEqual(0f, F(Rules("Fine", -5f, 1f)));
        Assert.AreEqual(-8f, F(Tune("BurglaryRep")), "two flattened neighbours' worth of Rep");

        Assert.AreEqual(0f, F(Rules("TrustPenalty", 0, 0))); Assert.AreEqual(0.04f, F(Rules("TrustPenalty", 1, 0)), 1e-5f);
        Assert.AreEqual(0.03f, F(Rules("TrustPenalty", 0, 3)), 1e-5f, "unseen thefts still cost: things went missing");
        Assert.AreEqual(0.10f, F(Rules("TrustPenalty", 2, 2)), 1e-5f); Assert.AreEqual(0.20f, F(Rules("TrustPenalty", 10, 10)), 1e-5f, "one night's cap");
        Assert.AreEqual(0f, F(Rules("TrustPenalty", -3, -3)));
        Assert.Greater(F(Tune("TrustNightCap")), 0.10f, "a crime spree is worse for Trust than a dirty district (0.10 a night)");
        for (int w = 0; w < 8; w++) Assert.GreaterOrEqual(F(Rules("TrustPenalty", w + 1, 2)), F(Rules("TrustPenalty", w, 2)));

        Assert.AreEqual(1, I(Rules("SuspicionAfterTheft", 0, false))); Assert.AreEqual(2, I(Rules("SuspicionAfterTheft", 0, true)));
        Assert.AreEqual(10, I(Rules("SuspicionAfterTheft", 10, true))); Assert.AreEqual(2, I(Rules("SuspicionAfterNights", 3, 1)));
        Assert.AreEqual(0, I(Rules("SuspicionAfterNights", 1, 5))); Assert.AreEqual(3, I(Rules("SuspicionAfterNights", 3, -2)));
    }

    [Test]
    public void EveryDistrictWritesItsOwnLetter()
    {
        var subjects = new HashSet<string>();
        for (byte d = 0; d < 6; d++)
            foreach (uint variant in new uint[] { 0, 1 })
                foreach (bool entered in new[] { true, false })
                {
                    var args = new object[] { d, "Gary", "14 Maple St", "2:15 AM", entered, 80f, false, 2, 1.25f, variant, null, null };
                    T("BurglaryLetters").GetMethod("Caught", Flags).Invoke(null, args);
                    string subject = (string)args[10], body = (string)args[11];
                    Assert.IsNotEmpty(subject); subjects.Add(subject);
                    StringAssert.Contains("14 Maple St", body); StringAssert.Contains("2:15 AM", body); StringAssert.Contains("$80", body); StringAssert.Contains("#2", body);
                    Assert.LessOrEqual(subject.Length, 120, "the letter rides in a network reply"); Assert.LessOrEqual(body.Length, 900);
                }
        Assert.AreEqual(6, subjects.Count, "six districts, six voices");
        var waived = new object[] { (byte)0, "", "", "noon", true, 0f, true, 1, 1f, 0u, null, null };
        T("BurglaryLetters").GetMethod("Caught", Flags).Invoke(null, waived);
        StringAssert.Contains("waived", (string)waived[11]); StringAssert.DoesNotContain("Fine: $", (string)waived[11]);
        var watch = new object[] { (byte)1, "Minivan Meadows", 3, null, null };
        T("BurglaryLetters").GetMethod("WatchBulletin", Flags).Invoke(null, watch);
        StringAssert.Contains("Minivan Meadows", (string)watch[4]); StringAssert.Contains("3/10", (string)watch[4]);
    }

    [Test]
    public void OnlyNpcHomesAreEverATarget()
    {
        foreach (string notAHouse in new[] { null, "", "made_up", "depot_lot_0", "truck" })
        {
            var args = new object[] { notAHouse, null, null };
            Assert.IsFalse((bool)T("ScavHouses").GetMethod("IsNpcHome", Flags).Invoke(null, args), notAHouse ?? "null");
        }
        // the fence stands in the alley between the two middle shops, not on anybody's lot
        var spot = (Vector3)Field(T("FenceSpot"), "Position");
        Assert.AreEqual(-1, I(Static("DepotRowLayout", "PadAt", spot)), "the fence is not on a business's pad");
        // and a theft fact carries who / where / how much for whoever polices this later
        foreach (string field in new[] { "clientId", "playerKey", "businessId", "stopId", "districtId", "pos", "witnessed", "entered", "lootCents", "fineCents", "day", "hours" })
            Assert.NotNull(T("TheftFact").GetField(field, Flags), "TheftFact." + field);
        Assert.NotNull(T("ScavengerEvents").GetEvent("Theft", Flags)); Assert.NotNull(T("FenceTerms").GetField("StandingFor", Flags));
    }

    // ------------------------------------------------------------------ the fence

    [Test]
    public void TheFencePaysAFractionAndOnlyAtNight()
    {
        Assert.AreEqual(3500L, Rules("FencePriceCents", 100f, 1f)); Assert.AreEqual(35L, Rules("FencePriceCents", 1f, 1f));
        Assert.AreEqual(6000L, Rules("FencePriceCents", 100f, 4f), "however much the King likes you: 60% at most");
        Assert.AreEqual(1500L, Rules("FencePriceCents", 100f, 0f), "and never under 15%"); Assert.AreEqual(4200L, Rules("FencePriceCents", 100f, 1.2f));
        Assert.AreEqual(3500L, Rules("FencePriceCents", 100f, float.NaN)); Assert.AreEqual(0L, Rules("FencePriceCents", 0f, 1f));
        Assert.AreEqual(0L, Rules("FencePriceCents", float.NaN, 1f)); Assert.AreEqual(0L, Rules("FencePriceCents", -5f, 1f));
        Assert.AreEqual(1L, Rules("FencePriceCents", 0.01f, 1f), "anything real is worth a cent");
        float pawn = (float)T("Economy").GetField("PawnFraction").GetRawConstantValue();
        Assert.Less(F(Tune("FenceFraction")), pawn, "stolen goods fetch less than Pawn Pete pays for clean ones");
        for (float worth = 1f; worth < 6000f; worth *= 1.7f)
            Assert.LessOrEqual((long)Rules("FencePriceCents", worth, 4f), (long)Math.Round(worth * 100.0), "he never pays over the appraisal");
        foreach (float open in new[] { 20.5f, 23f, 0f, 2f, 4.9f }) Assert.IsTrue((bool)Rules("FenceOpen", open), open + "h");
        foreach (float shut in new[] { 5f, 7f, 12f, 18f, 20.4f }) Assert.IsFalse((bool)Rules("FenceOpen", shut), shut + "h");
        // the Raccoon stage's seam: no provider means strangers' rate
        Assert.AreEqual(1f, F(Static("FenceTerms", "Standing", 0UL))); Assert.AreEqual("hello", Static("FenceTerms", "Greeting", "hello"));
    }

    // ------------------------------------------------------------------ call it

    static object Bag(params object[] items)
    {
        object bag = Activator.CreateInstance(T("BagContents")); With(bag, "bagId", "scav/test/bag");
        foreach (var item in items) ((IList)Field(bag, "items")).Add(item);
        return bag;
    }
    static Delegate Appraiser() => Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(T("ItemInstance"), typeof(float)), T("EbaeMarket").GetMethod("Appraise", Flags));

    [Test]
    public void CallItIsJudgedByTheDearestValuableInTheBag()
    {
        Assert.AreEqual(0, I(Rules("CallBand", false, 9999f)), "no valuable at all is its own call");
        var cases = new (float value, int band)[] { (1f, 1), (24.99f, 1), (25f, 2), (99.99f, 2), (100f, 3), (499f, 3), (500f, 4), (5000f, 4) };
        foreach (var (value, band) in cases) Assert.AreEqual(band, I(Rules("CallBand", true, value)), "$" + value);
        int bands = I(Field(T("ScavengerRules"), "CallBands"));
        Assert.AreEqual(5, bands); Assert.AreEqual(bands, ((Array)Tune("CallBonus")).Length); Assert.AreEqual(bands, ((Array)Tune("CallBandEdges")).Length + 2);

        var junk = Item("banana_peel", 0f, false, "j1"); var lamp = Item("lava_lamp", 30f, false, "v1"); var chain = Item("gold_chain", 700f, false, "v2");
        Assert.AreEqual(0, I(Rules("CallBandOf", Bag(junk), Appraiser()))); Assert.AreEqual(0, I(Rules("CallBandOf", Bag(), Appraiser())));
        Assert.AreEqual(0, I(Rules("CallBandOf", null, Appraiser())));
        Assert.AreEqual(2, I(Rules("CallBandOf", Bag(junk, lamp), Appraiser())));
        Assert.AreEqual(4, I(Rules("CallBandOf", Bag(lamp, junk, chain), Appraiser())), "the dearest thing decides, wherever it sits in the bag");
        Assert.AreEqual(4, I(Rules("CallBandOf", Bag(chain, lamp), Appraiser())));

        var bonus = (float[])Tune("CallBonus");
        for (int called = 0; called < bands; called++)
            for (int actual = 0; actual < bands; actual++)
                Assert.AreEqual(called == actual ? bonus[called] : 0f, F(Rules("CallPayout", called, actual)), $"called {called}, was {actual}");
        Assert.AreEqual(0f, F(Rules("CallPayout", -1, -1))); Assert.AreEqual(0f, F(Rules("CallPayout", 9, 9)));
        for (int i = 1; i < bonus.Length; i++) Assert.Greater(bonus[i], bonus[i - 1], "a braver call pays more");
        Assert.IsFalse((bool)Rules("CallAvailable", 12, 12), "one call a day"); Assert.IsTrue((bool)Rules("CallAvailable", 11, 12)); Assert.IsTrue((bool)Rules("CallAvailable", 0, 1));
    }

    // ------------------------------------------------------------------ x-ray specs

    static object Peek(object bag, int level) => Rules("Summarize", bag, level);

    [Test]
    public void TheXRayShowsShapesThenTheBestNameAndNeverAPrice()
    {
        var bag = Bag(Item("banana_peel", 0f, false, "a"), Item("banana_peel", 0f, false, "b"), Item("pizza_box", 0f, false, "c"),
            Item("lava_lamp", 30f, false, "d"), Item("gold_chain", 450f, false, "e", 0.6f));
        var one = Peek(bag, 1); var two = Peek(bag, 2);
        Assert.AreEqual((byte)1, Field(one, "level")); Assert.IsTrue((bool)Field(one, "hasValuable")); Assert.AreEqual("", Field(one, "bestDefId"), "level 1 never names anything");
        Assert.AreEqual(3, I(one.GetType().GetMethod("Count").Invoke(one, new object[] { 0 })), "three pieces of junk");
        Assert.AreEqual(3, I(Field(one, "Total")), "valuables are a yes/no at level 1, not a count");
        Assert.AreEqual("gold_chain", Field(two, "bestDefId"), "level 2 names the best thing (Epic beats Uncommon)"); Assert.AreEqual(Field(one, "counts"), Field(two, "counts"));

        // the same things at any price and in any condition look exactly the same: the summary cannot leak an appraisal
        var cheap = Bag(Item("banana_peel", 0f, false, "a"), Item("banana_peel", 0f, false, "b"), Item("pizza_box", 0f, false, "c"),
            Item("lava_lamp", 55f, false, "d", 0.3f), Item("gold_chain", 1300f, false, "e", 1f));
        foreach (int level in new[] { 1, 2 })
            foreach (var field in T("XRaySummary").GetFields(Flags)) Assert.AreEqual(field.GetValue(Peek(bag, level)), field.GetValue(Peek(cheap, level)), field.Name + " at level " + level);
        // ... even when the cheaper def holds the pricier roll: the pick goes by catalog rarity and catalog top price, never the rolled value
        var upset = Bag(Item("cursed_doll", 890f, false, "x"), Item("gameboi", 70f, false, "y"));   // both Rare; the doll's catalog top price is higher
        var calm = Bag(Item("cursed_doll", 15f, false, "x"), Item("gameboi", 180f, false, "y"));
        Assert.AreEqual(Field(Peek(upset, 2), "bestDefId"), Field(Peek(calm, 2), "bestDefId"));
        foreach (var field in T("XRaySummary").GetFields(Flags))
        {
            Assert.IsFalse(field.FieldType == typeof(float) || field.FieldType == typeof(double) || field.FieldType == typeof(long) || field.FieldType == typeof(int), "XRaySummary." + field.Name + " could carry a value");
            StringAssert.DoesNotContain("value", field.Name.ToLowerInvariant().Replace("valuable", "")); StringAssert.DoesNotContain("price", field.Name.ToLowerInvariant());
        }

        Assert.AreEqual((byte)0, Field(Peek(bag, 0), "level")); Assert.AreEqual((byte)0, Field(Peek(null, 2), "level"));
        Assert.AreEqual((byte)2, Field(Peek(bag, 9), "level"), "levels clamp");
        var trash = Peek(Bag(Item("banana_peel", 0f, false, "a")), 2);
        Assert.IsFalse((bool)Field(trash, "hasValuable")); Assert.AreEqual("", Field(trash, "bestDefId"));
        var stuffed = Bag(); for (int i = 0; i < 40; i++) ((IList)Field(stuffed, "items")).Add(Item("banana_peel", 0f, false, "p" + i));
        Assert.AreEqual(15, I(Peek(stuffed, 1).GetType().GetMethod("Count").Invoke(Peek(stuffed, 1), new object[] { 0 })), "counts cap at 15 (four bits each)");
    }

    // ------------------------------------------------------------------ nose for money + the tree's promises

    [Test]
    public void TheNoseRanksHousesByWealthAndDistrict()
    {
        Assert.AreEqual(0, I(Rules("RichTier", 0.5f, 1))); Assert.AreEqual(1, I(Rules("RichTier", 0.65f, 1))); Assert.AreEqual(2, I(Rules("RichTier", 0.8f, 1)));
        Assert.AreEqual(3, I(Rules("RichTier", 0.97f, 1))); Assert.AreEqual(1, I(Rules("RichTier", 0.56f, 2)), "the same money smells richer in a posher district");
        Assert.AreEqual(0, I(Rules("RichTier", 0.5f, 2))); Assert.AreEqual(3, I(Rules("RichTier", 0.8f, 4)));
        Assert.AreEqual(28f, F(Rules("NoseRange", 1))); Assert.AreEqual(55f, F(Rules("NoseRange", 2))); Assert.AreEqual(0f, F(Rules("NoseRange", 0)));
    }

    [Test]
    public void EveryScavengerLevelIsBuildableAndItsTextMatchesTheTuning()
    {
        var shipped = new Dictionary<string, int> { { "calling_nose_for_money", 2 }, { "calling_xray_specs", 2 }, { "calling_the_itch", 1 }, { "calling_lockpicks", 2 },
            { "calling_soft_soles", 2 }, { "calling_swag_sack", 2 }, { "calling_the_fence", 1 }, { "calling_call_it", 1 } };
        string Effect(string id, int level) =>
            (string)Field(T("CallingNode").GetMethod("Level", Flags).Invoke(Static("CallingCatalog", "Get", id), new object[] { level }), "effect");
        foreach (var kv in shipped)
        {
            var node = Static("CallingCatalog", "Get", kv.Key); Assert.NotNull(node, kv.Key);
            Assert.AreEqual(kv.Value, I(Field(node, "implementedLevels")), kv.Key); Assert.AreEqual(kv.Value, I(Field(node, "maxLevel")), kv.Key + ": the whole node shipped");
            Assert.AreEqual("Scavenger", Field(node, "calling").ToString());
        }
        string Num(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        StringAssert.Contains(Num(F(Rules("NoseRange", 1))) + " m", Effect("calling_nose_for_money", 1)); StringAssert.Contains(Num(F(Rules("NoseRange", 2))) + " m", Effect("calling_nose_for_money", 2));
        StringAssert.Contains(Num(F(Rules("PickSeconds", 1))) + " s", Effect("calling_lockpicks", 1)); StringAssert.Contains(Num(F(Rules("PickSeconds", 0))), Effect("calling_lockpicks", 1));
        StringAssert.Contains("78%", Effect("calling_lockpicks", 1)); StringAssert.Contains("55%", Effect("calling_lockpicks", 1));
        Assert.AreEqual(0.78f, ((float[])Tune("LockOdds"))[1]); Assert.AreEqual(0.55f, ((float[])Tune("LockOdds"))[0]); Assert.AreEqual(0.94f, ((float[])Tune("LockOdds"))[2]);
        StringAssert.Contains(Num(F(Rules("PickSeconds", 2))) + " s", Effect("calling_lockpicks", 2)); StringAssert.Contains("94%", Effect("calling_lockpicks", 2));
        StringAssert.Contains("30%", Effect("calling_soft_soles", 1)); StringAssert.Contains("25%", Effect("calling_soft_soles", 1)); StringAssert.Contains("55%", Effect("calling_soft_soles", 2));
        CollectionAssert.AreEqual(new[] { 1f, 0.70f, 0.45f }, (float[])Tune("SoftSolesRadius")); CollectionAssert.AreEqual(new[] { 1f, 0.75f, 0.50f }, (float[])Tune("SoftSolesNoise"));
        StringAssert.Contains("Hold " + I(Rules("SackCapacity", 1)), Effect("calling_swag_sack", 1)); StringAssert.Contains("instead of " + I(Rules("SackCapacity", 0)), Effect("calling_swag_sack", 1));
        StringAssert.Contains("Hold " + I(Rules("SackCapacity", 2)), Effect("calling_swag_sack", 2));
        StringAssert.Contains(Mathf.RoundToInt(F(Tune("FenceFraction")) * 100f) + "%", Effect("calling_the_fence", 1));
        StringAssert.Contains("$" + Num(((float[])Tune("CallBonus")).Max()), Effect("calling_call_it", 1));
        // the capstone asks for both scouting lines at their top level; the burglary branch hangs off The Itch
        CollectionAssert.AreEquivalent(new[] { "calling_xray_specs:2", "calling_nose_for_money:2" }, ((IEnumerable)Field(Static("CallingCatalog", "Get", "calling_call_it"), "prereqs")).Cast<string>());
        foreach (string branch in new[] { "calling_lockpicks", "calling_soft_soles", "calling_swag_sack" })
            CollectionAssert.AreEqual(new[] { "calling_the_itch:1" }, ((IEnumerable)Field(Static("CallingCatalog", "Get", branch), "prereqs")).Cast<string>(), branch);
    }
}

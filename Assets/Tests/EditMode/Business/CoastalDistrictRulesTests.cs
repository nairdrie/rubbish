using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CoastalDistrictRulesTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => R(type).GetMethod(method, Static).Invoke(null, args);
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name).GetValue(o);
    static object District(string id) => Call("Districts", "Get", id);
    static object[] DistrictList(string property) => ((IEnumerable)R("Districts").GetProperty(property).GetValue(null)).Cast<object>().ToArray();
    static string Id(object d) => Field<string>(d, "id");

    [Test]
    public void ExistingSaveAndWorldLifeIndicesAreAppendOnlyButUnlockOrderIsInterleaved()
    {
        string[] stable = { "mildly_pleasant", "minivan_meadows", "snootington_hills", "billionaire_bluffs", "coupon_commons", "stackington_heights" };
        CollectionAssert.AreEqual(stable, DistrictList("All").Select(Id));
        CollectionAssert.AreEqual(new[] { stable[0], stable[1], stable[4], stable[2], stable[5], stable[3] }, DistrictList("ProgressionOrder").Select(Id));
        var index = R("WorldLifeIds").GetMethod("DistrictIndex", new[] { typeof(string) });
        for (int i = 0; i < stable.Length; i++)
        {
            Assert.AreEqual((byte)i, index.Invoke(null, new object[] { stable[i] }));
            Assert.AreEqual(stable[i], Call("WorldLifeIds", "DistrictId", (byte)i));
            Assert.AreEqual(i + 1, Call("WorkOrders", "DistrictIndex", stable[i]));
            Assert.AreEqual(stable[i], Call("WorkOrders", "DistrictAt", i + 1));
        }
    }

    [TestCase("mm-1", "minivan_meadows")]
    [TestCase("sh-1", "snootington_hills")]
    [TestCase("bb-1", "billionaire_bluffs")]
    [TestCase("cc-1", "coupon_commons")]
    [TestCase("st-1", "stackington_heights")]
    public void StableStopAndBagIdsResolveTheirDistrict(string stop, string expected)
    {
        Assert.AreEqual(expected, Id(Call("Districts", "ForStopId", stop)));
        Assert.AreEqual(expected, Id(Call("Districts", "ForBagId", stop + "/day4/slot1")));
    }

    [Test]
    public void EveryDistrictUsesSharedLayoutBoundsAndOwnCenter()
    {
        foreach (var d in DistrictList("All"))
        {
            var bounds = Field<Rect>(d, "boundsXZ");
            Assert.AreEqual(Call("CoastalCityLayout", "DistrictBounds", Id(d)), bounds);
            Assert.Greater(bounds.width, 0f); Assert.Greater(bounds.height, 0f);
            Assert.AreEqual(Id(d), Id(Call("Districts", "AtPosition", new Vector3(bounds.center.x, 0f, bounds.center.y))));
        }
    }

    [Test]
    public void NewPermitsFormOneClearProgressionAndMatchCatalogPrices()
    {
        var progression = DistrictList("ProgressionOrder");
        for (int i = 1; i < progression.Length; i++)
        {
            var d = progression[i];
            var product = Call("ScrapazonCatalog", "Get", Field<string>(d, "permitId"));
            Assert.NotNull(product);
            Assert.AreEqual(Field<float>(d, "permitPrice"), Field<float[]>(product, "prices")[0]);
            Assert.AreEqual(Field<int>(d, "rankRequired"), Field<int[]>(product, "rankRequired")[0]);
            if (i > 1)
                CollectionAssert.Contains(Field<string[][]>(product, "prereqs")[0], Field<string>(progression[i - 1], "permitId") + ":1");
        }
    }

    [TestCase("coupon_commons", "minivan_meadows", 1)]
    [TestCase("stackington_heights", "snootington_hills", 2)]
    public void NewDistrictsChangeSortingMixWithoutInflatingRareOdds(string addedId, string peerId, int style)
    {
        var added = District(addedId); var peer = District(peerId);
        object a = Field<object>(added, "loot"), p = Field<object>(peer, "loot");
        foreach (string field in new[] { "p0", "p1", "springFlat", "common0", "common1", "keepsakeChance" }) Assert.AreEqual(Field<float>(p, field), Field<float>(a, field));
        foreach (string field in new[] { "rarity0", "rarity1", "tierBoost" }) CollectionAssert.AreEqual(Field<float[]>(p, field), Field<float[]>(a, field));
        foreach (string field in new[] { "tier", "minItems", "maxItems" }) Assert.AreEqual(Field<int>(p, field), Field<int>(a, field));
        CollectionAssert.AreNotEqual(Field<float[]>(p, "categoryWeights"), Field<float[]>(a, "categoryWeights"));
        Assert.AreEqual(style, Call("Districts", "StyleIndex", (int)Call("Districts", "IndexOf", addedId)));
        Assert.Greater(Field<float>(added, "fee"), Field<float>(peer, "fee"));
        Assert.Less(Field<float>(added, "fee"), Field<float>(District(style == 1 ? "snootington_hills" : "billionaire_bluffs"), "fee"));
    }

    [TestCase("permit_snootington_hills", 4)]
    [TestCase("permit_billionaire_bluffs", 4)]
    public void OlderUpperDistrictOwnersGainOnlyConnectingPermits(string owned, int version)
    {
        var levels = new Dictionary<string, int> { [owned] = 1, ["truck_capacity"] = 3 };
        Call("Districts", "MigrateLegacyPermits", levels, version);
        Assert.AreEqual(1, levels["permit_minivan_meadows"]);
        Assert.AreEqual(1, levels["permit_coupon_commons"]);
        Assert.AreEqual(3, levels["truck_capacity"]);
        Assert.AreEqual(owned == "permit_billionaire_bluffs" ? 6 : 4, levels.Count);
        if (owned == "permit_billionaire_bluffs") Assert.AreEqual(1, levels["permit_stackington_heights"]);
        var before = new Dictionary<string, int>(levels);
        Call("Districts", "MigrateLegacyPermits", levels, version);
        CollectionAssert.AreEquivalent(before, levels, "Migration must be idempotent.");
    }

    [Test]
    public void NewBusinessesAndCurrentSavesNeverReceiveFreeConnectingPermits()
    {
        var empty = new Dictionary<string, int>();
        Call("Districts", "MigrateLegacyPermits", empty, 0);
        Assert.IsEmpty(empty);
        var current = new Dictionary<string, int> { ["permit_billionaire_bluffs"] = 1 };
        Call("Districts", "MigrateLegacyPermits", current, 5);
        Assert.AreEqual(1, current.Count);
    }

    [Test]
    public void AllDistrictScheduleAndFineTablesHaveSixIndependentEntries()
    {
        foreach (string field in new[] { "DistrictScale", "CommuteChance", "ErrandChance", "OutingChance" })
            Assert.AreEqual(6, ((Array)R("LifeRoutines").GetField(field).GetValue(null)).Length);
        foreach (string field in new[] { "Flattened", "CarShunted", "PropKnocked", "CityWorksDailyCap" })
            Assert.AreEqual(6, ((Array)R("WorldLifeTuning+Fines").GetField(field).GetValue(null)).Length);
    }

    [Test]
    public void NewDistrictResidentsAndSchedulesKeepTheirIdentityWithoutFourDistrictClamping()
    {
        object graph = Call("StreetGraph", "BuildTest");
        try
        {
            foreach (string field in new[] { "nodes", "houses", "loops" })
            {
                var values = Field<Array>(graph, field);
                for (int i = 0; i < values.Length; i++)
                {
                    object v = values.GetValue(i);
                    byte previous = Field<byte>(v, "district");
                    v.GetType().GetField("district").SetValue(v, (byte)(previous == 0 ? 4 : 5));
                    values.SetValue(v, i);
                }
            }
            var sim = Call("LifeSim", "Create", graph, 987654UL, null);
            var roster = Field<object>(sim, "Roster");
            var residents = Field<Array>(roster, "residents").Cast<object>().ToArray();
            CollectionAssert.AreEquivalent(new byte[] { 4, 5 }, residents.Select(r => Field<byte>(r, "district")).Distinct());
            foreach (var person in residents)
            {
                byte district = Field<byte>(person, "district");
                var archetype = Field<object>(person, "archetype");
                Assert.AreEqual(district, Field<byte>(Call("ResidentLook", "StyleFor", archetype, district, 123UL), "district"));
            }
            Assert.DoesNotThrow(() => sim.GetType().GetMethod("Day").Invoke(sim, new object[] { 7, 63 }));
            StringAssert.Contains("district 4", (string)roster.GetType().GetMethod("Describe").Invoke(roster, null));
            StringAssert.Contains("d5", (string)Call("ResidentCast", "Describe"));
        }
        finally { UnityEngine.Object.DestroyImmediate((UnityEngine.Object)graph); }
    }
}

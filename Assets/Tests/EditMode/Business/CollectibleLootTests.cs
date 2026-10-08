using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CollectibleLootTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
    static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
    static object[] Catalog => ((IEnumerable)Runtime("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>().ToArray();
    static object[] Districts => ((IEnumerable)Runtime("Districts").GetProperty("All").GetValue(null)).Cast<object>().ToArray();
    static bool Valuable(object def) => Field(def, "category").ToString() == "Valuable";
    static int Rarity(object def) => Convert.ToInt32(Field(def, "rarity"));
    static IList Items(object bag) => (IList)Field(bag, "items");
    static float Kg(object bag) => (float)Property(bag, "TotalMassKg");

    [Test]
    public void NewCatalogIsAppendOnlyAndExpandsEveryRarityWithDedicatedArt()
    {
        var defs = Catalog;
        int oldLast = Array.FindIndex(defs, d => (string)Field(d, "id") == "big_cleanup_ribbon");
        var additions = defs.Where(d => ((string)Field(d, "visual")).StartsWith("find_", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(25, additions.Length);
        CollectionAssert.AreEqual(new[] { 8, 5, 4, 4, 4 }, Enumerable.Range(0, 5).Select(r => additions.Count(d => Rarity(d) == r)).ToArray());
        Assert.AreEqual(25, additions.Select(d => Field(d, "visual")).Distinct().Count());
        foreach (var def in additions)
        {
            Assert.IsTrue(Valuable(def));
            Assert.Greater(Array.IndexOf(defs, def), oldLast, "Existing saved item indices must stay stable.");
            Assert.Greater((float)Field(def, "maxValue"), 0f);
        }
    }

    [Test]
    public void EveryNewDisplayModelIsDistinctRendererOnlyGeometry()
    {
        var build = Runtime("ItemVisuals").GetMethod("Build");
        var signatures = new HashSet<string>();
        foreach (var def in Catalog.Where(d => ((string)Field(d, "visual")).StartsWith("find_", StringComparison.Ordinal)))
        {
            var parent = new GameObject("Collectible visual audit");
            try
            {
                var model = (GameObject)build.Invoke(null, new object[] { def, parent.transform });
                Assert.Zero(model.GetComponentsInChildren<Collider>(true).Length);
                Assert.Zero(model.GetComponentsInChildren<Rigidbody>(true).Length);
                var meshes = model.GetComponentsInChildren<MeshFilter>(true);
                Assert.GreaterOrEqual(meshes.Length, 6, "Must not fall back to a generic placeholder.");
                string geometry = string.Join("|", meshes.Select(m => m.sharedMesh.name + ":" + m.transform.localPosition + ":" + m.transform.localScale + ":" + m.transform.localRotation));
                Assert.IsTrue(signatures.Add(geometry), "New collectibles must not be geometry-identical recolors.");
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
        }
    }

    [Test]
    public void OriginalFourRarityBucketsAndAbsoluteRarePlusOddsAreUnchanged()
    {
        var profiles = Districts.Take(4).Select(d => Field(d, "loot")).ToArray();
        float[][] low = { new[]{84f,14f,1.7f,.3f}, new[]{68f,25f,6f,1f}, new[]{52f,32f,13f,3f}, new[]{55f,30f,12f,3f} };
        float[][] high = { new[]{74f,20f,5f,1f}, new[]{58f,30f,10f,2f}, new[]{42f,35f,18f,5f}, new[]{47f,33f,15f,5f} };
        float[] p0 = { .005f, .006f, .0068f, .0066f }, p1 = { .008f, .007f, .0068f, .0049f };
        var catalog = Runtime("ItemCatalog");
        var weightMethod = catalog.GetMethod("RarityWeights", new[] { typeof(float), Runtime("LootProfile") });
        var chanceMethod = catalog.GetMethod("ValuableChance", new[] { typeof(float), Runtime("LootProfile") });
        for (int p = 0; p < profiles.Length; p++) foreach (float wealth in new[] { 0f, .5f, 1f })
        {
            var weights = (float[])weightMethod.Invoke(null, new[] { (object)wealth, profiles[p] });
            float chance = (float)chanceMethod.Invoke(null, new[] { (object)wealth, profiles[p] });
            Assert.AreEqual(4, weights.Length);
            Assert.That(chance, Is.EqualTo(p0[p] + p1[p] * wealth).Within(1e-8));
            for (int r = 0; r < 4; r++) Assert.That(weights[r], Is.EqualTo(Mathf.Lerp(low[p][r], high[p][r], wealth)).Within(1e-6));
            for (int r = 1; r < 4; r++)
            {
                float expected = (p0[p] + p1[p] * wealth) * Mathf.Lerp(low[p][r], high[p][r], wealth) / 100f;
                Assert.That(chance * weights[r] / weights.Sum(), Is.EqualTo(expected).Within(1e-8));
            }
        }
    }

    [Test]
    public void EverydayPassPreservesEveryOriginalValuableAndExactBagMassForMatchedSeeds()
    {
        var core = Runtime("ItemCatalog").GetMethod("RollBagCore", Static);
        var districts = Districts;
        int replacements = 0, rarePlus = 0;
        for (int tier = 0; tier < districts.Length; tier++) for (int seed = 0; seed < 750; seed++)
        {
            var profile = Field(districts[tier], "loot");
            string id = "odds-audit/" + tier + "/" + seed;
            var args = new object[] { id, seed * 7919 + 17327, (seed % 3) * .5f, "audit", profile, .6f, false };
            var before = core.Invoke(null, args);
            args[6] = true;
            var after = core.Invoke(null, args);
            Assert.AreEqual(Kg(before), Kg(after), "The same source bags must fit in a full truck.");
            var original = Items(before); var changed = Items(after);
            Assert.AreEqual(original.Count, changed.Count);
            for (int i = 0; i < original.Count; i++)
            {
                var def = Property(original[i], "Def");
                Assert.AreEqual(Field(original[i], "massKg"), Field(changed[i], "massKg"));
                if (Valuable(def))
                {
                    Assert.AreEqual(Field(original[i], "defId"), Field(changed[i], "defId"));
                    Assert.AreEqual(Field(original[i], "value"), Field(changed[i], "value"));
                    if (Rarity(def) >= 2) rarePlus++;
                }
                else if (Valuable(Property(changed[i], "Def")))
                {
                    replacements++;
                    var added = Property(changed[i], "Def");
                    Assert.LessOrEqual(Rarity(added), 1);
                    Assert.That((float)Field(changed[i], "massKg"), Is.InRange((float)Field(added, "minMass"), (float)Field(added, "maxMass")));
                }
            }
        }
        Assert.Greater(replacements, 50);
        Assert.Greater(rarePlus, 100, "Exercise actual rare outcomes and rich-theme guarantees, not only empty bags.");
    }

    [TestCase(120f, 2.1f, 3.5f, .65f)]
    [TestCase(160f, 2.8f, 4.5f, .75f)]
    public void FullStarterTruckUsuallyContainsSeveralLowTierFindsWithoutGuaranteedRares(float capacity, float minFinds, float maxFinds, float minTwoFraction)
    {
        var catalog = Runtime("ItemCatalog");
        var roll = catalog.GetMethod("RollBag", new[] { typeof(string), typeof(int), typeof(float), typeof(string), Runtime("LootProfile"), typeof(float) });
        var profile = Field(Districts[0], "loot");
        const int trucks = 256;
        int totalLow = 0, twoOrMore = 0;
        for (int truck = 0; truck < trucks; truck++)
        {
            float load = 0; int low = 0;
            for (int attempt = 0; attempt < 100 && load < capacity - 4f; attempt++)
            {
                int seed = unchecked(17327 + truck * 7919 + attempt * 104729 + 10000019);
                var bag = roll.Invoke(null, new object[] { "probe/1/" + truck + "/" + attempt, seed, .5f, "audit", profile, .6f });
                if (load + Kg(bag) > capacity) continue;
                load += Kg(bag);
                foreach (var item in Items(bag))
                {
                    var def = Property(item, "Def");
                    if (Valuable(def) && Rarity(def) <= 1) low++;
                }
            }
            totalLow += low;
            if (low >= 2) twoOrMore++;
        }
        Assert.That(totalLow / (float)trucks, Is.InRange(minFinds, maxFinds));
        Assert.Greater(twoOrMore / (float)trucks, minTwoFraction);
    }
}

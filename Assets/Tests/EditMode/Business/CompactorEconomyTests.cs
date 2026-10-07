using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>Runtime economy is in Assembly-CSharp; this asmdef accesses its pure rate API by reflection.</summary>
public class CompactorEconomyTests
{
    static Type Economy => Type.GetType("Economy, Assembly-CSharp", true);
    static Type Effects => Type.GetType("UpgradeEffects, Assembly-CSharp", true);

    static float Constant(string name) => (float)Economy.GetField(name, BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();

    static float Rate(string name, int level) =>
        (float)Effects.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { level });

    [TestCase(0, 0.20f)]
    [TestCase(1, 0.25f)]
    [TestCase(2, 0.30f)]
    [TestCase(3, 0.35f)]
    public void ChamberAndBulkHaveTheSameRewardAtEveryUpgrade(int level, float expected)
    {
        Assert.AreEqual(expected, Rate("CompactPerKgAtLevel", level), 0.00001f);
        Assert.AreEqual(expected, Rate("LazyPerKgAtLevel", level), 0.00001f);
        Assert.AreEqual(expected, Constant("CompactorScrapPerKg") * Rate("ScrapMultAtLevel", level), 0.00001f);
    }

    [Test]
    public void CompactionRemainsBelowEveryUnupgradedRecyclingCategory()
    {
        var category = Type.GetType("ItemCategory, Assembly-CSharp", true);
        var recycle = Economy.GetMethod("RecycleRatePerKg", BindingFlags.Public | BindingFlags.Static);
        foreach (string name in new[] { "Paper", "Glass", "Plastic", "Metal" })
        {
            float recycling = (float)recycle.Invoke(null, new[] { Enum.Parse(category, name) });
            for (int level = 0; level <= 3; level++)
                Assert.Less(Rate("CompactPerKgAtLevel", level), recycling, $"{name}, compactor L{level}");
        }
    }

    [Test]
    public void InvalidSavedLevelsClampAndLegacyBaseAliasesAgree()
    {
        foreach (string api in new[] { "CompactPerKgAtLevel", "LazyPerKgAtLevel", "ScrapMultAtLevel" })
        {
            Assert.AreEqual(Rate(api, 0), Rate(api, -5), api);
            Assert.AreEqual(Rate(api, 3), Rate(api, 99), api);
        }
        Assert.AreEqual(Constant("CompactorScrapPerKg"), Constant("LazyDumpPerKg"));
    }

    [Test]
    public void HazardCompactionNeverChargesAFine()
    {
        Assert.Zero(Constant("HazardCompactFine"));
    }
}

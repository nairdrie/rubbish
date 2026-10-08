using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>Reflection because garage game code lives in Assembly-CSharp, outside this asmdef's references.</summary>
public class BusinessUpgradeRulesTests
{
    static Type Net => Type.GetType("BusinessUpgradeNet, Assembly-CSharp", true);
    static Type Effects => Type.GetType("UpgradeEffects, Assembly-CSharp", true);

    static bool Valid(ulong levels)
    {
        var m = Net.GetMethod("ValidPacked", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (bool)m.Invoke(null, new object[] { levels });
    }

    static float Number(string name, int level)
    {
        var m = Effects.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m, name);
        return (float)m.Invoke(null, new object[] { level });
    }

    [Test]
    public void PackedLevelsRejectAnyCatalogOverflowIncludingHighestNibble()
    {
        Assert.IsTrue(Valid(0UL));
        Assert.IsTrue(Valid(4UL << (6 * 4)));  // Magnus brain L4
        Assert.IsTrue(Valid(1UL << (15 * 4))); // Disco Ball cosmetic
        Assert.IsTrue(Valid(1UL));              // Auto Tipper L1
        Assert.IsFalse(Valid(2UL));             // Auto Tipper is one level now (its old L2 is migrated on load)
        Assert.IsFalse(Valid(2UL << (7 * 4)));  // Rail Motor maximum is L1
        Assert.IsFalse(Valid(2UL << (15 * 4))); // Final nibble must be checked too
    }

    [Test]
    public void GaragePayoutTablesClampExplicitBusinessLevels()
    {
        Assert.AreEqual(1f, Number("RecycleMultAtLevel", -1));
        Assert.AreEqual(4f, Number("RecycleMultAtLevel", 99));
        Assert.AreEqual(0.75f, Number("HazardPayAtLevel", 0));
        Assert.AreEqual(4f, Number("HazardPayAtLevel", 2));
        Assert.AreEqual(1f, Number("ScrapMultAtLevel", 0));
        Assert.AreEqual(1.25f, Number("ScrapMultAtLevel", 1));
        Assert.AreEqual(0f, Number("GrateKeepAtLevel", 2));
        Assert.AreEqual(0.3f, Number("GrateKeepAtLevel", 3));
        Assert.AreEqual(0.20f, Number("LazyPerKgAtLevel", 0));
        Assert.AreEqual(0.35f, Number("LazyPerKgAtLevel", 3));
    }
}

using System;
using System.Reflection;
using NUnit.Framework;

public class GuardedCrossingImpactTests
{
    static bool Target(uint packed, long crossing) => (bool)Type.GetType("HandsNet, Assembly-CSharp", true)
        .GetMethod("CrossingTarget", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { packed, crossing });

    [TestCase(0x70000u)]
    [TestCase(0x70001u)]
    [TestCase(0x70005u)]
    [TestCase(0x70008u)]
    [TestCase(0x7000du)]
    public void OnlyTheTwoSitesGuardAndFiveChildSlotsAreLegal(uint packed) => Assert.IsTrue(Target(packed, 17));

    [TestCase(0u)]
    [TestCase(0x10000u)]
    [TestCase(0x60000u)]
    [TestCase(0x70006u)]
    [TestCase(0x70007u)]
    [TestCase(0x7000eu)]
    [TestCase(0x70010u)]
    [TestCase(0x10070000u)]
    public void ContactClaimsCannotTargetOtherNpcsUnusedSlotsOrPackedAliases(uint packed) => Assert.IsFalse(Target(packed, 17));

    [Test]
    public void InactiveCrossingSerialCannotBeReusedAsAClaim() => Assert.IsFalse(Target(0x70000u, -1));
}

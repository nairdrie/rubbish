using System;
using System.Reflection;
using NUnit.Framework;

public class DistrictPermitReplicationTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => R(type).GetMethod(method, Static).Invoke(null, args);

    [TestCase("permit_minivan_meadows", 3)]
    [TestCase("permit_snootington_hills", 4)]
    [TestCase("permit_billionaire_bluffs", 5)]
    [TestCase("permit_coupon_commons", 6)]
    [TestCase("permit_stackington_heights", 7)]
    public void PermitFlagsDoNotOverlapPersonalPoleOrHeadlights(string id, int bit)
    {
        byte packed = (byte)((1 << bit) | 5); // pole + level2 headlights
        Assert.AreEqual(1, Call("BusinessUpgradeNet", "DecodePermitLevel", packed, id));
        Assert.AreEqual(0, Call("BusinessUpgradeNet", "DecodePermitLevel", (byte)5, id));
        Assert.IsTrue((bool)Call("BusinessUpgradeNet", "ValidDeclaration", 0UL, packed));
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "ValidDeclaration", 0UL, (byte)(packed | 6)), "Headlight level3 is still rejected.");
    }

    [Test]
    public void SixTrustValuesAreIndependentBoundedAndUnknownPermitIsClosed()
    {
        uint packed = 0;
        for (int i = 0; i < 6; i++) packed |= (uint)(i * 6) << (i * 5);
        Assert.IsTrue((bool)Call("BusinessUpgradeNet", "ValidDistrictTrust", packed));
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "ValidDistrictTrust", packed | (1u << 30)));
        for (int i = 0; i < 6; i++) Assert.That((float)Call("BusinessUpgradeNet", "DecodeDistrictTrust", packed, i), Is.EqualTo(i * 6f / 31f).Within(.00001f));
        Assert.AreEqual(0f, Call("BusinessUpgradeNet", "DecodeDistrictTrust", packed, 6));
        Assert.AreEqual(0, Call("BusinessUpgradeNet", "DecodePermitLevel", (byte)255, "unknown_permit"));
        Assert.IsFalse((bool)Call("BusinessUpgradeNet", "HasPermitForBusiness", "offline-nobody", Call("Districts", "Get", "coupon_commons")));
    }

    [TestCase(true, false, false, false, true)]
    [TestCase(false, true, true, true, true)]
    [TestCase(false, false, true, true, false)]
    [TestCase(false, true, false, true, false)]
    [TestCase(false, true, true, false, false)]
    public void OnlyOwnPermitOrCurrentOnlineEmployersPermitGrantsCrewAccess(bool own, bool hired, bool online, bool employerPermit, bool allowed) =>
        Assert.AreEqual(allowed, Call("DistrictAccess", "CrewAllowed", own, hired, online, employerPermit));

    [Test]
    public void TrustChangeIsVisibleToNetworkListAndLateJoinSnapshot()
    {
        Type type = R("BusinessUpgradeInfo");
        var before = Activator.CreateInstance(type); var after = Activator.CreateInstance(type);
        type.GetField("districtTrust").SetValue(after, 31u << 25);
        Assert.IsFalse(before.Equals(after));
        Assert.AreNotEqual(before.GetHashCode(), after.GetHashCode());
    }
}

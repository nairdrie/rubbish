using NUnit.Framework;
using UnityEngine;

/// <summary>Package N1 (Docs/Multiplayer.md §13): business colours, names, ids and the trash rate table.</summary>
public class BusinessRulesTests
{
    [Test]
    public void FourColoursOnePerSlot()
    {
        Assert.AreEqual(4, BusinessColors.Count);
        Assert.AreEqual(NetConstants.TownCap, BusinessColors.Count, "every player in a Town has a colour");
        Assert.AreEqual("Hi-Vis Orange", BusinessColors.Name(0));
        Assert.AreEqual("Bin Teal", BusinessColors.Name(1));
        Assert.AreEqual("Grape Purple", BusinessColors.Name(2));
        Assert.AreEqual("Slime Lime", BusinessColors.Name(3));
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                Assert.AreNotEqual((Color)BusinessColors.For(i), (Color)BusinessColors.For(j));
        Assert.AreEqual(BusinessColors.Name(0), BusinessColors.Name(-3), "bad indexes fall back to slot 0");
        Assert.AreEqual(BusinessColors.Name(1), BusinessColors.Name(5), "indexes wrap");
        StringAssert.StartsWith("#", BusinessColors.Hex(2));
    }

    [Test]
    public void NamesAreCleanedAndCapped()
    {
        Assert.AreEqual("Rubbish Co.", BusinessColors.CleanName(null));
        Assert.AreEqual("Rubbish Co.", BusinessColors.CleanName("   "));
        Assert.AreEqual("Dot's Disposal", BusinessColors.CleanName("  Dot's Disposal "));
        Assert.AreEqual("bnoparsebi", BusinessColors.CleanName("<b>no<parse>bi"));
        Assert.AreEqual(24, BusinessColors.CleanName(new string('x', 60)).Length);
        Assert.AreEqual("Nick's", BusinessColors.OwnerLine("Nick"));
        Assert.AreEqual("Chris'", BusinessColors.OwnerLine("Chris"));
        Assert.AreEqual("", BusinessColors.OwnerLine(""));
    }

    [Test]
    public void IdsAreFreshAndValidated()
    {
        string a = BusinessIds.New(), b = BusinessIds.New();
        Assert.AreNotEqual(a, b);
        Assert.AreEqual(17, a.Length);
        Assert.IsTrue(BusinessIds.IsValid(a));
        Assert.IsFalse(BusinessIds.IsValid(null));
        Assert.IsFalse(BusinessIds.IsValid("x"));
        Assert.IsFalse(BusinessIds.IsValid("../../etc"));
        Assert.IsFalse(BusinessIds.IsValid("Upper"));
        Assert.IsFalse(BusinessIds.IsValid(new string('a', 41)));
    }

    [Test]
    public void TrashRateScalesWithBusinesses()
    {
        Assert.AreEqual(1.0f, BusinessRates.PutOutScale(1), 1e-6f);
        Assert.AreEqual(1.7f, BusinessRates.PutOutScale(2), 1e-6f);
        Assert.AreEqual(2.3f, BusinessRates.PutOutScale(3), 1e-6f);
        Assert.AreEqual(2.8f, BusinessRates.PutOutScale(4), 1e-6f);
        Assert.AreEqual(1.0f, BusinessRates.PutOutScale(0), 1e-6f, "never below solo");
        Assert.AreEqual(2.8f, BusinessRates.PutOutScale(9), 1e-6f, "capped at a full Town");
    }
}

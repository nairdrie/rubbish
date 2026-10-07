using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>Reflection because the sorting-table runtime lives in Assembly-CSharp.</summary>
public class SortingTableCapacityRulesTests
{
    static bool Fits(int max, int landed, int pending, int bagItems)
    {
        var type = Type.GetType("SortingTableCapacityRules, Assembly-CSharp", true);
        var method = type.GetMethod("Fits", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, new object[] { max, landed, pending, bagItems });
    }

    [Test]
    public void ExactRemainingSlotsAdmitWholeBag()
    {
        Assert.IsTrue(Fits(40, 28, 5, 7));
        Assert.IsFalse(Fits(40, 28, 5, 8));
    }

    [Test]
    public void OverlappingRipsReserveTheirUnspawnedContents()
    {
        Assert.IsTrue(Fits(40, 25, 0, 9));
        Assert.IsFalse(Fits(40, 25, 9, 9));
    }

    [Test]
    public void FullBagIsNeverPartiallyAdmittedAndArithmeticCannotOverflow()
    {
        Assert.IsFalse(Fits(40, 39, 0, 2));
        Assert.IsFalse(Fits(40, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.IsFalse(Fits(40, -1, 0, 1));
    }
}

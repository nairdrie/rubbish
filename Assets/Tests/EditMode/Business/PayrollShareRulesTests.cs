using System;
using NUnit.Framework;

/// <summary>Pure payroll contract, eligibility and exact-cent rules; no saves, network session or game objects.</summary>
public sealed class PayrollShareRulesTests
{
    static Type Rules => Type.GetType("PayrollShareRules, Assembly-CSharp", true);
    static Type Payroll => Type.GetType("Payroll, Assembly-CSharp", true);
    static Type Job => Type.GetType("PayrollJob, Assembly-CSharp", true);
    static bool Valid(int wage, int share, int crew = 0) =>
        (bool)Rules.GetMethod("ValidContract").Invoke(null, new object[] { wage, share, crew });
    static bool Eligible(string kind, long cents, string id) =>
        (bool)Rules.GetMethod("EligibleCredit").Invoke(null, new object[] { kind, cents, id });
    static long Earn(long cents, int percent, ref byte remainder)
    {
        var args = new object[] { cents, percent, remainder, (byte)0 };
        long paid = (long)Rules.GetMethod("Accrue").Invoke(null, args);
        remainder = (byte)args[3];
        return paid;
    }

    [Test]
    public void StarterWageIsFiveDollarsAndFixedWagesRemainAnOption()
    {
        Assert.AreEqual(500, Payroll.GetField("DefaultWageCents").GetRawConstantValue());
        Assert.IsTrue(Valid(100, 0));
        Assert.IsTrue(Valid(500, 0, 75), "fixed wage contracts do not spend earnings-share capacity");
        Assert.IsTrue(Valid(20000, 0));
        Assert.IsFalse(Valid(0, 0));
        Assert.IsFalse(Valid(20001, 0));
    }

    [Test]
    public void SharesHaveNoHourlyWageAndCannotPromiseMoreThanTheCrewCap()
    {
        Assert.IsTrue(Valid(0, 20));
        Assert.IsTrue(Valid(0, 50, 25));
        Assert.IsFalse(Valid(500, 20), "contracts are wage OR share, never a hidden second bill");
        Assert.IsFalse(Valid(0, 4));
        Assert.IsFalse(Valid(0, 51));
        Assert.IsFalse(Valid(0, 20, 60), "rechecked on acceptance, not only when an offer was made");
        Assert.IsFalse(Valid(0, 20, int.MaxValue), "invalid counts must not overflow into an accepted contract");
        Assert.IsFalse(Valid(0, 20, -1));
    }

    [TestCase("pickup_fee")]
    [TestCase("recycle")]
    [TestCase("lazy_crush")]
    [TestCase("ebae")]
    [TestCase("pawn")]
    [TestCase("clean_streets")]
    [TestCase("night_shift")]
    [TestCase("event_prize")]
    [TestCase("raccoon_crew")]
    public void PositiveOperatingIncomeEarnsShares(string id) => Assert.IsTrue(Eligible("credit", 1, id));

    [TestCase("payroll_wage")]
    [TestCase("payroll_wages")]
    [TestCase("payroll_share")]
    [TestCase("payroll_shares")]
    [TestCase("purchase")]
    [TestCase("refund")]
    [TestCase("debug")]
    [TestCase("kevin_fees")]
    [TestCase("raccoon_antics")]
    [TestCase("hutch_food")]
    [TestCase(null)]
    public void WagesSharesRefundsAndUnknownCashNeverRecurse(string id) => Assert.IsFalse(Eligible("credit", 10000, id));

    [Test]
    public void LossesSpendsReputationAndZeroIncomeNeverAccruePay()
    {
        Assert.IsFalse(Eligible("credit", 0, "recycle"));
        Assert.IsFalse(Eligible("credit", -100, "recycle"));
        Assert.IsFalse(Eligible("charge", 100, "recycle"));
        Assert.IsFalse(Eligible("spend", 100, "ebae"));
        Assert.IsFalse(Eligible("rep", 100, "pickup_fee"));
        byte remainder = 40;
        Assert.AreEqual(0, Earn(-100, 20, ref remainder));
        Assert.AreEqual(40, remainder);
    }

    [Test]
    public void SmallCreditsCarryFractionsInsteadOfLosingOrDuplicatingPennies()
    {
        byte remainder = 0;
        long paid = 0;
        for (int i = 0; i < 5; i++) paid += Earn(1, 20, ref remainder);
        Assert.AreEqual(1, paid);
        Assert.AreEqual(0, remainder);
        Assert.AreEqual(2000, Earn(10000, 20, ref remainder));
    }

    [Test]
    public void SplitAndUnsplitIncomeHaveExactlyTheSameCumulativePayout()
    {
        byte splitRemainder = 0, onceRemainder = 0;
        long split = 0;
        for (int i = 0; i < 103; i++) split += Earn(7, 37, ref splitRemainder);
        Assert.AreEqual(Earn(721, 37, ref onceRemainder), split);
        Assert.AreEqual(onceRemainder, splitRemainder);
        byte largeRemainder = 0;
        Assert.AreEqual(long.MaxValue / 2, Earn(long.MaxValue, 50, ref largeRemainder));
        Assert.AreEqual(50, largeRemainder);
    }

    [Test]
    public void ReplicatedJobEqualityIncludesShareContractAndFractionalAccrual()
    {
        object a = Activator.CreateInstance(Job), b = Activator.CreateInstance(Job);
        Assert.IsTrue(a.Equals(b));
        Job.GetField("sharePercent").SetValue(b, (byte)20);
        Assert.IsFalse(a.Equals(b));
        Job.GetField("sharePercent").SetValue(a, (byte)20);
        Assert.IsTrue(a.Equals(b));
        Job.GetField("shareRemainder").SetValue(b, (byte)20);
        Assert.IsFalse(a.Equals(b));
        Assert.IsTrue((bool)Job.GetProperty("IsEarningsShare").GetValue(b));
    }

    [Test]
    public void SessionExitAndSessionRolesDoNotPretendToBeEmployment()
    {
        var text = Type.GetType("UiText+Pause, Assembly-CSharp", true);
        Assert.AreEqual("LEAVE TOWN", text.GetField("LeaveShift").GetRawConstantValue());
        Assert.AreEqual("HOST", text.GetField("Boss").GetRawConstantValue());
        Assert.AreEqual("GUEST", text.GetField("Crew").GetRawConstantValue());
        StringAssert.Contains("QUIT JOB", (string)text.GetField("LeaveBody").GetRawConstantValue());
    }
}

using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>The first campaign slice (Docs/Campaign.md §14): the pure rules, the contract table and what the store says.</summary>
public class CampaignRulesTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => R(type).GetMethod(method, Static).Invoke(null, args);
    static object Const(string type, string field) => R(type).GetField(field, Static).GetRawConstantValue();

    // ------------------------------------------------------------------ the hold

    [TestCase(0.90f, true)]
    [TestCase(0.895f, true)]
    [TestCase(0.894f, false)]
    [TestCase(1.0f, true)]
    [TestCase(0.0f, false)]
    public void ThresholdCountsTheRoundedPercent(float clean, bool expected) =>
        Assert.AreEqual(expected, Call("CampaignRules", "Qualifies", clean));

    static (double held, bool started, bool broke, double lost, bool won) Advance(double held, bool ok, double dt, double hold = 24.0)
    {
        var args = new object[] { held, ok, dt, hold, false, false, 0.0, false };
        double next = (double)Call("CampaignRules", "Advance", args);
        return (next, (bool)args[4], (bool)args[5], (double)args[6], (bool)args[7]);
    }

    [Test]
    public void TheHoldStartsWithTheFirstQualifyingTickAndKeepsCounting()
    {
        var a = Advance(0, true, 0.5);
        Assert.IsTrue(a.started);
        Assert.AreEqual(0.5, a.held, 1e-9);
        var b = Advance(a.held, true, 0.5);
        Assert.IsFalse(b.started, "only the first tick of an attempt is a start");
        Assert.AreEqual(1.0, b.held, 1e-9);
        Assert.IsFalse(b.won);
    }

    [Test]
    public void OneDipEndsTheAttemptAndReportsTheHoursLost()
    {
        var a = Advance(7.25, false, 0.5);
        Assert.IsTrue(a.broke);
        Assert.AreEqual(7.25, a.lost, 1e-9);
        Assert.AreEqual(0.0, a.held);
        var again = Advance(a.held, false, 0.5);
        Assert.IsFalse(again.broke, "nothing left to lose while it stays dirty");
    }

    [Test]
    public void ADayAtTheThresholdWinsAndNeverOvershoots()
    {
        var a = Advance(23.8, true, 0.5);
        Assert.IsTrue(a.won);
        Assert.AreEqual(24.0, a.held, 1e-9);
        var b = Advance(10, true, 0.5);
        Assert.IsFalse(b.won);
    }

    [Test]
    public void AHeldClockCountsNothing()
    {
        var a = Advance(5, true, 0.0);
        Assert.AreEqual(5.0, a.held, 1e-9);
        Assert.IsFalse(a.started);
    }

    [TestCase(24.0, "24h 00m")]
    [TestCase(23.6833, "23h 41m")]
    [TestCase(23.99, "24h 00m")]
    [TestCase(1.0, "1h 00m")]
    [TestCase(0.5, "30m")]
    [TestCase(0.001, "1m")]
    [TestCase(0.0, "0m")]
    [TestCase(-3.0, "0m")]
    public void TheCountdownRoundsUpToTheGameMinute(double hoursLeft, string expected) =>
        Assert.AreEqual(expected, Call("CampaignRules", "FormatLeft", hoursLeft));

    [TestCase(0.95f, false, 95)]
    [TestCase(0.95f, true, 89)]
    [TestCase(0.50f, true, 50)]
    [TestCase(0.894f, false, 89)]
    [TestCase(1.5f, false, 100)]
    [TestCase(-1f, false, 0)]
    public void TheBarNeverCrossesTheLineWhileOldTrashIsLeft(float clean, bool oldTrashLeft, int expected) =>
        Assert.AreEqual(expected, Call("CampaignRules", "BarPercent", clean, oldTrashLeft));

    [Test]
    public void TheWholeHeapIsExactlyAllTheDirt()
    {
        float one = (float)Call("CampaignRules", "HeapDirtPerBag", 30f, 56);
        Assert.AreEqual(30f, one * 56, 1e-3f);
        Assert.AreEqual(26f, (float)Call("CampaignRules", "HeapDirtPerBag", 26f, 48) * 48, 1e-3f);
        Assert.AreEqual(30f, (float)Call("CampaignRules", "HeapDirtPerBag", 30f, 0), "a zero heap never divides by zero");
    }

    // ------------------------------------------------------------------ the heaps

    [TestCase("mm-juicebox-210/old7", true)]
    [TestCase("user-12/old1", true)]
    [TestCase("mm-juicebox-210/9f8e7d6c5b4a", false)]
    [TestCase("old/xyz", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void OldBagIdsAreRecognisedByTheirTag(string id, bool expected) =>
        Assert.AreEqual(expected, Call("CampaignRules", "IsBacklogBagId", id));

    [Test]
    public void AnOldBagStillBelongsToItsStopsDistrict()
    {
        string id = (string)Call("CampaignRules", "BacklogBagId", "mm-juicebox-210", 7);
        Assert.AreEqual("mm-juicebox-210/old7", id);
        var district = Call("Districts", "ForBagId", id);
        Assert.AreEqual("minivan_meadows", R("DistrictDef").GetField("id").GetValue(district));
    }

    [TestCase(56, 37, 23)]
    [TestCase(48, 22, 20)]
    [TestCase(5, 2, 2)]
    [TestCase(0, 10, 0)]
    [TestCase(10, 0, 0)]
    public void HeapsAreAboutTwoAndAHalfBagsEach(int bags, int houses, int expected) =>
        Assert.AreEqual(expected, Call("CampaignRules", "HeapCount", bags, houses));

    [Test]
    public void EveryOldBagIsPastItsDistrictsNeglectWindowSoItStinks()
    {
        foreach (uint hash in new uint[] { 0u, 1u, 0x7FFFu, 0xFFFFu, 0xFFFFFFFFu })
        {
            double age = (double)Call("CampaignRules", "BacklogAgeHours", 16f, hash);
            Assert.GreaterOrEqual(age, 16.0 + 2.0 - 1e-9);
            Assert.LessOrEqual(age, 16.0 + 20.0 + 1e-9);
        }
    }

    [Test]
    public void TheSavedCountKeepsZeroMeaningNeverSeeded()
    {
        Assert.AreEqual(-1, Call("CampaignRules", "DecodeLeft", 0));
        Assert.AreEqual(0, Call("CampaignRules", "DecodeLeft", Call("CampaignRules", "EncodeLeft", 0)));
        Assert.AreEqual(41, Call("CampaignRules", "DecodeLeft", Call("CampaignRules", "EncodeLeft", 41)));
        Assert.AreEqual(1, Call("CampaignRules", "EncodeLeft", -5), "a negative remainder is stored as none left");
    }

    // ------------------------------------------------------------------ the table and the store

    [Test]
    public void TheAcresOpenTheMeadowsAndTheMeadowsOpenCommons()
    {
        var acres = Call("Campaign", "ContractFor", "mildly_pleasant");
        var meadows = Call("Campaign", "ContractFor", "minivan_meadows");
        Assert.AreEqual("minivan_meadows", R("CampaignContract").GetField("unlocksId").GetValue(acres));
        Assert.AreEqual("coupon_commons", R("CampaignContract").GetField("unlocksId").GetValue(meadows));
        Assert.IsNull(Call("Campaign", "ContractFor", "snootington_hills"), "slice 1 stops at Commons");
        Assert.IsNull(Call("Campaign", "ContractFor", ""));
    }

    [Test]
    public void TheSecondTownIsHarderAndTheFirstIsUnchanged()
    {
        var acres = Call("Campaign", "ContractFor", "mildly_pleasant");
        var meadows = Call("Campaign", "ContractFor", "minivan_meadows");
        var rate = R("CampaignContract").GetField("rateMult");
        Assert.AreEqual(1f, (float)rate.GetValue(acres));
        Assert.Greater((float)rate.GetValue(meadows), 1f);
    }

    [TestCase("permit_minivan_meadows", true)]
    [TestCase("permit_coupon_commons", true)]
    [TestCase("permit_snootington_hills", true)]   // the Commons contract (2026-10-14)
    [TestCase("permit_stackington_heights", false)]
    [TestCase("permit_billionaire_bluffs", false)]
    [TestCase("truck_turbo", false)]
    [TestCase(null, false)]
    public void OnlyTheContractDistrictsPermitsAreOffTheShelf(string id, bool contract) =>
        Assert.AreEqual(contract, Call("Campaign", "IsContractPermit", id));

    [Test]
    public void TheStoreSaysWhatToDoInsteadOfWhatItCosts()
    {
        var catalog = R("ScrapazonCatalog");
        var item = catalog.GetMethod("Get", Static).Invoke(null, new object[] { "permit_minivan_meadows" });
        Assert.IsNotNull(item);
        var args = new object[] { item, 1, null };
        bool ok = (bool)Call("Progression", "CanBuy", args);
        Assert.IsFalse(ok);
        string why = (string)args[2];
        StringAssert.Contains("Mildly Pleasant", why);
        StringAssert.Contains("90%", why);
        StringAssert.Contains("24", why);
    }

    [Test]
    public void OtherPermitsAreStillGatedByRankAndTheChainLikeBefore()
    {
        // Heights is the first permit past the contracts (Acres -> Meadows -> Commons -> Hills)
        var item = R("ScrapazonCatalog").GetMethod("Get", Static).Invoke(null, new object[] { "permit_stackington_heights" });
        var args = new object[] { item, 1, null };
        Assert.IsFalse((bool)Call("Progression", "CanBuy", args));
        StringAssert.DoesNotContain("contract", (string)args[2]);
    }

    [Test]
    public void OnlyDistrictsOpenedByContractHaveAContractLockLine()
    {
        Assert.IsNotNull(Call("Campaign", "LockedReason", "minivan_meadows"));
        Assert.IsNotNull(Call("Campaign", "LockedReason", "coupon_commons"));
        Assert.IsNotNull(Call("Campaign", "LockedReason", "snootington_hills"), "won with the Commons contract");
        Assert.IsNull(Call("Campaign", "LockedReason", "stackington_heights"));
        Assert.IsNull(Call("Campaign", "LockedReason", "billionaire_bluffs"));
        Assert.IsNull(Call("Campaign", "LockedReason", "mildly_pleasant"));
    }

    [Test]
    public void TheTuningIsTheNinetyPercentDayTheBriefAskedFor()
    {
        Assert.AreEqual(0.90f, (float)Const("CampaignTuning", "Threshold"), 1e-6f);
        Assert.AreEqual(24.0, (double)Const("CampaignTuning", "HoldHours"), 1e-9);
    }

    [Test]
    public void TheHudStartsEmptyAndTwoDisplaysCompareByValue()
    {
        var t = R("CampaignDisplay");
        object none = t.GetProperty("None", Static).GetValue(null);
        Assert.IsFalse((bool)t.GetProperty("Active").GetValue(none));
        object copy = t.GetProperty("None", Static).GetValue(null);
        Assert.IsTrue(none.Equals(copy));
        var changed = Activator.CreateInstance(t);
        t.GetField("district").SetValue(changed, (sbyte)1);
        Assert.IsFalse(none.Equals(changed));
        Assert.IsTrue((bool)t.GetProperty("Active").GetValue(changed));
    }
}

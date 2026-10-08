using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

public class StarterCollectionContractTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string method, params object[] args) => Runtime("StarterCollectionContract").GetMethod(method).Invoke(null, args);
    static int Advance(int count, string kind, string id, long cents, bool trusted, out bool award)
    {
        object[] args = { count, kind, id, cents, trusted, false };
        int next = (int)Call("Advance", args);
        award = (bool)args[5];
        return next;
    }

    [Test]
    public void EighthPaidBagAwardsExactlyOnceEvenWithFurtherPickups()
    {
        int count = 0, awards = 0;
        for (int bag = 1; bag <= 32; bag++)
        {
            count = Advance(count, "credit", "pickup_fee", 400, true, out bool award);
            if (award) { awards++; Assert.AreEqual(8, bag); }
            Assert.AreEqual(Math.Min(bag, 8), count);
        }
        Assert.AreEqual(1, awards);
    }

    [TestCase("credit", "pickup_fee", 1L, true, 1)]
    [TestCase("credit", "pickup_fee", 0L, true, 0)]
    [TestCase("credit", "pickup_fee", -1L, true, 0)]
    [TestCase("credit", "pickup_fee", 400L, false, 0)]
    [TestCase("charge", "pickup_fee", 400L, true, 0)]
    [TestCase("rep", "pickup_fee", 400L, true, 0)]
    [TestCase("credit", "curb_bonus", 200L, true, 0)]
    [TestCase("credit", "recycle", 200L, true, 0)]
    [TestCase("credit", "starter_collection", 10000L, true, 0)]
    public void OnlyAuthoritativePositivePickupFeesCount(string kind, string id, long cents, bool trusted, int expected)
    {
        Assert.AreEqual(expected, Advance(0, kind, id, cents, trusted, out bool award));
        Assert.IsFalse(award);
    }

    [TestCase(5, 0, 5)]
    [TestCase(5, 3, 5)]
    [TestCase(3, 5, 5)]
    [TestCase(8, 0, 8)]
    [TestCase(0, 8, 8)]
    [TestCase(-10, -5, 0)]
    [TestCase(99, 0, 8)]
    public void ReconnectUsesMaximumSavedOrRememberedProgress(int remembered, int saved, int expected) =>
        Assert.AreEqual(expected, Call("MergeProgress", remembered, saved));

    [Test]
    public void CompletedOrMigratedBusinessesCannotClaimAgain()
    {
        foreach (int current in new[] { 8, 99 })
        {
            int resumed = (int)Call("MergeProgress", current, 0);
            Assert.AreEqual(8, Advance(resumed, "credit", "pickup_fee", 400, true, out bool award));
            Assert.IsFalse(award);
        }
    }

    [Test]
    public void GrantFundsExactlyTheFirstHopperUpgrade()
    {
        float grant = (float)Runtime("StarterCollectionContract").GetField("Reward").GetRawConstantValue();
        var product = Runtime("ScrapazonCatalog").GetMethod("Get").Invoke(null, new object[] { "truck_capacity" });
        var prices = (float[])product.GetType().GetField("prices").GetValue(product);
        Assert.AreEqual(100f, grant);
        Assert.AreEqual(prices[0], grant);
        Assert.AreEqual("starter_collection_progress", Runtime("StarterCollectionContract").GetField("ProgressKey").GetRawConstantValue());
    }

    [TestCase("mine", "mine", 0, true)]
    [TestCase("mine", "mine", 8, true)]
    [TestCase("mine", "employer", 8, false)]
    [TestCase("new-save", "old-save", 8, false)]
    [TestCase(null, null, 0, false)]
    [TestCase("mine", "mine", 9, false)]
    [TestCase("mine", "mine", -1, false)]
    public void LateProgressCannotMarkAnotherBusinessComplete(string current, string message, int progress, bool expected) =>
        Assert.AreEqual(expected, Call("SnapshotMatches", current, message, progress));

    [Test]
    public void AwardLedgerIdIsReservedAndOnlyExactRewardCreditsMarkCompletion()
    {
        Assert.IsTrue((bool)Call("IsReservedLedgerId", "starter_collection"));
        Assert.IsFalse((bool)Call("IsReservedLedgerId", "pickup_fee"));
        Assert.IsTrue((bool)Call("IsRewardCredit", "credit", "starter_collection", 10000L));
        Assert.IsFalse((bool)Call("IsRewardCredit", "credit", "pickup_fee", 10000L));
        Assert.IsFalse((bool)Call("IsRewardCredit", "credit", "starter_collection", 1L));
        Assert.IsFalse((bool)Call("IsRewardCredit", "charge", "starter_collection", -10000L));
    }

    [TestCase("own-main-save", "own-main-save", true)]
    [TestCase("guest-business", "guest-business", true)]
    [TestCase("new-save", "old-save", false)]
    [TestCase("employee", "employer", false)]
    [TestCase(null, null, false)]
    public void CashMirrorCannotCreditAPreviouslySelectedOrEmployersBusiness(string selected, string message, bool expected)
    {
        var check = Runtime("BusinessRegistry").GetMethod("CashTargetsBusiness", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.AreEqual(expected, check.Invoke(null, new object[] { selected, message }));
    }

    [Test]
    public void CareerCardShowsProgressRewardAndCompletion()
    {
        StringAssert.Contains("0/8", (string)Call("ProgressText", 0));
        StringAssert.Contains("$100", (string)Call("ProgressText", 4));
        StringAssert.Contains("COMPLETE", (string)Call("ProgressText", 8));
        var row = (VisualElement)Runtime("ScrapazonCareer").GetMethod("StarterContractRow", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        Assert.IsTrue(row.ClassListContains("sz-cr-wo"));
        Assert.AreEqual("Starter collection contract", row.Q<Label>(className: "sz-cr-wo-text").text);
        Assert.NotNull(row.Q<Label>(className: "sz-cr-wo-status"));
    }
}

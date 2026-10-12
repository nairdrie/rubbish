using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>The words on the contract cards (Docs/Campaign.md §14.8): they say the real numbers, in the right number of cards.</summary>
public class ContractCardsTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Contract(string districtId) => R("Campaign").GetMethod("ContractFor", Static).Invoke(null, new object[] { districtId });
    static string[] Words(object contract, bool first) =>
        (string[])R("ContractIntroScreen").GetMethod("Words", Static).Invoke(null, new[] { contract, first });

    [Test]
    public void TheFirstContractGetsThreeCardsAndTheNumbersAreTheRealOnes()
    {
        var words = Words(Contract("mildly_pleasant"), true);
        Assert.AreEqual(3, words.Length);
        StringAssert.Contains("MILDLY PLEASANT", words[0].ToUpperInvariant());
        StringAssert.Contains("Minivan Meadows", words[0], "what it earns");
        StringAssert.Contains("56", words[1], "the old bags");
        StringAssert.Contains("90%", words[2]);
        StringAssert.Contains("24h 00m", words[2], "the hold, in the countdown's own format");
    }

    [Test]
    public void ALaterContractGetsOneCardAndTheMeadowsSayTheyAreBusier()
    {
        var acres = Words(Contract("mildly_pleasant"), false);
        var meadows = Words(Contract("minivan_meadows"), false);
        Assert.AreEqual(1, acres.Length);
        Assert.AreEqual(1, meadows.Length);
        StringAssert.DoesNotContain("more often", acres[0], "the Acres are no busier than before");
        StringAssert.Contains("50% more often", meadows[0]);
        StringAssert.Contains("Coupon Commons", meadows[0]);
        StringAssert.Contains("48", meadows[0], "its own old bags");
    }

    [Test]
    public void NoCardIsEmptyOrLeftWithABraceFromAFormat()
    {
        foreach (string id in new[] { "mildly_pleasant", "minivan_meadows" })
            foreach (bool first in new[] { true, false })
                foreach (string w in Words(Contract(id), first))
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(w));
                    StringAssert.DoesNotContain("{", w);
                    StringAssert.DoesNotContain("}", w);
                }
    }
}

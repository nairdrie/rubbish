using NUnit.Framework;

public class EbaeTradingRulesTests
{
    [TestCase("$0.50", 50L)]
    [TestCase("50,000.00", 5_000_000L)]
    [TestCase("$12.34", 1234L)]
    public void DollarInputAcceptsInclusiveBoundsAndExactCents(string input, long expected)
    {
        Assert.IsTrue(EbaeTradingRules.TryParsePrice(input, out long cents, out string error), error);
        Assert.AreEqual(expected, cents);
    }

    [TestCase("0.49", "minimum")]
    [TestCase("50000.01", "maximum")]
    [TestCase("12.345", "decimal")]
    [TestCase("1e9", "valid")]
    [TestCase("not money", "valid")]
    public void DollarInputRejectsOutOfRangeAndUnrepresentablePrices(string input, string reason)
    {
        Assert.IsFalse(EbaeTradingRules.TryParsePrice(input, out _, out string error));
        StringAssert.Contains(reason, error);
    }

    [Test]
    public void PresetsRejectNonfiniteAndOverCapPrices()
    {
        Assert.IsTrue(EbaeTradingRules.TryPriceCents(0.5f, out long minimum, out _));
        Assert.AreEqual(50L, minimum);
        Assert.IsTrue(EbaeTradingRules.TryPriceCents(50000f, out long maximum, out _));
        Assert.AreEqual(5_000_000L, maximum);
        Assert.IsFalse(EbaeTradingRules.TryPriceCents(50000.01f, out _, out _));
        Assert.IsFalse(EbaeTradingRules.TryPriceCents(float.NaN, out _, out _));
        Assert.IsFalse(EbaeTradingRules.TryPriceCents(float.PositiveInfinity, out _, out _));
    }

    [Test]
    public void NeighbourCashOffersAllowZeroAndSingleCentsButNotOverCap()
    {
        Assert.IsTrue(EbaeTradingRules.TryParseOfferCash("0", out long zero, out _));
        Assert.AreEqual(0L, zero);
        Assert.IsTrue(EbaeTradingRules.TryParseOfferCash("0.01", out long oneCent, out _));
        Assert.AreEqual(1L, oneCent);
        Assert.IsFalse(EbaeTradingRules.TryParseOfferCash("-0.01", out _, out _));
        Assert.IsFalse(EbaeTradingRules.TryParseOfferCash("50000.01", out _, out _));
    }

    [Test]
    public void TwelfthListingIsAllowedButThirteenthIsNot()
    {
        Assert.IsTrue(EbaeTradingRules.CanCreateListing(11));
        Assert.IsFalse(EbaeTradingRules.CanCreateListing(12));
        Assert.IsFalse(EbaeTradingRules.CanCreateListing(20)); // old saves are retained but cannot grow
        StringAssert.Contains("Delist", EbaeTradingRules.CapacityError(12));
    }

    [Test]
    public void PublicationStateCarriesRejectionReason()
    {
        var status = new EbaePublicationStatus(EbaePublicationState.Rejected, "too many listings");
        Assert.AreEqual(EbaePublicationState.Rejected, status.State);
        Assert.AreEqual("too many listings", status.Reason);
    }
}

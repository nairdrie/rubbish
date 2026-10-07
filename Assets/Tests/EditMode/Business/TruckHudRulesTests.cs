using NUnit.Framework;

public class TruckHudRulesTests
{
    [TestCase(false, false, false, false, TruckHudSource.Own)]
    [TestCase(false, false, false, true, TruckHudSource.Employer)]
    [TestCase(false, false, true, false, TruckHudSource.Depot)]
    [TestCase(false, false, true, true, TruckHudSource.Depot)]
    [TestCase(false, true, false, false, TruckHudSource.Riding)]
    [TestCase(false, true, true, true, TruckHudSource.Riding)]
    [TestCase(true, false, false, false, TruckHudSource.Driving)]
    [TestCase(true, true, true, true, TruckHudSource.Driving)]
    public void SelectUsesPhysicalContextBeforeJob(bool driving, bool riding, bool workingDepot, bool employed, TruckHudSource expected)
    {
        Assert.AreEqual(expected, TruckHudRules.Select(driving, riding, workingDepot, employed));
    }

    [Test]
    public void SelectCoversEveryCombinationOfTheFourContexts()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            bool driving = (mask & 1) != 0;
            bool riding = (mask & 2) != 0;
            bool depot = (mask & 4) != 0;
            bool employed = (mask & 8) != 0;
            var expected = driving ? TruckHudSource.Driving
                : riding ? TruckHudSource.Riding
                : depot ? TruckHudSource.Depot
                : employed ? TruckHudSource.Employer
                : TruckHudSource.Own;
            Assert.AreEqual(expected, TruckHudRules.Select(driving, riding, depot, employed), $"mask {mask}");
        }
    }

    [Test]
    public void ReturnToDepotUsesStrictFreeCapacityBoundary()
    {
        Assert.IsTrue(TruckHudRules.ShouldReturnToDepot(true, 95.51f, 100f, false));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 95.5f, 100f, false));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 0f, 100f, false));
    }

    [Test]
    public void ReturnToDepotWhenCleanOnlyWithMeaningfulLoad()
    {
        Assert.IsTrue(TruckHudRules.ShouldReturnToDepot(true, 0.51f, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 0.5f, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 0.51f, 100f, false));
    }

    [Test]
    public void UnknownOrInvalidCapacityNeverRequestsDepot()
    {
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(false, 99f, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 99f, 0f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 99f, -1f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 99f, float.NaN, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, 99f, float.PositiveInfinity, true));
    }

    [Test]
    public void NonfiniteOrNegativeLoadNeverRequestsDepot()
    {
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, -1f, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, float.NaN, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, float.PositiveInfinity, 100f, true));
        Assert.IsFalse(TruckHudRules.ShouldReturnToDepot(true, float.NegativeInfinity, 100f, true));
    }
}

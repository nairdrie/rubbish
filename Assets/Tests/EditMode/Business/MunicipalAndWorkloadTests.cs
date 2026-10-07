using System;
using NUnit.Framework;

public class MunicipalAndWorkloadTests
{
    // Runtime assembly is referenced reflectively by the project's EditMode test assembly.
    static object Call(string type, string method, params object[] args) =>
        Type.GetType(type + ", Assembly-CSharp", true).GetMethod(method).Invoke(null, args);
    static class MunicipalEconomy
    {
        public static long Share(long total, int count, int i) => (long)Call("MunicipalEconomy", "Share", total, count, i);
        public static long FineShare(long pool, int count, int i, float hours, long charged, int tier) =>
            (long)Call("MunicipalEconomy", "FineShare", pool, count, i, hours, charged, tier);
        public static float SanitizeHours(float hours) => (float)Call("MunicipalEconomy", "SanitizeHours", hours);
    }
    static class TownWorkDirector
    {
        public static float TargetScale(int active, int collected, float kg, int curb) =>
            (float)Call("TownWorkDirector", "TargetScale", active, collected, kg, curb);
        public static float ForPlayers(int active, int collected, float kg, int curb, int players) =>
            (float)Call("TownWorkDirector", "TargetScaleForPlayers", active, collected, kg, curb, players);
        public static float Approach(float current, float target, float hours) =>
            (float)Call("TownWorkDirector", "Approach", current, target, hours);
        public static int ScaledDropCount(int bags, float scale, float roll) =>
            (int)Call("TownWorkDirector", "ScaledDropCount", bags, scale, roll);
    }
    [Test]
    public void SplittingTownPoolConservesEveryCent()
    {
        for (int count = 1; count <= 4; count++)
        for (long pool = 0; pool <= 100; pool++)
        {
            long sum = 0;
            for (int i = 0; i < count; i++) sum += MunicipalEconomy.Share(pool, count, i);
            Assert.That(sum, Is.EqualTo(pool));
        }
    }

    [Test]
    public void StartupGraceAndRookieCapSurviveLargeBills()
    {
        Assert.That(MunicipalEconomy.FineShare(100000, 2, 0, 47.99f, 0, 4), Is.Zero);
        Assert.That(MunicipalEconomy.FineShare(800, 2, 0, 48f, 0, 1), Is.EqualTo(200));
        Assert.That(MunicipalEconomy.FineShare(100000, 2, 0, 60f, 800, 4), Is.EqualTo(200));
        Assert.That(MunicipalEconomy.FineShare(100000, 2, 0, 60f, 1000, 4), Is.Zero);
        Assert.That(MunicipalEconomy.FineShare(100000, 2, 0, 120f, 0, 1), Is.EqualTo(2500));
    }

    [Test]
    public void WaivedShareIsNotBilledToTheOtherBusiness()
    {
        Assert.That(MunicipalEconomy.FineShare(1000, 2, 0, 0f, 0, 1), Is.Zero);
        Assert.That(MunicipalEconomy.FineShare(1000, 2, 1, 120f, 0, 1), Is.EqualTo(500));
    }

    [Test]
    public void InvalidAgeCannotEvadeOrPoisonAccounting()
    {
        Assert.That(MunicipalEconomy.SanitizeHours(float.NaN), Is.Zero);
        Assert.That(MunicipalEconomy.SanitizeHours(float.PositiveInfinity), Is.Zero);
        Assert.That(MunicipalEconomy.SanitizeHours(-1f), Is.Zero);
        Assert.That(MunicipalEconomy.SanitizeHours(900f), Is.EqualTo(120f));
    }

    [Test]
    public void WorkingCrewsAndCollectionPaceIncreaseSupply()
    {
        Assert.That(TownWorkDirector.TargetScale(2, 6, 0, 8), Is.GreaterThan(TownWorkDirector.TargetScale(1, 3, 0, 8)));
        Assert.That(TownWorkDirector.TargetScale(1, 3, 0, 8), Is.GreaterThan(TownWorkDirector.TargetScale(1, 0, 0, 8)));
    }

    [Test]
    public void DepotAndStreetBacklogsEaseSupplyWithoutStarvingEmptyTown()
    {
        float active = TownWorkDirector.TargetScale(1, 3, 0, 8);
        Assert.That(TownWorkDirector.TargetScale(1, 3, 280, 8), Is.LessThan(active));
        Assert.That(TownWorkDirector.TargetScale(1, 3, 0, 50), Is.LessThan(active));
        Assert.That(TownWorkDirector.TargetScale(0, 0, 0, 0), Is.GreaterThanOrEqualTo(0.8f));
        Assert.That(TownWorkDirector.TargetScale(0, 0, 500, 80), Is.GreaterThanOrEqualTo(0.15f));
    }

    [Test]
    public void RateRampsGraduallyAndDoesNotOvershoot()
    {
        float moved = TownWorkDirector.Approach(1.2f, 0.2f, 0.1f);
        Assert.That(moved, Is.InRange(1f, 1.2f));
        Assert.That(TownWorkDirector.Approach(0.2f, 1.2f, 0.1f), Is.InRange(0.2f, 0.4f));
        Assert.That(TownWorkDirector.Approach(1f, 2f, 0), Is.EqualTo(1f));
    }

    [Test]
    public void ButlerDropsAlsoFollowWorkloadBudget()
    {
        Assert.That(TownWorkDirector.ScaledDropCount(2, 0.25f, 0.75f), Is.Zero);
        Assert.That(TownWorkDirector.ScaledDropCount(2, 0.25f, 0.1f), Is.EqualTo(1));
        Assert.That(TownWorkDirector.ScaledDropCount(2, 2f, 0.5f), Is.EqualTo(4));
    }

    [TestCase(0f, 0)]
    [TestCase(80f, 0)]
    [TestCase(280f, 8)]
    [TestCase(0f, 50)]
    public void ActiveSoloGetsOnlyFifteenPercentMorePickupWork(float kg, int curb)
    {
        float prior = TownWorkDirector.TargetScale(1, 3, kg, curb);
        Assert.That(TownWorkDirector.ForPlayers(1, 3, kg, curb, 1), Is.EqualTo(prior * 1.15f).Within(0.00001f));
    }

    [TestCase(0, 1)] // idle solo
    [TestCase(1, 2)] // one active coworker/crew, another player idle
    [TestCase(2, 2)]
    [TestCase(1, 4)] // three inactive competitors must not inflate the remaining crew's supply
    [TestCase(4, 4)]
    [TestCase(0, 0)]
    public void IdleAndMultiplayerSupplyBudgetsAreUnchanged(int active, int players)
    {
        foreach (float kg in new[] { 0f, 80f, 500f })
            Assert.That(TownWorkDirector.ForPlayers(active, 3, kg, 8, players),
                Is.EqualTo(TownWorkDirector.TargetScale(active, 3, kg, 8)));
    }

    [Test]
    public void SoloBonusPreservesBacklogPressureAndGradualRamp()
    {
        float empty = TownWorkDirector.ForPlayers(1, 3, 0f, 0, 1);
        float halfTruck = TownWorkDirector.ForPlayers(1, 3, 80f, 0, 1);
        float fullShop = TownWorkDirector.ForPlayers(1, 3, 320f, 0, 1);
        Assert.That(empty, Is.EqualTo(1.38f).Within(0.00001f));
        Assert.That(halfTruck, Is.EqualTo(0.8832f).Within(0.00001f));
        Assert.That(fullShop, Is.LessThan(halfTruck));
        Assert.That(TownWorkDirector.Approach(0.75f, empty, 0.1f), Is.InRange(0.75f, 0.80f));
    }

    [TestCase(1, 9)]
    [TestCase(2, 8)]
    [TestCase(3, 8)]
    [TestCase(4, 8)]
    public void SoloOpeningStockAddsOneBagWithoutChangingCoop(int players, int expected)
    {
        Assert.That((int)Call("CurbTrashSpawner", "StartingBagsForPlayers", players), Is.EqualTo(expected));
    }
}

using NUnit.Framework;

public class PunchComboRulesTests
{
    [Test]
    public void ThirdQuickHitOnSamePlayerKnocksDown()
    {
        byte hits = PunchComboRules.NextHit(0, 0, 0f, 2, 10f, 2.8f);
        Assert.AreEqual(1, hits);
        hits = PunchComboRules.NextHit(2, hits, 10f, 2, 10.6f, 2.8f);
        Assert.AreEqual(2, hits);
        hits = PunchComboRules.NextHit(2, hits, 10.6f, 2, 11.2f, 2.8f);
        Assert.AreEqual(PunchComboRules.KnockdownHit, hits);
    }

    [Test]
    public void SwitchingTargetOrWaitingBreaksCombo()
    {
        Assert.AreEqual(1, PunchComboRules.NextHit(2, 2, 10f, 3, 10.5f, 2.8f));
        Assert.AreEqual(1, PunchComboRules.NextHit(2, 2, 10f, 2, 12.9f, 2.8f));
        Assert.AreEqual(1, PunchComboRules.NextHit(2, 2, 10f, 2, 9f, 2.8f));
    }

    [Test]
    public void DefaultRecordAndExcessHitsAreSafe()
    {
        Assert.AreEqual(1, PunchComboRules.NextHit(0, 0, 0f, 0, 0.5f, 2.8f));
        Assert.AreEqual(PunchComboRules.KnockdownHit,
            PunchComboRules.NextHit(2, PunchComboRules.KnockdownHit, 10f, 2, 10.5f, 2.8f));
    }
}

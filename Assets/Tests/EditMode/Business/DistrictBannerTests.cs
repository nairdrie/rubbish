using System;
using NUnit.Framework;

/// <summary>When the "now entering" banner fires (DistrictBannerState, World/DistrictBanner.cs).</summary>
public class DistrictBannerTests
{
    static readonly Type StateType = Type.GetType("DistrictBannerState, Assembly-CSharp", true);
    object state;

    [SetUp] public void SetUp() => state = Activator.CreateInstance(StateType);

    string Step(string district, float dt)
    {
        var args = new object[] { district, dt };
        var result = (string)StateType.GetMethod("Step").Invoke(state, args);
        return result;
    }

    string Stay(string district, float seconds)
    {
        string fired = null;
        for (float t = 0f; t < seconds; t += 0.25f) fired = Step(district, 0.25f) ?? fired;
        return fired;
    }

    [Test]
    public void WhereYouStartIsNotAnnounced()
    {
        Assert.IsNull(Stay("mildly_pleasant", 20f));
    }

    [Test]
    public void CrossingIntoAnotherDistrictAnnouncesItOnceAfterAMoment()
    {
        Stay("mildly_pleasant", 10f);
        Assert.IsNull(Step("coupon_commons", 0.25f), "not on the first step over the line");
        Assert.AreEqual("coupon_commons", Stay("coupon_commons", 3f));
        Assert.IsNull(Stay("coupon_commons", 30f), "and not again while you stay");
    }

    [Test]
    public void DrivingAlongABorderDoesNotFlicker()
    {
        Stay("mildly_pleasant", 10f);
        string fired = null;
        for (int i = 0; i < 40; i++) fired = Step(i % 2 == 0 ? "minivan_meadows" : "mildly_pleasant", 0.25f) ?? fired;
        Assert.IsNull(fired);
    }

    [Test]
    public void BannersAreSpacedOutAndGoingIndoorsResetsNothing()
    {
        Stay("mildly_pleasant", 10f);
        Assert.AreEqual("coupon_commons", Stay("coupon_commons", 3f));
        Assert.IsNull(Stay("mildly_pleasant", 2f), "too soon after the last banner");
        Assert.AreEqual("mildly_pleasant", Stay("mildly_pleasant", 6f));
        Assert.IsNull(Stay(null, 30f), "indoors");
        Assert.IsNull(Stay("mildly_pleasant", 5f), "back out where you were");
    }
}

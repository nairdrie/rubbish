using NUnit.Framework;

public class FeedbackAudienceTests
{
    [Test]
    public void MultiplayerFullRevealBelongsOnlyToFinder()
    {
        Assert.IsTrue(FeedbackAudience.FullFindForLocal(true, 3UL, 3UL));
        Assert.IsFalse(FeedbackAudience.FullFindForLocal(true, 3UL, 4UL));
        Assert.IsFalse(FeedbackAudience.FullFindForLocal(true, FeedbackAudience.UnknownFinder, 3UL));
        Assert.IsTrue(FeedbackAudience.FullFindForLocal(false, FeedbackAudience.UnknownFinder, 0UL));
    }

    [Test]
    public void SharedEffectsStayWithinViewerRadius()
    {
        Assert.IsTrue(FeedbackAudience.Nearby(784f, 28f));
        Assert.IsFalse(FeedbackAudience.Nearby(784.01f, 28f));
        Assert.IsFalse(FeedbackAudience.Nearby(-1f, 28f));
        Assert.IsFalse(FeedbackAudience.Nearby(float.NaN, 28f));
        Assert.IsFalse(FeedbackAudience.Nearby(0f, 0f));
    }
}

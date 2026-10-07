/// <summary>Pure audience decisions for personal HUD rewards versus shared-world activity.</summary>
public static class FeedbackAudience
{
    public const ulong UnknownFinder = ulong.MaxValue;

    public static bool FullFindForLocal(bool multiplayer, ulong finderClientId, ulong localClientId) =>
        !multiplayer || finderClientId != UnknownFinder && finderClientId == localClientId;

    public static bool Nearby(float squaredDistance, float radius) =>
        squaredDistance >= 0f && radius > 0f && squaredDistance <= radius * radius;
}

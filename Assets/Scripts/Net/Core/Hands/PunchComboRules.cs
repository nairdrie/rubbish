/// <summary>Pure, host-side hit-counter rule for the bare-fist punch combo.</summary>
public static class PunchComboRules
{
    public const byte KnockdownHit = 3;

    /// <summary>Count a landed punch. A new target, expired window or clock reset starts at one.</summary>
    public static byte NextHit(ulong previousTarget, byte previousHits, float previousAt,
                               ulong target, float now, float window)
    {
        if (previousHits == 0 || previousTarget != target || now < previousAt || now - previousAt > window)
            return 1;
        return previousHits >= KnockdownHit ? KnockdownHit : (byte)(previousHits + 1);
    }
}

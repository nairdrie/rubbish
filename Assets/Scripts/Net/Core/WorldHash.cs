/// <summary>
/// FNV-1a 64-bit, for cross-machine state hashes (bots compare the host's and guests' hashes at matched versions).
/// Deterministic on every platform: no string.GetHashCode, no culture, no floats (hash cents and ids, not floats).
/// Usage: <c>ulong h = WorldHash.Seed; h = WorldHash.Fnv(h, cashCents); h = WorldHash.Fnv(h, bagId);</c>
/// </summary>
public static class WorldHash
{
    public const ulong Seed = 1469598103934665603UL;
    const ulong Prime = 1099511628211UL;

    /// <summary>Mix the 8 little-endian bytes of <paramref name="v"/>.</summary>
    public static ulong Fnv(ulong h, long v)
    {
        ulong u = unchecked((ulong)v);
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(u >> (i * 8));
            h = unchecked(h * Prime);
        }
        return h;
    }

    /// <summary>Mix a string (each UTF-16 unit as two bytes, then a terminator so "ab"+"c" != "a"+"bc"). Null differs from "".</summary>
    public static ulong Fnv(ulong h, string s)
    {
        if (s == null) return Fnv(h, unchecked((long)0xDEADBEEFCAFEF00DUL));
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            h ^= (byte)c;
            h = unchecked(h * Prime);
            h ^= (byte)(c >> 8);
            h = unchecked(h * Prime);
        }
        h ^= 0xFF;
        return unchecked(h * Prime);
    }
}

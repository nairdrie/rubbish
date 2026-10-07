using UnityEngine;

/// <summary>
/// Package F4's dice (Docs/Multiplayer.md §14.4, §9 rule 2). Gameplay rolls (heists, latches, stowaways, infection, the
/// Raccoon King) run on the host only, from <see cref="Host"/>; guests never roll gameplay. <see cref="Cosmetic"/> is for
/// looks only (hiccup timing, sparkle jitter) and may differ between machines. xorshift32, seeded from the clock.
/// </summary>
public sealed class NightDice
{
    uint s;

    public NightDice(uint seed) { s = seed == 0 ? 0x9E3779B9u : seed; }

    /// <summary>Host gameplay rolls (solo: this machine).</summary>
    public static readonly NightDice Host = new NightDice((uint)System.DateTime.UtcNow.Ticks);
    /// <summary>Looks only; never decides anything another machine must agree on.</summary>
    public static readonly NightDice Cosmetic = new NightDice((uint)System.DateTime.UtcNow.Ticks ^ 0xA5A5A5A5u);

    public uint Next()
    {
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        return s;
    }

    /// <summary>[0, 1).</summary>
    public float Value => (Next() & 0xFFFFFF) / 16777216f;

    public float Range(float min, float max) => min + (max - min) * Value;

    /// <summary>[min, max).</summary>
    public int Range(int min, int max) => max <= min ? min : min + (int)(Next() % (uint)(max - min));

    public bool Chance(float p) => Value < p;

    public Vector3 OnCircle(float radius)
    {
        float a = Value * Mathf.PI * 2f;
        return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
    }

    public T Pick<T>(T[] a) => a == null || a.Length == 0 ? default : a[Range(0, a.Length)];
}

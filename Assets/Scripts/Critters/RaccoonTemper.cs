using UnityEngine;

/// <summary>Shared, testable encounter limits. Rabies is a connected attack outcome, never an aura around a truck.</summary>
public static class RaccoonTemper
{
    public const float AngerSeconds = 18f, ChaseRange = 22f, BiteCooldown = 5.5f;
    public const float WindupSeconds = 0.4f, StartleCooldown = 8f;
    public const int BitesPerEncounter = 2;
    public static float InfectionChance(bool rabid, bool king = false) => king || rabid ? 0.8f : 0.6f;
    public static float LatchChance(bool rabid) => rabid ? 0.4f : 0.3f;
    public static bool CanRetaliate(bool onFoot, float distance, bool repelled, int bites) =>
        onFoot && distance >= 0f && distance <= ChaseRange && !repelled && bites < BitesPerEncounter;
    public static bool CanConnect(bool onFoot, bool immune, float distance, float heightDifference, float range) =>
        onFoot && !immune && distance >= 0f && distance <= range && Mathf.Abs(heightDifference) <= 2.4f;

    /// <summary>Closest actual source of a horn/stomp, including its driver; do not blame an unrelated nearby walker.</summary>
    public static ulong InstigatorNear(Vector3 source)
    {
        ulong client = ulong.MaxValue;
        float closest = 5f;
        foreach (var pc in NightPlayers.All)
        {
            if (pc == null || !pc.isActiveAndEnabled || NightPlayers.IsIndoors(pc)) continue;
            var t = NightPlayers.Describe(pc, source);
            if (t.dist >= closest) continue;
            closest = t.dist;
            client = t.client;
        }
        return client;
    }
}

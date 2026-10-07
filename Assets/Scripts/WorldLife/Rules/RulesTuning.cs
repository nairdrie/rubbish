/// <summary>
/// WorldLife package E (Docs/WorldLife.md §8, §9): the rules' own knobs. The shared numbers (thresholds, fines, the horn
/// radius, immunities) stay in <see cref="WorldLifeTuning"/>; these are the ones only E reads. Package G tunes both.
/// Values the host uses to decide are const so every machine agrees; presentation-only ones are static.
/// </summary>
public static class RulesTuning
{
    // ------------------------------------------------------------------ hits (§8.1 – §8.3)

    /// <summary>A flattened NPC can't be flattened again for this long (the flop, chase and rejoin plus
    /// <see cref="WorldLifeTuning.Hits.ImmuneAfterRejoinSeconds"/>: "~12 s in total", §8.1). A re-hit inside it is a
    /// cartoon dodge with no consequences.</summary>
    public const float FlattenImmuneSeconds = 12f;
    /// <summary>A dog near miss costs Rep at most once per dog per this long (the dog always leaps).</summary>
    public const float DogNearMissCooldownSeconds = 20f;
    /// <summary>The sensor claims a body again only after this many fixed steps (the decided act makes it unhittable anyway).</summary>
    public const int ReclaimGuardTicks = 8;
    /// <summary>NpcOverride hold after a flatten / shunt: lie + chase + rejoin (fixed ticks are derived from it).</summary>
    public const float OverrideHoldSeconds = FlattenImmuneSeconds;

    // ------------------------------------------------------------------ the horn (§8.5)

    /// <summary>A car counts as "ahead in the same lane" when it's in front of the truck, within this lateral offset of the
    /// truck's centre line and heading the same way (dot of the forwards ≥ <see cref="SameLaneHeadingDot"/>).</summary>
    public const float SameLaneHalfWidth = 2.6f, SameLaneHeadingDot = 0.5f;
    /// <summary>Night shouts over a door ("SOME OF US ARE SLEEPING!") at most this often (presentation).</summary>
    public static float NoiseShoutInterval = 2f;

    // ------------------------------------------------------------------ player contacts (§8.4)

    /// <summary>A player on foot "bumps" a person when their feet come this close (XZ, centre to centre).</summary>
    public const float BumpDistance = 0.8f;
    /// <summary>Slower than this a bump is just a "pardon me" (walking); sprinting at ≥ PlayerBumpStumbleSpeed stumbles them.</summary>
    public const float BumpMinSpeed = 0.6f;
    /// <summary>A thrown thing hits a person when its centre comes within their half width + this.</summary>
    public const float ThrownHitPad = 0.22f;
    /// <summary>The short flop of a ≥ 11 m/s throw: back along the throw and up (m/s).</summary>
    public const float ShortFlopBack = 2.5f, ShortFlopUp = 4.2f;
    /// <summary>A thrown bag that bonks a head bounces off with this fraction of its speed (plus a little up), so it never
    /// bursts on an NPC (§8.4, like TrashBag's IsPlayer guard).</summary>
    public const float ThrownBounce = 0.22f, ThrownBounceUp = 1.6f;
    /// <summary>Thrown things tracked at once (the thrower's machine; 0 B per frame).</summary>
    public const int MaxThrownTracked = 8;

    // ------------------------------------------------------------------ grudges (§9.1)

    /// <summary>A grudge never lasts longer than this (a stamp further ahead is from a session whose clock started
    /// elsewhere: it's stale and ignored).</summary>
    public const float GrudgeMaxHours = 24f;
    /// <summary>A grudged household glares at a truck within this distance (and doesn't wave), checked 4× a second.</summary>
    public static float GlareRadius = 18f, GlareInterval = 0.25f;
}

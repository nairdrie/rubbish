using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §8, §9, §10.3): the host's decision service. It replaces W0's no-op authority
/// the moment it boots, which switches C's and D's interim sensors off.
///
/// <para><b>Decide / present (rule 5).</b> Every claim goes <see cref="Decide"/> (pure rules + the host's memory) →
/// <see cref="Present"/> (<see cref="ILifeBody.PlayHit"/> + <see cref="WorldLifeEvents.HitDecided"/>, on every machine) →
/// <see cref="Commit"/> (host only: the timetable override and <see cref="NeighborConsequences"/>). Package F forwards a
/// guest's claims into the same calls and broadcasts the decided hit; until then a guest driver's hits are local cartoons
/// with no consequences (§10.3).</para>
///
/// <para><b>Rules.</b> A person in the truck's sweep: &lt; 0.8 m/s nothing, 0.8–2.5 a nudge, ≥ 2.5 flattened (the §8.1
/// launch). A flattened NPC is immune for 12 s, and the same driver can't flatten them again for 20 s: a re-hit inside
/// either is a cartoon "missed me!" dodge, free. Dogs always leap clear (a real near miss, ≥ 2.5 m/s, costs Rep −1 once per
/// 20 s). Cars shunt at ≥ 2.5 m/s, then stay immune for 10 s (below it a car is a wall). Thrown things boop / stumble /
/// short-flop people (Rep −1 for the flop, per thrower and NPC once every 4 s). A player sprinting into someone stumbles
/// them (1 s per NPC). Props knocked over go on the City Works tally; a hand-over earns the neighbourly Rep.</para>
/// </summary>
public sealed class WorldLifeAuthority : IWorldLifeAuthority
{
    public static readonly WorldLifeAuthority Instance = new WorldLifeAuthority();

    /// <summary>F: the player slot driving a truck (0 = host / solo until F maps trucks to seats).</summary>
    public static Func<TruckControllerV2, byte> ActorSlotOf;
    public static byte SlotOf(TruckControllerV2 t)
    {
        var f = ActorSlotOf;
        if (f == null || t == null) return 0;
        try { return f(t); } catch (Exception e) { Debug.LogException(e); return 0; }
    }

    // ------------------------------------------------------------------ the host's memory (0 B per frame after warm-up)

    readonly Dictionary<uint, double> immuneUntil = new Dictionary<uint, double>(64);    // target → real seconds
    readonly Dictionary<ulong, double> pairUntil = new Dictionary<ulong, double>(64);    // (actor | kind tag, target) → real seconds
    readonly List<uint> pruneIds = new List<uint>(32);
    readonly List<ulong> prunePairs = new List<ulong>(32);
    double nextPrune;

    const ulong TagDriver = 0, TagDog = 1, TagThrow = 2, TagBump = 3;
    static ulong PairKey(ulong tag, byte actor, LifeId target) => (tag << 40) | ((ulong)actor << 32) | target.Packed;

    // ------------------------------------------------------------------ stats

    public int Claims { get; private set; }
    public int Rejected { get; private set; }
    public int Flattens { get; private set; }
    public int Nudges { get; private set; }
    public int Dodges { get; private set; }
    public int DogLeaps { get; private set; }
    public int Shunts { get; private set; }
    public int Startles { get; private set; }
    public int PullOvers { get; private set; }
    public int HonkBacks { get; private set; }
    public int Bumps { get; private set; }
    public int ThrownHits { get; private set; }
    public int Knocks { get; private set; }
    public int HandOvers { get; private set; }
    public int NoiseComplaints { get; private set; }

    public void ResetStats()
    {
        Claims = Rejected = Flattens = Nudges = Dodges = DogLeaps = Shunts = Startles = PullOvers = HonkBacks = Bumps = ThrownHits = 0;
        Knocks = HandOvers = NoiseComplaints = 0;
    }

    /// <summary>Forget every immunity (new session, tests).</summary>
    public void Forget() { immuneUntil.Clear(); pairUntil.Clear(); }

    public string Describe() =>
        $"claims {Claims} (rejected {Rejected}): flattens {Flattens}, nudges {Nudges}, dodges {Dodges}, dog leaps {DogLeaps}, shunts {Shunts}; " +
        $"horn: startles {Startles}, pull-overs {PullOvers}, honk-backs {HonkBacks}, noise complaints {NoiseComplaints}; bumps {Bumps}, thrown hits {ThrownHits}; " +
        $"knocks {Knocks}, hand-overs {HandOvers}; memory {immuneUntil.Count}+{pairUntil.Count}";

    // ================================================================== truck vs NPC / dog / car (§8.1 – §8.3)

    public NpcHit ClaimNpcHit(in NpcHitClaim claim) => ClaimNpcHit(in claim, null);

    /// <summary>As <see cref="ClaimNpcHit(in NpcHitClaim)"/> for a body this machine actually saw (its own sensor): decide
    /// on that body even if another one is registered under the same id for a moment (D's car leaving or reaching its
    /// driveway). F's network claims carry only the id and come through the other overload.</summary>
    public NpcHit ClaimNpcHit(in NpcHitClaim claim, ILifeBody seen)
    {
        Claims++;
        var body = seen != null && seen.Id == claim.target ? seen : LifeBodies.Find(claim.target);
        var books = RulesBooks.Active;
        if (!Decide(in claim, body, books, out var hit, out bool consequential)) { Rejected++; return default; }
        Present(body, in hit);
        Commit(in hit, body, consequential, books);
        return hit;
    }

    /// <summary>
    /// The rules (§8.1 – §8.3) for one claim. Writes only the authority's own immunity memory. False = nothing happens
    /// (too slow, not hittable, an immune car). <paramref name="consequential"/> = book it (fine / Rep / grudge / letter).
    /// </summary>
    public bool Decide(in NpcHitClaim claim, ILifeBody body, IRulesBooks books, out NpcHit hit, out bool consequential)
    {
        hit = default; consequential = false;
        if (body == null || !body.Hittable) return false;
        Vector3 v = claim.truckVelocity; v.y = 0f;
        float s = v.magnitude;
        if (float.IsNaN(s)) return false;
        double now = books.RealSeconds;
        Vector3 p = body.Position;
        Vector3 side = SideToward(claim.truck, p, v);
        uint id = claim.target.Packed;
        NpcHitKind kind;
        switch (body.BodyKind)
        {
            case LifeBodyKind.Dog:
            {
                if (s < WorldLifeTuning.Hits.NudgeSpeed) return false;
                kind = NpcHitKind.DogLeap;                                   // dogs always leap clear (§8.2)
                ulong k = PairKey(TagDog, 0, claim.target);
                if (s >= WorldLifeTuning.Hits.FlattenSpeed && !Cooling(k, now))
                {
                    consequential = true;                                    // a real near miss: the owner is furious, Rep −1
                    pairUntil[k] = now + RulesTuning.DogNearMissCooldownSeconds;
                }
                break;
            }
            case LifeBodyKind.Car:
            {
                if (s < WorldLifeTuning.Hits.ShuntSpeed) return false;       // below it the kinematic box is a wall
                if (Immune(id, now)) return false;
                kind = NpcHitKind.Shunt;
                consequential = true;
                immuneUntil[id] = now + WorldLifeTuning.Hits.CarImmunitySeconds;
                break;
            }
            default:
            {
                kind = WorldLifeTuning.Hits.PersonResult(s);
                if (kind == NpcHitKind.None) return false;
                if (kind == NpcHitKind.Flatten)
                {
                    ulong k = PairKey(TagDriver, claim.actorSlot, claim.target);
                    if (Immune(id, now) || Cooling(k, now)) kind = NpcHitKind.Dodge;   // "Ha! Missed me!", free
                    else
                    {
                        consequential = true;
                        immuneUntil[id] = now + RulesTuning.FlattenImmuneSeconds;
                        pairUntil[k] = now + WorldLifeTuning.Hits.SameDriverCooldownSeconds;
                    }
                }
                break;
            }
        }
        hit = new NpcHit
        {
            target = claim.target, kind = kind, atHours = books.Hours, tick = claim.tick, point = p,
            truckVelocity = claim.truckVelocity, actorSlot = claim.actorSlot, truck = claim.truck,
            seed = Seed(claim.target, claim.tick),
            launch = LaunchFor(kind, claim.truckVelocity, v, s, side),
            delayCs = (ushort)(kind == NpcHitKind.Flatten || kind == NpcHitKind.Shunt ? WorldLifeTuning.Hits.RejoinDelaySeconds * 100f : 0f),
        };
        return true;
    }

    /// <summary>Launch / push / slide for a decided kind (§8.1, §8.3).</summary>
    static Vector3 LaunchFor(NpcHitKind kind, Vector3 truckVel, Vector3 flat, float s, Vector3 side)
    {
        switch (kind)
        {
            case NpcHitKind.Flatten: return WorldLifeTuning.Hits.FlopLaunch(truckVel, side);
            case NpcHitKind.Nudge: return side * WorldLifeTuning.Hits.NudgeDistance;
            case NpcHitKind.Shunt: return (s > 0.01f ? flat / s : side) * Mathf.Min(WorldLifeTuning.Hits.ShuntSlideMax, s * 0.15f);
            default: return side;
        }
    }

    /// <summary>Show a decided hit on this machine: the body plays it and the fact goes out.</summary>
    public static void Present(ILifeBody body, in NpcHit hit)
    {
        if (body != null) { try { body.PlayHit(in hit); } catch (Exception e) { Debug.LogException(e); } }
        WorldLifeEvents.RaiseHitDecided(in hit);
    }

    /// <summary>Host: the timetable interruption (D draws the agent with the delay) and the consequences.</summary>
    public void Commit(in NpcHit hit, ILifeBody body, bool consequential, IRulesBooks books)
    {
        Count(hit.kind);
        if (!books.IsHost) return;   // a guest driver's hit is a local cartoon until package F (§10.3)
        if (hit.kind == NpcHitKind.Flatten || hit.kind == NpcHitKind.Shunt)
        {
            float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            LifeOverrides.Add(new NpcOverride
            {
                id = hit.target, day = RulesBooks.DayOf(books.Hours), delayCs = hit.delayCs, kind = hit.kind,
                untilTick = hit.tick + Mathf.CeilToInt(RulesTuning.OverrideHoldSeconds / dt), actorSlot = hit.actorSlot,
            });
        }
        if (consequential) NeighborConsequences.Apply(in hit, body, books);
    }

    void Count(NpcHitKind k)
    {
        switch (k)
        {
            case NpcHitKind.Flatten: Flattens++; break;
            case NpcHitKind.Nudge: Nudges++; break;
            case NpcHitKind.Dodge: Dodges++; break;
            case NpcHitKind.DogLeap: DogLeaps++; break;
            case NpcHitKind.Shunt: Shunts++; break;
        }
    }

    // ================================================================== the horn (§8.5)

    /// <summary>
    /// A truck honked. Presentation (every machine, deterministic per NPC from (id, honk tick)): people in range and ahead
    /// of the truck, or within 6 m in any direction, startle and step off its line; cars ahead in the same lane pull to
    /// the kerb for 3 s, other cars in range honk back; dogs bark (<see cref="HonkReactions"/>). Decision (host): a honk
    /// within 30 m of a house between 22:00 and 06:00 is a noise complaint (Rep −1, ≤ 3 a night per district, one letter
    /// in the morning).
    /// </summary>
    public void ClaimHorn(TruckControllerV2 truck, int tick, StreetGraph graph, HonkReactions reactions)
    {
        if (truck == null) return;
        if (reactions != null)
        {
            reactions.Scatter(truck, tick, out int startles, out int pullOvers, out int honkBacks);
            Startles += startles; PullOvers += pullOvers; HonkBacks += honkBacks;
        }
        var books = RulesBooks.Active;
        if (!books.IsHost) return;
        var rb = truck.Body;
        Vector3 at = rb != null ? rb.position : truck.transform.position;
        if (NightNoise.Check(books, graph, at)) NoiseComplaints++;
    }

    // ================================================================== player contacts (§8.4)

    /// <summary>
    /// A player on foot ran into a person (detected on that player's own machine, L). Walking: "pardon me" (the person's
    /// avoidance already sidesteps). Sprinting (≥ 4.5 m/s): a stumble 0.5 m back and a spilled mug. No fine, 1 s per NPC.
    /// Returns the decided hit (None = nothing to play).
    /// </summary>
    public NpcHit ClaimBump(ILifeBody body, Vector3 playerPos, Vector3 playerVel, byte actorSlot, int tick)
    {
        if (body == null || !body.Hittable || body.BodyKind != LifeBodyKind.Person) return default;
        var books = RulesBooks.Active;
        Vector3 v = playerVel; v.y = 0f;
        float s = v.magnitude;
        if (s < RulesTuning.BumpMinSpeed) return default;
        double now = books.RealSeconds;
        ulong k = PairKey(TagBump, 0, body.Id);
        if (Cooling(k, now)) return default;
        pairUntil[k] = now + WorldLifeTuning.Hits.PlayerBumpCooldownSeconds;
        Vector3 away = body.Position - playerPos; away.y = 0f;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : (s > 0.01f ? v / s : Vector3.forward);
        bool sprint = s >= WorldLifeTuning.Hits.PlayerBumpStumbleSpeed;
        var hit = new NpcHit
        {
            target = body.Id, kind = sprint ? NpcHitKind.Stumble : NpcHitKind.None, atHours = books.Hours, tick = tick,
            point = body.Position, truckVelocity = playerVel, actorSlot = actorSlot, seed = Seed(body.Id, tick),
            launch = away * WorldLifeTuning.Hits.PlayerBumpPush,
        };
        if (sprint) { Bumps++; Present(body, in hit); }
        return hit;
    }

    /// <summary>
    /// A thrown bag or item reached a person (the thrower's machine claims; the host books the Rep). &lt; 6 m/s a boop,
    /// 6–11 a stumble and "HEY!", ≥ 11 a short flop (1.0 s lie) and Rep −1. Once per thrower and NPC every 4 s. Bags never
    /// burst on NPCs.
    /// </summary>
    public NpcHit ClaimThrownHit(ILifeBody body, Vector3 itemVel, byte throwerSlot, int tick)
    {
        if (body == null || !body.Hittable || body.BodyKind != LifeBodyKind.Person) return default;
        var books = RulesBooks.Active;
        double now = books.RealSeconds;
        ulong k = PairKey(TagThrow, throwerSlot, body.Id);
        if (Cooling(k, now)) return default;
        pairUntil[k] = now + WorldLifeTuning.Hits.ThrownCooldownSeconds;
        float s = itemVel.magnitude;
        Vector3 flat = itemVel; flat.y = 0f;
        Vector3 dir = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        NpcHitKind kind = s >= WorldLifeTuning.Hits.ThrownFlopSpeed ? NpcHitKind.ShortFlop
                        : s >= WorldLifeTuning.Hits.ThrownStumbleSpeed ? NpcHitKind.Stumble : NpcHitKind.Boop;
        var hit = new NpcHit
        {
            target = body.Id, kind = kind, atHours = books.Hours, tick = tick, point = body.Position,
            truckVelocity = itemVel, actorSlot = throwerSlot, seed = Seed(body.Id, tick),
            launch = kind == NpcHitKind.ShortFlop ? dir * RulesTuning.ShortFlopBack + Vector3.up * RulesTuning.ShortFlopUp
                   : kind == NpcHitKind.Stumble ? dir * WorldLifeTuning.Hits.PlayerBumpPush : Vector3.zero,
        };
        ThrownHits++;
        Present(body, in hit);
        if (kind == NpcHitKind.ShortFlop && body.Id.kind != LifeKind.Crossing) Commit(in hit, body, true, books);
        return hit;
    }

    // ================================================================== props and hand-overs

    /// <summary>A prop the claimant already shows down: the City Works tally (host).</summary>
    public void ClaimKnock(in KnockClaim claim, in KnockFact fact)
    {
        Knocks++;
        CityWorksInvoice.Tally(in fact, RulesBooks.Active);
    }

    const string HandOverCounter = "wl_handovers";

    /// <summary>A resident handed their bag over: +1 Rep (neighborly; Progression caps it at 10 a day), host only.</summary>
    public void ClaimHandOver(in HandOverFact fact)
    {
        var books = RulesBooks.Active;
        if (!books.IsHost) return;
        HandOvers++;
        string did = !string.IsNullOrEmpty(fact.stopId) ? Districts.ForStopId(fact.stopId).id : Districts.AtPosition(fact.pos).id;
        books.Incident(new NeighborIncidentFact
        {
            kind = NeighborIncidentKind.Neighborly, stopId = fact.stopId ?? "", districtId = did, pos = fact.pos, escalation = 1f,
        });
        int n = RulesBooks.DailyAdd(books, HandOverCounter, RulesBooks.DayOf(books.Hours), 1);
        if (n <= RepValues.NeighborlyPerDay)
            GameHUD.FloatingText(fact.pos + Vector3.up * 2.4f, "Service with a smile!", new Color(0.55f, 1f, 0.6f), 0.95f);
    }

    // ================================================================== helpers

    bool Immune(uint id, double now) => immuneUntil.TryGetValue(id, out double u) && now < u;
    bool Cooling(ulong key, double now) => pairUntil.TryGetValue(key, out double u) && now < u;

    /// <summary>Seconds of immunity a target has left (tests).</summary>
    public float ImmuneLeft(LifeId id) => immuneUntil.TryGetValue(id.Packed, out double u) ? Mathf.Max(0f, (float)(u - RulesBooks.Active.RealSeconds)) : 0f;

    /// <summary>The seed byte of a decided reaction: deterministic per (Town seed, target, tick) on every machine.</summary>
    public static byte Seed(LifeId target, int tick)
    {
        var ctx = WorldLifeRunner.Context;
        return (byte)(LifeHash.U64(ctx != null ? ctx.TownSeed : 0UL, target, tick, 0xE5u) & 0xFF);
    }

    /// <summary>The truck's right or left (flat, unit) toward a point; from its velocity when there's no truck.</summary>
    public static Vector3 SideToward(TruckControllerV2 truck, Vector3 p, Vector3 flatVel)
    {
        Vector3 right, origin;
        if (truck != null)
        {
            var rb = truck.Body;
            Quaternion rot = rb != null ? rb.rotation : truck.transform.rotation;
            origin = rb != null ? rb.position : truck.transform.position;
            right = rot * Vector3.right;
        }
        else
        {
            right = flatVel.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, flatVel.normalized) : Vector3.right;
            origin = p - flatVel.normalized;
        }
        right.y = 0f;
        right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
        return Vector3.Dot(p - origin, right) >= 0f ? right : -right;
    }

    /// <summary>Drop expired memory (the Rules director calls this every few seconds; allocation-free).</summary>
    public void Prune(double now)
    {
        if (now < nextPrune) return;
        nextPrune = now + 5.0;
        pruneIds.Clear();
        foreach (var kv in immuneUntil) if (kv.Value <= now) pruneIds.Add(kv.Key);
        for (int i = 0; i < pruneIds.Count; i++) immuneUntil.Remove(pruneIds[i]);
        prunePairs.Clear();
        foreach (var kv in pairUntil) if (kv.Value <= now) prunePairs.Add(kv.Key);
        for (int i = 0; i < prunePairs.Count; i++) pairUntil.Remove(prunePairs[i]);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance.Forget();
        Instance.ResetStats();
        Instance.nextPrune = 0;
        ActorSlotOf = null;
    }
}

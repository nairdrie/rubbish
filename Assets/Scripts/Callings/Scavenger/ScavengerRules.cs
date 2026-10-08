using System;
using System.Collections.Generic;

// Scavenger calling, stage 3 (Docs/Progression.md "Callings: Scavenger"). This file is the ONE place its numbers live
// (ScavengerTuning) and every rule that can be decided from plain values (ScavengerRules). No UnityEngine, no game state:
// the host (ScavengerHost) feeds these with what it sees, and ScavengerTests pins them.

/// <summary>Ledger ids, counter keys and guard names the Scavenger calling owns.</summary>
public static class ScavengerIds
{
    // ---- Ledger (cash). Fines stay inside the neighbour family: a burglary fine is a neighbour's damages, with its own id.
    public const string LedgerBurglaryFine = "burglary_fine", LedgerFence = "fence", LedgerCallIt = "call_it";

    // ---- host (Town) counters: GameSession ints on the host, so they ride in the Town owner's save
    /// <summary>The GameClock.Day from which this house can be burgled again (0 = never touched).</summary>
    public static string HouseReadyDay(string stopId) => "scav_ready:" + stopId;
    /// <summary>How jumpy a district is after things went missing (0..SuspicionMax; one point fades each midnight).</summary>
    public static string Suspicion(string districtId) => "scav_alert:" + districtId;
    /// <summary>The day the host last faded suspicion (so a reload never fades twice).</summary>
    public const string SuspicionFadedDay = "scav_alert_day";
    /// <summary>Packed (day, count): burglars caught today in a district (fine escalation).</summary>
    public static string CaughtToday(string districtId) => "scav_caught:" + districtId;

    // ---- burglar's own business counters (their machine, their save)
    /// <summary>Packed (day, count): thefts today in a district that somebody saw / that nobody saw.</summary>
    public static string WitnessedToday(string districtId) => "scav_w:" + districtId;
    public static string UnwitnessedToday(string districtId) => "scav_u:" + districtId;
    /// <summary>The last day whose thefts were already turned into a Trust penalty.</summary>
    public const string TrustAppliedDay = "scav_trust_day";
    /// <summary>The day this business last used its Call It.</summary>
    public const string CallItDay = "scav_callit_day";

    public const string GuardPeek = "rb.scav.peek", GuardCall = "rb.scav.call", GuardCase = "rb.scav.case",
        GuardBreak = "rb.scav.break", GuardFence = "rb.scav.fence";
}

/// <summary>What happened at the door (host decides; sent to the burglar as one byte).</summary>
public enum BreakInOutcome : byte
{
    /// <summary>The host refused the attempt (too far, wrong house, cleaned out, no Itch...). Nothing happened.</summary>
    Denied = 0,
    /// <summary>The lock won and nobody noticed. Try again.</summary>
    Fumbled = 1,
    /// <summary>The lock won AND somebody saw you: consequences, no loot.</summary>
    Caught = 2,
    /// <summary>In and out, nobody saw. Loot; the district gets suspicious.</summary>
    Clean = 3,
    /// <summary>You got in but were seen: you bolt with half, and the consequences land.</summary>
    Spotted = 4,
}

/// <summary>Why a house can or cannot be cased right now.</summary>
public enum CaseStatus : byte { Ok = 0, TooFar = 1, NoItch = 2, NotAHouse = 3, CleanedOut = 4, OnAlert = 5, DistrictClosed = 6, Busy = 7 }

/// <summary>Everything the detection roll looks at. Plain numbers so the rule is testable.</summary>
public struct DetectionInput
{
    /// <summary>Hour of the day, 0..24.</summary>
    public float hour;
    /// <summary>Residents inside this house right now who are up / in bed.</summary>
    public int awakeHome, asleepHome;
    /// <summary>Sum of sight weights (each 0..1 by distance) of residents out on foot near the door.</summary>
    public float streetWitness;
    /// <summary>Sum of sight weights of OTHER players who are not on the burglar's crew.</summary>
    public float playerWitness;
    /// <summary>Sum of weights of yard dogs in earshot.</summary>
    public float dogs;
    public int softSoles, lockpicks, misses;
    /// <summary>The district's suspicion points (things went missing here lately).</summary>
    public int suspicion;
}

/// <summary>What a bag looks like through the specs. Deliberately has no price in it, at any level.</summary>
public struct XRaySummary
{
    /// <summary>0 = nothing known, 1 = silhouettes + "something valuable", 2 = also names the best thing.</summary>
    public byte level;
    /// <summary>Six 4-bit counts (capped at 15) in <see cref="ScavengerRules.XRayCats"/> order: junk, plastic, glass, metal, paper, hazard.</summary>
    public uint counts;
    public bool hasValuable;
    /// <summary>Level 2 only: the catalog id of the best valuable ("" when there is none). Its rarity comes from the catalog.</summary>
    public string bestDefId;

    public int Count(int cat) => cat < 0 || cat >= ScavengerRules.XRayCats ? 0 : (int)((counts >> (cat * 4)) & 0xF);
    /// <summary>Top bit of <see cref="counts"/> (the six counts use 24): the bag holds a part the viewer's business pinned
    /// as wanted on the Callings tree. Set by the host (ScavengerHost.Peek), never by <see cref="ScavengerRules.Summarize"/>.</summary>
    public const uint WantedBit = 1u << 31;
    public bool wanted => (counts & WantedBit) != 0;
    public int Total { get { int n = 0; for (int i = 0; i < ScavengerRules.XRayCats; i++) n += Count(i); return n; } }
}

public static class ScavengerTuning
{
    // =============================================================== NOSE FOR MONEY (local presentation)
    /// <summary>How far you smell a rich house, by level (index 0 unused).</summary>
    public static readonly float[] NoseRange = { 0f, 28f, 55f };
    /// <summary>Richness score = wealth + RichTierBonus x (district tier - 1). One / two / three coins at these scores.</summary>
    public static readonly float[] RichScores = { 0.60f, 0.78f, 0.95f };
    public const float RichTierBonus = 0.06f;

    // =============================================================== X-RAY SPECS
    /// <summary>You must be this close to a bag to peek (the host allows a little lag slack on top).</summary>
    public const float XRayRange = 6f, XRayHostSlack = 3f;
    public const float XRayRetrySeconds = 1.5f;

    // =============================================================== BURGLARY
    public const float DoorRange = 3.2f, DoorHostSlack = 2f, BailRange = 4.2f;
    /// <summary>A casing stays good for this long (real seconds) before you must look again.</summary>
    public const float CaseValidSeconds = 90f;
    /// <summary>Indexed by Lockpicks level 0..2: 0 = jiggling the handle with The Itch alone.</summary>
    public static readonly float[] PickSeconds = { 9f, 6.5f, 4.5f };
    public static readonly int[] PickPins = { 4, 3, 3 };
    /// <summary>Width of the sweet spot on the pick bar (0..1 of the bar).</summary>
    public static readonly float[] PickSweetSpot = { 0.16f, 0.22f, 0.30f };
    public static readonly float[] LockOdds = { 0.55f, 0.78f, 0.94f };
    public const float LockMissPenalty = 0.12f, LockCleanBonus = 0.05f, LockOddsMin = 0.10f, LockOddsMax = 0.98f;
    public const int MaxMisses = 6;

    // ---- being seen
    /// <summary>Chance that one resident indoors notices: awake / in bed.</summary>
    public const float AwakeHomeChance = 0.20f, AsleepHomeChance = 0.04f;
    public const int HomeCounted = 3;
    /// <summary>Per full-weight witness: a neighbour on foot, a rival player, a yard dog.</summary>
    public const float StreetWitnessChance = 0.28f, PlayerWitnessChance = 0.18f, DogChance = 0.22f;
    /// <summary>Witnesses count inside these radii (metres); weight falls off linearly to 0 at the edge.</summary>
    public const float WitnessRadius = 16f, DogRadius = 12f;
    /// <summary>People in bed: 22:00 to 06:00. Outdoor eyes are worth this much less in the dark (20:30 to 05:00).</summary>
    public const float BedHour = 22f, WakeHour = 6f, DarkFrom = 20.5f, DarkUntil = 5f, DarkSightMult = 0.45f;
    /// <summary>Soft Soles 0..2: radius of every outdoor witness, and how much the people indoors hear.</summary>
    public static readonly float[] SoftSolesRadius = { 1f, 0.70f, 0.45f };
    public static readonly float[] SoftSolesNoise = { 1f, 0.75f, 0.50f };
    /// <summary>Each fumbled pin is a clatter: x(1 + this) on the whole chance.</summary>
    public const float MissNoise = 0.30f;
    /// <summary>A jumpy district looks twice: +5% per suspicion point, at most +50%.</summary>
    public const float SuspicionStep = 0.05f;
    public const int SuspicionMax = 10;
    public const float DetectMin = 0.02f, DetectMax = 0.95f;

    // ---- loot and the sack
    /// <summary>Hot goods you can hold at once, by Swag Sack level (0 = your pockets).</summary>
    public static readonly int[] SackCapacity = { 2, 4, 8 };
    public const int LootBase = 2, LootWealthBonus = 3;
    /// <summary>A house keeps better things than it throws out: wealth is nudged up by this before the rarity table is read.</summary>
    public const float LootWealthNudge = 0.25f;
    /// <summary>Cleaned out for this many days after you got in; on alert for this many after you were caught at the door.</summary>
    public const int CleanedOutDays = 3, OnAlertDays = 1;

    // ---- consequences (compare WorldLifeTuning.Fines: a flattened neighbour is $3-$120, a shunted car $5-$200)
    /// <summary>A burglary fine is this many car shunts in the same district: $20 / $80 / $240 / $800 / $120 / $360,
    /// escalated like any neighbour incident (x1.25 per repeat that day, at most x3).</summary>
    public const float FineInShunts = 4f;
    /// <summary>Base Rep (the district multiplier applies): two flattened neighbours' worth.</summary>
    public const float BurglaryRep = -8f;
    /// <summary>District Trust at the next midnight: per theft someone saw, per theft nobody saw, and the most one night
    /// can cost. Yardstick: a dirty district loses 0.10 a night; a sparkling one gains 0.15.</summary>
    public const float TrustPerWitnessed = 0.04f, TrustPerUnwitnessed = 0.01f, TrustNightCap = 0.20f;

    // =============================================================== THE FENCE
    /// <summary>He pays this share of the appraisal. Pawn Pete pays 0.45 for clean goods; ebag nets about 0.92.</summary>
    public const float FenceFraction = 0.35f;
    /// <summary>Whatever the Raccoon stage does to his mood, the share stays inside these.</summary>
    public const float FenceFractionMin = 0.15f, FenceFractionMax = 0.60f;
    public const float FenceRange = 4.5f, FenceHostSlack = 3f;

    // =============================================================== CALL IT
    /// <summary>Band edges in dollars for the best valuable's appraisal: band 0 = no valuable at all, 1 = under $25, ...</summary>
    public static readonly float[] CallBandEdges = { 25f, 100f, 500f };
    public static readonly string[] CallBandNames = { "Nothing but trash", "Pocket change (under $25)", "A decent find ($25 to $100)", "A proper score ($100 to $500)", "JACKPOT ($500 and up)" };
    /// <summary>Paid for a correct call, by band. A pickup fee is $4; a Work Order pays $25 and up.</summary>
    public static readonly float[] CallBonus = { 2f, 10f, 30f, 120f, 400f };
    public const float CallRange = 6f;
}

public static class ScavengerRules
{
    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    static T At<T>(T[] table, int level) => table[Clamp(level, 0, table.Length - 1)];

    // ------------------------------------------------------------------ nose for money

    public static float RichScore(float wealth, int districtTier) =>
        Clamp(wealth, 0f, 1f) + ScavengerTuning.RichTierBonus * (Clamp(districtTier, 1, 4) - 1);

    /// <summary>0 = not worth a sniff, 1..3 = coins over the roof.</summary>
    public static int RichTier(float wealth, int districtTier)
    {
        float score = RichScore(wealth, districtTier); int tier = 0;
        for (int i = 0; i < ScavengerTuning.RichScores.Length; i++) if (score >= ScavengerTuning.RichScores[i]) tier = i + 1;
        return tier;
    }
    public static float NoseRange(int level) => At(ScavengerTuning.NoseRange, level);

    // ------------------------------------------------------------------ x-ray

    public const int XRayCats = 6;
    /// <summary>The silhouette order: Junk, Plastic, Glass, Metal, Paper, Hazard (the ItemCategory order without Valuable).</summary>
    public static int XRayCatIndex(ItemCategory c) => c == ItemCategory.Valuable ? -1 : (int)c;
    public static ItemCategory XRayCategory(int index) => (ItemCategory)Clamp(index, 0, XRayCats - 1);

    /// <summary>
    /// What <paramref name="level"/> specs show of a bag. Level 1: how many things of each kind, and whether anything in
    /// there is a valuable. Level 2: also WHICH valuable is the best one (picked by catalog rarity, then by the catalog's
    /// top price, then by id: never by this item's rolled value or condition, so the answer cannot leak the appraisal).
    /// </summary>
    public static XRaySummary Summarize(BagContents contents, int level)
    {
        var s = new XRaySummary { level = (byte)Clamp(level, 0, 2), bestDefId = "" };
        if (s.level == 0 || contents == null || contents.items == null) { s.level = 0; return s; }
        var counts = new int[XRayCats]; ItemDef best = null;
        foreach (var item in contents.items) {
            var def = item != null ? item.Def : null;
            if (def == null) continue;
            int cat = XRayCatIndex(def.category);
            if (cat >= 0) { counts[cat]++; continue; }
            s.hasValuable = true;
            if (best == null || BetterForXRay(def, best)) best = def;
        }
        for (int i = 0; i < XRayCats; i++) s.counts |= (uint)Math.Min(15, counts[i]) << (i * 4);
        if (s.level >= 2 && best != null) s.bestDefId = best.id;
        return s;
    }
    static bool BetterForXRay(ItemDef a, ItemDef b)
    {
        if (a.rarity != b.rarity) return a.rarity > b.rarity;
        if (a.maxValue != b.maxValue) return a.maxValue > b.maxValue;
        return string.CompareOrdinal(a.id, b.id) < 0;
    }

    // ------------------------------------------------------------------ the lock

    public static float PickSeconds(int lockpicks) => At(ScavengerTuning.PickSeconds, lockpicks);
    public static int PickPins(int lockpicks) => At(ScavengerTuning.PickPins, lockpicks);
    public static float PickSweetSpot(int lockpicks) => At(ScavengerTuning.PickSweetSpot, lockpicks);

    /// <summary>Chance the door gives, from the Lockpicks level and how many pins you fumbled.</summary>
    public static float LockOdds(int lockpicks, int misses)
    {
        misses = Clamp(misses, 0, ScavengerTuning.MaxMisses);
        float odds = At(ScavengerTuning.LockOdds, lockpicks) - ScavengerTuning.LockMissPenalty * misses + (misses == 0 ? ScavengerTuning.LockCleanBonus : 0f);
        return Clamp(odds, ScavengerTuning.LockOddsMin, ScavengerTuning.LockOddsMax);
    }

    // ------------------------------------------------------------------ being seen

    public static bool IsBedtime(float hour) { hour = Wrap(hour); return hour >= ScavengerTuning.BedHour || hour < ScavengerTuning.WakeHour; }
    public static bool IsDark(float hour) { hour = Wrap(hour); return hour >= ScavengerTuning.DarkFrom || hour < ScavengerTuning.DarkUntil; }
    static float Wrap(float hour) { hour %= 24f; return hour < 0f ? hour + 24f : hour; }

    /// <summary>How much one outdoor witness counts at this distance (1 next to you, 0 at the edge of the radius that
    /// Soft Soles shrinks).</summary>
    public static float SightWeight(float distance, float radius, int softSoles)
    {
        float reach = radius * At(ScavengerTuning.SoftSolesRadius, softSoles);
        if (reach <= 0.01f || distance >= reach) return 0f;
        return Clamp(1f - Math.Max(0f, distance) / reach, 0f, 1f);
    }

    /// <summary>
    /// The chance somebody sees or hears the break-in. Independent sources (people indoors, the street, rival players,
    /// dogs) combine as 1 - product(1 - p). Night halves what the street can see and puts the household to bed; Soft
    /// Soles muffles the people indoors; quicker picks mean less time on the doorstep; fumbled pins and a jumpy district
    /// multiply the lot.
    /// </summary>
    public static float DetectionChance(DetectionInput d)
    {
        float noise = At(ScavengerTuning.SoftSolesNoise, d.softSoles);
        float exposure = PickSeconds(d.lockpicks) / ScavengerTuning.PickSeconds[0];
        float sight = IsDark(d.hour) ? ScavengerTuning.DarkSightMult : 1f;
        float miss = 1f;
        int awake = Clamp(d.awakeHome, 0, ScavengerTuning.HomeCounted), asleep = Clamp(d.asleepHome, 0, ScavengerTuning.HomeCounted - awake);
        for (int i = 0; i < awake; i++) miss *= 1f - Clamp(ScavengerTuning.AwakeHomeChance * noise * exposure, 0f, 1f);
        for (int i = 0; i < asleep; i++) miss *= 1f - Clamp(ScavengerTuning.AsleepHomeChance * noise * exposure, 0f, 1f);
        miss *= 1f - Clamp(ScavengerTuning.StreetWitnessChance * Math.Max(0f, d.streetWitness) * sight * exposure, 0f, 0.9f);
        miss *= 1f - Clamp(ScavengerTuning.PlayerWitnessChance * Math.Max(0f, d.playerWitness) * sight * exposure, 0f, 0.9f);
        miss *= 1f - Clamp(ScavengerTuning.DogChance * Math.Max(0f, d.dogs) * noise * exposure, 0f, 0.9f);
        float p = 1f - miss;
        p *= 1f + ScavengerTuning.MissNoise * Clamp(d.misses, 0, ScavengerTuning.MaxMisses);
        p *= 1f + ScavengerTuning.SuspicionStep * Clamp(d.suspicion, 0, ScavengerTuning.SuspicionMax);
        return Clamp(p, ScavengerTuning.DetectMin, ScavengerTuning.DetectMax);
    }

    /// <summary>Who is indoors when the live schedule is not running on the host (it is in the garage): two in bed at
    /// night, one pottering about by day.</summary>
    public static void FallbackHousehold(float hour, out int awake, out int asleep)
    {
        if (IsBedtime(hour)) { awake = 0; asleep = 2; } else { awake = 1; asleep = 0; }
    }

    public static BreakInOutcome Outcome(bool opened, bool seen) =>
        opened ? (seen ? BreakInOutcome.Spotted : BreakInOutcome.Clean) : (seen ? BreakInOutcome.Caught : BreakInOutcome.Fumbled);

    // ------------------------------------------------------------------ loot, the sack, cooldowns

    public static int SackCapacity(int swagSack) => At(ScavengerTuning.SackCapacity, swagSack);

    /// <summary>What a house has lying about: 2 to 5 things by wealth.</summary>
    public static int LootOnOffer(float wealth) =>
        ScavengerTuning.LootBase + (int)Math.Round(Clamp(wealth, 0f, 1f) * ScavengerTuning.LootWealthBonus, MidpointRounding.AwayFromZero);

    /// <summary>How many things you leave with: what the house has, what your sack still holds, and half of that (rounded
    /// down) if you were seen and had to bolt.</summary>
    public static int LootCount(float wealth, int swagSack, int hotHeld, bool seen)
    {
        int free = Math.Max(0, SackCapacity(swagSack) - Math.Max(0, hotHeld));
        int n = Math.Min(LootOnOffer(wealth), free);
        return seen ? n / 2 : n;
    }

    /// <summary>The wealth the rarity table is read at for a burglary (richer than the same house's bin).</summary>
    public static float LootWealth(float wealth) => Clamp(Clamp(wealth, 0f, 1f) + ScavengerTuning.LootWealthNudge, 0f, 1f);

    /// <summary>Index into four rarity weights (Uncommon, Rare, Epic, Legendary) for a roll in [0, 1).</summary>
    public static int PickWeighted(IReadOnlyList<float> weights, float roll01)
    {
        float total = 0f;
        for (int i = 0; i < weights.Count; i++) if (weights[i] > 0f) total += weights[i];
        if (total <= 0f) return 0;
        float r = Clamp(roll01, 0f, 0.999999f) * total; int last = 0;
        for (int i = 0; i < weights.Count; i++) {
            if (weights[i] <= 0f) continue;
            last = i;
            if (r < weights[i]) return i;
            r -= weights[i];
        }
        return last;
    }

    /// <summary>Can this def come out of a house in a district of this tier? Real catalog valuables only: nothing you can
    /// only earn (trophies, the crown, the pet), nothing that only turns up in odd bags.</summary>
    public static bool Stealable(ItemDef def, int districtTier)
    {
        if (def == null || def.category != ItemCategory.Valuable || def.maxValue <= def.minValue || def.minValue <= 0f) return false;
        if (ItemCatalog.TierOf(def) > Clamp(districtTier, 1, 4)) return false;
        return !ItemCatalog.HasTag(def, "night_only") && !ItemCatalog.HasTag(def, "tell_only") && !ItemCatalog.HasTag(def, "garage_only");
    }

    /// <summary>The first day the house can be hit again.</summary>
    public static int ReadyDayAfter(int today, bool opened, bool seen) =>
        opened ? today + ScavengerTuning.CleanedOutDays : seen ? today + ScavengerTuning.OnAlertDays : today;
    public static bool HouseReady(int readyDay, int today) => readyDay <= today;
    /// <summary>A house that would be ready only after a cleaned-out wait reads "cleaned out"; a shorter wait is "on alert".</summary>
    public static CaseStatus CooldownStatus(int readyDay, int today) =>
        readyDay <= today ? CaseStatus.Ok : readyDay - today > ScavengerTuning.OnAlertDays ? CaseStatus.CleanedOut : CaseStatus.OnAlert;

    // ------------------------------------------------------------------ hot goods

    public static bool IsHot(ItemInstance item) => item != null && item.hot;
    public static int HotCount(IReadOnlyList<ItemInstance> inventory)
    {
        int n = 0;
        if (inventory != null) for (int i = 0; i < inventory.Count; i++) if (IsHot(inventory[i])) n++;
        return n;
    }

    // ------------------------------------------------------------------ consequences

    /// <summary>The burglary fine in dollars (whole cents): the district's car-shunt fine x FineInShunts x the day's
    /// escalation (WorldLifeTuning.Fines.Escalation of the n-th burglar caught there today).</summary>
    public static float Fine(float shuntFine, float escalation)
    {
        return (float)Math.Round(Math.Max(0f, shuntFine) * ScavengerTuning.FineInShunts * Math.Max(1f, escalation) * 100.0) / 100f;
    }

    /// <summary>Trust a district takes away at midnight for one day's thefts there (0..TrustNightCap).</summary>
    public static float TrustPenalty(int witnessed, int unwitnessed) =>
        Clamp(ScavengerTuning.TrustPerWitnessed * Math.Max(0, witnessed) + ScavengerTuning.TrustPerUnwitnessed * Math.Max(0, unwitnessed), 0f, ScavengerTuning.TrustNightCap);

    public static int SuspicionAfterTheft(int suspicion, bool witnessed) => Clamp(suspicion + (witnessed ? 2 : 1), 0, ScavengerTuning.SuspicionMax);
    public static int SuspicionAfterNights(int suspicion, int nights) => Clamp(suspicion - Math.Max(0, nights), 0, ScavengerTuning.SuspicionMax);

    // ------------------------------------------------------------------ the fence

    /// <summary>What the fence pays for goods appraised at <paramref name="appraisal"/>, in whole cents.
    /// <paramref name="standing"/> is the Raccoon stage's seam (1 = he has no opinion of you).</summary>
    public static long FencePriceCents(float appraisal, float standing)
    {
        if (float.IsNaN(appraisal) || float.IsInfinity(appraisal) || appraisal <= 0f) return 0;
        if (float.IsNaN(standing) || float.IsInfinity(standing)) standing = 1f;
        float share = Clamp(ScavengerTuning.FenceFraction * standing, ScavengerTuning.FenceFractionMin, ScavengerTuning.FenceFractionMax);
        return Math.Max(1L, (long)Math.Floor((double)appraisal * share * 100.0 + 1e-4)); // the nudge keeps $1 x 0.35 at 35 cents, not 34
    }
    public static bool FenceOpen(float hour) => IsDark(hour);

    // ------------------------------------------------------------------ call it

    public static int CallBands => ScavengerTuning.CallBandNames.Length;
    /// <summary>Band of a bag whose best valuable is appraised at <paramref name="bestValue"/> (hasValuable false = band 0).</summary>
    public static int CallBand(bool hasValuable, float bestValue)
    {
        if (!hasValuable) return 0;
        int band = 1;
        for (int i = 0; i < ScavengerTuning.CallBandEdges.Length; i++) if (bestValue >= ScavengerTuning.CallBandEdges[i]) band = i + 2;
        return band;
    }
    /// <summary>The band the bag is really in: its dearest valuable decides.</summary>
    public static int CallBandOf(BagContents contents, Func<ItemInstance, float> appraise)
    {
        bool any = false; float best = 0f;
        if (contents != null && contents.items != null)
            foreach (var item in contents.items) {
                var def = item != null ? item.Def : null;
                if (def == null || def.category != ItemCategory.Valuable) continue;
                float v = appraise != null ? appraise(item) : item.value;
                if (!any || v > best) best = v;
                any = true;
            }
        return CallBand(any, best);
    }
    /// <summary>Dollars paid for calling <paramref name="called"/> when the bag is really <paramref name="actual"/>.</summary>
    public static float CallPayout(int called, int actual) =>
        called == actual && called >= 0 && called < ScavengerTuning.CallBonus.Length ? ScavengerTuning.CallBonus[called] : 0f;
    public static bool CallAvailable(int lastCallDay, int today) => lastCallDay != today;
}

// The Raccoon calling ("bitten one time too many"): EVERY number in one place. Pure data, no UnityEngine.
//
// ECONOMICS (Docs/Progression.md 6.9.3 has the full table). The chipped crew is the game's ONLY background collection:
// it has to stay a joke early and be able to keep the cheaper districts collected late, without ever out-earning the
// route you drive yourself. District numbers are Districts.cs: Acres 35 bags a day at $4, Meadows 30 at $15, Commons 28
// at $25 (93 bags a day between them at full Trust), then Snootington 24 at $40 and Stackington 26 at $55.
//   Pay        35% of fee x lateness fraction (CrewFeeSharePercent), no tip. A night sweep also earns the Night Shift x1.5
//              every night pickup gets (52.5%). Half Rep, no Work Order credit (BagCollectedFact.byRaccoon).
//   Volume     bags per raccoon per sweep = 1 (Carry 6+: 2, Carry 10: 3), a Zoom chance of one more, and with a Brain Chip
//              another 1 + Carry / 5. The whole crew is capped per sweep by hutch size and chip (CrewSweepCaps):
//                Collar Chip   2 night sweeps    cap  6 / 10   3 wild raccoons: about 6 bags a night
//                Brain Chip    + 12:00           cap 12 / 20   6 wild raccoons: about 45 bags a day
//                Full-Time     + 07:00           cap 16 / 26   6 bred raccoons: about 96 bags a day (ceiling 104)
//   Which      Collar Chip raccoons take the OLDEST (smelliest, worst-paying) bag they are allowed. A Brain Chip plans a
//              route: the cheapest district first, oldest first inside it, so the dear streets are left for you. No crew
//              ever works a district above CrewMaxDistrictTier (the Bluffs' Butler Drops are yours).
//   Losses     3% per Greed point of what they take is eaten (no pay, gone); Collar Chip raccoons lose about a fifth of
//              their sweeps to naps, dogs and garden gnomes.
//   Kibble     $5 a head a day for everybody; a crew raccoon costs $8 on a Brain Chip and $70 on a Full-Time Chip
//              (CrewFoodCents*). That is the wage: it keeps the crew's net fee income near break-even at every stage
//              (about -$9, +$25 and +$35 to +$65 a day in the expected-value model, 100% Trust, nobody else collecting).
//   Why        The crew's real worth is clean kerbs (Clean Streets, Trust, no neglect fines) and the bags on your tipping
//              floor, not the fees.
public static class RaccoonTuning
{
    // ------------------------------------------------------------------ hutch (calling_critter_hutch 1-2)
    public const int HutchCapacityL1 = 3, HutchCapacityL2 = 6;
    /// <summary>Hard ceiling the saved roster can ever hold (the replicated string budget is sized for it).</summary>
    public const int MaxRaccoons = HutchCapacityL2;
    public const int TraitMin = 1, TraitMax = 10;
    /// <summary>A trapped wild raccoon rolls each trait in this range: 8-10 only comes from breeding.</summary>
    public const int WildTraitMin = 2, WildTraitMax = 7;
    public const int NameMaxChars = 12;
    /// <summary>Hutch kibble, per raccoon (kits too), per in-game day, in cents. Charged at the day rollover.</summary>
    public const int FoodCentsPerHeadPerDay = 500;
    /// <summary>What a raccoon ON THE CREW eats instead, per in-game day, once the business has a Brain Chip (brain food)
    /// or a Full-Time Chip (premium, and they know it). Resting raccoons, kits and the pack stay on the plain kibble.</summary>
    public const int CrewFoodCentsBrain = 800, CrewFoodCentsFullTime = 7000;
    public const string LedgerFood = "hutch_food", LedgerCrew = "raccoon_crew", LedgerCrewEvent = "raccoon_antics",
        LedgerRival = "kings_rival", LedgerTribute = "kings_tribute";
    public const int MaxGnomeTrophies = 3;

    // ------------------------------------------------------------------ breeding (calling_breeding_box)
    public const int GestationDays = 2, GrowUpDays = 2, RestDaysAfterLitter = 1;
    public const int LitterMax = 2;
    /// <summary>Each trait = the parents' average, then -1..+1; this percent of the time -2..+2 instead.</summary>
    public const int BigMutationPercent = 12;
    /// <summary>Chance a kit is a special variant (Golden, Albino, Chonk, Zoomer).</summary>
    public const int VariantPercent = 6;
    /// <summary>Two rabid parents: always rabid kits. One: this percent.</summary>
    public const int OneRabidParentPercent = 50;

    // ------------------------------------------------------------------ crews (calling_collar_chip, calling_brain_chip 1-2)
    /// <summary>Sweep hours (whole hours of the day). Collar Chip: the night. Brain Chip: plus noon. Full-Time Chip
    /// (calling_brain_chip 2): plus the morning run, so no kerb waits longer than ten hours.</summary>
    public static readonly int[] CollarSweepHours = { 22, 2 };
    public static readonly int[] BrainSweepHours = { 22, 2, 12 };
    public static readonly int[] FullTimeSweepHours = { 22, 2, 7, 12 };
    /// <summary>A crew "shift" (one report) runs from this hour to the same hour next day.</summary>
    public const int ShiftStartHour = 6;
    public const int CrewFeeSharePercent = 35;
    /// <summary>Most bags one business's whole crew takes in one sweep: [chip: collar, brain, full-time][hutch level 1, 2].</summary>
    public static readonly int[][] CrewSweepCaps = { new[] { 6, 10 }, new[] { 12, 20 }, new[] { 16, 26 } };
    /// <summary>The biggest number in <see cref="CrewSweepCaps"/> (tests and the docs' ceiling).</summary>
    public const int CrewSweepCapMax = 26;
    /// <summary>Crews never collect in a district above this tier (4 = Billionaire Bluffs).</summary>
    public const int CrewMaxDistrictTier = 3;
    /// <summary>Carry trait at or above these values carries a second / third bag per sweep.</summary>
    public const int CarryTwoBags = 6, CarryThreeBags = 10;
    /// <summary>With a Brain Chip a raccoon pulls a little cart: this many more bags, plus one per so many Carry points.</summary>
    public const int BrainExtraBags = 1, BrainCarryPerExtraBag = 5;
    /// <summary>Speed trait: percent chance per point of one extra bag in a sweep.</summary>
    public const int SpeedBonusPercentPerPoint = 4;
    /// <summary>Greed trait: percent chance per point that a collected bag gets eaten (no pay, gone).</summary>
    public const int GreedEatPercentPerPoint = 3;
    public const int RabidExtraEatPercent = 10;
    /// <summary>Nerve trait: (10 - nerve) x this percent chance per sweep of being chased off by a dog (Collar Chip).</summary>
    public const int ScarePercentPerMissingNerve = 2;
    // Collar Chip antics, percent per raccoon per sweep. The Brain Chip keeps only the wallet and a fifth of the scares.
    public const int NapPercent = 6, WanderPercent = 4, GnomePercent = 3, WalletPercent = 3;
    public const int BrainScareDivisor = 5;
    public const int WanderAwayDays = 1;
    public const int GnomeDamagesCents = 600, WalletCents = 1000;

    // ------------------------------------------------------------------ Embrace It (calling_embrace_it 1-3)
    /// <summary>L1: the trippy rabies look is capped here (0..1) and the stumbling drift is off.</summary>
    public const float FeralSymptomCap = 0.12f;
    /// <summary>L2, infected, at night: on-foot speed multiplier (stacks with boots and juice).</summary>
    public const float FeralNightSpeed = 1.15f;
    /// <summary>L3: by day too, a little.</summary>
    public const float FeralDaySpeed = 1.05f;
    public const float PounceDistance = 7f, PounceHop = 1.3f, PounceSeconds = 0.45f, PounceCooldown = 9f;
    /// <summary>L3: wild raccoons this close when you land are friendly for this long (and drop what they stole).</summary>
    public const float PounceCharmRadius = 12f, PounceCharmSeconds = 20f;

    // ------------------------------------------------------------------ Pack Leader (calling_pack_leader 1-2)
    public const int CompanionsL1 = 1, CompanionsL2 = 3;
    /// <summary>Seconds between one companion's fetches.</summary>
    public const float FetchCooldown = 30f;
    /// <summary>A companion fetches a resting kerb bag this close to its leader into the leader's truck this close.</summary>
    public const float FetchBagRadius = 14f, FetchTruckRadius = 30f, FetchRunSeconds = 0.9f;
    /// <summary>L2: wild raccoons within this of a pack leader with a companion out back off.</summary>
    public const float PackDeferRadius = 7f, PackDeferSeconds = 12f;

    // ------------------------------------------------------------------ King's Rival (calling_kings_rival)
    /// <summary>With nobody to be angry at, the King goes for a Rival on foot within this range first (his own chase range).</summary>
    public const float RivalFocusRange = 30f;
    /// <summary>Paid to each Rival who landed a hit when the King is dethroned, on top of the normal pot share.</summary>
    public const int RivalBonusCents = 25000;
    /// <summary>What the King's collectors take from each Rival in town when he leaves undefeated.</summary>
    public const int TributeCents = 6000;
}

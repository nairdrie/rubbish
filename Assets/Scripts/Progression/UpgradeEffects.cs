using UnityEngine;

/// <summary>
/// Every upgrade number in Docs/Progression.md §6, as pure functions of the company's upgrade levels
/// (<see cref="GameSession.GetUpgradeLevel"/>). STEP 0 CONTRACT, owned by builder A for good: B, C and D only READ this;
/// if a number looks wrong, say so in your report instead of hard-coding your own.
/// Levels above a table's top are clamped (old saves keep their meaning: capacity 1-3, turbo 1, horn 1, headlamp 1;
/// the Magnus career rework clamps old rail 2 -> 1 and gripper 3 -> 2).
/// </summary>
public static class UpgradeEffects
{
    public static int Level(string id) => GameSession.GetUpgradeLevel(id);

    static int L(string id, int max) => Mathf.Clamp(Level(id), 0, max);

    // ------------------------------------------------------------------ TRUCK (§6.1)

    /// <summary>Hopper kg for an explicit business model and truck_capacity level. Upgrade bonuses transfer to a new chassis.</summary>
    public static float CapacityKgFor(int model, int capacityLevel) => TruckProgression.CapacityKg(model, capacityLevel);
    public static float CurrentTruckCapacityKg => CapacityKgFor(TruckProgression.CurrentModel, Level(ShopIds.TruckCapacity));

    public struct EngineTier { public float motorTorque, maxSpeed, steerAtMaxSpeed, brakeTorque, maxLateralAccel; }

    const float HardSpeedCap = 18f;
    static readonly EngineTier[] engineTiers =
    {
        // stock: the prefab's 500 Nm / 10 m/s / 10°, brakes 2000 -> 1000 (today's brakes ask for 2.1 g and lock up)
        new EngineTier { motorTorque = 500f, maxSpeed = 10f,   steerAtMaxSpeed = 10f,  brakeTorque = 1000f, maxLateralAccel = float.PositiveInfinity },
        new EngineTier { motorTorque = 675f, maxSpeed = 12.5f, steerAtMaxSpeed = 6.7f, brakeTorque = 1000f, maxLateralAccel = float.PositiveInfinity },
        new EngineTier { motorTorque = 800f, maxSpeed = 15f,   steerAtMaxSpeed = 4.7f, brakeTorque = 1100f, maxLateralAccel = float.PositiveInfinity },
        new EngineTier { motorTorque = 900f, maxSpeed = 17f,   steerAtMaxSpeed = 3.7f, brakeTorque = 1200f, maxLateralAccel = 7f },
    };

    /// <summary>Absolute engine values from truck_turbo 0..3; golden_truck lifts top speed to the 18 m/s hard cap.</summary>
    public static EngineTier Engine => EngineAtLevel(Level(ShopIds.TruckTurbo), Level(ShopIds.GoldenTruck) > 0);

    /// <summary>Explicit owner levels for multiplayer trucks; driving someone else's truck does not change its engine.</summary>
    public static EngineTier EngineAtLevel(int turbo, bool golden)
    {
        var e = engineTiers[Mathf.Clamp(turbo, 0, engineTiers.Length - 1)];
        if (golden) e.maxSpeed = HardSpeedCap;
        e.maxSpeed = Mathf.Min(e.maxSpeed, HardSpeedCap);
        return e;
    }

    public struct HandlingTier
    {
        public float sidewaysStiffness, forwardStiffness, cogOffset, unflipHoldSeconds, antiTipBelow;
        public bool freeUnflip, drift;
    }

    static readonly HandlingTier[] handlingTiers =
    {
        new HandlingTier { sidewaysStiffness = 1f,    forwardStiffness = 1f,    cogOffset = -1.0f, unflipHoldSeconds = 1.5f, antiTipBelow = 0f },
        new HandlingTier { sidewaysStiffness = 1.15f, forwardStiffness = 1f,    cogOffset = -1.3f, unflipHoldSeconds = 0.5f, antiTipBelow = 0f,  freeUnflip = true },
        new HandlingTier { sidewaysStiffness = 1.30f, forwardStiffness = 1.15f, cogOffset = -1.3f, unflipHoldSeconds = 0.5f, antiTipBelow = 12f, freeUnflip = true },
        new HandlingTier { sidewaysStiffness = 1.40f, forwardStiffness = 1.15f, cogOffset = -1.3f, unflipHoldSeconds = 0.5f, antiTipBelow = 12f, freeUnflip = true, drift = true },
    };

    /// <summary>truck_handling 0..3. Stiffness values are multipliers on the stock friction curves; cogOffset is absolute;
    /// antiTipBelow = m/s under which the anti-tip assist acts (0 = off); unflip hold is the "Hold R" recovery time.</summary>
    public static HandlingTier Handling => HandlingAtLevel(Level(ShopIds.TruckHandling));
    public static HandlingTier HandlingAtLevel(int level) => handlingTiers[Mathf.Clamp(level, 0, handlingTiers.Length - 1)];

    public struct ArmTier
    {
        public bool present, auto, bothSides, litterVac;
        public float reach, maxSpeed, deploySeconds, secondsPerBag, fumbleChance, vacRadius;
    }

    static readonly ArmTier[] armTiers =
    {
        new ArmTier(),
        new ArmTier { present = true, reach = 2.4f, maxSpeed = 0.5f, deploySeconds = 3f,   secondsPerBag = 2.5f, fumbleChance = 0.05f },
        new ArmTier { present = true, auto = true, bothSides = true, reach = 3.2f, maxSpeed = 3f, deploySeconds = 1.2f, secondsPerBag = 1.6f },
        new ArmTier { present = true, auto = true, bothSides = true, litterVac = true, reach = 5f, maxSpeed = 4f, deploySeconds = 0.5f, secondsPerBag = 0.9f, vacRadius = 3f },
    };

    /// <summary>truck_arm 0..3 (side-loader). L1 is hold-X while stopped, right side only.</summary>
    public static ArmTier Arm => ArmAtLevel(Level(ShopIds.TruckArm));
    public static ArmTier ArmAtLevel(int level) => armTiers[Mathf.Clamp(level, 0, armTiers.Length - 1)];

    static readonly float[] hornRadius = { 12f, 25f, 40f };
    /// <summary>Horn scare radius by air_horn 0..2: 12 / 25 / 40 m.</summary>
    public static float HornScareRadius => hornRadius[L(ShopIds.AirHorn, 2)];

    /// <summary>truck_headlights: range ×1.6 from L1.</summary>
    public static float HeadlightRangeMult => L(ShopIds.TruckHeadlights, 2) >= 1 ? 1.6f : 1f;
    static readonly float[] beamScare = { 0f, 20f, 30f };
    /// <summary>Calm raccoon flinch range: 0 / 20 / 30. Anger and foamy rabies ignore light.</summary>
    public static float BeamScareRadius => BeamScareRadiusAtLevel(L(ShopIds.TruckHeadlights, 2));
    public static float BeamScareRadiusAtLevel(int level) => beamScare[Mathf.Clamp(level, 0, beamScare.Length - 1)];

    // ------------------------------------------------------------------ GARAGE (§6.2)

    /// <summary>Auto-Tipper (one level; sold in the truck department): the truck's own TIP IT dumps in 3 s instead of 10.</summary>
    public static bool AutoTipper => Level(ShopIds.AutoTipper) >= 1;
    /// <summary>TIP IT (hold G in the driver's seat) dump time: 10 s stock, 3 s with the auto-tipper (DumpRules).</summary>
    public static float TipSeconds => DumpRules.Seconds(Level(ShopIds.AutoTipper));

    /// <summary>conveyor_belt level 0..3 (1 Bag Belt: normal bags to the queue rack, 2 Heavy-Duty Motor: heavy bags too,
    /// 3 Junk Diverter: small junk is shaken out into the compactor on the way). The belt never opens a bag (ConveyorRules).</summary>
    public static int Conveyor => L(ShopIds.ConveyorBelt, 3);

    /// <summary>Per-item hand-sort time multiplier: XL table ×0.95. (The conveyor no longer changes sorting time.)</summary>
    public static float ItemTimeMult => Level(ShopIds.SortingTable) >= 1 ? 0.95f : 1f;

    /// <summary>SortingTable.maxItemsOnTable: 40 stock, 60 from sorting_table L1.</summary>
    public static int TableMaxItems => Level(ShopIds.SortingTable) >= 1 ? 60 : 40;
    public static bool MagnetStrip => Level(ShopIds.SortingTable) >= 2;
    /// <summary>Sorting positions at the table: 1, or 2 with the Double-Wide table (sorting_table L2). See SortSeats.</summary>
    public static int SortingSeats => SortSeats.Count(Level(ShopIds.SortingTable));

    /// <summary>compactor_mk2 level 0..3.</summary>
    public static int Compactor => L(ShopIds.CompactorMk2, 3);
    static readonly float[] compactPerKg = { Economy.CompactorScrapPerKg, 1.00f, 1.20f, 1.40f };
    /// <summary>Chamber and bulk compact $/kg: 0.80 / 1.00 / 1.20 / 1.40; always below base recycling (paper 1.80).</summary>
    public static float CompactPerKg => CompactPerKgAtLevel(Compactor);
    public static float CompactPerKgAtLevel(int level) => compactPerKg[Mathf.Clamp(level, 0, compactPerKg.Length - 1)];
    /// <summary>Compatibility multiplier: ordinary loose scrap earns exactly the same rate as bulk compaction.</summary>
    public static float ScrapMult => ScrapMultAtLevel(Compactor);
    public static float ScrapMultAtLevel(int level) => CompactPerKgAtLevel(level) / Economy.CompactorScrapPerKg;
    /// <summary>Compatibility aliases for the old CRUSH ALL API; bulk uses the chamber's per-kg rate.</summary>
    public static float LazyPerKg => CompactPerKg;
    public static float LazyPerKgAtLevel(int level) => CompactPerKgAtLevel(level);
    /// <summary>Suppresses comic hazard bangs (compactor L2+); hazards are consumed once, never ejected for reuse.</summary>
    public static bool HazardSafe => Compactor >= 2;
    /// <summary>Valuables survive compaction into the KEEP crate at this value fraction (compactor L3), else 0.</summary>
    public static float GrateKeep => Compactor >= 3 ? 0.30f : 0f;
    public static float GrateKeepAtLevel(int level) => level >= 3 ? 0.30f : 0f;

    static readonly float[] recycleMult = { 1f, 1.5f, 2f, 3f, 4f };
    /// <summary>Recycling pay ×(1 / 1.5 / 2 / 3 / 4) by recycling_contracts, after the combo.</summary>
    public static float RecycleMult => recycleMult[L(ShopIds.RecyclingContracts, 4)];
    public static float RecycleMultAtLevel(int level) => recycleMult[Mathf.Clamp(level, 0, recycleMult.Length - 1)];
    static readonly float[] hazardPay = { Economy.HazardDisposalPay, 6.00f, 16.00f };
    /// <summary>$ per hazard binned: 3.00 / 6.00 / 16.00 by hazmat_contract.</summary>
    public static float HazardPay => hazardPay[L(ShopIds.HazmatContract, 2)];
    public static float HazardPayAtLevel(int level) => hazardPay[Mathf.Clamp(level, 0, hazardPay.Length - 1)];

    static readonly int[] trophySlots = { 0, 6, 24 };
    static readonly float[] trophyRep = { 0f, RepValues.TrophyShelfL1, RepValues.TrophyShelfL2 };
    /// <summary>Trophy shelf + museum wing slots: 0 / 6 / 24 by trophy_shelf (the whole display line: DisplayPlanner.OwnedSlots).</summary>
    public static int TrophySlots => trophySlots[L(ShopIds.TrophyShelf, 2)];
    /// <summary>Everything the displays pay at midnight, as trophy_shelf Rep (§6.8): the trophy shelf / museum wing's flat
    /// +40 / +160, plus the newer pieces' base Rep while they show a find and their Showcase Rep (DisplayPlanner).</summary>
    public static float TrophyRepPerDay => trophyRep[L(ShopIds.TrophyShelf, 2)] + DisplayPlanner.ExtraRepPerDay();

    // ------------------------------------------------------------------ DISPLAYS (§6.8, garage dept)
    // Kept finds (GameSession.Inventory) show themselves, best first: pedestal, vault (Epic+ only), glass case, trophy
    // shelf, museum wing, plank shelf (DisplayPlanner). trophy_shelf keeps its flat Rep by level; the newer pieces pay
    // their base Rep while they show at least one find, and the fancy ones (glass case, pedestal x2, vault) add Showcase
    // Rep for every Rare+ find they show. All of it is paid at midnight through TrophyRepPerDay.

    public const int PlankShelfSlots = 3, TrophyShelfSlots = 6, MuseumWingSlots = 18, GlassCaseSlots = 4, PedestalSlots = 1, VaultSlots = 6;
    /// <summary>Base Rep per midnight while the piece shows at least one find (a plank impresses nobody).</summary>
    public const float PlankShelfRep = 0f, GlassCaseRep = 30f, PedestalRep = 75f, VaultRep = 120f;
    /// <summary>The pedestal's spotlight doubles the Showcase Rep of its find.</summary>
    public const float PedestalShowcaseMult = 2f;
    /// <summary>The vault only takes Epic and Legendary finds.</summary>
    public const Rarity VaultMinRarity = Rarity.Epic;
    static readonly float[] showcaseRep = { 0f, 0f, 6f, 12f, 24f };  // Common, Uncommon, Rare, Epic, Legendary
    /// <summary>Showcase Rep per midnight for a find of this rarity in a fancy piece: Rare +6, Epic +12, Legendary +24.</summary>
    public static float ShowcaseRep(Rarity r) => showcaseRep[Mathf.Clamp((int)r, 0, showcaseRep.Length - 1)];

    // ------------------------------------------------------------------ MAGNUS (§6.3: a career path, see MagnusCareer)
    // Step 1 "Magnus Gets a Job" is magnus_brain L1: it brings the rail to the sort table, taped kitchen tongs (5 kg) and
    // Brain Module 1. From that first step he picks up bags within reach (MagnusReach: on or within 2 m of the table,
    // which includes the Bag Belt's queue rack; never the dump pile), rips them and sorts recyclables + junk. Later steps
    // each add one thing: rail motor (speed), lobster pincer = gripper L1 (25 kg items + HEAVY bags), googly eyes =
    // vision L1 (treasure flags), brain L2 (treasure), monocle = vision L2, brain L3 (hazards), quad-claw = gripper L2
    // (speed), brain L4 (24 h).

    public struct BrainTier
    {
        public float itemsPerMin, accuracy, startHour, endHour;
        public bool recyclablesAndJunk, valuables, hazards;
    }

    static readonly BrainTier[] brainTiers =
    {
        new BrainTier(),
        new BrainTier { itemsPerMin = 8f,  accuracy = 0.85f,  startHour = 6f, endHour = 22f, recyclablesAndJunk = true },
        new BrainTier { itemsPerMin = 12f, accuracy = 0.92f,  startHour = 6f, endHour = 22f, recyclablesAndJunk = true, valuables = true },
        new BrainTier { itemsPerMin = 18f, accuracy = 0.96f,  startHour = 6f, endHour = 22f, recyclablesAndJunk = true, valuables = true, hazards = true },
        new BrainTier { itemsPerMin = 30f, accuracy = 0.995f, startHour = 0f, endHour = 24f, recyclablesAndJunk = true, valuables = true, hazards = true },
    };

    /// <summary>magnus_brain 0..4 (0 = no job yet). Hours are in-game hours of day; 0..24 = around the clock.</summary>
    public static BrainTier Brain => brainTiers[L(ShopIds.MagnusBrain, 4)];

    /// <summary>Brain tier for a given magnus_brain level (Scrapazon / career text).</summary>
    public static BrainTier BrainAt(int level) => brainTiers[Mathf.Clamp(level, 0, brainTiers.Length - 1)];

    /// <summary>He has the job (magnus_brain L1): the rail to the Sort Stop, the tongs and a brain.</summary>
    public static bool MagnusEmployed => Level(ShopIds.MagnusBrain) >= 1;

    /// <summary>The rail branch to the sort station exists (it comes with the job).</summary>
    public static bool MagnusHasRailBranch => MagnusEmployed;

    /// <summary>Rail motor work-speed factor (also doubles the commute: MagnusTrack.motorSpeed).</summary>
    public const float RailMotorSpeed = 1.25f;
    /// <summary>Quad-claw (magnus_gripper L2) work-speed factor.</summary>
    public const float QuadClawSpeed = 1.4f;

    /// <summary>Speed contract multiplier: rail motor ×1.25 × quad-claw (gripper L2) ×1.4. (The conveyor no longer speeds
    /// him up: its old Twin Turbo level is the Junk Diverter now.)</summary>
    public static float MagnusSpeedMult => MagnusSpeedMultAt(Level(ShopIds.MagnusRailMotor), Level(ShopIds.MagnusGripper));

    public static float MagnusSpeedMultAt(int railMotor, int gripper)
    {
        float m = 1f;
        if (railMotor >= 1) m *= RailMotorSpeed;
        if (gripper >= 2) m *= QuadClawSpeed;
        return m;
    }

    /// <summary>Heaviest table item Magnus can pick: 0 without the job, 5 kg with the job's kitchen tongs, 25 kg with the
    /// lobster pincer (gripper L1+). Heavier = LEFTOVERS.</summary>
    public static float GripperMaxKg => GripperMaxKgAt(Level(ShopIds.MagnusBrain), Level(ShopIds.MagnusGripper));
    public static float GripperMaxKgAt(int brain, int gripper) => brain < 1 ? 0f : gripper >= 1 ? 25f : 5f;

    /// <summary>From the job on he picks up bags within reach (MagnusReach) and rips them himself.</summary>
    public static bool MagnusRipsBags => MagnusEmployed;

    /// <summary>Heavy bags (the 2-person-lift tell) need the lobster pincer (gripper L1+); before it he leaves them for you.</summary>
    public static bool MagnusHandlesHeavyBags => MagnusEmployed && Level(ShopIds.MagnusGripper) >= 1;

    static readonly float[] railSpeed = { 0.7f, 1.4f };
    /// <summary>Rail speed (m/s) by magnus_rail_motor 0..1.</summary>
    public static float RailSpeedAt(int railMotor) => railSpeed[Mathf.Clamp(railMotor, 0, railSpeed.Length - 1)];
    /// <summary>Share of treasure bags flagged by magnus_vision 0..2.</summary>
    public static float VisionDetectAt(int vision) => visionDetect[Mathf.Clamp(vision, 0, visionDetect.Length - 1)];
    /// <summary>ebag realised-price bonus from the Appraisal Monocle (magnus_vision L2).</summary>
    public static float MonocleEbaeBonusAt(int vision) => vision >= 2 ? 0.03f : 0f;

    static readonly float[] visionDetect = { 0f, 0.6f, 1f };
    /// <summary>Share of treasure-bearing pile bags Magnus flags: 0 / 0.6 / 1.0 (roll with <see cref="DetectRoll"/>).</summary>
    public static float VisionDetect => visionDetect[L(ShopIds.MagnusVision, 2)];

    /// <summary>
    /// Chess tier 0..11: every Magnus career step adds one, and the two big ones add two (the job brings a rail, tongs
    /// and a brain; the Grandmaster Module is the chess chip) = rail (0-1) + gripper (0-2) + vision (0-2) + brain (0-4)
    /// + 1 with the job + 1 with the Grandmaster Module. Old saves' higher rail/gripper levels clamp.
    /// </summary>
    public static int MagnusChessTier => ChessTierFor(Level(ShopIds.MagnusRailMotor), Level(ShopIds.MagnusGripper),
                                                      Level(ShopIds.MagnusVision), Level(ShopIds.MagnusBrain));

    /// <summary><see cref="MagnusChessTier"/> for explicit levels (tests, the career page's "next step" preview).</summary>
    public static int ChessTierFor(int rail, int gripper, int vision, int brain)
    {
        rail = Mathf.Clamp(rail, 0, 1);
        gripper = Mathf.Clamp(gripper, 0, 2);
        vision = Mathf.Clamp(vision, 0, 2);
        brain = Mathf.Clamp(brain, 0, 4);
        return Mathf.Min(11, rail + gripper + vision + brain + (brain >= 1 ? 1 : 0) + (brain >= 4 ? 1 : 0));
    }

    public struct ChessTier { public float thinkSeconds; public int maxDepth; /* 0 = uncapped */ public float blunder; public string label; }

    static readonly ChessTier[] chessTiers =
    {
        new ChessTier { thinkSeconds = 0.05f, maxDepth = 1, blunder = 0.40f,  label = "Factory Settings (~300 Elo)" },
        new ChessTier { thinkSeconds = 0.08f, maxDepth = 2, blunder = 0.30f,  label = "~450 Elo" },
        new ChessTier { thinkSeconds = 0.12f, maxDepth = 2, blunder = 0.22f,  label = "~600 Elo" },
        new ChessTier { thinkSeconds = 0.15f, maxDepth = 3, blunder = 0.16f,  label = "~800 Elo" },
        new ChessTier { thinkSeconds = 0.20f, maxDepth = 3, blunder = 0.11f,  label = "~1000 Elo" },
        new ChessTier { thinkSeconds = 0.30f, maxDepth = 4, blunder = 0.07f,  label = "~1200 Elo" },
        new ChessTier { thinkSeconds = 0.40f, maxDepth = 4, blunder = 0.045f, label = "~1400 Elo" },
        new ChessTier { thinkSeconds = 0.60f, maxDepth = 5, blunder = 0.025f, label = "~1600 Elo" },
        new ChessTier { thinkSeconds = 0.80f, maxDepth = 6, blunder = 0.012f, label = "~1800 Elo" },
        new ChessTier { thinkSeconds = 1.00f, maxDepth = 7, blunder = 0.005f, label = "~2000 Elo" },
        new ChessTier { thinkSeconds = 1.50f, maxDepth = 0, blunder = 0.002f, label = "~2200 Elo" },
        new ChessTier { thinkSeconds = 2.50f, maxDepth = 0, blunder = 0f,     label = "MAGNUS PRIME" },
    };

    /// <summary>Chess strength for tier 0..11 (clamped).</summary>
    public static ChessTier Chess(int tier) => chessTiers[Mathf.Clamp(tier, 0, chessTiers.Length - 1)];

    // ------------------------------------------------------------------ PERSONAL GEAR (§6.4)
    // Printed in SolidWaste 3D's blueprint tree (CallingCatalog) since Oct 2026, no longer sold on Scrapazon. The ids,
    // levels and numbers are unchanged: a built node raises the same upgrade level a purchase used to.

    /// <summary>The top level this section has numbers for, per gear id (0 = not personal gear). The Callings tree may only
    /// offer these levels as buildable (CallingsTests pins the two together); raise one here when a new level's effect ships.</summary>
    public static int GearMaxLevel(string id)
    {
        switch (id)
        {
            case ShopIds.WorkGloves: return 3;
            case ShopIds.WorkBoots: return 2;
            case ShopIds.Headlamp: return 2;
            case ShopIds.TreasureWand: return 2;
            case ShopIds.RabiesVaccine: return 2;
            case ShopIds.HardHat: return 1;
            case ShopIds.GrabberPole: return 3; // 2-3: longer reach, the yank, two bags (HaulerTuning)
            default: return 0;
        }
    }

    static readonly float[] carrySlow = { 1f, 0.5f, 0.5f, 0f };
    /// <summary>Multiplier on TrashCarrier.maxSlowdown (0.45): 1 / 0.5 / 0.5 / 0 by work_gloves.</summary>
    public static float CarrySlowdownMult => carrySlow[L(ShopIds.WorkGloves, 3)];
    static readonly int[] backSack = { 0, 0, 1, 2 };
    /// <summary>Extra bags on your back: 0 / 0 / 1 / 2 by work_gloves.</summary>
    public static int BackSackSlots => backSack[L(ShopIds.WorkGloves, 3)];
    /// <summary>TrashCarrier.maxThrowSpeed: 14 stock, 15.5 m/s from gloves L1.</summary>
    public static float MaxThrowSpeed => Level(ShopIds.WorkGloves) >= 1 ? 15.5f : 14f;

    static readonly float[] moveMult = { 1f, 1.1f, 1.2f };
    /// <summary>Walk + sprint multiplier: 1 / 1.1 / 1.2 by work_boots.</summary>
    public static float MoveSpeedMult => moveMult[L(ShopIds.WorkBoots, 2)];

    /// <summary>Night UV glow chance for valuable-bearing curb bags: 0.35 with headlamp L2.</summary>
    public static float UvDetect => Level(ShopIds.Headlamp) >= 2 ? 0.35f : 0f;
    static readonly float[] wandDetect = { 0f, 0.45f, 0.80f };
    /// <summary>Wand beep chance for a valuable-bearing bag: 0 / 0.45 / 0.80 (never a false positive).</summary>
    public static float WandDetect => wandDetect[L(ShopIds.TreasureWand, 2)];

    static readonly float[] infectionResist = { 0f, 0.5f, 1f };
    /// <summary>Value for <see cref="GameSession.InfectionResist01"/>: 0 / 0.5 / 1.0 by rabies_vaccine.</summary>
    public static float InfectionResist => infectionResist[L(ShopIds.RabiesVaccine, 2)];

    /// <summary>Critter bite/bonk knockback distance ×0.5 with the hard hat.</summary>
    public static float BonkKnockbackMult => Level(ShopIds.HardHat) >= 1 ? 0.5f : 1f;
    /// <summary>Chance you keep holding your bag on a bonk: 0.5 with the hard hat.</summary>
    public static float KeepBagOnBonk => Level(ShopIds.HardHat) >= 1 ? 0.5f : 0f;

    // ------------------------------------------------------------------ BUSINESS (§6.5)

    static readonly float[] ebaeFee = { Economy.EbaeFeeFraction, 0.05f, 0.03f, 0.01f };
    /// <summary>ebag fee fraction: 0.08 / 0.05 / 0.03 / 0.01 by ebae_power_seller.</summary>
    public static float EbaeFee => ebaeFee[L(ShopIds.EbaePowerSeller, 3)];
    public static int EbaeExtraBuyers => Level(ShopIds.EbaePowerSeller) >= 1 ? 1 : 0;
    /// <summary>Offer arrival speed multiplier: ×2 from L2.</summary>
    public static float EbaeOfferSpeed => Level(ShopIds.EbaePowerSeller) >= 2 ? 2f : 1f;
    /// <summary>Auto-accept offers at or above this fraction of appraisal (L3: 0.97); 0 = off.</summary>
    public static float EbaeAutoAccept => Level(ShopIds.EbaePowerSeller) >= 3 ? 0.97f : 0f;

    // ------------------------------------------------------------------ CONSUMABLES (§6.6)

    // GameSession stamps (GameClock.TotalHours the effect ends) written by A when a gnome is used.
    const string LuckyGnomeStamp = "lucky_gnome_until", GoldenGnomeStamp = "golden_gnome_until";
    const double GnomeHours = 24.0;

    static bool StampActive(string key)
    {
        double until = GameSession.GetStamp(key);
        if (until < 0) return false;
        double left = until - GameClock.TotalHours;
        // The clock restarts each server session (known save gap): a stamp more than one gnome-day ahead is stale.
        return left > 0 && left <= GnomeHours + 0.01;
    }

    /// <summary>Gnome valuable-chance multiplier for bags put out in a district of this tier: lucky gnome ×1.25 (tiers 1-2),
    /// golden gnome ×1.40 (all tiers); both multiply. Rookie Luck is D's, not here.</summary>
    public static float ValuableChanceMult(int districtTier)
    {
        float m = 1f;
        if (districtTier <= 2 && StampActive(LuckyGnomeStamp)) m *= 1.25f;
        if (StampActive(GoldenGnomeStamp)) m *= 1.40f;
        return m;
    }

    // ------------------------------------------------------------------ detection

    /// <summary>Deterministic per-bag detection roll (same answer on every machine, every time):
    /// (StableHash(bagId + salt) &amp; 0xFFFF) / 65535 &lt; chance. chance ≥ 1 always passes, ≤ 0 never does.</summary>
    public static bool DetectRoll(string bagId, string salt, float chance)
    {
        if (chance <= 0f) return false;
        if (chance >= 1f) return true;
        return (PickupVisuals.StableHash((bagId ?? "") + salt) & 0xFFFF) / 65535f < chance;
    }
}

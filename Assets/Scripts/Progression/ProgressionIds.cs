// Progression ids and Rep numbers (Docs/Progression.md §9.2, §4.1). STEP 0 CONTRACT: builders B, C and D compile
// against these. Add members freely; never rename or change a value without telling every builder.

/// <summary>Rep source ids (the <c>repId</c> on <see cref="GameSession.RepChanged"/> and <see cref="Ledger.Rep"/>).</summary>
public static class RepIds
{
    public const string BagOnTime = "bag_ontime", BagLate = "bag_late", CurbClean = "curb_clean",
        ItemSorted = "item_sorted", Contamination = "contamination", TreasureUncommon = "treasure_uncommon",
        TreasureRare = "treasure_rare", TreasureEpic = "treasure_epic", TreasureLegendary = "treasure_legendary",
        CleanStreets = "clean_streets", NeglectedCurb = "neglected_curb", Shift = "shift", WorkOrder = "work_order",
        ChessWin = "chess_win", TrophyShelf = "trophy_shelf";

    // WorldLife (Docs/WorldLife.md §9.1; W0, applied by Progression.OnNeighborIncident from NeighborIncidentFact).
    public const string NeighborIncident = "neighbor_incident", NoiseComplaint = "noise_complaint", Neighborly = "neighborly";
}

/// <summary>
/// The §4.1 Rep table as base values (before the district multiplier that <see cref="Ledger.Rep"/> applies).
/// Only builder A turns facts into Rep; nobody else hard-codes these.
/// </summary>
public static class RepValues
{
    public const float BagOnTime = 3f;              // curb bag into the hopper within its district's on-time window
    public const float BagLate = 1f;                // a late bag (any age)
    public const float CurbClean = 2f;              // the house's last curb bag was collected
    public const float ItemSorted = 0.5f;           // correct bin (Magnus counts; CRUSH ALL gives nothing)
    public const float Contamination = -2f;         // wrong bin (Magnus's misses too)
    public const float TreasureUncommon = 5f;       // valuable into the KEEP crate, by rarity
    public const float TreasureRare = 12f;
    public const float TreasureEpic = 25f;
    public const float TreasureLegendary = 50f;
    public const float CleanStreets = 120f;         // × clean01² per unlocked district at midnight
    public const float NeglectedCurb = -3f;         // per neglected curb at midnight
    public const float Shift = 150f;                // (top) at midnight, if ≥ ShiftMinBags went into the truck that day
    public const int ShiftMinBags = 5;
    public const float WorkOrder = 40f;             // (top) per completed Work Order
    public const float ChessWin = 25f;              // × max(1, chess tier); once per in-game day
    public const float TrophyShelfL1 = 40f;         // per midnight with trophy_shelf L1
    public const float TrophyShelfL2 = 160f;        // per midnight with trophy_shelf L2 (museum wing)
    public const float CrewShare = 0.5f;            // a raccoon crew's bags earn half of bag_ontime / bag_late

    // WorldLife (Docs/WorldLife.md §9.1). Base values: Ledger.Rep applies the district multiplier; never escalated.
    public const float NeighborFlattened = -4f;     // a resident / pedestrian flattened by your truck
    public const float NeighborCarShunt = -3f;      // a car shunted (moving or parked)
    public const float NeighborMinor = -1f;         // a dog near miss, or a thrown-item flop
    public const float NoiseComplaint = -1f;        // a night honk near houses (22:00–06:00)
    public const int NoiseComplaintsPerNight = 3;   // ...at most this many per night per district
    public const float Neighborly = 1f;             // a resident handed you their bag ("Take bag")
    public const int NeighborlyPerDay = 10;         // ...at most this many per in-game day
}

/// <summary>Cash ledger ids (the <c>id</c> on <see cref="Ledger.Credit"/> / <see cref="Ledger.Charge"/> / <see cref="Ledger.Spend"/>).</summary>
public static class LedgerIds
{
    public const string PickupFee = "pickup_fee", CurbBonus = "curb_bonus", Recycle = "recycle",
        Hazard = "hazard", Scrap = "scrap", LazyCrush = "lazy_crush", Contamination = "contamination",
        HazardFine = "hazard_fine", CleanStreets = "clean_streets", NeglectFine = "neglect_fine", Landfill = "landfill",
        WorkOrder = "work_order", Ebae = "ebae", Pawn = "pawn",
        Hospital = "hospital", Tow = "tow", Style = "style", Purchase = "purchase";

    // WorldLife (Docs/WorldLife.md §9.1): flattened residents / shunted cars, and the nightly City Works invoice for props.
    public const string NeighborDamages = "neighbor_damages", CityWorks = "city_works";
}

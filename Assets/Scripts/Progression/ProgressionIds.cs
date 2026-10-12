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
    // Pace pass 2026-10-12 (Docs/Progression.md §3.6): Rep ×3-4 so one full load of the Curb Pup (about 17 bags, 150 items) earns
    // rank 2 (350 Rep). The old values are in the comments.
    public const float BagOnTime = 12f;             // curb bag into the hopper within its district's on-time window (was 3)
    public const float BagLate = 8f;                // a late bag (any age) (was 1: the messy start's old bags are all late)
    public const float CurbClean = 10f;             // the house's last curb bag was collected (was 2)
    public const float ItemSorted = 3f;             // correct bin (Magnus counts; CRUSH ALL gives nothing) (was 0.5)
    public const float Contamination = -5f;         // wrong bin (Magnus's misses too) (was -2)
    public const float TreasureUncommon = 15f;      // valuable into the KEEP crate, by rarity (was 5 / 12 / 25 / 50)
    public const float TreasureRare = 36f;
    public const float TreasureEpic = 75f;
    public const float TreasureLegendary = 150f;
    public const float CleanStreets = 360f;         // × clean01² per unlocked district at midnight (was 120)
    public const float NeglectedCurb = -6f;         // per neglected curb at midnight (was -3)
    public const float Shift = 450f;                // (top) at midnight, if ≥ ShiftMinBags went into the truck that day (was 150)
    public const int ShiftMinBags = 5;
    public const float WorkOrder = 120f;            // (top) per completed Work Order (was 40)
    public const float ChessWin = 75f;              // × max(1, chess tier); once per in-game day (was 25)
    public const float TrophyShelfL1 = 120f;        // per midnight with trophy_shelf L1 (was 40)
    public const float TrophyShelfL2 = 480f;        // per midnight with trophy_shelf L2 (museum wing) (was 160)
    public const float CrewShare = 0.5f;            // a raccoon crew's bags earn half of bag_ontime / bag_late

    // WorldLife (Docs/WorldLife.md §9.1). Base values: Ledger.Rep applies the district multiplier; never escalated.
    public const float NeighborFlattened = -8f;     // a resident / pedestrian flattened by your truck (was -4)
    public const float NeighborCarShunt = -6f;      // a car shunted (moving or parked) (was -3)
    public const float NeighborMinor = -2f;         // a dog near miss, or a thrown-item flop (was -1)
    public const float NoiseComplaint = -2f;        // a night honk near houses (22:00–06:00) (was -1)
    public const int NoiseComplaintsPerNight = 3;   // ...at most this many per night per district
    public const float Neighborly = 3f;             // a resident handed you their bag ("Take bag") (was 1)
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

    // Passing out drunk, solo: the cab back to the depot (Docs/Casino.md §5).
    public const string CabFare = "cab_fare";

    // The Lucky Raccoon (Docs/Casino.md §4, §5): stakes charged by the host, winnings credited by the host, drinks at the bar.
    public const string CasinoWager = "casino_wager", CasinoPayout = "casino_payout", BarTab = "bar_tab";

    /// <summary>A night out, not the business: stakes, winnings, drinks and the cab home. These postings stay out of the
    /// day's "Net" (so they never price Work Orders), out of lifetime earnings and out of payroll shares; the shift report
    /// lists them on lines of their own (<see cref="DailyStats.MoveLeisure"/>).</summary>
    public static bool IsLeisure(string id) => id == CasinoWager || id == CasinoPayout || id == BarTab || id == CabFare;

    // WorldLife (Docs/WorldLife.md §9.1): flattened residents / shunted cars, and the nightly City Works invoice for props.
    public const string NeighborDamages = "neighbor_damages", CityWorks = "city_works";
}

using System.Collections.Generic;

public static partial class GameSession
{
    static WorkshopState workshop = new WorkshopState();
    static readonly HashSet<int> workshopWanted = new HashSet<int>();
    /// <summary>A copy: live edits must pass through the workshop's authoritative transaction.</summary>
    public static WorkshopState WorkshopSave
    {
        get {
            var copy = workshop.Clone();
            copy.displays.RemoveAll(pin => {
                var item = Workshop.OwnedItem(pin.uid); return item == null || item.defId != pin.defId;
            });
            return copy;
        }
    }
    /// <summary>This business is hunting for the item with this catalog number (<see cref="Workshop.CatalogNumber"/>). Cheap:
    /// the HUD asks every frame.</summary>
    public static bool WorkshopWants(int catalogNumber) => catalogNumber > 0 && workshopWanted.Contains(catalogNumber);
    /// <summary>How many things this business is hunting for.</summary>
    public static int WorkshopWantedCount => workshopWanted.Count;
    /// <summary>This business has the SolidWaste 3D disk in its laptop (the print tab exists). Cheap: no copy.</summary>
    public static bool WorkshopDisk => workshop.DiskInstalled;
    /// <summary>This business's Monument tier (0 = none). Cheap: no copy.</summary>
    public static int WorkshopMonument => workshop.monument;
    static void RebuildWanted() { workshopWanted.Clear(); foreach (int number in workshop.PinList()) workshopWanted.Add(number); }

    internal static void SetWorkshopSave(WorkshopState state)
    {
        if (state == null || !state.Valid()) return;
        bool hadDisk = workshop.DiskInstalled;
        workshop = state.Clone(); RebuildWanted();
        // Callings drive ordinary upgrade levels (UpgradeEffects reads them like any gear), and an installed decor piece is
        // an owned Scrapazon product. Raise only: nothing a save owns is ever taken away, whatever this state says.
        foreach (var raise in workshop.CallingUpgrades(GetUpgradeLevel)) SetUpgradeLevel(raise.Key, raise.Value);
        NotifyChanged(); SaveSoon();
        if (!hadDisk && workshop.DiskInstalled) Workshop.RaiseDiskInstalled(); // the floppy just went into the laptop
    }
    /// <summary>
    /// The load path (no events). Returns the cents the retired material bank converts to, which the caller adds to the
    /// balance the save loads with (<see cref="WorkshopBankRefund"/>): 0 for every save written since, and for a new business.
    /// </summary>
    static long LoadWorkshop(WorkshopState state)
    {
        long refund = 0;
        // before validation: a valid state has no bank and no bench sculptures (those become Monument tiers)
        if (state != null) { state = state.Clone(); refund = state.RetireBank(); state.RetireSculptures(); }
        // A stale display pin or a bad sign colour is dropped on its own: it must not take the Monument with it.
        if (state != null && !state.ValidIgnoringCallings()) state.RepairDecor();
        // Callings levels are repaired, not rejected: a bad entry must never throw the business's workshop away.
        if (state != null && state.ValidIgnoringCallings()) { workshop = state; workshop.NormalizeCallings(); }
        else workshop = new WorkshopState();
        // Gear this save already owns shows as printed (free), printed blueprints are owned gear, decor bought on Scrapazon
        // is installed and installed decor is owned. A save that printed anything before the disk existed has its disk.
        // All raise only, all idempotent; a new save passes through unchanged.
        workshop.SeedCallings(GetUpgradeLevel);
        workshop.NormalizePrinter();
        foreach (string decor in workshop.DecorToInstall(GetUpgradeLevel)) workshop.InstallDecor(decor, out _);
        foreach (var raise in workshop.CallingUpgrades(GetUpgradeLevel)) upgradeLevels[raise.Key] = raise.Value;
        if (refund > 0) counters[WorkshopBankRefund.PendingCounter] = (int)System.Math.Min(int.MaxValue, refund);
        RebuildWanted();
        return refund;
    }

    // ---------- kept parts (non-valuables in the inventory) ----------
    /// <summary>How many kept NON-valuables the inventory holds. Valuables are never capped.</summary>
    public const int PartsCap = 200;
    public static int PartCount
    {
        get { int n = 0; foreach (var item in Inventory) if (Workshop.IsPart(item)) n++; return n; }
    }
    /// <summary>The parts shelf is full: the KEEP crate hands the next hand-thrown part back (Magnus and hired crew deliveries
    /// still land: nothing a robot or an employee kept for you is ever dropped).</summary>
    public static bool PartsFull => PartCount >= PartsCap;
    /// <summary>Kept valuables only (the HUD's "Finds" chip, displays).</summary>
    public static int FindCount
    {
        get { int n = 0; foreach (var item in Inventory) if (item != null && !Workshop.IsPart(item)) n++; return n; }
    }
}

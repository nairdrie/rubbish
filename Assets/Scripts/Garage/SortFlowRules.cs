using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Bag Belt (conveyor_belt, Docs/Progression.md §6.2). It carries bags from the bay pile to the queue rack beside the
/// sorting table and NEVER opens one. L1 Bag Belt: normal bags only (heavy ones are rejected and stay on the pile).
/// L2 Heavy-Duty Motor: heavy bags too. L3 Junk Diverter: each bag is shaken over a grate on the way in (JunkDiverter).
/// Pure rules: no state, the same answer on every machine.
/// </summary>
public static class ConveyorRules
{
    public const int BagBelt = 1, HeavyMotor = 2, Diverter = 3;
    /// <summary>Bags that wait in the rack beside the table; the rest of the queue backs up along the belt.</summary>
    public const int QueueSlots = 2;
    /// <summary>Seconds between bags leaving the pile.</summary>
    public const float PullSeconds = 2f;

    /// <summary>The "heavy" tell (2-person lift, plops when thrown): the only thing the L1 belt refuses.</summary>
    public static bool IsHeavy(BagContents c) => c != null && c.sackVariant >= 0 && BagTells.Of(c.bagId) == BagTell.Heavy;

    public static bool Accepts(int level, bool heavy) => level >= BagBelt && (!heavy || level >= HeavyMotor);

    /// <summary>Whole sacks only: loose cargo (sackVariant −1) is not a bag.</summary>
    public static bool Accepts(int level, BagContents c) => c != null && c.sackVariant >= 0 && Accepts(level, IsHeavy(c));

    public static bool Diverts(int level) => level >= Diverter;
}

/// <summary>
/// Junk Diverter (conveyor_belt L3). On the way in the belt shakes each bag over a grate: loose, obvious junk falls
/// through and goes straight to the junk bin (the compactor's hopper), so the bag reaches the table with fewer items.
/// SHED = category Junk AND at most <see cref="GrateMaxKg"/> (it has to fit through the grate). Never a valuable, a
/// hazard or a recyclable, so what is shed says nothing about treasure. The bag always keeps at least one item (if all
/// of it is small junk, the heaviest piece stays in). A function of the bag's contents only: the same bag sheds the same
/// items on every machine, every time, and a second pass sheds nothing new that the first would not have.
/// The shed items are the very same ItemInstances (uid and mass unchanged), handed to the compactor exactly like junk
/// Magnus drops in, so they pay the same per-kg rate through the same Ledger path of the lot's business.
/// </summary>
public static class JunkDiverter
{
    public const float GrateMaxKg = 4f;

    public static bool Sheds(ItemInstance item)
    {
        var def = item != null ? item.Def : null;
        return def != null && def.category == ItemCategory.Junk && item.massKg <= GrateMaxKg;
    }

    /// <summary>Indices (ascending) of the items this bag sheds.</summary>
    public static List<int> ShedIndices(BagContents c)
    {
        var shed = new List<int>();
        if (c == null || c.items == null || c.sackVariant < 0) return shed;
        for (int i = 0; i < c.items.Count; i++) if (Sheds(c.items[i])) shed.Add(i);
        if (shed.Count > 0 && shed.Count == c.items.Count)
        {
            // Everything would fall through: the heaviest piece (first on a tie) jams the grate and stays in the bag.
            int keep = shed[0];
            for (int k = 1; k < shed.Count; k++) if (c.items[shed[k]].massKg > c.items[keep].massKg) keep = shed[k];
            shed.Remove(keep);
        }
        return shed;
    }

    /// <summary>
    /// Split a bag: <paramref name="kept"/> is a new BagContents (same bag id, address, sack and sack mass, so its tell,
    /// vision flag and look do not change) with the items that stay; the return value is what fell through the grate.
    /// The original is not modified. Nothing shed = <paramref name="kept"/> is the original and the list is empty.
    /// </summary>
    public static List<ItemInstance> Split(BagContents c, out BagContents kept)
    {
        var fell = new List<ItemInstance>();
        kept = c;
        var shed = ShedIndices(c);
        if (shed.Count == 0) return fell;
        kept = new BagContents { bagId = c.bagId, foundAt = c.foundAt, bagMassKg = c.bagMassKg, sackVariant = c.sackVariant };
        int next = 0;
        for (int i = 0; i < c.items.Count; i++)
        {
            if (next < shed.Count && shed[next] == i) { fell.Add(c.items[i]); next++; }
            else kept.items.Add(c.items[i]);
        }
        return fell;
    }
}

/// <summary>
/// What Magnus may pick up as a BAG (Docs/Progression.md §6.3). From career step 1 he rips bags himself, but only ones
/// within reach: on or within <see cref="ReachMetres"/> of the sorting table's top (bags you carried over, and the Bag
/// Belt's queue rack, which stands inside that band). He can never take from the dump pile. Heavy bags need the Lobster
/// Pincer (magnus_gripper L1+); before it he leaves them for you.
/// </summary>
public static class MagnusReach
{
    public const float ReachMetres = 2f;

    /// <summary>Flat distance (m) from a point to a rectangle (centre + half extents in the rectangle's own axes).</summary>
    public static float DistanceToRect(Vector2 localPoint, Vector2 halfExtents)
    {
        float dx = Mathf.Max(0f, Mathf.Abs(localPoint.x) - halfExtents.x);
        float dy = Mathf.Max(0f, Mathf.Abs(localPoint.y) - halfExtents.y);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Is a bag at this table-local point (x across, y toward the bins) within his reach, and not on the pile?</summary>
    public static bool InReach(Vector2 tableLocal, Vector2 tableHalfExtents, bool onDumpPile) =>
        !onDumpPile && DistanceToRect(tableLocal, tableHalfExtents) <= ReachMetres;

    /// <summary>May he rip this bag at these career levels?</summary>
    public static bool MayRip(int brainLevel, int gripperLevel, bool heavyBag) =>
        brainLevel >= 1 && (!heavyBag || gripperLevel >= 1);

    /// <summary>A bag he would take right now.</summary>
    public static bool MayTake(int brainLevel, int gripperLevel, bool heavyBag, Vector2 tableLocal, Vector2 tableHalfExtents, bool onDumpPile) =>
        MayRip(brainLevel, gripperLevel, heavyBag) && InReach(tableLocal, tableHalfExtents, onDumpPile);
}

/// <summary>
/// TIP IT lives on the truck (Docs/Progression.md §6.1): the driver holds the tip key and the hopper empties behind the
/// truck wherever it stands. The host decides with these rules (the same cargo the bay lever used to move: the truck's
/// business's cargo, or GameSession.TruckBags for the host's own truck).
/// </summary>
public static class DumpRules
{
    public const float StockSeconds = 10f, AutoTipperSeconds = 3f;
    /// <summary>The truck must be slower than this (flat m/s) to start, and to keep dumping.</summary>
    public const float MaxSpeed = 1f;
    /// <summary>Seconds the driver holds the key before the dump starts (a slip of the finger must not litter a street).</summary>
    public const float HoldSeconds = 0.6f;
    /// <summary>Beeps before the first bag leaves; part of the total time.</summary>
    public const float LeadSeconds = 0.6f;
    public const float MinBagInterval = 0.03f;
    /// <summary>Ground lower than this is the river or the sea.</summary>
    public const float WaterlineY = -0.9f;
    /// <summary>The pile lands this far behind the rear bumper (truck metres), and must find ground within this drop.</summary>
    public const float PileBehind = 2.6f, MaxGroundDrop = 3.5f;

    public enum Where { Outdoors, OwnBay, ForeignBay }
    public enum Verdict { Ok, NotDriver, Busy, Empty, Moving, NotYourShop, NoGround, Water, DepotFull }

    /// <summary>Dump time for the truck's business: 10 s, or 3 s with the Auto-Tipper (auto_tipper L1).</summary>
    public static float Seconds(int autoTipperLevel) => autoTipperLevel >= 1 ? AutoTipperSeconds : StockSeconds;

    /// <summary>Seconds between bags so the whole load takes <see cref="Seconds"/> however many bags there are.</summary>
    public static float BagInterval(int autoTipperLevel, int bags) =>
        Mathf.Max(MinBagInterval, (Seconds(autoTipperLevel) - LeadSeconds) / Mathf.Max(1, bags));

    /// <summary>
    /// May this dump start? <paramref name="where"/>: outdoors, inside a shop the driver may work in (owner or on-shift
    /// employee: Permissions.CanWorkIn), or inside somebody else's. Outdoors needs solid, dry ground behind the truck;
    /// indoors needs room on the tipping floor for the next bag (DepotBacklog).
    /// </summary>
    public static Verdict Check(bool isDriver, bool busy, bool hasCargo, float flatSpeed, Where where,
                                bool groundBehind, float groundY, bool roomForNextBag)
    {
        if (!isDriver) return Verdict.NotDriver;
        if (busy) return Verdict.Busy;
        if (!hasCargo) return Verdict.Empty;
        if (float.IsNaN(flatSpeed) || flatSpeed > MaxSpeed) return Verdict.Moving;
        if (where == Where.ForeignBay) return Verdict.NotYourShop;
        if (where == Where.OwnBay) return roomForNextBag ? Verdict.Ok : Verdict.DepotFull;
        if (!groundBehind) return Verdict.NoGround;
        if (groundY < WaterlineY) return Verdict.Water;
        return Verdict.Ok;
    }

    /// <summary>The toast for a refusal (null for Ok and for NotDriver, which is silent).</summary>
    public static string Message(Verdict v)
    {
        switch (v)
        {
            case Verdict.Busy: return "Already tipping. Hold still!";
            case Verdict.Empty: return "Truck's empty. Go get some trash!";
            case Verdict.Moving: return "Stop the truck to tip it.";
            case Verdict.NotYourShop: return "Not your shop! Tip it in your own bay, or outside.";
            case Verdict.NoGround: return "No solid ground behind the truck. Not here.";
            case Verdict.Water: return "Not into the water! Back up onto dry land.";
            case Verdict.DepotFull: return DepotBacklog.FullMessage;
            default: return null;
        }
    }
}

/// <summary>
/// Loose bags on the street are LITTER (Docs/Progression.md §3.3): a bag with no curb record lying outdoors (a load
/// tipped in the street, a bag carried out of a shop) dirties the district it lies in exactly as if its contents were
/// lying there loose: <see cref="CityCleanliness.LitterDirt"/> per item, at least one item's worth. Curb bags are not
/// litter (their record already counts, by age); bags in hands, in a truck or inside a shop are not either.
/// </summary>
public static class StreetLitter
{
    /// <summary>Loose-item equivalents of one stray bag.</summary>
    public static int ItemEquivalents(int itemCount) => Mathf.Max(1, itemCount);

    public static float BagDirt(int itemCount) => ItemEquivalents(itemCount) * CityCleanliness.LitterDirt;

    /// <summary>Does this bag count as street litter right now?</summary>
    public static bool Counts(bool hasCurbRecord, bool atAStop, bool carriedOrConsumed, bool insideAShop) =>
        !hasCurbRecord && !atAStop && !carriedOrConsumed && !insideAShop;
}

/// <summary>
/// Pure, idempotent load migrations for reworked upgrades (the GameSession.ApplySaveJson pattern; no events, no toasts).
/// Auto-Tipper became a one-level upgrade when tipping moved onto the truck: its old level 2 ("Direct Feed") is gone.
/// A save that owns level 2 keeps level 1 and gets the level-2 price back, once: after the first load the save says
/// level 1, so a second pass changes nothing. New businesses (level 0 or 1) are untouched.
/// </summary>
public static class UpgradeMigrations
{
    /// <summary>What auto_tipper level 2 cost when it was sold.</summary>
    public const long AutoTipperL2PriceCents = 250000;

    /// <summary>Clamp auto_tipper to its one level. Returns the cents to refund (0 when nothing changed).</summary>
    public static long MigrateAutoTipper(IDictionary<string, int> levels)
    {
        if (levels == null || !levels.TryGetValue(ShopIds.AutoTipper, out int level) || level <= 1) return 0;
        levels[ShopIds.AutoTipper] = 1;
        return AutoTipperL2PriceCents;
    }
}

/// <summary>
/// Sorting positions at the table. Position 1 is the original one (Magnus parks there when he is on shift). The
/// Double-Wide table (sorting_table L2) adds position 2, at the table's left end (clear of his track): a player can sort beside Magnus, or two players
/// can sort at once. Without the upgrade it is exactly one at a time, as before.
/// </summary>
public static class SortSeats
{
    public const int SecondSeatTableLevel = 2;

    public static int Count(int sortingTableLevel) => sortingTableLevel >= SecondSeatTableLevel ? 2 : 1;

    public static bool SeatExists(int seat, int sortingTableLevel) => seat >= 0 && seat < Count(sortingTableLevel);

    /// <summary>
    /// Which position a player stepping up gets (−1 = none: wait, or shoo Magnus). <paramref name="preferred"/> is the
    /// one they walked up to. A position is unavailable while another player stands at it; position 1 also while
    /// Magnus blocks it.
    /// </summary>
    public static int Assign(int preferred, int sortingTableLevel, bool seat0Player, bool seat0Magnus, bool seat1Player)
    {
        bool free0 = !seat0Player && !seat0Magnus;
        bool free1 = SeatExists(1, sortingTableLevel) && !seat1Player;
        if (preferred == 1 && free1) return 1;
        if (free0) return 0;
        return free1 ? 1 : -1;
    }

    /// <summary>
    /// Magnus never reaches into a player's side: while a player sorts at position 2 he leaves the items on that
    /// half of the table (table-local x on the position-2 side of the line between the two positions) to them, and he
    /// never takes the item a player is pointing at.
    /// </summary>
    public static bool MagnusMayTake(float itemTableLocalX, bool secondSeatPlayer, float seatSplitLocalX, float secondSeatLocalX, bool playerAimsAtIt) =>
        !playerAimsAtIt && (!secondSeatPlayer || (itemTableLocalX - seatSplitLocalX) * (secondSeatLocalX - seatSplitLocalX) < 0f);
}

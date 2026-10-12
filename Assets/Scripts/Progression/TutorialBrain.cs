using System;
using UnityEngine;

/// <summary>What the pointer should point at (the runner turns it into a place in the world).</summary>
public enum TutorialTarget : byte { None, Truck, Hopper, GarageDoor, OldTrash, DepotDoor, Laptop, TippingFloor, SortTable, BagPile, Disk }

/// <summary>What is true right now. The runner fills it from the game; a test fills it by hand.</summary>
public struct TutorialFacts
{
    /// <summary>The world is up and the player is free to act (no menu, loading screen or card on top).</summary>
    public bool control;
    public bool gamepad;
    /// <summary>A contract is running (the HUD bar is the contract) and how many old bags it has left.</summary>
    public bool contractActive;
    public int oldBags;
    public string districtName;
    /// <summary>Inside the player's own depot (the solo garage, or their own shop in the one world).</summary>
    public bool indoors;
    public bool driving;
    public bool truckThere;
    public bool carryingBag;
    public bool inSortMode;
    public bool laptopOpen;
    public bool cargoKnown;
    public float loadKg, capacityKg;
    /// <summary>Metres to the nearest old-trash curb, the player's truck and the sorting table (negative: unknown).</summary>
    public float heapDistance, truckDistance, tableDistance;
    public bool printerInstalled;
    /// <summary>The floppy disk has been found (the card showed) in this business.</summary>
    public bool diskSeen;
}

/// <summary>What has been seen this session (the runner counts the edges), plus how long things have been going.</summary>
public struct TutorialMemory
{
    /// <summary>The contract cards were closed (read or skipped).</summary>
    public bool cardsClosed;
    /// <summary>Seated in the truck at some point.</summary>
    public bool drove;
    /// <summary>Seen out of the depot (in the town).</summary>
    public bool wentOutside;
    /// <summary>The truck has been full (or too full for another bag).</summary>
    public bool sawFull;
    /// <summary>Dumps seen inside the depot (the truck's load dropped to nothing).</summary>
    public int tipped;
    /// <summary>Bags ripped open at the table, and how many of those were not done at the station (thrown or tossed).</summary>
    public int rips, thrownRips;
    /// <summary>Items that came out of the first rip, and items put in bins since the tutorial began.</summary>
    public int itemsAtRip, sorted;
    /// <summary>Seconds the world has been up with the tutorial waiting, and seconds of the depot tour so far.</summary>
    public float controlSeconds, tourSeconds;
    /// <summary>Laptop tabs the player has clicked when asked (TutorialRules.TabsKey).</summary>
    public int tabsMask;
}

/// <summary>What the banner says and where the pointer goes.</summary>
public struct TutorialCard
{
    public string title, body, key, targetName;
    public bool urgent;
    public TutorialTarget target;
}

/// <summary>The key names a card puts in its text (keyboard or pad).</summary>
public struct TutorialKeys
{
    public string interact, door, tip, throwIt, slam, leaveStation;

    public static TutorialKeys For(bool gamepad) => gamepad
        ? new TutorialKeys { interact = "X", door = "Y", tip = "Hold B", throwIt = "Hold RT", slam = "X/RT", leaveStation = "B" }
        : new TutorialKeys { interact = "E", door = "F", tip = "Hold G", throwIt = "Hold LMB", slam = "E/LMB", leaveStation = "Q" };
}

/// <summary>
/// The tutorial's mind (Docs/Campaign.md §14.8), pure: given what is true and what has been seen, is this lesson done, and what does
/// its banner say and point at? Only the current lesson (the first one not done) is ever asked, so each rule can lean on the ones
/// before it. Every rule has a way out that needs nothing from the player but being in the right place, so nothing can wedge:
/// the walk-through is always finishable, and a player who skips ahead is simply marked as having done what they skipped.
/// </summary>
public static class TutorialBrain
{
    public const float CardsWaitSeconds = 25f;
    public const float TourStopSeconds = 4f, TourStops = 3f;
    public const float NearHeap = 26f, NearTruck = 28f, NearTable = 4.5f, FarHeap = 60f;
    public const float EmptyKg = 0.5f;

    static bool Full(in TutorialFacts f) => TruckHudRules.ShouldReturnToDepot(f.cargoKnown, f.loadKg, f.capacityKg, false);
    static bool Loaded(in TutorialFacts f) => f.cargoKnown && f.loadKg > EmptyKg;

    // ------------------------------------------------------------------ is it done?

    public static bool Done(TutorialStep step, in TutorialFacts f, in TutorialMemory m)
    {
        switch (step)
        {
            case TutorialStep.Cards:
                // no contract to explain after a while (a save with every contract won): nothing to wait for
                return m.cardsClosed || (!f.contractActive && m.controlSeconds > CardsWaitSeconds);
            case TutorialStep.Truck: return m.drove || f.driving;
            case TutorialStep.Leave: return m.wentOutside || !f.indoors;
            case TutorialStep.Collect: return m.sawFull || Full(f) || (f.indoors && Loaded(f) && m.wentOutside);
            case TutorialStep.Return: return f.indoors && Loaded(f);
            case TutorialStep.Tour:
                return m.tourSeconds >= TourStopSeconds * TourStops || m.tipped > 0 || m.rips > 0 || (!f.indoors && m.tourSeconds > 0.5f);
            case TutorialStep.Tip:
                return m.tipped > 0 || m.rips > 0 || (f.indoors && f.cargoKnown && f.loadKg <= EmptyKg);
            case TutorialStep.Carry: return m.rips > 0;
            case TutorialStep.Sort: return m.sorted >= Mathf.Clamp(m.itemsAtRip, 1, 2);
            case TutorialStep.Throw: return m.thrownRips > 0;
            case TutorialStep.Laptop:
                return TutorialRules.NextTab(m.tabsMask, TutorialRules.WalkThroughTabs) < 0;
            case TutorialStep.Disk: return f.printerInstalled;
            case TutorialStep.DiskTab: return TutorialRules.TabSeen(m.tabsMask, TutorialRules.TabPrinter);
        }
        return true;
    }

    /// <summary>The short cheer when a lesson is finished.</summary>
    public static string DoneText(TutorialStep step)
    {
        switch (step)
        {
            case TutorialStep.Truck: return "Nice ride!";
            case TutorialStep.Leave: return "Out we go!";
            case TutorialStep.Collect: return "Truck's full!";
            case TutorialStep.Return: return "Home sweet depot!";
            case TutorialStep.Tour: return "That's the depot!";
            case TutorialStep.Tip: return "Unloaded!";
            case TutorialStep.Carry: return "Bag open!";
            case TutorialStep.Sort: return "Sorted!";
            case TutorialStep.Throw: return "Nice throw!";
            case TutorialStep.Laptop: return "You know the laptop!";
            case TutorialStep.Disk: return "Disk kept!";
            case TutorialStep.DiskTab: return "SolidWaste 3D!";
        }
        return "Nice!";
    }

    // ------------------------------------------------------------------ what to say

    static string K(string key) => $"<color=#FFD740><b>{key}</b></color>";

    static TutorialCard C(string title, string body, string key = null, TutorialTarget target = TutorialTarget.None, string targetName = null, bool urgent = false) =>
        new TutorialCard { title = title, body = body, key = key, target = target, targetName = targetName, urgent = urgent };

    public static TutorialCard Card(TutorialStep step, in TutorialFacts f, in TutorialMemory m)
    {
        var k = TutorialKeys.For(f.gamepad);
        switch (step)
        {
            case TutorialStep.Truck:
                return f.truckThere
                    ? C("GET IN YOUR TRUCK", $"That's your truck: your whole business on four wheels. Walk up to it and press {K(k.interact)}.", k.interact, TutorialTarget.Truck, "Your truck")
                    : C("GET IN YOUR TRUCK", "Your truck is on its way out of the shop...", null);

            case TutorialStep.Leave:
                return f.driving
                    ? C("DRIVE OUT TO THE TOWN", $"Roll up to the garage door and press {K(k.door)}. Fair warning: the town is a bit of a mess.", k.door, TutorialTarget.GarageDoor, "Garage door")
                    : C("HOP BACK IN THE TRUCK", $"Press {K(k.interact)} next to it.", k.interact, TutorialTarget.Truck, "Your truck");

            case TutorialStep.Collect: return CollectCard(f, k);

            case TutorialStep.Return:
                return Full(f)
                    ? C("TRUCK FULL! BACK TO THE DEPOT", $"Drive home to unload. Follow the arrow to the depot door and press {K(k.door)}.", k.door, TutorialTarget.DepotDoor, "Depot", true)
                    : C("BACK TO THE DEPOT", $"Take what you have home to unload. Drive up to the depot door and press {K(k.door)}.", k.door, TutorialTarget.DepotDoor, "Depot");

            case TutorialStep.Tour:
            {
                int stop = Mathf.Clamp((int)(m.tourSeconds / TourStopSeconds), 0, (int)TourStops - 1);
                if (stop == 0) return C("WELCOME TO THE DEPOT", "Home base. The LAPTOP sells your junk and buys your upgrades.", null, TutorialTarget.Laptop, "Laptop");
                if (stop == 1) return C("THE TIPPING FLOOR", "The truck's load goes out here, in a big stinky heap.", null, TutorialTarget.TippingFloor, "Tipping floor");
                return C("THE SORTING TABLE", "Bags get ripped open here, and the finds get sorted into bins.", null, TutorialTarget.SortTable, "Sorting table");
            }

            case TutorialStep.Tip:
                if (!f.indoors) return C("BACK TO THE DEPOT", "You unload inside the depot.", k.door, TutorialTarget.DepotDoor, "Depot");
                return f.driving
                    ? C("UNLOAD THE TRUCK", $"Stay in the truck and hold {K(k.tip.Replace("Hold ", "").Trim())} to tip the whole load out onto the tipping floor.", k.tip, TutorialTarget.TippingFloor, "Tipping floor")
                    : C("UNLOAD THE TRUCK", $"Get back in your truck ({K(k.interact)}), then tip it out on the tipping floor.", k.interact, TutorialTarget.Truck, "Your truck");

            case TutorialStep.Carry:
                if (f.inSortMode)
                    return f.carryingBag
                        ? C("RIP IT OPEN", $"Press {K(k.slam)} on the table to slam the bag open.", k.slam)
                        : C("SORT A BAG", $"You need a bag first. Step away ({K(k.leaveStation)}) and pick one up from the pile.", k.leaveStation, TutorialTarget.BagPile, "Bag pile");
                if (f.driving) return C("SORT A BAG", $"Hop out of the truck ({K(k.interact)}), then pick a bag up from the pile.", k.interact, TutorialTarget.BagPile, "Bag pile");
                if (f.carryingBag)
                    return C("SORT A BAG", $"Carry it to the sorting table and press {K(k.interact)} to step up to it.", k.interact, TutorialTarget.SortTable, "Sorting table");
                return C("SORT A BAG", $"Pick a bag up from the pile with {K(k.interact)}.", k.interact, TutorialTarget.BagPile, "Bag pile");

            case TutorialStep.Sort:
                return f.inSortMode
                    ? C("SORT THE FINDS", "Pick up each item and drop it in the right bin. The signs on the bins say which is which.", null)
                    : C("SORT THE FINDS", $"Step up to the table ({K(k.interact)}) and sort what came out.", k.interact, TutorialTarget.SortTable, "Sorting table");

            case TutorialStep.Throw:
                if (f.inSortMode) return C("NOW THROW ONE", $"Step away from the table ({K(k.leaveStation)}) first.", k.leaveStation);
                if (f.driving) return C("NOW THROW ONE", $"Hop out of the truck ({K(k.interact)}) and pick up another bag.", k.interact, TutorialTarget.BagPile, "Bag pile");
                if (!f.carryingBag) return C("NOW THROW ONE", $"Pick up another bag ({K(k.interact)}). You can throw it at the table.", k.interact, TutorialTarget.BagPile, "Bag pile");
                return C("NOW THROW ONE", $"Hold {K(k.throwIt.Replace("Hold ", "").Trim())} to charge, let go to throw it at the table. Thrown bags tear themselves open!", k.throwIt, TutorialTarget.SortTable, "Sorting table");

            case TutorialStep.Laptop:
                return C("CHECK THE LAPTOP", $"Walk up to the laptop and press {K(k.interact)}. Cash, upgrades and your contract all live there.", k.interact, TutorialTarget.Laptop, "Laptop");

            case TutorialStep.Disk:
                return C("A FLOPPY DISK!", "Keep it! Click KEEP on its card, or throw it in the gold KEEP crate.", null, TutorialTarget.Disk, "Floppy disk");

            case TutorialStep.DiskTab:
                return C("NEW SOFTWARE", $"Your laptop has a new tab. Go and press {K(k.interact)} on it to see.", k.interact, TutorialTarget.Laptop, "Laptop");
        }
        return default;
    }

    static TutorialCard CollectCard(in TutorialFacts f, in TutorialKeys k)
    {
        const string head = "PICK UP THE OLD TRASH";
        float heap = f.heapDistance, truck = f.truckDistance;
        if (f.carryingBag)
        {
            return truck >= 0f && truck > NearTruck
                ? C(head, "Carry it back to your truck.", null, TutorialTarget.Hopper, "Your truck")
                : C(head, $"Walk to the back of the truck and press {K(k.interact)} to toss the bag in.", k.interact, TutorialTarget.Hopper, "Toss it in");
        }
        bool loaded = Loaded(f);
        bool heapFar = heap < 0f || heap > NearHeap;
        if (heapFar && !f.driving && truck >= 0f && truck < 14f && (heap < 0f || heap > FarHeap))
            return C("DRIVE TO THE OLD TRASH", $"Hop in your truck ({K(k.interact)}) and follow the arrow.", k.interact, TutorialTarget.Truck, "Your truck");
        if (heapFar)
        {
            string left = f.oldBags > 0 ? $"{f.oldBags} old bags are heaped on the curbs (follow the flies)." : "Heaps of old bags are on the curbs (follow the flies).";
            if (loaded) left = $"{f.loadKg:0}/{f.capacityKg:0} kg loaded. On to the next heap.";
            return C(head, left + " Follow the arrow.", null, TutorialTarget.OldTrash, "Old trash");
        }
        return loaded
            ? C(head, $"{f.loadKg:0}/{f.capacityKg:0} kg loaded. Grab another one with {K(k.interact)} until the truck is full. Watch the clean bar fill.", k.interact, TutorialTarget.OldTrash, "Old trash")
            : C(head, $"Hop out and grab a bag from the heap with {K(k.interact)}. Park close, bags are heavy.", k.interact, TutorialTarget.OldTrash, "Old trash");
    }
}

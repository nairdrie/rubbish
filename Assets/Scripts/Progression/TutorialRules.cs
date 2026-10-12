using System;

/// <summary>
/// The guided first run (Docs/Campaign.md §14.8): which lesson comes next. Pure, so a test can walk it.
/// A new save is walked through the whole job once: the goal (the contract cards), the truck, the bags, the way home, the depot,
/// the tipping floor, the sort table, the laptop; and later, the first floppy disk. What has been taught is a bit mask in the
/// save (<see cref="MaskKey"/>), so the next lesson is always "the first one not done yet" and a player who does things out of
/// order is never told to do what they already did.
/// </summary>
public enum TutorialStep : byte
{
    /// <summary>The contract cards: what the district is, the old trash, the line, the hold.</summary>
    Cards = 0,
    /// <summary>Get in your truck.</summary>
    Truck = 1,
    /// <summary>Drive out of the garage to the town (only the solo garage start has anything to do here).</summary>
    Leave = 2,
    /// <summary>Pick the old bags up and toss them in the truck until it is full.</summary>
    Collect = 3,
    /// <summary>The truck is full: go home to the depot (the loud one).</summary>
    Return = 4,
    /// <summary>First time inside the depot with a load: here is the laptop, the tipping floor, the sort table.</summary>
    Tour = 5,
    /// <summary>In the truck, hold the key, the load goes on the floor.</summary>
    Tip = 6,
    /// <summary>Pick up a bag and carry it to the sort table and rip it open.</summary>
    Carry = 7,
    /// <summary>Sort what came out of it into the right bins.</summary>
    Sort = 8,
    /// <summary>Fetch another bag and throw it at the table: it tears itself open.</summary>
    Throw = 9,
    /// <summary>The laptop, tab by tab: the player clicks each one.</summary>
    Laptop = 10,
    /// <summary>A floppy disk turned up: keep it.</summary>
    Disk = 11,
    /// <summary>The first time at the laptop after that: the new tab, and the player clicks it.</summary>
    DiskTab = 12,
}

public static class TutorialRules
{
    /// <summary>Saved counters (GameSession, the player's own business).</summary>
    public const string MaskKey = "tut_mask", OffKey = "tut_off", InitKey = "tut_init", CardsKey = "tut_cards_", TabsKey = "tut_tabs", DiskKey = "tut_disk";

    /// <summary>The numbered lessons (Truck..Laptop): "3/10" on the banner.</summary>
    public const int NumberedFirst = (int)TutorialStep.Truck, NumberedLast = (int)TutorialStep.Laptop;
    public static int NumberedCount => NumberedLast - NumberedFirst + 1;

    /// <summary>Laptop tabs the walk-through makes the player click (a bit each in <see cref="TabsKey"/>), then the one the disk adds.</summary>
    public const int TabEbae = 0, TabScrapazon = 1, TabCareer = 2, TabPrinter = 3;
    public static readonly int[] WalkThroughTabs = { TabEbae, TabScrapazon, TabCareer };

    public static bool Done(int mask, TutorialStep s) => (mask & (1 << (int)s)) != 0;
    public static int Mark(int mask, TutorialStep s) => mask | (1 << (int)s);
    public static bool IsLesson(TutorialStep s) => s >= TutorialStep.Disk;

    /// <summary>"3/10" for the numbered steps, "" for the cards and the lessons (the banner draws a star).</summary>
    public static string Badge(TutorialStep s) =>
        (int)s >= NumberedFirst && (int)s <= NumberedLast ? $"{(int)s - NumberedFirst + 1}/{NumberedCount}" : "";

    /// <summary>
    /// The lesson to teach now, or null for none: the first of the walk-through not done, then (once it is all done) the floppy
    /// disk's two when the player has found one.
    /// </summary>
    public static TutorialStep? Next(int mask, bool diskFound)
    {
        for (int i = (int)TutorialStep.Cards; i <= NumberedLast; i++)
            if (!Done(mask, (TutorialStep)i)) return (TutorialStep)i;
        if (diskFound && !Done(mask, TutorialStep.Disk)) return TutorialStep.Disk;
        if (Done(mask, TutorialStep.Disk) && !Done(mask, TutorialStep.DiskTab)) return TutorialStep.DiskTab;
        return null;
    }

    /// <summary>Every step before <paramref name="s"/> and <paramref name="s"/> itself (a player who is already past a point).</summary>
    public static int Through(TutorialStep s)
    {
        int m = 0;
        for (int i = 0; i <= (int)s; i++) m |= 1 << i;
        return m;
    }

    /// <summary>The whole walk-through (not the disk lessons): what a save that knows the game gets, so nothing is taught twice.</summary>
    public static int WalkThroughDone => Through(TutorialStep.Laptop);

    /// <summary>
    /// Is this save one that already knows the job? The tutorial is for a business that has never done it: anything it has
    /// already earned or done (a sorted item, cash, recycled weight, rank, a bought upgrade, the starter contract's progress)
    /// means it does not need telling.
    /// </summary>
    public static bool LooksExperienced(int itemsSorted, float cashEarned, float kgRecycled, int rep, int starterProgress, int truckModelLevel) =>
        itemsSorted > 0 || cashEarned > 0.01f || kgRecycled > 0.01f || rep > 0 || starterProgress > 0 || truckModelLevel > 0;

    // ------------------------------------------------------------------ the laptop's tabs

    /// <summary>One bit per laptop tab the player has been made to click (the coach marks), by tab index.</summary>
    public static bool TabSeen(int tabsMask, int tab) => tab >= 0 && tab < 31 && (tabsMask & (1 << tab)) != 0;
    public static int TabMark(int tabsMask, int tab) => tab >= 0 && tab < 31 ? tabsMask | (1 << tab) : tabsMask;

    /// <summary>The first of <paramref name="tabs"/> (in screen order) not yet clicked, or -1.</summary>
    public static int NextTab(int tabsMask, int[] tabs)
    {
        if (tabs == null) return -1;
        foreach (int t in tabs) if (!TabSeen(tabsMask, t)) return t;
        return -1;
    }
}

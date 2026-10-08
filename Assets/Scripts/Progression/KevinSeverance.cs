using System.Collections.Generic;

/// <summary>
/// Kevin's Crew (the <c>hire_kevin</c> Scrapazon upgrade: background collection for a daily wage) was removed in save v6;
/// a business's chipped raccoon crew (Callings, <see cref="RaccoonCrew"/>) is the only background collection now.
/// This is what is left of him: the load migration that gives an owner the purchase price back, and the one-time note.
/// <list type="bullet">
/// <item><see cref="Migrate"/> is a pure load migration in the style of <c>Districts.MigrateLegacyPermits</c>: it runs inside
/// <c>GameSession.ApplySaveJson</c>, so it covers the main save, every extra business slot and the business a guest brings
/// into somebody else's Town, and it never sees a brand-new business (those are not loaded from JSON).</item>
/// <item>The refund is added to the balance the save loads with. It is not a Ledger posting: no payroll share, no day
/// stats, no Rep, no cash floater.</item>
/// <item>Bags Kevin had already collected (<c>GameSession.DepotDeliveries</c>) are left alone and drop at the depot as usual.</item>
/// </list>
/// </summary>
public static class KevinSeverance
{
    /// <summary>The removed upgrade's id, as saves and authored scenes from before v6 carry it. Nothing sells or reads it.</summary>
    public const string LegacyUpgradeId = "hire_kevin";
    /// <summary>Saves written at this version or later have already been through <see cref="Migrate"/>.</summary>
    public const int RemovedInSaveVersion = 6;
    /// <summary>Set by the migration to the cents refunded; cleared when the owner has been told (<see cref="TryTell"/>).</summary>
    public const string PendingCounter = "kevin_severance_cents";

    // What each level cost on Scrapazon when it was removed: Hire Kevin, Kevin + Other Kevin, The Kevins (Unionized).
    static readonly long[] levelPriceCents = { 60000, 700000, 2300000 };
    public const int MaxLevel = 3;

    /// <summary>Everything a business paid to reach <paramref name="level"/>: $600 / $7,600 / $30,600 (0 below level 1).</summary>
    public static long RefundCents(int level)
    {
        long cents = 0;
        for (int i = 0; i < level && i < levelPriceCents.Length; i++) cents += levelPriceCents[i];
        return cents;
    }

    /// <summary>
    /// The load migration. For a save older than v6: clears the Kevin upgrade, renames his day-stat counters to the crew's,
    /// notes the refund in <see cref="PendingCounter"/> and returns the cents to add to the loaded balance. Pure and
    /// idempotent: a second pass over the same dictionaries finds nothing to refund, a v6 save is never touched, and a
    /// save that never hired him gets 0 and no note.
    /// </summary>
    public static long Migrate(IDictionary<string, int> levels, IDictionary<string, int> counters, int saveVersion)
    {
        if (saveVersion >= RemovedInSaveVersion) return 0;
        DailyStats.MigrateLegacyCrewCounters(counters);
        if (levels == null || !levels.TryGetValue(LegacyUpgradeId, out int level)) return 0;
        levels.Remove(LegacyUpgradeId);
        long cents = RefundCents(level);
        if (cents > 0 && counters != null) counters[PendingCounter] = (int)cents; // at most 3,060,000
        return cents;
    }

    /// <summary>The Head Office letter for a refund of <paramref name="cents"/>.</summary>
    public static void Letter(long cents, out string subject, out string body)
    {
        subject = "Kevin has moved on";
        body = $"Kevin has moved on. Here's your deposit back: {Economy.Money(cents / 100f)}, already in the account. He kept the vest. " +
               "He left one shoe. If the kerbs pile up, Head Office is told raccoons will work nights for kibble: print a Critter Hutch in " + SolidWaste.Name + " (your laptop). " +
               "Head Office does not recognise raccoons as staff.";
    }

    /// <summary>
    /// Tell the owner once (letter + toast) and clear the note. Runs on any machine for its OWN business only: never while
    /// the statics may still show somebody else's world (a guest who has not brought a business in yet).
    /// </summary>
    public static bool TryTell()
    {
        int cents = GameSession.GetCounter(PendingCounter);
        if (cents <= 0) return false;
        if (SaveGuard.Foreign && !BusinessSlots.Declared) return false;
        GameSession.SetCounter(PendingCounter, 0);
        Letter(cents, out string subject, out string body);
        HeadOffice.Send(subject, body);
        GameHUD.Toast($"Kevin has moved on. Here's your deposit back: {Economy.Money(cents / 100f)}.", ToastStyle.Good, 6f);
        return true;
    }
}

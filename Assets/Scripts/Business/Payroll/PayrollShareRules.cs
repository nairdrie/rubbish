/// <summary>Pure contract validation and penny-safe earnings sharing. Shares are gross operating income, not profit.</summary>
public static class PayrollShareRules
{
    public static bool ValidContract(int wageCents, int sharePercent, int existingCrewShare)
    {
        if (sharePercent == 0) return wageCents >= Payroll.MinWageCents && wageCents <= Payroll.MaxWageCents;
        return wageCents == 0 && sharePercent >= Payroll.MinSharePercent && sharePercent <= Payroll.MaxSharePercent &&
            existingCrewShare >= 0 && existingCrewShare <= Payroll.MaxCrewSharePercent &&
            existingCrewShare + sharePercent <= Payroll.MaxCrewSharePercent;
    }

    /// <summary>Allow known earnings only. In particular transfers, refunds, debug cash, spends and charges cannot earn another share.</summary>
    public static bool EligibleCredit(string kind, long cents, string id)
    {
        if (kind != Ledger.KindCredit || cents <= 0) return false;
        switch (id)
        {
            case LedgerIds.PickupFee: case LedgerIds.CurbBonus: case LedgerIds.Recycle: case LedgerIds.Hazard:
            case LedgerIds.Scrap: case LedgerIds.LazyCrush: case LedgerIds.CleanStreets: case LedgerIds.WorkOrder:
            case LedgerIds.Ebae: case LedgerIds.Pawn: case RaccoonTuning.LedgerCrew: // the crew's collection fees (not its wallets)
            case "night_shift": case "animal_control": case "raccoon_king":
            case "ride_streak": case "crew_comfort": case "event_prize":
                return true;
            default: return false;
        }
    }

    /// <summary>Truncation is carried forward, so five 1-cent credits at 20% pay exactly 1 cent. No overflowing cents*percent.</summary>
    public static long Accrue(long cents, int percent, byte remainder, out byte nextRemainder)
    {
        nextRemainder = (byte)(remainder % 100);
        if (cents <= 0 || percent < Payroll.MinSharePercent || percent > Payroll.MaxSharePercent) return 0;
        long fraction = (cents % 100) * percent + nextRemainder;
        nextRemainder = (byte)(fraction % 100);
        return cents / 100 * percent + fraction / 100;
    }
}

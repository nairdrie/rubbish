using UnityEngine;

/// <summary>Business context for authored depot machinery. Never substitutes the viewer's business
/// for a missing remote owner, and never holds a global ledger scope across a coroutine yield.</summary>
public static class DepotMachineContext
{
    public static int LotOf(Component source)
    {
        if (!DepotRowWorld.Active || source == null) return -1;
        var lot = source.GetComponentInParent<DepotLot>();
        return lot != null ? lot.lot : -1;
    }

    public static int Level(Component source, string id)
    {
        int lot = LotOf(source);
        return lot >= 0 ? BusinessUpgradeNet.LevelForLot(lot, id) : GameSession.GetUpgradeLevel(id);
    }

    public static bool HostDepot(Component source, out int lot)
    {
        lot = LotOf(source);
        return lot >= 0 && !GameSession.IsGuest;
    }

    public static void Credit(Component source, float amount, string reason, string id, Vector3? pos = null, string district = null)
    {
        if (!HostDepot(source, out int lot)) { Ledger.Credit(amount, reason, id, pos, district); return; }
        if (!BusinessUpgradeNet.TryOwnerForLot(lot, out string business, out _)) return;
        using (Ledger.ToBusiness(business)) Ledger.Credit(amount, reason, id, pos, district);
    }

    public static void Charge(Component source, float amount, string reason, string id, Vector3? pos = null, string district = null)
    {
        if (!HostDepot(source, out int lot)) { Ledger.Charge(amount, reason, id, pos, district); return; }
        if (!BusinessUpgradeNet.TryOwnerForLot(lot, out string business, out _)) return;
        using (Ledger.ToBusiness(business)) Ledger.Charge(amount, reason, id, pos, district);
    }
}

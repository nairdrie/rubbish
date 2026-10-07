using UnityEngine;

/// <summary>Two owner-sized truckloads of unsorted physical stock per depot. Never deletes existing stock.</summary>
public static class DepotBacklog
{
    public const float Truckloads = 2f;
    public const string FullMessage = "Depot full — sort or compact some trash. The rest stays in the truck.";

    /// <summary>Disconnect cleanup follows the destination depot, never the employee/computer that spawned the junk.</summary>
    public static bool RetireWithOwner(bool claimedDepot, ulong depotOwner, ulong departingClient) =>
        claimedDepot && depotOwner == departingClient;

    public static bool Fits(float usedKg, float incomingKg, float capacityKg) =>
        !float.IsNaN(incomingKg) && !float.IsInfinity(incomingKg) && incomingKg >= 0f &&
        usedKg + incomingKg <= capacityKg + 0.001f;

    public static float CapacityKg(int lot)
    {
        string business = lot >= 0 ? DepotTruckCargo.BusinessOfLot(lot) : GameSession.BusinessId;
        float capacity = GameSession.TruckCapacityKg;
        if (lot >= 0 && !Businesses.TryGetTruckLoad(business, out _, out capacity)) capacity = Economy.StartingTruckCapacityKg;
        return Truckloads * Mathf.Max(1f, capacity);
    }

    public static float UsedKg(int lot)
    {
        float total = 0f;
        foreach (var item in Carryable.All)
        {
            if (item == null || item.State == CarryState.Consumed || !(item is TrashBag || item is LooseItem)) continue;
            bool here = lot >= 0 ? DepotRowLayout.InteriorAt(item.transform.position) == lot : item.gameObject.scene.name == "ShopScene"; // coop:allow(scene) legacy solo shop only; Depot Row uses lot bounds
            if (here) total += Mathf.Max(0f, item.MassKg);
        }
        return total + SortingTable.PendingMassForLot(lot);
    }

    public static bool CanFit(int lot, float kg) => Fits(UsedKg(lot), kg, CapacityKg(lot));
}

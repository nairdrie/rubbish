using UnityEngine;

/// <summary>Business-owned truck models. Capacity upgrades transfer intact when the chassis is replaced.</summary>
public static class TruckProgression
{
    public const int Mini = 0;
    public const int RearLoader = 1;
    public const float MiniCapacityKg = 120f;
    public const float RearLoaderCapacityKg = 200f;
    public const float RearLoaderPrice = 600f;

    static readonly float[] extraCapacityKg = { 0f, 40f, 80f, 120f, 180f };

    public static int NormalizeModel(int model) => Mathf.Clamp(model, Mini, RearLoader);
    public static int CurrentModel => NormalizeModel(GameSession.GetUpgradeLevel(ShopIds.TruckModel));

    /// <summary>The owning business's model, never the driver or an employee's personal model.</summary>
    public static int ModelFor(TruckControllerV2 truck) => NormalizeModel(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckModel));

    public static string ModelName(int model) => NormalizeModel(model) == Mini ? "Curb Pup" : "Rear-loader";

    public static float CapacityBonusKg(int capacityLevel) => extraCapacityKg[Mathf.Clamp(capacityLevel, 0, extraCapacityKg.Length - 1)];

    /// <summary>Mini: 120/160/200/240/300 kg. Rear-loader: 200/240/280/320/380 kg.</summary>
    public static float CapacityKg(int model, int capacityLevel) =>
        (NormalizeModel(model) == Mini ? MiniCapacityKg : RearLoaderCapacityKg) + CapacityBonusKg(capacityLevel);

    /// <summary>Pure purchase preconditions, shared by the catalog lock and the actual purchase.</summary>
    public static bool TradeInReady(bool foundTruck, bool atOwnDepot, bool driver, bool riders, float speed, out string whyNot)
    {
        if (!foundTruck) whyNot = "Your truck is not available yet. Wait for it to arrive.";
        else if (!atOwnDepot) whyNot = "Park your truck inside your own depot or on its apron to trade it in.";
        else if (driver || riders) whyNot = "Everyone must get out of your truck and off its riding steps before the trade-in.";
        else if (float.IsNaN(speed) || float.IsInfinity(speed) || Mathf.Abs(speed) > 0.2f)
            whyNot = "Let your truck come to a complete stop before the trade-in.";
        else { whyNot = null; return true; }
        return false;
    }

    public static bool CanTradeIn(out string whyNot)
    {
        TruckControllerV2 truck = null;
        foreach (var candidate in TruckControllerV2.All)
        {
            if (candidate == null) continue;
            if (DepotRowWorld.Active && DepotTruckCargo.BusinessOf(candidate) != GameSession.BusinessId) continue;
            truck = candidate;
            break;
        }
        if (truck == null) return TradeInReady(false, false, false, false, 0f, out whyNot);
        int ownLot = DepotRowWorld.LocalLot;
        bool atDepot = DepotRowWorld.Active
            ? ownLot >= 0 && (DepotRowLayout.InteriorAt(truck.transform.position) == ownLot ||
                truck.transform.position.y > -5f && DepotRowLayout.PadAt(truck.transform.position) == ownLot)
            : truck.gameObject.scene.name == "ShopScene"; // coop:allow(scene) legacy solo shop only; Depot Row uses business ownership
        var body = truck.Body;
        var ride = RideTruck.For(truck);
        float speed = Mathf.Max(body != null ? body.linearVelocity.magnitude : 0f, ride != null ? ride.Velocity.magnitude : 0f);
        bool occupied = Riding.AnyRiders(truck) ||
            truck.IsSpawned && HandsNet.Instance != null && HandsNet.Instance.Active && HandsNet.Instance.AnyRiderOn(truck.NetworkObjectId);
        if (!TradeInReady(true, atDepot, truck.HasDriver || truck.IsLocalPlayerSeated, occupied, speed, out whyNot)) return false;
        if (!HasReplacementClearance(truck))
        {
            whyNot = "Leave room around your truck for the bigger rear-loader. Move away from walls, people and other vehicles.";
            return false;
        }
        return true;
    }

    /// <summary>Buying a larger shell must not grow it through a wall or another player. The chassis identity stays put.</summary>
    public static bool HasReplacementClearance(TruckControllerV2 truck)
    {
        if (truck == null) return false;
        var box = TruckControllerV2.BodyBoxMetres;
        // Trim the underside slightly so a settled truck's floor contact does not count as blocked parking.
        box.SetMinMax(box.min + Vector3.up * .15f, box.max);
        var hits = Physics.OverlapBox(truck.transform.position + truck.transform.rotation * box.center,
            box.extents, truck.transform.rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
            if (hit != null && !hit.transform.IsChildOf(truck.transform) &&
                !Physics.GetIgnoreLayerCollision(truck.gameObject.layer, hit.gameObject.layer)) return false;
        return true;
    }
}

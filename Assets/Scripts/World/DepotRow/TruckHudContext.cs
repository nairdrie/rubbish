using UnityEngine;

/// <summary>One local view of a truck's persistent business identity, cargo and destination.
/// Never uses NetworkObject.OwnerClientId: that is the driver, not the business owner.</summary>
public struct TruckHudContext
{
    public TruckHudSource source;
    public TruckControllerV2 truck;
    public int lot;
    public string businessId, title, context;
    public float loadKg, capacityKg;
    public bool cargoKnown;

    public static TruckHudContext Resolve(PlayerController player)
    {
        var result = new TruckHudContext { lot = -1 };
        bool oneWorld = DepotRowWorld.Active;
        var net = oneWorld ? DepotRowNet.Instance : null;
        var driving = player != null && player.IsDriving && player.currentTruck != null &&
                      player.currentTruck.IsLocalPlayerSeated ? player.currentTruck : null;
        TruckControllerV2 riding = null;
        var rider = player != null ? player.GetComponent<RideRider>() : null;
        if (rider != null && rider.TryGetRide(out var ride, out _) && ride != null) riding = ride.Truck;

        int interior = oneWorld ? DepotRowWorld.LocalInterior : -1;
        bool workingDepot = interior >= 0 && Permissions.CanWorkIn(Permissions.LocalClientId, interior);
        bool employed = Payroll.TryGetLocalJob(out var job) && Businesses.TryGet(job.EmployerBusinessId, out _);
        result.source = TruckHudRules.Select(driving != null, riding != null, workingDepot, employed);
        switch (result.source)
        {
            case TruckHudSource.Driving: result.truck = driving; break;
            case TruckHudSource.Riding: result.truck = riding; break;
            case TruckHudSource.Depot: result.lot = interior; break;
            case TruckHudSource.Employer:
                result.businessId = job.EmployerBusinessId;
                result.lot = Payroll.EmployerLot(job);
                break;
            default:
                result.businessId = Businesses.LocalId;
                result.lot = oneWorld ? DepotRowWorld.LocalLot : -1;
                break;
        }

        if (result.truck != null)
        {
            if (oneWorld)
            {
                // Wait for the lot mapping instead of borrowing the host's load during truck spawn/rejoin.
                result.lot = result.truck.IsSpawned ? net.LotOfTruck(result.truck.NetworkObjectId) : -1;
                result.businessId = DepotTruckCargo.BusinessOfLot(result.lot);
            }
            else result.businessId = DepotTruckCargo.BusinessOf(result.truck);
        }
        else if (result.lot >= 0)
        {
            result.truck = net.TruckOf(result.lot);
            result.businessId = DepotTruckCargo.BusinessOfLot(result.lot) ?? result.businessId;
        }
        if (!oneWorld && string.IsNullOrEmpty(result.businessId)) result.businessId = Businesses.LocalId;

        result.cargoKnown = Businesses.TryGetTruckLoad(result.businessId, out result.loadKg, out result.capacityKg)
            && result.capacityKg > 0f && !float.IsNaN(result.capacityKg) && !float.IsInfinity(result.capacityKg)
            && result.loadKg >= 0f && !float.IsNaN(result.loadKg) && !float.IsInfinity(result.loadKg);
        bool own = result.businessId == Businesses.LocalId;
        var business = Businesses.Get(result.businessId);
        string owner = business.IsValid ? business.OwnerName : "";
        if (owner.Length == 0 && net != null && net.TryGetLot(result.lot, out var state)) owner = state.ownerName.ToString();
        result.title = own ? "YOUR TRUCK" : owner.Length > 0 ? BusinessColors.OwnerLine(owner) + " truck" : "TRUCK";
        result.context = result.source switch
        {
            TruckHudSource.Driving => "DRIVING",
            TruckHudSource.Riding => "RIDING",
            TruckHudSource.Depot => own ? "YOUR DEPOT" : "WORK DEPOT",
            TruckHudSource.Employer => "WORK TRUCK",
            _ => "OWN BUSINESS"
        };
        return result;
    }
}

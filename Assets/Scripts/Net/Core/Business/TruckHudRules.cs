/// <summary>The context supplying the truck load and capacity shown in the local HUD.</summary>
public enum TruckHudSource
{
    Own,
    Employer,
    Depot,
    Riding,
    Driving
}

/// <summary>Pure selection rules shared by the truck gauge and route-to-depot hint.</summary>
public static class TruckHudRules
{
    public const float DepotFreeCapacityKg = 4.5f;
    public const float DepotMinimumLoadKg = 0.5f;

    /// <summary>Physical truck context wins; a current job wins only when no truck or depot context is active.</summary>
    public static TruckHudSource Select(bool driving, bool riding, bool workingDepot, bool employed)
    {
        if (driving) return TruckHudSource.Driving;
        if (riding) return TruckHudSource.Riding;
        if (workingDepot) return TruckHudSource.Depot;
        if (employed) return TruckHudSource.Employer;
        return TruckHudSource.Own;
    }

    /// <summary>Send a known truck to the depot when it cannot fit a typical bag, or when the curbs are clean and it carries cargo.</summary>
    public static bool ShouldReturnToDepot(bool cargoKnown, float loadKg, float capacityKg, bool allClean)
    {
        if (!cargoKnown || capacityKg <= 0f || float.IsNaN(capacityKg) || float.IsInfinity(capacityKg) ||
            loadKg < 0f || float.IsNaN(loadKg) || float.IsInfinity(loadKg)) return false;
        return capacityKg - loadKg < DepotFreeCapacityKg ||
               allClean && loadKg > DepotMinimumLoadKg;
    }
}

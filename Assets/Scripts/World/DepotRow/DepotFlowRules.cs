using UnityEngine;

/// <summary>How a session moves between the garage and the town (Docs/Multiplayer.md §13.4).</summary>
public enum DepotFlow : byte
{
    /// <summary>Solo (and the <c>-depotrow-off</c> dev hatch): the garage is ShopScene, the town is its own scene, and the
    /// shop door on Depot Row swaps scenes for everyone.</summary>
    LegacyScenes = 0,
    /// <summary>Co-op: town + Depot Row stay loaded; a door moves only the player who uses it.</summary>
    OneWorld = 1,
}

/// <summary>What one Depot Row lot looks like from the street.</summary>
public enum DepotLotLook : byte
{
    /// <summary>One world, unclaimed: a FOR LEASE board on the bare pad (a shop can still plop onto it).</summary>
    ForLease = 0,
    /// <summary>The shop is standing and open: facade, sign in the business colour, usable door.</summary>
    Open = 1,
    /// <summary>One world, owner away: the shop with its shutters down and BACK SOON.</summary>
    BackSoon = 2,
    /// <summary>Legacy flow only: a shuttered unit nobody rents (sign reads FOR LEASE). Nothing ever plops here in solo.</summary>
    Vacant = 3,
}

/// <summary>Which spawn a legacy arrival marker stands for.</summary>
public enum LegacyArrival : byte { None = 0, OnFoot = 1, Truck = 2 }

/// <summary>
/// The pure rules of the depot flow: which flow a session uses, how each lot is presented in it, and where you arrive
/// when you leave the garage. No scene, network or session access (EditMode tests reach it by reflection).
/// <para>There is exactly one way in and out of a shop in each flow, and it is always the shop's own roller door on
/// Depot Row: in the one world it teleports the user into the pocket interior; in the legacy flow lot
/// <see cref="LegacyShopLot"/>'s door swaps to ShopScene (and ShopScene's door swaps back to that lot's apron).</para>
/// </summary>
public static class DepotFlowRules
{
    /// <summary>The lot the single legacy shop stands on (the host's lot in the one world too).</summary>
    public const int LegacyShopLot = 0;

    /// <summary>Legacy flow: the other lots are shuttered FOR LEASE units instead of bare asphalt.</summary>
    public const bool LegacyVacantUnits = true;

    /// <summary>Town markers ShopScene's garage door sends you to (Setup_GarageFlow's contract).</summary>
    public const string TownOnFootMarker = "SpawnPoint_FromGarage", TownTruckMarker = "SpawnPoint_FromGarage_Truck";

    /// <summary>ShopScene markers the legacy shop door sends you to.</summary>
    public const string GarageOnFootMarker = "SpawnPoint_FromNeighborhood", GarageTruckMarker = "SpawnPoint_FromNeighborhood_Truck";

    /// <summary>Solo always keeps the scene flow; a co-op host uses the one world unless the dev hatch forces legacy.</summary>
    public static DepotFlow FlowFor(bool offline, bool forceLegacy) =>
        offline || forceLegacy ? DepotFlow.LegacyScenes : DepotFlow.OneWorld;

    /// <summary>Street look of a lot. <paramref name="claimed"/>/<paramref name="online"/> only matter in the one world.</summary>
    public static DepotLotLook LookFor(DepotFlow flow, int lot, bool claimed, bool online)
    {
        if (flow == DepotFlow.OneWorld)
            return !claimed ? DepotLotLook.ForLease : online ? DepotLotLook.Open : DepotLotLook.BackSoon;
        if (lot == LegacyShopLot) return DepotLotLook.Open;
        return LegacyVacantUnits ? DepotLotLook.Vacant : DepotLotLook.ForLease;
    }

    /// <summary>Is the shop building standing on the pad?</summary>
    public static bool ShowsFacade(DepotLotLook look) => look != DepotLotLook.ForLease;

    /// <summary>Are the shutters down over the roller door?</summary>
    public static bool ShowsShutters(DepotLotLook look) => look == DepotLotLook.BackSoon || look == DepotLotLook.Vacant;

    /// <summary>Is the FOR LEASE board out on the apron?</summary>
    public static bool ShowsLeaseBoard(DepotLotLook look) => look == DepotLotLook.ForLease || look == DepotLotLook.Vacant;

    /// <summary>
    /// Can the street door of <paramref name="lot"/> be used at all? One world: every standing shop (the door itself
    /// decides visit / work / drive permissions). Legacy: only the one shop.
    /// </summary>
    public static bool StreetDoorUsable(DepotFlow flow, int lot, DepotLotLook look) =>
        flow == DepotFlow.OneWorld ? ShowsFacade(look) : lot == LegacyShopLot && look == DepotLotLook.Open;

    /// <summary>Which legacy arrival a town marker name stands for (the names ShopScene's garage door carries).</summary>
    public static LegacyArrival ArrivalFor(string markerName)
    {
        if (markerName == TownTruckMarker) return LegacyArrival.Truck;
        if (markerName == TownOnFootMarker) return LegacyArrival.OnFoot;
        return LegacyArrival.None;
    }

    /// <summary>
    /// Where you arrive in town out of the legacy garage: the same door-relative poses the one world uses for the shop's
    /// own door (beside the door on foot, on the apron in the truck; both facing the road, never inside the building).
    /// The scene's markers are only a fallback; the layout is the single source of truth.
    /// </summary>
    public static bool TryArrivalPose(string markerName, out Pose pose)
    {
        switch (ArrivalFor(markerName))
        {
            case LegacyArrival.Truck: pose = DepotRowLayout.StreetTruck(LegacyShopLot); return true;
            case LegacyArrival.OnFoot: pose = DepotRowLayout.StreetOnFoot(LegacyShopLot); return true;
            default: pose = default; return false;
        }
    }

    /// <summary>The garage marker the legacy shop door leads to.</summary>
    public static string GarageMarker(bool driving) => driving ? GarageTruckMarker : GarageOnFootMarker;
}

// Read-only host/guest probe after the isolated capacity fixture.
var rows = new System.Collections.Generic.List<object>();
for (int lot = 0; lot < DepotRowLayout.LotCount; lot++)
{
    var depot = DepotRowScene.Lot(lot);
    var truck = DepotRowNet.Instance != null ? DepotRowNet.Instance.TruckOf(lot) : null;
    var un = depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<TruckUnloader>(true) : null;
    var ids = new System.Collections.Generic.List<string>();
    foreach (var c in Carryable.All)
    {
        if (c == null || c.State == CarryState.Consumed || DepotRowLayout.InteriorAt(c.transform.position) != lot) continue;
        if (c is TrashBag bag && bag.Contents?.bagId?.StartsWith("capacity-") == true) ids.Add(bag.Contents.bagId);
        if (c is LooseItem item && item.Item?.uid?.StartsWith("capacity-") == true) ids.Add(item.Item.uid);
    }
    rows.Add(new { lot, used = DepotBacklog.UsedKg(lot), cap = DepotBacklog.CapacityKg(lot), truckKg = truck != null ? DepotTruckCargo.LoadKg(truck) : 0,
        status = un != null ? un.CurrentStatus.ToString() : "absent", ids });
}
return rows;

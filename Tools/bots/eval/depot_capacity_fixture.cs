// Isolated MPPM host fixture: lot 1 has room for ONE of two 6 kg cargo bags.
// Enter that depot on the guest, then pull its real TIP IT lever. No real-save use allowed.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("Requires an isolated host test save.");
const int lot = 1;
var truck = DepotRowNet.Instance.TruckOf(lot);
var depot = DepotRowScene.Lot(lot);
if (truck == null || depot == null || depot.interior == null)
    throw new System.InvalidOperationException("Connect a guest with lot 1 first.");
foreach (var sorter in UnityEngine.Object.FindObjectsByType<MagnusSorter>(UnityEngine.FindObjectsSortMode.None)) sorter.enabled = false;
foreach (var tip in UnityEngine.Object.FindObjectsByType<AutoTipper>(UnityEngine.FindObjectsSortMode.None)) tip.enabled = false;
var existing = new System.Collections.Generic.List<Carryable>(Carryable.All);
foreach (var c in existing) if (c != null && DepotRowLayout.InteriorAt(c.transform.position) == lot) c.MarkConsumed();
var def = ItemCatalog.InCategory(ItemCategory.Junk)[0];
System.Func<string, float, BagContents> bag = (id, kg) => new BagContents {
    bagId = id, bagMassKg = 0f, sackVariant = 0,
    items = new System.Collections.Generic.List<ItemInstance> {
        new ItemInstance { uid = id + "-item", defId = def.id, massKg = kg, foundAt = "Capacity test" }
    }
};
float cap = DepotBacklog.CapacityKg(lot);
var pile = TrashBag.Spawn(bag("capacity-existing", cap - 8f), DepotRowLayout.InteriorOrigin(lot) + new UnityEngine.Vector3(-5f, 0.3f, -3f), UnityEngine.Quaternion.identity);
pile.Body.isKinematic = true;
string business = DepotTruckCargo.BusinessOf(truck);
var cargo = Businesses.HostCargo(business);
cargo.Clear();
cargo.Add(bag("capacity-cargo-a", 6f));
cargo.Add(bag("capacity-cargo-b", 6f));
Businesses.TryGetTruckLoad(business, out float load, out float truckCapacity);
Businesses.SetTruckLoad(business, load, truckCapacity);
var pose = DepotRowWorld.InteriorTruckPose(lot);
DepotRowWorld.TeleportTruck(truck, pose.position + UnityEngine.Vector3.up * 0.2f, pose.rotation);
return new { capacity = cap, used = DepotBacklog.UsedKg(lot), truckKg = DepotTruckCargo.LoadKg(truck), expectedAfterTip = "one cargo bag left (6 kg), used = capacity - 2" };

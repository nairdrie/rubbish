// Host-only, isolated save: prepare mixed shop stock and protected cargo/other-depot/in-flight stock.
// Default targets the guest's depot. Change this one constant to 0 for a host-depot run.
// Enter the target depot normally first; wait for publication, then use the real bulk button twice.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("Requires an isolated host test save.");
const int lot = 1;
int otherLot = lot == 0 ? 1 : 0;
string prefix = "compactor-fixture-";
var depot = DepotRowScene.Lot(lot);
var otherDepot = DepotRowScene.Lot(otherLot);
var truck = DepotRowNet.Instance.TruckOf(lot);
if (depot == null || depot.interior == null || !depot.interior.activeSelf || otherDepot == null || truck == null)
    throw new System.InvalidOperationException("Claim two depots and enter the target depot before preparing this fixture.");
var compactor = depot.interior.GetComponentInChildren<Compactor>(true);
var table = depot.interior.GetComponentInChildren<SortingTable>(true);
var tip = depot.interior.GetComponentInChildren<TruckUnloader>(true);
if (compactor == null || table == null || tip == null || compactor.Busy || table.IsRipping || tip.Busy)
    throw new System.InvalidOperationException("Target depot needs an idle table, tip lever and compactor.");
foreach (var sorter in UnityEngine.Object.FindObjectsByType<MagnusSorter>(UnityEngine.FindObjectsSortMode.None)) sorter.enabled = false;
foreach (var autoTip in UnityEngine.Object.FindObjectsByType<AutoTipper>(UnityEngine.FindObjectsSortMode.None)) autoTip.enabled = false;
foreach (var strip in UnityEngine.Object.FindObjectsByType<MagnetStrip>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { strip.StopAllCoroutines(); strip.enabled = false; }
foreach (var feeder in UnityEngine.Object.FindObjectsByType<ConveyorFeeder>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { feeder.StopAllCoroutines(); feeder.enabled = false; }
foreach (var line in UnityEngine.Object.FindObjectsByType<ConveyorLine>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { line.StopAllCoroutines(); line.enabled = false; }
foreach (var tray in UnityEngine.Object.FindObjectsByType<ConveyorTray>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { tray.StopAllCoroutines(); tray.enabled = false; }

// No live save can reach this point. Clear existing target-depot stock so the expected receipt is exact.
foreach (var c in new System.Collections.Generic.List<Carryable>(Carryable.All))
{
    if (c == null) continue;
    string id = c is TrashBag tb ? tb.Contents?.bagId : c is LooseItem li ? li.Item?.uid : "";
    if (DepotRowLayout.InteriorAt(c.transform.position) == lot || (!string.IsNullOrEmpty(id) && id.StartsWith(prefix))) c.Consume();
}
var junk = ItemCatalog.InCategory(ItemCategory.Junk)[0];
var metal = ItemCatalog.InCategory(ItemCategory.Metal)[0];
var hazard = ItemCatalog.InCategory(ItemCategory.Hazard)[0];
var treasure = ItemCatalog.InCategory(ItemCategory.Valuable)[0];
System.Func<string, ItemDef, float, ItemInstance> item = (id, def, mass) => new ItemInstance {
    uid = prefix + id, defId = def.id, massKg = mass, condition = 1f,
    value = def.category == ItemCategory.Valuable ? UnityEngine.Mathf.Max(1f, (def.minValue + def.maxValue) * 0.5f) : 0f,
    foundAt = "Compactor verification"
};
// FreezeAll leaves bodies DYNAMIC (isKinematic false): deliberately not a scripted-flight fixture.
// NetCarry still publishes/rests these normally; presentation must never move their authoritative bodies.
System.Action<Carryable> settle = c => {
    c.Body.isKinematic = false;
    c.Body.constraints = UnityEngine.RigidbodyConstraints.FreezeAll;
    c.Body.linearVelocity = UnityEngine.Vector3.zero;
    c.Body.angularVelocity = UnityEngine.Vector3.zero;
};
UnityEngine.Vector3 origin = DepotRowLayout.InteriorOrigin(lot);
var floor = LooseItem.Spawn(item("floor-junk", junk, 2f), origin + new UnityEngine.Vector3(-5f, 0.3f, -3f), UnityEngine.Quaternion.identity, default, true);
settle(floor);
var top = LooseItem.Spawn(item("table-metal", metal, 3f), table.TopPoint(-0.3f, 0f) + UnityEngine.Vector3.up * 0.14f, UnityEngine.Quaternion.identity, default, true);
settle(top);
var bagContents = new BagContents {
    bagId = prefix + "drop-bag", bagMassKg = 0.15f, sackVariant = 0,
    items = new System.Collections.Generic.List<ItemInstance> {
        item("bag-junk", junk, 3.5f), item("bag-hazard", hazard, 0.3f), item("bag-treasure", treasure, 0.05f)
    }
};
var drop = TrashBag.Spawn(bagContents, tip.pileCenter + UnityEngine.Vector3.up * 0.35f, UnityEngine.Quaternion.identity);
settle(drop);
var chamber = LooseItem.Spawn(item("chamber-junk", junk, 1f), compactor.chamber.position,
    UnityEngine.Quaternion.identity, default, true);
settle(chamber);

var elsewhere = LooseItem.Spawn(item("protected-other-lot", metal, 7f),
    DepotRowLayout.InteriorOrigin(otherLot) + new UnityEngine.Vector3(-5f, 0.3f, -3f), UnityEngine.Quaternion.identity, default, true);
settle(elsewhere);
var scripted = LooseItem.Spawn(item("protected-scripted", metal, 5f),
    origin + new UnityEngine.Vector3(-4f, 1.4f, -3f), UnityEngine.Quaternion.identity, default, true);
scripted.SetInFlight();
// Use a real scripted arc so HandsNet does not mistake a stationary kinematic placeholder for a settled item.
// It remains in flight through the test; stopping Play discards this isolated fixture.
scripted.TossArcInto(compactor, 600f);

string business = DepotTruckCargo.BusinessOf(truck);
var cargo = business == GameSession.BusinessId ? GameSession.TruckBags : Businesses.HostCargo(business);
cargo.Clear();
cargo.Add(new BagContents { bagId = prefix + "protected-cargo", bagMassKg = 0f, sackVariant = 0,
    items = new System.Collections.Generic.List<ItemInstance> { item("truck-item", metal, 12.34f) } });
Businesses.TryGetTruckLoad(business, out float load, out float truckCapacity);
Businesses.SetTruckLoad(business, load, truckCapacity);
var parking = DepotRowWorld.InteriorTruckPose(lot);
DepotRowWorld.TeleportTruck(truck, parking.position + UnityEngine.Vector3.up * 0.2f, parking.rotation);

// Also prove stock that has already been kept into inventory is outside the sweep's domain.
var kept = item("protected-inventory", treasure, UnityEngine.Mathf.Clamp(0.05f, treasure.minMass, treasure.maxMass));
BusinessUpgradeNet.KeepRobotItem(lot, kept);
float rate = UpgradeEffects.LazyPerKgAtLevel(DepotMachineContext.Level(compactor, ShopIds.CompactorMk2));
return new {
    targetLot = lot, expectedBulkCount = 4, expectedKg = 10f,
    expectedPay = SortingKit.Cents(10f * rate), cashBefore = Businesses.Get(business).Cash,
    expectedTruckKgUnchanged = 12.34f,
    expectedProtected = new[] { "other-lot physical item", "scripted item", "truck cargo", "saved inventory treasure" },
    chamber = compactor.chamber.position, mouth = compactor.TossTargetPosition,
    reminder = "Wait for all four target fixture NetIds > 0 and Rest phase; press real bulk button twice >.35s apart. For normal mode expect only chamber-junk (1 kg)."
};

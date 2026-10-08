// Isolated host fixture: fill guest lot 1's table to MaxItems - 2, leaving two two-item bags beside it.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("Requires an isolated host test save.");
const int lot = 1;
var depot = DepotRowScene.Lot(lot);
var table = depot.interior.GetComponentInChildren<SortingTable>(true);
foreach (var sorter in UnityEngine.Object.FindObjectsByType<MagnusSorter>(UnityEngine.FindObjectsSortMode.None)) sorter.enabled = false;
// (No AutoTipper component any more: nothing tips a truck unless its driver holds the tip key.)
var existing = new System.Collections.Generic.List<Carryable>(Carryable.All);
foreach (var c in existing) if (c != null && DepotRowLayout.InteriorAt(c.transform.position) == lot) c.MarkConsumed();
var def = ItemCatalog.InCategory(ItemCategory.Junk)[0];
System.Func<string, ItemInstance> item = id => new ItemInstance { uid = id, defId = def.id, massKg = 0.2f, foundAt = "Table test" };
for (int i = 0; i < table.MaxItems - 2; i++)
{
    var loose = LooseItem.Spawn(item("table-fixture-" + i), table.TopPoint(((i % 10) - 4.5f) * 0.2f, ((i / 10) - 1.5f) * 0.15f) + UnityEngine.Vector3.up * 0.1f,
        UnityEngine.Quaternion.identity, default, true);
    loose.Body.isKinematic = true;
}
for (int i = 0; i < 2; i++)
{
    string id = "table-admit-" + i;
    var contents = new BagContents { bagId = id, bagMassKg = 0.15f, items = new System.Collections.Generic.List<ItemInstance> { item(id + "-a"), item(id + "-b") } };
    var bag = TrashBag.Spawn(contents, table.TopPoint(i == 0 ? -1.2f : 1.2f, -1.2f), UnityEngine.Quaternion.identity);
    bag.Body.isKinematic = true;
}
return new { max = table.MaxItems, prepared = table.MaxItems - 2, remaining = 2, expected = "first two-item reservation accepted; second refused without consuming either bag" };

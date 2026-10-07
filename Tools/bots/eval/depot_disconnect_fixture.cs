// Isolated host: prove disconnect cleanup follows depot ownership, not which peer spawned an item.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer || NetBoot.Manager.ConnectedClientsIds.Count < 2)
    throw new System.InvalidOperationException("Requires an isolated host and connected guest.");
foreach (var sorter in UnityEngine.Object.FindObjectsByType<MagnusSorter>(UnityEngine.FindObjectsSortMode.None)) sorter.enabled = false;
var def = ItemCatalog.InCategory(ItemCategory.Junk)[0];
foreach (string id in new[] { "disconnect-stays-host", "disconnect-stays-employee", "disconnect-retires-host", "disconnect-retires-guest" })
{
    int lot = id.Contains("stays") ? 0 : 1;
    var bag = TrashBag.Spawn(new BagContents { bagId = id, items = { new ItemInstance { uid = id + "-item", defId = def.id, massKg = 0.2f } } },
        DepotRowLayout.InteriorOrigin(lot) + new UnityEngine.Vector3(-5f, 0.2f, id.Contains("host") ? -3f : -2f), UnityEngine.Quaternion.identity);
    bag.Body.isKinematic = true;
}
return "Four stock fixtures spawned. Wait for publish, assign employee/guest origins, then disconnect guest.";

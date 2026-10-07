// Read-only per-peer bulk selection. Machine anchors exist on the host; guests submit a host-validated intent.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var shops = new System.Collections.Generic.List<object>();
foreach (var compactor in UnityEngine.Object.FindObjectsByType<Compactor>(UnityEngine.FindObjectsSortMode.None)) {
    var stock = compactor.CollectForCompact(true);
    var bags = new System.Collections.Generic.List<object>();
    foreach (var item in Carryable.All) {
        if (!(item is TrashBag bag) || !compactor.ContainsShopPoint(bag.transform.position)) continue;
        var nc = bag.GetComponent<NetCarry>();
        bags.Add(new { id = bag.NetId, at = bag.transform.position.ToString("F2"), state = bag.State.ToString(),
            phase = nc != null ? nc.St.Phase.ToString() : "solo", holder = nc != null ? nc.St.holder : 0ul,
            onConveyor = ConveyorStock.OwnedInShop(bag, compactor), selected = stock.Contains(bag),
            pending = nc != null && (nc.WindowOpen || nc.PendingGrab || nc.PendingGone) });
    }
    float kg = 0f;
    foreach (var item in stock) kg += item.MassKg;
    shops.Add(new { lot = DepotMachineContext.LotOf(compactor), compactor.Busy, selected = stock.Count, kg, bags });
}
return new { client = Permissions.LocalClientId, host = NetBoot.Manager != null && NetBoot.Manager.IsServer, shops };

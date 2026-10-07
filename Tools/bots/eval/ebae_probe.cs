// Read-only audit #2 probe: run in both host and guest Editors using --project-path.
// Verifies actual publication, NPC offers, pending transactions, cash and sale history (not just world hashes).
var coordinator = EbaeNeighbours.Instance;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var local = new System.Collections.Generic.List<object>();
foreach (var l in EbaeMarket.Listings)
{
    var offers = new System.Collections.Generic.List<object>();
    foreach (var o in l.offers) offers.Add(new { o.id, o.buyer, o.amount, seconds = o.TimeLeft, o.isCounter });
    var status = coordinator != null ? coordinator.PublicationStatus(l.id) : default;
    local.Add(new { l.id, uid = l.item?.uid, l.askingPrice, l.neighbourHold,
        state = status.State.ToString(), status.Reason, pending = coordinator != null && coordinator.IsNpcPending(l.id), offers });
}
var inventory = new System.Collections.Generic.List<object>();
foreach (var item in GameSession.Inventory) inventory.Add(new { item.uid, item.defId, item.value });
var board = new System.Collections.Generic.List<object>();
if (coordinator != null)
{
    var entries = typeof(EbaeNeighbours).GetField("listings", flags).GetValue(coordinator) as Unity.Netcode.NetworkList<NeighbourListing>;
    if (entries != null) foreach (var p in entries) board.Add(new { p.Id, p.Seller, p.Asking, uid = p.itemUid.ToString(), p.publicationVersion, p.Settling });
}
int PrivateCount(string field) => coordinator == null ? 0 :
    ((System.Collections.IDictionary)typeof(EbaeNeighbours).GetField(field, flags).GetValue(coordinator)).Count;
return Newtonsoft.Json.JsonConvert.SerializeObject(new {
    testSave = SaveRoot.IsOverridden, guest = GameSession.IsGuest, business = Businesses.LocalId,
    cash = GameSession.CashCents, totalSales = EbaeMarket.TotalSales, EbaeMarket.TotalEarned,
    local, inventory, board, pendingHostSales = PrivateCount("npcSales"), ownerHolds = PrivateCount("npcOwnerHolds")
});

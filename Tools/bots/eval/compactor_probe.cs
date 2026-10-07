// Read-only snapshot usable on host or guest after the guarded compactor_fixture.cs.
const int lot = 1;
var depot = DepotRowScene.Lot(lot);
var compactor = depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<Compactor>(true) : null;
var fixture = new System.Collections.Generic.List<object>();
foreach (var c in Carryable.All)
{
    if (c == null) continue;
    string id = c is TrashBag tb ? tb.Contents?.bagId : c is LooseItem li ? li.Item?.uid : "";
    if (string.IsNullOrEmpty(id) || !id.StartsWith("compactor-fixture-")) continue;
    var net = c.GetComponent<NetCarry>();
    fixture.Add(new { id, netId = c.NetId, state = c.State.ToString(),
        phase = net != null ? net.St.Phase.ToString() : "unpublished",
        lot = DepotRowLayout.InteriorAt(c.transform.position), position = c.transform.position.ToString("F2"),
        kinematic = c.Body != null && c.Body.isKinematic, scripted = c.IsInScriptedFlight,
        eligible = Compactor.AvailableForCompact(c), rendererCount = c.GetComponentsInChildren<UnityEngine.Renderer>().Length });
}
int ghosts = 0, ribbons = 0, bales = 0;
foreach (var t in UnityEngine.Object.FindObjectsByType<UnityEngine.Transform>(UnityEngine.FindObjectsSortMode.None))
{
    if (t.name == "Vacuum trash ghost") ghosts++;
    if (t.name == "Intake wind ribbon") ribbons++;
    if (t.name == "Bale") bales++;
}
var players = new System.Collections.Generic.List<object>();
foreach (var p in PlayerTells.All)
    if (p != null) players.Add(new { client = p.ClientId, position = p.transform.position.ToString("F2"),
        lot = DepotRowLayout.InteriorAt(p.transform.position), cubeSeconds = CompactorCube.Remaining(p.ClientId) });
var truck = DepotRowNet.Instance != null ? DepotRowNet.Instance.TruckOf(lot) : null;
var kept = new System.Collections.Generic.List<string>();
foreach (var i in GameSession.Inventory)
    if (i != null && i.uid != null && i.uid.StartsWith("compactor-fixture-")) kept.Add(i.uid);
return Newtonsoft.Json.JsonConvert.SerializeObject(new { lot, busy = compactor != null && compactor.Busy, fixture, ghosts, ribbons, bales, players,
    truckKg = truck != null ? DepotTruckCargo.LoadKg(truck) : -1f, localKept = kept,
    localCash = GameSession.Cash, host = NetBoot.Manager != null && NetBoot.Manager.IsServer });

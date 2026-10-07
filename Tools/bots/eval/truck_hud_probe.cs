// Read-only live HUD probe. Run on host AND guest with --project-path; unlike the generic hash,
// this records replicated cargo/capacity and the exact truck/depot selected by the local HUD.
// unity command eval_file --file <project>/Tools/bots/eval/truck_hud_probe.cs --json
var pc = PlayerController.Local;
var c = TruckHudContext.Resolve(pc);
var reg = BusinessRegistry.Instance;
var entries = new System.Collections.Generic.List<object>();
if (reg != null && reg.List != null)
    for (int i = 0; i < reg.List.Count; i++)
    {
        var b = reg.List[i];
        Businesses.TryGetTruckLoad(b.Id, out float kg, out float cap);
        entries.Add(new { id = b.Id, owner = b.ownerClientId, b.lot, kg, cap, cash = b.cashCents });
    }
var hud = UnityEngine.Object.FindFirstObjectByType<FeelHud>();
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var data = hud != null ? (FeelHud.HudData)typeof(FeelHud).GetMethod("GatherLive", flags).Invoke(hud, null) : default;
string ReadText(string field) => hud != null ? (typeof(FeelHud).GetField(field, flags)?.GetValue(hud) as TMPro.TMP_Text)?.text : null;
return Newtonsoft.Json.JsonConvert.SerializeObject(new {
    testSave = SaveRoot.IsOverridden,
    client = Permissions.LocalClientId,
    driving = pc != null && pc.IsDriving,
    interior = DepotRowWorld.LocalInterior,
    localBagCount = GameSession.TruckBags.Count,
    localSessionKg = GameSession.TruckLoadKg,
    selected = new { source = c.source.ToString(), c.businessId, c.lot, c.title, c.context, c.cargoKnown, c.loadKg, c.capacityKg,
        truck = c.truck != null && c.truck.IsSpawned ? c.truck.NetworkObjectId : 0 },
    display = new { title = ReadText("truckTitleText"), context = ReadText("truckContextText"), kg = ReadText("truckKgText") },
    route = new { data.hasRoute, data.hasTarget, data.targetIsDepot, data.truckFull, data.targetDist, data.arrowAngle },
    businesses = entries
});

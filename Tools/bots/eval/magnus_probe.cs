// Read-only, per-depot chess/automation probe. Run in both MPPM editors with eval_file.
var lots = new System.Collections.Generic.List<object>();
for (int i = 0; i < DepotRowLayout.LotCount; i++)
{
    var lot = DepotRowScene.Lot(i);
    if (lot == null || lot.interior == null) continue;
    var rig = lot.interior.GetComponentInChildren<GarageRig>(true);
    var sorter = lot.interior.GetComponentInChildren<MagnusSorter>(true);
    var board = lot.interior.GetComponentInChildren<DepotChessBoard>(true);
    object chess = null;
    if (DepotChessNet.Instance != null && DepotChessNet.Instance.TryState(i, out var state))
        chess = new {state.whiteSeat,state.blackSeat,turn=state.turn.ToString(),result=state.result.ToString(),state.busy,state.revision,state.ply,state.locations,state.types};
    string why = null;
    bool start = sorter != null && sorter.ShouldStartShift(out why);
    lots.Add(new {
        lot=i,active=lot.interior.activeInHierarchy,
        ownerKnown=BusinessUpgradeNet.TryOwnerForLot(i,out var business,out var owner),business,owner,
        upgradesKnown=BusinessUpgradeNet.HasStateForLot(i),
        brain=BusinessUpgradeNet.LevelForLot(i,ShopIds.MagnusBrain),rail=BusinessUpgradeNet.LevelForLot(i,ShopIds.MagnusRailMotor),
        vision=BusinessUpgradeNet.LevelForLot(i,ShopIds.MagnusVision),tableCapacity=rig?.table?.MaxItems,
        sorterActive=sorter!=null && sorter.isActiveAndEnabled,phase=sorter?.VisiblePhase.ToString(),
        sorted=sorter?.Sorted,start,why,
        cart=rig?.track?.transform.position.ToString("F3"),atChess=rig?.track?.AtChess,
        arm=rig?.arm?.transform.position.ToString("F3"),
        board=board!=null,pieces=board?.pieces?.Length,chess
    });
}
return Newtonsoft.Json.JsonConvert.SerializeObject(new {
    root=SaveRoot.Path,guest=GameSession.IsGuest,business=GameSession.BusinessId,
    cash=GameSession.Cash,rep=GameSession.Rep,itemsSorted=GameSession.TotalItemsSorted,
    inventory=GameSession.Inventory.Select(x=>new{x.uid,x.defId,x.value}).ToArray(),lots
});

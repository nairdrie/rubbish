// Read-only live print-shop/ownership probe: every shop's presenters (garage side and street side), what stands in
// them, the Monument tier showing and the replicated state. No save edits or scene changes.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var worlds = new System.Collections.Generic.List<object>();
foreach (var world in WorkshopWorld.All) {
    var state = Workshop.Snapshot(world.lot);
    var origin = WorkshopWorld.Origin(world.lot);
    // the garage floor under the display line on the south wall (WorkshopWorld.PlacementFor)
    bool floor = UnityEngine.Physics.Raycast(origin + new UnityEngine.Vector3(-.3f, 1f, -4.2f),
        UnityEngine.Vector3.down, out var floorHit, 2f, 1 << SortingKit.GroundLayer, UnityEngine.QueryTriggerInteraction.Ignore);
    var models = new System.Collections.Generic.List<string>();
    foreach (var model in world.Models) if (model != null) models.Add(model.recipeId + " @ " + (model.transform.position - origin).ToString("F2"));
    worlds.Add(new { lot = world.lot, interior = world.Interior, street = world.Street, floor,
        floorY = floor ? floorHit.point.y - origin.y : 999f, models, monumentShowing = world.MonumentTier,
        monumentAt = world.Monument != null ? world.Monument.transform.position.ToString("F2") : null,
        state = state != null ? UnityEngine.JsonUtility.ToJson(state) : null,
        canManage = Workshop.CanManage(world.lot), printer = state != null && state.DiskInstalled });
}
return new { client = Permissions.LocalClientId, server = NetBoot.Manager != null && NetBoot.Manager.IsServer,
    save = SaveRoot.Path, business = GameSession.BusinessId, localPos = PlayerController.Local != null ? PlayerController.Local.transform.position.ToString("F2") : null,
    hands = HandsNet.Instance != null && HandsNet.Instance.Active, hasPrinter = Workshop.HasPrinter, worlds };

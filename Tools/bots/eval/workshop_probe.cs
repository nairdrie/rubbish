// Read-only live workshop/ownership/annex probe. No save edits or scene changes.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var worlds = new System.Collections.Generic.List<object>();
foreach (var world in WorkshopWorld.All) {
    var state = Workshop.Snapshot(world.lot);
    var origin = WorkshopAnnex.Origin(world.lot);
    var from = origin + new UnityEngine.Vector3(5.8f, 0f, -.15f);
    bool blocked = UnityEngine.Physics.CapsuleCast(from + UnityEngine.Vector3.up * .35f,
        from + UnityEngine.Vector3.up * 1.55f, .28f, UnityEngine.Vector3.right, out var hit, 2.6f,
        (1 << SortingKit.GroundLayer) | (1 << SortingKit.WallsLayer), UnityEngine.QueryTriggerInteraction.Ignore);
    bool floor = UnityEngine.Physics.Raycast(origin + new UnityEngine.Vector3(11f, 1f, -.15f),
        UnityEngine.Vector3.down, out var floorHit, 2f, 1 << SortingKit.GroundLayer, UnityEngine.QueryTriggerInteraction.Ignore);
    int models = 0; foreach (var model in world.Models) if (model != null) models++;
    worlds.Add(new { lot = world.lot, annex = world.Annex != null, open = world.Annex != null && world.Annex.WallOpened,
        portalBlocked = blocked, blocker = blocked ? hit.collider.name : null, floor,
        floorY = floor ? floorHit.point.y - origin.y : 999f, models, state = state != null ? UnityEngine.JsonUtility.ToJson(state) : null,
        canManage = Workshop.CanManage(world.lot), canDonate = Workshop.CanDonate(world.lot) });
}
return new { client = Permissions.LocalClientId, server = NetBoot.Manager != null && NetBoot.Manager.IsServer,
    save = SaveRoot.Path, business = GameSession.BusinessId, localPos = PlayerController.Local != null ? PlayerController.Local.transform.position.ToString("F2") : null,
    hands = HandsNet.Instance != null && HandsNet.Instance.Active, worlds };

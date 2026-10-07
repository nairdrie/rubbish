// Read-only per-peer collision/recovery evidence. Run on host and guest after a test impact.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var trucks = new System.Collections.Generic.List<object>();
foreach (var truck in TruckControllerV2.All) {
    if (truck == null || truck.Body == null) continue;
    var impact = truck.GetComponent<TruckImpact>();
    trucks.Add(new { id = truck.IsSpawned ? truck.NetworkObjectId : 0, owner = truck.IsSpawned ? truck.OwnerClientId : 0,
        driver = truck.DriverClientId, position = truck.Body.position.ToString("F3"), velocity = truck.Body.linearVelocity.ToString("F3"),
        kinematic = truck.Body.isKinematic, recent = impact != null ? impact.RecentVelocity.ToString("F3") : null,
        applied = impact != null ? impact.AppliedImpacts : -1, kick = impact != null ? impact.LastKick.ToString("F3") : null,
        parkingBrakeScale = TruckImpact.ParkingBrakeScale(truck) });
}
var cars = new System.Collections.Generic.List<object>();
if (TrafficDirector.Instance != null) foreach (var car in TrafficDirector.Instance.Agents)
    if (car != null && car.Shown) cars.Add(new { id = car.id.Packed, roster = car.car, position = car.position.ToString("F3"),
        arc = car.s, speed = car.v, shuntSeconds = car.shuntT, skid = car.slide.ToString("F3"), yaw = car.shuntYaw,
        parked = car.parkedShell, holdLeft = car.holdUntil - UnityEngine.Time.time, collider = car.box != null && car.box.enabled });
return new { client = Permissions.LocalClientId, host = NetBoot.Manager != null && NetBoot.Manager.IsServer,
    truckEvents = HandsNet.Instance != null ? HandsNet.Instance.TruckImpactConfirmations : -1,
    carEvents = HandsNet.Instance != null ? HandsNet.Instance.CarImpactConfirmations : 0, trucks, cars };

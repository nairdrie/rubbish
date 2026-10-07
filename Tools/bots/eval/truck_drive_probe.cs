// Runtime-only, isolated-save fixture. Seat the LOCAL player normally, stop the truck, close menus, then run:
// unity command eval_file --file <project>/Tools/bots/eval/truck_drive_probe.cs --json
// Takes about 15 seconds. Read TRUCK_DRIVE_PROBE / TRUCK_DRIVE_RESULT in the Console, or
// <SaveRoot.Path>/truck-drive-probe.json. Restores truck pose/input; never claims a seat or changes cargo/upgrades.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Requires an isolated live test session.");
var me = PlayerController.Local;
var truck = me != null ? me.currentTruck : null;
if (truck == null || !truck.IsLocalPlayerSeated || (truck.IsSpawned && (!truck.IsOwner || !truck.IsLocalDriver)))
    throw new System.InvalidOperationException("Seat the local player normally and wait for real driver/physics ownership first.");
var body = truck.Body;
if (body == null || body.isKinematic || body.linearVelocity.magnitude > 0.3f || truck.InputOverride != null)
    throw new System.InvalidOperationException("Stop the authoritative truck first; no other input override may be active.");
if (UnityEngine.Time.timeScale < 0.99f || PlayerController.IsUIBlocking || SceneController.IsTransitioning || DepotRowWorld.Transitioning)
    throw new System.InvalidOperationException("Close menus, unpause, and finish scene transitions first.");
foreach (var rider in RideRider.All)
    if (rider != null && rider.TryGetRide(out var riding, out _) && riding != null && riding.Truck == truck)
        throw new System.InvalidOperationException("Ask riders to dismount before teleporting this test truck.");

var wheels = truck.GetComponentsInChildren<UnityEngine.WheelCollider>(true);
if (wheels.Length < 2) throw new System.InvalidOperationException("Truck has no usable wheel setup.");
var startPose = new UnityEngine.Pose(truck.transform.position, truck.transform.rotation);
var originalOverride = truck.InputOverride;
var routeA = RideRoute.Path[0];
var routeB = RideRoute.Path[1];
var forward = UnityEngine.Vector3.ProjectOnPlane(routeB - routeA, UnityEngine.Vector3.up).normalized;
var rotation = UnityEngine.Quaternion.LookRotation(forward, UnityEngine.Vector3.up);
var right = rotation * UnityEngine.Vector3.right;
const float corridorLength = 40f, baselineSpeed = 8f, phaseSeconds = 3f;
var spawn = UnityEngine.Vector3.zero;
float roadY = 0f;
string groundName = null;
bool found = false;

// Derive the lane from the existing ride test route, then verify actual flat static ground and an empty corridor.
bool StaticGround(UnityEngine.Vector3 at, out UnityEngine.RaycastHit ground)
{
    ground = default;
    float nearest = float.MaxValue;
    var hits = UnityEngine.Physics.RaycastAll(at + UnityEngine.Vector3.up * 4f, UnityEngine.Vector3.down, 8f,
        UnityEngine.Physics.DefaultRaycastLayers, UnityEngine.QueryTriggerInteraction.Ignore);
    foreach (var hit in hits)
    {
        var c = hit.collider;
        if (c == null || c.attachedRigidbody != null || c.GetComponentInParent<PlayerController>() != null ||
            c.GetComponentInParent<Carryable>() != null || c.GetComponentInParent<TruckControllerV2>() != null ||
            hit.normal.y < 0.985f || UnityEngine.Mathf.Abs(hit.point.y - at.y) > 0.5f || hit.distance >= nearest) continue;
        ground = hit;
        nearest = hit.distance;
    }
    return nearest < float.MaxValue;
}
bool OwnCollider(UnityEngine.Collider c) => c != null &&
    (c.GetComponentInParent<TruckControllerV2>() == truck || c.GetComponentInParent<PlayerController>() == me);
string Obstacle(UnityEngine.Vector3 from, float ahead, float groundY)
{
    // Include pedestrians, bags, other trucks, and props. Ignore only our truck/driver and the road below the box.
    var center = from + forward * (ahead * 0.5f) + UnityEngine.Vector3.up * 1.9f;
    center.y = groundY + 1.9f;
    var hits = UnityEngine.Physics.OverlapBox(center, new UnityEngine.Vector3(1.45f, 1.55f, ahead * 0.5f + 3.9f),
        rotation, UnityEngine.Physics.DefaultRaycastLayers, UnityEngine.QueryTriggerInteraction.Ignore);
    foreach (var hit in hits)
        if (!OwnCollider(hit) && hit.bounds.max.y > groundY + 0.36f) return hit.name;
    return null;
}
for (int attempt = 0; attempt < 5 && !found; attempt++)
{
    var candidate = routeA + forward * (12f + attempt * 13f);
    if (UnityEngine.Vector3.Distance(candidate, routeB) < corridorLength + 8f || !StaticGround(candidate, out var first)) continue;
    bool supported = true;
    for (float d = -3f; d <= corridorLength + 4f && supported; d += 3f)
        for (int side = -1; side <= 1; side++)
        {
            var at = candidate + forward * d + right * (side * 1.05f);
            if (!StaticGround(at, out var hit) || UnityEngine.Mathf.Abs(hit.point.y - first.point.y) > 0.10f)
            { supported = false; break; }
        }
    if (!supported || Obstacle(candidate, corridorLength, first.point.y) != null) continue;
    roadY = first.point.y;
    spawn = new UnityEngine.Vector3(candidate.x, roadY + 0.15f, candidate.z);
    groundName = first.collider.name;
    found = true;
}
if (!found) throw new System.InvalidOperationException("No clear, flat 40 m corridor on the existing RideRoute westbound straight. Move traffic/players away or retry; nothing was moved.");

int Grounded() { int n = 0; foreach (var w in wheels) if (w != null && w.isGrounded) n++; return n; }
float Speed() => UnityEngine.Vector3.ProjectOnPlane(body.linearVelocity, UnityEngine.Vector3.up).magnitude;
float ForwardSpeed() => UnityEngine.Vector3.Dot(body.linearVelocity, forward);
bool StillDriver() => truck != null && me != null && truck.IsLocalPlayerSeated && me.currentTruck == truck &&
    (!truck.IsSpawned || truck.IsOwner && truck.IsLocalDriver) && body != null && !body.isKinematic;
var samples = new System.Collections.Generic.List<object>();
var results = new System.Collections.Generic.List<object>();
var endSpeeds = new float[3];
string failure = null;
float command = 0f;
System.Func<UnityEngine.Vector2> scripted = () => new UnityEngine.Vector2(0f, command);
var resultPath = System.IO.Path.Combine(SaveRoot.Path, "truck-drive-probe.json");
object Cab()
{
    var cab = truck != null ? truck.GetComponent<TruckCabVisuals>() : null;
    var box = TruckControllerV2.BodyBoxMetres;
    var look = truck.transform.position + truck.transform.rotation * new UnityEngine.Vector3(-0.15f, 1.9f, 2.7f);
    var camera = truck.transform.position + truck.transform.rotation *
        new UnityEngine.Vector3(-box.extents.x - 3.0f, box.max.y + 1.0f, box.max.z + 4.6f);
    return new { driver = truck.DriverClientId, physicsOwner = truck.IsSpawned ? truck.OwnerClientId : 0,
        localClient = Permissions.LocalClientId, business = DepotTruckCargo.BusinessOf(truck),
        localSeated = truck.IsLocalPlayerSeated, truck = truck.name,
        shownDriver = cab != null ? cab.ShownDriver : ulong.MaxValue,
        glassSlots = cab != null ? cab.GlassSlots : 0, occupantVisible = cab != null && cab.OccupantVisible,
        head = ShopModKit.TruckPoint(truck.transform, TruckCabVisuals.DriverHeadMetres).ToString("F3"),
        suggestedCamera = camera.ToString("F3"), suggestedLookAt = look.ToString("F3") };
}
System.Collections.IEnumerator Probe()
{
    truck.InputOverride = scripted;
    try
    {
        string[] names = { "coast", "throttle", "brake" };
        float[] commands = { 0f, 1f, -1f };
        for (int phase = 0; phase < names.Length; phase++)
        {
            if (!StillDriver()) { failure = "Driver/authority changed."; yield break; }
            command = 0f;
            DepotRowWorld.TeleportTruck(truck, spawn, rotation);
            float settleEnd = UnityEngine.Time.realtimeSinceStartup + 2f;
            while (UnityEngine.Time.realtimeSinceStartup < settleEnd)
            {
                if (!StillDriver() || UnityEngine.Time.timeScale < 0.99f || PlayerController.IsUIBlocking)
                { failure = "Driver, pause, or UI changed during suspension settling."; yield break; }
                yield return null;
            }
            if (Grounded() < 2) { failure = "Fewer than two wheels grounded after settling."; yield break; }
            var obstruction = Obstacle(truck.transform.position, corridorLength, roadY);
            if (obstruction != null) { failure = "Road became occupied by " + obstruction; yield break; }
            body.angularVelocity = UnityEngine.Vector3.zero;
            body.linearVelocity = forward * baselineSpeed;
            command = commands[phase];
            float elapsed = 0f, nextSample = 0f, groundedSeconds = 0f;
            int minGrounded = wheels.Length;
            bool brakeStopped = false;
            while (elapsed < phaseSeconds)
            {
                if (!StillDriver() || truck.InputOverride != scripted || UnityEngine.Time.timeScale < 0.99f ||
                    PlayerController.IsUIBlocking || SceneController.IsTransitioning || DepotRowWorld.Transitioning)
                { failure = "Driver, authority, input, pause, menu, or scene changed during motion."; yield break; }
                if (UnityEngine.Vector3.Dot(truck.transform.forward, forward) < 0.97f ||
                    UnityEngine.Mathf.Abs(UnityEngine.Vector3.Dot(truck.transform.position - spawn, right)) > 0.45f)
                { failure = "Truck veered out of the verified straight test lane."; yield break; }
                obstruction = Obstacle(truck.transform.position, 7f, roadY);
                if (obstruction != null) { failure = "Obstacle entered the braking corridor: " + obstruction; yield break; }
                if (phase == 2 && ForwardSpeed() <= 0.2f) { command = 0f; brakeStopped = true; }
                int grounded = Grounded();
                minGrounded = UnityEngine.Mathf.Min(minGrounded, grounded);
                if (grounded > 0) groundedSeconds += UnityEngine.Time.deltaTime;
                if (elapsed >= nextSample)
                {
                    samples.Add(new { phase = names[phase], seconds = elapsed, speed = Speed(), forwardSpeed = ForwardSpeed(),
                        grounded, command, at = truck.transform.position.ToString("F3") });
                    nextSample += 0.5f;
                }
                elapsed += UnityEngine.Time.deltaTime;
                yield return null;
            }
            endSpeeds[phase] = Speed();
            results.Add(new { phase = names[phase], initialSpeed = baselineSpeed, endSpeed = endSpeeds[phase],
                elapsed, groundedSeconds, minGrounded, brakeStopped });
            UnityEngine.Debug.Log("TRUCK_DRIVE_PROBE " + Newtonsoft.Json.JsonConvert.SerializeObject(new { result = results[phase], cab = Cab() }));
            command = 0f;
        }
    }
    finally
    {
        bool complete = results.Count == 3;
        bool coastSlows = complete && endSpeeds[0] < baselineSpeed - 0.5f && endSpeeds[0] > 0.5f;
        bool throttleBeatsCoast = complete && endSpeeds[1] > endSpeeds[0] + 0.5f;
        bool brakeBeatsCoast = complete && endSpeeds[2] < endSpeeds[0] - 0.5f;
        // Never teleport somebody else's truck after authority changed. Never clobber a replacement automation.
        bool restored = StillDriver();
        if (restored) DepotRowWorld.TeleportTruck(truck, startPose.position, startPose.rotation);
        if (truck != null && truck.InputOverride == scripted) truck.InputOverride = originalOverride;
        var report = new { complete, passed = complete && coastSlows && throttleBeatsCoast && brakeBeatsCoast,
            failure, restored, coastSlows, throttleBeatsCoast, brakeBeatsCoast, baselineSpeed, phaseSeconds,
            route = "RideRoute.Path[0..1]: Minivan Mile westbound", groundName, roadY,
            start = spawn.ToString("F3"), cab = truck != null ? Cab() : null, results, samples };
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented);
        System.IO.File.WriteAllText(resultPath, json);
        UnityEngine.Debug.Log("TRUCK_DRIVE_RESULT " + json);
    }
}
truck.StartCoroutine(Probe());
return "Driving probe started on the current local driver's truck. In ~15 seconds read TRUCK_DRIVE_RESULT or " + resultPath;

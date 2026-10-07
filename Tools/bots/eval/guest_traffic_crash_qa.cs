if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying || Permissions.LocalClientId != 1)
    throw new System.InvalidOperationException("Isolated guest play-test required.");
var truck = TruckControllerV2.All.First(x => x.NetworkObjectId == 39);
if (!truck.IsOwner) throw new System.InvalidOperationException("Guest must own truck 39 first.");
var traffic = TrafficDirector.Instance;
var car = traffic.Agents.Where(a => a.Shown && !a.parkedShell && a.car >= 0 && a.path != null && a.path.Length - a.s > 130f)
    .OrderByDescending(a => a.path.Length - a.s).First();
var samples = new System.Collections.Generic.List<object>();
var output = System.IO.Path.Combine(SaveRoot.Path, "guest-traffic-crash-qa.json");
var originalInput = truck.InputOverride;
void Place(UnityEngine.Vector3 at, UnityEngine.Vector3 direction)
{
    at.y = car.position.y + .1f;
    var rotation = UnityEngine.Quaternion.LookRotation(direction);
    truck.GetComponent<Unity.Netcode.Components.NetworkTransform>().Teleport(at, rotation, truck.transform.localScale);
    truck.Body.position = at; truck.Body.rotation = rotation;
    truck.Body.linearVelocity = truck.Body.angularVelocity = UnityEngine.Vector3.zero;
}
System.Collections.IEnumerator Run()
{
    truck.InputOverride = () => UnityEngine.Vector2.zero;
    var direction = car.path.Tangent(car.path.SegmentAt(car.s));
    Place(car.position + direction * 14f, -direction);
    yield return new UnityEngine.WaitForSeconds(1f);
    direction = car.path.Tangent(car.path.SegmentAt(car.s));
    var before = new { id = car.id.Packed, car.car, car.s, car.v, position = car.position.ToString("F3"),
        events = HandsNet.Instance.CarImpactConfirmations, hour = GameClock.TotalHours };
    Place(car.position + direction * 13f, -direction);
    yield return new UnityEngine.WaitForSeconds(.4f);
    truck.Body.linearVelocity = -direction * 15f;
    truck.InputOverride = () => new UnityEngine.Vector2(0f, 1f);
    for (int i = 0; i < 270; i++) {
        yield return new UnityEngine.WaitForFixedUpdate();
        if (i == 70) truck.InputOverride = () => UnityEngine.Vector2.zero;
        samples.Add(new { t = i * UnityEngine.Time.fixedDeltaTime, events = HandsNet.Instance.CarImpactConfirmations,
            truck = truck.Body.position.ToString("F3"), velocity = truck.Body.linearVelocity.ToString("F3"),
            car = car.position.ToString("F3"), car.Shown, car.shuntT, car.shuntYaw, car.v,
            slide = car.slide.ToString("F3"), car.s, collider = car.box != null && car.box.enabled });
    }
    truck.InputOverride = originalInput;
    System.IO.File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(new { before, samples }));
}
HandsNet.Instance.StartCoroutine(Run());
return output;

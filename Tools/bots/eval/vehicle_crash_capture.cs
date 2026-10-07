// Start shortly before a real contact; samples both copies without forcing an RPC or simulating the collision.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var samples = new System.Collections.Generic.List<object>();
var clock = UnityEngine.Time.time;
var path = System.IO.Path.Combine(SaveRoot.Path, "crash-client-" + Permissions.LocalClientId + ".json");
System.Collections.IEnumerator Observe()
{
    for (int frame = 0; frame < 160; frame++)
    {
        yield return new UnityEngine.WaitForFixedUpdate();
        samples.Add(new { t = UnityEngine.Time.time - clock, confirms = HandsNet.Instance.TruckImpactConfirmations,
            cars = HandsNet.Instance.CarImpactConfirmations,
            trucks = TruckControllerV2.All.Select(tr => new { id = tr.NetworkObjectId, owner = tr.IsOwner,
                position = tr.Body.position.ToString("F3"), velocity = tr.Body.linearVelocity.ToString("F3"), applied = tr.GetComponent<TruckImpact>().AppliedImpacts }).ToArray(),
            traffic = TrafficDirector.Instance.Agents.Where(a => a.Shown && a.shuntT >= 0)
                .Select(a => new { id = a.id.Packed, position = a.position.ToString("F3"), a.shuntT, a.shuntYaw, slide = a.slide.ToString("F3"), a.s }).ToArray() });
    }
    System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(samples,
        new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));
}
HandsNet.Instance.StartCoroutine(Observe());
return path;

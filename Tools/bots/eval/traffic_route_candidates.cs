// Read-only real-roster selector for co-op collision QA. DebugTrip cars do not have a shared scheduled route.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var traffic = TrafficDirector.Instance;
var sim = traffic != null ? traffic.Sim : null;
if (sim == null) return new { ready = false, reason = "WorldLife is not awake" };
var day = sim.Today;
double now = day.MsOf(GameClock.TotalHours);
var view = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : UnityEngine.Vector3.zero;
var moving = new System.Collections.Generic.List<object>();
var upcoming = new System.Collections.Generic.List<object>();
for (int ci = 0; ci < sim.Roster.CarCount; ci++) {
    double delay = 1000.0 * (LifeOverrides.DelaySeconds(LifeId.Car(ci), day.day) +
        LifeOverrides.DelaySeconds(LifeId.Resident(sim.Roster.cars[ci].driver), day.day));
    var phase = day.CarAt(ci, now - delay, out int trip, out float arc, out float speed);
    if (phase != CarPhase.Out && phase != CarPhase.In) continue;
    var path = phase == CarPhase.Out ? day.trips[trip].outPath : day.trips[trip].inPath;
    if (path == null) continue;
    var position = path.PointAt(arc, out int segment);
    var direction = path.Tangent(segment);
    CarAgent shown = null;
    foreach (var agent in traffic.Agents) if (agent.car == ci && agent.Shown) { shown = agent; break; }
    moving.Add(new { car = ci, id = LifeId.Car(ci).Packed, phase = phase.ToString(), speed,
        position, direction, remainingMetres = path.Length - arc,
        viewerDistance = UnityEngine.Vector3.Distance(view, position),
        shownHere = shown != null, seenPosition = shown != null ? shown.position : position,
        drawnSpeed = shown != null ? shown.v : 0f, delaySeconds = delay * .001 });
}
foreach (var trip in day.trips) {
    if (upcoming.Count >= 12) break;
    if (trip.depart <= now || trip.outPath == null || trip.outPath.Duration <= 35f) continue;
    double desiredMs = trip.depart + 15000.0;
    var arc = trip.outPath.ArcAtTime(15f, out float speed, out _);
    upcoming.Add(new { car = trip.car, id = LifeId.Car(trip.car).Packed,
        forwardGameHours = (desiredMs - now) / LifeDay.MsPerHour,
        positionAfterForwardSkip = trip.outPath.PointAt(arc), speed });
}
return Newtonsoft.Json.JsonConvert.SerializeObject(new { ready = true, client = Permissions.LocalClientId, day = day.day, hour = GameClock.Hours, moving, upcoming },
    new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore });

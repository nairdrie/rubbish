if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var sim = TrafficDirector.Instance.Sim;
var day = sim.Today;
double time = day.MsOf(GameClock.TotalHours);
var found = new System.Collections.Generic.List<object>();
for (int ci = 0; ci < sim.Roster.CarCount; ci++)
{
    var id = LifeId.Car(ci);
    double delay = 1000 * (LifeOverrides.DelaySeconds(id, day.day) +
        LifeOverrides.DelaySeconds(LifeId.Resident(sim.Roster.cars[ci].driver), day.day));
    var phase = day.CarAt(ci, time - delay, out int ti, out float arc, out float speed);
    if ((phase != CarPhase.Out && phase != CarPhase.In) || speed < 2 || ti < 0) continue;
    var path = phase == CarPhase.Out ? day.trips[ti].outPath : day.trips[ti].inPath;
    if (path == null || path.Length - arc < 60) continue;
    var p = path.PointAt(arc, out int segment);
    var d = path.Tangent(segment);
    found.Add(new {ci, packed=id.Packed, p=p.ToString("F3"), d=d.ToString("F3"), remaining=path.Length-arc, speed});
}
return new { hours=GameClock.TotalHours, candidates=found };

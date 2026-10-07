using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// WorldLife package D setup + tests (Docs/WorldLife.md §7.1, §7.3, §12.3 D). <see cref="Run"/> is the umbrella's
/// idempotent step (D needs no assets: it validates that every household car has a route and reports the roster).
/// <see cref="SelfTest"/> is the EditMode suite (an editor class, because Assets/Tests/EditMode is the Net asmdef):
/// determinism (same seed + day → identical legs and positions, built in two orders), the §4.3 population table over
/// 20 seeded days, the gate rule, and the cost of a day build and of the per-frame queries.
/// <see cref="Probe"/> is the play-mode kit (lane deviation, same-lane gaps, honks, overtakes, counts).
/// </summary>
public static class Setup_WorldLifeTraffic
{
    [MenuItem("Rubbish/Setup/WorldLife Traffic")]
    public static void RunMenu() => Debug.Log(Run());

    public static string Run()
    {
        var g = AssetDatabase.LoadAssetAtPath<StreetGraph>(StreetGraph.AssetPath);
        if (g == null) return "[Setup_WorldLifeTraffic] no StreetGraph asset yet (package A): nothing to check";
        var sim = LifeSim.Create(g, (ulong)(uint)(g.builderSeed != 0 ? g.builderSeed : 1234));
        int all = -1, starter = 1, routed = 0, routedStarter = 0;
        for (int c = 0; c < sim.Roster.CarCount; c++)
        {
            if (sim.CarRouteFor(c, all).exit >= 0) routed++;
            if (sim.CarRouteFor(c, starter).exit >= 0) routedStarter++;
        }
        return $"[Setup_WorldLifeTraffic] {sim.Roster.Describe().Replace("\n", "; ")}\n  cars with a route: {routed}/{sim.Roster.CarCount} (all unlocked), {routedStarter}/{sim.Roster.CarCount} (starter only)";
    }

    // =================================================================================================================
    // EditMode suite
    // =================================================================================================================

    [MenuItem("Rubbish/WorldLife/Traffic Self Test")]
    public static void SelfTestMenu()
    {
        string r = SelfTest();
        if (r.Contains("FAIL")) Debug.LogWarning(r); else Debug.Log(r);
    }

    public static string SelfTest(int days = 20)
    {
        var sb = new StringBuilder("[WorldLife D self-test]\n");
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); }

        var g = AssetDatabase.LoadAssetAtPath<StreetGraph>(StreetGraph.AssetPath);
        if (g == null) { sb.AppendLine("  FAIL no StreetGraph asset"); return sb.ToString(); }
        ulong seed = 1234;

        // ---- the test graph works too (sandboxes)
        var tg = StreetGraph.BuildTest();
        var tsim = LifeSim.Create(tg, seed);
        var tday = tsim.Day(0, -1);
        Check(tday.trips.Count > 0 && tday.excursions.Count > 0, $"StreetGraph.Test(): {tsim.Roster.ResidentCount} residents, {tsim.Roster.CarCount} cars, day 0 → {tday.trips.Count} trips, {tday.excursions.Count} walks");
        UnityEngine.Object.DestroyImmediate(tg);

        // ---- roster
        var simA = LifeSim.Create(g, seed);
        sb.AppendLine("  roster: " + simA.Roster.Describe().Replace("\n", "\n          "));
        Check(simA.Roster.ResidentCount == ResidentCast.Count, $"roster reads package C's cast with the same ids ({simA.Roster.ResidentCount} residents)");

        // ---- determinism: two sims, days built in opposite orders, hash every leg and sampled positions
        var simB = LifeSim.Create(g, seed);
        var ha = new ulong[days]; var hb = new ulong[days];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int d = 0; d < days; d++) ha[d] = HashDay(simA, simA.Day(d, -1));
        sw.Stop();
        float buildMs = (float)sw.Elapsed.TotalMilliseconds / days;
        for (int d = days - 1; d >= 0; d--) hb[d] = HashDay(simB, simB.Day(d, -1));
        int same = 0; for (int d = 0; d < days; d++) if (ha[d] == hb[d]) same++;
        int distinct = new HashSet<ulong>(ha).Count;
        Check(same == days && distinct == days, $"same seed + day → identical legs and positions: {same}/{days} days match (built forward vs backward), {distinct} distinct days; day 0 hash {ha[0]:X16}");
        var simC = LifeSim.Create(g, seed + 1);
        Check(HashDay(simC, simC.Day(0, -1)) != ha[0], "a different Town seed gives a different town");

        // ---- the §4.3 population table over N seeded days (all districts unlocked: "unlocked or not")
        var table = PopulationTable(simA, days, -1, out int cellFails, out string tableText);
        sb.Append(tableText);
        Check(cellFails == 0, $"§4.3 population table holds to ±25% over {days} seeded days ({cellFails} cells out)");

        // ---- the rush is a plateau, not a peak (fixer pass: D's triangle put 22–26 cars on the move at 08:00 against the
        // band's 8–12): the worst 10-minute average of moving cars inside each rush band stays ≤ 12 × 1.25
        {
            var bins = new double[2, 24];
            for (int d = 0; d < days; d++)
            {
                var rushDay = simA.Day(d, -1);
                for (int k = 0; k < 24 * 10; k++)
                {
                    bins[0, k / 10] += simA.CountMovingCars(rushDay, -1, (6.0 + k / 60.0) * LifeDay.MsPerHour) / 10.0;
                    if (k < 240) bins[1, k / 10] += simA.CountMovingCars(rushDay, -1, (16.0 + k / 60.0) * LifeDay.MsPerHour) / 10.0;
                }
            }
            double am = 0, pm = 0;
            for (int b = 0; b < 18; b++) am = Math.Max(am, bins[0, b] / days);
            for (int b = 0; b < 24; b++) pm = Math.Max(pm, bins[1, b] / days);
            Check(am <= 15.0 && pm <= 15.0, $"rush plateau: the worst 10-minute average is {am:F1} moving cars in 06–09 and {pm:F1} in 16–20 (≤ 15 = 12 + 25 %)");
        }

        // ---- gates: no car route crosses a locked gate; who stays home
        var masks = new[] { 1, 1 | 2, 1 | 4, 1 | 2 | 4, 1 | 2 | 4 | 8, -1 };
        var gateLines = new StringBuilder();
        int crossings = 0;
        foreach (int m in masks)
        {
            int home = 0; var homeBy = new int[4];
            for (int c = 0; c < simA.Roster.CarCount; c++)
            {
                var cr = simA.CarRouteFor(c, m);
                if (cr.exit < 0) { home++; homeBy[simA.Roster.cars[c].district]++; continue; }
                foreach (var p in new[] { cr.outPath, cr.inPath })
                    foreach (int e in p.graphEdges) if (!g.EdgeOpen(e, m)) crossings++;
            }
            var dd = simA.Day(3, m);
            int tripsLocked = 0;
            for (int i = 0; i < dd.trips.Count; i++)
            {
                var tr = dd.trips[i];
                foreach (int e in tr.outPath.graphEdges) if (!g.EdgeOpen(e, m)) tripsLocked++;
                foreach (int e in tr.inPath.graphEdges) if (!g.EdgeOpen(e, m)) tripsLocked++;
            }
            crossings += tripsLocked;
            gateLines.Append($"mask {m}: {home} cars stay home (d0 {homeBy[0]}, d1 {homeBy[1]}, d2 {homeBy[2]}, d3 {homeBy[3]}), day-3 trips {dd.trips.Count}; ");
        }
        Check(crossings == 0, $"no car route crosses a locked gate (6 masks; {crossings} locked edges used) — {gateLines}");
        var starterRoutes = 0;
        for (int c = 0; c < simA.Roster.CarCount; c++) if (simA.Roster.cars[c].district == 2 && simA.CarRouteFor(c, 1).exit >= 0) starterRoutes++;
        Check(starterRoutes == 0, "Snootington cars stay home while Snootington is locked (its gate is between them and every exit)");

        // ---- every timetable car path: lanes are graph lanes, speeds sane, ends at the slot / in the tunnel
        int badPaths = 0; float maxV = 0f, maxDur = 0f, minDur = float.MaxValue;
        var d0 = simA.Day(0, -1);
        foreach (var tr in d0.trips)
        {
            foreach (var p in new[] { tr.outPath, tr.inPath })
            {
                for (int i = 0; i < p.Count; i++) { if (float.IsNaN(p.v[i]) || p.v[i] < 0f) badPaths++; maxV = Mathf.Max(maxV, p.v[i]); }
                maxDur = Mathf.Max(maxDur, p.Duration); minDur = Mathf.Min(minDur, p.Duration);
            }
            var hs = g.houses[simA.Roster.cars[tr.car].house];
            var slot = hs.CarSlot(simA.Roster.cars[tr.car].slot);
            if (Vector3.Distance(tr.outPath.pts[0], slot.position) > 0.01f || Vector3.Distance(tr.inPath.pts[tr.inPath.Count - 1], slot.position) > 0.01f) badPaths++;
            if ((tr.outPath.flags[0] & CarSegFlags.Reverse) == 0) badPaths++;
        }
        Check(badPaths == 0 && maxV <= LifeSim.LaneLimit + 0.01f, $"car paths: start reversing out of the slot, end nose-in at the slot; max speed {maxV:F1} m/s (truck is faster); leg duration {minDur:F1}–{maxDur:F1} s");

        // ---- cost: a day build, and the per-frame queries for every agent
        sb.AppendLine($"  day build: {buildMs:F2} ms average over {days} days (once per in-game day, at the day roll)");
        var day = simA.Day(5, -1);
        int nr = simA.Roster.ResidentCount, nc = simA.Roster.CarCount;
        sw.Restart();
        int found = 0;
        for (int f = 0; f < 1000; f++)
        {
            double t = LifeDay.HourToMs(7.5f) + f * 16.6;
            for (int r = 0; r < nr; r++) { int ex = day.ExcursionAt(r, t); if (ex >= 0 && day.Evaluate(ex, t, out _, out _, out _, out _, out _)) found++; }
            for (int c = 0; c < nc; c++) if (day.CarAt(c, t, out _, out _, out _) != CarPhase.Home) found++;
        }
        sw.Stop();
        double perFrame = sw.Elapsed.TotalMilliseconds / 1000.0;
        Check(perFrame < WorldLifeTuning.Budget.ScheduleMs, $"brute-force every resident + car every frame at 07:30: {perFrame:F4} ms/frame ({found / 1000} out on average; the directors do far less)");

        // ---- C's queries
        int carrierOk = 0, carrierNone = 0;
        for (int h = 0; h < simA.Roster.households.Length; h++)
        {
            int r = simA.PickCarrier(simA.Roster.households[h].stopId, 5 * 24 + 7.2, 0.4f, out _);
            if (r >= 0 && simA.Roster.residents[r].household == h) carrierOk++; else carrierNone++;
        }
        Check(carrierOk > 0, $"PickCarrier at 07:12: {carrierOk} households have someone home to carry the bag, {carrierNone} fall back to the drop-in");

        sb.AppendLine(fails == 0 ? "ALL PASS" : $"{fails} FAIL");
        return sb.ToString();
    }

    static ulong HashDay(LifeSim sim, LifeDay d)
    {
        ulong h = 1469598103934665603UL;
        void M(ulong x) { h = LifeHash.Mix(h ^ x); }
        void F(float x) { M((ulong)(long)Math.Round(x * 1000.0)); }
        foreach (var t in d.trips) { M(t.car); M(t.driver); M(t.exit); M((ulong)t.kind); M((ulong)t.depart); M((ulong)t.awayFrom); M((ulong)t.ret); M((ulong)t.park); F(t.outPath.Length); F(t.inPath.Duration); }
        foreach (var e in d.excursions)
        {
            M(e.resident); M((ulong)e.kind); M((ulong)e.t0); M((ulong)e.t1); M((ulong)e.segCount); M(e.intoCar ? 1UL : 0UL);
            for (int k = e.seg0; k < e.seg0 + e.segCount; k++) { var s = d.segments[k]; M((ulong)s.t0); M((ulong)s.t1); F(s.s0); F(s.s1); }
        }
        // Positions every 10 s of real time through the day.
        for (int t = 0; t < LifeDay.MsPerDay; t += 10000)
        {
            for (int r = 0; r < sim.Roster.ResidentCount; r++)
            {
                int ex = d.ExcursionAt(r, t);
                if (ex < 0 || !d.Evaluate(ex, t, out var p, out _, out _, out _, out _)) continue;
                M((ulong)r); F(p.x); F(p.y); F(p.z);
            }
            for (int c = 0; c < sim.Roster.CarCount; c++)
            {
                var ph = d.CarAt(c, t, out _, out float arc, out _);
                if (ph == CarPhase.Home) continue;
                M((ulong)c); M((ulong)ph); F(arc);
            }
        }
        return h;
    }

    // §4.3 targets: per band (05–06, 06–09, 09–16, 16–20, 20–23, 23–05) the (low, high) people on foot per district, and
    // moving cars town-wide.
    static readonly string[] BandNames = { "05–06", "06–09", "09–16", "16–20", "20–23", "23–05" };
    static readonly float[,] BandHours = { { 5, 6 }, { 6, 9 }, { 9, 16 }, { 16, 20 }, { 20, 23 }, { 23, 29 } };
    static readonly Vector2[][] Targets =
    {
        new[] { new Vector2(1, 1), new Vector2(8, 12), new Vector2(4, 6), new Vector2(8, 12), new Vector2(2, 2), new Vector2(0, 1) },   // Mildly Pleasant
        new[] { new Vector2(1, 1), new Vector2(8, 8), new Vector2(6, 6), new Vector2(8, 8), new Vector2(2, 2), new Vector2(0, 0) },     // Minivan Meadows
        new[] { new Vector2(0, 0), new Vector2(4, 4), new Vector2(3, 3), new Vector2(5, 5), new Vector2(1, 1), new Vector2(0, 0) },     // Snootington
        new[] { new Vector2(0, 0), new Vector2(2, 2), new Vector2(2, 2), new Vector2(3, 3), new Vector2(1, 1), new Vector2(0, 0) },     // Bluffs
        new[] { new Vector2(1, 1), new Vector2(8, 12), new Vector2(2, 4), new Vector2(8, 12), new Vector2(2, 2), new Vector2(0, 1) },   // moving cars, town-wide
    };
    static readonly string[] RowNames = { "Mildly Pleasant", "Minivan Meadows", "Snootington", "Bluffs", "Moving cars" };

    /// <summary>Average people on foot per district (and moving cars town-wide) per §4.3 band over <paramref name="days"/>
    /// days, sampled every 5 real seconds. A cell passes inside [low × 0.75, high × 1.25] (a "0" cell: ≤ 0.3).</summary>
    public static float[,] PopulationTable(LifeSim sim, int days, int mask, out int cellFails, out string text)
    {
        var sum = new double[5, 6]; var n = new int[5, 6];
        for (int d = 0; d < days; d++)
        {
            var day = sim.Day(d, mask);
            for (int t = 0; t < LifeDay.MsPerDay; t += 5000)
            {
                float hour = t / (float)LifeDay.MsPerHour;
                int band = -1;
                for (int b = 0; b < 6; b++)
                {
                    float h0 = BandHours[b, 0], h1 = BandHours[b, 1];
                    if ((hour >= h0 && hour < h1) || (h1 > 24f && hour + 24f >= h0 && hour + 24f < h1)) { band = b; break; }
                }
                if (band < 0) continue;
                for (int dist = 0; dist < 4; dist++) { sum[dist, band] += sim.CountOnFoot(day, dist, t); n[dist, band]++; }
                sum[4, band] += sim.CountMovingCars(day, -1, t); n[4, band]++;
            }
        }
        var avg = new float[5, 6];
        cellFails = 0;
        var sb = new StringBuilder($"  §4.3 population (avg over {days} days, mask {mask}; [target]):\n");
        sb.Append("    ").Append("".PadRight(16));
        foreach (var b in BandNames) sb.Append(b.PadLeft(16));
        sb.AppendLine();
        for (int row = 0; row < 5; row++)
        {
            sb.Append("    ").Append(RowNames[row].PadRight(16));
            for (int b = 0; b < 6; b++)
            {
                avg[row, b] = n[row, b] > 0 ? (float)(sum[row, b] / n[row, b]) : 0f;
                var tg = Targets[row][b];
                bool ok = tg.y <= 0f ? avg[row, b] <= 0.3f : avg[row, b] >= tg.x * 0.75f && avg[row, b] <= tg.y * 1.25f;
                if (!ok) cellFails++;
                string target = tg.x == tg.y ? $"{tg.x:0}" : $"{tg.x:0}–{tg.y:0}";
                sb.Append($"{avg[row, b]:F2} [{target}]{(ok ? " " : "!")}".PadLeft(16));
            }
            sb.AppendLine();
        }
        text = sb.ToString();
        return avg;
    }

    // =================================================================================================================
    // Play-mode probe (eval: Setup_WorldLifeTraffic.Probe.Install(); ... Probe.Installed.Report())
    // =================================================================================================================

    public sealed class Probe : WorldLifeDirector
    {
        public static Probe Installed;
        public override WorldLifeSlot Slot => WorldLifeSlot.Rules;

        public int samples, laneSamples, overtakeSamples, gapPairs, frames;
        public float maxLaneDev, maxLaneDevAll, minGap = float.PositiveInfinity, maxShown, maxMoving;
        public string maxLaneDevAt = "", minGapAt = "";
        public readonly List<string> honks = new List<string>();
        public float sampleEvery = 0.2f;
        float nextSample;
        List<Vector4> roadSegs;      // (ax, az, bx, bz)
        Dictionary<long, List<int>> grid;

        public static Probe Install()
        {
            Uninstall();
            Installed = new Probe();
            WorldLifeRunner.Register(Installed);
            TrafficDirector.CarHonked += Installed.OnHonk;
            return Installed;
        }

        public static void Uninstall()
        {
            if (Installed == null) return;
            WorldLifeRunner.Unregister(Installed);
            TrafficDirector.CarHonked -= Installed.OnHonk;
            Installed = null;
        }

        void OnHonk(CarAgent a, int why) => honks.Add($"{Time.time:F2}:{a.id}:{why}:stuck{a.stuckTime:F1}");

        public void ResetStats()
        {
            samples = laneSamples = overtakeSamples = gapPairs = frames = 0;
            maxLaneDev = maxLaneDevAll = 0f; minGap = float.PositiveInfinity; maxShown = maxMoving = 0f;
            maxLaneDevAt = minGapAt = ""; honks.Clear();
        }

        void BuildGrid(StreetGraph g)
        {
            roadSegs = new List<Vector4>();
            grid = new Dictionary<long, List<int>>();
            for (int e = 0; e < g.EdgeCount; e++)
            {
                var ed = g.edges[e];
                if (ed.kind != StreetGraph.EdgeKind.Road) continue;
                Vector3 a = g.nodes[ed.a].pos, b = g.nodes[ed.b].pos;
                int id = roadSegs.Count;
                roadSegs.Add(new Vector4(a.x, a.z, b.x, b.z));
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / 8f) - 1, x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / 8f) + 1;
                int z0 = Mathf.FloorToInt(Mathf.Min(a.z, b.z) / 8f) - 1, z1 = Mathf.FloorToInt(Mathf.Max(a.z, b.z) / 8f) + 1;
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                {
                    long k = ((long)x << 32) ^ (uint)z;
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(id);
                }
            }
        }

        float LaneDistance(Vector3 p)
        {
            long k = ((long)Mathf.FloorToInt(p.x / 8f) << 32) ^ (uint)Mathf.FloorToInt(p.z / 8f);
            if (!grid.TryGetValue(k, out var l)) return 99f;
            float best = 99f;
            foreach (int i in l)
            {
                var s = roadSegs[i];
                Vector2 a = new Vector2(s.x, s.y), b = new Vector2(s.z, s.w), q = new Vector2(p.x, p.z);
                Vector2 ab = b - a;
                float u = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(q, a + ab * u));
            }
            return best;
        }

        public override void LateTick(WorldLifeContext ctx)
        {
            frames++;
            var td = TrafficDirector.Instance;
            if (td == null) return;
            if (grid == null) BuildGrid(ctx.Graph);
            maxShown = Mathf.Max(maxShown, td.ShownMoving);
            maxMoving = Mathf.Max(maxMoving, td.MovingNow);
            if (Time.time < nextSample) return;
            nextSample = Time.time + sampleEvery;
            samples++;
            var list = td.Agents;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.parkedShell || a.path == null) continue;
                int seg = a.path.SegmentAt(a.s);
                var f = a.path.flags[seg];
                bool lane = (f & CarSegFlags.Lane) != 0 && (f & (CarSegFlags.Reverse | CarSegFlags.Driveway | CarSegFlags.Tunnel)) == 0;
                if (!lane) continue;
                // Measure only away from the lane's ends (the pull-out / turn-in joins).
                if (a.s < 6f || a.s > a.path.Length - 8f) continue;
                float dev = LaneDistance(a.position);
                bool special = a.overtake != 0 || Mathf.Abs(a.lateral) > 0.01f || a.slide.sqrMagnitude > 1e-4f || a.shuntT >= 0f;
                if (special) { overtakeSamples++; maxLaneDevAll = Mathf.Max(maxLaneDevAll, dev); continue; }
                laneSamples++;
                if (dev > maxLaneDev) { maxLaneDev = dev; maxLaneDevAt = $"{a.id} s {a.s:F1}/{a.path.Length:F1} @ {a.position:F1}"; }
            }
            // Same-lane gaps: pairs heading the same way, one behind the other within 1.6 m laterally.
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.parkedShell || a.path == null || a.overtake != 0) continue;
                Vector3 fa = Quaternion.Euler(0f, a.yaw, 0f) * Vector3.forward;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    var b = list[j];
                    if (b.parkedShell || b.path == null || b.overtake != 0) continue;
                    Vector3 fb = Quaternion.Euler(0f, b.yaw, 0f) * Vector3.forward;
                    if (Vector3.Dot(fa, fb) < 0.9f) continue;
                    Vector3 d = b.position - a.position; d.y = 0f;
                    float lon = Vector3.Dot(d, fa);
                    float lat = Vector3.Dot(d, new Vector3(fa.z, 0f, -fa.x));
                    if (lon <= 0f || Mathf.Abs(lat) > 1.6f || lon > 40f) continue;
                    float gap = lon - a.halfExtents.z - b.halfExtents.z;
                    gapPairs++;
                    if (gap < minGap) { minGap = gap; minGapAt = $"{a.id} behind {b.id} at {a.position:F1} (v {a.v:F1}/{b.v:F1})"; }
                }
            }
        }

        public string Report()
        {
            var td = TrafficDirector.Instance;
            return $"probe: frames {frames}, samples {samples}; lane samples {laneSamples} max deviation {maxLaneDev:F3} m ({maxLaneDevAt}); " +
                   $"overtake/pull-over samples {overtakeSamples} (max {maxLaneDevAll:F2} m); same-lane pairs {gapPairs} min bumper gap {(float.IsInfinity(minGap) ? "n/a" : minGap.ToString("F2"))} m ({minGapAt}); " +
                   $"max shown {maxShown}, max moving {maxMoving}; honks {honks.Count}; {(td != null ? td.Describe() : "")}";
        }
    }
}

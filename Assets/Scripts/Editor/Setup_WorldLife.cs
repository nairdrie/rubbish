using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// WorldLife umbrella setup (Docs/WorldLife.md §12.2; W0). Runs after Neighborhood in the CoreLoop.md §5 order and calls
/// each package's idempotent <c>Setup_WorldLife&lt;X&gt;.Run()</c> in turn: Graph check (A) → Knock (B) → Residents (C)
/// → Traffic (D) → Rules (E) → Kids (§12.K) → Polish (G). A package whose setup doesn't exist yet is skipped (it's looked up by name, so this compiles
/// before the packages land). Then it validates the contract's street graphs. Touches no scene itself; never saves
/// Bootstrap. Idempotent.
/// </summary>
public static class Setup_WorldLife
{
    /// <summary>Package setups, in run order.</summary>
    public static readonly string[] PackageSetups =
    {
        "Setup_WorldLifeGraph", "Setup_WorldLifeKnock", "Setup_WorldLifeResidents", "Setup_WorldLifeTraffic", "Setup_WorldLifeRules",
        "Setup_WorldLifeKids", "Setup_WorldLifePolish",
    };

    [MenuItem("Rubbish/Setup/WorldLife")]
    public static void Run() => Build();

    public static string Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Setup_WorldLife] Exit play mode first.");
            return "ERROR: play mode";
        }
        var sb = new StringBuilder("[Setup_WorldLife]\n");
        var asm = typeof(Setup_WorldLife).Assembly;
        foreach (var name in PackageSetups)
        {
            var t = asm.GetType(name);
            var m = t?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (t == null) { sb.AppendLine($"  {name}: not built yet (skipped)"); continue; }
            if (m == null) { sb.AppendLine($"  {name}: FAIL no public static Run()"); continue; }
            try
            {
                m.Invoke(null, null);
                sb.AppendLine($"  {name}: ran");
            }
            catch (TargetInvocationException e)
            {
                Debug.LogException(e.InnerException ?? e);
                sb.AppendLine($"  {name}: FAIL {(e.InnerException ?? e).Message}");
            }
        }
        sb.Append(ValidateGraphsReport());
        string report = sb.ToString();
        if (report.Contains("FAIL")) Debug.LogWarning(report); else Debug.Log(report);
        return report;
    }

    /// <summary>Validate StreetGraph.Test() and (when package A has written it) Resources/StreetGraph.asset.</summary>
    [MenuItem("Rubbish/WorldLife/Validate Street Graphs")]
    public static void ValidateGraphs() => Debug.Log("[Setup_WorldLife]\n" + ValidateGraphsReport());

    public static string ValidateGraphsReport()
    {
        var sb = new StringBuilder();
        var errors = new List<string>();
        var test = StreetGraph.BuildTest();
        int n = test.Validate(errors);
        sb.AppendLine($"  StreetGraph.Test(): {(n == 0 ? "ok" : "FAIL " + n + " errors")} · {test.Describe().Replace("\n", "\n    ")}");
        for (int i = 0; i < Mathf.Min(10, errors.Count); i++) sb.AppendLine("    " + errors[i]);
        UnityEngine.Object.DestroyImmediate(test);

        var asset = AssetDatabase.LoadAssetAtPath<StreetGraph>(StreetGraph.AssetPath);
        if (asset == null) sb.AppendLine($"  {StreetGraph.AssetPath}: not written yet (package A)");
        else
        {
            errors.Clear();
            n = asset.Validate(errors);
            sb.AppendLine($"  {StreetGraph.AssetPath}: {(n == 0 ? "ok" : "FAIL " + n + " errors")} · {asset.Describe().Replace("\n", "\n    ")}");
            for (int i = 0; i < Mathf.Min(20, errors.Count); i++) sb.AppendLine("    " + errors[i]);
        }
        return sb.ToString();
    }

    /// <summary>Play mode: the WorldLife.* marker table (avg / p99 / max ms over the last 600 frames) in the console.</summary>
    [MenuItem("Rubbish/WorldLife/Profiler Report")]
    public static void ProfilerReport() => Debug.Log("[WorldLife]\n" + WorldLifeProfiler.Report());

    // =================================================================================================================
    // Contract self-test (pure: no scene, no GameSession writes). Menu, or eval: return Setup_WorldLife.SelfTest();
    // =================================================================================================================

    /// <summary>Bytes allocated by <paramref name="body"/>, read as "GC Allocated In Frame" deltas inside one call and
    /// calibrated against a known 64 B allocation (G: GC.GetAllocatedBytesForCurrentThread reads 0 in this editor, so the
    /// old check could never fail). <paramref name="calibrated"/> is false when the meter didn't see the probe.</summary>
    public static long AllocBytes(Action body, out bool calibrated)
    {
        using (var r = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC Allocated In Frame"))
        {
            long c0 = r.CurrentValue;
            allocProbe = new object[4];
            calibrated = r.CurrentValue - c0 >= 32;
            long a0 = r.CurrentValue;
            body();
            return r.CurrentValue - a0;
        }
    }
    static object allocProbe;

    [MenuItem("Rubbish/WorldLife/Contract Self Test")]
    public static void SelfTestMenu()
    {
        string r = SelfTest();
        if (r.Contains("FAIL")) Debug.LogWarning(r); else Debug.Log(r);
    }

    public static string SelfTest()
    {
        var sb = new StringBuilder("[WorldLife W0 self-test]\n");
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); }

        // ---- street graph: validation, determinism, Unity-serializer round trip
        var errors = new List<string>();
        var g = StreetGraph.BuildTest();
        int ge = g.Validate(errors);
        Check(ge == 0, $"StreetGraph.Test() validates ({g.NodeCount} nodes, {g.EdgeCount} edges, {g.houses.Length} houses, {g.loops.Length} loops){(ge > 0 ? ": " + errors[0] : "")}");
        var g2 = StreetGraph.BuildTest();
        bool same = g.NodeCount == g2.NodeCount && g.EdgeCount == g2.EdgeCount;
        for (int i = 0; same && i < g.NodeCount; i++) same = g.nodes[i].pos == g2.nodes[i].pos && g.nodes[i].kind == g2.nodes[i].kind;
        for (int i = 0; same && i < g.EdgeCount; i++) same = g.edges[i].a == g2.edges[i].a && g.edges[i].b == g2.edges[i].b && g.edges[i].flags == g2.edges[i].flags;
        Check(same, "StreetGraph.Test() is identical on every build");
        var g3 = ScriptableObject.CreateInstance<StreetGraph>();
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(g), g3);
        errors.Clear();
        Check(g3.Validate(errors) == 0 && g3.NodeCount == g.NodeCount && g3.houses[2].carSlot1.rotation == g.houses[2].carSlot1.rotation
              && g3.loops[1].nodes.Length == g.loops[1].nodes.Length, "StreetGraph survives a Unity-serializer round trip (Pose, ushort, flags, nested arrays)");
        var reach = new bool[g.NodeCount];
        var q = new Queue<int>();
        int mm = g.HouseIndex("mm-test-1");
        g.Flood(g.houses[mm].driveOut, reach, q, walk: false, unlockedMask: 1, district: -1);
        bool lockedOut = !reach[g.exits[0].node];
        g.Flood(g.houses[mm].driveOut, reach, q, walk: false, unlockedMask: 3, district: -1);
        Check(lockedOut && reach[g.exits[0].node], "gate: a Meadows car reaches the exit only while Meadows is unlocked");
        UnityEngine.Object.DestroyImmediate(g); UnityEngine.Object.DestroyImmediate(g2); UnityEngine.Object.DestroyImmediate(g3);

        // ---- LifeHash: stable values (a change here breaks every machine's agreement)
        ulong h1 = LifeHash.U64(1234, LifeKind.Resident, 17, 3, 9), h2 = LifeHash.U64(1234, LifeKind.Resident, 17, 3, 9);
        ulong h3 = LifeHash.U64(1234, LifeKind.Resident, 17, 4, 9);
        Check(h1 == h2 && h1 != h3 && LifeHash.Str("mm-12") == LifeHash.Str("mm-12"), $"LifeHash is a pure function (U64 = {h1:X16})");
        int[] hist = new int[4];
        int[] weights = { 1, 2, 3, 4 };
        for (int i = 0; i < 10000; i++) hist[LifeHash.Pick(LifeHash.U64(7, LifeKind.Town, i, 0), weights)]++;
        Check(Mathf.Abs(hist[3] / 10000f - 0.4f) < 0.03f && Mathf.Abs(hist[0] / 10000f - 0.1f) < 0.02f, $"LifeHash.Pick honours integer weights ({hist[0]}/{hist[1]}/{hist[2]}/{hist[3]})");

        // ---- LifeBodies: queries against brute force, re-binning, zero GC after warm-up
        LifeBodies.Clear();
        var bodies = new List<ProbeBody>();
        ulong st = 42;
        for (int i = 0; i < 200; i++)
        {
            var b = new ProbeBody
            {
                id = LifeId.Resident(i),
                kind = i % 10 == 0 ? LifeBodyKind.Car : i % 7 == 0 ? LifeBodyKind.Dog : LifeBodyKind.Person,
                pos = new Vector3(LifeHash.Float01(LifeHash.Next(ref st)) * 400f - 50f, 0f, LifeHash.Float01(LifeHash.Next(ref st)) * 600f - 30f),
                rot = Quaternion.Euler(0f, LifeHash.Float01(LifeHash.Next(ref st)) * 360f, 0f),
            };
            b.he = b.kind == LifeBodyKind.Car ? new Vector3(0.95f, 0.75f, 2.2f) : new Vector3(0.3f, 0.85f, 0.3f);
            bodies.Add(b);
            LifeBodies.Register(b);
        }
        LifeBodies.Sync();
        var res = new List<ILifeBody>(64);
        int qMismatch = 0, boxMismatch = 0, found = 0, boxFound = 0;
        for (int k = 0; k < 400; k++)
        {
            var c = new Vector3(LifeHash.Float01(LifeHash.Next(ref st)) * 400f - 50f, 0f, LifeHash.Float01(LifeHash.Next(ref st)) * 600f - 30f);
            float r = 5f + LifeHash.Float01(LifeHash.Next(ref st)) * 40f;
            res.Clear();
            LifeBodies.Query(c, r, res);
            int brute = 0;
            foreach (var b in bodies)
            {
                float rr = r + Mathf.Sqrt(b.he.x * b.he.x + b.he.z * b.he.z), dx = b.pos.x - c.x, dz = b.pos.z - c.z;
                if (dx * dx + dz * dz <= rr * rr) brute++;
            }
            if (brute != res.Count) qMismatch++;
            found += res.Count;

            // A truck-sized box at a random yaw vs exact SAT on the body rectangles (brute force over every body).
            var rot = Quaternion.Euler(0f, LifeHash.Float01(LifeHash.Next(ref st)) * 360f, 0f);
            var he = new Vector3(1.3f, 1.45f, 4f);
            Vector3 bc = c + Vector3.up * 1.5f;
            res.Clear();
            LifeBodies.QueryBox(bc, rot, he, res, LifeBodyKind.All, 0.35f, false);
            int bruteBox = 0;
            foreach (var b in bodies) if (RectsOverlap(bc, rot, new Vector2(he.x + 0.35f, he.z + 0.35f), b.pos, b.rot, new Vector2(b.he.x, b.he.z))) bruteBox++;
            if (bruteBox != res.Count) boxMismatch++;
            boxFound += res.Count;
        }
        Check(qMismatch == 0, $"LifeBodies.Query matches brute force on 400 random queries ({found} hits)");
        Check(boxMismatch == 0, $"LifeBodies.QueryBox matches brute-force SAT on 400 random truck boxes ({boxFound} hits)");
        // Dense crowd (200 bodies in 60 × 60 m, straddling cell borders) for real box coverage.
        foreach (var b in bodies) b.pos = new Vector3(LifeHash.Float01(LifeHash.Next(ref st)) * 60f - 8f, 0f, LifeHash.Float01(LifeHash.Next(ref st)) * 60f - 8f);
        LifeBodies.Sync();
        boxMismatch = 0; boxFound = 0;
        for (int k = 0; k < 400; k++)
        {
            var c = new Vector3(LifeHash.Float01(LifeHash.Next(ref st)) * 60f - 8f, 1.5f, LifeHash.Float01(LifeHash.Next(ref st)) * 60f - 8f);
            var rot = Quaternion.Euler(0f, LifeHash.Float01(LifeHash.Next(ref st)) * 360f, 0f);
            var he = new Vector3(1.3f, 1.45f, 3f + LifeHash.Float01(LifeHash.Next(ref st)) * 2f);
            res.Clear();
            LifeBodies.QueryBox(c, rot, he, res, LifeBodyKind.All, 0.35f, false);
            int bruteBox = 0;
            foreach (var b in bodies) if (RectsOverlap(c, rot, new Vector2(he.x + 0.35f, he.z + 0.35f), b.pos, b.rot, new Vector2(b.he.x, b.he.z))) bruteBox++;
            if (bruteBox != res.Count) boxMismatch++;
            boxFound += res.Count;
        }
        Check(boxMismatch == 0 && boxFound > 400, $"LifeBodies.QueryBox matches brute-force SAT in a dense crowd ({boxFound} hits over 400 boxes)");
        foreach (var b in bodies) b.pos += new Vector3(37f, 0f, -21f);
        LifeBodies.Sync();
        res.Clear();
        LifeBodies.Query(new Vector3(150f, 0f, 270f), 2000f, res);
        Check(res.Count == 200 && ReferenceEquals(LifeBodies.Find(LifeId.Resident(7)), bodies[7]), "LifeBodies re-bins moved bodies; Find by id");

        // Warm-up: the crowd drifts 20 m, so the cell-list pool and the lists' capacities reach their steady state
        // (the first visit to a new cell or a crowd denser than a list's capacity allocates once; then it's pooled).
        // This crowd is far denser than a street (200 bodies in a few cells), so the pooled lists' capacities take a few
        // windows to settle; the check is that allocations stop (the last window is 0 B), with every window reported.
        for (int w = 0; w < 1000; w++) Frame(bodies, res);
        var sw = new System.Diagnostics.Stopwatch();
        var windows = new long[4];
        bool calibrated = true;
        for (int k = 0; k < windows.Length; k++)
        {
            windows[k] = AllocBytes(() =>
            {
                sw.Start();
                for (int f = 0; f < 1000; f++) Frame(bodies, res);
                sw.Stop();
            }, out bool cal);
            calibrated &= cal;
        }
        Check(calibrated && windows[windows.Length - 1] == 0, $"LifeBodies: allocations stop after warm-up (move 200 + Sync + 20 Query + 20 QueryBox + Nearest; 4 windows of 1000 frames: {string.Join(" / ", windows)} B; meter {(calibrated ? "calibrated" : "NOT calibrated")}), {sw.Elapsed.TotalMilliseconds / 4000.0:F4} ms/frame");
        foreach (var b in bodies) LifeBodies.Unregister(b);
        Check(LifeBodies.Count == 0 && LifeBodies.Find(LifeId.Resident(7)) == null, "LifeBodies.Unregister empties the hash");
        LifeBodies.Clear();

        // ---- LifeOverrides
        LifeOverrides.Clear();
        LifeOverrides.Add(new NpcOverride { id = LifeId.Resident(3), day = 5, delayCs = 1000, kind = NpcHitKind.Flatten, untilTick = 900 });
        LifeOverrides.Add(new NpcOverride { id = LifeId.Resident(3), day = 5, delayCs = 250, kind = NpcHitKind.Nudge });
        LifeOverrides.Add(new NpcOverride { id = LifeId.Resident(3), day = 4, delayCs = 999 });
        bool sum = Mathf.Approximately(LifeOverrides.DelaySeconds(LifeId.Resident(3), 5), 12.5f) && LifeOverrides.HoldUntilTick(LifeId.Resident(3)) == 900;
        LifeOverrides.Prune(5);
        Check(sum && LifeOverrides.All.Count == 2 && LifeOverrides.DelaySeconds(LifeId.Resident(3), 4) == 0f, "LifeOverrides sums delays per (id, day) and prunes old days");
        LifeOverrides.Clear();

        // ---- tuning formulas (§8.1 / §9.1)
        Vector3 v6 = WorldLifeTuning.Hits.FlopLaunch(new Vector3(0f, 0f, 6f), Vector3.right);
        // s = 6: forward 3.3, side 2.72, up clamp(5.3, 4, 8) = 5.3
        Check(Mathf.Abs(v6.z - 3.3f) < 0.01f && Mathf.Abs(v6.x - 2.72f) < 0.01f && Mathf.Abs(v6.y - 5.3f) < 0.01f, $"FlopLaunch(6 m/s) = {v6}");
        Vector3 v40 = WorldLifeTuning.Hits.FlopLaunch(new Vector3(0f, 0f, 40f), Vector3.right);
        Check(new Vector2(v40.x, v40.z).magnitude <= 10.001f && Mathf.Approximately(v40.y, 8f), "FlopLaunch caps horizontal 10 m/s, vertical 8 m/s");
        Check(Mathf.Approximately(WorldLifeTuning.Fines.Escalation(1), 1f) && Mathf.Approximately(WorldLifeTuning.Fines.Escalation(5), 2f)
              && Mathf.Approximately(WorldLifeTuning.Fines.Escalation(20), 3f), "Escalation ×1 / ×2 (5th) / ×3 cap");
        Check(WorldLifeTuning.Knock.Length == Enum.GetValues(typeof(KnockKind)).Length, "every KnockKind has a §6.1 row");
        Check(Mathf.Approximately(WorldLifeTuning.Fines.PropFine(3, KnockKind.Hydrant), 60f) && Mathf.Approximately(WorldLifeTuning.Fines.FlattenFine(0), 3f),
              "fines: Bluffs hydrant $60 (×4), Mildly Pleasant flatten $3");
        Check(WorldLifeTuning.Hits.PersonResult(0.5f) == NpcHitKind.None && WorldLifeTuning.Hits.PersonResult(1.5f) == NpcHitKind.Nudge
              && WorldLifeTuning.Hits.PersonResult(2.5f) == NpcHitKind.Flatten, "person result: <0.8 none, 0.8–2.5 nudge, ≥2.5 flatten");

        // ---- the truck sweep box
        var sweep = new TruckSweep { center = new Vector3(10f, 1.8f, 5f), rotation = Quaternion.Euler(0f, 90f, 0f), halfExtents = new Vector3(1.3f, 1.45f, 3.5f) };
        Check(sweep.Contains(new Vector3(13f, 1f, 5f)) && !sweep.Contains(new Vector3(10f, 1f, 7.5f)) && sweep.Contains(new Vector3(10f, 1f, 6.5f), 0.35f),
              "TruckSweep.Contains honours rotation and pad");

        sb.AppendLine(fails == 0 ? "ALL PASS" : $"{fails} FAIL");
        return sb.ToString();
    }

    static void Frame(List<ProbeBody> bodies, List<ILifeBody> res)
    {
        for (int i = 0; i < bodies.Count; i++) bodies[i].pos.x += 0.02f;
        LifeBodies.Sync();
        for (int k = 0; k < 20; k++)
        {
            res.Clear();
            LifeBodies.Query(new Vector3(k * 20f, 0f, k * 30f), 16f, res);
            res.Clear();
            LifeBodies.QueryBox(new Vector3(k * 20f, 1.5f, k * 30f), Quaternion.Euler(0f, k * 17f, 0f), new Vector3(1.3f, 1.45f, 4.5f), res, LifeBodyKind.All, 0.35f, true);
        }
        LifeBodies.Nearest(new Vector3(100f, 0f, 100f), 20f);
        res.Clear();
    }

    /// <summary>Exact 2D overlap of two yawed rectangles (SAT, written independently of LifeBodies for the test).</summary>
    static bool RectsOverlap(Vector3 ca, Quaternion ra, Vector2 ha, Vector3 cb, Quaternion rb, Vector2 hb)
    {
        Vector2 Ax(Quaternion r) { Vector3 v = r * Vector3.right; return new Vector2(v.x, v.z).normalized; }
        Vector2 Az(Quaternion r) { Vector3 v = r * Vector3.forward; return new Vector2(v.x, v.z).normalized; }
        Vector2[] axes = { Ax(ra), Az(ra), Ax(rb), Az(rb) };
        Vector2 d = new Vector2(cb.x - ca.x, cb.z - ca.z);
        foreach (var ax in axes)
        {
            float pa = Mathf.Abs(Vector2.Dot(Ax(ra), ax)) * ha.x + Mathf.Abs(Vector2.Dot(Az(ra), ax)) * ha.y;
            float pb = Mathf.Abs(Vector2.Dot(Ax(rb), ax)) * hb.x + Mathf.Abs(Vector2.Dot(Az(rb), ax)) * hb.y;
            if (Mathf.Abs(Vector2.Dot(d, ax)) > pa + pb) return false;
        }
        return true;
    }

    /// <summary>
    /// Play-mode probe for the runner contract (editor only): a director in every slot's place that counts its calls and
    /// a sensor client that records the sweeps it gets. Install / Uninstall from an eval.
    /// </summary>
    public sealed class Probe : WorldLifeDirector, ITruckSensorClient
    {
        public static Probe Installed;
        readonly WorldLifeSlot slot;
        public int wakes, sleeps, ticks, fixedTicks, lateTicks, sweeps, maxSweepsPerStep, stepSweeps, lastStep = -1;
        public TruckSweep lastSweep;
        public WorldLifeContext lastCtx;
        public Probe(WorldLifeSlot slot) { this.slot = slot; }
        public override WorldLifeSlot Slot => slot;
        public override void Wake(WorldLifeContext ctx) { wakes++; lastCtx = ctx; }
        public override void Sleep() { sleeps++; }
        public override void Tick(WorldLifeContext ctx) { ticks++; lastCtx = ctx; }
        public override void FixedTick(WorldLifeContext ctx) { fixedTicks++; }
        public override void LateTick(WorldLifeContext ctx) { lateTicks++; }
        public void Sense(in TruckSweep s)
        {
            sweeps++;
            if (s.tick != lastStep) { lastStep = s.tick; stepSweeps = 0; }
            stepSweeps++;
            if (stepSweeps > maxSweepsPerStep) maxSweepsPerStep = stepSweeps;
            lastSweep = s;
        }

        public static Probe Install(WorldLifeSlot slot = WorldLifeSlot.Rules)
        {
            Uninstall();
            Installed = new Probe(slot);
            WorldLifeRunner.Register(Installed);
            WorldLifeRunner.AddSensorClient(Installed);
            return Installed;
        }

        public static void Uninstall()
        {
            if (Installed == null) return;
            WorldLifeRunner.Unregister(Installed);
            WorldLifeRunner.RemoveSensorClient(Installed);
            Installed = null;
        }

        public override string ToString() =>
            $"wakes {wakes} sleeps {sleeps} ticks {ticks} fixed {fixedTicks} late {lateTicks} sweeps {sweeps} (max {maxSweepsPerStep}/step)";
    }

    /// <summary>A fake person / dog / car for tests (editor only).</summary>
    public sealed class ProbeBody : ILifeBody
    {
        public int hits;
        public NpcHit lastHit;
        public LifeId id;
        public LifeBodyKind kind;
        public Vector3 pos, he;
        public Quaternion rot = Quaternion.identity;
        public LifeId Id => id;
        public LifeBodyKind BodyKind => kind;
        public Vector3 Position => pos;
        public Quaternion Rotation => rot;
        public Vector3 HalfExtents => he;
        public bool Hittable => true;
        public string StopId => "";
        public void PlayHit(in NpcHit hit) { hits++; lastHit = hit; }
    }
}

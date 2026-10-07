using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

/// <summary>
/// WorldLife package G (Docs/WorldLife.md §12.1 "Polish + perf", §12.G): the umbrella's last step and G's play-test kit.
///
///  - <see cref="Run"/> (Rubbish/Setup/WorldLife Polish, also called by Setup_WorldLife): no assets and no scene changes;
///    it runs <see cref="SelfTest"/>: every WorldLife Sfx id has a Feel synth recipe that renders clean, every chatter /
///    letter line is non-empty and short enough for a floater, and the tuning tables are consistent with the §4.4 budget.
///  - <see cref="Kit"/>: an autopilot that drives the local truck along the street graph's right-hand lanes (or any
///    polyline) with a throttle that honours the speed the knockables and shunts take off, lane routes, a camera shot to
///    PNG, and a frame-time meter (main thread + every WorldLife.* marker) for the profiler-gated perf pass.
/// </summary>
public static class Setup_WorldLifePolish
{
    [MenuItem("Rubbish/Setup/WorldLife Polish")]
    public static void RunMenu()
    {
        string r = Run();
        if (r.Contains("FAIL")) Debug.LogWarning(r); else Debug.Log(r);
    }

    /// <summary>The umbrella step: nothing to build, so it runs the self test and returns the report.</summary>
    public static string Run() => SelfTest();

    [MenuItem("Rubbish/WorldLife/Polish Self Test")]
    public static void SelfTestMenu() => RunMenu();

    // =================================================================================================================
    // Self test (edit mode, pure: no scene, no GameSession writes)
    // =================================================================================================================

    public const int MaxLineChars = 48;

    public static string SelfTest()
    {
        var sb = new StringBuilder("[WorldLife G self-test]\n");
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); }

        // ---- Sfx: every id WorldLife asks for has a recipe, and it renders clean and short
        var ids = new List<string>();
        foreach (var f in typeof(WorldLifeIds.Sfx).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.IsLiteral && f.FieldType == typeof(string)) ids.Add((string)f.GetRawConstantValue());
        foreach (var m in WorldLifeIds.Sfx.Mumbles) if (!ids.Contains(m)) ids.Add(m);
        var known = new HashSet<string>(FeelSfxSynth.KnownIds);
        int missing = 0, bad = 0;
        var detail = new StringBuilder();
        foreach (var id in ids)
        {
            if (!known.Contains(id) || FeelSfxSynth.Normalize(id) != id) { missing++; detail.Append(" MISSING " + id); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float[] d = FeelSfxSynth.Render(id);
            sw.Stop();
            float peak = 0f; bool finite = true;
            for (int i = 0; i < d.Length; i++) { float a = Mathf.Abs(d[i]); if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) finite = false; if (a > peak) peak = a; }
            float secs = d.Length / (float)FeelSfxSynth.SampleRate;
            bool ok = finite && peak > 0.3f && peak <= 1f && secs <= 2f;
            if (!ok) bad++;
            detail.Append($" {id} {secs:F2}s/{peak:F2}/{sw.Elapsed.TotalMilliseconds:F0}ms{(ok ? "" : " BAD")};");
        }
        Check(missing == 0 && bad == 0, $"Feel has a clean recipe for all {ids.Count} WorldLife Sfx ids (≤ 2 s, peak 0.3–1, finite):{detail}");
        int fallbacks = 0;
        foreach (var id in ids) if (KnockSfx.Pick(id, "blip") != id) fallbacks++;
        Check(fallbacks == 0, $"KnockSfx plays the real recipe, never the fallback ({fallbacks} fallbacks)");

        // ---- Lines: every array non-empty, no blank line, short enough to read as a floater
        int arrays = 0, lines = 0, blank = 0, longOnes = 0;
        var longest = "";
        void Lines(string[] arr)
        {
            arrays++;
            if (arr == null || arr.Length == 0) { blank++; return; }
            foreach (var l in arr)
            {
                lines++;
                if (string.IsNullOrWhiteSpace(l)) { blank++; continue; }
                int len = l.Replace("\n", "").Length;
                if (len > MaxLineChars) { longOnes++; longest = l; }
            }
        }
        foreach (var f in typeof(ResidentLines).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(string[])) Lines((string[])f.GetValue(null));
        for (byte d = 0; d < 4; d++) { Lines(ResidentLines.AngryFor(d)); Lines(ResidentLines.DriverFor(d)); Lines(ResidentLines.OwnerFor(d)); }
        Check(blank == 0 && longOnes == 0, $"chatter: {arrays} line sets, {lines} lines, none blank, all ≤ {MaxLineChars} chars{(longOnes > 0 ? " (too long: '" + longest + "')" : "")}");
        Check(ResidentLines.DriverFor(3)[0].Contains("LIMO") && ResidentLines.DriverFor(1)[0].Contains("MINIVAN"), "driver lines speak for their district (Bluffs limo, Meadows minivan)");

        // ---- Letters: each district voice, every take says what happened and carries the fine
        var books = new MemoryBooks { host = true, hours = 7.5 };
        int letterFails = 0;
        for (byte d = 0; d < 4; d++)
            for (int k = 0; k < 3; k++)
            {
                NeighborLetters.ResetPopupLimit();
                var bill = new NeighborConsequences.Bill { district = d, baseFine = WorldLifeTuning.Fines.FlattenFine(d), charged = WorldLifeTuning.Fines.FlattenFine(d), mult = 1f, n = 1 };
                NeighborLetters.Flattened(books, d, "Gary" + k, (14 + k) + " Maple St", PuppetProp.Mug, in bill);
                var l = books.letters[books.letters.Count - 1];
                bool says = (l.body.Contains("flatten") || l.body.Contains("upended")) && l.body.Contains("Fine:") && l.body.Contains("Gary" + k);
                if (!says) { letterFails++; sb.AppendLine("    letter: " + l.subject + " / " + l.body.Replace("\n", " ")); }
            }
        Check(letterFails == 0, $"flatten letters: 4 district voices × 3 takes all name the neighbour, say what happened and the fine ({books.letters.Count} written, {letterFails} bad)");

        // ---- Tuning: the table matches the doc where it must, and the budgets add up
        Check(WorldLifeTuning.Knock.Length == Enum.GetValues(typeof(KnockKind)).Length, "every KnockKind has a §6.1 row");
        bool speedsOk = true;
        foreach (var k in WorldLifeTuning.Knock) speedsOk &= k.knockSpeed >= 1f && k.knockSpeed <= 3f && k.speedLoss >= 0f && k.speedLoss <= 0.1f && k.massKg > 0f;
        Check(speedsOk, "knock thresholds 1–3 m/s, speed loss ≤ 10 %, masses > 0");
        var picket = WorldLifeTuning.Spec(KnockKind.PicketPanel);
        Check(Mathf.Approximately(picket.massKg, 6f) && Mathf.Approximately(picket.speedLoss, 0.02f), "picket panels are A's 2 m panels: 6 kg, 2 % (half the doc's 4 m row)");
        float sum = WorldLifeTuning.Budget.ScheduleMs + WorldLifeTuning.Budget.PuppetsMs + WorldLifeTuning.Budget.CarsMs + WorldLifeTuning.Budget.SensorMs + WorldLifeTuning.Budget.KnockMs;
        Check(sum <= WorldLifeTuning.Budget.TotalAvgMs + 1e-4f, $"slot budgets add up inside the total ({sum:F2} ≤ {WorldLifeTuning.Budget.TotalAvgMs:F2} ms)");
        Check(WorldLifeTuning.Lod.MaxFullPuppets * WorldLifeTuning.Budget.FullPuppetMs + WorldLifeTuning.Lod.MaxLitePuppets * WorldLifeTuning.Budget.LitePuppetMs <= WorldLifeTuning.Budget.PuppetsMs,
              "24 full + 24 lite puppets fit the puppet budget");
        Check(WorldLifeTuning.Fines.Flattened[0] == 3f && WorldLifeTuning.Fines.CarShunted[3] == 200f && WorldLifeTuning.Fines.CityWorksDailyCap[0] == 5f,
              "fines unchanged from §9.1 (lead decision: small fines on)");

        sb.AppendLine(fails == 0 ? "ALL PASS" : fails + " FAIL");
        return sb.ToString();
    }

    // =================================================================================================================
    // Play-test kit
    // =================================================================================================================

    public static class Kit
    {
        /// <summary>
        /// Drives a truck along a polyline (pure pursuit, yaw set directly, a 3 m/s² throttle). When a prop goes down or a
        /// car is shunted, the throttle restarts from the truck's real speed, so the speed the knock / shunt took off stays
        /// taken off (the truck's own wheel drag at a standstill is ignored).
        /// Registered in the Net slot (unused until F), so its cost shows as WorldLife.Net, not in another package's slot.
        /// </summary>
        public sealed class Autopilot : WorldLifeDirector
        {
            public static Autopilot Installed;
            public override WorldLifeSlot Slot => WorldLifeSlot.Net;
            public override string Name => "G.Autopilot";

            public TruckControllerV2 truck;
            public readonly List<Vector3> path = new List<Vector3>();
            public float speed = 6f, accel = 3f, turnRateDeg = 75f, lookAhead = 6f, cornerSpeed = 4f;
            /// <summary>Driving along the path; when it ends (or Stop) the truck brakes to a hold.</summary>
            public bool driving, hold;
            public int seg;
            public float cur, maxSeen, minSeenAfter5;
            public int ticks;
            public float distanceDriven;
            public int lossesSeen;
            Vector3 lastPos;
            int lossPending;

            public static Autopilot Install(TruckControllerV2 t)
            {
                Uninstall();
                Installed = new Autopilot { truck = t };
                WorldLifeRunner.Register(Installed);
                WorldLifeEvents.PropKnocked += Installed.OnKnock;
                WorldLifeEvents.HitDecided += Installed.OnHit;
                return Installed;
            }

            public static void Uninstall()
            {
                if (Installed != null)
                {
                    WorldLifeRunner.Unregister(Installed);
                    WorldLifeEvents.PropKnocked -= Installed.OnKnock;
                    WorldLifeEvents.HitDecided -= Installed.OnHit;
                }
                Installed = null;
            }

            void OnKnock(KnockFact f) { lossPending = 2; lossesSeen++; }
            void OnHit(NpcHit h) { if (h.kind == NpcHitKind.Shunt) { lossPending = 2; lossesSeen++; } }

            /// <summary>Start driving <paramref name="pts"/> from the nearest point at <paramref name="mps"/>.</summary>
            public void Go(IList<Vector3> pts, float mps)
            {
                path.Clear();
                for (int i = 0; i < pts.Count; i++) path.Add(new Vector3(pts[i].x, 0f, pts[i].z));
                speed = mps; seg = 0; driving = path.Count >= 2; hold = false; maxSeen = 0f; minSeenAfter5 = float.MaxValue;
                cur = truck != null && truck.Body != null ? Mathf.Max(0f, Vector3.Dot(truck.Body.linearVelocity, truck.Body.rotation * Vector3.forward)) : 0f;
                if (truck != null) lastPos = truck.Body.position;
            }

            public void Stop() { driving = false; hold = true; }

            public override void FixedTick(WorldLifeContext ctx)
            {
                ticks++;
                if (truck == null) return;
                var rb = truck.Body;
                if (rb == null || rb.isKinematic) return;
                float dt = ctx.FixedDeltaTime;
                Vector3 p = rb.position;
                Vector3 fwd = rb.rotation * Vector3.forward; fwd.y = 0f; fwd.Normalize();
                float actual = Vector3.Dot(rb.linearVelocity, fwd);
                distanceDriven += new Vector2(p.x - lastPos.x, p.z - lastPos.z).magnitude;
                lastPos = p;
                if (!driving)
                {
                    if (hold)
                    {
                        float v = Mathf.MoveTowards(Mathf.Max(0f, actual), 0f, 6f * dt);
                        var lv = rb.linearVelocity;
                        rb.linearVelocity = new Vector3(fwd.x * v, lv.y, fwd.z * v);
                        rb.angularVelocity = new Vector3(rb.angularVelocity.x, 0f, rb.angularVelocity.z);
                    }
                    return;
                }
                // Advance along the path: past the end of a segment (projected) → next segment.
                Vector3 flatP = new Vector3(p.x, 0f, p.z);
                while (seg < path.Count - 2)
                {
                    Vector3 a = path[seg], b = path[seg + 1];
                    Vector3 ab = b - a;
                    float t = Vector3.Dot(flatP - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude);
                    if (t < 1f) break;
                    seg++;
                }
                // Pure pursuit: the point lookAhead metres along the path from the projection.
                Vector3 target = PointAhead(flatP, lookAhead, out float remaining);
                if (remaining < 1.2f) { driving = false; hold = true; return; }
                Vector3 to = target - flatP;
                float want = PuppetYaw(to);
                float yaw = rb.rotation.eulerAngles.y;
                float diff = Mathf.DeltaAngle(yaw, want);
                float newYaw = Mathf.MoveTowardsAngle(yaw, want, turnRateDeg * dt * Mathf.Clamp01(0.35f + cur / 6f));
                float targetSpeed = Mathf.Abs(diff) > 25f ? Mathf.Min(speed, cornerSpeed) : speed;
                if (remaining < 10f) targetSpeed = Mathf.Min(targetSpeed, Mathf.Max(1.5f, remaining * 0.6f));
                if (lossPending > 0) { lossPending--; cur = Mathf.Min(cur, Mathf.Max(0f, actual)); }   // honour knock / shunt losses
                cur = Mathf.MoveTowards(cur, targetSpeed, (targetSpeed > cur ? accel : 6f) * dt);
                var rot = Quaternion.Euler(0f, newYaw, 0f);
                rb.MoveRotation(rot);
                Vector3 nf = rot * Vector3.forward;
                var vel = rb.linearVelocity;
                rb.linearVelocity = new Vector3(nf.x * cur, vel.y, nf.z * cur);
                rb.angularVelocity = Vector3.zero;
                if (cur > maxSeen) maxSeen = cur;
                if (ticks > 250 && actual < minSeenAfter5) minSeenAfter5 = actual;
            }

            Vector3 PointAhead(Vector3 p, float dist, out float remaining)
            {
                Vector3 a = path[seg], b = path[seg + 1];
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
                Vector3 proj = a + ab * t;
                // remaining length from the projection to the path's end
                remaining = Vector3.Distance(proj, b);
                for (int i = seg + 1; i < path.Count - 1; i++) remaining += Vector3.Distance(path[i], path[i + 1]);
                float left = dist;
                Vector3 cur0 = proj;
                for (int i = seg; i < path.Count - 1; i++)
                {
                    Vector3 end = path[i + 1];
                    float l = Vector3.Distance(cur0, end);
                    if (l >= left) return cur0 + (end - cur0) * (left / Mathf.Max(1e-4f, l));
                    left -= l;
                    cur0 = end;
                }
                return path[path.Count - 1];
            }

            static float PuppetYaw(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

            public string Describe()
            {
                var rb = truck != null ? truck.Body : null;
                return rb == null ? "no truck" : $"autopilot {(driving ? "driving" : hold ? "holding" : "idle")} seg {seg}/{path.Count - 1} cur {cur:F2} actual {rb.linearVelocity.magnitude:F2} pos {rb.position:F1} yaw {rb.rotation.eulerAngles.y:F0} max {maxSeen:F2} driven {distanceDriven:F0} m";
            }
        }

        /// <summary>
        /// A right-hand lane route between two points (car rules: Road edges, gates by the unlocked mask), as node
        /// positions from the first lane node near <paramref name="from"/> to the one near <paramref name="to"/>.
        /// </summary>
        public static List<Vector3> LaneRoute(Vector3 from, Vector3 to, int mask = -1)
        {
            var g = WorldLifeRunner.Context?.Graph;
            var pts = new List<Vector3>();
            if (g == null) return pts;
            int lane = StreetGraph.KindMask(StreetGraph.NodeKind.Lane);
            int a = g.Nearest(from, lane), b = g.Nearest(to, lane);
            var nodes = new List<int>();
            var star = new LifeAStar(g);
            if (star.Route(a, b, LifeRouteMode.Car, mask, -1, nodes, null) < 0) return pts;
            foreach (int n in nodes) pts.Add(g.nodes[n].pos);
            return pts;
        }

        /// <summary>
        /// Photographs one resident through a flop: when it enters each act / flop phase, after a short delay, it renders a
        /// side shot (<see cref="Shot"/>) and a game-view capture (with the HUD), named prefix_phase. For the doc's renders.
        /// </summary>
        public sealed class Snap : WorldLifeDirector
        {
            public static Snap Installed;
            public override WorldLifeSlot Slot => WorldLifeSlot.Net;
            public override string Name => "G.Snap";
            public LifeId watch;
            public string folder, prefix;
            public Vector3 camOffset = new Vector3(-4.5f, 2.4f, -3.5f);
            public bool gameView = true;
            public readonly List<string> taken = new List<string>();
            string lastKey = "";
            float phaseStart;

            /// <summary>Also photograph the first this-many knocks, 0.35 s after each (side shot of the flying prop).</summary>
            public int knockShots;
            readonly List<(float at, Vector3 pos, KnockKind kind, Vector3 dir)> pendingKnocks = new List<(float, Vector3, KnockKind, Vector3)>();

            public static Snap Install(LifeId who, string folder, string prefix)
            {
                Uninstall();
                Installed = new Snap { watch = who, folder = folder, prefix = prefix };
                WorldLifeRunner.Register(Installed);
                WorldLifeEvents.PropKnocked += Installed.OnKnock;
                return Installed;
            }

            public static void Uninstall()
            {
                if (Installed != null) { WorldLifeRunner.Unregister(Installed); WorldLifeEvents.PropKnocked -= Installed.OnKnock; }
                Installed = null;
            }

            void OnKnock(KnockFact f)
            {
                if (knockShots <= 0) return;
                knockShots--;
                var k = Knockable.ById(f.knockId);
                if (k == null) return;
                Vector3 d = f.impulse; d.y = 0f;
                pendingKnocks.Add((Time.time + 0.35f, k.transform.position, k.kind, d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward));
            }

            static float DelayFor(string key) =>
                key.EndsWith("Air") ? 0.3f : key.EndsWith("Lie") ? 0.7f : key.EndsWith("Angry") ? 0.6f : key.EndsWith("Chase") ? 1.2f : 0.15f;

            public override void LateTick(WorldLifeContext ctx)
            {
                for (int i = pendingKnocks.Count - 1; i >= 0; i--)
                {
                    var pk = pendingKnocks[i];
                    if (Time.time < pk.at) continue;
                    pendingKnocks.RemoveAt(i);
                    Vector3 side = new Vector3(pk.dir.z, 0f, -pk.dir.x);
                    Vector3 look = pk.pos + pk.dir * 2.5f + Vector3.up * 1.2f;
                    Shot(System.IO.Path.Combine(folder, $"{prefix}_knock_{pk.kind.ToString().ToLowerInvariant()}_{taken.Count}.png"), look + side * 7f - pk.dir * 3f + Vector3.up * 2.5f, look, 50f);
                    taken.Add("knock" + i);
                }
                var dir = ResidentDirector.Instance;
                var a = dir != null && watch.IsValid ? dir.Find(watch) : null;
                if (a == null) return;
                string key = a.act == ResidentAgent.Act.Flop ? "Flop_" + a.flop : a.act.ToString();
                float now = Time.time;
                if (key != lastKey) { lastKey = key; phaseStart = now; }
                if (key == "None" || taken.Contains(key) || now - phaseStart < DelayFor(key)) return;
                taken.Add(key);
                string baseName = System.IO.Path.Combine(folder, prefix + "_" + key.ToLowerInvariant());
                Vector3 p = a.pos;
                Shot(baseName + "_side.png", p + camOffset, p + Vector3.up * 0.5f, 45f);
                if (gameView) ScreenCapture.CaptureScreenshot(baseName + "_game.png");
            }
        }

        /// <summary>Render a temporary camera (a copy of the main camera's settings) to a PNG.</summary>
        public static string Shot(string pngPath, Vector3 pos, Vector3 look, float fov = 50f, int w = 1280, int h = 720)
        {
            var main = Camera.main;
            var go = new GameObject("WL_G_ShotCam");
            var cam = go.AddComponent<Camera>();
            if (main != null) cam.CopyFrom(main);
            cam.fieldOfView = fov; cam.enabled = false;
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos));
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(go);
            return pngPath;
        }

        // ------------------------------------------------------------------------------------------ frame meter

        static ProfilerRecorder mainThread, gcAlloc, physics, render;
        static readonly List<ProfilerRecorder> wl = new List<ProfilerRecorder>();
        static readonly List<string> wlNames = new List<string>();

        /// <summary>Start recording the main thread's frame time, physics, the frame's GC and every WorldLife.* marker
        /// (capacity <paramref name="frames"/>). Call <see cref="MeterReport"/> later; <see cref="MeterStop"/> to dispose.</summary>
        public static void MeterStart(int frames = 1200)
        {
            MeterStop();
            mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", frames);
            gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", frames);
            // "Physics.Processing" isn't a marker in this Unity (the meter read n/a); "Physics.Simulate" is the step on the main
            // thread, including the wait for PhysX's workers (fixer pass).
            physics = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Physics.Simulate", frames);
            render = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Camera.Render", frames);
            wlNames.Add(WorldLifeRunner.TotalMarkerName);
            wlNames.AddRange(WorldLifeRunner.SlotMarkerNames);
            foreach (var n in wlNames) wl.Add(ProfilerRecorder.StartNew(ProfilerCategory.Scripts, n, frames));
        }

        public static void MeterStop()
        {
            if (mainThread.Valid) mainThread.Dispose();
            if (gcAlloc.Valid) gcAlloc.Dispose();
            if (physics.Valid) physics.Dispose();
            if (render.Valid) render.Dispose();
            foreach (var r in wl) if (r.Valid) r.Dispose();
            wl.Clear(); wlNames.Clear();
        }

        static string Stat(ProfilerRecorder r, double scale, string unit)
        {
            if (!r.Valid || r.Count == 0) return "n/a";
            int n = r.Count;
            var v = new double[n];
            double sum = 0;
            for (int k = 0; k < n; k++) { v[k] = r.GetSample(k).Value * scale; sum += v[k]; }
            Array.Sort(v);
            double p99 = v[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)];
            return $"avg {sum / n:F3}{unit} p99 {p99:F3}{unit} max {v[n - 1]:F3}{unit} ({n} frames)";
        }

        public static string MeterReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("main thread  " + Stat(mainThread, 1e-6, " ms"));
            sb.AppendLine("physics      " + Stat(physics, 1e-6, " ms"));
            sb.AppendLine("camera render " + Stat(render, 1e-6, " ms"));
            sb.AppendLine("GC per frame " + Stat(gcAlloc, 1.0, " B"));
            for (int i = 0; i < wl.Count; i++) sb.AppendLine($"{wlNames[i],-18} " + Stat(wl[i], 1e-6, " ms"));
            return sb.ToString();
        }
    }
}

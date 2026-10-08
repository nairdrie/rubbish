using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Where a director runs in the frame, and which profiler marker it's billed to (Docs/WorldLife.md §4.4).</summary>
public enum WorldLifeSlot : byte
{
    /// <summary>D: rosters, legs, routes, relevance (WorldLife.Schedule, 0.15 ms).</summary>
    Schedule = 0,
    /// <summary>D: cars (WorldLife.Cars, 0.20 ms).</summary>
    Traffic = 1,
    /// <summary>C: residents, pedestrians, walk-outs, dogs, LOD (WorldLife.Puppets, 0.60 ms).</summary>
    Puppets = 2,
    /// <summary>B: knockables (WorldLife.Knock, 0.05 ms).</summary>
    Knock = 3,
    /// <summary>E: anything per FixedUpdate besides the sweep clients (WorldLife.Sensor, 0.05 ms with the sweep).</summary>
    Sensor = 4,
    /// <summary>E: consequences, horn reactions, invoices, grudges (WorldLife.Rules).</summary>
    Rules = 5,
    /// <summary>F: WorldLifeNet (WorldLife.Net).</summary>
    Net = 6,
}

/// <summary>
/// A WorldLife sub-system driven by the runner (plain C# object, no MonoBehaviour Update cost). Packages register one per
/// job with <see cref="WorldLifeRunner.Register"/> from a
/// <c>[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]</c> method (the runner's statics reset at
/// SubsystemRegistration, which runs earlier). Directors are ticked in slot order, then registration order.
/// Wake / Sleep follow the <see cref="WorldLifeAnchor"/>; create presentation objects under <see cref="WorldLifeContext.Root"/>
/// so they live and die with the anchor's scene. A director that throws is logged (3×) and the others keep running.
/// </summary>
public abstract class WorldLifeDirector
{
    public abstract WorldLifeSlot Slot { get; }
    public virtual string Name => GetType().Name;
    public bool IsAwake { get; internal set; }
    internal int errors;

    /// <summary>An anchor became active (scene loaded). Build pools, read the graph.</summary>
    public virtual void Wake(WorldLifeContext ctx) { }
    /// <summary>The anchor went away (scene unloading). Drop references to scene objects.</summary>
    public virtual void Sleep() { }
    /// <summary>Update.</summary>
    public virtual void Tick(WorldLifeContext ctx) { }
    /// <summary>FixedUpdate, after the truck sweeps were handed to the sensor clients.</summary>
    public virtual void FixedTick(WorldLifeContext ctx) { }
    /// <summary>LateUpdate (before LifeBodies.Sync).</summary>
    public virtual void LateTick(WorldLifeContext ctx) { }
}

/// <summary>What every director gets each tick (refreshed once per frame by the runner; read-only for directors).</summary>
public sealed class WorldLifeContext
{
    public WorldLifeAnchor Anchor { get; internal set; }
    public StreetGraph Graph { get; internal set; }
    public Scene Scene { get; internal set; }
    /// <summary>Parent for presentation objects; lives in the anchor's scene.</summary>
    public Transform Root { get; internal set; }
    /// <summary>The Town seed (TownSeedProvider in M1+, else the anchor's builder seed).</summary>
    public ulong TownSeed { get; internal set; }

    /// <summary>!GameSession.IsGuest this frame (decisions only when true: rule 15).</summary>
    public bool IsHost { get; internal set; }
    /// <summary>GameClock.TotalHours / DayIndex / Hours this frame.</summary>
    public double Hours { get; internal set; }
    public int Day { get; internal set; }
    public float HourOfDay { get; internal set; }
    /// <summary>Seconds since local midnight (§2.1 timetables).</summary>
    public double SecondsOfDay => (Hours - Day * 24.0) * 3600.0;
    /// <summary>Unlocked district bits (gates, car trips).</summary>
    public int UnlockedMask { get; internal set; }

    /// <summary>This machine's camera (relevance / LOD only: rule 8) and its position.</summary>
    public Camera Camera { get; internal set; }
    public Vector3 Viewer { get; internal set; }

    public float DeltaTime { get; internal set; }
    public float FixedDeltaTime { get; internal set; }
    public int Frame { get; internal set; }
    public int FixedTick { get; internal set; }
}

/// <summary>
/// WorldLife W0 CONTRACT (Docs/WorldLife.md §2.2, §4.4): the DontDestroyOnLoad "[WorldLife]" runner, booted like
/// "[Critters]". It wakes its directors while a <see cref="WorldLifeAnchor"/> is enabled (no scene-name checks) and
/// sleeps when the anchor goes away; ticks them in slot order under the WorldLife.* profiler markers; hands every truck
/// THIS machine simulates to the <see cref="ITruckSensorClient"/>s once per FixedUpdate (§6.3); re-bins
/// <see cref="LifeBodies"/> each LateUpdate; prunes <see cref="LifeOverrides"/> at the day roll. With no anchor (today's
/// Neighborhood until package A adds one) it does nothing at all.
/// </summary>
[DefaultExecutionOrder(-30)]
[DisallowMultipleComponent]
public sealed class WorldLifeRunner : MonoBehaviour
{
    public static WorldLifeRunner Instance { get; private set; }

    /// <summary>True while an anchor is active.</summary>
    public static bool IsAwake => Instance != null && Instance.ctx != null;
    /// <summary>The live context (null while asleep).</summary>
    public static WorldLifeContext Context => Instance != null ? Instance.ctx : null;
    /// <summary>Fixed steps since boot (the local hit tick until F maps it to the network tick).</summary>
    public static int FixedTickCount { get; private set; }

    /// <summary>
    /// True while the directors sleep because the anchor's scene is going away (another scene became active, play mode
    /// is ending or the app is quitting), not because an anchor was merely switched off. Presenters must not spawn
    /// anything then (G): a <c>new GameObject</c> made now lands in the NEW active scene (a curb bag at Neighborhood
    /// coordinates inside the garage) or leaks past play mode. Curb records are data, so a bag that isn't materialised
    /// now simply appears in its slot the next time the Neighborhood loads.
    /// </summary>
    public static bool Leaving { get; private set; }

    /// <summary>F: the Town seed from WorldState (a hash of TownMeta.guid). Null = the anchor's builder seed.</summary>
    public static Func<ulong> TownSeedProvider;

    /// <summary>The decision service (E sets it; a no-op authority until then).</summary>
    public static IWorldLifeAuthority Authority
    {
        get => authority ?? NullAuthority.Instance;
        set => authority = value;
    }
    static IWorldLifeAuthority authority;

    // ------------------------------------------------------------------ profiler markers (§4.4)

    public const string TotalMarkerName = "WorldLife.Total";
    /// <summary>Marker names by slot (WorldLifeSlot order).</summary>
    public static readonly string[] SlotMarkerNames =
        { "WorldLife.Schedule", "WorldLife.Cars", "WorldLife.Puppets", "WorldLife.Knock", "WorldLife.Sensor", "WorldLife.Rules", "WorldLife.Net" };
    static readonly ProfilerMarker TotalMarker = new ProfilerMarker(ProfilerCategory.Scripts, TotalMarkerName);
    static readonly ProfilerMarker[] SlotMarkers =
    {
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Schedule"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Cars"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Puppets"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Knock"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Sensor"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Rules"),
        new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Net"),
    };
    public static ProfilerMarker MarkerFor(WorldLifeSlot slot) => SlotMarkers[Mathf.Clamp((int)slot, 0, SlotMarkers.Length - 1)];

    // ------------------------------------------------------------------ registration

    static readonly List<WorldLifeDirector> directors = new List<WorldLifeDirector>(8);
    static readonly List<ITruckSensorClient> sensorClients = new List<ITruckSensorClient>(4);

    public static IReadOnlyList<WorldLifeDirector> Directors => directors;
    public static int SensorClientCount => sensorClients.Count;

    /// <summary>Add a director (idempotent). Woken at once if WorldLife is already awake.</summary>
    public static void Register(WorldLifeDirector d)
    {
        if (d == null || directors.Contains(d)) return;
        int i = directors.Count;
        while (i > 0 && directors[i - 1].Slot > d.Slot) i--;
        directors.Insert(i, d);
        if (Instance != null && Instance.ctx != null) Instance.SafeWake(d);
    }

    public static void Unregister(WorldLifeDirector d)
    {
        if (d == null || !directors.Remove(d)) return;
        if (d.IsAwake) SafeSleep(d);
    }

    /// <summary>Get every simulated truck's sweep once per FixedUpdate (idempotent).</summary>
    public static void AddSensorClient(ITruckSensorClient c)
    {
        if (c != null && !sensorClients.Contains(c)) sensorClients.Add(c);
    }

    public static void RemoveSensorClient(ITruckSensorClient c) => sensorClients.Remove(c);

    // ------------------------------------------------------------------ boot

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Application.isPlaying || Instance != null) return;
        var go = new GameObject("[WorldLife]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<WorldLifeRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        directors.Clear();
        sensorClients.Clear();
        FixedTickCount = 0;
        TownSeedProvider = null;
        authority = null;
        Leaving = false;
        quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    static bool quitting;
    static void OnQuitting() => quitting = true;

    WorldLifeContext ctx;
    GameObject rootGo;
    int lastDay = int.MinValue;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnEnable()
    {
        WorldLifeAnchor.AnchorEnabled += OnAnchorEnabled;
        WorldLifeAnchor.AnchorDisabled += OnAnchorDisabled;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        var a = WorldLifeAnchor.Latest;
        if (a != null && ctx == null) WakeOn(a);
    }

    void OnDisable()
    {
        WorldLifeAnchor.AnchorEnabled -= OnAnchorEnabled;
        WorldLifeAnchor.AnchorDisabled -= OnAnchorDisabled;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        if (ctx != null) { sceneLeft = true; SleepAll(); }
    }

    /// <summary>A scene load made another scene active: the anchor's scene is on its way out (Single load).</summary>
    void OnActiveSceneChanged(Scene from, Scene to)
    {
        if (ctx != null) sceneLeft = to != ctx.Scene;
    }

    bool sceneLeft;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void OnAnchorEnabled(WorldLifeAnchor a)
    {
        if (ctx != null && ctx.Anchor == a) return;
        if (ctx != null)
        {
            Debug.LogWarning($"[WorldLife] A second anchor enabled ('{a.name}'); switching to it.", a);
            SleepAll();
        }
        WakeOn(a);
    }

    void OnAnchorDisabled(WorldLifeAnchor a)
    {
        if (ctx == null || ctx.Anchor != a) return;
        SleepAll();
        var next = WorldLifeAnchor.Latest;
        if (next != null) WakeOn(next);
    }

    // ------------------------------------------------------------------ wake / sleep

    void WakeOn(WorldLifeAnchor a)
    {
        sceneLeft = false;
        var graph = a.Graph;
        rootGo = new GameObject("[WorldLife Presentation]");
        if (a.gameObject.scene.IsValid() && a.gameObject.scene.isLoaded) SceneManager.MoveGameObjectToScene(rootGo, a.gameObject.scene);
        ctx = new WorldLifeContext
        {
            Anchor = a, Graph = graph, Scene = a.gameObject.scene, Root = rootGo.transform,
            TownSeed = ResolveTownSeed(a, graph),
        };
        RefreshFrame();
        lastDay = ctx.Day;
        LifeOverrides.Prune(ctx.Day);
        WorldLifeProfiler.Begin();
        for (int i = 0; i < directors.Count; i++) SafeWake(directors[i]);
        Debug.Log($"[WorldLife] Awake on '{a.name}': graph '{graph.name}' ({graph.NodeCount} nodes, {graph.EdgeCount} edges, " +
                  $"{graph.houses.Length} houses), town seed {ctx.TownSeed}, {directors.Count} directors, {sensorClients.Count} sensor clients.");
    }

    void SleepAll()
    {
        // The presentation root already destroyed = the scene was torn down before us (play-mode exit, quit).
        Leaving = sceneLeft || quitting || (rootGo == null && !ReferenceEquals(rootGo, null));
        try
        {
            for (int i = directors.Count - 1; i >= 0; i--) if (directors[i].IsAwake) SafeSleep(directors[i]);
        }
        finally { Leaving = false; }
        LifeBodies.Clear();
        WorldLifeProfiler.End();
        if (rootGo != null) Destroy(rootGo);
        rootGo = null;
        ctx = null;
        sceneLeft = false;
    }

    static ulong ResolveTownSeed(WorldLifeAnchor a, StreetGraph g)
    {
        var p = TownSeedProvider;
        if (p != null) { try { return p(); } catch (Exception e) { Debug.LogException(e); } }
        int seed = a != null && a.builderSeed != 0 ? a.builderSeed : g != null && g.builderSeed != 0 ? g.builderSeed : 1234;
        return (ulong)(uint)seed;
    }

    void SafeWake(WorldLifeDirector d)
    {
        if (d.IsAwake || ctx == null) return;
        try { d.Wake(ctx); d.IsAwake = true; }
        catch (Exception e) { Report(d, "Wake", e); }
    }

    static void SafeSleep(WorldLifeDirector d)
    {
        d.IsAwake = false;
        try { d.Sleep(); }
        catch (MissingReferenceException e) when (Leaving)
        {
            // Teardown order: the scene's objects can be gone before the runner sleeps. Nothing is lost (they're gone).
            Debug.Log($"[WorldLife] {d.Name}.Sleep met an already-destroyed object while the scene was leaving: {e.Message}");
        }
        catch (Exception e) { Report(d, "Sleep", e); }
    }

    static void Report(WorldLifeDirector d, string phase, Exception e)
    {
        if (d.errors++ < 3) Debug.LogError($"[WorldLife] {d.Name}.{phase} threw: {e}");
    }

    // ------------------------------------------------------------------ frame

    void RefreshFrame()
    {
        double h = GameClock.TotalHours;
        ctx.Hours = h;
        ctx.Day = (int)Math.Floor(h / 24.0);
        ctx.HourOfDay = (float)(h - ctx.Day * 24.0);
        ctx.IsHost = !GameSession.IsGuest;
        ctx.UnlockedMask = WorldLifeIds.UnlockedDistrictMask();
        var cam = Camera.main;
        ctx.Camera = cam;
        ctx.Viewer = cam != null ? cam.transform.position : ctx.Anchor != null ? ctx.Anchor.transform.position : Vector3.zero;
        ctx.DeltaTime = Time.deltaTime;
        ctx.FixedDeltaTime = Time.fixedDeltaTime;
        ctx.Frame = Time.frameCount;
        ctx.FixedTick = FixedTickCount;
    }

    void Update()
    {
        if (ctx == null) return;
        if (ctx.Anchor == null) { SleepAll(); return; }   // destroyed without OnDisable (scene torn down)
        using (TotalMarker.Auto())
        {
            RefreshFrame();
            if (ctx.Day != lastDay) { lastDay = ctx.Day; LifeOverrides.Prune(ctx.Day); }
            for (int i = 0; i < directors.Count; i++)
            {
                var d = directors[i];
                if (!d.IsAwake) continue;
                var m = SlotMarkers[(int)d.Slot];
                m.Begin();
                try { d.Tick(ctx); } catch (Exception e) { Report(d, "Tick", e); }
                m.End();
            }
        }
    }

    void FixedUpdate()
    {
        FixedTickCount++;
        if (ctx == null) return;
        using (TotalMarker.Auto())
        {
            ctx.FixedTick = FixedTickCount;
            ctx.FixedDeltaTime = Time.fixedDeltaTime;
            var sensor = SlotMarkers[(int)WorldLifeSlot.Sensor];
            sensor.Begin();
            Sweep();
            sensor.End();
            for (int i = 0; i < directors.Count; i++)
            {
                var d = directors[i];
                if (!d.IsAwake) continue;
                var m = SlotMarkers[(int)d.Slot];
                m.Begin();
                try { d.FixedTick(ctx); } catch (Exception e) { Report(d, "FixedTick", e); }
                m.End();
            }
        }
    }

    void LateUpdate()
    {
        if (ctx == null) return;
        using (TotalMarker.Auto())
        {
            for (int i = 0; i < directors.Count; i++)
            {
                var d = directors[i];
                if (!d.IsAwake) continue;
                var m = SlotMarkers[(int)d.Slot];
                m.Begin();
                try { d.LateTick(ctx); } catch (Exception e) { Report(d, "LateTick", e); }
                m.End();
            }
            LifeBodies.Sync();
        }
    }

    // ------------------------------------------------------------------ the per-truck sweep (§6.3 step 2)

    static int sensorErrors;

    /// <summary>Build each simulated truck's predictive box and hand it to the sensor clients (before the physics step).</summary>
    void Sweep()
    {
        if (sensorClients.Count == 0) return;
        var trucks = TruckControllerV2.All;
        for (int t = 0; t < trucks.Count; t++)
        {
            var truck = trucks[t];
            if (truck == null || !truck.isActiveAndEnabled) continue;
            var rb = truck.Body;
            if (rb == null || rb.isKinematic) continue;    // another machine simulates this truck
            if (!BuildSweep(truck, rb, FixedTickCount, Time.fixedDeltaTime, out var sweep)) continue;
            for (int c = 0; c < sensorClients.Count; c++)
            {
                try { sensorClients[c].Sense(in sweep); }
                catch (Exception e) { if (sensorErrors++ < 3) Debug.LogError("[WorldLife] sensor client threw: " + e); }
            }
        }
    }

    /// <summary>The predictive box for one truck this step (false when it's slower than <see cref="WorldLifeTuning.Props.SweepMinSpeed"/>).</summary>
    public static bool BuildSweep(TruckControllerV2 truck, Rigidbody rb, int tick, float fixedDt, out TruckSweep sweep)
    {
        sweep = default;
        Vector3 v = rb.linearVelocity;
        float speed = v.magnitude;
        if (speed < WorldLifeTuning.Props.SweepMinSpeed || float.IsNaN(speed)) return false;
        Quaternion rot = rb.rotation;
        float fwd = Vector3.Dot(rot * Vector3.forward, v);
        Bounds box = truck.ModelBodyBoxMetres;
        float look = speed * WorldLifeTuning.Props.LookaheadSteps * fixedDt + WorldLifeTuning.Props.LookaheadPad;
        Vector3 min = box.min, max = box.max;
        if (fwd >= 0f) max.z += look; else min.z -= look;
        sweep.truck = truck;
        sweep.body = rb;
        sweep.rotation = rot;
        sweep.center = rb.position + rot * ((min + max) * 0.5f);
        sweep.halfExtents = (max - min) * 0.5f;
        sweep.velocity = v;
        sweep.speed = speed;
        sweep.forwardSpeed = fwd;
        sweep.lookahead = look;
        sweep.tick = tick;
        sweep.fixedDeltaTime = fixedDt;
        return true;
    }
}

/// <summary>
/// The authority until package E registers the real one: decides hits with the §8 formulas and presents them, but has
/// no memory (no immunity), no consequences (no fines, Rep, grudges or letters) and ignores knocks and hand-overs.
/// Enough for the B / C / D sandboxes.
/// </summary>
sealed class NullAuthority : IWorldLifeAuthority
{
    public static readonly NullAuthority Instance = new NullAuthority();

    public NpcHit ClaimNpcHit(in NpcHitClaim claim)
    {
        var body = LifeBodies.Find(claim.target);
        if (body == null || !body.Hittable) return default;
        Vector3 v = claim.truckVelocity; v.y = 0f;
        float s = v.magnitude;
        NpcHitKind kind = body.BodyKind switch
        {
            LifeBodyKind.Dog => s >= WorldLifeTuning.Hits.NudgeSpeed ? NpcHitKind.DogLeap : NpcHitKind.None,
            LifeBodyKind.Car => s >= WorldLifeTuning.Hits.ShuntSpeed ? NpcHitKind.Shunt : NpcHitKind.None,
            _ => WorldLifeTuning.Hits.PersonResult(s),
        };
        if (kind == NpcHitKind.None) return default;
        Vector3 p = body.Position;
        Vector3 side = Vector3.right;
        if (claim.truck != null)
        {
            Vector3 right = claim.truck.transform.right; right.y = 0f;
            side = Vector3.Dot(p - claim.truck.transform.position, right) >= 0f ? right.normalized : -right.normalized;
        }
        var hit = new NpcHit
        {
            target = claim.target, kind = kind, atHours = GameClock.TotalHours, tick = claim.tick, point = p,
            truckVelocity = claim.truckVelocity, actorSlot = claim.actorSlot, truck = claim.truck,
            seed = (byte)(LifeHash.U64(0, claim.target, claim.tick) & 0xFF),
            launch = kind == NpcHitKind.Flatten ? WorldLifeTuning.Hits.FlopLaunch(claim.truckVelocity, side)
                   : kind == NpcHitKind.Nudge ? side * WorldLifeTuning.Hits.NudgeDistance
                   : kind == NpcHitKind.Shunt ? (s > 0.01f ? v / s : side) * Mathf.Min(WorldLifeTuning.Hits.ShuntSlideMax, s * 0.15f)
                   : side,
            delayCs = (ushort)(kind == NpcHitKind.Flatten || kind == NpcHitKind.Shunt ? WorldLifeTuning.Hits.RejoinDelaySeconds * 100f : 0f),
        };
        try { body.PlayHit(in hit); } catch (Exception e) { Debug.LogException(e); }
        WorldLifeEvents.RaiseHitDecided(in hit);
        return hit;
    }

    public void ClaimKnock(in KnockClaim claim, in KnockFact fact) { }
    public void ClaimHandOver(in HandOverFact fact) { }
}

/// <summary>
/// ProfilerRecorders on the WorldLife.* markers (and the frame's GC allocations) while WorldLife is awake, so acceptance
/// evals can read average / p99 / max ms per marker over the last <see cref="Frames"/> frames (Editor and development
/// builds; markers are compiled out of release players).
/// </summary>
public static class WorldLifeProfiler
{
    public const int Frames = 600;
    static ProfilerRecorder[] recorders;
    static ProfilerRecorder gcAlloc;
    static string[] names;

    public static bool Recording => recorders != null;

    internal static void Begin()
    {
        if (recorders != null) return;
        names = new string[WorldLifeRunner.SlotMarkerNames.Length + 1];
        names[0] = WorldLifeRunner.TotalMarkerName;
        Array.Copy(WorldLifeRunner.SlotMarkerNames, 0, names, 1, WorldLifeRunner.SlotMarkerNames.Length);
        recorders = new ProfilerRecorder[names.Length];
        for (int i = 0; i < names.Length; i++) recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, names[i], Frames);
        gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", Frames);
    }

    internal static void End()
    {
        if (recorders == null) return;
        foreach (var r in recorders) r.Dispose();
        gcAlloc.Dispose();
        recorders = null;
    }

    /// <summary>Stats for one marker (ms) over the recorded frames. False when not recording / unknown marker.</summary>
    public static bool TryGet(string marker, out float avgMs, out float p99Ms, out float maxMs, out int frames)
    {
        avgMs = p99Ms = maxMs = 0f; frames = 0;
        if (recorders == null) return false;
        int i = Array.IndexOf(names, marker);
        if (i < 0) return false;
        Stats(recorders[i], 1e-6, out avgMs, out p99Ms, out maxMs, out frames);
        return true;
    }

    static void Stats(ProfilerRecorder r, double scale, out float avg, out float p99, out float max, out int n)
    {
        avg = p99 = max = 0f;
        n = r.Valid ? r.Count : 0;
        if (n == 0) return;
        var v = new double[n];
        double sum = 0;
        for (int k = 0; k < n; k++) { v[k] = r.GetSample(k).Value * scale; sum += v[k]; }
        Array.Sort(v);
        avg = (float)(sum / n);
        p99 = (float)v[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)];
        max = (float)v[n - 1];
    }

    /// <summary>A table of every marker plus the frame's GC allocations, with the §4.4 verdicts.</summary>
    public static string Report()
    {
        if (recorders == null) return "[WorldLifeProfiler] not recording (WorldLife asleep)";
        var sb = new StringBuilder();
        float[] budgets = { WorldLifeTuning.Budget.TotalAvgMs, WorldLifeTuning.Budget.ScheduleMs, WorldLifeTuning.Budget.CarsMs,
                            WorldLifeTuning.Budget.PuppetsMs, WorldLifeTuning.Budget.KnockMs, WorldLifeTuning.Budget.SensorMs, -1f, -1f };
        for (int i = 0; i < names.Length; i++)
        {
            Stats(recorders[i], 1e-6, out float avg, out float p99, out float max, out int n);
            string verdict = budgets[i] < 0f ? "" : avg <= budgets[i] ? "  ok" : "  OVER";
            if (i == 0 && p99 > WorldLifeTuning.Budget.TotalP99Ms) verdict += " (p99 OVER)";
            sb.AppendLine($"{names[i],-20} avg {avg:F4} ms  p99 {p99:F4}  max {max:F4}  ({n} frames){verdict}");
        }
        Stats(gcAlloc, 1.0, out float gAvg, out float gP99, out float gMax, out int gn);
        sb.AppendLine($"GC Allocated In Frame (whole game) avg {gAvg:F0} B  p99 {gP99:F0}  max {gMax:F0}  ({gn} frames)");
        return sb.ToString();
    }
}

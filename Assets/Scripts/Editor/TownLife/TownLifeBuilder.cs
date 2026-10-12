// Owner: core agent. Contract: design/CONTRACT.md. Options + context + orchestration. The architect implemented
// Prepare / Run / BuildInto (the preview depends on them) and left the real-pipeline pieces as documented TODOs.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Which modules and districts to build, plus free-form key=value switches.</summary>
public sealed class TownLifeOptions
{
    /// <summary>Lower-case module names to run; EMPTY = every real module.</summary>
    public readonly HashSet<string> modules = new HashSet<string>();
    /// <summary>District ids to dress; EMPTY = both starter districts.</summary>
    public readonly HashSet<string> districts = new HashSet<string>();
    /// <summary>Free-form switches (preview options, module tuning).</summary>
    public readonly Dictionary<string, string> extra = new Dictionary<string, string>();

    /// <summary>What the module alias "everything" expands to: the preview-only fence drawing plus the six real modules.</summary>
    public static readonly string[] Everything = { "fences", "paint", "park", "meadows", "streets", "yards", "edges", "commons", "estates" };
    /// <summary>Pseudo-module: build nothing (photograph the scene as it is). Never matches a real module.</summary>
    public const string None = "none";

    public bool Module(string name) { return modules.Count == 0 || modules.Contains(name); }
    public bool District(string districtId) { return districts.Count == 0 || districts.Contains(districtId); }
    public string Get(string key, string fallback = null) { return extra.TryGetValue(key, out var v) ? v : fallback; }
    public bool Flag(string key, bool fallback = false)
    {
        if (!extra.TryGetValue(key, out var v)) return fallback;
        return v == "" || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
    public float Number(string key, float fallback) { return extra.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback; }

    /// <summary>modulesCsv: "all" or "park,streets"; optionsText: "key=value;key=value" (also accepts ',' or newlines
    /// between pairs when the value has no comma). The key "districts" (value "acres", "meadows", a district id, joined
    /// with '+') fills <see cref="districts"/>.</summary>
    public static TownLifeOptions Parse(string modulesCsv, string optionsText)
    {
        var o = new TownLifeOptions();
        foreach (var raw in (modulesCsv ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string m = raw.Trim().ToLowerInvariant();
            if (m == "all") continue;                                   // (alone) = every real module
            if (m == "everything") { foreach (var name in Everything) o.modules.Add(name); continue; }   // fences + the six real modules
            o.modules.Add(m);
        }
        foreach (var pair in (optionsText ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('='); string k = (eq < 0 ? pair : pair.Substring(0, eq)).Trim(), v = eq < 0 ? "" : pair.Substring(eq + 1).Trim();
            if (k.Length == 0) continue;
            if (k == "districts") foreach (var d in v.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries))
                o.districts.Add(d == "acres" ? TownLifeScene.Acres : d == "meadows" ? TownLifeScene.Meadows : d);
            else o.extra[k] = v;
        }
        return o;
    }
}

/// <summary>Everything a module needs. One instance per build; created by TownLifeBuilder.Prepare.</summary>
public sealed class TownLifeContext
{
    // ---- heights (world y, metres). Sit things ON the surface they stand on.
    public const float GrassY = -0.08f, AsphaltY = 0.02f, SidewalkY = 0.055f, HolderY = 0.15f, DepotPadY = 0.025f, WaterY = -1.1f;
    /// <summary>Minimum vertical gap between two stacked flat layers (z-fight safe).</summary>
    public const float DecalLift = 0.03f;
    /// <summary>Fixed levels for flat geometry so modules never z-fight each other: lawn paint (mown stripes, pitch
    /// grass), soil (beds, sand, bark, safety surface), paving (paths, plazas, patios), markings (lines on paving).</summary>
    public const float LawnY = GrassY + 0.03f, SoilY = GrassY + 0.06f, PavingY = GrassY + 0.09f, MarkingY = GrassY + 0.12f;
    /// <summary>The street verge strip (Streets) sits between the base grass and every lawn layer, so wherever it meets a yard / park lawn the
    /// lawn wins cleanly instead of z-fighting at LawnY. Keep it strictly between GrassY and LawnY.</summary>
    public const float VergeY = GrassY + 0.015f;

    public Transform town;                 // NeighborhoodGen (read only for modules)
    public Transform root;                 // where TownLife objects go: NeighborhoodGen/TownLife, or the preview's temporary root
    public bool transient;                 // preview: nothing may touch the AssetDatabase or the scene
    public bool continueOnError;           // preview: a throwing module is reported and skipped; real pipeline: rethrown
    public TownLifeOptions options;
    public TownLifeScene scene;
    public TownLifePlan.Result plan;
    public TownLifeFences.Result fences;   // planned boundary network (never null; may be empty)
    public TownLifeOccupancy occupancy;
    public readonly StringBuilder report = new StringBuilder();
    public readonly List<string> errors = new List<string>();
    /// <summary>Recolouring of EXISTING scene renderers (TownLifePaint): renderer -> full sharedMaterials array. Modules
    /// only fill it; the real pipeline applies it to the scene, the preview applies it to its photo clones.</summary>
    public readonly Dictionary<Renderer, Material[]> materialOverrides = new Dictionary<Renderer, Material[]>();

    readonly Dictionary<string, Transform> groups = new Dictionary<string, Transform>();

    /// <summary>Find-or-create the top-level group "TL_&lt;name&gt;" under root. One per module ("Park", "Yards", ...);
    /// make sub-groups with TownLifeKit.Group(parent, "TL_...").</summary>
    public Transform Group(string name)
    {
        if (groups.TryGetValue(name, out var t) && t) return t;
        string full = TownLifeKit.Prefix + name;
        // REAL pipeline: the park's objects live under NeighborhoodGen/Park (the town-map bake draws the park as the
        // bounds of the renderers under Park, and the incremental publish requires that group). Preview: under root.
        Transform parent = root;
        if (!transient && name == TownLifeBuilder.ParkGroupKey && scene != null && scene.park != null && scene.park.group != null) parent = scene.park.group;
        t = parent.Find(full);
        if (t == null)
        {
            t = TownLifeKit.Group(parent, full);
            if (parent != root) CityEditTransaction.RegisterCreated(t.gameObject);     // new subtree under a pre-existing parent
        }
        groups[name] = t; return t;
    }

    /// <summary>One line of the per-module summary (filled by TownLifeBuilder.Run).</summary>
    public sealed class ModuleStat { public string name; public int spawned, gameObjects; public long ms; public bool previewOnly, failed; }
    /// <summary>Per-module object counts and timings in run order.</summary>
    public readonly List<ModuleStat> stats = new List<ModuleStat>();
    /// <summary>GameObjects added by the REAL modules (what the budget counts; preview-only modules are excluded).</summary>
    public int budgetObjects;

    /// <summary>The ONLY source of randomness: a fresh System.Random seeded from a stable hash of the key. Key it by
    /// module + feature + identity ("yards/theme/" + stopId, "streets/" + roadId + "/" + side) so that moving one
    /// house never reshuffles another. Never share an instance between features; never use UnityEngine.Random.</summary>
    public System.Random Rng(string key) { return new System.Random(StableHash(key)); }

    /// <summary>32-bit FNV-1a (identical to PickupVisuals.StableHash). string.GetHashCode is randomised per process.</summary>
    public static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            if (s != null) for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (int)h;
        }
    }
    /// <summary>Uniform float in [min, max) from a System.Random.</summary>
    public static float Range(System.Random rng, float min, float max) { return min + (float)rng.NextDouble() * (max - min); }
    /// <summary>Deterministic pick from a list.</summary>
    public static T Pick<T>(System.Random rng, IList<T> items) { return items[rng.Next(items.Count)]; }

    /// <summary>Appends "[module] message" to the report.</summary>
    public void Log(string module, string message) { report.Append('[').Append(module).Append("] ").Append(message).Append('\n'); }
    /// <summary>Records a problem (also appended to the report). Does not throw.</summary>
    public void Error(string module, string message) { errors.Add("[" + module + "] " + message); Log(module, "ERROR " + message); }
}

/// <summary>Clear / build orchestration for the TownLife layer.</summary>
public static class TownLifeBuilder
{
    /// <summary>Name of the generated group under NeighborhoodGen.</summary>
    public const string GroupName = "TownLife";
    /// <summary>ctx.Group key of the park module; in the real pipeline its group "TL_Park" lives under NeighborhoodGen/Park.</summary>
    public const string ParkGroupKey = "Park";
    /// <summary>Name of the TownLife group inside NeighborhoodGen/Park.</summary>
    public const string ParkGroupName = "TL_Park";
    /// <summary>GameObject budget for both districts (DESIGN section 4); exceeding it is reported as an error.</summary>
    public const int ObjectBudget = 9000;
    public const string MenuPath = "Rubbish/City/Rebuild Town Life";
    public const string EnabledMenuPath = "Rubbish/City/Town Life Enabled";
    const string DisabledPref = "Rubbish.TownLife.Disabled";

    /// <summary>Emergency switch (per machine, EditorPrefs; default ON). TownLife is rebuilt inside EVERY city publish and
    /// any error in it aborts that publish; switching it off makes PipelineBuild a no-op (the layer is still cleared) so
    /// a broken module can never keep the owner from baking and playing. Toggle: Rubbish/City/Town Life Enabled.</summary>
    public static bool Enabled
    {
        get { return !EditorPrefs.GetBool(DisabledPref, false); }
        set { if (value) EditorPrefs.DeleteKey(DisabledPref); else EditorPrefs.SetBool(DisabledPref, true); }
    }

    /// <summary>A named build step.</summary>
    public sealed class Module { public string name; public Action<TownLifeContext> build; public bool previewOnly; }

    /// <summary>Fixed order. "fences" is preview-only (the real fences are built by CoastalYardAuthoring).</summary>
    public static readonly List<Module> Modules = new List<Module>
    {
        new Module { name = "fences", build = TownLifeFences.BuildPreview, previewOnly = true },
        new Module { name = TownLifePaint.Name, build = TownLifePaint.Build },
        new Module { name = TownLifePark.Name, build = TownLifePark.Build },
        new Module { name = TownLifeMeadows.Name, build = TownLifeMeadows.Build },
        new Module { name = TownLifeStreets.Name, build = TownLifeStreets.Build },
        new Module { name = TownLifeYards.Name, build = TownLifeYards.Build },
        new Module { name = TownLifeEdges.Name, build = TownLifeEdges.Build },
    new Module { name = TownLifeCommons.Name, build = TownLifeCommons.Build },
    new Module { name = TownLifeEstates.Name, build = TownLifeEstates.Build },
    };

    /// <summary>Report of the last PipelineBuild in this domain (null until one ran). RebuildStandalone uses it to prove
    /// that the hooks in NeighborhoodBuilder.Coastal.cs really ran.</summary>
    public static string LastPipelineReport { get; private set; }

    /// <summary>Adds (or replaces, by name) a preview-only module that runs after the fixed ones. Used by the preview's
    /// smoke test and by throw-away experiments; never by shipped code.</summary>
    public static void AddPreviewModule(string name, Action<TownLifeContext> build)
    {
        Modules.RemoveAll(m => m.name == name);
        Modules.Add(new Module { name = name, build = build, previewOnly = true });
    }

    /// <summary>Starts a build: kit session (TownLifeKit.Begin), scene snapshot, parcel plan, fence plan, occupancy
    /// seeded with the keep-outs and the planned fences. <paramref name="root"/> is where objects will go.</summary>
    public static TownLifeContext Prepare(Transform town, Transform root, TownLifeOptions options, bool transient)
    {
        if (town == null) throw new ArgumentNullException(nameof(town));
        if (root == null) throw new ArgumentNullException(nameof(root));
        var ctx = new TownLifeContext { town = town, root = root, options = options ?? new TownLifeOptions(), transient = transient, continueOnError = transient };
        TownLifeKit.Begin(transient);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ctx.scene = TownLifeScene.Capture(town);
        long captureMs = clock.ElapsedMilliseconds; clock.Restart();
        foreach (var note in ctx.scene.notes) ctx.Log("scene", note);
        ctx.plan = TownLifePlan.Build(ctx.scene.ToPlanInput());
        foreach (var problem in TownLifePlan.SelfCheck(ctx.plan)) ctx.Error("plan", problem);
        long planMs = clock.ElapsedMilliseconds; clock.Restart();
        TownLifeFences.DefineStyles();
        ctx.fences = TownLifeFences.Plan(ctx.scene, ctx.plan) ?? new TownLifeFences.Result();
        long fenceMs = clock.ElapsedMilliseconds; clock.Restart();
        ctx.occupancy = new TownLifeOccupancy(ctx.scene.area);
        ctx.occupancy.SeedKeepOuts(ctx.scene, ctx.plan);
        TownLifeFences.Reserve(ctx.fences, ctx.occupancy);
        ctx.Log("prepare", ctx.scene.houses.Length + " houses, " + ctx.scene.roads.Length + " roads, " + ctx.plan.parcels.Length + " parcels, " +
            ctx.fences.runs.Length + " fence runs, " + ctx.occupancy.Count + " keep-out shapes, " + ctx.scene.infill.Length + " infill, " + ctx.scene.kidPens.Length + " kid pens, " +
            ctx.scene.courts.Length + " courts, " + ctx.scene.lamps.Length + " lamps, " + ctx.scene.natureTrees.Length + " nature trees, " + ctx.scene.crossings.Length + " crossings, " + ctx.scene.gates.Length + " gates, " +
            ctx.scene.foreignObstacles.Length + " foreign obstacles, bank tops river " + ctx.scene.riverBankTop.Length + " / coast " + ctx.scene.coastBankTop.Length +
            " (capture " + captureMs + " ms, plan " + planMs + " ms, fences " + fenceMs + " ms, occupancy " + clock.ElapsedMilliseconds + " ms)");
        var tagLine = new StringBuilder("keep-outs:");
        foreach (var pair in ctx.occupancy.TagCounts()) tagLine.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
        ctx.Log("prepare", tagLine.ToString());
        if (ctx.scene.park != null) ctx.Log("prepare", "park block " + ctx.scene.park.block + " kerb " + ctx.scene.park.kerb + " landmark " + ctx.scene.park.landmark + " kept " + ctx.scene.park.kept.Length);
        foreach (var court in ctx.scene.courts) ctx.Log("prepare", "court " + court.roadId + " centre " + court.centre + " r " + court.radius + " island " + court.islandRadius + " entrance " + court.entrance);
        return ctx;
    }

    /// <summary>GameObjects that currently belong to this build: everything under ctx.root (root excluded) plus, in the
    /// real pipeline, the TL_Park group that lives under NeighborhoodGen/Park.</summary>
    static int CountObjects(TownLifeContext ctx)
    {
        int n = ctx.root != null ? ctx.root.GetComponentsInChildren<Transform>(true).Length - 1 : 0;
        if (!ctx.transient && ctx.scene != null && ctx.scene.park != null && ctx.scene.park.group != null)
        {
            var parkLife = ctx.scene.park.group.Find(ParkGroupName);
            if (parkLife != null && !parkLife.IsChildOf(ctx.root)) n += parkLife.GetComponentsInChildren<Transform>(true).Length;
        }
        return n;
    }

    /// <summary>Runs the selected modules in the fixed order, ends the kit session and returns the report. Preview-only
    /// modules run only when ctx.transient AND explicitly named in ctx.options.modules. Appends a per-module summary
    /// (objects, GameObjects, ms) to the report and fills ctx.stats.</summary>
    public static string Run(TownLifeContext ctx)
    {
        try
        {
            foreach (var module in Modules.ToArray())
            {
                if (module.previewOnly ? !(ctx.transient && ctx.options.modules.Contains(module.name)) : !ctx.options.Module(module.name)) continue;
                int before = TownLifeKit.SpawnCount, objectsBefore = CountObjects(ctx); var clock = System.Diagnostics.Stopwatch.StartNew();
                var stat = new TownLifeContext.ModuleStat { name = module.name, previewOnly = module.previewOnly };
                try { module.build(ctx); }
                catch (Exception e)
                {
                    stat.failed = true;
                    ctx.Error(module.name, "threw " + e);
                    if (!ctx.continueOnError) throw;
                }
                finally
                {
                    stat.spawned = TownLifeKit.SpawnCount - before; stat.gameObjects = CountObjects(ctx) - objectsBefore; stat.ms = clock.ElapsedMilliseconds;
                    ctx.stats.Add(stat); if (!module.previewOnly) ctx.budgetObjects += Mathf.Max(stat.spawned, stat.gameObjects);
                }
                ctx.Log(module.name, "+" + stat.spawned + " objects (" + stat.gameObjects + " GameObjects) in " + stat.ms + " ms");
            }
            if (ctx.budgetObjects > ObjectBudget) ctx.Error("budget", ctx.budgetObjects + " GameObjects exceeds the budget of " + ObjectBudget);
            ctx.Log("summary", "module        objects  gameObjects      ms");
            foreach (var stat in ctx.stats)
                ctx.Log("summary", stat.name.PadRight(12) + stat.spawned.ToString().PadLeft(9) + stat.gameObjects.ToString().PadLeft(13) + stat.ms.ToString().PadLeft(8) + (stat.previewOnly ? "  (preview only, not budgeted)" : "") + (stat.failed ? "  FAILED" : ""));
            ctx.Log("total", TownLifeKit.SpawnCount + " kit objects, " + CountObjects(ctx) + " GameObjects, " + ctx.budgetObjects + "/" + ObjectBudget + " budgeted, " + ctx.materialOverrides.Count + " repainted renderers, " + ctx.errors.Count + " errors");
        }
        finally { TownLifeKit.End(); }
        return ctx.report.ToString();
    }

    /// <summary>Prepare + Run. The one call both the preview and the real pipeline use.</summary>
    public static string BuildInto(Transform town, Transform root, TownLifeOptions options, bool transient, out TownLifeContext ctx)
    {
        ctx = Prepare(town, root, options, transient);
        return Run(ctx);
    }

    // ------------------------------------------------------------------------------------------- legacy park

    /// <summary>THE shared park-legacy rule (lead's decision): when the park module runs, every legacy child of
    /// NeighborhoodGen/Park is RETIRED except (1) the playground KidPen object, (2) everything standing inside the pen
    /// rect (swing, slide, sand ...), (3) SM_lemonade_shop* (FitCoastalParkEntrances re-poses it on every publish) and
    /// (4) TownLife's own TL_* objects. <paramref name="child"/> is a DIRECT child of the Park group. Used by the
    /// preview's hide filter and by the real pipeline (RetireParkLegacy).</summary>
    public static bool IsRetiredParkLegacy(Transform child, TownLifeScene scene)
    {
        if (child == null) return false;
        if (child.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal)) return false;
        if (child.name.StartsWith("SM_lemonade_shop", StringComparison.Ordinal)) return false;
        if (child.GetComponentInChildren<KidPen>(true) != null) return false;
        var pen = scene != null && scene.park != null ? scene.park.playground : null;
        if (pen != null)
        {
            // "standing inside the pen": the centre of what it shows (or its pivot when it shows nothing)
            Vector3 centre = child.position; bool any = false; Bounds b = default;
            foreach (var r in child.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            if (any) centre = b.center;
            if (TownLifeGeo.QuadContains(pen.quad, centre, 0.3f)) return false;
        }
        return true;
    }

    /// <summary>The direct child of the Park group that holds <paramref name="t"/>, or null when t is not under it.</summary>
    public static Transform ParkChildOf(Transform t, Transform parkGroup)
    {
        if (parkGroup == null) return null;
        for (var p = t; p != null; p = p.parent) if (p.parent == parkGroup) return p;
        return null;
    }

    /// <summary>REAL pipeline: destroys every retired legacy child of NeighborhoodGen/Park through
    /// CityEditTransaction.DestroyGenerated (so a failed publish restores them). Returns how many went.</summary>
    public static int RetireParkLegacy(Transform town, TownLifeScene scene)
    {
        var park = town != null ? town.Find("Park") : null; if (park == null) return 0;
        var doomed = new List<GameObject>();
        foreach (Transform child in park) if (IsRetiredParkLegacy(child, scene)) doomed.Add(child.gameObject);
        foreach (var go in doomed) CityEditTransaction.DestroyGenerated(go);
        return doomed.Count;
    }

    // -------------------------------------------------------------------------------------------- clear / build

    /// <summary>Destroys NeighborhoodGen/TownLife and NeighborhoodGen/Park/TL_* (through
    /// CityEditTransaction.DestroyGenerated, so a transaction can roll it back) and returns how many GameObjects went.
    /// Does NOT undo TownLifePaint (paint is idempotent and is simply re-applied) and does not bring retired legacy
    /// park objects back. Callers must Physics.SyncTransforms() before anything that queries physics.</summary>
    public static int ClearGenerated(Transform town)
    {
        if (town == null) return 0;
        int n = 0; var doomed = new List<GameObject>();
        foreach (Transform child in town) if (child.name == GroupName) doomed.Add(child.gameObject);
        var park = town.Find("Park");
        if (park != null) foreach (Transform child in park) if (child.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal)) doomed.Add(child.gameObject);
        foreach (var go in doomed) { n += go.GetComponentsInChildren<Transform>(true).Length; CityEditTransaction.DestroyGenerated(go); }
        return n;
    }

    static void InstallAssetHook()
    {
        // Every kit asset write (also the fence visuals CoastalYardAuthoring builds between the two hooks) is announced
        // to the active transaction BEFORE it happens. Null-safe outside a transaction (full build).
        TownLifeKit.BeforeAssetWrite = path => CityEditTransaction.Current?.WatchAsset(path);
    }

    /// <summary>REAL-PIPELINE HOOK 1: call inside the transaction BEFORE any yard planning / infill rebuild
    /// (CoastalYardAuthoring.ClearLine and CoastalInfillSite.Conflict see every collider). Clears the layer, installs the
    /// asset-write hook and syncs physics.</summary>
    public static void PipelineClear(Transform town)
    {
        if (town == null) throw new ArgumentNullException(nameof(town));
        InstallAssetHook();
        ClearGenerated(town);
        Physics.SyncTransforms();
    }

    /// <summary>REAL-PIPELINE HOOK 2: call AFTER yards, infill, depot pads and spawns, before the final
    /// Physics.SyncTransforms / graph build / validation. Retires the legacy park, creates NeighborhoodGen/TownLife
    /// (registered with the transaction) and Park/TL_Park, builds every module with asset writes watched, applies
    /// ctx.materialOverrides to the scene (Undo-safe), enforces static flags, removes stale TownLife assets and
    /// returns the report. Throws on any module or budget error so the caller's transaction rolls everything back.</summary>
    public static string PipelineBuild(Transform town)
    {
        if (town == null) throw new ArgumentNullException(nameof(town));
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("TownLife is built in Edit Mode only.");
        LastPipelineReport = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        InstallAssetHook();
        int cleared = ClearGenerated(town);
        if (!Enabled)
        {
            Physics.SyncTransforms();
            LastPipelineReport = "[TownLife] DISABLED on this machine (" + EnabledMenuPath + "): layer cleared (" + cleared + " objects), nothing built.";
            Debug.LogWarning(LastPipelineReport);
            return LastPipelineReport;
        }
        // The retire rule needs the playground pen; a light pre-capture of just the park would duplicate the rule, so
        // capture once, retire, and let Prepare capture again (the second capture then sees the park as it will be).
        int retired = RetireParkLegacy(town, TownLifeScene.Capture(town));
        var group = new GameObject(GroupName); group.transform.SetParent(town, false);
        CityEditTransaction.RegisterCreated(group);
        TownLifeContext ctx = null;
        try
        {
            string report = BuildInto(town, group.transform, new TownLifeOptions(), false, out ctx);
            if (ctx.errors.Count > 0) throw new InvalidOperationException("TownLife failed:\n" + string.Join("\n", ctx.errors) + "\n\n" + report);
            int repainted = ApplyMaterialOverrides(ctx);
            int flagged = EnforceStaticFlags(ctx);
            // stale mesh sub-assets inside the containers this build used (never one the scene still shows: the fence
            // meshes were written by CoastalYardAuthoring's own kit session), then whole files nothing references
            int pruned = TownLifeKit.PruneUnusedMeshes(ReferencedMeshes(town));
            int lateWatched = WatchDirtyAssets();
            int stale = DeleteStaleAssets(town);
            Physics.SyncTransforms();
            string head = "[TownLife] " + CountObjects(ctx) + " GameObjects (" + TownLifeKit.SpawnCount + " kit objects, budget " + ctx.budgetObjects + "/" + ObjectBudget + "), " +
                repainted + " renderers repainted, " + TownLifeKit.WrittenAssets.Count + " assets written, " + TownLifeKit.UsedAssets.Count + " in use, " + pruned + " stale meshes pruned, " + stale + " stale asset files removed, " +
                lateWatched + " late-watched, " + flagged + " static flags fixed, " + cleared + " old objects cleared, " + retired + " legacy park objects retired, " + clock.ElapsedMilliseconds + " ms.";
            LastPipelineReport = head + "\n" + report;
            return LastPipelineReport;
        }
        finally { if (TownLifeKit.Active) TownLifeKit.End(); }
    }

    /// <summary>Applies ctx.materialOverrides to the scene renderers (real pipeline only; the preview applies them to
    /// its clones instead). Undo-recorded (the publish transaction can revert it) and recorded as prefab-instance
    /// overrides so the scene file keeps them. Skips destroyed renderers and unchanged arrays. Returns how many changed.</summary>
    public static int ApplyMaterialOverrides(TownLifeContext ctx)
    {
        if (ctx.transient) throw new InvalidOperationException("Material overrides are never applied to the scene in transient mode.");
        int changed = 0;
        foreach (var pair in ctx.materialOverrides)
        {
            var renderer = pair.Key; var wanted = pair.Value;
            if (renderer == null || wanted == null) continue;
            var current = renderer.sharedMaterials; bool same = current.Length == wanted.Length;
            for (int i = 0; same && i < current.Length; i++) same = current[i] == wanted[i];
            if (same) continue;
            foreach (var m in wanted) if (m == null) throw new InvalidOperationException("TownLifePaint produced a null material for " + renderer.name + ".");
            Undo.RecordObject(renderer, CityEditTransaction.UndoName);
            renderer.sharedMaterials = wanted;
            if (PrefabUtility.IsPartOfPrefabInstance(renderer)) PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorUtility.SetDirty(renderer);
            changed++;
        }
        // Undo.RecordObject diffs (and dirties the scene) lazily at the end of the Editor tick, i.e. AFTER the publish
        // has saved the scene and signed it: flush now so the save sees a settled scene (found at integration: the
        // first, repainting publish left Neighborhood dirty and the fresh signature stale).
        if (changed > 0) Undo.FlushUndoRecordObjects();
        return changed;
    }

    /// <summary>Safety net behind TownLifeKit.Finish: every TownLife GameObject that draws something (text excepted) is
    /// BatchingStatic | OccludeeStatic. Returns how many had to be fixed (0 when the kit and the modules did their job).</summary>
    static int EnforceStaticFlags(TownLifeContext ctx)
    {
        const StaticEditorFlags needed = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic;
        int fixedCount = 0; var roots = new List<Transform> { ctx.root };
        if (ctx.scene != null && ctx.scene.park != null && ctx.scene.park.group != null) { var parkLife = ctx.scene.park.group.Find(ParkGroupName); if (parkLife != null) roots.Add(parkLife); }
        foreach (var root in roots)
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponent<TMPro.TMP_Text>() != null || t.GetComponent<Renderer>() == null) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                if ((flags & needed) == needed) continue;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags | needed); fixedCount++;
            }
        return fixedCount;
    }

    /// <summary>Every mesh a MeshFilter or MeshCollider of the town (TownAuthored included) still points at.</summary>
    static HashSet<Mesh> ReferencedMeshes(Transform town)
    {
        var set = new HashSet<Mesh>();
        var roots = new List<Transform> { town }; var authored = TownAuthoringScope.AuthoredRoot(town); if (authored != null) roots.Add(authored);
        foreach (var root in roots)
        {
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true)) if (f.sharedMesh != null) set.Add(f.sharedMesh);
            foreach (var c in root.GetComponentsInChildren<MeshCollider>(true)) if (c.sharedMesh != null) set.Add(c.sharedMesh);
        }
        return set;
    }

    static List<string> TownLifeAssetPaths()
    {
        var list = new List<string>();
        if (!AssetDatabase.IsValidFolder(TownLifeKit.AssetDir)) return list;
        foreach (string guid in AssetDatabase.FindAssets("", new[] { TownLifeKit.AssetDir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path) && !list.Contains(path)) list.Add(path);
        }
        list.Sort(string.CompareOrdinal);
        return list;
    }

    /// <summary>Belt and braces for "a dirty unwatched dependency aborts the publish": any TownLife asset that is dirty
    /// but was not announced through the kit's hook is handed to the transaction now, so SaveWatchedAssets writes it.</summary>
    static int WatchDirtyAssets()
    {
        var transaction = CityEditTransaction.Current; if (transaction == null) return 0;
        int n = 0; var written = new HashSet<string>(TownLifeKit.WrittenAssets);
        foreach (string path in TownLifeAssetPaths())
        {
            bool dirty = false;                                         // meshes are sub-assets of container files: any dirty object counts
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) if (asset != null && EditorUtility.IsDirty(asset)) { dirty = true; break; }
            if (!dirty) continue;
            if (!written.Contains(path)) n++;
            var main = AssetDatabase.LoadMainAssetAtPath(path); if (main != null) EditorUtility.SetDirty(main);   // SaveWatchedAssets saves through the main asset
            transaction.WatchAsset(path);                                // no-op when the kit already announced it
        }
        return n;
    }

    /// <summary>Deletes TownLife assets nothing uses any more: not referenced by this build (TownLifeKit.UsedAssets) AND
    /// not referenced by any mesh filter, mesh collider or renderer in the town (which also protects the fence meshes
    /// CoastalYardAuthoring built in an earlier kit session). Each file is watched first, so a rollback restores it.</summary>
    static int DeleteStaleAssets(Transform town)
    {
        var keep = new HashSet<string>(TownLifeKit.UsedAssets);
        string prefix = TownLifeKit.AssetDir + "/";
        void Keep(UnityEngine.Object o)
        {
            if (o == null) return; string path = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.Ordinal)) keep.Add(path);
        }
        var roots = new List<Transform> { town }; var authored = TownAuthoringScope.AuthoredRoot(town); if (authored != null) roots.Add(authored);
        foreach (var root in roots)
        {
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true)) Keep(f.sharedMesh);
            foreach (var c in root.GetComponentsInChildren<MeshCollider>(true)) Keep(c.sharedMesh);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) Keep(m);
        }
        int deleted = 0;
        foreach (string path in TownLifeAssetPaths())
        {
            if (keep.Contains(path)) continue;
            CityEditTransaction.Current?.WatchAsset(path);
            if (AssetDatabase.DeleteAsset(path)) deleted++;
        }
        return deleted;
    }

    /// <summary>Footprints the full build's random scenery (BuildCoastalScenery) must stay out of: the whole Pickle Park
    /// block (TownLife designs it) and whatever TownLife has already built. Blueprint + scene reads only.</summary>
    public static List<Bounds> SceneryExclusions(Transform town)
    {
        var list = new List<Bounds>();
        try
        {
            var block = TownLifeScene.ParkBlockFromBlueprint(out _, out _);
            if (block.width > 1f && block.height > 1f) list.Add(new Bounds(new Vector3(block.center.x, 5f, block.center.y), new Vector3(block.width, 30f, block.height)));
        }
        catch (ArgumentException) { /* a blueprint without the pickle-park landmark has no park block */ }
        if (town != null)
        {
            var life = town.Find(GroupName);
            if (life != null) foreach (var r in life.GetComponentsInChildren<Renderer>(true)) list.Add(r.bounds);
            var park = town.Find("Park");
            if (park != null) foreach (Transform child in park) if (child.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal)) foreach (var r in child.GetComponentsInChildren<Renderer>(true)) list.Add(r.bounds);
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------- standalone

    /// <summary>Why the standalone rebuild may not start right now, or null when everything is in order. Read-only.</summary>
    public static string StandaloneBlocker()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play Mode first.";
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "The Editor is compiling or importing; try again when it is idle.";
        if (CityEditTransaction.Current != null) return "Another city edit transaction is active.";
        var town = SceneManager.GetSceneByPath(TownLifeScene.TownScenePath);
        var depot = SceneManager.GetSceneByPath(DepotRowLayout.ScenePath);
        if (!town.IsValid() || !town.isLoaded) return "Open " + TownLifeScene.TownScenePath + " (additively) first.";
        if (!depot.IsValid() || !depot.isLoaded) return "Open " + DepotRowLayout.ScenePath + " (additively) first.";
        if (town.isDirty) return "Neighborhood.unity has unsaved changes: save & bake them (Town Builder) or reload the scene first.";
        if (depot.isDirty) return "DepotRow.unity has unsaved changes: save or reload it first.";
        var root = TownLifeScene.FindTown();
        if (root == null || !CoastalCityLayout.IsCoastal(root)) return "The scene has no coastal NeighborhoodGen root.";
        if (root.Find("Park") == null) return "The town has no Park group.";
        var stamp = root.GetComponent<CityBlueprintStamp>();
        if (stamp == null || !stamp.Matches(CoastalCityLayout.Data)) return "The blueprint's stamped sections changed (terrain, scale, landmarks, lot strips, infill): an incremental publish would refuse.";
        if (!TownWorldSignatureBuilder.IsCurrent(out string stale)) return "The town is not baked (" + stale + "). Publish the pending edits first so this run does not sign someone else's unbaked work.";
        return null;
    }

    /// <summary>STANDALONE COMMAND ("Rubbish/City/Rebuild Town Life"). A thin, guarded wrapper around the existing
    /// incremental publish NeighborhoodBuilder.ApplyCoastalChanges(): with the PipelineClear / PipelineBuild hooks in
    /// place that one transaction re-plans the yards (connected fence network), rebuilds TownLife, validates, saves both
    /// scenes, re-bakes street graph / house directory / town map and publishes the signature, or rolls ALL of it back.
    /// Refuses (throws InvalidOperationException, nothing touched) when StandaloneBlocker() names a problem. Returns the
    /// combined report. After a failed (rolled back) run Unity may leave Neighborhood flagged dirty although its file
    /// was restored byte-for-byte: reload the scene without saving.</summary>
    public static string RebuildStandalone()
    {
        string blocker = StandaloneBlocker();
        if (blocker == null && !Enabled) blocker = "Town Life is switched off on this machine (" + EnabledMenuPath + ").";
        if (blocker != null) throw new InvalidOperationException("Rebuild Town Life cannot start: " + blocker);
        LastPipelineReport = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string publish;
        try { publish = NeighborhoodBuilder.ApplyCoastalChanges(); }
        catch (Exception e)
        {
            var townScene = SceneManager.GetSceneByPath(TownLifeScene.TownScenePath);
            throw new InvalidOperationException("Rebuild Town Life failed and was rolled back (scene files and baked assets restored)." +
                (townScene.IsValid() && townScene.isLoaded && townScene.isDirty ? " Neighborhood is flagged dirty by the Undo revert: reload it WITHOUT saving." : "") +
                "\n" + e.Message, e);
        }
        if (LastPipelineReport == null)
            throw new InvalidOperationException("The publish completed but the TownLife hooks did not run: NeighborhoodBuilder.Coastal.cs has no PipelineClear / PipelineBuild calls (integration incomplete). Nothing of TownLife was built.\n" + publish);
        var root = TownLifeScene.FindTown();
        var sb = new StringBuilder();
        sb.Append("Rebuild Town Life: published in ").Append(clock.ElapsedMilliseconds).Append(" ms.\n");
        if (root != null)
        {
            var life = root.Find(GroupName); var park = root.Find("Park"); var parkLife = park != null ? park.Find(ParkGroupName) : null;
            sb.Append("NeighborhoodGen/TownLife: ").Append(life != null ? life.GetComponentsInChildren<Transform>(true).Length - 1 : 0).Append(" GameObjects");
            if (life != null) foreach (Transform g in life) sb.Append("; ").Append(g.name).Append(' ').Append(g.GetComponentsInChildren<Transform>(true).Length);
            sb.Append("\nNeighborhoodGen/Park/TL_Park: ").Append(parkLife != null ? parkLife.GetComponentsInChildren<Transform>(true).Length : 0).Append(" GameObjects; NeighborhoodGen total ")
              .Append(root.GetComponentsInChildren<Transform>(true).Length).Append('\n');
        }
        bool current = TownWorldSignatureBuilder.IsCurrent(out string reason);
        sb.Append("Signature current: ").Append(current ? "yes" : "NO (" + reason + ")").Append('\n');
        sb.Append(publish).Append('\n').Append(LastPipelineReport);
        return sb.ToString();
    }

    [MenuItem(MenuPath)]
    static void RebuildMenu()
    {
        try
        {
            string report = RebuildStandalone();
            Debug.Log(report);
            EditorUtility.DisplayDialog("Rebuild Town Life", report.Length > 900 ? report.Substring(0, 900) + "\n...\n(full report in the Console)" : report, "OK");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Rebuild Town Life failed", e.Message.Length > 1400 ? e.Message.Substring(0, 1400) + "\n...\n(full error in the Console)" : e.Message, "OK");
        }
    }

    [MenuItem(MenuPath, true)]
    static bool RebuildMenuValidate() { return !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling; }

    [MenuItem(EnabledMenuPath)]
    static void ToggleEnabled()
    {
        Enabled = !Enabled;
        Debug.Log(Enabled ? "[TownLife] enabled: the next city publish rebuilds the layer." : "[TownLife] DISABLED: the next city publish clears the layer and builds nothing (emergency switch).");
    }
    [MenuItem(EnabledMenuPath, true)]
    static bool ToggleEnabledValidate() { Menu.SetChecked(EnabledMenuPath, Enabled); return true; }
}

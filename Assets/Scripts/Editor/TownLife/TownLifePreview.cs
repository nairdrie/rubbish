// Owner: architect, then core agent. The no-recompile, no-save preview harness behind tools/preview.sh.
//
// Run() reads the LIVE town (never modifies it), builds the requested TownLife modules into a temporary
// HideAndDontSave root that lives inside a PreviewRenderUtility scene, with the kit in TRANSIENT mode (in-memory
// meshes / materials, zero AssetDatabase writes), mirrors the scene's own renderers next to it (minus what integration
// will replace), photographs fixed + custom camera poses, destroys everything and reports leaks and dirty flags.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Preview harness + the architect's smoke module.</summary>
public static class TownLifePreview
{
    /// <summary>A camera pose. ortho &gt; 0: straight-down style orthographic shot with that half height (metres);
    /// otherwise perspective with vertical field of view <c>fov</c> degrees.</summary>
    public sealed class Shot { public string name; public Vector3 eye, look; public float ortho, fov; public bool custom; }

    static Shot S(string name, float ex, float ey, float ez, float lx, float ly, float lz, float ortho, float fov)
    { return new Shot { name = name, eye = new Vector3(ex, ey, ez), look = new Vector3(lx, ly, lz), ortho = ortho, fov = fov }; }

    /// <summary>The 34 fixed poses of the "before" photo set (understand/evals/starter_district_photos.cs). Do not
    /// edit: before/after comparisons depend on them. Add new views through a custom shots file instead.</summary>
    public static readonly Shot[] FixedShots =
    {
        S("acres-top", 265, 400, 148, 265, 0, 148, 145, 0), S("meadows-top", 628, 400, 158, 628, 0, 158, 150, 0),
        S("park-top", 248, 400, 213, 248, 0, 213, 56, 0), S("park-eye", 256.8f, 1.7f, 171.5f, 256.8f, 1.2f, 213f, 0, 65),
        S("park-oblique", 186, 30, 163, 265, 0, 222, 0, 65), S("acres-oblique-sw", 30, 120, -40, 290, 0, 180, 0, 65),
        S("acres-oblique-ne", 520, 120, 330, 260, 0, 140, 0, 65), S("meadows-oblique", 470, 120, -20, 650, 0, 180, 0, 65),
        S("street-elm", 184, 1.7f, 70, 184, 1.5f, 250, 0, 65), S("street-maple", 192, 1.7f, 165.75f, 450, 1.5f, 165.75f, 0, 65),
        S("street-home", 192, 1.7f, 58.5f, 450, 1.5f, 58.5f, 0, 65), S("street-oak", 422.5f, 1.7f, 70, 422.5f, 1.5f, 250, 0, 65),
        S("street-birch", 192, 1.7f, 260, 420, 1.5f, 260, 0, 65), S("backyard-block", 238, 15, 86, 252, 0, 122, 0, 65),
        S("court-carpool-eye", 567, 1.7f, 151, 567, 1.0f, 129, 0, 65), S("court-juicebox-oblique", 664, 40, 238, 686, 0, 156, 0, 65),
        S("mile-eye", 498, 1.7f, 65, 700, 1.5f, 63, 0, 65), S("edge-river", 290, 1.7f, 266, 340, 0.3f, 335, 0, 65),
        S("edge-open", 312, 1.7f, 92, 312, 1.2f, -50, 0, 65), S("depot-row", 123.5f, 1.7f, 66, 123.5f, 1.5f, 250, 0, 65),
        S("park-eye-north", 256.8f, 1.7f, 254.5f, 256.8f, 1.2f, 213f, 0, 65), S("street-parkave", 312, 1.7f, 70, 312, 1.5f, 250, 0, 65),
        S("backyard-top", 248, 400, 112, 248, 0, 112, 62, 0), S("backyard-block-axis", 188, 15, 111, 236, 0, 111, 0, 65),
        S("backyard-void-eye", 250, 1.7f, 110, 300, 1.4f, 112, 0, 65), S("fence-detail-top", 252, 400, 80, 252, 0, 80, 22, 0),
        S("meadows-inner-eye", 633, 1.7f, 112, 628, 1.4f, 260, 0, 65), S("mile-east-eye", 757, 1.7f, 125, 760.5f, 1.5f, 230, 0, 65),
        S("meadows-oblique-ne", 800, 120, 330, 620, 0, 150, 0, 65), S("court-carpool-top", 567, 400, 129, 567, 0, 129, 45, 0),
        S("edge-open-aerial", 305, 30, 78, 305, 0, -30, 0, 65), S("edge-open-yard", 276.5f, 1.7f, 50, 276.5f, 1.2f, -50, 0, 65),
        S("depot-row-oblique", 160, 35, 30, 105, 0, 150, 0, 65), S("depot-exit-eye", 95, 1.7f, 84.5f, 184, 1.5f, 84.5f, 0, 65),
    };

    /// <summary>Reads a custom shots file: one shot per line, whitespace or tab separated, '#' comments, same columns
    /// as shots.tsv: <c>name eyeX eyeY eyeZ lookX lookY lookZ ortho fov</c> (ortho &gt; 0 = orthographic half height,
    /// else perspective with vertical fov; fov 0 defaults to 65). A header line starting with "name" is ignored.</summary>
    public static List<Shot> ReadShots(string path)
    {
        var list = new List<Shot>();
        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw.Trim(); if (line.Length == 0 || line[0] == '#' || line.StartsWith("name")) continue;
            var f = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 7) throw new FormatException("Bad shot line (need name + 6 numbers [+ ortho fov]): " + raw);
            float N(int i) { return i < f.Length ? float.Parse(f[i], CultureInfo.InvariantCulture) : 0f; }
            var shot = S(f[0], N(1), N(2), N(3), N(4), N(5), N(6), N(7), N(8)); shot.custom = true;
            if (shot.ortho <= 0f && shot.fov <= 0f) shot.fov = 65f;
            list.Add(shot);
        }
        return list;
    }

    /// <summary>
    /// ENTRY POINT (called by tools/preview.sh through reflection; all arguments are strings).
    /// modules  : "all" or a csv of module names: paint, park, meadows, streets, yards, edges, plus the preview-only
    ///            "fences" (draw the planned boundary network) and "smoke" (architect's self-test); the alias
    ///            "everything" = fences + the six real modules; "none" = build nothing (photograph the scene as it
    ///            is, nothing hidden unless asked). Any other name
    ///            "foo" runs static TownLifeFoo.Build(TownLifeContext) from the draft sources as a preview-only module
    ///            after the fixed ones (experiments, prop sheets).
    /// outDir   : absolute folder for &lt;shot&gt;.png, shots.tsv, report.txt and result.txt (written LAST; first line OK/FAILED).
    /// shots    : "all" (34 fixed + every custom shot) or a csv of shot names (fixed or custom).
    /// options  : "key=value;key=value". Keys:
    ///   shotsFile=&lt;abs path&gt;      custom shots (see ReadShots)
    ///   districts=acres|meadows|acres+meadows
    ///   hideFences=0|1             (default 1; 0 with "none") hide the yard fences integration replaces: LEGACY
    ///                              CoastalYardBoundary markers of starter-district homes always; NEW-style markers (built
    ///                              from TownLifeFences after integration; they contain TL_ objects) only while the
    ///                              "fences" preview module draws the planned network itself (no doubles)
    ///   hidePark=0|1               hide what the park module retires (TownLifeBuilder.IsRetiredParkLegacy: every legacy
    ///                              child of NeighborhoodGen/Park except the playground KidPen, what stands inside the pen
    ///                              and SM_lemonade_shop*). Default 1 when park, smoke or all modules run, else 0
    ///   parkKeep=a,b               name prefixes under Park that stay visible in addition
    ///   hideLive=0|1               hide the INTEGRATED TownLife objects already in the scene (NeighborhoodGen/TownLife,
    ///                              Park/TL_*) so a preview build does not double them (default 1 when any real module is built)
    ///   hide=a,b                   exact GameObject names (anywhere in the town scenes) whose subtrees are hidden
    ///   noScene=1                  photograph only the TownLife objects (prop sheets)
    ///   size=WxH                   image size (default 1600x1000)
    /// Any other key is passed through to the modules via ctx.options.
    /// </summary>
    public static string Run(string modules, string outDir, string shots, string options)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(outDir);
        string resultPath = Path.Combine(outDir, "result.txt");
        if (File.Exists(resultPath)) File.Delete(resultPath);
        var result = new StringBuilder(); bool ok = false; string failure = null;
        PreviewRenderUtility preview = null; GameObject root = null; TownLifeContext ctx = null;
        var dirtyBefore = DirtyFlags(); var countsBefore = ObjectCounts(); int previewScenesBefore = EditorSceneManager.previewSceneCount;
        long prepareMs = 0, buildMs = 0, cloneMs = 0, renderMs = 0; int rendered = 0, cloned = 0, hidden = 0, repainted = 0, tlRenderers = 0, wanted = 0;
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Edit Mode only; never interrupt a play session.");
            if (EditorApplication.isCompiling) throw new InvalidOperationException("The Editor is compiling.");
            var town = TownLifeScene.FindTown();
            if (town == null) throw new InvalidOperationException("Neighborhood.unity is not loaded (or has no NeighborhoodGen root).");
            var opts = TownLifeOptions.Parse(modules, options);
            int width = 1600, height = 1000;
            var size = (opts.Get("size") ?? "").Split('x'); if (size.Length == 2) { width = int.Parse(size[0]); height = int.Parse(size[1]); }

            // ---- shots
            var all = new List<Shot>(FixedShots);
            string shotsFile = opts.Get("shotsFile"); if (!string.IsNullOrEmpty(shotsFile)) all.AddRange(ReadShots(shotsFile));
            var names = new HashSet<string>((shots ?? "all").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            bool everything = names.Count == 0 || names.Contains("all");
            var todo = new List<Shot>(); foreach (var s in all) if (everything || names.Contains(s.name)) todo.Add(s);
            foreach (var n in names) if (n != "all" && !todo.Exists(s => s.name == n)) throw new ArgumentException("Unknown shot '" + n + "' (not a fixed shot and not in shotsFile).");
            wanted = todo.Count;

            // ---- build TownLife into a temporary root inside the preview scene
            var clock = System.Diagnostics.Stopwatch.StartNew();
            preview = new PreviewRenderUtility();
            root = new GameObject("TL_PreviewRoot") { hideFlags = HideFlags.HideAndDontSave };
            preview.AddSingleGO(root);
            TownLifeBuilder.AddPreviewModule("smoke", Smoke);
            // experiments: a requested module that is not registered resolves to TownLife<Name>.Build(TownLifeContext) in this assembly
            foreach (var wantedModule in opts.modules)
            {
                if (wantedModule == TownLifeOptions.None || TownLifeBuilder.Modules.Exists(m => m.name == wantedModule)) continue;
                var type = typeof(TownLifePreview).Assembly.GetType("TownLife" + char.ToUpperInvariant(wantedModule[0]) + wantedModule.Substring(1));
                var build = type != null ? type.GetMethod("Build", new[] { typeof(TownLifeContext) }) : null;
                if (build == null || !build.IsStatic) throw new ArgumentException("Unknown module '" + wantedModule + "' (no registered module and no static TownLife" + char.ToUpperInvariant(wantedModule[0]) + wantedModule.Substring(1) + ".Build(TownLifeContext)).");
                TownLifeBuilder.AddPreviewModule(wantedModule, (Action<TownLifeContext>)Delegate.CreateDelegate(typeof(Action<TownLifeContext>), build));
            }
            ctx = TownLifeBuilder.Prepare(town, root.transform, opts, true);
            prepareMs = clock.ElapsedMilliseconds; clock.Restart();
            TownLifeBuilder.Run(ctx);
            buildMs = clock.ElapsedMilliseconds; clock.Restart();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r.enabled) tlRenderers++;

            // ---- mirror the scene's own renderers (never touching them)
            bool nothingBuilt = opts.modules.Count == 1 && opts.modules.Contains(TownLifeOptions.None);
            bool realModuleBuilt = false; foreach (var m in TownLifeBuilder.Modules) if (!m.previewOnly && opts.Module(m.name)) realModuleBuilt = true;
            var rule = new HideRule
            {
                scene = ctx.scene, hideFences = opts.Flag("hideFences", !nothingBuilt), fencesDrawn = opts.modules.Contains("fences"),
                hidePark = opts.Flag("hidePark", opts.modules.Contains("park") || opts.modules.Contains("smoke") || opts.modules.Count == 0),
                hideLive = opts.Flag("hideLive", realModuleBuilt), liveRoot = town.Find(TownLifeBuilder.GroupName),
            };
            var parkKeep = (opts.Get("parkKeep") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var hideNames = new HashSet<string>((opts.Get("hide") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var parkGroup = ctx.scene.park != null ? ctx.scene.park.group : null;
            if (!opts.Flag("noScene"))
            {
                var scenes = new List<Scene> { town.gameObject.scene };
                var depot = SceneManager.GetSceneByPath("Assets/Scenes/DepotRow.unity"); if (depot.IsValid() && depot.isLoaded) scenes.Add(depot);
                foreach (var scene in scenes) foreach (var sceneRoot in scene.GetRootGameObjects())
                {
                    var lowLods = new HashSet<Renderer>();
                    foreach (var group in sceneRoot.GetComponentsInChildren<LODGroup>(true))
                    {
                        var lods = group.GetLODs(); var top = new HashSet<Renderer>();
                        if (lods.Length > 0 && lods[0].renderers != null) foreach (var r in lods[0].renderers) if (r) top.Add(r);
                        for (int i = 1; i < lods.Length; i++) if (lods[i].renderers != null) foreach (var r in lods[i].renderers) if (r && !top.Contains(r)) lowLods.Add(r);
                    }
                    foreach (var f in sceneRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var r = f.GetComponent<MeshRenderer>();
                        if (!r || !r.enabled || !r.gameObject.activeInHierarchy || !f.sharedMesh || lowLods.Contains(r)) continue;
                        if (rule.Hidden(r, parkGroup, parkKeep, hideNames)) { hidden++; continue; }
                        var go = new GameObject(f.name) { hideFlags = HideFlags.HideAndDontSave };
                        go.transform.SetPositionAndRotation(f.transform.position, f.transform.rotation); go.transform.localScale = f.transform.lossyScale;
                        go.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                        var copy = go.AddComponent<MeshRenderer>();
                        if (ctx.materialOverrides.TryGetValue(r, out var painted) && painted != null) { copy.sharedMaterials = painted; repainted++; }
                        else copy.sharedMaterials = r.sharedMaterials;
                        copy.shadowCastingMode = r.shadowCastingMode; copy.receiveShadows = r.receiveShadows;
                        if (r.HasPropertyBlock()) { var block = new MaterialPropertyBlock(); r.GetPropertyBlock(block); copy.SetPropertyBlock(block); }
                        preview.AddSingleGO(go); cloned++;
                    }
                }
            }
            cloneMs = clock.ElapsedMilliseconds; clock.Restart();

            // ---- fixed lighting: identical to the "before" set so photos are comparable
            var sun = preview.lights[0]; sun.transform.rotation = Quaternion.Euler(38, 302, 0);
            sun.color = new Color(1f, .9f, .76f); sun.intensity = 1.45f; sun.shadows = LightShadows.Soft; sun.shadowStrength = 1f;
            preview.lights[1].intensity = 0f;
            preview.ambientColor = new Color(.55f, .6f, .68f);
            var cam = preview.camera; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.57f, .74f, .84f);
            cam.nearClipPlane = .15f; cam.farClipPlane = 3000f; cam.cullingMask = ~0;

            var tsv = new StringBuilder("name\teyeX\teyeY\teyeZ\tlookX\tlookY\tlookZ\tortho\tfov\n");
            foreach (var shot in todo)
            {
                tsv.Append(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\n", shot.name, shot.eye.x, shot.eye.y, shot.eye.z, shot.look.x, shot.look.y, shot.look.z, shot.ortho, shot.fov));
                var forward = (shot.look - shot.eye).normalized;
                var up = Mathf.Abs(forward.y) > .999f ? Vector3.forward : Vector3.up;
                cam.orthographic = shot.ortho > 0; if (shot.ortho > 0) cam.orthographicSize = shot.ortho; else cam.fieldOfView = shot.fov;
                cam.transform.SetPositionAndRotation(shot.eye, Quaternion.LookRotation(forward, up));
                preview.BeginStaticPreview(new Rect(0, 0, width, height)); preview.Render(true, false); var image = preview.EndStaticPreview();
                try { File.WriteAllBytes(Path.Combine(outDir, shot.name + ".png"), image.EncodeToPNG()); rendered++; }
                finally { UnityEngine.Object.DestroyImmediate(image); }
            }
            File.WriteAllText(Path.Combine(outDir, "shots.tsv"), tsv.ToString());
            renderMs = clock.ElapsedMilliseconds;
            ok = true;
        }
        catch (Exception e) { failure = e.ToString(); }
        finally
        {
            // ---- destroy every temporary object, mesh, material and texture
            try { if (preview != null) preview.Cleanup(); } catch (Exception e) { failure = (failure ?? "") + "\ncleanup: " + e; ok = false; }
            if (root) UnityEngine.Object.DestroyImmediate(root);
            try { TownLifeKit.End(); TownLifeKit.DestroyTransient(); } catch (Exception e) { failure = (failure ?? "") + "\nkit cleanup: " + e; ok = false; }
        }

        // ---- post-conditions
        var dirtyAfter = DirtyFlags(); var countsAfter = ObjectCounts(); int leaked = LeakedTownLifeObjects(out string leakSample);
        bool dirtied = false; foreach (var pair in dirtyAfter) if (pair.Value && !(dirtyBefore.TryGetValue(pair.Key, out bool was) && was)) dirtied = true;
        if (dirtied) { ok = false; failure = (failure ?? "") + "\nA scene became dirty during the preview."; }
        if (EditorSceneManager.previewSceneCount > previewScenesBefore) { ok = false; failure = (failure ?? "") + "\nA preview scene leaked."; }
        if (leaked > 0) { ok = false; failure = (failure ?? "") + "\nLeaked " + leaked + " TownLife objects, e.g. " + leakSample; }
        bool moduleErrors = ctx != null && ctx.errors.Count > 0;

        result.Append(ok ? (moduleErrors ? "OK-WITH-MODULE-ERRORS" : "OK") : "FAILED").Append('\n');
        result.Append("rendered=" + rendered + "/" + wanted + " modules=" + modules + " outDir=" + outDir + "\n");
        result.Append("townLifeObjects=" + TownLifeKit.SpawnCount + " townLifeRenderers=" + tlRenderers + " sceneClones=" + cloned + " hiddenSceneRenderers=" + hidden + " repaintedClones=" + repainted + "\n");
        result.Append("ms: prepare=" + prepareMs + " build=" + buildMs + " clone=" + cloneMs + " render=" + renderMs + " total=" + total.ElapsedMilliseconds + "\n");
        result.Append("dirtyBefore: " + Describe(dirtyBefore) + "\ndirtyAfter:  " + Describe(dirtyAfter) + "\n");
        result.Append("leakedTownLifeObjects=" + leaked + " previewScenes before -> after: " + previewScenesBefore + " -> " + EditorSceneManager.previewSceneCount + "\n");
        result.Append("objectCounts before -> after: " + countsBefore + " -> " + countsAfter + "\n");
        if (ctx != null) foreach (var stat in ctx.stats) result.Append("module " + stat.name + ": objects=" + stat.spawned + " gameObjects=" + stat.gameObjects + " ms=" + stat.ms + (stat.previewOnly ? " previewOnly" : "") + (stat.failed ? " FAILED" : "") + "\n");
        if (ctx != null) result.Append("budget: " + ctx.budgetObjects + "/" + TownLifeBuilder.ObjectBudget + "\n");
        if (moduleErrors) { result.Append("MODULE ERRORS:\n"); foreach (var e in ctx.errors) result.Append("  " + e + "\n"); }
        if (failure != null) result.Append("FAILURE:\n" + failure + "\n");
        if (ctx != null) File.WriteAllText(Path.Combine(outDir, "report.txt"), ctx.report.ToString());
        File.WriteAllText(resultPath, result.ToString());
        return result.ToString();
    }

    /// <summary>What the photographs leave out of the live scene (see the option docs on Run).</summary>
    sealed class HideRule
    {
        public TownLifeScene scene; public bool hideFences, fencesDrawn, hidePark, hideLive; public Transform liveRoot;
        readonly Dictionary<CoastalYardBoundary, bool> markerHidden = new Dictionary<CoastalYardBoundary, bool>();
        readonly Dictionary<Transform, bool> parkChildHidden = new Dictionary<Transform, bool>();

        /// <summary>A marker built from TownLifeFences carries kit objects (TL_*); a legacy marker has PropertyBoundary / FencePanel children only.</summary>
        public static bool IsNewStyleMarker(CoastalYardBoundary marker)
        {
            foreach (var t in marker.GetComponentsInChildren<Transform>(true)) if (t != marker.transform && t.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal)) return true;
            return false;
        }
        bool MarkerHidden(CoastalYardBoundary marker)
        {
            if (markerHidden.TryGetValue(marker, out bool hide)) return hide;
            bool starter = false;
            if (marker.stopIds != null) foreach (var id in marker.stopIds) if (scene.House(id) != null) { starter = true; break; }
            // legacy fences of the two districts are replaced by integration; the new network is hidden only when the
            // preview draws the planned one in its place; Hills / Bluffs markers inside the area always stay.
            hide = starter && (IsNewStyleMarker(marker) ? fencesDrawn : true);
            markerHidden[marker] = hide; return hide;
        }
        public bool Hidden(Renderer renderer, Transform parkGroup, string[] parkKeep, HashSet<string> hideNames)
        {
            bool tl = false;
            for (var p = renderer.transform; p != null; p = p.parent)
            {
                if (hideNames.Count > 0 && hideNames.Contains(p.name)) return true;
                if (hideLive && liveRoot != null && p == liveRoot) return true;
                if (hideFences) { var marker = p.GetComponent<CoastalYardBoundary>(); if (marker != null && MarkerHidden(marker)) return true; }
                if (p.parent == parkGroup && parkGroup != null)
                {
                    tl = p.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal);
                    if (tl) return hideLive;                                   // integrated Park/TL_Park
                    if (!hidePark) return false;
                    if (!parkChildHidden.TryGetValue(p, out bool hide))
                    {
                        hide = TownLifeBuilder.IsRetiredParkLegacy(p, scene);
                        if (hide) foreach (var keep in parkKeep) if (p.name.StartsWith(keep, StringComparison.Ordinal)) { hide = false; break; }
                        parkChildHidden[p] = hide;
                    }
                    return hide;
                }
            }
            return false;
        }
    }

    static Dictionary<string, bool> DirtyFlags()
    {
        var d = new Dictionary<string, bool>();
        for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); d[s.name] = s.isDirty; }
        return d;
    }
    static string Describe(Dictionary<string, bool> flags)
    {
        var sb = new StringBuilder(); foreach (var p in flags) sb.Append(p.Key + "=" + p.Value + " "); return sb.ToString();
    }
    static string ObjectCounts()
    {
        return "go=" + Resources.FindObjectsOfTypeAll<GameObject>().Length + " mesh=" + Resources.FindObjectsOfTypeAll<Mesh>().Length +
               " mat=" + Resources.FindObjectsOfTypeAll<Material>().Length + " tex=" + Resources.FindObjectsOfTypeAll<Texture>().Length;
    }
    /// <summary>Non-asset objects whose name starts with "TL_" that are still alive (must be 0 after a preview).</summary>
    static int LeakedTownLifeObjects(out string sample)
    {
        int n = 0; sample = "";
        foreach (var o in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
        {
            if (!(o is GameObject || o is Mesh || o is Material || o is Texture)) continue;
            if (!o.name.StartsWith(TownLifeKit.Prefix, StringComparison.Ordinal) || EditorUtility.IsPersistent(o)) continue;
            if (o is GameObject go && go.scene.IsValid() && go.scene.isLoaded && (go.hideFlags & HideFlags.DontSave) == 0) continue;   // a real, integrated TownLife object
            if (n++ == 0) sample = o.GetType().Name + " " + o.name;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------------ smoke
    /// <summary>Architect's self-test (module name "smoke"): a few props, a ribbon path, a disc, a fence-like run and a
    /// sign inside the Pickle Park block. Not content; doubles as the worked example in CONTRACT.md.</summary>
    public static void Smoke(TownLifeContext ctx)
    {
        var park = ctx.scene.park; if (park == null) { ctx.Error("smoke", "no park"); return; }
        var block = park.block; var group = ctx.Group("Smoke"); var occ = ctx.occupancy;

        TownLifeKit.Define("smoke.pink", new Color(1f, 0.35f, 0.65f), 0.3f);
        TownLifeKit.DefineProp("smoke.totem")
            .Cyl("paint.red", Vector3.zero, 0.25f, 1.6f, 4)
            .Sphere("paint.yellow", new Vector3(0f, 2.0f, 0f), new Vector3(0.55f, 0.55f, 0.55f), 12, 8, true)
            .Cone("paint.blue", new Vector3(0f, 2.45f, 0f), 0.4f, 0.7f, 8)
            .Sphere("eye.white", new Vector3(-0.2f, 2.1f, 0.45f), Vector3.one * 0.16f).Sphere("eye.white", new Vector3(0.2f, 2.1f, 0.45f), Vector3.one * 0.16f)
            .Sphere("eye.black", new Vector3(-0.2f, 2.1f, 0.58f), Vector3.one * 0.07f).Sphere("eye.black", new Vector3(0.2f, 2.1f, 0.58f), Vector3.one * 0.07f)
            .Torus("smoke.pink", new Vector3(0f, 1.6f, 0f), 0.4f, 0.1f)
            .ColliderBox(new Vector3(0f, 0.8f, 0f), new Vector3(0.5f, 1.6f, 0.5f));
        TownLifeKit.DefineProp("smoke.panel")
            .Bar("wood.dark", new Vector3(0f, 0.45f, 0f), new Vector3(0f, 0.45f, 3f), 0.08f).Bar("wood.dark", new Vector3(0f, 1.15f, 0f), new Vector3(0f, 1.15f, 3f), 0.08f)
            .Box("wood.warm", new Vector3(0.05f, 0.75f, 0.375f), new Vector3(0.04f, 1.5f, 0.3f)).Box("wood.warm", new Vector3(0.05f, 0.75f, 0.75f), new Vector3(0.04f, 1.5f, 0.3f))
            .Box("wood.warm", new Vector3(0.05f, 0.75f, 1.125f), new Vector3(0.04f, 1.5f, 0.3f)).Box("wood.warm", new Vector3(0.05f, 0.75f, 1.5f), new Vector3(0.04f, 1.5f, 0.3f))
            .Box("wood.warm", new Vector3(0.05f, 0.75f, 1.875f), new Vector3(0.04f, 1.5f, 0.3f)).Box("wood.warm", new Vector3(0.05f, 0.75f, 2.25f), new Vector3(0.04f, 1.5f, 0.3f))
            .Box("wood.warm", new Vector3(0.05f, 0.75f, 2.625f), new Vector3(0.04f, 1.5f, 0.3f));
        TownLifeKit.DefineProp("smoke.post").Cyl("wood.dark", Vector3.zero, 0.09f, 1.7f, 4).Gable("paint.white", new Vector3(0f, 1.7f, 0f), new Vector3(0.26f, 0.12f, 0.26f));

        // a curving path (walkable ribbon) through the west half, and a pond-like disc
        var path = new List<Vector3>();
        for (int i = 0; i <= 16; i++) { float t = i / 16f; path.Add(new Vector3(Mathf.Lerp(block.xMin + 6f, park.landmark.x - 8f, t), 0f, Mathf.Lerp(block.yMin + 10f, block.yMax - 10f, t) + Mathf.Sin(t * Mathf.PI * 2f) * 8f)); }
        TownLifeKit.Ribbon(group, "smoke_path", path, 3f, "paving.warm", TownLifeContext.PavingY, true);
        occ.ReservePolyline(path, 2.2f, TownLifeTags.PathSurface, TownLifeOccupancy.Block.Solids);
        var pond = new Vector3(park.landmark.x + 26f, 0f, park.landmark.z + 18f);
        TownLifeKit.Disc(group, "smoke_pond", pond, 7f, "water", TownLifeContext.SoilY);
        TownLifeKit.Ring(group, "smoke_pond_rim", pond, 7f, 7.8f, "stone.light", TownLifeContext.PavingY);
        occ.ReserveCircle(pond, 8f, TownLifeTags.Water2, TownLifeOccupancy.Block.Everything);

        // 12 totems around the pond, facing outwards
        int totems = 0, trees = 0, rejected = 0;
        for (int i = 0; i < 12; i++)
        {
            float a = i * 30f; var p = pond + TownLifeGeo.Dir(a) * 10f; p.y = ctx.scene.GroundY(p);
            if (!occ.TryReserve(p, 0.6f, TownLifeTags.Feature + ".smoke")) { rejected++; continue; }
            TownLifeKit.Spawn(group, "smoke.totem", p, a); totems++;
        }
        // ~20 stock trees scattered with the module RNG, at least 7 m apart, clear of every keep-out and reservation
        var rng = ctx.Rng("smoke/trees");
        for (int tries = 0; tries < 400 && trees < 20; tries++)
        {
            var p = new Vector3(TownLifeContext.Range(rng, block.xMin + 3f, block.xMax - 3f), 0f, TownLifeContext.Range(rng, block.yMin + 3f, block.yMax - 3f));
            if (occ.Within(p, 7f, TownLifeTags.Tree) || !occ.TryReserve(p, 1.2f, TownLifeTags.Tree + ".smoke")) { rejected++; continue; }
            p.y = ctx.scene.GroundY(p);
            TownLifeKit.Spawn(group, "kit.tree.round", p, TownLifeContext.Range(rng, 0f, 360f), TownLifeContext.Range(rng, 0.85f, 1.3f)); trees++;
        }
        // a fence-like run with a corner, solid collider, one merged mesh
        var run = new List<Vector3> { new Vector3(block.xMin + 4f, 0f, block.yMin + 3f), new Vector3(park.landmark.x - 12f, 0f, block.yMin + 3f), new Vector3(park.landmark.x - 12f, 0f, block.yMin + 14.3f) };
        TownLifeKit.Run(group, "smoke_fence", run, new TownLifeKit.RunSpec { panel = "smoke.panel", panelLength = 3f, post = "smoke.post", collider = TownLifeKit.RunCollider.Box, colliderHeight = 1.5f });
        occ.ReservePolyline(run, 0.6f, TownLifeTags.Fence + ".smoke");
        // a sign facing the Maple Street entrance
        TownLifeKit.SignBoard(group, "smoke_sign", new Vector3(park.landmark.x + 7f, 1.9f, block.yMin + 7f), 180f, new Vector3(5f, 1.4f, 0.12f), "paint.white", "SMOKE TEST", new Color(0.1f, 0.35f, 0.15f), true);
        TownLifeKit.Spawn(group, "smoke.post", new Vector3(park.landmark.x + 5f, ctx.scene.GroundY(Vector3.zero), block.yMin + 7f), 0f, new Vector3(1f, 1.6f, 1f));
        TownLifeKit.Spawn(group, "smoke.post", new Vector3(park.landmark.x + 9f, ctx.scene.GroundY(Vector3.zero), block.yMin + 7f), 0f, new Vector3(1f, 1.6f, 1f));
        TownLifeKit.Mound(group, "smoke_mound", new Vector3(block.xMax - 14f, 0f, block.yMin + 14f), new Vector2(9f, 7f), 2.2f, "leaf.lime", TownLifeContext.GrassY, 7);

        ctx.Log("smoke", totems + " totems, " + trees + " trees, " + rejected + " rejected positions; park block " + block + "; playground pen " + (park.playground != null ? park.playground.penId : "none"));
    }
}

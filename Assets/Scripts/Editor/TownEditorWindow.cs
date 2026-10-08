using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Visual authoring workspace. Uses the real Unity Scene view, not a separate approximation of the town.</summary>
public sealed class TownEditorWindow : EditorWindow
{
    enum PaintMode { Select, Stamp, Brush, Line, Erase, MoveProperty }
    [SerializeField] int tab, category;
    [SerializeField] string search = "", activeId = "";
    [SerializeField] List<string> mixIds = new List<string>();
    [SerializeField] PaintMode mode;
    [SerializeField] float yaw, scale = 1, grid = 1, spacing = 5, radius = 12, variation = .15f;
    [SerializeField] int density = 4;
    [SerializeField] bool randomYaw = true, snapGrid, allowOverlap, eraseProperties, favoritesOnly;
    [SerializeField] List<string> favorites = new List<string>();
    [SerializeField] Vector2 paletteScroll, settingsScroll, bodyScroll;
    TownAssetPalette palette;
    TownRoadAuthoring roads;
    Material ghost;
    TownPlacedAsset moving;
    GameObject customPrefab;
    string customName = "", status = "Open the town, choose an asset, then click in the Scene view.";
    int customRole;
    float customFrontYaw;
    bool customOpen, stroke, hasHover, lineStarted;
    bool cameraNavigating;
    int undoGroup, placedInStroke;
    Vector3 hover, previousPaint, lineStart;
    readonly System.Random random = new System.Random();
    double previewAt;
    string previewError;
    float paletteTop = 370;
    float bodyTop = 115;
    [Serializable] sealed class PalettePreferences { public List<string> favorites = new List<string>(); }
    static string PreferencesKey => "Rubbish.TownEditor.Palette." + Hash128.Compute(Application.dataPath);

    [MenuItem("Rubbish/Map Editor")]
    [MenuItem("Rubbish/City/Map Editor")]
    public static void Open()
    {
        var inspector = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
        var window = inspector != null ? GetWindow<TownEditorWindow>("Town Builder", true, inspector) : GetWindow<TownEditorWindow>();
        window.titleContent = new GUIContent("Town Builder");
        window.minSize = new Vector2(360, 570); window.Show();
    }
    void OnEnable()
    {
        titleContent = new GUIContent("Town Builder"); minSize = new Vector2(360, 570);
        SceneView.duringSceneGui += OnSceneGUI; Undo.undoRedoPerformed += Changed;
        EditorApplication.playModeStateChanged += PlayState;
        roads = new TownRoadAuthoring();
        roads.PublishOverride = TownGameplayAuthoring.Publish;
        roads.Published = message => { status = message; Changed(); };
        if (favorites.Count == 0)
        {
            try { var preferences = JsonUtility.FromJson<PalettePreferences>(EditorPrefs.GetString(PreferencesKey, "{}")); if (preferences?.favorites != null) favorites = preferences.favorites; }
            catch { /* A damaged preference never prevents editing the project. */ }
        }
    }
    void OnDisable()
    {
        EndStroke(); SceneView.duringSceneGui -= OnSceneGUI; Undo.undoRedoPerformed -= Changed;
        EditorApplication.playModeStateChanged -= PlayState; roads?.Dispose();
        TownAssetThumbnails.Clear();
        EditorPrefs.SetString(PreferencesKey, JsonUtility.ToJson(new PalettePreferences { favorites = favorites }));
        if (ghost != null) DestroyImmediate(ghost);
    }
    void PlayState(PlayModeStateChange state) { mode = PaintMode.Select; lineStarted = false; EndStroke(); Repaint(); }
    void Changed() { previewAt = 0; Repaint(); SceneView.RepaintAll(); }
    TownAssetEntry Active => palette != null ? palette.entries.FirstOrDefault(e => e != null && e.id == activeId) : null;
    void EnsurePalette()
    {
        if (palette != null) return;
        try { palette = TownAssetLibrary.Ensure(); if (string.IsNullOrEmpty(activeId)) activeId = palette.entries.FirstOrDefault()?.id; }
        catch (Exception e) { status = "Asset library: " + e.Message; }
    }
    void OnGUI()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label("RUBBISH  /  TOWN BUILDER", EditorStyles.boldLabel);
            GUILayout.Label("Place working properties. Paint a lived-in neighborhood.", EditorStyles.miniLabel);
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        { EditorGUILayout.HelpBox("Exit Play mode to edit. Your town edits are project content, not changes to a player's save.", MessageType.Info); return; }
        EnsurePalette();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open town", GUILayout.Height(27))) Try(() =>
            {
                var town = TownPlacementTools.Scene(true);
                if (!UnityEngine.SceneManagement.SceneManager.GetSceneByPath(DepotRowLayout.ScenePath).isLoaded)
                    EditorSceneManager.OpenScene(DepotRowLayout.ScenePath, OpenSceneMode.Additive);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(town);
                FrameTown(false);
            });
            GUI.backgroundColor = new Color(.45f, .85f, .66f);
            if (GUILayout.Button("Save & bake", GUILayout.Height(27))) Try(() =>
            {
                if (roads.HasChanges || roads.HasPendingDrawing) { tab = 1; roads.Publish(); }
                else status = TownGameplayAuthoring.Publish();
            });
            GUI.backgroundColor = Color.white;
        }
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Undo", EditorStyles.toolbarButton)) Undo.PerformUndo();
            if (GUILayout.Button("Redo", EditorStyles.toolbarButton)) Undo.PerformRedo();
            if (GUILayout.Button("Top view", EditorStyles.toolbarButton)) FrameTown(true);
            if (GUILayout.Button("3D view", EditorStyles.toolbarButton)) FrameTown(false);
            if (GUILayout.Button("Depot", EditorStyles.toolbarButton)) FrameDepot();
        }
        int nextTab = GUILayout.Toolbar(tab, new[] { "Assets", "Streets", "Guide" });
        if (nextTab != tab) { EndStroke(); lineStarted = false; tab = nextTab; SceneView.RepaintAll(); }
        if (tab == 1) { roads.OnGUI(); return; }
        if (tab == 2) { Guide(); return; }
        if (palette == null) return;
        if (Event.current.type == EventType.Repaint) bodyTop = GUILayoutUtility.GetLastRect().yMax;
        bodyScroll = EditorGUILayout.BeginScrollView(bodyScroll);
        var scene = TownPlacementTools.Scene();
        if (!scene.isLoaded) EditorGUILayout.HelpBox("Open town to start placing. Your pasted objects live in the separate TownAuthored layer and survive regeneration.", MessageType.Info);
        else if (scene.isDirty) EditorGUILayout.HelpBox("Unpublished edits — Save & bake updates garbage stops, paths, traffic and map data together.", MessageType.Warning);
        int shownMode = mode == PaintMode.MoveProperty ? -1 : (int)mode;
        int nextMode = GUILayout.Toolbar(shownMode, new[] { "Select", "Stamp", "Brush", "Line", "Erase" });
        if (nextMode >= 0 && nextMode != shownMode) { EndStroke(); mode = (PaintMode)nextMode; moving = null; lineStarted = false; }
        DrawSettings();
        search = EditorGUILayout.TextField("Search", search);
        var categories = new[] { "All categories" }.Concat(TownAssetLibrary.Categories).ToArray();
        category = EditorGUILayout.Popup("Category", Mathf.Clamp(category, 0, categories.Length - 1), categories);
        using (new EditorGUILayout.HorizontalScope())
        {
            favoritesOnly = GUILayout.Toggle(favoritesOnly, "Favorites only");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear mix", EditorStyles.miniButton)) mixIds.Clear();
        }
        GUILayout.Label("Ctrl/Cmd-click cards to mix brush assets. ★ saves a favorite.", EditorStyles.miniLabel);
        DrawPalette(categories);
        DrawSelection(); DrawCustom();
        using (new EditorGUILayout.HorizontalScope())
        {
            var statusLine = (status ?? "").Split('\n')[0];
            EditorGUILayout.LabelField(new GUIContent(statusLine, status), EditorStyles.wordWrappedMiniLabel, GUILayout.Height(32));
            if ((status ?? "").Contains("\n") && GUILayout.Button("Details", GUILayout.Width(52)))
                EditorUtility.DisplayDialog("Town Builder report", status, "OK");
        }
        EditorGUILayout.EndScrollView();
    }
    void DrawSettings()
    {
        var entry = Active;
        bool property = TownPlacementTools.IsProperty(entry);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label(entry != null ? entry.displayName : "Choose an asset", EditorStyles.boldLabel);
            if (entry != null) GUILayout.Label(entry.description, EditorStyles.wordWrappedMiniLabel);
            if (mode == PaintMode.Erase)
            {
                radius = EditorGUILayout.Slider("Eraser radius", radius, 1, 35);
                eraseProperties = EditorGUILayout.Toggle("Also erase working properties", eraseProperties);
                GUILayout.Label("Only removes your pasted objects, never the original town.", EditorStyles.wordWrappedMiniLabel); return;
            }
            if (property)
                EditorGUILayout.HelpBox("Working property: snaps to a street and creates collection rules. Size and frontage stay life-sized. Use Stamp or Line.", MessageType.Info);
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    yaw = EditorGUILayout.FloatField("Rotation", yaw);
                    if (GUILayout.Button("↶", GUILayout.Width(28))) yaw -= 15;
                    if (GUILayout.Button("↷", GUILayout.Width(28))) yaw += 15;
                }
                scale = EditorGUILayout.Slider("Scale", scale, .25f, 2.5f);
                allowOverlap = EditorGUILayout.Toggle("Scenery overlap", allowOverlap);
            }
            using (new EditorGUILayout.HorizontalScope())
            { snapGrid = EditorGUILayout.Toggle("Grid snap", snapGrid); using (new EditorGUI.DisabledScope(!snapGrid)) grid = EditorGUILayout.FloatField(Mathf.Max(.1f, grid)); }
            if (mode == PaintMode.Brush || mode == PaintMode.Line)
            {
                spacing = EditorGUILayout.Slider("Spacing (m)", spacing, .5f, 45);
                if (mode == PaintMode.Brush)
                {
                    radius = EditorGUILayout.Slider("Brush radius", radius, 2, 40);
                    density = EditorGUILayout.IntSlider("Per dab", density, 1, 12);
                    variation = EditorGUILayout.Slider("Size variation", variation, 0, .45f);
                    randomYaw = EditorGUILayout.Toggle("Random rotation", randomYaw);
                    if (property) GUILayout.Label("Buildings are excluded from scatter. Use a property line instead.", EditorStyles.wordWrappedMiniLabel);
                }
            }
        }
    }
    void DrawPalette(string[] categories)
    {
        var entries = palette.entries.Where(e => e != null && e.prefab != null &&
            (category == 0 || e.category == categories[category]) && (!favoritesOnly || favorites.Contains(e.id)) &&
            (string.IsNullOrWhiteSpace(search) || (e.displayName + " " + e.category + " " + e.description).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        int columns = Mathf.Max(2, Mathf.FloorToInt((position.width - 22) / 112));
        float width = (position.width - 28) / columns;
        if (Event.current.type == EventType.Repaint) paletteTop = GUILayoutUtility.GetLastRect().yMax;
        float reserved = customOpen ? 225 : 74;
        if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponentInParent<TownPlacedAsset>() != null) reserved += 25;
        float paletteHeight = Mathf.Max(155, position.height - bodyTop - paletteTop - reserved);
        paletteScroll = EditorGUILayout.BeginScrollView(paletteScroll, GUILayout.Height(paletteHeight));
        for (int start = 0; start < entries.Length; start += columns)
        {
            using (new EditorGUILayout.HorizontalScope())
            for (int j = 0; j < columns && start + j < entries.Length; j++)
            {
                var e = entries[start + j];
                var rect = GUILayoutUtility.GetRect(width, 111, GUILayout.Width(width));
                var selected = mixIds.Count > 0 ? mixIds.Contains(e.id) : activeId == e.id;
                EditorGUI.DrawRect(rect, selected ? new Color(.18f, .4f, .33f) : new Color(.18f, .19f, .21f));
                var preview = TownAssetThumbnails.Get(e) ?? AssetPreview.GetAssetPreview(e.prefab) ?? AssetPreview.GetMiniThumbnail(e.prefab);
                if (preview != null) GUI.DrawTexture(new Rect(rect.x + 4, rect.y + 3, rect.width - 8, 68), preview, ScaleMode.ScaleToFit);
                if (GUI.Button(new Rect(rect.xMax - 22, rect.y + 2, 20, 20), favorites.Contains(e.id) ? "★" : "☆", EditorStyles.miniButton))
                { if (!favorites.Remove(e.id)) favorites.Add(e.id); EditorPrefs.SetString(PreferencesKey, JsonUtility.ToJson(new PalettePreferences { favorites = favorites })); }
                if (GUI.Button(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), new GUIContent("", e.description), GUIStyle.none))
                {
                    bool additive = Event.current.control || Event.current.command;
                    activeId = UpdateBrushSelection(mixIds, activeId, e.id, additive);
                    EndStroke(); lineStarted = false;
                    if (additive) mode = PaintMode.Brush;
                    else { scale = e.defaultScale; spacing = e.spacing; randomYaw = e.randomYaw; mode = PaintMode.Stamp; }
                    Changed();
                }
                GUI.Label(new Rect(rect.x + 5, rect.y + 72, rect.width - 10, 27), e.displayName, EditorStyles.wordWrappedMiniLabel);
                GUI.Label(new Rect(rect.x + 5, rect.y + 96, rect.width - 12, 13), TownPlacementTools.IsProperty(e) ? "● PROPERTY + TRASH" : e.role == TownAssetRole.StreetProp ? "● KNOCKABLE" : "SCENERY", EditorStyles.miniLabel);
            }
        }
        if (entries.Length == 0) GUILayout.Label("No matching assets. Clear the search or add your own prefab.", EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
        if (AssetPreview.IsLoadingAssetPreviews()) Repaint();
    }
    public static string UpdateBrushSelection(List<string> mix, string active, string clicked, bool additive)
    {
        if (!additive) { mix.Clear(); return clicked; }
        if (mix.Count == 0 && !string.IsNullOrEmpty(active)) mix.Add(active);
        if (!mix.Remove(clicked)) mix.Add(clicked);
        if (mix.Count == 0) mix.Add(clicked); // Keep one usable selection rather than an invisible fallback.
        return mix.Contains(active) ? active : mix[0];
    }
    void DrawSelection()
    {
        var chosen = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<TownPlacedAsset>() : null;
        if (chosen == null) return;
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Selected: " + chosen.name, EditorStyles.miniLabel);
            if (GUILayout.Button("Stamp a copy", EditorStyles.miniButton))
            { activeId = chosen.paletteId; mode = PaintMode.Stamp; status = "Click a new position; working copies receive their own household identity."; }
            if (chosen.role == TownAssetRole.House || chosen.role == TownAssetRole.CollectionBuilding)
                if (GUILayout.Button("Move property", EditorStyles.miniButton)) { moving = chosen; activeId = chosen.paletteId; mode = PaintMode.MoveProperty; status = "Click a new street frontage. Property identity and saved collection data stay intact."; }
            if (GUILayout.Button("Remove", EditorStyles.miniButton)) { Undo.DestroyObjectImmediate(chosen.gameObject); EditorSceneManager.MarkSceneDirty(TownPlacementTools.Scene()); }
        }
    }
    void DrawCustom()
    {
        customOpen = EditorGUILayout.Foldout(customOpen, "Add your own asset", true);
        if (!customOpen) return;
        customPrefab = (GameObject)EditorGUILayout.ObjectField("Prefab / model", customPrefab, typeof(GameObject), false);
        customName = EditorGUILayout.TextField("Palette name", customName);
        customRole = EditorGUILayout.Popup("Game role", customRole, new[] { "Decorative scenery", "Working property + trash", "Knockable street prop", "Parked car" });
        if (customRole == 1)
        {
            customFrontYaw = EditorGUILayout.FloatField("Front correction (degrees)", customFrontYaw);
            GUILayout.Label("Use 0 if the front faces local +Z, 180 if it faces −Z. A curb bin and front path are added automatically.", EditorStyles.wordWrappedMiniLabel);
        }
        using (new EditorGUI.DisabledScope(customPrefab == null))
        if (GUILayout.Button("Add to my palette"))
        {
            if (TownAssetLibrary.TryAddCustom(palette, customPrefab, customName, out var entry, out var why))
            {
                Undo.RecordObject(palette, "Configure custom town asset");
                entry.role = new[] { TownAssetRole.Decoration, TownAssetRole.CollectionBuilding, TownAssetRole.StreetProp, TownAssetRole.ParkedCar }[customRole];
                entry.collectionType = customRole == 1 ? "house" : "";
                entry.propertyFrontYaw = customFrontYaw;
                entry.description = customRole == 1 ? "Working custom property: curb trash, district rules and a path to the road." : "Custom " + entry.role + " asset.";
                EditorUtility.SetDirty(palette); AssetDatabase.SaveAssetIfDirty(palette);
                activeId = entry.id; category = 0; search = ""; mode = PaintMode.Stamp; status = "Added " + entry.displayName;
            }
            else status = why;
        }
    }
    void Guide()
    {
        settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll);
        GUILayout.Label("Build directly in the real town", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("1. Open town. Choose Top view or 3D view.\n2. Pick a palette card and click in the Scene view.\n3. Paint trees with Brush; click both ends of a Line for fences or rows.\n4. Save & bake before Play or a distributable build.", MessageType.Info);
        GUILayout.Label("Scene controls", EditorStyles.boldLabel);
        GUILayout.Label("Right mouse + WASD: fly camera. Scroll: zoom. Alt-drag: orbit. F: focus selection.\nP: stamp · B: brush · L: line · X: erase · Q/E: rotate 15° · Shift+Q/E: 90° · Esc: select/cancel.\nCtrl/Cmd+Z: undo an entire stroke. Ctrl/Cmd-click palette cards to mix assets.", EditorStyles.wordWrappedLabel);
        GUILayout.Space(10); GUILayout.Label("Working properties, not fake houses", EditorStyles.boldLabel);
        GUILayout.Label("House kits include curb bags, household rules, driveway/car information and a route to the street. Shops and apartment kits also create collection stops. Click near a street; they orient toward it automatically. Use Move property to relocate without changing its identity. Empty terrain does not automatically turn into a new district.\n\nParked cars are parked scenery, not new traffic spawns. Street-prop kits use the existing knockable system. Trees, gardens, fences and clutter do not generate garbage.", EditorStyles.wordWrappedLabel);
        GUILayout.Space(10); GUILayout.Label("Safe edits", EditorStyles.boldLabel);
        GUILayout.Label("Your placements live under TownAuthored, separate from regenerated content. Erase affects only that layer. Scenery overlap permits overlapping foliage, never a blocked driving lane. Undo/redo is native Unity; save the project in Git as well. Save & bake validates the town and refreshes collection, navigation and the map. Road drafts have their own Undo history and explicit publish.\n\nAll multiplayer players need the same published town/build; this is an authoring tool in Unity, not a live multiplayer editor.", EditorStyles.wordWrappedLabel);
        if (GUILayout.Button("Inspect palette asset")) { EnsurePalette(); Selection.activeObject = palette; }
        EditorGUILayout.EndScrollView();
    }
    void OnSceneGUI(SceneView view)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (tab == 1) { roads?.OnSceneGUI(view); return; }
        if (tab != 0 || !TownPlacementTools.Scene().isLoaded) return;
        Event e = Event.current;
        if (e.type == EventType.MouseDown && (e.button == 1 || e.button == 2)) cameraNavigating = true;
        if (e.type == EventType.MouseUp && (e.button == 1 || e.button == 2)) cameraNavigating = false;
        if (e.alt || cameraNavigating || e.button == 1 || e.button == 2) return;
        if (e.type == EventType.KeyDown && !EditorGUIUtility.editingTextField && !e.control && !e.command)
        {
            bool handled = true;
            switch (e.keyCode)
            {
                case KeyCode.Escape: mode = PaintMode.Select; lineStarted = false; moving = null; EndStroke(); break;
                case KeyCode.Q: if (mode == PaintMode.Select) handled = false; else yaw -= e.shift ? 90 : 15; break;
                case KeyCode.E: if (mode == PaintMode.Select) handled = false; else yaw += e.shift ? 90 : 15; break;
                case KeyCode.P: mode = PaintMode.Stamp; break;
                case KeyCode.B: mode = PaintMode.Brush; break;
                case KeyCode.L: mode = PaintMode.Line; break;
                case KeyCode.X: mode = PaintMode.Erase; break;
                default: handled = false; break;
            }
            if (handled) { e.Use(); Changed(); }
        }
        if (mode == PaintMode.Select) return;
        int control = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
        hasHover = TownPlacementTools.Ground(HandleUtility.GUIPointToWorldRay(e.mousePosition), out hover);
        if (hasHover && snapGrid)
        { hover = TownPlacementTools.Snap(hover, grid); if (TownPlacementTools.GroundAt(hover, out var grounded)) hover.y = grounded.y; }
        if (e.type == EventType.MouseMove) { view.Repaint(); Repaint(); }
        if (hasHover && e.type == EventType.Repaint) DrawGhost(view);
        if (e.type == EventType.MouseDown && e.button == 0 && hasHover)
        {
            if (mode == PaintMode.Line && !lineStarted) { lineStart = hover; lineStarted = true; e.Use(); return; }
            StartStroke(); previousPaint = hover;
            if (mode == PaintMode.Line) { PaintLine(hover); lineStarted = false; EndStroke(); }
            else if (mode == PaintMode.MoveProperty)
            {
                if (moving != null) { if (TownGameplayAuthoring.MoveProperty(moving, hover, out var why)) status = "Property moved; Save & bake to refresh routes."; else status = why; }
                mode = PaintMode.Select; EndStroke();
            }
            else Paint(hover);
            e.Use(); Changed();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0 && stroke && hasHover &&
            (mode == PaintMode.Brush || mode == PaintMode.Erase) && Vector3.Distance(hover, previousPaint) > Mathf.Max(.5f, spacing * .4f))
        { previousPaint = hover; Paint(hover); e.Use(); Changed(); }
        else if (e.type == EventType.MouseUp && e.button == 0 && stroke) { EndStroke(); e.Use(); }
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(58, 48, Mathf.Min(380, view.position.width - 70), 58), EditorStyles.helpBox);
        GUILayout.Label("TOWN BUILDER  ·  " + mode + (Active != null ? "  ·  " + Active.displayName : ""), EditorStyles.boldLabel);
        GUILayout.Label(!hasHover ? "Point at dry ground. Alt / right mouse keeps camera navigation." : previewError ?? (TownPlacementTools.IsProperty(Active) ? "Street attached. Click to validate the whole property's clearance and place." : "Click to place. Q/E rotate · Esc cancels · Undo reverses the whole stroke."), EditorStyles.wordWrappedMiniLabel);
        GUILayout.EndArea(); Handles.EndGUI();
    }
    void StartStroke()
    { EndStroke(); Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Town " + mode); stroke = true; placedInStroke = 0; }
    void EndStroke() { if (!stroke) return; Undo.CollapseUndoOperations(undoGroup); stroke = false; }
    void Paint(Vector3 at)
    {
        if (mode == PaintMode.Erase) { int n = TownPlacementTools.Erase(at, radius, eraseProperties); status = "Removed " + n + " authored objects. Undo restores them."; return; }
        if (mode == PaintMode.Stamp) { Place(Active, at, scale, yaw, true); return; }
        var pool = palette.entries.Where(x => x != null && !TownPlacementTools.IsProperty(x) && (mixIds.Count > 0 ? mixIds.Contains(x.id) : x.id == activeId)).ToArray();
        if (pool.Length == 0) { status = "Brush accepts scenery. Use Stamp or Line for working properties."; return; }
        int done = 0;
        for (int attempt = 0; attempt < density * 6 && done < density && placedInStroke < 500; attempt++)
        {
            var entry = pool[random.Next(pool.Length)];
            float angle = (float)random.NextDouble() * Mathf.PI * 2, distance = Mathf.Sqrt((float)random.NextDouble()) * radius;
            var p = at + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * distance;
            if (snapGrid) p = TownPlacementTools.Snap(p, grid);
            if (!TownPlacementTools.GroundAt(p, out p)) continue;
            var root = TownPlacementTools.Root();
            if (root != null && root.GetComponentsInChildren<TownPlacedAsset>().Any(x => Vector3.Distance(x.transform.position, p) < spacing)) continue;
            float s = scale * Mathf.Lerp(1 - variation, 1 + variation, (float)random.NextDouble());
            if (Place(entry, p, s, randomYaw ? (float)random.NextDouble() * 360 : yaw, false)) done++;
        }
        status = "Painted " + placedInStroke + " objects this stroke. Blocked positions were skipped.";
    }
    void PaintLine(Vector3 end)
    {
        var entry = Active; if (entry == null) return;
        var dimensions = TownPlacementTools.PrefabBounds(entry.prefab).size;
        float angle = TownPlacementTools.LineYaw(lineStart, end, dimensions, yaw);
        foreach (var p in TownPlacementTools.LinePoints(lineStart, end, spacing))
            if (TownPlacementTools.GroundAt(p, out var ground)) Place(entry, ground, scale, angle, false);
        status = "Placed " + placedInStroke + " objects along the line. Blocked positions were skipped.";
    }
    bool Place(TownAssetEntry entry, Vector3 at, float size, float rotation, bool select)
    {
        try
        {
            bool boundaryJoin = mode == PaintMode.Line && entry != null && entry.category == "Ground & boundaries";
            var go = TownPlacementTools.Place(entry, at, Quaternion.Euler(0, rotation, 0), size, allowOverlap || boundaryJoin, out var error);
            if (go == null) { status = error; return false; }
            placedInStroke++; if (select) Selection.activeGameObject = go;
            status = "Placed " + entry.displayName + ". Save & bake when ready."; return true;
        }
        catch (Exception ex) { status = ex.Message; return false; }
    }
    void DrawGhost(SceneView view)
    {
        Handles.color = mode == PaintMode.Erase ? new Color(1, .3f, .25f) : new Color(.3f, 1, .65f);
        if (mode == PaintMode.Brush || mode == PaintMode.Erase) { Handles.DrawWireDisc(hover + Vector3.up * .1f, Vector3.up, radius); previewError = null; return; }
        if (lineStarted) { Handles.DrawAAPolyLine(4, lineStart + Vector3.up * .1f, hover + Vector3.up * .1f); Handles.DrawWireDisc(lineStart, Vector3.up, .8f); }
        var entry = Active; if (entry == null || entry.prefab == null) return;
        var bounds = TownPlacementTools.PrefabBounds(entry.prefab);
        float previewYaw = mode == PaintMode.Line && lineStarted && !TownPlacementTools.IsProperty(entry)
            ? TownPlacementTools.LineYaw(lineStart, hover, bounds.size, yaw) : yaw;
        if (EditorApplication.timeSinceStartup > previewAt)
        { previewAt = EditorApplication.timeSinceStartup + .12; previewError = TownPlacementTools.Check(entry, hover, Quaternion.Euler(0, previewYaw, 0), scale, allowOverlap || (mode == PaintMode.Line && entry.category == "Ground & boundaries")); }
        var at = hover; var rotation = Quaternion.Euler(0, previewYaw, 0); float size = scale;
        var meshRoot = entry.prefab.transform;
        Matrix4x4 basis;
        if (TownPlacementTools.IsProperty(entry))
        {
            if (!TownGameplayAuthoring.PreviewProperty(entry, hover, out var pose, out var propertyBounds, out var why)) { previewError = why; return; }
            Handles.DrawDottedLine(hover, pose.position, 4); Handles.DrawWireDisc(pose.position, Vector3.up, 1.3f);
            Handles.Label(pose.position + Vector3.up * 2, "CURB PICKUP");
            at = pose.position; rotation = pose.rotation; size = 1;
            if (entry.role == TownAssetRole.House)
            {
                meshRoot = TownGameplayAuthoring.FindHouseTemplate(TownPlacementTools.Scene(), entry.houseModel).transform;
                basis = Matrix4x4.TRS(at, rotation, Vector3.one) * meshRoot.worldToLocalMatrix;
            }
            else
            {
                var correction = Quaternion.Euler(0, entry.propertyFrontYaw, 0);
                var corrected = new Bounds(); bool first = true;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    var p = correction * new Vector3(x == 0 ? bounds.min.x : bounds.max.x, y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z);
                    if (first) { corrected = new Bounds(p, Vector3.zero); first = false; } else corrected.Encapsulate(p);
                }
                var offset = new Vector3(-corrected.center.x, -corrected.min.y, -4 - corrected.max.z);
                basis = Matrix4x4.TRS(at, rotation, Vector3.one) * Matrix4x4.TRS(offset, correction, Vector3.one) * meshRoot.worldToLocalMatrix;
            }
            bounds = propertyBounds;
        }
        else basis = Matrix4x4.TRS(at, rotation, Vector3.one * size) * Matrix4x4.Translate(-new Vector3(bounds.center.x, bounds.min.y, bounds.center.z)) * meshRoot.worldToLocalMatrix;
        Handles.color = previewError == null ? new Color(.3f, 1, .65f) : new Color(1, .3f, .25f);
        var m = Matrix4x4.TRS(at, rotation, Vector3.one * size);
        using (new Handles.DrawingScope(m)) Handles.DrawWireCube(TownPlacementTools.IsProperty(entry) ? bounds.center : Vector3.up * bounds.extents.y, bounds.size);
        if (ghost == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return;
            ghost = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            ghost.SetFloat("_Surface", 1); ghost.SetFloat("_SrcBlend", 5); ghost.SetFloat("_DstBlend", 10);
            ghost.SetFloat("_ZWrite", 0); ghost.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); ghost.renderQueue = 3000;
        }
        ghost.SetColor("_BaseColor", previewError == null ? new Color(.28f, .9f, .6f, .35f) : new Color(1, .2f, .2f, .3f));
        foreach (var filter in meshRoot.GetComponentsInChildren<MeshFilter>(true))
            if (ShouldDrawGhostMesh(filter))
                for (int sub = 0; sub < filter.sharedMesh.subMeshCount; sub++)
                    Graphics.DrawMesh(filter.sharedMesh, basis * filter.transform.localToWorldMatrix, ghost, 0, view.camera, sub,
                        null, UnityEngine.Rendering.ShadowCastingMode.Off, false);
    }
    /// <summary>Match the property recipe's visible geometry: shared neighbourhood yards are regenerated
    /// after publication, not copied with this house. Hidden collision-only renderers are not previews.</summary>
    public static bool ShouldDrawGhostMesh(MeshFilter filter)
    {
        if (filter == null || filter.sharedMesh == null || filter.GetComponentInParent<CoastalYardBoundary>() != null) return false;
        var renderer = filter.GetComponent<Renderer>();
        return renderer != null && renderer.enabled;
    }
    void FrameTown(bool top)
    {
        if (!TownPlacementTools.Scene().isLoaded) { status = "Open town first."; return; }
        var view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
        ConfigureMapView(view);
        var b = CoastalCityLayout.BoundsXZ;
        view.LookAt(new Vector3(b.center.x, 0, b.center.y), Quaternion.Euler(top ? 90 : 55, 0, 0), Mathf.Max(b.width, b.height) * .53f, top);
        view.Focus();
    }
    void FrameDepot()
    {
        var view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
        ConfigureMapView(view);
        view.LookAt(CoastalCityLayout.DepotDoor(0) + Vector3.right * 45, Quaternion.Euler(50, 60, 0), 85, false); view.Focus();
    }
    static void ConfigureMapView(SceneView view)
    {
        // Authoring visibility only: leave the actual scene/game's fog, lights and camera untouched.
        view.sceneViewState.showFog = false; view.sceneViewState.showSkybox = false;
        view.sceneLighting = false; view.drawGizmos = false;
    }
    void Try(Action action) { try { action(); } catch (Exception e) { status = e.Message; Debug.LogException(e); } Repaint(); }
}

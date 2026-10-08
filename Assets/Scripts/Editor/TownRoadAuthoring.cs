using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Undoable, unpublished road data. Editing a handle never changes the game or writes its blueprint.</summary>
public sealed class TownRoadDraft : ScriptableObject
{
    [TextArea] public string json;
    [TextArea] public string baseline;
    public string sourceHash;
    public List<Vector3> points = new List<Vector3>();
    public string streetName = "New Street";
    public int streetDistrict;
    public float streetWidth = 10f;
    public bool streetCurve, streetService, streetBridge;
}

/// <summary>Road tab embedded by TownEditorWindow. Scene callbacks are deliberately owned by the host window.</summary>
[Serializable]
public sealed class TownRoadAuthoring : IDisposable
{
    [SerializeField] TownRoadDraft draft;
    [SerializeField] int mode;
    [SerializeField] string selectedRoad;
    [SerializeField] string selectedNode;
    [SerializeField] float grid = 1f;
    [SerializeField] float snapRadius = 7f;
    [SerializeField] string status;
    [SerializeField] bool error;
    [SerializeField] Vector2 scroll;
    [NonSerialized] string cachedJson;
    [NonSerialized] CoastalCityLayout.Blueprint cached;
    [NonSerialized] bool undoSubscribed;
    public Func<string> PublishOverride;
    public Action<string> Published;
    public bool IsActive => mode != 0;
    public bool HasChanges { get { Ensure(); return TownRoadDraftOperations.HasDraftChanges(draft); } }
    public bool HasBlueprintChanges { get { Ensure(); return draft.json != draft.baseline; } }
    public bool HasPendingDrawing { get { Ensure(); return draft.points.Count > 0; } }
    public TownRoadDraft Draft { get { Ensure(); return draft; } }
    static string RecoveryKey => "Rubbish.TownEditor.RoadDraft." + Hash128.Compute(Application.dataPath);

    CoastalCityLayout.Blueprint Data
    {
        get
        {
            Ensure();
            if (cached == null || cachedJson != draft.json)
            { cached = JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(draft.json); cachedJson = draft.json; }
            return cached;
        }
    }

    void Ensure()
    {
        if (!undoSubscribed) { Undo.undoRedoPerformed += AfterUndo; undoSubscribed = true; }
        if (draft != null) return;
        draft = ScriptableObject.CreateInstance<TownRoadDraft>();
        draft.name = "Town road draft"; draft.hideFlags = HideFlags.HideAndDontSave;
        string recovery = EditorPrefs.GetString(RecoveryKey, "");
        if (!string.IsNullOrEmpty(recovery))
        {
            try
            {
                JsonUtility.FromJsonOverwrite(recovery, draft);
                var recovered = JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(draft.json);
                if (recovered == null || string.IsNullOrEmpty(draft.baseline) || string.IsNullOrEmpty(draft.sourceHash))
                    throw new InvalidOperationException("Incomplete road draft recovery.");
                if (draft.points == null) draft.points = new List<Vector3>();
                selectedRoad = recovered.roads[0].id;
                if (draft.points.Count > 0) mode = 2;
                SetStatus("Recovered your unpublished road draft. Closing this window or reloading scripts does not discard it. Publish still checks for external blueprint changes.", false);
                return;
            }
            catch (Exception e) { Debug.LogWarning("Could not restore the town road draft: " + e.Message); }
        }
        ReloadFromDisk();
    }

    public void ReloadFromDisk()
    {
        if (draft == null) { Ensure(); return; }
        string source = File.ReadAllText(CoastalCityLayout.BlueprintPath);
        var blueprint = JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(source);
        var errors = CoastalCityValidation.ValidateBlueprintTopology(blueprint);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        Undo.ClearUndo(draft);
        draft.json = draft.baseline = JsonConvert.SerializeObject(blueprint, Formatting.Indented);
        draft.sourceHash = Hash128.Compute(source).ToString(); draft.points.Clear();
        cached = null; selectedNode = null;
        if (!blueprint.roads.Any(r => r.id == selectedRoad)) selectedRoad = blueprint.roads[0].id;
        SaveDraft();
        SetStatus("Road draft matches the published city.", false);
    }

    public void OnGUI()
    {
        using (var view = new EditorGUILayout.ScrollViewScope(scroll))
        { scroll = view.scrollPosition; DrawGUI(); }
    }

    void DrawGUI()
    {
        try { Ensure(); var unused = Data; }
        catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); return; }
        EditorGUILayout.LabelField("Connected streets", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Move junctions or draw streets in Scene view. Houses, sidewalks, navigation, gates and the map follow the published roads. Changes remain a draft until Publish.", MessageType.Info);
        mode = GUILayout.Toolbar(mode, new[] { "Inspect", "Move junctions", "Draw street" });
        grid = EditorGUILayout.Slider("Ground snap (m)", grid, .25f, 5f);
        snapRadius = EditorGUILayout.Slider("Road/junction snap (m)", snapRadius, 2f, 12f);
        var b = Data;
        if (mode == 2)
        {
            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.TextField("Street name", draft.streetName);
            int district = EditorGUILayout.Popup("District", Mathf.Clamp(draft.streetDistrict, 0, b.districts.Length - 1), b.districts.Select(x => DistrictLabel(x.id)).ToArray());
            float width = EditorGUILayout.Slider("Road width (m)", draft.streetWidth, 4f, 20f);
            bool curve = EditorGUILayout.Toggle("Smooth bends", draft.streetCurve);
            bool service = EditorGUILayout.Toggle("Service street", draft.streetService);
            bool bridge = EditorGUILayout.Toggle("Bridge", draft.streetBridge);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(draft, "Edit street drawing settings");
                draft.streetName = name; draft.streetDistrict = district; draft.streetWidth = width;
                draft.streetCurve = curve; draft.streetService = service; draft.streetBridge = bridge;
                EditorUtility.SetDirty(draft); SaveDraft();
            }
            EditorGUILayout.HelpBox("Click connected road or junction, then click your route. Click a road to join it; crossings create shared junctions automatically. New streets stay within one district: cross-district shortcuts need separately authored permit gates. Enter finishes; Backspace removes the last point; Esc cancels. Alt/right mouse still moves the camera.", MessageType.None);
            EditorGUILayout.LabelField(draft.points.Count + " route point(s)");
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(draft.points.Count < 2))
                    if (GUILayout.Button("Finish street (Enter)")) FinishRoad();
                using (new EditorGUI.DisabledScope(draft.points.Count == 0))
                    if (GUILayout.Button("Cancel drawing")) ClearDrawing();
            }
        }
        else
        {
            int selected = Array.FindIndex(b.roads, r => r.id == selectedRoad);
            int next = EditorGUILayout.Popup("Road", Mathf.Max(0, selected), b.roads.Select(x => x.name + "  [" + x.id + "]").ToArray());
            if (next != selected) { selectedRoad = b.roads[next].id; SceneView.RepaintAll(); }
            var road = b.roads[Mathf.Clamp(next, 0, b.roads.Length - 1)];
            EditorGUI.BeginChangeCheck();
            string label = EditorGUILayout.TextField("Street name", road.name);
            float width = EditorGUILayout.Slider("Road width (m)", road.halfWidth * 2f, 4f, 20f);
            bool curved = EditorGUILayout.Toggle("Smooth bends", road.curve);
            bool service = EditorGUILayout.Toggle("Service street", road.service);
            if (EditorGUI.EndChangeCheck())
            {
                var edited = TownRoadDraftOperations.Clone(b); var target = Array.Find(edited.roads, x => x.id == road.id);
                target.name = label; target.halfWidth = width * .5f; target.curve = curved; target.service = service;
                Store(edited, "Edit street settings");
            }
            if (GUILayout.Button("Frame selected road"))
            {
                var resolved = CoastalCityLayout.ResolveRoad(Data, selectedRoad);
                var bounds = new Bounds(resolved.points[0], Vector3.one * 15f);
                foreach (var point in resolved.points) bounds.Encapsulate(point);
                SceneView.lastActiveSceneView?.Frame(bounds, false);
            }
            if (mode == 1) EditorGUILayout.HelpBox("Select a junction dot, then drag its position handle. Connected streets share the same junction; existing collection identities are never renamed. River/coast points cannot be moved here.", MessageType.None);
            if (!string.IsNullOrEmpty(selectedNode)) EditorGUILayout.LabelField("Junction", selectedNode);
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(HasChanges ? "Unpublished road changes" : "No unpublished road changes", EditorStyles.boldLabel);
        if (HasPendingDrawing) EditorGUILayout.HelpBox("Finish this street with Enter, or Cancel drawing, before publishing. Its points and street settings are saved for recovery.", MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Undo")) { Undo.PerformUndo(); SceneView.RepaintAll(); }
            if (GUILayout.Button("Redo")) { Undo.PerformRedo(); SceneView.RepaintAll(); }
            if (GUILayout.Button("Reload published"))
                if (!HasChanges || EditorUtility.DisplayDialog("Discard road draft?", "Only unpublished road edits will be discarded. Published streets and pasted objects are unchanged.", "Discard draft", "Keep editing"))
                    Try(ReloadFromDisk);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Validate draft")) Try(() =>
            {
                var errors = TownRoadDraftOperations.Validate(Data);
                SetStatus(errors.Count == 0 ? "Blueprint topology passes. Publish also checks physical clearance and updates all derived assets." : string.Join("\n", errors), errors.Count != 0);
            });
            using (new EditorGUI.DisabledScope(!HasBlueprintChanges || HasPendingDrawing || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Publish road changes")) Try(Publish);
        }
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, error ? MessageType.Error : MessageType.Info);
    }

    public void OnSceneGUI(SceneView view)
    {
        if (draft == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
        CoastalCityLayout.Blueprint b;
        try { b = Data; } catch { return; }
        var evt = Event.current;
        foreach (var road in b.roads)
        {
            var resolved = CoastalCityLayout.ResolveRoad(b, road.id);
            Handles.color = road.id == selectedRoad ? new Color(.2f, .95f, 1f, .95f) : new Color(.35f, .8f, .95f, .34f);
            Handles.DrawAAPolyLine(road.id == selectedRoad ? 5f : 2f, resolved.points.Select(p => p + Vector3.up * .22f).ToArray());
        }
        if (mode == 0) return;
        if (mode == 1)
        {
            var editable = TownRoadDraftOperations.EditableJunctions(b);
            foreach (var node in b.nodes)
            {
                if (!editable.Contains(node.id)) continue;
                Vector3 pos = new Vector3(node.x * b.scale, .25f, node.z * b.scale);
                Handles.color = node.id == selectedNode ? Color.yellow : Color.cyan;
                float size = HandleUtility.GetHandleSize(pos) * .045f;
                if (Handles.Button(pos, Quaternion.identity, size, size * 1.6f, Handles.DotHandleCap))
                { selectedNode = node.id; selectedRoad = b.roads.First(r => r.nodes.Contains(node.id)).id; view.Repaint(); }
            }
            var selected = Array.Find(b.nodes, n => n.id == selectedNode);
            if (selected != null && editable.Contains(selected.id))
            {
                var position = new Vector3(selected.x * b.scale, 0, selected.z * b.scale);
                EditorGUI.BeginChangeCheck();
                var moved = Handles.PositionHandle(position, Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) Try(() => Store(TownRoadDraftOperations.MoveJunction(b, selected.id, GroundSnap(moved)), "Move town junction"));
                Handles.Label(position + Vector3.up * 2f, selectedNode);
            }
            return;
        }
        int control = GUIUtility.GetControlID("TownRoadDraw".GetHashCode(), FocusType.Passive);
        if (evt.type == EventType.Layout && !evt.alt) HandleUtility.AddDefaultControl(control);
        bool hit = new Plane(Vector3.up, Vector3.zero).Raycast(HandleUtility.GUIPointToWorldRay(evt.mousePosition), out float enter);
        Vector3 cursor = hit ? GroundSnap(HandleUtility.GUIPointToWorldRay(evt.mousePosition).GetPoint(enter)) : Vector3.zero;
        if (hit) cursor = TownRoadDraftOperations.SnapPoint(b, cursor, snapRadius, out string unused);
        Handles.color = new Color(1f, .8f, .16f);
        var line = new List<Vector3>(draft.points);
        if (hit) line.Add(cursor);
        if (line.Count >= 2) Handles.DrawAAPolyLine(6f, line.Select(p => p + Vector3.up * .35f).ToArray());
        foreach (var point in draft.points) Handles.DrawWireDisc(point + Vector3.up * .3f, Vector3.up, .8f);
        if (hit)
        {
            Handles.DrawWireDisc(cursor + Vector3.up * .3f, Vector3.up, draft.streetWidth * .5f);
            Handles.Label(cursor + Vector3.up * 2f, "Click road point · Enter finish");
        }
        if (!evt.alt && evt.type == EventType.MouseDown && evt.button == 0 && hit)
        {
            if (draft.points.Count == 0 || Vector3.Distance(draft.points[draft.points.Count - 1], cursor) > 1f)
            { Undo.RecordObject(draft, "Add road drawing point"); draft.points.Add(cursor); EditorUtility.SetDirty(draft); SaveDraft(); }
            if (evt.clickCount > 1 && draft.points.Count >= 2) FinishRoad();
            evt.Use();
        }
        if (evt.type == EventType.KeyDown && !evt.alt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { FinishRoad(); evt.Use(); }
            else if (evt.keyCode == KeyCode.Escape) { ClearDrawing(); evt.Use(); }
            else if (evt.keyCode == KeyCode.Backspace && draft.points.Count > 0)
            { Undo.RecordObject(draft, "Remove road drawing point"); draft.points.RemoveAt(draft.points.Count - 1); EditorUtility.SetDirty(draft); SaveDraft(); evt.Use(); }
        }
        if (evt.type == EventType.MouseMove) view.Repaint();
    }

    public void Publish()
    {
        Ensure();
        TownRoadDraftOperations.RequireFinishedStroke(draft);
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before publishing roads.");
        var scene = SceneManager.GetSceneByPath(NeighborhoodBuilder.DefaultScenePath);
        if (!scene.IsValid() || !scene.isLoaded || !scene.GetRootGameObjects().Any(g => g.name == "NeighborhoodGen"))
            throw new InvalidOperationException("Open the Neighborhood scene before publishing road changes. Nothing was written.");
        var errors = TownRoadDraftOperations.Validate(Data);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        string before = File.ReadAllText(CoastalCityLayout.BlueprintPath);
        if (Hash128.Compute(before).ToString() != draft.sourceHash)
            throw new InvalidOperationException("The city blueprint changed outside this draft. Reload published roads before editing again; publishing was cancelled to avoid overwriting another edit.");
        string published;
        try
        {
            WriteBlueprint(draft.json + "\n");
            published = PublishOverride != null ? PublishOverride() : NeighborhoodBuilder.ApplyCoastalChanges();
        }
        catch
        {
            WriteBlueprint(before);
            throw;
        }
        ReloadFromDisk(); SetStatus(published, false); Published?.Invoke(published);
        SceneView.RepaintAll();
    }

    static void WriteBlueprint(string json)
    {
        // Same-directory atomic replacement avoids leaving a truncated JSON asset on an interrupted write.
        string path = CoastalCityLayout.BlueprintPath;
        string temporary = path + ".town-draft-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Replace(temporary, path, null);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            CoastalCityLayout.Reload(); Districts.RefreshLayoutBounds();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    void FinishRoad() => Try(() =>
    {
        if (draft.points.Count < 2) throw new InvalidOperationException("Click at least two road points first.");
        string id = "painted-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        var changed = TownRoadDraftOperations.AddRoad(Data, new CoastalCityLayout.RoadSpec
        {
            id = id, name = string.IsNullOrWhiteSpace(draft.streetName) ? "New Street" : draft.streetName.Trim(),
            districtId = Data.districts[Mathf.Clamp(draft.streetDistrict, 0, Data.districts.Length - 1)].id,
            halfWidth = draft.streetWidth * .5f, curve = draft.streetCurve, service = draft.streetService, bridge = draft.streetBridge
        }, draft.points.ToArray(), snapRadius);
        Store(changed, "Draw connected street"); draft.points.Clear(); selectedRoad = id; SaveDraft();
        SetStatus("Street added to the draft. Its shared junctions connect to the driving graph. Publish checks buildings, sidewalks and collection access before saving.", false);
    });
    void ClearDrawing() { Undo.RecordObject(draft, "Cancel street drawing"); draft.points.Clear(); EditorUtility.SetDirty(draft); SaveDraft(); SceneView.RepaintAll(); }
    void Store(CoastalCityLayout.Blueprint b, string operation)
    {
        Undo.RecordObject(draft, operation); draft.json = JsonConvert.SerializeObject(b, Formatting.Indented);
        EditorUtility.SetDirty(draft); cached = null; SaveDraft(); SceneView.RepaintAll();
    }
    Vector3 GroundSnap(Vector3 p) => new Vector3(Mathf.Round(p.x / grid) * grid, 0, Mathf.Round(p.z / grid) * grid);
    void Try(Action action) { try { action(); } catch (Exception e) { SetStatus(e.Message, true); } }
    void SetStatus(string message, bool isError) { status = message; error = isError; }
    static string DistrictLabel(string id) => ObjectNames.NicifyVariableName(id.Replace('_', ' '));
    /// <summary>Per-project editor recovery, not a shipped game asset or a player save.</summary>
    public void SaveDraft()
    {
        if (draft == null) return;
        if (HasChanges || HasPendingDrawing) EditorPrefs.SetString(RecoveryKey, JsonUtility.ToJson(draft));
        else EditorPrefs.DeleteKey(RecoveryKey);
    }
    void AfterUndo() { cached = null; SaveDraft(); SceneView.RepaintAll(); }
    public void Dispose()
    {
        SaveDraft();
        if (undoSubscribed) Undo.undoRedoPerformed -= AfterUndo;
        undoSubscribed = false;
        if (draft != null) Object.DestroyImmediate(draft);
        draft = null; cached = null;
    }
}

/// <summary>Pure road-draft geometry operations, independently testable without scene or asset writes.</summary>
public static class TownRoadDraftOperations
{
    sealed class Split { public string road, node; public int segment; public float t; }
    struct Segment { public Vector3 a, b; public int control; public float t0, t1; }
    public static CoastalCityLayout.Blueprint Clone(CoastalCityLayout.Blueprint b) => JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(JsonConvert.SerializeObject(b));
    public static bool HasDraftChanges(TownRoadDraft draft) => draft != null &&
        (draft.json != draft.baseline || draft.points != null && draft.points.Count > 0);
    public static void RequireFinishedStroke(TownRoadDraft draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        if (draft.points != null && draft.points.Count > 0)
            throw new InvalidOperationException("Finish the unfinished street with Enter, or Cancel drawing, before publishing. Nothing was written and its drawing points are preserved.");
    }
    public static HashSet<string> EditableJunctions(CoastalCityLayout.Blueprint b)
    {
        var result = new HashSet<string>(b.roads.SelectMany(r => r.nodes), StringComparer.Ordinal);
        if (b.terrain != null)
            foreach (var path in new[] { b.terrain.river, b.terrain.coast, b.terrain.riverWalk })
                foreach (var id in path ?? Array.Empty<string>()) result.Remove(id);
        return result;
    }
    public static CoastalCityLayout.Blueprint MoveJunction(CoastalCityLayout.Blueprint source, string id, Vector3 world)
    {
        if (!EditableJunctions(source).Contains(id)) throw new ArgumentException("Select a road junction, not a terrain/river point.");
        CheckPoint(source, world);
        var b = Clone(source); var n = Array.Find(b.nodes, x => x.id == id);
        n.x = world.x / b.scale; n.z = world.z / b.scale;
        var errors = Validate(b);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return b;
    }
    public static List<string> Validate(CoastalCityLayout.Blueprint b)
    {
        var errors = CoastalCityValidation.ValidateBlueprintTopology(b);
        if (errors.Count != 0) return errors;
        foreach (var road in b.roads)
        {
            if (string.IsNullOrWhiteSpace(road.name)) errors.Add(road.id + ": give the street a name.");
            if (road.halfWidth > 10f) errors.Add(road.id + ": road width exceeds the supported 20 m authoring range.");
            foreach (var point in CoastalCityLayout.ResolveRoad(b, road.id).points)
                if (!Inside(b, point)) { errors.Add(road.id + ": road leaves the town boundary; expand the terrain blueprint first."); break; }
        }
        return errors;
    }
    public static Vector3 SnapPoint(CoastalCityLayout.Blueprint b, Vector3 world, float radius, out string nodeId)
    {
        world.y = 0;
        var roadNodes = new HashSet<string>(b.roads.SelectMany(r => r.nodes), StringComparer.Ordinal);
        var nearest = b.nodes.Where(n => roadNodes.Contains(n.id)).OrderBy(n => (Point(b, n) - world).sqrMagnitude).FirstOrDefault();
        nodeId = null;
        if (nearest != null && Vector3.Distance(Point(b, nearest), world) <= radius)
        { nodeId = nearest.id; return Point(b, nearest); }
        float distance = radius;
        var best = world; best.y = 0;
        foreach (var road in b.roads)
            foreach (var segment in Segments(b, road))
            {
                Vector3 p = Closest(world, segment.a, segment.b, out float unused);
                float d = Vector3.Distance(p, world);
                if (d < distance) { distance = d; best = p; }
            }
        return best;
    }
    public static CoastalCityLayout.Blueprint AddRoad(CoastalCityLayout.Blueprint source, CoastalCityLayout.RoadSpec definition, Vector3[] points, float snapRadius)
    {
        if (definition == null || string.IsNullOrEmpty(definition.id) || source.roads.Any(r => r.id == definition.id)) throw new ArgumentException("The new street needs a unique, stable road ID.");
        if (points == null || points.Length < 2) throw new ArgumentException("A street needs at least two points.");
        var b = Clone(source); var nodes = b.nodes.ToList(); var pending = new List<Split>();
        var ids = new List<string>();
        foreach (var p in points)
        {
            CheckPoint(b, p);
            var snapped = SnapPoint(b, p, snapRadius, out string id);
            if (id == null)
            {
                id = FindOrCreate(b, nodes, snapped);
                ScheduleNearbySplit(b, snapped, id, pending);
            }
            if (ids.Count == 0 || ids[ids.Count - 1] != id) ids.Add(id);
        }
        if (ids.Count < 2) throw new ArgumentException("The route collapsed onto one junction. Draw at least a few metres of street.");
        b.nodes = nodes.ToArray();
        var added = new CoastalCityLayout.RoadSpec { id = definition.id, name = definition.name, districtId = definition.districtId,
            halfWidth = definition.halfWidth, service = definition.service, bridge = definition.bridge, curve = definition.curve, nodes = ids.ToArray() };
        b.roads = b.roads.Concat(new[] { added }).ToArray();
        // Intersections are actual shared IDs in BOTH polylines, not merely overlapping asphalt.
        var newSegments = Segments(b, added);
        foreach (var road in b.roads.Where(r => r != added))
            foreach (var existing in Segments(b, road))
                foreach (var drawn in newSegments)
                {
                    if (CollinearOverlap(drawn.a, drawn.b, existing.a, existing.b) > 1f)
                        throw new InvalidOperationException("The new street overlaps " + road.name + ". Branch off its centerline instead of tracing a second street over existing asphalt.");
                    if (Intersection(drawn.a, drawn.b, existing.a, existing.b, out float a, out float c))
                    {
                        Vector3 crossing = Vector3.Lerp(drawn.a, drawn.b, a);
                        string id = FindOrCreate(b, nodes, crossing);
                        pending.Add(new Split { road = road.id, node = id, segment = existing.control, t = Mathf.Lerp(existing.t0, existing.t1, c) });
                        pending.Add(new Split { road = added.id, node = id, segment = drawn.control, t = Mathf.Lerp(drawn.t0, drawn.t1, a) });
                    }
                }
        PreserveSplitCurves(b, nodes, pending, added);
        b.nodes = nodes.ToArray();
        foreach (var road in b.roads)
        {
            var splits = pending.Where(p => p.road == road.id).ToArray();
            var joined = new List<string> { road.nodes[0] };
            for (int i = 0; i < road.nodes.Length - 1; i++)
            {
                foreach (var split in splits.Where(s => s.segment == i && s.node != road.nodes[i] && s.node != road.nodes[i + 1]).OrderBy(s => s.t))
                    if (joined[joined.Count - 1] != split.node) joined.Add(split.node);
                if (joined[joined.Count - 1] != road.nodes[i + 1]) joined.Add(road.nodes[i + 1]);
            }
            road.nodes = joined.ToArray();
        }
        // Do not add an orphan cul-de-sac: at least one shared node must join the existing network.
        var oldRoadNodes = new HashSet<string>(b.roads.Where(r => r != added).SelectMany(r => r.nodes), StringComparer.Ordinal);
        if (!added.nodes.Any(oldRoadNodes.Contains)) throw new InvalidOperationException("This street is disconnected. Start/end on an existing road, or cross one so a junction can be created.");
        foreach (var node in added.nodes)
        {
            var incident = b.roads.Where(r => r != added && r.nodes.Contains(node)).ToArray();
            if (incident.Any(r => r.districtId != added.districtId) && !incident.Any(r => r.districtId == added.districtId))
                throw new InvalidOperationException("This street would create a cross-district shortcut around permit gates. Keep new streets connected within their selected district; a new district connection needs an explicitly authored gate and progression review.");
        }
        RejectGatedDetours(source, b, added);
        RejectGateReachabilityExpansion(source, b);
        var errors = Validate(b);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return b;
    }
    static void RejectGatedDetours(CoastalCityLayout.Blueprint source, CoastalCityLayout.Blueprint changed, CoastalCityLayout.RoadSpec added)
    {
        // Road district labels alone are insufficient: Home Run Road is labelled Acres on BOTH
        // sides of its Meadows gate. Be conservative about new attachments spanning any gated approach.
        // Group approaches by their unlock district so a Home-before -> Maple-after detour is blocked too.
        var nodes = changed.nodes.ToDictionary(n => n.id, n => Point(changed, n), StringComparer.Ordinal);
        foreach (var group in source.gates.GroupBy(g => g.districtId))
        {
            bool before = false, after = false;
            foreach (var gate in group)
            {
                var road = CoastalCityLayout.ResolveRoad(source, gate.roadId);
                var changedRoad = changed.roads.First(r => r.id == gate.roadId);
                foreach (string node in added.nodes.Where(changedRoad.nodes.Contains))
                {
                    float distance = float.MaxValue, station = 0, walked = 0;
                    for (int i = 1; i < road.points.Length; i++)
                    {
                        float segmentLength = Vector3.Distance(road.points[i - 1], road.points[i]);
                        var nearest = Closest(nodes[node], road.points[i - 1], road.points[i], out float t);
                        float d = Vector3.Distance(nearest, nodes[node]);
                        if (d < distance) { distance = d; station = walked + segmentLength * t; }
                        walked += segmentLength;
                    }
                    float gap = (station - gate.u * road.Length) * gate.forward;
                    if (Mathf.Abs(gap) < 8f)
                        throw new InvalidOperationException("This street joins too close to permit gate " + gate.id + ". Keep new junctions at least 8 m clear, or author and review the gate connection explicitly.");
                    before |= gap < 0; after |= gap > 0;
                }
            }
            if (before && after)
                throw new InvalidOperationException("This street connects opposite sides of a permit gate for " + group.Key + ", even though its road labels may match. A gate bypass needs explicit gate authoring and progression review.");
        }
    }
    static void RejectGateReachabilityExpansion(CoastalCityLayout.Blueprint source, CoastalCityLayout.Blueprint changed)
    {
        // Compare all existing junctions with every combination of district gates closed. This catches
        // multi-street detours too: a previously added dead-end is an existing junction on the next stroke.
        var gateDistricts = source.gates.Select(g => g.districtId).Distinct(StringComparer.Ordinal).ToArray();
        var oldJunctions = new HashSet<string>(source.roads.SelectMany(r => r.nodes), StringComparer.Ordinal);
        if (gateDistricts.Length > 8) throw new InvalidOperationException("More than eight gated districts need a dedicated progression review before adding streets.");
        for (int mask = 1; mask < (1 << gateDistricts.Length); mask++)
        {
            var locked = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < gateDistricts.Length; i++) if ((mask & (1 << i)) != 0) locked.Add(gateDistricts[i]);
            var before = ReachableWithGateCuts(source, locked);
            var after = ReachableWithGateCuts(changed, locked);
            string bypassed = after.FirstOrDefault(n => oldJunctions.Contains(n) && !before.Contains(n));
            if (bypassed != null)
                throw new InvalidOperationException("This street would bypass existing permit gates and make junction " + bypassed + " reachable while " + string.Join(", ", locked) + " is locked. Keep the connection on the same permitted side, or explicitly author and review new gates.");
        }
    }
    static HashSet<string> ReachableWithGateCuts(CoastalCityLayout.Blueprint b, HashSet<string> locked)
    {
        var adjacent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var road in b.roads)
        {
            foreach (string node in road.nodes)
                if (!adjacent.ContainsKey(node)) adjacent[node] = new HashSet<string>(StringComparer.Ordinal);
            // Arc distances of original control junctions, including the actual sampled curve geometry.
            var stations = new float[road.nodes.Length];
            foreach (var segment in Segments(b, road)) stations[segment.control + 1] += Vector3.Distance(segment.a, segment.b);
            for (int i = 1; i < stations.Length; i++) stations[i] += stations[i - 1];
            var cuts = b.gates.Where(g => g.roadId == road.id && locked.Contains(g.districtId))
                .Select(g => g.u * stations[stations.Length - 1]).ToArray();
            for (int i = 1; i < road.nodes.Length; i++)
            {
                if (cuts.Any(c => c >= stations[i - 1] - .0001f && c <= stations[i] + .0001f)) continue;
                adjacent[road.nodes[i - 1]].Add(road.nodes[i]); adjacent[road.nodes[i]].Add(road.nodes[i - 1]);
            }
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(); string start = b.roads.First(r => r.id == b.depot.roadId).nodes[0];
        queue.Enqueue(start); visited.Add(start);
        while (queue.Count > 0)
            foreach (string next in adjacent[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
        return visited;
    }
    static void ScheduleNearbySplit(CoastalCityLayout.Blueprint b, Vector3 point, string id, List<Split> splits)
    {
        foreach (var road in b.roads)
            foreach (var segment in Segments(b, road))
            {
                Vector3 closest = Closest(point, segment.a, segment.b, out float t);
                if (Vector3.Distance(closest, point) < .06f)
                    splits.Add(new Split { road = road.id, node = id, segment = segment.control, t = Mathf.Lerp(segment.t0, segment.t1, t) });
            }
    }
    static string FindOrCreate(CoastalCityLayout.Blueprint b, List<CoastalCityLayout.NodeSpec> nodes, Vector3 p, float tolerance = .05f)
    {
        var terrain = new HashSet<string>((b.terrain.river ?? Array.Empty<string>()).Concat(b.terrain.coast ?? Array.Empty<string>()).Concat(b.terrain.riverWalk ?? Array.Empty<string>()), StringComparer.Ordinal);
        var existing = nodes.FirstOrDefault(n => !terrain.Contains(n.id) && Vector3.Distance(Point(b, n), p) < tolerance);
        if (existing != null) return existing.id;
        string id = "junction-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        nodes.Add(new CoastalCityLayout.NodeSpec { id = id, x = p.x / b.scale, z = p.z / b.scale });
        return id;
    }
    static void PreserveSplitCurves(CoastalCityLayout.Blueprint b, List<CoastalCityLayout.NodeSpec> nodes, List<Split> pending, CoastalCityLayout.RoadSpec added)
    {
        // Resmoothing a cubic after inserting a control point shifts all normalized-u house anchors.
        // Preserve the exact already-published sampled centerline for affected existing curved roads.
        foreach (var road in b.roads.Where(r => r != added && r.curve && pending.Any(p => p.road == r.id)))
        {
            var samples = CoastalCityLayout.ResolveRoad(b, road.id).points;
            var ids = new List<string>();
            foreach (var sample in samples) ids.Add(FindOrCreate(b, nodes, sample, .0001f));
            foreach (var split in pending.Where(p => p.road == road.id))
            {
                var point = Point(b, nodes.First(n => n.id == split.node));
                float distance = float.MaxValue;
                for (int i = 0; i < samples.Length - 1; i++)
                {
                    float d = Vector3.Distance(point, Closest(point, samples[i], samples[i + 1], out float t));
                    if (d < distance) { distance = d; split.segment = i; split.t = t; }
                }
            }
            road.curve = false; road.nodes = ids.ToArray();
            b.nodes = nodes.ToArray();
        }
    }
    static List<Segment> Segments(CoastalCityLayout.Blueprint b, CoastalCityLayout.RoadSpec road)
    {
        var result = new List<Segment>(); var resolved = CoastalCityLayout.ResolveRoad(b, road.id).points; int sample = 0;
        var lookup = b.nodes.ToDictionary(n => n.id, n => Point(b, n), StringComparer.Ordinal);
        for (int i = 0; i < road.nodes.Length - 1; i++)
        {
            int steps = road.curve ? Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(lookup[road.nodes[i]], lookup[road.nodes[i + 1]]) / 6f)) : 1;
            for (int j = 0; j < steps; j++) result.Add(new Segment { a = resolved[sample], b = resolved[++sample], control = i, t0 = j / (float)steps, t1 = (j + 1f) / steps });
        }
        return result;
    }
    static Vector3 Point(CoastalCityLayout.Blueprint b, CoastalCityLayout.NodeSpec n) => new Vector3(n.x * b.scale, 0, n.z * b.scale);
    static bool Inside(CoastalCityLayout.Blueprint b, Vector3 p) => p.x >= b.bounds[0] * b.scale && p.x <= b.bounds[2] * b.scale && p.z >= b.bounds[1] * b.scale && p.z <= b.bounds[3] * b.scale;
    static void CheckPoint(CoastalCityLayout.Blueprint b, Vector3 p)
    {
        if (float.IsNaN(p.x) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.z) || !Inside(b, p))
            throw new ArgumentException("Keep road points inside the authored town boundary.");
    }
    static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, out float t)
    { p.y = 0; var d = b - a; t = Mathf.Clamp01(Vector3.Dot(p - a, d) / Mathf.Max(.00001f, d.sqrMagnitude)); return a + d * t; }
    static bool Intersection(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float t, out float u)
    {
        var x = b - a; var y = d - c; float cross = x.x * y.z - x.z * y.x;
        t = u = 0; if (Mathf.Abs(cross) < .00001f) return false;
        var v = c - a; t = (v.x * y.z - v.z * y.x) / cross; u = (v.x * x.z - v.z * x.x) / cross;
        return t >= -.0001f && t <= 1.0001f && u >= -.0001f && u <= 1.0001f;
    }
    static float CollinearOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 axis = b - a; float length = axis.magnitude;
        if (length < .001f) return 0;
        axis /= length;
        if (Vector3.Cross(c - a, axis).magnitude > .025f || Vector3.Cross(d - a, axis).magnitude > .025f) return 0;
        float c0 = Vector3.Dot(c - a, axis), c1 = Vector3.Dot(d - a, axis);
        return Mathf.Max(0, Mathf.Min(length, Mathf.Max(c0, c1)) - Mathf.Max(0, Mathf.Min(c0, c1)));
    }
}

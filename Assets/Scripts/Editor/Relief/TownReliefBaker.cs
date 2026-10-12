using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Terrain relief, as the last step of every town bake (Docs/CityAuthoring.md, "Terrain relief").
///
/// The whole layout pipeline (roads, lots, yards, infill, Town Life, navigation, validation) only understands a flat
/// town and stays that way. <see cref="PipelineBegin"/> puts the scene back exactly flat from the record the previous
/// bake left; <see cref="PipelineApply"/> then shapes the land (<see cref="TownReliefDesign"/>), builds the terrain
/// mesh and lays everything onto it: small things are lifted, flat slabs are tilted, anything long (verges, lawns,
/// fences, driveways, roads) is draped vertex by vertex, and world-space points stored on components and in the
/// street graph are lifted too. Depot Row is level by design, so nothing about the depots changes.
/// </summary>
public static class TownReliefBaker
{
    public const string EnabledMenuPath = "Rubbish/City/Terrain Relief Enabled";
    const string DisabledPref = "Rubbish.TownRelief.Disabled";
    public const string AssetDir = "Assets/Materials/Generated/Relief";
    public const string MeshPath = AssetDir + "/Relief_Meshes.asset", TerrainPath = AssetDir + "/Relief_Terrain.asset";
    const string MatDir = "Assets/Materials/Generated/Neighborhood";
    const int LayerGround = 6;
    const float Small = 2.5f, Level = 0.03f, PlaneFit = 0.02f, FlatThickness = 0.7f;
    const int ChunkCells = 48, DrapeVertexLimit = 6000;

    /// <summary>Emergency switch (per machine, default ON), like Town Life's: when off the next bake leaves the town
    /// flat (and writes a flat height grid), so a problem here can never keep the owner from baking and playing.</summary>
    public static bool Enabled
    {
        get { return !EditorPrefs.GetBool(DisabledPref, false); }
        set { if (value) EditorPrefs.DeleteKey(DisabledPref); else EditorPrefs.SetBool(DisabledPref, true); }
    }
    [MenuItem(EnabledMenuPath)]
    static void ToggleEnabled()
    {
        Enabled = !Enabled;
        Debug.Log(Enabled ? "[relief] enabled: the next town bake shapes the land." : "[relief] DISABLED: the next town bake leaves the town flat.");
    }
    [MenuItem(EnabledMenuPath, true)]
    static bool ToggleEnabledValidate() { Menu.SetChecked(EnabledMenuPath, Enabled); return true; }

    public static string LastReport { get; private set; }

    static readonly Dictionary<Mesh, TownLifeKit.MeshData> kitFlat = new Dictionary<Mesh, TownLifeKit.MeshData>();
    static readonly Dictionary<Mesh, string> kitDraped = new Dictionary<Mesh, string>();
    static bool active;

    static readonly Regex Vegetation = new Regex("tree|clump|wood|pine|birch|oak|elm|willow|maple|hedge|ridge|hill|mound|dune|bunting|light|grove|bush|topiary|flower|border|verge|tuft|reed|shrub|palm|cypress",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> Structural = new HashSet<string> { "CityRoadAnchor", "CoastalYardBoundary", "CoastalInfillSite", "CityGeneratedRoad",
        "CityBlueprintStamp", "TownReliefRecord", "WorldLifeAnchor", "StreetGraphGizmos", "NeighborhoodAtmosphere", "TrashRoute" };

    // ------------------------------------------------------------------------------------------- pipeline hooks

    /// <summary>HOOK 1: first thing inside the publish (before any anchor is applied or any collider is queried).
    /// From here until <see cref="PipelineApply"/> the town is flat and <see cref="TownRelief"/> answers 0.</summary>
    public static void PipelineBegin(Transform town)
    {
        active = true; LastReport = null;
        kitFlat.Clear(); kitDraped.Clear();
        TownLifeKit.Stored = OnKitStored; TownLifeKit.StoredUnchanged = KitUnchanged;
        var baked = TownRelief.Suspended ? null : TownRelief.Field;       // what the scene is standing on right now
        TownRelief.Suspended = true;
        if (town != null) Flatten(town, baked);
        Physics.SyncTransforms();
    }

    /// <summary>HOOK 3: always (finally). Leaves the runtime lookup reading whatever is on disk now.</summary>
    public static void PipelineEnd()
    {
        active = false; TownRelief.Suspended = false; TownRelief.Reload();
        TownLifeKit.Stored = null; TownLifeKit.StoredUnchanged = null;
        kitFlat.Clear(); kitDraped.Clear();
    }

    static void OnKitStored(Mesh mesh, TownLifeKit.MeshData data)
    {
        if (mesh == null || data == null) return;
        if (mesh.name.StartsWith(TownLifeKit.Prefix + "G_", StringComparison.Ordinal) || mesh.name.StartsWith(TownLifeKit.Prefix + "R_", StringComparison.Ordinal)) kitFlat[mesh] = data;
    }
    static bool KitUnchanged(Mesh mesh, TownLifeKit.MeshData data)
    {
        return kitDraped.TryGetValue(mesh, out string hash) && hash == HashOf(data);
    }
    static string HashOf(TownLifeKit.MeshData d)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Add(float f) { h = (h ^ (uint)BitConverter.SingleToInt32Bits(f)) * 1099511628211UL; }
            foreach (var v in d.vertices) { Add(v.x); Add(v.y); Add(v.z); }
            foreach (var v in d.normals) { Add(v.x); Add(v.y); Add(v.z); }
            foreach (var list in d.triangles) { h = (h ^ (uint)list.Count) * 1099511628211UL; foreach (int i in list) h = (h ^ (uint)i) * 1099511628211UL; }
            foreach (string m in d.materials) h = (h ^ (uint)(m ?? "").GetHashCode()) * 1099511628211UL;
            return h.ToString("x16") + ":" + d.vertices.Count;
        }
    }

    // ------------------------------------------------------------------------------------------------ flatten

    static bool Same(Vector3 a, Vector3 b) { return a == b || (a - b).sqrMagnitude < 1e-10f; }

    static void Flatten(Transform town, TownReliefField baked)
    {
        var record = town.GetComponent<TownReliefRecord>();
        var known = new HashSet<Transform>();
        if (record == null)
        {
            // A town that stands on relief can only be put back flat from its record. Without one, laying it out again
            // would lift everything a second time: stop before anything is touched.
            var ground = town.Find("Ground");
            if (ground != null) foreach (Transform child in ground)
                if (child.name.StartsWith("Relief terrain", StringComparison.Ordinal))
                    throw new InvalidOperationException("The town has terrain relief but its TownReliefRecord (on " + town.name +
                        ") is missing, so it cannot be laid out flat again. Restore Neighborhood.unity from version control, then bake.");
        }
        if (record != null)
        {
            for (int i = 0; i < record.kitMeshes.Count && i < record.kitFlatHashes.Count; i++)
                if (record.kitMeshes[i] != null) kitDraped[record.kitMeshes[i]] = record.kitFlatHashes[i];
            for (int i = 0; i < record.moved.Count; i++)
            {
                var t = record.moved[i]; if (t == null) continue;
                known.Add(t);
                if (Same(t.localPosition, record.bakedPosition[i]) && Quaternion.Angle(t.localRotation, record.bakedRotation[i]) < 0.01f)
                { t.localPosition = record.flatPosition[i]; t.localRotation = record.flatRotation[i]; }
                else if (baked != null)
                {
                    // moved by hand since the bake: keep where it was put, standing on the flat ground instead
                    var p = t.position; p.y -= baked.Height(p.x, p.z); t.position = p;
                }
            }
            for (int i = 0; i < record.drapedFilters.Count; i++) if (record.drapedFilters[i] != null) record.drapedFilters[i].sharedMesh = record.flatFilterMeshes[i];
            for (int i = 0; i < record.drapedColliders.Count; i++) if (record.drapedColliders[i] != null) record.drapedColliders[i].sharedMesh = record.flatColliderMeshes[i];
            foreach (var box in record.disabledBoxes) if (box != null) box.enabled = true;
            foreach (var added in record.addedColliders) if (added != null) DestroyComponent(added);
            for (int i = 0; i < record.grownBoxes.Count; i++)
                if (record.grownBoxes[i] != null) { record.grownBoxes[i].center = record.flatBoxCentre[i]; record.grownBoxes[i].size = record.flatBoxSize[i]; }
            foreach (var go in record.hidden) if (go != null) go.SetActive(true);
            foreach (var go in record.created) if (go != null) CityEditTransaction.DestroyGenerated(go);
            int at = 0;
            for (int i = 0; i < record.pointOwners.Count; i++)
            {
                int n = record.pointCounts[i];
                if (record.pointOwners[i] != null) { var points = record.flatPoints.GetRange(at, n); int k = 0; Points(record.pointOwners[i], points, ref k, true); EditorUtility.SetDirty(record.pointOwners[i]); }
                at += n;
            }
            record.Clear(); EditorUtility.SetDirty(record);
        }
        // Things stamped by hand into a town that already had relief were dropped onto the visible ground.
        var authored = TownAuthoringScope.AuthoredRoot(town);
        if (authored != null && baked != null)
            foreach (var placed in authored.GetComponentsInChildren<TownPlacedAsset>(true))
            {
                bool recorded = false;
                for (var p = placed.transform; p != null && !recorded; p = p.parent) recorded = known.Contains(p);
                if (recorded) continue;
                var pos = placed.transform.position; pos.y -= baked.Height(pos.x, pos.z); placed.transform.position = pos;
            }
    }

    static void DestroyComponent(Component c)
    {
        if (CityEditTransaction.Current != null) Undo.DestroyObjectImmediate(c); else Object.DestroyImmediate(c);
    }

    // -------------------------------------------------------------------------------------------- mesh storage

    sealed class Store
    {
        public readonly string path; public Mesh main; bool watched;
        public readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        public readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        public int written, reused, pruned;
        public Store(string path)
        {
            this.path = path;
            if (!System.IO.File.Exists(path)) return;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = o as Mesh; if (m == null) continue;
                if (AssetDatabase.IsMainAsset(m)) main = m; else if (!meshes.ContainsKey(m.name)) meshes.Add(m.name, m);
            }
        }
        void Watch()
        {
            if (watched) return; watched = true;
            CityEditTransaction.Current?.WatchAsset(path);
            if (!AssetDatabase.IsValidFolder(AssetDir)) AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(AssetDir).Replace('\\', '/'), System.IO.Path.GetFileName(AssetDir));
        }
        public Mesh Put(string name, TownReliefMesh.Data d)
        {
            if (!used.Add(name)) throw new InvalidOperationException("Duplicate relief mesh name " + name);
            meshes.TryGetValue(name, out var mesh);
            if (mesh != null && SameContent(mesh, d)) { reused++; return mesh; }
            Watch();
            if (main == null) { main = new Mesh { name = System.IO.Path.GetFileNameWithoutExtension(path) }; AssetDatabase.CreateAsset(main, path); }
            if (mesh == null) { mesh = new Mesh { name = name }; Fill(mesh, d); AssetDatabase.AddObjectToAsset(mesh, main); meshes[name] = mesh; }
            else Fill(mesh, d);
            EditorUtility.SetDirty(mesh); EditorUtility.SetDirty(main); written++;
            return mesh;
        }
        public void Finish()
        {
            var stale = new List<Mesh>();
            foreach (var pair in meshes) if (!used.Contains(pair.Key)) stale.Add(pair.Value);
            if (stale.Count > 0)
            {
                Watch();
                foreach (var m in stale) { meshes.Remove(m.name); AssetDatabase.RemoveObjectFromAsset(m); Object.DestroyImmediate(m, true); pruned++; }
                if (main != null) EditorUtility.SetDirty(main);
            }
            if (CityEditTransaction.Current == null && main != null) AssetDatabase.SaveAssetIfDirty(main);
        }
        static void Fill(Mesh mesh, TownReliefMesh.Data d)
        {
            mesh.Clear();
            mesh.indexFormat = d.vertices.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(d.vertices); mesh.SetNormals(d.normals);
            if (d.uv != null) mesh.SetUVs(0, d.uv);
            if (d.uv2 != null) mesh.SetUVs(1, d.uv2);
            mesh.subMeshCount = Mathf.Max(1, d.triangles.Count);
            for (int i = 0; i < d.triangles.Count; i++) mesh.SetTriangles(d.triangles[i], i, false);
            mesh.RecalculateBounds();
        }
        static bool SameContent(Mesh mesh, TownReliefMesh.Data d)
        {
            if (mesh.vertexCount != d.vertices.Count || mesh.subMeshCount != Mathf.Max(1, d.triangles.Count)) return false;
            var v = mesh.vertices; var n = mesh.normals;
            if (n.Length != d.normals.Count) return false;
            for (int i = 0; i < v.Length; i++) if (v[i] != d.vertices[i] || n[i] != d.normals[i]) return false;
            if (d.uv != null) { var uv = mesh.uv; if (uv.Length != d.uv.Count) return false; for (int i = 0; i < uv.Length; i++) if (uv[i] != d.uv[i]) return false; }
            if (d.uv2 != null) { var uv = mesh.uv2; if (uv.Length != d.uv2.Count) return false; for (int i = 0; i < uv.Length; i++) if (uv[i] != d.uv2[i]) return false; }
            for (int s = 0; s < d.triangles.Count; s++)
            {
                var t = mesh.GetTriangles(s); var want = d.triangles[s];
                if (t.Length != want.Count) return false;
                for (int i = 0; i < t.Length; i++) if (t[i] != want[i]) return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------- the bake

    enum Kind { Rigid, Tilt, Drape }
    sealed class Unit
    {
        public Transform t; public Kind kind; public int depth;
        public Vector3 flatLocal, flatWorld; public Quaternion flatLocalRot, flatWorldRot;
        public Vector3 world; public Quaternion worldRot;
        public float lift;
    }
    struct KitUse { public MeshFilter filter; public Matrix4x4 flatToWorld; }
    struct DrapeUse { public MeshFilter filter; public Matrix4x4 flatToWorld; public string name; }

    sealed class Bake
    {
        public Transform town; public TownReliefField field; public TownReliefRecord record;
        public readonly List<Unit> units = new List<Unit>();
        public readonly List<KitUse> kit = new List<KitUse>();
        public readonly List<DrapeUse> drapes = new List<DrapeUse>();
        public readonly HashSet<Transform> skip = new HashSet<Transform>();
        public readonly Dictionary<Mesh, int> users = new Dictionary<Mesh, int>();
        public readonly List<KeyValuePair<Unit, Bounds>> boxOwners = new List<KeyValuePair<Unit, Bounds>>();
        public int rigid, tilted, draped, kitDraped, kitWritten, boxesGrown, points;
    }

    /// <summary>HOOK 2: after validation of the flat town, before the knockable bake and every file write.</summary>
    public static string PipelineApply(Transform town, StreetGraph graph)
    {
        if (!active) throw new InvalidOperationException("TownReliefBaker.PipelineBegin was not called for this bake.");
        if (town == null) throw new ArgumentNullException(nameof(town));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var record = town.GetComponent<TownReliefRecord>();
        if (record == null) record = CityEditTransaction.Current != null ? Undo.AddComponent<TownReliefRecord>(town.gameObject) : town.gameObject.AddComponent<TownReliefRecord>();
        record.Clear(); record.designRevision = TownReliefDesign.Revision;

        var input = TownReliefDesign.FromLayout(); input.strength = Enabled ? 1f : 0f;
        var authored = TownAuthoringScope.AuthoredRoot(town);
        if (Enabled) CollectPads(town, authored, input.pads);
        var result = TownReliefDesign.Build(input);
        WriteField(result.field);
        TownRelief.Use(result.field); TownRelief.Suspended = false;
        if (!Enabled)
        {
            EditorUtility.SetDirty(record);
            new Store(MeshPath).Finish(); new Store(TerrainPath).Finish();
            return LastReport = "[relief] DISABLED on this machine (" + EnabledMenuPath + "): the town was left flat.";
        }

        var bake = new Bake { town = town, field = result.field, record = record };
        var terrainStore = new Store(TerrainPath); var meshStore = new Store(MeshPath);
        BuildTerrain(bake, result, terrainStore);
        BuildWater(bake, input, result, terrainStore);
        foreach (var limit in town.GetComponentsInChildren<BoxCollider>(true))
            if (limit.name == "City limit") { var s = limit.size; if (s.y < 200f) limit.size = new Vector3(s.x, 240f, s.z); bake.skip.Add(limit.transform); }

        foreach (var f in TownAuthoringScope.Components<MeshFilter>(town))
        {
            if (f.sharedMesh != null) bake.users[f.sharedMesh] = (bake.users.TryGetValue(f.sharedMesh, out int n) ? n : 0) + 1;
            if (f.name == "Wooded ridgeline") bake.skip.Add(f.transform);          // the far backdrop stays where it is
        }
        foreach (Transform child in town) Collect(bake, child, 1);
        if (authored != null && !authored.IsChildOf(town)) foreach (Transform child in authored) Collect(bake, child, 1);
        LiftPoints(bake, town, authored);
        Move(bake);
        DrapeCopies(bake, meshStore);
        DrapeKit(bake);
        LiftGraph(result.field, graph);
        meshStore.Finish(); terrainStore.Finish();
        Physics.SyncTransforms();
        EditorUtility.SetDirty(record);
        LastReport = result.Report() + "\n[relief] " + input.pads.Count + " level pads; " + bake.rigid + " lifted, " + bake.tilted + " tilted, " + bake.draped + " draped copies (" +
            meshStore.written + " written, " + meshStore.reused + " unchanged, " + meshStore.pruned + " pruned), " + bake.kitDraped + " generated meshes draped in place (" + bake.kitWritten +
            " rewritten), " + bake.boxesGrown + " colliders stretched, " + bake.points + " stored points lifted, terrain " + terrainStore.used.Count + " chunks (" + terrainStore.written +
            " written); " + record.moved.Count + " transforms recorded, " + clock.ElapsedMilliseconds + " ms.";
        return LastReport;
    }

    static void WriteField(TownReliefField field)
    {
        var bytes = field.ToBytes();
        if (System.IO.File.Exists(TownRelief.AssetPath))
        {
            var old = System.IO.File.ReadAllBytes(TownRelief.AssetPath);
            if (old.Length == bytes.Length) { bool same = true; for (int i = 0; i < old.Length && same; i++) same = old[i] == bytes[i]; if (same) return; }
        }
        CityEditTransaction.Current?.WatchAsset(TownRelief.AssetPath);
        System.IO.File.WriteAllBytes(TownRelief.AssetPath, bytes);
        AssetDatabase.ImportAsset(TownRelief.AssetPath, ImportAssetOptions.ForceSynchronousImport);
    }

    // --------------------------------------------------------------------------------------------------- pads

    static bool Under(Transform t, Transform town, params string[] groups)
    {
        for (var p = t; p != null && p != town; p = p.parent)
            if (p.parent == town) { foreach (string g in groups) if (p.name == g) return true; return false; }
        return false;
    }

    static void CollectPads(Transform town, Transform authored, List<TownReliefDesign.Pad> pads)
    {
        foreach (var pad in TownAuthoringScope.Components<TownReliefPad>(town)) pads.Add(pad.ToPad());
        // Buildings stand on level ground: anything tall with a real footprint. The buildings of one lot (a mansion's
        // two wings and the hall between them) share ONE level; two pads side by side would meet in a bank.
        var lots = new Dictionary<Transform, Vector4>();           // frame -> min x, min z, max x, max z in that frame
        foreach (var renderer in TownAuthoringScope.Components<MeshRenderer>(town))
        {
            var t = renderer.transform; var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || kitFlat.ContainsKey(filter.sharedMesh)) continue;
            if (Under(t, town, "Ground", "Bounds", "CoastalRoads", "CoastalGates", "Nature") || Vegetation.IsMatch(t.name)) continue;
            if (Vector3.Angle(t.up, Vector3.up) > 5f) continue;
            var scale = t.lossyScale; var local = filter.sharedMesh.bounds;
            var size = new Vector3(Mathf.Abs(local.size.x * scale.x), Mathf.Abs(local.size.y * scale.y), Mathf.Abs(local.size.z * scale.z));
            if (size.y < 3.5f || Mathf.Min(size.x, size.z) < 6f) continue;
            var anchor = t.GetComponentInParent<CityRoadAnchor>();
            if (anchor == null || anchor.transform == t)
            {
                var centre = t.TransformPoint(local.center);
                pads.Add(new TownReliefDesign.Pad { centre = new Vector2(centre.x, centre.z), half = new Vector2(size.x * 0.5f + 2.5f, size.z * 0.5f + 2.5f), yaw = t.eulerAngles.y, feather = 6f, name = t.name });
                continue;
            }
            var frame = anchor.transform; bool known = lots.TryGetValue(frame, out var box);
            for (int i = 0; i < 4; i++)
            {
                var corner = frame.InverseTransformPoint(t.TransformPoint(local.center + new Vector3((i & 1) == 0 ? -local.extents.x : local.extents.x, 0f, (i & 2) == 0 ? -local.extents.z : local.extents.z)));
                box = known ? new Vector4(Mathf.Min(box.x, corner.x), Mathf.Min(box.y, corner.z), Mathf.Max(box.z, corner.x), Mathf.Max(box.w, corner.z)) : new Vector4(corner.x, corner.z, corner.x, corner.z);
                known = true;
            }
            lots[frame] = box;
        }
        foreach (var lot in lots)
        {
            var box = lot.Value; var centre = lot.Key.TransformPoint(new Vector3((box.x + box.z) * 0.5f, 0f, (box.y + box.w) * 0.5f));
            pads.Add(new TownReliefDesign.Pad { centre = new Vector2(centre.x, centre.z), half = new Vector2((box.z - box.x) * 0.5f + 2.5f, (box.w - box.y) * 0.5f + 2.5f), yaw = lot.Key.eulerAngles.y, feather = 6f, name = lot.Key.name });
        }
        // Attached shop rows, workyards and courtyards are one terrace each.
        foreach (var site in TownAuthoringScope.Components<CoastalInfillSite>(town))
        {
            if (site.kind != "storefront" && site.kind != "workyard" && site.kind != "courtyard") continue;
            var centre = site.transform.TransformPoint(new Vector3(0f, 0f, site.depth * 0.5f));
            pads.Add(new TownReliefDesign.Pad { centre = new Vector2(centre.x, centre.z), half = new Vector2(site.width * 0.5f, site.depth * 0.5f), yaw = site.transform.eulerAngles.y, feather = 4f, name = site.siteId });
        }
        // Play pens: the kids stand on one plane.
        foreach (var pen in TownAuthoringScope.Components<KidPen>(town))
        {
            float reach = 2f;
            if (pen.spots != null) foreach (var spot in pen.spots) reach = Mathf.Max(reach, Vector2.Distance(new Vector2(spot.x, spot.z), new Vector2(pen.center.x, pen.center.z)));
            pads.Add(new TownReliefDesign.Pad { centre = new Vector2(pen.center.x, pen.center.z), half = Vector2.one * (reach + 2.5f), yaw = pen.yaw, feather = 4f, name = pen.penId });
        }
        // Estate features built as a group (hedge maze, launch pad, pool): level, whatever they are called.
        foreach (var anchor in TownAuthoringScope.Components<CityRoadAnchor>(town))
            foreach (Transform lot in anchor.transform)
            {
                if (!lot.name.StartsWith("Lot_", StringComparison.Ordinal)) continue;
                foreach (Transform feature in lot)
                {
                    if (feature.childCount < 3 || feature.GetComponents<Component>().Length != 1) continue;
                    var inverse = feature.worldToLocalMatrix; bool any = false; Vector3 min = Vector3.zero, max = Vector3.zero;
                    foreach (var r in feature.GetComponentsInChildren<Renderer>(true))
                    {
                        var b = r.bounds;
                        for (int i = 0; i < 4; i++)
                        {
                            var p = inverse.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, b.center.y, (i & 2) == 0 ? b.min.z : b.max.z));
                            if (!any) { min = max = p; any = true; } else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                        }
                    }
                    if (!any) continue;
                    float w = max.x - min.x, d = max.z - min.z;
                    if (Mathf.Min(w, d) < 4f || Mathf.Max(w, d) > 45f) continue;
                    var centre = feature.TransformPoint(new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f));
                    pads.Add(new TownReliefDesign.Pad { centre = new Vector2(centre.x, centre.z), half = new Vector2(w * 0.5f + 1f, d * 0.5f + 1f), yaw = feature.eulerAngles.y, feather = 4f, name = feature.name });
                }
            }
        // The Hoverhog's satellite pads that stand on the ground.
        foreach (var pad in HogTuning.SatPads)
            if (pad.pos.y < 0.5f) pads.Add(new TownReliefDesign.Pad { centre = new Vector2(pad.pos.x, pad.pos.z), half = Vector2.one * 6f, yaw = 0f, feather = 5f, name = "hog pad" });
    }

    // ------------------------------------------------------------------------------------------------ terrain

    static void BuildTerrain(Bake bake, TownReliefDesign.Result result, Store store)
    {
        var ground = bake.town.Find("Ground"); if (ground == null) return;
        var flat = ground.Find("Coastal terrain");
        Material grass = flat != null && flat.GetComponent<Renderer>() != null ? flat.GetComponent<Renderer>().sharedMaterial : AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/NB_GrassFar.mat");
        Material rock = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/NB_Stone.mat") ?? grass, sand = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/NB_Sand.mat") ?? grass;
        var field = result.field; var terrain = result.terrain; int nx = field.nx, nz = field.nz; float cell = field.cell;
        Vector3 P(int ix, int iz) { return new Vector3(field.x0 + ix * cell, terrain[iz * nx + ix], field.z0 + iz * cell); }
        Vector3 N(int ix, int iz)
        {
            float l = terrain[iz * nx + Mathf.Max(ix - 1, 0)], r = terrain[iz * nx + Mathf.Min(ix + 1, nx - 1)];
            float d = terrain[Mathf.Max(iz - 1, 0) * nx + ix], u = terrain[Mathf.Min(iz + 1, nz - 1) * nx + ix];
            return new Vector3((l - r) / (2f * cell), 1f, (d - u) / (2f * cell)).normalized;
        }
        for (int cz = 0; cz * ChunkCells < nz - 1; cz++) for (int cx = 0; cx * ChunkCells < nx - 1; cx++)
        {
            var data = new TownReliefMesh.Data(); for (int s = 0; s < 3; s++) data.triangles.Add(new List<int>());
            var index = new Dictionary<int, int>();
            int Shared(int ix, int iz)
            {
                int key = iz * nx + ix;
                if (!index.TryGetValue(key, out int i)) { i = data.vertices.Count; data.vertices.Add(P(ix, iz)); data.normals.Add(N(ix, iz)); index.Add(key, i); }
                return i;
            }
            void Tri(int ax, int az, int bx, int bz, int kx, int kz)
            {
                Vector3 a = P(ax, az), b = P(bx, bz), k = P(kx, kz); var n = Vector3.Cross(b - a, k - a).normalized;
                float low = Mathf.Min(a.y, Mathf.Min(b.y, k.y));
                int sub = n.y < 0.82f && low > -1.2f ? 1 : low < 0.25f && n.y < 0.995f || low < -0.6f ? 2 : 0;
                if (sub == 0) { data.triangles[0].Add(Shared(ax, az)); data.triangles[0].Add(Shared(bx, bz)); data.triangles[0].Add(Shared(kx, kz)); return; }
                int first = data.vertices.Count;                           // rock and sand are faceted
                data.vertices.Add(a); data.vertices.Add(b); data.vertices.Add(k); data.normals.Add(n); data.normals.Add(n); data.normals.Add(n);
                data.triangles[sub].Add(first); data.triangles[sub].Add(first + 1); data.triangles[sub].Add(first + 2);
            }
            int x0 = cx * ChunkCells, z0 = cz * ChunkCells, x1 = Mathf.Min(x0 + ChunkCells, nx - 1), z1 = Mathf.Min(z0 + ChunkCells, nz - 1);
            for (int iz = z0; iz < z1; iz++) for (int ix = x0; ix < x1; ix++)
            {
                // the same split as TownReliefField.Height
                if (TownReliefField.Flipped(ix, iz)) { Tri(ix, iz, ix, iz + 1, ix + 1, iz); Tri(ix + 1, iz, ix, iz + 1, ix + 1, iz + 1); }
                else { Tri(ix, iz, ix, iz + 1, ix + 1, iz + 1); Tri(ix, iz, ix + 1, iz + 1, ix + 1, iz); }
            }
            string name = "Relief terrain " + cx + "_" + cz;
            var mesh = store.Put(name, data);
            var go = new GameObject(name) { layer = flat != null ? flat.gameObject.layer : LayerGround };
            go.transform.SetParent(ground, false);
            CityEditTransaction.RegisterCreated(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { grass, rock, sand };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            if (flat != null) GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(flat.gameObject));
            bake.record.created.Add(go); bake.skip.Add(go.transform);
        }
        if (flat != null) { flat.gameObject.SetActive(false); bake.record.hidden.Add(flat.gameObject); bake.skip.Add(flat); }
        foreach (string water in new[] { "Bandit River", "Eastern Sea" }) { var w = ground.Find(water); if (w != null) bake.skip.Add(w); }
    }

    // -------------------------------------------------------------------------------------------------- water

    const float SeaLevel = -1.1f, RiverLevel = -1.115f;        // the river a hair lower: where they overlap the sea wins cleanly
    public const string WaterShader = "Rubbish/Water";

    static Material WaterMaterial(string name, Action<Material> tune)
    {
        string path = AssetDir + "/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find(WaterShader);
        if (material != null)
        {
            // the owner's tuning is never overwritten; only a lost shader (the file was re-imported under a new id) is put back
            if (shader != null && (material.shader == null || material.shader.name == "Hidden/InternalErrorShader"))
            { CityEditTransaction.Current?.WatchAsset(path); material.shader = shader; EditorUtility.SetDirty(material); if (CityEditTransaction.Current == null) AssetDatabase.SaveAssetIfDirty(material); }
            return material;
        }
        if (shader == null) return null;
        CityEditTransaction.Current?.WatchAsset(path);
        if (!AssetDatabase.IsValidFolder(AssetDir)) AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(AssetDir).Replace('\\', '/'), System.IO.Path.GetFileName(AssetDir));
        material = new Material(shader) { name = name }; tune(material);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    /// <summary>The river and the sea as fine grids (so their waves have something to move), each vertex knowing how
    /// far it is from the waterline: the shader has no other way to find the bank.</summary>
    static void BuildWater(Bake bake, TownReliefDesign.Input input, TownReliefDesign.Result result, Store store)
    {
        var ground = bake.town.Find("Ground"); if (ground == null) return;
        var river = WaterMaterial("Relief_Water_River", m => { });
        var sea = WaterMaterial("Relief_Water_Sea", m =>
        {
            m.SetFloat("_WaveHeight", 0.13f); m.SetFloat("_WaveLength", 12f); m.SetFloat("_Flow", 0.45f); m.SetFloat("_Streaks", 0.18f);
            m.SetFloat("_DepthFade", 28f); m.SetFloat("_FoamWidth", 5f);
            m.SetColor("_ShallowColor", new Color(0.36f, 0.80f, 0.84f)); m.SetColor("_DeepColor", new Color(0.10f, 0.36f, 0.62f));
        });
        if (river == null || sea == null) return;               // no shader yet: the old flat water stays
        var bed = new TownReliefField(result.field.x0, result.field.z0, result.field.cell, result.field.nx, result.field.nz);
        Array.Copy(result.terrain, bed.h, bed.h.Length);
        float Shore(float x, float z, float level) { return Mathf.Clamp((level - bed.Height(x, z)) * 2f, 0f, 60f); }
        float CoastX(float z)
        {
            var c = input.coast;
            for (int i = 1; i < c.Length; i++)
                if (z >= Mathf.Min(c[i - 1].z, c[i].z) && z <= Mathf.Max(c[i - 1].z, c[i].z)) return Mathf.Lerp(c[i - 1].x, c[i].x, Mathf.InverseLerp(c[i - 1].z, c[i].z, z));
            return c[c.Length - 1].x;
        }
        TownReliefMesh.Data Grid(List<Vector3> rows, List<float> along, float[] offsets, Func<int, Vector3> across, float level)
        {
            var d = new TownReliefMesh.Data { uv = new List<Vector2>(), uv2 = new List<Vector2>() }; d.triangles.Add(new List<int>());
            int columns = offsets.Length;
            for (int i = 0; i < rows.Count; i++)
            {
                var side = across(i);
                for (int k = 0; k < columns; k++)
                {
                    var p = rows[i] + side * offsets[k]; p.y = level;
                    d.vertices.Add(p); d.normals.Add(Vector3.up); d.uv.Add(new Vector2(along[i], offsets[k])); d.uv2.Add(new Vector2(Shore(p.x, p.z, level), 0f));
                }
            }
            for (int i = 0; i + 1 < rows.Count; i++) for (int k = 0; k + 1 < columns; k++)
            {
                int a = i * columns + k, b = a + columns, c = b + 1, e = a + 1;
                bool up = Vector3.Cross(d.vertices[b] - d.vertices[a], d.vertices[c] - d.vertices[a]).y > 0f;
                var t = d.triangles[0];
                if (up) { t.Add(a); t.Add(b); t.Add(c); t.Add(a); t.Add(c); t.Add(e); }
                else { t.Add(a); t.Add(c); t.Add(b); t.Add(a); t.Add(e); t.Add(c); }
            }
            return d;
        }
        void Place(string name, string old, TownReliefMesh.Data data, Material material)
        {
            var mesh = store.Put(name, data);
            var previous = ground.Find(old);
            var go = new GameObject(name) { layer = previous != null ? previous.gameObject.layer : LayerGround };
            go.transform.SetParent(ground, false); CityEditTransaction.RegisterCreated(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bake.record.created.Add(go); bake.skip.Add(go.transform);
            if (previous != null) { previous.gameObject.SetActive(false); bake.record.hidden.Add(previous.gameObject); }
        }

        // River: rows every 3 m along the centreline, out past both waterlines; it stops where the sea takes over.
        var line = new List<Vector3>(); var lineAlong = new List<float>(); float run = 0f;
        for (int i = 1; i < input.river.Length; i++)
        {
            Vector3 a = input.river[i - 1], b = input.river[i]; a.y = b.y = 0f; float seg = Vector3.Distance(a, b); int steps = Mathf.Max(1, Mathf.CeilToInt(seg / 3f));
            for (int k = i == 1 ? 0 : 1; k <= steps; k++)
            {
                var p = Vector3.Lerp(a, b, k / (float)steps);
                if (p.x > CoastX(p.z) - 2f) continue;
                line.Add(p); lineAlong.Add(run + seg * k / steps);
            }
            run += seg;
        }
        if (line.Count >= 2)
        {
            Vector3 Across(int i)
            {
                var tangent = line[Mathf.Min(i + 1, line.Count - 1)] - line[Mathf.Max(i - 1, 0)]; tangent.y = 0f; tangent.Normalize();
                return new Vector3(tangent.z, 0f, -tangent.x);
            }
            Place("Relief river", "Bandit River", Grid(line, lineAlong, new[] { -24f, -22f, -19.5f, -16.5f, -13f, -9.5f, -6f, -3f, 0f, 3f, 6f, 9.5f, 13f, 16.5f, 19.5f, 22f, 24f }, Across, RiverLevel), river);
        }

        // Sea: rows along the coast, columns close together near the shore and far apart out to the horizon.
        var coastRows = new List<Vector3>(); var coastAlong = new List<float>(); run = 0f;
        for (int i = 0; i < input.coast.Length; i++)
        {
            var p = input.coast[i]; p.y = 0f;
            if (i > 0) { var q = input.coast[i - 1]; q.y = 0f; run += Vector3.Distance(p, q); }
            coastRows.Add(p); coastAlong.Add(run);
        }
        float far = Mathf.Max(520f, input.bounds.xMax + 500f - input.coast[0].x);
        Place("Relief sea", "Eastern Sea", Grid(coastRows, coastAlong, new[] { -9f, -6.5f, -4f, -1.5f, 1f, 4f, 7f, 10.5f, 14f, 18f, 23f, 29f, 37f, 48f, 62f, 85f, 120f, 180f, 280f, far }, i => Vector3.right, SeaLevel), sea);
    }

    // ------------------------------------------------------------------------------------------------ collect

    static bool RigidMarker(Transform t)
    {
        foreach (var c in t.GetComponents<MonoBehaviour>())
            if (c == null || !Structural.Contains(c.GetType().Name)) return true;
        return false;
    }

    static bool SubtreeBounds(Transform t, out Bounds bounds)
    {
        bool any = false; bounds = new Bounds(t.position, Vector3.zero);
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r.bounds.size == Vector3.zero) continue;
            if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
        }
        return any;
    }

    static void RegisterKit(Bake bake, Transform root)
    {
        foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
            if (f.sharedMesh != null && kitFlat.ContainsKey(f.sharedMesh)) bake.kit.Add(new KitUse { filter = f, flatToWorld = f.transform.localToWorldMatrix });
    }

    static Unit Add(Bake bake, Transform t, int depth, Kind kind)
    {
        var u = new Unit { t = t, kind = kind, depth = depth, flatLocal = t.localPosition, flatLocalRot = t.localRotation, flatWorld = t.position, flatWorldRot = t.rotation };
        u.world = u.flatWorld; u.worldRot = u.flatWorldRot;
        bake.units.Add(u); return u;
    }

    static void Collect(Bake bake, Transform t, int depth)
    {
        if (bake.skip.Contains(t) || t.name == "Bounds" && t.parent == bake.town) return;
        var filter = t.GetComponent<MeshFilter>(); var mesh = filter != null ? filter.sharedMesh : null;
        if (mesh != null && kitFlat.ContainsKey(mesh))
        {
            // drawn by a generated world-space mesh: the mesh is draped in place, only what hangs under it moves
            bake.kit.Add(new KitUse { filter = filter, flatToWorld = t.localToWorldMatrix });
            foreach (Transform child in t) Collect(bake, child, depth + 1);
            return;
        }
        bool renderer = t.GetComponent<Renderer>() != null, collider = t.GetComponent<Collider>() != null, marker = RigidMarker(t);
        bool prefab = PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject);
        if (!renderer && !collider && filter == null && !marker && !prefab)
        {
            if (t.childCount == 0) { Rigid(bake, Add(bake, t, depth, Kind.Rigid), false, default); return; }
            bool pure = t.GetComponents<Component>().Length == 1;
            if (pure && SubtreeBounds(t, out var compact) && Mathf.Max(compact.size.x, compact.size.z) <= 6f &&
                t.GetComponentInChildren<CityRoadAnchor>(true) == null && t.GetComponentInChildren<PickupStop>(true) == null && t.GetComponentInChildren<TownReliefPad>(true) == null)
            { RegisterKit(bake, t); Rigid(bake, Add(bake, t, depth, Kind.Rigid), true, compact); return; }
            // a group: an anchored one (a lot, a parcel) rides up with its own spot, then its contents are placed
            if (!pure && t.localPosition.sqrMagnitude > 1e-8f) Rigid(bake, Add(bake, t, depth, Kind.Rigid), false, default);
            foreach (Transform child in t) Collect(bake, child, depth + 1);
            return;
        }
        RegisterKit(bake, t);
        bool any = SubtreeBounds(t, out var bounds);
        if (!any)
        {
            // nothing drawn (a fence run's collision box): measure what it blocks instead
            foreach (var c in t.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = c.bounds; any = true; } else bounds.Encapsulate(c.bounds);
            }
            if (any && Mathf.Max(bounds.size.x, bounds.size.z) > Small)
            {
                var blocker = Add(bake, t, depth, Kind.Rigid); Rigid(bake, blocker, true, bounds);
                bake.boxOwners.Add(new KeyValuePair<Unit, Bounds>(blocker, bounds)); return;
            }
        }
        float foot = Mathf.Max(bounds.size.x, bounds.size.z);
        if (!any || foot <= Small) { Rigid(bake, Add(bake, t, depth, Kind.Rigid), any, bounds); return; }
        TownReliefMesh.Sample(bake.field, bounds, out float min, out float max, out float centre, out Vector2 slope, out float residual);
        bool flat = bounds.size.y <= FlatThickness;
        // Only a shape of its own (a road, a slab, a wall: a primitive or a one-off mesh) is reshaped. A mesh many
        // objects share (a tree, a bench) is never copied per object; it is stood on the ground instead.
        bool drapeable = mesh != null && renderer && !marker && mesh.isReadable && mesh.vertexCount <= DrapeVertexLimit &&
                         (bake.users.TryGetValue(mesh, out int users) && users == 1 || AssetDatabase.GetAssetPath(mesh).StartsWith("Library/", StringComparison.Ordinal)) &&
                         t.GetComponentsInChildren<Renderer>(true).Length == 1;
        if (max - min < Level) { Rigid(bake, Add(bake, t, depth, Kind.Rigid), true, bounds); return; }
        // a slab small enough for one plane to describe the ground under it is tilted; anything longer follows the
        // ground; a compact solid (a pillar, a ball) just stands on it
        if (drapeable && (flat ? !(foot <= 12f && residual <= PlaneFit) : foot > 6f))
        {
            bake.drapes.Add(new DrapeUse { filter = filter, flatToWorld = t.localToWorldMatrix, name = MeshName(t, bake.town) });
            bake.draped++; return;
        }
        // something placed by a road anchor (a gate) stands exactly where its anchor resolves to
        if (t.GetComponent<CityRoadAnchor>() != null) { Rigid(bake, Add(bake, t, depth, Kind.Rigid), false, default); return; }
        bool car = t.GetComponent<HouseholdCar>() != null, plant = !marker && !flat && Vegetation.IsMatch(t.name) && foot >= 8f;
        if (flat || car || plant)
        {
            var u = Add(bake, t, depth, Kind.Tilt);
            var normal = new Vector3(-slope.x, 1f, -slope.y).normalized;
            if (plant) normal = Vector3.RotateTowards(Vector3.up, normal, 10f * Mathf.Deg2Rad, 0f);
            float at = centre + slope.x * (u.flatWorld.x - bounds.center.x) + slope.y * (u.flatWorld.z - bounds.center.z);
            u.world = u.flatWorld + Vector3.up * (at - (plant ? Mathf.Min(0.5f, residual) : 0f));
            u.worldRot = Quaternion.FromToRotation(Vector3.up, normal) * u.flatWorldRot;
            bake.tilted++; return;
        }
        var rigid = Add(bake, t, depth, Kind.Rigid);
        // a building on a slope sits down into it rather than hovering over its low side
        bool building = bounds.size.y >= 3.5f && Mathf.Min(bounds.size.x, bounds.size.z) >= 6f && (marker || !Vegetation.IsMatch(t.name));
        if (building)
        {
            // measured under its middle, where its level pad is: the corners of its bounding box may hang over the
            // pad's edge (and, on the Bluffs, over the cliff)
            float low = float.MaxValue;
            for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++)
                low = Mathf.Min(low, bake.field.Height(bounds.center.x + i * bounds.size.x * 0.28f, bounds.center.z + j * bounds.size.z * 0.28f));
            rigid.lift = low; rigid.world = rigid.flatWorld + Vector3.up * low; bake.rigid++;
        }
        else Rigid(bake, rigid, true, bounds);
        bake.boxOwners.Add(new KeyValuePair<Unit, Bounds>(rigid, bounds));
    }

    static void Rigid(Bake bake, Unit u, bool hasBounds, Bounds bounds)
    {
        // stand on the ground under the pivot when the pivot is under the object, else under its middle
        Vector3 at = u.flatWorld;
        if (hasBounds && (Mathf.Abs(at.x - bounds.center.x) > bounds.extents.x + 0.5f || Mathf.Abs(at.z - bounds.center.z) > bounds.extents.z + 0.5f)) at = bounds.center;
        u.lift = bake.field.Height(at.x, at.z);
        u.world = u.flatWorld + Vector3.up * u.lift;
        bake.rigid++;
    }

    static string MeshName(Transform t, Transform town)
    {
        var sb = new StringBuilder();
        for (var p = t; p != null && p != town; p = p.parent) sb.Insert(0, "/" + p.name + "#" + p.GetSiblingIndex());
        string clean = TownLifeKit.Sanitize(t.name); if (clean.Length > 28) clean = clean.Substring(0, 28);
        return "RD_" + clean + "_" + Hash128.Compute(sb.ToString()).ToString().Substring(0, 14);
    }

    // --------------------------------------------------------------------------------------------------- move

    static void Move(Bake bake)
    {
        // Stretch long upright colliders over the slope they stand on (measured while the town is still flat).
        foreach (var pair in bake.boxOwners)
            foreach (var box in pair.Key.t.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box.isTrigger) continue;
                var bt = box.transform; var scale = bt.lossyScale;
                if (Vector3.Angle(bt.up, Vector3.up) > 2f || Mathf.Abs(scale.y) < 1e-4f) continue;
                Vector3 ax = bt.right * (box.size.x * Mathf.Abs(scale.x) * 0.5f), az = bt.forward * (box.size.z * Mathf.Abs(scale.z) * 0.5f);
                if (Mathf.Max(ax.magnitude, az.magnitude) < 2f) continue;
                var centre = bt.TransformPoint(box.center); float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i <= 8; i++) for (int j = -1; j <= 1; j += 2)
                {
                    var p = ax.sqrMagnitude > az.sqrMagnitude ? centre + ax * (i / 4f - 1f) + az * j : centre + az * (i / 4f - 1f) + ax * j;
                    float h = bake.field.Height(p.x, p.z) - pair.Key.lift; lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
                }
                if (hi - lo < 0.05f) continue;
                bake.record.grownBoxes.Add(box); bake.record.flatBoxCentre.Add(box.center); bake.record.flatBoxSize.Add(box.size);
                box.center += Vector3.up * ((hi + lo) * 0.5f / Mathf.Abs(scale.y));
                box.size += Vector3.up * ((hi - lo) / Mathf.Abs(scale.y));
                bake.boxesGrown++;
            }
        bake.units.Sort((a, b) => a.depth.CompareTo(b.depth));      // parents first: every pose below is a world pose
        foreach (var u in bake.units) u.t.SetPositionAndRotation(u.world, u.worldRot);
        foreach (var u in bake.units)
        {
            if (Same(u.t.localPosition, u.flatLocal) && u.t.localRotation == u.flatLocalRot) continue;
            bake.record.moved.Add(u.t); bake.record.flatPosition.Add(u.flatLocal); bake.record.flatRotation.Add(u.flatLocalRot);
            bake.record.bakedPosition.Add(u.t.localPosition); bake.record.bakedRotation.Add(u.t.localRotation);
        }
    }

    // -------------------------------------------------------------------------------------------------- drape

    static IList<IList<int>> Submeshes(Mesh mesh)
    {
        var list = new List<IList<int>>();
        for (int s = 0; s < mesh.subMeshCount; s++) list.Add(mesh.GetTriangles(s));
        return list;
    }

    static void DrapeCopies(Bake bake, Store store)
    {
        foreach (var use in bake.drapes)
        {
            var filter = use.filter; var flat = filter.sharedMesh; var t = filter.transform;
            var uv = flat.uv; var normals = flat.normals;
            var data = TownReliefMesh.Drape(bake.field, flat.vertices, normals, uv.Length == flat.vertexCount ? uv : null, Submeshes(flat), use.flatToWorld, t.worldToLocalMatrix);
            var draped = store.Put(use.name, data);
            bake.record.drapedFilters.Add(filter); bake.record.flatFilterMeshes.Add(flat);
            filter.sharedMesh = draped;
            bool solid = false;
            foreach (var mc in t.GetComponents<MeshCollider>())
                if (mc.sharedMesh == flat) { bake.record.drapedColliders.Add(mc); bake.record.flatColliderMeshes.Add(flat); mc.sharedMesh = draped; solid = true; }
            // a box cannot follow a slope: the draped shape itself becomes the collider
            bool boxes = false;
            foreach (var box in t.GetComponents<BoxCollider>())
                if (box.enabled && !box.isTrigger) { box.enabled = false; bake.record.disabledBoxes.Add(box); boxes = true; }
            if (boxes && !solid)
            {
                var added = CityEditTransaction.Current != null ? Undo.AddComponent<MeshCollider>(t.gameObject) : t.gameObject.AddComponent<MeshCollider>();
                added.sharedMesh = draped; bake.record.addedColliders.Add(added);
            }
        }
    }

    static void DrapeKit(Bake bake)
    {
        var users = new Dictionary<Mesh, int>();
        foreach (var use in bake.kit) users[use.filter.sharedMesh] = (users.TryGetValue(use.filter.sharedMesh, out int n) ? n : 0) + 1;
        var dirtyMains = new HashSet<Object>();
        foreach (var use in bake.kit)
        {
            var mesh = use.filter.sharedMesh; if (users[mesh] != 1) continue;          // a shared mesh cannot follow two places
            var flat = kitFlat[mesh];
            var subs = new List<IList<int>>(); foreach (var list in flat.triangles) subs.Add(list);
            var d = TownReliefMesh.Drape(bake.field, flat.vertices, flat.normals, null, subs, use.flatToWorld, use.filter.transform.worldToLocalMatrix);
            var draped = new TownLifeKit.MeshData();
            draped.vertices.AddRange(d.vertices); draped.normals.AddRange(d.normals); draped.materials.AddRange(flat.materials);
            foreach (var list in d.triangles) draped.triangles.Add(list);
            bake.kitDraped++;
            bake.record.kitMeshes.Add(mesh); bake.record.kitFlatHashes.Add(HashOf(flat));
            if (TownLifeKit.SameMesh(mesh, draped)) continue;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path)) CityEditTransaction.Current?.WatchAsset(path);
            TownLifeKit.FillMesh(mesh, draped); EditorUtility.SetDirty(mesh);
            var main = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (main != null) { EditorUtility.SetDirty(main); dirtyMains.Add(main); }
            foreach (var mc in use.filter.GetComponents<MeshCollider>()) if (mc.sharedMesh == mesh) { mc.sharedMesh = null; mc.sharedMesh = mesh; }
            bake.kitWritten++;
        }
        if (CityEditTransaction.Current == null) foreach (var main in dirtyMains) AssetDatabase.SaveAssetIfDirty(main);
    }

    // ------------------------------------------------------------------------------------------ stored points

    /// <summary>Reads (write = false) or writes the world-space points a component stores, always in the same order.</summary>
    static void Points(Component owner, List<Vector3> points, ref int k, bool write)
    {
        int at = k;
        void P(ref Vector3 v) { if (write) v = points[at]; else points.Add(v); at++; }
        void Q(ref Pose pose) { var v = pose.position; P(ref v); pose.position = v; }
        if (owner is CoastalHouseRoute route)
        {
            P(ref route.door); P(ref route.pathEnd); P(ref route.mailbox); P(ref route.ramp); P(ref route.top);
            if (route.bends != null) for (int i = 0; i < route.bends.Length; i++) P(ref route.bends[i]);
            Q(ref route.car0); Q(ref route.car1); Q(ref route.butlerPost);
        }
        else if (owner is KidPen pen)
        {
            P(ref pen.center);
            if (pen.spots != null) for (int i = 0; i < pen.spots.Length; i++) P(ref pen.spots[i]);
        }
        else if (owner is CoastalYardBoundary yard)
        {
            if (yard.ownerPositions != null) for (int i = 0; i < yard.ownerPositions.Length; i++) P(ref yard.ownerPositions[i]);
        }
        k = at;
    }

    static void LiftPoints(Bake bake, Transform town, Transform authored)
    {
        var owners = new List<Component>();
        owners.AddRange(TownAuthoringScope.Components<CoastalHouseRoute>(town));
        owners.AddRange(TownAuthoringScope.Components<KidPen>(town));
        owners.AddRange(TownAuthoringScope.Components<CoastalYardBoundary>(town));
        foreach (var owner in owners)
        {
            var flat = new List<Vector3>(); int k = 0; Points(owner, flat, ref k, false);
            if (flat.Count == 0) continue;
            var lifted = new List<Vector3>(flat.Count); bool changed = false;
            foreach (var p in flat)
            {
                // an all-zero point means "not set" (a house without a butler), not the south-west corner of the map
                var q = p == Vector3.zero ? p : p + Vector3.up * bake.field.Height(p.x, p.z);
                changed |= q != p; lifted.Add(q);
            }
            if (!changed) continue;
            bake.record.pointOwners.Add(owner); bake.record.pointCounts.Add(flat.Count); bake.record.flatPoints.AddRange(flat);
            k = 0; Points(owner, lifted, ref k, true); EditorUtility.SetDirty(owner);
            bake.points += flat.Count;
        }
    }

    /// <summary>The freshly built (flat) street graph, lifted before it is saved: nodes, parked-car slots, portals.
    /// An edge's length grows with the slope it now climbs.</summary>
    public static void LiftGraph(TownReliefField field, StreetGraph graph)
    {
        if (graph == null || field == null || graph.nodes == null) return;
        var flat = new Vector3[graph.nodes.Length];
        for (int i = 0; i < graph.nodes.Length; i++)
        {
            flat[i] = graph.nodes[i].pos;
            graph.nodes[i].pos = flat[i] + Vector3.up * field.Height(flat[i].x, flat[i].z);
        }
        if (graph.edges != null)
            for (int e = 0; e < graph.edges.Length; e++)
            {
                var edge = graph.edges[e]; if (edge.a >= flat.Length || edge.b >= flat.Length) continue;
                float before = Vector3.Distance(flat[edge.a], flat[edge.b]), after = Vector3.Distance(graph.nodes[edge.a].pos, graph.nodes[edge.b].pos);
                if (before > 0.01f && after > before) { edge.len *= after / before; graph.edges[e] = edge; }
            }
        Vector3 Up(Vector3 p) { return p == Vector3.zero ? p : p + Vector3.up * field.Height(p.x, p.z); }
        if (graph.houses != null)
            for (int i = 0; i < graph.houses.Length; i++)
            {
                var house = graph.houses[i];
                house.carSlot0.position = Up(house.carSlot0.position); house.carSlot1.position = Up(house.carSlot1.position);
                house.butlerPost.position = Up(house.butlerPost.position); house.mailbox = Up(house.mailbox);
                graph.houses[i] = house;
            }
        if (graph.exits != null)
            for (int i = 0; i < graph.exits.Length; i++) { var exit = graph.exits[i]; exit.portal.position = Up(exit.portal.position); graph.exits[i] = exit; }
        EditorUtility.SetDirty(graph);
    }
}

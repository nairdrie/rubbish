using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Replaces the old random backyard fence fragments with property boundaries derived AFTER parcel placement.
/// A short row shares one rear line and its dividers; no clipped fence fragments are ever published. Front gates,
/// paths, driveway openings and KidPen geometry remain authored by the house recipes.
/// Two generators share Rebuild: households of the two starter districts get the connected lot-line network planned
/// by TownLifeFences (one marker per frontage chain); every other lot (Hills, Bluffs, lots without road context such
/// as test fixtures, starter lots the plan could not serve) keeps the legacy row algorithm below, untouched.
/// </summary>
public static class CoastalYardAuthoring
{
    public const string SettingsPath = "Assets/Resources/City/CoastalYards.json";
    public const string SettingsResourcePath = "City/CoastalYards";
    public const string GroupName = "CohesiveBackyards";
    const string Materials = "Assets/Materials/Generated/Neighborhood/";

    [Serializable]
    public sealed class Settings
    {
        public int revision = 1;
        public float sideClearance = 2.2f, rearClearance = 5.5f, minimumRearClearance = 2.5f;
        public float sideGateWidth = 1.8f, sidewalkClearance = 3.6f, maximumNeighbourGap = 15f;
        public float maximumRowAngle = 3f, obstacleClearance = .35f, fenceHeight = 1.15f, sampleSpacing = 1f;
    }

    sealed class Lot
    {
        public CityRoadAnchor anchor;
        public CoastalHouseRoute route;
        public string district;
        public Vector3[] structurePoints;
    }
    sealed class Parcel
    {
        public Lot lot;
        public float left, right, front, back, buildingBack;
    }
    sealed class Row
    {
        public Pose frame;
        public readonly List<Parcel> parcels = new List<Parcel>();
        public Vector3 World(float x, float z) => frame.position + frame.rotation * new Vector3(x, 0, -z);
        public Vector3 Local(Vector3 point)
        {
            var p = Quaternion.Inverse(frame.rotation) * (point - frame.position);
            return new Vector3(p.x, p.y, -p.z);
        }
    }

    public static Settings LoadSettings()
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(SettingsPath);
        if (asset == null) throw new InvalidOperationException("Missing yard settings: " + SettingsPath);
        var settings = JsonUtility.FromJson<Settings>(asset.text);
        if (settings == null || settings.sideClearance < .8f || settings.rearClearance < settings.minimumRearClearance ||
            settings.minimumRearClearance < 1 || settings.sideGateWidth < 1.2f || settings.sidewalkClearance < 3.1f ||
            settings.maximumNeighbourGap < 0 || settings.maximumRowAngle < 0 || settings.maximumRowAngle > 8 ||
            settings.obstacleClearance < .1f || settings.fenceHeight < .5f || settings.fenceHeight > 2 ||
            settings.sampleSpacing < .25f || settings.sampleSpacing > 2)
            throw new InvalidOperationException("Invalid yard dimensions in " + SettingsPath);
        foreach (var f in typeof(Settings).GetFields().Where(f => f.FieldType == typeof(float)))
            if (float.IsNaN((float)f.GetValue(settings)) || float.IsInfinity((float)f.GetValue(settings)))
                throw new InvalidOperationException("Non-finite yard setting " + f.Name);
        return settings;
    }

    /// <summary>Only the legacy random backyard prefab is removed. Never delete a generic Fence/Picket by name.</summary>
    public static bool IsLegacyBackFence(Transform item) => item != null &&
        item.name.StartsWith("SM_garden_fence_long", StringComparison.Ordinal) && item.GetComponentInParent<KidPen>() == null;

    /// <summary>Full-build preflight: obsolete random fences must not push otherwise safe houses away during parcel repair.</summary>
    public static int RemoveLegacyBackFences(Transform town)
    {
        if (Application.isPlaying) throw new InvalidOperationException("Yards are authored in Edit Mode only.");
        if (!CoastalCityLayout.IsCoastal(town)) throw new ArgumentException("Yards require a coastal town.", nameof(town));
        int removed = 0;
        foreach (var anchor in TownAuthoringScope.Components<CityRoadAnchor>(town))
        {
            if (anchor.GetComponentInChildren<CoastalHouseRoute>(true) == null) continue;
            foreach (var child in anchor.GetComponentsInChildren<Transform>(true).Where(IsLegacyBackFence).ToArray())
                if (child != null) { CityEditTransaction.DestroyGenerated(child.gameObject); removed++; }
        }
        Physics.SyncTransforms();
        return removed;
    }

    /// <summary>Call after all lot anchors settle, on both full and explicit incremental city builds.</summary>
    public static string Rebuild(Transform town)
    {
        if (Application.isPlaying) throw new InvalidOperationException("Yards are authored in Edit Mode only.");
        if (!CoastalCityLayout.IsCoastal(town)) throw new ArgumentException("Yards require a coastal town.", nameof(town));
        var settings = LoadSettings();
        string fingerprint = CoastalYardBoundary.SettingsFingerprint();
        var old = TownAuthoringScope.Components<CoastalYardBoundary>(town);
        foreach (var marker in old) CityEditTransaction.DestroyGenerated(marker.gameObject);
        var lots = new List<Lot>(); int removed = RemoveLegacyBackFences(town);
        foreach (var anchor in TownAuthoringScope.Components<CityRoadAnchor>(town).OrderBy(a => a.stableId, StringComparer.Ordinal))
        {
            var route = anchor.GetComponentInChildren<CoastalHouseRoute>(true);
            var stop = route != null ? route.GetComponent<PickupStop>() : null;
            if (stop == null || route.buildingRoot == null) continue;
            string district = stop.District.id;
            if (district != Districts.MildlyPleasant && district != Districts.MinivanMeadows &&
                district != Districts.SnootingtonHills && district != Districts.BillionaireBluffs) continue;
            var points = StructurePoints(anchor, route.buildingRoot);
            if (points.Length > 0) lots.Add(new Lot { anchor = anchor, route = route, district = district, structurePoints = points });
        }
        Physics.SyncTransforms();
        var routes = TownAuthoringScope.Components<CoastalHouseRoute>(town);
        var coverage = new List<Vector3>();
        int homes = 0, connected = 0, skipped = 0;
        var omissions = new List<string>();
        // Starter districts: the planned lot-line network is authoritative (no physics veto). Its colliders exist and
        // are synced before the legacy planner looks at whatever is left, so the two can never cross.
        var planned = BuildNetwork(town, lots, routes, fingerprint, coverage, omissions, ref homes, ref connected, ref skipped, out string networkReport);
        if (planned.Count > 0) { lots.RemoveAll(lot => planned.Contains(lot.anchor.stableId)); Physics.SyncTransforms(); }
        var rows = Rows(lots, settings);
        // Opposing gardens meet midway between their buildings rather than having intersecting rear fences.
        ReconcileBackToBack(rows, settings);
        var accepted = new List<(Row row, List<Vector3> lines)>();
        foreach (var row in rows)
        {
            if (TryCompleteOutline(row, settings, coverage, routes, out var lines, out _))
            {
                accepted.Add((row, lines)); coverage.AddRange(lines);
                homes += row.parcels.Count; if (row.parcels.Count > 1) connected++;
                continue;
            }
            // A corner lot must not sacrifice its neighbors. Retry each complete yard independently, never its fragments.
            foreach (var parcel in row.parcels)
            {
                var single = CreateRow(new[] { parcel.lot }, settings);
                if (TryCompleteOutline(single, settings, coverage, routes, out lines, out string reason))
                { accepted.Add((single, lines)); coverage.AddRange(lines); homes++; }
                else { skipped++; omissions.Add(parcel.lot.anchor.stableId + ": " + reason); }
            }
        }
        // Geometry is created only after planning/checks so earlier new colliders do not bias the later yard plan.
        foreach (var entry in accepted) Build(entry.row, entry.lines, settings, fingerprint);
        Physics.SyncTransforms();
        return "[Coastal yards] " + homes + " homes with complete rear/side boundaries; " + connected +
            " connected garden rows; " + skipped + " constrained lots left deliberately open; removed " + removed + " legacy fence fragments.\n" +
            networkReport + string.Join("\n", omissions);
    }

    // ------------------------------------------------------------------------------- TownLife lot-line network

    /// <summary>Realises TownLifeFences.PlanForTown: one CoastalYardBoundary marker per frontage chain, parented under
    /// the chain's first household with an identity local transform, one BoxCollider per stored run, collider-free
    /// visuals as children. Returns the stop ids that are now served (the caller removes them from the legacy pass).
    /// Never throws for planning or drawing problems: an unserved chain simply stays with the legacy generator.</summary>
    static HashSet<string> BuildNetwork(Transform town, List<Lot> lots, CoastalHouseRoute[] routes, string fingerprint, List<Vector3> coverage,
        List<string> omissions, ref int homes, ref int connected, ref int skipped, out string report)
    {
        var served = new HashSet<string>(StringComparer.Ordinal); report = "";
        TownLifeFences.Result network;
        try { network = TownLifeFences.PlanForTown(town); }
        catch (Exception e) { Debug.LogWarning("[Coastal yards] lot-line planning failed, legacy yards everywhere: " + e); network = null; }
        if (network == null || network.chains == null || network.chains.Length == 0) return served;
        var byId = new Dictionary<string, Lot>(StringComparer.Ordinal);
        foreach (var lot in lots) if (!byId.ContainsKey(lot.anchor.stableId)) byId.Add(lot.anchor.stableId, lot);

        // The kit writes shared fence meshes/materials (assets). It normally has no session open here.
        bool ownSession = !TownLifeKit.Active, kit = true; var previousHook = TownLifeKit.BeforeAssetWrite;
        try
        {
            if (ownSession) { TownLifeKit.BeforeAssetWrite = path => CityEditTransaction.Current?.WatchAsset(path); TownLifeKit.Begin(false); }
            TownLifeFences.DefineStyles();
        }
        catch (Exception e) { kit = false; Debug.LogWarning("[Coastal yards] TownLife kit unavailable, plain panels used: " + e.Message); }
        int chains = 0, runs = 0, gates = 0, dropped = 0; float metres = 0;
        try
        {
            foreach (var chain in network.chains)
            {
                if (chain.stopIds == null || chain.stopIds.Length == 0 || chain.runs == null) continue;
                bool usable = chain.stopIds.Distinct(StringComparer.Ordinal).Count() == chain.stopIds.Length;
                foreach (string id in chain.stopIds)
                    if (served.Contains(id) || !byId.TryGetValue(id, out var member) || member.anchor.roadId != chain.roadId) usable = false;
                if (!usable) { omissions.Add("chain " + chain.id + ": owners do not match the live anchors; legacy yards used"); continue; }
                var safe = new List<TownLifeFences.Run>();
                foreach (var run in chain.runs) if (PlannedRunIsSafe(run, routes)) safe.Add(run); else dropped++;
                if (safe.Count == 0) { omissions.Add("chain " + chain.id + ": no safe run; legacy yards used"); continue; }

                var owner = byId[chain.stopIds[0]];
                var go = new GameObject(GroupName);
                go.transform.SetParent(owner.anchor.transform, false);
                CityEditTransaction.RegisterCreated(go);
                var marker = go.AddComponent<CoastalYardBoundary>();
                marker.stopIds = chain.stopIds.ToArray(); marker.roadId = owner.anchor.roadId;
                marker.layoutFingerprint = CoastalYardBoundary.LayoutFingerprint();
                marker.ownerPositions = chain.stopIds.Select(id => byId[id].anchor.transform.position).ToArray();
                marker.ownerRotations = chain.stopIds.Select(id => byId[id].anchor.transform.rotation).ToArray();
                marker.sourceFingerprint = fingerprint;
                float y = go.transform.position.y, ground = TownLifeContext.GrassY;
                var lines = new List<Vector3>();
                for (int i = 0; i < safe.Count; i++)
                {
                    var run = safe[i];
                    Vector3 a = new Vector3(run.a.x, y, run.a.z), b = new Vector3(run.b.x, y, run.b.z), delta = b - a;
                    float length = delta.magnitude;
                    var holder = new GameObject("PropertyBoundary").transform; holder.SetParent(go.transform, false);
                    holder.SetPositionAndRotation(a, Quaternion.LookRotation(delta / length));
                    // exactly one BoxCollider per stored endpoint pair, on the run itself; the visual below carries none
                    Vector2 size = TownLifeFences.ColliderSize(run.style);
                    var box = holder.gameObject.AddComponent<BoxCollider>();
                    box.center = new Vector3(0, ground - y + size.y * .5f, length * .5f);
                    box.size = new Vector3(size.x, size.y, length);
                    GameObject visual = null;
                    if (kit)
                    {
                        try { visual = TownLifeFences.BuildRunVisual(holder, run, ground, "Fence_" + run.role); }
                        catch (Exception e) { kit = false; Debug.LogWarning("[Coastal yards] TownLife fence visual failed, plain panels used: " + e.Message); }
                    }
                    if (visual == null) PlainRunVisual(holder, length, size.y, ground - y, run.style == TownLifeFences.StyleMeadowsPicket);
                    else foreach (var extra in visual.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(extra);
                    lines.Add(a); lines.Add(b); metres += length; runs++;
                }
                if (kit && chain.gates != null) foreach (var gate in chain.gates)
                {
                    try
                    {
                        var arch = TownLifeFences.BuildGateVisual(go.transform, gate, ground, "Gate_" + gate.stopId);
                        if (arch != null) { foreach (var extra in arch.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(extra); gates++; }
                    }
                    catch (Exception e) { kit = false; Debug.LogWarning("[Coastal yards] TownLife gate visual failed: " + e.Message); }
                }
                marker.boundaryPoints = lines.Select(go.transform.InverseTransformPoint).ToArray();
                coverage.AddRange(lines); chains++;
                foreach (string id in chain.stopIds)
                {
                    served.Add(id);
                    if (network.openYards != null && network.openYards.Contains(id)) skipped++; else homes++;
                }
                if (chain.stopIds.Length > 1) connected++;
            }
        }
        finally
        {
            if (ownSession) { TownLifeKit.End(); TownLifeKit.BeforeAssetWrite = previousHook; }
        }
        report = "[Coastal yards] lot-line network: " + chains + " frontage chains, " + runs + " runs, " + Mathf.RoundToInt(metres) + " m, " + gates + " garden gates" +
            (dropped > 0 ? ", " + dropped + " runs dropped by the final clearance check" : "") + (kit ? "" : " (plain panels: TownLife kit unavailable)") + ".\n";
        if (network.skipped != null) foreach (string note in network.skipped) omissions.Add(note);
        return served;
    }

    /// <summary>Last line of defence for a planned run, pure geometry only (the plan is authoritative; the physics
    /// scan of ClearLine is deliberately NOT applied): never on a carriageway or sidewalk of any road, never within the
    /// 2.6 m driveway corridor or on a household path.</summary>
    static bool PlannedRunIsSafe(TownLifeFences.Run run, CoastalHouseRoute[] routes)
    {
        Vector3 a = run.a, b = run.b; float length = Vector3.Distance(a, b);
        if (run.stopIds == null || run.stopIds.Length == 0 || length < .5f || float.IsNaN(length) || float.IsInfinity(length)) return false;
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / 1f));
        for (int s = 0; s <= samples; s++)
        {
            Vector3 p = Vector3.Lerp(a, b, s / (float)samples);
            foreach (var road in CoastalCityLayout.Roads)
                for (int k = 1; k < road.points.Length; k++)
                    if (CoastalYardRules.DistanceToSegment(p, road.points[k - 1], road.points[k]) < road.halfWidth + 3.2f) return false;
        }
        foreach (var route in routes)
        {
            if (route == null) continue;
            if (CoastalYardRules.PathDistance(a, b, route.door, route.bends, route.pathEnd) < 1.2f) return false;
            if (route.hasDrive && CoastalYardRules.SegmentDistance(a, b, route.ramp, route.top) < 2.6f) return false;
        }
        return true;
    }

    /// <summary>Fallback look of a planned run when the TownLife kit cannot draw it: the legacy picket panels.</summary>
    static void PlainRunVisual(Transform run, float length, float height, float baseY, bool white)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(Materials + "NB_PicketPanel.asset");
        var material = AssetDatabase.LoadAssetAtPath<Material>(Materials + (white ? "NB_White.mat" : "NB_Wood.mat"));
        if (mesh == null || material == null) return;
        int panels = Mathf.Max(1, Mathf.CeilToInt(length / 4)); float each = length / panels;
        for (int p = 0; p < panels; p++)
        {
            var panel = new GameObject("FencePanel"); panel.transform.SetParent(run, false);
            panel.transform.localPosition = new Vector3(0, baseY, p * each);
            panel.transform.localScale = new Vector3(1, height / 1.1f, each / 4);
            panel.AddComponent<MeshFilter>().sharedMesh = mesh;
            panel.AddComponent<MeshRenderer>().sharedMaterial = material;
            GameObjectUtility.SetStaticEditorFlags(panel, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        }
    }

    static List<Row> Rows(List<Lot> lots, Settings settings)
    {
        var remaining = new List<Lot>(lots); var result = new List<Row>();
        while (remaining.Count > 0)
        {
            var group = new List<Lot> { remaining[0] }; remaining.RemoveAt(0);
            bool more;
            do
            {
                more = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var next = remaining[i];
                    if (next.district != group[0].district || next.anchor.roadId != group[0].anchor.roadId ||
                        Mathf.Sign(next.anchor.lateral) != Mathf.Sign(group[0].anchor.lateral) ||
                        Quaternion.Angle(next.anchor.transform.rotation, group[0].anchor.transform.rotation) > settings.maximumRowAngle) continue;
                    if (!group.Any(current => Neighbours(current, next, settings))) continue;
                    group.Add(next); remaining.RemoveAt(i); more = true;
                }
            } while (more);
            result.Add(CreateRow(group, settings));
        }
        return result;
    }

    static bool Neighbours(Lot a, Lot b, Settings settings)
    {
        var frame = new Row { frame = new Pose(a.anchor.transform.position, a.anchor.transform.rotation) };
        var aa = Project(a, frame); var bb = Project(b, frame);
        float gap = Mathf.Max(aa.xMin, bb.xMin) - Mathf.Min(aa.xMax, bb.xMax);
        return gap >= 0 && gap <= settings.maximumNeighbourGap && Mathf.Abs(frame.Local(b.anchor.transform.position).z) < 1.2f;
    }

    static Row CreateRow(IEnumerable<Lot> lots, Settings settings)
    {
        var list = lots.ToArray();
        var row = new Row { frame = new Pose(list[0].anchor.transform.position, list[0].anchor.transform.rotation) };
        foreach (var lot in list)
        {
            Rect b = Project(lot, row);
            var parcel = new Parcel { lot = lot, left = b.xMin - settings.sideClearance, right = b.xMax + settings.sideClearance,
                front = b.yMin + settings.sideGateWidth, buildingBack = b.yMax, back = b.yMax + settings.rearClearance };
            // A backyard starts behind parking, not halfway down its driveway. Using the authored drive top
            // protects the opening even when the household car happens to be out driving during a preview.
            if (lot.route.hasDrive) parcel.front = Mathf.Max(parcel.front, row.Local(lot.route.top).z + Mathf.Max(2.8f, settings.sideGateWidth));
            foreach (var car in lot.anchor.GetComponentsInChildren<HouseholdCar>(true))
                foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
                {
                    var carBounds = renderer.localBounds;
                    for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++)
                    {
                        var point = row.Local(renderer.transform.TransformPoint(new Vector3(x == 0 ? carBounds.min.x : carBounds.max.x,
                            carBounds.center.y, z == 0 ? carBounds.min.z : carBounds.max.z)));
                        parcel.front = Mathf.Max(parcel.front, point.z + settings.sideGateWidth);
                    }
                }
            // A trampoline/pool belongs INSIDE its garden, not stranded behind the new boundary.
            // Trees and distant decorative estate features do not arbitrarily inflate the footprint.
            foreach (var t in lot.anchor.GetComponentsInChildren<Transform>(true))
            {
                if (!(t.name == "Trampoline" || t.name == "TennisCourt" || t.name == "InfinityPool" ||
                    t.name.StartsWith("SM_swimming_pool", StringComparison.Ordinal) ||
                    t.name.StartsWith("SM_whirlpool", StringComparison.Ordinal) ||
                    t.name.StartsWith("SM_clothes_drying", StringComparison.Ordinal))) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    var world = r.bounds;
                    for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++)
                    {
                        var p = row.Local(new Vector3(x == 0 ? world.min.x : world.max.x, 0, z == 0 ? world.min.z : world.max.z));
                        if (p.z < b.yMax - 1 || p.z > b.yMax + 22) continue;
                        parcel.left = Mathf.Min(parcel.left, p.x - 1); parcel.right = Mathf.Max(parcel.right, p.x + 1);
                        parcel.back = Mathf.Max(parcel.back, p.z + 1);
                    }
                }
            }
            row.parcels.Add(parcel);
        }
        row.parcels.Sort((a, b) => a.left.CompareTo(b.left));
        float commonBack = row.parcels.Max(p => p.back);
        foreach (var parcel in row.parcels) parcel.back = commonBack;
        for (int i = 1; i < row.parcels.Count; i++)
        {
            var left = row.parcels[i - 1]; var right = row.parcels[i];
            float shared = (left.right + right.left) * .5f;
            left.right = right.left = shared;
        }
        return row;
    }

    static void ReconcileBackToBack(List<Row> rows, Settings settings)
    {
        for (int i = 0; i < rows.Count; i++) for (int j = i + 1; j < rows.Count; j++)
        {
            var a = rows[i]; var b = rows[j];
            if (Vector3.Dot(a.frame.rotation * Vector3.forward, b.frame.rotation * Vector3.forward) > -.9986f) continue;
            foreach (var ap in a.parcels) foreach (var bp in b.parcels)
            {
                var bl = a.Local(b.World(bp.left, bp.back)); var br = a.Local(b.World(bp.right, bp.back));
                if (Mathf.Min(Mathf.Max(bl.x, br.x), ap.right) - Mathf.Max(Mathf.Min(bl.x, br.x), ap.left) < 1) continue;
                float bRear = a.Local(b.World(0, bp.buildingBack)).z;
                float available = bRear - ap.buildingBack;
                if (available < settings.minimumRearClearance * 2 || bl.z >= ap.back) continue;
                float shared = (ap.buildingBack + bRear) * .5f;
                ap.back = Mathf.Min(ap.back, shared);
                bp.back = Mathf.Min(bp.back, b.Local(a.World(0, shared)).z);
            }
        }
    }

    static bool TryCompleteOutline(Row row, Settings settings, List<Vector3> coverage, CoastalHouseRoute[] routes,
        out List<Vector3> output, out string reason)
    {
        reason = "insufficient space for a complete garden";
        var rejected = new List<string>();
        // Only shrink rear garden depth as a whole; preserve building and side-gate clearance.
        var backs = row.parcels.Select(p => p.back).ToArray();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var lines = new List<Vector3>();
            bool complete = true;
            for (int i = 0; i < row.parcels.Count; i++)
            {
                var p = row.parcels[i];
                float rear = Mathf.Max(p.buildingBack + settings.minimumRearClearance, backs[i] - attempt);
                if (rear <= p.front + .5f) { reason = "driveway occupies the available garden depth"; complete = false; break; }
                float leftStart = i > 0 ? Mathf.Max(p.front, row.parcels[i - 1].front) : p.front;
                float rightStart = i + 1 < row.parcels.Count ? Mathf.Max(p.front, row.parcels[i + 1].front) : p.front;
                Add(p.left, leftStart, p.left, rear);
                Add(p.left, rear, p.right, rear);
                Add(p.right, rightStart, p.right, rear);
                void Add(float x0, float z0, float x1, float z1)
                {
                    var existing = coverage.Concat(lines).ToArray();
                    lines.AddRange(CoastalYardRules.UncoveredSegments(row.World(x0, z0), row.World(x1, z1), existing));
                }
            }
            if (!complete) { rejected.Add(reason); continue; }
            bool okay = true;
            for (int i = 0; i < lines.Count && okay; i += 2)
            {
                if (!ClearLine(lines[i], lines[i + 1], settings, routes, out reason)) { okay = false; break; }
                for (int j = 0; j < coverage.Count; j += 2)
                    if (CoastalYardRules.CrossesBoundary(lines[i], lines[i + 1], coverage[j], coverage[j + 1]))
                    { reason = "crosses a neighboring garden boundary"; okay = false; break; }
            }
            if (okay && lines.Count > 0) { output = lines; return true; }
            if (!string.IsNullOrEmpty(reason)) rejected.Add(reason);
        }
        reason = string.Join("; ", rejected.Distinct());
        output = null; return false;
    }

    static bool ClearLine(Vector3 a, Vector3 b, Settings settings, CoastalHouseRoute[] routes, out string reason)
    {
        reason = null;
        int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / settings.sampleSpacing));
        for (int s = 0; s <= samples; s++)
        {
            Vector3 p = Vector3.Lerp(a, b, s / (float)samples);
            foreach (var road in CoastalCityLayout.Roads)
                for (int k = 1; k < road.points.Length; k++)
                    if (CoastalYardRules.DistanceToSegment(p, road.points[k - 1], road.points[k]) < road.halfWidth + settings.sidewalkClearance)
                    { reason = "road/sidewalk clearance: " + road.id; return false; }
        }
        foreach (var route in routes)
        {
            if (CoastalYardRules.PathDistance(a, b, route.door, route.bends, route.pathEnd) < 1.2f)
            { reason = "household walking path: " + route.GetComponent<PickupStop>()?.stopId; return false; }
            if (route.hasDrive && CoastalYardRules.SegmentDistance(a, b, route.ramp, route.top) < 2.6f)
            { reason = "driveway corridor: " + route.GetComponent<PickupStop>()?.stopId; return false; }
        }
        Vector3 delta = b - a; delta.y = 0;
        var center = (a + b) * .5f + Vector3.up * (settings.fenceHeight * .5f + .12f);
        var half = new Vector3(settings.obstacleClearance, settings.fenceHeight * .45f, delta.magnitude * .5f + .1f);
        foreach (var collider in Physics.OverlapBox(center, half, Quaternion.LookRotation(delta.normalized), ~0, QueryTriggerInteraction.Ignore))
        {
            if (!collider.enabled || collider.bounds.max.y < a.y + .35f) continue;
            // Existing authored gate/picket/child pen fences are solid and protected like every other obstacle.
            reason = "solid " + collider.name + " at " + (collider.GetComponentInParent<CityRoadAnchor>()?.stableId ?? "world");
            return false;
        }
        return true;
    }

    static Vector3[] StructurePoints(CityRoadAnchor anchor, Transform building)
    {
        var structures = new HashSet<Transform> { building };
        foreach (var t in anchor.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("SM_house", StringComparison.Ordinal) || t.name == "Hall") structures.Add(t);
        var renderers = new HashSet<Renderer>(); var points = new List<Vector3>();
        foreach (var structure in structures) foreach (var r in structure.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer || !renderers.Add(r)) continue;
            // Local mesh bounds avoid a rotated house's world-AABB unnecessarily swallowing the whole garden.
            var b = r.localBounds;
            for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++) for (int y = 0; y < 2; y++)
                points.Add(r.transform.TransformPoint(new Vector3(x == 0 ? b.min.x : b.max.x, y == 0 ? b.min.y : b.max.y, z == 0 ? b.min.z : b.max.z)));
        }
        return points.ToArray();
    }

    static Rect Project(Lot lot, Row row)
    {
        var points = lot.structurePoints.Select(row.Local).ToArray();
        return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.z), points.Max(p => p.x), points.Max(p => p.z));
    }

    static void Build(Row row, List<Vector3> lines, Settings settings, string fingerprint)
    {
        var owner = row.parcels[0].lot;
        var go = new GameObject(GroupName);
        go.transform.SetParent(owner.anchor.transform, false);
        CityEditTransaction.RegisterCreated(go);
        var marker = go.AddComponent<CoastalYardBoundary>();
        marker.stopIds = row.parcels.Select(p => p.lot.anchor.stableId).ToArray(); marker.roadId = owner.anchor.roadId;
        marker.layoutFingerprint = CoastalYardBoundary.LayoutFingerprint();
        marker.ownerPositions = row.parcels.Select(p => p.lot.anchor.transform.position).ToArray();
        marker.ownerRotations = row.parcels.Select(p => p.lot.anchor.transform.rotation).ToArray();
        marker.sourceFingerprint = fingerprint; marker.boundaryPoints = lines.Select(go.transform.InverseTransformPoint).ToArray();
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(Materials + "NB_PicketPanel.asset");
        if (mesh == null) throw new InvalidOperationException("Missing authored picket mesh for coherent yards.");
        var material = AssetDatabase.LoadAssetAtPath<Material>(Materials + (owner.district == Districts.MinivanMeadows ? "NB_White.mat" : "NB_Wood.mat"));
        for (int i = 0; i < lines.Count; i += 2)
        {
            Vector3 a = lines[i], b = lines[i + 1], delta = b - a;
            float length = delta.magnitude;
            var run = new GameObject("PropertyBoundary").transform; run.SetParent(go.transform, false);
            run.SetPositionAndRotation(a, Quaternion.LookRotation(delta.normalized));
            int panels = Mathf.Max(1, Mathf.CeilToInt(length / 4)); float each = length / panels;
            for (int p = 0; p < panels; p++)
            {
                var panel = new GameObject("FencePanel"); panel.transform.SetParent(run, false);
                panel.transform.localPosition = Vector3.forward * (p * each);
                panel.transform.localScale = new Vector3(1, settings.fenceHeight / 1.1f, each / 4);
                panel.AddComponent<MeshFilter>().sharedMesh = mesh;
                panel.AddComponent<MeshRenderer>().sharedMaterial = material;
                GameObjectUtility.SetStaticEditorFlags(panel, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            }
            var box = run.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0, settings.fenceHeight * .5f, length * .5f);
            box.size = new Vector3(.16f, settings.fenceHeight, length);
            // Deliberate posts finish every run and frame the side access opening; not a lone orphan panel.
            Post(run, Vector3.zero, settings, material); Post(run, Vector3.forward * length, settings, material);
        }
    }

    static void Post(Transform parent, Vector3 at, Settings settings, Material material)
    {
        var post = GameObject.CreatePrimitive(PrimitiveType.Cube); post.name = "BoundaryEndPost";
        post.transform.SetParent(parent, false); post.transform.localPosition = at + Vector3.up * ((settings.fenceHeight + .12f) * .5f);
        post.transform.localScale = new Vector3(.18f, settings.fenceHeight + .12f, .18f);
        post.GetComponent<MeshRenderer>().sharedMaterial = material;
        Object.DestroyImmediate(post.GetComponent<Collider>());
        GameObjectUtility.SetStaticEditorFlags(post, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
    }
}

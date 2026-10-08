using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Gameplay contracts behind the Town Editor's stamps. Property stamps include real collection and
/// resident routes; decoration never masquerades as a household. All publication goes through the city's
/// transactional graph/directory/map publisher, including road edits and dirty hand-authored drafts.</summary>
public static class TownGameplayAuthoring
{
    public const int MaximumHouseholds = 2000;
    public const float MaximumRoadDistance = 60f;
    const string BinPrefab = "Assets/Suburbs Town Pack/PREFABS/Props/SM_garbagecan_01.prefab";

    public static bool IsProperty(TownAssetEntry entry) => entry != null &&
        (entry.role == TownAssetRole.House || entry.role == TownAssetRole.CollectionBuilding);

    public static string Publish()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before publishing town edits.");
        var scene = SceneManager.GetSceneByPath(NeighborhoodBuilder.DefaultScenePath);
        if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the Neighborhood scene in the Town Editor first.");
        return NeighborhoodBuilder.ApplyCoastalChanges(NeighborhoodBuilder.DefaultScenePath, true);
    }

    public static bool PreviewHousePose(TownAssetEntry entry, Vector3 requestedPoint, out Pose pose, out string reason)
    {
        pose = default;
        if (!IsProperty(entry)) { reason = "Select a working property first."; return false; }
        if (!TryRoadAttachment(requestedPoint, out var road, out float u, out int side, out reason)) return false;
        pose = CityRoadAnchor.Resolve(road, u, side * (road.halfWidth + .8f), .15f, side > 0 ? -90 : 90);
        return true;
    }

    /// <summary>The complete kit's actual local extent, including driveway/frontage, at its road-snapped pose.
    /// Uses existing asset data only: no preview instantiation, scene mutation, physics or generated IDs.</summary>
    public static bool PreviewProperty(TownAssetEntry entry, Vector3 requestedPoint, out Pose pose, out Bounds localBounds, out string reason)
    {
        localBounds = default;
        if (!PreviewHousePose(entry, requestedPoint, out pose, out reason)) return false;
        try
        {
            if (entry.role == TownAssetRole.House)
            {
                var scene = SceneManager.GetSceneByPath(NeighborhoodBuilder.DefaultScenePath);
                var template = scene.IsValid() && scene.isLoaded ? FindHouseTemplate(scene, entry.houseModel) : null;
                if (template == null) { reason = "No complete property template is available for this house."; return false; }
                localBounds = LocalVisualBounds(template.transform);
            }
            else
            {
                if (entry.prefab == null) { reason = "This building's prefab is missing."; return false; }
                var raw = LocalVisualBounds(entry.prefab.transform); var rotation = Quaternion.Euler(0, entry.propertyFrontYaw, 0);
                bool first = true;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    var p = rotation * new Vector3(x == 0 ? raw.min.x : raw.max.x, y == 0 ? raw.min.y : raw.max.y, z == 0 ? raw.min.z : raw.max.z);
                    if (first) { localBounds = new Bounds(p, Vector3.zero); first = false; } else localBounds.Encapsulate(p);
                }
                localBounds.center += new Vector3(-localBounds.center.x, -localBounds.min.y, -4f - localBounds.max.z);
                localBounds.Encapsulate(new Vector3(-3.8f, 0, .4f)); localBounds.Encapsulate(new Vector3(1.2f, 1.7f, 0));
            }
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    public static bool MoveProperty(TownPlacedAsset marker, Vector3 requestedPoint, out string error)
    {
        error = null;
        if (marker == null || EditorApplication.isPlayingOrWillChangePlaymode ||
            marker.gameObject.scene.path != NeighborhoodBuilder.DefaultScenePath)
        { error = "Select a working property in the Neighborhood scene, outside Play Mode."; return false; }
        var anchor = marker.GetComponent<CityRoadAnchor>();
        if (anchor == null || marker.GetComponentsInChildren<PickupStop>(true).Length != 1)
        { error = "This stamp is not a working property."; return false; }
        if (!TryRoadAttachment(requestedPoint, out var road, out float u, out int side, out error)) return false;
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Move working property");
        Undo.RegisterFullObjectHierarchyUndo(marker.gameObject, "Move working property");
        try
        {
            // A shared row's generated fence may be parented to this property's anchor but also belongs to
            // its neighbours. It is not part of the movable kit; Save & Bake derives a fresh shared row.
            foreach (var yard in marker.GetComponentsInChildren<CoastalYardBoundary>(true)) Undo.DestroyObjectImmediate(yard.gameObject);
            ConfigureAnchor(anchor, anchor.stableId, road, u, side);
            if (!anchor.Apply()) throw new InvalidOperationException("The property could not attach to this road.");
            var stop = marker.GetComponentInChildren<PickupStop>(true);
            RewriteHouseholdIdentity(marker.gameObject, stop.stopId, road.districtId);
            Physics.SyncTransforms();
            if (!PlacementIsClear(marker.gameObject, true, out error)) throw new InvalidOperationException(error);
            EditorSceneManager.MarkSceneDirty(marker.gameObject.scene); Undo.CollapseUndoOperations(group); return true;
        }
        catch (Exception e)
        {
            Undo.RevertAllDownToGroup(group); Physics.SyncTransforms(); error = e.Message; return false;
        }
    }

    /// <summary>Nearest land road, with an explicit signed curb attachment. No snapping across a bridge or
    /// inventing routes through open countryside. Returned u remains stable when the road is edited.</summary>
    public static bool TryRoadAttachment(Vector3 point, out CoastalCityLayout.RoadDef road, out float u, out int side, out string reason)
    {
        road = null; u = 0; side = 1; reason = null;
        if (!Finite(point)) { reason = "Placement must have finite coordinates."; return false; }
        float best = float.PositiveInfinity;
        foreach (var candidate in CoastalCityLayout.Roads)
        {
            if (candidate.bridge) continue;
            float walked = 0, length = candidate.Length;
            for (int i = 1; i < candidate.points.Length; i++)
            {
                var a = candidate.points[i - 1]; var delta = candidate.points[i] - a;
                float segment = delta.magnitude;
                if (segment < .001f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(point - a, delta) / delta.sqrMagnitude);
                var nearest = a + delta * t; float distance = new Vector2(nearest.x - point.x, nearest.z - point.z).magnitude;
                if (distance < best)
                {
                    road = candidate; best = distance; u = (walked + t * segment) / length;
                    side = Vector3.Dot(point - nearest, Vector3.Cross(Vector3.up, delta.normalized)) < 0 ? -1 : 1;
                }
                walked += segment;
            }
        }
        if (road == null || best > MaximumRoadDistance)
        { reason = "Working properties need a land road within " + MaximumRoadDistance + " m. Draw a connected street first."; return false; }
        // Keep the property curb away from the very end of a dead end/junction; the collision preflight also
        // rejects a driveway or house which still intersects another road at a corner.
        float inset = Mathf.Min(.1f, 7f / road.Length);
        u = Mathf.Clamp(u, inset, 1f - inset);
        return true;
    }

    public static GameObject Create(TownAssetEntry entry, Vector3 position, Quaternion rotation, float scale, Transform parent, out string error, bool allowSceneryOverlap = false)
    {
        error = null;
        if (EditorApplication.isPlayingOrWillChangePlaymode) { error = "Town stamps are placed in Edit Mode."; return null; }
        if (entry == null || parent == null || !parent.gameObject.scene.IsValid() || !Finite(position) ||
            !Finite(scale) || scale <= 0 || !Finite(rotation))
        { error = "Select a valid asset and Neighborhood authoring layer."; return null; }
        if (parent.gameObject.scene.path != NeighborhoodBuilder.DefaultScenePath)
        { error = "Town stamps can only be added to the Neighborhood scene."; return null; }
        GameObject created = null;
        try
        {
            if (IsProperty(entry))
            {
                if (SceneComponents<PickupStop>(parent.gameObject.scene).Length >= MaximumHouseholds)
                    throw new InvalidOperationException("This town has reached the " + MaximumHouseholds + " household safety limit.");
                if (!TryRoadAttachment(position, out var road, out float u, out int side, out error)) return null;
                if (entry.role == TownAssetRole.House)
                {
                    var source = FindHouseTemplate(parent.gameObject.scene, entry.houseModel);
                    if (source == null) throw new InvalidOperationException("This house has no complete property recipe in the current town. Select a different house or register it as a custom working property.");
                    created = Object.Instantiate(source.gameObject, parent, true);
                    foreach (var yard in created.GetComponentsInChildren<CoastalYardBoundary>(true)) Object.DestroyImmediate(yard.gameObject);
                    // A cloned full property carries world-coordinate metadata. CityRoadAnchor.Apply moves
                    // those fields from the original frame alongside its transform hierarchy.
                    foreach (var stamp in created.GetComponentsInChildren<TownPlacedAsset>(true)) Object.DestroyImmediate(stamp);
                    var anchor = created.GetComponent<CityRoadAnchor>();
                    string stopId = NewStopId();
                    RewriteHouseholdIdentity(created, stopId, road.districtId);
                    ConfigureAnchor(anchor, stopId, road, u, side);
                    if (!anchor.Apply()) throw new InvalidOperationException("The selected house could not attach to this street.");
                }
                else created = CreateCollectionBuilding(entry, parent, road, u, side);
                created.name = entry.displayName + " · " + created.GetComponent<CityRoadAnchor>().stableId;
                AssignUnusedKnockIds(created);
                Physics.SyncTransforms();
                if (!PlacementIsClear(created, true, out error)) { Object.DestroyImmediate(created); return null; }
            }
            else
            {
                if (entry.prefab == null) throw new InvalidOperationException("This palette item is missing its prefab.");
                if (!TownAssetLibrary.ValidateVisualContents(entry.prefab, out string invalidAsset)) throw new InvalidOperationException(invalidAsset);
                created = new GameObject(entry.displayName ?? entry.prefab.name);
                created.transform.SetParent(parent, false);
                created.transform.SetPositionAndRotation(position, rotation);
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab, created.transform);
                visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.identity; visual.transform.localScale = Vector3.one;
                var bounds = LocalVisualBounds(visual.transform);
                visual.transform.localPosition = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * scale;
                visual.transform.localScale = Vector3.one * scale;
                RejectEmbeddedGameplay(visual);
                EnsureColliders(visual, entry.role == TownAssetRole.StreetProp);
                if (entry.role == TownAssetRole.StreetProp)
                {
                    var knock = created.AddComponent<Knockable>();
                    knock.kind = PropKind(entry.prefab.name); knock.proxyKey = ""; // baked mesh retains the selected asset's exact appearance
                    knock.district = WorldLifeIds.DistrictIndex(DistrictAt(position));
                    knock.restColliders = created.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger).ToArray();
                    knock.renderers = created.GetComponentsInChildren<Renderer>(true);
                    AssignUnusedKnockIds(created);
                }
                Physics.SyncTransforms();
                if (!PlacementIsClear(created, false, out error, allowSceneryOverlap)) { Object.DestroyImmediate(created); return null; }
            }
            return created;
        }
        catch (Exception e)
        {
            if (created != null) Object.DestroyImmediate(created);
            error = e.Message; return null;
        }
    }

    // WorldState serializes stopId as FixedString32Bytes (29 UTF-8 payload bytes). An unshortened GUID
    // would work for the host but silently truncate on guests, orphaning every pasted property's bags.
    public static string NewStopId() => "user-" + Guid.NewGuid().ToString("N").Substring(0, 20);

    public static CityRoadAnchor FindHouseTemplate(Scene scene, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        return SceneComponents<CoastalHouseRoute>(scene)
            .Where(r => r.buildingRoot != null && r.GetComponent<PickupStop>() != null &&
                r.GetComponentInParent<TownPlacedAsset>() == null &&
                (r.buildingRoot.name == model || r.buildingRoot.GetComponentsInChildren<Transform>(true).Any(t => t.name == model)))
            .OrderBy(r => r.buildingRoot.name == model ? 0 : 1)
            .ThenBy(r => r.GetComponent<PickupStop>().stopId, StringComparer.Ordinal)
            .Select(r => r.GetComponentInParent<CityRoadAnchor>()).FirstOrDefault(a => a != null);
    }

    /// <summary>Every source identity is replaced, including parked twins and fenced yard residents.</summary>
    public static void RewriteHouseholdIdentity(GameObject property, string stopId, string districtId)
    {
        var stops = property.GetComponentsInChildren<PickupStop>(true);
        if (stops.Length != 1) throw new InvalidOperationException("A working property must have exactly one curb stop.");
        var stop = stops[0]; stop.stopId = stopId; stop.districtId = districtId; stop.onRoute = true;
        stop.gameObject.name = "Stop_" + stopId;
        stop.address = "New property " + stopId.Substring(Mathf.Max(0, stopId.Length - 6));
        // The legacy map scanner still joins Lot_<identity> names to curb IDs. Preserve its contract too,
        // otherwise a cloned lot overwrites the source property's minimap footprint dictionary entry.
        foreach (var child in property.GetComponentsInChildren<Transform>(true))
            if (child.name.StartsWith("Lot_", StringComparison.Ordinal)) child.name = "Lot_" + stopId;
        byte district = WorldLifeIds.DistrictIndex(districtId);
        foreach (var car in property.GetComponentsInChildren<HouseholdCar>(true)) { car.stopId = stopId; car.district = district; }
        foreach (var pen in property.GetComponentsInChildren<KidPen>(true)) { pen.stopId = stopId; pen.penId = stopId; pen.district = district; }
        foreach (var prop in property.GetComponentsInChildren<Knockable>(true)) { prop.stopId = stopId; prop.district = district; }
        var anchor = property.GetComponent<CityRoadAnchor>(); if (anchor != null) anchor.stableId = stopId;
    }

    static void ConfigureAnchor(CityRoadAnchor anchor, string id, CoastalCityLayout.RoadDef road, float u, int side)
    {
        anchor.stableId = id; anchor.roadId = road.id; anchor.u = u; anchor.lateralFromRoadEdge = true;
        anchor.lateral = side * .8f; anchor.height = .15f; anchor.yawOffset = side > 0 ? -90 : 90;
    }

    static GameObject CreateCollectionBuilding(TownAssetEntry entry, Transform parent, CoastalCityLayout.RoadDef road, float u, int side)
    {
        if (entry.prefab == null) throw new InvalidOperationException("The selected working building has no prefab.");
        if (!TownAssetLibrary.ValidateVisualContents(entry.prefab, out string invalidAsset)) throw new InvalidOperationException(invalidAsset);
        var root = new GameObject(entry.displayName); root.transform.SetParent(parent, false);
        try
        {
            string id = NewStopId();
            var anchor = root.AddComponent<CityRoadAnchor>(); ConfigureAnchor(anchor, id, road, u, side); anchor.Apply();
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab, root.transform);
            visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.Euler(0, entry.propertyFrontYaw, 0); visual.transform.localScale = Vector3.one;
            RejectEmbeddedGameplay(visual);
            var bounds = LocalVisualBounds(root.transform);
            // Assets face local +Z; align that facade with the curb. A 4 m pedestrian apron keeps the
            // entry connected but leaves wheelie bins and all six curb slots outside the doorway.
            const float apron = 4f;
            visual.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -apron - bounds.max.z);
            EnsureColliders(visual, false);
            var path = GameObject.CreatePrimitive(PrimitiveType.Cube); path.name = "Front door path";
            path.transform.SetParent(root.transform, false); path.transform.localPosition = new Vector3(0, -.10f, -apron * .5f);
            path.transform.localScale = new Vector3(2.2f, .16f, apron); path.layer = 6;
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Generated/Neighborhood/NB_ParkPath.mat");
            if (material != null) path.GetComponent<Renderer>().sharedMaterial = material;
            var stopObject = new GameObject("Stop_" + id); stopObject.transform.SetParent(root.transform, false);
            var stop = stopObject.AddComponent<PickupStop>(); stop.stopId = id; stop.address = entry.displayName + " · " + id.Substring(id.Length - 6);
            stop.districtId = road.districtId; stop.onRoute = true; stop.bagCount = entry.collectionType == "house" ? 4 : 6;
            stop.wealth = entry.collectionType == "tower" ? .7f : entry.collectionType == "house" ? .3f : .52f;
            var route = stopObject.AddComponent<CoastalHouseRoute>(); route.buildingRoot = visual.transform;
            route.door = root.transform.TransformPoint(new Vector3(0, 0, -apron)); route.pathEnd = root.transform.TransformPoint(new Vector3(0, 0, -2));
            route.mailbox = root.transform.TransformPoint(new Vector3(2, 0, -2)); route.bends = Array.Empty<Vector3>();
            route.size = (byte)(entry.collectionType == "tower" ? 2 : 1);
            route.car0 = route.car1 = route.butlerPost = new Pose(route.door, root.transform.rotation);
            route.ramp = route.top = route.pathEnd;
            var binAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BinPrefab);
            if (binAsset == null) throw new InvalidOperationException("The required wheelie-bin prefab is missing.");
            var bin = (GameObject)PrefabUtility.InstantiatePrefab(binAsset, root.transform); bin.transform.localPosition = new Vector3(-3, 0, -.35f);
            bin.transform.localRotation = Quaternion.Euler(0, 180, 0); bin.transform.localScale = Vector3.one;
            EnsureColliders(bin, true); stop.bin = bin.transform;
            var knock = bin.AddComponent<Knockable>(); knock.kind = KnockKind.WheelieBin; knock.stopId = id;
            knock.district = WorldLifeIds.DistrictIndex(road.districtId); knock.proxyKey = WorldLifeIds.KnockPrefabPrefix + binAsset.name;
            knock.restColliders = bin.GetComponentsInChildren<Collider>(true); knock.renderers = bin.GetComponentsInChildren<Renderer>(true);
            return root;
        }
        catch { Object.DestroyImmediate(root); throw; }
    }

    public static Bounds LocalVisualBounds(Transform root)
    {
        bool any = false; Bounds result = default;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer || !renderer.enabled || renderer.GetComponentInParent<CoastalYardBoundary>() != null) continue;
            var b = renderer.localBounds;
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            {
                var p = root.InverseTransformPoint(renderer.transform.TransformPoint(new Vector3(x == 0 ? b.min.x : b.max.x,
                    y == 0 ? b.min.y : b.max.y, z == 0 ? b.min.z : b.max.z)));
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; } else result.Encapsulate(p);
            }
        }
        if (!any || result.size.sqrMagnitude < .0001f) throw new InvalidOperationException("The prefab needs visible 3D geometry.");
        return result;
    }

    static void RejectEmbeddedGameplay(GameObject prefabInstance)
    {
        if (!TownAssetLibrary.ValidateVisualContents(prefabInstance, out string invalidAsset)) throw new InvalidOperationException(invalidAsset);
        if (prefabInstance.GetComponentsInChildren<PickupStop>(true).Length > 0 ||
            prefabInstance.GetComponentsInChildren<CityRoadAnchor>(true).Length > 0 ||
            prefabInstance.GetComponentsInChildren<HouseholdCar>(true).Length > 0 ||
            prefabInstance.GetComponentsInChildren<Knockable>(true).Length > 0 ||
            prefabInstance.GetComponentsInChildren<TownPlacedAsset>(true).Length > 0)
            throw new InvalidOperationException("This custom prefab contains existing gameplay identities. Register a visual-only prefab; the editor creates fresh gameplay components.");
        foreach (var body in prefabInstance.GetComponentsInChildren<Rigidbody>(true))
            if (!body.isKinematic) throw new InvalidOperationException("Town assets must be static visual prefabs, not live physics actors.");
    }

    static void EnsureColliders(GameObject visual, bool primitiveOnly)
    {
        if (primitiveOnly)
            foreach (var mesh in visual.GetComponentsInChildren<MeshCollider>(true)) Object.DestroyImmediate(mesh);
        if (visual.GetComponentsInChildren<Collider>(true).Any(c => c.enabled && !c.isTrigger)) return;
        var b = LocalVisualBounds(visual.transform);
        var box = visual.AddComponent<BoxCollider>(); box.center = b.center; box.size = b.size;
    }

    public static void AssignUnusedKnockIds(GameObject added)
    {
        var used = new HashSet<ushort>();
        foreach (var prop in SceneComponents<Knockable>(added.scene))
            if (!prop.transform.IsChildOf(added.transform)) used.Add(prop.id);
        int next = 0;
        foreach (var prop in added.GetComponentsInChildren<Knockable>(true))
        {
            while (next <= ushort.MaxValue && used.Contains((ushort)next)) next++;
            if (next > ushort.MaxValue) throw new InvalidOperationException("The town has no free knockable IDs.");
            prop.id = (ushort)next++; used.Add(prop.id);
        }
    }

    public static List<string> ValidateAuthored(Transform town)
    {
        var errors = new List<string>();
        if (town == null) return errors;
        if (town.gameObject.scene.GetRootGameObjects().Count(root => root.name == TownAuthoringScope.RootName) > 1)
            errors.Add("The scene has more than one TownAuthored root. Keep all hand-authored stamps in one layer before publishing.");
        var authored = TownAuthoringScope.AuthoredRoot(town);
        foreach (var marker in SceneComponents<TownPlacedAsset>(town.gameObject.scene))
            if (authored == null || !marker.transform.IsChildOf(authored))
                errors.Add(marker.name + ": move this stamp back under TownAuthored so full regeneration preserves it.");
        if (authored == null) return errors;
        var stops = TownAuthoringScope.Components<PickupStop>(town);
        if (stops.Length > MaximumHouseholds) errors.Add("Town exceeds the " + MaximumHouseholds + " household safety limit.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placed in authored.GetComponentsInChildren<TownPlacedAsset>(true))
        {
            if (string.IsNullOrEmpty(placed.placementId) || !ids.Add(placed.placementId)) errors.Add(placed.name + ": missing/duplicate placement identity. Use the Town Editor Duplicate action.");
            if (!Finite(placed.transform.position) || !Finite(placed.transform.rotation) || !Finite(placed.transform.lossyScale)) errors.Add(placed.name + ": non-finite transform.");
            bool property = placed.role == TownAssetRole.House || placed.role == TownAssetRole.CollectionBuilding;
            if (placed.role == TownAssetRole.CollectionBuilding)
            {
                var route = placed.GetComponentInChildren<CoastalHouseRoute>(true);
                if (route != null && route.buildingRoot != null && !TownAssetLibrary.ValidateVisualContents(route.buildingRoot.gameObject, out string badVisual))
                    errors.Add(placed.name + ": " + badVisual);
            }
            else if (!property)
            {
                foreach (Transform visualRoot in placed.transform)
                    if (!TownAssetLibrary.ValidateVisualContents(visualRoot.gameObject, out string badVisual)) errors.Add(placed.name + ": " + badVisual);
            }
            if (property && (placed.GetComponent<CityRoadAnchor>() == null || placed.GetComponentsInChildren<PickupStop>(true).Length != 1))
                errors.Add(placed.name + ": working property must contain one road anchor and curb stop.");
            if (property && Vector3.Distance(placed.transform.lossyScale, Vector3.one) > .001f)
                errors.Add(placed.name + ": working properties must remain life-sized; scaling would invalidate world-space routes.");
            if (!PlacementIsClear(placed.gameObject, property, out string reason, placed.allowSceneryOverlap)) errors.Add(placed.name + ": " + reason);
        }
        var knockIds = new HashSet<ushort>();
        foreach (var prop in SceneComponents<Knockable>(town.gameObject.scene))
            if (!knockIds.Add(prop.id)) errors.Add("Duplicate knockable id " + prop.id + " at " + prop.name);
        foreach (var stop in authored.GetComponentsInChildren<PickupStop>(true))
        {
            if (string.IsNullOrEmpty(stop.stopId) || System.Text.Encoding.UTF8.GetByteCount(stop.stopId) > 29)
                errors.Add(stop.name + ": collection identity exceeds the multiplayer 29-byte limit.");
            var anchor = stop.GetComponentInParent<CityRoadAnchor>();
            if (anchor == null || anchor.stableId != stop.stopId) errors.Add(stop.name + ": property and curb identities differ.");
            if (string.IsNullOrEmpty(stop.districtId) || !Districts.All.Any(d => d.id == stop.districtId)) errors.Add(stop.stopId + ": missing/unknown collection district.");
            foreach (var car in anchor != null ? anchor.GetComponentsInChildren<HouseholdCar>(true) : Array.Empty<HouseholdCar>())
                if (car.stopId != stop.stopId) errors.Add(stop.stopId + ": parked car still belongs to another household.");
            foreach (var pen in anchor != null ? anchor.GetComponentsInChildren<KidPen>(true) : Array.Empty<KidPen>())
                if (pen.stopId != stop.stopId || pen.penId != stop.stopId) errors.Add(stop.stopId + ": yard residents still belong to another household.");
        }
        return errors;
    }

    /// <summary>Bounded physical preflight. Keeps roads, water, building solids and existing entry paths clear.
    /// Low ground surfaces are allowed to meet; decorations may meet without forcing a huge empty AABB.</summary>
    public static bool PlacementIsClear(GameObject added, bool property, out string reason, bool allowSceneryOverlap = false)
    {
        reason = null; var t = added.transform;
        Bounds visual;
        try { visual = LocalVisualBounds(t); }
        catch (Exception e) { reason = e.Message; return false; }
        var center = t.TransformPoint(visual.center);
        if (!CoastalCityLayout.BoundsXZ.Contains(new Vector2(center.x, center.z))) { reason = "Placement is outside the authored land bounds."; return false; }
        var worldBounds = new Bounds(center, Vector3.zero);
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            worldBounds.Encapsulate(t.TransformPoint(new Vector3(x == 0 ? visual.min.x : visual.max.x,
                y == 0 ? visual.min.y : visual.max.y, z == 0 ? visual.min.z : visual.max.z)));
        int depot = ReservedDepotOverlap(worldBounds);
        if (depot >= 0)
        { reason = "This stamp overlaps Depot " + (depot + 1) + "'s reserved shop/apron. Keep its facade, entrance and truck rollout clear."; return false; }
        // Geometry over river/ocean is not supported by these land stamps. Use the road editor for bridges.
        foreach (var corner in new[] { new Vector3(visual.min.x, 0, visual.min.z), new Vector3(visual.min.x, 0, visual.max.z),
            new Vector3(visual.max.x, 0, visual.min.z), new Vector3(visual.max.x, 0, visual.max.z), visual.center })
        {
            var p = t.TransformPoint(corner);
            if (InWater(p)) { reason = "This stamp overlaps the river or coast. Choose a land location."; return false; }
        }
        var own = new HashSet<Collider>(added.GetComponentsInChildren<Collider>(true)
            .Where(c => c.GetComponentInParent<CoastalYardBoundary>() == null));
        bool anySolid = false; Bounds solidBounds = default;
        foreach (var collider in own)
            if (collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger)
            {
                if (!anySolid) { solidBounds = collider.bounds; anySolid = true; } else solidBounds.Encapsulate(collider.bounds);
            }
        foreach (var collider in own)
        {
            if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger || collider.bounds.max.y < .4f) continue;
            var b = collider.bounds;
            foreach (var road in CoastalCityLayout.Roads)
            {
                // Sample the same narrow traffic channels as the city validator; side walks remain useful
                // places for benches/lamps, but their solid geometry must stay out of the carriageway.
                for (int i = 1; i < road.points.Length; i++)
                {
                    var a = road.points[i - 1]; var d = road.points[i] - a; float length = d.magnitude;
                    float along = Mathf.Clamp(Vector3.Dot(b.center - a, d.normalized), 0, length);
                    var p = a + d.normalized * along;
                    if (Mathf.Abs(p.x - b.center.x) > b.extents.x + road.halfWidth ||
                        Mathf.Abs(p.z - b.center.z) > b.extents.z + road.halfWidth) continue;
                    var closest = collider.ClosestPoint(p + Vector3.up * Mathf.Clamp(b.center.y, .6f, 2));
                    float distance = new Vector2(closest.x - p.x, closest.z - p.z).magnitude;
                    if (distance < road.halfWidth - .25f && b.max.y > p.y + .4f && b.min.y < p.y + 3.2f)
                    { reason = "Solid geometry blocks " + road.name + ". Move it beyond the curb."; return false; }
                }
            }
            var overlap = Physics.OverlapBox(b.center, Vector3.Max(b.extents - Vector3.one * .03f, Vector3.one * .01f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            foreach (var other in overlap)
            {
                if (own.Contains(other) || other.gameObject.scene != added.scene || other.bounds.max.y < .4f || other.isTrigger ||
                    !other.gameObject.activeInHierarchy || other.GetComponentInParent<CoastalYardBoundary>() != null) continue;
                if (allowSceneryOverlap && !property && other.GetComponentInParent<CityRoadAnchor>()?.GetComponentInChildren<CoastalHouseRoute>(true) == null) continue;
                if (Physics.ComputePenetration(collider, collider.transform.position, collider.transform.rotation,
                    other, other.transform.position, other.transform.rotation, out _, out float depth) && depth > .08f)
                { reason = "Overlaps " + other.name + ". Leave room for its existing geometry."; return false; }
            }
        }
        foreach (var route in SceneComponents<CoastalHouseRoute>(added.scene))
        {
            bool ours = route.transform.IsChildOf(t);
            if (ours && !property) continue;
            var points = new List<Vector3> { route.door }; points.AddRange(route.bends ?? Array.Empty<Vector3>()); points.Add(route.pathEnd);
            for (int i = 1; i < points.Count; i++)
            {
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(points[i - 1], points[i])));
                for (int s = 0; s <= samples; s++)
                {
                    var p = Vector3.Lerp(points[i - 1], points[i], s / (float)samples) + Vector3.up * .85f;
                    if (!ours && (!anySolid || Mathf.Abs(p.x - solidBounds.center.x) > solidBounds.extents.x + .5f ||
                        Mathf.Abs(p.z - solidBounds.center.z) > solidBounds.extents.z + .5f)) continue;
                    foreach (var hit in Physics.OverlapBox(p, new Vector3(.4f, .55f, .4f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.gameObject.scene != added.scene || hit.bounds.max.y < .4f || hit.GetComponentInParent<CoastalYardBoundary>() != null) continue;
                        if (ours ? own.Contains(hit) : !own.Contains(hit)) continue;
                        reason = ours ? "The property's front-door route is blocked by " + hit.name + "." : "This stamp blocks an existing resident's front-door path.";
                        return false;
                    }
                }
            }
            if (ours)
            {
                var stop = route.GetComponent<PickupStop>();
                if (stop != null)
                    foreach (var other in SceneComponents<PickupStop>(added.scene))
                        if (other != stop && (other.transform.position - stop.transform.position).sqrMagnitude < 25)
                        { reason = "Another curb stop is within 5 m; leave room for both garbage piles."; return false; }
            }
        }
        return true;
    }

    /// <summary>DepotRow is commonly unloaded while painting. Its reserved pads still protect all four shops,
    /// entrances and rollout lanes using the same authoring data as their scene, not a Physics scene scan.</summary>
    public static int ReservedDepotOverlap(Bounds bounds)
    {
        var footprint = Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
        for (int i = 0; i < DepotRowLayout.LotCount; i++)
            if (footprint.Overlaps(DepotRowLayout.PadRect(i))) return i;
        return -1;
    }

    static bool InWater(Vector3 p)
    {
        var river = CoastalCityLayout.River;
        for (int i = 1; i < river.Length; i++)
            if (CoastalCityLayout.DistanceToSegment(p, river[i - 1], river[i]) < 24f) return true;
        var coast = CoastalCityLayout.Coast;
        for (int i = 1; i < coast.Length; i++)
        {
            var a = coast[i - 1]; var b = coast[i];
            if (p.z >= Mathf.Min(a.z, b.z) && p.z <= Mathf.Max(a.z, b.z))
                return p.x >= Mathf.Lerp(a.x, b.x, Mathf.InverseLerp(a.z, b.z, p.z)) - 12f;
        }
        return false;
    }

    static KnockKind PropKind(string name)
    {
        name = (name ?? "").ToLowerInvariant();
        if (name.Contains("lamp")) return KnockKind.Lamp;
        if (name.Contains("mailbox")) return KnockKind.Mailbox;
        if (name.Contains("garbagecan")) return KnockKind.WheelieBin;
        if (name.Contains("bin")) return KnockKind.ParkBin;
        if (name.Contains("hydrant")) return KnockKind.Hydrant;
        if (name.Contains("cone")) return KnockKind.Cone;
        if (name.Contains("bench")) return KnockKind.ParkBench;
        if (name.Contains("fence")) return KnockKind.PicketPanel;
        return KnockKind.Sign;
    }

    static string DistrictAt(Vector3 point)
    {
        foreach (var district in Districts.All)
            if (CoastalCityLayout.DistrictBounds(district.id).Contains(new Vector2(point.x, point.z))) return district.id;
        return Districts.MildlyPleasant;
    }
    static T[] SceneComponents<T>(Scene scene) where T : Component => scene.GetRootGameObjects().Where(g => g.activeSelf)
        .SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
}

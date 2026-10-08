using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Scene authoring operations; never used by the running game or a player's save.</summary>
public static class TownPlacementTools
{
    public const string RootName = "TownAuthored";
    public static bool IsProperty(TownAssetEntry entry) => entry != null &&
        (entry.role == TownAssetRole.House || entry.role == TownAssetRole.CollectionBuilding);

    public static Scene Scene(bool open = false)
    {
        var scene = SceneManager.GetSceneByPath(NeighborhoodBuilder.DefaultScenePath);
        if (open && !scene.isLoaded)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before editing the town.");
            scene = EditorSceneManager.OpenScene(NeighborhoodBuilder.DefaultScenePath, OpenSceneMode.Additive);
        }
        return scene;
    }

    public static Transform Root(bool create = false)
    {
        var scene = Scene(create);
        if (!scene.IsValid() || !scene.isLoaded) return null;
        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
        if (root == null && create)
        {
            root = new GameObject(RootName); SceneManager.MoveGameObjectToScene(root, scene);
            Undo.RegisterCreatedObjectUndo(root, "Create town authoring layer");
        }
        return root != null ? root.transform : null;
    }

    public static Bounds PrefabBounds(GameObject prefab)
    {
        bool any = false; Bounds result = default;
        if (prefab != null) foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            var bounds = r.localBounds;
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            {
                var p = prefab.transform.InverseTransformPoint(r.transform.TransformPoint(new Vector3(
                    x == 0 ? bounds.min.x : bounds.max.x, y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z)));
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; } else result.Encapsulate(p);
            }
        }
        return any ? result : new Bounds(Vector3.up * .5f, Vector3.one);
    }

    public static Vector3 Snap(Vector3 point, float grid)
    {
        if (grid > .001f) { point.x = Mathf.Round(point.x / grid) * grid; point.z = Mathf.Round(point.z / grid) * grid; }
        return point;
    }

    public static bool Ground(Ray ray, out Vector3 point)
    {
        point = default; var scene = Scene();
        if (!scene.isLoaded) return false;
        // Ground-only targeting avoids painting on a tree canopy or a roof by accident.
        foreach (var hit in Physics.RaycastAll(ray, 6000, 1 << 6, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            if (hit.collider.gameObject.scene == scene && hit.normal.y > .45f && hit.point.y > -.45f)
            { point = hit.point; return true; }
        return false;
    }

    public static bool GroundAt(Vector3 position, out Vector3 point) => Ground(new Ray(position + Vector3.up * 500, Vector3.down), out point);

    public static string Check(TownAssetEntry entry, Vector3 point, Quaternion rotation, float scale, bool allowOverlap)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play mode to edit.";
        if (entry == null || entry.prefab == null) return "Choose an asset first.";
        if (!Scene().isLoaded) return "Open the town first.";
        if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z) || !Finite(scale) || scale < .1f || scale > 5)
            return "Invalid position or scale.";
        if (!CoastalCityLayout.BoundsXZ.Contains(new Vector2(point.x, point.z))) return "Outside the town boundary.";
        if (IsProperty(entry)) return null; // Complete property footprint, routes and curb checked by gameplay authoring.
        var bounds = PrefabBounds(entry.prefab);
        var half = bounds.extents * scale;
        var corners = new[] { new Vector3(-half.x, 0, -half.z), new Vector3(half.x, 0, -half.z),
            new Vector3(half.x, 0, half.z), new Vector3(-half.x, 0, half.z), Vector3.zero };
        foreach (var local in corners)
        {
            var p = point + rotation * local;
            if (!CoastalCityLayout.BoundsXZ.Contains(new Vector2(p.x, p.z))) return "Footprint extends outside the town.";
            if (!GroundAt(p, out var ground) || Mathf.Abs(ground.y - point.y) > 1.2f) return "Keep the footprint on dry, reasonably level ground.";
            if (CoastalCityLayout.DistanceToRoad(p) < .45f) return "Keep the driving lane clear.";
        }
        // Segment test, not only corners: a long fence must not bridge right across a street.
        foreach (var road in CoastalCityLayout.Roads)
            for (int i = 1; i < road.points.Length; i++)
            {
                var a = Quaternion.Inverse(rotation) * (road.points[i - 1] - point);
                var b = Quaternion.Inverse(rotation) * (road.points[i] - point);
                if (SegmentBox(a, b, half.x + road.halfWidth + .25f, half.z + road.halfWidth + .25f))
                    return "This footprint would obstruct " + road.name + ".";
            }
        if (!allowOverlap)
        {
            var hits = Physics.OverlapBox(point + Vector3.up * (half.y + .1f),
                new Vector3(Mathf.Max(.1f, half.x - .08f), Mathf.Max(.1f, half.y - .12f), Mathf.Max(.1f, half.z - .08f)), rotation, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
                if (hit.gameObject.scene == Scene() && hit.bounds.max.y > point.y + .3f)
                    return "Overlaps " + hit.name + ". Move it or enable scenery overlap.";
        }
        return null;
    }
    static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    static bool SegmentBox(Vector3 a, Vector3 b, float x, float z)
    {
        float lo = 0, hi = 1;
        bool Clip(float start, float delta, float extent)
        {
            if (Mathf.Abs(delta) < .00001f) return Mathf.Abs(start) <= extent;
            float l = (-extent - start) / delta, h = (extent - start) / delta;
            lo = Mathf.Max(lo, Mathf.Min(l, h)); hi = Mathf.Min(hi, Mathf.Max(l, h)); return lo <= hi;
        }
        return Clip(a.x, b.x - a.x, x) && Clip(a.z, b.z - a.z, z);
    }

    public static GameObject Place(TownAssetEntry entry, Vector3 point, Quaternion rotation, float scale, bool allowOverlap, out string error)
    {
        error = Check(entry, point, rotation, scale, allowOverlap);
        if (error != null) return null;
        var root = Root(true);
        var go = TownGameplayAuthoring.Create(entry, point, rotation, IsProperty(entry) ? 1 : scale, root, out error, allowOverlap);
        if (go == null) return null;
        var marker = go.GetComponent<TownPlacedAsset>() ?? go.AddComponent<TownPlacedAsset>();
        if (string.IsNullOrEmpty(marker.placementId)) marker.placementId = Guid.NewGuid().ToString("N");
        marker.paletteId = entry.id; marker.role = entry.role;
        marker.allowSceneryOverlap = allowOverlap && !IsProperty(entry);
        var bounds = PrefabBounds(entry.prefab); marker.radius = Mathf.Max(.5f, new Vector2(bounds.extents.x, bounds.extents.z).magnitude * scale);
        go.name = entry.displayName + " · " + marker.placementId.Substring(0, 6);
        Undo.RegisterCreatedObjectUndo(go, "Place " + entry.displayName);
        EditorSceneManager.MarkSceneDirty(go.scene); Physics.SyncTransforms(); return go;
    }

    public static int Erase(Vector3 point, float radius, bool includeProperties)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return 0;
        var root = Root(); if (root == null) return 0;
        int count = 0;
        foreach (var item in root.GetComponentsInChildren<TownPlacedAsset>(true))
        {
            if (!includeProperties && (item.role == TownAssetRole.House || item.role == TownAssetRole.CollectionBuilding)) continue;
            var delta = item.transform.position - point; delta.y = 0;
            if (delta.magnitude > radius) continue;
            Undo.DestroyObjectImmediate(item.gameObject); count++;
        }
        if (count > 0) { EditorSceneManager.MarkSceneDirty(root.gameObject.scene); Physics.SyncTransforms(); }
        return count;
    }

    public static float LineYaw(Vector3 a, Vector3 b, Vector3 dimensions, float yaw)
    {
        float angle = Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg + yaw;
        return dimensions.x > dimensions.z ? angle - 90 : angle;
    }

    public static Vector3[] LinePoints(Vector3 a, Vector3 b, float spacing, int cap = 200)
    {
        float length = Vector3.Distance(a, b); if (length < .01f) return new[] { a };
        float step = Mathf.Max(.5f, spacing);
        int count = Mathf.Clamp(Mathf.FloorToInt(length / step) + 1, 1, Mathf.Max(1, cap));
        var points = new Vector3[count];
        for (int i = 0; i < count; i++) points[i] = a + (b - a) / length * (i * step);
        return points;
    }
}

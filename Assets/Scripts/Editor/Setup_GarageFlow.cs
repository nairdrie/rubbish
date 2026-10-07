using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Idempotent scene setup for the garage ⇄ neighborhood flow (Flow system).
///
/// ShopScene:
///   - GarageDoor → Neighborhood (SpawnPoint_FromGarage / SpawnPoint_FromGarage_Truck, "Exit Garage" / "Drive Out")
///   - InteractionZone (Either) covering ~4.5 m inside the garage in front of the door
///   - Spawners/SpawnPoint_FromNeighborhood (on foot) and Spawners/SpawnPoint_FromNeighborhood_Truck (= TruckSpawner pose)
///   - moves the PlayerSpawner-tagged marker out of the sorting station to (0.5, 0, 0.2) facing +X
/// Neighborhood + GameScene:
///   - removes stray PlayerSpawner *components* from spawn markers (GameObjects are kept; only Bootstrap spawns players)
///
/// Scenes are opened additively when not already loaded, saved, and closed again.
/// </summary>
public static class Setup_GarageFlow
{
    const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    const string NeighborhoodScenePath = "Assets/Scenes/Neighborhood.unity";
    const string GameScenePath = "Assets/Scenes/GameScene.unity";
    const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    const string TruckPrefabPath = "Assets/Prefabs/Truck V2.prefab";

    // World facts (Docs/CoreLoop.md §6): garage door on the -X wall.
    static readonly Vector3 DoorCenterWorld = new Vector3(-6.76f, 0f, -2.41f);
    const float DoorZMin = -4.4f, DoorZMax = -0.4f;
    const float ZoneDepth = 4.5f;          // how far into the garage the door zone reaches (+X)
    const float ZoneYMin = -2f, ZoneYMax = 3f; // truck root rests ~0.7 m below the floor, player feet at ~-0.1

    static readonly Vector3 FromNeighborhoodPos = new Vector3(-5.8f, 0f, 0.6f);
    const float FromNeighborhoodYaw = 90f;
    static readonly Vector3 DefaultTruckSpawnerPos = new Vector3(-0.11f, 0f, -2.47f);
    const float DefaultTruckSpawnerYaw = 270f;
    static readonly Vector3 PlayerSpawnerPos = new Vector3(0.5f, 0f, 0.2f);
    const float PlayerSpawnerYaw = 90f;

    [MenuItem("Rubbish/Setup/GarageFlow")]
    public static void Run()
    {
        Execute(true);
    }

    /// <summary>
    /// save=false is a dry run: scenes that aren't already open are modified in memory and discarded on close
    /// (already-open scenes are skipped). Returns the summary that is also logged.
    /// </summary>
    public static string Execute(bool save)
    {
        var log = new StringBuilder();
        log.AppendLine(save ? "[Setup_GarageFlow] Run" : "[Setup_GarageFlow] DRY RUN (nothing saved)");
        ProcessScene(ShopScenePath, save, log, ConfigureShopScene);
        ProcessScene(NeighborhoodScenePath, save, log, RemoveStraySpawnerComponents);
        ProcessScene(GameScenePath, save, log, RemoveStraySpawnerComponents);
        CheckPrefabs(log);
        string summary = log.ToString();
        Debug.Log(summary);
        return summary;
    }

    // ------------------------------------------------------------------ scene plumbing

    static void ProcessScene(string path, bool save, StringBuilder log, System.Func<Scene, StringBuilder, bool> body)
    {
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                log.AppendLine($"  ! {path} not found, skipped");
                return;
            }
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            opened = true;
        }
        else if (!save)
        {
            log.AppendLine($"  (dry run) {path} is already open; skipped so it isn't modified");
            return;
        }

        log.AppendLine($"== {path}");
        try
        {
            bool changed = body(scene, log);
            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (save)
                {
                    bool ok = EditorSceneManager.SaveScene(scene);
                    log.AppendLine(ok ? "  saved" : "  ! SAVE FAILED");
                }
            }
            else
            {
                log.AppendLine("  no changes needed");
            }
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    // ------------------------------------------------------------------ ShopScene

    /// <summary>Applies the ShopScene changes to an already-open scene (no save). Returns true if anything changed.</summary>
    public static bool ConfigureShopScene(Scene scene, StringBuilder log)
    {
        bool changed = false;

        // 1) Garage door → Neighborhood
        var door = FindDoor(scene);
        if (door == null)
        {
            log.AppendLine("  ! GarageDoor (GarageDoorController) not found");
        }
        else
        {
            bool d = false;
            d |= SetString(ref door.targetSceneName, "Neighborhood");
            d |= SetString(ref door.targetSpawnPointName, "SpawnPoint_FromGarage");
            d |= SetString(ref door.targetSpawnPointNameDriving, "SpawnPoint_FromGarage_Truck");
            d |= SetString(ref door.promptOnFoot, "Exit Garage");
            d |= SetString(ref door.promptDriving, "Drive Out");
            if (d) MarkModified(door);
            log.AppendLine(d ? "  GarageDoor configured → Neighborhood" : "  GarageDoor already configured");
            changed |= d;

            // 2) Drive-up / walk-up zone inside the garage in front of the door.
            var zone = door.GetComponent<InteractionZone>();
            bool added = false;
            if (zone == null) { zone = door.gameObject.AddComponent<InteractionZone>(); added = true; }
            var worldCenter = new Vector3(DoorCenterWorld.x + ZoneDepth * 0.5f, (ZoneYMin + ZoneYMax) * 0.5f, (DoorZMin + DoorZMax) * 0.5f);
            var worldSize = new Vector3(ZoneDepth, ZoneYMax - ZoneYMin, DoorZMax - DoorZMin);
            Vector3 localCenter = door.transform.InverseTransformPoint(worldCenter);
            Vector3 localSize = Abs(door.transform.InverseTransformVector(worldSize));
            bool z = added;
            z |= SetVec(ref zone.center, localCenter);
            z |= SetVec(ref zone.size, localSize);
            if (zone.mode != ZoneMode.Either) { zone.mode = ZoneMode.Either; z = true; }
            if (!Mathf.Approximately(zone.score, 1f)) { zone.score = 1f; z = true; }
            if (zone.targetOverride != door) { zone.targetOverride = door; z = true; }
            if (z) MarkModified(zone);
            log.AppendLine(z ? $"  InteractionZone {(added ? "added" : "updated")}: local center {localCenter:F2} size {localSize:F2} (world center {worldCenter:F2})"
                             : "  InteractionZone already configured");
            changed |= z;
        }

        // 3) Spawn markers under "Spawners"
        var spawners = FindRoot(scene, "Spawners");
        if (spawners == null)
        {
            var go = new GameObject("Spawners");
            SceneManager.MoveGameObjectToScene(go, scene);
            spawners = go.transform;
            changed = true;
            log.AppendLine("  created Spawners root");
        }

        Vector3 truckPos = DefaultTruckSpawnerPos;
        Quaternion truckRot = Quaternion.Euler(0f, DefaultTruckSpawnerYaw, 0f);
        var truckSpawner = FindTagged(scene, "TruckSpawner");
        if (truckSpawner != null) { truckPos = truckSpawner.position; truckRot = truckSpawner.rotation; }
        else log.AppendLine("  ! no TruckSpawner-tagged marker; using the documented pose");

        changed |= EnsureMarker(scene, spawners, "SpawnPoint_FromNeighborhood", FromNeighborhoodPos, Quaternion.Euler(0f, FromNeighborhoodYaw, 0f), log);
        changed |= EnsureMarker(scene, spawners, "SpawnPoint_FromNeighborhood_Truck", truckPos, truckRot, log);

        // 4) Default (tagged) player spawn out of the sorting station.
        var playerSpawner = FindTagged(scene, "PlayerSpawner");
        if (playerSpawner == null)
        {
            log.AppendLine("  ! no PlayerSpawner-tagged marker found");
        }
        else
        {
            if (SetPose(playerSpawner, PlayerSpawnerPos, Quaternion.Euler(0f, PlayerSpawnerYaw, 0f)))
            {
                changed = true;
                log.AppendLine($"  moved PlayerSpawner marker '{playerSpawner.name}' → {PlayerSpawnerPos} yaw {PlayerSpawnerYaw}");
            }
            else log.AppendLine("  PlayerSpawner marker already placed");
        }

        changed |= RemoveStraySpawnerComponents(scene, log);
        return changed;
    }

    static bool EnsureMarker(Scene scene, Transform parent, string name, Vector3 pos, Quaternion rot, StringBuilder log)
    {
        var t = FindByName(scene, name);
        bool changed = false;
        if (t == null)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            t = go.transform;
            t.SetParent(parent, false);
            changed = true;
            log.AppendLine($"  created {name}");
        }
        else if (t.parent != parent)
        {
            t.SetParent(parent, true);
            changed = true;
        }
        if (SetPose(t, pos, rot)) changed = true;
        if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); changed = true; }
        if (changed) log.AppendLine($"  {name} @ {pos} yaw {rot.eulerAngles.y:0}");
        else log.AppendLine($"  {name} ok");
        return changed;
    }

    // ------------------------------------------------------------------ Neighborhood / GameScene

    /// <summary>Remove PlayerSpawner components from scene objects (keep the GameObjects). Bootstrap owns spawning.</summary>
    static bool RemoveStraySpawnerComponents(Scene scene, StringBuilder log)
    {
        bool changed = false;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var ps in root.GetComponentsInChildren<PlayerSpawner>(true))
            {
                string path = GetPath(ps.transform);
                bool isInstance = PrefabUtility.IsPartOfPrefabInstance(ps);
                Undo.DestroyObjectImmediate(ps);
                changed = true;
                log.AppendLine($"  removed stray PlayerSpawner component on '{path}'{(isInstance ? " (prefab-instance override)" : "")}");
            }
        }
        if (!changed) log.AppendLine("  no stray PlayerSpawner components");
        return changed;
    }

    // ------------------------------------------------------------------ prefab sanity (read-only)

    static void CheckPrefabs(StringBuilder log)
    {
        log.AppendLine("== prefab checks (read-only)");
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (player == null) log.AppendLine("  ! Player prefab missing");
        else
        {
            var tr = player.GetComponent<TruckRequester>();
            if (tr == null) log.AppendLine("  ! Player prefab has no TruckRequester");
            else
            {
                var so = new SerializedObject(tr);
                var p = so.FindProperty("truckPrefab");
                log.AppendLine(p != null && p.objectReferenceValue != null
                    ? $"  Player.TruckRequester.truckPrefab = {AssetDatabase.GetAssetPath(p.objectReferenceValue)}"
                    : "  ! Player.TruckRequester.truckPrefab is not assigned");
            }
        }
        var truck = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPrefabPath);
        if (truck == null) log.AppendLine("  ! Truck V2 prefab missing");
        else
        {
            var tc = truck.GetComponent<TruckControllerV2>();
            var so = tc != null ? new SerializedObject(tc) : null;
            var eject = so != null ? so.FindProperty("driverEjectPosition") : null;
            log.AppendLine(tc == null ? "  ! Truck V2 has no TruckControllerV2"
                : eject == null || eject.objectReferenceValue == null ? "  ! Truck V2 driverEjectPosition not assigned"
                : "  Truck V2 ok (TruckControllerV2 + DriverEjectPosition)");
            if (truck.transform.Find("Hopper") == null) log.AppendLine("  (info) Truck V2 has no 'Hopper' child yet (Pickup setup adds it)");
        }
    }

    // ------------------------------------------------------------------ helpers

    static GarageDoorController FindDoor(Scene scene)
    {
        GarageDoorController first = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var d in root.GetComponentsInChildren<GarageDoorController>(true))
            {
                if (d.gameObject.name == "GarageDoor") return d;
                if (first == null) first = d;
            }
        }
        return first;
    }

    static Transform FindRoot(Scene scene, string name)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == name) return root.transform;
        return null;
    }

    static Transform FindByName(Scene scene, string name)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
        return null;
    }

    static Transform FindTagged(Scene scene, string tag)
    {
        var all = new List<Transform>();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag(tag)) all.Add(t);
        if (all.Count == 0) return null;
        // Prefer an active one under "Spawners".
        foreach (var t in all)
            if (t.gameObject.activeInHierarchy && t.parent != null && t.parent.name == "Spawners") return t;
        foreach (var t in all)
            if (t.gameObject.activeInHierarchy) return t;
        return all[0];
    }

    static bool SetPose(Transform t, Vector3 pos, Quaternion rot)
    {
        bool changed = false;
        if ((t.position - pos).sqrMagnitude > 1e-6f) { t.position = pos; changed = true; }
        if (Quaternion.Angle(t.rotation, rot) > 0.01f) { t.rotation = rot; changed = true; }
        if (changed) MarkModified(t);
        return changed;
    }

    static bool SetString(ref string field, string value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }

    static bool SetVec(ref Vector3 field, Vector3 value)
    {
        if ((field - value).sqrMagnitude < 1e-6f) return false;
        field = value;
        return true;
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    static void MarkModified(Object o)
    {
        EditorUtility.SetDirty(o);
        if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
    }

    static string GetPath(Transform t)
    {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }
}

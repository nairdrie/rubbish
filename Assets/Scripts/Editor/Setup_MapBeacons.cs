using System.Text;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds a <see cref="MapBeacon"/> (kind Truck, Rubbish Co. olive) to <c>Truck V2.prefab</c> so the minimap and full map draw
/// every truck without searching the scene (Docs/UIPass.md §7.5). Players get theirs at runtime (CoopPlayerHooks.Spawned), so
/// Player.prefab is never touched. Run after <c>Setup_Coop</c> (§10.8); idempotent (a second run changes nothing) and it only
/// adds a component: the NetworkObject's GlobalObjectIdHash is checked before and after.
/// </summary>
public static class Setup_MapBeacons
{
    public const string TruckPrefabPath = "Assets/Prefabs/Truck V2.prefab";

    [MenuItem("Rubbish/Setup/Map Beacons")]
    public static void Run()
    {
        var log = new StringBuilder();
        bool ok = Apply(log);
        Debug.Log("[Setup_MapBeacons] " + (ok ? "OK" : "FAILED") + " " + log);
    }

    public static bool Apply(StringBuilder log)
    {
        if (Application.isPlaying) { log.Append("exit play mode first"); return false; }
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPrefabPath);
        if (asset == null) { log.Append("missing " + TruckPrefabPath); return false; }
        var no = asset.GetComponent<NetworkObject>();
        uint hashBefore = no != null ? no.PrefabIdHash : 0;
        var existing = asset.GetComponent<MapBeacon>();
        if (existing != null && existing.kind == MapBeaconKind.Truck && existing.tint == MapBeacon.TruckOlive)
        {
            log.Append("truck beacon already present (no change)");
            return true;
        }
        var root = PrefabUtility.LoadPrefabContents(TruckPrefabPath);
        try
        {
            var b = root.GetComponent<MapBeacon>();
            if (b == null) b = root.AddComponent<MapBeacon>();
            b.kind = MapBeaconKind.Truck;
            b.tint = MapBeacon.TruckOlive;
            PrefabUtility.SaveAsPrefabAsset(root, TruckPrefabPath, out bool saved);
            if (!saved) { log.Append("save failed"); return false; }
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        asset = AssetDatabase.LoadAssetAtPath<GameObject>(TruckPrefabPath);
        no = asset.GetComponent<NetworkObject>();
        uint hashAfter = no != null ? no.PrefabIdHash : 0;
        log.Append($"truck beacon added; NetworkObject hash {hashBefore} -> {hashAfter}{(hashBefore == hashAfter ? " (unchanged)" : " CHANGED")}");
        return hashBefore == hashAfter;
    }
}

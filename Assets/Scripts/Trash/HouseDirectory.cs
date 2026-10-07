using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every house in the Neighborhood that puts trash out, as data (written by NeighborhoodBuilder / Setup_Pickup
/// to Assets/Resources/HouseDirectory.asset). Lets the CurbTrashSpawner run from the moment the server starts —
/// even while you're still in the garage and the Neighborhood scene isn't loaded. When the Neighborhood loads,
/// its PickupStops are authoritative (TrashRoute re-registers them), so a stale/missing asset only costs the
/// trash that would have gone out before your first visit.
/// </summary>
public class HouseDirectory : ScriptableObject
{
    public const string ResourcePath = "HouseDirectory";
    public const string AssetPath = "Assets/Resources/HouseDirectory.asset";

    [Serializable]
    public class Entry
    {
        public string stopId;
        public string address;
        public int capacity = 4;
        [Range(0f, 1f)] public float wealth = 0.4f;
        public Vector3 position;
        /// <summary>District id (Districts.*). Empty in old assets: derived from the stopId prefix (starter suburb).</summary>
        public string districtId = "";
    }

    public string sceneName = "Neighborhood";
    public List<Entry> houses = new List<Entry>();

    public static HouseDirectory Load()
    {
        try { return Resources.Load<HouseDirectory>(ResourcePath); }
        catch (Exception e) { Debug.LogWarning("[HouseDirectory] load failed: " + e.Message); return null; }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class TownAssetEntry
{
    public string id, displayName, category, description;
    public GameObject prefab;
    public TownAssetRole role;
    public float defaultScale = 1;
    public float spacing = 4;
    public bool randomYaw = true;
    // Houses use the complete existing property recipe matching this visual prefab name.
    public string houseModel;
    public string collectionType;
    public float propertyFrontYaw;
}

public sealed class TownAssetPalette : ScriptableObject
{
    public const string AssetPath = "Assets/Editor/TownAuthoring/TownPalette.asset";
    public List<TownAssetEntry> entries = new List<TownAssetEntry>();
}

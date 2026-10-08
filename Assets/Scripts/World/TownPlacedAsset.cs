using UnityEngine;

public enum TownAssetRole { Decoration, House, StreetProp, ParkedCar, CollectionBuilding }

/// <summary>Persistent identity for a hand-authored town object. Never generated/replaced by the town recipe.</summary>
[DisallowMultipleComponent]
public sealed class TownPlacedAsset : MonoBehaviour
{
    public string placementId;
    public string paletteId;
    public TownAssetRole role;
    public float radius;
    public bool allowSceneryOverlap;
}

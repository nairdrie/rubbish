using UnityEngine;

/// <summary>
/// Marks a menu-only prefab copy (<c>Assets/Prefabs/UI/MenuChuck.prefab</c>, <c>MenuTruck.prefab</c>) with the source prefab
/// and its hash, so <c>Setup_MenuDiorama</c> rebuilds the copy only when the source changed (Docs/UIPass.md §4.7, §12: the
/// copies follow Character and Coop setups). Inert at runtime.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuPrefabStamp : MonoBehaviour
{
    public string source;
    public string sourceHash;
    public int version;
}

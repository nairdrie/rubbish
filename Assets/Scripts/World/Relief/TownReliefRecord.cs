using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What the relief bake changed, so the next bake can put the town back exactly flat before the layout code (which
/// only understands a flat town) runs again. Lives on NeighborhoodGen. Data only; nothing reads it at runtime.
/// </summary>
[DisallowMultipleComponent]
public sealed class TownReliefRecord : MonoBehaviour
{
    public int designRevision;
    // objects that were moved: their flat local pose, and the pose the bake left them in (to notice hand edits since)
    public List<Transform> moved = new List<Transform>();
    public List<Vector3> flatPosition = new List<Vector3>(), bakedPosition = new List<Vector3>();
    public List<Quaternion> flatRotation = new List<Quaternion>(), bakedRotation = new List<Quaternion>();
    // meshes that were swapped for a draped copy
    public List<MeshFilter> drapedFilters = new List<MeshFilter>();
    public List<Mesh> flatFilterMeshes = new List<Mesh>();
    public List<MeshCollider> drapedColliders = new List<MeshCollider>();
    public List<Mesh> flatColliderMeshes = new List<Mesh>();
    // box colliders replaced by a mesh collider on a draped object, and boxes stretched to cover a slope
    public List<BoxCollider> disabledBoxes = new List<BoxCollider>();
    public List<MeshCollider> addedColliders = new List<MeshCollider>();
    public List<BoxCollider> grownBoxes = new List<BoxCollider>();
    public List<Vector3> flatBoxCentre = new List<Vector3>(), flatBoxSize = new List<Vector3>();
    // the flat terrain (switched off) and the relief terrain (created)
    public List<GameObject> hidden = new List<GameObject>();
    public List<GameObject> created = new List<GameObject>();
    // generated meshes draped in place: which flat content each was made from
    public List<Mesh> kitMeshes = new List<Mesh>();
    public List<string> kitFlatHashes = new List<string>();
    // world-space points stored on components (house routes, kid pens, yard owners)
    public List<Component> pointOwners = new List<Component>();
    public List<int> pointCounts = new List<int>();
    public List<Vector3> flatPoints = new List<Vector3>();

    public void Clear()
    {
        moved.Clear(); flatPosition.Clear(); bakedPosition.Clear(); flatRotation.Clear(); bakedRotation.Clear();
        drapedFilters.Clear(); flatFilterMeshes.Clear(); drapedColliders.Clear(); flatColliderMeshes.Clear();
        disabledBoxes.Clear(); addedColliders.Clear(); grownBoxes.Clear(); flatBoxCentre.Clear(); flatBoxSize.Clear();
        hidden.Clear(); created.Clear(); kitMeshes.Clear(); kitFlatHashes.Clear();
        pointOwners.Clear(); pointCounts.Clear(); flatPoints.Clear();
    }
}

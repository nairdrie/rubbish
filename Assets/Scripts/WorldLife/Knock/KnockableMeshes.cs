using UnityEngine;

/// <summary>
/// WorldLife package B (Docs/WorldLife.md §6.3 step 3): the original mesh of every renderer of a <see cref="Knockable"/>,
/// baked at EDIT time by <c>Setup_WorldLifeKnock</c> (after NeighborhoodBuilder places the knockables).
///
/// Why: at runtime a batching-static renderer's <c>MeshFilter.sharedMesh</c> is the combined static batch, so a proxy
/// can't be built from the scene object (and must never be Instantiated from it). The baked list keeps the asset meshes
/// (Unity's Cube / Cylinder, the picket panel asset, pack FBX meshes...) so <see cref="KnockProxyPool"/> can rebuild the
/// same primitives for any builder-made prop: the "recipe" is read from the scene itself, so it always matches what the
/// builder made. Materials and poses are read from the live renderers at knock time (static batching keeps both).
/// </summary>
[DisallowMultipleComponent]
public sealed class KnockableMeshes : MonoBehaviour
{
    [Tooltip("Renderers of the knockable (MeshRenderers only; TMP text is handled separately).")]
    public Renderer[] renderers = new Renderer[0];
    [Tooltip("The asset mesh of each renderer (parallel to renderers), captured before static batching.")]
    public Mesh[] meshes = new Mesh[0];

    /// <summary>The baked asset mesh for a renderer, or null.</summary>
    public Mesh MeshFor(Renderer r)
    {
        if (r == null || renderers == null || meshes == null) return null;
        int n = Mathf.Min(renderers.Length, meshes.Length);
        for (int i = 0; i < n; i++) if (renderers[i] == r) return meshes[i];
        return null;
    }
}

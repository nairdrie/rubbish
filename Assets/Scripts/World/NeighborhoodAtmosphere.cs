using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scene-level ambience hook for the Neighborhood (NeighborhoodBuilder puts one under "NeighborhoodGen").
/// The day/night sky itself lives on a separate "WorldSky" root (<see cref="DayNightSky"/>, placed by
/// Setup_World, outside the builder's root so rebuilds never delete it). If a scene has no DayNightSky —
/// e.g. the neighborhood was rebuilt/copied but Setup_World wasn't run — this creates one at runtime so the
/// day/night cycle always works. With the sky disabled it falls back to the old behaviour: recompute ambient +
/// reflections from the static skybox on load.
/// </summary>
public class NeighborhoodAtmosphere : MonoBehaviour
{
    [Tooltip("Create a DayNightSky at runtime if the scene doesn't have one.")]
    public bool ensureDayNightSky = true;

    [Tooltip("Recompute ambient light + reflections from RenderSettings.skybox when the scene loads (only used when no DayNightSky runs).")]
    public bool refreshEnvironmentOnLoad = true;

    // Start, not Awake: during an NGO Single load this scene may not be the active one yet in Awake, and
    // RenderSettings (the skybox) always reflect the active scene.
    void Start()
    {
        NeighborhoodConnection.Ensure(gameObject.scene);
        if (ensureDayNightSky && EnsureSky(gameObject.scene) != null) return;
        if (refreshEnvironmentOnLoad && RenderSettings.skybox != null)
            DynamicGI.UpdateEnvironment();
    }

    /// <summary>Find the scene's DayNightSky, creating a "WorldSky (auto)" root with one if missing.</summary>
    public static DayNightSky EnsureSky(Scene scene)
    {
        if (!scene.IsValid()) return null;
        foreach (var root in scene.GetRootGameObjects())
        {
            var existing = root.GetComponentInChildren<DayNightSky>(true);
            if (existing != null) return existing;
        }
        var go = new GameObject("WorldSky (auto)");
        SceneManager.MoveGameObjectToScene(go, scene);
        var sky = go.AddComponent<DayNightSky>();
        if (!Application.isPlaying) return sky;
        Debug.Log("[Sky] No DayNightSky in " + scene.name + " — created one at runtime (run Rubbish/Setup/World to bake it in).");
        return sky;
    }
}

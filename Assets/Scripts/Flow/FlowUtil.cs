using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Small helpers shared by the scene-flow scripts: finding spawn markers in the freshly loaded scene,
/// finding / remembering the single truck, and upright poses.
/// </summary>
public static class FlowUtil
{
    public const string PlayerSpawnerTag = "PlayerSpawner";
    public const string TruckSpawnerTag = "TruckSpawner";

    /// <summary>
    /// First active GameObject named <paramref name="name"/> in <paramref name="scene"/> (breadth-first over the
    /// scene roots, so a top-level marker wins over a deeply nested namesake). Null if not found.
    /// </summary>
    public static Transform FindInScene(Scene scene, string name)
    {
        if (string.IsNullOrEmpty(name) || !scene.IsValid() || !scene.isLoaded) return null;
        var queue = new Queue<Transform>();
        foreach (var root in scene.GetRootGameObjects())
            if (root.activeInHierarchy) queue.Enqueue(root.transform);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.gameObject.activeSelf) queue.Enqueue(c);
            }
        }
        return null;
    }

    /// <summary>Marker by name: prefers the active (newly loaded) scene, then any loaded scene.</summary>
    public static Transform FindMarker(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var t = FindInScene(SceneManager.GetActiveScene(), name);
        if (t != null) return t;
        var go = GameObject.Find(name);
        return go != null ? go.transform : null;
    }

    /// <summary>
    /// Tagged marker, preferring the active scene. When a scene has several (e.g. the legacy
    /// CommonWorldElements/PlayerSpawner plus a builder-made one), markers outside CommonWorldElements win.
    /// Only falls back to other loaded scenes (DDOL etc.) when the active scene has none.
    /// </summary>
    public static Transform FindTagged(string tag)
    {
        GameObject[] all;
        try { all = GameObject.FindGameObjectsWithTag(tag); }
        catch (UnityException) { return null; } // tag not defined
        if (all == null || all.Length == 0) return null;

        var active = SceneManager.GetActiveScene();
        Transform best = null; int bestRank = int.MaxValue;
        foreach (var go in all)
        {
            if (go == null) continue;
            int rank = 0;
            if (go.scene != active) rank += 10;
            if (go.transform.root.name == "CommonWorldElements") rank += 1;
            if (rank < bestRank) { bestRank = rank; best = go.transform; }
        }
        return best;
    }

    /// <summary>The truck in the loaded scenes (prefers one in the active scene), or null.</summary>
    public static TruckControllerV2 FindTruck()
    {
        var trucks = Object.FindObjectsByType<TruckControllerV2>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (trucks == null || trucks.Length == 0) return null;
        var active = SceneManager.GetActiveScene();
        foreach (var t in trucks)
            if (t != null && t.gameObject.scene == active) return t;
        return trucks[0];
    }

    /// <summary>Remember where the (single) truck is parked in GameSession.</summary>
    public static void RecordTruck(TruckControllerV2 truck, string sceneName)
    {
        if (truck == null) return;
        RecordTruckPose(sceneName, truck.transform.position, truck.transform.rotation);
    }

    public static void RecordTruckPose(string sceneName, Vector3 position, Quaternion rotation)
    {
        GameSession.TruckSceneName = sceneName;
        GameSession.TruckPosition = position;
        GameSession.TruckRotation = Upright(rotation);
    }

    /// <summary>Yaw-only version of a rotation (a truck left on its roof comes back on its wheels).</summary>
    public static Quaternion Upright(Quaternion rotation)
    {
        Vector3 fwd = rotation * Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = rotation * Vector3.up; // pointing straight up/down: use its "up" as heading
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        return Quaternion.LookRotation(fwd.normalized, Vector3.up);
    }
}

/// <summary>
/// Makes the main camera's CinemachineBrain cut instantly between vcams while the screen is black
/// (e.g. the player is auto-seated in the truck on arrival), then restores the normal blend.
/// </summary>
public static class FlowCamera
{
    static Cinemachine.CinemachineBrain cutBrain;
    static Cinemachine.CinemachineBlendDefinition savedBlend;
    static bool cutting;

    /// <summary>On: current main-camera brain cuts. Call again after a scene load to catch the new brain. Off: restore.</summary>
    public static void SetCutMode(bool on)
    {
        if (!on) { Restore(); return; }
        var cam = Camera.main;
        var brain = cam != null ? cam.GetComponent<Cinemachine.CinemachineBrain>() : null;
        if (brain == null) return;
        if (cutting && cutBrain == brain) return;
        Restore();
        cutBrain = brain;
        savedBlend = brain.m_DefaultBlend;
        brain.m_DefaultBlend = new Cinemachine.CinemachineBlendDefinition(Cinemachine.CinemachineBlendDefinition.Style.Cut, 0f);
        cutting = true;
    }

    static void Restore()
    {
        if (cutting && cutBrain != null) cutBrain.m_DefaultBlend = savedBlend;
        cutting = false;
        cutBrain = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cutting = false; cutBrain = null; }
}

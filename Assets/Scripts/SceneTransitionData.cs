using UnityEngine;

/// <summary>
/// A static class to hold player data that needs to persist across scene loads.
///
/// Lifecycle of one door transition (host only in early game):
///   1. SceneController.TransitionToScene → <see cref="Begin"/> fills the *pending* fields
///      (TargetSpawnPointName, IsPlayerDriving, ...).
///   2. PlayerSpawner (when the load completes) → <see cref="MarkArrived"/> turns them into an
///      *arrival record* for the new scene and clears TargetSpawnPointName so it isn't re-used.
///      IsPlayerDriving is intentionally kept until the arrival is consumed.
///   3. TruckRequester (on the host's new player) → <see cref="TryConsumeArrival"/> reads
///      "did we arrive driving, and at which marker?", spawns/seats the truck, then the record is cleared.
/// </summary>
public static class SceneTransitionData
{
    // ---------------- pending transition (set before the load) ----------------

    /// <summary>The name of the spawn point to use in the next scene (driving or on-foot marker).</summary>
    public static string TargetSpawnPointName { get; set; }
    /// <summary>
    /// Is the player driving their truck through the transition? Stays true through the arrival until
    /// TruckRequester consumes the arrival record (so the truck can be spawned and the player seated).
    /// </summary>
    public static bool IsPlayerDriving { get; set; }
    /// <summary>On-foot marker in the target scene (used for other players when the host arrives driving).</summary>
    public static string OnFootSpawnPointName { get; set; }
    /// <summary>Scene we are leaving.</summary>
    public static string SourceSceneName { get; set; }
    /// <summary>Scene we are loading.</summary>
    public static string TargetSceneName { get; set; }
    /// <summary>True between Begin() and MarkArrived().</summary>
    public static bool IsPending { get; private set; }

    // ---------------- arrival record (set when the load completes) ----------------

    /// <summary>True from MarkArrived() until TruckRequester consumes it (or the next transition starts).</summary>
    public static bool HasArrival { get; private set; }
    /// <summary>Scene the arrival record belongs to.</summary>
    public static string ArrivalSceneName { get; private set; }
    /// <summary>Marker name the host's player was sent to (may be null = default spawner).</summary>
    public static string ArrivalSpawnPointName { get; private set; }
    /// <summary>Whether the host arrived in the truck (TruckRequester spawns it at the marker and seats the player).</summary>
    public static bool ArrivedDriving { get; private set; }

    /// <summary>Optional toast shown by SceneController once the screen has faded back in ("Day 3 — fresh trash is out!").</summary>
    public static string PendingArrivalToast { get; set; }
    public static ToastStyle PendingArrivalToastStyle { get; set; } = ToastStyle.Good;

    /// <summary>Start a transition: records where we're going and how (clears any stale arrival).</summary>
    public static void Begin(string fromScene, string toScene, string spawnPointName, bool driving, string onFootSpawnPointName)
    {
        SourceSceneName = fromScene;
        TargetSceneName = toScene;
        TargetSpawnPointName = spawnPointName;
        OnFootSpawnPointName = onFootSpawnPointName;
        IsPlayerDriving = driving;
        IsPending = true;
        HasArrival = false;
        ArrivalSceneName = null;
        ArrivalSpawnPointName = null;
        ArrivedDriving = false;
    }

    /// <summary>
    /// Called by PlayerSpawner when a scene finished loading. Converts the pending transition (if any)
    /// into the arrival record for <paramref name="sceneName"/>. Returns true if there was a pending transition.
    /// </summary>
    public static bool MarkArrived(string sceneName)
    {
        bool had = IsPending;
        HasArrival = had;
        ArrivalSceneName = sceneName;
        ArrivalSpawnPointName = had ? TargetSpawnPointName : null;
        ArrivedDriving = had && IsPlayerDriving;
        // Don't re-use the spawn point for later (non-transition) respawns / late joiners.
        TargetSpawnPointName = null;
        IsPending = false;
        return had;
    }

    /// <summary>
    /// Read-and-clear the arrival record for <paramref name="sceneName"/>. Returns false (and outputs
    /// defaults) if there is no arrival for that scene, e.g. the very first spawn after hosting.
    /// </summary>
    public static bool TryConsumeArrival(string sceneName, out string spawnPointName, out bool arrivedDriving)
    {
        spawnPointName = null;
        arrivedDriving = false;
        if (!HasArrival || ArrivalSceneName != sceneName) return false;
        spawnPointName = ArrivalSpawnPointName;
        arrivedDriving = ArrivedDriving;
        HasArrival = false;
        ArrivedDriving = false;
        IsPlayerDriving = false;
        return true;
    }

    // Clears the data after it has been used to prevent re-use on fresh scene loads.
    public static void Clear()
    {
        TargetSpawnPointName = null;
        IsPlayerDriving = false;
        OnFootSpawnPointName = null;
        SourceSceneName = null;
        TargetSceneName = null;
        IsPending = false;
        HasArrival = false;
        ArrivalSceneName = null;
        ArrivalSpawnPointName = null;
        ArrivedDriving = false;
        PendingArrivalToast = null;
        PendingArrivalToastStyle = ToastStyle.Good;
    }

    // Statics survive play sessions when domain reload is disabled.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Clear();
}

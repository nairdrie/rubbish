using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using System.Collections.Generic; // Required for OnLoadEventCompleted

/// <summary>
/// Server-side player spawning (lives on the Bootstrap object next to GameStartup/SceneController).
/// Spawns a player for each connecting client, and after every Single scene load despawns and
/// respawns every player at the transition's spawn marker (or the scene's PlayerSpawner-tagged marker).
///
/// Only ONE instance is ever active: the first to wake (Bootstrap). Stray copies left on spawn
/// markers in old scenes remove just their own component and never touch DontDestroyOnLoad.
/// </summary>
public class PlayerSpawner : MonoBehaviour
{
    /// <summary>The active (Bootstrap) spawner.</summary>
    public static PlayerSpawner Instance { get; private set; }

    /// <summary>
    /// Optional spawn pose for a client outside a scene transition (Depot Row: in front of your business's lot). Null or
    /// returning null = the scene's PlayerSpawner marker as before.
    /// </summary>
    public static System.Func<ulong, Pose?> SpawnPoseOverride;

    [SerializeField] private NetworkObject playerPrefab;
    [Tooltip("Sideways spacing between players that arrive at the same marker.")]
    [SerializeField] private float extraPlayerSpacing = 1.2f;

    private Transform defaultSpawner;
    private NetworkManager subscribedTo;
    private int arrivalsThisLoad;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Stray component (e.g. on a tagged spawn marker in an old scene). Remove only the component,
            // never the GameObject (it's the marker), and never DontDestroyOnLoad it.
            enabled = false;
            Destroy(this);
            return;
        }
        Instance = this;
        if (transform.parent == null) DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (Instance != this) return;
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[PlayerSpawner] No NetworkManager; player spawning disabled.");
            return;
        }
        nm.OnServerStarted -= RegisterSpawnHandler;
        nm.OnServerStarted += RegisterSpawnHandler;
        if (nm.IsServer) RegisterSpawnHandler();
    }

    private void RegisterSpawnHandler()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        Unsubscribe();
        subscribedTo = nm;

        // Subscribe to client connections for initial spawn
        nm.OnClientConnectedCallback += SpawnOnConnect;

        // Subscribe to scene load events to re-spawn players
        if (nm.SceneManager != null)
            nm.SceneManager.OnLoadEventCompleted += OnSceneLoaded;
    }

    private void Unsubscribe()
    {
        if (subscribedTo == null) return;
        subscribedTo.OnClientConnectedCallback -= SpawnOnConnect;
        if (subscribedTo.SceneManager != null)
            subscribedTo.SceneManager.OnLoadEventCompleted -= OnSceneLoaded;
        subscribedTo = null;
    }

    /// <summary>
    /// Called every time a scene finishes loading on the server.
    /// This is responsible for re-spawning players who are already connected.
    /// </summary>
    private void OnSceneLoaded(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (loadSceneMode != LoadSceneMode.Single)
        {
            // Additive loads (props, sub-areas) don't move anyone.
            return;
        }
        Debug.Log($"Scene '{sceneName}' loaded. Re-spawning players...");

        // Turn the pending transition into the arrival record TruckRequester reads (keeps IsPlayerDriving).
        SceneTransitionData.MarkArrived(sceneName);
        arrivalsThisLoad = 0;

        // Find the default spawn point for the newly loaded scene.
        defaultSpawner = FlowUtil.FindTagged(FlowUtil.PlayerSpawnerTag);
        if (defaultSpawner != null)
            Debug.Log($"Found default spawner in scene: {sceneName} ({defaultSpawner.name} @ {defaultSpawner.position})");
        else
            Debug.LogWarning($"No GameObject with tag 'PlayerSpawner' found in scene: {sceneName}");

        // Host first, so the host's player gets the arrival marker.
        var nm = NetworkManager.Singleton;
        var ids = new List<ulong>();
        foreach (var client in nm.ConnectedClientsList) ids.Add(client.ClientId);
        ids.Sort((a, b) => (a == nm.LocalClientId ? 0 : 1).CompareTo(b == nm.LocalClientId ? 0 : 1));
        foreach (var id in ids)
            SpawnPlayer(id, true);
    }

    /// <summary>
    /// Spawns a player for a new client connecting to the server.
    /// </summary>
    private void SpawnOnConnect(ulong clientId)
    {
        SpawnPlayer(clientId, false);
    }

    /// <summary>
    /// The core logic for spawning a player object for a given client ID.
    /// </summary>
    private void SpawnPlayer(ulong clientId, bool afterSceneLoad)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || playerPrefab == null)
        {
            Debug.LogError("[PlayerSpawner] Missing NetworkManager or player prefab.");
            return;
        }

        // First, destroy any existing player object for this client to prevent duplicates
        var existing = nm.SpawnManager.GetPlayerNetworkObject(clientId);
        if (existing != null)
        {
            // Never despawn a player while it's seated in (parented under) the truck.
            var pc = existing.GetComponent<PlayerController>();
            if (pc != null && pc.currentTruck != null) pc.currentTruck.ForceExit();
            if (existing.transform.parent != null) existing.transform.SetParent(null, true);
            existing.Despawn(true);
        }

        Debug.Log($"Spawning player prefab for client {clientId}.");

        ResolveSpawnPose(clientId, afterSceneLoad, out Vector3 spawnPos, out Quaternion spawnRot);

        var inst = Instantiate(playerPrefab, spawnPos, spawnRot);
        inst.SpawnAsPlayerObject(clientId);
    }

    void ResolveSpawnPose(ulong clientId, bool afterSceneLoad, out Vector3 pos, out Quaternion rot)
    {
        var nm = NetworkManager.Singleton;
        bool isHost = clientId == nm.LocalClientId;
        Transform marker = null;
        int slot = 0;

        if (afterSceneLoad && SceneTransitionData.HasArrival)
        {
            if (isHost)
            {
                // The host goes to the transition marker (the truck marker when arriving driving).
                marker = FlowUtil.FindMarker(SceneTransitionData.ArrivalSpawnPointName);
                if (marker != null)
                    Debug.Log($"Found transition spawn point: {SceneTransitionData.ArrivalSpawnPointName}");
                else if (!string.IsNullOrEmpty(SceneTransitionData.ArrivalSpawnPointName))
                    Debug.LogWarning($"Could not find spawn point '{SceneTransitionData.ArrivalSpawnPointName}'. Using default.");
            }
            else
            {
                // Other players walk in on foot beside the door (never inside the arriving truck).
                string onFoot = SceneTransitionData.ArrivedDriving ? SceneTransitionData.OnFootSpawnPointName : SceneTransitionData.ArrivalSpawnPointName;
                marker = FlowUtil.FindMarker(onFoot);
                slot = ++arrivalsThisLoad;
            }
        }

        if (marker == null && !(afterSceneLoad && SceneTransitionData.HasArrival) && SpawnPoseOverride != null)
        {
            Pose? over = null;
            try { over = SpawnPoseOverride(clientId); }
            catch (System.Exception e) { Debug.LogException(e); }
            if (over.HasValue)
            {
                pos = over.Value.position;
                rot = over.Value.rotation;
                return;
            }
        }

        if (marker == null)
        {
            if (defaultSpawner == null || !defaultSpawner.gameObject.activeInHierarchy)
                defaultSpawner = FlowUtil.FindTagged(FlowUtil.PlayerSpawnerTag);
            marker = defaultSpawner;
            // Spacing by roster seat (B1): the slot is remembered per PlayerKey, so a rejoin stands in the same place.
            // Seat 0 (the host's) stays on the marker; no seat (no session yet) falls back to the client id.
            if (!isHost && slot == 0)
                slot = SessionService.TryGetSlot(clientId, out int seat) && seat > 0 ? seat : 1 + (int)(clientId % 4);
        }

        if (marker != null)
        {
            pos = marker.position;
            rot = FlowUtil.Upright(marker.rotation);
        }
        else
        {
            Debug.LogError("No default spawn point found in the current scene. Spawning at world origin.");
            pos = Vector3.zero;
            rot = Quaternion.identity;
        }

        // Spread extra players sideways (alternating right/left) so they don't spawn inside each other.
        if (slot > 0)
        {
            float side = ((slot + 1) / 2) * extraPlayerSpacing * (slot % 2 == 1 ? 1f : -1f);
            pos += rot * Vector3.right * side;
        }
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;
        if (NetworkManager.Singleton != null) NetworkManager.Singleton.OnServerStarted -= RegisterSpawnHandler;
        Unsubscribe();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

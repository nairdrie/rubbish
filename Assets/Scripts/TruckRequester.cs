using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

/// <summary>
/// Lives on the Player prefab. Makes sure the ONE truck exists in the right place whenever the host's
/// own player spawns (initial host start and after every scene transition).
///
/// Rules (host's own player only):
///   1. Arrived driving        → spawn the truck at the arrival marker and auto-seat the player in it.
///   2. A truck already exists → never spawn a second one (just record where it is).
///   3. GameSession.TruckSceneName == this scene → spawn it at the saved (parked) pose.
///   4. TruckSceneName empty   → spawn at this scene's TruckSpawner-tagged marker (if any) and record it.
///   5. Otherwise the truck is parked in another scene → no truck here.
/// Trucks are spawned with destroyWithScene: true, so they never pile up across Single scene loads.
///
/// Early game is single-player: remote clients never request trucks (they'd need a server-side
/// request + per-client truck bookkeeping; add that when co-op lands).
/// </summary>
public class TruckRequester : NetworkBehaviour
{
    [Tooltip("Assign the same truck prefab you registered in NetworkManager.NetworkPrefabs")]
    [SerializeField] private NetworkObject truckPrefab;

    [Tooltip("Lift above the saved pose when re-spawning a parked truck (avoids wheels clipping the ground).")]
    [SerializeField] private float respawnLift = 0.15f;

    public override void OnNetworkSpawn()
    {
        // only the client that owns this player should ask for a truck
        if (!IsOwner) return;

        if (!IsServer || DepotRowWorld.Active || DepotRowWorld.HostWantsOneWorld)
        {
            // Remote clients don't get trucks in early game (the host drives the single truck).
            // Co-op one world (Depot Row): every business's truck is spawned at its lot by DepotRowNet instead.
            SceneController.NotifyLocalPlayerArrived();
            return;
        }

        // A tiny delay lets the PlayerObject finish registering before we spawn/parent anything.
        StartCoroutine(SetupTruckNextFrame());
    }

    IEnumerator SetupTruckNextFrame()
    {
        yield return null;
        try
        {
            EnsureTruck();
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
        }
        SceneController.NotifyLocalPlayerArrived();
    }

    void EnsureTruck()
    {
        if (DepotRowWorld.Active || DepotRowWorld.HostWantsOneWorld) return; // Depot Row spawns business trucks itself
        string sceneName = SceneManager.GetActiveScene().name;
        SceneTransitionData.TryConsumeArrival(sceneName, out string arrivalSpawn, out bool arrivedDriving);
        var existing = FlowUtil.FindTruck();

        if (arrivedDriving)
        {
            Pose pose = ResolveArrivalPose(arrivalSpawn);
            var truck = existing;
            if (truck != null) TeleportTruck(truck, pose.position, pose.rotation);
            else truck = SpawnTruck(pose.position, pose.rotation);
            if (truck == null) return;
            FlowUtil.RecordTruck(truck, sceneName);
            truck.ForceEnter(gameObject);
            return;
        }

        if (existing != null)
        {
            // Never spawn a duplicate. Keep bookkeeping honest.
            FlowUtil.RecordTruck(existing, sceneName);
            return;
        }

        if (GameSession.TruckSceneName == sceneName)
        {
            SpawnTruck(GameSession.TruckPosition + Vector3.up * respawnLift, FlowUtil.Upright(GameSession.TruckRotation));
            return;
        }

        if (string.IsNullOrEmpty(GameSession.TruckSceneName))
        {
            var spawner = FlowUtil.FindTagged(FlowUtil.TruckSpawnerTag);
            if (spawner != null && spawner.gameObject.scene == SceneManager.GetActiveScene())
            {
                var truck = SpawnTruck(spawner.position, FlowUtil.Upright(spawner.rotation));
                if (truck != null) FlowUtil.RecordTruck(truck, sceneName);
            }
            else
            {
                Debug.Log($"[TruckRequester] No TruckSpawner in '{sceneName}'; no truck yet.");
            }
            return;
        }

        Debug.Log($"[TruckRequester] Truck is parked in '{GameSession.TruckSceneName}', not here.");
    }

    Pose ResolveArrivalPose(string markerName)
    {
        var marker = FlowUtil.FindMarker(markerName);
        if (marker == null)
        {
            Debug.LogWarning($"[TruckRequester] Arrival marker '{markerName}' not found; using TruckSpawner / player pose.");
            marker = FlowUtil.FindTagged(FlowUtil.TruckSpawnerTag);
            if (marker != null && marker.gameObject.scene != SceneManager.GetActiveScene()) marker = null;
        }
        if (marker != null) return new Pose(marker.position, FlowUtil.Upright(marker.rotation));
        return new Pose(transform.position, FlowUtil.Upright(transform.rotation));
    }

    TruckControllerV2 SpawnTruck(Vector3 position, Quaternion rotation)
    {
        if (truckPrefab == null)
        {
            Debug.LogError("[TruckRequester] No truck prefab assigned on the Player prefab.");
            return null;
        }
        // the server (or host peer) now instantiates & spawns; destroyed with the scene so trucks never accumulate
        var truckInstance = Instantiate(truckPrefab, position, rotation);
        truckInstance.Spawn(destroyWithScene: true);
        Fx.Dust(position + Vector3.up * 0.3f, 1.6f);
        return truckInstance.GetComponent<TruckControllerV2>();
    }

    static void TeleportTruck(TruckControllerV2 truck, Vector3 position, Quaternion rotation)
    {
        var rb = truck.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        truck.transform.SetPositionAndRotation(position, rotation);
        if (rb != null) { rb.position = position; rb.rotation = rotation; }
        Physics.SyncTransforms();
    }
}

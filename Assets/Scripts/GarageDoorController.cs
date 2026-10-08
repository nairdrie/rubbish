using UnityEngine;

/// <summary>
/// A door between the garage (ShopScene) and the Neighborhood. Works on foot and while driving.
/// CONTRACT (fields are set by scene builders): targetSceneName, targetSpawnPointName,
/// targetSpawnPointNameDriving, promptOnFoot, promptDriving.
///
/// Usually paired with an InteractionZone (mode Either) in front of it so you can drive up and press E
/// without aiming. On foot you arrive at targetSpawnPointName; driving you arrive in the truck at
/// targetSpawnPointNameDriving (SceneController / PlayerSpawner / TruckRequester do the rest).
/// <para><b>Depot Row (co-op one world, package N3):</b> a door with <see cref="depotLot"/> ≥ 0 belongs to that lot's shop
/// (built by Setup_DepotRow): <see cref="depotExit"/> false = the street-side garage door, true = the door inside the pocket
/// interior. Using it moves only the local player (fade + teleport, or the truck drives through). In the one world a
/// legacy town door (depotLot −1) is closed.</para>
/// <para><b>Legacy scene flow (solo):</b> the street door of lot <see cref="DepotRowWorld.LegacyShopLot"/> is the garage
/// door: it swaps to ShopScene (on foot, or driving with the truck), and ShopScene's door brings you back to that lot's
/// apron. Every other Depot Row door is shut. One way in, one way out, in both flows.</para>
/// </summary>
public class GarageDoorController : MonoBehaviour, IInteractable
{
    /// <summary>The town scene's name, for code that has no HouseDirectory to ask.</summary>
    public const string DefaultTownScene = "Neighborhood";

    [Header("Scene Transition")]
    [Tooltip("The name of the scene to load.")]
    public string targetSceneName = DefaultTownScene;

    [Header("Depot Row (one world)")]
    [Tooltip("Lot index (0..3) when this is a Depot Row shop door; -1 = a legacy scene door.")]
    public int depotLot = -1;
    [Tooltip("True for the door inside the pocket interior (leads out to the street).")]
    public bool depotExit;
    [Tooltip("Spawn point (GameObject name) in the target scene when arriving on foot.")]
    public string targetSpawnPointName = "SpawnPoint_FromGarage";
    [Tooltip("Spawn point (GameObject name) in the target scene when arriving in the truck.")]
    public string targetSpawnPointNameDriving = "SpawnPoint_FromGarage_Truck";

    [Header("Prompts")]
    public string promptOnFoot = "Exit Garage";
    public string promptDriving = "Drive Out";
    [Tooltip("Shown instead of the on-foot prompt while carrying something (you can't take trash through the door).")]
    public string promptHandsFull = "Hands full! (G to drop)";

    public bool CanInteract()
    {
        if (depotLot >= 0)
        {
            if (DepotRowWorld.Active) return !DepotRowWorld.Transitioning;
            return IsLegacyShopDoor && !SceneController.IsTransitioning; // solo: lot 1's door is the garage door
        }
        if (DepotRowWorld.Active) return false; // a legacy town door is closed in the one world
        return !string.IsNullOrEmpty(targetSceneName) && !SceneController.IsTransitioning;
    }

    /// <summary>This is the street door of the one shop in the legacy scene flow (it leads to ShopScene).</summary>
    public bool IsLegacyShopDoor => depotLot >= 0 && !depotExit && DepotRowWorld.LegacyShopLot == depotLot;

    public string GetInteractionPrompt()
    {
        if (depotLot >= 0 && !depotExit && DepotRowNet.Instance != null && DepotRowNet.Instance.TryGetLot(depotLot, out var lotState)
            && lotState.Claimed && !Permissions.CanWorkIn(Permissions.LocalClientId, depotLot))
            return lotState.Online ? $"Visit {DepotLot.Possessive(lotState.ownerName.ToString())} shop" : "Peek inside (BACK SOON)";
        var p = PlayerController.Local;
        if (p != null && p.IsDriving) return promptDriving;
        var carrier = TrashCarrier.Local;
        if (carrier != null && carrier.IsCarrying) return promptHandsFull;
        return promptOnFoot;
    }

    public void OnInteract(GameObject interactor)
    {
        // Get the PlayerController from the object that interacted with the door.
        PlayerController playerController = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (playerController == null)
        {
            Debug.LogError("Interactor does not have a PlayerController component.");
            return;
        }
        if (!CanInteract()) return;

        bool legacyShop = IsLegacyShopDoor;
        if (depotLot >= 0 && !legacyShop)
        {
            DepotRowWorld.UseDoor(depotLot, depotExit, interactor);
            return;
        }

        bool isDriving = playerController.IsDriving;

        // Bags don't survive a scene change; make the player put it down instead of silently losing it.
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (!isDriving && carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Can't take trash through the door! Drop it first (G)", ToastStyle.Bad, 2f);
            Sfx.Play("buzz", transform.position, 0.7f);
            return;
        }

        if (isDriving && (playerController.currentTruck == null || !playerController.currentTruck.IsLocalPlayerSeated))
        {
            // Driving flag without a truck under us: treat as on foot.
            isDriving = false;
        }

        string spawn = isDriving && !string.IsNullOrEmpty(targetSpawnPointNameDriving) ? targetSpawnPointNameDriving : targetSpawnPointName;
        string scene = targetSceneName, onFootSpawn = targetSpawnPointName;
        if (legacyShop)
        {
            // A Depot Row door carries no scene contract of its own: the legacy shop door always leads into the garage.
            scene = SceneController.GarageSceneName;
            spawn = DepotFlowRules.GarageMarker(isDriving);
            onFootSpawn = DepotFlowRules.GarageMarker(false);
        }

        if (SceneController.Instance != null)
        {
            SceneController.Instance.TransitionToScene(scene, spawn, isDriving, onFootSpawn);
        }
        else
        {
            Debug.LogError("SceneController instance not found in the scene.");
        }
    }

    public void Highlight(bool highlight)
    {
        // Implement highlight logic here, e.g., change color or show an outline
    }
}

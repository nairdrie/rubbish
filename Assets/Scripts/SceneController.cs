using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

/// <summary>
/// Manages scene transitions for the game (lives on the Bootstrap object, DontDestroyOnLoad).
///
/// One transition (host only):
///   fade out → record the truck (parked pose, or "it's coming with me" when driving) → cleanly
///   unseat a driving player (nothing NGO-owned may stay parented under the truck, which is destroyed
///   with the scene) → maybe start a new day → NGO LoadScene(Single) → PlayerSpawner respawns players
///   at the named marker → TruckRequester spawns/seats the truck and calls <see cref="NotifyLocalPlayerArrived"/>
///   → fade in → pending toast.
/// </summary>
public class SceneController : MonoBehaviour
{
    public static SceneController Instance { get; private set; }

    /// <summary>The garage/shop interior. Leaving it (route done + empty truck) starts the next day.</summary>
    public const string GarageSceneName = "ShopScene";

    /// <summary>True from the start of a door transition until the screen has faded back in.</summary>
    public static bool IsTransitioning { get; private set; }

    [Header("Fades")]
    public float fadeOutSeconds = ScreenFader.DefaultFadeOut;
    public float fadeInSeconds = ScreenFader.DefaultFadeIn;
    [Tooltip("Max seconds to wait for the local player (and truck) after the load before fading in anyway.")]
    public float arrivalTimeout = 10f;

    static bool arrivalNotified;
    float transitionStartedAt;
    Coroutine transitionRoutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Only remove the duplicate component; the GameObject may carry other bootstrap components.
            Destroy(this);
            return;
        }
        Instance = this;
        if (transform.parent == null) DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Called on the host by TruckRequester once the local player is spawned and the truck handled.</summary>
    public static void NotifyLocalPlayerArrived()
    {
        arrivalNotified = true;
    }

    /// <summary>
    /// Initiates a scene change, storing transition data first.
    /// </summary>
    /// <param name="sceneName">The name of the scene to load.</param>
    /// <param name="spawnPointName">The name of the spawn point to use in the new scene.</param>
    /// <param name="isDriving">Is the player driving through the transition?</param>
    public void TransitionToScene(string sceneName, string spawnPointName, bool isDriving)
    {
        TransitionToScene(sceneName, spawnPointName, isDriving, null);
    }

    /// <summary>
    /// Same as <see cref="TransitionToScene(string,string,bool)"/>, plus the on-foot marker in the target scene
    /// (where other players go when the host arrives driving).
    /// </summary>
    public void TransitionToScene(string sceneName, string spawnPointName, bool isDriving, string onFootSpawnPointName)
    {
        if (IsTransitioning)
        {
            Debug.Log($"[SceneController] Already transitioning; ignoring request for '{sceneName}'.");
            return;
        }
        // Co-op one world (Depot Row, package N3): no scene swaps. "Go to the garage" = into your own shop, anything
        // else = out onto your lot's apron; only the local player (and their truck) moves.
        if (DepotRowWorld.Active)
        {
            int lot = DepotRowWorld.LocalLot;
            if (lot < 0)
            {
                GameHUD.Toast("You don't have a shop on Depot Row yet!", ToastStyle.Bad);
                return;
            }
            if (sceneName == GarageSceneName) DepotRowWorld.LocalGoInside(lot, isDriving);
            else DepotRowWorld.LocalGoToStreet(lot, false);
            return;
        }
        var nm = NetworkManager.Singleton;
        // Only the server can change scenes in Netcode for GameObjects
        if (nm == null || !nm.IsServer)
        {
            Debug.LogError("Only the server can initiate a scene transition.");
            GameHUD.Toast("Only the host can drive the route (for now)!", ToastStyle.Bad);
            return;
        }
        if (string.IsNullOrEmpty(sceneName) || !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError($"[SceneController] Scene '{sceneName}' is not in the build settings.");
            GameHUD.Toast("The door's jammed!", ToastStyle.Bad);
            Sfx.Play("buzz");
            return;
        }
        transitionStartedAt = Time.unscaledTime;
        transitionRoutine = StartCoroutine(TransitionRoutine(sceneName, spawnPointName, isDriving, onFootSpawnPointName));
    }

    void Update()
    {
        // Watchdog: if a transition coroutine died (exception) never leave the doors locked / screen black.
        if (IsTransitioning && Time.unscaledTime - transitionStartedAt > arrivalTimeout + fadeOutSeconds + fadeInSeconds + 8f)
        {
            Debug.LogWarning("[SceneController] Transition watchdog fired; recovering.");
            if (transitionRoutine != null) StopCoroutine(transitionRoutine);
            transitionRoutine = null;
            IsTransitioning = false;
            FlowCamera.SetCutMode(false);
            ScreenFader.FadeIn(fadeInSeconds);
        }
    }

    IEnumerator TransitionRoutine(string sceneName, string spawnPointName, bool isDriving, string onFootSpawnPointName)
    {
        IsTransitioning = true;
        arrivalNotified = false;
        string fromScene = SceneManager.GetActiveScene().name;
        var truck = FlowUtil.FindTruck();

        // Feel: engine rev / door clunk, then fade.
        if (isDriving)
        {
            Vector3? at = truck != null ? truck.transform.position : (Vector3?)null;
            Sfx.Play("engine_start", at);
            Sfx.Play("horn", at, 0.6f, 1.15f);
            Juice.Shake(0.15f, 0.25f);
        }
        else
        {
            Sfx.Play("door");
        }
        GameHUD.SetContextHint(null);
        yield return ScreenFader.FadeOut(fadeOutSeconds);
        FlowCamera.SetCutMode(true);

        // Truck bookkeeping + clean exit before anything gets destroyed with the scene.
        truck = FlowUtil.FindTruck();
        var local = PlayerController.Local;
        if (isDriving)
        {
            if (truck != null) truck.PrepareForSceneTransition();
            // The truck comes with us; its pose is decided by the arrival marker.
            GameSession.TruckSceneName = sceneName;
        }
        else
        {
            // Defensive: a seated player must never ride a scene unload.
            if (local != null && local.currentTruck != null) local.currentTruck.PrepareForSceneTransition();
            if (truck != null && truck.gameObject.scene.name == fromScene)
                FlowUtil.RecordTruck(truck, fromScene);
        }

        // (No daily reset: the day/night cycle runs on GameClock and houses put trash out continuously.)

        // Trash lying around here (tipped pile, half-sorted table, spilled bits) is put back when we return.
        SceneTrashKeeper.Capture(SceneManager.GetActiveScene());

        SceneTransitionData.Begin(fromScene, sceneName, spawnPointName, isDriving, onFootSpawnPointName);

        var nm = NetworkManager.Singleton;
        var status = nm != null && nm.SceneManager != null
            ? nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single)
            : SceneEventProgressStatus.SceneManagementNotEnabled;
        if (status != SceneEventProgressStatus.Started)
        {
            Debug.LogError($"[SceneController] LoadScene('{sceneName}') failed: {status}");
            SceneTransitionData.Clear();
            SceneTrashKeeper.Forget(fromScene); // nothing unloaded: the trash is all still here
            // Nothing moved: the truck is still here (put the driver back in their seat).
            if (truck != null)
            {
                FlowUtil.RecordTruck(truck, fromScene);
                if (isDriving && local != null) truck.ForceEnter(local.gameObject);
            }
            FlowCamera.SetCutMode(false);
            yield return ScreenFader.FadeIn(fadeInSeconds);
            GameHUD.Toast("The door's jammed!", ToastStyle.Bad);
            Sfx.Play("buzz");
            IsTransitioning = false;
            transitionRoutine = null;
            yield break;
        }

        // Wait for the load + respawn + truck (TruckRequester notifies), with a safety timeout.
        float waited = 0f;
        while (waited < arrivalTimeout && !(arrivalNotified && SceneManager.GetActiveScene().name == sceneName))
        {
            FlowCamera.SetCutMode(true); // catches the new scene's camera brain
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!arrivalNotified)
            Debug.LogWarning($"[SceneController] Arrival in '{sceneName}' not confirmed after {arrivalTimeout:0.#}s; fading in anyway.");

        // Let the camera snap to its new target before revealing.
        FlowCamera.SetCutMode(true);
        yield return new WaitForSecondsRealtime(0.12f);
        FlowCamera.SetCutMode(false);

        yield return ScreenFader.FadeIn(fadeInSeconds);
        IsTransitioning = false;
        transitionRoutine = null;

        if (!string.IsNullOrEmpty(SceneTransitionData.PendingArrivalToast))
        {
            GameHUD.Toast(SceneTransitionData.PendingArrivalToast, SceneTransitionData.PendingArrivalToastStyle, 3.5f);
            Sfx.Play("ding");
            SceneTransitionData.PendingArrivalToast = null;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        IsTransitioning = false;
        arrivalNotified = false;
    }
}

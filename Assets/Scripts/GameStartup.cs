using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Boot object on the Bootstrap scene (DontDestroyOnLoad): loads the main menu and gives <see cref="NetSession"/> the menu
/// scene name and the Relay protocol. Hosting and joining live in <see cref="NetSession"/> (package B1: guarded sign-in,
/// readable errors, Relay sized to the Town cap); <see cref="InitializeHost"/>/<see cref="InitializeClient"/> stay as thin
/// wrappers for older callers.
/// </summary>
public class GameStartup : MonoBehaviour
{
    [Tooltip("Unused since B1: the Town cap decides (NetConstants; Relay allocates TownCap − 1, approval enforces the crew cap).")]
    public int maxPlayers = NetConstants.TownCap;
    [Tooltip("Relay protocol: \"dtls\" (default), \"udp\" or \"wss\"")]
    public string connectionType = "dtls";


    [Tooltip("Name of your main menu scene")]
    [SerializeField] private string mainMenuSceneName = "MainMenuScene";

    /// <summary>Kept for older listeners; <see cref="NetSession.JoinCodeReady"/> is the source.</summary>
    public static event Action<string> OnJoinCodeReady;

    void Awake()
    {

        DontDestroyOnLoad(gameObject);
        NetSession.MenuScene = mainMenuSceneName;
        UnityRelayAdapter.ConnectionType = string.IsNullOrEmpty(connectionType) ? "dtls" : connectionType;
        NetSession.JoinCodeReady -= ForwardJoinCode;
        NetSession.JoinCodeReady += ForwardJoinCode;
        // Immediately load the Main Menu
        SceneManager.LoadScene(mainMenuSceneName);
    }

    void Start()
    {
        var nm = NetBoot.Manager;
        if (nm != null) nm.NetworkConfig.AutoSpawnPlayerPrefabClientSide = false;
    }

    void OnDestroy() => NetSession.JoinCodeReady -= ForwardJoinCode;

    static void ForwardJoinCode(string code) => OnJoinCodeReady?.Invoke(code);

    /// <summary>Host friends over Unity Relay in the current scene (wrapper; prefer <see cref="NetSession.HostAsync"/>).</summary>
    public Task<bool> InitializeHost(int maxPlayers, string connType)
    {
        if (!string.IsNullOrEmpty(connType)) UnityRelayAdapter.ConnectionType = connType;
        return NetSession.HostAsync(new HostOptions
        {
            mode = NetMode.UnityRelay,
            maxPlayers = Mathf.Clamp(maxPlayers, 1, NetConstants.CrewTownCap),
            firstScene = null,
        });
    }

    /// <summary>Join a friend by Relay code (wrapper; prefer <see cref="NetSession.JoinAsync"/>). Latches SaveGuard first.</summary>
    public Task<bool> InitializeClient(string joinCode, string connType)
    {
        if (!string.IsNullOrEmpty(connType)) UnityRelayAdapter.ConnectionType = connType;
        GameConfig.LastJoinCode = SessionKeys.NormalizeJoinCode(joinCode);
        return NetSession.JoinAsync(JoinTarget.Relay(GameConfig.LastJoinCode));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => OnJoinCodeReady = null;
}

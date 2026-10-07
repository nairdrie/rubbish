#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Steam's lifetime for this process (Docs/Multiplayer.md §8.7.4 B3, Accounts.md §1.2). <c>PlayerIdentityBoot</c> decides
/// <i>whether</i> Steam runs; this class only starts it, pumps callbacks every frame, and shuts it down on quit and when the
/// Editor leaves play mode. Everything is a safe no-op without Steam: on a Mac with no Steam client, in batchmode, or where
/// Steamworks isn't compiled, <see cref="TryInit"/> just returns false with a readable reason.
/// <para>App ID: development and playtest builds use Valve's test app 480 (Spacewar) until Rubbish has its own
/// (<see cref="RubbishAppId"/>). <c>RestartAppIfNecessary</c> runs only in release builds with a real App ID.</para>
/// </summary>
public static class SteamBootstrap
{
    /// <summary>Set this to Rubbish's own App ID once Nick's Steamworks account exists (Accounts.md §14). 480 = Spacewar.</summary>
    public const uint RubbishAppId = SteamPolicy.DevAppId;

    public static bool IsInitialized { get; private set; }
    /// <summary>The last init failure, player-readable (null when Steam is up or was never tried).</summary>
    public static string InitError { get; private set; }
    public static bool Attempted { get; private set; }
    /// <summary>The App ID Steam reports (480 for development and playtest builds).</summary>
    public static uint AppId { get; private set; }
    public static ulong LocalSteamId { get; private set; }

    /// <summary>After a successful init (Steam features hook in here).</summary>
    public static event Action Initialized;
    /// <summary>Just before <c>SteamAPI.Shutdown</c> (leave lobbies, cancel proofs, end auth sessions).</summary>
    public static event Action ShuttingDown;
    /// <summary>Every frame while Steam is up, right after callbacks are pumped.</summary>
    public static event Action Ticked;

    public static bool Compiled
    {
        get
        {
#if DISABLESTEAMWORKS
            return false;
#else
            return true;
#endif
        }
    }

    /// <summary>The local Steam name, live (never saved). Null without Steam.</summary>
    public static string LocalName
    {
        get
        {
#if !DISABLESTEAMWORKS
            if (IsInitialized)
            {
                try { return SteamFriends.GetPersonaName(); }
                catch (Exception) { return null; }
            }
#endif
            return null;
        }
    }

    /// <summary>A Steam user's current name ("" while Steam fetches it; asks Steam to fetch it). Display only.</summary>
    public static string NameOf(ulong steamId)
    {
#if !DISABLESTEAMWORKS
        if (!IsInitialized || steamId == 0) return "";
        try
        {
            var id = new CSteamID(steamId);
            if (steamId == LocalSteamId) return SteamFriends.GetPersonaName() ?? "";
            string n = SteamFriends.GetFriendPersonaName(id);
            if (string.IsNullOrEmpty(n) || n == "[unknown]") { SteamFriends.RequestUserInformation(id, true); return ""; }
            return n;
        }
        catch (Exception) { return ""; }
#else
        return "";
#endif
    }

    /// <summary>Whether the Steam overlay is attached (needed for the invite dialog).</summary>
    public static bool OverlayEnabled
    {
        get
        {
#if !DISABLESTEAMWORKS
            if (!IsInitialized) return false;
            try { return SteamUtils.IsOverlayEnabled(); }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Start Steam. Idempotent: true at once when it's already up. False with a player-readable <paramref name="error"/>
    /// otherwise; never throws. In a release build this may relaunch the game through Steam and quit.
    /// </summary>
    public static bool TryInit(out string error)
    {
        error = null;
        if (IsInitialized) return true;
        Attempted = true;
#if DISABLESTEAMWORKS
        error = InitError = "Steam isn't available on this platform.";
        return false;
#else
        try
        {
            if (!Packsize.Test())
            {
                error = InitError = "This build has the wrong Steam library for this computer (Steamworks.NET pack size test failed).";
                return false;
            }
            if (!DllCheck.Test())
            {
                error = InitError = "The Steam library next to the game is the wrong version. Re-extract the game folder.";
                return false;
            }

            bool release = !Application.isEditor && !Debug.isDebugBuild;
#if RUBBISH_PLAYTEST
            bool playtest = true;
#else
            bool playtest = false;
#endif
            if (SteamPolicy.ShouldRestartApp(Application.isEditor, Debug.isDebugBuild, RubbishAppId)
                && SteamAPI.RestartAppIfNecessary(new AppId_t(RubbishAppId)))
            {
                Debug.Log("[Steam] Relaunching through Steam.");
                error = InitError = "Relaunching through Steam…";
                Application.Quit();
                return false;
            }
            if (!release || playtest) EnsureDevAppIdEnvironment();

            ESteamAPIInitResult r = SteamAPI.InitEx(out string msg);
            if (r != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                error = InitError = Describe(r, msg);
                Debug.LogWarning($"[Steam] Steam didn't start: {r} {msg}");
                return false;
            }

            IsInitialized = true;
            InitError = null;
            AppId = SteamUtils.GetAppID().m_AppId;
            LocalSteamId = SteamUser.GetSteamID().m_SteamID;
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception e) { Debug.LogWarning($"[Steam] InitRelayNetworkAccess failed: {e.Message}"); }

            SteamRunner.Ensure();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
            Debug.Log($"[Steam] Steam is up: app {AppId}, signed in as {LocalName} ({LocalSteamId}), " +
                      $"logged on {SteamUser.BLoggedOn()}, overlay {(OverlayEnabled ? "on" : "off (yet)")}.");
            NetAudit.Event("steam", $"init ok app={AppId}");
            Raise(Initialized, "Initialized");
            return true;
        }
        catch (DllNotFoundException e)
        {
            error = InitError = "The Steam library (steam_api) is missing next to the game. Re-extract the game folder.";
            Debug.LogWarning($"[Steam] {e.Message}");
            return false;
        }
        catch (Exception e)
        {
            error = InitError = "Steam couldn't start: " + e.Message;
            Debug.LogWarning($"[Steam] Init threw: {e}");
            return false;
        }
#endif
    }

    /// <summary>
    /// Turn on what Steam gives a session once it's up: the lobby that follows a Friends host, join routing (invites, Join
    /// Game, <c>+connect_lobby</c>), the host's Steam check at the door, and the on-screen Steam layer.
    /// </summary>
    public static void StartFeatures(SteamAuthMode authMode)
    {
        if (!IsInitialized) return;
        SteamLobby.Start();
        SteamJoinRouter.Start();
        SteamAuthVerifier.Install(authMode);
        SteamHud.Ensure();
        HookSession();
    }

    // ------------------------------------------------------------------ B1's session hooks (NetSession / SessionService)

    static Func<PlayerKey, string> previousNames;
    static bool sessionHooked;

    /// <summary>
    /// Plug Steam into B1's session code without editing it: a Friends host opens the lobby (<c>NetSession.HostStarted</c>),
    /// a join target that is only a lobby id is resolved from its <c>rb_*</c> data (<c>NetSession.ResolveTarget</c>), and
    /// roster names come from Steam, never from the join request (<c>SessionService.DisplayNameFor</c>).
    /// </summary>
    static void HookSession()
    {
        if (sessionHooked) return;
        sessionHooked = true;
        NetSession.HostStarted += SteamLobby.OnHostStarted;
        if (NetSession.ResolveTarget == null) NetSession.ResolveTarget = SteamLobby.ResolveTargetAsync;
        previousNames = SessionService.DisplayNameFor;
        SessionService.DisplayNameFor = SteamDisplayName;
        ShuttingDown += UnhookSession;
    }

    static void UnhookSession()
    {
        if (!sessionHooked) return;
        sessionHooked = false;
        NetSession.HostStarted -= SteamLobby.OnHostStarted;
        if (NetSession.ResolveTarget == (Func<JoinTarget, System.Threading.CancellationToken, System.Threading.Tasks.Task<string>>)SteamLobby.ResolveTargetAsync)
            NetSession.ResolveTarget = null;
        if (SessionService.DisplayNameFor == (Func<PlayerKey, string>)SteamDisplayName && previousNames != null)
            SessionService.DisplayNameFor = previousNames;
        ShuttingDown -= UnhookSession;
    }

    /// <summary>A roster name: the Steam persona for <c>steam:</c> keys (live from Steam), else the previous rule.</summary>
    static string SteamDisplayName(PlayerKey k)
    {
        if (k.IsSteam && IsInitialized)
        {
            string n = LobbyInfo.Clean(NameOf(k.SteamId64));
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return previousNames != null ? previousNames(k) : k.Value;
    }

    /// <summary>Leave lobbies, end auth sessions, then <c>SteamAPI.Shutdown</c>. Safe to call twice or without Steam.</summary>
    public static void Shutdown(string reason)
    {
        if (!IsInitialized) return;
        Raise(ShuttingDown, "ShuttingDown");
#if !DISABLESTEAMWORKS
        try { SteamAPI.Shutdown(); }
        catch (Exception e) { Debug.LogWarning($"[Steam] Shutdown threw: {e.Message}"); }
#endif
        IsInitialized = false;
        LocalSteamId = 0;
        Debug.Log($"[Steam] Steam shut down ({reason}).");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif
    }

    /// <summary>Pump Steam callbacks (the runner calls this every frame).</summary>
    internal static void Pump()
    {
        if (!IsInitialized) return;
#if !DISABLESTEAMWORKS
        try { SteamAPI.RunCallbacks(); }
        catch (Exception e) { Debug.LogException(e); }
#endif
        if (Ticked == null) return;
        foreach (Action a in Ticked.GetInvocationList())
        {
            try { a(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

#if !DISABLESTEAMWORKS
    static string Describe(ESteamAPIInitResult r, string msg) => r switch
    {
        ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient => "Steam isn't running. Start Steam and sign in, then try again.",
        ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch => "Steam needs an update. Restart Steam to update it, then try again.",
        _ when (msg ?? "").IndexOf("install directory", StringComparison.OrdinalIgnoreCase) >= 0
            => "Steam isn't installed here (or can't be found). Install Steam and sign in, then try again.",
        _ => "Steam couldn't start" + (string.IsNullOrEmpty(msg) ? "." : ": " + msg),
    };

    /// <summary>
    /// Development and playtest builds: point the Steam API at app 480 even when <c>steam_appid.txt</c> isn't in the working
    /// directory (a shortcut with another "Start in" folder, a Mac launched from Finder). Release builds never do this.
    /// </summary>
    static void EnsureDevAppIdEnvironment()
    {
        try
        {
            string id = RubbishAppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId"))) Environment.SetEnvironmentVariable("SteamAppId", id);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId"))) Environment.SetEnvironmentVariable("SteamGameId", id);
        }
        catch (Exception) { /* best effort; steam_appid.txt still works */ }
    }
#endif

#if UNITY_EDITOR
    static void OnPlayModeChanged(UnityEditor.PlayModeStateChange s)
    {
        if (s == UnityEditor.PlayModeStateChange.ExitingPlayMode) Shutdown("leaving play mode");
    }
#endif

    static void Raise(Action a, string what)
    {
        if (a == null) return;
        foreach (Action h in a.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { Debug.LogError($"[Steam] A {what} handler failed: {e}"); }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // With domain reload off the native Steam state survives play sessions; it was shut down when play mode ended.
        IsInitialized = false;
        Attempted = false;
        InitError = null;
        AppId = 0;
        LocalSteamId = 0;
        Initialized = null;
        ShuttingDown = null;
        Ticked = null;
        previousNames = null;
        sessionHooked = false;
    }
}

/// <summary>Hidden DontDestroyOnLoad object that pumps Steam every frame and shuts it down on quit.</summary>
[AddComponentMenu("")]
sealed class SteamRunner : MonoBehaviour
{
    static SteamRunner instance;

    internal static void Ensure()
    {
        if (instance != null) return;
        var go = new GameObject("[Steam]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SteamRunner>();
    }

    void Update() => SteamBootstrap.Pump();

    void OnApplicationQuit() => SteamBootstrap.Shutdown("quit");

    void OnDestroy()
    {
        if (instance == this) instance = null;
        SteamBootstrap.Shutdown("runner destroyed");
    }
}

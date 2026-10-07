using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Runtime network configuration (D5, package B1). The Bootstrap scene is never edited or saved, so the NetworkManager
/// it holds is configured here as soon as it exists: connection approval on (<see cref="SessionApproval"/>), the tick rate
/// pinned to <see cref="NetConstants.TickRate"/>, and NGO's lifecycle events forwarded to <see cref="NetSession"/>.
/// <para>Every machine runs this, so host and client agree on the NetworkConfig hash (a mismatch would drop the client
/// without a reason). NGO's own <c>ProtocolVersion</c> is deliberately left alone: a mismatch there also drops the client
/// silently, while <see cref="JoinRequest"/>'s protocol check answers "Update your game".</para>
/// <para>This is the only file under <c>Net/**</c> that reads <c>NetworkManager.Singleton</c> (lint rule
/// <c>netsingleton</c>); everything else asks <see cref="Manager"/>.</para>
/// </summary>
public static class NetBoot
{
    static NetworkManager configured;
    static Runner runner;
    static bool quitApproved, quitting;

    /// <summary>The app (or the Editor's play mode) is shutting down: no new objects, coroutines or scene loads.</summary>
    public static bool Quitting => quitting;

    /// <summary>The game's NetworkManager, configured for sessions (null when the scene has none, e.g. edit mode).</summary>
    public static NetworkManager Manager
    {
        get
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm != configured) Configure(nm);
            return nm;
        }
    }

    /// <summary>Run a coroutine on the hidden session runner (created on demand; null when not playing).</summary>
    public static Coroutine Run(IEnumerator routine)
    {
        if (routine == null || !Application.isPlaying || quitting) return null;
        EnsureRunner();
        return runner != null ? runner.StartCoroutine(routine) : null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        configured = null;
        runner = null;
        quitApproved = false;
        quitting = false;
    }

    // NGO clears its static events at SubsystemRegistration, so hook after that (same pattern as SaveGuard).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        NetSession.RegisterDefaultAdapters();
        NetworkManager.OnInstantiated -= Configure;
        NetworkManager.OnInstantiated += Configure;
        NetworkManager.OnDestroying -= OnDestroying;
        NetworkManager.OnDestroying += OnDestroying;
        Application.wantsToQuit -= OnWantsToQuit;
        Application.wantsToQuit += OnWantsToQuit;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
#if UNITY_EDITOR
        // NGO shuts down on ExitingPlayMode (before Application.quitting): flag it first so a Stop isn't a "lost connection".
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        if (NetworkManager.Singleton != null) Configure(NetworkManager.Singleton);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Late()
    {
        if (NetworkManager.Singleton != null) Configure(NetworkManager.Singleton);
    }

    static void Configure(NetworkManager nm)
    {
        if (nm == null || nm == configured) return;
        Unhook(configured);
        configured = nm;

        var cfg = nm.NetworkConfig;
        if (cfg == null) { Debug.LogError("[NetBoot] The NetworkManager has no NetworkConfig."); return; }
        if (nm.IsListening)
            Debug.LogWarning("[NetBoot] The NetworkManager was already running; connection approval applies from the next session.");
        else
        {
            cfg.ConnectionApproval = true;
            cfg.TickRate = NetConstants.TickRate;
        }
        // We are the only approval: replace anything set before us (the setter refuses multicast delegates).
        nm.ConnectionApprovalCallback = null;
        nm.ConnectionApprovalCallback = SessionApproval.OnApproval;

        nm.OnServerStarted += OnServerStarted;
        nm.OnServerStopped += OnServerStopped;
        nm.OnClientStarted += OnClientStarted;
        nm.OnClientStopped += OnClientStopped;
        nm.OnClientConnectedCallback += OnClientConnected;
        nm.OnClientDisconnectCallback += OnClientDisconnected;
        nm.OnTransportFailure += OnTransportFailure;
        EnsureRunner();
        Debug.Log($"[NetBoot] Sessions ready: approval on, protocol {NetVersion.Protocol}, content {NetVersion.Content}, " +
                  $"tick {cfg.TickRate}, Town cap {NetConstants.TownCap}.");
    }

    static void Unhook(NetworkManager nm)
    {
        if (nm == null) return;
        nm.OnServerStarted -= OnServerStarted;
        nm.OnServerStopped -= OnServerStopped;
        nm.OnClientStarted -= OnClientStarted;
        nm.OnClientStopped -= OnClientStopped;
        nm.OnClientConnectedCallback -= OnClientConnected;
        nm.OnClientDisconnectCallback -= OnClientDisconnected;
        nm.OnTransportFailure -= OnTransportFailure;
        if (nm.ConnectionApprovalCallback == (Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse>)SessionApproval.OnApproval)
            nm.ConnectionApprovalCallback = null;
    }

    static void OnDestroying(NetworkManager nm)
    {
        if (nm != configured) return;
        Unhook(nm);
        configured = null;
    }

    static void OnServerStarted() => Guard(() => NetSession.HandleServerStarted(configured));
    static void OnServerStopped(bool wasHost) => Guard(() => NetSession.HandleServerStopped(configured, wasHost));
    static void OnClientStarted() => Guard(() => NetSession.HandleClientStarted(configured));
    static void OnClientStopped(bool wasHost) => Guard(() => NetSession.HandleClientStopped(configured, wasHost));
    static void OnClientConnected(ulong id) => Guard(() => NetSession.HandleClientConnected(configured, id));
    static void OnClientDisconnected(ulong id) => Guard(() => NetSession.HandleClientDisconnected(configured, id));
    static void OnTransportFailure() => Guard(() => NetSession.HandleTransportFailure(configured));

    static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>
    /// Quitting while hosting friends: tell them "The Boss went home" first, then quit a moment later (otherwise they'd sit
    /// in a frozen world until their connection times out).
    /// </summary>
    static bool OnWantsToQuit()
    {
        if (quitApproved) { quitting = true; return true; }
        var nm = configured;
        if (nm == null || !nm.IsListening || !nm.IsServer || nm.ConnectedClientsIds.Count <= 1) { quitting = true; return true; }
        quitApproved = true;
        NetSession.LeaveForQuit();
        if (runner == null) { quitting = true; return true; }
        runner.StartCoroutine(QuitSoon());
        return false;
    }

    static void OnQuitting() => quitting = true;

#if UNITY_EDITOR
    static void OnPlayModeChanged(UnityEditor.PlayModeStateChange change)
    {
        if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) quitting = true;
    }
#endif

    static IEnumerator QuitSoon()
    {
        yield return new WaitForSecondsRealtime(0.4f);
        Application.Quit();
    }

    static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying || quitting) return;
        var go = new GameObject("[NetSession]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
    }

    sealed class Runner : MonoBehaviour
    {
        void OnApplicationQuit() => quitting = true;

        void Update() => SessionApproval.Tick(Time.realtimeSinceStartupAsDouble);

        void OnDestroy()
        {
            if (runner == this) runner = null;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum NetSessionState { Idle, Preparing, Hosting, Joining, Connected, Leaving, Failed }

/// <summary>How to host (D8: the host picks a Town × a company slot; default Continue).</summary>
public sealed class HostOptions
{
    /// <summary>
    /// <see cref="NetMode.Offline"/> = solo: LocalDirect on 127.0.0.1 with a port the OS picks, refusing every remote
    /// client. <see cref="NetMode.LocalDirect"/> = bots / MPPM / LAN on <see cref="port"/>. <see cref="NetMode.UnityRelay"/>
    /// = friends by join code.
    /// </summary>
    public NetMode mode = NetMode.Offline;
    public string townGuid, companyGuid;
    /// <summary>Players in this Town, the host included (at most <see cref="NetConstants.TownCap"/> = 4).</summary>
    public int maxPlayers = NetConstants.TownCap;
    /// <summary>Loaded locally before StartHost (guests are synced to it). Null or empty: stay in the current scene.</summary>
    public string firstScene = "ShopScene"; // coop:allow(scene) contract default (MP §8.7.3 C1); B1/B9 move to a ZoneId-driven flow
    /// <summary>Friends host: also create the Steam lobby (B10).</summary>
    public bool steamLobby;
    /// <summary>LocalDirect only: where to listen (127.0.0.1 = this PC only; 0.0.0.0 = LAN).</summary>
    public string listenAddress = "127.0.0.1";
    public ushort port = NetConstants.DefaultPort;
}

/// <summary>
/// The one entry point for hosting, joining and leaving (package B1).
/// <list type="bullet">
/// <item><see cref="HostAsync"/>: prepares the transport (Relay allocation, socket), loads the first scene, starts the host.
/// Solo is LocalDirect and works with no internet and no sign-in.</item>
/// <item><see cref="JoinAsync"/>: latches <see cref="SaveGuard"/> first, prepares the transport, sends a
/// <see cref="JoinRequest"/> and waits for the host's answer. No scene is loaded locally: NGO syncs the host's scene.
/// Any failure shuts down, reloads the player's own save and leaves them in the menu with a readable
/// <see cref="LastError"/> (and <see cref="Failed"/>).</item>
/// <item><see cref="Leave"/>: the host tells guests "The Boss went home" first; everyone ends back in the menu with their own
/// save reloaded (<see cref="SaveGuard.ReloadOwn"/>). A dropped connection, a kick or the host leaving does the same and
/// raises <see cref="Ended"/> with the reason.</item>
/// </list>
/// Raw <c>NetworkManager.StartHost()</c> calls (Progression's <c>host.cs</c>/<c>host2.cs</c>, tools) are adopted as a
/// LocalDirect session, so approval, slots and the roster work for them too.
/// </summary>
public static class NetSession
{
    /// <summary>Seconds a join may take (transport + approval + scene sync start) before we give up.</summary>
    public const float JoinTimeoutSeconds = 25f;
    /// <summary>RubbishAssets key of the session prefab (NetRoster and friends), built by Setup_Coop.</summary>
    public const string SessionPrefabKey = "net/session";

    static readonly Dictionary<NetMode, INetTransportAdapter> adapters = new Dictionary<NetMode, INetTransportAdapter>();
    static INetTransportAdapter active;
    static bool solo, ending, warnedNoPrefab, rawJoin;
    static int joinWait;              // 0 waiting, 1 connected, -1 dropped
    static string joinDropReason;     // NGO's disconnect reason captured while joining
    static bool joinClosedByHost;     // the host closed the connection while we waited at the door
    static PlayerKey? localKey;
    static NetworkObject sessionObject;

    public static NetSessionState State { get; private set; }
    /// <summary>The transport of the current (or last) session.</summary>
    public static NetMode Mode { get; private set; }
    /// <summary>The Relay code friends type (null when not hosting over Relay).</summary>
    public static string JoinCode { get; private set; }
    public static string SessionId { get; private set; }
    /// <summary>The last failure as a player-facing sentence.</summary>
    public static string LastError { get; private set; }
    /// <summary>Why the last session ended (left, host went home, kicked, lost).</summary>
    public static SessionEndReason LastEnd { get; private set; }

    /// <summary>Hosting or connected as a guest.</summary>
    public static bool InSession => State == NetSessionState.Hosting || State == NetSessionState.Connected;
    public static bool IsHost => State == NetSessionState.Hosting;
    /// <summary>Hosting a solo game (nobody else can join).</summary>
    public static bool IsSolo => solo && State == NetSessionState.Hosting;
    /// <summary>Preparing, joining, in a session or leaving: Host/Join are refused until this is false.</summary>
    public static bool Busy => State == NetSessionState.Preparing || State == NetSessionState.Joining ||
                               State == NetSessionState.Hosting || State == NetSessionState.Connected ||
                               State == NetSessionState.Leaving;

    /// <summary>The main menu scene (GameStartup sets it from its inspector field). Leaving and failures return here.</summary>
    public static string MenuScene { get; set; }

    /// <summary>
    /// Set by the menu while it's open, so failures know whether a scene load is needed to get back there.
    /// </summary>
    public static bool MenuOpen { get; set; }

    public static event Action<NetSessionState> StateChanged;
    public static event Action<string> JoinCodeReady, Failed;
    /// <summary>A session ended without the player asking (host went home, kicked, connection lost): a readable sentence.</summary>
    public static event Action<string> Ended;
    /// <summary>A host session started: (options, join code or null). B10 creates the Steam lobby here.</summary>
    public static event Action<HostOptions, string> HostStarted;

    /// <summary>
    /// A guest is about to leave on purpose: last words to the host (N4 payroll settles a job's final pay so it lands in the
    /// business the guest takes home). Returns how long (seconds, ≤ 1.5) to stay connected for the answer; 0 = nothing to say.
    /// </summary>
    public static Func<float> GuestLeaving;

    /// <summary>
    /// Optional join-target resolver (B10: a Steam lobby id → its <c>rb_net</c>/<c>rb_relay</c>). Returns null to go ahead
    /// or a readable error to stop. It may rewrite the target (mode, code, address).
    /// </summary>
    public static Func<JoinTarget, CancellationToken, Task<string>> ResolveTarget;

    /// <summary>
    /// Who this machine is at the door: <c>PlayerIdentity.Local</c> (B3), else <c>-playerid</c>, else <c>dev:editor</c> in the
    /// Editor, else a random <c>dev:p-xxxxxxxx</c> for this run. Fixed once read.
    /// </summary>
    public static PlayerKey LocalPlayerKey
    {
        get
        {
            if (localKey.HasValue) return localKey.Value;
            PlayerKey k = default;
            try
            {
                var id = PlayerIdentity.Local;
                if (id != null && id.Key.IsValid) k = id.Key;
            }
            catch (Exception e) { Debug.LogException(e); }
            if (!k.IsValid)
            {
                // A build that requires Steam but couldn't start it has no identity (Accounts.md §1.2): never fix a dev key
                // for the rest of the run, so "Try again" on the Steam screen still gets the Steam key. Host/Join refuse.
                if (PlayerIdentityBoot.SteamMissing) return FallbackKey();
                k = FallbackKey();
            }
            localKey = k;
            return k;
        }
    }

    // ------------------------------------------------------------------ host

    public static async Task<bool> HostAsync(HostOptions o, CancellationToken ct = default)
    {
        if (o == null) throw new ArgumentNullException(nameof(o));
        if (Busy) return Fail("You're already in a game. Leave it first.", raise: false);
        if (PlayerIdentityBoot.SteamMissing) return Fail(NeedsSteam);
        var nm = NetBoot.Manager;
        if (nm == null) return Fail("Networking isn't set up here (start the game from the Bootstrap scene).");
        if (nm.IsListening || nm.ShutdownInProgress) return Fail("The last game is still closing. Try again in a moment.");

        bool isSolo = o.mode == NetMode.Offline;
        NetMode mode = isSolo ? NetMode.LocalDirect : o.mode;
        if (!adapters.TryGetValue(mode, out var adapter)) return Fail($"Hosting over {mode} isn't available in this build.");

        LastError = null;
        JoinCode = null;
        SessionId = SafeFile.NewSessionId(DateTime.UtcNow);
        solo = isSolo;
        Mode = mode;
        ending = false;
        SetState(NetSessionState.Preparing);
        bool sceneLoaded = false;
        try
        {
            if (adapter is LocalDirectAdapter direct)
            {
                direct.Port = isSolo ? (ushort)0 : (o.port == 0 ? NetConstants.DefaultPort : o.port);
                direct.ListenAddress = isSolo || string.IsNullOrEmpty(o.listenAddress) ? "127.0.0.1" : o.listenAddress;
            }
            int townCap = Mathf.Clamp(o.maxPlayers, 1, NetConstants.TownCap);
            // Relay allocates TownCap − 1 = 3 (§13.10); approval refuses a 5th player with "Town is full (4/4)".
            int remote = mode == NetMode.UnityRelay ? NetConstants.TownCap - 1 : Math.Max(1, townCap - 1);
            HostPrep prep = await adapter.PrepareHostAsync(nm, remote, ct);
            if (!prep.Ok) return AbortHost(nm, adapter, prep.Error, false);
            if (ct.IsCancellationRequested) return AbortHost(nm, adapter, null, false);

            // host.cs ordering: the first scene is loaded locally, then StartHost; guests are synced to it when they join.
            if (!string.IsNullOrEmpty(o.firstScene))
            {
                sceneLoaded = true;
                if (!await LoadSceneLocal(o.firstScene)) return AbortHost(nm, adapter, $"Couldn't load the scene '{o.firstScene}'.", true);
            }

            SessionService.BeginSession(townCap, NetConstants.CompanyCap, isSolo);
            bool started;
            try { started = nm.StartHost(); }
            catch (Exception e) { Debug.LogException(e); started = false; }
            if (!started)
                return AbortHost(nm, adapter, isSolo
                    ? "Couldn't start the game (networking failed to start)."
                    : "Couldn't open the game. Another copy of Rubbish may already be hosting on this PC.", sceneLoaded);

            active = adapter;
            SetState(NetSessionState.Hosting);
            if (!string.IsNullOrEmpty(prep.JoinCode)) RaiseJoinCode(prep.JoinCode);
            Debug.Log($"[NetSession] Hosting {(isSolo ? "solo" : mode.ToString())} as {LocalPlayerKey}" +
                      (prep.JoinCode != null ? $", join code {prep.JoinCode}" : "") + ".");
            NetAudit.Event("session", $"host {(isSolo ? "solo" : mode.ToString())} key {LocalPlayerKey} session {SessionId}");
            if (prep.JoinCode != null) NetAudit.Event("joincode", prep.JoinCode);
            try { HostStarted?.Invoke(o, prep.JoinCode); }
            catch (Exception e) { Debug.LogException(e); }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return AbortHost(nm, adapter, "Couldn't start the game: " + SessionEndReason.Clean(e.Message, 120), sceneLoaded);
        }
    }

    static bool AbortHost(NetworkManager nm, INetTransportAdapter adapter, string error, bool backToMenu)
    {
        try
        {
            if (nm != null && nm.IsListening) nm.Shutdown();
            adapter?.OnShutdown(nm);
        }
        catch (Exception e) { Debug.LogException(e); }
        SessionService.EndSession();
        solo = false;
        JoinCode = null;
        if (backToMenu) ReturnToMenu();
        if (error == null) { LastError = "Cancelled."; SetState(NetSessionState.Idle); return false; }
        return Fail(error);
    }

    /// <summary>The host's own approval (inside StartHost) and raw StartHost calls: make sure a session exists.</summary>
    internal static void EnsureHostSession()
    {
        if (SessionService.Active) return;
        SessionService.BeginSession(NetConstants.TownCap, NetConstants.CompanyCap, false);
    }

    // ------------------------------------------------------------------ join

    /// <summary>
    /// Join a game. Latches SaveGuard before anything else (no scene load happens before it); on failure the player's own
    /// save is reloaded and they're back in the menu with <see cref="LastError"/> set.
    /// </summary>
    public static async Task<bool> JoinAsync(JoinTarget t, CancellationToken ct = default)
    {
        if (t == null) throw new ArgumentNullException(nameof(t));
        if (Busy) return Fail("You're already in a game. Leave it first.", raise: false);
        if (PlayerIdentityBoot.SteamMissing) return Fail(NeedsSteam);
        var nm = NetBoot.Manager;
        if (nm == null) return Fail("Networking isn't set up here (start the game from the Bootstrap scene).");
        if (nm.IsListening || nm.ShutdownInProgress) return Fail("The last game is still closing. Try again in a moment.");

        SaveGuard.MarkForeign("join"); // from here until ReloadOwn nothing is saved
        LastError = null;
        JoinCode = null;
        SessionId = null;
        solo = false;
        ending = false;
        Mode = t.mode;
        SetState(NetSessionState.Joining);
        INetTransportAdapter adapter = null;
        string error;
        try { error = await JoinCore(nm, t, ct, a => adapter = a); }
        catch (Exception e)
        {
            Debug.LogException(e);
            error = "Couldn't join: " + SessionEndReason.Clean(e.Message, 120);
        }
        if (error == null)
        {
            active = adapter;
            SetState(NetSessionState.Connected);
            Debug.Log($"[NetSession] Joined over {Mode} as {LocalPlayerKey} (client {nm.LocalClientId}).");
            NetAudit.Event("session", $"joined {Mode} key {LocalPlayerKey} client {nm.LocalClientId}");
            return true;
        }

        // Failure: stop the network, put our own save back, make sure we're in the menu.
        await StopNetwork(nm);
        try { adapter?.OnShutdown(nm); }
        catch (Exception e) { Debug.LogException(e); }
        CancelTicket();
        SaveGuard.ReloadOwn("join failed");
        if (!MenuOpen) ReturnToMenu();
        if (error == Cancelled)
        {
            // The player cancelled, or Leave() took over (it finishes the clean-up and sets Idle itself).
            LastError = Cancelled;
            if (State == NetSessionState.Joining) SetState(NetSessionState.Idle);
            return false;
        }
        NetAudit.Event("session", "join failed: " + error);
        return Fail(error);
    }

    const string Cancelled = "Cancelled.";
    /// <summary>This build plays as a Steam account and Steam isn't up (the "Rubbish needs Steam" screen explains the rest).</summary>
    const string NeedsSteam = "Rubbish needs Steam. Start Steam and sign in, then press Try again.";
    const string NoAnswer = "Couldn't reach the host. Check the code, or ask your friend to host again.";
    const string TurnedAway = "The host turned you away. Their Town may be full (4 max), or you're on different versions of the game.";

    /// <summary>Null when connected; otherwise a readable error.</summary>
    static async Task<string> JoinCore(NetworkManager nm, JoinTarget t, CancellationToken ct, Action<INetTransportAdapter> got)
    {
        if (ResolveTarget != null)
        {
            string err = await ResolveTarget(t, ct);
            if (err != null) return err;
            Mode = t.mode;
        }
        if (!adapters.TryGetValue(t.mode, out var adapter)) return $"Joining over {t.mode} isn't available in this build.";
        got(adapter);
        if (!await adapter.PrepareClientAsync(nm, t, ct))
            return ct.IsCancellationRequested ? Cancelled : (adapter as UnityRelayAdapter)?.LastError ?? NoAnswer;
        if (ct.IsCancellationRequested) return Cancelled;

        byte[] request;
        try { request = BuildJoinRequest(t).Encode(); }
        catch (ArgumentException e)
        {
            Debug.LogWarning("[NetSession] Join request too large: " + e.Message);
            return "Couldn't build the join request (it's too large).";
        }
        nm.NetworkConfig.ConnectionData = request;
        joinWait = 0;
        joinDropReason = null;
        joinClosedByHost = false;
        bool started;
        try { started = nm.StartClient(); }
        catch (Exception e) { Debug.LogException(e); started = false; }
        if (!started) return "Couldn't start the connection.";

        float deadline = Time.realtimeSinceStartup + JoinTimeoutSeconds;
        while (joinWait == 0)
        {
            if (ct.IsCancellationRequested || State != NetSessionState.Joining) return Cancelled; // cancelled, or Leave()
            if (Time.realtimeSinceStartup > deadline) return NoAnswer;
            await Task.Yield();
        }
        if (joinWait > 0) return null;
        // The host's sentence travels on the reliable channel; on a lossy link the disconnect itself can overtake it. Then all we
        // know is that the host closed the door on us, which is still better than "couldn't reach the host".
        string reason = SessionEndReason.TryDecode(joinDropReason, out _) || !joinClosedByHost
            ? SessionEndReason.Describe(joinDropReason, NoAnswer)
            : TurnedAway;
        Debug.LogWarning($"[NetSession] Couldn't join: {reason}");
        return reason;
    }

    static JoinRequest BuildJoinRequest(JoinTarget t)
    {
        var req = JoinRequest.ForThisBuild(LocalPlayerKey);
        var id = PlayerIdentity.Local;
        if (id != null && id.IsSteam && t.hostSteamId != 0)
        {
            try { req.authTicket = id.GetJoinTicket(t.hostSteamId) ?? Array.Empty<byte>(); }
            catch (Exception e) { Debug.LogWarning("[NetSession] Steam join check unavailable: " + e.Message); }
        }
        // Development builds and the Editor: -proto <n> pretends to be another version (the bots' mismatch test).
        if (Debug.isDebugBuild && CoreSmoke.TryGetArg(Environment.GetCommandLineArgs(), "-proto", out string p) &&
            ushort.TryParse(p, out ushort proto))
        {
            req.proto = proto;
            Debug.Log($"[NetSession] -proto {proto}: joining as protocol {proto} (this build is {NetVersion.Protocol}).");
        }
        return req;
    }

    // ------------------------------------------------------------------ leave

    /// <summary>
    /// Leave the session (or give up joining) and go back to the menu. The host tells guests "The Boss went home" first.
    /// Always ends in <see cref="SaveGuard.ReloadOwn"/>.
    /// </summary>
    public static void Leave(string reason = null) => BeginLeave(reason, toMenu: true);

    /// <summary>Quitting the game: same as Leave without loading the menu (NetBoot quits a moment later).</summary>
    internal static void LeaveForQuit() => BeginLeave("quit", toMenu: false);

    static void BeginLeave(string reason, bool toMenu)
    {
        reason = string.IsNullOrEmpty(reason) ? "leave" : reason;
        var nm = NetBoot.Manager;
        if (State == NetSessionState.Leaving || ending) return;
        ending = true;
        bool wasHost = nm != null && nm.IsListening && nm.IsServer;
        int told = 0;
        SetState(NetSessionState.Leaving);
        if (wasHost)
        {
            string bye = SessionEndReason.Encode(JoinReject.HostLeaving);
            foreach (ulong id in new List<ulong>(nm.ConnectedClientsIds))
            {
                if (id == NetworkManager.ServerClientId) continue;
                try { nm.DisconnectClient(id, bye); told++; }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
        float lastWords = 0f;
        if (!wasHost && nm != null && nm.IsListening && GuestLeaving != null)
        {
            try { lastWords = Mathf.Clamp(GuestLeaving(), 0f, 1.5f); }
            catch (Exception e) { Debug.LogException(e); }
        }
        Debug.Log($"[NetSession] Leaving ({reason}){(told > 0 ? $"; told {told} guest(s) the Boss went home" : "")}.");
        NetAudit.Event("session", $"leave {reason}");
        var end = new SessionEndReason(SessionEndReason.Left, reason);
        if (NetBoot.Run(Finish(nm, end, Mathf.Max(told > 0 ? 0.3f : 0f, lastWords), toMenu, raise: false)) == null)
        {
            // Not playing (edit-mode tests): finish synchronously.
            if (nm != null && nm.IsListening) nm.Shutdown();
            Complete(nm, end, toMenu, raise: false);
        }
    }

    static IEnumerator Finish(NetworkManager nm, SessionEndReason end, float delay, bool toMenu, bool raise)
    {
        yield return null; // never finish inside an NGO callback (scene loads and save reloads happen outside its shutdown)
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        if (nm != null && nm.IsListening && !nm.ShutdownInProgress) nm.Shutdown();
        float until = Time.realtimeSinceStartup + 3f;
        while (nm != null && nm.IsListening && Time.realtimeSinceStartup < until) yield return null;
        Complete(nm, end, toMenu, raise);
    }

    static void Complete(NetworkManager nm, SessionEndReason end, bool toMenu, bool raise)
    {
        try { active?.OnShutdown(nm); }
        catch (Exception e) { Debug.LogException(e); }
        active = null;
        sessionObject = null;
        SessionService.EndSession();
        CancelTicket();
        SaveGuard.ReloadOwn(end.Code == SessionEndReason.Left ? end.Text : "session ended: " + end.Code);
        solo = false;
        JoinCode = null;
        LastEnd = end;
        ending = false;
        SetState(NetSessionState.Idle);
        if (toMenu) ReturnToMenu();
        if (!raise) return;
        string text = DescribeEnd(end);
        LastError = text;
        Debug.Log($"[NetSession] Session ended: {end.Code}: {text}");
        try { Ended?.Invoke(text); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>The sentence for an ended session.</summary>
    public static string DescribeEnd(SessionEndReason end)
    {
        if (end.TryGetReject(out var r) && r == JoinReject.HostLeaving) return "The Boss went home. The shift is over.";
        if (end.Code == SessionEndReason.Lost && !string.IsNullOrEmpty(end.Text)) return end.Text;
        return SessionEndReason.Describe(SessionEndReason.Encode(end.Code, end.Text), LostText);
    }

    const string LostText = "The Boss's truck broke down: the connection to the host was lost.";

    static void UnexpectedEnd(NetworkManager nm, bool wasHost, string wire)
    {
        if (NetBoot.Quitting)
        {
            // Quitting or leaving play mode: no menu, no reload, no new objects. LegacySave's quit flush is still latched.
            active = null;
            sessionObject = null;
            SessionService.EndSession();
            SetState(NetSessionState.Idle);
            return;
        }
        if (ending || State == NetSessionState.Leaving) return;
        ending = true;
        SetState(NetSessionState.Leaving);
        SessionEndReason end;
        if (wasHost) end = new SessionEndReason(SessionEndReason.Lost, "The game stopped: the network connection failed.");
        else if (!SessionEndReason.TryDecode(wire, out end))
        {
            // No reason (host crashed) or NGO's own words (a timeout): our sentence, NGO's detail in brackets.
            string detail = SessionEndReason.Clean(wire, 80);
            end = new SessionEndReason(SessionEndReason.Lost, detail.Length == 0 ? LostText : $"{LostText} ({detail})");
        }
        NetAudit.Event("session", $"ended {end.Code}");
        if (NetBoot.Run(Finish(nm, end, 0f, toMenu: true, raise: true)) == null) Complete(nm, end, true, true);
    }

    // ------------------------------------------------------------------ NGO events (from NetBoot)

    internal static void HandleServerStarted(NetworkManager nm)
    {
        if (nm == null) return;
        if (State != NetSessionState.Preparing && State != NetSessionState.Hosting)
        {
            // A raw StartHost (host.cs/host2.cs, tools): adopt it as a LocalDirect session.
            EnsureHostSession();
            SessionId = SafeFile.NewSessionId(DateTime.UtcNow);
            Mode = DetectMode(nm);
            solo = false;
            ending = false;
            active = adapters.TryGetValue(Mode, out var a) ? a : null;
            SetState(NetSessionState.Hosting);
            Debug.Log($"[NetSession] Adopted a direct StartHost as a {Mode} session (host {LocalPlayerKey}).");
            NetAudit.Event("session", $"host {Mode} (direct StartHost) key {LocalPlayerKey} session {SessionId}");
        }
        SessionService.KickHandler = (id, why) =>
        {
            var m = NetBoot.Manager;
            if (m != null && m.IsServer && id != NetworkManager.ServerClientId)
                m.DisconnectClient(id, SessionEndReason.Encode(SessionEndReason.Kicked, why));
        };
        SpawnSessionObject(nm);
    }

    internal static void HandleServerStopped(NetworkManager nm, bool wasHost)
    {
        sessionObject = null;
        if (State == NetSessionState.Hosting && !ending) UnexpectedEnd(nm, true, null);
        else if (!ending && State != NetSessionState.Preparing) SessionService.EndSession();
    }

    /// <summary>
    /// A raw <c>StartClient()</c> (tools, test scripts) isn't refused for lack of a join request: it gets this build's
    /// request (NGO sends it once the transport connects, after this) and is followed as a session.
    /// </summary>
    internal static void HandleClientStarted(NetworkManager nm)
    {
        if (nm == null || nm.IsServer || State == NetSessionState.Joining) return;
        var cfg = nm.NetworkConfig;
        if (cfg.ConnectionData == null || cfg.ConnectionData.Length == 0)
        {
            try { cfg.ConnectionData = BuildJoinRequest(new JoinTarget()).Encode(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        rawJoin = true;
        joinWait = 0;
        joinDropReason = null;
        joinClosedByHost = false;
        ending = false;
        Mode = DetectMode(nm);
        SessionId = null;
        SetState(NetSessionState.Joining);
        Debug.Log($"[NetSession] Following a direct StartClient over {Mode} as {LocalPlayerKey}.");
    }

    internal static void HandleClientStopped(NetworkManager nm, bool wasHost)
    {
        if (wasHost) return; // the host's stop is handled by HandleServerStopped
        string reason = nm != null ? nm.DisconnectReason : null;
        if (State == NetSessionState.Joining)
        {
            if (joinWait == 0) { joinDropReason ??= reason; joinClosedByHost |= ClosedByHost(nm); joinWait = -1; }
            if (rawJoin)
            {
                rawJoin = false;
                LastError = SessionEndReason.TryDecode(reason, out _) || !joinClosedByHost ? SessionEndReason.Describe(reason, NoAnswer) : TurnedAway;
                Debug.LogWarning($"[NetSession] Couldn't join: {LastError}");
                SetState(NetSessionState.Idle);
            }
            return;
        }
        if (State == NetSessionState.Connected && !ending) UnexpectedEnd(nm, false, reason);
    }

    internal static void HandleClientConnected(NetworkManager nm, ulong clientId)
    {
        if (nm == null) return;
        if (nm.IsServer)
        {
            if (clientId != NetworkManager.ServerClientId && SessionService.TryGetPlayer(clientId, out var k))
                Debug.Log($"[NetSession] {k} is in (client {clientId}).");
            return;
        }
        if (State == NetSessionState.Joining && clientId == nm.LocalClientId)
        {
            joinWait = 1;
            if (rawJoin)
            {
                rawJoin = false;
                SetState(NetSessionState.Connected);
                NetAudit.Event("session", $"joined {Mode} (direct StartClient) key {LocalPlayerKey} client {nm.LocalClientId}");
            }
        }
    }

    internal static void HandleClientDisconnected(NetworkManager nm, ulong clientId)
    {
        if (nm == null) return;
        if (nm.IsServer)
        {
            SessionApproval.ClientGone(clientId);
            if (SessionService.TryGetPlayer(clientId, out var k))
            {
                SessionService.Unbind(clientId);
                Debug.Log($"[NetSession] {k} left (client {clientId}).");
                NetAudit.Event("roster", $"left {k} client {clientId}");
            }
            return;
        }
        if (State == NetSessionState.Joining && joinWait == 0 &&
            (clientId == nm.LocalClientId || clientId == NetworkManager.ServerClientId))
        {
            joinDropReason = nm.DisconnectReason;
            joinClosedByHost |= ClosedByHost(nm);
            joinWait = -1;
        }
    }

    static bool ClosedByHost(NetworkManager nm) =>
        nm != null && nm.DisconnectEvent == NetworkTransport.DisconnectEvents.ClosedByRemote;

    internal static void HandleTransportFailure(NetworkManager nm)
    {
        Debug.LogWarning("[NetSession] The network transport failed.");
        NetAudit.Event("session", "transport failure");
        if (State == NetSessionState.Joining && joinWait == 0)
        {
            joinDropReason = SessionEndReason.Encode(SessionEndReason.Lost, NoAnswer);
            joinWait = -1;
        }
    }

    // ------------------------------------------------------------------ helpers

    public static void RegisterAdapter(INetTransportAdapter a)
    {
        if (a == null) throw new ArgumentNullException(nameof(a));
        adapters[a.Mode] = a;
    }

    public static bool TryGetAdapter(NetMode mode, out INetTransportAdapter a) => adapters.TryGetValue(mode, out a);

    /// <summary>LocalDirect and UnityRelay (NetBoot calls this at boot; later packages add SteamSdr).</summary>
    internal static void RegisterDefaultAdapters()
    {
        if (!adapters.ContainsKey(NetMode.LocalDirect)) RegisterAdapter(new LocalDirectAdapter());
        if (!adapters.ContainsKey(NetMode.UnityRelay)) RegisterAdapter(new UnityRelayAdapter());
    }

    static NetMode DetectMode(NetworkManager nm)
    {
        var ut = nm.NetworkConfig.NetworkTransport as UnityTransport;
        return ut != null && ut.Protocol == UnityTransport.ProtocolType.RelayUnityTransport ? NetMode.UnityRelay : NetMode.LocalDirect;
    }

    static void SpawnSessionObject(NetworkManager nm)
    {
        if (sessionObject != null && sessionObject.IsSpawned) return;
        sessionObject = null;
        GameObject prefab = null;
        try { prefab = RubbishAssets.Prefab(SessionPrefabKey); }
        catch (Exception e) { Debug.LogException(e); }
        if (prefab == null)
        {
            if (!warnedNoPrefab)
                Debug.LogWarning("[NetSession] NetSession.prefab isn't built yet (menu Rubbish > Setup > Coop): no roster this " +
                                 "session, so Dumpling variants and spawn spacing fall back to client ids.");
            warnedNoPrefab = true;
            return;
        }
        var go = UnityEngine.Object.Instantiate(prefab);
        var no = go.GetComponent<NetworkObject>();
        if (no == null)
        {
            UnityEngine.Object.Destroy(go);
            Debug.LogWarning("[NetSession] NetSession.prefab has no NetworkObject.");
            return;
        }
        try
        {
            no.Spawn(destroyWithScene: false);
            sessionObject = no;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            UnityEngine.Object.Destroy(go);
        }
    }

    static async Task<bool> LoadSceneLocal(string scene)
    {
        if (!Application.CanStreamedLevelBeLoaded(scene)) return false;
        var op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
        if (op == null) return false;
        while (!op.isDone) await Task.Yield();
        await Task.Yield(); // let the new scene's Awake/Start run before StartHost
        return true;
    }

    static async Task StopNetwork(NetworkManager nm)
    {
        if (nm == null) return;
        if (nm.IsListening && !nm.ShutdownInProgress) nm.Shutdown();
        float until = Time.realtimeSinceStartup + 3f;
        while ((nm.IsListening || nm.ShutdownInProgress) && Time.realtimeSinceStartup < until) await Task.Yield();
    }

    /// <summary>Back to the main menu: free the cursor, clear any UI lock and fade, load the menu scene.</summary>
    static void ReturnToMenu()
    {
        int guard = 0;
        while (PlayerController.IsUIBlocking && guard++ < 64) PlayerController.PopUILock();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (ScreenFader.Alpha > 0f) ScreenFader.SetAlpha(0f);
        if (MenuOpen || NetBoot.Quitting) return;
        if (string.IsNullOrEmpty(MenuScene) || !Application.CanStreamedLevelBeLoaded(MenuScene))
        {
            Debug.LogWarning("[NetSession] No main menu scene to return to.");
            return;
        }
        SceneManager.LoadScene(MenuScene, LoadSceneMode.Single);
    }

    static void CancelTicket()
    {
        try
        {
            var id = PlayerIdentity.Local;
            if (id != null && id.IsSteam) id.CancelJoinTicket();
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    static PlayerKey FallbackKey()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (CoreSmoke.TryGetArg(args, "-playerid", out string v) && !string.IsNullOrEmpty(v))
        {
            if (PlayerKey.TryParse(v, out var parsed)) return parsed;
            try { return PlayerKey.Dev(v); }
            catch (ArgumentException) { Debug.LogWarning($"[NetSession] Ignoring -playerid '{v}' (use a-z 0-9 . _ -)."); }
        }
        if (Application.isEditor) return PlayerKey.Dev("editor");
        var bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return PlayerKey.Dev("p-" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant());
    }

    static bool Fail(string error, bool raise = true)
    {
        LastError = error;
        Debug.LogWarning("[NetSession] " + error);
        if (!raise) return false;
        SetState(NetSessionState.Failed);
        try { Failed?.Invoke(error); }
        catch (Exception e) { Debug.LogException(e); }
        return false;
    }

    static void SetState(NetSessionState s)
    {
        if (State == s) return;
        State = s;
        try { StateChanged?.Invoke(s); }
        catch (Exception e) { Debug.LogException(e); }
    }

    internal static void RaiseJoinCode(string code)
    {
        JoinCode = code;
        try { JoinCodeReady?.Invoke(code); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        adapters.Clear();
        active = null;
        solo = ending = warnedNoPrefab = rawJoin = false;
        joinWait = 0;
        joinDropReason = null;
        joinClosedByHost = false;
        localKey = null;
        sessionObject = null;
        State = NetSessionState.Idle;
        Mode = NetMode.Offline;
        JoinCode = SessionId = LastError = null;
        LastEnd = default;
        MenuOpen = false;
        StateChanged = null;
        JoinCodeReady = null;
        Failed = null;
        Ended = null;
        HostStarted = null;
        ResolveTarget = null;
    }
}

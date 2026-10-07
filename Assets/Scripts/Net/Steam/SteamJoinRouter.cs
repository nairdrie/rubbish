#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Turns Steam's "join my friend" signals into a <see cref="NetSession.JoinAsync"/> (Docs/Multiplayer.md §8.7.4 B10):
/// <list type="bullet">
/// <item><c>GameLobbyJoinRequested_t</c>: an invite accepted, or Join Game on a friend who's in a lobby, while running;</item>
/// <item><c>GameRichPresenceJoinRequested_t</c>: Join Game through our rich-presence <c>connect</c> string;</item>
/// <item>cold start: <c>+connect_lobby &lt;id&gt;</c> on the command line (Steam launching the game for a join; with app 480
/// Steam launches Spacewar instead, so playtesters start Rubbish first and then accept);</item>
/// <item><c>NewUrlLaunchParameters_t</c> / <c>SteamApps.GetLaunchCommandLine</c> (a <c>steam://run</c> link while running).</item>
/// </list>
/// Steps: leave whatever session we're in (back to the menu), enter the Steam lobby (so the host sees us as a member), read
/// its <c>rb_*</c> data (<see cref="LobbyInfo"/>: right game, same protocol, a Relay code), pre-warm our Steam proof bound to
/// the host, then <c>NetSession.JoinAsync</c> over Relay. When the session ends we leave the lobby. Errors are shown, logged
/// and never thrown.
/// </summary>
public static class SteamJoinRouter
{
    public enum Phase : byte { Idle, WaitingForMenu, EnteringLobby, ReadingLobby, Preparing, Joining, InSession }

    public static Phase State { get; private set; }
    /// <summary>The lobby being joined or played in (0 when idle).</summary>
    public static ulong TargetLobbyId { get; private set; }
    public static string LastError { get; private set; }
    /// <summary>Something is in progress (the HUD shows "Joining…").</summary>
    public static bool Busy => State != Phase.Idle && State != Phase.InSession;

    const float SettleSeconds = 1.25f, ColdStartSettleSeconds = 2.5f, LeaveTimeoutSeconds = 20f;
    const float ReadLobbySeconds = 3f, ProofWaitSeconds = 2.5f, SelfTestDelaySeconds = 2f;
    public const string SelfTestFlag = "-steamselftest";

    static float phaseSince, idleSince, settle;
    static string source;
    static JoinTarget target;
    static string hostName;
    static Task<bool> joinTask;
    static CancellationTokenSource joinCts;
    static bool sawSession;
    static float selfTestAt = -1f;
    static ulong coldStartLobby;
    static string coldStartWhy;
    static bool started;
#if !DISABLESTEAMWORKS
    static Callback<GameLobbyJoinRequested_t> lobbyJoinCb;
    static Callback<GameRichPresenceJoinRequested_t> presenceJoinCb;
    static Callback<NewUrlLaunchParameters_t> urlLaunchCb;
#endif

    internal static void Start()
    {
        if (started) return;
        started = true;
#if !DISABLESTEAMWORKS
        lobbyJoinCb = Callback<GameLobbyJoinRequested_t>.Create(e => RequestJoin(e.m_steamIDLobby.m_SteamID, "Steam invite / Join Game"));
        presenceJoinCb = Callback<GameRichPresenceJoinRequested_t>.Create(e =>
        {
            if (ConnectArgs.TryParse(e.m_rgchConnect, out ulong id)) RequestJoin(id, "Join Game");
            else Debug.LogWarning("[SteamJoin] Join Game sent a connect string we don't understand.");
        });
        urlLaunchCb = Callback<NewUrlLaunchParameters_t>.Create(_ => CheckLaunchCommandLine("steam:// link"));
#endif
        SteamBootstrap.Ticked += Tick;
        SteamBootstrap.ShuttingDown += Stop;

        // Cold start: Steam (or a shortcut) launched us to join a lobby. Handled from the first Tick (scenes are up by then).
        if (ConnectArgs.TryParse(Environment.GetCommandLineArgs(), out ulong coldLobby)) { coldStartLobby = coldLobby; coldStartWhy = "launch command line"; }
        else CheckLaunchCommandLine("launch");

        if (SteamPolicy.HasFlag(Environment.GetCommandLineArgs(), SelfTestFlag)) selfTestAt = Time.realtimeSinceStartup + SelfTestDelaySeconds;
    }

    static void Stop()
    {
#if !DISABLESTEAMWORKS
        lobbyJoinCb?.Dispose(); lobbyJoinCb = null;
        presenceJoinCb?.Dispose(); presenceJoinCb = null;
        urlLaunchCb?.Dispose(); urlLaunchCb = null;
#endif
        SteamBootstrap.Ticked -= Tick;
        SteamBootstrap.ShuttingDown -= Stop;
        started = false;
        if (Busy) HideBusy();
        SetPhase(Phase.Idle);
        TargetLobbyId = 0;
    }

    /// <summary>
    /// Join the Rubbish game in Steam lobby <paramref name="lobbyId"/> (from an invite, Join Game, the command line, or a
    /// debug call). Leaves the current session first.
    /// </summary>
    public static void RequestJoin(ulong lobbyId, string why, float settleSeconds = SettleSeconds)
    {
        if (!SteamBootstrap.IsInitialized) { Debug.Log("[SteamJoin] Ignoring a join request: Steam isn't up."); return; }
        if (!ConnectArgs.LooksLikeLobbyId(lobbyId)) { Debug.LogWarning($"[SteamJoin] Ignoring a join request for {lobbyId}: not a lobby id."); return; }
        if (lobbyId == SteamLobby.CurrentLobbyId && (SteamLobby.IsHostLobby || State == Phase.InSession))
        {
            SteamHud.Say("You're already in that game.", false);
            return;
        }
        if (Busy)
        {
            if (lobbyId != TargetLobbyId) SteamHud.Say("Already joining a game… try again in a moment.", false);
            return;
        }
        Debug.Log($"[SteamJoin] Join requested for lobby {lobbyId} ({why}).");
        NetAudit.Event("steam", $"join requested {lobbyId} ({why})");
        TargetLobbyId = lobbyId;
        source = why;
        target = null;
        hostName = null;
        joinTask = null;
        sawSession = false;
        LastError = null;
        settle = settleSeconds;
        idleSince = -1f;

        var st = NetSession.State;
        bool leaving = st != NetSessionState.Idle && st != NetSessionState.Failed;
        Busy_("Joining your friend's game…");
        if (leaving)
        {
            SteamHud.Say("Leaving this game to join your friend…", false);
            try { NetSession.Leave("joining a friend on Steam"); }
            catch (Exception e) { Debug.LogException(e); }
        }
        if (SteamLobby.CurrentLobbyId != 0 && SteamLobby.CurrentLobbyId != lobbyId) SteamLobby.Leave("joining another game");
        SetPhase(Phase.WaitingForMenu);
    }

    static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (coldStartLobby != 0)
        {
            ulong id = coldStartLobby;
            coldStartLobby = 0;
            RequestJoin(id, coldStartWhy ?? "launch", ColdStartSettleSeconds);
        }
        if (selfTestAt >= 0f && now >= selfTestAt)
        {
            selfTestAt = -1f;
            SteamLobby.RunSelfTest((ok, msg) =>
            {
                if (ok) Debug.Log("STEAM SELFTEST OK (" + msg + ")");
                else Debug.LogWarning("STEAM SELFTEST FAIL: " + msg);
                SteamHud.Say(ok ? "Steam self-test passed." : "Steam self-test failed: " + msg, !ok);
            });
        }

        switch (State)
        {
            case Phase.WaitingForMenu:
            {
                var st = NetSession.State;
                var nm = NetBoot.Manager;
                bool netFree = nm == null || (!nm.IsListening && !nm.ShutdownInProgress);
                if ((st == NetSessionState.Idle || st == NetSessionState.Failed) && netFree)
                {
                    if (idleSince < 0f) idleSince = now;
                    // Wait for the menu to be up (B1 sets MenuOpen), or a few seconds if this scene has no menu.
                    bool menuReady = NetSession.MenuOpen || now - idleSince > 4f;
                    if (now - idleSince >= settle && menuReady) EnterLobby();
                }
                else
                {
                    idleSince = -1f;
                    if (now - phaseSince > LeaveTimeoutSeconds) Fail("Couldn't leave the current game to join your friend.");
                }
                break;
            }
            case Phase.ReadingLobby:
                if (SteamLobby.TryReadCurrent(out LobbyInfo info, out string err)) OnLobbyRead(info);
                else if (now - phaseSince > ReadLobbySeconds) Fail("That isn't a Rubbish game you can join (" + err + ").");
                break;
            case Phase.Preparing:
            {
                var me = SteamIdentity.Local;
                if (me == null || me.IsProofReadyFor(target.hostSteamId) || now - phaseSince > ProofWaitSeconds) StartJoin();
                break;
            }
            case Phase.Joining:
                if (joinTask == null || !joinTask.IsCompleted) break;
                bool ok = joinTask.Status == TaskStatus.RanToCompletion && joinTask.Result;
                if (joinTask.IsFaulted) Debug.LogWarning($"[SteamJoin] JoinAsync threw: {joinTask.Exception?.GetBaseException().Message}");
                joinTask = null;
                if (!ok)
                {
                    // NetSession raised Failed (B1's error panel shows it) unless the player cancelled.
                    Fail(string.IsNullOrEmpty(NetSession.LastError) ? "Couldn't join your friend's game." : NetSession.LastError, showError: false);
                    break;
                }
                HideBusy();
                SetPhase(Phase.InSession);
                SteamPresence.SetLobby(TargetLobbyId, SteamLobby.MemberCount, false);
                Debug.Log($"[SteamJoin] In {(string.IsNullOrEmpty(hostName) ? "the host" : hostName)}'s game (lobby {TargetLobbyId}).");
                NetAudit.Event("steam", $"joined {TargetLobbyId}");
                break;
            case Phase.InSession:
            {
                var st = NetSession.State;
                if (st == NetSessionState.Connected || st == NetSessionState.Joining) sawSession = true;
                bool ended = st == NetSessionState.Idle || st == NetSessionState.Failed || st == NetSessionState.Leaving
                             || st == NetSessionState.Hosting;
                if ((sawSession || now - phaseSince > 10f) && ended)
                {
                    SteamLobby.Leave("session ended");
                    TargetLobbyId = 0;
                    SetPhase(Phase.Idle);
                }
                else if (SteamLobby.CurrentLobbyId != TargetLobbyId && SteamLobby.CurrentLobbyId == 0)
                {
                    // Steam dropped us from the lobby (it closed); the network session decides when we leave the game.
                    TargetLobbyId = 0;
                    SetPhase(Phase.Idle);
                }
                break;
            }
        }
    }

    static void EnterLobby()
    {
        SetPhase(Phase.EnteringLobby);
        Busy_("Joining your friend's game…");
        ulong id = TargetLobbyId;
        SteamLobby.Join(id, (ok, err) =>
        {
            if (State != Phase.EnteringLobby || TargetLobbyId != id) return;
            if (!ok) { Fail(err); return; }
            SetPhase(Phase.ReadingLobby);
        });
    }

    static void OnLobbyRead(LobbyInfo info)
    {
        ulong owner = SteamLobby.OwnerId;
        if (owner != 0 && owner == SteamBootstrap.LocalSteamId) { Fail("That's your own game."); return; }
        if (!info.TryGetJoinTarget(TargetLobbyId, owner, out JoinTarget t, out JoinReject why, out string err))
        {
            Fail(err ?? JoinRejectText.For(why));
            return;
        }
        target = t;
        hostName = !string.IsNullOrEmpty(info.hostName) ? info.hostName : SteamBootstrap.NameOf(owner);
        Busy_($"Joining {(string.IsNullOrEmpty(hostName) ? "your friend" : hostName)}'s shift…");
        Debug.Log($"[SteamJoin] Lobby {TargetLobbyId} read: {info}. Host steam:{owner}.");
        SteamIdentity.Local?.Prewarm(owner);
        SetPhase(Phase.Preparing);
    }

    static void StartJoin()
    {
        SetPhase(Phase.Joining);
        joinCts?.Dispose();
        joinCts = new CancellationTokenSource();
        try { joinTask = NetSession.JoinAsync(target, joinCts.Token); }
        catch (Exception e)
        {
            joinTask = null;
            Fail("Couldn't start joining: " + e.Message);
        }
    }

    static void Fail(string message, bool showError = true)
    {
        LastError = message;
        Debug.LogWarning($"[SteamJoin] Join via {source ?? "Steam"} failed: {message}");
        NetAudit.Event("steam", "join failed: " + message);
        HideBusy();
        if (showError) ShowError(message);
        if (SteamLobby.CurrentLobbyId != 0 && !SteamLobby.IsHostLobby) SteamLobby.Leave("join failed");
        TargetLobbyId = 0;
        target = null;
        SetPhase(Phase.Idle);
    }

    /// <summary>The player pressed Cancel on the "Joining…" panel.</summary>
    public static void Cancel()
    {
        if (!Busy) return;
        Debug.Log("[SteamJoin] Cancelled by the player.");
        if (State == Phase.Joining && joinCts != null) { joinCts.Cancel(); return; } // JoinAsync cleans up; Tick sees the result
        Fail("Cancelled.", showError: false);
    }

    // The session UI is B1's NetErrorPanel (busy spinner + error card); SteamHud is the fallback when it isn't there.
    static void Busy_(string message)
    {
        if (Application.isBatchMode) return;
        try { NetErrorPanel.ShowBusy(message, Cancel); }
        catch (Exception e) { Debug.LogWarning($"[SteamJoin] Busy panel unavailable ({e.Message})."); SteamHud.Say(message, false); }
    }

    static void HideBusy()
    {
        if (Application.isBatchMode) return;
        try { NetErrorPanel.HideBusy(); }
        catch (Exception) { }
    }

    static void ShowError(string message)
    {
        if (Application.isBatchMode || message == "Cancelled.") return;
        try { NetErrorPanel.Show("Couldn't join", message); }
        catch (Exception e) { Debug.LogWarning($"[SteamJoin] Error panel unavailable ({e.Message})."); SteamHud.Say(message, true); }
    }

    static void SetPhase(Phase p)
    {
        State = p;
        phaseSince = Time.realtimeSinceStartup;
    }

    static void CheckLaunchCommandLine(string why)
    {
#if !DISABLESTEAMWORKS
        if (!SteamBootstrap.IsInitialized) return;
        try
        {
            int n = SteamApps.GetLaunchCommandLine(out string line, ConnectArgs.MaxLineChars);
            if (n <= 0 || !ConnectArgs.TryParse(line, out ulong id)) return;
            if (why == "launch") { coldStartLobby = id; coldStartWhy = why; }
            else RequestJoin(id, why);
        }
        catch (Exception e) { Debug.LogWarning($"[SteamJoin] GetLaunchCommandLine threw: {e.Message}"); }
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        State = Phase.Idle;
        TargetLobbyId = 0;
        LastError = null;
        target = null;
        hostName = null;
        joinTask = null;
        joinCts = null;
        sawSession = false;
        selfTestAt = -1f;
        coldStartLobby = 0;
        coldStartWhy = null;
        started = false;
#if !DISABLESTEAMWORKS
        lobbyJoinCb = null;
        presenceJoinCb = null;
        urlLaunchCb = null;
#endif
    }
}

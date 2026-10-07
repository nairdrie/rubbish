#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// The friends-only Steam lobby (Docs/Multiplayer.md §8.7.4 B10, D6). Until the Steam transport exists the lobby is only a
/// signpost: it carries <c>rb_net=relay</c> + <c>rb_relay=&lt;Unity Relay join code&gt;</c> (see <see cref="LobbyInfo"/>), and
/// gives friends invites and <b>Join Game</b> from the Steam friends list.
/// <para><b>Host:</b> while <see cref="AutoHostLobby"/> is on, a lobby opens by itself whenever <see cref="NetSession"/> is
/// <c>Hosting</c> with a Relay join code (a Friends host), and closes when hosting ends. <see cref="OpenForHost"/> and
/// <see cref="Leave"/> are the explicit calls. Cap = Crew Town cap.</para>
/// <para><b>Guest:</b> <see cref="SteamJoinRouter"/> enters the lobby (so the host can check membership), reads it, and
/// joins over Relay.</para>
/// Safe without Steam: every call is a no-op that says so.
/// </summary>
public static class SteamLobby
{
    /// <summary>Follow NetSession: open a lobby for every Relay host, close it when hosting ends.</summary>
    public static bool AutoHostLobby = true;

    public static ulong CurrentLobbyId { get; private set; }
    /// <summary>True when we created <see cref="CurrentLobbyId"/> (we're the host).</summary>
    public static bool IsHostLobby { get; private set; }
    /// <summary>What the lobby advertises (host: what we wrote; guest: what we read). Null without a lobby.</summary>
    public static LobbyInfo Info { get; private set; }
    public static bool Creating { get; private set; }
    /// <summary>Members, lobby joins and leaves, data changes.</summary>
    public static event Action Changed;

    const float GuestIdleLeaveSeconds = 5f;
    static float guestIdleSince = -1f;
    static bool guestPresenceSet;
    const int MaxCreateAttempts = 3;
    const float CreateTimeoutSeconds = 15f;
    static float creatingSince;
    const float CreateRetrySeconds = 5f;
    static int createAttempts;
    static float nextCreateAt;
    static bool started;

#if !DISABLESTEAMWORKS
    static CallResult<LobbyCreated_t> createCall;
    static CallResult<LobbyEnter_t> joinCall;
    static Callback<LobbyChatUpdate_t> chatUpdateCb;
    static Callback<LobbyDataUpdate_t> dataUpdateCb;
#endif

    internal static void Start()
    {
        if (started) return;
        started = true;
#if !DISABLESTEAMWORKS
        createCall = CallResult<LobbyCreated_t>.Create();
        joinCall = CallResult<LobbyEnter_t>.Create();
        chatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        dataUpdateCb = Callback<LobbyDataUpdate_t>.Create(OnDataUpdate);
#endif
        SteamBootstrap.Ticked += Tick;
        SteamBootstrap.ShuttingDown += Stop;
    }

    static void Stop()
    {
        Leave("Steam shutting down");
#if !DISABLESTEAMWORKS
        createCall?.Dispose(); createCall = null;
        joinCall?.Dispose(); joinCall = null;
        chatUpdateCb?.Dispose(); chatUpdateCb = null;
        dataUpdateCb?.Dispose(); dataUpdateCb = null;
#endif
        SteamBootstrap.Ticked -= Tick;
        SteamBootstrap.ShuttingDown -= Stop;
        started = false;
        Creating = false;
    }

    /// <summary>The lobby's owner (the host), or 0.</summary>
    public static ulong OwnerId
    {
        get
        {
#if !DISABLESTEAMWORKS
            if (CurrentLobbyId == 0 || !SteamBootstrap.IsInitialized) return 0;
            try { return SteamMatchmaking.GetLobbyOwner(new CSteamID(CurrentLobbyId)).m_SteamID; }
            catch (Exception) { return 0; }
#else
            return 0;
#endif
        }
    }

    public static int MemberCount
    {
        get
        {
#if !DISABLESTEAMWORKS
            if (CurrentLobbyId == 0 || !SteamBootstrap.IsInitialized) return 0;
            try { return SteamMatchmaking.GetNumLobbyMembers(new CSteamID(CurrentLobbyId)); }
            catch (Exception) { return 0; }
#else
            return 0;
#endif
        }
    }

    /// <summary>Is <paramref name="steamId"/> in our current lobby? (Only answerable while we're in it.)</summary>
    public static bool IsMember(ulong steamId)
    {
#if !DISABLESTEAMWORKS
        if (CurrentLobbyId == 0 || steamId == 0 || !SteamBootstrap.IsInitialized) return false;
        try
        {
            var lobby = new CSteamID(CurrentLobbyId);
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == steamId) return true;
        }
        catch (Exception) { }
#endif
        return false;
    }

    // ------------------------------------------------------------------ host

    /// <summary>
    /// Open a friends-only lobby advertising this Relay session. False (with a log) when Steam is down, a lobby is already
    /// being made, or the code isn't a Relay code. The lobby id arrives a moment later (<see cref="Changed"/>).
    /// </summary>
    public static bool OpenForHost(string relayCode, string townName, string sessionId)
    {
        if (!SteamBootstrap.IsInitialized) { Debug.Log("[SteamLobby] No Steam: no lobby (friends can still use the join code)."); return false; }
        var info = LobbyInfo.ForRelayHost(relayCode, townName, sessionId, SteamBootstrap.LocalName);
        if (!LobbyInfo.IsValidRelayCode(info.relay)) { Debug.LogWarning("[SteamLobby] Not opening a lobby: no Relay join code yet."); return false; }
        if (Creating) return false;
        if (CurrentLobbyId != 0)
        {
            if (IsHostLobby) { WriteHostData(info); return true; }
            Leave("hosting now");
        }
#if !DISABLESTEAMWORKS
        try
        {
            Creating = true;
            creatingSince = Time.realtimeSinceStartup;
            Info = info;
            createCall.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, NetConstants.TownCap), OnCreated);
            Debug.Log("[SteamLobby] Opening a friends-only Steam lobby…");
            return true;
        }
        catch (Exception e)
        {
            Creating = false;
            Debug.LogWarning($"[SteamLobby] CreateLobby threw: {e.Message}");
            return false;
        }
#else
        return false;
#endif
    }

#if !DISABLESTEAMWORKS
    static void OnCreated(LobbyCreated_t r, bool ioFailure)
    {
        Creating = false;
        if (ioFailure || r.m_eResult != EResult.k_EResultOK || r.m_ulSteamIDLobby == 0)
        {
            if (CurrentLobbyId == 0) Info = null;
            createAttempts++;
            nextCreateAt = Time.realtimeSinceStartup + CreateRetrySeconds;
            Debug.LogWarning($"[SteamLobby] Steam couldn't open a lobby ({(ioFailure ? "I/O failure" : r.m_eResult.ToString())}); " +
                             $"attempt {createAttempts}/{MaxCreateAttempts}. Friends can still join with the code.");
            if (createAttempts >= MaxCreateAttempts) SteamHud.Say("Steam couldn't open a lobby. Friends can still join with the join code.", true);
            return;
        }
        ulong id = r.m_ulSteamIDLobby;
        if (!WantHostLobby(out string code))
        {
            // Hosting ended while Steam was busy: don't leave a stray lobby behind.
            try { SteamMatchmaking.LeaveLobby(new CSteamID(id)); } catch (Exception) { }
            if (CurrentLobbyId == 0) Info = null;
            Debug.Log("[SteamLobby] Hosting ended before the lobby opened; closed it.");
            return;
        }
        CurrentLobbyId = id;
        IsHostLobby = true;
        createAttempts = 0;
        var info = Info ?? LobbyInfo.ForRelayHost(code, null, NetSession.SessionId, SteamBootstrap.LocalName);
        if (code != null && info.relay != LobbyInfo.NormalizeRelayCode(code)) info.relay = LobbyInfo.NormalizeRelayCode(code);
        WriteHostData(info);
        try { SteamMatchmaking.SetLobbyJoinable(new CSteamID(id), true); } catch (Exception) { }
        SteamPresence.SetLobby(id, MemberCount, true);
        Debug.Log($"[SteamLobby] Lobby {id} open for friends ({info}).");
        NetAudit.Event("steam", $"lobby open {id}");
        SteamHud.Say(SteamBootstrap.OverlayEnabled
            ? "Steam lobby open: friends can Join Game from your Steam profile, or press F2 to invite them."
            : "Steam lobby open: friends can right-click you in Steam and pick Join Game.", false);
        RaiseChanged();
    }
#endif

    static void WriteHostData(LobbyInfo info)
    {
        Info = info;
#if !DISABLESTEAMWORKS
        if (CurrentLobbyId == 0 || !SteamBootstrap.IsInitialized) return;
        var lobby = new CSteamID(CurrentLobbyId);
        foreach (var kv in info.Encode())
        {
            try
            {
                if (!SteamMatchmaking.SetLobbyData(lobby, kv.Key, kv.Value))
                    Debug.LogWarning($"[SteamLobby] Steam refused lobby data '{kv.Key}'.");
            }
            catch (Exception e) { Debug.LogWarning($"[SteamLobby] SetLobbyData('{kv.Key}') threw: {e.Message}"); }
        }
#endif
    }

    /// <summary>Is NetSession hosting with a Relay code right now? (The rule <see cref="AutoHostLobby"/> follows.)</summary>
    static bool WantHostLobby(out string code)
    {
        code = LobbyInfo.NormalizeRelayCode(NetSession.JoinCode);
        return NetSession.State == NetSessionState.Hosting && LobbyInfo.IsValidRelayCode(code);
    }

    static void Tick()
    {
        if (!SteamBootstrap.IsInitialized) return;
        if (Creating && Time.realtimeSinceStartup - creatingSince > CreateTimeoutSeconds)
        {
            // Steam never answered CreateLobby (lost connection): count it as a failed attempt and let Tick retry.
            Creating = false;
            if (CurrentLobbyId == 0) Info = null;
            createAttempts++;
            nextCreateAt = Time.realtimeSinceStartup + CreateRetrySeconds;
            Debug.LogWarning($"[SteamLobby] Steam didn't answer CreateLobby; attempt {createAttempts}/{MaxCreateAttempts}.");
        }
        TickGuest();
        if (!AutoHostLobby) return;
        if (WantHostLobby(out string code))
        {
            if (CurrentLobbyId != 0 && IsHostLobby)
            {
                if (Info == null || Info.relay != code) WriteHostData(LobbyInfo.ForRelayHost(code, Info?.town, NetSession.SessionId, SteamBootstrap.LocalName));
                return;
            }
            if (Creating || createAttempts >= MaxCreateAttempts || Time.realtimeSinceStartup < nextCreateAt) return;
            if (CurrentLobbyId != 0 && !IsHostLobby) return; // the router is still tidying up a guest lobby
            OpenForHost(code, null, NetSession.SessionId);
            return;
        }
        var st = NetSession.State;
        if (IsHostLobby && CurrentLobbyId != 0 && st != NetSessionState.Hosting && st != NetSessionState.Preparing)
            Leave("hosting ended");
        if (st == NetSessionState.Idle) { createAttempts = 0; nextCreateAt = 0f; }
    }

    /// <summary>
    /// <c>NetSession.HostStarted</c> (B1's hook): a host with a Relay join code gets a lobby right away (the per-frame
    /// follow in Tick is the backstop, e.g. when the code arrives later).
    /// </summary>
    internal static void OnHostStarted(HostOptions o, string joinCode)
    {
        if (!AutoHostLobby || !SteamBootstrap.IsInitialized || o == null) return;
        string code = LobbyInfo.NormalizeRelayCode(joinCode);
        if (!LobbyInfo.IsValidRelayCode(code)) return; // solo / LocalDirect: nothing for friends to join
        createAttempts = 0;
        nextCreateAt = 0f;
        OpenForHost(code, o.townGuid, NetSession.SessionId);
    }

    /// <summary>
    /// Guest lobby housekeeping: show the crew lobby in our rich presence once connected (so our friends can join too), and
    /// leave a lobby we're no longer playing in (join failed, or the session ended) after a few seconds.
    /// </summary>
    static void TickGuest()
    {
        if (CurrentLobbyId == 0 || IsHostLobby) { guestIdleSince = -1f; guestPresenceSet = false; return; }
        var st = NetSession.State;
        if (st == NetSessionState.Connected && !guestPresenceSet)
        {
            guestPresenceSet = true;
            SteamPresence.SetLobby(CurrentLobbyId, MemberCount, false);
        }
        bool idle = st == NetSessionState.Idle || st == NetSessionState.Failed;
        if (!idle || SteamJoinRouter.Busy) { guestIdleSince = -1f; return; }
        if (guestIdleSince < 0f) guestIdleSince = Time.realtimeSinceStartup;
        else if (Time.realtimeSinceStartup - guestIdleSince > GuestIdleLeaveSeconds) Leave("not in that game any more");
    }

    /// <summary>
    /// <c>NetSession.ResolveTarget</c> (B1's hook): a join target that carries only a Steam lobby id is turned into a Relay
    /// target from the lobby's <c>rb_*</c> data (entering the lobby first, so the host sees us as a member). Targets that
    /// already have a Relay code (the router's) pass straight through. Null = go ahead; otherwise a readable error.
    /// </summary>
    internal static async Task<string> ResolveTargetAsync(JoinTarget t, CancellationToken ct)
    {
        if (t == null || t.steamLobbyId == 0) return null;
        if (t.mode == NetMode.UnityRelay && LobbyInfo.IsValidRelayCode(LobbyInfo.NormalizeRelayCode(t.joinCode))) return null;
        if (!SteamBootstrap.IsInitialized) return "Steam isn't running, so this invite can't be opened. Start Steam and try again.";

        var entered = new TaskCompletionSource<string>();
        Join(t.steamLobbyId, (ok, err) => entered.TrySetResult(ok ? null : (err ?? "Couldn't join the Steam lobby.")));
        Task first = await Task.WhenAny(entered.Task, Task.Delay(10000, ct));
        if (first != entered.Task) return ct.IsCancellationRequested ? "Cancelled." : "Steam didn't answer. Check your connection and try again.";
        string error = entered.Task.Result;
        if (error != null) return error;

        LobbyInfo info = null;
        string why = null;
        for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            if (TryReadCurrent(out info, out why)) break;
            info = null;
            await Task.Delay(100, ct).ContinueWith(_ => { });
        }
        if (info == null) { Leave("not a Rubbish lobby"); return "That isn't a Rubbish game you can join (" + why + ")."; }
        if (!info.TryGetJoinTarget(t.steamLobbyId, OwnerId, out JoinTarget r, out JoinReject reason, out string err))
        {
            Leave("can't join that lobby");
            return err ?? JoinRejectText.For(reason);
        }
        t.mode = r.mode;
        t.joinCode = r.joinCode;
        t.hostSteamId = r.hostSteamId;
        Debug.Log($"[SteamLobby] Resolved lobby {t.steamLobbyId} to {t.mode} (host steam:{t.hostSteamId}).");
        return null;
    }

    // ------------------------------------------------------------------ guest

    /// <summary>
    /// Enter <paramref name="lobbyId"/>. <paramref name="entered"/> gets (true, null) or (false, a player-readable reason).
    /// </summary>
    public static void Join(ulong lobbyId, Action<bool, string> entered)
    {
        if (entered == null) return;
        if (!SteamBootstrap.IsInitialized) { entered(false, "Steam isn't running."); return; }
        if (CurrentLobbyId == lobbyId && lobbyId != 0) { entered(true, null); return; }
        if (CurrentLobbyId != 0) Leave("joining another lobby");
#if !DISABLESTEAMWORKS
        try
        {
            joinCall.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)), (r, ioFailure) =>
            {
                const uint success = 1; // k_EChatRoomEnterResponseSuccess
                if (ioFailure || r.m_EChatRoomEnterResponse != success)
                {
                    entered(false, ioFailure ? "Steam didn't answer. Check your connection and try again." : EnterFailure(r.m_EChatRoomEnterResponse));
                    return;
                }
                CurrentLobbyId = r.m_ulSteamIDLobby;
                IsHostLobby = false;
                Info = null;
                RaiseChanged();
                entered(true, null);
            });
        }
        catch (Exception e) { entered(false, "Couldn't join the Steam lobby: " + e.Message); }
#else
        entered(false, "Steam isn't available on this platform.");
#endif
    }

    /// <summary>Read the current lobby's <c>rb_*</c> data (call after entering it).</summary>
    public static bool TryReadCurrent(out LobbyInfo info, out string error)
    {
        info = null;
        if (CurrentLobbyId == 0) { error = "not in a lobby"; return false; }
        ulong id = CurrentLobbyId;
        bool ok = LobbyInfo.TryDecode(k => GetData(id, k), out info, out error);
        if (ok) Info = info;
        return ok;
    }

    /// <summary>One lobby data value ("" when missing or Steam is down).</summary>
    public static string GetData(ulong lobbyId, string key)
    {
#if !DISABLESTEAMWORKS
        if (lobbyId == 0 || !SteamBootstrap.IsInitialized) return "";
        try { return SteamMatchmaking.GetLobbyData(new CSteamID(lobbyId), key) ?? ""; }
        catch (Exception) { return ""; }
#else
        return "";
#endif
    }

    /// <summary>Leave the current lobby (host or guest) and stop advertising it. Safe to call any time.</summary>
    public static void Leave(string reason)
    {
        if (CurrentLobbyId == 0 && !Creating) { Info = null; return; }
        ulong id = CurrentLobbyId;
        bool wasHost = IsHostLobby;
        CurrentLobbyId = 0;
        IsHostLobby = false;
        Info = null;
#if !DISABLESTEAMWORKS
        if (id != 0 && SteamBootstrap.IsInitialized)
        {
            try { SteamMatchmaking.LeaveLobby(new CSteamID(id)); }
            catch (Exception e) { Debug.LogWarning($"[SteamLobby] LeaveLobby threw: {e.Message}"); }
        }
#endif
        SteamPresence.Clear();
        if (wasHost) SteamAuthVerifier.Instance?.EndAll();
        if (id != 0)
        {
            Debug.Log($"[SteamLobby] Left lobby {id} ({reason}).");
            NetAudit.Event("steam", $"lobby left {id}");
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ invites

    /// <summary>
    /// Open Steam's "invite friends to this lobby" overlay. False with a player-readable <paramref name="whyNot"/> when it
    /// can't (no Steam, no lobby, overlay off: friends can still use Join Game from the friends list).
    /// </summary>
    public static bool OpenInviteDialog(out string whyNot)
    {
        whyNot = null;
        if (!SteamBootstrap.IsInitialized) { whyNot = "Steam isn't running, so there's nobody to invite. Share the join code instead."; return false; }
        if (CurrentLobbyId == 0) { whyNot = "Host a game with friends first, then invite them."; return false; }
#if !DISABLESTEAMWORKS
        try { SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(CurrentLobbyId)); }
        catch (Exception e) { whyNot = "Couldn't open the Steam overlay: " + e.Message; return false; }
#endif
        if (!SteamBootstrap.OverlayEnabled)
        {
            whyNot = "The Steam overlay isn't attached to the game, so the invite window may not show. Friends can right-click you in Steam and pick Join Game.";
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ callbacks

#if !DISABLESTEAMWORKS
    static void OnChatUpdate(LobbyChatUpdate_t u)
    {
        if (u.m_ulSteamIDLobby != CurrentLobbyId || CurrentLobbyId == 0) return;
        const uint entered = 0x0001;
        string who = SteamBootstrap.NameOf(u.m_ulSteamIDUserChanged);
        Debug.Log($"[SteamLobby] {(string.IsNullOrEmpty(who) ? u.m_ulSteamIDUserChanged.ToString() : who)} " +
                  $"{((u.m_rgfChatMemberStateChange & entered) != 0 ? "joined" : "left")} the lobby ({MemberCount} in it).");
        SteamPresence.SetGroupSize(MemberCount);
        RaiseChanged();
    }

    static void OnDataUpdate(LobbyDataUpdate_t u)
    {
        if (u.m_ulSteamIDLobby != CurrentLobbyId || CurrentLobbyId == 0 || IsHostLobby) return;
        if (u.m_ulSteamIDMember == u.m_ulSteamIDLobby && TryReadCurrent(out _, out _)) RaiseChanged();
    }

    static string EnterFailure(uint code) => code switch
    {
        2 => "That game has ended.",
        3 => "You can't join that game (it's friends only, or the host closed it).",
        4 => "That crew is full.",
        6 => "The host isn't letting you in.",
        7 => "Your Steam account is limited, so it can't join lobbies.",
        10 => "Someone in that lobby has blocked you.",
        11 => "You've blocked someone in that lobby.",
        15 => "Too many join attempts. Wait a moment and try again.",
        _ => $"Couldn't join the Steam lobby (code {code}).",
    };
#endif

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ self-test (-steamselftest)

    /// <summary>
    /// One-machine check on app 480 (the B10 EV): open a lobby, write and read back every <c>rb_*</c> key, decode it into a
    /// Relay join target, set rich presence and read the connect key back, then leave. Reports one line through
    /// <paramref name="done"/>. Doesn't touch the real lobby state; refuses while one is open.
    /// </summary>
    public static void RunSelfTest(Action<bool, string> done)
    {
        if (done == null) return;
        if (!SteamBootstrap.IsInitialized) { done(false, "Steam isn't up"); return; }
        if (CurrentLobbyId != 0 || Creating) { done(false, "a real lobby is open; run it from the menu"); return; }
#if !DISABLESTEAMWORKS
        var call = CallResult<LobbyCreated_t>.Create();
        try
        {
            call.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, NetConstants.TownCap), (r, io) =>
            {
                call.Dispose();
                if (io || r.m_eResult != EResult.k_EResultOK || r.m_ulSteamIDLobby == 0) { done(false, $"CreateLobby: {(io ? "I/O failure" : r.m_eResult.ToString())}"); return; }
                var lobby = new CSteamID(r.m_ulSteamIDLobby);
                var problems = new List<string>();
                try
                {
                    var want = LobbyInfo.ForRelayHost("SELFTEST", "Self Test Town", "selftest", SteamBootstrap.LocalName);
                    foreach (var kv in want.Encode())
                    {
                        if (!SteamMatchmaking.SetLobbyData(lobby, kv.Key, kv.Value)) problems.Add("set " + kv.Key);
                        else if (SteamMatchmaking.GetLobbyData(lobby, kv.Key) != kv.Value) problems.Add("read " + kv.Key);
                    }
                    if (!LobbyInfo.TryDecode(k => SteamMatchmaking.GetLobbyData(lobby, k), out var got, out string err)) problems.Add("decode: " + err);
                    else if (!got.TryGetJoinTarget(lobby.m_SteamID, SteamBootstrap.LocalSteamId, out var t, out _, out string terr)) problems.Add("target: " + terr);
                    else if (t.joinCode != "SELFTEST" || t.mode != NetMode.UnityRelay || t.steamLobbyId != lobby.m_SteamID) problems.Add("target fields");
                    if (SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID != SteamBootstrap.LocalSteamId) problems.Add("owner");

                    SteamPresence.SetLobby(lobby.m_SteamID, 1, true);
                    string connect = SteamFriends.GetFriendRichPresence(new CSteamID(SteamBootstrap.LocalSteamId), "connect");
                    if (!ConnectArgs.TryParse(connect, out ulong back) || back != lobby.m_SteamID) problems.Add("presence connect");
                }
                catch (Exception e) { problems.Add("threw " + e.GetType().Name); }
                finally
                {
                    try { SteamMatchmaking.LeaveLobby(lobby); } catch (Exception) { }
                    SteamPresence.Clear();
                }
                done(problems.Count == 0, problems.Count == 0
                    ? $"lobby {lobby.m_SteamID} created, {LobbyInfo.Keys.Length} rb_* keys read back, join target ok, presence set, left"
                    : string.Join(", ", problems));
            });
        }
        catch (Exception e) { call.Dispose(); done(false, "CreateLobby threw: " + e.Message); }
#else
        done(false, "Steamworks isn't compiled for this platform");
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        CurrentLobbyId = 0;
        IsHostLobby = false;
        Info = null;
        Creating = false;
        Changed = null;
        createAttempts = 0;
        nextCreateAt = 0f;
        started = false;
        AutoHostLobby = true;
        guestIdleSince = -1f;
        guestPresenceSet = false;
#if !DISABLESTEAMWORKS
        createCall = null;
        joinCall = null;
        chatUpdateCb = null;
        dataUpdateCb = null;
#endif
    }
}

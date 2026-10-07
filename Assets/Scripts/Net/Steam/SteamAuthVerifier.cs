#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// The host's identity check at the door (Docs/Accounts.md §7.6, Docs/Multiplayer.md §8.7.4 B10). Runs inside connection
/// approval (B1 holds the client <c>Pending</c> until <c>done</c> is called): the joiner's PlayerKey must be backed by a Steam
/// session proof bound to <b>this</b> host (<c>BeginAuthSession</c> → <c>ValidateAuthTicketResponse_t</c>, 5 s timeout →
/// <see cref="JoinReject.AuthTimeout"/>), and the SteamID must be in our lobby. <c>End</c> closes the auth session.
/// <para>Modes (<see cref="SteamPolicy.AuthModeFor"/>): release builds <b>enforce</b> (and refuse <c>dev:</c> keys);
/// development and playtest builds <b>warn</b>: everything is checked and logged as "[SteamAuth] would refuse …", but
/// friends still get in, so a Steam hiccup never blocks a playtest. <c>-steamauth enforce</c> tests the release rules.</para>
/// Registered with <see cref="SessionService.RegisterVerifier"/> only while Steam is up.
/// </summary>
public sealed class SteamAuthVerifier : IPlayerAuthVerifier
{
    public static SteamAuthVerifier Instance { get; private set; }

    /// <summary>Warn mode gives up waiting for Steam (and lets the player in) a little before B1's approval timeout.</summary>
    public const double WarnTimeoutSeconds = 3.5;

    public SteamAuthMode Mode { get; }
    readonly PendingAuthTable table;
#if !DISABLESTEAMWORKS
    Callback<ValidateAuthTicketResponse_t> responseCb;
#endif

    SteamAuthVerifier(SteamAuthMode mode)
    {
        Mode = mode;
        // Warn mode answers before the approval's own 5 s timeout, so a slow Steam never keeps a playtester out.
        table = new PendingAuthTable(mode == SteamAuthMode.Enforce ? SteamAuthRules.TimeoutSeconds : WarnTimeoutSeconds, EndSteamSession);
#if !DISABLESTEAMWORKS
        responseCb = Callback<ValidateAuthTicketResponse_t>.Create(OnResponse);
#endif
        SteamBootstrap.Ticked += Tick;
        SteamBootstrap.ShuttingDown += Dispose;
    }

    /// <summary>Install the verifier for this Steam session (called once Steam is up). Replaces any previous one.</summary>
    internal static void Install(SteamAuthMode mode)
    {
        Instance?.Dispose();
        Instance = new SteamAuthVerifier(mode);
        SessionService.RegisterVerifier(Instance);
        Debug.Log($"[SteamAuth] Verifying joiners with Steam: mode {mode}" +
                  (mode == SteamAuthMode.Warn ? " (log only; -steamauth enforce applies the release rules)." : "."));
    }

    public void BeginVerify(ulong clientId, in JoinRequest r, Action<bool, JoinReject> done)
    {
        if (done == null) return;
        int proofLen = r.authTicket?.Length ?? 0;
        string key = r.playerKey;

        if (Mode == SteamAuthMode.Off) { done(true, JoinReject.None); return; }
        if (!SteamBootstrap.IsInitialized)
        {
            Conclude(clientId, key, JoinReject.AuthFailed, "Steam isn't running on the host", done);
            return;
        }

        JoinReject pre = SteamAuthRules.Precheck(Mode, key, proofLen, SteamBootstrap.LocalSteamId, out ulong steamId);
        if (pre != JoinReject.None)
        {
            Conclude(clientId, key, pre, proofLen == 0 ? "no Steam proof in the join request" : "bad key", done);
            return;
        }
        if (steamId == 0)
        {
            // A dev key this mode lets in (development/playtest builds only; Enforce refused it above).
            Debug.Log($"[SteamAuth] Client {clientId} joined with dev key {key}: allowed in {Mode} mode.");
            done(true, JoinReject.None);
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        Action<bool, JoinReject> finished = (ok, why) => Finished(clientId, steamId, ok, why, done);
        if (!table.Begin(clientId, steamId, now, finished))
        {
            // Same account back while its old connection is still open (crashed and relaunched): B1 lets the newest connection
            // win, so check the new one. Its old auth session ends now; SessionApproval drops the old connection on approval.
            if (table.TryTakeOver(clientId, steamId, out ulong old))
            {
                Debug.Log($"[SteamAuth] steam:{steamId} is joining again as client {clientId} while client {old} is still " +
                          "connected: checking the new connection (the newest one wins).");
                EndSteamSession(steamId);
            }
            if (!table.Begin(clientId, steamId, now, finished))
            {
                Conclude(clientId, key, JoinReject.AuthFailed, "that Steam account is already joining", done);
                return;
            }
        }
#if !DISABLESTEAMWORKS
        int begin;
        try { begin = (int)SteamUser.BeginAuthSession(r.authTicket, proofLen, new CSteamID(steamId)); }
        catch (Exception e)
        {
            Debug.LogWarning($"[SteamAuth] BeginAuthSession threw for client {clientId}: {e.Message}");
            begin = SteamAuthRules.BeginInvalidTicket;
        }
        if (begin != SteamAuthRules.BeginOk)
        {
            Debug.Log($"[SteamAuth] Client {clientId} ({key}): Steam refused the proof at once ({SteamAuthRules.DescribeBegin(begin)}).");
            table.Answer(steamId, SteamAuthRules.FromBeginResult(begin), true, JoinReject.None, out _, Approve);
        }
        // else: wait for ValidateAuthTicketResponse_t (or the 5 s timeout in Tick).
#else
        table.Answer(steamId, JoinReject.AuthFailed, true, JoinReject.None, out _, Approve);
#endif
    }

    public void End(ulong clientId)
    {
        ulong steamId = table.End(clientId);
        if (steamId != 0) EndSteamSession(steamId);
    }

    void Tick()
    {
        if (table.PendingCount == 0) return;
        table.Tick(Time.realtimeSinceStartupAsDouble, LobbyOk, Approve);
    }

#if !DISABLESTEAMWORKS
    void OnResponse(ValidateAuthTicketResponse_t e)
    {
        ulong steamId = e.m_SteamID.m_SteamID;
        int code = (int)e.m_eAuthSessionResponse;
        JoinReject verdict = SteamAuthRules.FromResponse(code);
        if (e.m_OwnerSteamID.m_SteamID != 0 && e.m_OwnerSteamID.m_SteamID != steamId)
            Debug.Log($"[SteamAuth] {steamId} plays on a borrowed copy (owner {e.m_OwnerSteamID.m_SteamID}).");

        bool lobbyOk = LobbyOk(steamId);
        if (!lobbyOk && Mode != SteamAuthMode.Enforce && verdict == JoinReject.None)
        {
            // Warn mode doesn't wait for lobby membership (joining by a typed code is fine in playtests); just note it.
            Approve(JoinReject.NotLobbyMember);
            lobbyOk = true;
        }
        if (table.Answer(steamId, verdict, lobbyOk, JoinReject.NotLobbyMember, out ulong clientId, Approve))
        {
            Debug.Log($"[SteamAuth] Client {clientId} (steam:{steamId}): Steam says {SteamAuthRules.DescribeResponse(code)}" +
                      (verdict == JoinReject.None && !lobbyOk ? "; waiting for them to show up in the lobby." : "."));
            return;
        }
        // A later change for someone already in (they quit and Steam canceled the proof, or a ban landed mid-session).
        if (verdict == JoinReject.None) return;
        Debug.Log($"[SteamAuth] steam:{steamId} auth changed mid-session: {SteamAuthRules.DescribeResponse(code)}.");
        if (Mode == SteamAuthMode.Enforce && verdict == JoinReject.Banned && SessionService.TryGetClient(PlayerKey.Steam(steamId), out ulong c))
            SessionService.Kick(c, JoinRejectText.For(verdict));
    }
#endif

    /// <summary>The joiner must be in the host's Steam lobby (when the host has one). No lobby: only Enforce minds.</summary>
    bool LobbyOk(ulong steamId)
    {
        if (SteamLobby.IsHostLobby && SteamLobby.CurrentLobbyId != 0) return SteamLobby.IsMember(steamId);
        return Mode != SteamAuthMode.Enforce;
    }

    bool Approve(JoinReject verdict)
    {
        bool ok = SteamAuthRules.Approve(Mode, verdict);
        if (ok && verdict != JoinReject.None)
            Debug.LogWarning($"[SteamAuth] Would refuse a joiner in release ({verdict}: \"{JoinRejectText.For(verdict)}\"); " +
                             $"letting them in because this build only warns.");
        return ok;
    }

    void Finished(ulong clientId, ulong steamId, bool ok, JoinReject why, Action<bool, JoinReject> done)
    {
        Debug.Log(ok ? $"[SteamAuth] Client {clientId} (steam:{steamId}) approved."
                     : $"[SteamAuth] Client {clientId} (steam:{steamId}) refused: {why}.");
        NetAudit.Event("steam", ok ? $"auth ok {clientId}" : $"auth refused {clientId} {why}");
        done(ok, why);
    }

    /// <summary>A verdict reached without Steam (precheck): apply the mode and answer.</summary>
    void Conclude(ulong clientId, string key, JoinReject verdict, string detail, Action<bool, JoinReject> done)
    {
        bool ok = Approve(verdict);
        Debug.Log($"[SteamAuth] Client {clientId} ({(PlayerKey.TryParse(key, out var k) ? k.ToString() : "bad key")}): {detail} → " +
                  (ok ? "allowed" : "refused " + verdict) + ".");
        NetAudit.Event("steam", ok ? $"auth allowed {clientId} ({verdict})" : $"auth refused {clientId} {verdict}");
        done(ok, ok ? JoinReject.None : verdict);
    }

    static void EndSteamSession(ulong steamId)
    {
#if !DISABLESTEAMWORKS
        if (steamId == 0 || !SteamBootstrap.IsInitialized) return;
        try { SteamUser.EndAuthSession(new CSteamID(steamId)); }
        catch (Exception) { /* Steam is going away */ }
#endif
    }

    void Dispose()
    {
        foreach (ulong id in table.Clear()) EndSteamSession(id);
#if !DISABLESTEAMWORKS
        responseCb?.Dispose();
        responseCb = null;
#endif
        SteamBootstrap.Ticked -= Tick;
        SteamBootstrap.ShuttingDown -= Dispose;
        if (Instance == this)
        {
            Instance = null;
            if (SessionService.Verifier == this) SessionService.RegisterVerifier(null);
        }
    }

    /// <summary>The hosted session ended: close every auth session (the table forgets them without calling back).</summary>
    internal void EndAll()
    {
        foreach (ulong id in table.Clear()) EndSteamSession(id);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

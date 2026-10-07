using System;
using System.Collections.Generic;

/// <summary>
/// The door check's decisions (Accounts.md §7.6, Docs/Multiplayer.md §8.7.4 B10), without Steamworks: the Steam verifier feeds
/// in the raw <c>EBeginAuthSessionResult</c> / <c>EAuthSessionResponse</c> values as ints. Pure so the rules are unit-tested.
/// </summary>
public static class SteamAuthRules
{
    /// <summary>Approval waits at most this long for Steam's answer, then refuses with <see cref="JoinReject.AuthTimeout"/>.</summary>
    public const double TimeoutSeconds = 5.0;

    // EBeginAuthSessionResult
    public const int BeginOk = 0, BeginInvalidTicket = 1, BeginDuplicateRequest = 2, BeginInvalidVersion = 3,
        BeginGameMismatch = 4, BeginExpiredTicket = 5;

    // EAuthSessionResponse
    public const int ResponseOk = 0, ResponseVacBanned = 3, ResponseAuthTicketCanceled = 6, ResponsePublisherBan = 9,
        ResponseNetworkIdentityFailure = 10;

    /// <summary>
    /// Before asking Steam: what the join request alone decides. <see cref="JoinReject.None"/> with <paramref name="steamId"/> set
    /// means "verify with Steam"; None with steamId 0 means a dev key that this mode lets in without Steam.
    /// </summary>
    public static JoinReject Precheck(SteamAuthMode mode, string playerKey, int proofLength, ulong hostSteamId, out ulong steamId)
    {
        steamId = 0;
        if (!PlayerKey.TryParse(playerKey, out PlayerKey k)) return JoinReject.BadRequest;
        if (k.IsDev) return mode == SteamAuthMode.Enforce ? JoinReject.DevKeyNotAllowed : JoinReject.None;
        steamId = k.SteamId64;
        if (steamId == 0) return JoinReject.BadRequest;
        // Someone claiming to be the host is never the host: its own connection is approved before any verifier runs.
        if (hostSteamId != 0 && steamId == hostSteamId) return JoinReject.AuthFailed;
        if (proofLength <= 0) return JoinReject.AuthFailed;
        return JoinReject.None;
    }

    /// <summary><c>SteamUser.BeginAuthSession</c>'s immediate result.</summary>
    public static JoinReject FromBeginResult(int code) => code == BeginOk ? JoinReject.None : JoinReject.AuthFailed;

    /// <summary>
    /// <c>ValidateAuthTicketResponse_t</c>. A proof bound to another host's identity comes back as
    /// <see cref="ResponseNetworkIdentityFailure"/> and is refused like any other failure; bans show as Banned.
    /// </summary>
    public static JoinReject FromResponse(int code) => code switch
    {
        ResponseOk => JoinReject.None,
        ResponseVacBanned => JoinReject.Banned,
        ResponsePublisherBan => JoinReject.Banned,
        _ => JoinReject.AuthFailed,
    };

    /// <summary>
    /// Apply the mode to a verdict: Enforce keeps it; Warn and Off let the player in (the caller logs what Enforce would have
    /// done). Returns whether to approve.
    /// </summary>
    public static bool Approve(SteamAuthMode mode, JoinReject verdict) => verdict == JoinReject.None || mode != SteamAuthMode.Enforce;

    /// <summary>Short log words for a response code (never the proof itself).</summary>
    public static string DescribeResponse(int code) => code switch
    {
        0 => "ok",
        1 => "user not connected to Steam",
        2 => "no license or expired",
        3 => "VAC banned",
        4 => "logged in elsewhere",
        5 => "VAC check timed out",
        6 => "canceled by the player",
        7 => "already used",
        8 => "invalid",
        9 => "publisher ban",
        10 => "bound to another host",
        _ => "code " + code,
    };

    public static string DescribeBegin(int code) => code switch
    {
        0 => "ok",
        1 => "invalid",
        2 => "duplicate request",
        3 => "incompatible version",
        4 => "for another game",
        5 => "expired",
        _ => "code " + code,
    };
}

/// <summary>
/// Joins waiting on Steam, with deadlines on a caller-supplied clock (so tests can use a fake one). One pending entry per
/// client; a SteamID can only be pending or active for one client at a time. Callbacks run exactly once.
/// </summary>
public sealed class PendingAuthTable
{
    sealed class Entry
    {
        public ulong clientId, steamId;
        public double deadline;
        public Action<bool, JoinReject> done;
        /// <summary>Steam said OK; waiting for the extra condition (lobby membership) until the deadline.</summary>
        public bool steamOk;
        public JoinReject timeoutReason = JoinReject.AuthTimeout;
    }

    readonly double timeout;
    readonly Action<ulong> refusedSession;
    readonly Dictionary<ulong, Entry> pending = new Dictionary<ulong, Entry>();   // by clientId
    readonly Dictionary<ulong, ulong> active = new Dictionary<ulong, ulong>();    // clientId → steamId (auth session open)

    /// <param name="refusedSession">Called with the SteamID when a join is refused, so the caller ends that auth session at
    /// once (otherwise a retry would be a "duplicate request").</param>
    public PendingAuthTable(double timeoutSeconds = SteamAuthRules.TimeoutSeconds, Action<ulong> refusedSession = null)
    {
        timeout = timeoutSeconds > 0 ? timeoutSeconds : SteamAuthRules.TimeoutSeconds;
        this.refusedSession = refusedSession;
    }

    public int PendingCount => pending.Count;
    public int ActiveCount => active.Count;

    /// <summary>
    /// Start waiting for <paramref name="steamId"/> on behalf of <paramref name="clientId"/>. False (and no callback) when that
    /// client is already pending or the SteamID is already in use by another client.
    /// </summary>
    public bool Begin(ulong clientId, ulong steamId, double now, Action<bool, JoinReject> done)
    {
        if (done == null || steamId == 0 || pending.ContainsKey(clientId)) return false;
        if (InUseByOther(clientId, steamId)) return false;
        pending[clientId] = new Entry { clientId = clientId, steamId = steamId, deadline = now + timeout, done = done };
        active[clientId] = steamId;
        return true;
    }

    /// <summary>True when another client is pending on, or holds an open session for, <paramref name="steamId"/>.</summary>
    public bool InUseByOther(ulong clientId, ulong steamId)
    {
        foreach (var kv in active)
            if (kv.Value == steamId && kv.Key != clientId) return true;
        return false;
    }

    /// <summary>
    /// B1's "newest connection wins": <paramref name="steamId"/> is joining again on <paramref name="newClientId"/> while an
    /// older, already-approved connection still holds its auth session (a crash and relaunch before the old one timed out).
    /// Forgets the old client's entry without a callback and returns it, so the caller ends that Steam auth session and can
    /// <see cref="Begin"/> for the new connection (the session layer disconnects the old one once the new one is approved).
    /// False when nobody else holds it, or when the holder is itself still waiting at the door (two joins at once: the second
    /// is refused).
    /// </summary>
    public bool TryTakeOver(ulong newClientId, ulong steamId, out ulong oldClientId)
    {
        oldClientId = 0;
        bool found = false;
        foreach (var kv in active)
            if (kv.Value == steamId && kv.Key != newClientId) { oldClientId = kv.Key; found = true; break; }
        if (!found || pending.ContainsKey(oldClientId) || pending.ContainsKey(newClientId)) { oldClientId = 0; return false; }
        active.Remove(oldClientId);
        return true;
    }

    public bool TryGetSteamId(ulong clientId, out ulong steamId) => active.TryGetValue(clientId, out steamId);

    public bool IsPending(ulong clientId) => pending.ContainsKey(clientId);

    /// <summary>
    /// Steam answered for <paramref name="steamId"/>. On a failure the entry finishes now. On success it finishes now when
    /// <paramref name="extraOk"/> is true; otherwise it keeps waiting (see <see cref="Tick"/>) and, if the deadline passes,
    /// finishes with <paramref name="extraFailReason"/>. Returns the client it applied to, or false if nobody was waiting.
    /// </summary>
    public bool Answer(ulong steamId, JoinReject verdict, bool extraOk, JoinReject extraFailReason, out ulong clientId,
                       Func<JoinReject, bool> approve)
    {
        clientId = 0;
        Entry e = null;
        foreach (var p in pending.Values)
            if (p.steamId == steamId && !p.steamOk) { e = p; break; }
        if (e == null) return false;
        clientId = e.clientId;
        if (verdict != JoinReject.None) { Finish(e, verdict, approve); return true; }
        if (extraOk) { Finish(e, JoinReject.None, approve); return true; }
        e.steamOk = true;
        e.timeoutReason = extraFailReason;
        return true;
    }

    /// <summary>
    /// Expire overdue entries (each finishes with its timeout reason) and finish Steam-approved entries whose extra condition
    /// now holds. Returns how many finished.
    /// </summary>
    public int Tick(double now, Func<ulong, bool> extraOk, Func<JoinReject, bool> approve)
    {
        if (pending.Count == 0) return 0;
        List<Entry> finished = null;
        foreach (var e in pending.Values)
        {
            if (e.steamOk && extraOk != null && extraOk(e.steamId)) (finished ??= new List<Entry>()).Add(e);
            else if (now >= e.deadline) (finished ??= new List<Entry>()).Add(e);
        }
        if (finished == null) return 0;
        foreach (var e in finished)
        {
            bool ok = e.steamOk && extraOk != null && extraOk(e.steamId);
            Finish(e, ok ? JoinReject.None : e.timeoutReason, approve);
        }
        return finished.Count;
    }

    /// <summary>
    /// The client left or approval was abandoned: forget it without calling back. Returns the SteamID whose auth session must
    /// be ended (0 if none).
    /// </summary>
    public ulong End(ulong clientId)
    {
        pending.Remove(clientId);
        if (!active.TryGetValue(clientId, out ulong steamId)) return 0;
        active.Remove(clientId);
        return steamId;
    }

    /// <summary>Every open session's SteamID (to end them all on shutdown); clears the table without callbacks.</summary>
    public List<ulong> Clear()
    {
        var ids = new List<ulong>(active.Values);
        pending.Clear();
        active.Clear();
        return ids;
    }

    void Finish(Entry e, JoinReject verdict, Func<JoinReject, bool> approve)
    {
        pending.Remove(e.clientId);
        bool ok = approve != null ? approve(verdict) : verdict == JoinReject.None;
        if (!ok)
        {
            active.Remove(e.clientId);
            try { refusedSession?.Invoke(e.steamId); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }
        try { e.done(ok, ok ? JoinReject.None : verdict); }
        catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
    }
}

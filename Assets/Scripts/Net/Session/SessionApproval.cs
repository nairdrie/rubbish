using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Connection approval on the host (Docs/Multiplayer.md §2.4, package B1). <see cref="NetBoot"/> installs
/// <see cref="OnApproval"/> on every machine; only the host ever runs it.
/// <list type="number">
/// <item>The host's own connection is approved at once (NGO can't decline it), so raw <c>StartHost()</c> calls such as
/// Progression's <c>host.cs</c>/<c>host2.cs</c> keep working, and the host is seated in slot 0.</item>
/// <item>Solo games refuse everyone else (<see cref="JoinReject.SoloOnly"/>).</item>
/// <item>The payload goes through a <see cref="NetGuard"/> (≤1,200 B, a few joins a second for the whole Town) and
/// <see cref="JoinRequest.TryDecode"/> (bad bytes → <see cref="JoinReject.BadRequest"/>, other protocol →
/// <see cref="JoinReject.ProtocolMismatch"/> "Update your game").</item>
/// <item>The PlayerKey must parse; <c>dev:</c> keys are refused when <see cref="SessionService.RejectDevKeys"/> is on (B10
/// turns it on for release builds with Steam auth).</item>
/// <item>Duplicates: a key already held by another guest means the same player is back (crash and relaunch, a second PC):
/// the newer connection wins and gets the same slot. A second window on the host's own PC with the host's dev key gets
/// <c>dev:name-2</c>.</item>
/// <item>The Town cap (4: every player brings their own business, §13.10) is checked, then, if a verifier is registered
/// (B10's Steam auth), the response stays <c>Pending</c> until it answers or 5 s pass (<see cref="JoinReject.AuthTimeout"/>).
/// There is no "crew is full" any more: a refusal for lack of room always reads "Town is full (4/4)".</item>
/// <item>The player is seated by PlayerKey (slot memory) and bound to their client id; <see cref="NetRoster"/> follows.</item>
/// </list>
/// Refusals travel as <see cref="SessionEndReason"/> so the joiner sees a sentence, never a code.
/// </summary>
public static class SessionApproval
{
    /// <summary>How long a verifier (Steam BeginAuthSession) may take before the join is refused.</summary>
    public const double AuthTimeoutSeconds = 5.0;
    /// <summary>
    /// The <see cref="SlotTable"/> company every player sits in. Since §13 each player has their own business and the slot is
    /// its colour (0..3, <see cref="BusinessColors"/>); the business itself lives in <see cref="Businesses"/>.
    /// </summary>
    public const int CrewCompany = 0;
    const ulong NoClient = ulong.MaxValue;

    struct PendingJoin
    {
        public NetworkManager.ConnectionApprovalResponse response;
        public JoinRequest request;
        public PlayerKey key;
        public ulong replaces;
        public double deadline;
    }

    // One bucket for the whole Town (every connection is a new client id): 4 joins/s, burst 8, ≤ JoinRequestMaxBytes.
    static NetGuard joinGuard = NewJoinGuard();
    static readonly Dictionary<ulong, PendingJoin> pending = new Dictionary<ulong, PendingJoin>();

    static NetGuard NewJoinGuard() => new NetGuard(4f, 8, NetConstants.JoinRequestMaxBytes);

    /// <summary>Joins waiting on the verifier.</summary>
    public static int PendingCount => pending.Count;

    /// <summary>NGO's approval callback (installed by NetBoot). Never throws.</summary>
    internal static void OnApproval(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
    {
        var nm = NetBoot.Manager;
        ulong id = req.ClientNetworkId;
        res.CreatePlayerObject = nm != null && nm.NetworkConfig.PlayerPrefab != null; // PlayerSpawner spawns players itself
        try
        {
            if (id == NetworkManager.ServerClientId) { ApproveHost(res); return; }
            Decide(nm, id, req.Payload, res);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            pending.Remove(id);
            Reject(res, id, JoinReject.BadRequest, "approval threw");
        }
    }

    static void ApproveHost(NetworkManager.ConnectionApprovalResponse res)
    {
        NetSession.EnsureHostSession();
        var key = NetSession.LocalPlayerKey;
        if (SessionService.Slots.TryAssign(key, CrewCompany, out int slot, out var why))
            SessionService.Bind(key, NetworkManager.ServerClientId, slot);
        else
            Debug.LogWarning($"[Session] Couldn't seat the host ({key}): {why}.");
        res.Approved = true;
        res.Pending = false;
    }

    static void Decide(NetworkManager nm, ulong id, byte[] payload, NetworkManager.ConnectionApprovalResponse res)
    {
        if (SessionService.SoloOnly) { Reject(res, id, JoinReject.SoloOnly, "solo game"); return; }
        int bytes = payload?.Length ?? 0;
        if (!joinGuard.Allow(0UL, bytes, Time.realtimeSinceStartupAsDouble))
        {
            Reject(res, id, JoinReject.BadRequest, bytes > NetConstants.JoinRequestMaxBytes ? $"payload {bytes} B" : "join flood");
            return;
        }
        if (!JoinRequest.TryDecode(payload, out var r, out var why))
        {
            Reject(res, id, why, why == JoinReject.ProtocolMismatch ? $"their protocol {r.proto}, ours {NetVersion.Protocol}" : "undecodable");
            return;
        }
        if (!PlayerKey.TryParse(r.playerKey, out var key)) { Reject(res, id, JoinReject.BadRequest, "bad player key"); return; }
        if (key.IsDev && SessionService.RejectDevKeys) { Reject(res, id, JoinReject.DevKeyNotAllowed, key.ToString()); return; }
        if (SessionService.IsBanned(key)) { Reject(res, id, JoinReject.Banned, key.ToString()); return; }

        // Duplicates.
        ulong replaces = NoClient;
        if (SessionService.TryGetClient(key, out ulong holder) && holder != id)
        {
            if (holder == NetworkManager.ServerClientId)
            {
                if (!SessionKeys.TryDisambiguate(key, k => SessionService.TryGetClient(k, out _), out var alt))
                {
                    RejectText(res, id, "SameAccount", "You can't join a game hosted by your own account.", key.ToString());
                    return;
                }
                key = alt;
            }
            else replaces = holder;
        }

        if (!CanSeat(key, replaces, out why)) { Reject(res, id, why, key.ToString()); return; }

        var verifier = SessionService.Verifier;
        if (verifier != null)
        {
            res.Pending = true;
            pending[id] = new PendingJoin { response = res, request = r, key = key, replaces = replaces, deadline = Time.realtimeSinceStartupAsDouble + AuthTimeoutSeconds };
            try
            {
                verifier.BeginVerify(id, r, (ok, reason) => Verified(id, ok, reason));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (pending.Remove(id)) Reject(res, id, JoinReject.AuthFailed, "verifier threw");
            }
            return;
        }
        Seat(nm, id, key, replaces, res);
    }

    /// <summary>Cap check before spending a Steam auth round trip (re-checked when seating).</summary>
    static bool CanSeat(PlayerKey key, ulong replaces, out JoinReject why)
    {
        why = JoinReject.None;
        if (replaces != NoClient) return true; // takes over their own seat
        var slots = SessionService.Slots;
        if (slots.TryGet(key, out _, out _)) return true;
        if (slots.Count >= slots.TownCap || slots.CountIn(CrewCompany) >= slots.CompanyCap) { why = JoinReject.TownFull; return false; }
        return true;
    }

    /// <summary>Every player sits in one SlotTable company, so its "company full" means the Town is full (§13.10).</summary>
    static JoinReject ForJoin(JoinReject why) => why == JoinReject.CompanyFull ? JoinReject.TownFull : why;

    static void Verified(ulong id, bool ok, JoinReject reason)
    {
        if (!pending.TryGetValue(id, out var p)) return; // timed out or gone already
        pending.Remove(id);
        if (!ok) { Reject(p.response, id, reason == JoinReject.None ? JoinReject.AuthFailed : reason, "verifier said no"); return; }
        var nm = NetBoot.Manager;
        if (nm != null && !nm.IsListening) { Reject(p.response, id, JoinReject.HostLeaving, "host stopped"); return; }
        // Someone may have taken the key meanwhile (a faster duplicate): resolve again.
        ulong replaces = p.replaces;
        if (SessionService.TryGetClient(p.key, out ulong holder) && holder != id && holder != NetworkManager.ServerClientId) replaces = holder;
        Seat(nm, id, p.key, replaces, p.response);
    }

    static void Seat(NetworkManager nm, ulong id, PlayerKey key, ulong replaces, NetworkManager.ConnectionApprovalResponse res)
    {
        if (replaces != NoClient && replaces != id)
        {
            SessionService.Unbind(replaces); // frees (and remembers) the seat for the newer connection
            SessionService.Verifier?.End(replaces);
            try
            {
                if (nm != null && nm.ConnectedClients.ContainsKey(replaces))
                    nm.DisconnectClient(replaces, SessionEndReason.Encode(SessionEndReason.Replaced, "You joined this game again from somewhere else."));
            }
            catch (Exception e) { Debug.LogException(e); }
            NetAudit.Event("roster", $"replace {key}: client {replaces} -> {id}");
        }
        if (!SessionService.Slots.TryAssign(key, CrewCompany, out int slot, out var why)) { Reject(res, id, ForJoin(why), key.ToString()); return; }
        SessionService.Bind(key, id, slot);
        res.Approved = true;
        res.Reason = null;
        res.Pending = false; // last: NGO polls Pending
        Debug.Log($"[Session] Approved {key} as client {id}, slot {slot} ({SessionService.Slots.Count}/{SessionService.Slots.TownCap}).");
        NetAudit.Event("roster", $"approve {key} client {id} slot {slot}");
    }

    static void Reject(NetworkManager.ConnectionApprovalResponse res, ulong id, JoinReject why, string detail)
    {
        if (why == JoinReject.None) why = JoinReject.BadRequest;
        RejectText(res, id, why.ToString(), JoinRejectText.For(why), detail);
    }

    static void RejectText(NetworkManager.ConnectionApprovalResponse res, ulong id, string code, string text, string detail)
    {
        res.Approved = false;
        res.CreatePlayerObject = false;
        res.Reason = SessionEndReason.Encode(code, text);
        res.Pending = false;
        Debug.Log($"[Session] Refused client {id}: {code} ({detail}). They see: \"{text}\"");
        NetAudit.Event("reject", $"client {id}: {code} ({detail})");
    }

    /// <summary>Per frame from NetBoot's runner: verifier timeouts.</summary>
    internal static void Tick(double nowSeconds)
    {
        if (pending.Count == 0) return;
        List<ulong> late = null;
        foreach (var kv in pending)
            if (nowSeconds >= kv.Value.deadline) (late ??= new List<ulong>()).Add(kv.Key);
        if (late == null) return;
        foreach (ulong id in late)
        {
            var p = pending[id];
            pending.Remove(id);
            SessionService.Verifier?.End(id);
            Reject(p.response, id, JoinReject.AuthTimeout, $"no answer in {AuthTimeoutSeconds:0} s");
        }
    }

    /// <summary>A client went away (approved or not): drop any pending verification and its auth session.</summary>
    internal static void ClientGone(ulong id)
    {
        pending.Remove(id);
        SessionService.Verifier?.End(id);
    }

    /// <summary>A new hosted session: forget pending joins and refill the join guard.</summary>
    internal static void Reset()
    {
        pending.Clear();
        joinGuard = NewJoinGuard();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        pending.Clear();
        joinGuard = NewJoinGuard();
    }
}

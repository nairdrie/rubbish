using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Host-side session bookkeeping (package B1): who is who (clientId ⇄ PlayerKey), seats (<see cref="Slots"/>), kicks, the
/// auth verifier and per-message <see cref="NetGuard"/>s. <see cref="SessionApproval"/> seats and binds players;
/// <see cref="NetSession"/> begins and ends sessions; <see cref="NetRoster"/> mirrors the seats to everyone.
/// </summary>
public static class SessionService
{
    // Default limit for a named message nobody configured: 10/s, burst 20, 16 KB (the company snapshot cap, D4).
    const float DefaultPerSecond = 10f;
    const int DefaultBurst = 20, DefaultMaxBytes = 16 * 1024;

    static readonly Dictionary<ulong, PlayerKey> byClient = new Dictionary<ulong, PlayerKey>();
    static readonly Dictionary<PlayerKey, ulong> byKey = new Dictionary<PlayerKey, ulong>();
    static readonly Dictionary<string, NetGuard> guards = new Dictionary<string, NetGuard>(StringComparer.Ordinal);
    static readonly HashSet<PlayerKey> banned = new HashSet<PlayerKey>();

    public static SlotTable Slots { get; private set; } = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);

    /// <summary>A hosted session is running (or starting) on this machine.</summary>
    public static bool Active { get; private set; }

    /// <summary>Solo: approval refuses every remote client (<see cref="JoinReject.SoloOnly"/>).</summary>
    public static bool SoloOnly { get; private set; }

    /// <summary>Refuse <c>dev:</c> keys at the door (B10 turns this on for release builds with Steam auth).</summary>
    public static bool RejectDevKeys;

    /// <summary>The host's verifier for join tickets (B10 registers the Steam one); null = dev keys only.</summary>
    public static IPlayerAuthVerifier Verifier { get; private set; }

    /// <summary>
    /// Display name for a roster entry (B10 sets a Steam persona lookup). Default: the dev name, or "Player" for a Steam
    /// key. Never taken from the join request.
    /// </summary>
    public static Func<PlayerKey, string> DisplayNameFor = DefaultDisplayName;

    /// <summary>(key, clientId, slot) after approval and seating.</summary>
    public static event Action<PlayerKey, ulong, int> PlayerJoined;
    public static event Action<PlayerKey, ulong> PlayerLeft;

    /// <summary>Players seated right now.</summary>
    public static int PlayerCount => byClient.Count;

    public static bool TryGetPlayer(ulong clientId, out PlayerKey k) => byClient.TryGetValue(clientId, out k);

    public static bool TryGetClient(PlayerKey k, out ulong clientId) => byKey.TryGetValue(k, out clientId);

    /// <summary>The seat (slot within their company) of a connected client; false when they aren't seated.</summary>
    public static bool TryGetSlot(ulong clientId, out int slot)
    {
        slot = -1;
        return byClient.TryGetValue(clientId, out var k) && Slots.TryGet(k, out _, out slot);
    }

    /// <summary>Every seated player: (key, clientId, company, slot), in no particular order.</summary>
    public static List<(PlayerKey key, ulong clientId, int company, int slot)> Seated()
    {
        var list = new List<(PlayerKey, ulong, int, int)>(byClient.Count);
        foreach (var kv in byClient)
            if (Slots.TryGet(kv.Value, out int company, out int slot)) list.Add((kv.Value, kv.Key, company, slot));
        return list;
    }

    /// <summary>Disconnect a client with a readable reason (host only; set up when a session starts).</summary>
    public static void Kick(ulong clientId, string reason)
    {
        NetAudit.Event("kick", $"{clientId}: {reason}");
        if (KickHandler != null) KickHandler(clientId, reason);
        else Debug.LogWarning($"[SessionService] Kick({clientId}, \"{reason}\") ignored: not hosting.");
    }

    /// <summary>Kick and refuse this PlayerKey for the rest of the session.</summary>
    public static void Ban(PlayerKey k, string reason)
    {
        if (!k.IsValid) return;
        banned.Add(k);
        Slots.Forget(k);
        if (byKey.TryGetValue(k, out ulong id)) Kick(id, reason);
    }

    public static bool IsBanned(PlayerKey k) => banned.Contains(k);

    /// <summary>Set by NetSession while hosting: performs the actual disconnect.</summary>
    public static Action<ulong, string> KickHandler;

    public static void RegisterVerifier(IPlayerAuthVerifier v) => Verifier = v;

    /// <summary>The guard for one named message or RPC; created with defaults the first time it's asked for.</summary>
    public static NetGuard GuardFor(string messageName)
    {
        if (string.IsNullOrEmpty(messageName)) throw new ArgumentException("message name is empty", nameof(messageName));
        if (!guards.TryGetValue(messageName, out var g))
        {
            g = new NetGuard(DefaultPerSecond, DefaultBurst, DefaultMaxBytes);
            guards[messageName] = g;
        }
        return g;
    }

    /// <summary>Configure a message's limits (B7's MessageGuards).</summary>
    public static void SetGuard(string messageName, NetGuard guard)
    {
        if (string.IsNullOrEmpty(messageName) || guard == null) throw new ArgumentException("bad guard registration");
        guards[messageName] = guard;
    }

    // ---- NetSession / SessionApproval call these ----

    /// <summary>A fresh seat table for a new hosted session.</summary>
    public static void BeginSession(int townCap, int companyCap) => BeginSession(townCap, companyCap, false);

    /// <summary>A fresh seat table for a new hosted session; <paramref name="soloOnly"/> refuses every remote client.</summary>
    public static void BeginSession(int townCap, int companyCap, bool soloOnly)
    {
        byClient.Clear();
        byKey.Clear();
        banned.Clear();
        Slots = new SlotTable(Mathf.Clamp(townCap, 1, NetConstants.TownCap), Mathf.Clamp(companyCap, 1, NetConstants.CompanyCap));
        SoloOnly = soloOnly;
        Active = true;
        SessionApproval.Reset();
    }

    /// <summary>The hosted session is over: nobody is seated, kicks go nowhere.</summary>
    public static void EndSession()
    {
        if (!Active && byClient.Count == 0) return;
        var left = new List<KeyValuePair<ulong, PlayerKey>>(byClient);
        foreach (var kv in left) Unbind(kv.Key);
        Active = false;
        SoloOnly = false;
        KickHandler = null;
        SessionApproval.Reset();
    }

    public static void Bind(PlayerKey k, ulong clientId, int slot)
    {
        if (byKey.TryGetValue(k, out ulong old) && old != clientId) byClient.Remove(old);
        if (byClient.TryGetValue(clientId, out var prevKey) && prevKey != k) byKey.Remove(prevKey);
        byClient[clientId] = k;
        byKey[k] = clientId;
        try { PlayerJoined?.Invoke(k, clientId, slot); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public static void Unbind(ulong clientId)
    {
        if (!byClient.TryGetValue(clientId, out var k)) return;
        byClient.Remove(clientId);
        if (byKey.TryGetValue(k, out ulong c) && c == clientId) byKey.Remove(k);
        foreach (var g in guards.Values) g.Forget(clientId);
        Slots.Release(k);
        try { PlayerLeft?.Invoke(k, clientId); }
        catch (Exception e) { Debug.LogException(e); }
    }

    static string DefaultDisplayName(PlayerKey k)
    {
        if (k.IsDev) return k.DevName;
        return k.IsSteam ? "Player" : "?";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        byClient.Clear();
        byKey.Clear();
        guards.Clear();
        banned.Clear();
        Slots = new SlotTable(NetConstants.TownCap, NetConstants.CompanyCap);
        Active = false;
        SoloOnly = false;
        RejectDevKeys = false;
        Verifier = null;
        DisplayNameFor = DefaultDisplayName;
        KickHandler = null;
        PlayerJoined = null;
        PlayerLeft = null;
    }
}

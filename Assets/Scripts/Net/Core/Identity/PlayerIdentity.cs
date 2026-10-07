using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The local player's identity (Docs/Accounts.md §1.1). Two implementations, registered by B3's boot code:
/// <c>DevIdentity</c> (<c>dev:&lt;name&gt;</c>) and <c>SteamIdentity</c> (<c>steam:&lt;id64&gt;</c>).
/// </summary>
public interface IPlayerIdentity
{
    PlayerKey Key { get; }
    /// <summary>Live from Steam (or the dev name). Never saved, never taken from a join request.</summary>
    string DisplayName { get; }
    bool IsSteam { get; }
    /// <summary>A session ticket bound to the host's identity (empty for dev identities). Never log it.</summary>
    byte[] GetJoinTicket(ulong hostSteamId);
    void CancelJoinTicket();
}

/// <summary>Host-side check of a joiner's ticket inside connection approval (B10: Steam <c>BeginAuthSession</c>).</summary>
public interface IPlayerAuthVerifier
{
    /// <summary>Start verifying; call <paramref name="done"/> exactly once (approved, reason).</summary>
    void BeginVerify(ulong clientId, in JoinRequest r, Action<bool, JoinReject> done);
    /// <summary>The client left or approval ended: release any auth session.</summary>
    void End(ulong clientId);
}

/// <summary>
/// Registry for the local identity. Providers register with a priority; the highest wins, resolved once on first read of
/// <see cref="Local"/> and then fixed for the process (identity never flips mid-session, Accounts.md §1.2).
/// </summary>
public static class PlayerIdentity
{
    struct Entry { public Func<IPlayerIdentity> provider; public int priority; public int order; }

    static readonly List<Entry> providers = new List<Entry>();
    static IPlayerIdentity local;
    static bool resolved;
    static int registrations;

    /// <summary>The local identity, or null if no provider is registered (or the winning provider failed).</summary>
    public static IPlayerIdentity Local
    {
        get
        {
            if (!resolved) Resolve();
            return local;
        }
    }

    public static void Register(Func<IPlayerIdentity> provider, int priority)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (resolved)
        {
            Debug.LogWarning($"[PlayerIdentity] A provider (priority {priority}) registered after the identity was fixed as " +
                             $"'{local?.Key}'. Ignored: identity is fixed per process.");
            return;
        }
        providers.Add(new Entry { provider = provider, priority = priority, order = registrations++ });
    }

    /// <summary>Forget the providers and the resolved identity (B3's boot on a fresh play session; tests).</summary>
    public static void Reset()
    {
        providers.Clear();
        local = null;
        resolved = false;
        registrations = 0;
    }

    static void Resolve()
    {
        resolved = true;
        local = null;
        if (providers.Count == 0) return;
        Entry best = providers[0];
        foreach (var e in providers)
            if (e.priority > best.priority || (e.priority == best.priority && e.order > best.order)) best = e;
        try { local = best.provider(); }
        catch (Exception e) { Debug.LogError($"[PlayerIdentity] The identity provider failed: {e.Message}"); local = null; }
    }
}

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The "these statics aren't mine" latch (Docs/Accounts.md §6.1, hotfix L0, red-team R2). <c>GameSession</c> and
/// <c>EbaeMarket</c> are static and hold whatever the game shows; on a guest that is (or will be) the host's world. Once
/// <see cref="MarkForeign"/> runs, every save path is refused (both <c>Save()</c>s, <c>SaveSoon</c>, pause, quit, scene change,
/// both <c>ResetAll(true)</c>) until <see cref="ReloadOwn"/> puts the player's own data back. So a host crash, a quit while
/// connected, or "leave, then Play solo" can never write a friend's world into the player's files.
/// <para>Latched by: the Join click (before any scene load), NGO's <c>OnClientStarted</c> on a non-host, and
/// <c>GameSession.IsGuest</c> as a backstop. Cleared only by <see cref="ReloadOwn"/>: leave, join failure,
/// <c>OnClientStopped</c>.</para>
/// <para>Co-op (§13, N1): the latch is about the host's <i>world</i>. The business a guest brought is the guest's own, so
/// <see cref="ReloadOwn"/> first hands it back to the guest's own files (<see cref="BusinessSlots.HandBack"/>).</para>
/// </summary>
public static class SaveGuard
{
    static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
    static NetworkManager hooked;
    static bool reloading;

    /// <summary>Latched: the statics show (or may show) someone else's world.</summary>
    public static bool Foreign { get; private set; }

    /// <summary>Why it latched ("join", "OnClientStarted", …); null when not latched.</summary>
    public static string ForeignReason { get; private set; }

    /// <summary>
    /// Saving is allowed: not latched, and both legacy files loaded cleanly (a good parse, a recovered backup, or no file yet).
    /// Reading it runs the <c>GameSession.IsGuest</c> backstop, so a raw <c>StartClient</c> latches too.
    /// </summary>
    public static bool WritesAllowed
    {
        get
        {
            _ = GameSession.IsGuest; // latches if we're connected as a guest without having gone through Join
            return !Foreign && LegacySave.LoadedCleanly;
        }
    }

    /// <summary>A save was refused (once per operation per latch). For NetAudit and session toasts (B14).</summary>
    public static event Action<string> Blocked;

    /// <summary>
    /// Latch. Called on the Join click before any scene load and from <c>OnClientStarted</c>. If we aren't connected as a guest
    /// yet, the statics are still our own, so a pending (≤2 s) write is flushed first; after that nothing is written.
    /// </summary>
    public static void MarkForeign(string reason)
    {
        if (Foreign) return;
        if (!ConnectedAsGuest()) LegacySave.FlushPending("before " + (reason ?? "join"));
        BusinessSlots.OnMarkForeign(); // co-op N1: remember this player's own business (and day) before the Town arrives
        Foreign = true;
        ForeignReason = string.IsNullOrEmpty(reason) ? "join" : reason;
        reported.Clear();
        LegacySave.DiscardPending();
        Debug.Log($"[SaveGuard] Saving paused ({ForeignReason}): your own save stays untouched until you're back in your own game.");
        NetAudit.Event("save", "foreign: " + ForeignReason);
    }

    /// <summary>
    /// Put the player's own data back and clear the latch: reset both statics while still latched, load both files, then clear.
    /// A no-op when not latched. If loading throws, the latch stays on (half-restored statics are never written).
    /// </summary>
    public static void ReloadOwn(string reason)
    {
        if (!Foreign)
        {
            Debug.Log($"[SaveGuard] ReloadOwn({reason}): not latched, nothing to reload.");
            return;
        }
        reloading = true;
        try
        {
            // Co-op N1: the business this guest brought is theirs: write it back to their own files first (never the host's).
            try { BusinessSlots.HandBack(reason); }
            catch (Exception e) { Debug.LogException(e); }
            LegacySave.DiscardPending();
            GameSession.ResetAll(false);
            GameSession.Load();
            EbaeMarket.Load();
            // Caches built from GameSession's counters while the statics weren't ours (a guest's local cash in today's stats,
            // Work Order progress) must be re-read from the reloaded counters, or the next save writes them back (fixer, W2).
            DailyStats.Reload();
            WorkOrders.Reload();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Debug.LogError($"[SaveGuard] Couldn't reload your own save ({reason}); saving stays paused this session.");
            return;
        }
        finally { reloading = false; }

        Foreign = false;
        ForeignReason = null;
        reported.Clear();
        LegacySave.DiscardPending();
        BusinessSlots.EndTown();
        Debug.Log($"[SaveGuard] Your own save is loaded again ({reason}); saving resumes.");
        NetAudit.Event("save", "reloaded own: " + reason);
    }

    /// <summary>LegacySave reports a refused write here (deduplicated per operation while the latch/block lasts).</summary>
    internal static void ReportBlocked(string op)
    {
        if (reloading || string.IsNullOrEmpty(op) || !reported.Add(op)) return;
        string why = Foreign
            ? $"{op} refused: saving is paused ({ForeignReason})"
            : $"{op} refused: {LegacySave.BlockReason ?? "the save hasn't loaded"}";
        Debug.Log("[SaveGuard] " + why);
        NetAudit.Event("save", why);
        try { Blocked?.Invoke(why); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Start-of-play-session reset (LegacySave.ResetForPlaySession calls it before the first Load).</summary>
    internal static void ResetForPlaySession()
    {
        Foreign = false;
        ForeignReason = null;
        reloading = false;
        reported.Clear();
        Blocked = null;
    }

    static bool ConnectedAsGuest()
    {
        var nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening && !nm.IsServer;
    }

    // ---- NGO backstops. NetworkManager clears its static events at SubsystemRegistration, so subscribe after that. ----

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void HookNetwork()
    {
        hooked = null;
        NetworkManager.OnInstantiated -= Hook;
        NetworkManager.OnInstantiated += Hook;
        NetworkManager.OnDestroying -= Unhook;
        NetworkManager.OnDestroying += Unhook;
        if (NetworkManager.Singleton != null) Hook(NetworkManager.Singleton);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void HookLate()
    {
        if (NetworkManager.Singleton != null) Hook(NetworkManager.Singleton);
    }

    static void Hook(NetworkManager nm)
    {
        if (nm == null || hooked == nm) return;
        Unhook(hooked);
        hooked = nm;
        nm.OnClientStarted += OnClientStarted;
        nm.OnClientStopped += OnClientStopped;
    }

    static void Unhook(NetworkManager nm)
    {
        if (nm == null || hooked != nm) return;
        nm.OnClientStarted -= OnClientStarted;
        nm.OnClientStopped -= OnClientStopped;
        hooked = null;
    }

    static void OnClientStarted()
    {
        var nm = hooked;
        if (nm != null && !nm.IsServer) MarkForeign("OnClientStarted");
    }

    static void OnClientStopped(bool wasHost)
    {
        if (!wasHost && Foreign) ReloadOwn("OnClientStopped");
    }
}

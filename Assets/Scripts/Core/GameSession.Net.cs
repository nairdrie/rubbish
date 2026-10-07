using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// GameSession's co-op half (Docs/Multiplayer.md §13, package N1).
/// <list type="bullet">
/// <item><b>Guest guard.</b> On a connected guest, cash and curb records belong to the host. A write to them outside
/// <see cref="NetMirror"/> is dropped, logged once per operation and counted (<see cref="NetAudit.GuestWritesDropped"/>).
/// Everything else in GameSession is the guest's own business and stays writable.</item>
/// <item><b>Mirror.</b> The host's decisions land on a guest through <see cref="NetMirror"/>, which fires the usual events
/// (<see cref="CashChanged"/>, <see cref="CurbTrashChanged"/>, …) so the HUD and Feel work unchanged.</item>
/// <item><b>Business identity.</b> <see cref="BusinessId"/> / <see cref="BusinessName"/> name the business this GameSession
/// holds; <see cref="BusinessSlots"/> switches between a player's businesses and hands a guest's back to its own files.</item>
/// </list>
/// </summary>
public static partial class GameSession
{
    static readonly HashSet<string> droppedOps = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Host-owned GameSession state (cash, curb records) can't be written here right now: this is a connected guest and the
    /// write isn't coming from <see cref="NetMirror"/>.
    /// </summary>
    public static bool WritesBlocked => !NetMirror.Applying && IsConnectedGuest;

    /// <summary>A connected guest (a client that isn't the host). Unlike <see cref="IsGuest"/> this never latches SaveGuard.</summary>
    public static bool IsConnectedGuest
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening && !nm.IsServer;
        }
    }

    /// <summary>True (and counted) when <paramref name="op"/> must be dropped: a guest writing host-owned state.</summary>
    static bool BlockGuestWrite(string op)
    {
        if (!WritesBlocked) return false;
        NetAudit.GuestWriteDropped(op);
        if (droppedOps.Add(op))
            Debug.Log($"[GameSession] Guest write dropped: {op} (the host owns this; it arrives through the mirror). Logged once.");
        return true;
    }

    /// <summary>Curb records changed in a way the list doesn't show (a claim, WorldState): PickupStops re-sync.</summary>
    internal static void NotifyCurbTrashChanged() => CurbTrashChanged?.Invoke();

    /// <summary>Make sure this business has an id (a save from before N1 gets one now; it's written with the next save).</summary>
    public static string EnsureBusinessId()
    {
        if (string.IsNullOrEmpty(BusinessId)) BusinessId = BusinessIds.New();
        return BusinessId;
    }

    /// <summary>Rename the local business (≤24 characters; cleaned). Saved with the next save.</summary>
    public static void SetBusinessName(string name)
    {
        string clean = BusinessColors.CleanName(name);
        if (clean == BusinessName) return;
        BusinessName = clean;
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>
    /// The statics become a brand-new business (BusinessSlots, "Start a new business"): everything reset, a new id and name.
    /// The world half (day, curbs) is left alone. Never saves by itself.
    /// </summary>
    internal static void BeginNewBusiness(string id, string name)
    {
        int day = Day;
        var curbs = new List<CurbBag>(CurbBags);
        ResetAll(false);
        Day = day;
        CurbBags.AddRange(curbs);
        BusinessId = BusinessIds.IsValid(id) ? id : BusinessIds.New();
        BusinessName = BusinessColors.CleanName(name);
        NotifyChanged();
    }

    /// <summary>
    /// Load a business save (the same JSON <see cref="BuildSaveJson(long, int)"/> writes) into the statics, keeping the
    /// current day (the world half). BusinessSlots uses it for a business that isn't the legacy save.
    /// </summary>
    internal static void LoadBusinessJson(string json)
    {
        int day = Day;
        ApplySaveJson(json);
        Day = day;
        NotifyChanged();
    }

    /// <summary>The business JSON parses (BusinessSlots checks a slot file before offering it).</summary>
    internal static bool BusinessJsonParses(string json) => SaveJsonParses(json);

    /// <summary>
    /// The host's decisions applied on a guest (and the host's own replication). Everything here fires the same events the
    /// local code would, so the HUD, floaters and stats follow. Only <c>Net/**</c> calls these.
    /// </summary>
    public static class NetMirror
    {
        static int depth;

        /// <summary>A mirror write is in progress: guest guards let it through.</summary>
        public static bool Applying => depth > 0;

        /// <summary>Set the local business's cash to the host's <paramref name="balanceCents"/>; <paramref name="deltaCents"/> drives
        /// the HUD floater and ticker (<see cref="CashChanged"/>).</summary>
        public static void SetCashCents(long balanceCents, long deltaCents, string reason, Vector3? pos)
        {
            depth++;
            try
            {
                long before = cashCents;
                cashCents = balanceCents;
                if (deltaCents > 0) TotalCashEarned += deltaCents / 100f;
                if (deltaCents != 0 || before != balanceCents)
                {
                    CashChanged?.Invoke(deltaCents / 100f, reason, pos);
                    NotifyChanged();
                }
            }
            finally { depth--; }
        }

        /// <summary>Replace the curb records with the host's (a guest's view; contents are placeholders until opened).</summary>
        public static void SetCurbs(IReadOnlyList<CurbBag> records)
        {
            depth++;
            try
            {
                CurbBags.Clear();
                if (records != null)
                    for (int i = 0; i < records.Count; i++) if (records[i] != null) CurbBags.Add(records[i]);
                CurbTrashChanged?.Invoke();
                NotifyChanged();
            }
            finally { depth--; }
        }

        /// <summary>Run <paramref name="write"/> as a mirror write (host-owned state may change inside it).</summary>
        public static void Run(Action write)
        {
            if (write == null) return;
            depth++;
            try { write(); }
            finally { depth--; }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetNetStatics() => droppedOps.Clear();
}

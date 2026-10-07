using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One posting on the <see cref="Ledger"/>. <c>kind</c> is "credit", "charge", "spend" or "rep".
/// <c>amount</c> is the signed cash change for credit (&gt; 0), charge and spend (&lt; 0), or the applied Rep for "rep".
/// "Net" income = the sum of credit + charge amounts; spends (purchases) are excluded.
/// </summary>
public struct LedgerEntry
{
    public string kind;          // credit | charge | spend | rep
    public string id;            // LedgerIds.* for cash, RepIds.* for rep
    public float amount;
    public string reason;
    public string districtId;
    public Vector3? pos;
    public double atHours;       // GameClock.TotalHours when posted
    /// <summary>The posting in whole cents for cash kinds (exact; <see cref="amount"/> is the same in dollars). 0 for "rep".</summary>
    public long cents;
    /// <summary>The business it was posted to (<see cref="Businesses"/>); null/empty or the local id = this machine's own.</summary>
    public string businessId;
}

/// <summary>
/// The single funnel for money and Rep (Docs/Multiplayer.md §9 rule 1; Docs/Progression.md §9.2). New code calls these,
/// never <c>GameSession.AddCash</c> directly. Money moves in whole cents.
/// <para><b>Businesses (§13, N1).</b> Every call targets a business: the local player's own (the default, held in
/// <see cref="GameSession"/>), or the one named by an enclosing <see cref="ToBusiness"/> scope, e.g. the business whose truck
/// took a curb bag: <c>using (Ledger.ToBusiness(id)) CurbTrash.CollectIntoTruck(…)</c>. Who applies it:</para>
/// <list type="bullet">
/// <item>Host (and solo): the local business through <see cref="GameSession.TryAddCash"/>; any other business through
/// <see cref="Businesses"/> (its owner's game is told the new balance and posts it there).</item>
/// <item>Guest, own business: sent to the host as a request (rate- and size-limited); the change arrives back through
/// <see cref="GameSession.NetMirror"/> and is posted then. A guest can never move another business's money.</item>
/// </list>
/// <see cref="Posted"/> carries the local business's postings only (daily stats, Work Orders, NetAudit);
/// <see cref="PostedAnyBusiness"/> carries every posting this machine made, for any business (host audits, payroll).
/// STEP 0 CONTRACT (builder A).
/// </summary>
public static class Ledger
{
    public const string KindCredit = "credit", KindCharge = "charge", KindSpend = "spend", KindRep = "rep";

    /// <summary>Every posting to the local business (cash and Rep). Daily stats, the Career page and Work Orders listen.</summary>
    public static event Action<LedgerEntry> Posted;

    /// <summary>Every posting this machine applied, to any business (<see cref="LedgerEntry.businessId"/> says which).</summary>
    public static event Action<LedgerEntry> PostedAnyBusiness;

    static readonly List<string> targets = new List<string>();

    /// <summary>
    /// N4 payroll (Docs/Multiplayer.md §13.6): a credit or charge made with no <see cref="ToBusiness"/> scope is offered here
    /// first. An employee working in the employer's shop hands it to the employer's business and returns true (taken);
    /// false = post it as usual. Args: kind, signed cents, reason, ledger id, world position, district id.
    /// </summary>
    public static Func<string, long, string, string, Vector3?, string, bool> Redirect;

    /// <summary>
    /// Route every Ledger call inside the scope to <paramref name="businessId"/> (null = the local business). Scopes nest;
    /// dispose to end it. Moving another business's money only works on the host.
    /// </summary>
    public static IDisposable ToBusiness(string businessId)
    {
        targets.Add(businessId);
        return new TargetScope(targets.Count);
    }

    /// <summary>The business the next call goes to (null = the local business).</summary>
    public static string TargetBusiness => targets.Count > 0 ? targets[targets.Count - 1] : null;

    sealed class TargetScope : IDisposable
    {
        readonly int depth;
        bool done;
        public TargetScope(int depth) { this.depth = depth; }
        public void Dispose()
        {
            if (done) return;
            done = true;
            if (targets.Count >= depth) targets.RemoveRange(depth - 1, targets.Count - depth + 1);
        }
    }

    /// <summary>The target is this machine's own business (no scope, or a scope naming it).</summary>
    static bool TargetIsLocal(out string target)
    {
        target = TargetBusiness;
        return string.IsNullOrEmpty(target) || target == Businesses.LocalId;
    }

    /// <summary>Income (fees, recycling, bonuses...). A negative amount is treated as a charge.</summary>
    public static void Credit(float amount, string reason, string id, Vector3? worldPos = null, string districtId = null)
    {
        if (amount < 0f) { Charge(-amount, reason, id, worldPos, districtId); return; }
        MoveCash(KindCredit, amount, reason, id, worldPos, districtId);
    }

    /// <summary>Fines, wages, fees: always applied, cash may go below zero. A negative amount is treated as a credit.</summary>
    public static void Charge(float amount, string reason, string id, Vector3? worldPos = null, string districtId = null)
    {
        if (amount < 0f) { Credit(-amount, reason, id, worldPos, districtId); return; }
        MoveCash(KindCharge, -amount, reason, id, worldPos, districtId);
    }

    /// <summary>Purchases: only if affordable (<see cref="GameSession.TrySpend"/>). Excluded from "net" income.</summary>
    public static bool Spend(float amount, string reason, string id = LedgerIds.Purchase)
    {
        long cents = GameSession.ToCents(Mathf.Max(0f, amount));
        if (!TargetIsLocal(out string target)) return Businesses.HostApply(target, KindSpend, -cents, reason, id, null, null);
        if (GameSession.IsConnectedGuest && !GameSession.NetMirror.Applying)
        {
            // A guest's own business: affordable by the host's last word; the host debits it and mirrors the new balance back.
            if (cents > GameSession.CashCents) return false;
            if (cents > 0) Businesses.RequestLedger(KindSpend, -cents, reason, id, null, null);
            return true;
        }
        if (!GameSession.TrySpend(cents / 100f, reason)) return false;
        if (cents > 0) Post(KindSpend, id, -cents, reason, null, null);
        return true;
    }

    /// <summary>Rep for an event in <paramref name="districtId"/> (null = the starter district, ×1): base × that district's
    /// Rep multiplier. Returns the Rep actually applied.</summary>
    public static float Rep(string repId, float baseRep, string districtId = null, Vector3? worldPos = null)
    {
        var d = Districts.Get(districtId);
        return ApplyRep(repId, baseRep * (d != null ? d.repMult : 1f), districtId, worldPos);
    }

    /// <summary>Rep marked "top" in §4.1 (shift, Work Orders): base × the highest unlocked district's multiplier.</summary>
    public static float RepTop(string repId, float baseRep)
    {
        var top = Districts.Top;
        return ApplyRep(repId, baseRep * (top != null ? top.repMult : 1f), null, null);
    }

    // ------------------------------------------------------------------ internals

    static void MoveCash(string kind, float signed, string reason, string id, Vector3? worldPos, string districtId)
    {
        long cents = GameSession.ToCents(signed);
        if (cents == 0) return;
        if (targets.Count == 0 && Redirect != null)
        {
            bool taken = false;
            try { taken = Redirect(kind, cents, reason, id, worldPos, districtId); }
            catch (Exception e) { Debug.LogException(e); }
            if (taken) return;
        }
        if (!TargetIsLocal(out string target))
        {
            Businesses.HostApply(target, kind, cents, reason, id, worldPos, districtId); // posts PostedAnyBusiness itself
            return;
        }
        if (GameSession.IsConnectedGuest && !GameSession.NetMirror.Applying)
        {
            // A guest's own business: the host applies it; the balance comes back through NetMirror and is posted then.
            Businesses.RequestLedger(kind, cents, reason, id, worldPos, districtId);
            return;
        }
        // The one money write: GameSession refuses it on a guest, and only what was applied is posted (D1).
        if (!GameSession.TryAddCash(cents / 100f, reason, worldPos, out long applied)) return;
        Post(kind, id, applied, reason, districtId, worldPos);
    }

    static float ApplyRep(string repId, float applied, string districtId, Vector3? worldPos)
    {
        if (Mathf.Approximately(applied, 0f)) return 0f;
        if (!TargetIsLocal(out string target))
        {
            return Businesses.HostApplyRep(target, applied, repId, districtId, worldPos) ? applied : 0f;
        }
        GameSession.AddRep(applied, repId, districtId, worldPos);
        PostRep(repId, applied, districtId, worldPos, null);
        return applied;
    }

    static void Post(string kind, string id, long cents, string reason, string districtId, Vector3? pos)
    {
        Raise(new LedgerEntry
        {
            kind = kind, id = id, amount = cents / 100f, cents = cents, reason = reason, districtId = districtId, pos = pos,
            atHours = GameClock.TotalHours, businessId = Businesses.LocalId,
        }, true);
    }

    static void PostRep(string repId, float applied, string districtId, Vector3? pos, string businessId)
    {
        bool local = string.IsNullOrEmpty(businessId) || businessId == Businesses.LocalId;
        Raise(new LedgerEntry
        {
            kind = KindRep, id = repId, amount = applied, reason = repId, districtId = districtId, pos = pos,
            atHours = GameClock.TotalHours, businessId = local ? Businesses.LocalId : businessId,
        }, local);
    }

    /// <summary>
    /// <see cref="Businesses"/> applied a cash change to another business (host) or the host's change arrived for the local
    /// business (guest mirror): post it. <paramref name="local"/> = it's this machine's own business (raises <see cref="Posted"/>).
    /// </summary>
    internal static void PostApplied(string kind, string id, long cents, string reason, string districtId, Vector3? pos, string businessId, bool local)
    {
        if (kind == KindRep) { PostRep(id, cents / 100f, districtId, pos, businessId); return; }
        Raise(new LedgerEntry
        {
            kind = kind, id = id, amount = cents / 100f, cents = cents, reason = reason, districtId = districtId, pos = pos,
            atHours = GameClock.TotalHours, businessId = businessId,
        }, local);
    }

    /// <summary>Rep applied to another business (host) or arrived for the local one (guest): post it.</summary>
    internal static void PostAppliedRep(string repId, float applied, string districtId, Vector3? pos, string businessId) =>
        PostRep(repId, applied, districtId, pos, businessId);

    static void Raise(LedgerEntry e, bool local)
    {
        if (local) Invoke(Posted, e);
        Invoke(PostedAnyBusiness, e);
    }

    static void Invoke(Action<LedgerEntry> handler, LedgerEntry e)
    {
        if (handler == null) return;
        // A listener's bug must never break the money flow that raised it.
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<LedgerEntry>)d)(e); } catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Posted = null;
        PostedAnyBusiness = null;
        Redirect = null;
        targets.Clear();
    }
}

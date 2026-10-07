using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One job in the Town (Docs/Multiplayer.md §13.6): <see cref="employeeClientId"/> works for the business of
/// <see cref="employerClientId"/> at <see cref="wageCents"/> per in-game hour. Host-written in <see cref="PayrollNet"/>.</summary>
public struct PayrollJob : INetworkSerializable, IEquatable<PayrollJob>
{
    public ulong employeeClientId, employerClientId;
    public FixedString64Bytes employeeBusinessId, employerBusinessId;
    /// <summary>Wage per in-game hour, in cents ($1–$200).</summary>
    public int wageCents;
    /// <summary>Zero = fixed hourly wage; otherwise this percentage of the employer's eligible positive income.</summary>
    public byte sharePercent;
    /// <summary>Fractional cents carried between share postings (hundredths of a cent, 0..99).</summary>
    public byte shareRemainder;
    /// <summary>GameClock.TotalHours the employee has been paid up to (host).</summary>
    public double paidThroughHours;
    /// <summary>Paydays in a row the employer couldn't cover (for the warning; never auto-releases).</summary>
    public byte missedHours;
    /// <summary>Total wages skipped on this job. Later successful paydays do not silently erase them.</summary>
    public long unpaidCents;
    /// <summary>Total cents paid on this job so far (shown in the Payroll app).</summary>
    public long paidCents;

    public string EmployerBusinessId => employerBusinessId.ToString();
    public string EmployeeBusinessId => employeeBusinessId.ToString();
    public float Wage => wageCents / 100f;
    public bool IsEarningsShare => sharePercent > 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref employeeClientId);
        s.SerializeValue(ref employerClientId);
        s.SerializeValue(ref employeeBusinessId);
        s.SerializeValue(ref employerBusinessId);
        s.SerializeValue(ref wageCents);
        s.SerializeValue(ref sharePercent);
        s.SerializeValue(ref shareRemainder);
        s.SerializeValue(ref paidThroughHours);
        s.SerializeValue(ref missedHours);
        s.SerializeValue(ref unpaidCents);
        s.SerializeValue(ref paidCents);
    }

    public bool Equals(PayrollJob o) =>
        employeeClientId == o.employeeClientId && employerClientId == o.employerClientId && wageCents == o.wageCents &&
        sharePercent == o.sharePercent && shareRemainder == o.shareRemainder &&
        paidThroughHours.Equals(o.paidThroughHours) && missedHours == o.missedHours && unpaidCents == o.unpaidCents && paidCents == o.paidCents &&
        employeeBusinessId.Equals(o.employeeBusinessId) && employerBusinessId.Equals(o.employerBusinessId);

    public override bool Equals(object obj) => obj is PayrollJob o && Equals(o);
    public override int GetHashCode() => (int)employeeClientId ^ ((int)employerClientId << 8) ^ wageCents;
    public override string ToString() => $"client {employeeClientId} works for client {employerClientId} at {Payroll.PayText(this)} (missed {missedHours} in a row, unpaid {unpaidCents}c)";
}

/// <summary>
/// Payroll (Docs/Multiplayer.md §13.6, package N4): hiring other players in the Town, read and requested from any machine.
/// The host decides everything in <see cref="PayrollNet"/>; this facade is what UI, permissions and bots call.
/// <list type="bullet">
/// <item><b>Rules:</b> one employer at a time; nobody hires themselves; an employer can't be someone's employee and an employee
/// can't hire. Wage $1–$200 per in-game hour (1 in-game hour = 50 real seconds).</item>
/// <item><b>On the job:</b> <see cref="Permissions.EmployeeHook"/> / <see cref="Permissions.DriverHook"/> let the employee work in
/// the employer's shop and drive its truck; shop pay made there goes to the employer (<see cref="Ledger.Redirect"/>).</item>
/// <item><b>Sessions only:</b> jobs are never saved. Only their Ledger effects (wages paid and earned) stay with each business.</item>
/// </list>
/// </summary>
public static class Payroll
{
    public const int MinWageCents = 100, MaxWageCents = 20000, DefaultWageCents = 500;
    public const int MinSharePercent = 5, MaxSharePercent = 50, DefaultSharePercent = 20, MaxCrewSharePercent = 75;
    public const string LedgerWagesPaid = "payroll_wages", LedgerWageEarned = "payroll_wage";
    public const string LedgerSharesPaid = "payroll_shares", LedgerShareEarned = "payroll_share";

    /// <summary>A confirmed local job transition, sent by the host only to the affected player.</summary>
    public static event Action<string, string, bool> Announcement;

    /// <summary>The jobs changed (hired, fired, quit, paid) on this machine.</summary>
    public static event Action Changed;

    /// <summary>A job offer for the local player arrived: (employer client id, wage cents, "Nick's RUBBISH CO.").</summary>
    public static event Action<ulong, int, string> OfferReceived;
    /// <summary>Full contract terms for the offer card: employer, fixed wage, earnings share, business label.</summary>
    public static event Action<ulong, int, byte, string> ContractOfferReceived;

    /// <summary>The host answered something the local player asked (offer sent, refused, …): (ok, sentence).</summary>
    public static event Action<bool, string> Notice;

    public static PayrollNet Net => PayrollNet.Instance != null && PayrollNet.Instance.IsSpawned ? PayrollNet.Instance : null;

    /// <summary>Payroll is available: a Town with the one world on (solo has nobody to hire).</summary>
    public static bool Available => Net != null && DepotRowWorld.Active;

    public static ulong LocalClientId => Permissions.LocalClientId;

    // ------------------------------------------------------------------ queries

    public static IReadOnlyList<PayrollJob> Jobs
    {
        get
        {
            var n = Net;
            var copy = new List<PayrollJob>();
            if (n == null) return copy;
            for (int i = 0; i < n.JobList.Count; i++) copy.Add(n.JobList[i]);
            return copy;
        }
    }

    public static bool TryGetJobOf(ulong employeeClientId, out PayrollJob job)
    {
        var n = Net;
        if (n != null)
            for (int i = 0; i < n.JobList.Count; i++)
                if (n.JobList[i].employeeClientId == employeeClientId) { job = n.JobList[i]; return true; }
        job = default;
        return false;
    }

    public static bool IsEmployee(ulong clientId) => TryGetJobOf(clientId, out _);

    public static bool IsEmployer(ulong clientId)
    {
        var n = Net;
        if (n == null) return false;
        for (int i = 0; i < n.JobList.Count; i++) if (n.JobList[i].employerClientId == clientId) return true;
        return false;
    }

    public static List<PayrollJob> EmployeesOf(ulong employerClientId)
    {
        var list = new List<PayrollJob>();
        var n = Net;
        if (n == null) return list;
        for (int i = 0; i < n.JobList.Count; i++) if (n.JobList[i].employerClientId == employerClientId) list.Add(n.JobList[i]);
        return list;
    }

    /// <summary>The local player's job, if they have one.</summary>
    public static bool TryGetLocalJob(out PayrollJob job) => TryGetJobOf(LocalClientId, out job);

    /// <summary>The Depot Row lot of a job's employer (−1 = their shop isn't open).</summary>
    public static int EmployerLot(PayrollJob job)
    {
        var net = DepotRowNet.Instance;
        return net != null ? net.LotOwnedBy(job.employerClientId) : -1;
    }

    /// <summary><see cref="Permissions.EmployeeHook"/>: <paramref name="clientId"/> works for the business on <paramref name="lot"/>.</summary>
    public static bool WorksFor(ulong clientId, int lot) => lot >= 0 && TryGetJobOf(clientId, out var j) && EmployerLot(j) == lot;

    /// <summary>"Nick" (the owner's display name), from the business registry.</summary>
    public static string NameOf(ulong clientId)
    {
        if (Businesses.TryGetForClient(clientId, out var b) && b.OwnerName.Length > 0) return b.OwnerName;
        return DepotBusinesses.OwnerNameFor(clientId);
    }

    /// <summary>"Nick's RUBBISH CO."</summary>
    public static string BusinessLabelOf(ulong clientId)
    {
        if (Businesses.TryGetForClient(clientId, out var b)) return $"{b.OwnerLine} {b.Name}".Trim();
        return $"{BusinessColors.OwnerLine(NameOf(clientId))} {DepotBusinesses.DefaultName}";
    }

    public static int ColorOf(ulong clientId) => Businesses.TryGetForClient(clientId, out var b) ? b.color : 0;

    /// <summary>"$12/hr".</summary>
    public static string WageText(int cents) => $"{Economy.Money(cents / 100f)}/hr";
    public static string PayText(int cents, int sharePercent) => sharePercent > 0 ? $"{sharePercent}% earnings share" : WageText(cents);
    public static string PayText(PayrollJob job) => PayText(job.wageCents, job.sharePercent);
    public static string PayExplanation(int cents, int sharePercent) => sharePercent > 0
        ? $"{sharePercent}% of this business's collection, recycling, compactor, sale and bonus income, paid as it earns. No hourly charge; no income means no pay. Purchases, refunds and wages do not count."
        : $"{WageText(cents)} from the employer's cash every in-game hour (50 real seconds), even when nobody earns anything.";

    public static int CrewSharePercent(ulong employer)
    {
        int total = 0;
        foreach (var j in EmployeesOf(employer)) total += j.sharePercent;
        return total;
    }

    /// <summary>
    /// Can <paramref name="employer"/> offer <paramref name="target"/> a job? Null = yes; else the sentence to show. The host
    /// checks the same rules again when the offer arrives.
    /// </summary>
    public static string WhyNotHire(ulong employer, ulong target)
    {
        if (!Available) return "Hiring needs friends in your Town.";
        if (employer == target) return "You can't hire yourself. Nice try.";
        if (!Businesses.TryGetForClient(target, out _)) return $"{NameOf(target)} hasn't opened a business yet.";
        if (!Businesses.TryGetForClient(employer, out _)) return "Open your business first.";
        if (IsEmployee(employer)) return "You're on someone else's payroll. Quit that job first (Esc).";
        if (TryGetJobOf(target, out var j))
            return j.employerClientId == employer ? $"{NameOf(target)} already works for you." : $"{NameOf(target)} already works for {BusinessLabelOf(j.employerClientId)}.";
        if (IsEmployer(target)) return $"{NameOf(target)} is the boss of their own crew.";
        return null;
    }

    // ------------------------------------------------------------------ requests (any machine)

    /// <summary>Offer <paramref name="target"/> a job at <paramref name="wageCents"/> per in-game hour.</summary>
    public static void Offer(ulong target, int wageCents) => Net?.RequestOffer(target, Mathf.Clamp(wageCents, MinWageCents, MaxWageCents), 0);

    /// <summary>Offer a share-only job: no fixed hourly charge. The host validates the whole crew's percentage.</summary>
    public static void OfferShare(ulong target, int percent) => Net?.RequestOffer(target, 0, (byte)Mathf.Clamp(percent, MinSharePercent, MaxSharePercent));

    /// <summary>Answer the job offer the local player has open.</summary>
    public static void Answer(bool accept) => Net?.RequestAnswer(accept);

    /// <summary>Employer: let <paramref name="employee"/> go (final pay is settled).</summary>
    public static void Fire(ulong employee) => Net?.RequestFire(employee);

    /// <summary>Employee: quit the job (final pay is settled).</summary>
    public static void Quit() => Net?.RequestQuit();

    // ------------------------------------------------------------------ internals

    internal static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
    }

    internal static void RaiseOffer(ulong employer, int wageCents, byte sharePercent, string label)
    {
        try { ContractOfferReceived?.Invoke(employer, wageCents, sharePercent, label); } catch (Exception e) { Debug.LogException(e); }
        try { OfferReceived?.Invoke(employer, wageCents, label); } catch (Exception e) { Debug.LogException(e); }
    }

    internal static void RaiseNotice(bool ok, string text)
    {
        try { Notice?.Invoke(ok, text); } catch (Exception e) { Debug.LogException(e); }
    }

    internal static void RaiseAnnouncement(string title, string detail, bool good)
    {
        try { Announcement?.Invoke(title, detail, good); } catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Changed = null;
        OfferReceived = null;
        ContractOfferReceived = null;
        Notice = null;
        Announcement = null;
    }

    // Permissions and Ledger reset their statics at SubsystemRegistration: hook in after that.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InstallHooks()
    {
        Permissions.EmployeeHook = WorksFor;
        Permissions.DriverHook = WorksFor;
        Ledger.Redirect = PayrollNet.RedirectShopPay;
        NetSession.GuestLeaving = PayrollNet.OnGuestLeaving;
    }
}

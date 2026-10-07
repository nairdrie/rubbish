using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Payroll's replicated state and host logic (Docs/Multiplayer.md §13.6, package N4). Lives on <c>NetSession.prefab</c>
/// (<see cref="NetSessionComponentAttribute"/>; <c>Setup_Coop</c> adds it). Read it through <see cref="Payroll"/>.
/// <list type="bullet">
/// <item><b>Jobs:</b> a host-written <see cref="NetworkList{T}"/> of <see cref="PayrollJob"/>, read by every machine (permissions,
/// HUD line, vests, the Payroll app).</item>
/// <item><b>Requests</b> (ChessController pattern, §9 rule 10): offer / answer / fire / quit / shop pay / kept item, each
/// validated on the host and rate-limited with <see cref="SessionService.GuardFor"/>.</item>
/// <item><b>Pay</b> (host): every in-game hour from the employer's business to the employee's, through the Ledger
/// (<c>Ledger.ToBusiness</c>: a charge to the employer, a credit to the employee). A payday the employer can't cover is
/// missed and both are told. A job remains active until someone quits, is fired or leaves. The part hour is
/// settled when a job ends (fire, quit, release, or either one leaving the Town).</item>
/// <item><b>Shop pay:</b> while an employee is inside the employer's shop, their un-scoped Ledger credits and charges
/// (sorting, recycling, compactor, fines) go to the employer (<see cref="RedirectShopPay"/>); valuables they keep go
/// to the employer's keep crate (<see cref="TryHandOverKept"/>).</item>
/// </list>
/// Nothing here is saved: jobs end with the session; the Ledger effects stay with each business.
/// </summary>
[NetSessionComponent(Order = 30)]
public class PayrollNet : NetworkBehaviour
{
    public static PayrollNet Instance { get; private set; }

    const float OfferSeconds = 30f;
    /// <summary>Largest single shop posting an employee may hand the employer ($5,000).</summary>
    const long MaxShopPayCents = 500_000;
    const string MsgOffer = "rb.pay.offer", MsgAnswer = "rb.pay.answer", MsgJob = "rb.pay.job", MsgShop = "rb.pay.shop", MsgKeep = "rb.pay.keep";

    NetworkList<PayrollJob> jobs;
    public NetworkList<PayrollJob> JobList => jobs;

    struct OfferState { public ulong employer; public int wageCents; public byte sharePercent; public float expiresAt; }

    // host only
    readonly Dictionary<ulong, OfferState> offers = new Dictionary<ulong, OfferState>();
    bool hostHooked;
    readonly Queue<LedgerEntry> incomeQueue = new Queue<LedgerEntry>();
    bool distributingShares;

    void Awake() => jobs = new NetworkList<PayrollJob>();

    public override void OnNetworkSpawn()
    {
        Instance = this;
        jobs.OnListChanged += OnJobsChanged;
        if (IsServer)
        {
            BusinessRegistry.Closing += OnBusinessClosing;
            SessionService.PlayerLeft += OnPlayerLeft;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            Ledger.PostedAnyBusiness += OnBusinessIncome;
            hostHooked = true;
        }
        Payroll.RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        jobs.OnListChanged -= OnJobsChanged;
        if (Instance == this) Instance = null;
        Payroll.RaiseChanged();
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hostHooked) return;
        BusinessRegistry.Closing -= OnBusinessClosing;
        SessionService.PlayerLeft -= OnPlayerLeft;
        Ledger.PostedAnyBusiness -= OnBusinessIncome;
        incomeQueue.Clear();
        offers.Clear();
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        hostHooked = false;
    }

    void OnJobsChanged(NetworkListEvent<PayrollJob> e) => Payroll.RaiseChanged();

    int IndexOfEmployee(ulong clientId)
    {
        for (int i = 0; i < jobs.Count; i++) if (jobs[i].employeeClientId == clientId) return i;
        return -1;
    }

    // =====================================================================
    // Requests (any machine → host)
    // =====================================================================

    internal void RequestOffer(ulong target, int wageCents, byte sharePercent) => OfferRpc(target, wageCents, sharePercent);
    internal void RequestAnswer(bool accept) => AnswerRpc(accept);
    internal void RequestFire(ulong employee) => FireRpc(employee);
    internal void RequestQuit() => QuitRpc();

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void OfferRpc(ulong target, int wageCents, byte sharePercent, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgOffer, sender, 16)) return;
        string why = Payroll.WhyNotHire(sender, target);
        if (why == null && !PayrollShareRules.ValidContract(wageCents, sharePercent, Payroll.CrewSharePercent(sender)))
            why = "Choose $1–$200/hr or 5–50% earnings. Crew shares cannot exceed 75% in total.";
        if (why == null && offers.TryGetValue(target, out var o) && o.employer != sender && Time.realtimeSinceStartup < o.expiresAt)
            why = $"{Payroll.NameOf(target)} is thinking about another offer.";
        if (why != null)
        {
            Tell(sender, false, why);
            Audit("offer-refused", sender, target, wageCents, why);
            return;
        }
        offers[target] = new OfferState { employer = sender, wageCents = wageCents, sharePercent = sharePercent, expiresAt = Time.realtimeSinceStartup + OfferSeconds };
        Audit("offer", sender, target, wageCents, null);
        OfferCardRpc(sender, wageCents, sharePercent, Fixed128(Payroll.BusinessLabelOf(sender)), RpcTarget.Single(target, RpcTargetUse.Temp));
        Tell(sender, true, $"Job offer sent to {Payroll.NameOf(target)} ({Payroll.PayText(wageCents, sharePercent)}).");
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void OfferCardRpc(ulong employer, int wageCents, byte sharePercent, FixedString128Bytes label, RpcParams p = default)
    {
        Payroll.RaiseOffer(employer, wageCents, sharePercent, label.ToString());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void AnswerRpc(bool accept, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgAnswer, sender, 8)) return;
        if (!offers.TryGetValue(sender, out var o)) return;
        offers.Remove(sender);
        if (Time.realtimeSinceStartup > o.expiresAt + 2f)
        {
            Tell(sender, false, "That offer ran out. Ask them again!");
            return;
        }
        if (!accept)
        {
            Tell(o.employer, false, $"{Payroll.NameOf(sender)} said no thanks.");
            Audit("declined", o.employer, sender, o.wageCents, null);
            return;
        }
        string why = Payroll.WhyNotHire(o.employer, sender);
        if (why == null && !PayrollShareRules.ValidContract(o.wageCents, o.sharePercent, Payroll.CrewSharePercent(o.employer)))
            why = "The crew's earnings shares are now full. Ask for a new offer.";
        if (why != null)
        {
            Tell(sender, false, "The job's gone: " + why);
            Tell(o.employer, false, why);
            return;
        }
        if (!Businesses.TryGetForClient(o.employer, out var er) || !Businesses.TryGetForClient(sender, out var ee)) return;
        var job = new PayrollJob
        {
            employeeClientId = sender, employerClientId = o.employer, wageCents = o.wageCents, sharePercent = o.sharePercent,
            paidThroughHours = GameClock.TotalHours,
        };
        job.employerBusinessId.CopyFromTruncated(er.Id);
        job.employeeBusinessId.CopyFromTruncated(ee.Id);
        jobs.Add(job);
        Audit("hired", o.employer, sender, o.wageCents, null);
        Debug.Log($"[Payroll] {Payroll.NameOf(sender)} now works for {Payroll.BusinessLabelOf(o.employer)} at {Payroll.PayText(job)}.");
        Tell(sender, true, $"You work for {Payroll.BusinessLabelOf(o.employer)} now! {Payroll.PayText(job)}.");
        Tell(o.employer, true, $"{Payroll.NameOf(sender)} is on your payroll ({Payroll.PayText(job)}).");
        Announce(sender, "HIRED!", $"Working for {Payroll.BusinessLabelOf(o.employer)}  •  {Payroll.PayText(job)}", true);
        Announce(o.employer, "HIRED!", $"{Payroll.NameOf(sender)} joined your crew", true);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void FireRpc(ulong employee, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgJob, sender, 8)) return;
        int i = IndexOfEmployee(employee);
        if (i < 0 || jobs[i].employerClientId != sender) { Tell(sender, false, "They don't work for you."); return; }
        ServerEndJob(i, "fired");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void QuitRpc(RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgJob, sender, 8)) return;
        int i = IndexOfEmployee(sender);
        if (i < 0) { Tell(sender, false, "You don't have a job to quit."); return; }
        ServerEndJob(i, "quit");
    }

    // =====================================================================
    // Host: pay
    // =====================================================================

    void Update()
    {
        if (!IsSpawned || !IsServer) return;
        if (offers.Count > 0) ExpireOffers();
        double now = GameClock.TotalHours;
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            if (jobs[i].IsEarningsShare) continue; // shares are paid on income, never on a timer
            int guard = 0;
            while (i < jobs.Count && now - jobs[i].paidThroughHours >= 1.0 && guard++ < 24)
            {
                ServerPayday(i);
            }
        }
    }

    readonly List<ulong> expired = new List<ulong>();

    void ExpireOffers()
    {
        expired.Clear();
        float t = Time.realtimeSinceStartup;
        foreach (var kv in offers) if (t > kv.Value.expiresAt + 2f) expired.Add(kv.Key);
        foreach (var k in expired)
        {
            var o = offers[k];
            offers.Remove(k);
            Tell(o.employer, false, $"{Payroll.NameOf(k)} didn't answer your job offer.");
        }
    }

    /// <summary>Host: process one in-game hour on job <paramref name="i"/>. A missed payment records unpaid wages but keeps the job.</summary>
    void ServerPayday(int i)
    {
        var j = jobs[i];
        if (j.IsEarningsShare) return;
        long cents = j.wageCents;
        j.paidThroughHours += 1.0;
        if (ServerMoveWage(j, cents, "Wages"))
        {
            j.missedHours = 0;
            j.paidCents += cents;
            jobs[i] = j;
            Audit("payday", j.employerClientId, j.employeeClientId, (int)cents, null);
            return;
        }
        if (j.missedHours < byte.MaxValue) j.missedHours++;
        j.unpaidCents += cents;
        jobs[i] = j;
        Audit("missed", j.employerClientId, j.employeeClientId, (int)cents, null);
        string emp = Payroll.NameOf(j.employeeClientId);
        Tell(j.employeeClientId, false, $"Payday missed! {Payroll.BusinessLabelOf(j.employerClientId)} owes you {Economy.Money(j.unpaidCents / 100f)}. You can quit from Esc.");
        Tell(j.employerClientId, false, $"Payroll missed! You owe {emp} {Economy.Money(j.unpaidCents / 100f)}. They stay hired until they quit or you fire them.");
    }

    /// <summary>Host: move <paramref name="cents"/> of wages from the job's employer to its employee. False if the employer can't cover it.</summary>
    bool ServerMoveWage(PayrollJob j, long cents, string what)
    {
        if (cents <= 0) return true;
        string er = j.EmployerBusinessId, ee = j.EmployeeBusinessId;
        if (!Businesses.TryGet(er, out _) || !Businesses.TryGet(ee, out _)) return false;
        if (CashOf(er) < cents) return false;
        float dollars = cents / 100f;
        string empName = Payroll.NameOf(j.employeeClientId);
        using (Ledger.ToBusiness(er)) Ledger.Charge(dollars, $"{what}: {empName}", j.IsEarningsShare ? Payroll.LedgerSharesPaid : Payroll.LedgerWagesPaid);
        using (Ledger.ToBusiness(ee)) Ledger.Credit(dollars, $"{what} from {Payroll.BusinessLabelOf(j.employerClientId)}", j.IsEarningsShare ? Payroll.LedgerShareEarned : Payroll.LedgerWageEarned);
        return true;
    }

    // Only the host listens to applied postings. A guest's mirror does not run payroll; payroll credits themselves
    // are ineligible. Queue nested genuine income instead of dropping it or recursively distributing transfers.
    void OnBusinessIncome(LedgerEntry entry)
    {
        if (!IsSpawned || !IsServer || !PayrollShareRules.EligibleCredit(entry.kind, entry.cents, entry.id)) return;
        incomeQueue.Enqueue(entry);
        if (distributingShares) return;
        distributingShares = true;
        try
        {
            while (incomeQueue.Count > 0) ServerShareIncome(incomeQueue.Dequeue());
        }
        finally { distributingShares = false; }
    }

    void ServerShareIncome(LedgerEntry entry)
    {
        string business = string.IsNullOrEmpty(entry.businessId) ? Businesses.LocalId : entry.businessId;
        // Snapshot identities: Ledger listeners may update replicated business state during the transfer.
        var employees = new List<ulong>();
        for (int i = 0; i < jobs.Count; i++)
            if (jobs[i].IsEarningsShare && jobs[i].EmployerBusinessId == business) employees.Add(jobs[i].employeeClientId);
        foreach (ulong employee in employees)
        {
            int i = IndexOfEmployee(employee);
            if (i < 0) continue;
            var j = jobs[i];
            if (!j.IsEarningsShare || j.EmployerBusinessId != business) continue;
            long earned = PayrollShareRules.Accrue(entry.cents, j.sharePercent, j.shareRemainder, out byte remainder);
            j.shareRemainder = remainder;
            bool alreadyOwed = j.unpaidCents > 0;
            long due = earned > long.MaxValue - j.unpaidCents ? long.MaxValue : earned + j.unpaidCents;
            // Record accrual before posting transfers. No waiting or guest-authored amount enters this path.
            j.unpaidCents = due;
            jobs[i] = j;
            if (due <= 0) continue;
            long pay = Math.Min(due, Math.Max(0, CashOf(business)));
            if (pay > 0 && ServerMoveWage(j, pay, "Earnings share"))
            {
                j.unpaidCents -= pay;
                j.paidCents += pay;
                jobs[i] = j;
                NetAudit.Event("payroll", new Dictionary<string, object> {
                    { "op", "earnings-share" }, { "employer", j.employerClientId }, { "employee", employee },
                    { "business", business }, { "income", entry.cents }, { "source", entry.id },
                    { "percent", j.sharePercent }, { "earned", earned }, { "paid", pay }, { "remainder", remainder } });
            }
            if (j.unpaidCents > 0 && !alreadyOwed)
            {
                Tell(employee, false, $"Earnings share owed: {Economy.Money(j.unpaidCents / 100f)}. Your employer is out of cash. Job stays active.");
                Tell(j.employerClientId, false, $"Earnings share owed to {Payroll.NameOf(employee)}. Unpaid shares catch up from future income.");
            }
        }
    }

    static long CashOf(string businessId)
    {
        if (businessId == GameSession.BusinessId) return GameSession.CashCents;
        return Businesses.TryGet(businessId, out var b) ? b.cashCents : 0;
    }

    /// <summary>Host: end job <paramref name="i"/> (fired / quit / broke / left): settle the part hour, free the truck, tell both.</summary>
    void ServerEndJob(int i, string why, bool settle = true)
    {
        if (i < 0 || i >= jobs.Count) return;
        var j = jobs[i];
        long part = 0;
        bool paid = true;
        if (settle && !j.IsEarningsShare)
        {
            double hours = Math.Max(0.0, Math.Min(1.0, GameClock.TotalHours - j.paidThroughHours));
            part = (long)Math.Round(hours * j.wageCents, MidpointRounding.AwayFromZero);
            paid = ServerMoveWage(j, part, "Final pay");
            if (paid) j.paidCents += part;
        }
        jobs.RemoveAt(i);
        ServerKickFromTruck(j);
        Audit("end-" + why, j.employerClientId, j.employeeClientId, (int)part, paid ? null : "unpaid");
        Debug.Log($"[Payroll] Job over ({why}): {Payroll.NameOf(j.employeeClientId)} no longer works for {Payroll.BusinessLabelOf(j.employerClientId)}; final {part}c {(paid ? "paid" : "UNPAID")}, {j.paidCents}c in all.");
        string emp = Payroll.NameOf(j.employeeClientId), boss = Payroll.BusinessLabelOf(j.employerClientId);
        string final = part > 0 ? (paid ? $" Final pay {Economy.Money(part / 100f)}." : $" They couldn't pay the last {Economy.Money(part / 100f)}.") : "";
        if (j.unpaidCents > 0) final += $" Earlier unpaid pay: {Economy.Money(j.unpaidCents / 100f)}.";
        string debt = j.unpaidCents > 0 ? $"  •  {Economy.Money(j.unpaidCents / 100f)} unpaid" : "";
        switch (why)
        {
            case "fired":
                Tell(j.employeeClientId, false, $"{boss} let you go.{final}");
                Tell(j.employerClientId, true, $"You fired {emp}.{final}");
                Announce(j.employeeClientId, "FIRED!", $"{boss} let you go{debt}", false);
                Announce(j.employerClientId, "FIRED!", $"{emp} left your crew", false);
                break;
            case "quit":
                Tell(j.employeeClientId, true, $"You quit {boss}.{final}");
                Tell(j.employerClientId, false, $"{emp} quit.{final}");
                Announce(j.employeeClientId, "YOU QUIT", $"No longer working for {boss}{debt}", false);
                Announce(j.employerClientId, "EMPLOYEE QUIT", $"{emp} left your crew", false);
                break;
            case "left":
            case "dropped":
                Tell(j.employeeClientId, false, $"{boss} closed up for the day. Job over.{final}");
                Tell(j.employerClientId, false, $"{emp} went home. Job over.{final}");
                Announce(j.employeeClientId, "JOB ENDED", $"No longer working for {boss}{debt}", false);
                Announce(j.employerClientId, "CREW LEFT", $"{emp} is no longer on payroll", false);
                break;
        }
    }

    /// <summary>Host: a former employee driving the employer's truck gets out.</summary>
    void ServerKickFromTruck(PayrollJob j)
    {
        var net = DepotRowNet.Instance;
        if (net == null) return;
        int lot = net.LotOwnedBy(j.employerClientId);
        var t = lot >= 0 ? net.TruckOf(lot) : null;
        if (t != null && t.HasDriver && t.DriverClientId == j.employeeClientId) t.ServerForceReleaseDriver();
    }

    // ---- leaving
    // A guest who leaves on purpose says so first (NetSession.GuestLeaving → LeavingRpc): its jobs end with final pay while it's
    // still connected, so the cash it takes home matches the host's books. A guest that drops (crash, lost connection) is
    // already gone: its jobs end without the part hour (it could never arrive in that guest's own save).

    /// <summary><see cref="NetSession.GuestLeaving"/>: settle my jobs before I go. Seconds to wait for the answer.</summary>
    internal static float OnGuestLeaving()
    {
        var n = Instance;
        if (n == null || !n.IsSpawned || n.IsServer) return 0f;
        ulong me = n.NetworkManager.LocalClientId;
        if (!Payroll.IsEmployee(me) && !Payroll.IsEmployer(me)) return 0f;
        n.LeavingRpc();
        return 0.8f;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LeavingRpc(RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgJob, sender, 8)) return;
        ServerEndAllOf(sender, settle: true);
    }

    void OnBusinessClosing(string businessId, ulong clientId) => ServerEndAllOf(clientId, settle: false);
    void OnPlayerLeft(PlayerKey key, ulong clientId) => ServerEndAllOf(clientId, settle: false);
    void OnClientDisconnected(ulong clientId)
    {
        if (IsServer && clientId != NetworkManager.ServerClientId) ServerEndAllOf(clientId, settle: false);
    }

    void ServerEndAllOf(ulong clientId, bool settle)
    {
        if (!IsServer || !IsSpawned) return;
        offers.Remove(clientId);
        for (int i = jobs.Count - 1; i >= 0; i--)
            if (i < jobs.Count && (jobs[i].employeeClientId == clientId || jobs[i].employerClientId == clientId))
                ServerEndJob(i, settle ? "left" : "dropped", settle);
    }

    // =====================================================================
    // Shop pay and kept valuables (employee → employer)
    // =====================================================================

    /// <summary>What sorting, binning, smashing and compacting in a shop post (Ledger ids). Only these follow an employee's work.</summary>
    static readonly string[] ShopLedgerIds =
    {
        LedgerIds.Recycle, LedgerIds.Hazard, LedgerIds.Scrap, LedgerIds.LazyCrush, LedgerIds.Contamination, LedgerIds.HazardFine,
        LedgerIds.Landfill,
    };

    /// <summary>The local player's job when they're working inside the employer's shop right now.</summary>
    static bool LocalWorkingInEmployerShop(Vector3? at, out PayrollJob job)
    {
        job = default;
        if (!Payroll.Available || !Payroll.TryGetLocalJob(out job)) return false;
        int lot = Payroll.EmployerLot(job);
        if (lot < 0) return false;
        int here = at.HasValue ? DepotRowLayout.InteriorAt(at.Value) : -1;
        if (here < 0) here = DepotRowWorld.LocalInterior;
        return here == lot;
    }

    /// <summary><see cref="Ledger.Redirect"/>: an employee's un-scoped shop pay inside the employer's shop goes to the employer.</summary>
    internal static bool RedirectShopPay(string kind, long cents, string reason, string id, Vector3? pos, string districtId)
    {
        if (kind != Ledger.KindCredit && kind != Ledger.KindCharge) return false;
        if (Array.IndexOf(ShopLedgerIds, id) < 0) return false; // only the shop's own work (never wages, invoices, pickups)
        if (!LocalWorkingInEmployerShop(pos, out var job)) return false;
        var n = Instance;
        if (n == null) return false;
        if (n.IsServer)
        {
            n.ServerShopPay(n.NetworkManager.LocalClientId, kind, cents, reason, id, pos, districtId);
            return true;
        }
        n.ShopPayRpc(kind == Ledger.KindCredit, cents, Fixed128(reason), Fixed32(id), Fixed32(districtId), pos ?? Vector3.zero, pos.HasValue);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShopPayRpc(bool credit, long cents, FixedString128Bytes reason, FixedString32Bytes id, FixedString32Bytes district, Vector3 pos, bool hasPos,
                    RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgShop, sender, 220)) return;
        bool signOk = credit ? cents > 0 : cents < 0;
        if (!signOk || Math.Abs(cents) > MaxShopPayCents) { Audit("shop-refused", 0, sender, (int)cents, "bad amount"); return; }
        ServerShopPay(sender, credit ? Ledger.KindCredit : Ledger.KindCharge, cents, reason.ToString(), Str(id), hasPos ? pos : (Vector3?)null, Str(district));
    }

    void ServerShopPay(ulong employee, string kind, long cents, string reason, string id, Vector3? pos, string district)
    {
        int i = IndexOfEmployee(employee);
        if (i < 0) { Audit("shop-refused", 0, employee, (int)cents, "no job"); return; }
        var j = jobs[i];
        string er = j.EmployerBusinessId;
        if (!Businesses.TryGet(er, out _)) return;
        string tagged = $"{reason} ({Payroll.NameOf(employee)})";
        float dollars = Math.Abs(cents) / 100f;
        using (Ledger.ToBusiness(er))
        {
            if (kind == Ledger.KindCredit) Ledger.Credit(dollars, tagged, id ?? LedgerIds.Recycle, pos, district);
            else Ledger.Charge(dollars, tagged, id ?? LedgerIds.Contamination, pos, district);
        }
        NetAudit.Event("payroll", new Dictionary<string, object>
        {
            { "op", "shop-pay" }, { "employee", employee }, { "employer", j.employerClientId }, { "business", er }, { "kind", kind },
            { "cents", cents }, { "reason", reason },
        });
    }

    /// <summary>
    /// An employee kept a valuable in the employer's shop: it goes to the employer's keep crate (company property, §13.5).
    /// True = handed over (don't add it to the local inventory).
    /// </summary>
    public static bool TryHandOverKept(ItemInstance item, Vector3 at)
    {
        if (item == null || !LocalWorkingInEmployerShop(at, out var job)) return false;
        var n = Instance;
        if (n == null) return false;
        string json = JsonUtility.ToJson(item);
        if (n.IsServer) n.ServerKeep(n.NetworkManager.LocalClientId, json);
        else n.KeepRpc(json);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void KeepRpc(string json, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgKeep, sender, json != null ? json.Length : 0)) return;
        ServerKeep(sender, json);
    }

    void ServerKeep(ulong employee, string json)
    {
        int i = IndexOfEmployee(employee);
        if (i < 0 || string.IsNullOrEmpty(json) || json.Length > 4000) return;
        var j = jobs[i];
        NetAudit.Event("payroll", new Dictionary<string, object> { { "op", "keep" }, { "employee", employee }, { "employer", j.employerClientId } });
        KeptRpc(json, Fixed64(Payroll.NameOf(employee)), RpcTarget.Single(j.employerClientId, RpcTargetUse.Temp));
    }

    /// <summary>Employer's machine: a valuable an employee kept lands in your inventory (the keep crate).</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void KeptRpc(string json, FixedString64Bytes byName, RpcParams p = default)
    {
        ItemInstance item = null;
        try { item = JsonUtility.FromJson<ItemInstance>(json); } catch (Exception e) { Debug.LogException(e); }
        if (item == null || string.IsNullOrEmpty(item.defId)) return;
        GameSession.AddToInventory(item);
        string what = item.Def != null ? item.Def.name : "something shiny";
        GameHUD.Toast($"{byName} found {what} for your keep crate!", ToastStyle.Rare, 3f);
    }

    // =====================================================================
    // Notices
    // =====================================================================

    void Tell(ulong clientId, bool ok, string text)
    {
        if (clientId != NetworkManager.ServerClientId && !NetworkManager.ConnectedClients.ContainsKey(clientId)) return; // gone
        try { NoticeRpc(ok, Fixed128(text), RpcTarget.Single(clientId, RpcTargetUse.Temp)); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Announce(ulong clientId, string title, string detail, bool good)
    {
        if (clientId != NetworkManager.ServerClientId && !NetworkManager.ConnectedClients.ContainsKey(clientId)) return;
        try { AnnouncementRpc(Fixed128(title), Fixed128(detail), good, RpcTarget.Single(clientId, RpcTargetUse.Temp)); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void AnnouncementRpc(FixedString128Bytes title, FixedString128Bytes detail, bool good, RpcParams p = default) =>
        Payroll.RaiseAnnouncement(title.ToString(), detail.ToString(), good);

    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(bool ok, FixedString128Bytes text, RpcParams p = default)
    {
        string t = text.ToString();
        GameHUD.Toast(t, ok ? ToastStyle.Good : ToastStyle.Bad, 3f);
        Sfx.Play(ok ? "ding" : "buzz", null, 0.6f);
        Payroll.RaiseNotice(ok, t);
    }

    // =====================================================================
    // helpers
    // =====================================================================

    static bool Allowed(string msg, ulong sender, int bytes)
    {
        if (SessionService.GuardFor(msg).Allow(sender, bytes, Time.realtimeSinceStartupAsDouble)) return true;
        NetAudit.Event("guard", $"{msg} from client {sender} over its limit");
        return false;
    }

    static void Audit(string op, ulong employer, ulong employee, int cents, string why)
    {
        var d = new Dictionary<string, object> { { "op", op }, { "employer", employer }, { "employee", employee }, { "cents", cents } };
        if (why != null) d["why"] = why;
        if (Businesses.TryGetForClient(employer, out var er)) { d["employerBiz"] = er.Id; d["employerCash"] = CashOf(er.Id); }
        if (Businesses.TryGetForClient(employee, out var ee)) { d["employeeBiz"] = ee.Id; d["employeeCash"] = CashOf(ee.Id); }
        NetAudit.Event("payroll", d);
    }

    static FixedString128Bytes Fixed128(string s) { var f = new FixedString128Bytes(); f.CopyFromTruncated(s ?? ""); return f; }
    static FixedString64Bytes Fixed64(string s) { var f = new FixedString64Bytes(); f.CopyFromTruncated(s ?? ""); return f; }
    static FixedString32Bytes Fixed32(string s) { var f = new FixedString32Bytes(); f.CopyFromTruncated(s ?? ""); return f; }
    static string Str(FixedString32Bytes f) => f.Length == 0 ? null : f.ToString();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

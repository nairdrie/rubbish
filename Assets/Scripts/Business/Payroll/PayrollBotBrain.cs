using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// <c>-botbrain payroll</c> (package N4 test rig, 3 bots: bot-1 hosts). Everything it sees goes to the NetAudit log
/// (<c>kind = botpay</c>); the host's <c>payroll</c>, <c>bizledger</c>, <c>cargo</c> and <c>curb</c> lines are the evidence.
/// <list type="number">
/// <item>t 20 s: the host puts out curb bags and moves the trucks to their aprons; t 25: hires bot-2 ($12/hr, accepted);
/// t 30: offers bot-3 ($15/hr, declined: bot-3 turns down its first offer).</item>
/// <item>t 40: the host loads 3 bags into its own truck. t 45–75: bot-2 (employee) checks its permissions, drives the boss's
/// truck into the boss's garage, tips it (the host hands the bags over), sorts (shop pay → boss), keeps a valuable (→ the
/// boss's crate), walks out, and tosses a curb bag into the boss's truck (pays the boss).</item>
/// <item>t 60: bot-3 tosses a curb bag into its OWN truck (pays bot-3's business: the integration fix), t 70 drives it into
/// its garage, t 76 tips it (its cargo comes from the host).</item>
/// <item>t 135: the host hires bot-3 ($9/hr, accepted); t 180 bot-3 quits; t 190 the host fires bot-2.</item>
/// <item>t 200: broke drill: the host drains bot-2's business to $0 (a ledger charge), bot-2 hires bot-3 at $200/hr, misses
/// two paydays and bot-3 stays hired with an unpaid balance; bot-3 quits at t 310. t 320: the host hires bot-3 again; when the run ends the guests leave first, so bot-3
/// leaving ends that job with final pay (settled before it disconnects).</item>
/// </list>
/// Wages land every 50 s (an in-game hour) in between. Run: <c>run_bots.sh -n 3 -m 6.5 -p none --brain payroll</c>.
/// </summary>
public sealed class PayrollBotBrain : IBotBrain
{
    readonly HashSet<int> done = new HashSet<int>();
    int offersSeen;
    string role;
    TrashBag held;
    bool warned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("payroll", () => new PayrollBotBrain());

    public void Begin(BotContext c)
    {
        c.Stop();
        string l = c.Options.label ?? "";
        int colon = l.LastIndexOf(':');
        role = colon >= 0 ? l.Substring(colon + 1) : l;
        Payroll.OfferReceived += OnOffer;
        Payroll.Notice += OnNotice;
    }

    public void End(BotContext c)
    {
        Payroll.OfferReceived -= OnOffer;
        Payroll.Notice -= OnNotice;
        Report("end");
    }

    void OnOffer(ulong employer, int wageCents, string label)
    {
        offersSeen++;
        // bot-3 turns down its first offer; everyone takes the rest.
        bool accept = !(role == "bot-3" && offersSeen == 1);
        Log("offer", accept, ("from", label), ("wage", wageCents), ("n", offersSeen));
        Payroll.Answer(accept);
    }

    void OnNotice(bool ok, string text) => Log("notice", ok, ("text", text));

    bool Step(BotContext c, int at)
    {
        if (c.Elapsed < at || done.Contains(at)) return false;
        done.Add(at);
        return true;
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        var pc = c.Player != null ? c.Player.GetComponent<PlayerController>() : null;
        if (pc == null || !DepotRowWorld.Active || !Payroll.Available)
        {
            if (!warned && c.Elapsed > 40f) { warned = true; Log("no one world / payroll", false); }
            return;
        }
        if (Step(c, 50) || Step(c, 110) || Step(c, 170) || Step(c, 230) || Step(c, 290) || Step(c, 350)) Report("tick");
        if (c.IsHost) HostTick(c, pc);
        else if (role == "bot-2") Bot2Tick(c, pc);
        else if (role == "bot-3") Bot3Tick(c, pc);
    }

    // ------------------------------------------------------------------ host (bot-1)

    void HostTick(BotContext c, PlayerController pc)
    {
        var net = DepotRowNet.Instance;
        if (Step(c, 15))
        {
            // Fresh bot saves start broke: seed the host's business through the Ledger (so the books still balance).
            Ledger.Credit(500f, "Bot seed money", LedgerIds.WorkOrder);
            Log("seed money", GameSession.CashCents >= 50000, ("cash", GameSession.CashCents));
        }
        if (Step(c, 20))
        {
            int n = CurbTrashSpawner.DebugPutOut(8);
            Log("put out curbs", n > 0, ("bags", n));
        }
        if (Step(c, 25)) Hire("bot-2", 1200);
        if (Step(c, 30)) Hire("bot-3", 1500);
        if (Step(c, 40))
        {
            for (int i = 0; i < 3; i++)
                GameSession.AddBagToTruck(new BagContents { bagId = $"botpay/b{i}", foundAt = "Payroll Ln", bagMassKg = 2.5f, sackVariant = i % 3 });
            Log("host truck loaded", GameSession.TruckBags.Count >= 3, ("bags", GameSession.TruckBags.Count));
        }
        if (Step(c, 100)) { int n = CurbTrashSpawner.DebugPutOut(6); Log("put out curbs", n > 0, ("bags", n)); }
        if (Step(c, 135)) Hire("bot-3", 900);
        if (Step(c, 190))
        {
            ulong b2 = ClientOf("bot-2");
            Log("fire bot-2", Payroll.TryGetJobOf(b2, out _));
            Payroll.Fire(b2);
        }
        if (Step(c, 198))
        {
            // Broke drill: bot-2's business pays a "fine" that empties it (a normal ledger charge, so the books balance).
            if (Businesses.TryGetForClient(ClientOf("bot-2"), out var b) && b.cashCents > 0)
                using (Ledger.ToBusiness(b.Id)) Ledger.Charge(b.cashCents / 100f, "Bot: broke drill", LedgerIds.CityWorks);
            Log("drained bot-2", Businesses.TryGetForClient(ClientOf("bot-2"), out b) && b.cashCents == 0, ("cash", b.cashCents));
        }
        if (Step(c, 320)) Hire("bot-3", 1000);
        if (Step(c, 335)) Log("before leave", Payroll.TryGetJobOf(ClientOf("bot-3"), out var j3) && j3.employerClientId == Payroll.LocalClientId);
    }

    void Hire(string who, int wageCents)
    {
        ulong id = ClientOf(who);
        string why = id == ulong.MaxValue ? "not here" : Payroll.WhyNotHire(Payroll.LocalClientId, id);
        Log("hire " + who, why == null, ("wage", wageCents), ("why", why));
        if (id != ulong.MaxValue) Payroll.Offer(id, wageCents);
    }

    // ------------------------------------------------------------------ bot-2 (the host's employee, then a broke boss)

    void Bot2Tick(BotContext c, PlayerController pc)
    {
        var net = DepotRowNet.Instance;
        ulong me = Payroll.LocalClientId;
        int bossLot = net.LotOwnedBy(NetworkManager.ServerClientId);
        var bossTruck = bossLot >= 0 ? net.TruckOf(bossLot) : null;
        int myLot = net.LotOwnedBy(me);

        if (Step(c, 45))
        {
            Log("employee permissions", Payroll.IsEmployee(me),
                ("canWorkBoss", Permissions.CanWorkIn(me, bossLot)), ("canDriveBoss", bossTruck != null && Permissions.CanDrive(me, bossTruck)),
                ("canLaptopBoss", Permissions.CanUseLaptop(me, bossLot)), ("canWorkOwn", Permissions.CanWorkIn(me, myLot)));
            held = PickCurbBag(pc);
        }
        if (Step(c, 46) && bossTruck != null)
        {
            var h = bossTruck.GetComponentInChildren<TruckHopper>();
            DepotRowWorld.TeleportPlayer(pc, h.transform.position - bossTruck.transform.forward * 2f, pc.transform.rotation);
        }
        if (Step(c, 47) && held != null && bossTruck != null)
        {
            var h = bossTruck.GetComponentInChildren<TruckHopper>();
            WorldState.Instance?.RequestHopper(held, h); // a second after the walk-up: the host checks we're near the truck
            Log("curb bag into the boss's truck", h != null, ("business", h != null ? h.ResolveBusiness() : "-"));
        }
        if (Step(c, 48) && bossTruck != null)
        {
            DepotRowWorld.TeleportPlayer(pc, bossTruck.transform.position + bossTruck.transform.right * -2.5f, pc.transform.rotation);
            bossTruck.OnInteract(pc.gameObject);
        }
        if (Step(c, 51)) Log("driving the boss's truck", bossTruck != null && bossTruck.DriverClientId == me && pc.IsDriving);
        if (Step(c, 52)) Log("drive into the boss's garage", pc.IsDriving && DepotRowWorld.UseDoor(bossLot, false, pc.gameObject));
        if (Step(c, 56))
        {
            // TIP IT is the driver's now: an on-shift employee tips the boss's truck from its seat, in the boss's bay.
            if (bossTruck != null && bossTruck.IsLocalPlayerSeated)
            {
                var tipper = bossTruck.GetComponent<TruckTipper>();
                if (tipper != null) tipper.RequestDump();
                bossTruck.ForceExit();
            }
            Log("boss's truck in the bay", bossTruck != null && DepotRowLayout.InteriorAt(bossTruck.transform.position) == bossLot);
        }
        if (Step(c, 59))
        {
            Log("in the boss's shop", DepotRowWorld.LocalInterior == bossLot, ("canWorkHere", Permissions.LocalCanWorkAt(pc.transform.position)),
                ("canLaptopHere", Permissions.LocalCanUseLaptopAt(pc.transform.position)), ("canShelvesHere", Permissions.LocalCanManageAt(pc.transform.position)));
            // Sorting pay inside the boss's shop goes to the boss (Ledger.Redirect).
            Ledger.Credit(1.23f, "Bot sorted", LedgerIds.Recycle, pc.transform.position);
            Ledger.Charge(0.50f, "Bot contaminated", LedgerIds.Contamination, pc.transform.position);
            var def = ItemCatalog.InCategory(ItemCategory.Valuable);
            if (def.Count > 0)
            {
                var item = new ItemInstance { uid = "botkeep-" + role, defId = def[0].id, massKg = 0.1f, value = 12f, foundAt = "Payroll Ln" };
                Log("keep for the boss", PayrollNet.TryHandOverKept(item, pc.transform.position), ("def", def[0].id));
            }
        }
        if (Step(c, 63)) Log("tipped the boss's truck", bossTruck != null && DepotTruckCargo.LoadKg(bossTruck) < 0.01f);
        if (Step(c, 64)) Log("leave the boss's shop", DepotRowWorld.UseDoor(bossLot, true, pc.gameObject));
        // t 200: the host has drained this business; hire bot-3 at $200/hr and miss payday twice.
        if (Step(c, 205))
        {
            ulong b3 = ClientOf("bot-3");
            string why = Payroll.WhyNotHire(me, b3);
            Log("broke boss hires bot-3", why == null, ("why", why), ("cash", Businesses.Local.cashCents));
            Payroll.Offer(b3, 20000);
        }
    }

    // ------------------------------------------------------------------ bot-3 (guest owner; declines, then works, quits)

    void Bot3Tick(BotContext c, PlayerController pc)
    {
        var net = DepotRowNet.Instance;
        ulong me = Payroll.LocalClientId;
        int myLot = net.LotOwnedBy(me);
        var myTruck = myLot >= 0 ? net.TruckOf(myLot) : null;

        if (Step(c, 60)) held = PickCurbBag(pc);
        if (Step(c, 62) && myTruck != null)
        {
            var h = myTruck.GetComponentInChildren<TruckHopper>();
            DepotRowWorld.TeleportPlayer(pc, h.transform.position - myTruck.transform.forward * 2f, pc.transform.rotation);
        }
        if (Step(c, 63) && held != null && myTruck != null)
        {
            var h = myTruck.GetComponentInChildren<TruckHopper>();
            WorldState.Instance?.RequestHopper(held, h);
            Log("curb bag into my own truck", h != null && h.ResolveBusiness() == Businesses.LocalId, ("business", h != null ? h.ResolveBusiness() : "-"),
                ("mine", Businesses.LocalId));
        }
        if (Step(c, 68) && myTruck != null)
        {
            DepotRowWorld.TeleportPlayer(pc, myTruck.transform.position + myTruck.transform.right * -2.5f, pc.transform.rotation);
            myTruck.OnInteract(pc.gameObject);
        }
        if (Step(c, 71)) Log("drive into my garage", pc.IsDriving && DepotRowWorld.UseDoor(myLot, false, pc.gameObject));
        if (Step(c, 75) && myTruck != null)
        {
            // Tip from the driver's seat (hold G in game), then get out. A full load takes 10 s (3 s with the Auto-Tipper).
            float kg = DepotTruckCargo.LoadKg(myTruck);
            bool local = DepotTruckCargo.IsLocal(myTruck);
            bool seated = myTruck.IsLocalPlayerSeated;
            var tipper = myTruck.GetComponent<TruckTipper>();
            if (seated && tipper != null) tipper.RequestDump();
            Log("requested own truck tip", kg > 0f && seated && tipper != null, ("kgBefore", kg), ("local", local));
        }
        if (Step(c, 77) && myTruck != null && myTruck.IsLocalPlayerSeated) myTruck.ForceExit();
        if (Step(c, 89)) Log("tipped my own truck", myTruck != null && DepotTruckCargo.LoadKg(myTruck) < 0.01f);
        if (Step(c, 90)) Log("leave my garage", DepotRowWorld.UseDoor(myLot, true, pc.gameObject));
        if (Step(c, 180))
        {
            Log("quit", Payroll.TryGetJobOf(me, out _));
            Payroll.Quit();
        }
        if (Step(c, 310))
        {
            bool stillHired = Payroll.TryGetJobOf(me, out var job) && job.unpaidCents >= 40000;
            Log("stays hired after missed pay", stillHired, ("unpaid", stillHired ? job.unpaidCents : 0));
            if (stillHired) Payroll.Quit();
        }
    }

    // ------------------------------------------------------------------ helpers

    TrashBag PickCurbBag(PlayerController pc)
    {
        foreach (var stop in PickupStop.All)
        {
            if (stop == null) continue;
            foreach (var b in stop.SpawnedBags)
                if (b != null && b.State == CarryState.Resting && b.IsCurbBag)
                {
                    Log("found a curb bag", true, ("bag", b.Contents != null ? b.Contents.bagId : "?"));
                    return b;
                }
        }
        Log("found a curb bag", false);
        return null;
    }

    static ulong ClientOf(string who)
    {
        foreach (var b in Businesses.All)
            if (b.IsValid && (b.OwnerName == who || b.OwnerName.EndsWith(who, StringComparison.Ordinal) || b.ownerKey.ToString().EndsWith(who, StringComparison.Ordinal)))
                return b.ownerClientId;
        return ulong.MaxValue;
    }

    void Report(string phase)
    {
        var d = new Dictionary<string, object>
        {
            { "phase", phase }, { "role", role }, { "cashCents", GameSession.CashCents }, { "business", GameSession.BusinessId },
            { "hours", Math.Round(GameClock.TotalHours, 2) }, { "inventory", GameSession.Inventory.Count },
        };
        if (Payroll.TryGetLocalJob(out var j)) { d["employer"] = j.employerClientId; d["wage"] = j.wageCents; d["paid"] = j.paidCents; d["unpaid"] = j.unpaidCents; }
        d["crew"] = Payroll.EmployeesOf(Payroll.LocalClientId).Count;
        NetAudit.Event("botpay", d);
    }

    void Log(string phase, bool? ok, params (string k, object v)[] extra)
    {
        var d = new Dictionary<string, object> { { "phase", phase }, { "role", role }, { "ok", ok.HasValue ? (object)ok.Value : "n/a" } };
        foreach (var (k, v) in extra) d[k] = v;
        NetAudit.Event("botpay", d);
        Debug.Log($"[PayrollBot] {role} {phase}: {(ok.HasValue ? (ok.Value ? "OK" : "FAIL") : "-")}");
    }
}

/// <summary>
/// <c>-botbrain payrollguest</c> (N4, for an Editor host + one bot guest): takes every job offer; once a job it held ends
/// (fired / quit), it waits 8 s and offers the HOST a job at $12/hr (so the Editor sees the offer card and the employee's HUD
/// line). Then idles. Everything goes to the NetAudit log (<c>kind = botpay</c>).
/// </summary>
public sealed class PayrollGuestBotBrain : IBotBrain
{
    bool wasEmployed, offered;
    float freeSince = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("payrollguest", () => new PayrollGuestBotBrain());

    public void Begin(BotContext c)
    {
        c.Stop();
        Payroll.OfferReceived += OnOffer;
        Payroll.Notice += OnNotice;
    }

    public void End(BotContext c)
    {
        Payroll.OfferReceived -= OnOffer;
        Payroll.Notice -= OnNotice;
    }

    static void OnOffer(ulong employer, int wageCents, string label)
    {
        NetAudit.Event("botpay", new Dictionary<string, object> { { "phase", "offer" }, { "from", label }, { "wage", wageCents } });
        Payroll.Answer(true);
    }

    static void OnNotice(bool ok, string text) =>
        NetAudit.Event("botpay", new Dictionary<string, object> { { "phase", "notice" }, { "ok", ok }, { "text", text } });

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (!Payroll.Available) return;
        bool employed = Payroll.TryGetLocalJob(out _);
        if (employed) { wasEmployed = true; freeSince = -1f; return; }
        if (!wasEmployed || offered) return;
        if (freeSince < 0f) freeSince = c.Elapsed;
        if (c.Elapsed - freeSince < 8f) return;
        offered = true;
        ulong host = NetworkManager.ServerClientId;
        string why = Payroll.WhyNotHire(Payroll.LocalClientId, host);
        NetAudit.Event("botpay", new Dictionary<string, object> { { "phase", "hire host" }, { "why", why } });
        if (why == null) Payroll.Offer(host, Payroll.DefaultWageCents);
    }
}

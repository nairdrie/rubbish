using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Two-bot regression for the guest sorting station. The guest creates a bag in its own depot, waits for the host to
/// take authority at rest (the guest's copy becomes kinematic), then uses the actual R fetch, slam, E pickup and bin
/// path. This catches the old "Pile's empty!" failure that a solo or host-only test cannot see.
/// </summary>
public sealed class SortStationNetBot : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        BotBrains.Register("sortnet", () => new SortStationNetBot(false));
        BotBrains.Register("sortnetemployee", () => new SortStationNetBot(true));
        BotBrains.Register("sortnethostemployee", () => new SortStationNetBot(true, true));
    }

    readonly bool employeeMode;
    readonly bool hostEmployer;
    string EmployeeRole => hostEmployer ? "bot-2" : "bot-3";
    string EmployerRole => hostEmployer ? "bot-1" : "bot-2";
    string role;
    ulong employeeId = ulong.MaxValue;
    bool employerSpawned, hired, offerAccepted, revocationIssued, revokedExited, reentryRejected;
    float lastOfferAt = -99f;
    bool employerTimeoutLogged;

    SortStationNetBot(bool employeeMode, bool hostEmployer = false)
    {
        this.employeeMode = employeeMode;
        this.hostEmployer = hostEmployer;
    }

    int stage;
    float stageAt;
    int lot = -1;
    string bagId, itemId;
    SortStation station;
    TrashBag bag;
    LooseItem item;
    bool remoteRest, fetched, held, ripped, itemSelected, earlyBlocked, binned, deliveryConfirmed;
    int revertsAtBegin;
    bool hostRest, hostTake, hostHeld, hostGone;
    readonly Dictionary<uint, HandsPhase> hostPhases = new Dictionary<uint, HandsPhase>();
    readonly Dictionary<uint, NetCarry> hostSeen = new Dictionary<uint, NetCarry>();

    public void Begin(BotContext c)
    {
        role = c.Options.label ?? "";
        revertsAtBegin = NetCarry.Reverts;
        if (employeeMode) Payroll.OfferReceived += OnOffer;
    }

    void OnOffer(ulong employer, int wageCents, string label)
    {
        if (!employeeMode || !role.EndsWith(EmployeeRole, StringComparison.Ordinal)) return;
        offerAccepted = true;
        Log("offer-accepted", ("employer", employer), ("wage", wageCents), ("label", label));
        Payroll.Answer(true);
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        var net = HandsNet.Instance;
        if (c.IsHost)
        {
            if (employeeMode && hostEmployer && c.Player != null && DepotRowWorld.Active && c.Elapsed >= 6f) EmployerTick(c, net);
            ObserveHost(net);
            return;
        }
        if (c.Player == null || !DepotRowWorld.Active || c.Elapsed < 6f || stage == 99) return;
        if (employeeMode && !hostEmployer && role.EndsWith(EmployerRole, StringComparison.Ordinal)) { EmployerTick(c, net); return; }
        if (employeeMode && !role.EndsWith(EmployeeRole, StringComparison.Ordinal)) return;
        var carrier = c.Player.GetComponent<TrashCarrier>();
        if (net == null || carrier == null) return;
        if (hostEmployer && stage == 6)
        {
            if (!Payroll.TryGetLocalJob(out _) && station != null && !station.IsActive)
            {
                revokedExited = true;
                Log("revoked-exit", ("exited", true), ("formerEmployerLot", lot));
                stage = 7;
                stageAt = c.Elapsed;
            }
            else if (c.Elapsed - stageAt > 30f) Fail("fired employee stayed at sort station");
            return;
        }
        if (hostEmployer && stage == 7)
        {
            if (c.Elapsed - stageAt < 0.6f) return; // clear the station's ordinary post-exit cooldown
            reentryRejected = station != null && !station.Enter();
            Log("revoked-reentry", ("rejected", reentryRejected), ("canWork", station != null && Permissions.LocalCanWorkAt(station.standPoint.position)));
            if (!reentryRejected) Fail("fired employee re-entered station");
            else stage = 99;
            return;
        }
        lot = employeeMode && Payroll.TryGetLocalJob(out var job) ? Payroll.EmployerLot(job) : DepotRowWorld.LocalLot;
        if (employeeMode && (!offerAccepted || !Payroll.TryGetLocalJob(out _) || lot == DepotRowWorld.LocalLot)) return;
        if (lot < 0) return;

        switch (stage)
        {
            case 0:
                if (employeeMode && !Permissions.CanWorkIn(net.LocalId, lot))
                {
                    Fail("hired employee cannot work in employer lot");
                    break;
                }
                if (DepotRowWorld.UseDoor(lot, false, c.Player.gameObject))
                {
                    Log("enter", ("employee", employeeMode), ("lot", lot), ("ownLot", DepotRowWorld.LocalLot),
                        ("canWork", Permissions.CanWorkIn(net.LocalId, lot)));
                    stage = 1;
                    stageAt = c.Elapsed;
                }
                else if (c.Elapsed > 30f) Fail("employee could not enter employer depot");
                break;

            case 1:
            {
                var depot = DepotRowScene.Lot(lot);
                if (DepotRowWorld.LocalInterior != lot || depot == null || !depot.InteriorOn || depot.interior == null)
                {
                    if (c.Elapsed - stageAt > 15f) Fail("employer interior stayed disabled on employee machine");
                    break;
                }
                station = depot.interior.GetComponentInChildren<SortStation>(true);
                if (station == null || station.compactor == null || station.table == null)
                {
                    if (c.Elapsed - stageAt > 15f) Fail("employer sorting station parts missing");
                    break;
                }
                if (employeeMode)
                {
                    Log("station-ready", ("lot", lot), ("ownLot", DepotRowWorld.LocalLot),
                        ("canWork", Permissions.LocalCanWorkAt(station.transform.position)));
                    stage = 2;
                    stageAt = c.Elapsed;
                    break;
                }
                bagId = $"sortnet/{net.LocalId}/{Time.frameCount}#tell=none";
                itemId = bagId + "/paper";
                var contents = new BagContents { bagId = bagId, foundAt = "sortnet", bagMassKg = 0.2f };
                contents.items.Add(new ItemInstance { uid = itemId, defId = "newspaper", massKg = 0.4f, condition = 1f, foundAt = "sortnet" });
                bag = TrashBag.Spawn(contents, station.compactor.pileCenter + Vector3.up * 0.2f, Quaternion.identity);
                Log("spawn", ("lot", lot), ("bag", bagId), ("pos", bag != null ? bag.transform.position.ToString("F2") : "none"));
                stage = 2;
                stageAt = c.Elapsed;
                break;
            }

            case 2:
            {
                if (employeeMode && bag == null)
                {
                    foreach (var peer in net.Items.Values)
                    {
                        if (peer == null || !peer.Desc.key.ToString().StartsWith("sortnetemployee/", StringComparison.Ordinal)) continue;
                        if (peer.Item is TrashBag found && DepotRowLayout.InteriorAt(found.transform.position) == lot)
                        {
                            bag = found;
                            bagId = found.Contents != null ? found.Contents.bagId : peer.Desc.key.ToString();
                            itemId = bagId + "/paper";
                            break;
                        }
                    }
                }
                if (employeeMode && bag == null) { if (c.Elapsed - stageAt > 20f) Fail("employer bag not replicated to employee"); break; }
                if (bag == null) { Fail("bag vanished before fetch"); break; }
                var nc = bag.GetComponent<NetCarry>();
                remoteRest = nc != null && nc.St.Phase == HandsPhase.Rest && bag.NetRemote && bag.Body != null && bag.Body.isKinematic;
                if (!remoteRest) { if (c.Elapsed - stageAt > 20f) Fail("guest bag never became a remote Rest copy"); break; }
                // No "next bag" key any more: grab the bag like a player would and carry it to the table.
                HandsBots.Teleport(c.Player.gameObject, bag.transform.position + Vector3.back * 0.6f, Vector3.forward);
                fetched = carrier.PickUp(bag);
                HandsBots.Teleport(c.Player.gameObject, station.standPoint.position, station.standPoint.forward);
                if (!station.Enter()) { Fail("could not enter station"); break; }
                Log("fetch", ("employee", employeeMode), ("lot", lot), ("canWork", Permissions.LocalCanWorkAt(station.transform.position)),
                    ("remoteRest", remoteRest), ("selected", fetched), ("localSortMode", SortStation.LocalInSortMode),
                    ("bodyKinematic", bag.Body.isKinematic), ("netPhase", nc.St.Phase.ToString()));
                if (!fetched) { Fail("could not grab the bag in guest depot"); break; }
                stage = 3;
                stageAt = c.Elapsed;
                break;
            }

            case 3:
                if (carrier.Held == bag)
                {
                    held = true;
                    var nc = bag.GetComponent<NetCarry>();
                    // Wait for the host's Held acknowledgement before the scripted slam. This proves the R fetch
                    // handed authority over cleanly, and avoids a test-only release during the heave window.
                    if (nc == null || nc.PendingGrab || nc.HostSt.Phase != HandsPhase.Held || nc.HostSt.holder != net.LocalId)
                    {
                        if (c.Elapsed - stageAt > 6f) Fail("host did not confirm fetched bag in guest hands");
                        break;
                    }
                    station.PrimaryAction(); // A held bag aimed away from the hopper slams onto this station's table.
                    stage = 4;
                    stageAt = c.Elapsed;
                }
                else if (c.Elapsed - stageAt > 6f) Fail("fetched bag never reached guest hands");
                break;

            case 4:
            {
                ripped = bag == null || bag.State == CarryState.Consumed;
                foreach (var li in LooseItem.All)
                    if (li != null && li.Item != null && li.Item.uid == itemId) { item = li; break; }
                var itemNet = item != null ? item.GetComponent<NetCarry>() : null;
                if (item != null && item.State == CarryState.Resting && itemNet != null &&
                    itemNet.HostSt.Phase == HandsPhase.Rest && item.NetRemote && carrier.Held == null)
                {
                    station.LookAt(item.WorldCenter);
                    station.PrimaryAction();
                    itemSelected = carrier.Held == item;
                    if (itemSelected)
                    {
                        var paper = PaperBin();
                        if (paper == null) { Fail("paper bin missing"); break; }
                        station.LookAt(paper.TossTargetPosition);
                        bool pending = itemNet.PendingGrab;
                        station.PrimaryAction(); // intentionally fast second click, while the host has not confirmed E
                        earlyBlocked = pending && carrier.Held == item;
                        Log("early-release", ("pending", pending), ("blocked", earlyBlocked),
                            ("target", station.DescribeTarget()), ("reverts", NetCarry.Reverts - revertsAtBegin));
                        if (!earlyBlocked) { Fail("unconfirmed item left hands during fast bin click"); break; }
                        stage = 5;
                        stageAt = c.Elapsed;
                    }
                }
                if (c.Elapsed - stageAt > 12f) Fail("ripped item could not be picked up at station");
                break;
            }

            case 5:
            {
                var paper = PaperBin();
                if (paper == null) { Fail("paper bin missing"); break; }
                var itemNet = item != null ? item.GetComponent<NetCarry>() : null;
                if (itemNet == null || itemNet.PendingGrab || itemNet.HostSt.Phase != HandsPhase.Held || itemNet.HostSt.holder != net.LocalId)
                {
                    if (c.Elapsed - stageAt > 6f) Fail("host did not confirm paper in employee hands");
                    break;
                }
                station.LookAt(paper.TossTargetPosition);
                station.PrimaryAction();
                binned = carrier.Held == null;
                if (!binned) { Fail("confirmed paper did not enter bin"); break; }
                stage = 8;
                stageAt = c.Elapsed;
                break;
            }

            case 8:
                if (c.Elapsed - stageAt < 0.8f) break; // let the receiver's arc and the host's Gone response finish
                deliveryConfirmed = item == null || item.State == CarryState.Consumed;
                Log("result", ("remoteRest", remoteRest), ("fetched", fetched), ("held", held), ("ripped", ripped),
                    ("itemSelected", itemSelected), ("earlyBlocked", earlyBlocked), ("binned", binned),
                    ("deliveryConfirmed", deliveryConfirmed), ("reverts", NetCarry.Reverts - revertsAtBegin),
                    ("pass", remoteRest && fetched && held && ripped && itemSelected && earlyBlocked && binned &&
                        deliveryConfirmed && NetCarry.Reverts == revertsAtBegin));
                if (!deliveryConfirmed || NetCarry.Reverts != revertsAtBegin) { Fail("paper delivery not confirmed cleanly"); break; }
                if (hostEmployer) { stage = 6; stageAt = c.Elapsed; } // stay at the station until fired; it must eject us
                else { stage = 99; station.Exit(); }
                break;
        }
    }

    public void End(BotContext c)
    {
        if (employeeMode) Payroll.OfferReceived -= OnOffer;
        if (station != null && station.IsActive) station.Exit();
        if (c.IsHost)
            Log("host-sync", ("rest", hostRest), ("take", hostTake), ("held", hostHeld), ("receivedGone", hostGone),
                ("hired", hired), ("spawned", employerSpawned), ("fired", revocationIssued),
                ("pass", hostRest && hostTake && hostHeld && hostGone &&
                    (!hostEmployer || hired && employerSpawned && revocationIssued)));
        else if (employeeMode && role.EndsWith(EmployerRole, StringComparison.Ordinal))
            Log("employer-end", ("hired", hired), ("spawned", employerSpawned), ("pass", hired && employerSpawned));
        else
            Log("end", ("remoteRest", remoteRest), ("fetched", fetched), ("held", held), ("ripped", ripped),
                ("itemSelected", itemSelected), ("earlyBlocked", earlyBlocked), ("binned", binned),
                ("deliveryConfirmed", deliveryConfirmed), ("reverts", NetCarry.Reverts - revertsAtBegin), ("employee", employeeMode),
                ("offerAccepted", offerAccepted), ("lot", lot), ("revokedExited", revokedExited), ("reentryRejected", reentryRejected),
                ("pass", remoteRest && fetched && held && ripped && itemSelected && earlyBlocked && binned && deliveryConfirmed &&
                    NetCarry.Reverts == revertsAtBegin && (!employeeMode || offerAccepted) && (!hostEmployer || revokedExited && reentryRejected)));
    }

    SortBin PaperBin()
    {
        if (station == null || station.bins == null) return null;
        foreach (var bin in station.bins)
            if (bin != null && bin.category == ItemCategory.Paper) return bin;
        return null;
    }

    void EmployerTick(BotContext c, HandsNet net)
    {
        if (net == null || !Payroll.Available) return;
        lot = DepotRowWorld.LocalLot;
        if (lot < 0) return;
        if (employeeId == ulong.MaxValue)
        {
            foreach (var b in Businesses.All)
                if (b.IsValid && (b.OwnerName == EmployeeRole || b.ownerKey.ToString().EndsWith(EmployeeRole, StringComparison.Ordinal)))
                { employeeId = b.ownerClientId; break; }
        }
        if (employeeId == ulong.MaxValue)
        {
            if (c.Elapsed > 30f && !employerTimeoutLogged)
            {
                employerTimeoutLogged = true;
                Log("employer-timeout", ("why", "employee business not found"));
            }
            return;
        }
        if (revocationIssued) return;
        if (!Payroll.TryGetJobOf(employeeId, out var job) || job.employerClientId != net.LocalId)
        {
            if (c.Elapsed - lastOfferAt > 33f && Payroll.WhyNotHire(net.LocalId, employeeId) == null)
            {
                lastOfferAt = c.Elapsed;
                Payroll.Offer(employeeId, Payroll.MinWageCents);
                Log("offer", ("employer", net.LocalId), ("employee", employeeId), ("lot", lot));
            }
            if (c.Elapsed > 45f && !employerTimeoutLogged)
            {
                employerTimeoutLogged = true;
                Log("employer-timeout", ("why", Payroll.WhyNotHire(net.LocalId, employeeId) ?? "offer not accepted"),
                    ("employee", employeeId), ("lot", lot));
            }
            return;
        }
        hired = Permissions.CanWorkIn(employeeId, lot);
        if (hostEmployer && hired && employerSpawned && c.Elapsed >= 25f)
        {
            revocationIssued = true;
            Payroll.Fire(employeeId);
            Log("employer-fire", ("employee", employeeId), ("lot", lot));
            return;
        }
        if (!hired || employerSpawned) return;
        var depot = DepotRowScene.Lot(lot);
        var s = depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<SortStation>(true) : null;
        if (s == null || s.compactor == null || !depot.InteriorOn) return;
        bagId = $"sortnetemployee/{net.LocalId}/{Time.frameCount}#tell=none";
        itemId = bagId + "/paper";
        var contents = new BagContents { bagId = bagId, foundAt = "employer depot", bagMassKg = 0.2f };
        contents.items.Add(new ItemInstance { uid = itemId, defId = "newspaper", massKg = 0.4f, condition = 1f, foundAt = "employer depot" });
        bag = TrashBag.Spawn(contents, s.compactor.pileCenter + Vector3.up * 0.2f, Quaternion.identity);
        if (hostEmployer && bag != null) net.HostPublishNow(bag);
        employerSpawned = bag != null;
        Log("employer-spawn", ("hired", hired), ("spawned", employerSpawned), ("lot", lot),
            ("employee", employeeId), ("bag", bagId));
    }

    void ObserveHost(HandsNet net)
    {
        if (net == null) return;
        foreach (var nc in net.Items.Values)
        {
            if (nc == null || !nc.Desc.key.ToString().StartsWith(employeeMode ? "sortnetemployee/" : "sortnet/", StringComparison.Ordinal)) continue;
            hostSeen[nc.Id] = nc;
            var phase = nc.St.Phase;
            if (phase == HandsPhase.Rest) hostRest = true;
            if (hostPhases.TryGetValue(nc.Id, out var before) && before == HandsPhase.Rest && phase == HandsPhase.Flight)
                hostTake = true;
            if (phase == HandsPhase.Held && nc.St.holder != net.LocalId) hostHeld = true;
            hostPhases[nc.Id] = phase;
        }
        // ConsumedRpc removes the carry from Items immediately. Retain the observed component for its short
        // shrink window so this proves the host accepted the guest's bin result, not just a local gulp.
        foreach (var nc in hostSeen.Values)
            if (nc != null && nc.HostSt.Phase == HandsPhase.Gone &&
                (nc.HostSt.flags & CarryNetState.FlagReceived) != 0)
                hostGone = true;
    }

    void Fail(string why)
    {
        Log("fail", ("why", why), ("stage", stage), ("target", station != null ? station.DescribeTarget() : "none"));
        stage = 99;
        if (station != null && station.IsActive) station.Exit();
    }

    static void Log(string phase, params (string key, object value)[] fields)
    {
        var data = new Dictionary<string, object> { { "phase", phase } };
        foreach (var field in fields) data[field.key] = field.value;
        NetAudit.Event("sortnet", data);
    }
}

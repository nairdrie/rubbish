using System.Collections.Generic;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// F5 bot brain <c>f5</c> (Docs/Multiplayer.md §14.5): every bot runs curb bags into its OWN business's truck (so the Big
/// Cleanup board has several businesses scoring, and bots working the same streets poach from each other), and every
/// ~35 s takes a freshly grabbed bag on a "dog run": it stands by the nearest yard dog until the dog snatches it, then either
/// catches the dog or waits for the drop, picks the bag back up and delivers it (bags must be conserved). The host also keeps
/// the curbs stocked and trucks emptied. Pair with host args <c>-f5cleanup START,RUN</c> and <c>-f5weather SEC</c>.
/// </summary>
public sealed class F5Brain : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("f5", () => new F5Brain());

    float nextAct = 8f, nextReport = 20f, nextSupply = 3f, nextDogRun = 25f;
    int picks, tosses, dogRuns, snatchedFromMe, caught, recovered, poachedSeen, trophies;
    TrashBag deferredBag;
    float deferredAt;
    TruckHopper deferredHopper;
    float deferredTossAt;

    enum DogRun { None, Waiting, Chasing, Recover }
    DogRun run;
    float runT;
    int runDog = -1;
    Carryable lost;
    bool tryCatch;

    public void Begin(BotContext c)
    {
        c.Stop();
        PoachingNet.Poached += OnPoached;
        BigCleanupNet.TrophyReceived += OnTrophy;
    }

    public void End(BotContext c)
    {
        c.Stop();
        PoachingNet.Poached -= OnPoached;
        BigCleanupNet.TrophyReceived -= OnTrophy;
        Report(c, "end");
    }

    void OnPoached(PoachingNet.PoachInfo p) => poachedSeen++;
    void OnTrophy(ItemInstance it) => trophies++;

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        float now = c.Elapsed;
        if (c.IsHost) HostChores(now);
        if (now >= nextReport)
        {
            nextReport = now + 30f;
            Report(c, "tick");
        }
        var carrier = TrashCarrier.Local;
        if (c.Player == null || carrier == null) return;
        StepDeferred(c, carrier);
        if (run != DogRun.None) { StepDogRun(c, carrier, now); return; }
        if (now < nextAct) return;
        nextAct = now + c.Range(2.5f, 4f);
        if (carrier.IsCarrying)
        {
            if (carrier.Held.NetId != 0 && !(TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier))) return;
            if (now >= nextDogRun && carrier.Held is TrashBag && StartDogRun(c)) return;
            TossOwn(c, carrier);
            return;
        }
        GrabCurbBag(c);
    }

    // ------------------------------------------------------------------ curb → own truck

    void StepDeferred(BotContext c, TrashCarrier carrier)
    {
        if (deferredHopper != null && c.Elapsed >= deferredTossAt)
        {
            var h = deferredHopper;
            deferredHopper = null;
            if (carrier.IsCarrying && h != null && h.CanAccept(carrier.Held, out _) &&
                (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier)))
            {
                carrier.TossInto(h);
                tosses++;
            }
        }
        if (deferredBag == null || c.Elapsed < deferredAt) return;
        var b = deferredBag;
        deferredBag = null;
        if (carrier.IsCarrying || b == null || b.State != CarryState.Resting) return;
        if (carrier.PickUp(b)) picks++;
    }

    void GrabCurbBag(BotContext c)
    {
        var list = new List<TrashBag>();
        foreach (var stop in PickupStop.All)
        {
            if (stop == null) continue;
            foreach (var b in stop.SpawnedBags)
                if (b != null && b.State == CarryState.Resting && !b.IsInScriptedFlight && b.IsCurbBag) list.Add(b);
        }
        if (list.Count == 0) return;
        list.Sort((a, b) => string.CompareOrdinal(a.Contents.bagId, b.Contents.bagId));
        int me = HandsNet.Instance != null ? (int)HandsNet.Instance.LocalId : 0;
        // Bots favour neighbouring bags (same streets): poaching happens.
        var bag = list[(me * 3 + (int)(c.Value() * 6f)) % list.Count];
        Vector3 back = bag.SourceStop != null ? bag.SourceStop.transform.forward : Vector3.forward;
        HandsBots.Teleport(c.Player.gameObject, bag.transform.position - back * 1.1f, back);
        deferredBag = bag;
        deferredAt = c.Elapsed + 0.6f;
    }

    static TruckHopper OwnHopper()
    {
        var net = DepotRowNet.Instance;
        var hn = HandsNet.Instance;
        if (net != null && hn != null)
        {
            int lot = net.LotOwnedBy(hn.LocalId);
            var t = lot >= 0 ? net.TruckOf(lot) : null;
            var h = t != null ? t.GetComponentInChildren<TruckHopper>() : null;
            if (h != null && DepotRowLayout.InteriorAt(h.transform.position) < 0) return h;
        }
        TruckHopper best = null;
        foreach (var r in CarryReceivers.All)
            if (r is TruckHopper th && th != null && th.isActiveAndEnabled && DepotRowLayout.InteriorAt(th.transform.position) < 0) { best = th; break; }
        return best;
    }

    void TossOwn(BotContext c, TrashCarrier carrier)
    {
        var held = carrier.Held;
        var hopper = OwnHopper();
        if (hopper != null && hopper.CanAccept(held, out _))
        {
            Vector3 back = hopper.TossTargetPosition - hopper.transform.forward * 2.2f;
            HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(new Vector3(back.x, hopper.TossTargetPosition.y, back.z)), hopper.transform.forward);
            deferredHopper = hopper;
            deferredTossAt = c.Elapsed + 0.6f;
            return;
        }
        if (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier)) carrier.Drop();
    }

    // ------------------------------------------------------------------ dog runs

    bool StartDogRun(BotContext c)
    {
        var y = YardDogs.Instance;
        if (y == null || y.Dogs.Count == 0) return false;
        Vector3 me = c.Player.transform.position;
        YardDogs.Dog best = null;
        float bestD = float.MaxValue;
        foreach (var d in y.Dogs)
        {
            if (d.walking) continue;
            float dd = (d.anchor - me).sqrMagnitude;
            if (dd < bestD) { bestD = dd; best = d; }
        }
        if (best == null) return false;
        nextDogRun = c.Elapsed + c.Range(30f, 40f);
        dogRuns++;
        runDog = best.idx;
        tryCatch = (dogRuns & 1) == 1;
        Vector3 toCurb = best.curb - best.anchor; toCurb.y = 0f;
        Vector3 spot = best.anchor + (toCurb.sqrMagnitude > 0.01f ? toCurb.normalized : Vector3.forward) * 5f;
        HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(spot), best.anchor - spot);
        run = DogRun.Waiting;
        runT = 0f;
        lost = TrashCarrier.Local != null ? TrashCarrier.Local.Held : null;
        NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "dog-run" }, { "dog", runDog }, { "catch", tryCatch } });
        return true;
    }

    YardDogs.Dog RunDog()
    {
        var y = YardDogs.Instance;
        if (y == null) return null;
        foreach (var d in y.Dogs) if (d.idx == runDog) return d;
        return null;
    }

    void StepDogRun(BotContext c, TrashCarrier carrier, float now)
    {
        runT += Time.deltaTime;
        var d = RunDog();
        switch (run)
        {
            case DogRun.Waiting:
                if (!carrier.IsCarrying)
                {
                    // The dog has it (or something else happened): give chase.
                    snatchedFromMe++;
                    run = DogRun.Chasing;
                    runT = 0f;
                    NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "snatched" }, { "dog", runDog } });
                }
                else if (runT > 10f) { run = DogRun.None; NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "dog-ignored" }, { "dog", runDog } }); }
                break;
            case DogRun.Chasing:
                if (tryCatch && runT > 1.6f && runT < 1.8f && d != null)
                {
                    HandsBots.Teleport(c.Player.gameObject, d.pos, d.pos - c.Player.transform.position);
                    caught++;
                }
                if (runT > 7.5f) { run = DogRun.Recover; runT = 0f; }
                break;
            case DogRun.Recover:
                if (carrier.IsCarrying) { recovered++; run = DogRun.None; break; }
                if (lost != null && lost.State != CarryState.Consumed && lost.Holder == null && runT > 0.5f && runT < 0.7f)
                {
                    HandsBots.Teleport(c.Player.gameObject, lost.transform.position - Vector3.forward * 1f, Vector3.forward);
                    deferredAt = c.Elapsed + 0.6f;
                }
                if (lost != null && lost.State == CarryState.Resting && lost.Holder == null && runT > 1.4f && runT < 1.6f && carrier.PickUp(lost))
                    NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "recover-grab" }, { "dog", runDog } });
                if (runT > 6f) { run = DogRun.None; NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "recover-gave-up" }, { "dog", runDog } }); }
                break;
        }
    }

    // ------------------------------------------------------------------ host

    void HostChores(float now)
    {
        if (now < nextSupply) return;
        nextSupply = now + 5f;
        if (GameSession.CurbBags.Count < 14) CurbTrashSpawner.DebugPutOut(6);
        if (GameSession.TruckFill01 > 0.7f) GameSession.TakeAllTruckBags();
        foreach (var b in Businesses.All)
        {
            var cargo = Businesses.HostCargo(b.Id);
            if (cargo == null || !Businesses.TryGetTruckLoad(b.Id, out float kg, out float cap) || kg < cap * 0.7f) continue;
            cargo.Clear();
            Businesses.SetTruckLoad(b.Id, 0f, cap);
        }
    }

    void Report(BotContext c, string phase)
    {
        var n = BigCleanupNet.Instance;
        NetAudit.Event("botf5", new Dictionary<string, object>
        {
            { "phase", phase }, { "picks", picks }, { "tosses", tosses }, { "dogRuns", dogRuns }, { "snatched", snatchedFromMe },
            { "caught", caught }, { "recovered", recovered }, { "poachedSeen", poachedSeen }, { "trophies", trophies },
            { "weather", Weather.Describe() }, { "cleanup", n != null && n.IsSpawned ? n.Phase.ToString() : "-" },
            { "cleanupRate", System.Math.Round(CurbTrashSpawner.EventRateMul, 2) },
            { "dogSnatches", YardDogs.Snatches }, { "business", GameSession.BusinessId }, { "cashCents", GameSession.CashCents },
            { "windPushes", WeatherWind.TotalPushes },
        });
    }
}

/// <summary>
/// Focused F5 rider/dog acceptance probe. Unlike the regular f5 brain's bag-carrier runs, this puts an actual
/// network rider on a parked truck beside a cast yard dog and requires the dog to chase/nip that rider through F1.
/// The probe reports its own pass bit; use one bot so no other player can draw the dog away.
/// </summary>
public sealed class F5RiderDogBrain : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("f5riderdog", () => new F5RiderDogBrain());

    TruckControllerV2 truck;
    YardDogs.Dog dog;
    bool placed, boarded, nipped, sawHoldOn;
    float gripBefore, gripLowest;
    float placedAt, nextHitch;
    int nipDog = -1;

    public void Begin(BotContext c)
    {
        c.Stop();
        YardDogs.DogEvent += OnDog;
    }

    public void End(BotContext c)
    {
        YardDogs.DogEvent -= OnDog;
        if (truck != null) truck.InputOverride = null;
        NetAudit.Event("botf5", new Dictionary<string, object>
        {
            { "phase", "rider-dog-end" }, { "placed", placed }, { "boarded", boarded },
            { "nipped", nipped }, { "dog", nipDog }, { "holdOn", sawHoldOn },
            { "gripBefore", System.Math.Round(gripBefore, 1) }, { "gripLowest", System.Math.Round(gripLowest, 1) },
            { "pass", placed && boarded && nipped && (sawHoldOn || gripLowest <= gripBefore - 10f) },
        });
        c.Stop();
    }

    void OnDog(int index, string what, ulong client, Carryable item)
    {
        if (what != "rider-nip") return;
        nipped = true;
        nipDog = index;
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (c.Player == null || c.Elapsed < 12f) return;
        var rider = RideRider.LocalRider;
        if (rider == null) return;
        if (nipped)
        {
            sawHoldOn |= rider.HoldOnActive;
            gripLowest = Mathf.Min(gripLowest, rider.Grip);
            return;
        }
        if (!placed)
        {
            truck = RideBots.RideTruck();
            var yards = YardDogs.Instance;
            if (truck == null || yards == null || yards.Dogs.Count == 0) return;
            foreach (var d in yards.Dogs)
                if (!d.walking && d.chain >= YardDogs.ChainMin && DepotRowLayout.InteriorAt(d.anchor) < 0)
                { dog = d; break; }
            if (dog == null) return;
            Vector3 towardCurb = dog.curb - dog.anchor;
            towardCurb.y = 0f;
            if (towardCurb.sqrMagnitude < 0.1f) towardCurb = Vector3.forward;
            towardCurb.Normalize();
            Quaternion facing = Quaternion.LookRotation(towardCurb, Vector3.up);
            Vector3 desiredFoot = dog.anchor + towardCurb * 4f;
            Vector3 at = desiredFoot - facing * RideHolds.FeetMetres(RideHolds.RearLeft);
            if (Physics.Raycast(at + Vector3.up * 5f, Vector3.down, out var hit, 20f,
                    Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer), QueryTriggerInteraction.Ignore))
                at.y = hit.point.y + 0.15f;
            var body = truck.Body;
            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.position = at;
                body.rotation = facing;
            }
            var networkTransform = truck.GetComponent<NetworkTransform>();
            if (networkTransform != null && networkTransform.IsSpawned && networkTransform.CanCommitToTransform)
                networkTransform.Teleport(at, facing, truck.transform.localScale);
            else truck.transform.SetPositionAndRotation(at, facing);
            placed = true;
            placedAt = Time.time;
            NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "rider-dog-place" }, { "dog", dog.idx } });
            return;
        }
        if (!boarded && rider.IsRiding)
        {
            boarded = true;
            gripBefore = gripLowest = rider.Grip;
            NetAudit.Event("botf5", new Dictionary<string, object> { { "phase", "rider-dog-board" }, { "dog", dog.idx } });
        }
        if (!boarded && Time.time - placedAt >= 0.7f && Time.time >= nextHitch)
        {
            var hs = truck != null ? truck.GetComponentInChildren<HitchStep>(true) : null;
            if (hs == null || !rider.CanBoard) return;
            var step = RideHolds.PoseOn(truck.transform, RideHolds.RearLeft);
            HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(step.position - step.rotation * Vector3.forward * 0.8f), step.rotation * Vector3.forward);
            rider.Hitch(hs, RideHolds.RearLeft);
            nextHitch = Time.time + 1f;
        }
    }
}

/// <summary>
/// Focused host/solo weather probe: a real light loose item must be displaced by wind, then storm rain must make an
/// outdoor player position slippery and increase rider grip drain. This checks the physical effect, not just synced
/// weather labels. Run with one bot to leave the test item under one physics authority.
/// </summary>
public sealed class F5WeatherProbeBrain : IBotBrain
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("f5weatherprobe", () => new F5WeatherProbeBrain());

    LooseItem item;
    Vector3 start;
    float changedAt;
    int phase, pushesAt;
    bool windPass, rainPass, comfortPass, rainSpawned;
    float windMetres, slip, grip, rainMaxSlip, walkedAt;
    int flopsAt;

    public void Begin(BotContext c) => c.Stop();

    public void End(BotContext c)
    {
        NetAudit.Event("botf5", new Dictionary<string, object>
        {
            { "phase", "weather-probe-end" }, { "windPass", windPass }, { "windMetres", System.Math.Round(windMetres, 2) },
            { "rainPass", rainPass }, { "slip", System.Math.Round(slip, 2) }, { "grip", System.Math.Round(grip, 2) },
            { "rainPuddles", RainPuddles() }, { "comfortPass", comfortPass },
            { "pass", windPass && rainPass && comfortPass },
        });
        c.Stop();
    }

    static int RainPuddles()
    {
        int n = 0;
        foreach (var p in Puddles.All) if (p.kind == PuddleKind.Rain) n++;
        return n;
    }

    public void Tick(BotContext c, float dt)
    {
        c.Stop();
        if (!c.IsHost || c.Player == null || c.Elapsed < 12f || !WorldLifeRunner.IsAwake) return;
        if (phase == 0)
        {
            // Bots begin in their shop; move the probe outside so an indoor spawn cannot silently stall it.
            if (!Weather.IsOutdoors(c.Player.transform.position))
            {
                HandsBots.Teleport(c.Player.gameObject, HandsBots.Arena(), Vector3.forward);
                return;
            }
            ItemDef def = null;
            foreach (var candidate in ItemCatalog.All)
                if (candidate.category != ItemCategory.Valuable && candidate.minMass <= 0.3f)
                { def = candidate; break; }
            if (def == null) return;
            Vector3 at = c.Player.transform.position + new Vector3(0f, 1.6f, 1f);
            if (!Weather.IsOutdoors(at)) return;
            var instance = new ItemInstance { uid = "f5/weather-probe", defId = def.id, massKg = 0.2f, condition = 1f };
            item = LooseItem.Spawn(instance, at, Quaternion.identity, revealed: true);
            if (item == null || item.Body == null) return;
            item.Body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
            item.Body.linearVelocity = Vector3.zero;
            start = item.transform.position;
            pushesAt = WeatherWind.TotalPushes;
            if (!WeatherNet.DebugSet(WeatherKind.Windy, 100f)) return;
            changedAt = Time.time;
            phase = 1;
            return;
        }
        if (phase == 1 && Time.time - changedAt >= 18f)
        {
            Vector3 delta = item != null ? item.transform.position - start : Vector3.zero;
            delta.y = 0f;
            windMetres = delta.magnitude;
            windPass = item != null && windMetres >= 0.25f && WeatherWind.TotalPushes > pushesAt;
            NetAudit.Event("botf5", new Dictionary<string, object>
            {
                { "phase", "weather-wind" }, { "metres", System.Math.Round(windMetres, 2) },
                { "pushes", WeatherWind.TotalPushes - pushesAt }, { "pass", windPass },
            });
            WeatherNet.DebugSet(WeatherKind.Storm, 100f);
            changedAt = Time.time;
            phase = 2;
        }
        if (phase == 2 && Time.time - changedAt >= 46f)
        {
            Vector3 at = c.Player.transform.position;
            slip = Puddles.SlipAt(at);
            grip = Riding.GripDrainScale;
            rainPass = Weather.Kind == WeatherKind.Storm && Weather.IsOutdoors(at) &&
                       Weather.Wetness >= 0.95f && slip >= 0.4f && grip >= 1.5f && RainPuddles() > 0;
            NetAudit.Event("botf5", new Dictionary<string, object>
            {
                { "phase", "weather-rain" }, { "wet", System.Math.Round(Weather.Wetness, 2) },
                { "slip", System.Math.Round(slip, 2) }, { "grip", System.Math.Round(grip, 2) },
                { "rainPuddles", RainPuddles() }, { "pass", rainPass },
            });
            // Run through an ordinary rain puddle under a full storm. It should
            // remain walkable, and rain alone must never cause a juice-style flop.
            rainSpawned = Puddles.Spawn(at, 1.4f, PuddleKind.Rain, 30f);
            rainMaxSlip = Puddles.LocalSlipStrength(PuddleKind.Rain, 1f, 1f);
            walkedAt = c.walked;
            flopsAt = PlayerTells.Flops;
            changedAt = Time.time;
            phase = 3;
            return;
        }
        if (phase == 3)
        {
            if (c.Input != null) { c.Input.move = Vector2.up; c.Input.sprint = true; }
            if (Time.time - changedAt < 4f) return;
            float walked = c.walked - walkedAt;
            comfortPass = rainSpawned && rainMaxSlip < 0.5f && walked >= 1.5f && PlayerTells.Flops == flopsAt;
            NetAudit.Event("botf5", new Dictionary<string, object>
            {
                { "phase", "weather-comfort" }, { "rainPuddleSlip", System.Math.Round(rainMaxSlip, 3) },
                { "walked", System.Math.Round(walked, 2) }, { "flops", PlayerTells.Flops - flopsAt },
                { "pass", comfortPass },
            });
            phase = 4;
        }
    }
}

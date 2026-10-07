using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Focused, disposable F3 acceptance brain: each 18 s slot tests one previously unproven feature.</summary>
public static class F3ProbeBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("tells-extra", () => new Probe());

    static void Log(string op, params (string key, object value)[] fields)
    {
        var d = new Dictionary<string, object> { { "op", op } };
        foreach (var f in fields) d[f.key] = f.value;
        NetAudit.Event("f3probe", d);
    }

    sealed class Probe : IBotBrain
    {
        const string Over = "f3probe/over#tell=over";
        const string Plain = "f3probe/plain#tell=none";
        const string Mystery = "f3probe/mystery#tell=mystery";
        const string Pole = "f3probe/pole#tell=none";
        const string SoloHeavy = "f3probe/solo-heavy#tell=heavy";
        const int StandInBase = 60010;

        readonly HashSet<string> done = new HashSet<string>();
        readonly Dictionary<uint, string> keys = new Dictionary<uint, string>();
        readonly HashSet<string> burst = new HashSet<string>();
        int neighbourSplats;
        ulong neighbourShooter, neighbourTarget;
        int phase = -1;
        bool gearGranted;
        GameObject fence;
        TrashBag overBag, misfireBag;
        Vector3 poleBag, wheelAt;
        Vector3 dragFrom;
        float dryStiff, juiceStiff, rainStiff;
        float drySlip, juiceSlip, rainSlip;
        bool trailMade, wheelJuicePlaced;
        bool overPass, mysteryPass, crowdPass, polePass, misfirePass, juicePass, rainPass, soloDragPass;

        public void Begin(BotContext c)
        {
            c.Stop();
            HandsNet.StateApplied += OnState;
            BagTellsNet.FxPlayed += OnFx;
            Log("begin");
        }

        public void End(BotContext c)
        {
            c.Stop();
            HandsNet.StateApplied -= OnState;
            BagTellsNet.FxPlayed -= OnFx;
            if (fence != null) UnityEngine.Object.Destroy(fence);
            RemoveStandIns();
            Log("end", ("over", overPass), ("mystery", mysteryPass), ("crowd", crowdPass), ("pole", polePass),
                ("misfire", misfirePass), ("juice", juicePass), ("rain", rainPass), ("soloDrag", soloDragPass));
        }

        void OnState(CarryNetState s)
        {
            if (s.Phase != HandsPhase.Gone) return;
            string key = keys.TryGetValue(s.id, out var tracked) ? tracked : "";
            if (key.Length == 0)
            {
                var nc = HandsNet.Instance != null ? HandsNet.Instance.Get(s.id) : null;
                if (nc != null) key = nc.Desc.key.ToString();
            }
            if (!key.StartsWith("f3probe/", StringComparison.Ordinal)) return;
            if (s.Has(CarryNetState.FlagBurst)) burst.Add(key);
            Log("gone", ("key", key), ("burst", s.Has(CarryNetState.FlagBurst)), ("flags", s.flags));
        }

        void OnFx(byte kind, ulong who, ulong other, Vector3 a, Vector3 b)
        {
            if (kind != BagTellsNet.FxNeighbourSplat) return;
            neighbourSplats++;
            neighbourShooter = who;
            neighbourTarget = other;
            Log("misfire-neighbour-splat", ("shooter", (long)who), ("target", (long)other));
        }

        bool Once(string key) => done.Add(key);

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            var net = HandsNet.Instance;
            var carrier = TrashCarrier.Local;
            if (c.Player == null || carrier == null || net == null || !net.IsSpawned || c.Elapsed < 15f) return;
            if (!gearGranted)
            {
                gearGranted = true;
                GameSession.SetUpgradeLevel(ShopIds.BagCannon, 1);
                GameSession.SetUpgradeLevel(ShopIds.GrabberPole, 1);
            }
            double shifted = net.ServerNow - 15.0;
            if (shifted < 0.0) return;
            int p = (int)Math.Floor(shifted / 18.0);
            float t = (float)(shifted - p * 18.0);
            if (p != phase)
            {
                phase = p;
                done.Clear();
                if (fence != null) { UnityEngine.Object.Destroy(fence); fence = null; }
                if (p > 1) RemoveStandIns();
                if (carrier.IsCarrying && !carrier.ExternalControl) carrier.Drop();
                Log("phase", ("index", p));
            }
            var bodies = HandsBots.Sorted();
            int idx = bodies.IndexOf(PlayerBody.For(net.LocalId));
            Vector3 arena = HandsBots.Arena();
            switch (p)
            {
                case 0: OverStuffed(c, net, t, arena); break;
                case 1: MysteryCrowd(c, net, t, arena); break;
                case 2: PoleFence(c, net, carrier, t, idx, arena); break;
                case 3: CannonMisfire(c, net, carrier, t, idx, bodies); break;
                case 4: Traction(c, net, t); break;
                case 5: SoloDrag(c, net, carrier, t, idx, arena); break;
            }
        }

        void Track(HandsNet net, string key)
        {
            var nc = HandsBots.Find(net, key);
            if (nc != null) keys[nc.Id] = key;
        }

        static TrashBag Spawn(HandsNet net, string key, Vector3 at, BagContents contents = null)
        {
            var bag = TrashBag.Spawn(contents ?? new BagContents { bagId = key, foundAt = "F3 Probe", bagMassKg = 4f, sackVariant = 0 }, at, Quaternion.identity);
            if (bag != null) net.HostPublishNow(bag);
            return bag;
        }

        void OverStuffed(BotContext c, HandsNet net, float t, Vector3 arena)
        {
            Vector3 at = HandsBots.Ground(arena + new Vector3(0f, 0f, 15f));
            if (c.IsHost && t >= 0.8f && Once("spawn"))
            {
                overBag = Spawn(net, Over, at + new Vector3(-2f, 7f, 0f));
                Spawn(net, Plain, at + new Vector3(2f, 7f, 0f));
                var stuffed = ItemCatalog.RollBag("f3probe/content#tell=over", 42, 0.3f, "F3 Probe");
                var control = ItemCatalog.RollBag("f3probe/content#tell=none", 42, 0.3f, "F3 Probe");
                Log("over-contents", ("extra", stuffed.items.Count - control.items.Count), ("count", stuffed.items.Count));
            }
            Track(net, Over); Track(net, Plain);
            if (t >= 7f && Once("check"))
            {
                var plain = HandsBots.Find(net, Plain);
                bool intact = plain != null && plain.St.Phase != HandsPhase.Gone && plain.Item != null && !((TrashBag)plain.Item).HasBurst;
                bool didBurst = burst.Contains(Over) || c.IsHost && overBag is object && overBag.HasBurst;
                overPass = didBurst && intact;
                Log("over-check", ("burst", didBurst), ("plainIntact", intact), ("pass", overPass));
            }
            if (c.IsHost && t >= 14f && Once("cleanup")) Cleanup(net, Plain);
        }

        void MysteryCrowd(BotContext c, HandsNet net, float t, Vector3 arena)
        {
            Vector3 at = HandsBots.Ground(arena + new Vector3(0f, 0f, 14f));
            if (c.IsHost && t >= 0.6f && Once("spawn"))
            {
                var contents = ItemCatalog.RollBag(Mystery, 251, 0.5f, "F3 Probe");
                int odd = 0;
                foreach (var it in contents.items) if (it != null && BagTells.IsMysteryId(it.defId)) odd++;
                Spawn(net, Mystery, at, contents);
                Log("mystery-contents", ("odd", odd), ("mass", contents.TotalMassKg));
            }
            Track(net, Mystery);
            if (t >= 1f && Once("standins"))
            {
                for (int i = 0; i < 3; i++)
                {
                    float a = (i * 120f + 20f) * Mathf.Deg2Rad;
                    ResidentDirector.StandIns.Add(new ResidentDirector.StandIn
                    {
                        id = LifeId.Resident(StandInBase + i), center = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 3.2f,
                        radius = 0f, speed = 0f, pose = PersonPose.Stand,
                    });
                }
                HandsBots.Teleport(c.Player.gameObject, at + new Vector3(0f, 0f, -5f), Vector3.forward);
            }
            if (t >= 7f && Once("check"))
            {
                var nc = HandsBots.Find(net, Mystery);
                var fx = nc != null && nc.Item != null ? nc.Item.GetComponent<BagTellFx>() : null;
                int curious = 0; float approach = 0f;
                var dir = ResidentDirector.Instance;
                if (dir != null)
                    for (int i = 0; i < 3; i++)
                    {
                        var a = dir.Find(LifeId.Resident(StandInBase + i));
                        if (a == null || !a.Curious) continue;
                        curious++;
                        approach = Mathf.Max(approach, Vector3.Distance(a.pos, a.reqPos));
                    }
                mysteryPass = fx != null && fx.Tell == BagTell.Mystery && fx.BuzzCount > 0 && fx.TickCount > 0;
                crowdPass = fx != null && fx.CrowdDrawn > 0 && curious > 0 && approach > 0.5f;
                Log("mystery-check", ("buzz", fx != null ? fx.BuzzCount : -1), ("ticks", fx != null ? fx.TickCount : -1),
                    ("drawn", fx != null ? fx.CrowdDrawn : -1), ("curious", curious), ("approach", Math.Round(approach, 2)),
                    ("audioPass", mysteryPass), ("crowdPass", crowdPass));
            }
            if (c.IsHost && t >= 12f && Once("cleanup")) Cleanup(net, Mystery);
            if (t >= 16f && Once("reset-check"))
            {
                int curious = 0;
                var dir = ResidentDirector.Instance;
                if (dir != null)
                    for (int i = 0; i < 3; i++) if (dir.Find(LifeId.Resident(StandInBase + i)) is ResidentAgent a && a.Curious) curious++;
                Log("mystery-release", ("curious", curious), ("pass", curious == 0));
            }
        }

        void PoleFence(BotContext c, HandsNet net, TrashCarrier carrier, float t, int idx, Vector3 arena)
        {
            poleBag = HandsBots.Ground(arena + new Vector3(0f, 0f, 15f));
            if (c.IsHost && t >= 0.6f && Once("spawn")) Spawn(net, Pole, poleBag);
            Track(net, Pole);
            if (idx != 1)
            {
                if (c.IsHost && t >= 14f && Once("cleanup")) Cleanup(net, Pole);
                return;
            }
            if (t >= 1f && Once("place"))
            {
                HandsBots.Teleport(c.Player.gameObject, poleBag - Vector3.forward * 3.4f, Vector3.forward);
                fence = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fence.name = "F3Probe Fence";
                fence.transform.position = poleBag - Vector3.forward * 1.7f + Vector3.up * 0.75f;
                fence.transform.localScale = new Vector3(2.6f, 1.5f, 0.14f);
            }
            if (t >= 4f && Once("grab"))
            {
                var nc = HandsBots.Find(net, Pole);
                var pole = c.Player.GetComponent<GrabberPole>();
                int before = GrabberPole.Grabs;
                Vector3 from = c.Player.transform.position + Vector3.up * 1.1f;
                Vector3 to = nc != null && nc.Item != null ? nc.Item.WorldCenter : poleBag + Vector3.up;
                bool blocked = Physics.Raycast(from, to - from, out var hit, Vector3.Distance(from, to), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                               fence != null && hit.collider == fence.GetComponent<Collider>();
                bool used = pole != null && pole.UseNow();
                polePass = blocked && used && nc != null && carrier.Held == nc.Item && GrabberPole.Grabs > before;
                Log("pole-fence-check", ("blocked", blocked), ("used", used), ("held", nc != null && carrier.Held == nc.Item),
                    ("grabbed", GrabberPole.Grabs > before), ("pass", polePass));
            }
            if (t >= 10f && Once("drop") && carrier.IsCarrying) carrier.Drop();
        }

        void CannonMisfire(BotContext c, HandsNet net, TrashCarrier carrier, float t, int idx, List<PlayerBody> bodies)
        {
            if (bodies.Count < 3) return;
            var targetClient = bodies[1].ClientId;
            TruckControllerV2 targetTruck = null;
            var row = DepotRowNet.Instance;
            foreach (var tr in TruckControllerV2.All)
            {
                if (tr == null || tr.NetworkObject == null) continue;
                if (row != null && row.LotOfTruck(tr.NetworkObjectId) == 1) { targetTruck = tr; break; }
            }
            if (targetTruck == null) { if (Once("no-truck")) Log("misfire-no-truck"); return; }
            string key = MisfireKey(targetTruck.NetworkObjectId, targetClient);
            var hopper = BagCannonMount.HopperOf(targetTruck);
            if (c.IsHost && t >= 0.6f && Once("spawn"))
            {
                string why = "missing hopper";
                if (hopper != null && OpenSpot(targetTruck, hopper, out var at, out why)) misfireBag = Spawn(net, key, at);
                else Log("misfire-no-spot", ("why", why));
            }
            Track(net, key);
            if (idx == 2 && t >= 3.7f && Once("neighbour-place"))
            {
                var shooter = PlayerBody.For(targetClient);
                if (shooter != null)
                {
                    HandsBots.Teleport(c.Player.gameObject, shooter.transform.position + shooter.transform.right * 1.2f,
                        shooter.transform.position - c.Player.transform.position);
                    Log("misfire-neighbour-place", ("shooter", (long)targetClient), ("neighbour", (long)bodies[2].ClientId));
                }
            }
            if (idx == 1)
            {
                var nc = HandsBots.Find(net, key);
                if (t >= 2f && Once("place") && nc != null && nc.Item != null)
                {
                    Vector3 away = nc.Item.transform.position - hopper.TossTargetPosition; away.y = 0f;
                    HandsBots.Teleport(c.Player.gameObject, nc.Item.transform.position + away.normalized * 0.8f, -away);
                }
                if (t >= 3f && Once("grab"))
                {
                    bool ok = nc != null && nc.Item != null && carrier.PickUp(nc.Item);
                    Log("misfire-grab", ("ok", ok));
                }
                if (t >= 5f && Once("fire"))
                {
                    int before = BagCannon.Misfires;
                    bool fired = nc != null && carrier.Held == nc.Item && c.Player.GetComponent<BagCannon>() is BagCannon cannon && cannon.FireNow();
                    bool misfired = BagCannon.Misfires > before;
                    Log("misfire-fire", ("fired", fired), ("misfired", misfired), ("key", key), ("roll", MisfireRoll(key, targetClient)));
                }
            }
            if (t >= 10f && Once("check"))
            {
                bool burstSeen = burst.Contains(key) || c.IsHost && misfireBag is object && misfireBag.HasBurst;
                int splats = 0; foreach (var p in Puddles.All) if (p.kind == PuddleKind.Splat) splats++;
                bool neighbourSeen = neighbourSplats > 0 && neighbourShooter == targetClient && neighbourTarget == bodies[2].ClientId;
                misfirePass = burstSeen && splats > 0 && neighbourSeen;
                Log("misfire-check", ("key", key), ("burst", burstSeen), ("splats", splats),
                    ("neighbourSplats", neighbourSplats), ("neighbourShooter", (long)neighbourShooter),
                    ("neighbourTarget", (long)neighbourTarget), ("neighbourSeen", neighbourSeen), ("pass", misfirePass));
            }
        }

        void Traction(BotContext c, HandsNet net, float t)
        {
            if (!c.IsHost) return;
            TruckControllerV2 truck = null;
            foreach (var tr in TruckControllerV2.All) if (tr != null && BagCannonMount.IsMine(tr)) { truck = tr; break; }
            if (truck == null) return;
            var grip = truck.GetComponent<TruckPuddleGrip>();
            var wheel = truck.GetComponentInChildren<WheelCollider>();
            if (grip == null || wheel == null || !wheel.isGrounded || !wheel.GetGroundHit(out var hit)) return;
            wheelAt = hit.point;
            if (t >= 0.5f && Once("clear"))
            {
                Puddles.RemoveAll();
                WeatherNet.DebugSet(WeatherKind.Clear, 120f);
            }
            if (t >= 1.5f && Once("dry"))
            {
                dryStiff = wheel.sidewaysFriction.stiffness;
                drySlip = Puddles.SlipAt(wheelAt);
                Log("traction-dry", ("wheel", Math.Round(dryStiff, 3)), ("slip", Math.Round(drySlip, 3)));
            }
            const string leak = "f3probe/wheel#tell=leaky";
            if (t >= 2f && Once("leak")) Spawn(net, leak, wheelAt + Vector3.up * 0.35f);
            if (t >= 3.5f && Once("wheel-juice"))
            {
                trailMade = Puddles.Count > 0;
                // The bag's first drip can land beside a wheel when its collider touches the parked truck.
                // Lay that same juice on the contact patch to test the wheel/juice physics seam independently.
                wheelJuicePlaced = Puddles.Spawn(wheelAt + Vector3.up * 0.15f, 1f, PuddleKind.Juice);
                Log("traction-trail", ("bagTrail", trailMade), ("wheelJuice", wheelJuicePlaced),
                    ("at", wheelAt.ToString()));
            }
            if (t >= 6f && Once("juice"))
            {
                juiceStiff = wheel.sidewaysFriction.stiffness;
                juiceSlip = Puddles.SlipAt(wheelAt);
                juicePass = trailMade && wheelJuicePlaced && juiceSlip > 0.5f && grip.Slip > 0.5f && juiceStiff < dryStiff * 0.8f;
                Log("traction-juice", ("puddles", Puddles.Count), ("wheel", Math.Round(juiceStiff, 3)),
                    ("slip", Math.Round(juiceSlip, 3)), ("grip", Math.Round(grip.Slip, 3)),
                    ("trailMade", trailMade), ("wheelJuicePlaced", wheelJuicePlaced), ("pass", juicePass));
            }
            if (t >= 7f && Once("rain-start"))
            {
                Cleanup(net, leak);
                Puddles.RemoveAll();
                WeatherNet.DebugSet(WeatherKind.Rain, 120f);
            }
            if (t >= 16f && Once("rain"))
            {
                rainStiff = wheel.sidewaysFriction.stiffness;
                rainSlip = Puddles.SlipAt(wheelAt);
                rainPass = Weather.Kind == WeatherKind.Rain && Weather.IsOutdoors(wheelAt) &&
                           rainSlip > 0.05f && grip.Slip > 0.05f && rainStiff < dryStiff;
                Log("traction-rain", ("weather", Weather.Kind.ToString()), ("wet", Math.Round(Weather.Wetness, 3)),
                    ("wheel", Math.Round(rainStiff, 3)), ("slip", Math.Round(rainSlip, 3)), ("grip", Math.Round(grip.Slip, 3)),
                    ("pass", rainPass));
            }
        }

        void SoloDrag(BotContext c, HandsNet net, TrashCarrier carrier, float t, int idx, Vector3 arena)
        {
            Vector3 at = HandsBots.Ground(arena + new Vector3(0f, 0f, 15f));
            if (c.IsHost && t >= 0.6f && Once("spawn"))
            {
                WeatherNet.DebugSet(WeatherKind.Clear, 120f);
                Puddles.RemoveAll();
                Spawn(net, SoloHeavy, at, new BagContents
                {
                    bagId = SoloHeavy, foundAt = "F3 Probe", bagMassKg = 21f, sackVariant = 0,
                });
            }
            Track(net, SoloHeavy);
            if (idx != 0) return;
            if (t >= 1f && Once("place")) HandsBots.Teleport(c.Player.gameObject, at - Vector3.forward * 1.2f, Vector3.forward);
            var nc = HandsBots.Find(net, SoloHeavy);
            if (t >= 2.2f && Once("grab"))
            {
                bool ok = nc != null && nc.Item != null && carrier.PickUp(nc.Item);
                Log("solo-drag-grab", ("ok", ok), ("mass", nc != null && nc.Item != null ? nc.Item.MassKg : -1f));
            }
            if (t >= 3.2f && Once("walk-from")) dragFrom = c.Player.transform.position;
            if (t >= 3.2f && t < 8.5f && carrier.IsCarrying) c.Input.move = Vector2.up;
            if (t >= 9f && Once("check"))
            {
                var pt = PlayerTells.LocalTells;
                bool held = nc != null && carrier.Held == nc.Item;
                float walked = Vector3.Distance(dragFrom, c.Player.transform.position);
                float bagHeight = held ? nc.Item.WorldCenter.y - c.Player.transform.position.y : -1f;
                soloDragPass = held && pt != null && pt.Dragging && carrier.HasHoldOverride && walked > 0.5f;
                Log("solo-drag-check", ("held", held), ("dragging", pt != null && pt.Dragging),
                    ("holdOverride", carrier.HasHoldOverride), ("walked", Math.Round(walked, 2)),
                    ("bagHeight", Math.Round(bagHeight, 2)), ("pass", soloDragPass));
            }
            if (t >= 11f && Once("drop") && carrier.IsCarrying) carrier.Drop();
            if (c.IsHost && t >= 14f && Once("cleanup")) Cleanup(net, SoloHeavy);
        }

        static string MisfireKey(ulong truckId, ulong client)
        {
            for (int i = 0; i < 10000; i++)
            {
                string key = $"f3probe/mis/{truckId}/{i}#tell=none";
                if (MisfireRoll(key, client) < 125) return key;
            }
            return $"f3probe/mis/{truckId}/fallback#tell=none";
        }

        static uint MisfireRoll(string key, ulong client) =>
            (uint)PickupVisuals.StableHash($"{key}#cannon#{client}#1") % 1000u;

        static bool OpenSpot(TruckControllerV2 tr, TruckHopper h, out Vector3 spot, out string why)
        {
            spot = default;
            Vector3 hop = h.TossTargetPosition, aim = BagCannon.AimPoint(h);
            int mask = Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer);
            string blocker = "";
            for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < 33; i++)
            {
                float yaw = 180f + (i % 2 == 0 ? -1 : 1) * ((i + 1) / 2) * 11.25f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * tr.transform.forward; dir.y = 0f; dir.Normalize();
                Vector3 p = hop + dir * (ring == 0 ? 9f : 10f); p.y = tr.transform.position.y;
                Vector3 g = HandsBots.Ground(p + Vector3.up * 2f);
                if (Mathf.Abs(g.y - tr.transform.position.y) > 1.2f ||
                    Physics.CheckCapsule(g + Vector3.up * 0.6f, g + Vector3.up * 1.6f, 0.45f, mask, QueryTriggerInteraction.Ignore)) continue;
                Vector3 toward = aim - g; toward.y = 0f;
                Vector3 launch = g - toward.normalized * 0.3f + Vector3.up * 0.9f;
                if (!BagCannon.TrySolveClear(launch, aim, null, out _, out _))
                { blocker = BagCannon.LastClearanceBlocker ?? ""; continue; }
                spot = g; why = ""; return true;
            }
            why = blocker; return false;
        }

        static void Cleanup(HandsNet net, string key)
        {
            var nc = HandsBots.Find(net, key);
            if (nc != null && nc.Item != null && nc.St.Phase != HandsPhase.Held && nc.St.Phase != HandsPhase.Tug)
                nc.Item.Consume();
        }

        static void RemoveStandIns()
        {
            var list = ResidentDirector.StandIns;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].id.kind == LifeKind.Resident && list[i].id.index >= StandInBase && list[i].id.index < StandInBase + 3) list.RemoveAt(i);
        }
    }
}

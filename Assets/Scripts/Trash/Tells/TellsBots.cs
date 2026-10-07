using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// F3 bot brain <c>tells</c> (Docs/Multiplayer.md §14.3; checker <c>scratchpad/f3/verify_tells.py</c>). 24 s rounds by server time,
/// rotating: <b>lift</b> (host holds a forced-heavy bag, bot 2 takes the other end, they walk 6 s together and the host throws it),
/// <b>leaky</b> (bot 2 walks a forced-leaky bag 6 s, then the last bot sprints along the juice trail: slips), <b>lift-sprint</b>
/// (both people sprint together without dropping), <b>cannon</b> (every bot with a truck of its
/// own fires a bag into it from 9 m) and <b>pole</b> (host holds a bag, bot 2 pole-snatches it from 2.6 m: a tug; then pokes).
/// Every round ends with a <c>tellsbot census</c> on every machine: each networked / curb bag's tell and mass, and the puddle list,
/// so the checker can prove every machine agrees. Brains only drive their own player.
/// </summary>
public static class TellsBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        BotBrains.Register("tells", () => new TellsBrain());
        BotBrains.Register("cube-timing", () => new CubeTimingBrain());
    }

    static void Log(Dictionary<string, object> d) => NetAudit.Event("tellsbot", d);

    sealed class TellsBrain : IBotBrain
    {
        const float RoundSec = 24f;
        static readonly string[] Kinds = { "lift", "leaky", "lift-sprint", "cannon", "pole" };
        long round = -1;
        readonly HashSet<string> done = new HashSet<string>();
        Vector3 throwFrom, walkFrom;
        bool throwing;
        Vector3 walkDir = Vector3.forward;
        int delivered;
        readonly List<string> cannonKeys = new List<string>();
        readonly Dictionary<uint, string> cannonIds = new Dictionary<uint, string>();
        readonly HashSet<string> receivedCannon = new HashSet<string>();

        public void Begin(BotContext c)
        {
            c.Stop();
            HandsNet.TugsEnabled = true;
            HandsNet.StateApplied += OnState;
            Log(new Dictionary<string, object> { { "op", "begin" } });
        }

        public void End(BotContext c)
        {
            c.Stop();
            HandsNet.StateApplied -= OnState;
            Log(new Dictionary<string, object>
            {
                { "op", "end" }, { "slips", PlayerTells.Slips }, { "flops", PlayerTells.Flops }, { "outOfSteps", PlayerTells.OutOfSteps },
                { "shots", BagCannon.Shots }, { "misfires", BagCannon.Misfires }, { "grabs", GrabberPole.Grabs }, { "pokes", GrabberPole.Pokes },
                { "snatches", GrabberPole.Snatches }, { "reverts", NetCarry.Reverts }, { "delivered", delivered },
            });
        }

        void OnState(CarryNetState s)
        {
            if (s.Phase != HandsPhase.Gone && s.Phase != HandsPhase.Tug) return;
            var net = HandsNet.Instance;
            var nc = net != null ? net.Get(s.id) : null;
            string key = nc != null ? nc.Desc.key.ToString() : cannonIds.TryGetValue(s.id, out var tracked) ? tracked : "";
            if (!key.StartsWith("tellbot/")) return;
            if (s.Phase == HandsPhase.Tug)
            {
                if (key.StartsWith("tellbot/pole")) Log(new Dictionary<string, object> { { "op", "pole-tug" }, { "key", key } });
                return;
            }
            if (s.Has(CarryNetState.FlagReceived) && !s.Has(CarryNetState.FlagBurst) && key.StartsWith("tellbot/cannon"))
            {
                delivered++;
                receivedCannon.Add(key);
            }
            Log(new Dictionary<string, object> { { "op", "gone" }, { "key", key }, { "flags", s.flags } });
        }

        bool Once(string k) => done.Add(k);

        public void Tick(BotContext c, float dt)
        {
            var net = HandsNet.Instance;
            var tn = BagTellsNet.Instance;
            if (net == null || !net.IsSpawned || tn == null || c.Player == null || c.Elapsed < 15f) { c.Stop(); return; }
            var me = PlayerBody.For(net.LocalId);
            var carrier = TrashCarrier.Local;
            if (me == null || carrier == null) { c.Stop(); return; }
            if (Once("own-tools"))
            {
                GameSession.SetUpgradeLevel(ShopIds.BagCannon, 1);
                GameSession.SetUpgradeLevel(ShopIds.GrabberPole, 1);
            }
            // A pole grab becomes a shared grip; the challenger runs away. Heavy lifts continue through Lift below.
            var tug = net.TugOf(net.LocalId);
            if (tug.HasValue && net.Get(tug.Value.item)?.Item is Carryable shared && BagTells.Of(shared) != BagTell.Heavy)
            {
                c.Stop();
                if (tug.Value.challenger == net.LocalId) GripBotInput.PullAway(c, net, tug.Value);
                return;
            }

            double now = net.ServerNow;
            long r = (long)Math.Floor(now / RoundSec);
            float t = (float)(now - r * RoundSec);
            if (r != round) { round = r; done.Clear(); throwing = false; c.Stop(); }
            var bodies = HandsBots.Sorted();
            int n = bodies.Count, idx = bodies.IndexOf(me);
            string kind = Kinds[(int)(r % Kinds.Length)];
            Vector3 arena = HandsBots.Arena() + new Vector3(0f, 0f, (r % 2) * 6f);
            bool helperRole = n >= 2 && idx == 1;
            int lastIdx = n - 1;
            // Every round starts empty-handed (a tug winner may still hold last round's bag).
            if (t < 1f && Once("reset") && carrier.IsCarrying && !carrier.ExternalControl) carrier.Drop();

            switch (kind)
            {
                case "lift":
                case "lift-sprint":
                    Lift(c, net, tn, carrier, r, t, idx, helperRole, arena, kind == "lift-sprint");
                    break;
                case "leaky":
                    Leaky(c, net, carrier, r, t, idx, n, lastIdx, arena);
                    break;
                case "cannon":
                    Cannon(c, net, carrier, r, t);
                    break;
                case "pole":
                    Pole(c, net, carrier, r, t, idx, helperRole, arena);
                    break;
            }
            if (t >= 22f && Once("census")) Census(net, r, kind);
        }

        // ---------------------------------------------------------------- lift

        void Lift(BotContext c, HandsNet net, BagTellsNet tn, TrashCarrier carrier, long r, float t, int idx, bool helper, Vector3 arena, bool sprint)
        {
            string key = $"tellbot/lift{r}#tell=heavy";
            if (c.IsHost && t >= 0.2f && Once("spawn"))
                Spawn(net, key, arena, 21f);
            if (t >= 1f && Once("place"))
            {
                if (idx == 0) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(-0.9f, 0f, 0f), Vector3.right);
                if (helper) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(-0.2f, 0f, 1.3f), Vector3.back);
            }
            var nc = HandsBots.Find(net, key);
            if (idx == 0 && t >= 2f && Once("grab"))
            {
                bool ok = nc != null && nc.Item != null && !carrier.IsCarrying && carrier.PickUp(nc.Item);
                Log(new Dictionary<string, object> { { "op", "lift-grab" }, { "round", r }, { "ok", ok }, { "mass", nc != null ? Math.Round(nc.Item.MassKg, 1) : -1 } });
            }
            if (helper && t >= 3.5f && Once("help"))
            {
                if (nc != null) tn.RequestHelp(nc.Id);
                Log(new Dictionary<string, object> { { "op", "lift-help" }, { "round", r }, { "seen", nc != null } });
            }
            if ((idx == 0 || helper) && t >= 5f && t < 11f && net.TugOf(net.LocalId).HasValue)
            {
                GripBotInput.Move(c, Vector3.right, sprint);
            }
            else c.Stop();
            if (helper && t >= 11f && Once("let-go") && nc != null) net.LocalLetGo(nc.Id);
            if (idx == 0 && t >= 5f && Once("walk-from")) walkFrom = c.Player.transform.position;
            if (t >= 7.5f && Once("mid"))
            {
                var pt = PlayerTells.LocalTells;
                var holderPt = nc != null ? PlayerTells.For(nc.St.holder) : null;
                float gap = holderPt != null ? Flat(holderPt.transform.position - c.Player.transform.position) : -1f;
                bool lifted = nc != null && tn.TryLift(nc.Id, out var l) && l.state == LiftNet.Lifting;
                Log(new Dictionary<string, object>
                {
                    { "op", "lift-mid" }, { "round", r }, { "sprint", sprint }, { "lifted", lifted }, { "helping", pt != null && pt.Helping },
                    { "together", pt != null && pt.LiftedTogether }, { "dragging", pt != null && pt.Dragging },
                    { "holder", nc != null && nc.St.Phase == HandsPhase.Held ? (long)nc.St.holder : -1 }, { "gap", Math.Round(gap, 2) },
                    { "walkedTotal", Math.Round(c.walked, 1) }, { "move", c.Input != null ? c.Input.move.ToString() : "-" },
                    { "tpc", c.Player.GetComponent<StarterAssets.ThirdPersonController>() is var tpc && tpc != null && tpc.enabled },
                    { "body", PlayerBody.For(net.LocalId) is var pb && pb != null ? pb.State.ToString() : "-" },
                    { "cc", c.Player.GetComponent<CharacterController>() is var cc && cc != null && cc.enabled }, { "trans", SceneController.IsTransitioning },
                    { "driving", c.Player.GetComponent<PlayerController>() is var ppc && ppc != null && ppc.IsDriving }, { "pos", c.Player.transform.position.ToString() },
                    { "speeds", tpc != null ? tpc.MoveSpeed.ToString("0.00") + "/" + tpc.SprintSpeed.ToString("0.00") : "-" },
                    { "analog", c.Input != null && c.Input.analogMovement }, { "slip", pt != null ? Math.Round(pt.Slip, 2) : -1 },
                    { "grounded", tpc != null && tpc.Grounded }, { "ccvel", c.Player.GetComponent<CharacterController>() is var cc2 && cc2 != null ? cc2.velocity.ToString() : "-" },
                });
            }
            if (idx == 0 && t >= 11.5f && Once("throw"))
            {
                bool holding = nc != null && carrier.Held == nc.Item;
                var pt = PlayerTells.LocalTells;
                bool together = pt != null && pt.LiftedTogether;
                if (holding) { throwFrom = nc.Item.WorldCenter; throwing = true; carrier.ThrowWithCharge(0.7f); }
                Log(new Dictionary<string, object> { { "op", "lift-throw" }, { "round", r }, { "sprint", sprint }, { "holding", holding }, { "together", together },
                    { "walked", Math.Round(Flat(c.Player.transform.position - walkFrom), 2) } });
            }
            if (idx == 0 && t >= 17f && Once("land"))
            {
                float d = throwing && nc != null && nc.Item != null ? Flat(nc.Item.WorldCenter - throwFrom) : -1f;
                Log(new Dictionary<string, object> { { "op", "lift-land" }, { "round", r }, { "sprint", sprint }, { "throwDist", Math.Round(d, 2) } });
            }
            if (c.IsHost && t >= 19f && Once("cleanup")) Cleanup(net, key);
        }

        // ---------------------------------------------------------------- leaky

        void Leaky(BotContext c, HandsNet net, TrashCarrier carrier, long r, float t, int idx, int n, int lastIdx, Vector3 arena)
        {
            string key = $"tellbot/leak{r}#tell=leaky";
            int walker = n >= 2 ? 1 : 0;
            Vector3 start = arena + new Vector3(0f, 0f, -4f);
            if (c.IsHost && t >= 0.2f && Once("spawn")) Spawn(net, key, start, 5f);
            var nc = HandsBots.Find(net, key);
            if (idx == walker && t >= 1f && Once("place")) HandsBots.Teleport(c.Player.gameObject, start + new Vector3(0f, 0f, -0.9f), Vector3.forward);
            if (idx == walker && t >= 2f && Once("grab"))
            {
                bool ok = nc != null && nc.Item != null && !carrier.IsCarrying && carrier.PickUp(nc.Item);
                Log(new Dictionary<string, object> { { "op", "leak-grab" }, { "round", r }, { "ok", ok } });
            }
            bool walking = idx == walker && t >= 3f && t < 9f && carrier.IsCarrying;
            bool sprinting = idx == lastIdx && t >= 11f && t < 15f;
            if (idx == walker && t >= 9.2f && Once("drop") && carrier.IsCarrying) carrier.Drop();
            if (idx == lastIdx && t >= 10.5f && Once("slip-place"))
            {
                if (carrier.IsCarrying) carrier.Drop();
                HandsBots.Teleport(c.Player.gameObject, start + new Vector3(0f, 0f, -3f), Vector3.forward);
            }
            if (walking || sprinting)
            {
                c.Input.move = new Vector2(0f, 1f);
                c.Input.sprint = sprinting;
                FaceAlong(c, Vector3.forward);
            }
            else c.Stop();
            if (idx == lastIdx && t >= 16f && Once("slips"))
                Log(new Dictionary<string, object> { { "op", "leak-slips" }, { "round", r }, { "slips", PlayerTells.Slips }, { "flops", PlayerTells.Flops } });
            if (c.IsHost && t >= 19f && Once("cleanup")) Cleanup(net, key);
        }

        // ---------------------------------------------------------------- cannon

        void Cannon(BotContext c, HandsNet net, TrashCarrier carrier, long r, float t)
        {
            if (c.IsHost && t >= 0.2f && Once("spawn"))
            {
                foreach (var tr in TruckControllerV2.All)
                {
                    var h = BagCannonMount.HopperOf(tr);
                    if (tr == null || h == null || tr.NetworkObject == null) continue;
                    if (!OpenSpot(tr, h, out var p, out string why))
                    {
                        Log(new Dictionary<string, object> { { "op", "cannon-no-spot" }, { "truck", (long)tr.NetworkObjectId }, { "why", why } });
                        continue;
                    }
                    Spawn(net, $"tellbot/cannon{r}/{tr.NetworkObjectId}#tell=none", p, 4f);
                }
            }
            TruckControllerV2 mine = null;
            foreach (var tr in TruckControllerV2.All) if (tr != null && BagCannonMount.IsMine(tr)) { mine = tr; break; }
            c.Stop();
            if (mine == null) { if (Once("no-truck")) Log(new Dictionary<string, object> { { "op", "cannon-no-truck" }, { "round", r } }); return; }
            string key = $"tellbot/cannon{r}/{mine.NetworkObjectId}#tell=none";
            var nc = HandsBots.Find(net, key);
            if (nc != null) cannonIds[nc.Id] = key;
            if (t >= 2f && Once("place") && nc != null && nc.Item != null)
            {
                Vector3 hop = BagCannonMount.HopperOf(mine).TossTargetPosition;
                Vector3 bag = nc.Item.transform.position;
                Vector3 back = bag - hop; back.y = 0f;
                HandsBots.Teleport(c.Player.gameObject, bag + back.normalized * 0.8f, -back);
            }
            if (t >= 3f && Once("grab"))
            {
                bool ok = nc != null && nc.Item != null && !carrier.IsCarrying && carrier.PickUp(nc.Item);
                Log(new Dictionary<string, object> { { "op", "cannon-grab" }, { "round", r }, { "ok", ok } });
            }
            if (t >= 4.8f && Once("fire"))
            {
                bool fired = nc != null && carrier.Held == nc.Item && c.Player.GetComponent<BagCannon>() is BagCannon bc && bc.FireNow();
                float dist = nc != null && nc.Item != null ? Flat(nc.Item.WorldCenter - BagCannonMount.HopperOf(mine).TossTargetPosition) : -1f;
                Log(new Dictionary<string, object> { { "op", "cannon-fire" }, { "round", r }, { "fired", fired }, { "dist", Math.Round(dist, 1) }, { "misfires", BagCannon.Misfires } });
                if (fired) cannonKeys.Add(key);
            }
            foreach (float at in new[] { 5.0f, 5.3f, 5.8f, 6.5f })
                if (t >= at && Once("trace" + at) && nc != null && nc.Item != null && cannonKeys.Contains(key))
                {
                    var hop = BagCannonMount.HopperOf(mine).TossTargetPosition;
                    var b = nc.Item;
                    Log(new Dictionary<string, object>
                    {
                        { "op", "cannon-trace" }, { "round", r }, { "at", at }, { "rel", (b.WorldCenter - hop).ToString() },
                        { "vel", b.Body != null ? b.Body.linearVelocity.ToString() : "-" }, { "kin", b.Body != null && b.Body.isKinematic },
                        { "state", b.State.ToString() }, { "phase", nc.St.Phase.ToString() }, { "remote", b.NetRemote },
                    });
                }
            if (t >= 9f && Once("check"))
            {
                var hop = BagCannonMount.HopperOf(mine);
                bool fired = cannonKeys.Contains(key);
                bool receivedGone = receivedCannon.Contains(key);
                bool hostCargo = c.IsHost && CargoContains(hop, key);
                bool gone = fired && (receivedGone || hostCargo);
                Log(new Dictionary<string, object>
                {
                    { "op", "cannon-check" }, { "round", r }, { "key", key }, { "fired", fired }, { "gone", gone },
                    { "receivedGone", receivedGone }, { "hostCargo", hostCargo },
                    { "phase", nc != null ? nc.St.Phase.ToString() : "none" }, { "flags", nc != null ? nc.GoneFlags : 0 },
                    { "fromHopper", !gone && hop != null && nc != null && nc.Item != null ? Math.Round(Flat(nc.Item.WorldCenter - hop.TossTargetPosition), 2) : -1 },
                    { "y", !gone && nc != null && nc.Item != null && hop != null ? Math.Round(nc.Item.WorldCenter.y - hop.TossTargetPosition.y, 2) : 0 },
                });
            }
            if (c.IsHost && t >= 9f && Once("cargo-check"))
                foreach (var tr in TruckControllerV2.All)
                {
                    if (tr == null || tr.NetworkObject == null) continue;
                    string cargoKey = $"tellbot/cannon{r}/{tr.NetworkObjectId}#tell=none";
                    Log(new Dictionary<string, object> { { "op", "cannon-cargo" }, { "round", r }, { "key", cargoKey },
                        { "collected", CargoContains(BagCannonMount.HopperOf(tr), cargoKey) } });
                }
            if (c.IsHost && t >= 19f && Once("cleanup"))
                foreach (var tr in TruckControllerV2.All)
                    if (tr != null && tr.NetworkObject != null) Cleanup(net, $"tellbot/cannon{r}/{tr.NetworkObjectId}#tell=none");
        }

        // ---------------------------------------------------------------- pole

        void Pole(BotContext c, HandsNet net, TrashCarrier carrier, long r, float t, int idx, bool helper, Vector3 arena)
        {
            string key = $"tellbot/pole{r}#tell=none";
            if (c.IsHost && t >= 0.2f && Once("spawn")) Spawn(net, key, arena, 4f);
            var nc = HandsBots.Find(net, key);
            c.Stop();
            if (idx == 0 && t >= 1f && Once("place")) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(-0.8f, 0f, 0f), Vector3.right);
            if (helper && t >= 1f && Once("place")) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(1.9f, 0f, 0f), Vector3.left);
            if (idx == 0 && t >= 2f && Once("grab"))
            {
                bool ok = nc != null && nc.Item != null && !carrier.IsCarrying && carrier.PickUp(nc.Item);
                Log(new Dictionary<string, object> { { "op", "pole-hold" }, { "round", r }, { "ok", ok } });
            }
            if (helper && t >= 4f && Once("snatch"))
            {
                var pole = c.Player.GetComponent<GrabberPole>();
                int before = GrabberPole.Snatches;
                bool used = pole != null && pole.UseNow();
                Log(new Dictionary<string, object> { { "op", "pole-use" }, { "round", r }, { "used", used }, { "snatch", GrabberPole.Snatches > before } });
            }
            if (t >= 10f && Once("view"))
            {
                Log(new Dictionary<string, object>
                {
                    { "op", "pole-view" }, { "round", r }, { "phase", nc != null ? nc.St.Phase.ToString() : "none" },
                    { "holder", nc != null && nc.St.Phase == HandsPhase.Held ? (long)nc.St.holder : -1 },
                });
            }
            if (t >= 11f && Once("drop") && carrier.IsCarrying) carrier.Drop();
            if (c.IsHost && t >= 12f && Once("early-cleanup")) Cleanup(net, key);
            if (t >= 13f && Once("poke-place"))
            {
                if (idx == 0) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(-0.8f, 0f, 0f), Vector3.right);
                else if (helper) HandsBots.Teleport(c.Player.gameObject, arena + new Vector3(1.9f, 0f, 0f), Vector3.left);
            }
            if (helper && t >= 13.5f && Once("poke"))
            {
                var pole = c.Player.GetComponent<GrabberPole>();
                int before = GrabberPole.Pokes;
                bool used = pole != null && pole.UseNow();
                Log(new Dictionary<string, object> { { "op", "pole-poke" }, { "round", r }, { "used", used }, { "poked", GrabberPole.Pokes > before } });
            }
            if (c.IsHost && t >= 19f && Once("cleanup")) Cleanup(net, key);
        }

        // ---------------------------------------------------------------- shared

        static bool CargoContains(TruckHopper hop, string key)
        {
            if (hop == null) return false;
            string business = hop.ResolveBusiness();
            var bags = string.IsNullOrEmpty(business) || business == Businesses.LocalId
                ? GameSession.TruckBags : Businesses.HostCargo(business);
            if (bags == null) return false;
            foreach (var bag in bags) if (bag != null && bag.bagId == key) return true;
            return false;
        }

        /// <summary>A clear spot ~9-10 m from the hopper, on walkable ground with an unobstructed physical arc.</summary>
        static bool OpenSpot(TruckControllerV2 tr, TruckHopper h, out Vector3 spot, out string why)
        {
            spot = default;
            Vector3 hop = h.TossTargetPosition;
            Vector3 aim = BagCannon.AimPoint(h);
            int mask = Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer);
            int steep = 0, occupied = 0, blocked = 0;
            string lastBlocker = "";
            for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < 33; i++)
            {
                float yaw = 180f + (i % 2 == 0 ? -1 : 1) * ((i + 1) / 2) * 11.25f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * tr.transform.forward; dir.y = 0f; dir.Normalize();
                Vector3 p = hop + dir * (ring == 0 ? 9f : 10f);
                p.y = tr.transform.position.y;
                Vector3 g = HandsBots.Ground(p + Vector3.up * 2f);
                if (Mathf.Abs(g.y - tr.transform.position.y) > 1.2f) { steep++; continue; }
                if (Physics.CheckCapsule(g + Vector3.up * 0.6f, g + Vector3.up * 1.6f, 0.45f, mask, QueryTriggerInteraction.Ignore))
                { occupied++; continue; }
                Vector3 toward = aim - g;
                toward.y = 0f;
                Vector3 launch = g - toward.normalized * 0.3f + Vector3.up * 0.9f; // held bag sits a little behind its curb spawn
                if (!BagCannon.TrySolveClear(launch, aim, null, out _, out _))
                { blocked++; lastBlocker = BagCannon.LastClearanceBlocker ?? ""; continue; }
                spot = g;
                why = "";
                return true;
            }
            why = $"height={steep}, occupied={occupied}, blocked={blocked}, last={lastBlocker}";
            return false;
        }

        static void Spawn(HandsNet net, string key, Vector3 at, float kg)
        {
            var bag = TrashBag.Spawn(new BagContents { bagId = key, foundAt = "Tellbot Ave", bagMassKg = kg, sackVariant = 0 }, HandsBots.Ground(at), Quaternion.identity);
            if (bag != null) net.HostPublishNow(bag);
            Log(new Dictionary<string, object> { { "op", "spawn" }, { "key", key }, { "tell", BagTells.Of(key).ToString() } });
        }

        static void Cleanup(HandsNet net, string key)
        {
            var nc = HandsBots.Find(net, key);
            if (nc == null || nc.Item == null || nc.Item.State == CarryState.Consumed) return;
            if (nc.St.Phase == HandsPhase.Held || nc.St.Phase == HandsPhase.Tug) return; // someone still has it; next time
            nc.Item.Consume();
        }

        /// <summary>Walk along a world direction: StarterAssets moves relative to the camera's yaw (like BotBrains' walk brain).</summary>
        static void FaceAlong(BotContext c, Vector3 dir)
        {
            var cam = Camera.main;
            float camYaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            Vector3 local = Quaternion.Euler(0f, -camYaw, 0f) * dir;
            if (c.Input != null && c.Input.move.sqrMagnitude > 0.01f) c.Input.move = new Vector2(local.x, local.z).normalized * c.Input.move.magnitude;
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        void Census(HandsNet net, long r, string kind)
        {
            var sb = new StringBuilder();
            var keys = new List<string>();
            foreach (var nc in net.Items.Values)
            {
                if (nc == null || nc.Item == null || nc.Desc.kind != CarryDesc.KindBag || nc.St.Phase == HandsPhase.Gone) continue;
                keys.Add($"{nc.Desc.key}|{BagTells.Of(nc.Desc.key.ToString())}|{nc.Item.MassKg:0.0}");
            }
            int curb = 0;
            foreach (var stop in PickupStop.All)
            {
                if (stop == null) continue;
                foreach (var b in stop.SpawnedBags)
                {
                    if (b == null || b.Contents == null || string.IsNullOrEmpty(b.Contents.bagId) || curb >= 400) continue;
                    curb++;
                    keys.Add($"{b.Contents.bagId}|{b.Tell}|{b.MassKg:0.0}");
                }
            }
            keys.Sort(StringComparer.Ordinal);
            var pud = new List<string>();
            foreach (var p in Puddles.All) pud.Add($"{p.id}@{p.pos.x:0.0},{p.pos.z:0.0}r{p.radius:0.00}k{(int)p.kind}");
            pud.Sort(StringComparer.Ordinal);
            Log(new Dictionary<string, object>
            {
                { "op", "census" }, { "round", r }, { "kind", kind }, { "bags", keys.ToArray() }, { "puddles", pud.ToArray() },
                { "lifts", BagTellsNet.Instance != null ? BagTellsNet.Instance.Lifts.Count : -1 }, { "reverts", NetCarry.Reverts },
            });
        }
    }

    /// <summary>One-host smoke check: enter during the press wind-up, then leave during the next wind-up.</summary>
    sealed class CubeTimingBrain : IBotBrain
    {
        Compactor compactor;
        float phaseAt;
        int phase;
        bool enterOk, leaveOk;

        public void Begin(BotContext c) { c.Stop(); Log(new Dictionary<string, object> { { "op", "cube-timing-begin" } }); }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            if (!c.IsHost || c.Player == null || PlayerTells.LocalTells == null || c.Elapsed < 3f) return;
            if (compactor == null)
            {
                Vector3 want = DepotRowWorld.Active && DepotRowWorld.LocalLot >= 0
                    ? DepotRowLayout.InteriorOrigin(DepotRowWorld.LocalLot) : c.Player.transform.position;
                float best = float.MaxValue;
                foreach (var candidate in UnityEngine.Object.FindObjectsByType<Compactor>(FindObjectsSortMode.None))
                {
                    if (candidate == null || !candidate.isActiveAndEnabled || candidate.chamber == null) continue;
                    float d = (candidate.transform.position - want).sqrMagnitude;
                    if (d >= best) continue;
                    compactor = candidate;
                    best = d;
                }
                if (compactor == null) return;
            }
            var local = PlayerTells.LocalTells;
            ulong id = local.ClientId;
            float elapsed = Time.time - phaseAt;
            switch (phase)
            {
                case 0:
                    Place(c, false);
                    bool outside = !Inside();
                    compactor.Smash();
                    phaseAt = Time.time;
                    phase = 1;
                    Log(new Dictionary<string, object> { { "op", "cube-press-outside" }, { "outside", outside }, { "busy", compactor.Busy } });
                    break;
                case 1 when elapsed >= 0.06f:
                    Place(c, true);
                    Log(new Dictionary<string, object> { { "op", "cube-enter-during-windup" }, { "inside", Inside() } });
                    phaseAt = Time.time;
                    phase = 2;
                    break;
                case 2 when elapsed >= 0.7f:
                    enterOk = CompactorCube.IsCubed(id) && !Inside();
                    Log(new Dictionary<string, object> { { "op", "cube-after-impact" }, { "cubed", CompactorCube.IsCubed(id) },
                        { "outside", !Inside() }, { "remaining", CompactorCube.Remaining(id) }, { "pass", enterOk } });
                    phaseAt = Time.time;
                    phase = 3;
                    break;
                case 3 when elapsed >= 10.2f && !compactor.Busy && !CompactorCube.IsCubed(id):
                    Place(c, true);
                    bool inside = Inside();
                    compactor.Smash();
                    phaseAt = Time.time;
                    phase = 4;
                    Log(new Dictionary<string, object> { { "op", "cube-press-inside" }, { "inside", inside }, { "busy", compactor.Busy } });
                    break;
                case 4 when elapsed >= 0.06f:
                    Place(c, false);
                    Log(new Dictionary<string, object> { { "op", "cube-leave-during-windup" }, { "outside", !Inside() } });
                    phaseAt = Time.time;
                    phase = 5;
                    break;
                case 5 when elapsed >= 0.7f:
                    leaveOk = !CompactorCube.IsCubed(id) && !Inside();
                    Log(new Dictionary<string, object> { { "op", "cube-left-before-impact" }, { "cubed", CompactorCube.IsCubed(id) },
                        { "outside", !Inside() }, { "pass", leaveOk } });
                    phase = 6;
                    break;
            }
        }

        // Check the actual position, not PlayersIn: that query intentionally excludes cubed players,
        // so it would report "outside" even if a newly cubed player were still trapped in the hopper.
        bool Inside()
        {
            var player = PlayerTells.LocalTells;
            if (player == null || compactor == null || compactor.chamber == null) return false;
            Transform ch = compactor.chamber;
            Vector3 half = Vector3.Scale(compactor.chamberSize * 0.5f,
                new Vector3(Mathf.Abs(ch.lossyScale.x), Mathf.Abs(ch.lossyScale.y), Mathf.Abs(ch.lossyScale.z)));
            Vector3 feet = player.transform.position;
            return At(feet + Vector3.up * 0.3f) || At(feet + Vector3.up * 0.9f);

            bool At(Vector3 world)
            {
                Vector3 local = Quaternion.Inverse(ch.rotation) * (world - ch.position);
                return Mathf.Abs(local.x) <= half.x + 0.35f && Mathf.Abs(local.z) <= half.z + 0.35f &&
                    local.y >= -half.y - 0.4f && local.y <= half.y + 1.2f;
            }
        }

        void Place(BotContext c, bool inside)
        {
            Vector3 at = inside ? compactor.chamber.position - Vector3.up * 0.3f
                : compactor.baleLanding != null ? compactor.baleLanding.position
                : compactor.chamber.position + compactor.transform.right * 3f;
            HandsBots.Teleport(c.Player.gameObject, at, compactor.transform.forward);
        }

        public void End(BotContext c)
        {
            c.Stop();
            Log(new Dictionary<string, object> { { "op", "cube-timing-end" }, { "enterPass", enterOk }, { "leavePass", leaveOk },
                { "complete", phase >= 6 } });
        }
    }
}

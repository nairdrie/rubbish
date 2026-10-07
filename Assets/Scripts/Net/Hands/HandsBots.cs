using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Bot brains for package N2 / N2b (Docs/Multiplayer.md §8.2 M2 gate, §8.7.6). Each brain only drives its OWN player (teleports
/// it, presses its own carrier and hands) and logs <c>handsbot</c> / <c>racebot</c> audit lines that
/// <c>scratchpad/n2/verify_hands.py</c> checks.
/// <list type="bullet">
/// <item><c>grabrace</c>: every 2 s of server time the host puts a 5 kg bag at the arena and every bot stands within 1 m of
/// it; on the round's beat they all press E (pattern by round: together, host 100 ms early, guests 100 ms early, together).
/// Tugs are off (the Lite tie rule: the earlier clamped press wins). The gate: exactly one holder per round on every
/// machine, the host wins ≤55% of simultaneous grabs, the earlier of a 100 ms pair wins.</item>
/// <item><c>hands</c>: 12 s slots rotate pairs of bots through pass, bonk, shove, carry + throw a flopped friend, carry +
/// wriggle free, hitch a ride, a stronger shared pull and an equal-pull rip; everyone else runs curb bags
/// into trucks (host-confirmed pay by the truck's business). The host keeps curbs stocked and empties full trucks.</item>
/// </list>
/// </summary>
public static class HandsBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        BotBrains.Register("grabrace", () => new GrabRaceBrain());
        BotBrains.Register("hands", () => new HandsBrain());
        BotBrains.Register("shop", () => new ShopBrain());
    }

    // ==================================================================================== shared helpers

    internal static Vector3 Arena()
    {
        Vector3 p = DepotRowWorld.Active ? DepotRowLayout.DoorPoint(0) + new Vector3(12f, 0f, 10f) : new Vector3(0f, 0f, 0f);
        return Ground(p);
    }

    public static Vector3 Ground(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 6f, Vector3.down, out var hit, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return hit.point;
        return p;
    }

    public static void Teleport(GameObject player, Vector3 to, Vector3 face)
    {
        var cc = player.GetComponent<CharacterController>();
        bool was = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        player.transform.position = to + Vector3.up * 0.05f;
        face.y = 0f;
        if (face.sqrMagnitude > 0.0001f) player.transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up);
        if (cc != null) cc.enabled = was;
    }

    internal static List<PlayerBody> Sorted()
    {
        var list = new List<PlayerBody>();
        foreach (var b in PlayerBody.All) if (b != null && b.Player != null && b.Player.IsSpawned) list.Add(b);
        list.Sort((a, b) => a.ClientId.CompareTo(b.ClientId));
        return list;
    }

    internal static NetCarry Find(HandsNet net, string key)
    {
        foreach (var nc in net.Items.Values) if (nc != null && nc.Desc.key.ToString() == key) return nc;
        return null;
    }

    internal static void Log(string kind, Dictionary<string, object> d) => NetAudit.Event(kind, d);

    // ==================================================================================== grab race

    sealed class GrabRaceBrain : IBotBrain
    {
        const float RoundSec = 2f;
        long lastSpawned = -1, lastPositioned = -1, lastPressed = -1, lastViewed = -1;
        TrashBag current;
        long currentRound = -1;
        int presses, holds;

        public void Begin(BotContext c)
        {
            c.Stop();
            HandsNet.TugsEnabled = false;
            Log("racebot", new Dictionary<string, object> { { "phase", "begin" } });
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            var net = HandsNet.Instance;
            if (net == null || !net.IsSpawned || c.Player == null) return;
            var me = PlayerBody.For(net.LocalId);
            var carrier = TrashCarrier.Local;
            if (me == null || carrier == null) return;
            double now = net.ServerNow;
            long r = (long)Math.Floor(now / RoundSec);
            double phase = now - r * RoundSec;
            if (c.Elapsed < 15f) return; // let everyone join and settle
            Vector3 arena = Arena();
            var bodies = Sorted();
            int idx = bodies.IndexOf(me);

            if (c.IsHost && r != lastSpawned && phase >= 0.05)
            {
                lastSpawned = r;
                if (current != null && current.State != CarryState.Consumed)
                {
                    current.Consume();
                }
                current = TrashBag.Spawn(new BagContents { bagId = "race/" + r, foundAt = "", bagMassKg = 5f, sackVariant = 0 }, arena, Quaternion.identity);
                currentRound = r;
                if (current != null) net.HostPublishNow(current);
            }
            if (r != lastPositioned && phase >= 0.35)
            {
                lastPositioned = r;
                float a = idx * 2.399f;
                Vector3 off = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.9f;
                Teleport(c.Player.gameObject, arena + off, -off);
            }
            int pattern = (int)(r % 4);
            double offset = pattern == 1 ? (c.IsHost ? 0.0 : 0.1) : pattern == 2 ? (c.IsHost ? 0.1 : 0.0) : 0.0;
            double press = r * RoundSec + 0.9 + offset;
            if (r != lastPressed && net.StampNow >= press && phase < 1.5)
            {
                lastPressed = r;
                var nc = Find(net, "race/" + r);
                bool ok = false;
                double stamp = net.StampNow;
                if (nc != null && nc.Item != null && nc.Item.State != CarryState.Carried && nc.Item.State != CarryState.Consumed && !carrier.IsCarrying)
                    ok = carrier.PickUp(nc.Item);
                presses++;
                Log("racebot", new Dictionary<string, object>
                {
                    { "phase", "press" }, { "round", r }, { "pattern", pattern }, { "ok", ok }, { "seen", nc != null },
                    { "stamp", Math.Round(stamp, 4) }, { "target", Math.Round(press, 4) }, { "host", c.IsHost },
                });
            }
            if (r != lastViewed && phase >= 1.75)
            {
                lastViewed = r;
                var nc = Find(net, "race/" + r);
                bool localHolds = nc != null && nc.Item != null && carrier.Held == nc.Item;
                if (localHolds) holds++;
                Log("racebot", new Dictionary<string, object>
                {
                    { "phase", "view" }, { "round", r }, { "pattern", pattern }, { "exists", nc != null },
                    { "holder", nc != null && nc.HostSt.Phase == HandsPhase.Held ? (long)nc.HostSt.holder : -1 },
                    { "statePhase", nc != null ? nc.HostSt.Phase.ToString() : "none" }, { "localHolds", localHolds }, { "me", (long)net.LocalId },
                    { "reverts", NetCarry.Reverts },
                });
            }
        }

        public void End(BotContext c)
        {
            c.Stop();
            Log("racebot", new Dictionary<string, object> { { "phase", "end" }, { "presses", presses }, { "holds", holds }, { "reverts", NetCarry.Reverts } });
        }
    }

    // ==================================================================================== chaos: every verb

    sealed class HandsBrain : IBotBrain
    {
        const float SlotSec = 12f;
        static readonly string[] Kinds = { "pass", "bonk", "shove", "carry-throw", "hitch", "grip-take", "grip-rip", "carry-wriggle" };

        long slot = -1;
        readonly HashSet<string> done = new HashSet<string>();
        float nextCurb, nextSupply, nextReport, nextWriggle;
        int curbPicks, curbTosses, wriggles;
        long paidCents;
        int paid;

        public void Begin(BotContext c)
        {
            c.Stop();
            HandsNet.TugsEnabled = true;
            HandsNet.DeliveryPaid += OnPaid;
            WorldState.HopperPaid += OnPaid;
        }

        void OnPaid(string key, long cents, string business) { paid++; paidCents += cents; }

        public void End(BotContext c)
        {
            c.Stop();
            HandsNet.DeliveryPaid -= OnPaid;
            WorldState.HopperPaid -= OnPaid;
            Report(c, "end");
        }

        bool Once(string key)
        {
            if (done.Contains(key)) return false;
            done.Add(key);
            return true;
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            var net = HandsNet.Instance;
            if (net == null || !net.IsSpawned || c.Player == null || c.Elapsed < 12f) return;
            var me = PlayerBody.For(net.LocalId);
            var carrier = TrashCarrier.Local;
            if (me == null || carrier == null) return;
            if (c.IsHost) HostChores(c);
            StepDeferred(c, carrier);
            if (c.Elapsed >= nextReport) { nextReport = c.Elapsed + 30f; Report(c, "progress"); }

            // Shared grips use ordinary movement: one runner takes, or equal opposing runners rip.
            var tug = net.TugOf(net.LocalId);
            if (tug.HasValue) { Pull(net, tug.Value, c); return; }

            double now = net.ServerNow;
            long s = (long)Math.Floor(now / SlotSec);
            float t = (float)(now - s * SlotSec);
            if (s != slot) { slot = s; done.Clear(); }
            var bodies = Sorted();
            int n = bodies.Count;
            int myIdx = bodies.IndexOf(me);
            string kind = Kinds[(int)(s % Kinds.Length)];
            // Rotate pairs across cycles too (with 4 bots and 8 kinds, s % n alone would give every kind the same actor).
            long r = s + s / Kinds.Length;
            int actorIdx = n >= 2 ? (int)(r % n) : -1;
            int partnerIdx = n >= 2 ? (int)((r + 1) % n) : -1;
            bool actor = myIdx == actorIdx, partner = myIdx == partnerIdx;
            if (!actor && !partner) { CurbRun(c, net, me, carrier); return; }
            var other = bodies[actor ? partnerIdx : actorIdx];
            Vector3 m = Arena() + new Vector3((s % 3) * 7f, 0f, 0f);
            m = Ground(m);
            Vector3 x = Vector3.right;
            switch (kind)
            {
                case "pass": Pass(c, net, me, carrier, other, actor, t, m, x); break;
                case "bonk": Bonk(c, net, me, carrier, other, actor, t, m, x); break;
                case "shove": Shove(c, net, me, carrier, other, actor, t, m, x); break;
                case "carry-throw": Carry(c, net, me, carrier, other, actor, t, m, x, false); break;
                case "carry-wriggle": Carry(c, net, me, carrier, other, actor, t, m, x, true); break;
                case "hitch": Hitch(c, net, me, carrier, actor, t); break;
                case "grip-take": Tug(c, net, me, carrier, other, actor, t, m, x, false); break;
                case "grip-rip": Tug(c, net, me, carrier, other, actor, t, m, x, true); break;
            }
        }

        void Act(string kind, string step, bool ok, Dictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object> { { "slot", slot }, { "kind", kind }, { "step", step }, { "ok", ok } };
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            Log("handsbot", d);
        }

        // ---- verbs

        void Pass(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, PlayerBody other, bool actor, float t, Vector3 m, Vector3 x)
        {
            if (actor && t >= 0.5f && Once("grab")) Act("pass", "grab", GrabCurbBag(c, carrier));
            if (actor && t >= 2.0f && !carrier.IsCarrying && Once("grab2")) GrabCurbBag(c, carrier);
            if (t >= 3f && Once("place"))
            {
                if (actor) Teleport(c.Player.gameObject, m + x * 4f, -x);
                else { if (carrier.IsCarrying) DropNow(carrier); Teleport(c.Player.gameObject, m, x); }
            }
            if (actor && t >= 5f && Once("pass"))
            {
                var nc = carrier.Held != null ? net.Get(carrier.Held.NetId) : null;
                bool ok = nc != null && !nc.PendingGrab;
                if (ok) { me.transform.rotation = Quaternion.LookRotation(-x); net.LocalPass(nc, other.ClientId); }
                Act("pass", "throw", ok, new Dictionary<string, object> { { "to", (long)other.ClientId } });
            }
            if (!actor && t >= 7.5f && Once("caught"))
            {
                bool got = carrier.IsCarrying && carrier.Held.NetId != 0;
                Act("pass", "received", got, new Dictionary<string, object> { { "from", (long)other.ClientId } });
            }
            // ...and back: a free throw at the actor, who faces it empty-handed: the thrower's machine detects the catch (§4.5).
            if (!actor && t >= 8.2f && Once("throwback"))
            {
                var held = carrier.Held;
                bool ok = held != null && held.NetId != 0 && (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier));
                if (ok)
                {
                    Vector3 chest = other.transform.position + Vector3.up * 1.1f;
                    me.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(chest - me.transform.position, Vector3.up));
                    carrier.ThrowWithCharge(0.2f);
                    if (held.Body != null && !held.Body.isKinematic)
                    {
                        Vector3 d = chest - held.WorldCenter;
                        float tt = Mathf.Max(0.3f, new Vector2(d.x, d.z).magnitude / 7f);
                        held.Body.linearVelocity = new Vector3(d.x / tt, d.y / tt + 0.5f * 9.81f * tt, d.z / tt);
                    }
                }
                Act("pass", "throwback", ok, new Dictionary<string, object> { { "to", (long)other.ClientId } });
            }
            if (actor && t >= 9.6f && Once("caught"))
                Act("pass", "caught", carrier.IsCarrying, new Dictionary<string, object> { { "from", (long)other.ClientId } });
            if (actor && t >= 10.5f && Once("bank") && carrier.IsCarrying) TossOrDrop(c, carrier);
        }

        void Bonk(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, PlayerBody other, bool actor, float t, Vector3 m, Vector3 x)
        {
            if (actor && t >= 0.5f && Once("grab")) Act("bonk", "grab", GrabCurbBag(c, carrier));
            if (actor && t >= 2.0f && !carrier.IsCarrying && Once("grab2")) GrabCurbBag(c, carrier);
            if (t >= 3f && Once("place"))
            {
                if (actor) Teleport(c.Player.gameObject, m - x * 3f, x);
                else { if (carrier.IsCarrying) DropNow(carrier); Teleport(c.Player.gameObject, m, x); } // facing away: no catch
            }
            if (actor && t >= 5f && Once("throw"))
            {
                var held = carrier.Held;
                bool ok = held != null && held.NetId != 0 && (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier));
                if (ok)
                {
                    Vector3 chest = other.transform.position + Vector3.up * 1.1f;
                    carrier.ThrowWithCharge(1f);
                    if (held.Body != null && !held.Body.isKinematic) held.Body.linearVelocity = (chest - held.WorldCenter).normalized * 12.5f;
                }
                Act("bonk", "throw", ok, new Dictionary<string, object> { { "at", (long)other.ClientId } });
            }
            if (!actor && t >= 6.2f && Once("hit"))
            {
                var st = me.State;
                Act("bonk", "victim", st == BodyState.KnockedOut || st == BodyState.Stunned, new Dictionary<string, object> { { "state", st.ToString() } });
            }
        }

        void Shove(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, PlayerBody other, bool actor, float t, Vector3 m, Vector3 x)
        {
            if (!actor && t >= 0.5f && Once("grab")) Act("shove", "victim-grab", GrabCurbBag(c, carrier));
            if (!actor && t >= 2.0f && !carrier.IsCarrying && Once("grab2")) GrabCurbBag(c, carrier);
            if (t >= 3f && Once("place"))
            {
                if (actor) { if (carrier.IsCarrying) DropNow(carrier); Teleport(c.Player.gameObject, m - x * 1.0f, x); }
                else Teleport(c.Player.gameObject, m, -x);
            }
            if (actor && t >= 4f && Once("shove")) Act("shove", "shove", me.TryShove(net), new Dictionary<string, object> { { "at", (long)other.ClientId } });
            if (!actor && t >= 4.6f && Once("hit"))
                Act("shove", "victim", me.State == BodyState.Stunned || !carrier.IsCarrying, new Dictionary<string, object> { { "state", me.State.ToString() }, { "holding", carrier.IsCarrying } });
        }

        void Carry(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, PlayerBody other, bool actor, float t, Vector3 m, Vector3 x, bool wriggle)
        {
            string k = wriggle ? "carry-wriggle" : "carry-throw";
            if (t >= 1f && Once("place"))
            {
                if (carrier.IsCarrying) DropNow(carrier);
                if (actor) Teleport(c.Player.gameObject, m - x * 1.1f, x);
                else Teleport(c.Player.gameObject, m, -x);
            }
            if (!actor && t >= 2f && Once("flop")) { net.LocalFlop(true); Act(k, "flop", true); }
            if (actor && t >= 4f && Once("pickup")) { net.LocalCarry(other.ClientId); Act(k, "pickup", true, new Dictionary<string, object> { { "target", (long)other.ClientId } }); }
            if (actor && t >= 5f && Once("carrying")) Act(k, "carrying", me.Body.carrying == other.ClientId);
            if (!actor && t >= 5.2f && Once("carried")) Act(k, "carried", me.State == BodyState.Carried);
            if (wriggle && !actor && t >= 5.5f && t < 7.5f && me.State == BodyState.Carried && c.Elapsed >= nextWriggle)
            {
                nextWriggle = c.Elapsed + 0.15f;
                net.LocalWriggle();
                wriggles++;
            }
            if (wriggle && !actor && t >= 8f && Once("free")) Act(k, "free", me.State == BodyState.Normal, new Dictionary<string, object> { { "state", me.State.ToString() } });
            if (!wriggle && actor && t >= 7f && Once("throw"))
            {
                bool ok = me.Body.carrying == other.ClientId;
                if (ok) me.ThrowCarried(net, 0.6f);
                Act(k, "throw", ok);
            }
            if (!wriggle && !actor && t >= 8.6f && Once("landed")) Act(k, "landed", me.State == BodyState.KnockedOut || me.State == BodyState.Normal, new Dictionary<string, object> { { "state", me.State.ToString() } });
            if (actor && t >= 10f && Once("drop") && me.Body.carrying != HandsNet.None) net.LocalDropCarried();
            if (!actor && t >= 10.5f && Once("getup") && me.State == BodyState.Flopped) net.LocalFlop(false);
        }

        void Hitch(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, bool actor, float t)
        {
            HitchStep hs = null;
            float best = float.MaxValue;
            Vector3 a = Arena();
            foreach (var h in HitchStep.All)
            {
                if (h == null || h.NetObject == null || !h.NetObject.IsSpawned) continue;
                float d = (h.transform.position - a).sqrMagnitude;
                if (d < best) { best = d; hs = h; }
            }
            if (hs == null) { if (Once("none")) Act("hitch", "no-truck", false); return; }
            int step = actor ? 0 : 1;
            if (t >= 1f && Once("place"))
            {
                if (carrier.IsCarrying) DropNow(carrier);
                var p = hs.StepPose(step);
                Teleport(c.Player.gameObject, p.position - p.rotation * Vector3.forward * 0.9f, p.rotation * Vector3.forward);
            }
            if (t >= (actor ? 2f : 2.6f) && Once("hop")) { net.LocalHitch(hs, step); Act("hitch", "on", true, new Dictionary<string, object> { { "truck", (long)hs.NetObject.NetworkObjectId }, { "step", step } }); }
            if (t >= 4f && Once("riding")) Act("hitch", "riding", me.State == BodyState.Riding, new Dictionary<string, object> { { "state", me.State.ToString() } });
            if (t >= 8f && Once("off")) { net.LocalUnhitch(); }
            if (t >= 9f && Once("offcheck")) Act("hitch", "off", me.State == BodyState.Normal, new Dictionary<string, object> { { "state", me.State.ToString() } });
        }

        void Tug(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier, PlayerBody other, bool actor, float t, Vector3 m, Vector3 x, bool rip)
        {
            string k = rip ? "grip-rip" : "grip-take";
            if (!actor && t >= 0.5f && Once("grab")) Act(k, "holder-grab", GrabCurbBag(c, carrier));
            if (!actor && t >= 2.0f && !carrier.IsCarrying && Once("grab2")) GrabCurbBag(c, carrier);
            if (t >= 3f && Once("place"))
            {
                if (actor) { if (carrier.IsCarrying) DropNow(carrier); Teleport(c.Player.gameObject, m + x * 1.0f, -x); }
                else Teleport(c.Player.gameObject, m, x);
            }
            if (actor && t >= 4f && Once("snatch"))
            {
                NetCarry target = null;
                foreach (var nc in net.Items.Values) if (nc != null && nc.HeldBy(other.ClientId)) { target = nc; break; }
                if (target != null) net.LocalSnatch(target);
                Act(k, "snatch", target != null, new Dictionary<string, object> { { "from", (long)other.ClientId } });
            }
            if (t >= 9f && Once("after"))
            {
                Act(k, "after", true, new Dictionary<string, object> { { "holding", carrier.IsCarrying }, { "cash", GameSession.CashCents } });
                if (carrier.IsCarrying) DropNow(carrier);
            }
        }

        void Pull(HandsNet net, TugNet t, BotContext c)
        {
            bool rip = Kinds[(int)((long)Math.Floor(t.beat0 / SlotSec) % Kinds.Length)] == "grip-rip";
            if (rip || t.challenger == net.LocalId) GripBotInput.PullAway(c, net, t);
        }

        // ---- curb runs (everyone else)

        void CurbRun(BotContext c, HandsNet net, PlayerBody me, TrashCarrier carrier)
        {
            if (c.Elapsed < nextCurb || me.State != BodyState.Normal) return;
            nextCurb = c.Elapsed + c.Range(2.5f, 4f);
            if (carrier.IsCarrying)
            {
                if (carrier.Held.NetId != 0 && !(TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier))) return;
                TossOrDrop(c, carrier);
                return;
            }
            GrabCurbBag(c, carrier);
        }

        // Teleports are instant here but reach the host ~0.3 s later (interpolation + bad wi-fi): press E a moment after
        // arriving, like a player who walked up.
        TrashBag deferredBag;
        float deferredAt;

        TruckHopper deferredHopper;
        float deferredTossAt;

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
                    curbTosses++;
                }
            }
            if (deferredBag == null || c.Elapsed < deferredAt) return;
            var b = deferredBag;
            deferredBag = null;
            if (carrier.IsCarrying || b == null || b.State != CarryState.Resting) return;
            if (carrier.PickUp(b)) curbPicks++;
        }

        bool GrabCurbBag(BotContext c, TrashCarrier carrier)
        {
            if (carrier.IsCarrying) return true;
            var list = new List<TrashBag>();
            foreach (var stop in PickupStop.All)
            {
                if (stop == null) continue;
                foreach (var b in stop.SpawnedBags)
                    if (b != null && b.State == CarryState.Resting && !b.IsInScriptedFlight && b.IsCurbBag && Permissions.LocalCanWorkAt(b.transform.position)) list.Add(b);
            }
            if (list.Count == 0) return false;
            // Spread the bots over different bags (by player), so a slot's actor isn't racing a curb runner for the same one.
            list.Sort((a, b) => string.CompareOrdinal(a.Contents.bagId, b.Contents.bagId));
            int me = HandsNet.Instance != null ? (int)HandsNet.Instance.LocalId : 0;
            var bag = list[(me * 5 + (int)(c.Value() * 3f)) % list.Count];
            Vector3 back = bag.SourceStop != null ? bag.SourceStop.transform.forward : Vector3.forward;
            Teleport(c.Player.gameObject, bag.transform.position - back * 1.1f, back);
            deferredBag = bag;
            deferredAt = c.Elapsed + 0.6f;
            return true;
        }

        void TossOrDrop(BotContext c, TrashCarrier carrier)
        {
            var held = carrier.Held;
            if (held == null) return;
            var hopper = NearestHopper(c.Player.transform.position);
            if (hopper != null && hopper.CanAccept(held, out _))
            {
                Vector3 back = hopper.TossTargetPosition - hopper.transform.forward * 2.2f;
                Teleport(c.Player.gameObject, Ground(new Vector3(back.x, hopper.TossTargetPosition.y, back.z)), hopper.transform.forward);
                deferredHopper = hopper;
                deferredTossAt = c.Elapsed + 0.6f;
                return;
            }
            carrier.Drop();
        }

        static void DropNow(TrashCarrier carrier)
        {
            if (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier)) carrier.Drop();
        }

        static TruckHopper NearestHopper(Vector3 from)
        {
            TruckHopper best = null;
            float bestD = float.MaxValue;
            foreach (var r in CarryReceivers.All)
            {
                if (!(r is TruckHopper h) || h == null || !h.isActiveAndEnabled) continue;
                if (DepotRowLayout.InteriorAt(h.transform.position) >= 0) continue; // parked inside a shop
                float d = (h.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        void HostChores(BotContext c)
        {
            if (c.Elapsed < nextSupply) return;
            nextSupply = c.Elapsed + 5f;
            if (GameSession.CurbBags.Count < 10) CurbTrashSpawner.DebugPutOut(5);
            if (GameSession.TruckFill01 > 0.75f) GameSession.TakeAllTruckBags();
            foreach (var b in Businesses.All)
            {
                var cargo = Businesses.HostCargo(b.Id);
                if (cargo == null || !Businesses.TryGetTruckLoad(b.Id, out float kg, out float cap) || kg < cap * 0.75f) continue;
                cargo.Clear();
                Businesses.SetTruckLoad(b.Id, 0f, cap);
            }
        }

        void Report(BotContext c, string phase)
        {
            var net = HandsNet.Instance;
            Log("handsbot", new Dictionary<string, object>
            {
                { "phase", phase }, { "curbPicks", curbPicks }, { "curbTosses", curbTosses }, { "paid", paid }, { "paidCents", paidCents },
                { "wriggles", wriggles }, { "reverts", NetCarry.Reverts }, { "items", net != null ? net.Items.Count : -1 },
                { "curbs", GameSession.CurbBags.Count }, { "cashCents", GameSession.CashCents }, { "business", GameSession.BusinessId },
            });
        }
    }
}

/// <summary>
/// <c>shop</c> brain (lead's shop-trash ask): each guest, in its own shop interior, makes a little pile the way a tip does
/// (bags spawned on its own machine), grabs one, rips it open on the spot (the station's Burst), and bins the items in its
/// shop's sort bins (an interior receiver: Accept runs on the guest, the host drops it everywhere). Every machine logs what it
/// sees in each lot (<c>shopview</c>); the hands hash proves everyone holds the same items. The host walks the street.
/// </summary>
sealed class ShopBrain : IBotBrain
{
    float next = 15f, nextView = 10f;
    int step, cycle, made, ripped, binned;
    TrashBag mine;
    readonly IBotBrain walk = BotBrains.Create("walk");

    public void Begin(BotContext c) => walk.Begin(c);
    public void End(BotContext c) { walk.End(c); Log(c, "end"); }

    void Log(BotContext c, string phase, Dictionary<string, object> extra = null)
    {
        var d = new Dictionary<string, object> { { "phase", phase }, { "cycle", cycle }, { "made", made }, { "ripped", ripped }, { "binned", binned }, { "reverts", NetCarry.Reverts } };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        NetAudit.Event("shopbot", d);
    }

    public void Tick(BotContext c, float dt)
    {
        var net = HandsNet.Instance;
        if (net == null || !net.IsSpawned || c.Player == null) return;
        if (c.Elapsed >= nextView)
        {
            nextView = c.Elapsed + 5f;
            var perLot = new int[DepotRowLayout.LotCount];
            int street = 0;
            foreach (var nc in net.Items.Values)
            {
                if (nc == null || nc.Item == null) continue;
                int lot = DepotRowLayout.InteriorAt(nc.Item.transform.position);
                if (lot >= 0) perLot[lot]++; else street++;
            }
            NetAudit.Event("shopview", new Dictionary<string, object> { { "lots", string.Join(",", perLot) }, { "street", street }, { "items", net.Items.Count } });
        }
        if (c.IsHost || !DepotRowWorld.Active) { walk.Tick(c, dt); return; }
        c.Stop();
        int myLot = DepotRowWorld.LocalLot;
        var carrier = TrashCarrier.Local;
        if (myLot < 0 || carrier == null || c.Elapsed < next) return;
        Vector3 o = DepotRowLayout.InteriorOrigin(myLot);
        Vector3 spot = HandsBots.Ground(o + new Vector3(2.5f, 1f, -1.5f));
        switch (step)
        {
            case 0: // a tip's worth of bags, made on this machine
                HandsBots.Teleport(c.Player.gameObject, spot + Vector3.back * 1.2f, Vector3.forward);
                for (int k = 0; k < 2; k++)
                {
                    var bc = new BagContents { bagId = $"shoptest/{net.LocalId}/{cycle}/{k}", foundAt = "bot street", bagMassKg = 0.2f, sackVariant = k };
                    var defs = ItemCatalog.All;
                    for (int j = 0; j < 3 && defs.Count > 0; j++)
                    {
                        var d = defs[(cycle * 7 + k * 3 + j) % defs.Count];
                        bc.items.Add(new ItemInstance { uid = $"st/{net.LocalId}/{cycle}/{k}/{j}", defId = d.id, massKg = 0.5f + j * 0.3f, condition = 1f, foundAt = "bot street" });
                    }
                    var bag = TrashBag.Spawn(bc, spot + new Vector3(k * 0.7f, 0.6f, 0f), Quaternion.identity);
                    if (k == 0) mine = bag;
                    made++;
                }
                Log(c, "made");
                next = c.Elapsed + 3f; step = 1; break;
            case 1: // walk up to one (it's public by now)...
                if (mine != null && mine.State != CarryState.Consumed && !carrier.IsCarrying)
                    HandsBots.Teleport(c.Player.gameObject, mine.transform.position + Vector3.back * 1.0f, Vector3.forward);
                next = c.Elapsed + 0.7f; step = 10; break;
            case 10: // ...and grab it
                if (mine != null && mine.State != CarryState.Consumed && mine.State != CarryState.Carried && !carrier.IsCarrying) carrier.PickUp(mine);
                Log(c, "grab", new Dictionary<string, object> { { "netId", mine != null ? (long)mine.NetId : -1 }, { "holding", carrier.IsCarrying } });
                next = c.Elapsed + 1.5f; step = 2; break;
            case 2: // rip it open on the spot (the station's Burst)
                if (carrier.Held is TrashBag held && (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier)))
                {
                    var b = carrier.ReleaseToScript() as TrashBag;
                    if (b != null) { b.Burst(true); ripped++; }
                }
                Log(c, "ripped");
                next = c.Elapsed + 3f; step = 3; break;
            case 3: // bin the spilled items in this shop's bins
            {
                SortBin bin = null;
                LooseItem item = null;
                foreach (var li in LooseItem.All)
                {
                    if (li == null || li.State != CarryState.Resting || DepotRowLayout.InteriorAt(li.transform.position) != myLot || !(li.Item?.uid ?? "").StartsWith($"st/{net.LocalId}/")) continue;
                    foreach (var r in CarryReceivers.All)
                        if (r is SortBin sb && sb != null && sb.category == li.Category && DepotRowLayout.InteriorAt(sb.TossTargetPosition) == myLot) { bin = sb; break; }
                    if (bin != null) { item = li; break; }
                }
                if (bin != null && item != null && !carrier.IsCarrying)
                {
                    HandsBots.Teleport(c.Player.gameObject, item.transform.position + Vector3.back * 0.9f, Vector3.forward);
                    pendingBin = bin;
                    pendingItem = item;
                }
                Log(c, "bin-pick", new Dictionary<string, object> { { "bin", bin != null }, { "item", item != null } });
                next = c.Elapsed + 0.7f; step = 11; break;
            }
            case 11:
                if (pendingItem != null && pendingItem.State == CarryState.Resting && !carrier.IsCarrying) carrier.PickUp(pendingItem);
                pendingItem = null;
                next = c.Elapsed + 1.2f; step = 4; break;
            case 4:
                if (pendingBin != null && carrier.IsCarrying && (TrashCarrier.NetCanRelease == null || TrashCarrier.NetCanRelease(carrier)))
                {
                    HandsBots.Teleport(c.Player.gameObject, pendingBin.TossTargetPosition - pendingBin.transform.forward * 1.2f + Vector3.down * 0.8f, pendingBin.transform.forward);
                    bool ok = pendingBin.CanAccept(carrier.Held, out string why);
                    if (ok) { carrier.TossInto(pendingBin); binned++; }
                    else carrier.Drop();
                    Log(c, "binned", new Dictionary<string, object> { { "ok", ok }, { "why", why } });
                }
                pendingBin = null;
                next = c.Elapsed + 5f; step = 0; cycle++; break;
        }
    }

    SortBin pendingBin;
    LooseItem pendingItem;
}

/// <summary>Bot input for shared grips. Uses the same camera-relative movement as a real player's stick.</summary>
public static class GripBotInput
{
    public static void Move(BotContext c, Vector3 worldDirection, bool sprint)
    {
        if (c.Input == null || c.Player == null) return;
        var cam = Camera.main;
        float yaw = cam != null ? cam.transform.eulerAngles.y : c.Player.transform.eulerAngles.y;
        var local = Quaternion.Euler(0f, -yaw, 0f) * worldDirection.normalized;
        c.Input.move = new Vector2(local.x, local.z);
        c.Input.sprint = sprint;
    }

    public static void PullAway(BotContext c, HandsNet net, TugNet t)
    {
        var other = PlayerBody.For(t.holder == net.LocalId ? t.challenger : t.holder);
        if (other == null || c.Player == null) return;
        var away = c.Player.transform.position - other.transform.position;
        away.y = 0f;
        Move(c, away, true);
    }
}

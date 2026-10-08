using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pack Leader (calling_pack_leader) and the cheap, safe "your raccoons are out there" visuals.
/// <list type="bullet">
/// <item><b>Companions</b> (L1: one, L2: three): hutch raccoons set to "Take along" trot behind their leader on foot and ride
/// in the truck's hopper while they drive. They are <see cref="RaccoonDoll"/>s that every machine builds for every player
/// from the replicated roster, so everybody sees them; each machine walks its own copies (positions are cosmetic).</item>
/// <item><b>Fetch</b> (host decides): every FetchCooldown per companion, when the leader is on foot with their own truck
/// within 30 m and a resting kerb bag within 14 m, a companion runs it to the truck. It goes through the normal collect
/// path (<see cref="TruckHopper.CollectCurbBag"/>): the leader's business is paid the normal fee, once, and the truck
/// must have room.</item>
/// <item><b>Alpha</b> (L2, host): wild raccoons within 7 m of an Alpha with a companion out back off instead of picking a
/// fight (see also <see cref="RaccoonStanding.Protects"/>: they never bite an Alpha).</item>
/// <item><b>Night helpers</b>: at night, up to two of a player's chipped crew raccoons trot between the kerbs near them,
/// wearing their collars. Purely presentational: the crew's real work is <see cref="RaccoonCrew"/>'s data sweep. Every
/// machine picks the same kerbs from the shared clock, so helpers look alike everywhere without any network traffic.</item>
/// </list>
/// </summary>
public static class PackCompanions
{
    sealed class Member
    {
        public RaccoonDollWalker walker;
        public int id;
        // a fetch run in progress (cosmetic): 0 none, 1 to the bag, 2 to the truck
        public int fetch;
        public Vector3 fetchBag, fetchTruck;
        public float sniffUntil;
    }

    sealed class Squad
    {
        public PlayerController pc;
        public readonly List<Member> companions = new List<Member>();
        public readonly List<Member> helpers = new List<Member>();
        public string key;
        public float nextLook;
    }

    static readonly Dictionary<PlayerController, Squad> squads = new Dictionary<PlayerController, Squad>();
    static readonly List<PlayerController> gone = new List<PlayerController>();

    // host
    sealed class PendingFetch { public string bagId, businessId, raccoon; public ulong client; public Vector3 truck; public float at; }
    static readonly List<PendingFetch> pending = new List<PendingFetch>();
    static readonly Dictionary<ulong, float> nextFetch = new Dictionary<ulong, float>();
    static float nextHostLook;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { squads.Clear(); gone.Clear(); pending.Clear(); nextFetch.Clear(); nextHostLook = 0f; }

    // ------------------------------------------------------------------ every machine: the dolls

    internal static void Tick(float dt)
    {
        var players = NightPlayers.All;
        for (int i = 0; i < players.Count; i++)
        {
            var pc = players[i];
            if (pc == null) continue;
            if (!squads.TryGetValue(pc, out var squad)) squads[pc] = squad = new Squad { pc = pc };
            if (Time.unscaledTime >= squad.nextLook) { squad.nextLook = Time.unscaledTime + 0.5f; Rebuild(squad); }
            Move(squad, dt);
        }
        gone.Clear();
        foreach (var kv in squads) if (kv.Key == null) gone.Add(kv.Key);
        foreach (var dead in gone) { Clear(squads[dead].companions); Clear(squads[dead].helpers); squads.Remove(dead); }
    }

    static void Clear(List<Member> list)
    {
        foreach (var m in list) if (m.walker.doll != null) Object.Destroy(m.walker.doll.gameObject);
        list.Clear();
    }

    static bool Outdoors(PlayerController pc) => pc.isActiveAndEnabled && !NightPlayers.IsIndoors(pc) && !pc.IsPlayingChess;

    static void Rebuild(Squad squad)
    {
        var pc = squad.pc;
        ulong client = NightPlayers.ClientOf(pc);
        var want = new List<HutchRaccoon>();
        var wantHelpers = new List<HutchRaccoon>();
        HutchState hutch = null;
        int brain = 0;
        if (Outdoors(pc) && Hutch.TryBusinessOf(client, out var business))
        {
            hutch = Hutch.For(business.Id);
            int limit = HutchRules.CompanionLimit(Hutch.LevelFor(client, CallingIds.PackLeader));
            foreach (var r in hutch.raccoons)
                if (r.job == (int)HutchJob.Companion && r.Available && want.Count < limit) want.Add(r);
            if (GameClock.IsNight && Hutch.LevelFor(client, CallingIds.CollarChip) > 0 && !pc.IsDriving)
            {
                brain = Hutch.LevelFor(client, CallingIds.BrainChip);
                foreach (var r in hutch.raccoons)
                    if (r.job == (int)HutchJob.Crew && r.Available && wantHelpers.Count < 2) wantHelpers.Add(r);
            }
        }
        string key = (hutch != null ? hutch.collars + ":" : "") + brain + "|";
        foreach (var r in want) key += r.id + "." + r.look + "." + r.variant + ",";
        key += "|";
        foreach (var r in wantHelpers) key += r.id + "." + r.look + "." + r.variant + ",";
        bool lost = false;
        foreach (var m in squad.companions) if (m.walker.doll == null) lost = true;
        foreach (var m in squad.helpers) if (m.walker.doll == null) lost = true;
        if (key == squad.key && !lost) return;
        squad.key = key;
        Clear(squad.companions); Clear(squad.helpers);
        Vector3 at = pc.transform.position;
        for (int i = 0; i < want.Count; i++)
            squad.companions.Add(Spawn(want[i], hutch.collars > 0 ? DollCollar.Gold : DollCollar.Collar, at - pc.transform.forward * (1.2f + i * 0.6f), 0.8f));
        for (int i = 0; i < wantHelpers.Count; i++)
        {
            Vector3 p = at + Quaternion.Euler(0f, 140f + i * 80f, 0f) * pc.transform.forward * 9f;
            p.y = CritterWorld.GroundY(p + Vector3.up * 2f, at.y);
            var m = Spawn(wantHelpers[i], brain > 0 ? DollCollar.Brain : DollCollar.Collar, p, 0.75f);
            m.walker.speed = 3.2f; m.walker.catchUpDistance = 70f;
            squad.helpers.Add(m);
        }
    }

    static Member Spawn(HutchRaccoon r, DollCollar collar, Vector3 at, float scale)
    {
        var doll = RaccoonDoll.Build(null, r, collar, scale);
        doll.transform.position = at;
        Juice.PopIn(doll.transform, 0.25f);
        return new Member { walker = new RaccoonDollWalker(doll) { speed = 7f }, id = r.id };
    }

    static void Move(Squad squad, float dt)
    {
        var pc = squad.pc;
        Vector3 me = pc.transform.position;
        Vector3 back = -CritterWorld.Flat(pc.transform.forward).normalized, right = Vector3.Cross(Vector3.up, -back);
        var truck = pc.IsDriving ? pc.currentTruck : null;
        for (int i = 0; i < squad.companions.Count; i++)
        {
            var m = squad.companions[i];
            var doll = m.walker.doll;
            if (doll == null) continue;
            if (truck != null)
            {
                // riding along in the hopper, like a stowaway who was invited
                doll.transform.SetPositionAndRotation(truck.transform.TransformPoint(Raccoon.StowLocal + new Vector3((i - 1) * 0.42f, 0f, 0f)),
                    truck.transform.rotation * Quaternion.Euler(0f, 180f, 0f));
                doll.speedNow = 0f; m.fetch = 0; doll.Carry(false);
                m.walker.goal = doll.transform.position;
                continue;
            }
            if (m.fetch == 1)
            {
                m.walker.goal = m.fetchBag; m.walker.speed = 14f;
                if (m.walker.Tick(dt, 0.6f)) { m.fetch = 2; doll.Carry(true); Juice.Squash(doll.transform, 0.2f, 0.2f); }
                continue;
            }
            if (m.fetch == 2)
            {
                m.walker.goal = m.fetchTruck; m.walker.speed = 12f;
                if (m.walker.Tick(dt, 1.4f))
                {
                    m.fetch = 0; doll.Carry(false);
                    GameHUD.FloatingText(doll.transform.position + Vector3.up * 0.8f, "fetch!", new Color(0.72f, 0.5f, 1f), 0.9f);
                    Sfx.Play("chitter", doll.transform.position, 0.5f, 1.4f);
                }
                continue;
            }
            m.walker.speed = 7.5f;
            m.walker.goal = me + back * (1.3f + (i / 2) * 0.8f) + right * (i == 0 ? 0.55f : i == 1 ? -0.55f : 0f);
            m.walker.Tick(dt, 0.5f);
        }

        if (squad.helpers.Count == 0) return;
        var stops = CritterWorld.Stops;
        int bucket = (int)System.Math.Floor(GameClock.TotalHours * 60.0 / 12.0); // a new kerb every 12 in-game minutes (10 s)
        for (int i = 0; i < squad.helpers.Count; i++)
        {
            var m = squad.helpers[i];
            if (m.walker.doll == null) continue;
            // the same kerb on every machine: the nearest few stops to the leader, picked by raccoon id and the shared clock
            PickupStop best = null; int pick = (PickupVisuals.StableHash("helper" + m.id + ":" + bucket) & 0x7fffffff) % 3, found = 0;
            float within = 38f * 38f;
            for (int s = 0; s < stops.Count && found <= pick; s++)
            {
                var stop = stops[s];
                if (stop == null || (stop.transform.position - me).sqrMagnitude > within) continue;
                best = stop; found++;
            }
            m.walker.goal = best != null ? best.PileCenter + right * (i == 0 ? 0.7f : -0.7f) : me + back * 6f + right * (i == 0 ? 3f : -3f);
            if (m.walker.Tick(dt, 0.8f) && Time.time >= m.sniffUntil)
            {
                m.sniffUntil = Time.time + NightDice.Cosmetic.Range(2.5f, 5f);
                Juice.Squash(m.walker.doll.transform, 0.12f, 0.2f);
                if (NightDice.Cosmetic.Chance(0.3f)) Sfx.Play("rustle", m.walker.doll.transform.position, 0.3f, 1.3f);
            }
        }
    }

    /// <summary>Every machine: a companion of <paramref name="who"/> runs a bag from the kerb to the truck.</summary>
    internal static void PlayFetch(ulong who, Vector3 bag, Vector3 truck)
    {
        foreach (var kv in squads)
        {
            if (kv.Key == null || NightPlayers.ClientOf(kv.Key) != who) continue;
            foreach (var m in kv.Value.companions)
            {
                if (m.fetch != 0 || m.walker.doll == null) continue;
                m.fetch = 1; m.fetchBag = bag; m.fetchTruck = truck;
                return;
            }
        }
    }

    // ------------------------------------------------------------------ host: fetch and deference

    internal static void HostTick()
    {
        if (GameSession.IsGuest) return;
        for (int i = pending.Count - 1; i >= 0; i--)
            if (Time.time >= pending[i].at) { var p = pending[i]; pending.RemoveAt(i); FinishFetch(p); }
        if (Time.unscaledTime < nextHostLook) return;
        nextHostLook = Time.unscaledTime + 0.5f;
        var players = NightPlayers.All;
        for (int i = 0; i < players.Count; i++)
        {
            var pc = players[i];
            if (pc == null || !Outdoors(pc) || pc.IsDriving) continue;
            ulong client = NightPlayers.ClientOf(pc);
            int pack = Hutch.LevelFor(client, CallingIds.PackLeader);
            if (pack <= 0 || !Hutch.TryBusinessOf(client, out var business)) continue;
            var hutch = Hutch.For(business.Id);
            int limit = HutchRules.CompanionLimit(pack), out_ = 0;
            string first = null;
            foreach (var r in hutch.raccoons)
                if (r.job == (int)HutchJob.Companion && r.Available && out_ < limit) { out_++; first ??= r.name; }
            if (out_ == 0) continue;
            if (pack >= 2) Defer(pc.transform.position, client);
            if (NightPlayers.IsRiding(pc)) continue;
            if (!nextFetch.TryGetValue(client, out float due)) { nextFetch[client] = Time.time + RaccoonTuning.FetchCooldown / out_; continue; }
            if (Time.time < due) continue;
            if (StartFetch(pc, client, business, first)) nextFetch[client] = Time.time + RaccoonTuning.FetchCooldown / out_;
            else nextFetch[client] = Time.time + 2f; // nothing to fetch right here: look again soon
        }
    }

    static void Defer(Vector3 at, ulong client)
    {
        var critters = new List<Critter>(Critter.All);
        foreach (var c in critters)
        {
            if (!(c is Raccoon r) || r == null || r.Despawning || r.NetPuppet) continue;
            if (CritterWorld.Flat(r.transform.position - at).sqrMagnitude > RaccoonTuning.PackDeferRadius * RaccoonTuning.PackDeferRadius) continue;
            r.Befriend(at, RaccoonTuning.PackDeferSeconds, client, true);
        }
    }

    static TruckControllerV2 TruckOf(BusinessInfo business, Vector3 near, out TruckHopper hopper)
    {
        hopper = null;
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null || (t.transform.position - near).sqrMagnitude > RaccoonTuning.FetchTruckRadius * RaccoonTuning.FetchTruckRadius) continue;
            var h = t.GetComponentInChildren<TruckHopper>();
            if (h == null || h.ResolveBusiness() != business.Id) continue;
            hopper = h; return t;
        }
        return null;
    }

    static bool Fits(string businessId, float kg) =>
        businessId == Businesses.LocalId ? GameSession.CanFitInTruck(kg)
        : !Businesses.TryGetTruckLoad(businessId, out float load, out float cap) || load + kg <= cap + 0.001f;

    static bool StartFetch(PlayerController pc, ulong client, BusinessInfo business, string raccoon)
    {
        Vector3 me = pc.transform.position;
        var truck = TruckOf(business, me, out var hopper);
        if (truck == null || CritterWorld.VelocityOf(truck).magnitude > 1.5f) return false;
        CurbBag best = null; float bestD = RaccoonTuning.FetchBagRadius * RaccoonTuning.FetchBagRadius; Vector3 bestAt = default;
        bool local = business.Id == Businesses.LocalId;
        foreach (var rec in GameSession.CurbBags)
        {
            if (rec == null || rec.contents == null) continue;
            var stop = PickupStop.Find(rec.stopId);
            if (stop == null) continue;
            float d = (stop.PileCenter - me).sqrMagnitude;
            if (d >= bestD || RaccoonCrew.HeldByAPlayer(rec) || !Fits(business.Id, rec.contents.TotalMassKg)) continue;
            var district = Districts.ForStopId(rec.stopId);
            if (!(local ? Districts.IsUnlocked(district) : BusinessUpgradeNet.HasPermitForBusiness(business.Id, district))) continue;
            bool queued = false;
            foreach (var p in pending) if (p.bagId == rec.bagId) { queued = true; break; }
            if (queued) continue;
            best = rec; bestD = d; bestAt = stop.PileCenter;
        }
        if (best == null) return false;
        Vector3 rear = hopper.transform.position;
        pending.Add(new PendingFetch { bagId = best.bagId, businessId = business.Id, client = client, truck = rear, at = Time.time + RaccoonTuning.FetchRunSeconds, raccoon = raccoon });
        NightNet.RaccoonFx(RaccoonCalling.FxFetch, client, bestAt, rear);
        return true;
    }

    /// <summary>The companion reached the bag: collect it into the leader's truck through the one collect path.</summary>
    static void FinishFetch(PendingFetch p)
    {
        var rec = GameSession.FindCurbBag(p.bagId);
        if (rec == null || rec.contents == null || RaccoonCrew.HeldByAPlayer(rec) || !Businesses.TryGet(p.businessId, out _)) return;
        if (!Fits(p.businessId, rec.contents.TotalMassKg)) return;
        float pay = TruckHopper.CollectCurbBag(rec.contents, p.businessId == Businesses.LocalId ? null : p.businessId, p.truck, false, rec.stopId, false, out _);
        NightNet.Say(p.truck + Vector3.up * 2.2f, $"{p.raccoon ?? "Fetch"}: +{Economy.Money(pay)}", 4);
        NightAudit.Event("pack", new Dictionary<string, object> { { "op", "fetch" }, { "client", (long)p.client }, { "bag", p.bagId }, { "cents", Mathf.RoundToInt(pay * 100f) } });
    }
}

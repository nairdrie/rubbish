using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §7.2): residents carry the trash out. Registered as
/// <see cref="PickupStop.PutOutPresenter"/> while WorldLife is awake. When a house puts a bag out, a household member pops
/// out of the front door hugging it, waddles down the garden path (the street graph), sets it in its curb slot, has a
/// beat (waves at a truck within 30 m, glares at a neglected pile, or stretches) and goes back in.
///
/// <para><b>The curb record is still the only truth.</b> The walk is a pure function of the record's own timestamp,
/// quantised to the in-game minute (T = floor(putOutAtHours × 60) / 60), and the house's route, so the host and every
/// guest draw the same walk with zero traffic. The real <see cref="TrashBag"/> spawns in its slot at set-down
/// (<see cref="PickupStop.MaterializeRecord"/>); the record exists, counts and ages from T the whole time.</para>
///
/// <para>Fallbacks (today's drop-in): the house is more than 120 m from the camera, 16 walk-outs are already running,
/// the house has no walkable route ≤ 45 m, nobody is free in the household, or the record is older than its set-down
/// time (shown on scene load mid-walk otherwise). Billionaire Bluffs uses the butler's cloche (<see cref="ButlerPosts"/>).</para>
///
/// <para>Interruptions: "Take bag" (E) hands the same bagId to the player (+1 Rep, neighborly); flattened → the bag(s)
/// fly out of their arms as resting bags (never burst; same bagId); the record vanishes (a raccoon crew's sweep, another player)
/// → a shrug, "Oh. Okay then."; a raccoon within 6 m at night → "RACCOON!", the bag drops on the spot and they sprint
/// back in. Night walk-outs come in pajamas with a flashlight.</para>
/// </summary>
public sealed class WalkOutPresenter
{
    public const int MaxJobs = 16;
    public const float PopSeconds = 0.45f, PopInSeconds = 0.4f, MaterializeAt = 0.6f;
    const float SetDown = WorldLifeTuning.Walk.SetDownSeconds, Beat = WorldLifeTuning.Walk.BeatSeconds;

    /// <summary>Package D can pick who carries the bag (the member home at T; the commuter on the way to the car). Return a
    /// member index (0..count−1) or −1 for "nobody home". Null = a hashed household member.</summary>
    public static Func<int, double, int> CarrierPicker;

    /// <summary>Package E: does this house (stopId) hold a grudge against the garbage trucks right now? Then the beat after
    /// the set-down is a glare at the truck instead of a wave. Null = no grudges.</summary>
    public static Func<string, bool> Grudged;

    public enum Mode : byte { Scripted, Interrupted, Shrug, Returning, Done }
    public enum Phase : byte { PopOut, Walk, SetDown, Beat, WalkBack, PopIn, End }

    public sealed class Job
    {
        public int house;
        public PickupStop stop;
        public string stopId;
        public CurbBag rec0, rec1;
        public bool done0, done1;
        public double T;
        public LifeId who;
        public ResidentAgent agent;
        public readonly WalkPath path = new WalkPath();
        public readonly WalkPath back = new WalkPath();
        public float tWalk, tBack, pause, localT;
        public Mode mode;
        public Phase phase;
        public bool night, panic, saidHello, beatPicked, materializedLate;
        public PersonPose beat;
        public Vector3 standPos, lookAt;
        public float standYaw;
        public Transform sack0, sack1;
        public double createdAtHours;
        public int handOvers;

        public bool HasBag0 => rec0 != null && !done0;
        public bool HasBag1 => rec1 != null && !done1;
        public bool Carrying => HasBag0 || HasBag1;
        public int BagsLeft => (HasBag0 ? 1 : 0) + (HasBag1 ? 1 : 0);
        /// <summary>Real seconds from T to the set-down moment.</summary>
        public float SetDownAt => PopSeconds + tWalk + MaterializeAt * SetDown;
    }

    readonly ResidentDirector dir;
    readonly List<Job> jobs = new List<Job>(MaxJobs);
    readonly Stack<Job> free = new Stack<Job>(MaxJobs);
    bool recordsDirty;
    float nextVanishCheck, nextRaccoonCheck;

    // ---- stats (acceptance evals)
    public int Offered { get; private set; }
    public int Accepted { get; private set; }
    public int DeclinedFar { get; private set; }
    public int DeclinedOld { get; private set; }
    public int DeclinedBusy { get; private set; }
    public int Materialized { get; private set; }
    public int HandOvers { get; private set; }
    public int Flung { get; private set; }
    public int Shrugs { get; private set; }
    public int Panics { get; private set; }
    public IReadOnlyList<Job> Jobs => jobs;
    /// <summary>Last walk-out started (tests): bagId → time the resident first showed.</summary>
    public readonly Dictionary<string, float> FirstShownAt = new Dictionary<string, float>();
    public readonly Dictionary<string, float> MaterializedAt = new Dictionary<string, float>();
    public readonly Dictionary<string, string> FirstShownInfo = new Dictionary<string, string>();

    public WalkOutPresenter(ResidentDirector dir) { this.dir = dir; }

    public void OnCurbTrashChanged() => recordsDirty = true;

    // ------------------------------------------------------------------ the PickupStop hook

    public bool Offer(PickupStop stop, CurbBag rec)
    {
        Offered++;
        var ctx = dir.Ctx;
        if (ctx == null || stop == null || rec == null) return false;
        var g = ctx.Graph;
        int house = g.HouseIndex(stop.stopId);
        if (house < 0) return false;
        var district = stop.District;
        if (district != null && district.butlerDrops) return dir.Butlers.Offer(stop, rec, house);

        double T = Math.Floor(rec.putOutAtHours * 60.0) / 60.0;
        // A second bag from the same put-out joins the first walk (one per hand).
        for (int i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (j.house == house && j.T == T && j.mode == Mode.Scripted && j.rec1 == null && j.HasBag0 && j.phase <= Phase.Walk)
            {
                j.rec1 = rec; j.done1 = false;
                RebuildStand(j);
                return true;
            }
        }
        Vector3 viewer = ctx.Viewer;
        Vector3 slotPos = g.nodes[g.houses[house].slot].pos;
        if ((slotPos - viewer).sqrMagnitude > WorldLifeTuning.Lod.WalkOutDistance * WorldLifeTuning.Lod.WalkOutDistance) { DeclinedFar++; return false; }
        if (jobs.Count >= MaxJobs) { DeclinedBusy++; return false; }

        var nodes = HouseRoutes.DoorToCurb(g, house);
        if (nodes == null || nodes.Length == 0) return false;

        var job = free.Count > 0 ? free.Pop() : new Job();
        job.house = house; job.stop = stop; job.stopId = stop.stopId; job.rec0 = rec; job.rec1 = null; job.done0 = job.done1 = false;
        job.T = T; job.agent = null; job.pause = 0f; job.localT = 0f; job.mode = Mode.Scripted; job.phase = Phase.PopOut;
        job.panic = job.saidHello = job.beatPicked = job.materializedLate = false; job.sack0 = job.sack1 = null; job.handOvers = 0;
        job.createdAtHours = ctx.Hours;
        float hr = (float)(T - Math.Floor(T / 24.0) * 24.0);
        job.night = hr >= GameClock.NightStart || hr < GameClock.DawnStart;
        job.path.Clear();
        for (int i = 0; i < nodes.Length; i++) job.path.Add(g.nodes[nodes[i]].pos);
        RebuildStand(job);
        if (job.path.Length > HouseRoutes.MaxRouteMetres) { free.Push(job); return false; }

        float elapsed = (float)((ctx.Hours - T) * GameClock.SecondsPerGameHour);
        if (elapsed >= job.SetDownAt) { free.Push(job); DeclinedOld++; return false; }

        int member = PickCarrier(house, T);
        if (member < 0) { free.Push(job); DeclinedBusy++; return false; }
        job.who = LifeId.Resident(ResidentCast.HouseholdOf(house).first + member);
        // They're C's puppet for the walk: package D's timetable doesn't draw them meanwhile.
        float total = PopSeconds + job.tWalk + SetDown + Beat + job.tBack + PopInSeconds + 5f;
        PedestrianFeed.Suppress(job.who, T + total / GameClock.SecondsPerGameHour);
        jobs.Add(job);
        Accepted++;
        return true;
    }

    /// <summary>Where the resident stands to set the bag(s) down: behind the slot(s), on the house side, facing the road.</summary>
    void RebuildStand(Job j)
    {
        var stop = j.stop;
        bool heap = stop.IsNeglected;
        Vector3 a = stop.SlotPosition(j.rec0 != null ? j.rec0.slot : 0, heap);
        Vector3 b = j.rec1 != null ? stop.SlotPosition(j.rec1.slot, heap) : a;
        Vector3 fwd = stop.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        j.standPos = (a + b) * 0.5f - fwd * 0.6f;
        j.standYaw = PuppetKit.YawOf(fwd);
        j.lookAt = (a + b) * 0.5f;
        // Re-end the path at the stand point (drop a previous stand point first).
        var g = dir.Ctx.Graph;
        var nodes = HouseRoutes.DoorToCurb(g, j.house);
        j.path.Clear();
        for (int i = 0; i < nodes.Length; i++) j.path.Add(g.nodes[nodes[i]].pos);
        j.path.Add(j.standPos);
        j.tWalk = j.path.Length / WorldLifeTuning.Walk.Carrying;
        j.tBack = j.path.Length / WorldLifeTuning.Walk.Waddle;
    }

    int PickCarrier(int house, double T)
    {
        var hh = ResidentCast.HouseholdOf(house);
        if (hh.count == 0) return -1;
        var pick = CarrierPicker;
        if (pick != null)
        {
            int m = -1;
            try { m = pick(house, T); } catch (Exception e) { Debug.LogException(e); }
            if (m >= 0 && m < hh.count && !dir.IsBusy(LifeId.Resident(hh.first + m)) && !PedestrianFeed.IsOut(LifeId.Resident(hh.first + m))) return m;
            if (m < 0) return -1;
        }
        int start = LifeHash.Range(LifeHash.U64(ResidentCast.Seed, LifeKind.Resident, house, (int)Math.Floor(T * 60.0), 0x7A5), 0, hh.count);
        for (int k = 0; k < hh.count; k++)
        {
            int m = (start + k) % hh.count;
            var arche = ResidentCast.Get(hh.first + m).archetype;
            if (arche == ResidentArchetype.Butler) continue;
            var id = LifeId.Resident(hh.first + m);
            if (!dir.IsBusy(id) && !PedestrianFeed.IsOut(id)) return m;
        }
        return -1;
    }

    // ------------------------------------------------------------------ tick

    public void Tick(WorldLifeContext ctx, float dt)
    {
        float now = Time.time;
        bool vanishCheck = recordsDirty || now >= nextVanishCheck;
        if (vanishCheck) { recordsDirty = false; nextVanishCheck = now + 0.5f; }
        bool raccoonCheck = now >= nextRaccoonCheck;
        if (raccoonCheck) nextRaccoonCheck = now + 0.5f;

        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            var j = jobs[i];
            if (j.stop == null) { Finish(i, false); continue; }   // the scene went away
            if (vanishCheck) CheckVanished(j);
            switch (j.mode)
            {
                case Mode.Scripted: TickScripted(ctx, j, dt, raccoonCheck); break;
                case Mode.Interrupted: TickInterrupted(j); break;
                case Mode.Shrug: TickShrug(j, dt); break;
                case Mode.Returning: TickReturning(j, dt); break;
            }
            if (j.mode == Mode.Done) Finish(i, true);
        }
    }

    void TickScripted(WorldLifeContext ctx, Job j, float dt, bool raccoonCheck)
    {
        var a = j.agent;
        // Mild interruptions (a horn startle, a nudge) hold the scripted clock: set-down waits for them.
        if (a != null && a.Interrupted) j.pause += dt;
        float t = (float)((ctx.Hours - j.T) * GameClock.SecondsPerGameHour) - j.pause;
        Vector3 door = j.path.Start;
        float tPop = PopSeconds, tWalkEnd = tPop + j.tWalk, tSetEnd = tWalkEnd + SetDown, tBeatEnd = tSetEnd + Beat,
              tBackEnd = tBeatEnd + j.tBack, tEnd = tBackEnd + PopInSeconds;
        Vector3 p; float yaw; PersonPose pose; float poseT = 0f, pop = 1f;
        Vector3 dirv;

        // Catch-up: the set-down time passed (a clock jump, a long interruption): the bag is in its slot.
        if (t >= tWalkEnd + MaterializeAt * SetDown && j.Carrying) { MaterializeAll(j, null, Vector3.zero); j.materializedLate = t > tSetEnd + 0.5f; }

        if (t < tPop)
        {
            j.phase = Phase.PopOut;
            p = door; j.path.At(0f, out dirv); yaw = PuppetKit.YawOf(dirv);
            pop = Mathf.Clamp01(t / tPop);
            pose = CarryPose(j);
        }
        else if (t < tWalkEnd)
        {
            j.phase = Phase.Walk;
            p = j.path.At((t - tPop) * WorldLifeTuning.Walk.Carrying, out dirv);
            yaw = PuppetKit.YawOf(dirv);
            pose = CarryPose(j);
        }
        else if (t < tSetEnd)
        {
            j.phase = Phase.SetDown;
            p = j.standPos; yaw = j.standYaw;
            poseT = (t - tWalkEnd) / SetDown;
            pose = j.Carrying || poseT < 1f ? PersonPose.SetDown : PersonPose.Stand;
            if (poseT >= MaterializeAt && j.Carrying) MaterializeAll(j, null, Vector3.zero);
        }
        else if (t < tBeatEnd)
        {
            j.phase = Phase.Beat;
            p = j.standPos; yaw = j.standYaw;
            if (!j.beatPicked) PickBeat(ctx, j);
            pose = j.beat;
            poseT = (t - tSetEnd) / Beat;
        }
        else if (t < tBackEnd)
        {
            j.phase = Phase.WalkBack;
            p = j.path.At(j.path.Length - (t - tBeatEnd) * WorldLifeTuning.Walk.Waddle, out dirv);
            yaw = PuppetKit.YawOf(-dirv);
            pose = PersonPose.Walk;
        }
        else if (t < tEnd)
        {
            j.phase = Phase.PopIn;
            p = door; j.path.At(0f, out dirv); yaw = PuppetKit.YawOf(-dirv);
            pop = 1f - Mathf.Clamp01((t - tBackEnd) / PopInSeconds);
            pose = PersonPose.Stand;
        }
        else { j.phase = Phase.End; j.mode = Mode.Done; return; }

        if (t < 0f) { pop = 0f; pose = PersonPose.Inside; }
        a = Show(j, p, yaw, pose, poseT, pop);
        if (a == null) return;
        if (j.phase == Phase.Beat && j.beat != PersonPose.Stretch) a.Look(j.lookAt);
        SyncSacks(j, a);

        if (!j.saidHello && j.phase == Phase.Walk && a.rig != null && !a.lite)
        {
            j.saidHello = true;
            KnockSfx.Play(WorldLifeIds.Sfx.DoorCreak, "door", door + Vector3.up, 0.45f, 1f);
            if (dir.NearCamera(p, 30f)) dir.Say(a, j.night ? ResidentLines.NightBag : ResidentLines.Morning, ResidentLines.Chat, false);
        }
        if (a.rig != null && !FirstShownAt.ContainsKey(j.rec0.bagId))
        {
            FirstShownAt[j.rec0.bagId] = Time.time;
            FirstShownInfo[j.rec0.bagId] = $"{j.phase} at {Vector3.Distance(a.pos, j.path.Start):F2} m from the door, {(a.lite ? "lite" : "full")} puppet, pop {a.pop:F2}, who {j.who} ({a.style.archetype}), route {j.path.Length:F1} m";
        }

        // A raccoon at night: "RACCOON!", the bag drops on the spot and they sprint back in.
        if (raccoonCheck && j.night && j.Carrying && j.phase == Phase.Walk && RaccoonNear(a.pos, WorldLifeTuning.Walk.RaccoonPanicDistance))
        {
            Panics++;
            Vector3 at = (j.sack0 != null ? j.sack0.position : a.pos + Vector3.up * 0.5f) + Vector3.up * 0.1f;
            MaterializeAll(j, at, a.Rotation * new Vector3(0f, 1.2f, 1.2f));
            dir.Say(a, ResidentLines.Raccoon, ResidentLines.Yell, true);
            a.KickEyes(6f);
            StartReturn(j, a, true);
        }
    }

    PersonPose CarryPose(Job j) => j.BagsLeft >= 2 ? PersonPose.CarryTwo : j.Carrying ? PersonPose.Carry : PersonPose.Walk;

    void PickBeat(WorldLifeContext ctx, Job j)
    {
        j.beatPicked = true;
        var a = j.agent;
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var tr = trucks[i];
            if (tr == null || (tr.transform.position - j.standPos).sqrMagnitude > WorldLifeTuning.Walk.WaveDistance * WorldLifeTuning.Walk.WaveDistance) continue;
            j.lookAt = tr.transform.position;
            var grudged = Grudged;
            if (grudged != null && grudged(j.stopId))
            {
                j.beat = PersonPose.Glare;
                if (a != null && dir.NearCamera(j.standPos, 35f)) dir.Say(a, ResidentLines.Grudge, ResidentLines.Hey, false);
                return;
            }
            j.beat = PersonPose.Wave;
            if (a != null && dir.NearCamera(j.standPos, 35f)) dir.Say(a, ResidentLines.Wave, ResidentLines.Happy, false);
            return;
        }
        if (j.stop.IsNeglected)
        {
            j.beat = PersonPose.Glare; j.lookAt = j.stop.PileCenter;
            if (a != null && dir.NearCamera(j.standPos, 35f)) dir.Say(a, ResidentLines.Glare, ResidentLines.Hey, false);
            return;
        }
        j.beat = PersonPose.Stretch;
    }

    void TickInterrupted(Job j)
    {
        var a = j.agent;
        if (a == null) { j.mode = Mode.Done; return; }
        Show(j, a.pos, a.yaw, PersonPose.Stand, 0f, 1f);   // keep the agent alive; its act draws it
        SyncSacks(j, a);
        if (!a.Interrupted) StartReturn(j, a, false);
    }

    void TickShrug(Job j, float dt)
    {
        var a = j.agent;
        j.localT += dt;
        if (a == null) { j.mode = Mode.Done; return; }
        Show(j, a.reqPos, a.reqYaw, PersonPose.Shrug, 0f, 1f);
        SyncSacks(j, a);
        if (j.localT >= 1.4f) StartReturn(j, a, false);
    }

    void TickReturning(Job j, float dt)
    {
        var a = j.agent;
        if (a == null) { j.mode = Mode.Done; return; }
        if (a.Interrupted) { Show(j, a.pos, a.yaw, PersonPose.Stand, 0f, 1f); return; }   // startled on the way home
        j.localT += dt;
        float speed = j.panic ? 3.3f : WorldLifeTuning.Walk.Waddle;
        float s = j.localT * speed;
        Vector3 dirv;
        Vector3 door = j.back.End;
        if (s < j.back.Length)
        {
            Vector3 p = j.back.At(s, out dirv);
            Show(j, p, PuppetKit.YawOf(dirv), j.panic ? PersonPose.Panic : CarryPose(j), 0f, 1f);
        }
        else
        {
            float k = (s - j.back.Length) / speed / PopInSeconds;
            j.back.At(j.back.Length, out dirv);
            Show(j, door, PuppetKit.YawOf(dirv), PersonPose.Stand, 0f, 1f - Mathf.Clamp01(k));
            if (k >= 1f) j.mode = Mode.Done;
        }
        SyncSacks(j, a);
    }

    /// <summary>Walk home from wherever the agent is (after a hand-over, a flop, a shrug, a raccoon).</summary>
    void StartReturn(Job j, ResidentAgent a, bool panic)
    {
        j.panic = panic;
        j.localT = 0f;
        float s = j.path.Project(a.pos);
        j.back.BuildReturn(j.path, s, a.pos);
        if (j.back.Count < 2) j.back.Add(j.path.Start);
        j.mode = Mode.Returning;
        Vector3 dirv;
        j.back.At(0f, out dirv);
        a.Request(a.pos, PuppetKit.YawOf(dirv), panic ? PersonPose.Panic : PersonPose.Walk, 0f, Time.frameCount);
    }

    ResidentAgent Show(Job j, Vector3 p, float yaw, PersonPose pose, float poseT, float pop)
    {
        var a = dir.ShowInternal(j.who, AgentOwner.WalkOut, j, j.stopId, p, yaw, pose, poseT, pop, j.night, false);
        j.agent = a;
        return a;
    }

    // ------------------------------------------------------------------ sacks (cosmetic copies of the real bags)

    void SyncSacks(Job j, ResidentAgent a)
    {
        bool shown = a != null && a.rig != null && a.act != ResidentAgent.Act.Flop;
        bool two = j.BagsLeft >= 2;
        Sack(ref j.sack0, j.HasBag0 && shown, j.rec0, a, two ? 0 : -1);
        Sack(ref j.sack1, j.HasBag1 && shown, j.rec1, a, two ? 1 : -1);
        if (a != null) { a.sack0 = j.sack0 != null ? j.sack0 : j.sack1; a.sack1 = two ? j.sack1 : null; }
    }

    /// <summary>hand: −1 = hugged at the belly, 0 = left hand, 1 = right hand.</summary>
    static void Sack(ref Transform sack, bool want, CurbBag rec, ResidentAgent a, int hand)
    {
        if (!want)
        {
            if (sack != null) { UnityEngine.Object.Destroy(sack.gameObject); sack = null; }
            return;
        }
        Transform parent = hand < 0 ? a.rig.carry : hand == 0 ? a.rig.handL : a.rig.handR;
        if (sack == null)
        {
            var holder = PuppetKit.Node(parent, "CarriedSack");
            var model = TrashBag.BuildSackVisual(rec.contents, holder);
            foreach (var r in model.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            sack = holder;
        }
        if (sack.parent != parent) sack.SetParent(parent, false);
        if (hand < 0) { sack.localPosition = Vector3.zero; sack.localRotation = Quaternion.identity; sack.localScale = Vector3.one * 0.92f; }
        else { sack.localPosition = new Vector3(0f, -0.52f, 0.06f); sack.localRotation = Quaternion.identity; sack.localScale = Vector3.one * 0.8f; }
    }

    // ------------------------------------------------------------------ bags

    /// <summary>Spawn the real bag(s) still in the resident's arms: in the slot (at == null) or flying from a point.</summary>
    void MaterializeAll(Job j, Vector3? at, Vector3 vel)
    {
        if (j.HasBag0) MaterializeOne(j, j.rec0, at, vel, 0);
        if (j.HasBag1) MaterializeOne(j, j.rec1, at.HasValue ? at.Value + Vector3.up * 0.35f : (Vector3?)null, vel * 0.9f + Vector3.up * 0.5f, 1);
        j.done0 = j.rec0 != null; j.done1 = j.rec1 != null;
        if (j.agent != null) SyncSacks(j, j.agent);
    }

    void MaterializeOne(Job j, CurbBag rec, Vector3? at, Vector3 vel, int which)
    {
        if (j.stop == null) return;
        var bag = j.stop.MaterializeRecord(rec, at, vel, false);
        if (bag == null) return;
        Materialized++;
        MaterializedAt[rec.bagId] = Time.time;
        if (at == null)
        {
            Vector3 p = bag.transform.position;
            Fx.Dust(p + Vector3.up * 0.05f, 0.45f);
            Sfx.Play("bag_land", p, 0.5f, 1.05f);
        }
        else Flung++;
        if (which == 0) j.done0 = true; else j.done1 = true;
    }

    void CheckVanished(Job j)
    {
        if (j.mode != Mode.Scripted || !j.Carrying) return;
        bool lost0 = j.HasBag0 && (!RecordAlive(j.rec0) || !j.stop.IsPresenting(j.rec0.bagId));
        bool lost1 = j.HasBag1 && (!RecordAlive(j.rec1) || !j.stop.IsPresenting(j.rec1.bagId));
        if (lost0) j.done0 = true;
        if (lost1) j.done1 = true;
        if ((lost0 || lost1) && !j.Carrying && j.agent != null && j.phase <= Phase.SetDown)
        {
            Shrugs++;
            j.mode = Mode.Shrug; j.localT = 0f;
            SyncSacks(j, j.agent);
            dir.Say(j.agent, ResidentLines.Shrug, ResidentLines.Chat, true);
        }
    }

    static bool RecordAlive(CurbBag rec)
    {
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++) if (ReferenceEquals(bags[i], rec)) return true;
        return false;
    }

    static bool RaccoonNear(Vector3 p, float r)
    {
        var all = Critter.All;
        float r2 = r * r;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c is Raccoon && c != null && !c.Despawning && (c.transform.position - p).sqrMagnitude < r2) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ interruptions (from the director)

    public void OnHit(Job j, ResidentAgent a, in NpcHit h)
    {
        if (j.mode == Mode.Done) return;
        if (h.kind == NpcHitKind.Flatten || h.kind == NpcHitKind.ShortFlop)
        {
            // The bag flies out of their arms and lands where physics puts it: a resting bag (never bursts), same bagId.
            if (j.Carrying)
            {
                Vector3 at = j.sack0 != null ? j.sack0.position + Vector3.up * 0.15f : a.pos + Vector3.up * 0.9f;
                Vector3 v = h.launch * 0.7f; v.y = Mathf.Max(v.y * 0.6f, 2.5f);
                MaterializeAll(j, at, v);
            }
            j.mode = Mode.Interrupted;
        }
    }

    public void OnActEnded(Job j, ResidentAgent a)
    {
        if (j.mode == Mode.Interrupted) StartReturn(j, a, false);
    }

    // ------------------------------------------------------------------ hand-over ("Take bag")

    /// <summary>Can this agent hand a bag over right now (carrying, still on the way to the curb, host)?</summary>
    public bool CanHandOver(Job j) =>
        j != null && j.mode == Mode.Scripted && j.Carrying && (j.phase == Phase.Walk || j.phase == Phase.PopOut) && !GameSession.IsGuest
        && j.agent != null && !j.agent.Interrupted;

    /// <summary>"Take bag" (§7.2): the same bagId spawns in the player's hands; +1 Rep (neighborly, capped) on the host.</summary>
    public bool HandOver(Job j, TrashCarrier carrier)
    {
        if (!CanHandOver(j) || carrier == null || carrier.IsCarrying) return false;
        var a = j.agent;
        var rec = j.HasBag0 ? j.rec0 : j.rec1;
        Transform sack = j.HasBag0 ? (j.sack0 != null ? j.sack0 : j.sack1) : j.sack1;
        Vector3 at = sack != null ? sack.position : a.pos + Vector3.up * 0.6f;
        var bag = j.stop.MaterializeRecord(rec, at, Vector3.zero, true);
        if (bag == null) return false;
        if (rec == j.rec0) j.done0 = true; else j.done1 = true;
        carrier.PickUp(bag);
        HandOvers++; j.handOvers++;
        Materialized++;
        MaterializedAt[rec.bagId] = Time.time;
        SyncSacks(j, a);
        dir.Say(a, ResidentLines.Thanks, ResidentLines.Happy, true);
        a.React(PersonPose.Wave, 1.2f);
        Fx.Burst(at + Vector3.up * 0.3f, ResidentLines.Happy, 8, 2.5f);
        Sfx.Play("sparkle", at, 0.5f, 1.3f);

        var fact = new HandOverFact { resident = j.who, bagId = rec.bagId, stopId = j.stopId, pos = a.pos, actorSlot = 0 };
        WorldLifeEvents.RaiseBagHandedOver(in fact);
        var auth = WorldLifeRunner.Authority;
        auth.ClaimHandOver(in fact);
        // Interim until package E's authority lands: the no-op authority keeps no books, so raise the Rep fact here
        // (Progression caps it at 10 a day). E's ClaimHandOver replaces this automatically.
        if (auth is NullAuthority && !GameSession.IsGuest)
            ProgressionEvents.RaiseNeighborIncident(new NeighborIncidentFact
            {
                kind = NeighborIncidentKind.Neighborly, stopId = j.stopId, districtId = j.stop.District.id, pos = a.pos, escalation = 1f,
            });
        if (!j.Carrying) StartReturn(j, a, false);
        return true;
    }

    // ------------------------------------------------------------------ end

    void Finish(int index, bool release)
    {
        var j = jobs[index];
        if (j.sack0 != null) { UnityEngine.Object.Destroy(j.sack0.gameObject); j.sack0 = null; }
        if (j.sack1 != null) { UnityEngine.Object.Destroy(j.sack1.gameObject); j.sack1 = null; }
        if (j.agent != null) { j.agent.sack0 = j.agent.sack1 = null; if (release) dir.ReleaseInternal(j.agent, j); }
        j.agent = null; j.stop = null; j.rec0 = j.rec1 = null;
        jobs.RemoveAt(index);
        free.Push(j);
    }

    /// <summary>WorldLife is going to sleep (scene unloading, anchor off): every bag still in someone's arms goes to its slot.</summary>
    public void Clear()
    {
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            var j = jobs[i];
            // Not while the scene is leaving: the bag would land in the next scene (records are data; it shows next time).
            if (j.stop != null && j.Carrying && !WorldLifeRunner.Leaving) MaterializeAll(j, null, Vector3.zero);
            Finish(i, false);
        }
        FirstShownAt.Clear();
        FirstShownInfo.Clear();
        MaterializedAt.Clear();
    }

    /// <summary>The running walk-out at a house, or null.</summary>
    public Job FindJobAt(int house)
    {
        for (int i = 0; i < jobs.Count; i++) if (jobs[i].house == house) return jobs[i];
        return null;
    }

    /// <summary>The job carrying this record (tests), or null.</summary>
    public Job FindJob(string bagId)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if ((j.rec0 != null && j.rec0.bagId == bagId) || (j.rec1 != null && j.rec1.bagId == bagId)) return j;
        }
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { CarrierPicker = null; Grudged = null; }
}

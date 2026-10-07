using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1, §4.2, §7, §8): the Neighbour Puppets director (slot Puppets, billed to
/// WorldLife.Puppets). It owns every person and dog shown on this machine:
/// <list type="bullet">
/// <item><b>Requests.</b> Whoever drives a person calls <see cref="Show"/> every frame they want them drawn (package D's
/// pedestrians, dog walkers and commuters) or <see cref="ShowInternal"/> (C's walk-outs, butlers and witnesses). An
/// agent no longer requested retires (after any act it's playing). A walk-out owns its resident: D's requests for the
/// same id are ignored meanwhile.</item>
/// <item><b>Relevance, pooling and LOD</b> (§4.2), from this machine's camera only (rule 8): the nearest 24 within 35 m
/// get a full puppet (animated every frame), the next 24 within 90 m a lite one (4 renderers, 15 Hz), the rest none
/// (they still simulate, so acts finish off screen). Rigs are universal and restyled on assignment, so the pool never
/// allocates after warm-up.</item>
/// <item><b>Bodies.</b> Every shown person / dog is an <see cref="ILifeBody"/> in <see cref="LifeBodies"/>; hits come by
/// rule through <see cref="IWorldLifeAuthority.ClaimNpcHit"/> (rule 13: no networked rigidbodies touch).
/// Until package E sets the real authority, an interim sensor client here claims truck-vs-person/dog overlaps so people
/// can already be run over (cartoon flops, no consequences); it switches itself off once E's authority is in.</item>
/// <item><b>Chatter</b>: one line per NPC per 6 s, 3 on screen at once, with an Animal-Crossing mumble.</item>
/// </list>
/// </summary>
public sealed class ResidentDirector : WorldLifeDirector, ITruckSensorClient
{
    public static ResidentDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Puppets;
    public override string Name => "ResidentDirector";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Instance = new ResidentDirector();
        WorldLifeRunner.Register(Instance);
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        players.Clear();
        InterimSensor = true;
        StandIns.Clear();
    }

    /// <summary>Claim truck-vs-person overlaps here while the no-op authority is in place (before package E).</summary>
    public static bool InterimSensor = true;

    // ------------------------------------------------------------------ players (rule 8: every player, never Local)

    static readonly List<PlayerController> players = new List<PlayerController>(8);
    static void OnPlayerSpawned(PlayerController p) { if (p != null && !players.Contains(p)) players.Add(p); }
    public IReadOnlyList<PlayerController> Players
    {
        get
        {
            for (int i = players.Count - 1; i >= 0; i--) if (players[i] == null) players.RemoveAt(i);
            return players;
        }
    }

    // ------------------------------------------------------------------ state

    public WorldLifeContext Ctx { get; private set; }
    public PuppetFx Fx { get; private set; }
    public WalkOutPresenter WalkOuts { get; private set; }
    public ButlerPosts Butlers { get; private set; }
    public NeglectWitness Witnesses { get; private set; }
    public CarScenes Cars { get; private set; }
    Transform root;

    readonly Dictionary<uint, ResidentAgent> byId = new Dictionary<uint, ResidentAgent>(96);
    readonly List<ResidentAgent> agents = new List<ResidentAgent>(96);
    readonly Stack<ResidentAgent> agentPool = new Stack<ResidentAgent>(64);
    readonly Dictionary<uint, DogAgent> dogById = new Dictionary<uint, DogAgent>(16);
    readonly List<DogAgent> dogs = new List<DogAgent>(16);
    readonly Stack<DogAgent> dogPool = new Stack<DogAgent>(16);

    sealed class RigSlot
    {
        public PuppetRig rig;
        public uint styledFor = uint.MaxValue;
        public int styledFlags = -1;
        public bool used;
        public InteractionZone zone;
        public TakeBagInteractable take;
    }
    readonly List<RigSlot> rigs = new List<RigSlot>(48);
    readonly Dictionary<PuppetRig, RigSlot> slotOf = new Dictionary<PuppetRig, RigSlot>(48);
    readonly List<DogRig> dogRigs = new List<DogRig>(8);
    readonly Stack<DogRig> dogRigFree = new Stack<DogRig>(8);
    readonly RigSpec spec = new RigSpec();

    ResidentAgent[] sortBuf = new ResidentAgent[128];
    float[] sortKey = new float[128];
    float nextLod;
    bool lodDirty;
    int buildsThisFrame, restylesThisFrame;
    public const int MaxBuildsPerFrame = 3, MaxRestylesPerFrame = 8, MaxRigs = WorldLifeTuning.Lod.MaxFullPuppets + WorldLifeTuning.Lod.MaxLitePuppets;
    public const int MaxDogRigs = 8;

    readonly float[] lineTimes = new float[8];
    int lineHead;

    readonly List<ILifeBody> sensed = new List<ILifeBody>(16);
    readonly Dictionary<uint, int> lastClaimTick = new Dictionary<uint, int>(32);

    // ---- stats
    public int FullCount { get; private set; }
    public int LiteCount { get; private set; }
    public int AgentCount => agents.Count;
    public int DogCount => dogs.Count;
    public int RigsBuilt => rigs.Count;
    public int DogRigsBuilt => dogRigs.Count;
    public int MaxFullSeen { get; private set; }
    public int MaxLiteSeen { get; private set; }
    public int Restyles { get; private set; }
    public int InterimClaims { get; private set; }
    public int Lines { get; private set; }
    public int LinesSuppressed { get; private set; }
    public IReadOnlyList<ResidentAgent> Agents => agents;
    public IReadOnlyList<DogAgent> Dogs => dogs;

    // ---- perf (§4.4: ≤ 0.02 ms per full puppet, ≤ 0.005 ms per lite)
    static readonly ProfilerMarker FullMarker = new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Puppets.Full");
    static readonly ProfilerMarker LiteMarker = new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Puppets.Lite");
    const int PerfFrames = 600;
    readonly float[] perFullMs = new float[PerfFrames];
    readonly float[] perLiteMs = new float[PerfFrames];
    readonly float[] tickMs = new float[PerfFrames];
    int perfHead, perfCount;
    float secScenes, secFeed, secStep, secLod, secDraw;
    /// <summary>Running averages (ms) of the tick's sections: C's scenes (walk-outs, butlers, witnesses), D's feed, agent
    /// simulation, LOD / pooling, drawing.</summary>
    public string Sections => $"scenes {secScenes:F4} / feed {secFeed:F4} / step {secStep:F4} / lod {secLod:F4} / draw {secDraw:F4} ms";

    // ------------------------------------------------------------------ wake / sleep

    public override void Wake(WorldLifeContext c)
    {
        Ctx = c;
        ResidentCast.Ensure(c.Graph, c.TownSeed);
        root = PuppetKit.Node(c.Root, "[Residents]");
        Fx = new PuppetFx(root);
        WalkOuts = new WalkOutPresenter(this);
        Butlers = new ButlerPosts(this);
        Witnesses = new NeglectWitness(this);
        Butlers.Wake(c);
        Cars = new CarScenes(this);
        Cars.Wake();
        PickupStop.PutOutPresenter = OfferToPresenter;
        GameSession.CurbTrashChanged += OnCurbTrashChanged;
        WorldLifeRunner.AddSensorClient(this);
        MaxFullSeen = MaxLiteSeen = Restyles = InterimClaims = Lines = LinesSuppressed = 0;
        perfHead = perfCount = 0;
        nextLod = 0f;
    }

    public override void Sleep()
    {
        WorldLifeRunner.RemoveSensorClient(this);
        GameSession.CurbTrashChanged -= OnCurbTrashChanged;
        if (PickupStop.PutOutPresenter == (Func<PickupStop, CurbBag, bool>)OfferToPresenter) PickupStop.PutOutPresenter = null;
        WalkOuts?.Clear();
        Witnesses?.Clear();
        Butlers?.Sleep();
        Cars?.Sleep();
        Fx?.Clear();
        for (int i = agents.Count - 1; i >= 0; i--) { var a = agents[i]; LifeBodies.Unregister(a); a.CancelAct(); a.DetachRig(); agentPool.Push(a); }
        agents.Clear(); byId.Clear();
        for (int i = dogs.Count - 1; i >= 0; i--) { LifeBodies.Unregister(dogs[i]); dogs[i].rig = null; dogPool.Push(dogs[i]); }
        dogs.Clear(); dogById.Clear();
        // The runner destroys ctx.Root (and every rig with it).
        rigs.Clear(); slotOf.Clear(); dogRigs.Clear(); dogRigFree.Clear();
        lastClaimTick.Clear();
        Ctx = null;
        root = null;
    }

    bool OfferToPresenter(PickupStop stop, CurbBag rec) => WalkOuts != null && WalkOuts.Offer(stop, rec);

    void OnCurbTrashChanged() => WalkOuts?.OnCurbTrashChanged();

    // ------------------------------------------------------------------ the request API

    /// <summary>
    /// Package D: draw resident <paramref name="id"/> this frame at <paramref name="pos"/> (feet) facing
    /// <paramref name="yaw"/> (degrees). Call it every frame the person is out (only when within
    /// <see cref="WorldLifeTuning.Lod.RelevanceDistance"/> of the camera is useful). <paramref name="withDog"/> shows their
    /// dog on its leash (<see cref="ResidentCast.DogOf"/>); <paramref name="pop"/> 0..1 squashes them into the door or car at
    /// the ends of an excursion (C's feed fills it in from <see cref="PedestrianDirector.Pedestrian.pop"/>). Returns the agent (its <see cref="ResidentAgent.Interrupted"/>
    /// says a flop or a startle is drawing them instead), or null before WorldLife wakes.
    /// </summary>
    public static ResidentAgent Show(LifeId id, Vector3 pos, float yaw, PersonPose pose = PersonPose.Walk, float poseT = 0f, bool withDog = false, float pop = 1f)
    {
        var d = Instance;
        if (d == null || d.Ctx == null) return null;
        string stop = "";
        if (ResidentCast.IsValid(id))
        {
            var g = d.Ctx.Graph;
            int h = ResidentCast.Get(id).house;
            if (h < g.houses.Length) stop = g.houses[h].stopId;
        }
        var a = d.ShowInternal(id, AgentOwner.External, null, stop, pos, yaw, pose, poseT, pop, false, true);
        if (a != null && withDog && a.owner == AgentOwner.External) d.WantDog(a);
        return a;
    }

    /// <summary>Is this person being drawn by one of C's own scenes (walk-out, butler, witness)?</summary>
    public bool IsBusy(LifeId id) => byId.TryGetValue(id.Packed, out var a) && a.owner != AgentOwner.External;
    public static bool IsInterrupted(LifeId id) => Instance != null && Instance.byId.TryGetValue(id.Packed, out var a) && a.Interrupted;
    public ResidentAgent Find(LifeId id) => byId.TryGetValue(id.Packed, out var a) ? a : null;

    /// <summary>C's own scenes and D's <see cref="Show"/>: create / refresh an agent.</summary>
    public ResidentAgent ShowInternal(LifeId id, AgentOwner owner, object ownerRef, string stopId, Vector3 pos, float yaw,
        PersonPose pose, float poseT, float pop, bool night, bool carryProp)
    {
        if (Ctx == null || !id.IsValid) return null;
        if (byId.TryGetValue(id.Packed, out var a))
        {
            if (a.owner != owner)
            {
                if (owner == AgentOwner.External) return a;      // a walk-out / butler / witness owns them right now
                a.owner = owner; a.ownerRef = ownerRef;
                if (a.night != night || a.carryProp != carryProp) { a.night = night; a.carryProp = carryProp; a.restyle = true; lodDirty = true; }
            }
            else if (!ReferenceEquals(a.ownerRef, ownerRef)) a.ownerRef = ownerRef;
        }
        else
        {
            a = agentPool.Count > 0 ? agentPool.Pop() : new ResidentAgent();
            a.Init(id, StyleOf(id), owner, ownerRef, stopId, night);
            a.carryProp = carryProp;
            byId[id.Packed] = a;
            agents.Add(a);
            lodDirty = true;
        }
        a.Request(pos, yaw, pose, poseT, Time.frameCount);
        a.pop = pop;
        return a;
    }

    /// <summary>A scene stops drawing its agent (it retires once its act, if any, is over).</summary>
    public void ReleaseInternal(ResidentAgent a, object ownerRef)
    {
        if (a == null || !ReferenceEquals(a.ownerRef, ownerRef)) return;
        a.reqFrame = -1000;
        a.ownerRef = null;
        a.owner = AgentOwner.External;
    }

    static ResidentStyle StyleOf(LifeId id)
    {
        if (ResidentCast.IsValid(id)) return ResidentCast.Style(id);
        // Not in the cast (tests, stand-ins): a look from the id so a crowd still varies.
        var a = (ResidentArchetype)(id.index % 18);
        byte d = (byte)(a <= ResidentArchetype.Gardener ? 0 : a <= ResidentArchetype.HoaLady ? 1 : a <= ResidentArchetype.Housekeeper ? 2 : 3);
        return ResidentLook.StyleFor(a, d, LifeHash.Mix(id.Packed));
    }

    // ------------------------------------------------------------------ dev / acceptance hooks

    /// <summary>A test person walking a circle (or standing, speed 0), drawn as an External agent every frame while listed.
    /// Dev and acceptance evals only (LOD / perf stress, run-over tests); empty in normal play.</summary>
    public struct StandIn
    {
        public LifeId id;
        public Vector3 center;
        public float radius, speed, phase;
        public PersonPose pose;
    }
    public static readonly List<StandIn> StandIns = new List<StandIn>();

    /// <summary>Acceptance probe: the curb record count, sampled every frame while armed (it must never change because of
    /// a walk-out).</summary>
    public bool ProbeArmed;
    public int ProbeMinRecords, ProbeMaxRecords, ProbeFrames, ProbeWatchMissing, ProbeStopMin, ProbeStopMax;
    public string ProbeWatch, ProbeStop;
    public void ArmProbe(string watchBagId = null, string stopId = null)
    {
        ProbeArmed = true; ProbeMinRecords = ProbeStopMin = int.MaxValue; ProbeMaxRecords = ProbeStopMax = -1; ProbeFrames = 0; ProbeWatchMissing = 0;
        ProbeWatch = watchBagId; ProbeStop = stopId;
    }

    void FeedStandIns(float time)
    {
        for (int i = 0; i < StandIns.Count; i++)
        {
            var s = StandIns[i];
            Vector3 p = s.center; float yaw = s.phase;
            if (s.speed > 0f && s.radius > 0.01f)
            {
                float ang = s.phase * Mathf.Deg2Rad + time * s.speed / s.radius;
                p = s.center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * s.radius;
                yaw = PuppetKit.YawOf(new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang)));
            }
            ShowInternal(s.id, AgentOwner.External, null, "", p, yaw, s.speed > 0f ? s.pose : PersonPose.Stand, 0f, 1f, false, true);
        }
    }

    void WantDog(ResidentAgent owner) => WantDog(owner, ResidentCast.DogOf(owner.id));

    /// <summary>Show this dog on its owner's leash this frame.</summary>
    public void WantDog(ResidentAgent owner, LifeId dogId)
    {
        if (owner == null || !ResidentCast.IsDog(dogId)) return;
        owner.dogId = dogId;
        if (!dogById.TryGetValue(dogId.Packed, out var dog))
        {
            dog = dogPool.Count > 0 ? dogPool.Pop() : new DogAgent();
            dog.Init(dogId, ResidentCast.DogStyleOf(dogId), owner);
            dogById[dogId.Packed] = dog;
            dogs.Add(dog);
        }
        dog.owner = owner;
        dog.reqFrame = Time.frameCount;
    }

    // ------------------------------------------------------------------ tick

    public override void Tick(WorldLifeContext c)
    {
        long t0 = Stopwatch.GetTimestamp();
        float dt = Mathf.Min(c.DeltaTime, 0.1f);
        double now = c.Hours;
        int frame = Time.frameCount;
        buildsThisFrame = restylesThisFrame = 0;

        WalkOuts.Tick(c, dt);
        Butlers.Tick(c, dt);
        Witnesses.Tick(c, dt);
        Cars.Tick(dt);
        long ts1 = Stopwatch.GetTimestamp();
        PedestrianFeed.Feed(this, c);   // the door pop + gossip looks for the people D drew this frame
        if (StandIns.Count > 0) FeedStandIns(Time.time);
        if (ProbeArmed)
        {
            var recs = GameSession.CurbBags;
            int nrec = recs.Count, atStop = 0;
            bool seen = ProbeWatch == null;
            for (int i = 0; i < nrec; i++)
            {
                if (!seen && recs[i].bagId == ProbeWatch) seen = true;
                if (ProbeStop != null && recs[i].stopId == ProbeStop) atStop++;
            }
            if (!seen) ProbeWatchMissing++;
            if (nrec < ProbeMinRecords) ProbeMinRecords = nrec;
            if (nrec > ProbeMaxRecords) ProbeMaxRecords = nrec;
            if (atStop < ProbeStopMin) ProbeStopMin = atStop;
            if (atStop > ProbeStopMax) ProbeStopMax = atStop;
            ProbeFrames++;
        }

        long ts2 = Stopwatch.GetTimestamp();
        // Simulate every agent (cheap), retire the ones nobody asked for (after their act).
        for (int i = agents.Count - 1; i >= 0; i--)
        {
            var a = agents[i];
            if (a.reqFrame < frame - 1 && !a.Interrupted) { Retire(i); continue; }
            a.Step(this, dt, now);
        }
        for (int i = dogs.Count - 1; i >= 0; i--)
        {
            var d = dogs[i];
            bool ownerGone = d.owner == null || !byId.TryGetValue(d.owner.id.Packed, out var o) || !ReferenceEquals(o, d.owner);
            if ((ownerGone || d.reqFrame < frame - 1) && d.act != DogAgent.DogAct.Leap) { RetireDog(i); continue; }
            d.Step(this, dt);
        }

        long ts3 = Stopwatch.GetTimestamp();
        if (lodDirty || Time.time >= nextLod) { UpdateLod(c); nextLod = Time.time + 0.2f; lodDirty = false; }

        // Draw: full puppets every frame, lite ones at 15 Hz.
        long tf0 = Stopwatch.GetTimestamp();
        FullMarker.Begin();
        for (int i = 0; i < agents.Count; i++) { var a = agents[i]; if (a.rig != null && !a.lite) a.Animate(this, dt); }
        for (int i = 0; i < dogs.Count; i++) { var d = dogs[i]; if (d.rig != null && !d.lite) d.Animate(dt); }
        FullMarker.End();
        long tf1 = Stopwatch.GetTimestamp();
        LiteMarker.Begin();
        for (int i = 0; i < agents.Count; i++) { var a = agents[i]; if (a.rig != null && a.lite) a.Animate(this, dt); }
        for (int i = 0; i < dogs.Count; i++) { var d = dogs[i]; if (d.rig != null && d.lite) d.Animate(dt); }
        LiteMarker.End();
        long tf2 = Stopwatch.GetTimestamp();
        UpdateTakeZones();
        Fx.Tick(dt);

        double toMs = 1000.0 / Stopwatch.Frequency;
        const float k = 0.02f;   // ~50-frame running averages of each section (ms)
        secScenes += ((float)((ts1 - t0) * toMs) - secScenes) * k;
        secFeed += ((float)((ts2 - ts1) * toMs) - secFeed) * k;
        secStep += ((float)((ts3 - ts2) * toMs) - secStep) * k;
        secLod += ((float)((tf0 - ts3) * toMs) - secLod) * k;
        secDraw += ((float)((tf2 - tf0) * toMs) - secDraw) * k;
        perFullMs[perfHead] = FullCount > 0 ? (float)((tf1 - tf0) * toMs / FullCount) : -1f;
        perLiteMs[perfHead] = LiteCount > 0 ? (float)((tf2 - tf1) * toMs / LiteCount) : -1f;
        tickMs[perfHead] = (float)((Stopwatch.GetTimestamp() - t0) * toMs);
        perfHead = (perfHead + 1) % PerfFrames;
        if (perfCount < PerfFrames) perfCount++;
    }

    void Retire(int i)
    {
        var a = agents[i];
        DetachRig(a);
        LifeBodies.Unregister(a);
        a.CancelAct();
        byId.Remove(a.id.Packed);
        agents.RemoveAt(i);
        a.ownerRef = null;
        agentPool.Push(a);
    }

    void RetireDog(int i)
    {
        var d = dogs[i];
        if (d.rig != null) { d.rig.leash.enabled = false; d.rig.go.SetActive(false); dogRigFree.Push(d.rig); d.rig = null; }
        LifeBodies.Unregister(d);
        dogById.Remove(d.id.Packed);
        dogs.RemoveAt(i);
        d.owner = null;
        dogPool.Push(d);
    }

    // ------------------------------------------------------------------ LOD / pooling (§4.2)

    void UpdateLod(WorldLifeContext c)
    {
        int n = agents.Count;
        if (sortBuf.Length < n) { sortBuf = new ResidentAgent[n * 2]; sortKey = new float[n * 2]; }
        int m = 0;
        Vector3 v = c.Viewer;
        for (int i = 0; i < n; i++)
        {
            var a = agents[i];
            if (a.reqPose == PersonPose.Inside && !a.Interrupted) { a.wantLod = 0; continue; }
            float d2 = (a.pos - v).sqrMagnitude;
            // insertion sort by distance (n is small)
            int k = m++;
            while (k > 0 && sortKey[k - 1] > d2) { sortKey[k] = sortKey[k - 1]; sortBuf[k] = sortBuf[k - 1]; k--; }
            sortKey[k] = d2; sortBuf[k] = a;
        }
        int full = 0, lite = 0;
        float fullR = WorldLifeTuning.Lod.FullDistance, liteR = WorldLifeTuning.Lod.LiteDistance;
        for (int i = 0; i < m; i++)
        {
            var a = sortBuf[i];
            float d = Mathf.Sqrt(sortKey[i]);
            bool hasFull = a.rig != null && !a.lite, hasRig = a.rig != null;
            if (d <= fullR + (hasFull ? 2f : 0f) && full < WorldLifeTuning.Lod.MaxFullPuppets) { a.wantLod = 2; full++; }
            else if (d <= liteR + (hasRig ? 3f : 0f) && lite < WorldLifeTuning.Lod.MaxLitePuppets) { a.wantLod = 1; lite++; }
            else a.wantLod = 0;
        }
        // Release first, then assign (nearest first), so the pool never exceeds 24 + 24.
        for (int i = 0; i < n; i++) { var a = agents[i]; if (a.wantLod == 0 && a.rig != null) DetachRig(a); }
        for (int i = 0; i < m; i++)
        {
            var a = sortBuf[i];
            if (a.wantLod == 0) continue;
            if (a.rig != null && a.restyle && restylesThisFrame < MaxRestylesPerFrame) DetachRig(a);
            if (a.rig == null && !AttachRig(a)) { lodDirty = true; continue; }
            bool wantLite = a.wantLod == 1;
            a.lite = wantLite;
            a.rig.SetLite(wantLite);
        }
        int fc = 0, lc = 0;
        for (int i = 0; i < n; i++) { var a = agents[i]; if (a.rig != null) { if (a.lite) lc++; else fc++; } }
        FullCount = fc; LiteCount = lc;
        if (fc > MaxFullSeen) MaxFullSeen = fc;
        if (lc > MaxLiteSeen) MaxLiteSeen = lc;

        // Dogs follow their owner's LOD (full / lite / none), ≤ 8 rigs.
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            bool want = d.owner != null && d.owner.rig != null;
            if (!want && d.rig != null) { d.rig.leash.enabled = false; d.rig.go.SetActive(false); dogRigFree.Push(d.rig); d.rig = null; LifeBodies.Unregister(d); }
            else if (want && d.rig == null)
            {
                DogRig r = dogRigFree.Count > 0 ? dogRigFree.Pop() : dogRigs.Count < MaxDogRigs ? BuildDogRig() : null;
                if (r == null) continue;
                r.Apply(d.style);
                r.go.SetActive(true);
                d.rig = r;
                LifeBodies.Register(d);
            }
            if (d.rig != null) { d.lite = d.owner.lite; d.rig.SetLite(d.lite); }
        }
    }

    DogRig BuildDogRig()
    {
        var r = DogRig.Build(root, "Dog" + dogRigs.Count);
        dogRigs.Add(r);
        return r;
    }

    bool AttachRig(ResidentAgent a)
    {
        int flags = (a.night ? 1 : 0) | (a.carryProp ? 2 : 0);
        RigSlot best = null;
        for (int i = 0; i < rigs.Count; i++)
        {
            var s = rigs[i];
            if (s.used) continue;
            if (s.styledFor == a.id.Packed && s.styledFlags == flags) { best = s; break; }
            if (best == null) best = s;
        }
        bool needStyle = best == null || best.styledFor != a.id.Packed || best.styledFlags != flags;
        if (best == null)
        {
            if (rigs.Count >= MaxRigs || buildsThisFrame >= MaxBuildsPerFrame) return false;
            buildsThisFrame++;
            best = BuildRigSlot();
        }
        if (needStyle)
        {
            if (restylesThisFrame >= MaxRestylesPerFrame) return false;
            restylesThisFrame++;
            Restyles++;
            ResidentLook.Compose(a.style, a.night, a.carryProp, spec);
            best.rig.Apply(spec);
            best.styledFor = a.id.Packed;
            best.styledFlags = flags;
        }
        else best.rig.ResetPose();
        best.used = true;
        best.rig.go.SetActive(true);
        best.take.agent = a;
        a.rig = best.rig;
        a.restyle = false;
        a.lite = false;
        a.OnRigAttached();
        if (ResidentLook.RidesSegway(a.style.archetype)) a.segway = Fx.TakeSegway();
        LifeBodies.Register(a);
        return true;
    }

    void DetachRig(ResidentAgent a)
    {
        if (a.rig == null) return;
        if (slotOf.TryGetValue(a.rig, out var s))
        {
            s.used = false;
            s.take.agent = null;
            if (s.zone.enabled) s.zone.enabled = false;
        }
        Fx.DetachStars(a.rig.head);
        a.rig.SetLite(false);
        a.rig.go.SetActive(false);
        if (a.segway != null) { Fx.ReturnSegway(a.segway); a.segway = null; }
        a.DetachRig();
        LifeBodies.Unregister(a);
    }

    RigSlot BuildRigSlot()
    {
        var s = new RigSlot { rig = PuppetRig.Build(root, "Resident" + rigs.Count) };
        s.rig.go.SetActive(false);
        s.zone = s.rig.go.AddComponent<InteractionZone>();
        s.zone.mode = ZoneMode.OnFoot;
        s.zone.center = new Vector3(0f, 1f, 0.3f);
        s.zone.size = new Vector3(3.4f, 3f, 3.6f);
        s.zone.score = 2.2f;
        s.take = s.rig.go.AddComponent<TakeBagInteractable>();
        s.zone.targetOverride = s.take;
        s.zone.enabled = false;
        rigs.Add(s);
        slotOf[s.rig] = s;
        return s;
    }

    /// <summary>The "Take bag" zone is on only for full puppets whose walk-out can hand over right now.</summary>
    void UpdateTakeZones()
    {
        for (int i = 0; i < rigs.Count; i++)
        {
            var s = rigs[i];
            bool want = false;
            if (s.used && s.take.agent != null)
            {
                var a = s.take.agent;
                want = !a.lite && a.owner == AgentOwner.WalkOut && WalkOuts.CanHandOver(a.ownerRef as WalkOutPresenter.Job);
            }
            if (s.zone.enabled != want) s.zone.enabled = want;
        }
    }

    // ------------------------------------------------------------------ the interim person sensor (until package E)

    /// <summary>
    /// ITruckSensorClient: the runner's predictive truck box (only trucks this machine simulates), once per FixedUpdate.
    /// While the no-op authority decides (package E hasn't set <see cref="WorldLifeRunner.Authority"/>), claim every
    /// hittable person / dog inside it (padded by <see cref="WorldLifeTuning.Hits.TruckBoxPad"/>). The authority decides
    /// Nudge / Flatten / DogLeap by speed and presents it. E's TruckLifeSensor replaces this the moment E's authority is set.
    /// </summary>
    public void Sense(in TruckSweep sweep)
    {
        if (!InterimSensor || !(WorldLifeRunner.Authority is NullAuthority)) return;
        sensed.Clear();
        LifeBodies.QueryBox(sweep.center, sweep.rotation, sweep.halfExtents, sensed, LifeBodyKind.Person | LifeBodyKind.Dog,
            WorldLifeTuning.Hits.TruckBoxPad, true);
        for (int i = 0; i < sensed.Count; i++)
        {
            var b = sensed[i];
            uint key = b.Id.Packed;
            if (lastClaimTick.TryGetValue(key, out int last) && sweep.tick - last < 8) continue;
            lastClaimTick[key] = sweep.tick;
            var claim = new NpcHitClaim { target = b.Id, tick = sweep.tick, truckVelocity = sweep.velocity, actorSlot = 0, truck = sweep.truck };
            WorldLifeRunner.Authority.ClaimNpcHit(in claim);
            InterimClaims++;
        }
        sensed.Clear();
    }

    // ------------------------------------------------------------------ events from agents

    public void OnAgentHit(ResidentAgent a, in NpcHit h)
    {
        switch (a.owner)
        {
            case AgentOwner.WalkOut: if (a.ownerRef is WalkOutPresenter.Job j) WalkOuts.OnHit(j, a, in h); break;
            case AgentOwner.Butler: if (a.ownerRef is ButlerPosts.Post p) Butlers.OnHit(p, a, in h); break;
        }
        if (h.kind == NpcHitKind.Flatten && NearCamera(a.pos, 25f)) Juice.Shake(0.12f, 0.15f);
    }

    public void OnActEnded(ResidentAgent a)
    {
        switch (a.owner)
        {
            case AgentOwner.WalkOut: if (a.ownerRef is WalkOutPresenter.Job j) WalkOuts.OnActEnded(j, a); break;
            case AgentOwner.Witness: if (a.ownerRef is NeglectWitness.W w) Witnesses.OnActEnded(w, a); break;
        }
    }

    /// <summary>A flopping body touched down (bounce = the one bounce).</summary>
    public void Land(ResidentAgent a, Vector3 at, bool bounce)
    {
        bool near = NearCamera(at, 60f);
        if (near)
        {
            global::Fx.Dust(at + Vector3.up * 0.1f, bounce ? 0.8f : 0.6f);
            KnockSfx.Play(WorldLifeIds.Sfx.Oof, "thud", at, bounce ? 0.8f : 0.6f, Mathf.Clamp(a.style.voicePitch, 0.7f, 1.4f) * (bounce ? 0.95f : 1.08f));
        }
        if (!bounce)
        {
            Say(a, ResidentLines.Oof, ResidentLines.Hey, true);
            if (a.rig != null && !a.lite) Fx.AttachStars(a.rig.head, 2f, a.rig.layout.headR);
            a.KickEyes(7f);
        }
    }

    /// <summary>A flattened resident's mug / newspaper / briefcase flies off as local debris.</summary>
    public void FlingProp(ResidentAgent a, Vector3 launch)
    {
        if (a.rig == null || a.lite) return;
        var rig = a.rig;
        int i = (int)PartSlot.Prop;
        if (rig.PartOn(PartSlot.Prop) && rig.r[i].gameObject.activeSelf) Fx.Fling(rig.r[i], rig.f[i], launch, ref a.Dice);
        rig.SetPropVisible(false);
    }

    // ------------------------------------------------------------------ chatter

    /// <summary>One line over a head (≤ 1 per NPC per 6 s, ≤ 3 on screen; <paramref name="force"/> skips the per-NPC
    /// limit for the beats that matter, like "OOF!" and "Oh! Thanks, dear!").</summary>
    public void Say(ResidentAgent a, string[] lines, Color color, bool force)
    {
        if (a == null || lines == null || lines.Length == 0) return;
        Say(a, a.Dice.Pick(lines), color, force);
    }

    public void Say(ResidentAgent a, string line, Color color, bool force)
    {
        if (a == null || string.IsNullOrEmpty(line) || a.rig == null || a.lite || Ctx == null) return;
        if (!NearCamera(a.pos, 45f)) return;
        float now = Time.time;
        if (!force && now < a.nextLineAt) { LinesSuppressed++; return; }
        int onScreen = 0;
        for (int i = 0; i < lineTimes.Length; i++) if (now - lineTimes[i] < 1.6f) onScreen++;
        if (onScreen >= WorldLifeTuning.Lod.ChatterOnScreen && !force) { LinesSuppressed++; return; }
        lineTimes[lineHead] = now;
        lineHead = (lineHead + 1) % lineTimes.Length;
        a.nextLineAt = now + WorldLifeTuning.Lod.ChatterInterval;
        GameHUD.FloatingText(a.HeadTop + Vector3.up * 0.25f, line, color, 0.85f);
        bool yell = color == ResidentLines.Yell;
        KnockSfx.Play(a.Dice.Pick(WorldLifeIds.Sfx.Mumbles), "blip", a.HeadTop, yell ? 0.6f : 0.42f, a.style.voicePitch * (yell ? 1.12f : 1f));
        a.Talk(0.9f);
        Lines++;
    }

    public bool NearCamera(Vector3 p, float r) => Ctx != null && (p - Ctx.Viewer).sqrMagnitude < r * r;

    // ------------------------------------------------------------------ reports

    public void PerfStats(out float fullAvg, out float fullP99, out float liteAvg, out float tickAvg, out float tickP99, out int frames)
    {
        fullAvg = fullP99 = liteAvg = tickAvg = tickP99 = 0f;
        frames = perfCount;
        if (perfCount == 0) return;
        var tmp = new List<float>(perfCount);
        double sum = 0; int nf = 0;
        for (int i = 0; i < perfCount; i++) if (perFullMs[i] >= 0f) { tmp.Add(perFullMs[i]); sum += perFullMs[i]; nf++; }
        if (nf > 0) { fullAvg = (float)(sum / nf); tmp.Sort(); fullP99 = tmp[Mathf.Clamp(Mathf.CeilToInt(nf * 0.99f) - 1, 0, nf - 1)]; }
        sum = 0; int nl = 0;
        for (int i = 0; i < perfCount; i++) if (perLiteMs[i] >= 0f) { sum += perLiteMs[i]; nl++; }
        if (nl > 0) liteAvg = (float)(sum / nl);
        tmp.Clear(); sum = 0;
        for (int i = 0; i < perfCount; i++) { tmp.Add(tickMs[i]); sum += tickMs[i]; }
        tickAvg = (float)(sum / perfCount);
        tmp.Sort();
        tickP99 = tmp[Mathf.Clamp(Mathf.CeilToInt(perfCount * 0.99f) - 1, 0, perfCount - 1)];
    }

    /// <summary>Renderers switched on right now (puppets + dogs + props), for the §4.4 renderer budget.</summary>
    public int ActiveRenderers()
    {
        int n = 0;
        for (int i = 0; i < agents.Count; i++) if (agents[i].rig != null) n += agents[i].rig.ActiveRenderers;
        for (int i = 0; i < dogs.Count; i++)
            if (dogs[i].rig != null) foreach (var r in dogs[i].rig.r) if (r.gameObject.activeInHierarchy) n++;
        return n + (Fx != null ? Fx.ActiveRenderers : 0);
    }

    public string Describe()
    {
        PerfStats(out float fa, out float fp, out float la, out float ta, out float tp, out int fr);
        return $"agents {agents.Count} (full {FullCount}, lite {LiteCount}; max {MaxFullSeen}/{MaxLiteSeen}), dogs {dogs.Count}, rigs built {rigs.Count} " +
               $"(+{dogRigs.Count} dog), restyles {Restyles}, renderers {ActiveRenderers()}; walk-outs {WalkOuts?.Jobs.Count} (accepted {WalkOuts?.Accepted}, " +
               $"materialized {WalkOuts?.Materialized}, hand-overs {WalkOuts?.HandOvers}); butler reveals {Butlers?.RevealsStarted}; witnesses {Witnesses?.Active.Count}; car scenes {Cars?.DriverScenes}+{Cars?.OwnerScenes}; " +
               $"per full puppet avg {fa:F4} ms p99 {fp:F4}; per lite {la:F4} ms; director tick avg {ta:F4} ms p99 {tp:F4} ({fr} frames); lines {Lines} (+{LinesSuppressed} held); sections {Sections}";
    }
}

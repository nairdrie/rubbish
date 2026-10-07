using System.Collections.Generic;
using System.Diagnostics;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>What a kid is doing.</summary>
public enum KidMode : byte
{
    /// <summary>Indoors (the timetable, or hiding from a truck that got too close).</summary>
    Inside = 0,
    /// <summary>Playing at their home spot.</summary>
    Play = 1,
    /// <summary>A truck is near: running along the fence after it, waving.</summary>
    Follow = 2,
}

/// <summary>One kid (plain C#, pooled with the roster; ticked by <see cref="KidDirector"/>). Presentation only: a kid has
/// no collider, isn't an ILifeBody and is never registered in LifeBodies, so no rule can ever hit one.</summary>
public sealed class KidAgent
{
    // ---- roster (data: Town seed + pen + kid number)
    public int index, slot, penIndex;
    public KidPen pen;
    public uint penKey;
    public KidStyle style;
    public bool holdsSign;
    public string signText;

    // ---- state (local presentation)
    public bool scheduledOut;
    public KidMode mode;
    public float pop;
    public Vector3 pos;
    public float yaw;
    public bool placed, onLoop;
    /// <summary>Arc length on the pen's run loop while <see cref="onLoop"/>.</summary>
    public float s;
    public float moveSpeed;
    public float cheerT, cheerDelay, cheerAge, shyT, hopT, hopDur, hopH, nextHop, nextLineAt, signRaise, propSpin;
    public int rank;
    public Vector3 lookAt;
    public bool hasLook;
    public Vector3 balloon;
    internal float idleT, phase, liteAcc;

    // ---- presentation
    public PuppetRig rig;
    public bool lite;
    public int wantLod;
    internal KidDirector.KidSign sign;
    public PuppetDice dice;

    public bool Out => pop > 0f;
    public bool Cheering => cheerDelay <= 0f && cheerT > 0f;
    public Vector3 HeadTop => pos + Vector3.up * (style.height + 0.12f);
}

/// <summary>
/// WorldLife kids (Docs/WorldLife.md §3.1, §12.K): the kids behind the fences of the yard pens and the park playground who
/// wave and cheer the garbage truck. A WorldLife director (slot Puppets, billed to WorldLife.Puppets, own marker
/// WorldLife.Puppets.Kids), with its own small pool of the residents' universal <see cref="PuppetRig"/> (same restyle pool
/// and LOD tiers: full ≤ 35 m every frame, lite ≤ 90 m at 15 Hz; 8 + 8 kids at most).
/// <list type="bullet">
/// <item><b>Never hittable.</b> Kids aren't bodies (no ILifeBody, never in LifeBodies, no colliders), so the sensor, the
/// horn scatter, bumps and thrown things can't reach them, and they only live inside <see cref="KidPen"/>s whose hard fence
/// the truck can't cross. A safety net sends a kid indoors if a truck body ever gets within 0.3 m anyway.</item>
/// <item><b>Schedule.</b> Out after school (~15:00–19:00) and the odd no-school morning (<see cref="KidSchedule"/>, pure
/// LifeHash data); indoors at night.</item>
/// <item><b>Reactions.</b> A truck within 25 m: they run along the inside of the fence after it and wave ("HI TRUCK!").
/// A honk within 30 m (or the air horn's radius): a jump-cheer and "YAAAY!". The first honk of the in-game day at each
/// pen is a fan moment ("BEST. DAY. EVER!"): +1 Rep through the Neighborly fact on the host (rule 15; Progression caps it
/// with the hand-overs at 10 a day).</item>
/// </list>
/// Everything except the fan moment's Rep is local presentation (rule 5); cosmetic dice come from LifeHash (rule 2).
/// </summary>
public sealed class KidDirector : WorldLifeDirector
{
    public static KidDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Puppets;
    public override string Name => "KidDirector";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Instance = new KidDirector();
        WorldLifeRunner.Register(Instance);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    /// <summary>GameSession counter (rule 9): (day &lt;&lt; 10) | a bit per pen that already had today's fan moment.</summary>
    public const string FanCounter = "wl_kids_fans";
    public const int MaxBuildsPerFrame = 2, MaxRestylesPerFrame = 4, MaxSigns = 4;
    public static int MaxRigs => WorldLifeTuning.Kids.MaxFull + WorldLifeTuning.Kids.MaxLite;

    static readonly ProfilerMarker KidsMarker = new ProfilerMarker(ProfilerCategory.Scripts, "WorldLife.Puppets.Kids");

    // ------------------------------------------------------------------ state

    WorldLifeContext ctx;
    Transform root;
    readonly List<KidPen> pens = new List<KidPen>(8);
    readonly List<KidAgent> kids = new List<KidAgent>(24);
    int[] penFirst = new int[8], penCount = new int[8];
    bool[] penRelevant = new bool[8];
    Vector3[] penTruckPos = new Vector3[8];
    float[] penTruckDist = new float[8];
    TruckControllerV2[] penTruck = new TruckControllerV2[8];
    int rosterVersion = -1;
    float nextSchedule, nextLod;
    bool lodDirty;
    int buildsThisFrame, restylesThisFrame;

    sealed class RigSlot
    {
        public PuppetRig rig;
        public int styledFor = -1;
        public bool styledSign;
        public bool used;
    }
    readonly List<RigSlot> rigs = new List<RigSlot>(16);
    readonly Dictionary<PuppetRig, RigSlot> slotOf = new Dictionary<PuppetRig, RigSlot>(16);
    readonly RigSpec spec = new RigSpec();

    internal sealed class KidSign
    {
        public Transform t;
        public TextMeshPro text;
        public bool used;
    }
    readonly List<KidSign> signs = new List<KidSign>(MaxSigns);

    KidAgent[] sortBuf = new KidAgent[32];
    float[] sortKey = new float[32];
    readonly float[] lineTimes = new float[6];
    int lineHead;

    // ---- stats (acceptance evals)
    public IReadOnlyList<KidAgent> Kids => kids;
    public IReadOnlyList<KidPen> Pens => pens;
    public int FullCount { get; private set; }
    public int LiteCount { get; private set; }
    public int MaxFullSeen { get; private set; }
    public int MaxLiteSeen { get; private set; }
    public int RigsBuilt => rigs.Count;
    public int Restyles { get; private set; }
    public int HonksHeard { get; private set; }
    public int Cheers { get; private set; }
    public int FanMoments { get; private set; }
    public int Waves { get; private set; }
    public int Shies { get; private set; }
    public int Lines { get; private set; }
    public int OutCount { get; private set; }
    const int PerfFrames = 600;
    readonly float[] tickMs = new float[PerfFrames];
    int perfHead, perfCount;

    // ------------------------------------------------------------------ wake / sleep

    public override void Wake(WorldLifeContext c)
    {
        ctx = c;
        root = PuppetKit.Node(c.Root, "[Kids]");
        rosterVersion = -1;
        nextSchedule = nextLod = 0f;
        perfHead = perfCount = 0;
        TruckControllerV2.Honked -= OnHonked;
        TruckControllerV2.Honked += OnHonked;
    }

    public override void Sleep()
    {
        TruckControllerV2.Honked -= OnHonked;
        // The runner destroys ctx.Root (the rigs and signs go with it): just drop the references.
        for (int i = 0; i < kids.Count; i++) { kids[i].rig = null; kids[i].sign = null; }
        kids.Clear();
        pens.Clear();
        rigs.Clear();
        slotOf.Clear();
        signs.Clear();
        rosterVersion = -1;
        ctx = null;
        root = null;
    }

    // ------------------------------------------------------------------ roster (data: pens of this scene, sorted by id)

    void RebuildRoster()
    {
        for (int i = 0; i < kids.Count; i++) DetachRig(kids[i]);
        kids.Clear();
        pens.Clear();
        var all = KidPen.All;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || !p.isActiveAndEnabled || p.gameObject.scene != ctx.Scene) continue;
            pens.Add(p);
        }
        pens.Sort((a, b) => string.CompareOrdinal(a.penId, b.penId));
        if (penFirst.Length < pens.Count)
        {
            int n = pens.Count * 2;
            penFirst = new int[n]; penCount = new int[n]; penRelevant = new bool[n];
            penTruckPos = new Vector3[n]; penTruckDist = new float[n]; penTruck = new TruckControllerV2[n];
        }
        ulong seed = ctx.TownSeed;
        for (int p = 0; p < pens.Count; p++)
        {
            var pen = pens[p];
            uint key = LifeHash.Str(pen.penId);
            int n = Mathf.Clamp(pen.kids, 0, 8);
            penFirst[p] = kids.Count;
            penCount[p] = n;
            int signer = pen.sign && n > 0 ? LifeHash.Range(LifeHash.U64(seed, LifeKind.Kid, 0, -1, key ^ 0x51u), 0, n) : -1;
            for (int k = 0; k < n; k++)
            {
                ulong h = LifeHash.U64(seed, LifeKind.Kid, k, -1, key);
                var kid = new KidAgent
                {
                    index = kids.Count, slot = k, penIndex = p, pen = pen, penKey = key, style = KidLook.StyleFor(h),
                    holdsSign = k == signer, dice = new PuppetDice(LifeHash.Mix(h ^ 0xC1D5UL)),
                };
                if (kid.holdsSign)
                {
                    kid.style.prop = KidProp.Sign;
                    kid.signText = KidLook.SignTexts[LifeHash.Range(LifeHash.Mix(h ^ 0x5161UL), 0, KidLook.SignTexts.Length)];
                }
                kids.Add(kid);
            }
        }
        if (sortBuf.Length < kids.Count) { sortBuf = new KidAgent[kids.Count * 2]; sortKey = new float[kids.Count * 2]; }
        rosterVersion = KidPen.Version;
        nextSchedule = 0f;
        lodDirty = true;
    }

    // ------------------------------------------------------------------ tick

    public override void Tick(WorldLifeContext c)
    {
        if (rosterVersion != KidPen.Version) RebuildRoster();
        if (kids.Count == 0) return;
        KidsMarker.Begin();
        long t0 = Stopwatch.GetTimestamp();
        float dt = Mathf.Min(c.DeltaTime, 0.1f);
        float now = Time.time;
        buildsThisFrame = restylesThisFrame = 0;

        // The timetable (pure) twice a second.
        if (now >= nextSchedule)
        {
            nextSchedule = now + 0.5f;
            double hours = c.Hours;
            ulong seed = c.TownSeed;
            int outs = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                var k = kids[i];
                k.scheduledOut = KidSchedule.IsOut(seed, k.penKey, k.slot, hours);
                if (k.scheduledOut) outs++;
            }
            OutCount = outs;
        }

        // Per pen: is it near this camera (rule 8: relevance only), and the nearest truck.
        Vector3 viewer = c.Viewer;
        var trucks = TruckControllerV2.All;
        for (int p = 0; p < pens.Count; p++)
        {
            var pen = pens[p];
            bool rel = pen != null && pen.DistanceTo(viewer) <= WorldLifeTuning.Kids.RelevanceDistance;
            penRelevant[p] = rel;
            penTruck[p] = null;
            penTruckDist[p] = float.MaxValue;
            if (!rel) continue;
            for (int t = 0; t < trucks.Count; t++)
            {
                var tr = trucks[t];
                if (tr == null || !tr.isActiveAndEnabled) continue;
                Vector3 tp = TruckPos(tr);
                float d = pen.DistanceTo(tp);
                if (d < penTruckDist[p]) { penTruckDist[p] = d; penTruckPos[p] = tp; penTruck[p] = tr; }
            }
        }

        // Step the kids of relevant pens; forget the others (they snap home when the camera comes back).
        for (int p = 0; p < pens.Count; p++)
        {
            int first = penFirst[p], n = penCount[p];
            if (!penRelevant[p])
            {
                for (int i = 0; i < n; i++) { var k = kids[first + i]; if (k.placed) { k.placed = false; lodDirty = true; } }
                continue;
            }
            int rank = 0;
            for (int i = 0; i < n; i++) { var k = kids[first + i]; k.rank = k.Out || k.scheduledOut ? rank++ : 0; }
            for (int i = 0; i < n; i++) Step(kids[first + i], p, rank, dt);
        }

        if (lodDirty || now >= nextLod) { UpdateLod(c); nextLod = now + 0.2f; lodDirty = false; }
        for (int i = 0; i < kids.Count; i++) { var k = kids[i]; if (k.rig != null) Animate(k, dt); }

        tickMs[perfHead] = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        perfHead = (perfHead + 1) % PerfFrames;
        if (perfCount < PerfFrames) perfCount++;
        KidsMarker.End();
    }

    static Vector3 TruckPos(TruckControllerV2 tr)
    {
        var rb = tr.Body;
        return rb != null ? rb.position : tr.transform.position;
    }

    static float FlatDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

    /// <summary>Is a world point inside any truck's body box (padded)? The safety net behind the fence.</summary>
    static bool InsideATruck(Vector3 p, float pad)
    {
        var trucks = TruckControllerV2.All;
        var box = TruckControllerV2.BodyBoxMetres;
        for (int t = 0; t < trucks.Count; t++)
        {
            var tr = trucks[t];
            if (tr == null || !tr.isActiveAndEnabled) continue;
            var rb = tr.Body;
            Quaternion rot = rb != null ? rb.rotation : tr.transform.rotation;
            Vector3 origin = rb != null ? rb.position : tr.transform.position;
            Vector3 l = Quaternion.Inverse(rot) * (p + Vector3.up * 0.5f - origin);
            if (l.x >= box.min.x - pad && l.x <= box.max.x + pad && l.z >= box.min.z - pad && l.z <= box.max.z + pad && l.y <= box.max.y + pad && l.y >= box.min.y - 1.2f)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ one kid

    void Step(KidAgent k, int p, int outN, float dt)
    {
        var pen = k.pen;
        var T = WorldLifeTuning.Kids.WaveDistance;
        if (!k.placed)
        {
            k.placed = true;
            k.pos = pen.Spot(k.slot);
            k.pos.y = pen.center.y;
            k.yaw = pen.yaw + k.dice.Range(-25f, 25f);
            k.onLoop = false;
            k.pop = k.scheduledOut ? 1f : 0f;
            k.mode = k.scheduledOut ? KidMode.Play : KidMode.Inside;
            k.cheerT = k.cheerDelay = k.shyT = k.hopT = 0f;
            k.nextHop = Time.time + k.dice.Range(1f, 4f);
            k.balloon = k.pos + Vector3.up * (k.style.height + 0.6f);
            lodDirty = true;
        }

        // The safety net: a truck body reaching a kid (the fence makes this impossible; physics glitches aside).
        bool hasTruck = penTruck[p] != null;
        if (k.pop > 0.3f && k.shyT <= 0f && hasTruck && penTruckDist[p] < 6f && InsideATruck(k.pos, WorldLifeTuning.Kids.ShyPad))
        {
            k.shyT = WorldLifeTuning.Kids.ShySeconds;
            Shies++;
            Say(k, k.dice.Pick(KidLines.Eek), ResidentLines.Yell, true);
        }
        if (k.shyT > 0f)
        {
            k.shyT -= dt;
            if (k.shyT <= 0f && hasTruck && penTruckDist[p] < WorldLifeTuning.Kids.ShyClearDistance) k.shyT = 0.5f;   // not until it's gone
        }

        bool wantOut = k.scheduledOut && k.shyT <= 0f;
        float popWas = k.pop;
        k.pop = Mathf.MoveTowards(k.pop, wantOut ? 1f : 0f, dt / (wantOut ? 0.45f : (k.shyT > 0f ? 0.18f : 0.3f)));
        if (popWas <= 0f && k.pop > 0f)
        {
            // Out the door (squash pop at the home spot).
            k.pos = pen.Spot(k.slot); k.pos.y = pen.center.y; k.onLoop = false;
            lodDirty = true;
            if (NearCamera(k.pos, 30f)) KnockSfx.Play("pop", "pop", k.pos + Vector3.up * 0.6f, 0.35f, 1.5f);
        }
        if (k.pop <= 0f)
        {
            if (k.mode != KidMode.Inside) lodDirty = true;
            k.mode = KidMode.Inside; k.onLoop = false; k.moveSpeed = 0f; k.cheerT = 0f; k.hasLook = false;
            return;
        }
        if (!wantOut) { k.moveSpeed = 0f; return; }   // popping back in: stand still

        // Wave / follow while a truck is near this kid.
        float dKid = hasTruck ? FlatDist(penTruckPos[p], k.pos) : float.MaxValue;
        bool near = dKid <= (k.mode == KidMode.Follow ? T + WorldLifeTuning.Kids.WaveHysteresis : T);
        if (near && k.mode != KidMode.Follow)
        {
            k.mode = KidMode.Follow;
            Waves++;
            Say(k, k.dice.Pick(KidLines.Wave), KidLines.Hi, false);
            Hop(k, 0.22f, 0.35f);
        }
        else if (!near && k.mode == KidMode.Follow)
        {
            k.mode = KidMode.Play;
            if (k.dice.Value < 0.5f) Say(k, k.dice.Pick(KidLines.Bye), KidLines.Hi, false);
        }
        else if (k.mode == KidMode.Inside) k.mode = KidMode.Play;

        Vector3 before = k.pos;
        if (k.mode == KidMode.Follow)
        {
            float sT = pen.LoopNearest(penTruckPos[p]) + (k.rank - (outN - 1) * 0.5f) * WorldLifeTuning.Kids.Spread;
            MoveOnLoop(k, sT, WorldLifeTuning.Kids.RunSpeed, dt);
            k.lookAt = penTruckPos[p]; k.hasLook = true;
            if (Time.time >= k.nextHop && k.hopT <= 0f) { Hop(k, k.dice.Range(0.12f, 0.24f), 0.32f); k.nextHop = Time.time + k.dice.Range(0.6f, 1.3f); }
        }
        else
        {
            MoveHome(k, WorldLifeTuning.Kids.StrollSpeed, dt);
            k.hasLook = false;
            if (Time.time >= k.nextHop && k.hopT <= 0f) { Hop(k, k.dice.Range(0.1f, 0.2f), 0.3f); k.nextHop = Time.time + k.dice.Range(2.5f, 6f); }
        }
        Vector3 moved = k.pos - before; moved.y = 0f;
        float inst = dt > 1e-5f ? moved.magnitude / dt : 0f;
        k.moveSpeed = PuppetKit.Damp(k.moveSpeed, inst > 6f ? 0f : inst, 12f, dt);

        // Facing: the truck while it's near, else where they're going, else the street (with a little wobble).
        float want;
        if (k.hasLook) want = PuppetKit.YawOf(k.lookAt - k.pos);
        else if (inst > 0.2f) want = PuppetKit.YawOf(moved);
        else want = pen.yaw + Mathf.Sin(k.idleT * 0.37f + k.slot * 1.7f) * 30f;
        k.yaw = PuppetKit.DampAngle(k.yaw, want, 8f, dt);

        if (k.cheerDelay > 0f) k.cheerDelay -= dt;
        else if (k.cheerT > 0f) { k.cheerT -= dt; k.cheerAge += dt; }
        if (k.hopT > 0f) k.hopT -= dt;
    }

    static void Hop(KidAgent k, float height, float seconds) { k.hopH = height; k.hopDur = seconds; k.hopT = seconds; }

    /// <summary>To arc <paramref name="sTarget"/>: onto the loop at its nearest point first, then round it the short way.</summary>
    static void MoveOnLoop(KidAgent k, float sTarget, float speed, float dt)
    {
        var pen = k.pen;
        if (!k.onLoop)
        {
            float sIn = pen.LoopNearest(k.pos);
            if (MoveToward(k, pen.LoopPoint(sIn), speed, dt)) { k.onLoop = true; k.s = sIn; }
            return;
        }
        float d = pen.LoopDelta(k.s, sTarget), step = speed * dt;
        k.s = Mathf.Abs(d) <= step ? sTarget : k.s + Mathf.Sign(d) * step;
        Vector3 q = pen.LoopPoint(k.s);
        k.pos = new Vector3(q.x, pen.center.y, q.z);
    }

    /// <summary>Home: round the loop to the point nearest the home spot, then step off it to the spot.</summary>
    static void MoveHome(KidAgent k, float speed, float dt)
    {
        var pen = k.pen;
        Vector3 home = pen.Spot(k.slot);
        if (k.onLoop)
        {
            float sOut = pen.LoopNearest(home);
            float d = pen.LoopDelta(k.s, sOut), step = speed * dt;
            if (Mathf.Abs(d) <= step) { k.s = sOut; k.onLoop = false; }
            else k.s += Mathf.Sign(d) * step;
            Vector3 q = pen.LoopPoint(k.s);
            k.pos = new Vector3(q.x, pen.center.y, q.z);
            return;
        }
        MoveToward(k, home, speed, dt);
    }

    static bool MoveToward(KidAgent k, Vector3 target, float speed, float dt)
    {
        Vector3 a = new Vector3(k.pos.x, 0f, k.pos.z), b = new Vector3(target.x, 0f, target.z);
        Vector3 n = Vector3.MoveTowards(a, b, speed * dt);
        k.pos = new Vector3(n.x, k.pen.center.y, n.z);
        return (n - b).sqrMagnitude < 0.0025f;
    }

    // ------------------------------------------------------------------ the horn (§8.5): jump-cheers + the daily fan moment

    void OnHonked(TruckControllerV2 truck)
    {
        if (ctx == null || truck == null || kids.Count == 0) return;
        HonksHeard++;
        Vector3 tp = TruckPos(truck);
        float r = WorldLifeTuning.Kids.HonkRadius;
        double hours = ctx.Hours;
        for (int p = 0; p < pens.Count; p++)
        {
            var pen = pens[p];
            if (pen == null || pen.DistanceTo(tp) > r) continue;
            int first = penFirst[p], n = penCount[p], cheered = 0;
            Vector3 at = pen.center;
            for (int i = 0; i < n; i++)
            {
                var k = kids[first + i];
                bool isOut = k.placed ? k.pop > 0.5f && k.shyT <= 0f : KidSchedule.IsOut(ctx.TownSeed, k.penKey, k.slot, hours);
                if (!isOut) continue;
                if (k.placed && FlatDist(k.pos, tp) > r) continue;
                k.cheerDelay = k.dice.Range(0.02f, 0.25f);
                k.cheerT = WorldLifeTuning.Kids.CheerSeconds;
                k.cheerAge = 0f;
                if (k.rig != null) k.rig.KickEyes(5f, ref k.dice);
                if (cheered == 0 && k.placed) at = k.pos;
                cheered++;
            }
            if (cheered == 0) continue;
            Cheers += cheered;
            if (NearCamera(at, 60f))
            {
                GameHUD.FloatingText(at + Vector3.up * 1.9f, KidLines.Yay[(HonksHeard + p) % KidLines.Yay.Length], KidLines.Cheer, 1f);
                KnockSfx.Play(WorldLifeIds.Sfx.Mumbles[(HonksHeard + p) % 3], "blip", at + Vector3.up, 0.55f, 2.05f);
                KnockSfx.Play(WorldLifeIds.Sfx.Mumbles[(HonksHeard + p + 1) % 3], "blip", at + Vector3.up, 0.45f, 2.3f);
            }
            TryFanMoment(p, at);
        }
    }

    /// <summary>Host (rule 15): the first honk of the in-game day at a pen makes its kids the truck's biggest fans: +1 Rep
    /// through the Neighborly fact (Progression applies it, capped with the hand-overs at 10 a day), once per pen per day.</summary>
    bool TryFanMoment(int p, Vector3 at)
    {
        var books = RulesBooks.Active;
        var pen = pens[p];
        if (!ClaimFan(books, p, pen.stopId, pen.district, at)) return false;
        FanMoments++;
        int day = RulesBooks.DayOf(books.Hours);
        // Presentation: a longer cheer, confetti and the line.
        int first = penFirst[p], n = penCount[p];
        for (int i = 0; i < n; i++) { var k = kids[first + i]; if (k.cheerT > 0f) k.cheerT = WorldLifeTuning.Kids.FanCheerSeconds; }
        if (NearCamera(at, 80f))
        {
            GameHUD.FloatingText(at + Vector3.up * 2.6f, KidLines.Fans[(day + p) % KidLines.Fans.Length], KidLines.Fan, 1.15f);
            Fx.Burst(at + Vector3.up * 1.4f, KidLines.Cheer, 26, 4.5f);
            KnockSfx.Play("sparkle", "ding", at + Vector3.up, 0.7f, 1.1f);
        }
        return true;
    }

    /// <summary>
    /// The decision (host only, rule 15): is this the first fan moment of the in-game day at pen <paramref name="penIndex"/>?
    /// If so, mark it (one packed GameSession counter, rule 9) and raise the Neighborly fact (+1 Rep; Progression applies
    /// it, capped at 10 a day together with the hand-overs). Pure over the books, so the self test runs it on MemoryBooks.
    /// </summary>
    public static bool ClaimFan(IRulesBooks books, int penIndex, string stopId, byte district, Vector3 at)
    {
        if (books == null || !books.IsHost) return false;
        int day = RulesBooks.DayOf(books.Hours);
        int bit = 1 << (Mathf.Max(0, penIndex) % 10);
        int v = books.GetCounter(FanCounter);
        int mask = v != 0 && (v >> 10) == day ? v & 1023 : 0;
        if ((mask & bit) != 0) return false;
        books.SetCounter(FanCounter, (day << 10) | mask | bit);
        string did = !string.IsNullOrEmpty(stopId) ? Districts.ForStopId(stopId).id : WorldLifeIds.DistrictId(district);
        books.Incident(new NeighborIncidentFact
        {
            kind = NeighborIncidentKind.Neighborly, stopId = stopId ?? "", districtId = did, pos = at, escalation = 1f,
        });
        return true;
    }

    // ------------------------------------------------------------------ chatter (≤ 1 line per kid per 5 s, ≤ 2 on screen)

    void Say(KidAgent k, string line, Color color, bool force)
    {
        if (k == null || string.IsNullOrEmpty(line) || k.rig == null || k.lite || ctx == null) return;
        if (!NearCamera(k.pos, 45f)) return;
        float now = Time.time;
        if (!force && now < k.nextLineAt) return;
        int onScreen = 0;
        for (int i = 0; i < lineTimes.Length; i++) if (now - lineTimes[i] < 1.6f) onScreen++;
        if (onScreen >= 2 && !force) return;
        lineTimes[lineHead] = now;
        lineHead = (lineHead + 1) % lineTimes.Length;
        k.nextLineAt = now + 5f;
        GameHUD.FloatingText(k.HeadTop + Vector3.up * 0.25f, line, color, 0.8f);
        KnockSfx.Play(k.dice.Pick(WorldLifeIds.Sfx.Mumbles), "blip", k.HeadTop, 0.4f, k.style.voicePitch);
        Lines++;
    }

    bool NearCamera(Vector3 p, float r) => ctx != null && (p - ctx.Viewer).sqrMagnitude < r * r;

    // ------------------------------------------------------------------ LOD / pool (§4.2 tiers; the residents' rig)

    void UpdateLod(WorldLifeContext c)
    {
        int m = 0;
        Vector3 v = c.Viewer;
        for (int i = 0; i < kids.Count; i++)
        {
            var k = kids[i];
            k.wantLod = 0;
            if (!k.placed || !k.Out || !penRelevant[k.penIndex]) continue;
            float d2 = (k.pos - v).sqrMagnitude;
            int j = m++;
            while (j > 0 && sortKey[j - 1] > d2) { sortKey[j] = sortKey[j - 1]; sortBuf[j] = sortBuf[j - 1]; j--; }
            sortKey[j] = d2; sortBuf[j] = k;
        }
        int full = 0, lite = 0;
        float fullR = WorldLifeTuning.Lod.FullDistance, liteR = WorldLifeTuning.Lod.LiteDistance;
        for (int i = 0; i < m; i++)
        {
            var k = sortBuf[i];
            float d = Mathf.Sqrt(sortKey[i]);
            bool hasFull = k.rig != null && !k.lite, hasRig = k.rig != null;
            if (d <= fullR + (hasFull ? 2f : 0f) && full < WorldLifeTuning.Kids.MaxFull) { k.wantLod = 2; full++; }
            else if (d <= liteR + (hasRig ? 3f : 0f) && lite < WorldLifeTuning.Kids.MaxLite) { k.wantLod = 1; lite++; }
        }
        for (int i = 0; i < kids.Count; i++) { var k = kids[i]; if (k.wantLod == 0 && k.rig != null) DetachRig(k); }
        for (int i = 0; i < m; i++)
        {
            var k = sortBuf[i];
            if (k.wantLod == 0) continue;
            if (k.rig == null && !AttachRig(k)) { lodDirty = true; continue; }
            bool wantLite = k.wantLod == 1;
            if (k.lite != wantLite || k.rig.IsLite != wantLite)
            {
                k.lite = wantLite;
                k.rig.SetLite(wantLite);
                k.rig.SetPropVisible(!wantLite);
                if (wantLite) ReturnSign(k); else if (k.holdsSign) TakeSign(k);
            }
        }
        int fc = 0, lc = 0;
        for (int i = 0; i < kids.Count; i++) { var k = kids[i]; if (k.rig != null) { if (k.lite) lc++; else fc++; } }
        FullCount = fc; LiteCount = lc;
        if (fc > MaxFullSeen) MaxFullSeen = fc;
        if (lc > MaxLiteSeen) MaxLiteSeen = lc;
    }

    bool AttachRig(KidAgent k)
    {
        RigSlot best = null;
        for (int i = 0; i < rigs.Count; i++)
        {
            var s = rigs[i];
            if (s.used) continue;
            if (s.styledFor == k.index) { best = s; break; }
            if (best == null) best = s;
        }
        if (best == null)
        {
            if (rigs.Count >= MaxRigs || buildsThisFrame >= MaxBuildsPerFrame) return false;
            buildsThisFrame++;
            best = new RigSlot { rig = PuppetRig.Build(root, "Kid" + rigs.Count) };
            best.rig.go.SetActive(false);
            rigs.Add(best);
            slotOf[best.rig] = best;
        }
        if (best.styledFor != k.index)
        {
            if (restylesThisFrame >= MaxRestylesPerFrame) return false;
            restylesThisFrame++;
            Restyles++;
            KidLook.Compose(k.style, k.holdsSign, spec);
            best.rig.Apply(spec);
            best.styledFor = k.index;
        }
        else best.rig.ResetPose();
        best.used = true;
        best.rig.go.SetActive(true);
        k.rig = best.rig;
        k.lite = false;
        k.rig.SetLite(false);
        k.rig.SetVisible(true);
        k.rig.Place(k.pos, k.yaw);
        k.balloon = k.pos + Vector3.up * (k.style.height + 0.6f);
        if (k.holdsSign) TakeSign(k);
        return true;
    }

    void DetachRig(KidAgent k)
    {
        if (k.rig == null) return;
        ReturnSign(k);
        if (slotOf.TryGetValue(k.rig, out var s)) s.used = false;
        if (k.rig.go != null)
        {
            k.rig.SetLite(false);
            k.rig.go.SetActive(false);
        }
        k.rig = null;
        k.lite = false;
    }

    // ---- cardboard signs (pooled; full LOD only)

    void TakeSign(KidAgent k)
    {
        if (k.sign != null || k.rig == null || string.IsNullOrEmpty(k.signText)) return;
        KidSign sg = null;
        for (int i = 0; i < signs.Count; i++) if (!signs[i].used) { sg = signs[i]; break; }
        if (sg == null)
        {
            if (signs.Count >= MaxSigns) return;
            sg = BuildSign();
            signs.Add(sg);
        }
        sg.used = true;
        sg.text.text = k.signText;
        sg.t.SetParent(k.rig.torso, false);
        sg.t.gameObject.SetActive(true);
        k.sign = sg;
        k.signRaise = 0f;
    }

    void ReturnSign(KidAgent k)
    {
        var sg = k.sign;
        k.sign = null;
        if (sg == null) return;
        sg.used = false;
        if (sg.t == null) return;
        sg.t.SetParent(root, false);
        sg.t.gameObject.SetActive(false);
    }

    KidSign BuildSign()
    {
        var sg = new KidSign { t = PuppetKit.Node(root, "KidSign") };
        var board = PuppetKit.Part(sg.t, "Board", out var f);
        var ps = new PartSpec { on = true, shape = PuppetKit.Shape.Cube, color = new Color(0.8f, 0.62f, 0.4f), smooth = 0.1f, pos = Vector3.zero, euler = Vector3.zero, scale = new Vector3(0.6f, 0.38f, 0.025f), shadow = true };
        PuppetRig.ApplyPart(board, f, in ps);
        var tgo = new GameObject("Text");
        tgo.layer = PuppetKit.Layer;
        tgo.transform.SetParent(sg.t, false);
        tgo.transform.localPosition = new Vector3(0f, 0f, 0.016f);
        tgo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        sg.text = tgo.AddComponent<TextMeshPro>();
        sg.text.fontStyle = FontStyles.Bold;
        sg.text.alignment = TextAlignmentOptions.Center;
        sg.text.color = new Color(0.1f, 0.08f, 0.08f);
        sg.text.enableAutoSizing = true;
        sg.text.fontSizeMin = 0.4f;
        sg.text.fontSizeMax = 2f;
        sg.text.rectTransform.sizeDelta = new Vector2(0.54f, 0.32f);
        sg.text.textWrappingMode = TextWrappingModes.NoWrap;
        sg.t.gameObject.SetActive(false);
        return sg;
    }

    // ------------------------------------------------------------------ animation (procedural; full every frame, lite 15 Hz)

    static float PopCurve(float p)
    {
        if (p >= 1f) return 1f;
        if (p <= 0f) return 0.001f;
        return p < 0.7f ? Mathf.Lerp(0.05f, 1.18f, p / 0.7f) : Mathf.Lerp(1.18f, 1f, (p - 0.7f) / 0.3f);
    }

    void Animate(KidAgent k, float dt)
    {
        var rig = k.rig;
        if (k.lite)
        {
            k.liteAcc += dt;
            if (k.liteAcc < 1f / WorldLifeTuning.Lod.LiteHz) return;
            dt = k.liteAcc;
            k.liteAcc = 0f;
        }
        k.idleT += dt;
        rig.Place(k.pos, k.yaw);
        float pop = PopCurve(k.pop);
        rig.juice.localScale = Vector3.one * pop;

        float H = k.style.height;
        float run = Mathf.Clamp01(k.moveSpeed / 2f);
        if (k.moveSpeed > 0.15f) k.phase += k.moveSpeed * dt / (0.42f * H) * Mathf.PI;
        else k.phase = Mathf.MoveTowards(k.phase, Mathf.Round(k.phase / Mathf.PI) * Mathf.PI, dt * 5f);
        float sn = Mathf.Sin(k.phase), cs = Mathf.Cos(k.phase);
        float t = k.idleT + k.slot * 0.77f;

        // A trot by default: quick little legs, pumping arms, a bouncy bob.
        float lx = sn * 48f * run, rx = -sn * 48f * run;
        float alx = -sn * 55f * run, arx = sn * 55f * run, alz = 12f + 6f * run, arz = 12f + 6f * run, aly = 0f, ary = 0f;
        float bob = Mathf.Abs(cs) * 0.06f * H * run;
        float pitch = 10f * run, roll = sn * 4f * run, headP = 0f, headY = 0f, headR = 0f;
        float squash = 0.02f * Mathf.Sin(t * 3.1f) * (1f - run);
        float mouth = 0.12f;
        float hop = 0f, signRaiseWant = 0f;

        if (k.hopT > 0f && k.hopDur > 0f)
        {
            float u = 1f - k.hopT / k.hopDur;
            hop = 4f * k.hopH * u * (1f - u);
        }

        bool waveLeft = k.style.WavesLeft;
        if (k.Cheering)
        {
            // The jump-cheer: both arms up, bouncing, mouth wide open.
            float a = k.cheerAge;
            hop = Mathf.Max(hop, 0.36f * Mathf.Abs(Mathf.Sin(a * Mathf.PI / 0.42f)));
            float w = Mathf.Sin(a * 22f);
            alx = -168f; arx = -168f; alz = 20f + w * 16f; arz = 20f - w * 16f;
            mouth = 1f; pitch = -6f; headP = -10f;
            squash = hop < 0.04f ? -0.06f : 0.04f;
            signRaiseWant = 1f;
        }
        else if (k.mode == KidMode.Follow)
        {
            // Wave at the truck (the free hand), the other one shows off the prop.
            float w = Mathf.Sin(t * 16f);
            if (waveLeft) { alx = -160f; alz = 30f + w * 24f; arx = k.style.prop == KidProp.ToyTruck ? -95f : -35f; arz = 14f; }
            else { arx = -160f; arz = 30f + w * 24f; alx = -10f + alx * 0.3f; alz = 10f; }
            mouth = 0.6f;
            headR = Mathf.Sin(t * 7f) * 6f;
            signRaiseWant = 0.35f + 0.15f * Mathf.Sin(t * 9f);
        }
        else
        {
            // Play: look around, and the toy truck goes "vroom".
            headY = Mathf.Sin(t * 0.9f) * 30f;
            if (k.style.prop == KidProp.ToyTruck && run < 0.3f) { arx = -55f; ary = Mathf.Sin(t * 2.6f) * 40f; arz = 4f; }
            else if (k.style.prop == KidProp.Lollipop && run < 0.3f) { arx = -125f + Mathf.Sin(t * 1.3f) * 10f; arz = -12f; }
        }
        if (k.holdsSign)
        {
            float lift = Mathf.Lerp(-78f, -168f, k.signRaise);
            alx = arx = lift; alz = arz = -6f; aly = ary = 0f;
            k.signRaise = PuppetKit.Damp(k.signRaise, signRaiseWant, 9f, dt);
        }
        if (k.hasLook)
        {
            Vector3 d = k.lookAt - k.pos; d.y = 0f;
            if (d.sqrMagnitude > 0.01f) headY = Mathf.Clamp(Mathf.DeltaAngle(k.yaw, PuppetKit.YawOf(d)), -70f, 70f);
        }

        rig.Body(new Vector3(0f, bob + hop, 0f), Quaternion.Euler(0f, 0f, roll), squash);
        rig.Torso(pitch, 0f);
        rig.Head(headP, headY, headR);
        rig.Arms(alx, alz, arx, arz, aly, ary);
        rig.Legs(lx, rx, hop > 0.05f ? 10f : 0f);
        if (k.lite) return;

        rig.SetMouth(mouth, k.Cheering ? 1.25f : 1f);
        rig.TickEyes(dt, ref k.dice);

        // The propeller beanie spins (faster when excited).
        if (k.style.hat == KidHat.Propeller)
        {
            k.propSpin += dt * (k.Cheering ? 1500f : k.mode == KidMode.Follow ? 800f : 160f);
            rig.t[(int)PartSlot.Hat2].localRotation = Quaternion.Euler(0f, k.propSpin, 0f);
        }

        // The balloon floats above the hand whatever the arm does (a lagging spring), string to the hand.
        if (k.style.prop == KidProp.Balloon && !k.holdsSign && rig.PartOn(PartSlot.Prop2))
        {
            Vector3 hand = rig.handR.position;
            Vector3 want = hand + new Vector3(Mathf.Sin(t * 1.3f) * 0.12f, 0.75f * pop + 0.05f, Mathf.Cos(t * 1.1f) * 0.1f);
            k.balloon = PuppetKit.Damp(k.balloon, want, 5f, dt);
            Vector3 dir = k.balloon - hand;
            float len = Mathf.Max(0.05f, dir.magnitude);
            SetWorld(rig.t[(int)PartSlot.Prop], hand + dir * 0.5f, Quaternion.FromToRotation(Vector3.up, dir / len), new Vector3(0.008f, len * 0.5f, 0.008f) * pop);
            SetWorld(rig.t[(int)PartSlot.Prop2], k.balloon, Quaternion.Euler(Mathf.Sin(t * 1.7f) * 8f, 0f, Mathf.Cos(t * 1.4f) * 8f), new Vector3(0.3f, 0.36f, 0.3f) * pop);
        }

        // The cardboard sign: held at the chest, waved, lifted overhead in a cheer.
        if (k.sign != null)
        {
            var L = rig.layout;
            Vector3 chest = new Vector3(0f, L.shoulderY - L.hipY - 0.04f * H, L.bodyD * 0.5f + 0.2f);
            Vector3 over = new Vector3(0f, H - L.hipY + 0.22f, 0.1f);
            k.sign.t.localPosition = Vector3.Lerp(chest, over, k.signRaise);
            k.sign.t.localRotation = Quaternion.Euler(Mathf.Lerp(-8f, 12f, k.signRaise), 0f, Mathf.Sin(t * 5f) * 4f);
        }
    }

    /// <summary>World pose and (approximately) world scale for a part under the rig's scaled hierarchy.</summary>
    static void SetWorld(Transform tr, Vector3 pos, Quaternion rot, Vector3 worldScale)
    {
        tr.SetPositionAndRotation(pos, rot);
        Vector3 ps = tr.parent != null ? tr.parent.lossyScale : Vector3.one;
        tr.localScale = new Vector3(worldScale.x / Mathf.Max(1e-4f, Mathf.Abs(ps.x)), worldScale.y / Mathf.Max(1e-4f, Mathf.Abs(ps.y)), worldScale.z / Mathf.Max(1e-4f, Mathf.Abs(ps.z)));
    }

    // ------------------------------------------------------------------ reports

    /// <summary>Average / p99 of this director's tick (ms) over the last 600 frames.</summary>
    public void PerfStats(out float avg, out float p99, out int frames)
    {
        avg = p99 = 0f; frames = perfCount;
        if (perfCount == 0) return;
        var tmp = new float[perfCount];
        double sum = 0;
        for (int i = 0; i < perfCount; i++) { tmp[i] = tickMs[i]; sum += tickMs[i]; }
        System.Array.Sort(tmp);
        avg = (float)(sum / perfCount);
        p99 = tmp[Mathf.Clamp(Mathf.CeilToInt(perfCount * 0.99f) - 1, 0, perfCount - 1)];
    }

    /// <summary>Renderers switched on right now (kids + signs), for the §4.4 renderer budget.</summary>
    public int ActiveRenderers()
    {
        int n = 0;
        for (int i = 0; i < kids.Count; i++) if (kids[i].rig != null) n += kids[i].rig.ActiveRenderers;
        for (int i = 0; i < signs.Count; i++) if (signs[i].used) n += 2;
        return n;
    }

    public string Describe()
    {
        PerfStats(out float avg, out float p99, out int fr);
        return $"pens {pens.Count}, kids {kids.Count} (out {OutCount}; full {FullCount}, lite {LiteCount}; max {MaxFullSeen}/{MaxLiteSeen}), rigs {rigs.Count}, " +
               $"restyles {Restyles}, renderers {ActiveRenderers()}; waves {Waves}, honks {HonksHeard}, cheers {Cheers}, fan moments {FanMoments}, " +
               $"shies {Shies}, lines {Lines}; tick avg {avg:F4} ms p99 {p99:F4} ({fr} frames)";
    }
}

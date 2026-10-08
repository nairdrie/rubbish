using System.Collections.Generic;
using UnityEngine;

/// <summary>Occasional Pickle Park crossings, derived from the shared town clock on every peer.
/// Guard and children have rule-based pedestrian hit boxes and recover from shared cartoon impacts.</summary>
public sealed class CrossingGuardDirector : WorldLifeDirector
{
    public static CrossingGuardDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Schedule;
    public override string Name => "CrossingGuardDirector";
    const float ShowDistance = 115f;
    readonly List<Site> sites = new List<Site>(2);
    WorldLifeContext context;
    Transform root;
    public int SiteCount => sites.Count;
    public int ActiveCount { get; private set; }
    public int VisibleCount { get; private set; }
    public int HittableCount { get; private set; }

    sealed class Site
    {
        public Vector3 from, to, center, along;
        public float length, age;
        public byte district;
        public int children;
        public long serial = -1, dressedSerial = -1;
        public bool active, holding, scheduled;
        public Transform root;
        public CrossingGuardLook guard;
        public readonly PuppetRig[] kids = new PuppetRig[5];
        public readonly PuppetDice[] dice = new PuppetDice[5];
        public readonly CrossingWalker[] bodies = new CrossingWalker[6];
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() => Instance = null;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { Instance = new CrossingGuardDirector(); WorldLifeRunner.Register(Instance); }

    public override void Wake(WorldLifeContext c)
    {
        context = c; sites.Clear(); ActiveCount = VisibleCount = HittableCount = 0;
        root = PuppetKit.Node(c.Root, "[Guarded crossings]");
        // Both the generated zebras and navigation use these blueprint-derived park entrances.
        if (c.Graph != null && c.Graph.layoutId == CoastalCityLayout.LayoutId)
            foreach (var crossing in CoastalParkRoutes.Crossings()) AddSite(c.Graph, crossing.position + Vector3.up * .15f);
        else
        {
            AddSite(c.Graph, new Vector3(80f, .15f, 60f));
            AddSite(c.Graph, new Vector3(80f, .15f, 170f));
        }
        for (int i = 0; i < sites.Count; i++)
            for (int k = 0; k < 6; k++) sites[i].bodies[k] = new CrossingWalker(i * 8 + k, k == 0);
    }

    void AddSite(StreetGraph graph, Vector3 target)
    {
        if (graph == null || graph.edges == null || graph.nodes == null) return;
        foreach (var e in graph.edges)
        {
            if (e.kind != StreetGraph.EdgeKind.Crosswalk || e.a >= graph.NodeCount || e.b >= graph.NodeCount) continue;
            Vector3 a = graph.nodes[e.a].pos, b = graph.nodes[e.b].pos;
            if (((a + b) * .5f - target).sqrMagnitude > 1f) continue;
            Vector3 across = (b - a).normalized;
            sites.Add(new Site { from = a, to = b, center = (a + b) * .5f,
                length = Vector3.Distance(a, b), along = new Vector3(across.z, 0, -across.x), district = graph.nodes[e.a].district });
            break;
        }
    }

    public override void Sleep()
    {
        foreach (var site in sites)
        { foreach (var body in site.bodies) body?.Dispose(); site.guard?.Destroy(); }
        if (root != null) { if (Application.isPlaying) Object.Destroy(root.gameObject); else Object.DestroyImmediate(root.gameObject); }
        sites.Clear(); root = null; context = null; ActiveCount = VisibleCount = 0;
    }

    public override void Tick(WorldLifeContext c)
    {
        context = c; ActiveCount = VisibleCount = HittableCount = 0;
        for (int i = 0; i < sites.Count; i++)
        {
            Site s = sites[i];
            bool scheduled = GuardedCrossingRules.TryEvent(c.Hours, c.TownSeed, i, sites.Count, s.length,
                out long serial, out float age, out int children) && (c.UnlockedMask & (1 << s.district)) != 0;
            s.scheduled = scheduled;
            bool finishingHit = false;
            foreach (var body in s.bodies) finishingHit |= body.Reacting;
            s.active = scheduled || finishingHit;
            if (!s.active)
            {
                s.holding = false;
                foreach (var body in s.bodies) body.Schedule(false, s.serial, body.Position, body.Rotation);
                if (s.root != null) s.root.gameObject.SetActive(false);
                continue;
            }
            if (scheduled) { s.serial = serial; s.age = age; s.children = children; }
            else s.age += Mathf.Max(0f, c.DeltaTime);
            ActiveCount++;
            float end = GuardedCrossingRules.CrossEnd(s.length, s.children);
            s.holding = scheduled && s.age < end + GuardedCrossingRules.GuardLeaveSeconds;
            bool show = (c.Viewer - s.center).sqrMagnitude <= ShowDistance * ShowDistance;
            if (show) { EnsureLooks(s, i); s.root.gameObject.SetActive(true); VisibleCount++; }
            else if (s.root != null) s.root.gameObject.SetActive(false);
            Pose(s, c.DeltaTime, show);
            foreach (var body in s.bodies) if (body.Hittable) HittableCount++;
        }
    }

    void EnsureLooks(Site s, int index)
    {
        if (s.root == null)
        {
            s.root = PuppetKit.Node(root, index == 0 ? "Maple park crossing" : "Birch park crossing");
            s.guard = new CrossingGuardLook(s.root, LifeHash.Mix(context.TownSeed ^ (uint)index ^ 0x6711UL));
            for (int k = 0; k < s.kids.Length; k++) s.kids[k] = PuppetRig.Build(s.root, "Crossing kid " + (k + 1));
        }
        if (s.dressedSerial == s.serial) return;
        s.dressedSerial = s.serial;
        var spec = new RigSpec();
        for (int k = 0; k < s.kids.Length; k++)
        {
            ulong hash = LifeHash.Mix(context.TownSeed ^ (ulong)s.serial ^ ((ulong)(index * 8 + k) << 24) ^ 0xC2011UL);
            var style = KidLook.StyleFor(hash);
            style.prop = KidProp.None; style.outfit = KidOutfit.None;
            KidLook.Compose(style, false, spec); s.kids[k].Apply(spec);
            s.dice[k] = new PuppetDice(hash);
            s.kids[k].go.SetActive(k < s.children);
        }
    }

    void Pose(Site s, float dt, bool show)
    {
        float age = s.age, end = GuardedCrossingRules.CrossEnd(s.length, s.children);
        bool reverse = (s.serial & 1) != 0;
        Vector3 from = reverse ? s.to : s.from, to = reverse ? s.from : s.to;
        Vector3 across = (to - from).normalized, along = s.along;
        Vector3 curb = from + along * 1.4f, post = s.center + along * 1.4f;
        float enter = Mathf.Clamp01((age - GuardedCrossingRules.GatherSeconds) / GuardedCrossingRules.GuardEnterSeconds);
        float leave = Mathf.Clamp01((age - end) / GuardedCrossingRules.GuardLeaveSeconds);
        Vector3 guard = Vector3.Lerp(curb, post, enter * (1f - leave));
        bool walking = (enter > 0 && enter < 1) || (leave > 0 && leave < 1);
        Vector3 face = walking ? (leave > 0 ? -across : across) : along;
        s.bodies[0].Schedule(s.scheduled || s.bodies[0].Reacting, s.serial, Ground(guard, from.y), Quaternion.LookRotation(face));
        if (show)
        {
            s.guard.Pose(s.bodies[0].Position, Quaternion.LookRotation(face), walking ? 1.6f : 0f, age,
                s.holding, Mathf.Max(0, dt));
            s.bodies[0].PoseReaction(s.guard.Rig);
        }
        float fade = Mathf.Min(Mathf.Clamp01(age / .5f), Mathf.Clamp01((GuardedCrossingRules.Duration(s.length, s.children) - age) / .7f));
        if (show) s.guard.Root.localScale = Vector3.one * (s.bodies[0].Reacting ? 1f : fade);
        for (int k = s.children + 1; k < 6; k++) s.bodies[k].Schedule(false, s.serial, from, Quaternion.identity);
        for (int k = 0; k < s.children; k++)
        {
            float progress = GuardedCrossingRules.ChildProgress(age, s.length, k);
            Vector3 p; Vector3 heading; bool move;
            if (progress <= 0)
            {
                float gather = Mathf.Clamp01(age / GuardedCrossingRules.GatherSeconds);
                float release = GuardedCrossingRules.GatherSeconds + GuardedCrossingRules.GuardEnterSeconds + k * GuardedCrossingRules.KidSpacingSeconds;
                float queue = Mathf.Min(k * .68f, Mathf.Max(0f, release - age) * .8f);
                p = from + along * (-.65f - queue - (1f - gather) * 3f);
                move = gather < 1f || (queue > 0f && queue < k * .68f);
                heading = move ? along : across;
            }
            else if (progress < 1)
            {
                p = Vector3.Lerp(from, to, progress) - along * .65f;
                heading = across; move = true;
            }
            else
            {
                float arrived = GuardedCrossingRules.GatherSeconds + GuardedCrossingRules.GuardEnterSeconds +
                    s.length / GuardedCrossingRules.WalkSpeed + k * GuardedCrossingRules.KidSpacingSeconds;
                p = to + along * Mathf.Min(7f, -.65f + (age - arrived) * 1.15f);
                heading = along; move = true;
            }
            p = Ground(p, from.y);
            var body = s.bodies[k + 1];
            body.Schedule(s.scheduled || body.Reacting, s.serial, p, Quaternion.LookRotation(heading));
            if (!show) continue;
            var rig = s.kids[k];
            rig.Place(p, Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg);
            float swing = move ? Mathf.Sin(age * 9f + k * 1.7f) : 0f;
            rig.Body(new Vector3(0, Mathf.Abs(swing) * .035f, 0), Quaternion.identity, 0);
            rig.Torso(move ? 6f : 0f, swing * 2f); rig.Head(0, 0, 0);
            rig.Arms(-swing * 35f, 8f, swing * 35f, 8f); rig.Legs(swing * 35f, -swing * 35f);
            rig.SetMouth(.12f, 1f);
            rig.TickEyes(dt, ref s.dice[k]); rig.juice.localScale = Vector3.one * (body.Reacting ? 1f : fade);
            body.PoseReaction(rig);
        }
    }

    static Vector3 Ground(Vector3 p, float sidewalkY)
    {
        // Crosswalk graph points sit at sidewalk height; the road between them is at zero.
        // A ray only accepts near-height static ground, never a passing truck/character.
        if (Physics.Raycast(p + Vector3.up * .35f, Vector3.down, out var hit, .65f,
            LayerMask.GetMask("Ground"), QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.point.y - sidewalkY) < .3f)
            p.y = hit.point.y + .015f;
        return p;
    }

    public static long EventSerial(LifeId id) => FindBody(id, -1) is CrossingWalker body ? body.Serial : -1;

    public static ILifeBody FindBody(LifeId id, long eventSerial)
    {
        var d = Instance;
        if (d == null || id.kind != LifeKind.Crossing) return null;
        int site = id.index / 8, actor = id.index % 8;
        if (site >= d.sites.Count || actor >= 6) return null;
        var body = d.sites[site].bodies[actor];
        return body != null && body.Active && (eventSerial < 0 || body.Serial == eventSerial) ? body : null;
    }

    public static void PresentHit(LifeId id, long eventSerial, in NpcHit hit)
    { if (FindBody(id, eventSerial) is CrossingWalker body) body.Apply(in hit); }

    public static float StopGap(CarPath path, float arc, float halfLength, float lookAhead)
    {
        var d = Instance;
        if (d == null || d.context == null) return float.PositiveInfinity;
        float gap = float.PositiveInfinity;
        foreach (var s in d.sites) if (s.active && s.holding)
            gap = Mathf.Min(gap, GuardedCrossingRules.SegmentStopGap(s.from, s.to, path, arc, halfLength, lookAhead));
        return gap;
    }

    public static bool HoldsTrafficNear(Vector3 position, float radius)
    {
        var d = Instance;
        if (d == null || d.context == null) return false;
        foreach (var s in d.sites)
        {
            if (!s.active || !s.holding || Mathf.Abs(position.y - s.center.y) > 3f) continue;
            Vector3 delta = position - s.center;
            if (Mathf.Abs(Vector3.Dot(delta, s.along)) <= radius + GuardedCrossingRules.StopMargin &&
                Mathf.Abs(Vector3.Dot(delta, (s.to - s.from).normalized)) <= s.length * .5f + radius) return true;
        }
        return false;
    }
}

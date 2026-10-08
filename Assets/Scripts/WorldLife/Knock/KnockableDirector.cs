using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// WorldLife package B (Docs/WorldLife.md §6): knockable street furniture. Lamp posts, mailboxes, wheelie bins, signs,
/// picket panels, lemonade stands, hoops, drying racks, park benches and bins, hydrants, cones, sawhorses and flamingos
/// fly when the truck hits them at speed, and come back later.
///
/// <para><b>Lifecycle</b> (§6.3): <b>rest</b> (the builder's batching-static object and its rest collider) →
/// <b>detect</b> (a <see cref="ITruckSensorClient"/>: the runner's predictive truck box, once per FixedUpdate before the
/// physics step, only for trucks this machine simulates; at or above the kind's speed the rest collider is switched off
/// that same step, so PhysX never resolves the contact; below it the prop is a solid wall you can nudge) →
/// <b>swap</b> (the original's renderers hide; a pooled proxy built by <see cref="KnockProxyPool"/> flies from the rest
/// pose with v·1.1 + up·(1.5 + 0.1|v|), a seeded spin, a push out of the truck's path, and ignores that truck for 0.3 s;
/// the truck loses the kind's speed fraction once, capped at 35 % a second) → <b>settle</b> (3 s at rest = kinematic,
/// still an obstacle; drive into it again and it tumbles again) → <b>reset</b> (decided on the host: down ≥ 120 s and no
/// player within 25 m, and everything at 06:00 "City Works was here"; the proxy shrinks away in a poof, the original
/// comes back, a lamp flickers on).</para>
///
/// <para><b>Caps</b> (§4.2): ≤ 24 awake proxies (over it, the slowest freezes, or poofs if it's still flying); ≤ 80
/// props down (the 81st knock resets the oldest early with a poof). <b>Budget</b> (§4.4): WorldLife.Knock 0.05 ms, the
/// sensor inside WorldLife.Sensor; 0 B per frame steady state.</para>
///
/// <para><b>Decide / present</b> (rule 5): the knock is presented at once by the machine that simulates the truck and
/// claimed with <see cref="IWorldLifeAuthority.ClaimKnock"/> (E: the City Works tally). Resets are decided on the host
/// (<see cref="ResetsFromHost"/> lets F make guests wait for the host's KnockReset). F's seams: <see cref="PresentRemoteKnock"/>,
/// <see cref="PresentRemoteReset"/>, <see cref="CopyDownBits"/>. Debris is local cosmetics on every machine (rule 13).</para>
/// </summary>
public sealed class KnockableDirector : WorldLifeDirector, ITruckSensorClient
{
    public static KnockableDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Knock;
    public override string Name => "KnockableDirector";

    /// <summary>F: true on guests once WorldLifeNet relays the host's KnockReset. Until then every machine resets its own
    /// props by the same rule (a guest's knocks are local-only before F, §10.3).</summary>
    public static bool ResetsFromHost;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        Instance = new KnockableDirector();
        WorldLifeRunner.Register(Instance);
        CoopPlayerHooks.Spawned -= OnPlayerSpawned;
        CoopPlayerHooks.Spawned += OnPlayerSpawned;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        players.Clear();
        ResetsFromHost = false;
    }

    // ------------------------------------------------------------------ players (rule 8: every player, never Local)

    static readonly List<PlayerController> players = new List<PlayerController>(8);

    static void OnPlayerSpawned(PlayerController p)
    {
        if (p != null && !players.Contains(p)) players.Add(p);
    }

    /// <summary>Is any player (any machine's, from replicated positions) within <paramref name="radius"/> of the point?</summary>
    public static bool PlayerWithin(Vector3 p, float radius)
    {
        float r2 = radius * radius;
        for (int i = players.Count - 1; i >= 0; i--)
        {
            var pc = players[i];
            if (pc == null) { players.RemoveAt(i); continue; }
            if (!pc.isActiveAndEnabled) continue;
            if ((pc.transform.position - p).sqrMagnitude <= r2) return true;
        }
        return false;
    }

    public static int PlayerCount => players.Count;

    // ------------------------------------------------------------------ state

    /// <summary>A prop that is down on this machine.</summary>
    public sealed class Down
    {
        public Knockable k;
        public ushort id;
        public KnockKind kind;
        public Vector3 pos, top;
        public double downAt;
        public int tick;
        public bool lamp, cityWorks;
        public KnockProxy proxy;
        public readonly List<Renderer> hidden = new List<Renderer>(8);
        public readonly List<Collider> colliders = new List<Collider>(4);
        public readonly List<TextHome> texts = new List<TextHome>(2);
        public void Clear() { k = null; proxy = null; hidden.Clear(); colliders.Clear(); texts.Clear(); cityWorks = false; }
    }

    /// <summary>A TMP label riding on a proxy, and where it lives at rest.</summary>
    public struct TextHome
    {
        public Transform t, parent;
        public Vector3 lp, ls;
        public Quaternion lr;
    }

    sealed class TruckState
    {
        // The speed lost to props in the last second (a ring of (time, fraction)): the 35 %/s feel cap is a sliding window.
        public readonly double[] lossAt = new double[16];
        public readonly float[] loss = new float[16];
        public int lossHead;
        public Collider[] colliders;
        public double lastShake = -99;

        public float LostInLastSecond(double now)
        {
            float sum = 0f;
            for (int i = 0; i < loss.Length; i++) if (loss[i] > 0f && now - lossAt[i] < 1.0) sum += loss[i];
            return sum;
        }

        public void Add(double now, float f)
        {
            lossAt[lossHead] = now;
            loss[lossHead] = f;
            lossHead = (lossHead + 1) % loss.Length;
        }
    }

    /// <summary>The fraction of speed a knock takes off: the kind's loss, limited so props never take more than
    /// <see cref="WorldLifeTuning.Props.SpeedLossCapPerSecond"/> of the truck's speed within any one second.</summary>
    public static float CappedLoss(float wanted, float lostInLastSecond) =>
        Mathf.Clamp(Mathf.Min(wanted, WorldLifeTuning.Props.SpeedLossCapPerSecond - lostInLastSecond), 0f, 1f);

    WorldLifeContext ctx;
    KnockProxyPool pool;
    KnockFx fx;
    public HydrantGeysers Geysers { get; private set; }

    readonly Collider[] overlap = new Collider[128];
    int layerMask, maskForCount = -1;

    readonly List<Down> down = new List<Down>(96);
    readonly Dictionary<ushort, Down> downById = new Dictionary<ushort, Down>(128);
    readonly Stack<Down> freeDowns = new Stack<Down>(32);
    readonly List<KnockProxy> awake = new List<KnockProxy>(32);
    readonly List<KnockProxy> shrinking = new List<KnockProxy>(16);
    readonly List<KnockProxy> stuck = new List<KnockProxy>(4);
    readonly List<KnockProxy> ignoring = new List<KnockProxy>(16);
    readonly List<KnockProxy> debris = new List<KnockProxy>(KnockTuning.MaxDebris);
    readonly Dictionary<Knockable, KnockShape> shapes = new Dictionary<Knockable, KnockShape>(128);
    readonly Dictionary<TruckControllerV2, TruckState> trucks = new Dictionary<TruckControllerV2, TruckState>(4);
    readonly List<Renderer> tmpRenderers = new List<Renderer>(16);

    double nextResetCheck, nextCityWorks;
    int cityWorksReset;

    // ---- stats (tests, the profiler report)
    public int Knocks { get; private set; }
    public int Resets { get; private set; }
    public int CityWorksResets { get; private set; }
    public int Rekicks { get; private set; }
    public int MaxAwakeSeen { get; private set; }
    public int CapFreezes { get; private set; }
    public int CapPoofs { get; private set; }
    public float SpeedLostTotal { get; private set; }
    public int DownCount => down.Count;
    public int AwakeCount => awake.Count;
    public int DebrisCount => debris.Count;
    public int ProxiesCreated => pool != null ? pool.Created : 0;
    public IReadOnlyList<Down> DownList => down;
    public double NextCityWorksHours => nextCityWorks;

    // ------------------------------------------------------------------ wake / sleep

    public override void Wake(WorldLifeContext c)
    {
        ctx = c;
        pool = new KnockProxyPool(c.Root);
        fx = new KnockFx(this);
        Geysers = new HydrantGeysers(c.Root, this);
        maskForCount = -1;
        nextResetCheck = 0;
        nextCityWorks = NextCityWorksAfter(c.Hours);
        Knocks = Resets = CityWorksResets = Rekicks = MaxAwakeSeen = CapFreezes = CapPoofs = 0;
        SpeedLostTotal = 0f;
        WorldLifeRunner.AddSensorClient(this);
    }

    public override void Sleep()
    {
        WorldLifeRunner.RemoveSensorClient(this);
        // Put every prop back as it was (no juice): the scene may be staying (an anchor switched off) or going away.
        for (int i = down.Count - 1; i >= 0; i--) RestoreQuiet(down[i]);
        down.Clear();
        downById.Clear();
        awake.Clear(); shrinking.Clear(); stuck.Clear(); ignoring.Clear(); debris.Clear();
        shapes.Clear();
        trucks.Clear();
        Geysers?.Clear();
        fx?.Clear();
        pool = null;   // its objects live under ctx.Root, which the runner destroys
        ctx = null;
    }

    /// <summary>The next 06:00 strictly after <paramref name="hours"/> (GameClock.TotalHours).</summary>
    public static double NextCityWorksAfter(double hours)
    {
        double h6 = WorldLifeTuning.Props.CityWorksHour;
        double day = Math.Floor((hours - h6) / 24.0);
        return (day + 1.0) * 24.0 + h6;
    }

    // ------------------------------------------------------------------ the sensor (WorldLife.Sensor)

    public void Sense(in TruckSweep s)
    {
        if (ctx == null || pool == null) return;
        EnsureMask();
        if (layerMask == 0) return;
        // Reach down to the ground: the body box starts 0.41 m up and cones / flamingos are short.
        Vector3 he = s.halfExtents;
        float ext = KnockTuning.SweepExtendDown;
        he.y += ext * 0.5f;
        Vector3 c = s.center - (s.rotation * Vector3.up) * (ext * 0.5f);
        int n = Physics.OverlapBoxNonAlloc(c, he, overlap, s.rotation, layerMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = overlap[i];
            var k = Knockable.ForCollider(col);
            if (k != null)
            {
                if (!k.IsDown && s.speed >= k.Spec.knockSpeed) KnockLocal(k, in s);
                continue;
            }
            if (pool.byCollider.TryGetValue(col, out var p)) MaybeRekick(p, in s);
        }
    }

    void EnsureMask()
    {
        int count = Knockable.Count;
        if (count == maskForCount) return;
        maskForCount = count;
        int mask = 1 << KnockProxyPool.ProxyLayer;
        var all = Knockable.All;
        for (int i = 0; i < all.Count; i++)
        {
            var k = all[i];
            if (k == null) continue;
            var cols = k.RestColliders;
            for (int j = 0; j < cols.Length; j++) if (cols[j] != null) mask |= 1 << cols[j].gameObject.layer;
        }
        layerMask = mask & ~(1 << 2);
    }

    // ------------------------------------------------------------------ knock (predicted on the simulating machine)

    void KnockLocal(Knockable k, in TruckSweep s)
    {
        var spec = k.Spec;
        ulong h = LifeHash.U64(ctx.TownSeed, LifeKind.Prop, k.id, s.tick);
        byte seed = (byte)(h & 0xFF);
        Vector3 launch = LaunchVelocity(k.transform.position, s.velocity, s.center, s.rotation, h);
        var d = Present(k, launch, h, s.tick, s.truck, s.speed);
        if (d == null) return;
        float lost = ApplySpeedLoss(in s, spec.speedLoss);
        var truck = s.truck;
        if (truck != null && truck.IsLocalPlayerSeated)
        {
            var ts = StateFor(truck);
            double now = Time.timeAsDouble;
            if (now - ts.lastShake > 0.12) { ts.lastShake = now; Juice.Shake(Mathf.Clamp(0.04f + spec.massKg * 0.0015f + lost * 0.4f, 0.04f, 0.16f), 0.15f); }
        }
        var fact = MakeFact(k, d, launch, seed, 0);
        var claim = new KnockClaim { knockId = k.id, tick = s.tick, impulse = launch, truckVelocity = s.velocity, seed = seed, actorSlot = 0, truck = truck };
        try { WorldLifeRunner.Authority.ClaimKnock(in claim, in fact); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>§6.3 step 4: v·1.1 + up·(1.5 + 0.1|v|), plus a seeded push out of the truck's path (local cosmetics).</summary>
    public static Vector3 LaunchVelocity(Vector3 propPos, Vector3 truckVel, Vector3 truckCentre, Quaternion truckRot, ulong h)
    {
        float speed = truckVel.magnitude;
        Vector3 v = truckVel * WorldLifeTuning.Props.LaunchVelMult
                    + Vector3.up * (WorldLifeTuning.Props.LaunchUp + WorldLifeTuning.Props.LaunchUpPerSpeed * speed);
        Vector3 right = truckRot * Vector3.right; right.y = 0f;
        right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
        float side = Vector3.Dot(propPos - truckCentre, right) >= 0f ? 1f : -1f;
        float k = 0.6f + 0.8f * LifeHash.Float01(h);
        return v + right * side * (KnockTuning.SidePush + KnockTuning.SidePushPerSpeed * speed) * k;
    }

    static Vector3 Spin(KnockKind kind, ulong h, Vector3 launch)
    {
        float a = LifeHash.Float01(h >> 3) * Mathf.PI * 2f, b = LifeHash.Float01(h >> 17) * 2f - 1f;
        float sign = (h & 0x100) == 0 ? 1f : -1f;
        if (kind == KnockKind.Sign) return Vector3.up * KnockTuning.PropellerSpin * sign;   // spins like a propeller
        Vector3 axis = new Vector3(Mathf.Cos(a), b * 0.6f, Mathf.Sin(a)).normalized;
        float rate = Mathf.Lerp(KnockTuning.SpinMin, KnockTuning.SpinMax, LifeHash.Float01(h >> 29));
        if (kind == KnockKind.Flamingo || kind == KnockKind.Cone) rate *= 1.8f;
        // Tumble forward over the top: mostly about the axis across the launch direction.
        Vector3 flat = new Vector3(launch.x, 0f, launch.z);
        if (flat.sqrMagnitude > 0.01f) axis = (Vector3.Cross(Vector3.up, flat.normalized) + axis * 0.5f).normalized;
        return axis * rate * sign;
    }

    /// <summary>
    /// Present a knock on this machine: hide the original, switch its rest colliders off, fly a proxy, juice it, raise
    /// <see cref="WorldLifeEvents.PropKnocked"/>. Returns null if it was already down. Used by the sensor (predicted),
    /// F (the host's KnockEvent) and the sandbox.
    /// </summary>
    Down Present(Knockable k, Vector3 launch, ulong h, int tick, TruckControllerV2 truck, float speed)
    {
        if (k == null || k.IsDown || pool == null) return null;
        if (down.Count >= WorldLifeTuning.Props.MaxDown) ResetEntry(down[0], false);

        KnockShape shape;
        try { shape = ShapeFor(k); }
        catch (Exception e) { Debug.LogException(e); return null; }

        var spec = k.Spec;
        var d = freeDowns.Count > 0 ? freeDowns.Pop() : new Down();
        d.k = k;
        d.id = k.id;
        d.kind = k.kind;
        d.pos = k.transform.position;
        d.downAt = Time.timeAsDouble;
        d.tick = tick;
        d.lamp = k.kind == KnockKind.Lamp;
        k.IsDown = true;

        var rc = k.RestColliders;
        for (int i = 0; i < rc.Length; i++)
        {
            var c = rc[i];
            if (c != null && c.enabled) { c.enabled = false; d.colliders.Add(c); }
        }

        float topY = d.pos.y + 1f;
        bool first = true;
        KnockShape.CollectRenderers(k, tmpRenderers);
        for (int i = 0; i < tmpRenderers.Count; i++)
        {
            var r = tmpRenderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            var b = r.bounds;
            if (first || b.max.y > topY) { topY = b.max.y; first = false; }
            if (shape.prefab == null && r.TryGetComponent<TMP_Text>(out _))
            {
                // Builder-made sign text rides on the proxy (it's never static), and goes home on reset.
                var t = r.transform;
                d.texts.Add(new TextHome { t = t, parent = t.parent, lp = t.localPosition, lr = t.localRotation, ls = t.localScale });
                continue;
            }
            r.enabled = false;
            d.hidden.Add(r);
            if (d.lamp) StreetLamps.SetLampDown(r, true);
        }
        tmpRenderers.Clear();
        d.top = new Vector3(d.pos.x, topY, d.pos.z);

        var p = pool.Acquire(shape, k, spec.massKg);
        p.owner = d;
        d.proxy = p;
        for (int i = 0; i < d.texts.Count; i++) d.texts[i].t.SetParent(p.t, true);
        p.rb.linearVelocity = launch;
        p.rb.angularVelocity = Spin(k.kind, h, launch);
        if (truck != null) IgnoreTruck(p, truck);
        AddAwake(p);

        down.Add(d);
        downById[d.id] = d;
        Knocks++;
        try { fx.Knocked(k, d, p, launch, speed, h, truck, Time.timeAsDouble); }
        catch (Exception e) { Debug.LogException(e); }
        var fact = MakeFact(k, d, launch, (byte)(h & 0xFF), 0);
        WorldLifeEvents.RaisePropKnocked(in fact);
        return d;
    }

    KnockFact MakeFact(Knockable k, Down d, Vector3 launch, byte seed, byte actor) => new KnockFact
    {
        knockId = k.id, kind = k.kind, pos = d.pos, impulse = launch, seed = seed, actorSlot = actor, district = k.district,
        atHours = ctx != null ? ctx.Hours : GameClock.TotalHours,
    };

    KnockShape ShapeFor(Knockable k)
    {
        if (shapes.TryGetValue(k, out var s)) return s;
        s = KnockShape.Resolve(k);
        shapes[k] = s;
        return s;
    }

    TruckState StateFor(TruckControllerV2 t)
    {
        if (trucks.TryGetValue(t, out var s)) return s;
        s = new TruckState();
        var all = t.GetComponentsInChildren<Collider>(true);
        int n = 0;
        for (int i = 0; i < all.Length; i++) if (all[i] != null && !all[i].isTrigger && !(all[i] is WheelCollider)) n++;
        s.colliders = new Collider[n];
        n = 0;
        for (int i = 0; i < all.Length; i++) if (all[i] != null && !all[i].isTrigger && !(all[i] is WheelCollider)) s.colliders[n++] = all[i];
        trucks[t] = s;
        return s;
    }

    /// <summary>§6.2: the truck loses the kind's fraction of its speed once, but never more than 35 % in any second.</summary>
    float ApplySpeedLoss(in TruckSweep s, float loss)
    {
        if (loss <= 0f || s.body == null || s.truck == null) return 0f;
        var ts = StateFor(s.truck);
        double now = Time.timeAsDouble;
        float apply = CappedLoss(loss, ts.LostInLastSecond(now));
        if (apply <= 0f) return 0f;
        ts.Add(now, apply);
        var v = s.body.linearVelocity;
        v.x *= 1f - apply;
        v.z *= 1f - apply;
        s.body.linearVelocity = v;
        SpeedLostTotal += apply;
        return apply;
    }

    void IgnoreTruck(KnockProxy p, TruckControllerV2 truck)
    {
        if (p.ignoreTruck != null) Unignore(p);
        var ts = StateFor(truck);
        for (int i = 0; i < p.colliders.Length; i++)
        {
            var a = p.colliders[i];
            if (a == null) continue;
            for (int j = 0; j < ts.colliders.Length; j++) if (ts.colliders[j] != null) Physics.IgnoreCollision(a, ts.colliders[j], true);
        }
        p.ignoreTruck = truck;
        p.ignoreUntil = Time.timeAsDouble + WorldLifeTuning.Props.IgnoreTruckSeconds;
        if (!ignoring.Contains(p)) ignoring.Add(p);
    }

    void Unignore(KnockProxy p)
    {
        var truck = p.ignoreTruck;
        p.ignoreTruck = null;
        p.ignoreUntil = -1;
        if (truck == null || !trucks.TryGetValue(truck, out var ts)) return;
        for (int i = 0; i < p.colliders.Length; i++)
        {
            var a = p.colliders[i];
            if (a == null) continue;
            for (int j = 0; j < ts.colliders.Length; j++) if (ts.colliders[j] != null) Physics.IgnoreCollision(a, ts.colliders[j], false);
        }
    }

    // ------------------------------------------------------------------ awake cap, settle, rekick

    void AddAwake(KnockProxy p)
    {
        if (!p.inAwake) { p.inAwake = true; awake.Add(p); }
        p.frozen = false;
        p.restSeconds = 0f;
        while (awake.Count > WorldLifeTuning.Props.MaxAwakeProxies)
        {
            // The slowest one settles early; if even that one is still flying it poofs (the prop stays down).
            int best = -1;
            float bestV = float.MaxValue;
            for (int i = 0; i < awake.Count; i++)
            {
                if (awake[i] == p) continue;
                float v = awake[i].rb.linearVelocity.sqrMagnitude;
                if (v < bestV) { bestV = v; best = i; }
            }
            if (best < 0) break;
            var q = awake[best];
            if (bestV <= KnockTuning.CapFreezeMaxSpeed * KnockTuning.CapFreezeMaxSpeed) { Freeze(q); CapFreezes++; }
            else { Park(q); CapPoofs++; }
        }
        if (awake.Count > MaxAwakeSeen) MaxAwakeSeen = awake.Count;
    }

    void RemoveAwake(KnockProxy p)
    {
        if (!p.inAwake) return;
        p.inAwake = false;
        awake.Remove(p);
    }

    void Freeze(KnockProxy p)
    {
        RemoveAwake(p);
        if (!p.rb.isKinematic) { p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; }
        p.rb.isKinematic = true;   // still an obstacle, at zero cost
        p.frozen = true;
    }

    /// <summary>Take a proxy out of the world (shrink + poof) while its prop stays down; its texts go home hidden.</summary>
    void Park(KnockProxy p)
    {
        RemoveAwake(p);
        var d = p.owner;
        if (d != null)
        {
            SendTextsHome(d, hide: true);
            d.proxy = null;
            p.owner = null;
        }
        p.parked = true;
        StartShrink(p);
    }

    void StartShrink(KnockProxy p)
    {
        if (p.shrinking) return;
        RemoveAwake(p);
        if (p.ignoreTruck != null) Unignore(p);
        p.shrinking = true;
        p.shrinkT = 0f;
        if (!p.rb.isKinematic) { p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; }
        p.rb.isKinematic = true;
        for (int i = 0; i < p.colliders.Length; i++) if (p.colliders[i] != null) p.colliders[i].enabled = false;
        if (p.stuckTo != null) { p.stuckTo = null; stuck.Remove(p); }
        Fx.Dust(p.t.position, 0.7f);
        shrinking.Add(p);
    }

    /// <summary>
    /// Debris in the truck's way is batted aside by rule too (never plowed: a plowed lamp post would be a wall). A settled
    /// proxy wakes; at ≥ <see cref="KnockTuning.RekickMinSpeed"/> it (or an awake one slower than the truck) gets a fresh
    /// launch out of the truck's path and ignores the truck for 0.3 s. No speed loss. Below that speed a settled proxy just
    /// wakes, so the truck can nudge it.
    /// </summary>
    void MaybeRekick(KnockProxy p, in TruckSweep s)
    {
        if (p.shrinking || p.parked || p.stuckTo != null || p.poolKey == "debris") return;
        double now = Time.timeAsDouble;
        if (s.speed < KnockTuning.RekickMinSpeed)
        {
            if (p.frozen) { p.rb.isKinematic = false; AddAwake(p); }
            return;
        }
        if (now - p.lastKick < KnockTuning.RekickInterval) return;
        if (!p.frozen && s.speed > 0.01f && Vector3.Dot(p.rb.linearVelocity, s.velocity / s.speed) > s.speed * 0.9f) return;   // already clear
        p.lastKick = now;
        p.rb.isKinematic = false;
        ulong h = LifeHash.U64(ctx.TownSeed, LifeKind.Prop, p.owner != null ? p.owner.id : 0, s.tick, 7);
        Vector3 v = LaunchVelocity(p.t.position, s.velocity * KnockTuning.RekickVelMult, s.center, s.rotation, h);
        v.y = Mathf.Min(v.y, KnockTuning.RekickUp + 0.08f * s.speed);
        p.rb.linearVelocity = v;
        p.rb.angularVelocity = Spin(p.owner != null ? p.owner.kind : KnockKind.Cone, h, v) * 0.7f;
        IgnoreTruck(p, s.truck);
        AddAwake(p);
        Rekicks++;
        if (now - lastRekickSfx > 0.25) { lastRekickSfx = now; Sfx.Play("thud", p.t.position, 0.5f, 1.3f); }
    }
    double lastRekickSfx = -99;

    // ------------------------------------------------------------------ NPC cars vs debris (fixer pass)

    /// <summary>Fallen props batted out of the way by package D's NPC cars (tests, the report).</summary>
    public int CarBats { get; private set; }

    /// <summary>
    /// Package D's shown cars (local presentation): a fallen prop lying in a car's way is batted aside by the same rule as
    /// the truck's re-kick, instead of the car driving through it. (A car shell is a kinematic box moved by its transform,
    /// so PhysX never pushed settled, kinematic debris.) <paramref name="center"/> / <paramref name="rot"/> (yaw) /
    /// <paramref name="halfExtents"/> are the car's box, stretched <paramref name="reach"/> m forward. A settled proxy wakes;
    /// at ≥ <see cref="KnockTuning.RekickMinSpeed"/> it gets a fresh launch out of the car's path. Returns the mass (kg) of the
    /// heaviest prop batted, so the car can lose a little speed; 0 when nothing was in the way. A loop over the down list
    /// (≤ 80) with a distance reject first: no physics query, 0 B.
    /// </summary>
    public float BatForCar(Vector3 center, Quaternion rot, Vector3 halfExtents, float reach, Vector3 velocity, int salt)
    {
        if (ctx == null || pool == null || down.Count == 0) return 0f;
        Vector3 f = rot * Vector3.forward; f.y = 0f;
        if (f.sqrMagnitude < 1e-6f) return 0f;
        f.Normalize();
        Vector3 r = new Vector3(f.z, 0f, -f.x);
        float hz = halfExtents.z + reach * 0.5f, hx = halfExtents.x;
        Vector3 c = center + f * (reach * 0.5f);
        float r0 = hz + hx + KnockTuning.CarBatSlack;
        float kg = 0f;
        for (int i = 0; i < down.Count; i++)
        {
            var p = down[i].proxy;
            if (p == null || p.shrinking || p.parked || p.stuckTo != null || p.go == null || !p.go.activeSelf) continue;
            Vector3 pp = p.t.position;
            float dx = pp.x - c.x, dz = pp.z - c.z;
            if (dx * dx + dz * dz > r0 * r0) continue;
            if (!TouchesCarBox(p, c, f, r, hx, hz, halfExtents.y)) continue;
            kg = Mathf.Max(kg, BatAside(p, c, rot, velocity, salt));
        }
        return kg;
    }

    /// <summary>Does any of the proxy's colliders (world AABB) overlap the car's yaw box? 2-D separating axes (the car's
    /// two and the world's two) plus a height band.</summary>
    static bool TouchesCarBox(KnockProxy p, Vector3 c, Vector3 f, Vector3 r, float hx, float hz, float hy)
    {
        var cols = p.colliders;
        for (int k = 0; k < cols.Length; k++)
        {
            var col = cols[k];
            if (col == null || !col.enabled) continue;
            Bounds b = col.bounds;
            if (b.max.y < c.y - hy - 0.3f || b.min.y > c.y + hy) continue;
            Vector3 d = b.center - c; d.y = 0f;
            Vector3 e = b.extents;
            if (Mathf.Abs(Vector3.Dot(d, r)) > hx + Mathf.Abs(r.x) * e.x + Mathf.Abs(r.z) * e.z) continue;
            if (Mathf.Abs(Vector3.Dot(d, f)) > hz + Mathf.Abs(f.x) * e.x + Mathf.Abs(f.z) * e.z) continue;
            if (Mathf.Abs(d.x) > e.x + Mathf.Abs(r.x) * hx + Mathf.Abs(f.x) * hz) continue;
            if (Mathf.Abs(d.z) > e.z + Mathf.Abs(r.z) * hx + Mathf.Abs(f.z) * hz) continue;
            return true;
        }
        return false;
    }

    float BatAside(KnockProxy p, Vector3 carCentre, Quaternion carRot, Vector3 velocity, int salt)
    {
        double now = Time.timeAsDouble;
        float speed = velocity.magnitude;
        if (speed < KnockTuning.RekickMinSpeed)
        {
            if (p.frozen) { p.rb.isKinematic = false; AddAwake(p); }
            return 0f;
        }
        if (now - p.lastKick < KnockTuning.RekickInterval) return 0f;
        if (!p.frozen && !p.rb.isKinematic && Vector3.Dot(p.rb.linearVelocity, velocity / speed) > speed * 0.9f) return 0f;   // already clear
        p.lastKick = now;
        p.rb.isKinematic = false;
        ulong h = LifeHash.U64(ctx.TownSeed, LifeKind.Prop, p.owner != null ? p.owner.id : 0, Time.frameCount, (uint)salt ^ 0xCA2u);
        Vector3 v = LaunchVelocity(p.t.position, velocity * KnockTuning.RekickVelMult, carCentre, carRot, h);
        v.y = Mathf.Min(v.y, KnockTuning.RekickUp + 0.08f * speed);
        p.rb.linearVelocity = v;
        p.rb.angularVelocity = Spin(p.owner != null ? p.owner.kind : KnockKind.Cone, h, v) * 0.7f;
        AddAwake(p);
        CarBats++;
        if (now - lastRekickSfx > 0.25) { lastRekickSfx = now; Sfx.Play("thud", p.t.position, 0.5f, 1.3f); }
        return p.rb.mass;
    }

    /// <summary>Is a truck's body (padded) against this proxy? Then it doesn't settle: a frozen proxy would be a wall.</summary>
    static bool NearAnyTruck(KnockProxy p)
    {
        var trucks = TruckControllerV2.All;
        if (trucks.Count == 0 || p.colliders.Length == 0 || p.colliders[0] == null) return false;
        Bounds pb = p.colliders[0].bounds;
        float r = pb.extents.magnitude + 0.5f;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null) continue;
            Bounds box = t.ModelBodyBoxMetres;
            Vector3 l = Quaternion.Inverse(t.transform.rotation) * (pb.center - t.transform.position);
            if (Mathf.Abs(l.x) <= box.extents.x + r && l.z >= box.min.z - r && l.z <= box.max.z + r && l.y <= box.max.y + r) return true;
        }
        return false;
    }

    /// <summary>A geyser caught a proxy or debris: wake it and send it up.</summary>
    public void GeyserLaunch(KnockProxy p, Vector3 v)
    {
        if (p == null || p.shrinking || p.parked || p.stuckTo != null) return;
        p.rb.isKinematic = false;
        p.rb.linearVelocity = v;
        p.rb.WakeUp();
        if (p.poolKey != "debris") AddAwake(p);
    }

    public bool TryGetProxy(Collider c, out KnockProxy p)
    {
        p = null;
        return pool != null && c != null && pool.byCollider.TryGetValue(c, out p) && p.go.activeInHierarchy;
    }

    // ------------------------------------------------------------------ debris and the cone on the bumper

    public void SpawnDebris(Mesh mesh, Material mat, Vector3 at, float diameter, float mass, Vector3 v)
    {
        if (pool == null) return;
        if (debris.Count >= KnockTuning.MaxDebris) { var old = debris[0]; debris.RemoveAt(0); StartShrink(old); }
        var p = pool.AcquireDebris(mesh, mat, at, diameter, mass);
        p.rb.linearVelocity = v;
        p.dieAt = Time.timeAsDouble + KnockTuning.DebrisSeconds;
        debris.Add(p);
    }

    /// <summary>"Sometimes a cone sticks to the bumper for 10 s" (§6.1).</summary>
    public void StickToBumper(KnockProxy p, TruckControllerV2 truck, ulong h, double now)
    {
        if (p == null || truck == null) return;
        RemoveAwake(p);
        if (p.ignoreTruck != null) Unignore(p);
        p.rb.linearVelocity = Vector3.zero;
        p.rb.angularVelocity = Vector3.zero;
        p.rb.isKinematic = true;
        for (int i = 0; i < p.colliders.Length; i++) if (p.colliders[i] != null) p.colliders[i].enabled = false;
        float x = (LifeHash.Float01(h >> 11) - 0.5f) * 1.6f;
        p.stuckTo = truck;
        p.stuckUntil = now + WorldLifeTuning.Props.ConeStickSeconds;
        p.stuckOffset = new Vector3(x, 0.95f, truck.ModelBodyBoxMetres.max.z + 0.2f);
        p.stuckRot = Quaternion.Euler(80f + 20f * LifeHash.Float01(h >> 21), 15f * (LifeHash.Float01(h >> 25) - 0.5f), 0f);
        stuck.Add(p);
        GameHUD.FloatingText(p.t.position + Vector3.up, "hood ornament!", KnockFx.ColorFor(KnockKind.Cone), 0.8f);
        FollowTruck(p, 0f);
    }

    void FollowTruck(KnockProxy p, float t)
    {
        var tr = p.stuckTo.transform;
        float wob = Mathf.Sin(t * 14f) * 4f;
        p.t.SetPositionAndRotation(tr.position + tr.rotation * p.stuckOffset, tr.rotation * p.stuckRot * Quaternion.Euler(wob, 0f, wob * 0.5f));
    }

    void Unstick(KnockProxy p)
    {
        var truck = p.stuckTo;
        p.stuckTo = null;
        stuck.Remove(p);
        for (int i = 0; i < p.colliders.Length; i++) if (p.colliders[i] != null) p.colliders[i].enabled = true;
        p.rb.isKinematic = false;
        p.rb.position = p.t.position;
        p.rb.rotation = p.t.rotation;
        Vector3 v = Vector3.up * 2.5f;
        if (truck != null)
        {
            var rb = truck.Body;
            if (rb != null) v += rb.linearVelocity;
            v += truck.transform.right * (p.stuckOffset.x >= 0f ? 1.5f : -1.5f);
            IgnoreTruck(p, truck);
        }
        p.rb.linearVelocity = v;
        p.rb.angularVelocity = new Vector3(4f, 0f, 3f);
        AddAwake(p);
        Sfx.Play("pop", p.t.position, 0.5f, 1.2f);
    }

    // ------------------------------------------------------------------ ticks (WorldLife.Knock)

    public override void Tick(WorldLifeContext c)
    {
        if (pool == null) return;
        double now = Time.timeAsDouble;
        float dt = c.DeltaTime;

        // settle: 3 s at rest → kinematic (still an obstacle)
        for (int i = awake.Count - 1; i >= 0; i--)
        {
            var p = awake[i];
            if (p.go == null) { awake.RemoveAt(i); continue; }
            if (p.t.position.y < KnockTuning.KillY) { Park(p); continue; }
            var rb = p.rb;
            bool still = rb.IsSleeping() ||
                         (rb.linearVelocity.sqrMagnitude < KnockTuning.SettleLinear * KnockTuning.SettleLinear &&
                          rb.angularVelocity.sqrMagnitude < KnockTuning.SettleAngular * KnockTuning.SettleAngular);
            if (still && NearAnyTruck(p)) still = false;
            p.restSeconds = still ? p.restSeconds + dt : 0f;
            if (p.restSeconds >= WorldLifeTuning.Props.SettleSeconds) Freeze(p);
        }

        // collision with the hitter's truck comes back after 0.3 s
        for (int i = ignoring.Count - 1; i >= 0; i--)
        {
            var p = ignoring[i];
            if (p.ignoreTruck == null) { ignoring.RemoveAt(i); continue; }
            if (now >= p.ignoreUntil) { Unignore(p); ignoring.RemoveAt(i); }
        }

        // shrink-outs (poof) back to the pool
        for (int i = shrinking.Count - 1; i >= 0; i--)
        {
            var p = shrinking[i];
            p.shrinkT += dt / KnockTuning.ShrinkSeconds;
            if (p.shrinkT >= 1f || p.go == null)
            {
                shrinking.RemoveAt(i);
                p.shrinking = false;
                pool.Release(p);
                continue;
            }
            float s = 1f - p.shrinkT * p.shrinkT;
            p.t.localScale = p.restScale * s;
        }

        // debris lifetime
        for (int i = debris.Count - 1; i >= 0; i--)
        {
            var p = debris[i];
            if (now >= p.dieAt || p.t.position.y < KnockTuning.KillY) { debris.RemoveAt(i); StartShrink(p); }
        }

        // cones on bumpers
        for (int i = stuck.Count - 1; i >= 0; i--)
        {
            var p = stuck[i];
            if (p.stuckTo == null || !p.stuckTo.isActiveAndEnabled || now >= p.stuckUntil) Unstick(p);
        }

        fx.Tick(now);
        Geysers.Tick(now);
        DecideResets(c, now);
    }

    public override void FixedTick(WorldLifeContext c)
    {
        if (pool == null) return;
        Geysers.FixedTick(c.IsHost, Time.timeAsDouble);
    }

    public override void LateTick(WorldLifeContext c)
    {
        if (stuck.Count == 0) return;
        float t = Time.time;
        for (int i = 0; i < stuck.Count; i++) if (stuck[i].stuckTo != null) FollowTruck(stuck[i], t);
    }

    // ------------------------------------------------------------------ resets (decided on the host)

    void DecideResets(WorldLifeContext c, double now)
    {
        if (!c.IsHost && ResetsFromHost) return;
        if (c.Hours >= nextCityWorks)
        {
            // 06:00: City Works fixes everything that's down right now.
            for (int i = 0; i < down.Count; i++) down[i].cityWorks = true;
            while (nextCityWorks <= c.Hours) nextCityWorks += 24.0;
            cityWorksReset = 0;
            nextResetCheck = 0;
        }
        if (now < nextResetCheck) return;
        nextResetCheck = now + KnockTuning.ResetCheckInterval;
        bool anyCityWorks = false;
        for (int i = 0; i < down.Count;)
        {
            var d = down[i];
            bool due = d.cityWorks ||
                       (now - d.downAt >= WorldLifeTuning.Props.ResetAfterSeconds && !PlayerWithin(d.pos, WorldLifeTuning.Props.ResetClearRadius));
            if (due && !ResetBlocked(d.pos))
            {
                bool cw = d.cityWorks;
                ResetEntry(d, cw);   // removes d from the list
                if (cw) { cityWorksReset++; CityWorksResets++; }
                continue;
            }
            if (d.cityWorks) anyCityWorks = true;
            i++;
        }
        if (!anyCityWorks && cityWorksReset > 0)
        {
            GameHUD.Toast($"City Works was here: {cityWorksReset} thing{(cityWorksReset == 1 ? "" : "s")} put back.", ToastStyle.Info, 2.5f);
            cityWorksReset = 0;
        }
    }

    /// <summary>A truck parked on the spot or a player standing in it: wait, so the rest collider never shoves anybody.</summary>
    static bool ResetBlocked(Vector3 p)
    {
        var trucks = TruckControllerV2.All;
        float t2 = KnockTuning.ResetTruckClearance * KnockTuning.ResetTruckClearance;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null) continue;
            Vector3 d = t.transform.position - p; d.y = 0f;
            if (d.sqrMagnitude < t2) return true;
        }
        return PlayerWithin(p, KnockTuning.ResetPlayerClearance);
    }

    /// <summary>Bring a prop back on this machine (the proxy poofs, the original returns, a lamp flickers on).</summary>
    void ResetEntry(Down d, bool byCityWorks)
    {
        var p = d.proxy;
        SendTextsHome(d, hide: false);
        if (p != null)
        {
            p.owner = null;
            d.proxy = null;
            if (debris.Contains(p)) debris.Remove(p);
            StartShrink(p);
        }
        for (int i = 0; i < d.hidden.Count; i++)
        {
            var r = d.hidden[i];
            if (r == null) continue;
            r.enabled = true;
            if (d.lamp) StreetLamps.SetLampDown(r, false);
        }
        for (int i = 0; i < d.colliders.Count; i++) if (d.colliders[i] != null) d.colliders[i].enabled = true;
        if (d.k != null) d.k.IsDown = false;
        try { fx.Reset(d, byCityWorks); } catch (Exception e) { Debug.LogException(e); }
        ushort id = d.id;
        down.Remove(d);
        if (downById.TryGetValue(id, out var cur) && cur == d) downById.Remove(id);
        d.Clear();
        freeDowns.Push(d);
        Resets++;
        WorldLifeEvents.RaisePropReset(id, byCityWorks);
    }

    void SendTextsHome(Down d, bool hide)
    {
        for (int i = 0; i < d.texts.Count; i++)
        {
            var h = d.texts[i];
            if (h.t == null) continue;
            if (h.parent != null) h.t.SetParent(h.parent, false);
            h.t.localPosition = h.lp;
            h.t.localRotation = h.lr;
            h.t.localScale = h.ls;
            if (hide && h.t.TryGetComponent<Renderer>(out var r) && r.enabled) { r.enabled = false; d.hidden.Add(r); }
        }
        d.texts.Clear();
    }

    void RestoreQuiet(Down d)
    {
        SendTextsHome(d, hide: false);
        for (int i = 0; i < d.hidden.Count; i++)
        {
            var r = d.hidden[i];
            if (r == null) continue;
            r.enabled = true;
            if (d.lamp) StreetLamps.SetLampDown(r, false);
        }
        for (int i = 0; i < d.colliders.Count; i++) if (d.colliders[i] != null) d.colliders[i].enabled = true;
        if (d.k != null) d.k.IsDown = false;
        d.Clear();
    }

    // ------------------------------------------------------------------ F seams + queries

    /// <summary>Is this prop down on this machine?</summary>
    public static bool IsDown(ushort knockId) => Instance != null && Instance.downById.ContainsKey(knockId);

    /// <summary>The down bitset (bit = knock id) for F's late-join snapshot. bits.Length ≥ (maxId + 8) / 8.</summary>
    public void CopyDownBits(byte[] bits)
    {
        if (bits == null) return;
        Array.Clear(bits, 0, bits.Length);
        for (int i = 0; i < down.Count; i++)
        {
            int id = down[i].id;
            if ((id >> 3) < bits.Length) bits[id >> 3] |= (byte)(1 << (id & 7));
        }
    }

    /// <summary>F (guest): the host's KnockEvent for a prop this machine didn't knock itself. Debris flies from the
    /// event's impulse and seed; no claim, no truck speed loss.</summary>
    public void PresentRemoteKnock(in KnockFact f)
    {
        var k = Knockable.ById(f.knockId);
        if (k == null || k.IsDown) return;
        ulong h = LifeHash.U64(ctx != null ? ctx.TownSeed : 0UL, LifeKind.Prop, f.knockId, 0, f.seed);
        Present(k, f.impulse, h, WorldLifeRunner.FixedTickCount, null, f.impulse.magnitude);
    }

    /// <summary>F (guest): the host's KnockReset.</summary>
    public void PresentRemoteReset(ushort knockId, bool byCityWorks)
    {
        if (downById.TryGetValue(knockId, out var d)) ResetEntry(d, byCityWorks);
    }

    /// <summary>Sandbox / tests: knock a prop as if a truck hit it with this velocity (no truck: no speed loss).</summary>
    public bool DebugKnock(Knockable k, Vector3 truckVelocity, TruckControllerV2 truck = null)
    {
        if (ctx == null || k == null || k.IsDown) return false;
        int tick = WorldLifeRunner.FixedTickCount;
        ulong h = LifeHash.U64(ctx.TownSeed, LifeKind.Prop, k.id, tick);
        Vector3 centre = truck != null ? truck.transform.position : k.transform.position - truckVelocity.normalized * 3f;
        Quaternion rot = truck != null ? truck.transform.rotation : (truckVelocity.sqrMagnitude > 0.01f ? Quaternion.LookRotation(truckVelocity) : Quaternion.identity);
        var d = Present(k, LaunchVelocity(k.transform.position, truckVelocity, centre, rot, h), h, tick, truck, truckVelocity.magnitude);
        return d != null;
    }

    /// <summary>Sandbox / tests: reset one prop now (as the host would).</summary>
    public bool DebugReset(ushort knockId, bool byCityWorks = false)
    {
        if (!downById.TryGetValue(knockId, out var d)) return false;
        ResetEntry(d, byCityWorks);
        return true;
    }

    public string Describe()
    {
        int frozen = 0;
        for (int i = 0; i < down.Count; i++) if (down[i].proxy != null && down[i].proxy.frozen) frozen++;
        return $"knockables {Knockable.Count}, down {down.Count}, awake {awake.Count} (max seen {MaxAwakeSeen}), frozen {frozen}, shrinking {shrinking.Count}, " +
               $"debris {debris.Count}, stuck cones {stuck.Count}, geysers {Geysers?.Active ?? 0}, proxies created {ProxiesCreated}; knocks {Knocks}, resets {Resets} " +
               $"(City Works {CityWorksResets}), rekicks {Rekicks}, cap freezes {CapFreezes}, cap poofs {CapPoofs}; next City Works at {nextCityWorks:F2} h";
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Package F5 "Dogs" (Docs/Multiplayer.md §14.5; WorldLife's 30 cast dogs, Docs/WorldLife.md §8.2). When a dog isn't out on
/// its walk it's a <b>yard dog</b>: it lounges in its household's front yard, and when a player carrying a bag (or riding a
/// truck's step) comes within <see cref="ChaseRadius"/> it barks and gives chase, never further than its yard chain
/// (<see cref="Chain"/> from the yard, reaching the curb and a bit of street). It can snatch a dangling bag and run off with it
/// (<see cref="SnatchRange"/>); the bag drops when the dog is bonked by a thrown thing, caught (a player touches it), someone
/// grabs the bag back out of its mouth, or after a few proud seconds. Riders on a truck's step (F1) get chased and barked
/// at, but they stay on (only a crash throws a rider off since 12 Oct 2026).
/// <para><b>Authority:</b> the host (and solo) simulates, cheaply: only dogs with a player within <see cref="WakeRadius"/>
/// think; the rest sit. Guests draw the host's stream (<see cref="YardDogsNet"/>). A snatch in co-op goes through
/// <see cref="HandsNet.HostYank"/>: the bag stays one networked object (conserved), flown by the host while in the mouth.</para>
/// Dogs are never hit and never hurt anyone (§8.2). Drawn with WorldLife's <see cref="DogRig"/> (≤ <see cref="MaxRigs"/>).
/// </summary>
public sealed class YardDogs : MonoBehaviour
{
    // ------------------------------------------------------------------ tuning (rule 12)
    public const float WakeRadius = 45f, ChaseRadius = 14f, GiveUpRadius = 22f, Chain = 13f, ChainMin = 8f;
    public const float RunSpeed = 6.6f, CarrySpeed = 7.2f, TrotSpeed = 1.6f, SnatchRange = 1.25f, CatchRange = 1.0f;
    public const float CarryMax = 5.5f, CatchGrace = 0.7f, DogCooldown = 14f, VictimCooldown = 18f, RiderCooldown = 15f;
    public const float BonkRange = 1.1f, DizzySeconds = 1.6f;
    /// <summary>Walled in for this long (s): a dog with a bag lets go of it; a chasing dog gives up.</summary>
    public const float BlockedDrop = 1.2f, BlockedGiveUp = 2.5f;
    public const int MaxRigs = 6;
    const float RigRadius = 55f;

    public enum Mode : byte { Idle = 0, Alert = 1, Chase = 2, Carry = 3, Dizzy = 4, Return = 5, Proud = 6 }

    /// <summary>Raised wherever the event is decided (host/solo): (dog index, what, client, item).</summary>
    public static event Action<int, string, ulong, Carryable> DogEvent;

    public const ulong LocalTarget = ulong.MaxValue - 7;
    public static YardDogs Instance { get; private set; }

    public sealed class Dog
    {
        public int idx;
        public LifeId id;
        public DogStyle style;
        public Vector3 anchor, curb, pos;
        public float chain, yaw, t, barkT, speed, phase, wagT, groundT, wanderT, cooldownUntil, carryStart;
        public Mode mode;
        public ulong target = ulong.MaxValue;
        public Carryable bag;
        /// <summary>Curb-record id of the bag in the mouth (kept past the object's death, to unpublish it).</summary>
        public string bagId;
        /// <summary>How long (s) the dog has been walled in while running; the last spot a sweep proved open.</summary>
        public float blockedT;
        public Vector3 safePos;
        public Vector3 runTo, lastPos;
        public bool awake, walking;
        public DogRig rig;
        // guests
        public Vector3 netPos;
        public float netYaw, netAt = -10f;
        public Mode netMode;
    }

    struct Target
    {
        public ulong client;
        public Vector3 pos;
        public bool carrying, riding;
    }

    readonly List<Dog> dogs = new List<Dog>(32);
    readonly List<Target> targets = new List<Target>(4);
    readonly Stack<DogRig> freeRigs = new Stack<DogRig>(MaxRigs);
    readonly List<DogRig> allRigs = new List<DogRig>(MaxRigs);
    readonly Dictionary<ulong, float> victimAt = new Dictionary<ulong, float>();
    readonly Dictionary<ulong, float> riderAt = new Dictionary<ulong, float>();
    WorldLifeContext builtFor;
    Transform rigRoot;
    float nextRigPass, nextSend;
    uint rng = 0xD0661E5u;

    public IReadOnlyList<Dog> Dogs => dogs;
    public static int Snatches { get; private set; }
    public static int Drops { get; private set; }

    /// <summary>Is <paramref name="c"/> in a dog's mouth right now (wind and other systems leave it alone)?</summary>
    public static bool IsInMouth(Carryable c)
    {
        var y = Instance;
        if (y == null || c == null) return false;
        for (int i = 0; i < y.dogs.Count; i++) if (ReferenceEquals(y.dogs[i].bag, c)) return true;
        return false;
    }

    /// <summary>Has a dog been walled in long enough that a carrying dog drops its bag / a chasing dog gives up?</summary>
    public static bool BlockedTooLong(bool carrying, float blockedT) => blockedT >= (carrying ? BlockedDrop : BlockedGiveUp);

    // ------------------------------------------------------------------ boot

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        DogEvent = null;
        Snatches = Drops = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null || !Application.isPlaying) return;
        var go = new GameObject("[YardDogs]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<YardDogs>();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Simulate here: solo and the host. A connected guest only draws the host's stream.</summary>
    static bool Authority => !GameSession.IsConnectedGuest;

    void Rebuild(WorldLifeContext ctx)
    {
        Clear();
        builtFor = ctx;
        var g = ctx?.Graph;
        if (g == null || g.houses == null || g.nodes == null) return;
        ResidentCast.Ensure(g, ctx.TownSeed);
        int n = ResidentCast.DogCount;
        for (int d = 0; d < n; d++)
        {
            var id = LifeId.Dog(d);
            var owner = ResidentCast.OwnerOf(id);
            if (!ResidentCast.IsValid(owner)) continue;
            int hi = ResidentCast.Get(owner).house;
            if (hi < 0 || hi >= g.houses.Length) continue;
            var h = g.houses[hi];
            if (h.door >= g.nodes.Length || h.pathEnd >= g.nodes.Length || h.slot >= g.nodes.Length) continue;
            Vector3 door = g.nodes[h.door].pos, pathEnd = g.nodes[h.pathEnd].pos, slot = g.nodes[h.slot].pos;
            Vector3 anchor = Vector3.Lerp(door, pathEnd, 0.4f);
            float chain = Mathf.Clamp(Flat(slot - anchor).magnitude + 3.5f, ChainMin, Chain + 6f);
            var dog = new Dog
            {
                idx = d, id = id, style = ResidentCast.DogStyleOf(id), anchor = anchor, curb = slot, chain = chain,
                pos = anchor + new Vector3(0.6f, 0f, 0.3f), yaw = PuppetKit.YawOf(Flat(slot - anchor)), mode = Mode.Idle,
            };
            dog.lastPos = dog.netPos = dog.safePos = dog.pos;
            dog.netYaw = dog.yaw;
            dogs.Add(dog);
        }
        if (rigRoot == null)
        {
            rigRoot = new GameObject("YardDogRigs").transform;
            rigRoot.SetParent(transform, false);
        }
        Debug.Log($"[YardDogs] {dogs.Count} yard dogs ready (cast has {n}).");
    }

    void Clear()
    {
        foreach (var d in dogs)
        {
            if (d.bag != null) DropBag(d, "reset", false);
            if (d.rig != null) { d.rig.go.SetActive(false); freeRigs.Push(d.rig); d.rig = null; }
        }
        dogs.Clear();
        builtFor = null;
    }

    // ------------------------------------------------------------------ per frame

    readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
    double costAcc;
    int costFrames;
    /// <summary>Average cost of the dogs per frame (ms) over the last ~second (sim + drawing).</summary>
    public static float CostMsPerFrame { get; private set; }

    void Update()
    {
        watch.Restart();
        try { Tick(); }
        finally
        {
            watch.Stop();
            costAcc += watch.Elapsed.TotalMilliseconds;
            if (++costFrames >= 60) { CostMsPerFrame = (float)(costAcc / costFrames); costAcc = 0; costFrames = 0; }
        }
    }

    void Tick()
    {
        var ctx = WorldLifeRunner.IsAwake ? WorldLifeRunner.Context : null;
        if (ctx != builtFor || (ctx != null && dogs.Count == 0 && ResidentCast.DogCount > 0 && Time.frameCount % 120 == 0))
        {
            if (ctx == null) Clear();
            else Rebuild(ctx);
        }
        if (dogs.Count == 0) return;
        float dt = Time.deltaTime;
        MarkWalking();
        if (Authority)
        {
            GatherTargets();
            for (int i = 0; i < dogs.Count; i++) Think(dogs[i], dt);
            if (YardDogsNet.Instance != null && Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + YardDogsNet.SendInterval;
                YardDogsNet.Instance.HostSend(dogs);
            }
        }
        else FollowStream(dt);
        if (!Application.isBatchMode) Present(dt);
    }

    void LateUpdate()
    {
        // The bag rides in the mouth after everything else moved this frame.
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (d.bag != null) PoseBag(d);
        }
    }

    void MarkWalking()
    {
        var rd = ResidentDirector.Instance;
        for (int i = 0; i < dogs.Count; i++) dogs[i].walking = false;
        if (rd == null) return;
        var walking = rd.Dogs;
        for (int i = 0; i < walking.Count; i++)
        {
            var w = walking[i];
            if (w == null) continue;
            int k = w.id.index;
            for (int j = 0; j < dogs.Count; j++) if (dogs[j].idx == k) { dogs[j].walking = true; break; }
        }
    }

    void GatherTargets()
    {
        targets.Clear();
        var bodies = PlayerBody.All;
        if (bodies.Count > 0)
        {
            var hn = HandsNet.Instance;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || b.Player == null || b.Player.IsDriving) continue;
                Vector3 p = b.transform.position;
                if (DepotRowLayout.InteriorAt(p) >= 0) continue;
                var rr = RideRider.For(b.ClientId);
                bool riding = b.State == BodyState.Riding || (rr != null && rr.TryGetRide(out _, out _));
                bool carrying = hn != null ? hn.IsHoldingBag(b.ClientId) : b.Carrier != null && b.Carrier.Held is TrashBag heldBag && !heldBag.HasBurst;
                targets.Add(new Target { client = b.ClientId, pos = p, carrying = carrying, riding = riding });
            }
            return;
        }
        var pc = PlayerController.Local; // coop:allow(local) solo: the one local player is the only one a dog can chase
        if (pc == null || pc.IsDriving) return;
        var carrier = TrashCarrier.Local;
        var lr = RideRider.LocalRider;
        bool ride = lr != null && lr.TryGetRide(out _, out _);
        targets.Add(new Target
        {
            client = LocalTarget, pos = pc.transform.position,
            carrying = carrier != null && carrier.Held is TrashBag soloBag && !soloBag.HasBurst, riding = ride,
        });
    }

    // ------------------------------------------------------------------ the brain (host / solo)

    void Think(Dog d, float dt)
    {
        if (d.walking)
        {
            if (d.bag != null) DropBag(d, "walk", false);
            d.awake = false;
            d.mode = Mode.Idle;
            d.pos = d.anchor;
            return;
        }
        float now = Time.time;
        bool wasAwake = d.awake;
        d.awake = false;
        for (int i = 0; i < targets.Count; i++)
            if (Flat(targets[i].pos - d.anchor).sqrMagnitude < WakeRadius * WakeRadius) { d.awake = true; break; }
        if (!d.awake && d.mode != Mode.Carry)
        {
            if (wasAwake) { d.mode = Mode.Idle; d.target = ulong.MaxValue; }
            d.speed = 0f;
            return;
        }
        d.t += dt;
        if (d.barkT > 0f) d.barkT -= dt;
        Ground(d, dt);

        switch (d.mode)
        {
            case Mode.Idle:
            case Mode.Return:
            {
                // Lounge near the yard; wander a little; spot someone worth chasing.
                Vector3 home = d.mode == Mode.Return ? d.anchor : d.runTo == Vector3.zero ? d.anchor : d.runTo;
                if (d.mode == Mode.Idle && now >= d.wanderT)
                {
                    d.wanderT = now + 3f + Rand() * 5f;
                    d.runTo = d.anchor + new Vector3(Rand() * 6f - 3f, 0f, Rand() * 6f - 3f);
                }
                MoveTo(d, home, d.mode == Mode.Return ? 3.5f : TrotSpeed, dt, 0.3f);
                if (d.mode == Mode.Return && Flat(d.pos - d.anchor).sqrMagnitude < 1f) d.mode = Mode.Idle;
                if (now >= d.cooldownUntil && PickTarget(d, out var tg))
                {
                    d.target = tg.client;
                    d.mode = Mode.Alert;
                    d.t = 0f;
                    Bark(d, true);
                }
                break;
            }
            case Mode.Alert:
                FaceTarget(d, dt);
                if (d.t > 0.45f) { d.mode = Mode.Chase; d.t = 0f; }
                break;
            case Mode.Chase:
                Chase(d, dt, now);
                break;
            case Mode.Carry:
                Carry(d, dt, now);
                break;
            case Mode.Dizzy:
                d.speed = 0f;
                d.yaw += 540f * dt;
                if (d.t > DizzySeconds) { d.mode = Mode.Return; d.t = 0f; }
                break;
            case Mode.Proud:
                d.speed = 0f;
                d.wagT = 1f;
                if (d.t > 1.5f) { d.mode = Mode.Return; d.t = 0f; }
                break;
        }
    }

    bool PickTarget(Dog d, out Target best)
    {
        // Someone carrying a bag (or riding a truck) within ChaseRadius of the yard or of the dog; nearest to the dog wins.
        best = default;
        bool found = false;
        float bestD = float.MaxValue, now = Time.time, r2 = ChaseRadius * ChaseRadius;
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (!t.carrying && !t.riding) continue;
            if (t.carrying && !t.riding && victimAt.TryGetValue(t.client, out float va) && now - va < VictimCooldown) continue;
            if (Flat(t.pos - d.anchor).sqrMagnitude > r2 && Flat(t.pos - d.pos).sqrMagnitude > r2) continue;
            float dd = Flat(t.pos - d.pos).sqrMagnitude;
            if (dd < bestD) { bestD = dd; best = t; found = true; }
        }
        return found;
    }

    bool FindTarget(ulong client, out Target t)
    {
        for (int i = 0; i < targets.Count; i++) if (targets[i].client == client) { t = targets[i]; return true; }
        t = default;
        return false;
    }

    void Chase(Dog d, float dt, float now)
    {
        if (!FindTarget(d.target, out var tg) || (!tg.carrying && !tg.riding) ||
            Flat(tg.pos - d.anchor).sqrMagnitude > GiveUpRadius * GiveUpRadius || d.t > 14f)
        {
            d.mode = Mode.Return;
            d.t = 0f;
            d.target = ulong.MaxValue;
            return;
        }
        MoveTo(d, tg.pos, RunSpeed, dt, 0.6f, true);
        if (BlockedTooLong(false, d.blockedT))
        {
            // A fence or hedge between us: stop pawing at it.
            d.mode = Mode.Return;
            d.t = 0f;
            d.target = ulong.MaxValue;
            d.cooldownUntil = now + 4f;
            return;
        }
        if (d.barkT <= 0f) Bark(d, false);
        float dist = Flat(tg.pos - d.pos).magnitude;
        if (tg.carrying && dist < SnatchRange) TrySnatch(d, tg, now);
        else if (tg.riding && dist < 1.8f && (!riderAt.TryGetValue(tg.client, out float ra) || now - ra > RiderCooldown))
        {
            riderAt[tg.client] = now;
            // F1: a snap at the rider's ankles, all bark: riders stay on unless their truck crashes.
            Say(d, "GRRR-WOOF!", ResidentLines.Yell);
            Emit(d, "rider-nip", tg.client, null);
            d.mode = Mode.Return;
            d.cooldownUntil = now + 4f;
        }
    }

    void TrySnatch(Dog d, Target tg, float now)
    {
        Carryable item = null;
        var hn = HandsNet.Instance;
        if (tg.client != LocalTarget && hn != null && hn.Active) item = hn.HostYank(tg.client, "dog");
        if (item == null)
        {
            // Solo (or a host alone before co-op activates): the local carrier lets go.
            bool localPlayer = tg.client == LocalTarget || (hn != null && tg.client == hn.LocalId && !hn.Active);
            var carrier = TrashCarrier.Local;
            if (localPlayer && carrier != null && carrier.Held is TrashBag bag && !bag.HasBurst && bag.NetId == 0)
                item = carrier.ReleaseToScript();
        }
        victimAt[tg.client] = now;
        if (item == null) { d.mode = Mode.Return; d.cooldownUntil = now + 3f; return; }
        d.bag = item;
        d.bagId = item is TrashBag taken && taken.Contents != null ? taken.Contents.bagId : null;
        d.blockedT = 0f;
        d.safePos = d.pos;
        item.NetSetState(CarryState.Thrown);
        item.NetSetKinematic(true, true); // colliders on: anyone can grab it back out of the mouth
        d.mode = Mode.Carry;
        d.t = 0f;
        d.carryStart = now;
        Vector3 away = Flat(d.pos - tg.pos);
        if (away.sqrMagnitude < 0.01f) away = Flat(d.anchor - tg.pos);
        if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
        d.runTo = ClampToChain(d, d.pos + away.normalized * 9f);
        Snatches++;
        KnockSfx.Play(WorldLifeIds.Sfx.Bark, "squeak", d.pos + Vector3.up * 0.4f, 1f, 1.15f * d.style.barkPitch);
        Say(d, "YOINK!", ResidentLines.Yell);
        if (tg.client == LocalTarget || (hn != null && tg.client == hn.LocalId))
            GameHUD.Toast($"{DogName(d)} stole your bag! Bonk it or catch it!", ToastStyle.Bad, 2.5f);
        Emit(d, "snatch", tg.client, item);
    }

    void Carry(Dog d, float dt, float now)
    {
        var bag = d.bag;
        var hn = HandsNet.Instance;
        bool gone = bag == null || bag.State == CarryState.Consumed || bag.Holder != null ||
                    (bag.NetId != 0 && hn != null && hn.Active && !hn.HostFlies(bag));
        if (gone)
        {
            // Someone grabbed it back out of the mouth (or it went somewhere): let go, sulk home.
            if (bag != null && bag.Holder != null) Say(d, "aww", ResidentLines.Chat);
            // The object is gone but its record may live: un-hide it at the curb (co-op), or the glow outlasts the trash.
            if (bag == null || bag.State == CarryState.Consumed) WorldState.Instance?.HostUnpublish(d.bagId);
            Emit(d, "lost", ulong.MaxValue, bag);
            d.bag = null;
            d.bagId = null;
            Drops++;
            d.mode = Mode.Return;
            d.cooldownUntil = now + DogCooldown;
            return;
        }
        // Run to the far side of the yard, then dance about with it.
        if (Flat(d.runTo - d.pos).sqrMagnitude < 1.2f)
            d.runTo = ClampToChain(d, d.anchor + new Vector3(Rand() * 2f - 1f, 0f, Rand() * 2f - 1f).normalized * (d.chain * 0.7f));
        MoveTo(d, d.runTo, CarrySpeed, dt, 0.2f, true);
        d.wagT = 1f;
        // Walled in (a fence, hedge or house between the dog and its run): try another corner of the yard, then give the bag up
        // right here rather than jitter against the wall.
        if (d.blockedT >= 0.4f && d.blockedT - dt < 0.4f)
            d.runTo = ClampToChain(d, d.anchor + new Vector3(Rand() * 2f - 1f, 0f, Rand() * 2f - 1f).normalized * (d.chain * 0.7f));
        if (BlockedTooLong(true, d.blockedT))
        {
            DropBag(d, "blocked", true);
            d.mode = Mode.Proud;
            d.t = 0f;
            return;
        }

        // Caught: a player gets a hand on the dog. Bonked: something thrown hits it.
        if (now - d.carryStart > CatchGrace)
            for (int i = 0; i < targets.Count; i++)
                if (Flat(targets[i].pos - d.pos).sqrMagnitude < CatchRange * CatchRange)
                {
                    Say(d, "Caught!", ResidentLines.Happy);
                    DropBag(d, "caught", true);
                    Emit(d, "caught", targets[i].client, null);
                    d.mode = Mode.Return;
                    return;
                }
        if (Bonked(d))
        {
            KnockSfx.Play(WorldLifeIds.Sfx.Yelp, "squeak", d.pos + Vector3.up * 0.4f, 0.9f, 0.8f * d.style.barkPitch);
            Say(d, "BONK! YIP!", ResidentLines.Hey);
            DropBag(d, "bonked", true);
            d.mode = Mode.Dizzy;
            d.t = 0f;
            return;
        }
        if (now - d.carryStart > CarryMax)
        {
            DropBag(d, "bored", true);
            d.mode = Mode.Proud;
            d.t = 0f;
        }
    }

    bool Bonked(Dog d)
    {
        var all = Carryable.All;
        float now = Time.time;
        Vector3 head = d.pos + Vector3.up * 0.35f;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c == null || ReferenceEquals(c, d.bag) || c.State != CarryState.Thrown || now - c.LastReleaseTime > 2.5f) continue;
            if ((c.WorldCenter - head).sqrMagnitude < BonkRange * BonkRange) return true;
        }
        return false;
    }

    void DropBag(Dog d, string why, bool fx)
    {
        var bag = d.bag;
        d.bag = null;
        d.bagId = null;
        d.cooldownUntil = Time.time + DogCooldown;
        if (bag == null || bag.State == CarryState.Consumed || bag.Holder != null) return;
        Drops++;
        bag.NetSetState(CarryState.Thrown);
        bag.NetSetKinematic(false);
        SeatBag(d, bag);
        if (fx)
        {
            Fx.Dust(bag.WorldCenter, 0.7f);
            Sfx.Play("drop", bag.WorldCenter, 0.7f, 1.1f);
        }
        Emit(d, "drop-" + why, ulong.MaxValue, bag);
    }

    /// <summary>
    /// Set a dropped bag down on open ground: the first of (just ahead of the dog, the dog, its last swept-open spot, the yard
    /// anchor) with a floor under it and nothing solid around the bag. Never where the dog's mouth happens to be, which a
    /// wall, hedge or porch roof can be inside of. Then it rests there, and the rescue logic is told it's a good spot.
    /// </summary>
    void SeatBag(Dog d, Carryable bag)
    {
        Vector3 fwd = Quaternion.Euler(0f, d.yaw, 0f) * Vector3.forward;
        Vector3 floor = default, fallback = default;
        bool found = false, haveFallback = false;
        for (int k = 0; k < 4 && !found; k++)
        {
            Vector3 c = k == 0 ? d.pos + fwd * 0.4f : k == 1 ? d.pos : k == 2 ? d.safePos : d.anchor;
            if (!FloorAt(c.x, c.z, Mathf.Max(c.y, d.pos.y) + 2.5f, d.pos.y + StepUp, out float fy)) continue;
            var f = new Vector3(c.x, fy, c.z);
            if (!haveFallback || k == 3) { fallback = f; haveFallback = true; }
            if (!Physics.CheckSphere(f + Vector3.up * 0.55f, 0.28f, SolidMask, QueryTriggerInteraction.Ignore)) { floor = f; found = true; }
        }
        if (!found) floor = haveFallback ? fallback : d.anchor;
        Vector3 p = floor + Vector3.up * 0.08f;
        var rot = Quaternion.Euler(0f, d.yaw, 0f);
        bag.transform.SetPositionAndRotation(p, rot);
        if (bag.Body != null)
        {
            bag.Body.position = p;
            bag.Body.rotation = rot;
            bag.Body.linearVelocity = Vector3.zero;
            bag.Body.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        bag.NoteSafePosition();
    }

    void PoseBag(Dog d)
    {
        var bag = d.bag;
        if (bag == null || bag.State == CarryState.Consumed || bag.Holder != null) return;
        if (!bag.Body.isKinematic) bag.NetSetKinematic(true, true);
        var rot = Quaternion.Euler(0f, d.yaw, 0f);
        Vector3 mouth = d.pos + rot * new Vector3(0f, 0.32f, 0.55f) + Vector3.down * 0.05f;
        Vector3 off = bag.WorldCenter - bag.transform.position;
        float swing = Mathf.Sin(Time.time * 14f) * 12f;
        bag.transform.SetPositionAndRotation(mouth - off + Vector3.down * 0.15f, rot * Quaternion.Euler(swing, 0f, swing * 0.5f));
        if (bag.Body != null) { bag.Body.position = bag.transform.position; bag.Body.rotation = bag.transform.rotation; }
    }

    // ------------------------------------------------------------------ motion helpers

    /// <summary>What a dog can't run through: Default (hedges, fences, props), Ground (houses, cars) and Walls. Same set the
    /// residents' walls use (ResidentAgent.Acts).</summary>
    static readonly int SolidMask = (1 << 0) | (1 << 6) | (1 << 7);
    const float StepUp = 0.7f;
    static readonly float[] Sidesteps = { 35f, -35f, 70f, -70f, 110f, -110f };
    static readonly RaycastHit[] castHits = new RaycastHit[8];

    /// <summary>Would a dog-sized sweep along <paramref name="dir"/> hit scenery within <paramref name="len"/>? Rigidbodies
    /// (trucks), the player, triggers and walkable slopes don't count; neither does something it already overlaps.</summary>
    static bool Blocked(Vector3 from, Vector3 dir, float len)
    {
        int n = Physics.SphereCastNonAlloc(from + Vector3.up * 0.5f, 0.22f, dir, castHits, len, SolidMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = castHits[i];
            var c = h.collider;
            if (c == null || h.distance <= 0f || c.attachedRigidbody != null || c is CharacterController || h.normal.y >= 0.6f) continue;
            return true;
        }
        return false;
    }

    /// <summary>Keep <paramref name="dir"/> if it's open for a step; else slide along the nearest open heading. False = walled in.</summary>
    static bool FreeHeading(Dog d, ref Vector3 dir, float step)
    {
        float look = step + 0.35f;
        if (!Blocked(d.pos, dir, look)) return true;
        for (int i = 0; i < Sidesteps.Length; i++)
        {
            Vector3 alt = Quaternion.Euler(0f, Sidesteps[i], 0f) * dir;
            if (!Blocked(d.pos, alt, look)) { dir = alt; return true; }
        }
        return false;
    }

    /// <param name="solid">Sweep each step against scenery (a chasing or carrying dog): it slides along fences and hedges, and
    /// <see cref="Dog.blockedT"/> counts how long it's been walled in. Off = the free yard-lounging step.</param>
    void MoveTo(Dog d, Vector3 to, float speed, float dt, float stopAt, bool solid = false)
    {
        to = ClampToChain(d, to);
        Vector3 v = Flat(to - d.pos);
        float dist = v.magnitude;
        if (dist > stopAt)
        {
            float step = Mathf.Min(dist - stopAt, speed * dt);
            Vector3 dir = v / dist;
            if (solid && !FreeHeading(d, ref dir, step)) { step = 0f; d.blockedT += dt; }
            else
            {
                d.blockedT = 0f;
                if (solid) d.safePos = d.pos;
            }
            d.pos += dir * step;
            if (solid) d.pos = ClampToChain(d, d.pos);
            d.yaw = PuppetKit.DampAngle(d.yaw, PuppetKit.YawOf(dir), 10f, dt);
        }
        else d.blockedT = 0f;
        d.speed = dt > 1e-5f ? Flat(d.pos - d.lastPos).magnitude / dt : 0f;
        d.lastPos = d.pos;
    }

    void FaceTarget(Dog d, float dt)
    {
        if (!FindTarget(d.target, out var tg)) return;
        Vector3 v = Flat(tg.pos - d.pos);
        if (v.sqrMagnitude > 0.01f) d.yaw = PuppetKit.DampAngle(d.yaw, PuppetKit.YawOf(v), 12f, dt);
        d.speed = 0f;
    }

    Vector3 ClampToChain(Dog d, Vector3 p)
    {
        Vector3 off = Flat(p - d.anchor);
        if (off.sqrMagnitude > d.chain * d.chain) off = off.normalized * d.chain;
        return new Vector3(d.anchor.x + off.x, d.pos.y, d.anchor.z + off.z);
    }

    static readonly RaycastHit[] hits = new RaycastHit[6];

    void Ground(Dog d, float dt)
    {
        d.groundT -= dt;
        if (d.groundT > 0f) return;
        d.groundT = 0.25f;
        // A porch roof or an awning is a hit too, but the dog can't step up onto it: prefer the floor it can reach.
        float from = Mathf.Max(d.anchor.y, d.pos.y) + 3f;
        if (FloorAt(d.pos.x, d.pos.z, from, d.pos.y + StepUp, out float y) || FloorAt(d.pos.x, d.pos.z, from, float.PositiveInfinity, out y))
            d.pos.y = Mathf.Lerp(d.pos.y, y, 0.7f);
    }

    /// <summary>Highest solid, non-rigidbody surface at or below <paramref name="limitY"/> under (x, z), looking down from <paramref name="fromY"/>.</summary>
    static bool FloorAt(float x, float z, float fromY, float limitY, out float y)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(x, fromY, z), Vector3.down, hits, 8f, ~(1 << Carryable.InteractableLayer), QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null || c.attachedRigidbody != null || c is CharacterController) continue;
            if (hits[i].point.y <= limitY && hits[i].point.y > best) best = hits[i].point.y;
        }
        y = best;
        return !float.IsNegativeInfinity(best);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void Bark(Dog d, bool words)
    {
        d.barkT = 0.7f + Rand() * 0.4f;
        KnockSfx.Play(WorldLifeIds.Sfx.Bark, "squeak", d.pos + Vector3.up * 0.4f, 0.75f, 0.85f * d.style.barkPitch);
        if (words) Say(d, ResidentLines.Woof[(int)(Rand() * ResidentLines.Woof.Length) % ResidentLines.Woof.Length], ResidentLines.Chat);
    }

    void Say(Dog d, string text, Color c)
    {
        if (Application.isBatchMode) return;
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - d.pos).sqrMagnitude < 50f * 50f)
            GameHUD.FloatingText(d.pos + Vector3.up * 0.9f, text, c, 0.8f);
    }

    static string DogName(Dog d) => string.IsNullOrEmpty(d.style.name) ? "A dog" : d.style.name;

    void Emit(Dog d, string what, ulong client, Carryable item)
    {
        NetAudit.Event("dog", new Dictionary<string, object>
        {
            { "op", what }, { "dog", d.idx }, { "client", client == ulong.MaxValue ? -1 : (long)client },
            { "item", item != null ? (long)item.NetId : 0 }, { "key", item is TrashBag tb && tb.Contents != null ? tb.Contents.bagId ?? "" : "" },
        });
        try { DogEvent?.Invoke(d.idx, what, client, item); }
        catch (Exception e) { Debug.LogException(e); }
        if (what == "snatch" || what.StartsWith("drop") || what == "lost") YardDogsNet.Instance?.HostSay(d.idx, what);
    }

    float Rand()
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return (rng & 0xFFFFFF) / 16777216f;
    }

    // ------------------------------------------------------------------ guests: the host's stream

    /// <summary>Guest: a dog's pose from the host.</summary>
    public void OnRemote(int idx, Vector3 pos, float yaw, Mode mode)
    {
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (d.idx != idx) continue;
            if (d.netMode != mode && (mode == Mode.Alert || mode == Mode.Carry)) Bark(d, mode == Mode.Alert);
            d.netPos = pos;
            d.netYaw = yaw;
            d.netMode = mode;
            d.netAt = Time.time;
            d.awake = true;
            return;
        }
    }

    /// <summary>Guest: a dog moment the host announced (sound + words).</summary>
    public void OnRemoteSay(int idx, string what)
    {
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (d.idx != idx) continue;
            if (what == "snatch") { Say(d, "YOINK!", ResidentLines.Yell); KnockSfx.Play(WorldLifeIds.Sfx.Bark, "squeak", d.pos + Vector3.up * 0.4f, 1f, 1.15f * d.style.barkPitch); }
            else if (what == "drop-bonked") { Say(d, "BONK! YIP!", ResidentLines.Hey); KnockSfx.Play(WorldLifeIds.Sfx.Yelp, "squeak", d.pos, 0.9f, 0.8f * d.style.barkPitch); }
            else if (what == "drop-caught") Say(d, "Caught!", ResidentLines.Happy);
            return;
        }
    }

    void FollowStream(float dt)
    {
        float now = Time.time;
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (now - d.netAt > 1.5f)
            {
                // Not streamed: it's sitting in its yard.
                d.awake = false;
                d.netMode = Mode.Idle;
                d.netPos = d.anchor;
            }
            float k = 1f - Mathf.Exp(-12f * dt);
            Vector3 before = d.pos;
            d.pos = Vector3.Lerp(d.pos, d.netPos, k);
            if ((d.pos - d.netPos).sqrMagnitude > 25f) d.pos = d.netPos;
            d.yaw = Mathf.LerpAngle(d.yaw, d.netYaw, k);
            d.mode = d.netMode;
            d.speed = dt > 1e-5f ? Flat(d.pos - before).magnitude / dt : 0f;
            if (d.mode == Mode.Carry || d.mode == Mode.Proud) d.wagT = 1f;
        }
    }

    // ------------------------------------------------------------------ drawing

    void Present(float dt)
    {
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 cp = cam.transform.position;
        if (Time.unscaledTime >= nextRigPass)
        {
            nextRigPass = Time.unscaledTime + 0.4f;
            AssignRigs(cp);
        }
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (d.rig != null) Animate(d, dt);
        }
    }

    readonly List<Dog> byDist = new List<Dog>(32);

    void AssignRigs(Vector3 cp)
    {
        byDist.Clear();
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            bool want = !d.walking && (d.pos - cp).sqrMagnitude < RigRadius * RigRadius;
            if (want) byDist.Add(d);
            else if (d.rig != null) { d.rig.go.SetActive(false); freeRigs.Push(d.rig); d.rig = null; }
        }
        byDist.Sort((a, b) => (a.pos - cp).sqrMagnitude.CompareTo((b.pos - cp).sqrMagnitude));
        for (int i = MaxRigs; i < byDist.Count; i++)
        {
            var d = byDist[i];
            if (d.rig != null) { d.rig.go.SetActive(false); freeRigs.Push(d.rig); d.rig = null; }
        }
        for (int i = 0; i < byDist.Count && i < MaxRigs; i++)
        {
            var d = byDist[i];
            if (d.rig != null) continue;
            DogRig r = freeRigs.Count > 0 ? freeRigs.Pop() : allRigs.Count < MaxRigs ? BuildRig() : null;
            if (r == null) break;
            r.Apply(d.style);
            r.SetLite(false);
            r.leash.enabled = false;
            r.go.SetActive(true);
            d.rig = r;
        }
    }

    DogRig BuildRig()
    {
        var r = DogRig.Build(rigRoot, "YardDog" + allRigs.Count);
        allRigs.Add(r);
        return r;
    }

    static void Animate(Dog d, float dt)
    {
        var rig = d.rig;
        rig.root.SetPositionAndRotation(d.pos, Quaternion.Euler(0f, d.yaw, 0f));
        float spd = d.speed;
        d.phase += spd * dt / Mathf.Max(0.12f, rig.legLen * 2.2f) * Mathf.PI;
        float k = Mathf.Clamp01(spd / 1.5f);
        float s = Mathf.Sin(d.phase);
        float amp = 42f * k;
        rig.legs[0].localRotation = Quaternion.Euler(s * amp, 0f, 0f);
        rig.legs[3].localRotation = Quaternion.Euler(s * amp, 0f, 0f);
        rig.legs[1].localRotation = Quaternion.Euler(-s * amp, 0f, 0f);
        rig.legs[2].localRotation = Quaternion.Euler(-s * amp, 0f, 0f);
        d.wagT = Mathf.Max(0f, d.wagT - dt);
        bool excited = d.mode == Mode.Chase || d.mode == Mode.Alert || d.mode == Mode.Carry || d.wagT > 0f;
        float wag = Mathf.Sin(Time.time * (excited ? 30f : 9f)) * (excited ? 45f : 18f);
        rig.tailPivot.localRotation = Quaternion.Euler(0f, wag, 0f);
        float headPitch = d.mode == Mode.Alert ? -25f : d.mode == Mode.Chase ? -10f + Mathf.Sin(Time.time * 20f) * 6f
                        : d.mode == Mode.Carry ? 8f : spd < 0.2f ? 18f + Mathf.Sin(Time.time * 2f) * 6f : -4f;
        rig.headPivot.localRotation = Quaternion.Euler(headPitch, 0f, 0f);
        float bob = Mathf.Abs(Mathf.Cos(d.phase)) * 0.04f * k;
        bool lying = d.mode == Mode.Idle && spd < 0.1f;
        rig.visual.localPosition = new Vector3(0f, lying ? -rig.legLen * 0.6f : bob, 0f);
        rig.bodyPivot.localRotation = d.mode == Mode.Dizzy ? Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 18f) * 18f)
                                    : d.mode == Mode.Alert ? Quaternion.Euler(-12f, 0f, 0f) : Quaternion.identity;
    }
}

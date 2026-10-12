using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One truck's riding state on this machine (package F1, Docs/Multiplayer.md §14.1, §4.4 "How the truck works on the
/// network", red-team T4). Added at runtime to every truck with a hopper (with <see cref="HitchStep"/>; no prefab edit).
/// <list type="bullet">
/// <item><b>Crash sensor</b> (only where the truck's physics runs: the driver's machine, or the host for an empty truck):
/// a heavy hit is the truck's horizontal velocity changing by ≥ 5 m/s within ~0.06 s (a tree, a wall, another truck,
/// being rear-ended). Braking, cornering, drifting, curbs and potholes never get near it, so they never throw anyone off.
/// Crashes are stamped with NGO server time and, while anyone rides, sent to every machine (<see cref="HandsNet"/>) along
/// with 10 pose samples a second (~21 B each).</item>
/// <item><b>Display timeline</b> (every machine): a 5 s history of where this machine SHOWS the truck, and the lag
/// between the driver's clock and that display, measured by matching each pose sample against the displayed path. A
/// rider applies a crash when their displayed truck reaches that moment, so you're never thrown before the hit you see;
/// the owner sends that tick and its exact world start for the flight.</item>
/// <item><b>Crew Comfort</b> (the driver's machine): a smooth leg ending in a stop with riders aboard earns a small tip
/// for the truck's business (sideways g, braking, drift, bumps and knocks, measured right here, spoil it).</item>
/// <item><b>Kit</b>: side ladder rungs and grab rails (cosmetic, no colliders); a padded step and a safety bar when the
/// truck's business owns them.</item>
/// </list>
/// </summary>
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public class RideTruck : MonoBehaviour
{
    static readonly List<RideTruck> all = new List<RideTruck>();
    public static IReadOnlyList<RideTruck> All => all;

    public static RideTruck For(TruckControllerV2 t) => t != null ? t.GetComponent<RideTruck>() : null;

    public static RideTruck Find(ulong netId)
    {
        for (int i = 0; i < all.Count; i++)
        {
            var r = all[i];
            if (r != null && r.NetObject != null && r.NetObject.IsSpawned && r.NetObject.NetworkObjectId == netId) return r;
        }
        return null;
    }

    public static RideTruck Ensure(TruckControllerV2 t)
    {
        if (t == null) return null;
        var rt = t.GetComponent<RideTruck>();
        return rt != null ? rt : t.gameObject.AddComponent<RideTruck>();
    }

    public TruckControllerV2 Truck { get; private set; }
    public NetworkObject NetObject { get; private set; }
    public ulong NetId => NetObject != null && NetObject.IsSpawned ? NetObject.NetworkObjectId : 0;
    Rigidbody body;
    TruckHandlingMods handling;

    /// <summary>This machine runs the truck's physics (the driver, the host for an empty truck, solo).</summary>
    public bool Simulated => Truck == null || !Truck.IsSpawned || Truck.IsOwner;

    static HandsNet Net => HandsNet.Instance != null && HandsNet.Instance.Active ? HandsNet.Instance : null;

    void Awake()
    {
        Truck = GetComponent<TruckControllerV2>();
        NetObject = GetComponent<NetworkObject>();
        body = GetComponent<Rigidbody>();
        handling = GetComponent<TruckHandlingMods>();
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() { StopSoloCruise(); all.Remove(this); }

    void OnDestroy()
    {
        StopSoloCruise();
        if (kit != null) Destroy(kit.gameObject);
    }

    // ==================================================================================== optional solo cruise

    SoloCruiseRoute soloCruise;
    RideRider soloCruiseRider;
    System.Func<Vector2> soloCruiseInput;
    public bool SoloCruising => soloCruise != null;

    /// <summary>A solo rider's explicit [C] request. Never takes over a seated driver, a network session, or another
    /// system's InputOverride. The road graph must have an open lane close to the parked truck and pointing its way.</summary>
    public bool StartSoloCruise(RideRider rider, out string why)
    {
        why = null;
        if (rider == null || !rider.IsLocal || !rider.TryGetRide(out var riding, out _) || riding != this ||
            Truck == null || body == null || body.isKinematic || !Simulated || Truck.HasDriver || Truck.IsLocalPlayerSeated ||
            Net != null || SceneController.IsTransitioning)
        { why = "Cruise needs an empty solo truck."; return false; }
        if (soloCruise != null) { why = "Already cruising."; return false; }
        if (Truck.InputOverride != null) { why = "Another driver controls this truck."; return false; }
        var graph = WorldLifeRunner.Context != null ? WorldLifeRunner.Context.Graph : StreetGraph.Load();
        if (!SoloCruiseRoute.TryBuild(graph, Truck, WorldLifeIds.UnlockedDistrictMask(), out var route))
        { why = "Park on an open road lane to cruise."; return false; }
        soloCruise = route;
        soloCruiseRider = rider;
        soloCruiseInput ??= () => soloCruise != null ? soloCruise.Command : Vector2.zero;
        Truck.InputOverride = soloCruiseInput;
        return true;
    }

    /// <summary>Only the rider who started it can stop it, unless the truck itself is shutting down.</summary>
    public void StopSoloCruise(RideRider rider = null)
    {
        if (rider != null && soloCruiseRider != rider) return;
        if (Truck != null && Truck.InputOverride == soloCruiseInput) Truck.InputOverride = null;
        soloCruise = null;
        soloCruiseRider = null;
    }

    void SoloCruiseTick(float dt)
    {
        if (soloCruise == null) return;
        if (soloCruiseRider == null || !soloCruiseRider.TryGetRide(out var riding, out _) || riding != this ||
            Truck == null || Truck.InputOverride != soloCruiseInput || Truck.HasDriver || Truck.IsLocalPlayerSeated ||
            Net != null || !Simulated || body == null || body.isKinematic ||
            PlayerController.IsUIBlocking || SceneController.IsTransitioning || Vector3.Dot(transform.up, Vector3.up) < 0.55f)
        { StopSoloCruise(); return; }
        soloCruise.Tick(Truck, body, dt, soloCruiseRider.Player);
        if (soloCruise.Done) StopSoloCruise();
    }

    // ==================================================================================== display history

    struct Snap { public double t; public Vector3 p; public Quaternion r; }

    const int HistLen = 320;
    readonly Snap[] hist = new Snap[HistLen];
    int histHead = -1, histCount;

    /// <summary>The truck's velocity as this machine shows it (smoothed).</summary>
    public Vector3 Velocity { get; private set; }
    public float Speed => new Vector2(Velocity.x, Velocity.z).magnitude;
    /// <summary>Sideways (x, + = toward the truck's right) and braking (y) acceleration of the truck as this machine shows it,
    /// smoothed, m/s². Cosmetic only: riders' puppets sway with it (ChuckPuppet); nothing is decided from it.</summary>
    public Vector2 SwayAccel { get; private set; }
    Vector3 swayPrevV;
    bool swayHasPrev;

    void ResetHistory()
    {
        histHead = -1;
        histCount = 0;
        pending.Clear();
        Velocity = Vector3.zero;
        lastSampleT = double.MinValue;
        hasLastSamplePos = false;
        swayHasPrev = false;
        SwayAccel = Vector2.zero;
    }

    void Push(double t, Vector3 p, Quaternion r)
    {
        histHead = (histHead + 1) % HistLen;
        hist[histHead] = new Snap { t = t, p = p, r = r };
        if (histCount < HistLen) histCount++;
    }

    Snap At(int back) => hist[(histHead - back + HistLen * 2) % HistLen];

    /// <summary>Where this machine showed the truck at local clock time <paramref name="tLocal"/>.</summary>
    public bool TryPoseAtLocal(double tLocal, out Vector3 p, out Quaternion r)
    {
        p = transform.position;
        r = transform.rotation;
        if (histCount == 0) return false;
        var newest = At(0);
        if (tLocal >= newest.t) { p = newest.p; r = newest.r; return tLocal - newest.t < 0.25; }
        for (int i = 1; i < histCount; i++)
        {
            var a = At(i);
            if (a.t <= tLocal)
            {
                var b = At(i - 1);
                float f = b.t > a.t ? (float)((tLocal - a.t) / (b.t - a.t)) : 0f;
                p = Vector3.LerpUnclamped(a.p, b.p, f);
                r = Quaternion.SlerpUnclamped(a.r, b.r, f);
                return true;
            }
        }
        var oldest = At(histCount - 1);
        p = oldest.p;
        r = oldest.r;
        return false;
    }

    /// <summary>Where this machine showed (or shows) the truck at driver-clock time <paramref name="tDriver"/>.</summary>
    public bool TryPoseAtDriver(double tDriver, out Vector3 p, out Quaternion r) => TryPoseAtLocal(tDriver + Lag, out p, out r);

    // ==================================================================================== lag (display vs driver clock)

    struct Pending { public double t; public Vector3 pos, dir; public float added; }
    readonly List<Pending> pending = new List<Pending>(16);
    double lag = RideTuning.LagGuess;

    /// <summary>Seconds this machine shows the truck behind the driver's clock (0 where it's simulated).</summary>
    public double Lag => Simulated ? 0.0 : lag;
    /// <summary>The driver-clock moment this machine is showing now.</summary>
    public double DisplayTime => RideClock.Now - Lag;
    public int LagMatches { get; private set; }

    void AddMatch(double t, Vector3 pos, Vector3 dir)
    {
        // The display may already be past it (a late sample): look back through the history first.
        for (int i = 1; i < histCount; i++)
        {
            var a = At(i);
            var b = At(i - 1);
            if (b.t < t - 0.2) break;
            if (Crossing(a, b, pos, dir, out double tc)) { ApplyLag(tc - t); return; }
        }
        if (pending.Count >= 16) pending.RemoveAt(0);
        pending.Add(new Pending { t = t, pos = pos, dir = dir, added = Time.unscaledTime });
    }

    static bool Crossing(in Snap a, in Snap b, Vector3 pos, Vector3 dir, out double tc)
    {
        tc = 0;
        float sa = Vector3.Dot(a.p - pos, dir), sb = Vector3.Dot(b.p - pos, dir);
        if (!(sa < 0f && sb >= 0f)) return false;
        float f = sb - sa > 1e-5f ? -sa / (sb - sa) : 0f;
        Vector3 at = Vector3.Lerp(a.p, b.p, f);
        Vector3 off = at - pos; off.y = 0f;
        if (off.sqrMagnitude > 2.5f * 2.5f) return false;
        tc = a.t + (b.t - a.t) * f;
        return true;
    }

    // The lag is the median of the last 15 matches (~1.5 s of driving): display jitter and the odd late frame don't move it.
    readonly double[] lagRing = new double[15];
    readonly double[] lagSort = new double[15];
    int lagCount, lagNext;

    void ApplyLag(double sample)
    {
        if (sample < -0.05 || sample > 1.5) return;
        lagRing[lagNext] = sample;
        lagNext = (lagNext + 1) % lagRing.Length;
        if (lagCount < lagRing.Length) lagCount++;
        System.Array.Copy(lagRing, lagSort, lagCount);
        System.Array.Sort(lagSort, 0, lagCount);
        lag = lagSort[lagCount / 2];
        LagMatches++;
    }

    void MatchPending()
    {
        if (pending.Count == 0 || histCount < 2) return;
        var a = At(1);
        var b = At(0);
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var q = pending[i];
            if (Crossing(a, b, q.pos, q.dir, out double tc)) { ApplyLag(tc - q.t); pending.RemoveAt(i); continue; }
            if (Time.unscaledTime - q.added > 2.5f) pending.RemoveAt(i);
        }
    }

    // ==================================================================================== pose samples and crashes

    /// <summary>Where the truck was at a driver-clock tick (lag matching only; ~21 B on the wire).</summary>
    public struct Sample { public double t; public float speed; public Vector3 pos; }

    /// <summary>A heavy hit: the driver-clock tick and the truck's Δv (m/s). Each rider's machine compares it with the
    /// truck's Safety Bar and Padded Step (<see cref="RideTuning.CrashFlingDv"/>).</summary>
    public struct Crash
    {
        public double t;
        public float dv;
        // Local receipt order. The wire still sends only the driver-clock tick and the Δv.
        public int serial;

        public static byte PackDv(float dv) => (byte)Mathf.Clamp(Mathf.RoundToInt(dv * 8f), 0, 255);
        public static float UnpackDv(byte packed) => packed / 8f;
    }

    readonly List<Crash> crashes = new List<Crash>(8);
    int lastCrashSerial;
    public int LastCrashSerial => lastCrashSerial;
    double lastSampleT = double.MinValue;
    Vector3 lastSamplePos;
    bool hasLastSamplePos;

    public void ReceiveSample(in Sample s)
    {
        // Unreliable samples can arrive late or twice: only the newest extends the matched path.
        if (s.t <= lastSampleT) return;
        if (!Simulated && hasLastSamplePos)
        {
            Vector3 d = s.pos - lastSamplePos; d.y = 0f;
            if (s.speed > 1.2f && d.sqrMagnitude > 0.01f && d.sqrMagnitude < 100f) AddMatch(s.t, s.pos, d.normalized);
        }
        lastSampleT = s.t;
        lastSamplePos = s.pos;
        hasLastSamplePos = true;
    }

    public void ReceiveCrash(in Crash c)
    {
        var received = c;
        received.serial = ++lastCrashSerial;
        crashes.Add(received);
        if (crashes.Count > 8) crashes.RemoveAt(0);
    }

    /// <summary>New crashes whose driver tick is visible. Receipt serials, rather than arrival order or a time-only
    /// cursor, let a delayed older crash pass a future-stamped one without being skipped or applied twice.</summary>
    public void CrashesSince(int afterSerial, double upTo, HashSet<int> consumed, List<Crash> into)
    {
        for (int i = 0; i < crashes.Count; i++)
        {
            var c = crashes[i];
            if (c.serial <= afterSerial || c.t > upTo || consumed.Contains(c.serial)) continue;
            into.Add(c);
            consumed.Add(c.serial);
        }
        // The truck retains at most 8 crashes. Prune the rider's receipts when that window advances.
        if (consumed.Count > 8)
        {
            int oldest = crashes.Count > 0 ? crashes[0].serial : lastCrashSerial;
            consumed.RemoveWhere(serial => serial < oldest);
        }
    }

    // ==================================================================================== sensor (simulating machine)

    Vector3 prevV, prevPos;
    bool hasPrev;
    // The last few steps' velocities: a hit the solver spreads over two or three steps still reads as one Δv.
    readonly Vector3[] recentV = new Vector3[RideTuning.CrashWindowSteps];
    int recentCount, recentHead;
    float latEma, brakeEma, driftEma, lastCrashDv;
    double nextSampleAt, lastCrashAt = -9, lastKnockAt = -9;

    /// <summary>How rough the ride is right now, for the driver's Crew Comfort tip only (the simulating machine's smoothed
    /// sideways g, braking and drift). Nobody's hold on the truck depends on it.</summary>
    public static float Roughness(float lat, float brake, float drift) =>
        RideTuning.ComfortLatCost * Mathf.Max(0f, Mathf.Abs(lat) - RideTuning.ComfortLatFree) +
        RideTuning.ComfortBrakeCost * Mathf.Max(0f, brake - RideTuning.ComfortBrakeFree) +
        RideTuning.ComfortDriftCost * Mathf.Max(0f, drift - RideTuning.ComfortDriftFree);

    /// <summary>Smoothed sideways accel (+ = toward the right) and braking right now (simulating machine; tests).</summary>
    public float LatNow => latEma;
    public float BrakeNow => brakeEma;

    /// <summary>Peak per-step |Δv| seen by the sensor (tuning / tests).</summary>
    public float MaxStepDvY { get; private set; }
    public float MaxStepDvH { get; private set; }
    /// <summary>Peak horizontal Δv over the crash window (tuning / tests): what ordinary driving reaches next to
    /// <see cref="RideTuning.FlingAllDv"/>.</summary>
    public float MaxWindowDv { get; private set; }

    /// <summary>Start the peak readings over (the test rig does it per lap).</summary>
    public void ResetSensorPeaks() => MaxStepDvY = MaxStepDvH = MaxWindowDv = 0f;

    void FixedUpdate()
    {
        SoloCruiseTick(Time.fixedDeltaTime);
        if (!Simulated || body == null || body.isKinematic) { hasPrev = false; recentCount = 0; sensing = false; return; }
        float dt = Time.fixedDeltaTime;
        Vector3 v = body.linearVelocity;
        Vector3 pos = body.position;
        if (!hasPrev || (pos - prevPos).sqrMagnitude > 25f)
        {
            // A teleport (garage door, recovery) or the first simulated step: no Δv to read yet.
            prevV = v; prevPos = pos; hasPrev = true;
            recentCount = 0;
            Remember(v);
            latEma = brakeEma = driftEma = 0f;
            return;
        }
        Quaternion inv = Quaternion.Inverse(body.rotation);
        Vector3 dvL = inv * (v - prevV);
        Vector3 vL = inv * v;
        float dvH = new Vector2(dvL.x, dvL.z).magnitude;
        MaxStepDvY = Mathf.Max(MaxStepDvY, Mathf.Abs(dvL.y));
        MaxStepDvH = Mathf.Max(MaxStepDvH, dvH);
        double now = RideClock.Now;

        // A crash: the horizontal velocity jumped within the last few steps, from any speed (a parked truck that gets
        // rear-ended counts as much as one that drives into a tree).
        float crashDv = 0f;
        for (int i = 0; i < recentCount; i++)
        {
            Vector3 d = inv * (v - recentV[i]);
            crashDv = Mathf.Max(crashDv, new Vector2(d.x, d.z).magnitude);
        }
        MaxWindowDv = Mathf.Max(MaxWindowDv, crashDv);
        if (crashDv >= RideTuning.FlingAllDv) EmitCrash(now, crashDv);

        // The rest only feeds the driver's Crew Comfort tip.
        float k = 1f - Mathf.Exp(-dt / 0.15f);
        float preSpeed = new Vector2(prevV.x, prevV.z).magnitude;
        if (dvH > RideTuning.ComfortKnockDv) Knock(now);
        else
        {
            Vector3 aL = dvL / dt;
            float fwd = vL.z;
            float lat = aL.x; // + = toward the truck's right (a right turn)
            float brake = Mathf.Abs(fwd) > 0.3f ? Mathf.Max(0f, -aL.z * Mathf.Sign(fwd)) : 0f;
            latEma += (lat - latEma) * k;
            brakeEma += (brake - brakeEma) * k;
        }
        float slip = Mathf.Abs(vL.x) + (handling != null && handling.Drifting ? 2f : 0f);
        driftEma += (slip - driftEma) * k;
        if (Mathf.Abs(dvL.y) > RideTuning.ComfortBumpDv && preSpeed > 1f) Knock(now);
        prevV = v;
        prevPos = pos;
        Remember(v);
        simSpeed = new Vector2(v.x, v.z).magnitude;
        sensing = true;
        ComfortTick(dt, simSpeed);
    }

    void Remember(Vector3 v)
    {
        recentV[recentHead] = v;
        recentHead = (recentHead + 1) % recentV.Length;
        if (recentCount < recentV.Length) recentCount++;
    }

    float simSpeed;
    bool sensing;

    /// <summary>Simulating machine, after physics (LateUpdate): a sample stamped with the pose this frame shows, the same
    /// convention as the display history, so every machine maps a driver-clock tick to the same truck pose.</summary>
    void EmitSample(double now)
    {
        if (!sensing || !Simulated || now < nextSampleAt) return;
        nextSampleAt = now + 1.0 / RideTuning.SampleHz;
        var s = new Sample { t = now, speed = simSpeed, pos = transform.position };
        ReceiveSample(s);
        var net = Net;
        if (net != null && NetId != 0 && net.AnyRiderOn(NetId)) net.SendRideSample(this, s);
    }

    void EmitCrash(double now, float dv)
    {
        // One crash per hit, unless the hit is still building: a bigger reading may beat a Safety Bar the first didn't.
        if (now - lastCrashAt < RideTuning.CrashCooldown && dv < lastCrashDv + RideTuning.CrashGrowth) return;
        lastCrashAt = now;
        lastCrashDv = dv;
        var c = new Crash { t = now, dv = dv };
        ReceiveCrash(c);
        Knock(now);
        var net = Net;
        if (net != null && NetId != 0 && net.AnyRiderOn(NetId)) net.SendRideCrash(this, c);
        if (Truck != null && Truck.IsLocalPlayerSeated && Riding.AnyRiders(Truck)) Juice.Shake(0.06f, 0.12f);
    }

    /// <summary>A bump, curb, pothole or knock on this leg (Crew Comfort; at most one per <see cref="RideTuning.ComfortKnockGap"/>).</summary>
    void Knock(double now)
    {
        if (now - lastKnockAt < RideTuning.ComfortKnockGap) return;
        lastKnockAt = now;
        legKnocks++;
    }

    // ==================================================================================== Crew Comfort (driver)

    bool legActive;
    float legDist, legPeak, stoppedFor;
    int legRidersMin, legKnocks, flingsSeen, legFlingsAtStart;

    /// <summary>A rider on this truck was flung (any machine's event): spoils the driver's comfort leg.</summary>
    public void NoteFling() => flingsSeen++;

    void ComfortTick(float dt, float speed)
    {
        var net = Net;
        if (net == null || Truck == null || !Truck.IsSpawned || !Truck.IsLocalDriver || NetId == 0) { legActive = false; return; }
        int riders = net.RidersOn(NetId);
        if (!legActive)
        {
            if (speed > 2.5f && riders > 0)
            {
                legActive = true;
                legDist = 0f; legPeak = 0f; stoppedFor = 0f; legKnocks = 0;
                legRidersMin = riders;
                legFlingsAtStart = flingsSeen;
            }
            return;
        }
        legDist += speed * dt;
        legPeak = Mathf.Max(legPeak, Roughness(latEma, brakeEma, driftEma));
        legRidersMin = Mathf.Min(legRidersMin, riders);
        if (speed < RideTuning.ComfortStopSpeed) stoppedFor += dt; else stoppedFor = 0f;
        if (stoppedFor < 0.6f) return;
        legActive = false;
        if (legDist < RideTuning.ComfortMinLeg || legRidersMin <= 0 || flingsSeen != legFlingsAtStart) return;
        float smooth = Mathf.Clamp01(1f - legPeak / 160f - legKnocks * 0.15f);
        if (smooth < 0.35f) return;
        int cents = Mathf.RoundToInt(RideTuning.ComfortPerRider * legRidersMin * smooth * 100f);
        if (cents <= 0) return;
        net.LocalRidePay(HandsNet.RidePayComfort, NetId, cents);
        GameHUD.FloatingText(transform.position + Vector3.up * 3.6f, $"CREW COMFORT +${cents / 100f:0.00}", new Color(0.55f, 1f, 0.75f), 1.1f);
        Sfx.Play("cash", transform.position, 0.6f, 1.2f);
        NetAudit.Event("ride", new Dictionary<string, object> { { "op", "comfort" }, { "truck", (long)NetId }, { "cents", cents }, { "riders", legRidersMin }, { "smooth", System.Math.Round(smooth, 2) } });
    }

    // ==================================================================================== per frame

    void LateUpdate()
    {
        double now = RideClock.Now;
        Vector3 p = transform.position;
        Quaternion r = transform.rotation;
        if (histCount > 0)
        {
            var last = At(0);
            if (now < last.t - 0.25 || (p - last.p).sqrMagnitude > 30f * 30f) ResetHistory(); // clock change / door teleport
            else if (now <= last.t) { KitTick(); return; }
        }
        Push(now, p, r);
        EmitSample(now);
        if (histCount >= 2)
        {
            // Velocity over the last ~0.1 s of display.
            var a = At(0);
            Snap b = a;
            for (int i = 1; i < histCount; i++) { b = At(i); if (a.t - b.t >= 0.1) break; }
            if (a.t > b.t)
            {
                Vector3 v = (a.p - b.p) / (float)(a.t - b.t);
                Velocity = Vector3.Lerp(Velocity, v, 1f - Mathf.Exp(-12f * Time.deltaTime));
            }
        }
        SwayTick(r, Time.deltaTime);
        MatchPending();
        KitTick();
    }

    void SwayTick(Quaternion r, float dt)
    {
        if (swayHasPrev && dt > 1e-4f)
        {
            Vector3 aL = Quaternion.Inverse(r) * ((Velocity - swayPrevV) / dt);
            float fwd = Vector3.Dot(Velocity, r * Vector3.forward);
            var a = new Vector2(aL.x, Mathf.Abs(fwd) > 0.3f ? Mathf.Max(0f, -aL.z * Mathf.Sign(fwd)) : 0f);
            SwayAccel = Vector2.Lerp(SwayAccel, a, 1f - Mathf.Exp(-dt / 0.2f));
        }
        swayPrevV = Velocity;
        swayHasPrev = true;
    }

    // ==================================================================================== upgrades and the kit

    /// <summary>This truck's Padded Step / Safety Bar levels (its business's).</summary>
    public int PaddedLevel { get; private set; }
    public int BarLevel { get; private set; }
    /// <summary>The entire crew sees one host-owned bar position (solo keeps the same state locally).</summary>
    public bool BarDown { get; private set; } = true;
    bool soloBarDown = true;

    Transform kit;
    int kitPadded = -1, kitBar = -1;
    bool kitBarDown;
    float nextModsAt;
    byte sentPadded = 255, sentBar = 255;

    void KitTick()
    {
        if (Time.unscaledTime < nextModsAt) return;
        nextModsAt = Time.unscaledTime + 0.1f;
        var net = Net;
        int padded, bar;
        bool barDown;
        if (net != null && NetId != 0)
        {
            // The truck's business reports its levels; everyone reads the host's list.
            string biz = DepotTruckCargo.BusinessOf(Truck);
            if (!string.IsNullOrEmpty(biz) && biz == Businesses.LocalId)
            {
                byte pl = (byte)RideShop.Local(RideShop.PaddedStep), bl = (byte)RideShop.Local(RideShop.SafetyBar);
                if (pl != sentPadded || bl != sentBar || Time.unscaledTime > modsResendAt)
                {
                    sentPadded = pl; sentBar = bl; modsResendAt = Time.unscaledTime + 10f;
                    net.LocalRideMods(NetId, pl, bl);
                }
            }
            net.TryRideMods(NetId, out byte p2, out byte b2, out barDown);
            padded = p2; bar = b2;
        }
        else
        {
            padded = RideShop.Local(RideShop.PaddedStep);
            bar = RideShop.Local(RideShop.SafetyBar);
            barDown = soloBarDown;
        }
        PaddedLevel = padded;
        BarLevel = bar;
        BarDown = barDown;
        if (kit == null || kitPadded != padded || kitBar != bar || kitBarDown != barDown) BuildKit(padded, bar, barDown);
    }

    float modsResendAt;

    /// <summary>Only a rider may request the shared bar to fold; the host validates their ride seat.</summary>
    public bool ToggleBar(RideRider rider)
    {
        if (rider == null || BarLevel == 0 || !rider.TryGetRide(out var riding, out _) || riding != this) return false;
        var net = Net;
        if (net != null && NetId != 0) net.LocalRideBarToggle(NetId);
        else
        {
            soloBarDown = !soloBarDown;
            BarDown = soloBarDown;
            nextModsAt = 0f;
            KitTick();
        }
        return true;
    }

    void BuildKit(int padded, int bar, bool barDown)
    {
        kitPadded = padded;
        kitBar = bar;
        kitBarDown = barDown;
        if (kit != null) Destroy(kit.gameObject);
        kit = ShopModKit.MetreRoot(transform, "F1 Ride Kit");
        var steel = ShopModKit.Lit("f1_rail", new Color(0.78f, 0.8f, 0.82f), 0.6f, 0.7f);
        var frameSteel = ShopModKit.Lit("f1_step_frame", new Color(0.35f, 0.4f, 0.43f), 0.35f, 0.6f);
        var hiVis = ShopModKit.Lit("f1_pad", new Color(1f, 0.55f, 0.1f), 0.2f);
        var yellow = ShopModKit.Lit("f1_bar", new Color(1f, 0.85f, 0.1f), 0.4f, 0.2f);
        for (int side = -1; side <= 1; side += 2)
        {
            // Compact two-rung side ladder. Keep the four rider mounts, but remove the oversized scaffolding.
            float x = side * 1.36f;
            var f = RideHolds.FeetMetres(side < 0 ? RideHolds.RungLeft : RideHolds.RungRight);
            var rear = RideHolds.FeetMetres(side < 0 ? RideHolds.RearLeft : RideHolds.RearRight);
            // These are always present: the hold points need a visible place for the planted boots even without
            // the Padded Step upgrade. Renderer-only like the rails; rider pinning is still done by RideHolds.
            ShopModKit.Prim(PrimitiveType.Cube, "RearFootplate", kit, steel,
                rear + new Vector3(0f, -0.055f, 0f), new Vector3(0.42f, 0.06f, 0.25f));
            ShopModKit.Prim(PrimitiveType.Cube, "SideFootplate", kit, steel,
                f + new Vector3(0f, -0.055f, 0f), new Vector3(0.30f, 0.06f, 0.26f));
            // Cantilever each plate directly into the truck chassis; the inner upright overlaps the body/bumper.
            // These short square beams sit under the boots, not through them, and make the unpadded steps read as
            // built-in truck hardware rather than unattached floating tiles.
            ShopModKit.Prim(PrimitiveType.Cube, "RearStepArm", kit, frameSteel,
                new Vector3(rear.x, rear.y - 0.12f, -2.40f), new Vector3(0.07f, 0.07f, 0.64f));
            ShopModKit.Prim(PrimitiveType.Cube, "RearStepMount", kit, frameSteel,
                new Vector3(rear.x, 1.01f, -2.05f), new Vector3(0.12f, 0.96f, 0.18f));
            ShopModKit.Prim(PrimitiveType.Cube, "SideStepArm", kit, frameSteel,
                new Vector3(side * 1.17f, f.y - 0.12f, f.z), new Vector3(0.76f, 0.12f, 0.12f));
            ShopModKit.Prim(PrimitiveType.Cube, "SideStepMount", kit, frameSteel,
                new Vector3(side * 0.88f, 1.04f, f.z), new Vector3(0.14f, 0.90f, 0.14f));
            // TruckBody's main shell is ~0.96m wide from centre; the old 1.18m anchors were outside it
            // (the 1.3m coarse collision box includes mirrors). Bolt both ladder rails into the real shell.
            for (int k = 0; k < 2; k++)
            {
                float z = f.z + (k == 0 ? -RideHolds.LadderHalfWidth : RideHolds.LadderHalfWidth);
                ShopModKit.Prim(PrimitiveType.Cylinder, "LadderRail", kit, steel, new Vector3(x, 1.36f, z), new Vector3(0.04f, 0.40f, 0.04f));
                ShopModKit.Prim(PrimitiveType.Cube, "LadderTopStandoff", kit, frameSteel,
                    new Vector3(side * 1.10f, 1.70f, z), new Vector3(0.60f, 0.07f, 0.07f));
            }
            for (int k = 0; k < 2; k++)
                ShopModKit.Prim(PrimitiveType.Cylinder, "LadderRung", kit, steel, new Vector3(x + side * 0.01f, 1.00f + k * 0.42f, f.z), new Vector3(0.04f, 0.19f, 0.04f), new Vector3(90f, 0f, 0f));
            // The side plate meets the ladder's lowest rung through one short vertical stem.
            ShopModKit.Prim(PrimitiveType.Cylinder, "SideStepStem", kit, steel,
                new Vector3(x, 0.86f, f.z), new Vector3(0.05f, 0.14f, 0.05f));
            // Rear grab rail beside each step.
            ShopModKit.Prim(PrimitiveType.Cylinder, "GrabRail", kit, steel,
                new Vector3(side * 0.77f, 1.47f, -2.36f), new Vector3(0.04f, 0.29f, 0.04f));
            ShopModKit.Prim(PrimitiveType.Cube, "RearRailStandoff", kit, frameSteel,
                new Vector3(side * 0.77f, 1.70f, -2.16f), new Vector3(0.07f, 0.07f, 0.52f));
            if (padded > 0)
            {
                ShopModKit.Prim(PrimitiveType.Cube, "PaddedStep", kit, hiVis, rear + new Vector3(0f, -0.04f, 0f), new Vector3(0.42f, 0.06f, 0.25f));
                ShopModKit.Prim(PrimitiveType.Cube, "PaddedRung", kit, hiVis, f + new Vector3(0f, -0.04f, 0f), new Vector3(0.28f, 0.06f, 0.24f));
            }
        }
        // A real contact surface for both mitts of each rear-step rider; the two uprights alone only let one hand
        // reach a visible rail. The grip targets in RideHolds sit on this bar.
        ShopModKit.Prim(PrimitiveType.Cylinder, "RearGripBar", kit, steel, new Vector3(0f, 1.72f, -2.36f),
            new Vector3(0.04f, 0.77f, 0.04f), new Vector3(0f, 0f, 90f));
        if (bar > 0)
        {
            // A raised bar moves above the riders and is visibly distinct to every client.
            float barY = barDown ? 1.62f : 2.28f;
            float barZ = barDown ? -2.98f : -2.56f;
            ShopModKit.Prim(PrimitiveType.Cylinder, "SafetyBar", kit, yellow, new Vector3(0f, barY, barZ),
                new Vector3(0.07f, 0.72f, 0.07f), new Vector3(0f, 0f, 90f));
            for (int side = -1; side <= 1; side += 2)
            {
                var mount = new Vector3(side * 0.72f, 1.52f, -2.36f);
                var end = new Vector3(side * 0.72f, barY, barZ);
                var arm = end - mount;
                ShopModKit.Prim(PrimitiveType.Cylinder, "SafetyBarArm", kit, yellow,
                    (mount + end) * 0.5f, new Vector3(0.06f, arm.magnitude * 0.5f, 0.06f),
                    Quaternion.FromToRotation(Vector3.up, arm).eulerAngles);
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}

/// <summary>A short, low-speed solo ride along directed open road lanes. Built only when the rider presses C; no
/// teleports, lane changes, or route replans. It yields to obstacles and parks at the end of the available lane.</summary>
public sealed class SoloCruiseRoute
{
    const float MaxStartOffset = 4.5f, MaxRoute = 90f, CruiseSpeed = 2.35f;
    const float ObstacleLook = 5f, RouteLost = 7f;
    static readonly RaycastHit[] obstacleHits = new RaycastHit[16];

    readonly List<Vector3> points = new List<Vector3>(32);
    readonly List<float> arc = new List<float>(32);
    float length, progress, blockedFor, stalledFor;
    public float Length => length;
    public Vector3 End => points.Count > 0 ? points[points.Count - 1] : Vector3.zero;
    public Vector2 Command { get; private set; }
    public bool Done { get; private set; }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void Add(Vector3 p)
    {
        p.y = 0f;
        if (points.Count > 0)
        {
            float d = (p - points[points.Count - 1]).magnitude;
            if (d < 0.2f) return;
            if (length + d > MaxRoute)
            {
                p = Vector3.Lerp(points[points.Count - 1], p, (MaxRoute - length) / d);
                d = MaxRoute - length;
            }
            length += d;
        }
        points.Add(p);
        arc.Add(length);
    }

    public static bool TryBuild(StreetGraph graph, TruckControllerV2 truck, int openMask, out SoloCruiseRoute route)
    {
        route = null;
        if (graph == null || graph.nodes == null || graph.edges == null || truck == null) return false;
        Vector3 p = Flat(truck.transform.position), forward = Flat(truck.transform.forward).normalized;
        int first = -1;
        float firstT = 0f, best = float.MaxValue;
        // Pick a nearby lane pointing the way the player parked. This avoids turning an unattended truck across
        // traffic or steering out of a lot without a route.
        for (int i = 0; i < graph.EdgeCount; i++)
        {
            var e = graph.edges[i];
            if (e.kind != StreetGraph.EdgeKind.Road || e.a >= graph.NodeCount || e.b >= graph.NodeCount ||
                !graph.EdgeOpen(i, openMask)) continue;
            Vector3 a = Flat(graph.nodes[e.a].pos), b = Flat(graph.nodes[e.b].pos), d = b - a;
            if (d.sqrMagnitude < 0.25f) continue;
            float aligned = Vector3.Dot(forward, d.normalized);
            if (aligned < 0.72f) continue;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, d) / d.sqrMagnitude);
            float offset = (p - (a + d * t)).sqrMagnitude;
            if (offset > MaxStartOffset * MaxStartOffset) continue;
            float score = offset + (1f - aligned) * 3f;
            if (score >= best) continue;
            best = score; first = i; firstT = t;
        }
        if (first < 0) return false;

        var built = new SoloCruiseRoute();
        var start = graph.edges[first];
        Vector3 from = Flat(graph.nodes[start.a].pos), to = Flat(graph.nodes[start.b].pos);
        built.Add(Vector3.Lerp(from, to, firstT));
        built.Add(to);
        int node = start.b;
        Vector3 previous = (to - from).normalized;
        var used = new HashSet<int> { first };
        // Prefer the straightest directed continuation. A turn is allowed at low speed; a U-turn and a closed gate are not.
        for (int step = 0; step < 30 && built.length < MaxRoute - 0.2f; step++)
        {
            int next = -1;
            float bestDot = -0.2f;
            for (int k = graph.OutBegin(node); k < graph.OutEnd(node); k++)
            {
                int ei = graph.OutEdge(k);
                var e = graph.edges[ei];
                if (used.Contains(ei) || e.kind != StreetGraph.EdgeKind.Road || !graph.EdgeOpen(ei, openMask)) continue;
                int dest = graph.OutTo(k);
                Vector3 d = Flat(graph.nodes[dest].pos - graph.nodes[node].pos);
                if (d.sqrMagnitude < 0.25f) continue;
                float dot = Vector3.Dot(previous, d.normalized);
                if (dot <= bestDot) continue;
                bestDot = dot; next = ei;
            }
            if (next < 0) break;
            used.Add(next);
            var edge = graph.edges[next];
            int nextNode = edge.a == node ? edge.b : edge.a;
            Vector3 destination = Flat(graph.nodes[nextNode].pos);
            previous = (destination - Flat(graph.nodes[node].pos)).normalized;
            built.Add(destination);
            node = nextNode;
        }
        if (built.length < 12f) return false;
        route = built;
        return true;
    }

    Vector3 PointAt(float s)
    {
        s = Mathf.Clamp(s, 0f, length);
        for (int i = 1; i < points.Count; i++)
            if (s <= arc[i]) return Vector3.Lerp(points[i - 1], points[i],
                (s - arc[i - 1]) / Mathf.Max(0.001f, arc[i] - arc[i - 1]));
        return points[points.Count - 1];
    }

    static bool Blocked(TruckControllerV2 truck, PlayerController rider)
    {
        var t = truck.transform;
        // along the truck's own nose: on a climb a level cast would call the road ahead an obstacle
        Vector3 f = t.forward;
        Vector3 origin = t.position + t.up * 1.45f + f * 3.45f;
        int n = Physics.BoxCastNonAlloc(origin, new Vector3(1.2f, 0.65f, 0.4f), f, obstacleHits,
            t.rotation, ObstacleLook, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = obstacleHits[i].collider;
            if (c == null || c.transform.IsChildOf(t) || (rider != null && c.transform.IsChildOf(rider.transform))) continue;
            return true;
        }
        return false;
    }

    public void Tick(TruckControllerV2 truck, Rigidbody body, float dt, PlayerController rider)
    {
        if (Done) return;
        Vector3 p = Flat(truck.transform.position);
        float nearest = float.MaxValue, nearestS = progress;
        for (int i = 1; i < points.Count; i++)
        {
            if (arc[i] < progress - 5f || arc[i - 1] > progress + 20f) continue;
            Vector3 a = points[i - 1], d = points[i] - a;
            float u = Mathf.Clamp01(Vector3.Dot(p - a, d) / Mathf.Max(0.001f, d.sqrMagnitude));
            float sq = (p - (a + d * u)).sqrMagnitude;
            if (sq >= nearest) continue;
            nearest = sq; nearestS = Mathf.Lerp(arc[i - 1], arc[i], u);
        }
        if (nearest > RouteLost * RouteLost) { Done = true; Command = Vector2.zero; return; }
        float advanced = Mathf.Max(0f, nearestS - progress);
        progress = Mathf.Max(progress, nearestS);
        stalledFor = advanced < 0.02f ? stalledFor + dt : 0f;
        if (stalledFor > 8f) { Done = true; Command = Vector2.zero; return; }

        float speed = Vector3.Dot(truck.transform.forward, body.linearVelocity);
        Vector3 target = PointAt(progress + Mathf.Clamp(3.5f + Mathf.Abs(speed) * 0.35f, 3.5f, 5f));
        Vector3 to = target - p;
        Vector3 forward = Flat(truck.transform.forward).normalized;
        float angle = to.sqrMagnitude > 0.01f ? Vector3.SignedAngle(forward, to, Vector3.up) : 0f;
        float steerRange = Mathf.Lerp(truck.steeringRange, truck.steeringRangeAtMaxSpeed,
            Mathf.InverseLerp(0f, truck.maxSpeed, Mathf.Abs(speed)));
        float steer = Mathf.Clamp(angle / Mathf.Max(1f, steerRange), -1f, 1f);
        bool blocked = Blocked(truck, rider);
        blockedFor = blocked ? blockedFor + dt : 0f;
        float remaining = length - progress;
        float targetSpeed = Mathf.Min(CruiseSpeed, Mathf.Sqrt(3f * Mathf.Max(0f, remaining - 1.5f)));
        if (Mathf.Abs(angle) > 35f) targetSpeed = Mathf.Min(targetSpeed, 1.3f);
        if (blocked) targetSpeed = 0f;
        if ((remaining < 1.7f || blockedFor > 2.5f) && Mathf.Abs(speed) < 0.3f)
        { Done = true; Command = Vector2.zero; return; }
        float throttle = Mathf.Clamp((targetSpeed - speed) * 0.7f, -0.6f, 0.65f);
        Command = new Vector2(steer, throttle);
    }
}

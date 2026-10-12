using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base for the living-world critters (raccoons, pile goblins). Plain local MonoBehaviours in the active scene,
/// spawned by the host-side CritterSpawner — no NetworkObjects.
///
/// Provides: perception of the local player / truck, flat-ground steering with SphereCast obstacle avoidance
/// (houses, fences, bins, the truck, map walls), ground snapping, being bonked by thrown carryables (trigger on the
/// Ignore Raycast layer), getting launched when the truck runs them over (cartoon arc + spin, never hurt),
/// horn scares, and a poof-despawn. Subclasses implement the state machine in <see cref="Tick"/> and their
/// procedural animation.
/// </summary>
[DisallowMultipleComponent]
public abstract class Critter : MonoBehaviour
{
    public static readonly List<Critter> All = new List<Critter>();

    public CritterRig Rig { get; protected set; }
    /// <summary>The curb stop this critter belongs to (may be null for debug spawns).</summary>
    public PickupStop Home;
    public abstract string DisplayName { get; }
    /// <summary>True once the critter is poofing away (ignore it).</summary>
    public bool Despawning { get; private set; }

    // ---------------- co-op (F4, Docs/Multiplayer.md §14.4: NightNet streams host critters to guests) ----------------
    /// <summary>The id the host streams this critter under (0 = not streamed yet).</summary>
    public ushort NetId;
    /// <summary>A guest's copy of a host critter: no brain, it follows the host's snapshots and plays the looks.</summary>
    public bool NetPuppet { get; private set; }
    /// <summary>The player this critter is about this frame (nearest on foot, else nearest; <see cref="NightPlayers"/>).</summary>
    protected NightTarget target;
    /// <summary>Client id of whoever threw the last thing that bonked us (host).</summary>
    protected ulong lastHitBy;

    // ---------------- perception (refreshed every frame) ----------------
    protected PlayerController player;
    protected bool playerOnFoot;
    protected bool playerDriving;
    protected Vector3 playerFeet;
    protected float playerDist = 999f;
    protected Vector3 PlayerHead => playerFeet + Vector3.up * 1.5f;

    // ---------------- motion ----------------
    protected float speedNow;
    protected float hopPhase;
    protected float turnRate = 540f;
    protected float stepRadius = 0.2f;
    Vector3 steerDir;
    float nextSteerAt;
    Vector3 stuckCheckPos;
    float stuckCheckAt;
    float unstickUntil;
    Vector3 unstickDir;
    float groundY;
    bool grounded;

    // ---------------- airborne (launched by the truck) ----------------
    protected bool Airborne { get; private set; }
    Vector3 airVel;
    Vector3 airSpinAxis;
    float airSpin;
    int bounces;
    float hitCooldownUntil;
    float runOverCooldownUntil;

    static readonly RaycastHit[] castHits = new RaycastHit[12];
    static readonly float[] probeAngles = { 0f, 30f, -30f, 60f, -60f, 95f, -95f, 140f, -140f, 180f };

    protected virtual void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    protected virtual void OnDisable() { All.Remove(this); }

    protected virtual void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || Rig == null) return;
        Sense();
        if (NetPuppet) { PuppetFollow(dt); if (!Despawning) TickPuppet(dt); return; }
        if (anchorSet) { FollowAnchor(); if (!Despawning) Tick(dt); return; }
        if (Airborne) { TickAirborne(dt); return; }
        if (Despawning) return;
        CheckRunOver();
        Tick(dt);
        if (GroundSnap) SnapToGround(dt);
    }

    /// <summary>The state machine + animation for this frame (not called while airborne / despawning).</summary>
    protected abstract void Tick(float dt);

    /// <summary>False while the critter positions itself vertically (e.g. a goblin sitting on top of a pile).</summary>
    protected virtual bool GroundSnap => true;

    /// <summary>A thrown bag / item hit us.</summary>
    protected abstract void OnHitByThrown(Carryable c, Vector3 velocity);

    /// <summary>Loud noise (truck horn) at <paramref name="from"/>.</summary>
    public abstract void Scare(Vector3 from, bool horn);

    /// <summary>Landed after being launched by the truck.</summary>
    protected abstract void OnLanded();

    /// <summary>A trash bag was just grabbed by a player somewhere.</summary>
    public virtual void OnBagGrabbed(TrashBag bag, TrashCarrier by) { }

    // =====================================================================
    // Perception
    // =====================================================================

    void Sense()
    {
        // Co-op (§4.10): the nearest player on foot, not just this machine's own (solo: the local player, as before).
        if (!NightPlayers.Nearest(transform.position, out target))
        {
            player = null; playerOnFoot = false; playerDriving = false; playerDist = 999f;
            return;
        }
        player = target.pc;
        playerOnFoot = target.onFoot;
        playerDriving = target.driving;
        playerFeet = target.feet;
        playerDist = target.dist;
    }

    protected Vector3 FlatDirTo(Vector3 p)
    {
        Vector3 d = CritterWorld.Flat(p - transform.position);
        return d.sqrMagnitude > 0.0001f ? d.normalized : transform.forward;
    }

    // =====================================================================
    // Steering
    // =====================================================================

    /// <summary>Walk/run along desiredDir (flattened) at speed, steering around obstacles. Returns the step length.</summary>
    protected float Move(Vector3 desiredDir, float speed, float dt)
    {
        desiredDir.y = 0f;
        if (speed <= 0.01f || desiredDir.sqrMagnitude < 0.0001f)
        {
            speedNow = Mathf.MoveTowards(speedNow, 0f, dt * 12f);
            return 0f;
        }
        desiredDir.Normalize();

        if (Time.time < unstickUntil) desiredDir = unstickDir;
        if (Time.time >= nextSteerAt)
        {
            nextSteerAt = Time.time + Random.Range(0.08f, 0.14f);
            steerDir = ChooseDirection(desiredDir, Mathf.Clamp(speed * 0.45f, 0.7f, 2.2f));
        }
        if (steerDir.sqrMagnitude < 0.0001f) steerDir = desiredDir;

        Vector3 fwd = Vector3.RotateTowards(CritterWorld.Flat(transform.forward).normalized, steerDir, turnRate * Mathf.Deg2Rad * dt, 0f);
        if (fwd.sqrMagnitude < 0.0001f) fwd = steerDir;
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);

        speedNow = Mathf.MoveTowards(speedNow, speed, dt * 14f);
        float step = speedNow * dt;
        // Last-moment check so we never walk into a wall even between steering updates.
        if (Blocked(fwd, step + stepRadius * 0.5f)) step = 0f;
        transform.position += fwd * step;
        hopPhase += step * 7.5f;

        // Stuck? (pinned against something the probes missed) → wiggle out sideways for a moment.
        if (Time.time >= stuckCheckAt)
        {
            float moved = CritterWorld.Flat(transform.position - stuckCheckPos).magnitude;
            if (speed > 0.8f && moved < speed * 0.6f * 0.12f && Time.time > unstickUntil)
            {
                unstickDir = Quaternion.Euler(0f, Random.value < 0.5f ? 100f : -100f, 0f) * desiredDir;
                unstickUntil = Time.time + 0.6f;
            }
            stuckCheckPos = transform.position;
            stuckCheckAt = Time.time + 0.6f;
        }
        return step;
    }

    /// <summary>Move toward a point; true once within arriveDist.</summary>
    protected bool MoveTo(Vector3 target, float speed, float dt, float arriveDist = 0.25f)
    {
        Vector3 d = CritterWorld.Flat(target - transform.position);
        float dist = d.magnitude;
        if (dist <= arriveDist) { Move(Vector3.zero, 0f, dt); return true; }
        // Ease in so we don't overshoot tiny targets.
        Move(d / dist, Mathf.Min(speed, Mathf.Max(0.6f, dist * 3f)), dt);
        return false;
    }

    /// <summary>Turn in place toward a point.</summary>
    protected void Face(Vector3 point, float dt, float degPerSec = 360f)
    {
        Vector3 d = CritterWorld.Flat(point - transform.position);
        if (d.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d.normalized, Vector3.up), degPerSec * dt);
    }

    Vector3 ChooseDirection(Vector3 desired, float look)
    {
        for (int i = 0; i < probeAngles.Length; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, probeAngles[i], 0f) * desired;
            if (!Blocked(dir, look)) return dir;
        }
        return -desired;
    }

    /// <summary>Is there an obstacle within dist along dir (ignores bags, loose items, players, triggers, other critters)?</summary>
    protected bool Blocked(Vector3 dir, float dist)
    {
        if (dist <= 0f) return false;
        // Shop interiors always, Depot Row lots in the one world (§4.10: nobody leads raccoons into a neighbour's bay).
        if (CritterWorld.KeepOut(transform.position + dir * dist, KeepOutOfLots)) return true;
        Vector3 origin = transform.position + Vector3.up * (CritterWorld.StepHeight + stepRadius);
        int n = Physics.SphereCastNonAlloc(origin, stepRadius, dir, castHits, dist, CritterWorld.ObstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = castHits[i];
            var col = h.collider;
            if (col == null || col is CharacterController) continue;
            if (h.distance <= 0f && h.point == Vector3.zero) continue; // started inside (e.g. hugging a hedge): let it walk out
            var rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < 60f) continue; // bags, items: we clamber over those
            if (col.GetComponentInParent<Carryable>() != null) continue;
            if (col.GetComponentInParent<PlayerController>() != null) continue;
            if (Vector3.Dot(h.normal, Vector3.up) > 0.7f) continue; // walkable-ish top surface (curb lip, ramp)
            return true;
        }
        return false;
    }

    void SnapToGround(float dt)
    {
        Vector3 p = transform.position;
        float gy = CritterWorld.GroundY(p, float.NaN);
        if (float.IsNaN(gy)) { grounded = false; return; }
        groundY = gy;
        grounded = true;
        if (Mathf.Abs(p.y - gy) > 0.0005f)
        {
            // Step up curbs quickly, fall down a bit slower (looks less teleporty).
            p.y = p.y < gy ? Mathf.MoveTowards(p.y, gy, dt * 3f) : Mathf.MoveTowards(p.y, gy, dt * 2f);
            transform.position = p;
        }
    }

    /// <summary>False for critters allowed onto the Depot Row lots (the Raccoon King). Shop interiors are always off limits.</summary>
    protected virtual bool KeepOutOfLots => true;

    /// <summary>Ground height under us (last known).</summary>
    protected float GroundHeight => grounded ? groundY : transform.position.y;

    // =====================================================================
    // Hits: thrown things, truck, horn
    // =====================================================================

    void OnTriggerEnter(Collider other)
    {
        if (Despawning || Airborne || other == null) return;
        var c = other.GetComponentInParent<Carryable>();
        if (c == null || c.State != CarryState.Thrown || c.IsInScriptedFlight) return;
        if (c.NetRemote) return; // another machine's throw: the thrower's machine detects the hit and tells the host (F4)
        var rb = c.Body;
        Vector3 v = rb != null ? rb.linearVelocity : Vector3.zero;
        if (v.magnitude < 2.2f || Time.time < hitCooldownUntil) return;
        hitCooldownUntil = Time.time + 0.6f;

        // Shared juice: the hit itself.
        Vector3 at = Rig != null && Rig.head != null ? Rig.head.position : transform.position + Vector3.up * 0.4f;
        Sfx.Play("thud", at, 0.9f, Random.Range(1.1f, 1.3f));
        Sfx.Play("squeak", at, 0.8f, Random.Range(1.2f, 1.5f));
        Fx.Burst(at, new Color(1f, 0.9f, 0.3f), 10, 3f);
        GameHUD.FloatingText(at + Vector3.up * 0.4f, Random.value < 0.5f ? "BONK!" : "DOINK!", new Color(1f, 0.85f, 0.25f), 1.1f);
        if (Rig != null) { Juice.Squash(Rig.juice, 0.35f, 0.3f); Rig.KickEyes(5f); }
        // Knock the bag back a little so it doesn't sail through.
        if (rb != null && !rb.isKinematic) rb.linearVelocity = -v * 0.25f + Vector3.up * 2f;
        var thrower = c.LastHolder != null ? c.LastHolder.GetComponent<PlayerController>() : null;
        ulong by = NightPlayers.ClientOf(thrower);
        if (NetPuppet) { NightNet.RequestBonk(this, v, by); return; } // the host's critter takes the hit (juice already played here)
        lastHitBy = by;
        bonkAt[by] = Time.time;
        OnHitByThrown(c, v);
    }

    readonly Dictionary<ulong, float> bonkAt = new Dictionary<ulong, float>();

    /// <summary>Stable, forgiving body target for fists, including pile goblins with projectile-only triggers.</summary>
    public Vector3 MeleePoint => transform.position + Vector3.up * 0.55f;

    /// <summary>Authority-only melee hit, sharing projectile reactions/loot drops and per-attacker debounce.</summary>
    public bool TryMeleeBonk(Vector3 velocity, ulong by)
    {
        if (Despawning || Airborne || NetPuppet || !isActiveAndEnabled) return false;
        if (bonkAt.TryGetValue(by, out float last) && Time.time - last < 0.5f) return false;
        NetBonk(velocity, by);
        return true;
    }

    /// <summary>Host: another machine's throw bonked our copy there (the juice played on that machine).</summary>
    public void NetBonk(Vector3 velocity, ulong by)
    {
        if (Despawning || Airborne || NetPuppet) return;
        // Debounce per thrower (one bag never hits twice), so a whole team can pelt the same critter.
        if (bonkAt.TryGetValue(by, out float last) && Time.time - last < 0.5f) return;
        bonkAt[by] = Time.time;
        hitCooldownUntil = Time.time + 0.6f;
        lastHitBy = by;
        if (Rig != null) { Juice.Squash(Rig.juice, 0.35f, 0.3f); Rig.KickEyes(5f); }
        OnHitByThrown(null, velocity);
    }

    void CheckRunOver()
    {
        if (Time.time < runOverCooldownUntil) return;
        // Every truck in the Town (co-op has one per business), not just the first one.
        TruckControllerV2 truck = null;
        Vector3 tv = Vector3.zero;
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null) continue;
            Vector3 v = CritterWorld.VelocityOf(t);
            if (v.sqrMagnitude < 2.5f * 2.5f) continue;
            if (!CritterWorld.InsideTruckBox(t, transform.position + Vector3.up * 0.3f, 0.35f)) continue;
            truck = t; tv = v;
            break;
        }
        if (truck == null) return;
        runOverCooldownUntil = Time.time + 1.5f;

        // Cartoon physics: BOING off the bumper, spin through the air, land dizzy. Nobody gets hurt.
        Vector3 side = CritterWorld.Flat(transform.position - truck.transform.position);
        side = side.sqrMagnitude > 0.01f ? side.normalized : truck.transform.right;
        Vector3 launch = CritterWorld.Flat(tv) * 0.55f + side * 3f + Vector3.up * Random.Range(6f, 8f);
        Launch(launch);
        Vector3 at = transform.position + Vector3.up * 0.5f;
        Sfx.Play("squeak", at, 1f, 0.7f);
        Sfx.Play("thud", at, 0.8f, 1.2f);
        GameHUD.FloatingText(at + Vector3.up * 0.5f, Random.value < 0.5f ? "BOING!" : "WHEEE!", new Color(1f, 0.6f, 0.9f), 1.3f);
        Juice.Shake(0.08f, 0.12f);
    }

    /// <summary>Send the critter flying (ballistic arc + tumbling) until it lands.</summary>
    public void Launch(Vector3 velocity)
    {
        Airborne = true;
        airVel = velocity;
        airSpinAxis = Random.onUnitSphere;
        airSpin = Random.Range(540f, 900f);
        bounces = 0;
        if (Rig != null) Rig.KickEyes(6f);
    }

    void TickAirborne(float dt)
    {
        airVel += Physics.gravity * dt;
        Vector3 p = transform.position + airVel * dt;
        // Don't fly through walls: stop horizontal motion when blocked.
        Vector3 flat = CritterWorld.Flat(airVel);
        if (flat.sqrMagnitude > 0.01f && Blocked(flat.normalized, flat.magnitude * dt + stepRadius))
        {
            airVel = new Vector3(-airVel.x * 0.3f, airVel.y, -airVel.z * 0.3f);
            p = transform.position + Vector3.up * airVel.y * dt;
        }
        transform.position = p;
        if (Rig != null) Rig.visual.localRotation = Quaternion.AngleAxis(airSpin * dt, airSpinAxis) * Rig.visual.localRotation;

        float gy = CritterWorld.GroundY(p, p.y - 10f);
        if (airVel.y < 0f && p.y <= gy)
        {
            p.y = gy;
            transform.position = p;
            if (bounces == 0 && airVel.y < -4f)
            {
                bounces++;
                airVel = new Vector3(airVel.x * 0.4f, -airVel.y * 0.35f, airVel.z * 0.4f);
                Fx.Dust(p, 0.6f);
                Sfx.Play("thud", p, 0.6f, 1.3f);
                if (Rig != null) Juice.Squash(Rig.juice, 0.4f, 0.25f);
                return;
            }
            Airborne = false;
            if (Rig != null) { Rig.visual.localRotation = Quaternion.identity; Juice.Squash(Rig.juice, 0.3f, 0.25f); }
            Fx.Dust(p, 0.5f);
            OnLanded();
        }
        if (p.y < -30f) Poof(false);
    }

    // =====================================================================
    // Player hits
    // =====================================================================

    /// <summary>Knock the local player back (bite / bonk). Returns false if the player was protected (repellent) or not hittable.</summary>
    protected bool HitPlayer(float distance, float hop, string word, Color color, string sfx, float shake) =>
        HitPlayer(distance, hop, word, color, sfx, shake, false);

    /// <summary>
    /// Knock the target player back (bite / bonk): this machine's own player directly, anyone else through
    /// <see cref="NightNet"/> (their machine plays it; <paramref name="infect"/> = the host's rabies roll came up).
    /// False when nobody is on foot, the player is still immune from the last critter hit (5 s, §12 G3), or (local) protected.
    /// </summary>
    protected bool HitPlayer(float distance, float hop, string word, Color color, string sfx, float shake, bool infect)
    {
        if (!playerOnFoot || player == null) return false;
        if (NightShift.CritterImmune(target.client)) return false;
        Vector3 dir = CritterWorld.Flat(playerFeet - transform.position);
        dir = dir.sqrMagnitude > 0.001f ? dir.normalized : transform.forward;
        Vector3 src = Rig != null && Rig.head != null ? Rig.head.position : transform.position;
        bool ok = target.local
            ? CritterKnockback.HitLocalPlayer(dir, distance, hop, word, color, sfx, shake, src)
            : NightNet.HostHitRemote(target.client, dir, distance, hop, word, color, sfx, shake, src, infect);
        if (ok) NightShift.GrantCritterImmunity(target.client);
        return ok;
    }

    // =====================================================================
    // Co-op puppets and anchors (F4)
    // =====================================================================

    Vector3 netFrom, netTo;
    float netYawFrom, netYawTo, netT = 1f, netDur = 0.25f;
    bool netHas;
    Transform anchor;
    Vector3 anchorLocal;
    Quaternion anchorRot = Quaternion.identity;
    bool anchorSet;
    Transform anchorFrame;      // axes the local offset is expressed in (null = the anchor itself); lets a bone supply position only
    Vector3 anchorScaleBackup;  // root scale to restore when the anchor is cleared
    bool anchorScaled;

    /// <summary>Guest: this object copies the host's critter <paramref name="id"/>.</summary>
    public void MakePuppet(ushort id) { NetPuppet = true; NetId = id; }

    /// <summary>The host's word on where this critter is (guests). <paramref name="interval"/> = seconds between snapshots.</summary>
    public void NetSnapshot(Vector3 pos, float yaw, float interval)
    {
        if (!netHas || (pos - transform.position).sqrMagnitude > 12f * 12f)
        {
            netHas = true;
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            netFrom = netTo = pos; netYawFrom = netYawTo = yaw; netT = 1f;
            return;
        }
        netFrom = transform.position;
        netYawFrom = transform.eulerAngles.y;
        netTo = pos;
        netYawTo = yaw;
        netT = 0f;
        netDur = Mathf.Clamp(interval * 1.2f, 0.05f, 0.6f);
    }

    void PuppetFollow(float dt)
    {
        if (anchorSet) { FollowAnchor(); speedNow = Damp(speedNow, 0f, 10f, dt); return; }
        Vector3 before = transform.position;
        netT = Mathf.Min(1f, netT + dt / Mathf.Max(0.01f, netDur));
        Vector3 p = Vector3.Lerp(netFrom, netTo, netT);
        transform.SetPositionAndRotation(p, Quaternion.Euler(0f, Mathf.LerpAngle(netYawFrom, netYawTo, netT), 0f));
        float moved = CritterWorld.Flat(p - before).magnitude;
        speedNow = Damp(speedNow, moved / dt, 10f, dt);
        hopPhase += moved * 7.5f;
    }

    /// <summary>Guests: looks only (state from <see cref="ApplyNetState"/>).</summary>
    protected virtual void TickPuppet(float dt) { }

    /// <summary>What the host streams as this critter's state byte.</summary>
    public virtual byte NetState => 0;
    /// <summary>Flag bits streamed with the state (subclass meaning).</summary>
    public virtual byte NetFlags => 0;
    /// <summary>Guest: the host's critter is in this state now.</summary>
    public virtual void ApplyNetState(byte state, byte flags) { }

    public bool HasAnchor => anchorSet;
    public Transform AnchorTransform => anchor;

    /// <summary>Ride along with <paramref name="t"/> (a truck's hopper, a player's head) at a local offset (no scale).</summary>
    public void SetAnchor(Transform t, Vector3 localPos, Quaternion localRot) => SetAnchor(t, null, localPos, localRot, 0f);

    /// <summary>
    /// Ride along with <paramref name="bone"/> (position only) at <paramref name="localPos"/> world metres, expressed in the axes of
    /// <paramref name="frame"/> (e.g. the player root, so the unknown bone orientation can't tip the critter). <paramref name="scale"/>
    /// &gt; 0 also sets the critter's root scale until <see cref="ClearAnchor"/>.
    /// </summary>
    public void SetAnchor(Transform bone, Transform frame, Vector3 localPos, Quaternion localRot, float scale)
    {
        RestoreAnchorScale();
        anchor = bone;
        anchorFrame = frame;
        if (scale > 0f && bone != null)
        {
            anchorScaleBackup = transform.localScale;
            anchorScaled = true;
            transform.localScale = anchorScaleBackup * scale;
        }
        anchorLocal = localPos;
        anchorRot = localRot;
        anchorSet = bone != null;
        if (anchorSet) FollowAnchor();
    }

    void RestoreAnchorScale()
    {
        if (!anchorScaled) return;
        anchorScaled = false;
        transform.localScale = anchorScaleBackup;
    }

    public void ClearAnchor()
    {
        RestoreAnchorScale();
        anchorSet = false;
        anchor = null;
        anchorFrame = null;
        netHas = false; // puppets: snap to the next snapshot
    }

    void FollowAnchor()
    {
        if (anchor == null) { ClearAnchor(); OnAnchorLost(); return; }
        Quaternion fr = anchorFrame != null ? anchorFrame.rotation : anchor.rotation;
        transform.SetPositionAndRotation(anchor.position + fr * anchorLocal, fr * anchorRot);
    }

    /// <summary>The thing we rode on vanished.</summary>
    protected virtual void OnAnchorLost() { }

    // =====================================================================
    // Despawn
    // =====================================================================

    /// <summary>Vanish in a puff of dust (ninja exit).</summary>
    public void Poof(bool fx = true)
    {
        if (Despawning) return;
        Despawning = true;
        if (fx)
        {
            Vector3 p = transform.position + Vector3.up * 0.3f;
            Fx.Dust(p, 0.8f);
            Sfx.Play("pop", p, 0.5f, Random.Range(0.8f, 1.1f));
        }
        Juice.ShrinkAndDestroy(gameObject, 0.25f);
    }

    /// <summary>Remove immediately (scene cleanup, debug).</summary>
    public void RemoveNow()
    {
        Despawning = true;
        Destroy(gameObject);
    }

    // =====================================================================
    // Animation helpers
    // =====================================================================

    protected static float Damp(float current, float target, float sharpness, float dt) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    protected static Vector3 Damp(Vector3 current, Vector3 target, float sharpness, float dt) =>
        Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    protected static Quaternion Damp(Quaternion current, Quaternion target, float sharpness, float dt) =>
        Quaternion.Slerp(current, target, 1f - Mathf.Exp(-sharpness * dt));

    /// <summary>Is the local player looking at (roughly) this critter? Used to decide if despawns can be sneaky.</summary>
    protected bool Seen => CritterWorld.CameraCanSee(transform.position + Vector3.up * 0.3f);

    protected static string Pick(string[] lines) => lines == null || lines.Length == 0 ? "" : lines[Random.Range(0, lines.Length)];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => All.Clear();
}

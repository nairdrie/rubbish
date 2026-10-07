using UnityEngine;

/// <summary>
/// The acts a hit starts (Docs/WorldLife.md §8.1, §8.4, §8.5): presentation only, played on every machine from the
/// decided <see cref="NpcHit"/>. Nobody is ever hurt: no blood, no injury states, nobody stays down.
/// <list type="table">
/// <item><b>Flatten</b>: ballistic flop (the hit's launch, spin 540–900°/s about an axis from the seed byte, one bounce at
/// restitution 0.35, walls clamp the horizontal motion) → lie 1.6 s in a starfish with dizzy stars → get up 0.6 s →
/// angry 1.4 s (red face, brows, steam, fist) → chase the hitter's truck ≤ 3 s at 4.5 m/s, giving up past 15 m with a
/// stomp → jog back to the hit point and catch up with the timetable. Immune until 2 s after rejoining.</item>
/// <item><b>ShortFlop</b> (a ≥ 11 m/s throw): the same with a 1.0 s lie and no chase.</item>
/// <item><b>Nudge</b>: pushed 0.6 m aside, "HEY! WATCH IT!", glare. <b>Startle</b> (horn): a 0.4 m hop, "AAH!", 2–3 s
/// stepping off the truck's line, then a glare. <b>Stumble</b>: 0.5 m back. <b>Boop</b>: a squash. <b>Dodge</b>: the
/// rejected-claim leap.</item>
/// </list>
/// </summary>
public sealed partial class ResidentAgent
{
    public enum Act : byte { None, Flop, Nudge, Startle, Stumble, Boop, Dodge }
    public enum FlopPhase : byte { Air, Lie, GetUp, Angry, Chase, GiveUp, Rejoin }

    public Act act;
    public FlopPhase flop;
    float actT, phaseT;
    Vector3 vel, spinAxis, hitPoint, actFrom, actTo;
    Quaternion spin = Quaternion.identity;
    float spinRate, groundY, lieSeconds, stepSeconds, nextGroundProbe;
    int bounces;
    bool canChase, propHidden;
    TruckControllerV2 chaseTruck;
    float immuneUntil;
    NpcHitKind lastKind;

    /// <summary>Stats for the acceptance evals.</summary>
    public float LastAirSeconds { get; private set; }
    public float LastLieSeconds { get; private set; }
    public float LastChaseSeconds { get; private set; }
    public Vector3 LastLandPoint { get; private set; }
    public Vector3 LastHitPoint => hitPoint;
    public NpcHitKind LastHitKind => lastKind;
    public int Flops { get; private set; }

    static readonly int WallMask = (1 << 0) | (1 << 6) | (1 << 7);   // Default, Ground (houses, cars), Walls
    const int GroundMask = 1 << 6;

    void ResetActs()
    {
        act = Act.None; flop = FlopPhase.Air; actT = phaseT = 0f; spin = Quaternion.identity; bounces = 0;
        canChase = false; chaseTruck = null; immuneUntil = 0f; propHidden = false; lastKind = NpcHitKind.None;
        LastAirSeconds = LastLieSeconds = LastChaseSeconds = 0f; Flops = 0;
    }

    // ------------------------------------------------------------------ ILifeBody.PlayHit

    /// <summary>Present a decided (or predicted) hit. Called on every machine; touches no game state itself (the owner
    /// of a carried bag materialises it through the director).</summary>
    public void PlayHit(in NpcHit h)
    {
        var dir = ResidentDirector.Instance;
        lastKind = h.kind;
        switch (h.kind)
        {
            case NpcHitKind.Flatten:
                BeginFlop(dir, h, WorldLifeTuning.Hits.LieSeconds, h.truck != null);
                break;
            case NpcHitKind.ShortFlop:
                BeginFlop(dir, h, WorldLifeTuning.Hits.ThrownShortLieSeconds, false);
                break;
            case NpcHitKind.Nudge:
            {
                Vector3 push = h.launch; push.y = 0f;
                if (push.sqrMagnitude < 1e-4f) push = Vector3.right * WorldLifeTuning.Hits.NudgeDistance;
                Begin(Act.Nudge, pos, pos + Vector3.ClampMagnitude(push, 1.2f));
                dir?.Say(this, ResidentLines.Nudge, ResidentLines.Hey, false);
                KickEyes(4f);
                break;
            }
            case NpcHitKind.Startle:
            {
                Vector3 away = StepOffDirection(h);
                stepSeconds = Mathf.Lerp(WorldLifeTuning.Horn.StepOffMinSeconds, WorldLifeTuning.Horn.StepOffMaxSeconds, (h.seed & 0x7F) / 127f);
                Begin(Act.Startle, pos, pos + away * 1.7f);
                dir?.Say(this, ResidentLines.Startle, ResidentLines.Hey, false);
                KickEyes(6f);
                break;
            }
            case NpcHitKind.Stumble:
            {
                Vector3 push = h.launch; push.y = 0f;
                if (push.sqrMagnitude < 1e-4f) push = -Forward * WorldLifeTuning.Hits.PlayerBumpPush;
                Begin(Act.Stumble, pos, pos + Vector3.ClampMagnitude(push, 1f));
                dir?.Say(this, ResidentLines.Stumble, ResidentLines.Hey, false);
                KickEyes(4f);
                break;
            }
            case NpcHitKind.Boop:
                Begin(Act.Boop, pos, pos);
                KickEyes(3f);
                break;
            case NpcHitKind.Dodge:
            {
                Vector3 side = h.launch; side.y = 0f;
                if (side.sqrMagnitude < 1e-4f) side = Right;
                Begin(Act.Dodge, pos, pos + side.normalized * 1.2f);
                dir?.Say(this, ResidentLines.Dodge, ResidentLines.Happy, true);
                break;
            }
            default:
                return;   // car kinds (Shunt, PullOver) and DogLeap aren't for people
        }
        dir?.OnAgentHit(this, in h);
    }

    /// <summary>Walking while holding whatever we hold (a hugged sack keeps the hug through a startle).</summary>
    PersonPose WalkPose => sack1 != null ? PersonPose.CarryTwo : sack0 != null ? PersonPose.Carry : PersonPose.Walk;

    Vector3 Forward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    Vector3 Right => Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

    Vector3 StepOffDirection(in NpcHit h)
    {
        Vector3 tv = h.truckVelocity; tv.y = 0f;
        Vector3 tpos = h.truck != null ? h.truck.transform.position : h.point - tv.normalized * 4f;
        Vector3 rel = pos - tpos; rel.y = 0f;
        if (tv.sqrMagnitude < 0.01f)
            return rel.sqrMagnitude > 1e-4f ? rel.normalized : Right;
        Vector3 perp = Vector3.Cross(Vector3.up, tv.normalized);
        return Vector3.Dot(rel, perp) >= 0f ? perp : -perp;
    }

    void Begin(Act a, Vector3 from, Vector3 to)
    {
        if (act == Act.Flop) return;   // a flop wins over anything milder
        act = a; actT = 0f; actFrom = from; actTo = to; hitPoint = from;
    }

    void BeginFlop(ResidentDirector dir, in NpcHit h, float lie, bool chase)
    {
        act = Act.Flop; flop = FlopPhase.Air; actT = phaseT = 0f; Flops++;
        hitPoint = pos;
        vel = h.launch;
        if (vel.sqrMagnitude < 0.01f) vel = Vector3.up * 4f;
        // Spin axis from the seed byte (every machine spins the same way), 540–900°/s.
        float a = (h.seed & 0x0F) / 16f * Mathf.PI * 2f, b = ((h.seed >> 4) & 0x0F) / 16f;
        spinAxis = new Vector3(Mathf.Cos(a), b - 0.5f, Mathf.Sin(a)).normalized;
        spinRate = Mathf.Lerp(WorldLifeTuning.Hits.SpinMinDegPerSec, WorldLifeTuning.Hits.SpinMaxDegPerSec, b);
        spin = Quaternion.identity;
        bounces = 0;
        groundY = pos.y;
        lieSeconds = lie;
        canChase = chase;
        chaseTruck = h.truck;
        LastAirSeconds = LastLieSeconds = LastChaseSeconds = 0f;
        avoid = Vector3.zero;
        dir?.Say(this, ResidentLines.Airborne, ResidentLines.Yell, true);
        Sfx.Play("whoosh", pos + Vector3.up, 0.6f, 1.3f);
        KickEyes(8f);
        if (rig != null && dir != null && !propHidden) { propHidden = true; dir.FlingProp(this, vel); }
    }

    // ------------------------------------------------------------------ simulation (every frame, shown or not)

    void StepAct(ResidentDirector dir, float dt)
    {
        actT += dt;
        phaseT += dt;
        switch (act)
        {
            case Act.Flop: StepFlop(dir, dt); break;
            case Act.Nudge:
            {
                float k = Mathf.Clamp01(actT / 0.3f);
                pos = Vector3.Lerp(actFrom, actTo, 1f - (1f - k) * (1f - k));
                if (actT > 1.1f) End(dir);
                break;
            }
            case Act.Startle:
            {
                if (actT < 0.45f) pos = actFrom;
                else if (actT < 0.45f + stepSeconds)
                {
                    float k = Mathf.Clamp01((actT - 0.45f) / stepSeconds);
                    pos = Vector3.Lerp(actFrom, actTo, k * k * (3f - 2f * k));
                    Vector3 d = actTo - actFrom; d.y = 0f;
                    if (d.sqrMagnitude > 1e-3f) yaw = PuppetKit.DampAngle(yaw, PuppetKit.YawOf(d), 10f, dt);
                }
                else if (actT > 0.45f + stepSeconds + 1f) End(dir);
                break;
            }
            case Act.Stumble:
            {
                float k = Mathf.Clamp01(actT / 0.4f);
                pos = Vector3.Lerp(actFrom, actTo, 1f - (1f - k) * (1f - k));
                if (actT > 1.0f) End(dir);
                break;
            }
            case Act.Boop:
                if (actT > 0.4f) End(dir);
                break;
            case Act.Dodge:
            {
                float k = Mathf.Clamp01(actT / 0.6f);
                pos = Vector3.Lerp(actFrom, actTo, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 1.0f);
                if (actT > 0.9f) { pos = actTo; End(dir); }
                break;
            }
        }
    }

    void StepFlop(ResidentDirector dir, float dt)
    {
        switch (flop)
        {
            case FlopPhase.Air:
            {
                LastAirSeconds += dt;
                vel += Physics.gravity * dt;
                Vector3 next = pos + vel * dt;
                Vector3 flat = new Vector3(vel.x, 0f, vel.z);
                float fm = flat.magnitude;
                // Walls clamp the horizontal motion (as Critter.TickAirborne does); trucks aren't walls.
                if (fm > 0.05f && Physics.Raycast(pos + Vector3.up * 0.8f, flat / fm, out var wall, fm * dt + 0.35f, WallMask, QueryTriggerInteraction.Ignore)
                    && wall.normal.y < 0.6f)
                {
                    vel = new Vector3(-vel.x * 0.25f, vel.y, -vel.z * 0.25f);
                    next = pos + Vector3.up * vel.y * dt;
                }
                if (Time.time >= nextGroundProbe)
                {
                    nextGroundProbe = Time.time + 0.06f;
                    if (Physics.Raycast(new Vector3(next.x, Mathf.Max(next.y, groundY) + 2.5f, next.z), Vector3.down, out var g, 8f, GroundMask, QueryTriggerInteraction.Ignore))
                        groundY = g.point.y;
                }
                spin = Quaternion.AngleAxis(spinRate * dt, spinAxis) * spin;
                if (vel.y < 0f && next.y <= groundY)
                {
                    next.y = groundY;
                    if (bounces == 0 && vel.y < -2f)
                    {
                        bounces++;
                        float rest = WorldLifeTuning.Hits.BounceRestitution;
                        vel = new Vector3(vel.x * 0.4f, -vel.y * rest, vel.z * 0.4f);
                        dir?.Land(this, next, true);
                    }
                    else
                    {
                        pos = next;
                        LastLandPoint = next;
                        flop = FlopPhase.Lie; phaseT = 0f;
                        dir?.Land(this, next, false);
                        break;
                    }
                }
                pos = next;
                if (pos.y < -30f) { pos = hitPoint; flop = FlopPhase.Lie; phaseT = 0f; }
                break;
            }
            case FlopPhase.Lie:
                LastLieSeconds += dt;
                if (phaseT >= lieSeconds) { flop = FlopPhase.GetUp; phaseT = 0f; }
                break;
            case FlopPhase.GetUp:
                if (phaseT >= WorldLifeTuning.Hits.GetUpSeconds)
                {
                    flop = FlopPhase.Angry; phaseT = 0f;
                    dir?.Say(this, ResidentLines.AngryFor(style.district), ResidentLines.Yell, true);
                    KickEyes(4f);
                }
                break;
            case FlopPhase.Angry:
                if (chaseTruck != null) FaceToward(chaseTruck.transform.position, dt, 8f);
                if (phaseT >= WorldLifeTuning.Hits.AngrySeconds)
                {
                    phaseT = 0f;
                    flop = canChase && chaseTruck != null && Flat(chaseTruck.transform.position - pos).magnitude < WorldLifeTuning.Hits.ChaseGiveUpDistance
                        ? FlopPhase.Chase : (canChase ? FlopPhase.GiveUp : FlopPhase.Rejoin);
                }
                break;
            case FlopPhase.Chase:
            {
                LastChaseSeconds += dt;
                if (chaseTruck == null) { flop = FlopPhase.GiveUp; phaseT = 0f; break; }
                Vector3 to = Flat(chaseTruck.transform.position - pos);
                float dist = to.magnitude;
                if (dist > WorldLifeTuning.Hits.ChaseGiveUpDistance || phaseT >= WorldLifeTuning.Hits.ChaseMaxSeconds)
                {
                    flop = FlopPhase.GiveUp; phaseT = 0f;
                    dir?.Say(this, ResidentLines.GiveUp, ResidentLines.Yell, true);
                    break;
                }
                if (dist > 3.2f) pos += to / dist * (WorldLifeTuning.Hits.ChaseSpeed * dt);   // never into the bumper
                FaceToward(chaseTruck.transform.position, dt, 12f);
                ProbeGround();
                break;
            }
            case FlopPhase.GiveUp:
                if (phaseT >= 0.7f) { flop = FlopPhase.Rejoin; phaseT = 0f; }
                break;
            case FlopPhase.Rejoin:
            {
                if (owner != AgentOwner.External) { End(dir); break; }
                Vector3 to = Flat(hitPoint - pos);
                float dist = to.magnitude;
                if (dist < 0.3f || phaseT > 12f) { End(dir); break; }
                pos += to / dist * Mathf.Min(dist, WorldLifeTuning.Walk.Jog * dt);
                FaceToward(hitPoint, dt, 10f);
                ProbeGround();
                break;
            }
        }
    }

    void ProbeGround()
    {
        if (Time.time < nextGroundProbe) return;
        nextGroundProbe = Time.time + 0.1f;
        if (Physics.Raycast(pos + Vector3.up * 1.2f, Vector3.down, out var g, 3f, GroundMask, QueryTriggerInteraction.Ignore))
            groundY = g.point.y;
        pos.y = PuppetKit.Damp(pos.y, groundY, 20f, 0.1f);
    }

    void FaceToward(Vector3 at, float dt, float sharp)
    {
        Vector3 d = Flat(at - pos);
        if (d.sqrMagnitude > 0.01f) yaw = PuppetKit.DampAngle(yaw, PuppetKit.YawOf(d), sharp, dt);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void End(ResidentDirector dir)
    {
        bool wasFlop = act == Act.Flop;
        act = Act.None;
        actT = phaseT = 0f;
        spin = Quaternion.identity;
        if (wasFlop) immuneUntil = Time.time + WorldLifeTuning.Hits.ImmuneAfterRejoinSeconds;
        if (propHidden && rig != null) rig.SetPropVisible(true);
        propHidden = false;
        dir?.OnActEnded(this);     // the owner may move the request to where we are now (walk-outs: walk back from here)
        blend = pos - reqPos;       // then catch up with the driver's line
        blend.y = 0f;
        if (blend.sqrMagnitude > 40f * 40f) blend = Vector3.zero;   // a far teleport: just snap
    }

    /// <summary>Stop any act at once (pool return, sleep).</summary>
    public void CancelAct()
    {
        if (propHidden && rig != null) rig.SetPropVisible(true);
        act = Act.None; propHidden = false; spin = Quaternion.identity;
    }

    // ------------------------------------------------------------------ animation of acts

    void AnimateAct(ResidentDirector dir, float dt)
    {
        float H = style.height;
        Vector3 c = new Vector3(0f, 0.5f * H, 0f);
        switch (act)
        {
            case Act.Flop:
                switch (flop)
                {
                    case FlopPhase.Air:
                    {
                        rig.Body(c - spin * c, spin, 0f);
                        float t = actT;
                        rig.Torso(0f, 0f);
                        rig.Head(Mathf.Sin(t * 13f) * 20f, Mathf.Sin(t * 9f) * 25f, 0f);
                        rig.Arms(-95f + Mathf.Sin(t * 23f) * 70f, 55f + Mathf.Sin(t * 17f) * 35f, -95f + Mathf.Sin(t * 21f + 1.3f) * 70f, 55f + Mathf.Sin(t * 19f) * 35f);
                        rig.Legs(Mathf.Sin(t * 19f) * 50f, Mathf.Sin(t * 19f + 2f) * 50f, 18f);
                        if (!lite) { rig.SetMouth(1f); rig.SetBrows(true, -0.6f); }
                        break;
                    }
                    case FlopPhase.Lie:
                    {
                        float lift = Mathf.Max(rig.layout.bodyD * 0.5f, rig.layout.headR * 0.9f);
                        float wig = phaseT > 0.8f ? Mathf.Sin(phaseT * 9f) * 6f : 0f;
                        rig.Body(new Vector3(0f, lift, 0f), Quaternion.Euler(-90f, 0f, 0f), 0f);
                        rig.Torso(0f, 0f);
                        rig.Head(0f, Mathf.Sin(phaseT * 3f) * 20f, 0f);
                        rig.Arms(0f, 85f + wig, 0f, 85f - wig);
                        rig.Legs(0f, 0f, 32f);
                        if (!lite) { rig.SetMouth(0.4f + 0.3f * Mathf.Sin(phaseT * 5f)); rig.SetBrows(true, -0.8f); }
                        break;
                    }
                    case FlopPhase.GetUp:
                    {
                        float k = Mathf.Clamp01(phaseT / WorldLifeTuning.Hits.GetUpSeconds);
                        float e = 1f - (1f - k) * (1f - k);
                        float lift = Mathf.Max(rig.layout.bodyD * 0.5f, rig.layout.headR * 0.9f) * (1f - e);
                        rig.Body(new Vector3(0f, lift, 0f), Quaternion.Euler(Mathf.Lerp(-90f, 0f, e), 0f, Mathf.Sin(phaseT * 20f) * 12f * (1f - k)), 0f);
                        rig.Arms(-30f, 40f, -30f, 40f);
                        rig.Legs(-20f * (1f - e), -20f * (1f - e), 10f * (1f - e));
                        if (!lite) { rig.SetMouth(0.2f); rig.SetBrows(true, 0.3f); }
                        break;
                    }
                    case FlopPhase.Angry:
                        AnimatePose(dir, dt, PersonPose.FistShake);
                        Fume(dir, dt, 3);
                        if (phaseT < 0.25f) rig.Body(new Vector3(0f, Mathf.Sin(phaseT / 0.25f * Mathf.PI) * 0.12f, 0f), Quaternion.identity, 0f);
                        break;
                    case FlopPhase.Chase:
                        AnimatePose(dir, dt, PersonPose.Jog);
                        rig.Arms(-40f, 20f, -160f + Mathf.Sin(phaseT * 28f) * 15f, 28f);
                        Fume(dir, dt, 3);
                        break;
                    case FlopPhase.GiveUp:
                    {
                        AnimatePose(dir, dt, PersonPose.FistShake);
                        float hop = Mathf.Max(0f, Mathf.Sin(phaseT / 0.35f * Mathf.PI)) * 0.25f;
                        rig.Body(new Vector3(0f, hop, 0f), Quaternion.identity, phaseT > 0.35f && phaseT < 0.5f ? -0.2f : 0f);
                        Fume(dir, dt, 2);
                        break;
                    }
                    case FlopPhase.Rejoin:
                        AnimatePose(dir, dt, PersonPose.Jog);
                        Fume(dir, dt, 1);
                        break;
                }
                break;
            case Act.Nudge:
                AnimatePose(dir, dt, PersonPose.Glare);
                if (!lite) rig.SetMouth(0.6f);
                break;
            case Act.Startle:
                if (actT < 0.45f)
                {
                    float hop = Mathf.Sin(actT / 0.45f * Mathf.PI) * WorldLifeTuning.Horn.StartleHop;
                    rig.Body(new Vector3(0f, hop, 0f), Quaternion.identity, actT < 0.1f ? -0.15f : 0f);
                    rig.Arms(-160f, 40f, -160f, 40f);
                    rig.Legs(-20f, 10f, 12f);
                    if (!lite) { rig.SetMouth(1f); rig.SetBrows(true, -0.7f); }
                }
                else if (actT < 0.45f + stepSeconds) AnimatePose(dir, dt, WalkPose);
                else AnimatePose(dir, dt, PersonPose.Glare);
                break;
            case Act.Stumble:
            {
                AnimatePose(dir, dt, WalkPose);
                float k = 1f - Mathf.Clamp01(actT / 1f);
                rig.Torso(-25f * k, Mathf.Sin(actT * 18f) * 12f * k);
                rig.Arms(-120f + Mathf.Sin(actT * 20f) * 60f, 50f, -120f + Mathf.Sin(actT * 20f + 2f) * 60f, 50f);
                if (!lite) rig.SetMouth(0.8f);
                break;
            }
            case Act.Boop:
                AnimatePose(dir, dt, PersonPose.Stand);
                rig.Body(Vector3.zero, Quaternion.identity, -0.18f * Mathf.Sin(Mathf.Clamp01(actT / 0.4f) * Mathf.PI));
                break;
            case Act.Dodge:
            {
                float k = Mathf.Clamp01(actT / 0.6f);
                rig.Body(c - Quaternion.Euler(0f, 0f, -360f * k) * c, Quaternion.Euler(0f, 0f, -360f * k), 0f);
                rig.Arms(-150f, 30f, -150f, 30f);
                rig.Legs(-30f, -30f, 15f);
                break;
            }
        }
    }

    /// <summary>Red face, brows down, little steam puffs.</summary>
    void Fume(ResidentDirector dir, float dt, int step)
    {
        if (lite) return;
        SetAnger(step);
        rig.SetBrows(true, 1f);
        steamT -= dt;
        if (steamT <= 0f && dir != null && dir.NearCamera(pos, 30f))
        {
            steamT = 0.3f;
            Fx.Burst(HeadTop, new Color(1f, 1f, 1f, 0.9f), 2, 1.4f);
        }
    }

    void AnimatePose(ResidentDirector dir, float dt, PersonPose p)
    {
        var saved = reqPose;
        reqPose = p;
        AnimatePose(dir, dt);
        reqPose = saved;
    }
}

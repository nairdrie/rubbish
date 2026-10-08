using UnityEngine;

/// <summary>What a person's body is doing when nothing has interrupted them (set every frame by whoever drives them).</summary>
public enum PersonPose : byte
{
    Walk = 0, Jog, Segway, Stand, Carry, CarryTwo, SetDown, Wave, Stretch, Glare, ArmsCrossed, FootTap,
    Bow, ClocheLift, ClocheSet, FistShake, Shrug, Panic, Talk,
    /// <summary>Indoors: not drawn, not hittable.</summary>
    Inside,
}

/// <summary>Who drives an agent's position: package D's pedestrians (External) or one of C's own scenes (walk-outs,
/// butlers, neglect witnesses, the car scenes).</summary>
public enum AgentOwner : byte { External = 0, WalkOut = 1, Butler = 2, Witness = 3, Scene = 4 }

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §4, §7, §8): one person shown on this machine. It is the <see cref="ILifeBody"/>
/// the rules hit (registered in <see cref="LifeBodies"/> while it has a puppet), and it owns the person's local
/// presentation: where they're drawn (the driver's position, plus avoidance ≤ 1.2 m and a catch-up blend after an
/// interruption), the procedural animation of its <see cref="PuppetRig"/>, and the acts a hit starts (the cartoon flop,
/// nudge, startle, stumble, dodge; see the partial ResidentAgent.Acts.cs). Plain C# object, ticked by
/// <see cref="ResidentDirector"/>; pooled, no allocation per frame.
/// </summary>
public sealed partial class ResidentAgent : ILifeBody
{
    // ------------------------------------------------------------------ identity

    public LifeId id;
    public ResidentStyle style;
    public bool night;
    public AgentOwner owner;
    /// <summary>The owning scene (a walk-out job, butler post, witness); null for External.</summary>
    public object ownerRef;
    public string stopId = "";
    public LifeId dogId;

    // ------------------------------------------------------------------ the driver's request (refreshed every frame)

    public Vector3 reqPos;
    public float reqYaw;
    public PersonPose reqPose;
    /// <summary>0..1 progress through timed poses (set-down, bow, cloche).</summary>
    public float poseT;
    public Vector3 lookAt;
    public bool hasLook;
    /// <summary>Door pop: 0 = not out yet (squashed into the door), 1 = fully out.</summary>
    public float pop = 1f;
    public int reqFrame;

    // ------------------------------------------------------------------ drawn state

    public Vector3 pos;
    public float yaw;
    public float moveSpeed;
    Vector3 lastDrawn;
    Vector3 blend;          // drawn − requested after an interruption; decays (re-convergence, §2.1)
    Vector3 avoid;          // local avoidance offset (≤ MaxAvoidOffset)
    bool placed;

    // ------------------------------------------------------------------ presentation

    public PuppetRig rig;
    public bool lite;
    float liteAcc;
    float phase;
    float talkT;
    public float nextLineAt = -99f;
    PuppetDice dice;
    int angerStep;
    float steamT;
    float idleT;
    /// <summary>Hugged sack(s) (cosmetic BuildSackVisual; owned by the walk-out job).</summary>
    public Transform sack0, sack1;
    /// <summary>The Segway (Billionaires), pooled by the director.</summary>
    public Transform segway;
    public float lastShownTime;
    /// <summary>Director bookkeeping: the LOD it wants (0 none, 1 lite, 2 full), a pending restyle, and whether the hand
    /// prop shows (walk-out carriers hug the bag instead).</summary>
    public int wantLod;
    public bool restyle, carryProp = true;

    public ResidentAgent() { }

    /// <summary>Reset for a new person (pool reuse).</summary>
    public void Init(LifeId id, in ResidentStyle st, AgentOwner owner, object ownerRef, string stopId, bool night)
    {
        this.id = id;
        style = st;
        this.owner = owner;
        this.ownerRef = ownerRef;
        this.stopId = stopId ?? "";
        this.night = night;
        dogId = LifeId.None;
        reqPose = PersonPose.Stand; poseT = 0f; hasLook = false; pop = 1f; reqFrame = -1;
        placed = false; blend = Vector3.zero; avoid = Vector3.zero; curiosityOffset = Vector3.zero;
        curiosityAt = Vector3.zero; curiosityUntil = -1f; curiositySlot = 0; moveSpeed = 0f;
        rig = null; lite = false; liteAcc = 0f; phase = 0f; talkT = 0f; nextLineAt = -99f; angerStep = 0; steamT = 0f; idleT = 0f;
        sack0 = sack1 = null; segway = null; reactUntil = 0f; wantLod = 0; restyle = false; carryProp = true;
        dice = new PuppetDice(LifeHash.Mix(id.Packed ^ 0xF1A7UL));
        ResetActs();
    }

    /// <summary>The driver's request for this frame.</summary>
    public void Request(Vector3 p, float yawDeg, PersonPose pose, float t, int frame)
    {
        reqPos = p; reqYaw = yawDeg; reqPose = pose; poseT = t; reqFrame = frame;
        if (!placed) { pos = p; yaw = yawDeg; lastDrawn = p; placed = true; }
    }

    public void Look(Vector3 at) { lookAt = at; hasLook = true; }

    float reactUntil;
    PersonPose reactPose;

    // F3 mystery bags: a small, local-only detour from the pedestrian timetable. The schedule
    // remains authoritative; this offset eases back to zero as soon as the bag goes away.
    Vector3 curiosityAt, curiosityOffset;
    float curiosityUntil;
    int curiositySlot;

    public bool Curious => owner == AgentOwner.External && Time.time < curiosityUntil;

    /// <summary>Give a nearby pedestrian a brief, presentation-only reason to gather around a buzzing bag.</summary>
    public void CuriousAbout(Vector3 at, float seconds, int slot)
    {
        if (owner != AgentOwner.External || Interrupted || reqPose == PersonPose.Inside || reqPose == PersonPose.Segway || dogId.IsValid) return;
        curiosityAt = at;
        curiositySlot = slot;
        curiosityUntil = Time.time + seconds;
    }

    /// <summary>Play a pose over whatever the driver wants for a moment (an owner's fist when their dog nearly got hit).</summary>
    public void React(PersonPose p, float seconds) { reactPose = p; reactUntil = Time.time + seconds; }

    // ------------------------------------------------------------------ ILifeBody

    public LifeId Id => id;
    public LifeBodyKind BodyKind => LifeBodyKind.Person;
    public Vector3 Position => pos;
    public Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);
    public Vector3 HalfExtents => new Vector3(0.3f, Mathf.Max(0.6f, style.height * 0.5f), 0.3f);
    public bool Hittable => rig != null && act == Act.None && Time.time >= immuneUntil && pop >= 0.95f && reqPose != PersonPose.Inside;
    public string StopId => stopId;

    /// <summary>Interrupted right now (flop, nudge, startle…): D's timetable position is not being drawn.</summary>
    public bool Interrupted => act != Act.None;
    public float Height => style.height;
    public Vector3 HeadTop => pos + Vector3.up * (style.height + 0.15f);

    // ------------------------------------------------------------------ per-frame update (called by the director)

    /// <summary>Move + act. Every frame for every agent (cheap), even without a puppet, so acts finish off-screen.</summary>
    public void Step(ResidentDirector dir, float dt, double now)
    {
        if (act != Act.None) StepAct(dir, dt);
        else
        {
            // Re-convergence: an interruption leaves the drawn body off the driver's line; it catches up at up to
            // 1.4× walking speed (§7.3 / §8.1 "the drawn position catches up").
            float bm = blend.magnitude;
            if (bm > 0.001f)
            {
                float rate = Mathf.Max(1.1f, ResidentLook.WalkSpeed(style.archetype) * (WorldLifeTuning.Traffic.CatchUpFactor - 1f) + 1.0f);
                blend = bm <= rate * dt ? Vector3.zero : blend * ((bm - rate * dt) / bm);
            }
            if (rig != null && reqPose != PersonPose.Inside) Avoid(dir, dt);
            else avoid = Vector3.zero;
            if (Curious)
            {
                float angle = (curiositySlot * 137.5f + 35f) * Mathf.Deg2Rad;
                Vector3 ring = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.55f;
                Vector3 toward = curiosityAt + ring - reqPos;
                toward.y = 0f;
                curiosityOffset = Vector3.MoveTowards(curiosityOffset, Vector3.ClampMagnitude(toward, 3.5f), dt * 1.8f);
                Look(curiosityAt);
            }
            else curiosityOffset = Vector3.MoveTowards(curiosityOffset, Vector3.zero, dt * 2.4f);
            pos = reqPos + blend + avoid + curiosityOffset;
            float yawSharp = reqPose == PersonPose.Segway ? 4f : 9f;
            Vector3 face = curiosityAt - pos; face.y = 0f;
            float wantYaw = Curious && face.sqrMagnitude > 0.01f ? PuppetKit.YawOf(face) : reqYaw;
            yaw = PuppetKit.DampAngle(yaw, wantYaw, yawSharp, dt);
        }
        if (dt > 1e-5f)
        {
            Vector3 d = pos - lastDrawn; d.y = 0f;
            float inst = d.magnitude / dt;
            if (inst > 12f) inst = moveSpeed;   // teleport / snap
            moveSpeed = PuppetKit.Damp(moveSpeed, inst, 10f, dt);
        }
        lastDrawn = pos;
    }

    /// <summary>Local avoidance (presentation, may diverge between machines, §2.1): sidestep players and trucks standing
    /// on the path, never more than <see cref="WorldLifeTuning.Walk.MaxAvoidOffset"/>.</summary>
    void Avoid(ResidentDirector dir, float dt)
    {
        Vector3 basePos = reqPos + blend;
        Vector3 push = Vector3.zero;
        var players = dir.Players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null || !p.isActiveAndEnabled || p.IsDriving) continue;
            Vector3 d = basePos + avoid - p.transform.position; d.y = 0f;
            float m = d.magnitude;
            if (m < 0.95f && m > 1e-3f) push += d / m * (0.95f - m);
        }
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var tr = trucks[i];
            if (tr == null) continue;
            Vector3 l = Quaternion.Inverse(tr.transform.rotation) * (basePos + avoid - tr.transform.position);
            var box = tr.ModelBodyBoxMetres;
            float ex = box.extents.x + 0.55f;
            if (l.z < box.min.z - 0.5f || l.z > box.max.z + 0.5f || Mathf.Abs(l.x) > ex) continue;
            float side = l.x >= 0f ? 1f : -1f;
            push += tr.transform.right * side * (ex - Mathf.Abs(l.x) + 0.1f) * 2f;
        }
        if (push.sqrMagnitude > 1e-6f) avoid += push * Mathf.Min(1f, 6f * dt);
        else avoid = Vector3.MoveTowards(avoid, Vector3.zero, 0.8f * dt);
        float max = WorldLifeTuning.Walk.MaxAvoidOffset;
        if (avoid.sqrMagnitude > max * max) avoid = avoid.normalized * max;
    }

    /// <summary>Draw the puppet (full: every frame; lite: 15 Hz).</summary>
    public void Animate(ResidentDirector dir, float dt)
    {
        if (rig == null) return;
        bool inside = reqPose == PersonPose.Inside && act == Act.None;
        rig.SetVisible(!inside);
        if (inside) return;
        if (lite)
        {
            liteAcc += dt;
            if (liteAcc < 1f / WorldLifeTuning.Lod.LiteHz) return;
            dt = liteAcc;
            liteAcc = 0f;
        }
        rig.Place(pos, yaw);
        rig.juice.localScale = Vector3.one * PopCurve(pop);
        if (act != Act.None) AnimateAct(dir, dt);
        else if (Time.time < reactUntil) AnimatePose(dir, dt, reactPose);
        else AnimatePose(dir, dt);
        if (!lite)
        {
            rig.TickEyes(dt, ref dice);
            if (talkT > 0f) { talkT -= dt; rig.SetMouth(0.25f + 0.35f * Mathf.Abs(Mathf.Sin(talkT * 22f))); }
        }
        if (segway != null) segway.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
    }

    static float PopCurve(float p)
    {
        if (p >= 1f) return 1f;
        if (p <= 0f) return 0.001f;
        // overshoot: 0 → 1.15 → 1
        return p < 0.7f ? Mathf.Lerp(0.05f, 1.15f, p / 0.7f) : Mathf.Lerp(1.15f, 1f, (p - 0.7f) / 0.3f);
    }

    void AnimatePose(ResidentDirector dir, float dt)
    {
        float H = style.height;
        var pose = reqPose;
        bool moving = moveSpeed > 0.15f;
        float spd = moveSpeed;
        float stride = pose == PersonPose.Jog || pose == PersonPose.Panic ? 0.85f * H : 0.5f * H;
        if (moving) phase += spd * dt / stride * Mathf.PI;
        else phase = Mathf.MoveTowards(phase, Mathf.Round(phase / Mathf.PI) * Mathf.PI, dt * 4f);
        float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
        float walkK = Mathf.Clamp01(spd / 1.2f);
        idleT += dt;

        // Defaults: a waddle. Legs swing, arms counter-swing, the body bobs and rolls (the waddle is the rhythm).
        float legAmp = pose == PersonPose.Jog || pose == PersonPose.Panic ? 42f : 28f;
        float lx = s * legAmp * walkK, rx = -s * legAmp * walkK;
        float armAmp = 24f * walkK;
        float alx = -s * armAmp, arx = s * armAmp, alz = 10f, arz = 10f, aly = 0f, ary = 0f;
        float bob = Mathf.Abs(c) * 0.035f * H * walkK;
        float roll = s * 5f * walkK;
        float pitch = 0f, headP = 0f, headY = 0f, headR = 0f, squash = 0f;
        bool brows = false; float browAngry = 0f; int anger = 0;
        float mouth = -1f;

        switch (pose)
        {
            case PersonPose.Jog:
                pitch = 12f; alx = -s * 60f * walkK - 25f; arx = s * 60f * walkK - 25f; alz = arz = 18f; bob *= 1.6f;
                break;
            case PersonPose.Panic:
                pitch = 14f; alx = -150f + s * 30f; arx = -150f - s * 30f; alz = arz = 30f + c * 15f; bob *= 1.8f; mouth = 1f;
                brows = true; browAngry = -0.6f;
                break;
            case PersonPose.Segway:
                lx = rx = 0f; alx = -35f; arx = -35f; alz = arz = 12f; bob = 0.004f * Mathf.Sin(idleT * 9f); roll = 0f; pitch = 4f + spd * 2f;
                break;
            case PersonPose.Carry:
                // The bear hug: arms forward around the sack, an extra waddle.
                alx = arx = -62f; alz = arz = 24f; aly = 0f; ary = 0f; roll = s * 9f * walkK; pitch = -6f; bob *= 1.2f;
                break;
            case PersonPose.CarryTwo:
                alx = -12f - s * 6f * walkK; arx = -12f + s * 6f * walkK; alz = arz = 28f; roll = s * 12f * walkK; bob *= 1.3f;
                break;
            case PersonPose.SetDown:
            {
                float k = Mathf.Sin(Mathf.Clamp01(poseT) * Mathf.PI);
                pitch = 55f * k; alx = arx = Mathf.Lerp(-62f, -80f, k); alz = arz = 22f; lx = rx = -12f * k; headP = -20f * k;
                break;
            }
            case PersonPose.Wave:
                arx = -165f; arz = 35f + Mathf.Sin(idleT * 14f) * 25f; alx = 5f; alz = 8f; headR = Mathf.Sin(idleT * 7f) * 6f; mouth = 0.6f;
                break;
            case PersonPose.Stretch:
            {
                float k = Mathf.Sin(Mathf.Clamp01(poseT) * Mathf.PI);
                alx = arx = Mathf.Lerp(0f, -170f, k); alz = arz = Mathf.Lerp(10f, 25f, k); pitch = -12f * k; mouth = 0.8f * k; squash = 0.05f * k;
                break;
            }
            case PersonPose.Glare:
            case PersonPose.ArmsCrossed:
            case PersonPose.FootTap:
                alx = -72f; arx = -78f; alz = -38f; arz = -38f; aly = 25f; ary = -25f;
                brows = true; browAngry = pose == PersonPose.Glare ? 0.8f : 0.5f; anger = pose == PersonPose.Glare ? 1 : 0;
                if (pose == PersonPose.FootTap) rx = -14f * Mathf.Max(0f, Mathf.Sin(idleT * 11f));
                break;
            case PersonPose.Bow:
            {
                float k = Mathf.Sin(Mathf.Clamp01(poseT) * Mathf.PI);
                pitch = 42f * k; headP = 8f * k; alx = -10f; alz = 6f; arx = -55f; arz = 10f;
                break;
            }
            case PersonPose.ClocheLift:
            case PersonPose.ClocheSet:
                alx = -62f; alz = 5f; arx = -62f; arz = 4f; ary = -10f; pitch = pose == PersonPose.ClocheSet ? 30f * Mathf.Sin(Mathf.Clamp01(poseT) * Mathf.PI) : 0f;
                break;
            case PersonPose.FistShake:
                arx = -150f + Mathf.Sin(idleT * 30f) * 18f; arz = 30f; alx = -40f; alz = 55f; brows = true; browAngry = 1f; anger = 2; mouth = 0.5f;
                headP = Mathf.Sin(idleT * 30f) * 4f;
                break;
            case PersonPose.Shrug:
                alx = arx = -25f; alz = arz = 55f; headR = 12f; mouth = 0.3f; squash = -0.03f;
                break;
            case PersonPose.Talk:
                alx = -20f + Mathf.Sin(idleT * 5f) * 15f; arx = -30f - Mathf.Sin(idleT * 4f) * 25f; alz = arz = 14f;
                break;
            case PersonPose.Stand:
                squash = 0.012f * Mathf.Sin(idleT * 2.2f);
                break;
        }

        // The butler's platter stays level: his right arm holds it out whatever the body does.
        if (style.prop == PuppetProp.Platter && sack0 == null) { arx = -ResidentLook.PlatterTilt - pitch; arz = 4f; ary = 0f; }

        // Look at something (the truck, the camera, the pile) with the head.
        if (hasLook)
        {
            Vector3 d = lookAt - pos; d.y = 0f;
            if (d.sqrMagnitude > 0.01f)
            {
                float want = Mathf.DeltaAngle(yaw, PuppetKit.YawOf(d));
                headY = Mathf.Clamp(want, -70f, 70f);
            }
        }

        rig.Body(new Vector3(0f, bob, 0f), Quaternion.Euler(0f, 0f, roll), squash);
        rig.Torso(pitch, 0f);
        rig.Head(headP, headY, headR);
        rig.Arms(alx, alz, arx, arz, aly, ary);
        rig.Legs(lx, rx);
        if (!lite)
        {
            SetAnger(anger);
            rig.SetBrows(brows, browAngry);
            if (talkT <= 0f) rig.SetMouth(mouth < 0f ? 0f : mouth);
        }
    }

    void SetAnger(int step)
    {
        if (step == angerStep) return;
        angerStep = step;
        rig.SetAngry(step);
    }

    // ------------------------------------------------------------------ talk

    /// <summary>Open the mouth for a moment (a chatter line).</summary>
    public void Talk(float seconds = 0.9f) { talkT = Mathf.Max(talkT, seconds); }

    public void KickEyes(float s)
    {
        if (rig != null) rig.KickEyes(s, ref dice);
    }

    internal ref PuppetDice Dice => ref dice;

    // ------------------------------------------------------------------ pool

    /// <summary>Forget the puppet (the director returns it to the pool).</summary>
    public void DetachRig()
    {
        rig = null;
        angerStep = 0;
    }

    /// <summary>A fresh puppet was attached: snap it to the drawn pose.</summary>
    public void OnRigAttached()
    {
        angerStep = 0;
        if (rig == null) return;
        rig.Place(pos, yaw);
        rig.SetVisible(reqPose != PersonPose.Inside);
    }
}

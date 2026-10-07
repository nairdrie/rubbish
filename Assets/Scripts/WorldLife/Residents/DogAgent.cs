using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1, §8.2): a dog on a leash, shown beside its owner. Dogs are NEVER hit:
/// inside the truck box they do a scripted leap (a 1.2 m arc sideways, a yelp, a wagging landing) and the owner shakes a
/// fist ("YOU NEARLY HIT BISCUIT!"). If the owner is flattened the dog runs barking circles, then sits beside them until
/// they get up. They sniff around when the owner stops and bark at raccoons. Presentation only; pooled; the
/// <see cref="ILifeBody"/> the rules see (kind Dog) while shown.
/// </summary>
public sealed class DogAgent : ILifeBody
{
    public LifeId id;
    public DogStyle style;
    public ResidentAgent owner;
    public DogRig rig;
    public bool lite;
    public Vector3 pos;
    public float yaw;
    float moveSpeed, phase, wagT, liteAcc;
    Vector3 lastPos, wander;
    float sniffUntil, nextSniff, barkT, nextRaccoonCheck;
    PuppetDice dice;
    public int reqFrame;

    public enum DogAct : byte { None, Leap, Circle, Sit }
    public DogAct act;
    float actT, immuneUntil;
    Vector3 from, to;
    public int Leaps { get; private set; }

    public void Init(LifeId id, in DogStyle st, ResidentAgent owner)
    {
        this.id = id; style = st; this.owner = owner;
        rig = null; lite = false; act = DogAct.None; actT = 0f; immuneUntil = 0f; moveSpeed = 0f; phase = 0f; Leaps = 0;
        dice = new PuppetDice(LifeHash.Mix(id.Packed ^ 0xD066UL));
        pos = owner != null ? owner.pos + owner.Rotation * new Vector3(0.6f, 0f, 0.4f) : Vector3.zero;
        lastPos = pos;
        yaw = owner != null ? owner.yaw : 0f;
        nextSniff = Time.time + dice.Range(4f, 9f);
    }

    // ------------------------------------------------------------------ ILifeBody

    public LifeId Id => id;
    public LifeBodyKind BodyKind => LifeBodyKind.Dog;
    public Vector3 Position => pos;
    public Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);
    public Vector3 HalfExtents => new Vector3(0.18f, 0.25f, 0.35f);
    public bool Hittable => rig != null && act == DogAct.None && Time.time >= immuneUntil;
    public string StopId => owner != null ? owner.stopId : "";

    public void PlayHit(in NpcHit h)
    {
        if (h.kind != NpcHitKind.DogLeap && h.kind != NpcHitKind.Flatten && h.kind != NpcHitKind.Nudge) return;
        // Always leap clear, sideways away from the truck's line (§8.2).
        Vector3 tv = h.truckVelocity; tv.y = 0f;
        Vector3 side = tv.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, tv.normalized) : Vector3.right;
        Vector3 tp = h.truck != null ? h.truck.transform.position : h.point - tv.normalized * 3f;
        if (Vector3.Dot(pos - tp, side) < 0f) side = -side;
        act = DogAct.Leap; actT = 0f; from = pos; to = pos + side * 2.2f;
        Leaps++;
        var dir = ResidentDirector.Instance;
        KnockSfx.Play(WorldLifeIds.Sfx.Yelp, "squeak", pos + Vector3.up * 0.4f, 0.8f, 0.75f * style.barkPitch);
        if (dir != null && dir.NearCamera(pos, 45f)) GameHUD.FloatingText(pos + Vector3.up * 0.9f, "YIP!", ResidentLines.Hey, 0.8f);
        if (owner != null)
        {
            owner.React(PersonPose.FistShake, 2.2f);
            dir?.Say(owner, ResidentLines.NearlyHit(style.name), ResidentLines.Yell, true);
        }
    }

    public void Bark(ResidentDirector dir)
    {
        if (barkT > 0f) return;
        barkT = 0.6f;
        KnockSfx.Play(WorldLifeIds.Sfx.Bark, "squeak", pos + Vector3.up * 0.4f, 0.7f, 0.8f * style.barkPitch);
        if (dir != null && dir.NearCamera(pos, 35f) && dice.Value < 0.5f) GameHUD.FloatingText(pos + Vector3.up * 0.8f, dice.Pick(ResidentLines.Woof), ResidentLines.Chat, 0.7f);
    }

    // ------------------------------------------------------------------ step

    public void Step(ResidentDirector dir, float dt)
    {
        if (barkT > 0f) barkT -= dt;
        actT += dt;
        switch (act)
        {
            case DogAct.Leap:
            {
                float k = Mathf.Clamp01(actT / 0.7f);
                pos = Vector3.Lerp(from, to, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * WorldLifeTuning.Hits.DogLeapHeight);
                if (k >= 1f) { pos = to; act = DogAct.None; immuneUntil = Time.time + 1.5f; wagT = 1.5f; }
                break;
            }
            case DogAct.Circle:
            case DogAct.Sit:
            {
                if (owner == null || owner.act != ResidentAgent.Act.Flop) { act = DogAct.None; break; }
                Vector3 c = owner.pos;
                if (act == DogAct.Circle)
                {
                    float a = actT * 4.5f;
                    Vector3 p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 1.2f;
                    yaw = PuppetKit.YawOf(new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)));
                    pos = new Vector3(p.x, c.y, p.z);
                    if ((int)(actT * 2.5f) != (int)((actT - dt) * 2.5f)) Bark(dir);
                    if (actT > 3f) { act = DogAct.Sit; actT = 0f; }
                }
                else
                {
                    pos = Vector3.MoveTowards(pos, c + new Vector3(0.7f, 0f, 0.2f), 2f * dt);
                    Vector3 d = c - pos; d.y = 0f;
                    if (d.sqrMagnitude > 0.01f) yaw = PuppetKit.DampAngle(yaw, PuppetKit.YawOf(d), 6f, dt);
                }
                break;
            }
            default:
                Follow(dir, dt);
                break;
        }
        if (dt > 1e-5f)
        {
            Vector3 d = pos - lastPos; d.y = 0f;
            float inst = Mathf.Min(d.magnitude / dt, 8f);
            moveSpeed = PuppetKit.Damp(moveSpeed, inst, 10f, dt);
        }
        lastPos = pos;
    }

    void Follow(ResidentDirector dir, float dt)
    {
        if (owner == null) return;
        if (owner.act == ResidentAgent.Act.Flop && owner.flop != ResidentAgent.FlopPhase.Air)
        {
            act = DogAct.Circle; actT = 0f; Bark(dir);
            return;
        }
        float now = Time.time;
        bool ownerStill = owner.moveSpeed < 0.2f;
        if (now >= nextSniff) { sniffUntil = now + dice.Range(1.2f, 2.2f); nextSniff = now + dice.Range(6f, 11f); wander = new Vector3(dice.Range(-1f, 1f), 0f, dice.Range(0.2f, 1.2f)); }
        Quaternion ownerRot = owner.Rotation;
        Vector3 target = owner.pos + ownerRot * (new Vector3(0.65f, 0f, 0.55f) + (ownerStill || now < sniffUntil ? wander : Vector3.zero));
        target.y = owner.pos.y;
        Vector3 to = target - pos; to.y = 0f;
        float dist = to.magnitude;
        bool sniffing = now < sniffUntil && dist < 1.8f;
        float speed = sniffing ? 0f : Mathf.Clamp(dist * 2.2f, 0f, owner.moveSpeed * 1.4f + 1.2f);
        if (dist > 0.05f && !sniffing) pos += to / dist * Mathf.Min(dist, speed * dt);
        pos.y = PuppetKit.Damp(pos.y, owner.pos.y, 10f, dt);
        if (dist > 2.6f) pos = target - to / dist * 2.6f;   // the leash
        Vector3 face = sniffing ? to : (owner.moveSpeed > 0.2f ? ownerRot * Vector3.forward : to);
        face.y = 0f;
        if (face.sqrMagnitude > 0.01f) yaw = PuppetKit.DampAngle(yaw, PuppetKit.YawOf(face), 7f, dt);

        if (now >= nextRaccoonCheck && rig != null)
        {
            nextRaccoonCheck = now + 1f;
            var crit = Critter.All;
            for (int i = 0; i < crit.Count; i++)
            {
                var c = crit[i];
                if (c is Raccoon && c != null && (c.transform.position - pos).sqrMagnitude < 8f * 8f) { Bark(dir); wagT = 0f; break; }
            }
        }
    }

    // ------------------------------------------------------------------ animate

    public void Animate(float dt)
    {
        if (rig == null) return;
        if (lite)
        {
            liteAcc += dt;
            if (liteAcc < 1f / WorldLifeTuning.Lod.LiteHz) return;
            dt = liteAcc; liteAcc = 0f;
        }
        rig.root.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        float spd = moveSpeed;
        phase += spd * dt / Mathf.Max(0.12f, rig.legLen * 2.2f) * Mathf.PI;
        float k = Mathf.Clamp01(spd / 1.5f);
        float s = Mathf.Sin(phase);
        float amp = 38f * k;
        rig.legs[0].localRotation = Quaternion.Euler(s * amp, 0f, 0f);
        rig.legs[3].localRotation = Quaternion.Euler(s * amp, 0f, 0f);
        rig.legs[1].localRotation = Quaternion.Euler(-s * amp, 0f, 0f);
        rig.legs[2].localRotation = Quaternion.Euler(-s * amp, 0f, 0f);
        wagT = Mathf.Max(0f, wagT - dt);
        float wag = Mathf.Sin(Time.time * (wagT > 0f ? 30f : 14f)) * (wagT > 0f ? 45f : 25f);
        rig.tailPivot.localRotation = Quaternion.Euler(0f, wag, 0f);
        bool sniff = act == DogAct.None && spd < 0.2f;
        float headPitch = sniff ? 28f + Mathf.Sin(Time.time * 18f) * 4f : act == DogAct.Circle ? -15f : -4f;
        if (barkT > 0.35f) headPitch = -25f;
        rig.headPivot.localRotation = Quaternion.Euler(headPitch, 0f, 0f);
        float bob = Mathf.Abs(Mathf.Cos(phase)) * 0.025f * k;
        if (act == DogAct.Sit) { rig.visual.localPosition = Vector3.zero; rig.bodyPivot.localRotation = Quaternion.Euler(-28f, 0f, 0f); }
        else
        {
            rig.bodyPivot.localRotation = act == DogAct.Leap ? Quaternion.Euler(-15f, 0f, Mathf.Sin(actT * 12f) * 20f) : Quaternion.identity;
            rig.visual.localPosition = new Vector3(0f, bob, 0f);
        }
        // The leash: owner's hand → collar.
        bool leash = !lite && owner != null && owner.rig != null && !owner.lite && owner.act == ResidentAgent.Act.None;
        if (rig.leash.enabled != leash) rig.leash.enabled = leash;
        if (leash)
        {
            rig.leash.SetPosition(0, owner.rig.handR.position);
            rig.leash.SetPosition(1, rig.collar.position);
        }
    }
}

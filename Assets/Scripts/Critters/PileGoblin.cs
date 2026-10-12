using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The pile squatter (look + lines come from <see cref="SquatterProfile"/>, default "Pile Goblin"). Any time of day a
/// neglected curb pile may get one napping on top of it: snoring Z's, a snot bubble, nightcap and all.
///
///   Sleeping  on top of the heap (sinks as the heap shrinks). Wander within ~2 m and it stirs: one eye opens, "...?".
///   Waking    grab a bag from its pile / bump it / linger → POP goes the bubble, "WHO TOUCHED MY PILE?!".
///   Chase     grumpy waddle-run after you with its plunger raised (4 m/s: sprint to escape).
///   Bonk      THWACK: big knockback, you drop your bag. It gloats, then goes back to its nap.
///   Grumpy    woken by a honk (or too tired to chase again): shakes its fist, grumbles, back to sleep.
///   Evicted   the pile got cleared: tantrum, then it stomps off muttering and vanishes.
/// Gives up when you drive off, get far away, or after a while. Repellent: sniffs you, "EW!", goes back to bed.
/// </summary>
public class PileGoblin : Critter
{
    public enum State { Sleeping, Stirring, Waking, Jumping, Chase, Windup, Bonk, Gloat, Return, Grumpy, Stunned, Evicted, Leaving }

    public const float ChaseSpeed = 4.0f;
    public const float BumpDistance = 0.85f;
    public const float StirDistance = 2.3f;

    public SquatterProfile Look { get; private set; }
    public State Current { get; private set; } = State.Sleeping;
    public override string DisplayName => Look != null ? Look.displayName : "Pile Goblin";

    /// <summary>World point on the ground under the pile it calls home.</summary>
    public Vector3 Nest { get; private set; }

    float stateT, stateDur;
    State afterJump;
    Vector3 jumpFrom, jumpTo;
    float jumpHeight;
    float seatY;
    bool seated;
    float lastPileCheck = -99f;
    int pileBags;
    float chaseStartedAt;
    int bonks;
    float stirTimer;
    float nextSnore, nextMutter, nextStomp, nextGrumble;
    float breath;
    float bubbleT;
    int breathCount;
    bool announced;
    /// <summary>Who woke it (host brain / solo): the one player the "You woke it!" warning is for.</summary>
    NightTarget waker;
    CritterZs zs;

    float animHip, animLean, animMouth, animSpin, animRoll;
    Vector3 lastPos;

    static readonly List<TrashBag> tmpBags = new List<TrashBag>();

    protected override bool GroundSnap => !seated && Current != State.Jumping;

    // =====================================================================
    // Spawning
    // =====================================================================

    /// <summary>Spawn a sleeping squatter on a stop's pile (or on the ground at pos when stop is null).</summary>
    public static PileGoblin Spawn(PickupStop stop, Vector3 fallbackPos, SquatterProfile look = null)
    {
        look ??= SquatterProfile.Current;
        var go = new GameObject(look.displayName);
        Vector3 nest = fallbackPos;
        Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        if (stop != null)
        {
            CritterWorld.BagsAt(stop, tmpBags);
            nest = CritterWorld.PileCenter(stop, tmpBags);
            nest.y = CritterWorld.GroundY(nest, stop.transform.position.y);
            Vector3 toRoad = CritterWorld.Flat(stop.transform.forward);
            if (toRoad.sqrMagnitude > 0.01f) rot = Quaternion.LookRotation(toRoad.normalized, Vector3.up) * Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f);
        }
        go.transform.SetPositionAndRotation(nest, rot);
        var g = go.AddComponent<PileGoblin>();
        g.Init(stop, nest, look);
        return g;
    }

    /// <summary>Guest copy of the host's goblin <paramref name="id"/> (F4 NightNet).</summary>
    public static PileGoblin SpawnPuppet(ushort id, Vector3 pos, float yaw, byte state, byte flags)
    {
        var look = SquatterProfile.Current;
        var go = new GameObject(look.displayName + " (net)");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        var g = go.AddComponent<PileGoblin>();
        g.MakePuppet(id);
        g.Init(null, pos, look);
        g.NetSnapshot(pos, yaw, 0.2f);
        g.ApplyNetState(state, flags);
        return g;
    }

    public override byte NetState => (byte)Current;
    public override byte NetFlags => (byte)(seated ? 1 : 0);

    public override void ApplyNetState(byte state, byte flags)
    {
        seated = (flags & 1) != 0;
        if (state <= (byte)State.Leaving && (State)state != Current) Enter((State)state);
    }

    protected override void TickPuppet(float dt)
    {
        stateT += dt;
        Animate(dt);
    }

    void Init(PickupStop stop, Vector3 nest, SquatterProfile look)
    {
        Home = stop;
        Nest = nest;
        Look = look;
        stepRadius = 0.26f;
        turnRate = 420f;
        Rig = CritterLook.BuildGoblin(gameObject, look);
        if (Rig.zAnchor != null)
        {
            zs = Rig.zAnchor.gameObject.AddComponent<CritterZs>();
            zs.interval = 1.2f;
        }
        seated = true;
        seatY = SeatHeight();
        var p = transform.position; p.y = seatY; transform.position = p;
        lastPos = p;
        Juice.PopIn(Rig.juice, 0.35f);
        Enter(State.Sleeping);
    }

    float SeatHeight()
    {
        if (Home == null) return CritterWorld.GroundY(Nest, Nest.y);
        pileBags = CritterWorld.BagsAt(Home, tmpBags);
        float ground = CritterWorld.GroundY(Nest, Nest.y);
        if (pileBags == 0) return ground;
        return Mathf.Max(ground, CritterWorld.PileTop(tmpBags, ground) - 0.3f); // nestled into the heap
    }

    /// <summary>Bags left in its pile (records, or the ones lying there).</summary>
    int PileCount()
    {
        if (Home == null) return 1;
        return Mathf.Max(CritterWorld.BagsAt(Home, tmpBags), CritterWorld.CurbBagCount(Home));
    }

    // =====================================================================
    // State machine
    // =====================================================================

    void Enter(State s)
    {
        Current = s;
        stateT = 0f;
        Vector3 head = Rig.head != null ? Rig.head.position : transform.position + Vector3.up * 0.8f;
        bool asleep = s == State.Sleeping || s == State.Stirring;
        if (zs != null) zs.Emitting = s == State.Sleeping;
        Rig.CloseEyes(asleep);
        if (s == State.Stirring && Rig.eyes.Count > 0) Rig.eyes[0].Closed = false; // one eye pops open

        switch (s)
        {
            case State.Sleeping:
                stateDur = 0f;
                stirTimer = 0f;
                announced = false; // a fresh nap: whoever wakes it next (maybe someone else) gets their own warning
                waker = default;
                break;
            case State.Stirring:
                stateDur = 1.6f;
                Sfx.Play(Look.snoreSfx, head, 0.6f, 1.5f);
                GameHUD.FloatingText(head + Vector3.up * 0.5f, "...?", new Color(0.8f, 0.9f, 1f), 0.9f);
                Rig.KickEyes(2f);
                break;
            case State.Waking:
                stateDur = 0.9f;
                PopBubble(true);
                Sfx.Play(Look.angrySfx, head, 1f, Random.Range(0.95f, 1.1f));
                GameHUD.FloatingText(head + Vector3.up * 0.6f, Pick(Look.wakeLines), new Color(1f, 0.35f, 0.25f), 1.4f);
                Juice.Punch(Rig.juice, 0.4f, 0.35f);
                Rig.KickEyes(7f);
                Fx.Dust(head + Vector3.up * 0.2f, 0.6f); // steam out of the ears
                if (!announced && !NetPuppet)
                {
                    announced = true;
                    WarnWaker();
                }
                break;
            case State.Jumping:
                stateDur = 0.45f;
                Sfx.Play("whoosh", transform.position + Vector3.up * 0.5f, 0.5f, 0.9f);
                break;
            case State.Chase:
                if (chaseStartedAt <= 0f) chaseStartedAt = Time.time;
                break;
            case State.Windup:
                stateDur = 0.32f;
                Sfx.Play("whoosh", head, 0.6f, 0.7f);
                GameHUD.FloatingText(head + Vector3.up * 0.5f, "HNNNG", new Color(1f, 0.55f, 0.3f), 0.8f);
                break;
            case State.Bonk:
                stateDur = 0.28f;
                break;
            case State.Gloat:
                stateDur = 1.3f;
                GameHUD.FloatingText(head + Vector3.up * 0.5f, Pick(Look.gloatLines), new Color(0.6f, 1f, 0.4f), 1.1f);
                Sfx.Play(Look.grumbleSfx, head, 0.8f, 1.3f);
                break;
            case State.Return:
                chaseStartedAt = 0f;
                break;
            case State.Grumpy:
                stateDur = 2.2f;
                PopBubble(false);
                Sfx.Play(Look.angrySfx, head, 0.8f, 1.2f);
                GameHUD.FloatingText(head + Vector3.up * 0.6f, Random.value < 0.5f ? "KEEP IT DOWN!" : "*shakes fist*", new Color(1f, 0.5f, 0.3f), 1.1f);
                Rig.KickEyes(4f);
                break;
            case State.Stunned:
                stateDur = 1.8f;
                DizzyStars.Attach(Rig.head, stateDur, 0.28f, 0.3f);
                Sfx.Play(Look.grumbleSfx, head, 0.7f, 0.8f);
                break;
            case State.Evicted:
                stateDur = 1.7f;
                seated = false; // the heap is gone: it's standing on the curb now (ground snap takes over)
                PopBubble(false);
                Sfx.Play(Look.angrySfx, head, 1f, 0.85f);
                GameHUD.FloatingText(head + Vector3.up * 0.6f, Pick(Look.evictLines), new Color(1f, 0.4f, 0.3f), 1.3f);
                // Everyone near the heap hears about it, not just whoever happens to be the goblin's nearest player.
                if (Home != null && !NetPuppet)
                    NightNet.HostToastNear(transform.position, 40f, $"The {DisplayName} got evicted from {Home.address}! Clean streets win.", ToastStyle.Good);
                break;
            case State.Leaving:
                stateDur = Random.Range(5f, 8f);
                break;
        }
    }

    protected override void Tick(float dt)
    {
        stateT += dt;
        switch (Current)
        {
            case State.Sleeping: TickSleeping(dt); break;
            case State.Stirring: TickStirring(dt); break;
            case State.Waking: TickWaking(dt); break;
            case State.Jumping: TickJumping(dt); break;
            case State.Chase: TickChase(dt); break;
            case State.Windup: TickWindup(dt); break;
            case State.Bonk: TickBonk(dt); break;
            case State.Gloat: TickGloat(dt); break;
            case State.Return: TickReturn(dt); break;
            case State.Grumpy: TickGrumpy(dt); break;
            case State.Stunned: TickStunned(dt); break;
            case State.Evicted: TickEvicted(dt); break;
            case State.Leaving: TickLeaving(dt); break;
        }
        Animate(dt);
    }

    void UpdateSeat(float dt)
    {
        if (Time.time - lastPileCheck > 0.4f)
        {
            lastPileCheck = Time.time;
            seatY = SeatHeight();
        }
        var p = transform.position;
        if (p.y > seatY + 0.01f)
        {
            p.y = Mathf.MoveTowards(p.y, seatY, dt * 3.5f); // flop down as the pile shrinks
            if (Mathf.Abs(p.y - seatY) < 0.02f) { Juice.Squash(Rig.juice, 0.2f, 0.2f); Fx.Dust(p, 0.3f); }
        }
        else p.y = Mathf.MoveTowards(p.y, seatY, dt * 1.5f);
        transform.position = p;
    }

    bool PileGone => Home != null && PileCount() == 0;

    void TickSleeping(float dt)
    {
        UpdateSeat(dt);
        if (PileGone) { WakeAndLeave(); return; }
        if (playerOnFoot && playerDist < BumpDistance)
        {
            GameHUD.FloatingText(transform.position + Vector3.up * 1f, "*bump*", Color.white, 0.8f);
            Wake();
            return;
        }
        if (playerOnFoot && playerDist < StirDistance)
        {
            stirTimer += dt;
            if (stirTimer > 0.6f) Enter(State.Stirring);
        }
        else stirTimer = Mathf.Max(0f, stirTimer - dt);

        // Snore on the inhale.
        if (Time.time >= nextSnore)
        {
            nextSnore = Time.time + 3f;
            if (playerDist < 28f) Sfx.Play(Look.snoreSfx, Rig.head.position, Mathf.Lerp(0.8f, 0.25f, playerDist / 28f), Random.Range(0.9f, 1.1f));
        }
    }

    void TickStirring(float dt)
    {
        UpdateSeat(dt);
        if (PileGone) { WakeAndLeave(); return; }
        if (playerOnFoot && playerDist < BumpDistance) { Wake(); return; }
        if (stateT >= stateDur)
        {
            if (playerOnFoot && playerDist < StirDistance - 0.3f) Wake();
            else
            {
                GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, "zz...", new Color(0.7f, 0.85f, 1f), 0.8f);
                Enter(State.Sleeping);
            }
        }
    }

    /// <summary>Angry wake-up (you messed with its pile): blamed on the player it is watching right now.</summary>
    public void Wake() => Wake(target);

    /// <summary>Angry wake-up caused by <paramref name="by"/> (a bag grabbed off its pile may not be the nearest player).</summary>
    void Wake(NightTarget by)
    {
        if (Despawning) return;
        if (by.Valid) waker = by;
        if (bonks >= 2)
        {
            // Too tired for another chase: just grumbles at you.
            Enter(State.Grumpy);
            return;
        }
        Enter(State.Waking);
    }

    /// <summary>
    /// "You woke the Pile Goblin! RUN!" is for the player who woke it, not for everyone who can see the goblin (the host's
    /// brain and every guest's puppet all enter Waking): this machine's own player directly, a guest through the host.
    /// </summary>
    void WarnWaker()
    {
        var who = waker.Valid ? waker : target;
        if (!who.Valid) return;
        string text = $"You woke the {DisplayName}! RUN!";
        if (who.local) GameHUD.Toast(text, ToastStyle.Bad, 2.2f);
        else NightNet.HostToastTo(who.client, text, ToastStyle.Bad);
    }

    void WakeAndLeave()
    {
        Enter(State.Evicted);
    }

    void TickWaking(float dt)
    {
        if (player != null) Face(playerFeet, dt, 400f);
        if (stateT < stateDur) return;
        if (PileGone && !playerOnFoot) { Enter(State.Evicted); return; }
        if (!playerOnFoot) { Enter(State.Grumpy); return; }
        // Hop down off the pile toward the player.
        Vector3 land = transform.position + FlatDirTo(playerFeet) * 0.9f;
        land.y = CritterWorld.GroundY(land, Nest.y);
        JumpTo(land, 0.5f, State.Chase);
    }

    void JumpTo(Vector3 to, float height, State next)
    {
        seated = false;
        jumpFrom = transform.position;
        jumpTo = to;
        jumpHeight = height;
        afterJump = next;
        Enter(State.Jumping);
    }

    void TickJumping(float dt)
    {
        float k = Mathf.Clamp01(stateT / stateDur);
        Vector3 p = Vector3.Lerp(jumpFrom, jumpTo, k);
        p.y = Mathf.Lerp(jumpFrom.y, jumpTo.y, k) + Mathf.Sin(k * Mathf.PI) * jumpHeight;
        transform.position = p;
        Face(jumpTo, dt, 300f);
        if (k >= 1f)
        {
            Juice.Squash(Rig.juice, 0.3f, 0.25f);
            Fx.Dust(transform.position, 0.5f);
            Sfx.Play("thud", transform.position, 0.6f, 1.1f);
            if (afterJump == State.Sleeping) { seated = true; seatY = SeatHeight(); }
            Enter(afterJump);
        }
    }

    void TickChase(float dt)
    {
        float chaseTime = Time.time - chaseStartedAt;
        if (playerDriving || !playerOnFoot)
        {
            if (stateT > 0.8f) { GiveUp(); return; }
            Move(Vector3.zero, 0f, dt);
            return;
        }
        if (playerDist > 20f || chaseTime > 15f) { GiveUp(); return; }
        if (CritterRepellent.IsActive && playerDist < 2.2f)
        {
            GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, "EW! what IS that smell", new Color(0.6f, 1f, 0.4f), 1.1f);
            CritterRepellent.TryProtect();
            Enter(State.Return);
            return;
        }
        Move(FlatDirTo(playerFeet), ChaseSpeed, dt);
        if (Time.time >= nextStomp)
        {
            nextStomp = Time.time + 0.28f;
            Fx.Dust(transform.position, 0.3f);
            if (playerDist < 20f) Sfx.Play("thud", transform.position, 0.25f, Random.Range(1.3f, 1.6f));
        }
        if (Time.time >= nextGrumble)
        {
            nextGrumble = Time.time + Random.Range(1.2f, 2.2f);
            Sfx.Play(Look.grumbleSfx, Rig.head.position, 0.6f, Random.Range(1f, 1.2f));
        }
        if (playerDist < 1.3f) Enter(State.Windup);
    }

    void GiveUp()
    {
        GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, Pick(Look.giveUpLines), new Color(0.9f, 0.8f, 0.6f), 1f);
        Sfx.Play(Look.grumbleSfx, Rig.head.position, 0.7f, 0.9f);
        Enter(State.Return);
    }

    void TickWindup(float dt)
    {
        if (player != null) Face(playerFeet, dt, 900f);
        if (stateT < stateDur) return;
        if (!playerOnFoot || playerDist > 2.3f) { Enter(State.Chase); return; }
        Enter(State.Bonk);
    }

    bool swingResolved, swingHit;

    void TickBonk(float dt)
    {
        if (stateT < 0.1f) { swingResolved = false; swingHit = false; return; }
        if (!swingResolved)
        {
            swingResolved = true;
            if (playerOnFoot && playerDist < 2f && HitPlayer(2.4f, 0.45f, Pick(Look.bonkLines), new Color(1f, 0.85f, 0.2f), "slam", 0.45f))
            {
                swingHit = true;
                bonks++;
                Fx.Burst(Rig.weapon != null ? Rig.weapon.position : transform.position + Vector3.up, new Color(1f, 0.25f, 0.25f), 8, 3f);
            }
            else
            {
                Sfx.Play("whoosh", transform.position + Vector3.up * 0.8f, 0.8f, 0.6f);
                GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, "*whiff*", new Color(0.8f, 0.8f, 0.8f), 0.9f);
            }
        }
        if (stateT >= stateDur) Enter(swingHit ? State.Gloat : State.Chase);
    }

    void TickGloat(float dt)
    {
        Move(Vector3.zero, 0f, dt);
        if (stateT >= stateDur) Enter(State.Return);
    }

    void TickReturn(float dt)
    {
        if (PileGone) { Enter(State.Evicted); return; }
        Vector3 nestGround = Nest; nestGround.y = CritterWorld.GroundY(Nest, Nest.y);
        if (MoveTo(nestGround, 2.2f, dt, 0.6f))
        {
            float seat = SeatHeight();
            Vector3 top = new Vector3(Nest.x, seat, Nest.z);
            GameHUD.FloatingText(transform.position + Vector3.up * 1.2f, "*yawn*", new Color(0.75f, 0.85f, 1f), 0.9f);
            JumpTo(top, 0.35f + Mathf.Max(0f, seat - transform.position.y), State.Sleeping);
            return;
        }
        if (Time.time >= nextMutter)
        {
            nextMutter = Time.time + Random.Range(1.8f, 3f);
            if (playerDist < 25f) GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, Pick(Look.mutterLines), new Color(0.75f, 0.9f, 0.6f), 0.75f);
        }
        // Grabbed another of its bags while it walks home? Straight back to chasing.
    }

    void TickGrumpy(float dt)
    {
        if (seated) UpdateSeat(dt);
        else Move(Vector3.zero, 0f, dt);
        if (player != null) Face(playerFeet, dt, 200f);
        if (stateT < stateDur) return;
        if (PileGone) { Enter(State.Evicted); return; }
        if (!seated) { Enter(State.Return); return; }
        Enter(State.Sleeping);
    }

    void TickStunned(float dt)
    {
        Move(Vector3.zero, 0f, dt);
        if (stateT < stateDur) return;
        if (playerOnFoot && playerDist < 15f && bonks < 2) { Enter(State.Chase); return; }
        Enter(State.Return);
    }

    void TickEvicted(float dt)
    {
        // Tantrum on the spot: stomp stomp stomp.
        Move(Vector3.zero, 0f, dt);
        if (player != null) Face(playerFeet, dt, 200f);
        if (Time.time >= nextStomp)
        {
            nextStomp = Time.time + 0.22f;
            Fx.Dust(transform.position, 0.45f);
            Sfx.Play("thud", transform.position, 0.45f, Random.Range(0.9f, 1.2f));
        }
        if (stateT >= stateDur) Enter(State.Leaving);
    }

    void TickLeaving(float dt)
    {
        // Stomp off away from the street / the player, muttering.
        Vector3 away = Home != null ? -CritterWorld.Flat(Home.transform.forward) : CritterWorld.Flat(transform.position - playerFeet);
        if (player != null && playerDist < 6f) away += CritterWorld.Flat(transform.position - playerFeet).normalized;
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        Move(away.normalized, 2.3f, dt);
        if (Time.time >= nextStomp)
        {
            nextStomp = Time.time + 0.35f;
            Fx.Dust(transform.position, 0.25f);
        }
        if (Time.time >= nextMutter)
        {
            nextMutter = Time.time + Random.Range(1.4f, 2.4f);
            if (playerDist < 30f)
            {
                GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, Pick(Look.mutterLines), new Color(0.75f, 0.9f, 0.6f), 0.8f);
                Sfx.Play(Look.grumbleSfx, Rig.head.position, 0.5f, Random.Range(0.9f, 1.2f));
            }
        }
        if ((stateT > 2f && (!Seen || playerDist > 30f)) || stateT > stateDur) Poof();
    }

    void PopBubble(bool loud)
    {
        if (Rig.bubble == null) return;
        if (Rig.bubble.localScale.x > 0.04f)
        {
            Vector3 at = Rig.bubble.position;
            Sfx.Play("pop", at, loud ? 1f : 0.6f, loud ? 0.9f : 1.3f);
            Fx.Burst(at, new Color(0.75f, 0.9f, 1f), loud ? 10 : 5, 2f);
            if (loud) GameHUD.FloatingText(at + Vector3.up * 0.3f, "*POP*", new Color(0.75f, 0.9f, 1f), 0.9f);
        }
        bubbleT = -1.5f;
        Rig.bubble.localScale = Vector3.one * 0.001f;
    }

    // =====================================================================
    // Events
    // =====================================================================

    bool IsFromMyPile(TrashBag bag)
    {
        if (bag == null) return false;
        if (Home != null && bag.SourceStop == Home) return true;
        return CritterWorld.Flat(bag.transform.position - Nest).sqrMagnitude < 2.6f * 2.6f;
    }

    public override void OnBagGrabbed(TrashBag bag, TrashCarrier by)
    {
        if (Despawning || NetPuppet || !IsFromMyPile(bag)) return;
        switch (Current)
        {
            case State.Sleeping:
            case State.Stirring:
                var grabber = by != null ? by.GetComponentInParent<PlayerController>() : null;
                Wake(grabber != null ? NightPlayers.Describe(grabber, transform.position) : target);
                break;
            case State.Return:
            case State.Gloat:
            case State.Grumpy:
                if (bonks >= 2) { if (Current != State.Grumpy) Enter(State.Grumpy); }
                else
                {
                    GameHUD.FloatingText(Rig.head.position + Vector3.up * 0.5f, "HEY!!", new Color(1f, 0.35f, 0.25f), 1.2f);
                    Sfx.Play(Look.angrySfx, Rig.head.position, 0.9f, 1.1f);
                    if (seated) Enter(State.Waking); else Enter(State.Chase);
                }
                break;
        }
    }

    public override void Scare(Vector3 from, bool horn)
    {
        if (Despawning || NetPuppet) return;
        // Horns don't scare goblins. They just make them grumpy.
        if (Current == State.Sleeping || Current == State.Stirring)
        {
            if (playerOnFoot && playerDist < 6f) Wake();
            else Enter(State.Grumpy);
        }
    }

    protected override void OnHitByThrown(Carryable c, Vector3 velocity)
    {
        if (seated) { seated = false; }
        Enter(State.Stunned);
    }

    protected override void OnLanded()
    {
        seated = false;
        Enter(State.Stunned);
    }

    // =====================================================================
    // Procedural animation
    // =====================================================================

    void Animate(float dt)
    {
        float t = Time.time + GetInstanceID() * 0.21f;
        Vector3 delta = CritterWorld.Flat(transform.position - lastPos);
        lastPos = transform.position;
        float speed = dt > 0f ? delta.magnitude / dt : 0f;
        bool moving = speed > 0.3f && Current != State.Jumping;
        bool asleep = Current == State.Sleeping || Current == State.Stirring;

        // Breathing / snot bubble (sleeping).
        breath += dt * (Mathf.PI * 2f / 3f);
        float inhale = Mathf.Sin(breath) * 0.5f + 0.5f;
        if (asleep)
        {
            if (breath > Mathf.PI * 2f) { breath -= Mathf.PI * 2f; breathCount++; }
            bubbleT += dt;
            float b = Current == State.Sleeping && bubbleT > 0f ? Mathf.Lerp(0.02f, 0.1f, inhale) : 0.001f;
            if (breathCount >= 5 && inhale > 0.97f && Current == State.Sleeping) { breathCount = 0; PopBubble(false); b = 0.001f; }
            if (Rig.bubble != null) Rig.bubble.localScale = Damp(Rig.bubble.localScale, Vector3.one * b, 8f, dt);
        }
        else if (Rig.bubble != null && Rig.bubble.localScale.x > 0.002f) Rig.bubble.localScale = Vector3.one * 0.001f;

        float hip = 0f, lean = 0f, mouth = 0.035f, roll = 0f, spin = 0f;
        float armL = -20f, armR = 20f, armRPitch = 0f, armLPitch = 0f, legPitchL = 0f, legPitchR = 0f;
        float y = 0f, squash = 0f, headRoll = 0f, headPitch = 0f;
        switch (Current)
        {
            case State.Sleeping:
                hip = -24f; headRoll = 20f + Mathf.Sin(t * 0.4f) * 4f; headPitch = -10f;
                mouth = Mathf.Lerp(0.03f, 0.08f, inhale);
                armL = -65f; armR = 65f;
                legPitchL = legPitchR = -80f; // legs sticking out forward, sitting on the heap
                squash = (inhale - 0.5f) * -0.06f;
                break;
            case State.Stirring:
                hip = -16f; headRoll = 8f; headPitch = -4f;
                armL = -60f; armR = 60f;
                legPitchL = legPitchR = -80f;
                break;
            case State.Waking:
                hip = -5f; mouth = 0.1f; armL = -110f; armR = 110f;
                legPitchL = legPitchR = seated ? -60f : 0f;
                squash = Mathf.Sin(stateT * 30f) * 0.05f;
                break;
            case State.Jumping:
                armL = -120f; armR = 120f; legPitchL = -30f; legPitchR = 20f;
                break;
            case State.Chase:
                lean = 14f; mouth = 0.08f;
                armLPitch = Mathf.Sin(hopPhase) * 50f; armRPitch = -150f; armR = 10f;
                break;
            case State.Windup:
                lean = -10f; armRPitch = -175f; armR = 5f; mouth = 0.1f;
                squash = -0.1f + Mathf.Sin(t * 60f) * 0.03f;
                break;
            case State.Bonk:
                lean = 20f; armRPitch = Mathf.Lerp(-175f, -35f, Mathf.Clamp01(stateT / 0.1f)); armR = 5f; mouth = 0.12f;
                break;
            case State.Gloat:
                spin = Mathf.Clamp01(stateT / 0.7f) * 360f;
                y = Mathf.Abs(Mathf.Sin(stateT * 9f)) * 0.12f;
                armL = -140f; armR = 140f; mouth = 0.06f;
                break;
            case State.Grumpy:
                hip = seated ? -10f : 0f;
                armR = 30f; armRPitch = -130f + Mathf.Sin(t * 22f) * 25f; // shaking its fist (plunger)
                legPitchL = legPitchR = seated ? -70f : 0f;
                mouth = 0.09f;
                break;
            case State.Stunned:
                hip = -30f; headRoll = Mathf.Sin(t * 6f) * 20f; roll = Mathf.Sin(t * 5f) * 10f;
                armL = -80f; armR = 80f; mouth = 0.05f;
                legPitchL = legPitchR = -80f;
                y = -0.12f;
                break;
            case State.Evicted:
                y = Mathf.Abs(Mathf.Sin(stateT * 11f)) * 0.22f;
                armL = -90f + Mathf.Sin(t * 25f) * 50f; armR = 90f + Mathf.Sin(t * 23f + 1f) * 50f;
                mouth = 0.12f;
                break;
            case State.Return:
            case State.Leaving:
                mouth = 0.05f;
                armLPitch = Mathf.Sin(hopPhase) * 35f; armRPitch = -Mathf.Sin(hopPhase) * 35f;
                break;
        }

        if (moving)
        {
            float swing = Mathf.Sin(hopPhase) * 38f;
            legPitchL += swing; legPitchR -= swing;
            roll += Mathf.Sin(hopPhase) * 9f; // waddle
            y += Mathf.Abs(Mathf.Cos(hopPhase)) * 0.06f;
            squash += Mathf.Cos(hopPhase * 2f) * 0.05f;
        }
        else if (!asleep) squash += Mathf.Sin(t * 3f) * 0.02f;

        animHip = Damp(animHip, hip, 8f, dt);
        animLean = Damp(animLean, lean, 8f, dt);
        animMouth = Damp(animMouth, mouth, 14f, dt);
        animRoll = Damp(animRoll, roll, 12f, dt);
        animSpin = spin; // explicit, not damped (it's a full turn)

        Rig.visual.localPosition = new Vector3(0f, y, 0f);
        Rig.visual.localScale = new Vector3(1f - squash * 0.5f, 1f + squash, 1f - squash * 0.5f);
        Rig.visual.localRotation = Quaternion.Euler(animLean, animSpin, animRoll);
        Rig.rear.localRotation = Quaternion.Euler(animHip, 0f, 0f);

        // Head looks at the player when awake.
        float yaw = 0f;
        if (!asleep && player != null && playerDist < 15f)
        {
            Vector3 local = transform.InverseTransformPoint(PlayerHead);
            yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -50f, 50f);
        }
        Rig.head.localRotation = Damp(Rig.head.localRotation, Quaternion.Euler(headPitch, yaw, headRoll), 8f, dt);
        if (Rig.mouthShape != null) Rig.mouthShape.localScale = new Vector3(0.16f, animMouth, 0.06f);

        if (Rig.armL != null) Rig.armL.localRotation = Damp(Rig.armL.localRotation, Quaternion.Euler(armLPitch, 0f, armL), 14f, dt);
        if (Rig.armR != null) Rig.armR.localRotation = Damp(Rig.armR.localRotation, Quaternion.Euler(armRPitch, 0f, armR), Current == State.Bonk ? 40f : 14f, dt);
        if (Rig.legL != null) Rig.legL.localRotation = Damp(Rig.legL.localRotation, Quaternion.Euler(legPitchL, 0f, 0f), 16f, dt);
        if (Rig.legR != null) Rig.legR.localRotation = Damp(Rig.legR.localRotation, Quaternion.Euler(legPitchR, 0f, 0f), 16f, dt);

        // Ears droop asleep, flap when running.
        for (int i = 0; i < Rig.ears.Count; i++)
        {
            float side = i == 0 ? 1f : -1f;
            float droop = asleep ? 28f : Current == State.Evicted || Current == State.Grumpy ? -10f : 0f;
            float flap = moving ? Mathf.Sin(hopPhase * 2f + i) * 14f : Mathf.Sin(t * 1.3f + i) * 3f;
            Rig.ears[i].localRotation = Rig.earRest[i] * Quaternion.Euler(0f, 0f, (droop + flap) * side);
        }

        // Nightcap flops around (more when moving).
        float flop = moving ? 7f : 2.5f;
        for (int i = 0; i < Rig.cap.Count; i++)
        {
            float w = Mathf.Sin(t * (moving ? 9f : 1.5f) - i * 0.6f) * flop * (0.3f + i * 0.1f);
            float bounce = moving ? Mathf.Cos(hopPhase * 2f - i * 0.5f) * 3f : Mathf.Cos(t * 1.1f - i) * 1.5f;
            Rig.cap[i].localRotation = Rig.capRest[i] * Quaternion.Euler(bounce, 0f, w);
        }
    }
}

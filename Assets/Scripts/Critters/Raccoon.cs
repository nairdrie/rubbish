using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Night-time trash panda. Spawned by CritterSpawner behind a curb bag (host only, local object).
///
/// States:
///   Hiding     crouched behind its bag; peeks over it (googly eyes glinting), tail sticking out, bag wiggles, chitters.
///   Rummaging  head-first in the bag: bits fly, bag jiggles; sometimes spills 1–2 items as litter.
///   Alert      you came within ~4 m: stands up on its hind legs and hisses.
///   Flee       finished its attack encounter, or repellent sent it away; mere truck approach does not scare it off.
///   Attack     any provoked raccoon chases its aggressor, telegraphs a lunge and can bite/infect; foamy ones are worse.
///   Stunned    hit by a thrown bag: flat on its back, legs wiggling, dizzy stars. Gets up angry at the thrower.
///   Sniff      you're wearing repellent: sniff sniff... "EW!" and it leaves.
///   Leaving    dawn: yawns and wanders off.
/// F4 night shift (Docs/Multiplayer.md §14.4), host-simulated and streamed to guests by <see cref="NightNet"/>:
///   Heist      swiped the shiniest thing out of its bag (host-rolled contents) and legs it with a sparkle trail; bonk it (or
///              run it over) and the loot drops as a shared loose item. Escapes after ~22 s or once nobody is near.
///   Lured      sniffing its way into a baited trash-can trap (<see cref="RaccoonTrap"/>).
///   Stowaway   hopped into a parked truck's hopper and eats the cargo (a bit every few seconds) until someone shoos it (E
///              near, or a bonk). The horn doesn't work: it's busy.
///   Latched    a provoked one clamped onto a player's head: scrambled controls for them; a friend bonks it off (E / shove / a
///              throw), or they mash Space; after 4 s it lets go and may infect. 5 s critter immunity after.
/// On a guest the same class runs as a puppet: no brain, the host's state and position, the same looks.
/// </summary>
public class Raccoon : Critter
{
    public enum State { Hiding, Rummaging, Alert, Flee, Attack, Windup, Lunge, Retreat, Stunned, Sniff, Leaving, Heist, Lured, Stowaway, Latched }

    public const float AlertDistance = 4f;
    public const float FleeDistance = 3f;
    public const float BiteDistance = 2f;
    public const float FleeSpeed = 4.8f;
    public const float ChaseSpeed = 4.6f;

    public bool Rabid { get; private set; }
    public State Current { get; private set; } = State.Hiding;
    /// <summary>The bag it hides behind / rummages in (null once it's gone).</summary>
    public TrashBag Bag { get; private set; }

    public override string DisplayName => Rabid ? "Rabid Raccoon" : "Raccoon";

    // ---- F4 night shift
    public const float HeistSeconds = 22f;
    public const float ShinyHeistChance = 0.12f;    // no valuable in the bag: still nicks something metal/glass sometimes
    public const float LatchChance = 0.4f;          // of a rabid lunge that connects
    public const float LatchSeconds = 4f;
    public const float NomEvery = 4.5f;
    public const int MaxNoms = 6;
    /// <summary>Where a stowaway sits, in metres from the truck's root (unscaled, like F1's <see cref="RideHolds"/>): in the hopper
    /// mouth just inside the rear lip, head poking out the back.</summary>
    public static readonly Vector3 StowLocal = new Vector3(0f, 1.45f, -2.2f);
    /// <summary>Fallback (no ChuckPuppet head bone found): player-root-local spot a latched raccoon clings to.</summary>
    public static readonly Vector3 LatchLocal = new Vector3(0f, 1.42f, -0.05f);
    /// <summary>
    /// Face-hug offset in WORLD metres from the player's head bone (= eye level), in the player ROOT's axes (+Z = the way he faces).
    /// The raccoon's pivot is at its feet and in the latched pose (rear -70 deg, head counter-pitched) its eyes sit about
    /// (0, 0.57, -0.16) m from that pivot at scale 0.8, so after the 180 deg turn the pivot goes ~0.59 below and 0.14 in front of
    /// the head bone to put the raccoon's face at about (0, -0.02, 0.30): over his face, snout against his nose. Unverified in play.
    /// </summary>
    public static readonly Vector3 LatchHeadLocal = new Vector3(0f, -0.59f, 0.14f);
    /// <summary>Faces the player's face.</summary>
    public static readonly Quaternion LatchHeadRot = Quaternion.Euler(0f, 180f, 0f);
    public const float LatchScale = 0.8f;

    /// <summary>Host (TryLatch) and guests (NightNet) both use this, so the attach pose is defined in one place.</summary>
    public static void AttachToFace(Critter c, PlayerController pc)
    {
        if (c == null || pc == null) return;
        var puppet = pc.GetComponentInChildren<ChuckPuppet>(true);
        Transform head = puppet != null ? puppet.head : null;
        if (head != null) c.SetAnchor(head, pc.transform, LatchHeadLocal, LatchHeadRot, LatchScale);
        else c.SetAnchor(pc.transform, LatchLocal, Quaternion.identity);
    }

    /// <summary>What it stole (host only; guests know just <see cref="LootDefId"/>).</summary>
    public ItemInstance Loot { get; private set; }
    public string LootDefId { get; private set; }
    public TruckControllerV2 StowTruck { get; private set; }
    public bool Boarded { get; private set; }
    /// <summary>Latched: the victim's client id.</summary>
    public ulong LatchVictim { get; private set; } = ulong.MaxValue;
    public RaccoonTrap LureTrap { get; private set; }
    PlayerController latchPc;
    GameObject lootVisual;
    float nextSparkle, nextNom;
    int noms;
    static readonly Color LootGold = new Color(1f, 0.85f, 0.25f);

    float stateT, stateDur;
    Vector3 threat;
    Vector3 hideSpot;
    Vector3 lungeFrom, lungeTo;
    int bites, spills;
    float attackStartedAt;
    ulong angryAt = ulong.MaxValue;
    float angryUntil, nextBiteAt, nextStartleAt, startledUntil;
    public bool Angry => Time.time < angryUntil;
    float nextChitter, nextWiggle, nextGlint, nextPeek, peekUntil, nextTwitch, nextDust, nextHiss;
    FoamBubbles foam;

    // animation state
    float animRear, animCrouch, animLean, animMouth, animFlip, animHeadYaw, animHeadPitch;
    Vector3 twitchEuler;

    static readonly string[] FleeWords = { "EEK!", "!!", "SKREEE!", "nope!", "yikes!" };
    static readonly string[] HissWords = { "hsss!", "HISSS", "*hiss*", "kkhhh!" };
    static readonly string[] CackleWords = { "hehehe", "*chitter*", "heh heh", "nyehehe" };
    static readonly string[] RabidWords = { "RAAAH!", "GRRAH!", "*foam noises*", "SKRAAA!" };
    static readonly string[] YawnWords = { "*yawn*", "zzz... bye", "nap time" };
    static readonly string[] EwWords = { "EW!", "ew ew EW", "what IS that", "*gag*" };
    static readonly Color[] BitColors =
    {
        new Color(0.45f, 0.35f, 0.2f), new Color(0.9f, 0.9f, 0.85f), new Color(0.3f, 0.6f, 0.3f), new Color(0.8f, 0.6f, 0.3f)
    };
    static readonly List<TrashBag> tmpBags = new List<TrashBag>();

    /// <summary>
    /// Town-wide cap on items raccoons spill per night. Uncapped, ~10 raccoons spilled ~20 items a night (40 after
    /// two nights), and litter alone kept the city "STINKY" even with every curb cleared.
    /// </summary>
    public const int MaxSpillsPerNight = 8;
    static int nightSpills, spillNight = int.MinValue;

    /// <summary>Night index that stays the same from noon to the next noon (a night spans midnight).</summary>
    static int NightIndex => (int)System.Math.Floor((GameClock.TotalHours - 12.0) / 24.0);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSpillBudget() { nightSpills = 0; spillNight = int.MinValue; }

    // =====================================================================
    // Spawning
    // =====================================================================

    /// <summary>Spawn a raccoon hiding behind a bag (or at pos with no bag, e.g. debug).</summary>
    public static Raccoon Spawn(Vector3 pos, Quaternion rot, bool rabid, PickupStop home, TrashBag bag)
    {
        var go = new GameObject(rabid ? "Raccoon (rabid)" : "Raccoon");
        go.transform.SetPositionAndRotation(pos, rot);
        var r = go.AddComponent<Raccoon>();
        r.Init(rabid, home, bag);
        return r;
    }

    /// <summary>Guest copy of the host's raccoon <paramref name="id"/> (NightNet).</summary>
    public static Raccoon SpawnPuppet(ushort id, bool rabid, Vector3 pos, float yaw, byte state)
    {
        var go = new GameObject(rabid ? "Raccoon (rabid, net)" : "Raccoon (net)");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        var r = go.AddComponent<Raccoon>();
        r.MakePuppet(id);
        r.Init(rabid, null, null);
        r.NetSnapshot(pos, yaw, 0.2f);
        r.ApplyNetState(state, 0);
        return r;
    }

    void Init(bool rabid, PickupStop home, TrashBag bag)
    {
        Rabid = rabid;
        Home = home;
        Bag = bag;
        stepRadius = 0.2f;
        turnRate = rabid ? 720f : 600f;
        Rig = CritterLook.BuildRaccoon(gameObject, rabid);
        if (rabid && Rig.mouth != null)
        {
            foam = Rig.mouth.gameObject.AddComponent<FoamBubbles>();
            foam.rate = 2.5f;
            foam.size = 0.035f;
            foam.popSounds = false;
        }
        hideSpot = transform.position;
        CritterPoke.Attach(this);
        Juice.PopIn(Rig.juice, 0.3f);
        nextPeek = Time.time + Random.Range(1f, 3f);
        nextWiggle = Time.time + Random.Range(0.5f, 2f);
        nextChitter = Time.time + Random.Range(2f, 6f);
        if (NetPuppet) Current = State.Hiding; // the host's state arrives with the snapshot (no spawn hiss)
        else Enter(bag != null ? State.Hiding : State.Alert);
    }

    /// <summary>Where to crouch so the bag is between us and the street.</summary>
    public static Vector3 HideSpotFor(TrashBag bag, PickupStop stop, out Quaternion facing)
    {
        Vector3 toRoad = stop != null ? CritterWorld.Flat(stop.transform.forward).normalized : Vector3.forward;
        if (toRoad.sqrMagnitude < 0.01f) toRoad = Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, toRoad);
        Vector3 p = bag.transform.position - toRoad * Random.Range(0.78f, 0.9f) + side * Random.Range(-0.15f, 0.15f); // snout just behind the bag
        facing = Quaternion.LookRotation(toRoad, Vector3.up);
        p.y = CritterWorld.GroundY(p, bag.transform.position.y);
        return p;
    }

    // =====================================================================
    // State machine
    // =====================================================================

    void Enter(State s)
    {
        Current = s;
        stateT = 0f;
        Vector3 head = Rig.head != null ? Rig.head.position : transform.position + Vector3.up * 0.4f;
        switch (s)
        {
            case State.Hiding:
                stateDur = Random.Range(6f, 16f);
                break;
            case State.Rummaging:
                stateDur = Random.Range(5f, 10f);
                break;
            case State.Alert:
                stateDur = Random.Range(0.8f, 1.2f);
                Sfx.Play("hiss", head, 0.9f, Rabid ? 0.8f : Random.Range(1f, 1.2f));
                GameHUD.FloatingText(head + Vector3.up * 0.4f, HissWords[Random.Range(0, HissWords.Length)], new Color(0.8f, 0.8f, 0.85f), 0.9f);
                Juice.Punch(Rig.juice, 0.25f, 0.25f);
                Rig.KickEyes(4f);
                break;
            case State.Flee:
                stateDur = Random.Range(4f, 7f);
                Sfx.Play("raccoon_scream", head, 0.9f, Random.Range(0.95f, 1.2f));
                GameHUD.FloatingText(head + Vector3.up * 0.45f, FleeWords[Random.Range(0, FleeWords.Length)], new Color(1f, 0.95f, 0.6f), 1f);
                Juice.Squash(Rig.juice, -0.3f, 0.25f);
                Rig.KickEyes(6f);
                Fx.Dust(transform.position, 0.5f);
                break;
            case State.Attack:
                stateDur = 0f;
                if (attackStartedAt <= 0f)
                {
                    attackStartedAt = Time.time;
                    Sfx.Play("raccoon_scream", head, 1f, 0.75f);
                    GameHUD.FloatingText(head + Vector3.up * 0.45f, RabidWords[Random.Range(0, RabidWords.Length)], new Color(1f, 0.25f, 0.2f), 1.25f);
                    Juice.Shake(0.06f, 0.1f);
                }
                if (foam != null) foam.rate = 9f;
                break;
            case State.Windup:
                stateDur = RaccoonTemper.WindupSeconds;
                Sfx.Play("hiss", head, 1f, 0.7f);
                break;
            case State.Lunge:
                stateDur = 0.3f;
                lungeFrom = transform.position;
                Vector3 to = playerFeet - FlatDirTo(playerFeet) * 0.35f;
                Vector3 d = CritterWorld.Flat(to - lungeFrom);
                if (d.magnitude > 2.2f) to = lungeFrom + d.normalized * 2.2f;
                lungeTo = to;
                Sfx.Play("whoosh", head, 0.7f, 1.5f);
                break;
            case State.Retreat:
                stateDur = Random.Range(0.9f, 1.4f);
                GameHUD.FloatingText(head + Vector3.up * 0.4f, CackleWords[Random.Range(0, CackleWords.Length)], new Color(1f, 0.5f, 0.45f), 0.9f);
                Sfx.Play("chitter", head, 0.9f, 1.25f);
                break;
            case State.Stunned:
                stateDur = 2.2f;
                DizzyStars.Attach(Rig.head, stateDur, 0.2f, 0.18f);
                Sfx.Play("squeak", head, 0.9f, 0.8f);
                Rig.KickEyes(8f);
                break;
            case State.Sniff:
                stateDur = 1.1f;
                Sfx.Play("sniff", head, 0.7f, 1.3f);
                GameHUD.FloatingText(head + Vector3.up * 0.4f, "sniff sniff...", new Color(0.85f, 0.85f, 0.85f), 0.8f);
                break;
            case State.Leaving:
                stateDur = Random.Range(4f, 8f);
                if (Seen && playerDist < 25f)
                    GameHUD.FloatingText(head + Vector3.up * 0.4f, YawnWords[Random.Range(0, YawnWords.Length)], new Color(0.8f, 0.85f, 1f), 0.8f);
                break;
            case State.Heist:
                stateDur = HeistSeconds;
                Sfx.Play("chitter", head, 1f, 1.5f);
                Sfx.Play("sparkle", head, 0.7f, 1.2f);
                GameHUD.FloatingText(head + Vector3.up * 0.5f, NightDice.Cosmetic.Pick(HeistWords), LootGold, 1.3f);
                Juice.Punch(Rig.juice, 0.3f, 0.25f);
                Rig.KickEyes(5f);
                break;
            case State.Lured:
                stateDur = 30f;
                if (playerDist < 20f) GameHUD.FloatingText(head + Vector3.up * 0.4f, "*sniff sniff* ...fish?", new Color(0.85f, 0.9f, 1f), 0.8f);
                break;
            case State.Stowaway:
                stateDur = 90f;
                noms = 0;
                nextNom = Time.time + 2f;
                break;
            case State.Latched:
                stateDur = LatchSeconds;
                Sfx.Play("raccoon_scream", head, 1f, 0.9f);
                GameHUD.FloatingText(head + Vector3.up * 0.4f, "LATCHED!", new Color(1f, 0.3f, 0.25f), 1.4f);
                Rig.KickEyes(8f);
                break;
        }
    }

    static readonly string[] HeistWords = { "MINE!", "*yoink*", "shiny!!", "finders keepers!", "hehe SHINY" };

    protected override void Tick(float dt)
    {
        bool hadAggressor = Angry || angryAt != ulong.MaxValue;
        FocusAggressor();
        // Losing the aggressor does not silently redirect an in-flight attack at an innocent bystander.
        if (hadAggressor && !Angry && (Current == State.Attack || Current == State.Windup || Current == State.Lunge || Current == State.Retreat))
            Enter(State.Alert);
        if (!Angry) angryAt = ulong.MaxValue;
        if (Time.time < startledUntil && Current != State.Stunned && Current != State.Latched && Current != State.Stowaway)
        {
            Move(CritterWorld.Flat(transform.position - threat), 2.8f, dt);
            Animate(dt);
            return;
        }
        stateT += dt;
        if (Bag != null && (!CritterWorld.IsGrabbableBag(Bag) || (Bag.transform.position - hideSpot).sqrMagnitude > 2.5f * 2.5f))
            Bag = null; // gone (carried, burst, moved)

        switch (Current)
        {
            case State.Hiding: TickHiding(dt); break;
            case State.Rummaging: TickRummaging(dt); break;
            case State.Alert: TickAlert(dt); break;
            case State.Flee: TickFlee(dt); break;
            case State.Attack: TickAttack(dt); break;
            case State.Windup: TickWindup(dt); break;
            case State.Lunge: TickLunge(dt); break;
            case State.Retreat: TickRetreat(dt); break;
            case State.Stunned: TickStunned(dt); break;
            case State.Sniff: TickSniff(dt); break;
            case State.Leaving: TickLeaving(dt); break;
            case State.Heist: TickHeist(dt); break;
            case State.Lured: TickLured(dt); break;
            case State.Stowaway: TickStowaway(dt); break;
            case State.Latched: TickLatched(dt); break;
        }
        Animate(dt);
    }

    bool PlayerThreatens => playerOnFoot && playerDist < AlertDistance;

    void FocusAggressor()
    {
        if (!Angry || angryAt == ulong.MaxValue) return;
        var pc = NightPlayers.ForClient(angryAt);
        if (pc == null || NightPlayers.IsIndoors(pc)) { angryUntil = 0f; return; }
        var t = NightPlayers.Describe(pc, transform.position);
        if (t.dist > RaccoonTemper.ChaseRange) { angryUntil = 0f; return; }
        target = t; player = t.pc; playerOnFoot = t.onFoot; playerDriving = t.driving;
        playerFeet = t.feet; playerDist = t.dist;
    }

    void Provoke(ulong by, Vector3 from)
    {
        if (NetPuppet || Despawning) return;
        if (Friendly(by)) return; // Raccoon calling: no grudges against family
        if (!Angry) { bites = 0; attackStartedAt = 0f; }
        angryAt = by;
        angryUntil = Time.time + RaccoonTemper.AngerSeconds;
        threat = from;
        FocusAggressor();
    }

    void LostBagReaction()
    {
        if (playerOnFoot && playerDist < 7f) Provoke(target.client, playerFeet);
        Enter(State.Alert); // hold ground even when a nearby driver is still safe inside the cab
    }

    void TickHiding(float dt)
    {
        if (Bag == null) { LostBagReaction(); return; }
        MoveTo(hideSpot, 1.4f, dt, 0.08f);
        if (speedNow < 0.2f) Face(Bag.transform.position + (Home != null ? Home.transform.forward : Vector3.zero) * 3f, dt, 180f);

        if (PlayerThreatens) { Enter(State.Alert); return; }

        // Peek over the bag every so often; eyes glint in the dark.
        if (Time.time >= nextPeek)
        {
            peekUntil = Time.time + Random.Range(0.8f, 1.6f);
            nextPeek = peekUntil + Random.Range(2f, 5f);
            if (GameClock.Night01 > 0.4f && playerDist < 28f && Time.time >= nextGlint)
            {
                nextGlint = Time.time + 3f;
                foreach (var e in Rig.eyes) if (e != null) Fx.Sparkle(e.transform, Rabid ? CritterLook.RabidRed : Color.white, 0.35f);
            }
        }
        // The bag wiggles suspiciously.
        if (Time.time >= nextWiggle)
        {
            nextWiggle = Time.time + Random.Range(1.5f, 4f);
            if (playerDist < 35f)
            {
                Juice.Squash(Bag.JuiceTarget, Random.Range(0.06f, 0.12f), 0.25f);
                if (playerDist < 18f) Sfx.Play("rustle", Bag.transform.position, 0.35f, Random.Range(1.1f, 1.4f));
            }
        }
        if (Time.time >= nextChitter)
        {
            nextChitter = Time.time + Random.Range(4f, 9f);
            if (playerDist < 16f) Sfx.Play("chitter", transform.position + Vector3.up * 0.4f, 0.45f, Random.Range(1f, 1.3f));
        }
        if (stateT >= stateDur && playerDist > 6f) Enter(State.Rummaging);
    }

    void TickRummaging(float dt)
    {
        if (Bag == null) { LostBagReaction(); return; }
        // Stand at the bag's side, face into it.
        Vector3 side = Vector3.Cross(Vector3.up, CritterWorld.Flat(hideSpot - Bag.transform.position).normalized);
        Vector3 spot = Bag.transform.position + side * 0.62f; // head goes in, butt stays out
        if (!MoveTo(spot, 1.6f, dt, 0.1f)) return;
        Face(Bag.transform.position, dt, 300f);

        if (PlayerThreatens) { Enter(State.Alert); return; }

        if (Time.time >= nextWiggle)
        {
            nextWiggle = Time.time + Random.Range(0.25f, 0.45f);
            Juice.Squash(Bag.JuiceTarget, Random.Range(0.08f, 0.16f), 0.2f);
            Vector3 top = Bag.WorldCenter + Vector3.up * 0.25f;
            if (playerDist < 40f) Fx.Burst(top, BitColors[Random.Range(0, BitColors.Length)], 3, 1.8f);
            if (playerDist < 20f && Random.value < 0.45f) Sfx.Play("rustle", top, 0.5f, Random.Range(0.9f, 1.3f));
            if (playerDist < 14f && Random.value < 0.12f) Sfx.Play("chitter", top, 0.5f, Random.Range(1.1f, 1.4f));
        }
        if (stateT >= stateDur)
        {
            if (TryHeist()) return;
            TrySpill();
            Enter(State.Hiding);
        }
    }

    /// <summary>Tear the bag a bit: 1–2 items tumble out as loose litter.</summary>
    void TrySpill()
    {
        if (Bag == null || Bag.Contents == null || Bag.Contents.items == null) return;
        if (spills >= 2 || Bag.Contents.items.Count <= 3 || Random.value > 0.4f) return;
        int night = NightIndex;
        if (night != spillNight) { spillNight = night; nightSpills = 0; }
        if (nightSpills >= MaxSpillsPerNight) return;
        int n = Mathf.Min(Random.Range(1, 3), 2 - spills, MaxSpillsPerNight - nightSpills);
        int spilledBefore = spills;
        Vector3 from = Bag.WorldCenter + Vector3.up * 0.2f;
        Vector3 out1 = CritterWorld.Flat(transform.position - Bag.transform.position);
        out1 = out1.sqrMagnitude > 0.01f ? out1.normalized : Vector3.forward;
        for (int i = 0; i < n && Bag.Contents.items.Count > 3; i++)
        {
            int idx = Random.Range(0, Bag.Contents.items.Count);
            var item = Bag.Contents.items[idx];
            if (item == null) continue;
            var def = item.Def;
            if (def != null && def.category == ItemCategory.Valuable) continue; // raccoons have taste; they keep the shiny stuff buried
            Bag.Contents.items.RemoveAt(idx);
            spills++;
            nightSpills++;
            try
            {
                Vector3 v = (out1 + Random.insideUnitSphere * 0.5f + Vector3.up).normalized * Random.Range(2f, 3.5f);
                LooseItem.Spawn(item, from + Random.insideUnitSphere * 0.1f, Random.rotation, v, false);
            }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        if (spills > spilledBefore)
        {
            Sfx.Play("bag_burst", from, 0.35f, 1.4f);
            Fx.Burst(from, new Color(0.06f, 0.06f, 0.07f), 8, 2.5f);
            if (playerDist < 25f) GameHUD.FloatingText(from + Vector3.up * 0.4f, "*rip*", new Color(0.8f, 0.8f, 0.8f), 0.8f);
        }
    }

    void TickAlert(float dt)
    {
        if (player != null) Face(playerFeet, dt, 540f);
        if (stateT < stateDur) return;
        threat = playerFeet;
        if (playerOnFoot && (Angry || playerDist < BiteDistance + 0.2f))
        {
            // The hiss is the warning. Crowd it, steal its bag or hit it and even a plain raccoon bites back.
            if (!Angry) Provoke(target.client, playerFeet);
            if (TargetRepelled) Enter(State.Sniff);
            else if (RaccoonTemper.CanRetaliate(playerOnFoot, playerDist, false, bites)) Enter(State.Attack);
            else if (bites >= RaccoonTemper.BitesPerEncounter) Enter(State.Flee);
            return;
        }
        if (!playerOnFoot || playerDist > 6f)
        {
            if (Bag != null) { Enter(State.Hiding); return; }
            Move(Vector3.zero, 0f, dt);
            return;
        }
        // All raccoons hold their ground until you come too close or actually provoke them.
        // Stand-off: hiss again.
        if (Time.time >= nextHiss)
        {
            nextHiss = Time.time + 1.2f;
            Sfx.Play("hiss", Rig.head.position, 0.6f, Random.Range(1f, 1.3f));
            Juice.Squash(Rig.juice, 0.12f, 0.2f);
        }
    }

    void TickFlee(float dt)
    {
        Vector3 away = CritterWorld.Flat(transform.position - threat);
        if (away.sqrMagnitude < 0.01f) away = -transform.forward;
        // A little panicked zig-zag.
        away = Quaternion.Euler(0f, Mathf.Sin(Time.time * 7f + GetInstanceID()) * 25f, 0f) * away.normalized;
        Move(away, FleeSpeed, dt);
        if (Time.time >= nextDust && playerDist < 40f)
        {
            nextDust = Time.time + 0.22f;
            Fx.Dust(transform.position, 0.25f);
        }
        bool far = playerDist > 16f;
        if ((stateT > stateDur && (far || !Seen)) || stateT > 10f) Poof();
    }

    void TickAttack(float dt)
    {
        if (TargetRepelled && playerDist < 2.4f) { Enter(State.Sniff); return; }
        bool giveUp = playerDist > RaccoonTemper.ChaseRange || Time.time - attackStartedAt > RaccoonTemper.AngerSeconds || bites >= RaccoonTemper.BitesPerEncounter;
        if (giveUp) { threat = playerFeet; Enter(State.Flee); return; }
        if (!playerOnFoot) { Move(Vector3.zero, 0f, dt); Face(playerFeet, dt, 400f); return; }
        if (Time.time < nextBiteAt || NightShift.CritterImmune(target.client))
        { Move(Vector3.zero, 0f, dt); Face(playerFeet, dt, 400f); return; }

        Vector3 dir = FlatDirTo(playerFeet);
        dir = Quaternion.Euler(0f, Mathf.Sin(Time.time * 11f) * 18f, 0f) * dir; // twitchy, derpy chase
        Move(dir, ChaseSpeed, dt);
        if (Time.time >= nextChitter)
        {
            nextChitter = Time.time + Random.Range(0.6f, 1.1f);
            Sfx.Play("chitter", transform.position + Vector3.up * 0.4f, 0.7f, Random.Range(0.7f, 0.9f));
        }
        if (playerDist < 1.35f) Enter(State.Windup);
    }

    void TickWindup(float dt)
    {
        if (player != null) Face(playerFeet, dt, 900f);
        if (stateT >= stateDur)
        {
            if (!playerOnFoot || playerDist > 2.4f || NightShift.CritterImmune(target.client) || Time.time < nextBiteAt) { Enter(State.Attack); return; }
            if (Friendly(target.client)) { Enter(State.Sniff); return; }
            if (target.local && CritterRepellent.TryProtect()) { Enter(State.Sniff); return; }
            Enter(State.Lunge);
        }
    }

    void TickLunge(float dt)
    {
        float k = Mathf.Clamp01(stateT / stateDur);
        Vector3 p = Vector3.Lerp(lungeFrom, lungeTo, k);
        p.y = Mathf.Lerp(lungeFrom.y, lungeTo.y, k) + Mathf.Sin(k * Mathf.PI) * 0.4f;
        if (!Blocked(FlatDirTo(lungeTo), 0.25f)) transform.position = p;
        if (k >= 1f || (playerOnFoot && playerDist < 0.7f))
        {
            if (RaccoonTemper.CanConnect(playerOnFoot, NightShift.CritterImmune(target.client), playerDist,
                playerFeet.y - transform.position.y, 1.4f) && Time.time >= nextBiteAt)
            {
                if (TryLatch()) return;
                Bite();
            }
            Enter(State.Retreat);
        }
    }

    void Bite()
    {
        Vector3 at = Rig.mouth != null ? Rig.mouth.position : transform.position + Vector3.up * 0.4f;
        // The host rolls the infection (§9 rule 2); a guest's machine applies it with the hit (NightNet).
        bool infect = NightDice.Host.Chance(RaccoonTemper.InfectionChance(Rabid));
        var victim = target;
        if (!HitPlayer(1.3f, 0.2f, "CHOMP!", new Color(1f, 0.35f, 0.3f), "bite", 0.3f, infect)) return;
        bites++;
        nextBiteAt = Time.time + RaccoonTemper.BiteCooldown;
        Fx.Burst(at, CritterLook.RabidFur, 10, 2.5f);
        if (victim.local && infect && !GameSession.HasRabies)
        {
            GameSession.Infect(GameClock.TotalHours); // RabiesStatus reacts (toast, shot auto-use, symptoms later)
        }
        NightAudit.Event("bite", new Dictionary<string, object>
        {
            { "critter", NetId }, { "victim", victim.client }, { "local", victim.local }, { "rabid", Rabid }, { "infect", infect },
        });
    }

    /// <summary>Is the target protected by repellent (theirs, wherever they play)?</summary>
    bool TargetRepelled => Friendly(target.client) || (target.local ? CritterRepellent.IsActive : NightNet.IsRepelled(target.client));

    void TickRetreat(float dt)
    {
        Vector3 away = CritterWorld.Flat(transform.position - playerFeet);
        away = away.sqrMagnitude > 0.01f ? away.normalized : -transform.forward;
        // Hop backwards while facing the player (cackling).
        if (!Blocked(away, 0.4f)) transform.position += away * 2.4f * dt * (1f - stateT / Mathf.Max(0.01f, stateDur));
        Face(playerFeet, dt, 600f);
        hopPhase += dt * 10f;
        if (stateT >= stateDur) Enter(bites >= RaccoonTemper.BitesPerEncounter ? State.Flee : State.Attack);
    }

    void TickStunned(float dt)
    {
        Move(Vector3.zero, 0f, dt);
        if (stateT >= stateDur)
        {
            if (player != null) threat = playerFeet;
            Enter(bites >= RaccoonTemper.BitesPerEncounter ? State.Flee : State.Alert);
        }
    }

    void TickSniff(float dt)
    {
        Move(Vector3.zero, 0f, dt);
        Face(playerFeet, dt, 300f);
        if (stateT >= stateDur)
        {
            Vector3 head = Rig.head.position;
            bool pal = Friendly(target.client); // Raccoon calling: it backs off out of respect, not disgust
            GameHUD.FloatingText(head + Vector3.up * 0.45f, pal ? PalWords[Random.Range(0, PalWords.Length)] : EwWords[Random.Range(0, EwWords.Length)], new Color(0.6f, 1f, 0.4f), 1.1f);
            Sfx.Play(pal ? "chitter" : "buzz", head, 0.4f, 1.6f);
            Juice.Squash(Rig.juice, -0.25f, 0.25f);
            if (target.local && !pal) CritterRepellent.TryProtect(); // "it's working" toast (rate-limited)
            threat = playerFeet;
            Enter(State.Flee);
        }
    }

    void TickLeaving(float dt)
    {
        Vector3 away = CritterWorld.Flat(transform.position - (player != null ? playerFeet : transform.position - transform.forward));
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        Move(away.normalized, 2.2f, dt);
        if ((stateT > 1.2f && (!Seen || playerDist > 30f)) || stateT > stateDur) Poof();
    }

    // =====================================================================
    // Events
    // =====================================================================

    public override void OnBagGrabbed(TrashBag bag, TrashCarrier by)
    {
        if (bag == null || Despawning || NetPuppet) return;
        bool mine = bag == Bag || (bag.transform.position - transform.position).sqrMagnitude < 1.6f * 1.6f;
        if (!mine) return;
        Bag = null;
        if (Current == State.Leaving) return;
        if (Current == State.Heist || Current == State.Stowaway || Current == State.Latched || Current == State.Lured) return;
        var pc = by != null ? by.GetComponent<PlayerController>() : target.pc;
        Provoke(pc != null ? NightPlayers.ClientOf(pc) : ulong.MaxValue, by != null ? by.transform.position : playerFeet);
        if (Current != State.Stunned) Enter(TargetRepelled ? State.Sniff : State.Alert);
    }

    // ---------------------------------------------------------------- Raccoon calling: friends of the family

    float friendUntil;
    static readonly string[] PalWords = { "oh. it's you", "*respectful nod*", "boss.", "one of us" };

    /// <summary>Wild raccoons hold nothing against this player (feral, an Alpha) or anybody while charmed.</summary>
    bool Friendly(ulong client) => Time.time < friendUntil || RaccoonStanding.Protects(client);

    /// <summary>Host (Fully Feral's pounce, an Alpha's pack): calm down, give back what it stole and bite nobody for
    /// <paramref name="seconds"/>. <paramref name="backOff"/> also sends it running.</summary>
    public void Befriend(Vector3 from, float seconds, ulong by, bool backOff)
    {
        if (NetPuppet || Despawning || Current == State.Latched || Current == State.Stowaway || Current == State.Lured || Current == State.Leaving) return;
        if (backOff && Time.time < friendUntil) return; // already backing off
        friendUntil = Mathf.Max(friendUntil, Time.time + seconds);
        angryUntil = 0f;
        angryAt = ulong.MaxValue;
        if (Loot != null) DropLoot(Vector3.up * 2.5f, "befriended", by);
        threat = from;
        if (backOff) Enter(State.Flee);
        else if (Current == State.Attack || Current == State.Windup || Current == State.Lunge || Current == State.Retreat || Current == State.Heist) Enter(State.Alert);
    }

    public override void Scare(Vector3 from, bool horn)
    {
        if (Despawning || Current == State.Stunned || NetPuppet) return;
        if (Current == State.Latched) return; // too busy hugging a face
        if (Current == State.Stowaway && Boarded)
        {
            if (horn) GameHUD.FloatingText(transform.position + Vector3.up * 0.6f, "*crunch crunch* (busy)", new Color(0.9f, 0.9f, 0.8f), 0.8f);
            return;
        }
        if (Time.time < nextStartleAt) return; // repeated horn presses cannot keep an encounter permanently disabled
        nextStartleAt = Time.time + RaccoonTemper.StartleCooldown;
        if (Current == State.Heist) { threat = from; startleAt = Time.time; return; } // thief still keeps the loot
        Provoke(RaccoonTemper.InstigatorNear(from), from);
        threat = from;
        Juice.Punch(Rig.juice, 0.35f, 0.3f);
        startledUntil = Time.time + (Rabid ? 0.25f : 0.6f);
        Enter(State.Alert); // cancel the old lunge, then telegraph a fresh attack after the short startle
        if (horn) startleAt = Time.time;
    }

    /// <summary>Upgraded headlights buy a brief flinch from a calm animal, never a flee/despawn or an anger reset.</summary>
    public bool Dazzle(Vector3 from)
    {
        if (NetPuppet || Despawning || Angry || Rabid || Time.time < nextStartleAt ||
            Current != State.Hiding && Current != State.Rummaging && Current != State.Alert) return false;
        nextStartleAt = Time.time + RaccoonTemper.StartleCooldown;
        startledUntil = Time.time + 0.45f;
        threat = from;
        return true;
    }

    float startleAt = -99f;

    /// <summary>Dawn: head home.</summary>
    public void Leave()
    {
        if (Despawning || NetPuppet || Current == State.Leaving || Current == State.Flee || Current == State.Heist) return;
        if (Current == State.Latched) { EndLatch("dawn", ulong.MaxValue, false); return; }
        if (Current == State.Stowaway && Boarded) { LeaveTruck("*BURP* (nap time)"); return; }
        Enter(State.Leaving);
    }

    protected override void OnHitByThrown(Carryable c, Vector3 velocity)
    {
        threat = transform.position - CritterWorld.Flat(velocity);
        if (Current == State.Latched) { EndLatch("bonk", lastHitBy, false); return; }
        if (Current == State.Stowaway && Boarded) { Shoo(lastHitBy, "bonk"); return; }
        Provoke(lastHitBy, threat);
        if (Loot != null) DropLoot(CritterWorld.Flat(velocity) * 0.15f + Vector3.up * 3f, "bonk", lastHitBy);
        Enter(State.Stunned);
    }

    protected override void OnLanded()
    {
        if (Loot != null) DropLoot(Vector3.up * 2.5f, "launched", ulong.MaxValue);
        Enter(State.Stunned);
    }

    protected override void OnAnchorLost()
    {
        if (Current == State.Latched) EndLatch("lost", ulong.MaxValue, false);
        else if (Current == State.Stowaway) { StowTruck = null; Boarded = false; Enter(State.Flee); }
    }

    // =====================================================================
    // F4 night shift: heists, traps, stowaways, latches, puppets
    // =====================================================================

    /// <summary>End of a rummage (host): nick the shiniest thing in the bag (a valuable always; metal/glass sometimes).</summary>
    bool TryHeist()
    {
        if (NetPuppet || Rabid || Bag == null || Bag.Contents == null || Bag.Contents.items == null) return false;
        var items = Bag.Contents.items;
        int best = -1;
        float bestScore = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var def = it != null ? it.Def : null;
            if (def == null) continue;
            float score = def.category == ItemCategory.Valuable ? 1000f + it.value
                        : def.category == ItemCategory.Metal || def.category == ItemCategory.Glass ? 1f + it.massKg * 0.1f : 0f;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return false;
        if (bestScore < 1000f && !forceHeist && !NightDice.Host.Chance(ShinyHeistChance)) return false;
        var loot = items[best];
        items.RemoveAt(best);
        string from = Bag.Contents.foundAt;
        Bag = null;
        StartHeist(loot, from);
        return true;
    }

    bool forceHeist;

    /// <summary>Debug / bots: rob its bag right now (the loot still comes out of the bag's real contents).</summary>
    public bool DebugHeist()
    {
        forceHeist = true;
        bool ok = TryHeist();
        forceHeist = false;
        return ok;
    }

    /// <summary>Host (and the debug menu): run off with <paramref name="loot"/>.</summary>
    public void StartHeist(ItemInstance loot, string from)
    {
        if (loot == null || NetPuppet) return;
        Loot = loot;
        threat = playerFeet;
        Enter(State.Heist);
        SetLootLook(loot.defId);
        NightNet.HostCritterChanged(this);
        NightAudit.Event("heist", new Dictionary<string, object>
        {
            { "op", "steal" }, { "uid", loot.uid }, { "def", loot.defId }, { "critter", NetId },
            { "cents", Mathf.RoundToInt(loot.value * 100f) }, { "from", from ?? "" },
        });
    }

    /// <summary>Everyone: show (or clear) the loot in its mouth; a toast says what it took.</summary>
    public void SetLootLook(string defId)
    {
        defId = string.IsNullOrEmpty(defId) ? null : defId;
        if (defId == LootDefId) return;
        bool had = LootDefId != null;
        LootDefId = defId;
        if (lootVisual != null) { Destroy(lootVisual); lootVisual = null; }
        if (defId == null)
        {
            if (had && !Despawning)
            {
                Vector3 at = transform.position + Vector3.up * 0.9f;
                GameHUD.FloatingText(at, "DROPPED IT!", LootGold, 1.3f);
                Sfx.Play("sparkle", at, 0.8f, 1f);
                Fx.Burst(at, LootGold, 14, 3f);
            }
            return;
        }
        var def = ItemCatalog.Get(defId);
        if (def == null || Rig == null) return;
        Transform at2 = Rig.mouth != null ? Rig.mouth : Rig.head;
        lootVisual = ItemVisuals.Build(def, at2);
        foreach (var col in lootVisual.GetComponentsInChildren<Collider>()) Destroy(col);
        lootVisual.transform.localPosition = new Vector3(0f, -0.04f, 0.1f);
        // Mouth-sized whatever it is: 22 cm across in the world (a ring and a toaster both fit).
        var rends = lootVisual.GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            var bb = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) bb.Encapsulate(rends[i].bounds);
            float size = Mathf.Max(bb.size.x, bb.size.y, bb.size.z);
            if (size > 0.001f) lootVisual.transform.localScale *= 0.22f / size;
        }
        foreach (var tr in lootVisual.GetComponentsInChildren<Transform>()) tr.gameObject.layer = CritterWorld.CritterLayer;
        Fx.Sparkle(lootVisual.transform, LootGold, 1.5f);
        if (def.category == ItemCategory.Valuable && LocalDist() < 45f)
            GameHUD.Toast($"A raccoon swiped a {def.name}! Bonk it so it drops it!", ToastStyle.Info, 3f);
        else if (LocalDist() < 30f)
            GameHUD.FloatingText(transform.position + Vector3.up * 1f, $"*yoink* {def.name}", LootGold, 1f);
    }

    void DropLoot(Vector3 vel, string how, ulong by)
    {
        if (Loot == null) return;
        var item = Loot;
        Loot = null;
        Vector3 at = (Rig.mouth != null ? Rig.mouth.position : transform.position + Vector3.up * 0.4f) + Vector3.up * 0.25f;
        try { LooseItem.Spawn(item, at, Quaternion.identity, vel, true); } // the host's loose items are shared (N2 publishes them)
        catch (System.Exception e) { Debug.LogException(e); }
        SetLootLook(null);
        NightNet.HostCritterChanged(this);
        NightAudit.Event("heist", new Dictionary<string, object>
        {
            { "op", "drop" }, { "uid", item.uid }, { "def", item.defId }, { "critter", NetId }, { "how", how }, { "by", (long)by },
        });
    }

    void TickHeist(float dt)
    {
        if (player != null && playerDist < 14f) threat = playerFeet;
        Vector3 away = CritterWorld.Flat(transform.position - threat);
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        away = Quaternion.Euler(0f, Mathf.Sin(Time.time * 5f + GetInstanceID()) * 30f, 0f) * away.normalized;
        float speed = Time.time - startleAt < 3f ? FleeSpeed * 1.15f : FleeSpeed * 0.82f;
        Move(away, speed, dt);
        TrailSparkle();
        if ((stateT > 8f && playerDist > 28f) || stateT > stateDur) Escape();
    }

    void TrailSparkle()
    {
        if (Time.time < nextSparkle || LootDefId == null) return;
        nextSparkle = Time.time + 0.18f;
        Fx.Burst(transform.position + Vector3.up * 0.35f + NightDice.Cosmetic.OnCircle(0.15f), LootGold, 2, 0.6f);
    }

    void Escape()
    {
        var item = Loot;
        string name = item != null && item.Def != null ? item.Def.name : "something shiny";
        NightAudit.Event("heist", new Dictionary<string, object>
        {
            { "op", "escape" }, { "uid", item != null ? item.uid : "" }, { "def", item != null ? item.defId : "" }, { "critter", NetId },
        });
        NightNet.HostToastNear(transform.position, 60f, $"The raccoon got away with the {name}!", ToastStyle.Bad);
        Loot = null;
        Poof();
    }

    float LocalDist()
    {
        var me = NightPlayers.LocalPlayer;
        return me != null ? CritterWorld.Flat(me.transform.position - transform.position).magnitude : 999f;
    }

    // ---------------------------------------------------------------- traps

    public bool TryLure(RaccoonTrap trap)
    {
        if (NetPuppet || Despawning || Airborne || trap == null || LureTrap != null || HasAnchor) return false;
        if (Current != State.Hiding && Current != State.Rummaging && Current != State.Flee && Current != State.Leaving) return false;
        LureTrap = trap;
        Bag = null;
        Enter(State.Lured);
        return true;
    }

    void TickLured(float dt)
    {
        if (LureTrap == null || !LureTrap.Armed) { LureTrap = null; threat = playerFeet; Enter(State.Flee); return; }
        if (playerOnFoot && playerDist < 1.2f) { LureTrap = null; Provoke(target.client, playerFeet); Enter(State.Alert); return; }
        if (MoveTo(LureTrap.Mouth, 2.4f, dt, 0.45f))
        {
            var trap = LureTrap;
            LureTrap = null;
            trap.HostCatch(this);
            return;
        }
        if (stateT > stateDur) { LureTrap = null; Enter(State.Flee); }
    }

    // ---------------------------------------------------------------- stowaways

    /// <summary>Host: sneak into this (slow, loaded) truck's hopper.</summary>
    public bool TryStowaway(TruckControllerV2 truck)
    {
        if (NetPuppet || Despawning || Airborne || Angry || Rabid || truck == null || Loot != null || HasAnchor) return false;
        if (Current != State.Hiding && Current != State.Rummaging && Current != State.Alert && Current != State.Leaving) return false;
        StowTruck = truck;
        Boarded = false;
        Bag = null;
        Enter(State.Stowaway);
        return true;
    }

    void TickStowaway(float dt)
    {
        if (StowTruck == null) { Boarded = false; threat = playerFeet; Enter(State.Flee); return; }
        if (!Boarded)
        {
            Vector3 mouth = StowTruck.transform.TransformPoint(new Vector3(0f, 0f, -5f));
            mouth.y = CritterWorld.GroundY(mouth, transform.position.y);
            if (MoveTo(mouth, FleeSpeed, dt, 0.8f)) Board();
            else if (stateT > 9f || CritterWorld.VelocityOf(StowTruck).magnitude > 4f) { StowTruck = null; threat = playerFeet; Enter(State.Flee); }
            return;
        }
        if (Time.time >= nextNom) { nextNom = Time.time + NomEvery; Nom(); }
        if (noms >= MaxNoms || stateT > stateDur || !GameClock.IsNight) LeaveTruck("*BURP*");
    }

    void Board()
    {
        Boarded = true;
        SetAnchor(StowTruck.transform, StowLocal, Quaternion.Euler(0f, 180f, 0f));
        NightNet.HostCritterChanged(this);
        Vector3 at = transform.position + Vector3.up * 0.5f;
        Sfx.Play("rustle", at, 0.9f, 1.2f);
        NightNet.Say(at, "*hops into the hopper*", 1);
        NightNet.HostToastNear(transform.position, 35f, "A raccoon jumped into a truck's hopper and is EATING THE CARGO! Hop on the back and shoo it (E)!", ToastStyle.Bad);
        NightAudit.Event("stowaway", new Dictionary<string, object> { { "op", "board" }, { "critter", NetId }, { "truck", TruckNetId(StowTruck) } });
    }

    void Nom()
    {
        string ate = NightCargo.EatOne(StowTruck);
        Vector3 at = transform.position + Vector3.up * 0.7f;
        if (ate == null) { noms += 2; NightNet.Say(at, "*sniff* ...empty?", 0); return; }
        noms++;
        Juice.Squash(Rig.juice, 0.18f, 0.2f);
        Sfx.Play("bite", at, 0.6f, 1.4f);
        NightNet.Say(at, $"*NOM* -{ate}", 2);
        NightAudit.Event("stowaway", new Dictionary<string, object> { { "op", "eat" }, { "critter", NetId }, { "item", ate }, { "truck", TruckNetId(StowTruck) } });
    }

    void LeaveTruck(string word)
    {
        var t = StowTruck;
        ClearAnchor();
        Boarded = false;
        StowTruck = null;
        NightNet.HostCritterChanged(this);
        Vector3 side = t != null ? t.transform.right * (NightDice.Host.Chance(0.5f) ? 1f : -1f) : transform.right;
        Vector3 carry = t != null ? CritterWorld.VelocityOf(t) * 0.6f : Vector3.zero;
        NightNet.Say(transform.position + Vector3.up * 0.8f, word, 1);
        bites = 2; // done here: off it goes once it lands
        Launch(side * 3.5f + Vector3.up * 5.5f + carry);
    }

    /// <summary>Host: a stowaway gets shooed out (E near, or a bonk).</summary>
    public void Shoo(ulong by, string how)
    {
        if (Current != State.Stowaway || !Boarded) return;
        NightAudit.Event("stowaway", new Dictionary<string, object> { { "op", "shoo" }, { "critter", NetId }, { "by", (long)by }, { "how", how }, { "ate", noms } });
        LeaveTruck(NightDice.Cosmetic.Pick(ShooWords));
        Provoke(by, transform.position);
        bites = 0; // shooing is a warning and an escape window, not a permanent pacification
    }

    static readonly string[] ShooWords = { "EEK! FINE!", "SHOO'D!", "*indignant chitter*", "rude!!" };

    static ulong TruckNetId(TruckControllerV2 t) => t != null && t.NetworkObject != null && t.NetworkObject.IsSpawned ? t.NetworkObject.NetworkObjectId : 0;

    // ---------------------------------------------------------------- latch

    bool forceLatch;

    /// <summary>Debug / tests: latch onto the nearest player on foot right now; immunity still applies.</summary>
    public bool DebugLatch()
    {
        forceLatch = true;
        bool ok = TryLatch();
        forceLatch = false;
        return ok;
    }

    bool TryLatch()
    {
        if ((!Rabid && !Angry && !forceLatch) || NetPuppet || !playerOnFoot || target.pc == null || Time.time < nextBiteAt) return false;
        if (NightShift.CritterImmune(target.client) || NightLatch.IsLatched(target.client)) return false;
        if (TargetRepelled) return false;
        if (!forceLatch && !NightDice.Host.Chance(RaccoonTemper.LatchChance(Rabid))) return false;
        LatchVictim = target.client;
        latchPc = target.pc;
        AttachToFace(this, latchPc);
        Enter(State.Latched);
        NightNet.HostCritterChanged(this);
        NightNet.HostLatch(LatchVictim, this, true, false);
        NightAudit.Event("latch", new Dictionary<string, object> { { "op", "start" }, { "victim", (long)LatchVictim }, { "critter", NetId } });
        return true;
    }

    void TickLatched(float dt)
    {
        if (latchPc == null || !latchPc.isActiveAndEnabled) { EndLatch("lost", ulong.MaxValue, false); return; }
        if (stateT >= stateDur) EndLatch("timeout", ulong.MaxValue, NightDice.Host.Chance(RaccoonTemper.InfectionChance(Rabid)));
    }

    /// <summary>Host: let go of the victim (mash, a friend's bonk, timeout = the rabies roll).</summary>
    public void EndLatch(string how, ulong by, bool infect)
    {
        if (Current != State.Latched) return;
        ulong victim = LatchVictim;
        var pc = latchPc;
        float held = stateT;
        ClearAnchor();
        latchPc = null;
        LatchVictim = ulong.MaxValue;
        NightNet.HostLatch(victim, this, false, infect);
        NightShift.GrantCritterImmunity(victim);
        bites = 2; // flees once it lands
        nextBiteAt = Time.time + RaccoonTemper.BiteCooldown;
        Vector3 back = pc != null ? -CritterWorld.Flat(pc.transform.forward).normalized : -transform.forward;
        if (back.sqrMagnitude < 0.01f) back = Vector3.back;
        Enter(State.Stunned); // looks: flipped while flying; landing re-enters Stunned then Flee
        NightNet.HostCritterChanged(this);
        string word = how == "mash" ? "*shaken off!*" : how == "timeout" ? "*lets go*" : how == "dawn" ? "*yawns, lets go*" : "BONKED OFF!";
        NightNet.Say(transform.position + Vector3.up * 0.6f, word, 1);
        Launch(back * 3.2f + Vector3.up * 5.5f);
        NightAudit.Event("latch", new Dictionary<string, object>
        {
            { "op", "end" }, { "victim", (long)victim }, { "critter", NetId }, { "how", how }, { "by", (long)by },
            { "held", System.Math.Round(held, 2) }, { "infect", infect },
        });
    }

    /// <summary>Host: someone pressed E on this raccoon: shoo a stowaway, or bonk a latched one off a friend.</summary>
    public void Poke(ulong by)
    {
        if (Current == State.Stowaway && Boarded) Shoo(by, "poke");
        else if (Current == State.Latched && by != LatchVictim) EndLatch("friend", by, false);
    }

    /// <summary>Can this machine's player poke it right now (prompt)?</summary>
    public string PokePrompt(ulong me)
    {
        if (Current == State.Stowaway && Boarded) return "Shoo the raccoon out of the hopper!";
        if (Current == State.Latched && LatchVictim != me && LatchVictim != ulong.MaxValue)
            return $"Bonk the raccoon off {HandsNet.NameOf(LatchVictim)}!";
        return null;
    }

    // ---------------------------------------------------------------- puppets (guests)

    public override byte NetState => (byte)Current;
    public override byte NetFlags => (byte)((Rabid ? 1 : 0) | (LootDefId != null ? 2 : 0) | (Boarded ? 4 : 0));

    public override void ApplyNetState(byte state, byte flags)
    {
        if (state > (byte)State.Latched) return;
        Boarded = (flags & 4) != 0;
        var s = (State)state;
        if (s != Current) Enter(s);
        if (s != State.Latched) LatchVictim = ulong.MaxValue;
    }

    /// <summary>Guest: who it's latched onto (for the poke prompt).</summary>
    public void PuppetLatch(ulong victim) => LatchVictim = victim;

    protected override void TickPuppet(float dt)
    {
        stateT += dt;
        if (Current == State.Heist) TrailSparkle();
        Animate(dt);
    }

    void OnDestroy()
    {
        if (lootVisual != null) Destroy(lootVisual);
    }

    // =====================================================================
    // Procedural animation
    // =====================================================================

    void Animate(float dt)
    {
        float t = Time.time + GetInstanceID() * 0.137f;
        bool moving = speedNow > 0.25f;
        bool peeking = Time.time < peekUntil;

        float rear = 0f, crouch = 0f, lean = 0f, mouth = 0.015f, flip = 0f;
        float hopH = Mathf.Lerp(0.03f, 0.14f, Mathf.InverseLerp(0.5f, FleeSpeed, speedNow));
        switch (Current)
        {
            case State.Hiding:
                crouch = peeking ? -0.02f : -0.14f;
                rear = peeking ? -38f : 0f;
                break;
            case State.Rummaging:
                rear = 12f + Mathf.Sin(t * 16f) * 10f; // head-first in the bag
                break;
            case State.Alert:
                rear = -72f;
                mouth = 0.05f;
                break;
            case State.Flee:
                lean = 14f;
                break;
            case State.Attack:
                lean = 10f;
                mouth = 0.045f + Mathf.Sin(t * 20f) * 0.01f;
                break;
            case State.Windup:
                crouch = -0.08f;
                rear = 16f;
                mouth = 0.06f;
                break;
            case State.Lunge:
                rear = -25f;
                mouth = 0.07f;
                break;
            case State.Retreat:
                rear = -30f;
                mouth = 0.04f;
                break;
            case State.Stunned:
                flip = 1f;
                break;
            case State.Sniff:
                rear = -30f + Mathf.Sin(t * 14f) * 6f;
                break;
            case State.Leaving:
                mouth = stateT < 1.2f ? 0.07f : 0.015f; // yawn
                break;
            case State.Heist:
                lean = 18f;
                mouth = 0.035f;
                break;
            case State.Lured:
                rear = moving ? 0f : -20f + Mathf.Sin(t * 12f) * 6f;
                break;
            case State.Stowaway:
                if (Boarded) { rear = 14f + Mathf.Sin(t * 15f) * 12f; crouch = -0.06f; mouth = 0.03f + Mathf.Abs(Mathf.Sin(t * 9f)) * 0.03f; }
                break;
            case State.Latched:
                rear = -70f + Mathf.Sin(t * 22f) * 8f;
                mouth = 0.06f;
                break;
        }

        animRear = Damp(animRear, rear, Current == State.Alert || Current == State.Lunge ? 18f : 10f, dt);
        animCrouch = Damp(animCrouch, crouch, 10f, dt);
        animLean = Damp(animLean, lean + Mathf.Clamp(speedNow * 2f, 0f, 8f), 8f, dt);
        animMouth = Damp(animMouth, mouth, 16f, dt);
        animFlip = Damp(animFlip, flip, 9f, dt);

        // Body hop + squash & stretch while running, breathing when idle.
        float hop = moving ? Mathf.Abs(Mathf.Sin(hopPhase)) * hopH : 0f;
        float squash = moving ? Mathf.Cos(hopPhase * 2f) * 0.07f : Mathf.Sin(t * 2.4f) * 0.025f;
        if (Current == State.Windup) squash = -0.14f + Mathf.Sin(t * 60f) * 0.03f;
        float startle = Time.time - startleAt;
        if (startle < 0.45f) { hop += Mathf.Sin(startle / 0.45f * Mathf.PI) * 0.7f; squash -= 0.2f * Mathf.Sin(startle / 0.45f * Mathf.PI); }
        Rig.visual.localPosition = new Vector3(0f, hop + animCrouch + animFlip * 0.48f, 0f);
        Rig.visual.localScale = new Vector3(1f - squash * 0.5f, 1f + squash, 1f - squash * 0.5f);
        Rig.visual.localRotation = Quaternion.Euler(animLean, 0f, animFlip * 180f + (moving ? Mathf.Sin(hopPhase) * 6f : 0f));
        Rig.rear.localRotation = Quaternion.Euler(animRear, 0f, 0f);

        // Head: looks at the player when they're around; rummaging bobs it into the bag; rabid ones twitch.
        float yaw = 0f, pitch = 0f;
        if (player != null && playerDist < 14f && Current != State.Rummaging && Current != State.Stunned)
        {
            Vector3 local = transform.InverseTransformPoint(PlayerHead);
            yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -55f, 55f);
            pitch = Mathf.Clamp(-Mathf.Atan2(local.y - 0.4f, Mathf.Max(0.5f, local.z)) * Mathf.Rad2Deg * 0.5f, -25f, 20f);
        }
        else if (Current == State.Hiding && peeking) yaw = Mathf.Sin(t * 1.7f) * 35f;
        if (Current == State.Rummaging) pitch = 30f + Mathf.Sin(t * 18f) * 14f;
        animHeadYaw = Damp(animHeadYaw, yaw, 10f, dt);
        animHeadPitch = Damp(animHeadPitch, pitch, 10f, dt);
        if (Rabid && Time.time >= nextTwitch)
        {
            nextTwitch = Time.time + Random.Range(0.25f, 1.1f);
            twitchEuler = new Vector3(Random.Range(-12f, 12f), Random.Range(-18f, 18f), Random.Range(-25f, 25f));
            Rig.KickEyes(1.5f);
        }
        twitchEuler = Damp(twitchEuler, Vector3.zero, 9f, dt);
        // Counter the body's rear-up pitch so the face keeps looking ahead (not at the sky).
        Rig.head.localRotation = Quaternion.Euler(animHeadPitch - animRear * 0.85f + twitchEuler.x, animHeadYaw + twitchEuler.y, twitchEuler.z);

        if (Rig.mouthShape != null) Rig.mouthShape.localScale = new Vector3(0.07f, animMouth, 0.04f);

        // Tail: lazy swish, straight up in a panic, wiggly when stunned.
        float wagSpeed = Current == State.Flee || Current == State.Attack ? 14f : Current == State.Stunned ? 22f : 3f;
        float wagAmp = Current == State.Hiding ? 22f : Current == State.Flee ? 12f : 16f;
        float curl = Current == State.Flee ? -14f : Current == State.Alert ? -8f : 0f;
        for (int i = 0; i < Rig.tail.Count; i++)
        {
            float w = Mathf.Sin(t * wagSpeed - i * 0.7f) * wagAmp * (0.4f + i * 0.12f);
            Rig.tail[i].localRotation = Rig.tailRest[i] * Quaternion.Euler(curl + Mathf.Sin(t * wagSpeed * 0.5f - i) * 4f, w, 0f);
        }

        // Paws: little steps when moving, frantic air-running when stunned.
        for (int i = 0; i < Rig.paws.Count; i++)
        {
            float ph = hopPhase * 1.6f + (i % 2 == 0 ? 0f : Mathf.PI) + (i < 2 ? 0f : Mathf.PI * 0.5f);
            float up = moving ? Mathf.Max(0f, Mathf.Sin(ph)) * 0.07f : 0f;
            float fwd = moving ? Mathf.Cos(ph) * 0.05f : 0f;
            if (Current == State.Stunned) { up = Mathf.Abs(Mathf.Sin(t * 25f + i)) * 0.08f; fwd = 0f; }
            if (Current == State.Alert && i < 2) { up = 0.18f + Mathf.Sin(t * 9f + i) * 0.02f; fwd = -0.05f; } // tiny T-rex hands up
            Rig.paws[i].localPosition = Rig.pawRest[i] + new Vector3(0f, up, fwd);
        }

        // Ears: perk up when alert, twitch now and then.
        for (int i = 0; i < Rig.ears.Count; i++)
        {
            float perk = Current == State.Alert || Current == State.Attack ? -12f : 0f;
            float twitch = Mathf.PerlinNoise(t * 1.3f, i * 7f) > 0.78f ? Mathf.Sin(t * 40f) * 12f : 0f;
            Rig.ears[i].localRotation = Rig.earRest[i] * Quaternion.Euler(perk + twitch, 0f, 0f);
        }

        if (foam != null) foam.rate = Current == State.Attack || Current == State.Lunge || Current == State.Windup ? 10f : 2.5f;
    }
}

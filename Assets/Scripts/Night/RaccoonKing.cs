using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Raccoon King (F4, Docs/Multiplayer.md §14.4): a rare night event. A giant crowned raccoon thuds down on Depot Row and
/// raids it: it lumbers after whoever is nearest on foot, remembers attackers and swipes them flying (rabies risk, 5 s immunity after). Every business
/// teams up: each thrown thing that bonks it is a hit (HP = 8 + 4 per player). When it's dethroned the crown pops off,
/// every contributor gets a <b>Raccoon King's Crown</b> trophy for their shelves and a cash pot is split by hits, through the
/// Ledger to each business. At dawn an undefeated King waddles off. Host-simulated; guests see a puppet (<see cref="NightNet"/>).
/// </summary>
public class RaccoonKing : Critter
{
    public enum State : byte { Arrive, Roam, Windup, Swipe, Roar, Flinch, Dethroned, Leaving }

    public const float Scale = 3.2f;
    public const float SwipeRange = 3.2f;
    public const string TrophyDefId = "raccoon_king_crown";

    public State Phase { get; private set; } = State.Arrive;
    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public override string DisplayName => "The Raccoon King";
    protected override bool KeepOutOfLots => false; // he raids Depot Row (shop interiors stay off limits)

    readonly Dictionary<ulong, int> hits = new Dictionary<ulong, int>();
    public IReadOnlyDictionary<ulong, int> Hits => hits;

    float stateT, stateDur, nextRoar, animRear, animSwipe;
    ulong angryAt = ulong.MaxValue;
    float angryUntil, nextSwipe;
    Vector3 wander;
    Transform crown;

    static readonly string[] RoarWords = { "ROOOAAAR!", "BOW BEFORE ME!", "ALL TRASH IS MINE!", "*royal chittering*" };
    static readonly string[] OwWords = { "OOF!", "HOW DARE YOU", "MY CROWN!", "*royal outrage*", "PEASANT!" };

    public static RaccoonKing Spawn(Vector3 pos, int maxHp)
    {
        var go = new GameObject("The Raccoon King");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, NightDice.Host.Range(0f, 360f), 0f));
        var k = go.AddComponent<RaccoonKing>();
        k.Init(maxHp);
        return k;
    }

    public static RaccoonKing SpawnPuppet(ushort id, Vector3 pos, float yaw, byte state)
    {
        var go = new GameObject("The Raccoon King (net)");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        var k = go.AddComponent<RaccoonKing>();
        k.MakePuppet(id);
        k.Init(1);
        k.NetSnapshot(pos, yaw, 0.2f);
        k.ApplyNetState(state, 0);
        return k;
    }

    void Init(int maxHp)
    {
        MaxHp = Hp = Mathf.Max(1, maxHp);
        stepRadius = 0.6f;
        turnRate = 160f;
        Rig = CritterLook.BuildRaccoon(gameObject, false);
        Rig.visual.localScale = Vector3.one * Scale;
        var col = GetComponent<SphereCollider>();
        if (col != null) { col.radius *= Scale; col.center *= Scale; }
        // The crown.
        var gold = CritterLook.Mat(new Color(1f, 0.8f, 0.2f), 0.85f, 0.4f);
        var gem = CritterLook.Mat(new Color(0.9f, 0.1f, 0.25f), 0.9f, 0.8f);
        crown = CritterLook.Node(Rig.head, "Crown", new Vector3(0f, 0.2f, 0f));
        CritterLook.Part(crown, PrimitiveType.Cylinder, "Band", Vector3.zero, new Vector3(0.24f, 0.05f, 0.24f), gold);
        for (int i = 0; i < 5; i++)
        {
            float a = i * 72f * Mathf.Deg2Rad;
            CritterLook.Part(crown, PrimitiveType.Cube, "Point", new Vector3(Mathf.Cos(a) * 0.1f, 0.07f, Mathf.Sin(a) * 0.1f),
                             new Vector3(0.04f, 0.09f, 0.04f), gold, new Vector3(0f, -i * 72f, 45f));
        }
        CritterLook.Part(crown, PrimitiveType.Sphere, "Gem", new Vector3(0f, 0.02f, 0.12f), Vector3.one * 0.05f, gem);
        Juice.PopIn(Rig.juice, 0.5f);
        Enter(State.Arrive);
    }

    void Enter(State s)
    {
        Phase = s;
        stateT = 0f;
        Vector3 head = Rig.head != null ? Rig.head.position : transform.position + Vector3.up * 1.5f;
        switch (s)
        {
            case State.Arrive:
                stateDur = 2f;
                Sfx.Play("thud", transform.position, 1f, 0.5f);
                Sfx.Play("raccoon_scream", head, 1f, 0.55f);
                Fx.Dust(transform.position, 3f);
                Juice.Shake(0.35f, 0.5f);
                GameHUD.FloatingText(head + Vector3.up * 1.2f, "THE RACCOON KING HAS ARRIVED", new Color(1f, 0.8f, 0.2f), 2f);
                break;
            case State.Roam:
                stateDur = NightDice.Cosmetic.Range(5f, 9f);
                break;
            case State.Windup:
                stateDur = 0.6f;
                Sfx.Play("hiss", head, 1f, 0.5f);
                break;
            case State.Swipe:
                stateDur = 0.35f;
                Sfx.Play("whoosh", head, 1f, 0.6f);
                break;
            case State.Roar:
                stateDur = 1.6f;
                Sfx.Play("raccoon_scream", head, 1f, 0.5f);
                GameHUD.FloatingText(head + Vector3.up * 1.2f, NightDice.Cosmetic.Pick(RoarWords), new Color(1f, 0.7f, 0.2f), 1.8f);
                Juice.Shake(0.2f, 0.4f);
                break;
            case State.Flinch:
                stateDur = 0.5f;
                Sfx.Play("squeak", head, 1f, 0.5f);
                GameHUD.FloatingText(head + Vector3.up * 1f, NightDice.Cosmetic.Pick(OwWords), new Color(1f, 0.5f, 0.3f), 1.4f);
                Juice.Squash(Rig.juice, 0.25f, 0.3f);
                Rig.KickEyes(8f);
                break;
            case State.Dethroned:
                stateDur = 3.5f;
                Sfx.Play("raccoon_scream", head, 1f, 0.7f);
                Sfx.Play("cash", head, 1f, 1f);
                GameHUD.FloatingText(head + Vector3.up * 1.4f, "DETHRONED!!", new Color(1f, 0.85f, 0.3f), 2.2f);
                Fx.Burst(head, new Color(1f, 0.85f, 0.25f), 40, 6f);
                DizzyStars.Attach(Rig.head, 3f, 0.5f, 0.3f);
                PopCrown(); // visual drop on every peer; contributor trophies are awarded separately by the host
                break;
            case State.Leaving:
                stateDur = 12f;
                GameHUD.FloatingText(head + Vector3.up * 1.2f, "I SHALL RETURN!", new Color(0.8f, 0.85f, 1f), 1.6f);
                break;
        }
    }

    void PopCrown()
    {
        if (crown == null) return;
        var popped = crown;
        crown = null;
        popped.SetParent(null, true);
        popped.gameObject.layer = CritterWorld.DefaultLayer;
        var collider = popped.gameObject.AddComponent<SphereCollider>();
        collider.radius = 0.23f;
        var body = popped.gameObject.AddComponent<Rigidbody>();
        body.mass = 0.3f;
        body.linearVelocity = Vector3.up * 5.5f + transform.forward * 2f + NightDice.Cosmetic.OnCircle(1.5f);
        body.angularVelocity = new Vector3(7f, 10f, 4f);
        Destroy(popped.gameObject, 3f); // a cosmetic pop, never a second claimable trophy
    }

    protected override void Tick(float dt)
    {
        FocusAggressor();
        stateT += dt;
        switch (Phase)
        {
            case State.Arrive:
                if (stateT >= stateDur) Enter(State.Roam);
                break;
            case State.Roam:
                TickRoam(dt);
                break;
            case State.Windup:
                if (player != null) Face(playerFeet, dt, 200f);
                if (stateT >= stateDur) { Enter(State.Swipe); DoSwipe(); }
                break;
            case State.Swipe:
            case State.Flinch:
                Move(Vector3.zero, 0f, dt);
                if (stateT >= stateDur) Enter(State.Roam);
                break;
            case State.Roar:
                Move(Vector3.zero, 0f, dt);
                if (stateT >= stateDur) Enter(State.Roam);
                break;
            case State.Dethroned:
                Move(Vector3.zero, 0f, dt);
                if (stateT >= stateDur) Poof();
                break;
            case State.Leaving:
                Vector3 away = CritterWorld.Flat(transform.position - playerFeet);
                Move(away.sqrMagnitude > 0.01f ? away.normalized : transform.forward, 3f, dt);
                if (stateT >= stateDur || (playerDist > 40f && stateT > 3f)) Poof();
                break;
        }
        Animate(dt);
    }

    void TickRoam(float dt)
    {
        if (!GameClock.IsNight) { Enter(State.Leaving); RaccoonKingEvent.HostEnded(this, false); return; }
        if (Time.time >= nextRoar && stateT > 2f) { nextRoar = Time.time + NightDice.Host.Range(10f, 16f); Enter(State.Roar); return; }
        if (playerOnFoot && playerDist < 30f)
        {
            if (playerDist < SwipeRange - 0.4f && Time.time >= nextSwipe && !NightShift.CritterImmune(target.client)) { Enter(State.Windup); return; }
            Move(FlatDirTo(playerFeet), playerDist < SwipeRange - 0.6f ? 0f : 3.3f, dt);
            return;
        }
        if (wander == Vector3.zero || CritterWorld.Flat(wander - transform.position).magnitude < 2f || stateT > stateDur)
        {
            wander = RaccoonKingEvent.Home + NightDice.Host.OnCircle(NightDice.Host.Range(4f, 18f));
            stateT = 0f;
        }
        MoveTo(wander, 1.8f, dt, 1f);
    }

    void DoSwipe()
    {
        nextSwipe = Time.time + 2.6f;
        // Everyone in front of him within range goes flying (each machine's own player moves itself).
        var all = NightPlayers.All;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc == null) continue;
            var t = NightPlayers.Describe(pc, transform.position);
            if (!RaccoonTemper.CanConnect(t.onFoot, NightShift.CritterImmune(t.client), t.dist,
                t.feet.y - transform.position.y, SwipeRange)) continue;
            Vector3 dir = CritterWorld.Flat(t.feet - transform.position).normalized;
            if (Vector3.Dot(dir, transform.forward) < -0.2f) continue;
            Vector3 src = Rig.head != null ? Rig.head.position : transform.position;
            bool infect = NightDice.Host.Chance(RaccoonTemper.InfectionChance(false, true));
            bool ok = t.local
                ? CritterKnockback.HitLocalPlayer(dir, 2.4f, 0.5f, "ROYAL CHOMP!", new Color(1f, 0.8f, 0.2f), "bite", 0.35f, src)
                : NightNet.HostHitRemote(t.client, dir, 2.4f, 0.5f, "ROYAL CHOMP!", new Color(1f, 0.8f, 0.2f), "bite", 0.35f, src, infect);
            if (ok)
            {
                NightShift.GrantCritterImmunity(t.client);
                if (t.local && infect && !GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
                NightAudit.Event("king-bite", new Dictionary<string, object> { { "victim", t.client }, { "infect", infect } });
            }
        }
    }

    void FocusAggressor()
    {
        if (Time.time >= angryUntil || angryAt == ulong.MaxValue) return;
        var pc = NightPlayers.ForClient(angryAt);
        if (pc == null || NightPlayers.IsIndoors(pc)) { angryUntil = 0f; return; }
        var t = NightPlayers.Describe(pc, transform.position);
        if (t.dist > 30f) { angryUntil = 0f; return; }
        target = t; player = t.pc; playerOnFoot = t.onFoot; playerDriving = t.driving;
        playerFeet = t.feet; playerDist = t.dist;
    }

    protected override void OnHitByThrown(Carryable c, Vector3 velocity) => TakeHit(lastHitBy, 1);

    protected override void OnLanded() => TakeHit(ulong.MaxValue, 2);

    void TakeHit(ulong by, int dmg)
    {
        if (Phase == State.Dethroned || Phase == State.Leaving || NetPuppet) return;
        Hp = Mathf.Max(0, Hp - dmg);
        if (by != ulong.MaxValue) { angryAt = by; angryUntil = Time.time + RaccoonTemper.AngerSeconds; FocusAggressor(); }
        if (by != ulong.MaxValue) hits[by] = (hits.TryGetValue(by, out int n) ? n : 0) + dmg;
        NightAudit.Event("king", new Dictionary<string, object> { { "op", "hit" }, { "by", (long)by }, { "hp", Hp }, { "max", MaxHp } });
        RaccoonKingEvent.HostChanged(this);
        if (Hp <= 0) { Enter(State.Dethroned); RaccoonKingEvent.HostEnded(this, true); return; }
        Enter(State.Flinch);
    }

    public override void Scare(Vector3 from, bool horn)
    {
        if (NetPuppet || Despawning || Phase == State.Dethroned) return;
        if (horn) GameHUD.FloatingText(transform.position + Vector3.up * 4f, "a horn? HOW QUAINT.", new Color(1f, 0.85f, 0.5f), 1.2f);
    }

    /// <summary>Dawn: the King waddles off (undefeated).</summary>
    public void Leave()
    {
        if (NetPuppet || Phase == State.Dethroned || Phase == State.Leaving) return;
        Enter(State.Leaving);
    }

    // ---------------------------------------------------------------- puppets

    public override byte NetState => (byte)Phase;

    public override void ApplyNetState(byte state, byte flags)
    {
        if (state > (byte)State.Leaving) return;
        if ((State)state != Phase) Enter((State)state);
    }

    protected override void TickPuppet(float dt)
    {
        stateT += dt;
        Animate(dt);
    }

    // ---------------------------------------------------------------- looks

    void Animate(float dt)
    {
        float t = Time.time;
        bool moving = speedNow > 0.25f;
        float rear = Phase == State.Roar ? -60f : Phase == State.Windup ? -35f : Phase == State.Dethroned ? 0f : 0f;
        float swipe = Phase == State.Swipe ? 1f : 0f;
        animRear = Damp(animRear, rear, 8f, dt);
        animSwipe = Damp(animSwipe, swipe, 18f, dt);
        float hop = moving ? Mathf.Abs(Mathf.Sin(hopPhase * 0.5f)) * 0.25f : 0f;
        float flip = Phase == State.Dethroned ? 180f : 0f;
        Rig.visual.localPosition = new Vector3(0f, hop + (Phase == State.Dethroned ? 1.4f : 0f), 0f);
        Rig.visual.localRotation = Quaternion.Euler(moving ? 6f : 0f, animSwipe * 25f, flip + (moving ? Mathf.Sin(hopPhase * 0.5f) * 5f : 0f));
        if (Rig.rear != null) Rig.rear.localRotation = Quaternion.Euler(animRear, 0f, 0f);
        if (Rig.head != null) Rig.head.localRotation = Quaternion.Euler(-animRear * 0.8f + Mathf.Sin(t * 1.3f) * 4f, Mathf.Sin(t * 0.7f) * 12f, 0f);
        if (Rig.mouthShape != null) Rig.mouthShape.localScale = new Vector3(0.07f, Phase == State.Roar ? 0.07f : 0.02f, 0.04f);
        for (int i = 0; i < Rig.tail.Count; i++)
            Rig.tail[i].localRotation = Rig.tailRest[i] * Quaternion.Euler(0f, Mathf.Sin(t * 2.5f - i * 0.7f) * 18f, 0f);
        for (int i = 0; i < Rig.paws.Count; i++)
        {
            float ph = hopPhase * 0.8f + (i % 2 == 0 ? 0f : Mathf.PI);
            float up = moving ? Mathf.Max(0f, Mathf.Sin(ph)) * 0.06f : 0f;
            if (i < 2 && (Phase == State.Windup || Phase == State.Roar)) up = 0.2f;
            if (i == 0 && Phase == State.Swipe) up = 0.12f;
            Rig.paws[i].localPosition = Rig.pawRest[i] + new Vector3(0f, up, 0f);
        }
        if (crown != null && crown.gameObject.activeSelf) crown.localRotation = Quaternion.Euler(Mathf.Sin(t * 3f) * 4f, 0f, Mathf.Sin(t * 2.2f) * 6f);
    }
}

/// <summary>
/// When the Raccoon King comes (host): once a night, 25% chance, sometime between 22:00 and 03:00 while someone's outdoors
/// (debug: <see cref="DebugStart"/>). Home = the middle of Depot Row's street in the one world (solo: near the player).
/// </summary>
public static class RaccoonKingEvent
{
    public const float ChancePerNight = 0.25f;

    public static RaccoonKing Active { get; private set; }
    public static Vector3 Home { get; private set; }
    static int rolledNight = int.MinValue;
    static float rolledAtHour = -1f;
    static int lastHp = -1;

    /// <summary>Diagnostic: this night's pending natural arrival hour, or −1 if the 25% roll missed/was spent.</summary>
    public static float PendingScheduledHour => rolledAtHour;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Active = null; rolledNight = int.MinValue; rolledAtHour = -1f; lastHp = -1; }

    static int NightIndex => (int)System.Math.Floor((GameClock.TotalHours - 12.0) / 24.0);

    /// <summary>Called by the host's CritterSpawner every frame in the town.</summary>
    public static void HostTick()
    {
        if (Active != null)
        {
            if (Active.Despawning) { Active = null; Publish(null); }
            else if (!GameClock.IsNight) Active.Leave();
            return;
        }
        if (!GameClock.IsNight) return;
        int night = NightIndex;
        if (night != rolledNight)
        {
            rolledNight = night;
            rolledAtHour = NightDice.Host.Chance(ChancePerNight) ? NightDice.Host.Range(22f, 27f) % 24f : -1f;
            NightAudit.Event("king.roll", new Dictionary<string, object>
            {
                { "night", night }, { "scheduled", System.Math.Round(rolledAtHour, 3) },
            });
        }
        if (rolledAtHour < 0f) return;
        if (!ScheduledHourDue(rolledAtHour, GameClock.Hours) || !HasOutdoorPlayer()) return;
        rolledAtHour = -1f;
        Start();
    }

    /// <summary>Pure midnight-crossing check for the night's scheduled 22:00–03:00 arrival.
    /// A missed evening arrival remains due after midnight, but never during the following daytime.</summary>
    public static bool ScheduledHourDue(float scheduled, float hour) =>
        scheduled >= 0f && (scheduled >= 12f ? hour >= scheduled || hour < GameClock.DawnStart
                                            : hour >= scheduled && hour < GameClock.DawnStart);

    static bool HasOutdoorPlayer()
    {
        var players = NightPlayers.All;
        for (int i = 0; i < players.Count; i++)
            if (players[i] != null && players[i].isActiveAndEnabled && !NightPlayers.IsIndoors(players[i])) return true;
        return false;
    }

    public static string DebugStart()
    {
        if (!CritterWorld.IsHost) return "Host only.";
        if (Active != null && !Active.Despawning) return "The King is already here.";
        return Start() ? "The Raccoon King arrives!" : "No place for the King (nobody outside?).";
    }

    static bool Start()
    {
        if (!HasOutdoorPlayer()) return false;
        Vector3 home;
        if (DepotRowWorld.Active)
        {
            home = new Vector3((DepotRowLayout.LotCenterX(1) + DepotRowLayout.LotCenterX(2)) * 0.5f, 0f, DepotRowLayout.RoadZ);
        }
        else
        {
            var me = NightPlayers.LocalPlayer;
            if (me == null) return false;
            Vector3 fwd = CritterWorld.Flat(me.transform.forward).normalized;
            home = me.transform.position + fwd * 14f;
        }
        home.y = CritterWorld.GroundY(home + Vector3.up * 3f, home.y);
        Home = home;
        int hp = 8 + 4 * NightPlayers.Count;
        Active = RaccoonKing.Spawn(home, hp);
        Publish(Active);
        NightNet.HostToastNear(home, 10000f, "THE RACCOON KING is raiding Depot Row! Everyone: throw trash at him!", ToastStyle.Bad);
        NightAudit.Event("king", new Dictionary<string, object> { { "op", "start" }, { "hp", hp }, { "players", NightPlayers.Count } });
        return true;
    }

    public static void HostChanged(RaccoonKing k) => Publish(k);

    static void Publish(RaccoonKing k)
    {
        var n = NightNet.Instance;
        if (k == null) { lastHp = -1; if (NightNet.Streaming && n.IsServer) n.HostSetKing(default); return; }
        lastHp = k.Hp;
        if (NightNet.Streaming && n.IsServer)
            n.HostSetKing(new KingNet { active = true, critter = k.NetId, hp = (short)k.Hp, maxHp = (short)k.MaxHp });
    }

    /// <summary>Host: the King is beaten (pay out) or leaves.</summary>
    public static void HostEnded(RaccoonKing k, bool beaten)
    {
        if (k == null) return;
        if (!beaten)
        {
            NightNet.HostToastNear(k.transform.position, 10000f, "The Raccoon King retreats into the night... for now.", ToastStyle.Info);
            NightAudit.Event("king", new Dictionary<string, object> { { "op", "leave" }, { "hp", k.Hp } });
            Publish(null);
            return;
        }
        int total = 0;
        foreach (var kv in k.Hits) total += kv.Value;
        float pot = 150f + 100f * NightPlayers.Count;
        var parts = new List<string>();
        foreach (var kv in k.Hits)
        {
            if (kv.Value <= 0 || total <= 0) continue;
            float share = Mathf.Round(pot * kv.Value / total * 100f) / 100f;
            using (Ledger.ToBusiness(NightTraps.BusinessOf(kv.Key)))
                Ledger.Credit(share, "Dethroned the Raccoon King!", NightShift.LedgerKing, k.transform.position);
            string uid = $"crown-{GameClock.Day}-{kv.Key}-{(long)(GameClock.TotalHours * 1000) % 100000}";
            NightNet.HostGiveItem(kv.Key, RaccoonKing.TrophyDefId, uid, 500f, $"Dethroned on day {GameClock.Day}. {kv.Value} royal bonks.");
            parts.Add($"{HandsNet.NameOf(kv.Key)} {kv.Value}");
            NightAudit.Event("king", new Dictionary<string, object> { { "op", "pay" }, { "client", (long)kv.Key }, { "hits", kv.Value }, { "cents", Mathf.RoundToInt(share * 100f) } });
        }
        NightNet.HostToastNear(k.transform.position, 10000f,
            $"THE RACCOON KING IS DETHRONED! {Economy.Money(pot)} split by hits ({string.Join(", ", parts)}). Crowns for everyone who helped!", ToastStyle.Good);
        NightAudit.Event("king", new Dictionary<string, object> { { "op", "beaten" }, { "total", total }, { "pot", pot } });
        Publish(null);
    }

    // ---------------------------------------------------------------- HUD (every machine)

    /// <summary>The boss bar: hp/max, or false when there's no King.</summary>
    public static bool HudState(out int hp, out int max, out Vector3 pos)
    {
        hp = max = 0;
        pos = Vector3.zero;
        if (NightNet.Streaming && !NightNet.Instance.IsServer)
        {
            var k = NightNet.Instance.King;
            if (!k.active) return false;
            hp = k.hp; max = k.maxHp;
            var p = NightNet.Instance.Puppet(k.critter);
            pos = p != null ? p.transform.position : Vector3.zero;
            return true;
        }
        if (Active == null || Active.Despawning || Active.Phase == RaccoonKing.State.Leaving) return false;
        hp = Active.Hp; max = Active.MaxHp; pos = Active.transform.position;
        return true;
    }
}

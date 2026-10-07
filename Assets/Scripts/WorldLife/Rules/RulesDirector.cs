using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §8, §9): the Rules director (slot Rules, billed to WorldLife.Rules).
/// <list type="bullet">
/// <item><b>Boot</b> (BeforeSceneLoad, before any scene wakes WorldLife): sets <see cref="WorldLifeRunner.Authority"/> to
/// <see cref="WorldLifeAuthority"/> (so C's and D's interim sensors switch off), hooks the grudge into CurbTrash's pay,
/// and starts the hourly books (the midnight City Works invoice, the morning noise letter), which run in any scene,
/// garage included.</item>
/// <item><b>Awake</b> (an anchor is up): adds the <see cref="TruckLifeSensor"/> to the runner's per-truck sweep, listens to
/// the horn (<see cref="HonkReactions"/>, <see cref="NightNoise"/>), checks player bumps and thrown things
/// (<see cref="PlayerContacts"/>), and makes grudged households glare at your truck instead of waving.</item>
/// </list>
/// </summary>
public sealed class RulesDirector : WorldLifeDirector
{
    public static RulesDirector Instance { get; private set; }
    public override WorldLifeSlot Slot => WorldLifeSlot.Rules;
    public override string Name => "RulesDirector";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        WorldLifeRunner.Authority = WorldLifeAuthority.Instance;
        Instance = new RulesDirector();
        WorldLifeRunner.Register(Instance);
        NeighborGrudge.Boot();
        WalkOutPresenter.Grudged = NeighborGrudge.Has;     // a grudged house glares at the truck after its walk-out (§9.1)
        PlayerContacts.Boot();
        GameClock.HourChanged -= OnHour;
        GameClock.HourChanged += OnHour;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    /// <summary>Every in-game hour, any scene of a running session (garage included), host only: bill yesterday's City
    /// Works, send the morning noise letter. Not in the menus, whose clock is a local one.</summary>
    static void OnHour(int hour)
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return;
        var b = RulesBooks.Active;
        if (!b.IsHost) return;
        CityWorksInvoice.BillDue(b);
        NightNoise.MaybeSendMorningLetter(b);
    }

    WorldLifeContext ctx;
    readonly HonkReactions horn = new HonkReactions();
    public PlayerContacts Contacts { get; } = new PlayerContacts();
    float nextGlare;
    public int Glares { get; private set; }
    public int Honks { get; private set; }

    public override void Wake(WorldLifeContext c)
    {
        ctx = c;
        WorldLifeRunner.AddSensorClient(TruckLifeSensor.Instance);
        TruckControllerV2.Honked -= OnHonked;
        TruckControllerV2.Honked += OnHonked;
        if (!(WorldLifeRunner.Authority is WorldLifeAuthority))
            Debug.LogWarning("[WorldLife] RulesDirector woke but the authority isn't WorldLifeAuthority (F wraps it?): " + WorldLifeRunner.Authority.GetType().Name);
        CityWorksInvoice.BillDue(RulesBooks.Active);
        nextGlare = 0f;
    }

    public override void Sleep()
    {
        WorldLifeRunner.RemoveSensorClient(TruckLifeSensor.Instance);
        TruckControllerV2.Honked -= OnHonked;
        TruckLifeSensor.Instance.Clear();
        Contacts.Clear();
        lastGlare.Clear();
        ctx = null;
    }

    void OnHonked(TruckControllerV2 truck)
    {
        if (ctx == null || truck == null) return;
        Honks++;
        var m = WorldLifeRunner.MarkerFor(WorldLifeSlot.Rules);
        m.Begin();
        try { WorldLifeAuthority.Instance.ClaimHorn(truck, WorldLifeRunner.FixedTickCount, ctx.Graph, horn); }
        catch (System.Exception e) { Debug.LogException(e); }
        m.End();
    }

    public override void Tick(WorldLifeContext c)
    {
        Contacts.Tick(c, c.DeltaTime);
        if (Time.time >= nextGlare)
        {
            nextGlare = Time.time + RulesTuning.GlareInterval;
            Glare();
        }
        WorldLifeAuthority.Instance.Prune(RulesBooks.Active.RealSeconds);
    }

    public override void FixedTick(WorldLifeContext c) => Contacts.FixedTick(c);

    // ------------------------------------------------------------------ grudged households glare (§9.1)

    /// <summary>When each resident last glared (a line only when a new glare starts, not every pass).</summary>
    readonly System.Collections.Generic.Dictionary<uint, float> lastGlare = new System.Collections.Generic.Dictionary<uint, float>(64);

    void Glare()
    {
        var dir = ResidentDirector.Instance;
        var trucks = TruckControllerV2.All;
        if (dir == null || trucks.Count == 0) return;
        var agents = dir.Agents;
        float r2 = RulesTuning.GlareRadius * RulesTuning.GlareRadius;
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.rig == null || a.lite || a.Interrupted || string.IsNullOrEmpty(a.stopId) || !CanGlare(a.reqPose) || a.sack0 != null) continue;
            if (!NeighborGrudge.Has(a.stopId)) continue;
            for (int t = 0; t < trucks.Count; t++)
            {
                var tr = trucks[t];
                if (tr == null) continue;
                Vector3 d = tr.transform.position - a.pos; d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                a.React(PersonPose.Glare, RulesTuning.GlareInterval + 0.2f);
                uint key = a.id.Packed;
                if (!lastGlare.TryGetValue(key, out float last) || Time.time - last > 3f) dir.Say(a, ResidentLines.Grudge, ResidentLines.Hey, false);
                lastGlare[key] = Time.time;
                Glares++;
                break;
            }
        }
    }

    static bool CanGlare(PersonPose p) =>
        p == PersonPose.Stand || p == PersonPose.Walk || p == PersonPose.Wave || p == PersonPose.Stretch || p == PersonPose.Talk || p == PersonPose.Glare;

    public string Describe() =>
        $"authority: {WorldLifeAuthority.Instance.Describe()}\nsensor: sweeps {TruckLifeSensor.Instance.Sweeps}, claims {TruckLifeSensor.Instance.Claimed}, truck bumps {TruckLifeSensor.Instance.TruckBumps}; " +
        $"honks {Honks}; glares {Glares}; contacts: pardons {Contacts.Pardons}, stumbles {Contacts.Stumbles}, bonks {Contacts.Bonks}, throws seen {PlayerContacts.ThrowsSeen}\n" +
        $"books: flattened {NeighborConsequences.Flattened}, shunted {NeighborConsequences.Shunted}, dog near misses {NeighborConsequences.DogNearMisses}, thrown flops {NeighborConsequences.ThrownFlops}, " +
        $"fines {Economy.Money(NeighborConsequences.FinesCharged)} (waived {NeighborConsequences.Waived}); grudges +{NeighborGrudge.AddedCount} / forgiven {NeighborGrudge.ForgivenCount}; " +
        $"noise {NightNoise.Complaints} (+{NightNoise.Capped} capped, letters {NightNoise.Letters}); City Works invoices {CityWorksInvoice.Invoices}; letters {NeighborLetters.Sent} ({NeighborLetters.Popups} popped up)";
}

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One station's 3D presenter (blackjack table, roulette wheel, slot machines, bar): built from the room's
/// <see cref="CasinoStation"/>, fed by <see cref="CasinoPresenters"/>, ticked while the local player is in the room.</summary>
public abstract class CasinoStationPresenter
{
    protected CasinoPresenters host;

    /// <summary>Create whatever this presenter draws. A missing optional transform is logged once and skipped, never thrown.</summary>
    public abstract void Build(CasinoPresenters host);

    /// <summary>Every frame while the local player is inside. <paramref name="now"/> is this machine's real-time clock in seconds.</summary>
    public virtual void Tick(double now) { }

    /// <summary>The local player came into the room (the latest snapshots have been re-applied without animation).</summary>
    public virtual void Enter() { }

    /// <summary>The local player left the room.</summary>
    public virtual void Leave() { }

    public abstract void Teardown();
}

/// <summary>
/// The 3D, spectator-visible state of the casino tables (Docs/Casino.md §11): cards, chips, wheel and ball, reels, wins, name tags,
/// drinks. It lives on every machine and is driven ONLY by what <see cref="CasinoClient"/> replicates (public table snapshots, slot
/// spins, occupancy, big wins, drinks served), so every player in the room sees the same thing.
/// <para><b>Lifecycle.</b> <see cref="Ensure"/> is called by <see cref="CasinoRunner"/> once <c>Casino.unity</c> is up. The object builds
/// the presenters for the stations it finds, subscribes to the client's events (applying the latest snapshots straight away, so a late
/// joiner sees the current state), asks for a sync whenever the local player enters the room, and destroys itself when the scene
/// unloads. Snapshots are coalesced to the latest per table per frame; an older one that arrives late is ignored. Nothing here sends
/// anything or touches host state.</para>
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class CasinoPresenters : MonoBehaviour
{
    public static CasinoPresenters Instance { get; private set; }

    /// <summary>Start the presenters when the casino scene is up (idempotent; a no-op outside play mode).</summary>
    public static void Ensure()
    {
        if (Instance != null || !Application.isPlaying) return;
        if (CasinoScene.Instance == null) return;
        var go = new GameObject("[CasinoPresenters]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CasinoPresenters>();
    }

    /// <summary>This machine's real-time clock, the base of every local animation.</summary>
    public static double Now => Time.realtimeSinceStartupAsDouble;

    // ------------------------------------------------------------------ services for the presenters

    public CasinoScene Scene { get; private set; }
    public CasinoPropPool Pool { get; private set; }
    public CasinoLabels Labels { get; private set; }
    public Transform PropRoot { get; private set; }
    public bool Inside { get; private set; }

    /// <summary>The presenters of the stations (blackjack, roulette, five machines, bar), for tests and the debug menu.</summary>
    public BlackjackPresenter Blackjack { get; private set; }
    public RoulettePresenter Roulette { get; private set; }
    public readonly SlotPresenter[] Slots = new SlotPresenter[CasinoLayout.SlotCount];
    public BarPresenter Bar { get; private set; }
    public SlotRegulars Regulars { get; private set; }

    readonly List<CasinoStationPresenter> presenters = new List<CasinoStationPresenter>();
    readonly HashSet<string> warned = new HashSet<string>();

    BlackjackTableSnapshot pendingBj, lastBj;
    RouletteTableSnapshot pendingRl, lastRl;
    bool hasPendingBj, hasPendingRl, hasLastBj, hasLastRl, forceSnap;
    bool subscribed;
    double lastSyncAt = -100.0;

    struct Job { public double at; public Action<bool> run; }
    readonly List<Job> jobs = new List<Job>(32);

    /// <summary>Log a missing optional piece of the room ONCE (the room may be older than the presenters, or a station may be absent).</summary>
    public void WarnOnce(string key, string message)
    {
        if (warned.Add(key)) Debug.LogWarning("[CasinoPresenters] " + message);
    }

    /// <summary>Run <paramref name="run"/> when this machine's real-time clock reaches <paramref name="localTime"/>. If that moment is already past
    /// it runs NOW with <c>late = true</c> (a presenter snaps to the final picture instead of animating).</summary>
    public void At(double localTime, Action<bool> run)
    {
        if (run == null) return;
        if (localTime <= Now) { Run(run, true); return; }
        jobs.Add(new Job { at = localTime, run = run });
    }

    /// <summary>The same for a HOST time (a snapshot stamp): converted with the client's clock offset.</summary>
    public void AtTable(double hostTime, Action<bool> run) => At(CasinoClient.ToLocalTime(hostTime), run);

    static void Run(Action<bool> a, bool late)
    {
        try { a(late); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ lifecycle

    void OnEnable()
    {
        if (Instance == null) Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Teardown();
    }

    void Update()
    {
        var scene = CasinoScene.Instance;
        if (scene == null)
        {
            // the casino scene unloaded (or never was): drop everything and go away; CasinoRunner brings a new one with the next scene
            Destroy(gameObject);
            return;
        }
        if (scene != Scene)
        {
            Teardown();
            Build(scene);
        }
        bool inside = CasinoWorld.LocalInside;
        if (inside != Inside)
        {
            Inside = inside;
            if (inside) OnEntered(); else OnLeft();
        }
        if (!Inside) return;

        double now = Now;
        RunJobs(now);
        if (hasPendingBj) { var s = pendingBj; hasPendingBj = false; ApplyBlackjack(s, forceSnap); }
        if (hasPendingRl) { var s = pendingRl; hasPendingRl = false; ApplyRoulette(s, forceSnap); }
        forceSnap = false;
        for (int i = 0; i < presenters.Count; i++)
        {
            try { presenters[i].Tick(now); }
            catch (Exception e) { WarnOnce("tick" + i, "a presenter threw while ticking (shown once): " + e.Message); Debug.LogException(e); }
        }
        Pool.Tick(now);
        Labels.Tick(now);
    }

    void RunJobs(double now)
    {
        for (int guard = 0; guard < 64 && jobs.Count > 0; guard++)
        {
            int best = -1; double bt = double.MaxValue;
            for (int i = 0; i < jobs.Count; i++) if (jobs[i].at <= now && jobs[i].at < bt) { bt = jobs[i].at; best = i; }
            if (best < 0) break;
            var j = jobs[best];
            jobs.RemoveAt(best);
            Run(j.run, false);
        }
    }

    void Build(CasinoScene scene)
    {
        Scene = scene;
        var parent = scene.interior != null ? scene.interior.transform : scene.transform;
        var root = new GameObject("[CasinoProps]") { hideFlags = HideFlags.DontSave };
        root.transform.SetParent(parent, false);
        PropRoot = root.transform;
        Pool = new CasinoPropPool(PropRoot);
        Labels = new CasinoLabels(PropRoot);

        var bj = CasinoStation.Find(CasinoStationKind.Blackjack);
        if (bj != null) { Blackjack = new BlackjackPresenter(bj); Add(Blackjack); } else WarnOnce("nobj", "no blackjack station in the casino scene");
        var rl = CasinoStation.Find(CasinoStationKind.Roulette);
        if (rl != null) { Roulette = new RoulettePresenter(rl); Add(Roulette); } else WarnOnce("norl", "no roulette station in the casino scene");
        for (int i = 0; i < CasinoLayout.SlotCount; i++)
        {
            var st = CasinoStation.Find(CasinoStationKind.Slot, i);
            if (st == null) { WarnOnce("noslot" + i, "no slot machine " + i + " in the casino scene"); continue; }
            Slots[i] = new SlotPresenter(st, i);
            Add(Slots[i]);
        }
        var bar = CasinoStation.Find(CasinoStationKind.Bar);
        if (bar != null) { Bar = new BarPresenter(bar); Add(Bar); }
        Regulars = new SlotRegulars();
        Add(Regulars);

        if (!subscribed)
        {
            subscribed = true;
            CasinoClient.BlackjackSnapshotChanged += OnBlackjack;
            CasinoClient.RouletteSnapshotChanged += OnRoulette;
            CasinoClient.SlotSpun += OnSlotSpun;
            CasinoClient.SlotOccupancyChanged += OnOccupancy;
            CasinoClient.BigWin += OnBigWin;
            CasinoClient.BarServed += OnBarServed;
        }
        // late join / scene up after the snapshots: show the current state at once
        Inside = CasinoWorld.LocalInside;
        if (Inside) ApplyLatest();
    }

    void Add(CasinoStationPresenter p)
    {
        try { p.Build(this); presenters.Add(p); }
        catch (Exception e) { Debug.LogException(e); WarnOnce("build" + p.GetType().Name, p.GetType().Name + " failed to build: " + e.Message); }
    }

    void Teardown()
    {
        if (subscribed)
        {
            subscribed = false;
            CasinoClient.BlackjackSnapshotChanged -= OnBlackjack;
            CasinoClient.RouletteSnapshotChanged -= OnRoulette;
            CasinoClient.SlotSpun -= OnSlotSpun;
            CasinoClient.SlotOccupancyChanged -= OnOccupancy;
            CasinoClient.BigWin -= OnBigWin;
            CasinoClient.BarServed -= OnBarServed;
        }
        for (int i = 0; i < presenters.Count; i++)
        {
            try { presenters[i].Teardown(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        presenters.Clear();
        jobs.Clear();
        Blackjack = null; Roulette = null; Bar = null; Regulars = null;
        Array.Clear(Slots, 0, Slots.Length);
        if (Pool != null) Pool.DestroyAll();
        if (Labels != null) Labels.DestroyAll();
        Pool = null; Labels = null;
        if (PropRoot != null) Destroy(PropRoot.gameObject);
        PropRoot = null;
        Scene = null;
        hasPendingBj = hasPendingRl = hasLastBj = hasLastRl = false;
    }

    void OnEntered()
    {
        // The latest picture first (no animation: you walked in on a table in progress), then ask the host for a fresh one.
        ApplyLatest();
        if (Time.realtimeSinceStartup - lastSyncAt > 1.0)
        {
            lastSyncAt = Time.realtimeSinceStartup;
            try { CasinoClient.RequestSync(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        for (int i = 0; i < presenters.Count; i++) { try { presenters[i].Enter(); } catch (Exception e) { Debug.LogException(e); } }
    }

    void OnLeft()
    {
        for (int i = 0; i < presenters.Count; i++) { try { presenters[i].Leave(); } catch (Exception e) { Debug.LogException(e); } }
    }

    void ApplyLatest()
    {
        if (CasinoClient.HasBlackjack) { pendingBj = CasinoClient.LatestBlackjack; hasPendingBj = true; }
        if (CasinoClient.HasRoulette) { pendingRl = CasinoClient.LatestRoulette; hasPendingRl = true; }
        forceSnap = true;
        for (int s = 0; s < CasinoLayout.SlotCount; s++)
        {
            var p = Slots[s];
            if (p == null) continue;
            int key = CasinoClient.OccupantOfSlot(s);
            p.SetOccupant(key, key >= 0 ? CasinoClient.NameOf(key) : "");
        }
    }

    // ------------------------------------------------------------------ client events (coalesced; applied in Update)

    void OnBlackjack(BlackjackTableSnapshot snap, double offset)
    {
        if (hasPendingBj && snap.now < pendingBj.now) return;
        if (hasLastBj && snap.now < lastBj.now) return; // an older picture that arrived late
        pendingBj = snap; hasPendingBj = true;
    }

    void OnRoulette(RouletteTableSnapshot snap, double offset)
    {
        if (hasPendingRl && snap.now < pendingRl.now) return;
        if (hasLastRl && snap.now < lastRl.now) return;
        pendingRl = snap; hasPendingRl = true;
    }

    void ApplyBlackjack(BlackjackTableSnapshot snap, bool snapOnly)
    {
        if (Blackjack == null) return;
        // A snapshot that took a long time to arrive (or a late join) snaps to the final picture instead of replaying the deal.
        bool animate = !snapOnly && hasLastBj && CasinoClient.TableTime() - snap.now < 1.2;
        try { Blackjack.Apply(snap, hasLastBj ? lastBj : default, hasLastBj, animate); }
        catch (Exception e) { WarnOnce("bjapply", "blackjack snapshot not drawn: " + e.Message); Debug.LogException(e); }
        lastBj = snap; hasLastBj = true;
    }

    void ApplyRoulette(RouletteTableSnapshot snap, bool snapOnly)
    {
        if (Roulette == null) return;
        bool animate = !snapOnly && hasLastRl && CasinoClient.TableTime() - snap.now < 1.2;
        try { Roulette.Apply(snap, hasLastRl ? lastRl : default, hasLastRl, animate); }
        catch (Exception e) { WarnOnce("rlapply", "roulette snapshot not drawn: " + e.Message); Debug.LogException(e); }
        lastRl = snap; hasLastRl = true;
    }

    void OnSlotSpun(SlotEventInfo e)
    {
        if (e.machine >= Slots.Length) return;
        var p = Slots[e.machine];
        if (p == null) return;
        try { p.Spin(e); }
        catch (Exception ex) { WarnOnce("slotspin", "slot spin not drawn: " + ex.Message); Debug.LogException(ex); }
        if (Regulars != null) Regulars.RealSpin(e.machine);
    }

    void OnOccupancy(int machine, int key, string name)
    {
        if (machine < 0 || machine >= Slots.Length || Slots[machine] == null) return;
        Slots[machine].SetOccupant(key, name);
        if (Regulars != null) Regulars.Occupancy(machine, key >= 0);
    }

    void OnBigWin(int key, string who, string what, long cents)
    {
        // The slot spin event already carries the celebration; blackjack and roulette wins are shown from the snapshots.
        // (Kept as a hook for the room: a toast for a big win elsewhere is the HUD's job.)
    }

    void OnBarServed(int key, int drink, int spot)
    {
        if (Bar == null) return;
        try { Bar.Serve(key, drink, spot); }
        catch (Exception ex) { WarnOnce("barserve", "bar pour not drawn: " + ex.Message); Debug.LogException(ex); }
    }
}

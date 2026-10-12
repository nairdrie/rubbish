using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// The guided first run (Docs/Campaign.md §14.8). A new save is walked through the whole job once: the contract cards, the truck,
/// the old trash, the way home, the depot, the tipping floor, the sort table, the laptop; and later, the first floppy disk. It is an
/// overlay, never a gate: a banner with one sentence and a key, an arrow at the thing to go to, a green flash when a lesson is done.
/// Hold U to switch it all off.
/// <para>Everything here is polling and public statics (no events were added anywhere, no scene script was touched, so no town
/// bake is needed). <see cref="TutorialBrain"/> decides what is done and what to say; this class reads the game into
/// <see cref="TutorialFacts"/>, counts the edges the brain wants (<see cref="TutorialMemory"/>), finds the places to point at,
/// keeps the saved bit mask (<see cref="TutorialRules.MaskKey"/> in the business's own counters) and drives
/// <see cref="TutorialHud"/>, <see cref="ContractIntroScreen"/> and <see cref="LaptopCoach"/>.</para>
/// </summary>
public sealed class Tutorial : MonoBehaviour
{
    const float HeavyEvery = 0.25f, ForgetAfterNoPlayer = 3f, SkipHoldSeconds = 1.2f, ScanEvery = 2f, RipSampleDelay = 1.6f;

    static Tutorial instance;

    /// <summary>The lesson being taught right now (null: none).</summary>
    public static TutorialStep? Current => instance != null ? instance.current : null;

    // ---- state
    TutorialFacts f;
    TutorialMemory m;
    TutorialStep? current;
    bool inited, cardsShowing, wasRipping, tickedOnce, prevCargoKnown, prevIndoors;
    float stepSince, nextHeavy, nextScan, noPlayerSince = -1f, skipHeld, cardsRetryAt, ripSampleAt, highlightUntil, prevLoad;
    int prevTabsMask = -1;
    // the banner's words are rebuilt a few times a second, not every frame (they are strings)
    TutorialCard cachedCard;
    TutorialStep? cachedStep;
    bool cachedDriving;
    float nextCard;

    // ---- cached places (refreshed at 4 Hz / every 2 s)
    TruckControllerV2 truck;
    LaptopController laptop;
    SortingTable table;
    TippingFloor floor;
    GarageDoorController exitDoor, depotDoor;
    Vector3 heapPos;
    bool hasHeap;
    readonly HashSet<string> oldStops = new HashSet<string>();
    LaptopCoach coach;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Application.isPlaying || Application.isBatchMode || instance != null) return;
        var go = new GameObject("[Tutorial]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<Tutorial>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void OnEnable() => ProgressionEvents.ItemSorted += OnSorted;

    void OnDisable()
    {
        ProgressionEvents.ItemSorted -= OnSorted;
        FeelHudCity.Highlight = false;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        coach?.Dispose();
    }

    void OnSorted(ItemSortedFact fact)
    {
        if (!fact.byMagnus) m.sorted++;
    }

    // ------------------------------------------------------------------ the frame

    int faults;

    /// <summary>An overlay must never break the game: a fault is logged (once) and counted, and after a run of them it stops for the session.</summary>
    void Update()
    {
        try { Step(); }
        catch (Exception e)
        {
            if (faults++ == 0) Debug.LogException(e);
            if (faults > 40) { Debug.LogWarning("[Tutorial] too many faults, switching off for this session."); Forget(); enabled = false; }
        }
    }

    void Step()
    {
        float dt = Time.unscaledDeltaTime;
        var local = PlayerController.Local; // coop:allow(local) the walk-through is for this machine's own player
        var lay = HudLayout.Layout;
        bool world = lay.valid && local != null && !SceneController.IsTransitioning &&
                     (lay.mode == HudMode.Street || lay.mode == HudMode.Driving || lay.mode == HudMode.Depot ||
                      lay.mode == HudMode.Station || lay.mode == HudMode.Laptop);
        f.control = world && lay.mode != HudMode.Laptop && !UiStack.AnyOpen && !DiskFoundPopup.IsShowing;

        if (local == null)
        {
            // a trip to the menu forgets what this session has seen; a scene swap does not last this long
            if (noPlayerSince < 0f) noPlayerSince = Time.unscaledTime;
            else if (Time.unscaledTime - noPlayerSince > ForgetAfterNoPlayer && (inited || m.controlSeconds > 0f)) Forget();
        }
        else noPlayerSince = -1f;

        // the floppy's card is a modal screen (the HUD reads Paused behind it), so it is noticed here, not in a live tick
        if (inited && local != null && DiskFoundPopup.IsShowing && GameSession.GetCounter(TutorialRules.DiskKey) == 0)
            GameSession.SetCounter(TutorialRules.DiskKey, 1);

        if (world)
        {
            if (!inited && f.control) Init();
            if (inited) Tick(local, lay, dt);
        }
        else if (inited) Present(false);
    }

    void Forget()
    {
        m = default;
        f = default;
        current = null;
        inited = false;
        cardsShowing = false;
        prevTabsMask = -1;
        truck = null; laptop = null; table = null; floor = null; exitDoor = null; depotDoor = null;
        wasRipping = false;
        ContractIntroScreen.Dismiss();
        coach?.Hide();
        TutorialHud.Instance?.Clear();
        FeelHudCity.Highlight = false;
    }

    // ------------------------------------------------------------------ first sight of a save

    void Init()
    {
        inited = true;
        if (GameSession.GetCounter(TutorialRules.InitKey) != 0) return;
        GameSession.SetCounter(TutorialRules.InitKey, 1);
        bool experienced = TutorialRules.LooksExperienced(GameSession.TotalItemsSorted, GameSession.TotalCashEarned, GameSession.TotalKgRecycled,
                                                          Mathf.CeilToInt(GameSession.Rep), StarterCollectionContract.LocalProgress,
                                                          GameSession.GetUpgradeLevel(ShopIds.TruckModel));
        int mask = 0;
        if (experienced)
        {
            // a save that knows the job: only the new goal needs explaining
            mask = TutorialRules.WalkThroughDone & ~(1 << (int)TutorialStep.Cards);
            if (Workshop.HasPrinter) mask |= (1 << (int)TutorialStep.Disk) | (1 << (int)TutorialStep.DiskTab);
        }
        GameSession.SetCounter(TutorialRules.MaskKey, mask);
        Metrics.Begin("tutorial_init").Bool("experienced", experienced).Int("mask", mask).End();
    }

    // ------------------------------------------------------------------ one tick of a live world

    void Tick(PlayerController local, HudLayout.Frame lay, float dt)
    {
        tickedOnce = true;
        bool off = GameSession.GetCounter(TutorialRules.OffKey) != 0;
        m.tabsMask = GameSession.GetCounter(TutorialRules.TabsKey);
        if (f.control) m.controlSeconds += dt;
        FeelHudCity.Highlight = f.control && Time.unscaledTime < highlightUntil;

        // cheap per-frame reads
        f.driving = IsDriving(local);
        if (f.driving) m.drove = true;
        WatchRips();

        if (Time.unscaledTime >= nextHeavy)
        {
            nextHeavy = Time.unscaledTime + HeavyEvery;
            Heavy(local);
        }

        if (off)
        {
            current = null;
            Present(false);
            coach?.Hide();
            return;
        }

        SkipKey(dt, local);
        Advance(dt);
        CardsFlow();
        LaptopFlow();
        Present(f.control && !cardsShowing);
    }

    static bool IsDriving(PlayerController p) =>
        p != null && p.IsDriving && p.currentTruck != null && p.currentTruck.IsLocalPlayerSeated;

    /// <summary>A bag starts to rip on my table: a thrown one if the player is not at the station.</summary>
    void WatchRips()
    {
        bool ripping = table != null && table.isActiveAndEnabled && table.IsRipping;
        if (ripping && !wasRipping)
        {
            m.rips++;
            if (!SortStation.LocalInSortMode) m.thrownRips++;
            if (m.itemsAtRip == 0) ripSampleAt = Time.unscaledTime + RipSampleDelay;
        }
        wasRipping = ripping;
        if (ripSampleAt > 0f && Time.unscaledTime >= ripSampleAt)
        {
            ripSampleAt = 0f;
            int n = 0;
            foreach (var li in LooseItem.All) if (li != null && li.isActiveAndEnabled && li.State != CarryState.Consumed) n++;
            m.itemsAtRip = Mathf.Max(1, n);
        }
    }

    // ------------------------------------------------------------------ the slower reads

    void Heavy(PlayerController local)
    {
        f.gamepad = GameControls.UsingGamepad;
        var camp = Campaign.Display;
        f.contractActive = camp.Active;
        f.oldBags = camp.backlog;
        f.districtName = camp.Active && camp.district < Districts.All.Count ? Districts.All[camp.district].name : null;
        f.indoors = IsIndoors();
        f.inSortMode = SortStation.LocalInSortMode;
        var carrier = TrashCarrier.Local;
        f.carryingBag = carrier != null && carrier.IsLocalOwner && carrier.Held is TrashBag;
        f.laptopOpen = EbaeUI.IsOpen;
        f.printerInstalled = Workshop.HasPrinter;
        f.diskSeen = GameSession.GetCounter(TutorialRules.DiskKey) != 0;

        var ctx = TruckHudContext.Resolve(local);
        // Resolve only names the truck while driving (or in the one world's depot); on foot in a solo game it is the nearest of mine
        truck = ctx.truck != null ? ctx.truck : MyTruck(local.transform.position);
        f.truckThere = truck != null;
        f.cargoKnown = ctx.cargoKnown;
        f.loadKg = ctx.loadKg;
        f.capacityKg = ctx.capacityKg;

        // edges
        if (!f.indoors) m.wentOutside = true;
        if (TruckHudRules.ShouldReturnToDepot(f.cargoKnown, f.loadKg, f.capacityKg, false)) m.sawFull = true;
        if (f.indoors && prevIndoors && prevCargoKnown && f.cargoKnown && prevLoad > TutorialBrain.EmptyKg && f.loadKg <= TutorialBrain.EmptyKg) m.tipped++;
        prevIndoors = f.indoors;
        prevCargoKnown = f.cargoKnown;
        prevLoad = f.loadKg;

        // places
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanEvery;
            Scan();
        }
        Vector3 me = local.transform.position;
        f.truckDistance = truck != null ? Flat(me, truck.transform.position) : -1f;
        f.tableDistance = table != null ? Flat(me, TablePoint()) : -1f;
        FindHeap(me);
        f.heapDistance = hasHeap ? Flat(me, heapPos) : -1f;
    }

    /// <summary>The nearest truck that is mine (every truck outside the one world, my own lot's inside it).</summary>
    static TruckControllerV2 MyTruck(Vector3 me)
    {
        TruckControllerV2 best = null;
        float bestD = float.MaxValue;
        foreach (var t in TruckControllerV2.All)
        {
            if (t == null || !t.isActiveAndEnabled || !BagCannonMount.IsMine(t)) continue;
            float d = (t.transform.position - me).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>Where a door is in the world: its drive-through zone (the door object's own origin can be up in the rafters).</summary>
    static Vector3 DoorPoint(GarageDoorController door)
    {
        foreach (var z in InteractionZone.Active)
            if (z != null && ReferenceEquals(z.Target, door)) return z.transform.TransformPoint(z.center);
        return door.transform.position;
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        a.y = b.y;
        return Vector3.Distance(a, b);
    }

    /// <summary>My own depot: the solo garage scene, or my own shop interior in the one world.</summary>
    static bool IsIndoors()
    {
        if (DepotRowWorld.Active) return DepotRowWorld.LocalInterior >= 0 && DepotRowWorld.LocalInterior == DepotRowWorld.LocalLot;
        return SceneManager.GetActiveScene().name == SceneController.GarageSceneName; // coop:allow(scene) the solo garage is its own scene
    }

    static bool MineLot(Component c)
    {
        if (!DepotRowWorld.Active) return true;
        return DepotMachineContext.LotOf(c) == DepotRowWorld.LocalLot;
    }

    /// <summary>The machines and doors of my depot, found by type (never by scene path), a couple of times a second at most.</summary>
    void Scan()
    {
        laptop = null; table = null; floor = null; exitDoor = null; depotDoor = null;
        float best = float.MaxValue;
        var me = PlayerController.Local != null ? PlayerController.Local.transform.position : Vector3.zero; // coop:allow(local) own player
        foreach (var l in UnityEngine.Object.FindObjectsByType<LaptopController>(FindObjectsSortMode.None))
        {
            if (l == null || !l.isActiveAndEnabled || !MineLot(l)) continue;
            float d = (l.transform.position - me).sqrMagnitude;
            if (d < best) { best = d; laptop = l; }
        }
        best = float.MaxValue;
        foreach (var r in CarryReceivers.All)
        {
            if (!(r is SortingTable t) || t == null || !t.isActiveAndEnabled || !MineLot(t)) continue;
            float d = (t.transform.position - me).sqrMagnitude;
            if (d < best) { best = d; table = t; }
        }
        best = float.MaxValue;
        foreach (var fl in TippingFloor.All)
        {
            if (fl == null || !fl.isActiveAndEnabled) continue;
            if (DepotRowWorld.Active && fl.Lot != DepotRowWorld.LocalLot) continue;
            float d = (fl.pileCenter - me).sqrMagnitude;
            if (d < best) { best = d; floor = fl; }
        }
        if (DepotRowWorld.Active)
        {
            var lot = DepotRowScene.Lot(DepotRowWorld.LocalLot);
            if (lot != null) { depotDoor = lot.streetDoor; exitDoor = lot.interiorDoor; }
        }
        else
        {
            float bestIn = float.MaxValue, bestOut = float.MaxValue;
            foreach (var door in UnityEngine.Object.FindObjectsByType<GarageDoorController>(FindObjectsSortMode.None))
            {
                if (door == null || !door.isActiveAndEnabled) continue;
                float d = (door.transform.position - me).sqrMagnitude;
                bool toShop = !string.IsNullOrEmpty(door.targetSceneName) && door.targetSceneName.Contains("Shop");
                if (toShop) { if (d < bestIn) { bestIn = d; depotDoor = door; } }
                else if (d < bestOut) { bestOut = d; exitDoor = door; }
            }
        }
    }

    Vector3 TablePoint() => table.TossTargetPosition;

    /// <summary>The nearest curb that still has the contract's old trash on it (any neglected heap in its district if the records are not here).</summary>
    void FindHeap(Vector3 me)
    {
        hasHeap = false;
        var contract = Campaign.Active;
        if (contract == null) return;
        oldStops.Clear();
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++)
            if (CampaignRules.IsBacklogBagId(bags[i].bagId)) oldStops.Add(bags[i].stopId);
        float best = float.MaxValue;
        var stops = PickupStop.All;
        for (int i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            if (s == null || !s.isActiveAndEnabled || !s.HasTrash) continue;
            bool mine = oldStops.Count > 0 ? oldStops.Contains(s.stopId) : s.District != null && s.District.id == contract.districtId && s.IsNeglected;
            if (!mine) continue;
            Vector3 p = s.PileCenter;
            float d = (p - me).sqrMagnitude;
            if (d < best) { best = d; heapPos = p; hasHeap = true; }
        }
    }

    // ------------------------------------------------------------------ the lessons

    int Mask => GameSession.GetCounter(TutorialRules.MaskKey);

    void Advance(float dt)
    {
        bool diskFound = f.diskSeen || f.printerInstalled;
        for (int guard = 0; guard < 16; guard++)
        {
            var next = TutorialRules.Next(Mask, diskFound);
            if (next == null)
            {
                current = null;
                return;
            }
            var step = next.Value;
            if (current != step) Begin(step);
            if (step == TutorialStep.Tour && f.indoors && f.control) m.tourSeconds += dt;
            if (!TutorialBrain.Done(step, f, m)) return;
            Complete(step);
            dt = 0f;
        }
    }

    void Begin(TutorialStep step)
    {
        current = step;
        stepSince = Time.unscaledTime;
        Metrics.Begin("tutorial_step").Str("step", step.ToString()).Int("n", (int)step).End();
    }

    void Complete(TutorialStep step)
    {
        float secs = Time.unscaledTime - stepSince;
        GameSession.SetCounter(TutorialRules.MaskKey, TutorialRules.Mark(Mask, step));
        Metrics.Begin("tutorial_done").Str("step", step.ToString()).Int("n", (int)step).Num("secs", secs, "0.0").End();
        current = null;
        if (step == TutorialStep.Cards || secs < 1.2f) return;      // a lesson that was already done when it came up gets no cheer
        TutorialHud.Get()?.Celebrate(TutorialBrain.DoneText(step));
        if (step == TutorialStep.Laptop)
        {
            string where = string.IsNullOrEmpty(f.districtName) ? "the town" : f.districtName;
            GameHUD.Toast($"That's the whole job! Now clean up {where}: {CampaignTuning.ThresholdPct}% for a full day.", ToastStyle.Good, 6f);
            Metrics.Begin("tutorial_complete").End();
        }
    }

    // ------------------------------------------------------------------ the cards

    void CardsFlow()
    {
        if (cardsShowing || !f.control || !f.contractActive || Time.unscaledTime < cardsRetryAt) return;
        var contract = Campaign.Active;
        if (contract == null) return;
        bool first = current == TutorialStep.Cards;
        // later contracts get one short card, once each
        if (!first && (!TutorialRules.Done(Mask, TutorialStep.Cards) || GameSession.GetCounter(TutorialRules.CardsKey + contract.districtId) != 0)) return;
        string id = contract.districtId;
        cardsShowing = ContractIntroScreen.Show(contract, first, skipped =>
        {
            cardsShowing = false;
            GameSession.SetCounter(TutorialRules.CardsKey + id, 1);
            if (first)
            {
                m.cardsClosed = true;
                highlightUntil = Time.unscaledTime + 7f;
                GameHUD.Toast("That bar is your goal. Keep an eye on it.", ToastStyle.Good, 4f);
            }
        });
        if (!cardsShowing) cardsRetryAt = Time.unscaledTime + 2f;
    }

    // ------------------------------------------------------------------ the laptop

    void LaptopFlow()
    {
        bool want = f.laptopOpen && (current == TutorialStep.Laptop || current == TutorialStep.DiskTab);
        if (!want)
        {
            coach?.Hide();
            return;
        }
        if (coach == null) coach = new LaptopCoach();
        var app = EbaeUI.CurrentApp;
        int tab, mark;
        string title, body, hint = "Click it to go on";
        VisualElement el;
        if (current == TutorialStep.DiskTab)
        {
            tab = mark = TutorialRules.TabPrinter;
            el = coach.TabElement(tab);
            title = "NEW TAB: SOLIDWASTE 3D";
            body = "Your floppy disk installed this. Print your own gear from the parts you sort, and build Blueprints. Click its tab.";
        }
        else
        {
            tab = TutorialRules.NextTab(m.tabsMask, TutorialRules.WalkThroughTabs);
            mark = tab;
            el = tab >= 0 ? coach.TabElement(tab) : null;
            switch (tab)
            {
                case TutorialRules.TabEbae:
                    title = "EBAG: SELL YOUR JUNK";
                    body = "Everything you find lands in YOUR JUNK. List it for a price, or pawn it for quick cash. Strangers on the internet make offers.";
                    break;
                case TutorialRules.TabScrapazon:
                    title = "SCRAPAZON: BUY GEAR";
                    body = "Spend your cash on upgrades: a bigger truck, better tools, a faster table. Click the tab.";
                    break;
                default:
                    title = "CAREER: YOUR CONTRACT";
                    body = "Your rank, your Trust and the city's contract are under Career. It says what to do next. Click it.";
                    if (el == null)
                    {
                        // Career lives inside Scrapazon: back to that tab first
                        el = coach.TabElement(TutorialRules.TabScrapazon);
                        mark = -1;
                        title = "BACK TO SCRAPAZON";
                        body = "Career is a department inside Scrapazon. Click the tab to go back.";
                    }
                    break;
            }
        }
        if (el == null) { coach.Hide(); return; }
        coach.Show(el, mark, title, body, hint, OnCoachClick);
        coach.Tick();
    }

    void OnCoachClick(int tab)
    {
        if (tab < 0) return;
        int tabs = TutorialRules.TabMark(GameSession.GetCounter(TutorialRules.TabsKey), tab);
        GameSession.SetCounter(TutorialRules.TabsKey, tabs);
        m.tabsMask = tabs;
        Metrics.Begin("tutorial_tab").Int("tab", tab).End();
    }

    // ------------------------------------------------------------------ what the player sees

    void SkipKey(float dt, PlayerController local)
    {
        bool can = f.control && !f.laptopOpen && current.HasValue && current != TutorialStep.Cards && !f.gamepad;
        if (can && Input.GetKey(KeyCode.U)) skipHeld += dt;
        else skipHeld = Mathf.Max(0f, skipHeld - dt * 2f);
        if (skipHeld < SkipHoldSeconds) return;
        skipHeld = 0f;
        GameSession.SetCounter(TutorialRules.OffKey, 1);
        Metrics.Begin("tutorial_skip").Int("n", current.HasValue ? (int)current.Value : -1).End();
        GameHUD.Toast("Tutorial off. Good luck out there!", ToastStyle.Good, 3.5f);
        current = null;
        Present(false);
    }

    void Present(bool show)
    {
        var hud = TutorialHud.Instance;
        if (!show || !current.HasValue)
        {
            if (hud != null) { hud.SetCard(null); hud.SetTarget(null, null); hud.SetSkip(null, 0f); }
            return;
        }
        var step = current.Value;
        if (Time.unscaledTime >= nextCard || cachedStep != step || cachedDriving != f.driving)
        {
            cachedCard = TutorialBrain.Card(step, f, m);
            cachedStep = step;
            cachedDriving = f.driving;
            nextCard = Time.unscaledTime + 0.1f;
        }
        var c = cachedCard;
        if (string.IsNullOrEmpty(c.title))
        {
            if (hud != null) { hud.SetCard(null); hud.SetTarget(null, null); hud.SetSkip(null, 0f); }
            return;
        }
        hud = TutorialHud.Get();
        if (hud == null) return;
        hud.SetCard(new TutorialHud.Card { badge = TutorialRules.Badge(step), title = c.title, body = c.body, key = c.key, urgent = c.urgent });
        hud.SetTarget(Resolve(c.target, out var p) ? p : (Vector3?)null, c.targetName);
        hud.SetSkip(f.gamepad ? null : "hold U to skip the tutorial", skipHeld / SkipHoldSeconds);
    }

    /// <summary>Where the arrow goes for a target kind (null: nowhere to point right now).</summary>
    bool Resolve(TutorialTarget t, out Vector3 pos)
    {
        pos = default;
        switch (t)
        {
            case TutorialTarget.Truck:
                if (truck == null) return false;
                pos = truck.transform.position + Vector3.up * 3.4f;
                return true;
            case TutorialTarget.Hopper:
            {
                if (truck == null) return false;
                var h = BagCannonMount.HopperOf(truck);
                pos = (h != null ? h.TossTargetPosition : truck.transform.position) + Vector3.up * 1.6f;
                return true;
            }
            case TutorialTarget.GarageDoor:
                if (exitDoor == null) return false;
                pos = DoorPoint(exitDoor) + Vector3.up * 1.6f;
                return true;
            case TutorialTarget.DepotDoor:
                if (depotDoor == null) return false;
                pos = DoorPoint(depotDoor) + Vector3.up * 1.8f;
                return true;
            case TutorialTarget.OldTrash:
                if (!hasHeap) return false;
                pos = heapPos + Vector3.up * 3.4f;
                return true;
            case TutorialTarget.Laptop:
                if (laptop == null) return false;
                pos = laptop.transform.position + Vector3.up * 0.9f;
                return true;
            case TutorialTarget.TippingFloor:
            case TutorialTarget.BagPile:
                if (floor == null) return false;
                pos = floor.pileCenter + Vector3.up * 1.7f;
                return true;
            case TutorialTarget.SortTable:
                if (table == null) return false;
                pos = TablePoint() + Vector3.up * 0.9f;
                return true;
            case TutorialTarget.Disk:
                foreach (var li in LooseItem.All)
                {
                    if (li == null || li.State == CarryState.Consumed || !DiskItem.Is(li.Item)) continue;
                    pos = li.transform.position + Vector3.up * 0.6f;
                    return true;
                }
                return false;
        }
        return false;
    }

    // ------------------------------------------------------------------ debug and tests

    /// <summary>Back to a brand new save's state (the debug menu): every lesson to do again, the cards too.</summary>
    public static void DebugRestart()
    {
        GameSession.SetCounter(TutorialRules.MaskKey, 0);
        GameSession.SetCounter(TutorialRules.OffKey, 0);
        GameSession.SetCounter(TutorialRules.InitKey, 1);
        GameSession.SetCounter(TutorialRules.TabsKey, 0);
        GameSession.SetCounter(TutorialRules.DiskKey, 0);
        foreach (var d in Districts.All) GameSession.SetCounter(TutorialRules.CardsKey + d.id, 0);
        if (instance != null) instance.Forget();
    }

    /// <summary>Switch the whole thing off (or on again) for this save.</summary>
    public static void DebugOff(bool off) => GameSession.SetCounter(TutorialRules.OffKey, off ? 1 : 0);

    /// <summary>One line for the console: where the walk-through is.</summary>
    public static string DebugState()
    {
        int mask = GameSession.GetCounter(TutorialRules.MaskKey);
        var t = instance;
        return $"mask={Convert.ToString(mask, 2)} off={GameSession.GetCounter(TutorialRules.OffKey)} init={GameSession.GetCounter(TutorialRules.InitKey)} " +
               $"tabs={Convert.ToString(GameSession.GetCounter(TutorialRules.TabsKey), 2)} current={(t != null && t.current.HasValue ? t.current.Value.ToString() : "-")} " +
               (t != null ? $"facts: control={t.f.control} indoors={t.f.indoors} driving={t.f.driving} load={t.f.loadKg:0.0}/{t.f.capacityKg:0} heap={t.f.heapDistance:0} " +
                            $"memory: drove={t.m.drove} out={t.m.wentOutside} full={t.m.sawFull} tipped={t.m.tipped} rips={t.m.rips}/{t.m.thrownRips} sorted={t.m.sorted}" : "(not running)");
    }
}

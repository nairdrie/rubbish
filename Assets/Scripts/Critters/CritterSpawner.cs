using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runtime brain of the Critters system. One DontDestroyOnLoad "[Critters]" object, created automatically when
/// the game starts (no scene/prefab setup needed).
///
/// Every client (for its own local player):
///   - attaches <see cref="RabiesStatus"/> and <see cref="CritterKnockback"/> to PlayerController.Local when it spawns,
///   - auto-uses a stored 'rabies_shot' consumable ~1.5 s after you get infected,
///   - polls the truck horn (H while driving): scatters raccoons within 12 m (25 m with the 'air_horn' upgrade),
///     wakes pile goblins grumpy.
/// Host only (NetworkManager.IsServer, or offline), while a scene with curb stops (the Neighborhood) is active:
///   - Raccoons at night: at nightfall and every ~30 s through the night, 25–40 % of the houses with bags out
///     (more when the city is dirtier) get 1–2 raccoons hiding behind their bags; ~20–40 % are rabid.
///     At dawn (GameClock.IsNight turns false) they all head home.
///   - Pile Goblins any time: every ~12 s each neglected pile may get a napping squatter (max 3, 4 when filthy).
///   - Tells critters when a trash bag gets grabbed (TrashCarrier.Local.PickedUp, plus TrashBag.AnyPickedUp when the
///     Trash system provides it).
/// Critters are plain scene objects: they vanish with the Neighborhood and get re-populated when you come back.
/// F4 night shift (Docs/Multiplayer.md §14.4): the raccoon count scales with players (×(1 + 0.25·(n−1)), at most 8 in co-op);
/// raccoons don't pop in near ANY player; bags holding something shiny attract them (heists); loaded trucks parked at night
/// attract stowaways; armed trash-can traps lure them in; the Raccoon King turns up some nights on Depot Row
/// (<see cref="RaccoonKingEvent"/>). Guests run none of this: <see cref="NightNet"/> streams the host's critters to them.
/// </summary>
[DefaultExecutionOrder(-40)]
public class CritterSpawner : MonoBehaviour
{
    public static CritterSpawner Instance { get; private set; }

    public const int MaxRaccoons = 12;
    public const int MaxGoblins = 3;
    public const float HornRadius = 12f;
    public const float AirHornRadius = 25f;
    public const string AirHornUpgradeId = "air_horn";
    public const string RabiesShotId = "rabies_shot";

    /// <summary>Fraction of houses-with-trash that get raccoons at night (lerped by dirt).</summary>
    public static float RaccoonHouseFraction => Mathf.Lerp(0.25f, 0.40f, CritterWorld.Dirt01);
    /// <summary>Chance a new raccoon is rabid (more when dirty).</summary>
    public static float RabidChance => Mathf.Lerp(0.2f, 0.4f, CritterWorld.Dirt01);

    /// <summary>Horn scare radius for the current upgrades.</summary>
    public static float CurrentHornRadius => GameSession.GetUpgradeLevel(AirHornUpgradeId) > 0 ? AirHornRadius : HornRadius;

    public static int RaccoonCount { get { int n = 0; foreach (var c in Critter.All) if (c is Raccoon && !c.Despawning) n++; return n; } }
    public static int GoblinCount { get { int n = 0; foreach (var c in Critter.All) if (c is PileGoblin && !c.Despawning) n++; return n; } }

    float sceneReadyAt;
    float nextRaccoonCheck, nextGoblinCheck;
    bool wasNight;
    int toastedNightId = int.MinValue;
    float lastGoblinToast = -999f;
    float nextHornAt;
    bool hornTipShown;
    PlayerController lastPlayer;
    TrashCarrier watchedCarrier;
    TrashBag lastGrabbed;
    int lastGrabFrame = -10;
    EventInfo anyPickedUpEvent;
    Delegate anyPickedUpHandler;
    Coroutine autoShot;

    static readonly List<TrashBag> tmpBags = new List<TrashBag>();
    static readonly List<PickupStop> tmpStops = new List<PickupStop>();
    static readonly List<Critter> tmpCritters = new List<Critter>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("[Critters]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CritterSpawner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        GameClock.NightFell += OnNightFell;
        GameSession.RabiesChanged += OnRabiesChanged;
        SubscribeAnyPickedUp();
        sceneReadyAt = Time.time + 2f;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameClock.NightFell -= OnNightFell;
        GameSession.RabiesChanged -= OnRabiesChanged;
        if (watchedCarrier != null) watchedCarrier.PickedUp -= OnLocalPickedUp;
        watchedCarrier = null;
        if (anyPickedUpEvent != null && anyPickedUpHandler != null)
        {
            try { anyPickedUpEvent.RemoveEventHandler(null, anyPickedUpHandler); } catch { }
        }
        anyPickedUpEvent = null; anyPickedUpHandler = null;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        CritterWorld.RefreshStops();
        sceneReadyAt = Time.time + 2.5f; // let curb bags materialize first
        nextRaccoonCheck = sceneReadyAt;
        nextGoblinCheck = sceneReadyAt + 1f;
        wasNight = GameClock.IsNight;
    }

    void OnNightFell()
    {
        nextRaccoonCheck = Mathf.Min(nextRaccoonCheck, Time.time + 1.5f);
    }

    // =====================================================================
    // Update
    // =====================================================================

    float nextStowCheck, nextLureCheck;

    void Update()
    {
        EnsurePlayerComponents();
        WatchCarrier();
        PollHorn();

        if (!CritterWorld.IsHost || Time.time < sceneReadyAt) return;
        // Depot Row is one shared world: the host can be in their pocket shop while guests are still on the street.
        // CritterWorld.InNeighborhood follows the host's local indoor view, so don't pause the town's night simulation.
        if (!CritterWorld.InNeighborhood && !DepotRowWorld.Active) return;
        RaccoonKingEvent.HostTick();
        if (GameClock.IsNight && Time.time >= nextStowCheck) { nextStowCheck = Time.time + 3f; CheckStowaways(); }
        if (Time.time >= nextLureCheck) { nextLureCheck = Time.time + 1.5f; CheckTraps(); }

        bool night = GameClock.IsNight;
        if (wasNight && !night) SendRaccoonsHome();
        wasNight = night;

        if (night && Time.time >= nextRaccoonCheck)
        {
            nextRaccoonCheck = Time.time + UnityEngine.Random.Range(22f, 38f);
            PopulateRaccoons();
        }
        if (Time.time >= nextGoblinCheck)
        {
            nextGoblinCheck = Time.time + UnityEngine.Random.Range(10f, 14f);
            UpdateGoblins();
        }
    }

    void EnsurePlayerComponents()
    {
        var pc = PlayerController.Local;
        if (pc == null || pc == lastPlayer) return;
        lastPlayer = pc;
        if (pc.GetComponent<CritterKnockback>() == null) pc.gameObject.AddComponent<CritterKnockback>();
        if (pc.GetComponent<RabiesStatus>() == null) pc.gameObject.AddComponent<RabiesStatus>();
        if (pc.GetComponent<RaccoonLatch>() == null) pc.gameObject.AddComponent<RaccoonLatch>();
    }

    /// <summary>Raccoons for this many players: ×(1 + 0.25·(n−1)) (Docs/Multiplayer.md §4.10).</summary>
    public static float PlayerScale => 1f + 0.25f * (NightPlayers.Count - 1);

    /// <summary>Raccoon cap: 12 solo, 8 in co-op (the streaming budget, §4.10).</summary>
    public static int RaccoonCap => NightNet.Streaming ? NightShift.CoopRaccoonCap : MaxRaccoons;

    /// <summary>Is any player within <paramref name="r"/> m of p (drivers at their truck)?</summary>
    static bool AnyPlayerNear(Vector3 p, float r)
    {
        var all = NightPlayers.All;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc == null || !pc.isActiveAndEnabled) continue;
            Vector3 at = pc.IsDriving && pc.currentTruck != null ? pc.currentTruck.transform.position : pc.transform.position;
            if (CritterWorld.Flat(at - p).sqrMagnitude < r * r) return true;
        }
        return false;
    }

    static bool HasShiny(TrashBag b)
    {
        var items = b != null && b.Contents != null ? b.Contents.items : null;
        if (items == null) return false;
        for (int i = 0; i < items.Count; i++)
        {
            var d = items[i] != null ? items[i].Def : null;
            if (d != null && d.category == ItemCategory.Valuable) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- stowaways and traps (F4)

    void CheckStowaways()
    {
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
        {
            var t = trucks[i];
            if (t == null || CritterWorld.VelocityOf(t).magnitude > 1.2f || !DepotTruckCargo.HasCargo(t)) continue;
            if (CritterWorld.KeepOut(t.transform.position, true)) continue; // parked on a lot: safe
            bool taken = false;
            Raccoon best = null;
            float bestD = 14f;
            Vector3 rear = t.transform.TransformPoint(new Vector3(0f, 0f, -4.5f));
            foreach (var c in Critter.All)
            {
                if (!(c is Raccoon r) || r.Despawning || r.NetPuppet) continue;
                if (r.StowTruck == t) { taken = true; break; }
                float d = CritterWorld.Flat(r.transform.position - rear).magnitude;
                if (d < bestD && !r.Rabid && r.Loot == null) { bestD = d; best = r; }
            }
            if (taken || best == null || !NightDice.Host.Chance(0.35f)) continue;
            best.TryStowaway(t);
        }
    }

    void CheckTraps()
    {
        var traps = RaccoonTrap.All;
        for (int i = 0; i < traps.Count; i++)
        {
            var trap = traps[i];
            if (trap == null || !trap.Armed || trap.HasLuredRaccoon) continue;
            Raccoon best = null;
            float bestD = RaccoonTrap.LureRadius;
            foreach (var c in Critter.All)
            {
                if (!(c is Raccoon r) || r.Despawning || r.NetPuppet || r.LureTrap != null) continue;
                float d = CritterWorld.Flat(r.transform.position - trap.Mouth).magnitude;
                if (d < bestD) { bestD = d; best = r; }
            }
            if (best != null) best.TryLure(trap);
        }
    }

    // =====================================================================
    // Raccoons
    // =====================================================================

    void PopulateRaccoons()
    {
        var stops = CritterWorld.Stops;
        tmpStops.Clear();
        foreach (var s in stops) if (s != null && CritterWorld.BagsAt(s, tmpBags) > 0) tmpStops.Add(s);
        if (tmpStops.Count == 0) return;

        int target = Mathf.Clamp(Mathf.RoundToInt(tmpStops.Count * RaccoonHouseFraction * 1.35f * PlayerScale), 1, RaccoonCap);
        int toSpawn = Mathf.Min(target - RaccoonCount, 6);
        if (toSpawn <= 0) return;

        Shuffle(tmpStops);
        // Armed traps smell of fish: houses near one go first.
        var traps = RaccoonTrap.All;
        if (traps.Count > 0)
            tmpStops.Sort((a, b) => NearTrap(a).CompareTo(NearTrap(b)));
        var plan = new List<(PickupStop stop, TrashBag bag, bool rabid)>();
        foreach (var stop in tmpStops)
        {
            if (plan.Count >= toSpawn) break;
            if (HasCritterAt<Raccoon>(stop)) continue;
            if (AnyPlayerNear(stop.transform.position, 22f)) continue; // don't materialize next to anyone
            if (CritterWorld.KeepOut(stop.transform.position, true)) continue;
            CritterWorld.BagsAt(stop, tmpBags);
            Shuffle(tmpBags);
            // A nose for shiny things (F4 heists): a bag with a valuable in it goes first.
            for (int k = 1; k < tmpBags.Count; k++)
                if (HasShiny(tmpBags[k]) && !HasShiny(tmpBags[0])) { (tmpBags[0], tmpBags[k]) = (tmpBags[k], tmpBags[0]); break; }
            int n = UnityEngine.Random.value < 0.35f && tmpBags.Count > 1 ? 2 : 1;
            for (int i = 0; i < n && i < tmpBags.Count && plan.Count < toSpawn; i++)
                plan.Add((stop, tmpBags[i], UnityEngine.Random.value < RabidChance));
        }
        if (plan.Count > 0) StartCoroutine(SpawnRaccoonsStaggered(plan));
    }

    IEnumerator SpawnRaccoonsStaggered(List<(PickupStop stop, TrashBag bag, bool rabid)> plan)
    {
        int spawned = 0;
        foreach (var p in plan)
        {
            if (p.stop == null || !CritterWorld.IsGrabbableBag(p.bag)) continue;
            Vector3 spot = Raccoon.HideSpotFor(p.bag, p.stop, out Quaternion facing);
            Raccoon.Spawn(spot, facing, p.rabid, p.stop, p.bag);
            spawned++;
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.15f, 0.6f));
        }
        if (spawned > 0) NightToast();
    }

    void NightToast()
    {
        int nightId = (int)Math.Floor((GameClock.TotalHours - GameClock.NightStart) / 24.0);
        if (nightId == toastedNightId) return;
        toastedNightId = nightId;
        GameHUD.Toast("The raccoons are out! Honk (H) to scatter them. Some of them are... foamy.", ToastStyle.Info, 3.5f);
        Sfx.Play("chitter", null, 0.6f, 1.1f);
    }

    void SendRaccoonsHome()
    {
        tmpCritters.Clear();
        tmpCritters.AddRange(Critter.All);
        foreach (var c in tmpCritters) if (c is Raccoon r && r != null) r.Leave();
        if (tmpCritters.Exists(c => c is Raccoon)) GameHUD.Toast("Dawn! The raccoons shuffle home to sleep it off.", ToastStyle.Info, 2.5f);
    }

    // =====================================================================
    // Pile goblins
    // =====================================================================

    void UpdateGoblins()
    {
        float dirt = CritterWorld.Dirt01;
        int cap = dirt > 0.6f ? MaxGoblins + 1 : MaxGoblins;
        int count = GoblinCount;
        if (count >= cap) return;

        tmpStops.Clear();
        tmpStops.AddRange(CritterWorld.Stops);
        Shuffle(tmpStops);
        foreach (var stop in tmpStops)
        {
            if (count >= cap) break;
            if (stop == null || HasCritterAt<PileGoblin>(stop)) continue;
            if (!CritterWorld.IsNeglected(stop)) continue;
            if (CritterWorld.BagsAt(stop, tmpBags) == 0) continue;
            if (AnyPlayerNear(stop.transform.position, 16f)) continue;
            if (UnityEngine.Random.value > Mathf.Lerp(0.3f, 0.55f, dirt)) continue;
            var g = PileGoblin.Spawn(stop, stop.transform.position);
            count++;
            if (Time.time - lastGoblinToast > 60f)
            {
                lastGoblinToast = Time.time;
                GameHUD.Toast($"A {g.DisplayName} moved into the neglected trash at {stop.address}. Zzz...", ToastStyle.Info, 3f);
            }
        }
    }

    static int NearTrap(PickupStop s)
    {
        if (s == null) return 1;
        foreach (var t in RaccoonTrap.All)
            if (t != null && t.Armed && CritterWorld.Flat(t.Mouth - s.transform.position).magnitude < 40f) return 0;
        return 1;
    }

    static bool HasCritterAt<T>(PickupStop stop) where T : Critter
    {
        foreach (var c in Critter.All) if (c is T && c.Home == stop && !c.Despawning) return true;
        return false;
    }

    // =====================================================================
    // Bags being grabbed
    // =====================================================================

    void WatchCarrier()
    {
        var c = TrashCarrier.Local;
        if (c == watchedCarrier) return;
        if (watchedCarrier != null) watchedCarrier.PickedUp -= OnLocalPickedUp;
        watchedCarrier = c;
        if (c != null) c.PickedUp += OnLocalPickedUp;
    }

    void OnLocalPickedUp(Carryable item)
    {
        if (item is TrashBag bag) NotifyBagGrabbed(bag, watchedCarrier);
    }

    void OnAnyPickedUp(TrashBag bag, TrashCarrier by) => NotifyBagGrabbed(bag, by);

    void SubscribeAnyPickedUp()
    {
        // TrashBag.AnyPickedUp(TrashBag, TrashCarrier) is being added by the Trash system; hook it if present.
        try
        {
            var ev = typeof(TrashBag).GetEvent("AnyPickedUp", BindingFlags.Public | BindingFlags.Static);
            if (ev == null || ev.EventHandlerType != typeof(Action<TrashBag, TrashCarrier>)) return;
            anyPickedUpHandler = new Action<TrashBag, TrashCarrier>(OnAnyPickedUp);
            ev.AddEventHandler(null, anyPickedUpHandler);
            anyPickedUpEvent = ev;
        }
        catch (Exception e) { Debug.LogWarning("[Critters] Couldn't hook TrashBag.AnyPickedUp: " + e.Message); }
    }

    /// <summary>Tell every critter a bag was grabbed (deduped when both hooks fire).</summary>
    public static void NotifyBagGrabbed(TrashBag bag, TrashCarrier by)
    {
        var self = Instance;
        if (bag == null) return;
        if (self != null)
        {
            if (bag == self.lastGrabbed && Time.frameCount - self.lastGrabFrame < 3) return;
            self.lastGrabbed = bag;
            self.lastGrabFrame = Time.frameCount;
        }
        tmpCritters.Clear();
        tmpCritters.AddRange(Critter.All);
        foreach (var c in tmpCritters)
        {
            if (c == null || c.Despawning) continue;
            try { c.OnBagGrabbed(bag, by); } catch (Exception e) { Debug.LogException(e); }
        }
    }

    // =====================================================================
    // Horn
    // =====================================================================

    void PollHorn()
    {
        var pc = PlayerController.Local;
        if (pc == null || !pc.IsDriving || PlayerController.IsUIBlocking) return;
        var truck = pc.currentTruck != null ? pc.currentTruck : CritterWorld.Truck;
        if (truck == null || !GameControls.Down(truck.hornKey, GameControls.Action.Horn) || Time.time < nextHornAt) return;
        nextHornAt = Time.time + 0.3f;
        Honk(truck.transform.position);
        if (!CritterWorld.IsHost) NightNet.RequestHonk(truck.transform.position); // the host's critters hear it (F4)
    }

    /// <summary>A horn blast at pos: briefly startles raccoons (then angers them), annoys goblins. Returns nearby critter count.</summary>
    public static int Honk(Vector3 pos)
    {
        var self = Instance;
        bool air = GameSession.GetUpgradeLevel(AirHornUpgradeId) > 0;
        float radius = air ? AirHornRadius : HornRadius;
        int raccoons = 0, goblins = 0;
        tmpCritters.Clear();
        tmpCritters.AddRange(Critter.All);
        foreach (var c in tmpCritters)
        {
            if (c == null || c.Despawning || c.NetPuppet) continue;
            if (CritterWorld.Flat(c.transform.position - pos).magnitude > radius) continue;
            c.Scare(pos, true);
            if (c is Raccoon) raccoons++; else goblins++;
        }

        Vector3 above = pos + Vector3.up * 3.6f;
        // The Shop's ShopTruckMods already plays the air horn's layered HOOOONK + text + shake on the same H press;
        // only do our own air-horn juice when no modded truck is honking (e.g. the debug menu's Honk).
        var honkTruck = CritterWorld.Truck;
        bool shopHornJuice = air && honkTruck != null && honkTruck.GetComponent<ShopTruckMods>() != null
                             && CritterWorld.Flat(honkTruck.transform.position - pos).sqrMagnitude < 36f;
        if (air && !shopHornJuice)
        {
            // The derpy air horn: layered honks + a shock ring.
            Sfx.Play("horn", pos, 1f, 0.62f);
            Sfx.Play("horn", pos, 0.8f, 1.4f);
            Juice.Shake(0.14f, 0.25f);
            GameHUD.FloatingText(above + Vector3.up * 0.6f, "HOOOOONK!!", new Color(1f, 0.85f, 0.2f), 1.6f);
        }
        if (self != null && (air || raccoons + goblins > 0)) self.StartCoroutine(ShockRing(pos, radius, air));
        if (raccoons > 0)
        {
            GameHUD.FloatingText(above, raccoons > 1 ? "SCATTER!" : "shoo!", new Color(0.7f, 1f, 0.5f), 1.3f);
            if (self != null && !self.hornTipShown)
            {
                self.hornTipShown = true;
                GameHUD.Toast(air ? "AIR HORN! A brief startle — then they get angry." : "That horn buys a moment. Raccoons bite back!", ToastStyle.Info, 2.5f);
            }
        }
        return raccoons + goblins;
    }

    static IEnumerator ShockRing(Vector3 pos, float radius, bool big)
    {
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        UnityEngine.Object.DestroyImmediate(ring.GetComponent<Collider>());
        ring.name = "HornRing";
        ring.layer = CritterWorld.CritterLayer;
        var r = ring.GetComponent<Renderer>();
        r.sharedMaterial = CritterLook.Unlit(big ? new Color(1f, 0.9f, 0.4f) : new Color(0.85f, 1f, 0.7f));
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        float dur = big ? 0.55f : 0.4f;
        float t = 0f;
        while (t < dur && ring != null)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / dur);
            float d = Mathf.Lerp(2f, radius * 2f, 1f - (1f - k) * (1f - k));
            ring.transform.position = pos + Vector3.up * 0.25f;
            ring.transform.localScale = new Vector3(d, Mathf.Lerp(0.05f, 0.005f, k), d);
            yield return null;
        }
        if (ring != null) UnityEngine.Object.Destroy(ring);
    }

    // =====================================================================
    // Rabies helpers
    // =====================================================================

    void OnRabiesChanged(bool infected)
    {
        if (!infected) return;
        if (autoShot != null) StopCoroutine(autoShot);
        autoShot = StartCoroutine(AutoShot());
    }

    IEnumerator AutoShot()
    {
        yield return new WaitForSecondsRealtime(1.5f);
        autoShot = null;
        if (!GameSession.HasRabies || GameSession.GetConsumable(RabiesShotId) <= 0 || !FeralStatus.LocalAutoShot) yield break; // Embrace It: you choose
        GameSession.AddConsumable(RabiesShotId, -1);
        GameHUD.Toast("Good thing you packed a Rabies Shot!", ToastStyle.Good, 2.5f);
        RabiesStatus.CureWithShot();
    }

    // =====================================================================
    // Debug (menu items under Rubbish/Debug/Critters)
    // =====================================================================

    public static string DebugSpawnRaccoon(bool rabid)
    {
        if (!Application.isPlaying) return "Play mode only.";
        if (!FindNearPlayer(out Vector3 feet, out Vector3 fwd)) return "No local player.";
        if (NearestStopWithBags(feet, 30f, out var stop))
        {
            CritterWorld.BagsAt(stop, tmpBags);
            TrashBag bag = null;
            foreach (var b in tmpBags)
            {
                bool taken = false;
                foreach (var c in Critter.All) if (c is Raccoon r && r.Bag == b) taken = true;
                if (!taken) { bag = b; break; }
            }
            if (bag == null && tmpBags.Count > 0) bag = tmpBags[0];
            Vector3 spot = Raccoon.HideSpotFor(bag, stop, out Quaternion facing);
            Raccoon.Spawn(spot, facing, rabid, stop, bag);
            return $"{(rabid ? "Rabid raccoon" : "Raccoon")} hiding at {stop.address}.";
        }
        Vector3 p = feet + fwd * 3.5f;
        p.y = CritterWorld.GroundY(p, feet.y);
        Raccoon.Spawn(p, Quaternion.LookRotation(-fwd, Vector3.up), rabid, null, null);
        return $"{(rabid ? "Rabid raccoon" : "Raccoon")} spawned in front of you (no bags nearby).";
    }

    public static string DebugSpawnGoblin()
    {
        if (!Application.isPlaying) return "Play mode only.";
        if (!FindNearPlayer(out Vector3 feet, out Vector3 fwd)) return "No local player.";
        if (NearestStopWithBags(feet, 30f, out var stop) && !HasCritterAt<PileGoblin>(stop))
        {
            var g = PileGoblin.Spawn(stop, stop.transform.position);
            return $"{g.DisplayName} napping on the pile at {stop.address}.";
        }
        Vector3 p = feet + fwd * 3f;
        p.y = CritterWorld.GroundY(p, feet.y);
        var g2 = PileGoblin.Spawn(null, p);
        return $"{g2.DisplayName} napping in front of you (no free pile nearby).";
    }

    public static string DebugClearCritters()
    {
        tmpCritters.Clear();
        tmpCritters.AddRange(Critter.All);
        foreach (var c in tmpCritters) if (c != null) c.Poof();
        return $"Poofed {tmpCritters.Count} critters.";
    }

    static bool FindNearPlayer(out Vector3 feet, out Vector3 fwd)
    {
        feet = Vector3.zero; fwd = Vector3.forward;
        var pc = PlayerController.Local;
        if (pc == null) return false;
        Transform t = pc.IsDriving && pc.currentTruck != null ? pc.currentTruck.transform : pc.transform;
        feet = t.position;
        fwd = CritterWorld.Flat(t.forward);
        fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
        return true;
    }

    static bool NearestStopWithBags(Vector3 from, float maxDist, out PickupStop best)
    {
        best = null;
        float bestD = maxDist;
        foreach (var s in CritterWorld.Stops)
        {
            if (s == null || CritterWorld.BagsAt(s, tmpBags) == 0) continue;
            float d = CritterWorld.Flat(s.transform.position - from).magnitude;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best != null;
    }

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

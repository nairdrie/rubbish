using System;
using System.Collections.Generic;
using System.Reflection;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;

/// <summary>A bot behaviour (<c>-botbrain</c>). Brains only ever touch the bot's OWN player (its <see cref="BotContext.Input"/>).</summary>
public interface IBotBrain
{
    void Begin(BotContext c);
    void Tick(BotContext c, float dt);
    void End(BotContext c);
}

/// <summary>What a brain can see: the session, the bot's own player and input, a deterministic RNG, and the clock.</summary>
public sealed class BotContext
{
    public readonly BotDriver Driver;
    public readonly BotOptions Options;
    public NetworkManager Nm { get; private set; }
    public bool IsHost => Nm != null && Nm.IsServer;
    public bool IsGuest => Nm != null && Nm.IsListening && !Nm.IsServer;
    /// <summary>This process's own player object (null between scenes or when offline).</summary>
    public NetworkObject Player { get; private set; }
    public StarterAssetsInputs Input { get; private set; }
    /// <summary>Seconds since the brain started.</summary>
    public float Elapsed { get; private set; }

    internal float walked;
    Vector3 lastPos;
    bool hasLast;
    uint rng;

    public BotContext(BotDriver driver, BotOptions options)
    {
        Driver = driver;
        Options = options;
        rng = unchecked((uint)WorldHash.Fnv(WorldHash.Seed, options.label)) | 1u;
    }

    /// <summary>[0, 1). Deterministic per bot (seeded from its player id), so a run can be replayed.</summary>
    public float Value()
    {
        // xorshift32: bot input only, never gameplay content.
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return (rng & 0xFFFFFF) / 16777216f;
    }

    public float Range(float min, float max) => min + (max - min) * Value();

    /// <summary>Let go of every input.</summary>
    public void Stop()
    {
        if (Input == null) return;
        Input.move = Vector2.zero;
        Input.sprint = false;
        Input.jump = false;
    }

    internal void Tick(NetworkManager nm, float dt)
    {
        Nm = nm;
        Elapsed += dt;
        NetworkObject po = null;
        if (nm != null && nm.IsListening && nm.LocalClient != null) po = nm.LocalClient.PlayerObject;
        if (po != Player)
        {
            Player = po;
            Input = po != null ? po.GetComponent<StarterAssetsInputs>() : null;
            hasLast = false;
        }
        if (Player == null) return;
        Vector3 p = Player.transform.position;
        if (hasLast)
        {
            float d = Vector3.Distance(p, lastPos);
            if (d < 3f) walked += d; // bigger jumps are respawns and teleports, not walking
        }
        lastPos = p;
        hasLast = true;
    }
}

/// <summary>
/// The <c>-botbrain</c> registry: <c>walk</c> (default), <c>idle</c>, <c>laptop</c>, <c>ritual</c>, <c>grief</c>, <c>curb</c>. Later packages
/// add or replace brains from their own files with <see cref="Register"/> (call it at <c>SubsystemRegistration</c> or
/// <c>BeforeSceneLoad</c>): B9 replaces the <c>ritual</c> placeholder, B12 extends <c>laptop</c> for guests.
/// </summary>
public static class BotBrains
{
    static readonly Dictionary<string, Func<IBotBrain>> factories = new Dictionary<string, Func<IBotBrain>>(StringComparer.OrdinalIgnoreCase)
    {
        { "walk", () => new WalkBrain() },
        { "idle", () => new IdleBrain() },
        { "laptop", () => new LaptopBrain() },
        { "ritual", () => new PlaceholderBrain("ritual", "B9 (CrewRitual)") },
        { "grief", () => new GriefBrain() },
        { "curb", () => new CurbBrain() },
    };

    public static IEnumerable<string> Names => factories.Keys;

    public static bool Exists(string name) => !string.IsNullOrEmpty(name) && factories.ContainsKey(name);

    /// <summary>Add or replace a brain (idempotent; never cleared, so registration order doesn't matter).</summary>
    public static void Register(string name, Func<IBotBrain> factory)
    {
        if (string.IsNullOrEmpty(name) || factory == null) throw new ArgumentException("brain name and factory are required");
        factories[name] = factory;
    }

    public static IBotBrain Create(string name) =>
        factories.TryGetValue(name ?? "", out var f) ? f() : new WalkBrain();

    // ------------------------------------------------------------------ built-in brains

    sealed class IdleBrain : IBotBrain
    {
        public void Begin(BotContext c) => c.Stop();
        public void Tick(BotContext c, float dt) => c.Stop();
        public void End(BotContext c) => c.Stop();
    }

    /// <summary>
    /// A random walk around where the player spawned (within 9 m): a new heading every 2–6 s, sometimes sprinting, sometimes
    /// pausing, turning away when stuck. Headings are world directions converted to camera-relative input, as the
    /// ThirdPersonController reads it.
    /// </summary>
    internal sealed class WalkBrain : IBotBrain
    {
        const float Radius = 9f;
        Vector3 home, stuckRef;
        bool hasHome, sprint, homing;
        float heading, nextTurn, pauseUntil, nextStuckCheck, nextReport;

        public void Begin(BotContext c) => nextReport = 60f;

        public void Tick(BotContext c, float dt)
        {
            if (c.Input == null || c.Player == null) { hasHome = false; return; }
            float now = c.Elapsed;
            Vector3 pos = c.Player.transform.position;
            if (!hasHome || (pos - home).sqrMagnitude > 40f * 40f)
            {
                home = pos; // first spawn, or respawned somewhere new (scene change)
                stuckRef = pos;
                hasHome = true;
                nextStuckCheck = now + 2f;
            }
            if (now >= nextReport)
            {
                nextReport = now + 60f;
                NetAudit.Event("bot", new Dictionary<string, object> { { "phase", "walking" }, { "walked", Math.Round(c.walked, 1) } });
            }
            if (now < pauseUntil) { c.Stop(); return; }
            if (now >= nextTurn && !homing)
            {
                heading = c.Range(0f, 360f);
                nextTurn = now + c.Range(2f, 6f);
                sprint = c.Value() < 0.3f;
                if (c.Value() < 0.15f) { pauseUntil = now + c.Range(0.5f, 2f); c.Stop(); return; }
            }
            Vector3 toHome = home - pos;
            toHome.y = 0f;
            float away = toHome.magnitude;
            if (away < Radius * 0.6f) homing = false;
            if (away > Radius && (!homing || now >= nextTurn))
            {
                heading = Mathf.Atan2(toHome.x, toHome.z) * Mathf.Rad2Deg + c.Range(-25f, 25f);
                nextTurn = now + 3f; // commit to heading home for a bit
                homing = true;
            }
            if (now >= nextStuckCheck)
            {
                if ((pos - stuckRef).magnitude < 0.3f)
                {
                    heading += c.Range(90f, 270f);
                    nextTurn = now + c.Range(1.5f, 3f);
                    if (c.Value() < 0.3f) c.Input.jump = true;
                }
                stuckRef = pos;
                nextStuckCheck = now + 2f;
            }
            Vector3 world = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            var cam = Camera.main;
            float camYaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            Vector3 local = Quaternion.Euler(0f, -camYaw, 0f) * world;
            c.Input.move = new Vector2(local.x, local.z).normalized;
            c.Input.sprint = sprint;
        }

        public void End(BotContext c) => c.Stop();
    }

    /// <summary>
    /// The laptop crowd. Host: credits a small tip every 10 s through <see cref="Ledger"/> (so ledgers and wallets move) and
    /// every 30 s buys the cheapest affordable, unlocked Scrapazon item through <see cref="ScrapazonStore.TryBuy(string)"/>.
    /// Guest: once B12 lands, buys through its <c>ShopRequests.Buy(id)</c> (found by name); until then it stands still.
    /// </summary>
    sealed class LaptopBrain : IBotBrain
    {
        float nextTip = 10f, nextBuy = 30f;
        int tips, buys;
        MethodInfo guestBuy;
        bool warned;

        public void Begin(BotContext c)
        {
            c.Stop();
            var t = Type.GetType("ShopRequests, Assembly-CSharp");
            guestBuy = t?.GetMethod("Buy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            float now = c.Elapsed;
            if (c.IsHost)
            {
                if (now >= nextTip)
                {
                    nextTip = now + 10f;
                    Ledger.Credit(25f, "Bot shift tip", LedgerIds.PickupFee);
                    tips++;
                }
                if (now >= nextBuy)
                {
                    nextBuy = now + 30f;
                    var it = Cheapest();
                    if (it == null) return;
                    var result = ScrapazonStore.TryBuy(it.id, out string message);
                    if (result == ScrapazonPurchase.Ok) buys++;
                    NetAudit.Event("laptop", new Dictionary<string, object>
                    {
                        { "side", "host" }, { "item", it.id }, { "result", result }, { "cash", GameSession.Cash }, { "buys", buys }, { "tips", tips },
                    });
                }
                return;
            }
            if (!c.IsGuest || now < nextBuy) return;
            nextBuy = now + 30f;
            if (guestBuy == null)
            {
                if (!warned) Debug.Log("[Bot] laptop brain on a guest needs B12's ShopRequests; standing by the laptop instead.");
                warned = true;
                return;
            }
            var pick = Cheapest();
            if (pick == null) return;
            try
            {
                guestBuy.Invoke(null, new object[] { pick.id });
                NetAudit.Event("laptop", new Dictionary<string, object> { { "side", "guest" }, { "item", pick.id }, { "requested", true } });
            }
            catch (Exception e) { Debug.LogException(e.InnerException ?? e); }
        }

        static ScrapazonItem Cheapest()
        {
            ScrapazonItem best = null;
            float bestPrice = float.MaxValue;
            foreach (var it in ScrapazonCatalog.All)
            {
                if (it == null || it.IsTeaser || ScrapazonStore.IsMaxed(it) || ScrapazonStore.IsLocked(it) || !ScrapazonStore.CanAfford(it)) continue;
                float price = ScrapazonStore.NextPrice(it);
                if (price < bestPrice) { best = it; bestPrice = price; }
            }
            return best;
        }

        public void End(BotContext c) => c.Stop();
    }

    /// <summary>
    /// Hostile guest (B7's gate): every 5 s calls <c>GameSession.AddCash(999)</c> directly, which a guest must never be able to
    /// do. The host's cash must not move, and <see cref="NetAudit.GuestWritesDropped"/> must equal the calls once C2's guards
    /// are in. Walks in between. On the host it only walks.
    /// </summary>
    sealed class GriefBrain : IBotBrain
    {
        readonly WalkBrain walk = new WalkBrain();
        float next = 5f;
        int calls;

        public void Begin(BotContext c) => walk.Begin(c);

        public void Tick(BotContext c, float dt)
        {
            walk.Tick(c, dt);
            if (!c.IsGuest || c.Elapsed < next) return;
            next = c.Elapsed + 5f;
            calls++;
            GameSession.AddCash(999f, "bot grief: a guest minting money"); // coop:allow(cash) the grief brain deliberately calls the forbidden path to prove a guest's write is dropped (B7 gate)
            NetAudit.Event("grief", new Dictionary<string, object>
            {
                { "calls", calls }, { "guestWritesDropped", NetAudit.GuestWritesDropped }, { "localCash", GameSession.Cash },
            });
        }

        public void End(BotContext c) => walk.End(c);
    }

    /// <summary>
    /// Shared trash (Docs/Multiplayer.md §13, N1): every bot grabs curb bags and tosses them into a truck, so the run shows
    /// shared curbs (a grabbed bag is gone for everyone), host-checked tosses, pay to the truck's business and no double pay.
    /// The bot teleports its OWN player next to the bag, picks it up through its TrashCarrier (which claims it), teleports to
    /// the nearest truck hopper and tosses it in (a guest's toss is a request the host checks).
    /// <list type="bullet">
    /// <item>Host: loads the Neighborhood if no curbs are loaded; keeps ≥ 8 bags out (debug put-outs); empties full trucks
    /// (a dump); every 45 s hands the truck to the next business in the Town (so guests' businesses get paid too); every 30 s
    /// pays each guest business $2.00 through <c>Ledger.ToBusiness</c> (payroll-style).</item>
    /// <item>Guests: every 20 s a $1.25 tip and every 50 s a $0.75 purchase through their own Ledger (host requests).</item>
    /// </list>
    /// Audit events: <c>botcurb</c> (picks, tosses, paid, lost) and the ledger/business lines.
    /// </summary>
    sealed class CurbBrain : IBotBrain
    {
        float nextAct = 6f, nextReport = 30f, nextTip = 20f, nextSpend = 50f, nextWage = 30f, nextOwner = 45f, nextSupply = 5f;
        bool loadedScene;
        int picks, tosses, paid, lost, ownerTurn;
        long paidCents;
        TrashBag holding;
        float holdingSince;

        public void Begin(BotContext c)
        {
            c.Stop();
            WorldState.HopperPaid += OnPaid;
        }

        void OnPaid(string bagId, long cents, string business)
        {
            paid++;
            paidCents += cents;
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            float now = c.Elapsed;
            if (c.IsHost) HostChores(c, now);
            else if (c.IsGuest && BusinessSlots.Declared) GuestChores(now);
            if (now >= nextReport)
            {
                nextReport = now + 30f;
                Report(c, "curb");
            }
            var carrier = TrashCarrier.Local;
            if (c.Player == null || carrier == null || now < nextAct) return;
            nextAct = now + c.Range(2.5f, 4.5f);

            if (carrier.IsCarrying)
            {
                var hopper = NearestHopper(c.Player.transform.position);
                if (hopper == null) return;
                Vector3 back = hopper.TossTargetPosition - hopper.transform.forward * 2.2f;
                if ((c.Player.transform.position - back).sqrMagnitude > 4f * 4f)
                {
                    // Walk up first (next act tosses): the host checks the thrower is near the truck.
                    Teleport(c.Player.gameObject, new Vector3(back.x, hopper.TossTargetPosition.y - 0.6f, back.z));
                    nextAct = now + 1f;
                    return;
                }
                if (!hopper.CanAccept(carrier.Held, out string why))
                {
                    if (now - holdingSince > 20f) carrier.Drop(); // full for too long: give it up
                    return;
                }
                carrier.TossInto(hopper);
                tosses++;
                holding = null;
                return;
            }
            if (!ReferenceEquals(holding, null) && (holding == null || holding.State == CarryState.Consumed))
            {
                lost++; // our bag went poof (someone else won it)
                holding = null;
            }
            else holding = null;
            var bag = PickBag(c);
            if (bag == null) return;
            Teleport(c.Player.gameObject, bag.transform.position + Vector3.up * 0.1f - (bag.SourceStop != null ? bag.SourceStop.transform.forward : Vector3.forward) * 1.2f);
            if (carrier.PickUp(bag))
            {
                picks++;
                holding = bag;
                holdingSince = now;
            }
        }

        void HostChores(BotContext c, float now)
        {
            if (!loadedScene && PickupStop.All.Count == 0 && now > 4f)
            {
                loadedScene = true;
                var dir = HouseDirectory.Load();
                var nm = c.Nm;
                if (dir != null && !string.IsNullOrEmpty(dir.sceneName) && nm != null && nm.SceneManager != null)
                {
                    Debug.Log($"[Bot] curb brain: loading {dir.sceneName} for the curbs.");
                    nm.SceneManager.LoadScene(dir.sceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
                }
            }
            if (now >= nextSupply)
            {
                nextSupply = now + 5f;
                EnsureTruck(c);
                if (GameSession.CurbBags.Count < 8) CurbTrashSpawner.DebugPutOut(4);
                if (GameSession.TruckFill01 > 0.75f) GameSession.TakeAllTruckBags(); // a quick dump at the depot
                foreach (var b in Businesses.All)
                {
                    var cargo = Businesses.HostCargo(b.Id);
                    if (cargo == null || !Businesses.TryGetTruckLoad(b.Id, out float kg, out float cap) || kg < cap * 0.75f) continue;
                    cargo.Clear();
                    Businesses.SetTruckLoad(b.Id, 0f, cap);
                }
            }
            if (now >= nextWage)
            {
                nextWage = now + 30f;
                foreach (var b in Businesses.All)
                {
                    if (b.IsHostBusiness) continue;
                    using (Ledger.ToBusiness(b.Id)) Ledger.Credit(2f, "Bot wage", LedgerIds.PickupFee);
                }
            }
            if (now >= nextOwner)
            {
                nextOwner = now + 45f;
                var all = Businesses.All;
                if (all.Count > 0)
                {
                    var owner = all[ownerTurn++ % all.Count];
                    foreach (var r in CarryReceivers.All)
                        if (r is TruckHopper h && h != null) h.BusinessId = owner.Id;
                    NetAudit.Event("botcurb", new Dictionary<string, object> { { "phase", "truck-owner" }, { "business", owner.Id }, { "name", owner.Name } });
                }
            }
        }

        bool spawnedTruck;

        /// <summary>The legacy scene flow brings the truck only when the host drives in: put one at the curbs (bots walk).</summary>
        void EnsureTruck(BotContext c)
        {
            if (spawnedTruck || PickupStop.All.Count == 0 || NearestHopper(Vector3.zero) != null) return;
            var nm = c.Nm;
            if (nm == null || !nm.IsServer) return;
            GameObject prefab = null;
            foreach (var p in nm.NetworkConfig.Prefabs.Prefabs)
                if (p != null && p.Prefab != null && p.Prefab.GetComponentInChildren<TruckHopper>(true) != null) { prefab = p.Prefab; break; }
            var stop = PickupStop.All[0];
            if (prefab == null || stop == null) return;
            spawnedTruck = true;
            Vector3 at = stop.transform.position + stop.transform.forward * 5f + Vector3.up * 0.6f;
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(stop.transform.right, Vector3.up));
            go.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
            Debug.Log($"[Bot] curb brain: spawned a truck at {at} for the curb run.");
        }

        void GuestChores(float now)
        {
            if (now >= nextTip)
            {
                nextTip = now + 20f;
                Ledger.Credit(1.25f, "Bot tip", LedgerIds.PickupFee);
            }
            if (now >= nextSpend)
            {
                nextSpend = now + 50f;
                Ledger.Spend(0.75f, "Bot snack");
            }
        }

        TrashBag PickBag(BotContext c)
        {
            // One of the three oldest bags this machine can see, so bots sometimes go for the same one (claim races).
            var list = new List<TrashBag>();
            foreach (var stop in PickupStop.All)
            {
                if (stop == null) continue;
                foreach (var b in stop.SpawnedBags)
                    if (b != null && b.State == CarryState.Resting && !b.IsInScriptedFlight && b.IsCurbBag) list.Add(b);
            }
            if (list.Count == 0) return null;
            list.Sort((a, b) => a.CurbRecord.putOutAtHours.CompareTo(b.CurbRecord.putOutAtHours));
            return list[Mathf.Min(list.Count - 1, (int)(c.Value() * 3f))];
        }

        static TruckHopper NearestHopper(Vector3 from)
        {
            TruckHopper best = null;
            float bestD = float.MaxValue;
            foreach (var r in CarryReceivers.All)
            {
                if (!(r is TruckHopper h) || h == null || !h.isActiveAndEnabled) continue;
                float d = (h.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        static void Teleport(GameObject player, Vector3 to)
        {
            var cc = player.GetComponent<CharacterController>();
            bool was = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            player.transform.position = to;
            if (cc != null) cc.enabled = was;
        }

        void Report(BotContext c, string phase)
        {
            NetAudit.Event("botcurb", new Dictionary<string, object>
            {
                { "phase", phase }, { "picks", picks }, { "tosses", tosses }, { "paid", paid }, { "paidCents", paidCents }, { "lost", lost },
                { "curbs", GameSession.CurbBags.Count }, { "business", GameSession.BusinessId }, { "cashCents", GameSession.CashCents },
                { "day", GameClock.Day }, { "hours", Math.Round(GameClock.TotalHours, 2) }, { "businesses", Businesses.OnlineCount },
            });
        }

        public void End(BotContext c)
        {
            c.Stop();
            WorldState.HopperPaid -= OnPaid;
            Report(c, "end");
        }
    }

    /// <summary>A brain whose real behaviour belongs to a later package: walks, and says so once.</summary>
    sealed class PlaceholderBrain : IBotBrain
    {
        readonly string name, owner;
        readonly WalkBrain walk = new WalkBrain();

        public PlaceholderBrain(string name, string owner)
        {
            this.name = name;
            this.owner = owner;
        }

        public void Begin(BotContext c)
        {
            Debug.Log($"[Bot] The '{name}' brain arrives with {owner}; walking until then.");
            NetAudit.Event("bot", new Dictionary<string, object> { { "phase", "placeholder-brain" }, { "brain", name }, { "owner", owner } });
            walk.Begin(c);
        }

        public void Tick(BotContext c, float dt) => walk.Tick(c, dt);
        public void End(BotContext c) => walk.End(c);
    }
}

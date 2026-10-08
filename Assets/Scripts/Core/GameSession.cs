using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Local game state that survives scene loads (cash, day, truck cargo, kept finds, world progress).
/// <para><b>Co-op (Docs/Multiplayer.md §13, package N1):</b> the business half of this class (cash, upgrades, inventory,
/// progression) is <b>the local player's own business</b> on every machine; the world half (curb bags, day) is the
/// host's Town. On a guest, cash and curb records are host-owned: they change only through <see cref="NetMirror"/>
/// (GameSession.Net.cs), and any other write to them is dropped and counted (<see cref="WritesBlocked"/>). Money is integer
/// cents inside; <see cref="Cash"/> stays a float for the ~40 callers.</para>
/// FROZEN CONTRACT: other systems call these members. Keep signatures stable.
/// </summary>
public static partial class GameSession
{
    // ---------- economy ----------
    public static int Day { get; private set; } = 1;
    /// <summary>The local player's business cash in dollars (backed by <see cref="CashCents"/>).</summary>
    public static float Cash => cashCents / 100f;
    /// <summary>The local player's business cash in whole cents (the stored and networked value).</summary>
    public static long CashCents => cashCents;
    static long cashCents;

    // ---------- truck ----------
    public static float TruckCapacityKg { get; private set; } = Economy.StartingTruckCapacityKg;
    /// <summary>Bags currently in the truck, in the order they were tossed in.</summary>
    public static readonly List<BagContents> TruckBags = new List<BagContents>();
    public static float TruckLoadKg
    {
        get
        {
            float m = 0f;
            for (int i = 0; i < TruckBags.Count; i++) m += TruckBags[i].TotalMassKg;
            return m;
        }
    }
    public static float TruckFill01 => TruckCapacityKg <= 0 ? 1f : Mathf.Clamp01(TruckLoadKg / TruckCapacityKg);

    /// <summary>Where the (single) truck was last left. Owned by the truck/scene-flow system.</summary>
    public static string TruckSceneName;
    public static Vector3 TruckPosition;
    public static Quaternion TruckRotation = Quaternion.identity;

    // ---------- kept finds ----------
    public static readonly List<ItemInstance> Inventory = new List<ItemInstance>();

    // ---------- world progress (reset each day) ----------
    /// <summary>bagIds that have left the curb today (into the truck). Stops don't respawn these.</summary>
    public static readonly HashSet<string> CollectedBagIds = new HashSet<string>();
    /// <summary>stopIds fully cleared today (route pay already given).</summary>
    public static readonly HashSet<string> ClearedStops = new HashSet<string>();
    public static bool RouteCompleteToday;

    // ---------- living world: curb trash (put out randomly by houses, never reset daily) ----------
    /// <summary>Every bag currently sitting at a curb in the neighborhood (world data, scene-independent).</summary>
    public static readonly List<CurbBag> CurbBags = new List<CurbBag>();
    /// <summary>Curb trash was added/removed. The HUD route planner and PickupStops listen to this.</summary>
    public static event Action CurbTrashChanged;

    public static void AddCurbBag(CurbBag bag)
    {
        if (bag == null || string.IsNullOrEmpty(bag.bagId)) return;
        if (BlockGuestWrite("AddCurbBag")) return;
        if (CurbBags.Exists(b => b.bagId == bag.bagId)) return;
        CurbBags.Add(bag);
        CurbTrashChanged?.Invoke();
        NotifyChanged();
    }

    /// <summary>Remove a curb bag record (picked up, burst, stolen...). Returns the removed record or null.</summary>
    public static CurbBag RemoveCurbBag(string bagId)
    {
        if (BlockGuestWrite("RemoveCurbBag")) return null;
        int i = CurbBags.FindIndex(b => b.bagId == bagId);
        if (i < 0) return null;
        var bag = CurbBags[i];
        CurbBags.RemoveAt(i);
        CurbTrashChanged?.Invoke();
        NotifyChanged();
        return bag;
    }

    public static CurbBag FindCurbBag(string bagId) => CurbBags.Find(b => b.bagId == bagId);

    public static int CurbBagCountAt(string stopId)
    {
        int n = 0;
        for (int i = 0; i < CurbBags.Count; i++) if (CurbBags[i].stopId == stopId) n++;
        return n;
    }

    // ---------- status effects ----------
    /// <summary>GameClock.TotalHours when the player got rabies, or -1 when healthy.</summary>
    public static double RabiesInfectedAtHours = -1;
    public static bool HasRabies => RabiesInfectedAtHours >= 0;
    public static event Action<bool> RabiesChanged;

    public static void Infect(double atHours)
    {
        if (HasRabies) return;
        // rabies_vaccine (Docs/Progression.md §9.2): a deterministic roll seeded by the bite time resists the infection.
        // coop:allow(random) seeded by the bite time, so every machine gets the same result (prescribed by Progression.md §9.2)
        if (InfectionResist01 > 0f && new System.Random(unchecked((int)(atHours * 1000))).NextDouble() < InfectionResist01)
        {
            InfectionResisted?.Invoke();
            return;
        }
        RabiesInfectedAtHours = atHours;
        RabiesChanged?.Invoke(true);
        NotifyChanged();
    }

    /// <summary>Restore a confirmed infection from the host without rerolling vaccine resistance.</summary>
    public static void RestoreRabies(double atHours)
    {
        if (atHours < 0 || RabiesInfectedAtHours == atHours) return;
        bool wasInfected = HasRabies;
        RabiesInfectedAtHours = atHours;
        if (!wasInfected) RabiesChanged?.Invoke(true);
        NotifyChanged();
    }

    public static void CureRabies()
    {
        if (!HasRabies) return;
        RabiesInfectedAtHours = -1;
        RabiesChanged?.Invoke(false);
        NotifyChanged();
    }

    // ---------- shop: upgrades + consumables (persisted) ----------
    static readonly Dictionary<string, int> upgradeLevels = new Dictionary<string, int>();
    static readonly Dictionary<string, int> consumables = new Dictionary<string, int>();
    public static event Action<string, int> UpgradeChanged;

    public static int GetUpgradeLevel(string id) => id != null && upgradeLevels.TryGetValue(id, out int l) ? l : 0;

    public static void SetUpgradeLevel(string id, int level)
    {
        if (string.IsNullOrEmpty(id)) return;
        upgradeLevels[id] = Mathf.Max(0, level);
        UpgradeChanged?.Invoke(id, level);
        NotifyChanged();
        SaveSoon();
    }

    public static int GetConsumable(string id) => id != null && consumables.TryGetValue(id, out int n) ? n : 0;

    public static void AddConsumable(string id, int delta)
    {
        if (string.IsNullOrEmpty(id)) return;
        consumables[id] = Mathf.Max(0, GetConsumable(id) + delta);
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>Called by GameClock when the in-game day rolls over (no world reset — just the counter + event).</summary>
    public static void SetDayFromClock(int day)
    {
        day = Mathf.Max(1, day);
        if (day == Day) return;
        Day = day;
        DayStarted?.Invoke(day);
        NotifyChanged();
        SaveSoon();
    }

    // ---------- lifetime stats (fun numbers for the HUD / ebag profile) ----------
    public static int TotalBagsCollected;
    public static int TotalItemsSorted;
    public static float TotalKgRecycled;
    public static int TotalTreasuresFound;

    // ---------- progression (Docs/Progression.md §9.2) ----------
    // Company-scoped: GameSession is "the company" for now; all of this moves into a CompanyState / save slot later.
    /// <summary>Lifetime Business Rep (can dip; rank never drops). Change it through Ledger.Rep, not AddRep.</summary>
    public static float Rep { get; private set; }
    /// <summary>Midnights that counted as Clean Days (§4.2).</summary>
    public static int CleanDays { get; private set; }
    public static bool KeyToCity { get; private set; }
    /// <summary>Sum of every positive AddCash (incremented inside AddCash).</summary>
    public static float TotalCashEarned { get; private set; }
    /// <summary>(delta, repId, districtId, worldPos). Fired on every Rep change.</summary>
    public static event Action<float, string, string, Vector3?> RepChanged;

    static readonly Dictionary<string, float> districtTrust = new Dictionary<string, float>();
    static readonly Dictionary<string, int> counters = new Dictionary<string, int>();
    static readonly Dictionary<string, double> stamps = new Dictionary<string, double>();

    /// <summary>Raw Rep change (no multipliers). New code calls Ledger.Rep / Ledger.RepTop, which apply the district multiplier.</summary>
    public static void AddRep(float delta, string repId, string districtId = null, Vector3? worldPos = null)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        Rep += delta;
        RepChanged?.Invoke(delta, repId, districtId, worldPos);
        NotifyChanged();
        SaveSoon();
    }

    public static void AddCleanDay()
    {
        CleanDays++;
        NotifyChanged();
        SaveSoon();
    }

    public static void GrantKeyToCity()
    {
        if (KeyToCity) return;
        KeyToCity = true;
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>District Trust 0..1; <paramref name="fallback"/> when never set (pass the district's trustStart).</summary>
    public static float GetDistrictTrust(string districtId, float fallback) =>
        districtId != null && districtTrust.TryGetValue(districtId, out float t) ? t : fallback;

    public static void SetDistrictTrust(string districtId, float trust01)
    {
        if (string.IsNullOrEmpty(districtId)) return;
        districtTrust[districtId] = Mathf.Clamp01(trust01);
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>Generic persisted ints (streaks, Work Order progress, highest rank, Trash-dex...). 0 when unset.</summary>
    public static int GetCounter(string key) => key != null && counters.TryGetValue(key, out int v) ? v : 0;

    public static void SetCounter(string key, int value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (counters.TryGetValue(key, out int old) && old == value) return;
        counters[key] = value;
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>Persisted GameClock.TotalHours stamps (gnome timers...). -1 when unset.</summary>
    public static double GetStamp(string key) => key != null && stamps.TryGetValue(key, out double v) ? v : -1;

    public static void SetStamp(string key, double totalHours)
    {
        if (string.IsNullOrEmpty(key)) return;
        stamps[key] = totalHours;
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>Bags a background crew collected for this business, waiting for the tipping floor (oldest first): the
    /// raccoon crew's hauls, and whatever Kevin's Crew had left in a save from before he moved on.</summary>
    public static readonly List<BagContents> DepotDeliveries = new List<BagContents>();

    public static void AddDepotDelivery(BagContents bag)
    {
        if (bag == null) return;
        DepotDeliveries.Add(bag);
        NotifyChanged();
        SaveSoon();
    }

    /// <summary>Removes and returns the oldest delivery, or null when empty.</summary>
    public static BagContents TakeDepotDelivery()
    {
        if (DepotDeliveries.Count == 0) return null;
        var b = DepotDeliveries[0];
        DepotDeliveries.RemoveAt(0);
        NotifyChanged();
        SaveSoon();
        return b;
    }

    /// <summary>0..1 chance a rabid bite doesn't infect. Set by Shop from rabies_vaccine; not saved (re-applied on spawn).</summary>
    public static float InfectionResist01;
    /// <summary>A bite was resisted by the vaccine (Infect returned without infecting).</summary>
    public static event Action InfectionResisted;

    // ---------- events ----------
    /// <summary>Any state changed. HUD listens to this.</summary>
    public static event Action Changed;
    /// <summary>(delta, reason, optional world position for floating text). Fired on every cash change.</summary>
    public static event Action<float, string, Vector3?> CashChanged;
    /// <summary>Fired when the in-game day counter increments (GameClock midnight). No world reset.</summary>
    public static event Action<int> DayStarted;

    public static void NotifyChanged() => Changed?.Invoke();

    public static void AddCash(float amount, string reason, Vector3? worldPos = null) => TryAddCash(amount, reason, worldPos, out _);

    /// <summary>
    /// Change the local business's cash by <paramref name="delta"/> dollars, rounded to whole cents. Refused (false, nothing
    /// applied) on a connected guest outside <see cref="NetMirror"/>: a guest's cash belongs to the host (§13.1). New code
    /// goes through <see cref="Ledger"/>, which posts exactly <paramref name="appliedCents"/>.
    /// </summary>
    public static bool TryAddCash(float delta, string reason, Vector3? worldPos, out long appliedCents)
    {
        appliedCents = ToCents(delta);
        if (appliedCents == 0) return false;
        if (BlockGuestWrite("AddCash")) { appliedCents = 0; return false; }
        ApplyCashCents(appliedCents, reason, worldPos);
        return true;
    }

    /// <summary>Spend if affordable. Returns false (and changes nothing) if not enough cash, or on a guest (host-owned cash).</summary>
    public static bool TrySpend(float amount, string reason)
    {
        long cents = ToCents(amount);
        if (cents > cashCents) return false;
        if (BlockGuestWrite("TrySpend")) return false;
        if (cents != 0) ApplyCashCents(-cents, reason, null);
        return true;
    }

    /// <summary>Dollars → whole cents (half away from zero), the one rounding rule for money.</summary>
    public static long ToCents(float dollars) => (long)Math.Round((double)dollars * 100.0, MidpointRounding.AwayFromZero);

    static void ApplyCashCents(long cents, string reason, Vector3? worldPos)
    {
        cashCents += cents;
        float amount = cents / 100f;
        if (cents > 0) TotalCashEarned += amount;
        CashChanged?.Invoke(amount, reason, worldPos);
        NotifyChanged();
        SaveSoon();
    }

    public static bool CanFitInTruck(float kg) => TruckLoadKg + kg <= TruckCapacityKg + 0.001f;

    public static void AddBagToTruck(BagContents bag)
    {
        if (bag == null) return;
        TruckBags.Add(bag);
        if (!string.IsNullOrEmpty(bag.bagId)) CollectedBagIds.Add(bag.bagId);
        TotalBagsCollected++;
        NotifyChanged();
    }

    /// <summary>Removes and returns the most recently loaded bag (for unloading one by one), or null.</summary>
    public static BagContents TakeTruckBag()
    {
        if (TruckBags.Count == 0) return null;
        var b = TruckBags[TruckBags.Count - 1];
        TruckBags.RemoveAt(TruckBags.Count - 1);
        NotifyChanged();
        return b;
    }

    public static List<BagContents> TakeAllTruckBags()
    {
        var all = new List<BagContents>(TruckBags);
        TruckBags.Clear();
        NotifyChanged();
        return all;
    }

    public static void SetTruckCapacity(float kg)
    {
        TruckCapacityKg = Mathf.Max(1f, kg);
        NotifyChanged();
        SaveSoon();
    }

    public static void AddToInventory(ItemInstance item)
    {
        if (item == null) return;
        Inventory.Add(item);
        // Only valuables are treasure finds: a kept part must not end Rookie Luck or pad the lifetime treasure count.
        if (item.Def == null || item.Def.category == ItemCategory.Valuable) TotalTreasuresFound++;
        NotifyChanged();
        SaveSoon();
    }

    public static bool RemoveFromInventory(string uid)
    {
        int i = Inventory.FindIndex(x => x.uid == uid);
        if (i < 0) return false;
        Inventory.RemoveAt(i);
        NotifyChanged();
        SaveSoon();
        return true;
    }

    public static void MarkStopCleared(string stopId)
    {
        if (ClearedStops.Add(stopId)) NotifyChanged();
    }

    public static void MarkRouteComplete()
    {
        if (RouteCompleteToday) return;
        RouteCompleteToday = true;
        NotifyChanged();
    }

    /// <summary>Fresh trash goes out: clears daily progress and increments Day.</summary>
    public static void StartNextDay()
    {
        Day++;
        CollectedBagIds.Clear();
        ClearedStops.Clear();
        RouteCompleteToday = false;
        DayStarted?.Invoke(Day);
        NotifyChanged();
        SaveSoon();
    }

    public static void RecordSorted(ItemInstance item, bool recycled)
    {
        TotalItemsSorted++;
        if (recycled && item != null) TotalKgRecycled += item.massKg;
    }

    /// <summary>
    /// True while saving is refused: this process is a guest (or was, until its own save is reloaded), or the save didn't
    /// load cleanly. Hotfix L0: the sticky latch lives in <see cref="SaveGuard"/>.
    /// </summary>
    public static bool SaveBlocked => !SaveGuard.WritesAllowed;

    /// <summary>
    /// True on a connected guest (a client that isn't the host). Guests never write the local save — see
    /// Docs/Multiplayer.md (companies/profiles travel with their owner; the Town save is the host's). Also SaveGuard's
    /// backstop: finding a guest here latches saving off until <see cref="SaveGuard.ReloadOwn"/>.
    /// </summary>
    public static bool IsGuest
    {
        get
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool guest = nm != null && nm.IsListening && !nm.IsServer;
            if (guest) SaveGuard.MarkForeign("connected as a guest");
            return guest;
        }
    }

    /// <summary>Stable-ish unique id for new ItemInstances.</summary>
    public static string NewUid() => Guid.NewGuid().ToString("N").Substring(0, 12);

    // ---------- persistence (cash, day, capacity, inventory, stats) ----------
    [Serializable]
    class SaveData
    {
        public int day = 1;
        public float cash;
        public float truckCapacityKg = Economy.StartingTruckCapacityKg;
        public List<ItemInstance> inventory = new List<ItemInstance>();
        public int totalBags, totalItems, totalTreasures;
        public float totalKgRecycled;
        public List<string> upgradeIds = new List<string>();
        public List<int> upgradeLevels = new List<int>();
        public List<string> consumableIds = new List<string>();
        public List<int> consumableCounts = new List<int>();

        // v2: progression (Docs/Progression.md §9.2). A v1 file has none of these: they load as their defaults.
        public int saveVersion;                     // 0 = a v1 file (pre-progression); Save writes CurrentSaveVersion
        public float rep;
        public int cleanDays;
        public bool keyToCity;
        public float totalCashEarned;
        public List<string> trustIds = new List<string>();
        public List<float> trustValues = new List<float>();
        public List<string> counterIds = new List<string>();
        public List<int> counterValues = new List<int>();
        public List<string> stampIds = new List<string>();
        public List<double> stampValues = new List<double>();
        public List<BagContents> depotDeliveries = new List<BagContents>();

        // Hotfix L0: both legacy files carry the same gen on every save, so recovery can pick a matching pair.
        public long gen;

        // v3 (co-op N1, Docs/Multiplayer.md §13): money in cents (cash stays written for older builds), and this save's
        // business identity. A v2 file has none of these: cash converts, the id is made on the next save, the name defaults.
        public long cashCents;
        public string businessId;
        public string businessName;
        // The Town's time of day when last saved by its host (-1 = never: a fresh Town starts at 7 AM).
        public float clockHours = -1f;
        // Additive business-owned workshop data; absent in older saves means an empty workshop.
        public WorkshopState workshop;
    }

    /// <summary>The host's time of day from the save (-1 = none). GameClock restores it when a hosted session starts.</summary>
    public static float SavedClockHours { get; private set; } = -1f;

    const int CurrentSaveVersion = 6;

    /// <summary>The local business's id (made once, saved with it) and name ("Rubbish Co." by default).</summary>
    public static string BusinessId { get; private set; }
    public static string BusinessName { get; private set; } = BusinessColors.DefaultBusinessName;

    /// <summary>Set only after this save loaded cleanly: a good parse, a recovered backup, or no file yet (hotfix L0).</summary>
    static bool loaded;

    /// <summary>
    /// Save both legacy files (hotfix L0: <c>LegacySave.WriteBoth</c>, under the same gen as ebag's). At most one write per
    /// 2 s (later calls are flushed when the window ends, and always on quit, pause and scene change). Refused while
    /// <see cref="SaveGuard"/> is latched or the save didn't load cleanly, and never from edit mode.
    /// </summary>
    public static void Save() => LegacySave.WriteBoth(false);

    /// <summary>This session's save as JSON; LegacySave writes it beside ebag's half under the same <paramref name="gen"/>.</summary>
    internal static string BuildSaveJson(long gen) => BuildSaveJson(gen, Day);

    /// <summary>As <see cref="BuildSaveJson(long)"/> with the day number given: a guest handing its business back writes its
    /// own Town's day, not the host's (BusinessSlots).</summary>
    internal static string BuildSaveJson(long gen, int day)
    {
        if (string.IsNullOrEmpty(BusinessId)) BusinessId = BusinessIds.New();
        var d = new SaveData
        {
            day = Mathf.Max(1, day), cash = Cash, cashCents = cashCents, businessId = BusinessId, businessName = BusinessName,
            clockHours = GameClock.IsHostClock ? GameClock.Hours : SavedClockHours,
            truckCapacityKg = TruckCapacityKg,
            inventory = new List<ItemInstance>(Inventory),
            totalBags = TotalBagsCollected, totalItems = TotalItemsSorted,
            totalTreasures = TotalTreasuresFound, totalKgRecycled = TotalKgRecycled,
            saveVersion = CurrentSaveVersion, rep = Rep, cleanDays = CleanDays, keyToCity = KeyToCity,
            totalCashEarned = TotalCashEarned, depotDeliveries = new List<BagContents>(DepotDeliveries), workshop = WorkshopSave
        };
        foreach (var kv in upgradeLevels) { d.upgradeIds.Add(kv.Key); d.upgradeLevels.Add(kv.Value); }
        foreach (var kv in consumables) { d.consumableIds.Add(kv.Key); d.consumableCounts.Add(kv.Value); }
        foreach (var kv in districtTrust) { d.trustIds.Add(kv.Key); d.trustValues.Add(kv.Value); }
        foreach (var kv in counters) { d.counterIds.Add(kv.Key); d.counterValues.Add(kv.Value); }
        foreach (var kv in stamps) { d.stampIds.Add(kv.Key); d.stampValues.Add(kv.Value); }
        d.gen = gen;
        return JsonUtility.ToJson(d, true);
    }

    internal static bool SaveJsonParses(string json)
    {
        try { return JsonUtility.FromJson<SaveData>(json) != null; }
        catch (Exception) { return false; }
    }

    // Cash ticks a lot while sorting: every change asks for a save and LegacySave coalesces them (at most one write per 2 s).
    static void SaveSoon()
    {
        if (!loaded) return;
        LegacySave.WriteBoth(false);
    }

    /// <summary>
    /// Load the save (hotfix L0). <c>loaded</c> is set only after a good parse, a recovered backup or a missing file. An
    /// unreadable file is copied to *.corrupt-*.json and the newest backup present in BOTH legacy files is used; if nothing
    /// parses, saving stays off for the session (defaults are never written over an unreadable file).
    /// </summary>
    public static void Load()
    {
        loaded = LegacySave.Load(LegacySave.Part.GameSession, ApplySaveJson);
    }

    static void ApplySaveJson(string json)
    {
        var d = JsonUtility.FromJson<SaveData>(json);
        if (d == null) throw new FormatException("rubbish_save.json is not a GameSession save");
        Day = Mathf.Max(1, d.day);
        cashCents = d.saveVersion >= 3 ? d.cashCents : ToCents(d.cash);
        BusinessId = BusinessIds.IsValid(d.businessId) ? d.businessId : null;
        BusinessName = BusinessColors.CleanName(d.businessName);
        SavedClockHours = d.clockHours >= 0f && d.clockHours < 24f ? d.clockHours : -1f;
        TruckCapacityKg = d.truckCapacityKg > 0 ? d.truckCapacityKg : Economy.StartingTruckCapacityKg;
        Inventory.Clear();
        if (d.inventory != null) Inventory.AddRange(d.inventory);
        TotalBagsCollected = d.totalBags;
        TotalItemsSorted = d.totalItems;
        TotalTreasuresFound = d.totalTreasures;
        TotalKgRecycled = d.totalKgRecycled;
        upgradeLevels.Clear();
        if (d.upgradeIds != null && d.upgradeLevels != null)
            for (int i = 0; i < d.upgradeIds.Count && i < d.upgradeLevels.Count; i++) upgradeLevels[d.upgradeIds[i]] = d.upgradeLevels[i];
        // Before the vehicle progression every business already owned the full-size truck.
        // Only new businesses start with the mini; loading an old save must never confiscate a truck or its mods.
        if (d.saveVersion < 4) upgradeLevels[ShopIds.TruckModel] = TruckProgression.RearLoader;
        Districts.MigrateLegacyPermits(upgradeLevels, d.saveVersion);
        // Auto-Tipper is one level now (tipping moved onto the truck): an owned level 2 becomes level 1 and its price comes
        // back. Idempotent without a version gate: once migrated, the save says level 1.
        cashCents += UpgradeMigrations.MigrateAutoTipper(upgradeLevels);
        TruckCapacityKg = TruckProgression.CapacityKg(GetUpgradeLevel(ShopIds.TruckModel), GetUpgradeLevel(ShopIds.TruckCapacity));
        consumables.Clear();
        if (d.consumableIds != null && d.consumableCounts != null)
            for (int i = 0; i < d.consumableIds.Count && i < d.consumableCounts.Count; i++) consumables[d.consumableIds[i]] = d.consumableCounts[i];

        // v2 progression; every field tolerates a v1 file (missing -> default / empty).
        Rep = d.rep;
        CleanDays = Mathf.Max(0, d.cleanDays);
        KeyToCity = d.keyToCity;
        TotalCashEarned = Mathf.Max(0f, d.totalCashEarned);
        districtTrust.Clear();
        if (d.trustIds != null && d.trustValues != null)
            for (int i = 0; i < d.trustIds.Count && i < d.trustValues.Count; i++)
                if (!string.IsNullOrEmpty(d.trustIds[i])) districtTrust[d.trustIds[i]] = Mathf.Clamp01(d.trustValues[i]);
        counters.Clear();
        if (d.counterIds != null && d.counterValues != null)
            for (int i = 0; i < d.counterIds.Count && i < d.counterValues.Count; i++)
                if (!string.IsNullOrEmpty(d.counterIds[i])) counters[d.counterIds[i]] = d.counterValues[i];
        if (d.saveVersion < 4) counters[StarterCollectionContract.ProgressKey] = StarterCollectionContract.TargetBags;
        // v6: Kevin's Crew was removed. A save that owned him gets its purchase price back here, as part of the balance it
        // loads with: exactly once (the level is cleared and the version moves on), with no Ledger posting (a refund is not
        // income: no payroll share, no day stats, no Rep) and no event. A brand-new business never passes through here.
        cashCents += KevinSeverance.Migrate(upgradeLevels, counters, d.saveVersion);
        stamps.Clear();
        if (d.stampIds != null && d.stampValues != null)
            for (int i = 0; i < d.stampIds.Count && i < d.stampValues.Count; i++)
                if (!string.IsNullOrEmpty(d.stampIds[i])) stamps[d.stampIds[i]] = d.stampValues[i];
        DepotDeliveries.Clear();
        if (d.depotDeliveries != null)
            foreach (var b in d.depotDeliveries) if (b != null) DepotDeliveries.Add(b);
        // The Workshop's retired material bank pays out here, once, as part of the balance the save loads with (bin rates;
        // WorkshopBankRefund). Idempotent without a version gate: a converted save has no bank left to convert.
        cashCents += LoadWorkshop(d.workshop);
        NotifyChanged();
    }

    /// <summary>Wipe progress (debug / new game).</summary>
    public static void ResetAll(bool deleteSave)
    {
        // Hotfix L0: a "delete" rewrites the file with the reset state (the old progress stays in the generations), and is
        // refused entirely while SaveGuard is latched or the save didn't load cleanly.
        if (deleteSave && !LegacySave.MayReset("GameSession.ResetAll(true)")) return;
        Day = 1; cashCents = 0; TruckCapacityKg = Economy.StartingTruckCapacityKg;
        BusinessId = null; BusinessName = BusinessColors.DefaultBusinessName; SavedClockHours = -1f;
        TruckBags.Clear(); Inventory.Clear(); CollectedBagIds.Clear(); ClearedStops.Clear();
        RouteCompleteToday = false; TruckSceneName = null;
        CurbBags.Clear(); RabiesInfectedAtHours = -1; upgradeLevels.Clear(); consumables.Clear();
        TotalBagsCollected = TotalItemsSorted = TotalTreasuresFound = 0; TotalKgRecycled = 0;
        Rep = 0; CleanDays = 0; KeyToCity = false; TotalCashEarned = 0;
        districtTrust.Clear(); counters.Clear(); stamps.Clear(); DepotDeliveries.Clear(); InfectionResist01 = 0;
        LoadWorkshop(null);
        NotifyChanged();
        if (deleteSave) LegacySave.WriteChangeNow("GameSession.ResetAll(true)");
    }

    // Statics survive play sessions when domain reload is disabled; start clean each play session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Changed = null; CashChanged = null; DayStarted = null;
        CurbTrashChanged = null; RabiesChanged = null; UpgradeChanged = null;
        RepChanged = null; InfectionResisted = null;
        loaded = false;
        LegacySave.ResetForPlaySession(); // data root, SaveGuard latch, gen, pending writes: before the first Load
        ResetAll(false);
        Load();
    }

    // LegacySave's runner (the 2 s flush and the pause/quit/scene-change saves) must exist from the start of a play session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootSaveFlusher() => LegacySave.EnsureRunner();
}

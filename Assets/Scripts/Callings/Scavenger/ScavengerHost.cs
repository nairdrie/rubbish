using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Where a house's front door is, on any machine (the loaded town when it is up, else the baked street graph).</summary>
public static class ScavHouses
{
    static StreetGraph graph;
    static bool graphTried;

    public static bool TryDoor(string stopId, out Vector3 door) => TryDoor(PickupStop.Find(stopId), stopId, out door);
    /// <summary>The same for a stop already in hand (the per-frame scans: no lookup by id).</summary>
    public static bool TryDoor(PickupStop stop, out Vector3 door) => TryDoor(stop, stop != null ? stop.stopId : null, out door);

    static bool TryDoor(PickupStop stop, string stopId, out Vector3 door)
    {
        door = Vector3.zero;
        if (string.IsNullOrEmpty(stopId)) return false;
        var route = stop != null ? stop.GetComponent<CoastalHouseRoute>() : null;
        if (route != null && route.door.sqrMagnitude > 0.01f) { door = route.door; return true; }
        if (!graphTried) { graphTried = true; graph = StreetGraph.Load(); }
        if (graph == null || graph.houses == null) return false;
        int h = graph.HouseIndex(stopId);
        if (h < 0 || graph.houses[h].door >= graph.NodeCount) return false;
        door = graph.nodes[graph.houses[h].door].pos;
        return true;
    }

    /// <summary>
    /// Is this a house the Scavenger may burgle? NPC homes only (Docs/Multiplayer.md 4.6: never stealing from depots,
    /// trucks or other players). A target must be a household in the Town's house directory, its door must not stand on a
    /// Depot Row lot or inside a shop, and it must belong to a real district.
    /// </summary>
    public static bool IsNpcHome(string stopId, out CurbTrashSpawner.House house, out Vector3 door)
    {
        door = Vector3.zero;
        house = CurbTrashSpawner.FindHouse(stopId);
        if (house == null || house.District == null || !TryDoor(stopId, out door)) return false;
        return DepotRowLayout.PadAt(door) < 0 && DepotRowLayout.InteriorAt(door) < 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { graph = null; graphTried = false; }
}

/// <summary>What the host tells you after you case a house. Counts are what YOU can make out, never names.</summary>
public struct CaseInfo
{
    public CaseStatus status;
    public byte awake, asleep, street, players, dogs;
    /// <summary>Chance of being seen with a clean pick, 0..100.</summary>
    public byte risk;
    /// <summary>Chance the lock gives with a clean pick, 0..100.</summary>
    public byte lockOdds;
    public byte richTier, readyInDays, suspicion;
}

public struct CallResult
{
    /// <summary>0 = refused (see why), 1 = wrong, 2 = called it.</summary>
    public byte status;
    public byte called, actual;
    public long bonusCents;
    public string why;
}

public struct BreakInResult
{
    public BreakInOutcome outcome;
    public string stopId, districtId, why;
    /// <summary>JSON of a <see cref="ScavLoot"/> ("" when nothing was taken).</summary>
    public string lootJson;
    public long fineCents;
    public string letterSubject, letterBody;
    public byte readyInDays;
}

public struct FenceResult
{
    public bool ok;
    public string uid, why;
    public long cents;
}

/// <summary>Stolen goods on the wire (JsonUtility needs a wrapper for a list).</summary>
[Serializable]
public sealed class ScavLoot { public List<ItemInstance> items = new List<ItemInstance>(); }

/// <summary>
/// Everything the Scavenger calling DECIDES. Runs only on the machine with authority (the host, or a solo game): guests
/// reach it through the guarded RPCs in <c>HandsNet.Scavenger.cs</c>, the host's own player calls it directly
/// (<see cref="Scavenger"/>). Every roll comes from the host dice; every level is read from the replicated workshop state
/// of the business the client OWNS, so an employee burgles on their own account, never their employer's.
/// </summary>
public static class ScavengerHost
{
    public static bool Authority => !GameSession.IsConnectedGuest;

    sealed class Casing { public string stopId; public double at, startedAt = -1; }
    static readonly Dictionary<ulong, Casing> casings = new Dictionary<ulong, Casing>();
    static readonly Dictionary<string, int> callDayByPlayer = new Dictionary<string, int>(StringComparer.Ordinal);
    static readonly HashSet<string> calledBags = new HashSet<string>(StringComparer.Ordinal);
    static readonly HashSet<string> soldUids = new HashSet<string>(StringComparer.Ordinal);
    static readonly Dictionary<ulong, int> hotGiven = new Dictionary<ulong, int>();
    static int lootSerial;
    /// <summary>Suspicion at which the Neighbourhood Watch bulletin goes out (once on the way up).</summary>
    public const int WatchLetterAt = 3;

    static double Now => Time.realtimeSinceStartupAsDouble;
    static float Sq(float v) => v * v;
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static int Level(ulong client, string id) => Workshop.CallingLevelForClient(client, id);
    static bool IsLocal(ulong client) => client == Permissions.LocalClientId;

    internal static bool PlayerAt(ulong client, out Vector3 pos, out PlayerController player)
    {
        var body = PlayerBody.For(client);
        player = body != null ? body.Player : IsLocal(client) ? PlayerController.Local : null; // coop:allow(local) a solo player has no spawned PlayerBody; a remote client never falls back to this machine's player
        pos = body != null ? body.transform.position : player != null ? player.transform.position : Vector3.zero;
        return player != null;
    }

    static bool Busy(PlayerController player) => player == null || player.IsDriving || player.IsPlayingChess;

    /// <summary>The Ledger scope for a client's OWN business (null = this machine's). False when a remote client's
    /// business is not connected yet.</summary>
    static bool OwnBusiness(ulong client, out string ledgerTarget, out string businessId)
    {
        if (IsLocal(client)) { ledgerTarget = null; businessId = Businesses.LocalId; return true; }
        if (Businesses.TryGetForClient(client, out var b)) { ledgerTarget = businessId = b.Id; return true; }
        ledgerTarget = businessId = null; return false;
    }

    static string PlayerKeyOf(ulong client) => SessionService.TryGetPlayer(client, out var key) && key.IsValid ? key.Value : "client:" + client;

    /// <summary>Two players on the same payroll (boss and hire, or two hires of one boss) are a crew: a lookout, not a witness.</summary>
    internal static bool SameCrew(ulong a, ulong b)
    {
        bool ja = Payroll.TryGetJobOf(a, out var jobA), jb = Payroll.TryGetJobOf(b, out var jobB);
        return ja && jobA.employerClientId == b || jb && jobB.employerClientId == a || ja && jb && jobA.employerClientId == jobB.employerClientId;
    }

    // ================================================================================================ X-RAY SPECS

    /// <summary>The real contents of a bag the host knows, and where it is (located false = only a record, at its curb).</summary>
    internal static BagContents FindContents(string bagId, uint netId, out Vector3 at, out bool located)
    {
        at = Vector3.zero; located = false;
        if (string.IsNullOrEmpty(bagId)) return null;
        var hands = HandsNet.Instance;
        var carry = netId != 0 && hands != null ? hands.Get(netId) : null;
        if (carry != null && carry.Item is TrashBag held && held.Contents != null && held.Contents.bagId == bagId)
        { at = held.WorldCenter; located = true; return held.Contents; }
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] is TrashBag bag && bag.Contents != null && bag.State != CarryState.Consumed && !bag.HasBurst && bag.Contents.bagId == bagId)
            { at = bag.WorldCenter; located = true; return bag.Contents; }
        var record = GameSession.FindCurbBag(bagId);
        if (record == null || record.contents == null) return null;
        var house = CurbTrashSpawner.FindHouse(record.stopId);
        if (house != null) { at = house.position; located = true; }
        return record.contents;
    }

    /// <summary>What <paramref name="client"/>'s specs show of this bag (level 0 = nothing: no specs, too far, no such bag).</summary>
    public static XRaySummary Peek(ulong client, string bagId, uint netId)
    {
        int level = Level(client, CallingIds.XRaySpecs);
        if (!Authority || level <= 0 || !PlayerAt(client, out var pos, out _)) return default;
        var contents = FindContents(bagId, netId, out var at, out bool located);
        if (contents == null || !located || Flat(pos - at).sqrMagnitude > Sq(ScavengerTuning.XRayRange + ScavengerTuning.XRayHostSlack)) return default;
        var summary = ScavengerRules.Summarize(contents, level);
        if (Workshop.AnyWantedBy(client, contents.items)) summary.counts |= XRaySummary.WantedBit; // "WANTED PART!" on the plate
        return summary;
    }

    // ================================================================================================ CALL IT

    public static CallResult CallIt(ulong client, string bagId, uint netId, int band)
    {
        var result = new CallResult { called = (byte)Mathf.Clamp(band, 0, 255) };
        if (!Authority) { result.why = "Only the host decides that."; return result; }
        if (Level(client, CallingIds.CallIt) <= 0) { result.why = "Build Call It first."; return result; }
        if (band < 0 || band >= ScavengerRules.CallBands) { result.why = "That is not a call."; return result; }
        if (!OwnBusiness(client, out string target, out _)) { result.why = "Your business isn't connected yet."; return result; }
        if (!PlayerAt(client, out var pos, out _)) { result.why = "Where are you?"; return result; }
        var contents = FindContents(bagId, netId, out var at, out bool located);
        if (contents == null || !located || Flat(pos - at).sqrMagnitude > Sq(ScavengerTuning.CallRange + ScavengerTuning.XRayHostSlack))
        { result.why = "Hold the bag you are calling."; return result; }
        int today = GameClock.Day; string who = PlayerKeyOf(client);
        bool usedToday = callDayByPlayer.TryGetValue(who, out int last) && last == today ||
                         IsLocal(client) && !ScavengerRules.CallAvailable(GameSession.GetCounter(ScavengerIds.CallItDay), today);
        if (usedToday) { result.why = "One call a day. Come back tomorrow."; return result; }
        if (!calledBags.Add(bagId)) { result.why = "Somebody already called this bag."; return result; }
        callDayByPlayer[who] = today;

        int actual = ScavengerRules.CallBandOf(contents, EbaeMarket.Appraise);
        float bonus = ScavengerRules.CallPayout(band, actual);
        result.actual = (byte)actual; result.status = (byte)(bonus > 0f ? 2 : 1); result.bonusCents = GameSession.ToCents(bonus);
        if (bonus > 0f)
            using (Ledger.ToBusiness(target)) Ledger.Credit(bonus, "Called it!", ScavengerIds.LedgerCallIt, at + Vector3.up * 1.2f);
        return result;
    }

    // ================================================================================================ BURGLARY: CASING

    /// <summary>Who is about, as numbers. <paramref name="kinds"/> gets a ScavengerEvents.Saw* bit for every source that
    /// could see anything at all.</summary>
    static DetectionInput Sense(ulong client, CurbTrashSpawner.House house, Vector3 door, int misses, ref CaseInfo info, out byte kinds)
    {
        var d = new DetectionInput
        {
            hour = GameClock.Hours, misses = misses,
            softSoles = Level(client, CallingIds.SoftSoles), lockpicks = Level(client, CallingIds.Lockpicks),
            suspicion = GameSession.GetCounter(ScavengerIds.Suspicion(house.districtId)),
        };
        kinds = 0;
        int street = 0, players = 0, dogs = 0;
        bool scheduled = false;
        var sim = LifeSim.Active;
        if (sim != null)
        {
            try
            {
                double hours = GameClock.TotalHours;
                int hh = sim.Roster.HouseholdIndex(house.stopId);
                if (hh >= 0)
                {
                    var hold = sim.Roster.households[hh];
                    bool bed = ScavengerRules.IsBedtime(d.hour);
                    for (int k = 0; k < hold.residentCount; k++)
                        if (sim.IsHome(hold.firstResident + k, hours)) { if (bed) d.asleepHome++; else d.awakeHome++; }
                    scheduled = true;
                }
                // Neighbours out on foot, straight from the timetable (their puppets only exist near a camera, and the
                // host's camera may be across town from a guest burglar).
                int dayIndex = (int)Math.Floor(hours / 24.0);
                var today = sim.Today;
                var day = sim.OtherDay(dayIndex, today.day == dayIndex ? today.mask : WorldLifeIds.UnlockedDistrictMask());
                double t = day.MsOf(hours);
                var residents = sim.Roster.residents;
                for (int r = 0; r < residents.Length; r++)
                {
                    if (hh >= 0 && residents[r].household == hh) continue; // the household itself is counted indoors
                    int ex = day.ExcursionAt(r, t);
                    if (ex < 0 || !day.Evaluate(ex, t, out var at, out _, out _, out _, out _)) continue;
                    float w = ScavengerRules.SightWeight(Flat(at - door).magnitude, ScavengerTuning.WitnessRadius, d.softSoles);
                    if (w <= 0f) continue;
                    d.streetWitness += w; street++;
                }
            }
            catch (Exception e) { Debug.LogException(e); scheduled = false; d.awakeHome = d.asleepHome = 0; }
        }
        if (!scheduled) ScavengerRules.FallbackHousehold(d.hour, out d.awakeHome, out d.asleepHome);

        var bodies = PlayerBody.All;
        for (int i = 0; i < bodies.Count; i++)
        {
            var pb = bodies[i];
            if (pb == null || pb.ClientId == client || pb.ClientId == HandsNet.None || SameCrew(client, pb.ClientId)) continue;
            float w = ScavengerRules.SightWeight(Flat(pb.transform.position - door).magnitude, ScavengerTuning.WitnessRadius, d.softSoles);
            if (w <= 0f) continue;
            d.playerWitness += w; players++;
        }
        var yard = YardDogs.Instance;
        if (yard != null)
            foreach (var dog in yard.Dogs)
            {
                if (dog == null) continue;
                float w = ScavengerRules.SightWeight(Flat(dog.pos - door).magnitude, ScavengerTuning.DogRadius, d.softSoles);
                if (w <= 0f) continue;
                d.dogs += w; dogs++;
            }
        if (d.awakeHome + d.asleepHome > 0) kinds |= ScavengerEvents.SawIndoors;
        if (street > 0) kinds |= ScavengerEvents.SawStreet;
        if (players > 0) kinds |= ScavengerEvents.SawPlayer;
        if (dogs > 0) kinds |= ScavengerEvents.SawDog;
        info.awake = (byte)Mathf.Min(9, d.awakeHome); info.asleep = (byte)Mathf.Min(9, d.asleepHome);
        info.street = (byte)Mathf.Min(9, street); info.players = (byte)Mathf.Min(9, players); info.dogs = (byte)Mathf.Min(9, dogs);
        info.suspicion = (byte)Mathf.Clamp(d.suspicion, 0, ScavengerTuning.SuspicionMax);
        return d;
    }

    /// <summary>Shared door checks. Ok means: this client may work on this door right now.</summary>
    static CaseStatus CheckDoor(ulong client, string stopId, float range, out CurbTrashSpawner.House house, out Vector3 door, out int readyDay)
    {
        house = null; door = Vector3.zero; readyDay = 0;
        if (Level(client, CallingIds.TheItch) <= 0) return CaseStatus.NoItch;
        if (!PlayerAt(client, out var pos, out var player) || Busy(player)) return CaseStatus.Busy;
        if (!ScavHouses.IsNpcHome(stopId, out house, out door)) return CaseStatus.NotAHouse;
        if (Flat(pos - door).sqrMagnitude > Sq(range) || Mathf.Abs(pos.y - door.y) > 4f) return CaseStatus.TooFar;
        if (!DistrictAccess.OpenInTown(house.District)) return CaseStatus.DistrictClosed;
        readyDay = GameSession.GetCounter(ScavengerIds.HouseReadyDay(stopId));
        return ScavengerRules.CooldownStatus(readyDay, GameClock.Day);
    }

    public static CaseInfo Case(ulong client, string stopId)
    {
        var info = new CaseInfo { status = CaseStatus.Busy };
        if (!Authority) return info;
        info.status = CheckDoor(client, stopId, ScavengerTuning.DoorRange + ScavengerTuning.DoorHostSlack, out var house, out var door, out int readyDay);
        if (house != null)
        {
            info.richTier = (byte)ScavengerRules.RichTier(house.wealth, house.District.tier);
            info.readyInDays = (byte)Mathf.Clamp(readyDay - GameClock.Day, 0, 99);
        }
        if (info.status != CaseStatus.Ok) { casings.Remove(client); return info; }
        var d = Sense(client, house, door, 0, ref info, out _);
        info.risk = (byte)Mathf.RoundToInt(ScavengerRules.DetectionChance(d) * 100f);
        info.lockOdds = (byte)Mathf.RoundToInt(ScavengerRules.LockOdds(d.lockpicks, 0) * 100f);
        casings[client] = new Casing { stopId = stopId, at = Now };
        return info;
    }

    /// <summary>The pick went into the lock: start the host's own stopwatch for this attempt.</summary>
    public static bool BreakStart(ulong client, string stopId)
    {
        if (!Authority || !casings.TryGetValue(client, out var c) || c.stopId != stopId || Now - c.at > ScavengerTuning.CaseValidSeconds) return false;
        if (CheckDoor(client, stopId, ScavengerTuning.DoorRange + ScavengerTuning.DoorHostSlack, out _, out _, out _) != CaseStatus.Ok) return false;
        c.startedAt = Now;
        return true;
    }

    // ================================================================================================ BURGLARY: THE DOOR

    static BreakInResult Deny(string stopId, string why) => new BreakInResult { outcome = BreakInOutcome.Denied, stopId = stopId, why = why, lootJson = "" };

    public static BreakInResult BreakFinish(ulong client, string stopId, int misses, int hotHeldReported)
    {
        if (!Authority) return Deny(stopId, "Only the host decides that.");
        if (!casings.TryGetValue(client, out var casing) || casing.stopId != stopId || casing.startedAt < 0) return Deny(stopId, "Case the joint first.");
        casings.Remove(client);
        int lockpicks = Level(client, CallingIds.Lockpicks), sack = Level(client, CallingIds.SwagSack);
        double elapsed = Now - casing.startedAt, need = ScavengerRules.PickSeconds(lockpicks);
        if (elapsed < need * 0.7 - 0.25 || elapsed > need * 2.0 + 20.0) return Deny(stopId, "The lock needs a proper go.");
        var status = CheckDoor(client, stopId, ScavengerTuning.BailRange + ScavengerTuning.DoorHostSlack, out var house, out var door, out _);
        if (status != CaseStatus.Ok) return Deny(stopId, status == CaseStatus.TooFar ? "You wandered off." : "Not this house, not now.");
        if (!OwnBusiness(client, out string target, out string businessId)) return Deny(stopId, "Your business isn't connected yet.");

        misses = Mathf.Clamp(misses, 0, ScavengerTuning.MaxMisses);
        int hotHeld = IsLocal(client) ? ScavengerRules.HotCount(GameSession.Inventory)
            : Mathf.Max(Mathf.Clamp(hotHeldReported, 0, 64), hotGiven.TryGetValue(client, out int given) ? given : 0);
        if (hotHeld >= ScavengerRules.SackCapacity(sack)) return Deny(stopId, "Your sack is full. See the fence first.");

        var district = house.District; int today = GameClock.Day;
        var info = new CaseInfo();
        var sensed = Sense(client, house, door, misses, ref info, out byte kinds);
        var dice = NightDice.Host;
        bool seen = dice.Chance(ScavengerRules.DetectionChance(sensed));
        bool opened = dice.Chance(ScavengerRules.LockOdds(lockpicks, misses));
        var outcome = ScavengerRules.Outcome(opened, seen);

        var loot = new ScavLoot();
        if (opened)
        {
            int count = ScavengerRules.LootCount(house.wealth, sack, hotHeld, seen);
            string prefix = $"hot{today}-{(uint)PickupVisuals.StableHash(stopId) & 0xFFFF:x4}-{++lootSerial:x}-{dice.Next() & 0xFFFFFF:x6}";
            loot.items = RollLoot(house.wealth, district, count, () => dice.Value, house.address, today, prefix);
        }
        long lootCents = 0;
        foreach (var item in loot.items) lootCents += GameSession.ToCents(EbaeMarket.Appraise(item));
        if (!IsLocal(client)) hotGiven[client] = hotHeld + loot.items.Count;

        int readyDay = ScavengerRules.ReadyDayAfter(today, opened, seen);
        if (readyDay > today) GameSession.SetCounter(ScavengerIds.HouseReadyDay(stopId), readyDay);
        if (opened || seen)
        {
            string key = ScavengerIds.Suspicion(district.id);
            GameSession.SetCounter(key, ScavengerRules.SuspicionAfterTheft(GameSession.GetCounter(key), seen));
        }

        var result = new BreakInResult
        {
            outcome = outcome, stopId = stopId, districtId = district.id, why = "",
            lootJson = loot.items.Count > 0 ? JsonUtility.ToJson(loot) : "", readyInDays = (byte)Mathf.Clamp(readyDay - today, 0, 99),
        };
        result.letterSubject = result.letterBody = "";
        if (seen) result.fineCents = ApplyConsequences(client, target, house, district, door, opened, out result.letterSubject, out result.letterBody);
        else if (opened)
        {
            // Nobody saw, but the district keeps count: the first time it gets properly jumpy, the Watch writes.
            int suspicion = GameSession.GetCounter(ScavengerIds.Suspicion(district.id));
            if (suspicion == WatchLetterAt)
                BurglaryLetters.WatchBulletin(WorldLifeIds.DistrictIndex(district), district.name, suspicion, out result.letterSubject, out result.letterBody);
        }

        ScavengerEvents.RaiseTheft(new TheftFact
        {
            clientId = client, playerKey = SessionService.TryGetPlayer(client, out var pk) && pk.IsValid ? pk.Value : "", businessId = businessId,
            stopId = stopId, districtId = district.id, address = house.address, pos = door, outcome = outcome, witnessed = seen, entered = opened,
            itemCount = loot.items.Count, lootCents = lootCents, fineCents = result.fineCents, witnessKinds = seen ? kinds : (byte)0,
            day = today, hours = GameClock.TotalHours,
        });
        return result;
    }

    /// <summary>
    /// Roll <paramref name="count"/> stolen valuables for a house: catalog valuables the district's bins could hold
    /// (item tier at most the district tier), rarity from the district's own loot table read a little richer than the
    /// house's wealth, value and weight inside the catalog's ranges (so ebag's validator would accept them if they were
    /// not hot). <paramref name="next01"/> is the host dice; every item comes back flagged hot.
    /// </summary>
    internal static List<ItemInstance> RollLoot(float wealth, DistrictDef district, int count, Func<float> next01, string foundAt, int day, string uidPrefix)
    {
        var items = new List<ItemInstance>();
        district ??= Districts.Starter;
        if (count <= 0 || next01 == null) return items;
        var pools = new List<ItemDef>[5];
        for (int i = 0; i < pools.Length; i++) pools[i] = new List<ItemDef>();
        foreach (var def in ItemCatalog.All) if (ScavengerRules.Stealable(def, district.tier)) pools[(int)def.rarity].Add(def);
        var weights = ItemCatalog.RarityWeights(ScavengerRules.LootWealth(wealth), district.loot);
        for (int i = 0; i < count; i++)
        {
            int rarity = ScavengerRules.PickWeighted(weights, next01()) + 1; // weights are Uncommon..Legendary
            while (rarity > 0 && pools[rarity].Count == 0) rarity--;
            if (pools[rarity].Count == 0) break;
            var pool = pools[rarity];
            var def = pool[Mathf.Clamp((int)(next01() * pool.Count), 0, pool.Count - 1)];
            float condition = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(next01())); // kept indoors, not binned
            items.Add(new ItemInstance
            {
                uid = uidPrefix + "-" + i, defId = def.id, condition = condition,
                massKg = Mathf.Lerp(def.minMass, def.maxMass, Mathf.Clamp01(next01())),
                value = Mathf.Max(1f, Mathf.Round(Mathf.Lerp(def.minValue, def.maxValue, Mathf.Clamp01(next01())) * condition)),
                foundAt = foundAt ?? "", dayFound = day, note = "", hot = true,
            });
        }
        return items;
    }

    /// <summary>Somebody saw: fine and Rep to the burglar's own business, a grudge on the house, and the letter text.</summary>
    static long ApplyConsequences(ulong client, string ledgerTarget, CurbTrashSpawner.House house, DistrictDef district, Vector3 door, bool entered,
        out string subject, out string body)
    {
        var books = RulesBooks.Active;
        byte index = WorldLifeIds.DistrictIndex(district);
        int nth = RulesBooks.DailyAdd(books, ScavengerIds.CaughtToday(district.id), RulesBooks.DayOf(books.Hours), 1);
        float escalation = WorldLifeTuning.Fines.Escalation(nth);
        float fine = books.FinesOn ? ScavengerRules.Fine(WorldLifeTuning.Fines.ShuntFine(index), escalation) : 0f;
        using (Ledger.ToBusiness(ledgerTarget))
        {
            if (fine > 0f) books.Charge(fine, "Caught at " + house.address, ScavengerIds.LedgerBurglaryFine, door + Vector3.up * 1.2f, district.id);
            books.Incident(new NeighborIncidentFact { kind = NeighborIncidentKind.Burglary, stopId = house.stopId, districtId = district.id, pos = door, escalation = escalation });
        }
        NeighborGrudge.Add(house.stopId, books);
        string who = ResidentName(house.stopId);
        BurglaryLetters.Caught(index, who, house.address, GameClock.FormatHours(GameClock.Hours), entered, fine, !books.FinesOn, nth, escalation,
            LifeHash.Str(house.stopId) + (uint)nth, out subject, out body);
        return GameSession.ToCents(fine);
    }

    static string ResidentName(string stopId)
    {
        var sim = LifeSim.Active;
        int hh = sim != null ? sim.Roster.HouseholdIndex(stopId) : -1;
        if (hh < 0) return "";
        var hold = sim.Roster.households[hh];
        for (int k = 0; k < hold.residentCount; k++)
        {
            int r = hold.firstResident + k;
            if (sim.Roster.residents[r].archetype == ResidentArchetype.Butler) continue;
            string name = NeighborConsequences.WhoOf(LifeId.Resident(r), stopId);
            if (!string.IsNullOrEmpty(name) && name != "a neighbour") return name;
        }
        return "";
    }

    // ================================================================================================ THE FENCE

    public static FenceResult FenceSell(ulong client, ItemInstance item)
    {
        var result = new FenceResult { uid = item != null ? item.uid ?? "" : "", why = "" };
        if (!Authority) { result.why = "Only the host decides that."; return result; }
        if (Level(client, CallingIds.TheFence) <= 0) { result.why = "He doesn't know you."; return result; }
        if (item == null || !item.hot) { result.why = "He only takes hot goods."; return result; }
        if (!EbaeItemValidation.IsPlausible(item, out _)) { result.why = "He squints at it. That isn't real."; return result; }
        if (!ScavengerRules.FenceOpen(GameClock.Hours)) { result.why = "He only works nights."; return result; }
        if (!PlayerAt(client, out var pos, out var player) || Busy(player) ||
            Flat(pos - FenceSpot.Position).sqrMagnitude > Sq(ScavengerTuning.FenceRange + ScavengerTuning.FenceHostSlack))
        { result.why = "Come closer. Not so loud."; return result; }
        if (!OwnBusiness(client, out string target, out string businessId)) { result.why = "Your business isn't connected yet."; return result; }
        if (!soldUids.Add(item.uid)) { result.why = "He already has that one."; return result; }

        float appraisal = EbaeMarket.Appraise(item), standing = FenceTerms.Standing(client);
        long cents = ScavengerRules.FencePriceCents(appraisal, standing);
        if (cents <= 0) { soldUids.Remove(item.uid); result.why = "Worthless. Even to him."; return result; }
        string name = item.Def != null ? item.Def.name : item.defId;
        using (Ledger.ToBusiness(target)) Ledger.Credit(cents / 100f, "Fenced: " + name, ScavengerIds.LedgerFence, FenceSpot.Position + Vector3.up * 1.4f);
        if (!IsLocal(client) && hotGiven.TryGetValue(client, out int held)) hotGiven[client] = Mathf.Max(0, held - 1);
        ScavengerEvents.RaiseFenced(new FenceSaleFact
        {
            clientId = client, businessId = businessId, itemUid = item.uid, defId = item.defId,
            appraisalCents = GameSession.ToCents(appraisal), paidCents = cents, standing = standing, day = GameClock.Day,
        });
        result.ok = true; result.cents = cents;
        return result;
    }

    // ================================================================================================ the Town's memory

    /// <summary>Host, now and then: one point of suspicion fades from every district per midnight that passed.</summary>
    public static void HostDayTick()
    {
        if (!Authority || GameSession.IsGuest) return;
        int today = GameClock.Day, last = GameSession.GetCounter(ScavengerIds.SuspicionFadedDay);
        if (last == today) return;
        if (last > 0 && today > last)
            foreach (var d in Districts.All)
            {
                string key = ScavengerIds.Suspicion(d.id); int now = GameSession.GetCounter(key);
                if (now > 0) GameSession.SetCounter(key, ScavengerRules.SuspicionAfterNights(now, today - last));
            }
        GameSession.SetCounter(ScavengerIds.SuspicionFadedDay, today);
    }

    internal static void ForgetClient(ulong client) { casings.Remove(client); hotGiven.Remove(client); }

    /// <summary>This machine's game ended: per-session memory (casings, who used a call, what the fence bought) goes with it.</summary>
    internal static void ResetSession() => ResetStatics();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        casings.Clear(); callDayByPlayer.Clear(); calledBags.Clear(); soldUids.Clear(); hotGiven.Clear(); lootSerial = 0;
    }
}

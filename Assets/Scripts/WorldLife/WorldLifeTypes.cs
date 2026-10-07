using System;
using System.Collections.Generic;
using UnityEngine;

// =====================================================================================================================
// WorldLife W0 CONTRACT (Docs/WorldLife.md §2, §8, §10, §12). Packages A–G compile against these types; add members
// freely, never rename or change a meaning without telling every package. Global namespace, Assembly-CSharp.
//
//   DATA (every machine computes the same)  ->  DECIDE (host: IWorldLifeAuthority)  ->  PRESENT (each machine, local)
//
// Ids, the stateless hash, hit / claim / fact structs, the host override store, the per-truck sweep and the seams
// between packages live here. The spatial hash is LifeBodies.cs, the numbers are WorldLifeTuning.cs, the street graph
// schema is Graph/StreetGraph.cs, boot + activation + profiler markers are WorldLifeRunner.cs.
// =====================================================================================================================

// ------------------------------------------------------------------------------------------------------------- ids

/// <summary>What a <see cref="LifeId"/> (and a <see cref="LifeHash"/> stream) refers to.</summary>
public enum LifeKind : byte
{
    None = 0,
    /// <summary>A household member (pedestrians, dog walkers, joggers and commuters are all residents). Ordered by stopId
    /// then member index (§7.1).</summary>
    Resident = 1,
    /// <summary>A household car (§3.2).</summary>
    Car = 2,
    /// <summary>A dog (§8.2: never hit; leaps clear).</summary>
    Dog = 3,
    /// <summary>A knockable prop (<see cref="Knockable.id"/>, build order).</summary>
    Prop = 4,
    /// <summary>Town-level streams (exits, road works...).</summary>
    Town = 5,
    /// <summary>A kid in a fenced yard or the playground (§3.1, §12.K): a hash stream for looks and the daily schedule
    /// only. Kids are presentation: never an <see cref="ILifeBody"/>, never a <see cref="LifeId"/> in LifeBodies.</summary>
    Kid = 6,
}

/// <summary>
/// A stable id for anything WorldLife simulates: (kind, index). Identical on every machine because rosters are built in
/// a fixed order from the street graph and the Town seed. 3 bytes on the wire (F).
/// </summary>
[Serializable]
public struct LifeId : IEquatable<LifeId>
{
    public LifeKind kind;
    public ushort index;

    public LifeId(LifeKind kind, ushort index) { this.kind = kind; this.index = index; }
    public LifeId(LifeKind kind, int index) { this.kind = kind; this.index = (ushort)Mathf.Clamp(index, 0, ushort.MaxValue); }

    public static readonly LifeId None = default;
    public bool IsValid => kind != LifeKind.None;
    /// <summary>(kind &lt;&lt; 16) | index: a dictionary key and the hash input.</summary>
    public uint Packed => ((uint)kind << 16) | index;
    public static LifeId FromPacked(uint p) => new LifeId((LifeKind)((p >> 16) & 0xFF), (ushort)(p & 0xFFFF));

    public static LifeId Resident(int i) => new LifeId(LifeKind.Resident, i);
    public static LifeId Car(int i) => new LifeId(LifeKind.Car, i);
    public static LifeId Dog(int i) => new LifeId(LifeKind.Dog, i);
    public static LifeId Prop(int i) => new LifeId(LifeKind.Prop, i);

    public bool Equals(LifeId o) => kind == o.kind && index == o.index;
    public override bool Equals(object obj) => obj is LifeId o && Equals(o);
    public override int GetHashCode() => (int)Packed;
    public static bool operator ==(LifeId a, LifeId b) => a.Equals(b);
    public static bool operator !=(LifeId a, LifeId b) => !a.Equals(b);
    public override string ToString() => kind == LifeKind.None ? "none" : kind + "#" + index;
}

/// <summary>
/// Stateless 64-bit hashing for WorldLife DATA (§2.1): SplitMix64 over (Town seed, kind, id, day, salt). Every machine
/// computes the same value with no shared state. Never touches UnityEngine.Random / System.Random / ContentRng: nothing
/// here rolls content; it only decides who walks where and what a neighbour looks like. Integer outputs for data;
/// <see cref="Float01"/> is for presentation only.
/// </summary>
public static class LifeHash
{
    /// <summary>The SplitMix64 finaliser.</summary>
    public static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>The hash for (townSeed, kind, id, day, salt). day = GameClock.DayIndex (or -1 for "every day").</summary>
    public static ulong U64(ulong townSeed, LifeKind kind, int id, int day, uint salt = 0)
    {
        unchecked
        {
            ulong h = Mix(townSeed ^ ((ulong)kind << 56));
            h = Mix(h ^ (uint)id);
            h = Mix(h ^ ((ulong)(uint)day << 1));
            return Mix(h ^ ((ulong)salt << 7));
        }
    }

    /// <summary>As <see cref="U64(ulong, LifeKind, int, int, uint)"/> for a <see cref="LifeId"/>.</summary>
    public static ulong U64(ulong townSeed, LifeId id, int day, uint salt = 0) => U64(townSeed, id.kind, id.index, day, salt);

    /// <summary>Stable string hash (FNV-1a 32) for stopIds / exit ids as hash inputs. Same on every machine and build.</summary>
    public static uint Str(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            if (s != null) for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h;
        }
    }

    /// <summary>Next value of a SplitMix64 sequence (state advances). For a short deterministic run of draws.</summary>
    public static ulong Next(ref ulong state)
    {
        unchecked { state += 0x9E3779B97F4A7C15UL; }
        return Mix(state);
    }

    /// <summary>Integer in [min, maxExclusive) from a hash (data-safe).</summary>
    public static int Range(ulong h, int min, int maxExclusive)
    {
        if (maxExclusive <= min) return min;
        return min + (int)((h >> 11) % (ulong)(maxExclusive - min));
    }

    /// <summary>True with probability num/den (integer odds; data-safe).</summary>
    public static bool Chance(ulong h, int num, int den) => den > 0 && (int)((h >> 13) % (ulong)den) < num;

    /// <summary>Index into integer weights (data-safe). Returns -1 when every weight is ≤ 0.</summary>
    public static int Pick(ulong h, IReadOnlyList<int> weights)
    {
        long total = 0;
        for (int i = 0; i < weights.Count; i++) if (weights[i] > 0) total += weights[i];
        if (total <= 0) return -1;
        long r = (long)((h >> 9) % (ulong)total);
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            if (r < weights[i]) return i;
            r -= weights[i];
        }
        return weights.Count - 1;
    }

    /// <summary>[0, 1) from a hash. Exact on every machine (24-bit integer / 2^24), but use it for LOOKS and timings that
    /// never pay or hurt; data decisions use the integer helpers.</summary>
    public static float Float01(ulong h) => (h >> 40) * (1f / 16777216f);
}

// ------------------------------------------------------------------------------------------------------- districts

/// <summary>Small id helpers shared by every package (district bytes, GameSession keys, asset keys, Sfx ids).</summary>
public static class WorldLifeIds
{
    /// <summary>District byte used in the street graph, knockables and facts: tier − 1 (0 Mildly Pleasant .. 3 Bluffs).</summary>
    public static byte DistrictIndex(DistrictDef d) => (byte)Mathf.Clamp((d ?? Districts.Starter).tier - 1, 0, 3);
    public static byte DistrictIndex(string districtId) => DistrictIndex(Districts.Get(districtId));
    public static byte DistrictIndexForStop(string stopId) => DistrictIndex(Districts.ForStopId(stopId));
    /// <summary>The district for a district byte (Starter when out of range).</summary>
    public static DistrictDef District(byte index)
    {
        var all = Districts.All;
        return index < all.Count ? all[index] : Districts.Starter;
    }
    public static string DistrictId(byte index) => District(index).id;

    /// <summary>Bit per district byte: is it unlocked for this company right now (gate edges, car trips)?</summary>
    public static int UnlockedDistrictMask()
    {
        int mask = 0;
        var all = Districts.All;
        for (int i = 0; i < all.Count && i < 31; i++) if (Districts.IsUnlocked(all[i])) mask |= 1 << i;
        return mask;
    }

    // ---- GameSession keys (rule 9: company-scoped counters / stamps until M1's stores) ----

    /// <summary>GameSession stamp: the grudge on a house lasts until this GameClock.TotalHours (§9.1).</summary>
    public static string GrudgeStamp(string stopId) => "wl_grudge:" + stopId;
    /// <summary>GameSession counter (packed day + count, see <see cref="DailyCount"/>): flattens + shunts today in a
    /// district (escalation, §9.1). Packed instead of the doc's ":&lt;day&gt;" suffix so the save never grows per day.</summary>
    public static string IncidentCounter(string districtId) => "wl_incidents:" + districtId;
    /// <summary>GameSession counter (packed): City Works dollars × 100 billed today in a district (the cap, §9.1).</summary>
    public static string CityWorksCounter(string districtId) => "wl_cityworks:" + districtId;
    /// <summary>GameSession counter: non-zero = neighbour fines are shame-only (the Town toggle before M1 owns TownRules).</summary>
    public const string FinesOffCounter = "wl_fines_off";

    // ---- RubbishAssets keys ----

    /// <summary>Proxy prefab per knockable prefab: "knock/&lt;prefabName&gt;" (Setup_WorldLifeKnock registers them).</summary>
    public const string KnockPrefabPrefix = "knock/";
    /// <summary>Builder-made knockables (signs, picket panels, hoops...) rebuild from a recipe: "recipe/&lt;name&gt;".</summary>
    public const string KnockRecipePrefix = "recipe/";
    /// <summary>WorldLife materials: "mat/worldlife_&lt;name&gt;".</summary>
    public const string MaterialPrefix = "mat/worldlife_";

    /// <summary>New Sfx ids (§11 Feel row). Feel has a synth recipe for every one (package G); an id Feel didn't know would
    /// play its fallback through <c>KnockSfx.Play</c>.</summary>
    public static class Sfx
    {
        public const string Clang = "clang", CarAlarm = "car_alarm", CarHorn = "car_horn", Geyser = "geyser", Sparks = "sparks",
            DoorCreak = "door_creak", Oof = "oof", Yelp = "yelp", Bark = "bark", Mumble = "mumble", TireScreech = "tire_screech";
        /// <summary>Three takes of the mumble, so a street of chatter isn't one phrase on repeat (pick by the cosmetic dice).</summary>
        public static readonly string[] Mumbles = { "mumble", "mumble2", "mumble3" };
    }
}

/// <summary>
/// A per-day counter packed into one GameSession int: (day &lt;&lt; 10) | count (count ≤ 1023). Reading a key from an
/// older day returns 0, so the save holds one int per key forever instead of one per day.
/// </summary>
public static class DailyCount
{
    const int Bits = 10, Mask = (1 << Bits) - 1;

    public static int Get(string key, int day)
    {
        int v = GameSession.GetCounter(key);
        return v != 0 && (v >> Bits) == day ? v & Mask : 0;
    }

    /// <summary>Adds <paramref name="delta"/> for <paramref name="day"/> (host only; rule 15/16). Returns the new count.</summary>
    public static int Add(string key, int day, int delta = 1)
    {
        int n = Mathf.Clamp(Get(key, day) + delta, 0, Mask);
        GameSession.SetCounter(key, (day << Bits) | n);
        return n;
    }
}

// ----------------------------------------------------------------------------------------------------------- bodies

/// <summary>What a <see cref="ILifeBody"/> is (flags, so queries can take a mask).</summary>
[Flags]
public enum LifeBodyKind : byte
{
    None = 0,
    Person = 1,
    Dog = 2,
    Car = 4,
    All = Person | Dog | Car,
}

/// <summary>
/// Anything the rules can hit: a resident / pedestrian puppet (C), a dog (C), a car (D). Registered in
/// <see cref="LifeBodies"/> while it is SHOWN on this machine. All getters are "as drawn this frame" and must not
/// allocate. Decisions read these through the authority; <see cref="PlayHit"/> is presentation only.
/// </summary>
public interface ILifeBody
{
    LifeId Id { get; }
    LifeBodyKind BodyKind { get; }
    /// <summary>Feet (people, dogs) or the chassis centre at ground height (cars), world space.</summary>
    Vector3 Position { get; }
    /// <summary>Facing (yaw only for the overlap tests).</summary>
    Quaternion Rotation { get; }
    /// <summary>Half extents of the body's box in its own frame: x = half width, y = half height, z = half length. The box
    /// is centred at Position + up × y. People ≈ (0.3, 0.85, 0.3); cars ≈ (0.95, 0.75, 2.2).</summary>
    Vector3 HalfExtents { get; }
    /// <summary>Can be hit right now: shown, on the ground, outdoors, not immune, not mid-flop.</summary>
    bool Hittable { get; }
    /// <summary>The household (grudges, letters); "" when none.</summary>
    string StopId { get; }
    /// <summary>Present a decided (or predicted) hit: flop, nudge, leap, shunt, startle... Called on every machine.</summary>
    void PlayHit(in NpcHit hit);
}

// ------------------------------------------------------------------------------------------------------------- hits

/// <summary>What happened to an NPC or car (the NpcEvent kind on the wire, F).</summary>
public enum NpcHitKind : byte
{
    None = 0,
    /// <summary>Truck 0.8–2.5 m/s: pushed 0.6 m aside, "HEY! WATCH IT!". No fine.</summary>
    Nudge = 1,
    /// <summary>Truck ≥ 2.5 m/s: the cartoon flop (§8.1) + consequences (§9).</summary>
    Flatten = 2,
    /// <summary>A dog inside the truck box: scripted leap clear (§8.2). Owner furious, Rep −1.</summary>
    DogLeap = 3,
    /// <summary>A car hit at ≥ 2.5 m/s: the cartoon shunt (§8.3) + consequences.</summary>
    Shunt = 4,
    /// <summary>The horn (§8.5): hop, "AAH!", step off the line.</summary>
    Startle = 5,
    /// <summary>A sprinting player or a 6–11 m/s throw: stumble back, "HEY!".</summary>
    Stumble = 6,
    /// <summary>A ≥ 11 m/s throw: a short flop (1.0 s lie), Rep −1.</summary>
    ShortFlop = 7,
    /// <summary>A gentle &lt; 6 m/s throw.</summary>
    Boop = 8,
    /// <summary>A rejected claim (F): the "dodged!" cartoon leap — the rejection is the joke.</summary>
    Dodge = 9,
    /// <summary>A car ahead yields to the horn: pulls to the kerb for 3 s.</summary>
    PullOver = 10,
}

/// <summary>
/// A decided hit (the NpcEvent of §10.2). The host's values are canonical; the machine that simulates the truck plays
/// the same formula at once (<see cref="predicted"/>) and blends if the host's launch differs by more than 15%.
/// </summary>
public struct NpcHit
{
    public LifeId target;
    public NpcHitKind kind;
    /// <summary>GameClock.TotalHours of the hit (letters, delays).</summary>
    public double atHours;
    /// <summary>Fixed-step tick of the hit (<see cref="WorldLifeRunner.FixedTickCount"/>; F maps it to the network tick).</summary>
    public int tick;
    /// <summary>Where the body was when it was hit (world).</summary>
    public Vector3 point;
    /// <summary>Flatten / ShortFlop: launch velocity (§8.1). Nudge / Stumble: push (metres, world). Shunt: slide (metres).</summary>
    public Vector3 launch;
    public Vector3 truckVelocity;
    /// <summary>Spin axis, line choice, yaw-kick sign... (byte so the event stays small).</summary>
    public byte seed;
    /// <summary>The player who caused it (slot; 0 = host / solo).</summary>
    public byte actorSlot;
    /// <summary>Timetable delay the host fixed for this interruption, centiseconds (10 s = 1000).</summary>
    public ushort delayCs;
    /// <summary>LOCAL ONLY: the hitter's truck on this machine (chase target; never PlayerController.Local).</summary>
    [NonSerialized] public TruckControllerV2 truck;
    /// <summary>True while only the simulating machine has played it (before the host's event).</summary>
    public bool predicted;
}

/// <summary>A machine that simulates a truck saw it overlap an NPC or car (§10.2 NpcHitClaim, 16 B on the wire).</summary>
public struct NpcHitClaim
{
    public LifeId target;
    public int tick;
    public Vector3 truckVelocity;
    public byte actorSlot;
    [NonSerialized] public TruckControllerV2 truck;
}

// ------------------------------------------------------------------------------------------------------- knockables

/// <summary>Knockable street furniture kinds (§6.1). The byte is the index into <see cref="WorldLifeTuning.Knock"/>.</summary>
public enum KnockKind : byte
{
    Lamp = 0,
    Mailbox = 1,
    WheelieBin = 2,
    /// <summary>Street, yard and park signs.</summary>
    Sign = 3,
    /// <summary>A 4 m picket panel (Meadows fronts; one box collider per panel).</summary>
    PicketPanel = 4,
    LemonadeStand = 5,
    BasketballHoop = 6,
    DryingRack = 7,
    ParkBench = 8,
    ParkBin = 9,
    Hydrant = 10,
    Cone = 11,
    Sawhorse = 12,
    Flamingo = 13,
}

/// <summary>A prop went over (§10.2 KnockEvent). Raised on <see cref="WorldLifeEvents.PropKnocked"/>.</summary>
public struct KnockFact
{
    public ushort knockId;
    public KnockKind kind;
    public Vector3 pos;
    /// <summary>The proxy's launch velocity (§6.3 step 4).</summary>
    public Vector3 impulse;
    public byte seed;
    public byte actorSlot;
    /// <summary>District byte (<see cref="WorldLifeIds.DistrictIndex(DistrictDef)"/>): where City Works bills it.</summary>
    public byte district;
    public double atHours;
}

/// <summary>A machine that simulates a truck knocked a prop (§10.2 KnockClaim). It already shows the prop down.</summary>
public struct KnockClaim
{
    public ushort knockId;
    public int tick;
    public Vector3 impulse;
    public Vector3 truckVelocity;
    public byte seed;
    public byte actorSlot;
    [NonSerialized] public TruckControllerV2 truck;
}

// ------------------------------------------------------------------------------------------------------- residents

/// <summary>A resident handed their bag to a player ("Take bag", §7.2). +1 Rep (neighborly, capped) when the host agrees.</summary>
public struct HandOverFact
{
    public LifeId resident;
    public string bagId;
    public string stopId;
    public Vector3 pos;
    public byte actorSlot;
}

// -------------------------------------------------------------------------------------------- host overrides (§9.2)

/// <summary>
/// A host-written interruption of a timetable (§10.1: 12 B each, a few alive). D's schedule draws an agent at
/// s(t − Σ delay for today); E writes them; F syncs them (event + late-join snapshot).
/// </summary>
[Serializable]
public struct NpcOverride
{
    public LifeId id;
    /// <summary>GameClock.DayIndex this delay belongs to.</summary>
    public int day;
    /// <summary>Timetable delay, centiseconds.</summary>
    public ushort delayCs;
    public NpcHitKind kind;
    /// <summary>The interruption lasts until this fixed tick (immunity, lie + chase); 0 = no hold.</summary>
    public int untilTick;
    public byte actorSlot;
}

/// <summary>
/// The host's override store (E writes on the host; F mirrors it on guests; D reads <see cref="DelaySeconds"/>).
/// O(1) per lookup, no allocations after warm-up. Cleared for older days by <see cref="Prune"/>.
/// </summary>
public static class LifeOverrides
{
    static readonly List<NpcOverride> list = new List<NpcOverride>();
    static readonly Dictionary<ulong, int> delayCs = new Dictionary<ulong, int>();   // (day, id) -> Σ delayCs
    static readonly Dictionary<uint, int> holdUntil = new Dictionary<uint, int>();   // id -> latest untilTick

    /// <summary>Bumped on every change (F: snapshot dirtiness; D: cache invalidation).</summary>
    public static int Version { get; private set; }
    public static event Action<NpcOverride> Added;
    public static IReadOnlyList<NpcOverride> All => list;

    static ulong Key(LifeId id, int day) => ((ulong)(uint)day << 32) | id.Packed;

    /// <summary>Host (E) or the F mirror only.</summary>
    public static void Add(in NpcOverride o)
    {
        if (!o.id.IsValid) return;
        list.Add(o);
        ulong k = Key(o.id, o.day);
        delayCs[k] = (delayCs.TryGetValue(k, out int d) ? d : 0) + o.delayCs;
        if (o.untilTick != 0 && (!holdUntil.TryGetValue(o.id.Packed, out int u) || o.untilTick > u)) holdUntil[o.id.Packed] = o.untilTick;
        Version++;
        var h = Added;
        if (h != null) { try { h(o); } catch (Exception e) { Debug.LogException(e); } }
    }

    /// <summary>Σ host delays for this agent today, in seconds (0 when none).</summary>
    public static float DelaySeconds(LifeId id, int day) => delayCs.TryGetValue(Key(id, day), out int d) ? d * 0.01f : 0f;

    /// <summary>The latest untilTick written for this agent (0 when none).</summary>
    public static int HoldUntilTick(LifeId id) => holdUntil.TryGetValue(id.Packed, out int u) ? u : 0;

    /// <summary>Drop overrides from days before <paramref name="today"/> (call at the day roll).</summary>
    public static void Prune(int today)
    {
        int before = list.Count;
        list.RemoveAll(o => o.day < today);
        if (list.Count == before) return;
        delayCs.Clear(); holdUntil.Clear();
        foreach (var o in list)
        {
            ulong k = Key(o.id, o.day);
            delayCs[k] = (delayCs.TryGetValue(k, out int d) ? d : 0) + o.delayCs;
            if (o.untilTick != 0 && (!holdUntil.TryGetValue(o.id.Packed, out int u) || o.untilTick > u)) holdUntil[o.id.Packed] = o.untilTick;
        }
        Version++;
    }

    /// <summary>Forget everything (new Town / session).</summary>
    public static void Clear()
    {
        list.Clear(); delayCs.Clear(); holdUntil.Clear();
        Version++;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { list.Clear(); delayCs.Clear(); holdUntil.Clear(); Version = 0; Added = null; }
}

// --------------------------------------------------------------------------------------------- the per-truck sweep

/// <summary>
/// One simulated truck's predictive box for one FixedUpdate (§6.3 step 2): the body box (TruckControllerV2.BodyBoxMetres)
/// stretched along the direction of travel by |v| × 2 × fixedDeltaTime + 0.3 m. Built by <see cref="WorldLifeRunner"/>
/// only for trucks whose Rigidbody THIS machine simulates (non-kinematic), and handed to every
/// <see cref="ITruckSensorClient"/> before the physics step, so a knocked prop's collider can be switched off before
/// PhysX ever resolves the contact.
/// </summary>
public struct TruckSweep
{
    [NonSerialized] public TruckControllerV2 truck;
    [NonSerialized] public Rigidbody body;
    /// <summary>World centre / rotation / half extents of the stretched box.</summary>
    public Vector3 center;
    public Quaternion rotation;
    public Vector3 halfExtents;
    /// <summary>World velocity, its magnitude, and the signed speed along the truck's forward.</summary>
    public Vector3 velocity;
    public float speed;
    public float forwardSpeed;
    /// <summary>The stretch added along the travel direction this step (m).</summary>
    public float lookahead;
    public int tick;
    public float fixedDeltaTime;

    /// <summary>Is a world point inside the box (padded by <paramref name="pad"/> on every side)?</summary>
    public bool Contains(Vector3 p, float pad = 0f)
    {
        Vector3 l = Quaternion.Inverse(rotation) * (p - center);
        return Mathf.Abs(l.x) <= halfExtents.x + pad && Mathf.Abs(l.y) <= halfExtents.y + pad && Mathf.Abs(l.z) <= halfExtents.z + pad;
    }

    /// <summary>The truck's right (+1) or left (−1) side relative to a world point (for "side" in the flop launch).</summary>
    public Vector3 SideToward(Vector3 p)
    {
        Vector3 right = rotation * Vector3.right; right.y = 0f;
        right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
        return Vector3.Dot(p - center, right) >= 0f ? right : -right;
    }
}

/// <summary>Gets every simulated truck's sweep once per FixedUpdate (B: knockables; E: people, dogs, cars).
/// Register with <see cref="WorldLifeRunner.AddSensorClient"/>. Must not allocate.</summary>
public interface ITruckSensorClient
{
    void Sense(in TruckSweep sweep);
}

// --------------------------------------------------------------------------------------------- decide (the host)

/// <summary>
/// The host's decision service (§10.3: "claims are direct calls into WorldLifeAuthority"). E implements it and sets
/// <see cref="WorldLifeRunner.Authority"/>; F's WorldLifeNet forwards guests' claims into the same calls. Until E lands
/// a no-op authority accepts everything with no consequences, so B and C sandboxes run. Implementations decide only
/// when <c>!GameSession.IsGuest</c> (rule 15) and never mutate GameSession on a guest (rule 16).
/// </summary>
public interface IWorldLifeAuthority
{
    /// <summary>Validate + decide a truck-vs-NPC/car overlap. Returns the decided hit (kind None = rejected / immune). The
    /// authority presents it (target.PlayHit) and raises <see cref="WorldLifeEvents.HitDecided"/>.</summary>
    NpcHit ClaimNpcHit(in NpcHitClaim claim);
    /// <summary>A prop the claimant already shows down: bookkeeping (City Works tally, down bit) on the host.</summary>
    void ClaimKnock(in KnockClaim claim, in KnockFact fact);
    /// <summary>A resident handed a bag over: +1 Rep (neighborly, capped) on the host.</summary>
    void ClaimHandOver(in HandOverFact fact);
}

// ------------------------------------------------------------------------------------------------------------ facts

/// <summary>
/// WorldLife's own facts (presentation, stats, letters and Feel listen). Consequences that touch money or Rep go through
/// Ledger / ProgressionEvents.RaiseNeighborIncident, never through these. Each listener is isolated.
/// </summary>
public static class WorldLifeEvents
{
    /// <summary>The authority decided a hit (host), or the host's event arrived (guest, F).</summary>
    public static event Action<NpcHit> HitDecided;
    /// <summary>A prop went down on this machine (host-confirmed or predicted).</summary>
    public static event Action<KnockFact> PropKnocked;
    /// <summary>A prop came back up: (knockId, byCityWorks = the 06:00 reset).</summary>
    public static event Action<ushort, bool> PropReset;
    /// <summary>A resident handed a bag to a player.</summary>
    public static event Action<HandOverFact> BagHandedOver;

    public static void RaiseHitDecided(in NpcHit h) => Invoke(HitDecided, h);
    public static void RaisePropKnocked(in KnockFact f) => Invoke(PropKnocked, f);
    public static void RaiseBagHandedOver(in HandOverFact f) => Invoke(BagHandedOver, f);
    public static void RaisePropReset(ushort knockId, bool byCityWorks)
    {
        var h = PropReset;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<ushort, bool>)d)(knockId, byCityWorks); } catch (Exception e) { Debug.LogException(e); }
    }

    static void Invoke<T>(Action<T> h, T a)
    {
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<T>)d)(a); } catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { HitDecided = null; PropKnocked = null; PropReset = null; BagHandedOver = null; }
}

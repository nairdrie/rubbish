using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §7.1): who lives where. Households and their members are a pure function of the
/// street graph and the Town seed (<see cref="LifeHash"/>), so every machine builds the same cast with no sync:
/// <list type="bullet">
/// <item>Household sizes: small 1–2, medium 2–3, big 2–4; Meadows 2–4; Snootington 2 plus a housekeeper; Bluffs the owner,
/// the butler and sometimes a bodyguard.</item>
/// <item>Resident ids (<see cref="LifeKind.Resident"/>) are ordered by stopId (ordinal) then member index; dog ids
/// (<see cref="LifeKind.Dog"/>) in the same order over the dog owners.</item>
/// <item>Archetypes come from the district cast's integer weights; one HOA Lady in the Meadows and one Mr. Pemberton in
/// Snootington. Looks come from <see cref="ResidentLook.StyleFor"/>.</item>
/// </list>
/// Package D's roster and schedules should read this cast (same ids) rather than build another one.
/// </summary>
public static class ResidentCast
{
    public struct Member
    {
        public ushort house;             // index into StreetGraph.houses
        public byte member;              // 0..count−1 within the household
        public byte district;
        public ResidentArchetype archetype;
        public ulong lookHash;
        public bool hasDog;
        public DogBreed dogBreed;
        public ushort dogIndex;          // LifeKind.Dog index (valid when hasDog)
    }

    public struct Household
    {
        public ushort first;             // resident index of member 0
        public byte count;
        public byte district;
    }

    static StreetGraph builtFor;
    static ulong builtSeed;
    static int builtVersion = -1;
    static Member[] members = Array.Empty<Member>();
    static Household[] households = Array.Empty<Household>();   // by graph house index
    static ResidentStyle[] styles = Array.Empty<ResidentStyle>();
    static bool[] styleMade = Array.Empty<bool>();
    static ushort[] dogOwner = Array.Empty<ushort>();
    static DogStyle[] dogStyles = Array.Empty<DogStyle>();

    public static int Count => members.Length;
    public static int DogCount => dogOwner.Length;
    public static ulong Seed => builtSeed;

    /// <summary>(Re)build for this graph and Town seed (cheap: ~200 residents, done once per Wake).</summary>
    public static void Ensure(StreetGraph g, ulong townSeed)
    {
        if (g == null) return;
        if (ReferenceEquals(builtFor, g) && builtSeed == townSeed && builtVersion == g.NodeCount) return;
        builtFor = g; builtSeed = townSeed; builtVersion = g.NodeCount;
        int n = g.houses?.Length ?? 0;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => string.CompareOrdinal(g.houses[a].stopId, g.houses[b].stopId));

        // Unique characters: one HOA Lady (Meadows), one Mr. Pemberton (Snootington), in a hashed household.
        int hoaHouse = PickHouse(g, order, 1, townSeed, 0x40A);
        int pembertonHouse = PickHouse(g, order, 2, townSeed, 0x9E3);

        var list = new List<Member>(256);
        households = new Household[n];
        var dogs = new List<ushort>(32);
        foreach (int hi in order)
        {
            var h = g.houses[hi];
            byte dist = (byte)Mathf.Min(Districts.All.Count - 1, h.district);
            int style = Districts.StyleIndex(dist);
            ulong hh = LifeHash.U64(townSeed, LifeKind.Town, (int)LifeHash.Str(h.stopId), -1, 0xC457);
            int count;
            switch (dist)
            {
                case 1: count = LifeHash.Range(hh, 2, 5); break;
                case 2: count = 3; break;
                case 3: count = LifeHash.Chance(hh >> 3, 1, 2) ? 3 : 2; break;
                case 4: count = LifeHash.Range(hh, 1, 3); break; // small shop staff
                case 5: count = LifeHash.Range(hh, 2, 5); break; // condominium households
                default: count = h.size == 0 ? LifeHash.Range(hh, 1, 3) : h.size == 1 ? LifeHash.Range(hh, 2, 4) : LifeHash.Range(hh, 2, 5); break;
            }
            households[hi] = new Household { first = (ushort)list.Count, count = (byte)count, district = dist };
            for (int m = 0; m < count; m++)
            {
                ulong mh = LifeHash.U64(townSeed, LifeKind.Resident, (int)LifeHash.Str(h.stopId) ^ (m * 7919), -1, 0xA7C);
                ResidentArchetype a;
                if (dist == 3) a = m == 0 ? ResidentArchetype.Billionaire : m == 1 ? ResidentArchetype.Butler : ResidentArchetype.Bodyguard;
                else if (dist == 2 && m == count - 1) a = ResidentArchetype.Housekeeper;
                else if (m == 0 && hi == hoaHouse) a = ResidentArchetype.HoaLady;
                else if (m == 0 && hi == pembertonHouse) a = ResidentArchetype.Pemberton;
                else
                {
                    var cast = ResidentLook.DistrictCast[style];
                    int k = LifeHash.Pick(mh, ResidentLook.DistrictWeights[style]);
                    a = cast[Mathf.Clamp(k, 0, cast.Length - 1)];
                }
                var mem = new Member
                {
                    house = (ushort)hi, member = (byte)m, district = dist, archetype = a,
                    lookHash = LifeHash.U64(townSeed, LifeKind.Resident, list.Count, -1, 0x100C),
                };
                if (ResidentLook.WalksDog(a))
                {
                    mem.hasDog = true;
                    mem.dogBreed = a == ResidentArchetype.PearlLady ? DogBreed.Poodle : LifeHash.Chance(mh >> 17, 1, 2) ? DogBreed.Corgi : DogBreed.Sausage;
                    mem.dogIndex = (ushort)dogs.Count;
                    dogs.Add((ushort)list.Count);
                }
                list.Add(mem);
            }
        }
        members = list.ToArray();
        dogOwner = dogs.ToArray();
        styles = new ResidentStyle[members.Length];
        styleMade = new bool[members.Length];
        dogStyles = new DogStyle[dogOwner.Length];
        for (int i = 0; i < dogOwner.Length; i++)
        {
            var owner = members[dogOwner[i]];
            dogStyles[i] = DogRig.StyleFor(owner.dogBreed, LifeHash.U64(townSeed, LifeKind.Dog, i, -1, 0xD06));
        }
    }

    static int PickHouse(StreetGraph g, int[] order, int district, ulong seed, uint salt)
    {
        int n = 0;
        foreach (int hi in order) if (g.houses[hi].district == district) n++;
        if (n == 0) return -1;
        int k = LifeHash.Range(LifeHash.U64(seed, LifeKind.Town, district, -1, salt), 0, n);
        foreach (int hi in order) if (g.houses[hi].district == district && k-- == 0) return hi;
        return -1;
    }

    // ------------------------------------------------------------------ queries

    public static bool IsValid(LifeId id) => id.kind == LifeKind.Resident && id.index < members.Length;
    public static Member Get(LifeId id) => members[id.index];
    public static Member Get(int index) => members[index];
    public static ResidentArchetype ArchetypeOf(LifeId id) => IsValid(id) ? members[id.index].archetype : ResidentArchetype.RobeDad;

    /// <summary>The household of a graph house (count 0 when unknown).</summary>
    public static Household HouseholdOf(int houseIndex) =>
        houseIndex >= 0 && houseIndex < households.Length ? households[houseIndex] : default;

    public static LifeId ResidentId(int index) => LifeId.Resident(index);

    /// <summary>A resident's look (made once, cached).</summary>
    public static ref readonly ResidentStyle Style(LifeId id)
    {
        int i = id.index;
        if (!styleMade[i])
        {
            var m = members[i];
            styles[i] = ResidentLook.StyleFor(m.archetype, m.district, m.lookHash);
            styleMade[i] = true;
        }
        return ref styles[i];
    }

    public static bool IsDog(LifeId id) => id.kind == LifeKind.Dog && id.index < dogOwner.Length;
    public static LifeId DogOf(LifeId resident) =>
        IsValid(resident) && members[resident.index].hasDog ? LifeId.Dog(members[resident.index].dogIndex) : LifeId.None;
    public static LifeId OwnerOf(LifeId dog) => IsDog(dog) ? LifeId.Resident(dogOwner[dog.index]) : LifeId.None;
    public static DogStyle DogStyleOf(LifeId dog) => IsDog(dog) ? dogStyles[dog.index] : default;

    /// <summary>A short census for reports: per-district residents, archetype counts, dogs.</summary>
    public static string Describe()
    {
        var perD = new int[Districts.All.Count];
        var perA = new int[Enum.GetValues(typeof(ResidentArchetype)).Length];
        foreach (var m in members) { perD[m.district]++; perA[(int)m.archetype]++; }
        var sb = new System.Text.StringBuilder();
        sb.Append($"{members.Length} residents (");
        for (int i = 0; i < perD.Length; i++) sb.Append($"{(i == 0 ? "" : ", ")}d{i} {perD[i]}");
        sb.Append($"), {dogOwner.Length} dogs; ");
        for (int i = 0; i < perA.Length; i++) if (perA[i] > 0) sb.Append($"{(ResidentArchetype)i} {perA[i]} ");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Stable hash of the whole cast (determinism checks).</summary>
    public static ulong Fingerprint()
    {
        ulong h = 1469598103934665603UL;
        foreach (var m in members)
        {
            h = LifeHash.Mix(h ^ m.house); h = LifeHash.Mix(h ^ m.member); h = LifeHash.Mix(h ^ (ulong)m.archetype);
            h = LifeHash.Mix(h ^ m.lookHash); h = LifeHash.Mix(h ^ (m.hasDog ? 1UL + (ulong)m.dogBreed : 0UL));
        }
        return h;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        builtFor = null; builtSeed = 0; builtVersion = -1;
        members = Array.Empty<Member>(); households = Array.Empty<Household>();
        styles = Array.Empty<ResidentStyle>(); styleMade = Array.Empty<bool>();
        dogOwner = Array.Empty<ushort>(); dogStyles = Array.Empty<DogStyle>();
    }
}

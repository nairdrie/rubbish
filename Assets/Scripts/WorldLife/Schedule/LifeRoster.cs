using System;
using System.Collections.Generic;
using UnityEngine;

// =====================================================================================================================
// WorldLife package D (Docs/WorldLife.md §7.1): who lives in town, as the schedules see it. The cast itself (households,
// members, archetypes, dogs, looks, names) is package C's ResidentCast, a pure function of (street graph, Town seed);
// this roster reads it with the SAME ids (residents ordered by stopId then member; dogs in owner order) and adds what
// the timetables need: households as index ranges, each house's cars (LifeId.Car, stopId order then slot) and who
// drives them, and everyday walking speeds. Pure and deterministic: every machine builds the same roster.
// =====================================================================================================================

/// <summary>A household = one house (a <see cref="StreetGraph.House"/>).</summary>
[Serializable]
public struct LifeHousehold
{
    public string stopId;
    /// <summary>Index into <see cref="StreetGraph.houses"/>.</summary>
    public ushort house;
    public byte district;
    public ushort firstResident;
    public byte residentCount;
    /// <summary>First car id, or −1 when the house has none.</summary>
    public short firstCar;
    public byte carCount;
}

/// <summary>A neighbour (LifeId.Resident(index), the same index as <see cref="ResidentCast"/>).</summary>
[Serializable]
public struct LifeResident
{
    public ushort index;
    public ushort household;
    /// <summary>Member index inside the household (0 = the head of the house).</summary>
    public byte member;
    public byte district;
    public ResidentArchetype archetype;
    /// <summary>Their everyday walking speed (m/s): 1.3 waddle, 0.9 for Curler Gran and the Monocle Gent.</summary>
    public float walkSpeed;
    /// <summary>The car they drive (LifeId.Car), or −1.</summary>
    public short car;
    /// <summary>Their dog (LifeId.Dog, ResidentCast's dog index), or −1.</summary>
    public short dog;
    public LifeId Id => LifeId.Resident(index);
}

/// <summary>A household car (LifeId.Car(index)); its HouseholdCar in the scene is (stopId, slot).</summary>
[Serializable]
public struct LifeCar
{
    public ushort index;
    public ushort household;
    public ushort house;
    /// <summary>0 = <see cref="StreetGraph.House.carSlot0"/> (deepest), 1 = carSlot1 (nearer the street).</summary>
    public byte slot;
    public byte district;
    /// <summary>The resident who drives it.</summary>
    public ushort driver;
    public LifeId Id => LifeId.Car(index);
}

/// <summary>
/// The town's households, residents and cars for the timetables (§7.1). <see cref="Build"/> is a pure function of the
/// graph and the Town seed (it makes sure C's <see cref="ResidentCast"/> is built for the same pair and reads it).
/// </summary>
public sealed class LifeRoster
{
    public readonly LifeHousehold[] households;
    public readonly LifeResident[] residents;
    public readonly LifeCar[] cars;
    /// <summary>Graph house index → household index.</summary>
    public readonly ushort[] householdOfHouse;
    public readonly int dogCount;

    readonly Dictionary<string, int> byStop;

    LifeRoster(LifeHousehold[] h, LifeResident[] r, LifeCar[] c, ushort[] hh, int dogs)
    {
        households = h; residents = r; cars = c; householdOfHouse = hh; dogCount = dogs;
        byStop = new Dictionary<string, int>(h.Length, StringComparer.Ordinal);
        for (int i = 0; i < h.Length; i++) if (!string.IsNullOrEmpty(h[i].stopId)) byStop[h[i].stopId] = i;
    }

    public int ResidentCount => residents.Length;
    public int CarCount => cars.Length;

    /// <summary>Household index for a stopId, or −1.</summary>
    public int HouseholdIndex(string stopId) => !string.IsNullOrEmpty(stopId) && byStop.TryGetValue(stopId, out int i) ? i : -1;

    /// <summary>The car parked in (stopId, slot), or −1.</summary>
    public int CarAt(string stopId, int slot)
    {
        int h = HouseholdIndex(stopId);
        if (h < 0 || households[h].firstCar < 0) return -1;
        for (int k = 0; k < households[h].carCount; k++)
        {
            int c = households[h].firstCar + k;
            if (cars[c].slot == slot) return c;
        }
        return -1;
    }

    public string StopIdOf(LifeId id)
    {
        switch (id.kind)
        {
            case LifeKind.Resident: return id.index < residents.Length ? households[residents[id.index].household].stopId : "";
            case LifeKind.Car: return id.index < cars.Length ? households[cars[id.index].household].stopId : "";
            default: return "";
        }
    }

    // ------------------------------------------------------------------ build

    /// <summary>The roster for a graph and Town seed (deterministic; allocates; call once per wake).</summary>
    public static LifeRoster Build(StreetGraph g, ulong seed)
    {
        ResidentCast.Ensure(g, seed);
        var houses = g.houses ?? new StreetGraph.House[0];
        // Households in stopId order (ordinal), the order ResidentCast numbers residents in.
        var order = new int[houses.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = string.CompareOrdinal(houses[a].stopId, houses[b].stopId);
            return c != 0 ? c : a.CompareTo(b);
        });

        var hh = new List<LifeHousehold>(houses.Length);
        var rs = new LifeResident[ResidentCast.Count];
        var cs = new List<LifeCar>(houses.Length);
        var householdOfHouse = new ushort[houses.Length];

        for (int k = 0; k < order.Length; k++)
        {
            int hi = order[k];
            var house = houses[hi];
            var ch = ResidentCast.HouseholdOf(hi);
            var hold = new LifeHousehold
            {
                stopId = house.stopId, house = (ushort)hi, district = (byte)Mathf.Clamp(house.district, 0, 3),
                firstResident = ch.first, residentCount = ch.count, firstCar = -1, carCount = 0,
            };
            householdOfHouse[hi] = (ushort)hh.Count;
            for (int m = 0; m < ch.count; m++)
            {
                int ri = ch.first + m;
                var mem = ResidentCast.Get(ri);
                rs[ri] = new LifeResident
                {
                    index = (ushort)ri, household = (ushort)hh.Count, member = mem.member, district = mem.district,
                    archetype = mem.archetype, walkSpeed = WalkSpeed(mem.archetype), car = -1,
                    dog = mem.hasDog ? (short)mem.dogIndex : (short)-1,
                };
            }

            // Cars: House.cars counts the ones that can drive (slot 0 first). Drivers by preference, distinct when possible.
            int carCount = Mathf.Clamp(house.cars, 0, 2);
            if (carCount > 0 && ch.count > 0)
            {
                hold.firstCar = (short)cs.Count;
                hold.carCount = (byte)carCount;
                var used = new bool[ch.count];
                for (int c = 0; c < carCount; c++)
                {
                    int best = 0, bestRank = int.MaxValue;
                    for (int m = 0; m < ch.count; m++)
                    {
                        int rank = DriverRank(rs[ch.first + m].archetype) * 8 + m + (used[m] ? 1000 : 0);
                        if (rank < bestRank) { bestRank = rank; best = m; }
                    }
                    used[best] = true;
                    int ri = ch.first + best;
                    var car = new LifeCar
                    {
                        index = (ushort)cs.Count, household = (ushort)hh.Count, house = (ushort)hi, slot = (byte)c,
                        district = hold.district, driver = (ushort)ri,
                    };
                    if (rs[ri].car < 0) rs[ri].car = (short)car.index;
                    cs.Add(car);
                }
            }
            hh.Add(hold);
        }
        return new LifeRoster(hh.ToArray(), rs, cs.ToArray(), householdOfHouse, ResidentCast.DogCount);
    }

    /// <summary>Everyday walking speed (§7.1).</summary>
    public static float WalkSpeed(ResidentArchetype a) =>
        a == ResidentArchetype.CurlerGran || a == ResidentArchetype.MonocleGent ? WorldLifeTuning.Walk.Slow : WorldLifeTuning.Walk.Waddle;

    /// <summary>Lower = drives first.</summary>
    static int DriverRank(ResidentArchetype a)
    {
        switch (a)
        {
            case ResidentArchetype.Commuter: case ResidentArchetype.SoccerMom: case ResidentArchetype.Bodyguard: return 0;
            case ResidentArchetype.CargoDad: case ResidentArchetype.Billionaire: case ResidentArchetype.MonocleGent: return 1;
            case ResidentArchetype.Jogger: case ResidentArchetype.RobeDad: case ResidentArchetype.TennisTwin: return 2;
            case ResidentArchetype.DogPerson: case ResidentArchetype.Gardener: case ResidentArchetype.PowerWalker:
            case ResidentArchetype.PearlLady: return 3;
            case ResidentArchetype.HoaLady: case ResidentArchetype.Pemberton: case ResidentArchetype.Housekeeper: return 4;
            default: return 6;   // Curler Gran, the Butler
        }
    }

    /// <summary>"households 81 · residents 210 · cars 86" plus per-district counts.</summary>
    public string Describe()
    {
        var hd = new int[4]; var rd = new int[4]; var cd = new int[4]; var dd = new int[4];
        foreach (var h in households) hd[h.district]++;
        foreach (var r in residents) { rd[r.district]++; if (r.dog >= 0) dd[r.district]++; }
        foreach (var c in cars) cd[c.district]++;
        var sb = new System.Text.StringBuilder();
        sb.Append($"households {households.Length} · residents {residents.Length} · cars {cars.Length} · dogs {dogCount}");
        for (int d = 0; d < 4; d++) if (hd[d] > 0) sb.Append($"\n  district {d}: households {hd[d]}, residents {rd[d]}, cars {cd[d]}, dogs {dd[d]}");
        return sb.ToString();
    }
}

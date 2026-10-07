using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A curb bag went into the truck (by hand, the side-loader arm, or Kevin's Crew). Raised by D.</summary>
public struct BagCollectedFact
{
    public string bagId, stopId, districtId;
    public double ageHours;
    public bool onTime, byArm, byKevin;
    public float pay;
    public Vector3 pos;
}

/// <summary>An item landed in a bin (or the compactor / KEEP crate). Raised by C (SortBin, compactor, magnet strip, Magnus).</summary>
public struct ItemSortedFact
{
    public ItemInstance item;
    public ItemCategory bin;
    public bool correct, byMagnus;
    public string districtId;    // Districts.ForAddress(item.foundAt).id
    public Vector3 pos;
}

/// <summary>What a neighbour incident was (Docs/WorldLife.md §9.1).</summary>
public enum NeighborIncidentKind : byte
{
    /// <summary>A resident / pedestrian flattened by a truck (Rep −4).</summary>
    Flattened = 0,
    /// <summary>A car shunted, moving or parked (Rep −3).</summary>
    CarShunt = 1,
    /// <summary>A dog leapt clear of a truck; the owner is furious (Rep −1).</summary>
    DogNearMiss = 2,
    /// <summary>A thrown bag / item flopped a resident (Rep −1).</summary>
    ThrownFlop = 3,
    /// <summary>A night honk near houses (Rep −1; at most 3 a night per district).</summary>
    NoiseComplaint = 4,
    /// <summary>A resident handed their bag over (Rep +1; at most 10 a day).</summary>
    Neighborly = 5,
}

/// <summary>
/// A neighbour incident decided by WorldLife's authority (host). Builder A (Progression) turns it into Rep; the fines
/// themselves go through Ledger.Charge (LedgerIds.NeighborDamages / CityWorks) in WorldLife. Raised by WorldLife E.
/// </summary>
public struct NeighborIncidentFact
{
    public NeighborIncidentKind kind;
    public string stopId, districtId;
    public Vector3 pos;
    /// <summary>The fine multiplier that was applied (×1..×3, §9.1 escalation). Informational: Rep is never escalated.</summary>
    public float escalation;
}

/// <summary>D's midnight inspection results (per district id). Raised once per rollover, after D has paid bonuses,
/// fines and Kevin's wage.</summary>
public sealed class MidnightReport
{
    public int day;
    public int bagsIntoTruckToday;
    public readonly Dictionary<string, float> clean01 = new();
    public readonly Dictionary<string, int> neglectedCurbs = new();
    public readonly Dictionary<string, float> cleanBonusPaid = new();
    public float finesPaid;
}

/// <summary>
/// The only cross-builder events of the progression build (Docs/Progression.md §9.2, §9.3). Builders raise FACTS here;
/// builder A turns them into Rep, Work Orders, Trust and ranks. STEP 0 CONTRACT (builder A).
/// Raise* just invokes; each subscriber is isolated so a listener's bug never breaks the raiser's gameplay.
/// </summary>
public static class ProgressionEvents
{
    public static event Action<BagCollectedFact> BagCollected;
    public static void RaiseBagCollected(BagCollectedFact f) => Invoke(BagCollected, f);                 // D (CurbTrash, KevinCrew)

    public static event Action<string, string, Vector3> CurbCleared;
    public static void RaiseCurbCleared(string stopId, string districtId, Vector3 pos)                   // D
    {
        var h = CurbCleared;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<string, string, Vector3>)d)(stopId, districtId, pos); } catch (Exception e) { Debug.LogException(e); }
    }

    public static event Action<ItemSortedFact> ItemSorted;
    public static void RaiseItemSorted(ItemSortedFact f) => Invoke(ItemSorted, f);                       // C

    public static event Action<ItemInstance, bool> TreasureKept;
    public static void RaiseTreasureKept(ItemInstance item, bool byMagnus) => Invoke(TreasureKept, item, byMagnus); // C

    public static event Action<int, float, int> LoadCrushed;
    public static void RaiseLoadCrushed(int bags, float kg, int treasuresLost)                           // C
    {
        var h = LoadCrushed;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<int, float, int>)d)(bags, kg, treasuresLost); } catch (Exception e) { Debug.LogException(e); }
    }

    public static event Action<MidnightReport> Midnight;
    public static void RaiseMidnight(MidnightReport r) => Invoke(Midnight, r);                           // D, once per rollover

    public static event Action<string> DistrictEntered;
    public static void RaiseDistrictEntered(string districtId) => Invoke(DistrictEntered, districtId);   // D (gate ribbon)

    public static event Action<bool, int, int> ChessGameEnded;
    public static void RaiseChessGameEnded(bool humanWon, int plies, int tier)                           // C
    {
        var h = ChessGameEnded;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<bool, int, int>)d)(humanWon, plies, tier); } catch (Exception e) { Debug.LogException(e); }
    }

    public static event Action<int, int> RankChanged;
    public static void RaiseRankChanged(int oldRank, int newRank) => Invoke(RankChanged, oldRank, newRank); // A

    public static event Action<string> DistrictUnlocked;
    public static void RaiseDistrictUnlocked(string districtId) => Invoke(DistrictUnlocked, districtId); // A (permit bought)

    public static event Action<NeighborIncidentFact> NeighborIncident;
    public static void RaiseNeighborIncident(NeighborIncidentFact f) => Invoke(NeighborIncident, f);   // WorldLife E (host)

    // ------------------------------------------------------------------ internals

    static void Invoke<T>(Action<T> h, T a)
    {
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<T>)d)(a); } catch (Exception e) { Debug.LogException(e); }
    }

    static void Invoke<T1, T2>(Action<T1, T2> h, T1 a, T2 b)
    {
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
            try { ((Action<T1, T2>)d)(a, b); } catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        BagCollected = null; CurbCleared = null; ItemSorted = null; TreasureKept = null; LoadCrushed = null;
        Midnight = null; DistrictEntered = null; ChessGameEnded = null; RankChanged = null; DistrictUnlocked = null;
        NeighborIncident = null;
    }
}

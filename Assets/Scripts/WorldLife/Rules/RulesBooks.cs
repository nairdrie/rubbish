using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where WorldLife's rules keep their books (Docs/WorldLife.md §9, §10.4 rules 1, 9, 15, 16): company counters and stamps,
/// money, Rep facts and letters. The game's books (<see cref="GameBooks"/>) are GameSession (rule 9: counters / stamps
/// until M1's stores), Ledger (rule 1), ProgressionEvents (Rep facts that Progression A applies) and HeadOffice. The
/// edit-mode self test swaps in <see cref="MemoryBooks"/> so the whole decide → consequence pipeline runs without ever
/// touching a save, the cash or the inbox.
/// </summary>
public interface IRulesBooks
{
    /// <summary>This machine decides (rule 15): solo, or the host. Consequences are written only when true.</summary>
    bool IsHost { get; }
    /// <summary>GameClock.TotalHours.</summary>
    double Hours { get; }
    /// <summary>Real seconds (immunities, cooldowns).</summary>
    double RealSeconds { get; }
    /// <summary>Neighbour fines are charged (the Town toggle, §9.3; off = receipt shame only).</summary>
    bool FinesOn { get; }

    int GetCounter(string key);
    void SetCounter(string key, int value);
    /// <summary>-1 when unset.</summary>
    double GetStamp(string key);
    void SetStamp(string key, double totalHours);

    /// <summary>Ledger.Charge (LedgerIds.NeighborDamages / CityWorks).</summary>
    void Charge(float amount, string reason, string ledgerId, Vector3? worldPos, string districtId);
    /// <summary>ProgressionEvents.RaiseNeighborIncident (Progression applies the Rep and its caps).</summary>
    void Incident(in NeighborIncidentFact fact);
    /// <summary>HeadOffice.Send (popup false = inbox only).</summary>
    void Letter(string subject, string body, bool popup);
}

/// <summary>The real books: GameSession, Ledger, ProgressionEvents, HeadOffice.</summary>
public sealed class GameBooks : IRulesBooks
{
    public bool IsHost => !GameSession.IsGuest;
    public double Hours => GameClock.TotalHours;
    public double RealSeconds => Time.timeAsDouble;
    public bool FinesOn => WorldLifeTuning.NeighborFines;
    public int GetCounter(string key) => GameSession.GetCounter(key);
    public void SetCounter(string key, int value) => GameSession.SetCounter(key, value);
    public double GetStamp(string key) => GameSession.GetStamp(key);
    public void SetStamp(string key, double totalHours) => GameSession.SetStamp(key, totalHours);
    public void Charge(float amount, string reason, string ledgerId, Vector3? worldPos, string districtId) =>
        Ledger.Charge(amount, reason, ledgerId, worldPos, districtId);
    public void Incident(in NeighborIncidentFact fact) => ProgressionEvents.RaiseNeighborIncident(fact);
    public void Letter(string subject, string body, bool popup) => HeadOffice.Send(subject, body, popup);
}

/// <summary>In-memory books for tests: everything the rules write lands in these lists and dictionaries.</summary>
public sealed class MemoryBooks : IRulesBooks
{
    public struct ChargeRow { public float amount; public string reason, ledgerId, districtId; public Vector3? pos; }
    public struct LetterRow { public string subject, body; public bool popup; }

    public bool host = true, finesOn = true;
    public double hours = 7.0, realSeconds = 100.0;
    public readonly Dictionary<string, int> counters = new Dictionary<string, int>();
    public readonly Dictionary<string, double> stamps = new Dictionary<string, double>();
    public readonly List<ChargeRow> charges = new List<ChargeRow>();
    public readonly List<NeighborIncidentFact> incidents = new List<NeighborIncidentFact>();
    public readonly List<LetterRow> letters = new List<LetterRow>();

    public bool IsHost => host;
    public double Hours => hours;
    public double RealSeconds => realSeconds;
    public bool FinesOn => finesOn;
    public int GetCounter(string key) => key != null && counters.TryGetValue(key, out int v) ? v : 0;
    public void SetCounter(string key, int value) { if (!string.IsNullOrEmpty(key)) counters[key] = value; }
    public double GetStamp(string key) => key != null && stamps.TryGetValue(key, out double v) ? v : -1;
    public void SetStamp(string key, double totalHours) { if (!string.IsNullOrEmpty(key)) stamps[key] = totalHours; }
    public void Charge(float amount, string reason, string ledgerId, Vector3? worldPos, string districtId) =>
        charges.Add(new ChargeRow { amount = amount, reason = reason, ledgerId = ledgerId, districtId = districtId, pos = worldPos });
    public void Incident(in NeighborIncidentFact fact) => incidents.Add(fact);
    public void Letter(string subject, string body, bool popup) => letters.Add(new LetterRow { subject = subject, body = body, popup = popup });

    public float Charged(string ledgerId)
    {
        float s = 0f;
        foreach (var c in charges) if (c.ledgerId == ledgerId) s += c.amount;
        return s;
    }

    public int IncidentCount(NeighborIncidentKind k)
    {
        int n = 0;
        foreach (var f in incidents) if (f.kind == k) n++;
        return n;
    }
}

/// <summary>
/// The active books (the game's unless a test swapped them) and the packed per-day counters: one int per key,
/// (day &lt;&lt; 10) | count, the same format as W0's <see cref="DailyCount"/> (so the save never grows per day).
/// </summary>
public static class RulesBooks
{
    public static readonly GameBooks Game = new GameBooks();
    static IRulesBooks active;

    /// <summary>The books the rules write to (tests swap in <see cref="MemoryBooks"/>; null restores the game's).</summary>
    public static IRulesBooks Active
    {
        get => active ?? Game;
        set => active = value;
    }

    const int Bits = 10, Mask = (1 << Bits) - 1;

    /// <summary>In-game day index of a TotalHours value (GameClock.DayIndex's formula).</summary>
    public static int DayOf(double hours) => (int)Math.Floor(hours / 24.0);
    /// <summary>The night a TotalHours value belongs to: noon to noon, so 22:00–06:00 is one night (Progression's formula).</summary>
    public static int NightOf(double hours) => (int)Math.Floor((hours - 12.0) / 24.0);

    public static int DailyGet(IRulesBooks b, string key, int day)
    {
        int v = b.GetCounter(key);
        return v != 0 && (v >> Bits) == day ? v & Mask : 0;
    }

    /// <summary>Adds delta for this day (an older day's count is dropped first). Returns the new count.</summary>
    public static int DailyAdd(IRulesBooks b, string key, int day, int delta)
    {
        int n = Mathf.Clamp(DailyGet(b, key, day) + delta, 0, Mask);
        b.SetCounter(key, (day << Bits) | n);
        return n;
    }

    /// <summary>A packed counter's (day, count), or false when it's empty.</summary>
    public static bool DailyRead(IRulesBooks b, string key, out int day, out int count)
    {
        int v = b.GetCounter(key);
        day = v >> Bits; count = v & Mask;
        return v != 0 && count > 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => active = null;
}

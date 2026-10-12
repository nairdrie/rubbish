using System;
using System.Collections.Generic;

/// <summary>Why a chip could not go down.</summary>
public enum BookAdd { Added, CantAfford, Refused }

/// <summary>
/// The roulette bets the player is building on the felt: one chip at a time (so Undo and "take a chip off this spot" work like the
/// old screen), always validated with <see cref="RouletteRules.Validate"/> and the player's cash, aggregated per spot when sent.
/// The host's accepted set (the roulette snapshot's participant bets) is the truth: <see cref="Adopt"/> replaces this book with it.
/// Pure, no Unity types.
/// </summary>
public sealed class RouletteBetBook
{
    struct Chip { public RouletteBetType type; public int index; public int cents; }

    readonly List<Chip> chips = new List<Chip>();

    /// <summary>Counts every change, so a sender knows when it has something new.</summary>
    public int Version { get; private set; }
    public long Total { get; private set; }
    public int ChipCount => chips.Count;
    public bool IsEmpty => chips.Count == 0;

    /// <summary>What is down on one spot (cents).</summary>
    public long OnSpot(RouletteBetType type, int index)
    {
        long t = 0;
        for (int i = 0; i < chips.Count; i++) if (chips[i].type == type && chips[i].index == index) t += chips[i].cents;
        return t;
    }

    /// <summary>How many chips are down on a spot (for stacking the ghost above them).</summary>
    public int ChipsOnSpot(RouletteBetType type, int index)
    {
        int n = 0;
        for (int i = 0; i < chips.Count; i++) if (chips[i].type == type && chips[i].index == index) n++;
        return n;
    }

    /// <summary>The set as the host takes it: one entry per spot, in the order the spots were first covered.</summary>
    public RouletteBet[] ToBets()
    {
        var list = new List<RouletteBet>(chips.Count);
        for (int i = 0; i < chips.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < list.Count; j++) if (list[j].type == chips[i].type && list[j].index == chips[i].index) { at = j; break; }
            if (at < 0) list.Add(new RouletteBet(chips[i].type, chips[i].index, chips[i].cents));
            else { var b = list[at]; b.cents += chips[i].cents; list[at] = b; }
        }
        return list.ToArray();
    }

    public int SpotCount => ToBets().Length;

    /// <summary>Put a chip on a spot. <paramref name="error"/> says which limit refused it (Refused), or nothing for CantAfford.</summary>
    public BookAdd TryAdd(RouletteBetType type, int index, int cents, long cashCents, out RouletteBetError error)
    {
        error = RouletteBetError.Ok;
        chips.Add(new Chip { type = type, index = index, cents = cents });
        var tentative = ToBets();
        chips.RemoveAt(chips.Count - 1);
        error = RouletteRules.Validate(tentative, tentative.Length);
        if (error != RouletteBetError.Ok) return BookAdd.Refused;
        if (Total + cents > cashCents) return BookAdd.CantAfford;
        chips.Add(new Chip { type = type, index = index, cents = cents });
        Total += cents;
        Version++;
        return BookAdd.Added;
    }

    /// <summary>Take the chip most recently put on this spot off (the whole stack when it came from the host's set as one chip). False when nothing is there.</summary>
    public bool RemoveLastAt(RouletteBetType type, int index, out int cents)
    {
        cents = 0;
        for (int i = chips.Count - 1; i >= 0; i--)
        {
            if (chips[i].type != type || chips[i].index != index) continue;
            cents = chips[i].cents;
            Total -= cents;
            chips.RemoveAt(i);
            Version++;
            return true;
        }
        return false;
    }

    /// <summary>Take the chip put down last, wherever it is.</summary>
    public bool Undo(out RouletteBetType type, out int index, out int cents)
    {
        type = RouletteBetType.Straight; index = 0; cents = 0;
        if (chips.Count == 0) return false;
        var c = chips[chips.Count - 1];
        type = c.type; index = c.index; cents = c.cents;
        chips.RemoveAt(chips.Count - 1);
        Total -= c.cents;
        Version++;
        return true;
    }

    public bool Clear()
    {
        if (chips.Count == 0) return false;
        chips.Clear();
        Total = 0;
        Version++;
        return true;
    }

    /// <summary>Replace the book with the host's set (each stack split into rack chips so Undo still goes chip by chip).</summary>
    public void Adopt(RouletteBet[] host, int[] rack)
    {
        chips.Clear();
        Total = 0;
        if (host != null)
            for (int i = 0; i < host.Length; i++)
            {
                var parts = ChipSelector.Decompose(host[i].cents, rack);
                long left = host[i].cents;
                foreach (int p in parts) { chips.Add(new Chip { type = host[i].type, index = host[i].index, cents = p }); left -= p; }
                if (left > 0) chips.Add(new Chip { type = host[i].type, index = host[i].index, cents = (int)left }); // below the smallest chip
                Total += host[i].cents;
            }
        Version++;
    }

    /// <summary>Does the book hold exactly this set (order does not matter)?</summary>
    public bool SameAs(RouletteBet[] other)
    {
        var mine = ToBets();
        int n = other == null ? 0 : other.Length;
        if (mine.Length != n) return false;
        for (int i = 0; i < n; i++)
            if (OnSpot(other[i].type, other[i].index) != other[i].cents) return false;
        return true;
    }

    /// <summary>Put a remembered set down again, as much of it as the limits and the cash allow. Returns the cents placed.</summary>
    public long Replace(RouletteBet[] set, long cashCents, int[] rack)
    {
        chips.Clear();
        Total = 0;
        Version++;
        if (set == null) return 0;
        for (int i = 0; i < set.Length; i++)
            foreach (int c in ChipSelector.Decompose(set[i].cents, rack))
                if (TryAdd(set[i].type, set[i].index, c, cashCents, out _) != BookAdd.Added) return Total;
        return Total;
    }
}

/// <summary>What to do with the host's picture of my bets while I am building a set.</summary>
public static class RouletteSync
{
    /// <summary>
    /// A roulette snapshot arrived: adopt its set into the local book? Yes when nothing of mine is in flight or unsent (the host
    /// is the truth), and always once the bets are locked (Locked / Spinning / Result: the host's set is what plays).
    /// </summary>
    public static bool ShouldAdopt(bool hasUnsentChanges, int requestsInFlight, RouletteTablePhase phase)
    {
        if (phase == RouletteTablePhase.Locked || phase == RouletteTablePhase.Spinning || phase == RouletteTablePhase.Result) return true;
        return !hasUnsentChanges && requestsInFlight == 0;
    }

    /// <summary>Is betting open for me? (Idle / Betting, the clock not out.)</summary>
    public static bool BettingOpen(RouletteTablePhase phase, double secondsLeft) =>
        phase == RouletteTablePhase.Idle || (phase == RouletteTablePhase.Betting && secondsLeft > 0.0);
}

/// <summary>At most about four sets a second go to the host (guard rb.casino.bets: 4 per second, burst 8): changes are coalesced.</summary>
public sealed class SendGate
{
    public const double MinInterval = 0.26;
    double lastAt = -1000.0;

    public bool Due(double now, bool dirty) => dirty && now - lastAt >= MinInterval;
    public void Mark(double now) => lastAt = now;
    public double LastAt => lastAt;
}

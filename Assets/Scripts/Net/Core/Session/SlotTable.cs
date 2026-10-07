using System;
using System.Collections.Generic;

/// <summary>
/// Host-side seat table (D9): which company and slot (0..companyCap-1) each PlayerKey holds. Replaces
/// <c>PlayerSpawner</c>'s <c>clientId % 4</c>. The slot picks spawn spacing and the Dumpling variant, so a player who
/// leaves and rejoins gets the same slot back when it's still free. Main thread only.
/// </summary>
public sealed class SlotTable
{
    struct Seat { public int company, slot; public bool active; public long releasedAt; }

    readonly int townCap, companyCap;
    readonly Dictionary<PlayerKey, Seat> seats = new Dictionary<PlayerKey, Seat>();
    long releaseCounter;

    public SlotTable(int townCap, int companyCap)
    {
        if (townCap < 1) throw new ArgumentOutOfRangeException(nameof(townCap));
        if (companyCap < 1) throw new ArgumentOutOfRangeException(nameof(companyCap));
        this.townCap = townCap;
        this.companyCap = companyCap;
    }

    public int TownCap => townCap;
    public int CompanyCap => companyCap;

    /// <summary>Active players.</summary>
    public int Count
    {
        get { int n = 0; foreach (var s in seats.Values) if (s.active) n++; return n; }
    }

    /// <summary>Active players in one company.</summary>
    public int CountIn(int companyIndex)
    {
        int n = 0;
        foreach (var s in seats.Values) if (s.active && s.company == companyIndex) n++;
        return n;
    }

    /// <summary>
    /// Seat <paramref name="p"/> in <paramref name="companyIndex"/>. Already seated: returns the same seat. Rejoining: the
    /// remembered slot if nobody active holds it. Otherwise the lowest slot that is neither held nor remembered for a
    /// released player, falling back to the lowest free one. Company cap is checked before the Town cap, so the 5th member
    /// of a Crew Town reads "Crew is full (4/4)".
    /// </summary>
    public bool TryAssign(PlayerKey p, int companyIndex, out int slot, out JoinReject why)
    {
        slot = -1;
        if (!p.IsValid || companyIndex < 0 || companyIndex >= NetConstants.MaxCompanies) { why = JoinReject.BadRequest; return false; }

        if (seats.TryGetValue(p, out var seat) && seat.active)
        {
            if (seat.company == companyIndex) { slot = seat.slot; why = JoinReject.None; return true; }
            why = JoinReject.BadRequest; // already seated in another company: leave first
            return false;
        }

        if (CountIn(companyIndex) >= companyCap) { why = JoinReject.CompanyFull; return false; }
        if (Count >= townCap) { why = JoinReject.TownFull; return false; }

        bool remembered = seats.TryGetValue(p, out var old) && old.company == companyIndex;
        int pick = remembered && !HeldByActive(companyIndex, old.slot) ? old.slot : -1;
        if (pick < 0)
        {
            for (int s = 0; s < companyCap && pick < 0; s++)
                if (!HeldByActive(companyIndex, s) && !RememberedForOther(companyIndex, s, p)) pick = s;
            for (int s = 0; s < companyCap && pick < 0; s++)
                if (!HeldByActive(companyIndex, s)) pick = s;
        }
        if (pick < 0) { why = JoinReject.CompanyFull; return false; } // unreachable while CountIn < companyCap

        seats[p] = new Seat { company = companyIndex, slot = pick, active = true };
        slot = pick;
        why = JoinReject.None;
        return true;
    }

    /// <summary>The player left: their seat is freed but remembered, so a rejoin gets it back if it's still free.</summary>
    public void Release(PlayerKey p)
    {
        if (!seats.TryGetValue(p, out var s) || !s.active) return;
        s.active = false;
        s.releasedAt = ++releaseCounter;
        seats[p] = s;
    }

    /// <summary>Drop the player entirely (kicked, banned, session over).</summary>
    public void Forget(PlayerKey p) => seats.Remove(p);

    /// <summary>The active seat of <paramref name="p"/>; false when they're not currently seated.</summary>
    public bool TryGet(PlayerKey p, out int companyIndex, out int slot)
    {
        if (seats.TryGetValue(p, out var s) && s.active) { companyIndex = s.company; slot = s.slot; return true; }
        companyIndex = -1; slot = -1;
        return false;
    }

    /// <summary>The remembered (released) seat of <paramref name="p"/>, if any.</summary>
    public bool TryGetRemembered(PlayerKey p, out int companyIndex, out int slot)
    {
        if (seats.TryGetValue(p, out var s) && !s.active) { companyIndex = s.company; slot = s.slot; return true; }
        companyIndex = -1; slot = -1;
        return false;
    }

    /// <summary>Load remembered (inactive) seats, e.g. from a Town save. Active seats are kept; bad rows are skipped.</summary>
    public void Restore(IEnumerable<(PlayerKey key, int company, int slot)> remembered)
    {
        if (remembered == null) return;
        foreach (var (key, company, slot) in remembered)
        {
            if (!key.IsValid || company < 0 || company >= NetConstants.MaxCompanies || slot < 0 || slot >= companyCap) continue;
            if (seats.TryGetValue(key, out var s) && s.active) continue;
            seats[key] = new Seat { company = company, slot = slot, active = false, releasedAt = ++releaseCounter };
        }
    }

    /// <summary>Every seat, active or remembered (for saving into the Town).</summary>
    public IEnumerable<(PlayerKey key, int company, int slot, bool active)> All()
    {
        foreach (var kv in seats) yield return (kv.Key, kv.Value.company, kv.Value.slot, kv.Value.active);
    }

    bool HeldByActive(int company, int slot)
    {
        foreach (var s in seats.Values) if (s.active && s.company == company && s.slot == slot) return true;
        return false;
    }

    bool RememberedForOther(int company, int slot, PlayerKey self)
    {
        foreach (var kv in seats)
            if (!kv.Value.active && kv.Value.company == company && kv.Value.slot == slot && kv.Key != self) return true;
        return false;
    }
}

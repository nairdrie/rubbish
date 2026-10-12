using System;
using System.Collections.Generic;

/// <summary>
/// Why a shared table or a machine seat said no (Docs/Casino.md §8). One byte. Used by <see cref="CasinoSeats"/>,
/// <see cref="CasinoSingleSeat"/>, <see cref="BlackjackTable"/> and <see cref="RouletteTable"/>.
/// </summary>
public enum CasinoTableError : byte
{
    Ok = 0,
    /// <summary>Every seat is taken.</summary>
    TableFull,
    /// <summary>That player already has a seat (or the slot machine already has its player).</summary>
    AlreadySeated,
    /// <summary>The key is not seated / not taking part.</summary>
    NotSeated,
    /// <summary>The table is in a phase that does not take this request.</summary>
    WrongPhase,
    /// <summary>The phase's deadline has passed; the next tick moves the table on without this request.</summary>
    TooLate,
    BelowMinimum,
    AboveMaximum,
    NotWholeDollars,
    /// <summary>Blackjack: this seat already has its stake down.</summary>
    AlreadyBet,
    /// <summary>Blackjack: this seat has no hand in the round (no stake, or it walked away).</summary>
    NotInRound,
    /// <summary>Blackjack: another seat is the one to act.</summary>
    NotYourTurn,
    /// <summary>Not legal for this hand right now.</summary>
    IllegalAction,
    /// <summary>The host's charge delegate said no: nothing happened, nothing was charged.</summary>
    CantAfford,
    /// <summary>Roulette: the bet set was refused, see the RouletteBetError that came with it.</summary>
    BadBets,
    /// <summary>A machine seat is taken by somebody else.</summary>
    Occupied,
}

/// <summary>
/// Who sits where at a table with a fixed number of seats (Docs/Casino.md §8.2). Pure bookkeeping, no clock and no dice.
/// An occupant is an int key (the host passes the client id); <see cref="Empty"/> (-1) is the key of an empty seat, so a
/// real key is never negative. Seats are numbered 0..<see cref="Capacity"/>-1.
/// </summary>
public sealed class CasinoSeats
{
    /// <summary>The key of an empty seat.</summary>
    public const int Empty = -1;

    readonly int[] keys;

    /// <summary>A table with <paramref name="capacity"/> seats (1 to 64), all empty.</summary>
    public CasinoSeats(int capacity)
    {
        if (capacity < 1 || capacity > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        keys = new int[capacity];
        for (int i = 0; i < keys.Length; i++) keys[i] = Empty;
    }

    public int Capacity => keys.Length;
    /// <summary>How many seats are taken.</summary>
    public int Count { get; private set; }
    public bool IsFull => Count == keys.Length;
    public bool IsEmpty => Count == 0;

    /// <summary>The key sitting at <paramref name="seat"/>, or <see cref="Empty"/> (also for a seat number out of range).</summary>
    public int KeyAt(int seat) => seat >= 0 && seat < keys.Length ? keys[seat] : Empty;
    public bool IsFree(int seat) => seat >= 0 && seat < keys.Length && keys[seat] == Empty;

    /// <summary>The seat of <paramref name="key"/>, or -1.</summary>
    public int SeatOf(int key)
    {
        if (key < 0) return -1;
        for (int i = 0; i < keys.Length; i++) if (keys[i] == key) return i;
        return -1;
    }

    public bool Contains(int key) => SeatOf(key) >= 0;

    /// <summary>
    /// Seats <paramref name="key"/> at <paramref name="preferredSeat"/> when that seat exists and is free, otherwise at the
    /// lowest free seat. Returns the seat, or -1 when the table is full, the key is already seated, or the key is negative.
    /// Use <see cref="TrySit"/> to learn which.
    /// </summary>
    public int Sit(int key, int preferredSeat = -1)
    {
        return TrySit(key, preferredSeat, out int seat) == CasinoTableError.Ok ? seat : -1;
    }

    /// <summary>Like <see cref="Sit"/> but says why it failed: <see cref="CasinoTableError.AlreadySeated"/> (with the existing seat in <paramref name="seat"/>), <see cref="CasinoTableError.TableFull"/> or <see cref="CasinoTableError.NotSeated"/> for a negative key.</summary>
    public CasinoTableError TrySit(int key, int preferredSeat, out int seat)
    {
        seat = -1;
        if (key < 0) return CasinoTableError.NotSeated;
        int existing = SeatOf(key);
        if (existing >= 0) { seat = existing; return CasinoTableError.AlreadySeated; }
        if (IsFree(preferredSeat)) seat = preferredSeat;
        else
        {
            for (int i = 0; i < keys.Length; i++) if (keys[i] == Empty) { seat = i; break; }
        }
        if (seat < 0) return CasinoTableError.TableFull;
        keys[seat] = key;
        Count++;
        return CasinoTableError.Ok;
    }

    /// <summary>Stands <paramref name="key"/> up. Returns the seat they left, or -1 when they were not seated.</summary>
    public int Leave(int key)
    {
        int seat = SeatOf(key);
        if (seat < 0) return -1;
        keys[seat] = Empty;
        Count--;
        return seat;
    }

    /// <summary>Everyone stands up.</summary>
    public void Clear()
    {
        for (int i = 0; i < keys.Length; i++) keys[i] = Empty;
        Count = 0;
    }

    /// <summary>The occupants in seat order (a new array; empty seats are left out).</summary>
    public int[] Occupants()
    {
        var list = new int[Count];
        int n = 0;
        for (int i = 0; i < keys.Length; i++) if (keys[i] != Empty) list[n++] = keys[i];
        return list;
    }

    /// <summary>Appends the occupants in seat order to <paramref name="into"/> (no allocation of its own) and returns how many.</summary>
    public int CopyOccupants(List<int> into)
    {
        int n = 0;
        for (int i = 0; i < keys.Length; i++) if (keys[i] != Empty) { into.Add(keys[i]); n++; }
        return n;
    }

    /// <summary>One key per seat, <see cref="Empty"/> for a free one (a copy).</summary>
    public int[] KeysBySeat() => (int[])keys.Clone();

    /// <summary>
    /// The free seat with the smallest distance, for "using the table takes the nearest free seat". <paramref name="distances"/>
    /// holds one distance per seat (a missing, NaN or negative entry is not a candidate). Ties go to the lower seat.
    /// Returns -1 when no free seat has a distance.
    /// </summary>
    public int NearestFreeSeat(float[] distances) => NearestFreeSeat(distances, float.PositiveInfinity);

    /// <summary>As <see cref="NearestFreeSeat(float[])"/> but a seat farther than <paramref name="maxDistance"/> does not count.</summary>
    public int NearestFreeSeat(float[] distances, float maxDistance)
    {
        if (distances == null) return -1;
        int best = -1;
        float bestD = float.PositiveInfinity;
        int n = Math.Min(distances.Length, keys.Length);
        for (int i = 0; i < n; i++)
        {
            float d = distances[i];
            if (keys[i] != Empty || float.IsNaN(d) || d < 0f || d > maxDistance) continue;
            if (best < 0 || d < bestD) { best = i; bestD = d; }
        }
        return best;
    }
}

/// <summary>
/// A seat for one player at a time, with idle release: a slot machine (Docs/Casino.md §8.4). The occupant holds it until
/// they <see cref="Release"/> it (screen closed, walked away, passed out, disconnected) or go
/// <see cref="IdleSeconds"/> without a <see cref="Touch"/> (a spin). Time is passed in, never read.
/// </summary>
public sealed class CasinoSingleSeat
{
    /// <summary>Three minutes without a spin and the machine frees itself.</summary>
    public const double IdleSeconds = 180.0;

    int key = CasinoSeats.Empty;
    double lastActive;

    /// <summary>The occupant's key, or <see cref="CasinoSeats.Empty"/>.</summary>
    public int Key => key;
    public bool Occupied => key != CasinoSeats.Empty;
    public bool IsOccupant(int k) => k >= 0 && key == k;
    /// <summary>When the occupant last did something (host seconds). Meaningless while free.</summary>
    public double LastActive => lastActive;
    /// <summary>When the machine will free itself if nothing happens (host seconds), or +infinity while free.</summary>
    public double IdleDeadline => Occupied ? lastActive + IdleSeconds : double.PositiveInfinity;

    /// <summary>
    /// Gives the machine to <paramref name="k"/> when it is free (Ok), or confirms it when they already have it (Ok, and
    /// counts as activity). Another occupant: <see cref="CasinoTableError.Occupied"/>. A negative key: NotSeated.
    /// </summary>
    public CasinoTableError TryOccupy(int k, double now)
    {
        if (k < 0) return CasinoTableError.NotSeated;
        if (key != CasinoSeats.Empty && key != k) return CasinoTableError.Occupied;
        key = k;
        lastActive = now;
        return CasinoTableError.Ok;
    }

    /// <summary>The occupant did something (a spin). False when <paramref name="k"/> is not the occupant.</summary>
    public bool Touch(int k, double now)
    {
        if (!IsOccupant(k)) return false;
        lastActive = now;
        return true;
    }

    /// <summary>Frees the machine if <paramref name="k"/> has it. False otherwise (somebody else's machine stays theirs).</summary>
    public bool Release(int k)
    {
        if (!IsOccupant(k)) return false;
        key = CasinoSeats.Empty;
        return true;
    }

    /// <summary>Frees the machine whoever has it (session ended). Returns the key that was seated, or Empty.</summary>
    public int ReleaseAny()
    {
        int old = key;
        key = CasinoSeats.Empty;
        return old;
    }

    /// <summary>Call about once a second. Frees the machine when the occupant has been idle for <see cref="IdleSeconds"/> and returns their key, otherwise <see cref="CasinoSeats.Empty"/>.</summary>
    public int Tick(double now)
    {
        if (key == CasinoSeats.Empty || now - lastActive < IdleSeconds) return CasinoSeats.Empty;
        int old = key;
        key = CasinoSeats.Empty;
        return old;
    }
}

/// <summary>Little-endian byte writer for the table snapshots (host to every client). Internal: the formats are owned by the snapshot structs.</summary>
internal sealed class CasinoWireWriter
{
    byte[] buf = new byte[256];
    int n;

    void Need(int more)
    {
        if (n + more <= buf.Length) return;
        Array.Resize(ref buf, Math.Max(buf.Length * 2, n + more));
    }

    public void U8(int v) { Need(1); buf[n++] = (byte)v; }
    public void U16(int v) { Need(2); buf[n++] = (byte)v; buf[n++] = (byte)(v >> 8); }
    public void I32(int v) { Need(4); buf[n++] = (byte)v; buf[n++] = (byte)(v >> 8); buf[n++] = (byte)(v >> 16); buf[n++] = (byte)(v >> 24); }
    public void F64(double v)
    {
        long b = BitConverter.DoubleToInt64Bits(v);
        Need(8);
        for (int i = 0; i < 8; i++) buf[n++] = (byte)(b >> (8 * i));
    }
    public void Bytes(byte[] v, int count) { Need(count); Array.Copy(v, 0, buf, n, count); n += count; }
    /// <summary>A money amount as a 32-bit int (every amount at the casino tables is far below 21 million dollars).</summary>
    public void Cents(long v) { I32((int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v))); }

    public byte[] ToArray()
    {
        var r = new byte[n];
        Array.Copy(buf, r, n);
        return r;
    }
}

/// <summary>Reads what <see cref="CasinoWireWriter"/> wrote. Never throws: running past the end sets <see cref="Ok"/> to false and returns zeros.</summary>
internal sealed class CasinoWireReader
{
    readonly byte[] b;
    int p;
    public bool Ok = true;

    public CasinoWireReader(byte[] data) { b = data ?? Array.Empty<byte>(); }

    public int Remaining => b.Length - p;
    public bool AtEnd => p >= b.Length;

    bool Take(int n)
    {
        if (!Ok || p + n > b.Length) { Ok = false; return false; }
        return true;
    }

    public int U8() { if (!Take(1)) return 0; return b[p++]; }
    public int U16() { if (!Take(2)) return 0; int v = b[p] | b[p + 1] << 8; p += 2; return v; }
    public int I32() { if (!Take(4)) return 0; int v = b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24; p += 4; return v; }
    public double F64()
    {
        if (!Take(8)) return 0;
        long v = 0;
        for (int i = 0; i < 8; i++) v |= (long)b[p + i] << (8 * i);
        p += 8;
        return BitConverter.Int64BitsToDouble(v);
    }
    public byte[] Bytes(int count)
    {
        if (count < 0 || !Take(count)) return Array.Empty<byte>();
        var r = new byte[count];
        Array.Copy(b, p, r, 0, count);
        p += count;
        return r;
    }
}

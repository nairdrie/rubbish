using System;
using System.Collections.Generic;

/// <summary>Where the shared wheel is (Docs/Casino.md §8.3).</summary>
public enum RouletteTablePhase : byte
{
    /// <summary>No bets down. The first bet set opens Betting.</summary>
    Idle = 0,
    /// <summary><see cref="RouletteTable.BettingSeconds"/> from the first bet set; ends early when every participant is Ready.</summary>
    Betting = 1,
    /// <summary>The instant betting closes: each participant's total is charged (an unaffordable set is cancelled) and the pocket is drawn. Never lasts: the next event moves to Spinning (or back to Idle if nobody paid).</summary>
    Locked = 2,
    /// <summary><see cref="RouletteTable.SpinSeconds"/> of wheel and ball, derived from the pocket and the start time.</summary>
    Spinning = 3,
    /// <summary><see cref="RouletteTable.ResultSeconds"/>: the winning pocket is marked and the wins are paid.</summary>
    Result = 4,
}

public enum RouletteTableEventKind : byte
{
    /// <summary>The new <c>phase</c>; <c>deadline</c> is when it ends (+infinity for Idle).</summary>
    PhaseChanged = 0,
    /// <summary>A participant joined. <c>key</c>, <c>colour</c>.</summary>
    Joined,
    /// <summary>A participant left. <c>flag</c> is true when their charged bets stay on the felt and pay in absentia.</summary>
    Left,
    /// <summary>A participant's bet set was replaced. <c>cents</c> is its total.</summary>
    BetsSet,
    BetsCleared,
    /// <summary><c>flag</c> is the new Ready state.</summary>
    ReadyChanged,
    /// <summary>The participant left during Betting: their set is dropped. Nothing was charged.</summary>
    SetDropped,
    /// <summary>The host's charge refused the set (or no charge was possible): the set is cancelled. Nothing was charged.</summary>
    SetCancelled,
    /// <summary>The host's charge accepted a set. <c>cents</c> is the total. (Informational: the host already knows.)</summary>
    Charged,
    /// <summary>The pocket was drawn and the wheel starts: <c>pocket</c>, <c>time</c> is the start time presenters animate from.</summary>
    Spun,
    /// <summary>MONEY: credit <c>cents</c> (stake plus winnings of the winning bets, 0 if all lost) to <c>key</c>, also when <c>flag</c> says they left. <c>net</c> is their net.</summary>
    ParticipantSettled,
    /// <summary>Last event of a settlement. <c>pocket</c> is the winning pocket, <c>net</c> the sum of the participants' nets.</summary>
    RoundSettled,
}

[Serializable]
public struct RouletteTableEvent
{
    public RouletteTableEventKind kind;
    public RouletteTablePhase phase;
    public double time;
    public int key;
    public int colour;
    public byte pocket;
    public bool flag;
    public long cents, net;
    public double deadline;

    public override string ToString() => kind + " t=" + time.ToString("0.00") + " key=" + key + " colour=" + colour + " pocket=" + pocket + " cents=" + cents + " net=" + net;
}

/// <summary>One participant as a spectator sees them: their colour, their chips on the felt, and (after the lock) what was staked and returned.</summary>
[Serializable]
public struct RouletteParticipantSnapshot
{
    public const byte FlagReady = 1, FlagAbsent = 2, FlagCharged = 4, FlagSettled = 8;

    public int key;
    /// <summary>Chip colour index 0..<see cref="RouletteTable.MaxParticipants"/>-1, stable while they stay (their seat order at the wheel).</summary>
    public byte colour;
    public byte flags;
    public RouletteBet[] bets;
    /// <summary>What was charged at the lock (0 before).</summary>
    public int stakedCents;
    /// <summary>What comes back once the result is out (0 before; stake plus winnings of the winning bets).</summary>
    public int returnCents;

    public bool Has(byte flag) => (flags & flag) != 0;
    public int BetCount => bets == null ? 0 : bets.Length;
    public long TotalCents { get { long t = 0; if (bets != null) for (int i = 0; i < bets.Length; i++) t += bets[i].cents; return t; } }
}

/// <summary>
/// The public state of the shared wheel, safe to send to every machine. There is nothing secret in it: the pocket is drawn only
/// at the lock and shown from the first moment of the spin. Times are the table's clock (use a clock every machine can read);
/// a presenter animates the wheel from <see cref="pocket"/> and <see cref="spinStart"/>.
/// </summary>
[Serializable]
public struct RouletteTableSnapshot
{
    public const byte Version = 1, Magic = 0xB2;

    public double now;
    public RouletteTablePhase phase;
    /// <summary>When the phase ends; +infinity in Idle.</summary>
    public double phaseDeadline;
    /// <summary>The pocket of the current spin (<see cref="RouletteTable.NoPocket"/> before the lock and after the result).</summary>
    public byte pocket;
    /// <summary>When the wheel started (table clock). Meaningless without a pocket.</summary>
    public double spinStart;
    /// <summary>Counts spins, so a late snapshot is never mistaken for a newer one.</summary>
    public int round;
    /// <summary>The last pockets, oldest first, at most <see cref="RouletteTable.HistoryLength"/>.</summary>
    public byte[] history;
    /// <summary>Everyone taking part (and anyone who left with chips still on the felt), in colour order.</summary>
    public RouletteParticipantSnapshot[] participants;

    public bool HasPocket => pocket != RouletteTable.NoPocket;

    /// <summary>How far through the spin it is at <paramref name="at"/>: 0 at the start, 1 at the end (clamped).</summary>
    public double SpinFraction(double at) => !HasPocket ? 0.0 : Math.Max(0.0, Math.Min(1.0, (at - spinStart) / RouletteTable.SpinSeconds));

    public byte[] Serialize()
    {
        var w = new CasinoWireWriter();
        w.U8(Magic);
        w.U8(Version);
        w.F64(now);
        w.U8((int)phase);
        w.F64(phaseDeadline);
        w.U8(pocket);
        w.F64(spinStart);
        w.I32(round);
        int hn = history == null ? 0 : history.Length;
        w.U8(hn);
        if (hn > 0) w.Bytes(history, hn);
        int pn = participants == null ? 0 : participants.Length;
        w.U8(pn);
        for (int i = 0; i < pn; i++)
        {
            var p = participants[i];
            w.I32(p.key);
            w.U8(p.colour);
            w.U8(p.flags);
            w.Cents(p.stakedCents);
            w.Cents(p.returnCents);
            int bn = p.bets == null ? 0 : p.bets.Length;
            w.U8(bn);
            for (int b = 0; b < bn; b++)
            {
                w.U8((int)p.bets[b].type);
                w.U8(p.bets[b].index);
                w.I32(p.bets[b].cents);
            }
        }
        return w.ToArray();
    }

    public static RouletteTableSnapshot Deserialize(byte[] data)
    {
        if (!TryDeserialize(data, out var snap)) throw new FormatException("not a roulette table snapshot (version " + Version + ")");
        return snap;
    }

    /// <summary>False for foreign, truncated or other-version bytes.</summary>
    public static bool TryDeserialize(byte[] data, out RouletteTableSnapshot snapshot)
    {
        snapshot = default;
        var r = new CasinoWireReader(data);
        if (r.U8() != Magic || r.U8() != Version) return false;
        var s = new RouletteTableSnapshot();
        s.now = r.F64();
        s.phase = (RouletteTablePhase)r.U8();
        s.phaseDeadline = r.F64();
        s.pocket = (byte)r.U8();
        s.spinStart = r.F64();
        s.round = r.I32();
        int hn = r.U8();
        if (hn > 64) return false;
        s.history = r.Bytes(hn);
        int pn = r.U8();
        if (pn > 32) return false;
        s.participants = new RouletteParticipantSnapshot[pn];
        for (int i = 0; i < pn && r.Ok; i++)
        {
            var p = new RouletteParticipantSnapshot();
            p.key = r.I32();
            p.colour = (byte)r.U8();
            p.flags = (byte)r.U8();
            p.stakedCents = r.I32();
            p.returnCents = r.I32();
            int bn = r.U8();
            if (bn > RouletteRules.MaxBetsPerSpin) return false;
            p.bets = new RouletteBet[bn];
            for (int b = 0; b < bn && r.Ok; b++)
            {
                var bet = new RouletteBet();
                bet.type = (RouletteBetType)r.U8();
                bet.index = (byte)r.U8();
                bet.cents = r.I32();
                p.bets[b] = bet;
            }
            s.participants[i] = p;
        }
        if (!r.Ok || !r.AtEnd) return false;
        snapshot = s;
        return true;
    }
}

/// <summary>
/// The shared roulette wheel of The Lucky Raccoon (Docs/Casino.md §8.3): up to <see cref="MaxParticipants"/> players bet on one
/// spin, chips coloured per player, one pocket for everybody. Pure and deterministic: the clock comes in as seconds, the pocket
/// comes from the dice the host passes to <see cref="Tick"/>, money is integer cents.
/// <para><b>How the host drives it:</b></para>
/// <list type="bullet">
/// <item><see cref="Join"/> when a player opens the wheel's screen, <see cref="Leave"/> when they close it or walk away.
/// <see cref="SetBets"/> sends the player's WHOLE bet set whenever it changes (validated by <see cref="RouletteRules.Validate"/>:
/// spots, whole dollars, $1 minimum, $100 inside / $500 outside per spot, $1,000 per spin) and replaces the previous one, until the lock.
/// <see cref="SetReady"/> ends Betting early once everybody has pressed it.</item>
/// <item><see cref="Tick"/>(now, dice, charge) every frame (or at least 5 times a second). At the lock it calls
/// <c>charge(key, totalCents)</c> once per participant with a set, in colour order; <c>false</c> cancels that set with an event and
/// nothing is charged for it. Then it draws the pocket from <c>dice</c>.</item>
/// <item>Money out: credit exactly the <c>cents</c> of every <see cref="RouletteTableEventKind.ParticipantSettled"/> event to its
/// <c>key</c> (also when the player left or disconnected), nothing else. Charges minus credits then equals minus the sum of nets.</item>
/// <item>After anything changes broadcast <see cref="Snapshot"/>'s bytes.</item>
/// </list>
/// Events queue up until the next <see cref="Tick"/> (or <see cref="TakeEvents"/>) and each is delivered once.
/// </summary>
public sealed class RouletteTable
{
    public const int MaxParticipants = 4;
    public const double BettingSeconds = 25, SpinSeconds = 7, ResultSeconds = 6;
    public const int HistoryLength = 12;
    /// <summary>The pocket value before the lock.</summary>
    public const byte NoPocket = 255;

    sealed class Slot
    {
        public int owner = -1;
        public bool ready, ghost, charged, settled;
        public readonly RouletteBet[] bets = new RouletteBet[RouletteRules.MaxBetsPerSpin];
        public int count;
        public long staked, returned;

        public void Reset()
        {
            owner = -1; ready = ghost = charged = settled = false; count = 0; staked = returned = 0;
        }
    }

    readonly CasinoSeats seats = new CasinoSeats(MaxParticipants);
    readonly Slot[] slots = new Slot[MaxParticipants];
    readonly List<byte> history = new List<byte>(HistoryLength + 1);
    RouletteTablePhase phase;
    double dueAt = double.PositiveInfinity, clock, spinStart;
    byte pocket = NoPocket;
    int round;
    List<RouletteTableEvent> queue = new List<RouletteTableEvent>(), output = new List<RouletteTableEvent>();

    public RouletteTable()
    {
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
    }

    // ---------------------------------------------------------------- queries
    public RouletteTablePhase Phase => phase;
    /// <summary>Who takes part. Read-only use.</summary>
    public CasinoSeats Participants => seats;
    public int ParticipantCount => seats.Count;
    public int Round => round;
    public byte Pocket => pocket;
    public double SpinStart => spinStart;
    public double NextDueAt => dueAt;
    public double Clock => clock;
    public long RoundsSettled { get; private set; }
    public bool IsParticipant(int key) => seats.Contains(key);
    /// <summary>The chip colour index (0..3) of a participant, or -1. Stable until they leave.</summary>
    public int ColourOf(int key) => seats.SeatOf(key);
    /// <summary>The last pockets, oldest first (a copy).</summary>
    public byte[] History() => history.ToArray();

    public bool IsReady(int key)
    {
        int c = seats.SeatOf(key);
        return c >= 0 && slots[c].ready;
    }

    /// <summary>A copy of the participant's current bet set (empty when none or not a participant).</summary>
    public RouletteBet[] BetsOf(int key)
    {
        int c = seats.SeatOf(key);
        if (c < 0) return new RouletteBet[0];
        var r = new RouletteBet[slots[c].count];
        Array.Copy(slots[c].bets, r, r.Length);
        return r;
    }

    public long TotalOf(int key)
    {
        int c = seats.SeatOf(key);
        return c < 0 ? 0 : RouletteRules.TotalStake(slots[c].bets, slots[c].count);
    }

    // ---------------------------------------------------------------- events
    void Emit(RouletteTableEvent e) => queue.Add(e);

    /// <summary>Everything queued since the last drain, then the queue is empty. The list is reused: copy what you keep.</summary>
    public List<RouletteTableEvent> TakeEvents()
    {
        var t = output; output = queue; queue = t;
        queue.Clear();
        return output;
    }

    double Now(double now)
    {
        if (now > clock) clock = now;
        return clock;
    }

    void ChangePhase(RouletteTablePhase next, double at, double deadline)
    {
        phase = next;
        dueAt = deadline;
        Emit(new RouletteTableEvent { kind = RouletteTableEventKind.PhaseChanged, phase = next, time = at, deadline = deadline });
    }

    // ---------------------------------------------------------------- joining
    /// <summary>Joins the wheel; returns the chip colour index (0..3) or -1 when it is full / the key is already there / negative.</summary>
    public int Join(int key, double now)
    {
        return TryJoin(key, now, out int colour) == CasinoTableError.Ok ? colour : -1;
    }

    /// <summary>Like <see cref="Join"/> but says why not (TableFull, AlreadySeated with the existing colour). Anyone may join in any phase, but bets only open in Idle / Betting.</summary>
    public CasinoTableError TryJoin(int key, double now, out int colour)
    {
        now = Now(now);
        colour = -1;
        if (key < 0) return CasinoTableError.NotSeated;
        int existing = seats.SeatOf(key);
        if (existing >= 0) { colour = existing; return CasinoTableError.AlreadySeated; }
        int free = -1;
        for (int i = 0; i < MaxParticipants && free < 0; i++) if (seats.IsFree(i) && !slots[i].ghost) free = i;
        if (free < 0) return CasinoTableError.TableFull;
        seats.TrySit(key, free, out colour);
        slots[colour].Reset();
        slots[colour].owner = key;
        Emit(new RouletteTableEvent { kind = RouletteTableEventKind.Joined, time = now, key = key, colour = colour });
        EvaluateBetting(now);
        return CasinoTableError.Ok;
    }

    /// <summary>
    /// Leaves the wheel. During Betting the player's set is dropped (nothing was charged). After the lock their charged bets stay
    /// on the felt and pay in absentia (a <see cref="RouletteTableEventKind.ParticipantSettled"/> with <c>flag</c> set); their
    /// colour is held until the round is cleared. False when they were not taking part.
    /// </summary>
    public bool Leave(int key, double now)
    {
        now = Now(now);
        int c = seats.SeatOf(key);
        if (c < 0) return false;
        var slot = slots[c];
        bool keeps = (phase == RouletteTablePhase.Spinning || phase == RouletteTablePhase.Result) && slot.charged;
        if (!keeps && slot.count > 0 && (phase == RouletteTablePhase.Betting || phase == RouletteTablePhase.Idle))
            Emit(new RouletteTableEvent { kind = RouletteTableEventKind.SetDropped, time = now, key = key, colour = c });
        seats.Leave(key);
        if (keeps) slot.ghost = true;
        else slot.Reset();
        Emit(new RouletteTableEvent { kind = RouletteTableEventKind.Left, time = now, key = key, colour = c, flag = keeps });
        EvaluateBetting(now);
        return true;
    }

    // ---------------------------------------------------------------- betting
    /// <summary>
    /// Replaces the participant's whole bet set with the first <paramref name="count"/> of <paramref name="bets"/> (count 0 clears it).
    /// Idle / Betting only; the first non-empty set opens Betting. Refused sets change nothing:
    /// BadBets (with <paramref name="betError"/>), WrongPhase, TooLate, NotSeated. Changing a set takes back the participant's Ready.
    /// </summary>
    public CasinoTableError SetBets(int key, RouletteBet[] bets, int count, double now, out RouletteBetError betError)
    {
        now = Now(now);
        betError = RouletteBetError.Ok;
        int c = seats.SeatOf(key);
        if (c < 0) return CasinoTableError.NotSeated;
        if (phase != RouletteTablePhase.Idle && phase != RouletteTablePhase.Betting) return CasinoTableError.WrongPhase;
        if (phase == RouletteTablePhase.Betting && now > dueAt) return CasinoTableError.TooLate;
        var slot = slots[c];
        if (count > 0)
        {
            betError = RouletteRules.Validate(bets, count);
            if (betError != RouletteBetError.Ok) return CasinoTableError.BadBets;
        }
        if (count <= 0)
        {
            if (slot.count > 0)
            {
                slot.count = 0;
                Emit(new RouletteTableEvent { kind = RouletteTableEventKind.BetsCleared, time = now, key = key, colour = c });
            }
        }
        else
        {
            Array.Copy(bets, slot.bets, count);
            slot.count = count;
            if (phase == RouletteTablePhase.Idle) ChangePhase(RouletteTablePhase.Betting, now, now + BettingSeconds);
            Emit(new RouletteTableEvent { kind = RouletteTableEventKind.BetsSet, time = now, key = key, colour = c, cents = RouletteRules.TotalStake(bets, count) });
        }
        if (slot.ready)
        {
            slot.ready = false;
            Emit(new RouletteTableEvent { kind = RouletteTableEventKind.ReadyChanged, time = now, key = key, colour = c, flag = false });
        }
        EvaluateBetting(now);
        return CasinoTableError.Ok;
    }

    /// <summary>Empties the participant's bet set (same as <c>SetBets</c> with none).</summary>
    public CasinoTableError ClearBets(int key, double now) => SetBets(key, null, 0, now, out _);

    /// <summary>Ready (or not) to spin. Betting only. When every participant is Ready the lock comes at once.</summary>
    public CasinoTableError SetReady(int key, bool ready, double now)
    {
        now = Now(now);
        int c = seats.SeatOf(key);
        if (c < 0) return CasinoTableError.NotSeated;
        if (phase != RouletteTablePhase.Betting) return CasinoTableError.WrongPhase;
        if (now > dueAt) return CasinoTableError.TooLate;
        if (slots[c].ready != ready)
        {
            slots[c].ready = ready;
            Emit(new RouletteTableEvent { kind = RouletteTableEventKind.ReadyChanged, time = now, key = key, colour = c, flag = ready });
        }
        EvaluateBetting(now);
        return CasinoTableError.Ok;
    }

    /// <summary>Betting ends early when every participant is Ready; with no set left at all it goes back to Idle.</summary>
    void EvaluateBetting(double now)
    {
        if (phase != RouletteTablePhase.Betting) return;
        bool any = false, all = true;
        for (int i = 0; i < MaxParticipants; i++)
        {
            if (seats.IsFree(i)) continue;
            if (slots[i].count > 0) any = true;
            if (!slots[i].ready) all = false;
        }
        if (!any) ChangePhase(RouletteTablePhase.Idle, now, double.PositiveInfinity);
        else if (all && dueAt > now) dueAt = now;
    }

    // ---------------------------------------------------------------- time
    /// <summary>
    /// Advances the wheel to <paramref name="now"/>: the lock (charging each set through <paramref name="charge"/>(key, cents) and
    /// drawing the pocket from <paramref name="dice"/>), the end of the spin (settlement) and the end of the result, each stamped with
    /// the time it was due. Returns every event since the last drain. The list is reused by the next call.
    /// </summary>
    public List<RouletteTableEvent> Tick(double now, Func<int, int> dice, Func<int, long, bool> charge)
    {
        now = Now(now);
        for (int guard = 0; guard < 64 && phase != RouletteTablePhase.Idle && dueAt <= now; guard++) AdvanceOne(dueAt, dice, charge);
        return TakeEvents();
    }

    /// <summary>Runs the next pending step now even if it is not due yet. For tests and hosts that pace it themselves.</summary>
    public List<RouletteTableEvent> Step(double now, Func<int, int> dice, Func<int, long, bool> charge)
    {
        now = Now(now);
        if (phase != RouletteTablePhase.Idle) AdvanceOne(Math.Min(dueAt, now), dice, charge);
        return TakeEvents();
    }

    void AdvanceOne(double at, Func<int, int> dice, Func<int, long, bool> charge)
    {
        switch (phase)
        {
            case RouletteTablePhase.Betting: Lock(at, dice, charge); break;
            case RouletteTablePhase.Spinning: Settle(at); break;
            case RouletteTablePhase.Result: EndRound(at); break;
            default: ChangePhase(RouletteTablePhase.Idle, at, double.PositiveInfinity); break;
        }
    }

    /// <summary>Betting is over: the pocket is drawn, every set is charged (or cancelled), and the wheel starts if anyone paid.</summary>
    void Lock(double at, Func<int, int> dice, Func<int, long, bool> charge)
    {
        if (dice == null) throw new InvalidOperationException("the wheel needs dice to spin");
        byte drawn = RouletteRules.Spin(dice);
        ChangePhase(RouletteTablePhase.Locked, at, at);
        bool any = false;
        for (int i = 0; i < MaxParticipants; i++)
        {
            var slot = slots[i];
            if (seats.IsFree(i) || slot.count <= 0) continue;
            long total = RouletteRules.TotalStake(slot.bets, slot.count);
            bool paid = false;
            if (charge != null)
            {
                try { paid = charge(slot.owner, total); }
                catch (Exception) { paid = false; }
            }
            if (paid)
            {
                slot.charged = true;
                slot.staked = total;
                any = true;
                Emit(new RouletteTableEvent { kind = RouletteTableEventKind.Charged, time = at, key = slot.owner, colour = i, cents = total });
            }
            else
            {
                slot.count = 0;
                Emit(new RouletteTableEvent { kind = RouletteTableEventKind.SetCancelled, time = at, key = slot.owner, colour = i, cents = total });
            }
        }
        if (!any)
        {
            ClearRound();
            ChangePhase(RouletteTablePhase.Idle, at, double.PositiveInfinity);
            return;
        }
        pocket = drawn;
        spinStart = at;
        round++;
        ChangePhase(RouletteTablePhase.Spinning, at, at + SpinSeconds);
        Emit(new RouletteTableEvent { kind = RouletteTableEventKind.Spun, phase = RouletteTablePhase.Spinning, time = at, pocket = pocket, deadline = dueAt });
    }

    void Settle(double at)
    {
        long sum = 0;
        for (int i = 0; i < MaxParticipants; i++)
        {
            var slot = slots[i];
            if (!slot.charged) continue;
            var r = RouletteRules.Resolve(slot.bets, slot.count, pocket);
            slot.returned = r.returnCents;
            slot.settled = true;
            sum += r.NetCents;
            Emit(new RouletteTableEvent
            {
                kind = RouletteTableEventKind.ParticipantSettled, time = at, key = slot.owner, colour = i, pocket = pocket,
                cents = r.returnCents, net = r.NetCents, flag = slot.ghost,
            });
        }
        history.Add(pocket);
        if (history.Count > HistoryLength) history.RemoveAt(0);
        RoundsSettled++;
        Emit(new RouletteTableEvent { kind = RouletteTableEventKind.RoundSettled, time = at, pocket = pocket, net = sum });
        ChangePhase(RouletteTablePhase.Result, at, at + ResultSeconds);
    }

    void EndRound(double at)
    {
        ClearRound();
        ChangePhase(RouletteTablePhase.Idle, at, double.PositiveInfinity);
    }

    /// <summary>The chips come off: sets, Ready flags and the pocket go; ghosts release their colour; participants stay.</summary>
    void ClearRound()
    {
        for (int i = 0; i < MaxParticipants; i++)
        {
            var slot = slots[i];
            if (seats.IsFree(i)) { slot.Reset(); continue; }
            slot.ready = slot.charged = slot.settled = false;
            slot.count = 0; slot.staked = slot.returned = 0;
        }
        pocket = NoPocket;
    }

    /// <summary>
    /// The session or the table is closing: everyone leaves (Betting sets are dropped, nothing was charged for them) and a spin in
    /// progress runs to its settlement. Returns the events; credit them like any others. Afterwards the table is Idle.
    /// </summary>
    public List<RouletteTableEvent> Close(double now, Func<int, int> dice, Func<int, long, bool> charge)
    {
        now = Now(now);
        foreach (int key in seats.Occupants()) Leave(key, now);
        for (int guard = 0; guard < 64 && phase != RouletteTablePhase.Idle; guard++) AdvanceOne(Math.Min(dueAt, now), dice, charge);
        return TakeEvents();
    }

    // ---------------------------------------------------------------- the public picture
    public RouletteTableSnapshot Snapshot()
    {
        var snap = new RouletteTableSnapshot
        {
            now = clock, phase = phase, phaseDeadline = dueAt, pocket = pocket, spinStart = spinStart, round = round,
            history = history.ToArray(),
        };
        var list = new List<RouletteParticipantSnapshot>(MaxParticipants);
        for (int i = 0; i < MaxParticipants; i++)
        {
            var slot = slots[i];
            if (slot.owner < 0) continue;
            byte flags = 0;
            if (slot.ready) flags |= RouletteParticipantSnapshot.FlagReady;
            if (slot.ghost) flags |= RouletteParticipantSnapshot.FlagAbsent;
            if (slot.charged) flags |= RouletteParticipantSnapshot.FlagCharged;
            if (slot.settled) flags |= RouletteParticipantSnapshot.FlagSettled;
            var bets = new RouletteBet[slot.count];
            Array.Copy(slot.bets, bets, slot.count);
            list.Add(new RouletteParticipantSnapshot
            {
                key = slot.owner, colour = (byte)i, flags = flags, bets = bets, stakedCents = (int)slot.staked, returnCents = (int)slot.returned,
            });
        }
        snap.participants = list.ToArray();
        return snap;
    }
}

/// <summary>What <see cref="RouletteTableSim.Chaos"/> found. Clean: <see cref="violations"/> == 0 and <see cref="Balanced"/>.</summary>
[Serializable]
public struct RouletteTableAudit
{
    public long spins, chargedCents, creditedCents, settledNetCents, settledStakedCents;
    public long cancelled, dropped, absentSettles, refusedCharges, snapshots;
    public long violations;
    public string firstViolation;

    public bool Balanced => chargedCents - creditedCents == -settledNetCents;
}

/// <summary>Drives a <see cref="RouletteTable"/> the way a host would, with a virtual clock, to measure it.</summary>
public static class RouletteTableSim
{
    /// <summary>The pocket of each of <paramref name="spins"/> spins of one player's $1 straight-up bet through the real table: how often each of the 38 came up.</summary>
    public static long[] PocketHistogram(long spins, Func<int, int> dice)
    {
        var counts = new long[RouletteRules.PocketCount];
        var table = new RouletteTable();
        table.Join(1, 0);
        var bet = new[] { new RouletteBet(RouletteBetType.Straight, 17, CasinoRules.ChipCents) };
        Func<int, long, bool> pay = (k, c) => true;
        double now = 0;
        long done = 0;
        while (done < spins)
        {
            if (table.Phase == RouletteTablePhase.Idle)
            {
                table.SetBets(1, bet, 1, now, out _);
                table.SetReady(1, true, now);
            }
            var events = table.Tick(now, dice, pay);
            for (int i = 0; i < events.Count; i++)
                if (events[i].kind == RouletteTableEventKind.Spun) { counts[events[i].pocket]++; done++; }
            if (events.Count == 0)
            {
                double due = table.NextDueAt;
                if (double.IsInfinity(due)) break;
                now = Math.Max(now, due);
            }
        }
        return counts;
    }

    /// <summary>
    /// Plays <paramref name="rounds"/> spins with up to four players who join, leave (before the lock and during the spin), bet
    /// random valid and invalid sets, press Ready, and sometimes cannot afford their set. A till adds up every charge and every
    /// credit the events ask for; <paramref name="chaos"/> decides the players' behaviour (separate from the wheel's dice).
    /// </summary>
    public static RouletteTableAudit Chaos(long rounds, Func<int, int> dice, Func<int, int> chaos)
    {
        var audit = new RouletteTableAudit();
        var table = new RouletteTable();
        double now = 0;
        int nextKey = 500;
        bool inCharge = false;
        Func<int, long, bool> till = (k, c) =>
        {
            if (chaos(6) == 0) { audit.refusedCharges++; return false; }
            audit.chargedCents += c;
            inCharge = true;
            return true;
        };
        var bets = new RouletteBet[RouletteRules.MaxBetsPerSpin];
        long steps = 0;

        while (audit.spins < rounds && steps++ < rounds * 300)
        {
            // players come and go
            if (table.ParticipantCount < RouletteTable.MaxParticipants && chaos(5) == 0) table.Join(nextKey++, now);
            int[] who = table.Participants.Occupants();
            for (int w = 0; w < who.Length; w++)
            {
                int key = who[w];
                if (chaos(80) == 0) { table.Leave(key, now); continue; }
                if (table.Phase != RouletteTablePhase.Idle && table.Phase != RouletteTablePhase.Betting) continue;
                int roll = chaos(10);
                if (roll < 3)
                {
                    int n = 1 + chaos(6);
                    for (int b = 0; b < n; b++)
                    {
                        var type = (RouletteBetType)chaos(RouletteRules.BetTypeCount);
                        int index = chaos(RouletteRules.SpotCount(type));
                        bets[b] = new RouletteBet(type, index, (1 + chaos(RouletteRules.IsInside(type) ? 12 : 60)) * 100);
                    }
                    if (chaos(12) == 0) bets[0].cents += 37; // not whole dollars: must be refused
                    table.SetBets(key, bets, n, now, out _);
                }
                else if (roll < 5) table.SetReady(key, true, now);
                else if (roll == 5) table.ClearBets(key, now);
            }

            inCharge = false;
            var events = table.Tick(now, dice, till);
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                switch (e.kind)
                {
                    case RouletteTableEventKind.Spun: audit.spins++; break;
                    case RouletteTableEventKind.ParticipantSettled:
                        audit.creditedCents += e.cents;
                        audit.settledNetCents += e.net;
                        if (e.flag) audit.absentSettles++;
                        break;
                    case RouletteTableEventKind.SetCancelled: audit.cancelled++; break;
                    case RouletteTableEventKind.SetDropped: audit.dropped++; break;
                    case RouletteTableEventKind.Charged: break;
                }
            }
            if (inCharge && table.Phase != RouletteTablePhase.Spinning) Violation(ref audit, "charged without spinning");

            if (steps % 5 == 0)
            {
                audit.snapshots++;
                var snap = table.Snapshot();
                var bytes = snap.Serialize();
                if (!RouletteTableSnapshot.TryDeserialize(bytes, out var back) || !Same(bytes, back.Serialize())) Violation(ref audit, "snapshot round trip");
                if (table.Phase == RouletteTablePhase.Betting && snap.HasPocket) Violation(ref audit, "pocket visible while betting");
            }
            if (table.Phase == RouletteTablePhase.Idle && !audit.Balanced) Violation(ref audit, "books out of balance between spins");

            if (events.Count == 0)
            {
                double due = table.NextDueAt;
                if (double.IsInfinity(due)) now += 0.5;
                else now = Math.Max(now + 0.05, due);
            }
            else now += 0.05;
        }

        var closing = table.Close(now, dice, till);
        for (int i = 0; i < closing.Count; i++)
        {
            var e = closing[i];
            if (e.kind == RouletteTableEventKind.ParticipantSettled)
            {
                audit.creditedCents += e.cents; audit.settledNetCents += e.net;
                if (e.flag) audit.absentSettles++;
            }
        }
        if (table.Phase != RouletteTablePhase.Idle) Violation(ref audit, "table did not close");
        if (!audit.Balanced) Violation(ref audit, "books out of balance after close: charged " + audit.chargedCents + " credited " + audit.creditedCents + " nets " + audit.settledNetCents);
        return audit;
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Violation(ref RouletteTableAudit audit, string what)
    {
        audit.violations++;
        if (audit.firstViolation == null) audit.firstViolation = what;
    }
}

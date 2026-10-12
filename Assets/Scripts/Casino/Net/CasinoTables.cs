using System;
using System.Collections.Generic;
using Unity.Netcode;

/// <summary>Things the host tells ONE player that they did not ask for just now (Docs/Casino.md §10).</summary>
public enum CasinoNotice : byte
{
    None = 0,
    /// <summary>Roulette: the host could not charge the bet set at the lock (not enough cash): it was cancelled, nothing was charged.</summary>
    SetCancelled = 1,
    /// <summary>The host stood the player up: they left the room, passed out, were carried off, or the session ended.</summary>
    Released = 2,
    /// <summary>A slot machine freed itself after three minutes without a spin.</summary>
    IdleRelease = 3,
    /// <summary>Blackjack: the decision timed out and the host stood the hand.</summary>
    TurnTimedOut = 4,
}

/// <summary>
/// The ids of the casino's tables and machines (Docs/Casino.md §10): one blackjack table <c>bj0</c>, one roulette wheel
/// <c>rl0</c>, five slot machines <c>slot0</c>..<c>slot4</c>. On the wire an id is one byte (<see cref="ToWire"/>).
/// </summary>
public static class CasinoTableIds
{
    public const string Blackjack = "bj0", Roulette = "rl0";
    static readonly string[] slots = { "slot0", "slot1", "slot2", "slot3", "slot4" };

    /// <summary>The id of slot machine <paramref name="machine"/> (0..4), or null.</summary>
    public static string Slot(int machine) => machine >= 0 && machine < slots.Length ? slots[machine] : null;

    /// <summary>Parses an id. <paramref name="index"/> is the machine for a slot, otherwise 0.</summary>
    public static bool TryParse(string id, out CasinoGame game, out int index)
    {
        game = CasinoGame.Blackjack;
        index = 0;
        if (id == Blackjack) return true;
        if (id == Roulette) { game = CasinoGame.Roulette; return true; }
        for (int i = 0; i < slots.Length; i++)
            if (id == slots[i]) { game = CasinoGame.Slot; index = i; return true; }
        return false;
    }

    /// <summary>0 = blackjack, 1 = roulette, 2..6 = slot machines 0..4; -1 for an unknown id.</summary>
    public static int ToWire(string id)
    {
        if (!TryParse(id, out var game, out int index)) return -1;
        return game == CasinoGame.Blackjack ? 0 : game == CasinoGame.Roulette ? 1 : 2 + index;
    }

    public static string FromWire(int wire) =>
        wire == 0 ? Blackjack : wire == 1 ? Roulette : wire >= 2 && wire < 2 + slots.Length ? slots[wire - 2] : null;
}

/// <summary>
/// The host's answer to one table request. <see cref="gate"/> is the request-level verdict (unknown table, too far from it, no
/// business, throttled, no answer); when it is Ok, <see cref="error"/> is what the table said (with <see cref="betError"/> for a
/// refused roulette bet set). <see cref="seat"/> is the seat / chip colour taken by Sit and Join (the existing one when the answer
/// is AlreadySeated), otherwise -1. One struct for every intent.
/// </summary>
public struct CasinoResult : INetworkSerializable
{
    public CasinoError gate;
    public CasinoTableError error;
    public RouletteBetError betError;
    public sbyte seat;

    public bool Ok => gate == CasinoError.Ok && error == CasinoTableError.Ok;
    /// <summary>Ok, or the player already sits there (a screen treats both as "you are seated").</summary>
    public bool Seated => gate == CasinoError.Ok && (error == CasinoTableError.Ok || error == CasinoTableError.AlreadySeated);

    public static CasinoResult Fail(CasinoError gate) => new CasinoResult { gate = gate, seat = -1 };
    public static CasinoResult Of(CasinoTableError error, int seat = -1, RouletteBetError betError = RouletteBetError.Ok) =>
        new CasinoResult { error = error, seat = (sbyte)seat, betError = betError };

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref gate);
        s.SerializeValue(ref error);
        s.SerializeValue(ref betError);
        s.SerializeValue(ref seat);
    }

    public override string ToString() => Ok ? "Ok" : gate != CasinoError.Ok ? gate.ToString() : error + (betError != RouletteBetError.Ok ? "/" + betError : "");
}

/// <summary>One slot pull as everybody sees it: the real stops, the line paid, who pulled, and when (host table time).</summary>
public struct SlotEventInfo
{
    public byte machine, stop0, stop1, stop2;
    /// <summary>Index into the machine's pays, or -1.</summary>
    public sbyte rule;
    public int winCents, stakeCents;
    /// <summary>The player (client id).</summary>
    public int key;
    /// <summary>When the reels start turning, on the table's clock (see <see cref="CasinoClient.ToLocalTime"/>).</summary>
    public double start;
    /// <summary>The win is worth the room's attention (≥ $1,000 or ≥ 50× the stake): add the celebration lights.</summary>
    public bool big;

    public int Stop(int reel) => reel == 0 ? stop0 : reel == 1 ? stop1 : stop2;
    public bool Won => rule >= 0;
    public SlotSpin Spin => new SlotSpin { machine = machine, stop0 = stop0, stop1 = stop1, stop2 = stop2, rule = rule, winCents = winCents };
}

/// <summary>What a client gets when it asks for the casino's current state (entering the room, joining late).</summary>
public struct CasinoSync
{
    public byte[] blackjack, roulette;
    /// <summary>Occupant (client id) of each slot machine, -1 when free.</summary>
    public int[] slotOccupants;
}

/// <summary>
/// The shared casino tables on the machine with authority (Docs/Casino.md §8, §10): ONE <see cref="BlackjackTable"/>
/// (<c>bj0</c>), ONE <see cref="RouletteTable"/> (<c>rl0</c>) and five slot-machine seats (<c>slot0</c>..<c>slot4</c>), with the
/// money, the proximity checks and the clean-up that go with them. Pure: the till, the dice, the player positions and the clock
/// all come in through delegates and parameters, so the same code runs on the host (wired to <see cref="Ledger"/> by
/// <see cref="CasinoHost"/>) and in EditMode tests (a fake till and seeded dice).
/// <para><b>Keys.</b> A player is the int client id; every intent takes the CALLER's client id from the transport (never from the
/// message), so nobody can act for somebody else's seat.</para>
/// <para><b>Money.</b> validate → affordable → charge → roll → credit, always. The shared tables get their approval from the
/// rules' <c>charge</c> delegate (called once, after everything else was validated). Whatever a table's events ask to be credited
/// (<c>BetRefunded</c>, <c>SeatSettled</c>, <c>ParticipantSettled</c>) is credited to the event's key, also for a player who has
/// left or disconnected; winnings the till refuses (their business is gone) are counted in <see cref="UnpaidCents"/>, never lost
/// silently.</para>
/// <para><b>Output.</b> After anything changed, <see cref="Flush"/> (called by every <see cref="Tick"/>) raises one snapshot event
/// per changed table (coalescing a frame's changes), every slot spin, slot occupancy changes and per-player notices.</para>
/// </summary>
public sealed class CasinoTables
{
    /// <summary>The host's own floor between two pulls of one player.</summary>
    public const double SlotMinSeconds = 1.0;
    /// <summary>A seated player whose body is not at their table for this long is stood up (pass-out, teleport, carried off).</summary>
    public const double PresenceGraceSeconds = 1.5, PresenceCheckSeconds = 0.5;

    // ---- the outside world
    /// <summary>The caller's business cash in cents, or a negative number when they have no business here.</summary>
    public Func<ulong, long> CashOf;
    /// <summary>(client, cents, ledger id, reason) → applied. Cents are always positive.</summary>
    public Func<ulong, long, string, string, bool> Charge, Credit;
    /// <summary>Host dice: uniform 0..n-1.</summary>
    public Func<int, int> Dice;
    /// <summary>(client, table id) → the caller is standing at that table. Null = no check.</summary>
    public Func<ulong, string, bool> Near;
    /// <summary>(client, table id) → the caller's body is still at the table they sit at. Null = everyone stays. Checked twice a second.</summary>
    public Func<ulong, string, bool> Present;
    /// <summary>(client, <see cref="CasinoGame"/>, what, cents won): tell the room.</summary>
    public Action<ulong, int, string, long> BigWin;

    // ---- raised by Flush
    /// <summary>The blackjack table changed: the public snapshot and its wire bytes (the same bytes for every client).</summary>
    public event Action<BlackjackTableSnapshot, byte[]> BlackjackChanged;
    public event Action<RouletteTableSnapshot, byte[]> RouletteChanged;
    public event Action<SlotEventInfo> SlotSpun;
    /// <summary>(machine, occupant client id or -1).</summary>
    public event Action<int, int> SlotOccupancyChanged;
    /// <summary>(client, table id, notice): tell that one player.</summary>
    public event Action<ulong, string, CasinoNotice> NoticeSent;

    // ---- the books (for audits and tests)
    public long TotalChargedCents { get; private set; }
    public long TotalCreditedCents { get; private set; }
    /// <summary>Winnings the till refused to pay (the business had gone). Never silently dropped.</summary>
    public long UnpaidCents { get; private set; }
    public long SlotSpins { get; private set; }
    public int BigWinsAnnounced { get; private set; }

    readonly BlackjackTable bj = new BlackjackTable();
    readonly RouletteTable rl = new RouletteTable();
    readonly CasinoSingleSeat[] slots = new CasinoSingleSeat[CasinoRules.SlotMachineCount];
    readonly Dictionary<int, double> lastSlotAt = new Dictionary<int, double>();
    readonly Dictionary<int, long> unpaid = new Dictionary<int, long>();
    readonly long[] betBySeat = new long[BlackjackTable.SeatCount];
    readonly Dictionary<int, double> absentSince = new Dictionary<int, double>();
    readonly List<int> scratch = new List<int>();
    readonly List<string> scratchTable = new List<string>();

    bool bjDirty, rlDirty, pumping;
    readonly bool[] occupancyDirty = new bool[CasinoRules.SlotMachineCount];
    List<SlotEventInfo> spins = new List<SlotEventInfo>(), spinsOut = new List<SlotEventInfo>();
    struct PendingNotice { public ulong client; public string table; public CasinoNotice notice; }
    List<PendingNotice> notices = new List<PendingNotice>(), noticesOut = new List<PendingNotice>();
    double nextPresenceAt = double.NegativeInfinity;

    public CasinoTables()
    {
        for (int i = 0; i < slots.Length; i++) slots[i] = new CasinoSingleSeat();
    }

    // ================================================================================================ queries

    /// <summary>The shared blackjack table. Read-only use (tests and the host's saves); play through the intents.</summary>
    public BlackjackTable BlackjackTable => bj;
    public RouletteTable RouletteTable => rl;
    public bool Wired => CashOf != null && Charge != null && Credit != null && Dice != null;

    /// <summary>The client id sitting at slot machine <paramref name="machine"/>, or -1.</summary>
    public int OccupantOfSlot(int machine) => machine >= 0 && machine < slots.Length ? slots[machine].Key : -1;

    /// <summary>The seat (blackjack), chip colour (roulette) or 0 (slot machine) of <paramref name="client"/> at a table, or -1.</summary>
    public int SeatAt(ulong client, string table)
    {
        int key = KeyOf(client);
        if (key < 0 || !CasinoTableIds.TryParse(table, out var game, out int index)) return -1;
        switch (game)
        {
            case CasinoGame.Blackjack: return bj.SeatOf(key);
            case CasinoGame.Roulette: return rl.ColourOf(key);
            default: return slots[index].IsOccupant(key) ? 0 : -1;
        }
    }

    public bool IsSeatedAnywhere(ulong client)
    {
        int key = KeyOf(client);
        if (key < 0) return false;
        if (bj.SeatOf(key) >= 0 || rl.IsParticipant(key)) return true;
        for (int i = 0; i < slots.Length; i++) if (slots[i].IsOccupant(key)) return true;
        return false;
    }

    /// <summary>Winnings that could not be paid to a player whose business is gone (client id → cents).</summary>
    public long UnpaidFor(int key) => unpaid.TryGetValue(key, out long c) ? c : 0;

    /// <summary>What a client needs on entering the casino: both tables' snapshots and who sits at the machines.</summary>
    public CasinoSync Sync(double now)
    {
        if (Wired) Pump(now);
        var s = new CasinoSync
        {
            blackjack = bj.Snapshot().Serialize(),
            roulette = rl.Snapshot().Serialize(),
            slotOccupants = new int[slots.Length],
        };
        for (int i = 0; i < slots.Length; i++) s.slotOccupants[i] = slots[i].Key;
        return s;
    }

    static int KeyOf(ulong client) => client <= int.MaxValue ? (int)client : -1;

    /// <summary>A win this large (or 50× the stake) is announced to the room.</summary>
    public static bool IsBig(long wonCents, long stakeCents) => CasinoBank.IsBig(wonCents, stakeCents);

    // ================================================================================================ the till

    long CashNow(ulong client)
    {
        try { return CashOf != null ? CashOf(client) : -1; }
        catch (Exception) { return -1; }
    }

    /// <summary>Affordability, then the charge. Nothing is charged unless Ok comes back.</summary>
    CasinoError Take(ulong client, long cents, string ledgerId, string reason)
    {
        if (cents <= 0) return CasinoError.BadStake;
        long cash = CashNow(client);
        if (cash < 0) return CasinoError.NoBusiness;
        if (!CasinoRules.CanAfford(cash, cents)) return CasinoError.CantAfford;
        bool ok;
        try { ok = Charge(client, cents, ledgerId, reason); }
        catch (Exception) { ok = false; }
        if (!ok) return CasinoError.NoBusiness;
        TotalChargedCents += cents;
        return CasinoError.Ok;
    }

    void Give(int key, long cents, string ledgerId, string reason)
    {
        if (cents <= 0) return;
        bool paid = false;
        if (key >= 0)
        {
            try { paid = Credit((ulong)key, cents, ledgerId, reason); }
            catch (Exception) { paid = false; }
        }
        if (paid) TotalCreditedCents += cents;
        else
        {
            UnpaidCents += cents;
            unpaid[key] = UnpaidFor(key) + cents;
        }
    }

    void Announce(int key, CasinoGame game, string what, long cents)
    {
        BigWinsAnnounced++;
        if (BigWin == null || key < 0) return;
        try { BigWin((ulong)key, (int)game, what, cents); }
        catch (Exception) { /* an announcement must never undo a paid win */ }
    }

    // ================================================================================================ intents

    CasinoError Gate(ulong client, string table, bool checkNear)
    {
        if (!Wired) return CasinoError.NotHost;
        if (KeyOf(client) < 0) return CasinoError.NoBusiness;
        if (checkNear && Near != null)
        {
            bool near;
            try { near = Near(client, table); }
            catch (Exception) { near = false; }
            if (!near) return CasinoError.TooFar;
        }
        if (CashNow(client) < 0) return CasinoError.NoBusiness;
        return CasinoError.Ok;
    }

    /// <summary>
    /// Sits <paramref name="client"/> at a table or machine: blackjack takes <paramref name="preferredSeat"/> or the lowest free
    /// seat, roulette joins the wheel (a chip colour), a slot machine gives its seat to the caller if it is free. A player is at
    /// one station at a time: sitting down stands them up from the others. Validates: the table id, the caller at the station,
    /// the caller has a business. Replies AlreadySeated (with the seat) when they already sit there.
    /// </summary>
    public CasinoResult Sit(ulong client, string table, int preferredSeat, double now)
    {
        if (!CasinoTableIds.TryParse(table, out var game, out int index)) return CasinoResult.Fail(CasinoError.BadMachine);
        var gate = Gate(client, table, true);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        LeaveEverywhere(client, now, table, CasinoNotice.None);
        CasinoResult r;
        switch (game)
        {
            case CasinoGame.Blackjack:
            {
                var err = bj.TrySit(key, preferredSeat, now, out int seat);
                r = CasinoResult.Of(err, seat);
                break;
            }
            case CasinoGame.Roulette:
            {
                var err = rl.TryJoin(key, now, out int colour);
                r = CasinoResult.Of(err, colour);
                break;
            }
            default:
            {
                bool changes = slots[index].Key != key;
                var err = slots[index].TryOccupy(key, now);
                if (err == CasinoTableError.Ok && changes) occupancyDirty[index] = true;
                r = CasinoResult.Of(err, err == CasinoTableError.Ok ? 0 : -1);
                break;
            }
        }
        Pump(now);
        return r;
    }

    /// <summary>
    /// Stands the caller up. Always allowed (no position check): blackjack refunds a stake that is not dealt yet and otherwise
    /// plays the hands out for them (stand, no insurance) and settles to them; roulette drops a set that has not locked and
    /// otherwise lets locked bets pay in absentia; a slot machine is freed. NotSeated when they were not there.
    /// </summary>
    public CasinoResult Leave(ulong client, string table, double now)
    {
        if (!CasinoTableIds.TryParse(table, out var game, out int index)) return CasinoResult.Fail(CasinoError.BadMachine);
        if (!Wired) return CasinoResult.Fail(CasinoError.NotHost);
        int key = KeyOf(client);
        if (key < 0) return CasinoResult.Fail(CasinoError.NoBusiness);
        Pump(now);
        bool was = LeaveOne(key, game, index, now);
        Pump(now);
        return was ? CasinoResult.Of(CasinoTableError.Ok) : CasinoResult.Of(CasinoTableError.NotSeated);
    }

    bool LeaveOne(int key, CasinoGame game, int index, double now)
    {
        switch (game)
        {
            case CasinoGame.Blackjack: return bj.Leave(key, now);
            case CasinoGame.Roulette: return rl.Leave(key, now);
            default:
                if (!slots[index].Release(key)) return false;
                occupancyDirty[index] = true;
                return true;
        }
    }

    /// <summary>Stands the key up at every station except <paramref name="except"/>; a non-None notice is sent for each one left.</summary>
    void LeaveEverywhere(ulong client, double now, string except, CasinoNotice notice)
    {
        int key = KeyOf(client);
        if (key < 0) return;
        if (except != CasinoTableIds.Blackjack && bj.Leave(key, now) && notice != CasinoNotice.None) Notify(client, CasinoTableIds.Blackjack, notice);
        if (except != CasinoTableIds.Roulette && rl.Leave(key, now) && notice != CasinoNotice.None) Notify(client, CasinoTableIds.Roulette, notice);
        for (int i = 0; i < slots.Length; i++)
        {
            string id = CasinoTableIds.Slot(i);
            if (id == except || !slots[i].Release(key)) continue;
            occupancyDirty[i] = true;
            if (notice != CasinoNotice.None) Notify(client, id, notice);
        }
    }

    /// <summary>
    /// Blackjack: put an opening stake down (whole dollars, $5 to $200) in Idle / Betting. The first stake opens Betting. Validates:
    /// the caller at the table and seated, the phase and its deadline, the limits, one stake per seat per round, then affordability;
    /// the stake is charged only if all of that passed.
    /// </summary>
    public CasinoResult BlackjackBet(ulong client, long cents, double now)
    {
        var gate = Gate(client, CasinoTableIds.Blackjack, true);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        var err = bj.PlaceBet(key, cents, now, c => Take(client, c, LedgerIds.CasinoWager, "Blackjack") == CasinoError.Ok);
        Pump(now);
        return CasinoResult.Of(err, bj.SeatOf(key));
    }

    /// <summary>Blackjack: sit this round out so the others need not wait for the clock (Betting, before a stake is down).</summary>
    public CasinoResult BlackjackSitOut(ulong client, double now)
    {
        var gate = Gate(client, CasinoTableIds.Blackjack, false);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        var err = bj.SitOut(key, now);
        Pump(now);
        return CasinoResult.Of(err, bj.SeatOf(key));
    }

    /// <summary>
    /// Blackjack decision: Hit, Stand, Double, Split on the caller's turn, Insurance / NoInsurance in the insurance window. A
    /// double, a split and insurance are charged BEFORE the card is drawn (the table asks the till once, after validating). No
    /// position check: a hand in play must always be finishable; the presence sweep deals with players who were moved away.
    /// </summary>
    public CasinoResult BlackjackAct(ulong client, BlackjackAction action, double now)
    {
        var gate = Gate(client, CasinoTableIds.Blackjack, false);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        if ((int)action < 0 || (int)action > (int)BlackjackAction.NoInsurance) return CasinoResult.Of(CasinoTableError.IllegalAction);
        Pump(now);
        int key = KeyOf(client);
        string what = action == BlackjackAction.Double ? "Blackjack: double" : action == BlackjackAction.Split ? "Blackjack: split" : "Blackjack: insurance";
        var err = bj.Act(key, action, now, c => Take(client, c, LedgerIds.CasinoWager, what) == CasinoError.Ok);
        Pump(now);
        return CasinoResult.Of(err, bj.SeatOf(key));
    }

    /// <summary>Roulette: join the wheel (same as <c>Sit("rl0")</c>). Validates the caller at the table. A chip colour comes back in <c>seat</c>.</summary>
    public CasinoResult RouletteJoin(ulong client, double now) => Sit(client, CasinoTableIds.Roulette, -1, now);

    /// <summary>
    /// Roulette: replace the caller's WHOLE bet set (count 0 clears it) until the lock. Validates: the caller at the wheel and
    /// taking part, the phase and deadline, every spot (<see cref="RouletteRules.Validate"/>: real spot, whole dollars, spot and
    /// spin limits). Nothing is charged here; the host charges the total at the lock, and cancels the set if it can't be covered.
    /// </summary>
    public CasinoResult RouletteSetBets(ulong client, RouletteBet[] bets, int count, double now)
    {
        var gate = Gate(client, CasinoTableIds.Roulette, true);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        if (bets == null) count = 0;
        else if (count > bets.Length) count = bets.Length;
        var err = rl.SetBets(key, bets, Math.Max(0, count), now, out var betError);
        Pump(now);
        return CasinoResult.Of(err, rl.ColourOf(key), betError);
    }

    /// <summary>Roulette: ready (or not) to spin; the lock comes at once when every participant is ready. Betting phase only.</summary>
    public CasinoResult RouletteReady(ulong client, bool ready, double now)
    {
        var gate = Gate(client, CasinoTableIds.Roulette, false);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        var err = rl.SetReady(key, ready, now);
        Pump(now);
        return CasinoResult.Of(err, rl.ColourOf(key));
    }

    /// <summary>
    /// One pull of the caller's own slot machine: the stake is charged, three stops are drawn from the host dice, the line is paid
    /// and the spin is broadcast to everybody (<see cref="SlotSpun"/>). Validates: a real machine, the caller at it, the caller
    /// is its occupant, ≥ <see cref="SlotMinSeconds"/> since their last pull, and they can cover the stake.
    /// </summary>
    public CasinoResult SlotSpin(ulong client, int machine, double now)
    {
        if (!CasinoRules.IsSlotMachine(machine) || !SlotRules.IsMachine(machine)) return CasinoResult.Fail(CasinoError.BadMachine);
        string table = CasinoTableIds.Slot(machine);
        var gate = Gate(client, table, true);
        if (gate != CasinoError.Ok) return CasinoResult.Fail(gate);
        Pump(now);
        int key = KeyOf(client);
        var seat = slots[machine];
        if (!seat.IsOccupant(key)) return CasinoResult.Of(seat.Occupied ? CasinoTableError.Occupied : CasinoTableError.NotSeated);
        if (lastSlotAt.TryGetValue(key, out double last) && now - last < SlotMinSeconds) return CasinoResult.Fail(CasinoError.Throttled);

        int stake = SlotRules.StakeCents(machine);
        string name = SlotRules.Machine(machine).name;
        var take = Take(client, stake, LedgerIds.CasinoWager, name);
        if (take == CasinoError.CantAfford) return CasinoResult.Of(CasinoTableError.CantAfford);
        if (take != CasinoError.Ok) return CasinoResult.Fail(take);
        lastSlotAt[key] = now;
        seat.Touch(key, now);

        SlotSpin spin;
        try { spin = SlotRules.Spin(machine, Dice); }
        catch (Exception)
        {
            // The dice misbehaved after the stake was taken: the pull never happened.
            Give(key, stake, LedgerIds.CasinoWager, name + " (void)");
            return CasinoResult.Fail(CasinoError.Unavailable);
        }
        SlotSpins++;
        bool big = spin.winCents > 0 && IsBig(spin.winCents, stake);
        if (spin.winCents > 0)
        {
            string line = spin.rule >= 0 ? SlotRules.Machine(machine).pays[spin.rule].name : "win";
            Give(key, spin.winCents, LedgerIds.CasinoPayout, name + ": " + line);
            if (big) Announce(key, CasinoGame.Slot, spin.rule == 0 ? "the " + name + " jackpot" : line + " on " + name, spin.winCents);
        }
        spins.Add(new SlotEventInfo
        {
            machine = (byte)machine, stop0 = spin.stop0, stop1 = spin.stop1, stop2 = spin.stop2, rule = spin.rule,
            winCents = spin.winCents, stakeCents = stake, key = key, start = now, big = big,
        });
        return CasinoResult.Of(CasinoTableError.Ok, 0);
    }

    // ================================================================================================ time

    /// <summary>Host, every frame: advances both tables (dealer pacing, timers, the roulette lock and spin), settles and pays,
    /// frees idle machines, stands up players whose body left, and raises the changes. Call it with one monotonic host clock.</summary>
    public void Tick(double now)
    {
        if (!Wired) return;
        Pump(now);
        if (now >= nextPresenceAt) CheckPresence(now);
        Flush();
    }

    bool RouletteCharge(int key, long cents) =>
        key >= 0 && Take((ulong)key, cents, LedgerIds.CasinoWager, "Roulette") == CasinoError.Ok;

    /// <summary>Advance everything that is due and process what happened (money first, then dirty flags).</summary>
    void Pump(double now)
    {
        if (pumping || !Wired) return;
        pumping = true;
        try
        {
            ProcessBlackjack(bj.Tick(now, Dice));
            ProcessRoulette(rl.Tick(now, Dice, RouletteCharge));
            for (int i = 0; i < slots.Length; i++)
            {
                int old = slots[i].Tick(now);
                if (old == CasinoSeats.Empty) continue;
                occupancyDirty[i] = true;
                Notify((ulong)old, CasinoTableIds.Slot(i), CasinoNotice.IdleRelease);
            }
        }
        finally { pumping = false; }
    }

    void ProcessBlackjack(List<BlackjackTableEvent> events)
    {
        if (events.Count > 0) bjDirty = true;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            switch (e.kind)
            {
                case BlackjackTableEventKind.BetPlaced:
                    if (e.seat >= 0 && e.seat < betBySeat.Length) betBySeat[e.seat] = e.cents;
                    break;
                case BlackjackTableEventKind.BetRefunded:
                    Give(e.key, e.cents, LedgerIds.CasinoWager, "Blackjack (refund)");
                    break;
                case BlackjackTableEventKind.TurnTimedOut:
                    if (e.key >= 0) Notify((ulong)e.key, CasinoTableIds.Blackjack, CasinoNotice.TurnTimedOut);
                    break;
                case BlackjackTableEventKind.SeatSettled:
                {
                    long net = e.result.totalNetCents;
                    Give(e.key, e.cents, LedgerIds.CasinoPayout, net > 0 ? "Blackjack win" : net == 0 ? "Blackjack push" : "Blackjack");
                    long bet = e.seat >= 0 && e.seat < betBySeat.Length ? betBySeat[e.seat] : 0;
                    if (IsBig(net, bet)) Announce(e.key, CasinoGame.Blackjack, "a big hand at blackjack", net);
                    break;
                }
            }
        }
    }

    void ProcessRoulette(List<RouletteTableEvent> events)
    {
        if (events.Count > 0) rlDirty = true;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            switch (e.kind)
            {
                case RouletteTableEventKind.SetCancelled:
                    if (e.key >= 0) Notify((ulong)e.key, CasinoTableIds.Roulette, CasinoNotice.SetCancelled);
                    break;
                case RouletteTableEventKind.ParticipantSettled:
                {
                    string pocket = RouletteRules.PocketName(e.pocket);
                    Give(e.key, e.cents, LedgerIds.CasinoPayout, "Roulette: " + pocket);
                    if (IsBig(e.net, e.cents - e.net)) Announce(e.key, CasinoGame.Roulette, pocket + " at roulette", e.net);
                    break;
                }
            }
        }
    }

    void CheckPresence(double now)
    {
        nextPresenceAt = now + PresenceCheckSeconds;
        if (Present == null) return;
        scratch.Clear();
        scratchTable.Clear();
        AddSeated(bj.Seats, CasinoTableIds.Blackjack);
        AddSeated(rl.Participants, CasinoTableIds.Roulette);
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].Occupied) { scratch.Add(slots[i].Key); scratchTable.Add(CasinoTableIds.Slot(i)); }
        if (scratch.Count == 0) { absentSince.Clear(); return; }
        // forget keys that are no longer seated anywhere
        if (absentSince.Count > 0)
        {
            var gone = new List<int>();
            foreach (var kv in absentSince) if (!scratch.Contains(kv.Key)) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++) absentSince.Remove(gone[i]);
        }
        for (int i = 0; i < scratch.Count; i++)
        {
            int key = scratch[i];
            bool here;
            try { here = Present((ulong)key, scratchTable[i]); }
            catch (Exception) { here = true; }
            if (here) { absentSince.Remove(key); continue; }
            if (!absentSince.TryGetValue(key, out double since)) { absentSince[key] = now; continue; }
            if (now - since < PresenceGraceSeconds) continue;
            absentSince.Remove(key);
            LeaveEverywhere((ulong)key, now, null, CasinoNotice.Released);
        }
        scratch.Clear();
        scratchTable.Clear();
        Pump(now);
    }

    void AddSeated(CasinoSeats seated, string table)
    {
        int before = scratch.Count;
        seated.CopyOccupants(scratch);
        for (int i = before; i < scratch.Count; i++) scratchTable.Add(table);
    }

    /// <summary>The client left the session: they stand up everywhere. Hands in play and locked bets settle in absentia (paid to
    /// their business if it still exists, otherwise counted in <see cref="UnpaidCents"/>).</summary>
    public void Disconnected(ulong client, double now)
    {
        if (!Wired) return;
        Pump(now);
        LeaveEverywhere(client, now, null, CasinoNotice.None);
        absentSince.Remove(KeyOf(client));
        lastSlotAt.Remove(KeyOf(client));
        Pump(now);
    }

    /// <summary>The player was moved away (pass-out, teleport): the host stands them up and tells them.</summary>
    public void Release(ulong client, double now)
    {
        if (!Wired) return;
        Pump(now);
        LeaveEverywhere(client, now, null, CasinoNotice.Released);
        Pump(now);
    }

    /// <summary>The session or the casino is closing: everyone stands up, a round in play runs to its settlement at once and is paid, the machines are freed.</summary>
    public void Close(double now)
    {
        if (!Wired) return;
        Pump(now);
        ProcessBlackjack(bj.Close(now, Dice));
        ProcessRoulette(rl.Close(now, Dice, RouletteCharge));
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].ReleaseAny() != CasinoSeats.Empty) occupancyDirty[i] = true;
        absentSince.Clear();
        lastSlotAt.Clear();
        bjDirty = rlDirty = true;
        Flush();
    }

    // ================================================================================================ output

    void Notify(ulong client, string table, CasinoNotice notice) =>
        notices.Add(new PendingNotice { client = client, table = table, notice = notice });

    /// <summary>Raise what changed since the last flush: one snapshot per changed table, then occupancy, then every slot spin and notice, in order.</summary>
    public void Flush()
    {
        if (bjDirty)
        {
            bjDirty = false;
            var snap = bj.Snapshot();
            var h = BlackjackChanged;
            if (h != null) Raise(h, snap, snap.Serialize());
        }
        if (rlDirty)
        {
            rlDirty = false;
            var snap = rl.Snapshot();
            var h = RouletteChanged;
            if (h != null) Raise(h, snap, snap.Serialize());
        }
        for (int i = 0; i < occupancyDirty.Length; i++)
        {
            if (!occupancyDirty[i]) continue;
            occupancyDirty[i] = false;
            var h = SlotOccupancyChanged;
            if (h != null) Raise(h, i, slots[i].Key);
        }
        if (spins.Count > 0)
        {
            var t = spins; spins = spinsOut; spinsOut = t;
            var h = SlotSpun;
            if (h != null) for (int i = 0; i < spinsOut.Count; i++) Raise(h, spinsOut[i]);
            spinsOut.Clear();
        }
        if (notices.Count > 0)
        {
            var t = notices; notices = noticesOut; noticesOut = t;
            var h = NoticeSent;
            if (h != null) for (int i = 0; i < noticesOut.Count; i++) Raise(h, noticesOut[i].client, noticesOut[i].table, noticesOut[i].notice);
            noticesOut.Clear();
        }
    }

    static void Raise<T>(Action<T> h, T a)
    {
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<T>)d)(a); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }

    static void Raise<A, B>(Action<A, B> h, A a, B b)
    {
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<A, B>)d)(a, b); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }

    static void Raise<A, B, C>(Action<A, B, C> h, A a, B b, C c)
    {
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<A, B, C>)d)(a, b, c); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }

    // ================================================================================================ words

    public static string Describe(CasinoTableError e)
    {
        switch (e)
        {
            case CasinoTableError.Ok: return "";
            case CasinoTableError.TableFull: return "Table full.";
            case CasinoTableError.AlreadySeated: return "You're already seated.";
            case CasinoTableError.NotSeated: return "Take a seat first.";
            case CasinoTableError.WrongPhase: return "Not now.";
            case CasinoTableError.TooLate: return "Too late for this round.";
            case CasinoTableError.BelowMinimum: return "Below the table minimum.";
            case CasinoTableError.AboveMaximum: return "Above the table maximum.";
            case CasinoTableError.NotWholeDollars: return "Whole dollars only.";
            case CasinoTableError.AlreadyBet: return "Your bet is already down.";
            case CasinoTableError.NotInRound: return "You're not in this hand.";
            case CasinoTableError.NotYourTurn: return "Wait for your turn.";
            case CasinoTableError.IllegalAction: return "You can't do that now.";
            case CasinoTableError.CantAfford: return "You can't cover that.";
            case CasinoTableError.BadBets: return "The table won't take those bets.";
            case CasinoTableError.Occupied: return "Somebody is using that machine.";
            default: return "No.";
        }
    }

    /// <summary>A short line for the player, empty for Ok. A refused roulette set says why (<see cref="CasinoBank.Describe(RouletteBetError)"/>).</summary>
    public static string Describe(CasinoResult r)
    {
        if (r.gate != CasinoError.Ok) return CasinoBank.Describe(r.gate);
        if (r.error == CasinoTableError.BadBets && r.betError != RouletteBetError.Ok) return CasinoBank.Describe(r.betError);
        return Describe(r.error);
    }
}

/// <summary>Bytes for a roulette bet set on the wire (guest to host): type, index, cents (int, little endian), 6 bytes a bet.</summary>
public static class CasinoWire
{
    /// <summary>Bytes per roulette bet.</summary>
    public const int BetBytes = 6;

    public static byte[] PackBets(RouletteBet[] bets, int count)
    {
        count = Math.Max(0, Math.Min(count, bets != null ? Math.Min(bets.Length, RouletteRules.MaxBetsPerSpin) : 0));
        var raw = new byte[count * BetBytes];
        for (int i = 0; i < count; i++)
        {
            int o = i * BetBytes, c = bets[i].cents;
            raw[o] = (byte)bets[i].type; raw[o + 1] = bets[i].index;
            raw[o + 2] = (byte)c; raw[o + 3] = (byte)(c >> 8); raw[o + 4] = (byte)(c >> 16); raw[o + 5] = (byte)(c >> 24);
        }
        return raw;
    }

    /// <summary>Null for anything that is not a whole number of bets or carries too many (an empty packet is an empty set). Amounts and spots are checked by the rules.</summary>
    public static RouletteBet[] UnpackBets(byte[] raw)
    {
        if (raw == null) return null;
        if (raw.Length == 0) return new RouletteBet[0];
        if (raw.Length % BetBytes != 0 || raw.Length / BetBytes > RouletteRules.MaxBetsPerSpin) return null;
        var bets = new RouletteBet[raw.Length / BetBytes];
        for (int i = 0; i < bets.Length; i++)
        {
            int o = i * BetBytes;
            bets[i].type = (RouletteBetType)raw[o];
            bets[i].index = raw[o + 1];
            bets[i].cents = raw[o + 2] | raw[o + 3] << 8 | raw[o + 4] << 16 | raw[o + 5] << 24;
        }
        return bets;
    }
}

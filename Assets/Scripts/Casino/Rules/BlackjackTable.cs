using System;
using System.Collections.Generic;

/// <summary>Where the shared blackjack table is (Docs/Casino.md §8.2).</summary>
public enum BlackjackTablePhase : byte
{
    /// <summary>Nobody has a stake down. The first <see cref="BlackjackTable.PlaceBet"/> opens Betting.</summary>
    Idle = 0,
    /// <summary><see cref="BlackjackTable.BettingSeconds"/> from the first stake; ends early when every seated player has bet or sat out.</summary>
    Betting = 1,
    /// <summary>Cards go out one per <see cref="BlackjackTable.DealStepSeconds"/>: one to each stake, the dealer's up card, a second to each, the hole card.</summary>
    Deal = 2,
    /// <summary>Only against a dealer ace. Simultaneous, <see cref="BlackjackTable.InsuranceSeconds"/>; the dealer peeks afterwards.</summary>
    Insurance = 3,
    /// <summary>Seat order, one decision at a time, 25 s each (120 s when one player is in the round).</summary>
    Turns = 4,
    /// <summary>The hole card turns, then one draw per <see cref="BlackjackTable.DealerStepSeconds"/>, then the round settles.</summary>
    Dealer = 5,
    /// <summary>The settled round stays on the felt for <see cref="BlackjackTable.ResultsSeconds"/>.</summary>
    Results = 6,
}

/// <summary>What happened at the table. Every event is PUBLIC: a face-down card is reported as <see cref="BlackjackTable.Hidden"/>.</summary>
public enum BlackjackTableEventKind : byte
{
    /// <summary><see cref="BlackjackTableEvent.phase"/> is the new phase, <c>deadline</c> when it ends (+infinity if it has no clock).</summary>
    PhaseChanged = 0,
    /// <summary>A player sat. <c>seat</c>, <c>key</c>.</summary>
    Seated,
    /// <summary>A player stood up. <c>seat</c>, <c>key</c>; <c>flag</c> is true when their hands stay in play and settle in absentia.</summary>
    SeatLeft,
    /// <summary>A stake went down (the host has charged it). <c>seat</c>, <c>key</c>, <c>cents</c>.</summary>
    BetPlaced,
    /// <summary>A seated player sat the round out. <c>seat</c>, <c>key</c>.</summary>
    SatOut,
    /// <summary>MONEY: credit <c>cents</c> to <c>key</c>. A stake taken back because the player left before the deal.</summary>
    BetRefunded,
    /// <summary>The shoe was shuffled. <c>flag</c> is true for an emergency shuffle in the middle of a round.</summary>
    Reshuffled,
    /// <summary>A card to a seat's hand (<c>seat</c>, <c>hand</c>) or to the dealer (<c>seat</c> = -1). <c>faceDown</c> cards carry 255.</summary>
    CardDealt,
    /// <summary>Insurance is open until <c>deadline</c>.</summary>
    InsuranceOpened,
    /// <summary>A seat took insurance. <c>seat</c>, <c>key</c>, <c>cents</c> (the host has charged it).</summary>
    InsuranceTaken,
    /// <summary>A seat declined insurance, or its time ran out. <c>seat</c>, <c>key</c>.</summary>
    InsuranceDeclined,
    InsuranceClosed,
    /// <summary>The dealer looked under an ace or a ten. <c>flag</c> is true when there was a blackjack.</summary>
    Peek,
    /// <summary>The dealer turned the hole card over. <c>card</c> is the real card.</summary>
    HoleRevealed,
    /// <summary>A decision is asked of <c>seat</c> / <c>hand</c> until <c>deadline</c>. Sent again after every action that leaves the seat to act.</summary>
    TurnStarted,
    /// <summary>A player acted. <c>seat</c>, <c>hand</c> (the hand acted on), <c>action</c>, <c>cents</c> (what it cost).</summary>
    PlayerAction,
    /// <summary>Time ran out: the seat's remaining hands stood. <c>seat</c>, <c>key</c>.</summary>
    TurnTimedOut,
    /// <summary>The dealer drew <c>card</c>.</summary>
    DealerCard,
    /// <summary>MONEY: credit <c>cents</c> (the return: stakes plus winnings, never negative) to <c>key</c>, even when <c>flag</c> says the player had left. <c>result</c> holds the nets.</summary>
    SeatSettled,
    /// <summary>The last event of a settlement. <c>cents</c> is the sum of every seat's total net (the house takes the negative of it).</summary>
    RoundSettled,
}

/// <summary>One thing that happened. A plain struct; which fields matter depends on <see cref="kind"/>.</summary>
[Serializable]
public struct BlackjackTableEvent
{
    public BlackjackTableEventKind kind;
    public BlackjackTablePhase phase;
    /// <summary>When it happened on the table's clock (the time the step was due, not the time the host got round to it).</summary>
    public double time;
    /// <summary>The seat, or -1 for the dealer / the table.</summary>
    public int seat;
    public int hand;
    /// <summary>The player: the occupant, or the owner of the hands for settlements and refunds.</summary>
    public int key;
    public byte card;
    public bool faceDown, flag;
    public BlackjackAction action;
    public long cents;
    public double deadline;
    /// <summary>For <see cref="BlackjackTableEventKind.SeatSettled"/>.</summary>
    public BlackjackResult result;

    public override string ToString() => kind + " t=" + time.ToString("0.00") + " seat=" + seat + " hand=" + hand + " key=" + key + " card=" + card + " cents=" + cents;
}

/// <summary>One hand as a spectator sees it. <see cref="flags"/> are the <c>Flag*</c> bits.</summary>
[Serializable]
public struct BlackjackHandSnapshot
{
    public const byte FlagDoubled = 1, FlagFromSplit = 2, FlagSplitAces = 4, FlagDone = 8, FlagStood = 16, FlagBusted = 32, FlagBlackjack = 64, FlagActive = 128;

    public byte[] cards;
    /// <summary>What rides on the hand (twice the stake after a double).</summary>
    public int stakeCents;
    public byte flags;
    /// <summary>None until the round settles.</summary>
    public BlackjackOutcome outcome;
    /// <summary>Net of this hand once settled (0 before).</summary>
    public int netCents;

    public bool Has(byte flag) => (flags & flag) != 0;
    public int Value => cards == null ? 0 : BlackjackRules.HandValue(cards, cards.Length);
}

/// <summary>One seat. <see cref="key"/> is who sits there now; <see cref="ownerKey"/> who the stake and hands belong to (they differ when the player left mid-round or a new one sat down).</summary>
[Serializable]
public struct BlackjackSeatSnapshot
{
    public const byte FlagInRound = 1, FlagAbsent = 2, FlagSitOut = 4, FlagNatural = 8, FlagInsuranceDecided = 16, FlagSettled = 32, FlagActing = 64;

    /// <summary>The sitter, or -1.</summary>
    public int key;
    /// <summary>The owner of this round's stake, or -1.</summary>
    public int ownerKey;
    public byte flags;
    /// <summary>The opening stake (0 = none down).</summary>
    public int betCents;
    public int insuranceCents;
    public BlackjackHandSnapshot[] hands;
    public int insuranceNetCents, totalNetCents;
    /// <summary>Last round's result at this seat, kept while the table is Idle: who played it (-1 = nobody) and their total net.</summary>
    public int lastKey, lastNetCents;

    public bool Has(byte flag) => (flags & flag) != 0;
    public bool Occupied => key >= 0;
    public int HandCount => hands == null ? 0 : hands.Length;
}

/// <summary>
/// Everything a spectator at the shared blackjack table may know and nothing else: no shoe order, and the dealer's hole card
/// is <see cref="BlackjackTable.Hidden"/> (255) until the dealer turns it. The host broadcasts <see cref="Serialize"/>'s bytes
/// whenever the table changes. Times are the table's clock, whatever clock the host passes in (use one clock every machine can
/// read, e.g. the network server time); a client turns a deadline into a countdown with <c>deadline - now</c>.
/// </summary>
[Serializable]
public struct BlackjackTableSnapshot
{
    public const byte Version = 1, Magic = 0xB1;

    /// <summary>The table's clock when this was taken.</summary>
    public double now;
    public BlackjackTablePhase phase;
    /// <summary>When the phase (or, in Turns, the current decision) times out. +infinity when the phase has no clock (Idle).</summary>
    public double phaseDeadline;
    /// <summary>Counts rounds, so a late snapshot is never mistaken for a newer one.</summary>
    public int round;
    /// <summary>Cards left in the shoe (of 312): the cut-card indicator.</summary>
    public int shoeLeft;
    /// <summary>The seat to act in Turns (and its hand), or -1.</summary>
    public int activeSeat, activeHand;
    /// <summary>The dealer's cards; the hole card (index 1) is 255 while hidden.</summary>
    public byte[] dealer;
    /// <summary>Always <see cref="BlackjackTable.SeatCount"/> long.</summary>
    public BlackjackSeatSnapshot[] seats;

    public bool HoleHidden => dealer != null && dealer.Length >= 2 && dealer[1] == BlackjackTable.Hidden;
    /// <summary>Cards left as a fraction of the full shoe (1 = fresh).</summary>
    public float ShoeFraction => shoeLeft / (float)BlackjackRules.ShoeCards;
    /// <summary>The cut card is out: the next round shuffles first.</summary>
    public bool CutReached => shoeLeft <= BlackjackRules.ShoeCards - BlackjackRules.CutCard;
    public double TurnDeadline => phase == BlackjackTablePhase.Turns ? phaseDeadline : double.PositiveInfinity;

    /// <summary>What the dealer visibly shows (a hidden hole card does not count).</summary>
    public int DealerShownValue
    {
        get
        {
            if (dealer == null) return 0;
            int n = HoleHidden ? 1 : dealer.Length;
            return BlackjackRules.HandValue(dealer, n);
        }
    }

    /// <summary>The bytes the host sends. Versioned; <see cref="TryDeserialize"/> reads them back.</summary>
    public byte[] Serialize()
    {
        var w = new CasinoWireWriter();
        w.U8(Magic);
        w.U8(Version);
        w.F64(now);
        w.U8((int)phase);
        w.F64(phaseDeadline);
        w.I32(round);
        w.U16(shoeLeft);
        w.U8(activeSeat + 1);
        w.U8(activeHand);
        int dn = dealer == null ? 0 : dealer.Length;
        w.U8(dn);
        if (dn > 0) w.Bytes(dealer, dn);
        int sn = seats == null ? 0 : seats.Length;
        w.U8(sn);
        for (int i = 0; i < sn; i++)
        {
            var s = seats[i];
            w.I32(s.key);
            w.I32(s.ownerKey);
            w.U8(s.flags);
            w.Cents(s.betCents);
            w.Cents(s.insuranceCents);
            w.Cents(s.insuranceNetCents);
            w.Cents(s.totalNetCents);
            w.I32(s.lastKey);
            w.Cents(s.lastNetCents);
            int hn = s.hands == null ? 0 : s.hands.Length;
            w.U8(hn);
            for (int h = 0; h < hn; h++)
            {
                var hand = s.hands[h];
                int cn = hand.cards == null ? 0 : hand.cards.Length;
                w.U8(cn);
                if (cn > 0) w.Bytes(hand.cards, cn);
                w.Cents(hand.stakeCents);
                w.U8(hand.flags);
                w.U8((int)hand.outcome);
                w.Cents(hand.netCents);
            }
        }
        return w.ToArray();
    }

    public static BlackjackTableSnapshot Deserialize(byte[] data)
    {
        if (!TryDeserialize(data, out var snap)) throw new FormatException("not a blackjack table snapshot (version " + Version + ")");
        return snap;
    }

    /// <summary>False for foreign, truncated or other-version bytes (<paramref name="snapshot"/> is then default).</summary>
    public static bool TryDeserialize(byte[] data, out BlackjackTableSnapshot snapshot)
    {
        snapshot = default;
        var r = new CasinoWireReader(data);
        if (r.U8() != Magic || r.U8() != Version) return false;
        var s = new BlackjackTableSnapshot();
        s.now = r.F64();
        s.phase = (BlackjackTablePhase)r.U8();
        s.phaseDeadline = r.F64();
        s.round = r.I32();
        s.shoeLeft = r.U16();
        s.activeSeat = r.U8() - 1;
        s.activeHand = r.U8();
        int dn = r.U8();
        if (dn > BlackjackRules.MaxHandCards) return false;
        s.dealer = r.Bytes(dn);
        int sn = r.U8();
        if (sn > 16) return false;
        s.seats = new BlackjackSeatSnapshot[sn];
        for (int i = 0; i < sn && r.Ok; i++)
        {
            var seat = new BlackjackSeatSnapshot();
            seat.key = r.I32();
            seat.ownerKey = r.I32();
            seat.flags = (byte)r.U8();
            seat.betCents = r.I32();
            seat.insuranceCents = r.I32();
            seat.insuranceNetCents = r.I32();
            seat.totalNetCents = r.I32();
            seat.lastKey = r.I32();
            seat.lastNetCents = r.I32();
            int hn = r.U8();
            if (hn > BlackjackRules.MaxHands) return false;
            seat.hands = new BlackjackHandSnapshot[hn];
            for (int h = 0; h < hn && r.Ok; h++)
            {
                var hand = new BlackjackHandSnapshot();
                int cn = r.U8();
                if (cn > BlackjackRules.MaxHandCards) return false;
                hand.cards = r.Bytes(cn);
                hand.stakeCents = r.I32();
                hand.flags = (byte)r.U8();
                hand.outcome = (BlackjackOutcome)r.U8();
                hand.netCents = r.I32();
                seat.hands[h] = hand;
            }
            s.seats[i] = seat;
        }
        if (!r.Ok || !r.AtEnd) return false;
        snapshot = s;
        return true;
    }
}

/// <summary>
/// The shared blackjack table of The Lucky Raccoon (Docs/Casino.md §8.2): five seats, one six-deck shoe, one dealer hand. Pure
/// and deterministic: the clock is passed in as seconds, the only randomness is the shuffle (the dice the host passes to
/// <see cref="Tick"/>), and money is integer cents. It plays every hand with <see cref="BlackjackRules"/> itself (a seat is a
/// <see cref="BlackjackRound"/> whose dealer cannot draw), and settles with <see cref="BlackjackRules.Settle"/>, so a hand
/// pays exactly what it pays at the single-player table.
/// <para><b>How the host drives it</b> (all on the host; guests only send intents):</para>
/// <list type="bullet">
/// <item>Call <see cref="Tick"/> every frame or at least 5 times a second with the clock and the host dice. It advances every
/// timer and step that is due and returns every event since the last drain, in order.</item>
/// <item>Intents: <see cref="Sit"/>, <see cref="Leave"/>, <see cref="PlaceBet"/>, <see cref="SitOut"/>, <see cref="Act"/>.
/// <see cref="PlaceBet"/> and <see cref="Act"/> take a <c>charge</c> delegate: the table calls it at most once, only after
/// everything else was validated, and applies the request only if it returns true (so a stake can never be charged without
/// being placed, or placed without being charged). A request that returns anything but Ok changed nothing.</item>
/// <item>Money in: whatever the <c>charge</c> delegate approved. Money out: credit exactly the <c>cents</c> of every
/// <see cref="BlackjackTableEventKind.BetRefunded"/> and <see cref="BlackjackTableEventKind.SeatSettled"/> event to its
/// <c>key</c> (also when that player has left or disconnected), nothing else. Then charges minus credits equals minus the
/// sum of the settled nets.</item>
/// <item>After anything changes, broadcast <see cref="Snapshot"/>'s bytes. The dealer hole card is 255 in it until revealed.</item>
/// </list>
/// Events from <see cref="Sit"/> / <see cref="Leave"/> / <see cref="PlaceBet"/> / <see cref="Act"/> wait in a queue and come
/// out of the next <see cref="Tick"/> (or <see cref="TakeEvents"/>); each event is delivered exactly once.
/// </summary>
public sealed class BlackjackTable
{
    public const int SeatCount = 5;
    /// <summary>Phase lengths in seconds (Docs/Casino.md §8.2). A decision gets 25 s with two or more players in the round, 120 s with one.</summary>
    public const double BettingSeconds = 15, InsuranceSeconds = 8, TurnSeconds = 25, SoloTurnSeconds = 120, ResultsSeconds = 5;
    /// <summary>The dealer draws one card per this many seconds; the deal itself goes out one card per <see cref="DealStepSeconds"/>.</summary>
    public const double DealerStepSeconds = 0.8, DealStepSeconds = 0.35;
    /// <summary>The value a face-down card has in every public place.</summary>
    public const byte Hidden = 255;

    sealed class Seat
    {
        public int ownerKey = -1;
        public bool inRound, absent, sitOut, natural, insuranceDecided, settled;
        public long betCents, chargedCents;
        public readonly BlackjackRound mirror = new BlackjackRound();
        public readonly bool[] stood = new bool[BlackjackRules.MaxHands];
        public BlackjackResult result;
        public int lastOwner = -1;
        public long lastNet;
    }

    readonly CasinoSeats seats = new CasinoSeats(SeatCount);
    readonly Seat[] st = new Seat[SeatCount];
    readonly BlackjackShoe shoe = new BlackjackShoe();
    readonly byte[] dealer = new byte[BlackjackRules.MaxHandCards];
    int dealerCount;
    bool holeRevealed;
    BlackjackTablePhase phase;
    double dueAt = double.PositiveInfinity, clock;
    int activeSeat = -1, activeHand, round;
    readonly int[] dealOrder = new int[2 * SeatCount + 2];
    int dealTotal, dealIndex;
    Func<int, int> lastDice;
    List<BlackjackTableEvent> queue = new List<BlackjackTableEvent>(), output = new List<BlackjackTableEvent>();

    public BlackjackTable()
    {
        for (int i = 0; i < SeatCount; i++) st[i] = new Seat();
    }

    // ---------------------------------------------------------------- queries
    public BlackjackTablePhase Phase => phase;
    /// <summary>Who sits where. Read-only use: seat and stand up through <see cref="Sit"/> / <see cref="Leave"/>.</summary>
    public CasinoSeats Seats => seats;
    /// <summary>
    /// HOST ONLY, never sent anywhere: the shoe, for the host's saves and for tests that stack it (set <c>cards</c>, <c>next = 0</c>
    /// and <c>shuffles = 1</c>). Cards are dealt in this order: seat stakes in seat order, the dealer's up card, the seats again, the hole card.
    /// </summary>
    public BlackjackShoe Shoe => shoe;
    public int RoundNumber => round;
    public int ActiveSeat => activeSeat;
    public int ActiveHand => activeHand;
    /// <summary>When the next timer or step is due (+infinity when none). A host that sleeps between ticks can wake then; a simulation jumps its clock here.</summary>
    public double NextDueAt => dueAt;
    /// <summary>The latest time the table has been told.</summary>
    public double Clock => clock;
    public bool HoleHidden => dealerCount >= 2 && !holeRevealed;
    public int DealerCardCount => dealerCount;
    public long RoundsSettled { get; private set; }

    public int SeatOf(int key) => seats.SeatOf(key);
    public int KeyAt(int seat) => seats.KeyAt(seat);
    /// <summary>The key that owns the stake and hands at a seat this round (the sitter, or the player who walked away), or -1.</summary>
    public int OwnerKeyAt(int seat) => seat >= 0 && seat < SeatCount ? st[seat].ownerKey : -1;
    /// <summary>The stake down at a seat in the current round (0 while Idle).</summary>
    public long BetAt(int seat) => seat >= 0 && seat < SeatCount && phase != BlackjackTablePhase.Idle ? st[seat].betCents : 0;
    public bool InRound(int seat) => seat >= 0 && seat < SeatCount && st[seat].inRound;
    public int NearestFreeSeat(float[] distances) => seats.NearestFreeSeat(distances);

    /// <summary>The dealer's cards, the hole card shown as <see cref="Hidden"/> while it is face down.</summary>
    public byte[] DealerCards()
    {
        var r = new byte[dealerCount];
        for (int i = 0; i < dealerCount; i++) r[i] = i == 1 && !holeRevealed ? Hidden : dealer[i];
        return r;
    }

    /// <summary>The actions <paramref name="key"/> may take right now (insurance decisions in Insurance, the active hand's actions on their turn), or None. Ignores the clock.</summary>
    public BlackjackActions LegalActionsFor(int key)
    {
        int s = seats.SeatOf(key);
        if (s < 0 || !Seated(st[s], key)) return BlackjackActions.None;
        if (phase == BlackjackTablePhase.Insurance)
            return st[s].insuranceDecided ? BlackjackActions.None : BlackjackActions.Insurance | BlackjackActions.NoInsurance;
        if (phase == BlackjackTablePhase.Turns && s == activeSeat) return BlackjackRules.LegalActions(st[s].mirror);
        return BlackjackActions.None;
    }

    /// <summary>What <paramref name="action"/> would cost <paramref name="key"/> now (the host's charge), or -1 when it is not legal for them now.</summary>
    public long CostOf(int key, BlackjackAction action)
    {
        if (((int)LegalActionsFor(key) & 1 << (int)action) == 0) return -1;
        return BlackjackRules.ActionCostCents(st[seats.SeatOf(key)].mirror, action);
    }

    /// <summary>Basic strategy for <paramref name="key"/>'s decision right now (NoInsurance in the insurance window). False when they have no decision.</summary>
    public bool TryAdvice(int key, out BlackjackAction action)
    {
        action = BlackjackAction.Stand;
        var legal = LegalActionsFor(key);
        if (legal == BlackjackActions.None) return false;
        if (phase == BlackjackTablePhase.Insurance) { action = BlackjackAction.NoInsurance; return true; }
        action = BlackjackRules.BasicStrategy(st[seats.SeatOf(key)].mirror);
        return true;
    }

    static bool Seated(Seat seat, int key) => seat.inRound && !seat.absent && seat.ownerKey == key;

    // ---------------------------------------------------------------- events
    void Emit(BlackjackTableEvent e) => queue.Add(e);

    /// <summary>Everything queued since the last drain, then the queue is empty. The list is reused: copy what you keep.</summary>
    public List<BlackjackTableEvent> TakeEvents()
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

    void ChangePhase(BlackjackTablePhase next, double at, double deadline)
    {
        phase = next;
        dueAt = deadline;
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.PhaseChanged, phase = next, time = at, seat = -1, deadline = deadline });
    }

    // ---------------------------------------------------------------- seating
    /// <summary>
    /// Takes a seat: <paramref name="preferredSeat"/> if it is free, otherwise the lowest free one (pick one with
    /// <see cref="NearestFreeSeat"/>). Returns the seat or -1 (full / already seated). A player who sits mid-round waits for the next Betting phase.
    /// </summary>
    public int Sit(int key, int preferredSeat, double now)
    {
        return TrySit(key, preferredSeat, now, out int seat) == CasinoTableError.Ok ? seat : -1;
    }

    public CasinoTableError TrySit(int key, int preferredSeat, double now, out int seat)
    {
        now = Now(now);
        var err = seats.TrySit(key, preferredSeat, out seat);
        if (err == CasinoTableError.Ok)
        {
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.Seated, time = now, seat = seat, key = key });
            EvaluateBetting(now);
        }
        return err;
    }

    /// <summary>
    /// Stands <paramref name="key"/> up. Before the deal a stake is refunded (a <see cref="BlackjackTableEventKind.BetRefunded"/>
    /// event). After the deal the hands stay: insurance is declined, every hand stands, and the round settles normally to
    /// this key (<see cref="BlackjackTableEventKind.SeatSettled"/> with <c>flag</c> set). False when they were not seated.
    /// </summary>
    public bool Leave(int key, double now)
    {
        now = Now(now);
        int s = seats.SeatOf(key);
        if (s < 0) return false;
        var seat = st[s];
        bool keeps = false;
        if (seat.inRound && seat.ownerKey == key && !seat.settled && phase >= BlackjackTablePhase.Deal && phase < BlackjackTablePhase.Results)
        {
            keeps = true;
            seat.absent = true;
            seat.insuranceDecided = true;
        }
        else if (phase == BlackjackTablePhase.Betting && seat.betCents > 0 && seat.ownerKey == key)
        {
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.BetRefunded, time = now, seat = s, key = key, cents = seat.chargedCents });
            seat.betCents = 0; seat.chargedCents = 0; seat.ownerKey = -1; seat.sitOut = false;
        }
        seats.Leave(key);
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.SeatLeft, time = now, seat = s, key = key, flag = keeps });
        if (keeps) AfterAbsence(s, now);
        else EvaluateBetting(now);
        return true;
    }

    /// <summary>A player with a hand in play walked away: settle their decisions for them.</summary>
    void AfterAbsence(int s, double now)
    {
        if (phase == BlackjackTablePhase.Insurance)
        {
            EvaluateInsurance(now);
            return;
        }
        if (phase != BlackjackTablePhase.Turns) return; // the Deal and Dealer phases pick it up when they reach it
        bool wasActive = s == activeSeat;
        AutoPlayAbsent(now);
        if (wasActive) AfterSeatAction(s, now);
    }

    // ---------------------------------------------------------------- betting
    /// <summary>Why <paramref name="cents"/> is not a legal opening stake (whole dollars, $5 to $200), or Ok.</summary>
    public static CasinoTableError CheckStake(long cents)
    {
        if (cents < CasinoRules.BlackjackMinCents) return CasinoTableError.BelowMinimum;
        if (cents > CasinoRules.BlackjackMaxCents) return CasinoTableError.AboveMaximum;
        if (cents % CasinoRules.ChipCents != 0) return CasinoTableError.NotWholeDollars;
        return CasinoTableError.Ok;
    }

    /// <summary>
    /// Puts <paramref name="cents"/> down for <paramref name="key"/>'s seat. The first stake opens Betting. Validation order:
    /// seated, phase (Idle or Betting), deadline, limits, already bet, then <paramref name="charge"/>(cents), which must return
    /// true (false = <see cref="CasinoTableError.CantAfford"/>, nothing changed). One stake per seat per round.
    /// </summary>
    public CasinoTableError PlaceBet(int key, long cents, double now, Func<long, bool> charge)
    {
        now = Now(now);
        int s = seats.SeatOf(key);
        if (s < 0) return CasinoTableError.NotSeated;
        if (phase != BlackjackTablePhase.Idle && phase != BlackjackTablePhase.Betting) return CasinoTableError.WrongPhase;
        if (phase == BlackjackTablePhase.Betting && now > dueAt) return CasinoTableError.TooLate;
        var limit = CheckStake(cents);
        if (limit != CasinoTableError.Ok) return limit;
        if (phase == BlackjackTablePhase.Betting && st[s].betCents > 0) return CasinoTableError.AlreadyBet;
        if (charge == null || !charge(cents)) return CasinoTableError.CantAfford;

        if (phase == BlackjackTablePhase.Idle)
        {
            ClearRound();
            ChangePhase(BlackjackTablePhase.Betting, now, now + BettingSeconds);
        }
        var seat = st[s];
        seat.betCents = cents; seat.chargedCents = cents; seat.ownerKey = key; seat.sitOut = false;
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.BetPlaced, time = now, seat = s, key = key, cents = cents });
        EvaluateBetting(now);
        return CasinoTableError.Ok;
    }

    /// <summary>Sits this round out (Betting only, before a stake is down) so the others need not wait for the clock.</summary>
    public CasinoTableError SitOut(int key, double now)
    {
        now = Now(now);
        int s = seats.SeatOf(key);
        if (s < 0) return CasinoTableError.NotSeated;
        if (phase != BlackjackTablePhase.Betting) return CasinoTableError.WrongPhase;
        if (now > dueAt) return CasinoTableError.TooLate;
        if (st[s].betCents > 0) return CasinoTableError.AlreadyBet;
        st[s].sitOut = true;
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.SatOut, time = now, seat = s, key = key });
        EvaluateBetting(now);
        return CasinoTableError.Ok;
    }

    /// <summary>Betting ends early when every sitter has a stake down or sat out; with no stake left at all it goes back to Idle.</summary>
    void EvaluateBetting(double now)
    {
        if (phase != BlackjackTablePhase.Betting) return;
        bool any = false, all = true;
        for (int s = 0; s < SeatCount; s++)
        {
            if (st[s].betCents > 0) { any = true; continue; }
            if (!seats.IsFree(s) && !st[s].sitOut) all = false;
        }
        if (!any) ChangePhase(BlackjackTablePhase.Idle, now, double.PositiveInfinity);
        else if (all && dueAt > now) dueAt = now;
    }

    void ClearRound()
    {
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            seat.ownerKey = -1;
            seat.inRound = seat.absent = seat.sitOut = seat.natural = seat.insuranceDecided = seat.settled = false;
            seat.betCents = seat.chargedCents = 0;
            seat.result = default;
            Array.Clear(seat.stood, 0, seat.stood.Length);
            var m = seat.mirror;
            m.phase = BlackjackPhase.Idle;
            m.handCount = 0; m.active = 0; m.dealerCount = 0; m.baseStakeCents = 0; m.insuranceCents = 0; m.holeRevealed = false;
            for (int h = 0; h < BlackjackRules.MaxHands; h++)
            {
                var hand = m.hands[h];
                hand.count = 0; hand.stakeCents = 0; hand.doubled = hand.fromSplit = hand.splitAces = hand.done = false;
            }
        }
        dealerCount = 0;
        holeRevealed = false;
        activeSeat = -1; activeHand = 0;
        dealTotal = dealIndex = 0;
    }

    // ---------------------------------------------------------------- acting
    static bool Pay(Func<long, bool> charge, long cents)
    {
        if (cents <= 0) return true;
        return charge != null && charge(cents);
    }

    /// <summary>
    /// A decision. In Insurance: <see cref="BlackjackAction.Insurance"/> (half the stake) or <see cref="BlackjackAction.NoInsurance"/>.
    /// In Turns, on the seat's turn: Hit, Stand, Double, Split. A double, a split or insurance costs
    /// <see cref="BlackjackRules.ActionCostCents"/>; the table calls <paramref name="charge"/> with that amount once, after every
    /// check passed, and only a true answer lets the action happen (false = <see cref="CasinoTableError.CantAfford"/>, nothing
    /// changed). <see cref="CostOf"/> tells a screen the price beforehand.
    /// </summary>
    public CasinoTableError Act(int key, BlackjackAction action, double now, Func<long, bool> charge)
    {
        now = Now(now);
        int s = seats.SeatOf(key);
        if (s < 0) return CasinoTableError.NotSeated;
        var seat = st[s];
        if (!Seated(seat, key)) return CasinoTableError.NotInRound;
        var m = seat.mirror;

        if (phase == BlackjackTablePhase.Insurance)
        {
            if (action != BlackjackAction.Insurance && action != BlackjackAction.NoInsurance) return CasinoTableError.IllegalAction;
            if (seat.insuranceDecided) return CasinoTableError.IllegalAction;
            if (now > dueAt) return CasinoTableError.TooLate;
            if (action == BlackjackAction.NoInsurance)
            {
                seat.insuranceDecided = true;
                Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.InsuranceDeclined, time = now, seat = s, key = key });
            }
            else
            {
                long cost = BlackjackRules.ActionCostCents(m, BlackjackAction.Insurance);
                if (!Pay(charge, cost)) return CasinoTableError.CantAfford;
                m.insuranceCents = cost;
                seat.chargedCents += cost;
                seat.insuranceDecided = true;
                Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.InsuranceTaken, time = now, seat = s, key = key, cents = cost });
            }
            EvaluateInsurance(now);
            return CasinoTableError.Ok;
        }

        if (phase != BlackjackTablePhase.Turns) return CasinoTableError.WrongPhase;
        if (s != activeSeat) return CasinoTableError.NotYourTurn;
        if (now > dueAt) return CasinoTableError.TooLate;
        if (!BlackjackRules.IsLegal(m, action)) return CasinoTableError.IllegalAction;

        long price = BlackjackRules.ActionCostCents(m, action);
        if (!Pay(charge, price)) return CasinoTableError.CantAfford;
        seat.chargedCents += price;
        int h = m.active;
        EnsureShoe(now, 3);
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.PlayerAction, time = now, seat = s, hand = h, key = key, action = action, cents = price });
        if (action == BlackjackAction.Stand) seat.stood[h] = true;
        if (!BlackjackRules.Apply(m, shoe, action)) throw new InvalidOperationException("a legal action was refused: " + action);
        switch (action)
        {
            case BlackjackAction.Hit:
            case BlackjackAction.Double:
                DealtToSeat(s, h, m.hands[h].cards[m.hands[h].count - 1], now);
                break;
            case BlackjackAction.Split:
                DealtToSeat(s, h, m.hands[h].cards[1], now);
                DealtToSeat(s, h + 1, m.hands[h + 1].cards[1], now);
                break;
        }
        AfterSeatAction(s, now);
        return CasinoTableError.Ok;
    }

    void DealtToSeat(int s, int hand, byte card, double at) =>
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.CardDealt, time = at, seat = s, hand = hand, key = st[s].ownerKey, card = card });

    /// <summary>After a seat acted (or timed out, or walked away): ask it again, ask the next seat, or hand over to the dealer.</summary>
    void AfterSeatAction(int s, double at)
    {
        if (st[s].mirror.phase == BlackjackPhase.Player) { StartTurn(s, at); return; }
        int next = NextTurnSeat(s + 1);
        if (next >= 0) StartTurn(next, at);
        else EnterDealer(at);
    }

    int NextTurnSeat(int from)
    {
        for (int s = from; s < SeatCount; s++)
            if (st[s].inRound && !st[s].absent && st[s].mirror.phase == BlackjackPhase.Player) return s;
        return -1;
    }

    void StartTurn(int s, double at)
    {
        int present = 0;
        for (int i = 0; i < SeatCount; i++) if (st[i].inRound && !st[i].absent) present++;
        activeSeat = s;
        activeHand = st[s].mirror.active;
        dueAt = at + (present >= 2 ? TurnSeconds : SoloTurnSeconds);
        if (phase != BlackjackTablePhase.Turns) ChangePhase(BlackjackTablePhase.Turns, at, dueAt);
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.TurnStarted, phase = BlackjackTablePhase.Turns, time = at, seat = s, hand = activeHand, key = st[s].ownerKey, deadline = dueAt });
    }

    /// <summary>Stands every remaining hand of a seat (a timeout, or a player who left).</summary>
    void StandAll(int s)
    {
        var seat = st[s];
        var m = seat.mirror;
        for (int guard = 0; guard < 8 && m.phase == BlackjackPhase.Player; guard++)
        {
            seat.stood[m.active] = true;
            if (!BlackjackRules.Apply(m, shoe, BlackjackAction.Stand)) break;
        }
    }

    void AutoPlayAbsent(double at)
    {
        for (int s = 0; s < SeatCount; s++)
            if (st[s].inRound && st[s].absent && st[s].mirror.phase == BlackjackPhase.Player) StandAll(s);
    }

    // ---------------------------------------------------------------- time
    /// <summary>
    /// Advances the table to <paramref name="now"/>: every timer that ran out and every dealing or dealer step that is due, in
    /// order, each stamped with the time it was due. <paramref name="dice"/> is only used to shuffle (the first deal, and after
    /// the cut card): the host dice, <c>Func&lt;int,int&gt;(n) → uniform 0..n-1</c>. Returns every event queued since the last drain.
    /// The list is reused by the next call.
    /// </summary>
    public List<BlackjackTableEvent> Tick(double now, Func<int, int> dice)
    {
        now = Now(now);
        if (dice != null) lastDice = dice;
        for (int guard = 0; guard < 4096 && phase != BlackjackTablePhase.Idle && dueAt <= now; guard++) AdvanceOne(dueAt);
        return TakeEvents();
    }

    /// <summary>
    /// Runs the next pending step right now even if it is not due yet (the dealing of one card, the dealer's one reveal or draw,
    /// a phase's end) and returns the events. For hosts that pace the dealer themselves and for tests.
    /// </summary>
    public List<BlackjackTableEvent> Step(double now, Func<int, int> dice)
    {
        now = Now(now);
        if (dice != null) lastDice = dice;
        if (phase != BlackjackTablePhase.Idle) AdvanceOne(Math.Min(dueAt, now));
        return TakeEvents();
    }

    void AdvanceOne(double at)
    {
        switch (phase)
        {
            case BlackjackTablePhase.Betting: StartDeal(at); break;
            case BlackjackTablePhase.Deal: DealStep(at); break;
            case BlackjackTablePhase.Insurance: CloseInsurance(at); break;
            case BlackjackTablePhase.Turns: TimeOutTurn(at); break;
            case BlackjackTablePhase.Dealer: DealerStep(at); break;
            case BlackjackTablePhase.Results: EndRound(at); break;
        }
    }

    /// <summary>The results are over: remember them for the Idle snapshot and sweep the felt.</summary>
    void EndRound(double at)
    {
        for (int s = 0; s < SeatCount; s++)
        {
            st[s].lastOwner = st[s].settled ? st[s].ownerKey : -1;
            st[s].lastNet = st[s].settled ? st[s].result.totalNetCents : 0;
        }
        ClearRound();
        ChangePhase(BlackjackTablePhase.Idle, at, double.PositiveInfinity);
    }

    // ---------------------------------------------------------------- the deal
    void StartDeal(double at)
    {
        dealTotal = 0;
        for (int s = 0; s < SeatCount; s++) if (st[s].betCents > 0) dealOrder[dealTotal++] = s;
        if (dealTotal == 0) { ChangePhase(BlackjackTablePhase.Idle, at, double.PositiveInfinity); return; }
        int players = dealTotal;
        if (shoe.NeedsShuffle)
        {
            if (lastDice == null) throw new InvalidOperationException("the shoe needs a shuffle and no dice were passed to Tick");
            BlackjackRules.Shuffle(shoe, lastDice);
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.Reshuffled, time = at, seat = -1 });
        }
        // The order cards go out: every stake, the dealer's up card, every stake again, the hole card.
        for (int i = 0; i < players; i++) dealOrder[players + 1 + i] = dealOrder[i];
        dealOrder[players] = -1;
        dealOrder[2 * players + 1] = -1;
        dealTotal = 2 * players + 2;
        dealIndex = 0;
        round++;
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            if (seat.betCents <= 0) continue;
            seat.inRound = true;
            var m = seat.mirror;
            m.handCount = 1; m.active = 0; m.baseStakeCents = seat.betCents; m.insuranceCents = 0;
            m.hands[0].stakeCents = seat.betCents;
            m.phase = BlackjackPhase.Idle;
        }
        ChangePhase(BlackjackTablePhase.Deal, at, at);
    }

    void DealStep(double at)
    {
        if (dealIndex >= dealTotal) { FinishDeal(at); return; }
        EnsureShoe(at, 1);
        int target = dealOrder[dealIndex++];
        byte card = BlackjackRules.Draw(shoe);
        if (target >= 0)
        {
            var hand = st[target].mirror.hands[0];
            hand.cards[hand.count++] = card;
            DealtToSeat(target, 0, card, at);
        }
        else
        {
            dealer[dealerCount++] = card;
            bool down = dealerCount == 2;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.CardDealt, time = at, seat = -1, hand = 0, card = down ? Hidden : card, faceDown = down });
        }
        dueAt = at + DealStepSeconds;
    }

    void FinishDeal(double at)
    {
        bool anyPresent = false;
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            if (!seat.inRound) continue;
            var m = seat.mirror;
            // The seat's private round must never draw dealer cards: its dealer stands on anything (up card + two tens).
            m.dealer[0] = dealer[0]; m.dealer[1] = 9; m.dealer[2] = 9; m.dealerCount = 3; m.holeRevealed = false;
            seat.natural = BlackjackRules.IsBlackjack(m.hands[0]);
            if (seat.natural) { m.hands[0].done = true; m.phase = BlackjackPhase.Settled; }
            else { m.phase = BlackjackPhase.Player; m.active = 0; }
            if (seat.absent) seat.insuranceDecided = true;
            else anyPresent = true;
        }
        if (BlackjackRules.IsAce(dealer[0]) && anyPresent)
        {
            ChangePhase(BlackjackTablePhase.Insurance, at, at + InsuranceSeconds);
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.InsuranceOpened, phase = BlackjackTablePhase.Insurance, time = at, seat = -1, deadline = dueAt });
            EvaluateInsurance(at);
        }
        else Peek(at);
    }

    // ---------------------------------------------------------------- insurance and the peek
    /// <summary>Insurance closes early once everyone present has decided.</summary>
    void EvaluateInsurance(double now)
    {
        if (phase != BlackjackTablePhase.Insurance) return;
        for (int s = 0; s < SeatCount; s++)
            if (st[s].inRound && !st[s].insuranceDecided) return;
        if (dueAt > now) dueAt = now;
    }

    void CloseInsurance(double at)
    {
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            if (!seat.inRound || seat.insuranceDecided) continue;
            seat.insuranceDecided = true;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.InsuranceDeclined, time = at, seat = s, key = seat.ownerKey });
        }
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.InsuranceClosed, phase = BlackjackTablePhase.Insurance, time = at, seat = -1 });
        Peek(at);
    }

    /// <summary>The dealer looks under an ace or a ten. A blackjack ends the round; otherwise the players act.</summary>
    void Peek(double at)
    {
        bool needs = BlackjackRules.IsAce(dealer[0]) || BlackjackRules.CardValue(dealer[0]) == 10;
        bool natural = needs && BlackjackRules.IsNatural(dealer, 2);
        if (needs) Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.Peek, time = at, seat = -1, flag = natural });
        if (natural)
        {
            holeRevealed = true;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.HoleRevealed, time = at, seat = -1, card = dealer[1] });
            for (int s = 0; s < SeatCount; s++)
            {
                if (!st[s].inRound) continue;
                var m = st[s].mirror;
                for (int h = 0; h < m.handCount; h++) m.hands[h].done = true;
                m.phase = BlackjackPhase.Settled;
            }
            EnterDealer(at);
            return;
        }
        AutoPlayAbsent(at);
        int first = NextTurnSeat(0);
        if (first < 0) { EnterDealer(at); return; }
        StartTurn(first, at);
    }

    void TimeOutTurn(double at)
    {
        int s = activeSeat;
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.TurnTimedOut, time = at, seat = s, hand = activeHand, key = st[s].ownerKey });
        StandAll(s);
        AfterSeatAction(s, at);
    }

    // ---------------------------------------------------------------- the dealer
    void EnterDealer(double at)
    {
        activeSeat = -1; activeHand = 0;
        ChangePhase(BlackjackTablePhase.Dealer, at, at + DealerStepSeconds);
    }

    /// <summary>The dealer draws only against a hand that is neither bust nor a natural (a natural wins whatever the dealer draws).</summary>
    bool DealerNeedsToDraw()
    {
        bool live = false;
        for (int s = 0; s < SeatCount && !live; s++)
        {
            if (!st[s].inRound) continue;
            var m = st[s].mirror;
            for (int h = 0; h < m.handCount; h++)
                if (BlackjackRules.HandValue(m.hands[h]) <= 21 && !BlackjackRules.IsBlackjack(m.hands[h])) live = true;
        }
        return live && BlackjackRules.DealerMustHit(dealer, dealerCount);
    }

    void DealerStep(double at)
    {
        if (!holeRevealed)
        {
            holeRevealed = true;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.HoleRevealed, time = at, seat = -1, card = dealer[1] });
            dueAt = at + DealerStepSeconds;
        }
        else if (DealerNeedsToDraw())
        {
            EnsureShoe(at, 1);
            byte card = BlackjackRules.Draw(shoe);
            dealer[dealerCount++] = card;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.DealerCard, time = at, seat = -1, card = card });
            dueAt = at + DealerStepSeconds;
        }
        else Settle(at);
    }

    /// <summary>Settles every seat with <see cref="BlackjackRules.Settle"/> on the real dealer hand, so each pays exactly as the single-player table does.</summary>
    void Settle(double at)
    {
        long sum = 0;
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            if (!seat.inRound) continue;
            var m = seat.mirror;
            Array.Copy(dealer, m.dealer, dealerCount);
            m.dealerCount = dealerCount;
            m.holeRevealed = true;
            m.phase = BlackjackPhase.Settled;
            var r = BlackjackRules.Settle(m);
            seat.result = r;
            seat.settled = true;
            // What goes back is what the rules say for what was actually charged (they always agree).
            long back = r.stakedCents == seat.chargedCents ? r.returnCents : Math.Max(0, seat.chargedCents + r.totalNetCents);
            sum += r.totalNetCents;
            Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.SeatSettled, time = at, seat = s, key = seat.ownerKey, cents = back, flag = seat.absent, result = r });
        }
        RoundsSettled++;
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.RoundSettled, time = at, seat = -1, cents = sum });
        ChangePhase(BlackjackTablePhase.Results, at, at + ResultsSeconds);
    }

    /// <summary>
    /// A fresh shoe when fewer than <paramref name="need"/> cards remain (a round can only get there in a pathological pile of
    /// splits; the cut card normally leaves 78 behind it).
    /// </summary>
    void EnsureShoe(double at, int need)
    {
        if (shoe.Remaining >= need) return;
        if (lastDice == null) throw new InvalidOperationException("the shoe ran out and no dice were ever passed to Tick");
        BlackjackRules.Shuffle(shoe, lastDice);
        Emit(new BlackjackTableEvent { kind = BlackjackTableEventKind.Reshuffled, time = at, seat = -1, flag = true });
    }

    /// <summary>
    /// The session or the table is closing: everyone stands up (stakes before the deal are refunded) and a round in play runs to
    /// its settlement at once. Returns the events; credit them like any others. Afterwards the table is Idle.
    /// </summary>
    public List<BlackjackTableEvent> Close(double now, Func<int, int> dice)
    {
        now = Now(now);
        if (dice != null) lastDice = dice;
        foreach (int key in seats.Occupants()) Leave(key, now);
        for (int guard = 0; guard < 4096 && phase != BlackjackTablePhase.Idle; guard++) AdvanceOne(Math.Min(dueAt, now));
        return TakeEvents();
    }

    // ---------------------------------------------------------------- the public picture
    /// <summary>The public state, safe to send to every machine. Contains no shoe card and not the hole card while it is face down.</summary>
    public BlackjackTableSnapshot Snapshot()
    {
        var snap = new BlackjackTableSnapshot
        {
            now = clock, phase = phase, phaseDeadline = dueAt, round = round,
            shoeLeft = shoe.Remaining, activeSeat = phase == BlackjackTablePhase.Turns ? activeSeat : -1,
            activeHand = phase == BlackjackTablePhase.Turns ? activeHand : 0,
            dealer = DealerCards(), seats = new BlackjackSeatSnapshot[SeatCount],
        };
        for (int s = 0; s < SeatCount; s++)
        {
            var seat = st[s];
            var o = new BlackjackSeatSnapshot { key = seats.KeyAt(s), ownerKey = seat.ownerKey, betCents = (int)seat.betCents, lastKey = seat.lastOwner, lastNetCents = (int)seat.lastNet };
            byte flags = 0;
            if (seat.inRound) flags |= BlackjackSeatSnapshot.FlagInRound;
            if (seat.absent) flags |= BlackjackSeatSnapshot.FlagAbsent;
            if (seat.sitOut) flags |= BlackjackSeatSnapshot.FlagSitOut;
            if (seat.natural) flags |= BlackjackSeatSnapshot.FlagNatural;
            if (seat.insuranceDecided) flags |= BlackjackSeatSnapshot.FlagInsuranceDecided;
            if (seat.settled) flags |= BlackjackSeatSnapshot.FlagSettled;
            if (phase == BlackjackTablePhase.Turns && s == activeSeat) flags |= BlackjackSeatSnapshot.FlagActing;
            o.flags = flags;
            var m = seat.mirror;
            o.insuranceCents = (int)m.insuranceCents;
            int handCount = seat.inRound ? m.handCount : 0;
            o.hands = new BlackjackHandSnapshot[handCount];
            for (int h = 0; h < handCount; h++)
            {
                var hand = m.hands[h];
                var cards = new byte[hand.count];
                Array.Copy(hand.cards, cards, hand.count);
                byte hf = 0;
                if (hand.doubled) hf |= BlackjackHandSnapshot.FlagDoubled;
                if (hand.fromSplit) hf |= BlackjackHandSnapshot.FlagFromSplit;
                if (hand.splitAces) hf |= BlackjackHandSnapshot.FlagSplitAces;
                if (hand.done) hf |= BlackjackHandSnapshot.FlagDone;
                if (seat.stood[h]) hf |= BlackjackHandSnapshot.FlagStood;
                if (BlackjackRules.HandValue(hand) > 21) hf |= BlackjackHandSnapshot.FlagBusted;
                if (BlackjackRules.IsBlackjack(hand)) hf |= BlackjackHandSnapshot.FlagBlackjack;
                if (phase == BlackjackTablePhase.Turns && s == activeSeat && h == activeHand) hf |= BlackjackHandSnapshot.FlagActive;
                var oh = new BlackjackHandSnapshot { cards = cards, stakeCents = (int)hand.stakeCents, flags = hf };
                if (seat.settled)
                {
                    oh.outcome = seat.result.Outcome(h);
                    oh.netCents = (int)seat.result.HandNet(h);
                }
                o.hands[h] = oh;
            }
            if (seat.settled)
            {
                o.insuranceNetCents = (int)seat.result.insuranceNetCents;
                o.totalNetCents = (int)seat.result.totalNetCents;
            }
            snap.seats[s] = o;
        }
        return snap;
    }
}

/// <summary>What <see cref="BlackjackTableSim.Chaos"/> found. A clean run has <see cref="violations"/> == 0 and <see cref="Balanced"/>.</summary>
[Serializable]
public struct BlackjackTableAudit
{
    public long rounds, seatRounds, chargedCents, creditedCents, refundedCents, settledNetCents, settledStakedCents;
    public long leaves, absentSettles, timeouts, refusedCharges, refunds, snapshots, shuffles;
    public long violations;
    public string firstViolation;

    /// <summary>The books balance: what the host took minus what it paid back is exactly minus the sum of the settled nets.</summary>
    public bool Balanced => chargedCents - creditedCents == -settledNetCents;
}

/// <summary>
/// Drives a <see cref="BlackjackTable"/> the way a host would, with a virtual clock, to measure it. For the odds test and for
/// the conservation test; allocates little per round.
/// </summary>
public static class BlackjackTableSim
{
    /// <summary>
    /// Plays <paramref name="seatRounds"/> seat-rounds of basic strategy at a flat stake with <paramref name="seats"/> players at
    /// one table and one continuous shoe, through the real phases, timers and settlement. The house edge is in the result.
    /// </summary>
    public static BlackjackSimStats BasicStrategy(int seats, long seatRounds, long stakeCents, Func<int, int> dice)
    {
        var stats = new BlackjackSimStats();
        if (!CasinoRules.IsLegalBlackjackStake(stakeCents) || seats < 1 || seats > BlackjackTable.SeatCount) return stats;
        var table = new BlackjackTable();
        for (int k = 0; k < seats; k++) table.Sit(k, k, 0);
        Func<long, bool> pay = c => true;
        double now = 0;
        while (stats.rounds < seatRounds)
        {
            for (int k = 0; k < seats; k++)
            {
                switch (table.Phase)
                {
                    case BlackjackTablePhase.Idle:
                    case BlackjackTablePhase.Betting:
                        if (table.BetAt(k) == 0) table.PlaceBet(k, stakeCents, now, pay);
                        break;
                    case BlackjackTablePhase.Insurance:
                        table.Act(k, BlackjackAction.NoInsurance, now, pay);
                        break;
                    case BlackjackTablePhase.Turns:
                        if (table.ActiveSeat == k && table.TryAdvice(k, out var a)) table.Act(k, a, now, pay);
                        break;
                }
            }
            var events = table.Tick(now, dice);
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.kind == BlackjackTableEventKind.PlayerAction)
                {
                    if (e.action == BlackjackAction.Double) stats.doubles++;
                    else if (e.action == BlackjackAction.Split) stats.splits++;
                }
                else if (e.kind == BlackjackTableEventKind.SeatSettled)
                {
                    var r = e.result;
                    stats.rounds++;
                    stats.hands += r.handCount;
                    stats.initialStakeCents += stakeCents;
                    stats.wageredCents += r.stakedCents;
                    stats.netCents += r.totalNetCents;
                    for (int h = 0; h < r.handCount; h++)
                    {
                        var o = r.Outcome(h);
                        if (o == BlackjackOutcome.Blackjack) stats.blackjacks++;
                        else if (o == BlackjackOutcome.Bust) stats.busts++;
                        else if (o == BlackjackOutcome.Push) stats.pushes++;
                    }
                }
            }
            if (events.Count == 0)
            {
                double due = table.NextDueAt;
                if (double.IsInfinity(due)) break;
                now = Math.Max(now, due);
            }
        }
        stats.shuffles = table.Shoe.shuffles;
        return stats;
    }

    /// <summary>
    /// Plays <paramref name="rounds"/> rounds with up to five players who sit down, sit out, bet, play (a mix of basic strategy
    /// and random legal actions), time out, walk away mid-hand, and reconnect, while the host's charge sometimes refuses.
    /// Everything the table asks for money, and everything it hands back through events, goes through a till that
    /// <see cref="BlackjackTableAudit"/> adds up. <paramref name="chaos"/> decides the players' behaviour (separate from the table dice).
    /// </summary>
    public static BlackjackTableAudit Chaos(long rounds, Func<int, int> dice, Func<int, int> chaos)
    {
        var audit = new BlackjackTableAudit();
        var table = new BlackjackTable();
        double now = 0;
        int nextKey = 1000;
        bool refuse = false;
        Func<long, bool> till = c =>
        {
            if (refuse && chaos(25) == 0) { audit.refusedCharges++; return false; }
            audit.chargedCents += c;
            return true;
        };
        long steps = 0;
        var seen = new HashSet<int>();

        while (audit.rounds < rounds && steps++ < rounds * 400)
        {
            // players come and go
            for (int seat = 0; seat < BlackjackTable.SeatCount; seat++)
            {
                int k = table.KeyAt(seat);
                if (k < 0 && chaos(6) == 0)
                {
                    int key = chaos(8) == 0 && seen.Count > 0 ? PickOld(seen, chaos) : nextKey++;
                    seen.Add(key);
                    if (table.Seats.Contains(key)) continue;
                    table.Sit(key, seat, now);
                }
                else if (k >= 0 && chaos(60) == 0) { table.Leave(k, now); audit.leaves++; }
            }

            // everyone seated decides
            for (int seat = 0; seat < BlackjackTable.SeatCount; seat++)
            {
                int k = table.KeyAt(seat);
                if (k < 0) continue;
                refuse = true;
                switch (table.Phase)
                {
                    case BlackjackTablePhase.Idle:
                    case BlackjackTablePhase.Betting:
                        if (table.BetAt(seat) == 0 && chaos(4) != 0)
                        {
                            if (chaos(10) == 0) table.SitOut(k, now);
                            else table.PlaceBet(k, (5 + chaos(40)) * 100L, now, till);
                        }
                        break;
                    case BlackjackTablePhase.Insurance:
                        if (chaos(3) != 0) table.Act(k, chaos(3) == 0 ? BlackjackAction.Insurance : BlackjackAction.NoInsurance, now, till);
                        break;
                    case BlackjackTablePhase.Turns:
                        if (table.ActiveSeat != seat) break;
                        int roll = chaos(100);
                        if (roll < 3) { table.Leave(k, now); audit.leaves++; }
                        else if (roll < 8) { /* freeze: let the clock run it out */ }
                        else
                        {
                            BlackjackAction a;
                            if (roll < 60 && table.TryAdvice(k, out var adv)) a = adv;
                            else a = (BlackjackAction)chaos(4);
                            table.Act(k, a, now, till);
                        }
                        break;
                }
                refuse = false;
            }

            var events = table.Tick(now, dice);
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                switch (e.kind)
                {
                    case BlackjackTableEventKind.BetRefunded:
                        audit.creditedCents += e.cents; audit.refundedCents += e.cents; audit.refunds++;
                        break;
                    case BlackjackTableEventKind.SeatSettled:
                        audit.creditedCents += e.cents;
                        audit.settledNetCents += e.result.totalNetCents;
                        audit.settledStakedCents += e.result.stakedCents;
                        audit.seatRounds++;
                        if (e.flag) audit.absentSettles++;
                        if (e.cents != e.result.stakedCents + e.result.totalNetCents || e.cents < 0) Violation(ref audit, "seat return " + e.cents + " != staked + net");
                        break;
                    case BlackjackTableEventKind.RoundSettled: audit.rounds++; break;
                    case BlackjackTableEventKind.TurnTimedOut: audit.timeouts++; break;
                    case BlackjackTableEventKind.Reshuffled: audit.shuffles++; break;
                }
            }

            // invariants on what everyone may see
            var snap = table.Snapshot();
            if (steps % 7 == 0)
            {
                audit.snapshots++;
                var bytes = snap.Serialize();
                if (!BlackjackTableSnapshot.TryDeserialize(bytes, out var back) || !Same(bytes, back.Serialize())) Violation(ref audit, "snapshot round trip");
                if (table.HoleHidden && (snap.dealer.Length < 2 || snap.dealer[1] != BlackjackTable.Hidden)) Violation(ref audit, "hole card visible");
                if (!table.HoleHidden && snap.dealer.Length >= 2 && snap.dealer[1] == BlackjackTable.Hidden) Violation(ref audit, "hole card hidden after the reveal");
            }
            if (table.Phase == BlackjackTablePhase.Idle)
            {
                // Between rounds nothing may be owed: charges minus credits is minus the settled nets.
                if (!audit.Balanced) Violation(ref audit, "books out of balance between rounds");
            }

            if (events.Count == 0)
            {
                double due = table.NextDueAt;
                if (double.IsInfinity(due)) now += 0.5;
                else now = Math.Max(now + 0.05, due);
            }
            else now += 0.05;
        }

        // wind down: close the table, credit what comes out, and the books must balance exactly.
        var closing = table.Close(now, dice);
        for (int i = 0; i < closing.Count; i++)
        {
            var e = closing[i];
            if (e.kind == BlackjackTableEventKind.BetRefunded) { audit.creditedCents += e.cents; audit.refundedCents += e.cents; audit.refunds++; }
            else if (e.kind == BlackjackTableEventKind.SeatSettled)
            {
                audit.creditedCents += e.cents; audit.settledNetCents += e.result.totalNetCents; audit.settledStakedCents += e.result.stakedCents;
                audit.seatRounds++; if (e.flag) audit.absentSettles++;
            }
            else if (e.kind == BlackjackTableEventKind.RoundSettled) audit.rounds++;
        }
        if (table.Phase != BlackjackTablePhase.Idle) Violation(ref audit, "table did not close");
        if (!audit.Balanced) Violation(ref audit, "books out of balance after close: charged " + audit.chargedCents + " credited " + audit.creditedCents + " nets " + audit.settledNetCents);
        return audit;
    }

    static int PickOld(HashSet<int> seen, Func<int, int> chaos)
    {
        int pick = chaos(seen.Count), i = 0;
        foreach (int k in seen) if (i++ == pick) return k;
        return 0;
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Violation(ref BlackjackTableAudit audit, string what)
    {
        audit.violations++;
        if (audit.firstViolation == null) audit.firstViolation = what;
    }
}

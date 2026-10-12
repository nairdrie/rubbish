using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The host side of the shared casino tables (<c>CasinoTables</c>, Docs/Casino.md §10): seats and machines, the money (charge
/// before roll, payouts to the event's key even when the player left), proximity, who may act for whom, the late-join sync, and
/// that no snapshot carries the shoe or the hole card. A fake till, a stacked or seeded shoe and an explicit clock stand in for
/// the Ledger, the host dice and real time. Reflection because the logic lives in Assembly-CSharp.
/// </summary>
public sealed class CasinoTablesTests
{
    // ================================================================================================ reflection plumbing

    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name, params object[] args) => Activator.CreateInstance(T(name), args);
    static object E(string type, string value) => Enum.Parse(T(type), value);

    static object Inv(object target, string method, params object[] args)
    {
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == method && m.GetParameters().Length == args.Length) return m.Invoke(target, args);
        Assert.Fail(target.GetType().Name + "." + method + "/" + args.Length + " not found");
        return null;
    }

    static object Static(string type, string method, params object[] args)
    {
        foreach (var m in T(type).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == method && m.GetParameters().Length == args.Length) return m.Invoke(null, args);
        Assert.Fail(type + "." + method + "/" + args.Length + " not found");
        return null;
    }

    static V G<V>(object target, string name)
    {
        var f = target.GetType().GetField(name);
        if (f != null) return (V)f.GetValue(target);
        var p = target.GetType().GetProperty(name);
        Assert.IsNotNull(p, target.GetType().Name + "." + name + " not found");
        return (V)p.GetValue(target);
    }

    static void Put(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);

    /// <summary>A delegate of any Action type that calls <paramref name="body"/> with its arguments boxed.</summary>
    static Delegate Handler(Type delegateType, Action<object[]> body)
    {
        var invoke = delegateType.GetMethod("Invoke");
        var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var arr = Expression.NewArrayInit(typeof(object), ps.Select(p => (Expression)Expression.Convert(p, typeof(object))));
        var call = Expression.Call(Expression.Constant(body), typeof(Action<object[]>).GetMethod("Invoke"), arr);
        return Expression.Lambda(delegateType, call, ps).Compile();
    }

    /// <summary>A seeded xorshift64* with rejection, so every n is exactly uniform.</summary>
    static Func<int, int> Seeded(ulong seed)
    {
        ulong s = seed * 0x9E3779B97F4A7C15UL + 1UL;
        return n =>
        {
            uint limit = uint.MaxValue - uint.MaxValue % (uint)n;
            while (true)
            {
                s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
                uint r = (uint)(s * 0x2545F4914F6CDD1DUL >> 32);
                if (r < limit) return (int)(r % (uint)n);
            }
        };
    }

    const string Ranks = "A23456789TJQK";
    static byte Card(char rank) => (byte)Ranks.IndexOf(rank);

    const ulong Alice = 1, Bob = 2, Carol = 3, Dan = 4;
    const string Bj = "bj0", Rl = "rl0";

    /// <summary>What a request answered.</summary>
    sealed class Res
    {
        public string Gate, Error, BetError;
        public int Seat;
        public bool Ok => Gate == "Ok" && Error == "Ok";
        public override string ToString() => Gate + "/" + Error + "/" + BetError + " seat=" + Seat;
    }

    static Res R(object r) => new Res
    {
        Gate = G<object>(r, "gate").ToString(), Error = G<object>(r, "error").ToString(),
        BetError = G<object>(r, "betError").ToString(), Seat = G<sbyte>(r, "seat"),
    };

    static object Bet(string type, int index, int cents) =>
        New("RouletteBet", E("RouletteBetType", type), index, cents);

    static Array Bets(params object[] bets)
    {
        var a = Array.CreateInstance(T("RouletteBet"), bets.Length);
        for (int i = 0; i < bets.Length; i++) a.SetValue(bets[i], i);
        return a;
    }

    /// <summary>The casino floor: the tables on a fake till with a clock, scripted dice, and everything they raise recorded.</summary>
    sealed class Floor
    {
        public readonly object Tables = New("CasinoTables");
        public readonly Dictionary<ulong, long> cash = new Dictionary<ulong, long>();
        public readonly Dictionary<ulong, long> start = new Dictionary<ulong, long>();
        public readonly List<string> log = new List<string>();
        public readonly HashSet<ulong> far = new HashSet<ulong>(), away = new HashSet<ulong>();
        public readonly List<string> bigWins = new List<string>();
        public readonly List<object> bjSnaps = new List<object>(), rlSnaps = new List<object>();
        public readonly List<byte[]> bjBytes = new List<byte[]>(), rlBytes = new List<byte[]>();
        public readonly List<object> spins = new List<object>();
        public readonly List<string> occupancy = new List<string>(), notices = new List<string>();
        public Func<int, int> dice = n => throw new InvalidOperationException("no dice expected");
        public int diceCalls;
        public long cashAtFirstRoll = -1;
        public ulong watched;
        public double now = 1000;
        public bool chargeRefuses;

        public Floor(string stack = null)
        {
            Put(Tables, "CashOf", (Func<ulong, long>)(c => cash.TryGetValue(c, out long v) ? v : -1));
            Put(Tables, "Charge", (Func<ulong, long, string, string, bool>)((c, cents, id, why) =>
            {
                if (chargeRefuses || !cash.ContainsKey(c) || cents <= 0) return false;
                cash[c] -= cents;
                log.Add($"-{cents} {id} {c}");
                return true;
            }));
            Put(Tables, "Credit", (Func<ulong, long, string, string, bool>)((c, cents, id, why) =>
            {
                if (!cash.ContainsKey(c) || cents <= 0) return false;
                cash[c] += cents;
                log.Add($"+{cents} {id} {c}");
                return true;
            }));
            Put(Tables, "Dice", (Func<int, int>)(n =>
            {
                if (diceCalls++ == 0 && watched != 0) cashAtFirstRoll = cash[watched];
                return dice(n);
            }));
            Put(Tables, "Near", (Func<ulong, string, bool>)((c, table) => !far.Contains(c)));
            Put(Tables, "Present", (Func<ulong, string, bool>)((c, table) => !away.Contains(c)));
            Put(Tables, "BigWin", (Action<ulong, int, string, long>)((c, game, what, cents) => bigWins.Add($"{c}:{game}:{what}:{cents}")));
            Hook("BlackjackChanged", a => { bjBytes.Add((byte[])a[1]); bjSnaps.Add(a[0]); });
            Hook("RouletteChanged", a => { rlBytes.Add((byte[])a[1]); rlSnaps.Add(a[0]); });
            Hook("SlotSpun", a => spins.Add(a[0]));
            Hook("SlotOccupancyChanged", a => occupancy.Add(a[0] + ":" + a[1]));
            Hook("NoticeSent", a => notices.Add(a[0] + ":" + a[1] + ":" + a[2]));
            if (stack != null) { dice = n => throw new InvalidOperationException("a stacked shoe must not be shuffled"); Stack(stack); }
        }

        void Hook(string ev, Action<object[]> body)
        {
            var e = Tables.GetType().GetEvent(ev);
            e.AddEventHandler(Tables, Handler(e.EventHandlerType, body));
        }

        public void Fund(ulong c, long cents) { cash[c] = cents; start[c] = cents; }

        public object BjTable => G<object>(Tables, "BlackjackTable");
        public object RlTable => G<object>(Tables, "RouletteTable");

        /// <summary>These ranks come out in dealing order (stakes in seat order, dealer up, stakes again, hole, then draws), twos after.</summary>
        public void Stack(string ranks)
        {
            var shoe = G<object>(BjTable, "Shoe");
            var cards = G<byte[]>(shoe, "cards");
            for (int i = 0; i < cards.Length; i++) cards[i] = Card(i < ranks.Length ? ranks[i] : '2');
            Put(shoe, "next", 0);
            Put(shoe, "shuffles", 1);
        }

        public string BjPhase => G<object>(BjTable, "Phase").ToString();
        public string RlPhase => G<object>(RlTable, "Phase").ToString();

        public Res Sit(ulong c, string table, int seat = -1) => R(Inv(Tables, "Sit", c, table, seat, now));
        public Res Leave(ulong c, string table) => R(Inv(Tables, "Leave", c, table, now));
        public Res BjBet(ulong c, long cents) => R(Inv(Tables, "BlackjackBet", c, cents, now));
        public Res BjSitOut(ulong c) => R(Inv(Tables, "BlackjackSitOut", c, now));
        public Res BjAct(ulong c, string action) => R(Inv(Tables, "BlackjackAct", c, E("BlackjackAction", action), now));
        public Res RlJoin(ulong c) => R(Inv(Tables, "RouletteJoin", c, now));
        public Res RlBets(ulong c, params object[] bets) => R(Inv(Tables, "RouletteSetBets", c, Bets(bets), bets.Length, now));
        public Res RlReady(ulong c, bool ready = true) => R(Inv(Tables, "RouletteReady", c, ready, now));
        public Res Spin(ulong c, int machine) => R(Inv(Tables, "SlotSpin", c, machine, now));
        public void Tick(double dt = 0) { now += dt; Inv(Tables, "Tick", now); }
        public void Disconnect(ulong c) { Inv(Tables, "Disconnected", c, now); Inv(Tables, "Flush"); }

        /// <summary>Advance the clock in small steps.</summary>
        public void Run(double seconds, double step = 0.25)
        {
            for (double t = 0; t < seconds; t += step) Tick(step);
        }

        public void RunUntil(Func<bool> done, double max = 600, double step = 0.25)
        {
            for (double t = 0; t < max && !done(); t += step) Tick(step);
            Assert.IsTrue(done(), "condition not reached; blackjack " + BjPhase + ", roulette " + RlPhase);
        }

        public long Charged => G<long>(Tables, "TotalChargedCents");
        public long Credited => G<long>(Tables, "TotalCreditedCents");
        public long Unpaid => G<long>(Tables, "UnpaidCents");
        public int Occupant(int machine) => (int)Inv(Tables, "OccupantOfSlot", machine);
        public int SeatAt(ulong c, string table) => (int)Inv(Tables, "SeatAt", c, table);
        public object LastBj => bjSnaps[bjSnaps.Count - 1];
        public object LastRl => rlSnaps[rlSnaps.Count - 1];
        public object BjSeat(int i) => ((Array)G<object>(LastBj, "seats")).GetValue(i);
        public long SumCash() => cash.Values.Sum();
    }

    static byte[] Dealer(object snap) => G<byte[]>(snap, "dealer");

    // ================================================================================================ seating

    [Test]
    public void BlackjackSeatsAreSharedAndNearestFirst()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        var a = f.Sit(Alice, Bj, 3);
        Assert.IsTrue(a.Ok, a.ToString());
        Assert.AreEqual(3, a.Seat, "the preferred seat when it is free");
        var b = f.Sit(Bob, Bj, 3);
        Assert.IsTrue(b.Ok);
        Assert.AreEqual(0, b.Seat, "the lowest free seat when the preferred one is taken");
        var again = f.Sit(Alice, Bj, 1);
        Assert.AreEqual("AlreadySeated", again.Error);
        Assert.AreEqual(3, again.Seat, "and says where they already sit");
        Assert.AreEqual(3, f.SeatAt(Alice, Bj));
        Assert.AreEqual(-1, f.SeatAt(Carol, Bj));

        f.Tick();
        var seats = (Array)G<object>(f.LastBj, "seats");
        Assert.AreEqual(5, seats.Length);
        Assert.AreEqual((int)Alice, G<int>(seats.GetValue(3), "key"));
        Assert.AreEqual((int)Bob, G<int>(seats.GetValue(0), "key"));
        Assert.AreEqual(-1, G<int>(seats.GetValue(1), "key"));

        // five seats, then "table full"
        f.Fund(10, 1000); f.Fund(11, 1000); f.Fund(12, 1000);
        Assert.IsTrue(f.Sit(Carol, Bj).Ok);
        Assert.IsTrue(f.Sit(10, Bj).Ok);
        Assert.IsTrue(f.Sit(11, Bj).Ok);
        Assert.AreEqual("TableFull", f.Sit(12, Bj).Error);
        Assert.IsTrue(f.Leave(Bob, Bj).Ok);
        Assert.AreEqual("NotSeated", f.Leave(Bob, Bj).Error, "standing up twice");
        var seat = f.Sit(12, Bj);
        Assert.IsTrue(seat.Ok);
        Assert.AreEqual(0, seat.Seat, "the seat Bob freed");
    }

    [Test]
    public void SittingAtOneStationStandsYouUpFromTheOthers()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000);
        Assert.IsTrue(f.Sit(Alice, Bj).Ok);
        Assert.IsTrue(f.Sit(Alice, "slot2").Ok);
        Assert.AreEqual(-1, f.SeatAt(Alice, Bj), "left blackjack");
        Assert.AreEqual((int)Alice, f.Occupant(2));
        Assert.IsTrue(f.Sit(Alice, Rl).Ok);
        Assert.AreEqual(-1, f.Occupant(2), "left the machine");
        Assert.AreEqual(0, f.SeatAt(Alice, Rl));
        Assert.IsTrue(f.Sit(Alice, Bj).Ok);
        Assert.AreEqual(-1, f.SeatAt(Alice, Rl));
    }

    [Test]
    public void UnknownTablesAndMachinesAreRefused()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000);
        Assert.AreEqual("BadMachine", f.Sit(Alice, "bj1").Gate);
        Assert.AreEqual("BadMachine", f.Sit(Alice, "slot5").Gate);
        Assert.AreEqual("BadMachine", f.Sit(Alice, "").Gate);
        Assert.AreEqual("BadMachine", f.Sit(Alice, null).Gate);
        Assert.AreEqual("BadMachine", f.Leave(Alice, "nope").Gate);
        Assert.AreEqual("BadMachine", f.Spin(Alice, 7).Gate);
        Assert.AreEqual("BadMachine", f.Spin(Alice, -1).Gate);
        Assert.AreEqual("NoBusiness", f.Sit(Bob, Bj).Gate, "no business, no seat");
    }

    [Test]
    public void IdsAndWireBytesRoundTrip()
    {
        Assert.AreEqual(0, Static("CasinoTableIds", "ToWire", "bj0"));
        Assert.AreEqual(1, Static("CasinoTableIds", "ToWire", "rl0"));
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(2 + i, Static("CasinoTableIds", "ToWire", "slot" + i));
            Assert.AreEqual("slot" + i, Static("CasinoTableIds", "FromWire", 2 + i));
        }
        Assert.AreEqual(-1, Static("CasinoTableIds", "ToWire", "slot9"));
        Assert.IsNull(Static("CasinoTableIds", "FromWire", 99));
        Assert.IsNull(Static("CasinoTableIds", "FromWire", -1));

        var bets = Bets(Bet("Straight", 17, 1000), Bet("Red", 0, 500), Bet("Corner", 3, 100));
        var raw = (byte[])Static("CasinoWire", "PackBets", bets, 3);
        Assert.AreEqual(18, raw.Length);
        var back = (Array)Static("CasinoWire", "UnpackBets", raw);
        Assert.AreEqual(3, back.Length);
        Assert.AreEqual("Red", G<object>(back.GetValue(1), "type").ToString());
        Assert.AreEqual(500, G<int>(back.GetValue(1), "cents"));
        Assert.IsNull(Static("CasinoWire", "UnpackBets", new byte[7]), "not a whole number of bets");
        Assert.IsNull(Static("CasinoWire", "UnpackBets", new byte[6 * 65]), "more than a spin can carry");
        Assert.AreEqual(0, ((Array)Static("CasinoWire", "UnpackBets", new byte[0])).Length, "an empty packet is an empty set");
    }

    // ================================================================================================ blackjack money

    /// <summary>Alice 18, Bob 16, Carol 14 against a dealer 6-T that draws a ten and busts.</summary>
    const string BustStack = "T98" + "6" + "876" + "T" + "TTTTTTTT";
    /// <summary>The same for two stakes: 18 and 16 against a dealer that busts.</summary>
    const string TwoBust = "T9" + "6" + "87" + "T" + "TTTTTTTT";

    [Test]
    public void ThreePlayers_OneStands_OneLeavesMidHand_OneDisconnects_AllArePaid()
    {
        var f = new Floor(BustStack);
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1); f.Sit(Carol, Bj, 2);
        Assert.IsTrue(f.BjBet(Alice, 1000).Ok);
        Assert.AreEqual("Betting", f.BjPhase);
        Assert.IsTrue(f.BjBet(Bob, 2000).Ok);
        Assert.IsTrue(f.BjBet(Carol, 3000).Ok);
        Assert.AreEqual(100000 - 1000, f.cash[Alice], "charged at the bet");
        Assert.AreEqual(6000, f.Charged);

        f.RunUntil(() => f.BjPhase == "Turns");
        Assert.AreEqual(0, G<int>(f.BjTable, "ActiveSeat"));
        Assert.IsTrue(f.BjAct(Alice, "Stand").Ok);
        // Bob's turn now. Bob walks away mid-hand: the hand stands, plays on and pays him anyway.
        Assert.IsTrue(f.Leave(Bob, Bj).Ok);
        f.Tick();
        var bobSeat = f.BjSeat(1);
        Assert.AreEqual(-1, G<int>(bobSeat, "key"), "the stool is empty");
        Assert.AreEqual((int)Bob, G<int>(bobSeat, "ownerKey"), "but the hand is still his");
        Assert.IsTrue((G<byte>(bobSeat, "flags") & 2) != 0, "and marked absent");
        // Carol drops off the network at her turn
        f.Disconnect(Carol);

        f.RunUntil(() => f.BjPhase == "Idle");
        Assert.AreEqual(100000 + 1000, f.cash[Alice]);
        Assert.AreEqual(100000 + 2000, f.cash[Bob], "paid in absentia");
        Assert.AreEqual(100000 + 3000, f.cash[Carol], "paid to the business of the player who dropped");
        Assert.AreEqual(f.Charged - f.Credited, f.start.Values.Sum() - f.SumCash(), "the till and the books agree");
        Assert.AreEqual(0, f.Unpaid);
    }

    [Test]
    public void WinningsForABusinessThatIsGoneAreCountedNotInvented()
    {
        var f = new Floor(TwoBust);
        f.Fund(Alice, 100000); f.Fund(Carol, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Carol, Bj, 2);
        f.BjBet(Alice, 1000); f.BjBet(Carol, 3000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.cash.Remove(Carol);  // their business left with them
        f.Disconnect(Carol);
        f.BjAct(Alice, "Stand");
        f.RunUntil(() => f.BjPhase == "Idle");
        Assert.AreEqual(100000 + 1000, f.cash[Alice]);
        Assert.AreEqual(6000, f.Unpaid, "Carol's stake and winnings, never silently dropped");
        Assert.AreEqual(6000, (long)Inv(f.Tables, "UnpaidFor", (int)Carol));
        Assert.AreEqual(2000, f.Credited, "only Alice was credited");
    }

    [Test]
    public void LeavingBeforeTheDealRefundsTheStake()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000);
        f.Sit(Alice, Bj); f.Sit(Bob, Bj);
        Assert.IsTrue(f.BjBet(Alice, 5000).Ok);
        Assert.AreEqual(95000, f.cash[Alice]);
        Assert.IsTrue(f.Leave(Alice, Bj).Ok);
        Assert.AreEqual(100000, f.cash[Alice], "refunded at once");
        Assert.AreEqual(5000, f.Credited);
        StringAssert.Contains("+5000 casino_wager", f.log.Last(), "on the wager id, so the day's books net out");
        f.Tick();
        Assert.AreEqual("Idle", f.BjPhase, "nobody has a stake down any more");
    }

    [Test]
    public void TurnsThatTimeOutAreStoodAndSettled()
    {
        // Alice T+T = 20, Bob 9+T = 19, dealer 6+T draws T and busts
        var f = new Floor("T9" + "6" + "TT" + "T" + "TTTT");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1);
        f.BjBet(Alice, 2000); f.BjBet(Bob, 2000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.Run(130);
        Assert.AreEqual("Idle", f.BjPhase);
        CollectionAssert.Contains(f.notices, Alice + ":bj0:TurnTimedOut");
        CollectionAssert.Contains(f.notices, Bob + ":bj0:TurnTimedOut");
        Assert.AreEqual(102000, f.cash[Alice]);
        Assert.AreEqual(102000, f.cash[Bob]);
    }

    [Test]
    public void Refusals_ChargeNothingAndChangeNothing()
    {
        var f = new Floor(TwoBust);
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 300);
        // not seated
        Assert.AreEqual("NotSeated", f.BjBet(Bob, 1000).Error);
        Assert.AreEqual("NotSeated", f.BjAct(Bob, "Hit").Error);
        Assert.AreEqual("NotSeated", f.BjSitOut(Bob).Error);
        // out of range
        f.Sit(Alice, Bj, 0);
        f.far.Add(Bob);
        Assert.AreEqual("TooFar", f.Sit(Bob, Bj).Gate);
        Assert.AreEqual("TooFar", f.Sit(Bob, "slot1").Gate);
        Assert.AreEqual("TooFar", f.Sit(Bob, Rl).Gate);
        f.far.Remove(Bob);
        f.Sit(Bob, Bj, 1);
        f.far.Add(Bob);
        Assert.AreEqual("TooFar", f.BjBet(Bob, 1000).Gate, "a stake needs the player at the table");
        Assert.AreEqual(100000, f.cash[Bob]);
        f.far.Remove(Bob);
        // limits
        Assert.AreEqual("BelowMinimum", f.BjBet(Alice, 400).Error);
        Assert.AreEqual("AboveMaximum", f.BjBet(Alice, 20100).Error);
        Assert.AreEqual("NotWholeDollars", f.BjBet(Alice, 1050).Error);
        Assert.AreEqual(100000, f.cash[Alice]);
        // can't afford
        f.Sit(Carol, Bj, 2);
        Assert.AreEqual("CantAfford", f.BjBet(Carol, 500).Error);
        Assert.AreEqual(300, f.cash[Carol]);
        Assert.AreEqual("Idle", f.BjPhase, "an unaffordable first bet doesn't open the betting phase");
        Assert.AreEqual(0, f.Charged);
        // wrong phase and not in the round
        Assert.AreEqual("NotInRound", f.BjAct(Alice, "Hit").Error, "no hand yet");
        Assert.IsTrue(f.BjBet(Alice, 1000).Ok);
        Assert.AreEqual("AlreadyBet", f.BjBet(Alice, 1000).Error);
        Assert.AreEqual("NotInRound", f.BjAct(Alice, "Hit").Error, "dealing hasn't started");
        Assert.IsTrue(f.BjBet(Bob, 1000).Ok);
        f.BjSitOut(Carol);
        f.RunUntil(() => f.BjPhase == "Turns");
        Assert.AreEqual("WrongPhase", f.BjBet(Carol, 1000).Error);
        Assert.AreEqual("NotInRound", f.BjAct(Carol, "Hit").Error, "Carol sat the round out");
        Assert.AreEqual("NotYourTurn", f.BjAct(Bob, "Hit").Error, "it is Alice's turn: Bob can't act for her or ahead of her");
        Assert.AreEqual("IllegalAction", f.BjAct(Alice, "Insurance").Error);
        Assert.AreEqual("IllegalAction", f.BjAct(Alice, "Split").Error, "T and 8 are not a pair");
        var bad = R(Inv(f.Tables, "BlackjackAct", Alice, Enum.ToObject(T("BlackjackAction"), 9), f.now));
        Assert.AreEqual("IllegalAction", bad.Error, "an action byte that isn't one");
        // a double needs cash: Alice has exactly her 18 and 99.00 left, so make her poor
        f.cash[Alice] = 0;
        Assert.AreEqual("CantAfford", f.BjAct(Alice, "Double").Error);
        Assert.AreEqual(0, f.cash[Alice]);
        Assert.IsTrue(f.BjAct(Alice, "Stand").Ok, "and the hand is still hers to play");
    }

    [Test]
    public void NobodyCanActForSomebodyElsesSeat()
    {
        var f = new Floor("T9" + "6" + "TT" + "T" + "TTTT");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1);
        f.BjBet(Alice, 2000); f.BjBet(Bob, 2000);
        f.RunUntil(() => f.BjPhase == "Turns");
        // Carol (not seated) and Bob (not his turn) both try to act for Alice
        Assert.AreEqual("NotSeated", f.BjAct(Carol, "Hit").Error);
        Assert.AreEqual("NotYourTurn", f.BjAct(Bob, "Hit").Error);
        Assert.AreEqual("NotSeated", f.Leave(Carol, Bj).Error, "Carol can't stand Alice up");
        Assert.AreEqual(0, f.SeatAt(Alice, Bj));
        f.Tick();
        var hand = ((Array)G<object>(f.BjSeat(0), "hands")).GetValue(0);
        Assert.AreEqual(2, G<byte[]>(hand, "cards").Length, "Alice's hand didn't change");
        // roulette: Carol can't touch Bob's bets
        f.RlJoin(Bob);
        f.RlBets(Bob, Bet("Red", 0, 500));
        Assert.AreEqual("NotSeated", f.RlBets(Carol, Bet("Black", 0, 500)).Error);
        Assert.AreEqual("NotSeated", f.RlReady(Carol).Error);
        var parts = (Array)G<object>(Inv(f.RlTable, "Snapshot"), "participants");
        Assert.AreEqual(1, parts.Length);
        Assert.AreEqual(1, G<Array>(parts.GetValue(0), "bets").Length, "Bob's set is untouched");
        Assert.AreEqual("Red", G<object>(G<Array>(parts.GetValue(0), "bets").GetValue(0), "type").ToString());
        // a slot machine: only its occupant can pull it
        f.Sit(Alice, "slot1");
        Assert.AreEqual("Occupied", f.Spin(Carol, 1).Error);
        Assert.AreEqual("NotSeated", f.Spin(Carol, 2).Error);
    }

    [Test]
    public void DoublesAreChargedBeforeTheCardAndInsuranceBeforeThePeek()
    {
        // Alice 5+6 = 11, dealer A up (insurance), hole 9: Alice doubles and gets a ten
        var f = new Floor("5" + "A" + "6" + "9" + "T" + "TTTT");
        f.Fund(Alice, 100000);
        f.Sit(Alice, Bj, 0);
        f.BjBet(Alice, 2000);
        f.RunUntil(() => f.BjPhase == "Insurance");
        long before = f.cash[Alice];
        Assert.IsTrue(f.BjAct(Alice, "Insurance").Ok);
        Assert.AreEqual(before - 1000, f.cash[Alice], "half the stake, charged when asked");
        f.RunUntil(() => f.BjPhase == "Turns");
        Assert.IsTrue(f.BjAct(Alice, "Double").Ok);
        Assert.AreEqual(before - 1000 - 2000, f.cash[Alice]);
        f.RunUntil(() => f.BjPhase == "Idle");
        // 11 + T = 21 doubled (+4000 net) vs dealer A+9 = soft 20: win, insurance lost
        Assert.AreEqual(100000 + 4000 - 1000, f.cash[Alice]);
    }

    [Test]
    public void NoSnapshotCarriesTheHoleCardOrTheShoe()
    {
        // dealer up 6, hole K; every card still in the shoe is a queen (11)
        var f = new Floor("T9" + "6" + "87" + "K" + "QQQQQQQQQQQQQQQQQQQQQQQQQQQQ");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1);
        f.BjBet(Alice, 1000); f.BjBet(Bob, 1000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.BjAct(Alice, "Stand");
        f.Tick();
        int hiddenSeen = 0, turnsSeen = 0;
        for (int i = 0; i < f.bjSnaps.Count; i++)
        {
            var snap = f.bjSnaps[i];
            string phase = G<object>(snap, "phase").ToString();
            var dealer = Dealer(snap);
            if (phase == "Turns") turnsSeen++;
            if (dealer.Length >= 2 && (phase == "Deal" || phase == "Insurance" || phase == "Turns"))
            {
                Assert.AreEqual(255, dealer[1], "the hole card is face down for everybody in " + phase);
                hiddenSeen++;
            }
            if (phase != "Dealer" && phase != "Results" && phase != "Idle")
                Assert.IsFalse(dealer.Contains((byte)12), "no king in the dealer's public cards before the reveal");
            foreach (object seat in (Array)G<object>(snap, "seats"))
                foreach (object hand in (Array)G<object>(seat, "hands"))
                    Assert.IsFalse(G<byte[]>(hand, "cards").Contains((byte)11), "no queen (undealt) in anybody's hand");
            // the wire form is the same picture
            var back = Static("BlackjackTableSnapshot", "Deserialize", f.bjBytes[i]);
            Assert.AreEqual(dealer, Dealer(back));
        }
        Assert.Greater(hiddenSeen, 2);
        Assert.Greater(turnsSeen, 0);
        f.RunUntil(() => f.BjPhase == "Dealer");
        f.Tick(); f.Tick(1);
        Assert.IsFalse(Dealer(f.LastBj).Contains((byte)255), "turned over by the dealer, then it is public");
        // the roulette snapshot has no hidden state at all, but no pocket before the lock
        f.RlJoin(Alice);
        f.RlBets(Alice, Bet("Red", 0, 500));
        f.Tick();
        Assert.AreEqual(255, G<byte>(f.LastRl, "pocket"), "no pocket while bets are open");
    }

    // ================================================================================================ roulette

    [Test]
    public void SharedWheel_ChargesAtTheLock_CancelsWhatCannotBePaid_AndPaysThoseWhoLeft()
    {
        var f = new Floor();
        f.dice = n => 17; // the ball lands on 17 (black)
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 300);
        Assert.AreEqual(0, f.RlJoin(Alice).Seat);
        Assert.AreEqual(1, f.RlJoin(Bob).Seat);
        Assert.AreEqual(2, f.RlJoin(Carol).Seat, "a chip colour each");
        Assert.IsTrue(f.RlBets(Alice, Bet("Straight", 17, 1000)).Ok);
        Assert.IsTrue(f.RlBets(Bob, Bet("Red", 0, 500)).Ok);
        Assert.IsTrue(f.RlBets(Carol, Bet("Straight", 5, 500)).Ok, "bets are only a promise until the lock");
        Assert.AreEqual(100000, f.cash[Alice], "nothing is charged while betting");
        Assert.AreEqual(0, f.Charged);
        // changing a set replaces it
        Assert.IsTrue(f.RlBets(Bob, Bet("Red", 0, 500), Bet("Even", 0, 500)).Ok);
        Assert.IsTrue(f.RlBets(Bob, Bet("Black", 0, 500)).Ok);
        Assert.IsTrue(f.RlReady(Alice).Ok); Assert.IsTrue(f.RlReady(Bob).Ok); Assert.IsTrue(f.RlReady(Carol).Ok);
        f.Tick(0.1);
        Assert.AreEqual("Spinning", f.RlPhase);
        Assert.AreEqual(100000 - 1000, f.cash[Alice]);
        Assert.AreEqual(100000 - 500, f.cash[Bob]);
        Assert.AreEqual(300, f.cash[Carol], "her set was cancelled, nothing charged");
        CollectionAssert.Contains(f.notices, Carol + ":rl0:SetCancelled");
        Assert.AreEqual(1000 + 500, f.Charged);
        Assert.AreEqual(17, G<byte>(f.LastRl, "pocket"));

        // Alice walks away while the ball is rolling: her straight-up still pays
        Assert.IsTrue(f.Leave(Alice, Rl).Ok);
        f.RunUntil(() => f.RlPhase == "Idle");
        Assert.AreEqual(100000 - 1000 + 36000, f.cash[Alice], "35:1 plus the stake, paid in absentia");
        Assert.AreEqual(100000 - 500 + 1000, f.cash[Bob], "black wins even money");
        Assert.AreEqual(300, f.cash[Carol]);
        Assert.AreEqual(f.Charged - f.Credited, f.start.Values.Sum() - f.SumCash());
        Assert.AreEqual(0, f.Unpaid);
        Assert.AreEqual(1, G<byte[]>(f.LastRl, "history").Length);
    }

    [Test]
    public void WheelRefusals_AndLimits()
    {
        var f = new Floor();
        f.dice = n => 1;
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000); f.Fund(Dan, 100000); f.Fund(5, 100000);
        Assert.AreEqual("NotSeated", f.RlBets(Alice, Bet("Red", 0, 500)).Error, "join first");
        f.far.Add(Alice);
        Assert.AreEqual("TooFar", f.RlJoin(Alice).Gate);
        f.far.Remove(Alice);
        f.RlJoin(Alice); f.RlJoin(Bob); f.RlJoin(Carol); f.RlJoin(Dan);
        Assert.AreEqual("TableFull", f.RlJoin(5).Error, "four players at the wheel");
        var bad = f.RlBets(Alice, Bet("Straight", 17, 150));
        Assert.AreEqual("BadBets", bad.Error);
        Assert.AreEqual("BadAmount", bad.BetError);
        Assert.AreEqual("AboveSpotMaximum", f.RlBets(Alice, Bet("Straight", 17, 10100)).BetError);
        Assert.AreEqual("BadSpot", f.RlBets(Alice, Bet("Straight", 99, 500)).BetError);
        Assert.AreEqual("Idle", f.RlPhase, "refused sets don't open the betting phase");
        f.far.Add(Alice);
        Assert.AreEqual("TooFar", f.RlBets(Alice, Bet("Red", 0, 500)).Gate);
        f.far.Remove(Alice);
        Assert.IsTrue(f.RlBets(Alice, Bet("Red", 0, 500)).Ok);
        Assert.AreEqual("Betting", f.RlPhase);
        Assert.AreEqual(0, f.Charged);
        // leaving during betting drops the set; nothing was charged
        Assert.IsTrue(f.Leave(Alice, Rl).Ok);
        f.Tick();
        Assert.AreEqual("Idle", f.RlPhase);
        Assert.AreEqual(100000, f.cash[Alice]);
    }

    [Test]
    public void RouletteBigWin_IsAThousandDollarsNet()
    {
        var f = new Floor();
        f.dice = n => 17;
        f.Fund(Alice, 1000000); f.Fund(Bob, 1000000);
        f.RlJoin(Alice); f.RlJoin(Bob);
        f.RlBets(Alice, Bet("Straight", 17, 10000)); // $100 straight: 35:1 = $3,500
        f.RlBets(Bob, Bet("Straight", 17, 1000));    // $10 straight: $350
        f.RlReady(Alice); f.RlReady(Bob);
        f.Run(30);
        Assert.AreEqual(1, f.bigWins.Count, string.Join(" | ", f.bigWins));
        StringAssert.StartsWith(Alice + ":" + Convert.ToInt32(Enum.Parse(T("CasinoGame"), "Roulette")) + ":17 at roulette:350000", f.bigWins[0]);
    }

    // ================================================================================================ slot machines

    [Test]
    public void SlotMachines_HaveOneOccupant_AndFreeThemselvesOnLeaveDisconnectIdleAndRelease()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        Assert.IsTrue(f.Sit(Alice, "slot2").Ok);
        Assert.AreEqual((int)Alice, f.Occupant(2));
        Assert.AreEqual("Occupied", f.Sit(Bob, "slot2").Error, "single occupancy");
        Assert.IsTrue(f.Sit(Bob, "slot3").Ok);
        Assert.IsTrue(f.Sit(Alice, "slot2").Ok, "sitting again confirms the machine and is not a change");
        Assert.AreEqual("NotSeated", f.Leave(Bob, "slot2").Error, "Bob can't free Alice's machine");
        Assert.AreEqual((int)Alice, f.Occupant(2));
        f.Tick();
        CollectionAssert.AreEqual(new[] { "2:" + (int)Alice, "3:" + (int)Bob }, f.occupancy, "occupancy changes are raised");

        Assert.IsTrue(f.Leave(Alice, "slot2").Ok);
        Assert.AreEqual(-1, f.Occupant(2));
        Assert.IsTrue(f.Sit(Bob, "slot2").Ok, "free again (and Bob moves over)");
        Assert.AreEqual(-1, f.Occupant(3));

        // disconnect frees it
        f.Disconnect(Bob);
        Assert.AreEqual(-1, f.Occupant(2));

        // idle release: 180 s without a spin
        f.Sit(Carol, "slot4");
        f.Run(170);
        Assert.AreEqual((int)Carol, f.Occupant(4));
        f.Run(20);
        Assert.AreEqual(-1, f.Occupant(4), "freed after 180 s idle");
        CollectionAssert.Contains(f.notices, Carol + ":slot4:IdleRelease");
        Assert.AreEqual("-1", f.occupancy.Last().Split(':')[1]);

        // a spin keeps it alive; the presence sweep frees it when the body is gone
        f.dice = n => 0;
        f.Sit(Carol, "slot0");
        f.Run(150);
        Assert.IsTrue(f.Spin(Carol, 0).Ok);
        f.Run(150);
        Assert.AreEqual((int)Carol, f.Occupant(0), "the spin reset the idle clock");
        f.away.Add(Carol);
        f.Run(3);
        Assert.AreEqual(-1, f.Occupant(0), "stood up once their body was elsewhere");
        CollectionAssert.Contains(f.notices, Carol + ":slot0:Released");
    }

    [Test]
    public void SlotSpin_ChargesFirst_RollsHostDice_AndBroadcastsTheRealStops()
    {
        var f = new Floor("");
        f.Fund(Alice, 10000000);
        f.watched = Alice;
        var dice = Seeded(77);
        f.dice = dice;
        var expectDice = Seeded(77);
        Assert.IsTrue(f.Sit(Alice, "slot1").Ok);
        long won = 0, staked = 0;
        for (int i = 0; i < 300; i++)
        {
            f.Tick(1.1);
            var expected = Static("SlotRules", "Spin", 1, expectDice);
            long before = f.cash[Alice];
            var r = f.Spin(Alice, 1);
            Assert.IsTrue(r.Ok, r.ToString());
            f.Tick();
            var e = f.spins[f.spins.Count - 1];
            Assert.AreEqual(G<byte>(expected, "stop0"), G<byte>(e, "stop0"));
            Assert.AreEqual(G<byte>(expected, "stop1"), G<byte>(e, "stop1"));
            Assert.AreEqual(G<byte>(expected, "stop2"), G<byte>(e, "stop2"));
            Assert.AreEqual(G<sbyte>(expected, "rule"), G<sbyte>(e, "rule"));
            Assert.AreEqual(G<int>(expected, "winCents"), G<int>(e, "winCents"));
            Assert.AreEqual(100, G<int>(e, "stakeCents"));
            Assert.AreEqual((int)Alice, G<int>(e, "key"));
            Assert.AreEqual(f.now, G<double>(e, "start"), 1e-9, "the table time the reels start");
            Assert.AreEqual(before - 100 + G<int>(expected, "winCents"), f.cash[Alice]);
            staked += 100; won += G<int>(expected, "winCents");
        }
        Assert.AreEqual(300, f.spins.Count, "every spin is raised once");
        Assert.AreEqual(10000000 - staked + won, f.cash[Alice]);
        Assert.AreEqual(10000000 - 100, f.cashAtFirstRoll, "the stake was charged before the first die was rolled");
    }

    [Test]
    public void SlotSpin_Refusals()
    {
        var f = new Floor("");
        f.dice = n => 0;
        f.Fund(Alice, 100000); f.Fund(Bob, 100); f.Fund(Carol, 100000);
        Assert.AreEqual("NotSeated", f.Spin(Alice, 0).Error, "sit first");
        f.Sit(Alice, "slot0");
        Assert.AreEqual("Occupied", f.Spin(Carol, 0).Error);
        Assert.AreEqual(0, f.Charged);
        f.far.Add(Alice);
        Assert.AreEqual("TooFar", f.Spin(Alice, 0).Gate);
        f.far.Remove(Alice);
        Assert.IsTrue(f.Spin(Alice, 0).Ok);
        Assert.AreEqual("Throttled", f.Spin(Alice, 0).Gate, "the host's own floor: 1 s between pulls");
        f.Tick(0.5);
        Assert.AreEqual("Throttled", f.Spin(Alice, 0).Gate);
        f.Tick(0.6);
        Assert.IsTrue(f.Spin(Alice, 0).Ok);
        // poor Bob at the $5 machine
        f.Sit(Bob, "slot2");
        long chargedBefore = f.Charged;
        Assert.AreEqual("CantAfford", f.Spin(Bob, 2).Error);
        Assert.AreEqual(100, f.cash[Bob]);
        Assert.AreEqual(chargedBefore, f.Charged);
        Assert.AreEqual(2, f.diceCalls / 3, "no die was rolled for the refused spins");
    }

    [Test]
    public void BigWin_IsAnnouncedAtAThousandDollarsOrFiftyTimesTheStake()
    {
        var f = new Floor("");
        f.Fund(Alice, 100000000);
        int bigs = 0;
        for (int machine = 0; machine < 5; machine++)
        {
            f.bigWins.Clear();
            var machineObj = Static("SlotRules", "Machine", machine);
            byte top = (byte)Static("SlotRules", "TopSymbol", machine);
            var reels = G<byte[][]>(machineObj, "reels");
            int[] stops = reels.Select(r => Array.IndexOf(r, top)).ToArray();
            int i = 0;
            f.dice = n => stops[i++ % 3];
            f.Sit(Alice, "slot" + machine);
            f.Tick(2);
            Assert.IsTrue(f.Spin(Alice, machine).Ok);
            f.Tick();
            var e = f.spins.Last();
            int win = G<int>(e, "winCents"), stake = G<int>(e, "stakeCents");
            Assert.Greater(win, 0, "the top prize pays on machine " + machine);
            bool big = win >= 100000 || win >= 50L * stake;
            Assert.AreEqual(big, G<bool>(e, "big"), "machine " + machine + " win " + win);
            Assert.AreEqual(big ? 1 : 0, f.bigWins.Count, "machine " + machine);
            if (big) bigs++;
        }
        Assert.Greater(bigs, 0, "at least one jackpot is a big win");
        // a small or missing win is never announced
        foreach (int[] stops in new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } })
        {
            f.bigWins.Clear();
            int k = 0;
            f.dice = n => stops[k++ % 3];
            f.Sit(Alice, "slot0");
            f.Tick(2);
            f.Spin(Alice, 0);
            f.Tick();
            int win = G<int>(f.spins.Last(), "winCents");
            Assert.AreEqual(win >= 100000 || win >= 5000 ? 1 : 0, f.bigWins.Count, "win " + win);
        }
    }

    // ================================================================================================ presence and the session

    [Test]
    public void APlayerWhoseBodyLeavesIsStoodUpAndTold_TheirHandStillPlays()
    {
        var f = new Floor(BustStack);
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1); f.Sit(Carol, Bj, 2);
        f.BjBet(Alice, 1000); f.BjBet(Bob, 1000); f.BjBet(Carol, 1000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.away.Add(Bob); // teleported, carried off or passed out
        f.Run(1.0);
        Assert.AreEqual(1, f.SeatAt(Bob, Bj), "within the grace period nothing happens");
        f.Run(2.0);
        Assert.AreEqual(-1, f.SeatAt(Bob, Bj), "stood up");
        CollectionAssert.Contains(f.notices, Bob + ":bj0:Released");
        f.BjAct(Alice, "Stand");
        f.RunUntil(() => f.BjPhase == "Idle");
        Assert.AreEqual(101000, f.cash[Bob], "and his hand was played out and paid");
        // someone who comes back may sit again
        f.away.Remove(Bob);
        Assert.IsTrue(f.Sit(Bob, Bj).Ok);
    }

    [Test]
    public void ClosingTheSessionSettlesEverythingAndFreesEverySeat()
    {
        var f = new Floor(TwoBust);
        f.dice = n => 5;
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000); f.Fund(Dan, 5000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1);
        f.BjBet(Alice, 1000); f.BjBet(Bob, 2000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.RlJoin(Carol);
        f.RlBets(Carol, Bet("Straight", 5, 1000));
        f.Sit(Dan, "slot1");
        Inv(f.Tables, "Close", f.now);
        Assert.AreEqual("Idle", f.BjPhase);
        Assert.AreEqual("Idle", f.RlPhase);
        Assert.AreEqual(-1, f.Occupant(1));
        Assert.AreEqual(0, G<int>(G<object>(f.BjTable, "Seats"), "Count"));
        Assert.AreEqual(100000, f.cash[Carol], "a set that never locked was never charged");
        Assert.AreEqual(f.Charged - f.Credited, f.start.Values.Sum() - f.SumCash());
        Assert.Greater(f.cash[Alice], 100000, "the hand in play was settled and paid (the dealer busts)");
    }

    // ================================================================================================ late join

    [Test]
    public void SyncReturnsBothSnapshotsAndTheMachineOccupants()
    {
        var f = new Floor(TwoBust);
        f.Fund(Alice, 100000); f.Fund(Bob, 100000); f.Fund(Carol, 100000);
        f.Sit(Alice, Bj, 0); f.Sit(Bob, Bj, 1);
        f.BjBet(Alice, 1000); f.BjBet(Bob, 2000);
        f.RunUntil(() => f.BjPhase == "Turns");
        f.dice = n => 3;
        f.RlJoin(Carol);
        f.RlBets(Carol, Bet("Red", 0, 500), Bet("Straight", 7, 100));
        f.Fund(Dan, 1000);
        f.Sit(Dan, "slot3");
        f.Tick();

        var sync = Inv(f.Tables, "Sync", f.now);
        var bj = Static("BlackjackTableSnapshot", "Deserialize", G<byte[]>(sync, "blackjack"));
        var rl = Static("RouletteTableSnapshot", "Deserialize", G<byte[]>(sync, "roulette"));
        var occ = G<int[]>(sync, "slotOccupants");
        Assert.AreEqual("Turns", G<object>(bj, "phase").ToString());
        Assert.AreEqual(5, ((Array)G<object>(bj, "seats")).Length);
        var seat0 = ((Array)G<object>(bj, "seats")).GetValue(0);
        Assert.AreEqual((int)Alice, G<int>(seat0, "key"));
        Assert.AreEqual(1000, G<int>(seat0, "betCents"));
        Assert.AreEqual(255, Dealer(bj)[1], "the hole card is hidden in the late-join picture too");
        Assert.AreEqual("Betting", G<object>(rl, "phase").ToString());
        var parts = (Array)G<object>(rl, "participants");
        Assert.AreEqual(1, parts.Length);
        Assert.AreEqual((int)Carol, G<int>(parts.GetValue(0), "key"));
        Assert.AreEqual(2, G<Array>(parts.GetValue(0), "bets").Length);
        CollectionAssert.AreEqual(new[] { -1, -1, -1, (int)Dan, -1 }, occ);
        Assert.Less(G<byte[]>(sync, "blackjack").Length, 1200, "a snapshot is small");
    }

    // ================================================================================================ the books, end to end

    /// <summary>Four clients doing everything at once for a long while, including walking away, vanishing and not being able to
    /// afford things. The money each player ends with must equal what the PUBLIC pictures (settled snapshots, slot events) say
    /// they won and lost: that is exactly the information a spectator has.</summary>
    [Test]
    public void ManyClientsAtOnce_EveryCentIsAccountedForFromTheSnapshots()
    {
        var rng = Seeded(20261009);
        var f = new Floor();
        f.dice = Seeded(424242);
        ulong[] players = { Alice, Bob, Carol, Dan };
        var expected = new Dictionary<ulong, long>();
        var pay = new Dictionary<ulong, long>();
        foreach (var p in players) { f.Fund(p, p == Dan ? 60000 : 5_000_000); expected[p] = 0; pay[p] = 0; }
        string[] tables = { Bj, Bj, Bj, Rl, Rl, Rl, Rl, "slot0", "slot1", "slot2", "slot3", "slot4" };
        string[] actions = { "Hit", "Stand", "Double", "Split", "Insurance", "NoInsurance" };
        string[] types = { "Straight", "Split", "Street", "Corner", "SixLine", "TopLine", "Dozen", "Column", "Red", "Black", "Odd", "Even", "Low", "High" };
        long lastBjRound = -1, lastRlRound = -1, bjSettled = 0, rlSettled = 0, slotSpins = 0, refused = 0, absentees = 0;

        void Account()
        {
            for (int i = 0; i < f.bjSnaps.Count; i++)
            {
                var s = f.bjSnaps[i];
                if (G<object>(s, "phase").ToString() != "Results" || G<int>(s, "round") == lastBjRound) continue;
                lastBjRound = G<int>(s, "round");
                foreach (object seat in (Array)G<object>(s, "seats"))
                {
                    if ((G<byte>(seat, "flags") & 32) == 0) continue;
                    expected[(ulong)G<int>(seat, "ownerKey")] += G<int>(seat, "totalNetCents");
                    bjSettled++;
                    if ((G<byte>(seat, "flags") & 2) != 0) absentees++;
                }
            }
            f.bjSnaps.Clear(); f.bjBytes.Clear();
            for (int i = 0; i < f.rlSnaps.Count; i++)
            {
                var s = f.rlSnaps[i];
                if (G<object>(s, "phase").ToString() != "Result" || G<int>(s, "round") == lastRlRound) continue;
                lastRlRound = G<int>(s, "round");
                foreach (object part in (Array)G<object>(s, "participants"))
                {
                    if ((G<byte>(part, "flags") & 4) == 0) continue;
                    expected[(ulong)G<int>(part, "key")] += G<int>(part, "returnCents") - G<int>(part, "stakedCents");
                    rlSettled++;
                }
            }
            f.rlSnaps.Clear(); f.rlBytes.Clear();
            foreach (object e in f.spins)
            {
                expected[(ulong)G<int>(e, "key")] += G<int>(e, "winCents") - G<int>(e, "stakeCents");
                slotSpins++;
            }
            f.spins.Clear();
        }

        for (int step = 0; step < 40000; step++)
        {
            foreach (var p in players)
            {
                int r = rng(100);
                Res res = null;
                if (r < 3) res = f.Sit(p, tables[rng(tables.Length)], rng(6) - 1);
                else if (r < 4) res = f.Leave(p, tables[rng(tables.Length)]);
                else if (r < 12) res = f.BjBet(p, rng(8) == 0 ? 1050 : 100 * (5 + rng(40)));
                else if (r < 28) res = f.BjAct(p, actions[rng(6)]);
                else if (r < 31) res = f.BjSitOut(p);
                else if (r < 38)
                {
                    int n = rng(5);
                    var list = new object[n];
                    for (int i = 0; i < n; i++)
                    {
                        string type = types[rng(types.Length)];
                        bool inside = rng(2) == 0;
                        list[i] = Bet(inside ? "Straight" : type, inside ? rng(40) : rng(3), rng(15) == 0 ? 130 : 100 * (1 + rng(30)));
                    }
                    res = f.RlBets(p, list);
                }
                else if (r < 41) res = f.RlReady(p, rng(4) != 0);
                else if (r < 52) res = f.Spin(p, rng(5));
                else if (r < 53) { f.Disconnect(p); }
                else if (r < 55) { if (f.away.Contains(p)) f.away.Remove(p); else f.away.Add(p); }
                else if (r < 56) { if (f.far.Contains(p)) f.far.Remove(p); else f.far.Add(p); }
                else if (r < 58) { f.cash[p] += 10000; pay[p] += 10000; } // a payday, so Dan isn't broke all the time
                if (res != null && !res.Ok) refused++;
            }
            f.Tick(0.25);
            Account();
        }

        // everybody goes home and the tables run out
        foreach (var p in players) f.Disconnect(p);
        for (int i = 0; i < 4000 && (f.BjPhase != "Idle" || f.RlPhase != "Idle"); i++) { f.Tick(0.25); Account(); }
        Assert.AreEqual("Idle", f.BjPhase);
        Assert.AreEqual("Idle", f.RlPhase);
        f.Tick(0.25);
        Account();

        foreach (var p in players)
        {
            Assert.AreEqual(f.start[p] + pay[p] + expected[p], f.cash[p], "player " + p + ": the till against the public pictures");
            Assert.GreaterOrEqual(f.cash[p], 0, "nobody is ever charged below zero");
        }
        Assert.AreEqual(0, f.Unpaid, "every business was still there");
        Assert.AreEqual(f.Charged - f.Credited, f.start.Values.Sum() + pay.Values.Sum() - f.SumCash(), "the bank's books against the till");
        Assert.Greater(bjSettled, 40, "blackjack rounds were played (" + bjSettled + ")");
        Assert.Greater(rlSettled, 25, "roulette spins were played (" + rlSettled + ")");
        Assert.Greater(slotSpins, 100, "slots were pulled (" + slotSpins + ")");
        Assert.Greater(refused, 1000, "and plenty was refused");
        Assert.Greater(absentees, 0, "some hands were played out for players who had left");
    }
}

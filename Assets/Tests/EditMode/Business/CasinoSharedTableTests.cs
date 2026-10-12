using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The shared casino tables (Docs/Casino.md §8): seats, the five-seat blackjack table, the shared roulette wheel and their public
/// snapshots. Every table runs on an explicit clock and a seeded or stacked shoe. Reflection because the rules live in Assembly-CSharp.
/// </summary>
public sealed class CasinoSharedTableTests
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

    /// <summary>A seeded xorshift64* with rejection, so every n is exactly uniform.</summary>
    static Func<int, int> Dice(ulong seed)
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

    static readonly Func<int, int> noDice = n => throw new InvalidOperationException("a stacked shoe must not be shuffled");

    const string Ranks = "A23456789TJQK";
    static byte Card(char rank) => (byte)Ranks.IndexOf(rank);

    sealed class Ev
    {
        public string Kind, Phase, Action;
        public double Time, Deadline;
        public int Seat, Hand, Key, Colour;
        public byte Card, Pocket;
        public bool FaceDown, Flag;
        public long Cents, Net;
        public object Result;
        public override string ToString() => Kind + "@" + Time.ToString("0.###") + " seat=" + Seat + " hand=" + Hand + " key=" + Key + " card=" + Card + " cents=" + Cents;
    }

    static List<Ev> Blackjack(object list)
    {
        var r = new List<Ev>();
        foreach (object e in (IEnumerable)list)
            r.Add(new Ev
            {
                Kind = G<object>(e, "kind").ToString(), Phase = G<object>(e, "phase").ToString(), Action = G<object>(e, "action").ToString(),
                Time = G<double>(e, "time"), Deadline = G<double>(e, "deadline"), Seat = G<int>(e, "seat"), Hand = G<int>(e, "hand"),
                Key = G<int>(e, "key"), Card = G<byte>(e, "card"), FaceDown = G<bool>(e, "faceDown"), Flag = G<bool>(e, "flag"),
                Cents = G<long>(e, "cents"), Result = G<object>(e, "result"),
            });
        return r;
    }

    static List<Ev> Roulette(object list)
    {
        var r = new List<Ev>();
        foreach (object e in (IEnumerable)list)
            r.Add(new Ev
            {
                Kind = G<object>(e, "kind").ToString(), Phase = G<object>(e, "phase").ToString(), Time = G<double>(e, "time"),
                Deadline = G<double>(e, "deadline"), Key = G<int>(e, "key"), Colour = G<int>(e, "colour"), Pocket = G<byte>(e, "pocket"),
                Flag = G<bool>(e, "flag"), Cents = G<long>(e, "cents"), Net = G<long>(e, "net"),
            });
        return r;
    }

    // ================================================================================================ blackjack table driver

    /// <summary>A blackjack table plus the host's till: every charge it approves and every credit its events ask for.</summary>
    sealed class Bj
    {
        public readonly object Table = New("BlackjackTable");
        public long Charged, Credited, NetSum;
        public readonly List<long> Costs = new List<long>();
        public readonly List<Ev> Log = new List<Ev>();
        public bool Refuse;
        public Func<int, int> TableDice;

        public Bj(string stack = null, ulong seed = 1)
        {
            TableDice = stack != null ? noDice : Dice(seed);
            if (stack != null) Stack(stack);
        }

        bool Till(long cents)
        {
            if (Refuse) return false;
            Charged += cents;
            Costs.Add(cents);
            return true;
        }

        public object Shoe => G<object>(Table, "Shoe");
        public string Dump() => string.Join("\n", Log.Select(e => e.ToString()));

        /// <summary>The next cards dealt, in order: seat stakes, the dealer's up card, the seats again, the hole card, then draws. Twos after.</summary>
        public void Stack(string ranks)
        {
            var shoe = Shoe;
            var cards = G<byte[]>(shoe, "cards");
            for (int i = 0; i < cards.Length; i++) cards[i] = i < ranks.Length ? Card(ranks[i]) : Card('2');
            Put(shoe, "next", 0);
            Put(shoe, "shuffles", 1);
        }

        public string Phase => G<object>(Table, "Phase").ToString();
        public int Active => G<int>(Table, "ActiveSeat");
        public double Due => G<double>(Table, "NextDueAt");
        public int Sit(int key, int seat = -1, double now = 0) => (int)Inv(Table, "Sit", key, seat, now);
        public bool Leave(int key, double now) => (bool)Inv(Table, "Leave", key, now);
        public string Bet(int key, long cents, double now) => Inv(Table, "PlaceBet", key, cents, now, (Func<long, bool>)Till).ToString();
        public string SitOut(int key, double now) => Inv(Table, "SitOut", key, now).ToString();
        public string Act(int key, string action, double now) => Inv(Table, "Act", key, E("BlackjackAction", action), now, (Func<long, bool>)Till).ToString();
        public string Legal(int key) => Inv(Table, "LegalActionsFor", key).ToString();
        public long Cost(int key, string action) => (long)Inv(Table, "CostOf", key, E("BlackjackAction", action));
        public string Advice(int key)
        {
            var a = new object[] { key, null };
            return (bool)Inv(Table, "TryAdvice", a) ? a[1].ToString() : null;
        }

        public List<Ev> Tick(double now) => Drain(Inv(Table, "Tick", now, TableDice));
        public List<Ev> Step(double now) => Drain(Inv(Table, "Step", now, TableDice));
        public List<Ev> Take() => Drain(Inv(Table, "TakeEvents"));
        public List<Ev> Close(double now) => Drain(Inv(Table, "Close", now, TableDice));

        List<Ev> Drain(object list)
        {
            var evs = Blackjack(list);
            foreach (var e in evs)
            {
                if (e.Kind == "BetRefunded" || e.Kind == "SeatSettled") Credited += e.Cents;
                if (e.Kind == "SeatSettled") NetSum += G<long>(e.Result, "totalNetCents");
            }
            Log.AddRange(evs);
            return evs;
        }

        /// <summary>Moves the clock to the next thing that is due and runs it.</summary>
        public List<Ev> Advance(ref double now)
        {
            double due = Due;
            Assert.IsFalse(double.IsInfinity(due), "nothing is due in phase " + Phase);
            now = Math.Max(now, due);
            return Tick(now);
        }

        /// <summary>Runs the clock until the phase is <paramref name="phase"/>.</summary>
        public void RunTo(string phase, ref double now)
        {
            for (int guard = 0; guard < 500 && Phase != phase; guard++) Advance(ref now);
            Assert.AreEqual(phase, Phase);
        }

        public object Snapshot() => Inv(Table, "Snapshot");
        public byte[] Bytes() => (byte[])Inv(Snapshot(), "Serialize");
    }

    static string Kinds(IEnumerable<Ev> evs) => string.Join(",", evs.Select(e => e.Kind));
    static IEnumerable<Ev> Only(IEnumerable<Ev> evs, string kind) => evs.Where(e => e.Kind == kind);
    static long Net(object result, int hand) => (long)result.GetType().GetMethod("HandNet").Invoke(result, new object[] { hand });
    static string Outcome(object result, int hand) => result.GetType().GetMethod("Outcome").Invoke(result, new object[] { hand }).ToString();
    static long Total(object result) => G<long>(result, "totalNetCents");

    /// <summary>The seat-settled event of a seat from a list of events.</summary>
    static Ev Settled(IEnumerable<Ev> evs, int seat)
    {
        var e = evs.FirstOrDefault(x => x.Kind == "SeatSettled" && x.Seat == seat);
        Assert.IsNotNull(e, "seat " + seat + " was not settled");
        return e;
    }

    // ================================================================================================ seats

    [Test]
    public void SeatsAreAssignedAndAFullTableRefuses()
    {
        object seats = New("CasinoSeats", 5);
        Assert.AreEqual(5, G<int>(seats, "Capacity"));
        for (int k = 0; k < 5; k++) Assert.AreEqual(k, (int)Inv(seats, "Sit", 100 + k, -1), "lowest free seat");
        Assert.IsTrue(G<bool>(seats, "IsFull"));
        Assert.AreEqual(-1, (int)Inv(seats, "Sit", 999, -1), "full table");
        var a = new object[] { 999, -1, null };
        Assert.AreEqual("TableFull", Inv(seats, "TrySit", a).ToString());
        a = new object[] { 102, -1, null };
        Assert.AreEqual("AlreadySeated", Inv(seats, "TrySit", a).ToString());
        Assert.AreEqual(2, a[2], "already seated reports the seat they have");
        Assert.AreEqual(-1, (int)Inv(seats, "Sit", 100, 4), "already seated");

        Assert.AreEqual(2, (int)Inv(seats, "SeatOf", 102));
        Assert.AreEqual(102, (int)Inv(seats, "KeyAt", 2));
        Assert.AreEqual(2, (int)Inv(seats, "Leave", 102));
        Assert.AreEqual(-1, (int)Inv(seats, "Leave", 102), "leaving twice");
        Assert.AreEqual(-1, (int)Inv(seats, "KeyAt", 2), "empty seat reads -1");
        Assert.AreEqual(4, G<int>(seats, "Count"));
        Assert.AreEqual(2, (int)Inv(seats, "Sit", 777, -1), "the freed seat is reused");
        CollectionAssert.AreEqual(new[] { 100, 101, 777, 103, 104 }, (int[])Inv(seats, "Occupants"));

        // A preferred seat is honoured when free and falls back to the lowest free seat when not.
        seats = New("CasinoSeats", 5);
        Assert.AreEqual(3, (int)Inv(seats, "Sit", 1, 3));
        Assert.AreEqual(0, (int)Inv(seats, "Sit", 2, 3), "preferred seat taken");
        Assert.AreEqual(1, (int)Inv(seats, "Sit", 3, 9), "preferred seat does not exist");
        Assert.AreEqual(-1, (int)Inv(seats, "Sit", -1, 0), "-1 is the empty key, never a player");
    }

    [Test]
    public void TheNearestFreeSeatIsPicked()
    {
        object seats = New("CasinoSeats", 5);
        var d = new[] { 3.0f, 1.0f, 2.0f, 0.5f, 4.0f };
        Assert.AreEqual(3, (int)Inv(seats, "NearestFreeSeat", (object)d));
        Inv(seats, "Sit", 1, 3);
        Assert.AreEqual(1, (int)Inv(seats, "NearestFreeSeat", (object)d), "seat 3 is taken");
        Assert.AreEqual(-1, (int)Inv(seats, "NearestFreeSeat", (object)d, 0.9f), "nothing within 0.9");
        Assert.AreEqual(0, (int)Inv(seats, "NearestFreeSeat", (object)new[] { 2f, 2f, 2f, 2f, 2f }), "ties go to the lower seat");
        Assert.AreEqual(2, (int)Inv(seats, "NearestFreeSeat", (object)new[] { float.NaN, -1f, 5f, 1f }), "NaN and negative distances are not candidates; seat 3 is taken");
        Assert.AreEqual(-1, (int)Inv(seats, "NearestFreeSeat", (object)null));
    }

    [Test]
    public void AMachineSeatHasOneOccupantAndFreesItselfWhenIdle()
    {
        object seat = New("CasinoSingleSeat");
        Assert.AreEqual(180.0, (double)T("CasinoSingleSeat").GetField("IdleSeconds").GetValue(null));
        Assert.AreEqual("Ok", Inv(seat, "TryOccupy", 7, 100.0).ToString());
        Assert.AreEqual("Occupied", Inv(seat, "TryOccupy", 8, 101.0).ToString());
        Assert.AreEqual("Ok", Inv(seat, "TryOccupy", 7, 120.0).ToString(), "the occupant confirming counts as activity");
        Assert.IsFalse((bool)Inv(seat, "Release", 8), "somebody else's machine stays theirs");
        Assert.AreEqual(-1, (int)Inv(seat, "Tick", 299.9), "180 s since 120 is 300");
        Assert.IsTrue((bool)Inv(seat, "Touch", 7, 250.0));
        Assert.IsFalse((bool)Inv(seat, "Touch", 8, 251.0));
        Assert.AreEqual(-1, (int)Inv(seat, "Tick", 429.9));
        Assert.AreEqual(7, (int)Inv(seat, "Tick", 430.0), "released on idle, and the key comes back");
        Assert.IsFalse(G<bool>(seat, "Occupied"));
        Assert.AreEqual(-1, (int)Inv(seat, "Tick", 10000.0));
        Assert.AreEqual("Ok", Inv(seat, "TryOccupy", 8, 500.0).ToString());
        Assert.IsTrue((bool)Inv(seat, "Release", 8));
        Assert.AreEqual("NotSeated", Inv(seat, "TryOccupy", -1, 0.0).ToString());
    }

    // ================================================================================================ blackjack: phases and the clock

    [Test]
    public void ARoundRunsThroughItsPhasesOnTheClock()
    {
        // Player T,8 against 7,9 (16). The dealer draws a 5 for 21.
        var bj = new Bj("T78" + "9" + "5");
        Assert.AreEqual(0, bj.Sit(1, 0, 0));
        Assert.AreEqual(1, bj.Sit(2, 1, 0), "a second player who is slow to decide keeps betting open for the full 15 s");
        Assert.AreEqual("Idle", bj.Phase);
        Assert.AreEqual("Seated,Seated", Kinds(bj.Tick(50)), "events of earlier calls come out of the next tick, once");
        Assert.AreEqual(0, bj.Tick(51).Count, "an idle table does nothing");
        Assert.IsTrue(double.IsInfinity(bj.Due));

        Assert.AreEqual("Ok", bj.Bet(1, 500, 100));
        var evs = bj.Tick(100);
        Assert.AreEqual("Betting", bj.Phase);
        Assert.AreEqual(115.0, bj.Due, 1e-9, "15 s from the first stake");
        Assert.AreEqual("PhaseChanged,BetPlaced", Kinds(evs), "betting opens, then the stake is shown");
        Assert.AreEqual(115.0, evs.First(e => e.Kind == "PhaseChanged").Deadline, 1e-9);

        Assert.AreEqual(0, bj.Tick(114.99).Count);
        Assert.AreEqual("Betting", bj.Phase);

        evs = bj.Tick(115);
        Assert.AreEqual("Deal", bj.Phase);
        Assert.AreEqual("PhaseChanged,CardDealt", Kinds(evs), "the first card goes out the moment betting closes");
        evs = bj.Tick(116.05);
        Assert.AreEqual(3, evs.Count, "the next three cards, 0.35 s apart: " + Kinds(evs));
        for (int i = 0; i < 3; i++) Assert.AreEqual(115.35 + 0.35 * i, evs[i].Time, 1e-9, "each card is stamped with the time it was due");
        Assert.AreEqual(-1, evs[0].Seat, "the dealer's up card");
        Assert.AreEqual(Card('7'), evs[0].Card);
        Assert.AreEqual(0, evs[1].Seat);
        Assert.AreEqual(-1, evs[2].Seat);
        Assert.IsTrue(evs[2].FaceDown, "the hole card goes down");
        Assert.AreEqual(255, evs[2].Card, "and an event never tells what it is");

        evs = bj.Tick(116.40);
        Assert.AreEqual("Turns", bj.Phase);
        Assert.AreEqual("PhaseChanged,TurnStarted", Kinds(evs), "no ace or ten up: no insurance, no peek");
        var turn = evs[1];
        Assert.AreEqual(0, turn.Seat);
        Assert.AreEqual(116.40 + 120.0, turn.Deadline, 1e-9, "one player in the round: 120 s");
        Assert.AreEqual(turn.Deadline, bj.Due, 1e-9);

        Assert.AreEqual("Ok", bj.Act(1, "Stand", 120));
        evs = bj.Tick(120);
        Assert.AreEqual("Dealer", bj.Phase);
        Assert.AreEqual("PlayerAction,PhaseChanged", Kinds(evs));
        Assert.AreEqual(120.8, bj.Due, 1e-9);

        evs = bj.Step(120.8);
        Assert.AreEqual("HoleRevealed", Kinds(evs), "one visible event per step");
        Assert.AreEqual(Card('9'), evs[0].Card);
        evs = bj.Step(121.6);
        Assert.AreEqual("DealerCard", Kinds(evs));
        Assert.AreEqual(Card('5'), evs[0].Card);
        Assert.AreEqual(121.6, evs[0].Time, 1e-9);
        evs = bj.Tick(122.4);
        Assert.AreEqual("SeatSettled,RoundSettled,PhaseChanged", Kinds(evs));
        Assert.AreEqual("Results", bj.Phase);
        Assert.AreEqual(127.4, bj.Due, 1e-9, "Results for 5 s");
        var s = Settled(evs, 0);
        Assert.AreEqual(1, s.Key);
        Assert.AreEqual("Lose", Outcome(s.Result, 0));
        Assert.AreEqual(-500L, Total(s.Result));
        Assert.AreEqual(0L, s.Cents, "stake back minus the loss");
        Assert.AreEqual(-500L, evs.First(e => e.Kind == "RoundSettled").Cents);

        Assert.AreEqual(0, bj.Tick(127.39).Count);
        evs = bj.Tick(127.4);
        Assert.AreEqual("Idle", bj.Phase);
        Assert.AreEqual("PhaseChanged", Kinds(evs));
        Assert.AreEqual(500, bj.Charged - bj.Credited);
        Assert.AreEqual(-bj.NetSum, bj.Charged - bj.Credited);
    }

    [Test]
    public void BettingEndsEarlyWhenEveryoneHasBetOrSatOut()
    {
        var bj = new Bj("T7" + "9" + "T8" + "8" + "9");
        bj.Sit(1, 0); bj.Sit(2, 1); bj.Sit(3, 2);
        Assert.AreEqual("Ok", bj.Bet(1, 1000, 1.0));
        bj.Tick(1.0);
        Assert.AreEqual(16.0, bj.Due, 1e-9);
        Assert.AreEqual("Ok", bj.Bet(2, 500, 3.0));
        bj.Tick(3.0);
        Assert.AreEqual("Betting", bj.Phase, "seat 2 has not decided");
        Assert.AreEqual("AlreadyBet", bj.SitOut(1, 4.0), "a stake down cannot be sat out");
        Assert.AreEqual("Ok", bj.SitOut(3, 5.0));
        var evs = bj.Tick(5.0);
        Assert.AreEqual("Deal", bj.Phase, "everyone has bet or sat out: no waiting for the clock");
        Assert.AreEqual(5.0, evs.First(e => e.Kind == "PhaseChanged").Time, 1e-9);
        Assert.AreEqual("CardDealt", evs.Last().Kind);
        Assert.AreEqual(0, evs.Last().Seat, "dealing starts with the first stake in seat order");

        // Only the seats with stakes get cards, in seat order: seats 0 and 1, never the one that sat out.
        bj.Tick(10.0);
        var dealt = bj.Log.Where(e => e.Kind == "CardDealt").ToList();
        CollectionAssert.AreEqual(new[] { 0, 1, -1, 0, 1, -1 }, dealt.Select(e => e.Seat).ToArray());
        CollectionAssert.AreEqual(new[] { Card('T'), Card('7'), Card('9'), Card('T'), Card('8'), 255 }.Select(b => (int)b).ToArray(), dealt.Select(e => (int)e.Card).ToArray());
    }

    [Test]
    public void StakesAndPhasesAreChecked()
    {
        var bj = new Bj("T78" + "9" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1);
        Assert.AreEqual("NotSeated", bj.Bet(9, 500, 0));
        Assert.AreEqual("BelowMinimum", bj.Bet(1, 400, 0));
        Assert.AreEqual("AboveMaximum", bj.Bet(1, 20100, 0));
        Assert.AreEqual("NotWholeDollars", bj.Bet(1, 550, 0));
        Assert.AreEqual("Idle", bj.Phase, "refused stakes change nothing");
        Assert.AreEqual(0L, bj.Charged);

        bj.Refuse = true;
        Assert.AreEqual("CantAfford", bj.Bet(1, 500, 0));
        Assert.AreEqual("Idle", bj.Phase, "a stake that cannot be charged is not placed");
        Assert.AreEqual(0L, (long)Inv(bj.Table, "BetAt", 0));
        bj.Refuse = false;

        Assert.AreEqual("WrongPhase", bj.SitOut(1, 0), "sitting out is a Betting thing");
        Assert.AreEqual("Ok", bj.Bet(1, 20000, 0), "$200 is the top");
        Assert.AreEqual("AlreadyBet", bj.Bet(1, 500, 1));
        Assert.AreEqual(20000L, bj.Charged, "the refused second stake charged nothing");
        Assert.AreEqual("TooLate", bj.Bet(2, 500, 15.5), "the betting clock ran out");
        Assert.AreEqual("NotSeated", bj.SitOut(9, 1));

        double now = 15.0;
        bj.Tick(now);
        Assert.AreEqual("Deal", bj.Phase);
        Assert.AreEqual("WrongPhase", bj.Bet(2, 500, now));
        Assert.AreEqual("WrongPhase", bj.Act(1, "Stand", now));
        bj.RunTo("Turns", ref now);
        Assert.AreEqual("NotInRound", bj.Act(2, "Stand", now), "seat 2 has no hand");
        Assert.AreEqual("WrongPhase", bj.Bet(1, 500, now));
        Assert.AreEqual("NotSeated", bj.Act(9, "Stand", now));
    }

    [Test]
    public void ATurnTimesOutToStand()
    {
        // Two players in the round: 25 s a decision. Seat 0 holds 5,6; seat 1 holds T,9; dealer 7,9 draws a 5.
        var bj = new Bj("5T" + "7" + "69" + "9" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 500, 0); bj.Bet(2, 500, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);
        var turn = bj.Log.Last(e => e.Kind == "TurnStarted");
        Assert.AreEqual(0, turn.Seat);
        Assert.AreEqual(now + 25.0, turn.Deadline, 1e-9, "25 s with two or more in the round");

        Assert.AreEqual("NotYourTurn", bj.Act(2, "Stand", now));
        var evs = bj.Tick(turn.Deadline - 0.01);
        Assert.AreEqual(0, evs.Count, bj.Dump());
        Assert.AreEqual("TooLate", bj.Act(1, "Hit", turn.Deadline + 0.01), "an action after the deadline is refused");
        evs = bj.Tick(turn.Deadline);
        Assert.AreEqual("TurnTimedOut", evs[0].Kind);
        Assert.AreEqual(1, evs[0].Key);
        Assert.AreEqual(turn.Deadline, evs[0].Time, 1e-9);
        Assert.AreEqual("TurnStarted", evs[1].Kind, "the next seat is asked straight away");
        Assert.AreEqual(1, evs[1].Seat);
        Assert.AreEqual(turn.Deadline + 25.0, evs[1].Deadline, 1e-9);

        // Seat 1 hits (T,9 + 2 = bust is not wanted; just stand) and the dealer plays.
        Assert.AreEqual("Ok", bj.Act(2, "Stand", turn.Deadline + 3));
        bj.RunTo("Results", ref now);
        var s0 = Settled(bj.Log, 0);
        Assert.AreEqual("Lose", Outcome(s0.Result, 0), "11 stood on 11 loses to 21");
        Assert.AreEqual(-500L, Total(s0.Result));
    }

    [Test]
    public void ATimeOutStandsEverySplitHandOfTheSeat()
    {
        // 8,8 splits; the player then goes quiet.
        var bj = new Bj("8T" + "6" + "89" + "T" + "32" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 500, 0); bj.Bet(2, 500, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);
        Assert.AreEqual("Ok", bj.Act(1, "Split", now));
        var turn = bj.Log.Last(e => e.Kind == "TurnStarted");
        Assert.AreEqual(0, turn.Seat);
        bj.Tick(turn.Deadline);
        var timed = bj.Log.Last(e => e.Kind == "TurnTimedOut");
        Assert.AreEqual(0, timed.Seat);
        Assert.AreEqual(1, bj.Active, "both split hands stood together: the turn moved to seat 1");
    }

    // ================================================================================================ blackjack: turns, splits, doubles, same pay as the single-player rules

    [Test]
    public void SeatsPlayInOrderAndSplitsAndDoublesPayAsAlone()
    {
        // seat 0: 8,8 against a 6; seat 1: T,9. Hole T (dealer 16). Split draws 3 and 2; both hands double and draw T and T
        // (21 and 20); seat 1 stands; the dealer draws a 5 for 21.
        string stack = "8T6" + "89T" + "32" + "TT" + "5";
        var bj = new Bj(stack);
        bj.Sit(10, 0); bj.Sit(11, 1);
        bj.Bet(10, 500, 0); bj.Bet(11, 500, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);

        Assert.AreEqual("Hit,Stand,Double,Split", bj.Legal(10).Replace(" ", ""), "any two cards double, a pair splits");
        Assert.AreEqual("None", bj.Legal(11), "not seat 1's turn");
        Assert.AreEqual(500L, bj.Cost(10, "Split"));
        Assert.AreEqual(-1L, bj.Cost(10, "Insurance"), "no insurance outside the insurance window");
        Assert.AreEqual("NotYourTurn", bj.Act(11, "Stand", now));

        bj.Costs.Clear();
        Assert.AreEqual("Ok", bj.Act(10, "Split", now));
        bj.Tick(now);
        CollectionAssert.AreEqual(new long[] { 500 }, bj.Costs, "a split is charged the opening stake");
        Assert.AreEqual("0.0", Turn(bj), "after a split the first hand is asked again");

        Assert.AreEqual("Ok", bj.Act(10, "Double", now));
        bj.Tick(now);
        Assert.AreEqual("0.1", Turn(bj));
        Assert.AreEqual("Ok", bj.Act(10, "Double", now));
        bj.Tick(now);
        CollectionAssert.AreEqual(new long[] { 500, 500, 500 }, bj.Costs);
        Assert.AreEqual("1.0", Turn(bj), "then seat 1");
        Assert.AreEqual("NotYourTurn", bj.Act(10, "Hit", now));
        Assert.AreEqual("Ok", bj.Act(11, "Stand", now));
        bj.RunTo("Results", ref now);

        var s0 = Settled(bj.Log, 0);
        Assert.AreEqual(2, G<byte>(s0.Result, "handCount"));
        Assert.AreEqual("Push", Outcome(s0.Result, 0));
        Assert.AreEqual("Lose", Outcome(s0.Result, 1));
        Assert.AreEqual(0L, Net(s0.Result, 0));
        Assert.AreEqual(-1000L, Net(s0.Result, 1), "a doubled loss is twice the stake");
        Assert.AreEqual(2000L, G<long>(s0.Result, "stakedCents"), bj.Dump());
        Assert.AreEqual(1000L, s0.Cents, "the pushed doubled hand comes back");
        var s1 = Settled(bj.Log, 1);
        Assert.AreEqual(-500L, Total(s1.Result));
        Assert.AreEqual(2500L, bj.Charged, "seat 0: stake, split, two doubles; seat 1: stake");
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);

        // The same cards at the single-player table pay the same.
        var alone = Alone("8" + "6" + "8" + "T" + "32" + "TT" + "5", 500, "Split", "Double", "Double");
        Assert.AreEqual(Net(s0.Result, 0), Net(alone, 0));
        Assert.AreEqual(Net(s0.Result, 1), Net(alone, 1));
        Assert.AreEqual(Total(s0.Result), Total(alone));
    }

    static string Turn(Bj bj) { var t = bj.Log.Last(e => e.Kind == "TurnStarted"); return t.Seat + "." + t.Hand; }

    /// <summary>One player alone against the dealer on a stacked shoe, with the given actions, settled with the single-player rules.</summary>
    static object Alone(string ranks, long stake, params string[] actions) => AloneBytes(ranks.Select(Card).ToArray(), stake, actions);

    static object AloneBytes(byte[] stack, long stake, params string[] actions)
    {
        object shoe = Activator.CreateInstance(T("BlackjackShoe"));
        var cards = G<byte[]>(shoe, "cards");
        for (int i = 0; i < cards.Length; i++) cards[i] = i < stack.Length ? stack[i] : Card('2');
        Put(shoe, "next", 0);
        Put(shoe, "shuffles", 1);
        object round = Static("BlackjackRules", "Deal", shoe, stake, noDice);
        Assert.IsNotNull(round);
        foreach (var a in actions)
            Assert.IsTrue((bool)Static("BlackjackRules", "Apply", round, shoe, E("BlackjackAction", a)), "alone: " + a);
        Assert.AreEqual("Settled", G<object>(round, "phase").ToString());
        return Static("BlackjackRules", "Settle", round);
    }

    [Test]
    public void RandomSharedRoundsPayExactlyWhatTheSinglePlayerRulesPay()
    {
        // Real shuffles, 1 to 5 seats, a mix of basic strategy and random legal actions, insurance taken now and then. Every
        // settled seat is replayed alone, on a shoe stacked with exactly the cards that seat saw, and must settle identically.
        var dice = Dice(31);
        var chaos = Dice(32);
        var bj = new Bj(null, 31);
        bj.TableDice = dice;
        for (int k = 0; k < 5; k++) bj.Sit(k, k);
        double now = 0;
        int seatRounds = 0, splitRounds = 0, doubleRounds = 0, insuredRounds = 0, naturals = 0;

        for (int round = 0; round < 1500; round++)
        {
            var log = new List<Ev>();
            var stakes = new long[5];
            bool anyBet = false;
            for (int k = 0; k < 5; k++)
            {
                if ((chaos(5) != 0 || (k == 4 && !anyBet)))
                {
                    stakes[k] = (5 + chaos(40)) * 100;
                    Assert.AreEqual("Ok", bj.Bet(k, stakes[k], now));
                    anyBet = true;
                }
                else bj.SitOut(k, now);
            }
            var actions = new List<string>[5];
            var insurance = new string[5];
            for (int k = 0; k < 5; k++) actions[k] = new List<string>();

            for (int guard = 0; guard < 400 && bj.Phase != "Results"; guard++)
            {
                if (bj.Phase == "Insurance")
                {
                    for (int k = 0; k < 5; k++)
                    {
                        if (stakes[k] == 0 || insurance[k] != null) continue;
                        string a = chaos(4) == 0 ? "Insurance" : "NoInsurance";
                        Assert.AreEqual("Ok", bj.Act(k, a, now), "insurance " + k);
                        insurance[k] = a;
                    }
                }
                else if (bj.Phase == "Turns")
                {
                    int k = bj.Active;
                    string a = bj.Advice(k);
                    string[] legal = bj.Legal(k).Split(',').Select(s => s.Trim()).ToArray();
                    if (chaos(4) == 0) a = legal[chaos(legal.Length)];
                    Assert.AreEqual("Ok", bj.Act(k, a, now), "seat " + k + " " + a + " of " + string.Join("/", legal));
                    actions[k].Add(a);
                }
                log.AddRange(bj.Tick(now));
                if (bj.Phase != "Results" && bj.Phase != "Turns" && bj.Phase != "Insurance")
                {
                    now = Math.Max(now, bj.Due);
                    log.AddRange(bj.Tick(now));
                }
            }
            Assert.AreEqual("Results", bj.Phase, "round " + round + " did not finish");
            now = bj.Due;
            log.AddRange(bj.Tick(now)); // Results -> Idle

            byte up = log.First(e => e.Kind == "CardDealt" && e.Seat == -1).Card;
            byte hole = log.First(e => e.Kind == "HoleRevealed").Card;
            var draws = log.Where(e => e.Kind == "DealerCard").Select(e => e.Card).ToList();
            foreach (var settled in log.Where(e => e.Kind == "SeatSettled"))
            {
                int k = settled.Seat;
                var mine = log.Where(e => e.Kind == "CardDealt" && e.Seat == k).Select(e => e.Card).ToList();
                var stack = new List<byte> { mine[0], up, mine[1], hole };
                stack.AddRange(mine.Skip(2));
                stack.AddRange(draws);
                var acts = new List<string>();
                if (insurance[k] != null) acts.Add(insurance[k]);
                acts.AddRange(actions[k]);
                var alone = AloneBytes(stack.ToArray(), stakes[k], acts.ToArray());
                var shared = settled.Result;
                string what = "round " + round + " seat " + k + " [" + string.Join(" ", acts) + "]";
                Assert.AreEqual(G<byte>(alone, "handCount"), G<byte>(shared, "handCount"), what + " hands");
                for (int h = 0; h < G<byte>(alone, "handCount"); h++)
                {
                    Assert.AreEqual(Outcome(alone, h), Outcome(shared, h), what + " outcome " + h);
                    Assert.AreEqual(Net(alone, h), Net(shared, h), what + " net " + h);
                }
                Assert.AreEqual(G<long>(alone, "insuranceNetCents"), G<long>(shared, "insuranceNetCents"), what + " insurance");
                Assert.AreEqual(G<long>(alone, "stakedCents"), G<long>(shared, "stakedCents"), what + " staked");
                Assert.AreEqual(G<long>(alone, "totalNetCents"), G<long>(shared, "totalNetCents"), what + " total");
                Assert.AreEqual(G<long>(alone, "returnCents"), settled.Cents, what + " credited");
                seatRounds++;
                if (acts.Contains("Split")) splitRounds++;
                if (acts.Contains("Double")) doubleRounds++;
                if (acts.Contains("Insurance")) insuredRounds++;
                if (G<byte>(shared, "handCount") == 1 && Outcome(shared, 0) == "Blackjack") naturals++;
            }
        }
        Assert.AreEqual(-bj.NetSum, bj.Charged - bj.Credited, "the till balances");
        Assert.Greater(splitRounds, 20, "the test must actually split");
        Assert.Greater(doubleRounds, 100);
        Assert.Greater(insuredRounds, 10);
        Assert.Greater(naturals, 10);
        UnityEngine.Debug.Log("[CasinoSharedTableTests] " + seatRounds + " shared seat-rounds replayed alone: identical outcomes and nets (" + splitRounds + " with splits, " + doubleRounds + " with doubles, " + insuredRounds + " insured, " + naturals + " blackjacks)");
    }

    // ================================================================================================ blackjack: insurance and the peek

    [Test]
    public void InsuranceIsOnlyOfferedAgainstAnAceAndTheDealerPeeks()
    {
        // Ace up: Insurance opens for 8 s. Player T,9 (19); hole K: the dealer has blackjack.
        var bj = new Bj("TA" + "9K" + "5");
        bj.Sit(1, 0);
        bj.Bet(1, 1000, 0);
        double now = 0;
        bj.RunTo("Insurance", ref now);
        var opened = bj.Log.Last(e => e.Kind == "InsuranceOpened");
        Assert.AreEqual(now + 8.0, opened.Deadline, 1e-9);
        Assert.AreEqual("Insurance, NoInsurance", bj.Legal(1));
        Assert.AreEqual(500L, bj.Cost(1, "Insurance"), "half the stake");
        Assert.AreEqual("IllegalAction", bj.Act(1, "Hit", now), "only insurance decisions in the window");
        bj.Refuse = true;
        Assert.AreEqual("CantAfford", bj.Act(1, "Insurance", now));
        Assert.AreEqual("Insurance, NoInsurance", bj.Legal(1), "a refused charge leaves the decision open");
        bj.Refuse = false;
        Assert.AreEqual("Ok", bj.Act(1, "Insurance", now + 1));
        Assert.AreEqual("IllegalAction", bj.Act(1, "NoInsurance", now + 2), "one decision per round");
        var evs = bj.Tick(now + 1);
        Assert.AreEqual(1000L + 500L, bj.Charged);
        Assert.AreEqual("InsuranceTaken", evs[0].Kind);
        // Everyone decided: the window closed at once and the dealer peeked, saw blackjack and never asked for a turn.
        var kinds = Kinds(bj.Log.SkipWhile(e => e.Kind != "InsuranceTaken"));
        StringAssert.Contains("InsuranceClosed,Peek,HoleRevealed,PhaseChanged", kinds);
        Assert.IsFalse(bj.Log.Any(e => e.Kind == "TurnStarted"), "a dealer blackjack ends the round before the turns");
        Assert.IsTrue(bj.Log.First(e => e.Kind == "Peek").Flag);
        Assert.AreEqual(Card('K'), bj.Log.First(e => e.Kind == "HoleRevealed").Card);
        bj.RunTo("Results", ref now);
        var s = Settled(bj.Log, 0);
        Assert.AreEqual("Lose", Outcome(s.Result, 0));
        Assert.AreEqual(-1000L, Net(s.Result, 0));
        Assert.AreEqual(1000L, G<long>(s.Result, "insuranceNetCents"), "insurance pays 2:1");
        Assert.AreEqual(0L, Total(s.Result));
        Assert.AreEqual(1500L, s.Cents, "stake + insurance + the 2:1");
        Assert.AreEqual(0L, bj.Charged - bj.Credited + bj.NetSum);
    }

    [Test]
    public void InsuranceTimesOutToDeclineAndLosesToAMissingBlackjack()
    {
        // Ace up, hole 6 (soft 17: the dealer draws a 4 for 21). Both players hold 18, stand, and lose.
        var bj = new Bj("T9A" + "89" + "6" + "4");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 500, 0); bj.Bet(2, 500, 0);
        double now = 0;
        bj.RunTo("Insurance", ref now);
        Assert.AreEqual("Ok", bj.Act(2, "Insurance", now));
        var open = bj.Log.Last(e => e.Kind == "InsuranceOpened");
        var evs = bj.Tick(open.Deadline);
        Assert.AreEqual("InsuranceTaken,InsuranceDeclined,InsuranceClosed,Peek,PhaseChanged,TurnStarted", Kinds(evs), "seat 1's insurance was queued before; seat 0 never answered");
        Assert.AreEqual(0, evs[1].Seat);
        Assert.AreEqual(open.Deadline, evs[1].Time, 1e-9);
        Assert.IsFalse(evs[3].Flag, "no blackjack under the ace");
        Assert.AreEqual("Turns", bj.Phase);
        Assert.AreEqual(0, bj.Active);
        Assert.AreEqual("Ok", bj.Act(1, "Stand", open.Deadline + 1));
        Assert.AreEqual("Ok", bj.Act(2, "Stand", open.Deadline + 2));
        bj.RunTo("Results", ref now);
        var s1 = Settled(bj.Log, 1);
        Assert.AreEqual(-250L, G<long>(s1.Result, "insuranceNetCents"), "insurance lost");
        Assert.AreEqual(-250L - 500L, Total(s1.Result));
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);
    }

    [Test]
    public void ATenUpIsPeekedAtOnceAndNoInsuranceIsOffered()
    {
        // Ten up, ace in the hole: blackjack straight after the deal. The player's own blackjack pushes.
        var bj = new Bj("A" + "K" + "T" + "A");
        bj.Sit(1, 0);
        bj.Bet(1, 500, 0);
        double now = 0;
        bj.RunTo("Dealer", ref now);
        Assert.IsFalse(bj.Log.Any(e => e.Kind == "InsuranceOpened"));
        Assert.IsTrue(bj.Log.First(e => e.Kind == "Peek").Flag);
        bj.RunTo("Results", ref now);
        Assert.AreEqual("Push", Outcome(Settled(bj.Log, 0).Result, 0));
        Assert.AreEqual(500L, Settled(bj.Log, 0).Cents);

        // Ten up, no blackjack under it: the peek is announced and play goes on. Player 20 beats 18.
        bj = new Bj("TK" + "T8");
        bj.Sit(1, 0);
        bj.Bet(1, 500, 0);
        now = 0;
        bj.RunTo("Turns", ref now);
        Assert.IsFalse(bj.Log.First(e => e.Kind == "Peek").Flag);
        bj.Act(1, "Stand", now);
        bj.RunTo("Results", ref now);
        Assert.AreEqual("Win", Outcome(Settled(bj.Log, 0).Result, 0));

        // A six up is never peeked at.
        bj = new Bj("T6" + "8" + "9");
        bj.Sit(1, 0);
        bj.Bet(1, 500, 0);
        now = 0;
        bj.RunTo("Turns", ref now);
        Assert.IsFalse(bj.Log.Any(e => e.Kind == "Peek"));
    }

    [Test]
    public void NaturalsPayThreeToTwoWithoutATurnAndTheDealerOnlyDrawsAgainstALiveHand()
    {
        // Seat 0 has a blackjack, seat 1 busts: nothing left for the dealer to beat, so the dealer shows and stops at 6,T = 16.
        var bj = new Bj("AT" + "6" + "KQ" + "T" + "Q");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 1000, 0); bj.Bet(2, 500, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);
        Assert.AreEqual(1, bj.Active, "a natural has no decision: straight to seat 1");
        Assert.AreEqual("Ok", bj.Act(2, "Hit", now), "20 hits...");
        Assert.AreEqual("Dealer", bj.Phase, "...and busts");
        bj.RunTo("Results", ref now);
        Assert.IsFalse(bj.Log.Any(e => e.Kind == "DealerCard"), "a dealer facing only busts and blackjacks draws nothing");
        var s0 = Settled(bj.Log, 0);
        Assert.AreEqual("Blackjack", Outcome(s0.Result, 0));
        Assert.AreEqual(1500L, Net(s0.Result, 0), "3:2");
        Assert.AreEqual(2500L, s0.Cents);
        Assert.AreEqual("Bust", Outcome(Settled(bj.Log, 1).Result, 0));
    }

    // ================================================================================================ blackjack: leaving

    [Test]
    public void LeavingBeforeTheDealRefundsTheStake()
    {
        var bj = new Bj("T78" + "9" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1); bj.Sit(3, 2); // seat 2 never decides, so betting stays open
        bj.Bet(1, 700, 0);
        bj.Bet(2, 900, 1);
        bj.Tick(1);
        Assert.AreEqual(1600L, bj.Charged);
        Assert.IsTrue(bj.Leave(1, 2));
        Assert.IsFalse(bj.Leave(1, 2), "already gone");
        var evs = bj.Tick(2);
        var refund = evs.Single(e => e.Kind == "BetRefunded");
        Assert.AreEqual(1, refund.Key);
        Assert.AreEqual(700L, refund.Cents);
        Assert.AreEqual(0, refund.Seat);
        Assert.AreEqual(700L, bj.Credited);
        Assert.AreEqual("Betting", bj.Phase, "the other stake is still down");
        Assert.AreEqual(-1, (int)Inv(bj.Table, "KeyAt", 0));
        Assert.AreEqual(0L, (long)Inv(bj.Table, "BetAt", 0));

        // The last stake leaving takes the table back to Idle with the clock stopped.
        Assert.IsTrue(bj.Leave(2, 3));
        evs = bj.Tick(3);
        Assert.AreEqual(900L, evs.Single(e => e.Kind == "BetRefunded").Cents);
        Assert.AreEqual("Idle", bj.Phase);
        Assert.IsTrue(double.IsInfinity(bj.Due));
        Assert.AreEqual(bj.Charged, bj.Credited, "everything charged came back");
        bj.Tick(1000);
        Assert.AreEqual("Idle", bj.Phase, "no phantom round");
    }

    [Test]
    public void LeavingAfterTheDealStandsAndSettlesToTheLeaver()
    {
        // Seat 0: 5,6 (11); seat 1: T,9. Dealer 7,9 draws a 5 for 21. Seat 0 would have doubled; it leaves during Turns instead.
        var bj = new Bj("5T" + "7" + "69" + "9" + "5");
        bj.Sit(10, 0); bj.Sit(11, 1);
        bj.Bet(10, 500, 0); bj.Bet(11, 500, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);
        Assert.AreEqual(0, bj.Active);
        Assert.IsTrue(bj.Leave(10, now));
        var evs = bj.Tick(now);
        var left = evs.First(e => e.Kind == "SeatLeft");
        Assert.IsTrue(left.Flag, "the hand stays in play");
        Assert.AreEqual("TurnStarted", evs.Last().Kind, "the turn passes to seat 1 at once");
        Assert.AreEqual(1, evs.Last().Seat);
        Assert.AreEqual("NotSeated", bj.Act(10, "Stand", now));

        // A new player sits in the empty seat: they wait for Betting. The old hand is not theirs.
        Assert.AreEqual(0, bj.Sit(20, 0, now));
        Assert.AreEqual("WrongPhase", bj.Bet(20, 500, now));
        Assert.AreEqual("NotInRound", bj.Act(20, "Stand", now));
        var snap = bj.Snapshot();
        var seat0 = ((Array)G<object>(snap, "seats")).GetValue(0);
        Assert.AreEqual(20, G<int>(seat0, "key"), "the new player is sitting there");
        Assert.AreEqual(10, G<int>(seat0, "ownerKey"), "the hand on the felt is still the leaver's");

        Assert.AreEqual("Ok", bj.Act(11, "Stand", now));
        bj.RunTo("Results", ref now);
        var s = Settled(bj.Log, 0);
        Assert.AreEqual(10, s.Key, "settled to the player who left, not the one who sits there now");
        Assert.IsTrue(s.Flag, "in absentia");
        Assert.AreEqual("Lose", Outcome(s.Result, 0), "11 stood on 11 loses to 21");
        Assert.AreEqual(0L, s.Cents);
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);
        bj.RunTo("Idle", ref now);
        // The next round the newcomer may bet.
        Assert.AreEqual("Ok", bj.Bet(20, 500, now));
    }

    [Test]
    public void LeavingDuringDealOrInsuranceDeclinesAndStands()
    {
        // Leaving while the cards go out, against an ace: the hand stands, insurance is declined, the round settles to the leaver.
        var bj = new Bj("T9A" + "99" + "6" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 500, 0); bj.Bet(2, 500, 0);
        double now = 0;
        bj.Tick(0);
        Assert.AreEqual("Deal", bj.Phase);
        Assert.IsTrue(bj.Leave(1, 0.1));
        bj.Tick(0.1);
        bj.RunTo("Insurance", ref now);
        Assert.AreEqual("None", bj.Legal(1), "gone");
        Assert.AreEqual("Insurance, NoInsurance", bj.Legal(2));
        Assert.AreEqual("Ok", bj.Act(2, "NoInsurance", now));
        bj.RunTo("Turns", ref now);
        Assert.AreEqual(1, bj.Active, "seat 0 left: its hand stood without a turn");
        bj.Act(2, "Stand", now);
        bj.RunTo("Results", ref now);
        var s0 = Settled(bj.Log, 0);
        Assert.AreEqual(1, s0.Key);
        Assert.IsTrue(s0.Flag);
        Assert.AreEqual(0L, G<long>(s0.Result, "insuranceNetCents"), "declined for them");
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);

        // Everybody leaving mid-round: the table finishes alone, all hands standing.
        bj = new Bj("TT" + "6" + "98" + "T" + "5");
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 500, 0); bj.Bet(2, 500, 0);
        now = 0;
        bj.RunTo("Turns", ref now);
        bj.Leave(1, now); bj.Leave(2, now);
        bj.RunTo("Idle", ref now);
        Assert.AreEqual(2, bj.Log.Count(e => e.Kind == "SeatSettled"));
        Assert.IsTrue(bj.Log.Where(e => e.Kind == "SeatSettled").All(e => e.Flag));
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);
    }

    [Test]
    public void ClosingTheTableRefundsAndSettlesEverything()
    {
        var bj = new Bj(null, 5);
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 800, 0);
        var evs = bj.Close(1);
        Assert.AreEqual("Idle", bj.Phase);
        Assert.AreEqual(800L, evs.Single(e => e.Kind == "BetRefunded").Cents);
        Assert.AreEqual(bj.Charged, bj.Credited);

        bj = new Bj(null, 6);
        bj.Sit(1, 0); bj.Sit(2, 1);
        bj.Bet(1, 800, 0); bj.Bet(2, 1200, 0);
        double now = 0;
        bj.RunTo("Turns", ref now);
        evs = bj.Close(now);
        Assert.AreEqual("Idle", bj.Phase);
        Assert.AreEqual(2, evs.Count(e => e.Kind == "SeatSettled"), "a round in play runs out to its settlement");
        Assert.AreEqual(bj.Charged - bj.Credited, -bj.NetSum);
        Assert.AreEqual(0, G<int>(G<object>(bj.Table, "Seats"), "Count"));
    }

    // ================================================================================================ blackjack: the shoe

    [Test]
    public void TheShoeShufflesBeforeTheFirstDealAndAtTheCutCard()
    {
        var bj = new Bj(null, 77);
        bj.Sit(1, 0);
        double now = 0;
        bj.Bet(1, 500, now);
        bj.RunTo("Turns", ref now);
        var first = bj.Log.Where(e => e.Kind == "Reshuffled").ToList();
        Assert.AreEqual(1, first.Count, "the first deal shuffles");
        Assert.IsFalse(first[0].Flag);
        Assert.AreEqual(1, G<int>(bj.Shoe, "shuffles"));

        // Just short of the cut card (234 dealt): no shuffle. At it: shuffle before the next deal.
        bj.Act(1, "Stand", now);
        bj.RunTo("Idle", ref now);
        Put(bj.Shoe, "next", 233);
        bj.Log.Clear();
        bj.Bet(1, 500, now);
        bj.RunTo("Deal", ref now);
        Assert.IsFalse(bj.Log.Any(e => e.Kind == "Reshuffled"), "233 cards out is not yet the cut card");
        bj.RunTo("Turns", ref now);
        bj.Act(1, "Stand", now);
        bj.RunTo("Idle", ref now);
        Put(bj.Shoe, "next", 234);
        bj.Log.Clear();
        bj.Bet(1, 500, now);
        bj.RunTo("Deal", ref now);
        var re = bj.Log.Single(e => e.Kind == "Reshuffled");
        Assert.AreEqual(2, G<int>(bj.Shoe, "shuffles"));
        Assert.LessOrEqual(G<int>(bj.Shoe, "next"), 4, "a fresh shoe: only this round's first cards are out");
        Assert.LessOrEqual(re.Time, bj.Log.First(e => e.Kind == "CardDealt").Time, "the shuffle comes before the cards");
    }

    [Test]
    public void AShoeThatRunsDryMidRoundIsRefilledRatherThanCrashing()
    {
        var bj = new Bj(null, 78);
        bj.Sit(1, 0);
        double now = 0;
        bj.Bet(1, 500, now);
        bj.RunTo("Turns", ref now);
        Put(bj.Shoe, "next", 311);
        bj.Log.Clear();
        string a = bj.Legal(1).Contains("Hit") ? "Hit" : "Stand";
        Assert.AreEqual("Ok", bj.Act(1, a, now));
        bj.Tick(now);
        var re = bj.Log.FirstOrDefault(e => e.Kind == "Reshuffled");
        Assert.IsNotNull(re);
        Assert.IsTrue(re.Flag, "flagged as an emergency shuffle");
    }

    // ================================================================================================ blackjack: the odds and the money

    [Test]
    public void BasicStrategyAtTheSharedTableKeepsTheHousesEdge()
    {
        var one = (BlackjackStats)Edge(1, 1000000, 101);
        var three = (BlackjackStats)Edge(3, 600000, 103);
        var five = (BlackjackStats)Edge(5, 1000000, 105);
        UnityEngine.Debug.Log("[CasinoSharedTableTests] house edge with basic strategy at the shared table, flat $10: 1 seat " + one.Percent.ToString("F3") + "% over " + one.rounds + " seat-rounds, 3 seats " +
                              three.Percent.ToString("F3") + "% over " + three.rounds + ", 5 seats " + five.Percent.ToString("F3") + "% over " + five.rounds + " (shuffles " + one.shuffles + "/" + three.shuffles + "/" + five.shuffles + ")");
        foreach (var s in new[] { one, three, five })
        {
            Assert.GreaterOrEqual(s.rounds, 600000);
            Assert.Greater(s.Percent, 0.2, s.seats + " seats: the house must not be giving it away");
            Assert.Less(s.Percent, 1.2, s.seats + " seats: nor taking too much");
            Assert.Greater(s.splits, 0);
            Assert.Greater(s.doubles, 0);
            Assert.Greater(s.blackjacks, 0);
        }
        Assert.GreaterOrEqual(one.rounds + three.rounds + five.rounds, 1000000);
    }

    struct BlackjackStats
    {
        public int seats;
        public long rounds, initial, net, splits, doubles, blackjacks, shuffles;
        public double Percent => initial == 0 ? 0 : -100.0 * net / initial;
    }

    static object Edge(int seats, long seatRounds, ulong seed)
    {
        object stats = Static("BlackjackTableSim", "BasicStrategy", seats, seatRounds, 1000L, Dice(seed));
        return new BlackjackStats
        {
            seats = seats, rounds = G<long>(stats, "rounds"), initial = G<long>(stats, "initialStakeCents"), net = G<long>(stats, "netCents"),
            splits = G<long>(stats, "splits"), doubles = G<long>(stats, "doubles"), blackjacks = G<long>(stats, "blackjacks"), shuffles = G<long>(stats, "shuffles"),
        };
    }

    [Test]
    public void MoneyIsConservedOverAHundredThousandRoundsOfChaos()
    {
        // 100,000 rounds with players coming and going, sitting out, timing out, leaving mid-hand, declined charges and
        // insurance. The till is the host's: it counts what the table asked for and what its events told it to pay.
        object audit = Static("BlackjackTableSim", "Chaos", 100000L, Dice(2024), Dice(2025));
        long rounds = G<long>(audit, "rounds"), charged = G<long>(audit, "chargedCents"), credited = G<long>(audit, "creditedCents"), net = G<long>(audit, "settledNetCents");
        UnityEngine.Debug.Log("[CasinoSharedTableTests] chaos: " + rounds + " rounds, " + G<long>(audit, "seatRounds") + " seat-rounds, charged $" + charged / 100.0 + ", credited $" + credited / 100.0 +
                              ", settled net $" + net / 100.0 + "; " + G<long>(audit, "leaves") + " leaves, " + G<long>(audit, "absentSettles") + " settled in absentia, " + G<long>(audit, "timeouts") + " timeouts, " +
                              G<long>(audit, "refunds") + " refunds, " + G<long>(audit, "refusedCharges") + " refused charges, " + G<long>(audit, "snapshots") + " snapshots checked");
        Assert.IsNull(G<string>(audit, "firstViolation"), "violation: " + G<string>(audit, "firstViolation"));
        Assert.AreEqual(0L, G<long>(audit, "violations"));
        Assert.GreaterOrEqual(rounds, 100000L);
        Assert.AreEqual(-net, charged - credited, "charges minus credits is minus the sum of the nets, to the cent");
        Assert.IsTrue(G<bool>(audit, "Balanced"));
        Assert.Greater(G<long>(audit, "leaves"), 1000L);
        Assert.Greater(G<long>(audit, "absentSettles"), 100L, "players who walked away mid-hand were paid");
        Assert.Greater(G<long>(audit, "timeouts"), 100L);
        Assert.Greater(G<long>(audit, "refunds"), 100L);
        Assert.Greater(G<long>(audit, "refusedCharges"), 100L);
        Assert.Greater(G<long>(audit, "seatRounds"), 100000L);
    }

    // ================================================================================================ blackjack: snapshots

    [Test]
    public void SnapshotsRoundTripAtEveryPhase()
    {
        var bj = new Bj(null, 9);
        bj.Sit(5, 0); bj.Sit(6, 2); bj.Sit(7, 4);
        double now = 0;
        var seenPhases = new HashSet<string>();
        int maxBytes = 0, minBytes = int.MaxValue;
        var chaos = Dice(10);
        bj.Bet(5, 1000, now); bj.Bet(6, 500, now);
        for (int guard = 0; guard < 300; guard++)
        {
            bj.Tick(now);
            var snap = bj.Snapshot();
            var bytes = (byte[])Inv(snap, "Serialize");
            maxBytes = Math.Max(maxBytes, bytes.Length); minBytes = Math.Min(minBytes, bytes.Length);
            Assert.AreEqual(0xB1, bytes[0]);
            Assert.AreEqual(1, bytes[1], "the version byte");
            var back = Static("BlackjackTableSnapshot", "Deserialize", (object)bytes);
            CollectionAssert.AreEqual(bytes, (byte[])Inv(back, "Serialize"), "round trip in phase " + bj.Phase);
            Assert.AreEqual(G<object>(snap, "phase").ToString(), G<object>(back, "phase").ToString());
            Assert.AreEqual(G<double>(snap, "phaseDeadline"), G<double>(back, "phaseDeadline"));
            Assert.AreEqual(5, ((Array)G<object>(back, "seats")).Length);
            seenPhases.Add(bj.Phase);
            if (bj.Phase == "Results") break;
            if (bj.Phase == "Insurance") { bj.Act(5, "NoInsurance", now); bj.Act(6, "Insurance", now); }
            else if (bj.Phase == "Turns")
            {
                int k = bj.Active == 0 ? 5 : 6;
                string a = bj.Advice(k);
                bj.Act(k, a, now);
                if (chaos(3) == 0) bj.Leave(k == 5 ? 6 : 5, now);
            }
            else now = Math.Max(now, bj.Due);
        }
        foreach (var p in new[] { "Betting", "Deal", "Turns", "Dealer", "Results" })
            Assert.IsTrue(seenPhases.Contains(p), "phase " + p + " was not exercised: " + string.Join(",", seenPhases));
        UnityEngine.Debug.Log("[CasinoSharedTableTests] blackjack snapshot sizes: " + minBytes + " to " + maxBytes + " bytes");

        // An empty table.
        var empty = new Bj(null, 1);
        var eb = empty.Bytes();
        UnityEngine.Debug.Log("[CasinoSharedTableTests] empty blackjack snapshot: " + eb.Length + " bytes");
        Assert.Less(eb.Length, 200);

        // Garbage is refused, not crashed on.
        var good = bj.Bytes();
        var args = new object[] { good, null };
        Assert.IsTrue((bool)Static("BlackjackTableSnapshot", "TryDeserialize", args));
        foreach (var bad in new[] { new byte[0], good.Take(good.Length - 1).ToArray(), good.Concat(new byte[] { 0 }).ToArray(), new byte[] { 0xB1, 2 }, new byte[] { 0xB2, 1 }.Concat(good.Skip(2)).ToArray() })
            Assert.IsFalse((bool)Static("BlackjackTableSnapshot", "TryDeserialize", new object[] { bad, null }));
        Assert.Throws<TargetInvocationException>(() => Static("BlackjackTableSnapshot", "Deserialize", (object)new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void AHiddenHoleCardAndTheUndealtShoeNeverLeaveTheHost()
    {
        // Two tables, the same dealt cards, a different hole card and a different undealt shoe. While the hole card is hidden
        // the public bytes must be identical: nothing secret is in them. After the reveal they must differ (the test can tell).
        var a = new Bj("T7" + "8" + "6" + "KQJ5432");
        var c = new Bj("T7" + "8" + "9" + "2345KQJ");
        foreach (var b in new[] { a, c }) { b.Sit(1, 0); b.Bet(1, 500, 0); }
        double now = 0;
        int hiddenChecks = 0;
        for (int guard = 0; guard < 100 && a.Phase != "Results"; guard++)
        {
            a.Tick(now); c.Tick(now);
            var snapA = a.Snapshot();
            if (G<bool>(snapA, "HoleHidden"))
            {
                hiddenChecks++;
                CollectionAssert.AreEqual(a.Bytes(), c.Bytes(), "phase " + a.Phase + " t=" + now + ": hole card and shoe must not show in the bytes");
                Assert.AreEqual(255, G<byte[]>(snapA, "dealer")[1]);
            }
            if (a.Phase == "Turns") { Assert.AreEqual("Ok", a.Act(1, "Stand", now)); Assert.AreEqual("Ok", c.Act(1, "Stand", now)); }
            else now = Math.Max(now, a.Due);
        }
        Assert.GreaterOrEqual(hiddenChecks, 3, "Deal, Turns and the start of Dealer all had a hidden hole card");
        a.Tick(now); c.Tick(now);
        CollectionAssert.AreNotEqual(a.Bytes(), c.Bytes(), "after the reveal the hole card is public");

        // Five seats on a real shuffle: at every moment the cards in the public picture are exactly the cards dealt out of the
        // shoe, less the one face down. Nothing else (no card still in the shoe) can be in there.
        var bj = new Bj(null, 42);
        for (int k = 0; k < 5; k++) { bj.Sit(k, k); bj.Bet(k, 500, 0); }
        now = 0;
        int checkedSnaps = 0;
        for (int guard = 0; guard < 400 && bj.Phase != "Results"; guard++)
        {
            bj.Tick(now);
            var back = Static("BlackjackTableSnapshot", "Deserialize", (object)bj.Bytes());
            var visible = new List<byte>(G<byte[]>(back, "dealer").Where(x => x != 255));
            foreach (object seat in (Array)G<object>(back, "seats"))
                foreach (object hand in (Array)G<object>(seat, "hands"))
                    visible.AddRange(G<byte[]>(hand, "cards"));
            int next = G<int>(bj.Shoe, "next"); // the first round of a fresh shoe: everything dealt is shoe[0..next)
            Assert.AreEqual(312 - next, G<int>(back, "shoeLeft"), "the picture shows how many cards are left, not which");
            int down = G<bool>(back, "HoleHidden") ? 1 : 0;
            Assert.AreEqual(next - down, visible.Count, "phase " + bj.Phase + ": the picture shows what was dealt, less the hole card");
            var window = G<byte[]>(bj.Shoe, "cards").Take(next).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var g in visible.GroupBy(x => x))
                Assert.LessOrEqual(g.Count(), window.ContainsKey(g.Key) ? window[g.Key] : 0, "card " + g.Key + " is shown more often than it was dealt");
            checkedSnaps++;
            if (bj.Phase == "Turns") bj.Act(bj.Active, bj.Advice(bj.Active), now);
            else if (bj.Phase == "Insurance") { for (int k = 0; k < 5; k++) bj.Act(k, "NoInsurance", now); }
            else now = Math.Max(now, bj.Due);
        }
        Assert.Greater(checkedSnaps, 10);
    }

    // ================================================================================================ roulette

    static object RBet(string type, int index, int cents)
    {
        object bet = Activator.CreateInstance(T("RouletteBet"));
        T("RouletteBet").GetField("type").SetValue(bet, E("RouletteBetType", type));
        T("RouletteBet").GetField("index").SetValue(bet, (byte)index);
        T("RouletteBet").GetField("cents").SetValue(bet, cents);
        return bet;
    }

    static Array RBets(params object[] bets)
    {
        var array = Array.CreateInstance(T("RouletteBet"), bets.Length);
        for (int i = 0; i < bets.Length; i++) array.SetValue(bets[i], i);
        return array;
    }

    /// <summary>A roulette table plus the host's till.</summary>
    sealed class Rl
    {
        public readonly object Table = New("RouletteTable");
        public long Charged, Credited, NetSum;
        public readonly HashSet<int> Broke = new HashSet<int>();
        public readonly List<Ev> Log = new List<Ev>();
        public readonly List<int> Pockets;
        public int Draws;
        public Func<int, int> Dice;

        /// <summary>The wheel's pockets come from <paramref name="pockets"/> in turn (or a seeded generator when null).</summary>
        public Rl(params int[] pockets)
        {
            Pockets = pockets.ToList();
            var seeded = CasinoSharedTableTests.Dice(5);
            Dice = n => { Draws++; Assert.AreEqual(38, n); return Pockets.Count > 0 ? Pockets[(Draws - 1) % Pockets.Count] : seeded(n); };
        }

        bool Till(int key, long cents)
        {
            if (Broke.Contains(key)) return false;
            Charged += cents;
            return true;
        }

        public string Phase => G<object>(Table, "Phase").ToString();
        public double Due => G<double>(Table, "NextDueAt");
        public int Join(int key, double now = 0) => (int)Inv(Table, "Join", key, now);
        public bool Leave(int key, double now) => (bool)Inv(Table, "Leave", key, now);
        public string Bets(int key, double now, params object[] bets)
        {
            var args = new object[] { key, RBets(bets), bets.Length, now, null };
            string err = Inv(Table, "SetBets", args).ToString();
            return err + "/" + args[4];
        }
        public string Ready(int key, double now, bool ready = true) => Inv(Table, "SetReady", key, ready, now).ToString();
        public string Clear(int key, double now) => Inv(Table, "ClearBets", key, now).ToString();
        public List<Ev> Tick(double now) => Drain(Inv(Table, "Tick", now, Dice, (Func<int, long, bool>)Till));
        public List<Ev> Step(double now) => Drain(Inv(Table, "Step", now, Dice, (Func<int, long, bool>)Till));
        public List<Ev> Close(double now) => Drain(Inv(Table, "Close", now, Dice, (Func<int, long, bool>)Till));

        List<Ev> Drain(object list)
        {
            var evs = Roulette(list);
            foreach (var e in evs)
                if (e.Kind == "ParticipantSettled") { Credited += e.Cents; NetSum += e.Net; }
            Log.AddRange(evs);
            return evs;
        }

        public List<Ev> Advance(ref double now)
        {
            double due = Due;
            Assert.IsFalse(double.IsInfinity(due), "nothing is due in phase " + Phase);
            now = Math.Max(now, due);
            return Tick(now);
        }

        public void RunTo(string phase, ref double now)
        {
            for (int guard = 0; guard < 50 && Phase != phase; guard++) Advance(ref now);
            Assert.AreEqual(phase, Phase);
        }

        public object Snapshot() => Inv(Table, "Snapshot");
        public byte[] Bytes() => (byte[])Inv(Snapshot(), "Serialize");
        public int Colour(int key) => (int)Inv(Table, "ColourOf", key);
        public long Total(int key) => (long)Inv(Table, "TotalOf", key);
        public int BetCount(int key) => ((Array)Inv(Table, "BetsOf", key)).Length;
    }

    [Test]
    public void ABetSetReplacesTheEarlierOneAndIsCheckedAgainstTheLimits()
    {
        var rl = new Rl(17);
        Assert.AreEqual(0, rl.Join(1));
        Assert.AreEqual(1, rl.Join(2));
        Assert.AreEqual("Idle", rl.Phase);
        Assert.AreEqual("Ok/Ok", rl.Bets(1, 10.0, RBet("Red", 0, 1000)));
        Assert.AreEqual("Betting", rl.Phase);
        Assert.AreEqual(35.0, rl.Due, 1e-9, "25 s from the first bet");
        Assert.AreEqual(1000L, rl.Total(1));

        Assert.AreEqual("Ok", rl.Ready(1, 11.0));
        Assert.IsTrue((bool)Inv(rl.Table, "IsReady", 1));
        Assert.AreEqual("Ok/Ok", rl.Bets(1, 12.0, RBet("Straight", 17, 500), RBet("Dozen", 1, 2000)));
        Assert.AreEqual(2500L, rl.Total(1), "the new set replaced the old one, it was not added to it");
        Assert.AreEqual(2, rl.BetCount(1));
        Assert.IsFalse((bool)Inv(rl.Table, "IsReady", 1), "changing the set takes the Ready back");
        var evs = rl.Tick(12.0);
        Assert.IsTrue(evs.Any(e => e.Kind == "ReadyChanged" && !e.Flag));
        Assert.AreEqual(35.0, rl.Due, 1e-9, "a second set does not restart the clock");

        // Refused sets change nothing.
        Assert.AreEqual("BadBets/AboveSpotMaximum", rl.Bets(1, 13.0, RBet("Straight", 3, 10100)));
        Assert.AreEqual("BadBets/BadAmount", rl.Bets(1, 13.0, RBet("Red", 0, 150)));
        Assert.AreEqual("BadBets/BadSpot", rl.Bets(1, 13.0, RBet("Dozen", 3, 100)));
        Assert.AreEqual("BadBets/AboveTableMaximum", rl.Bets(1, 13.0, RBet("Red", 0, 50000), RBet("Black", 0, 50000), RBet("Odd", 0, 50000)));
        Assert.AreEqual("BadBets/AboveSpotMaximum", rl.Bets(1, 13.0, RBet("Red", 0, 30000), RBet("Red", 0, 30000)), "stacks on one spot are added up");
        Assert.AreEqual(2500L, rl.Total(1), "still the last good set");
        var tooMany = Enumerable.Range(0, 65).Select(i => RBet("Straight", i % 38, 100)).ToArray();
        Assert.AreEqual("BadBets/TooManyBets", rl.Bets(1, 13.0, tooMany));

        // Each participant has their own $1,000 a spin.
        Assert.AreEqual("Ok/Ok", rl.Bets(2, 14.0, RBet("Red", 0, 50000), RBet("Black", 0, 50000)));
        Assert.AreEqual("Ok/Ok", rl.Bets(1, 14.0, RBet("Odd", 0, 50000), RBet("Even", 0, 50000)));
        Assert.AreEqual("NotSeated/Ok", rl.Bets(9, 14.0, RBet("Red", 0, 100)));

        // Clearing a set takes the chips off; clearing the last one stops the clock.
        Assert.AreEqual("Ok", rl.Clear(1, 15.0));
        Assert.AreEqual(0, rl.BetCount(1));
        Assert.AreEqual("Betting", rl.Phase, "participant 2 still has chips down");
        Assert.AreEqual("Ok", rl.Clear(2, 16.0));
        Assert.AreEqual("Idle", rl.Phase);
        Assert.IsTrue(double.IsInfinity(rl.Due));
        Assert.AreEqual("Ok", rl.Clear(2, 17.0), "clearing nothing is fine");
        Assert.AreEqual("WrongPhase", rl.Ready(1, 17.0), "Ready is a Betting thing");

        // Once the betting clock has run out, a new set is refused.
        Assert.AreEqual("Ok/Ok", rl.Bets(1, 20.0, RBet("Red", 0, 100)));
        Assert.AreEqual("TooLate/Ok", rl.Bets(1, 45.5, RBet("Red", 0, 100)));
    }

    [Test]
    public void ASpinRunsThroughItsPhasesOnTheClock()
    {
        var rl = new Rl(17);
        rl.Join(1);
        Assert.AreEqual("Joined", Kinds(rl.Tick(5)));
        Assert.AreEqual(0, rl.Tick(6).Count);
        rl.Bets(1, 10.0, RBet("Straight", 17, 1000));
        rl.Tick(10.0);
        Assert.AreEqual(0, rl.Tick(34.9).Count);
        Assert.AreEqual(0, rl.Draws, "the pocket is not drawn before the lock");
        var snap = rl.Snapshot();
        Assert.AreEqual(255, G<byte>(snap, "pocket"), "nothing to see yet");

        var evs = rl.Tick(35.0);
        Assert.AreEqual("PhaseChanged,Charged,PhaseChanged,Spun", Kinds(evs));
        Assert.AreEqual("Locked", evs[0].Phase);
        Assert.AreEqual(1, evs[1].Key);
        Assert.AreEqual(1000L, evs[1].Cents);
        Assert.AreEqual("Spinning", evs[2].Phase);
        Assert.AreEqual(42.0, evs[2].Deadline, 1e-9, "7 s");
        Assert.AreEqual(17, evs[3].Pocket);
        Assert.AreEqual(35.0, evs[3].Time, 1e-9, "the start time presenters animate from");
        Assert.AreEqual(1, rl.Draws);
        Assert.AreEqual(1000L, rl.Charged);
        snap = rl.Snapshot();
        Assert.AreEqual(17, G<byte>(snap, "pocket"));
        Assert.AreEqual(35.0, G<double>(snap, "spinStart"), 1e-9);
        Assert.AreEqual(0.0, (double)Inv(snap, "SpinFraction", 35.0), 1e-9);
        Assert.AreEqual(0.5, (double)Inv(snap, "SpinFraction", 38.5), 1e-9);
        Assert.AreEqual(1.0, (double)Inv(snap, "SpinFraction", 99.0), 1e-9);
        Assert.AreEqual("WrongPhase/Ok", rl.Bets(1, 36.0, RBet("Red", 0, 100)), "no bets once the wheel is turning");

        Assert.AreEqual(0, rl.Tick(41.9).Count);
        evs = rl.Tick(42.0);
        Assert.AreEqual("ParticipantSettled,RoundSettled,PhaseChanged", Kinds(evs));
        Assert.AreEqual("Result", rl.Phase);
        Assert.AreEqual(36000L, evs[0].Cents, "35:1 and the stake back");
        Assert.AreEqual(35000L, evs[0].Net);
        Assert.AreEqual(17, evs[1].Pocket);
        Assert.AreEqual(48.0, rl.Due, 1e-9, "6 s of result");
        CollectionAssert.AreEqual(new byte[] { 17 }, (byte[])Inv(rl.Table, "History"));

        Assert.AreEqual(0, rl.Tick(47.9).Count);
        evs = rl.Tick(48.0);
        Assert.AreEqual("Idle", rl.Phase);
        Assert.AreEqual(255, G<byte>(rl.Snapshot(), "pocket"));
        Assert.AreEqual(0, rl.BetCount(1), "the chips come off");
        Assert.IsTrue((bool)Inv(rl.Table, "IsParticipant", 1), "the player stays at the wheel for the next spin");
        Assert.AreEqual("Ok/Ok", rl.Bets(1, 49.0, RBet("Red", 0, 100)));
        Assert.AreEqual(rl.Charged - rl.Credited, -rl.NetSum);
    }

    [Test]
    public void BettingEndsEarlyWhenEveryParticipantIsReady()
    {
        var rl = new Rl(5);
        rl.Join(1); rl.Join(2); rl.Join(3);
        rl.Bets(1, 1.0, RBet("Red", 0, 100));
        rl.Bets(2, 2.0, RBet("Black", 0, 100));
        Assert.AreEqual("Ok", rl.Ready(1, 3.0));
        Assert.AreEqual("Ok", rl.Ready(2, 4.0));
        rl.Tick(4.0);
        Assert.AreEqual("Betting", rl.Phase, "participant 3 has not pressed Ready");
        Assert.AreEqual(26.0, rl.Due, 1e-9);
        Assert.AreEqual("Ok", rl.Ready(3, 6.0), "a participant with no bets can be Ready too");
        var evs = rl.Tick(6.0);
        Assert.AreEqual("Spinning", rl.Phase);
        Assert.AreEqual(6.0, evs.Single(e => e.Kind == "Spun").Time, 1e-9, "the lock came the moment the last Ready did");

        // Leaving also counts: the last participant who was holding it up goes.
        rl = new Rl(5);
        rl.Join(1); rl.Join(2);
        rl.Bets(1, 1.0, RBet("Red", 0, 100));
        rl.Ready(1, 2.0);
        rl.Tick(2.0);
        Assert.AreEqual("Betting", rl.Phase);
        rl.Leave(2, 3.0);
        rl.Tick(3.0);
        Assert.AreEqual("Spinning", rl.Phase, "nobody left to wait for");

        // The clock still ends it when someone dawdles.
        rl = new Rl(5);
        rl.Join(1); rl.Join(2);
        rl.Bets(1, 100.0, RBet("Red", 0, 100));
        rl.Ready(1, 101.0);
        rl.Tick(124.9);
        Assert.AreEqual("Betting", rl.Phase);
        rl.Tick(125.0);
        Assert.AreEqual("Spinning", rl.Phase);
    }

    [Test]
    public void AnUnaffordableSetIsCancelledAtTheLockAndTheRestSpin()
    {
        var rl = new Rl(17);
        rl.Join(1); rl.Join(2); rl.Join(3);
        rl.Bets(1, 0, RBet("Red", 0, 1000));
        rl.Bets(2, 0, RBet("Straight", 17, 500));
        rl.Bets(3, 0, RBet("Black", 0, 700));
        rl.Broke.Add(2);
        var evs = rl.Tick(25.0);
        Assert.AreEqual("Spinning", rl.Phase);
        var cancelled = evs.Single(e => e.Kind == "SetCancelled");
        Assert.AreEqual(2, cancelled.Key);
        Assert.AreEqual(500L, cancelled.Cents);
        Assert.AreEqual(2, evs.Count(e => e.Kind == "Charged"));
        Assert.AreEqual(1700L, rl.Charged, "only the sets that were accepted");
        Assert.AreEqual(0, rl.BetCount(2), "the cancelled set is gone from the felt");
        evs = rl.Tick(32.0);
        Assert.AreEqual(2, evs.Count(e => e.Kind == "ParticipantSettled"), "nothing is paid to the one who was cancelled");
        Assert.IsFalse(evs.Any(e => e.Kind == "ParticipantSettled" && e.Key == 2));
        Assert.AreEqual(rl.Charged - rl.Credited, -rl.NetSum);

        // If nobody can pay there is no spin at all.
        rl = new Rl(17);
        rl.Join(1); rl.Join(2);
        rl.Bets(1, 0, RBet("Red", 0, 1000));
        rl.Bets(2, 0, RBet("Red", 0, 1000));
        rl.Broke.Add(1); rl.Broke.Add(2);
        evs = rl.Tick(25.0);
        Assert.AreEqual("Idle", rl.Phase);
        Assert.AreEqual(2, evs.Count(e => e.Kind == "SetCancelled"));
        Assert.IsFalse(evs.Any(e => e.Kind == "Spun"));
        Assert.AreEqual(0, ((byte[])Inv(rl.Table, "History")).Length, "no spin, no history");
        Assert.AreEqual(0L, rl.Charged);
    }

    [Test]
    public void SeveralParticipantsPayOutAndTheMoneyIsConserved()
    {
        var rl = new Rl(17); // black, odd, 2nd dozen, column 2 (2,5,8,...,17)
        for (int k = 1; k <= 4; k++) rl.Join(k);
        rl.Bets(1, 0, RBet("Straight", 17, 1000));
        rl.Bets(2, 0, RBet("Red", 0, 2000), RBet("Straight", 3, 500));
        rl.Bets(3, 0, RBet("Black", 0, 3000));
        rl.Bets(4, 0, RBet("Column", 1, 1000));
        for (int k = 1; k <= 4; k++) rl.Ready(k, 1.0);
        double now = 1.0;
        rl.Tick(now);
        rl.RunTo("Result", ref now);
        var paid = rl.Log.Where(e => e.Kind == "ParticipantSettled").ToDictionary(e => e.Key);
        Assert.AreEqual(36000L, paid[1].Cents);
        Assert.AreEqual(0L, paid[2].Cents);
        Assert.AreEqual(6000L, paid[3].Cents);
        Assert.AreEqual(3000L, paid[4].Cents, "a column pays 2:1");
        Assert.AreEqual(35000L, paid[1].Net);
        Assert.AreEqual(-2500L, paid[2].Net);
        Assert.AreEqual(3000L, paid[3].Net);
        Assert.AreEqual(2000L, paid[4].Net);
        Assert.AreEqual(7500L, rl.Charged);
        Assert.AreEqual(45000L, rl.Credited);
        Assert.AreEqual(rl.Charged - rl.Credited, -rl.NetSum);
        Assert.AreEqual(37500L, rl.Log.Single(e => e.Kind == "RoundSettled").Net);

        // The same bets resolved by the single-wheel rules agree to the cent.
        var r = Static("RouletteRules", "Resolve", RBets(RBet("Red", 0, 2000), RBet("Straight", 3, 500)), 2, 17);
        Assert.AreEqual(G<long>(r, "returnCents"), paid[2].Cents);
        Assert.AreEqual(2500L, G<long>(r, "stakedCents"));
    }

    [Test]
    public void LeavingTheWheelDropsAnOpenSetButAChargedOneStays()
    {
        var rl = new Rl(17);
        Assert.AreEqual(0, rl.Join(1));
        Assert.AreEqual(1, rl.Join(2));
        Assert.AreEqual(2, rl.Join(3));
        Assert.AreEqual(3, rl.Join(4));
        Assert.AreEqual(-1, rl.Join(5), "four at the wheel is the cap");
        Assert.AreEqual(-1, rl.Join(1), "already there");
        Assert.AreEqual(-1, rl.Join(-1));

        // Colours are stable while others come and go.
        Assert.IsTrue(rl.Leave(2, 1.0));
        Assert.AreEqual(0, rl.Colour(1)); Assert.AreEqual(2, rl.Colour(3)); Assert.AreEqual(3, rl.Colour(4));
        Assert.AreEqual(1, rl.Join(6), "the freed colour is reused");
        Assert.IsFalse(rl.Leave(99, 1.0));

        // Leaving during Betting drops the set, charges nothing, and is no one's win.
        rl.Bets(1, 2.0, RBet("Red", 0, 1000));
        rl.Bets(3, 2.0, RBet("Black", 0, 1000));
        Assert.IsTrue(rl.Leave(1, 3.0));
        var evs = rl.Tick(3.0);
        Assert.IsTrue(evs.Any(e => e.Kind == "SetDropped" && e.Key == 1));
        Assert.IsFalse(evs.Last(e => e.Kind == "Left").Flag);
        Assert.AreEqual("Betting", rl.Phase);
        Assert.AreEqual(0L, rl.Charged);
        Assert.IsTrue(rl.Leave(3, 4.0));
        rl.Tick(4.0);
        Assert.AreEqual("Idle", rl.Phase, "the last chips left the felt");

        // After the lock it stands: left during the spin, the bet pays in absentia and the colour is held until the chips come off.
        rl.Bets(4, 10.0, RBet("Straight", 17, 1000));
        rl.Bets(6, 10.0, RBet("Red", 0, 1000));
        int colour4 = rl.Colour(4);
        rl.Ready(4, 11.0); rl.Ready(6, 11.0);
        rl.Tick(11.0);
        Assert.AreEqual("Spinning", rl.Phase);
        Assert.IsTrue(rl.Leave(4, 12.0));
        evs = rl.Tick(12.0);
        Assert.IsTrue(evs.Last(e => e.Kind == "Left").Flag);
        Assert.AreEqual(-1, rl.Colour(4));
        int newcomer = rl.Join(7, 12.5);
        Assert.AreNotEqual(colour4, newcomer, "the leaver's chips are still on the felt in their colour");
        var snap = rl.Snapshot();
        var ps = ((Array)G<object>(snap, "participants")).Cast<object>().ToList();
        var ghost = ps.Single(p => G<int>(p, "key") == 4);
        Assert.AreEqual(2, G<byte>(ghost, "flags") & 2, "marked absent");
        Assert.AreEqual(1, ((Array)G<object>(ghost, "bets")).Length, "their chips are still shown");

        double now = 12.5;
        rl.RunTo("Result", ref now);
        var pay = rl.Log.Single(e => e.Kind == "ParticipantSettled" && e.Key == 4);
        Assert.IsTrue(pay.Flag, "in absentia");
        Assert.AreEqual(36000L, pay.Cents);
        rl.RunTo("Idle", ref now);
        Assert.AreEqual(2, rl.Join(8, now), "the lowest free colour");
        Assert.AreEqual(colour4, rl.Join(9, now), "the leaver's colour is free again once the round is cleared");
        Assert.AreEqual(rl.Charged - rl.Credited, -rl.NetSum);

        // Someone who leaves during the spin with nothing charged just goes.
        var rl2 = new Rl(3);
        rl2.Join(1); rl2.Join(2);
        rl2.Bets(1, 0, RBet("Red", 0, 100));
        rl2.Ready(1, 0.5); rl2.Ready(2, 0.5);
        rl2.Tick(0.5);
        Assert.AreEqual("Spinning", rl2.Phase);
        Assert.IsTrue(rl2.Leave(2, 1.0));
        Assert.IsFalse(rl2.Tick(1.0).Last(e => e.Kind == "Left").Flag);
        Assert.AreEqual(1, rl2.Join(3, 1.0), "their colour is free at once");
    }

    [Test]
    public void ThePocketIsUniformOverThreeHundredEightyThousandSpins()
    {
        // The real wheel behind the real table: one player, one Ready, one spin at a time.
        long spins = 380000;
        var counts = (long[])Static("RouletteTableSim", "PocketHistogram", spins, Dice(77));
        Assert.AreEqual(38, counts.Length);
        Assert.AreEqual(spins, counts.Sum());
        double expected = spins / 38.0, chi = 0;
        foreach (long c in counts) chi += (c - expected) * (c - expected) / expected;
        UnityEngine.Debug.Log("[CasinoSharedTableTests] roulette table: " + spins + " spins, chi-square " + chi.ToString("F2") + " (37 dof), min " + counts.Min() + ", max " + counts.Max());
        Assert.Less(chi, 69.3, "the pocket is not uniform");
        Assert.Greater(chi, 12.0, "too even to be random");
    }

    [Test]
    public void RouletteSnapshotsRoundTripAndKeepTheLastTwelvePockets()
    {
        var script = new[] { 0, 37, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        var rl = new Rl(script);
        rl.Join(1); rl.Join(2);
        double now = 0;
        int maxBytes = 0;
        for (int spin = 0; spin < script.Length; spin++)
        {
            rl.Bets(1, now, RBet("Straight", spin % 36 + 1, 100), RBet("Red", 0, 200));
            if (spin % 2 == 0) rl.Bets(2, now, RBet("Split", 5, 300), RBet("Corner", 4, 100), RBet("TopLine", 0, 100));
            rl.Tick(now);
            foreach (string phase in new[] { "Betting", "Spinning", "Result" })
            {
                rl.RunTo(phase, ref now);
                var snap = rl.Snapshot();
                var bytes = (byte[])Inv(snap, "Serialize");
                maxBytes = Math.Max(maxBytes, bytes.Length);
                Assert.AreEqual(0xB2, bytes[0]);
                Assert.AreEqual(1, bytes[1]);
                var back = Static("RouletteTableSnapshot", "Deserialize", (object)bytes);
                CollectionAssert.AreEqual(bytes, (byte[])Inv(back, "Serialize"), "round trip in " + phase);
                Assert.AreEqual(phase, G<object>(back, "phase").ToString());
                if (phase == "Betting") Assert.AreEqual(255, G<byte>(back, "pocket"), "no pocket while betting");
                else Assert.AreEqual(script[spin], G<byte>(back, "pocket"));
                var ps = (Array)G<object>(back, "participants");
                Assert.AreEqual(2, ps.Length);
                Assert.AreEqual(0, G<byte>(ps.GetValue(0), "colour"));
                Assert.AreEqual(1, G<byte>(ps.GetValue(1), "colour"));
                if (phase == "Betting")
                {
                    Assert.AreEqual(2, ((Array)G<object>(ps.GetValue(0), "bets")).Length, "chips are public so every machine can draw them");
                    Assert.AreEqual(200 + 100, G<int>(ps.GetValue(0), "stakedCents") + (int)(long)Inv(rl.Table, "TotalOf", 1), "sanity: the set total");
                }
            }
            rl.RunTo("Idle", ref now);
        }
        var history = (byte[])Inv(rl.Table, "History");
        CollectionAssert.AreEqual(script.Skip(script.Length - 12).Select(p => (byte)p).ToArray(), history, "the last twelve, oldest first");
        var finalSnap = rl.Snapshot();
        CollectionAssert.AreEqual(history, G<byte[]>(finalSnap, "history"));

        UnityEngine.Debug.Log("[CasinoSharedTableTests] roulette snapshot: up to " + maxBytes + " bytes with two players, empty " + new Rl(1).Bytes().Length + " bytes");
        var good = rl.Bytes();
        foreach (var bad in new[] { new byte[0], good.Take(good.Length - 1).ToArray(), good.Concat(new byte[] { 7 }).ToArray(), new byte[] { 0xB1, 1 }.Concat(good.Skip(2)).ToArray(), new byte[] { 0xB2, 9 }.Concat(good.Skip(2)).ToArray() })
            Assert.IsFalse((bool)Static("RouletteTableSnapshot", "TryDeserialize", new object[] { bad, null }));
    }

    [Test]
    public void RouletteMoneyIsConservedOverChaos()
    {
        object audit = Static("RouletteTableSim", "Chaos", 30000L, Dice(404), Dice(405));
        long charged = G<long>(audit, "chargedCents"), credited = G<long>(audit, "creditedCents"), net = G<long>(audit, "settledNetCents");
        UnityEngine.Debug.Log("[CasinoSharedTableTests] roulette chaos: " + G<long>(audit, "spins") + " spins, charged $" + charged / 100.0 + ", credited $" + credited / 100.0 + ", net $" + net / 100.0 + "; " +
                              G<long>(audit, "cancelled") + " cancelled, " + G<long>(audit, "dropped") + " dropped, " + G<long>(audit, "absentSettles") + " in absentia, " + G<long>(audit, "snapshots") + " snapshots checked");
        Assert.IsNull(G<string>(audit, "firstViolation"), "violation: " + G<string>(audit, "firstViolation"));
        Assert.AreEqual(0L, G<long>(audit, "violations"));
        Assert.GreaterOrEqual(G<long>(audit, "spins"), 30000L);
        Assert.AreEqual(-net, charged - credited);
        Assert.IsTrue(G<bool>(audit, "Balanced"));
        Assert.Greater(G<long>(audit, "cancelled"), 100L);
        Assert.Greater(G<long>(audit, "dropped"), 100L);
        Assert.Greater(G<long>(audit, "absentSettles"), 50L);
    }
}

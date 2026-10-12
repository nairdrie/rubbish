using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The pure parts of the in-world casino controllers (Docs/Casino.md §12): chip selection, the pending blackjack stake and when it goes out,
/// the legal actions / costs / advice a client works out from a PUBLIC snapshot (checked against the host's own table over thousands of
/// seeded decisions), the 3D plate states, the roulette bet book and its reconciliation with the host's set, auto-spin pacing, the hint strip
/// markup and the bar's words. Reflection because the code lives in Assembly-CSharp (like the other Casino* fixtures).
/// </summary>
public sealed class CasinoControllerTests
{
    // ================================================================================================ reflection plumbing

    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name, params object[] args) => Activator.CreateInstance(T(name), args);
    static object E(string type, string value) => Enum.Parse(T(type), value);
    static object E(string type, int value) => Enum.ToObject(T(type), value);

    static object Call(object target, string method, params object[] args)
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

    static int I(object enumOrInt) => Convert.ToInt32(enumOrInt);

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

    static readonly int[] BjRack = { 500, 1000, 2500, 5000, 10000 };
    static readonly int[] RlRack = { 100, 500, 2500, 10000 };

    // ================================================================================================ chip selection

    [Test]
    public void ChipSelector_cycles_with_wraparound_and_clamps_direct_selection()
    {
        var c = New("ChipSelector", new object[] { BjRack, 0 });
        Assert.AreEqual(500, G<int>(c, "Cents"));
        Assert.AreEqual(4, Call(c, "Step", -1), "previous from the first wraps to the last");
        Assert.AreEqual(10000, G<int>(c, "Cents"));
        Assert.AreEqual(0, Call(c, "Step", 1), "next from the last wraps to the first");
        Assert.IsTrue((bool)Call(c, "Select", 2));
        Assert.IsFalse((bool)Call(c, "Select", 2), "same chip: no change");
        Call(c, "Select", 99);
        Assert.AreEqual(4, G<int>(c, "Index"), "clamped to the rack");
        Call(c, "Select", -3);
        Assert.AreEqual(0, G<int>(c, "Index"));
        Assert.AreEqual(2500, Call(c, "CentsAt", 2));
    }

    [Test]
    public void ChipSelector_picks_the_largest_chip_a_player_can_cover()
    {
        var c = New("ChipSelector", new object[] { BjRack, 0 });
        Call(c, "SelectLargestAtMost", 3000L);
        Assert.AreEqual(2500, G<int>(c, "Cents"));
        Call(c, "SelectLargestAtMost", 100L);
        Assert.AreEqual(500, G<int>(c, "Cents"), "nothing fits: the smallest chip");
        Call(c, "SelectLargestAtMost", 1000000L);
        Assert.AreEqual(10000, G<int>(c, "Cents"));
    }

    [Test]
    public void Decompose_splits_a_total_into_rack_chips_largest_first()
    {
        Assert.AreEqual(new[] { 2500, 1000 }, ((IEnumerable<int>)Static("ChipSelector", "Decompose", 3500L, BjRack)).ToArray());
        Assert.AreEqual(new[] { 10000, 10000 }, ((IEnumerable<int>)Static("ChipSelector", "Decompose", 20000L, BjRack)).ToArray());
        Assert.AreEqual(new[] { 5000, 2500, 1000, 500 }, ((IEnumerable<int>)Static("ChipSelector", "Decompose", 9000L, BjRack)).ToArray());
        Assert.AreEqual(new[] { 10000, 2500, 100, 100 }, ((IEnumerable<int>)Static("ChipSelector", "Decompose", 12700L, RlRack)).ToArray());
        Assert.AreEqual(0, ((IEnumerable<int>)Static("ChipSelector", "Decompose", 300L, BjRack)).Count(), "below the smallest chip: nothing");
    }

    // ================================================================================================ the pending blackjack stake

    static object NewBet() => New("PendingBet");

    [Test]
    public void PendingBet_stacks_chips_and_sums_the_total()
    {
        var b = NewBet();
        Assert.AreEqual("Added", Call(b, "Add", 2500, 100000L, 20000L).ToString());
        Assert.AreEqual("Added", Call(b, "Add", 1000, 100000L, 20000L).ToString());
        Assert.AreEqual(3500L, G<long>(b, "Total"));
        Assert.AreEqual(2, G<int>(b, "Count"));
        Assert.IsTrue(G<bool>(b, "Legal"));
    }

    [Test]
    public void PendingBet_refuses_past_the_table_maximum_and_past_the_cash_in_hand()
    {
        var b = NewBet();
        for (int i = 0; i < 2; i++) Assert.AreEqual("Added", Call(b, "Add", 10000, 1000000L, 20000L).ToString());
        Assert.AreEqual("AtMaximum", Call(b, "Add", 500, 1000000L, 20000L).ToString(), "$200 is the limit");
        Assert.AreEqual(20000L, G<long>(b, "Total"));

        var c = NewBet();
        Assert.AreEqual("Added", Call(c, "Add", 2500, 3000L, 20000L).ToString());
        Assert.AreEqual("CantAfford", Call(c, "Add", 2500, 3000L, 20000L).ToString(), "$25 + $25 > $30 in hand");
        Assert.AreEqual("Added", Call(c, "Add", 500, 3000L, 20000L).ToString(), "a $5 chip still fits");
        Assert.AreEqual(3000L, G<long>(c, "Total"));
        Assert.AreEqual("BadChip", Call(c, "Add", 150, 99999L, 20000L).ToString(), "no half-dollar chips");
    }

    [Test]
    public void PendingBet_removes_the_last_chip_clears_and_restores_a_previous_total()
    {
        var b = NewBet();
        Call(b, "Add", 2500, 100000L, 20000L); Call(b, "Add", 500, 100000L, 20000L);
        var args = new object[] { 0 };
        Assert.IsTrue((bool)b.GetType().GetMethod("RemoveLast").Invoke(b, args));
        Assert.AreEqual(500, (int)args[0]);
        Assert.AreEqual(2500L, G<long>(b, "Total"));
        Call(b, "Clear");
        Assert.IsTrue(G<bool>(b, "IsEmpty"));
        Assert.IsFalse((bool)b.GetType().GetMethod("RemoveLast").Invoke(b, new object[] { 0 }), "nothing left to take off");
        Assert.AreEqual(3500L, Call(b, "SetTotal", 3500L, BjRack), "same bet again");
        Assert.AreEqual(2, G<int>(b, "Count"));
        Assert.AreEqual(20000L, Call(b, "SetTotal", 99999L, BjRack), "clamped to the table maximum");
    }

    [Test]
    public void PendingBet_check_says_why_a_stack_cannot_go_out()
    {
        var b = NewBet();
        Assert.AreEqual("BelowMinimum", Call(b, "Check", 100000L).ToString(), "empty");
        Call(b, "Add", 500, 100000L, 20000L);
        Assert.AreEqual("Ok", Call(b, "Check", 100000L).ToString());
        Assert.AreEqual("CantAfford", Call(b, "Check", 400L).ToString());
        Assert.IsTrue(((string)Static("PendingBet", "Say", E("PendingAdd", "AtMaximum"), 500, 100L)).Contains("$200"));
        Assert.IsTrue(((string)Static("PendingBet", "Say", E("CasinoTableError", "BelowMinimum"), 100L)).Contains("$5"));
    }

    [Test]
    public void BetTiming_sends_a_legal_stack_once_in_the_last_second_of_betting()
    {
        var betting = E("BlackjackTablePhase", "Betting");
        bool Auto(object phase, double left, bool legal = true, bool placed = false, bool outHand = false, bool inFlight = false) =>
            (bool)Static("BetTiming", "ShouldAutoSend", phase, left, legal, placed, outHand, inFlight);
        Assert.IsFalse(Auto(betting, 5.0), "plenty of time");
        Assert.IsTrue(Auto(betting, 0.9));
        Assert.IsTrue(Auto(betting, 1.0));
        Assert.IsFalse(Auto(betting, 0.0), "out of time: nothing to send");
        Assert.IsFalse(Auto(betting, -0.5));
        Assert.IsFalse(Auto(betting, 0.5, legal: false), "an illegal stack is never sent");
        Assert.IsFalse(Auto(betting, 0.5, placed: true), "one stake per round");
        Assert.IsFalse(Auto(betting, 0.5, outHand: true));
        Assert.IsFalse(Auto(betting, 0.5, inFlight: true), "one request at a time");
        Assert.IsFalse(Auto(E("BlackjackTablePhase", "Idle"), 0.5), "the clock only runs in Betting");
        Assert.IsFalse(Auto(E("BlackjackTablePhase", "Turns"), 0.5));
    }

    // ================================================================================================ legal actions from the public snapshot

    sealed class Table
    {
        public readonly object T = New("BlackjackTable");
        public readonly Func<int, int> dice;
        public double now = 1000.0;
        public long charged;
        public Table(ulong seed) { dice = Dice(seed); }
        public Func<long, bool> Till => c => { charged += c; return true; };
        public void Tick() { Call(T, "Tick", now, dice); }
        public object Snap => Call(T, "Snapshot");
    }

    static bool TryAdvice(object table, int key, out int action)
    {
        var args = new object[] { key, null };
        bool ok = (bool)table.GetType().GetMethod("TryAdvice").Invoke(table, args);
        action = ok ? I(args[1]) : -1;
        return ok;
    }

    static bool TryAdviceSnap(object snap, int seat, out int action)
    {
        var m = T("BlackjackSnapshotRules").GetMethod("TryAdvice");
        var args = new object[] { snap, seat, null };
        bool ok = (bool)m.Invoke(null, args);
        action = ok ? I(args[2]) : -1;
        return ok;
    }

    [Test]
    public void Snapshot_rules_agree_with_the_hosts_table_on_every_decision_of_3000_rounds()
    {
        int decisions = 0, splits = 0, doubles = 0, insurances = 0, advised = 0;
        var tb = new Table(7);
        int[] keys = { 11, 22, 33 };
        for (int k = 0; k < keys.Length; k++) Call(tb.T, "Sit", keys[k], -1, tb.now);
        var pick = Dice(99);
        for (int round = 0; round < 3000; round++)
        {
            foreach (int key in keys) Call(tb.T, "PlaceBet", key, (long)(500 * (1 + pick(8))), tb.now, tb.Till);
            for (int step = 0; step < 4000; step++)
            {
                tb.now += 0.2; tb.Tick();
                string phase = G<object>(tb.Snap, "phase").ToString();
                if (phase == "Idle") break;
                foreach (int key in keys)
                {
                    object snap = tb.Snap; // fresh: the previous player's action changed the table
                    int seat = (int)Static("BlackjackSnapshotRules", "SeatOf", snap, key);
                    Assert.GreaterOrEqual(seat, 0);
                    int hostLegal = I(Call(tb.T, "LegalActionsFor", key));
                    int mine = I(Static("BlackjackSnapshotRules", "Legal", snap, seat));
                    Assert.AreEqual(hostLegal, mine, "legal actions, round " + round + " phase " + phase + " key " + key);
                    for (int a = 0; a <= 5; a++)
                    {
                        long hostCost = (long)Call(tb.T, "CostOf", key, E("BlackjackAction", a));
                        long myCost = (long)Static("BlackjackSnapshotRules", "Cost", snap, seat, E("BlackjackAction", a));
                        Assert.AreEqual(hostCost, myCost, "cost of action " + a + ", round " + round + " phase " + phase);
                    }
                    bool hostAdv = TryAdvice(tb.T, key, out int ha);
                    bool myAdv = TryAdviceSnap(snap, seat, out int ma);
                    Assert.AreEqual(hostAdv, myAdv, "advice availability, round " + round);
                    if (hostAdv) { Assert.AreEqual(ha, ma, "basic strategy, round " + round + " phase " + phase); advised++; }
                    if (hostLegal != 0)
                    {
                        decisions++;
                        var options = Enumerable.Range(0, 6).Where(a => (hostLegal & (1 << a)) != 0).ToArray();
                        int choose = options[pick(options.Length)];
                        if (choose == 3) splits++; else if (choose == 2) doubles++; else if (choose == 4) insurances++;
                        Call(tb.T, "Act", key, E("BlackjackAction", choose), tb.now, tb.Till);
                    }
                }
            }
            tb.now += 10; tb.Tick();
        }
        Assert.Greater(decisions, 5000, "enough decisions to mean something");
        Assert.Greater(splits, 50); Assert.Greater(doubles, 100); Assert.Greater(insurances, 20); Assert.Greater(advised, 5000);
    }

    [Test]
    public void BetPlaced_and_CanBet_follow_the_phase_the_stake_and_the_clock()
    {
        var tb = new Table(3);
        Call(tb.T, "Sit", 5, -1, tb.now);
        Call(tb.T, "Sit", 6, -1, tb.now);
        object snap = tb.Snap;
        Assert.IsTrue((bool)Static("BlackjackSnapshotRules", "CanBet", snap, 0, double.PositiveInfinity), "Idle: anyone seated may bet");
        Assert.IsFalse((bool)Static("BlackjackSnapshotRules", "CanBet", snap, 3, double.PositiveInfinity), "an empty seat cannot");
        Call(tb.T, "PlaceBet", 5, 2500L, tb.now, tb.Till);
        snap = tb.Snap;
        Assert.AreEqual("Betting", G<object>(snap, "phase").ToString());
        Assert.IsTrue((bool)Static("BlackjackSnapshotRules", "BetPlaced", snap, 0));
        Assert.IsFalse((bool)Static("BlackjackSnapshotRules", "BetPlaced", snap, 1));
        Assert.IsFalse((bool)Static("BlackjackSnapshotRules", "CanBet", snap, 0, 10.0), "one stake per round");
        Assert.IsTrue((bool)Static("BlackjackSnapshotRules", "CanBet", snap, 1, 10.0));
        Assert.IsFalse((bool)Static("BlackjackSnapshotRules", "CanBet", snap, 1, 0.0), "the clock is out");
        Call(tb.T, "SitOut", 6, tb.now);
        Assert.IsFalse((bool)Static("BlackjackSnapshotRules", "CanBet", tb.Snap, 1, 10.0), "sitting out");
    }

    // ================================================================================================ the plates

    static object Plate(string id, int legalMask, long stake, long cash) =>
        Static("BlackjackPlates", "For", id, E("BlackjackActions", legalMask), stake, cash);

    [Test]
    public void Plates_are_lit_and_enabled_exactly_for_the_legal_actions()
    {
        int hit = 1, stand = 2, dbl = 4, split = 8, ins = 16, noIns = 32;
        var turn = hit | stand | dbl | split;
        foreach (var id in new[] { "Hit", "Stand", "Double", "Split" })
        {
            var p = Plate(id, turn, 2500, 100000);
            Assert.IsTrue(G<bool>(p, "enabled") && G<bool>(p, "lit"), id + " on a turn with split and double");
        }
        Assert.IsFalse(G<bool>(Plate("Insurance", turn, 2500, 100000), "enabled"), "no insurance on a turn");
        var three = Plate("Hit", hit | stand, 2500, 100000);
        Assert.IsTrue(G<bool>(three, "enabled"));
        Assert.IsFalse(G<bool>(Plate("Double", hit | stand, 2500, 100000), "enabled"), "doubling is for the first two cards");
        Assert.IsFalse(G<bool>(Plate("Split", hit | stand | dbl, 2500, 100000), "enabled"));
        foreach (var id in CasinoIds()) Assert.IsFalse(G<bool>(Plate(id, 0, 2500, 100000), "enabled"), id + " is dark when it is not your turn");
        // insurance window: Insurance and the Stand plate (relabelled "no insurance")
        Assert.IsTrue(G<bool>(Plate("Insurance", ins | noIns, 2500, 100000), "enabled"));
        var no = Plate("Stand", ins | noIns, 2500, 100000);
        Assert.IsTrue(G<bool>(no, "enabled"));
        Assert.IsTrue(G<string>(no, "label").StartsWith("NO"));
        Assert.IsFalse(G<bool>(Plate("Hit", ins | noIns, 2500, 100000), "enabled"), "no hitting during insurance");
    }

    static IEnumerable<string> CasinoIds() => new[] { "Hit", "Stand", "Double", "Split", "Insurance" };

    [Test]
    public void Plates_show_the_cost_and_go_dark_when_the_player_cannot_cover_it()
    {
        var dbl = Plate("Double", 1 | 2 | 4, 2500, 100000);
        Assert.IsTrue(G<string>(dbl, "label").Contains("+$25"), G<string>(dbl, "label"));
        var broke = Plate("Double", 1 | 2 | 4, 2500, 2400);
        Assert.IsFalse(G<bool>(broke, "enabled"));
        Assert.IsFalse(G<bool>(broke, "lit"));
        Assert.IsTrue(G<string>(broke, "reason").Contains("$25"));
        var ins = Plate("Insurance", 16 | 32, 2500, 100000);
        Assert.IsTrue(G<string>(ins, "label").Contains("+$12.50"), "half the stake");
        Assert.IsFalse(G<bool>(Plate("Insurance", 16 | 32, 2500, 1000), "enabled"));
    }

    [Test]
    public void A_plate_maps_to_its_action_and_Stand_declines_insurance_while_the_window_is_open()
    {
        object Act(string id, int legal)
        {
            var m = T("BlackjackPlates").GetMethod("TryAction");
            var args = new object[] { id, E("BlackjackActions", legal), null };
            Assert.IsTrue((bool)m.Invoke(null, args));
            return args[2];
        }
        Assert.AreEqual("Hit", Act("Hit", 3).ToString());
        Assert.AreEqual("Stand", Act("Stand", 3).ToString());
        Assert.AreEqual("NoInsurance", Act("Stand", 16 | 32).ToString());
        Assert.AreEqual("Insurance", Act("Insurance", 16 | 32).ToString());
        Assert.AreEqual("Double", Act("Double", 7).ToString());
        Assert.AreEqual("Split", Act("Split", 15).ToString());
        Assert.IsFalse((bool)T("BlackjackPlates").GetMethod("TryAction").Invoke(null, new object[] { "Nope", E("BlackjackActions", 0), null }));
    }

    // ================================================================================================ auto-spin

    [Test]
    public void AutoSpin_starts_after_a_half_second_hold_and_paces_pulls_2_6_seconds_apart()
    {
        var a = New("AutoSpin");
        Assert.IsFalse((bool)Call(a, "Tick", true, 0.3f));
        Assert.IsTrue((bool)Call(a, "Tick", true, 0.25f), "held 0.55 s");
        Assert.IsFalse((bool)Call(a, "Tick", false, 0.016f), "letting go stops it");
        Assert.IsFalse(G<bool>(a, "Auto"));
        Assert.IsTrue((bool)Call(a, "CanPullNow", 100.0));
        Call(a, "MarkPull", 100.0);
        Assert.IsFalse((bool)Call(a, "CanPullNow", 102.5), "2.6 s between pulls");
        Assert.IsTrue((bool)Call(a, "CanPullNow", 102.6));
        Assert.AreEqual(1.0, (double)Call(a, "WaitSeconds", 101.6), 1e-6);
        Assert.IsFalse((bool)Call(a, "ShouldPull", true, 101.0), "a tap inside the spacing waits");
        Assert.IsTrue((bool)Call(a, "ShouldPull", true, 103.0));
        Call(a, "Tick", true, 0.6f);
        Assert.IsTrue((bool)Call(a, "ShouldPull", false, 103.0), "auto pulls by itself");
        Assert.IsFalse((bool)Call(a, "ShouldPull", false, 100.5));
        Call(a, "StopAuto");
        Assert.IsFalse((bool)Call(a, "ShouldPull", false, 105.0));
    }

    [Test]
    public void Slot_reveal_waits_for_the_last_reel_and_longer_for_a_tease()
    {
        double Reveal(int machine, int s0, int s1, int s2)
        {
            var e = New("SlotEventInfo");
            Put(e, "machine", (byte)machine); Put(e, "stop0", (byte)s0); Put(e, "stop1", (byte)s1); Put(e, "stop2", (byte)s2);
            return (float)Static("SlotRevealTiming", "Seconds", e);
        }
        // find a combination whose first two reels sit on the top symbol, and one that does not
        int top = Convert.ToInt32(Static("SlotRules", "TopSymbol", 0));
        int len0 = (int)Static("SlotRules", "ReelLength", 0, 0), len1 = (int)Static("SlotRules", "ReelLength", 0, 1);
        int a = -1, b = -1;
        for (int i = 0; i < len0 && a < 0; i++) if (Convert.ToInt32(Static("SlotRules", "SymbolAt", 0, 0, i, 0)) == top) a = i;
        for (int i = 0; i < len1 && b < 0; i++) if (Convert.ToInt32(Static("SlotRules", "SymbolAt", 0, 1, i, 0)) == top) b = i;
        Assert.GreaterOrEqual(a, 0); Assert.GreaterOrEqual(b, 0);
        Assert.Greater(Reveal(0, a, b, 0), Reveal(0, a == 0 ? 1 : 0, b == 0 ? 1 : 0, 0) + 0.5, "the tease takes its time");
        Assert.Less(Reveal(0, a == 0 ? 1 : 0, b == 0 ? 1 : 0, 0), 3.0);
    }

    static void Put(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);

    // ================================================================================================ the roulette bet book

    static object NewBook() => New("RouletteBetBook");
    static object Spot(string type, int index, int cents = 0) => New("RouletteBet", E("RouletteBetType", type), index, cents);

    static string TryAdd(object book, string type, int index, int cents, long cash, out string err)
    {
        var m = book.GetType().GetMethod("TryAdd");
        var args = new object[] { E("RouletteBetType", type), index, cents, cash, null };
        string r = m.Invoke(book, args).ToString();
        err = args[4].ToString();
        return r;
    }

    static object[] Bets(object book) => ((IEnumerable)Call(book, "ToBets")).Cast<object>().ToArray();

    [Test]
    public void Book_stacks_chips_per_spot_and_keeps_the_order_the_spots_were_first_covered()
    {
        var b = NewBook();
        Assert.AreEqual("Added", TryAdd(b, "Straight", 17, 500, 100000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Corner", 8, 100, 100000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Straight", 17, 2500, 100000, out _));
        var bets = Bets(b);
        Assert.AreEqual(2, bets.Length);
        Assert.AreEqual("Straight", G<object>(bets[0], "type").ToString());
        Assert.AreEqual(3000, G<int>(bets[0], "cents"));
        Assert.AreEqual(100, G<int>(bets[1], "cents"));
        Assert.AreEqual(3100L, G<long>(b, "Total"));
        Assert.AreEqual(2, (int)Call(b, "ChipsOnSpot", E("RouletteBetType", "Straight"), 17));
        Assert.AreEqual(3000L, (long)Call(b, "OnSpot", E("RouletteBetType", "Straight"), 17));
    }

    [Test]
    public void Book_enforces_the_roulette_limits_and_the_cash_with_the_rules_wording()
    {
        var b = NewBook();
        Assert.AreEqual("Added", TryAdd(b, "Straight", 5, 10000, 1000000, out _), "$100 is the inside maximum");
        Assert.AreEqual("Refused", TryAdd(b, "Straight", 5, 100, 1000000, out string err));
        Assert.AreEqual("AboveSpotMaximum", err);
        Assert.AreEqual("Inside spots max $100", Static("CasinoSeatText", "Say", E("RouletteBetError", "AboveSpotMaximum"), true));
        Assert.AreEqual("Outside spots max $500", Static("CasinoSeatText", "Say", E("RouletteBetError", "AboveSpotMaximum"), false));
        Assert.AreEqual("Added", TryAdd(b, "Red", 0, 10000, 1000000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Red", 0, 10000, 1000000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Red", 0, 10000, 1000000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Red", 0, 10000, 1000000, out _));
        Assert.AreEqual("Added", TryAdd(b, "Red", 0, 10000, 1000000, out _), "$500 outside");
        Assert.AreEqual("Refused", TryAdd(b, "Red", 0, 100, 1000000, out err));
        // the table maximum: $1,000 a spin
        var c = NewBook();
        for (int i = 0; i < 10; i++) Assert.AreEqual("Added", TryAdd(c, "Straight", i, 10000, 10000000, out _));
        Assert.AreEqual("Refused", TryAdd(c, "Straight", 20, 100, 10000000, out err));
        Assert.AreEqual("AboveTableMaximum", err);
        Assert.AreEqual("Table max $1,000 a spin", Static("CasinoSeatText", "Say", E("RouletteBetError", "AboveTableMaximum"), true));
        // cash
        var d = NewBook();
        Assert.AreEqual("Added", TryAdd(d, "Red", 0, 2500, 3000, out _));
        Assert.AreEqual("CantAfford", TryAdd(d, "Black", 0, 2500, 3000, out _));
        Assert.AreEqual(2500L, G<long>(d, "Total"), "a refused chip changes nothing");
    }

    [Test]
    public void Book_removes_the_last_chip_of_a_spot_undoes_in_order_and_clears()
    {
        var b = NewBook();
        TryAdd(b, "Straight", 1, 100, 100000, out _);
        TryAdd(b, "Straight", 2, 500, 100000, out _);
        TryAdd(b, "Straight", 1, 2500, 100000, out _);
        var m = b.GetType().GetMethod("RemoveLastAt");
        var args = new object[] { E("RouletteBetType", "Straight"), 1, 0 };
        Assert.IsTrue((bool)m.Invoke(b, args));
        Assert.AreEqual(2500, (int)args[2], "the chip put on this spot last comes off");
        Assert.AreEqual(100L, (long)Call(b, "OnSpot", E("RouletteBetType", "Straight"), 1));
        Assert.IsFalse((bool)m.Invoke(b, new object[] { E("RouletteBetType", "Straight"), 9, 0 }), "nothing on that spot");
        var u = b.GetType().GetMethod("Undo");
        var ua = new object[] { null, 0, 0 };
        Assert.IsTrue((bool)u.Invoke(b, ua));
        Assert.AreEqual(2, (int)ua[1]); Assert.AreEqual(500, (int)ua[2]);
        Assert.IsTrue((bool)Call(b, "Clear"));
        Assert.IsTrue(G<bool>(b, "IsEmpty"));
        Assert.AreEqual(0L, G<long>(b, "Total"));
        Assert.IsFalse((bool)Call(b, "Clear"), "nothing to clear");
    }

    [Test]
    public void Book_adopts_the_hosts_set_split_into_rack_chips_and_recognises_an_identical_set()
    {
        var host = new[] { Spot("Straight", 17, 3100), Spot("Red", 0, 500) };
        var arr = Array.CreateInstance(T("RouletteBet"), 2);
        for (int i = 0; i < 2; i++) arr.SetValue(host[i], i);
        var b = NewBook();
        Call(b, "Adopt", arr, RlRack);
        Assert.AreEqual(3600L, G<long>(b, "Total"));
        Assert.IsTrue((bool)Call(b, "SameAs", arr));
        Assert.AreEqual(4, G<int>(b, "ChipCount"), "$31 = $25 + $5 + $1, plus the $5 on red: stacks are split into chips so Undo goes one chip at a time");
        TryAdd(b, "Black", 0, 100, 100000, out _);
        Assert.IsFalse((bool)Call(b, "SameAs", arr));
        var other = Array.CreateInstance(T("RouletteBet"), 2);
        other.SetValue(Spot("Red", 0, 500), 0); other.SetValue(Spot("Straight", 17, 3100), 1);
        var c = NewBook(); Call(c, "Adopt", other, RlRack);
        Assert.IsTrue((bool)Call(c, "SameAs", arr), "order does not matter");
    }

    [Test]
    public void Rebet_puts_back_as_much_of_the_last_set_as_the_limits_and_the_cash_allow()
    {
        var set = Array.CreateInstance(T("RouletteBet"), 2);
        set.SetValue(Spot("Straight", 4, 2500), 0); set.SetValue(Spot("Red", 0, 5000), 1);
        var b = NewBook();
        Assert.AreEqual(7500L, Call(b, "Replace", set, 100000L, RlRack));
        var poor = NewBook();
        Assert.AreEqual(2500L, Call(poor, "Replace", set, 3000L, RlRack), "stops at the first chip it cannot cover");
        var again = NewBook();
        TryAdd(again, "Black", 0, 100, 100000, out _);
        Call(again, "Replace", set, 100000L, RlRack);
        Assert.AreEqual(7500L, G<long>(again, "Total"), "Rebet replaces what was there");
    }

    [Test]
    public void Sync_adopts_the_host_set_only_when_nothing_of_mine_is_unsent_or_in_flight_and_always_after_the_lock()
    {
        bool Adopt(bool unsent, int inFlight, string phase) => (bool)Static("RouletteSync", "ShouldAdopt", unsent, inFlight, E("RouletteTablePhase", phase));
        Assert.IsTrue(Adopt(false, 0, "Betting"));
        Assert.IsFalse(Adopt(true, 0, "Betting"), "my newer set is on its way");
        Assert.IsFalse(Adopt(false, 1, "Betting"), "an answer is still out");
        Assert.IsTrue(Adopt(true, 1, "Locked"), "after the lock the host's set is what plays");
        Assert.IsTrue(Adopt(true, 2, "Spinning"));
        Assert.IsTrue(Adopt(false, 0, "Result"));
        Assert.IsTrue((bool)Static("RouletteSync", "BettingOpen", E("RouletteTablePhase", "Idle"), double.PositiveInfinity));
        Assert.IsTrue((bool)Static("RouletteSync", "BettingOpen", E("RouletteTablePhase", "Betting"), 3.0));
        Assert.IsFalse((bool)Static("RouletteSync", "BettingOpen", E("RouletteTablePhase", "Betting"), 0.0));
        Assert.IsFalse((bool)Static("RouletteSync", "BettingOpen", E("RouletteTablePhase", "Locked"), 3.0));
    }

    [Test]
    public void Send_gate_keeps_at_most_four_sets_a_second()
    {
        var g = New("SendGate");
        Assert.IsFalse((bool)Call(g, "Due", 10.0, false), "nothing changed");
        Assert.IsTrue((bool)Call(g, "Due", 10.0, true));
        Call(g, "Mark", 10.0);
        Assert.IsFalse((bool)Call(g, "Due", 10.2, true));
        Assert.IsTrue((bool)Call(g, "Due", 10.27, true));
        int sent = 0;
        double last = -100;
        for (double t = 0; t < 10.0; t += 0.016)
            if ((bool)Call(g, "Due", 50 + t, true)) { Call(g, "Mark", 50 + t); sent++; last = t; }
        Assert.LessOrEqual(sent, 40, "10 s of frantic clicking is at most 4 a second");
        Assert.GreaterOrEqual(sent, 30);
    }

    /// <summary>The host replays the sets it was sent through the real validator; the local book is reconciled with its answers.</summary>
    [Test]
    public void Local_book_converges_on_the_hosts_set_through_refusals_and_late_answers()
    {
        var rng = Dice(5);
        var book = NewBook();
        var gate = New("SendGate");
        Array hostSet = Array.CreateInstance(T("RouletteBet"), 0);
        int sentVersion = G<int>(book, "Version"), inFlight = 0;
        var pending = new Queue<(Array set, int version)>();
        double now = 0;
        for (int step = 0; step < 4000; step++)
        {
            now += 0.016;
            // the player clicks around
            int roll = rng(100);
            string[] types = { "Straight", "Split", "Corner", "Red", "Dozen", "Street" };
            if (roll < 30)
            {
                string t = types[rng(types.Length)];
                int count = (int)Static("RouletteRules", "SpotCount", E("RouletteBetType", t));
                int cents = RlRack[rng(RlRack.Length)];
                TryAdd(book, t, rng(count), cents, 1000000, out _);
            }
            else if (roll < 36) { var ua = new object[] { null, 0, 0 }; book.GetType().GetMethod("Undo").Invoke(book, ua); }
            else if (roll < 38) Call(book, "Clear");
            // sending
            if ((bool)Call(gate, "Due", now, G<int>(book, "Version") != sentVersion))
            {
                var bets = ((IEnumerable)Call(book, "ToBets")).Cast<object>().ToArray();
                var arr = Array.CreateInstance(T("RouletteBet"), bets.Length);
                for (int i = 0; i < bets.Length; i++) arr.SetValue(bets[i], i);
                pending.Enqueue((arr, G<int>(book, "Version")));
                sentVersion = G<int>(book, "Version");
                Call(gate, "Mark", now);
                inFlight++;
            }
            // the host answers one request every few frames
            if (pending.Count > 0 && rng(4) == 0)
            {
                var (set, _) = pending.Dequeue();
                inFlight--;
                object err = Static("RouletteRules", "Validate", set, set.Length);
                // refuse some sets at random, like a lost race at the lock
                bool refused = err.ToString() != "Ok" && set.Length > 0 || rng(10) == 0;
                if (!refused) hostSet = set;
                if (refused) { /* the controller takes the host's set back */ Call(book, "Adopt", hostSet, RlRack); sentVersion = G<int>(book, "Version"); }
            }
            // a snapshot every few frames: adopt when allowed
            if (rng(5) == 0)
            {
                bool should = (bool)Static("RouletteSync", "ShouldAdopt", G<int>(book, "Version") != sentVersion, inFlight, E("RouletteTablePhase", "Betting"));
                if (should && !(bool)Call(book, "SameAs", hostSet)) { Call(book, "Adopt", hostSet, RlRack); sentVersion = G<int>(book, "Version"); }
            }
        }
        // drain: every request answered, one last snapshot
        while (pending.Count > 0) { var (set, _) = pending.Dequeue(); inFlight--; if (set.Length == 0 || (string)Static("RouletteRules", "Validate", set, set.Length).ToString() == "Ok") hostSet = set; }
        if ((bool)Static("RouletteSync", "ShouldAdopt", G<int>(book, "Version") != sentVersion, inFlight, E("RouletteTablePhase", "Betting")))
            Call(book, "Adopt", hostSet, RlRack);
        else
        {
            // unsent changes: one more send and the host takes it
            var bets = ((IEnumerable)Call(book, "ToBets")).Cast<object>().ToArray();
            var arr = Array.CreateInstance(T("RouletteBet"), bets.Length);
            for (int i = 0; i < bets.Length; i++) arr.SetValue(bets[i], i);
            if (arr.Length == 0 || Static("RouletteRules", "Validate", arr, arr.Length).ToString() == "Ok") hostSet = arr;
        }
        Assert.IsTrue((bool)Call(book, "SameAs", hostSet), "the local book ends on the host's accepted set");
        Assert.AreEqual("Ok", arr0(hostSet));
    }

    static string arr0(Array set) => set.Length == 0 ? "Ok" : Static("RouletteRules", "Validate", set, set.Length).ToString();

    // ================================================================================================ words, markup, the bar

    [Test]
    public void Spot_names_and_payouts_read_like_the_printed_layout()
    {
        Assert.AreEqual("pays 8 to 1", Static("CasinoSeatText", "Pays", E("RouletteBetType", "Corner")));
        Assert.AreEqual("Corner 17-18-20-21", Static("CasinoSeatText", "SpotName", E("RouletteBetType", "Corner"), (int)Static("RouletteRules", "CornerIndex", 17)));
        Assert.AreEqual("Red", Static("CasinoSeatText", "SpotName", E("RouletteBetType", "Red"), 0));
        Assert.AreEqual("Straight up 00", Static("CasinoSeatText", "SpotName", E("RouletteBetType", "Straight"), 37));
        Assert.AreEqual("pays 35 to 1", Static("CasinoSeatText", "Pays", E("RouletteBetType", "Straight")));
        Assert.AreEqual("Third dozen (25-36)", Static("CasinoSeatText", "SpotName", E("RouletteBetType", "Dozen"), 2));
    }

    [Test]
    public void Countdown_and_signed_money_format()
    {
        Assert.AreEqual("12s", Static("CasinoSeatText", "Countdown", 11.2));
        Assert.AreEqual("4.2s", Static("CasinoSeatText", "Countdown", 4.2));
        Assert.AreEqual("0s", Static("CasinoSeatText", "Countdown", -3.0));
        Assert.AreEqual("", Static("CasinoSeatText", "Countdown", double.PositiveInfinity));
        Assert.AreEqual("+$25", Static("CasinoSeatText", "Signed", 2500L));
        Assert.AreEqual("-$5", Static("CasinoSeatText", "Signed", -500L));
    }

    [Test]
    public void Hint_markup_splits_into_key_chips_and_labels()
    {
        var segs = ((IEnumerable)Static("CasinoHintStrip", "Parse", "[Q/E] chip   [Space] add chip  [Esc] leave")).Cast<object>().ToArray();
        Assert.AreEqual(6, segs.Length);
        Assert.IsTrue(G<bool>(segs[0], "chip")); Assert.AreEqual("Q/E", G<string>(segs[0], "text"));
        Assert.IsFalse(G<bool>(segs[1], "chip")); Assert.AreEqual("chip", G<string>(segs[1], "text"));
        Assert.AreEqual("Esc", G<string>(segs[4], "text"));
        Assert.AreEqual(0, ((IEnumerable)Static("CasinoHintStrip", "Parse", "")).Cast<object>().Count());
        var plain = ((IEnumerable)Static("CasinoHintStrip", "Parse", "Bet $35 goes down in 4s")).Cast<object>().ToArray();
        Assert.AreEqual(1, plain.Length); Assert.IsFalse(G<bool>(plain[0], "chip"));
        Assert.AreEqual("[Hold Space] auto-spin", Static("CasinoHintStrip", "Key", "Hold Space", "auto-spin"));
        var open = ((IEnumerable)Static("CasinoHintStrip", "Parse", "tail [unclosed")).Cast<object>().ToArray();
        Assert.AreEqual(2, open.Length, "an unclosed bracket stays text");
    }

    [Test]
    public void Bar_cuts_off_alcohol_from_1_15_but_never_water()
    {
        string Why(float level, long cash, int drink) => (string)Static("BarMenu", "WhyNot", level, cash, drink);
        Assert.AreEqual("", Why(0f, 100000, 0));
        Assert.AreEqual("", Why(1.14f, 100000, 2), "1.14: still served");
        Assert.AreNotEqual("", Why(1.15f, 100000, 0));
        Assert.AreNotEqual("", Why(1.2f, 100000, 2));
        Assert.AreEqual("", Why(1.2f, 100000, 3), "water is always sold");
        Assert.IsTrue(Why(0f, 500, 0).Contains("$6"), "can't cover a $6 beer with $5");
        Assert.AreEqual("", Why(0f, 100, 3), "water is a dollar");
        Assert.AreNotEqual("", Why(0f, 100000, 9), "not on the menu");
        Assert.IsTrue((bool)Static("BarMenu", "Refused", 1.15f, 1));
        Assert.IsFalse((bool)Static("BarMenu", "Refused", 1.2f, 3));
    }

    [Test]
    public void Bar_stage_meter_and_warnings_follow_the_drunk_level()
    {
        Assert.AreEqual(0, Static("BarMenu", "Stage", 0.1f));
        Assert.AreEqual(1, Static("BarMenu", "Stage", 0.2f));
        Assert.AreEqual(2, Static("BarMenu", "Stage", 0.5f));
        Assert.AreEqual(3, Static("BarMenu", "Stage", 0.9f));
        Assert.AreEqual("", Static("BarMenu", "Warning", 0.0f));
        Assert.IsTrue(((string)Static("BarMenu", "Warning", 0.9f)).Contains("water"));
        Assert.IsTrue(((string)Static("BarMenu", "Warning", 1.16f)).Contains("Water or the door"));
        string full = (string)Static("BarMenu", "Meter", 1.2f);
        Assert.IsTrue(full.Contains("WASTED") && full.Contains("############"), full);
        Assert.IsFalse(full.Contains("[") || full.Contains("]"), "the strip's markup owns the square brackets");
        string empty = (string)Static("BarMenu", "Meter", 0f);
        Assert.IsTrue(empty.Contains("SOBER") && empty.Contains("------------"), empty);
    }
}

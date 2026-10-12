using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// American roulette at The Lucky Raccoon (Docs/Casino.md §4.2): every bet's real edge by enumeration, the layout, the
/// wheel and the limits. Reflection because the rules live in Assembly-CSharp.
/// </summary>
public sealed class CasinoRouletteTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    static Type Rules => Type.GetType("RouletteRules, Assembly-CSharp", true);
    static Type BetType => Type.GetType("RouletteBet, Assembly-CSharp", true);
    static Type Kind => Type.GetType("RouletteBetType, Assembly-CSharp", true);
    const int DoubleZero = 37;

    static object Call(string method, params object[] args)
    {
        foreach (var m in Rules.GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            return m.Invoke(null, args);
        }
        Assert.Fail("RouletteRules." + method + "/" + args.Length + " not found");
        return null;
    }

    static object Type_(string name) => Enum.Parse(Kind, name);
    static object Bet(string type, int index, int cents)
    {
        object bet = Activator.CreateInstance(BetType);
        BetType.GetField("type").SetValue(bet, Type_(type));
        BetType.GetField("index").SetValue(bet, (byte)index);
        BetType.GetField("cents").SetValue(bet, cents);
        return bet;
    }

    static Array Bets(params object[] bets)
    {
        var array = Array.CreateInstance(BetType, bets.Length);
        for (int i = 0; i < bets.Length; i++) array.SetValue(bets[i], i);
        return array;
    }

    static int Spots(string type) => (int)Call("SpotCount", Type_(type));
    static byte[] Pockets(string type, int index) => (byte[])Call("Pockets", Type_(type), index);
    static bool Covers(object bet, int pocket) => (bool)Call("Covers", bet, pocket);
    static long Payout(object bet, int pocket) => (long)Call("Payout", bet, pocket);
    static long Return(object bet, int pocket) => (long)Call("Return", bet, pocket);
    static string Validate(params object[] bets) => Call("Validate", Bets(bets), bets.Length).ToString();
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);

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

    // type, spots, pockets covered, pays to one
    static readonly (string type, int spots, int covers, int odds)[] table =
    {
        ("Straight", 38, 1, 35), ("Split", 62, 2, 17), ("Street", 15, 3, 11), ("Corner", 22, 4, 8), ("SixLine", 11, 6, 5),
        ("TopLine", 1, 5, 6), ("Dozen", 3, 12, 2), ("Column", 3, 12, 2), ("Red", 1, 18, 1), ("Black", 1, 18, 1),
        ("Odd", 1, 18, 1), ("Even", 1, 18, 1), ("Low", 1, 18, 1), ("High", 1, 18, 1),
    };

    [Test]
    public void EveryBetOnTheLayoutHasItsRealEdge()
    {
        Assert.AreEqual(38, (int)Rules.GetField("PocketCount", Static).GetValue(null));
        Assert.AreEqual(table.Length, Enum.GetValues(Kind).Length, "a bet type is missing from this test");
        int spots = 0;
        foreach (var row in table)
        {
            Assert.AreEqual(row.spots, Spots(row.type), row.type + " spots");
            Assert.AreEqual(row.odds, (int)Call("Odds", Type_(row.type)), row.type + " odds");
            for (int index = 0; index < row.spots; index++)
            {
                string what = row.type + " " + index;
                object bet = Bet(row.type, index, 100);
                int wins = 0;
                long back = 0;
                for (int pocket = 0; pocket < 38; pocket++)
                {
                    bool covers = Covers(bet, pocket);
                    if (covers) wins++;
                    Assert.AreEqual(covers ? 100L * row.odds : 0L, Payout(bet, pocket), what + " payout on " + pocket);
                    Assert.AreEqual(covers ? 100L * (row.odds + 1) : 0L, Return(bet, pocket), what + " return on " + pocket);
                    back += Return(bet, pocket);
                }
                Assert.AreEqual(row.covers, wins, what + " covers");
                Assert.AreEqual(row.covers, Pockets(row.type, index).Length, what + " pocket list");
                // Staking 100 on each of the 38 pockets in turn costs 3800. A fair bet would bring 3800 back.
                long expected = row.type == "TopLine" ? 3500 : 3600;
                Assert.AreEqual(expected, back, what + " total return over the wheel");
                double edge = 1.0 - (double)Call("ExpectedReturn", Type_(row.type), index);
                Assert.AreEqual(row.type == "TopLine" ? 3.0 / 38.0 : 2.0 / 38.0, edge, 1e-12, what + " edge");
                spots++;
            }
        }
        Assert.AreEqual(161, spots);
        UnityEngine.Debug.Log("[CasinoRouletteTests] " + spots + " spots enumerated over 38 pockets: edge 2/38 = 5.263% on all, 3/38 = 7.895% on the top line");
    }

    [Test]
    public void TheColoursAndTheWheelAreARealAmericanWheel()
    {
        int[] reds = { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };
        for (int p = 0; p < 38; p++)
        {
            bool red = (bool)Call("IsRed", p), black = (bool)Call("IsBlack", p), green = (bool)Call("IsGreen", p);
            Assert.AreEqual(reds.Contains(p), red, "red " + p);
            Assert.AreEqual(p == 0 || p == DoubleZero, green, "green " + p);
            Assert.AreEqual(1, (red ? 1 : 0) + (black ? 1 : 0) + (green ? 1 : 0), "one colour for " + p);
            Assert.AreEqual(red, Covers(Bet("Red", 0, 100), p));
            Assert.AreEqual(black, Covers(Bet("Black", 0, 100), p));
        }
        Assert.AreEqual("00", (string)Call("PocketName", DoubleZero));
        Assert.AreEqual("17", (string)Call("PocketName", 17));

        var wheel = (byte[])Rules.GetField("WheelOrder", Static).GetValue(null);
        Assert.AreEqual(38, wheel.Length);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 38).Select(i => (byte)i), wheel, "every pocket once");
        Assert.AreEqual(0, wheel[0]);
        Assert.AreEqual(DoubleZero, wheel[19], "00 sits opposite 0");
        // On a real wheel the colours alternate all the way round, and each number faces its neighbour (1 opposite 2, ...).
        for (int i = 0; i < 38; i++)
        {
            int a = wheel[i], b = wheel[(i + 1) % 38];
            if (a == 0 || a == DoubleZero || b == 0 || b == DoubleZero) continue;
            Assert.AreNotEqual((bool)Call("IsRed", a), (bool)Call("IsRed", b), a + " and " + b + " share a colour on the wheel");
        }
        for (int i = 0; i < 38; i++)
        {
            int a = wheel[i], b = wheel[(i + 19) % 38];
            if (a == 0 || a == DoubleZero) continue;
            Assert.AreEqual(1, Math.Abs(a - b), a + " should face its neighbour");
            Assert.AreEqual(i, (int)Call("WheelSlot", a));
        }
    }

    [Test]
    public void ZeroAndDoubleZeroLoseEveryOutsideBet()
    {
        foreach (string type in new[] { "Dozen", "Column", "Red", "Black", "Odd", "Even", "Low", "High" })
            for (int index = 0; index < Spots(type); index++)
            {
                Assert.IsFalse(Covers(Bet(type, index, 100), 0), type);
                Assert.IsFalse(Covers(Bet(type, index, 100), DoubleZero), type);
                Assert.IsFalse((bool)Call("IsInside", Type_(type)));
            }
        foreach (string type in new[] { "Straight", "Split", "Street", "Corner", "SixLine", "TopLine" }) Assert.IsTrue((bool)Call("IsInside", Type_(type)));
        Assert.IsTrue(Covers(Bet("Odd", 0, 100), 35));
        Assert.IsTrue(Covers(Bet("Even", 0, 100), 36));
        Assert.IsTrue(Covers(Bet("Low", 0, 100), 18));
        Assert.IsTrue(Covers(Bet("High", 0, 100), 19));
        CollectionAssert.AreEqual(Enumerable.Range(13, 12).Select(i => (byte)i), Pockets("Dozen", 1));
        CollectionAssert.AreEqual(new byte[] { 2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35 }, Pockets("Column", 1));
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3, DoubleZero }, Pockets("TopLine", 0));
    }

    static bool Adjacent(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        if (lo >= 1 && hi <= 36) return hi - lo == 3 || (hi - lo == 1 && lo % 3 != 0);
        // The zeros: 0 touches 1 and 2, 00 touches 2 and 3, and they touch each other.
        return (lo == 0 && (hi == 1 || hi == 2 || hi == DoubleZero)) || (hi == DoubleZero && (lo == 2 || lo == 3));
    }

    [Test]
    public void SplitsAreExactlyThePairsThatTouchOnTheLayout()
    {
        var seen = new HashSet<int>();
        for (int a = 0; a < 38; a++)
            for (int b = 0; b < 38; b++)
            {
                int index = (int)Call("SplitIndex", a, b);
                Assert.AreEqual(a != b && Adjacent(a, b), index >= 0, "split " + a + "-" + b);
                if (index < 0) continue;
                Assert.AreEqual(index, (int)Call("SplitIndex", b, a), "order must not matter");
                CollectionAssert.AreEquivalent(new[] { (byte)a, (byte)b }, Pockets("Split", index));
                seen.Add(index);
            }
        Assert.AreEqual(62, seen.Count, "every split spot is reachable from a pair");
        // The ones people get wrong.
        Assert.Less((int)Call("SplitIndex", 3, 4), 0, "3 and 4 are on different rows");
        Assert.Less((int)Call("SplitIndex", 0, 3), 0);
        Assert.Less((int)Call("SplitIndex", DoubleZero, 1), 0);
        Assert.Less((int)Call("SplitIndex", 1, 5), 0, "diagonal");
        Assert.Less((int)Call("SplitIndex", 36, 38), 0);
        Assert.Less((int)Call("SplitIndex", -1, 0), 0);
        foreach (var pair in new[] { (0, 1), (0, 2), (DoubleZero, 2), (DoubleZero, 3), (0, DoubleZero), (1, 2), (1, 4), (33, 36), (35, 36) })
            Assert.GreaterOrEqual((int)Call("SplitIndex", pair.Item1, pair.Item2), 0, pair.ToString());
    }

    [Test]
    public void StreetsCornersAndSixLinesAreRealLayoutGroups()
    {
        // Streets: the twelve rows and the three zero trios, nothing else.
        var streets = new HashSet<string>();
        for (int i = 0; i < Spots("Street"); i++) streets.Add(string.Join("-", Pockets("Street", i)));
        var expected = new HashSet<string> { "0-1-2", "0-2-37", "2-3-37" };
        for (int r = 0; r < 12; r++) expected.Add((3 * r + 1) + "-" + (3 * r + 2) + "-" + (3 * r + 3));
        Assert.IsTrue(expected.SetEquals(streets), string.Join(" ", streets));
        Assert.AreEqual(0, (int)Call("StreetIndex", 1, 2, 3));
        Assert.AreEqual(11, (int)Call("StreetIndex", 36, 34, 35));
        Assert.GreaterOrEqual((int)Call("StreetIndex", 0, 1, 2), 12);
        Assert.GreaterOrEqual((int)Call("StreetIndex", 2, 0, DoubleZero), 12);
        Assert.GreaterOrEqual((int)Call("StreetIndex", DoubleZero, 2, 3), 12);
        Assert.Less((int)Call("StreetIndex", 0, 1, DoubleZero), 0, "0-00-1 is not a bet");
        Assert.Less((int)Call("StreetIndex", 0, 2, 3), 0);
        Assert.Less((int)Call("StreetIndex", 2, 3, 4), 0, "a street cannot run across rows");
        Assert.Less((int)Call("StreetIndex", 1, 1, 2), 0);

        // Corners: four numbers in a square, never wrapping off the right-hand column or past 36.
        int corners = 0;
        for (int n = -2; n <= 40; n++)
        {
            int index = (int)Call("CornerIndex", n);
            bool real = n >= 1 && n <= 32 && n % 3 != 0;
            Assert.AreEqual(real, index >= 0, "corner at " + n);
            if (!real) continue;
            CollectionAssert.AreEqual(new[] { (byte)n, (byte)(n + 1), (byte)(n + 3), (byte)(n + 4) }, Pockets("Corner", index));
            corners++;
        }
        Assert.AreEqual(22, corners);
        Assert.AreEqual(22, Enumerable.Range(0, 22).Select(i => string.Join("-", Pockets("Corner", i))).Distinct().Count());

        // Six lines: two whole rows that touch.
        int lines = 0;
        for (int n = -2; n <= 40; n++)
        {
            int index = (int)Call("SixLineIndex", n);
            bool real = n >= 1 && n <= 31 && n % 3 == 1;
            Assert.AreEqual(real, index >= 0, "six line at " + n);
            if (!real) continue;
            CollectionAssert.AreEqual(Enumerable.Range(n, 6).Select(i => (byte)i), Pockets("SixLine", index));
            lines++;
        }
        Assert.AreEqual(11, lines);
    }

    [Test]
    public void ASpotThatIsNotOnTheLayoutCoversNothingAndIsRefused()
    {
        foreach (var row in table)
        {
            object off = Bet(row.type, row.spots, 100);
            for (int pocket = 0; pocket < 38; pocket++)
            {
                Assert.IsFalse(Covers(off, pocket), row.type);
                Assert.AreEqual(0L, Return(off, pocket));
            }
            Assert.IsFalse((bool)Call("IsLegalSpot", Type_(row.type), row.spots));
            Assert.IsFalse((bool)Call("IsLegalSpot", Type_(row.type), -1));
            Assert.AreEqual("BadSpot", Validate(off), row.type);
            Assert.AreEqual(0, Pockets(row.type, row.spots).Length);
        }
        object unknownType = Activator.CreateInstance(BetType);
        BetType.GetField("type").SetValue(unknownType, Enum.ToObject(Kind, 14));
        BetType.GetField("cents").SetValue(unknownType, 100);
        Assert.AreEqual("BadSpot", Validate(unknownType));
        Assert.IsFalse(Covers(unknownType, 5));
        Assert.IsFalse(Covers(Bet("Red", 0, 100), 38), "there is no pocket 38");
        Assert.IsFalse(Covers(Bet("Red", 0, 100), -1));
    }

    [Test]
    public void TheTableLimitsAreEnforced()
    {
        Assert.AreEqual("NoBets", Validate());
        Assert.AreEqual("NoBets", Call("Validate", null, 0).ToString());
        Assert.AreEqual("Ok", Validate(Bet("Straight", 17, 100)));
        Assert.AreEqual("BadAmount", Validate(Bet("Straight", 17, 0)));
        Assert.AreEqual("BadAmount", Validate(Bet("Straight", 17, -500)));
        Assert.AreEqual("BadAmount", Validate(Bet("Straight", 17, 150)), "whole dollars only");
        Assert.AreEqual("BadAmount", Validate(Bet("Red", 0, 50)), "under the $1 minimum and not a whole dollar");

        // $100 on an inside spot, $500 on an outside spot.
        Assert.AreEqual("Ok", Validate(Bet("Straight", 17, 10000)));
        Assert.AreEqual("AboveSpotMaximum", Validate(Bet("Straight", 17, 10100)));
        Assert.AreEqual("AboveSpotMaximum", Validate(Bet("TopLine", 0, 10100)));
        Assert.AreEqual("Ok", Validate(Bet("Red", 0, 50000)));
        Assert.AreEqual("AboveSpotMaximum", Validate(Bet("Red", 0, 50100)));
        Assert.AreEqual("Ok", Validate(Bet("Dozen", 2, 50000)));
        // Two stacks on one spot count together; the same number on another spot type is another spot.
        Assert.AreEqual("AboveSpotMaximum", Validate(Bet("Straight", 17, 6000), Bet("Straight", 17, 6000)));
        Assert.AreEqual("Ok", Validate(Bet("Straight", 17, 6000), Bet("Straight", 18, 6000)));
        Assert.AreEqual("Ok", Validate(Bet("Straight", 0, 10000), Bet("Split", 0, 10000)));

        // $1,000 a spin.
        Assert.AreEqual("Ok", Validate(Bet("Red", 0, 50000), Bet("Odd", 0, 50000)));
        Assert.AreEqual("AboveTableMaximum", Validate(Bet("Red", 0, 50000), Bet("Odd", 0, 50000), Bet("Straight", 7, 100)));

        int most = (int)Rules.GetField("MaxBetsPerSpin", Static).GetValue(null);
        var many = Enumerable.Range(0, most + 1).Select(i => Bet("Split", i % 62, 100)).ToArray();
        Assert.AreEqual("TooManyBets", Validate(many));
        Assert.AreEqual("Ok", Validate(many.Take(62).ToArray()));

        var casino = Type.GetType("CasinoRules, Assembly-CSharp", true);
        Assert.AreEqual(100, (int)casino.GetField("RouletteMinSpotCents").GetValue(null));
        Assert.AreEqual(10000, (int)casino.GetField("RouletteMaxInsideCents").GetValue(null));
        Assert.AreEqual(50000, (int)casino.GetField("RouletteMaxOutsideCents").GetValue(null));
        Assert.AreEqual(100000, (int)casino.GetField("RouletteMaxSpinCents").GetValue(null));
    }

    [Test]
    public void ASpinIsSettledBetByBet()
    {
        // 17 is black and odd, in the second dozen and the middle column.
        var bets = Bets(Bet("Straight", 17, 1000), Bet("Red", 0, 2000), Bet("Black", 0, 2000), Bet("Dozen", 1, 500),
            Bet("Split", (int)Call("SplitIndex", 17, 20), 300), Bet("Corner", (int)Call("CornerIndex", 1), 400));
        Assert.AreEqual(6200L, (long)Call("TotalStake", bets, 6));
        object r = Call("Resolve", bets, 6, 17);
        Assert.AreEqual(17, Field<byte>(r, "pocket"));
        Assert.AreEqual(6200L, Field<long>(r, "stakedCents"));
        Assert.AreEqual(1000L * 36 + 2000 * 2 + 500 * 3 + 300 * 18, Field<long>(r, "returnCents"));
        object zero = Call("Resolve", bets, 6, DoubleZero);
        Assert.AreEqual(0L, Field<long>(zero, "returnCents"), "00 takes the lot");
    }

    [Test]
    public void TheWheelIsFair()
    {
        // 380,000 seeded spins: 10,000 expected per pocket. Chi-square, 37 degrees of freedom: 69.3 is the 0.1% point.
        var dice = Dice(20261009);
        var spin = Rules.GetMethod("Spin", Static);
        var args = new object[] { dice };
        const int spins = 380000;
        var counts = new int[38];
        for (int i = 0; i < spins; i++) counts[(byte)spin.Invoke(null, args)]++;
        double expected = spins / 38.0, chi = 0;
        foreach (int c in counts) chi += (c - expected) * (c - expected) / expected;
        UnityEngine.Debug.Log("[CasinoRouletteTests] " + spins + " spins: chi-square " + chi.ToString("F2") + " (37 dof), min " + counts.Min() + ", max " + counts.Max());
        Assert.Less(chi, 69.3, "the wheel is not uniform");
        Assert.Greater(chi, 12.0, "too even to be random");
        // The pocket is the die, untouched: no remapping that could hide a bias.
        for (int p = 0; p < 38; p++) Assert.AreEqual(p, (byte)spin.Invoke(null, new object[] { (Func<int, int>)(n => p) }));
        Assert.Throws<TargetInvocationException>(() => spin.Invoke(null, new object[] { (Func<int, int>)(n => 38) }));
    }
}

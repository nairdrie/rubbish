using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The five slot machines of The Lucky Raccoon (Docs/Casino.md §4.3). The return of each is worked out here from scratch,
/// from the strips and the pay table, over every stop combination, and compared with the target and with what SlotRules
/// reports. Reflection because the rules live in Assembly-CSharp.
/// </summary>
public sealed class CasinoSlotTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    static Type Rules => Type.GetType("SlotRules, Assembly-CSharp", true);
    static object Call(string method, params object[] args) => Rules.GetMethod(method, Static).Invoke(null, args);
    static T Get<T>(object target, string name) => (T)target.GetType().GetField(name).GetValue(target);

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

    /// <summary>A machine read out of SlotRules into plain arrays, so the sums below owe nothing to its own code.</summary>
    sealed class Machine
    {
        public int id, stake;
        public string name;
        public double target;
        public string[] symbols;
        public byte[][] reels;
        public string[] payNames;
        public int[] payMask, payCount, payMultiple;

        public int Winner(int a, int b, int c)
        {
            for (int i = 0; i < payMask.Length; i++) if (Matches(i, a, b, c)) return i;
            return -1;
        }

        public bool Matches(int rule, int a, int b, int c) =>
            ((payMask[rule] >> a) & 1) + ((payMask[rule] >> b) & 1) + ((payMask[rule] >> c) & 1) >= payCount[rule];

        public long Combinations => (long)reels[0].Length * reels[1].Length * reels[2].Length;
    }

    static Machine Load(int id)
    {
        object m = Call("Machine", id);
        var pays = (Array)Get<object>(m, "pays");
        var machine = new Machine
        {
            id = Get<byte>(m, "id"), name = Get<string>(m, "name"), stake = Get<int>(m, "stakeCents"), target = Get<double>(m, "targetReturn"),
            symbols = Get<string[]>(m, "symbols"), reels = Get<byte[][]>(m, "reels"),
            payNames = new string[pays.Length], payMask = new int[pays.Length], payCount = new int[pays.Length], payMultiple = new int[pays.Length],
        };
        for (int i = 0; i < pays.Length; i++)
        {
            object pay = pays.GetValue(i);
            machine.payNames[i] = Get<string>(pay, "name");
            machine.payMask[i] = Get<ushort>(pay, "symbols");
            machine.payCount[i] = Get<byte>(pay, "count");
            machine.payMultiple[i] = Get<int>(pay, "multiple");
        }
        return machine;
    }

    sealed class Exact
    {
        public long combinations, hits, paid, paidSquared, twoTop;
        public long[] ruleHits;
        public double Return => paid / (double)combinations;
        public double HitFrequency => hits / (double)combinations;
        public double Volatility => Math.Sqrt(paidSquared / (double)combinations - Return * Return);
    }

    static readonly Dictionary<int, Exact> exact = new Dictionary<int, Exact>();

    /// <summary>Every stop of reel one against every stop of reel two against every stop of reel three.</summary>
    static Exact Enumerate(Machine m)
    {
        if (exact.TryGetValue(m.id, out var known)) return known;
        var e = new Exact { ruleHits = new long[m.payMask.Length], combinations = m.Combinations };
        int top = Enumerable.Range(0, 16).First(s => (m.payMask[0] >> s & 1) != 0);
        foreach (byte a in m.reels[0])
            foreach (byte b in m.reels[1])
                foreach (byte c in m.reels[2])
                {
                    if ((a == top ? 1 : 0) + (b == top ? 1 : 0) + (c == top ? 1 : 0) == 2) e.twoTop++;
                    int rule = m.Winner(a, b, c);
                    if (rule < 0) continue;
                    e.hits++;
                    e.ruleHits[rule]++;
                    e.paid += m.payMultiple[rule];
                    e.paidSquared += (long)m.payMultiple[rule] * m.payMultiple[rule];
                }
        return exact[m.id] = e;
    }

    // id, name, stake, target return, hit-frequency band, top prize (× stake) and its exact odds (1 in combinations / ways)
    static readonly object[] designs =
    {
        new object[] { 0, "Lucky Sevens", 100, 0.95, 0.25, 0.30, 500, 32768L, 1L },
        new object[] { 1, "Trash Panda Bandit", 100, 0.92, 0.16, 0.22, 1000, 70400L, 1L },
        new object[] { 2, "Golden Gnome", 500, 0.90, 0.10, 0.14, 4000, 262144L, 2L },
        new object[] { 3, "Dumpster Fire", 500, 0.88, 0.12, 0.17, 500, 147456L, 49L },
        new object[] { 4, "Mystery Bag", 2500, 0.93, 0.16, 0.22, 5000, 215040L, 2L },
    };

    [Test]
    public void ThereAreFiveMachines()
    {
        Assert.AreEqual(5, (int)Rules.GetProperty("MachineCount", Static).GetValue(null));
        Assert.AreEqual(designs.Length, 5);
        Assert.IsFalse((bool)Call("IsMachine", 5));
        Assert.IsFalse((bool)Call("IsMachine", -1));
        Assert.AreEqual(5, Enumerable.Range(0, 5).Select(i => Load(i).name).Distinct().Count());
    }

    [TestCaseSource(nameof(designs))]
    public void TheReturnIsTheTargetAndTheFeelIsAsDesigned(int id, string name, int stake, double target, double hitLow, double hitHigh,
        int topMultiple, long combinations, long topWays)
    {
        var m = Load(id);
        Assert.AreEqual(id, m.id);
        Assert.AreEqual(name, m.name);
        Assert.AreEqual(stake, m.stake);
        Assert.AreEqual(stake, (int)Call("StakeCents", id));
        Assert.AreEqual(target, m.target, 1e-12);

        var e = Enumerate(m);
        Assert.AreEqual(combinations, e.combinations);
        string line = "[CasinoSlotTests] " + id + " " + name + ": return " + (e.Return * 100).ToString("F3") + "% (target " + (target * 100).ToString("F0") +
            "%), hit frequency " + (e.HitFrequency * 100).ToString("F2") + "%, volatility " + e.Volatility.ToString("F2") + " stakes, top prize " +
            m.payMultiple[0] + "x at 1 in " + (e.combinations / (double)e.ruleHits[0]).ToString("F0") + ", two-of-top near misses " +
            (e.twoTop * 100.0 / e.combinations).ToString("F2") + "%";
        UnityEngine.Debug.Log(line);

        // The contract: within half a point of the target. (The strips were tuned to within a hundredth.)
        Assert.AreEqual(target, e.Return, 0.005, name + " return");
        Assert.AreEqual(target, e.Return, 0.0015, name + " has drifted from its tuned return");
        Assert.That(e.HitFrequency, Is.InRange(hitLow, hitHigh), name + " hit frequency");

        // SlotRules reports the same numbers it is built from.
        Assert.AreEqual(e.Return, (double)Call("Return", id), 1e-12);
        Assert.AreEqual(e.HitFrequency, (double)Call("HitFrequency", id), 1e-12);
        object stats = Call("Stats", id);
        Assert.AreEqual(e.combinations, Get<long>(stats, "combinations"));
        Assert.AreEqual(e.hits, Get<long>(stats, "hits"));
        Assert.AreEqual(e.twoTop, Get<long>(stats, "nearMisses"));
        Assert.AreEqual(e.Volatility, Get<double>(stats, "volatility"), 1e-9);
        CollectionAssert.AreEqual(e.ruleHits, Get<long[]>(stats, "ruleHits"));

        // The top prize: the biggest line on the table, and exactly as rare as designed.
        Assert.AreEqual(topMultiple, m.payMultiple[0]);
        Assert.AreEqual(m.payMultiple.Max(), m.payMultiple[0]);
        Assert.AreEqual(3, m.payCount[0]);
        Assert.AreEqual(topWays, e.ruleHits[0], name + " top prize combinations");
        Assert.AreEqual(combinations / (double)topWays, Get<double>(stats, "topPrizeOneIn"), 1e-9);
        Assert.AreEqual((long)stake * topMultiple, Field<int>(Call("Resolve", id, TopStop(m, 0), TopStop(m, 1), TopStop(m, 2)), "winCents"));
    }

    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
    static int TopSymbol(Machine m) => Enumerable.Range(0, 16).First(s => (m.payMask[0] >> s & 1) != 0);
    static int TopStop(Machine m, int reel) => Array.IndexOf(m.reels[reel], (byte)TopSymbol(m));

    [Test]
    public void TheMachinesFeelDifferent()
    {
        var e = Enumerable.Range(0, 5).Select(i => Enumerate(Load(i))).ToArray();
        // Lucky Sevens hits most often and swings least; Golden Gnome hits least often.
        Assert.AreEqual(e.Max(x => x.HitFrequency), e[0].HitFrequency);
        Assert.AreEqual(e.Min(x => x.Volatility), e[0].Volatility);
        Assert.AreEqual(e.Min(x => x.HitFrequency), e[2].HitFrequency);
        // Volatility climbs: sevens, bandit, dumpster, gnome, mystery bag.
        Assert.Less(e[0].Volatility, e[1].Volatility);
        Assert.Less(e[1].Volatility, e[3].Volatility);
        Assert.Less(e[3].Volatility, e[2].Volatility);
        Assert.Less(e[2].Volatility, e[4].Volatility);
        // Golden Gnome: a rare, very big jackpot.
        var gnome = Load(2);
        Assert.GreaterOrEqual(gnome.payMultiple[0], 1000);
        Assert.Greater(e[2].combinations / e[2].ruleHits[0], 100000);
        // Dumpster Fire: two Dumpster Fires on the line far more often than anywhere else, purely from its strips.
        double[] near = e.Select(x => x.twoTop / (double)x.combinations).ToArray();
        Assert.Greater(near[3], 0.02, "Dumpster Fire should tease about one spin in forty");
        for (int i = 0; i < 5; i++) if (i != 3) Assert.Greater(near[3], near[i] * 5, "machine " + i);
        var fire = Load(3);
        int top = TopSymbol(fire);
        Assert.AreEqual(1, fire.reels[2].Count(s => s == top), "one Dumpster Fire on the last reel");
        Assert.GreaterOrEqual(fire.reels[0].Count(s => s == top), 6);
        Assert.GreaterOrEqual(fire.reels[1].Count(s => s == top), 6);
        // Mystery Bag: the widest spread, from money back to 5,000 times.
        var bag = Load(4);
        Assert.AreEqual(1, bag.payMultiple.Min());
        Assert.AreEqual(5000, bag.payMultiple.Max());
        Assert.AreEqual(2500, bag.stake);
    }

    [Test]
    public void PayTablesAreOrderedAndNoLineHidesAnother()
    {
        for (int id = 0; id < 5; id++)
        {
            var m = Load(id);
            var e = Enumerate(m);
            Assert.AreEqual(m.payNames.Length, m.payNames.Distinct().Count(), m.name + " has two lines with one name");
            for (int i = 0; i < m.payMask.Length; i++)
            {
                string what = m.name + " / " + m.payNames[i];
                Assert.Greater(m.payMultiple[i], 0, what);
                Assert.That(m.payCount[i], Is.InRange(1, 3), what);
                Assert.AreNotEqual(0, m.payMask[i], what);
                Assert.AreEqual(0, m.payMask[i] & 1, what + " pays on blanks");
                Assert.AreEqual(0, m.payMask[i] >> m.symbols.Length, what + " names a symbol the machine does not have");
                if (i > 0) Assert.LessOrEqual(m.payMultiple[i], m.payMultiple[i - 1], what + " is out of order");
                // Reachable: a line that can never win is shadowed by the ones above it (or impossible on these strips).
                Assert.Greater(e.ruleHits[i], 0, what + " can never win");
            }

            // On every line the reels can show, the line that pays is the best one that matches: nothing higher up steals
            // a bigger win from a line lower down.
            var on0 = m.reels[0].Distinct().ToArray();
            var on1 = m.reels[1].Distinct().ToArray();
            var on2 = m.reels[2].Distinct().ToArray();
            foreach (byte a in on0)
                foreach (byte b in on1)
                    foreach (byte c in on2)
                    {
                        int winner = m.Winner(a, b, c);
                        int best = 0;
                        for (int i = 0; i < m.payMask.Length; i++) if (m.Matches(i, a, b, c)) best = Math.Max(best, m.payMultiple[i]);
                        Assert.AreEqual(best, winner < 0 ? 0 : m.payMultiple[winner], m.name + " on " + m.symbols[a] + ", " + m.symbols[b] + ", " + m.symbols[c]);
                        Assert.AreEqual(winner, (int)Call("WinningRule", id, a, b, c), "SlotRules disagrees on " + m.symbols[a] + ", " + m.symbols[b] + ", " + m.symbols[c]);
                    }

            // Three blanks, or any line of three different non-paying symbols, pays nothing.
            Assert.AreEqual(-1, m.Winner(0, 0, 0), m.name + " pays on three blanks");
        }
    }

    [Test]
    public void StripsAreRealReels()
    {
        for (int id = 0; id < 5; id++)
        {
            var m = Load(id);
            Assert.AreEqual("Blank", m.symbols[0]);
            Assert.AreEqual(m.symbols.Length, m.symbols.Distinct().Count());
            Assert.AreEqual(3, m.reels.Length);
            for (int r = 0; r < 3; r++)
            {
                var strip = m.reels[r];
                string what = m.name + " reel " + r;
                Assert.That(strip.Length, Is.InRange(32, 64), what);
                Assert.AreEqual(strip.Length, (int)Call("ReelLength", id, r));
                foreach (byte s in strip) Assert.Less(s, m.symbols.Length, what);
                // Every symbol is on every reel (so every three-of-a-kind is possible), and none sits next to itself.
                for (int s = 0; s < m.symbols.Length; s++) Assert.IsTrue(strip.Contains((byte)s), what + " has no " + m.symbols[s]);
                for (int i = 0; i < strip.Length; i++)
                {
                    byte here = strip[i], next = strip[(i + 1) % strip.Length];
                    if (here != 0) Assert.AreNotEqual(here, next, what + " has two " + m.symbols[here] + " together at " + i);
                }
                // The window above and below the line is the strip itself, wrapping at the ends.
                for (int i = 0; i < strip.Length; i++)
                {
                    Assert.AreEqual(strip[i], (byte)Call("SymbolAt", id, r, i, 0));
                    Assert.AreEqual(strip[(i + strip.Length - 1) % strip.Length], (byte)Call("SymbolAt", id, r, i, -1));
                    Assert.AreEqual(strip[(i + 1) % strip.Length], (byte)Call("SymbolAt", id, r, i, 1));
                }
            }
        }
    }

    [Test]
    public void ASpinIsThreeHonestStops()
    {
        var spin = Rules.GetMethod("Spin", Static);
        for (int id = 0; id < 5; id++)
        {
            var m = Load(id);
            // The dice are asked for one stop per reel, in order, and the result is exactly those stops.
            var asked = new List<int>();
            int[] forced = { 3, 7, 11 };
            object s = spin.Invoke(null, new object[] { id, (Func<int, int>)(n => { asked.Add(n); return forced[asked.Count - 1]; }) });
            CollectionAssert.AreEqual(m.reels.Select(r => r.Length), asked);
            Assert.AreEqual(id, Field<byte>(s, "machine"));
            Assert.AreEqual(3, Field<byte>(s, "stop0"));
            Assert.AreEqual(7, Field<byte>(s, "stop1"));
            Assert.AreEqual(11, Field<byte>(s, "stop2"));

            // Seeded spins: the win is always the pay table's verdict on the symbols at the stops that are reported.
            var dice = Dice(1000 + (ulong)id);
            var args = new object[] { id, dice };
            var seenStops = new HashSet<int>();
            for (int i = 0; i < 20000; i++)
            {
                s = spin.Invoke(null, args);
                int a = Field<byte>(s, "stop0"), b = Field<byte>(s, "stop1"), c = Field<byte>(s, "stop2");
                int rule = m.Winner(m.reels[0][a], m.reels[1][b], m.reels[2][c]);
                Assert.AreEqual(rule, Field<sbyte>(s, "rule"));
                Assert.AreEqual(rule < 0 ? 0 : m.stake * m.payMultiple[rule], Field<int>(s, "winCents"));
                seenStops.Add(a); seenStops.Add(100 + b); seenStops.Add(200 + c);
                int top = TopSymbol(m);
                int tops = (m.reels[0][a] == top ? 1 : 0) + (m.reels[1][b] == top ? 1 : 0) + (m.reels[2][c] == top ? 1 : 0);
                Assert.AreEqual(tops == 2, (bool)Call("IsNearMiss", id, a, b, c));
            }
            Assert.AreEqual(m.reels.Sum(r => r.Length), seenStops.Count, m.name + ": some stop never comes up");
            Assert.Throws<TargetInvocationException>(() => spin.Invoke(null, new object[] { id, (Func<int, int>)(n => n) }), "a stop off the strip");
        }
    }

    [TestCaseSource(nameof(designs)), Timeout(120000)]
    public void AMillionSpinsAgreeWithTheExactReturn(int id, string name, int stake, double target, double hitLow, double hitHigh,
        int topMultiple, long combinations, long topWays)
    {
        const long spins = 1000000;
        var m = Load(id);
        var e = Enumerate(m);
        long won = (long)Call("SampleWinCents", id, spins, Dice(20261009 + (ulong)id));
        double sampled = won / (double)(spins * stake);
        // The standard error of the mean of n spins is volatility / sqrt(n); four of them is a 1-in-16,000 miss.
        double tolerance = 4 * e.Volatility / Math.Sqrt(spins);
        UnityEngine.Debug.Log("[CasinoSlotTests] " + id + " " + name + ": " + spins + " seeded spins returned " + (sampled * 100).ToString("F3") +
            "% against an exact " + (e.Return * 100).ToString("F3") + "% (tolerance ±" + (tolerance * 100).ToString("F2") + " points)");
        Assert.AreEqual(e.Return, sampled, tolerance, name);
        Assert.AreEqual(0, won % stake, "wins are whole multiples of the stake");
    }
}

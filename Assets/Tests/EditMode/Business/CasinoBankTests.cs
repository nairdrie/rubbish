using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The casino's BAR cashier (<c>CasinoBank</c>, Docs/Casino.md §5, §10) and the money helpers it shares with the shared tables.
/// The blackjack, roulette and slot play that used to live here moved to <c>CasinoTables</c> (see <c>CasinoTablesTests</c>).
/// A fake till stands in for the Ledger. Reflection because the logic lives in Assembly-CSharp.
/// </summary>
public sealed class CasinoBankTests
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type BankType => T("CasinoBank");

    const ulong Alice = 7, Bob = 9;

    /// <summary>A bank on a fake till: balances in cents, every movement logged.</summary>
    sealed class House
    {
        public readonly object bank = Activator.CreateInstance(BankType);
        public readonly Dictionary<ulong, long> cash = new Dictionary<ulong, long>();
        public readonly List<string> log = new List<string>();
        public bool near = true;
        public float drunk;
        public double now = 1000;

        public House()
        {
            Set("CashOf", (Func<ulong, long>)(c => cash.TryGetValue(c, out long v) ? v : -1));
            Set("Charge", (Func<ulong, long, string, string, bool>)((c, cents, id, why) =>
            {
                if (!cash.ContainsKey(c) || cents <= 0) return false;
                cash[c] -= cents;
                log.Add($"-{cents} {id}");
                return true;
            }));
            Set("Credit", (Func<ulong, long, string, string, bool>)((c, cents, id, why) =>
            {
                if (!cash.ContainsKey(c) || cents <= 0) return false;
                cash[c] += cents;
                log.Add($"+{cents} {id}");
                return true;
            }));
            Set("Near", (Func<ulong, int, int, bool>)((c, game, index) => near));
            Set("DrunkLevelOf", (Func<ulong, float>)(c => drunk));
        }

        void Set(string field, object value) => BankType.GetField(field).SetValue(bank, value);
        public long Charged => (long)BankType.GetProperty("TotalChargedCents").GetValue(bank);

        public object Drink(ulong c, int id) =>
            BankType.GetMethods(Any).First(x => x.Name == "Drink" && x.GetParameters().Length == 3).Invoke(bank, new object[] { c, id, now += 10 });
    }

    static T F<T>(object view, string name)
    {
        var f = view.GetType().GetField(name);
        if (f != null) return (T)f.GetValue(view);
        return (T)view.GetType().GetProperty(name).GetValue(view);
    }

    static string Err(object view) => F<object>(view, "error").ToString();

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

    // ================================================================================================ the bar

    [Test]
    public void Bar_ChargesTheTab_AndCutsOffAlcoholNearTheCap()
    {
        var h = new House();
        h.cash[Alice] = 5000;
        var v = h.Drink(Alice, 2);
        Assert.AreEqual("Ok", Err(v));
        Assert.AreEqual(1400, F<int>(v, "priceCents"));
        Assert.AreEqual(0.30f, F<float>(v, "units"), 1e-6f);
        Assert.AreEqual(3600, h.cash[Alice]);
        StringAssert.StartsWith("-1400 bar_tab", h.log[0]);

        h.drunk = 1.14f;
        Assert.AreEqual("Ok", Err(h.Drink(Alice, 0)), "still served just under the cut-off: going overboard is the player's call");
        h.drunk = 1.15f;
        long before = h.cash[Alice];
        Assert.AreEqual("CutOff", Err(h.Drink(Alice, 0)));
        Assert.AreEqual("CutOff", Err(h.Drink(Alice, 1)));
        Assert.AreEqual(before, h.cash[Alice]);
        v = h.Drink(Alice, 3);
        Assert.AreEqual("Ok", Err(v), "water is always sold");
        Assert.Less(F<float>(v, "units"), 0f);
        Assert.AreEqual(before - 100, h.cash[Alice]);

        Assert.AreEqual("BadDrink", Err(h.Drink(Alice, 4)));
        h.cash[Alice] = 50;
        h.drunk = 0f;
        Assert.AreEqual("CantAfford", Err(h.Drink(Alice, 3)));
        Assert.AreEqual(50, h.cash[Alice]);
    }

    [Test]
    public void Bar_RefusesWhenTooFar_WhenThrottled_AndWithoutABusiness()
    {
        var h = new House();
        h.cash[Alice] = 5000;
        h.near = false;
        Assert.AreEqual("TooFar", Err(h.Drink(Alice, 0)));
        Assert.AreEqual(5000, h.cash[Alice], "nothing charged");
        h.near = true;
        Assert.AreEqual("NoBusiness", Err(h.Drink(Bob, 0)), "no business, no tab");

        // the host's own floor between two drinks by one player (the bank is told the same instant twice)
        var m = BankType.GetMethods(Any).First(x => x.Name == "Drink" && x.GetParameters().Length == 3);
        Assert.AreEqual("Ok", Err(m.Invoke(h.bank, new object[] { Alice, 3, 5000.0 })));
        Assert.AreEqual("Throttled", Err(m.Invoke(h.bank, new object[] { Alice, 3, 5000.2 })));
        Assert.AreEqual("Ok", Err(m.Invoke(h.bank, new object[] { Alice, 3, 5000.6 })));
        Assert.AreEqual(4800, h.cash[Alice]);
    }

    // ================================================================================================ the shared helpers

    [Test]
    public void BigWin_IsAThousandDollarsOrFiftyTimesTheStake()
    {
        var big = BankType.GetMethod("IsBig", Any);
        bool Big(long won, long stake) => (bool)big.Invoke(null, new object[] { won, stake });
        Assert.IsTrue(Big(100000, 0), "$1,000 is big whatever the stake");
        Assert.IsFalse(Big(99999, 0));
        Assert.IsTrue(Big(5000, 100), "50x a $1 stake");
        Assert.IsFalse(Big(4900, 100));
        Assert.IsTrue(Big(1250, 25), "50x a $0.25 stake");
        Assert.IsFalse(Big(0, 100));
        Assert.IsFalse(Big(-100000, 100), "a loss is never a big win");
    }

    [Test]
    public void EveryRefusalHasWords()
    {
        var describe = BankType.GetMethods(Any).First(x => x.Name == "Describe" && x.GetParameters()[0].ParameterType == T("CasinoError"));
        foreach (object e in Enum.GetValues(T("CasinoError")))
        {
            string words = (string)describe.Invoke(null, new[] { e });
            if (e.ToString() == "Ok") Assert.AreEqual("", words);
            else Assert.IsNotEmpty(words, e.ToString());
        }
    }

    // ================================================================================================ the books

    [Test]
    public void Bar_NoMoneyIsCreatedOrDestroyed_Over20kOrders()
    {
        var rng = Seeded(20261009);
        var h = new House();
        ulong[] players = { Alice, Bob };
        var expected = new Dictionary<ulong, long> { { Alice, 40_000 }, { Bob, 900 } };
        foreach (var p in players) h.cash[p] = expected[p];
        long refused = 0, served = 0;
        for (int i = 0; i < 20000; i++)
        {
            ulong p = players[rng(2)];
            h.drunk = rng(4) == 0 ? 1.2f : 0f;
            var v = h.Drink(p, rng(5));
            if (Err(v) == "Ok") { expected[p] -= F<int>(v, "priceCents"); served++; }
            else refused++;
            if (rng(7) == 0) { h.cash[p] += 1500; expected[p] += 1500; } // a payday
        }
        foreach (var p in players)
        {
            Assert.AreEqual(expected[p], h.cash[p], "player " + p + ": the till against the sum of the replies");
            Assert.GreaterOrEqual(h.cash[p], 0, "nobody is ever charged below zero");
        }
        Assert.Greater(served, 1000);
        Assert.Greater(refused, 0, "the poor, drunk player was turned away sometimes");
    }
}

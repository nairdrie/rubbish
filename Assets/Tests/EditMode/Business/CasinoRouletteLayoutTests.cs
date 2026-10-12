using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The roulette layout mapping (Docs/Casino.md §9.1, Assets/Scripts/Casino/World/CasinoRouletteLayout.cs): every one of the
/// 161 spots must be found again at its own chip centre, and the grid must never hand back an illegal spot.
/// Reflection because the code lives in Assembly-CSharp.
/// </summary>
public sealed class CasinoRouletteLayoutTests
{
    static Type Layout => Type.GetType("CasinoRouletteLayout, Assembly-CSharp", true);
    static Type Rules => Type.GetType("RouletteRules, Assembly-CSharp", true);
    static Type BetType => Type.GetType("RouletteBet, Assembly-CSharp", true);
    static Type Kind => Type.GetType("RouletteBetType, Assembly-CSharp", true);

    static int SpotCount(object type) => (int)Rules.GetMethod("SpotCount").Invoke(null, new[] { type });

    static object Bet(object type, int index)
    {
        object bet = Activator.CreateInstance(BetType);
        BetType.GetField("type").SetValue(bet, type);
        BetType.GetField("index").SetValue(bet, (byte)index);
        return bet;
    }

    static object SpotAt(float u, float v) => Layout.GetMethod("SpotAt").Invoke(null, new object[] { u, v });

    static bool Centre(object bet, out float u, out float v)
    {
        var args = new object[] { bet, 0f, 0f };
        bool ok = (bool)Layout.GetMethod("BetCentre").Invoke(null, args);
        u = (float)args[1]; v = (float)args[2];
        return ok;
    }

    static string Name(object bet) => BetType.GetField("type").GetValue(bet) + "#" + BetType.GetField("index").GetValue(bet);

    [Test]
    public void EverySpotIsFoundAtItsOwnCentre()
    {
        int total = 0;
        foreach (var type in Enum.GetValues(Kind))
        {
            int n = SpotCount(type);
            for (int i = 0; i < n; i++)
            {
                var bet = Bet(type, i);
                Assert.IsTrue(Centre(bet, out float u, out float v), Name(bet) + " has no centre");
                var back = SpotAt(u, v);
                Assert.IsNotNull(back, Name(bet) + " not found at (" + u + ", " + v + ")");
                Assert.AreEqual(Name(bet), Name(back), "round trip at (" + u + ", " + v + ")");
                total++;
            }
        }
        Assert.AreEqual(161, total, "the layout has 161 spots");
    }

    [Test]
    public void TheGridOnlyReturnsLegalSpotsAndReachesEverySpot()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (float v = -1.6f; v <= 3.1f; v += 0.0125f)
            for (float u = -1.1f; u <= 13.1f; u += 0.0125f)
            {
                var s = SpotAt(u, v);
                if (s == null) continue;
                object type = BetType.GetField("type").GetValue(s);
                int index = (byte)BetType.GetField("index").GetValue(s);
                Assert.Less(index, SpotCount(type), "illegal spot " + Name(s) + " at (" + u + ", " + v + ")");
                seen.Add(Name(s));
            }
        Assert.AreEqual(161, seen.Count, "every spot is reachable by the pointer");
    }

    [Test]
    public void OffTheLayoutIsNull()
    {
        Assert.IsNull(SpotAt(-3f, 1f));
        Assert.IsNull(SpotAt(14f, 1f));
        Assert.IsNull(SpotAt(5f, 3.5f));
        Assert.IsNull(SpotAt(5f, -1.6f));
        Assert.IsNull(SpotAt(13f, -0.5f));
    }

    [Test]
    public void NumbersAreWhereTheyAreOnTheCloth()
    {
        // 1 is the first cell, 3 above it, 34 sits at u 11..12
        Assert.AreEqual("Straight#1", Name(SpotAt(0.5f, 0.5f)));
        Assert.AreEqual("Straight#3", Name(SpotAt(0.5f, 2.5f)));
        Assert.AreEqual("Straight#36", Name(SpotAt(11.5f, 2.5f)));
        Assert.AreEqual("Straight#0", Name(SpotAt(-0.5f, 0.7f)));
        Assert.AreEqual("Straight#37", Name(SpotAt(-0.5f, 2.3f)));
        Assert.AreEqual("Column#0", Name(SpotAt(12.5f, 0.5f)));
        Assert.AreEqual("Dozen#2", Name(SpotAt(10f, -0.4f)));
        Assert.AreEqual("Red#0", Name(SpotAt(5f, -1.0f)));
    }
}

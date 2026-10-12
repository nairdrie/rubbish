using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// The sort-streak ladder and the 2026-10-12 pace targets (Docs/Progression.md §3.6). Runtime code is in Assembly-CSharp, so this
/// asmdef reaches it by reflection, like the other economy tests.
/// </summary>
public class SortLadderTests
{
    const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;
    static Type Ladder => Type.GetType("SortLadder, Assembly-CSharp", true);
    static Type Combo => Type.GetType("SortCombo, Assembly-CSharp", true);
    static Type Rep => Type.GetType("RepValues, Assembly-CSharp", true);
    static Type Prog => Type.GetType("Progression, Assembly-CSharp", true);

    static object Call(Type t, string method, params object[] args) => t.GetMethod(method, Pub, null, Types(args), null).Invoke(null, args);
    static Type[] Types(object[] args) { var t = new Type[args.Length]; for (int i = 0; i < t.Length; i++) t[i] = args[i].GetType(); return t; }
    static float RepConst(string name) => (float)Rep.GetField(name, Pub).GetRawConstantValue();

    [SetUp]
    public void ClearTheStreak() => Combo.GetMethod("Clear", Pub).Invoke(null, null);

    // ------------------------------------------------------------------ the ladder

    [TestCase(0, 0, 1f)]
    [TestCase(1, 0, 1f)]
    [TestCase(2, 0, 1f)]
    [TestCase(3, 1, 1.5f)]
    [TestCase(5, 1, 1.5f)]
    [TestCase(6, 2, 2f)]
    [TestCase(9, 2, 2f)]
    [TestCase(10, 3, 3f)]
    [TestCase(14, 3, 3f)]
    [TestCase(15, 4, 4f)]
    [TestCase(21, 4, 4f)]
    [TestCase(22, 5, 5f)]
    [TestCase(34, 5, 5f)]
    [TestCase(35, 6, 10f)]
    [TestCase(5000, 6, 10f)]
    public void StreaksClimbTheLadderToTenTimes(int streak, int tier, float multiplier)
    {
        Assert.AreEqual(tier, (int)Call(Ladder, "TierFor", streak), "tier");
        Assert.AreEqual(multiplier, (float)Call(Ladder, "MultiplierFor", streak), 0.0001f, "multiplier");
        Assert.AreEqual(multiplier, (float)Call(Combo, "MultiplierFor", streak), 0.0001f, "SortCombo agrees");
    }

    [Test]
    public void NegativeStreaksAreJustCold()
    {
        Assert.AreEqual(0, (int)Call(Ladder, "TierFor", -7));
        Assert.AreEqual(1f, (float)Call(Ladder, "MultiplierFor", int.MinValue));
    }

    [Test]
    public void HotterTiersPayMoreAndAllowLessTimeBetweenSorts()
    {
        float lastMult = 0f, lastWindow = float.MaxValue;
        for (int tier = 0; tier <= 6; tier++)
        {
            float m = (float)Call(Ladder, "Multiplier", tier), w = (float)Call(Ladder, "Window", tier);
            Assert.Greater(m, lastMult, $"tier {tier} pays more");
            Assert.Less(w, lastWindow, $"tier {tier} is tighter");
            lastMult = m; lastWindow = w;
        }
        Assert.AreEqual(10f, lastMult);
        Assert.AreEqual(3.0f, (float)Call(Ladder, "Window", 0), "a cold streak is forgiving");
        Assert.AreEqual(1.8f, lastWindow, "the top tier is a sprint");
    }

    [Test]
    public void EveryTierHasANameExceptTheColdOne()
    {
        Assert.AreEqual("", (string)Call(Ladder, "Name", 0));
        for (int tier = 1; tier <= 6; tier++) Assert.IsNotEmpty((string)Call(Ladder, "Name", tier));
        Assert.AreEqual("TRASH GOD", (string)Call(Ladder, "Name", 6));
    }

    [TestCase(0, 3)]
    [TestCase(2, 3)]
    [TestCase(3, 6)]
    [TestCase(21, 22)]
    [TestCase(34, 35)]
    [TestCase(35, -1)]
    public void TheHudCanSayHowFarTheNextTierIs(int streak, int next) =>
        Assert.AreEqual(next, (int)Call(Ladder, "NextTierAt", streak));

    // ------------------------------------------------------------------ cooling: slow is a step down, not the end

    [Test]
    public void ASortInsideTheWindowKeepsTheWholeStreak()
    {
        Assert.AreEqual(35, (int)Call(Ladder, "Cool", 35, 1.79f));
        Assert.AreEqual(12, (int)Call(Ladder, "Cool", 12, 2.39f));
    }

    [Test]
    public void EachMissedWindowCoolsOneTier()
    {
        Assert.AreEqual(22, (int)Call(Ladder, "Cool", 40, 1.85f), "x10 falls to the start of x5");
        Assert.AreEqual(15, (int)Call(Ladder, "Cool", 40, 1.85f + 2.0f), "another window: x4");
        Assert.AreEqual(10, (int)Call(Ladder, "Cool", 40, 1.85f + 2.0f + 2.2f), "and x3");
    }

    [Test]
    public void ALongPauseEndsTheStreakEntirely()
    {
        Assert.AreEqual(0, (int)Call(Ladder, "Cool", 40, 60f));
        Assert.AreEqual(0, (int)Call(Ladder, "Cool", 4, 2.9f), "x1.5 has nowhere lower to go");
        Assert.AreEqual(0, (int)Call(Ladder, "Cool", 0, 0f));
    }

    // ------------------------------------------------------------------ the live streak

    static float Register(float now) => (float)Combo.GetMethod("RegisterCorrect", Pub, null, new[] { typeof(float) }, null).Invoke(null, new object[] { now });
    static int StreakAt(float now) => (int)Combo.GetMethod("StreakAt", Pub).Invoke(null, new object[] { now });
    static void Break(float now) => Combo.GetMethod("Break", Pub, null, new[] { typeof(float) }, null).Invoke(null, new object[] { now });

    [Test]
    public void ARunOfFastCorrectSortsPaysEveryTierAndReachesTen()
    {
        float t = 1000f, last = 0f;
        var seen = new System.Collections.Generic.List<float>();
        for (int i = 1; i <= 36; i++)
        {
            t += 1.0f;
            last = Register(t);
            if (i == 1 || i == 3 || i == 6 || i == 10 || i == 15 || i == 22 || i == 35) seen.Add(last);
        }
        Assert.AreEqual(new[] { 1f, 1.5f, 2f, 3f, 4f, 5f, 10f }, seen.ToArray());
        Assert.AreEqual(10f, last);
    }

    [Test]
    public void AWrongBinEndsTheStreakAtOnce()
    {
        float t = 2000f;
        for (int i = 0; i < 12; i++) Register(t += 1f);
        Assert.AreEqual(12, StreakAt(t));
        Break(t + 0.2f);
        Assert.AreEqual(0, StreakAt(t + 0.2f));
        Assert.AreEqual(1f, Register(t + 1f), "the next right bin starts from the bottom");
    }

    [Test]
    public void FetchingTheNextBagCostsATierNotTheStreak()
    {
        float t = 3000f;
        for (int i = 0; i < 12; i++) Register(t += 1f);          // x3 (streak 12)
        Assert.AreEqual(12, StreakAt(t + 2.0f));
        Assert.AreEqual(6, StreakAt(t + 2.6f), "one window gone: back to the start of x2");
        float next = Register(t + 2.6f);                         // sort again before it cools more
        Assert.AreEqual(2f, next, "keeps going from x2, streak 7");
        Assert.AreEqual(7, StreakAt(t + 2.6f));
    }

    [Test]
    public void ABrokenListenerNeverBreaksTheSortThatRaisedIt()
    {
        // the caller pays out right after RegisterCorrect returns: an exception from the HUD must not skip that
        var changed = Combo.GetEvent("Changed", Pub);
        Action<int, float> boom = (streak, mult) => throw new InvalidOperationException("boom from a listener");
        changed.AddEventHandler(null, boom);
        try
        {
            LogAssert.Expect(LogType.Exception, new Regex("boom from a listener"));
            float m = 0f;
            Assert.DoesNotThrow(() => m = Register(7000f));
            Assert.AreEqual(1f, m, "the multiplier still comes back");
            Assert.AreEqual(1, StreakAt(7000f), "and the streak still counted it");
        }
        finally { changed.RemoveEventHandler(null, boom); }
    }

    [Test]
    public void ClockReadingsBeforeTheLastSortAreHarmless()
    {
        Register(5000f);
        Assert.AreEqual(1, StreakAt(4000f));
    }

    // ------------------------------------------------------------------ the pace targets (Docs/Progression.md §3.6)

    [Test]
    public void OneFullFirstLoadEarnsRankTwoBeforeTheLastBagIsSorted()
    {
        // Curb Pup: 120 kg / 6.9 kg a bag ≈ 17 bags, ~9 items each, from about 7 heaps. The messy start's backlog is all late.
        const int bags = 17, curbs = 6, items = 152;
        float collecting = bags * RepConst("BagLate") + curbs * RepConst("CurbClean");
        float sorting = items * RepConst("ItemSorted");
        float rank2 = (float)Call(Prog, "RankRep", 2);

        Assert.Less(collecting, rank2, "driving round alone is not a promotion");
        Assert.GreaterOrEqual(collecting + sorting, rank2 * 1.5f, "a whole load is comfortably a promotion (a casual run that skips some of it still gets there)");
        Assert.GreaterOrEqual(collecting + sorting * 0.5f, rank2, "and it lands while you are still sorting");
    }

    [Test]
    public void RankThreeIsAboutTwoLoadsAndRankTwoNeedsNoCleanDays()
    {
        float perLoad = 17 * RepConst("BagLate") + 6 * RepConst("CurbClean") + 152 * RepConst("ItemSorted");
        Assert.LessOrEqual((float)Call(Prog, "RankRep", 3), perLoad * 2f, "rank 3 (the Bag Belt) by the second load");
        Assert.AreEqual(0, (int)Call(Prog, "RankCleanDays", 2));
        Assert.AreEqual(0, (int)Call(Prog, "RankCleanDays", 3));
    }

    [Test]
    public void CleanDayMinimumsNeverDecreaseAndTheTopRankIsStillAFewDaysAway()
    {
        int last = 0;
        for (int rank = 1; rank <= 12; rank++)
        {
            int days = (int)Call(Prog, "RankCleanDays", rank);
            Assert.GreaterOrEqual(days, last, $"rank {rank}");
            last = days;
        }
        Assert.GreaterOrEqual(last, 5, "rank 12 still takes some days; the Clean Day gate was cut, not removed");
        Assert.LessOrEqual(last, 10, "but it no longer parks 20 days of Rep behind a clock");
    }
}

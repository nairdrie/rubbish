using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>The pure maths of being drunk (Docs/Casino.md §5). Reflection because DrunkRules lives in Assembly-CSharp.</summary>
public sealed class DrunkRulesTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    static Type Rules => Type.GetType("DrunkRules, Assembly-CSharp", true);
    static T Const<T>(string field) => (T)Rules.GetField(field, Static).GetValue(null);
    static float F(string method, params object[] args) => (float)Rules.GetMethod(method, Static).Invoke(null, args);
    static string Stage(float level) => Rules.GetMethod("Stage", Static).Invoke(null, new object[] { level }).ToString();
    static float Chance(float level, float seconds) => F("PassOutChance", level, seconds);

    [Test]
    public void TheNumbersAreTheOnesInTheDoc()
    {
        Assert.AreEqual(1.2f, Const<float>("MaxLevel"));
        Assert.AreEqual(0.25f, Const<float>("DecayPerGameHour"));
        Assert.AreEqual(0.25f, Const<float>("WakeLevel"));
        Assert.AreEqual(4000, Const<int>("CabFareCents"));
        Assert.AreEqual("Tipsy", Stage(Const<float>("WakeLevel")), "you wake hung over, not sober");
    }

    [Test]
    public void StagesStartAtTheirThresholds()
    {
        Assert.AreEqual("Sober", Stage(0f));
        Assert.AreEqual("Sober", Stage(0.19f));
        Assert.AreEqual("Tipsy", Stage(0.2f));
        Assert.AreEqual("Tipsy", Stage(0.49f));
        Assert.AreEqual("Drunk", Stage(0.5f));
        Assert.AreEqual("Drunk", Stage(0.79f));
        Assert.AreEqual("Wasted", Stage(0.8f));
        Assert.AreEqual("Wasted", Stage(1.2f));
        Assert.AreEqual("Wasted", Stage(99f));
        Assert.AreEqual("Sober", Stage(-1f));
        Assert.AreEqual("Sober", Stage(float.NaN));
    }

    [Test]
    public void DrinksAddAndTheLevelStaysInRange()
    {
        Assert.AreEqual(0.12f, F("AfterDrink", 0f, 0.12f), 1e-6f);
        Assert.AreEqual(0.62f, F("AfterDrink", 0.12f, 0.2f + 0.3f), 1e-6f);
        Assert.AreEqual(1.2f, F("AfterDrink", 1.1f, 0.3f), "capped");
        Assert.AreEqual(0.4f, F("AfterDrink", 0.5f, -0.1f), 1e-6f, "water sobers");
        Assert.AreEqual(0f, F("AfterDrink", 0.05f, -0.1f), "never below sober");
        // Two cocktails and a whisky is Wasted; four beers is only Tipsy.
        Assert.AreEqual("Wasted", Stage(F("AfterDrink", F("AfterDrink", F("AfterDrink", 0f, 0.3f), 0.3f), 0.2f)));
        Assert.AreEqual("Tipsy", Stage(0.12f * 4));
    }

    [Test]
    public void ItWearsOffAtAQuarterAnHour()
    {
        Assert.AreEqual(0.75f, F("Decay", 1f, 1f), 1e-6f);
        Assert.AreEqual(0.5f, F("Decay", 1f, 2f), 1e-6f);
        Assert.AreEqual(0f, F("Decay", 1f, 4f), 1e-6f);
        Assert.AreEqual(0f, F("Decay", 1f, 100f));
        Assert.AreEqual(1f, F("Decay", 1f, -5f), "time never runs backwards");
        Assert.AreEqual(1.2f, F("Decay", 5f, 0f), "an out-of-range level is clamped first");
        Assert.AreEqual(4.8f, F("GameHoursToSober", 1.2f), 1e-5f);
        // Decay in steps is the same as decay in one go.
        float stepped = 1f;
        for (int i = 0; i < 10; i++) stepped = F("Decay", stepped, 0.1f);
        Assert.AreEqual(F("Decay", 1f, 1f), stepped, 1e-5f);
    }

    [TestCase("Vision01")]
    [TestCase("Walk01")]
    [TestCase("Drive01")]
    public void EffectsRiseSmoothlyFromNothingToFull(string curve)
    {
        Assert.AreEqual(0f, F(curve, 0f));
        Assert.AreEqual(1f, F(curve, 1.2f));
        float last = 0f;
        for (int i = 0; i <= 120; i++)
        {
            float v = F(curve, i / 100f);
            Assert.That(v, Is.InRange(0f, 1f));
            Assert.GreaterOrEqual(v, last, curve + " dips at " + i / 100f);
            Assert.Less(v - last, 0.05f, curve + " jumps at " + i / 100f);
            last = v;
        }
    }

    [Test]
    public void DrivingGoesWrongBeforeWalkingDoes()
    {
        Assert.AreEqual(0f, F("Walk01", 0.2f), "walking is fine until Tipsy");
        Assert.Greater(F("Drive01", 0.2f), 0f);
        Assert.Greater(F("Vision01", 0.2f), 0f);
        for (float level = 0.15f; level < 0.8f; level += 0.05f) Assert.Greater(F("Drive01", level), F("Walk01", level));
        Assert.AreEqual(1f, F("Drive01", 0.8f), 1e-6f, "at its worst from Wasted");
        Assert.AreEqual(1f, F("Walk01", 1f), 1e-6f);
    }

    [Test]
    public void NobodyPassesOutBelowWasted()
    {
        for (int i = 0; i <= 80; i++) Assert.AreEqual(0f, Chance(i / 100f, 1f), "level " + i / 100f);
        Assert.AreEqual(0f, Chance(0.8f, 3600f), "even over an hour");
        Assert.AreEqual(0f, Chance(1f, 0f), "no time, no chance");
        Assert.AreEqual(0f, Chance(1f, -1f));
    }

    [Test]
    public void ThePassOutChanceRisesAndIsCertainAtTheTop()
    {
        float last = 0f;
        for (int i = 81; i <= 120; i++)
        {
            float c = Chance(i / 100f, 1f);
            Assert.That(c, Is.InRange(0f, 1f));
            Assert.Greater(c, last, "level " + i / 100f);
            last = c;
        }
        Assert.AreEqual(1f, Chance(1.2f, 1f));
        Assert.AreEqual(1f, Chance(1.2f, 0.01f), "certain however short the check");
        Assert.AreEqual(1f, Chance(5f, 1f));
        Assert.Less(Chance(1.19f, 1f), 1f);
        // A longer wait between checks is a bigger chance per check...
        Assert.Greater(Chance(1f, 2f), Chance(1f, 1f));
        // ...but the same odds over the same time: surviving two 1 s checks = surviving one 2 s check.
        float one = 1f - Chance(1f, 1f), two = 1f - Chance(1f, 2f);
        Assert.AreEqual(two, one * one, 1e-5f);
    }

    [Test]
    public void TheDocumentedWaitsHold()
    {
        Assert.AreEqual(120f, F("ExpectedSecondsToPassOut", 0.9f), 0.5f);
        Assert.AreEqual(40f, F("ExpectedSecondsToPassOut", 1.0f), 0.5f);
        Assert.AreEqual(13.3f, F("ExpectedSecondsToPassOut", 1.1f), 0.5f);
        Assert.AreEqual(0f, F("ExpectedSecondsToPassOut", 1.2f));
        Assert.IsTrue(float.IsPositiveInfinity(F("ExpectedSecondsToPassOut", 0.5f)));
        // The per-check chance is that rate: at 0.9, a check every second is about 1 in 120.
        Assert.AreEqual(1f / 120f, Chance(0.9f, 1f), 2e-4f);
    }
}

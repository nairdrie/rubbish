using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

/// <summary>Clock-only crossing contracts. Never starts a network session or reads/writes a save.</summary>
public class GuardedCrossingRulesTests
{
    static Type Rules => Type.GetType("GuardedCrossingRules, Assembly-CSharp", true);
    static object Call(string name, params object[] args) => Rules.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static float Constant(string name) => (float)Rules.GetField(name).GetRawConstantValue();
    static double SecondsPerHour => (float)Type.GetType("GameClock, Assembly-CSharp", true)
        .GetProperty("SecondsPerGameHour").GetValue(null);

    readonly struct Sample
    {
        public readonly bool active;
        public readonly long serial;
        public readonly float age;
        public readonly int children;
        public Sample(bool active, long serial, float age, int children)
        { this.active = active; this.serial = serial; this.age = age; this.children = children; }
    }

    static Sample Event(double hours, ulong seed = 792UL, int site = 0, int sites = 4, float length = 12f)
    {
        object[] args = { hours, seed, site, sites, length, 0L, 0f, 0 };
        bool active = (bool)Call("TryEvent", args);
        return new Sample(active, (long)args[5], (float)args[6], (int)args[7]);
    }

    static double FirstEvent(ulong seed, int site, int day = 0)
    {
        double start = day * 24.0 + Constant("FirstHour");
        for (double seconds = 0; seconds < Constant("CycleSeconds") + 10; seconds += .25)
        {
            double hours = start + seconds / SecondsPerHour;
            if (Event(hours, seed, site).active) return hours;
        }
        Assert.Fail("Every eligible crossing site should have an occasional daytime event.");
        return 0;
    }

    [TestCase(-1.0)]
    [TestCase(0.0)]
    [TestCase(7.2)]
    [TestCase(18.4)]
    [TestCase(23.9)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void CrossingsDoNotRunAtNightOrWithAnInvalidClock(double hours) => Assert.IsFalse(Event(hours).active);

    [TestCase(-1, 4)]
    [TestCase(4, 4)]
    [TestCase(0, 0)]
    [TestCase(0, -1)]
    public void InvalidSiteCannotScheduleACrossing(int site, int count) => Assert.IsFalse(Event(9, site: site, sites: count).active);

    [Test]
    public void ScheduleUsesThreeToFiveChildrenAndIncludesAllGroupSizes()
    {
        var counts = new HashSet<int>();
        for (ulong seed = 1; seed <= 20; seed++)
            for (int site = 0; site < 4; site++)
            {
                var sample = Event(FirstEvent(seed, site), seed, site);
                Assert.That(sample.children, Is.InRange(3, 5));
                counts.Add(sample.children);
            }
        CollectionAssert.AreEquivalent(new[] { 3, 4, 5 }, counts);
    }

    [Test]
    public void SharedSeedAndTimeGiveHostGuestAndLateJoinTheSameCrossing()
    {
        const ulong seed = 64111UL;
        double start = FirstEvent(seed, 2, 3);
        for (int step = 0; step < 60; step++) Event(start + step * .1 / SecondsPerHour, seed, 2);
        double now = start + 8.0 / SecondsPerHour;
        var host = Event(now, seed, 2);
        // Irrelevant calls cannot advance an RNG or mutate an event that a late joiner missed.
        for (int i = 0; i < 10; i++) Event(now + i, (ulong)i, i % 4);
        var guest = Event(now, seed, 2);
        Assert.IsTrue(host.active);
        Assert.AreEqual(host.active, guest.active);
        Assert.AreEqual(host.serial, guest.serial);
        Assert.AreEqual(host.age, guest.age);
        Assert.AreEqual(host.children, guest.children);
        Assert.AreEqual(Call("ChildProgress", host.age, 12f, 1), Call("ChildProgress", guest.age, 12f, 1));
    }

    [Test]
    public void CrossingsAreOccasionalAndSitesAreStaggered()
    {
        double start = FirstEvent(792UL, 0);
        var current = Event(start);
        float duration = (float)Call("Duration", 12f, current.children);
        Assert.Less(duration, Constant("CycleSeconds") * .4f, "Traffic should spend most of its time unblocked.");
        Assert.IsFalse(Event(start + (duration + 2f) / SecondsPerHour).active);
        var next = Event(start + Constant("CycleSeconds") / SecondsPerHour);
        Assert.IsTrue(next.active);
        Assert.AreNotEqual(current.serial, next.serial);
        var starts = new HashSet<double>();
        for (int site = 0; site < 4; site++) starts.Add(FirstEvent(792UL, site));
        Assert.AreEqual(4, starts.Count, "All four crossings must not hold up town traffic together.");
    }

    [TestCase(4f, 3)]
    [TestCase(12f, 4)]
    [TestCase(22f, 5)]
    public void ChildrenFinishBeforeGuardReturnsAndStartOnlyAfterGuardStepsOut(float length, int children)
    {
        float start = Constant("GatherSeconds") + Constant("GuardEnterSeconds");
        float end = (float)Call("CrossEnd", length, children);
        for (int child = 0; child < children; child++)
        {
            Assert.AreEqual(0f, Call("ChildProgress", start, length, child));
            Assert.That((float)Call("ChildProgress", end, length, child), Is.EqualTo(1f).Within(.00001f));
        }
        Assert.Less((float)Call("ChildProgress", end - .05f, length, children - 1), 1f);
        float duration = (float)Call("Duration", length, children);
        Assert.That(duration - end, Is.EqualTo(Constant("GuardLeaveSeconds") + Constant("DepartSeconds")).Within(.001f));
        Assert.Greater(duration, end, "The guard stays in the road until the last child is over the curb.");
    }

    [Test]
    public void ChildProgressIsClampedAndEachChildHasAStaggeredStart()
    {
        float firstStart = Constant("GatherSeconds") + Constant("GuardEnterSeconds");
        float spacing = Constant("KidSpacingSeconds");
        Assert.AreEqual(0f, Call("ChildProgress", -10f, 12f, 0));
        Assert.AreEqual(1f, Call("ChildProgress", 1000f, 12f, 4));
        Assert.Greater((float)Call("ChildProgress", firstStart + spacing * .5f, 12f, 0), 0f);
        Assert.AreEqual(0f, Call("ChildProgress", firstStart + spacing * .5f, 12f, 1));
        Assert.AreEqual(Call("ChildProgress", firstStart + 1f, 1f, 0), Call("ChildProgress", firstStart + 1f, 0f, -1));
    }
}

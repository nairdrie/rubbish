using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>The guided first run's order (Docs/Campaign.md §14.8): the next lesson is always the first one not done.</summary>
public class TutorialRulesTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Step(string name) => Enum.Parse(R("TutorialStep"), name);
    static object Call(string method, params object[] args) => R("TutorialRules").GetMethod(method, Static).Invoke(null, args);
    static string NextName(int mask, bool disk) => Call("Next", mask, disk)?.ToString();
    static int Mark(int mask, string step) => (int)Call("Mark", mask, Step(step));

    [Test]
    public void ANewSaveStartsWithTheGoalCards()
    {
        Assert.AreEqual("Cards", NextName(0, false));
    }

    [Test]
    public void TheWalkThroughGoesInTheOrderTheJobDoes()
    {
        string[] order = { "Cards", "Truck", "Leave", "Collect", "Return", "Tour", "Tip", "Carry", "Sort", "Throw", "Laptop" };
        int mask = 0;
        foreach (string s in order)
        {
            Assert.AreEqual(s, NextName(mask, false));
            mask = Mark(mask, s);
        }
        Assert.IsNull(NextName(mask, false), "nothing left until a disk turns up");
    }

    [Test]
    public void ADonePlayerIsNeverToldAgainAndOutOfOrderWorkJustCounts()
    {
        // someone who tipped before the tour: the tour is still next in line, but the tip is never asked for
        int mask = 0;
        foreach (string s in new[] { "Cards", "Truck", "Leave", "Collect", "Return", "Tip" }) mask = Mark(mask, s);
        Assert.AreEqual("Tour", NextName(mask, false));
        mask = Mark(mask, "Tour");
        Assert.AreEqual("Carry", NextName(mask, false), "the tip was already done");
    }

    [Test]
    public void TheDiskLessonsWaitForTheWalkThroughAndForAFloppy()
    {
        int walk = (int)R("TutorialRules").GetProperty("WalkThroughDone", Static).GetValue(null);
        Assert.IsNull(NextName(walk, false));
        Assert.AreEqual("Disk", NextName(walk, true), "a floppy was found");
        Assert.AreEqual("Laptop", NextName(walk & ~(1 << Convert.ToInt32(Step("Laptop"))), true), "the walk-through comes first");
        int disk = Mark(walk, "Disk");
        Assert.AreEqual("DiskTab", NextName(disk, true));
        Assert.AreEqual("DiskTab", NextName(disk, false), "once taught about the disk, the tab stays due");
        Assert.IsNull(NextName(Mark(disk, "DiskTab"), true));
    }

    [Test]
    public void TheBadgeNumbersOnlyTheTenStepsOfTheJob()
    {
        Assert.AreEqual("", Call("Badge", Step("Cards")));
        Assert.AreEqual("1/10", Call("Badge", Step("Truck")));
        Assert.AreEqual("2/10", Call("Badge", Step("Leave")));
        Assert.AreEqual("4/10", Call("Badge", Step("Return")));
        Assert.AreEqual("10/10", Call("Badge", Step("Laptop")));
        Assert.AreEqual("", Call("Badge", Step("Disk")));
        Assert.AreEqual("", Call("Badge", Step("DiskTab")));
    }

    [Test]
    public void ThroughMarksEverythingUpToAndIncludingAStep()
    {
        int m = (int)Call("Through", Step("Return"));
        foreach (string s in new[] { "Cards", "Truck", "Collect", "Return" }) Assert.IsTrue((bool)Call("Done", m, Step(s)), s);
        foreach (string s in new[] { "Tour", "Tip", "Laptop", "Disk" }) Assert.IsFalse((bool)Call("Done", m, Step(s)), s);
    }

    // itemsSorted, cashEarned, kgRecycled, rep, starterProgress, truckModelLevel
    [TestCase(0, 0f, 0f, 0, 0, 0, false)]
    [TestCase(1, 0f, 0f, 0, 0, 0, true)]
    [TestCase(0, 25f, 0f, 0, 0, 0, true)]
    [TestCase(0, 0f, 3.5f, 0, 0, 0, true)]
    [TestCase(0, 0f, 0f, 2, 0, 0, true)]
    [TestCase(0, 0f, 0f, 0, 8, 0, true)]
    [TestCase(0, 0f, 0f, 0, 0, 1, true)]
    public void ASaveThatHasDoneAnythingAlreadyKnowsTheJob(int sorted, float earned, float kg, int rep, int starter, int truck, bool experienced) =>
        Assert.AreEqual(experienced, Call("LooksExperienced", sorted, earned, kg, rep, starter, truck));

    [Test]
    public void LaptopTabsAreRememberedOneBitEach()
    {
        int tabs = 0;
        int[] screen = { 0, 1, 2, 3 };
        Assert.AreEqual(0, Call("NextTab", tabs, screen));
        tabs = (int)Call("TabMark", tabs, 0);
        tabs = (int)Call("TabMark", tabs, 2);
        Assert.AreEqual(1, Call("NextTab", tabs, screen), "screen order, skipping the ones already clicked");
        tabs = (int)Call("TabMark", tabs, 1);
        Assert.AreEqual(3, Call("NextTab", tabs, screen));
        tabs = (int)Call("TabMark", tabs, 3);
        Assert.AreEqual(-1, Call("NextTab", tabs, screen));
        Assert.AreEqual(tabs, Call("TabMark", tabs, -4), "a bad tab index changes nothing");
        Assert.AreEqual(tabs, Call("TabMark", tabs, 99));
    }
}

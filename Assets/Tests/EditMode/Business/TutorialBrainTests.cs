using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The tutorial's mind (Docs/Campaign.md §14.8) played through with made-up facts: a whole first run in the order a new player
/// does it, the ways a player can skip ahead, and the guarantee that no step can wedge (each has a way out that needs only the player
/// to be in the right place).
/// </summary>
public class TutorialBrainTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Step(string name) => Enum.Parse(R("TutorialStep"), name);

    /// <summary>The facts and the memory as plain fields on boxed structs, so a test reads like a play-through.</summary>
    sealed class World
    {
        public object f = Activator.CreateInstance(R("TutorialFacts"));
        public object m = Activator.CreateInstance(R("TutorialMemory"));
        public int mask;

        public void F(string field, object value) => R("TutorialFacts").GetField(field).SetValue(f, value);
        public void M(string field, object value) => R("TutorialMemory").GetField(field).SetValue(m, value);
        public T GetF<T>(string field) => (T)R("TutorialFacts").GetField(field).GetValue(f);
        public T GetM<T>(string field) => (T)R("TutorialMemory").GetField(field).GetValue(m);

        public bool Done(string step) => (bool)R("TutorialBrain").GetMethod("Done", Static).Invoke(null, new[] { Step(step), f, m });
        public object Card(string step) => R("TutorialBrain").GetMethod("Card", Static).Invoke(null, new[] { Step(step), f, m });
        public string CardText(string step, string field) => (string)R("TutorialCard").GetField(field).GetValue(Card(step));
        public bool CardUrgent(string step) => (bool)R("TutorialCard").GetField("urgent").GetValue(Card(step));
        public string Target(string step) => R("TutorialCard").GetField("target").GetValue(Card(step)).ToString();

        /// <summary>What the runner does each tick: complete every step that is done, in order, and say which one is up.</summary>
        public string Advance(bool diskFound = false)
        {
            var rules = R("TutorialRules");
            for (int guard = 0; guard < 16; guard++)
            {
                object next = rules.GetMethod("Next", Static).Invoke(null, new object[] { mask, diskFound });
                if (next == null) return null;
                string name = next.ToString();
                if (!Done(name)) return name;
                mask = (int)rules.GetMethod("Mark", Static).Invoke(null, new object[] { mask, next });
            }
            return "stuck";
        }
    }

    static World NewSoloStart()
    {
        var w = new World();
        w.F("control", true);
        w.F("contractActive", true);
        w.F("oldBags", 56);
        w.F("districtName", "Mildly Pleasant");
        w.F("indoors", true);             // the solo garage
        w.F("truckThere", true);
        w.F("cargoKnown", true);
        w.F("capacityKg", 120f);
        w.F("heapDistance", 90f);
        w.F("truckDistance", 3f);
        w.F("tableDistance", 8f);
        return w;
    }

    // ------------------------------------------------------------------ the whole first run

    [Test]
    public void ANewSoloPlayerIsWalkedThroughTheWholeJobInOrder()
    {
        var w = NewSoloStart();
        var seen = new List<string>();
        void Expect(string step)
        {
            string up = w.Advance();
            Assert.AreEqual(step, up, $"after {string.Join(", ", seen)}");
            seen.Add(step);
        }

        Expect("Cards");
        w.M("cardsClosed", true);
        Expect("Truck");
        w.F("driving", true);
        Expect("Leave");
        w.F("indoors", false);                       // out of the garage door and into the town
        w.F("driving", false);
        Expect("Collect");

        // a bag at a time into the truck
        w.F("heapDistance", 12f);
        w.F("truckDistance", 20f);
        StringAssert.Contains("grab a bag", Body(w, "Collect"));
        w.F("carryingBag", true);
        Assert.AreEqual("Hopper", w.Target("Collect"), "a bag in hand points at the truck");
        w.F("carryingBag", false);
        w.F("loadKg", 60f);
        Assert.IsFalse(w.Done("Collect"), "half full is not full");
        w.F("loadKg", 118f);                         // under 4.5 kg of room left
        Expect("Return");
        Assert.IsTrue(w.CardUrgent("Return"), "a full truck is the loud one");
        Assert.AreEqual("DepotDoor", w.Target("Return"));

        w.F("indoors", true);                        // through the door, in the truck, with the load
        Expect("Tour");
        w.M("tourSeconds", 6f);
        Assert.AreEqual("Tour", w.Advance(), "half way round");
        w.M("tourSeconds", 12.5f);
        Expect("Tip");
        w.F("driving", true);
        Assert.AreEqual("TippingFloor", w.Target("Tip"));
        w.F("loadKg", 0f);                           // the load is on the floor
        w.M("tipped", 1);
        Expect("Carry");
        Assert.AreEqual("BagPile", w.Target("Carry"), "still seated: hop out and pick one up");
        w.F("driving", false);                       // hopped out
        w.F("carryingBag", true);
        Assert.AreEqual("SortTable", w.Target("Carry"));
        w.M("rips", 1);                              // slammed open at the station
        w.M("itemsAtRip", 3);
        Expect("Sort");
        w.M("sorted", 1);
        Assert.IsFalse(w.Done("Sort"), "two items for a bag with three");
        w.M("sorted", 2);
        Expect("Throw");
        w.F("carryingBag", true);
        Assert.AreEqual("SortTable", w.Target("Throw"));
        w.M("rips", 2);
        w.M("thrownRips", 1);
        Expect("Laptop");
        w.F("laptopOpen", true);
        w.M("tabsMask", 0b011);
        Assert.IsFalse(w.Done("Laptop"), "the third tab is still to click");
        w.M("tabsMask", 0b111);
        Assert.IsNull(w.Advance(), "the walk-through is over");
    }

    static string Title(World w, string step) => w.CardText(step, "title");
    static string Body(World w, string step) => w.CardText(step, "body");

    // ------------------------------------------------------------------ skipping ahead, and the ways out

    [Test]
    public void AGuestWhoStartsOutsideNeverGetsToldToLeaveTheGarage()
    {
        var w = NewSoloStart();
        w.F("indoors", false);
        w.M("cardsClosed", true);
        w.F("driving", true);
        Assert.AreEqual("Collect", w.Advance(), "Truck and Leave were both already true");
    }

    [Test]
    public void SomeoneWhoBringsAFullTruckHomeEarlyIsNotMadeToWaitForTheTrash()
    {
        var w = NewSoloStart();
        w.M("cardsClosed", true);
        w.F("driving", true);
        w.F("indoors", false);
        w.M("wentOutside", true);
        w.F("loadKg", 50f);                          // half a load...
        Assert.AreEqual("Collect", w.Advance());
        w.F("indoors", true);                        // ...and they drive home anyway
        Assert.AreEqual("Tour", w.Advance(), "Collect and Return are done by being home with cargo");
    }

    [Test]
    public void TheTourEndsWhenThePlayerDrivesOffOrGetsOnWithIt()
    {
        var w = NewSoloStart();
        w.M("cardsClosed", true); w.M("drove", true); w.M("wentOutside", true); w.M("sawFull", true);
        w.F("loadKg", 118f);
        w.F("indoors", true);
        Assert.AreEqual("Tour", w.Advance());
        w.M("tourSeconds", 1f);
        w.F("indoors", false);
        Assert.AreEqual("Tip", w.Advance(), "left the depot part-way: the tour is over");
    }

    [Test]
    public void ATruckWithNothingToTipIsNotAskedToTip()
    {
        var w = NewSoloStart();
        w.mask = (int)R("TutorialRules").GetMethod("Through", Static).Invoke(null, new[] { Step("Tour") });
        w.F("indoors", true);
        w.F("loadKg", 0f);                           // dumped it in the street, or tipped it between ticks
        Assert.AreEqual("Carry", w.Advance());
    }

    [Test]
    public void ThrowingABagBeforeBeingToldCountsAsTheThrowLesson()
    {
        var w = NewSoloStart();
        w.mask = (int)R("TutorialRules").GetProperty("WalkThroughDone", Static).GetValue(null);
        // roll the mask back to just before Carry
        int carry = Convert.ToInt32(Step("Carry"));
        w.mask &= (1 << carry) - 1;
        w.M("rips", 1); w.M("thrownRips", 1); w.M("itemsAtRip", 1); w.M("sorted", 1);
        Assert.AreEqual("Laptop", w.Advance(), "rip, sort and throw were all done by that one throw");
    }

    [Test]
    public void WithNoContractToExplainTheCardsStepEndsOnItsOwn()
    {
        var w = NewSoloStart();
        w.F("contractActive", false);
        Assert.AreEqual("Cards", w.Advance());
        w.M("controlSeconds", 30f);
        Assert.AreEqual("Truck", w.Advance());
    }

    [Test]
    public void TheCardsWaitForeverWhileAContractIsRunningAndUnread()
    {
        var w = NewSoloStart();
        w.M("controlSeconds", 9999f);
        Assert.AreEqual("Cards", w.Advance(), "a running contract must be read (or skipped) first");
    }

    // ------------------------------------------------------------------ the disk lessons

    [Test]
    public void TheDiskLessonsRunAfterTheWalkThroughAndTheInstallEndsTheFirstOne()
    {
        var w = NewSoloStart();
        w.mask = (int)R("TutorialRules").GetProperty("WalkThroughDone", Static).GetValue(null);
        Assert.IsNull(w.Advance(false));
        Assert.AreEqual("Disk", w.Advance(true));
        w.F("printerInstalled", true);
        Assert.AreEqual("DiskTab", w.Advance(true), "kept: now the tab");
        w.M("tabsMask", 1 << 3);
        Assert.IsNull(w.Advance(true));
    }

    [Test]
    public void ADiskKeptInOneGoSkipsStraightToTheTab()
    {
        var w = NewSoloStart();
        w.mask = (int)R("TutorialRules").GetProperty("WalkThroughDone", Static).GetValue(null);
        w.F("printerInstalled", true);
        Assert.AreEqual("DiskTab", w.Advance(true));
    }

    // ------------------------------------------------------------------ the words

    [Test]
    public void EveryStepHasAWordsCardWithAKeyNamedForThePad()
    {
        string[] steps = { "Truck", "Leave", "Collect", "Return", "Tour", "Tip", "Carry", "Sort", "Throw", "Laptop", "Disk", "DiskTab" };
        foreach (bool pad in new[] { false, true })
        {
            var w = NewSoloStart();
            w.F("gamepad", pad);
            w.F("driving", true);
            w.F("loadKg", 30f);
            foreach (string s in steps)
            {
                Assert.IsFalse(string.IsNullOrEmpty(Title(w, s)), s + " has a title");
                Assert.IsFalse(string.IsNullOrEmpty(Body(w, s)), s + " has a line of help");
            }
            string truck = Body(w, "Truck");
            StringAssert.Contains(pad ? ">X<" : ">E<", truck);
            StringAssert.DoesNotContain(pad ? ">E<" : ">X<", truck);
        }
    }

    [Test]
    public void APlayerStillSeatedInTheTruckIsToldToHopOutBeforePickingABagUp()
    {
        var w = NewSoloStart();
        w.F("driving", true);
        StringAssert.Contains("Hop out", Body(w, "Carry"));
        StringAssert.Contains("Hop out", Body(w, "Throw"));
        Assert.AreEqual("BagPile", w.Target("Carry"));
        w.F("driving", false);
        StringAssert.Contains("pick a bag up", Body(w, "Carry").ToLowerInvariant());
    }

    [Test]
    public void AtTheStationWithNoBagThePlayerIsSentBackForOne()
    {
        var w = NewSoloStart();
        w.F("inSortMode", true);
        w.F("carryingBag", true);
        StringAssert.Contains("slam", Body(w, "Carry"));
        w.F("carryingBag", false);
        StringAssert.Contains("need a bag", Body(w, "Carry"));
        Assert.AreEqual("BagPile", w.Target("Carry"));
    }

    [Test]
    public void TheTourVisitsTheLaptopThenTheFloorThenTheTable()
    {
        var w = NewSoloStart();
        w.M("tourSeconds", 0.5f);
        Assert.AreEqual("Laptop", w.Target("Tour"));
        w.M("tourSeconds", 4.5f);
        Assert.AreEqual("TippingFloor", w.Target("Tour"));
        w.M("tourSeconds", 8.5f);
        Assert.AreEqual("SortTable", w.Target("Tour"));
        w.M("tourSeconds", 500f);
        Assert.AreEqual("SortTable", w.Target("Tour"), "never runs past the last stop");
    }

    [Test]
    public void FullIsTheSameLineTheHudArrowUses()
    {
        var w = NewSoloStart();
        w.F("loadKg", 115.4f);                       // 4.6 kg of room: not yet
        Assert.IsFalse(w.Done("Collect"));
        w.F("loadKg", 115.6f);                       // under 4.5 kg left
        Assert.IsTrue(w.Done("Collect"));
    }

    [Test]
    public void PlayersOnFootNextToTheirTruckAreSentToDriveToADistantHeap()
    {
        var w = NewSoloStart();
        w.F("indoors", false);
        w.F("driving", false);
        w.F("heapDistance", 150f);
        w.F("truckDistance", 5f);
        Assert.AreEqual("Truck", w.Target("Collect"));
        w.F("truckDistance", 80f);
        Assert.AreEqual("OldTrash", w.Target("Collect"), "too far from the truck to walk back: just point at the heap");
        w.F("heapDistance", 10f);
        w.F("truckDistance", 12f);
        Assert.AreEqual("OldTrash", w.Target("Collect"));
    }
}

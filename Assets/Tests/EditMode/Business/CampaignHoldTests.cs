using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The host's contract tick run for real: the clock is skipped forward half an hour at a time and <c>Campaign.HostTick</c> runs
/// in between, with a stand-in for the running spawner. A full game day at 90% wins; old trash, or one dip, does not.
/// </summary>
public class CampaignHoldTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Acres = "mildly_pleasant";

    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => R(type).GetMethod(method, Static).Invoke(null, args);
    static T Prop<T>(string type, string name) => (T)R(type).GetProperty(name, Static).GetValue(null);
    static object District(string id) => Call("Districts", "Get", id);

    GameObject spawnerObject;

    [SetUp]
    public void SetUp()
    {
        Reset();
        Call("CurbTrashSpawner", "EnsureHouses");
        Call("Campaign", "OnSessionBegin");
        // A spawner that "is running": HostTick only works while there is one (its Update never runs in edit mode).
        spawnerObject = new GameObject("campaign-hold-test");
        var spawner = spawnerObject.AddComponent(R("CurbTrashSpawner"));
        R("CurbTrashSpawner").GetField("running", Inst).SetValue(spawner, true);
        R("CurbTrashSpawner").GetField("<Instance>k__BackingField", Static).SetValue(null, spawner);
    }

    [TearDown]
    public void TearDown()
    {
        if (spawnerObject != null) UnityEngine.Object.DestroyImmediate(spawnerObject);
        Reset();
    }

    static void Reset()
    {
        Call("GameSession", "ResetAll", false);
        Call("CurbTrashSpawner", "ResetStatics");
        Call("CityCleanliness", "ResetStatics");
        Call("Campaign", "ResetStatics");
        Call("GameClock", "ResetStatics");
        Call("CityCleanliness", "Invalidate");
    }

    /// <summary>Half a game hour passes and the host looks at the contract once.</summary>
    static void Tick(float hours = 0.5f)
    {
        Call("GameClock", "DebugSkipHours", hours);
        Call("CityCleanliness", "Invalidate");
        R("Campaign").GetField("nextTickAt", Static).SetValue(null, 0.0);
        Call("Campaign", "HostTick");
    }

    static int Permit(string id) => (int)Call("GameSession", "GetUpgradeLevel", id);
    static double Hold => Prop<double>("Campaign", "HoldHours");
    static int Attempts => Prop<int>("Campaign", "Attempts");
    static object Display => Prop<object>("Campaign", "Display");
    static T D<T>(string field) => (T)Display.GetType().GetField(field).GetValue(Display);
    static double LeftNow() =>
        (double)Display.GetType().GetMethod("HoursLeftAt").Invoke(Display, new object[] { Prop<double>("GameClock", "TotalHours") });
    static void Clear() => Call("Campaign", "DebugClearBacklog", Acres);
    static void Seed() => Call("Campaign", "OnSpawnerSession");

    [Test]
    public void AFullDayAtNinetyPercentWinsTheContractAndOpensTheMeadows()
    {
        Seed();
        Clear();
        Tick(0.5f);                                    // the first look only starts the clock
        for (int i = 0; i < 46; i++) Tick();           // 23 hours at 100%
        Assert.AreEqual(0, Permit("permit_minivan_meadows"), "not yet");
        Assert.AreEqual(23.0, Hold, 1e-3);
        Assert.AreEqual(1, D<byte>("qualifying"));
        double anchor = D<double>("holdStart");
        Assert.Greater(anchor, 0.0, "the countdown is anchored on the game clock");
        Assert.AreEqual(1.0, LeftNow(), 0.01, "one hour to go, from the anchor and this machine's clock");
        Tick(0.1f);
        Assert.AreEqual(anchor, D<double>("holdStart"), 0.021, "the anchor does not move while the hold runs (no traffic)");
        Assert.AreEqual(0.9, LeftNow(), 0.01);

        Tick(0.4f); Tick();                            // 23.5, then past the 24th hour
        Assert.AreEqual(1, Permit("permit_minivan_meadows"), "won: the city hands over the Meadows");
        Assert.AreEqual(0.0, Hold, "the hold is spent");
        Assert.AreEqual(0.0, D<double>("holdStart"), "no countdown any more");
        Assert.AreEqual(1, D<ushort>("wonSerial"), "every machine's HUD hears about it");
        Assert.AreEqual(Call("Districts", "IndexOf", Acres), (int)D<sbyte>("lastWon"));
        Assert.AreEqual(1, Attempts, "one unbroken attempt");
        Assert.AreEqual("minivan_meadows", (string)R("CampaignContract").GetField("districtId").GetValue(Prop<object>("Campaign", "Active")));
    }

    [Test]
    public void OldTrashBlocksTheHoldNoMatterHowCleanTheRestIs()
    {
        Seed();                                        // 56 old bags out
        Tick();
        for (int i = 0; i < 60; i++) Tick();           // 30 hours go by
        Assert.AreEqual(0.0, Hold);
        Assert.AreEqual(0, Permit("permit_minivan_meadows"));
        Assert.AreEqual(56, D<ushort>("backlog"));
        Assert.AreEqual(0, D<byte>("qualifying"));
    }

    [Test]
    public void OneDipStartsTheHoldOverAndTheSecondAttemptStillWins()
    {
        Seed();
        Clear();
        Tick();
        for (int i = 0; i < 20; i++) Tick();           // 10 hours held
        Assert.AreEqual(10.0, Hold, 1e-3);

        Call("Campaign", "DebugReseed", Acres);        // the mob dumps the heaps again
        Tick();
        Assert.AreEqual(0.0, Hold, "the hold is gone");
        Assert.AreEqual(0, D<byte>("qualifying"));
        Assert.AreEqual(0.0, D<double>("holdStart"), "the countdown is wiped");
        Assert.AreEqual(1, D<ushort>("lostSerial"), "every HUD flashes it");
        Assert.AreEqual(100, D<ushort>("lostTenths"), "ten game hours were lost");

        Clear();
        Tick();
        for (int i = 0; i < 60 && Permit("permit_minivan_meadows") == 0; i++) Tick();   // stop at the win: the next tick follows the Meadows
        Assert.AreEqual(1, Permit("permit_minivan_meadows"));
        Assert.AreEqual(2, Attempts, "a broken attempt and the one that won");
    }

    [Test]
    public void TheSavedCountTracksWhatIsLeftOnTheCurbs()
    {
        Seed();
        Assert.AreEqual(56, (int)Call("Campaign", "BacklogLeft", Acres));
        // pick up twenty of them the way a player would: the record goes away
        var bags = (System.Collections.IList)R("GameSession").GetField("CurbBags", Static).GetValue(null);
        int removed = 0;
        for (int i = bags.Count - 1; i >= 0 && removed < 20; i--)
        {
            string id = (string)bags[i].GetType().GetField("bagId").GetValue(bags[i]);
            if (!(bool)Call("CampaignRules", "IsBacklogBagId", id)) continue;
            Call("GameSession", "RemoveCurbBag", id);
            removed++;
        }
        Tick();
        Assert.AreEqual(36, (int)Call("Campaign", "BacklogLeft", Acres), "the next session will put out 36");
        Assert.AreEqual(36, D<ushort>("backlog"));
    }

    [Test]
    public void NothingHappensUntilTheHeapsHaveBeenSeededThisSession()
    {
        // The host session began (the curbs are empty) but the spawner has not put the heaps out yet: the saved count must survive.
        Call("GameSession", "SetCounter", "campaign_backlog_" + Acres, (int)Call("CampaignRules", "EncodeLeft", 30));
        Call("Campaign", "OnSessionBegin");
        Tick(); Tick(); Tick();
        Assert.AreEqual(30, (int)Call("Campaign", "BacklogLeft", Acres), "an empty curb before seeding is not an empty backlog");
    }
}

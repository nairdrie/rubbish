using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// The first campaign slice end to end without a scene: the heaps go out on the published houses, a saved remainder comes back,
/// a win grants the next permit and starts its heaps, and the district under contract is spared the fines. Everything runs on the
/// real <c>HouseDirectory</c> and the static game state (reset around each test).
/// </summary>
public class CampaignFlowTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string Acres = "mildly_pleasant", Meadows = "minivan_meadows", Commons = "coupon_commons";

    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string method, params object[] args) => R(type).GetMethod(method, Static).Invoke(null, args);
    static T Prop<T>(string type, string name) => (T)R(type).GetProperty(name, Static).GetValue(null);
    static object District(string id) => Call("Districts", "Get", id);

    [SetUp]
    public void SetUp()
    {
        Reset();
        Call("CurbTrashSpawner", "EnsureHouses");
        Call("Campaign", "OnSessionBegin");
    }

    [TearDown]
    public void TearDown() => Reset();

    static void Reset()
    {
        Call("GameSession", "ResetAll", false);
        Call("CurbTrashSpawner", "ResetStatics");
        Call("CityCleanliness", "ResetStatics");
        Call("Campaign", "ResetStatics");
        Call("CityCleanliness", "Invalidate");
    }

    static IList<object> OldBags(string districtId)
    {
        var all = (System.Collections.IEnumerable)R("GameSession").GetField("CurbBags", Static).GetValue(null);
        var list = new List<object>();
        foreach (var b in all)
        {
            string id = (string)b.GetType().GetField("bagId").GetValue(b);
            string stop = (string)b.GetType().GetField("stopId").GetValue(b);
            object d = Call("Districts", "ForStopId", stop);
            if ((bool)Call("CampaignRules", "IsBacklogBagId", id) && (string)d.GetType().GetField("id").GetValue(d) == districtId) list.Add(b);
        }
        return list;
    }

    static int HousesIn(string districtId)
    {
        int n = 0;
        foreach (var h in (System.Collections.IEnumerable)Prop<object>("CurbTrashSpawner", "Houses"))
            if ((string)h.GetType().GetField("districtId").GetValue(h) == districtId) n++;
        return n;
    }

    static int Counter(string key) => (int)Call("GameSession", "GetCounter", key);
    static void Seed() => Call("Campaign", "OnSpawnerSession");
    static string ActiveId()
    {
        var c = Prop<object>("Campaign", "Active");
        return c == null ? null : (string)c.GetType().GetField("districtId").GetValue(c);
    }

    // ------------------------------------------------------------------ the heaps

    [Test]
    public void ANewBusinessStartsInAMessyAcresWithTheAcresContractRunning()
    {
        Assert.AreEqual(Acres, ActiveId());
        Seed();
        var old = OldBags(Acres);
        Assert.AreEqual(56, old.Count, "the Acres' whole heap fits under its curb cap");
        Assert.AreEqual(0, OldBags(Meadows).Count, "the Meadows are locked: nothing there yet");
        Assert.AreEqual(57, Counter("campaign_backlog_" + Acres), "56 left, stored +1 so that 0 means never seeded");
    }

    [Test]
    public void TheHeapsAreStinkyAndSpreadOverAboutTwoAndAHalfBagsPerHouse()
    {
        Seed();
        var old = OldBags(Acres);
        double now = Prop<double>("GameClock", "TotalHours");
        float neglected = (float)District(Acres).GetType().GetField("neglectedHours").GetValue(District(Acres));
        var perStop = new Dictionary<string, List<int>>();
        var ids = new HashSet<string>();
        foreach (var b in old)
        {
            var t = b.GetType();
            double age = now - (double)t.GetField("putOutAtHours").GetValue(b);
            Assert.GreaterOrEqual(age, neglected + 2.0 - 1e-6, "every old bag is past the neglect window");
            Assert.LessOrEqual(age, neglected + 20.0 + 1e-6);
            string stop = (string)t.GetField("stopId").GetValue(b);
            if (!perStop.TryGetValue(stop, out var slots)) perStop[stop] = slots = new List<int>();
            slots.Add((int)t.GetField("slot").GetValue(b));
            Assert.IsTrue(ids.Add((string)t.GetField("bagId").GetValue(b)), "unique ids");
            Assert.IsNotNull(t.GetField("contents").GetValue(b), "rolled contents");
        }
        int expectedHeaps = (int)Call("CampaignRules", "HeapCount", 56, HousesIn(Acres));
        Assert.AreEqual(expectedHeaps, perStop.Count);
        foreach (var kv in perStop) Assert.AreEqual(kv.Value.Count, new HashSet<int>(kv.Value).Count, "no two bags share a slot at " + kv.Key);
    }

    [Test]
    public void EveryBagTakenAwayMovesTheBar()
    {
        Seed();
        Call("CityCleanliness", "Invalidate");
        float all = (float)Call("CityCleanliness", "District01", Acres);
        Assert.Less(all, 0.01f, "the whole heap reads 0%");

        var old = OldBags(Acres);
        for (int i = 0; i < old.Count / 2; i++) Call("GameSession", "RemoveCurbBag", (string)old[i].GetType().GetField("bagId").GetValue(old[i]));
        Call("CityCleanliness", "Invalidate");
        float half = (float)Call("CityCleanliness", "District01", Acres);
        Assert.AreEqual(0.5f, half, 0.03f, "half the heap gone reads about half clean");

        for (int i = old.Count / 2; i < old.Count; i++) Call("GameSession", "RemoveCurbBag", (string)old[i].GetType().GetField("bagId").GetValue(old[i]));
        Call("CityCleanliness", "Invalidate");
        Assert.AreEqual(1f, (float)Call("CityCleanliness", "District01", Acres), 1e-3f);
    }

    [Test]
    public void TheHeapsGoToTheSameHousesEverySession()
    {
        Seed();
        var first = new SortedSet<string>();
        foreach (var b in OldBags(Acres)) first.Add((string)b.GetType().GetField("stopId").GetValue(b));
        Call("CurbTrashSpawner", "DebugClearAll");
        Call("Campaign", "OnSessionBegin");
        Call("GameSession", "SetCounter", "campaign_backlog_" + Acres, 57);
        Seed();
        var second = new SortedSet<string>();
        foreach (var b in OldBags(Acres)) second.Add((string)b.GetType().GetField("stopId").GetValue(b));
        CollectionAssert.AreEqual(first, second);
    }

    [Test]
    public void AQuitMidCleanUpBringsOnlyTheRemainderBack()
    {
        Seed();
        Call("CurbTrashSpawner", "DebugClearAll");                         // a new session wipes the curbs
        Call("GameSession", "SetCounter", "campaign_backlog_" + Acres, (int)Call("CampaignRules", "EncodeLeft", 21));
        Call("Campaign", "OnSessionBegin");
        Seed();
        Assert.AreEqual(21, OldBags(Acres).Count);
    }

    [Test]
    public void AClearedDistrictStaysCleared()
    {
        Seed();
        Call("CurbTrashSpawner", "DebugClearAll");
        Call("GameSession", "SetCounter", "campaign_backlog_" + Acres, (int)Call("CampaignRules", "EncodeLeft", 0));
        Call("Campaign", "OnSessionBegin");
        Seed();
        Assert.AreEqual(0, OldBags(Acres).Count);
    }

    // ------------------------------------------------------------------ winning

    [Test]
    public void WinningTheAcresGrantsTheMeadowsAndStartsItsHeaps()
    {
        Seed();
        Assert.AreEqual(0, (int)Call("GameSession", "GetUpgradeLevel", "permit_minivan_meadows"));
        Assert.IsFalse((bool)Call("DistrictAccess", "OpenInTown", District(Meadows)));

        Assert.IsTrue((bool)Call("Campaign", "DebugWinActive"));

        Assert.AreEqual(1, (int)Call("GameSession", "GetUpgradeLevel", "permit_minivan_meadows"));
        Assert.IsTrue((bool)Call("DistrictAccess", "OpenInTown", District(Meadows)));
        Assert.AreEqual(Meadows, ActiveId(), "the next contract is running");
        Assert.AreEqual(48, OldBags(Meadows).Count);
        Assert.AreEqual(49, Counter("campaign_backlog_" + Meadows));
        Assert.AreEqual(1, Counter("campaign_won_" + Acres));
    }

    [Test]
    public void TheSecondTownIsHarderOnlyWhileItsContractRuns()
    {
        float Rate(string id) => (float)Call("Campaign", "RateMult", District(id));
        Seed();
        Assert.AreEqual(1f, Rate(Acres));
        Assert.AreEqual(1f, Rate(Meadows), "locked: nothing to speed up");
        Call("Campaign", "DebugWinActive");
        Assert.AreEqual(1f, Rate(Acres), "the Acres are won and back to normal");
        Assert.AreEqual(1.5f, Rate(Meadows));
        Call("Campaign", "DebugWinActive");
        Assert.AreEqual(1f, Rate(Meadows), "won: the Meadows settle down");
        Assert.IsNull(ActiveId(), "slice 1 ends at Commons");
    }

    [Test]
    public void ABusinessThatBoughtTheMeadowsPermitBeforeCountsAsHavingWonTheAcres()
    {
        Call("GameSession", "SetUpgradeLevel", "permit_minivan_meadows", 1);
        Assert.AreEqual(Meadows, ActiveId());
        Assert.IsTrue((bool)Call("Campaign", "IsWon", Call("Campaign", "ContractFor", Acres)));
        Seed();
        Assert.AreEqual(0, OldBags(Acres).Count, "no heaps for a district that is already behind you");
        Assert.AreEqual(48, OldBags(Meadows).Count);
    }

    [Test]
    public void AGuestsGateOpensForTheWholeTownWhenTheHostWon()
    {
        // The business in this process stands in for the host; the rule under test is the access check itself.
        Seed();
        Assert.IsFalse((bool)Call("DistrictAccess", "LocalCanEnter", District(Meadows)));
        Call("Campaign", "DebugWinActive");
        Assert.IsTrue((bool)Call("DistrictAccess", "LocalCanEnter", District(Meadows)));
        Assert.IsFalse((bool)Call("DistrictAccess", "LocalCanEnter", District(Commons)), "Commons waits for the Meadows contract");
    }

    // ------------------------------------------------------------------ what a dirty district costs

    [Test]
    public void TheDistrictUnderContractDoesNotDragTheCleanCityFactorDown()
    {
        Seed();
        Call("CityCleanliness", "Invalidate");
        Assert.Less((float)Call("CityCleanliness", "District01", Acres), 0.01f, "56 neglected bags read 0%");
        Assert.AreEqual(1f, Prop<float>("CityCleanliness", "Worst01"), 1e-4f);
        Assert.AreEqual(1f, Prop<float>("CityCleanliness", "CleanCityFactor"), 1e-4f);

        Call("Campaign", "DebugWinActive");                                // the Acres are now an ordinary district again
        Call("CityCleanliness", "Invalidate");
        Assert.Less(Prop<float>("CityCleanliness", "Worst01"), 0.01f, "...and its heaps count against everybody");
        Assert.AreEqual(0.4f, Prop<float>("CityCleanliness", "CleanCityFactor"), 1e-4f);
    }

    [Test]
    public void TheMidnightInspectionFinesNoNeglectedCurbsInTheDistrictUnderContract()
    {
        Seed();
        Call("CityCleanliness", "Invalidate");
        var under = Call("CityCleanliness", "MidnightInspection");
        var neglected = (Dictionary<string, int>)under.GetType().GetField("neglectedCurbs").GetValue(under);
        Assert.AreEqual(0, neglected[Acres]);

        Call("Campaign", "DebugWinActive");
        Call("CityCleanliness", "Invalidate");
        var after = Call("CityCleanliness", "MidnightInspection");
        neglected = (Dictionary<string, int>)after.GetType().GetField("neglectedCurbs").GetValue(after);
        Assert.Greater(neglected[Acres], 15, "once won, the Acres' standing heaps are neglected curbs like anywhere");
    }

    [Test]
    public void TheMidnightReviewLeavesTrustAloneWhileTheDistrictIsUnderContract()
    {
        Seed();
        float before = (float)Call("GameSession", "GetDistrictTrust", Acres, 0.6f);
        var report = Activator.CreateInstance(R("MidnightReport"));
        ((Dictionary<string, float>)R("MidnightReport").GetField("clean01").GetValue(report))[Acres] = 0.05f;
        R("Progression").GetField("lastMidnightDay", Static).SetValue(null, -1);
        Call("Progression", "OnMidnight", report);
        float after = (float)Call("GameSession", "GetDistrictTrust", Acres, 0.6f);
        Assert.AreEqual(before, after, 1e-4f, "filthy on purpose: its customers stay");
    }
}

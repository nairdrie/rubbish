using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>Real save routing, but only under a generated temporary SaveRoot. No account/real-save migration is performed.</summary>
public class BusinessSelectionTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type R(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Call(string type, string name, params object[] args) => R(type).GetMethod(name, Static).Invoke(null, args);
    static object Get(string type, string name) => R(type).GetProperty(name, Static).GetValue(null);
    static void Field(string type, string name, object value) => R(type).GetField(name, Static).SetValue(null, value);
    string temp, beforeGame, beforeEbae;
    IDisposable scope;
    readonly Dictionary<FieldInfo, object> beforeFields = new Dictionary<FieldInfo, object>();

    static string Game(string id, int day = 5, long cash = 12345) =>
        "{\"saveVersion\":3,\"gen\":1,\"businessId\":\"" + id + "\",\"businessName\":\"" + id +
        "\",\"day\":" + day + ",\"cashCents\":" + cash + ",\"truckCapacityKg\":240,\"clockHours\":9," +
        "\"upgradeIds\":[\"grabber_pole\"],\"upgradeLevels\":[1],\"counterIds\":[\"test-kept\"],\"counterValues\":[7]}";
    const string Ebae = "{\"gen\":1,\"totalSales\":9,\"totalEarned\":81,\"listings\":[],\"history\":[]}";

    void Remember(string type)
    {
        foreach (var f in R(type).GetFields(Static)) if (!f.IsLiteral && !f.IsInitOnly) beforeFields[f] = f.GetValue(null);
    }

    [SetUp]
    public void SetUp()
    {
        temp = Path.Combine(Path.GetTempPath(), "rubbish-business-selection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        scope = SaveRoot.PushTestOverride(temp);
        Remember("LegacySave"); Remember("BusinessSlots"); Remember("SaveGuard"); Remember("GameSession"); Remember("EbaeMarket");
        beforeGame = (string)R("GameSession").GetMethod("BuildSaveJson", Static, null, new[] { typeof(long) }, null).Invoke(null, new object[] { 0L });
        beforeEbae = (string)Call("EbaeMarket", "BuildSaveJson", 0L);
        Call("SaveGuard", "ResetForPlaySession");
        Call("BusinessSlots", "ResetStatics");
        Call("BusinessSlots", "ResetSelectionForPlaySession");
        File.WriteAllText(Path.Combine(temp, "rubbish_save.json"), Game("main-business"));
        File.WriteAllText(Path.Combine(temp, "ebae_save.json"), Ebae);
        Reload();
    }

    void Reload()
    {
        Call("LegacySave", "PrepareForBusinessLoad");
        Call("GameSession", "ResetAll", false);
        Call("GameSession", "Load"); Call("EbaeMarket", "Load");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            // Block save callbacks while restoring process state. Keep the test override until restoration is complete.
            R("SaveGuard").GetProperty("Foreign").SetValue(null, true);
            Call("GameSession", "ApplySaveJson", beforeGame);
            Call("EbaeMarket", "LoadBusinessJson", beforeEbae);
            foreach (var pair in beforeFields) pair.Key.SetValue(null, pair.Value);
            beforeFields.Clear();
        }
        finally
        {
            scope?.Dispose();
            if (temp != null && Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    string SlotPath(string id) => Path.Combine((string)Get("BusinessSlots", "FolderPath"), id + ".json");
    void Slot(string id, int day = 12, long cash = 67890)
    {
        Assert.IsTrue((bool)Call("BusinessSlots", "WriteSlotPair", id, Game(id, day, cash), Ebae));
    }
    object Find(string id) => ((IList)Call("BusinessSlots", "List")).Cast<object>().Single(s => (string)s.GetType().GetField("id").GetValue(s) == id);
    bool Select(object slot, out string error)
    {
        object[] args = { slot, null };
        bool ok = (bool)Call("BusinessSlots", "SelectForPlay", args);
        error = (string)args[1];
        return ok;
    }
    void Select(string id) { Assert.IsTrue(Select(Find(id), out var error), error); }
    bool Restore(out string error)
    {
        object[] args = { null };
        bool ok = (bool)Call("BusinessSlots", "RestoreSelection", args);
        error = (string)args[0]; return ok;
    }

    [Test]
    public void ExistingBusinessKeepsFullSizeTruckAndEveryUpgrade()
    {
        Call("GameSession", "ApplySaveJson", "{\"saveVersion\":3,\"cashCents\":12345,\"truckCapacityKg\":240,\"upgradeIds\":[\"truck_capacity\",\"truck_turbo\",\"truck_handling\"],\"upgradeLevels\":[2,1,2]}");
        Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "truck_model"));
        Assert.AreEqual(2, Call("GameSession", "GetUpgradeLevel", "truck_capacity"));
        Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "truck_turbo"));
        Assert.AreEqual(2, Call("GameSession", "GetUpgradeLevel", "truck_handling"));
        Assert.AreEqual(280f, Get("GameSession", "TruckCapacityKg"));
        Assert.AreEqual(12345L, Get("GameSession", "CashCents"));
        Assert.AreEqual(8, Call("GameSession", "GetCounter", "starter_collection_progress"), "existing businesses must not collect a new-business grant");
    }

    [Test]
    public void LegacyUpperDistrictSaveKeepsRouteAccessWithoutChangingMoneyOrWorkOrders()
    {
        Call("GameSession", "ApplySaveJson", "{\"saveVersion\":4,\"cashCents\":24680,\"businessId\":\"old-bluffs-owner\",\"upgradeIds\":[\"permit_billionaire_bluffs\",\"truck_capacity\"],\"upgradeLevels\":[1,2],\"counterIds\":[\"wo_0_district\",\"wo_1_district\"],\"counterValues\":[3,4]}");
        foreach (string permit in new[] { "minivan_meadows", "coupon_commons", "snootington_hills", "stackington_heights", "billionaire_bluffs" })
            Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "permit_" + permit));
        Assert.AreEqual(24680L, Get("GameSession", "CashCents"));
        Assert.AreEqual(2, Call("GameSession", "GetUpgradeLevel", "truck_capacity"));
        Assert.AreEqual(0, Call("GameSession", "GetUpgradeLevel", "truck_model"), "v4 vehicles are not migrated again");
        Assert.AreEqual(0, Call("GameSession", "GetCounter", "starter_collection_progress"));
        Assert.AreEqual(3, Call("GameSession", "GetCounter", "wo_0_district"));
        Assert.AreEqual(4, Call("GameSession", "GetCounter", "wo_1_district"));
        Assert.AreEqual("snootington_hills", Call("WorkOrders", "DistrictAt", 3));
        Assert.AreEqual("billionaire_bluffs", Call("WorkOrders", "DistrictAt", 4));
        string migrated = (string)R("GameSession").GetMethod("BuildSaveJson", Static, null, new[] { typeof(long) }, null).Invoke(null, new object[] { 1L });
        StringAssert.Contains("\"saveVersion\": 6", migrated);
        Call("GameSession", "ApplySaveJson", migrated);
        Assert.AreEqual(24680L, Get("GameSession", "CashCents"));
        Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "permit_stackington_heights"));
    }

    // ------------------------------------------------------------------ save v6: Kevin's Crew removed, its owners refunded

    static string KevinGame(string id, int level, long cash = 12345, int version = 5) =>
        "{\"saveVersion\":" + version + ",\"gen\":1,\"businessId\":\"" + id + "\",\"businessName\":\"" + id + "\",\"day\":9,\"cashCents\":" + cash +
        ",\"truckCapacityKg\":240,\"clockHours\":9,\"upgradeIds\":[\"truck_model\",\"hire_kevin\",\"truck_capacity\"],\"upgradeLevels\":[1," + level + ",2]," +
        "\"counterIds\":[\"st0_kevin\",\"st1_kevin\",\"st0_bags\"],\"counterValues\":[7,11,3]," +
        "\"depotDeliveries\":[{\"bagId\":\"kevin-left-this\",\"foundAt\":\"1 Maple St\",\"items\":[]}]}";
    static string BuildGame() => (string)R("GameSession").GetMethod("BuildSaveJson", Static, null, new[] { typeof(long) }, null).Invoke(null, new object[] { 1L });
    static int Level(string id) => (int)Call("GameSession", "GetUpgradeLevel", id);
    static int Counter(string key) => (int)Call("GameSession", "GetCounter", key);
    static long CashCents => (long)Get("GameSession", "CashCents");

    [TestCase(1, 60000L)]
    [TestCase(2, 760000L)]
    [TestCase(3, 3060000L)]
    public void ABusinessThatOwnedKevinGetsThePurchasePriceBackExactlyOnceWhenItLoads(int level, long refund)
    {
        Call("GameSession", "ApplySaveJson", KevinGame("kevin-owner", level));
        Assert.AreEqual(12345L + refund, CashCents, "$600, then $7,000, then $23,000: everything paid for the levels owned");
        Assert.AreEqual(0, Level("hire_kevin"), "the upgrade is gone"); Assert.AreEqual(1, Level("truck_model")); Assert.AreEqual(2, Level("truck_capacity"));
        Assert.AreEqual((int)refund, Counter("kevin_severance_cents"), "noted, so the owner is told once");
        Assert.AreEqual(0f, (float)Get("GameSession", "TotalCashEarned"), "a refund is not income");
        Assert.AreEqual(7, Counter("st0_crew")); Assert.AreEqual(11, Counter("st1_crew")); Assert.AreEqual(0, Counter("st0_kevin")); Assert.AreEqual(3, Counter("st0_bags"));
        var deliveries = (IList)R("GameSession").GetField("DepotDeliveries", Static).GetValue(null);
        Assert.AreEqual(1, deliveries.Count, "bags he had already collected still wait for the depot chute");

        string migrated = BuildGame();
        StringAssert.Contains("\"saveVersion\": 6", migrated); StringAssert.DoesNotContain("hire_kevin", migrated); StringAssert.DoesNotContain("st0_kevin", migrated);
        StringAssert.Contains("kevin-left-this", migrated);
        for (int i = 0; i < 2; i++)
        {
            Call("GameSession", "ApplySaveJson", migrated);
            Assert.AreEqual(12345L + refund, CashCents, "loading the migrated save grants nothing more");
            Assert.AreEqual((int)refund, Counter("kevin_severance_cents")); Assert.AreEqual(7, Counter("st0_crew"));
            Assert.AreEqual(1, ((IList)R("GameSession").GetField("DepotDeliveries", Static).GetValue(null)).Count);
        }
        // the old file read again (a crash before the first save) gives the same balance, never a refund on top of a refund
        Call("GameSession", "ApplySaveJson", KevinGame("kevin-owner", level));
        Assert.AreEqual(12345L + refund, CashCents);
    }

    [Test]
    public void NoRefundWithoutKevinForANewBusinessOrForASaveThatWasAlreadyMigrated()
    {
        Call("GameSession", "ApplySaveJson", Game("never-hired"));
        Assert.AreEqual(12345L, CashCents); Assert.AreEqual(0, Counter("kevin_severance_cents")); Assert.AreEqual(7, Counter("test-kept"));
        // a brand-new business starts with nothing and never passes through the migration; neither does its first save
        Call("GameSession", "BeginNewBusiness", "brand-new-co", "Brand New Co");
        Assert.AreEqual(0L, CashCents); Assert.AreEqual(0, Counter("kevin_severance_cents"));
        Call("GameSession", "ApplySaveJson", BuildGame());
        Assert.AreEqual(0L, CashCents); Assert.AreEqual(0, Counter("kevin_severance_cents"));
        // the version gate: a v6 save is final, whatever it carries
        Call("GameSession", "ApplySaveJson", KevinGame("already-v6", 3, 500, 6));
        Assert.AreEqual(500L, CashCents); Assert.AreEqual(0, Counter("kevin_severance_cents")); Assert.AreEqual(7, Counter("st0_kevin"));
        // an old save that lists him at level 0 (or nonsense) is cleaned up and granted nothing
        foreach (int level in new[] { 0, -2 })
        {
            Call("GameSession", "ApplySaveJson", KevinGame("level-" + (level + 5), level));
            Assert.AreEqual(12345L, CashCents); Assert.AreEqual(0, Level("hire_kevin")); Assert.AreEqual(0, Counter("kevin_severance_cents"));
        }
        // a level past the top is paid as the top, never more
        Call("GameSession", "ApplySaveJson", KevinGame("level-99", 99));
        Assert.AreEqual(12345L + 3060000L, CashCents);
    }

    [Test]
    public void ExtraBusinessSlotsAndAGuestsOwnBusinessAreRefundedByTheSameLoadAndSaveBackMigrated()
    {
        // an extra slot picked for solo or hosting
        Assert.IsTrue((bool)Call("BusinessSlots", "WriteSlotPair", "kevin-slot", KevinGame("kevin-slot", 2, 67890), Ebae));
        Select("kevin-slot");
        Assert.AreEqual(67890L + 760000L, CashCents); Assert.AreEqual(0, Level("hire_kevin")); Assert.AreEqual(9, Get("GameSession", "Day"));
        Call("GameSession", "SetBusinessName", "Kevin slot continued");
        Assert.IsTrue((bool)Call("LegacySave", "FlushForBusinessSwitch"));
        Reload();
        Assert.AreEqual("kevin-slot", Get("GameSession", "BusinessId")); Assert.AreEqual("Kevin slot continued", Get("GameSession", "BusinessName"));
        Assert.AreEqual(67890L + 760000L, CashCents, "written back as v6: read again, not refunded again"); Assert.AreEqual(0, Level("hire_kevin"));
        // the main business never hired him: it is not paid for somebody else's Kevin
        Select("main-business");
        Assert.AreEqual(12345L, CashCents); Assert.AreEqual(0, Counter("kevin_severance_cents"));

        // a guest brings its own business into a friend's Town (the load BusinessSlots.Choose does), then leaves
        Assert.IsTrue((bool)Call("BusinessSlots", "WriteSlotPair", "guest-kevin", KevinGame("guest-kevin", 1, 22200), Ebae));
        Call("SaveGuard", "MarkForeign", "test join");
        Field("BusinessSlots", "active", Find("guest-kevin")); Field("BusinessSlots", "declared", true); Field("BusinessSlots", "preJoinDay", 9);
        Call("GameSession", "LoadBusinessJson", KevinGame("guest-kevin", 1, 22200));
        Assert.AreEqual(22200L + 60000L, CashCents, "the balance the guest declares to the host already holds the refund");
        Assert.AreEqual(60000, Counter("kevin_severance_cents"));
        Call("SaveGuard", "ReloadOwn", "test leave");
        Assert.AreEqual("main-business", Get("GameSession", "BusinessId")); Assert.AreEqual(12345L, CashCents, "nothing of the guest's refund leaks into another business");
        Select("guest-kevin");
        Assert.AreEqual(22200L + 60000L, CashCents, "handed back as v6: once is once"); Assert.AreEqual(0, Level("hire_kevin"));
    }

    [Test]
    public void TheDaysStatsCountCrewBagsApartFromTheBagsThatWentIntoATruck()
    {
        Call("GameSession", "ApplySaveJson", KevinGame("crew-stats", 0)); // st0_kevin 7 becomes today's crew count, st0_bags is 3
        Call("DailyStats", "Reload");
        try
        {
            var fact = R("BagCollectedFact");
            object crewBag = Activator.CreateInstance(fact); fact.GetField("byRaccoon").SetValue(crewBag, true); fact.GetField("onTime").SetValue(crewBag, true);
            object handBag = Activator.CreateInstance(fact); fact.GetField("onTime").SetValue(handBag, true);
            Call("DailyStats", "OnBag", crewBag); Call("DailyStats", "OnBag", crewBag); Call("DailyStats", "OnBag", handBag);
            object today = Get("DailyStats", "Today");
            int Stat(string name) => (int)today.GetType().GetField(name).GetValue(today);
            Assert.AreEqual(9, Stat("crewBags")); Assert.AreEqual(4, Stat("bags"), "a crew bag is not a truck bag"); Assert.AreEqual(1, Stat("onTime"), "and never feeds your on-time %");
            Assert.AreEqual(9, Counter("st0_crew")); Assert.AreEqual(4, Counter("st0_bags")); Assert.AreEqual(0, Counter("st0_kevin"));
            Call("DailyStats", "Reload");
            today = Get("DailyStats", "Today");
            Assert.AreEqual(9, Stat("crewBags"), "read back from the business's counters");
        }
        finally { Call("DailyStats", "Reload"); }
    }

    [Test]
    public void TheOwnerIsToldOnceAndOnlyWhenTheStaticsAreTheirOwnBusiness()
    {
        Call("GameSession", "ApplySaveJson", KevinGame("told-once", 3));
        var inbox = (IList)R("HeadOffice").GetField("inbox", Static).GetValue(null);
        int before = inbox.Count;
        var foreign = R("SaveGuard").GetProperty("Foreign");
        try
        {
            // a guest who has not brought a business into the Town yet: wait
            foreign.SetValue(null, true); Field("BusinessSlots", "declared", false);
            Assert.IsFalse((bool)Call("KevinSeverance", "TryTell")); Assert.AreEqual(3060000, Counter("kevin_severance_cents")); Assert.AreEqual(before, inbox.Count);
            // ... and once it has (or on a host, or solo): the letter, once
            Field("BusinessSlots", "declared", true);
            Assert.IsTrue((bool)Call("KevinSeverance", "TryTell"));
            Assert.AreEqual(0, Counter("kevin_severance_cents")); Assert.AreEqual(3060000L + 12345L, CashCents, "telling moves no money");
            var letter = inbox[0];
            Assert.AreEqual("Kevin has moved on", (string)letter.GetType().GetField("subject").GetValue(letter));
            string body = (string)letter.GetType().GetField("body").GetValue(letter);
            StringAssert.Contains("Here's your deposit back: $30,600", body); StringAssert.Contains("Critter Hutch", body);
            int after = inbox.Count;
            Assert.IsFalse((bool)Call("KevinSeverance", "TryTell")); Assert.AreEqual(after, inbox.Count);
            foreign.SetValue(null, false); Field("BusinessSlots", "declared", false);
            Assert.IsFalse((bool)Call("KevinSeverance", "TryTell"), "nothing left to tell");
        }
        finally
        {
            foreign.SetValue(null, false);
            if (inbox.Count > before) inbox.RemoveAt(0);
        }
    }

    [Test]
    public void NewBusinessMiniAndPurchasedRearLoaderSurviveSaveReload()
    {
        Call("GameSession", "ResetAll", false);
        string Build() => (string)R("GameSession").GetMethod("BuildSaveJson", Static, null, new[] { typeof(long) }, null).Invoke(null, new object[] { 1L });
        Call("GameSession", "ApplySaveJson", Build());
        Assert.AreEqual(0, Call("GameSession", "GetUpgradeLevel", "truck_model"));
        Assert.AreEqual(120f, Get("GameSession", "TruckCapacityKg"));
        Call("GameSession", "SetUpgradeLevel", "truck_capacity", 1);
        Call("GameSession", "SetUpgradeLevel", "truck_model", 1);
        Call("ShopEffects", "ApplyTruckCapacity");
        string upgraded = Build();
        Call("GameSession", "ResetAll", false);
        Call("GameSession", "ApplySaveJson", upgraded);
        Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "truck_model"));
        Assert.AreEqual(1, Call("GameSession", "GetUpgradeLevel", "truck_capacity"));
        Assert.AreEqual(240f, Get("GameSession", "TruckCapacityKg"));
    }

    [Test]
    public void GuestCreatedBlobCanBeSelectedSavedAndContinuedForSoloOrHosting()
    {
        Slot("guest-created", 12, 67890);
        Select("guest-created");
        Assert.AreEqual("guest-created", Get("GameSession", "BusinessId"));
        Assert.AreEqual(12, Get("GameSession", "Day"));
        Assert.AreEqual(67890L, Get("GameSession", "CashCents"));
        Assert.AreEqual(9, Get("EbaeMarket", "TotalSales"));
        Assert.IsTrue((bool)Get("BusinessSlots", "CanPlaySelected"));
        byte[] original = File.ReadAllBytes(Path.Combine(temp, "rubbish_save.json"));
        Call("GameSession", "SetBusinessName", "Continued guest business");
        Assert.IsTrue((bool)Call("LegacySave", "FlushForBusinessSwitch"));
        Reload();
        Assert.AreEqual("Continued guest business", Get("GameSession", "BusinessName"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(temp, "rubbish_save.json")), "non-main solo saves never replace the original pair");
    }

    [Test]
    public void SelectionSurvivesRestartAndOriginalBusinessRemainsSelectable()
    {
        Slot("guest-created"); Select("guest-created");
        Call("BusinessSlots", "ResetSelectionForPlaySession"); Reload();
        Assert.AreEqual("main-business", Get("GameSession", "BusinessId"), "early subsystem load remains identity-independent");
        Assert.IsTrue(Restore(out string error), error);
        Assert.AreEqual("guest-created", Get("GameSession", "BusinessId"));
        Select("main-business");
        Assert.IsNull(Get("BusinessSlots", "SelectedId"));
        Assert.AreEqual(5, Get("GameSession", "Day"));
        Assert.AreEqual(12345L, Get("GameSession", "CashCents"));
        Assert.IsNotNull(Find("guest-created"));
    }

    [Test]
    public void CreatingNewBusinessDoesNotResetOrOverwriteExistingOne()
    {
        object[] args = { "New company", null };
        Assert.IsTrue((bool)Call("BusinessSlots", "CreateForPlay", args), args[1] as string);
        string created = (string)Get("GameSession", "BusinessId");
        Assert.AreNotEqual("main-business", created);
        Assert.AreEqual(1, Get("GameSession", "Day"));
        Assert.AreEqual(0L, Get("GameSession", "CashCents"));
        Assert.IsTrue(File.Exists(SlotPath(created)));
        Select("main-business");
        Assert.AreEqual(12345L, Get("GameSession", "CashCents"));
        Assert.AreEqual(9, Get("EbaeMarket", "TotalSales"));
    }

    [Test]
    public void MoreThanFiveSavedBusinessesAreDiscoverableInBothModes()
    {
        for (int i = 0; i < 8; i++) Slot("business-" + i);
        Assert.AreEqual(9, ((IList)Call("BusinessSlots", "List")).Count);
        Select("business-7");
        Call("SaveGuard", "MarkForeign", "test join");
        Assert.AreEqual(9, ((IList)Call("BusinessSlots", "List")).Count);
        Call("SaveGuard", "ReloadOwn", "test failed join");
        Assert.AreEqual("business-7", Get("GameSession", "BusinessId"));
    }

    [Test]
    public void LeavingGuestSessionHandsBackOnlyBroughtSlotAndRestoresPreviousSelection()
    {
        Slot("solo-selection", 12, 11100); Slot("brought-to-friend", 7, 22200);
        Select("solo-selection");
        byte[] beforeSelected = File.ReadAllBytes(SlotPath("solo-selection"));
        Call("SaveGuard", "MarkForeign", "test join");
        Field("BusinessSlots", "active", Find("brought-to-friend")); Field("BusinessSlots", "declared", true);
        Field("BusinessSlots", "preJoinDay", 7);
        Call("GameSession", "LoadBusinessJson", Game("brought-to-friend", 99, 77700));
        Call("SaveGuard", "ReloadOwn", "test leave");
        Assert.AreEqual("solo-selection", Get("GameSession", "BusinessId"));
        Assert.AreEqual(11100L, Get("GameSession", "CashCents"));
        CollectionAssert.AreEqual(beforeSelected, File.ReadAllBytes(SlotPath("solo-selection")));
        Select("brought-to-friend");
        Assert.AreEqual(77700L, Get("GameSession", "CashCents"));
        Assert.AreEqual(7, Get("GameSession", "Day"), "host's clock/day must not replace this business's own saved day");
    }

    [Test]
    public void OriginalBusinessGuestHandbackDoesNotWriteSelectedBlob()
    {
        Slot("solo-selection"); Select("solo-selection");
        byte[] selected = File.ReadAllBytes(SlotPath("solo-selection"));
        Call("SaveGuard", "MarkForeign", "test join");
        Field("BusinessSlots", "active", Find("main-business")); Field("BusinessSlots", "declared", true);
        Field("BusinessSlots", "preJoinDay", 5);
        Call("GameSession", "LoadBusinessJson", Game("main-business", 100, 99900));
        Call("SaveGuard", "ReloadOwn", "test leave");
        Assert.AreEqual("solo-selection", Get("GameSession", "BusinessId"));
        CollectionAssert.AreEqual(selected, File.ReadAllBytes(SlotPath("solo-selection")));
        Select("main-business");
        Assert.AreEqual(99900L, Get("GameSession", "CashCents"));
    }

    [Test]
    public void MismatchedSlotIdentityCannotBeListedSelectedOrWritten()
    {
        Slot("valid-slot");
        object slot = Find("valid-slot");
        string path = SlotPath("valid-slot");
        string before = File.ReadAllText(path);
        Assert.IsFalse((bool)Call("BusinessSlots", "WriteSlotPair", "valid-slot", Game("different-business"), Ebae));
        Assert.AreEqual(before, File.ReadAllText(path));
        slot.GetType().GetField("id").SetValue(slot, "missing-slot");
        Assert.IsFalse(Select(slot, out _));
        Assert.AreEqual("main-business", Get("GameSession", "BusinessId"));
    }

    [Test]
    public void CorruptSelectedMainBlobRecoversAtomicBackupWithoutTouchingOriginalPair()
    {
        Slot("recoverable"); Select("recoverable");
        byte[] original = File.ReadAllBytes(Path.Combine(temp, "rubbish_save.json"));
        File.WriteAllText(SlotPath("recoverable"), "broken json");
        Reload();
        Assert.IsTrue((bool)Get("LegacySave", "LoadedCleanly"));
        Assert.AreEqual("recoverable", Get("GameSession", "BusinessId"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(temp, "rubbish_save.json")));
    }

    [Test]
    public void SlotWithOnlyBackupsStillAppearsAndCanBeSelected()
    {
        Slot("backup-only");
        File.Delete(SlotPath("backup-only"));
        Assert.IsNotNull(Find("backup-only"));
        Select("backup-only");
        Assert.AreEqual("backup-only", Get("GameSession", "BusinessId"));
    }

    [Test]
    public void FailedPointerCommitRollsBackLoadedBusinessAndDoesNotOverwriteEitherSave()
    {
        Slot("safe-slot");
        byte[] slotBytes = File.ReadAllBytes(SlotPath("safe-slot"));
        string account = Path.GetDirectoryName((string)Get("BusinessSlots", "FolderPath"));
        Directory.CreateDirectory(Path.Combine(account, "selected-business.json")); // deliberate temp-root-only write failure
        Assert.IsFalse(Select(Find("safe-slot"), out _));
        Assert.IsNull(Get("BusinessSlots", "SelectedId"));
        Assert.AreEqual("main-business", Get("GameSession", "BusinessId"));
        Assert.AreEqual(12345L, Get("GameSession", "CashCents"));
        CollectionAssert.AreEqual(slotBytes, File.ReadAllBytes(SlotPath("safe-slot")));
    }

    [Test]
    public void MissingSelectedBusinessBlocksSavingInsteadOfCreatingDefaultsOverIt()
    {
        Slot("missing-slot"); Select("missing-slot");
        File.Delete(SlotPath("missing-slot")); Directory.Delete(SafeFile.GenDir(SlotPath("missing-slot")), true);
        LogAssert.Expect(LogType.Error, new Regex("\\[Save\\] Saving is OFF.*selected business"));
        Reload();
        Assert.IsFalse((bool)Get("LegacySave", "LoadedCleanly"));
        Assert.IsFalse((bool)Get("BusinessSlots", "CanPlaySelected"));
        Assert.IsFalse((bool)Call("LegacySave", "FlushForBusinessSwitch"));
        Assert.IsFalse(File.Exists(SlotPath("missing-slot")));
        Select("main-business");
        Assert.AreEqual(12345L, Get("GameSession", "CashCents"));
    }
}

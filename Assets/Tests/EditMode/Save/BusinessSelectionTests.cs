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

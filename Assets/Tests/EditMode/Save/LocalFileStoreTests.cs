using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Local-only behaviour of <see cref="LocalFileStore"/> and <see cref="LocalAccountBackend"/> (Docs/Multiplayer.md §8.7.4 B4)
/// that the shared <see cref="AccountBackendContract"/> can't express: the on-disk format, recovery details, key rules, lock-file
/// liveness and the folder layout. Runs in a throwaway data root.
/// </summary>
public class LocalFileStoreTests
{
    string dataRoot;
    DateTime now;
    DateTime Clock() => now;
    static readonly PlayerKey Nick = PlayerKey.Dev("editor");

    [SetUp]
    public void SetUp()
    {
        dataRoot = Path.Combine(Path.GetTempPath(), "rubbish-b4-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        now = new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(dataRoot, true); } catch (Exception) { /* temp */ }
    }

    LocalAccountBackend Backend(string session = "s1") => LocalAccountBackend.ForPlayer(dataRoot, Nick, Clock, session);
    static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
    static string Text(byte[] b) => b == null ? null : Encoding.UTF8.GetString(b);

    // ------------------------------------------------------------------ layout

    [Test]
    public void Accounts_LiveUnderDataRootAccountsPlayerFolder()
    {
        Assert.AreEqual(Path.Combine(dataRoot, "accounts", "dev_editor"), LocalAccountBackend.AccountFolder(dataRoot, Nick));
        Assert.AreEqual(Path.Combine(dataRoot, "accounts", "steam_76561197960287930"),
            LocalAccountBackend.AccountFolder(dataRoot, PlayerKey.Steam(76561197960287930UL)));
        Assert.AreEqual(Path.Combine(dataRoot, "accounts", "dev_bot-1"), LocalAccountBackend.AccountFolder(dataRoot, PlayerKey.Dev("bot-1")));
        Assert.Throws<ArgumentException>(() => LocalAccountBackend.AccountFolder(dataRoot, default));
    }

    [Test]
    public void Keys_MapToFilesInsideTheAccount()
    {
        var s = Backend().Files;
        string acct = s.Scope;
        Assert.IsTrue(s.TryPathFor(SaveKeys.Profile, out string p));
        Assert.AreEqual(Path.Combine(acct, "profile.json"), p);
        Assert.IsTrue(s.TryPathFor(SaveKeys.Company("abc-1"), out p));
        Assert.AreEqual(Path.Combine(acct, "companies", "abc-1.json"), p);
        Assert.IsTrue(s.TryPathFor(SaveKeys.CompanyPrev("abc-1"), out p));
        Assert.AreEqual(Path.Combine(acct, "companies", "abc-1.prev.json"), p);
        Assert.IsTrue(s.TryPathFor(SaveKeys.Town("t-1"), out p));
        Assert.AreEqual(Path.Combine(acct, "towns", "t-1.json"), p);
        var slash = new LocalFileStore(acct + Path.DirectorySeparatorChar);
        Assert.AreEqual(acct, slash.Scope, "a trailing separator is ignored");
        Assert.IsTrue(slash.TryPathFor(SaveKeys.Profile, out p));
        Assert.AreEqual(Path.Combine(acct, "profile.json"), p);
        Assert.IsFalse(LocalFileStore.IsValidKey(SaveKeys.Lock("abc-1"), out _), "the lock file isn't a record");
        Assert.IsFalse(LocalFileStore.IsValidKey("companies/ABC", out string why));
        StringAssert.Contains("lower-case", why);
    }

    [Test]
    public async Task ReadingNeverCreatesTheAccountFolder()
    {
        var b = Backend();
        Assert.IsFalse((await b.Store.ReadAsync(SaveKeys.Profile)).Found);
        Assert.AreEqual(0, (await b.Store.ListAsync("")).Count);
        Assert.IsFalse(await b.Store.DeleteAsync(SaveKeys.Profile));
        Assert.IsFalse(b.Inspect("co-1").Exists);
        Assert.IsFalse(Directory.Exists(b.Files.Scope));
    }

    // ------------------------------------------------------------------ format

    [Test]
    public void Record_JsonPayloadIsReadable_AndRoundTrips()
    {
        byte[] payload = Json("{\"cash\":1250,\"name\":\"Rubbish Co.\"}");
        byte[] file = StoreRecord.Encode(17, now, payload);
        string text = Encoding.UTF8.GetString(file);
        StringAssert.StartsWith("{\"rbstore\":1,\"version\":17,\"savedUtc\":\"2026-10-01T20:00:00.000Z\",\"bytes\":34,\"crc32\":\"", text);
        StringAssert.Contains("\"data\":{\"cash\":1250,\"name\":\"Rubbish Co.\"}}", text);
        Assert.IsTrue(StoreRecord.TryDecode(file, out var r));
        Assert.AreEqual(17, r.Version);
        Assert.AreEqual(now, r.SavedUtc);
        Assert.IsFalse(r.Deleted);
        CollectionAssert.AreEqual(payload, r.Payload);
    }

    [Test]
    public void Record_BinaryPayloadGoesBase64()
    {
        byte[] payload = { 0x1f, 0x8b, 0, 255, (byte)'{', (byte)'}' };
        byte[] file = StoreRecord.Encode(3, now, payload);
        StringAssert.Contains("\"data64\":\"", Encoding.ASCII.GetString(file));
        Assert.IsTrue(StoreRecord.TryDecode(file, out var r));
        CollectionAssert.AreEqual(payload, r.Payload);
        Assert.IsFalse(StoreRecord.LooksLikeJson(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }), "a BOM goes base64");
        Assert.IsFalse(StoreRecord.LooksLikeJson(new byte[] { (byte)'{', 0xC3, (byte)'}' }), "invalid UTF-8 goes base64");
    }

    [Test]
    public void Record_ATruncatedOrFlippedFileNeverVerifies()
    {
        foreach (byte[] payload in new[] { Json("{\"bag\":\"" + new string('x', 300) + "\"}"), new byte[] { 1, 2, 3, 4, 5, 6, 7 } })
        {
            byte[] file = StoreRecord.Encode(9, now, payload);
            for (int n = 0; n < file.Length - 1; n++) // "}\n" ends the file: dropping only the newline is still whole
            {
                var cut = new byte[n];
                Buffer.BlockCopy(file, 0, cut, 0, n);
                Assert.IsFalse(StoreRecord.TryDecode(cut, out _), $"truncated to {n} of {file.Length}");
            }
            for (int i = 0; i < file.Length; i++)
            {
                var flipped = (byte[])file.Clone();
                flipped[i] ^= 0x20;
                if (StoreRecord.TryDecode(flipped, out var r))
                    Assert.Fail($"byte {i} flipped and it still verified (version {r.Version})");
            }
            Assert.AreEqual(9, StoreRecord.PeekVersion(file.Take(60).ToArray()), "the header version is readable from a damaged copy");
        }
        var tomb = StoreRecord.EncodeTombstone(12, now);
        Assert.IsTrue(StoreRecord.TryDecode(tomb, out var t));
        Assert.IsTrue(t.Deleted);
        Assert.AreEqual(12, t.Version);
    }

    // ------------------------------------------------------------------ recovery

    [Test]
    public async Task ADamagedMainFile_IsQuarantinedOnce_AndTheNewestBackupIsUsed()
    {
        var s = Backend().Files;
        string k = SaveKeys.Company("co-1");
        for (int i = 1; i <= 3; i++)
        {
            now = now.AddSeconds(3);
            await s.WriteAsync(k, Json("{\"n\":" + i + "}"), -1);
        }
        Assert.IsTrue(s.TryPathFor(k, out string path));
        byte[] whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole.Take(whole.Length / 2).ToArray()); // e.g. a disk error, a sync tool, a bad copy

        var r = await s.ReadAsync(k);
        Assert.IsTrue(r.Found);
        Assert.AreEqual("{\"n\":3}", Text(r.Data), "latest.1 holds the same write");
        Assert.AreEqual("latest.1", r.Source);
        await s.ReadAsync(k);
        await s.ListAsync("");
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path), "co-1.corrupt-*.json").Length, "kept once, not on every read");

        var w = await s.WriteAsync(k, Json("{\"n\":4}"), 3);
        Assert.IsTrue(w.Ok);
        Assert.AreEqual(4, w.Version, "versions never go backwards past a damaged copy");
        Assert.AreEqual(SafeFile.Main, (await s.ReadAsync(k)).Source);
    }

    [Test]
    public void ADamagedMainFile_WithNoBackup_ReadsAsMissing_AndIsKept()
    {
        var s = Backend().Files; // synchronous calls: the expected error is logged on this thread
        string k = SaveKeys.Town("t-1");
        Assert.IsTrue(s.TryPathFor(k, out string path));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"rbstore\":1,\"version\":5,\"savedUtc\":\"2026-10-01T20:00:00.000Z\",\"bytes\":900,\"crc32\":\"00000000\",\"data\":{\"half");
        LogAssert.Expect(LogType.Error, new Regex("is damaged and no backup verifies"));
        Assert.IsFalse(s.ReadNow(k).Found);
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path), "t-1.corrupt-*.json").Length);
        LogAssert.Expect(LogType.Error, new Regex("is damaged and no backup verifies"));
        Assert.AreEqual(6, s.WriteNow(k, Json("{\"fresh\":true}"), -1).Version, "the next write outranks the damaged header");
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path), "t-1.corrupt-*.json").Length);
    }

    [Test]
    public async Task ATornTempFile_IsIgnored_AndAnOlderTempFileDoesntWin()
    {
        var s = Backend().Files;
        string k = SaveKeys.Profile;
        await s.WriteAsync(k, Json("{\"v\":1}"), -1);
        await s.WriteAsync(k, Json("{\"v\":2}"), -1);
        Assert.IsTrue(s.TryPathFor(k, out string path));

        byte[] next = StoreRecord.Encode(3, now, Json("{\"v\":3,\"pad\":\"" + new string('z', 500) + "\"}"));
        File.WriteAllBytes(path + ".tmp", next.Take(next.Length - 40).ToArray()); // killed mid-write
        var r = await s.ReadAsync(k);
        Assert.AreEqual("{\"v\":2}", Text(r.Data));
        Assert.AreEqual(SafeFile.Main, r.Source);

        File.WriteAllBytes(path + ".tmp", StoreRecord.Encode(1, now, Json("{\"v\":\"old\"}"))); // stale and complete
        Assert.AreEqual("{\"v\":2}", Text((await s.ReadAsync(k)).Data));

        Assert.AreEqual(3, (await s.WriteAsync(k, Json("{\"v\":3}"), 2)).Version);
        Assert.IsFalse(File.Exists(path + ".tmp"), "a normal write consumes the temp file");
    }

    [Test]
    public async Task AFinishedTempFile_IsRecommitted_SoASecondCrashCantLoseIt()
    {
        var s = Backend().Files;
        string k = SaveKeys.Company("co-2");
        await s.WriteAsync(k, Json("{\"cash\":1}"), -1);
        Assert.IsTrue(s.TryPathFor(k, out string path));
        File.WriteAllBytes(path + ".tmp", StoreRecord.Encode(2, now, Json("{\"cash\":2}")));
        Assert.AreEqual("tmp", (await s.ReadAsync(k)).Source);
        Assert.IsFalse(File.Exists(path + ".tmp"));
        var again = await s.ReadAsync(k);
        Assert.AreEqual(SafeFile.Main, again.Source, "now the main file");
        Assert.AreEqual(2, again.Version);
        Assert.AreEqual(2, s.ReadGeneration(k, SafeFile.Latest1).Version, "and in the backups");
    }

    [Test]
    public async Task Delete_KeepsBackups_PurgeRemovesEverything()
    {
        var s = Backend().Files;
        string k = SaveKeys.Town("t-2");
        await s.WriteAsync(k, Json("{\"t\":1}"), -1);
        await s.WriteAsync(k, Json("{\"t\":2}"), -1);
        Assert.IsTrue(await s.DeleteAsync(k));
        var info = s.Inspect(k);
        Assert.IsTrue(info.Deleted);
        Assert.AreEqual(3, info.Version);
        Assert.AreEqual(2, s.Generations(k).Max(g => g.version), "the last live version is still restorable");
        Assert.AreEqual("{\"t\":2}", Text(s.ReadGeneration(k, SafeFile.Latest2).Data));

        Assert.IsTrue(s.PurgeNow(k));
        Assert.IsFalse(s.Inspect(k).Any);
        Assert.AreEqual(0, s.Generations(k).Count);
        Assert.IsTrue(s.TryPathFor(k, out string path));
        Assert.IsFalse(Directory.Exists(SafeFile.GenDir(path)));
    }

    // ------------------------------------------------------------------ locks

    [Test]
    public void LockFile_RecordsEpochSessionProcessAndStart()
    {
        var b = Backend();
        var l = b.CheckoutNow("co-3", "net-7", "Maple Hollow");
        string text = File.ReadAllText(Path.Combine(b.Files.Scope, "companies", "co-3.lock"));
        StringAssert.StartsWith("rubbish-lease 1\n", text);
        StringAssert.Contains("epoch=" + l.Epoch.ToString(CultureInfo.InvariantCulture) + "\n", text);
        StringAssert.Contains("state=held\n", text);
        StringAssert.Contains("session=net-7\n", text);
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
        {
            StringAssert.Contains("pid=" + me.Id.ToString(CultureInfo.InvariantCulture) + "\n", text);
            StringAssert.Contains("start=" + me.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\n", text);
        }
        StringAssert.Contains("run=" + LocalAccountBackend.RunId + "\n", text);
        StringAssert.Contains("town=Maple Hollow\n", text);
        var info = b.Inspect("co-3");
        Assert.IsTrue(info.OnTheRoad);
        Assert.IsTrue(info.IsThisProcess);
        Assert.AreEqual("Maple Hollow", info.TownName);
        Assert.IsTrue(b.CheckinNow(l));
        Assert.IsFalse(b.Inspect("co-3").OnTheRoad);
        StringAssert.Contains("state=released", File.ReadAllText(Path.Combine(b.Files.Scope, "companies", "co-3.lock")));
    }

    [Test]
    public void ALockWhosePidWasReused_IsFree()
    {
        var b = Backend();
        var l = b.CheckoutNow("co-4", "net-1", "Maple Hollow");
        // Same PID as a live process (us), but that process started at another time: the holder is gone, the PID was reused.
        LocalStoreTestUtil.RewriteLock(b.Files.Scope, "co-4", "start", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
        LocalStoreTestUtil.RewriteLock(b.Files.Scope, "co-4", "run", "someone-else");
        var l2 = Backend("s2").CheckoutNow("co-4", "net-2", "Maple Hollow");
        Assert.IsTrue(l2.Granted, l2.HeldBy);
        Assert.Greater(l2.Epoch, l.Epoch);
    }

    [Test]
    public void ALockFromAnEarlierPlaySessionOfThisProcess_IsFree()
    {
        string oldRun = LocalAccountBackend.RunId;
        try
        {
            var l = Backend().CheckoutNow("co-5", "net-1", "Maple Hollow");
            Assert.IsTrue(l.Granted);
            LocalAccountBackend.NewRun(); // the Editor left play mode and entered it again
            var l2 = Backend("s2").CheckoutNow("co-5", "net-2", "Maple Hollow");
            Assert.IsTrue(l2.Granted, l2.HeldBy);
            Assert.Greater(l2.Epoch, l.Epoch);
        }
        finally { Assert.AreNotEqual(oldRun, LocalAccountBackend.RunId, "later tests simply run under the new run id"); }
    }

    [Test]
    public void AnUnreadableLockFile_IsFree_AndKeepsItsEpoch()
    {
        var b = Backend();
        var l = b.CheckoutNow("co-6", "net-1", "Maple Hollow");
        b.CheckinNow(l);
        b.CheckoutNow("co-6", "net-2", "Maple Hollow");
        string path = Path.Combine(b.Files.Scope, "companies", "co-6.lock");
        File.WriteAllText(path, "garbage\nepoch=41\n\0\0");
        var l3 = Backend("s3").CheckoutNow("co-6", "net-3", "Maple Hollow");
        Assert.IsTrue(l3.Granted);
        Assert.AreEqual(42, l3.Epoch);
    }

    [Test]
    public void CheckoutIntoAnotherTown_KeepsAPrevCopy_SameTownDoesnt()
    {
        var b = Backend();
        var l = b.CheckoutNow("co-7", "net-1", "Maple Hollow");
        Assert.IsTrue(b.CommitNow(l, Json("{\"cash\":1}"), -1).Ok);
        b.CheckinNow(l);
        l = b.CheckoutNow("co-7", "net-2", "Maple Hollow");
        Assert.IsFalse(b.Files.ReadNow(SaveKeys.CompanyPrev("co-7")).Found, "same Town: no copy");
        Assert.IsTrue(b.CommitNow(l, Json("{\"cash\":2}"), -1).Ok);
        b.CheckinNow(l);
        b.CheckoutNow("co-7", "net-3", "Pine Ridge");
        Assert.AreEqual("{\"cash\":2}", Text(b.Files.ReadNow(SaveKeys.CompanyPrev("co-7")).Data));
        var info = b.Inspect("co-7");
        Assert.AreEqual("Pine Ridge", info.PrevTown, "the copy is from before visiting Pine Ridge");
    }

    [Test]
    public void BadCompanyIds_AreRefused()
    {
        var b = Backend();
        foreach (string g in new[] { null, "", "../x", "ABC", "-lead", "a/b", new string('a', 65) })
        {
            var l = b.CheckoutNow(g, "net-1", "Maple Hollow");
            Assert.IsFalse(l.Granted, g);
            Assert.IsFalse(b.TakeBackNow(g, "net-1", null).Granted, g);
            Assert.IsFalse(b.RestorePreviousNow(g), g);
        }
        Assert.IsFalse(Directory.Exists(b.Files.Scope));
    }

    // ------------------------------------------------------------------ folder lock

    [Test]
    public void AccountFolderLock_IsReentrant_AndExcludesOtherThreads()
    {
        string dir = Path.Combine(dataRoot, "lockdir");
        var entered = new ManualResetEventSlim();
        using (AccountFolderLock.Enter(dir, TimeSpan.FromSeconds(1)))
        {
            using (AccountFolderLock.Enter(dir, TimeSpan.FromSeconds(1))) { } // re-entrant on this thread
            var other = Task.Run(() =>
            {
                try { using (AccountFolderLock.Enter(dir, TimeSpan.FromMilliseconds(200))) { entered.Set(); } return "entered"; }
                catch (TimeoutException) { return "timed out"; }
            });
            Assert.AreEqual("timed out", other.Result, "another thread waits while it's held");
        }
        var after = Task.Run(() => { using (AccountFolderLock.Enter(dir, TimeSpan.FromSeconds(1))) { return "entered"; } });
        Assert.AreEqual("entered", after.Result);
        Assert.IsTrue(File.Exists(Path.Combine(dir, AccountFolderLock.FileName)));
    }

    [Test]
    public async Task Cancelled_BeforeItStarts_IsCancelled()
    {
        var s = Backend().Files;
        var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await s.WriteAsync(SaveKeys.Profile, Json("{}"), -1, cts.Token);
            Assert.Fail("expected a cancellation");
        }
        catch (OperationCanceledException) { /* expected */ }
        Assert.IsFalse((await s.ReadAsync(SaveKeys.Profile)).Found);
    }
}

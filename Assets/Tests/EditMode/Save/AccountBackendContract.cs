using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

/// <summary>
/// The contract every <see cref="IAccountBackend"/> must pass (Docs/Multiplayer.md §8.7.4 B4; Docs/Accounts.md §5.3):
/// <see cref="LocalAccountBackend"/> now (<c>LocalAccountBackendContractTests</c>), and M1.5's <c>UgsAccountBackend</c> later.
/// Subclass it, implement the hooks, and every test here runs against your backend. Time comes from <see cref="Now"/> (the
/// backend must take <see cref="Clock"/>), so generation tiers are tested over a scripted hour and a local midnight.
/// </summary>
public abstract class AccountBackendContract
{
    // ------------------------------------------------------------------ hooks

    /// <summary>
    /// A new handle on <paramref name="who"/>'s account, as one game process sees it (two calls = two processes on one PC), using
    /// <see cref="Clock"/> and <paramref name="playSession"/> as its play session (lease session default, <c>session</c> tier).
    /// </summary>
    protected abstract IAccountBackend Open(PlayerKey who, string playSession);

    /// <summary>The process holding <paramref name="lease"/> died without checking in (killed or crashed).</summary>
    protected abstract void KillHolder(IAccountBackend holder, CompanyLease lease);

    /// <summary>A write of <paramref name="data"/> to <paramref name="key"/> got as far as its finished, durable temp (or local
    /// cache) copy, and the process died before the write was committed.</summary>
    protected abstract void CrashMidWrite(IAccountBackend b, string key, byte[] data);

    /// <summary>Tier names that currently hold a copy of <paramref name="key"/> (<c>latest.1</c>, <c>10min</c>, <c>daily-…</c>).</summary>
    protected abstract IReadOnlyList<string> GenerationTiers(IAccountBackend b, string key);

    /// <summary>The version held by one tier of <paramref name="key"/>, or -1.</summary>
    protected abstract long TierVersion(IAccountBackend b, string key, string tier);

    /// <summary>The local folder holding <paramref name="who"/>'s account (or its local cache).</summary>
    protected abstract string AccountFolder(PlayerKey who);

    // ------------------------------------------------------------------ fixture

    protected DateTime Now;
    protected DateTime Clock() => Now;

    protected static readonly PlayerKey Nick = PlayerKey.Dev("editor");
    protected static readonly PlayerKey Dot = PlayerKey.Dev("dot");
    protected const string Co = "3f2a9c0e-company-a";
    protected const string TownA = "Maple Hollow", TownB = "Pine Ridge";

    [SetUp]
    public void ContractSetUp() => Now = new DateTime(2026, 10, 1, 21, 40, 0, DateTimeKind.Utc);

    protected static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
    protected static string Text(byte[] b) => b == null ? null : Encoding.UTF8.GetString(b);
    static byte[] Blob(int n, byte fill = 7) => Enumerable.Repeat(fill, n).ToArray();

    protected IAccountBackend SignedIn(PlayerKey who, string session)
    {
        var b = Open(who, session);
        Assert.IsTrue(b.SignInAsync(new FakeIdentity(who)).GetAwaiter().GetResult(), "sign-in");
        return b;
    }

    protected sealed class FakeIdentity : IPlayerIdentity
    {
        public FakeIdentity(PlayerKey k) { Key = k; }
        public PlayerKey Key { get; }
        public string DisplayName => Key.ToString();
        public bool IsSteam => Key.IsSteam;
        public byte[] GetJoinTicket(ulong hostSteamId) => Array.Empty<byte>();
        public void CancelJoinTicket() { }
    }

    // ------------------------------------------------------------------ account

    [Test]
    public async Task SignIn_AcceptsTheOwnerOnly()
    {
        var b = Open(Nick, "s1");
        var seen = new List<AccountState>();
        b.StateChanged += seen.Add;
        Assert.IsFalse(await b.SignInAsync(new FakeIdentity(Dot)), "someone else's identity");
        Assert.AreEqual(AccountState.Failed, b.State);
        Assert.IsTrue(await b.SignInAsync(new FakeIdentity(Nick)));
        Assert.AreEqual(AccountState.SignedIn, b.State);
        Assert.Contains(AccountState.SignedIn, seen);
        Assert.IsFalse(string.IsNullOrEmpty(b.AccountId));
        Assert.IsNotNull(b.Store);
    }

    // ------------------------------------------------------------------ store

    [Test]
    public async Task Read_Missing_IsNotFound()
    {
        var s = SignedIn(Nick, "s1").Store;
        var r = await s.ReadAsync(SaveKeys.Profile);
        Assert.IsFalse(r.Found);
        Assert.AreEqual(0, r.Version);
        Assert.IsNull(r.Data);
    }

    [Test]
    public async Task Write_ThenRead_RoundTripsBytesVersionAndTime()
    {
        var s = SignedIn(Nick, "s1").Store;
        var w1 = await s.WriteAsync(SaveKeys.Profile, Json("{\"pocket\":1}"), -1);
        Assert.IsTrue(w1.Ok, w1.Error.ToString());
        Assert.AreEqual(1, w1.Version);
        Now = Now.AddSeconds(5);
        byte[] bin = { 0, 1, 2, 250, 255, 10, 13 }; // non-JSON bytes must round-trip too
        var w2 = await s.WriteAsync(SaveKeys.Profile, bin, 1);
        Assert.AreEqual(2, w2.Version);
        var r = await s.ReadAsync(SaveKeys.Profile);
        Assert.IsTrue(r.Found);
        CollectionAssert.AreEqual(bin, r.Data);
        Assert.AreEqual(2, r.Version);
        Assert.AreEqual(Now, r.SavedUtc);
    }

    [Test]
    public async Task VersionConflict_IsReportedWithTheCurrentVersion()
    {
        var s = SignedIn(Nick, "s1").Store;
        string k = SaveKeys.Town("town-1");
        Assert.AreEqual(1, (await s.WriteAsync(k, Json("{\"v\":1}"), 0)).Version, "0 = create only");
        var dup = await s.WriteAsync(k, Json("{\"v\":\"again\"}"), 0);
        Assert.AreEqual(SaveError.VersionConflict, dup.Error, "create-only on an existing record");
        Assert.AreEqual(1, dup.Version, "the conflict reports the current version");
        Assert.AreEqual(SaveError.VersionConflict, (await s.WriteAsync(k, Json("{}"), 7)).Error);
        Assert.AreEqual(2, (await s.WriteAsync(k, Json("{\"v\":2}"), 1)).Version);
        var stale = await s.WriteAsync(k, Json("{\"v\":\"stale\"}"), 1);
        Assert.IsFalse(stale.Ok);
        Assert.AreEqual(SaveError.VersionConflict, stale.Error);
        Assert.AreEqual(3, (await s.WriteAsync(k, Json("{\"v\":3}"), -1)).Version, "-1 = any");
        Assert.AreEqual("{\"v\":3}", Text((await s.ReadAsync(k)).Data), "refused writes changed nothing");
    }

    [Test]
    public async Task OverOneMiB_IsTooLarge_AndChangesNothing()
    {
        var s = SignedIn(Nick, "s1").Store;
        string k = SaveKeys.Company(Co);
        Assert.IsTrue((await s.WriteAsync(k, Blob(1024 * 1024), -1)).Ok, "exactly 1 MiB is allowed");
        var big = await s.WriteAsync(k, Blob(1024 * 1024 + 1, 9), -1);
        Assert.IsFalse(big.Ok);
        Assert.AreEqual(SaveError.TooLarge, big.Error);
        var r = await s.ReadAsync(k);
        Assert.AreEqual(1, r.Version);
        Assert.AreEqual(7, r.Data[0]);
        Assert.IsFalse(s.TryWriteNow(k, Blob(2 * 1024 * 1024)), "the quit path has the same cap");
    }

    [Test]
    public async Task AWriteThatCrashedAfterItsTempCopy_Survives()
    {
        var s = SignedIn(Nick, "s1").Store;
        string k = SaveKeys.Company(Co);
        await s.WriteAsync(k, Json("{\"cash\":100}"), -1);
        Now = Now.AddSeconds(2);
        var b2 = Open(Nick, "s1");
        CrashMidWrite(b2, k, Json("{\"cash\":250}"));

        var after = SignedIn(Nick, "s2"); // the next launch
        var r = await after.Store.ReadAsync(k);
        Assert.IsTrue(r.Found);
        Assert.AreEqual("{\"cash\":250}", Text(r.Data), "the finished write is recovered, not lost");
        Assert.AreEqual(2, r.Version);
        Assert.AreEqual(3, (await after.Store.WriteAsync(k, Json("{\"cash\":260}"), 2)).Version, "and saving carries on from it");
    }

    [Test]
    public async Task Generations_RotatePerTier()
    {
        string k = SaveKeys.Company(Co);
        var a = SignedIn(Nick, "session-a");
        Now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime start = Now;
        Assert.AreEqual(1, (await a.Store.WriteAsync(k, Json("{\"n\":1}"), -1)).Version);
        CollectionAssert.IsSupersetOf(GenerationTiers(a, k), new[] { "latest.1", "10min", "session" });

        Now = start.AddMinutes(5);
        await a.Store.WriteAsync(k, Json("{\"n\":2}"), -1);
        Assert.AreEqual(2, TierVersion(a, k, "latest.1"));
        Assert.AreEqual(1, TierVersion(a, k, "latest.2"));
        Assert.AreEqual(1, TierVersion(a, k, "10min"), "10min isn't refreshed before 10 minutes");
        Assert.AreEqual(1, TierVersion(a, k, "session"), "session keeps the session's first write");

        Now = start.AddMinutes(11);
        await a.Store.WriteAsync(k, Json("{\"n\":3}"), -1);
        Assert.AreEqual(3, TierVersion(a, k, "10min"), "10min refreshed once its copy is 10+ minutes old");
        Assert.AreEqual(2, TierVersion(a, k, "latest.2"));

        var b = SignedIn(Nick, "session-b"); // the next play session
        Now = start.AddMinutes(12);
        await b.Store.WriteAsync(k, Json("{\"n\":4}"), -1);
        Assert.AreEqual(4, TierVersion(b, k, "session"));
        Assert.AreEqual(1, TierVersion(b, k, "session.prev"), "the previous session's first write");

        // Ten local days of play: one daily per local day, the newest 7 kept.
        DateTime day0 = start.ToLocalTime().Date;
        long v = 4;
        for (int d = 1; d <= 9; d++)
        {
            Now = DateTime.SpecifyKind(day0.AddDays(d).AddHours(9), DateTimeKind.Local).ToUniversalTime();
            v = (await b.Store.WriteAsync(k, Json("{\"day\":" + d + "}"), -1)).Version;
        }
        var dailies = GenerationTiers(b, k).Where(t => t.StartsWith("daily-", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(7, dailies.Count, string.Join(",", dailies));
        Assert.Contains("daily-" + day0.AddDays(9).ToString("yyyyMMdd"), dailies);
        Assert.IsFalse(dailies.Contains("daily-" + day0.ToString("yyyyMMdd")), "older dailies are pruned");
        Assert.AreEqual(v, TierVersion(b, k, "daily-" + day0.AddDays(9).ToString("yyyyMMdd")));
    }

    [Test]
    public async Task List_ByPrefix_ShowsLiveRecordsOnly()
    {
        var s = SignedIn(Nick, "s1").Store;
        await s.WriteAsync(SaveKeys.Profile, Json("{\"p\":1}"), -1);
        await s.WriteAsync(SaveKeys.Company("co-a"), Json("{\"a\":1}"), -1);
        await s.WriteAsync(SaveKeys.Company("co-b"), Json("{\"b\":12345}"), -1);
        await s.WriteAsync(SaveKeys.Company("co-b"), Json("{\"b\":2}"), -1);
        await s.WriteAsync(SaveKeys.Town("t-1"), Json("{\"t\":1}"), -1);
        await s.WriteAsync(SaveKeys.Town("t-2"), Json("{\"t\":2}"), -1);
        Assert.IsTrue(await s.DeleteAsync(SaveKeys.Town("t-2")));

        var cos = await s.ListAsync("companies/");
        CollectionAssert.AreEqual(new[] { "companies/co-a", "companies/co-b" }, cos.Select(i => i.Key).ToArray());
        var b = cos.Single(i => i.Key == "companies/co-b");
        Assert.AreEqual(2, b.Version);
        Assert.AreEqual(7, b.Bytes);
        CollectionAssert.AreEqual(new[] { "towns/t-1" }, (await s.ListAsync("towns/")).Select(i => i.Key).ToArray(), "deleted records aren't listed");
        Assert.AreEqual(4, (await s.ListAsync("")).Count);
        Assert.AreEqual(0, (await s.ListAsync("nothing/")).Count);
    }

    [Test]
    public async Task Delete_HidesTheRecord_AndItCanBeCreatedAgain()
    {
        var s = SignedIn(Nick, "s1").Store;
        string k = SaveKeys.Town("t-9");
        Assert.IsFalse(await s.DeleteAsync(k), "nothing to delete");
        await s.WriteAsync(k, Json("{\"old\":true}"), -1);
        await s.WriteAsync(k, Json("{\"old\":2}"), -1);
        Assert.IsTrue(await s.DeleteAsync(k));
        Assert.IsFalse((await s.ReadAsync(k)).Found);
        Assert.IsFalse(await s.DeleteAsync(k), "already deleted");

        var again = SignedIn(Nick, "s2").Store; // a deleted record doesn't come back from its backups on the next launch
        Assert.IsFalse((await again.ReadAsync(k)).Found);
        var w = await again.WriteAsync(k, Json("{\"new\":true}"), 0);
        Assert.IsTrue(w.Ok, "create-only works on a deleted key");
        Assert.Greater(w.Version, 2, "versions keep counting up past the delete");
        Assert.AreEqual("{\"new\":true}", Text((await again.ReadAsync(k)).Data));
    }

    [Test]
    public async Task TryWriteNow_LandsAfterEveryQueuedWrite()
    {
        var s = SignedIn(Nick, "s1").Store;
        string k = SaveKeys.Company(Co);
        var queued = new List<Task<SaveWrite>>();
        for (int i = 0; i < 20; i++) queued.Add(s.WriteAsync(k, Json("{\"queued\":" + i + "}"), -1));
        Assert.IsTrue(s.TryWriteNow(k, Json("{\"quit\":true}")));
        await Task.WhenAll(queued);
        var r = await s.ReadAsync(k);
        Assert.AreEqual("{\"quit\":true}", Text(r.Data), "an older queued write never lands after the quit write");
        Assert.AreEqual(21, r.Version);
        CollectionAssert.AreEqual(Enumerable.Range(1, 20).Select(i => (long)i).ToArray(), queued.Select(t => t.Result.Version).ToArray(),
            "async writes land in call order");
    }

    [Test]
    public async Task KeysThatCouldEscapeTheAccount_AreDenied()
    {
        var s = SignedIn(Nick, "s1").Store;
        await s.WriteAsync(SaveKeys.Profile, Json("{}"), -1);
        string acct = AccountFolder(Nick);
        string parent = Path.GetDirectoryName(acct);
        var before = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(acct + Path.DirectorySeparatorChar)).ToArray();
        foreach (string bad in new[] { null, "", "../escape", "companies/../../escape", "/etc/escape", "a\\b", "C:/x", "Profile",
                     "companies/" + Co + ".lock", "companies/x.tmp", "x.gen", ".hidden", "towns/", "a/b/c/d/e", "con", "lpt1.txt",
                     new string('a', 129) })
        {
            var w = await s.WriteAsync(bad, Json("{}"), -1);
            Assert.AreEqual(SaveError.Denied, w.Error, $"write '{bad}'");
            Assert.IsFalse((await s.ReadAsync(bad)).Found, $"read '{bad}'");
            Assert.IsFalse(await s.DeleteAsync(bad), $"delete '{bad}'");
            Assert.IsFalse(s.TryWriteNow(bad, Json("{}")), $"write now '{bad}'");
        }
        var after = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(acct + Path.DirectorySeparatorChar)).ToArray();
        CollectionAssert.AreEquivalent(before, after, "nothing was written outside the account folder");
    }

    [Test]
    public async Task TwoAccounts_WriteToDisjointFolders()
    {
        var nick = SignedIn(Nick, "s1");
        var dot = SignedIn(Dot, "s1");
        string a = Path.GetFullPath(AccountFolder(Nick)), d = Path.GetFullPath(AccountFolder(Dot));
        Assert.AreNotEqual(a, d);
        Assert.IsFalse(a.StartsWith(d + Path.DirectorySeparatorChar) || d.StartsWith(a + Path.DirectorySeparatorChar), "neither folder holds the other");

        await nick.Store.WriteAsync(SaveKeys.Profile, Json("{\"who\":\"nick\"}"), -1);
        await nick.Store.WriteAsync(SaveKeys.Company(Co), Json("{\"cash\":1}"), -1);
        var dotFilesBefore = Snapshot(d);
        await dot.Store.WriteAsync(SaveKeys.Profile, Json("{\"who\":\"dot\"}"), -1);
        var nickFiles = Snapshot(a);
        await dot.Store.WriteAsync(SaveKeys.Company(Co), Json("{\"cash\":2}"), -1); // same company id, different account
        await nick.Store.WriteAsync(SaveKeys.Profile, Json("{\"who\":\"nick2\"}"), -1);

        Assert.AreEqual("{\"who\":\"dot\"}", Text((await dot.Store.ReadAsync(SaveKeys.Profile)).Data));
        Assert.AreEqual("{\"cash\":1}", Text((await nick.Store.ReadAsync(SaveKeys.Company(Co))).Data));
        Assert.AreEqual("{\"cash\":2}", Text((await dot.Store.ReadAsync(SaveKeys.Company(Co))).Data));
        Assert.AreEqual(0, dotFilesBefore.Count, "Nick's writes created nothing in Dot's folder");
        foreach (var f in nickFiles.Keys)
            if (!f.Contains("profile")) Assert.AreEqual(nickFiles[f], Snapshot(a)[f], $"Dot's writes didn't touch {f}");
        Assert.IsTrue(Snapshot(d).Keys.All(f => f.StartsWith(d)), "Dot's files are all in Dot's folder");
    }

    static Dictionary<string, string> Snapshot(string dir)
    {
        var map = new Dictionary<string, string>();
        if (!Directory.Exists(dir)) return map;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            map[f] = Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(f)));
        return map;
    }

    // ------------------------------------------------------------------ leases

    [Test]
    public async Task Checkout_OfAFreeCompany_IsGranted()
    {
        var b = SignedIn(Nick, "s1");
        var l = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        Assert.IsTrue(l.Granted);
        Assert.AreEqual(Co, l.CompanyGuid);
        Assert.AreEqual("net-1", l.SessionId);
        Assert.AreEqual(TownA, l.TownName);
        Assert.Greater(l.Epoch, 0);
        Assert.Greater(l.ExpiresUtc, Now);
    }

    [Test]
    public async Task DoubleCheckout_IsRefused_WithWhoHoldsIt()
    {
        var first = SignedIn(Nick, "s1");
        var second = SignedIn(Nick, "s2"); // another game process on this PC
        var held = await first.CheckoutCompanyAsync(Co, "net-1", TownA);
        Assert.IsTrue(held.Granted);
        var refused = await second.CheckoutCompanyAsync(Co, "net-2", TownB);
        Assert.IsFalse(refused.Granted, "one company can't be in two places");
        Assert.IsFalse(string.IsNullOrEmpty(refused.HeldBy), "says who holds it");
        Assert.AreEqual(TownA, refused.TownName, "on the road in the holder's Town");
        Assert.AreEqual(held.Epoch, refused.Epoch);
        var refusedToo = await first.CheckoutCompanyAsync(Co, "net-3", TownB);
        Assert.IsFalse(refusedToo.Granted, "a different session in the same process is refused too");
    }

    [Test]
    public async Task Checkout_BySameSession_RenewsWithTheSameEpoch()
    {
        var b = SignedIn(Nick, "s1");
        var l1 = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        var l2 = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        Assert.IsTrue(l2.Granted);
        Assert.AreEqual(l1.Epoch, l2.Epoch);
    }

    [Test]
    public async Task Checkin_FreesIt_AndTheNextCheckoutHasAHigherEpoch()
    {
        var first = SignedIn(Nick, "s1");
        var second = SignedIn(Nick, "s2");
        var l1 = await first.CheckoutCompanyAsync(Co, "net-1", TownA);
        await first.CheckinCompanyAsync(l1);
        var l2 = await second.CheckoutCompanyAsync(Co, "net-2", TownA);
        Assert.IsTrue(l2.Granted);
        Assert.Greater(l2.Epoch, l1.Epoch);
    }

    [Test]
    public async Task ALockHeldByADeadProcess_IsGrantedAtOnce()
    {
        var crashed = SignedIn(Nick, "s1");
        var l1 = await crashed.CheckoutCompanyAsync(Co, "net-1", TownA);
        Assert.IsTrue(l1.Granted);
        KillHolder(crashed, l1);
        var next = SignedIn(Nick, "s2");
        var l2 = await next.CheckoutCompanyAsync(Co, "net-2", TownA); // no clock advance: no expiry involved
        Assert.IsTrue(l2.Granted, "free at once: " + l2.HeldBy);
        Assert.Greater(l2.Epoch, l1.Epoch);
    }

    [Test]
    public async Task TakeBack_BumpsTheEpoch_AndTheOldSessionsCommitsFailStale()
    {
        var away = SignedIn(Nick, "s1");   // the company is on the road here
        var home = SignedIn(Nick, "s2");   // Nick takes it back from another window
        var road = await away.CheckoutCompanyAsync(Co, "net-1", TownB);
        Assert.IsTrue((await away.CommitCompanyAsync(road, Json("{\"cash\":10}"), -1)).Ok);

        var back = await home.TakeBackAsync(Co);
        Assert.IsTrue(back.Granted);
        Assert.Greater(back.Epoch, road.Epoch);

        var stale = await away.CommitCompanyAsync(road, Json("{\"cash\":999999}"), -1);
        Assert.IsFalse(stale.Ok);
        Assert.AreEqual(SaveError.StaleEpoch, stale.Error);
        Assert.IsTrue((await home.CommitCompanyAsync(back, Json("{\"cash\":11}"), -1)).Ok);

        await away.CheckinCompanyAsync(road); // the old session's check-in must not free the new holder's lease
        var third = SignedIn(Nick, "s3");
        Assert.IsFalse((await third.CheckoutCompanyAsync(Co, "net-3", TownA)).Granted, "still held by the taker");
        Assert.AreEqual("{\"cash\":11}", Text((await home.Store.ReadAsync(SaveKeys.Company(Co))).Data));
    }

    [Test]
    public async Task Commit_ChecksVersionAndSize()
    {
        var b = SignedIn(Nick, "s1");
        var l = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        var c1 = await b.CommitCompanyAsync(l, Json("{\"cash\":1}"), 0);
        Assert.IsTrue(c1.Ok);
        var c2 = await b.CommitCompanyAsync(l, Json("{\"cash\":2}"), c1.Version);
        Assert.IsTrue(c2.Ok);
        Assert.AreEqual(SaveError.VersionConflict, (await b.CommitCompanyAsync(l, Json("{\"cash\":3}"), c1.Version)).Error);
        Assert.AreEqual(SaveError.TooLarge, (await b.CommitCompanyAsync(l, Blob(1024 * 1024 + 1), -1)).Error);
        Assert.AreEqual("{\"cash\":2}", Text((await b.Store.ReadAsync(SaveKeys.Company(Co))).Data));
    }

    [Test]
    public async Task Commit_WithARefusedLease_IsDenied()
    {
        var first = SignedIn(Nick, "s1");
        var second = SignedIn(Nick, "s2");
        await first.CheckoutCompanyAsync(Co, "net-1", TownA);
        var refused = await second.CheckoutCompanyAsync(Co, "net-2", TownA);
        Assert.IsFalse(refused.Granted);
        Assert.IsFalse((await second.CommitCompanyAsync(refused, Json("{\"cash\":5}"), -1)).Ok);
        Assert.IsFalse((await second.CommitCompanyAsync(default, Json("{\"cash\":5}"), -1)).Ok);
        Assert.IsFalse((await second.Store.ReadAsync(SaveKeys.Company(Co))).Found);
    }

    [Test]
    public async Task ALateCommit_AfterCheckin_WithTheSameEpoch_IsAccepted()
    {
        var b = SignedIn(Nick, "s1");
        var l = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        await b.CheckinCompanyAsync(l);
        var late = await b.CommitCompanyAsync(l, Json("{\"cash\":42}"), -1);
        Assert.IsTrue(late.Ok, "nobody checked it out since (Accounts.md §6.4)");
        var other = SignedIn(Nick, "s2");
        var l2 = await other.CheckoutCompanyAsync(Co, "net-2", TownA);
        Assert.IsTrue(l2.Granted);
        Assert.AreEqual(SaveError.StaleEpoch, (await b.CommitCompanyAsync(l, Json("{\"cash\":43}"), -1)).Error, "but not once someone else has it");
    }

    [Test]
    public async Task RestorePrevious_BringsBackTheCompanyFromBeforeTheVisit_AndIsUndoable()
    {
        var b = SignedIn(Nick, "s1");
        Assert.IsFalse(await b.RestorePreviousAsync(Co), "nothing to restore yet");
        var home = await b.CheckoutCompanyAsync(Co, "net-1", TownA);
        await b.CommitCompanyAsync(home, Json("{\"cash\":500}"), -1);
        await b.CheckinCompanyAsync(home);

        var visit = await b.CheckoutCompanyAsync(Co, "net-2", TownB); // into another Town: keeps a copy from before
        await b.CommitCompanyAsync(visit, Json("{\"cash\":3}"), -1);
        Assert.IsFalse(await b.RestorePreviousAsync(Co), "not while it's on the road");
        await b.CheckinCompanyAsync(visit);

        Assert.IsTrue(await b.RestorePreviousAsync(Co));
        Assert.AreEqual("{\"cash\":500}", Text((await b.Store.ReadAsync(SaveKeys.Company(Co))).Data));
        Assert.AreEqual(SaveError.StaleEpoch, (await b.CommitCompanyAsync(visit, Json("{\"cash\":4}"), -1)).Error,
            "a restore starts a new epoch, so the visit can't commit over it");
        Assert.IsTrue(await b.RestorePreviousAsync(Co), "undo");
        Assert.AreEqual("{\"cash\":3}", Text((await b.Store.ReadAsync(SaveKeys.Company(Co))).Data));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

/// <summary><see cref="AccountBackendContract"/> against <see cref="LocalAccountBackend"/> in a throwaway data root.</summary>
public class LocalAccountBackendContractTests : AccountBackendContract
{
    string dataRoot;

    [SetUp]
    public void LocalSetUp()
    {
        dataRoot = Path.Combine(Path.GetTempPath(), "rubbish-b4-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
    }

    [TearDown]
    public void LocalTearDown()
    {
        try { Directory.Delete(dataRoot, true); } catch (Exception) { /* temp */ }
    }

    protected override IAccountBackend Open(PlayerKey who, string playSession) =>
        new LocalAccountBackend(AccountFolder(who), who, Clock, playSession);

    protected override string AccountFolder(PlayerKey who) => LocalAccountBackend.AccountFolder(dataRoot, who);

    protected override void KillHolder(IAccountBackend holder, CompanyLease lease) =>
        LocalStoreTestUtil.RewriteLock(((LocalAccountBackend)holder).Files.Scope, lease.CompanyGuid, "pid", LocalStoreTestUtil.DeadPid().ToString(CultureInfo.InvariantCulture));

    protected override void CrashMidWrite(IAccountBackend b, string key, byte[] data)
    {
        var store = ((LocalAccountBackend)b).Files;
        Assert.IsTrue(store.TryPathFor(key, out string path));
        long next = store.Inspect(key).Version + 1;
        // Exactly what SafeFile.WriteAtomic leaves when it dies between "flush" and "rename".
        File.WriteAllBytes(path + ".tmp", StoreRecord.Encode(next, Clock(), data));
    }

    protected override IReadOnlyList<string> GenerationTiers(IAccountBackend b, string key) =>
        ((LocalAccountBackend)b).Files.Generations(key).Select(g => g.tier).ToList();

    protected override long TierVersion(IAccountBackend b, string key, string tier)
    {
        var r = ((LocalAccountBackend)b).Files.ReadGeneration(key, tier);
        return r.Found ? r.Version : -1;
    }
}

static class LocalStoreTestUtil
{
    /// <summary>A PID with no process behind it.</summary>
    public static int DeadPid()
    {
        for (int pid = 999_983; pid > 300_000; pid -= 7)
        {
            try { using (System.Diagnostics.Process.GetProcessById(pid)) { } }
            catch (ArgumentException) { return pid; }
            catch (InvalidOperationException) { return pid; }
        }
        Assert.Fail("no free PID found");
        return 0;
    }

    /// <summary>Change one field of a lock file in place (simulating another process's lock).</summary>
    public static void RewriteLock(string accountDir, string guid, string field, string value)
    {
        string path = Path.Combine(accountDir, "companies", guid + ".lock");
        string text = File.ReadAllText(path);
        Assert.IsTrue(Regex.IsMatch(text, "(?m)^" + field + "="), $"lock has {field}=");
        File.WriteAllText(path, Regex.Replace(text, "(?m)^" + field + "=.*$", field + "=" + value));
    }
}

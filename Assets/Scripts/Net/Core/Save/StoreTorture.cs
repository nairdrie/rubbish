using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// The strict kill-during-write test of the account store and leases (Docs/Multiplayer.md §8.7.4 B4 gate; M0 gate 10). It
/// complements B5's <c>-savetorture</c> bot drill (records + checksums through <see cref="LocalFileStore"/>) by also checking
/// acknowledged writes, version/data binding and lock liveness. A process launched with
/// <c>-storetorture</c> (see <c>StoreTortureBoot</c>, and <c>Tools/bots/store_torture.sh</c>, which kills it at random moments):
/// <list type="number">
/// <item><b>verifies</b> what the previous (killed) process left: every record that was ever acknowledged still verifies (the
/// main file, a finished temp file or a generation), no acknowledged write was lost, a record is at most one write past its last
/// acknowledgement, the version inside each payload equals the store's version (version and data land together), deleted
/// records stay deleted, no main file was ever damaged, and the dead process's company lock is free at once with a higher epoch;</item>
/// <item>then <b>writes</b> as fast as it can (store writes of 1 KB–1 MB, company commits under a lease, deletes, check-in /
/// check-out, quit-path writes), appending an acknowledgement after each one, until it's killed or the time runs out.</item>
/// </list>
/// Everything lives under <c>&lt;data root&gt;/accounts/dev_store-torture/</c> and <c>&lt;data root&gt;/store-torture/</c>
/// (<c>acks.log</c>, <c>report.jsonl</c> with one verify line per launch, <c>running-&lt;pid&gt;</c>). Callers must pass a scratch data
/// root, never the real one. Pure (no Unity API but <c>Debug.Log</c>), so a plain Mono harness can run it too.
/// </summary>
public static class StoreTorture
{
    public const string Flag = "-storetorture";             // -storetorture [run|verify]
    public const string SecondsFlag = "-storetorture-seconds"; // run time if nobody kills it (default 30)
    public const string SeedFlag = "-storetorture-seed";
    public const string Folder = "store-torture";
    public const string AcksFile = "acks.log", ReportFile = "report.jsonl", RunningPrefix = "running-";
    public const string PlayerName = "store-torture";
    public const string CompanyA = "torture-company-a", CompanyB = "torture-company-b", TownGuid = "torture-town", TownName = "Torture Town";

    static string[] Keys => new[] { SaveKeys.Profile, SaveKeys.Company(CompanyA), SaveKeys.Company(CompanyB), SaveKeys.Town(TownGuid) };

    public sealed class Report
    {
        public bool Ok = true;
        public string Mode;
        public int Pid;
        /// <summary>Keys with an acknowledged write, and how they verified.</summary>
        public int AckedKeys, Live, Deleted, FromTmp, FromBackup;
        /// <summary>Temp files the kill left behind (a write was in flight); damaged main files (must be 0).</summary>
        public int TmpLeft, Corrupt;
        public bool LeaseFreeAtOnce;
        public long Epoch;
        public int Acks;
        public readonly List<string> Problems = new List<string>();

        public void Fail(string why)
        {
            Ok = false;
            if (Problems.Count < 20) Problems.Add(why);
        }

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"pid\":").Append(Pid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mode\":\"").Append(Mode).Append('"');
            sb.Append(",\"ok\":").Append(Ok ? "true" : "false");
            Num(sb, "acks", Acks); Num(sb, "ackedKeys", AckedKeys); Num(sb, "live", Live); Num(sb, "deleted", Deleted);
            Num(sb, "fromTmp", FromTmp); Num(sb, "fromBackup", FromBackup); Num(sb, "tmpLeft", TmpLeft); Num(sb, "corrupt", Corrupt);
            sb.Append(",\"leaseFreeAtOnce\":").Append(LeaseFreeAtOnce ? "true" : "false");
            sb.Append(",\"epoch\":").Append(Epoch.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"problems\":[");
            for (int i = 0; i < Problems.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Problems[i].Replace("\\", "/").Replace("\"", "'")).Append('"');
            }
            return sb.Append("]}").ToString();
        }

        static void Num(StringBuilder sb, string k, long v) => sb.Append(",\"").Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));
    }

    static int Pid
    {
        get
        {
            try { using (var p = System.Diagnostics.Process.GetCurrentProcess()) return p.Id; }
            catch (Exception) { return 0; }
        }
    }

    // ------------------------------------------------------------------ verify

    /// <summary>Check what the previous process left. Appends one line to <c>report.jsonl</c>.</summary>
    public static Report Verify(string dataRoot)
    {
        var rep = VerifyCore(dataRoot);
        AppendReport(dataRoot, rep);
        return rep;
    }

    static Report VerifyCore(string dataRoot)
    {
        var rep = new Report { Mode = "verify", Pid = Pid };
        var acks = ReadAcks(Path.Combine(dataRoot, Folder, AcksFile), out long lastEpoch, out rep.Acks);
        var backend = LocalAccountBackend.ForPlayer(dataRoot, PlayerKey.Dev(PlayerName), null, "verify-" + rep.Pid);
        string acct = backend.Files.Scope;

        // Evidence first (reads repair leftovers): temp files mean a kill landed mid-write; corrupt copies mean a main file broke.
        if (Directory.Exists(acct))
        {
            foreach (string f in Directory.GetFiles(acct, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".json.tmp", StringComparison.Ordinal) || name.EndsWith(".lock.tmp", StringComparison.Ordinal)) rep.TmpLeft++;
                if (name.Contains(".corrupt-")) rep.Corrupt++;
            }
        }
        if (rep.Corrupt > 0) rep.Fail($"{rep.Corrupt} damaged main file(s) were quarantined: an atomic replace didn't hold");

        foreach (string key in Keys)
        {
            var info = backend.Files.Inspect(key);
            if (info.Any && info.Source == "tmp") rep.FromTmp++;
            else if (info.Any && info.Source != SafeFile.Main) rep.FromBackup++;
            bool acked = acks.TryGetValue(key, out var last);
            if (info.Live)
            {
                string bad = CheckPayload(key, info.Version, info.Payload);
                if (bad != null) rep.Fail($"{key} v{info.Version}: {bad}");
            }
            if (!acked) continue;
            rep.AckedKeys++;
            if (!info.Any)
            {
                rep.Fail($"{key}: nothing verifies (main, temp and every generation), last ack v{last.version}");
                continue;
            }
            if (info.Version < last.version) rep.Fail($"{key}: acknowledged v{last.version} was lost (now v{info.Version} from {info.Source})");
            else if (info.Version > last.version + 1) rep.Fail($"{key}: v{info.Version} is more than one write past the last ack v{last.version}");
            if (info.Live)
            {
                rep.Live++;
                if (last.op == 'd' && info.Version == last.version) rep.Fail($"{key}: deleted at v{last.version} but it's back");
            }
            else
            {
                rep.Deleted++;
                bool expected = (last.op == 'd' && info.Version == last.version) || info.Version == last.version + 1;
                if (!expected) rep.Fail($"{key}: unexpected tombstone v{info.Version} (last ack {last.op} v{last.version})");
            }
        }

        // The killed process held company A. Its lock must be free at once, with a higher epoch.
        var before = backend.Inspect(CompanyA);
        var lease = backend.CheckoutNow(CompanyA, "verify-" + rep.Pid, TownName);
        rep.LeaseFreeAtOnce = lease.Granted;
        if (!lease.Granted)
        {
            if (before.Exists) rep.Fail($"company lock not free after the kill: held by {lease.HeldBy}");
        }
        else
        {
            rep.Epoch = lease.Epoch;
            if (lease.Epoch <= lastEpoch) rep.Fail($"epoch went backwards: {lease.Epoch} after an acknowledged {lastEpoch}");
            if (!backend.CheckinNow(lease)) rep.Fail("couldn't check the verify lease back in");
        }
        return rep;
    }

    // ------------------------------------------------------------------ run

    /// <summary>Verify, then write until killed or <paramref name="seconds"/> pass. Returns the verify report (plus run problems).</summary>
    public static Report Run(string dataRoot, int seconds, ulong seed, Action<string> log = null)
    {
        log = log ?? (s => Debug.Log(s));
        var rep = VerifyCore(dataRoot);
        rep.Mode = "run";
        AppendReport(dataRoot, rep);
        if (!rep.Ok) return rep;

        string dir = Path.Combine(dataRoot, Folder);
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir, RunningPrefix + "*")) TryDelete(f);

        var backend = LocalAccountBackend.ForPlayer(dataRoot, PlayerKey.Dev(PlayerName), null, "torture-" + rep.Pid);
        var store = backend.Files;
        var ver = new Dictionary<string, long>();   // last version number used (tombstones included)
        var vis = new Dictionary<string, long>();   // visible version (0 when absent or deleted)
        foreach (string key in Keys)
        {
            var info = store.Inspect(key);
            ver[key] = info.Any ? info.Version : 0;
            vis[key] = info.Live ? info.Version : 0;
        }

        var rng = new Rng(seed ^ ((ulong)rep.Pid << 32) ^ (ulong)DateTime.UtcNow.Ticks);
        int ops = 0;
        using (var acks = new FileStream(Path.Combine(dir, AcksFile), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            void Ack(char op, string key, long v)
            {
                byte[] b = Encoding.ASCII.GetBytes(op + " " + key + " " + v.ToString(CultureInfo.InvariantCulture) + "\n");
                acks.Write(b, 0, b.Length);
                acks.Flush(); // to the OS: a killed process keeps it; only power loss wouldn't
            }

            // Baseline: what this launch verified (it may include one unacknowledged write from the killed process).
            foreach (string key in Keys)
                if (ver[key] > 0) Ack(vis[key] > 0 ? 'w' : 'd', key, ver[key]);

            var lease = backend.CheckoutNow(CompanyA, backend.SessionId, TownName);
            if (!lease.Granted) { rep.Fail("run: couldn't check out company A: " + lease.HeldBy); return rep; }
            Ack('l', "epoch", lease.Epoch);

            File.WriteAllText(Path.Combine(dir, RunningPrefix + rep.Pid.ToString(CultureInfo.InvariantCulture)), "writing");
            DateTime end = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            string companyA = SaveKeys.Company(CompanyA), companyB = SaveKeys.Company(CompanyB), town = SaveKeys.Town(TownGuid);
            while (DateTime.UtcNow < end && rep.Ok)
            {
                int r = rng.Range(0, 100);
                if (r < 30)
                {
                    long v = ver[companyA] + 1;
                    var w = backend.CommitNow(lease, Payload(companyA, v, rng.Range(50_000, 400_000), rng), vis[companyA]);
                    if (!w.Ok || w.Version != v) { rep.Fail($"run: commit of {companyA} gave {w.Error} v{w.Version}, wanted v{v}"); break; }
                    ver[companyA] = vis[companyA] = v;
                    Ack('w', companyA, v);
                }
                else if (r < 85)
                {
                    string key = r < 45 ? SaveKeys.Profile : r < 65 ? companyB : town;
                    int size = key == SaveKeys.Profile ? rng.Range(1_000, 4_000) : key == companyB ? rng.Range(300_000, 1_000_000) : rng.Range(20_000, 150_000);
                    long v = ver[key] + 1;
                    var w = store.WriteNow(key, Payload(key, v, size, rng), vis[key]);
                    if (!w.Ok || w.Version != v) { rep.Fail($"run: write of {key} gave {w.Error} v{w.Version}, wanted v{v}"); break; }
                    ver[key] = vis[key] = v;
                    Ack('w', key, v);
                }
                else if (r < 90)
                {
                    if (vis[town] == 0) continue;
                    if (!store.DeleteNow(town)) { rep.Fail("run: delete of the town failed"); break; }
                    var info = store.Inspect(town);
                    if (!info.Deleted || info.Version != ver[town] + 1) { rep.Fail($"run: tombstone is v{info.Version}, wanted v{ver[town] + 1}"); break; }
                    ver[town] = info.Version;
                    vis[town] = 0;
                    Ack('d', town, info.Version);
                }
                else if (r < 95)
                {
                    if (!backend.CheckinNow(lease)) { rep.Fail("run: check-in failed"); break; }
                    lease = backend.CheckoutNow(CompanyA, backend.SessionId, TownName);
                    if (!lease.Granted) { rep.Fail("run: re-checkout refused: " + lease.HeldBy); break; }
                    Ack('l', "epoch", lease.Epoch);
                }
                else
                {
                    long v = ver[SaveKeys.Profile] + 1; // single writer: the quit path's "any version" write lands as v
                    if (!store.TryWriteNow(SaveKeys.Profile, Payload(SaveKeys.Profile, v, rng.Range(1_000, 4_000), rng)))
                    { rep.Fail("run: TryWriteNow failed"); break; }
                    ver[SaveKeys.Profile] = vis[SaveKeys.Profile] = v;
                    Ack('w', SaveKeys.Profile, v);
                }
                ops++;
            }
            backend.CheckinNow(lease);
        }
        log($"[StoreTorture] ran {ops} operations without being killed; {(rep.Ok ? "no problems" : string.Join("; ", rep.Problems))}");
        return rep;
    }

    // ------------------------------------------------------------------ payloads and acks

    /// <summary><c>{"torture":1,"key":…,"version":v,"n":len,"fnv":hex,"pad":"…"}</c>: binds the payload to its key and version.</summary>
    static byte[] Payload(string key, long version, int size, Rng rng)
    {
        // Slices of one random block, so the loop's time goes to writing (where a kill should land), not to making data.
        if (letters == null)
        {
            var block = new char[1 << 20];
            for (int i = 0; i < block.Length; i++) block[i] = (char)('a' + (int)(rng.Next() % 26));
            letters = new string(block);
        }
        int n = Math.Min(letters.Length, Math.Max(16, size - 128));
        string p = letters.Substring(rng.Range(0, letters.Length - n + 1), n);
        string json = "{\"torture\":1,\"key\":\"" + key + "\",\"version\":" + version.ToString(CultureInfo.InvariantCulture) +
                      ",\"n\":" + n.ToString(CultureInfo.InvariantCulture) + ",\"fnv\":\"" + Fnv(p).ToString("x16", CultureInfo.InvariantCulture) +
                      "\",\"pad\":\"" + p + "\"}";
        return Encoding.UTF8.GetBytes(json);
    }

    static string letters;

    /// <summary>Null if the payload is intact and says it's <paramref name="key"/> at <paramref name="version"/>.</summary>
    static string CheckPayload(string key, long version, byte[] payload)
    {
        if (payload == null) return "no payload";
        string s = Encoding.UTF8.GetString(payload);
        string k = Between(s, "\"key\":\"", "\"");
        string v = Between(s, "\"version\":", ",");
        string n = Between(s, "\"n\":", ",");
        string f = Between(s, "\"fnv\":\"", "\"");
        string p = Between(s, "\"pad\":\"", "\"}");
        if (k == null || v == null || n == null || f == null || p == null) return "payload isn't a torture record";
        if (k != key) return $"payload belongs to '{k}'";
        if (v != version.ToString(CultureInfo.InvariantCulture)) return $"payload says v{v} but the store says v{version} (version and data didn't land together)";
        if (n != p.Length.ToString(CultureInfo.InvariantCulture)) return "payload length is wrong";
        if (f != Fnv(p).ToString("x16", CultureInfo.InvariantCulture)) return "payload checksum is wrong";
        return null;
    }

    static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s.Substring(i, j - i);
    }

    static Dictionary<string, (char op, long version)> ReadAcks(string path, out long lastEpoch, out int count)
    {
        var map = new Dictionary<string, (char op, long version)>(StringComparer.Ordinal);
        lastEpoch = 0;
        count = 0;
        if (!File.Exists(path)) return map;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] parts = line.Split(' ');
            if (parts.Length != 3 || parts[0].Length != 1) continue; // a kill mid-append leaves a partial last line
            if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long v)) continue;
            count++;
            char op = parts[0][0];
            if (op == 'l') lastEpoch = Math.Max(lastEpoch, v);
            else if (op == 'w' || op == 'd') map[parts[1]] = (op, v);
        }
        return map;
    }

    static void AppendReport(string dataRoot, Report rep)
    {
        try
        {
            string dir = Path.Combine(dataRoot, Folder);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, ReportFile), rep.ToJson() + "\n");
        }
        catch (Exception e) { Debug.LogWarning("[StoreTorture] Couldn't append the report: " + e.Message); }
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (Exception) { /* a marker; harmless */ }
    }

    static ulong Fnv(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>xorshift64: test data only (no gameplay meaning).</summary>
    sealed class Rng
    {
        ulong s;
        public Rng(ulong seed) { s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }

        public ulong Next()
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            return s;
        }

        public int Range(int min, int maxExclusive) => min + (int)(Next() % (ulong)(maxExclusive - min));
    }
}

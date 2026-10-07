using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// <c>-savetorture</c>: kill-during-write drills for the save layer (Docs/Multiplayer.md §8.7.4: B4's gate, "1,000 kill-during-write
/// cycles never leave a record without a parseable latest or fallback"; M0 gate 10). Driven by <c>Tools/bots/savetorture.sh</c>.
/// <para>
/// Each launch first VERIFIES every record the previous (killed) launch was writing, logging
/// <c>TORTURE VERIFY ok=N bad=N missing=N via=…</c> (any bad record is also a Debug error), then logs <c>TORTURE WRITING</c> and
/// rewrites four records as fast as it can, 2–200 KB each with a checksum inside, until the script kills it. <c>-savetorture verify</c>
/// only verifies and exits (0 = every record loads).
/// </para>
/// Records go through B4's <c>LocalFileStore</c> (an <see cref="ISaveStore"/>, found by name with a <c>(string root)</c>
/// constructor) when it exists, else straight through L0's <see cref="SafeFile"/>. Only ever under a <c>-dataroot</c>: it refuses
/// the real save folder.
/// </summary>
public static class SaveTorture
{
    public const string Flag = "-savetorture";
    public const string VerifyLine = "TORTURE VERIFY";
    public const string WritingLine = "TORTURE WRITING";
    const int Keys = 4;

    [Serializable]
    class Record
    {
        public string key;
        public long seq;
        public int len;
        public string sum;
        public string pad;
    }

    public static IEnumerator Run(BotOptions o, BotDriver driver)
    {
        if (!SaveRoot.IsOverridden || SaveRoot.IsTestOverride)
        {
            Debug.LogError($"[Torture] Refusing to run on the real save folder ({SaveRoot.Path}); pass -dataroot <scratch dir>.");
            driver.End(BotDriver.ExitFailed);
            yield break;
        }
        string root = Path.Combine(SaveRoot.Path, "torture");
        Directory.CreateDirectory(root);
        ISaveStore store = MakeStore(root, out string via);

        // ---- verify what the previous launch left behind
        int ok = 0, bad = 0, missing = 0;
        var notes = new List<string>();
        for (int k = 0; k < Keys; k++)
        {
            string key = "torture/k" + k;
            byte[] data = null;
            string source = null;
            bool found;
            if (store != null)
            {
                var task = store.ReadAsync(key);
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                {
                    bad++;
                    notes.Add($"{key}: read threw {task.Exception?.GetBaseException().Message}");
                    continue;
                }
                found = task.Result.Found;
                data = task.Result.Data;
                source = task.Result.Source;
            }
            else
            {
                string path = Path.Combine(root, "k" + k + ".json");
                if (!SafeFile.AnyCopyExists(path)) { missing++; continue; }
                found = SafeFile.TryReadNewest(path, Valid, out data, out source);
                if (!found) { bad++; notes.Add($"{key}: copies exist but none passes its checksum"); continue; }
            }
            if (!found) { missing++; continue; }
            if (Valid(data)) { ok++; if (source != null && source != "main" && source != "latest") notes.Add($"{key}: from {source}"); }
            else { bad++; notes.Add($"{key}: newest readable copy ({source}) fails its checksum"); }
        }
        string line = $"{VerifyLine} ok={ok} bad={bad} missing={missing} via={via}" + (notes.Count > 0 ? " | " + string.Join("; ", notes) : "");
        if (bad > 0) Debug.LogError("[Torture] " + line);
        else Debug.Log("[Torture] " + line);
        NetAudit.Event("save", new Dictionary<string, object> { { "torture", "verify" }, { "ok", ok }, { "bad", bad }, { "missing", missing }, { "via", via } });
        if (o.torture == "verify")
        {
            driver.End(bad > 0 ? BotDriver.ExitFailed : BotDriver.ExitOk);
            yield break;
        }

        // ---- write until killed (or until -botminutes runs out)
        Debug.Log($"[Torture] {WritingLine} via={via} root={root}");
        uint rng = (uint)Environment.TickCount | 1u;
        long seq = DateTime.UtcNow.Ticks;
        int writes = 0, failures = 0;
        float end = o.minutes > 0 ? Time.realtimeSinceStartup + o.minutes * 60f : float.MaxValue;
        float nextReport = Time.realtimeSinceStartup + 5f;
        var sw = new System.Diagnostics.Stopwatch();
        while (Time.realtimeSinceStartup < end)
        {
            sw.Restart();
            while (sw.ElapsedMilliseconds < 25)
            {
                int k = writes % Keys;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                byte[] bytes = Build("torture/k" + k, ++seq, 2048 + (int)(rng % (200 * 1024)), rng);
                bool wrote;
                try
                {
                    wrote = store != null
                        ? store.TryWriteNow("torture/k" + k, bytes)
                        // coop:allow(save) the torture harness writes test records through L0's SafeFile, only under a -dataroot
                        : SafeFile.WriteAtomic(Path.Combine(root, "k" + k + ".json"), bytes, DateTime.UtcNow, SafeFile.ProcessSessionId);
                }
                catch (Exception e) { wrote = false; Debug.LogWarning("[Torture] write threw: " + e.Message); }
                writes++;
                if (!wrote) failures++;
            }
            if (Time.realtimeSinceStartup >= nextReport)
            {
                nextReport = Time.realtimeSinceStartup + 5f;
                Debug.Log($"[Torture] {writes} writes ({failures} refused)");
            }
            yield return null;
        }
        Debug.Log($"[Torture] Time's up after {writes} writes ({failures} refused).");
        driver.End(BotDriver.ExitOk);
    }

    static byte[] Build(string key, long seq, int len, uint seed)
    {
        var pad = new StringBuilder(len);
        uint r = seed | 1u;
        for (int i = 0; i < len; i++)
        {
            r ^= r << 13; r ^= r >> 17; r ^= r << 5;
            pad.Append((char)('a' + r % 26));
        }
        string p = pad.ToString();
        var rec = new Record { key = key, seq = seq, len = len, sum = WorldHash.Fnv(WorldHash.Seed, p).ToString("x16"), pad = p };
        return Encoding.UTF8.GetBytes(JsonUtility.ToJson(rec));
    }

    /// <summary>A record parses and its checksum and length hold (a torn or mixed write fails).</summary>
    static bool Valid(byte[] data)
    {
        if (data == null || data.Length == 0) return false;
        try
        {
            var rec = JsonUtility.FromJson<Record>(Encoding.UTF8.GetString(data));
            return rec != null && rec.pad != null && rec.pad.Length == rec.len &&
                   WorldHash.Fnv(WorldHash.Seed, rec.pad).ToString("x16") == rec.sum;
        }
        catch (Exception) { return false; }
    }

    /// <summary>B4's LocalFileStore over <paramref name="root"/> if it's in the build, else null (SafeFile directly).</summary>
    static ISaveStore MakeStore(string root, out string via)
    {
        via = "SafeFile";
        try
        {
            Type t = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("LocalFileStore", false))
                .FirstOrDefault(x => x != null && typeof(ISaveStore).IsAssignableFrom(x));
            if (t == null) return null;
            var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (ctor == null)
            {
                Debug.Log("[Torture] LocalFileStore has no (string root) constructor; torturing SafeFile directly.");
                return null;
            }
            via = "LocalFileStore";
            return (ISaveStore)ctor.Invoke(new object[] { root });
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Torture] Couldn't make a LocalFileStore: " + (e.InnerException ?? e).Message);
            via = "SafeFile";
            return null;
        }
    }
}

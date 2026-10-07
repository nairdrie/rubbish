using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Crash-safe save files with time-tiered generations (Docs/Accounts.md §5.1, hotfix L0). Pure: no game types, so it lives in
/// Rubbish.Net.Core. Every write goes <c>x.tmp</c> → flush → rename over <c>x.json</c>, then updates full copies in
/// <c>x.gen/</c>:
/// <list type="table">
/// <item><c>latest.1</c>, <c>latest.2</c>: every write (2 kept)</item>
/// <item><c>10min</c>: when the existing copy is 10+ minutes old</item>
/// <item><c>session</c> (+ <c>session.prev</c>): first write of each play session</item>
/// <item><c>daily-yyyyMMdd</c>: first write of each local day (7 kept)</item>
/// <item><c>premigration-v&lt;n&gt;</c>: before a migration rewrites the file (kept forever)</item>
/// </list>
/// A generation's savedUtc is its file time, set to the <c>nowUtc</c> passed in, so tier rules are testable with a fake clock.
/// Reads never write; callers decide when to <see cref="Quarantine"/> an unreadable file.
/// </summary>
public static class SafeFile
{
    public const string Main = "main", Tmp = "tmp", Bak = "bak";
    public const string Latest1 = "latest.1", Latest2 = "latest.2", TenMin = "10min", Session = "session",
        SessionPrev = "session.prev", DailyPrefix = "daily-", PreMigrationPrefix = "premigration-v";
    public const int DailyKept = 7;
    public static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    static string processSessionId;

    /// <summary>A session id for callers that don't track play sessions (one per process).</summary>
    public static string ProcessSessionId => processSessionId ??= NewSessionId(DateTime.UtcNow);

    /// <summary>A fresh session id: UTC time (ms) + process id. Unique per play session in practice.</summary>
    public static string NewSessionId(DateTime nowUtc)
    {
        int pid = 0;
        try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch (Exception) { /* sandboxed */ }
        return AsUtc(nowUtc).ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "-" + pid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>dir/name.json</c> → <c>dir/name.gen</c>.</summary>
    public static string GenDir(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", System.IO.Path.GetFileNameWithoutExtension(path) + ".gen");

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Atomically replace <paramref name="path"/> with <paramref name="data"/>, then update the generations. False (logged) when
    /// the main file couldn't be written; a generation failure is logged but still returns true. Never throws.
    /// </summary>
    public static bool WriteAtomic(string path, byte[] data, DateTime nowUtc, string sessionId)
    {
        if (string.IsNullOrEmpty(path) || data == null) return false;
        nowUtc = AsUtc(nowUtc);
        string tmp = path + ".tmp";
        try
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Io(() =>
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
            });
            ReplaceWith(tmp, path);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SafeFile] Couldn't write {System.IO.Path.GetFileName(path)}: {e.Message}");
            return false;
        }

        try { RotateGenerations(path, data, nowUtc, sessionId ?? ProcessSessionId); }
        catch (Exception e) { Debug.LogWarning($"[SafeFile] Couldn't update the backups of {System.IO.Path.GetFileName(path)}: {e.Message}"); }
        return true;
    }

    // ------------------------------------------------------------------ Windows: brief retries
    //
    // On Windows a file we just closed is often opened for a few milliseconds by an antivirus scan (Defender) or the search
    // indexer, and a rename/replace/delete that touches it fails with a sharing violation (IOException) or "access denied"
    // (UnauthorizedAccessException). Each file operation is retried a few times (≤ ~0.2 s in total, only when it fails) before
    // the failure is reported, so a scan never turns a save into a "recovered from a backup" on the next launch.

    /// <summary>Attempts per file operation (1 + retries).</summary>
    public const int IoAttempts = 5;
    /// <summary>Sleep before retry n is n × this.</summary>
    public const int IoRetryDelayMs = 20;
    static int ioRetries;
    /// <summary>File operations that needed a retry (diagnostics and tests).</summary>
    public static int IoRetries => System.Threading.Volatile.Read(ref ioRetries);

    static void Io(Action op)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { op(); return; }
            catch (Exception e) when (attempt < IoAttempts && Transient(e))
            {
                System.Threading.Interlocked.Increment(ref ioRetries);
                System.Threading.Thread.Sleep(IoRetryDelayMs * attempt);
            }
        }
    }

    /// <summary>Worth a retry: a sharing/lock violation or "access denied" (a scanner holding the file). Not a missing file or
    /// folder, a too-long path, or a full disk: those won't change in 0.2 s.</summary>
    static bool Transient(Exception e)
    {
        if (e is UnauthorizedAccessException) return true;
        if (!(e is IOException) || e is FileNotFoundException || e is DirectoryNotFoundException || e is PathTooLongException) return false;
        int win32 = e.HResult & 0xFFFF;
        return win32 != 112 && win32 != 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
    }

    static void ReplaceWith(string tmp, string path)
    {
        try
        {
            // Re-checked on every attempt: a Windows ReplaceFile that fails half-way (ERROR_UNABLE_TO_MOVE_REPLACEMENT) leaves
            // no main file and the temp file in place, so the next attempt is a plain move.
            Io(() =>
            {
                if (!File.Exists(path)) File.Move(tmp, path);
                else File.Replace(tmp, path, null);
            });
        }
        catch (Exception) when (File.Exists(tmp))
        {
            // Some platforms/filesystems refuse Replace. Delete + move has a tiny window with no main file: a crash there is
            // recovered from the flushed .tmp or latest.1 (see Candidates).
            Io(() => { if (File.Exists(path)) File.Delete(path); });
            Io(() => File.Move(tmp, path));
        }
    }

    static void RotateGenerations(string path, byte[] data, DateTime nowUtc, string sessionId)
    {
        string gen = GenDir(path);
        Directory.CreateDirectory(gen);

        // latest.1 / latest.2: every write.
        string l1 = GenFile(gen, Latest1), l2 = GenFile(gen, Latest2);
        if (File.Exists(l1))
        {
            Io(() =>
            {
                if (File.Exists(l2)) File.Delete(l2);
                File.Move(l1, l2);
            });
        }
        WriteGen(l1, data, nowUtc);

        // 10min: refreshed when the existing copy is 10+ minutes old.
        string ten = GenFile(gen, TenMin);
        if (!File.Exists(ten) || nowUtc - File.GetLastWriteTimeUtc(ten) >= TenMinutes) WriteGen(ten, data, nowUtc);

        // session: first write of each play session; the previous session's copy is kept as session.prev.
        string ses = GenFile(gen, Session), sesId = System.IO.Path.Combine(gen, Session + ".sid");
        string cur = File.Exists(sesId) ? SafeReadText(sesId) : null;
        if (!File.Exists(ses) || cur != sessionId)
        {
            if (File.Exists(ses))
            {
                string prev = GenFile(gen, SessionPrev), prevId = System.IO.Path.Combine(gen, SessionPrev + ".sid");
                Io(() =>
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(ses, prev);
                });
                Io(() =>
                {
                    if (File.Exists(prevId)) File.Delete(prevId);
                    if (File.Exists(sesId)) File.Move(sesId, prevId);
                });
            }
            WriteGen(ses, data, nowUtc);
            Io(() => File.WriteAllText(sesId, sessionId ?? ""));
        }

        // daily-yyyyMMdd: first write of each local day; the newest 7 days are kept.
        string day = DailyPrefix + nowUtc.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string daily = GenFile(gen, day);
        if (!File.Exists(daily))
        {
            WriteGen(daily, data, nowUtc);
            var days = new List<string>(Directory.GetFiles(gen, DailyPrefix + "*.json"));
            days.Sort(StringComparer.Ordinal); // yyyyMMdd sorts by date
            for (int i = 0; i < days.Count - DailyKept; i++)
            {
                string old = days[i];
                Io(() => File.Delete(old));
            }
        }
    }

    static string GenFile(string genDir, string tier) => System.IO.Path.Combine(genDir, tier + ".json");

    static void WriteGen(string file, byte[] data, DateTime nowUtc) => Io(() =>
    {
        File.WriteAllBytes(file, data);
        File.SetLastWriteTimeUtc(file, nowUtc);
    });

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Every place a copy of <paramref name="path"/> may live: the main file first (if present), then the leftover temp file,
    /// the generations and the old hotfix's <c>.bak</c>, newest first. Sources: "main", "tmp", a tier name, or "bak".
    /// </summary>
    public static IReadOnlyList<(string source, string file, DateTime savedUtc)> Candidates(string path)
    {
        var list = new List<(string source, string file, DateTime savedUtc)>();
        if (string.IsNullOrEmpty(path)) return list;
        if (File.Exists(path)) list.Add((Main, path, File.GetLastWriteTimeUtc(path)));
        var rest = new List<(string source, string file, DateTime savedUtc)>();
        string tmp = path + ".tmp", bak = path + ".bak";
        if (File.Exists(tmp)) rest.Add((Tmp, tmp, File.GetLastWriteTimeUtc(tmp)));
        foreach (var g in Generations(path)) rest.Add(g);
        if (File.Exists(bak)) rest.Add((Bak, bak, File.GetLastWriteTimeUtc(bak)));
        rest.Sort(NewestFirst);
        list.AddRange(rest);
        return list;
    }

    /// <summary>The generations in <c>x.gen/</c>, newest first.</summary>
    public static IReadOnlyList<(string tier, string file, DateTime savedUtc)> Generations(string path)
    {
        var list = new List<(string tier, string file, DateTime savedUtc)>();
        if (string.IsNullOrEmpty(path)) return list;
        string gen = GenDir(path);
        if (!Directory.Exists(gen)) return list;
        foreach (string f in Directory.GetFiles(gen, "*.json"))
            list.Add((System.IO.Path.GetFileNameWithoutExtension(f), f, File.GetLastWriteTimeUtc(f)));
        list.Sort(NewestFirst);
        return list;
    }

    // Newest first; equal times (one write landing in several tiers) prefer the most specific copy.
    static int NewestFirst((string source, string file, DateTime savedUtc) a, (string source, string file, DateTime savedUtc) b)
    {
        int c = b.savedUtc.CompareTo(a.savedUtc);
        return c != 0 ? c : Rank(a.source).CompareTo(Rank(b.source));
    }

    static int Rank(string source)
    {
        if (source == Latest1) return 0;
        if (source == Latest2) return 1;
        if (source == Tmp) return 2;
        if (source == TenMin) return 3;
        if (source == Session) return 4;
        if (source == SessionPrev) return 5;
        if (source != null && source.StartsWith(DailyPrefix, StringComparison.Ordinal)) return 6;
        if (source != null && source.StartsWith(PreMigrationPrefix, StringComparison.Ordinal)) return 7;
        return 8; // bak
    }

    /// <summary>
    /// The main file if it parses, else the newest candidate that does. False when nothing parses (or nothing exists:
    /// check <see cref="AnyCopyExists"/> to tell "fresh" from "all unreadable").
    /// </summary>
    public static bool TryReadNewest(string path, Func<byte[], bool> parses, out byte[] data, out string source)
    {
        data = null;
        source = null;
        foreach (var c in Candidates(path))
        {
            byte[] bytes = TryReadAll(c.file);
            if (bytes == null) continue;
            bool ok;
            try { ok = parses == null || parses(bytes); }
            catch (Exception) { ok = false; }
            if (!ok) continue;
            data = bytes;
            source = c.source;
            return true;
        }
        return false;
    }

    /// <summary>True if the main file, its temp file, a generation or a .bak exists.</summary>
    public static bool AnyCopyExists(string path) => Candidates(path).Count > 0;

    /// <summary>All bytes, or null if the file is missing or unreadable.</summary>
    public static byte[] TryReadAll(string file)
    {
        try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>UTF-8 text without a BOM (null for null).</summary>
    public static string Utf8(byte[] data)
    {
        if (data == null) return null;
        int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(data, skip, data.Length - skip);
    }

    // ------------------------------------------------------------------ keep

    /// <summary>
    /// Copy an unreadable <paramref name="path"/> to <c>name.corrupt-&lt;utc&gt;.json</c> beside it (never overwriting an existing
    /// copy) so no later write can destroy it. Returns the copy's path, or null if there was nothing to copy.
    /// </summary>
    public static string Quarantine(string path, DateTime nowUtc)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            string stem = System.IO.Path.GetFileNameWithoutExtension(path) + ".corrupt-" +
                          AsUtc(nowUtc).ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            string dst = System.IO.Path.Combine(dir, stem + ".json");
            for (int i = 1; File.Exists(dst); i++) dst = System.IO.Path.Combine(dir, $"{stem}-{i}.json");
            File.Copy(path, dst);
            return dst;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SafeFile] Couldn't keep a copy of unreadable {System.IO.Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>Keep the current file as <c>premigration-v&lt;fromSchema&gt;</c> (once, forever) before a migration rewrites it.</summary>
    public static void KeepPreMigration(string path, int fromSchema)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string gen = GenDir(path);
            Directory.CreateDirectory(gen);
            string dst = GenFile(gen, PreMigrationPrefix + fromSchema.ToString(CultureInfo.InvariantCulture));
            if (!File.Exists(dst)) File.Copy(path, dst);
        }
        catch (Exception e) { Debug.LogWarning($"[SafeFile] Couldn't keep the pre-migration copy of {System.IO.Path.GetFileName(path)}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ compat (the lead's first hotfix API)

    /// <summary>Text convenience: <see cref="WriteAtomic(string, byte[], DateTime, string)"/> with now and the process session.</summary>
    public static void WriteAtomic(string path, string contents) =>
        WriteAtomic(path, Encoding.UTF8.GetBytes(contents ?? ""), DateTime.UtcNow, ProcessSessionId);

    /// <summary>
    /// JsonUtility read with fallback: the main file, else the newest generation/.bak that parses. An unreadable main file is
    /// quarantined first. Null when nothing exists or nothing parses.
    /// </summary>
    public static T ReadJson<T>(string path, string logTag) where T : class
    {
        bool mainBad = false;
        byte[] main = TryReadAll(path);
        if (main != null && !JsonParses<T>(main)) { mainBad = true; Quarantine(path, DateTime.UtcNow); }
        if (TryReadNewest(path, JsonParses<T>, out byte[] data, out string source))
        {
            if (source != Main) Debug.LogWarning($"[{logTag}] Save was unreadable; restored from {source}.");
            return JsonUtility.FromJson<T>(Utf8(data));
        }
        if (mainBad || AnyCopyExists(path))
            Debug.LogError($"[{logTag}] No readable save found. The unreadable file was kept as *.corrupt-*.json.");
        return null;
    }

    /// <summary>Old name for <see cref="Quarantine"/>.</summary>
    public static void PreserveCorrupt(string path, string logTag)
    {
        if (Quarantine(path, DateTime.UtcNow) == null && File.Exists(path))
            Debug.LogWarning($"[{logTag}] Could not preserve {System.IO.Path.GetFileName(path)}.");
    }

    /// <summary>A JSON object that JsonUtility accepts as <typeparamref name="T"/>.</summary>
    public static bool JsonParses<T>(byte[] data) where T : class
    {
        string text = Utf8(data);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}') return false;
        try { return JsonUtility.FromJson<T>(text) != null; }
        catch (Exception) { return false; }
    }

    static string SafeReadText(string file)
    {
        try { return File.ReadAllText(file).Trim(); }
        catch (Exception) { return null; }
    }

    static DateTime AsUtc(DateTime t) =>
        t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

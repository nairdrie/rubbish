using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The local <see cref="ISaveStore"/> (Docs/Multiplayer.md §8.7.4 B4, Docs/Accounts.md §5.1): one folder per account, one file
/// per key. <c>profile</c> → <c>profile.json</c>, <c>companies/&lt;guid&gt;</c> → <c>companies/&lt;guid&gt;.json</c>.
/// <list type="bullet">
/// <item><b>One atomic write per record.</b> The version, the time and the bytes travel together in one file
/// (<see cref="StoreRecord"/>, with a CRC-32), written through L0's <see cref="SafeFile.WriteAtomic(string, byte[], DateTime, string)"/>:
/// temp → flush → rename, then the time-tiered generations in <c>&lt;name&gt;.gen/</c>.</item>
/// <item><b>Recovery.</b> A complete temp file newer than the main file (a crash between flush and rename) is adopted and
/// re-committed. A damaged main file is kept as <c>&lt;name&gt;.corrupt-&lt;utc&gt;.json</c> and the newest generation that
/// verifies is used. Versions never go backwards, even past a damaged copy.</item>
/// <item><b>Versions:</b> 0 = no record; the first write is 1. <c>expectedVersion</c> -1 = any, 0 = create only, n = must
/// equal the current version (else <see cref="SaveError.VersionConflict"/>, with the current version in the result).</item>
/// <item><b>Caps:</b> <see cref="MaxRecordBytes"/> (1 MiB) per record → <see cref="SaveError.TooLarge"/>. Keys are lower-case
/// <c>[a-z0-9._-]</c> segments joined by <c>/</c>; anything that could leave the account folder is
/// <see cref="SaveError.Denied"/>.</item>
/// <item><b>Delete</b> writes a tombstone version, so a later read can't fall back to an older generation and bring the record
/// back; the generations stay for Restore. <see cref="PurgeNow"/> removes every trace.</item>
/// <item><b>Order:</b> async calls run one at a time on a background thread, in call order. <see cref="TryWriteNow"/> waits for
/// queued calls first, so a queued older write can never land after it. Every operation also holds
/// <see cref="AccountFolderLock"/> (in-process monitor + an OS file lock, released by the kernel if the process dies).</item>
/// </list>
/// Pure: no game types and no Unity API except <c>Debug.Log*</c>, so it runs on any thread and in the torture harness.
/// </summary>
public sealed class LocalFileStore : ISaveStore
{
    /// <summary>Largest payload one record may hold (Accounts.md §7.4: "over 1 MiB inflated" is rejected).</summary>
    public const int MaxRecordBytes = NetConstants.InflateMaxBytes;
    public const string Extension = ".json";
    public const int MaxKeyLength = 128;
    public const int MaxKeySegments = 4;
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan QuitDrainTimeout = TimeSpan.FromSeconds(3);

    // A record file can't legitimately be bigger than a base64-wrapped 1 MiB payload plus its header.
    static readonly long MaxFileBytes = (MaxRecordBytes + 2L) / 3L * 4L + 1024L;

    readonly string root;
    readonly Func<DateTime> clock;
    readonly object queueGate = new object();
    Task tail = Task.CompletedTask;
    int pending;
    string sessionId;

    /// <param name="accountDir">The account folder (<c>&lt;data root&gt;/accounts/&lt;PlayerKey.FolderName&gt;</c>). Nothing is
    /// created until the first write.</param>
    /// <param name="utcNow">Clock for <c>savedUtc</c> and the generation tiers (tests pass a fake one).</param>
    /// <param name="playSessionId">Play-session id for the <c>session</c> tier (default: one per process).</param>
    public LocalFileStore(string accountDir) : this(accountDir, null, null) { } // also what B5's -savetorture drill looks up

    public LocalFileStore(string accountDir, Func<DateTime> utcNow, string playSessionId = null)
    {
        if (string.IsNullOrEmpty(accountDir)) throw new ArgumentException("account folder is empty", nameof(accountDir));
        root = Path.GetFullPath(accountDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) throw new ArgumentException("account folder can't be the file system root", nameof(accountDir));
        clock = utcNow ?? (() => DateTime.UtcNow);
        sessionId = playSessionId;
    }

    /// <summary>The account folder (absolute).</summary>
    public string Scope => root;

    /// <summary>Play-session id for the <c>session</c>/<c>session.prev</c> generation tiers.</summary>
    public string SessionId
    {
        get => sessionId ?? SafeFile.ProcessSessionId;
        set => sessionId = value;
    }

    /// <summary>Async calls queued or running.</summary>
    public int PendingOperations => Volatile.Read(ref pending);

    internal DateTime UtcNow => AsUtc(clock());

    // ------------------------------------------------------------------ ISaveStore

    public Task<SaveRead> ReadAsync(string key, CancellationToken ct = default) => Enqueue(ct, () => ReadNow(key));

    public Task<SaveWrite> WriteAsync(string key, byte[] data, long expectedVersion, CancellationToken ct = default)
    {
        byte[] copy = data == null ? null : (byte[])data.Clone(); // the caller may reuse its buffer before the write runs
        return Enqueue(ct, () => WriteNow(key, copy, expectedVersion));
    }

    public Task<IReadOnlyList<SaveKeyInfo>> ListAsync(string prefix, CancellationToken ct = default) =>
        Enqueue(ct, () => (IReadOnlyList<SaveKeyInfo>)ListNow(prefix));

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Enqueue(ct, () => DeleteNow(key));

    /// <summary>Quit path: waits (≤ <see cref="QuitDrainTimeout"/>) for queued calls, then writes synchronously (any version).</summary>
    public bool TryWriteNow(string key, byte[] data)
    {
        WaitIdle(QuitDrainTimeout);
        return WriteNow(key, data, -1).Ok;
    }

    /// <summary>Wait until every queued async call has finished. False on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        Task t;
        lock (queueGate) t = tail;
        try { return t.Wait(timeout); }
        catch (AggregateException) { return true; } // a faulted call is still finished
    }

    // ------------------------------------------------------------------ synchronous core
    // These run on the caller's thread right away. Don't mix them with un-awaited async calls on the same key.

    /// <summary>Read a record (with recovery). Missing, deleted or invalid keys read as not found.</summary>
    public SaveRead ReadNow(string key)
    {
        if (!TryPathFor(key, out string path)) return SaveRead.Missing;
        if (!Directory.Exists(root)) return SaveRead.Missing; // never create an account folder by reading
        try
        {
            using (AccountFolderLock.Enter(root, LockTimeout))
            {
                var st = Resolve(path, true);
                return st.Live ? new SaveRead(true, st.Payload, st.Version, st.SavedUtc, st.Source) : SaveRead.Missing;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveStore] Couldn't read '{key}': {e.Message}");
            return SaveRead.Missing;
        }
    }

    public SaveWrite WriteNow(string key, byte[] data, long expectedVersion)
    {
        if (!TryPathFor(key, out string path)) return SaveWrite.Fail(SaveError.Denied);
        if (data == null || expectedVersion < -1)
        {
            Debug.LogWarning($"[SaveStore] Refused a write to '{key}': {(data == null ? "no data" : "bad expected version " + expectedVersion)}.");
            return SaveWrite.Fail(SaveError.Denied);
        }
        if (data.Length > MaxRecordBytes)
        {
            Debug.LogWarning($"[SaveStore] Refused a write to '{key}': {data.Length} bytes is over the {MaxRecordBytes}-byte cap.");
            return SaveWrite.Fail(SaveError.TooLarge);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
            using (AccountFolderLock.Enter(root, LockTimeout))
            {
                var cur = Resolve(path, true);
                long visible = cur.Live ? cur.Version : 0;
                if (expectedVersion >= 0 && expectedVersion != visible)
                    return new SaveWrite(false, visible, SaveError.VersionConflict);
                return Commit(path, StoreRecordBody.Data(data), Math.Max(cur.Version, cur.MaxSeen) + 1);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveStore] Couldn't write '{key}': {e.Message}");
            return SaveWrite.Fail(SaveError.Io);
        }
    }

    /// <summary>Delete a record by writing a tombstone version. False when there was no record (or on error).</summary>
    public bool DeleteNow(string key)
    {
        if (!TryPathFor(key, out string path)) return false;
        if (!Directory.Exists(root)) return false;
        try
        {
            using (AccountFolderLock.Enter(root, LockTimeout))
            {
                var cur = Resolve(path, true);
                if (!cur.Live) return false;
                return Commit(path, StoreRecordBody.Tombstone, Math.Max(cur.Version, cur.MaxSeen) + 1).Ok;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveStore] Couldn't delete '{key}': {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Remove every trace of a key: the record, its temp file and its generations (not quarantined <c>.corrupt-*</c> copies).
    /// For "Recently deleted" expiry; normal deletes use <see cref="DeleteNow"/>.
    /// </summary>
    public bool PurgeNow(string key)
    {
        if (!TryPathFor(key, out string path)) return false;
        if (!Directory.Exists(root)) return false;
        try
        {
            using (AccountFolderLock.Enter(root, LockTimeout))
            {
                bool any = false;
                foreach (string f in new[] { path, path + ".tmp" })
                    if (File.Exists(f)) { File.Delete(f); any = true; }
                string gen = SafeFile.GenDir(path);
                if (Directory.Exists(gen)) { Directory.Delete(gen, true); any = true; }
                return any;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveStore] Couldn't purge '{key}': {e.Message}");
            return false;
        }
    }

    /// <summary>Live records whose key starts with <paramref name="prefix"/> (null/empty = all), sorted by key.</summary>
    public List<SaveKeyInfo> ListNow(string prefix)
    {
        var list = new List<SaveKeyInfo>();
        if (!Directory.Exists(root)) return list;
        prefix = prefix ?? "";
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                if (rel.IndexOf(".gen/", StringComparison.Ordinal) >= 0) continue;
                string key;
                if (rel.EndsWith(Extension, StringComparison.Ordinal)) key = rel.Substring(0, rel.Length - Extension.Length);
                else if (rel.EndsWith(Extension + ".tmp", StringComparison.Ordinal)) key = rel.Substring(0, rel.Length - Extension.Length - 4);
                else continue;
                if (!IsValidKey(key, out _) || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                keys.Add(key);
            }
            foreach (string key in keys)
            {
                if (!TryPathFor(key, out string path)) continue;
                var st = Resolve(path, false);
                if (st.Live) list.Add(new SaveKeyInfo(key, st.Version, st.SavedUtc, st.Payload.Length));
            }
        }
        catch (Exception e) { Debug.LogWarning($"[SaveStore] Couldn't list '{prefix}': {e.Message}"); }
        return list;
    }

    /// <summary>What's on disk for a key, without reading it into a <see cref="SaveRead"/> (tests, tools, the torture check).</summary>
    public readonly struct RecordInfo
    {
        /// <summary>Some copy verified (a record or a tombstone).</summary>
        public readonly bool Any;
        public readonly bool Live;
        public readonly bool Deleted;
        public readonly long Version;
        public readonly DateTime SavedUtc;
        public readonly string Source;
        public readonly byte[] Payload;

        public RecordInfo(bool any, bool live, long version, DateTime savedUtc, string source, byte[] payload)
        {
            Any = any; Live = live; Deleted = any && !live; Version = version; SavedUtc = savedUtc; Source = source; Payload = payload;
        }
    }

    /// <summary>Resolve a key like a read does (including recovery), but also report tombstones.</summary>
    public RecordInfo Inspect(string key)
    {
        if (!TryPathFor(key, out string path) || !Directory.Exists(root)) return default;
        using (AccountFolderLock.Enter(root, LockTimeout))
        {
            var st = Resolve(path, true);
            return new RecordInfo(st.Any, st.Live, st.Version, st.SavedUtc, st.Source, st.Payload);
        }
    }

    /// <summary>A key's generations (tier, version, time, bytes), newest first; damaged copies are skipped.</summary>
    public List<(string tier, long version, DateTime savedUtc, int bytes)> Generations(string key)
    {
        var list = new List<(string tier, long version, DateTime savedUtc, int bytes)>();
        if (!TryPathFor(key, out string path)) return list;
        foreach (var g in SafeFile.Generations(path))
        {
            byte[] bytes = ReadCapped(g.file);
            if (bytes == null || !StoreRecord.TryDecode(bytes, out var rec) || rec.Deleted) continue;
            list.Add((g.tier, rec.Version, rec.SavedUtc, rec.Payload.Length));
        }
        return list;
    }

    /// <summary>One generation's payload (for Restore…: write it back with <see cref="WriteAsync"/> as a new version).</summary>
    public SaveRead ReadGeneration(string key, string tier)
    {
        if (!TryPathFor(key, out string path) || string.IsNullOrEmpty(tier) || tier.IndexOfAny(new[] { '/', '\\' }) >= 0 || tier.Contains(".."))
            return SaveRead.Missing;
        byte[] bytes = ReadCapped(Path.Combine(SafeFile.GenDir(path), tier + Extension));
        if (bytes == null || !StoreRecord.TryDecode(bytes, out var rec) || rec.Deleted) return SaveRead.Missing;
        return new SaveRead(true, rec.Payload, rec.Version, rec.SavedUtc, tier);
    }

    // ------------------------------------------------------------------ keys and paths

    /// <summary>
    /// Lower-case segments of <c>[a-z0-9._-]</c> (each starting with a letter or digit, not ending in '.'), joined by '/',
    /// ≤ <see cref="MaxKeyLength"/> chars and <see cref="MaxKeySegments"/> segments. Names the store uses itself
    /// (<c>.gen</c>, <c>.tmp</c>, <c>.lock</c>, <c>.json</c>, <c>.corrupt-</c>) and Windows device names are refused.
    /// </summary>
    public static bool IsValidKey(string key, out string why)
    {
        why = null;
        if (string.IsNullOrEmpty(key)) { why = "empty key"; return false; }
        if (key.Length > MaxKeyLength) { why = "key too long"; return false; }
        foreach (char c in key)
            if (c >= 'A' && c <= 'Z') { why = "keys are lower-case"; return false; } // Mac and Windows folders ignore case
        string[] parts = key.Split('/');
        if (parts.Length > MaxKeySegments) { why = "too many segments"; return false; }
        foreach (string s in parts)
        {
            if (s.Length == 0) { why = "empty segment"; return false; }
            char c0 = s[0];
            if (!((c0 >= 'a' && c0 <= 'z') || (c0 >= '0' && c0 <= '9'))) { why = "a segment must start with a-z or 0-9"; return false; }
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                { why = $"bad character '{c}'"; return false; }
            if (s[s.Length - 1] == '.') { why = "a segment can't end with '.'"; return false; }
            if (s.EndsWith(".gen", StringComparison.Ordinal) || s.EndsWith(".tmp", StringComparison.Ordinal) ||
                s.EndsWith(".lock", StringComparison.Ordinal) || s.EndsWith(".json", StringComparison.Ordinal) ||
                s.EndsWith(".mutex", StringComparison.Ordinal) || s.Contains(".corrupt-") || s.Contains(".."))
            { why = "reserved name"; return false; }
            int dot = s.IndexOf('.');
            string stem = dot < 0 ? s : s.Substring(0, dot);
            if (IsDeviceName(stem)) { why = "reserved device name"; return false; }
        }
        return true;
    }

    static bool IsDeviceName(string stem)
    {
        switch (stem)
        {
            case "con": case "prn": case "aux": case "nul": return true;
        }
        return stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) &&
               stem[3] >= '1' && stem[3] <= '9';
    }

    /// <summary>The record file for a key, or false (logged) if the key is invalid or would leave the account folder.</summary>
    public bool TryPathFor(string key, out string path)
    {
        path = null;
        if (!IsValidKey(key, out string why))
        {
            Debug.LogWarning($"[SaveStore] Refused key '{key}': {why}.");
            return false;
        }
        string p = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar) + Extension));
        if (!p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Debug.LogWarning($"[SaveStore] Refused key '{key}': outside the account folder.");
            return false;
        }
        path = p;
        return true;
    }

    // ------------------------------------------------------------------ internals

    internal Task<T> Enqueue<T>(CancellationToken ct, Func<T> op)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        Interlocked.Increment(ref pending);
        lock (queueGate)
        {
            Task<T> t = tail.ContinueWith(_ =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested(); // not started yet: safe to skip. A started operation always finishes.
                    return op();
                }
                finally { Interlocked.Decrement(ref pending); }
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            tail = t;
            return t;
        }
    }

    enum StoreRecordBodyKind { Data, Tombstone }

    readonly struct StoreRecordBody
    {
        public readonly StoreRecordBodyKind Kind;
        public readonly byte[] Bytes;
        StoreRecordBody(StoreRecordBodyKind k, byte[] b) { Kind = k; Bytes = b; }
        public static StoreRecordBody Data(byte[] b) => new StoreRecordBody(StoreRecordBodyKind.Data, b);
        public static StoreRecordBody Tombstone => new StoreRecordBody(StoreRecordBodyKind.Tombstone, null);
    }

    SaveWrite Commit(string path, StoreRecordBody body, long version)
    {
        DateTime now = UtcNow;
        byte[] file = body.Kind == StoreRecordBodyKind.Tombstone ? StoreRecord.EncodeTombstone(version, now) : StoreRecord.Encode(version, now, body.Bytes);
        return SafeFile.WriteAtomic(path, file, now, SessionId) ? SaveWrite.Success(version) : SaveWrite.Fail(SaveError.Io);
    }

    struct State
    {
        public bool Any, Live;
        public long Version, MaxSeen;
        public DateTime SavedUtc;
        public byte[] Payload;
        public string Source;

        public void Take(in StoreRecord.Parsed r, string source)
        {
            Any = true;
            Live = !r.Deleted;
            Version = r.Version;
            SavedUtc = r.SavedUtc;
            Payload = r.Deleted ? null : r.Payload;
            Source = source;
            if (r.Version > MaxSeen) MaxSeen = r.Version;
        }
    }

    // Which copy of a record is current. With repair: adopt a finished temp file, keep a damaged main file aside.
    State Resolve(string path, bool repair)
    {
        var st = new State();
        byte[] mainBytes = ReadCapped(path);
        StoreRecord.Parsed main = default;
        bool mainOk = mainBytes != null && StoreRecord.TryDecode(mainBytes, out main);
        if (mainBytes != null) st.MaxSeen = Math.Max(st.MaxSeen, StoreRecord.PeekVersion(mainBytes));

        string tmpPath = path + ".tmp";
        byte[] tmpBytes = ReadCapped(tmpPath);
        StoreRecord.Parsed tmp = default;
        bool tmpOk = tmpBytes != null && StoreRecord.TryDecode(tmpBytes, out tmp);
        if (tmpBytes != null) st.MaxSeen = Math.Max(st.MaxSeen, StoreRecord.PeekVersion(tmpBytes));

        if (tmpOk && (!mainOk || tmp.Version > main.Version))
        {
            // A write that was flushed but never renamed (a crash or kill between the two): it's the newest state. Re-commit it.
            st.Take(tmp, "tmp");
            if (repair)
            {
                if (mainBytes != null && !mainOk) QuarantineOnce(path);
                if (SafeFile.WriteAtomic(path, tmpBytes, UtcNow, SessionId))
                    Debug.Log($"[SaveStore] Recovered an unfinished save of {Path.GetFileName(path)} (version {tmp.Version}).");
            }
            return st;
        }
        if (mainOk)
        {
            st.Take(main, SafeFile.Main);
            return st;
        }

        // The main file is missing or damaged and there is no finished temp file: the highest version that verifies.
        if (mainBytes != null && repair) QuarantineOnce(path);
        bool found = false;
        StoreRecord.Parsed best = default;
        string bestTier = null;
        foreach (var g in SafeFile.Generations(path))
        {
            byte[] bytes = ReadCapped(g.file);
            if (bytes == null) continue;
            st.MaxSeen = Math.Max(st.MaxSeen, StoreRecord.PeekVersion(bytes));
            if (!StoreRecord.TryDecode(bytes, out var rec)) continue;
            if (!found || rec.Version > best.Version) { best = rec; bestTier = g.tier; found = true; }
        }
        if (found)
        {
            long maxSeen = st.MaxSeen;
            st.Take(best, bestTier);
            st.MaxSeen = Math.Max(maxSeen, st.MaxSeen);
            if (mainBytes != null && repair)
                Debug.LogWarning($"[SaveStore] {Path.GetFileName(path)} was damaged; using the {bestTier} backup (version {best.Version}).");
        }
        else if (mainBytes != null && repair)
        {
            Debug.LogError($"[SaveStore] {Path.GetFileName(path)} is damaged and no backup verifies. A copy was kept as *.corrupt-*.json.");
        }
        return st;
    }

    void QuarantineOnce(string path)
    {
        try
        {
            byte[] bad = SafeFile.TryReadAll(path);
            if (bad == null) return;
            string dir = Path.GetDirectoryName(path) ?? root;
            string pattern = Path.GetFileNameWithoutExtension(path) + ".corrupt-*" + Extension;
            foreach (string f in Directory.GetFiles(dir, pattern))
            {
                var fi = new FileInfo(f);
                if (fi.Length != bad.Length) continue;
                byte[] other = SafeFile.TryReadAll(f);
                if (other != null && BytesEqual(other, bad)) return; // already kept
            }
            SafeFile.Quarantine(path, UtcNow);
        }
        catch (Exception e) { Debug.LogWarning($"[SaveStore] Couldn't keep a copy of damaged {Path.GetFileName(path)}: {e.Message}"); }
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static byte[] ReadCapped(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            if (fi.Length > MaxFileBytes)
            {
                Debug.LogWarning($"[SaveStore] Ignored {fi.Name}: {fi.Length} bytes is too big to be a save record.");
                return null;
            }
            return File.ReadAllBytes(file);
        }
        catch (Exception) { return null; }
    }

    static DateTime AsUtc(DateTime t) =>
        t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

/// <summary>
/// The on-disk form of one store record: a small JSON envelope, the payload's length and CRC-32, then the payload. Readable in a
/// text editor; a JSON payload is embedded as-is, anything else as base64.
/// <code>
/// {"rbstore":1,"version":17,"savedUtc":"2026-10-01T05:40:42.891Z","bytes":1234,"crc32":"89abcdef","data":{…payload…}}
/// {"rbstore":1,"version":18,"savedUtc":"…","bytes":5,"crc32":"…","data64":"AAECAwQ="}
/// {"rbstore":1,"version":19,"savedUtc":"…","deleted":true}
/// </code>
/// Decoding checks the header, the exact length and the CRC, so a torn or truncated file never verifies.
/// </summary>
public static class StoreRecord
{
    public const int Format = 1;
    const int MaxHeader = 512;

    public readonly struct Parsed
    {
        public readonly long Version;
        public readonly DateTime SavedUtc;
        public readonly bool Deleted;
        public readonly byte[] Payload;

        public Parsed(long version, DateTime savedUtc, bool deleted, byte[] payload)
        {
            Version = version; SavedUtc = savedUtc; Deleted = deleted; Payload = payload;
        }
    }

    public static byte[] Encode(long version, DateTime savedUtc, byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        bool raw = LooksLikeJson(payload);
        string head = "{\"rbstore\":" + Format + ",\"version\":" + version.ToString(CultureInfo.InvariantCulture) +
                      ",\"savedUtc\":\"" + Time(savedUtc) + "\",\"bytes\":" + payload.Length.ToString(CultureInfo.InvariantCulture) +
                      ",\"crc32\":\"" + Crc32(payload, 0, payload.Length).ToString("x8", CultureInfo.InvariantCulture) + "\"," +
                      (raw ? "\"data\":" : "\"data64\":\"");
        byte[] h = Encoding.ASCII.GetBytes(head);
        byte[] body = raw ? payload : Encoding.ASCII.GetBytes(Convert.ToBase64String(payload));
        byte[] tailBytes = Encoding.ASCII.GetBytes(raw ? "}\n" : "\"}\n");
        var file = new byte[h.Length + body.Length + tailBytes.Length];
        Buffer.BlockCopy(h, 0, file, 0, h.Length);
        Buffer.BlockCopy(body, 0, file, h.Length, body.Length);
        Buffer.BlockCopy(tailBytes, 0, file, h.Length + body.Length, tailBytes.Length);
        return file;
    }

    public static byte[] EncodeTombstone(long version, DateTime savedUtc)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        return Encoding.ASCII.GetBytes("{\"rbstore\":" + Format + ",\"version\":" + version.ToString(CultureInfo.InvariantCulture) +
                                       ",\"savedUtc\":\"" + Time(savedUtc) + "\",\"deleted\":true}\n");
    }

    /// <summary>Verify and unpack a record file. Never throws.</summary>
    public static bool TryDecode(byte[] file, out Parsed record)
    {
        record = default;
        try { return Decode(file, out record); }
        catch (Exception) { return false; }
    }

    /// <summary>The header's version even if the rest is damaged (0 if unreadable), so versions never go backwards.</summary>
    public static long PeekVersion(byte[] file)
    {
        try
        {
            int pos = 0;
            if (file == null || !Expect(file, ref pos, '{')) return 0;
            for (int i = 0; i < 3; i++)
            {
                if (!ReadName(file, ref pos, out string name) || !Expect(file, ref pos, ':')) return 0;
                if (name == "version") return ReadLong(file, ref pos, out long v) ? Math.Max(0, v) : 0;
                if (!SkipScalar(file, ref pos) || !Expect(file, ref pos, ',')) return 0;
            }
        }
        catch (Exception) { /* unreadable */ }
        return 0;
    }

    static bool Decode(byte[] f, out Parsed record)
    {
        record = default;
        if (f == null || f.Length < 16) return false;
        int pos = 0;
        if (!Expect(f, ref pos, '{')) return false;
        long format = -1, version = -1, bytes = -1;
        string saved = null, crc = null;
        bool deleted = false;
        while (pos < Math.Min(f.Length, MaxHeader))
        {
            if (!ReadName(f, ref pos, out string name) || !Expect(f, ref pos, ':')) return false;
            if (name == "data" || name == "data64")
            {
                if (format != Format || version <= 0 || saved == null || crc == null || bytes < 0 || bytes > LocalFileStore.MaxRecordBytes) return false;
                if (!TryParseTime(saved, out DateTime when)) return false;
                if (crc.Length != 8) return false;
                byte[] payload;
                int end;
                if (name == "data")
                {
                    if (pos + bytes + 1 > f.Length) return false;
                    payload = new byte[bytes];
                    Buffer.BlockCopy(f, pos, payload, 0, (int)bytes);
                    end = pos + (int)bytes;
                }
                else
                {
                    if (!Expect(f, ref pos, '"')) return false;
                    int close = Array.IndexOf(f, (byte)'"', pos);
                    if (close < 0) return false;
                    payload = Convert.FromBase64String(Encoding.ASCII.GetString(f, pos, close - pos));
                    if (payload.Length != bytes) return false;
                    end = close + 1;
                }
                if (end >= f.Length || f[end] != (byte)'}') return false;
                if (!OnlyNewline(f, end + 1)) return false;
                if (!string.Equals(Crc32(payload, 0, payload.Length).ToString("x8", CultureInfo.InvariantCulture), crc, StringComparison.Ordinal))
                    return false; // exact text: a flipped letter case in the header must not verify
                record = new Parsed(version, when, false, payload);
                return true;
            }
            switch (name)
            {
                case "rbstore": if (!ReadLong(f, ref pos, out format)) return false; break;
                case "version": if (!ReadLong(f, ref pos, out version)) return false; break;
                case "bytes": if (!ReadLong(f, ref pos, out bytes)) return false; break;
                case "savedUtc": if (!ReadString(f, ref pos, out saved)) return false; break;
                case "crc32": if (!ReadString(f, ref pos, out crc)) return false; break;
                case "deleted": if (!ReadBool(f, ref pos, out deleted)) return false; break;
                default: if (!SkipScalar(f, ref pos)) return false; break; // a newer writer's header field
            }
            if (pos >= f.Length) return false;
            if (f[pos] == (byte)',') { pos++; continue; }
            if (f[pos] != (byte)'}') return false;
            // End of a header-only record: only a tombstone may end here.
            if (!deleted || format != Format || version <= 0 || saved == null || !TryParseTime(saved, out DateTime at)) return false;
            if (!OnlyNewline(f, pos + 1)) return false;
            record = new Parsed(version, at, true, null);
            return true;
        }
        return false;
    }

    static bool OnlyNewline(byte[] f, int from)
    {
        int rest = f.Length - from;
        if (rest == 0) return true;
        if (rest == 1) return f[from] == (byte)'\n';
        return rest == 2 && f[from] == (byte)'\r' && f[from + 1] == (byte)'\n';
    }

    static bool Expect(byte[] f, ref int pos, char c)
    {
        if (pos >= f.Length || f[pos] != (byte)c) return false;
        pos++;
        return true;
    }

    static bool ReadName(byte[] f, ref int pos, out string name) => ReadString(f, ref pos, out name);

    static bool ReadString(byte[] f, ref int pos, out string s)
    {
        s = null;
        if (!Expect(f, ref pos, '"')) return false;
        int start = pos;
        while (pos < f.Length && pos - start < 64)
        {
            byte b = f[pos];
            if (b == (byte)'"') { s = Encoding.ASCII.GetString(f, start, pos - start); pos++; return true; }
            if (b == (byte)'\\' || b < 0x20 || b > 0x7e) return false;
            pos++;
        }
        return false;
    }

    static bool ReadLong(byte[] f, ref int pos, out long v)
    {
        v = 0;
        int start = pos;
        if (pos < f.Length && f[pos] == (byte)'-') pos++;
        while (pos < f.Length && f[pos] >= (byte)'0' && f[pos] <= (byte)'9' && pos - start < 20) pos++;
        return pos > start && long.TryParse(Encoding.ASCII.GetString(f, start, pos - start), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out v);
    }

    static bool ReadBool(byte[] f, ref int pos, out bool v)
    {
        v = false;
        if (Match(f, pos, "true")) { pos += 4; v = true; return true; }
        if (Match(f, pos, "false")) { pos += 5; return true; }
        return false;
    }

    static bool SkipScalar(byte[] f, ref int pos)
    {
        if (pos >= f.Length) return false;
        if (f[pos] == (byte)'"') return ReadString(f, ref pos, out _);
        if (f[pos] == (byte)'t' || f[pos] == (byte)'f') return ReadBool(f, ref pos, out _);
        return ReadLong(f, ref pos, out _);
    }

    static bool Match(byte[] f, int pos, string s)
    {
        if (pos + s.Length > f.Length) return false;
        for (int i = 0; i < s.Length; i++) if (f[pos + i] != (byte)s[i]) return false;
        return true;
    }

    static string Time(DateTime t)
    {
        t = t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    static bool TryParseTime(string s, out DateTime t) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);

    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>UTF-8 text that starts with '{' or '[' and ends with '}' or ']' (no BOM) is embedded as-is.</summary>
    public static bool LooksLikeJson(byte[] p)
    {
        if (p == null || p.Length < 2) return false;
        if (p.Length >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF) return false;
        int a = 0, b = p.Length - 1;
        while (a <= b && IsWs(p[a])) a++;
        while (b >= a && IsWs(p[b])) b--;
        if (a >= b) return false;
        bool open = p[a] == (byte)'{' || p[a] == (byte)'[';
        bool close = p[b] == (byte)'}' || p[b] == (byte)']';
        if (!open || !close) return false;
        try { StrictUtf8.GetCharCount(p); }
        catch (Exception) { return false; }
        return Array.IndexOf(p, (byte)0) < 0;
    }

    static bool IsWs(byte c) => c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n';

    static uint[] crcTable;

    /// <summary>CRC-32 (IEEE 802.3, the zip/PNG one).</summary>
    public static uint Crc32(byte[] data, int offset, int count)
    {
        var table = crcTable;
        if (table == null)
        {
            table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            crcTable = table;
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// Mutual exclusion for one account folder, within this process (a re-entrant monitor) and across processes (an OS byte-range
/// lock on <c>&lt;account&gt;/account.mutex</c>, which the kernel drops if the holder dies, so it can never go stale). Held only
/// for the length of one store or lease operation. Never <c>await</c> while holding it.
/// </summary>
public static class AccountFolderLock
{
    public const string FileName = "account.mutex";

    sealed class Slot
    {
        public readonly object Sync = new object();
        public int Depth;
        public FileStream File;
    }

    static readonly ConcurrentDictionary<string, Slot> slots = new ConcurrentDictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
    static int osLockUnsupported;

    /// <summary>Enter (re-entrant on the same thread). Throws <see cref="TimeoutException"/> if another holder doesn't let go.</summary>
    public static IDisposable Enter(string accountDir, TimeSpan timeout)
    {
        string full = Path.GetFullPath(accountDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var slot = slots.GetOrAdd(full, _ => new Slot());
        DateTime deadline = DateTime.UtcNow + timeout;
        if (!Monitor.TryEnter(slot.Sync, timeout)) throw new TimeoutException($"account folder busy in this process: {full}");
        try
        {
            if (slot.Depth == 0) slot.File = AcquireOsLock(full, deadline);
            slot.Depth++;
            return new Releaser(slot);
        }
        catch
        {
            Monitor.Exit(slot.Sync);
            throw;
        }
    }

    static FileStream AcquireOsLock(string dir, DateTime deadline)
    {
        Directory.CreateDirectory(dir);
        var fs = new FileStream(Path.Combine(dir, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        while (true)
        {
            try
            {
                fs.Lock(0, 1);
                return fs;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    fs.Dispose();
                    throw new TimeoutException($"another process is holding {Path.Combine(dir, FileName)}");
                }
                Thread.Sleep(5);
            }
            catch (PlatformNotSupportedException)
            {
                if (Interlocked.Exchange(ref osLockUnsupported, 1) == 0)
                    Debug.LogWarning("[SaveStore] File locks aren't supported here; only this process is serialised.");
                return fs;
            }
        }
    }

    sealed class Releaser : IDisposable
    {
        Slot slot;
        public Releaser(Slot s) { slot = s; }

        public void Dispose()
        {
            var s = slot;
            if (s == null) return;
            slot = null;
            try
            {
                if (--s.Depth == 0 && s.File != null)
                {
                    try { s.File.Unlock(0, 1); } catch (Exception) { /* closing releases it anyway */ }
                    s.File.Dispose();
                    s.File = null;
                }
            }
            finally { Monitor.Exit(s.Sync); }
        }
    }
}

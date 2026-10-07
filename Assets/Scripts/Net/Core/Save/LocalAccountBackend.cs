using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The local <see cref="IAccountBackend"/> (Docs/Multiplayer.md §8.7.4 B4; Docs/Accounts.md §5.1, §5.3, §6.4, §8, §9). One
/// account = one folder, <c>&lt;data root&gt;/accounts/&lt;PlayerKey.FolderName&gt;/</c>, holding a <see cref="LocalFileStore"/>.
/// A company checkout is a lock file, <c>companies/&lt;guid&gt;.lock</c> (<see cref="SaveKeys.Lock"/>):
/// <list type="bullet">
/// <item>It records the <b>epoch</b>, the session id, the holder's <b>process id and start time</b>, the play-session run id,
/// and the Town. Every new checkout and every take-back gets a higher epoch; a commit must carry the current epoch and
/// session, so the old session's commits fail with <see cref="SaveError.StaleEpoch"/> after a take-back.</item>
/// <item><b>Liveness, not expiry</b> (Accounts.md §0.1): a lock whose process is gone (or whose PID now belongs to a process that
/// started at another time) is free at once. In the Editor, a lock left by an earlier play session of the same Editor process
/// is free too (<see cref="NewRun"/>). Local locks never expire on a timer.</item>
/// <item>The same process and session checking out again <b>renews</b> (same epoch). Anyone else is refused with
/// <see cref="CompanyLease.HeldBy"/> and the Town it's "on the road" in.</item>
/// <item>Check-in releases the lock but keeps the epoch, so a late commit from the same session is still accepted (§6.4).</item>
/// <item><b><c>.prev</c>:</b> a checkout into a different Town than the last one first copies the company to
/// <see cref="SaveKeys.CompanyPrev"/> ("from before visiting X", §9). <see cref="RestorePreviousAsync"/> swaps the two (so it's
/// undoable) and bumps the epoch; it's refused while the company is on the road.</item>
/// </list>
/// Every lock change happens under <see cref="AccountFolderLock"/> and is written atomically (temp → flush → rename).
/// Async calls share the store's ordered background queue.
/// </summary>
public sealed class LocalAccountBackend : IAccountBackend
{
    public const string AccountsFolder = "accounts";
    /// <summary>Local locks don't expire; they end with their process.</summary>
    public static readonly DateTime NeverExpires = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    readonly LocalFileStore store;
    readonly PlayerKey key;
    AccountState state = AccountState.Offline;

    /// <param name="accountDir">The account folder (see <see cref="AccountFolder"/>). Created on the first write.</param>
    /// <param name="key">Whose account this is; <see cref="SignInAsync"/> only accepts this identity.</param>
    /// <param name="utcNow">Clock for records and generations (tests pass a fake one).</param>
    /// <param name="playSessionId">Play-session id: the default lease session and the store's <c>session</c> tier.</param>
    public LocalAccountBackend(string accountDir, PlayerKey key, Func<DateTime> utcNow = null, string playSessionId = null)
    {
        if (!key.IsValid) throw new ArgumentException("invalid player key", nameof(key));
        this.key = key;
        store = new LocalFileStore(accountDir, utcNow, playSessionId);
    }

    /// <summary>A backend for <paramref name="key"/>'s folder under <paramref name="dataRoot"/>.</summary>
    public static LocalAccountBackend ForPlayer(string dataRoot, PlayerKey key, Func<DateTime> utcNow = null, string playSessionId = null) =>
        new LocalAccountBackend(AccountFolder(dataRoot, key), key, utcNow, playSessionId);

    /// <summary><c>&lt;dataRoot&gt;/accounts</c></summary>
    public static string AccountsRoot(string dataRoot)
    {
        if (string.IsNullOrEmpty(dataRoot)) throw new ArgumentException("data root is empty", nameof(dataRoot));
        return Path.Combine(Path.GetFullPath(dataRoot), AccountsFolder);
    }

    /// <summary><c>&lt;dataRoot&gt;/accounts/&lt;key.FolderName&gt;</c> (<c>steam_7656…</c> | <c>dev_editor</c>).</summary>
    public static string AccountFolder(string dataRoot, PlayerKey key)
    {
        if (!key.IsValid) throw new ArgumentException("invalid player key", nameof(key));
        string folder = key.FolderName;
        if (folder.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || folder == "." || folder == "..")
            throw new ArgumentException($"player key '{key}' doesn't make a safe folder name", nameof(key));
        return Path.Combine(AccountsRoot(dataRoot), folder);
    }

    // ------------------------------------------------------------------ IAccountBackend

    public AccountState State => state;
    /// <summary>The PlayerKey (<c>dev:editor</c>, <c>steam:7656…</c>). The cloud backend uses the UGS player id instead.</summary>
    public string AccountId => key.Value;
    public bool IsCloud => false;
    public event Action<AccountState> StateChanged;
    public ISaveStore Store => store;

    /// <summary>The store with its local extras (generations, inspect, purge).</summary>
    public LocalFileStore Files => store;
    public PlayerKey Key => key;

    /// <summary>The default lease session (and the store's play session). Set it once per play session.</summary>
    public string SessionId
    {
        get => store.SessionId;
        set => store.SessionId = value;
    }

    /// <summary>Local sign-in: accepts the identity this account belongs to. Nothing to reach, so it never waits.</summary>
    public Task<bool> SignInAsync(IPlayerIdentity id, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<bool>(ct);
        if (id == null || !id.Key.IsValid || id.Key != key)
        {
            Debug.LogWarning($"[Accounts] Sign-in refused: this is {key}'s account, not {(id == null ? "(no identity)" : id.Key.ToString())}'s.");
            SetState(AccountState.Failed);
            return Task.FromResult(false);
        }
        SetState(AccountState.SigningIn);
        SetState(AccountState.SignedIn);
        return Task.FromResult(true);
    }

    void SetState(AccountState s)
    {
        if (state == s) return;
        state = s;
        try { StateChanged?.Invoke(s); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public Task<CompanyLease> CheckoutCompanyAsync(string companyGuid, string sessionId, string townName, CancellationToken ct = default) =>
        store.Enqueue(ct, () => CheckoutNow(companyGuid, sessionId, townName));

    public Task<SaveWrite> CommitCompanyAsync(CompanyLease lease, byte[] blob, long baseVersion, CancellationToken ct = default)
    {
        byte[] copy = blob == null ? null : (byte[])blob.Clone();
        return store.Enqueue(ct, () => CommitNow(lease, copy, baseVersion));
    }

    public Task CheckinCompanyAsync(CompanyLease lease, CancellationToken ct = default) =>
        store.Enqueue(ct, () => CheckinNow(lease));

    /// <summary>Take the company from whoever holds it (alive or not) for this backend's <see cref="SessionId"/>.</summary>
    public Task<CompanyLease> TakeBackAsync(string companyGuid, CancellationToken ct = default) => TakeBackAsync(companyGuid, null, null, ct);

    /// <summary>Take-back for a given session and Town (null = <see cref="SessionId"/>, keep the last Town).</summary>
    public Task<CompanyLease> TakeBackAsync(string companyGuid, string sessionId, string townName, CancellationToken ct = default) =>
        store.Enqueue(ct, () => TakeBackNow(companyGuid, sessionId, townName));

    public Task<bool> RestorePreviousAsync(string companyGuid, CancellationToken ct = default) =>
        store.Enqueue(ct, () => RestorePreviousNow(companyGuid));

    // ------------------------------------------------------------------ synchronous core (callers: the async API, tools, the torture check)

    public CompanyLease CheckoutNow(string companyGuid, string sessionId, string townName)
    {
        sessionId = string.IsNullOrEmpty(sessionId) ? SessionId : Clean(sessionId);
        if (!ValidGuid(companyGuid)) return Refused(companyGuid, sessionId, "invalid company id");
        townName = Clean(townName);
        try
        {
            using (AccountFolderLock.Enter(store.Scope, LocalFileStore.LockTimeout))
            {
                string path = LockPath(companyGuid);
                var cur = ReadLease(path);
                if (cur != null && HolderAlive(cur))
                {
                    if (IsSelf(cur) && cur.Session == sessionId)
                    {
                        if (townName != null) cur.Town = townName;
                        WriteLease(path, cur);
                        return Granted(companyGuid, cur);
                    }
                    return new CompanyLease(false, companyGuid, cur.Session, Describe(cur), cur.Town, cur.Epoch, NeverExpires);
                }
                if (cur != null && cur.Held)
                    Debug.Log($"[Accounts] {companyGuid}: its last holder ({Describe(cur)}) is gone, so the lock is free.");
                var next = NewHeld(cur, sessionId, townName ?? cur?.Town);
                if (cur == null || !string.Equals(cur.Town, townName, StringComparison.Ordinal)) KeepPrev(companyGuid, next);
                WriteLease(path, next);
                return Granted(companyGuid, next);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Accounts] Couldn't check out {companyGuid}: {e.Message}");
            return Refused(companyGuid, sessionId, "the lock couldn't be taken (" + e.Message + ")");
        }
    }

    public SaveWrite CommitNow(CompanyLease lease, byte[] blob, long baseVersion)
    {
        if (!lease.Granted || !ValidGuid(lease.CompanyGuid) || blob == null) return SaveWrite.Fail(SaveError.Denied);
        if (blob.Length > LocalFileStore.MaxRecordBytes) return SaveWrite.Fail(SaveError.TooLarge);
        try
        {
            using (AccountFolderLock.Enter(store.Scope, LocalFileStore.LockTimeout))
            {
                var cur = ReadLease(LockPath(lease.CompanyGuid));
                if (cur == null || cur.Epoch != lease.Epoch || cur.Session != lease.SessionId)
                {
                    Debug.LogWarning($"[Accounts] Commit of {lease.CompanyGuid} refused: epoch {lease.Epoch} is stale " +
                                     $"(now {(cur == null ? "no lock" : cur.Epoch.ToString(CultureInfo.InvariantCulture))}). It was taken back or restored.");
                    return SaveWrite.Fail(SaveError.StaleEpoch);
                }
                return store.WriteNow(SaveKeys.Company(lease.CompanyGuid), blob, baseVersion);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Accounts] Couldn't commit {lease.CompanyGuid}: {e.Message}");
            return SaveWrite.Fail(SaveError.Io);
        }
    }

    /// <summary>Release a lease. Ignored (logged) when it's no longer the current one.</summary>
    public bool CheckinNow(CompanyLease lease)
    {
        if (!lease.Granted || !ValidGuid(lease.CompanyGuid)) return false;
        try
        {
            using (AccountFolderLock.Enter(store.Scope, LocalFileStore.LockTimeout))
            {
                string path = LockPath(lease.CompanyGuid);
                var cur = ReadLease(path);
                if (cur == null || !cur.Held || cur.Epoch != lease.Epoch || cur.Session != lease.SessionId)
                {
                    Debug.Log($"[Accounts] Check-in of {lease.CompanyGuid} (epoch {lease.Epoch}) ignored: that lease isn't the current one.");
                    return false;
                }
                cur.Held = false;
                cur.ReleasedUtc = store.UtcNow;
                WriteLease(path, cur);
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Accounts] Couldn't check in {lease.CompanyGuid}: {e.Message}");
            return false;
        }
    }

    public CompanyLease TakeBackNow(string companyGuid, string sessionId, string townName)
    {
        sessionId = string.IsNullOrEmpty(sessionId) ? SessionId : Clean(sessionId);
        if (!ValidGuid(companyGuid)) return Refused(companyGuid, sessionId, "invalid company id");
        try
        {
            using (AccountFolderLock.Enter(store.Scope, LocalFileStore.LockTimeout))
            {
                string path = LockPath(companyGuid);
                var cur = ReadLease(path);
                var next = NewHeld(cur, sessionId, Clean(townName) ?? cur?.Town);
                WriteLease(path, next);
                if (cur != null && cur.Held && HolderAlive(cur))
                    Debug.Log($"[Accounts] {companyGuid} taken back from {Describe(cur)} (epoch {cur.Epoch} → {next.Epoch}).");
                return Granted(companyGuid, next);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Accounts] Couldn't take back {companyGuid}: {e.Message}");
            return Refused(companyGuid, sessionId, "the lock couldn't be taken (" + e.Message + ")");
        }
    }

    /// <summary>Swap the company with its <c>.prev</c> copy and bump the epoch. False while on the road or with no <c>.prev</c>.</summary>
    public bool RestorePreviousNow(string companyGuid)
    {
        if (!ValidGuid(companyGuid)) return false;
        try
        {
            using (AccountFolderLock.Enter(store.Scope, LocalFileStore.LockTimeout))
            {
                string path = LockPath(companyGuid);
                var cur = ReadLease(path);
                if (cur != null && HolderAlive(cur))
                {
                    Debug.LogWarning($"[Accounts] Restore of {companyGuid} refused: it's on the road ({Describe(cur)}).");
                    return false;
                }
                var prev = store.ReadNow(SaveKeys.CompanyPrev(companyGuid));
                if (!prev.Found) return false;
                var now = store.ReadNow(SaveKeys.Company(companyGuid));
                if (!store.WriteNow(SaveKeys.Company(companyGuid), prev.Data, -1).Ok) return false;
                if (now.Found) store.WriteNow(SaveKeys.CompanyPrev(companyGuid), now.Data, -1);
                var next = cur ?? new LeaseFile();
                next.Epoch = (cur?.Epoch ?? 0) + 1; // whatever session held the old epoch can't commit over the restore
                next.Held = false;
                next.ReleasedUtc = store.UtcNow;
                WriteLease(path, next);
                Debug.Log($"[Accounts] {companyGuid} restored from its previous copy (version {prev.Version}); the replaced copy is the new .prev.");
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Accounts] Couldn't restore {companyGuid}: {e.Message}");
            return false;
        }
    }

    /// <summary>The lock as it stands, for company cards ("ON THE ROAD: Maple Hollow") and tests. Never changes anything.</summary>
    public readonly struct LeaseInfo
    {
        public readonly bool Exists, Held, HolderAlive, IsThisProcess;
        public readonly long Epoch;
        public readonly string SessionId, TownName, HeldBy, PrevTown;
        public readonly int Pid;
        public readonly DateTime SinceUtc, PrevUtc;

        public LeaseInfo(bool exists, bool held, bool alive, bool self, long epoch, string session, string town, string heldBy,
            int pid, DateTime since, string prevTown, DateTime prevUtc)
        {
            Exists = exists; Held = held; HolderAlive = alive; IsThisProcess = self; Epoch = epoch; SessionId = session; TownName = town;
            HeldBy = heldBy; Pid = pid; SinceUtc = since; PrevTown = prevTown; PrevUtc = prevUtc;
        }

        /// <summary>Checked out by a live session somewhere (the "on the road" badge).</summary>
        public bool OnTheRoad => Held && HolderAlive;
    }

    public LeaseInfo Inspect(string companyGuid)
    {
        if (!ValidGuid(companyGuid)) return default;
        var cur = ReadLease(LockPath(companyGuid));
        if (cur == null) return default;
        bool alive = HolderAlive(cur);
        return new LeaseInfo(true, cur.Held, alive, IsSelf(cur), cur.Epoch, cur.Session, cur.Town, cur.Held ? Describe(cur) : null,
            cur.Pid, cur.SinceUtc, cur.PrevTown, cur.PrevUtc);
    }

    // ------------------------------------------------------------------ this process (lock liveness)

    /// <summary>
    /// Identifies this play session inside this process. A lock written by this process under another run id belongs to a play
    /// session that has ended (the Editor keeps one process across many), so it's free. <c>AccountService</c> calls
    /// <see cref="NewRun"/> at the start of every play session.
    /// </summary>
    public static string RunId { get; private set; } = NewRunId();

    public static void NewRun() => RunId = NewRunId();

    static int runCounter;

    static string NewRunId() =>
        DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" + Self.Pid.ToString(CultureInfo.InvariantCulture) + "-" +
        Interlocked.Increment(ref runCounter).ToString(CultureInfo.InvariantCulture);

    static class Self
    {
        public static readonly int Pid;
        public static readonly DateTime StartUtc;
        public static readonly string ProcessName = "?", Machine = "?";

        static Self()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    Pid = p.Id;
                    try { StartUtc = p.StartTime.ToUniversalTime(); } catch (Exception) { /* left default: nobody can prove we're alive */ }
                    try { ProcessName = Clean(p.ProcessName) ?? "?"; } catch (Exception) { /* cosmetic */ }
                }
            }
            catch (Exception) { /* sandboxed */ }
            try { Machine = Clean(Environment.MachineName) ?? "?"; } catch (Exception) { /* cosmetic */ }
        }
    }

    static bool SameStart(DateTime a, DateTime b) => a != default && b != default && (a - b).Duration() <= StartTolerance;

    static bool IsSelf(LeaseFile l) => l.Pid == Self.Pid && SameStart(l.StartUtc, Self.StartUtc) && l.Run == RunId;

    /// <summary>Is the process that holds this lock still running (and still the same process)?</summary>
    static bool HolderAlive(LeaseFile l)
    {
        if (!l.Held || l.Pid <= 0) return false;
        if (l.Pid == Self.Pid && SameStart(l.StartUtc, Self.StartUtc)) return l.Run == RunId; // an earlier play session here has ended
        try
        {
            using (var p = System.Diagnostics.Process.GetProcessById(l.Pid))
            {
                DateTime started;
                try { started = p.StartTime.ToUniversalTime(); }
                catch (Exception) { return false; } // no access: a system process reusing the PID, not a game we could have started
                return SameStart(started, l.StartUtc);
            }
        }
        catch (Exception) { return false; } // no such process
    }

    static string Describe(LeaseFile l)
    {
        string who = $"{l.Process ?? "?"} (pid {l.Pid.ToString(CultureInfo.InvariantCulture)}) on {l.Machine ?? "?"}";
        if (l.Pid == Self.Pid && SameStart(l.StartUtc, Self.StartUtc)) who += l.Run == RunId ? ", this game, session " + l.Session : ", an earlier play session";
        return string.IsNullOrEmpty(l.Town) ? who : who + ", in " + l.Town;
    }

    LeaseFile NewHeld(LeaseFile cur, string sessionId, string town) => new LeaseFile
    {
        Epoch = (cur?.Epoch ?? 0) + 1,
        Held = true,
        Session = sessionId,
        Pid = Self.Pid,
        StartUtc = Self.StartUtc,
        Run = RunId,
        Process = Self.ProcessName,
        Machine = Self.Machine,
        Town = town,
        SinceUtc = store.UtcNow,
        PrevTown = cur?.PrevTown,
        PrevUtc = cur?.PrevUtc ?? default,
    };

    void KeepPrev(string companyGuid, LeaseFile next)
    {
        var cur = store.ReadNow(SaveKeys.Company(companyGuid));
        if (!cur.Found) return;
        if (store.WriteNow(SaveKeys.CompanyPrev(companyGuid), cur.Data, -1).Ok)
        {
            next.PrevTown = next.Town;
            next.PrevUtc = store.UtcNow;
        }
    }

    static CompanyLease Granted(string g, LeaseFile l) => new CompanyLease(true, g, l.Session, null, l.Town, l.Epoch, NeverExpires);

    static CompanyLease Refused(string g, string sessionId, string why) => new CompanyLease(false, g, sessionId, why, null, 0, default);

    bool ValidGuid(string g)
    {
        string why = "not [A-Za-z0-9-], 1-64 chars";
        if (SaveKeys.IsValidGuid(g) && LocalFileStore.IsValidKey("companies/" + g, out why)) return true;
        Debug.LogWarning($"[Accounts] Refused company id '{g}': {why}.");
        return false;
    }

    string LockPath(string g) => Path.Combine(store.Scope, SaveKeys.Lock(g).Replace('/', Path.DirectorySeparatorChar));

    static string Clean(string s)
    {
        if (s == null) return null;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
        string t = sb.ToString().Trim();
        if (t.Length > 120) t = t.Substring(0, 120);
        return t.Length == 0 ? null : t;
    }

    // ------------------------------------------------------------------ the lock file

    sealed class LeaseFile
    {
        const string Magic = "rubbish-lease 1";

        public long Epoch;
        public bool Held;
        public string Session, Run, Process, Machine, Town, PrevTown;
        public int Pid;
        public DateTime StartUtc, SinceUtc, ReleasedUtc, PrevUtc;

        public string Format()
        {
            var sb = new StringBuilder(256);
            sb.Append(Magic).Append('\n');
            Line(sb, "epoch", Epoch.ToString(CultureInfo.InvariantCulture));
            Line(sb, "state", Held ? "held" : "released");
            Line(sb, "session", Session);
            Line(sb, "pid", Pid.ToString(CultureInfo.InvariantCulture));
            Line(sb, "start", T(StartUtc));
            Line(sb, "run", Run);
            Line(sb, "process", Process);
            Line(sb, "machine", Machine);
            Line(sb, "town", Town);
            Line(sb, "since", T(SinceUtc));
            Line(sb, "released", T(ReleasedUtc));
            Line(sb, "prevTown", PrevTown);
            Line(sb, "prevUtc", T(PrevUtc));
            return sb.ToString();
        }

        static void Line(StringBuilder sb, string k, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            sb.Append(k).Append('=').Append(Clean(v)).Append('\n');
        }

        static string T(DateTime t) => t == default ? null : t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        static DateTime P(string s) =>
            s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToUniversalTime() : default;

        public static LeaseFile Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] lines = text.Replace("\r", "").Split('\n');
            if (lines.Length < 3 || lines[0] != Magic) return null;
            var l = new LeaseFile();
            bool haveEpoch = false, haveState = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) return null;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                switch (k)
                {
                    case "epoch": haveEpoch = long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out l.Epoch); break;
                    case "state": haveState = v == "held" || v == "released"; l.Held = v == "held"; break;
                    case "session": l.Session = v; break;
                    case "pid": int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out l.Pid); break;
                    case "start": l.StartUtc = P(v); break;
                    case "run": l.Run = v; break;
                    case "process": l.Process = v; break;
                    case "machine": l.Machine = v; break;
                    case "town": l.Town = v; break;
                    case "since": l.SinceUtc = P(v); break;
                    case "released": l.ReleasedUtc = P(v); break;
                    case "prevTown": l.PrevTown = v; break;
                    case "prevUtc": l.PrevUtc = P(v); break;
                    default: break; // a newer writer's field
                }
            }
            return haveEpoch && haveState ? l : null;
        }
    }

    static LeaseFile ReadLease(string path)
    {
        string text = TryReadText(path);
        var l = LeaseFile.Parse(text);
        if (l != null) return l;
        // The atomic replace can fall back to delete + move; a crash in between leaves only the finished temp file.
        var t = LeaseFile.Parse(TryReadText(path + ".tmp"));
        if (t != null) return t;
        if (text == null) return null;
        // Unreadable (shouldn't happen: lock files are only ever replaced whole). Free, keeping the epoch if it can be read.
        Debug.LogWarning($"[Accounts] {Path.GetFileName(path)} is unreadable; treating the company as not checked out.");
        long epoch = 0;
        int at = text.IndexOf("epoch=", StringComparison.Ordinal);
        if (at >= 0)
        {
            int end = at + 6;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            long.TryParse(text.Substring(at + 6, end - at - 6), NumberStyles.None, CultureInfo.InvariantCulture, out epoch);
        }
        return new LeaseFile { Epoch = epoch, Held = false };
    }

    static string TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch (Exception) { return null; }
    }

    static void WriteLease(string path, LeaseFile l)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        byte[] bytes = new UTF8Encoding(false).GetBytes(l.Format());
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try { File.Replace(tmp, path, null); }
        catch (Exception)
        {
            File.Delete(path); // a crash here leaves the finished temp file, which ReadLease accepts
            File.Move(tmp, path);
        }
    }
}

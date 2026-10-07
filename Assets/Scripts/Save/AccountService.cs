using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The local player's account: which backend holds their saves, and where (Docs/Multiplayer.md §8.7.4 B4, D8;
/// Docs/Accounts.md §1.2, §5.1, §5.3). The account lives in <c>&lt;SaveRoot.Path&gt;/accounts/&lt;PlayerKey.FolderName&gt;/</c>
/// (<c>steam_7656…</c>, <c>dev_editor</c>, <c>dev_bot-1</c>) for <see cref="PlayerIdentity.Local"/>, so the folder follows the
/// build's fixed identity, never whether Steam happens to be running.
/// <list type="bullet">
/// <item>M0/M1: always a <see cref="LocalAccountBackend"/>. M1.5 registers <c>UgsAccountBackend</c> with
/// <see cref="RegisterBackend"/>; the highest-priority factory that returns a backend wins, else local.</item>
/// <item>Opened lazily on first use and kept for the play session; reopened if the data root changes (a test root pushed or
/// popped). Every play session gets a new <see cref="SessionId"/> and a new lock run (<see cref="LocalAccountBackend.NewRun"/>),
/// so a lock left by an earlier play session in the same Editor process is free.</item>
/// <item>Never opens an account in edit mode unless a test data root is pushed (<see cref="SaveRoot.PushTestOverride"/>):
/// an eval or editor tool must not create folders in the real saves.</item>
/// </list>
/// The stores on top (TownStore, CompanyStore, ProfileStore, SaveSlots: B8) use <see cref="Backend"/>; nothing else should write
/// account files.
/// </summary>
public static class AccountService
{
    struct Factory
    {
        public string name;
        public int priority;
        public Func<PlayerKey, string, IAccountBackend> make;
    }

    static readonly List<Factory> factories = new List<Factory>();
    static IAccountBackend backend;
    static PlayerKey key;
    static string openedRoot;
    static string sessionId;
    static string lastWarning;

    /// <summary><c>&lt;data root&gt;/accounts</c></summary>
    public static string AccountsRoot => LocalAccountBackend.AccountsRoot(SaveRoot.Path);

    /// <summary><c>&lt;data root&gt;/accounts/&lt;key.FolderName&gt;</c>. Throws on an invalid key.</summary>
    public static string FolderFor(PlayerKey k) => LocalAccountBackend.AccountFolder(SaveRoot.Path, k);

    /// <summary>This play session's id: the default lease session and the store's <c>session</c> backup tier.</summary>
    public static string SessionId => sessionId ??= SafeFile.NewSessionId(DateTime.UtcNow);

    /// <summary>The open account, opened on first use; null with no identity yet, or in edit mode without a test data root.</summary>
    public static IAccountBackend Backend => TryGet(out var b) ? b : null;

    /// <summary>The open account's store, or null.</summary>
    public static ISaveStore Store => Backend?.Store;

    /// <summary>Whose account is open (default when none).</summary>
    public static PlayerKey Key => backend != null ? key : default;

    public static bool TryGet(out IAccountBackend b)
    {
        string root = SaveRoot.Path;
        if (backend != null && string.Equals(openedRoot, root, StringComparison.Ordinal))
        {
            b = backend;
            return true;
        }
        backend = null;
        b = null;
        if (!Application.isPlaying && !SaveRoot.IsTestOverride)
        {
            WarnOnce("edit mode without a test data root: no account is opened (it would be the real one)");
            return false;
        }
        var id = PlayerIdentity.Local;
        if (id == null || !id.Key.IsValid)
        {
            WarnOnce("no player identity yet, so no account");
            return false;
        }
        try
        {
            b = Create(id.Key);
        }
        catch (Exception e)
        {
            WarnOnce($"couldn't open {id.Key}'s account: {e.Message}");
            return false;
        }
        backend = b;
        key = id.Key;
        openedRoot = root;
        Debug.Log($"[Accounts] {id.Key}: {(b.IsCloud ? "cloud" : "local")} account in {FolderFor(id.Key)}" +
                  (SaveRoot.IsOverridden ? " (TEST DATA ROOT)" : ""));
        return true;
    }

    /// <summary>A backend for <paramref name="k"/> (the registered factories first, else local). Doesn't open it as current.</summary>
    public static IAccountBackend Create(PlayerKey k)
    {
        string dir = FolderFor(k);
        var ordered = new List<Factory>(factories);
        ordered.Sort((x, y) => y.priority.CompareTo(x.priority));
        foreach (var f in ordered)
        {
            IAccountBackend made = null;
            try { made = f.make(k, dir); }
            catch (Exception e) { Debug.LogWarning($"[Accounts] Backend '{f.name}' failed: {e.Message}. Trying the next one."); }
            if (made != null) return made;
        }
        return new LocalAccountBackend(dir, k, null, SessionId);
    }

    /// <summary>
    /// Offer another backend (M1.5: UgsAccountBackend, write-through over a local cache in the given folder). Return null from the
    /// factory to decline (e.g. a <c>dev:</c> key, or no Steam). Re-registering a name replaces it.
    /// </summary>
    public static void RegisterBackend(string name, Func<PlayerKey, string, IAccountBackend> factory, int priority)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        factories.RemoveAll(f => f.name == name);
        factories.Add(new Factory { name = name, priority = priority, make = factory });
    }

    /// <summary>Sign the open account in with the local identity. False with no account.</summary>
    public static Task<bool> SignInAsync(CancellationToken ct = default)
    {
        if (!TryGet(out var b)) return Task.FromResult(false);
        return b.SignInAsync(PlayerIdentity.Local, ct);
    }

    /// <summary>Forget the open account (tests and tools; the identity never changes inside a play session).</summary>
    public static void Close()
    {
        backend = null;
        key = default;
        openedRoot = null;
    }

    static void WarnOnce(string msg)
    {
        if (msg == lastWarning) return;
        lastWarning = msg;
        Debug.LogWarning("[Accounts] " + msg + ".");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetForPlaySession()
    {
        Close();
        sessionId = null;
        lastWarning = null;
        LocalAccountBackend.NewRun();
    }
}

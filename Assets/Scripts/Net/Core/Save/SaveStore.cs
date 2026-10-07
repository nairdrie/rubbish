using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Save-store contract (Docs/Multiplayer.md §8.7.3 C1, Docs/Accounts.md §5.3). Async-first, with a synchronous fast path for
// quit. B4 implements LocalFileStore + LocalAccountBackend on SafeFile; M1.5 adds UgsAccountBackend behind the same interfaces.

/// <summary>Versioned key → bytes store for one account (<see cref="Scope"/> = the account folder or cloud player).</summary>
public interface ISaveStore
{
    string Scope { get; }
    Task<SaveRead> ReadAsync(string key, CancellationToken ct = default);
    /// <summary><paramref name="expectedVersion"/> = -1 writes regardless; otherwise a mismatch is <see cref="SaveError.VersionConflict"/>.</summary>
    Task<SaveWrite> WriteAsync(string key, byte[] data, long expectedVersion, CancellationToken ct = default);
    Task<IReadOnlyList<SaveKeyInfo>> ListAsync(string prefix, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Synchronous local write for quit. True when the bytes are durably on disk.</summary>
    bool TryWriteNow(string key, byte[] data);
}

public readonly struct SaveRead
{
    public readonly bool Found;
    public readonly byte[] Data;
    public readonly long Version;
    public readonly DateTime SavedUtc;
    /// <summary>Where it came from: "main", a generation tier ("latest.2", "session"…), "cloud".</summary>
    public readonly string Source;

    public SaveRead(bool found, byte[] data, long version, DateTime savedUtc, string source)
    {
        Found = found; Data = data; Version = version; SavedUtc = savedUtc; Source = source;
    }

    public static SaveRead Missing => new SaveRead(false, null, 0, default, null);
}

public readonly struct SaveWrite
{
    public readonly bool Ok;
    public readonly long Version;
    public readonly SaveError Error;

    public SaveWrite(bool ok, long version, SaveError error)
    {
        Ok = ok; Version = version; Error = error;
    }

    public static SaveWrite Success(long version) => new SaveWrite(true, version, SaveError.None);
    public static SaveWrite Fail(SaveError e) => new SaveWrite(false, 0, e);
}

public enum SaveError { None, VersionConflict, NotFound, Io, Offline, TooLarge, Denied, StaleEpoch }

public readonly struct SaveKeyInfo
{
    public readonly string Key;
    public readonly long Version;
    public readonly DateTime SavedUtc;
    public readonly int Bytes;

    public SaveKeyInfo(string key, long version, DateTime savedUtc, int bytes)
    {
        Key = key; Version = version; SavedUtc = savedUtc; Bytes = bytes;
    }
}

public enum AccountState { Offline, SigningIn, SignedIn, Failed }

/// <summary>A company checkout (local lock in M0/M1, cloud lease from M1.5). A higher <see cref="Epoch"/> wins (take-back).</summary>
public readonly struct CompanyLease
{
    public readonly bool Granted;
    public readonly string CompanyGuid, SessionId;
    /// <summary>When refused: who holds it ("Nick's PC", a session id).</summary>
    public readonly string HeldBy;
    public readonly string TownName;
    public readonly long Epoch;
    public readonly DateTime ExpiresUtc;

    public CompanyLease(bool granted, string companyGuid, string sessionId, string heldBy, string townName, long epoch, DateTime expiresUtc)
    {
        Granted = granted; CompanyGuid = companyGuid; SessionId = sessionId; HeldBy = heldBy; TownName = townName;
        Epoch = epoch; ExpiresUtc = expiresUtc;
    }
}

public interface IAccountBackend
{
    AccountState State { get; }
    string AccountId { get; }
    bool IsCloud { get; }
    event Action<AccountState> StateChanged;
    ISaveStore Store { get; }
    Task<bool> SignInAsync(IPlayerIdentity id, CancellationToken ct = default);
    Task<CompanyLease> CheckoutCompanyAsync(string companyGuid, string sessionId, string townName, CancellationToken ct = default);
    Task<SaveWrite> CommitCompanyAsync(CompanyLease lease, byte[] blob, long baseVersion, CancellationToken ct = default);
    Task CheckinCompanyAsync(CompanyLease lease, CancellationToken ct = default);
    /// <summary>New epoch; the old session's commits then fail with <see cref="SaveError.StaleEpoch"/>.</summary>
    Task<CompanyLease> TakeBackAsync(string companyGuid, CancellationToken ct = default);
    Task<bool> RestorePreviousAsync(string companyGuid, CancellationToken ct = default);
}

/// <summary>
/// Store keys (no file extensions; a store maps them). Guids are restricted to <c>[A-Za-z0-9-]</c> so a key can never escape
/// the account folder.
/// </summary>
public static class SaveKeys
{
    public const string Profile = "profile";
    public static string Company(string g) => "companies/" + Check(g);
    public static string CompanyPrev(string g) => "companies/" + Check(g) + ".prev";
    public static string Town(string g) => "towns/" + Check(g);
    public static string Lock(string g) => "companies/" + Check(g) + ".lock";

    public static bool IsValidGuid(string g)
    {
        if (string.IsNullOrEmpty(g) || g.Length > 64) return false;
        foreach (char c in g)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return true;
    }

    static string Check(string g)
    {
        if (!IsValidGuid(g)) throw new ArgumentException($"bad save guid '{g}'", nameof(g));
        return g;
    }
}

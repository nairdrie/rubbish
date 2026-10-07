using System;
using System.Globalization;

/// <summary>
/// Who a player is, for approval, slots, bans and save folders (Docs/Accounts.md §1.1): <c>steam:&lt;id64&gt;</c> or
/// <c>dev:&lt;name&gt;</c>. Fixed per build (§1.2), never inferred from whether Steam happens to be running.
/// Dev names are lower-case <c>[a-z0-9._-]</c>, 1–48 chars, so <see cref="FolderName"/> is safe on every file system.
/// </summary>
public readonly struct PlayerKey : IEquatable<PlayerKey>
{
    public const string SteamPrefix = "steam:";
    public const string DevPrefix = "dev:";
    public const int MaxDevNameLength = 48;

    /// <summary>The canonical text form; null for <c>default(PlayerKey)</c>.</summary>
    public readonly string Value;

    PlayerKey(string value) { Value = value; }

    public bool IsValid => Value != null;
    public bool IsSteam => Value != null && Value.StartsWith(SteamPrefix, StringComparison.Ordinal);
    public bool IsDev => Value != null && Value.StartsWith(DevPrefix, StringComparison.Ordinal);

    /// <summary>The SteamID64, or 0 for a dev key.</summary>
    public ulong SteamId64 =>
        IsSteam && ulong.TryParse(Value.Substring(SteamPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL;

    /// <summary>The name after <c>dev:</c>, or null.</summary>
    public string DevName => IsDev ? Value.Substring(DevPrefix.Length) : null;

    /// <summary>Account folder name under <c>&lt;data root&gt;/accounts/</c>: <c>steam_7656…</c> | <c>dev_editor</c>.</summary>
    public string FolderName => Value?.Replace(':', '_');

    public static PlayerKey Steam(ulong id64)
    {
        if (id64 == 0) throw new ArgumentOutOfRangeException(nameof(id64), "SteamID64 can't be 0");
        return new PlayerKey(SteamPrefix + id64.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A dev key. The name is lower-cased; throws on characters outside <c>[a-z0-9._-]</c> or a bad length.</summary>
    public static PlayerKey Dev(string name)
    {
        string n = name?.Trim().ToLowerInvariant();
        if (!IsValidDevName(n)) throw new ArgumentException($"bad dev player name '{name}' (use 1-{MaxDevNameLength} of a-z 0-9 . _ -)", nameof(name));
        return new PlayerKey(DevPrefix + n);
    }

    public static bool TryParse(string s, out PlayerKey k)
    {
        k = default;
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        if (s.StartsWith(SteamPrefix, StringComparison.Ordinal))
        {
            string digits = s.Substring(SteamPrefix.Length);
            if (digits.Length == 0 || digits.Length > 20 || (digits.Length > 1 && digits[0] == '0')) return false;
            if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0) return false;
            k = new PlayerKey(s);
            return true;
        }
        if (s.StartsWith(DevPrefix, StringComparison.Ordinal))
        {
            string n = s.Substring(DevPrefix.Length);
            if (!IsValidDevName(n)) return false; // already canonical (lower-case) or rejected
            k = new PlayerKey(s);
            return true;
        }
        return false;
    }

    static bool IsValidDevName(string n)
    {
        if (string.IsNullOrEmpty(n) || n.Length > MaxDevNameLength) return false;
        if (n == "." || n == "..") return false;
        foreach (char c in n)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    public bool Equals(PlayerKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object obj) => obj is PlayerKey o && Equals(o);
    public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    public static bool operator ==(PlayerKey a, PlayerKey b) => a.Equals(b);
    public static bool operator !=(PlayerKey a, PlayerKey b) => !a.Equals(b);
    public override string ToString() => Value ?? "(none)";
}

using System;
using System.Text;

/// <summary>Small pure helpers for the session code (package B1): join codes, duplicate dev keys, services profiles.</summary>
public static class SessionKeys
{
    /// <summary>Longest join code accepted from the menu (Unity Relay codes are 6 characters).</summary>
    public const int MaxJoinCode = 16;

    /// <summary>What the player typed → an upper-case code of letters and digits (spaces, dashes and junk dropped), ≤16.</summary>
    public static string NormalizeJoinCode(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var sb = new StringBuilder(8);
        foreach (char c in typed)
        {
            if (sb.Length >= MaxJoinCode) break;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// A second window on the same PC can arrive with the host's own dev key (B3 derives it per install). Give it a distinct
    /// key: <c>dev:name-2</c>, <c>-3</c>, … (the first one <paramref name="taken"/> says is free). Steam keys are never
    /// rewritten: false. False also when 2..99 are all taken.
    /// </summary>
    public static bool TryDisambiguate(PlayerKey key, Func<PlayerKey, bool> taken, out PlayerKey result)
    {
        result = key;
        if (!key.IsDev || taken == null) return false;
        string name = key.DevName;
        for (int n = 2; n < 100; n++)
        {
            string suffix = "-" + n;
            string stem = name.Length + suffix.Length > PlayerKey.MaxDevNameLength
                ? name.Substring(0, PlayerKey.MaxDevNameLength - suffix.Length)
                : name;
            var k = PlayerKey.Dev(stem + suffix);
            if (!taken(k)) { result = k; return true; }
        }
        return false;
    }

    /// <summary>
    /// A Unity Services sign-in profile for this player (so bots and test windows on one machine don't share one cached
    /// anonymous session): <c>s&lt;id64&gt;</c> for Steam (Accounts.md §1.1), <c>d_&lt;name&gt;</c> for dev keys; only
    /// <c>[A-Za-z0-9_-]</c>, ≤30 characters. Null for an invalid key (use the default profile).
    /// </summary>
    public static string ServicesProfile(PlayerKey key)
    {
        if (!key.IsValid) return null;
        string raw = key.IsSteam ? "s" + key.SteamId64 : "d_" + key.DevName;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (sb.Length >= 30) break;
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            sb.Append(ok ? c : '_');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}

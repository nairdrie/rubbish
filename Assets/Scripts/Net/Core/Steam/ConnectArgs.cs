using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// The Steam "join this lobby" strings (Docs/Multiplayer.md §8.7.4 B10). Pure, so it's unit-tested without Steam:
/// <list type="bullet">
/// <item>cold start: Steam launches the game with <c>+connect_lobby &lt;id&gt;</c> on the command line;</item>
/// <item>rich presence: our <c>connect</c> value is <c>+connect_lobby &lt;id&gt;</c>, handed back verbatim in
/// <c>GameRichPresenceJoinRequested_t</c>;</item>
/// <item><c>SteamApps.GetLaunchCommandLine</c> (a <c>steam://run</c> launch while running) returns one line to tokenize.</item>
/// </list>
/// Everything here is another machine's text: bounded, never throws, and the id must look like a Steam lobby.
/// </summary>
public static class ConnectArgs
{
    public const string ConnectLobby = "+connect_lobby";
    /// <summary>Longest line we bother to read (Steam's rich-presence values are ≤256 chars; launch lines are short).</summary>
    public const int MaxLineChars = 1024;

    // SteamID64 layout: bits 0-31 account, 32-51 instance, 52-55 account type, 56-63 universe.
    const int ChatAccountType = 8;          // k_EAccountTypeChat
    const uint LobbyInstanceFlag = 0x40000; // k_EChatInstanceFlagLobby = (k_unSteamAccountInstanceMask + 1) >> 2

    /// <summary>The rich-presence <c>connect</c> value for a lobby.</summary>
    public static string ForLobby(ulong lobbyId) => ConnectLobby + " " + lobbyId.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="id"/> is a Steam lobby id (chat account type with the lobby instance flag).</summary>
    public static bool LooksLikeLobbyId(ulong id)
    {
        if (id == 0) return false;
        uint universe = (uint)(id >> 56);
        uint type = (uint)(id >> 52) & 0xF;
        uint instance = (uint)(id >> 32) & 0xFFFFF;
        return universe >= 1 && universe <= 4 && type == ChatAccountType && (instance & LobbyInstanceFlag) != 0;
    }

    /// <summary>
    /// Find <c>+connect_lobby &lt;id&gt;</c> (or <c>+connect_lobby=&lt;id&gt;</c>; a leading '-' is tolerated) in a command line
    /// already split into arguments. The first valid lobby id wins.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out ulong lobbyId)
    {
        lobbyId = 0;
        if (args == null) return false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (string.IsNullOrEmpty(a) || a.Length > MaxLineChars) continue;
            if (!IsConnectToken(a, out string inlineValue)) continue;
            string v = inlineValue ?? (i + 1 < args.Count ? args[i + 1] : null);
            if (TryParseLobbyId(v, out lobbyId)) return true;
        }
        lobbyId = 0;
        return false;
    }

    /// <summary>Parse one line (a rich-presence connect string or a launch command line).</summary>
    public static bool TryParse(string line, out ulong lobbyId)
    {
        lobbyId = 0;
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineChars) return false;
        return TryParse(Tokenize(line), out lobbyId);
    }

    /// <summary>A lobby id as decimal digits (surrounding quotes and spaces ignored). Must pass <see cref="LooksLikeLobbyId"/>.</summary>
    public static bool TryParseLobbyId(string s, out ulong lobbyId)
    {
        lobbyId = 0;
        if (string.IsNullOrEmpty(s)) return false;
        string t = s.Trim().Trim('"').Trim();
        if (t.Length == 0 || t.Length > 20) return false;
        foreach (char c in t)
            if (c < '0' || c > '9') return false;
        if (!ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) return false;
        if (!LooksLikeLobbyId(id)) return false;
        lobbyId = id;
        return true;
    }

    /// <summary>Split on whitespace, honouring double quotes. Bounded by <see cref="MaxLineChars"/>.</summary>
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(line)) return tokens;
        if (line.Length > MaxLineChars) line = line.Substring(0, MaxLineChars);
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { tokens.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) tokens.Add(sb.ToString());
        return tokens;
    }

    static bool IsConnectToken(string a, out string inlineValue)
    {
        inlineValue = null;
        string t = a.Trim();
        if (t.StartsWith("-", StringComparison.Ordinal) && t.Length > 1 && t[1] != '-') t = "+" + t.Substring(1);
        if (string.Equals(t, ConnectLobby, StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Length > ConnectLobby.Length + 1 && t[ConnectLobby.Length] == '='
            && t.StartsWith(ConnectLobby, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = t.Substring(ConnectLobby.Length + 1);
            return true;
        }
        return false;
    }
}

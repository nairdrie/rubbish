using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// What a Rubbish Steam lobby advertises (Docs/Multiplayer.md §8.7.4 B10, Accounts.md §6.2): the <c>rb_*</c> lobby data keys.
/// Pure: encode to key/value pairs on the host, decode and validate on the joiner. Lobby data is written by another player, so
/// decoding is hostile-input safe (bounded, never throws) and the result only ever becomes a <see cref="JoinTarget"/>.
/// AppID 480 is shared by thousands of test games, so <c>rb_game</c> must be checked on every lobby we read.
/// </summary>
public sealed class LobbyInfo
{
    public const string KeyGame = "rb_game", KeyProto = "rb_proto", KeyContent = "rb_content", KeyNet = "rb_net",
        KeyRelay = "rb_relay", KeyTown = "rb_town", KeySession = "rb_session", KeyName = "rb_name";

    /// <summary>Every key we write, in order.</summary>
    public static readonly string[] Keys = { KeyGame, KeyProto, KeyContent, KeyNet, KeyRelay, KeyTown, KeySession, KeyName };

    public const string GameId = "rubbish";
    public const string NetRelay = "relay", NetSdr = "sdr";
    public const int MaxTextChars = 64;
    /// <summary>Unity Relay join codes are short upper-case alphanumerics (6 today); allow a little growth.</summary>
    public const int MinRelayCode = 6, MaxRelayCode = 12;

    public string game = GameId;
    public ushort proto = NetVersion.Protocol;
    public ushort content = NetVersion.Content;
    public string net = NetRelay;
    /// <summary>Unity Relay join code (when <see cref="net"/> is <c>relay</c>).</summary>
    public string relay;
    public string town;
    public string session;
    /// <summary>The host's Steam name at creation, for "Joining Nick's shift…" before connecting. Display only.</summary>
    public string hostName;

    /// <summary>A Friends host on Unity Relay (D6: invites and Join Game before the Steam transport exists).</summary>
    public static LobbyInfo ForRelayHost(string relayCode, string townName, string sessionId, string hostName) => new LobbyInfo
    {
        net = NetRelay,
        relay = NormalizeRelayCode(relayCode),
        town = Clean(townName),
        session = Clean(sessionId),
        hostName = Clean(hostName),
    };

    /// <summary>Key/value pairs for <c>SteamMatchmaking.SetLobbyData</c>. Empty optional values are written as "".</summary>
    public List<KeyValuePair<string, string>> Encode()
    {
        return new List<KeyValuePair<string, string>>
        {
            Kv(KeyGame, game ?? GameId),
            Kv(KeyProto, proto.ToString(CultureInfo.InvariantCulture)),
            Kv(KeyContent, content.ToString(CultureInfo.InvariantCulture)),
            Kv(KeyNet, net ?? ""),
            Kv(KeyRelay, relay ?? ""),
            Kv(KeyTown, Clean(town) ?? ""),
            Kv(KeySession, Clean(session) ?? ""),
            Kv(KeyName, Clean(hostName) ?? ""),
        };
    }

    /// <summary>
    /// Read a lobby through <paramref name="get"/> (e.g. <c>k =&gt; SteamMatchmaking.GetLobbyData(id, k)</c>; missing keys
    /// come back empty). False with a reason when it isn't a Rubbish lobby or a field is malformed. Version and transport are
    /// checked by <see cref="TryGetJoinTarget"/>, so a mismatched lobby still decodes and can be described to the player.
    /// </summary>
    public static bool TryDecode(Func<string, string> get, out LobbyInfo info, out string error)
    {
        info = null;
        error = null;
        if (get == null) { error = "no lobby data"; return false; }
        try
        {
            string g = Bounded(get(KeyGame));
            if (!string.Equals(g, GameId, StringComparison.Ordinal))
            {
                error = string.IsNullOrEmpty(g) ? "that lobby isn't a Rubbish game (or it has closed)" : "that lobby is a different game";
                return false;
            }
            if (!ushort.TryParse(Bounded(get(KeyProto)), NumberStyles.None, CultureInfo.InvariantCulture, out ushort p))
            {
                error = "the lobby has no protocol version";
                return false;
            }
            ushort.TryParse(Bounded(get(KeyContent)), NumberStyles.None, CultureInfo.InvariantCulture, out ushort c);
            info = new LobbyInfo
            {
                game = g,
                proto = p,
                content = c,
                net = (Bounded(get(KeyNet)) ?? "").Trim().ToLowerInvariant(),
                relay = NormalizeRelayCode(Bounded(get(KeyRelay))),
                town = Clean(get(KeyTown)),
                session = Clean(get(KeySession)),
                hostName = Clean(get(KeyName)),
            };
            return true;
        }
        catch (Exception e)
        {
            info = null;
            error = "couldn't read the lobby: " + e.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Turn a decoded lobby into somewhere to connect. Refuses a different protocol (<see cref="JoinReject.ProtocolMismatch"/>,
    /// "Update your game") and transports this build can't use. A different content version is fine (the host's values win).
    /// </summary>
    public bool TryGetJoinTarget(ulong lobbyId, ulong ownerSteamId, out JoinTarget target, out JoinReject why, out string error)
    {
        target = null;
        why = JoinReject.None;
        error = null;
        if (proto != NetVersion.Protocol)
        {
            why = JoinReject.ProtocolMismatch;
            error = JoinRejectText.For(why) + $" (host {proto}, you {NetVersion.Protocol})";
            return false;
        }
        if (!TryParseNet(net, out NetMode mode))
        {
            why = JoinReject.BadRequest;
            error = string.IsNullOrEmpty(net) ? "the host hasn't opened the game to friends yet" : $"unknown connection type '{Clip(net, 16)}'";
            return false;
        }
        if (mode == NetMode.UnityRelay)
        {
            if (!IsValidRelayCode(relay))
            {
                why = JoinReject.BadRequest;
                error = "the lobby has no usable join code";
                return false;
            }
            target = new JoinTarget { mode = NetMode.UnityRelay, joinCode = relay, steamLobbyId = lobbyId, hostSteamId = ownerSteamId };
            return true;
        }
        why = JoinReject.ProtocolMismatch;
        error = "The host is using the Steam relay, which this build doesn't have yet. Update your game.";
        return false;
    }

    /// <summary><c>relay</c> → UnityRelay, <c>sdr</c> → SteamSdr.</summary>
    public static bool TryParseNet(string s, out NetMode mode)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case NetRelay: mode = NetMode.UnityRelay; return true;
            case NetSdr: mode = NetMode.SteamSdr; return true;
            default: mode = NetMode.Offline; return false;
        }
    }

    public static string NetKeyFor(NetMode mode) => mode switch
    {
        NetMode.UnityRelay => NetRelay,
        NetMode.SteamSdr => NetSdr,
        _ => "",
    };

    public static bool IsValidRelayCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length < MinRelayCode || code.Length > MaxRelayCode) return false;
        foreach (char c in code)
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
        return true;
    }

    /// <summary>Trimmed and upper-cased; null when empty or too long to be a code.</summary>
    public static string NormalizeRelayCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string t = code.Trim().ToUpperInvariant();
        return t.Length > MaxRelayCode ? null : t;
    }

    /// <summary>Display text from another machine: control characters and markup brackets removed, at most 64 chars.</summary>
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder(Math.Min(s.Length, MaxTextChars));
        foreach (char ch in s)
        {
            if (sb.Length >= MaxTextChars) break;
            if (char.IsControl(ch) || ch == '<' || ch == '>') continue;
            sb.Append(ch);
        }
        string t = sb.ToString().Trim();
        return t.Length == 0 ? null : t;
    }

    public override string ToString() =>
        $"{KeyGame}={game} {KeyProto}={proto} {KeyContent}={content} {KeyNet}={net} {KeyRelay}={(string.IsNullOrEmpty(relay) ? "-" : "set")} " +
        $"{KeyTown}={town ?? "-"} {KeyName}={hostName ?? "-"}";

    static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);

    static string Bounded(string s) => s == null ? null : (s.Length > 256 ? s.Substring(0, 256) : s);

    static string Clip(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n));
}

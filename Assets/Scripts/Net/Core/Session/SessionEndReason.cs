using System;
using System.Text;

/// <summary>
/// Why a session ended or a join was refused, as it travels in NGO's disconnect reason (package B1). Wire form:
/// <c>RB1|&lt;Code&gt;|&lt;text&gt;</c>, where <c>Code</c> is a <see cref="JoinReject"/> name or one of the extra codes below,
/// and <c>text</c> is the sender's player-facing sentence. The receiver prefers its <b>own</b> text for codes it knows (its
/// own language and version) and treats the sender's text as hostile: it's shown only for unknown codes, stripped of
/// markup and control characters, and capped.
/// </summary>
public readonly struct SessionEndReason
{
    public const string Prefix = "RB1|";
    /// <summary>Longest text shown from another machine.</summary>
    public const int MaxText = 160;

    // Codes that aren't JoinReject values.
    public const string Kicked = "Kicked";
    /// <summary>The same PlayerKey joined again from another window or PC; the older connection is dropped.</summary>
    public const string Replaced = "Replaced";
    /// <summary>The connection dropped without a reason (host crashed, network gone).</summary>
    public const string Lost = "Lost";
    /// <summary>The player left on purpose.</summary>
    public const string Left = "Left";

    public readonly string Code;
    public readonly string Text;

    public SessionEndReason(string code, string text)
    {
        Code = string.IsNullOrEmpty(code) ? Lost : code;
        Text = text ?? "";
    }

    public bool IsReject => TryGetReject(out _);

    /// <summary>The <see cref="JoinReject"/> this code names (false for Kicked, Lost, …).</summary>
    public bool TryGetReject(out JoinReject r)
    {
        if (Enum.TryParse(Code, false, out r) && Enum.IsDefined(typeof(JoinReject), r) && r != JoinReject.None) return true;
        r = JoinReject.None;
        return false;
    }

    public static string Encode(JoinReject r) => Encode(r.ToString(), JoinRejectText.For(r));

    public static string Encode(string code, string text)
    {
        string c = Clean(code, 32).Replace("|", "");
        if (c.Length == 0) c = Lost;
        return Prefix + c + "|" + Clean(text, MaxText);
    }

    /// <summary>Parse a wire reason. False for empty or foreign strings (NGO's own reasons, old builds).</summary>
    public static bool TryDecode(string wire, out SessionEndReason reason)
    {
        reason = default;
        if (string.IsNullOrEmpty(wire) || !wire.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int bar = wire.IndexOf('|', Prefix.Length);
        string code = bar < 0 ? wire.Substring(Prefix.Length) : wire.Substring(Prefix.Length, bar - Prefix.Length);
        string text = bar < 0 ? "" : wire.Substring(bar + 1);
        code = Clean(code, 32);
        if (code.Length == 0) return false;
        reason = new SessionEndReason(code, Clean(text, MaxText));
        return true;
    }

    /// <summary>
    /// The sentence to show for any disconnect reason: ours (this build's wording for known codes, else the sender's cleaned
    /// text), a foreign non-empty string (cleaned), or <paramref name="fallback"/> when there's nothing usable.
    /// </summary>
    public static string Describe(string wire, string fallback)
    {
        if (TryDecode(wire, out var r))
        {
            if (r.TryGetReject(out var jr)) return JoinRejectText.For(jr);
            switch (r.Code)
            {
                case Kicked: return string.IsNullOrEmpty(r.Text) ? "The host removed you from the game." : "The host removed you: " + r.Text;
                case Replaced: return "You joined this game again from another window or PC.";
                case Left: return "You left the game.";
                case Lost: return fallback;
            }
            return string.IsNullOrEmpty(r.Text) ? fallback : r.Text;
        }
        string plain = Clean(wire, MaxText);
        return plain.Length > 0 ? plain : fallback;
    }

    /// <summary>Strip markup ('&lt;', '&gt;'), control characters and surrounding space; cap the length.</summary>
    public static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (char c in s)
        {
            if (sb.Length >= max) break;
            if (c == '<' || c == '>' || char.IsControl(c) || char.IsSurrogate(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public override string ToString() => $"{Code}: {Text}";
}

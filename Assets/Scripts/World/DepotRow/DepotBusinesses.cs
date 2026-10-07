using System;
using UnityEngine;

/// <summary>
/// The four player/business colours (Docs/Multiplayer.md §13.3). Lot k uses colour k until N1's business registry
/// gives each business its own (then <see cref="DepotBusinesses.ColorIndexFor"/> switches over).
/// </summary>
public static class DepotColors
{
    public static readonly string[] Names = { "Hi-Vis Orange", "Bin Teal", "Grape Purple", "Slime Lime" };

    public static readonly Color[] Colors =
    {
        new Color(1.00f, 0.47f, 0.10f), // Hi-Vis Orange
        new Color(0.09f, 0.70f, 0.66f), // Bin Teal
        new Color(0.56f, 0.30f, 0.86f), // Grape Purple
        new Color(0.56f, 0.86f, 0.14f), // Slime Lime
    };

    public static Color Get(int index) => Colors[Mathf.Abs(index) % Colors.Length];

    /// <summary>Darker shade for trims and sign backs.</summary>
    public static Color Dark(int index) => Color.Lerp(Get(index), Color.black, 0.45f);
}

/// <summary>
/// N3's adapter onto the business identity (package N1 owns businesses, wallets and saves: <see cref="Businesses"/>).
/// Everything Depot Row needs to know about a business goes through here:
/// <list type="bullet">
/// <item><see cref="KeyFor"/>: who holds a lot. The owner's PlayerKey: one business per player per session (§13.10), and a
/// guest gets their lot at the door, before N1's picker has declared their business.</item>
/// <item><see cref="BusinessIdFor"/> / <see cref="OwnerNameFor"/> / <see cref="ColorIndexFor"/> / <see cref="NameFor"/>: N1's
/// registry when it has the business (id, owner's display name, colour = roster slot, name), else the roster / lot /
/// <see cref="DefaultName"/>.</item>
/// </list>
/// </summary>
public static class DepotBusinesses
{
    public const string DefaultName = "RUBBISH CO.";
    public const int MaxNameLength = 24;

    /// <summary>Raised on the owner's machine after their business was renamed (persisted through GameSession).</summary>
    public static event Action<string> LocalNameChanged;

    public static string KeyFor(ulong clientId)
    {
        if (SessionService.TryGetPlayer(clientId, out var key) && key.IsValid) return key.Value;
        var roster = NetRoster.Instance;
        if (roster != null && roster.TryGetByClient(clientId, out var e2) && e2.playerKey.Length > 0) return e2.playerKey.ToString();
        return "client:" + clientId;
    }

    static bool TryBusiness(ulong clientId, out BusinessInfo b)
    {
        try { return Businesses.TryGetForClient(clientId, out b) && b.IsValid; }
        catch (Exception e) { Debug.LogException(e); b = default; return false; }
    }

    /// <summary>N1's business id for this client's business, or null (not declared yet / no registry).</summary>
    public static string BusinessIdFor(ulong clientId) => TryBusiness(clientId, out var b) ? b.Id : null;

    public static string OwnerNameFor(ulong clientId)
    {
        if (TryBusiness(clientId, out var b) && b.ownerName.Length > 0) return b.OwnerName;
        var roster = NetRoster.Instance;
        if (roster != null && roster.TryGetByClient(clientId, out var e) && e.displayName.Length > 0) return e.displayName.ToString();
        if (SessionService.TryGetPlayer(clientId, out var key) && key.IsValid)
        {
            try { var n = SessionService.DisplayNameFor?.Invoke(key); if (!string.IsNullOrEmpty(n)) return n; }
            catch (Exception ex) { Debug.LogException(ex); }
        }
        return clientId == 0 ? "Boss" : "Player " + clientId;
    }

    /// <summary>The business colour (N1: the owner's roster slot), else the lot's.</summary>
    public static int ColorIndexFor(ulong clientId, int lot)
    {
        if (TryBusiness(clientId, out var b)) return b.color % DepotColors.Colors.Length;
        return Mathf.Clamp(lot, 0, DepotColors.Colors.Length - 1);
    }

    /// <summary>The business name as the sign shows it (upper case), or null when N1 doesn't know it yet.</summary>
    public static string NameFor(ulong clientId)
    {
        if (!TryBusiness(clientId, out var b) || b.name.Length == 0) return null;
        return Clean(b.Name);
    }

    internal static void RaiseLocalNameChanged(string name)
    {
        try { GameSession.SetBusinessName(name); }
        catch (Exception e) { Debug.LogException(e); }
        try { LocalNameChanged?.Invoke(name); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ names

    static readonly string[] Blocked =
    {
        "fuck", "shit", "cunt", "nigg", "fag", "bitch", "whore", "slut", "rape", "nazi", "hitler", "kike", "spic", "chink",
        "retard", "penis", "vagina", "cock", "dick", "pussy", "porn", "sex",
    };

    /// <summary>
    /// A business name as shown on signs: trimmed, single spaces, printable ASCII-ish, ≤ 24 characters, upper case. Null
    /// when it's empty or trips the word filter.
    /// </summary>
    public static string Clean(string raw)
    {
        if (raw == null) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        bool space = false;
        foreach (char ch in raw)
        {
            char c = ch;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (c < 32 || c == '<' || c == '>' || c == '\\' || c == '{' || c == '}') continue; // no rich text / control chars
            if (!char.IsLetterOrDigit(c) && "'.&!-?,#@$+:()".IndexOf(c) < 0) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length >= MaxNameLength) break;
        }
        string s = sb.ToString().Trim().ToUpperInvariant();
        if (s.Length == 0) return null;
        if (!PassesFilter(s)) return null;
        return s;
    }

    public static bool PassesFilter(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var flat = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            char d = c switch { '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '@' => 'a', '$' => 's', _ => c };
            if (char.IsLetter(d)) flat.Append(d);
        }
        string f = flat.ToString();
        foreach (var w in Blocked) if (f.Contains(w)) return false;
        return true;
    }
}

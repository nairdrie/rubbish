using System.Text;
using UnityEngine;

/// <summary>
/// The four business colours (Docs/Multiplayer.md §13.3): one per player, per Depot Row lot and per truck. A player's
/// colour index is their roster slot (0 = the host). N3 tints Chuck's vest, cap and rings, the shop sign and the truck paint
/// from <see cref="For"/>; the HUD and menus use <see cref="Name"/>. Also the business-name rules shared by every screen.
/// </summary>
public static class BusinessColors
{
    /// <summary>Colours (and so players and businesses) per Town.</summary>
    public const int Count = 4;
    /// <summary>A new business's name; the sign reads "Nick's" small above it.</summary>
    public const string DefaultBusinessName = "Rubbish Co.";
    /// <summary>Longest business name (§13.2).</summary>
    public const int MaxNameLength = 24;

    static readonly Color32[] colors =
    {
        new Color32(0xFF, 0x7A, 0x0D, 0xFF), // Hi-Vis Orange
        new Color32(0x14, 0xB8, 0xA6, 0xFF), // Bin Teal
        new Color32(0x8B, 0x4F, 0xD6, 0xFF), // Grape Purple
        new Color32(0x9C, 0xE0, 0x2B, 0xFF), // Slime Lime
    };

    static readonly string[] names = { "Hi-Vis Orange", "Bin Teal", "Grape Purple", "Slime Lime" };

    /// <summary>The colour for index 0..3 (wraps; negative = 0).</summary>
    public static Color32 For(int index) => colors[Wrap(index)];

    /// <summary>"Hi-Vis Orange", "Bin Teal", "Grape Purple", "Slime Lime".</summary>
    public static string Name(int index) => names[Wrap(index)];

    /// <summary>The colour as a rich-text hex ("#FF7A0D").</summary>
    public static string Hex(int index)
    {
        var c = For(index);
        return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    static int Wrap(int index) => index < 0 ? 0 : index % Count;

    /// <summary>
    /// A business name as everyone sees it: trimmed, no control characters or rich-text brackets, at most
    /// <see cref="MaxNameLength"/> characters; empty becomes <see cref="DefaultBusinessName"/>. (Steam's text filter is applied
    /// by the viewer when it exists.)
    /// </summary>
    public static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultBusinessName;
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (char.IsControl(ch) || ch == '<' || ch == '>') continue;
            sb.Append(ch);
            if (sb.Length >= MaxNameLength) break;
        }
        string s = sb.ToString().Trim();
        return s.Length == 0 ? DefaultBusinessName : s;
    }

    /// <summary>The small line above the name on a sign: "Nick's" ("Chris'" for a name ending in s).</summary>
    public static string OwnerLine(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName)) return "";
        string n = ownerName.Trim();
        return n.EndsWith("s") || n.EndsWith("S") ? n + "'" : n + "'s";
    }
}

/// <summary>Business ids: "b" + 16 lowercase hex characters, made once per business and saved with it.</summary>
public static class BusinessIds
{
    public const int MaxLength = 40;

    /// <summary>A new random id (a save-slot id, not gameplay content, so it doesn't come from the host RNG).</summary>
    public static string New()
    {
        var bytes = new byte[8];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        var sb = new StringBuilder("b", 17);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>2..40 characters of a-z, 0-9, '-' and '_' (ids arrive from other players: check before use).</summary>
    public static bool IsValid(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 2 || id.Length > MaxLength) return false;
        foreach (char c in id)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
        return true;
    }
}

/// <summary>Trash put-out scaling by businesses online (Docs/Multiplayer.md §13.7): 1 → 1.0×, 2 → 1.7×, 3 → 2.3×, 4 → 2.8×.</summary>
public static class BusinessRates
{
    static readonly float[] scale = { 1f, 1.7f, 2.3f, 2.8f };

    /// <summary>The put-out rate and bag-cap multiplier for <paramref name="businessesOnline"/> (clamped to 1..4).</summary>
    public static float PutOutScale(int businessesOnline)
    {
        int i = Mathf.Clamp(businessesOnline, 1, scale.Length) - 1;
        return scale[i];
    }
}

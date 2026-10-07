using TMPro;
using UnityEngine;

/// <summary>
/// The UI fonts as data (Docs/UIPass.md §13 Q1: Fredoka, SIL OFL, approved 1 Oct 2026). <c>Editor/Setup_Fonts.cs</c> bakes three
/// static TMP SDF assets from <c>Assets/Fonts/Fredoka/*.ttf</c>, puts this component on <c>Assets/Prefabs/UI/UiFonts.prefab</c> and
/// registers that prefab in <see cref="RubbishAssets"/> as <see cref="Key"/>, so builds include the fonts without a Resources
/// folder. <see cref="UiTheme"/> reads it; nothing else should.
/// </summary>
public sealed class UiFontSet : MonoBehaviour
{
    /// <summary>The RubbishAssets prefab key.</summary>
    public const string Key = "ui/fonts";

    [Tooltip("Titles, big buttons, stamps (Fredoka Bold).")]
    public TMP_FontAsset display;
    [Tooltip("Labels, small buttons, HUD numbers (Fredoka SemiBold). It's also the Bold style of the body font (weight table).")]
    public TMP_FontAsset label;
    [Tooltip("Body text: descriptions, sub lines (Fredoka Regular). Its Bold style renders with the label font.")]
    public TMP_FontAsset body;

    /// <summary>The registered set, or null (setup not run: the UI stays on LiberationSans).</summary>
    public static UiFontSet Load()
    {
        var prefab = RubbishAssets.Prefab(Key);
        return prefab != null ? prefab.GetComponent<UiFontSet>() : null;
    }
}

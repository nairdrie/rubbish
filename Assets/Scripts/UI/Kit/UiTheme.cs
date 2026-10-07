using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The UI pass's look (Docs/UIPass.md §3): the two surfaces (plum for "game" things, paper for "documents"), the palette,
/// the text sizes and the UI sound ids. Everything new reads its colours and fonts from here.
/// <para><b>Fonts (§13 Q1: Fredoka, approved).</b> <c>Editor/Setup_Fonts.cs</c> bakes Fredoka (SIL OFL) into three static TMP SDF
/// assets and registers them in <see cref="RubbishAssets"/> (<see cref="UiFontSet"/>). Two fonts are ever assigned to a text, so
/// every material matches its text's atlas:</para>
/// <list type="bullet">
/// <item><see cref="DisplayFont"/> = Fredoka <b>Bold</b>: titles, big sticker buttons, stamps (UiKit "display" text).</item>
/// <item><see cref="BodyFont"/> = Fredoka <b>Regular</b>, everything else (the whole Feel HUD goes through <see cref="FeelUi.Text"/>).
/// Its weight table makes the Bold style render with Fredoka <b>SemiBold</b> glyphs (no faux bold), so labels, small buttons and
/// HUD numbers, which are all Bold-style text, come out SemiBold, and descriptions (Normal style) stay Regular.</item>
/// </list>
/// <para>Every Fredoka asset falls back to LiberationSans SDF, so a glyph Fredoka lacks (arrows, ★, ✓) never shows as a box.
/// Without the setup (no font set registered) everything is LiberationSans, exactly as before. A TMP font asset at
/// <c>Resources/UI/DisplayFont</c> still overrides the display font. Materials come from <see cref="OutlineFor"/> and
/// <see cref="InkFor"/>, one per font (never assign one font's material to another font's text: the atlas wouldn't match).</para>
/// </summary>
public static class UiTheme
{
    // ------------------------------------------------------------------ fonts

    /// <summary>Resources path of an optional display-font override (a TMP_FontAsset).</summary>
    public const string DisplayFontResource = "UI/DisplayFont";

    static TMP_FontAsset displayFont, bodyFont, labelFont;
    static bool fontsLooked;
    static readonly Dictionary<TMP_FontAsset, Material> outlineMats = new Dictionary<TMP_FontAsset, Material>();
    static readonly Dictionary<TMP_FontAsset, Material> inkMats = new Dictionary<TMP_FontAsset, Material>();

    static void LookFonts()
    {
        if (fontsLooked && bodyFont != null) return;
        fontsLooked = true;
        var liberation = TMP_Settings.defaultFontAsset;
        UiFontSet set = null;
        try { set = UiFontSet.Load(); } catch (System.Exception) { set = null; }
        bodyFont = set != null && set.body != null ? set.body : liberation;
        labelFont = set != null && set.label != null ? set.label : bodyFont;
        var overrideDisplay = Resources.Load<TMP_FontAsset>(DisplayFontResource);
        displayFont = overrideDisplay != null ? overrideDisplay : set != null && set.display != null ? set.display : bodyFont;
    }

    /// <summary>Forget the looked-up fonts (Setup_Fonts calls this after baking so edit-mode previews pick them up).</summary>
    public static void ReloadFonts()
    {
        fontsLooked = false;
        displayFont = bodyFont = labelFont = null;
    }

    /// <summary>The font of every non-display text (Fredoka Regular; Bold style = SemiBold glyphs). LiberationSans without the setup.</summary>
    public static TMP_FontAsset BodyFont { get { LookFonts(); return bodyFont; } }

    /// <summary>Fredoka SemiBold (the Bold style of <see cref="BodyFont"/>). For measuring or for code that wants the asset itself.</summary>
    public static TMP_FontAsset LabelFont { get { LookFonts(); return labelFont; } }

    /// <summary>Title / big button / stamp font (Fredoka Bold), else <see cref="BodyFont"/>.</summary>
    public static TMP_FontAsset DisplayFont { get { LookFonts(); return displayFont; } }

    /// <summary>True when a display font is installed (else display text is the body font in bold).</summary>
    public static bool HasDisplayFont => DisplayFont != BodyFont;

    /// <summary>True when the Fredoka set is installed (else the UI runs on LiberationSans).</summary>
    public static bool HasFontSet { get { LookFonts(); return bodyFont != TMP_Settings.defaultFontAsset; } }

    /// <summary>Chunky outline + drop shadow for text on plum or over the world (the HUD's look).</summary>
    public static Material OutlineMaterial(bool display) => OutlineFor(display ? DisplayFont : BodyFont);

    /// <summary>Clean ink for text on paper: a touch heavier, no outline, no shadow.</summary>
    public static Material InkMaterial(bool display) => InkFor(display ? DisplayFont : BodyFont);

    /// <summary>The outlined (HUD) material for <paramref name="font"/>'s atlas (cached; one per font).</summary>
    public static Material OutlineFor(TMP_FontAsset font)
    {
        if (font == null) return null;
        if (outlineMats.TryGetValue(font, out var m) && m != null) return m;
        m = MakeOutline(font, font.name + " (UiOutline)");
        if (m != null) outlineMats[font] = m;
        return m;
    }

    /// <summary>The paper-ink material for <paramref name="font"/>'s atlas (cached; one per font).</summary>
    public static Material InkFor(TMP_FontAsset font)
    {
        if (font == null) return null;
        if (inkMats.TryGetValue(font, out var m) && m != null) return m;
        m = MakeInk(font, font.name + " (UiInk)");
        if (m != null) inkMats[font] = m;
        return m;
    }

    /// <summary>Fredoka is rounder and heavier than LiberationSans: it gets less face dilation, so Regular stays Regular.</summary>
    static bool IsFredoka(TMP_FontAsset font) => font != null && font != TMP_Settings.defaultFontAsset && font.name.StartsWith("Fredoka");

    static Material MakeOutline(TMP_FontAsset font, string name)
    {
        if (font == null || font.material == null) return null;
        bool fredoka = IsFredoka(font);
        var m = new Material(font.material) { name = name, hideFlags = HideFlags.DontSave };
        m.EnableKeyword("OUTLINE_ON");
        m.SetFloat("_OutlineWidth", fredoka ? 0.2f : 0.22f);
        m.SetColor("_OutlineColor", new Color(0.07f, 0.05f, 0.1f, 1f));
        m.SetFloat("_FaceDilate", fredoka ? 0.05f : 0.12f);
        m.EnableKeyword("UNDERLAY_ON");
        m.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.55f));
        m.SetFloat("_UnderlayOffsetX", 0.55f);
        m.SetFloat("_UnderlayOffsetY", -0.55f);
        m.SetFloat("_UnderlayDilate", 0.2f);
        m.SetFloat("_UnderlaySoftness", 0.15f);
        return m;
    }

    static Material MakeInk(TMP_FontAsset font, string name)
    {
        if (font == null || font.material == null) return null;
        var m = new Material(font.material) { name = name, hideFlags = HideFlags.DontSave };
        m.DisableKeyword("OUTLINE_ON");
        m.DisableKeyword("UNDERLAY_ON");
        m.SetFloat("_OutlineWidth", 0f);
        m.SetFloat("_FaceDilate", IsFredoka(font) ? 0.02f : 0.08f);
        return m;
    }

    // ------------------------------------------------------------------ surfaces

    /// <summary>Plum: buttons, HUD, minimap frame (#1C172B at 88%; the same as <see cref="FeelUi.PanelColor"/>).</summary>
    public static readonly Color Plum = FeelUi.PanelColor;
    /// <summary>Plum, opaque (for things drawn over busy backgrounds).</summary>
    public static readonly Color PlumSolid = Hex(0x1C172B);
    /// <summary>Paper: the Esc timesheet, settings, the full map, confirm dialogs (#FFF5DB).</summary>
    public static readonly Color Paper = Hex(0xFFF5DB);
    /// <summary>A shade darker than paper: manila tabs, insets, row stripes.</summary>
    public static readonly Color PaperDark = Hex(0xEFDDB4);
    /// <summary>Ink on paper (#1F1A2E).</summary>
    public static readonly Color Ink = Hex(0x1F1A2E);
    /// <summary>Faded ink for descriptions on paper (keeps ≥ 4.5:1 on <see cref="Paper"/>).</summary>
    public static readonly Color InkSoft = Hex(0x5A5266);
    /// <summary>Rubber-stamp red (#DB1F29).</summary>
    public static readonly Color StampRed = Hex(0xDB1F29);
    /// <summary>Drop shadow under stickers and panels.</summary>
    public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.45f);

    // ------------------------------------------------------------------ palette

    /// <summary>Focus and the next stop (#FFD640).</summary>
    public static readonly Color Gold = Hex(0xFFD640);
    /// <summary>Chuck's colour (#FF7A1F).</summary>
    public static readonly Color Tangerine = Hex(0xFF7A1F);
    /// <summary>Gertrude the truck (#5B6B2F).</summary>
    public static readonly Color TruckOlive = Hex(0x5B6B2F);
    /// <summary>Hazard stripes: yellow on ink.</summary>
    public static readonly Color HazardYellow = Hex(0xFFD640);
    /// <summary>Good / on / confirm.</summary>
    public static readonly Color Go = Hex(0x2FA866);
    /// <summary>Disabled grey.</summary>
    public static readonly Color Disabled = Hex(0x9A94A3);
    /// <summary>White text on plum.</summary>
    public static readonly Color TextOnPlum = Color.white;

    /// <summary>The recycling-bin colours for the title letters (hazard, metal, plastic, glass, paper, KEEP).</summary>
    public static readonly Color[] BinColors =
    {
        Hex(0xFFD640), // hazard yellow
        Hex(0xA7AFB8), // metal grey
        Hex(0x2F6BD8), // plastic blue
        Hex(0x2FA866), // glass green
        Hex(0xB9875A), // paper brown
        Hex(0xFFC21A), // KEEP gold
    };

    /// <summary>Sticker button colours by style (body colour; text is white unless <see cref="StickerInk"/> says ink).</summary>
    public static Color StickerColor(UiButtonStyle s) => s switch
    {
        UiButtonStyle.Primary => Gold,
        UiButtonStyle.Danger => StampRed,
        UiButtonStyle.Go => Go,
        UiButtonStyle.Paper => PaperDark,
        UiButtonStyle.Tab => PaperDark,
        UiButtonStyle.Quiet => new Color(0.85f, 0.8f, 0.7f, 1f),
        _ => PlumSolid,
    };

    /// <summary>True when a sticker of this style carries ink text (light bodies), else white outlined text.</summary>
    public static bool StickerInk(UiButtonStyle s) =>
        s == UiButtonStyle.Primary || s == UiButtonStyle.Paper || s == UiButtonStyle.Tab || s == UiButtonStyle.Quiet;

    // ------------------------------------------------------------------ sizes (reference px at 1080)

    /// <summary>The legibility floor: nothing smaller, anywhere (§3).</summary>
    public const float MinText = 18f;
    /// <summary>Descriptions and small print.</summary>
    public const float Small = 20f;
    public const float Body = 26f;
    public const float Button = 34f;
    public const float Heading = 56f;
    public const float Title = 120f;

    // ------------------------------------------------------------------ motion

    /// <summary>Panel enter (OutBack) and leave times, unscaled seconds (§3 Motion).</summary>
    public const float EnterSeconds = 0.25f, LeaveSeconds = 0.15f;

    // ------------------------------------------------------------------ sound ids (existing Sfx only)

    public const string SfxOpen = "click";
    public const string SfxBack = "whoosh";
    public const string SfxHover = "squeak";
    public const string SfxPress = "pop";
    public const string SfxConfirm = "ding";
    public const string SfxError = "buzz";
    public const string SfxPlay = "horn";
    public const string SfxInvite = "notify";

    /// <summary>A UI sound on the 2D pool (no-op in edit mode).</summary>
    public static void Play(string id, float volume = 0.5f, float pitch = 1f)
    {
        if (!Application.isPlaying) return;
        Sfx.Play(id, null, volume, pitch);
    }

    /// <summary>The hover squeak (§3: pitch 1.4, volume 0.25).</summary>
    public static void PlayHover() => Play(SfxHover, 0.25f, 1.4f);

    // ------------------------------------------------------------------ helpers

    public static Color Hex(int rgb, float a = 1f) =>
        new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    public static Color WithAlpha(Color c, float a) { c.a = a; return c; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // Materials are DontSave and survive (cached per font); only re-look for the fonts (they may have been baked since).
        ReloadFonts();
    }
}

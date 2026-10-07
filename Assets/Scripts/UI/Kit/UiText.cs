/// <summary>
/// Every UI-pass string in one place (Docs/UIPass.md §3 "Copy voice"), so localisation later is one file. Head Office voice:
/// cheerful, a bit bureaucratic, never mean. Partial: each screen package keeps its block beside its code
/// (the main menu's is <c>UI/Menu/UiText.Menu.cs</c>); it is still one class to localise.
/// </summary>
public static partial class UiText
{
    // ---- common
    public const string Back = "Back";
    public const string ResetTab = "Reset tab";
    public const string Soon = "SOON";
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Yes = "Yes";
    public const string No = "No";
    public const string Keep = "Keep";
    public const string Revert = "Revert";

    // ---- quit / leave (shared by the menu and the pause menu)
    public const string QuitTitle = "Clock out for real?";
    public const string QuitYes = "Yes, I'm tired";
    public const string QuitNo = "One more bag";

    // ---- settings screen
    public const string SettingsTitle = "SETTINGS";
    public const string SettingsForm = "Form 27-B · Personal Preferences";
    public const string SettingsSaved = "FILED!";
    public const string SettingsNotSaved = "NOT FILED";
    public const string NeverMind = "Never mind";
    public const string SettingsReset = "Settings were reset (the file was damaged).";
    public const string SettingsRecovered = "Settings file was damaged. Restored your last good settings.";
    public const string RebindingSoon = "Rebinding is coming. For now, here's the cheat sheet.";
    public const string EditorVideoNote = "In the Editor this is logged, not applied.";
    public const string KeepDisplayTitle = "Keep these settings?";
    public const string KeepDisplayBody = "Reverting in {0}…";
    public const string ResetTabTitle = "Reset this tab?";
    public const string ResetTabBody = "Puts every {0} setting back to how Head Office likes it.";

    /// <summary>Head Office sticky notes on the settings folder (a different one each visit).</summary>
    public static readonly string[] StickyNotes =
    {
        "Raccoons are NOT employees.",
        "Gertrude runs on diesel and spite.",
        "Please stop honking at the mayor.",
        "Lost a ring? Check the compactor.",
        "Bins go IN the truck. Not you.",
        "Mandatory fun is on Fridays.",
    };

    public static readonly string[] SettingsTabs = { "AUDIO", "CONTROLS", "VIDEO", "INTERFACE", "ACCESSIBILITY" };

    // ---- audio
    public const string Master = "Master volume";
    public const string MasterDesc = "Everything. Trucks, raccoons, the lot.";
    public const string SfxVol = "Sound effects";
    public const string SfxVolDesc = "Bags, bins, honks, splats and dings.";
    public const string AmbienceVol = "Ambience";
    public const string AmbienceVolDesc = "Crickets, owls and one very loud rooster.";
    public const string MusicVol = "Music";
    public const string MusicVolDesc = "There's no music yet. Chuck hums.";
    public const string MuteUnfocused = "Mute when unfocused";
    public const string MuteUnfocusedDesc = "Silence while you're in another window.";

    // ---- controls
    public const string LookSens = "Look sensitivity";
    public const string LookSensDesc = "Mouse and stick look speed, on foot and at the sort station.";
    public const string InvertY = "Invert Y";
    public const string InvertYDesc = "Push up to look down. For pilots.";
    public const string Fov = "Field of view";
    public const string FovDesc = "On-foot camera, horizontal degrees. The truck and sort station follow along.";
    public const string Keys = "KEYS";

    // ---- video
    public const string DisplayMode = "Display mode";
    public const string DisplayModeDesc = "Borderless is the friendliest for alt-tabbing.";
    public const string Resolution = "Resolution";
    public const string ResolutionDesc = "Native looks sharpest.";
    public const string VSync = "VSync";
    public const string VSyncDesc = "No tearing. Caps the frame rate at your monitor's.";
    public const string FrameCap = "Frame cap";
    public const string FrameCapDesc = "Ignored while VSync is on.";
    public const string Quality = "Graphics quality";
    public const string QualityDesc = "Shadows, anti-aliasing and detail. High is how it ships.";
    public const string RenderScale = "Render scale";
    public const string RenderScaleDesc = "Below 100% draws fewer pixels: faster, softer.";
    public static readonly string[] DisplayModes = { "Borderless", "Fullscreen", "Windowed" };
    public static readonly string[] Qualities = { "Low", "Medium", "High", "Ultra" };
    public const string Native = "Native";
    public const string Unlimited = "Unlimited";

    // ---- interface
    public const string UiScaleLabel = "UI scale";
    public const string UiScaleDesc = "Makes every menu and the HUD bigger or smaller.";
    public const string MinimapSizeLabel = "Minimap size";
    public const string MinimapSizeDesc = "The bin lid in the bottom-left corner.";
    public const string MinimapTurn = "Minimap rotation";
    public const string MinimapTurnDesc = "Heading-up turns with you. North-up never turns.";
    public static readonly string[] MinimapTurns = { "Heading-up", "North-up" };
    public static readonly string[] MinimapSizes = { "Small", "Medium", "Large" };
    public const string StreetLabel = "Street name under minimap";
    public const string StreetLabelDesc = "\"Maple St · Mildly Pleasant Acres\".";
    public const string MapKeys = "Map key";
    public const string MapKeysDesc = "How the full paper map opens.";
    public static readonly string[] MapKeyOptions = { "M toggles + hold Tab peeks", "M toggles", "Hold Tab peeks" };
    public const string Hints = "Control hints";
    public const string HintsDesc = "Key-cap chips under prompts. The prompt itself always shows.";
    public const string ToastTimeLabel = "Toast time";
    public const string ToastTimeDesc = "How long pop-up messages stay. Every one goes to Messages too.";
    public const string Streamer = "Streamer mode";
    public const string StreamerDesc = "Hides your join code until you press Show.";

    // ---- accessibility
    public const string ScreenFx = "Screen effects";
    public const string ScreenFxDesc = "Rabies wobble, camera shake, slow-mo and FOV kicks. Below 25% slow-mo is off.";
    public const string Colorblind = "Colour-blind-safe markers";
    public const string ColorblindDesc = "Okabe–Ito colours plus shapes on curb markers, route rows and the map.";
    public const string ReduceFlashing = "Reduce flashing";
    public const string ReduceFlashingDesc = "Steadier toasts, blinks and rays.";
    public static readonly string[] FreshnessNames = { "Fresh", "Late soon", "Late", "Stinky" };
}

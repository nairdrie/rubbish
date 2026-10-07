/// <summary>
/// Canvas sort orders for every screen-space UI layer (Docs/UIPass.md §8.5). New UI picks its order from here, so nothing
/// fights over the same number. The legacy canvases keep their own literals; they're listed for reference.
/// </summary>
public static class UiLayers
{
    // ---- built by the UI pass
    /// <summary>The full-screen paper map (UP-2).</summary>
    public const int Map = 300;
    /// <summary>The main menu shell (UP-4, B14).</summary>
    public const int Menu = 500;
    /// <summary>The Esc timesheet (UP-3).</summary>
    public const int Pause = 1000;
    /// <summary>The settings screen (UP-0).</summary>
    public const int Settings = 1010;
    /// <summary>Paper confirm dialogs (<see cref="UiKit.Confirm"/>), always on top of the screen that asked.</summary>
    public const int Dialog = 1020;

    // ---- existing canvases (reference only; their owners set these)
    /// <summary>The legacy TooltipUI scene canvas.</summary>
    public const int LegacyTooltip = 0;
    /// <summary>The sort-station crosshair.</summary>
    public const int Crosshair = 90;
    /// <summary>ProgressionHud (rank chip, memos).</summary>
    public const int ProgressionHud = 99;
    /// <summary>FeelHUD (the minimap is nested in it).</summary>
    public const int Hud = 100;
    /// <summary>The laptop's UI Toolkit panel.</summary>
    public const int Laptop = 200;
    /// <summary>DistrictWelcome's arrival card.</summary>
    public const int DistrictWelcome = 640;
    /// <summary>ScreenFader.</summary>
    public const int Fader = 32000;
    /// <summary>LoadingSystem and SteamHud's blocker.</summary>
    public const int Loading = 32767;
}

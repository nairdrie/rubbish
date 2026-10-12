using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;
using UnityEngine.Scripting;

/// <summary>Display mode choices (Video tab).</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum DisplayModeSetting { Borderless, Exclusive, Windowed }

/// <summary>Graphics quality presets (Video tab; Docs/UIPass.md §6.2 table).</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum QualityPreset { Low, Medium, High, Ultra }

/// <summary>Minimap lid sizes: S 200, M 250, L 300 reference px.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum MinimapSize { S, M, L }

/// <summary>
/// Everything in <c>settings.json</c> (Docs/UIPass.md §6.2, §6.4). Plain fields with defaults, so a file that lacks a field (an
/// older game wrote it) reads as the default, and a field this game doesn't know survives in <see cref="GameSettings"/>'s
/// retained JSON. Never networked. Edit through <see cref="GameSettings.Edit"/> so the change applies and saves.
/// </summary>
[Serializable, Preserve]
public sealed class GameSettingsData
{
    public const int Schema = 1;

    public int schema = Schema;
    public AudioS audio = new AudioS();
    public ControlsS controls = new ControlsS();
    public VideoS video = new VideoS();
    public InterfaceS ui = new InterfaceS();
    public AccessS access = new AccessS();
    /// <summary>The host's preferred Town rules, saved locally and applied to the next Town they host.</summary>
    public TownRulesS townRules = new TownRulesS();

    [Preserve] public GameSettingsData() { }

    /// <summary>Audio tab. Volumes are 0–100.</summary>
    [Serializable, Preserve]
    public sealed class AudioS
    {
        /// <summary>Everything (AudioListener.volume). 100 = today's mix (see SettingsApplier).</summary>
        public int master = 100;
        /// <summary>Sound effects multiplier (FeelSfxPlayer, category SFX).</summary>
        public int sfx = 100;
        /// <summary>Night ambience multiplier (cricket, owl, rooster).</summary>
        public int ambience = 100;
        /// <summary>Music multiplier: the background tracks, the casino's house band and the workshop radio's tune
        /// (BackgroundMusic, CasinoMusic, WorkshopGadget). Separate from the Sound effects and Ambience sliders.</summary>
        public int music = 70;
        /// <summary>Hidden until M3's voice chat.</summary>
        public int voice = 100;
        /// <summary>Silence while the window isn't focused.</summary>
        public bool muteUnfocused = true;
        [Preserve] public AudioS() { }
    }

    /// <summary>Controls tab.</summary>
    [Serializable, Preserve]
    public sealed class ControlsS
    {
        /// <summary>Look sensitivity multiplier, 0.25–3.00.</summary>
        public float lookSensitivity = 1f;
        public bool invertY = false;
        /// <summary>On-foot field of view in horizontal degrees at 16:9 (50–90); 0 = the authored camera's.</summary>
        public int fov = 0;
        [Preserve] public ControlsS() { }
    }

    /// <summary>Video tab. Per machine: a resolution missing on this PC falls back to native.</summary>
    [Serializable, Preserve]
    public sealed class VideoS
    {
        public DisplayModeSetting displayMode = DisplayModeSetting.Borderless;
        /// <summary>0×0 = native (the desktop's resolution; 1280×720 when windowed).</summary>
        public int width = 0, height = 0;
        public bool vsync = true;
        /// <summary>Frames per second cap; 0 = unlimited.</summary>
        public int frameCap = 0;
        public QualityPreset quality = QualityPreset.High;
        /// <summary>Render scale in percent (50–100); 0 = the preset's.</summary>
        public int renderScale = 0;
        [Preserve] public VideoS() { }
    }

    /// <summary>Interface tab.</summary>
    [Serializable, Preserve]
    public sealed class InterfaceS
    {
        /// <summary>UI scale in percent, 80–150 in steps of 10.</summary>
        public int scale = 100;
        public MinimapSize minimapSize = MinimapSize.M;
        /// <summary>False = heading-up (§13 Q3 default), true = north-up.</summary>
        public bool minimapNorthUp = false;
        public bool minimapStreetLabel = true;
        /// <summary>M toggles the full map.</summary>
        public bool mapKeyToggle = true;
        /// <summary>Holding Tab peeks at the full map.</summary>
        public bool mapKeyPeek = true;
        /// <summary>Key-cap hint chips under the prompt.</summary>
        public bool controlHints = true;
        /// <summary>Toast duration multiplier: 1, 1.5 or 2.</summary>
        public float toastTime = 1f;
        /// <summary>Hide the join code until Show is pressed (§13 Q7: on by default).</summary>
        public bool streamerMode = true;
        [Preserve] public InterfaceS() { }
    }

    /// <summary>Accessibility tab.</summary>
    [Serializable, Preserve]
    public sealed class AccessS
    {
        /// <summary>Screen effects intensity in percent (rabies, shake, hit-stop, FOV kicks).</summary>
        public int screenFx = 100;
        /// <summary>Okabe–Ito freshness palette plus shapes.</summary>
        public bool colorblind = false;
        public bool reduceFlashing = false;
        /// <summary>Hidden until M5's tug.</summary>
        public bool autoPull = false;
        [Preserve] public AccessS() { }
    }

    /// <summary>Only implemented, host-controllable Town rules belong here. The host broadcasts the live value via NightNet.</summary>
    [Serializable, Preserve]
    public sealed class TownRulesS
    {
        /// <summary>New and older settings files default to chaotic Rabies Tag being on.</summary>
        public bool rabiesTag = true;
        [Preserve] public TownRulesS() { }
    }

    /// <summary>Clamp every field into its range and repair nulls (after a load, before use).</summary>
    public void Sanitize()
    {
        schema = Schema;
        audio ??= new AudioS();
        controls ??= new ControlsS();
        video ??= new VideoS();
        ui ??= new InterfaceS();
        access ??= new AccessS();
        townRules ??= new TownRulesS();

        audio.master = Mathf.Clamp(audio.master, 0, 100);
        audio.sfx = Mathf.Clamp(audio.sfx, 0, 100);
        audio.ambience = Mathf.Clamp(audio.ambience, 0, 100);
        audio.music = Mathf.Clamp(audio.music, 0, 100);
        audio.voice = Mathf.Clamp(audio.voice, 0, 100);

        float s = float.IsNaN(controls.lookSensitivity) ? 1f : controls.lookSensitivity;
        controls.lookSensitivity = Mathf.Round(Mathf.Clamp(s, 0.25f, 3f) * 20f) / 20f; // steps of 0.05
        if (controls.fov != 0) controls.fov = Mathf.Clamp(controls.fov, 50, 90);

        if (!Enum.IsDefined(typeof(DisplayModeSetting), video.displayMode)) video.displayMode = DisplayModeSetting.Borderless;
        if (!Enum.IsDefined(typeof(QualityPreset), video.quality)) video.quality = QualityPreset.High;
        if (video.width < 0 || video.height < 0 || (video.width == 0) != (video.height == 0)) video.width = video.height = 0;
        if (video.width > 0 && (video.width < 640 || video.height < 360)) video.width = video.height = 0;
        if (video.frameCap != 0 && Array.IndexOf(SettingsOptions.FrameCaps, video.frameCap) < 0) video.frameCap = 0;
        if (video.renderScale != 0) video.renderScale = Mathf.Clamp(video.renderScale, 50, 100);

        ui.scale = Mathf.Clamp(Mathf.RoundToInt(ui.scale / 10f) * 10, 80, 150);
        if (!Enum.IsDefined(typeof(MinimapSize), ui.minimapSize)) ui.minimapSize = MinimapSize.M;
        if (!(ui.toastTime == 1.5f || ui.toastTime == 2f)) ui.toastTime = 1f;
        if (!ui.mapKeyToggle && !ui.mapKeyPeek) ui.mapKeyToggle = true; // the map must stay reachable

        access.screenFx = Mathf.Clamp(access.screenFx, 0, 100);
    }

    /// <summary>A deep copy (keep/revert dialogs, tests).</summary>
    public GameSettingsData Clone() =>
        JsonConvert.DeserializeObject<GameSettingsData>(JsonConvert.SerializeObject(this, GameSettings.Json), GameSettings.Json);
}

/// <summary>The fixed choice lists the settings screen offers (shared with Sanitize).</summary>
public static class SettingsOptions
{
    /// <summary>Frame caps; 0 = unlimited.</summary>
    public static readonly int[] FrameCaps = { 30, 60, 120, 144, 165, 240, 0 };
    public static readonly float[] ToastTimes = { 1f, 1.5f, 2f };
    public static readonly int[] UiScales = { 80, 90, 100, 110, 120, 130, 140, 150 };
}

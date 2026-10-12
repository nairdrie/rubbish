using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The player's settings (Docs/UIPass.md §6): loaded once per play session from <see cref="SettingsStore"/>
/// (<c>&lt;data root&gt;/settings.json</c>), edited through <see cref="Edit"/>, applied live by <see cref="SettingsApplier"/>
/// and saved at most once per second while something changed, plus always on Back and on quit. Per machine, never
/// networked (D4).
/// <para><b>Unknown fields survive</b> (MP rule 9): the parsed file is kept as a <see cref="JObject"/> and every save merges
/// the known model over it, so a field written by a newer game (or a section this one hides) is written back untouched.</para>
/// <para><b>Cheap reads</b> (<see cref="LookSensitivity"/>, <see cref="ScreenFx01"/>, …) are plain field reads after the first
/// load, safe to call every frame from any system. In edit mode they read the file once (never write) or the defaults.</para>
/// </summary>
public static class GameSettings
{
    /// <summary>Section names for <see cref="Edit"/>, <see cref="Reset"/> and <see cref="Changed"/>.</summary>
    public const string Audio = "audio", Controls = "controls", Video = "video", Interface = "ui", Access = "access",
                        TownRules = "townRules", All = "all";

    /// <summary>Serializer settings: enums as strings, bad values skipped (the field keeps its default).</summary>
    public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Error = (sender, args) => args.ErrorContext.Handled = true,
        Formatting = Formatting.Indented,
    };

    static GameSettingsData data;
    static JObject retained;
    static bool dirty, loading;
    static float lastSaveAt = -10f;
    static float onFootBaseFov = DefaultOnFootFov;
    static bool damagedToastPending;

    /// <summary>The PlayerFollowCamera's authored vertical FOV (until FovApplier sees the real one).</summary>
    public const float DefaultOnFootFov = 40f;

    /// <summary>Minimum seconds between background saves (§6.4).</summary>
    public const float SaveInterval = 1f;

    /// <summary>The live settings (loaded on first use). Change them only through <see cref="Edit"/>.</summary>
    public static GameSettingsData Current
    {
        get
        {
            if (data == null) Load();
            return data;
        }
    }

    /// <summary>Same object as <see cref="Current"/> (a name that doesn't read as a world singleton).</summary>
    public static GameSettingsData Values => Current;

    /// <summary>A section changed (its name, or <see cref="All"/> after a load). Raised after the change is in place.</summary>
    public static event Action<string> Changed;

    /// <summary>Unsaved changes are waiting.</summary>
    public static bool Dirty => dirty;

    /// <summary>
    /// Change settings: <paramref name="change"/> edits the live object, then it's clamped, <see cref="Changed"/> fires with
    /// <paramref name="section"/> and a save is scheduled.
    /// </summary>
    public static void Edit(string section, Action<GameSettingsData> change)
    {
        var d = Current;
        try { change?.Invoke(d); }
        catch (Exception e) { Debug.LogException(e); }
        d.Sanitize();
        Raise(section ?? All);
        SaveSoon();
    }

    /// <summary>Put one section (or <see cref="All"/>) back to its defaults, apply and schedule a save.</summary>
    public static void Reset(string section)
    {
        var d = Current;
        var def = new GameSettingsData();
        switch (section)
        {
            case Audio: d.audio = def.audio; break;
            case Controls: d.controls = def.controls; break;
            case Video: d.video = def.video; break;
            case Interface: d.ui = def.ui; break;
            case Access: d.access = def.access; break;
            case TownRules: d.townRules = def.townRules; break;
            default:
                d.audio = def.audio; d.controls = def.controls; d.video = def.video; d.ui = def.ui; d.access = def.access;
                d.townRules = def.townRules;
                section = All;
                break;
        }
        d.Sanitize();
        Raise(section);
        SaveSoon();
    }

    /// <summary>Replace the whole model (keep/revert dialogs restore a snapshot this way).</summary>
    public static void Restore(GameSettingsData snapshot, string section)
    {
        if (snapshot == null) return;
        data = snapshot.Clone();
        data.Sanitize();
        Raise(section ?? All);
        SaveSoon();
    }

    /// <summary>Mark for saving; the applier writes at most once per <see cref="SaveInterval"/>.</summary>
    public static void SaveSoon() => dirty = true;

    /// <summary>Called every frame by the applier: writes when dirty and the interval has passed.</summary>
    public static void TickSave(float unscaledNow)
    {
        if (!dirty || unscaledNow - lastSaveAt < SaveInterval) return;
        lastSaveAt = unscaledNow;
        SaveNow();
    }

    /// <summary>Write now if anything changed (Back, quit, leaving play mode). True = written or nothing to write.</summary>
    public static bool SaveNow(bool force = false)
    {
        if (data == null || (!dirty && !force)) return true;
        string json = ToJson();
        if (!SettingsStore.Write(json)) return false;
        dirty = false;
        return true;
    }

    /// <summary>The file text a save would write: the retained JSON with the known model merged over it.</summary>
    public static string ToJson()
    {
        var d = Current;
        var root = retained != null ? (JObject)retained.DeepClone() : new JObject();
        var known = JObject.FromObject(d, JsonSerializer.Create(Json));
        root.Merge(known, new JsonMergeSettings
        {
            MergeArrayHandling = MergeArrayHandling.Replace,
            MergeNullValueHandling = MergeNullValueHandling.Ignore,
        });
        retained = root;
        return root.ToString(Formatting.Indented);
    }

    /// <summary>(Re)load from the store. Defaults when there's no file or it's unreadable (one toast in play mode).</summary>
    public static void Load()
    {
        if (loading) return;
        loading = true;
        try
        {
            retained = null;
            GameSettingsData d = null;
            if (SettingsStore.TryLoad(out string json)) d = FromJson(json, out retained);
            data = d ?? new GameSettingsData();
            data.Sanitize();
            dirty = false;
            var r = SettingsStore.LastLoad;
            damagedToastPending = Application.isPlaying &&
                                  (r == SettingsStore.LoadResult.DamagedUsingDefaults || r == SettingsStore.LoadResult.RecoveredFromBackup);
            // A recovered or reset file is rewritten at the next save, so the next launch reads a good main file.
            if (r == SettingsStore.LoadResult.DamagedUsingDefaults || r == SettingsStore.LoadResult.RecoveredFromBackup) dirty = true;
        }
        finally { loading = false; }
        Raise(All);
    }

    /// <summary>Parse a settings file. Bad fields keep their defaults; the whole object is returned in <paramref name="root"/>.</summary>
    public static GameSettingsData FromJson(string json, out JObject root)
    {
        root = null;
        try
        {
            root = JToken.Parse(json) as JObject;
            if (root == null) return null;
            var d = new GameSettingsData();
            // Section by section, so one broken section can't take the others down.
            var ser = JsonSerializer.Create(Json);
            using (var r = root.CreateReader()) ser.Populate(r, d);
            d.Sanitize();
            return d;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Settings] Couldn't read settings: {e.Message}. Using defaults.");
            return null;
        }
    }

    /// <summary>The loaded file has this section (e.g. "video": a fresh install has none, so startup keeps the build's display).</summary>
    public static bool FileHas(string section)
    {
        if (data == null) Load();
        return retained != null && retained[section] is JObject;
    }

    /// <summary>Show the "Settings were reset" toast once, when a HUD can show it.</summary>
    internal static void FlushDamagedToast()
    {
        if (!damagedToastPending) return;
        damagedToastPending = false;
        GameHUD.Toast(SettingsStore.LastLoad == SettingsStore.LoadResult.RecoveredFromBackup
            ? UiText.SettingsRecovered : UiText.SettingsReset, ToastStyle.Bad, 4f);
    }

    static void Raise(string section)
    {
        try { Changed?.Invoke(section); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Forget everything (a new play session, or a test switching data roots). The next read loads again.</summary>
    public static void Unload()
    {
        data = null;
        retained = null;
        dirty = false;
        lastSaveAt = -10f;
        damagedToastPending = false;
    }

    // ------------------------------------------------------------------ cheap reads (no allocation after the first load)

    /// <summary>Look sensitivity multiplier (0.25–3).</summary>
    public static float LookSensitivity => Current.controls.lookSensitivity;
    public static bool InvertY => Current.controls.invertY;

    /// <summary>Degrees (vertical) to add to every gameplay camera's authored FOV. 0 at the default.</summary>
    public static float FovDelta
    {
        get
        {
            int h = Current.controls.fov;
            if (h <= 0) return 0f;
            return VerticalFov(h) - onFootBaseFov;
        }
    }

    /// <summary>Screen effects intensity 0–1 (rabies, shake, hit-stop below 0.25 = off, FOV kicks).</summary>
    public static float ScreenFx01 => Current.access.screenFx * 0.01f;
    /// <summary>Master volume 0–1.</summary>
    public static float MasterVolume => Current.audio.master * 0.01f;
    /// <summary>Sound-effects volume 0–1 (Feel's per-play multiplier, category SFX).</summary>
    public static float SfxVolume => Current.audio.sfx * 0.01f;
    /// <summary>Ambience volume 0–1 (cricket, owl, rooster, the rain and wind beds).</summary>
    public static float AmbienceVolume => Current.audio.ambience * 0.01f;
    /// <summary>Music volume 0–1: every music source follows this and only this (the background tracks, the Lucky Raccoon's
    /// house band, the workshop radio's tune); the Sound effects and Ambience sliders never scale music.</summary>
    public static float MusicVolume => Current.audio.music * 0.01f;
    public static bool MuteWhenUnfocused => Current.audio.muteUnfocused;
    public static bool ColorblindMarkers => Current.access.colorblind;
    public static bool ReduceFlashing => Current.access.reduceFlashing;
    /// <summary>Toast duration multiplier (1, 1.5, 2).</summary>
    public static float ToastTime => Current.ui.toastTime;
    /// <summary>UI scale factor (0.8–1.5).</summary>
    public static float UiScale01 => Current.ui.scale * 0.01f;
    /// <summary>Minimap lid diameter in reference px (200 / 250 / 300).</summary>
    public static float MinimapDiameter => Current.ui.minimapSize == MinimapSize.S ? 200f : Current.ui.minimapSize == MinimapSize.L ? 300f : 250f;
    public static bool MinimapNorthUp => Current.ui.minimapNorthUp;
    public static bool MinimapStreetLabel => Current.ui.minimapStreetLabel;
    public static bool MapKeyToggle => Current.ui.mapKeyToggle;
    public static bool MapKeyPeek => Current.ui.mapKeyPeek;
    public static bool ControlHints => Current.ui.controlHints;
    public static bool StreamerMode => Current.ui.streamerMode;

    // ------------------------------------------------------------------ field of view

    /// <summary>The on-foot camera's authored vertical FOV (FovApplier reports the real one when it first sees it).</summary>
    public static float OnFootBaseFov => onFootBaseFov;

    internal static void SetOnFootBaseFov(float verticalDegrees)
    {
        if (verticalDegrees < 1f || verticalDegrees > 170f || Mathf.Approximately(verticalDegrees, onFootBaseFov)) return;
        onFootBaseFov = verticalDegrees;
        Raise(Controls);
    }

    /// <summary>Horizontal degrees at 16:9 → vertical degrees.</summary>
    public static float VerticalFov(float horizontal16x9) =>
        2f * Mathf.Atan(Mathf.Tan(horizontal16x9 * 0.5f * Mathf.Deg2Rad) * (9f / 16f)) * Mathf.Rad2Deg;

    /// <summary>Vertical degrees → horizontal degrees at 16:9 (what the slider shows).</summary>
    public static float HorizontalFov(float vertical) =>
        2f * Mathf.Atan(Mathf.Tan(vertical * 0.5f * Mathf.Deg2Rad) * (16f / 9f)) * Mathf.Rad2Deg;

    /// <summary>The slider's value for the current setting (the authored FOV shown as horizontal degrees when 0).</summary>
    public static int ShownFov => Current.controls.fov > 0 ? Current.controls.fov : Mathf.RoundToInt(HorizontalFov(onFootBaseFov));

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Unload();
        onFootBaseFov = DefaultOnFootFov;
        Changed = null;
    }
}

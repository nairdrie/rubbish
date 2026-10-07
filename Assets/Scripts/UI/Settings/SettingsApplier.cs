using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Applies <see cref="GameSettings"/> to the running game (Docs/UIPass.md §6.2, §6.5): audio, look, FOV, video and UI scale,
/// live as they change, and runs the once-a-second save. A hidden DontDestroyOnLoad runner created before the first scene
/// (never in batchmode: bots keep the build's defaults).
/// <para><b>Graphics quality</b> never edits the URP asset: a preset other than High (today's asset) is applied to a
/// <b>runtime clone</b> (<c>Instantiate</c> of the current pipeline asset, DontSave) assigned to
/// <c>QualitySettings.renderPipeline</c>. High with the preset's render scale puts the original asset back.</para>
/// <para><b>Editor safety (hard rule, §6.5).</b> In the Editor only runtime objects change: the URP clone (and the original
/// is restored on ExitingPlayMode), the look overrides and the camera lenses. Display mode, resolution, VSync, frame cap and
/// LOD bias are logged, not applied, so <c>ProjectSettings/QualitySettings.asset</c> and <c>PC_RPAsset.asset</c> stay
/// byte-identical (check UP0-4).</para>
/// </summary>
[DefaultExecutionOrder(-60)]
public sealed class SettingsApplier : MonoBehaviour
{
    static SettingsApplier instance;
    static bool focused = true;

    // URP clone bookkeeping
    static RenderPipelineAsset originalQualityRp;
    static bool capturedOriginal;
    static UniversalRenderPipelineAsset clone;
#pragma warning disable CS0414 // one of these is unused in each of Editor / player builds
    static float originalLodBias = -1f;
    static string lastEditorVideoLog;
#pragma warning restore CS0414

    /// <summary>The live runner (null in edit mode / batchmode).</summary>
    public static SettingsApplier Instance => instance;

    /// <summary>The URP asset the game is drawing with now (the clone while a non-High preset is applied).</summary>
    public static RenderPipelineAsset ActivePipeline => QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;

    /// <summary>A runtime clone is in use (tests).</summary>
    public static bool UsingClone => clone != null && QualitySettings.renderPipeline == clone;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode || instance != null) return;
        var go = new GameObject("[Settings]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SettingsApplier>();
    }

    void Awake()
    {
        GameSettings.Load();
        GameSettings.Changed += OnChanged;
        Application.focusChanged += OnFocus;
        Application.quitting += OnQuitting;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        focused = Application.isFocused;
        ApplyAll(true);
    }

    void OnDestroy()
    {
        GameSettings.Changed -= OnChanged;
        Application.focusChanged -= OnFocus;
        Application.quitting -= OnQuitting;
        if (instance == this) instance = null;
    }

    void Update()
    {
        float now = Time.unscaledTime;
        LookApplier.Tick();
        FovApplier.Tick(now);
        GameSettings.TickSave(now);
        if (PlayerController.Local != null) GameSettings.FlushDamagedToast(); // coop:allow(local) HUD only: show the toast once a HUD is up
    }

    void OnChanged(string section)
    {
        bool all = section == GameSettings.All;
        if (all || section == GameSettings.Audio) ApplyAudio();
        if (all || section == GameSettings.Controls) { LookApplier.Refresh(); FovApplier.Refresh(); }
        if (all || section == GameSettings.Video) ApplyVideo(GameSettings.Values.video, false);
        if (all || section == GameSettings.Interface) UiScale.Refresh();
    }

    void ApplyAll(bool startup)
    {
        ApplyAudio();
        LookApplier.Refresh();
        FovApplier.Refresh();
        ApplyVideo(GameSettings.Values.video, startup);
        UiScale.Refresh();
    }

    static void OnFocus(bool f)
    {
        focused = f;
        ApplyAudio();
    }

    // ------------------------------------------------------------------ audio

    /// <summary>Master → AudioListener.volume (× 0 while unfocused if that's on; builds only, so the Editor never goes quiet
    /// while you're in another window watching an agent).</summary>
    public static void ApplyAudio()
    {
        var a = GameSettings.Values.audio;
        float v = a.master * 0.01f;
        if (a.muteUnfocused && !focused && !Application.isEditor) v = 0f;
        AudioListener.volume = v;
    }

    // ------------------------------------------------------------------ video

    /// <summary>Display mode, resolution, VSync, frame cap and quality. At startup the display mode and resolution are only
    /// applied if the file had a video section (so a fresh install keeps the build's defaults).</summary>
    public static void ApplyVideo(GameSettingsData.VideoS v, bool startup)
    {
        if (v == null) return;
        ApplyQuality(v.quality, v.renderScale);
#if UNITY_EDITOR
        string line = $"[Settings] Editor: display {v.displayMode} {ResolutionLabel(v.width, v.height)}, VSync {(v.vsync ? "on" : "off")}, " +
                      $"frame cap {(v.frameCap > 0 ? v.frameCap.ToString() : "unlimited")}, LOD bias ×{LodBiasScale(v.quality):0.##}: logged, not applied.";
        if (line != lastEditorVideoLog) { lastEditorVideoLog = line; Debug.Log(line); }
#else
        QualitySettings.vSyncCount = v.vsync ? 1 : 0;
        Application.targetFrameRate = v.frameCap > 0 ? v.frameCap : -1;
        if (!startup || GameSettings.FileHas(GameSettings.Video)) ApplyDisplay(v);
        if (originalLodBias < 0f) originalLodBias = QualitySettings.lodBias;
        QualitySettings.lodBias = originalLodBias * LodBiasScale(v.quality);
#endif
    }

    /// <summary>The fullscreen mode for a setting.</summary>
    public static FullScreenMode ModeFor(DisplayModeSetting m) => m switch
    {
        DisplayModeSetting.Exclusive => FullScreenMode.ExclusiveFullScreen,
        DisplayModeSetting.Windowed => FullScreenMode.Windowed,
        _ => FullScreenMode.FullScreenWindow,
    };

    static void ApplyDisplay(GameSettingsData.VideoS v)
    {
        var mode = ModeFor(v.displayMode);
        Vector2Int r = ResolveResolution(v.width, v.height, mode);
        var rr = Screen.currentResolution.refreshRateRatio;
        foreach (var res in Screen.resolutions)
            if (res.width == r.x && res.height == r.y && res.refreshRateRatio.value > rr.value) rr = res.refreshRateRatio;
        if (Screen.width == r.x && Screen.height == r.y && Screen.fullScreenMode == mode) return;
        Screen.SetResolution(r.x, r.y, mode, rr);
        Debug.Log($"[Settings] Display: {v.displayMode} {r.x}×{r.y}.");
    }

    /// <summary>The desktop's resolution (what "Native" means).</summary>
    public static Vector2Int NativeResolution
    {
        get
        {
            int w = Display.main != null ? Display.main.systemWidth : Screen.currentResolution.width;
            int h = Display.main != null ? Display.main.systemHeight : Screen.currentResolution.height;
            if (w <= 0 || h <= 0) { w = Screen.currentResolution.width; h = Screen.currentResolution.height; }
            return new Vector2Int(w, h);
        }
    }

    /// <summary>0×0 = native (1280×720 windowed, per WindowsBuild.md); a size this PC doesn't list falls back the same way.</summary>
    public static Vector2Int ResolveResolution(int w, int h, FullScreenMode mode)
    {
        if (w > 0 && h > 0)
        {
            foreach (var r in Screen.resolutions) if (r.width == w && r.height == h) return new Vector2Int(w, h);
            if (mode == FullScreenMode.Windowed && w <= NativeResolution.x && h <= NativeResolution.y) return new Vector2Int(w, h);
        }
        if (mode == FullScreenMode.Windowed) return new Vector2Int(Mathf.Min(1280, NativeResolution.x), Mathf.Min(720, NativeResolution.y));
        return NativeResolution;
    }

    /// <summary>The resolutions this PC offers, deduplicated by size (highest refresh kept), smallest first.</summary>
    public static List<Vector2Int> Resolutions()
    {
        var list = new List<Vector2Int>();
        foreach (var r in Screen.resolutions)
        {
            var v = new Vector2Int(r.width, r.height);
            if (r.width >= 640 && r.height >= 360 && !list.Contains(v)) list.Add(v);
        }
        list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return list;
    }

    public static string ResolutionLabel(int w, int h) => w > 0 && h > 0 ? $"{w} × {h}" : UiText.Native;

    // ------------------------------------------------------------------ quality presets

    /// <summary>One row of the §6.2 preset table.</summary>
    public readonly struct Preset
    {
        public readonly float renderScale, shadowDistance, lodBiasScale;
        public readonly int cascades, mainShadowRes, extraLights, msaa;
        public Preset(float rs, float sd, int c, int res, int lights, int msaa, float lod)
        {
            renderScale = rs; shadowDistance = sd; cascades = c; mainShadowRes = res; extraLights = lights; this.msaa = msaa; lodBiasScale = lod;
        }
    }

    /// <summary>The preset table (§6.2). High is today's PC_RPAsset. LOD bias is a multiplier of the project's (2.0 today).</summary>
    public static Preset PresetFor(QualityPreset q) => q switch
    {
        QualityPreset.Low => new Preset(0.75f, 25f, 1, 1024, 2, 1, 0.7f),
        QualityPreset.Medium => new Preset(0.9f, 40f, 2, 2048, 4, 1, 1f),
        QualityPreset.Ultra => new Preset(1f, 80f, 4, 4096, 8, 2, 1.5f),
        _ => new Preset(1f, 50f, 4, 2048, 4, 1, 1f),
    };

    static float LodBiasScale(QualityPreset q) => PresetFor(q).lodBiasScale;

    /// <summary>The render scale a setting resolves to (0 = the preset's).</summary>
    public static float RenderScaleFor(QualityPreset q, int renderScalePercent) =>
        renderScalePercent > 0 ? Mathf.Clamp(renderScalePercent, 50, 100) * 0.01f : PresetFor(q).renderScale;

    /// <summary>Apply a preset (+ render scale override) through a runtime clone; High at its own render scale = the original.</summary>
    public static void ApplyQuality(QualityPreset q, int renderScalePercent)
    {
        if (!Application.isPlaying) return; // never in edit mode
        CaptureOriginal();
        var original = OriginalPipeline as UniversalRenderPipelineAsset;
        if (original == null) return;
        float rs = RenderScaleFor(q, renderScalePercent);
        bool identity = q == QualityPreset.High && Mathf.Approximately(rs, original.renderScale);
        if (identity)
        {
            if (QualitySettings.renderPipeline != originalQualityRp) QualitySettings.renderPipeline = originalQualityRp;
            DestroyClone();
            return;
        }
        if (clone == null)
        {
            clone = Instantiate(original);
            clone.name = original.name + " (runtime settings)";
            clone.hideFlags = HideFlags.DontSave;
        }
        var p = PresetFor(q);
        clone.renderScale = rs;
        clone.shadowDistance = p.shadowDistance;
        clone.shadowCascadeCount = p.cascades;
        clone.mainLightShadowmapResolution = p.mainShadowRes;
        clone.maxAdditionalLightsCount = p.extraLights;
        clone.msaaSampleCount = p.msaa;
        if (QualitySettings.renderPipeline != clone) QualitySettings.renderPipeline = clone;
    }

    /// <summary>The project's pipeline asset (never the clone).</summary>
    public static RenderPipelineAsset OriginalPipeline
    {
        get
        {
            CaptureOriginal();
            return originalQualityRp != null ? originalQualityRp : GraphicsSettings.defaultRenderPipeline;
        }
    }

    static void CaptureOriginal()
    {
        if (capturedOriginal) return;
        capturedOriginal = true;
        originalQualityRp = QualitySettings.renderPipeline;
    }

    static void DestroyClone()
    {
        if (clone == null) return;
        var c = clone;
        clone = null;
        if (Application.isPlaying) Destroy(c);
        else DestroyImmediate(c);
    }

    /// <summary>
    /// Dry run for a preset (acceptance UP0-3): every value the clone would get next to the original's, and whether URP
    /// 17.3 lets us set it (read-only properties are reported and skipped).
    /// </summary>
    public static string Describe(QualityPreset q, int renderScalePercent = 0)
    {
        var original = OriginalPipeline as UniversalRenderPipelineAsset;
        var p = PresetFor(q);
        var sb = new StringBuilder();
        sb.Append($"{q}: ");
        if (original == null) return sb.Append("no URP asset").ToString();
        void Row(string prop, object from, object to)
        {
            var pi = typeof(UniversalRenderPipelineAsset).GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            bool writable = pi != null && pi.SetMethod != null && pi.SetMethod.IsPublic;
            sb.Append($"{prop} {from}→{to}{(writable ? "" : " (READ-ONLY, skipped)")}; ");
        }
        Row(nameof(UniversalRenderPipelineAsset.renderScale), original.renderScale, RenderScaleFor(q, renderScalePercent));
        Row(nameof(UniversalRenderPipelineAsset.shadowDistance), original.shadowDistance, p.shadowDistance);
        Row(nameof(UniversalRenderPipelineAsset.shadowCascadeCount), original.shadowCascadeCount, p.cascades);
        Row(nameof(UniversalRenderPipelineAsset.mainLightShadowmapResolution), original.mainLightShadowmapResolution, p.mainShadowRes);
        Row(nameof(UniversalRenderPipelineAsset.maxAdditionalLightsCount), original.maxAdditionalLightsCount, p.extraLights);
        Row(nameof(UniversalRenderPipelineAsset.msaaSampleCount), original.msaaSampleCount, p.msaa);
        float lod = QualitySettings.lodBias;
        sb.Append($"lodBias {lod}→{lod * p.lodBiasScale} (×{p.lodBiasScale}; builds only)");
        bool identity = q == QualityPreset.High && Mathf.Approximately(RenderScaleFor(q, renderScalePercent), original.renderScale);
        sb.Append(identity ? " [original asset, no clone]" : " [runtime clone]");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ leaving

    static void OnQuitting()
    {
        GameSettings.SaveNow();
        RestoreRuntime();
    }

#if UNITY_EDITOR
    static void OnPlayModeChanged(UnityEditor.PlayModeStateChange s)
    {
        if (s != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
        GameSettings.SaveNow();
        RestoreRuntime();
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
    }
#endif

    /// <summary>Undo every runtime change (Editor: before leaving play mode, so nothing reaches the project).</summary>
    public static void RestoreRuntime()
    {
        if (capturedOriginal && QualitySettings.renderPipeline != originalQualityRp)
        {
            QualitySettings.renderPipeline = originalQualityRp;
            Debug.Log("[Settings] Restored the project's render pipeline asset.");
        }
        DestroyClone();
#if !UNITY_EDITOR
        if (originalLodBias >= 0f) QualitySettings.lodBias = originalLodBias;
#endif
        LookApplier.RestoreAll();
        FovApplier.RestoreAll();
        AudioListener.volume = 1f;
        capturedOriginal = false;
        originalQualityRp = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        focused = true;
        capturedOriginal = false;
        originalQualityRp = null;
        clone = null;
        originalLodBias = -1f;
        lastEditorVideoLog = null;
    }
}

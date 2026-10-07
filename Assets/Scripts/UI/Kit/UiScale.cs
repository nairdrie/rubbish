using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Interface → UI scale setting (Docs/UIPass.md §6.2, §8.6): one factor (0.8–1.5) applied to every registered
/// <see cref="CanvasScaler"/> by setting its reference resolution to 1920×1080 ÷ factor, so every canvas grows together and
/// layouts (which work in reference units) see a smaller reference width at large scales. Callers register their scaler once
/// (FeelHUD, ProgressionHud, the map, menus, pause, settings, DistrictWelcome) and never touch the reference resolution again.
/// Edit-mode previews (scaler disabled, manual <c>scaleFactor</c>) use <see cref="PreviewScaleFactor"/>.
/// </summary>
public static class UiScale
{
    /// <summary>The reference resolution at 100%.</summary>
    public static readonly Vector2 Reference = new Vector2(1920f, 1080f);
    public const float Min = 0.8f, Max = 1.5f;

    static readonly List<CanvasScaler> scalers = new List<CanvasScaler>();
    static float applied = -1f;

    /// <summary>Current UI scale factor (1 = 100%), from <see cref="GameSettings"/>.</summary>
    public static float Factor => Mathf.Clamp(GameSettings.UiScale01, Min, Max);

    /// <summary>Raised after the factor changed and every registered scaler was updated.</summary>
    public static event Action Changed;

    /// <summary>Scale <paramref name="s"/> with the setting from now on (idempotent). Also sets ScaleWithScreenSize.</summary>
    public static void Register(CanvasScaler s)
    {
        if (s == null) return;
        if (!scalers.Contains(s)) scalers.Add(s);
        ApplyTo(s, Factor);
    }

    public static void Unregister(CanvasScaler s)
    {
        if (s == null) return;
        scalers.Remove(s);
    }

    /// <summary>Registered scalers still alive (diagnostics/tests).</summary>
    public static int RegisteredCount
    {
        get
        {
            scalers.RemoveAll(x => x == null);
            return scalers.Count;
        }
    }

    /// <summary>Re-apply the setting to every registered scaler (SettingsApplier calls this on a ui change).</summary>
    public static void Refresh()
    {
        float f = Factor;
        scalers.RemoveAll(x => x == null);
        for (int i = 0; i < scalers.Count; i++) ApplyTo(scalers[i], f);
        if (!Mathf.Approximately(f, applied))
        {
            applied = f;
            try { Changed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void ApplyTo(CanvasScaler s, float f)
    {
        s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        s.referenceResolution = Reference / Mathf.Max(0.01f, f);
        // Every canvas in the game matches width and height half-and-half (the HUD's choice).
        s.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        s.matchWidthOrHeight = 0.5f;
    }

    /// <summary>
    /// The canvas scale factor a ScaleWithScreenSize scaler (match 0.5) would use on a <paramref name="w"/>×<paramref name="h"/>
    /// screen at UI scale <paramref name="uiScale"/> (default: the setting). For edit-mode previews that render through a
    /// camera with the scaler disabled.
    /// </summary>
    public static float PreviewScaleFactor(int w, int h, float uiScale = -1f)
    {
        if (uiScale <= 0f) uiScale = Factor;
        Vector2 refRes = Reference / Mathf.Max(0.01f, uiScale);
        float logW = Mathf.Log(Mathf.Max(1, w) / refRes.x, 2f);
        float logH = Mathf.Log(Mathf.Max(1, h) / refRes.y, 2f);
        return Mathf.Pow(2f, Mathf.Lerp(logW, logH, 0.5f));
    }

    /// <summary>The canvas size in reference units on a <paramref name="w"/>×<paramref name="h"/> screen (what layouts see).</summary>
    public static Vector2 ReferenceSize(int w, int h, float uiScale = -1f)
    {
        float s = PreviewScaleFactor(w, h, uiScale);
        return new Vector2(w / s, h / s);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        scalers.Clear();
        applied = -1f;
        Changed = null;
    }
}

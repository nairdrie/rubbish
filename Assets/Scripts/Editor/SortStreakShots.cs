using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screenshots of the sort-streak HUD (SortStreakHud) without playing: the HUD is built in edit mode, driven through each moment
/// with fixed time steps, turned into a 1920x1080 world-space canvas and rendered to a PNG, like UiShots. Menu: Rubbish/Debug/UI
/// Shots/Sort Streak. Batch: <c>Unity -batchmode -projectPath P -executeMethod SortStreakShots.RunBatch -logFile L</c>, folder in
/// <c>UISHOTS_OUT</c>. Layout and animation shape only: no scene, no sound, no input.
/// </summary>
public static class SortStreakShots
{
    const int W = 1920, H = 1080;
    const float Dt = 1f / 60f;
    static readonly Color Dark = new Color(0.12f, 0.16f, 0.2f), Mid = new Color(0.30f, 0.46f, 0.38f), Bright = new Color(0.93f, 0.92f, 0.82f);

    [MenuItem("Rubbish/Debug/UI Shots/Sort Streak")]
    public static void RunMenu()
    {
        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp", "UiShots");
        RunTo(dir);
        EditorUtility.RevealInFinder(dir);
    }

    public static void RunBatch()
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("UISHOTS_OUT");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp", "UiShots");
            RunTo(dir);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    public static void RunTo(string dir)
    {
        Directory.CreateDirectory(dir);
        HudLayout.Publish(HudLayout.Compute(new HudLayout.Inputs { size = new Vector2(W, H), mode = HudMode.Station, dockWanted = 64f }));

        // sorts landing one per second climb the ladder; each shot freezes one moment of it
        Shot(dir, "streak_01_cold_2", Mid, hud => { Climb(hud, 2); Run(hud, 40); hud.Preview(2, 0.8f); Run(hud, 10); });
        Shot(dir, "streak_02_nice_x1.5_slam", Mid, hud => { Climb(hud, 3); Run(hud, 6); });
        Shot(dir, "streak_03_nice_x1.5_settled", Mid, hud => { Climb(hud, 4); Run(hud, 60); hud.Preview(4, 0.7f); Run(hud, 4); });
        Shot(dir, "streak_04_hot_x2_slam", Mid, hud => { Climb(hud, 6); Run(hud, 8); });
        Shot(dir, "streak_05_onfire_x3_settled", Mid, hud => { Climb(hud, 12); Run(hud, 90); hud.Preview(12, 0.7f); Run(hud, 4); });
        Shot(dir, "streak_06_onfire_x3_hurry", Mid, hud => { Climb(hud, 12); Run(hud, 90); hud.Preview(12, 0.12f); Run(hud, 7); });
        Shot(dir, "streak_07_unstoppable_x4_slam", Mid, hud => { Climb(hud, 15); Run(hud, 7); });
        Shot(dir, "streak_08_legendary_x5_settled", Mid, hud => { Climb(hud, 25); Run(hud, 120); hud.Preview(25, 0.6f); Run(hud, 4); });
        Shot(dir, "streak_09_trashgod_x10_slam", Mid, hud => { Climb(hud, 35); Run(hud, 9); });
        Shot(dir, "streak_10_trashgod_x10_later", Mid, hud => { Climb(hud, 38); Run(hud, 130); hud.Preview(38, 0.55f); Run(hud, 4); });
        Shot(dir, "streak_11_lost_early", Mid, hud => { Climb(hud, 14); Run(hud, 60); hud.PreviewLost(14); Run(hud, 14); });
        Shot(dir, "streak_12_lost_falling", Mid, hud => { Climb(hud, 14); Run(hud, 60); hud.PreviewLost(14); Run(hud, 55); });
        Shot(dir, "streak_13_onfire_on_bright", Bright, hud => { Climb(hud, 12); Run(hud, 90); hud.Preview(12, 0.7f); Run(hud, 4); });
        Shot(dir, "streak_14_trashgod_on_dark", Dark, hud => { Climb(hud, 38); Run(hud, 130); hud.Preview(38, 0.55f); Run(hud, 4); });
        Shot(dir, "streak_15_depot_mode", Mid, hud => { hud.Preview(12, 0.7f, HudMode.Depot); Climb(hud, 12, HudMode.Depot); Run(hud, 90); });
        Debug.Log("[SortStreakShots] wrote " + dir);
    }

    /// <summary>Sort correctly <paramref name="streak"/> times (a tenth of a second between, so every tier up fires its moment).</summary>
    static void Climb(SortStreakHud hud, int streak, HudMode mode = HudMode.Station)
    {
        hud.Preview(0, 0f, mode);
        for (int i = 1; i <= streak; i++)
        {
            hud.PreviewSort(i);
            if (i < streak) Run(hud, 6);
        }
    }

    static void Run(SortStreakHud hud, int frames)
    {
        for (int i = 0; i < frames; i++) hud.Tick(Dt);
    }

    static void Shot(string dir, string name, Color backdrop, Action<SortStreakHud> drive)
    {
        SortStreakHud hud = null;
        try
        {
            hud = SortStreakHud.CreateForPreview();
            var canvas = hud.GetComponent<Canvas>();
            foreach (var s in canvas.GetComponentsInChildren<CanvasScaler>(true)) s.enabled = false;
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)canvas.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(W, H);
            rt.localScale = Vector3.one;
            rt.position = Vector3.zero;
            canvas.enabled = true;
            Canvas.ForceUpdateCanvases();

            drive(hud);
            Canvas.ForceUpdateCanvases();
            foreach (var t in hud.GetComponentsInChildren<TMP_Text>(true)) t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
            Render(dir, name, backdrop);
        }
        finally
        {
            if (hud != null) UnityEngine.Object.DestroyImmediate(hud.gameObject);
        }
    }

    static void Render(string dir, string name, Color backdrop)
    {
        var camGo = new GameObject("shot cam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = H * 0.5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = backdrop;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 500f;
        cam.cullingMask = 1 << 5;
        cam.transform.position = new Vector3(0f, 0f, -100f);
        var rtex = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        var prev = RenderTexture.active;
        try
        {
            cam.targetTexture = rtex;
            cam.Render();
            RenderTexture.active = rtex;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(rtex);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}

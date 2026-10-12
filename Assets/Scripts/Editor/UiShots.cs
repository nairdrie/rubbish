using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screenshots of the contract and tutorial UI without playing the game: each canvas is turned into a 1920x1080 world-space canvas
/// in front of an orthographic camera (1 canvas unit = 1 pixel) and rendered to a PNG. Menu: Rubbish/Debug/UI Shots. Batch:
/// <c>Unity -batchmode -projectPath P -executeMethod UiShots.RunBatch -logFile L</c> with the folder in <c>UISHOTS_OUT</c>.
/// Only the layout is checked this way: no scene, no sound, no input.
/// </summary>
public static class UiShots
{
    const int W = 1920, H = 1080;
    static readonly Color Backdrop = new Color(0.20f, 0.34f, 0.30f);

    [MenuItem("Rubbish/Debug/UI Shots/Contract And Tutorial")]
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
        Reset(typeof(Campaign));
        Reset(typeof(GameClock));
        GameClock.DebugSkipHours(30f);                     // a clock that is well past zero, so a hold anchor can sit before it
        var acres = Campaign.ContractFor("mildly_pleasant");
        var meadows = Campaign.ContractFor("minivan_meadows");
        int acresIdx = Districts.IndexOf("mildly_pleasant");
        double now = GameClock.TotalHours;

        // ---- the contract bar in the left column, in each state
        Bar(dir, "bar_1_start", Disp(acresIdx, 56, 56, 0, 0, 0, 0));
        Bar(dir, "bar_2_half", Disp(acresIdx, 28, 56, 52, 0, 0, 0));
        Bar(dir, "bar_3_last_bags", Disp(acresIdx, 3, 56, 89, 0, 0, 0));
        Bar(dir, "bar_4_get_over", Disp(acresIdx, 0, 56, 84, 0, 0, 0));
        Bar(dir, "bar_5_holding", Disp(acresIdx, 0, 56, 94, 1, now - 5.3, 0));
        Bar(dir, "bar_6_holding_late", Disp(acresIdx, 0, 56, 91, 1, now - 22.4, 0));
        Bar(dir, "bar_7_lost", Disp(acresIdx, 0, 56, 71, 0, 0, 1), lostFlash: true);
        Bar(dir, "bar_8_highlight", Disp(acresIdx, 56, 56, 0, 0, 0, 0), highlight: true);

        // ---- the intro cards
        for (int i = 0; i < 3; i++) Cards(dir, $"cards_acres_{i + 1}", acres, true, i, Disp(acresIdx, 56, 56, 0, 0, 0, 0));
        Cards(dir, "cards_meadows", meadows, false, 0, Disp(Districts.IndexOf("minivan_meadows"), 48, 48, 0, 0, 0, 0));

        // ---- the tutorial banner and pointer
        Banner(dir, "banner_1_truck", new TutorialHud.Card { badge = "1/10", title = "GET IN YOUR TRUCK", body = "Walk up to the truck and climb in.", key = "E" }, null);
        Banner(dir, "banner_2_collect", new TutorialHud.Card { badge = "3/10", title = "GRAB THE OLD TRASH", body = "Pull up beside a heap, then grab a bag with the truck's arm. Fill the truck.", key = "LMB" }, "Old trash");
        Banner(dir, "banner_3_full", new TutorialHud.Card { badge = "4/10", title = "TRUCK FULL! BACK TO THE DEPOT", body = "Drive home to unload. Follow the arrow to the depot gate.", urgent = true }, "Depot");
        Banner(dir, "banner_4_tip", new TutorialHud.Card { badge = "6/10", title = "UNLOAD THE TRUCK", body = "Sit in the truck over the tipping floor and hold the key to tip it all out.", key = "Hold G" }, "Tipping floor");
        Banner(dir, "banner_5_done", new TutorialHud.Card { badge = "6/10", title = "UNLOAD THE TRUCK", body = "x" }, null, celebrate: "Truck unloaded!");
        Banner(dir, "banner_6_edge", new TutorialHud.Card { badge = "3/10", title = "PICK UP THE OLD TRASH", body = "Heaps of old bags are on the curbs (follow the flies). Follow the arrow.", key = null }, "Old trash", edge: true, skip: true);
        Banner(dir, "banner_7_combo", new TutorialHud.Card { badge = "3/10", title = "PICK UP THE OLD TRASH", body = "12/120 kg loaded. Grab another one with E until the truck is full. Watch the clean bar fill.", key = "E" }, "Old trash", withBar: true, skip: true);
        Debug.Log("[UiShots] wrote " + dir);
    }

    static void Reset(Type t) =>
        t.GetMethod("ResetStatics", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.Invoke(null, null);

    static CampaignDisplay Disp(int district, int left, int start, int pct, int qualifying, double holdStart, int lost) =>
        new CampaignDisplay
        {
            district = (sbyte)district, backlog = (ushort)left, backlogStart = (ushort)start, cleanPct = (byte)pct,
            qualifying = (byte)qualifying, holdStart = holdStart, lostSerial = (ushort)lost, lostTenths = (ushort)(lost > 0 ? 104 : 0),
            lastWon = -1,
        };

    // ------------------------------------------------------------------ scenes

    static Canvas WorldCanvas(string name, int order, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
        go.layer = 5;
        var c = go.GetComponent<Canvas>();
        c.renderMode = RenderMode.WorldSpace;
        c.sortingOrder = order;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(W, H);
        rt.localScale = Vector3.one;
        rt.position = new Vector3(0f, 0f, -order * 0.01f);
        return c;
    }

    /// <summary>An overlay canvas some screen built, turned into one the camera can see at the same size.</summary>
    static void Convert(Canvas c, float z)
    {
        foreach (var s in c.GetComponentsInChildren<CanvasScaler>(true)) s.enabled = false;
        c.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)c.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(W, H);
        rt.localScale = Vector3.one;
        rt.position = new Vector3(0f, 0f, z);
    }

    static Camera MakeCamera(string name)
    {
        var cam = new GameObject(name).AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = H * 0.5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Backdrop;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 500f;
        cam.cullingMask = 1 << 5;
        cam.transform.position = new Vector3(0f, 0f, -100f);
        cam.transform.rotation = Quaternion.identity;
        return cam;
    }

    static void Publish(HudMode mode)
    {
        HudLayout.Publish(HudLayout.Compute(new HudLayout.Inputs
        {
            size = new Vector2(W, H), mode = mode, minimapWanted = true, minimapDiameter = 250f, minimapLabel = true,
            leftWanted = 150f, dockWanted = 64f,
        }));
    }

    /// <summary>The city meter alone on a fake town, with the campaign display set to <paramref name="d"/>.</summary>
    static void Bar(string dir, string name, CampaignDisplay d, bool lostFlash = false, bool highlight = false)
    {
        var made = new List<GameObject>();
        try
        {
            Publish(HudMode.Driving);
            Campaign.ApplyRemote(CampaignDisplay.None);
            var canvas = WorldCanvas("shot hud", 0);
            made.Add(canvas.gameObject);
            var city = new FeelHudCity();
            city.Build((RectTransform)canvas.transform);
            city.SetTopLeft(new Vector2(HudLayout.Margin, HudLayout.ColumnTop));
            city.SetWidth(HudLayout.LeftWidth(HudLayout.Density));
            // settle on the first state, then change to the one wanted (so the "lost" flash sees a change)
            Campaign.ApplyRemote(Prior(d, lostFlash));
            float t = 0f;
            for (int i = 0; i < 40; i++) { city.Tick(true, 0f, 4, 1, false, 0.05f, t); t += 0.05f; }
            Campaign.ApplyRemote(d);
            FeelHudCity.Highlight = highlight;
            for (int i = 0; i < 100; i++) { city.Tick(true, 0f, 4, 1, false, 0.05f, t); t += 0.05f; if (lostFlash && i == 30) break; }
            Shoot(dir, name, made);
        }
        finally
        {
            FeelHudCity.Highlight = false;
            Campaign.ApplyRemote(CampaignDisplay.None);
            foreach (var go in made) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static CampaignDisplay Prior(CampaignDisplay d, bool lost)
    {
        if (!lost) return d;
        var p = d;
        p.lostSerial = 0; p.lostTenths = 0; p.qualifying = 1; p.holdStart = GameClock.TotalHours - 10.0; p.cleanPct = 92;
        return p;
    }

    static void Cards(string dir, string name, CampaignContract contract, bool first, int page, CampaignDisplay d)
    {
        var made = new List<GameObject>();
        try
        {
            Publish(HudMode.Paused);                 // a modal screen is up: the HUD is faded behind it
            Campaign.ApplyRemote(d);
            var screen = ContractIntroScreen.Preview(contract, first, page);
            made.Add(screen.gameObject);
            var sc = screen.GetComponent<Canvas>();
            Convert(sc, -5f);
            Canvas.ForceUpdateCanvases();
            screen.Refresh();
            Shoot(dir, name, made);
        }
        finally
        {
            Campaign.ApplyRemote(CampaignDisplay.None);
            foreach (var go in made) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static void Banner(string dir, string name, TutorialHud.Card card, string targetName, string celebrate = null, bool withBar = false, bool edge = false, bool skip = false)
    {
        var made = new List<GameObject>();
        try
        {
            Publish(HudMode.Driving);
            var hudCanvas = WorldCanvas("shot hud", 0);
            made.Add(hudCanvas.gameObject);
            if (withBar)
            {
                Campaign.ApplyRemote(Disp(Districts.IndexOf("mildly_pleasant"), 31, 56, 38, 0, 0, 0));
                var city = new FeelHudCity();
                city.Build((RectTransform)hudCanvas.transform);
                city.SetTopLeft(new Vector2(HudLayout.Margin, HudLayout.ColumnTop));
                city.SetWidth(HudLayout.LeftWidth(HudLayout.Density));
                float ct = 0f;
                for (int i = 0; i < 80; i++) { city.Tick(true, 0f, 4, 1, false, 0.05f, ct); ct += 0.05f; }
            }
            // a stand-in for the dock's prompt so the banner sits above something
            var prompt = new FeelHudPrompt();
            prompt.Build((RectTransform)hudCanvas.transform);
            prompt.SetDirect("E", "Grab Saggy Trash Bag (4.2 kg)", null, null, 900f);
            prompt.Tick(new Vector2((W - prompt.Size.x) * 0.5f, H - HudLayout.DockBottom - prompt.Size.y), true, 1f);

            var tutorial = TutorialHud.BuildForPreview(null);
            made.Add(tutorial.gameObject);
            var tc = tutorial.GetComponent<Canvas>();
            Convert(tc, -5f);
            var rt = (RectTransform)tc.transform;

            // a perspective camera looking at a point 30 m ahead and 12 m to the right (the pointer's target)
            var scene = new GameObject("shot scene cam").AddComponent<Camera>();
            made.Add(scene.gameObject);
            scene.enabled = false;
            scene.transform.position = new Vector3(1000f, 0f, 0f);
            scene.transform.rotation = Quaternion.identity;
            scene.fieldOfView = 60f;
            scene.aspect = W / (float)H;
            var rtex = new RenderTexture(W, H, 16);
            scene.targetTexture = rtex;
            tutorial.cameraOverride = scene;
            Vector3 target = scene.transform.position + (edge ? new Vector3(90f, -1f, 20f) : new Vector3(8f, -1f, 30f));

            tutorial.SetCard(card);
            if (skip) tutorial.SetSkip("hold U to skip the tutorial", 0.35f);
            tutorial.SetTarget(targetName != null ? target : (Vector3?)null, targetName);
            float tt = 0f;
            for (int i = 0; i < 60; i++) { tutorial.Tick(0.05f, tt); tt += 0.05f; }
            if (celebrate != null)
            {
                tutorial.Celebrate(celebrate);
                for (int i = 0; i < 14; i++) { tutorial.Tick(0.05f, tt); tt += 0.05f; }
            }
            Shoot(dir, name, made);
            scene.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rtex);
        }
        finally
        {
            Campaign.ApplyRemote(CampaignDisplay.None);
            foreach (var go in made) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // ------------------------------------------------------------------ the picture

    static void Shoot(string dir, string name, List<GameObject> canvases)
    {
        Canvas.ForceUpdateCanvases();
        foreach (var go in canvases)
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.ForceMeshUpdate();
        Canvas.ForceUpdateCanvases();

        // canvases keep their own order: lower sorting order further back
        int n = 0;
        foreach (var go in canvases)
        {
            var c = go.GetComponent<Canvas>();
            if (c != null) c.transform.position = new Vector3(0f, 0f, -n++ * 5f);
        }

        var cam = MakeCamera("shot cam");
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        var prev = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}

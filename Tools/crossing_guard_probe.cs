// Editor QA only: run with `unity command run_script --file Tools/crossing_guard_probe.cs --entry CrossingGuardProbe.Stage`.
// Runtime probes refuse real player saves. No authored scene or prefab is modified.
using System;
using System.IO;
using UnityEngine;

public static class CrossingGuardProbe
{
    public static object Stage()
    {
        if (!Application.isPlaying || !SaveRoot.IsOverridden || GameSession.IsConnectedGuest)
            throw new InvalidOperationException("Use an isolated host test-data-root play session.");
        var c = WorldLifeRunner.Context;
        if (c == null || CrossingGuardDirector.Instance == null) throw new InvalidOperationException("Outdoor world not ready.");
        double target = 0, now = GameClock.TotalHours;
        for (double hour = now + .02; hour < now + 24; hour += .005)
            if (GuardedCrossingRules.TryEvent(hour, c.TownSeed, 0, CrossingGuardDirector.Instance.SiteCount, 14.8f,
                out _, out float age, out _) && age >= 9f && age < 10f) { target = hour; break; }
        if (target == 0) throw new InvalidOperationException("No crossing was scheduled.");
        GameClock.DebugSkipHours((float)(target - GameClock.TotalHours));
        PlayerController player = null;
        foreach (var candidate in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            if (CoopPlayerHooks.IsLocalOwner(candidate)) { player = candidate; break; }
        if (player != null)
        {
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.transform.position = new Vector3(69, .2f, 52);
            if (cc != null) cc.enabled = true;
        }
        return new { targetHour = target, sites = CrossingGuardDirector.Instance.SiteCount };
    }

    public static object Capture()
    {
        if (!Application.isPlaying || !SaveRoot.IsOverridden) throw new InvalidOperationException("Use isolated test saves.");
        var go = new GameObject("Temporary crossing QA camera");
        var camera = go.AddComponent<Camera>();
        RenderTexture rt = null; Texture2D image = null;
        try
        {
            camera.transform.position = new Vector3(66f, 5.5f, 51f);
            camera.transform.LookAt(new Vector3(80f, 1f, 61f));
            camera.fieldOfView = 52; camera.nearClipPlane = .1f; camera.farClipPlane = 180f;
            camera.clearFlags = CameraClearFlags.Skybox;
            rt = new RenderTexture(1600, 1000, 24); camera.targetTexture = rt;
            camera.Render();
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
            }
            finally { RenderTexture.active = previous; }
            string path = Path.GetFullPath("Logs/crossing-guard-qa.png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return new { path, active = CrossingGuardDirector.Instance.ActiveCount,
                visible = CrossingGuardDirector.Instance.VisibleCount, hittable = CrossingGuardDirector.Instance.HittableCount };
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            if (image != null) UnityEngine.Object.DestroyImmediate(image);
            if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
        }
    }
}

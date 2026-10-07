using System;
using System.Reflection;
using UnityEngine;

/// <summary>Focused F1 check for the grip upgrades, shared Safety Bar visuals, hedge catching and trampoline bounce.
/// Run in the isolated Editor copy with -batchmode -nographics -quit -executeMethod RideFeatureCheck.Run.</summary>
public static class RideFeatureCheck
{
    public static void Run()
    {
        Check(RideHolds.Count == 4, "four holds");
        Check(RideTruck.Drain(6f, 0f, 0f) > RideTruck.Drain(3f, 0f, 0f), "cornering is the main drain");
        Check(RideTruck.Drain(0f, 7f, 0f) > 0f, "hard braking drains grip");
        Check(RideTruck.Drain(0f, 0f, 3f) > 0f, "drifting drains grip");
        Check(RideShop.GloveDrainMult(1) < 1f && RideShop.GloveDrainMult(2) < RideShop.GloveDrainMult(1),
            "both glove levels reduce drain");
        Check(RideShop.PadJoltMult(1) < 1f && RideShop.PadJoltMult(2) < RideShop.PadJoltMult(1),
            "both padded-step levels reduce jolts");
        Check(RideTuning.SafetyBarSpeedFor(1) == 8f && RideTuning.SafetyBarSpeedFor(0) == 0f,
            "Safety Bar protects only equipped trucks below threshold");
        Check(RideTuning.BarSaves(1, true, 7.99f) && !RideTuning.BarSaves(1, true, 8f) &&
            !RideTuning.BarSaves(1, false, 2f) && !RideTuning.BarSaves(0, true, 2f),
            "bar protection switches off at 8 m/s, when raised or when unequipped");
        var down = new RideModsNet { truck = 1, bar = 1, barDown = true };
        var up = new RideModsNet { truck = 1, bar = 1, barDown = false };
        Check(!down.Equals(up), "a bar flip changes replicated mod state");
        CheckBarGeometry();
        CheckHedgeAndTrampoline();
        Debug.Log("[RideFeatureCheck] PASS: grip/upgrades, shared bar visuals, hedge and trampoline flight checks");
    }

    static void CheckBarGeometry()
    {
        var build = typeof(RideTruck).GetMethod("BuildKit", BindingFlags.Instance | BindingFlags.NonPublic);
        var kitField = typeof(RideTruck).GetField("kit", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(build != null && kitField != null, "production ride-kit builder is available");
        float downY = BarY(build, kitField, true);
        float upY = BarY(build, kitField, false);
        Check(upY - downY > 0.5f, "raised Safety Bar visibly clears the rider");
    }

    static float BarY(MethodInfo build, FieldInfo kitField, bool down)
    {
        var go = new GameObject(down ? "Ride bar down check" : "Ride bar up check");
        var ride = go.AddComponent<RideTruck>();
        try
        {
            build.Invoke(ride, new object[] { 1, 1, down });
            var kit = (Transform)kitField.GetValue(ride);
            Check(kit != null && kit.Find("RearFootplate") != null && kit.Find("SideFootplate") != null &&
                kit.Find("PaddedStep") != null, "equipped kit has attached footholds and padding");
            var bar = kit.Find("SafetyBar");
            Check(bar != null, "equipped kit has a Safety Bar");
            return bar.localPosition.y;
        }
        finally
        {
            var kit = (Transform)kitField.GetValue(ride);
            kitField.SetValue(ride, null); // avoid runtime Destroy from RideTruck.OnDestroy in Edit Mode
            if (kit != null) UnityEngine.Object.DestroyImmediate(kit.gameObject);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static void CheckHedgeAndTrampoline()
    {
        var hedge = new GameObject("Hedge");
        var trampoline = new GameObject("Trampoline");
        try
        {
            hedge.transform.position = new Vector3(10000f, 1.5f, 10002f);
            hedge.transform.localScale = new Vector3(4f, 3f, 0.4f);
            hedge.AddComponent<BoxCollider>();
            trampoline.transform.position = new Vector3(10010f, 0.85f, 10000f);
            trampoline.transform.localScale = new Vector3(4f, 0.12f, 4f);
            trampoline.AddComponent<BoxCollider>();
            Physics.SyncTransforms();

            var caught = new RideFlight(new Vector3(10000f, 1.2f, 10000f), new Vector3(0f, 1f, 13f), false);
            for (int i = 0; i < 120 && !caught.Landed; i++) caught.Step();
            Check(caught.Landed && caught.Soft, "hedge catches a flung rider softly");

            var bounce = new RideFlight(new Vector3(10010f, 2.8f, 10000f), new Vector3(0f, -1f, 0f), false);
            for (int i = 0; i < 120 && bounce.Bounces == 0 && !bounce.Landed; i++) bounce.Step();
            Check(bounce.Bounces > 0 && !bounce.Soft, "trampoline bounces a falling rider");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hedge);
            UnityEngine.Object.DestroyImmediate(trampoline);
        }
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("[RideFeatureCheck] FAIL: " + what);
    }
}

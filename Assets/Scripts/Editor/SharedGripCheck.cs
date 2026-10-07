using System;
using System.Reflection;
using UnityEngine;

/// <summary>Editor-only deterministic regressions for shared carrying and directional grip contests. No saves or builds.</summary>
public static class SharedGripCheck
{
    static int checks;
    public static void Run()
    {
        checks = 0;
        Vector3 right = Vector3.right;
        foreach (float dt in new[] { 1f / 30f, 1f / 60f, 0.05f })
        {
            foreach (bool heavy in new[] { false, true })
            {
                Check(Sim(right, Vector3.forward * 2f, Vector3.forward * 2f, 30f, dt, heavy) == 0,
                    "matching walks carry indefinitely");
                Check(Sim(right, Vector3.forward * 4.5f, Vector3.forward * 4.5f, 30f, dt, heavy) == 0,
                    "matching runs never punish a heavy-bag helper");
                Check(Sim(right, right * 2f, right * 4.5f, 10f, dt, heavy) == 0,
                    "same-direction travel does not become a theft");
                Check(Sim(right, Vector3.zero, Vector3.zero, 30f, dt, heavy) == 0, "idle grips have no timeout");
                Check(Sim(right, Vector3.zero, right * 4.5f, 1f, dt, heavy) == 2, "runner takes from stationary holder");
                Check(Sim(right, -right * 4.5f, Vector3.zero, 1f, dt, heavy) == 1, "original holder can equally pull away");
                Check(Sim(right, -right * 2f, right * 4.5f, 2f, dt, heavy) == 2, "running beats opposite walking");
                Check(Sim(right, -right * 4.5f, right * 2f, 2f, dt, heavy) == 1, "swapped strengths reverse the winner");
                Check(Sim(right, -right * 4.5f, right * 4.5f, 1f, dt, heavy) == 0, "opposition gives time to react");
                Check(Sim(right, -right * 4.5f, right * 4.5f, 3f, dt, heavy) == 3, "sustained equal running tears");
                Check(Sim(right, -right * 2f, right * 2f, 4.5f, dt, heavy) == 3, "sustained equal walking tears more slowly");
                Check(Sim(right, right * 4.5f, -right * 4.5f, 10f, dt, heavy) == 0, "moving toward each other is slack");
            }
        }

        float advantage = 0f, strain = 0f;
        var opposed = SharedGripRules.Measure(right, -right * 4.5f, right * 4.5f);
        for (int i = 0; i < 20; i++) SharedGripRules.Step(ref advantage, ref strain, opposed, 0.05f, false);
        Check(strain > 0.4f && strain < 0.7f, "strain visibly builds before a tear");
        for (int i = 0; i < 30; i++) SharedGripRules.Step(ref advantage, ref strain, default, 0.05f, false);
        Check(strain == 0f && advantage == 0f, "relaxing saves the bag");
        // Camera orientation should transform the stick; merely looking elsewhere must never add force.
        Check(SharedGripRules.Effort(Vector2.zero, true, 150f) == Vector3.zero, "camera alone never pulls");
        Check(Vector3.Distance(SharedGripRules.Effort(Vector2.up, true, 90f), right * 4.5f) < 0.001f,
            "camera-relative walk intent uses the same world direction as locomotion");
        Check(!SharedGripRules.Finite(new Vector3(float.NaN, 0f, 0f)) &&
            !SharedGripRules.Finite(new Vector3(float.PositiveInfinity, 0f, 0f)), "invalid network input rejected");
        Check(SharedGripRules.Tether(right, right * 0.1f, 0.02f) == Vector3.zero, "close partners are not dragged");
        Check(SharedGripRules.Tether(right * 2.7f, right * 0.1f, 0.02f).x < 0f, "outward steps meet a soft leash");
        Check(SharedGripRules.Tether(right * 100f, right, 0.02f) == Vector3.zero, "teleports do not yank the other player");
        Check(typeof(TugHud).GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "there is no tug screen");
        CheckHelperInput();
        Debug.Log("[SharedGripCheck] PASS: " + checks + " checks (cooperation, both winners, tearing, relaxing, input, tether, helper movement, no tug UI)");
    }

    static byte Sim(Vector3 separation, Vector3 a, Vector3 b, float seconds, float dt, bool heavy)
    {
        float advantage = 0f, strain = 0f;
        var load = SharedGripRules.Measure(separation, a, b);
        for (int i = 0; i < Mathf.CeilToInt(seconds / dt); i++)
        {
            byte result = SharedGripRules.Step(ref advantage, ref strain, load, dt, heavy);
            if (result != 0) return result;
        }
        return 0;
    }

    static void CheckHelperInput()
    {
        var go = new GameObject("Shared grip helper check") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var p = go.AddComponent<PlayerTells>();
            typeof(PlayerTells).GetProperty("Helping").SetValue(p, true);
            typeof(PlayerTells).GetProperty("LiftedTogether").SetValue(p, true);
            var method = typeof(PlayerTells).GetMethod("SpeedScale", BindingFlags.NonPublic | BindingFlags.Instance);
            float speed = (float)method.Invoke(p, null);
            Check(Mathf.Abs(speed - PlayerTells.LiftSpeed) < 0.001f, "helper retains real movement input at paired carry speed");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) throw new Exception("[SharedGripCheck] FAIL: " + message);
    }
}

using UnityEngine;

/// <summary>Symmetric shared-bag rules. World-space movement intent drives effort; facing and network jitter do not.</summary>
public static class SharedGripRules
{
    public const float WalkEffort = 2f, RunEffort = 4.5f;
    public const float GraceSeconds = 0.4f, Span = 1.45f, MaxSeparation = 5f;
    public const float InputLifetime = 0.65f, InputTimeout = 2f;
    public const float TakeSeconds = 0.65f, RipSeconds = 3.2f;

    public struct Load { public float holder, challenger; public bool opposed; }

    public static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

    public static Load Measure(Vector3 separation, Vector3 holderIntent, Vector3 challengerIntent)
    {
        separation.y = holderIntent.y = challengerIntent.y = 0f;
        if (separation.sqrMagnitude < 0.16f) return default;
        var axis = separation.normalized;
        var a = Vector3.ClampMagnitude(holderIntent, RunEffort);
        var b = Vector3.ClampMagnitude(challengerIntent, RunEffort);
        // Following, walking side by side and ordinary turns are cooperation.
        if (a.magnitude > 0.25f && b.magnitude > 0.25f && Vector3.Dot(a.normalized, b.normalized) > 0.5f)
            return default;
        return new Load
        {
            holder = Mathf.Max(0f, Vector3.Dot(a, -axis)),
            challenger = Mathf.Max(0f, Vector3.Dot(b, axis)),
            opposed = a.magnitude > 0.25f && b.magnitude > 0.25f && Vector3.Dot(a.normalized, b.normalized) < -0.25f
        };
    }

    /// <summary>0 keeps sharing, 1/2 gives the bag to that grip, 3 tears it. Neither first holder nor company gets an advantage.</summary>
    public static byte Step(ref float advantage, ref float strain, Load load, float dt, bool heavy)
    {
        dt = Mathf.Clamp(dt, 0f, 0.1f);
        float difference = load.challenger - load.holder;
        if (Mathf.Abs(difference) > 0.65f)
            advantage = Mathf.Clamp(advantage + Mathf.Sign(difference) *
                Mathf.InverseLerp(0.65f, RunEffort, Mathf.Abs(difference)) * dt / TakeSeconds, -1f, 1f);
        else advantage = Mathf.MoveTowards(advantage, 0f, dt * 1.5f);
        float balanced = Mathf.Min(load.holder, load.challenger);
        bool taut = load.opposed && balanced > 0.5f && Mathf.Abs(difference) <= 0.9f;
        float build = Mathf.Clamp(balanced / WalkEffort, 0f, 1.6f) / (heavy ? RipSeconds + 0.8f : RipSeconds);
        strain = Mathf.Clamp01(strain + (taut ? build : -0.6f) * dt);
        if (advantage <= -0.999f) return 1;
        if (advantage >= 0.999f) return 2;
        return strain >= 0.999f ? (byte)3 : (byte)0;
    }

    public static Vector3 Effort(Vector2 move, bool sprint, float cameraYaw) =>
        Quaternion.Euler(0f, cameraYaw, 0f) * new Vector3(move.x, 0f, move.y).normalized *
        Mathf.Clamp01(move.magnitude) * (sprint ? RunEffort : WalkEffort);

    /// <summary>Soft local leash, resolved with CharacterController collisions, never teleporting a remote player.</summary>
    public static Vector3 Tether(Vector3 separation, Vector3 frameMove, float dt)
    {
        separation.y = frameMove.y = 0f;
        float distance = separation.magnitude;
        if (distance <= Span || distance >= MaxSeparation) return Vector3.zero;
        Vector3 outward = separation / distance;
        return -outward * Mathf.Min(distance - Span,
            Mathf.Max(0f, Vector3.Dot(frameMove, outward)) + 1.5f * Mathf.Min(dt, 0.05f));
    }
}

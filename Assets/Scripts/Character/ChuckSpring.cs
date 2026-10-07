using UnityEngine;

/// <summary>
/// One channel of Chuck's secondary motion: a semi-implicit damped oscillator
/// a = -w^2 (x - target) - 2 z w v, sub-stepped at 1/120 s (dt clamped to 1/30 s).
/// Kicks are velocity impulses sized so the first swing peaks near the given displacement.
/// </summary>
public struct ChuckSpring
{
    public float x, v;

    const float MaxDt = 1f / 30f;
    const float SubStep = 1f / 120f;

    public void Step(float target, float omega, float zeta, float dt)
    {
        if (dt <= 0f) return;
        dt = Mathf.Min(dt, MaxDt);
        int n = Mathf.Max(1, Mathf.CeilToInt(dt / SubStep));
        float h = dt / n;
        float w2 = omega * omega, c = 2f * zeta * omega;
        for (int i = 0; i < n; i++)
        {
            v += (-w2 * (x - target) - c * v) * h;
            x += v * h;
        }
    }

    /// <summary>Velocity impulse that makes the spring swing roughly <paramref name="displacement"/> away from where it is.</summary>
    public void Kick(float displacement, float omega) => v += displacement * omega;

    /// <summary>
    /// Velocity impulse sized so that, from rest, the first swing peaks at exactly <paramref name="peak"/>
    /// (x(t) = v0/ωd · e^(-ζωt) · sin(ωd t), peak at tan(ωd t) = ωd/(ζω)).
    /// </summary>
    public void KickPeak(float peak, float omega, float zeta)
    {
        zeta = Mathf.Clamp(zeta, 0.01f, 0.95f);
        float wd = omega * Mathf.Sqrt(1f - zeta * zeta);
        float tp = Mathf.Atan2(wd, zeta * omega) / wd;
        float f = Mathf.Exp(-zeta * omega * tp) * Mathf.Sin(wd * tp) / wd;
        v += peak / Mathf.Max(1e-4f, f);
    }

    /// <summary>Hard limits; the velocity into the limit is killed (with an optional bounce).</summary>
    public void Clamp(float min, float max, float bounce = 0f)
    {
        if (x < min) { x = min; if (v < 0f) v = -v * bounce; }
        else if (x > max) { x = max; if (v > 0f) v = -v * bounce; }
    }

    public void Reset(float value = 0f) { x = value; v = 0f; }
}

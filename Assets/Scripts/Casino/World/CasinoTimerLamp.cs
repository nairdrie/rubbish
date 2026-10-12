using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The countdown lamp (Docs/Casino.md §9.1): a row of bulbs on a brass arch. The presenter drives it with
/// <see cref="SetValue"/> (1 = full time left, 0 = out); the bulbs go out from the end and the colour slides green, amber, red.
/// Local and cosmetic: every machine drives its own copy from the replicated timer.
/// </summary>
public class CasinoTimerLamp : MonoBehaviour
{
    public Renderer[] bulbs = new Renderer[0];
    [Tooltip("Optional point light under the arch: its colour follows the bulbs, its intensity the lit fraction.")]
    public Light glow;
    public float glowIntensity = 0.6f;
    public Color offEmission = new Color(0.05f, 0.03f, 0.02f), offBase = new Color(0.25f, 0.22f, 0.2f);
    public Color good = new Color(0.25f, 2.3f, 0.45f), warn = new Color(2.7f, 1.7f, 0.2f), bad = new Color(2.9f, 0.22f, 0.14f);

    float value;
    MaterialPropertyBlock mpb;
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor"), BaseColor = Shader.PropertyToID("_BaseColor");

    /// <summary>The last value set (0..1).</summary>
    public float Value01 => value;

    /// <summary>1 = full time left, 0 = out of time (all bulbs dark).</summary>
    public void SetValue(float v01)
    {
        value = Mathf.Clamp01(v01);
        if (bulbs == null || bulbs.Length == 0) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        int n = bulbs.Length;
        int on = value <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(value * n - 0.001f), 1, n);
        Color c = value > 0.5f ? Color.Lerp(warn, good, (value - 0.5f) * 2f) : Color.Lerp(bad, warn, value * 2f);
        for (int i = 0; i < n; i++)
        {
            var r = bulbs[i];
            if (r == null) continue;
            bool lit = i < on;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(EmissionColor, lit ? c : offEmission);
            mpb.SetColor(BaseColor, lit ? Color.Lerp(Color.white, c, 0.35f) : offBase);
            r.SetPropertyBlock(mpb);
        }
        if (glow != null)
        {
            glow.enabled = on > 0;
            glow.color = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b));
            glow.intensity = glowIntensity * on / n;
        }
    }

    /// <summary>All bulbs dark (no round running).</summary>
    public void SetOff() => SetValue(0f);
}

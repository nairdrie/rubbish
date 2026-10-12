using System;
using TMPro;
using UnityEngine;

/// <summary>
/// A three-dimensional button: a lit plate (blackjack Hit / Stand / Double / Split / Insurance, roulette Ready / Undo / Clear /
/// Rebet, slot Spin / Bet+ / Bet-, the lever, a drink slot). Local and cosmetic; the controller listens to <see cref="Pressed"/>
/// (or the static <see cref="AnyPressed"/>) and decides what the press means.
/// </summary>
public class CasinoButton : CasinoPickable
{
    [Tooltip("Printed on the plate (also set into labelText).")]
    public string label = "";
    public TMP_Text labelText;
    [Tooltip("The plate's face: gets the lit / dim / disabled colour by a property block.")]
    public Renderer[] lit = new Renderer[0];
    [Tooltip("Moves 'in' by pressDepth while pressed: local -Y, or +Z when pressAxisZ (a board whose face looks toward local -Z).")]
    public Transform cap;
    public float pressDepth = 0.006f;
    public bool pressAxisZ;
    public Color litColor = new Color(0.95f, 0.55f, 0.10f);
    public Color dimColor = new Color(0.16f, 0.09f, 0.02f);
    public Color disabledColor = new Color(0.07f, 0.06f, 0.05f);
    [Tooltip("Tint of the plate's base colour in each state: lit, dim, disabled.")]
    public Color litBase = new Color(1f, 0.9f, 0.55f), dimBase = new Color(0.86f, 0.74f, 0.46f), disabledBase = new Color(0.25f, 0.22f, 0.2f);

    public event Action<CasinoButton> Pressed;
    public static event Action<CasinoButton> AnyPressed;

    bool isLit, isEnabled = true;
    float pressT = -1f;
    Vector3 capRest;
    bool capKnown;
    MaterialPropertyBlock mpb;

    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor"), BaseColor = Shader.PropertyToID("_BaseColor");

    public bool IsLit => isLit;
    public bool IsEnabled => isEnabled;
    public override bool Clickable => isActiveAndEnabled && isEnabled;

    void Awake()
    {
        kind = CasinoPickKind.Button;
        if (string.IsNullOrEmpty(id)) id = label;
        Refresh();
    }

    public void SetLabel(string text)
    {
        label = text ?? "";
        if (labelText != null) labelText.text = label;
    }

    /// <summary>Light the plate (an action is on offer) or leave it dim.</summary>
    public void SetLit(bool on)
    {
        if (isLit == on) return;
        isLit = on;
        Refresh();
    }

    /// <summary>A disabled button is greyed and ignores presses (it can still be hovered, to say why).</summary>
    public void SetEnabled(bool on)
    {
        if (isEnabled == on) return;
        isEnabled = on;
        Refresh();
    }

    /// <summary>Press it: animates the cap and raises <see cref="Pressed"/>. False when disabled.</summary>
    public bool Press()
    {
        if (!Clickable) return false;
        pressT = 0f;
        try { Pressed?.Invoke(this); }
        catch (Exception e) { Debug.LogException(e); }
        try { AnyPressed?.Invoke(this); }
        catch (Exception e) { Debug.LogException(e); }
        return true;
    }

    void Refresh()
    {
        if (lit == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        Color e = !isEnabled ? disabledColor : isLit ? litColor : dimColor;
        Color b = !isEnabled ? disabledBase : isLit ? litBase : dimBase;
        for (int i = 0; i < lit.Length; i++)
        {
            var r = lit[i];
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(EmissionColor, e);
            mpb.SetColor(BaseColor, b);
            r.SetPropertyBlock(mpb);
        }
    }

    protected override void ApplyHover()
    {
        // The plate's own lit / dim colour wins; hover just brightens it.
        if (!Hovered) { Refresh(); return; }
        if (lit == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        Color e = isEnabled ? hoverEmission : disabledColor;
        for (int i = 0; i < lit.Length; i++)
        {
            var r = lit[i];
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(EmissionColor, e);
            r.SetPropertyBlock(mpb);
        }
    }

    void Update()
    {
        if (cap == null || pressT < 0f) return;
        if (!capKnown) { capRest = cap.localPosition; capKnown = true; }
        pressT += Time.unscaledDeltaTime / 0.16f;
        float k = pressT >= 1f ? 0f : Mathf.Sin(pressT * Mathf.PI);
        Vector3 down = pressAxisZ ? Vector3.forward : Vector3.down;
        cap.localPosition = capRest + down * (pressDepth * k);
        if (pressT >= 1f) { pressT = -1f; cap.localPosition = capRest; }
    }
}

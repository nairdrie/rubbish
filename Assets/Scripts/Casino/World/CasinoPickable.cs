using UnityEngine;

public enum CasinoPickKind { Other, Button, BetCircle, ChipBin, HandSpot, Layout, Lever, DrinkSlot }

/// <summary>
/// Anything in the casino a <see cref="CasinoCursor"/> can hover and click (Docs/Casino.md §9.1). Sits on (or above) a
/// trigger collider on <see cref="CasinoLayout.PickLayer"/>. The identity fields say WHAT it is, so a controller needs no name
/// matching: <c>kind</c>, the owning <c>station</c>, the <c>spot</c> index (-1 = shared by the station) and an <c>index</c>
/// (chip denomination, hand, drink slot).
/// </summary>
[DisallowMultipleComponent]
public class CasinoPickable : MonoBehaviour
{
    public CasinoPickKind kind;
    public CasinoStation station;
    [Tooltip("Which CasinoStation.spots entry owns it; -1 = shared by the whole station.")]
    public int spot = -1;
    [Tooltip("Chip denomination index, hand index or drink slot, depending on kind.")]
    public int index;
    [Tooltip("Stable id: a button id (\"Hit\"), \"BetCircle\", \"Chip\", \"Layout\", \"Lever\", ...")]
    public string id = "";
    [Tooltip("Short text for the floating label while hovered (the controller may override it).")]
    public string hint = "";
    [Tooltip("Parts that light up while hovered (their materials' emission / base colour is tinted by a property block).")]
    public Renderer[] hoverRenderers = new Renderer[0];
    public Color hoverEmission = new Color(1.8f, 1.35f, 0.45f);

    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor"), BaseId = Shader.PropertyToID("_BaseColor");
    MaterialPropertyBlock block;

    public bool Hovered { get; private set; }

    /// <summary>Can it be clicked right now? (A disabled button is still hovered, so a label can say why not.)</summary>
    public virtual bool Clickable => isActiveAndEnabled;

    public virtual void SetHover(bool on)
    {
        if (Hovered == on) return;
        Hovered = on;
        ApplyHover();
    }

    protected virtual void ApplyHover()
    {
        if (hoverRenderers == null) return;
        if (block == null) block = new MaterialPropertyBlock();
        for (int i = 0; i < hoverRenderers.Length; i++)
        {
            var r = hoverRenderers[i];
            if (r == null) continue;
            if (!Hovered) { r.SetPropertyBlock(null); continue; }
            r.GetPropertyBlock(block);
            block.SetColor(EmissionId, hoverEmission);
            block.SetColor(BaseId, Color.Lerp(Color.white, hoverEmission, 0.25f));
            r.SetPropertyBlock(block);
        }
    }

    void OnDisable()
    {
        if (Hovered) { Hovered = false; ApplyHover(); }
    }
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime helper added by Carryable.Highlight. Tints every renderer's _BaseColor (or _Color) toward a
/// warm glow through MaterialPropertyBlocks and gently pulses Carryable.HighlightScaleTarget. Restores
/// the exact previous property-block colour and scale when turned off. Auto-fades if nobody refreshes
/// it for a few frames (the focusing PlayerController went away, UI opened, etc).
/// </summary>
[DisallowMultipleComponent]
public class CarryHighlight : MonoBehaviour
{
    public static readonly Color GlowColor = new Color(1f, 0.93f, 0.45f);
    const float TintAmount = 0.38f;
    const float ExpireAfter = 0.2f;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock mpb;

    struct Slot
    {
        public Renderer renderer;
        public int materialIndex;   // -1 = renderer-wide block
        public int propId;
        public bool hadColor;
        public bool blockWasEmpty;
        public Color original;
    }

    readonly List<Slot> slots = new List<Slot>();
    Carryable owner;
    Transform scaleTarget;
    Vector3 scaleBase;
    float lastRefresh;
    float onSince;

    public bool IsOn { get; private set; }

    public void Init(Carryable c) { owner = c; }

    /// <summary>Turn on (or keep on) the highlight. Cheap to call every frame.</summary>
    public void Refresh()
    {
        lastRefresh = Time.unscaledTime;
        if (IsOn) return;
        IsOn = true;
        onSince = Time.unscaledTime;
        ApplyTint();
        scaleTarget = owner != null ? owner.HighlightScaleTarget : null;
        if (scaleTarget == transform) scaleTarget = null; // never scale the physics root
        if (scaleTarget != null) scaleBase = scaleTarget.localScale;
        enabled = true;
    }

    public void TurnOff()
    {
        if (!IsOn) return;
        IsOn = false;
        RestoreTint();
        if (scaleTarget != null) scaleTarget.localScale = scaleBase;
        scaleTarget = null;
        enabled = false;
    }

    void Update()
    {
        if (!IsOn) { enabled = false; return; }
        if (Time.unscaledTime - lastRefresh > ExpireAfter || owner == null ||
            owner.State == CarryState.Carried || owner.State == CarryState.Consumed)
        {
            TurnOff();
            return;
        }
        if (scaleTarget != null)
        {
            float t = Time.unscaledTime - onSince;
            // quick pop to 1.07 then a soft breathing pulse
            float pop = 1f + 0.07f * Mathf.Clamp01(t / 0.08f) + 0.025f * Mathf.Sin(t * 9f);
            scaleTarget.localScale = scaleBase * pop;
        }
    }

    void OnDisable()
    {
        // Component disabled externally or object being destroyed: leave things as we found them.
        if (IsOn)
        {
            IsOn = false;
            RestoreTint();
            if (scaleTarget != null) scaleTarget.localScale = scaleBase;
            scaleTarget = null;
        }
    }

    static Color Tint(Color c)
    {
        var t = Color.Lerp(c, GlowColor, TintAmount);
        t.a = c.a;
        return t;
    }

    void ApplyTint()
    {
        if (mpb == null) mpb = new MaterialPropertyBlock();
        slots.Clear();
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
            var mats = r.sharedMaterials;
            bool single = mats.Length <= 1;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                int id = m.HasProperty(BaseColorId) ? BaseColorId : m.HasProperty(ColorId) ? ColorId : -1;
                if (id < 0) continue;
                int index = single ? -1 : i;
                if (index < 0) r.GetPropertyBlock(mpb); else r.GetPropertyBlock(mpb, index);
                var slot = new Slot
                {
                    renderer = r,
                    materialIndex = index,
                    propId = id,
                    blockWasEmpty = mpb.isEmpty,
                    hadColor = mpb.HasColor(id) || mpb.HasVector(id),
                };
                slot.original = slot.hadColor ? mpb.GetColor(id) : m.GetColor(id);
                mpb.SetColor(id, Tint(slot.original));
                if (index < 0) r.SetPropertyBlock(mpb); else r.SetPropertyBlock(mpb, index);
                slots.Add(slot);
            }
        }
    }

    void RestoreTint()
    {
        if (mpb == null) mpb = new MaterialPropertyBlock();
        foreach (var s in slots)
        {
            if (s.renderer == null) continue;
            if (s.blockWasEmpty)
            {
                // There was no block before we came along: remove ours entirely.
                if (s.materialIndex < 0) s.renderer.SetPropertyBlock(null);
                else s.renderer.SetPropertyBlock(null, s.materialIndex);
                continue;
            }
            if (s.materialIndex < 0) s.renderer.GetPropertyBlock(mpb); else s.renderer.GetPropertyBlock(mpb, s.materialIndex);
            if (s.hadColor)
                mpb.SetColor(s.propId, s.original);
            else
            {
                // A block existed without this colour; MPBs can't drop one property, so write the
                // material's own colour (visually identical).
                var mats = s.renderer.sharedMaterials;
                int mi = s.materialIndex < 0 ? 0 : s.materialIndex;
                if (mi < mats.Length && mats[mi] != null) mpb.SetColor(s.propId, mats[mi].GetColor(s.propId));
            }
            if (s.materialIndex < 0) s.renderer.SetPropertyBlock(mpb); else s.renderer.SetPropertyBlock(mpb, s.materialIndex);
        }
        slots.Clear();
    }
}

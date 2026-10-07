using TMPro;
using UnityEngine;

/// <summary>
/// Floating marker over a house's curb while it has trash out: a bobbing, spinning arrow and a pulsing ring on
/// the sidewalk, both colored by FRESHNESS of the oldest bag (green fresh → yellow late soon → orange late →
/// red neglected), plus a billboard label ("14 Maple St / 3 bags · 2 h late"). The stop that is
/// TrashRoute.Current.NextStop(player) gets a bigger pulsing arrow, a "NEXT" tag and a sky beam so you can find
/// it from the truck. Pops out with confetti when the curb is cleaned (and back in when the house puts more
/// out). Built at runtime by PickupStop (no assets needed).
/// </summary>
public class StopMarker : MonoBehaviour
{
    public PickupStop stop;

    BobbingArrow arrow;
    Transform ringT;
    MeshRenderer ringRend;
    Transform beamT;
    MeshRenderer beamRend;
    TextMeshPro label;
    SpriteRenderer glyph, glyphBack; // colour-blind shape over the label (Settings → Accessibility)

    bool isNext;
    float nextCheck;
    float shown;          // 0..1 visibility (animated)
    float celebrateT = -1f;
    string lastLabel;
    Color color = CurbTrash.FreshColor;
    Color targetColor = CurbTrash.FreshColor;
    int level;
    float wobble;
    float extraSpin;
    bool hiddenApplied;

    const float LabelMaxDistance = 45f;
    const float BeamHeight = 30f;

    /// <summary>World position of the arrow (for confetti etc).</summary>
    public Vector3 ArrowPosition => arrow != null ? arrow.transform.position : transform.position;
    /// <summary>True while this is the route planner's next stop.</summary>
    public bool IsNext => isNext;

    public static StopMarker Create(PickupStop stop)
    {
        if (stop == null) return null;
        var go = new GameObject($"StopMarker ({stop.address})");
        if (stop.gameObject.scene.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, stop.gameObject.scene);
        go.transform.position = stop.transform.position;
        var m = go.AddComponent<StopMarker>();
        m.stop = stop;
        m.Build();
        return m;
    }

    // Ring hugs the pile: grows as the house puts more out (1 bag ≈ 0.9 m, a full tidy curb ≈ 1.6 m).
    float ringBags = 1f;
    float RingRadius => 0.55f + Mathf.Min(3f, ringBags) * 0.32f + (ringBags > 3f ? 0.12f : 0f);

    void Build()
    {
        // Arrow
        var arrowGo = new GameObject("Arrow");
        arrowGo.transform.SetParent(transform, false);
        arrowGo.transform.localPosition = Vector3.up * stop.markerHeight;
        arrow = arrowGo.AddComponent<BobbingArrow>();
        arrow.color = CurbTrash.FreshColor;
        arrow.size = 0.85f;
        arrow.bobAmplitude = 0.22f;
        arrow.bobSpeed = 2.6f;
        arrow.spinSpeed = 90f;

        // Ground ring around the pile
        ringRend = PickupVisuals.MakeMeshChild(transform, "Ring", PickupVisuals.Ring, PickupVisuals.MarkerMaterial, CurbTrash.FreshColor);
        ringT = ringRend.transform;
        ringT.position = stop.PileCenter + Vector3.up * 0.03f;
        ringT.localScale = new Vector3(RingRadius, 1f, RingRadius);

        // Beacon beam (only if the transparent material exists)
        var beamMat = PickupVisuals.BeamMaterial;
        if (beamMat != null)
        {
            beamRend = PickupVisuals.MakeMeshChild(transform, "Beam", PickupVisuals.Beam, beamMat, new Color(1f, 0.6f, 0.1f, 0.22f));
            beamT = beamRend.transform;
            beamT.localPosition = Vector3.up * (stop.markerHeight + 1.9f);
            beamT.localScale = new Vector3(0.22f, BeamHeight, 0.22f);
            beamRend.enabled = false;
        }

        // Label
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(transform, false);
        labelGo.transform.localPosition = Vector3.up * (stop.markerHeight + 1.35f);
        label = labelGo.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 3.2f;
        label.fontStyle = FontStyles.Bold;
        label.color = Color.white;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.rectTransform.sizeDelta = new Vector2(9f, 2f);
        label.outlineWidth = 0.22f;
        label.outlineColor = new Color32(20, 20, 20, 255);
        var lr = label.GetComponent<MeshRenderer>();
        if (lr != null) lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        glyphBack = MakeGlyph(labelGo.transform, "GlyphBack", 1.25f, 0.02f);
        glyph = MakeGlyph(labelGo.transform, "Glyph", 1f, 0f);

        shown = 0f;
        ringBags = Mathf.Max(1, stop.BagsRemaining);
        Refresh();
        color = targetColor;
        ApplyVisibility(0f);
    }

    /// <summary>Update the label + freshness color now.</summary>
    public void Refresh()
    {
        if (stop == null || label == null) return;
        int n = stop.BagsRemaining;
        double age = stop.OldestBagAgeHours;
        var d = stop.District;
        level = n > 0 ? CurbTrash.FreshnessLevel(d, age) : 0;
        targetColor = n > 0 ? CurbTrash.FreshnessColor(d, age) : CurbTrash.StageColor(FreshnessStage.Fresh);
        if (glyph != null) glyph.sprite = glyphBack.sprite = UiKit.Sprites.Glyph(level);
        string hex = ColorUtility.ToHtmlStringRGB(Color.Lerp(targetColor, Color.white, 0.25f));
        string head = isNext ? $"<color=#FFD98C>NEXT »</color> {stop.address}" : stop.address;
        // Rich districts flag the money: "$40 + $15 tip" while on time.
        string pay = n > 0 && d.tier > 1 && level < 2 ? $" <color=#FFD24A>{PickupVisuals.Cash(d.fee)}{(d.tip > 0f ? " + tip" : "")}</color>" : "";
        string text = n > 0
            ? $"{head}\n<size=70%><color=#{hex}>{CurbTrash.StopLabel(d, n, age)}</color>{pay}</size>"
            : head;
        if (text == lastLabel) return;
        lastLabel = text;
        label.text = text;
    }

    /// <summary>Curb cleaned: punch, spin fast, shrink away.</summary>
    public void Celebrate()
    {
        celebrateT = 0f;
        if (arrow != null)
        {
            arrow.Pop();
            Juice.Punch(arrow.transform, 0.5f, 0.3f);
        }
    }

    /// <summary>Something happened at this curb (new bag, pile went stinky): pop the arrow.</summary>
    public void Bump()
    {
        if (arrow != null && arrow.gameObject.activeInHierarchy) { arrow.Pop(); Juice.Punch(arrow.transform, 0.3f, 0.25f); }
        Refresh();
    }

    void Update()
    {
        if (stop == null) { Destroy(gameObject); return; }
        float dt = Time.deltaTime;

        bool want = stop.HasTrash;

        // Clean curb, fully hidden, nothing animating: nothing to do (most houses, most of the time).
        if (!want && shown <= 0f && celebrateT < 0f)
        {
            if (!hiddenApplied) { isNext = false; ApplyVisibility(dt); hiddenApplied = true; }
            return;
        }
        hiddenApplied = false;

        if (Time.time >= nextCheck)
        {
            nextCheck = Time.time + 0.3f;
            var route = TrashRoute.Current;
            bool wasNext = isNext;
            isNext = want && route != null && route.NextStop(ReferencePosition()) == stop;
            if (isNext && !wasNext && shown > 0.5f && arrow != null) arrow.Pop();
            lastLabel = wasNext != isNext ? null : lastLabel;
            Refresh();
        }

        // Celebration: spin like crazy for a moment, then shrink out.
        if (celebrateT >= 0f)
        {
            celebrateT += dt;
            extraSpin += 900f * dt;
            if (celebrateT < 0.45f) want = true;
            else celebrateT = -1f;
        }

        color = Color.Lerp(color, targetColor, dt > 0f ? 1f - Mathf.Exp(-6f * dt) : 1f);
        if (stop.HasTrash) ringBags = Mathf.MoveTowards(ringBags, stop.BagsRemaining, dt * 4f);
        shown = Mathf.MoveTowards(shown, want ? 1f : 0f, dt * (want ? 3f : 2.2f));
        ApplyVisibility(dt);
    }

    Vector3 ReferencePosition()
    {
        var p = PlayerController.Local;
        if (p != null)
        {
            if (p.IsDriving && p.currentTruck != null) return p.currentTruck.transform.position;
            return p.transform.position;
        }
        var cam = Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    void ApplyVisibility(float dt)
    {
        bool visible = shown > 0.001f;
        float ease = shown * shown * (3f - 2f * shown);
        bool neglected = level >= 3;

        if (arrow != null)
        {
            if (arrow.gameObject.activeSelf != visible) arrow.gameObject.SetActive(visible);
            arrow.SetColor(color);
            arrow.ScaleMultiplier = Mathf.Lerp(arrow.ScaleMultiplier, (isNext ? 1.45f : 1f) * ease, dt > 0f ? 1f - Mathf.Exp(-8f * dt) : 1f);
            arrow.pulse = isNext ? 0.12f : level >= 2 ? 0.07f : 0.03f;
            arrow.pulseSpeed = level >= 2 ? 9f : 6f;
            arrow.spinSpeed = isNext ? 160f : neglected ? 40f : 90f;
            // Neglected piles: the arrow droops and wobbles like it's queasy.
            wobble += dt * (neglected ? 1f : -1f);
            wobble = Mathf.Clamp01(wobble);
            float w = wobble * Mathf.Sin(Time.time * 3.1f) * 14f;
            arrow.transform.localRotation = Quaternion.Euler(w, extraSpin, wobble * Mathf.Cos(Time.time * 2.3f) * 10f);
        }

        if (ringRend != null)
        {
            ringRend.enabled = visible;
            float pulse = 1f + (isNext ? 0.08f : 0.03f) * Mathf.Sin(Time.time * (isNext ? 5f : 2.5f));
            float r = RingRadius * pulse * Mathf.Max(0.01f, ease);
            ringT.localScale = new Vector3(r, 1f, r);
            PickupVisuals.SetColor(ringRend, color);
        }

        if (beamRend != null)
        {
            bool beam = visible && isNext;
            beamRend.enabled = beam;
            if (beam)
            {
                float w = 0.22f * ease * (1f + 0.15f * Mathf.Sin(Time.time * 4f));
                beamT.localScale = new Vector3(w, BeamHeight, w);
                var bc = Color.Lerp(color, Color.white, 0.35f);
                bc.a = 0.24f;
                PickupVisuals.SetColor(beamRend, bc);
            }
        }

        if (label != null)
        {
            var cam = Camera.main;
            bool near = cam != null && (cam.transform.position - label.transform.position).sqrMagnitude < LabelMaxDistance * LabelMaxDistance;
            bool showLabel = visible && (near || isNext);
            if (label.enabled != showLabel) label.enabled = showLabel;
            if (showLabel && cam != null)
            {
                label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position, Vector3.up);
                float far = Mathf.InverseLerp(LabelMaxDistance * 0.6f, LabelMaxDistance * 2.5f, (cam.transform.position - label.transform.position).magnitude);
                label.transform.localScale = Vector3.one * Mathf.Max(0.01f, ease) * (isNext ? 1.15f + far * 1.2f : 1f);
                label.color = isNext ? new Color(1f, 0.95f, 0.85f) : Color.white;
            }
            bool shape = showLabel && GameSettings.ColorblindMarkers && stop.HasTrash;
            if (glyph != null && glyph.enabled != shape) glyph.enabled = glyphBack.enabled = shape;
            if (shape) glyph.color = color;
        }
    }

    /// <summary>A billboarded shape above the label (it turns with the label): ● fresh, ◆ late soon, ▲ late, ✖ stinky.</summary>
    static SpriteRenderer MakeGlyph(Transform labelT, string name, float scale, float behind)
    {
        var go = new GameObject(name);
        go.transform.SetParent(labelT, false);
        go.transform.localPosition = new Vector3(0f, 1.35f, behind);
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // the label faces away from the camera; sprites face -Z
        go.transform.localScale = Vector3.one * 1.1f * scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = UiKit.Sprites.Disc;
        sr.color = behind > 0f ? new Color(0.08f, 0.06f, 0.1f, 1f) : Color.white;
        sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        sr.receiveShadows = false;
        sr.enabled = false;
        return sr;
    }
}

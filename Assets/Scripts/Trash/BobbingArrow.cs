using UnityEngine;

/// <summary>
/// A bright, bobbing, spinning down-arrow ("put it HERE" / "trash HERE"). Builds its own mesh child
/// at runtime, pops in whenever it's enabled. Used above the truck hopper and above pickup stops.
/// </summary>
public class BobbingArrow : MonoBehaviour
{
    public Color color = new Color(0.3f, 1f, 0.4f);
    [Tooltip("Arrow height in this transform's units.")]
    public float size = 0.6f;
    public float bobAmplitude = 0.15f;
    public float bobSpeed = 3.2f;
    [Tooltip("Degrees per second around Y.")]
    public float spinSpeed = 120f;
    [Tooltip("Extra breathing scale pulse (0 = none).")]
    public float pulse = 0f;
    public float pulseSpeed = 6f;

    Transform visual;
    MeshRenderer rend;
    Color appliedColor;
    float phase;
    float popT = 1f;
    float scaleMul = 1f;

    /// <summary>The arrow mesh renderer (built on first enable).</summary>
    public MeshRenderer Renderer { get { EnsureBuilt(); return rend; } }

    /// <summary>Extra scale multiplier (e.g. bigger for the next stop). Smoothly applied.</summary>
    public float ScaleMultiplier { get => scaleMul; set => scaleMul = Mathf.Max(0f, value); }

    void Awake()
    {
        EnsureBuilt();
        phase = Random.value * 10f;
    }

    void EnsureBuilt()
    {
        if (visual != null) return;
        var existing = transform.Find("ArrowMesh");
        if (existing != null) Destroy(existing.gameObject);
        rend = PickupVisuals.MakeMeshChild(transform, "ArrowMesh", PickupVisuals.Arrow, PickupVisuals.MarkerMaterial, color);
        visual = rend.transform;
        appliedColor = color;
    }

    void OnEnable()
    {
        EnsureBuilt();
        popT = 0f; // pop in with overshoot
    }

    public void SetColor(Color c)
    {
        color = c;
        EnsureBuilt();
        if (appliedColor == c) return;
        appliedColor = c;
        PickupVisuals.SetColor(rend, c);
    }

    /// <summary>Restart the pop-in animation (e.g. to punch it on an event).</summary>
    public void Pop() { popT = 0f; }

    void Update()
    {
        if (visual == null) return;
        if (appliedColor != color) SetColor(color);
        float t = Time.time + phase;

        popT = Mathf.Min(1f, popT + Time.deltaTime / 0.35f);
        float pop = popT >= 1f ? 1f : BackOut(popT);
        float breathe = pulse > 0f ? 1f + pulse * Mathf.Sin(t * pulseSpeed) : 1f;
        float s = size * pop * breathe * scaleMul;

        visual.localScale = new Vector3(s, s, s);
        visual.localPosition = Vector3.up * (Mathf.Sin(t * bobSpeed) * bobAmplitude);
        visual.localRotation = Quaternion.Euler(0f, t * spinSpeed, 0f);
    }

    static float BackOut(float x)
    {
        const float c1 = 2.2f, c3 = c1 + 1f;
        float p = x - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }
}

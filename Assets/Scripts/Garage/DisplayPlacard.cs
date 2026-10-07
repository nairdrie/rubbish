using TMPro;
using UnityEngine;

/// <summary>
/// The museum placard that pops up next to a displayed find while you look at it (DisplayGallery's gaze). A dark card
/// with a rarity-coloured bar and a TMP text, facing the camera, sized for reading from a few metres. Local, runtime
/// only (renderers, no colliders); one per gallery.
/// </summary>
public class DisplayPlacard : MonoBehaviour
{
    const float Width = 0.62f, Height = 0.4f;

    TextMeshPro text;
    Renderer bar;
    MaterialPropertyBlock mpb;
    float shown01;          // 0 hidden .. 1 fully shown
    bool want;
    Vector3 anchor;
    Camera cam;
    string lastText;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    public static DisplayPlacard Create(Transform parent)
    {
        var go = new GameObject("DisplayPlacard");
        go.transform.SetParent(parent, false);
        var p = go.AddComponent<DisplayPlacard>();
        p.Build();
        go.SetActive(false);
        return p;
    }

    void Build()
    {
        var bg = Quad("Card", new Vector3(0f, 0f, 0.002f), new Vector3(Width, Height, 1f),
                      SortingKit.RuntimeMaterial("mat/display_placard", new Color(0.07f, 0.06f, 0.09f), true));
        bg.name = "Card";
        var frame = Quad("Frame", new Vector3(0f, 0f, 0.004f), new Vector3(Width + 0.025f, Height + 0.025f, 1f),
                         SortingKit.RuntimeMaterial("mat/display_brass_unlit", new Color(0.78f, 0.6f, 0.25f), true));
        frame.name = "Frame";
        bar = Quad("Bar", new Vector3(0f, Height * 0.5f - 0.012f, 0.001f), new Vector3(Width, 0.024f, 1f),
                   SortingKit.RuntimeMaterial("mat/display_placard_bar", Color.white, true)).GetComponent<Renderer>();
        mpb = new MaterialPropertyBlock();

        text = SortingKit.MakeText(transform, "Text", "", 0.5f, Color.white);
        text.transform.localPosition = new Vector3(0f, -0.008f, 0f);
        text.transform.localRotation = Quaternion.identity;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.enableAutoSizing = true;
        text.fontSizeMin = 0.08f;
        text.fontSizeMax = 0.62f;
        text.outlineWidth = 0.08f;
        text.fontStyle = FontStyles.Normal;
        text.rectTransform.sizeDelta = new Vector2(Width - 0.05f, Height - 0.055f);
        var tr = text.GetComponent<MeshRenderer>();
        if (tr != null) tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    GameObject Quad(string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        var col = q.GetComponent<Collider>();
        if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
        q.transform.SetParent(transform, false);
        q.transform.localPosition = localPos;
        q.transform.localScale = scale;
        var r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return q;
    }

    /// <summary>Show (or update) the card for a find at <paramref name="at"/>, read from <paramref name="camera"/>.</summary>
    public void Show(string body, Color rarity, Vector3 at, Camera camera)
    {
        want = true;
        anchor = at;
        cam = camera;
        if (!gameObject.activeSelf) { gameObject.SetActive(true); shown01 = 0f; }
        if (body != lastText)
        {
            lastText = body;
            text.text = body;
            text.ForceMeshUpdate();
            if (shown01 > 0.5f) shown01 = 0.75f;   // a little re-pop when you look at the next find
        }
        mpb.SetColor(BaseColorId, rarity);
        bar.SetPropertyBlock(mpb);
        Place();
    }

    public void Hide(bool immediate)
    {
        want = false;
        if (immediate) { shown01 = 0f; gameObject.SetActive(false); lastText = null; }
    }

    void LateUpdate()
    {
        shown01 = Mathf.MoveTowards(shown01, want ? 1f : 0f, Time.unscaledDeltaTime * (want ? 7f : 5f));
        if (!want && shown01 <= 0f) { gameObject.SetActive(false); lastText = null; return; }
        Place();
    }

    void Place()
    {
        if (cam == null) return;
        // Along your line of sight to the find, part of the way there (so whatever stands between you and the find
        // can't hide the card), a little above it in view, sized to a constant ~10° tall.
        Vector3 c = cam.transform.position;
        Vector3 d = anchor - c;
        float dist = d.magnitude;
        Vector3 dir = dist > 1e-3f ? d / dist : cam.transform.forward;
        float dp = Mathf.Clamp(dist * 0.55f, 0.75f, 2.0f);
        float size = dp / 2.4f;
        Vector3 up = cam.transform.up;
        Vector3 p = c + dir * dp + up * (Height * size * 0.8f);
        transform.position = p;
        // TMP reads along +Z, so the card's +Z points away from the camera.
        transform.rotation = Quaternion.LookRotation(p - c, up);
        float pop = shown01 < 1f ? SortingKit.EaseOutBack(shown01) : 1f;
        transform.localScale = Vector3.one * (size * Mathf.Max(0.01f, pop));
    }
}

using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Local, cosmetic, see-through things a seat controller puts in the world: a stack of "pending" chips on the bet circle, one ghost chip
/// where a click would drop a roulette chip, a pulsing ring round the selected denomination, and a small floating label. Nothing here is
/// networked or kept: the real chips are the presenters' (they follow the host's snapshots). Materials use "Sprites/Default" (always in the
/// build, premultiplied alpha, no depth write) so they need no keyword variants.
/// </summary>
public static class CasinoGhostKit
{
    static Mesh chipMesh, ringMesh, outlineMesh, shadowRingMesh;
    static readonly Dictionary<int, Material> chipMats = new Dictionary<int, Material>();
    static Material ringMat, outlineMat, shadowRingMat;

    static Shader Shade => Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");

    /// <summary>A chip: a short cylinder, ChipDiameter wide and ChipThickness high, origin at its base.</summary>
    public static Mesh ChipMesh()
    {
        if (chipMesh != null) return chipMesh;
        const int seg = 24;
        float r = CasinoLayout.ChipDiameter * 0.5f, h = CasinoLayout.ChipThickness;
        var v = new List<Vector3>(); var t = new List<int>();
        // top and bottom caps (fans) and the side
        v.Add(new Vector3(0, h, 0)); v.Add(new Vector3(0, 0, 0));
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v.Add(new Vector3(Mathf.Cos(a) * r, h, Mathf.Sin(a) * r));
            v.Add(new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
        }
        for (int i = 0; i < seg; i++)
        {
            int j = (i + 1) % seg;
            int t0 = 2 + i * 2, b0 = 3 + i * 2, t1 = 2 + j * 2, b1 = 3 + j * 2;
            t.Add(0); t.Add(t1); t.Add(t0);          // top
            t.Add(1); t.Add(b0); t.Add(b1);          // bottom
            t.Add(t0); t.Add(t1); t.Add(b0);         // side
            t.Add(b0); t.Add(t1); t.Add(b1);
        }
        chipMesh = new Mesh { name = "CasinoGhostChip", hideFlags = HideFlags.DontSave };
        chipMesh.SetVertices(v); chipMesh.SetTriangles(t, 0);
        chipMesh.RecalculateNormals(); chipMesh.RecalculateBounds();
        return chipMesh;
    }

    /// <summary>A flat ring (annulus) in the XZ plane.</summary>
    public static Mesh RingMesh(float inner = 0.037f, float outer = 0.046f)
    {
        if (ringMesh != null) return ringMesh;
        const int seg = 40;
        var v = new Vector3[seg * 2]; var t = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v[i * 2] = new Vector3(Mathf.Cos(a) * inner, 0, Mathf.Sin(a) * inner);
            v[i * 2 + 1] = new Vector3(Mathf.Cos(a) * outer, 0, Mathf.Sin(a) * outer);
            int j = (i + 1) % seg, k = i * 6;
            t[k] = i * 2; t[k + 1] = j * 2; t[k + 2] = i * 2 + 1; t[k + 3] = i * 2 + 1; t[k + 4] = j * 2; t[k + 5] = j * 2 + 1;
        }
        ringMesh = new Mesh { name = "CasinoGhostRing", hideFlags = HideFlags.DontSave, vertices = v, triangles = t };
        ringMesh.RecalculateBounds();
        return ringMesh;
    }

    static Mesh Annulus(string name, float inner, float outer)
    {
        const int seg = 48;
        var v = new Vector3[seg * 2]; var t = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v[i * 2] = new Vector3(Mathf.Cos(a) * inner, 0, Mathf.Sin(a) * inner);
            v[i * 2 + 1] = new Vector3(Mathf.Cos(a) * outer, 0, Mathf.Sin(a) * outer);
            int j = (i + 1) % seg, k = i * 6;
            t[k] = i * 2; t[k + 1] = j * 2; t[k + 2] = i * 2 + 1; t[k + 3] = i * 2 + 1; t[k + 4] = j * 2; t[k + 5] = j * 2 + 1;
        }
        var m = new Mesh { name = name, hideFlags = HideFlags.DontSave, vertices = v, triangles = t };
        m.RecalculateBounds();
        return m;
    }

    /// <summary>The ghost chip's bright outline: a ring just outside the chip's edge (chip radius is 0.032 m).</summary>
    public static Mesh OutlineMesh() => outlineMesh != null ? outlineMesh : (outlineMesh = Annulus("CasinoGhostOutline", 0.0325f, 0.0405f));

    /// <summary>A dark halo outside the bright outline, so the target reads against both the green felt and the brass.</summary>
    public static Mesh HaloMesh() => shadowRingMesh != null ? shadowRingMesh : (shadowRingMesh = Annulus("CasinoGhostHalo", 0.0405f, 0.0465f));

    public static Material OutlineMaterial()
    {
        if (outlineMat != null) return outlineMat;
        var shader = Shade;
        if (shader == null) return null;
        outlineMat = new Material(shader) { name = "CasinoGhostOutline", hideFlags = HideFlags.DontSave, color = Premul(new Color(1f, 0.93f, 0.55f), 1f) };
        outlineMat.renderQueue = 3002;
        return outlineMat;
    }

    public static Material HaloMaterial()
    {
        if (shadowRingMat != null) return shadowRingMat;
        var shader = Shade;
        if (shader == null) return null;
        shadowRingMat = new Material(shader) { name = "CasinoGhostHalo", hideFlags = HideFlags.DontSave, color = Premul(new Color(0.02f, 0.015f, 0.01f), 0.85f) };
        shadowRingMat.renderQueue = 3001;
        return shadowRingMat;
    }

    static Color Premul(Color c, float a) => new Color(c.r * a, c.g * a, c.b * a, a);

    public static Material ChipMaterial(int cents, float alpha)
    {
        int key = cents * 16 + Mathf.RoundToInt(alpha * 10f);
        if (chipMats.TryGetValue(key, out var m) && m != null) return m;
        var shader = Shade;
        if (shader == null) return null;
        m = new Material(shader) { name = "CasinoGhostChip " + cents, hideFlags = HideFlags.DontSave, color = Premul(CasinoArt.ChipColor(cents), alpha) };
        m.renderQueue = 3000;
        chipMats[key] = m;
        return m;
    }

    public static Material RingMaterial()
    {
        if (ringMat != null) return ringMat;
        var shader = Shade;
        if (shader == null) return null;
        ringMat = new Material(shader) { name = "CasinoGhostRing", hideFlags = HideFlags.DontSave, color = Premul(new Color(1f, 0.82f, 0.3f), 0.9f) };
        ringMat.renderQueue = 3000;
        return ringMat;
    }

    public static void SetRingAlpha(float a)
    {
        var m = RingMaterial();
        if (m != null) m.color = Premul(new Color(1f, 0.82f, 0.3f), Mathf.Clamp01(a));
    }

    public static GameObject NewRenderer(string name, Transform parent, Mesh mesh, Material mat)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        if (parent != null) go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        go.layer = 0; // not the pick layer: a ghost is never picked
        return go;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { chipMesh = null; ringMesh = null; outlineMesh = null; shadowRingMesh = null; chipMats.Clear(); ringMat = null; outlineMat = null; shadowRingMat = null; }
}

/// <summary>A stack of see-through chips lying on a transform (the blackjack stake the player is still building).</summary>
public sealed class CasinoGhostStack
{
    readonly Transform root;
    readonly List<GameObject> pool = new List<GameObject>();
    int shown;

    public CasinoGhostStack(string name, Transform parent, Vector3 localPos)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        root = go.transform;
        root.SetParent(parent, false);
        root.localPosition = localPos;
    }

    public Transform Root => root;
    public int Shown => shown;

    /// <summary>Show these chips (cents each, bottom first): same denominations share a column, up to 10 high.</summary>
    public void Set(IReadOnlyList<int> chips)
    {
        int n = chips == null ? 0 : chips.Count;
        // columns by first appearance of a denomination
        var cols = new List<int>();
        var heights = new List<int>();
        int placed = 0;
        for (int i = 0; i < n; i++)
        {
            int c = chips[i];
            int col = cols.IndexOf(c);
            if (col < 0) { cols.Add(c); heights.Add(0); col = cols.Count - 1; }
            int level = heights[col]++;
            var go = Get(placed++);
            go.SetActive(true);
            go.GetComponent<MeshRenderer>().sharedMaterial = CasinoGhostKit.ChipMaterial(c, 0.85f);
            float pitch = CasinoLayout.ChipDiameter * 1.05f;
            float x = (col - (cols.Count - 1) * 0.5f) * pitch;
            go.transform.localPosition = new Vector3(x, level * CasinoLayout.ChipThickness * 1.02f, 0f);
            go.transform.localRotation = Quaternion.identity;
        }
        for (int i = placed; i < pool.Count; i++) pool[i].SetActive(false);
        shown = placed;
    }

    GameObject Get(int i)
    {
        while (pool.Count <= i) pool.Add(CasinoGhostKit.NewRenderer("GhostChip", root, CasinoGhostKit.ChipMesh(), CasinoGhostKit.ChipMaterial(500, 0.85f)));
        return pool[i];
    }

    public void Destroy()
    {
        if (root != null) Object.Destroy(root.gameObject);
        pool.Clear();
    }
}

/// <summary>One see-through chip with a floating label above it (the roulette bet a click would make).</summary>
public sealed class CasinoGhostChip
{
    readonly GameObject chip;
    readonly CasinoWorldLabel label;
    int cents = -1;

    /// <summary>Size of the ghost chip against a real one (1.6x props are already used on the felt, so this is about 3x a real chip).</summary>
    public const float Scale = 2.2f;

    public CasinoGhostChip(Transform parent)
    {
        chip = CasinoGhostKit.NewRenderer("GhostChip", parent, CasinoGhostKit.ChipMesh(), CasinoGhostKit.ChipMaterial(100, 0.8f));
        // bigger than a real chip (it has to read from the eye pose, where the felt is a metre away), with a bright outline and a dark halo
        chip.transform.localScale = new Vector3(Scale, Scale, Scale);
        var halo = CasinoGhostKit.NewRenderer("GhostHalo", chip.transform, CasinoGhostKit.HaloMesh(), CasinoGhostKit.HaloMaterial());
        halo.transform.localPosition = new Vector3(0f, 0.0006f, 0f);
        var ring = CasinoGhostKit.NewRenderer("GhostRing", chip.transform, CasinoGhostKit.OutlineMesh(), CasinoGhostKit.OutlineMaterial());
        ring.transform.localPosition = new Vector3(0f, 0.001f, 0f);
        // the label rides above it (it inherits the chip's scale: about 0.66 effective font size, nearly twice what it was)
        label = new CasinoWorldLabel("GhostLabel", chip.transform, new Vector3(0f, 0.05f, 0f), 0.5f, 0.06f, 0.3f);
        chip.SetActive(false);
    }

    public bool Visible => chip != null && chip.activeSelf;

    public void Show(Vector3 world, Quaternion rot, int chipCents, string text)
    {
        if (chip == null) return;
        if (!chip.activeSelf) chip.SetActive(true);
        chip.transform.SetPositionAndRotation(world, rot);
        if (cents != chipCents)
        {
            cents = chipCents;
            chip.GetComponent<MeshRenderer>().sharedMaterial = CasinoGhostKit.ChipMaterial(chipCents, 0.8f);
        }
        label.Text = text;
        label.Face(Camera.main);
    }

    public void Hide() { if (chip != null && chip.activeSelf) chip.SetActive(false); }

    public void Destroy() { if (chip != null) Object.Destroy(chip); }
}

/// <summary>A pulsing ring under the selected chip bin.</summary>
public sealed class CasinoSelectionRing
{
    readonly GameObject ring;

    public CasinoSelectionRing()
    {
        ring = CasinoGhostKit.NewRenderer("SelectedChipRing", null, CasinoGhostKit.RingMesh(), CasinoGhostKit.RingMaterial());
        ring.SetActive(false);
    }

    public void Place(Transform bin)
    {
        if (ring == null) return;
        if (bin == null) { ring.SetActive(false); return; }
        ring.SetActive(true);
        ring.transform.SetParent(bin, false);
        ring.transform.localPosition = new Vector3(0f, 0.0042f, 0f);
        ring.transform.localRotation = Quaternion.identity;
        ring.transform.localScale = Vector3.one;
    }

    public void Pulse(float time)
    {
        if (ring == null || !ring.activeSelf) return;
        bool still = GameSettings.ReduceFlashing;
        float k = still ? 1f : 1f + 0.06f * Mathf.Sin(time * 6f);
        var s = ring.transform.localScale;
        ring.transform.localScale = new Vector3(k, 1f, k);
        CasinoGhostKit.SetRingAlpha(still ? 0.85f : 0.65f + 0.3f * Mathf.Sin(time * 6f + 1f));
    }

    public void Destroy() { if (ring != null) Object.Destroy(ring); }
}

/// <summary>A small world-space TextMeshPro label that faces the camera (ASCII only, like all casino lettering).</summary>
public sealed class CasinoWorldLabel
{
    readonly GameObject go;
    readonly TextMeshPro tmp;
    string text = "";

    public CasinoWorldLabel(string name, Transform parent, Vector3 localPos, float width = 0.5f, float height = 0.09f, float fontSize = 0.4f)
    {
        go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        tmp = go.AddComponent<TextMeshPro>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = false; tmp.fontSize = fontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(1f, 0.95f, 0.78f);
        tmp.outlineWidth = 0.22f; tmp.outlineColor = new Color32(20, 12, 8, 255);
        tmp.rectTransform.sizeDelta = new Vector2(width, height);
        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        go.SetActive(false);
        CasinoFacing.Add(go.transform, false); // faces whichever camera renders it (never mirrored)
    }

    public string Text
    {
        get => text;
        set
        {
            value = value ?? "";
            bool show = value.Length > 0;
            if (go.activeSelf != show) go.SetActive(show);
            if (text == value) return;
            text = value;
            tmp.text = value;
        }
    }

    public Color Color { set { if (tmp != null) tmp.color = value; } }

    public Transform Transform => go != null ? go.transform : null;

    /// <summary>Turn to face the camera (call each frame while shown).</summary>
    public void Face(Camera cam)
    {
        if (go == null || cam == null || !go.activeSelf) return;
        go.transform.rotation = Quaternion.LookRotation(go.transform.position - cam.transform.position, cam.transform.up);
    }

    public void SetPosition(Vector3 world) { if (go != null) go.transform.position = world; }

    public void Destroy() { if (go != null) { CasinoFacing.Remove(go.transform); Object.Destroy(go); } }
}

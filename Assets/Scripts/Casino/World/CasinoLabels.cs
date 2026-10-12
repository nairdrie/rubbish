using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Turns world-space lettering to face the camera that is RENDERING it (not just Camera.main), so a name tag reads the right way round
/// from the dealer's side, from a spectator's spot, in a screenshot camera or in the Scene view, and is never seen mirrored. Tags turn
/// about the vertical axis only (they stay upright); hand totals face the camera fully. Hooked into the render pipeline's per-camera
/// callback (URP) and <c>Camera.onPreCull</c> (built-in), so it costs a handful of quaternions per camera.
/// </summary>
public static class CasinoFacing
{
    struct Entry { public Transform t; public bool yawOnly; }
    static readonly List<Entry> entries = new List<Entry>();
    static bool hooked;

    public static int Count => entries.Count;

    /// <summary>Keep <paramref name="t"/> facing whichever camera renders it, until <see cref="Remove"/> or the object is destroyed.</summary>
    public static void Add(Transform t, bool yawOnly)
    {
        if (t == null) return;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].t == t) { entries[i] = new Entry { t = t, yawOnly = yawOnly }; return; }
        entries.Add(new Entry { t = t, yawOnly = yawOnly });
        Hook();
    }

    public static void Remove(Transform t)
    {
        for (int i = entries.Count - 1; i >= 0; i--) if (entries[i].t == t || entries[i].t == null) entries.RemoveAt(i);
    }

    static void Hook()
    {
        if (hooked) return;
        hooked = true;
        RenderPipelineManager.beginCameraRendering += OnSrp;
        Camera.onPreCull += OnPreCull;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        entries.Clear();
        if (hooked) { RenderPipelineManager.beginCameraRendering -= OnSrp; Camera.onPreCull -= OnPreCull; }
        hooked = false;
    }

    static void OnSrp(ScriptableRenderContext ctx, Camera cam) { FaceAll(cam); }
    static void OnPreCull(Camera cam) { if (GraphicsSettings.currentRenderPipeline == null) FaceAll(cam); }

    static void FaceAll(Camera cam)
    {
        if (cam == null || entries.Count == 0) return;
        if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
        Transform ct = cam.transform;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.t == null) { entries.RemoveAt(i); continue; }
            if (!e.t.gameObject.activeInHierarchy) continue;
            Face(e.t, ct.position, ct.up, e.yawOnly);
        }
    }

    /// <summary>The facing rule: the text's +Z points away from the viewer (TextMeshPro reads correctly from its -Z side).</summary>
    public static void Face(Transform t, Vector3 camPos, Vector3 camUp, bool yawOnly)
    {
        Vector3 d = t.position - camPos;
        if (yawOnly) d.y = 0f;
        if (d.sqrMagnitude < 1e-4f) return;
        t.rotation = Quaternion.LookRotation(d, yawOnly ? Vector3.up : camUp);
    }
}

/// <summary>
/// One pooled world-space text (TextMeshPro, the same text-mesh approach as the pay plate and the result board; ASCII only): a name
/// tag, a hand total, a floating "+$85". It faces the camera, can pop in, rise and fade out, and is shown by every machine.
/// </summary>
public sealed class CasinoLabel
{
    public readonly GameObject go;
    public readonly Transform t;
    public readonly TextMeshPro tmp;

    Vector3 pos;
    Color colour = Color.white;
    double born;
    float life, rise, fadeIn = 0.12f, fadeOut = 0.45f, scale = 1f, targetScale = 1f;
    bool shown, persistent, yawOnly;
    string text = "";

    public bool Shown => shown;
    public string Text => text;
    internal bool Released;

    internal CasinoLabel(Transform parent, Material mat)
    {
        go = new GameObject("Label") { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(parent, false);
        tmp = go.AddComponent<TextMeshPro>();
        t = go.transform; // after the TMP: adding it swaps the Transform for a RectTransform
        tmp.enableAutoSizing = false;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.rectTransform.sizeDelta = new Vector2(4f, 1f);
        tmp.richText = true;
        tmp.raycastTarget = false;
        if (mat != null) tmp.fontSharedMaterial = mat;
        var r = go.GetComponent<MeshRenderer>();
        if (r != null) { r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false; }
        go.SetActive(false);
    }

    /// <summary>Show text at a world position. <paramref name="fontSize"/> 1.0 is about a tenth of a metre tall; a <paramref name="lifeSeconds"/> of 0 keeps it until hidden.</summary>
    public void Show(string s, Vector3 world, Color c, float fontSize, double now, float lifeSeconds = 0f, float riseMetres = 0f, float popFrom = 0.6f, bool yawOnly = false)
    {
        this.yawOnly = yawOnly;
        text = s ?? "";
        if (tmp.text != text) tmp.text = text;
        tmp.fontSize = fontSize;
        pos = world; colour = c; born = now; life = lifeSeconds; rise = riseMetres; persistent = lifeSeconds <= 0f;
        scale = popFrom; targetScale = 1f;
        shown = true;
        t.position = world;
        tmp.color = new Color(c.r, c.g, c.b, 0f);
        go.SetActive(true);
        CasinoFacing.Add(t, yawOnly);
    }

    /// <summary>Change the text of a shown label without restarting its animation.</summary>
    public void SetText(string s)
    {
        s = s ?? "";
        if (s == text) return;
        text = s;
        tmp.text = s;
    }

    public void SetColour(Color c) { colour = c; }
    public void SetPosition(Vector3 world) { pos = world; }

    /// <summary>Fade out now (persistent labels) or let a timed one end.</summary>
    public void Hide(double now, bool instant = false)
    {
        if (!shown) return;
        if (instant) { shown = false; go.SetActive(false); CasinoFacing.Remove(t); return; }
        persistent = false; born = now - fadeIn; life = fadeOut + fadeIn; rise = 0f;
    }

    /// <summary>Per frame: face the camera, animate. Returns false once it has gone (the pool takes it back).</summary>
    internal bool Tick(double now, Vector3 camPos, Vector3 camUp, bool haveCam)
    {
        if (!shown) return false;
        float age = (float)(now - born);
        float a = Mathf.Clamp01(age / fadeIn);
        if (!persistent)
        {
            float left = life - age;
            if (left <= 0f) { shown = false; go.SetActive(false); CasinoFacing.Remove(t); return false; }
            a = Mathf.Min(a, Mathf.Clamp01(left / fadeOut));
        }
        scale = Mathf.MoveTowards(scale, targetScale, Time.unscaledDeltaTime * 4f);
        float lift = rise > 0f ? rise * (1f - Mathf.Pow(1f - Mathf.Clamp01(age / Mathf.Max(0.1f, life > 0f ? life : 1f)), 2f)) : 0f;
        t.position = pos + Vector3.up * lift;
        t.localScale = Vector3.one * scale;
        if (haveCam)
        {
            CasinoFacing.Face(t, camPos, camUp, yawOnly); // (the per-camera hook turns it again for whichever camera renders it)
        }
        var c = tmp.color;
        if (!Mathf.Approximately(c.a, a) || c.r != colour.r || c.g != colour.g || c.b != colour.b) tmp.color = new Color(colour.r, colour.g, colour.b, a);
        return true;
    }
}

/// <summary>The pool of world-space labels (Docs/Casino.md §11), one shared outlined material, ticked by <see cref="CasinoPresenters"/> while the player is in the room.</summary>
public sealed class CasinoLabels
{
    readonly Transform parent;
    readonly Stack<CasinoLabel> free = new Stack<CasinoLabel>();
    readonly List<CasinoLabel> active = new List<CasinoLabel>();
    Material outlined;

    public CasinoLabels(Transform parent) { this.parent = parent; }

    public int ActiveCount => active.Count;

    Material Outlined(TextMeshPro sample)
    {
        if (outlined != null) return outlined;
        var src = sample != null ? sample.fontSharedMaterial : null;
        if (src == null) return null;
        ShaderUtilities.GetShaderPropertyIDs();
        outlined = new Material(src) { name = "CasinoLabelOutlined", hideFlags = HideFlags.DontSave };
        outlined.EnableKeyword(ShaderUtilities.Keyword_Outline);
        outlined.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.22f);
        outlined.SetColor(ShaderUtilities.ID_OutlineColor, new Color(0.04f, 0.03f, 0.03f, 1f));
        return outlined;
    }

    /// <summary>A label shown now. Keep the returned object to move, change or hide it (persistent ones); timed ones need no cleanup.</summary>
    public CasinoLabel Show(string text, Vector3 world, Color colour, float fontSize, double now, float lifeSeconds = 0f, float riseMetres = 0f, float popFrom = 0.6f, bool yawOnly = false)
    {
        CasinoLabel l = null;
        while (free.Count > 0 && l == null) { l = free.Pop(); if (l != null && l.go == null) l = null; }
        if (l == null)
        {
            l = new CasinoLabel(parent, outlined);
            if (outlined == null)
            {
                var m = Outlined(l.tmp);
                if (m != null) l.tmp.fontSharedMaterial = m;
            }
        }
        l.Released = false;
        l.Show(text, world, colour, fontSize, now, lifeSeconds, riseMetres, popFrom, yawOnly);
        active.Add(l);
        return l;
    }

    /// <summary>Hide a label now and give it back.</summary>
    public void Hide(CasinoLabel l, double now, bool instant = false)
    {
        if (l == null) return;
        l.Hide(now, instant);
    }

    /// <summary>Per frame. Returns true while any label is shown (name tags are persistent).</summary>
    public bool Tick(double now)
    {
        if (active.Count == 0) return false;
        var cam = Camera.main;
        bool haveCam = cam != null;
        Vector3 cp = haveCam ? cam.transform.position : Vector3.zero;
        Vector3 cu = haveCam ? cam.transform.up : Vector3.up;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var l = active[i];
            if (l.go == null || !l.Tick(now, cp, cu, haveCam))
            {
                active.RemoveAt(i);
                if (l.go != null) { l.Released = true; free.Push(l); }
            }
        }
        return active.Count > 0;
    }

    public void DestroyAll()
    {
        for (int i = 0; i < active.Count; i++) if (active[i].go != null) { CasinoFacing.Remove(active[i].t); Object.Destroy(active[i].go); }
        foreach (var l in free) if (l != null && l.go != null) Object.Destroy(l.go);
        active.Clear(); free.Clear();
        if (outlined != null) Object.Destroy(outlined);
        outlined = null;
    }
}

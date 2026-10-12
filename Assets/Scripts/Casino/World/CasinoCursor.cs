using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The world cursor of seat mode (Docs/Casino.md §9 / §9.1): a 2D screen position (the mouse, or a virtual cursor driven by the
/// pad's left stick) that raycasts onto the table through the ACTUAL camera (<see cref="Camera.ScreenPointToRay"/>, so lens
/// effects, drunk sway and camera roll are all included; never the Cinemachine brain's idea of the pose).
/// <list type="bullet">
/// <item>Picks only colliders on <see cref="CasinoLayout.PickMask"/> that carry a <see cref="CasinoPickable"/> (buttons, bet
/// circles, chip bins, the layout plane, the lever, drink slots).</item>
/// <item><see cref="Hovered"/> / <see cref="HoverChanged"/>; <see cref="Clicked"/> for primary (mouse 0, pad A or RT) and
/// secondary (mouse 1, pad X or RB); a <see cref="CasinoButton"/> under a primary click is pressed automatically
/// (<see cref="autoPressButtons"/>).</item>
/// <item>A small cosmetic ring on the surface under the cursor (local, never networked) and an optional <see cref="ghost"/>.</item>
/// </list>
/// Mouse: while the OS cursor is free it follows the mouse; while it is locked (mouse delta is looking) the position stays at the
/// screen centre, a crosshair. Pad: the left stick moves a virtual cursor over the screen (the right stick looks).
/// Drive it yourself instead with <see cref="readInput"/> = false and <see cref="Position"/> / <see cref="PressPrimary"/>.
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class CasinoCursor : MonoBehaviour
{
    public enum Source { Mouse, Pad }

    public struct Click
    {
        public bool secondary;
        /// <summary>The ray hit a pickable.</summary>
        public bool hit;
        public CasinoPickable target;
        public Collider collider;
        public Vector3 point, normal;
        public Vector2 screen;
    }

    [Tooltip("The camera to cast from; null = Camera.main (re-resolved every frame).")]
    public Camera cam;
    public float maxDistance = 8f;
    [Tooltip("Read the mouse / pad here. Off: set Position and call PressPrimary / PressSecondary yourself.")]
    public bool readInput = true;
    [Tooltip("A primary click on an enabled CasinoButton presses it.")]
    public bool autoPressButtons = true;
    public bool drawMarker = true;
    [Tooltip("Only pick things of this station (null = any).")]
    public CasinoStation onlyStation;
    [Tooltip("Virtual cursor speed in pixels per second at full stick.")]
    public float padSpeed = 900f;
    [Tooltip("An optional ghost (e.g. a see-through chip): moved to the hit point while the cursor is over the table, hidden otherwise.")]
    public Transform ghost;
    public float ghostLift = 0.004f;

    public Source CurrentSource { get; private set; }
    public CasinoPickable Hovered { get; private set; }
    public Collider HoveredCollider { get; private set; }
    public bool HasHit => Hovered != null;
    public Vector3 HitPoint { get; private set; }
    public Vector3 HitNormal { get; private set; } = Vector3.up;
    public float HitDistance { get; private set; }
    /// <summary>The hovered thing's hint text (empty when nothing is hovered).</summary>
    public string HoverHint => Hovered != null ? Hovered.hint : "";

    /// <summary>Raised when the hovered thing changes: (old, new), either may be null.</summary>
    public event Action<CasinoPickable, CasinoPickable> HoverChanged;
    /// <summary>Raised on a primary or secondary press (also over empty table / nothing: <c>hit</c> is false then).</summary>
    public event Action<Click> Clicked;

    Vector2 position;
    bool positionSet;
    float suppressUntil;
    Transform marker;
    MeshRenderer markerRenderer;
    Material markerMaterial;
    MaterialPropertyBlock markerBlock;
    static readonly RaycastHit[] hits = new RaycastHit[24];
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), ColorId = Shader.PropertyToID("_Color");

    /// <summary>Screen position in pixels (bottom-left origin, like <c>Input.mousePosition</c>).</summary>
    public Vector2 Position
    {
        get
        {
            if (!positionSet) { position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f); positionSet = true; }
            return position;
        }
        set
        {
            position = new Vector2(Mathf.Clamp(value.x, 0f, Screen.width), Mathf.Clamp(value.y, 0f, Screen.height));
            positionSet = true;
        }
    }

    /// <summary>The camera this cursor casts from.</summary>
    public Camera Cam => cam != null ? cam : Camera.main;

    /// <summary>The ray through <see cref="Position"/>: the actual camera's, lens effects and roll included.</summary>
    public Ray Ray()
    {
        var c = Cam;
        return c != null ? c.ScreenPointToRay(Position) : new Ray(transform.position, transform.forward);
    }

    /// <summary>Ignore clicks for a moment (the press that opened the seat must not also act).</summary>
    public void SuppressFor(float seconds) => suppressUntil = Mathf.Max(suppressUntil, Time.unscaledTime + seconds);

    /// <summary>Make one at a place in the hierarchy (or on a fresh object).</summary>
    public static CasinoCursor Create(Transform parent = null)
    {
        var go = new GameObject("[CasinoCursor]");
        if (parent != null) go.transform.SetParent(parent, false);
        return go.AddComponent<CasinoCursor>();
    }

    void OnEnable() { SuppressFor(0.25f); }

    void OnDisable()
    {
        SetHovered(null, null);
        HitPoint = Vector3.zero;
        if (marker != null) marker.gameObject.SetActive(false);
        if (ghost != null) ghost.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (marker != null) Destroy(marker.gameObject);
        if (markerMaterial != null) Destroy(markerMaterial);
    }

    void Update() => Tick();

    /// <summary>One step: read input (if <see cref="readInput"/>), raycast, update hover, marker and clicks. Called every frame by
    /// the component itself; call it yourself only if you disabled the component.</summary>
    public void Tick()
    {
        var c = Cam;
        if (readInput) ReadPosition();
        if (c == null || !c.isActiveAndEnabled) { SetHovered(null, null); HideVisuals(); return; }

        Pick(c);
        UpdateVisuals();

        if (readInput && Time.unscaledTime >= suppressUntil)
        {
            if (PrimaryPressed()) PressPrimary();
            else if (SecondaryPressed()) PressSecondary();
        }
    }

    // ------------------------------------------------------------------ input

    static Gamepad Pad => Gamepad.current;

    void ReadPosition()
    {
        var pad = Pad;
        Vector2 stick = pad != null ? pad.leftStick.ReadValue() : Vector2.zero;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool mouseMoved = !locked && (Input.mousePosition - (Vector3)lastMouse).sqrMagnitude > 4f;
        if (locked) mouseMoved = Mathf.Abs(Input.GetAxisRaw("Mouse X")) + Mathf.Abs(Input.GetAxisRaw("Mouse Y")) > 0.02f;
        lastMouse = Input.mousePosition;

        if (stick.sqrMagnitude > 0.09f) CurrentSource = Source.Pad;
        else if (mouseMoved || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || pad == null) CurrentSource = Source.Mouse;

        if (CurrentSource == Source.Pad)
        {
            if (!positionSet) Position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (stick.sqrMagnitude > 0.04f)
            {
                float k = padSpeed * Time.unscaledDeltaTime * Screen.height / 1080f;
                Position = position + stick * k;
            }
        }
        else Position = locked ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : (Vector2)Input.mousePosition;
    }

    Vector2 lastMouse;

    static bool PrimaryPressed()
    {
        if (Input.GetMouseButtonDown(0)) return true;
        var p = Pad;
        return p != null && (p.buttonSouth.wasPressedThisFrame || p.rightTrigger.wasPressedThisFrame);
    }

    static bool SecondaryPressed()
    {
        if (Input.GetMouseButtonDown(1)) return true;
        var p = Pad;
        return p != null && (p.buttonWest.wasPressedThisFrame || p.rightShoulder.wasPressedThisFrame);
    }

    /// <summary>A primary press at the current position (what a click does).</summary>
    public void PressPrimary() => Press(false);

    /// <summary>A secondary press at the current position.</summary>
    public void PressSecondary() => Press(true);

    void Press(bool secondary)
    {
        var click = new Click
        {
            secondary = secondary, hit = Hovered != null, target = Hovered, collider = HoveredCollider,
            point = HitPoint, normal = HitNormal, screen = Position,
        };
        if (!secondary && autoPressButtons && Hovered is CasinoButton b && b.Clickable) b.Press();
        try { Clicked?.Invoke(click); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ picking

    /// <summary>Raycast the pick layer through <see cref="Position"/>; the nearest collider that has a pickable.</summary>
    public bool Raycast(out RaycastHit best)
    {
        best = default;
        var c = Cam;
        if (c == null) return false;
        var ray = c.ScreenPointToRay(Position);
        int n = Physics.RaycastNonAlloc(ray, hits, maxDistance, CasinoLayout.PickMask, QueryTriggerInteraction.Collide);
        float bd = float.MaxValue;
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            if (h.distance >= bd) continue;
            var p = h.collider != null ? h.collider.GetComponentInParent<CasinoPickable>() : null;
            if (p == null || !p.isActiveAndEnabled) continue;
            if (onlyStation != null && p.station != null && p.station != onlyStation) continue;
            best = h; bd = h.distance; any = true;
        }
        return any;
    }

    void Pick(Camera c)
    {
        if (!Raycast(out var h))
        {
            SetHovered(null, null);
            return;
        }
        var p = h.collider.GetComponentInParent<CasinoPickable>();
        HitPoint = h.point;
        // A trigger collider box gives a zero normal when the ray starts inside; keep the last good one then.
        HitNormal = h.normal.sqrMagnitude > 0.5f ? h.normal : HitNormal;
        HitDistance = h.distance;
        SetHovered(p, h.collider);
    }

    void SetHovered(CasinoPickable p, Collider col)
    {
        HoveredCollider = col;
        if (p == Hovered) return;
        var old = Hovered;
        if (old != null) old.SetHover(false);
        Hovered = p;
        if (p != null) p.SetHover(true);
        try { HoverChanged?.Invoke(old, p); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ cosmetics

    void HideVisuals()
    {
        if (marker != null && marker.gameObject.activeSelf) marker.gameObject.SetActive(false);
        if (ghost != null && ghost.gameObject.activeSelf) ghost.gameObject.SetActive(false);
    }

    void UpdateVisuals()
    {
        bool on = Hovered != null;
        if (ghost != null)
        {
            if (ghost.gameObject.activeSelf != on) ghost.gameObject.SetActive(on);
            if (on) ghost.position = HitPoint + HitNormal * ghostLift;
        }
        if (!drawMarker || !on) { if (marker != null && marker.gameObject.activeSelf) marker.gameObject.SetActive(false); return; }
        if (marker == null) BuildMarker();
        if (marker == null) return;
        if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
        marker.position = HitPoint + HitNormal * 0.003f;
        marker.rotation = Quaternion.FromToRotation(Vector3.up, HitNormal);
        bool ok = Hovered.Clickable;
        float pulse = 1f + 0.08f * Mathf.Sin(Time.unscaledTime * 8f);
        marker.localScale = Vector3.one * (ok ? pulse : 0.8f);
        if (markerBlock == null) markerBlock = new MaterialPropertyBlock();
        Color col = ok ? new Color(1f, 0.82f, 0.35f, 0.95f) : new Color(0.7f, 0.7f, 0.7f, 0.6f);
        markerRenderer.GetPropertyBlock(markerBlock);
        markerBlock.SetColor(BaseColorId, col);
        markerBlock.SetColor(ColorId, col);
        markerRenderer.SetPropertyBlock(markerBlock);
    }

    void BuildMarker()
    {
        var go = new GameObject("CasinoCursorMarker") { hideFlags = HideFlags.DontSave };
        marker = go.transform;
        go.AddComponent<MeshFilter>().sharedMesh = RingMesh();
        markerRenderer = go.AddComponent<MeshRenderer>();
        markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        markerRenderer.receiveShadows = false;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader == null) { Destroy(go); marker = null; return; }
        markerMaterial = new Material(shader) { name = "CasinoCursorRing (runtime)", hideFlags = HideFlags.DontSave };
        markerRenderer.sharedMaterial = markerMaterial;
        go.SetActive(false);
    }

    static Mesh ringMesh;

    static Mesh RingMesh()
    {
        if (ringMesh != null) return ringMesh;
        const int seg = 36;
        const float r0 = 0.020f, r1 = 0.028f;
        var v = new Vector3[seg * 2];
        var n = new Vector3[seg * 2];
        var t = new int[seg * 6 * 2];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v[i * 2] = new Vector3(Mathf.Cos(a) * r0, 0f, Mathf.Sin(a) * r0);
            v[i * 2 + 1] = new Vector3(Mathf.Cos(a) * r1, 0f, Mathf.Sin(a) * r1);
            n[i * 2] = n[i * 2 + 1] = Vector3.up;
            int j = (i + 1) % seg, k = i * 12;
            int a0 = i * 2, a1 = i * 2 + 1, b0 = j * 2, b1 = j * 2 + 1;
            // up-facing and down-facing, so it shows from any side
            t[k] = a0; t[k + 1] = b0; t[k + 2] = a1; t[k + 3] = a1; t[k + 4] = b0; t[k + 5] = b1;
            t[k + 6] = a0; t[k + 7] = a1; t[k + 8] = b0; t[k + 9] = a1; t[k + 10] = b1; t[k + 11] = b0;
        }
        ringMesh = new Mesh { name = "CasinoCursorRing", hideFlags = HideFlags.DontSave };
        ringMesh.vertices = v; ringMesh.normals = n; ringMesh.triangles = t;
        ringMesh.RecalculateBounds();
        return ringMesh;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => ringMesh = null;
}

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

/// <summary>
/// Small shared toolbox for the Sorting system: scripted arcs, ballistic launch maths, runtime materials,
/// world-space labels and text formatting. Everything here is local-only (no networking).
/// </summary>
public static class SortingKit
{
    public const int InteractableLayer = 3;
    public const int GroundLayer = 6;
    public const int WallsLayer = 7;

    // ------------------------------------------------------------------ colors

    public static readonly Color CashGreen = new Color(0.45f, 1f, 0.45f);
    public static readonly Color ComboOrange = new Color(1f, 0.65f, 0.15f);
    public static readonly Color BadRed = new Color(1f, 0.3f, 0.25f);
    public static readonly Color Gold = new Color(1f, 0.84f, 0.2f);
    public static readonly Color GunkBrown = new Color(0.22f, 0.2f, 0.16f);
    public static readonly Color SackBlack = new Color(0.08f, 0.08f, 0.1f);

    public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

    /// <summary>
    /// Feel's HUD already floats "+$0.42" wherever GameSession.AddCash is given a world position, so Sorting's
    /// own cash floaters are off. Flip on if that ever changes (combo / hint floaters always show).
    /// </summary>
    public static bool CashFloaters = false;

    /// <summary>World "+$0.42" / "-$0.25" floater, only when CashFloaters is on.</summary>
    public static void CashFloater(Vector3 pos, float amount, float scale = 1f)
    {
        if (!CashFloaters) return;
        GameHUD.FloatingText(pos, (amount >= 0f ? "+" : "") + Economy.Money(amount), amount >= 0f ? CashGreen : BadRed, scale);
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>"15 g" for tiny things, "0.3 kg" / "12.4 kg" otherwise.</summary>
    public static string Mass(float kg)
    {
        if (kg < 0.095f) return Mathf.Max(1f, Mathf.Round(kg * 1000f)).ToString("0") + " g";
        return kg.ToString(kg < 10f ? "0.0#" : "0.0") + " kg";
    }

    /// <summary>Vibe-rounded valuable price ("~$2,300", "~$85").</summary>
    public static string Appraisal(float value)
    {
        float step = value >= 1000f ? 100f : value >= 200f ? 10f : value >= 50f ? 5f : 1f;
        float r = Mathf.Max(1f, Mathf.Round(value / step) * step);
        return "~$" + r.ToString("N0");
    }

    /// <summary>Round money to whole cents (never below one cent for positive amounts).</summary>
    public static float Cents(float v)
    {
        float r = Mathf.Round(v * 100f) / 100f;
        return v > 0f ? Mathf.Max(0.01f, r) : r;
    }

    public static string Plural(int n, string one, string many) => n == 1 ? one : many;

    // Optional flavor from the Items system ("Party Bag!"). Looked up by reflection so Sorting never depends
    // on non-contract members.
    static MethodInfo bagThemeMethod;
    static bool bagThemeLooked;
    public static string BagThemeLabel(BagContents bag)
    {
        if (bag == null) return null;
        if (!bagThemeLooked)
        {
            bagThemeLooked = true;
            bagThemeMethod = typeof(ItemCatalog).GetMethod("BagTheme", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BagContents) }, null);
        }
        if (bagThemeMethod == null || bagThemeMethod.ReturnType != typeof(string)) return null;
        try { return bagThemeMethod.Invoke(null, new object[] { bag }) as string; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ carryable helpers

    /// <summary>Lying around in the world (not held, not already eaten by a receiver).</summary>
    public static bool IsLoose(Carryable c) =>
        c != null && c.State != CarryState.Carried && c.State != CarryState.Consumed;

    /// <summary>World bounds of every renderer under root (falls back to a small box at the position).</summary>
    public static Bounds RendererBounds(GameObject root)
    {
        var b = new Bounds(root.transform.position, Vector3.zero);
        bool any = false;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (r is ParticleSystemRenderer || r.GetComponent<TMP_Text>() != null) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!any) b.size = Vector3.one * 0.2f;
        return b;
    }

    /// <summary>
    /// Renderer bounds in root's local space (independent of how it's currently rotated). Mesh bounds are
    /// transformed exactly; other renderers fall back to their world AABB.
    /// </summary>
    public static Bounds LocalRendererBounds(Transform root)
    {
        var inv = root.worldToLocalMatrix;
        bool any = false;
        var b = new Bounds(Vector3.zero, Vector3.one * 0.2f);
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (r is ParticleSystemRenderer || r.GetComponent<TMP_Text>() != null) continue;
            var mf = r.GetComponent<MeshFilter>();
            Bounds src; Matrix4x4 m;
            if (mf != null && mf.sharedMesh != null) { src = mf.sharedMesh.bounds; m = inv * r.transform.localToWorldMatrix; }
            else { src = r.bounds; m = inv; }
            Vector3 c = src.center, e = src.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    /// <summary>Carryables whose colliders overlap a world box (deduplicated, loose only).</summary>
    public static List<Carryable> CarryablesInBox(Vector3 center, Vector3 halfExtents, Quaternion rotation, List<Carryable> into = null)
    {
        into ??= new List<Carryable>();
        into.Clear();
        var hits = Physics.OverlapBox(center, halfExtents, rotation, 1 << InteractableLayer, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            var c = h.GetComponentInParent<Carryable>();
            if (IsLoose(c) && !into.Contains(c)) into.Add(c);
        }
        return into;
    }

    /// <summary>The local on-foot player's position, if there is one.</summary>
    public static bool TryGetLocalPlayer(out Vector3 pos)
    {
        var p = PlayerController.Local;
        if (p != null && !p.IsDriving) { pos = p.transform.position; return true; }
        pos = default;
        return false;
    }

    public static bool LocalPlayerDriving => PlayerController.Local != null && PlayerController.Local.IsDriving;

    /// <summary>What the local player is carrying right now (null if nothing).</summary>
    public static Carryable LocalHeld => TrashCarrier.Local != null ? TrashCarrier.Local.Held : null;

    // ------------------------------------------------------------------ motion

    /// <summary>Launch velocity that flies from -> to in exactly `time` seconds under Physics.gravity.</summary>
    public static Vector3 Ballistic(Vector3 from, Vector3 to, float time)
    {
        time = Mathf.Max(0.05f, time);
        return (to - from) / time - 0.5f * time * Physics.gravity;
    }

    /// <summary>
    /// Flight time for a launch with upward speed vy starting `drop` metres above the landing height
    /// (positive drop = starting higher).
    /// </summary>
    public static float FlightTime(float vy, float drop)
    {
        float g = -Physics.gravity.y;
        float disc = vy * vy + 2f * g * drop;
        return (vy + Mathf.Sqrt(Mathf.Max(0f, disc))) / g;
    }

    /// <summary>Scripted parabolic hop of a (kinematic) transform. Optionally spins toward endRot.</summary>
    public static IEnumerator Arc(Transform t, Vector3 to, float duration, float height, Quaternion? endRot = null)
    {
        if (t == null) yield break;
        Vector3 from = t.position;
        Quaternion r0 = t.rotation;
        float d = Mathf.Max(0.01f, duration);
        for (float e = 0f; e < d; e += Time.deltaTime)
        {
            if (t == null) yield break;
            float k = e / d;
            Vector3 p = Vector3.Lerp(from, to, k);
            p.y += height * 4f * k * (1f - k);
            t.position = p;
            if (endRot.HasValue) t.rotation = Quaternion.Slerp(r0, endRot.Value, k);
            yield return null;
        }
        if (t == null) yield break;
        t.position = to;
        if (endRot.HasValue) t.rotation = endRot.Value;
    }

    /// <summary>Arc duration that feels snappy for a given distance.</summary>
    public static float ArcDuration(float distance) => Mathf.Clamp(0.12f + distance * 0.07f, 0.14f, 0.38f);

    public static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float a = x - 1f;
        return 1f + c3 * a * a * a + c1 * a * a;
    }

    public static float EaseInQuad(float x) => x * x;
    public static float EaseOutQuad(float x) => 1f - (1f - x) * (1f - x);

    /// <summary>Downward raycast against Ground for the floor height under a point (fallback given).</summary>
    public static float FloorY(Vector3 above, float fallback)
    {
        if (Physics.Raycast(above + Vector3.up * 2f, Vector3.down, out var hit, 10f, 1 << GroundLayer, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return fallback;
    }

    // ------------------------------------------------------------------ materials

    static readonly Dictionary<string, Material> matCache = new Dictionary<string, Material>();

    /// <summary>
    /// A shared runtime material: the RubbishAssets entry for key if registered (Setup_SortingStation does),
    /// otherwise a URP Lit/Unlit material created once and cached.
    /// </summary>
    public static Material RuntimeMaterial(string key, Color color, bool unlit = false)
    {
        if (matCache.TryGetValue(key, out var m) && m != null) return m;
        m = RubbishAssets.Material(key);
        if (m == null)
        {
            var shader = Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find(unlit ? "Unlit/Color" : "Standard");
            m = new Material(shader) { name = key };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else m.color = color;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
        }
        matCache[key] = m;
        return m;
    }

    /// <summary>Inverted-hull outline material (front faces culled). Tint per renderer with a property block.</summary>
    public static Material OutlineMaterial()
    {
        const string key = "mat/sorting_outline";
        if (matCache.TryGetValue(key, out var m) && m != null) return m;
        m = RubbishAssets.Material(key);
        if (m == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            m = new Material(shader) { name = key };
            m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 1f); // Front
        }
        matCache[key] = m;
        return m;
    }

    /// <summary>Visual-only cube (no collider) for runtime bits: sack scraps, bale straps.</summary>
    public static GameObject VisualCube(string name, Transform parent, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    // ------------------------------------------------------------------ text

    /// <summary>World-space TextMeshPro label (created as a child, centered, no wrapping).</summary>
    public static TextMeshPro MakeText(Transform parent, string name, string text, float size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.richText = true;
        tmp.fontStyle = FontStyles.Bold;
        tmp.outlineWidth = 0.18f;
        tmp.outlineColor = new Color32(0, 0, 0, 200);
        tmp.rectTransform.sizeDelta = new Vector2(4f, 1f);
        return tmp;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        matCache.Clear();
        bagThemeLooked = false;
        bagThemeMethod = null;
    }
}

/// <summary>
/// Sort combo: consecutive correct sorts within Economy.ComboWindow seconds stack +Economy.ComboStep each,
/// capped at Economy.ComboMax. Wrong bins break it.
/// </summary>
public static class SortCombo
{
    /// <summary>Correct sorts in the current streak (0 = no streak).</summary>
    public static int Streak { get; private set; }
    static float lastTime = -999f;

    /// <summary>(streak, multiplier) whenever the combo changes. HUD may show a meter.</summary>
    public static event System.Action<int, float> Changed;

    public static bool Alive => Streak > 0 && Time.time - lastTime <= Economy.ComboWindow;

    /// <summary>Current payout multiplier (1 when no streak).</summary>
    public static float Multiplier => Alive ? MultiplierFor(Streak) : 1f;

    /// <summary>0..1 how much of the combo window is left (for a HUD bar).</summary>
    public static float TimeLeft01 => Alive ? 1f - (Time.time - lastTime) / Economy.ComboWindow : 0f;

    public static float MultiplierFor(int streak) =>
        Mathf.Min(Economy.ComboMax, 1f + Economy.ComboStep * Mathf.Max(0, streak - 1));

    /// <summary>A short major-scale climb in a lower register, held at a fifth for long streaks.</summary>
    public static float DingPitchFor(int streak)
    {
        int step = Mathf.Clamp(streak, 1, 5);
        int semitones = step == 1 ? 0 : step == 2 ? 2 : step == 3 ? 4 : step == 4 ? 5 : 7;
        return .75f * Mathf.Pow(2f, semitones / 12f);
    }

    // The extra combo flourish must not keep climbing after the main ding has settled.
    public static float FlourishPitchFor(int streak) => DingPitchFor(streak) * .85f;

    /// <summary>Count a correct sort. Returns the multiplier to apply to this sort's payout.</summary>
    public static float RegisterCorrect()
    {
        if (!Alive) Streak = 0;
        Streak++;
        lastTime = Time.time;
        float m = MultiplierFor(Streak);
        Changed?.Invoke(Streak, m);
        return m;
    }

    public static void Break()
    {
        if (Streak == 0) return;
        Streak = 0;
        Changed?.Invoke(0, 1f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Streak = 0;
        lastTime = -999f;
        Changed = null;
    }
}

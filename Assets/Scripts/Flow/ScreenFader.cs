using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Full-screen fade for scene transitions. Built at runtime (no prefab / scene setup), lives in
/// DontDestroyOnLoad, sits just under the LoadingSystem overlay. While black it shows a dumb wobbling quip.
/// Uses unscaled time so slow-mo / HitStop never stalls a transition.
///
/// Usage: <c>yield return ScreenFader.FadeOut();</c> … load … <c>yield return ScreenFader.FadeIn();</c>
/// </summary>
public class ScreenFader : MonoBehaviour
{
    public const float DefaultFadeOut = 0.35f;
    public const float DefaultFadeIn = 0.45f;
    const int SortingOrder = 32000; // LoadingSystem uses short.MaxValue (32767)

    static readonly string[] Quips =
    {
        "beep beep beep...",
        "*honk honk*",
        "smells like progress",
        "loading more garbage...",
        "one person's trash...",
        "vroom (responsibly)",
        "sniffing the bins...",
        "rattle rattle clunk",
        "don't look in the hopper",
        "garbage day, baby!",
    };

    static ScreenFader instance;

    Canvas canvas;
    CanvasGroup group;
    RectTransform quipRect;
    TextMeshProUGUI quip;
    Coroutine running;
    float wobbleTime;
    float quipPop;

    /// <summary>Current overlay alpha (0 = clear, 1 = black).</summary>
    public static float Alpha => instance != null ? instance.group.alpha : 0f;
    /// <summary>True while the screen is (fully) covered.</summary>
    public static bool IsBlack => Alpha >= 0.99f;
    /// <summary>True while a fade is animating.</summary>
    public static bool IsFading => instance != null && instance.running != null;

    /// <summary>Fade to black. Returns a Coroutine you can yield on.</summary>
    public static Coroutine FadeOut(float duration = DefaultFadeOut, bool showQuip = true)
    {
        var f = Ensure();
        if (showQuip) f.PickQuip();
        return f.StartFade(1f, duration);
    }

    /// <summary>Fade back to the game. Returns a Coroutine you can yield on.</summary>
    public static Coroutine FadeIn(float duration = DefaultFadeIn)
    {
        var f = Ensure();
        return f.StartFade(0f, duration);
    }

    /// <summary>Snap the overlay to an alpha immediately (cancels a running fade).</summary>
    public static void SetAlpha(float alpha)
    {
        var f = Ensure();
        if (f.running != null) { f.StopCoroutine(f.running); f.running = null; }
        f.Apply(alpha);
    }

    static ScreenFader Ensure()
    {
        if (instance != null) return instance;
        var go = new GameObject("ScreenFader");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ScreenFader>();
        instance.Build();
        return instance;
    }

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>(); // swallow clicks while black

        group = gameObject.AddComponent<CanvasGroup>();
        group.interactable = false;

        var bg = new GameObject("Black", typeof(RectTransform));
        bg.transform.SetParent(transform, false);
        var bgRt = (RectTransform)bg.transform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = new Vector2(-8, -8);
        bgRt.offsetMax = new Vector2(8, 8);
        var img = bg.AddComponent<Image>();
        img.color = new Color32(0x1c, 0x19, 0x22, 0xFF);

        var q = new GameObject("Quip", typeof(RectTransform));
        q.transform.SetParent(transform, false);
        quipRect = (RectTransform)q.transform;
        quipRect.anchorMin = quipRect.anchorMax = new Vector2(1f, 0f);
        quipRect.pivot = new Vector2(1f, 0f);
        quipRect.anchoredPosition = new Vector2(-70f, 60f);
        quipRect.sizeDelta = new Vector2(900f, 90f);
        quip = q.AddComponent<TextMeshProUGUI>();
        quip.alignment = TextAlignmentOptions.BottomRight;
        quip.fontSize = 46f;
        quip.fontStyle = FontStyles.Bold | FontStyles.Italic;
        quip.color = new Color(1f, 0.84f, 0.25f);
        quip.raycastTarget = false;
        quip.text = "";

        Apply(0f);
    }

    void PickQuip()
    {
        if (quip == null) return;
        quip.text = Quips[Random.Range(0, Quips.Length)];
        quipPop = 0f;
    }

    Coroutine StartFade(float target, float duration)
    {
        if (running != null) StopCoroutine(running);
        running = StartCoroutine(FadeRoutine(target, duration));
        return running;
    }

    IEnumerator FadeRoutine(float target, float duration)
    {
        float start = group.alpha;
        float t = 0f;
        duration = Mathf.Max(0.01f, duration);
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
            Apply(Mathf.Lerp(start, target, k));
            yield return null;
        }
        Apply(target);
        running = null;
    }

    void Apply(float alpha)
    {
        group.alpha = alpha;
        group.blocksRaycasts = alpha > 0.5f;
        canvas.enabled = alpha > 0.001f;
    }

    void Update()
    {
        if (!canvas.enabled || quipRect == null) return;
        // Derpy wobble + pop-in on the quip while the screen is dark.
        wobbleTime += Time.unscaledDeltaTime;
        quipPop = Mathf.Min(1f, quipPop + Time.unscaledDeltaTime * 4f);
        float pop = 1f + Mathf.Sin(quipPop * Mathf.PI) * 0.25f;
        float breathe = 1f + Mathf.Sin(wobbleTime * 5f) * 0.03f;
        quipRect.localScale = Vector3.one * (pop * breathe * Mathf.Clamp01(quipPop * 2f));
        quipRect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(wobbleTime * 7f) * 3.5f);
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>A fully readable, temporary warning; no modal, cursor capture, raycast target, or input blocker.</summary>
public sealed class CompactorConfirmOverlay : MonoBehaviour
{
    static CompactorConfirmOverlay current;
    Compactor source;
    float expires, shownAt;
    RectTransform card;
    public static bool IsShowing => current != null;

    public static void Show(Compactor source, string message, float seconds)
    {
        if (!Application.isPlaying || Application.isBatchMode || source == null || string.IsNullOrEmpty(message)) return;
        if (current != null) Destroy(current.gameObject);
        var canvas = UiKit.Canvas("Compactor confirmation (non-interactive)", UiLayers.Hud + 8, source.transform);
        var raycaster = canvas.GetComponent<GraphicRaycaster>();
        if (raycaster != null) raycaster.enabled = false;
        var group = canvas.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;
        var overlay = canvas.gameObject.AddComponent<CompactorConfirmOverlay>();
        current = overlay;
        overlay.source = source;
        overlay.shownAt = Time.unscaledTime;
        overlay.expires = Time.unscaledTime + Mathf.Max(0.1f, seconds);
        overlay.card = FeelUi.Rect("Readable warning", canvas.transform, new Vector2(0.5f, 0.57f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 210f));
        FeelUi.Panel(overlay.card, new Color(0.19f, 0.10f, 0.25f, 0.97f), 0.7f);
        var label = FeelUi.Text(overlay.card, "Full warning", 26f, Color.white, TextAlignmentOptions.Center);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Overflow;
        label.rectTransform.offsetMin = new Vector2(28f, 20f);
        label.rectTransform.offsetMax = new Vector2(-28f, -20f);
        label.text = message;
        float preferred = label.GetPreferredValues(message, 764f, 0f).y;
        overlay.card.sizeDelta = new Vector2(820f, Mathf.Clamp(preferred + 42f, 210f, 330f));
    }

    public static void Clear(Compactor source)
    {
        if (current == null || (source != null && current.source != source)) return;
        var old = current;
        current = null;
        if (old != null) Destroy(old.gameObject);
    }

    void Update()
    {
        if (current != this) { Destroy(gameObject); return; }
        if (source == null || !source.isActiveAndEnabled || !source.LocalWatching || Time.unscaledTime >= expires)
        { Clear(source); return; }
        float t = Mathf.Clamp01((Time.unscaledTime - shownAt) / 0.16f);
        if (card != null) card.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, Mathf.SmoothStep(0f, 1f, t));
    }

    void OnDisable()
    {
        if (current == this) current = null;
        if (Application.isPlaying) Destroy(gameObject);
    }
    void OnDestroy()
    {
        if (current == this) current = null;
        var scaler = GetComponent<CanvasScaler>();
        if (scaler != null) UiScale.Unregister(scaler);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => current = null;
}

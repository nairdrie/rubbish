using TMPro;
using UnityEngine;

/// <summary>
/// Whose wallet the HUD shows (Docs/Accounts.md §3.4 "the HUD wallet carries the company's name and colour", package N1): a
/// small tag hanging under FeelHud's cash panel, "YOUR BUSINESS ● RUBBISH CO.", in the business's colour. Only in a Town with friends
/// (solo shows nothing new). Its own overlay canvas just above the HUD; follows <see cref="HudLayout.Layout"/>'s cash rect and
/// hides in the modes where the cash panel is off (station, map, solo pause). Never built in batchmode.
/// </summary>
public sealed class BusinessHud : MonoBehaviour
{
    static BusinessHud instance;

    Canvas canvas;
    CanvasGroup group;
    RectTransform tag;
    TextMeshProUGUI label;
    string shown;
    bool dirty = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode || instance != null) return;
        var c = UiKit.Canvas("[BusinessHud]", FeelHud.SortingOrder + 1);
        instance = c.gameObject.AddComponent<BusinessHud>();
        instance.canvas = c;
        instance.Build();
    }

    void Build()
    {
        group = gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;
        var root = (RectTransform)transform;
        tag = FeelUi.Rect("BusinessTag", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(320f, 29f));
        tag.localRotation = Quaternion.Euler(0f, 0f, 1.5f);
        FeelUi.Img(tag, "Body", FeelUi.Rounded, new Color(0.11f, 0.09f, 0.17f, 0.95f), 0.5f);
        label = FeelUi.Text(tag, "Name", 19f, Color.white, TextAlignmentOptions.Left);
        label.enableAutoSizing = true;
        label.fontSizeMin = 16f;
        label.fontSizeMax = 19f;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(14f, 0f);
        label.rectTransform.offsetMax = new Vector2(-10f, 0f);
        Businesses.Changed += MarkDirty;
        GameSession.Changed += MarkDirty;
    }

    void OnDestroy()
    {
        Businesses.Changed -= MarkDirty;
        GameSession.Changed -= MarkDirty;
        if (instance == this) instance = null;
    }

    void MarkDirty() => dirty = true;

    void LateUpdate()
    {
        var frame = HudLayout.Layout;
        var mode = HudLayout.Mode;
        bool coop = NetSession.InSession && !NetSession.IsSolo;
        bool cashShown = mode == HudMode.Driving || mode == HudMode.Street || mode == HudMode.Depot || mode == HudMode.Laptop ||
                         (mode == HudMode.Paused && coop);
        bool show = coop && frame.valid && cashShown;
        group.alpha = Mathf.MoveTowards(group.alpha, show ? 1f : 0f, Time.unscaledDeltaTime * 6f);
        if (!show && group.alpha <= 0f) return;

        if (dirty)
        {
            dirty = false;
            var b = Businesses.Local;
            string name = b.IsValid ? b.Name : GameSession.BusinessName;
            int color = b.IsValid ? b.color : 0;
            string text = $"YOUR CASH  <color={BusinessColors.Hex(color)}>●</color> {name.ToUpperInvariant()}";
            if (text != shown)
            {
                shown = text;
                label.text = text;
            }
        }
        // Hang the tag off the cash panel's bottom-left corner.
        var cash = frame.cash;
        float x = cash.x + 14f;
        HudLayout.PlaceAt(tag, new Vector2(x, HudLayout.TopBarBottom + 4f));
        // Stay left of the payroll sticker, which starts under the clock. A short badge also clears the
        // HUD's side columns and toast lane below the top bar.
        float maxW = Mathf.Max(1f, frame.clock.xMin - x - 8f);
        float w = Mathf.Min(maxW, Mathf.Max(180f, label.preferredWidth + 24f));
        tag.sizeDelta = new Vector2(w, 29f);
    }
}

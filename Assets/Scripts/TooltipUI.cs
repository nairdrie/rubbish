using TMPro;
using UnityEngine;

/// <summary>
/// The legacy interaction prompt, now a forwarder (Docs/UIPass.md §8.1 H1). Its API (<see cref="Show"/>,
/// <see cref="AppendAltHint"/>, <see cref="Hide"/>) is unchanged, so PlayerController, the truck and the sort station keep
/// calling it as before; the prompt itself is drawn by the HUD's auto-sizing <see cref="FeelHudPrompt"/> (key cap + text, at
/// most 2 lines, an alt row), which fixes the fixed-size box that let long names spill out and the key cap overlap the text.
/// The scene box in CommonWorldElements stays inactive.
/// </summary>
public class TooltipUI : MonoBehaviour
{
    public static TooltipUI Instance;

    // The old scene box (kept serialized so the prefab doesn't lose its references; never shown any more).
    [SerializeField] private GameObject tooltip;
    [SerializeField] private TextMeshProUGUI tooltipKey;
    [SerializeField] private TextMeshProUGUI tooltipText;
    [SerializeField] private GameObject altHintGroup;
    [SerializeField] private TextMeshProUGUI altHintKey;
    [SerializeField] private TextMeshProUGUI altHintMessage;

    void Awake()
    {
        Instance = this;
        KeepSceneBoxHidden();
        FeelHudPrompt.Hide();
    }

    // (No OnDestroy reset: PlayerController calls Instance.Show without a null check, and these methods only touch statics
    // and null-checked objects, so a stale instance between scene loads is harmless.)

    void KeepSceneBoxHidden()
    {
        if (tooltip != null && tooltip.activeSelf) tooltip.SetActive(false);
        if (altHintGroup != null && altHintGroup.activeSelf) altHintGroup.SetActive(false);
    }

    public void Show(KeyCode key, string message)
    {
        KeepSceneBoxHidden();
        string cap = GameControls.UsingGamepad && key == IInteractable.InteractKey ? "X" :
                     GameControls.UsingGamepad && key == IInteractable.AltInteractKey ? "Y" : FeelUi.KeyName(key);
        FeelHudPrompt.Show(cap, message);
    }

    public void AppendAltHint(KeyCode key, string message)
    {
        string cap = GameControls.UsingGamepad && key == IInteractable.AltInteractKey ? "Y" : FeelUi.KeyName(key);
        FeelHudPrompt.Alt(cap, message);
    }

    public void Hide()
    {
        KeepSceneBoxHidden();
        FeelHudPrompt.Hide();
    }
}

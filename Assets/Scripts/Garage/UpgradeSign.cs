using TMPro;
using UnityEngine;

/// <summary>A sign whose text follows an upgrade level (recycling contracts: who's buying your recycling this week).</summary>
public class UpgradeSign : MonoBehaviour
{
    public string id;
    [Tooltip("Text per level (index = level; level 0 usually hidden by GarageUpgradeVisuals).")]
    public string[] texts = new string[0];
    public TextMeshPro text;

    void OnEnable()
    {
        GameSession.UpgradeChanged += OnChanged;
        BusinessUpgradeNet.Changed += Refresh;
        Refresh();
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnChanged;
        BusinessUpgradeNet.Changed -= Refresh;
    }

    void OnChanged(string changed, int level)
    {
        if (changed != id || (DepotRowWorld.Active && DepotMachineContext.LotOf(this) != DepotRowWorld.LocalLot)) return;
        Refresh();
        if (Application.isPlaying) Juice.Punch(transform, 0.2f, 0.3f);
    }

    public void Refresh()
    {
        if (text == null || texts == null || texts.Length == 0) return;
        int lvl = Mathf.Clamp(DepotMachineContext.Level(this, id), 0, texts.Length - 1);
        text.text = texts[lvl];
    }
}

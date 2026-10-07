using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Esc menu's Town Rules page. Only actual host-controlled game rules appear here: the old reference card listed
/// speculative switches that no gameplay system read. Rabies Tag is saved in the host's settings and replicated by NightNet.
/// </summary>
public static class TownRulesCard
{
    public const int RowCount = 1;

    public static TownRulesCardView Build(RectTransform page, bool isHost, bool preview)
    {
        var view = page.gameObject.AddComponent<TownRulesCardView>();
        view.Build(page, isHost, preview);
        return view;
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PT = UiText.Pause;

/// <summary>The live pill/button can refresh while a guest leaves the Rules page open and the host changes the rule.</summary>
public sealed class TownRulesCardView : MonoBehaviour
{
    Image pillBody;
    TextMeshProUGUI pillWord, description;
    UiButton toggle;
    bool preview;

    public Selectable Selectable => toggle;

    public void Build(RectTransform page, bool isHost, bool isPreview)
    {
        preview = isPreview;
        float w = Mathf.Max(400f, page.rect.width), h = Mathf.Max(400f, page.rect.height);
        var head = PauseUi.Text(page, "Rules Header", "LIVE TOWN RULE", 30f, TextAlignmentOptions.MidlineLeft, true, UiTheme.Ink);
        PauseUi.TopLeft(head.rectTransform, 12f, 10f, w - 24f, 42f);
        var sub = PauseUi.Text(page, "Rules Sub", "The Boss sets this for everyone in the Town.", 21f,
                               TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Normal);
        PauseUi.TopLeft(sub.rectTransform, 12f, 55f, w - 24f, 32f);

        var row = FeelUi.Rect("Rabies Tag Rule", page, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero,
                              new Vector2(w - 20f, 174f));
        PauseUi.TopLeft(row, 10f, 106f, w - 20f, 174f);
        var bg = FeelUi.Img(row, "Paper", FeelUi.Rounded, UiTheme.PaperDark, 0.55f);
        bg.raycastTarget = false;
        var pill = FeelUi.Rect("Pill", row, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(98f, 42f));
        PauseUi.TopLeft(pill, 18f, 22f, 98f, 42f);
        pillBody = FeelUi.Img(pill, "Body", FeelUi.Rounded, UiTheme.Go, 0.5f);
        pillBody.raycastTarget = false;
        pillWord = UiKit.OutlineLabel(pill, "Word", UiText.On, 21f, Color.white, TextAlignmentOptions.Center, true);
        pillWord.raycastTarget = false;
        var title = PauseUi.Text(row, "Name", "RABIES TAG", 27f, TextAlignmentOptions.MidlineLeft, true, UiTheme.Ink);
        PauseUi.TopLeft(title.rectTransform, 130f, 20f, w - 160f, 48f);
        description = PauseUi.Text(row, "Effect", "", 22f, TextAlignmentOptions.TopLeft, false,
                                   UiTheme.InkSoft, FontStyles.Normal);
        description.textWrappingMode = TextWrappingModes.Normal;
        PauseUi.TopLeft(description.rectTransform, 22f, 82f, w - 64f, 78f);

        if (isHost)
        {
            toggle = UiKit.Button(page, "TURN OFF", () =>
            {
                if (preview) return;
                if (NightShift.TrySetRabiesTag(!NightShift.RabiesTag)) Apply(NightShift.RabiesTag);
                else GameHUD.Toast("Only the Boss can change Town rules.", ToastStyle.Bad, 2.5f);
            }, UiButtonStyle.Sticker);
            toggle.Size(240f, 58f);
            PauseUi.TopLeft(toggle.Rect, 18f, 289f, 240f, 58f);
            if (preview) toggle.SetDisabled("PREVIEW");
        }

        var foot = PauseUi.Text(page, "Footer", isHost ? PT.RulesFooterHost : PT.RulesFooterGuest, 19f,
                                TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Italic);
        PauseUi.TopLeft(foot.rectTransform, 12f, h - 47f, w - 24f, 42f);
        foot.textWrappingMode = TextWrappingModes.Normal;
        Apply(preview || NightShift.RabiesTag);
    }

    public void Apply(bool on)
    {
        if (pillBody != null) pillBody.color = on ? UiTheme.Go : UiTheme.Disabled;
        if (pillWord != null) pillWord.text = on ? UiText.On : UiText.Off;
        if (description != null) description.text = on
            ? $"A rabid player's knockdown can infect the victim (50%). A Rabies Shot from {Scrapazon.Name} still cures it."
            : "Raccoons can still infect players, but players cannot pass rabies to one another.";
        if (toggle != null) toggle.SetLabel(on ? "TURN OFF" : "TURN ON");
    }
}

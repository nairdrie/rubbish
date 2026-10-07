using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Continue card on the main menu (Docs/UIPass.md §4.3): a Head Office business card with a tint stripe in the company
/// colour, "Maple Hollow · Day 14", the company name, cash and a rank badge, "last played 2 h ago" and a mode chip (SOLO).
/// It's a <see cref="UiButton"/> (so it tilts, springs, squeaks and takes keyboard focus like every sticker) with the card
/// drawn on its visual. Before M1 it reads the legacy save through <see cref="GameSession"/>; B14 swaps the data source for
/// the active Town × company (<see cref="Data"/>) and adds the ⋯ menu and the ON THE ROAD state.
/// </summary>
public sealed class ContinueCard : MonoBehaviour
{
    /// <summary>What the card shows (B14 fills this from SaveSlots; today it comes from the legacy save).</summary>
    public struct Data
    {
        public bool hasSave;
        public string town, company;
        public Color tint;
        public int day, rank;
        public string rankTitle;
        public float cash;
        public DateTime? lastPlayedUtc;
        public bool withFriends;
    }

    public UiButton Button { get; private set; }
    RectTransform face;
    TextMeshProUGUI kicker, line1, line2, line3, chip, badgeNum;
    Image stripe, chipBody, badge;
    bool compact;
    float lineW;

    /// <summary>The legacy save as card data (GameSession is loaded at boot, so this works in the menu).</summary>
    public static Data FromLegacySave()
    {
        var d = new Data
        {
            town = UiText.MenuLegacyTown,
            company = GameSession.BusinessName,
            tint = UiTheme.TruckOlive,
            day = GameSession.Day,
            cash = GameSession.Cash,
            rank = 1,
            withFriends = false,
        };
        try
        {
            d.rank = Progression.Rank;
            d.rankTitle = Progression.RankTitle(d.rank);
        }
        catch (Exception) { d.rankTitle = null; }
        try
        {
            string path = LegacySave.GameSessionPath;
            d.hasSave = System.IO.File.Exists(path);
            if (d.hasSave) d.lastPlayedUtc = System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception) { d.hasSave = false; }
        if (!d.hasSave && (d.day > 1 || d.cash > 0f)) d.hasSave = true; // loaded from a backup generation
        return d;
    }

    /// <summary>Build the card (544×150 at 100%, 112 tall in compact layouts).</summary>
    public static ContinueCard Build(Transform parent, Vector2 size, Action click)
    {
        var b = UiKit.Button(parent, "", click, UiButtonStyle.Paper);
        b.Size(size.x, size.y);
        b.name = "ContinueCard";
        var card = b.gameObject.AddComponent<ContinueCard>();
        card.Button = b;
        card.MakeFace(size);
        return card;
    }

    void MakeFace(Vector2 size)
    {
        compact = size.y < 130f;
        var v = Button.Visual;
        // a white card face over the manila sticker body, so it reads as paper; the sticker's focus stripe stays on top
        face = FeelUi.Stretch("Face", v);
        face.SetSiblingIndex(Mathf.Min(v.childCount - 1, 2));
        var faceImg = FeelUi.Img(face, "Paper", FeelUi.Rounded, UiTheme.Paper, 0.45f);
        faceImg.rectTransform.offsetMin = new Vector2(3f, 3f);
        faceImg.rectTransform.offsetMax = new Vector2(-3f, -3f);
        FeelUi.Img(face, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.35f), 0.45f);
        stripe = FeelUi.Img(face, "Tint", FeelUi.Rounded, UiTheme.TruckOlive, 0.2f);
        UiKit.Place(stripe.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(14f, size.y - 30f));
        stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
        stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
        stripe.rectTransform.offsetMin = new Vector2(22f, 15f);
        stripe.rectTransform.offsetMax = new Vector2(36f, -15f);

        float x0 = 56f;
        lineW = size.x - x0 - 118f; // the right 118 px belong to the rank badge
        kicker = UiKit.InkLabel(face, "Kicker", UiText.MenuContinue, 20f, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, true);
        kicker.characterSpacing = 6f;
        line1 = UiKit.InkLabel(face, "Town", "", compact ? 26f : 30f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft, true);
        line2 = UiKit.InkLabel(face, "Company", "", 22f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft, false);
        line3 = UiKit.InkLabel(face, "LastPlayed", "", UiTheme.Small, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        foreach (var t in new[] { line1, line2 })
        {
            t.enableAutoSizing = true;
            t.fontSizeMax = t.fontSize;
            t.fontSizeMin = UiTheme.MinText;
        }
        float kTop = compact ? 10f : 14f;
        Row(kicker, x0, kTop, 170f, 26f);
        if (compact)
        {
            Row(line1, x0, 38f, lineW, 34f);
            Row(line2, x0, 74f, lineW, 28f);
            line3.gameObject.SetActive(false);
        }
        else
        {
            Row(line1, x0, 42f, lineW, 38f);
            Row(line2, x0, 82f, lineW, 28f);
            Row(line3, x0, 112f, lineW, 26f);
        }

        // mode chip (after the kicker) and rank badge (right, centred)
        var chipRt = FeelUi.Rect("ModeChip", face, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 150f, -kTop + 2f), new Vector2(96f, 30f));
        chipBody = FeelUi.Img(chipRt, "Body", FeelUi.Rounded, UiTheme.PlumSolid, 0.25f);
        chip = UiKit.OutlineLabel(chipRt, "Text", UiText.MenuModeSolo, UiTheme.MinText, Color.white, TextAlignmentOptions.Center, true);
        chip.characterSpacing = 3f;
        chip.enableAutoSizing = true;
        chip.fontSizeMax = 20f;
        chip.fontSizeMin = UiTheme.MinText;
        UiKit.Inset(chip.rectTransform, 8f, 2f, 8f, 2f);

        float bd = compact ? 60f : 74f;
        var badgeRt = FeelUi.Rect("RankBadge", face, new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-62f, compact ? -4f : -10f), new Vector2(bd, bd));
        var bs = FeelUi.Img(badgeRt, "Shadow", FeelUi.Circle, UiTheme.Shadow);
        bs.rectTransform.offsetMin = bs.rectTransform.offsetMax = new Vector2(2f, -3f);
        badge = FeelUi.Img(badgeRt, "Disc", FeelUi.Circle, UiTheme.Gold);
        FeelUi.Img(badgeRt, "Rim", UiKit.Sprites.Ring, UiTheme.WithAlpha(UiTheme.Ink, 0.7f));
        badgeNum = UiKit.InkLabel(badgeRt, "Rank", "1", compact ? 28f : 34f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        var cap = UiKit.InkLabel(badgeRt, "Cap", "RANK", UiTheme.MinText, UiTheme.InkSoft, TextAlignmentOptions.Center, true);
        cap.characterSpacing = 2f;
        UiKit.Place(cap.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(84f, 24f));
    }

    static void Row(TMP_Text t, float x, float top, float w, float h) =>
        UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -top), new Vector2(w, h));

    /// <summary>Show <paramref name="d"/>.</summary>
    public void Show(Data d)
    {
        if (stripe != null) stripe.color = d.tint;
        string company = $"<color=#{FeelUi.Hex(d.tint)}><b>{d.company}</b></color>";
        if (!d.hasSave)
        {
            kicker.text = UiText.MenuCardNewTitle;
            line1.text = $"{d.town} · Day 1";
            line2.text = $"{company}  {Economy.Money(d.cash)}";
            line3.text = FeelUi.Fit(line3, UiText.MenuCardNewLine, lineW);
        }
        else
        {
            kicker.text = UiText.MenuContinue;
            line1.text = $"{d.town} · Day {Mathf.Max(1, d.day)}";
            line2.text = $"{company}  {Economy.Money(d.cash)}";
            string last = LastPlayed(d.lastPlayedUtc);
            line3.text = FeelUi.Fit(line3, string.IsNullOrEmpty(d.rankTitle) ? last : $"{last} · {d.rankTitle}", lineW);
        }
        badgeNum.text = Mathf.Max(1, d.rank).ToString();
        chip.text = d.withFriends ? UiText.MenuModeFriends : UiText.MenuModeSolo;
        var chipRt = (RectTransform)chip.transform.parent;
        chipRt.sizeDelta = new Vector2(d.withFriends ? 170f : 84f, 30f);
        // the kicker's width decides where the chip sits
        kicker.ForceMeshUpdate();
        float kw = kicker.GetPreferredValues(kicker.text).x;
        chipRt.anchoredPosition = new Vector2(kicker.rectTransform.anchoredPosition.x + kw + 14f, chipRt.anchoredPosition.y);
    }

    /// <summary>"last played 2 h ago".</summary>
    public static string LastPlayed(DateTime? utc)
    {
        if (!utc.HasValue) return UiText.MenuLastPlayedNever;
        var ago = DateTime.UtcNow - utc.Value;
        if (ago.TotalMinutes < 2) return UiText.MenuLastPlayedNow;
        string span = ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min"
                    : ago.TotalHours < 48 ? $"{(int)ago.TotalHours} h"
                    : $"{(int)ago.TotalDays} days";
        return string.Format(UiText.MenuLastPlayedFmt, span);
    }

    /// <summary>Greyed while the menu is busy (the sticker's own disabled look is under the face, so grey the face too).</summary>
    public void SetBusy(bool on)
    {
        if (face == null) return;
        var g = face.GetComponent<CanvasGroup>();
        if (g == null) g = face.gameObject.AddComponent<CanvasGroup>(); // (no ??: Unity's fake-null components)
        g.alpha = on ? 0.55f : 1f;
        g.blocksRaycasts = false;
        g.interactable = false;
    }
}

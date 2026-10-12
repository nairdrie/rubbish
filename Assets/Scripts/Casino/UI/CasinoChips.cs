using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Casino chips as uGUI: one chip, a little stack for an amount, and the denominations to draw one from.</summary>
public static class CasinoChips
{
    static readonly long[] Denominations = { 50000, 10000, 5000, 2500, 1000, 500, 100 };

    /// <summary>The largest chip that fits into <paramref name="cents"/> (at least a $1 chip).</summary>
    public static long DenominationFor(long cents)
    {
        for (int i = 0; i < Denominations.Length; i++) if (cents >= Denominations[i]) return Denominations[i];
        return 100;
    }

    /// <summary>"25", "1,000": whole dollars as printed on a chip.</summary>
    public static string Face(long cents) => (cents / 100).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One chip coloured for <paramref name="colorCents"/> with <paramref name="label"/> on it (null = its value).</summary>
    public static RectTransform Chip(Transform parent, long colorCents, float size, string label = null)
    {
        var rt = FeelUi.Rect("Chip", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        var shadow = FeelUi.Img(rt, "Shadow", UiKit.Sprites.Disc, new Color(0f, 0f, 0f, 0.45f));
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(size * 0.04f, -size * 0.07f);
        var rim = FeelUi.Img(rt, "Rim", UiKit.Sprites.Disc, Color.Lerp(CasinoArt.ChipColor(colorCents), Color.black, 0.35f));
        rim.rectTransform.offsetMin = new Vector2(-size * 0.11f, -size * 0.11f);
        rim.rectTransform.offsetMax = new Vector2(size * 0.11f, size * 0.11f);
        var body = FeelUi.Img(rt, "Body", UiKit.Sprites.Disc, CasinoArt.ChipColor(colorCents));
        body.rectTransform.offsetMin = new Vector2(-size * 0.09f, -size * 0.09f);
        body.rectTransform.offsetMax = new Vector2(size * 0.09f, size * 0.09f);
        FeelUi.Img(rt, "Inserts", CasinoArt.ChipInserts, colorCents >= 500 ? CasinoArt.Cream : CasinoArt.Hex(0x2456C4));
        string text = label ?? Face(colorCents);
        var t = UiKit.InkLabel(rt, "Value", text, Mathf.Max(UiTheme.MinText, size * 0.3f), CasinoArt.ChipInk(colorCents), TextAlignmentOptions.Center, false);
        t.enableAutoSizing = true;
        t.fontSizeMax = Mathf.Max(UiTheme.MinText, size * 0.34f);
        t.fontSizeMin = 12f;
        UiKit.Inset(t.rectTransform, size * 0.2f, size * 0.25f, size * 0.2f, size * 0.25f);
        return rt;
    }

    /// <summary>
    /// A stack standing for <paramref name="cents"/>: up to three chips of its biggest denomination, the top one printed
    /// with the whole amount. Returns the root (its size is one chip; the stack grows upwards).
    /// </summary>
    public static RectTransform Stack(Transform parent, long cents, float size)
    {
        var root = FeelUi.Rect("Stack", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        long denom = DenominationFor(cents);
        int n = (int)Mathf.Clamp(cents / Math.Max(1, denom), 1, 3);
        for (int i = 0; i < n; i++)
        {
            var chip = Chip(root, denom, size, i == n - 1 ? Face(cents) : "");
            chip.anchoredPosition = new Vector2(0f, i * size * 0.12f);
        }
        return root;
    }
}

/// <summary>
/// One playing card as uGUI: a cream face with the rank and suit in two corners and proper pips (or a framed court
/// letter), or a lattice back. Cards are bytes 0..51 as in <see cref="BlackjackRules"/>; <see cref="BlackjackTable.Hidden"/>
/// shows the back.
/// </summary>
public sealed class CasinoCardView
{
    public readonly RectTransform Rect;
    public byte Card { get; private set; } = BlackjackTable.Hidden;
    public bool FaceUp => Card != BlackjackTable.Hidden;

    readonly RectTransform back, face;
    readonly Vector2 size;

    static readonly string[] RankText = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    // Pip places as fractions of the card: x of the half width, y of the half height.
    const float PX = 0.46f, PT = 0.56f, PM = 0.28f, P3 = 0.19f;
    static readonly Vector2[][] Pips =
    {
        new[] { new Vector2(0, 0) },                                                                                   // ace (drawn big)
        new[] { new Vector2(0, PT), new Vector2(0, -PT) },
        new[] { new Vector2(0, PT), new Vector2(0, 0), new Vector2(0, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(0, 0), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(-PX, 0), new Vector2(PX, 0), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(0, PM), new Vector2(-PX, 0), new Vector2(PX, 0), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(0, PM), new Vector2(-PX, 0), new Vector2(PX, 0), new Vector2(0, -PM), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(-PX, P3), new Vector2(PX, P3), new Vector2(0, 0),
                new Vector2(-PX, -P3), new Vector2(PX, -P3), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
        new[] { new Vector2(-PX, PT), new Vector2(PX, PT), new Vector2(0, 0.38f), new Vector2(-PX, P3), new Vector2(PX, P3),
                new Vector2(-PX, -P3), new Vector2(PX, -P3), new Vector2(0, -0.38f), new Vector2(-PX, -PT), new Vector2(PX, -PT) },
    };

    public CasinoCardView(Transform parent, Vector2 size)
    {
        this.size = size;
        Rect = FeelUi.Rect("Card", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var shadow = FeelUi.Img(Rect, "Shadow", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.4f), 0.3f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(3f, -4f);

        back = FeelUi.Stretch("Back", Rect);
        FeelUi.Img(back, "Edge", FeelUi.Rounded, CasinoArt.Cream, 0.3f);
        var body = FeelUi.Img(back, "Body", FeelUi.Rounded, CasinoArt.CardBack, 0.24f);
        body.rectTransform.offsetMin = new Vector2(6f, 6f);
        body.rectTransform.offsetMax = new Vector2(-6f, -6f);
        var lattice = FeelUi.Img(back, "Lattice", CasinoArt.Lattice, new Color(1f, 1f, 1f, 0.22f));
        lattice.type = Image.Type.Tiled;
        lattice.pixelsPerUnitMultiplier = 2f;
        lattice.rectTransform.offsetMin = new Vector2(10f, 10f);
        lattice.rectTransform.offsetMax = new Vector2(-10f, -10f);

        face = FeelUi.Stretch("Face", Rect);
        face.gameObject.SetActive(false);
    }

    /// <summary>Show a card face (0..51) or the back (<see cref="BlackjackTable.Hidden"/>). Rebuilds only when it changes.</summary>
    public void Show(byte card)
    {
        if (card == Card && (card == BlackjackTable.Hidden || face.childCount > 0)) return;
        Card = card;
        bool up = card != BlackjackTable.Hidden;
        back.gameObject.SetActive(!up);
        face.gameObject.SetActive(up);
        if (!up) return;
        for (int i = face.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(face.GetChild(i).gameObject);
        BuildFace(card);
    }

    void BuildFace(byte card)
    {
        int rank = BlackjackRules.Rank(card), suit = BlackjackRules.Suit(card);
        Color ink = CasinoArt.SuitColor(suit);
        var pip = CasinoArt.Suit(suit);
        float w = size.x, h = size.y;

        FeelUi.Img(face, "Body", FeelUi.Rounded, CasinoArt.Cream, 0.3f);
        FeelUi.Img(face, "Edge", UiKit.Sprites.RoundedOutline, new Color(0f, 0f, 0f, 0.25f), 0.3f);

        Corner(rank, ink, pip, w, h, false);
        Corner(rank, ink, pip, w, h, true);

        if (rank >= 10)
        {
            // court cards: a framed letter, the suit above and below
            var frame = FeelUi.Img(face, "Frame", UiKit.Sprites.RoundedOutline, new Color(ink.r, ink.g, ink.b, 0.55f), 0.2f);
            frame.rectTransform.offsetMin = new Vector2(w * 0.24f, h * 0.14f);
            frame.rectTransform.offsetMax = new Vector2(-w * 0.24f, -h * 0.14f);
            var letter = UiKit.InkLabel(face, "Court", RankText[rank], h * 0.36f, ink, TextAlignmentOptions.Center, true);
            UiKit.Place(letter.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(w * 0.5f, h * 0.44f));
            Pip(pip, ink, new Vector2(0f, h * 0.27f), w * 0.2f, false);
            Pip(pip, ink, new Vector2(0f, -h * 0.27f), w * 0.2f, true);
            return;
        }
        if (rank == 0) { Pip(pip, ink, Vector2.zero, w * 0.46f, false); return; }
        var places = Pips[rank];
        float sizePip = w * (rank >= 8 ? 0.2f : 0.23f);
        for (int i = 0; i < places.Length; i++)
            Pip(pip, ink, new Vector2(places[i].x * w * 0.5f, places[i].y * h * 0.5f), sizePip, places[i].y < -0.01f);
    }

    void Pip(Sprite sprite, Color ink, Vector2 pos, float s, bool flipped)
    {
        var img = FeelUi.Img(face, "Pip", sprite, ink);
        UiKit.Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, new Vector2(s, s));
        if (flipped) img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
    }

    void Corner(int rank, Color ink, Sprite pip, float w, float h, bool bottomRight)
    {
        var c = FeelUi.Rect("Corner", face, bottomRight ? new Vector2(1f, 0f) : new Vector2(0f, 1f), new Vector2(0.5f, 0.5f),
                            bottomRight ? new Vector2(-w * 0.14f, h * 0.17f) : new Vector2(w * 0.14f, -h * 0.17f), new Vector2(w * 0.26f, h * 0.3f));
        if (bottomRight) c.localRotation = Quaternion.Euler(0f, 0f, 180f);
        var t = UiKit.InkLabel(c, "Rank", RankText[rank], h * 0.17f, ink, TextAlignmentOptions.Center, true);
        t.enableAutoSizing = true;
        t.fontSizeMax = Mathf.Max(UiTheme.MinText, h * 0.17f);
        t.fontSizeMin = 12f;
        t.characterSpacing = rank == 9 ? -8f : 0f;
        UiKit.Place(t.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(w * 0.3f, h * 0.18f));
        var s = FeelUi.Img(c, "Suit", pip, ink);
        UiKit.Place(s.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, h * 0.01f), new Vector2(w * 0.17f, w * 0.17f));
    }

    public void Destroy()
    {
        if (Rect != null) UnityEngine.Object.Destroy(Rect.gameObject);
    }
}

/// <summary>
/// Small props the screens put on the real tables when the world offers a spot for them (cards on the felt, chips on
/// the bet circle, a glass on the bar). Local and cosmetic; every call tolerates a missing transform, and
/// <see cref="Clear"/> removes whatever was made.
/// </summary>
public sealed class CasinoProps
{
    readonly List<GameObject> made = new List<GameObject>();

    /// <summary>A coloured solid parented to <paramref name="spot"/> (null when there is no spot or the kit fails).</summary>
    public Transform Add(Transform spot, string name, PuppetKit.Shape shape, Color color, Vector3 localPos, Vector3 localScale, float smooth = 0.3f, Vector3 euler = default)
    {
        if (spot == null) return null;
        try
        {
            var renderer = PuppetKit.Part(spot, name, out var filter);
            var part = new PartSpec { on = true, shape = shape, color = color, pos = localPos, euler = euler, scale = localScale, smooth = smooth, shadow = false };
            PuppetRig.ApplyPart(renderer, filter, in part);
            renderer.gameObject.hideFlags = HideFlags.DontSave;
            made.Add(renderer.gameObject);
            return renderer.transform;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Casino] table prop skipped: " + e.Message);
            return null;
        }
    }

    public int Count => made.Count;

    public void Clear()
    {
        for (int i = 0; i < made.Count; i++)
            if (made[i] != null) UnityEngine.Object.Destroy(made[i]);
        made.Clear();
    }
}

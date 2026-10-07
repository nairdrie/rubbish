using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Invite cards in the main menu (Docs/UIPass.md §4.8): "Dot invited you · Maple Hollow [JOIN] [x]" slides in at the bottom
/// right with <c>Sfx notify</c> and stays 30 s (a thin fuse shows the time left); at most 3 stack up. UP-5's Steam feed calls
/// <see cref="Push"/> when a <c>LobbyInvite_t</c> arrives; during gameplay (no menu) Push returns false and the caller makes a
/// toast instead. The JOIN action is the caller's (UP-5: <c>SteamJoinRouter.RequestJoin(lobbyId, …)</c>).
/// </summary>
public sealed class InviteCards : MonoBehaviour
{
    /// <summary>One invite as the feed sees it (plain data; no Steam types here).</summary>
    public struct Invite
    {
        public string from;
        public string town;
        public Color tint;
        public Action join;
    }

    public const float Lifetime = 30f;
    public const int MaxCards = 3;
    const float CardW = 470f, CardH = 112f, Gap = 14f;

    static InviteCards active;
    RectTransform root;
    readonly List<Card> cards = new List<Card>();
    bool preview;

    sealed class Card
    {
        public RectTransform rt;
        public Image fuse;
        public float age;
        public float y, yVel, x;
        public Invite data;
        public bool closing;
    }

    /// <summary>The menu's invite stack is up (the menu is open).</summary>
    public static bool Available => active != null && active.isActiveAndEnabled;
    /// <summary>Cards on screen.</summary>
    public static int Count => active != null ? active.cards.Count : 0;

    /// <summary>Show an invite card. False when there's no menu (show a toast instead).</summary>
    public static bool Push(Invite invite)
    {
        if (!Available) return false;
        active.Add(invite);
        return true;
    }

    /// <summary>Build the (empty) stack anchored bottom-right of <paramref name="parent"/>.</summary>
    public static InviteCards Build(RectTransform parent, bool preview = false)
    {
        var rt = FeelUi.Rect("InviteCards", parent, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-28f, 28f), new Vector2(CardW, CardH));
        var s = rt.gameObject.AddComponent<InviteCards>();
        s.root = rt;
        s.preview = preview;
        return s;
    }

    void OnEnable() { if (!preview) active = this; }
    void OnDisable() { if (active == this) active = null; }

    /// <summary>Preview / tests: add a card directly.</summary>
    public void Add(Invite inv)
    {
        while (cards.Count >= MaxCards) Remove(cards[0]);
        var c = new Card { data = inv };
        c.rt = FeelUi.Rect("Invite " + inv.from, root, new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(CardW, CardH));
        c.rt.localRotation = Quaternion.Euler(0f, 0f, UiKit.TiltFor(inv.from, 1.2f));
        FeelUi.Panel(c.rt, UiTheme.WithAlpha(UiTheme.PlumSolid, 0.96f), 0.5f);
        var stripe = FeelUi.Img(c.rt, "Tint", FeelUi.Rounded, inv.tint.a > 0f ? inv.tint : UiTheme.Tangerine, 0.2f);
        stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
        stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
        stripe.rectTransform.offsetMin = new Vector2(12f, 14f);
        stripe.rectTransform.offsetMax = new Vector2(24f, -14f);

        var av = FeelUi.Rect("Avatar", c.rt, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(66f, 0f), new Vector2(60f, 60f));
        FeelUi.Img(av, "Disc", FeelUi.Circle, inv.tint.a > 0f ? inv.tint : UiTheme.Tangerine);
        var ini = UiKit.OutlineLabel(av, "Initial", Initial(inv.from), 30f, Color.white, TextAlignmentOptions.Center);
        ini.rectTransform.offsetMin = Vector2.zero;

        using (UiKit.OnPlum())
        {
            // the text column ends where the JOIN button starts (long names shrink, then end in "…"; never under the button)
            const float textW = CardW - 108f - 74f - 120f - 10f;
            var who = UiKit.Label(c.rt, "Who", "", 24f, TextAlignmentOptions.MidlineLeft);
            UiKit.Place(who.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(108f, -18f), new Vector2(textW, 32f));
            who.text = FitInviteLine(who, string.IsNullOrEmpty(inv.from) ? "A friend" : inv.from, textW);
            var where = UiKit.Label(c.rt, "Town", "", UiTheme.Small, TextAlignmentOptions.MidlineLeft, false, FontStyles.Normal,
                                    new Color(0.82f, 0.8f, 0.88f));
            UiKit.Place(where.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(108f, -54f), new Vector2(textW, 28f));
            where.text = FeelUi.Fit(where, inv.town ?? "", textW);
        }

        var join = UiKit.Button(c.rt, UiText.MenuInviteJoin, () => { Remove(c); inv.join?.Invoke(); }, UiButtonStyle.Go);
        join.Size(120f, 58f);
        UiKit.Place(join.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-74f, 4f), new Vector2(120f, 58f));
        var x = UiKit.Button(c.rt, "", () => Remove(c), UiButtonStyle.Quiet);
        x.Size(46f, 46f);
        var xi = FeelUi.Img(x.Visual, "Cross", UiKit.Sprites.Cross, UiTheme.Ink); // shapes are sprites, never font glyphs (D10)
        UiKit.Inset(xi.rectTransform, 10f, 10f, 10f, 10f);
        UiKit.Place(x.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-16f, 4f), new Vector2(46f, 46f));

        var fuseBack = FeelUi.Img(c.rt, "FuseBack", FeelUi.Rounded, new Color(1f, 1f, 1f, 0.12f), 0.05f);
        fuseBack.rectTransform.anchorMin = new Vector2(0f, 0f);
        fuseBack.rectTransform.anchorMax = new Vector2(1f, 0f);
        fuseBack.rectTransform.offsetMin = new Vector2(108f, 10f);
        fuseBack.rectTransform.offsetMax = new Vector2(-20f, 15f);
        c.fuse = FeelUi.Img(fuseBack.rectTransform, "Fuse", FeelUi.Rounded, UiTheme.Gold, 0.05f);

        c.x = preview ? 0f : CardW + 60f;
        c.y = 0f;
        cards.Add(c);
        Layout(true);
        if (!preview) UiTheme.Play(UiTheme.SfxInvite, 0.6f, 1f);
    }

    static string Initial(string name) => string.IsNullOrEmpty(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();

    /// <summary>"Dot invited you" in <paramref name="w"/>: 24 px, else smaller (down to the legibility floor), else the name is
    /// shortened with "…" so "invited you" always reads.</summary>
    static string FitInviteLine(TMP_Text t, string from, float w)
    {
        string full = string.Format(UiText.MenuInviteFmt, from);
        foreach (float size in new[] { 24f, 21f, UiTheme.MinText })
        {
            t.fontSize = size;
            if (t.GetPreferredValues(full, 100000f, 0f).x <= w) return full;
        }
        int at = UiText.MenuInviteFmt.IndexOf("{0}", StringComparison.Ordinal);
        string before = at > 0 ? UiText.MenuInviteFmt.Substring(0, at) : "";
        string after = at >= 0 ? UiText.MenuInviteFmt.Substring(at + 3) : "";
        float rest = t.GetPreferredValues(before + after, 100000f, 0f).x;
        return before + FeelUi.Fit(t, from, Mathf.Max(24f, w - rest)) + after;
    }

    void Remove(Card c)
    {
        if (!cards.Remove(c)) return;
        if (c.rt != null)
        {
            if (Application.isPlaying) Destroy(c.rt.gameObject);
            else DestroyImmediate(c.rt.gameObject);
        }
        Layout(false);
    }

    void Layout(bool snapNew)
    {
        // newest at the bottom
        for (int i = 0; i < cards.Count; i++)
        {
            float target = (cards.Count - 1 - i) * (CardH + Gap);
            if (preview || (snapNew && i == cards.Count - 1)) cards[i].y = target;
            cards[i].rt.anchoredPosition = new Vector2(cards[i].x, cards[i].y);
        }
    }

    void Update()
    {
        if (preview) return;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            var c = cards[i];
            c.age += dt;
            if (c.age >= Lifetime) { Remove(c); continue; }
            float target = (cards.Count - 1 - i) * (CardH + Gap);
            c.y = FeelUi.Spring(c.y, ref c.yVel, target, dt, 160f, 14f);
            c.x = Mathf.Lerp(c.x, 0f, 1f - Mathf.Exp(-12f * dt));
            c.rt.anchoredPosition = new Vector2(c.x, c.y);
            var f = c.fuse.rectTransform;
            f.anchorMax = new Vector2(1f - c.age / Lifetime, 1f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => active = null;
}

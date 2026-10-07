using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Progression HUD (Docs/Progression.md §4.4, §9.5 A.6): its own ScreenSpaceOverlay canvas one sorting step under
/// FeelHUD, built in code with the public <see cref="FeelUi"/> helpers.
///   - the rank chip (top row, right of the clock; HudLayout's rank slot): "RANK 5 · Senior Sack Specialist" + a springy
///     Rep bar with the Rep numbers under it; badge-only (the number in a circular Rep ring) when the top bar is Compact/Tight;
///   - "+25 REP" floaters (events of 10 Rep or more) rising up out of the chip (never into the toast lane, UIPass H8);
///   - the stamp card: PROMOTED! (rubber stamp slams in, confetti, legendary sfx, shake), Key to the City, permits,
///     Work Order done;
///   - Head Office memos sliding in under the route panel (HudLayout.MemoSlot; they wait while there's no room, H9).
/// Mirrors FeelHUD's visibility (no local-player lookups) and holds cards while a UI (the laptop) is open.
/// </summary>
public sealed class ProgressionHud
{
    public const int SortingOrder = FeelHud.SortingOrder - 1;
    public const float ChipWidth = 432f, ChipHeight = 104f, BadgeWidth = 82f;
    static readonly Vector2 DefaultChipPos = new Vector2(708f, -20f);
    Vector2 ChipPos = DefaultChipPos;
    static readonly Color RepPurple = new Color(0.78f, 0.55f, 1f);
    static readonly Color Paper = new Color(1f, 0.96f, 0.86f, 0.98f);
    static readonly Color Ink = new Color(0.12f, 0.1f, 0.18f);
    static readonly Color StampRed = new Color(0.86f, 0.12f, 0.16f);

    GameObject go;
    Canvas canvas;
    CanvasScaler scaler;
    RectTransform root;
    bool built, preview;
    Camera previewCam;

    // ---- chip
    RectTransform chip, badge, barFillRt, chipBody, chipFull, ring;
    CanvasGroup chipGroup;
    Image barFill, badgeDisk, barGlow, ringFill;
    TextMeshProUGUI rankNum, titleText, capText, repText;
    bool badgeOnly;
    float chipAlpha, barShown = -1f, barVel, chipScale = 1f, chipScaleVel, badgeScale = 1f, badgeScaleVel, barFlash, wait;
    int shownRank = -1;
    string shownTitle, shownCap, shownRep;

    // ---- floaters
    sealed class Floater { public RectTransform rt; public TextMeshProUGUI text; public float age; public bool live; public float x; }
    readonly Floater[] floaters = new Floater[8];
    int floaterCursor;

    // ---- stamp card
    public enum CardKind { Promotion, KeyToCity, Permit, WorkOrder }
    sealed class Card
    {
        public CardKind kind;
        public string stamp, header, title, sub, foot;
        public Color stampColor;
        public float scale = 1f, hold = 4.5f;
        public int newRank;
    }
    readonly Queue<Card> cards = new Queue<Card>();
    Card current;
    float cardAge;
    bool stampLanded;
    RectTransform cardRoot, card, stamp, rays;
    CanvasGroup cardGroup;
    Image raysImg, stampFrame;
    TextMeshProUGUI headerText, stampText, cardTitle, cardSub, cardFoot;

    struct Conf { public RectTransform rt; public Image img; public Vector2 pos, vel; public float rot, spin, life; public bool alive; }
    readonly List<Conf> confetti = new List<Conf>();
    static readonly Color[] ConfettiColors =
    {
        new Color(1f, 0.84f, 0.25f), new Color(0.35f, 0.9f, 0.45f), new Color(0.35f, 0.65f, 1f), new Color(1f, 0.4f, 0.55f),
    };

    // ---- Head Office memo
    readonly Queue<HeadOfficeLetter> letters = new Queue<HeadOfficeLetter>();
    HeadOfficeLetter letter;
    float letterAge;
    RectTransform memo;
    CanvasGroup memoGroup;
    TextMeshProUGUI memoSubject, memoBody, memoStamp;
    const float MemoHold = 7f;
    float MemoWidth = 420f;
    bool memoPlaced;
    Vector2 memoTopLeft;

    public bool Built => built;

    // ------------------------------------------------------------------ build

    public void Build(Transform parent)
    {
        if (built) return;
        built = true;
        go = new GameObject("ProgressionHUD", typeof(RectTransform)) { layer = 5 };
        if (parent != null) go.transform.SetParent(parent, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal |
                                           AdditionalCanvasShaderChannels.Tangent;
        scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        if (Application.isPlaying) UiScale.Register(scaler); // Settings → Interface → UI scale
        root = (RectTransform)go.transform;

        BuildChip();
        BuildCard();
        BuildMemo();
        canvas.enabled = false;
    }

    public void Destroy()
    {
        if (scaler != null) UiScale.Unregister(scaler);
        if (go != null) FeelRunner.SafeDestroy(go);
        go = null;
        built = false;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    void BuildChip()
    {
        chip = FeelUi.Rect("RankChip", root, new Vector2(0f, 1f), new Vector2(0f, 1f), ChipPos, new Vector2(ChipWidth, ChipHeight));
        chip.localRotation = Quaternion.Euler(0f, 0f, -1f);
        chipGroup = chip.gameObject.AddComponent<CanvasGroup>();
        chipGroup.interactable = false;
        chipGroup.blocksRaycasts = false;
        chipGroup.alpha = 0f;
        chipBody = FeelUi.Stretch("Body", chip);
        FeelUi.Panel(chipBody, FeelUi.PanelColor);

        // badge: ink ring + gold disk + big number, a "RANK" ribbon on top (18 px on a wider ribbon: H5)
        badge = FeelUi.Rect("Badge", chip, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(42f, -6f), new Vector2(66f, 66f));
        // badge-only form: a circular Rep ring around the badge (fills toward the next rank)
        ring = FeelUi.Rect("RepRing", badge, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(78f, 78f));
        FeelUi.Img(ring, "Track", UiKit.Sprites.Ring, new Color(0f, 0f, 0f, 0.55f));
        ringFill = FeelUi.Img(ring, "Fill", UiKit.Sprites.Ring, RepPurple);
        ringFill.type = Image.Type.Filled;
        ringFill.fillMethod = Image.FillMethod.Radial360;
        ringFill.fillOrigin = (int)Image.Origin360.Top;
        ringFill.fillClockwise = true;
        ring.gameObject.SetActive(false);
        FeelUi.Img(badge, "Ring", FeelUi.Circle, Ink);
        badgeDisk = FeelUi.Img(badge, "Disk", FeelUi.Circle, FeelUi.Gold);
        badgeDisk.rectTransform.offsetMin = new Vector2(4f, 4f);
        badgeDisk.rectTransform.offsetMax = new Vector2(-4f, -4f);
        rankNum = FeelUi.Text(badge, "Num", 38f, Color.white, TextAlignmentOptions.Center);
        rankNum.rectTransform.anchoredPosition = new Vector2(0f, -4f);
        var ribbon = FeelUi.Rect("Ribbon", badge, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -2f), new Vector2(74f, 24f));
        ribbon.localRotation = Quaternion.Euler(0f, 0f, 6f);
        FeelUi.Img(ribbon, "Bg", FeelUi.Rounded, StampRed, 0.4f);
        var rw = FeelUi.Text(ribbon, "Word", 18f, Color.white, TextAlignmentOptions.Center);
        rw.text = "RANK";

        chipFull = FeelUi.Stretch("Full", chip);
        titleText = FeelUi.Text(chipFull, "Title", 27f, Color.white, TextAlignmentOptions.Left);
        Place(titleText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(88f, -6f), new Vector2(ChipWidth - 100f, 34f));

        var barBg = FeelUi.Rect("Bar", chipFull, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(88f, -42f), new Vector2(ChipWidth - 100f, 14f));
        FeelUi.Img(barBg, "BarBg", FeelUi.Rounded, new Color(0f, 0f, 0f, 0.55f), 0.3f);
        var fillArea = FeelUi.Stretch("FillArea", barBg);
        fillArea.offsetMin = new Vector2(2f, 2f);
        fillArea.offsetMax = new Vector2(-2f, -2f);
        barFill = FeelUi.Img(fillArea, "Fill", FeelUi.Rounded, RepPurple, 0.2f);
        barFillRt = barFill.rectTransform;
        barFillRt.anchorMax = new Vector2(0f, 1f);
        barGlow = FeelUi.Img(fillArea, "Glow", FeelUi.Rounded, new Color(1f, 1f, 1f, 0f), 0.2f);
        // Rep numbers UNDER the bar at 18 px (they were 14 px inside it: illegible at 720p, H5)
        repText = FeelUi.Text(chipFull, "Rep", 18f, new Color(0.86f, 0.78f, 1f), TextAlignmentOptions.Left);
        Place(repText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(88f, -58f), new Vector2(ChipWidth - 100f, 22f));

        capText = FeelUi.Text(chipFull, "Caption", 18f, new Color(0.85f, 0.85f, 0.95f), TextAlignmentOptions.Left, FontStyles.Normal);
        Place(capText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(88f, -80f), new Vector2(ChipWidth - 100f, 22f));

        for (int i = 0; i < floaters.Length; i++)
        {
            var rt = FeelUi.Rect("RepFloater" + i, root, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 40f));
            var t = FeelUi.Text(rt, "Text", 30f, RepPurple, TextAlignmentOptions.Center);
            rt.gameObject.SetActive(false);
            floaters[i] = new Floater { rt = rt, text = t };
        }
    }

    void BuildCard()
    {
        cardRoot = FeelUi.Rect("StampCard", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 70f), new Vector2(760f, 360f));
        cardGroup = cardRoot.gameObject.AddComponent<CanvasGroup>();
        cardGroup.interactable = false;
        cardGroup.blocksRaycasts = false;

        rays = FeelUi.Rect("Rays", cardRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1100f, 1100f));
        raysImg = rays.gameObject.AddComponent<Image>();
        raysImg.sprite = FeelUi.Rays;
        raysImg.raycastTarget = false;

        card = FeelUi.Rect("Card", cardRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(700f, 290f));
        FeelUi.Panel(card, Paper, 1.1f, new Vector2(9f, -11f));
        // a ruled "letterhead" strip under the stamp
        var head = FeelUi.Rect("Letterhead", card, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(660f, 28f));
        FeelUi.Img(head, "Line", FeelUi.Rounded, new Color(0.12f, 0.1f, 0.18f, 0.12f), 0.3f);
        headerText = MakeCardText(head, "Header", 18f, new Color(0.3f, 0.27f, 0.38f), FontStyles.Bold);
        headerText.rectTransform.anchoredPosition = new Vector2(0f, -14f);

        cardTitle = MakeCardText(card, "Title", 54f, Ink, FontStyles.Bold);
        cardTitle.rectTransform.anchoredPosition = new Vector2(0f, -118f);
        cardSub = MakeCardText(card, "Sub", 24f, new Color(0.25f, 0.22f, 0.32f), FontStyles.Italic);
        cardSub.rectTransform.anchoredPosition = new Vector2(0f, -184f);
        cardSub.textWrappingMode = TextWrappingModes.Normal;
        cardSub.rectTransform.sizeDelta = new Vector2(620f, 64f);
        cardFoot = MakeCardText(card, "Foot", 24f, new Color(0.1f, 0.45f, 0.2f), FontStyles.Bold);
        cardFoot.rectTransform.anchoredPosition = new Vector2(0f, -252f);

        // the rubber stamp (frame + word), slammed on top, hanging over the card's top edge
        stamp = FeelUi.Rect("Stamp", card, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 4f), new Vector2(420f, 76f));
        stampFrame = FeelUi.Img(stamp, "Frame", FeelUi.Rounded, StampRed, 0.6f);
        var inner = FeelUi.Img(stamp, "Inner", FeelUi.Rounded, Paper, 0.5f);
        inner.rectTransform.offsetMin = new Vector2(6f, 6f);
        inner.rectTransform.offsetMax = new Vector2(-6f, -6f);
        stampText = FeelUi.Text(stamp, "Word", 50f, StampRed, TextAlignmentOptions.Center);

        cardRoot.gameObject.SetActive(false);
    }

    TextMeshProUGUI MakeCardText(Transform parent, string name, float size, Color color, FontStyles style)
    {
        var t = FeelUi.Text(parent, name, size, color, TextAlignmentOptions.Center, style);
        // ink on paper: no heavy outline material (keeps the paper look); the ink material of the text's own font
        var ink = UiTheme.InkFor(t.font);
        if (ink != null) t.fontSharedMaterial = ink;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(660f, 60f);
        return t;
    }

    void BuildMemo()
    {
        memo = FeelUi.Rect("HeadOfficeMemo", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(MemoWidth, 150f));
        memo.localRotation = Quaternion.Euler(0f, 0f, 1.2f);
        memoGroup = memo.gameObject.AddComponent<CanvasGroup>();
        memoGroup.interactable = false;
        memoGroup.blocksRaycasts = false;
        FeelUi.Panel(memo, Paper, 0.8f, new Vector2(6f, -7f));
        var strip = FeelUi.Rect("Strip", memo, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(MemoWidth - 20f, 26f));
        FeelUi.Img(strip, "Bg", FeelUi.Rounded, StampRed, 0.4f);
        StretchX(strip, 10f, 10f);
        var label = FeelUi.Text(strip, "Label", 18f, Color.white, TextAlignmentOptions.Left);
        label.rectTransform.offsetMin = new Vector2(12f, 0f);
        label.text = "MEMO · HEAD OFFICE";
        memoStamp = FeelUi.Text(strip, "Day", 18f, new Color(1f, 0.9f, 0.85f), TextAlignmentOptions.Right, FontStyles.Normal);
        memoStamp.rectTransform.offsetMax = new Vector2(-12f, 0f);

        memoSubject = MakeMemoText("Subject", 22f, Ink, FontStyles.Bold);
        memoSubject.rectTransform.anchoredPosition = new Vector2(18f, -40f);
        memoBody = MakeMemoText("Body", 18f, new Color(0.22f, 0.2f, 0.3f), FontStyles.Normal);
        memoBody.rectTransform.anchoredPosition = new Vector2(18f, -68f);
        memoBody.textWrappingMode = TextWrappingModes.Normal;
        memo.gameObject.SetActive(false);
    }

    static void StretchX(RectTransform rt, float left, float right)
    {
        float y = rt.anchoredPosition.y, h = rt.sizeDelta.y;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, y - h);
        rt.offsetMax = new Vector2(-right, y);
    }

    TextMeshProUGUI MakeMemoText(string name, float size, Color color, FontStyles style)
    {
        var t = FeelUi.Text(memo, name, size, color, TextAlignmentOptions.TopLeft, style);
        var ink = UiTheme.InkFor(t.font); // the material must match the text's font atlas
        if (ink != null) t.fontSharedMaterial = ink;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(MemoWidth - 36f, 28f);
        return t;
    }

    // ------------------------------------------------------------------ preview (edit-mode screenshots)

    /// <summary>Render through a camera (ScreenSpaceCamera) and always visible: edit-mode verification shots.</summary>
    public void EnablePreview(Camera cam, float uiScale = -1f)
    {
        preview = true;
        previewCam = cam;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 1f;
        scaler.enabled = false;
        UiScale.Unregister(scaler);
        // the same scale a real screen of the camera's size would use (match 0.5 × UI scale), like FeelHud's preview
        canvas.scaleFactor = cam != null && cam.pixelHeight > 0 ? UiScale.PreviewScaleFactor(cam.pixelWidth, cam.pixelHeight, uiScale) : 1f;
        // above FeelHUD's preview canvas (both render through the same camera at plane distance 1)
        canvas.sortingOrder = SortingOrder;
    }

    /// <summary>Preview: show this memo right away (no slide-in), for verification shots.</summary>
    public void PreviewLetter(HeadOfficeLetter l)
    {
        if (!built || l == null) return;
        letters.Clear();
        letter = null;
        letters.Enqueue(l);
    }

    // ------------------------------------------------------------------ events in

    /// <summary>A Rep change: "+25 REP" floater for 10+, bar flash for anything positive.</summary>
    public void OnRep(float delta, Vector3? worldPos)
    {
        if (!built) return;
        if (delta > 0f) { barFlash = Mathf.Min(1f, barFlash + 0.35f); chipScaleVel += Mathf.Min(3f, 0.6f + delta * 0.04f); }
        else if (delta < 0f) { wait = 0.5f; }
        if (Mathf.Abs(delta) < 10f) return;
        string s = (delta > 0 ? "+" : "") + Mathf.RoundToInt(delta).ToString("N0") + " REP";
        SpawnFloater(s, delta > 0 ? RepPurple : FeelUi.BadRed);
        if (worldPos.HasValue) GameHUD.FloatingText(worldPos.Value + Vector3.up * 0.8f, s, delta > 0 ? RepPurple : FeelUi.BadRed, 0.9f);
    }

    void SpawnFloater(string text, Color color)
    {
        var f = floaters[floaterCursor];
        floaterCursor = (floaterCursor + 1) % floaters.Length;
        f.live = true;
        f.age = 0f;
        f.x = badgeOnly ? ChipPos.x + BadgeWidth * 0.5f + ((floaterCursor % 3) - 1) * 18f
                        : ChipPos.x + 88f + (ChipWidth - 100f) * Mathf.Clamp01(barShown) + ((floaterCursor % 3) - 1) * 18f;
        f.text.text = text;
        f.text.color = color;
        f.rt.gameObject.SetActive(true);
        f.rt.SetAsLastSibling();
    }

    public void ShowPromotion(int oldRank, int newRank)
    {
        int unlocks = Progression.UnlockCountBetween(oldRank, newRank);
        Enqueue(new Card
        {
            kind = CardKind.Promotion, stamp = "PROMOTED!", stampColor = StampRed,
            header = $"RUBBISH CO. HEAD OFFICE  ·  RANK {newRank} OF {Progression.MaxRank}",
            title = Progression.RankTitle(newRank).ToUpperInvariant(),
            sub = "\"" + Progression.HeadOfficeLine(newRank) + "\"",
            foot = unlocks > 0 ? $"New on {Scrapazon.Name}: {unlocks} item{(unlocks == 1 ? "" : "s")}" : "",
            hold = 4.8f, newRank = newRank,
        });
    }

    public void ShowKeyToCity() => Enqueue(new Card
    {
        kind = CardKind.KeyToCity, stamp = "KEY TO THE CITY", stampColor = new Color(0.85f, 0.6f, 0.05f),
        header = "OFFICE OF THE MAYOR  ·  (A RACCOON IN A SUIT)",
        title = "THE CITY IS YOURS",
        sub = "\"Mayor Bandit handed over a giant key, then took off his face. That explains the budget.\"",
        foot = "Golden Garbage Truck unlocked on " + Scrapazon.Name, hold = 5.5f,
    });

    public void ShowPermit(DistrictDef d)
    {
        if (d == null) return;
        Enqueue(new Card
        {
            kind = CardKind.Permit, stamp = "PERMIT APPROVED", stampColor = new Color(0.15f, 0.55f, 0.25f),
            header = "CITY COLLECTIONS DEPT.  ·  TIER " + d.tier,
            title = d.name.ToUpperInvariant(),
            sub = $"${d.fee:0} a bag{(d.tip > 0f ? $" + ${d.tip:0} tip" : "")} · Rep ×{d.repMult:0.#} · drive through the gate to cut the ribbon",
            foot = $"{Progression.Subscribers(d)} of {Progression.Houses(d)} houses signed up", hold = 4f,
        });
    }

    public void ShowWorkOrderDone(WorkOrder w, float pay, float rep) => Enqueue(new Card
    {
        kind = CardKind.WorkOrder, stamp = "DONE!", stampColor = new Color(0.15f, 0.55f, 0.25f),
        header = "WORK ORDER  ·  DAY " + Progression.Today,
        title = w != null ? w.Text : "Work Order",
        sub = "Head Office is mildly impressed.",
        foot = $"+{Economy.Money(pay)}  ·  +{Mathf.RoundToInt(rep)} REP", scale = 0.72f, hold = 2.4f,
    });

    void Enqueue(Card c)
    {
        if (!built || c == null) return;
        if (cards.Count < 6) cards.Enqueue(c);
    }

    public void ShowLetter(HeadOfficeLetter l)
    {
        if (!built || l == null || !l.popup) return;
        if (letters.Count < 6) letters.Enqueue(l);
    }

    // ------------------------------------------------------------------ per frame

    public void Tick(float dt)
    {
        if (!built) return;
        dt = Mathf.Clamp(dt, 0f, 0.1f);
        bool visible = preview || FeelVisible();
        canvas.enabled = visible;
        if (!visible) return;
        // HudLayout's mode (UIPass §8.3): the chip and memo show in Street / Driving / Depot and a co-op pause; Station,
        // the laptop, the full map and a solo pause hide them (cards wait while a UI holds the lock)
        var mode = HudLayout.Mode;
        bool solo = preview || NetSession.IsSolo || !NetSession.InSession;
        bool chipMode = mode == HudMode.Street || mode == HudMode.Driving || mode == HudMode.Depot || (mode == HudMode.Paused && !solo);
        bool uiBlocked = !preview && PlayerController.IsUIBlocking;
        bool blocked = uiBlocked || !chipMode;
        float time = Time.unscaledTime;

        TickChip(blocked, dt, time);
        TickFloaters(dt);
        TickCard(uiBlocked, dt, time);
        TickMemo(blocked, dt);
    }

    float revealGrace;

    /// <summary>FeelHUD's big treasure card is up (FeelHud.RevealShowing; read-only).</summary>
    static bool RevealShowing()
    {
        var runner = FeelRunner.Get();
        var hud = runner != null ? runner.Hud : null;
        return hud != null && hud.RevealShowing;
    }

    static bool FeelVisible()
    {
        var runner = FeelRunner.Get();
        var hud = runner != null ? runner.Hud : null;
        if (hud == null) return false;
        var c = hud.GetComponent<Canvas>();
        return c != null && c.enabled;
    }

    void TickChip(bool blocked, float dt, float time)
    {
        chipAlpha = Mathf.MoveTowards(chipAlpha, blocked ? 0f : 1f, dt * 6f);
        chipGroup.alpha = chipAlpha;

        // the rank slot in HudLayout's top bar: the full chip (Full) or badge-only (Compact / Tight, §8.2)
        var lay = HudLayout.Layout;
        if (lay.valid)
        {
            if (lay.rankBadge != badgeOnly || chip.sizeDelta.x <= 0f)
            {
                badgeOnly = lay.rankBadge;
                chipFull.gameObject.SetActive(!badgeOnly);
                ring.gameObject.SetActive(badgeOnly);
                chip.sizeDelta = new Vector2(badgeOnly ? BadgeWidth : ChipWidth, ChipHeight);
                badge.anchoredPosition = badgeOnly ? new Vector2(BadgeWidth * 0.5f, -6f) : new Vector2(42f, -6f);
            }
            ChipPos = new Vector2(lay.rank.x, -lay.rank.y);
            chip.anchoredPosition = ChipPos;
        }

        int rank = Progression.Rank;
        if (rank != shownRank)
        {
            if (shownRank > 0 && rank > shownRank) { badgeScaleVel += 14f; chipScaleVel += 6f; }
            shownRank = rank;
            rankNum.text = rank.ToString();
        }
        string title = Progression.RankTitle(rank);
        if (title != shownTitle)
        {
            // Shrink before trimming, like the promotion card: "Regional Rubbish Manager" read "Regional Rubbish Ma…".
            shownTitle = title;
            float size = 27f;
            titleText.fontSize = size;
            while (size > 20f && titleText.GetPreferredValues(title, 100000f, 0f).x > ChipWidth - 104f) titleText.fontSize = size -= 1f;
            titleText.text = FeelUi.Fit(titleText, title, ChipWidth - 104f);
        }

        string cap;
        bool waiting = Progression.WaitingOnCleanDays;
        int next = Progression.Next;
        if (next == 0)
            cap = GameSession.KeyToCity ? "Key to the City: yours" : $"Key to the City: {Mathf.Min(Progression.KeyStreak, Progression.KeyNightsNeeded)}/{Progression.KeyNightsNeeded} sparkling nights";
        else if (waiting)
        {
            int n = Progression.CleanDaysToNext;
            cap = $"<color=#FFB347>Promotion waits for {n} more Clean Day{(n == 1 ? "" : "s")}</color>";
        }
        else cap = "Next: " + Progression.RankTitle(next);
        if (cap != shownCap) { shownCap = cap; capText.text = cap.IndexOf('<') >= 0 ? cap : FeelUi.Fit(capText, cap, ChipWidth - 104f); }

        string rep = next == 0 ? $"{Mathf.FloorToInt(GameSession.Rep):N0} REP"
                               : $"{Mathf.FloorToInt(GameSession.Rep):N0} / {Progression.RankRep(next):N0} REP";
        if (rep != shownRep) { shownRep = rep; repText.text = rep; }

        float target = Progression.RankProgress01;
        if (barShown < 0f) barShown = target;
        barShown = FeelUi.Spring(barShown, ref barVel, target, dt, 70f, 10f);
        float s01 = Mathf.Clamp01(barShown);
        barFillRt.anchorMax = new Vector2(s01, 1f);
        barFill.gameObject.SetActive(s01 > 0.005f);
        barFill.color = waiting ? Color.Lerp(new Color(1f, 0.7f, 0.3f), Color.white, 0.25f + 0.25f * Mathf.Sin(time * 6f))
                                : Color.Lerp(RepPurple, FeelUi.Gold, s01 * s01);
        barFlash = Mathf.MoveTowards(barFlash, 0f, dt * 2.5f);
        barGlow.rectTransform.anchorMax = new Vector2(s01, 1f);
        barGlow.color = new Color(1f, 1f, 1f, barFlash * 0.7f);
        ringFill.fillAmount = s01;
        ringFill.color = barFill.color;

        chipScale = FeelUi.Spring(chipScale, ref chipScaleVel, 1f, dt, 240f, 13f);
        chip.localScale = Vector3.one * Mathf.Clamp(chipScale, 0.8f, 1.3f);
        badgeScale = FeelUi.Spring(badgeScale, ref badgeScaleVel, 1f, dt, 200f, 9f);
        badge.localScale = Vector3.one * Mathf.Clamp(badgeScale, 0.6f, 1.8f);
        wait = Mathf.MoveTowards(wait, 0f, dt);
        badge.localRotation = Quaternion.Euler(0f, 0f, 6f * Mathf.Sin(time * 1.7f) + wait * 25f * Mathf.Sin(time * 40f));
        badgeDisk.color = rank >= Progression.MaxRank
            ? Color.HSVToRGB(Mathf.Repeat(time * 0.15f, 1f), 0.45f, 1f)
            : Color.Lerp(FeelUi.Gold, Color.white, 0.15f + 0.15f * Mathf.Sin(time * 2.3f));
    }

    void TickFloaters(float dt)
    {
        foreach (var f in floaters)
        {
            if (!f.live) continue;
            f.age += dt;
            const float life = 1.4f;
            if (f.age >= life) { f.live = false; f.rt.gameObject.SetActive(false); continue; }
            float t = f.age / life;
            float pop = f.age < 0.15f ? Mathf.Lerp(0.4f, 1.25f, f.age / 0.15f) : Mathf.Lerp(1.25f, 1f, Mathf.Clamp01((f.age - 0.15f) / 0.2f));
            // rises UP out of the chip and off the top edge (H8: never down into the toast lane)
            float ease = 1f - (1f - t) * (1f - t);
            f.rt.anchoredPosition = new Vector2(f.x, Mathf.Lerp(ChipPos.y - 30f, 10f, ease));
            f.rt.localScale = Vector3.one * pop;
            f.text.alpha = t < 0.7f ? 1f : 1f - (t - 0.7f) / 0.3f;
        }
    }

    // ---- card

    void TickCard(bool blocked, float dt, float time)
    {
        if (current == null)
        {
            // never on top of FeelHUD's treasure reveal card (a Legendary find often IS the promotion)
            if (!preview && RevealShowing()) revealGrace = 0.4f;
            else revealGrace = Mathf.Max(0f, revealGrace - dt);
            if (blocked || cards.Count == 0 || revealGrace > 0f) { TickConfetti(dt); return; }
            BeginCard(cards.Dequeue());
        }
        cardAge += dt;
        var c = current;
        float k = c.scale;
        const float popIn = 0.28f, stampAt = 0.35f, stampDur = 0.16f, outDur = 0.3f;

        // card pops in, bobs, flies out
        float inT = Mathf.Clamp01(cardAge / popIn);
        float s = EaseOutBack(inT) * k;
        float outStart = c.hold;
        if (cardAge > outStart)
        {
            float o = Mathf.Clamp01((cardAge - outStart) / outDur);
            s *= 1f - o * 0.4f;
            cardGroup.alpha = 1f - o;
            if (o >= 1f) { EndCard(); TickConfetti(dt); return; }
        }
        else cardGroup.alpha = 1f;
        card.localScale = Vector3.one * s;
        card.localRotation = Quaternion.Euler(0f, 0f, -2.5f + 1.2f * Mathf.Sin(time * 2.1f));
        // every stamp card is a centre card (UIPass §8.2 CenterStage): the toast lane clamps above it, so nothing overlaps
        cardRoot.anchoredPosition = c.kind == CardKind.WorkOrder
            ? new Vector2(0f, 40f + 5f * Mathf.Sin(time * 1.6f))
            : new Vector2(0f, 70f + 6f * Mathf.Sin(time * 1.6f));

        // the stamp slams down a beat later
        float st = (cardAge - stampAt) / stampDur;
        if (st < 0f) { stamp.gameObject.SetActive(false); }
        else
        {
            stamp.gameObject.SetActive(true);
            float a = Mathf.Clamp01(st);
            stamp.localScale = Vector3.one * Mathf.Lerp(3.2f, 1f, a * a);
            stamp.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(-24f, -4f, a));
            var col = c.stampColor;
            stampText.color = new Color(col.r, col.g, col.b, Mathf.Lerp(0.2f, 1f, a));
            stampFrame.color = new Color(col.r, col.g, col.b, Mathf.Lerp(0.2f, 0.95f, a));
            if (!stampLanded && a >= 1f)
            {
                stampLanded = true;
                OnStampLanded(c);
            }
        }

        // sunburst
        rays.gameObject.SetActive(c.kind != CardKind.WorkOrder);
        bool calm = !preview && GameSettings.ReduceFlashing;   // Reduce flashing: PROMOTED rays stay still
        rays.localRotation = Quaternion.Euler(0f, 0f, calm ? 0f : time * 14f);
        float ra = Mathf.Clamp01(cardAge / 0.4f) * (cardAge > outStart ? cardGroup.alpha : 1f);
        var rc = c.kind == CardKind.KeyToCity ? FeelUi.Gold : c.stampColor;
        raysImg.color = new Color(Mathf.Lerp(rc.r, 1f, 0.4f), Mathf.Lerp(rc.g, 1f, 0.4f), Mathf.Lerp(rc.b, 1f, 0.4f), 0.45f * ra);
        rays.localScale = Vector3.one * (calm ? 0.93f : 0.9f + 0.06f * Mathf.Sin(time * 3f)) * k;

        TickConfetti(dt);
    }

    void BeginCard(Card c)
    {
        current = c;
        cardAge = 0f;
        stampLanded = false;
        headerText.text = c.header ?? "";
        stampText.text = c.stamp;
        stamp.sizeDelta = new Vector2(Mathf.Max(300f, stampText.GetPreferredValues(c.stamp).x + 60f), 76f);
        // Size first, then fit: long rank titles ("CURBSIDE ASSOCIATE", "REGIONAL RUBBISH MANAGER") shrink before they'd
        // ever be cut off with "…" (integration fix: the fit used to run at the previous card's size, then truncate).
        string title = c.title ?? "";
        float size = c.kind == CardKind.WorkOrder ? 40f : 54f;
        cardTitle.fontSize = size;
        while (size > 30f && cardTitle.GetPreferredValues(title, 100000f, 0f).x > 650f) cardTitle.fontSize = size -= 2f;
        cardTitle.text = FeelUi.Fit(cardTitle, title, 650f);
        cardSub.text = c.sub ?? "";
        cardFoot.text = c.foot ?? "";
        cardRoot.gameObject.SetActive(true);
        cardRoot.SetAsLastSibling();
        card.localScale = Vector3.zero;
        stamp.gameObject.SetActive(false);
        Sfx.Play(c.kind == CardKind.WorkOrder ? "ding" : "whoosh", null, 0.8f, c.kind == CardKind.WorkOrder ? 1.2f : 0.9f);
    }

    void OnStampLanded(Card c)
    {
        Sfx.Play("slam", null, c.kind == CardKind.WorkOrder ? 0.6f : 1f, 1.05f);
        switch (c.kind)
        {
            case CardKind.Promotion:
                Sfx.Play("legendary");
                Juice.Shake(0.25f, 0.3f);
                WorldConfetti();
                SpawnConfetti(70);
                break;
            case CardKind.KeyToCity:
                Sfx.Play("legendary", null, 1f, 0.9f);
                Juice.Shake(0.3f, 0.35f);
                WorldConfetti();
                SpawnConfetti(90);
                break;
            case CardKind.Permit:
                Sfx.Play("rare");
                Juice.Shake(0.15f, 0.2f);
                SpawnConfetti(40);
                break;
            default:
                Sfx.Play("cash", null, 0.9f, 1.1f);
                Juice.Shake(0.06f, 0.12f);
                SpawnConfetti(16);
                break;
        }
    }

    /// <summary>Fx.Burst ×4 colours just in front of the camera (§4.4).</summary>
    static void WorldConfetti()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var t = cam.transform;
        Vector3 c = t.position + t.forward * 2.6f + t.up * 0.4f;
        for (int i = 0; i < ConfettiColors.Length; i++)
            Fx.Burst(c + t.right * ((i - 1.5f) * 0.6f), ConfettiColors[i], 18, 4.5f);
    }

    void EndCard()
    {
        current = null;
        cardRoot.gameObject.SetActive(false);
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    void SpawnConfetti(int n) => SpawnConfetti(n, current != null && current.kind == CardKind.WorkOrder ? new Vector2(0f, 90f) : new Vector2(0f, 120f));

    void SpawnConfetti(int n, Vector2 origin)
    {
        for (int i = 0; i < n; i++)
        {
            Conf c = default;
            int idx = confetti.FindIndex(x => !x.alive);
            if (idx < 0)
            {
                if (confetti.Count >= 120) break;
                var rt = FeelUi.Rect("Confetti", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(12f, 20f));
                c.rt = rt;
                c.img = rt.gameObject.AddComponent<Image>();
                c.img.raycastTarget = false;
                confetti.Add(c);
                idx = confetti.Count - 1;
            }
            else c = confetti[idx];
            // deterministic-looking spread without a gameplay RNG (purely cosmetic)
            float h1 = Hash01(i * 7 + 1 + (int)(Time.unscaledTime * 60f)), h2 = Hash01(i * 13 + 5), h3 = Hash01(i * 31 + 11);
            c.alive = true;
            float spread = current != null && current.kind == CardKind.WorkOrder ? 150f : 260f;
            c.pos = origin + new Vector2(Mathf.Lerp(-spread, spread, h1), 0f);
            c.vel = new Vector2(Mathf.Lerp(-420f, 420f, h2), Mathf.Lerp(380f, 900f, h3));
            c.rot = h2 * 360f;
            c.spin = Mathf.Lerp(-720f, 720f, h1);
            c.life = Mathf.Lerp(1.6f, 2.6f, h3);
            c.img.color = ConfettiColors[i % ConfettiColors.Length];
            c.rt.gameObject.SetActive(true);
            c.rt.SetAsLastSibling();
            confetti[idx] = c;
        }
    }

    static float Hash01(int x)
    {
        unchecked
        {
            uint h = (uint)x * 2654435761u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }

    void TickConfetti(float dt)
    {
        for (int i = 0; i < confetti.Count; i++)
        {
            var c = confetti[i];
            if (!c.alive) continue;
            c.life -= dt;
            if (c.life <= 0f) { c.alive = false; c.rt.gameObject.SetActive(false); confetti[i] = c; continue; }
            c.vel.y -= 1300f * dt;
            c.vel.x *= 1f - 0.8f * dt;
            c.pos += c.vel * dt;
            c.rot += c.spin * dt;
            c.rt.anchoredPosition = c.pos;
            c.rt.localRotation = Quaternion.Euler(0f, 0f, c.rot);
            c.rt.localScale = new Vector3(Mathf.Cos(c.rot * Mathf.Deg2Rad * 2f), 1f, 1f);
            confetti[i] = c;
        }
    }

    // ---- memo

    void TickMemo(bool blocked, float dt)
    {
        var lay = HudLayout.Layout;
        float w = lay.valid && lay.rightWidth > 0f ? lay.rightWidth : 420f;
        if (letter == null)
        {
            if (blocked || letters.Count == 0 || current != null) return;
            // H9: under the route panel in the right column, sized for it; it waits while there's no room
            if (!HudLayout.MemoSlot(w, MeasureMemo(letters.Peek(), w), out _)) return;
            letter = letters.Dequeue();
            letterAge = 0f;
            LayoutMemo(w);
            memo.gameObject.SetActive(true);
            Sfx.Play("notify", null, 0.8f, 0.9f);
            Sfx.Play("rustle", null, 0.5f, 1.2f);
        }
        if (!Mathf.Approximately(w, MemoWidth)) LayoutMemo(w);
        bool fits = HudLayout.MemoSlot(MemoWidth, memo.sizeDelta.y, out var tl);
        if (fits) { memoTopLeft = tl; memoPlaced = true; }
        if (!fits || blocked)
        {
            // no room right now (the route grew, a small screen) or a UI is up: hide, and hold the clock
            memoGroup.alpha = 0f;
            if (!memoPlaced) return;
            return;
        }
        letterAge += dt;
        const float slide = 0.35f;
        float hold = MemoHold + Mathf.Min(4f, letter.body.Length / 60f);
        float x;
        if (letterAge < slide) x = Mathf.Lerp(MemoWidth + 60f, 0f, EaseOutBack(letterAge / slide));
        else if (letterAge < hold) x = 0f;
        else
        {
            float o = Mathf.Clamp01((letterAge - hold) / slide);
            x = Mathf.Lerp(0f, MemoWidth + 60f, o * o);
            if (o >= 1f) { letter = null; memoPlaced = false; memo.gameObject.SetActive(false); return; }
        }
        memo.anchoredPosition = new Vector2(memoTopLeft.x + x, -memoTopLeft.y);
        memo.localRotation = Quaternion.Euler(0f, 0f, 1.2f + (letterAge < 0.6f ? 4f * Mathf.Sin(letterAge * 30f) * (0.6f - letterAge) : 0f));
        memoGroup.alpha = 1f;
    }

    float MeasureMemo(HeadOfficeLetter l, float w)
    {
        if (l == null) return 110f;
        float bodyH = memoBody.GetPreferredValues(l.body ?? "", w - 36f, 0f).y;
        return Mathf.Max(110f, 80f + bodyH);
    }

    void LayoutMemo(float w)
    {
        MemoWidth = w;
        if (letter == null) return;
        memoSubject.rectTransform.sizeDelta = new Vector2(MemoWidth - 36f, 28f);
        memoSubject.text = FeelUi.Fit(memoSubject, letter.subject, MemoWidth - 40f);
        memoBody.text = letter.body;
        float bodyH = memoBody.GetPreferredValues(letter.body ?? "", MemoWidth - 36f, 0f).y;
        memoBody.rectTransform.sizeDelta = new Vector2(MemoWidth - 36f, bodyH + 4f);
        memo.sizeDelta = new Vector2(MemoWidth, Mathf.Max(110f, 80f + bodyH));
        memoStamp.text = $"DAY {letter.day} · {GameClock.FormatHours(letter.hour)}";
    }

    // ------------------------------------------------------------------ layout queries (FeelHud's toast lane, audits)

    /// <summary>A stamp card is on screen: its top-down rect in a root of <paramref name="size"/> (FeelHud clamps toasts above it).</summary>
    public bool CardRect(Vector2 size, out Rect r)
    {
        r = default;
        if (current == null || cardRoot == null || !cardRoot.gameObject.activeSelf) return false;
        float k = Mathf.Max(0.01f, card.localScale.x);
        var c = new Vector2(size.x * 0.5f + cardRoot.anchoredPosition.x, size.y * 0.5f - cardRoot.anchoredPosition.y);
        float w = 700f * k, h = 290f * k + 40f;
        r = new Rect(c.x - w * 0.5f, c.y - h * 0.5f - 20f, w, h);
        return true;
    }

    /// <summary>Top-down rects of the rank chip and the memo (when on screen), for FeelHud's overlap audit.</summary>
    public void CollectZoneRects(RectTransform hudRoot, List<(string name, Rect r)> into)
    {
        if (!built || !canvas.enabled) return;
        if (chipGroup.alpha > 0.01f) into.Add(("Rank", Aabb(chip)));
        if (memo.gameObject.activeSelf && memoGroup.alpha > 0.01f) into.Add(("Memo", Aabb(memo)));
    }

    Rect Aabb(RectTransform rt)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        var rs = root.rect.size;
        for (int i = 0; i < 4; i++)
        {
            var lp = root.InverseTransformPoint(corners[i]);
            float x = lp.x + rs.x * root.pivot.x;
            float y = rs.y * (1f - root.pivot.y) - lp.y;
            x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x);
            y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
        }
        return Rect.MinMaxRect(x0, y0, x1, y1);
    }
}

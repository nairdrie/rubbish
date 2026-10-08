using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Critter Hutch screen (opened at the hutch): a card per resident with its traits and what it is up to, and for the
/// owner the buttons that run the place (crew / pack / rest, the Breeding Box, a name, release, retire to the shop).
/// Presentation only: every button is a host-validated request (<see cref="Hutch"/>), and the cards are redrawn from the
/// replicated roster. Built on UiKit; <see cref="CreatePreview"/> renders it in edit mode.
/// </summary>
public sealed class HutchMenu : UiScreen
{
    const float SheetW = 1560f, SheetH = 900f, CardW = 480f, CardH = 270f, Gap = 16f, GridX = 44f, GridY = 132f, FooterY = 712f;
    const int CanvasLayer = UiLayers.Dialog - 6;
    static readonly Color accent = UiTheme.Hex(0x8E5BD0); // the Raccoon calling's purple (CallingsMenu.Accent)

    /// <summary>What the screen shows: live (a lot's business) or a sample (previews).</summary>
    public sealed class View
    {
        public HutchState hutch = new HutchState();
        public string businessName = "", ownerName = "";
        public bool owner, infected;
        public int hutchLevel, box, collar, brain, pack, embrace, rival;
    }

    static HutchMenu current;
    public static bool IsShowing => current != null && current.IsOpen;

    int lot = -1, pairPick, picked;
    bool preview, destroying;
    View sample;
    RectTransform sheet, grid, footer;
    TextMeshProUGUI title, line, hint, report, pickedLabel;
    TMP_InputField nameField;
    UiButton renameButton, retireButton, nibbleButton, closeButton;
    string shownKey;
    float nextRefresh;

    public static void Show(int lot)
    {
        if (!Application.isPlaying || Application.isBatchMode) return;
        Hide();
        var canvas = UiKit.Canvas("[Hutch]", CanvasLayer);
        current = canvas.gameObject.AddComponent<HutchMenu>();
        current.lot = lot;
        current.Build();
        current.Open(true);
    }

    public static void Hide()
    {
        if (current == null) return;
        var menu = current;
        current = null;
        if (menu.IsOpen) menu.Close();
        else Destroy(menu.gameObject);
    }

    /// <summary>Edit-mode preview for screenshots (no play mode, no stack, no lock).</summary>
    public static HutchMenu CreatePreview(Transform parent, Camera cam, int w, int h, View sample, int pickedId = 0, float uiScale = -1f)
    {
        var canvas = UiKit.Canvas("[Hutch Preview]", CanvasLayer, parent);
        UiKit.PreviewCanvas(canvas, cam, w, h, uiScale);
        var menu = canvas.gameObject.AddComponent<HutchMenu>();
        menu.preview = true; menu.sample = sample ?? new View(); menu.picked = pickedId;
        menu.Build();
        menu.Layout(UiScale.ReferenceSize(w, h, uiScale));
        menu.Refresh();
        return menu;
    }

    // ------------------------------------------------------------------ data

    View Read()
    {
        if (preview) return sample;
        var v = new View();
        if (!Workshop.TryBusinessAt(lot, out var business)) return v;
        ulong owner = business.ownerClientId;
        v.hutch = Hutch.For(business.Id);
        v.businessName = business.Name; v.ownerName = business.OwnerName;
        v.owner = owner == Permissions.LocalClientId;
        v.infected = GameSession.HasRabies;
        v.hutchLevel = Hutch.LevelFor(owner, CallingIds.CritterHutch); v.box = Hutch.LevelFor(owner, CallingIds.BreedingBox);
        v.collar = Hutch.LevelFor(owner, CallingIds.CollarChip); v.brain = Hutch.LevelFor(owner, CallingIds.BrainChip);
        v.pack = Hutch.LevelFor(owner, CallingIds.PackLeader); v.embrace = Hutch.LevelFor(owner, CallingIds.EmbraceIt);
        v.rival = Hutch.LevelFor(owner, CallingIds.KingsRival);
        return v;
    }

    // ------------------------------------------------------------------ build

    static RectTransform At(RectTransform child, float x, float y, float width, float height)
    {
        UiKit.Place(child, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
        return child;
    }

    static TextMeshProUGUI Text(Transform parent, string name, string text, float font, float x, float y, float w, float h,
        Color? color = null, bool display = false, FontStyles style = FontStyles.Normal, bool wrap = false)
    {
        var label = UiKit.InkLabel(parent, name, text, font, color ?? UiTheme.Ink, TextAlignmentOptions.TopLeft, display, style);
        label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Min(UiTheme.MinText, font);
        label.fontSizeMax = font;
        At(label.rectTransform, x, y, w, h);
        return label;
    }

    UiButton Btn(Transform parent, string label, System.Action click, float x, float y, float w, float h, UiButtonStyle style = UiButtonStyle.Paper)
    {
        var b = UiKit.Button(parent, label, click, style);
        At(b.Rect, x, y, w, h);
        b.Text.fontSizeMax = 20f;
        return b;
    }

    void Build()
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.6f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(SheetW, SheetH));
        var band = At(FeelUi.Rect("Band", sheet, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, 0f, SheetW, 12f);
        FeelUi.Img(band, "Fill", null, accent);
        title = Text(sheet, "Title", "CRITTER HUTCH", 44f, 44f, 22f, 1000f, 54f, null, true, FontStyles.Bold);
        line = Text(sheet, "Line", "", 24f, 46f, 74f, 1100f, 30f);
        hint = Text(sheet, "Hint", "", 20f, 46f, 102f, 1460f, 26f, UiTheme.InkSoft);
        closeButton = UiKit.Button(sheet, "Back", Hide, UiButtonStyle.Primary);
        At(closeButton.Rect, SheetW - 244f, 26f, 200f, 60f);
        grid = At(FeelUi.Rect("Cards", sheet, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), GridX, GridY, CardW * 3f + Gap * 2f, CardH * 2f + Gap);
        footer = At(FeelUi.Rect("Footer", sheet, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), GridX, FooterY, CardW * 3f + Gap * 2f, SheetH - FooterY - 24f);

        var news = At(FeelUi.Rect("Report", footer, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, 0f, 900f, 164f);
        FeelUi.Img(news, "Body", FeelUi.Rounded, UiTheme.PaperDark, 0.35f);
        Text(news, "Heading", "LAST SHIFT", 20f, 16f, 8f, 400f, 26f, accent, true, FontStyles.Bold).characterSpacing = 4f;
        report = Text(news, "Lines", "", 20f, 16f, 36f, 868f, 122f, null, false, FontStyles.Normal, true);

        var side = At(FeelUi.Rect("Picked", footer, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 916f, 0f, 556f, 164f);
        FeelUi.Img(side, "Body", FeelUi.Rounded, UiTheme.PaperDark, 0.35f);
        pickedLabel = Text(side, "Who", "", 20f, 16f, 8f, 524f, 26f, accent, true, FontStyles.Bold);
        nameField = UiKit.TextField(side, "New name", RaccoonTuning.NameMaxChars, TMP_InputField.CharacterValidation.None);
        At((RectTransform)nameField.transform, 16f, 40f, 330f, 52f);
        renameButton = Btn(side, "Rename", () => { if (picked > 0 && nameField != null) { Hutch.RequestRename(picked, nameField.text); nameField.text = ""; } }, 356f, 42f, 184f, 48f);
        retireButton = Btn(side, "Retire to shop pet", AskRetire, 16f, 104f, 262f, 48f);
        nibbleButton = Btn(side, "Get nibbled", Nibble, 286f, 104f, 254f, 48f, UiButtonStyle.Go);
        Refresh();
    }

    void AskRetire()
    {
        var r = Read().hutch.Find(picked);
        if (r == null) return;
        int id = r.id;
        UiKit.Confirm($"Retire {r.name}?", $"{r.name} leaves the hutch for good and becomes a Pet Raccoon in your keep crate, ready for a display shelf.",
            "Retire", "Keep", yes => { if (yes) Hutch.RequestRetire(id); });
    }

    void AskRelease(HutchRaccoon r)
    {
        int id = r.id;
        UiKit.Confirm($"Let {r.name} go?", $"{r.name} waddles back into the night. This frees a bunk. It can't be undone.",
            "Release", "Keep", yes => { if (yes) Hutch.RequestRelease(id); });
    }

    void Nibble()
    {
        var v = Read();
        var r = v.hutch.Find(picked);
        if (r == null || !FeralStatus.LocalNibble(r)) return;
        Hide();
    }

    // ------------------------------------------------------------------ refresh

    static HutchJob NextJob(HutchRaccoon r, View v)
    {
        var job = (HutchJob)r.job;
        if (job == HutchJob.Rest) return v.collar > 0 ? HutchJob.Crew : v.pack > 0 ? HutchJob.Companion : HutchJob.Rest;
        if (job == HutchJob.Crew) return v.pack > 0 ? HutchJob.Companion : HutchJob.Rest;
        return HutchJob.Rest;
    }
    static string JobLabel(HutchJob next, HutchRaccoon r) =>
        next == HutchJob.Crew || next == HutchJob.Rest && r.job == (int)HutchJob.Rest ? "Put on crew" : next == HutchJob.Companion ? "Take along" : "Send to rest";
    static string ShortEvent(int evt) => evt switch
    {
        CrewEvent.Dog => "dog chase", CrewEvent.Wandered => "wandered off", CrewEvent.Gnome => "gnome heist",
        CrewEvent.Wallet => "found a wallet", CrewEvent.Nap => "bin nap", _ => "",
    };

    void Refresh()
    {
        var v = Read();
        var h = v.hutch;
        int capacity = HutchRules.Capacity(v.hutchLevel);
        string key = h.Encode() + $"|{v.owner}{v.hutchLevel}{v.box}{v.collar}{v.brain}{v.pack}{v.embrace}{v.rival}{v.infected}|{pairPick}|{picked}|{v.businessName}";
        if (key == shownKey) return;
        shownKey = key;
        if (h.Find(pairPick) == null || !h.Find(pairPick).Available) pairPick = 0;
        if (h.Find(picked) == null) picked = 0;

        title.text = (v.hutchLevel >= 2 ? "CRITTER CONDO" : "CRITTER HUTCH") + (string.IsNullOrEmpty(v.businessName) ? "" : "  ·  " + v.businessName);
        var tier = RaccoonStandingRules.Tier(v.hutchLevel, v.embrace, v.collar, v.brain, v.pack, v.rival, h.collars);
        line.text = $"{h.Count} of {capacity} bunks  ·  kibble {Economy.Money(HutchRules.FoodCents(h, v.brain) / 100f)} a day  ·  standing: {RaccoonStandingRules.Title(tier)}" +
                    (h.collars > 0 ? $"  ·  King's Collars: {h.collars}" : "");
        hint.text = !v.owner ? $"{(string.IsNullOrEmpty(v.ownerName) ? "The owner" : v.ownerName)}'s raccoons. Look, don't feed."
            : h.Count == 0 ? "Empty. Catch a raccoon in a trash-can trap at night, then press F on the trap: \"Send it to your hutch\"."
            : v.collar <= 0 ? "They eat and nap. Print the Collar Chip (" + SolidWaste.Name + " on your laptop) and they'll collect bags at night."
            : v.brain <= 0 ? "Crew raccoons sweep the kerbs at 22:00 and 02:00, smelliest bag first. The Brain Chip makes them reliable and adds a noon run."
            : v.brain == 1 ? $"Crew raccoons sweep at 22:00, 02:00 and noon, cheapest streets first. Crew kibble: ${RaccoonTuning.CrewFoodCentsBrain / 100} a head. Report at 6 in the morning."
            : $"Full-time crew: 22:00, 02:00, 07:00 and noon, cheapest streets first. Crew kibble: ${RaccoonTuning.CrewFoodCentsFullTime / 100} a head, so rest the slackers.";

        for (int i = grid.childCount - 1; i >= 0; i--)
        {
            var child = grid.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
        }
        for (int slot = 0; slot < RaccoonTuning.MaxRaccoons; slot++)
        {
            var card = At(FeelUi.Rect("Card " + slot, grid, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero),
                (slot % 3) * (CardW + Gap), (slot / 3) * (CardH + Gap), CardW, CardH);
            if (slot < h.Count) BuildCard(card, h, h.raccoons[slot], v);
            else BuildEmpty(card, slot < capacity, v);
        }

        var lines = CrewRules.ReportLines(h);
        CrewRules.ShiftTotals(h, out int brought, out int ate);
        report.text = v.collar <= 0 ? "No crew yet. No report. Just vibes."
            : lines.Count == 0 ? "Nothing to report. Either a quiet night, or nobody is on the crew."
            : $"{brought} delivered, {ate} eaten.  " + string.Join("  ", lines);

        var pick = h.Find(picked);
        pickedLabel.text = pick != null ? pick.name.ToUpperInvariant() : v.owner ? "PRESS \"MORE\" ON A RACCOON" : "";
        bool can = v.owner && pick != null;
        nameField.gameObject.SetActive(v.owner);
        nameField.interactable = can;
        renameButton.gameObject.SetActive(v.owner); renameButton.SetDisabled(can ? null : "pick one", false);
        retireButton.gameObject.SetActive(v.owner); retireButton.SetDisabled(can && !pick.IsKit ? null : "pick an adult", false);
        bool nibble = v.owner && v.embrace > 0;
        nibbleButton.gameObject.SetActive(nibble);
        if (nibble)
        {
            nibbleButton.SetLabel(pick != null && pick.rabid ? $"Get nibbled by {pick.name}" : "Get nibbled");
            nibbleButton.SetDisabled(v.infected ? "already feral" : pick == null || !pick.rabid ? "pick a foamy one" : null, false);
        }
    }

    void BuildEmpty(RectTransform card, bool open, View v)
    {
        FeelUi.Img(card, "Body", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, open ? 0.35f : 0.18f), 0.4f);
        var t = Text(card, "Empty", open ? "EMPTY BUNK" : "CRITTER CONDO BUNK", 26f, 20f, 96f, CardW - 40f, 34f, UiTheme.WithAlpha(UiTheme.Ink, open ? 0.55f : 0.35f), true, FontStyles.Bold);
        t.alignment = TextAlignmentOptions.Top;
        var sub = Text(card, "How", open ? "Trap a raccoon at night. F on the trap sends it here." : "Build the Critter Condo for three more bunks.",
            20f, 30f, 134f, CardW - 60f, 60f, UiTheme.WithAlpha(UiTheme.Ink, open ? 0.55f : 0.4f), false, FontStyles.Normal, true);
        sub.alignment = TextAlignmentOptions.Top;
    }

    void BuildCard(RectTransform card, HutchState h, HutchRaccoon r, View v)
    {
        bool isPicked = r.id == picked;
        FeelUi.Img(card, "Body", FeelUi.Rounded, isPicked ? Color.Lerp(UiTheme.PaperDark, accent, 0.18f) : UiTheme.PaperDark, 0.4f);
        if (isPicked) FeelUi.Img(card, "Ring", UiKit.Sprites.RoundedOutline, accent, 0.4f);
        var swatch = At(FeelUi.Rect("Fur", card, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 14f, 12f, 44f, 44f);
        FeelUi.Img(swatch, "Fill", FeelUi.Circle, RaccoonDoll.FurOf(r));
        var mask = At(FeelUi.Rect("Mask", swatch, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 4f, 15f, 36f, 12f);
        FeelUi.Img(mask, "Fill", FeelUi.Rounded, UiTheme.Ink, 0.3f);
        Text(card, "Name", r.name, 30f, 68f, 10f, 250f, 40f, null, true, FontStyles.Bold);
        string tags = (r.IsKit ? "KIT " : "") + (r.variant != HutchVariant.Normal ? HutchVariant.Name(r.variant).ToUpperInvariant() + " " : "") + (r.rabid ? "FOAMY" : "");
        var tag = Text(card, "Tags", tags.Trim(), 20f, 322f, 18f, 144f, 26f, r.rabid ? UiTheme.Go : accent, true, FontStyles.Bold);
        tag.alignment = TextAlignmentOptions.TopRight;
        Text(card, "State", HutchRules.StateText(h, r), 20f, 16f, 58f, CardW - 32f, 26f, UiTheme.InkSoft);

        string[] labels = { "Zoom", "Carry", "Greed", "Nerve" };
        int[] values = { r.speed, r.carry, r.greed, r.nerve };
        for (int i = 0; i < 4; i++)
        {
            float y = 90f + i * 26f;
            Text(card, labels[i], labels[i], 19f, 16f, y, 70f, 24f);
            for (int k = 0; k < RaccoonTuning.TraitMax; k++)
            {
                var pip = At(FeelUi.Rect("Pip", card, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 88f + k * 18f, y + 6f, 14f, 12f);
                bool on = k < values[i];
                FeelUi.Img(pip, "Fill", FeelUi.Rounded, on ? (i == 2 ? UiTheme.Tangerine : accent) : UiTheme.WithAlpha(UiTheme.Ink, 0.14f), 0.25f);
            }
            Text(card, "Value", values[i].ToString(), 19f, 272f, y, 30f, 24f, null, false, FontStyles.Bold);
        }
        // the right-hand column: last shift in three short lines, then the lifetime count
        bool news = r.brought > 0 || r.ate > 0 || r.evt != CrewEvent.None;
        if (news || r.job == (int)HutchJob.Crew)
        {
            Text(card, "Shift head", "LAST SHIFT", 18f, 316f, 90f, 152f, 22f, accent, true, FontStyles.Bold);
            Text(card, "Shift haul", news ? $"{r.brought} in" + (r.ate > 0 ? $", ate {r.ate}" : "") : "nothing yet", 19f, 316f, 114f, 152f, 24f);
            Text(card, "Shift event", ShortEvent(r.evt), 19f, 316f, 140f, 152f, 24f, UiTheme.InkSoft);
        }
        Text(card, "Total", r.total > 0 ? $"{r.total} all time" : "", 18f, 316f, 168f, 152f, 24f, UiTheme.InkSoft);

        if (!v.owner) return;
        const float y0 = 222f, bh = 40f;
        var next = NextJob(r, v);
        var job = Btn(card, JobLabel(next, r), () => Hutch.RequestJob(r.id, next), 12f, y0, 150f, bh);
        if (!r.CanHoldJob || r.awayDays > 0) job.SetDisabled(r.IsKit ? "kit" : "busy", false);
        else if (v.collar <= 0 && v.pack <= 0) job.SetDisabled("needs a chip", false);

        UiButton pair;
        if (r.Paired) pair = Btn(card, "Unpair", () => Hutch.RequestUnpair(r.id), 168f, y0, 112f, bh);
        else if (pairPick == r.id) pair = Btn(card, "Cancel", () => { pairPick = 0; shownKey = null; }, 168f, y0, 112f, bh, UiButtonStyle.Go);
        else if (pairPick != 0) pair = Btn(card, "Pair!", () => { Hutch.RequestPair(pairPick, r.id); pairPick = 0; shownKey = null; }, 168f, y0, 112f, bh, UiButtonStyle.Go);
        else pair = Btn(card, "Pair", () => { pairPick = r.id; shownKey = null; }, 168f, y0, 112f, bh);
        if (v.box <= 0) pair.SetDisabled("needs the box", false);
        else if (!r.Paired && !r.Available) pair.SetDisabled("busy", false);

        Btn(card, "More", () => { picked = picked == r.id ? 0 : r.id; shownKey = null; }, 286f, y0, 84f, bh);
        Btn(card, "Release", () => AskRelease(r), 376f, y0, 92f, bh);
    }

    // ------------------------------------------------------------------ screen plumbing

    void Layout(Vector2 canvasSize)
    {
        if (canvasSize.x < 64f || canvasSize.y < 64f || sheet == null) return;
        float scale = Mathf.Min(1f, Mathf.Min((canvasSize.y - 36f) / SheetH, (canvasSize.x - 36f) / SheetW));
        sheet.localScale = new Vector3(scale, scale, 1f);
    }

    void Update()
    {
        if (preview || !IsOpen) return;
        if (SceneController.IsTransitioning || DepotRowWorld.Transitioning || !HutchWorld.TryFind(lot, out _)) { Hide(); return; }
        Layout(((RectTransform)transform).rect.size);
        if (shownKey == null || Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.25f; Refresh(); }
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.9f);
        Sfx.Play("chitter", null, 0.4f, 1.2f);
        Layout(((RectTransform)transform).rect.size);
        if (closeButton != null) UiKit.Focus(closeButton.gameObject);
    }
    public override bool OnBack() { Hide(); return true; }
    public override void OnHide()
    {
        if (current == this) current = null;
        if (!destroying && this != null) Destroy(gameObject);
    }
    protected override void OnDestroy() { destroying = true; if (current == this) current = null; base.OnDestroy(); }
}

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The fence's trade screen (uGUI on UiKit, a paper sheet like the Workshop's). Lists the hot goods in your keep crate
/// with what he pays; a click asks the host to buy one. Presentation only: the price shown is the same pure rule the host
/// uses (<see cref="ScavengerRules.FencePriceCents"/>), and nothing leaves your inventory until the host says sold.
/// </summary>
public sealed class FenceMenu : UiScreen
{
    const float SheetW = 900f, SheetH = 700f;
    static FenceMenu current;
    public static bool IsShowing => current != null && current.IsOpen;

    RectTransform sheet;
    ScrollRect scroll;
    TextMeshProUGUI greeting, sack, empty, status;
    UiButton sellAll, close;
    readonly List<UiButton> rows = new List<UiButton>();
    List<ItemInstance> previewItems;
    bool preview, destroying, dirty = true;
    float standing = 1f, nextRefresh;
    string signature = "";
    GameObject lastFocused;

    public static void Show()
    {
        if (!Application.isPlaying || Application.isBatchMode) return;
        Hide();
        var canvas = UiKit.Canvas("[Fence]", UiLayers.Dialog - 6);
        current = canvas.gameObject.AddComponent<FenceMenu>();
        current.Build();
        Scavenger.FenceAnswered += current.OnAnswer;
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

    /// <summary>Edit-mode preview for screenshots: no play mode, no stack, no lock; shows <paramref name="items"/>.</summary>
    public static FenceMenu CreatePreview(Transform parent, Camera cam, int w, int h, List<ItemInstance> items, float standing = 1f, float uiScale = -1f)
    {
        var canvas = UiKit.Canvas("[Fence Preview]", UiLayers.Dialog - 6, parent);
        UiKit.PreviewCanvas(canvas, cam, w, h, uiScale);
        var menu = canvas.gameObject.AddComponent<FenceMenu>();
        menu.preview = true; menu.previewItems = items ?? new List<ItemInstance>(); menu.standing = standing;
        menu.Build();
        var size = UiScale.ReferenceSize(w, h, uiScale);
        menu.sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (size.x - 36f) / SheetW, (size.y - 36f) / SheetH));
        menu.Refresh();
        return menu;
    }

    static TextMeshProUGUI Line(RectTransform parent, string name, string text, float size, Color color, float y, float height, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var label = UiKit.InkLabel(parent, name, text, size, color, align, false, FontStyles.Normal);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.enableAutoSizing = true; label.fontSizeMin = 16f; label.fontSizeMax = size;
        UiKit.Place(label.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -y), new Vector2(SheetW - 80f, height));
        return label;
    }

    void Build()
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.72f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(SheetW, SheetH));
        var title = UiKit.InkLabel(sheet, "Title", "THE FENCE", 44f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(SheetW - 80f, 56f));
        greeting = Line(sheet, "Greeting", "", 23f, UiTheme.InkSoft, 84f, 56f);
        sack = Line(sheet, "Sack", "", 24f, UiTheme.Ink, 146f, 34f);
        scroll = UiKit.ScrollList(sheet, new Vector2(SheetW - 70f, 372f));
        UiKit.Place((RectTransform)scroll.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -188f), new Vector2(SheetW - 70f, 372f));
        empty = Line(sheet, "Empty", "Nothing hot on you. He looks disappointed. He always looks disappointed.", 24f, UiTheme.InkSoft, 330f, 70f);
        status = Line(sheet, "Status", "", 21f, UiTheme.StampRed, 566f, 30f);
        sellAll = UiKit.Button(sheet, "Sell the lot", SellAll, UiButtonStyle.Primary);
        UiKit.Place(sellAll.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-180f, 30f), new Vector2(320f, 62f));
        close = UiKit.Button(sheet, "Walk away", Hide, UiButtonStyle.Paper);
        UiKit.Place(close.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 30f), new Vector2(320f, 62f));
        greeting.text = FenceTerms.Greeting("\"No names. No receipts. No refunds. The King takes his cut, I take mine.\"");
    }

    List<ItemInstance> HotItems()
    {
        if (preview) return previewItems;
        var list = new List<ItemInstance>();
        foreach (var item in GameSession.Inventory) if (ScavengerRules.IsHot(item)) list.Add(item);
        return list;
    }

    static string RowText(ItemInstance item, float standing)
    {
        var def = item.Def;
        float worth = EbaeMarket.Appraise(item);
        string rarity = def != null ? RarityInfo.Label(def.rarity) : "???";
        return $"{(def != null ? def.name : item.defId)} · {rarity} · worth {Economy.Money(worth)} · he pays {Economy.Money(ScavengerRules.FencePriceCents(worth, standing) / 100f)}";
    }

    void Refresh()
    {
        dirty = false;
        var items = HotItems();
        if (!preview) standing = ScavengerHost.Authority ? FenceTerms.Standing(Permissions.LocalClientId) : 1f;
        var key = new System.Text.StringBuilder();
        foreach (var item in items) key.Append(item.uid).Append(Scavenger.IsSelling(item.uid) ? "*" : "").Append(',');
        key.Append(standing);
        long total = 0;
        foreach (var item in items) total += ScavengerRules.FencePriceCents(EbaeMarket.Appraise(item), standing);
        int capacity = preview ? ScavengerRules.SackCapacity(1) : Scavenger.SackCapacity;
        sack.text = $"Swag sack: {items.Count} of {capacity}" + (items.Count > 0 ? $"   ·   the lot: {Economy.Money(total / 100f)}" : "");
        empty.gameObject.SetActive(items.Count == 0);
        sellAll.gameObject.SetActive(items.Count > 1);
        if (key.ToString() == signature) return;
        signature = key.ToString();

        foreach (var row in rows) if (row != null) { if (Application.isPlaying) Destroy(row.gameObject); else DestroyImmediate(row.gameObject); }
        rows.Clear();
        foreach (var item in items)
        {
            var it = item;
            bool busy = Scavenger.IsSelling(it.uid);
            var button = UiKit.Button(scroll.content, busy ? "Counting it out..." : RowText(it, standing), () => Sell(it), UiButtonStyle.Paper);
            button.Size(SheetW - 110f, 60f);
            button.Text.fontSizeMin = 15f; button.Text.fontSizeMax = 23f;
            if (it.Def != null) button.Text.color = Color.Lerp(UiTheme.Ink, RarityInfo.Color(it.Def.rarity), 0.35f);
            button.interactable = !busy;
            var le = button.GetComponent<LayoutElement>();
            if (le != null) le.minHeight = le.preferredHeight = 60f;
            rows.Add(button);
        }
        var navigation = new List<Selectable>(rows);
        if (sellAll.gameObject.activeSelf) navigation.Add(sellAll);
        navigation.Add(close);
        UiKit.ChainVertical(navigation);
        if (Application.isPlaying && IsOpen) UiKit.Focus(rows.Count > 0 ? rows[0].gameObject : close.gameObject);
    }

    void Sell(ItemInstance item)
    {
        if (preview || item == null) return;
        status.text = "";
        Scavenger.RequestFenceSell(item);
        dirty = true;
    }

    void SellAll()
    {
        if (preview) return;
        foreach (var item in HotItems()) Scavenger.RequestFenceSell(item);
        dirty = true;
    }

    void OnAnswer(FenceResult result)
    {
        dirty = true;
        if (result.ok) { UiTheme.Play("cash", 0.6f, 1f); if (status != null) status.text = ""; }
        else { UiTheme.Play("buzz", 0.4f, 1f); if (status != null) status.text = string.IsNullOrEmpty(result.why) ? "No deal." : result.why; }
    }

    void Update()
    {
        if (preview || !IsOpen) return;
        if (SceneController.IsTransitioning || DepotRowWorld.Transitioning || !ScavengerRules.FenceOpen(GameClock.Hours) || !ScavFence.LocalInReach) { Hide(); return; }
        if (dirty || Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.3f; Refresh(); }
        var root = (RectTransform)transform;
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / SheetW, (root.rect.height - 36f) / SheetH));
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected != lastFocused)
            foreach (var row in rows) if (row != null && selected == row.gameObject) UiKit.EnsureVisible(scroll, row.Rect);
        lastFocused = selected;
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.85f); Refresh(); }
    public override bool OnBack() { Hide(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; Scavenger.FenceAnswered -= OnAnswer; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

/// <summary>Call It: five sticker buttons, one guess. Opens over the game while you hold the bag.</summary>
public sealed class CallItMenu : UiScreen
{
    static CallItMenu current;
    public static bool IsShowing => current != null && current.IsOpen;

    RectTransform sheet;
    TrashBag bag;
    readonly List<UiButton> buttons = new List<UiButton>();
    bool destroying, preview;

    public static void Show(TrashBag bag)
    {
        if (!Application.isPlaying || Application.isBatchMode || bag == null) return;
        Hide();
        var canvas = UiKit.Canvas("[CallIt]", UiLayers.Dialog - 6);
        current = canvas.gameObject.AddComponent<CallItMenu>();
        current.bag = bag;
        current.Build("this bag");
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

    public static CallItMenu CreatePreview(Transform parent, Camera cam, int w, int h, float uiScale = -1f)
    {
        var canvas = UiKit.Canvas("[CallIt Preview]", UiLayers.Dialog - 6, parent);
        UiKit.PreviewCanvas(canvas, cam, w, h, uiScale);
        var menu = canvas.gameObject.AddComponent<CallItMenu>();
        menu.preview = true;
        menu.Build("this bag");
        return menu;
    }

    void Build(string what)
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.6f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(760f, 640f));
        var title = UiKit.InkLabel(sheet, "Title", "CALL IT", 46f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(700f, 58f));
        var note = UiKit.InkLabel(sheet, "Note", $"What is the best thing in {what} worth? One call a day. Right pays. Wrong pays nothing.", 22f, UiTheme.InkSoft, TextAlignmentOptions.Center, false, FontStyles.Normal);
        note.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(note.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(680f, 60f));
        for (int i = 0; i < ScavengerRules.CallBands; i++)
        {
            int band = i;
            string label = $"{ScavengerTuning.CallBandNames[i]}   (+{Economy.Money(ScavengerTuning.CallBonus[i])})";
            var button = UiKit.Button(sheet, label, () => Pick(band), i == ScavengerRules.CallBands - 1 ? UiButtonStyle.Primary : UiButtonStyle.Paper);
            UiKit.Place(button.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -160f - i * 72f), new Vector2(640f, 62f));
            button.Text.fontSizeMin = 16f; button.Text.fontSizeMax = 24f;
            buttons.Add(button);
        }
        var back = UiKit.Button(sheet, "Not this one", Hide, UiButtonStyle.Paper);
        UiKit.Place(back.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(300f, 56f));
        var navigation = new List<Selectable>(buttons) { back };
        UiKit.ChainVertical(navigation);
    }

    void Pick(int band)
    {
        if (preview) return;
        var target = bag;
        Hide();
        if (target != null) Scavenger.RequestCall(target, band);
    }

    void Update()
    {
        if (preview || !IsOpen) return;
        var carrier = TrashCarrier.Local;
        if (SceneController.IsTransitioning || bag == null || carrier == null || carrier.Held != bag) { Hide(); return; }
        var root = (RectTransform)transform;
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / 760f, (root.rect.height - 36f) / 640f));
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1.2f); if (buttons.Count > 0) UiKit.Focus(buttons[0].gameObject); }
    public override bool OnBack() { Hide(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Owner-only inventory picker, opened by pressing E at a display piece (<see cref="WorkshopDisplay"/>). Pins a
/// replicated display preview; does not remove or consume the valuable.</summary>
public sealed class WorkshopDisplayPicker : UiScreen
{
    static WorkshopDisplayPicker current;
    int lot;
    string recipeId;
    RectTransform sheet;
    ScrollRect scroll;
    readonly List<UiButton> buttons = new List<UiButton>();
    bool destroying;
    GameObject lastFocused;

    public static void Show(int lot, string recipe)
    {
        if (!Workshop.CanManage(lot)) return;
        if (current != null) current.Close();
        var canvas = UiKit.Canvas("[WorkshopDisplayPicker]", UiLayers.Dialog - 2);
        current = canvas.gameObject.AddComponent<WorkshopDisplayPicker>();
        current.lot = lot;
        current.recipeId = recipe;
        current.Build();
        current.Open(true);
    }

    void Build()
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.6f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(850f, 670f));
        var title = UiKit.InkLabel(sheet, "Title", "CHOOSE A DISPLAY ITEM", 36f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(780f, 52f));
        var note = UiKit.InkLabel(sheet, "Note", "From your own inventory. The original stays yours; selling/removing it clears its display.", 22f, UiTheme.InkSoft, TextAlignmentOptions.Center, false);
        note.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(note.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(770f, 60f));
        scroll = UiKit.ScrollList(sheet, new Vector2(780f, 398f));
        UiKit.Place((RectTransform)scroll.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -154f), new Vector2(780f, 398f));
        Add("Empty display", "");
        foreach (var item in GameSession.Inventory)
        {
            if (item == null || item.hot || string.IsNullOrEmpty(item.uid) || item.Def == null || item.Def.category != ItemCategory.Valuable) continue;
            Add((item.Def != null ? item.Def.name : item.defId) + $" · {Economy.Money(item.value)}", item.uid);
        }
        var close = UiKit.Button(sheet, "Back", Close, UiButtonStyle.Paper);
        UiKit.Place(close.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(330f, 58f));
        var navigation = new List<Selectable>(buttons);
        navigation.Add(close);
        UiKit.ChainVertical(navigation);
    }

    void Add(string label, string uid)
    {
        var button = UiKit.Button(scroll.content, label, () => { if (Workshop.CanManage(lot)) Workshop.PinDisplay(lot, recipeId, uid); Close(); }, UiButtonStyle.Paper);
        button.Size(748f, 60f);
        button.Text.fontSizeMax = 25f;
        button.GetComponent<LayoutElement>().preferredHeight = 60f;
        buttons.Add(button);
    }
    void Update()
    {
        if (!Workshop.CanManage(lot) || SceneController.IsTransitioning || DepotRowWorld.Transitioning) { Close(); return; }
        var root = (RectTransform)transform;
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / 850f, (root.rect.height - 36f) / 670f));
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected != lastFocused)
            foreach (var button in buttons) if (selected == button.gameObject) UiKit.EnsureVisible(scroll, button.Rect);
        lastFocused = selected;
    }
    public override void OnShow() { if (buttons.Count > 0) UiKit.Focus(buttons[0].gameObject); }
    public override bool OnBack() { Close(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

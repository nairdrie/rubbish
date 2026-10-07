using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Business-scoped workshop presentation. All spending/consumption is requested through host-validated Workshop commands.</summary>
public sealed class WorkshopMenu : UiScreen
{
    static WorkshopMenu current;
    public static bool IsShowing => current != null && current.IsOpen;
    public static WorkshopMenu Instance => current;
    public int Lot => lot;
    int lot;
    string selectedId;
    RectTransform sheet, detail, recipeContent;
    ScrollRect recipesScroll;
    TextMeshProUGUI title, access, materials, progress, detailTitle, detailBody, cost, completion;
    Image progressFill;
    UiButton startButton, cancelButton, donateButton, closeButton, renameButton, displayButton;
    readonly List<UiButton> recipeButtons = new List<UiButton>();
    readonly List<UiButton> paletteButtons = new List<UiButton>();
    readonly List<Selectable> detailControls = new List<Selectable>();
    float nextRefresh;
    bool dirty = true, destroying;
    GameObject lastFocused;

    public static void Show(int lot)
    {
        if (!Application.isPlaying || Application.isBatchMode) return;
        Hide();
        var canvas = UiKit.Canvas("[Workshop]", UiLayers.Dialog - 6);
        current = canvas.gameObject.AddComponent<WorkshopMenu>();
        current.lot = lot;
        current.Build();
        Workshop.Changed += current.Changed;
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

    /// <summary>Only the four mundane recyclable categories can become building stock. No mystery pieces or treasures.</summary>
    public static bool IsBuildingCategory(ItemCategory category) => category == ItemCategory.Metal ||
        category == ItemCategory.Plastic || category == ItemCategory.Glass || category == ItemCategory.Paper;

    public static bool CanSaveHeld(LooseItem item) => item != null && item.Item != null && item.Def != null &&
        item.Revealed && item.State != CarryState.Consumed && IsBuildingCategory(item.Category) &&
        WorkshopCatalog.TryGrams(item.Item, out _, out _);

    public static bool IsDisplayRecipe(string id) => id == "bottle_cabinet" || id == "rotating_pedestal" ||
        id == "wall_display" || id == "grand_display";

    public static string BuildMaterialsPrompt => GameControls.UsingGamepad ? "[Y] BUILD MATERIALS" : "[F] BUILD MATERIALS";

    /// <summary>The station keeps the item in hand until the host accepts the exact held network object.</summary>
    public static bool SaveHeldForBuilding(SortStation station)
    {
        if (station == null || !station.IsActive || !CanSaveHeld(SortingKit.LocalHeld as LooseItem)) return false;
        int targetLot = DepotRowWorld.Active ? DepotRowLayout.InteriorAt(station.transform.position) : -1;
        if (!Workshop.CanDonate(targetLot))
        {
            GameHUD.Toast("Only this business's owner and hired crew can save its building materials.", ToastStyle.Bad);
            return false;
        }
        Workshop.DonateHeld(targetLot); // No local release/consume: rejection must leave the held item intact.
        return true;
    }

    void Changed() => dirty = true;

    static RectTransform At(RectTransform child, float x, float y, float width, float height)
    {
        UiKit.Place(child, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
        return child;
    }

    static TextMeshProUGUI Text(RectTransform parent, string name, string text, float font, float x, float y, float w, float h,
        Color? color = null)
    {
        var label = UiKit.InkLabel(parent, name, text, font, color ?? UiTheme.Ink, TextAlignmentOptions.TopLeft, false, FontStyles.Normal);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Min(18f, font);
        label.fontSizeMax = font;
        At(label.rectTransform, x, y, w, h);
        return label;
    }

    void Build()
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.6f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(1180f, 760f));
        title = Text(sheet, "Title", "WORKSHOP", 44f, 32f, 22f, 1116f, 58f);
        access = Text(sheet, "Permissions", "", 22f, 34f, 86f, 1112f, 54f, UiTheme.InkSoft);
        materials = Text(sheet, "Materials bank", "", 28f, 34f, 140f, 1112f, 46f);
        progress = Text(sheet, "Active project", "", 23f, 34f, 188f, 1112f, 36f);
        var track = At(FeelUi.Rect("Progress", sheet, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 34f, 226f, 1112f, 10f);
        FeelUi.Img(track, "Track", FeelUi.Rounded, UiTheme.PaperDark);
        var fill = FeelUi.Stretch("Fill", track);
        progressFill = FeelUi.Img(fill, "Bar", FeelUi.Rounded, UiTheme.Go);

        recipesScroll = UiKit.ScrollList(sheet, new Vector2(548f, 398f));
        At((RectTransform)recipesScroll.transform, 30f, 252f, 548f, 398f);
        recipeContent = recipesScroll.content;
        foreach (var recipe in WorkshopCatalog.All)
        {
            string id = recipe.id;
            var button = UiKit.Button(recipeContent, recipe.name, () => SelectRecipe(id), UiButtonStyle.Paper);
            button.Size(510f, 68f);
            button.Text.fontSizeMin = 18f;
            button.Text.fontSizeMax = 25f;
            var le = button.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 68f;
            recipeButtons.Add(button);
        }
        detail = At(FeelUi.Rect("Project details", sheet, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 606f, 252f, 536f, 398f);
        donateButton = UiKit.Button(sheet, "BUILD MATERIALS · save held piece", Donate, UiButtonStyle.Go);
        At(donateButton.Rect, 32f, 674f, 786f, 56f);
        donateButton.Text.fontSizeMax = 25f;
        closeButton = UiKit.Button(sheet, "Close", Hide, UiButtonStyle.Paper);
        At(closeButton.Rect, 854f, 674f, 292f, 56f);
        if (WorkshopCatalog.All.Count > 0) SelectRecipe(WorkshopCatalog.All[0].id);
        Refresh();
    }

    public void SelectRecipe(string recipeId)
    {
        var recipe = FindRecipe(recipeId);
        if (recipe == null) return;
        selectedId = recipeId;
        foreach (Transform child in detail) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        detailControls.Clear();
        paletteButtons.Clear();
        renameButton = displayButton = null;
        detailTitle = Text(detail, "Project name", recipe.name, 31f, 0f, 0f, 536f, 46f);
        detailBody = Text(detail, "Description", recipe.description, 22f, 0f, 48f, 536f, 67f, UiTheme.InkSoft);
        cost = Text(detail, "Recipe materials", "", 22f, 0f, 117f, 536f, 52f);
        completion = Text(detail, "Project status", "", 21f, 0f, 174f, 536f, 34f, UiTheme.InkSoft);
        startButton = UiKit.Button(detail, "Start project", StartProject, UiButtonStyle.Go);
        At(startButton.Rect, 0f, 216f, 256f, 57f);
        startButton.Text.fontSizeMax = 23f;
        cancelButton = UiKit.Button(detail, "Cancel & refund", CancelProject, UiButtonStyle.Danger);
        At(cancelButton.Rect, 274f, 216f, 262f, 57f);
        cancelButton.Text.fontSizeMax = 22f;
        detailControls.Add(startButton);
        detailControls.Add(cancelButton);

        if (recipeId == "business_sign")
        {
            At(startButton.Rect, 0f, 216f, 256f, 45f);
            At(cancelButton.Rect, 274f, 216f, 262f, 45f);
            Text(detail, "Sign colour label", "SIGN COLOUR · existing business name", 20f, 0f, 269f, 536f, 25f);
            string[] extraColors = { "Ruby", "Blue", "Gold", "Charcoal" };
            for (int i = 0; i < WorkshopCatalog.ColorCount; i++)
            {
                int index = i;
                string colorName = i < DepotColors.Names.Length ? DepotColors.Names[i] : extraColors[i - 4];
                var button = UiKit.Button(detail, colorName, () => Customize(index), UiButtonStyle.Paper);
                At(button.Rect, i % 4 * 136f, 299f + i / 4 * 35f, 126f, 30f);
                button.Text.fontSizeMin = 16f;
                button.Text.fontSizeMax = 18f;
                button.Text.color = Color.Lerp(WorkshopWorld.SignColor(i), Color.black, .3f);
                paletteButtons.Add(button);
                detailControls.Add(button);
            }
            renameButton = UiKit.Button(detail, "Rename your business", Rename, UiButtonStyle.Paper);
            At(renameButton.Rect, 0f, 370f, 536f, 28f);
            renameButton.Text.fontSizeMax = 20f;
            detailControls.Add(renameButton);
        }
        else if (IsDisplayRecipe(recipeId))
        {
            displayButton = UiKit.Button(detail, "Choose inventory display…", OpenDisplayPicker, UiButtonStyle.Paper);
            At(displayButton.Rect, 0f, 296f, 536f, 56f);
            displayButton.Text.fontSizeMax = 24f;
            detailControls.Add(displayButton);
            Text(detail, "Display hint", "Display items remain in your inventory. No sale, purchase or destructive move is made here.", 20f, 0f, 359f, 536f, 39f, UiTheme.InkSoft);
        }
        RebuildNavigation();
        dirty = true;
        Refresh();
    }

    static WorkshopRecipe FindRecipe(string id)
    {
        foreach (var recipe in WorkshopCatalog.All) if (recipe.id == id) return recipe;
        return null;
    }

    static string MaterialsText(WorkshopMaterials stock) =>
        $"METAL {stock.metal / 1000f:0.##} kg   ·   PLASTIC {stock.plastic / 1000f:0.##} kg   ·   GLASS {stock.glass / 1000f:0.##} kg   ·   PAPER {stock.paper / 1000f:0.##} kg";

    void Refresh()
    {
        dirty = false;
        var state = Workshop.Snapshot(lot);
        bool manage = Workshop.CanManage(lot), donate = Workshop.CanDonate(lot);
        string business = GameSession.BusinessName;
        if (lot >= 0 && DepotRowNet.Instance != null && DepotRowNet.Instance.TryGetLot(lot, out var depot)) business = depot.businessName.ToString();
        title.text = "WORKSHOP · " + business;
        access.text = manage ? "OWNER · Choose projects and displays. Save recyclable pieces instead of selling them; no cash is charged."
            : donate ? "HIRED CREW · You can donate held recyclables. The owner chooses projects, colour and displays."
            : "VISITOR · Read-only. Only this business's owner and hired crew can donate. Only the owner can start or customize projects.";
        materials.text = state != null ? "BANK: " + MaterialsText(state.bank) : "Loading this business's workshop…";
        var active = state != null ? FindRecipe(state.activeRecipe) : null;
        progress.text = active != null ? $"BUILDING {active.name} · {state.Progress:P0} funded · saved materials fill this project first"
            : "NO ACTIVE PROJECT · Materials stay in this business's bank until its owner starts a project.";
        progressFill.rectTransform.anchorMax = new Vector2(state != null ? Mathf.Clamp01(state.Progress) : 0f, 1f);
        progressFill.rectTransform.offsetMax = Vector2.zero;
        for (int i = 0; i < recipeButtons.Count; i++)
        {
            var recipe = WorkshopCatalog.All[i];
            string status = state != null && state.Completed(recipe.id) ? "BUILT" : state != null && state.activeRecipe == recipe.id ? $"BUILDING {state.Progress:P0}" : "NOT BUILT";
            recipeButtons[i].SetLabel((recipe.id == selectedId ? "> " : "") + recipe.name + "\n<size=70%>" + status + "</size>");
        }
        var selected = FindRecipe(selectedId);
        if (selected != null)
        {
            cost.text = "COST: " + MaterialsText(selected.cost);
            bool built = state != null && state.Completed(selectedId);
            completion.text = built ? "BUILT · Installed in this business's shop" : state != null && state.activeRecipe == selectedId
                ? "ALLOCATED: " + MaterialsText(state.allocated) : "Start now; add the missing materials as you sort.";
            startButton.SetDisabled(!manage ? "Owner only" : state == null ? "Loading" : built ? "Already built"
                : !string.IsNullOrEmpty(state.activeRecipe) ? "Finish or cancel the active project" : null, false);
            startButton.SetLabel(built ? "Already built" : "Start project");
            cancelButton.SetDisabled(!manage ? "Owner only" : active == null ? "No active project" : null, false);
            for (int i = 0; i < paletteButtons.Count; i++)
            {
                var button = paletteButtons[i];
                button.SetDisabled(!manage ? "Owner only" : !built ? "Build this sign first" : null, false);
                // SetDisabled refreshes the widget colours; retain each palette's meaningful coloured label.
                Color color = Color.Lerp(WorkshopWorld.SignColor(i), Color.black, .3f);
                if (!button.IsInteractable()) color.a = .7f;
                button.Text.color = color;
            }
            if (renameButton != null) renameButton.SetDisabled(!manage ? "Owner only" : !built ? "Build this sign first" : null, false);
            if (displayButton != null)
            {
                displayButton.SetDisabled(!manage ? "Owner only" : !built ? "Build this display first" : null, false);
                var item = state != null ? state.Display(selectedId) : null;
                displayButton.SetLabel(item != null ? "Display: " + (item.Def != null ? item.Def.name : item.defId) : "Choose inventory display…");
            }
        }
        var held = SortingKit.LocalHeld as LooseItem;
        donateButton.SetDisabled(!donate ? "Owner or hired crew only" : state == null ? "Loading"
            : !CanSaveHeld(held) ? "Hold a revealed recyclable first" : null, false);
        donateButton.SetLabel(CanSaveHeld(held) ? "BUILD MATERIALS · save " + held.DisplayName
            : "BUILD MATERIALS · hold a revealed recyclable first");
        RebuildNavigation();
    }

    void RebuildNavigation()
    {
        var actions = new List<Selectable>();
        foreach (var control in detailControls) if (control != null && control.IsInteractable()) actions.Add(control);
        if (donateButton != null && donateButton.IsInteractable()) actions.Add(donateButton);
        if (closeButton != null) actions.Add(closeButton);
        UiKit.ChainVertical(actions);
        Selectable firstAction = actions.Count > 0 ? actions[0] : closeButton;
        for (int i = 0; i < recipeButtons.Count; i++)
        {
            var nav = recipeButtons[i].navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? recipeButtons[i - 1] : closeButton;
            nav.selectOnDown = i + 1 < recipeButtons.Count ? recipeButtons[i + 1] : firstAction;
            nav.selectOnRight = firstAction;
            recipeButtons[i].navigation = nav;
        }
        foreach (var selectable in actions)
        {
            var nav = selectable.navigation;
            nav.selectOnLeft = recipeButtons.Count > 0 ? recipeButtons[0] : null;
            selectable.navigation = nav;
        }
    }

    void Donate() { if (Workshop.CanDonate(lot) && CanSaveHeld(SortingKit.LocalHeld as LooseItem)) Workshop.DonateHeld(lot); }
    void StartProject() { if (Workshop.CanManage(lot)) Workshop.Start(lot, selectedId); }
    void CancelProject()
    {
        if (!Workshop.CanManage(lot)) return;
        UiKit.Confirm("Cancel this build?", "Every allocated material returns to this business's bank. Completed furniture stays built.", "Cancel & refund", "Keep building",
            yes => { if (yes && this != null && Workshop.CanManage(lot)) Workshop.Cancel(lot); });
    }
    void Customize(int index) { if (Workshop.CanManage(lot)) Workshop.Customize(lot, index); }
    void Rename()
    {
        if (!Workshop.CanManage(lot)) return;
        BusinessRenameDialog.Show(GameSession.BusinessName, name =>
        {
            if (name == null || this == null || !Workshop.CanManage(lot)) return;
            if (lot >= 0 && DepotRowNet.Instance != null) DepotRowNet.Instance.RequestRename(name);
            else Businesses.RenameLocal(name);
            dirty = true;
        });
    }
    void OpenDisplayPicker() { if (Workshop.CanManage(lot)) WorkshopDisplayPicker.Show(lot, selectedId); }

    void Update()
    {
        if (!IsOpen) return;
        if (SceneController.IsTransitioning || DepotRowWorld.Transitioning) { Hide(); return; }
        if (dirty || Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.25f; Refresh(); }
        var root = (RectTransform)transform;
        float scale = Mathf.Min(1f, (root.rect.width - 36f) / 1180f, (root.rect.height - 36f) / 760f);
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, scale);
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        // Keep controller/keyboard recipe selection visible while traversing the long catalogue.
        if (selected != null && selected != lastFocused)
            foreach (var button in recipeButtons) if (selected == button.gameObject) EnsureVisible(recipesScroll, button.Rect);
        lastFocused = selected;
    }

    internal static void EnsureVisible(ScrollRect scroll, RectTransform row)
    {
        if (scroll == null || row == null) return;
        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, row);
        Rect view = scroll.viewport.rect;
        float move = bounds.min.y < view.yMin ? view.yMin - bounds.min.y : bounds.max.y > view.yMax ? view.yMax - bounds.max.y : 0f;
        if (Mathf.Abs(move) < 0.5f) return;
        scroll.StopMovement();
        scroll.content.anchoredPosition += Vector2.up * move;
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f); if (recipeButtons.Count > 0) UiKit.Focus(recipeButtons[0].gameObject); }
    public override bool OnBack() { Hide(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; Workshop.Changed -= Changed; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

/// <summary>Owner-only inventory picker. Pins a replicated display preview; does not remove or consume the valuable.</summary>
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
            if (item == null || string.IsNullOrEmpty(item.uid) || item.Def == null || item.Def.category != ItemCategory.Valuable) continue;
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
        if (!Workshop.CanManage(lot) || SceneController.IsTransitioning || !WorkshopMenu.IsShowing) { Close(); return; }
        var root = (RectTransform)transform;
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / 850f, (root.rect.height - 36f) / 670f));
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected != lastFocused)
            foreach (var button in buttons) if (selected == button.gameObject) WorkshopMenu.EnsureVisible(scroll, button.Rect);
        lastFocused = selected;
    }
    public override void OnShow() { if (buttons.Count > 0) UiKit.Focus(buttons[0].gameObject); }
    public override bool OnBack() { Close(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

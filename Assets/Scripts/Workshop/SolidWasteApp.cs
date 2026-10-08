using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// SolidWaste 3D, the print app on the shop laptop (Assets/UI/print/solidwaste.uxml, instanced inside the laptop document
/// as "solidWastePage"; the tab only exists once the business has its floppy disk: <see cref="Workshop.HasPrinter"/>).
/// Hosted by <see cref="EbaeUI"/>, which owns show / hide and the single UI lock. This is where blueprints (the Callings
/// tree) are printed from specific kept items and where the Monument is loaded and printed. Presentation only: the tree
/// comes from <see cref="CallingCatalog"/>, recipes from <see cref="CallingRecipes"/>, rules from
/// <see cref="WorkshopState"/>; a print is a host-validated <see cref="Workshop"/> request for the local player's own
/// business that consumes the parts from their inventory.
/// </summary>
public class SolidWasteApp
{
    const float ConfirmSeconds = 4f, RefreshSeconds = 0.4f;
    static string lastSelected;
    static readonly string[] MaterialNames = { "METAL", "PLASTIC", "GLASS", "PAPER" };
    static readonly (string menu, string joke)[] Menus =
    {
        ("File", "File > Save is disabled in the trial version. So is the trial version."),
        ("Edit", "Edit > Undo: you cannot un-print a glove."),
        ("Slice", "Slicing... sliced. It was already in slices. It is garbage."),
        ("View", "View > Wireframe requires a graphics card from this century."),
        ("Help", "Help > About: " + SolidWaste.Edition + ". Licensed to: NOBODY. Days left in trial: -10,372."),
    };

    readonly EbaeUI host;
    readonly VisualElement root;
    VisualElement viewport, canvas, legend, zoomBar, shelf, printerPill, logo, inspectorContent;
    ScrollView inspector;
    Label printerLabel, statusLeft, statusRight;
    readonly Label[] shelfValues = new Label[5];
    readonly VisualElement[] shelfChips = new VisualElement[5];
    SolidWasteTree tree;
    MonumentPreview preview;
    Image previewImage;

    bool active, dirty = true, userView, dragging;
    string selectedId, builtSignature;
    float zoom = 1f, fit = 1f, nextRefresh, nextTipAt, nextBlink, confirmUntil, busyUntil;
    Vector2 pan, dragFrom, panFrom;
    int tipIndex;
    bool blink;
    string confirmFor;
    Label printButton;
    VisualElement printProgress;
    Action printAction;
    string printLabel, printTitle, sentTitle;
    int confirmCount, printTier, sentTier;
    float confirmTreasure;

    public bool IsActive => active;

    public static SolidWasteApp Create(EbaeUI host, VisualElement documentRoot)
    {
        var page = documentRoot?.Q<VisualElement>("solidWastePage");
        if (page == null || page.Q<VisualElement>("swPage") == null) return null;
        return new SolidWasteApp(host, page);
    }

    SolidWasteApp(EbaeUI host, VisualElement root)
    {
        this.host = host;
        this.root = root;
        Wire();
    }

    static int Lot => Workshop.OwnLot;

    // ------------------------------------------------------------------ wiring (once per document)

    void Wire()
    {
        viewport = root.Q<VisualElement>("swViewport");
        canvas = root.Q<VisualElement>("swCanvas");
        legend = root.Q<VisualElement>("swLegend");
        zoomBar = root.Q<VisualElement>("swZoom");
        shelf = root.Q<VisualElement>("swShelf");
        printerPill = root.Q<VisualElement>("swPrinter");
        printerLabel = root.Q<Label>("swPrinterLabel");
        statusLeft = root.Q<Label>("swStatusLeft");
        statusRight = root.Q<Label>("swStatusRight");
        logo = root.Q<VisualElement>("swLogo");
        inspector = root.Q<ScrollView>("swInspector");
        if (inspector != null)
        {
            inspector.mode = ScrollViewMode.Vertical;
            inspector.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            inspector.mouseWheelScrollSize = 60f;
            inspectorContent = new VisualElement { name = "swInspectorContent" };
            inspectorContent.AddToClassList("sw-inspector-content");
            inspector.Add(inspectorContent);
        }

        var mark = root.Q<VisualElement>("swLogoMark");
        if (mark != null) mark.generateVisualContent += mgc => DrawMark(mgc, mark, SolidWasteColors.Hot, true, 2.4f);
        OnClick(logo, () => { Flash(logo, "sw-logo--hop", 140); FitView(); });

        var menus = root.Q<VisualElement>("swMenus");
        if (menus != null)
        {
            menus.Clear();
            foreach (var (menu, joke) in Menus)
            {
                string line = joke;
                var label = L(menu, "sw-menu");
                OnClick(label, () => { Sfx.Play("squeak", null, 0.5f, 1.2f); host.LaptopToast(line, "sw"); });
                menus.Add(label);
            }
        }

        if (shelf != null)
        {
            shelf.Clear();
            string[] names = { "SHELF", "METAL", "PLASTIC", "GLASS", "PAPER" };
            for (int i = 0; i < names.Length; i++)
            {
                var chip = V("sw-chip");
                chip.tooltip = i == 0 ? "Kept parts on your shelf" : "Kept " + names[i].ToLowerInvariant() + " the Monument could take";
                if (i > 0)
                {
                    var dot = V("sw-chip-dot"); dot.style.backgroundColor = SolidWasteColors.Materials[i - 1];
                    chip.Add(dot);
                }
                chip.Add(L(names[i], "sw-chip-label"));
                chip.Add(shelfValues[i] = L("0", "sw-chip-value"));
                shelf.Add(shelfChips[i] = chip);
            }
        }

        if (legend != null)
        {
            legend.Clear();
            LegendRow(HexLook.Full, UiText.PrintLegendPrinted);
            LegendRow(HexLook.Ready, UiText.PrintLegendReady);
            LegendRow(HexLook.Need, UiText.PrintLegendNeed);
            LegendRow(HexLook.Locked, UiText.PrintLegendLocked);
        }

        if (zoomBar != null)
        {
            zoomBar.Clear();
            zoomBar.Add(ZoomButton("-", () => ZoomBy(1f / 1.25f)));
            zoomBar.Add(ZoomButton("+", () => ZoomBy(1.25f)));
            zoomBar.Add(ZoomButton("FIT", FitView));
        }

        if (canvas != null)
        {
            tree = new SolidWasteTree(canvas);
            tree.Selected = hex => Select(hex.id, true);
            tree.Submitted = hex => { Select(hex.id, true); PressPrint(); };
        }
        if (viewport != null)
        {
            viewport.RegisterCallback<GeometryChangedEvent>(_ => { if (!userView) FitView(); else ApplyView(); });
            viewport.RegisterCallback<WheelEvent>(OnWheel);
            viewport.RegisterCallback<PointerDownEvent>(OnPointerDown);
            viewport.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            viewport.RegisterCallback<PointerUpEvent>(OnPointerUp);
            viewport.RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
        }
        root.style.display = DisplayStyle.None;
        builtSignature = null;
    }

    void LegendRow(HexLook look, string word)
    {
        var row = V("sw-legend-row");
        var m = V("sw-legend-mark");
        Color color = look == HexLook.Ready ? SolidWasteColors.Hot : look == HexLook.Locked ? SolidWasteColors.Off : SolidWasteColors.Cyan;
        m.generateVisualContent += mgc => DrawMark(mgc, m, color, look == HexLook.Full, look == HexLook.Ready ? 2.4f : 1.6f, look == HexLook.Locked);
        row.Add(m);
        row.Add(L(word, "sw-legend-text"));
        legend.Add(row);
    }

    Label ZoomButton(string text, Action action)
    {
        var b = L(text, "sw-zoom-btn");
        OnClick(b, action);
        return b;
    }

    /// <summary>A small hexagon: the logo, the legend's marks.</summary>
    static void DrawMark(MeshGenerationContext mgc, VisualElement e, Color color, bool filled, float width, bool dashed = false)
    {
        float w = e.contentRect.width, h = e.contentRect.height;
        if (float.IsNaN(w) || w < 4f) return;
        var p = mgc.painter2D;
        var pts = new Vector2[6];
        SolidWasteLayout.Corners(new Vector2(w * 0.5f, h * 0.5f), Mathf.Min(w, h) * 0.5f - width * 0.5f, pts);
        p.lineJoin = LineJoin.Round;
        if (dashed)
        {
            p.strokeColor = color; p.lineWidth = width;
            for (int i = 0; i < 6; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % 6];
                p.BeginPath(); p.MoveTo(Vector2.Lerp(a, b, 0.2f)); p.LineTo(Vector2.Lerp(a, b, 0.8f)); p.Stroke();
            }
            return;
        }
        p.BeginPath(); p.MoveTo(pts[0]); for (int i = 1; i < 6; i++) p.LineTo(pts[i]); p.ClosePath();
        if (filled) { p.fillColor = color; p.Fill(); }
        p.strokeColor = color; p.lineWidth = width; p.Stroke();
    }

    // ------------------------------------------------------------------ activation

    /// <summary>Show (true) or hide (false) the page. The host calls this on every open / app switch / close.</summary>
    public void SetActive(bool on)
    {
        if (!on)
        {
            if (active) { Workshop.BuildReplied -= OnBuildReplied; if (Workshop.NoticeSink == (Func<bool, string, bool>)Notice) Workshop.NoticeSink = null; }
            active = false;
            root.style.display = DisplayStyle.None;
            confirmFor = null;
            preview?.Hide();
            lastSelected = selectedId;
            return;
        }
        bool fresh = !active;
        active = true;
        root.style.display = DisplayStyle.Flex;
        host.SetAddress(SolidWaste.Address);
        if (!fresh) return;
        Workshop.BuildReplied += OnBuildReplied;
        Workshop.NoticeSink = Notice;
        var state = State();
        selectedId = tree != null && tree.Find(lastSelected) != null ? lastSelected : DefaultSelection(state);
        builtSignature = null; dirty = true; nextRefresh = 0f; nextTipAt = 0f;
        Refresh();
        if (!userView) FitView();
        Sfx.Play("pop", null, 0.7f, 1.2f);
        Flash(logo, "sw-logo--hop", 160);
        if (GameSession.GetCounter(SeenCounter) == 0)
        {
            GameSession.SetCounter(SeenCounter, 1);
            host.LaptopToast($"{SolidWaste.Edition} loaded from A:\\. pick a blueprint. press PRINT. try not to print your fingers.", "sw");
        }
    }

    /// <summary>Saved with the business: SolidWaste 3D has been opened at least once (the tab's NEW badge goes away).</summary>
    public const string SeenCounter = "solidwaste_seen";

    public void MarkDirty() => dirty = true;

    bool Notice(bool ok, string text)
    {
        if (!active) return false;
        host.LaptopToast(text, ok ? "sw" : "bad");
        return true;
    }

    static WorkshopState State() => Workshop.Snapshot(Lot) ?? GameSession.WorkshopSave;
    static bool Syncing => Businesses.Registry != null && Workshop.Snapshot(Lot) == null;

    /// <summary>What to show first: something printable, else the first thing still to print.</summary>
    string DefaultSelection(WorkshopState state)
    {
        var stock = Workshop.StockFor(state);
        foreach (var node in CallingCatalog.All) if (state.StatusOf(node, stock.Have) == CallingStatus.Ready) return node.id;
        foreach (var node in CallingCatalog.All) if (state.StatusOf(node, stock.Have) == CallingStatus.CantAfford) return node.id;
        return CallingCatalog.TrunkId;
    }

    // ------------------------------------------------------------------ per frame

    public void Tick()
    {
        if (!active || tree == null) return;
        float now = Time.unscaledTime;
        if (dirty || now >= nextRefresh) Refresh();
        tree.Animate(now, Time.unscaledDeltaTime);
        if (confirmFor != null && now > confirmUntil) { confirmFor = null; RefreshPrintButton(); }
        if (printProgress != null)
        {
            bool busy = now < busyUntil;
            printProgress.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            if (busy) printProgress.style.width = Length.Percent(Mathf.Clamp01(1f - (busyUntil - now) / 1.5f) * 100f);
        }
        if (now >= nextBlink)
        {
            nextBlink = now + 0.45f; blink = !blink;
            bool printing = Workshop.BuildPending || now < busyUntil;
            printerPill?.EnableInClassList("sw-printer--busy", printing);
            printerPill?.EnableInClassList("sw-printer--blink", printing && blink);
            if (printerLabel != null) printerLabel.text = printing ? "PRINTING" : "PRINTER IDLE";
            printButton?.EnableInClassList("sw-print--pulse", blink && printAction != null && confirmFor == null);
        }
        if (now >= nextTipAt && statusLeft != null)
        {
            nextTipAt = now + 9f;
            statusLeft.text = UiText.PrintTips[tipIndex++ % UiText.PrintTips.Length];
        }
        if (statusRight != null && (Time.frameCount & 31) == 0)
            statusRight.text = $"nozzle {208 + (int)(Mathf.Sin(now * 0.7f) * 3f)}°C  ·  bed warm-ish  ·  filament: garbage  ·  zoom {Mathf.RoundToInt(zoom / Mathf.Max(0.01f, fit) * 100f)}%";
        if (preview != null && previewImage != null && previewImage.panel != null) preview.Tick(Time.unscaledDeltaTime);
    }

    // ------------------------------------------------------------------ refresh

    void Refresh()
    {
        dirty = false;
        nextRefresh = Time.unscaledTime + RefreshSeconds;
        var state = State();
        var stock = Workshop.StockFor(state);
        var loadable = Workshop.MonumentStock(state, stock);
        bool monumentReady = state.monument < MonumentCatalog.MaxLevel && LoadCount(state, loadable) > 0;
        tree.Refresh(state, stock.Have, selectedId, monumentReady);

        int parts = GameSession.PartCount;
        SetChip(0, parts + " / " + GameSession.PartsCap, parts >= GameSession.PartsCap);
        for (int m = 0; m < 4; m++) SetChip(m + 1, loadable.Get((WorkshopMaterial)m).ToString(), false);

        string signature = Signature(state, stock, loadable);
        if (signature == builtSignature) { RefreshPrintButton(); return; }
        builtSignature = signature;
        BuildInspector(state, stock, loadable);
    }

    void SetChip(int index, string value, bool full)
    {
        if (shelfValues[index] == null) return;
        if (shelfValues[index].text != value) shelfValues[index].text = value;
        shelfChips[index].EnableInClassList("sw-chip--full", full);
    }

    /// <summary>How many pieces one press of the Monument button would load now.</summary>
    static int LoadCount(WorkshopState state, WorkshopMaterials loadable)
    {
        var need = state.MonumentNeed(); int n = 0;
        for (int m = 0; m < 4; m++) n += Mathf.Min(need.Get((WorkshopMaterial)m), loadable.Get((WorkshopMaterial)m));
        return n;
    }

    /// <summary>Everything the inspector shows for the selected hexagon, as text: rebuild only when it changes.</summary>
    string Signature(WorkshopState state, Workshop.Stock stock, WorkshopMaterials loadable)
    {
        var sb = new StringBuilder(96).Append(selectedId).Append('|').Append(Workshop.BuildPending ? 'p' : '-').Append(Syncing ? 's' : '-');
        var hex = tree.Find(selectedId);
        if (hex == null) return sb.ToString();
        if (hex.tier > 0)
            return sb.Append(state.monument).Append('/').Append(state.fed.metal).Append(',').Append(state.fed.plastic).Append(',').Append(state.fed.glass).Append(',')
                .Append(state.fed.paper).Append('/').Append(loadable.metal).Append(',').Append(loadable.plastic).Append(',').Append(loadable.glass).Append(',').Append(loadable.paper).ToString();
        var node = hex.node;
        sb.Append(state.CallingLevelAt(node.index)).Append('/').Append((int)state.StatusOf(node, stock.Have)).Append('/').Append(state.pins);
        var recipe = state.NextRecipe(node);
        if (recipe != null)
            foreach (var slot in recipe.slots)
                foreach (string option in slot.anyOf) sb.Append(':').Append(stock.Have(option)).Append('.').Append(stock.Blocked(option));
        return sb.ToString();
    }

    void Select(string id, bool center)
    {
        if (tree == null || tree.Find(id) == null) return;
        if (id == selectedId) return;
        selectedId = id; lastSelected = id; confirmFor = null;
        Sfx.Play("click", null, 0.35f, 1.3f);
        dirty = true;
        Refresh();
        if (center) EnsureVisible(tree.Find(id));
    }

    // ------------------------------------------------------------------ inspector

    void BuildInspector(WorkshopState state, Workshop.Stock stock, WorkshopMaterials loadable)
    {
        if (inspectorContent == null) return;
        Vector2 offset = inspector.scrollOffset;
        inspectorContent.Clear();
        printButton = null; printProgress = null; printAction = null; previewImage = null;
        var hex = tree.Find(selectedId);
        if (hex == null) return;
        if (hex.tier > 0) BuildMonument(hex.tier, state, loadable);
        else { preview?.Hide(); BuildBlueprint(hex.node, state, stock); }
        inspector.schedule.Execute(() => inspector.scrollOffset = offset).ExecuteLater(1);
    }

    void KitRow(string kit, Color color, string note)
    {
        var row = V("sw-kit-row");
        var chip = L(kit, "sw-kit-chip"); chip.style.backgroundColor = color;
        row.Add(chip);
        if (!string.IsNullOrEmpty(note)) row.Add(L(note, "sw-kit-note"));
        inspectorContent.Add(row);
    }

    void BuildBlueprint(CallingNode node, WorkshopState state, Workshop.Stock stock)
    {
        Color accent = SolidWasteColors.Kit(node.calling);
        int level = state.CallingLevelAt(node.index);
        var status = state.StatusOf(node, stock.Have);
        var next = node.Level(level + 1);
        var have = node.Level(level);
        var recipe = next?.recipe;

        KitRow(SolidWasteTree.KitName(node.calling), accent, node.isCapstone ? UiText.PrintCapstone : null);
        inspectorContent.Add(L(node.name, "sw-title"));
        inspectorContent.Add(L(node.description, "sw-flavour"));

        var versions = V("sw-versions");
        for (int i = 1; i <= node.maxLevel; i++)
        {
            var v = L("v" + i, "sw-version" + (i <= level ? " sw-version--done" : i == level + 1 ? " sw-version--next" : ""));
            if (i <= level) v.style.backgroundColor = accent;
            v.tooltip = node.Level(i).title;
            versions.Add(v);
        }
        versions.Add(L(level > 0 ? string.Format(UiText.PrintVersions, level, node.maxLevel) : UiText.PrintNotPrinted, "sw-versions-note"));
        inspectorContent.Add(versions);

        if (next != null)
        {
            inspectorContent.Add(L(UiText.PrintNext, "sw-section"));
            inspectorContent.Add(L(next.title, "sw-next-title"));
            inspectorContent.Add(L(next.effect, "sw-effect"));
        }
        else if (have != null)
        {
            inspectorContent.Add(L(UiText.PrintHave, "sw-section"));
            inspectorContent.Add(L(have.title, "sw-next-title"));
            inspectorContent.Add(L(have.effect, "sw-effect"));
        }

        if (recipe != null)
        {
            var head = V("sw-section-row");
            head.Add(L(UiText.PrintParts, "sw-section"));
            head.Add(V("sw-bar-spacer"));
            head.Add(L(UiText.PrintPartsHint, "sw-section-right"));
            inspectorContent.Add(head);
            foreach (var slot in recipe.slots) inspectorContent.Add(PartRow(slot, state, stock));
        }

        // why (not), and the button
        string why; bool good = false, bad = false;
        Action action = null; string label;
        switch (status)
        {
            case CallingStatus.Maxed: why = UiText.PrintMaxed; label = UiText.PrintButtonMaxed; good = true; break;
            case CallingStatus.ComingSoon: why = UiText.PrintSoon; label = UiText.PrintButtonSoon; break;
            case CallingStatus.Locked:
                state.MissingPrereq(node, out var needs, out int needsLevel);
                why = string.Format(UiText.PrintNeeds, needs.Level(needsLevel).title, needs.name, needsLevel);
                label = UiText.PrintButtonLocked; bad = true; break;
            case CallingStatus.CantAfford:
                why = Workshop.ShortText(recipe, stock);
                if (why.Length <= 60) why += " " + UiText.PrintKeepHint;
                label = UiText.PrintButtonNeed; bad = true; break;
            default:
                why = UiText.PrintReady; good = true;
                label = string.Format(UiText.PrintButton, next != null ? next.title.ToUpperInvariant() : node.name.ToUpperInvariant());
                string id = node.id;
                action = () => Workshop.BuildCalling(Lot, id);
                break;
        }
        if (Syncing) { why = UiText.PrintSyncing; action = null; label = UiText.PrintButtonLocked; good = bad = false; }
        confirmCount = recipe != null ? recipe.total : 0;
        confirmTreasure = 0f; printTitle = next != null ? next.title : node.name; printTier = 0;
        if (action != null)
        {
            var offer = Workshop.SelectFor(recipe, stock);
            if (offer != null) foreach (var item in offer) if (!Workshop.IsPart(item)) confirmTreasure += EbaeMarket.Appraise(item);
        }
        inspectorContent.Add(L(why, "sw-why" + (good ? " sw-why--good" : bad ? " sw-why--bad" : "")));
        AddPrintButton(label, action, node.id);

        if (next != null && have != null) inspectorContent.Add(L(UiText.PrintHave + ": " + have.title + ". " + have.effect, "sw-have"));
        if (level > 0 && have != null && !string.IsNullOrEmpty(have.howTo)) inspectorContent.Add(L("> " + have.howTo, "sw-log"));
    }

    VisualElement PartRow(RecipeSlot slot, WorkshopState state, Workshop.Stock stock)
    {
        int got = PartRecipe.Covered(slot, stock.Have), blocked = 0;
        bool enough = got >= slot.count, pinned = false;
        string shown = slot.defId, blockedId = null;
        foreach (string option in slot.anyOf)
        {
            if (state.IsPinned(Workshop.CatalogNumber(option))) pinned = true;
            if (stock.Blocked(option) > 0) { blocked += stock.Blocked(option); blockedId ??= option; }
        }
        foreach (string option in slot.anyOf) if (stock.Have(option) > 0) { shown = option; break; }

        var row = V("sw-part" + (enough ? " sw-part--have" : pinned ? " sw-part--hunting" : ""));
        var photo = V("sw-part-photo");
        photo.Add(host.LaptopPhoto(shown, Workshop.NameOf(shown)));
        row.Add(photo);
        var body = V("sw-part-body");
        body.Add(L(Workshop.NameOf(shown), "sw-part-name"));
        string hint; string hintClass = "sw-part-hint";
        if (!enough && blocked > 0) { hint = string.Format(UiText.PrintBlocked, blocked, stock.BlockedWhy(blockedId)); hintClass += " sw-part-hint--blocked"; }
        else if (slot.isChoice)
        {
            var others = new List<string>();
            foreach (string option in slot.anyOf) if (option != shown) others.Add(Workshop.NameOf(option));
            hint = string.Format(UiText.PrintOr, string.Join(" or ", others));
        }
        else hint = PartSources.Hint(ItemCatalog.Get(shown), pinned);
        if (pinned) { hint = UiText.PrintHunting + " · " + hint; if (!enough) hintClass += " sw-part-hint--hunting"; }
        body.Add(L(hint, hintClass));
        row.Add(body);
        row.Add(L(got + " / " + slot.count, "sw-part-count" + (enough ? " sw-part-count--ok" : "")));
        var mark = V("sw-part-mark");
        mark.generateVisualContent += mgc => DrawTick(mgc, mark, enough);
        row.Add(mark);
        row.tooltip = pinned ? "Stop hunting for this" : "Hunt for this: it gets marked WANTED, and Magnus keeps it";
        OnClick(row, () => TogglePin(slot, state));
        return row;
    }

    /// <summary>A tick or a cross (never colour alone).</summary>
    static void DrawTick(MeshGenerationContext mgc, VisualElement e, bool ok)
    {
        float w = e.contentRect.width, h = e.contentRect.height;
        if (float.IsNaN(w) || w < 4f) return;
        var p = mgc.painter2D;
        p.lineCap = LineCap.Round; p.lineJoin = LineJoin.Round; p.lineWidth = 2.6f;
        p.strokeColor = ok ? SolidWasteColors.Good : SolidWasteColors.Bad;
        p.BeginPath();
        if (ok) { p.MoveTo(new Vector2(w * 0.15f, h * 0.55f)); p.LineTo(new Vector2(w * 0.42f, h * 0.8f)); p.LineTo(new Vector2(w * 0.88f, h * 0.2f)); }
        else { p.MoveTo(new Vector2(w * 0.2f, h * 0.2f)); p.LineTo(new Vector2(w * 0.8f, h * 0.8f)); p.MoveTo(new Vector2(w * 0.8f, h * 0.2f)); p.LineTo(new Vector2(w * 0.2f, h * 0.8f)); }
        p.Stroke();
    }

    /// <summary>Pin or unpin a part ("hunt for this"): every option of an "or" slot together.</summary>
    void TogglePin(RecipeSlot slot, WorkshopState state)
    {
        if (slot == null || !Workshop.CanManage(Lot)) { Sfx.Play("buzz", null, 0.5f); return; }
        bool pinned = false;
        foreach (string option in slot.anyOf) if (state.IsPinned(Workshop.CatalogNumber(option))) pinned = true;
        foreach (string option in slot.anyOf)
            if (state.IsPinned(Workshop.CatalogNumber(option)) == pinned) Workshop.SetWanted(Lot, option, !pinned);
        dirty = true;
    }

    void BuildMonument(int tier, WorkshopState state, WorkshopMaterials loadable)
    {
        var def = MonumentCatalog.Tier(tier);
        bool printed = tier <= state.monument, next = tier == state.monument + 1;
        KitRow(UiText.PrintMonumentBanner, SolidWasteColors.Gold, string.Format(UiText.PrintTierOf, tier, MonumentCatalog.MaxLevel));
        inspectorContent.Add(L(def.name, "sw-title"));
        inspectorContent.Add(L(def.description, "sw-flavour"));

        // the turntable
        var frame = V("sw-preview");
        previewImage = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        previewImage.AddToClassList("sw-preview-image");
        frame.Add(previewImage);
        frame.Add(L(printed ? UiText.PrintStanding : UiText.PrintPreview, "sw-preview-badge" + (printed ? " sw-preview-badge--done" : "")));
        frame.Add(L("\"" + def.plaque + "\"", "sw-preview-size"));
        inspectorContent.Add(frame);
        try
        {
            preview ??= new MonumentPreview();
            var business = Businesses.Local;
            preview.Show(tier, business.IsValid ? DepotColors.Get(business.color) : DepotColors.Get(0), GameSession.BusinessName);
            previewImage.image = preview.Texture;
            previewImage.tintColor = printed ? Color.white : new Color(0.62f, 0.93f, 1f, 0.95f); // a blueprint until it is real
        }
        catch (Exception e) { Debug.LogException(e); previewImage.image = null; }

        var head = V("sw-section-row");
        head.Add(L(UiText.PrintFeedstock, "sw-section"));
        head.Add(V("sw-bar-spacer"));
        head.Add(L(string.Format(UiText.PrintFeedstockEach, def.each), "sw-section-right"));
        inspectorContent.Add(head);

        var need = state.MonumentNeed();
        int load = next ? LoadCount(state, loadable) : 0;
        for (int m = 0; m < 4; m++)
        {
            var material = (WorkshopMaterial)m;
            int cost = def.each, fed = printed ? cost : next ? state.fed.Get(material) : 0;
            int pending = next ? Mathf.Min(need.Get(material), loadable.Get(material)) : 0;
            var row = V("sw-spool");
            var dot = V("sw-spool-dot"); dot.style.backgroundColor = SolidWasteColors.Materials[m];
            row.Add(dot);
            row.Add(L(MaterialNames[m], "sw-spool-name"));
            var bar = V("sw-spool-bar");
            var fill = V("sw-spool-fill"); fill.style.backgroundColor = SolidWasteColors.Materials[m];
            fill.style.width = Length.Percent(cost > 0 ? fed * 100f / cost : 0f);
            bar.Add(fill);
            if (pending > 0)
            {
                var ghost = V("sw-spool-pending"); ghost.style.backgroundColor = SolidWasteColors.Materials[m];
                ghost.style.left = Length.Percent(fed * 100f / cost); ghost.style.width = Length.Percent(pending * 100f / cost);
                bar.Add(ghost);
            }
            row.Add(bar);
            row.Add(L(fed + " / " + cost, "sw-spool-count"));
            row.Add(L(pending > 0 ? "+" + pending : "", "sw-spool-shelf"));
            inspectorContent.Add(row);
        }

        string why, label; Action action = null; bool good = false, bad = false;
        if (printed) { why = UiText.PrintMonumentDone; label = string.Format(UiText.PrintTierDone, tier); good = true; }
        else if (!next) { why = string.Format(UiText.PrintMonumentLocked, tier - 1); label = UiText.PrintButtonLocked; bad = true; }
        else if (load <= 0) { why = UiText.PrintMonumentEmpty; label = UiText.PrintButtonNeed; bad = true; }
        else
        {
            bool finishes = load >= need.Total;
            why = finishes ? UiText.PrintReady : Workshop.MonumentLoadedText(load, need.Total - load).Replace("Loaded", "This loads");
            label = finishes ? string.Format(UiText.PrintTier, tier) : string.Format(UiText.PrintLoad, load, load == 1 ? "" : "S");
            good = finishes;
            action = () => Workshop.FeedMonument(Lot);
        }
        if (state.monument >= MonumentCatalog.MaxLevel && tier == MonumentCatalog.MaxLevel) why = UiText.PrintMonumentFinished;
        if (Syncing) { why = UiText.PrintSyncing; action = null; label = UiText.PrintButtonLocked; good = bad = false; }
        confirmCount = load; confirmTreasure = 0f; printTitle = def.name; printTier = tier;
        inspectorContent.Add(L(why, "sw-why" + (good ? " sw-why--good" : bad ? " sw-why--bad" : "")));
        AddPrintButton(label, action, SolidWasteTree.MonumentId(tier));
        if (next) inspectorContent.Add(L(UiText.PrintMonumentNote, "sw-note"));
    }

    // ------------------------------------------------------------------ the print button ("1-Click" takes two)

    void AddPrintButton(string label, Action action, string forId)
    {
        printLabel = label; printAction = action;
        printButton = L(label, "sw-print");
        printProgress = V("sw-print-progress");
        printProgress.pickingMode = PickingMode.Ignore;
        printProgress.style.display = DisplayStyle.None;
        printButton.Add(printProgress);
        OnClick(printButton, PressPrint);
        if (confirmFor != forId) confirmFor = null;
        inspectorContent.Add(printButton);
        RefreshPrintButton();
    }

    void RefreshPrintButton()
    {
        if (printButton == null) return;
        bool pending = Workshop.BuildPending;
        bool confirming = confirmFor != null && printAction != null && !pending;
        string text = pending ? UiText.PrintButtonBusy : !confirming ? printLabel
            : string.Format(UiText.PrintConfirm, confirmCount, confirmCount == 1 ? "" : "s") +
              (confirmTreasure > 0f ? string.Format(UiText.PrintConfirmTreasure, Economy.Money(confirmTreasure)) : "");
        if (printButton.text != text) printButton.text = text;
        printButton.EnableInClassList("sw-print--off", printAction == null && !pending);
        printButton.EnableInClassList("sw-print--confirm", confirming);
        printButton.EnableInClassList("sw-print--busy", pending);
    }

    void PressPrint()
    {
        if (printButton == null) return;
        if (printAction == null || Workshop.BuildPending || !Workshop.CanManage(Lot)) { Sfx.Play("buzz", null, 0.5f); Flash(printButton, "sw-print--pulse", 120); return; }
        if (confirmFor != selectedId)
        {
            // first press arms it: say exactly what leaves the inventory
            confirmFor = selectedId; confirmUntil = Time.unscaledTime + ConfirmSeconds;
            Sfx.Play("click", null, 0.6f, 0.9f);
            RefreshPrintButton();
            return;
        }
        confirmFor = null;
        var action = printAction;
        sentTitle = printTitle; sentTier = printTier; // what the reply will be about (the replicated state may lag behind it)
        Sfx.Play("whoosh", null, 0.6f, 1.2f);
        action();
        dirty = true;
        RefreshPrintButton();
    }

    void OnBuildReplied(byte action, string id, bool ok, string text)
    {
        dirty = true; builtSignature = null;
        if (!active || tree == null) return;
        if (!ok) { Sfx.Play("buzz", null, 0.6f); return; }
        if (action == Workshop.ActionBuildCalling)
        {
            string title = sentTitle ?? CallingCatalog.Get(id)?.name;
            tree.PlayPrint(id);
            busyUntil = Time.unscaledTime + 1.5f;
            Sfx.Play("sparkle", null, 0.8f);
            host.LaptopAfter(1500, () => { if (active) { Sfx.Play("ding", null, 1f, 1.15f); host.LaptopStamp("PRINTED!", title, "sw"); host.LaptopConfetti(46, 2, 5, 1); } });
        }
        else if (action == Workshop.ActionFeedMonument && !string.IsNullOrEmpty(text))
        {
            int tier = sentTier > 0 ? sentTier : State().monument;
            bool printed = text.StartsWith("MONUMENT TIER", StringComparison.Ordinal);
            Sfx.Play(printed ? "sparkle" : "toss_in", null, 0.8f);
            if (!printed) return;
            tree.PlayPrint(SolidWasteTree.MonumentId(tier));
            busyUntil = Time.unscaledTime + 1.5f;
            Select(SolidWasteTree.MonumentId(tier), false);
            host.LaptopAfter(1500, () =>
            {
                if (!active) return;
                Sfx.Play("legendary", null, 0.9f);
                host.LaptopStamp("TIER " + tier + "!", MonumentCatalog.Tier(tier)?.name, "sw");
                host.LaptopConfetti(tier >= 8 ? 120 : 70, 2, 5);
                host.LaptopToast("go outside and watch it print.", "sw");
            });
        }
    }

    // ------------------------------------------------------------------ the view: fit, zoom, pan

    void FitView()
    {
        if (viewport == null || canvas == null) return;
        float vw = viewport.layout.width, vh = viewport.layout.height;
        if (float.IsNaN(vw) || vw < 40f || vh < 40f) return;
        Vector2 size = SolidWasteLayout.Size;
        fit = Mathf.Min(vw / size.x, vh / size.y);
        zoom = fit; userView = false;
        pan = new Vector2((vw - size.x * zoom) * 0.5f, (vh - size.y * zoom) * 0.5f);
        ApplyView();
    }

    void ApplyView()
    {
        if (viewport == null || canvas == null) return;
        float vw = viewport.layout.width, vh = viewport.layout.height;
        Vector2 size = SolidWasteLayout.Size * zoom;
        // keep the sheet on the plate: centred when it fits, never dragged out of sight when it does not
        pan.x = size.x <= vw ? (vw - size.x) * 0.5f : Mathf.Clamp(pan.x, vw - size.x, 0f);
        pan.y = size.y <= vh ? (vh - size.y) * 0.5f : Mathf.Clamp(pan.y, vh - size.y, 0f);
        canvas.style.scale = new Scale(new Vector3(zoom, zoom, 1f));
        canvas.style.translate = new Translate(pan.x, pan.y);
    }

    void ZoomBy(float factor)
    {
        if (viewport == null) return;
        ZoomAt(new Vector2(viewport.layout.width, viewport.layout.height) * 0.5f, factor);
    }

    void ZoomAt(Vector2 at, float factor)
    {
        float target = Mathf.Clamp(zoom * factor, fit, Mathf.Max(fit, 1.6f));
        if (Mathf.Approximately(target, zoom)) return;
        pan = at - (at - pan) * (target / zoom);
        zoom = target; userView = zoom > fit * 1.01f;
        ApplyView();
    }

    void OnWheel(WheelEvent e)
    {
        ZoomAt(e.localMousePosition, e.delta.y < 0f ? 1.12f : 1f / 1.12f);
        e.StopPropagation();
    }

    void OnPointerDown(PointerDownEvent e)
    {
        if (e.button != 0 || e.target is HexNode || zoom <= fit * 1.01f) return;
        dragging = true; dragFrom = e.position; panFrom = pan;
        viewport.CapturePointer(e.pointerId);
        viewport.AddToClassList("sw-viewport--grab");
    }

    void OnPointerMove(PointerMoveEvent e)
    {
        if (!dragging) return;
        pan = panFrom + ((Vector2)e.position - dragFrom);
        ApplyView();
    }

    void OnPointerUp(PointerUpEvent e)
    {
        if (!dragging) return;
        viewport.ReleasePointer(e.pointerId);
        EndDrag();
    }

    void EndDrag() { dragging = false; viewport?.RemoveFromClassList("sw-viewport--grab"); }

    /// <summary>Pan (when zoomed in) so a hexagon picked with the keyboard or pad is on the plate.</summary>
    void EnsureVisible(HexNode hex)
    {
        if (hex == null || viewport == null || zoom <= fit * 1.01f) return;
        Vector2 c = new Vector2(hex.layout.center.x, hex.layout.center.y) * zoom + pan;
        float vw = viewport.layout.width, vh = viewport.layout.height, m = 70f * zoom;
        if (c.x < m) pan.x += m - c.x; else if (c.x > vw - m) pan.x -= c.x - (vw - m);
        if (c.y < m) pan.y += m - c.y; else if (c.y > vh - m) pan.y -= c.y - (vh - m);
        ApplyView();
    }

    /// <summary>Called by the host when the laptop document is rebuilt or closed for good.</summary>
    public void Dispose()
    {
        SetActive(false);
        preview?.Dispose(); preview = null;
    }

    // ------------------------------------------------------------------ element helpers

    static void Flash(VisualElement e, string cls, long ms)
    {
        if (e == null) return;
        e.AddToClassList(cls);
        e.schedule.Execute(() => e.RemoveFromClassList(cls)).ExecuteLater(ms);
    }

    static void OnClick(VisualElement e, Action action)
    {
        if (e == null) return;
        e.focusable = true;
        e.RegisterCallback<FocusInEvent>(_ => e.AddToClassList("controller-focus"));
        e.RegisterCallback<FocusOutEvent>(_ => e.RemoveFromClassList("controller-focus"));
        e.RegisterCallback<NavigationSubmitEvent>(ev => { ev.StopPropagation(); action(); });
        e.AddManipulator(new Clickable(action));
    }

    static Label L(string text, string classes)
    {
        var l = new Label(text);
        AddClasses(l, classes);
        return l;
    }

    static VisualElement V(string classes)
    {
        var v = new VisualElement();
        AddClasses(v, classes);
        return v;
    }

    static void AddClasses(VisualElement e, string classes)
    {
        if (string.IsNullOrEmpty(classes)) return;
        foreach (var c in classes.Split(' '))
            if (c.Length > 0) e.AddToClassList(c);
    }
}

/// <summary>
/// A slowly turning Monument tier for SolidWaste 3D's inspector: the real model (<c>WorkshopModels.BuildMonument</c>) in a
/// private studio far under the town, on its own layer with its own lamps, drawn into a small render texture a few times
/// a second. Nothing here is gameplay, physics or networked.
/// </summary>
public sealed class MonumentPreview : IDisposable
{
    const int Width = 512, Height = 288, PreviewLayer = 31;
    const float Interval = 1f / 20f;
    GameObject studio;
    Transform turntable;
    Camera camera;
    RenderTexture texture;
    WorkshopModel model;
    int shownTier;
    string shownKey;
    float nextRender, spin;
    bool warned;

    public Texture Texture => texture;

    void EnsureStudio()
    {
        if (studio != null) { studio.SetActive(true); return; }
        studio = new GameObject("SolidWaste preview studio (visuals only)") { hideFlags = HideFlags.HideAndDontSave, layer = PreviewLayer };
        studio.transform.position = new Vector3(600f, -12000f, 600f); // nowhere near the item photo studio, the town or a kill plane's business
        turntable = new GameObject("Turntable") { hideFlags = HideFlags.HideAndDontSave, layer = PreviewLayer }.transform;
        turntable.SetParent(studio.transform, false);
        var cameraObject = new GameObject("Preview camera") { hideFlags = HideFlags.HideAndDontSave };
        cameraObject.transform.SetParent(studio.transform, false);
        camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.027f, 0.063f, 0.118f, 1f);
        camera.cullingMask = 1 << PreviewLayer;
        camera.allowHDR = false; camera.allowMSAA = true; camera.useOcclusionCulling = false;
        camera.fieldOfView = 24f; camera.nearClipPlane = 0.2f; camera.farClipPlane = 80f;
        Lamp("Key", new Vector3(-5f, 8f, 7f), 260f, new Color(1f, 0.96f, 0.88f));
        Lamp("Fill", new Vector3(7f, 3f, 3f), 120f, new Color(0.7f, 0.9f, 1f));
        Lamp("Rim", new Vector3(1f, 6f, -8f), 160f, new Color(0.45f, 0.9f, 1f));
        texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "SolidWaste preview", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 2 };
        texture.Create();
    }

    void Lamp(string name, Vector3 position, float intensity, Color color)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(studio.transform, false);
        go.transform.localPosition = position;
        var lamp = go.AddComponent<Light>();
        lamp.type = LightType.Point; lamp.range = 30f; lamp.intensity = intensity; lamp.color = color;
        lamp.cullingMask = 1 << PreviewLayer; lamp.shadows = LightShadows.None;
    }

    /// <summary>Put tier <paramref name="tier"/> on the turntable (rebuilt only when it, the colour or the name changes).</summary>
    public void Show(int tier, Color accent, string businessName)
    {
        EnsureStudio();
        string key = tier + "/" + ColorUtility.ToHtmlStringRGB(accent) + "/" + businessName;
        if (key == shownKey && model != null) { nextRender = 0f; return; }
        shownKey = key; shownTier = tier;
        if (model != null) Destroy(model.gameObject);
        model = WorkshopModels.BuildMonument(tier, turntable, accent, businessName);
        foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PreviewLayer;
        // frame it: the whole piece, a little air above, seen from slightly over its shoulder
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(turntable.position, Vector3.one);
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        float height = Mathf.Max(1f, bounds.size.y), radius = Mathf.Max(0.5f, Mathf.Max(bounds.extents.x, bounds.extents.z));
        float halfV = camera.fieldOfView * 0.5f * Mathf.Deg2Rad, aspect = Width / (float)Height;
        float fitV = (height * 0.5f + 0.25f) / Mathf.Tan(halfV), fitH = (radius + 0.3f) / (Mathf.Tan(halfV) * aspect);
        float distance = Mathf.Max(fitV, fitH) + radius;
        Vector3 look = turntable.position + Vector3.up * (height * 0.5f);
        Vector3 dir = Quaternion.Euler(-9f, 0f, 0f) * Vector3.forward; // the camera sits in +Z (the model's face), a little above, and looks back at it
        camera.transform.position = look + dir * distance;
        camera.transform.rotation = Quaternion.LookRotation(look - camera.transform.position, Vector3.up);
        camera.farClipPlane = distance * 2f + 20f;
        spin = -24f; turntable.localRotation = Quaternion.Euler(0f, spin, 0f);
        nextRender = 0f;
        Render();
    }

    public void Tick(float dt)
    {
        if (studio == null || !studio.activeSelf || model == null) return;
        spin += dt * 22f;
        turntable.localRotation = Quaternion.Euler(0f, spin, 0f);
        nextRender -= dt;
        if (nextRender > 0f) return;
        nextRender = Interval;
        Render();
    }

    void Render()
    {
        try
        {
            var request = new RenderPipeline.StandardRequest { destination = texture };
            camera.targetTexture = texture;
            if (RenderPipeline.SupportsRenderRequest(camera, request)) RenderPipeline.SubmitRenderRequest(camera, request);
            else if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
        }
        catch (Exception e)
        {
            if (!warned) Debug.LogWarning("[SolidWaste] Monument preview unavailable: " + e.Message);
            warned = true;
        }
        finally { if (camera != null) camera.targetTexture = null; }
    }

    public void Hide() { if (studio != null) studio.SetActive(false); }

    public void Dispose()
    {
        if (texture != null) { texture.Release(); Destroy(texture); }
        if (studio != null) { studio.SetActive(false); Destroy(studio); }
        studio = null; camera = null; texture = null; model = null; shownKey = null;
    }

    static void Destroy(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
    }
}

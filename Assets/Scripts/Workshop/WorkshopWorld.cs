using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Per-business presentation of what a business owns that is not a machine: the DECOR bought on Scrapazon (inside the
/// garage, along its free walls) and the street pieces out front (the sign, the doorbell and the MONUMENT printed in
/// SolidWaste 3D). There is no workshop room and no workbench any more: printing lives on the laptop.
/// State comes only from Workshop's accepted business snapshot. Hidden guest interiors get the same canonical poses as the
/// owner's visible one. Installed props are not carryables, saved inventory, or additional sellable items.
/// <para>One world: one presenter per <see cref="DepotLot"/>, both sides. Legacy solo flow: one in the garage scene for
/// the inside, and one on the legacy shop's lot for the street side (<see cref="DepotFlowRules.LegacyShopLot"/>).</para>
/// </summary>
public sealed class WorkshopWorld : MonoBehaviour
{
    /// <summary>The business's logical lot (−1: the legacy solo garage, wherever it is shown).</summary>
    public int lot = -1;
    /// <summary>What this presenter shows.</summary>
    public bool Interior { get; private set; }
    public bool Street { get; private set; }
    /// <summary>The Monument standing out front right now (null when there is none, or this presenter has no street side).</summary>
    public WorkshopModel Monument { get; private set; }
    public int MonumentTier { get; private set; }
    public static event Action<int, string, WorkshopModel> ModelInstalled;
    static readonly List<WorkshopWorld> worlds = new List<WorkshopWorld>();
    public static IReadOnlyList<WorkshopWorld> All => worlds;
    readonly Dictionary<string, WorkshopModel> models = new Dictionary<string, WorkshopModel>();
    public IEnumerable<WorkshopModel> Models => models.Values;
    DepotLot depot;
    Transform inside, outside;
    float nextRefresh;
    string businessKey;
    int signColor = -1, streetLot = -1;
    bool ready;

    /// <summary>The garage floor in garage-local coordinates (the authored floor sits a few centimetres under zero).</summary>
    public const float FloorY = -.066f;
    /// <summary>Door-local street slot of the Monument: left of the bay door, clear of the walk-up hatch (x −5.6), the truck
    /// poses (x 0), the spawn apron (x −6..−2, z 9) and the pad's edge (x −15). Its plinth is at most 1.8 m in radius.</summary>
    public static readonly Vector3 MonumentLocal = new Vector3(-10.5f, 0f, 4.4f);
    public const float MonumentYaw = 12f;

    /// <summary>One world: the presenter of a lot (inside and street).</summary>
    public static WorkshopWorld Ensure(DepotLot lot)
    {
        if (lot == null || lot.interior == null) return null;
        var current = lot.GetComponent<WorkshopWorld>();
        if (current != null && current.ready && current.Interior) return current;
        if (current == null) current = lot.gameObject.AddComponent<WorkshopWorld>();
        current.lot = lot.lot; current.depot = lot; current.streetLot = lot.lot;
        current.Initialize(true, true); return current;
    }

    /// <summary>Legacy solo flow: the inside of the garage scene.</summary>
    public static WorkshopWorld EnsureLegacy(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || scene.name != "ShopScene") return null; // coop:allow(scene) authored solo room only
        foreach (var world in worlds) if (world != null && world.lot == -1 && world.Interior && world.gameObject.scene == scene) return world;
        var go = new GameObject("Shop decor"); SceneManager.MoveGameObjectToScene(go, scene);
        var component = go.AddComponent<WorkshopWorld>(); component.Initialize(true, false); return component;
    }

    /// <summary>Legacy solo flow: the street side of the shop, on the lot that stands in for it while the town is up.</summary>
    public static WorkshopWorld EnsureLegacyStreet(DepotLot lot)
    {
        if (lot == null || lot.lot != DepotFlowRules.LegacyShopLot) return null;
        var current = lot.GetComponent<WorkshopWorld>();
        if (current != null && current.ready) return current;
        if (current == null) current = lot.gameObject.AddComponent<WorkshopWorld>();
        current.lot = -1; current.depot = lot; current.streetLot = lot.lot;
        current.Initialize(false, true); return current;
    }

    void Initialize(bool interior, bool street)
    {
        if (ready) Clear();
        ready = true; Interior = interior; Street = street;
        if (interior)
        {
            Transform scope = depot != null && depot.interior != null ? depot.interior.transform : transform;
            inside = WorkshopModels.Node(scope, "Shop decor pieces", Vector3.zero);
        }
        if (street && depot != null) outside = WorkshopModels.Node(depot.transform, "Handmade street pieces", Vector3.zero);
        Refresh();
    }

    void Clear()
    {
        foreach (var model in models.Values) Kill(model != null ? model.gameObject : null);
        models.Clear(); Kill(Monument != null ? Monument.gameObject : null); Monument = null; MonumentTier = 0; signColor = -1;
        businessKey = null; pendingTier = 0;
        if (inside != null) Kill(inside.gameObject);
        if (outside != null) Kill(outside.gameObject);
        inside = outside = null;
    }

    void OnEnable() { if (!worlds.Contains(this)) worlds.Add(this); Workshop.Changed += Refresh; }
    void OnDisable() { worlds.Remove(this); Workshop.Changed -= Refresh; }
    void OnDestroy() { worlds.Remove(this); if (outside != null) Destroy(outside.gameObject); if (inside != null && depot != null) Destroy(inside.gameObject); }
    void Update()
    {
        if (!ready) return;
        if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .75f; Refresh(); }
        if (pendingTier > 0) ShowPendingTier();
    }

    public static WorkshopModel Find(int lot, string recipeId)
    {
        foreach (var world in worlds)
            if (world != null && world.ready && world.lot == lot && world.models.TryGetValue(recipeId, out var model)) return model;
        return null;
    }

    public void Refresh()
    {
        if (!ready) return;
        // the legacy street presenter only stands while that flow is on (a session can switch to the one world)
        if (lot == -1 && !Interior && !DepotRowWorld.LegacyStreet) { if (models.Count > 0 || Monument != null) { Clear(); ready = false; } return; }
        var state = Workshop.Snapshot(lot);
        bool have = Workshop.TryBusinessAt(lot, out var business);
        string key = have ? business.Id : "";
        if (key != businessKey)
        {
            foreach (var model in models.Values) Kill(model != null ? model.gameObject : null);
            models.Clear(); Kill(Monument != null ? Monument.gameObject : null); Monument = null; MonumentTier = 0;
            businessKey = key; signColor = -1; pendingTier = 0;
        }
        if (outside != null) outside.gameObject.SetActive(have && depot != null && depot.facade != null && depot.facade.gameObject.activeSelf);
        string name = have ? business.Name : GameSession.BusinessName;
        Color accent = have ? DepotColors.Get(business.color) : DepotColors.Get(Mathf.Max(lot, 0));
        foreach (string id in WorkshopModels.RecipeIds)
        {
            bool onStreet = StreetRecipe(id);
            Transform parent = onStreet ? outside : inside;
            if (parent == null) continue; // not this presenter's side
            bool built = state != null && state.Completed(id);
            if (models.TryGetValue(id, out var old) && (!built || id == "business_sign" && signColor != state.colorIndex))
            { Kill(old != null ? old.gameObject : null); models.Remove(id); }
            if (!built) continue;
            if (!models.TryGetValue(id, out var model) || model == null)
            {
                model = WorkshopModels.Build(id, parent, id == "business_sign" ? SignColor(state.colorIndex) : accent, name);
                model.lot = lot;
                Pose pose = onStreet ? StreetPlacement(streetLot, id) : PlacementFor(lot, id);
                model.transform.SetPositionAndRotation(pose.position, pose.rotation);
                models[id] = model;
                if (id == "business_sign") { signColor = state.colorIndex; WorkshopSign.Attach(model); }
                WorkshopGadget.Attach(model); WorkshopDisplay.Attach(model);
                ModelInstalled?.Invoke(lot, id, model);
            }
            model.SetBusinessName(name);
        }
        int tier = state != null ? Mathf.Clamp(state.monument, 0, MonumentCatalog.MaxLevel) : 0;
        // whichever side of the shop learns the business first notes the tier that was standing then
        if (have && state != null && !shownTier.ContainsKey(key)) shownTier[key] = tier;
        if (outside == null) return;
        monumentAccent = accent; monumentName = name;
        if (tier != MonumentTier || tier > 0 && Monument == null)
        {
            // A tier printed since this machine last saw the Monument waits for its player to walk up and watch it print
            // (ShowPendingTier); the one a save loads with, or a lower one, just stands there. "Last saw" outlives this
            // presenter (shownTier): solo prints at the laptop in the garage scene, and the street only loads afterwards.
            int shown = have && shownTier.TryGetValue(key, out int seen) ? seen : -1;
            if (Application.isPlaying && state != null && shown >= 0 && tier > shown)
            {
                if (MonumentTier != shown || shown > 0 && Monument == null) BuildMonument(shown, false);
                pendingTier = tier;
            }
            else { pendingTier = 0; BuildMonument(tier, false); if (have && state != null) shownTier[key] = tier; }
        }
        if (Monument != null) Monument.SetBusinessName(name);
    }
    /// <summary>The Monument tier this machine last SHOWED for a business (by business id), across scene loads.</summary>
    static readonly Dictionary<string, int> shownTier = new Dictionary<string, int>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetShownTiers() => shownTier.Clear();
    int pendingTier;
    Color monumentAccent;
    string monumentName;
    const float WatchRadius = 26f, PrintSeconds = 3.2f;

    void BuildMonument(int tier, bool printIn)
    {
        Kill(Monument != null ? Monument.gameObject : null); Monument = null; MonumentTier = tier;
        if (tier <= 0 || outside == null) return;
        Monument = WorkshopModels.BuildMonument(tier, outside, monumentAccent, monumentName);
        Monument.lot = lot;
        Pose pose = MonumentPose(streetLot);
        Monument.transform.SetPositionAndRotation(pose.position, pose.rotation);
        ModelInstalled?.Invoke(lot, MonumentCatalog.NodeId, Monument);
        if (!printIn) return;
        Monument.PrintIn(PrintSeconds);
        Fx.Dust(pose.position, 2.4f);
        Fx.Burst(pose.position + Vector3.up * 1.5f, SortingKit.Gold, 60, 6f);
        Sfx.Play("legendary", pose.position, 0.8f);
        var tierDef = MonumentCatalog.Tier(tier);
        GameHUD.FloatingText(pose.position + Vector3.up * (1.6f + tier * 0.45f), $"TIER {tier}: {(tierDef != null ? tierDef.name.ToUpperInvariant() : "MONUMENT")}!", SortingKit.Gold, 1.6f);
    }

    /// <summary>The new tier goes up when this machine's player is close enough to watch (and the street side is showing).</summary>
    void ShowPendingTier()
    {
        if (pendingTier <= MonumentTier) { pendingTier = 0; return; }
        if (outside == null || !outside.gameObject.activeInHierarchy) return;
        var viewer = PlayerController.Local; // coop:allow(local) presentation only: when this machine plays the print-in for its own player
        if (viewer == null || (viewer.transform.position - MonumentPose(streetLot).position).sqrMagnitude > WatchRadius * WatchRadius) return;
        int tier = pendingTier; pendingTier = 0;
        BuildMonument(tier, true);
        if (!string.IsNullOrEmpty(businessKey)) shownTier[businessKey] = tier;
    }

    public static Color SignColor(int i)
    {
        if (i < 4) return DepotColors.Get(i);
        return i switch { 4 => new Color(.85f, .30f, .37f), 5 => new Color(.24f, .42f, .71f),
            6 => new Color(.72f, .60f, .31f), _ => new Color(.28f, .34f, .33f) };
    }
    public static string SignColorName(int i)
    {
        string[] extra = { "Ruby", "Blue", "Gold", "Charcoal" };
        return i >= 0 && i < DepotColors.Names.Length ? DepotColors.Names[i] : extra[Mathf.Clamp(i - 4, 0, extra.Length - 1)];
    }

    /// <summary>Pieces that stand on the street side of a depot: the sign and the doorbell by the door, and the two bag
    /// toys on the forecourt (a hoop on the driveway, with the launcher that feeds it). Everything else is inside the garage.</summary>
    public static bool StreetRecipe(string id) => id == "business_sign" || id == "doorbell" || id == "bag_hoop" || id == "spring_launcher";

    /// <summary>The Monument's canonical pose on a street lot.</summary>
    public static Pose MonumentPose(int streetLot) => DepotRowLayout.StreetPose(Mathf.Max(0, streetLot), MonumentLocal, MonumentYaw);

    /// <summary>The spring launcher throws a bag this far: the hoop stands exactly here in front of it (its Landing anchor).</summary>
    public const float LaunchDistance = 3.10f;
    /// <summary>Door-local slot of the hoop on the forecourt: right of the door, beyond the sign, clear of the truck's way out
    /// (x 0), the lease sign's post (7, 8) and the hutch (11.6, 2.6). It faces back across the lot at the launcher.</summary>
    public static readonly Vector3 HoopLocal = new Vector3(12.2f, 0f, 7.6f);

    /// <summary>Canonical street pose of a street piece on <paramref name="streetLot"/> (door-local slots: x across the shop
    /// front, z toward the road).</summary>
    public static Pose StreetPlacement(int streetLot, string id)
    {
        Vector3 p; float yaw = 0f;
        switch (id)
        {
            case "business_sign": p = new Vector3(8.3f, 0, 2.2f); break;
            case "bag_hoop": p = HoopLocal; yaw = -90f; break;
            case "spring_launcher": p = HoopLocal + Vector3.left * LaunchDistance; yaw = 90f; break;
            default: p = new Vector3(4.9f, 0, .35f); break; // the doorbell
        }
        return DepotRowLayout.StreetPose(Mathf.Max(0, streetLot), p, yaw);
    }

    /// <summary>
    /// Pure canonical poses: authority never accepts a client-provided gadget location. Street pieces stand on the
    /// business's street lot (the legacy shop's lot outside the one world); everything else is inside the garage, in
    /// garage-local coordinates, in the two stretches of wall the machines leave free: the display line on the south wall
    /// between its two pillars (slim pieces only: the truck parks along it), and the break corner between the office step
    /// (z 1.06), the east wall's pillar and the tipping floor's box (z -1.54).
    /// </summary>
    public static Pose PlacementFor(int lot, string id)
    {
        if (StreetRecipe(id)) return StreetPlacement(lot >= 0 ? lot : DepotFlowRules.LegacyShopLot, id);
        float yaw = 0f;
        Vector3 at;
        switch (id)
        {
            // south wall, facing north into the garage (pillars at x -3.81..-2.55 and 2.12..3.38)
            case "bottle_cabinet": at = new Vector3(-1.90f, 0, -4.74f); break;
            case "grand_display": at = new Vector3(-.30f, 0, -4.72f); break;
            case "wall_display": at = new Vector3(1.35f, 1.65f, -4.86f); break;
            // the break corner: a sofa and a radio with their backs to the office step, the pedestal and the chute on the floor in front
            case "break_sofa": at = new Vector3(5.15f, 0, .66f); yaw = 180; break;
            case "scrap_radio": at = new Vector3(6.38f, 0, .64f); yaw = 180; break;
            case "rotating_pedestal": at = new Vector3(5.40f, 0, -.75f); break;
            case "sorting_chute": at = new Vector3(4.45f, 0, -.78f); yaw = 180; break;
            default: at = Vector3.zero; break;
        }
        at.y += FloorY;
        return new Pose(Origin(lot) + at, Quaternion.Euler(0, yaw, 0));
    }

    /// <summary>Where a lot's garage-local coordinates start in the world (the solo garage sits at the origin).</summary>
    public static Vector3 Origin(int lot) => lot >= 0 ? DepotRowLayout.InteriorOrigin(lot) : Vector3.zero;

    static void Kill(GameObject go) { if (go == null) return; go.SetActive(false); if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { worlds.Clear(); ModelInstalled = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (!Application.isPlaying) return;
        var go = new GameObject("Shop decor installer"); DontDestroyOnLoad(go); go.AddComponent<WorkshopWorldInstaller>();
    }
}

/// <summary>The business sign out front: the owner presses E to repaint it or rename the business (<see cref="WorkshopSignMenu"/>).</summary>
public sealed class WorkshopSign : MonoBehaviour, IInteractable
{
    WorkshopModel model;
    public static void Attach(WorkshopModel m)
    {
        if (m == null || m.GetComponent<WorkshopSign>() != null) return;
        var sign = m.gameObject.AddComponent<WorkshopSign>(); sign.model = m;
        var action = m.Anchor("Action") ?? m.transform;
        var zone = action.gameObject.AddComponent<InteractionZone>();
        zone.center = Vector3.up * .9f; zone.size = new Vector3(2.6f, 2.8f, 2.2f);
        zone.mode = ZoneMode.OnFoot; zone.score = 2.2f; zone.targetOverride = sign;
    }
    public bool CanInteract() => model != null && isActiveAndEnabled && !PlayerController.IsUIBlocking && Workshop.CanManage(model.lot);
    public string GetInteractionPrompt() => "Your sign: repaint or rename";
    public void OnInteract(GameObject interactor) { if (CanInteract()) WorkshopSignMenu.Show(model.lot); }
    public void Highlight(bool on) { }
}

/// <summary>Owner-only: the sign's eight colours and the business's name. Presentation only: a colour is a host-validated
/// <see cref="Workshop.Customize"/> request (allowed from the street), a rename goes the way the laptop's does.</summary>
public sealed class WorkshopSignMenu : UiScreen
{
    const float SheetW = 660f, SheetH = 430f;
    static WorkshopSignMenu current;
    int lot;
    RectTransform sheet;
    readonly List<UiButton> swatches = new List<UiButton>();
    bool destroying;
    float nextRefresh;

    public static void Show(int lot)
    {
        if (!Application.isPlaying || Application.isBatchMode || !Workshop.CanManage(lot)) return;
        if (current != null) current.Close();
        var canvas = UiKit.Canvas("[SignMenu]", UiLayers.Dialog - 4);
        current = canvas.gameObject.AddComponent<WorkshopSignMenu>();
        current.lot = lot;
        current.Build();
        current.Open(true);
    }

    void Build()
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.55f)).raycastTarget = true;
        sheet = UiKit.Paper(root, new Vector2(SheetW, SheetH));
        var title = UiKit.InkLabel(sheet, "Title", "YOUR SIGN", 40f, UiTheme.Ink, TMPro.TextAlignmentOptions.Center, true);
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(SheetW - 60f, 54f));
        for (int i = 0; i < WorkshopCatalog.ColorCount; i++)
        {
            int index = i;
            var button = UiKit.Button(sheet, WorkshopWorld.SignColorName(i), () => { if (Workshop.CanManage(lot)) Workshop.Customize(lot, index); nextRefresh = 0f; }, UiButtonStyle.Paper);
            UiKit.Place(button.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f + i % 4 * 150f, -(96f + i / 4 * 66f)), new Vector2(142f, 56f));
            button.Text.fontSizeMin = UiTheme.MinText; button.Text.fontSizeMax = 22f;
            swatches.Add(button);
        }
        var rename = UiKit.Button(sheet, "Rename your business", Rename, UiButtonStyle.Primary);
        UiKit.Place(rename.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 104f), new Vector2(SheetW - 68f, 62f));
        rename.Text.fontSizeMax = 26f;
        var close = UiKit.Button(sheet, "Done", Close, UiButtonStyle.Paper);
        UiKit.Place(close.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(300f, 54f));
        // d-pad: two rows of four swatches, then the two wide buttons
        for (int i = 0; i < swatches.Count; i++)
        {
            var nav = swatches[i].navigation; nav.mode = UnityEngine.UI.Navigation.Mode.Explicit;
            nav.selectOnLeft = i % 4 > 0 ? swatches[i - 1] : null; nav.selectOnRight = i % 4 < 3 ? swatches[i + 1] : null;
            nav.selectOnUp = i >= 4 ? swatches[i - 4] : null; nav.selectOnDown = i < 4 ? swatches[i + 4] : (UnityEngine.UI.Selectable)rename;
            swatches[i].navigation = nav;
        }
        UiKit.ChainVertical(new UnityEngine.UI.Selectable[] { rename, close }, swatches[4]);
        Refresh();
    }

    void Refresh()
    {
        int chosen = Workshop.Snapshot(lot)?.colorIndex ?? -1;
        for (int i = 0; i < swatches.Count; i++)
        {
            swatches[i].SetLabel((i == chosen ? "> " : "") + WorkshopWorld.SignColorName(i)); // the mark, never colour alone
            swatches[i].Text.color = Color.Lerp(WorkshopWorld.SignColor(i), Color.black, .3f);
        }
    }

    void Rename()
    {
        if (!Workshop.CanManage(lot)) return;
        BusinessRenameDialog.Show(GameSession.BusinessName, name =>
        {
            if (name == null || this == null || !Workshop.CanManage(lot)) return;
            if (lot >= 0 && DepotRowNet.Instance != null) DepotRowNet.Instance.RequestRename(name);
            else Businesses.RenameLocal(name);
        });
    }

    void Update()
    {
        if (!IsOpen) return;
        if (!Workshop.CanManage(lot) || SceneController.IsTransitioning || DepotRowWorld.Transitioning) { Close(); return; }
        if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.3f; Refresh(); }
        var root = (RectTransform)transform;
        sheet.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / SheetW, (root.rect.height - 36f) / SheetH));
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f); if (swatches.Count > 0) UiKit.Focus(swatches[0].gameObject); }
    public override bool OnBack() { Close(); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy() { destroying = true; if (current == this) current = null; base.OnDestroy(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}

sealed class WorkshopWorldInstaller : MonoBehaviour
{
    float next;
    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + 1f;
        // One world: every lot shows its own business, inside and out. Legacy flow (solo): the garage is its own scene
        // (the inside), and Depot Row is loaded beside the town for its street side only (the sign, the bell, the Monument).
        bool legacyStreet = DepotRowWorld.LegacyStreet;
        foreach (var lot in FindObjectsByType<DepotLot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!legacyStreet) WorkshopWorld.Ensure(lot);
            else if (lot.lot == DepotFlowRules.LegacyShopLot) WorkshopWorld.EnsureLegacyStreet(lot);
        }
        if (!DepotRowWorld.Active) WorkshopWorld.EnsureLegacy(SceneManager.GetSceneByName("ShopScene")); // coop:allow(scene) additive pockets use their own per-lot path
    }
}

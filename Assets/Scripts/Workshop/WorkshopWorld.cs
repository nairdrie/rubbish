using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Per-business presentation of the handmade workshop. State comes only from Workshop's accepted business snapshot.
/// Hidden guest interiors get the same canonical construction, anchors and geometry as the owner's visible one.
/// Installed props and unfinished projects are not carryables, saved inventory, or additional sellable items.
/// </summary>
public sealed class WorkshopWorld : MonoBehaviour
{
    public int lot = -1;
    public WorkshopAnnex Annex { get; private set; }
    public WorkshopModel Bench { get; private set; }
    public WorkshopModel Project { get; private set; }
    public static event Action<int, string, WorkshopModel> ModelInstalled;
    static readonly List<WorkshopWorld> worlds = new List<WorkshopWorld>();
    public static IReadOnlyList<WorkshopWorld> All => worlds;
    readonly Dictionary<string, WorkshopModel> models = new Dictionary<string, WorkshopModel>();
    public IEnumerable<WorkshopModel> Models => models.Values;
    DepotLot depot;
    Transform outside, assembly;
    TMP_Text projectLabel;
    float nextRefresh;
    string businessKey, projectId;
    int signColor = -1;
    bool ready;

    public static readonly Vector3 BenchLocal = new Vector3(12.48f, WorkshopAnnex.FloorY, 1.65f);
    public static readonly Vector3 BenchActionLocal = new Vector3(11.43f, WorkshopAnnex.FloorY, 1.65f);
    public static readonly Vector3 AssemblyLocal = new Vector3(9.35f, WorkshopAnnex.FloorY, 1.10f);

    public static WorkshopWorld Ensure(DepotLot lot)
    {
        if (lot == null || lot.interior == null) return null;
        var current = lot.GetComponent<WorkshopWorld>();
        if (current != null && current.ready) return current;
        var annex = WorkshopAnnex.Ensure(lot.interior.transform, lot.gameObject.scene, lot.lot);
        if (annex == null) return null;
        if (current == null) current = lot.gameObject.AddComponent<WorkshopWorld>();
        current.lot = lot.lot; current.depot = lot; current.Annex = annex;
        current.Initialize(); return current;
    }

    public static WorkshopWorld EnsureLegacy(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || scene.name != "ShopScene") return null; // coop:allow(scene) authored solo room only
        foreach (var world in worlds) if (world != null && world.lot == -1 && world.gameObject.scene == scene) return world;
        var annex = WorkshopAnnex.Ensure(null, scene, -1); if (annex == null) return null;
        var go = new GameObject("Workshop World"); SceneManager.MoveGameObjectToScene(go, scene);
        var component = go.AddComponent<WorkshopWorld>(); component.Annex = annex; component.Initialize(); return component;
    }

    void Initialize()
    {
        if (ready) return;
        ready = true;
        Bench = WorkshopModels.BuildBench(Annex.transform, DepotColors.Get(Mathf.Max(0, lot)));
        Bench.lot = lot; Bench.transform.localPosition = BenchLocal; Bench.transform.localRotation = Quaternion.Euler(0, -90f, 0);
        var interaction = Bench.gameObject.AddComponent<WorkshopBench>(); interaction.lot = lot;
        var zone = Bench.Anchor("Action").gameObject.AddComponent<InteractionZone>();
        zone.center = Vector3.up * .95f; zone.size = new Vector3(1.8f, 2.7f, 1.8f);
        zone.mode = ZoneMode.OnFoot; zone.score = 2.8f; zone.targetOverride = interaction;
        // One deliberate collider inside the bench's exact footprint, never one per tool or decorative part.
        var body = new GameObject("Workbench body") { layer = SortingKit.GroundLayer };
        body.transform.SetParent(Bench.transform, false);
        var collider = body.AddComponent<BoxCollider>(); collider.center = new Vector3(0, .5f, 0); collider.size = new Vector3(1.64f, 1f, .72f);

        projectLabel = WorkshopModels.MakeLabel(Bench.Anchor("ProjectLabel"), "Project status", "YOUR NEXT GOOD IDEA", 2.5f, new Color(.97f, .90f, .63f));
        projectLabel.transform.localRotation = Quaternion.Euler(0, 180f, 0);
        projectLabel.rectTransform.sizeDelta = new Vector2(1.65f, .23f); projectLabel.enableAutoSizing = true;
        projectLabel.fontSizeMin = .6f; projectLabel.fontSizeMax = 2.5f; projectLabel.richText = false;

        assembly = WorkshopModels.Node(Annex.transform, "Assembly bay", AssemblyLocal);
        Color timber = new Color(.47f, .35f, .19f), yellow = new Color(.91f, .68f, .24f);
        foreach (float x in new[] { -1.03f, 1.03f }) foreach (float z in new[] { -.68f, .68f })
        {
            WorkshopModels.BoxVisual(assembly, "Scaffold upright", new Vector3(x, 1.45f, z), new Vector3(.04f, 2.9f, .04f), timber);
            WorkshopModels.BoxVisual(assembly, "Scaffold foot", new Vector3(x, .015f, z), new Vector3(.17f, .03f, .17f), timber);
        }
        foreach (float z in new[] { -.68f, .68f })
        {
            WorkshopModels.BoxVisual(assembly, "Scaffold top", new Vector3(0, 2.88f, z), new Vector3(2.11f, .05f, .05f), timber);
            WorkshopModels.BoxVisual(assembly, "Assembly floor marking", new Vector3(0, .003f, z), new Vector3(2.1f, .005f, .035f), yellow);
        }
        assembly.gameObject.SetActive(false);
        if (depot != null) outside = WorkshopModels.Node(depot.transform, "Handmade street pieces", Vector3.zero);
        Refresh();
    }

    void OnEnable() { if (!worlds.Contains(this)) worlds.Add(this); Workshop.Changed += Refresh; }
    void OnDisable() { worlds.Remove(this); Workshop.Changed -= Refresh; }
    void OnDestroy() { worlds.Remove(this); if (outside != null) Destroy(outside.gameObject); }
    void Update() { if (ready && Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .75f; Refresh(); } }

    public static WorkshopModel Find(int lot, string recipeId)
    {
        foreach (var world in worlds)
            if (world != null && world.ready && world.lot == lot && world.models.TryGetValue(recipeId, out var model)) return model;
        return null;
    }

    public static bool TryWorkbenchPosition(int lot, out Vector3 position)
    {
        position = default;
        if (lot < -1 || lot >= DepotRowLayout.LotCount) return false;
        if (lot >= 0 && DepotRowScene.Lot(lot)?.interior == null) return false;
        if (lot == -1 && !DepotRowWorld.IsSceneLoaded("ShopScene")) return false; // coop:allow(scene) canonical legacy bench is only in the shop
        position = WorkbenchPosition(lot); return true;
    }
    public static Vector3 WorkbenchPosition(int lot) => WorkshopAnnex.Origin(lot) + BenchActionLocal;

    public void Refresh()
    {
        if (!ready) return;
        var state = Workshop.Snapshot(lot);
        bool have = Workshop.TryBusinessAt(lot, out var business);
        string key = have ? business.Id : "";
        if (key != businessKey)
        {
            foreach (var model in models.Values) Kill(model != null ? model.gameObject : null);
            models.Clear(); businessKey = key; signColor = -1;
        }
        if (outside != null) outside.gameObject.SetActive(have && depot != null && depot.facade != null && depot.facade.gameObject.activeSelf);
        string name = have ? business.Name : GameSession.BusinessName;
        Color accent = have ? DepotColors.Get(business.color) : DepotColors.Get(Mathf.Max(lot, 0));
        foreach (string id in WorkshopModels.RecipeIds)
        {
            bool built = state != null && state.Completed(id);
            if (models.TryGetValue(id, out var old) && (!built || id == "business_sign" && signColor != state.colorIndex))
            { Kill(old != null ? old.gameObject : null); models.Remove(id); }
            if (!built) continue;
            if (!models.TryGetValue(id, out var model) || model == null)
            {
                bool street = lot >= 0 && StreetRecipe(id);
                model = WorkshopModels.Build(id, street ? outside : Annex.transform, id == "business_sign" ? SignColor(state.colorIndex) : accent, name);
                model.lot = lot;
                Pose pose = PlacementFor(lot, id);
                model.transform.SetPositionAndRotation(pose.position, pose.rotation);
                models[id] = model;
                if (id == "business_sign") signColor = state.colorIndex;
                WorkshopGadget.Attach(model); WorkshopDisplay.Attach(model);
                ModelInstalled?.Invoke(lot, id, model);
            }
            model.SetBusinessName(name);
        }
        string nextProject = state != null ? state.activeRecipe ?? "" : "";
        if (nextProject != projectId)
        {
            Kill(Project != null ? Project.gameObject : null); Project = null; projectId = nextProject;
            if (!string.IsNullOrEmpty(projectId)) Project = WorkshopModels.Build(projectId, assembly, accent, name);
        }
        bool active = Project != null;
        assembly.gameObject.SetActive(active);
        if (active)
        {
            Project.lot = lot; Project.SetBusinessName(name);
            Project.SetProgress(state.Progress);
            projectLabel.text = WorkshopCatalog.Get(projectId).name.ToUpperInvariant() + "  " + Mathf.RoundToInt(state.Progress * 100f) + "%";
        }
        else projectLabel.text = "YOUR NEXT GOOD IDEA";
    }

    public static Color SignColor(int i)
    {
        if (i < 4) return DepotColors.Get(i);
        return i switch { 4 => new Color(.85f, .30f, .37f), 5 => new Color(.24f, .42f, .71f),
            6 => new Color(.72f, .60f, .31f), _ => new Color(.28f, .34f, .33f) };
    }

    public static bool StreetRecipe(string id) => id == "business_sign" || id == "junk_sculpture" || id == "sculpture_tower" || id == "sculpture_monument" || id == "doorbell";

    /// <summary>Pure canonical poses: authority never accepts a client-provided gadget or bench location.</summary>
    public static Pose PlacementFor(int lot, string id)
    {
        if (lot >= 0 && StreetRecipe(id))
        {
            float x = DepotRowLayout.LotCenterX(lot), z = DepotRowLayout.FacadeZ;
            Vector3 p = id switch {
                "business_sign" => new Vector3(x + 8.3f, 0, z + 2.2f),
                "doorbell" => new Vector3(x + 4.9f, 0, z + .35f),
                "junk_sculpture" => new Vector3(x - 9f, 0, z + 2.2f),
                "sculpture_tower" => new Vector3(x - 11f, 0, z + 3.6f),
                _ => new Vector3(x - 12.6f, 0, z + 6.1f) };
            return new Pose(p, Quaternion.identity);
        }
        float yaw = 0f;
        Vector3 at;
        switch (id)
        {
            case "bottle_cabinet": at = new Vector3(7.70f, 0, 3.19f); yaw = 180; break;
            case "rotating_pedestal": at = new Vector3(9.20f, 0, 3.13f); yaw = 180; break;
            case "grand_display": at = new Vector3(11.15f, 0, 3.17f); yaw = 180; break;
            case "wall_display": at = new Vector3(7.8f, 1.65f, 3.37f); yaw = 180; break;
            case "break_sofa": at = new Vector3(11.93f, 0, -4.05f); break;
            case "scrap_radio": at = new Vector3(10.52f, 0, -4.06f); break;
            case "bag_hoop": at = new Vector3(12.25f, 0, -.80f); yaw = -90; break;
            case "sorting_chute": at = new Vector3(7.55f, 0, -2.25f); yaw = 90; break;
            case "spring_launcher": at = new Vector3(9.15f, 0, -.80f); yaw = 90; break;
            case "business_sign": at = new Vector3(9f, 0, -4.04f); break;
            case "junk_sculpture": at = new Vector3(7.23f, 0, 1.91f); break;
            case "sculpture_tower": at = new Vector3(7.23f, 0, -3.82f); break;
            case "sculpture_monument": at = new Vector3(9f, 0, -2.95f); break;
            case "doorbell": at = new Vector3(7.09f, 0, -1.73f); yaw = 90; break;
            default: at = Vector3.zero; break;
        }
        at.y += WorkshopAnnex.FloorY;
        return new Pose(WorkshopAnnex.Origin(lot) + at, Quaternion.Euler(0, yaw, 0));
    }

    static void Kill(GameObject go) { if (go == null) return; go.SetActive(false); if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { worlds.Clear(); ModelInstalled = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (!Application.isPlaying) return;
        var go = new GameObject("Workshop world installer"); DontDestroyOnLoad(go); go.AddComponent<WorkshopWorldInstaller>();
    }
}

public sealed class WorkshopBench : MonoBehaviour, IInteractable
{
    public int lot = -1;
    public bool CanInteract() => isActiveAndEnabled && !PlayerController.IsUIBlocking;
    public string GetInteractionPrompt() => Workshop.CanDonate(lot) ? "Open workbench — save materials and build" : "Look at this business's workshop";
    public void OnInteract(GameObject interactor) { if (CanInteract()) WorkshopMenu.Show(lot); }
    public void Highlight(bool on) { }
}

sealed class WorkshopWorldInstaller : MonoBehaviour
{
    float next;
    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + 1f;
        foreach (var lot in FindObjectsByType<DepotLot>(FindObjectsInactive.Include, FindObjectsSortMode.None)) WorkshopWorld.Ensure(lot);
        if (!DepotRowWorld.Active) WorkshopWorld.EnsureLegacy(SceneManager.GetSceneByName("ShopScene")); // coop:allow(scene) additive pockets use their own per-lot path
    }
}

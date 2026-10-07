using UnityEngine;

/// <summary>Read-only copies of owned keepsakes. A display is never another pickable/sellable item.</summary>
public sealed class WorkshopDisplay : MonoBehaviour
{
    WorkshopModel model;
    GameObject shown;
    string signature;
    bool pet;
    Vector3 shownRest;
    Quaternion shownRotation;
    public static void Attach(WorkshopModel m)
    {
        if (m == null || !(m.recipeId == "bottle_cabinet" || m.recipeId == "rotating_pedestal" ||
            m.recipeId == "wall_display" || m.recipeId == "grand_display") || m.GetComponent<WorkshopDisplay>() != null) return;
        m.gameObject.AddComponent<WorkshopDisplay>().model = m;
    }
    void OnEnable() { Workshop.Changed += Refresh; Refresh(); }
    void OnDisable() => Workshop.Changed -= Refresh;
    void Start() => Refresh();
    void Refresh()
    {
        if (model == null) return;
        var item = Workshop.Snapshot(model.lot)?.Display(model.recipeId);
        string want = item != null ? item.uid + "/" + item.defId : "";
        if (signature == want) return; signature = want;
        if (shown != null) { shown.SetActive(false); Destroy(shown); shown = null; }
        if (item?.Def == null) return;
        var mount = model.Anchor("DisplaySlot0") ?? model.transform;
        // Centre inside a wrapper: spinning/asymmetric finds rotate on the shelf, not around their model's import pivot.
        shown = new GameObject("Workshop find " + item.Def.name);
        shown.transform.SetParent(mount, false);
        var visual = ItemVisuals.Build(item.Def, shown.transform);
        var b = SortingKit.LocalRendererBounds(visual.transform);
        pet = item.defId == RaccoonTrap.PetDefId;
        float scale = FitScale(model.recipeId, b.size, pet);
        visual.transform.localScale = Vector3.one * scale;
        visual.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z) * scale;
        shownRest = shown.transform.localPosition;
        shownRotation = shown.transform.localRotation;
    }

    /// <summary>Clear volume above DisplaySlot0, allowing for each handmade prop's back, shelf, columns and crown.</summary>
    public static Vector3 AvailableVolume(string recipe) => recipe switch
    {
        "bottle_cabinet" => new Vector3(.82f, .50f, .33f),
        "wall_display" => new Vector3(.80f, .50f, .25f),
        "grand_display" => new Vector3(.95f, .90f, .34f),
        _ => new Vector3(.45f, .80f, .45f)
    };

    public static float FitScale(string recipe, Vector3 size, bool isPet = false)
    {
        var volume = AvailableVolume(recipe);
        Vector3 envelope = size;
        if (recipe == "rotating_pedestal")
        {
            // A rectangular find's corners must stay on the round platter at every rotation, not only at spawn.
            float diameter = new Vector2(size.x, size.z).magnitude;
            envelope.x = envelope.z = diameter;
        }
        else if (isPet)
        {
            // Conservative envelope for the gentle ±16° look and small breathing tilt below.
            envelope = new Vector3(size.x + size.z * .28f + size.y * .035f,
                size.y + size.z * .027f + size.x * .035f, size.z + size.x * .28f + size.y * .027f);
        }
        if (isPet) volume.y -= .016f;
        float cap = recipe == "grand_display" ? .85f : .42f;
        return Mathf.Min(cap / Mathf.Max(.01f, Mathf.Max(size.x, size.y, size.z)),
            volume.x / Mathf.Max(.01f, envelope.x), volume.y / Mathf.Max(.01f, envelope.y),
            volume.z / Mathf.Max(.01f, envelope.z));
    }
    void Update()
    {
        if (shown == null) return;
        if (pet)
        {
            float t = Time.time, breathe = Mathf.Sin(t * 2.8f);
            float look = 13f * Mathf.Sin(t * .65f) + 3f * Mathf.Sin(t * 1.35f);
            shown.transform.localRotation = shownRotation * Quaternion.Euler(1.5f * breathe, look, 2f * Mathf.Sin(t * 1.1f));
            shown.transform.localPosition = shownRest + Vector3.up * (.006f * (1f + breathe));
        }
        else if (model.recipeId == "rotating_pedestal") shown.transform.Rotate(Vector3.up, 22f * Time.deltaTime, Space.Self);
    }
}

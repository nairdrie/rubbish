using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// One display in the garage (Docs/Progression.md §6.8): the plank shelf, the trophy shelf, the museum wing, the lit
/// glass case, the pedestal of honor or the Legendary Vault. <see cref="DisplayGallery"/> hands it the finds to show
/// (DisplayPlanner decides; this only presents): each slot gets a renderers-only prop (ItemVisuals.Build) and a little
/// brass plate. New arrivals pop in with a dust puff and a rarity sparkle; the plank wobbles, cases glow brighter,
/// props turn slowly. Built by Setup_Displays; shown by GarageUpgradeVisuals when its upgrade is owned.
/// Also the "Arrange" interactable (E while looking at a slot from inside its InteractionZone).
/// </summary>
public class DisplayPiece : MonoBehaviour, IInteractable
{
    [Tooltip("DisplayPlanner piece key (pedestal, vault, glass_case, trophy_shelf, museum_wing, plank_shelf).")]
    public string pieceKey;
    [Tooltip("Stand points: a find's bottom sits on the slot; slot.forward points at the viewer (plates go in front).")]
    public Transform[] slots = new Transform[0];
    public DisplayGallery gallery;
    [Tooltip("Largest prop size (m) on a slot.")]
    public float maxPropSize = 0.32f;
    [Tooltip("Showroom spin (deg/s); 0 = still.")]
    public float spin = 25f;
    [Tooltip("Gentle bob (m), e.g. the pedestal's find floats a little.")]
    public float bob;
    [Tooltip("Optional: the plank. Sways a little and wobbles hard when a find lands on it (slots are its children).")]
    public Transform wobble;
    public float wobbleIdleDegrees = 0.8f;
    [Tooltip("Optional lights that flare when a find arrives (case lights, the spotlight, the vault glow).")]
    public Light[] lights = new Light[0];
    [Tooltip("Optional: the vault door; it wiggles proudly when something arrives.")]
    public Transform door;
    [Tooltip("Small brass name plate under each find.")]
    public bool plates = true;
    [Tooltip("Plate position in slot space.")]
    public Vector3 plateOffset = new Vector3(0f, -0.035f, 0.17f);
    public Vector2 plateSize = new Vector2(0.34f, 0.07f);
    [Tooltip("How close (m) the camera's aim must pass a slot to look at it.")]
    public float gazeRadius = 0.22f;

    ItemInstance[] shown = new ItemInstance[0];
    GameObject[] props = new GameObject[0];
    GameObject[] plateObjs = new GameObject[0];
    Vector3[] propBase = new Vector3[0];
    Quaternion[] propRest = new Quaternion[0];
    Vector3[] propCenter = new Vector3[0];   // visual centre in the prop's own space (gaze target, no per-frame bounds)
    float[] baseIntensity;
    float wobbleKick, wobbleKickT = -10f, flareT = -10f, doorT = -10f;
    Quaternion wobbleRest, doorRest;
    bool restCached;

    public DisplayPieceDef Def => DisplayPlanner.Get(pieceKey);
    public int SlotCount => slots != null ? slots.Length : 0;
    public ItemInstance ShownAt(int i) => i >= 0 && i < shown.Length ? shown[i] : null;

    void Awake() => CacheRest();

    void CacheRest()
    {
        if (restCached) return;
        restCached = true;
        if (wobble != null) wobbleRest = wobble.localRotation;
        if (door != null) doorRest = door.localRotation;
        baseIntensity = new float[lights != null ? lights.Length : 0];
        for (int i = 0; i < baseIntensity.Length; i++) baseIntensity[i] = lights[i] != null ? lights[i].intensity : 0f;
    }

    void OnEnable()
    {
        if (gallery != null) gallery.PieceEnabled(this);
    }

    void EnsureArrays()
    {
        int n = SlotCount;
        if (shown.Length == n) return;
        System.Array.Resize(ref shown, n);
        System.Array.Resize(ref props, n);
        System.Array.Resize(ref plateObjs, n);
        System.Array.Resize(ref propBase, n);
        System.Array.Resize(ref propRest, n);
        System.Array.Resize(ref propCenter, n);
    }

    // ------------------------------------------------------------------ presenting

    /// <summary>
    /// Show these finds (the planner's slots; null = empty). Pinned slots (Arrange) show exactly their find. Other finds that
    /// are already on this piece keep their spot, so a new best find doesn't reshuffle the whole wall; newcomers take the free
    /// slots in order. Only slots whose find changed are rebuilt. <paramref name="celebrate"/> = juice the arrivals (play mode
    /// only). Returns the finds that just arrived.
    /// </summary>
    public List<ItemInstance> Show(ItemInstance[] items, bool[] pinned, bool celebrate)
    {
        CacheRest();
        EnsureArrays();
        int n = SlotCount;
        var target = new ItemInstance[n];
        var placed = new HashSet<string>();
        bool Pinned(int i) => pinned != null && i < pinned.Length && pinned[i];
        ItemInstance Want(int i) => items != null && i < items.Length ? items[i] : null;
        for (int i = 0; i < n; i++)
            if (Pinned(i) && Want(i) != null) { target[i] = Want(i); placed.Add(Want(i).uid); }
        var wanted = new HashSet<string>();
        for (int i = 0; i < n; i++) { var w = Want(i); if (w != null && !placed.Contains(w.uid)) wanted.Add(w.uid); }
        for (int i = 0; i < n; i++)   // stay put
        {
            if (Pinned(i) || target[i] != null) continue;
            var have = shown[i];
            if (have != null && wanted.Contains(have.uid) && placed.Add(have.uid))
                for (int k = 0; k < n; k++) { var w = Want(k); if (w != null && w.uid == have.uid) { target[i] = w; break; } }
        }
        int free = 0;
        for (int i = 0; i < n; i++)   // newcomers, best first, into the free slots
        {
            var w = Want(i);
            if (w == null || placed.Contains(w.uid)) continue;
            while (free < n && (target[free] != null || Pinned(free))) free++;
            if (free >= n) break;
            target[free] = w;
            placed.Add(w.uid);
        }
        return Present(target, celebrate);
    }

    /// <summary>Show these finds with no pinned slots (finds already here still keep their spots).</summary>
    public List<ItemInstance> Show(ItemInstance[] items, bool celebrate) => Show(items, null, celebrate);

    List<ItemInstance> Present(ItemInstance[] items, bool celebrate)
    {
        var arrived = new List<ItemInstance>();
        bool juice = celebrate && Application.isPlaying && isActiveAndEnabled;
        for (int i = 0; i < SlotCount; i++)
        {
            var want = items != null && i < items.Length ? items[i] : null;
            var have = shown[i];
            if ((want == null && have == null) || (want != null && have != null && want.uid == have.uid)) continue;
            var slot = slots[i];
            if (props[i] != null)
            {
                if (juice)
                {
                    Fx.Dust(props[i].transform.position, 0.35f);
                    Juice.ShrinkAndDestroy(props[i], 0.18f);
                }
                else Kill(props[i]);
                props[i] = null;
            }
            if (plateObjs[i] != null) { Kill(plateObjs[i]); plateObjs[i] = null; }
            shown[i] = want;
            if (want == null || want.Def == null || slot == null) continue;

            var go = new GameObject("Find " + want.Def.name);
            go.layer = slot.gameObject.layer;
            go.transform.SetParent(slot, false);
            var vis = ItemVisuals.Build(want.Def, go.transform);
            propCenter[i] = Fit(go.transform, vis);
            props[i] = go;
            propBase[i] = go.transform.localPosition;
            propRest[i] = go.transform.localRotation;
            if (plates) plateObjs[i] = Plate(slot, want);
            arrived.Add(want);
            if (juice)
            {
                Juice.PopIn(go.transform, 0.3f);
                Fx.Dust(slot.position + Vector3.up * 0.05f, 0.45f);
                var rarity = want.Def.rarity;
                Fx.Sparkle(go.transform, RarityInfo.Color(rarity), 1.2f + 0.5f * (int)rarity);
                Sfx.Play("pop", slot.position, 0.7f, 1.1f);
                if (rarity >= Rarity.Rare) Sfx.Play("sparkle", slot.position, 0.5f + 0.1f * (int)rarity);
            }
        }
        if (juice && arrived.Count > 0)
        {
            wobbleKick = 7f;
            wobbleKickT = Time.time;
            flareT = Time.time;
            if (door != null) { doorT = Time.time; Sfx.Play("squeak", door.position, 0.6f, 0.7f); }
        }
        return arrived;
    }

    /// <summary>Remove every prop (setup / previews).</summary>
    public void Clear() => Show(new ItemInstance[SlotCount], false);

    /// <summary>Scale and stand the prop on its slot; returns its visual centre in its own space.</summary>
    Vector3 Fit(Transform root, GameObject vis)
    {
        if (vis == null) return Vector3.zero;
        // Measured in the prop's own space, so a piece that is still popping in (scaled) doesn't skew the size.
        var b = SortingKit.LocalRendererBounds(root);
        float size = Mathf.Max(0.01f, Mathf.Max(b.size.x, b.size.y, b.size.z));
        float k = Mathf.Min(1.6f, maxPropSize / size);
        root.localScale = Vector3.one * k;
        // Stand it on the slot (the prop sits at the slot's origin, slot-up = up; a wobbling plank carries it along).
        root.localPosition = new Vector3(0f, -b.min.y * k + 0.005f, 0f);
        return b.center;
    }

    GameObject Plate(Transform slot, ItemInstance it)
    {
        var holder = new GameObject("Plate");
        holder.layer = slot.gameObject.layer;
        holder.transform.SetParent(slot, false);
        holder.transform.localPosition = plateOffset;
        holder.transform.localRotation = Quaternion.identity;
        // Brass backing (renderer only), tipped back a little like a museum label.
        holder.transform.localRotation = Quaternion.Euler(-20f, 0f, 0f);
        var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
        back.name = "Brass";
        Kill(back.GetComponent<Collider>());
        back.layer = holder.layer;
        back.transform.SetParent(holder.transform, false);
        back.transform.localScale = new Vector3(plateSize.x, plateSize.y, 0.006f);
        var br = back.GetComponent<Renderer>();
        br.sharedMaterial = SortingKit.RuntimeMaterial("mat/display_brass", new Color(0.78f, 0.6f, 0.25f));
        br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        // slot.forward points at the viewer and TMP is read looking along its +Z, so the text turns around (180°)
        // and sits just in front of the brass.
        var tmp = SortingKit.MakeText(holder.transform, "Name", it.Def.name, 0.3f, new Color(0.16f, 0.09f, 0.02f));
        tmp.transform.localPosition = new Vector3(0f, 0f, 0.0045f);
        tmp.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        tmp.outlineWidth = 0f;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.05f;
        tmp.fontSizeMax = 0.3f;
        tmp.rectTransform.sizeDelta = new Vector2(plateSize.x * 0.92f, plateSize.y * 0.8f);
        var mr = tmp.GetComponent<MeshRenderer>();
        if (mr != null) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return holder;
    }

    static void Kill(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    /// <summary>World centre of a slot's find (or of the empty slot, a bit above it).</summary>
    public Vector3 SlotCenter(int i)
    {
        if (i < 0 || i >= SlotCount || slots[i] == null) return transform.position;
        if (i < props.Length && props[i] != null) return props[i].transform.TransformPoint(propCenter[i]);
        return slots[i].position + slots[i].up * (maxPropSize * 0.5f);
    }

    /// <summary>Juice when the gallery says a Legendary landed here.</summary>
    public void Legendary()
    {
        flareT = Time.time + 0.3f;
        wobbleKick = 12f;
        wobbleKickT = Time.time;
        if (door != null) doorT = Time.time;
    }

    void Update()
    {
        float t = Time.time, dt = Time.deltaTime;
        AnimateProps(t, dt);
        if (wobble != null)
        {
            float since = t - wobbleKickT;
            float kick = since < 3f ? wobbleKick * Mathf.Exp(-since * 2.2f) * Mathf.Sin(since * 14f) : 0f;
            float idle = wobbleIdleDegrees * Mathf.Sin(t * 1.3f) + 0.4f * wobbleIdleDegrees * Mathf.Sin(t * 3.1f + 1f);
            wobble.localRotation = wobbleRest * Quaternion.Euler(0f, 0f, idle + kick);
        }
        if (baseIntensity != null && lights != null)
        {
            float since = t - flareT;
            float k = since >= 0f && since < 1.6f ? 1f + 2.2f * Mathf.Exp(-since * 2.5f) : 1f;
            for (int i = 0; i < lights.Length && i < baseIntensity.Length; i++)
                if (lights[i] != null) lights[i].intensity = baseIntensity[i] * k * (1f + 0.04f * Mathf.Sin(t * 2f + i));
        }
        if (door != null)
        {
            float since = t - doorT;
            float a = since < 2f ? 9f * Mathf.Exp(-since * 2.4f) * Mathf.Sin(since * 11f) : 0f;
            door.localRotation = doorRest * Quaternion.Euler(0f, a, 0f);
        }
    }

    // Separate from Update so the isolated visual preview can sample two exact poses without entering Play Mode.
    void AnimateProps(float t, float dt)
    {
        for (int i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (p == null) continue;
            // The kept raccoon is a shop pet, not a rotating museum piece. Move only the display wrapper:
            // ItemVisuals.Build must keep its renderers-only model contract for loose items and previews.
            if (i < shown.Length && shown[i] != null && shown[i].defId == RaccoonTrap.PetDefId)
            {
                float look = 13f * Mathf.Sin(t * 0.65f + i * 1.7f) + 3f * Mathf.Sin(t * 1.35f + i);
                float breathe = Mathf.Sin(t * 2.8f + i * 0.7f);
                p.transform.localRotation = propRest[i] * Quaternion.Euler(1.5f * breathe, look, 2f * Mathf.Sin(t * 1.1f + i));
                p.transform.localPosition = propBase[i] + Vector3.up * (0.006f * (1f + breathe));
                continue;
            }
            if (spin != 0f) p.transform.Rotate(Vector3.up, spin * dt, Space.World);
            if (bob > 0f) p.transform.localPosition = propBase[i] + Vector3.up * (bob * Mathf.Sin(t * 1.6f + i));
        }
    }

    // ------------------------------------------------------------------ Arrange (IInteractable via InteractionZone)

    public bool CanInteract() => gallery != null && gallery.CanArrange(this);

    public string GetInteractionPrompt() => gallery != null ? gallery.ArrangePrompt(this) : "Arrange";

    public void OnInteract(GameObject interactor)
    {
        if (!Permissions.LocalCanManageAt(transform.position)) { Permissions.Deny(transform.position, Permissions.LocalCanWorkAt(transform.position) ? "Only the owner rearranges the shelves." : Permissions.NotYourShop); return; }
        if (gallery != null) gallery.Arrange(this);
    }

    public void Highlight(bool highlight) { }   // the gaze placard is the highlight
}

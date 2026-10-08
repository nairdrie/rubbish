using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The Critter Hutch you can see and walk up to: one per business that printed it, on every machine, showing the actual
/// residents. It stands street-side on the owner's lot at a canonical pose (clear of the sign, the doorbell, the Monument
/// and every truck / spawn pose listed in WorkshopWorld and DepotRowLayout); in the solo flow that is the lot standing in
/// for the garage (<see cref="DepotFlowRules.LegacyShopLot"/>). Presentation of the replicated roster only: the model,
/// the dolls and the "open the hutch" prompt. Nothing here decides anything.
/// </summary>
public sealed class HutchWorld : MonoBehaviour
{
    /// <summary>Door-local street slot (x across the shop front, z toward the road): beyond the business sign's corner.</summary>
    public static readonly Vector3 StreetLocal = new Vector3(11.6f, 0f, 2.6f);
    public const float NearRadius = 12f;

    static readonly List<HutchWorld> all = new List<HutchWorld>();
    public static IReadOnlyList<HutchWorld> All => all;

    public int lot = -1;
    Transform model, dolls;
    TMP_Text sign;
    string shownModel, shownDolls;
    float nextRefresh;
    WorkshopWorld world;

    /// <summary>The hutch's canonical pose for a lot (authority never accepts a client-provided location).</summary>
    public static Pose PoseFor(int lot) => DepotRowLayout.StreetPose(lot >= 0 ? lot : DepotFlowRules.LegacyShopLot, StreetLocal, 0f);

    /// <summary>Host rule: is this client's player standing at the hutch of <paramref name="lot"/>?</summary>
    public static bool IsNear(ulong client, int lot)
    {
        var pc = NightPlayers.ForClient(client);
        if (pc == null || pc.IsDriving) return false;
        if (!DepotRowWorld.Active) lot = -1; // solo: the business's lot number is only a formality, the hutch is outside the legacy shop
        // the solo garage is its own scene: street coordinates mean nothing in there
        if (lot < 0 && (DepotRowWorld.Active || NightPlayers.IsIndoors(pc))) return false;
        return (pc.transform.position - PoseFor(lot).position).sqrMagnitude <= NearRadius * NearRadius;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    /// <summary>Called about once a second: every street presenter gets its hutch object (it shows itself only when printed).</summary>
    internal static void EnsureAll()
    {
        var worlds = WorkshopWorld.All;
        for (int i = 0; i < worlds.Count; i++)
        {
            var w = worlds[i];
            if (w == null || !w.Street || w.lot < -1) continue;
            bool have = false;
            for (int k = 0; k < all.Count; k++) if (all[k] != null && all[k].world == w) { have = true; break; }
            if (have) continue;
            var go = new GameObject("Critter Hutch");
            go.transform.SetParent(w.transform, false);
            var pose = PoseFor(w.lot);
            go.transform.SetPositionAndRotation(pose.position, pose.rotation);
            var hutch = go.AddComponent<HutchWorld>();
            hutch.lot = w.lot; hutch.world = w;
        }
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    void Update()
    {
        if (world == null) { Destroy(gameObject); return; }
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.75f;
        Refresh();
    }

    void Refresh()
    {
        bool have = Workshop.TryBusinessAt(lot, out var business);
        int level = have ? Hutch.LevelFor(business.ownerClientId, CallingIds.CritterHutch) : 0;
        if (have)
        {
            var depot = world.GetComponent<DepotLot>();
            if (depot != null && depot.facade != null && !depot.facade.gameObject.activeSelf) level = 0;
        }
        if (level <= 0)
        {
            if (model != null) { Destroy(model.gameObject); model = null; dolls = null; shownModel = shownDolls = null; }
            return;
        }
        var hutch = Hutch.For(business.Id);
        ulong owner = business.ownerClientId;
        bool box = Hutch.LevelFor(owner, CallingIds.BreedingBox) > 0;
        int collar = Hutch.LevelFor(owner, CallingIds.CollarChip), brain = Hutch.LevelFor(owner, CallingIds.BrainChip);
        string modelKey = $"{business.Id}/{level}/{(box ? 1 : 0)}/{hutch.gnomes}/{(hutch.collars > 0 ? 1 : 0)}/{business.Name}";
        if (modelKey != shownModel)
        {
            if (model != null) Destroy(model.gameObject);
            shownModel = modelKey; shownDolls = null;
            BuildModel(level, box, hutch.gnomes, hutch.collars > 0, DepotColors.Get(business.color), business.Name);
        }
        bool out_ = collar > 0 && GameClock.IsNight;
        string dollKey = hutch.Encode() + (out_ ? "/night" : "/day") + collar + brain;
        if (dollKey == shownDolls) return;
        shownDolls = dollKey;
        BuildDolls(hutch, level, box, collar, brain, out_);
    }

    // ------------------------------------------------------------------ the model (primitives, CritterLook style)

    const float FloorTop = 0.43f, Storey = 0.74f, Width = 1.9f, Depth = 0.9f, BoxX = 0.52f;

    void BuildModel(int level, bool breedingBox, int gnomes, bool kingsCollar, Color accent, string businessName)
    {
        model = CritterLook.Node(transform, "Model", Vector3.zero);
        var wood = CritterLook.Mat(new Color(0.55f, 0.39f, 0.22f), 0.2f);
        var woodDark = CritterLook.Mat(new Color(0.38f, 0.27f, 0.16f), 0.2f);
        var roofM = CritterLook.Mat(new Color(0.42f, 0.26f, 0.58f), 0.35f);
        var wire = CritterLook.Mat(new Color(0.62f, 0.64f, 0.68f), 0.75f);
        var accentM = CritterLook.Mat(accent, 0.45f);
        int storeys = level >= 2 ? 2 : 1;
        float wall = storeys * Storey, top = FloorTop + wall;

        foreach (float x in new[] { -0.85f, 0.85f }) foreach (float z in new[] { -0.35f, 0.35f })
            CritterLook.Part(model, PrimitiveType.Cube, "Leg", new Vector3(x, 0.19f, z), new Vector3(0.09f, 0.38f, 0.09f), woodDark);
        CritterLook.Part(model, PrimitiveType.Cube, "Floor", new Vector3(0f, FloorTop - 0.04f, 0f), new Vector3(Width, 0.08f, Depth), wood);
        CritterLook.Part(model, PrimitiveType.Cube, "Back", new Vector3(0f, FloorTop + wall * 0.5f, -Depth * 0.5f + 0.03f), new Vector3(Width, wall, 0.06f), wood);
        foreach (float x in new[] { -Width * 0.5f + 0.03f, Width * 0.5f - 0.03f })
            CritterLook.Part(model, PrimitiveType.Cube, "Side", new Vector3(x, FloorTop + wall * 0.5f, 0f), new Vector3(0.06f, wall, Depth), wood);
        if (storeys == 2)
        {
            CritterLook.Part(model, PrimitiveType.Cube, "Upstairs", new Vector3(0f, FloorTop + Storey - 0.025f, 0f), new Vector3(Width, 0.05f, Depth), woodDark);
            CritterLook.Part(model, PrimitiveType.Cube, "Ladder", new Vector3(0.78f, FloorTop + Storey * 0.5f, 0.2f), new Vector3(0.05f, Storey * 1.1f, 0.14f), woodDark, new Vector3(28f, 0f, 0f));
        }
        CritterLook.Part(model, PrimitiveType.Cube, "Roof", new Vector3(0f, top + 0.08f, 0.02f), new Vector3(Width + 0.3f, 0.09f, Depth + 0.3f), roofM, new Vector3(-7f, 0f, 0f));
        CritterLook.Part(model, PrimitiveType.Cube, "Roof trim", new Vector3(0f, top + 0.02f, Depth * 0.5f + 0.12f), new Vector3(Width + 0.3f, 0.07f, 0.05f), accentM, new Vector3(-7f, 0f, 0f));
        // chicken-wire front: bars with a doorway on the left of the ground floor
        for (int s = 0; s < storeys; s++)
            for (int i = 0; i <= 9; i++)
            {
                float x = -Width * 0.5f + 0.06f + i * ((Width - 0.12f) / 9f);
                if (s == 0 && i >= 1 && i <= 2) continue;
                CritterLook.Part(model, PrimitiveType.Cube, "Wire", new Vector3(x, FloorTop + s * Storey + Storey * 0.5f, Depth * 0.5f - 0.02f),
                    new Vector3(0.018f, Storey - 0.04f, 0.018f), wire, default, false);
            }
        CritterLook.Part(model, PrimitiveType.Cube, "Ramp", new Vector3(-0.58f, 0.21f, Depth * 0.5f + 0.32f), new Vector3(0.36f, 0.04f, 0.78f), wood, new Vector3(32f, 0f, 0f));
        // food bowl with kibble
        CritterLook.Part(model, PrimitiveType.Cylinder, "Bowl", new Vector3(0.55f, 0.05f, Depth * 0.5f + 0.38f), new Vector3(0.34f, 0.05f, 0.34f), accentM);
        for (int i = 0; i < 5; i++)
            CritterLook.Part(model, PrimitiveType.Sphere, "Kibble", new Vector3(0.55f + Mathf.Cos(i * 1.3f) * 0.07f, 0.11f, Depth * 0.5f + 0.38f + Mathf.Sin(i * 1.3f) * 0.07f),
                Vector3.one * 0.06f, CritterLook.Mat(new Color(0.5f, 0.3f, 0.14f), 0.2f), default, false);

        // the sign over the roof edge
        var plank = CritterLook.Part(model, PrimitiveType.Cube, "Sign", new Vector3(0f, top + 0.33f, Depth * 0.5f + 0.02f), new Vector3(1.5f, 0.3f, 0.05f), woodDark);
        foreach (float x in new[] { -0.6f, 0.6f })
            CritterLook.Part(model, PrimitiveType.Cube, "Sign post", new Vector3(x, top + 0.16f, Depth * 0.5f), new Vector3(0.05f, 0.24f, 0.05f), woodDark);
        sign = WorkshopModels.MakeLabel(model, "Hutch sign", level >= 2 ? "CRITTER CONDO" : "CRITTER HUTCH", 1.55f, new Color(1f, 0.92f, 0.6f));
        sign.transform.localPosition = plank.localPosition + new Vector3(0f, 0f, 0.03f);
        sign.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        sign.rectTransform.sizeDelta = new Vector2(1.42f, 0.26f);
        sign.enableAutoSizing = true; sign.fontSizeMin = 0.5f; sign.fontSizeMax = 1.55f;

        if (breedingBox)
        {
            // the penthouse: on the roof, so the hutch keeps one footprint
            var box = CritterLook.Node(model, "Breeding Box", new Vector3(BoxX, top + 0.12f, -0.05f), new Vector3(-7f, 0f, 0f));
            CritterLook.Part(box, PrimitiveType.Cube, "Crate", new Vector3(0f, 0.3f, 0f), new Vector3(0.66f, 0.6f, 0.62f), wood);
            CritterLook.Part(box, PrimitiveType.Cube, "Door", new Vector3(0f, 0.22f, 0.3f), new Vector3(0.3f, 0.36f, 0.04f), CritterLook.Mat(new Color(0.08f, 0.06f, 0.07f), 0.2f));
            CritterLook.Part(box, PrimitiveType.Cube, "Lid", new Vector3(0f, 0.63f, 0.02f), new Vector3(0.74f, 0.06f, 0.72f), roofM, new Vector3(-6f, 0f, 0f));
            var red = CritterLook.Mat(new Color(0.95f, 0.25f, 0.4f), 0.6f, 0.3f);
            CritterLook.Part(box, PrimitiveType.Sphere, "Heart L", new Vector3(-0.045f, 0.5f, 0.32f), new Vector3(0.11f, 0.11f, 0.04f), red, default, false);
            CritterLook.Part(box, PrimitiveType.Sphere, "Heart R", new Vector3(0.045f, 0.5f, 0.32f), new Vector3(0.11f, 0.11f, 0.04f), red, default, false);
            CritterLook.Part(box, PrimitiveType.Cube, "Heart tip", new Vector3(0f, 0.45f, 0.32f), new Vector3(0.1f, 0.1f, 0.035f), red, new Vector3(0f, 0f, 45f), false);
        }
        for (int i = 0; i < gnomes; i++)
        {
            var g = CritterLook.Node(model, "Stolen gnome", new Vector3(-0.78f + i * 0.3f, top + 0.13f, -0.2f), new Vector3(-7f, 20f * i - 20f, 0f));
            CritterLook.Part(g, PrimitiveType.Capsule, "Body", new Vector3(0f, 0.09f, 0f), new Vector3(0.12f, 0.09f, 0.12f), CritterLook.Mat(new Color(0.2f, 0.4f, 0.8f), 0.4f));
            CritterLook.Part(g, PrimitiveType.Sphere, "Head", new Vector3(0f, 0.2f, 0f), Vector3.one * 0.1f, CritterLook.Mat(new Color(0.95f, 0.76f, 0.62f), 0.3f), default, false);
            CritterLook.Part(g, PrimitiveType.Sphere, "Beard", new Vector3(0f, 0.16f, 0.04f), new Vector3(0.09f, 0.1f, 0.05f), CritterLook.Mat(Color.white, 0.2f), default, false);
            CritterLook.Part(g, PrimitiveType.Capsule, "Hat", new Vector3(0f, 0.3f, 0f), new Vector3(0.09f, 0.1f, 0.09f), CritterLook.Mat(new Color(0.9f, 0.15f, 0.15f), 0.4f), default, false);
        }
        if (kingsCollar)
        {
            var gold = CritterLook.Mat(new Color(1f, 0.8f, 0.2f), 0.85f, 0.5f);
            var hook = CritterLook.Node(model, "King's Collar", new Vector3(0.88f, top + 0.3f, Depth * 0.5f + 0.06f));
            CritterLook.Part(hook, PrimitiveType.Cylinder, "Ring", Vector3.zero, new Vector3(0.2f, 0.015f, 0.2f), gold, new Vector3(90f, 0f, 0f), false);
            CritterLook.Part(hook, PrimitiveType.Cylinder, "Hole", new Vector3(0f, 0f, 0.004f), new Vector3(0.13f, 0.015f, 0.13f), woodDark, new Vector3(90f, 0f, 0f), false);
            CritterLook.Part(hook, PrimitiveType.Sphere, "Gem", new Vector3(0f, -0.1f, 0.012f), Vector3.one * 0.055f, CritterLook.Mat(new Color(0.9f, 0.1f, 0.25f), 0.9f, 0.9f), default, false);
        }

        dolls = CritterLook.Node(model, "Residents", Vector3.zero);
        if (!Application.isPlaying) return; // edit-mode preview: looks only
        // one solid box, and the prompt
        var solid = new GameObject("Hutch body") { layer = CritterWorld.DefaultLayer };
        solid.transform.SetParent(model, false);
        var col = solid.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, top * 0.5f, 0f); col.size = new Vector3(Width, top, Depth);
        var zoneGo = new GameObject("Hutch zone");
        zoneGo.transform.SetParent(model, false);
        var door = zoneGo.AddComponent<HutchDoor>(); door.hutch = this;
        var zone = zoneGo.AddComponent<InteractionZone>();
        zone.center = new Vector3(0f, 1f, 0.9f); zone.size = new Vector3(3.4f, 2.6f, 2.8f);
        zone.mode = ZoneMode.OnFoot; zone.score = 2.7f; zone.targetOverride = door;
        Juice.PopIn(model, 0.3f);
    }

    /// <summary>Edit-mode preview for screenshots: the model and its residents for a sample roster, with no prompt, no
    /// collider and no live business behind it.</summary>
    public static HutchWorld CreatePreview(Transform parent, HutchState hutch, int level, bool breedingBox, int collar, int brain, Color accent, bool night)
    {
        var go = new GameObject("Critter Hutch (preview)");
        go.transform.SetParent(parent, false);
        var preview = go.AddComponent<HutchWorld>();
        preview.BuildModel(level, breedingBox, hutch.gnomes, hutch.collars > 0, accent, "Preview");
        preview.BuildDolls(hutch, level, breedingBox, collar, brain, night && collar > 0);
        return preview;
    }

    void BuildDolls(HutchState hutch, int level, bool breedingBox, int collar, int brain, bool crewOut)
    {
        if (dolls == null) return;
        for (int i = dolls.childCount - 1; i >= 0; i--) Destroy(dolls.GetChild(i).gameObject);
        int slot = 0, boxed = 0;
        int slots = level >= 2 ? 6 : 3;
        float lidY = FloorTop + (level >= 2 ? 2 : 1) * Storey + 0.12f + 0.66f;
        foreach (var r in hutch.raccoons)
        {
            if (r.awayDays > 0 || r.job == (int)HutchJob.Companion) continue;       // out wandering, or out with the boss
            if (crewOut && r.job == (int)HutchJob.Crew && r.Available) continue;    // on the night shift
            Vector3 at; float yaw; bool onBox = false;
            if (r.Paired && breedingBox && boxed < 2)
            {
                // snuggled on the Breeding Box's lid, up on the roof
                at = new Vector3(BoxX + (boxed == 0 ? -0.17f : 0.17f), lidY, -0.05f); yaw = boxed == 0 ? 30f : -30f; boxed++; onBox = true;
            }
            else
            {
                if (slot >= slots) continue;
                int col = slot % 3, row = slot / 3;
                at = new Vector3(-0.58f + col * 0.58f, FloorTop + row * Storey, -0.05f + ((r.look >> 4) % 3 - 1) * 0.07f);
                yaw = ((r.look >> 2) % 5 - 2) * 14f;
                slot++;
            }
            var dress = hutch.collars > 0 && r.job != (int)HutchJob.Rest ? DollCollar.Gold : r.job == (int)HutchJob.Crew ? (brain > 0 ? DollCollar.Brain : DollCollar.Collar) : DollCollar.None;
            var doll = RaccoonDoll.Build(dolls, r, dress, 0.55f);
            doll.transform.localPosition = at;
            doll.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            doll.asleep = !GameClock.IsNight && r.job != (int)HutchJob.Rest || r.restDays > 0;
            // name tags hang on the wire (in front of the bars), not on the raccoon, so they read from the street
            var tag = WorkshopModels.MakeLabel(dolls, "Name tag", r.name, 0.75f, r.rabid ? new Color(0.7f, 1f, 0.55f) : Color.white);
            tag.transform.localPosition = new Vector3(at.x, at.y + (r.IsKit ? 0.3f : 0.42f), onBox ? at.z + 0.2f : Depth * 0.5f + 0.02f);
            tag.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            tag.rectTransform.sizeDelta = new Vector2(0.7f, 0.14f);
        }
    }

    public static bool TryFind(int lot, out HutchWorld hutch)
    {
        foreach (var h in all) if (h != null && h.lot == lot && h.model != null) { hutch = h; return true; }
        hutch = null; return false;
    }
}

/// <summary>The hutch's prompt. Owners manage; everybody else may look.</summary>
public sealed class HutchDoor : MonoBehaviour, IInteractable
{
    public HutchWorld hutch;
    public bool CanInteract() => isActiveAndEnabled && hutch != null && !PlayerController.IsUIBlocking;
    public string GetInteractionPrompt() => hutch != null && Workshop.CanManage(hutch.lot) ? "Open your Critter Hutch" : "Peek into the Critter Hutch";
    public void OnInteract(GameObject interactor) { if (CanInteract()) HutchMenu.Show(hutch.lot); }
    public void Highlight(bool on) { }
}

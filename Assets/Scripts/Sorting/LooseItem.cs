using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One physical piece of trash/treasure (spilled from a burst bag). Owned by the Sorting system.
/// FROZEN CONTRACT: Item, Def, Revealed, Spawn(), Reveal().
///
/// Unrevealed items are grimy "Mystery Gunk". They reveal when they land on the sorting table or when
/// picked up: grime washes off, a category-colored outline appears, and treasures sparkle + trigger the
/// big "RARE FIND!" card. A small world label ("Beer Bottle · Glass · 0.3 kg") shows while highlighted
/// or carried.
/// </summary>
public class LooseItem : Carryable
{
    public ItemInstance Item;
    public ItemDef Def => Item != null ? Item.Def : null;
    public bool Revealed { get; private set; }
    /// <summary>The player who opened the source bag or first handled this item; unknown for automated finds.</summary>
    public ulong FinderClientId { get; set; } = FeedbackAudience.UnknownFinder;

    public override float MassKg => Item != null ? Item.massKg : 0.1f;
    public override string DisplayName => Revealed && Def != null ? Def.name : "Mystery Gunk";

    // ------------------------------------------------------------------ extra (non-contract) API

    /// <summary>Category, Junk when the def is unknown.</summary>
    public ItemCategory Category => Def != null ? Def.category : ItemCategory.Junk;
    public Rarity Rarity => Def != null ? Def.rarity : Rarity.Common;
    public bool IsTreasure => Category == ItemCategory.Valuable;
    /// <summary>Treasure worth a big reveal (Uncommon or better).</summary>
    public bool IsFancyFind => IsTreasure && Rarity >= Rarity.Uncommon;
    /// <summary>The ItemVisuals child (Juice target).</summary>
    public GameObject Visual { get; private set; }
    public BoxCollider Box { get; private set; }

    Transform pulse; // parent of Visual; the highlight scale-pulses this so it never fights Juice on Visual
    public override Transform JuiceTarget => Visual != null ? Visual.transform : base.JuiceTarget;
    public override Transform HighlightScaleTarget => pulse;

    static readonly List<LooseItem> all = new List<LooseItem>();
    /// <summary>Every LooseItem alive in the loaded scenes.</summary>
    public static IReadOnlyList<LooseItem> All => all;

    // Unrevealed look
    static readonly Color GrimeColor = new Color(0.24f, 0.22f, 0.16f);
    const float GrimeAmount = 0.72f;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    // index -1 = renderer-wide block (single-material renderers), same convention as CarryHighlight so the
    // focus tint stacks on top of the grime and restores it exactly.
    struct Slot { public Renderer r; public int index; public Color color; public int prop; }
    readonly List<Slot> slots = new List<Slot>();
    static MaterialPropertyBlock mpb;

    // Label (a separate root object that follows us, so highlight tints / collider toggles never touch it)
    TextMeshPro label;
    bool highlighted;
    float highlightTime;

    // Reveal-on-landing (sorting table spray)
    bool revealOnLand;
    float revealDeadline;
    float armedAt;

    // Receiver cooldown (bins spit items back out; don't instantly re-swallow them)
    Object blockedReceiver;
    float blockedUntil;

    float lastImpactSfx;
    static float impactBudgetTime;
    static int impactBudget;

    static PhysicsMaterial itemPhysics;

    // ------------------------------------------------------------------ spawning

    public static LooseItem Spawn(ItemInstance item, Vector3 position, Quaternion rotation, Vector3 velocity = default, bool revealed = false) =>
        Spawn(item, position, rotation, velocity, revealed, FeedbackAudience.UnknownFinder);

    public static LooseItem Spawn(ItemInstance item, Vector3 position, Quaternion rotation, Vector3 velocity, bool revealed, ulong finderClientId)
    {
        var def = item != null ? item.Def : null;
        var go = new GameObject("LooseItem_" + (item != null ? item.defId : "?"));
        go.layer = InteractableLayer;
        go.transform.SetPositionAndRotation(position, Quaternion.identity); // size the collider unrotated

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Clamp(item != null ? item.massKg : 0.1f, 0.2f, 20f);
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.8f;
        rb.maxDepenetrationVelocity = 3f;

        var li = go.AddComponent<LooseItem>();
        li.Item = item;
        li.FinderClientId = finderClientId;
        li.BuildBody(def);

        go.transform.rotation = rotation;
        if (revealed) li.Reveal();
        else li.ApplyGrime();
        // A sprayed item is genuinely in flight, not already at rest. HandsNet hands guest-made objects back
        // to the host when a Flight settles; leaving this at the default Resting state handed it over before
        // the spray had even landed, which made remote sorting especially prone to snapping/falling away.
        if (velocity.sqrMagnitude > 0.0001f) li.EndCarry(velocity, true);
        else rb.linearVelocity = velocity;
        return li;
    }

    void BuildBody(ItemDef def)
    {
        pulse = new GameObject("Pulse").transform;
        pulse.gameObject.layer = InteractableLayer;
        pulse.SetParent(transform, false);
        GameObject vis = null;
        try { vis = ItemVisuals.Build(def, pulse); }
        catch (System.Exception e) { Debug.LogWarning($"[LooseItem] ItemVisuals.Build failed for {def?.id}: {e.Message}"); }
        if (vis == null)
        {
            vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(pulse, false);
            vis.transform.localScale = Vector3.one * 0.18f;
        }
        Visual = vis;
        foreach (var t in vis.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = InteractableLayer;
        foreach (var c in vis.GetComponentsInChildren<Collider>(true)) Destroy(c); // contract says none; be safe

        // Collider from renderer bounds. Largest side at least 12 cm so it stays grabbable; thin things
        // (newspaper) stay fairly thin so they don't hover.
        var b = SortingKit.RendererBounds(vis);
        Vector3 size = Vector3.Max(b.size, Vector3.one * 0.05f);
        float maxSide = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        if (maxSide < 0.12f) size *= 0.12f / maxSide;
        Box = gameObject.AddComponent<BoxCollider>();
        Box.center = transform.InverseTransformPoint(b.center);
        Box.size = size;
        if (itemPhysics == null)
        {
            itemPhysics = new PhysicsMaterial("LooseItem")
            {
                dynamicFriction = 0.6f, staticFriction = 0.7f, bounciness = 0.15f,
                frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Average
            };
        }
        Box.sharedMaterial = itemPhysics;
        gameObject.AddComponent<LooseTrashCollisions>();

        // Remember original colors for the grime tint. ItemVisuals tints parts with property blocks, so the
        // block's color (when present) is the real one, not the shared material's.
        slots.Clear();
        mpb ??= new MaterialPropertyBlock();
        foreach (var r in vis.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            var mats = r.sharedMaterials;
            bool single = mats.Length <= 1;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                int prop = m.HasProperty(BaseColorId) ? BaseColorId : m.HasProperty(ColorId) ? ColorId : -1;
                if (prop == -1) continue;
                int index = single ? -1 : i;
                if (index < 0) r.GetPropertyBlock(mpb); else r.GetPropertyBlock(mpb, index);
                Color c = mpb.HasColor(prop) || mpb.HasVector(prop) ? mpb.GetColor(prop) : m.GetColor(prop);
                slots.Add(new Slot { r = r, index = index, color = c, prop = prop });
            }
        }
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void Awake()
    {
        base.Awake();
        gameObject.layer = InteractableLayer;
    }

    protected override void OnEnable() { base.OnEnable(); if (!all.Contains(this)) all.Add(this); }

    protected override void OnDisable()
    {
        base.OnDisable();
        all.Remove(this);
        if (label != null) label.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (label != null) Destroy(label.gameObject);
    }

    void Update()
    {
        if (revealOnLand && !Revealed && Time.time >= revealDeadline) Reveal();
        if (revealOnLand && Revealed) revealOnLand = false;
    }

    void LateUpdate()
    {
        if (highlighted && Time.unscaledTime - highlightTime > 0.25f) highlighted = false; // focus went away
        // Held in first-person hands (sort station): the label would sit right in your face — the HUD hint shows it.
        bool heldFirstPerson = State == CarryState.Carried && Holder != null && Holder.HasHoldOverride;
        bool show = (highlighted || (State == CarryState.Carried && !heldFirstPerson)) && State != CarryState.Consumed && !IsInScriptedFlight;
        if (show && label == null) CreateLabel();
        if (label == null) return;
        if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
        if (!show) return;
        var cam = Camera.main;
        // (A disabled collider — e.g. while carried — reports empty bounds at the origin: use the renderers then.)
        float top = Box != null && Box.enabled ? Box.bounds.max.y : VisualTopY();
        label.transform.position = new Vector3(transform.position.x, top + 0.2f, transform.position.z);
        if (cam != null) label.transform.rotation = cam.transform.rotation;
        label.transform.localScale = Vector3.one * (State == CarryState.Carried ? 0.8f : 1f);
    }

    Renderer[] topRenderers; // cached for VisualTopY (no per-frame GetComponentsInChildren)

    /// <summary>World top of the visual's renderers (for the label while the collider is off).</summary>
    float VisualTopY()
    {
        if (Visual == null) return transform.position.y + 0.1f;
        if (topRenderers == null) topRenderers = Visual.GetComponentsInChildren<Renderer>();
        float top = float.NegativeInfinity;
        for (int i = 0; i < topRenderers.Length; i++)
        {
            var r = topRenderers[i];
            if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
            float y = r.bounds.max.y;
            if (y > top) top = y;
        }
        return float.IsNegativeInfinity(top) ? transform.position.y + 0.1f : top;
    }

    // ------------------------------------------------------------------ reveal

    /// <summary>Show what this actually is (name + rarity fx). Idempotent.</summary>
    public virtual void Reveal()
    {
        if (Revealed) return;
        Revealed = true;
        revealOnLand = false;
        // Let the focus highlight restore its snapshot (the grime) before we wash the grime off, so it
        // never "restores" grime onto a revealed item. PlayerController re-highlights next frame.
        bool wasHighlighted = IsHighlighted;
        base.Highlight(false);
        ClearGrime();
        BuildOutline();
        topRenderers = null; // the outline added renderers
        if (wasHighlighted) highlightTime = Time.unscaledTime;
        RefreshLabel();

        var def = Def;
        Vector3 p = transform.position;
        var holder = Holder != null ? Holder : LastHolder;
        if (holder != null && holder.Player != null) FinderClientId = holder.Player.OwnerClientId;
        HandsNet.Instance?.NotifyItemRevealed(this);
        if (Visual != null) Juice.Punch(Visual.transform, 0.35f, 0.25f);
        Sfx.Play("pop", p, 0.35f, Random.Range(0.95f, 1.35f));
        if (def == null) return;

        Color cat = ItemCategoryInfo.Color(def.category);
        Fx.Burst(p, cat, 6, 1.6f);

        if (DiskItem.Is(Item)) { DiskItem.OnRevealed(this); return; } // its own popup (with a KEEP button), not the treasure card
        if (def.category != ItemCategory.Valuable) return;
        Color rc = RarityInfo.Color(def.rarity);
        Fx.Sparkle(transform, rc, 3600f); // lasts as long as the item does
        if (def.rarity >= Rarity.Uncommon)
        {
            ulong localId = PlayerController.Local != null ? PlayerController.Local.OwnerClientId : FeedbackAudience.UnknownFinder; // coop:allow(local) this viewer's personal discovery HUD
            bool full = FeedbackAudience.FullFindForLocal(NetSession.InSession, FinderClientId, localId) && PlayerController.Local != null; // coop:allow(local) this viewer's personal discovery HUD
            if (full) GameHUD.BigReveal(Item);
            else if (PlayerController.Local != null) GameHUD.Activity($"{RarityInfo.Label(def.rarity)} find: {def.name}"); // coop:allow(local) observer activity feed only
            if (!full) return;
            Sfx.Play(def.rarity >= Rarity.Epic ? "legendary" : "rare", p);
            // At the first-person sort station this pops ~1 m from your eye: a gentler burst so it doesn't fill the view.
            float burst = SortStation.LocalInSortMode ? 0.5f : 1f;
            Fx.Burst(p, rc, Mathf.RoundToInt((def.rarity >= Rarity.Epic ? 50 : 30) * Mathf.Lerp(1f, burst, 0.5f)), (def.rarity >= Rarity.Epic ? 6f : 4.5f) * burst);
            Juice.Shake(def.rarity >= Rarity.Epic ? 0.3f : 0.12f, 0.25f);
            if (def.rarity >= Rarity.Epic) Juice.HitStop(def.rarity == Rarity.Legendary ? 0.45f : 0.25f, 0.06f);
        }
        else Sfx.Play("sparkle", p, 0.7f);
    }

    /// <summary>
    /// Reveal the next time this lands on something (sorting table spray), or after fallbackSeconds at the
    /// latest. No-op when already revealed.
    /// </summary>
    public void RevealOnLanding(float fallbackSeconds = 1f)
    {
        if (Revealed) return;
        revealOnLand = true;
        armedAt = Time.time;
        revealDeadline = Time.time + Mathf.Max(0.05f, fallbackSeconds);
    }

    /// <summary>
    /// Put this item back in its revealed look without any fanfare (no sounds, reveal card or hit-stop).
    /// Used by SceneTrashKeeper when a saved pile is restored after a scene change. Treasures keep sparkling.
    /// </summary>
    public void RestoreRevealed()
    {
        if (Revealed) return;
        Revealed = true;
        revealOnLand = false;
        ClearGrime();
        BuildOutline();
        topRenderers = null;
        RefreshLabel();
        if (IsTreasure) Fx.Sparkle(transform, RarityInfo.Color(Rarity), 3600f);
    }

    // ------------------------------------------------------------------ receiver cooldown

    /// <summary>Stop a receiver from swallowing this item again for a moment (after it spat it out).</summary>
    public void BlockReceiver(Object receiver, float seconds)
    {
        blockedReceiver = receiver;
        blockedUntil = Time.time + seconds;
    }

    public bool IsBlockedFor(Object receiver) => receiver != null && receiver == blockedReceiver && Time.time < blockedUntil;

    // ------------------------------------------------------------------ Carryable hooks

    public override string GetInteractionPrompt()
    {
        if (Revealed && IsTreasure) return $"{PickupVerb} {DisplayName} ({RarityInfo.Label(Rarity)}!)";
        return $"{PickupVerb} {DisplayName} ({SortingKit.Mass(MassKg)})";
    }

    public override void Highlight(bool highlight)
    {
        base.Highlight(highlight);
        highlighted = highlight && State != CarryState.Carried && State != CarryState.Consumed;
        if (highlighted) highlightTime = Time.unscaledTime;
    }

    protected override void OnPickedUp(TrashCarrier carrier)
    {
        highlighted = false;
        Reveal();
    }

    protected override void OnReleased(TrashCarrier carrier, bool thrown)
    {
        highlighted = false;
    }

    protected override void OnImpact(Collision collision, float speed)
    {
        if (revealOnLand && !Revealed && speed > 0.2f && Time.time - armedAt > 0.05f) Reveal();

        if (speed < 1.6f || Time.time - lastImpactSfx < 0.15f) return;
        // Global cap so a 40-item avalanche doesn't turn into white noise.
        if (Time.time - impactBudgetTime > 0.25f) { impactBudgetTime = Time.time; impactBudget = 4; }
        if (impactBudget <= 0) return;
        impactBudget--;
        lastImpactSfx = Time.time;

        float vol = Mathf.Clamp01(0.15f + speed * 0.06f);
        string id = "thud";
        float pitch;
        switch (Revealed ? Category : ItemCategory.Junk)
        {
            case ItemCategory.Glass: pitch = 1.8f; break;
            case ItemCategory.Metal: pitch = 1.45f; break;
            case ItemCategory.Plastic: pitch = 1.25f; break;
            case ItemCategory.Paper: pitch = 0.85f; vol *= 0.6f; break;
            case ItemCategory.Valuable: pitch = 2.1f; break;
            case ItemCategory.Hazard: pitch = 1.1f; break;
            default: id = Revealed ? "splat" : "thud"; pitch = Mathf.Lerp(1.3f, 0.8f, Mathf.Clamp01(MassKg / 2f)); vol *= 0.7f; break;
        }
        Sfx.Play(id, transform.position, vol, pitch);
    }

    // ------------------------------------------------------------------ visuals

    void ApplyGrime()
    {
        mpb ??= new MaterialPropertyBlock();
        foreach (var s in slots)
        {
            if (s.r == null) continue;
            if (s.index < 0) s.r.GetPropertyBlock(mpb); else s.r.GetPropertyBlock(mpb, s.index);
            var c = Color.Lerp(s.color, GrimeColor, GrimeAmount);
            c.a = s.color.a;
            mpb.SetColor(s.prop, c);
            if (s.index < 0) s.r.SetPropertyBlock(mpb); else s.r.SetPropertyBlock(mpb, s.index);
        }
    }

    void ClearGrime()
    {
        mpb ??= new MaterialPropertyBlock();
        foreach (var s in slots)
        {
            if (s.r == null) continue;
            if (s.index < 0) s.r.GetPropertyBlock(mpb); else s.r.GetPropertyBlock(mpb, s.index);
            mpb.SetColor(s.prop, s.color);
            if (s.index < 0) s.r.SetPropertyBlock(mpb); else s.r.SetPropertyBlock(mpb, s.index);
        }
    }

    void BuildOutline()
    {
        if (Visual == null || Def == null) return;
        var mat = SortingKit.OutlineMaterial();
        if (mat == null) return;
        Color c = IsTreasure ? RarityInfo.Color(Rarity) : ItemCategoryInfo.Color(Category);
        float thickness = IsFancyFind ? 0.018f : 0.011f;
        mpb ??= new MaterialPropertyBlock();
        foreach (var mf in Visual.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh;
            var src = mf.GetComponent<MeshRenderer>();
            if (mesh == null || src == null || mf.name == "Outline") continue;
            var go = new GameObject("Outline");
            go.layer = InteractableLayer;
            go.transform.SetParent(mf.transform, false);
            // Scale about the mesh center so the hull grows evenly by ~thickness on every side.
            Vector3 lossy = mf.transform.lossyScale;
            Vector3 size = mesh.bounds.size;
            Vector3 k = new Vector3(
                Grow(size.x * Mathf.Abs(lossy.x), thickness),
                Grow(size.y * Mathf.Abs(lossy.y), thickness),
                Grow(size.z * Mathf.Abs(lossy.z), thickness));
            Vector3 cen = mesh.bounds.center;
            go.transform.localScale = k;
            go.transform.localPosition = new Vector3(cen.x * (1f - k.x), cen.y * (1f - k.y), cen.z * (1f - k.z));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mats = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            mpb.Clear();
            mpb.SetColor(BaseColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }

    static float Grow(float worldSize, float thickness) =>
        Mathf.Clamp(1f + 2f * thickness / Mathf.Max(0.01f, worldSize), 1.03f, 1.6f);

    void CreateLabel()
    {
        label = SortingKit.MakeText(null, "ItemLabel", "", 0.72f, Color.white);
        label.sortingOrder = 10;
        RefreshLabel();
    }

    void RefreshLabel()
    {
        if (label == null) return;
        label.text = LabelText();
    }

    /// <summary>"Beer Bottle · Glass · 0.3 kg" / "Wedding Ring · LEGENDARY · ~$2,300" (rich text).</summary>
    public string LabelText()
    {
        var def = Def;
        if (!Revealed || def == null)
            return $"<color=#C8BE9A>Mystery Gunk</color> · <color=#8A8A8A>???</color> · {SortingKit.Mass(MassKg)}";
        if (def.category == ItemCategory.Valuable)
        {
            string rc = SortingKit.Hex(RarityInfo.Color(def.rarity));
            return $"<color=#{rc}>{def.name}</color> · <color=#{rc}>{RarityInfo.Label(def.rarity)}</color> · {SortingKit.Appraisal(Item.value)}";
        }
        string cc = SortingKit.Hex(ItemCategoryInfo.Color(def.category));
        return $"{def.name} · <color=#{cc}>{ItemCategoryInfo.DisplayName(def.category)}</color> · {SortingKit.Mass(MassKg)}";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        impactBudget = 0;
        impactBudgetTime = 0f;
    }
}

using UnityEngine;

/// <summary>
/// A curbside trash sack. Owned by the Trash (ex-Pickup) system. FROZEN CONTRACT: Contents, Spawn(), Burst(),
/// SourceStop. Living world additions: static event AnyPickedUp (critters react when the player grabs any
/// bag), CurbRecord / IsCurbBag / CurbAgeHours (the GameSession.CurbBags record while it's still a curb bag).
/// A burst curb bag loses its record (no pay; its loose items are litter you can still collect).
///
/// Hierarchy built by Spawn:  TrashBag (Rigidbody + BoxCollider, layer Interactable)
///                              └ Visual (highlight pulse)
///                                  └ Model (sack mesh; Juice squash target)
/// Flops rather than rolls (box collider, high angular drag, grippy low-bounce material). Thud + dust
/// + squash on landing; a hard hit while Thrown bursts it into LooseItems.
/// </summary>
public class TrashBag : Carryable
{
    public BagContents Contents;
    /// <summary>The stop this bag was put out at (null once it's been to the truck / at the shop).</summary>
    public PickupStop SourceStop;
    /// <summary>True for this physical curb visual even after its stop and curb record are cleared. A truck-cargo bag
    /// made later from the same contents is a different object and does not inherit this marker.</summary>
    public bool WasMaterializedAtCurb { get; private set; }

    internal void MarkCurbVisual(PickupStop stop)
    {
        SourceStop = stop;
        WasMaterializedAtCurb = true;
    }

    /// <summary>Fired whenever a player grabs any TrashBag (curb, pile, anywhere). Critters react to this.</summary>
    public static event System.Action<TrashBag, TrashCarrier> AnyPickedUp;

    /// <summary>The curb record (GameSession.CurbBags) while this bag is still waiting to be collected, else null.</summary>
    public CurbBag CurbRecord => Contents != null && !string.IsNullOrEmpty(Contents.bagId) ? GameSession.FindCurbBag(Contents.bagId) : null;
    /// <summary>True while this is an uncollected curb bag (pays a pickup fee when it goes into the truck).</summary>
    public bool IsCurbBag => CurbRecord != null;
    /// <summary>In-game hours since its house put it out (0 if it isn't a curb bag).</summary>
    public double CurbAgeHours { get { var r = CurbRecord; return r != null ? GameClock.AgeHours(r.putOutAtHours) : 0; } }

    /// <summary>
    /// Set by the truck's side-loader arm (builder B) before it tosses this bag into the hopper, so the BagCollected fact
    /// says byArm. Cleared when a player picks the bag up.
    /// </summary>
    public bool ByArm;

    /// <summary>Impact speed (m/s) above which a thrown bag bursts.</summary>
    public const float BurstImpactSpeed = 9f;
    public const float MinBodyMass = 2f, MaxBodyMass = 25f;

    public override float MassKg => Contents != null ? Contents.TotalMassKg : 5f;

    string displayName;
    public override string DisplayName
    {
        get
        {
            if (displayName == null) displayName = BagTells.NameFor(Contents, Tell, NameFor(Contents));
            return displayName;
        }
    }

    public override string PickupVerb => MassKg > 12f || Tell == BagTell.Heavy ? "Heave" : "Grab";

    // ---------------- F3 bag tells (Docs/Multiplayer.md §14.3; Trash/Tells) ----------------
    /// <summary>This bag's personality (a hash of its bag id: identical on every machine).</summary>
    public BagTell Tell => BagTells.Of(this);
    /// <summary>Time.time before which an impact never bursts this bag (the bag cannon's soft landing).</summary>
    public float NoBurstUntil;

    /// <summary>Burst it right here as if it hit something hard (cannon misfire). Co-op: goes through the network like any burst.</summary>
    public void BurstNow(Vector3 point, Vector3 normal)
    {
        if (State == CarryState.Consumed || HasBurst) return;
        BurstFromImpact(point, normal, -normal * BurstImpactSpeed);
    }

    public override void OnInteract(GameObject interactor)
    {
        var pt = interactor != null ? interactor.GetComponent<PlayerTells>() : null;
        if (pt != null && pt.IsCube)
        {
            GameHUD.FloatingText(transform.position + Vector3.up * 1.2f, "cubes have no hands", new Color(1f, 0.7f, 0.9f), 0.9f);
            Sfx.Play("squeak", transform.position, 0.5f, 1.6f);
            return;
        }
        base.OnInteract(interactor);
    }

    protected override void OnReleased(TrashCarrier carrier, bool thrown)
    {
        // A heavy bag thrown by one person alone just... plops (Heavy tell: it takes two to throw it).
        if (!thrown || Tell != BagTell.Heavy || carrier == null || Body == null || Body.isKinematic) return;
        var pt = carrier.GetComponent<PlayerTells>();
        if (pt != null && pt.LiftedTogether) return;
        Body.linearVelocity = Vector3.Scale(Body.linearVelocity, new Vector3(0.22f, 0.3f, 0.22f));
        if (carrier.IsLocalOwner)
        {
            GameHUD.FloatingText(WorldCenter + Vector3.up * 0.7f, "HNNNG... *plop*", new Color(1f, 0.8f, 0.4f), 1f);
            Sfx.Play("oof", WorldCenter, 0.8f, 0.8f);
        }
    }

    Transform visualRoot, modelRoot;
    public override Transform JuiceTarget => modelRoot != null ? modelRoot : base.JuiceTarget;
    public override Transform HighlightScaleTarget => visualRoot;

    /// <summary>True once Burst has run (the object is about to be destroyed).</summary>
    public bool HasBurst { get; private set; }

    float lastLandFx = -999f;
    static PhysicsMaterial flopMaterial;
    static float lastBurstToast = -999f;

    static readonly Color[] SackColors =
    {
        new Color(0.035f, 0.035f, 0.04f),   // classic black
        new Color(0.035f, 0.035f, 0.04f),
        new Color(0.035f, 0.035f, 0.04f),
        new Color(0.04f, 0.10f, 0.05f),     // lawn-and-leaf green
        new Color(0.05f, 0.06f, 0.14f),     // navy
        new Color(0.78f, 0.78f, 0.74f),     // white kitchen bag
    };
    static readonly string[] BurstWords = { "SPLORCH!", "SPLAT!", "BLORP!", "KER-SPLUT!", "oh no" };
    static readonly string[] BurstToasts =
    {
        "Bag burst! Pick up the bits.",
        "SPLORCH. That's gonna stain.",
        "Oops. Garbage confetti.",
        "Gentler next time, champ.",
    };

    protected override void Awake()
    {
        base.Awake();
        visualRoot = transform.Find("Visual");
        modelRoot = visualRoot != null ? visualRoot.Find("Model") : null;
    }

    // =====================================================================
    // Spawning
    // =====================================================================

    /// <summary>
    /// Spawn a physical bag for these contents (sack visual chosen by contents.sackVariant; -1 = loose pile,
    /// which uses sack 0). position = where the bottom of the bag sits.
    /// </summary>
    public static TrashBag Spawn(BagContents contents, Vector3 position, Quaternion rotation, Vector3 velocity = default)
    {
        float mass = contents != null ? contents.TotalMassKg : 5f;
        var go = new GameObject("TrashBag" + (contents != null && !string.IsNullOrEmpty(contents.bagId) ? " " + contents.bagId : ""));
        go.SetActive(false); // build everything before physics sees it
        go.layer = InteractableLayer;
        go.transform.SetPositionAndRotation(position, rotation);

        // --- visual ---
        var visual = new GameObject("Visual").transform;
        visual.SetParent(go.transform, false);
        Transform model = BuildModel(contents, visual);

        // Heavier bags look a bit chunkier.
        float size = Mathf.Lerp(0.9f, 1.18f, Mathf.InverseLerp(3f, 15f, mass));
        model.localScale *= size;

        // Sit the model's bottom-center on the root pivot.
        var tmpBounds = LocalRendererBounds(go.transform);
        model.localPosition -= new Vector3(tmpBounds.center.x, tmpBounds.min.y, tmpBounds.center.z);
        var b = LocalRendererBounds(go.transform);

        PickupVisuals.SetLayerRecursively(go, InteractableLayer);

        // --- physics ---
        var col = go.AddComponent<BoxCollider>();
        col.center = b.center;
        col.size = new Vector3(Mathf.Max(0.2f, b.size.x * 0.88f), Mathf.Max(0.2f, b.size.y * 0.95f), Mathf.Max(0.2f, b.size.z * 0.88f));
        col.sharedMaterial = FlopMaterial;

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Clamp(mass, MinBodyMass, MaxBodyMass);
        rb.linearDamping = 0.35f;
        rb.angularDamping = 3f;
        rb.maxAngularVelocity = 14f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

        var bag = go.AddComponent<TrashBag>();
        bag.Contents = contents;
        bag.visualRoot = visual;
        bag.modelRoot = model;

        BagTellFx.Attach(bag); // F3: leaky / heavy / over-stuffed / mystery look (no-op for plain bags)
        go.SetActive(true);
        rb.linearVelocity = velocity;
        if (velocity.sqrMagnitude > 0.01f) rb.angularVelocity = Random.insideUnitSphere * 3f;

        // Some bags are just... like that.
        if (bag.DisplayName.StartsWith("Stinky")) Fx.Stink(go.transform);
        return bag;
    }

    /// <summary>
    /// The cosmetic sack for these contents (WorldLife W0: a resident carrying a bag out, Docs/WorldLife.md §7.2): the same
    /// model, size and colour the real TrashBag will have, renderers only (no colliders / Rigidbody / TrashBag), with its
    /// bottom-centre on <paramref name="parent"/>'s origin. Returns the model transform (destroy it when done).
    /// </summary>
    public static Transform BuildSackVisual(BagContents contents, Transform parent)
    {
        Transform model = BuildModel(contents, parent);
        float mass = contents != null ? contents.TotalMassKg : 5f;
        model.localScale *= Mathf.Lerp(0.9f, 1.18f, Mathf.InverseLerp(3f, 15f, mass));
        var b = LocalRendererBounds(model);   // model-local (unscaled) units
        Vector3 s = model.localScale;
        model.localPosition = -new Vector3(b.center.x * s.x, b.min.y * s.y, b.center.z * s.z);
        if (contents != null && contents.sackVariant >= 0 && BagTells.Of(contents.bagId) == BagTell.Heavy)
            BagTellFx.EnlargeHeavySack(model);
        model.name = "SackVisual";
        return model;
    }

    static Transform BuildModel(BagContents contents, Transform visual)
    {
        int variant = contents != null ? contents.sackVariant : 0;
        if (variant < 0) variant = 0;

        GameObject prefab = RubbishAssets.Prefab("sack/" + variant);
        if (prefab == null)
        {
            var list = RubbishAssets.PrefabsWithPrefix("sack/");
            if (list.Count > 0) prefab = list[Mathf.Clamp(variant, 0, list.Count - 1)];
        }

        GameObject modelGo;
        if (prefab != null)
        {
            modelGo = Object.Instantiate(prefab, visual, false);
            modelGo.name = "Model";
            modelGo.transform.localPosition = Vector3.zero;
            modelGo.transform.localRotation = Quaternion.identity;
            modelGo.transform.localScale = Vector3.one;
            // Strip the prop's own colliders (non-convex MeshColliders can't live under a dynamic body).
            foreach (var c in modelGo.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var r in modelGo.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(r);
        }
        else
        {
            // Fallback: a squashed glossy blob.
            modelGo = new GameObject("Model");
            modelGo.transform.SetParent(visual, false);
            var blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(blob.GetComponent<Collider>());
            blob.name = "Blob";
            blob.transform.SetParent(modelGo.transform, false);
            blob.transform.localPosition = new Vector3(0f, 0.33f, 0f);
            blob.transform.localScale = new Vector3(0.55f, 0.66f, 0.52f);
            blob.GetComponent<Renderer>().sharedMaterial = PickupVisuals.BagMaterial;
            var knot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(knot.GetComponent<Collider>());
            knot.name = "Knot";
            knot.transform.SetParent(modelGo.transform, false);
            knot.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            knot.transform.localScale = new Vector3(0.14f, 0.12f, 0.14f);
            knot.GetComponent<Renderer>().sharedMaterial = PickupVisuals.BagMaterial;
        }

        // Per-bag colour + a glossy plastic sheen (property block, so the shared material is untouched).
        string id = contents != null ? contents.bagId : null;
        int h = PickupVisuals.StableHash(id ?? "bag");
        Color col = SackColors[(int)((uint)h % (uint)SackColors.Length)];
        var mpb = new MaterialPropertyBlock();
        foreach (var r in modelGo.GetComponentsInChildren<Renderer>())
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", col);
            mpb.SetColor("_Color", col);
            mpb.SetFloat("_Smoothness", 0.62f);
            r.SetPropertyBlock(mpb);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
        return modelGo.transform;
    }

    static PhysicsMaterial FlopMaterial
    {
        get
        {
            if (flopMaterial == null)
            {
                flopMaterial = new PhysicsMaterial("TrashBagFlop")
                {
                    dynamicFriction = 0.85f,
                    staticFriction = 1f,
                    bounciness = 0.04f,
                    frictionCombine = PhysicsMaterialCombine.Maximum,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
            return flopMaterial;
        }
    }

    static Bounds LocalRendererBounds(Transform root)
    {
        var inv = root.worldToLocalMatrix;
        bool any = false;
        Bounds b = new Bounds(Vector3.up * 0.35f, new Vector3(0.6f, 0.7f, 0.6f));
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var m = inv * r.transform.localToWorldMatrix;
            var mb = mf.sharedMesh.bounds;
            Vector3 c = mb.center, e = mb.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    /// <summary>Derpy weight-hinting name, stable per bag.</summary>
    public static string NameFor(BagContents c)
    {
        if (c == null) return "Trash Bag";
        float m = c.TotalMassKg;
        if (c.sackVariant < 0) return "Mystery Sack";
        if (m > 18f) return "Absolute Unit of a Trash Bag";
        if (m > 13f) return "Chonky Trash Bag";
        if (m > 10f) return "Chunky Trash Bag";
        if (m < 4f) return "Wimpy Little Trash Bag";

        int glass = 0, hazard = 0, paper = 0;
        foreach (var it in c.items)
        {
            var d = it != null ? it.Def : null;
            if (d == null) continue;
            if (d.category == ItemCategory.Glass) glass++;
            else if (d.category == ItemCategory.Hazard) hazard++;
            else if (d.category == ItemCategory.Paper) paper++;
        }
        if (glass >= 3) return "Clinky Trash Bag";
        if (hazard >= 2) return "Suspicious Trash Bag";
        if (paper >= 4) return "Crinkly Trash Bag";

        string[] adj = { "Lumpy", "Squishy", "Stinky", "Leaky", "Saggy", "Regular Ol'" };
        int h = PickupVisuals.StableHash(c.bagId ?? "bag");
        return adj[(int)((uint)h % (uint)adj.Length)] + " Trash Bag";
    }

    // =====================================================================
    // Physics feel
    // =====================================================================

    protected override void OnPickedUp(TrashCarrier carrier)
    {
        ByArm = false;
        // Leaving the curb (or the pile): the carrier plays the pickup juice; critters get told.
        try { AnyPickedUp?.Invoke(this, carrier); }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    protected override void OnImpact(Collision collision, float speed)
    {
        if (State == CarryState.Consumed || HasBurst) return;

        Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        Vector3 normal = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.up;
        float normalSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal));
        // Head-on hits count fully; glancing slides count less.
        float impact = Mathf.Max(normalSpeed, speed * 0.7f);

        bool soft = Time.time < NoBurstUntil;
        bool stuffed = Tell == BagTell.OverStuffed;
        float burstAt = stuffed ? BagTells.OverStuffedBurstSpeed : BurstImpactSpeed;
        if (!soft && !IsPlayer(collision) &&
            ((State == CarryState.Thrown && impact > burstAt) || (stuffed && impact > BagTells.OverStuffedSlamSpeed)))
        {
            if (stuffed)
            {
                GameHUD.FloatingText(WorldCenter + Vector3.up * 1f, "KA-BLOOEY! (it was over-stuffed)", new Color(1f, 0.6f, 0.2f), 1.3f);
                Fx.Burst(WorldCenter, new Color(0.85f, 0.75f, 0.5f), 24, 7f);
            }
            BurstFromImpact(point, normal, collision.relativeVelocity);
            return;
        }

        if (impact < 1.4f || Time.time - lastLandFx < 0.12f) return;
        lastLandFx = Time.time;
        float k = Mathf.InverseLerp(1.4f, BurstImpactSpeed, impact);
        float heavy = Mathf.InverseLerp(2f, 20f, MassKg);
        Sfx.Play("bag_land", point, Mathf.Lerp(0.35f, 1f, k), Mathf.Lerp(1.2f, 0.8f, heavy) * Random.Range(0.92f, 1.08f));
        if (k > 0.3f) Sfx.Play("thud", point, Mathf.Lerp(0.3f, 0.9f, k), Mathf.Lerp(1.1f, 0.75f, heavy));
        Juice.Squash(JuiceTarget, Mathf.Lerp(0.12f, 0.42f, k), Mathf.Lerp(0.2f, 0.3f, k));
        Fx.Dust(point, Mathf.Lerp(0.3f, 1.3f, k));
        if (k > 0.7f) Juice.Shake(0.06f * k, 0.12f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        AnyPickedUp = null;
        NetBurst = null;
        lastBurstToast = -999f;
    }

    static bool IsPlayer(Collision c) => c.collider is CharacterController || (c.collider != null && c.collider.GetComponentInParent<PlayerController>() != null);

    // =====================================================================
    // Bursting
    // =====================================================================

    /// <summary>
    /// Rip the bag open: spawns a LooseItem per content item flying outward from the bag, then
    /// destroys the bag. revealItems=true shows item names immediately (sorting table) and keeps the
    /// spill gentle so things stay on the table.
    /// </summary>
    public void Burst(bool revealItems = false)
    {
        if (State == CarryState.Consumed || HasBurst) return;
        DoBurst(revealItems, false, Vector3.up, Vector3.zero);
    }

    /// <summary>
    /// Co-op (N2): a networked bag this machine simulates hit the static world hard enough to burst. Return true when the
    /// network layer took it (a guest plays the splat, hides its copy and asks the host, which rolls what comes out).
    /// </summary>
    public static System.Func<TrashBag, Vector3, Vector3, Vector3, bool> NetBurst;

    /// <summary>Net/Hands (host): burst this bag as if it hit something at <paramref name="point"/> (a guest's throw).</summary>
    public void NetBurstAt(Vector3 point, Vector3 normal, Vector3 relVel)
    {
        if (State == CarryState.Consumed || HasBurst) return;
        BurstFromImpact(point, normal, relVel, true);
    }

    void BurstFromImpact(Vector3 point, Vector3 normal, Vector3 relVel, bool fromNet = false)
    {
        if (!fromNet && NetId != 0 && NetBurst != null)
        {
            bool taken = false;
            try { taken = NetBurst(this, point, normal, relVel); }
            catch (System.Exception e) { Debug.LogException(e); }
            if (taken) return;
        }
        Vector3 pos = WorldCenter;
        // --- juice: the good stuff ---
        Sfx.Play("bag_burst", pos, 1f, Random.Range(0.9f, 1.1f));
        Sfx.Play("splat", pos, 0.8f, Random.Range(0.85f, 1.15f));
        Fx.Burst(pos, new Color(0.06f, 0.06f, 0.07f), 34, 6f);      // shredded black plastic
        Fx.Burst(pos, new Color(0.38f, 0.30f, 0.12f), 14, 4f);      // mystery gunk
        Fx.Dust(point, 1.4f);
        var stink = new GameObject("BurstStink");
        stink.transform.position = point + Vector3.up * 0.2f;
        Fx.Stink(stink.transform);
        Destroy(stink, 7f);
        Juice.Shake(0.28f, 0.22f);
        Juice.HitStop(0.05f, 0.1f);
        GameHUD.FloatingText(pos + Vector3.up * 0.6f, BurstWords[Random.Range(0, BurstWords.Length)], new Color(0.6f, 0.9f, 0.2f), 1.3f);
        if (Time.unscaledTime - lastBurstToast > 12f)
        {
            lastBurstToast = Time.unscaledTime;
            GameHUD.Toast(BurstToasts[Random.Range(0, BurstToasts.Length)], ToastStyle.Bad);
        }

        // Carry a bit of the incoming momentum along the surface.
        Vector3 v = Body != null ? Body.linearVelocity : Vector3.zero;
        Vector3 along = Vector3.ProjectOnPlane(v.sqrMagnitude > 0.5f ? v : -relVel, normal) * 0.25f;
        DoBurst(false, true, normal, along);
    }

    void DoBurst(bool revealItems, bool violent, Vector3 normal, Vector3 carryVelocity)
    {
        HasBurst = true;
        Vector3 pos = WorldCenter;
        if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;

        if (!violent)
        {
            // Rip-open (sorting table etc): a plasticky pop, not a disaster.
            Sfx.Play("bag_burst", pos, 0.8f, Random.Range(1.05f, 1.2f));
            Sfx.Play("rustle", pos, 0.8f, 1f);
            Fx.Burst(pos, new Color(0.06f, 0.06f, 0.07f), 18, 3f);
        }

        // F3: a leaky bag that bursts leaves a big juice splat (the puddle authority adds it; a guest never gets here).
        if (violent && Tell == BagTell.Leaky) Puddles.Spawn(pos, 1.2f, PuddleKind.Splat);

        // A burst curb bag has left the curb for good: drop its record (no pay — the bits are litter now).
        CurbTrash.RemoveRecord(Contents);
        var stop = SourceStop;
        SourceStop = null;

        // Hand everything back to physics before spawning so nothing collides with a kinematic ghost.
        SetCollidersEnabled(false);

        if (Contents != null && Contents.items != null)
        {
            int n = Contents.items.Count;
            for (int i = 0; i < n; i++)
            {
                var item = Contents.items[i];
                if (item == null) continue;
                Vector3 dir;
                Vector3 vel;
                if (revealItems)
                {
                    float a = (i + Random.value * 0.5f) / Mathf.Max(1, n) * Mathf.PI * 2f;
                    dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    vel = dir * Random.Range(0.3f, 0.9f) + Vector3.up * Random.Range(1.4f, 2.6f);
                }
                else
                {
                    dir = (normal * 1.1f + Random.insideUnitSphere * 0.9f + Vector3.up * 0.5f).normalized;
                    vel = carryVelocity + dir * Random.Range(2.5f, violent ? 5.5f : 3.5f);
                }
                Vector3 p = pos + dir * 0.18f + Random.insideUnitSphere * 0.08f;
                try
                {
                    ulong finderClientId = LastHolder != null && LastHolder.Player != null
                        ? LastHolder.Player.OwnerClientId : FeedbackAudience.UnknownFinder;
                    var li = LooseItem.Spawn(item, p, Random.rotation, vel, revealItems, finderClientId);
                    if (li != null && li.Body != null) li.Body.angularVelocity = Random.insideUnitSphere * 8f;
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        if (stop != null) stop.NotifyBagCollected(this);
        Consume();
    }
}

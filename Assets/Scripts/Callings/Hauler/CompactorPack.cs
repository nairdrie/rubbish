using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Compactor Backpack (Hauler calling, <see cref="CallingIds.CompactorBackpack"/>) on every player copy (added by
/// <see cref="HaulerRuntime"/>). It replaces the old solo-shaped Back Sack: Work Gloves 2 / 3 still give 1 / 2 back slots,
/// and they now ride in the same host-tracked inventory (<c>HandsNet.Pack.cs</c>), so they work in co-op too.
/// <list type="bullet">
/// <item><b>Owner - E:</b> carrying a bag, E on another = "stash and grab"; at a truck hopper or your walk-up hatch, E unloads
/// the lot (the hopper pays the truck's business, as ever); empty-handed, E takes the top bag back off.</item>
/// <item><b>Owner - <see cref="HaulerTuning.PackKey"/>:</b> tap = stash the bag you hold (or take one off with empty hands);
/// hold (level 2) = CRUSH, one bag every 0.55 s. Crushing never touches the contents (<see cref="HaulerRules.CrushKeepsContents"/>).
/// <see cref="HaulerTuning.EjectKey"/> (level 3) ejects the whole load at your feet as one bale.</item>
/// <item><b>Everyone:</b> the pack and its growing stack of bags (bricks once crushed) from the replicated <see cref="PackNet"/>,
/// and the crunch / stash / unload moments (<see cref="HandsNet.PackFx"/>).</item>
/// </list>
/// The load slides off when you drive, sit at chess or the sort station, become a cube or leave (host-checked), and bursts
/// everywhere on a Rocket Blades wipe-out.
/// </summary>
[DisallowMultipleComponent]
public class CompactorPack : MonoBehaviour, IInteractable
{
    enum Act { None, StashGrab, Unload, TakeOff }
    const int MaxLumps = 16;

    /// <summary>This machine's own player's pack (HUD, speed).</summary>
    public static CompactorPack Local { get; private set; }
    /// <summary>The local player's walk / sprint multiplier for the weight on their back (BootsMods composes it).</summary>
    public static float LocalSpeedMult => Local != null ? Local.speedMult : 1f;

    PlayerController pc;
    TrashCarrier carrier;
    InteractionZone zone;

    // ---- owner
    Act act;
    TrashBag grabTarget, highlighted, pendingGrab;
    ICarryReceiver unloadTarget;
    int slots, packLevel;
    float speedMult = 1f, nextSlotCheck, keyDownAt = -1f, nextCrush, pendingGrabUntil, nextSpillAsk, busyUntil;
    bool crushedThisHold, tapArmed;
    RideRider rider;
    static readonly Collider[] overlap = new Collider[24];

    // ---- every copy
    PackNet shown;
    bool haveShown;
    Transform root, frame, crusher, stack, piston;
    readonly List<Transform> lumps = new List<Transform>(MaxLumps);
    int shownLevel = -1;
    float nextLevelCheck, pistonKick, wobble;
    Material sackMat, brickMat;
    static Mesh cubeMesh, sphereMesh;

    public ulong ClientId => pc != null && pc.IsSpawned ? pc.OwnerClientId : 0;
    public bool IsLocal => pc != null && CoopPlayerHooks.IsLocalOwner(pc);
    /// <summary>Bags on this player's back as this machine knows it.</summary>
    public int Count => haveShown ? shown.Count : 0;
    public int Slots => slots;

    static HandsNet Hands => HandsNet.Instance != null && HandsNet.Instance.IsSpawned ? HandsNet.Instance : null;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
    }

    void OnEnable()
    {
        HandsNet.PackFx += OnPackFx;
        HandsNet.PacksChanged += Refresh;
    }

    void OnDisable()
    {
        HandsNet.PackFx -= OnPackFx;
        HandsNet.PacksChanged -= Refresh;
        if (Local == this) Local = null;
        act = Act.None;
        if (highlighted != null) { highlighted.Highlight(false); highlighted = null; }
    }

    void OnDestroy() { if (root != null) Destroy(root.gameObject); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Local = null;

    // ==================================================================================== per frame

    void Update()
    {
        var hands = Hands;
        if (Time.unscaledTime >= nextLevelCheck)
        {
            nextLevelCheck = Time.unscaledTime + 0.5f;
            packLevel = hands != null ? Mathf.Clamp(hands.HaulerLevel(ClientId, CallingIds.CompactorBackpack), 0, 3) : 0;
            Refresh();
        }
        if (IsLocal) OwnerUpdate(hands);
        else act = Act.None;
        UpdateLook();
    }

    void Refresh()
    {
        var hands = Hands;
        var now = hands != null ? hands.PackOf(ClientId) : new PackNet { client = ClientId };
        if (haveShown && now.Equals(shown) && packLevel == shownLevel) return;
        shown = now;
        haveShown = true;
        shownLevel = packLevel;
        LayoutLook();
    }

    // ==================================================================================== owner

    void OwnerUpdate(HandsNet hands)
    {
        Local = this;
        if (hands == null || pc == null || carrier == null) { act = Act.None; speedMult = 1f; return; }
        if (Time.unscaledTime >= nextSlotCheck)
        {
            nextSlotCheck = Time.unscaledTime + 0.5f;
            slots = hands.PackSlotsOf(ClientId);
        }
        var pack = hands.PackOf(ClientId);
        speedMult = HaulerRules.PackSpeedMult(HaulerRules.PackCarryKg(pack.plainKg10 / 10f, pack.crushedKg10 / 10f), UpgradeEffects.CarrySlowdownMult);
        if (slots > 0 || pack.Count > 0) EnsureZone();

        if (rider == null) rider = GetComponent<RideRider>();
        bool free = !pc.IsDriving && !pc.IsPlayingChess && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning &&
                    !carrier.ExternalControl && !CompactorCube.IsCubed(ClientId) && !CompactorCube.IsSuctioning(ClientId) &&
                    (rider == null || !rider.IsRiding); // (on a truck's rear step B and C are the rider's keys)
        // The sort station and a scene change take the load off your back (driving / chess / cube are the host's call).
        if (pack.Count > 0 && (carrier.ExternalControl || SceneController.IsTransitioning) && Time.unscaledTime >= nextSpillAsk)
        {
            nextSpillAsk = Time.unscaledTime + 1f;
            hands.LocalPackSpill(false);
        }

        Decide(hands, pack, free);
        if (zone != null) zone.score = act == Act.Unload ? 3.2f : act == Act.StashGrab ? 2.4f : act == Act.TakeOff ? 0.3f : 0f;

        // The second half of "stash and grab": once the host has taken the first bag, pick the next one up as usual.
        if (pendingGrab != null)
        {
            if (Time.time > pendingGrabUntil || pendingGrab.State == CarryState.Consumed || pendingGrab.Holder != null) pendingGrab = null;
            else if (!carrier.IsCarrying && free)
            {
                var next = pendingGrab;
                pendingGrab = null;
                if (next.CanInteract()) next.OnInteract(gameObject);
            }
        }
        if (free && slots > 0) Keys(hands, pack);
        else keyDownAt = -1f;
    }

    void Keys(HandsNet hands, PackNet pack)
    {
        bool canCrush = HaulerRules.PackCanCrush(packLevel);
        if (GameControls.Down(HaulerTuning.PackKey, GameControls.Action.Crush))
        {
            keyDownAt = Time.time;
            crushedThisHold = false;
            tapArmed = Input.GetKeyDown(HaulerTuning.PackKey); // the tap is the keyboard's: on a pad the button is Y (hold only)
        }
        if (keyDownAt >= 0f && GameControls.Held(HaulerTuning.PackKey, GameControls.Action.Crush))
        {
            if (canCrush && Time.time - keyDownAt >= HaulerTuning.CrushHoldSeconds && Time.time >= nextCrush)
            {
                crushedThisHold = true;
                nextCrush = Time.time + HaulerTuning.CrushSecondsPerBag;
                if (pack.plain > 0) hands.LocalPackCrush();
                else
                {
                    GameHUD.FloatingText(transform.position + Vector3.up * 2f, pack.Count > 0 ? "all crushed!" : "nothing to crush", new Color(0.85f, 0.85f, 0.85f), 0.75f);
                    keyDownAt = -1f;
                }
            }
        }
        else if (keyDownAt >= 0f)
        {
            // Released: a short press is the tap (stash what you hold, or take one off).
            bool tap = tapArmed && !crushedThisHold && (!canCrush || Time.time - keyDownAt < HaulerTuning.CrushHoldSeconds);
            keyDownAt = -1f;
            if (tap && Time.time >= busyUntil)
            {
                if (carrier.Held is TrashBag held) { if (hands.LocalCanStash(held)) { busyUntil = Time.time + 0.25f; hands.LocalPackStash(held); } }
                else if (!carrier.IsCarrying && pack.Count > 0) { busyUntil = Time.time + 0.25f; hands.LocalPackTake(); }
            }
        }
        if (HaulerRules.PackEjectsBale(packLevel) && pack.Count > 0 && Input.GetKeyDown(HaulerTuning.EjectKey) && Time.time >= busyUntil)
        {
            busyUntil = Time.time + 0.5f;
            hands.LocalPackSpill(false);
        }
    }

    void Decide(HandsNet hands, PackNet pack, bool free)
    {
        act = Act.None;
        grabTarget = null;
        unloadTarget = null;
        if (!free || Time.time < busyUntil || slots <= 0 && pack.Count == 0) return;
        if (pack.Count > 0)
        {
            var r = CarryReceivers.FindBest(transform.position, transform.forward, carrier.Held);
            if (r is TruckHopper || r is FootHatch hatch && HatchOpenToMe(hatch))
            { act = Act.Unload; unloadTarget = r; return; }
        }
        if (carrier.Held is TrashBag held && hands.LocalCanStash(held) && HaulerRules.PackHasRoom(slots, pack.plain, pack.crushed) &&
            BagTells.Of(held) != BagTell.Heavy)
        {
            grabTarget = FindNearbyBag();
            if (grabTarget != null) { act = Act.StashGrab; return; }
        }
        if (!carrier.IsCarrying && pack.Count > 0) act = Act.TakeOff;
    }

    /// <summary>Is this hatch mine to use (owner of its lot, with the capstone)? What it says about each bag comes later.</summary>
    bool HatchOpenToMe(FootHatch hatch) =>
        hatch != null && Workshop.TryBusinessAt(hatch.lot, out var business) && business.ownerClientId == ClientId && FootHatch.RouteLevel(ClientId) > 0;

    TrashBag FindNearbyBag()
    {
        Vector3 fwd = transform.forward;
        Vector3 feet = transform.position;
        float r = pc.proximityRadius;
        Vector3 center = feet + Vector3.up * 0.6f + fwd * (r * 0.7f);
        int n = Physics.OverlapSphereNonAlloc(center, r, overlap, 1 << Carryable.InteractableLayer, QueryTriggerInteraction.Ignore);
        TrashBag best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var col = overlap[i];
            if (col == null || col.transform.IsChildOf(transform)) continue;
            var b = col.GetComponentInParent<TrashBag>();
            if (b == null || b == carrier.Held || b.HasBurst || b.Holder != null || b.IsInScriptedFlight) continue;
            if (b.State != CarryState.Resting && b.State != CarryState.Thrown) continue;
            if (!Permissions.LocalCanWorkAt(b.transform.position)) continue;
            Vector3 flat = col.ClosestPoint(center) - feet; flat.y = 0f;
            float dist = flat.magnitude;
            float facing = dist < 0.3f ? 1f : Vector3.Dot(fwd, flat / dist);
            if (facing < -0.2f) continue;
            float score = (1f - Mathf.Clamp01(dist / (r * 2f))) + facing;
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    void EnsureZone()
    {
        if (zone != null) return;
        // A zone that rides on the LOCAL player only (zones are global: one on a remote copy would offer itself to us).
        var go = new GameObject("Compactor Pack Zone");
        go.transform.SetParent(transform, false);
        zone = go.AddComponent<InteractionZone>();
        zone.mode = ZoneMode.OnFoot;
        zone.center = Vector3.up;
        zone.size = Vector3.one * 6f;
        zone.score = 0f;
        zone.targetOverride = this;
    }

    // ==================================================================================== IInteractable (the zone publishes it)

    public bool CanInteract() => IsLocal && act != Act.None;

    public string GetInteractionPrompt()
    {
        switch (act)
        {
            case Act.Unload:
            {
                int n = Count + (carrier != null && carrier.Held is TrashBag ? 1 : 0);
                string into = unloadTarget is FootHatch ? "the walk-up hatch" : "the truck";
                return n == 1 ? $"Unload the back bag into {into}" : $"Unload {n} bags into {into}";
            }
            case Act.StashGrab:
                return grabTarget != null ? $"Stash bag on back & grab {grabTarget.DisplayName} ({grabTarget.MassKg:0.#} kg)" : "Stash & grab";
            case Act.TakeOff:
                return "Take a bag off your back";
            default:
                return "";
        }
    }

    public void OnInteract(GameObject interactor)
    {
        var hands = Hands;
        if (hands == null || !IsLocal || carrier == null) return;
        switch (act)
        {
            case Act.StashGrab:
                if (grabTarget == null || !(carrier.Held is TrashBag held) || !hands.LocalCanStash(held)) return;
                pendingGrab = grabTarget;
                pendingGrabUntil = Time.time + 1.5f;
                busyUntil = Time.time + 0.2f;
                hands.LocalPackStash(held);
                break;
            case Act.Unload:
                if (unloadTarget == null) return;
                busyUntil = Time.time + 0.6f;
                if (carrier.Held != null) carrier.TossInto(unloadTarget); // the one in your hands goes the ordinary way
                hands.LocalPackUnload(unloadTarget);
                break;
            case Act.TakeOff:
                busyUntil = Time.time + 0.25f;
                hands.LocalPackTake();
                break;
        }
    }

    public void Highlight(bool highlight)
    {
        var want = highlight && act == Act.StashGrab ? grabTarget : null;
        if (highlighted != null && highlighted != want) highlighted.Highlight(false);
        highlighted = want;
        if (want != null) want.Highlight(true);
    }

    // ==================================================================================== HUD (the local hotbar asks)

    /// <summary>The local player's pack line for the pocket bar ("3/7"); false = no back slots at all.</summary>
    public static bool Hotbar(out string detail, out bool ready)
    {
        detail = ""; ready = false;
        var p = Local;
        if (p == null || p.slots <= 0 && p.Count == 0) return false;
        int max = HaulerRules.PackMaxBags(p.slots, HaulerRules.PackCanCrush(p.packLevel));
        ready = HaulerRules.PackHasRoom(p.slots, p.shown.plain, p.shown.crushed);
        detail = ready ? $"{p.Count}/{max}" : p.shown.plain > 0 && HaulerRules.PackCanCrush(p.packLevel) ? "CRUSH IT!" : "FULL";
        return true;
    }

    // ==================================================================================== moments (every copy)

    void OnPackFx(byte kind, ulong client, Vector3 a, Vector3 b)
    {
        if (pc == null || client != ClientId) return;
        Vector3 back = transform.position + Vector3.up * 1.2f - transform.forward * 0.35f;
        switch (kind)
        {
            case HandsNet.PackFxStash:
                Sfx.Play("rustle", back, 0.7f, 0.9f);
                Sfx.Play("whoosh", back, 0.5f, 1.3f);
                StartCoroutine(FlyLump(a, null, 0.2f));
                if (stack != null) Juice.Squash(stack, 0.2f, 0.25f);
                if (IsLocal) GameHUD.FloatingText(back + Vector3.up * 0.7f, "*onto the back*", new Color(1f, 0.85f, 0.4f), 0.8f);
                break;
            case HandsNet.PackFxTake:
                Sfx.Play("pickup", back, 0.6f, 1.1f);
                break;
            case HandsNet.PackFxCrush:
                pistonKick = 1f;
                Sfx.Play("crunch", back, 0.9f, 0.9f);
                Sfx.Play("slam", back, 0.5f, 1.4f);
                Fx.Dust(back, 0.7f);
                Fx.Burst(back, new Color(0.2f, 0.2f, 0.22f), 8, 2.5f);
                if (stack != null) Juice.Squash(stack, 0.35f, 0.25f);
                GameHUD.FloatingText(back + Vector3.up * 0.6f, ShopModKit.Pick(CrunchWords), new Color(1f, 0.6f, 0.2f), 1f);
                if (IsLocal) Juice.Shake(0.1f, 0.12f);
                break;
            case HandsNet.PackFxUnload:
                Sfx.Play("whoosh", a, 0.6f, 1.2f);
                StartCoroutine(FlyLump(a, b, 0.22f));
                break;
            case HandsNet.PackFxSpill:
                Sfx.Play("drop", a, 0.8f, 0.9f);
                Fx.Dust(a - Vector3.up * 0.8f, b.x > 0.5f ? 1.6f : 0.9f);
                GameHUD.FloatingText(a + Vector3.up, b.x > 0.5f ? "BAGS EVERYWHERE!" : "*the load slides off*", new Color(0.9f, 0.9f, 0.9f), b.x > 0.5f ? 1.3f : 0.85f);
                break;
            case HandsNet.PackFxYank:
                Sfx.Play("whoosh", a, 0.7f, 1.5f);
                Fx.Dust(a, 0.6f);
                Fx.Dust(b, 0.5f);
                break;
        }
    }

    static readonly string[] CrunchWords = { "CRUNCH!", "SKRONCH!", "KA-CHUNK!", "*squish*" };

    /// <summary>A cosmetic sack flying into the pack (to == null) or from it to a receiver.</summary>
    IEnumerator FlyLump(Vector3 from, Vector3? to, float seconds)
    {
        if (sackMat == null) sackMat = ShopModKit.Lit("mat/hauler_pack_sack", new Color(0.07f, 0.07f, 0.08f), 0.35f);
        var lump = ShopMaterials.Primitive(PrimitiveType.Sphere, "Pack lump (fx)", null, sackMat);
        lump.localScale = new Vector3(0.36f, 0.3f, 0.34f);
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            float k = t / seconds;
            Vector3 end = to ?? transform.position + Vector3.up * 1.25f - transform.forward * 0.35f;
            Vector3 p = Vector3.Lerp(from, end, k);
            p.y += Mathf.Sin(k * Mathf.PI) * 0.5f;
            lump.position = p;
            lump.localScale = new Vector3(0.36f, 0.3f, 0.34f) * Mathf.Lerp(1f, 0.6f, k);
            yield return null;
        }
        Destroy(lump.gameObject);
    }

    // ==================================================================================== looks (every copy)

    void BuildLook()
    {
        // Under the Skeleton node so it lies down with the body; pre-scale numbers (the Player root is 0.8).
        Transform parent = transform.Find("Skeleton");
        if (parent == null) parent = transform;
        root = ShopModKit.Pivot("Compactor Pack", parent, new Vector3(0f, 1.2f, -0.3f));
        var orange = ShopModKit.Lit("mat/hauler_pack_orange", new Color(1f, 0.55f, 0.1f), 0.4f);
        var steel = ShopModKit.Lit("mat/hauler_pack_steel", new Color(0.62f, 0.65f, 0.7f), 0.6f, 0.6f);
        var dark = ShopModKit.Lit("mat/hauler_pack_dark", new Color(0.12f, 0.12f, 0.14f), 0.3f);
        sackMat = ShopModKit.Lit("mat/hauler_pack_sack", new Color(0.07f, 0.07f, 0.08f), 0.35f);
        brickMat = ShopModKit.Lit("mat/hauler_pack_brick", new Color(0.16f, 0.17f, 0.2f), 0.5f, 0.2f);

        frame = ShopModKit.Pivot("Frame", root, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cube, "Backplate", frame, orange, new Vector3(0f, 0f, 0.06f), new Vector3(0.4f, 0.46f, 0.05f));
        ShopModKit.Prim(PrimitiveType.Cube, "Floor", frame, steel, new Vector3(0f, -0.22f, -0.09f), new Vector3(0.42f, 0.04f, 0.34f));
        foreach (float x in new[] { -0.2f, 0.2f })
        {
            ShopModKit.Prim(PrimitiveType.Cube, "Rail", frame, steel, new Vector3(x, 0.02f, -0.24f), new Vector3(0.03f, 0.5f, 0.03f));
            ShopModKit.Prim(PrimitiveType.Cube, "Side", frame, dark, new Vector3(x, -0.12f, -0.09f), new Vector3(0.02f, 0.18f, 0.32f));
            ShopModKit.Prim(PrimitiveType.Cube, "Strap", frame, dark, new Vector3(x * 0.7f, 0.05f, 0.2f), new Vector3(0.06f, 0.42f, 0.03f), new Vector3(-12f, 0f, 0f));
        }
        crusher = ShopModKit.Pivot("Crusher", root, new Vector3(0f, 0.3f, -0.09f));
        ShopModKit.Prim(PrimitiveType.Cube, "Gantry", crusher, orange, new Vector3(0f, 0.02f, 0.13f), new Vector3(0.44f, 0.05f, 0.05f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Ram", crusher, steel, new Vector3(0f, 0.08f, 0.13f), new Vector3(0.07f, 0.08f, 0.07f));
        piston = ShopModKit.Prim(PrimitiveType.Cube, "Press plate", crusher, steel, new Vector3(0f, 0f, 0f), new Vector3(0.38f, 0.03f, 0.3f));

        stack = ShopModKit.Pivot("Stack", root, new Vector3(0f, -0.2f, -0.09f));
        for (int i = 0; i < MaxLumps; i++)
        {
            var lump = ShopMaterials.Primitive(PrimitiveType.Sphere, "Bag " + i, stack, sackMat);
            lump.gameObject.SetActive(false);
            lumps.Add(lump);
        }
        foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
    }

    /// <summary>Lay the stack out for the current counts: bricks at the bottom, plain sacks on top, two columns.</summary>
    void LayoutLook()
    {
        int count = Count;
        bool want = packLevel > 0 || count > 0;
        if (root == null)
        {
            if (!want) return;
            BuildLook();
        }
        root.gameObject.SetActive(want);
        if (!want) return;
        frame.gameObject.SetActive(packLevel > 0); // gloves-only back slots: just the bags, slung over the shoulder
        crusher.gameObject.SetActive(HaulerRules.PackCanCrush(packLevel));
        if (cubeMesh == null) cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        if (sphereMesh == null) sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        float[] top = { 0.02f, 0.02f };
        for (int i = 0; i < lumps.Count; i++)
        {
            var lump = lumps[i];
            bool on = i < count;
            if (lump.gameObject.activeSelf != on) lump.gameObject.SetActive(on);
            if (!on) continue;
            bool brick = i < shown.crushed;
            int column = i % 2;
            float height = brick ? 0.1f : 0.21f;
            var filter = lump.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = brick ? cubeMesh : sphereMesh;
            var rend = lump.GetComponent<Renderer>();
            if (rend != null) rend.sharedMaterial = brick ? brickMat : sackMat;
            lump.localScale = brick ? new Vector3(0.2f, 0.09f, 0.26f) : new Vector3(0.23f, 0.22f, 0.27f);
            lump.localPosition = new Vector3(column == 0 ? -0.105f : 0.105f, top[column] + height * 0.5f, (i % 3 - 1) * 0.012f);
            lump.localRotation = Quaternion.Euler(0f, (i * 37) % 30 - 15, brick ? 0f : (i * 53) % 16 - 8);
            top[column] += height;
        }
        // The press sits just above the tallest column.
        if (crusher != null) crusher.localPosition = new Vector3(0f, -0.2f + Mathf.Max(0.3f, Mathf.Max(top[0], top[1]) + 0.06f), -0.09f);
    }

    void UpdateLook()
    {
        if (root == null || !root.gameObject.activeSelf) return;
        // The taller the load, the more it sways as you move.
        int count = Count;
        var blades = GetComponent<RocketBlades>();
        float speed = blades != null ? blades.Speed : 0f;
        wobble += Time.deltaTime * (3f + speed * 1.2f);
        float amount = Mathf.Clamp01(count / 8f) * Mathf.Clamp01(0.25f + speed / 6f);
        if (stack != null) stack.localRotation = Quaternion.Euler(Mathf.Sin(wobble * 0.9f) * 3.5f * amount, 0f, Mathf.Sin(wobble) * 6f * amount);
        if (piston != null)
        {
            pistonKick = Mathf.MoveTowards(pistonKick, 0f, Time.deltaTime * 3f);
            piston.localPosition = new Vector3(0f, -0.16f * Mathf.Sin(pistonKick * Mathf.PI), 0f);
        }
    }
}

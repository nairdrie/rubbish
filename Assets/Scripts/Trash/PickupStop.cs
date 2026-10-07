using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A house's curb (every house in the Neighborhood has one). Placed by NeighborhoodBuilder; runtime owned by
/// the Trash system. FROZEN CONTRACT (fields are set by the builder):
///   Transform position = the curb spot on the sidewalk where bags sit (y = sidewalk top).
///   Transform forward  = points from the house toward the road.
///   stopId / address   = stable per house. bagCount = the curb CAPACITY (3–6 bags by house size).
///   wealth             = loot richness. onRoute = obsolete (every house is on the living route: always true).
///   districtId         = the district the house belongs to (Districts.*; empty = from the stopId prefix, i.e. the
///                        starter suburb). Its DistrictDef sets the lateness / neglect windows and the pay.
///
/// Living world: houses put bags out at random times (CurbTrashSpawner → GameSession.CurbBags). This stop
/// materializes its house's records as TrashBags in curb slots (tidy rows; a neglected pile is heaped up
/// and stinks), keeps them in sync as records come and go (a bag put out while you're nearby drops in with
/// a thud), and shows a StopMarker colored by freshness while it has trash. The record is removed by the
/// truck hopper (pays the lateness-scaled fee) or when a bag bursts.
/// </summary>
public class PickupStop : MonoBehaviour
{
    public string stopId = "stop";
    public string address = "1 Maple St";
    [Tooltip("Curb capacity: the most bags this house has out at once (3–6 by house size). A full curb stops putting out more.")]
    [Range(0, 6)] public int bagCount = 4;
    [Range(0f, 1f)] public float wealth = 0.3f;
    [Tooltip("Obsolete: every house is on the living route (treated as always true).")]
    public bool onRoute = true;
    [Tooltip("District id (Districts.MildlyPleasant ...). Empty = derived from the stopId prefix (mm- / sh- / bb-).")]
    public string districtId = "";
    [Tooltip("The house's wheelie bin prop (optional): carries the rival's gold-star sticker while the house doesn't subscribe.")]
    public Transform bin;

    [Header("Runtime placement")]
    [Tooltip("Spacing between bags along the curb (transform.right).")]
    public float bagSpacing = 0.8f;
    [Tooltip("How far the bags sit back toward the house (along -forward).")]
    public float setBack = 0.3f;
    [Tooltip("Height of the floating marker above the curb.")]
    public float markerHeight = 3.2f;

    /// <summary>Most curb slots a house can have.</summary>
    public const int MaxSlots = 6;

    // ------------------------------------------------------------------ registry

    static readonly List<PickupStop> all = new List<PickupStop>();
    /// <summary>Every PickupStop in loaded scenes (critters, HUD).</summary>
    public static IReadOnlyList<PickupStop> All => all;

    /// <summary>
    /// Co-op (N1, WorldState): true for a curb record someone ELSE is carrying right now. Such records count (they're still
    /// out) but get no TrashBag at the curb on this machine. Null = nothing is hidden (solo, menus).
    /// </summary>
    public static System.Func<string, bool> HiddenHere;

    static bool IsHidden(string bagId)
    {
        var f = HiddenHere;
        if (f == null || string.IsNullOrEmpty(bagId)) return false;
        try { return f(bagId); }
        catch (System.Exception e) { Debug.LogException(e); return false; }
    }

    public static PickupStop Find(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return null;
        for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].stopId == stopId) return all[i];
        return null;
    }

    // ------------------------------------------------------------------ state (from GameSession.CurbBags)

    /// <summary>Curb capacity (bags). bagCount clamped to 1..MaxSlots.</summary>
    public int Capacity => Mathf.Clamp(bagCount, 1, MaxSlots);

    /// <summary>This house's district (its windows, pay and loot). Never null.</summary>
    public DistrictDef District => !string.IsNullOrEmpty(districtId) ? Districts.Get(districtId) : Districts.ForStopId(stopId);
    /// <summary>The district is open to the company (permit owned, or the starter suburb).</summary>
    public bool DistrictUnlocked => Districts.IsUnlocked(District);
    /// <summary>The house subscribes to Rubbish Co. right now (Trust-driven; locked districts never do).</summary>
    public bool IsSubscribed => Progression.IsSubscribed(stopId);

    /// <summary>Bags this house has out that haven't reached the truck (or burst) yet — including one you're carrying.</summary>
    public int BagsRemaining => CurbTrash.StatsFor(stopId).bags;
    public bool HasTrash => BagsRemaining > 0;
    /// <summary>Curb is clean right now (no bags out). Becomes false again when the house puts more out.</summary>
    public bool IsCleared => !HasTrash;
    public bool IsFull => BagsRemaining >= Capacity;

    /// <summary>Age (in-game hours) of this house's oldest bag; 0 when clean.</summary>
    public double OldestBagAgeHours => CurbTrash.StatsFor(stopId).oldestAgeHours;
    public int LateBagCount => CurbTrash.StatsFor(stopId).lateBags;
    public bool IsLate => HasTrash && CurbTrash.IsLate(District, OldestBagAgeHours);
    /// <summary>Oldest bag older than the district's neglect window (16 h in the starter suburb): stinks, heaped, marker
    /// red, Pile Goblin nest candidate.</summary>
    public bool IsNeglected => HasTrash && CurbTrash.IsNeglected(District, OldestBagAgeHours);
    /// <summary>Route-planner urgency (sum over the bags; 0 when clean).</summary>
    public float Urgency => CurbTrash.StatsFor(stopId).urgency;
    /// <summary>Total kg sitting at this curb.</summary>
    public float CurbKg => CurbTrash.StatsFor(stopId).kg;
    /// <summary>Marker color for this stop right now (by its oldest bag).</summary>
    public Color FreshnessColor => CurbTrash.FreshnessColor(District, OldestBagAgeHours);
    /// <summary>"3 bags · 2 h late".</summary>
    public string StatusLabel => CurbTrash.StopLabel(District, BagsRemaining, OldestBagAgeHours);

    /// <summary>Id of the bag in curb slot i, or null if that slot is empty.</summary>
    public string BagId(int i)
    {
        var bags = GameSession.CurbBags;
        for (int k = 0; k < bags.Count; k++)
            if (bags[k] != null && bags[k].stopId == stopId && bags[k].slot == i) return bags[k].bagId;
        return null;
    }

    /// <summary>This house's curb records, sorted by slot.</summary>
    public void GetCurbBags(List<CurbBag> into) => CurbTrash.BagsAt(stopId, into);

    /// <summary>Middle of the bag pile on the sidewalk (ground height).</summary>
    public Vector3 PileCenter => transform.position - transform.forward * (setBack + 0.3f);

    readonly List<TrashBag> spawnedBags = new List<TrashBag>();
    /// <summary>Curb bags this stop has materialized that are still around (may include ones the player moved or carries).</summary>
    public IReadOnlyList<TrashBag> SpawnedBags { get { Prune(); return spawnedBags; } }

    public StopMarker Marker { get; private set; }

    const int GroundLayerMask = 1 << 6;
    static readonly List<CurbBag> tmpRecords = new List<CurbBag>();
    static readonly HashSet<string> tmpIds = new HashSet<string>();

    bool materialized;
    bool heaped;           // current layout is the neglected heap
    Transform stinkT;
    float nextCheck;
    bool dirty;

    // ------------------------------------------------------------------ WorldLife walk-out hook (Docs/WorldLife.md §7.2, W0)

    /// <summary>
    /// Optional put-out presenter (WorldLife residents carry the bag out). SyncBags asks it about every record that has no
    /// TrashBag yet and is younger than <see cref="PresentWindowHours"/>: return true to take the record (a resident walks
    /// it to the curb and calls <see cref="MaterializeRecord"/> at set-down); false = today's behaviour (the drop-in pop
    /// when fresh, else straight into the slot). Null (the default) = exactly today's behaviour. The record is still the
    /// only truth: it exists, counts and ages from its own timestamp the whole time.
    /// </summary>
    public static System.Func<PickupStop, CurbBag, bool> PutOutPresenter;
    /// <summary>Records younger than this (in-game hours) are offered to <see cref="PutOutPresenter"/> (a walk-out shown
    /// mid-walk on scene load included).</summary>
    public const double PresentWindowHours = 0.5;
    /// <summary>A presenter that hasn't materialized its record after this many real seconds loses it (the bag appears).</summary>
    public const float PresentTimeoutSeconds = 90f;

    readonly Dictionary<string, float> presenting = new Dictionary<string, float>();   // bagId -> Time.time deadline
    readonly HashSet<string> noPresent = new HashSet<string>();                         // timed out: never offered again
    static readonly List<string> tmpPresent = new List<string>();
    float nextPresentCheck;

    /// <summary>True while a presenter owns this record (no TrashBag yet; SyncBags leaves it alone).</summary>
    public bool IsPresenting(string bagId) => bagId != null && presenting.ContainsKey(bagId);

    /// <summary>
    /// Spawn the real TrashBag for one of this house's records and stop presenting it (idempotent: returns the existing
    /// bag if it already has one; null if the record is gone — Kevin's sweep, a debug clear, another player).
    /// <paramref name="at"/> null = in its curb slot (no drop-in pop; the marker bumps and "Fresh trash!" floats);
    /// otherwise at that world point with velocity <paramref name="vel"/> (a flattened resident's bag flying out of their
    /// arms). <paramref name="intoHands"/>: spawned still and quiet at <paramref name="at"/> (or the pile) for the caller to
    /// hand to a TrashCarrier ("Take bag").
    /// </summary>
    public TrashBag MaterializeRecord(CurbBag rec, Vector3? at, Vector3 vel, bool intoHands)
    {
        if (rec == null || string.IsNullOrEmpty(rec.bagId)) return null;
        presenting.Remove(rec.bagId);
        if (rec.contents == null || GameSession.FindCurbBag(rec.bagId) == null) return null;
        Prune();
        for (int i = 0; i < spawnedBags.Count; i++)
            if (spawnedBags[i].Contents != null && spawnedBags[i].Contents.bagId == rec.bagId) return spawnedBags[i];

        TrashBag bag;
        if (at == null && !intoHands)
        {
            bag = SpawnRecord(rec, false);
            if (bag != null) OnFreshTrash();
        }
        else
        {
            Vector3 p = at ?? PileCenter + Vector3.up * 0.4f;
            float yaw = (uint)PickupVisuals.StableHash(rec.bagId) % 360u;   // stable per bag, no RNG
            bag = TrashBag.Spawn(rec.contents, p, Quaternion.Euler(0f, yaw, 0f), intoHands ? Vector3.zero : vel);
            if (bag != null)
            {
                bag.MarkCurbVisual(this);
                spawnedBags.Add(bag);
            }
        }
        if (Marker != null) Marker.Refresh();
        return bag;
    }

    bool OfferToPresenter(CurbBag rec)
    {
        var presenter = PutOutPresenter;
        if (presenter == null || noPresent.Contains(rec.bagId)) return false;
        if (GameClock.AgeHours(rec.putOutAtHours) >= PresentWindowHours) return false;
        // Mark it first: a presenter may call MaterializeRecord straight away (which un-marks it).
        presenting[rec.bagId] = Time.time + PresentTimeoutSeconds;
        bool taken;
        try { taken = presenter(this, rec); }
        catch (System.Exception e) { Debug.LogException(e); taken = false; }
        if (!taken) presenting.Remove(rec.bagId);
        return taken;
    }

    /// <summary>Forget presented records that vanished; time out stuck ones (they get a bag on this sync).</summary>
    void PrunePresenting()
    {
        if (presenting.Count == 0 && noPresent.Count == 0) return;
        tmpPresent.Clear();
        foreach (var kv in presenting)
            if (!tmpIds.Contains(kv.Key) || Time.time > kv.Value) tmpPresent.Add(kv.Key);
        foreach (var id in tmpPresent)
        {
            if (tmpIds.Contains(id)) noPresent.Add(id);   // timed out: the bag appears now
            presenting.Remove(id);
        }
        if (noPresent.Count > 0)
        {
            tmpPresent.Clear();
            foreach (var id in noPresent) if (!tmpIds.Contains(id)) tmpPresent.Add(id);
            foreach (var id in tmpPresent) noPresent.Remove(id);
        }
    }

    // ------------------------------------------------------------------ slots

    struct SlotDef { public float along, back; public bool top; public SlotDef(float a, float b, bool t = false) { along = a; back = b; top = t; } }

    // Tidy: a row along the curb, a second row a step back toward the house. Meters (along scales with bagSpacing/0.8).
    static readonly SlotDef[] TidySlots =
    {
        new SlotDef(0f, 0f), new SlotDef(0.8f, 0f), new SlotDef(-0.8f, 0f),
        new SlotDef(0.4f, 0.7f), new SlotDef(-0.4f, 0.7f), new SlotDef(1.2f, 0.7f),
    };
    // Neglected heap: shoved together, two bags piled on top, one slumped behind.
    static readonly SlotDef[] HeapSlots =
    {
        new SlotDef(0f, 0.05f), new SlotDef(0.52f, 0f), new SlotDef(-0.52f, 0.08f),
        new SlotDef(0.26f, 0.12f, true), new SlotDef(-0.26f, 0.1f, true), new SlotDef(0.05f, 0.58f),
    };

    /// <summary>Desired (unsnapped) world position of a curb slot.</summary>
    public Vector3 SlotPosition(int slot, bool heap)
    {
        var defs = heap ? HeapSlots : TidySlots;
        var d = defs[Mathf.Clamp(slot, 0, defs.Length - 1)];
        float along = d.along * (heap ? 1f : bagSpacing / 0.8f);
        return transform.position + transform.right * along - transform.forward * (setBack + d.back);
    }

    // ------------------------------------------------------------------ lifecycle

    protected virtual void Awake()
    {
        onRoute = true; // obsolete flag: every house is on the living route
        if (!all.Contains(this)) all.Add(this);
    }

    protected virtual void Start()
    {
        GameSession.CurbTrashChanged += OnCurbTrashChanged;
        Marker = StopMarker.Create(this);
        heaped = IsNeglected;
        SyncBags(false);
        materialized = true;
        UpdateStink();
    }

    protected virtual void OnDestroy()
    {
        all.Remove(this);
        GameSession.CurbTrashChanged -= OnCurbTrashChanged;
        if (Marker != null)
        {
            if (Application.isPlaying) Destroy(Marker.gameObject);
            else DestroyImmediate(Marker.gameObject);
        }
    }

    void OnCurbTrashChanged()
    {
        // Several records can change in one frame (catch-up, a 2-bag put-out): sync once, next Update.
        dirty = true;
    }

    void Update()
    {
        if (presenting.Count > 0 && Time.time >= nextPresentCheck)
        {
            nextPresentCheck = Time.time + 1f;
            foreach (var kv in presenting) if (Time.time > kv.Value) { dirty = true; break; }
        }
        if (dirty)
        {
            dirty = false;
            SyncBags(materialized);
        }
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.75f + Random.value * 0.25f;
        UpdateSticker();

        bool neg = IsNeglected;
        if (neg != heaped)
        {
            heaped = neg;
            if (neg) BecomeNeglected();
        }
        UpdateStink();
    }

    // ------------------------------------------------------------------ materialize

    /// <summary>Legacy name: make sure every curb record at this house has a TrashBag. Safe to call any time.</summary>
    public void SpawnTodaysBags() => SyncBags(false);

    void Prune() => spawnedBags.RemoveAll(b => b == null || b.State == CarryState.Consumed || b.HasBurst);

    /// <summary>Spawn TrashBags for records that have none; remove resting bags whose record is gone.</summary>
    void SyncBags(bool juicy)
    {
        Prune();
        CurbTrash.BagsAt(stopId, tmpRecords);
        // Co-op: a bag another player is carrying is gone from this curb (it's in their hands).
        if (HiddenHere != null) tmpRecords.RemoveAll(r => IsHidden(r.bagId));
        tmpIds.Clear();
        foreach (var r in tmpRecords) tmpIds.Add(r.bagId);

        // Records that vanished (picked up elsewhere, debug clear, critter...): tidy away bags still at the curb.
        bool guest = GameSession.IsConnectedGuest;
        for (int i = spawnedBags.Count - 1; i >= 0; i--)
        {
            var b = spawnedBags[i];
            string id = b.Contents != null ? b.Contents.bagId : null;
            if (id != null && tmpIds.Contains(id)) continue;
            spawnedBags.RemoveAt(i);
            if (b.SourceStop == this) b.SourceStop = null;
            if (b.State == CarryState.Resting && !b.IsInScriptedFlight) Juice.ShrinkAndDestroy(b.gameObject, 0.25f);
            else if (guest && b.State != CarryState.Consumed && !b.IsInScriptedFlight && (id == null || GameSession.FindCurbBag(id) == null))
            {
                // A guest's copy of a bag the host no longer has (swept, burst, collected): it can't be delivered, so it goes.
                b.MarkConsumed();
                Juice.ShrinkAndDestroy(b.gameObject, 0.25f);
            }
        }

        var present = new HashSet<string>();
        foreach (var b in spawnedBags) if (b.Contents != null) present.Add(b.Contents.bagId);
        PrunePresenting();

        bool anyNew = false;
        foreach (var rec in tmpRecords)
        {
            if (rec.contents == null || present.Contains(rec.bagId)) continue;
            // WorldLife: a resident is carrying this one out (or is about to): no bag until they set it down.
            if (presenting.ContainsKey(rec.bagId) || OfferToPresenter(rec)) continue;
            bool fresh = juicy && GameClock.AgeHours(rec.putOutAtHours) < 0.35;
            if (SpawnRecord(rec, fresh) != null) anyNew |= fresh;
        }
        if (anyNew) OnFreshTrash();
        if (Marker != null) Marker.Refresh();
    }

    TrashBag SpawnRecord(CurbBag rec, bool dropIn)
    {
        bool heap = heaped;
        var rng = new System.Random(PickupVisuals.StableHash(rec.bagId) ^ 0x5f3759df);
        Vector3 desired = SlotPosition(rec.slot, heap);
        desired += transform.right * (((float)rng.NextDouble() - 0.5f) * (heap ? 0.12f : 0.2f))
                 - transform.forward * (((float)rng.NextDouble() - 0.5f) * 0.15f);
        bool top = heap && HeapSlots[Mathf.Clamp(rec.slot, 0, HeapSlots.Length - 1)].top;
        Vector3 pos = top ? OnTopOfPile(desired) : FindFreeSpot(desired);
        float yaw = (float)rng.NextDouble() * 360f;
        Quaternion rot = heap
            ? Quaternion.Euler(((float)rng.NextDouble() - 0.5f) * 36f, yaw, ((float)rng.NextDouble() - 0.5f) * 36f)
            : Quaternion.Euler(0f, yaw, 0f);

        var bag = TrashBag.Spawn(rec.contents, pos + Vector3.up * (dropIn ? 0.9f : 0.02f), rot, dropIn ? Vector3.down * 0.5f : Vector3.zero);
        if (bag == null) return null;
        bag.MarkCurbVisual(this);
        spawnedBags.Add(bag);
        if (dropIn)
        {
            Juice.PopIn(bag.JuiceTarget, 0.3f);
            Sfx.Play("rustle", pos, 0.6f, Random.Range(1f, 1.2f));
        }
        else if (bag.Body != null && !top) bag.Body.Sleep();
        return bag;
    }

    /// <summary>Juice for trash that just went out while the scene is loaded.</summary>
    void OnFreshTrash()
    {
        if (Marker != null) Marker.Bump();
        Vector3 p = PileCenter + Vector3.up * 1.4f;
        var cam = Camera.main;
        bool near = cam != null && (cam.transform.position - p).sqrMagnitude < 45f * 45f;
        if (near)
        {
            int n = BagsRemaining;
            GameHUD.FloatingText(p, IsFull ? "Curb's full!" : n == 1 ? "Fresh trash!" : $"+1 bag ({n})", CurbTrash.FreshColor, 0.85f);
            Sfx.Play("pop", p, 0.45f, Random.Range(1.1f, 1.3f));
        }
    }

    // ------------------------------------------------------------------ neglected piles

    void BecomeNeglected()
    {
        // Untouched bags slump into a heap: "this has been here a while".
        Prune();
        tmpRecords.Clear();
        CurbTrash.BagsAt(stopId, tmpRecords);
        bool any = false;
        foreach (var b in spawnedBags)
        {
            if (b.State != CarryState.Resting || b.IsInScriptedFlight || b.LastReleaseTime > -900f || b.Contents == null) continue;
            int slot = -1;
            foreach (var r in tmpRecords) if (r.bagId == b.Contents.bagId) { slot = r.slot; break; }
            if (slot < 0) continue;
            var hs = HeapSlots[Mathf.Clamp(slot, 0, HeapSlots.Length - 1)];
            Vector3 p = hs.top ? OnTopOfPile(SlotPosition(slot, true)) : FindFreeSpot(SlotPosition(slot, true));
            b.transform.position = p + Vector3.up * (hs.top ? 0.15f : 0.03f);
            b.transform.rotation = Quaternion.Euler(Random.Range(-18f, 18f), b.transform.eulerAngles.y + Random.Range(-40f, 40f), Random.Range(-18f, 18f));
            if (b.Body != null && !b.Body.isKinematic) { b.Body.linearVelocity = Vector3.zero; b.Body.WakeUp(); }
            Juice.Squash(b.JuiceTarget, 0.3f, 0.3f);
            any = true;
        }
        Vector3 c = PileCenter;
        if (any) { Fx.Dust(c, 1f); Sfx.Play("splat", c, 0.35f, 0.7f); }
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - c).sqrMagnitude < 50f * 50f)
            GameHUD.FloatingText(c + Vector3.up * 1.6f, "EWW. It's been sitting here.", CurbTrash.NeglectedColor, 0.85f);
        if (Marker != null) Marker.Bump();
    }

    void UpdateStink()
    {
        bool want = IsNeglected;
        if (want && stinkT == null)
        {
            var go = new GameObject("PileStink");
            go.transform.SetParent(transform, false);
            go.transform.position = PileCenter + Vector3.up * 0.55f;
            stinkT = go.transform;
            Fx.Stink(stinkT);
        }
        else if (!want && stinkT != null)
        {
            Destroy(stinkT.gameObject);
            stinkT = null;
        }
    }

    // ------------------------------------------------------------------ GoldStar sticker (lore: the rival company)

    Transform sticker;
    float nextStickerCheck;

    /// <summary>Unsubscribed houses (and every house in a locked district) wear the rival's gold-star sticker on their bin.</summary>
    void UpdateSticker()
    {
        if (bin == null || Time.time < nextStickerCheck) return;
        nextStickerCheck = Time.time + 2.5f;
        bool rival = !IsSubscribed;
        if (rival && sticker == null) sticker = BuildSticker(bin);
        if (sticker != null && sticker.gameObject.activeSelf != rival)
        {
            sticker.gameObject.SetActive(rival);
            if (!rival)
            {
                // The house just signed up with us: the sticker pops off with a little celebration.
                Vector3 p = sticker.position;
                Fx.Burst(p, new Color(1f, 0.85f, 0.2f), 10, 2.5f);
                var cam = Camera.main;
                if (cam != null && (cam.transform.position - p).sqrMagnitude < 40f * 40f)
                    GameHUD.FloatingText(p + Vector3.up * 0.8f, "Switched to Rubbish Co.!", CurbTrash.FreshColor, 0.8f);
            }
        }
    }

    static Transform BuildSticker(Transform bin)
    {
        // Measure the bin so the sticker sits on its road-facing side (the builder turns bins to face the house: +Z).
        var rends = bin.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return null;
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        Vector3 toRoad = -bin.forward; toRoad.y = 0f;
        toRoad = toRoad.sqrMagnitude > 0.01f ? toRoad.normalized : Vector3.back;
        float radius = Mathf.Min(b.extents.x, b.extents.z);
        var root = new GameObject("GoldStarSticker").transform;
        root.SetParent(bin, true);
        root.SetPositionAndRotation(new Vector3(b.center.x, b.min.y + b.size.y * 0.6f, b.center.z) + toRoad * (radius + 0.015f),
                                    Quaternion.LookRotation(-toRoad, Vector3.up));
        var mat = PickupVisuals.MarkerMaterial;
        // A chunky gold star: two overlapping squares turned 45° apart.
        for (int i = 0; i < 2; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(s.GetComponent<Collider>());
            s.name = "Star" + i;
            s.transform.SetParent(root, false);
            s.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            s.transform.localRotation = Quaternion.Euler(0f, 0f, 45f * i);
            s.transform.localScale = new Vector3(0.17f, 0.17f, 0.012f);
            var mr = s.GetComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            PickupVisuals.SetColor(mr, new Color(1f, 0.8f, 0.15f));
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        var lab = new GameObject("Label").AddComponent<TMPro.TextMeshPro>();
        lab.transform.SetParent(root, false);
        lab.transform.localPosition = new Vector3(0f, -0.12f, -0.01f);
        lab.text = "GOLDSTAR\n<size=55%>Premium Waste</size>";
        lab.fontSize = 0.7f;
        lab.alignment = TMPro.TextAlignmentOptions.Center;
        lab.color = new Color(1f, 0.85f, 0.25f);
        lab.fontStyle = TMPro.FontStyles.Bold;
        lab.outlineWidth = 0.2f;
        lab.outlineColor = new Color32(30, 20, 0, 255);
        lab.rectTransform.sizeDelta = new Vector2(0.9f, 0.3f);
        return root;
    }

    // ------------------------------------------------------------------ placement helpers

    /// <summary>Ground point near desired that isn't inside a bin/fence/etc (nudges along the curb if blocked).</summary>
    Vector3 FindFreeSpot(Vector3 desired)
    {
        Vector3[] tries =
        {
            Vector3.zero, transform.right * 0.45f, -transform.right * 0.45f, transform.forward * 0.4f,
            transform.right * 0.9f, -transform.right * 0.9f, transform.forward * 0.4f + transform.right * 0.5f,
            -transform.forward * 0.5f,
        };
        int blockMask = Physics.DefaultRaycastLayers & ~GroundLayerMask & ~(1 << Carryable.InteractableLayer);
        foreach (var t in tries)
        {
            Vector3 p = SnapToGround(desired + t);
            bool blocked = Physics.CheckBox(p + Vector3.up * 0.42f, new Vector3(0.26f, 0.32f, 0.26f), Quaternion.identity, blockMask, QueryTriggerInteraction.Ignore);
            if (!blocked) return p;
        }
        return SnapToGround(desired);
    }

    /// <summary>Resting spot on top of the bags below (falls back to the ground).</summary>
    Vector3 OnTopOfPile(Vector3 desired)
    {
        Physics.SyncTransforms();
        Vector3 origin = desired + Vector3.up * 2.5f;
        int mask = GroundLayerMask | (1 << Carryable.InteractableLayer);
        var hits = Physics.RaycastAll(origin, Vector3.down, 4f, mask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Vector3 p = SnapToGround(desired);
        foreach (var h in hits)
        {
            bool isBag = h.collider.GetComponentInParent<TrashBag>() != null;
            bool isGround = h.collider.gameObject.layer == 6;
            if (!isBag && !isGround) continue;
            if (h.distance < best) { best = h.distance; p = h.point; }
        }
        return p;
    }

    Vector3 SnapToGround(Vector3 p)
    {
        Vector3 origin = p + Vector3.up * 1.5f;
        if (Physics.Raycast(origin, Vector3.down, out var hit, 4f, GroundLayerMask, QueryTriggerInteraction.Ignore))
            return hit.point;
        int anyMask = Physics.DefaultRaycastLayers & ~(1 << Carryable.InteractableLayer);
        if (Physics.Raycast(origin, Vector3.down, out hit, 4f, anyMask, QueryTriggerInteraction.Ignore))
            return hit.point;
        p.y = transform.position.y;
        return p;
    }

    // ------------------------------------------------------------------ collection

    /// <summary>
    /// Called by TruckHopper after one of this stop's bags went into the truck (its record is already removed and
    /// paid) and by TrashBag when one bursts. Refreshes the marker; the "Curb clean!" bonus is paid by
    /// CurbTrash.CollectIntoTruck.
    /// </summary>
    public virtual void NotifyBagCollected(TrashBag bag)
    {
        if (bag != null) spawnedBags.Remove(bag);
        if (Marker != null) Marker.Refresh();
        UpdateStink();
        if (!IsNeglected) heaped = false;
    }

    // ------------------------------------------------------------------ gizmos

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.7f, 1f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.35f, 0.35f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1f);
        for (int i = 0; i < Capacity; i++)
            Gizmos.DrawWireCube(SlotPosition(i, false) + Vector3.up * 0.35f, new Vector3(0.5f, 0.7f, 0.5f));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        PutOutPresenter = null;
        HiddenHere = null;
        tmpPresent.Clear();
    }
}

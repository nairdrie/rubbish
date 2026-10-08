using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The sorting table: toss a trash bag on it and it thumps down, RIPS open and sprays its contents across
/// the tabletop, each item revealing as it lands. Also takes unrevealed loose items (they reveal on landing).
/// Revealed items are refused (they belong in a bin), and the table hides from the toss picker while you
/// hold one so the bins behind it win.
///
/// Layout (local space): the usable top is a topSize.x × topSize.y rectangle centered at (topOffset.x, topOffset.y)
/// on this transform at height topHeight. Rails/lips are plain colliders built by Setup_SortingStation; a child with
/// CarryReceiverTrigger catches physically thrown bags.
///
/// Upgrades (sorting_table, Docs/Progression.md §6.2): L1 "XL" folds out a front leaf (built by Setup_GarageUpgrades and
/// handed over as <see cref="xlLeaf"/>) that makes the top deeper toward the sorter, and takes 60 items instead of 40;
/// L2's magnet strip lives in <see cref="MagnetStrip"/>.
/// </summary>
public class SortingTable : MonoBehaviour, ICarryReceiver, ICarryReceiverFeedback
{
    [Header("Layout (local space)")]
    [Tooltip("Usable tabletop size (local X, local Z).")]
    public Vector2 topSize = new Vector2(3f, 0.9f);
    [Tooltip("Local Y of the tabletop surface.")]
    public float topHeight = 0.95f;
    [Tooltip("Visual-only root (no colliders) that gets squashed on thumps.")]
    public Transform visualRoot;

    [Header("Tuning")]
    public float tossRange = 2.4f;
    [Tooltip("Refuse new bags when this many loose items are already on the table.")]
    public int maxItemsOnTable = 40;
    [Tooltip("Seconds between items popping out of a ripped bag.")]
    public float spillInterval = 0.05f;
    [Tooltip("Keep spray targets this far inside the tabletop edges.")]
    public float edgeMargin = 0.22f;
    [Tooltip("Upward pop speed range of spilled items (m/s).")]
    public Vector2 sprayUpSpeed = new Vector2(2.2f, 3.2f);

    [Header("XL table (sorting_table L1)")]
    [Tooltip("Local XZ of the usable top's centre (the XL leaf moves it toward the sorter).")]
    public Vector2 topOffset;
    [Tooltip("The fold-out front leaf (its own top + lip colliders). Null = no XL extension available.")]
    public GameObject xlLeaf;
    [Tooltip("How much deeper (m, toward the sorter) the top gets with the XL leaf.")]
    public float xlFrontExtension = 0.35f;

    Vector2 baseTopSize, baseTopOffset;
    bool baseCaptured, xlApplied;
    Transform[] hiddenByLeaf;

    /// <summary>Items the table takes before refusing new bags (40, or 60 with the XL table).</summary>
    public int MaxItems => Mathf.Max(maxItemsOnTable, DepotMachineContext.Level(this, ShopIds.SortingTable) >= 1 ? 60 : 40);

    /// <summary>A bag is being thumped / ripped right now (auto-slitter and Magnus wait for it).</summary>
    public bool IsRipping => admitting.Count > 0 || incoming.Count > 0 || activeSprays > 0;

    /// <summary>The XL leaf is out.</summary>
    public bool IsXL => xlApplied;

    /// <summary>Local point at (x, z) relative to the top's centre, on the surface.</summary>
    public Vector3 LocalTop(float x, float z) => new Vector3(topOffset.x + x, topHeight, topOffset.y + z);

    /// <summary>World point at (x, z) relative to the top's centre, on the surface.</summary>
    public Vector3 TopPoint(float x, float z) => transform.TransformPoint(LocalTop(x, z));

    /// <summary>Local z of the sorter-side (front) edge of the usable top.</summary>
    public float FrontEdgeZ => topOffset.y - topSize.y * 0.5f;

    void Awake() => CaptureBase();

    void CaptureBase()
    {
        if (baseCaptured) return;
        baseCaptured = true;
        baseTopSize = topSize;
        baseTopOffset = topOffset;
    }

    void Start() => ApplyUpgrades();

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.SortingTable) ApplyUpgrades();
    }

    /// <summary>Fold the XL leaf out (or back in) to match the sorting_table level. Idempotent.</summary>
    public void ApplyUpgrades()
    {
        CaptureBase();
        bool want = xlLeaf != null && DepotMachineContext.Level(this, ShopIds.SortingTable) >= 1;
        if (xlLeaf != null && xlLeaf.activeSelf != want) xlLeaf.SetActive(want);
        hiddenByLeaf ??= new[] { transform.Find("Visual/LipFront"), transform.Find("Colliders/LipFront") };
        foreach (var t in hiddenByLeaf) if (t != null) t.gameObject.SetActive(!want);
        topSize = want ? new Vector2(baseTopSize.x, baseTopSize.y + xlFrontExtension) : baseTopSize;
        topOffset = want ? new Vector2(baseTopOffset.x, baseTopOffset.y - xlFrontExtension * 0.5f) : baseTopOffset;
        if (want && !xlApplied && Application.isPlaying && Time.timeSinceLevelLoad > 1f)
        {
            Vector3 p = TopPoint(0f, -topSize.y * 0.5f);
            Sfx.Play("slam", p, 0.7f, 1.2f);
            Fx.Dust(p, 1.2f);
            if (visualRoot != null) Juice.Squash(visualRoot, 0.08f, 0.3f);
            GameHUD.FloatingText(p + Vector3.up * 0.6f, "XL!", SortingKit.ComboOrange, 1.3f);
        }
        xlApplied = want;
    }

    readonly HashSet<Carryable> incoming = new HashSet<Carryable>();
    readonly HashSet<TrashBag> admitting = new HashSet<TrashBag>();
    sealed class SprayState
    {
        public readonly List<ItemInstance> items = new List<ItemInstance>();
        public int next;
        public ulong finder;
    }
    readonly List<SprayState> sprays = new List<SprayState>();
    int localPendingContents;
    int activeSprays;
    int cachedCountFrame = -1;
    int cachedCount;

    static int ContentCount(TrashBag bag)
    {
        int count = 0;
        var items = bag != null && bag.Contents != null ? bag.Contents.items : null;
        if (items != null) foreach (var item in items) if (item != null) count++;
        return count;
    }

    int PendingContents => DepotMachineContext.LotOf(this) >= 0 && SortingTableCapacityNet.Instance != null &&
        SortingTableCapacityNet.Instance.IsSpawned && SortingTableCapacityNet.Instance.IsServer
            ? SortingTableCapacityNet.PendingForLot(DepotMachineContext.LotOf(this)) : localPendingContents;

    /// <summary>Unmaterialized bag contents in this lot, for the host's unloading backlog mass cap.</summary>
    public static float PendingMassForLot(int lot) => SortingTableCapacityNet.PendingMassForLot(lot);

    /// <summary>Available slots after both landed and admitted-but-not-yet-landed contents.</summary>
    public int AvailableItemSlots => Mathf.Max(0, MaxItems - ItemsOnTable() - PendingContents);

    // ------------------------------------------------------------------ ICarryReceiver

    /// <summary>
    /// The point on the table's long axis nearest the local player (so "slam it down" always means right in
    /// front of you, and the table beats the compactor next door when you're standing at it).
    /// </summary>
    public Vector3 TossTargetPosition
    {
        get
        {
            float x = 0f;
            if (SortingKit.TryGetLocalPlayer(out var p)) x = ClampAlong(transform.InverseTransformPoint(p).x - topOffset.x, 0.45f);
            return transform.TransformPoint(topOffset.x + x, topHeight + 0.35f, topOffset.y);
        }
    }

    float ClampAlong(float localX, float margin)
    {
        float hx = Mathf.Max(0f, topSize.x * 0.5f - margin);
        return Mathf.Clamp(localX, -hx, hx);
    }

    /// <summary>
    /// Zero while the local player holds something the table won't take, so CarryReceivers.FindBest picks a
    /// bin instead of showing "that goes in a bin" every time you stand at the table. Always zero when the
    /// first-person SortStation exists: third person only offers stepping up to it (physical throws still land).
    /// </summary>
    public float TossRange
    {
        get
        {
            if (SortStation.Current != null) return 0f;
            var held = SortingKit.LocalHeld;
            if (held != null && !Wants(held)) return 0f;
            return tossRange;
        }
    }

    static bool Wants(Carryable item) => item is TrashBag || (item is LooseItem li && !li.Revealed);

    public bool CanAccept(Carryable item, out string reason)
    {
        reason = null;
        if (Compactor.ShopBusy(this)) { reason = "Compactor running — wait for the suction to finish"; return false; }
        if (item == null || item.State == CarryState.Consumed) { reason = "Huh?"; return false; }
        if (item is TrashBag bag)
        {
            if (admitting.Contains(bag) || incoming.Contains(bag)) { reason = "Bag is already being ripped"; return false; }
            if (!SortingTableCapacityRules.Fits(MaxItems, ItemsOnTable(), PendingContents, ContentCount(bag)))
            { reason = "Table's full! Sort some junk first"; return false; }
            return true;
        }
        if (item is LooseItem li)
        {
            if (!li.Revealed) return true;
            reason = li.Category switch
            {
                ItemCategory.Valuable => "Treasure! Put it in the KEEP crate",
                ItemCategory.Junk => "Junk goes in the COMPACTOR",
                ItemCategory.Hazard => "Careful! HAZARD barrel",
                _ => $"That's {ItemCategoryInfo.DisplayName(li.Category)} - bin it!"
            };
            return false;
        }
        reason = "Can't sort that";
        return false;
    }

    public string GetTossPrompt(Carryable item) =>
        item is TrashBag ? "Slam it on the table!" : "Toss onto the sorting table";

    public void Accept(Carryable item)
    {
        if (item == null || item.State == CarryState.Consumed || incoming.Contains(item)) return;
        if (item is TrashBag bag) AcceptBag(bag, false);
        else if (item is LooseItem li) StartCoroutine(LandItem(li));
    }

    /// <summary>Trusted host machine feed; actor permissions still apply to physical/player deliveries.</summary>
    public void AcceptFromMachine(TrashBag bag) => AcceptBag(bag, true);

    void AcceptBag(TrashBag bag, bool machine)
    {
        RequestAdmission(bag, (accepted, _) =>
        {
            if (accepted && bag != null && bag.State != CarryState.Consumed && isActiveAndEnabled)
                BeginRip(bag, false, null, null);
            else if (accepted) CancelAdmission(bag);
            else if (bag != null && bag.State != CarryState.Consumed && bag.State != CarryState.Carried &&
                     !bag.NetRemote && bag.Body != null && bag.Body.isKinematic)
                bag.EndCarry(Vector3.down, true);
        }, machine);
    }

    /// <summary>Single bag-capacity gate used by hands, physical throws, Magnus and the auto-slitter.</summary>
    public void RequestAdmission(TrashBag bag, System.Action<bool, string> done, bool machine = false)
    {
        if (!CanAccept(bag, out string reason)) { done?.Invoke(false, reason); return; }
        admitting.Add(bag);
        SortingTableCapacityNet.Request(this, bag, machine, (accepted, why) =>
        {
            admitting.Remove(bag);
            done?.Invoke(accepted, why);
        });
    }

    public void CancelAdmission(TrashBag bag) => SortingTableCapacityNet.Cancel(this, bag);

    void BeginRip(TrashBag bag, bool slam, Vector3? aimPoint, System.Action onImpact)
    {
        if (bag == null || bag.State == CarryState.Consumed || incoming.Contains(bag))
        { CancelAdmission(bag); return; }
        incoming.Add(bag);
        localPendingContents += ContentCount(bag);
        StartCoroutine(RipBag(bag, slam, aimPoint, onImpact));
    }

    /// <summary>
    /// First-person SLAM (SortStation): the bag — already handed to script by the carrier (kinematic, in flight) —
    /// winds up, slams straight down onto the table at aimPoint (or right in front of the player) and bursts
    /// open with extra juice. onImpact fires the moment it hits the wood. Returns false (doing nothing) when the
    /// table won't take it right now, e.g. it's full.
    /// </summary>
    public bool Slam(TrashBag bag, Vector3? aimPoint = null, System.Action onImpact = null, bool machine = false)
    {
        if (bag == null || bag.State == CarryState.Consumed || incoming.Contains(bag)) return false;
        if (!CanAccept(bag, out _)) return false;
        bool replied = false, granted = false;
        RequestAdmission(bag, (accepted, _) =>
        {
            replied = true;
            granted = accepted;
            if (accepted && bag != null && bag.State != CarryState.Consumed && isActiveAndEnabled)
                BeginRip(bag, true, aimPoint, onImpact);
            else if (accepted) CancelAdmission(bag);
            else if (bag != null && bag.State != CarryState.Consumed && bag.State != CarryState.Carried &&
                     !bag.NetRemote && bag.Body != null && bag.Body.isKinematic)
                bag.EndCarry(Vector3.down, true);
        }, machine);
        return !replied || granted;
    }

    /// <summary>The station's held bag already received its host grant; do not reserve its slots twice.</summary>
    public void SlamReserved(TrashBag bag, Vector3? aimPoint, System.Action onImpact) =>
        BeginRip(bag, true, aimPoint, onImpact);

    /// <summary>World Y of the tabletop surface.</summary>
    public float TopWorldY => transform.TransformPoint(0f, topHeight, 0f).y;

    /// <summary>Clamp a world point onto the tabletop (inset by margin), at surface height.</summary>
    public Vector3 ClampToTop(Vector3 world, float margin)
    {
        Vector3 l = transform.InverseTransformPoint(world);
        float hx = Mathf.Max(0.05f, topSize.x * 0.5f - margin);
        float hz = Mathf.Max(0.05f, topSize.y * 0.5f - margin);
        return transform.TransformPoint(topOffset.x + Mathf.Clamp(l.x - topOffset.x, -hx, hx), topHeight, topOffset.y + Mathf.Clamp(l.z - topOffset.y, -hz, hz));
    }

    /// <summary>Only feedback when a toss is refused (table full); thrown revealed items just land on the table.</summary>
    public void OnRejected(Carryable item, string reason, bool physical)
    {
        if (physical) return;
        Sfx.Play("buzz", TossTargetPosition, 0.5f, 1.1f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.04f, 0.15f);
    }

    void OnEnable()
    {
        CarryReceivers.Register(this);
        GameSession.UpgradeChanged += OnUpgradeChanged;
        BusinessUpgradeNet.Changed += ApplyUpgrades;
    }

    void OnDisable()
    {
        CarryReceivers.Unregister(this);
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        BusinessUpgradeNet.Changed -= ApplyUpgrades;
        StopAllCoroutines();
        // A visitor may leave while a guest-authoritative spray coroutine is yielding. Materialize every
        // not-yet-spawned item before Unity stops that coroutine; never consume half a bag and silently lose it.
        foreach (var spray in sprays)
            for (int i = spray.next; i < spray.items.Count; i++)
                LooseItem.Spawn(spray.items[i], RandomTopPoint(edgeMargin) + Vector3.up * 0.12f,
                    Random.rotation, Vector3.zero, false, spray.finder); // coop:allow(random) cosmetic resting orientation; existing item contents are unchanged and published
        sprays.Clear();
        foreach (var c in incoming)
            if (c != null)
            {
                if (c is TrashBag bag) CancelAdmission(bag);
                if (c.State != CarryState.Consumed && c.State != CarryState.Carried && !c.NetRemote &&
                    c.Body != null && c.Body.isKinematic) c.EndCarry(Vector3.down, true);
            }
        foreach (var bag in admitting) if (bag != null) CancelAdmission(bag);
        incoming.Clear();
        admitting.Clear();
        localPendingContents = 0;
        activeSprays = 0;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Loose items currently lying on the tabletop.</summary>
    public int ItemsOnTable()
    {
        if (cachedCountFrame == Time.frameCount) return cachedCount;
        cachedCountFrame = Time.frameCount;
        int n = 0;
        var items = LooseItem.All;
        for (int i = 0; i < items.Count; i++)
            if (items[i] != null && SortingKit.IsLoose(items[i]) && IsOnTop(items[i].transform.position)) n++;
        cachedCount = n;
        return n;
    }

    /// <summary>Is a world point above the tabletop (within ~0.9 m of the surface)?</summary>
    public bool IsOnTop(Vector3 world)
    {
        Vector3 l = transform.InverseTransformPoint(world);
        return Mathf.Abs(l.x - topOffset.x) <= topSize.x * 0.5f + 0.05f && Mathf.Abs(l.z - topOffset.y) <= topSize.y * 0.5f + 0.05f
            && l.y >= topHeight - 0.15f && l.y <= topHeight + 0.9f;
    }

    /// <summary>Random world point on the tabletop surface, inset by margin.</summary>
    public Vector3 RandomTopPoint(float margin)
    {
        float hx = Mathf.Max(0.05f, topSize.x * 0.5f - margin);
        float hz = Mathf.Max(0.05f, topSize.y * 0.5f - margin);
        return TopPoint(Random.Range(-hx, hx), Random.Range(-hz, hz));
    }

    // ------------------------------------------------------------------ bag: thump -> rip -> spray

    static readonly string[] SlamWords = { "WHAM!", "SLAM!", "THWACK!", "KER-THUMP!" };

    IEnumerator RipBag(TrashBag bag, bool slam = false, Vector3? aimPoint = null, System.Action onImpact = null)
    {
        int remaining = ContentCount(bag);
        bag.SetInFlight();

        // Where it thumps down: in front of you (or where you aimed), a bit random so repeated bags don't stack
        // perfectly. It ends upright (yaw only), so its local bottom is how high the pivot sits above the tabletop.
        float bottomOffset = -SortingKit.LocalRendererBounds(bag.transform).min.y * bag.transform.lossyScale.y;
        Vector3 from = aimPoint ?? bag.transform.position;
        Vector3 fromLocal = transform.InverseTransformPoint(from);
        float along = ClampAlong(fromLocal.x - topOffset.x + Random.Range(-0.15f, 0.15f) * (slam ? 0.4f : 1f), 0.45f);
        float acrossMax = Mathf.Max(0.14f, topSize.y * 0.5f - 0.31f);
        float across = slam && aimPoint.HasValue ? Mathf.Clamp(fromLocal.z - topOffset.y, -acrossMax, acrossMax) : Random.Range(-0.12f, 0.12f);
        Vector3 spot = TopPoint(along, across);
        Vector3 land = spot + Vector3.up * bottomOffset;
        Quaternion upright = Quaternion.Euler(0f, slam ? bag.transform.eulerAngles.y + Random.Range(-15f, 15f) : Random.Range(0f, 360f), 0f);
        if (slam)
        {
            // Wind up (hop over the front lip) to above the spot, then SLAM straight down.
            Vector3 over = land + Vector3.up * 0.42f;
            yield return SortingKit.Arc(bag.transform, over, 0.12f, 0.25f, upright);
            if (bag == null) { FinishRip(bag, remaining, null); yield break; }
            yield return MoveTo(bag.transform, land, upright, 0.075f, SortingKit.EaseInQuad);
        }
        else
        {
            float dist = Vector3.Distance(bag.transform.position, land);
            if (dist > 0.05f)
                yield return SortingKit.Arc(bag.transform, land, SortingKit.ArcDuration(dist), Mathf.Min(0.7f, 0.2f + dist * 0.2f), upright);
        }
        if (bag == null) { FinishRip(bag, remaining, null); yield break; }

        // THUMP
        Sfx.Play("bag_land", spot, 1f, Random.Range(0.85f, 1f));
        Juice.Squash(bag.JuiceTarget, slam ? 0.55f : 0.4f, slam ? 0.2f : 0.25f);
        if (visualRoot != null) Juice.Squash(visualRoot, slam ? 0.1f : 0.06f, 0.2f);
        Fx.Dust(spot, slam ? 1.1f : 0.7f);
        if (slam)
        {
            Sfx.Play("slam", spot, 0.9f, Random.Range(0.9f, 1.05f));
            Sfx.Play("thud", spot, 0.8f, 0.7f);
            Juice.Shake(0.2f, 0.16f);
            Juice.HitStop(0.05f, 0.08f);
            GameHUD.FloatingText(spot + Vector3.up * 0.7f, SlamWords[Random.Range(0, SlamWords.Length)], SortingKit.ComboOrange, 1.2f);
            try { onImpact?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
        }
        else Juice.Shake(0.08f, 0.12f);

        if (slam)
        {
            // The slam already did the work: one quick shudder and it gives.
            yield return new WaitForSeconds(0.07f);
            Sfx.Play("rustle", spot, 0.9f, 1.2f);
            if (bag != null) yield return Wiggle(bag.transform, upright * Quaternion.Euler(0f, 0f, 8f), 0.04f);
        }
        else
        {
            // Anticipation wiggle
            yield return new WaitForSeconds(0.14f);
            Sfx.Play("rustle", spot, 0.8f);
            for (int i = 0; i < 3 && bag != null; i++)
            {
                float a = (i % 2 == 0 ? 1f : -1f) * (10f - i * 2f);
                yield return Wiggle(bag.transform, upright * Quaternion.Euler(0f, 0f, a), 0.045f);
            }
        }
        if (bag == null) { FinishRip(bag, remaining, null); yield break; }

        // RIP!
        var b2 = SortingKit.RendererBounds(bag.gameObject);
        Vector3 rip = b2.center;
        float ripTop = b2.max.y;
        var contents = bag.Contents;
        ulong finderClientId = bag.LastHolder != null && bag.LastHolder.Player != null
            ? bag.LastHolder.Player.OwnerClientId : FeedbackAudience.UnknownFinder;
        Sfx.Play("bag_burst", rip, 1f, Random.Range(0.9f, 1.1f));
        Fx.Burst(rip, SortingKit.SackBlack, slam ? 38 : 26, slam ? 6f : 5f);
        Fx.Burst(rip, SortingKit.GunkBrown, slam ? 16 : 10, slam ? 3.8f : 3f);
        Juice.Shake(slam ? 0.24f : 0.18f, 0.18f);
        GameHUD.FloatingText(rip + Vector3.up * 0.5f, "RIP!", Color.white, slam ? 1.6f : 1.3f);
        string theme = SortingKit.BagThemeLabel(contents);
        if (!string.IsNullOrEmpty(theme)) GameHUD.FloatingText(rip + Vector3.up * 0.85f, theme, SortingKit.ComboOrange, 1.1f);
        SpawnStink(rip);
        SpawnSackScraps(rip, b2.size);
        incoming.Remove(bag);
        activeSprays++;
        var spray = new SprayState { finder = finderClientId };
        if (contents != null && contents.items != null)
            foreach (var item in contents.items) if (item != null) spray.items.Add(item);
        sprays.Add(spray);
        bag.Consume();
        Workshop.BagRipped(this); // a business with no SolidWaste 3D disk yet finds one in its first bags

        if (contents == null || contents.items == null || contents.items.Count == 0)
        {
            GameHUD.FloatingText(rip + Vector3.up * 0.3f, "...it was empty?", Color.gray, 0.9f);
            FinishRip(bag, remaining, spray);
            yield break;
        }

        // Spray the contents across the table, one pop at a time. Siblings ignore each other for the first
        // moments of flight (they spawn on top of each other; depenetration would fling them off the table).
        var items = spray.items;
        var siblings = new List<Collider>();
        float startY = transform.TransformPoint(0f, topHeight + 0.4f, 0f).y;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;
            Vector3 target = RandomTopPoint(edgeMargin) + Vector3.up * 0.06f;
            Vector3 flat = target - rip; flat.y = 0f;
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.right;
            Vector3 start = new Vector3(rip.x, startY, rip.z) + flat * 0.12f + Random.insideUnitSphere * 0.04f;
            float vy = Random.Range(sprayUpSpeed.x, sprayUpSpeed.y);
            float t = SortingKit.FlightTime(vy, start.y - target.y);
            Vector3 v = (target - start) / t;
            v.y = vy;
            var li = LooseItem.Spawn(item, start, Random.rotation, v, false, finderClientId);
            spray.next = i + 1;
            remaining--;
            localPendingContents = Mathf.Max(0, localPendingContents - 1);
            cachedCountFrame = -1;
            li.Body.angularVelocity = Random.insideUnitSphere * 6f;
            li.RevealOnLanding(t + 0.25f);
            if (li.Box != null)
            {
                for (int k = Mathf.Max(0, siblings.Count - 12); k < siblings.Count; k++)
                    if (siblings[k] != null) Physics.IgnoreCollision(li.Box, siblings[k], true);
                StartCoroutine(RestoreCollisions(li.Box, new List<Collider>(siblings), 0.45f));
                siblings.Add(li.Box);
            }
            if (li.Visual != null) Juice.PopIn(li.Visual.transform, 0.12f);
            Sfx.Play("pop", start, 0.55f, Mathf.Min(1.9f, 0.9f + i * 0.06f));
            yield return new WaitForSeconds(spillInterval);
        }
        FinishRip(bag, remaining, spray);
    }

    void FinishRip(TrashBag bag, int remaining, SprayState spray)
    {
        incoming.Remove(bag);
        localPendingContents = Mathf.Max(0, localPendingContents - remaining);
        if (spray != null)
        {
            sprays.Remove(spray);
            activeSprays = Mathf.Max(0, activeSprays - 1);
        }
    }

    static IEnumerator RestoreCollisions(Collider a, List<Collider> others, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (a == null) yield break;
        foreach (var o in others) if (o != null) Physics.IgnoreCollision(a, o, false);
    }

    static IEnumerator MoveTo(Transform t, Vector3 to, Quaternion rot, float dur, System.Func<float, float> ease)
    {
        if (t == null) yield break;
        Vector3 from = t.position;
        Quaternion r0 = t.rotation;
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            if (t == null) yield break;
            float k = ease(Mathf.Clamp01(e / dur));
            t.position = Vector3.LerpUnclamped(from, to, k);
            t.rotation = Quaternion.Slerp(r0, rot, Mathf.Clamp01(e / dur));
            yield return null;
        }
        if (t == null) yield break;
        t.position = to;
        t.rotation = rot;
    }

    IEnumerator Wiggle(Transform t, Quaternion to, float dur)
    {
        Quaternion from = t.rotation;
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            if (t == null) yield break;
            t.rotation = Quaternion.Slerp(from, to, e / dur);
            yield return null;
        }
    }

    void SpawnStink(Vector3 at)
    {
        var anchor = new GameObject("TableStink");
        anchor.transform.position = at + Vector3.up * 0.2f;
        Fx.Stink(anchor.transform);
        Destroy(anchor, 6f);
    }

    void SpawnSackScraps(Vector3 at, Vector3 size)
    {
        var mat = SortingKit.RuntimeMaterial("mat/sorting_sack_scrap", SortingKit.SackBlack);
        for (int i = 0; i < 3; i++)
        {
            var go = SortingKit.VisualCube("SackScrap", null, at, new Vector3(Random.Range(0.18f, 0.32f), 0.015f, Random.Range(0.12f, 0.25f)), mat);
            go.transform.rotation = Random.rotation;
            StartCoroutine(FlingScrap(go.transform, (Random.insideUnitSphere + Vector3.up * 1.5f).normalized * Random.Range(1.5f, 2.5f)));
        }
    }

    IEnumerator FlingScrap(Transform t, Vector3 v)
    {
        Vector3 spin = Random.insideUnitSphere * 540f;
        float floor = transform.TransformPoint(0f, topHeight, 0f).y + 0.01f;
        for (float e = 0f; e < 0.7f && t != null; e += Time.deltaTime)
        {
            v += Physics.gravity * Time.deltaTime;
            Vector3 p = t.position + v * Time.deltaTime;
            if (p.y < floor && IsOnTop(new Vector3(p.x, floor + 0.01f, p.z))) { p.y = floor; v = Vector3.zero; spin = Vector3.zero; }
            t.position = p;
            t.Rotate(spin * Time.deltaTime, Space.World);
            yield return null;
        }
        if (t != null) Juice.ShrinkAndDestroy(t.gameObject, 0.25f);
    }

    // ------------------------------------------------------------------ single unrevealed item

    IEnumerator LandItem(LooseItem li)
    {
        if (li.Revealed) yield break;
        // Physically thrown/dropped onto the table: just let it land.
        if (!li.Body.isKinematic)
        {
            li.RevealOnLanding(0.8f);
            yield break;
        }
        incoming.Add(li);
        Vector3 target = RandomTopPoint(edgeMargin + 0.1f) + Vector3.up * 0.3f;
        float dist = Vector3.Distance(li.transform.position, target);
        yield return SortingKit.Arc(li.transform, target, SortingKit.ArcDuration(dist), Mathf.Min(0.6f, 0.15f + dist * 0.2f));
        incoming.Remove(li);
        if (li == null || li.State == CarryState.Consumed) yield break;
        li.EndCarry(Vector3.down * 0.8f, true);
        li.RevealOnLanding(0.6f);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.4f);
        Vector3 c = LocalTop(0f, 0f) + Vector3.up * 0.02f;
        Gizmos.DrawWireCube(c, new Vector3(topSize.x, 0.04f, topSize.y));
        Gizmos.color = new Color(1f, 1f, 0.2f, 0.5f);
        Gizmos.DrawWireCube(c, new Vector3(topSize.x - edgeMargin * 2f, 0.04f, topSize.y - edgeMargin * 2f));
    }
}

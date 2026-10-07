using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Shop-scoped compaction. COMPACT CHAMBER presses only its contents; BULK COMPACT SHOP vacuums loose
/// trash from this interior (including its conveyor), never truck cargo or saved valuables. The host captures and settles each
/// transaction once, before a cosmetic suction/press sequence. Treasures are the risk; there are no fees.
/// </summary>
public partial class Compactor : MonoBehaviour, ICarryReceiver, ICarryReceiverFeedback
{
    [Header("Parts")]
    [Tooltip("Center of the crush chamber (the box items pile up in).")]
    public Transform chamber;
    [Tooltip("Chamber size in the chamber transform's local space.")]
    public Vector3 chamberSize = new Vector3(0.9f, 1.0f, 0.9f);
    [Tooltip("Toss target just above the hopper opening.")]
    public Transform mouth;
    [Tooltip("Press plate (moves along local Y between pressUpY and pressDownY).")]
    public Transform pressPlate;
    public float pressUpY = 1.9f;
    public float pressDownY = 0.35f;
    [Tooltip("Visual-only root (no colliders) for squash/punch.")]
    public Transform visualRoot;
    [Tooltip("Where bales pop out.")]
    public Transform baleSpawn;
    [Tooltip("Bales are launched to land around here (world).")]
    public Transform baleLanding;
    public Vector2 baleLandingSize = new Vector2(1.2f, 0.9f);
    [Tooltip("Optional readout on the machine (\"12.4 kg\").")]
    public TextMeshPro readout;

    [Header("Lazy crush: the tipping floor (world space box)")]
    public Vector3 pileCenter = new Vector3(5.5f, 0.8f, -3.1f);
    public Vector3 pileSize = new Vector3(3.2f, 2.2f, 3.6f);

    [Header("Tuning")]
    public float tossRange = 2.4f;
    [Tooltip("While holding a whole bag you have to be this close (bags belong on the table).")]
    public float bagTossRange = 1.6f;
    public int maxBales = 6;
    public float crushAllConfirmWindow = 4f;
    [Tooltip("Seconds SMASH stays armed when an unconfirmed, unopened bag is in the hopper (press again to crush it).")]
    public float smashConfirmWindow = 3f;
    [Tooltip("Compactor Mk II auto-bale: crush on its own once the pile in the chamber reaches this fraction of its height.")]
    public float autoBaleFill = 0.8f;
    [Tooltip("Hazards the Sniffer Nose sneezes out of a CRUSH ALL / whole bag as loose items (the rest are disposed of quietly).")]
    public int maxSneezedItems = 4;

    /// <summary>A confirm press this soon after arming is ignored (a double-tap isn't a decision).</summary>
    public const float ConfirmMinDelay = 0.35f;

    bool busy;
    public bool Busy { get => busy || CompactorNet.IsBusy(DepotMachineContext.LotOf(this)); private set => busy = value; }

    readonly List<GameObject> bales = new List<GameObject>();
    readonly List<Carryable> scratch = new List<Carryable>();
    readonly Dictionary<Carryable, float> lastFeedback = new Dictionary<Carryable, float>();
    float crushAllArmedUntil = -1f;
    float smashArmedUntil = -1f;
    int statsFrame = -1;
    float cachedHopperKg;
    int cachedHopperCount, cachedHopperBags, cachedUnconfirmedBags;
    float nextReadout;
    // Whole bags the player deliberately compacts (warned + confirmed at the sort station), and bags already warned about.
    readonly HashSet<Carryable> confirmedBags = new HashSet<Carryable>();
    readonly HashSet<Carryable> warnedBags = new HashSet<Carryable>();
    // Junk Magnus dropped in (credited "byMagnus" when it's crushed; never feeds the player's combo).
    readonly HashSet<Carryable> robotDropped = new HashSet<Carryable>();

    int CompactorLevel => Mathf.Clamp(DepotMachineContext.Level(this, ShopIds.CompactorMk2), 0, 3);
    bool HazardSafeHere => CompactorLevel >= 2;
    float GrateKeepHere => UpgradeEffects.GrateKeepAtLevel(CompactorLevel);
    float LazyPerKgHere => UpgradeEffects.LazyPerKgAtLevel(CompactorLevel);
    float fillCached, nextFillCheck, fullSince = -1f;
    SortBin keepCrate;
    SortingTable table;
    bool partsLooked;

    public bool CrushAllArmed => Time.time < crushAllArmedUntil;
    /// <summary>SMASH was pressed once with an unconfirmed unopened bag inside: the next press crushes it.</summary>
    public bool SmashArmed => Time.unscaledTime < smashArmedUntil;
    /// <summary>SMASH needs a second press right now (an unopened bag nobody confirmed is in the hopper).</summary>
    public bool SmashNeedsConfirm => !Busy && UnconfirmedBags > 0;

    // ------------------------------------------------------------------ ICarryReceiver

    public Vector3 TossTargetPosition => mouth != null ? mouth.position : transform.position + Vector3.up * 1.3f;

    /// <summary>
    /// Zero when the first-person SortStation exists: the hopper is only offered from there (a whole bag with a
    /// warning + confirm). Physically thrown junk still lands in the hopper from anywhere.
    /// </summary>
    public float TossRange => SortStation.Current != null ? 0f : SortingKit.LocalHeld is TrashBag ? bagTossRange : tossRange;

    public bool CanAccept(Carryable item, out string reason)
    {
        reason = null;
        if (Busy) { reason = "SMASHING! Hold on"; return false; }
        if (item == null || item.State == CarryState.Consumed) { reason = "Huh?"; return false; }
        if (item is LooseItem || item is TrashBag) return true;
        reason = "That doesn't fit";
        return false;
    }

    public string GetTossPrompt(Carryable item) => item is TrashBag ? "Toss whole bag in compactor (lazy!)" : "Toss into COMPACTOR";

    public void Accept(Carryable item)
    {
        if (item == null || item.State == CarryState.Consumed) return;
        StartCoroutine(Receive(item));
    }

    /// <summary>Magnus drops junk in: same as a toss, but no combo for robots and it's credited to him when crushed.</summary>
    public void AcceptFromRobot(Carryable item)
    {
        if (item == null || item.State == CarryState.Consumed) return;
        if (robotDropped.Count > 64) robotDropped.RemoveWhere(c => c == null || c.State == CarryState.Consumed);
        robotDropped.Add(item);
        StartCoroutine(Receive(item));
    }

    /// <summary>How full the chamber is (0..1): the top of the pile relative to the chamber's height. Cached ~0.3 s.</summary>
    public float Fill01
    {
        get
        {
            if (Time.time < nextFillCheck) return fillCached;
            nextFillCheck = Time.time + 0.3f;
            fillCached = 0f;
            if (chamber == null) return 0f;
            RefreshStats();
            float h = Mathf.Abs(chamberSize.y * chamber.lossyScale.y);
            float bottom = chamber.position.y - h * 0.5f;
            foreach (var c in scratch)
            {
                if (c == null) continue;
                float top = SortingKit.RendererBounds(c.gameObject).max.y;
                fillCached = Mathf.Max(fillCached, Mathf.Clamp01((top - bottom) / Mathf.Max(0.05f, h)));
            }
            return fillCached;
        }
    }

    /// <summary>The station's KEEP crate and table (siblings under the same root), looked up once.</summary>
    void FindParts()
    {
        if (partsLooked) return;
        partsLooked = true;
        Transform root = transform.parent != null ? transform.parent : transform;
        foreach (var b in root.GetComponentsInChildren<SortBin>()) if (b.IsKeepCrate) { keepCrate = b; break; }
        table = root.GetComponentInChildren<SortingTable>();
    }

    public void OnRejected(Carryable item, string reason, bool physical)
    {
        if (Busy) GameHUD.FloatingText(TossTargetPosition + Vector3.up * 0.4f, "hold on!", SortingKit.ComboOrange, 0.9f);
    }

    void OnEnable() => CarryReceivers.Register(this);
    void OnDisable()
    {
        CarryReceivers.Unregister(this);
        CompactorConfirmOverlay.Clear(this);
        StopAllCoroutines();
        CompactorCube.CancelSuction(suctionSequence);
        suctionSequence = 0;
        Busy = false;
        SetBusyNetwork(false);
        if (pressPlate != null)
        {
            var p = pressPlate.localPosition; p.y = pressUpY; pressPlate.localPosition = p;
        }
        confirmations.Clear();
        bulkConfirmationHoldUntil = -1f;
    }

    IEnumerator Receive(Carryable item)
    {
        bool robot = robotDropped.Contains(item);
        if (item.Body.isKinematic)
        {
            // Auto-toss: hop it over the lip, then let physics drop it into the pile.
            Vector3 m = TossTargetPosition + Vector3.up * 0.15f;
            float dist = Vector3.Distance(item.transform.position, m);
            item.SetInFlight();
            if (dist > 0.1f)
                yield return SortingKit.Arc(item.transform, m, SortingKit.ArcDuration(dist), Mathf.Min(0.6f, 0.2f + dist * 0.15f));
            if (item == null || item.State == CarryState.Consumed) yield break;
            item.EndCarry(Vector3.down * 1.5f + Random.insideUnitSphere * 0.3f, true);
        }
        if (item is LooseItem li && !li.Revealed) li.Reveal();
        Feedback(item, robot);
    }

    void Feedback(Carryable item, bool robot = false)
    {
        if (lastFeedback.TryGetValue(item, out float t) && Time.time - t < 1f) return;
        if (lastFeedback.Count > 64)
        {
            var dead = new List<Carryable>();
            foreach (var kv in lastFeedback) if (kv.Key == null || Time.time - kv.Value > 5f) dead.Add(kv.Key);
            foreach (var d in dead) lastFeedback.Remove(d);
        }
        lastFeedback[item] = Time.time;
        Vector3 m = TossTargetPosition;
        Sfx.Play("toss_in", m, 0.7f, Random.Range(0.75f, 0.95f));
        if (visualRoot != null) Juice.Punch(visualRoot, 0.06f, 0.15f);

        if (item is TrashBag)
        {
            // Got in without the sort station's warning + confirm (chucked / thrown in): warn now, once per bag.
            if (!confirmedBags.Contains(item) && warnedBags.Add(item))
            {
                PruneBagSets();
                GameHUD.Toast("Whole bag in the compactor! Treasures inside get crushed too", ToastStyle.Bad, 3f);
                GameHUD.FloatingText(m + Vector3.up * 0.45f, "UNOPENED!", SortingKit.ComboOrange, 1.05f);
                Sfx.Play("beep", m, 0.7f, 0.9f);
                return;
            }
            GameHUD.FloatingText(m + Vector3.up * 0.4f, "lazy!", Color.gray, 0.9f);
            return;
        }
        if (item is LooseItem li)
        {
            switch (li.Category)
            {
                case ItemCategory.Junk:
                    // Junk in the compactor IS a correct sort: keep the combo alive (robots never touch the combo).
                    if (robot) { Fx.Burst(m, ItemCategoryInfo.Color(ItemCategory.Junk), 6, 2f); break; }
                    float mult = SortCombo.RegisterCorrect();
                    if (SortCombo.Streak >= 2)
                        GameHUD.FloatingText(m + Vector3.up * 0.5f, $"x{mult:0.0}", SortingKit.ComboOrange, 0.9f);
                    Fx.Burst(m, ItemCategoryInfo.Color(ItemCategory.Junk), 6, 2f);
                    break;
                case ItemCategory.Hazard:
                    GameHUD.FloatingText(m + Vector3.up * 0.4f, "uh oh...", SortingKit.ComboOrange, 1f);
                    Sfx.Play("squeak", m, 0.6f, 0.7f);
                    break;
                case ItemCategory.Valuable:
                    GameHUD.FloatingText(m + Vector3.up * 0.4f, "wait, was that...?", SortingKit.Gold, 0.95f);
                    break;
                default:
                    GameHUD.FloatingText(m + Vector3.up * 0.4f, "that's recyclable...", ItemCategoryInfo.Color(li.Category), 0.8f);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ chamber queries

    /// <summary>Loose carryables currently inside the crush chamber.</summary>
    public List<Carryable> Contents(List<Carryable> into)
    {
        into.Clear();
        if (chamber == null) return into;
        Vector3 half = Vector3.Scale(chamberSize * 0.5f, Abs(chamber.lossyScale));
        return SortingKit.CarryablesInBox(chamber.position, half, chamber.rotation, into);
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    void RefreshStats()
    {
        if (statsFrame == Time.frameCount) return;
        statsFrame = Time.frameCount;
        Contents(scratch);
        cachedHopperKg = 0f;
        cachedHopperBags = cachedUnconfirmedBags = 0;
        foreach (var c in scratch)
        {
            cachedHopperKg += c.MassKg;
            if (!(c is TrashBag)) continue;
            cachedHopperBags++;
            if (!confirmedBags.Contains(c)) cachedUnconfirmedBags++;
        }
        cachedHopperCount = scratch.Count;
    }

    public float HopperKg { get { RefreshStats(); return cachedHopperKg; } }
    public int HopperCount { get { RefreshStats(); return cachedHopperCount; } }
    /// <summary>Whole (unopened) trash bags in the hopper.</summary>
    public int HopperBags { get { RefreshStats(); return cachedHopperBags; } }
    /// <summary>Unopened bags in the hopper that nobody confirmed compacting whole.</summary>
    public int UnconfirmedBags { get { RefreshStats(); return cachedUnconfirmedBags; } }

    /// <summary>
    /// The player chose to compact this whole, unopened bag after the warning (sort station confirm): no more
    /// warnings about it, and SMASH crushes it without asking again.
    /// </summary>
    public void ConfirmWholeBag(Carryable bag)
    {
        if (bag == null) return;
        PruneBagSets();
        confirmedBags.Add(bag);
        warnedBags.Add(bag);
        statsFrame = -1;
    }

    void PruneBagSets()
    {
        if (confirmedBags.Count > 16) confirmedBags.RemoveWhere(b => b == null || b.State == CarryState.Consumed);
        if (warnedBags.Count > 16) warnedBags.RemoveWhere(b => b == null || b.State == CarryState.Consumed);
    }

    /// <summary>Unheld shop trash, including conveyor stock. No truck, inventory or display contents.</summary>
    public float LazyKg(out int things)
    {
        var stock = CollectForCompact(true);
        float kg = 0f;
        foreach (var c in stock) kg += Mathf.Max(0f, c.MassKg);
        things = stock.Count;
        return kg;
    }

    void Update()
    {
        AutoBaleTick();
        if (readout == null || Time.time < nextReadout) return;
        nextReadout = Time.time + 0.25f;
        float kg = HopperKg;
        readout.text = Busy ? "SMASH!" : HopperCount == 0 ? "EMPTY" : CompactorLevel >= 1 ? $"{SortingKit.Mass(kg)} {Mathf.RoundToInt(Fill01 * 100f)}%" : SortingKit.Mass(kg);
    }

    /// <summary>
    /// Compactor Mk II: crushes on its own once the pile has sat at ≥ autoBaleFill for a moment (host decides). Never
    /// crushes an unopened bag nobody confirmed: it waits for SMASH instead.
    /// </summary>
    void AutoBaleTick()
    {
        if (AutomationPaused(this) || CompactorLevel < 1 || GameSession.IsGuest) { fullSince = -1f; return; }
        bool full = HopperCount > 0 && UnconfirmedBags == 0 && Fill01 >= autoBaleFill;
        if (!full) { fullSince = -1f; return; }
        if (fullSince < 0f) { fullSince = Time.time; return; }
        if (Time.time - fullSince < 1.2f) return;
        fullSince = -1f;
        AutoBale();
    }

    /// <summary>The Mk II auto-bale (also handy for tests): a SMASH that announces itself.</summary>
    public void AutoBale()
    {
        if (!Businesses.IsAuthority || AutomationPaused(this) || UnconfirmedBags > 0) return;
        BeginHostCompact(Permissions.LocalClientId, false, CollectForCompact(false));
    }

    // ------------------------------------------------------------------ player actions (host decides)

    public string SmashPrompt()
    {
        if (Busy) return "COMPACTING...";
        if (SmashArmed) return "Compact unopened bags too? Press again";
        return $"Compact chamber · {SortingKit.Mass(HopperKg)} · ~{Economy.Money(SortingKit.Cents(HopperKg * LazyPerKgHere))}";
    }

    public void Smash() => RequestCompact(false);

    IEnumerator Boom(int hazards, string name)
    {
        Vector3 c = chamber != null ? chamber.position : transform.position;
        if (LocalWatching) Juice.HitStop(0.12f, 0.05f);
        Sfx.Play("boom", c, 1f, Random.Range(0.9f, 1.05f));
        Fx.Burst(c, new Color(1f, 0.5f, 0.1f), 70, 9f);
        Fx.Burst(c, new Color(1f, 0.85f, 0.2f), 30, 6f);
        Fx.Burst(c, new Color(0.15f, 0.15f, 0.15f), 30, 4f);
        Fx.Dust(c + Vector3.up * 0.8f, 2.2f);
        if (LocalWatching) Juice.Shake(0.45f, 0.3f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.35f, 0.4f);
        bool battery = name != null && name.ToLowerInvariant().Contains("batter");
        string what = battery ? "Battery fire!" : $"{name} went KABOOM!";
        if (hazards > 1) what = $"{hazards}x hazard KABOOM!";
        if (LocalWatching) GameHUD.Toast(what + " No disposal fee.", ToastStyle.Info, 2f);
        GameHUD.FloatingText(c + Vector3.up * 1.8f, "BOOM!", new Color(1f, 0.55f, 0.1f), 2f);
        yield return new WaitForSeconds(0.35f);
    }

    void ReportLostTreasures(List<string> lost, bool lazy)
    {
        if (lost.Count == 0) return;
        if (lazy)
        {
            string msg = lost.Count == 1 ? $"RIP {lost[0]}. 1 treasure crushed. Monster." : $"{lost.Count} treasures crushed. Monster.";
            GameHUD.Toast(msg, ToastStyle.Bad, 3.5f);
        }
        else
        {
            for (int i = 0; i < lost.Count && i < 3; i++) GameHUD.Toast($"RIP {lost[i]}", ToastStyle.Bad, 3f);
            if (lost.Count > 3) GameHUD.Toast($"...and {lost.Count - 3} more treasures. Oof.", ToastStyle.Bad, 3f);
        }
        Sfx.Play("buzz", TossTargetPosition, 0.7f, 0.6f);
    }

    /// <summary>SMASH summary for whole bags crushed unopened: calls out the treasures you never even saw.</summary>
    void ReportUnopenedBags(int bags, List<string> lost)
    {
        if (bags <= 0) return;
        string what = bags == 1 ? "1 unopened bag" : $"{bags} unopened bags";
        Vector3 m = TossTargetPosition;
        if (lost.Count == 0)
        {
            GameHUD.Toast($"Crushed {what}. No treasures inside. Phew!", ToastStyle.Info, 3f);
            return;
        }
        if (lost.Count == 1)
            GameHUD.Toast($"RIP {lost[0]}. It was still in the bag. Never even saw it.", ToastStyle.Bad, 3.5f);
        else
        {
            string names = string.Join(", ", lost.GetRange(0, Mathf.Min(3, lost.Count)));
            if (lost.Count > 3) names += $" +{lost.Count - 3} more";
            GameHUD.Toast($"{lost.Count} treasures crushed in {what}: {names}", ToastStyle.Bad, 4f);
        }
        GameHUD.FloatingText(m + Vector3.up * 1.1f, lost.Count == 1 ? "...was that a treasure?" : "...were those treasures?!", SortingKit.Gold, 1f);
        Sfx.Play("buzz", m, 0.7f, 0.55f);
    }

    /// <summary>Wind up, slam down (squishing the pile), impact effects. Contents are frozen & flattened.</summary>
    IEnumerator PressSlam(List<Carryable> contents, float strength, System.Action onImpact = null)
    {
        foreach (var c in contents) if (c != null) c.SetInFlight();
        var tops = new Dictionary<Carryable, (float bottom, float height, Vector3 scale)>();
        foreach (var c in contents)
        {
            if (c == null) continue;
            var b = SortingKit.RendererBounds(c.gameObject);
            tops[c] = (b.min.y, Mathf.Max(0.02f, b.size.y), c.transform.localScale);
        }

        Sfx.Play("click", TossTargetPosition, 0.8f);
        // wind-up (from wherever the plate is now)
        float y0 = pressPlate != null ? pressPlate.localPosition.y : pressUpY;
        yield return MovePlate(y0, pressUpY + 0.15f, 0.12f, SortingKit.EaseOutQuad, null, null);
        Sfx.Play("whoosh", TossTargetPosition, 0.6f, 0.7f);
        // slam
        yield return MovePlate(pressUpY + 0.15f, pressDownY, 0.13f, SortingKit.EaseInQuad, contents, tops);
        onImpact?.Invoke();

        Vector3 c0 = chamber != null ? chamber.position : transform.position;
        Sfx.Play("compactor", c0, 1f, Random.Range(0.9f, 1.05f));
        Sfx.Play("crunch", c0, 1f, Random.Range(0.85f, 1.1f));
        Sfx.Play("slam", c0, 0.8f);
        if (LocalWatching) Juice.Shake(0.45f * strength, 0.3f);
        Fx.Dust(c0 + Vector3.up * 0.3f, 1.5f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.12f, 0.25f);
        yield return new WaitForSeconds(0.18f);
    }

    IEnumerator MovePlate(float fromY, float toY, float dur, System.Func<float, float> ease,
        List<Carryable> squish, Dictionary<Carryable, (float bottom, float height, Vector3 scale)> tops)
    {
        if (pressPlate == null) { yield return new WaitForSeconds(dur); yield break; }
        Vector3 lp = pressPlate.localPosition;
        for (float e = 0f; e <= dur; e += Time.deltaTime)
        {
            float k = ease(Mathf.Clamp01(e / dur));
            lp.y = Mathf.Lerp(fromY, toY, k);
            pressPlate.localPosition = lp;
            if (squish != null) Squish(squish, tops);
            yield return null;
        }
        lp.y = toY;
        pressPlate.localPosition = lp;
        if (squish != null) Squish(squish, tops);
    }

    void Squish(List<Carryable> items, Dictionary<Carryable, (float bottom, float height, Vector3 scale)> tops)
    {
        float plateY = pressPlate.position.y;
        foreach (var c in items)
        {
            if (c == null || !tops.TryGetValue(c, out var info)) continue;
            float avail = plateY - info.bottom;
            float k = Mathf.Clamp(avail / info.height, 0.12f, 1f);
            var t = c.transform;
            t.localScale = new Vector3(info.scale.x * (1f + (1f - k) * 0.6f), info.scale.y * k, info.scale.z * (1f + (1f - k) * 0.6f));
            Vector3 p = t.position;
            p.y = Mathf.Min(p.y, plateY - info.height * k * 0.5f);
            t.position = p;
        }
    }

    IEnumerator PressReturn()
    {
        yield return MovePlate(pressDownY, pressUpY, 0.45f, SortingKit.EaseOutBack, null, null);
    }

    /// <summary>Spawn a bale cube sized by kg, colored like its contents, and fling it onto the bale pile.</summary>
    GameObject PopBale(float kg, List<Color> colors, bool charred)
    {
        Vector3 from = baleSpawn != null ? baleSpawn.position : TossTargetPosition + Vector3.up * 0.3f;
        float side = Mathf.Clamp(0.3f + kg * 0.012f, 0.3f, 0.85f);

        Color col = new Color(0.5f, 0.47f, 0.42f);
        if (colors.Count > 0)
        {
            Color sum = Color.black;
            foreach (var c in colors) sum += c;
            col = Color.Lerp(sum / colors.Count, new Color(0.55f, 0.5f, 0.45f), 0.35f);
        }
        if (charred) col = Color.Lerp(col, new Color(0.08f, 0.07f, 0.06f), 0.75f);
        col.a = 1f;

        var go = new GameObject("Bale");
        go.transform.position = from;
        go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        var baseMat = SortingKit.RuntimeMaterial("mat/sorting_bale", col);
        var body = SortingKit.VisualCube("Body", go.transform, Vector3.zero, Vector3.one * side, baseMat);
        var r = body.GetComponent<Renderer>();
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", col);
        r.SetPropertyBlock(mpb);
        var strapMat = SortingKit.RuntimeMaterial("mat/sorting_strap", new Color(0.1f, 0.1f, 0.1f));
        SortingKit.VisualCube("Strap", go.transform, new Vector3(0f, 0f, side * 0.22f), new Vector3(side * 1.04f, side * 1.04f, side * 0.08f), strapMat);
        SortingKit.VisualCube("Strap", go.transform, new Vector3(0f, 0f, -side * 0.22f), new Vector3(side * 1.04f, side * 1.04f, side * 0.08f), strapMat);
        var col2 = go.AddComponent<BoxCollider>();
        col2.size = Vector3.one * side;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Clamp(kg, 1f, 60f);
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.maxDepenetrationVelocity = 2f;

        Vector3 to;
        if (baleLanding != null)
            to = baleLanding.TransformPoint(new Vector3(Random.Range(-0.5f, 0.5f) * baleLandingSize.x, side * 0.5f, Random.Range(-0.5f, 0.5f) * baleLandingSize.y));
        else
            to = from + transform.forward * 1.2f + Vector3.down * from.y;
        rb.linearVelocity = SortingKit.Ballistic(from, to, 0.6f);
        rb.angularVelocity = Random.insideUnitSphere * 6f;

        Juice.PopIn(go.transform, 0.3f);
        Sfx.Play("pop", from, 1f, 0.7f);
        Sfx.Play("thud", from, 0.8f, 0.6f);
        Fx.Burst(from, col, 14, 3f);
        if (charred) Fx.Stink(go.transform);

        CompactorFx.FloatAwayBale(go);
        bales.Add(go);
        bales.RemoveAll(b => b == null);
        while (bales.Count > Mathf.Max(1, maxBales))
        {
            var old = bales[0];
            bales.RemoveAt(0);
            if (old != null) Juice.ShrinkAndDestroy(old, 0.35f);
        }
        return go;
    }

    // ------------------------------------------------------------------ bulk: the shop, never truck cargo

    public string CrushAllPrompt()
    {
        if (Busy) return "VACUUMING...";
        float kg = LazyKg(out int things);
        string pay = Economy.Money(SortingKit.Cents(kg * LazyPerKgHere));
        return CrushAllArmed ? $"Bulk compact shop · {things} things / {kg:0.#} kg / ~{pay} · press again"
            : $"Bulk compact shop · {things} things / {kg:0.#} kg / ~{pay}";
    }

    public void CrushAllPressed() => RequestCompact(true);

    // ------------------------------------------------------------------ Mk II, facts, landfill

    /// <summary>A crushed loose item as a sort fact: junk in the compactor is a correct sort, a hazard is not.</summary>
    void RaiseCrushedFact(LooseItem li, bool correct, bool byMagnus)
    {
        if (DepotMachineContext.HostDepot(this, out int lot) &&
            BusinessUpgradeNet.ReportRobotSort(lot, li.Item, ItemCategory.Junk, correct, byMagnus)) return;
        ProgressionEvents.RaiseItemSorted(new ItemSortedFact
        {
            item = li.Item, bin = ItemCategory.Junk, correct = correct, byMagnus = byMagnus,
            districtId = SortBin.DistrictOf(li.Item), pos = TossTargetPosition,
        });
    }

    /// <summary>
    /// Everything inside an unopened bag that gets crushed: valuables die (or the grate rescues them), hazards roll
    /// their boom chance (or get sneezed out with the Sniffer Nose).
    /// </summary>
    public static void TallyBag(BagContents bag, bool grate, List<ItemInstance> rescued, List<string> lost, List<ItemInstance> sneezeOut,
        ref int booms, ref int sneezed, ref string hazardName)
        => TallyBag(bag, grate, rescued, lost, sneezeOut, ref booms, ref sneezed, ref hazardName, UpgradeEffects.HazardSafe);

    static void TallyBag(BagContents bag, bool grate, List<ItemInstance> rescued, List<string> lost, List<ItemInstance> sneezeOut,
        ref int booms, ref int sneezed, ref string hazardName, bool hazardSafe)
    {
        if (bag == null || bag.items == null) return;
        foreach (var it in bag.items)
        {
            var d = it != null ? it.Def : null;
            if (d == null) continue;
            if (d.category == ItemCategory.Valuable)
            {
                if (grate) rescued.Add(it);
                else lost.Add(d.name);
            }
            else if (d.category == ItemCategory.Hazard) TallyHazard(it, sneezeOut, ref booms, ref sneezed, ref hazardName, hazardSafe);
        }
    }

    public static void TallyHazard(ItemInstance it, List<ItemInstance> sneezeOut, ref int booms, ref int sneezed, ref string hazardName)
        => TallyHazard(it, sneezeOut, ref booms, ref sneezed, ref hazardName, UpgradeEffects.HazardSafe);

    static void TallyHazard(ItemInstance it, List<ItemInstance> sneezeOut, ref int booms, ref int sneezed, ref string hazardName, bool hazardSafe)
    {
        if (hazardSafe)
        {
            sneezed++;
            sneezeOut.Add(it);
            return;
        }
        if (!LazyBoomRoll(it)) return;
        booms++;
        hazardName ??= it.Def != null ? it.Def.name : "Hazard";
    }

    /// <summary>CRUSH ALL / whole-bag hazard roll: deterministic per item (same answer on every machine).</summary>
    public static bool LazyBoomRoll(ItemInstance it) =>
        it != null && GarageRoll.Chance((it.uid ?? it.defId) + "/crush_boom", Economy.LazyCrushHazardBoomChance);

    /// <summary>"Green Guarantee": rich districts charge per unopened bag that gets crushed.</summary>
    public static void AddLandfill(BagContents bag, Dictionary<string, float> into)
    {
        if (bag == null || bag.sackVariant == -1) return;
        var d = Districts.ForBagId(bag.bagId);
        if (d == null || d.landfillPerBag <= 0f) return;
        into.TryGetValue(d.id, out float sum);
        into[d.id] = sum + d.landfillPerBag;
    }

    /// <summary>Mk II L2 (SMASH): loose hazards in the chamber get sneezed back out onto the table before the slam.</summary>
    IEnumerator SneezeLooseHazards(List<Carryable> contents)
    {
        var hz = new List<LooseItem>();
        foreach (var c in contents) if (c is LooseItem li && li.Category == ItemCategory.Hazard) hz.Add(li);
        if (hz.Count == 0) yield break;
        AchooFx(hz.Count);
        yield return new WaitForSeconds(0.25f);
        for (int i = 0; i < hz.Count; i++)
        {
            var li = hz[i];
            contents.Remove(li);
            if (li == null || li.State == CarryState.Consumed) continue;
            StartCoroutine(SneezeArc(li, SneezeFrom(), SneezeSpot(i)));
        }
        yield return new WaitForSeconds(0.2f);
    }

    /// <summary>Mk II L2 (whole bags / CRUSH ALL): hazards inside the load get sneezed out (a few as loose items to bin).</summary>
    IEnumerator SneezeFromLoad(List<ItemInstance> items, int count)
    {
        AchooFx(count);
        yield return new WaitForSeconds(0.2f);
        int n = Mathf.Min(maxSneezedItems, items.Count);
        for (int i = 0; i < n; i++)
        {
            var it = items[i];
            if (it == null) continue;
            Vector3 from = SneezeFrom();
            var li = LooseItem.Spawn(it, from, Quaternion.identity, Vector3.zero, false);
            if (li != null) StartCoroutine(SneezeArc(li, from, SneezeSpot(i)));
            yield return new WaitForSeconds(0.08f);
        }
        if (count > n) GameHUD.FloatingText(TossTargetPosition + Vector3.up * 1.1f, $"+{count - n} sneezed into a bucket", Color.white, 0.8f);
    }

    /// <summary>
    /// One sneezed hazard: a scripted (kinematic) pop high over the gantry post that lands gently on the table. A physics
    /// throw (the first version) bounced off the post between the chamber and the table or rolled off the table edge,
    /// so in the play test most hazards ended back in the chamber or on the floor.
    /// </summary>
    IEnumerator SneezeArc(LooseItem li, Vector3 from, Vector3 to)
    {
        if (li == null || li.State == CarryState.Consumed) yield break;
        li.BlockReceiver(this, 3f);
        li.SetInFlight();
        li.transform.position = from;
        Juice.Squash(li.JuiceTarget, 0.4f, 0.25f);
        var endRot = Quaternion.Euler(0f, li.transform.eulerAngles.y + 200f, 0f);
        yield return SortingKit.Arc(li.transform, to, 0.65f, 1.3f, endRot);
        if (li == null || li.State == CarryState.Consumed || li.Holder != null) yield break;
        li.EndCarry(Vector3.zero, false);
        Sfx.Play("thud", to, 0.4f, 1.4f);
        Fx.Dust(to, 0.3f);
    }

    void AchooFx(int count)
    {
        Vector3 m = TossTargetPosition;
        GameHUD.FloatingText(m + Vector3.up * 0.9f, count > 1 ? $"ACHOO! x{count}" : "ACHOO!", new Color(0.6f, 1f, 0.6f), 1.4f);
        GameHUD.Toast(count == 1 ? "The Sniffer Nose sneezed out a hazard. Achoo means no." : $"The Sniffer Nose sneezed out {count} hazards. Achoo means no.", ToastStyle.Good, 2.5f);
        Sfx.Play("squeak", m, 0.9f, 0.55f);
        Sfx.Play("whoosh", m, 0.8f, 1.4f);
        Fx.Burst(m + Vector3.up * 0.3f, new Color(0.75f, 1f, 0.75f), 26, 4f);
        if (visualRoot != null) Juice.Squash(visualRoot, -0.25f, 0.3f);
        Juice.Shake(0.15f, 0.15f);
    }

    /// <summary>
    /// Where a sneeze launches from: the chamber's sorter-side half, clear of the gantry crossbeam and post above the
    /// chamber's table side (play test: hazards thrown from the centre hit them and landed at x -5.0..-5.3, not on the table).
    /// </summary>
    Vector3 SneezeFrom()
    {
        FindParts();
        Vector3 from = TossTargetPosition + Vector3.up * 0.3f;
        if (table != null) from -= table.transform.forward * 0.38f;
        return from;
    }

    /// <summary>Where sneezed hazards land: the table's compactor end (sortable from the station), else the floor in
    /// front.</summary>
    Vector3 SneezeSpot(int i)
    {
        FindParts();
        float k = (i % 4) * 0.18f;
        if (table != null)
        {
            Vector3 l = table.transform.InverseTransformPoint(TossTargetPosition);
            Vector3 p = table.transform.TransformPoint(l.x + 0.95f + k, table.topHeight + 0.05f, -0.05f + (i % 2) * 0.12f);
            return table.ClampToTop(p, table.edgeMargin) + Vector3.up * 0.05f;
        }
        return transform.TransformPoint(0.2f - k, 0.1f, 1.2f);
    }

    /// <summary>Mk II L3: crushed valuables pop out of the grate, dented, and fly into the KEEP crate.</summary>
    /// <summary>The grate's dented copy: value ×GrateKeep, condition knocked down, "Survived the compactor".</summary>
    public static ItemInstance Dent(ItemInstance it)
    {
        float keep = UpgradeEffects.GrateKeep > 0f ? UpgradeEffects.GrateKeep : 0.3f;
        return Dent(it, keep);
    }

    static ItemInstance Dent(ItemInstance it, float keep)
    {
        return new ItemInstance
        {
            uid = it.uid, defId = it.defId, massKg = it.massKg, foundAt = it.foundAt, dayFound = it.dayFound,
            value = Mathf.Max(1f, Mathf.Round(it.value * keep)), condition = Mathf.Clamp01(it.condition * keep),
            note = "Survived the compactor",
        };
    }

    IEnumerator FlyProp(ItemInstance it, Vector3 from, Vector3 to)
    {
        var go = new GameObject("RescuedTreasure");
        go.transform.position = from;
        if (it.Def != null) ItemVisuals.Build(it.Def, go.transform);
        Juice.PopIn(go.transform, 0.15f);
        Fx.Burst(from, SortingKit.Gold, 20, 3.5f);
        Sfx.Play("pop", from, 0.8f, 1.2f);
        yield return SortingKit.Arc(go.transform, to, 0.55f, 0.9f);
        if (go == null) yield break;
        Fx.Burst(to, SortingKit.Gold, 28, 4f);
        Sfx.Play("cash", to, 0.8f);
        if (keepCrate != null && keepCrate.visualRoot != null) Juice.Punch(keepCrate.visualRoot, 0.3f, 0.3f);
        GameHUD.FloatingText(to + Vector3.up * 0.4f, "dented, but SAVED", SortingKit.Gold, 1f);
        Juice.ShrinkAndDestroy(go, 0.15f);
    }

    static void CountLost(BagContents bag, List<string> lost)
    {
        if (bag == null || bag.items == null) return;
        foreach (var it in bag.items)
        {
            var d = it != null ? it.Def : null;
            if (d != null && d.category == ItemCategory.Valuable) lost.Add(d.name);
        }
    }



    void OnDrawGizmosSelected()
    {
        if (chamber != null)
        {
            Gizmos.matrix = chamber.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.5f);
            Gizmos.DrawWireCube(Vector3.zero, chamberSize);
        }
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.4f);
        Gizmos.DrawWireCube(pileCenter, pileSize);
    }
}

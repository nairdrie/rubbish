using System.Collections;
using UnityEngine;

/// <summary>
/// One sorting receptacle, configured by category:
///   Metal / Plastic / Glass / Paper -> recycling bins (pay rate × kg, min $0.10, × combo, × recycling contracts)
///   Hazard                          -> hazard barrel (flat disposal pay × combo; hazmat contract raises it)
///   Valuable                        -> KEEP crate (item goes to GameSession.Inventory for ebae)
/// Wrong item -> "Contaminated!" penalty and the bin SPITS it back out (keep crate just calls you a hoarder).
/// Takes LooseItems via auto-toss (Accept) or physical throws (CarryReceiverTrigger on the mouth child), and from
/// robots (<see cref="AcceptFromRobot"/>: Magnus) which never touch the player's combo.
/// Money goes through <see cref="Ledger"/>; every sort raises <see cref="ProgressionEvents.ItemSorted"/> (and
/// <see cref="ProgressionEvents.TreasureKept"/> for the KEEP crate) so builder A can turn it into Rep.
/// </summary>
public class SortBin : MonoBehaviour, ICarryReceiver, ICarryReceiverFeedback
{
    [Tooltip("What belongs in here. Valuable = the KEEP crate.")]
    public ItemCategory category = ItemCategory.Metal;
    [Tooltip("Shown in prompts: \"Toss into METAL bin\".")]
    public string displayName = "METAL bin";
    [Tooltip("Mouth of the bin: toss target, spit origin. Defaults to 1 m above the pivot.")]
    public Transform mouth;
    [Tooltip("Visual-only root (no colliders) that gets punched/squashed.")]
    public Transform visualRoot;
    [Tooltip("Optional: a visual that rises as the bin fills up (local Y scale 0..1).")]
    public Transform fillVisual;
    public float tossRange = 2.6f;
    [Tooltip("Bags are refused; while holding one the bin only competes when you're this close.")]
    public float bagPromptRange = 1.2f;
    [Tooltip("Items needed to fill the fill visual all the way.")]
    public int fillCapacity = 40;

    int itemsSorted;
    Vector3 fillBaseScale;
    Vector3 visualBaseScale;
    bool aimed;
    float aimTweenUntil;
    LineRenderer aimRim;
    TMPro.TextMeshPro aimLabel;

    public bool IsKeepCrate => category == ItemCategory.Valuable;
    public bool IsRecycling => ItemCategoryInfo.IsRecyclable(category);

    void Awake()
    {
        if (visualRoot != null) visualBaseScale = visualRoot.localScale;
        if (fillVisual != null)
        {
            fillBaseScale = fillVisual.localScale;
            fillVisual.localScale = new Vector3(fillBaseScale.x, 0.001f, fillBaseScale.z);
            fillVisual.gameObject.SetActive(false);
        }
    }

    void OnEnable() => CarryReceivers.Register(this);
    void OnDisable()
    {
        CarryReceivers.Unregister(this);
        SetAimed(false);
        if (visualRoot != null) visualRoot.localScale = visualBaseScale;
    }

    /// <summary>Persistent first-person target treatment; its ring and label have no colliders.</summary>
    public void SetAimed(bool value)
    {
        if (aimed == value) return;
        aimed = value;
        aimTweenUntil = Time.unscaledTime + 0.3f;
        if (value && aimRim == null) BuildAimMarker();
        if (aimRim != null) aimRim.enabled = value;
        if (aimLabel != null) aimLabel.gameObject.SetActive(value);
    }

    void BuildAimMarker()
    {
        var anchor = mouth != null ? mouth : transform;
        var ring = new GameObject("AimOutline");
        ring.transform.SetParent(anchor, false);
        ring.layer = 2; // Ignore Raycast: aiming must keep hitting the actual bin.
        aimRim = ring.AddComponent<LineRenderer>();
        aimRim.useWorldSpace = false;
        aimRim.loop = true;
        aimRim.positionCount = 8;
        aimRim.widthMultiplier = 0.028f;
        aimRim.numCornerVertices = 2;
        aimRim.sharedMaterial = SortingKit.RuntimeMaterial("mat/sort_aim_rim", Color.white, true);
        Color color = ItemCategoryInfo.Color(category);
        aimRim.startColor = aimRim.endColor = color;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI * 0.25f;
            aimRim.SetPosition(i, new Vector3(Mathf.Cos(a) * 0.28f, 0.045f, Mathf.Sin(a) * 0.34f));
        }
        string name = category == ItemCategory.Valuable ? "KEEP" : ItemCategoryInfo.DisplayName(category).ToUpperInvariant();
        aimLabel = SortingKit.MakeText(anchor, "AimCategory", name, 0.26f, Color.white);
        aimLabel.transform.localPosition = new Vector3(0f, 0.55f, -0.34f);
        aimLabel.gameObject.layer = 2;
        aimLabel.rectTransform.sizeDelta = new Vector2(1.5f, 0.35f);
    }

    void LateUpdate()
    {
        if (visualRoot == null || !aimed && Time.unscaledTime > aimTweenUntil) return;
        Vector3 target = visualBaseScale * (aimed ? 1.08f : 1f);
        if ((visualRoot.localScale - target).sqrMagnitude > 0.000001f)
            visualRoot.localScale = Vector3.Lerp(visualRoot.localScale, target,
                1f - Mathf.Exp(-13f * Time.unscaledDeltaTime));
    }

    // ------------------------------------------------------------------ ICarryReceiver

    public Vector3 TossTargetPosition => mouth != null ? mouth.position : transform.position + Vector3.up;

    /// <summary>Zero when the first-person SortStation exists: bins are worked from there (physical throws still land).</summary>
    public float TossRange => SortStation.Current != null ? 0f : SortingKit.LocalHeld is TrashBag ? bagPromptRange : tossRange;

    public bool CanAccept(Carryable item, out string reason)
    {
        reason = null;
        if (item is TrashBag) { reason = "Rip it open on the table first!"; return false; }
        if (item is LooseItem li)
        {
            if (li.State == CarryState.Consumed) { reason = "Huh?"; return false; }
            if (li.IsBlockedFor(this)) { reason = "Nope. Not again."; return false; }
            // Stuff that tumbled in on its own (never touched by a player) gets bounced: sort it yourself!
            if (li.LastHolder == null) { reason = "Hey! Sort it properly"; return false; }
            return true;
        }
        reason = "That doesn't go in there";
        return false;
    }

    public string GetTossPrompt(Carryable item) => $"Toss into {displayName}";

    public void Accept(Carryable item)
    {
        if (item is LooseItem li && li.State != CarryState.Consumed && !li.IsBlockedFor(this))
            StartCoroutine(Receive(li, null));
    }

    /// <summary>
    /// A machine drops <paramref name="li"/> in (Magnus, or the table's magnet strip with byMagnus = false): same pay and
    /// facts, but no combo for robots, and a wrong-bin item is spat back to <paramref name="spitBackTo"/> (e.g. onto the
    /// sorting table) instead of at you.
    /// </summary>
    public void AcceptFromRobot(LooseItem li, Vector3? spitBackTo = null, bool byMagnus = true)
    {
        if (li == null || li.State == CarryState.Consumed || li.IsBlockedFor(this)) return;
        StartCoroutine(Receive(li, new RobotDrop { spitTo = spitBackTo, byMagnus = byMagnus }));
    }

    sealed class RobotDrop { public Vector3? spitTo; public bool byMagnus; }

    /// <summary>The district an item came from (for facts and the ledger).</summary>
    public static string DistrictOf(ItemInstance item)
    {
        var d = Districts.ForAddress(item != null ? item.foundAt : null);
        return d != null ? d.id : null;
    }

    /// <summary>Recycling pay for one correct sort: max(min payout, rate × kg) × combo × recycling-contract multiplier.</summary>
    public static float RecyclePay(ItemCategory category, float massKg, float comboMult) =>
        SortingKit.Cents(Mathf.Max(Economy.MinRecyclePayout, Economy.RecycleRatePerKg(category) * massKg) * comboMult * UpgradeEffects.RecycleMult);

    /// <summary>Hazard barrel pay for one correct sort (hazmat contract level) × combo.</summary>
    public static float HazardPay(float comboMult) => SortingKit.Cents(UpgradeEffects.HazardPay * comboMult);

    static void RaiseSorted(LooseItem li, ItemCategory bin, bool correct, bool byMagnus, Vector3 pos)
    {
        ProgressionEvents.RaiseItemSorted(new ItemSortedFact
        {
            item = li != null ? li.Item : null, bin = bin, correct = correct, byMagnus = byMagnus,
            districtId = DistrictOf(li != null ? li.Item : null), pos = pos,
        });
    }

    // ------------------------------------------------------------------ the good stuff

    IEnumerator Receive(LooseItem li, RobotDrop robot)
    {
        // Freeze it (also makes it ungrabbable) and block re-entry while we deal with it.
        li.BlockReceiver(this, 5f);
        li.SetInFlight();
        if (!li.Revealed) li.Reveal();

        Vector3 m = TossTargetPosition;
        float dist = Vector3.Distance(li.transform.position, m);
        if (dist > 0.2f)
            yield return SortingKit.Arc(li.transform, m, SortingKit.ArcDuration(dist), Mathf.Min(0.6f, 0.15f + dist * 0.15f));
        if (li == null || li.State == CarryState.Consumed) yield break;

        bool correct = li.Category == category;
        if (correct)
        {
            li.MarkConsumed(); // ours now: frees any holder, no physics, can't be grabbed mid-gulp
            yield return Gulp(li);
            if (IsKeepCrate) Keep(li, robot != null, robot != null && robot.byMagnus);
            else Sorted(li, robot != null, robot != null && robot.byMagnus);
            if (li != null) Destroy(li.gameObject);
        }
        else if (IsKeepCrate) yield return Hoarder(li, robot);
        else yield return Contaminated(li, robot);
    }

    /// <summary>Item drops into the bin, shrinking as it goes.</summary>
    IEnumerator Gulp(LooseItem li)
    {
        Sfx.Play("toss_in", TossTargetPosition, 0.6f, Random.Range(0.95f, 1.15f));
        Transform t = li.transform;
        Vector3 from = t.position;
        Vector3 to = TossTargetPosition + Vector3.down * 0.45f;
        Vector3 s0 = t.localScale;
        const float dur = 0.12f;
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            if (t == null) yield break;
            float k = SortingKit.EaseInQuad(e / dur);
            t.position = Vector3.Lerp(from, to, k);
            t.localScale = s0 * Mathf.Lerp(1f, 0.4f, k);
            yield return null;
        }
    }

    void Sorted(LooseItem li, bool robot, bool byMagnus)
    {
        Vector3 m = TossTargetPosition;
        // Robots never feed (or break) the player's combo.
        float mult = robot ? 1f : SortCombo.RegisterCorrect();
        int streak = robot ? 0 : SortCombo.Streak;
        bool hazard = category == ItemCategory.Hazard;
        float amount = DepotMachineContext.LotOf(this) >= 0
            ? SortingKit.Cents(mult * (hazard
                ? UpgradeEffects.HazardPayAtLevel(DepotMachineContext.Level(this, ShopIds.HazmatContract))
                : Mathf.Max(Economy.MinRecyclePayout, Economy.RecycleRatePerKg(category) * li.MassKg) *
                  UpgradeEffects.RecycleMultAtLevel(DepotMachineContext.Level(this, ShopIds.RecyclingContracts))))
            : hazard ? HazardPay(mult) : RecyclePay(category, li.MassKg, mult);
        string what = li.Def != null ? li.Def.name : "trash";

        if (robot) DepotMachineContext.Credit(this, amount, hazard ? $"Disposed {what}" : $"Recycled {what}", hazard ? LedgerIds.Hazard : LedgerIds.Recycle, m, DistrictOf(li.Item));
        else Ledger.Credit(amount, hazard ? $"Disposed {what}" : $"Recycled {what}", hazard ? LedgerIds.Hazard : LedgerIds.Recycle, m, DistrictOf(li.Item));
        if (!(robot && BusinessUpgradeNet.ReportRobotSort(DepotMachineContext.LotOf(this), li.Item, category, true, byMagnus)))
        {
            GameSession.RecordSorted(li.Item, IsRecycling);
            RaiseSorted(li, category, true, byMagnus, m);
        }

        SortingKit.CashFloater(m + Vector3.up * 0.3f, amount, 1f + Mathf.Min(0.8f, amount * 0.25f));
        if (streak >= 2)
            GameHUD.FloatingText(m + Vector3.up * 0.7f, $"x{mult:0.0} COMBO!", SortingKit.ComboOrange, Mathf.Min(1.8f, 1f + streak * 0.07f));
        Sfx.Play("ding", m, 0.8f, SortCombo.DingPitchFor(streak));
        if (streak >= 3) Sfx.Play("combo", m, 0.45f, SortCombo.FlourishPitchFor(streak));
        Color c = ItemCategoryInfo.Color(category);
        Fx.Burst(m, c, 12 + 3 * Mathf.Min(streak, 10), 3f + 0.2f * Mathf.Min(streak, 10));
        if (visualRoot != null) Juice.Punch(visualRoot, Mathf.Min(0.4f, 0.15f + 0.025f * streak), 0.22f);
        if (streak >= 5 && streak % 5 == 0) Juice.Shake(0.1f, 0.12f);
        BumpFill();
    }

    void Keep(LooseItem li, bool robot, bool byMagnus)
    {
        Vector3 m = TossTargetPosition;
        if (!robot) SortCombo.RegisterCorrect();
        var item = li.Item;
        string what = li.Def != null ? li.Def.name : "Something shiny";
        // N4 payroll: an employee's find in the employer's shop goes to the employer's keep crate (company property, §13.5).
        bool routedRobot = robot && BusinessUpgradeNet.ReportRobotSort(DepotMachineContext.LotOf(this), item, ItemCategory.Valuable, true, byMagnus);
        bool forBoss = !robot && PayrollNet.TryHandOverKept(item, m);
        if (!routedRobot)
        {
            if (!forBoss) GameSession.AddToInventory(item);
            GameSession.RecordSorted(item, false);
            RaiseSorted(li, ItemCategory.Valuable, true, byMagnus, m);
            ProgressionEvents.RaiseTreasureKept(item, byMagnus);
        }

        var style = li.Rarity >= Rarity.Epic ? ToastStyle.Legendary : ToastStyle.Rare;
        // The owner receives their robot's keep notification with the inventory hand-back.
        if (!routedRobot) GameHUD.Toast(byMagnus ? $"Magnus kept: {what}! (your credit)" : forBoss ? $"Kept: {what}, for the boss's crate." : $"Kept: {what} — sell it on ebae!", style, 3f);
        if (item != null && item.value > 0f)
            GameHUD.FloatingText(m + Vector3.up * 0.4f, SortingKit.Appraisal(item.value), RarityInfo.Color(li.Rarity), 1.3f);
        Sfx.Play("cash", m);
        Sfx.Play("sparkle", m, 0.8f);
        Fx.Burst(m, SortingKit.Gold, 34, 4.5f);
        Fx.Burst(m, RarityInfo.Color(li.Rarity), 16, 3f);
        if (visualRoot != null)
        {
            Juice.Punch(visualRoot, 0.3f, 0.3f);
            Fx.Sparkle(visualRoot, SortingKit.Gold, 1.6f);
        }
        BumpFill();
    }

    IEnumerator Contaminated(LooseItem li, RobotDrop robot)
    {
        Vector3 m = TossTargetPosition;
        if (robot == null) SortCombo.Break();
        bool byMagnus = robot != null && robot.byMagnus;
        if (robot != null) DepotMachineContext.Charge(this, Economy.WrongBinPenalty, byMagnus ? "Magnus contaminated!" : "Contaminated!", LedgerIds.Contamination, m, DistrictOf(li.Item));
        else Ledger.Charge(Economy.WrongBinPenalty, "Contaminated!", LedgerIds.Contamination, m, DistrictOf(li.Item));
        if (!(robot != null && BusinessUpgradeNet.ReportRobotSort(DepotMachineContext.LotOf(this), li.Item, category, false, byMagnus)))
            RaiseSorted(li, category, false, byMagnus, m);
        Sfx.Play("buzz", m, 0.9f);
        GameHUD.FloatingText(m + Vector3.up * 0.65f, "Contaminated!", SortingKit.BadRed, 1.25f);
        SortingKit.CashFloater(m + Vector3.up * 0.1f, -Economy.WrongBinPenalty, 0.9f);
        string hint = li.Category switch
        {
            ItemCategory.Valuable => "(that's TREASURE! Keep it!)",
            ItemCategory.Junk => "(that's junk - compactor)",
            ItemCategory.Hazard => "(that's HAZARDOUS)",
            _ => $"(that's {ItemCategoryInfo.DisplayName(li.Category)})"
        };
        GameHUD.FloatingText(m + Vector3.up * 1.0f, hint, ItemCategoryInfo.Color(li.Category), 0.85f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.3f, 0.25f);
        Juice.Shake(0.06f, 0.1f);
        yield return new WaitForSeconds(0.1f);
        Spit(li, robot != null ? robot.spitTo : null);
    }

    IEnumerator Hoarder(LooseItem li, RobotDrop robot)
    {
        Vector3 m = TossTargetPosition;
        if (robot == null) SortCombo.Break();
        GameHUD.Toast(robot != null ? "Magnus tried to keep trash. Sentimental." : "That's just trash, you hoarder", ToastStyle.Bad);
        GameHUD.FloatingText(m + Vector3.up * 0.45f, "nope.", SortingKit.BadRed, 1.1f);
        Sfx.Play("squeak", m, 0.9f, 0.8f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.25f, 0.25f);
        yield return new WaitForSeconds(0.1f);
        Spit(li, robot != null ? robot.spitTo : null);
    }

    /// <summary>
    /// Hock the item back out with comic velocity, roughly at whoever threw it. While the local player is at the
    /// first-person sort station it's hocked back onto the sorting table instead (over your head is unreachable).
    /// </summary>
    void Spit(LooseItem li, Vector3? landOn = null)
    {
        if (li == null || li.State == CarryState.Consumed) return;
        Vector3 m = TossTargetPosition;
        Vector3 start = m + Vector3.up * 0.5f;
        // Bins face the player along local -Z (their backs are to the wall).
        Vector3 dir;
        if (SortingKit.TryGetLocalPlayer(out var player)) dir = player - m;
        else dir = -transform.forward;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) dir = -transform.forward;
        dir = Quaternion.Euler(0f, Random.Range(-25f, 25f), 0f) * dir.normalized;

        Vector3 velocity = dir * Random.Range(2.2f, 3.2f) + Vector3.up * Random.Range(4f, 5.5f);
        var station = SortStation.Current;
        if (landOn.HasValue) velocity = SortingKit.Ballistic(start, landOn.Value + Vector3.up * 0.08f, 0.8f);
        else if (station != null && station.IsActive && station.table != null)
        {
            // Big comedic lob back onto the table (front half, so it's easy to grab again).
            var table = station.table;
            Vector3 spot = table.ClampToTop(new Vector3(m.x + Random.Range(-0.3f, 0.3f), 0f, m.z), 0.3f);
            Vector3 l = table.transform.InverseTransformPoint(spot);
            spot = table.TopPoint(l.x - table.topOffset.x, -table.topSize.y * 0.12f + Random.Range(-0.08f, 0.08f)) + Vector3.up * 0.08f;
            velocity = SortingKit.Ballistic(start, spot, Random.Range(0.75f, 0.9f));
        }

        li.transform.position = start;
        li.transform.localScale = Vector3.one;
        li.BlockReceiver(this, 1.2f);
        li.EndCarry(velocity, true);
        li.Body.angularVelocity = Random.insideUnitSphere * 18f;
        Sfx.Play("splat", start, 0.8f, Random.Range(1.1f, 1.4f));
        Sfx.Play("throw", start, 0.5f, 1.3f);
        Juice.Squash(li.JuiceTarget, 0.4f, 0.25f);
        Fx.Burst(start, SortingKit.BadRed, 8, 2.5f);
    }

    /// <summary>Refused (a bag, or an item we just spat out): buzz, wobble, and bounce free objects back out.</summary>
    public void OnRejected(Carryable item, string reason, bool physical)
    {
        Vector3 m = TossTargetPosition;
        Sfx.Play("buzz", m, 0.5f, 1.25f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.15f, 0.2f);
        if (!physical || item == null) return; // the carrier already showed the reason
        if (!string.IsNullOrEmpty(reason)) GameHUD.FloatingText(m + Vector3.up * 0.4f, reason, SortingKit.BadRed, 0.85f);
        if (item.Body != null && !item.Body.isKinematic)
        {
            Vector3 away = item.transform.position - transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -transform.forward;
            item.Body.linearVelocity = away.normalized * 1.8f + Vector3.up * 3f;
        }
    }

    void BumpFill()
    {
        itemsSorted++;
        if (fillVisual == null) return;
        if (!fillVisual.gameObject.activeSelf) fillVisual.gameObject.SetActive(true);
        float k = Mathf.Clamp01((float)itemsSorted / Mathf.Max(1, fillCapacity));
        fillVisual.localScale = new Vector3(fillBaseScale.x, Mathf.Max(0.001f, fillBaseScale.y * k), fillBaseScale.z);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = ItemCategoryInfo.Color(category);
        Gizmos.DrawWireSphere(TossTargetPosition, 0.15f);
    }
}

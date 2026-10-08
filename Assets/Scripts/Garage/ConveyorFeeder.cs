using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Feeds the Bag Belt (conveyor_belt L1+, Docs/Progression.md §6.2): every <see cref="pullInterval"/> seconds the bucket
/// elevator takes a bag from the bay pile (Magnus-vision-flagged bags first, then the top of the heap) and sends it up
/// the line to the queue rack beside the sorting table. The belt only CARRIES bags: it never opens one.
/// L1 takes normal bags only; a HEAVY bag is visibly refused ("TOO HEAVY!") and stays on the pile until the Heavy-Duty
/// Motor (L2). Which bags it takes is <see cref="ConveyorRules.Accepts(int, BagContents)"/>. Host decides.
/// </summary>
public class ConveyorFeeder : MonoBehaviour
{
    static readonly List<ConveyorFeeder> feeders = new List<ConveyorFeeder>();
    public static IReadOnlyList<ConveyorFeeder> Feeders => feeders;

    public GarageRig rig;
    [Tooltip("The belt (one lane). An array so older scene copies with a second, retired lane still load; only lane 0 runs.")]
    public ConveyorLine[] lanes;
    public float pullInterval = ConveyorRules.PullSeconds;
    [Tooltip("Seconds between \"TOO HEAVY!\" refusals shown for heavy pile bags the belt can't take yet.")]
    public float rejectInterval = 6f;

    float nextPull;
    float nextReject;
    int rejectCursor;

    /// <summary>Heavy pile bags this belt refused (tests / stats).</summary>
    public int Rejected { get; private set; }

    int Level => DepotMachineContext.Level(this, ShopIds.ConveyorBelt);
    ConveyorLine Line => lanes != null && lanes.Length > 0 ? lanes[0] : null;

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void OnEnable()
    {
        if (!feeders.Contains(this)) feeders.Add(this);
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        if (rig != null) rig.Resolve();
    }

    void OnDisable() => feeders.Remove(this);

    /// <summary>On this conveyor anywhere: riding the belt, or on its way into the queue rack.</summary>
    public bool OwnsAnywhere(TrashBag bag)
    {
        if (bag == null || lanes == null) return false;
        foreach (var l in lanes)
        {
            if (l == null) continue;
            if (l.Owns(bag) || (l.tray != null && l.tray.IsArriving(bag))) return true;
        }
        return false;
    }

    /// <summary>
    /// Scene change: a bag riding the conveyor (scripted, up in the air) should come back on the tipping floor instead of
    /// dropping from 3.7 m. Returns where (spread out a little), or null if no conveyor owns it.
    /// </summary>
    public static Vector3? Rehome(TrashBag bag, int index)
    {
        foreach (var f in feeders)
        {
            if (f == null || !f.OwnsAnywhere(bag)) continue;
            var floor = f.rig != null ? f.rig.tippingFloor : null;
            Vector3 c = floor != null ? floor.pileCenter : f.transform.position;
            Vector2 size = floor != null ? floor.pileSize : Vector2.one;
            float x = ((index * 37) % 10 / 10f - 0.45f) * size.x;
            float z = ((index * 53) % 10 / 10f - 0.45f) * size.y;
            return new Vector3(c.x + x, c.y + 0.05f + (index / 6) * 0.4f, c.z + z);
        }
        return null;
    }

    void Update()
    {
        int level = Level;
        var line = Line;
        if (level < 1 || line == null || Compactor.ShopBusy(this)) return;
        TickRejects(level);
        if (!GarageRig.Decides || Time.time < nextPull || !line.Running) return;
        if (!line.HasRoomAtStart) { nextPull = Time.time + 0.25f; return; }

        var bag = NextBag(level);
        if (bag == null) { nextPull = Time.time + 0.5f; return; }
        if (line.Launch(bag))
        {
            nextPull = Time.time + pullInterval;
            Vector3 p = line.StartPoint;
            Sfx.Play("click", p, 0.6f, 0.8f);
            Sfx.Play("whoosh", p, 0.4f, 0.9f);
            Fx.Dust(p, 0.5f);
        }
        else nextPull = Time.time + 0.25f;
    }

    /// <summary>The best pile bag this belt accepts at <paramref name="level"/> (flagged first, then the top of the heap).</summary>
    public TrashBag NextBag(int level)
    {
        var line = Line;
        return GaragePile.Best(rig != null ? rig.compactor : null, line != null ? line.StartPoint : transform.position,
            true, DepotMachineContext.Level(this, ShopIds.MagnusVision), b => ConveyorRules.Accepts(level, b.Contents));
    }

    /// <summary>
    /// The visible refusal: now and then the elevator buzzes at a heavy bag it can't lift and the bag shrugs. Cosmetic,
    /// so every machine plays it from its own copy of the pile (the tell is a hash of the bag id).
    /// </summary>
    void TickRejects(int level)
    {
        if (level >= ConveyorRules.HeavyMotor || Time.time < nextReject) return;
        nextReject = Time.time + rejectInterval;
        var c = rig != null ? rig.compactor : null;
        if (c == null) return;
        TrashBag pick = null;
        int seen = 0;
        foreach (var b in GaragePile.Bags)
        {
            if (!GaragePile.IsFree(b) || !GaragePile.InPile(c, b.WorldCenter) || !ConveyorRules.IsHeavy(b.Contents)) continue;
            if (pick == null || seen == rejectCursor) pick = b;
            seen++;
        }
        if (pick == null) return;
        rejectCursor = seen > 0 ? (rejectCursor + 1) % seen : 0;
        Rejected++;
        Vector3 at = pick.WorldCenter;
        GameHUD.FloatingText(at + Vector3.up * 0.7f, "TOO HEAVY FOR THE BELT!", new Color(1f, 0.45f, 0.3f), 1.1f);
        Juice.Punch(pick.JuiceTarget, 0.18f, 0.25f);
        var line = Line;
        Vector3 foot = line != null ? line.StartPoint : transform.position;
        Sfx.Play("buzz", foot, 0.45f, 0.8f);
        Fx.Burst(foot + Vector3.up * 0.6f, new Color(1f, 0.3f, 0.2f), 6, 1.5f);
    }

    void OnDestroy() => feeders.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => feeders.Clear();
}

/// <summary>
/// The tipping-floor pile (the compactor's lazy-crush box, padded: tipped bags spill over the painted edge). Shared by
/// the conveyor feeder, Magnus vision, the workshop's Sorting Chute and the raccoon crew's depot deliveries.
/// </summary>
public static class GaragePile
{
    public static bool InPile(Compactor c, Vector3 p)
    {
        if (c == null) return false;
        Vector3 d = p - c.pileCenter;
        Vector3 h = c.pileSize * 0.5f + new Vector3(0.7f, 0.5f, 0.7f);
        return Mathf.Abs(d.x) <= h.x && Mathf.Abs(d.y) <= h.y && Mathf.Abs(d.z) <= h.z;
    }

    /// <summary>Every live TrashBag on this machine (from the Carryable registry: no scene search).</summary>
    public static IEnumerable<TrashBag> Bags
    {
        get
        {
            var all = Carryable.All;
            for (int i = all.Count - 1; i >= 0; i--)
                if (i < all.Count && all[i] is TrashBag b) yield return b;
        }
    }

    /// <summary>A bag lying free on the floor: not held, not scripted (belt / fetch), not burst, not a curb bag.</summary>
    public static bool IsFree(TrashBag b) =>
        b != null && b.SourceStop == null && b.State != CarryState.Carried && b.State != CarryState.Consumed && !b.IsInScriptedFlight
        && b.Body != null && !b.Body.isKinematic && !b.HasBurst && b.Contents != null;

    /// <summary>Bags lying on the tipping floor right now.</summary>
    public static int CountInPile(Compactor c)
    {
        int n = 0;
        foreach (var b in GaragePile.Bags)
            if (b != null && b.Contents != null && b.State != CarryState.Consumed && InPile(c, b.WorldCenter)) n++;
        return n;
    }

    /// <summary>
    /// The best free pile bag: Magnus-vision-flagged first (when <paramref name="flaggedFirst"/>), then the top of the heap,
    /// then nearest to <paramref name="from"/>. <paramref name="accept"/> filters (the belt's heavy rule). Null = none.
    /// </summary>
    public static TrashBag Best(Compactor c, Vector3 from, bool flaggedFirst, int visionLevel = -1, System.Predicate<TrashBag> accept = null)
    {
        TrashBag best = null;
        float bestScore = float.MinValue;
        foreach (var b in GaragePile.Bags)
        {
            if (!IsFree(b)) continue;
            Vector3 p = b.WorldCenter;
            if (!InPile(c, p)) continue;
            if (accept != null && !accept(b)) continue;
            float score = p.y * 10f - Vector3.Distance(p, from) * 0.1f;
            if (flaggedFirst && MagnusVisionFlags.IsFlagged(b.Contents,
                visionLevel >= 0 ? visionLevel : UpgradeEffects.Level(ShopIds.MagnusVision))) score += 500f;
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }
}

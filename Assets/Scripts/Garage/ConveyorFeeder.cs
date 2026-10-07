using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Feeds the Bag Belt (conveyor_belt L1+): every <see cref="pullInterval"/> seconds the bucket elevator grabs a bag from
/// the tipping-floor pile (Magnus-vision-flagged bags first, then the top of the heap) and sends it up the line. With
/// the Twin Turbo Belt (L3) the two lanes take turns, which doubles the feed. It is also the Tip-O-Matic Direct Feed
/// (auto_tipper L2) intake: bags tipped out of the truck go into a little queue chute at the elevator foot and ride up
/// from there; only the overflow lands on the pile. Host decides.
/// </summary>
public class ConveyorFeeder : MonoBehaviour, TruckUnloader.IBagIntake
{
    static readonly List<ConveyorFeeder> feeders = new List<ConveyorFeeder>();
    public static IReadOnlyList<ConveyorFeeder> Feeders => feeders;

    public GarageRig rig;
    public ConveyorLine[] lanes;
    public float pullInterval = 2f;
    [Tooltip("Where Direct Feed bags wait for the elevator (a chute at the elevator foot).")]
    public Transform chute;
    [Tooltip("How many tipped bags the Direct Feed chute holds before the rest spill onto the pile.")]
    public int chuteCapacity = 8;

    readonly List<TrashBag> queued = new List<TrashBag>();
    readonly HashSet<TrashBag> arriving = new HashSet<TrashBag>();
    float nextPull;
    int nextLane;

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void OnEnable()
    {
        if (!feeders.Contains(this)) feeders.Add(this);
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        if (rig != null) rig.Resolve();
        if (rig != null && rig.unloader != null) rig.unloader.directFeed = this;
    }

    void OnDisable()
    {
        feeders.Remove(this);
        if (rig != null && rig.unloader != null && ReferenceEquals(rig.unloader.directFeed, this)) rig.unloader.directFeed = null;
    }

    /// <summary>Bags waiting in the Direct Feed chute.</summary>
    public int QueuedCount { get { queued.RemoveAll(b => b == null || b.State == CarryState.Consumed || b.State == CarryState.Carried); return queued.Count; } }

    public bool Owns(TrashBag bag) => bag != null && queued.Contains(bag);

    public void Detach(TrashBag bag)
    {
        queued.Remove(bag);
        arriving.Remove(bag);
    }

    /// <summary>On this conveyor anywhere: the Direct Feed chute, a lane, or the tray.</summary>
    public bool OwnsAnywhere(TrashBag bag)
    {
        if (bag == null) return false;
        if (Owns(bag)) return true;
        if (lanes != null)
            foreach (var l in lanes)
            {
                if (l == null) continue;
                if (l.Owns(bag) || (l.tray != null && l.tray.Owns(bag))) return true;
            }
        return false;
    }

    /// <summary>
    /// Scene change: a bag riding the conveyor (scripted, up in the air) should come back on the tipping floor instead of
    /// dropping from 3.3 m. Returns where (spread out a little), or null if no conveyor owns it.
    /// </summary>
    public static Vector3? Rehome(TrashBag bag, int index)
    {
        foreach (var f in feeders)
        {
            if (f == null || !f.OwnsAnywhere(bag)) continue;
            var un = f.rig != null ? f.rig.unloader : null;
            Vector3 c = un != null ? un.pileCenter : f.transform.position;
            Vector2 size = un != null ? un.pileSize : Vector2.one;
            float x = ((index * 37) % 10 / 10f - 0.45f) * size.x;
            float z = ((index * 53) % 10 / 10f - 0.45f) * size.y;
            return new Vector3(c.x + x, c.y + 0.05f + (index / 6) * 0.4f, c.z + z);
        }
        return null;
    }

    // ------------------------------------------------------------------ Direct Feed

    public bool TryTakeFromTruck(BagContents contents, Vector3 from)
    {
        if (contents == null || Compactor.ShopBusy(this) || DepotMachineContext.Level(this, ShopIds.ConveyorBelt) < 1 || !GarageRig.Decides) return false;
        if (QueuedCount >= chuteCapacity) return false;
        Vector3 to = ChuteSpot(queued.Count);
        var bag = TrashBag.Spawn(contents, from, Quaternion.Euler(0f, PickupVisuals.StableHash(contents.bagId ?? "") % 360, 0f));
        if (bag == null) return false;
        bag.SetInFlight();
        queued.Add(bag);
        arriving.Add(bag);
        StartCoroutine(FlyToChute(bag, to));
        return true;
    }

    Vector3 ChuteSpot(int i)
    {
        Vector3 basePos = chute != null ? chute.position : (lanes != null && lanes.Length > 0 && lanes[0] != null ? lanes[0].StartPoint : transform.position);
        Vector3 right = chute != null ? chute.right : Vector3.right;
        Vector3 fwd = chute != null ? chute.forward : Vector3.forward;
        return basePos + right * ((i % 2) * 0.5f - 0.25f) + fwd * ((i / 2 % 2) * 0.45f - 0.2f) + Vector3.up * (i / 4) * 0.45f;
    }

    System.Collections.IEnumerator FlyToChute(TrashBag bag, Vector3 to)
    {
        float dist = Vector3.Distance(bag.transform.position, to);
        yield return ConveyorStock.WhileOwned(bag, () => Owns(bag),
            SortingKit.Arc(bag.transform, to, Mathf.Clamp(0.3f + dist * 0.06f, 0.35f, 0.8f), Mathf.Min(1.5f, 0.5f + dist * 0.15f)));
        arriving.Remove(bag);
        if (bag == null || !Owns(bag) || bag.State == CarryState.Consumed || bag.State == CarryState.Carried) yield break;
        Sfx.Play("bag_land", to, 0.5f, 1.2f);
        Juice.Squash(bag.JuiceTarget, 0.3f, 0.2f);
    }

    // ------------------------------------------------------------------ feeding the line

    void Update()
    {
        if (Compactor.ShopBusy(this) || DepotMachineContext.Level(this, ShopIds.ConveyorBelt) < 1 || !GarageRig.Decides || lanes == null || lanes.Length == 0) return;
        if (Time.time < nextPull) return;

        // Twin lanes take turns: each lane still pulls every pullInterval, so two lanes = 2x feed.
        int running = 0;
        foreach (var l in lanes) if (l != null && l.Running) running++;
        if (running == 0) return;

        for (int k = 0; k < lanes.Length; k++)
        {
            var lane = lanes[(nextLane + k) % lanes.Length];
            if (lane == null || !lane.HasRoomAtStart) continue;
            var bag = NextBag();
            if (bag == null) { nextPull = Time.time + 0.5f; return; }
            if (lane.Launch(bag))
            {
                Detach(bag);
                nextLane = (System.Array.IndexOf(lanes, lane) + 1) % lanes.Length;
                nextPull = Time.time + pullInterval / running;
                PunchElevator(lane);
                return;
            }
        }
        nextPull = Time.time + 0.25f;
    }

    void PunchElevator(ConveyorLine lane)
    {
        Vector3 p = lane.StartPoint;
        Sfx.Play("click", p, 0.6f, 0.8f);
        Sfx.Play("whoosh", p, 0.4f, 0.9f);
        Fx.Dust(p, 0.5f);
    }

    /// <summary>The chute first (Direct Feed), then the best bag on the tipping floor.</summary>
    TrashBag NextBag()
    {
        for (int i = 0; i < queued.Count; i++)
        {
            var b = queued[i];
            if (b != null && b.State != CarryState.Consumed && b.State != CarryState.Carried && !b.IsInScriptedFlight && !arriving.Contains(b)) return b;
        }
        return GaragePile.Best(rig != null ? rig.compactor : null, lanes != null && lanes.Length > 0 && lanes[0] != null ? lanes[0].StartPoint : transform.position,
            true, DepotMachineContext.Level(this, ShopIds.MagnusVision));
    }

    void OnDestroy() => feeders.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => feeders.Clear();
}

/// <summary>
/// The tipping-floor pile (the compactor's lazy-crush box, padded like SortStation's: tipped bags spill over the painted
/// edge). Shared by "Next bag!", the conveyor feeder, Magnus vision and Kevin's depot deliveries.
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

    /// <summary>A bag lying free on the floor: not held, not scripted (belt / tray / fetch), not burst, not a curb bag.</summary>
    public static bool IsFree(TrashBag b) =>
        b != null && b.SourceStop == null && b.State != CarryState.Carried && b.State != CarryState.Consumed && !b.IsInScriptedFlight
        && b.Body != null && !b.Body.isKinematic && !b.HasBurst && b.Contents != null;

    /// <summary>Bags lying on the tipping floor right now.</summary>
    public static int CountInPile(Compactor c)
    {
        int n = 0;
        foreach (var b in Object.FindObjectsByType<TrashBag>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (b != null && b.Contents != null && b.State != CarryState.Consumed && InPile(c, b.WorldCenter)) n++;
        return n;
    }

    /// <summary>
    /// The best free pile bag: Magnus-vision-flagged first (when <paramref name="flaggedFirst"/>), then the top of the heap,
    /// then nearest to <paramref name="from"/>. Null when the pile is empty.
    /// </summary>
    public static TrashBag Best(Compactor c, Vector3 from, bool flaggedFirst, int visionLevel = -1)
    {
        TrashBag best = null;
        float bestScore = float.MinValue;
        foreach (var b in Object.FindObjectsByType<TrashBag>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!IsFree(b)) continue;
            Vector3 p = b.WorldCenter;
            if (!InPile(c, p)) continue;
            float score = p.y * 10f - Vector3.Distance(p, from) * 0.1f;
            if (flaggedFirst && MagnusVisionFlags.IsFlagged(b.Contents,
                visionLevel >= 0 ? visionLevel : UpgradeEffects.Level(ShopIds.MagnusVision))) score += 500f;
            if (score > bestScore) { bestScore = score; best = b; }
        }
        return best;
    }
}

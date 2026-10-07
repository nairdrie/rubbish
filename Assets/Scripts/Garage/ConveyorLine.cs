using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One lane of the Bag Belt (conveyor_belt, Docs/Progression.md §6.2): a bucket elevator up from the tipping floor, an
/// overhead belt across the garage at y 3.3 and a derpy slide down to the tray by the sorting table. Bags on it are
/// scripted carriers (kinematic, in flight), not physics: they move along the waypoint path at belt speed, keep their
/// spacing, and queue when the tray is full. Lane 2 only runs with the Twin Turbo Belt (L3).
/// Host decides; the bags themselves are local objects like every other bag in the garage today.
/// </summary>
public class ConveyorLine : MonoBehaviour
{
    static readonly List<ConveyorLine> lines = new List<ConveyorLine>();
    /// <summary>Every enabled lane in the loaded scenes (SceneTrashKeeper re-homes their bags on a scene change).</summary>
    public static IReadOnlyList<ConveyorLine> Lines => lines;

    [Tooltip("Path from the elevator foot to the end of the slide (world positions of these transforms).")]
    public Transform[] waypoints;
    public ConveyorTray tray;
    [Tooltip("0 = the main lane (conveyor L1+), 1 = the twin lane (conveyor L3).")]
    public int lane;
    public float speed = 0.8f;
    public float spacing = 1.5f;
    [Tooltip("Bags pick up speed on the slides: speed × (1 + slideBoost × steepness), steepness = −dir.y (0 = constant speed).")]
    public float slideBoost = 1.5f;
    [Tooltip("Optional: rollers/buckets spun while the belt runs (local X axis).")]
    public Transform[] rollers;
    public float rollerSpin = 360f;

    struct Carrier
    {
        public TrashBag bag;
        public float dist;
        public bool hopping;      // still flying in from the pile / truck
        public float wobble;
    }

    readonly List<Carrier> carriers = new List<Carrier>();   // index 0 = furthest along
    Vector3[] pts;
    float[] cum;
    float length;
    bool waitingAtEnd;

    /// <summary>This lane runs at the current conveyor level.</summary>
    public bool Running => isActiveAndEnabled && DepotMachineContext.Level(this, ShopIds.ConveyorBelt) >= (lane == 0 ? 1 : 3) && length > 0.1f;
    public int Count => carriers.Count;
    public float Length => length;
    public Vector3 StartPoint => pts != null && pts.Length > 0 ? pts[0] : transform.position;

    /// <summary>Room for one more bag at the elevator foot.</summary>
    public bool HasRoomAtStart
    {
        get
        {
            if (!Running) return false;
            if (carriers.Count == 0) return true;
            var last = carriers[carriers.Count - 1];
            return !last.hopping && last.dist >= spacing;
        }
    }

    public bool Owns(TrashBag bag)
    {
        for (int i = 0; i < carriers.Count; i++) if (carriers[i].bag == bag) return true;
        return false;
    }

    public void Detach(TrashBag bag) => carriers.RemoveAll(c => c.bag == bag);

    void Awake() => BuildPath();
    void OnEnable() { if (!lines.Contains(this)) lines.Add(this); }
    void OnDisable() => lines.Remove(this);

    /// <summary>Cache the path (call again if waypoints move).</summary>
    public void BuildPath()
    {
        var list = new List<Vector3>();
        if (waypoints != null) foreach (var w in waypoints) if (w != null) list.Add(w.position);
        pts = list.ToArray();
        cum = new float[pts.Length];
        length = 0f;
        for (int i = 1; i < pts.Length; i++)
        {
            length += Vector3.Distance(pts[i - 1], pts[i]);
            cum[i] = length;
        }
    }

    /// <summary>World point and travel direction at <paramref name="d"/> metres along the path.</summary>
    public Vector3 PointAt(float d, out Vector3 dir)
    {
        dir = Vector3.forward;
        if (pts == null || pts.Length == 0) return transform.position;
        if (pts.Length == 1) return pts[0];
        d = Mathf.Clamp(d, 0f, length);
        int i = 1;
        while (i < pts.Length - 1 && cum[i] < d) i++;
        float seg = Mathf.Max(1e-4f, cum[i] - cum[i - 1]);
        dir = (pts[i] - pts[i - 1]) / seg;
        return Vector3.Lerp(pts[i - 1], pts[i], (d - cum[i - 1]) / seg);
    }

    /// <summary>
    /// Hop a bag from wherever it is onto the elevator foot, then ride. False = no room / not running.
    /// <paramref name="hop"/> = false puts it straight on the foot (tests, restores).
    /// </summary>
    public bool Launch(TrashBag bag, bool hop = true)
    {
        if (bag == null || !HasRoomAtStart || bag.State == CarryState.Consumed) return false;
        bag.SetInFlight();
        carriers.Add(new Carrier { bag = bag, dist = 0f, hopping = hop, wobble = (PickupVisuals.StableHash(bag.name) & 255) / 255f * 6.28f });
        if (hop) StartCoroutine(HopOn(bag));
        else bag.transform.position = StartPoint;
        return true;
    }

    /// <summary>Metres along the path of the bag (−1 if it isn't on this lane).</summary>
    public float DistanceOf(TrashBag bag)
    {
        for (int i = 0; i < carriers.Count; i++) if (carriers[i].bag == bag) return carriers[i].dist;
        return -1f;
    }

    IEnumerator HopOn(TrashBag bag)
    {
        Vector3 to = StartPoint;
        float dist = Vector3.Distance(bag.transform.position, to);
        Sfx.Play("whoosh", bag.transform.position, 0.5f, 1.2f);
        yield return ConveyorStock.WhileOwned(bag, () => Owns(bag),
            SortingKit.Arc(bag.transform, to, Mathf.Clamp(0.25f + dist * 0.08f, 0.3f, 0.7f), Mathf.Min(1.2f, 0.4f + dist * 0.2f),
                Quaternion.Euler(0f, bag.transform.eulerAngles.y, 0f)));
        if (bag == null || !Owns(bag) || bag.State == CarryState.Consumed || bag.State == CarryState.Carried) yield break;
        for (int i = 0; i < carriers.Count; i++)
        {
            if (carriers[i].bag != bag) continue;
            var c = carriers[i]; c.hopping = false; carriers[i] = c;
            break;
        }
        if (bag != null)
        {
            Sfx.Play("thud", to, 0.5f, 1.2f);
            Juice.Squash(bag.JuiceTarget, 0.3f, 0.2f);
            Fx.Dust(to, 0.4f);
        }
    }

    void Update() => Tick(Time.deltaTime);

    /// <summary>Advance the belt by <paramref name="dt"/> seconds (Update calls it; tests can drive it directly).</summary>
    public void Tick(float dt)
    {
        bool run = Running;
        if (rollers != null && run)
            foreach (var r in rollers) if (r != null) r.Rotate(Vector3.right, rollerSpin * speed * dt, Space.Self);
        if (carriers.Count == 0) return;
        // The lane's level went away (debug level reset / another save loaded): its visuals are hidden, so let the bags
        // drop instead of hanging in mid-air where the belt used to be.
        if (DepotMachineContext.Level(this, ShopIds.ConveyorBelt) < (lane == 0 ? 1 : 3)) { DropAll(); return; }

        waitingAtEnd = false;
        for (int i = 0; i < carriers.Count; i++)
        {
            var c = carriers[i];
            if (c.bag == null || c.bag.State == CarryState.Consumed || c.bag.State == CarryState.Carried)
            {
                carriers.RemoveAt(i--);
                continue;
            }
            if (c.hopping) continue;
            float max = i == 0 ? length : carriers[i - 1].dist - spacing;
            float v = speed;
            if (slideBoost > 0f) { PointAt(c.dist + 0.01f, out Vector3 segDir); v *= 1f + slideBoost * Mathf.Max(0f, -segDir.y); }
            if (run) c.dist = Mathf.Min(c.dist + v * dt, Mathf.Max(c.dist, max));

            Vector3 p = PointAt(c.dist, out Vector3 dir);
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            float yaw = flat.sqrMagnitude > 0.01f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : c.bag.transform.eulerAngles.y;
            float t = (Application.isPlaying ? Time.time : 0f) * 3f + c.wobble;
            c.bag.transform.SetPositionAndRotation(p + Vector3.up * Mathf.Abs(Mathf.Sin(t)) * 0.03f,
                Quaternion.Euler(Mathf.Sin(t * 0.7f) * 4f, yaw, Mathf.Sin(t) * 5f));
            carriers[i] = c;

            if (i == 0 && c.dist >= length - 0.01f)
            {
                // End of the slide: into the tray, or wait (the line backs up behind it).
                if (tray != null && tray.Offer(c.bag, lane))
                {
                    carriers.RemoveAt(0);
                    i--;
                }
                else waitingAtEnd = true;
            }
        }
    }

    /// <summary>The front bag is sitting at the end of the slide because the tray is full.</summary>
    public bool BackedUp => waitingAtEnd;

    /// <summary>Let go of every bag (they fall where they are, as Resting so they don't burst). Used when the line is
    /// torn down (its conveyor level is gone).</summary>
    public void DropAll()
    {
        StopAllCoroutines();   // hop-ons in progress would keep dragging their bag toward the elevator foot
        foreach (var c in carriers) if (c.bag != null && c.bag.State != CarryState.Consumed) c.bag.EndCarry(Vector3.zero, false);
        carriers.Clear();
    }

    void OnDestroy() => lines.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => lines.Clear();

    void OnDrawGizmosSelected()
    {
        if (waypoints == null) return;
        Gizmos.color = lane == 0 ? Color.cyan : Color.magenta;
        for (int i = 1; i < waypoints.Length; i++)
            if (waypoints[i - 1] && waypoints[i]) Gizmos.DrawLine(waypoints[i - 1].position, waypoints[i].position);
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Bag Belt (conveyor_belt, Docs/Progression.md §6.2): a bucket elevator up from the tipping floor, an overhead belt
/// round the garage wall at y 3.7 and a slide down to the queue rack by the sorting table. Bags on it are scripted
/// carriers (kinematic, in flight), not physics: they move along the waypoint path at belt speed, keep their spacing,
/// and queue along the belt when the rack is full. The belt never opens a bag.
/// With the Junk Diverter (L3) each bag is shaken over a grate at <see cref="divertAt"/> metres along the path:
/// <see cref="JunkDiverter"/> says what falls out, and those items go straight into the compactor's hopper (the junk
/// bin), credited like any junk dropped in by a machine; the bag carries on lighter.
/// Host decides; the bags themselves are local objects like every other bag in the garage.
/// </summary>
public class ConveyorLine : MonoBehaviour
{
    static readonly List<ConveyorLine> lines = new List<ConveyorLine>();
    /// <summary>Every enabled lane in the loaded scenes (SceneTrashKeeper re-homes their bags on a scene change).</summary>
    public static IReadOnlyList<ConveyorLine> Lines => lines;

    [Tooltip("Path from the elevator foot to the end of the slide (world positions of these transforms).")]
    public Transform[] waypoints;
    public ConveyorTray tray;
    [Tooltip("0 = the belt. Other lanes are retired (the old twin lane): they never run.")]
    public int lane;
    public float speed = 0.8f;
    public float spacing = 1.5f;
    [Tooltip("Bags pick up speed on the slides: speed × (1 + slideBoost × steepness), steepness = −dir.y (0 = constant speed).")]
    public float slideBoost = 1.5f;
    [Tooltip("Optional: rollers/buckets spun while the belt runs (local X axis).")]
    public Transform[] rollers;
    public float rollerSpin = 360f;
    [Tooltip("Junk Diverter: metres along the path where the grate shakes each bag (≤ 0 = no grate on this lane).")]
    public float divertAt = -1f;
    [Tooltip("Optional: the grate (punched when it shakes a bag).")]
    public Transform grate;
    [Tooltip("Optional: where the shed junk comes out (the tube's spout over the compactor). Empty = under the grate.")]
    public Transform junkSpout;

    struct Carrier
    {
        public TrashBag bag;
        public float dist;
        public bool hopping;      // still flying in from the pile
        public bool diverted;     // already been over the grate
        public float wobble;
    }

    readonly List<Carrier> carriers = new List<Carrier>();   // index 0 = furthest along
    Vector3[] pts;
    float[] cum;
    float length;
    bool waitingAtEnd;
    GarageRig rig;

    /// <summary>Items the diverter has dropped into the junk bin this session (tests / stats).</summary>
    public int ItemsDiverted { get; private set; }
    public int BagsDiverted { get; private set; }

    int Level => DepotMachineContext.Level(this, ShopIds.ConveyorBelt);

    /// <summary>This lane runs at the current conveyor level.</summary>
    public bool Running => isActiveAndEnabled && lane == 0 && Level >= ConveyorRules.BagBelt && length > 0.1f;
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

    /// <summary>Metres along the path to waypoint <paramref name="index"/> (clamped).</summary>
    public float DistanceToWaypoint(int index) =>
        cum == null || cum.Length == 0 ? 0f : cum[Mathf.Clamp(index, 0, cum.Length - 1)];

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
        // The belt's level went away (debug level reset / another save loaded), or this is a retired lane: its visuals are
        // hidden, so let the bags drop instead of hanging in mid-air where the belt used to be.
        int level = Level;
        if (lane != 0 || level < ConveyorRules.BagBelt) { DropAll(); return; }

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

            if (!c.diverted && divertAt > 0f && c.dist >= divertAt)
            {
                c.diverted = true;
                if (ConveyorRules.Diverts(level) && GarageRig.Decides) c.bag = Divert(c.bag, PointAt(divertAt, out _));
            }

            Vector3 p = PointAt(c.dist, out Vector3 dir);
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            float yaw = flat.sqrMagnitude > 0.01f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : c.bag.transform.eulerAngles.y;
            float t = (Application.isPlaying ? Time.time : 0f) * 3f + c.wobble;
            c.bag.transform.SetPositionAndRotation(p + Vector3.up * Mathf.Abs(Mathf.Sin(t)) * 0.03f,
                Quaternion.Euler(Mathf.Sin(t * 0.7f) * 4f, yaw, Mathf.Sin(t) * 5f));
            carriers[i] = c;

            if (i == 0 && c.dist >= length - 0.01f)
            {
                // End of the slide: into the queue rack, or wait (the line backs up behind it).
                if (tray != null && tray.Offer(c.bag, lane))
                {
                    carriers.RemoveAt(0);
                    i--;
                }
                else waitingAtEnd = true;
            }
        }
    }

    /// <summary>
    /// The grate: shake the bag; what <see cref="JunkDiverter"/> sheds drops into the compactor's hopper (the junk bin),
    /// the same way Magnus drops junk in, so it is paid and counted exactly like sorted junk for this lot's business.
    /// Returns the bag that carries on: a fresh, lighter one (same bag id; every machine gets the new mass and size), or
    /// the same bag when nothing was shed or the junk bin can't take anything right now (it is never shed twice).
    /// </summary>
    TrashBag Divert(TrashBag bag, Vector3 at)
    {
        if (bag == null || bag.Contents == null) return bag;
        if (grate != null && Application.isPlaying) Juice.Punch(grate, 0.25f, 0.3f);
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        var comp = rig != null ? rig.compactor : null;
        if (comp == null || comp.Busy || Compactor.AutomationPaused(this)) return bag;
        var fell = JunkDiverter.Split(bag.Contents, out var kept);
        if (fell.Count == 0) return bag;

        Vector3 pos = bag.transform.position;
        Quaternion rot = bag.transform.rotation;
        var fresh = TrashBag.Spawn(kept, pos, rot);
        if (fresh == null) return bag;
        fresh.SetInFlight();
        bag.Consume();   // the caller swaps its carrier to the fresh bag; no other machine anchor holds a riding bag

        Vector3 outAt = junkSpout != null ? junkSpout.position : at + Vector3.down * 0.25f;
        for (int k = 0; k < fell.Count; k++)
        {
            string key = fell[k].uid ?? (bag.name + k);
            var jitter = new Vector3(GarageRoll.Value(key + "/dx") - 0.5f, GarageRoll.Value(key + "/dy") - 0.5f, GarageRoll.Value(key + "/dz") - 0.5f) * 0.2f;
            var li = LooseItem.Spawn(fell[k], outAt + jitter, Quaternion.Euler(GarageRoll.Value(key + "/r") * 360f, GarageRoll.Value(key + "/s") * 360f, 0f), Vector3.zero, true);
            if (li == null) continue;
            li.SetInFlight();
            comp.AcceptFromRobot(li);
            ItemsDiverted++;
        }
        BagsDiverted++;
        Sfx.Play("rustle", at, 0.8f, 1.3f);
        Sfx.Play("click", at, 0.5f, 0.7f);
        Fx.Dust(at + Vector3.down * 0.2f, 0.6f);
        GameHUD.FloatingText(at + Vector3.up * 0.4f, $"SHAKE! -{fell.Count} junk", SortingKit.ComboOrange, 0.9f);
        Juice.Squash(fresh.JuiceTarget, 0.3f, 0.25f);
        return fresh;
    }

    /// <summary>The front bag is sitting at the end of the slide because the rack is full.</summary>
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

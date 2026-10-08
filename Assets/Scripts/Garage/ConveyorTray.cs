using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Bag Belt's QUEUE RACK beside the sorting table (conveyor_belt L1+, Docs/Progression.md §6.2). Bags come off the
/// slide into the first free bay and are then ordinary resting bags again: point at one and grab it (from the sort
/// station or on foot), carry it to the table and rip it open; Magnus takes them too (they are within his reach). The
/// rack holds <see cref="ConveyorRules.QueueSlots"/> bags; the rest of the queue waits along the belt behind it.
/// It never opens a bag. (The class keeps its old name so authored scenes keep their references.)
/// </summary>
public class ConveyorTray : MonoBehaviour
{
    static readonly List<ConveyorTray> trays = new List<ConveyorTray>();
    /// <summary>Every enabled rack in the loaded scenes (SceneTrashKeeper re-homes arriving bags on a scene change).</summary>
    public static IReadOnlyList<ConveyorTray> Trays => trays;

    public GarageRig rig;
    [Tooltip("Where queued bags rest, nearest the table first.")]
    public Transform[] slots;
    [Tooltip("Half extents (m) of a bay around its slot: a bag whose centre is inside is \"in the queue\".")]
    public Vector3 bayHalfExtents = new Vector3(0.36f, 0.5f, 0.36f);

    TrashBag[] arriving;          // hopping off the slide into a bay (scripted); a landed bag is plain physics
    float occupancyAt = -1f;
    bool[] occupied;
    TrashBag[] resting;

    static bool Valid(TrashBag b) => b != null && b.State != CarryState.Consumed && b.State != CarryState.Carried;

    public int SlotCount => slots != null && slots.Length > 0 ? slots.Length : 1;

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        EnsureSlots();
    }

    void OnEnable()
    {
        if (!trays.Contains(this)) trays.Add(this);
        if (rig == null) rig = GetComponentInParent<GarageRig>();
        if (rig != null) rig.Resolve();
    }

    void OnDisable() { trays.Remove(this); ReleaseArriving(); }
    void OnDestroy() => trays.Remove(this);

    void EnsureSlots()
    {
        int n = SlotCount;
        if (arriving == null || arriving.Length != n) { arriving = new TrashBag[n]; occupancyAt = -1f; }
        if (occupied == null || occupied.Length != n) { occupied = new bool[n]; occupancyAt = -1f; }
        if (resting == null || resting.Length != n) { resting = new TrashBag[n]; occupancyAt = -1f; }
    }

    Transform Slot(int i)
    {
        if (slots == null || slots.Length == 0) return transform;
        var s = slots[Mathf.Clamp(i, 0, slots.Length - 1)];
        return s != null ? s : transform;
    }

    /// <summary>Centre of bay <paramref name="i"/> (a bag's WorldCenter rests about here).</summary>
    public Vector3 BayCenter(int i) => Slot(i).position + Vector3.up * (bayHalfExtents.y - 0.1f);

    /// <summary>Is this world point inside bay <paramref name="i"/>?</summary>
    public bool InBay(int i, Vector3 world)
    {
        Vector3 d = Quaternion.Inverse(Slot(i).rotation) * (world - BayCenter(i));
        return Mathf.Abs(d.x) <= bayHalfExtents.x && Mathf.Abs(d.y) <= bayHalfExtents.y && Mathf.Abs(d.z) <= bayHalfExtents.z;
    }

    /// <summary>Which bays have a bag in them right now (resting there or hopping in). Refreshed a few times a second.</summary>
    void RefreshOccupancy(bool force = false)
    {
        EnsureSlots();
        float now = Application.isPlaying ? Time.time : 0f;
        if (!force && occupancyAt >= 0f && now - occupancyAt < 0.15f && Application.isPlaying) return;
        occupancyAt = now;
        for (int i = 0; i < occupied.Length; i++)
        {
            if (!Valid(arriving[i])) arriving[i] = null;
            occupied[i] = arriving[i] != null;
            resting[i] = null;
        }
        var all = Carryable.All;
        for (int k = 0; k < all.Count; k++)
        {
            if (!(all[k] is TrashBag b) || b.State == CarryState.Consumed || b.State == CarryState.Carried || b.HasBurst) continue;
            Vector3 c = b.WorldCenter;
            for (int i = 0; i < occupied.Length; i++)
            {
                if (!InBay(i, c)) continue;
                occupied[i] = true;
                if (resting[i] == null && b.Body != null && !b.Body.isKinematic) resting[i] = b;
                break;
            }
        }
    }

    /// <summary>Bags in the rack (resting or hopping in).</summary>
    public int BagCount
    {
        get
        {
            RefreshOccupancy();
            int n = 0;
            for (int i = 0; i < occupied.Length; i++) if (occupied[i]) n++;
            return n;
        }
    }

    public bool HasBag => BagCount > 0;
    public bool Full => BagCount >= SlotCount;

    /// <summary>The bag resting in bay <paramref name="i"/> (null = empty, or still hopping in).</summary>
    public TrashBag RestingIn(int i)
    {
        RefreshOccupancy();
        return i >= 0 && i < resting.Length ? resting[i] : null;
    }

    /// <summary>Still hopping off the slide into a bay (scripted, in the air).</summary>
    public bool IsArriving(TrashBag bag)
    {
        if (bag == null || arriving == null) return false;
        for (int i = 0; i < arriving.Length; i++) if (arriving[i] == bag) return true;
        return false;
    }

    /// <summary>This rack's bag: hopping into a bay, or resting in one (bulk compaction counts it as shop stock).</summary>
    public bool Owns(TrashBag bag)
    {
        if (bag == null) return false;
        EnsureSlots();
        for (int i = 0; i < arriving.Length; i++) if (arriving[i] == bag) return true;
        if (!Valid(bag)) return false;
        Vector3 c = bag.WorldCenter;
        for (int i = 0; i < arriving.Length; i++) if (InBay(i, c)) return true;
        return false;
    }

    /// <summary>Let go of a bag hopping in (something else is taking it). A resting bag needs no detaching.</summary>
    public void Detach(TrashBag bag)
    {
        EnsureSlots();
        for (int i = 0; i < arriving.Length; i++) if (arriving[i] == bag) arriving[i] = null;
        occupancyAt = -1f;
    }

    /// <summary>A bag arrives at the end of the slide. True = it is hopping into a free bay now.</summary>
    public bool Offer(TrashBag bag, int lane)
    {
        if (!Valid(bag) || Compactor.ShopBusy(this)) return false;
        RefreshOccupancy();
        int free = -1;
        for (int i = 0; i < occupied.Length; i++) if (!occupied[i]) { free = i; break; }
        if (free < 0) return false;
        arriving[free] = bag;
        occupied[free] = true;
        bag.SetInFlight();
        if (isActiveAndEnabled && Application.isPlaying) StartCoroutine(Settle(bag, free));
        else Land(bag, free);
        return true;
    }

    IEnumerator Settle(TrashBag bag, int i)
    {
        var slot = Slot(i);
        Quaternion rot = Quaternion.Euler(0f, slot.eulerAngles.y + (PickupVisuals.StableHash(bag.name) % 40 - 20), 0f);
        float dist = Vector3.Distance(bag.transform.position, slot.position);
        yield return ConveyorStock.WhileOwned(bag, () => arriving[i] == bag,
            SortingKit.Arc(bag.transform, slot.position + Vector3.up * 0.04f, Mathf.Clamp(0.16f + dist * 0.12f, 0.18f, 0.45f), 0.1f + dist * 0.12f, rot));
        if (arriving[i] != bag) yield break;
        Land(bag, i);
        if (!Valid(bag)) yield break;
        Sfx.Play("bag_land", slot.position, 0.7f, 1.1f);
        Sfx.Play("ding", slot.position, 0.35f, 1.5f);
        Juice.Squash(bag.JuiceTarget, 0.35f, 0.25f);
        Juice.Punch(transform, 0.08f, 0.2f);
        Fx.Dust(slot.position, 0.4f);
    }

    /// <summary>In the bay: back to physics as a plain resting bag (anyone may grab it; it does not burst).</summary>
    void Land(TrashBag bag, int i)
    {
        if (arriving[i] == bag) arriving[i] = null;
        occupancyAt = -1f;
        if (!Valid(bag)) return;
        bag.transform.position = Slot(i).position + Vector3.up * 0.04f;
        bag.EndCarry(Vector3.zero, false);
    }

    void Update()
    {
        if (DepotMachineContext.Level(this, ShopIds.ConveyorBelt) < ConveyorRules.BagBelt) ReleaseArriving();
    }

    /// <summary>The rack is hidden below conveyor L1 (debug level reset / another save loaded): drop what is hopping in.</summary>
    void ReleaseArriving()
    {
        if (arriving == null) return;
        bool any = false;
        for (int i = 0; i < arriving.Length; i++)
        {
            var b = arriving[i];
            arriving[i] = null;
            if (Valid(b) && b.Body != null && b.Body.isKinematic) { b.EndCarry(Vector3.zero, false); any = true; }
        }
        if (any) StopAllCoroutines();
        occupancyAt = -1f;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.6f);
        for (int i = 0; i < SlotCount; i++)
        {
            Gizmos.matrix = Matrix4x4.TRS(BayCenter(i), Slot(i).rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, bayHalfExtents * 2f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => trays.Clear();
}

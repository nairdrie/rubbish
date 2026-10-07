using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The tray at the bottom of the Bag Belt's slide, by the sorting table. It holds one bag per lane (kinematic, in
/// place). "Next bag!" (R) at the sort station takes the tray bag first; Magnus (gripper L2 / brain L3) takes bags from
/// here and slams them open himself; and with the Item Belt Add-On (conveyor L2) the auto-slitter rips the tray bag
/// straight onto the table whenever the table holds fewer than 10 items and nobody is mid-rip.
/// </summary>
public class ConveyorTray : MonoBehaviour
{
    static readonly List<ConveyorTray> trays = new List<ConveyorTray>();
    /// <summary>Every enabled tray in the loaded scenes (SceneTrashKeeper re-homes their bags on a scene change).</summary>
    public static IReadOnlyList<ConveyorTray> Trays => trays;

    public GarageRig rig;
    [Tooltip("Where the bag sits, one per lane (index = ConveyorLine.lane).")]
    public Transform[] slots;
    [Tooltip("Optional: the slitter blade (swipes when it rips a bag).")]
    public Transform slitterBlade;
    [Tooltip("The slitter only feeds the table while it holds fewer items than this.")]
    public int slitterBelowItems = 10;
    public float slitterInterval = 0.6f;

    TrashBag[] held;
    readonly HashSet<TrashBag> slitting = new HashSet<TrashBag>();
    float nextSlit;

    SortingTable Table => rig != null ? rig.table : null;

    public bool HasBag
    {
        get
        {
            EnsureSlots();
            for (int i = 0; i < held.Length; i++) if (Valid(held[i])) return true;
            return false;
        }
    }

    public int BagCount
    {
        get
        {
            EnsureSlots();
            int n = 0;
            for (int i = 0; i < held.Length; i++) if (Valid(held[i])) n++;
            return n;
        }
    }

    public bool Owns(TrashBag bag)
    {
        if (bag != null && slitting.Contains(bag)) return true;
        EnsureSlots();
        for (int i = 0; i < held.Length; i++) if (held[i] == bag && bag != null) return true;
        return false;
    }

    public void Detach(TrashBag bag)
    {
        EnsureSlots();
        for (int i = 0; i < held.Length; i++) if (held[i] == bag) held[i] = null;
        slitting.Remove(bag);
    }

    bool Holds(TrashBag bag)
    {
        EnsureSlots();
        for (int i = 0; i < held.Length; i++) if (held[i] == bag && bag != null) return true;
        return false;
    }

    /// <summary>World position of the first occupied slot (or the tray itself).</summary>
    public Vector3 BagPosition
    {
        get
        {
            EnsureSlots();
            for (int i = 0; i < held.Length; i++) if (Valid(held[i])) return held[i].WorldCenter;
            return transform.position;
        }
    }

    static bool Valid(TrashBag b) => b != null && b.State != CarryState.Consumed && b.State != CarryState.Carried;

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
        if (rig != null && rig.station != null && rig.station.tray == null) rig.station.tray = this;
    }

    void OnDisable() => trays.Remove(this);
    void OnDestroy() => trays.Remove(this);

    void EnsureSlots()
    {
        int n = slots != null && slots.Length > 0 ? slots.Length : 1;
        if (held == null || held.Length != n) held = new TrashBag[n];
    }

    Transform Slot(int lane)
    {
        if (slots == null || slots.Length == 0) return transform;
        var s = slots[Mathf.Clamp(lane, 0, slots.Length - 1)];
        return s != null ? s : transform;
    }

    /// <summary>A bag arrives at the end of the slide. True = it's in the tray now.</summary>
    public bool Offer(TrashBag bag, int lane)
    {
        EnsureSlots();
        if (!Valid(bag) || Compactor.ShopBusy(this)) return false;
        int i = Mathf.Clamp(lane, 0, held.Length - 1);
        if (Valid(held[i]))
        {
            // Twin lanes share the tray when one side is free.
            i = -1;
            for (int k = 0; k < held.Length; k++) if (!Valid(held[k])) { i = k; break; }
            if (i < 0) return false;
        }
        held[i] = bag;
        bag.SetInFlight();
        StartCoroutine(Settle(bag, Slot(i)));
        return true;
    }

    IEnumerator Settle(TrashBag bag, Transform slot)
    {
        Quaternion rot = Quaternion.Euler(0f, slot.eulerAngles.y + (PickupVisuals.StableHash(bag.name) % 40 - 20), 0f);
        yield return ConveyorStock.WhileOwned(bag, () => Holds(bag), SortingKit.Arc(bag.transform, slot.position, 0.18f, 0.12f, rot));
        if (!Valid(bag) || !Holds(bag)) yield break;
        Sfx.Play("bag_land", slot.position, 0.7f, 1.1f);
        Sfx.Play("ding", slot.position, 0.35f, 1.5f);
        Juice.Squash(bag.JuiceTarget, 0.35f, 0.25f);
        Juice.Punch(transform, 0.08f, 0.2f);
        Fx.Dust(slot.position, 0.4f);
    }

    /// <summary>Hand the tray's bag over (kinematic, in flight: the caller animates it). Null when the tray is empty.</summary>
    public TrashBag Take()
    {
        EnsureSlots();
        for (int i = 0; i < held.Length; i++)
        {
            if (!Valid(held[i])) { held[i] = null; continue; }
            var b = held[i];
            Detach(b);
            return b;
        }
        return null;
    }

    TrashBag Peek()
    {
        EnsureSlots();
        for (int i = 0; i < held.Length; i++) if (Valid(held[i])) return held[i];
        return null;
    }

    void Update()
    {
        if (DepotMachineContext.Level(this, ShopIds.ConveyorBelt) < 1) { ReleaseAll(); return; }
        if (!CanStartAutoSlit(DepotMachineContext.Level(this, ShopIds.ConveyorBelt), GarageRig.Decides, Time.time)) return;
        nextSlit = Time.time + slitterInterval;
        var table = Table;
        if (table == null || !HasBag || table.IsRipping) return;
        if (table.MaxItems - table.AvailableItemSlots >= slitterBelowItems) return;
        if (!table.CanAccept(Peek(), out _)) return;
        // A player mid-slam at the station counts as "mid-rip" too.
        var bag = Take();
        if (bag == null) return;
        slitting.Add(bag);
        StartCoroutine(Slit(bag, table));
    }

    internal bool CanStartAutoSlit(int conveyorLevel, bool decides, float now) =>
        conveyorLevel >= 2 && decides && now >= nextSlit && !Compactor.AutomationPaused(this);

    /// <summary>The tray is hidden below conveyor L1 (debug level reset / another save loaded): drop what it holds.</summary>
    void ReleaseAll()
    {
        if (held == null) return;
        StopAllCoroutines();
        for (int i = 0; i < held.Length; i++)
        {
            var b = held[i];
            held[i] = null;
            if (Valid(b)) b.EndCarry(Vector3.zero, false);
        }
        foreach (var b in slitting) if (Valid(b)) b.EndCarry(Vector3.zero, false);
        slitting.Clear();
    }

    IEnumerator Slit(TrashBag bag, SortingTable table)
    {
        Vector3 at = bag.WorldCenter;
        if (slitterBlade != null)
        {
            Juice.Punch(slitterBlade, 0.5f, 0.2f);
            slitterBlade.Rotate(Vector3.forward, 180f, Space.Self);
        }
        Sfx.Play("whoosh", at, 0.7f, 1.6f);
        Sfx.Play("rustle", at, 0.8f, 1.3f);
        GameHUD.FloatingText(at + Vector3.up * 0.5f, "SLIT!", SortingKit.ComboOrange, 1f);
        Fx.Burst(at, SortingKit.SackBlack, 10, 2.5f);
        yield return new WaitForSeconds(0.12f);
        if (!slitting.Remove(bag) || !Valid(bag)) yield break;
        // The table rips it and sprays the contents across the top (toward the bins).
        if (table != null) table.AcceptFromMachine(bag);
        else bag.EndCarry(Vector3.zero, false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => trays.Clear();
}

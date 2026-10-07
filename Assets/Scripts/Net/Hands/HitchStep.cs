using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A truck's hold points (package N2, Docs/Multiplayer.md §4.4, §13.5 "Hitching"; F1 §14.1 added the side ladders): two rear
/// steps and a ladder rung on each flank (<see cref="RideHolds"/>). Added at runtime to every truck with a hopper (no prefab
/// edit), with on-foot <see cref="InteractionZone"/>s behind and beside the truck: E = "Hitch a ride" (any truck, employee
/// or not), E or Space = "Hop off". Riders are pinned to the hold on each machine's own copy of the truck
/// (<see cref="PlayerBody"/> in co-op, <see cref="RideRider"/> solo), so rider and truck never jitter apart. It also
/// measures the truck's speed on this machine (catch it at ≤4.5 m/s; hopping off above 3 m/s is a stumble). F1's grip,
/// fling and drive-by grabs live on <see cref="RideTruck"/> / <see cref="RideRider"/>.
/// </summary>
[DisallowMultipleComponent]
public class HitchStep : MonoBehaviour, IInteractable
{
    static readonly List<HitchStep> all = new List<HitchStep>();
    public static IReadOnlyList<HitchStep> All => all;


    /// <summary>The holds' feet in the truck's local (0.67-scaled) space: 0/1 rear steps, 2/3 side ladder rungs (F1). Metres:
    /// <see cref="RideHolds"/>.</summary>
    public static readonly Vector3[] StepLocal = MakeStepLocal();

    static Vector3[] MakeStepLocal()
    {
        var a = new Vector3[RideHolds.Count];
        for (int i = 0; i < a.Length; i++) a[i] = RideHolds.FeetMetres(i) / 0.67f;
        return a;
    }

    public TruckControllerV2 Truck { get; private set; }
    public NetworkObject NetObject { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float Speed => new Vector2(Velocity.x, Velocity.z).magnitude;

    Vector3 lastPos;
    bool hasLast;

    public static HitchStep Find(ulong truckNetId)
    {
        if (truckNetId == HandsNet.None) return null;
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h != null && h.NetObject != null && h.NetObject.IsSpawned && h.NetObject.NetworkObjectId == truckNetId) return h;
        }
        return null;
    }

    public static void Ensure(TruckControllerV2 truck)
    {
        if (truck == null) return;
        RideTruck.Ensure(truck);
        if (truck.GetComponentInChildren<HitchStep>(true) != null) return;
        var go = ShopModKit.MetreRoot(truck.transform, "RearStepHitch").gameObject;
        var hs = go.AddComponent<HitchStep>();
        // One compact pocket per hold, in actual metres (independent of the imported truck's 0.67 scale).
        for (int hold = 0; hold < RideHolds.Count; hold++)
        {
            var bounds = RideHolds.BoardingBounds(hold);
            Zone(go, hs, bounds.center, bounds.size);
        }
    }

    static void Zone(GameObject go, HitchStep hs, Vector3 center, Vector3 size)
    {
        var z = go.AddComponent<InteractionZone>();
        z.center = center;
        z.size = size;
        z.mode = ZoneMode.OnFoot;
        z.score = 1.1f;
        z.targetOverride = hs;
    }

    void Awake()
    {
        Truck = GetComponentInParent<TruckControllerV2>();
        NetObject = GetComponentInParent<NetworkObject>();
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    /// <summary>Where a rider on hold <paramref name="step"/> stands (feet) and faces.</summary>
    public Pose StepPose(int step) => RideHolds.PoseOn(Truck != null ? Truck.transform : transform.parent, Mathf.Clamp(step, 0, RideHolds.Count - 1));

    void LateUpdate()
    {
        var t = Truck != null ? Truck.transform : transform;
        float dt = Time.deltaTime;
        if (hasLast && dt > 0f)
        {
            Vector3 v = (t.position - lastPos) / dt;
            if (v.sqrMagnitude > 60f * 60f) v = Vector3.zero; // a teleport (garage door), not speed
            Velocity = Vector3.Lerp(Velocity, v, 1f - Mathf.Exp(-8f * dt));
        }
        lastPos = t.position;
        hasLast = true;
    }

    // ==================================================================================== IInteractable

    HandsNet Net => HandsNet.Instance;
    static bool NetMode => HandsNet.Instance != null && HandsNet.Instance.Active;

    /// <summary>This machine's rider (co-op and solo).</summary>
    static RideRider Me => RideRider.LocalRider;

    bool RidingThis(RideRider me) => me != null && me.IsRidingTruck(this);

    /// <summary>The nearest free hold the player can actually reach and is looking toward. Never redirects
    /// someone at an occupied step onto the other side of the truck.</summary>
    public int FreeStepNear(Vector3 p)
    {
        var rt = RideTruck.For(Truck);
        if (rt == null) return -1;
        if (NetMode && NetObject == null) return -1;
        int best = -1;
        float bestD = float.MaxValue;
        var truckTransform = Truck != null ? Truck.transform : transform.parent;
        Vector3 local = ShopModKit.ToTruckMetres(truckTransform, p);
        var view = Camera.main;
        Vector3 forward = view != null ? view.transform.forward : (Me != null ? Me.transform.forward : truckTransform.forward);
        Vector3 localForward = Quaternion.Inverse(truckTransform.rotation) * forward;
        for (int k = 0; k < RideHolds.Count; k++)
        {
            if (!RideHolds.WithinBoardingBounds(k, local) || !RideHolds.FacesBoardingHold(k, local, localForward)) continue;
            if (RideRider.RiderAt(rt, k) != HandsNet.None) continue;
            float d = (StepPose(k).position - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    public bool CanInteract()
    {
        var me = Me;
        if (me == null || Truck == null) return false;
        if (NetMode && (NetObject == null || !NetObject.IsSpawned)) return false;
        if (RidingThis(me)) return true;
        if (!me.CanBoard) return false;
        if (Speed > HandsTuning.HitchMaxSpeed) return false;
        return FreeStepNear(me.transform.position) >= 0;
    }

    public string GetInteractionPrompt()
    {
        var me = Me;
        if (RidingThis(me)) return "Hop off";
        int k = me != null ? FreeStepNear(me.transform.position) : 0;
        string where = RideHolds.Name(Mathf.Max(0, k));
        return Speed > 2.5f ? $"Hitch a ride ({where}, RISKY)" : $"Hitch a ride ({where})";
    }

    public void OnInteract(GameObject interactor)
    {
        var me = Me;
        if (me == null) return;
        if (RidingThis(me)) { me.Unhitch(); return; }
        if (!CanInteract()) return;
        int k = FreeStepNear(me.transform.position);
        if (k >= 0) me.Hitch(this, k);
    }

    public void Highlight(bool highlight) { }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}

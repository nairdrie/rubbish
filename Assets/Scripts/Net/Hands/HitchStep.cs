using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A truck's hold points (package N2, Docs/Multiplayer.md §4.4, §13.5 "Hitching"; F1 §14.1 added the side ladders): two rear
/// steps and a ladder rung on each flank (<see cref="RideHolds"/>). Added at runtime to every truck with a hopper (no prefab
/// edit), with one on-foot <see cref="InteractionZone"/> round the back: E anywhere behind the truck, round its rear corners or
/// alongside its ladders = "Hitch a ride" onto the nearest free hold (any truck, employee or not; no aiming at a spot), E or
/// Space = "Hop off", Shift = switch spot (<see cref="RideRider.SwitchSpot"/>). Riders are pinned to the hold on each
/// machine's own copy of the truck (<see cref="PlayerBody"/> in co-op, <see cref="RideRider"/> solo), so rider and truck
/// never jitter apart. It also measures the truck's speed on this machine (catch it at ≤4.5 m/s; hopping off above 3 m/s is
/// a stumble). F1's crash flings and drive-by grabs live on <see cref="RideTruck"/> / <see cref="RideRider"/>.
/// </summary>
[DisallowMultipleComponent]
public class HitchStep : MonoBehaviour, IInteractable
{
    static readonly List<HitchStep> all = new List<HitchStep>();
    public static IReadOnlyList<HitchStep> All => all;

    /// <summary>The ride's place in the E/F list: above everything you can look at or stand by (≤ 2, e.g. the truck's own
    /// "Enter Truck" when you face it from behind), so E near the back is the ride; below any bag or piece of trash you can reach
    /// (<see cref="PlayerController.PickUpScore"/>, 2.1+: Nick, 12 Oct 2026, "prioritize bags and trash over boarding"), a held
    /// bag's toss (3), a downed friend's pickup (3+) and shooing a raccoon (<see cref="CritterPoke"/>).</summary>
    public const float ZoneScore = 2.05f;

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
        // One zone over everything either body can be boarded from. This root shrinks with the Curb Pup, so the box is sized
        // in rear-loader metres to still cover the Pup's area; CanInteract applies the exact area of the body the truck has.
        var z = go.AddComponent<InteractionZone>();
        z.center = new Vector3(0f, 0.6f, -3.3f);
        z.size = new Vector3(6.6f, 4f, 7f);
        z.mode = ZoneMode.OnFoot;
        z.score = ZoneScore;
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

    /// <summary>The free hold nearest a player standing at <paramref name="p"/>: their own side first, then round the back
    /// (−1 when all four are taken).</summary>
    public int BestFreeHold(Vector3 p)
    {
        var rt = RideTruck.For(Truck);
        if (rt == null || Truck == null) return -1;
        if (NetMode && NetObject == null) return -1;
        return RideHolds.NearestFreeHold(Truck.transform, p, RideRider.TakenHolds(rt));
    }

    public bool CanInteract()
    {
        var me = Me;
        if (me == null || Truck == null) return false;
        if (NetMode && (NetObject == null || !NetObject.IsSpawned)) return false;
        if (RidingThis(me)) return true;
        if (!me.CanBoard) return false;
        // Hands full (a bag on its way to the hopper, which is also back here): the hopper's "Toss into truck" is the prompt, not a ride.
        var carrier = me.Player != null ? me.Player.GetComponent<TrashCarrier>() : null;
        if (carrier != null && carrier.IsCarrying) return false;
        if (Speed > HandsTuning.HitchMaxSpeed) return false;
        // Anywhere round the back; a full truck still answers ("No room on the back") rather than going quiet.
        return RideHolds.InBoardingArea(Truck, me.transform.position);
    }

    public string GetInteractionPrompt()
    {
        var me = Me;
        if (RidingThis(me)) return "Hop off";
        return me != null && BestFreeHold(me.transform.position) < 0 ? "No room on the back" : "Hitch a ride";
    }

    public void OnInteract(GameObject interactor)
    {
        var me = Me;
        if (me == null) return;
        if (RidingThis(me)) { me.Unhitch(); return; }
        if (!CanInteract()) return;
        int k = BestFreeHold(me.transform.position);
        if (k >= 0) { me.Hitch(this, k); return; }
        Sfx.Play("buzz", me.transform.position, 0.5f);
        GameHUD.Toast("Every step and ladder on this truck is taken.", ToastStyle.Info, 1.6f);
    }

    public void Highlight(bool highlight) { }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}

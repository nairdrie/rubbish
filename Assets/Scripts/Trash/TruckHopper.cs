using System.Collections;
using UnityEngine;

/// <summary>
/// The garbage truck's rear mouth. Lives on a child named exactly "Hopper" of Truck V2.prefab (added by
/// Setup_Pickup). ICarryReceiver for TrashBags and LooseItems: E-toss arcs land here, and a child trigger
/// (CarryReceiverTrigger) catches physical throws. Capacity comes from GameSession (TruckLoadKg vs
/// TruckCapacityKg); a load that won't fit bounces off with a "Truck's full!" buzz.
/// Accepted bags are added to GameSession.TruckBags; loose items are piled into a loose BagContents
/// (sackVariant = -1, bagMassKg = 0). Curb bags pay their district's fee × PayFraction(age) (+ tip) with a
/// floating "+$4.00 On time!" / "+$1.60 Late…" and remove their GameSession.CurbBags record (CurbTrash).
/// <para><b>Co-op (Docs/Multiplayer.md §13, N1):</b> every truck belongs to a business (<see cref="BusinessId"/>; N3 sets it per
/// lot, empty = the host's). The host collects: a curb bag pays the truck's business (<see cref="CollectCurbBag"/>), and its
/// contents go into that business's cargo. On a guest, a curb bag landing here is a request to the host
/// (<see cref="WorldState.RequestHopper"/>); other things don't go into a friend's truck yet.</para>
/// </summary>
public class TruckHopper : MonoBehaviour, ICarryReceiver, ICarryReceiverFeedback
{
    [Tooltip("Max horizontal distance from the player for the E 'Toss into truck' prompt.")]
    public float tossRange = 2.8f;
    [Tooltip("Visual-only transform squashed on every accepted load (never the physics body).")]
    public Transform squashTarget;
    [Tooltip("Bobbing arrow shown while the local player carries something within indicatorShowDistance.")]
    public Transform indicator;
    public float indicatorShowDistance = 15f;
    [Tooltip("Downward kick (m/s at the rear) per load so the suspension bounces — the truck 'gulps' it.")]
    public float gulpKick = 0.35f;
    [Tooltip("Fill fraction that triggers a 'nearly full' heads-up.")]
    [Range(0.5f, 1f)] public float nearlyFullWarn = 0.85f;

    public Vector3 TossTargetPosition => transform.position;

    /// <summary>Pay (and the business it went to) of the last bag this hopper accepted on this machine (Net/Hands reports it).</summary>
    public float LastPay { get; private set; }
    public string LastPayBusiness { get; private set; }
    public CurbTrash.CollectFeedback LastCollectFeedback { get; private set; }
    public float TossRange => tossRange;

    /// <summary>The business that owns this truck (its pickup pay and cargo). Null or empty = the host's business.</summary>
    public string BusinessId { get; set; }

    /// <summary>Optional: who owns a truck whose <see cref="BusinessId"/> is empty (N3 can answer from the truck's lot).</summary>
    public static System.Func<TruckHopper, string> BusinessResolver;

    /// <summary>The owning business's id: <see cref="BusinessId"/>, else <see cref="BusinessResolver"/>, else the host's.</summary>
    public string ResolveBusiness()
    {
        if (!string.IsNullOrEmpty(BusinessId)) return BusinessId;
        string id = null;
        if (BusinessResolver != null)
        {
            try { id = BusinessResolver(this); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        if (!string.IsNullOrEmpty(id)) return id;
        var host = Businesses.Host;
        return host.IsValid ? host.Id : Businesses.LocalId;
    }

    /// <summary>This truck's load and capacity (its business's; the host's own truck is GameSession's).</summary>
    bool TruckLoad(out float kg, out float capacityKg)
    {
        string id = ResolveBusiness();
        if (Businesses.IsAuthority && (string.IsNullOrEmpty(id) || id == Businesses.LocalId))
        {
            kg = GameSession.TruckLoadKg;
            capacityKg = GameSession.TruckCapacityKg;
            return true;
        }
        return Businesses.TryGetTruckLoad(id, out kg, out capacityKg);
    }

    bool Fits(float kg) => !TruckLoad(out float load, out float cap) || load + kg <= cap + 0.001f;

    /// <summary>
    /// Host: a curb bag went into the truck of <paramref name="businessId"/> (null/empty = the host's): its contents join that
    /// business's cargo and its pickup pay (and Rep) go to that business. Returns the pay. The one collect path for the host's own
    /// tosses and guests' requests, so a bag is paid exactly once (CurbTrash removes the record as it pays).
    /// </summary>
    public static float CollectCurbBag(BagContents contents, string businessId, Vector3 fxPos, bool byArm, string stopId = null)
        => CollectCurbBag(contents, businessId, fxPos, byArm, stopId, true, out _);

    /// <summary>Host collection with an explicit local actor HUD decision; payment and event facts are identical.</summary>
    public static float CollectCurbBag(BagContents contents, string businessId, Vector3 fxPos, bool byArm, string stopId,
                                       bool actorFeedback, out CurbTrash.CollectFeedback feedback)
    {
        feedback = default;
        if (contents == null) return 0f;
        bool hostTruck = string.IsNullOrEmpty(businessId) || businessId == Businesses.LocalId;
        if (hostTruck) GameSession.AddBagToTruck(contents);
        else
        {
            var cargo = Businesses.HostCargo(businessId);
            if (cargo != null)
            {
                cargo.Add(contents);
                float kg = 0f;
                foreach (var b in cargo) if (b != null) kg += b.TotalMassKg;
                Businesses.TryGetTruckLoad(businessId, out _, out float cap);
                Businesses.SetTruckLoad(businessId, kg, cap);
            }
        }
        bool curb = GameSession.FindCurbBag(contents.bagId) != null;
        float pay;
        using (Ledger.ToBusiness(hostTruck ? null : businessId))
            pay = CurbTrash.CollectIntoTruck(contents, fxPos, byArm, actorFeedback, out feedback);
        if (curb)
            NetAudit.Event("collect", new System.Collections.Generic.Dictionary<string, object>
            {
                { "bag", contents.bagId }, { "business", hostTruck ? Businesses.LocalId : businessId }, { "pay", GameSession.ToCents(pay) },
            });
        var stop = PickupStop.Find(stopId ?? StopOf(contents.bagId));
        if (stop != null) stop.NotifyBagCollected(null);
        if (curb && CurbBagCollected != null)
        {
            // F5 (Docs/Multiplayer.md §14.5): Big Cleanup scores and poaching callouts listen here (host / solo only).
            try { CurbBagCollected(hostTruck ? Businesses.LocalId : businessId, contents, stopId ?? StopOf(contents.bagId), fxPos); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        return pay;
    }

    /// <summary>
    /// Host / solo: a curb bag was collected into a truck and paid (business id, contents, stop id, where). Raised once per bag,
    /// right after <see cref="CollectCurbBag"/> paid it. F5's Big Cleanup scoreboard and poaching listen.
    /// </summary>
    public static event System.Action<string, BagContents, string, Vector3> CurbBagCollected;

    static string StopOf(string bagId)
    {
        int slash = bagId != null ? bagId.IndexOf('/') : -1;
        return slash > 0 ? bagId.Substring(0, slash) : null;
    }

    Rigidbody truckBody;
    BobbingArrow arrow;
    bool indicatorShown;
    float lastRejectSfx = -999f, lastRejectToast = -999f;

    void Awake()
    {
        truckBody = GetComponentInParent<Rigidbody>();
        if (indicator == null)
        {
            var t = transform.Find("HopperIndicator");
            if (t != null) indicator = t;
        }
        if (indicator != null)
        {
            arrow = indicator.GetComponent<BobbingArrow>();
            indicator.gameObject.SetActive(false);
        }
        if (squashTarget == null)
        {
            var t = transform.Find("MouthVisual");
            if (t != null) squashTarget = t;
        }
    }

    void OnEnable() => CarryReceivers.Register(this);
    void OnDisable() => CarryReceivers.Unregister(this);

    // =====================================================================
    // ICarryReceiver
    // =====================================================================

    public bool CanAccept(Carryable item, out string reason)
    {
        reason = null;
        if (item == null || item.State == CarryState.Consumed) { reason = ""; return false; }
        if (!(item is TrashBag) && !(item is LooseItem)) { reason = "That doesn't go in the truck"; return false; }
        // Co-op: networked bags and items go in any truck (the host checks and collects, N2). A guest's local-only copy
        // (one that never went through the network) can only be a curb bag, which the host collects by its record (N1).
        if (GameSession.IsConnectedGuest && item.NetId == 0 && !(item is TrashBag tb && tb.IsCurbBag))
        {
            reason = "Only curb bags go in a friend's truck (for now)";
            return false;
        }
        if (!Fits(item.MassKg))
        {
            TruckLoad(out float kg, out float cap);
            reason = $"Truck's full! ({kg:0}/{cap:0} kg) — go dump it";
            return false;
        }
        return true;
    }

    public string GetTossPrompt(Carryable item)
    {
        if (item == null) return "Toss into truck";
        return item is TrashBag ? $"Toss into truck (+{item.MassKg:0.#} kg)" : $"Toss {item.DisplayName} into truck";
    }

    public void Accept(Carryable item)
    {
        if (item == null || item.State == CarryState.Consumed) return;
        if (!CanAccept(item, out string reason))
        {
            OnRejected(item, reason, true);
            return;
        }

        float kg = item.MassKg;
        TruckLoad(out float loadBefore, out float capBefore);
        float fillBefore = capBefore > 0f ? loadBefore / capBefore : 1f;
        bool guest = GameSession.IsConnectedGuest;

        LastPay = 0f;
        LastPayBusiness = null;
        LastCollectFeedback = default;
        if (guest && item.NetId != 0)
        {
            // Networked items never get here on a guest (Carryable.NetDeliver hands them to the host first).
            Debug.LogWarning($"[TruckHopper] guest Accept of networked item {item.NetId}: ignored");
            return;
        }
        if (guest && item is TrashBag gbag)
        {
            // Co-op: the host checks and pays (the truck's business); the bag leaves this player's hands either way.
            WorldState.Instance?.RequestHopper(gbag, this);
            var gstop = gbag.SourceStop;
            if (gstop != null) gstop.NotifyBagCollected(gbag);
            gbag.SourceStop = null;
        }
        else if (item is TrashBag bag)
        {
            var contents = bag.Contents ?? new BagContents
            {
                bagId = "misc/" + GameSession.NewUid(),
                foundAt = "",
                bagMassKg = kg,
                sackVariant = 0,
            };
            // Living world: a curb bag pays its pickup fee (scaled by lateness), loses its CurbBag record, and
            // the house's last bag pays the "Curb clean!" bonus. Bags from elsewhere (shop pile) pay nothing here.
            // The truck's business gets the bag and the pay (the host's own truck: GameSession).
            LastPayBusiness = ResolveBusiness();
            ulong localId = PlayerController.Local != null ? PlayerController.Local.OwnerClientId : FeedbackAudience.UnknownFinder; // coop:allow(local) personal collection HUD audience, not payout authority
            ulong actorId = bag.LastHolder != null && bag.LastHolder.Player != null
                ? bag.LastHolder.Player.OwnerClientId : FeedbackAudience.UnknownFinder;
            if (actorId == FeedbackAudience.UnknownFinder && bag.ByArm)
            {
                var driverTruck = GetComponentInParent<TruckControllerV2>();
                if (driverTruck != null) actorId = driverTruck.DriverClientId;
            }
            bool actorFeedback = !NetSession.InSession || actorId == localId;
            LastPay = CollectCurbBag(contents, LastPayBusiness, transform.position, bag.ByArm,
                                     bag.SourceStop != null ? bag.SourceStop.stopId : null, actorFeedback, out var feedback);
            LastCollectFeedback = feedback;
            WorldState.Instance?.BroadcastCollectionFx(transform.position, feedback, actorId);
            var stop = bag.SourceStop;
            if (stop != null) stop.NotifyBagCollected(bag);
            bag.SourceStop = null;
        }
        else if (item is LooseItem li)
        {
            string biz = ResolveBusiness();
            LastPayBusiness = biz;
            if (string.IsNullOrEmpty(biz) || biz == Businesses.LocalId) AddLooseToTruck(li.Item);
            else
            {
                // Co-op (N2): a loose item thrown into a neighbour's truck joins that business's cargo.
                var cargo = Businesses.HostCargo(biz);
                if (cargo != null && li.Item != null)
                {
                    cargo.Add(new BagContents { bagId = "loose/" + li.NetId, foundAt = li.Item != null ? li.Item.foundAt : "", bagMassKg = 0f, sackVariant = -1, items = { li.Item } });
                    float kgSum = 0f;
                    foreach (var b in cargo) if (b != null) kgSum += b.TotalMassKg;
                    Businesses.TryGetTruckLoad(biz, out _, out float capKg);
                    Businesses.SetTruckLoad(biz, kgSum, capKg);
                }
            }
        }

        // It's ours now: no more physics, no holder. Animate it down the gullet.
        item.MarkConsumed();
        item.StartCoroutine(SuckIn(item, transform.position, InsideDirection()));

        // --- juice ---
        Vector3 mouth = transform.position;
        float heavy = Mathf.InverseLerp(0.2f, 15f, kg);
        bool nearViewer = PlayerController.Local != null && FeedbackAudience.Nearby( // coop:allow(local) spatial FX for this observer
            (PlayerController.Local.transform.position - mouth).sqrMagnitude, 28f); // coop:allow(local) spatial FX for this observer
        if (nearViewer)
        {
            Sfx.Play("toss_in", mouth, Mathf.Lerp(0.6f, 1f, heavy), Mathf.Lerp(1.25f, 0.85f, heavy) * Random.Range(0.95f, 1.05f));
            if (item is TrashBag) Sfx.Play("crunch", mouth, 0.35f + 0.3f * heavy, Random.Range(0.9f, 1.15f));
            Fx.Dust(mouth, Mathf.Lerp(0.4f, 1.1f, heavy));
            if (squashTarget != null) Juice.Squash(squashTarget, Mathf.Lerp(0.15f, 0.35f, heavy), 0.25f);
            if (arrow != null) arrow.Pop();
        }
        Gulp(heavy);

        float fill = fillBefore;
        if (TruckLoad(out float loadAfter, out float capAfter)) fill = capAfter > 0f ? (loadAfter + (guest ? kg : 0f)) / capAfter : 1f;
        Color c = fill < 0.6f ? new Color(0.45f, 1f, 0.45f) : fill < 0.9f ? new Color(1f, 0.8f, 0.25f) : new Color(1f, 0.35f, 0.25f);
        bool localActor = !NetSession.InSession || item.LastHolder != null && item.LastHolder.Player != null &&
                          PlayerController.Local != null && item.LastHolder.Player.OwnerClientId == PlayerController.Local.OwnerClientId; // coop:allow(local) personal kg/near-full HUD audience only
        if (!localActor && item is TrashBag armBag && armBag.ByArm && PlayerController.Local != null) // coop:allow(local) personal side-loader HUD audience only
        {
            var driverTruck = GetComponentInParent<TruckControllerV2>();
            localActor = driverTruck != null && driverTruck.DriverClientId == PlayerController.Local.OwnerClientId; // coop:allow(local) personal side-loader HUD audience only
        }
        if (guest || localActor) GameHUD.FloatingText(mouth + Vector3.up * 0.5f, $"+{kg:0.#} kg", c, item is TrashBag ? 1f : 0.75f);

        if ((guest || localActor) && fillBefore < nearlyFullWarn && fill >= nearlyFullWarn && fill < 0.999f)
            GameHUD.Toast($"Truck's nearly full ({fill * capAfter:0}/{capAfter:0} kg)", ToastStyle.Info);
        else if ((guest || localActor) && fillBefore < 0.999f && fill >= 0.999f)
        {
            GameHUD.Toast("Truck's FULL — back to the depot to dump it!", ToastStyle.Bad, 3f);
            Sfx.Play("horn", mouth, 0.6f, 1.2f);
        }
    }

    // =====================================================================
    // ICarryReceiverFeedback
    // =====================================================================

    public void OnRejected(Carryable item, string reason, bool physical)
    {
        Vector3 mouth = transform.position;
        bool full = reason != null && reason.StartsWith("Truck's full");
        if (Time.time - lastRejectSfx > 0.35f)
        {
            lastRejectSfx = Time.time;
            Sfx.Play(full ? "truck_full" : "buzz", mouth, 1f, 1f);
            if (squashTarget != null) Juice.Punch(squashTarget, 0.2f, 0.2f);
            if (full) Gulp(0.2f);
        }
        if (!physical) return; // the carrier already toasted and is still holding it

        // Spit it back out toward whoever's behind the truck.
        if (item != null && item.Body != null && !item.Body.isKinematic && item.State != CarryState.Consumed)
        {
            Vector3 outDir = -InsideDirection(); outDir.y = 0f;
            outDir = outDir.sqrMagnitude > 0.01f ? outDir.normalized : Vector3.back;
            item.Body.linearVelocity = outDir * 3.2f + Vector3.up * 3.2f;
            item.Body.angularVelocity = Random.insideUnitSphere * 6f;
            Juice.Punch(item.JuiceTarget, 0.25f, 0.2f);
        }
        GameHUD.FloatingText(mouth + Vector3.up * 0.6f, full ? "FULL!" : "NOPE", new Color(1f, 0.35f, 0.25f), 1.1f);
        if (!string.IsNullOrEmpty(reason) && Time.time - lastRejectToast > 1.5f)
        {
            lastRejectToast = Time.time;
            GameHUD.Toast(reason, ToastStyle.Bad);
        }
    }

    // =====================================================================
    // Internals
    // =====================================================================

    /// <summary>Direction from the mouth into the truck body (the truck's forward).</summary>
    Vector3 InsideDirection()
    {
        var root = truckBody != null ? truckBody.transform : transform.parent;
        return root != null ? root.forward : transform.forward;
    }

    /// <summary>Appends a loose item to the newest loose pile in the truck (or starts one).</summary>
    public static void AddLooseToTruck(ItemInstance it)
    {
        if (it == null) return;
        BagContents pile = null;
        var bags = GameSession.TruckBags;
        for (int i = bags.Count - 1; i >= 0; i--)
            if (bags[i] != null && bags[i].sackVariant == -1) { pile = bags[i]; break; }
        if (pile == null)
        {
            pile = new BagContents
            {
                bagId = "loose/" + GameSession.NewUid(),
                foundAt = it.foundAt,
                bagMassKg = 0f,
                sackVariant = -1,
            };
            bags.Add(pile);
        }
        pile.items.Add(it);
        GameSession.NotifyChanged();
    }

    void Gulp(float heavy01)
    {
        if (truckBody == null || truckBody.isKinematic) return;
        // Push the rear down a touch; the suspension bounces it back up = satisfying "gulp".
        Vector3 at = transform.position;
        truckBody.AddForceAtPosition(Vector3.down * gulpKick * Mathf.Lerp(0.6f, 1.4f, heavy01) * truckBody.mass, at, ForceMode.Impulse);
    }

    static IEnumerator SuckIn(Carryable item, Vector3 mouth, Vector3 inward)
    {
        if (item == null) yield break;
        var go = item.gameObject;
        // Safety net: if anything interrupts the animation, it still disappears.
        Object.Destroy(go, 1.5f);

        var t = item.transform;
        Vector3 start = t.position;
        Vector3 center = item.WorldCenter;
        Vector3 pivotOffset = start - center;
        Vector3 end = mouth + inward * 0.5f;
        Vector3 s0 = t.localScale;
        Quaternion r0 = t.rotation;
        const float dur = 0.16f;
        float k = 0f;
        while (k < 1f)
        {
            if (t == null) yield break;
            k = Mathf.Min(1f, k + Time.deltaTime / dur);
            float e = k * k;
            Vector3 c = Vector3.Lerp(center, end, e);
            float sq = Mathf.Sin(k * Mathf.PI) * 0.35f;
            float s = Mathf.Lerp(1f, 0.55f, e);
            t.localScale = new Vector3(s0.x * s * (1f + sq), s0.y * s * (1f - sq), s0.z * s * (1f + sq));
            t.rotation = Quaternion.AngleAxis(240f * e, Vector3.up) * r0;
            t.position = c + pivotOffset * s;
            yield return null;
        }
        if (go != null) Juice.ShrinkAndDestroy(go, 0.12f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => BusinessResolver = null;

    void Update()
    {
        if (indicator == null) return;
        var carrier = TrashCarrier.Local;
        bool show = false;
        bool fits = true;
        if (carrier != null && carrier.IsCarrying && (carrier.Held is TrashBag || carrier.Held is LooseItem))
        {
            var p = carrier.Player;
            bool driving = p != null && p.IsDriving;
            Vector3 d = carrier.transform.position - transform.position; d.y = 0f;
            show = !driving && d.sqrMagnitude < indicatorShowDistance * indicatorShowDistance;
            if (show) fits = Fits(carrier.Held.MassKg);
        }
        if (show != indicatorShown)
        {
            indicatorShown = show;
            indicator.gameObject.SetActive(show);
        }
        if (show && arrow != null) arrow.SetColor(fits ? PickupVisuals.GoodColor : PickupVisuals.BadColor);
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The One-Man Route's walk-up hatch (Hauler capstone, Docs/Progression.md §6.9): a bag chute on the street side of a depot
/// lot whose OWNER has built <see cref="CallingIds.OneManRoute"/>. The owner tosses curb bags (or unloads a Compactor
/// Backpack) into it on foot and is paid exactly as a truck's hopper would pay, plus the foot bonus
/// (<see cref="HaulerTuning.FootBonusPct"/>); the bag then slides down the chute onto that lot's tipping floor, so sorting
/// happens as usual (no TIP IT step).
/// <para><b>Authority.</b> Built at runtime on every machine at one canonical pose per lot (<see cref="HaulerTuning.HatchLocal"/>
/// in the lot's street frame), so it is the same receiver everywhere. It is an ordinary <see cref="ICarryReceiver"/> outside
/// any shop interior: a guest's toss is the existing guarded request (HandsNet's DeliverRpc) and the host runs
/// <see cref="Accept"/>. Pay goes through <see cref="TruckHopper.CollectCurbBag"/> under <c>Ledger.ToBusiness</c> for the
/// lot's business (the same record removal, BagCollected fact, curb bonus and audit as a hopper), never through a guest.</para>
/// </summary>
[DisallowMultipleComponent]
public class FootHatch : MonoBehaviour, ICarryReceiver, ICarryReceiverFeedback
{
    public const string FootBonusLedgerId = "foot_bonus";
    const string DepotFull = "Depot's full: sort or compact some trash first.";

    public int lot = -1;

    static readonly FootHatch[] byLot = new FootHatch[DepotRowLayout.LotCount];
    /// <summary>Host: kilograms sliding down each lot's chute right now (paid, not yet on the tipping floor).</summary>
    static readonly float[] chuteKg = new float[DepotRowLayout.LotCount];
    public static FootHatch ForLot(int lot) => lot >= 0 && lot < byLot.Length ? byLot[lot] : null;

    /// <summary>The last bag this hatch took on this machine (host): what it paid, to whom (Net/Hands reports it).</summary>
    public long LastPayCents { get; private set; }
    public long LastFootCents { get; private set; }
    public string LastBusinessName { get; private set; }
    public CurbTrash.CollectFeedback LastFeedback { get; private set; }

    Transform mouth, flap, body;
    GameObject arrow;
    float flapKick, lastRejectAt = -99f;

    public Vector3 TossTargetPosition => mouth != null ? mouth.position : transform.position + Vector3.up;
    public float TossRange => HaulerTuning.HatchTossRange;

    void OnEnable() => CarryReceivers.Register(this);
    void OnDisable() => CarryReceivers.Unregister(this);
    void OnDestroy() { if (lot >= 0 && lot < byLot.Length && byLot[lot] == this) byLot[lot] = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { for (int i = 0; i < byLot.Length; i++) { byLot[i] = null; chuteKg[i] = 0f; } }

    // ==================================================================================== who may use it

    /// <summary>The One-Man Route level of <paramref name="client"/> as every machine can read it.</summary>
    public static int RouteLevel(ulong client)
    {
        int level = Workshop.CallingLevelForClient(client, CallingIds.OneManRoute);
        if (client == Permissions.LocalClientId) level = Mathf.Max(level, GameSession.GetUpgradeLevel(CallingIds.OneManRoute));
        return level;
    }

    /// <summary>Does lot <paramref name="lot"/> have a hatch right now (claimed, its owner here and holding the capstone)?</summary>
    public static bool Wanted(int lot)
    {
        if (!DepotRowWorld.Active || !Workshop.TryBusinessAt(lot, out var business)) return false;
        return RouteLevel(business.ownerClientId) > 0;
    }

    static ulong ActorOf(Carryable item)
    {
        var carrier = item.Holder != null ? item.Holder : item.LastHolder;
        return carrier != null && carrier.Player != null && carrier.Player.IsSpawned ? carrier.Player.OwnerClientId : FeedbackAudience.UnknownFinder;
    }

    /// <summary>The rule, by actor and contents (the backpack unload asks before it makes a bag).</summary>
    public bool CanAcceptFrom(ulong actor, BagContents contents, out string reason)
    {
        reason = null;
        if (!Workshop.TryBusinessAt(lot, out var business)) { reason = "Nobody's home."; return false; }
        if (actor != business.ownerClientId)
        {
            string owner = business.ownerName.ToString();
            reason = string.IsNullOrEmpty(owner) ? "Only this depot's owner can use the hatch." : $"Only {owner} can use this hatch.";
            return false;
        }
        if (RouteLevel(actor) <= 0) { reason = "The hatch needs the One-Man Route."; return false; }
        if (contents == null || contents.sackVariant < 0) { reason = "Bags only: it's a bag chute."; return false; }
        if (GameSession.FindCurbBag(contents.bagId) == null) { reason = "That one's already paid for. Carry it inside."; return false; }
        float sliding = lot >= 0 && lot < chuteKg.Length ? chuteKg[lot] : 0f;
        if (!DepotBacklog.Fits(DepotBacklog.UsedKg(lot) + sliding, contents.TotalMassKg, DepotBacklog.CapacityKg(lot))) { reason = DepotFull; return false; }
        return true;
    }

    // ==================================================================================== ICarryReceiver

    public bool CanAccept(Carryable item, out string reason)
    {
        reason = null;
        if (item == null || item.State == CarryState.Consumed) { reason = ""; return false; }
        if (!(item is TrashBag bag) || bag.HasBurst) { reason = "Bags only: it's a bag chute."; return false; }
        return CanAcceptFrom(ActorOf(item), bag.Contents, out reason);
    }

    public string GetTossPrompt(Carryable item) =>
        item == null ? "Toss into the walk-up hatch" : $"Toss into the walk-up hatch (+{Mathf.RoundToInt(HaulerTuning.FootBonusPct * 100f)}% on foot)";

    public void Accept(Carryable item)
    {
        if (item == null || item.State == CarryState.Consumed) return;
        if (!CanAccept(item, out string reason)) { OnRejected(item, reason, true); return; }
        if (GameSession.IsConnectedGuest)
        {
            // Networked bags never get here on a guest (Carryable.NetDeliver hands them to the host first).
            Debug.LogWarning($"[FootHatch] guest Accept of item {item.NetId}: ignored");
            return;
        }
        var bag = (TrashBag)item;
        var contents = bag.Contents;
        Workshop.TryBusinessAt(lot, out var business);
        ulong actor = ActorOf(item);
        bool actorFeedback = !NetSession.InSession || actor == Permissions.LocalClientId;
        Vector3 at = TossTargetPosition;
        Vector3? exit = ChuteExit(lot);

        long pay = HostCollectOnFoot(contents, business.Id, at, bag.SourceStop != null ? bag.SourceStop.stopId : null,
                                     actorFeedback, exit == null, out long foot, out var feedback);
        LastPayCents = pay;
        LastFootCents = foot;
        LastBusinessName = business.Name;
        LastFeedback = feedback;
        var stop = bag.SourceStop;
        if (stop != null) stop.NotifyBagCollected(bag);
        bag.SourceStop = null;
        WorldState.Instance?.BroadcastCollectionFx(at, feedback, actor);
        HandsNet.Instance?.HostNotifyHatchPay(actor, pay, foot, feedback, business.Name, at);
        if (actorFeedback && foot > 0)
            GameHUD.FloatingText(at + Vector3.up * 2.3f, $"+{Economy.Money(foot / 100f)} on foot!", new Color(1f, 0.85f, 0.3f), 1f);

        // It's the depot's now: down the chute, then onto the tipping floor as an ordinary (paid) bag.
        item.MarkConsumed();
        item.transform.position = at - item.transform.rotation * Vector3.Scale(item.LocalBounds().center, item.transform.lossyScale);
        Juice.ShrinkAndDestroy(item.gameObject, 0.16f);
        Gulp(Mathf.InverseLerp(0.2f, 15f, contents != null ? contents.TotalMassKg : 5f));
        if (exit.HasValue && contents != null)
        {
            chuteKg[lot] += contents.TotalMassKg;
            StartCoroutine(DropInside(contents, exit.Value));
        }
    }

    /// <summary>
    /// Host / solo: an on-foot delivery for <paramref name="businessId"/>. The pay path IS the hopper's
    /// (<see cref="TruckHopper.CollectCurbBag"/>: record removed, fee x lateness + tip, curb bonus, BagCollected, audit, Big
    /// Cleanup hook), then the foot bonus on the pickup pay. The hopper path also loads the truck; a walked-in bag goes down
    /// the chute instead, so it is taken straight back out of the cargo (unless there is no chute to drop it through).
    /// Returns the pickup pay in cents.
    /// </summary>
    public static long HostCollectOnFoot(BagContents contents, string businessId, Vector3 fxPos, string stopId, bool actorFeedback,
                                         bool leaveInTruck, out long footCents, out CurbTrash.CollectFeedback feedback)
    {
        footCents = 0;
        feedback = default;
        if (contents == null || GameSession.IsConnectedGuest) return 0;
        bool hostBusiness = string.IsNullOrEmpty(businessId) || businessId == Businesses.LocalId;
        var record = GameSession.FindCurbBag(contents.bagId);
        string districtId = record != null ? Districts.ForStopId(record.stopId).id : null;

        float pay = TruckHopper.CollectCurbBag(contents, businessId, fxPos, false, stopId, actorFeedback, out feedback);

        if (!leaveInTruck)
        {
            if (hostBusiness) { if (GameSession.TruckBags.Remove(contents)) GameSession.NotifyChanged(); }
            else
            {
                var cargo = Businesses.HostCargo(businessId);
                if (cargo != null && cargo.Remove(contents))
                {
                    float kg = 0f;
                    foreach (var b in cargo) if (b != null) kg += b.TotalMassKg;
                    Businesses.TryGetTruckLoad(businessId, out _, out float cap);
                    Businesses.SetTruckLoad(businessId, kg, cap);
                }
            }
        }
        long payCents = GameSession.ToCents(pay);
        footCents = HaulerRules.FootBonusCents(payCents);
        if (footCents > 0)
            using (Ledger.ToBusiness(hostBusiness ? null : businessId))
                Ledger.Credit(footCents / 100f, "On foot!", FootBonusLedgerId, null, districtId);
        return payCents;
    }

    /// <summary>Where the chute lets out: above the lot's tipping floor (null while that interior is not up on this machine).</summary>
    static Vector3? ChuteExit(int lot)
    {
        var depot = DepotRowScene.Lot(lot);
        if (depot == null || depot.interior == null || !depot.InteriorOn) return null;
        var tipFloor = depot.interior.GetComponentInChildren<TippingFloor>(true);
        if (tipFloor == null) return null;
        float floor = SortingKit.FloorY(tipFloor.pileCenter, tipFloor.transform.position.y);
        return new Vector3(tipFloor.pileCenter.x, floor + 3.1f, tipFloor.pileCenter.z);
    }

    IEnumerator DropInside(BagContents contents, Vector3 exit)
    {
        yield return new WaitForSeconds(0.35f); // the slide
        chuteKg[lot] = Mathf.Max(0f, chuteKg[lot] - contents.TotalMassKg);
        int h = PickupVisuals.StableHash(contents.bagId ?? "hatch"); // stable, cosmetic scatter (no RNG)
        Vector3 from = exit + new Vector3(((h & 255) / 255f - 0.5f) * 1.2f, 0f, (((h >> 8) & 255) / 255f - 0.5f) * 1.6f);
        var bag = TrashBag.Spawn(contents, from, Quaternion.Euler(0f, h % 360, 0f), Vector3.down * 1.5f);
        if (bag == null) yield break;
        bag.NoBurstUntil = Time.time + 3f;
        Sfx.Play("whoosh", from, 0.35f, 0.9f);
    }

    // ==================================================================================== ICarryReceiverFeedback

    public void OnRejected(Carryable item, string reason, bool physical)
    {
        Vector3 at = TossTargetPosition;
        if (Time.time - lastRejectAt > 0.4f)
        {
            lastRejectAt = Time.time;
            Sfx.Play("buzz", at, 0.9f, 1f);
            if (body != null) Juice.Punch(body, 0.12f, 0.2f);
        }
        if (!physical) return; // the carrier already toasted and still holds it
        if (item != null && item.Body != null && !item.Body.isKinematic && item.State != CarryState.Consumed)
        {
            Vector3 outDir = transform.forward; outDir.y = 0f;
            item.Body.linearVelocity = outDir.normalized * 3f + Vector3.up * 3f;
            Juice.Punch(item.JuiceTarget, 0.25f, 0.2f);
        }
        GameHUD.FloatingText(at + Vector3.up * 0.6f, "NOPE", new Color(1f, 0.35f, 0.25f), 1.1f);
        var holder = item != null ? item.LastHolder : null;
        if (!string.IsNullOrEmpty(reason) && holder != null && holder.IsLocalOwner) GameHUD.Toast(reason, ToastStyle.Bad);
    }

    // ==================================================================================== looks

    void Gulp(float heavy)
    {
        Vector3 at = TossTargetPosition;
        flapKick = 1f;
        var me = PlayerTells.LocalTells;
        bool near = me == null || FeedbackAudience.Nearby((me.transform.position - at).sqrMagnitude, 28f);
        if (!near) return;
        Sfx.Play("toss_in", at, Mathf.Lerp(0.6f, 1f, heavy), Mathf.Lerp(1.3f, 0.9f, heavy));
        Sfx.Play("crunch", at, 0.3f + 0.3f * heavy, 1.1f);
        Fx.Dust(at, Mathf.Lerp(0.4f, 1f, heavy));
        if (body != null) Juice.Squash(body, Mathf.Lerp(0.08f, 0.18f, heavy), 0.25f);
    }

    void Update()
    {
        if (flap != null)
        {
            flapKick = Mathf.MoveTowards(flapKick, 0f, Time.deltaTime * 2.2f);
            flap.localRotation = Quaternion.Euler(-70f * Mathf.Sin(flapKick * Mathf.PI) * flapKick, 0f, 0f);
        }
        if (arrow == null) return;
        // A "put it HERE" arrow for the one player who can use this hatch, while they have something for it.
        bool show = false;
        var carrier = TrashCarrier.Local;
        if (carrier != null && Workshop.TryBusinessAt(lot, out var business) && business.ownerClientId == Permissions.LocalClientId)
        {
            bool carrying = carrier.Held is TrashBag held && held.IsCurbBag;
            var hands = HandsNet.Instance;
            bool packed = hands != null && hands.IsSpawned && hands.PackOf(Permissions.LocalClientId).Count > 0;
            Vector3 d = carrier.transform.position - transform.position; d.y = 0f;
            show = (carrying || packed) && d.sqrMagnitude < 18f * 18f && (carrier.Player == null || !carrier.Player.IsDriving);
        }
        if (arrow.activeSelf != show) arrow.SetActive(show);
    }

    // ==================================================================================== build (every machine)

    /// <summary>Make every lot's hatch match the replicated truth (called about once a second on every machine).</summary>
    public static void Sync()
    {
        for (int lot = 0; lot < byLot.Length; lot++)
        {
            var depot = DepotRowScene.Lot(lot);
            bool want = depot != null && depot.facade != null && depot.facade.gameObject.activeInHierarchy && Wanted(lot);
            var hatch = byLot[lot];
            if (hatch == null)
            {
                if (!want) continue;
                hatch = byLot[lot] = Build(lot, depot);
                Vector3 at = hatch.transform.position + Vector3.up;
                Fx.Dust(at, 1.4f);
                Sfx.Play("slam", at, 0.7f, 1.1f);
            }
            if (hatch.gameObject.activeSelf != want) hatch.gameObject.SetActive(want);
        }
    }

    static FootHatch Build(int lot, DepotLot depot)
    {
        var go = new GameObject($"Walk-up Hatch (lot {lot})") { layer = 2 };
        go.transform.SetParent(depot.transform, true);
        var pose = DepotRowLayout.StreetPose(lot, HaulerTuning.HatchLocal);
        go.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var hatch = go.AddComponent<FootHatch>();
        hatch.lot = lot;
        Transform root = go.transform;

        var steel = ShopModKit.Lit("mat/hauler_hatch_steel", new Color(0.46f, 0.5f, 0.55f), 0.55f, 0.6f);
        var dark = ShopModKit.Lit("mat/hauler_hatch_dark", new Color(0.05f, 0.05f, 0.06f), 0.2f);
        var orange = ShopModKit.Lit("mat/hauler_hatch_orange", new Color(1f, 0.55f, 0.1f), 0.4f);
        var yellow = ShopModKit.Lit("mat/hauler_hatch_yellow", new Color(0.98f, 0.82f, 0.16f), 0.35f);

        hatch.body = ShopModKit.Pivot("Body", root, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cube, "Kiosk", hatch.body, steel, new Vector3(0f, 0.75f, 0f), new Vector3(1.3f, 1.5f, 0.9f));
        ShopModKit.Prim(PrimitiveType.Cube, "Mouth", hatch.body, dark, new Vector3(0f, 0.98f, 0.41f), new Vector3(0.92f, 0.7f, 0.1f));
        ShopModKit.Prim(PrimitiveType.Cube, "Lip", hatch.body, orange, new Vector3(0f, 0.6f, 0.5f), new Vector3(1f, 0.06f, 0.22f), new Vector3(18f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Hood", hatch.body, orange, new Vector3(0f, 1.56f, 0.2f), new Vector3(1.5f, 0.08f, 1.3f), new Vector3(-8f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Stripe", hatch.body, yellow, new Vector3(0f, 0.28f, 0.455f), new Vector3(1.3f, 0.2f, 0.02f));
        for (int i = -2; i <= 2; i++)
            ShopModKit.Prim(PrimitiveType.Cube, "Chevron", hatch.body, dark, new Vector3(i * 0.26f, 0.28f, 0.468f), new Vector3(0.09f, 0.2f, 0.01f), new Vector3(0f, 0f, 35f));
        // The chute: a fat pipe out of the back of the kiosk, up and into the depot.
        ShopModKit.Prim(PrimitiveType.Cylinder, "Chute", hatch.body, steel, new Vector3(0f, 1.25f, -0.95f), new Vector3(0.6f, 0.75f, 0.6f), new Vector3(68f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Chute collar", hatch.body, orange, new Vector3(0f, 1.02f, -0.47f), new Vector3(0.68f, 0.05f, 0.68f), new Vector3(68f, 0f, 0f));

        hatch.flap = ShopModKit.Pivot("Flap hinge", hatch.body, new Vector3(0f, 1.33f, 0.47f));
        ShopModKit.Prim(PrimitiveType.Cube, "Flap", hatch.flap, yellow, new Vector3(0f, -0.17f, 0f), new Vector3(0.9f, 0.34f, 0.03f));
        hatch.mouth = ShopModKit.Pivot("Mouth point", root, new Vector3(0f, 1f, 0.5f));

        var label = WorkshopModels.MakeLabel(root, "Hatch label", "WALK-UP HATCH", 2.2f, new Color(0.08f, 0.08f, 0.1f));
        label.transform.localPosition = new Vector3(0f, 1.62f, 0.86f);
        label.transform.localRotation = Quaternion.Euler(8f, 180f, 0f);
        label.rectTransform.sizeDelta = new Vector2(1.3f, 0.2f);
        label.enableAutoSizing = true; label.fontSizeMin = 0.5f; label.fontSizeMax = 2.2f;
        var sub = WorkshopModels.MakeLabel(root, "Hatch sublabel", "ONE-MAN ROUTE  ·  BAGS ONLY", 1.2f, new Color(0.97f, 0.9f, 0.63f));
        sub.transform.localPosition = new Vector3(0f, 0.5f, 0.462f);
        sub.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        sub.rectTransform.sizeDelta = new Vector2(1.2f, 0.14f);
        sub.enableAutoSizing = true; sub.fontSizeMin = 0.4f; sub.fontSizeMax = 1.2f;

        foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r.gameObject.layer != 2) r.gameObject.layer = 2;

        // One deliberate collider for the kiosk, and one trigger in front of the mouth for honest throws.
        var solid = new GameObject("Kiosk collider") { layer = SortingKit.GroundLayer };
        solid.transform.SetParent(root, false);
        var box = solid.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.8f, -0.05f);
        box.size = new Vector3(1.3f, 1.6f, 1f);
        var trigger = new GameObject("Mouth trigger") { layer = 2 };
        trigger.transform.SetParent(root, false);
        var tb = trigger.AddComponent<BoxCollider>();
        tb.isTrigger = true;
        tb.center = new Vector3(0f, 1.05f, 0.75f);
        tb.size = new Vector3(1.05f, 0.95f, 0.6f);
        var crt = trigger.AddComponent<CarryReceiverTrigger>();
        crt.receiverOverride = hatch;
        crt.onlyPlayerReleased = true;

        hatch.arrow = new GameObject("Hatch arrow") { layer = 2 };
        hatch.arrow.transform.SetParent(root, false);
        hatch.arrow.transform.localPosition = new Vector3(0f, 2.6f, 0.5f);
        var bob = hatch.arrow.AddComponent<BobbingArrow>();
        bob.color = PickupVisuals.GoodColor;
        hatch.arrow.SetActive(false);
        return hatch;
    }
}

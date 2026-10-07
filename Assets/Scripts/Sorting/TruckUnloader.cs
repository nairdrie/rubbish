using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The "TIP IT!" lever by the tipping floor. When the truck is parked in the bay (within truckRange of the
/// TruckSpawner) and has cargo, pulling it cascades every bag out of the truck's hopper, one every
/// bagInterval seconds, arcing onto the tipping floor. Loose cargo (BagContents.sackVariant == -1) comes
/// out as individual LooseItems. On foot only.
/// With the auto_tipper upgrade (Docs/Progression.md §6.2) the lever pulls itself (Garage/AutoTipper) and bags fly out
/// three times faster; at L2 ("Direct Feed") tipped bags go straight onto the Bag Belt (<see cref="directFeed"/>), and only
/// the overflow lands on the pile.
/// </summary>
public class TruckUnloader : MonoBehaviour, IInteractable
{
    /// <summary>Something that can take a bag straight from the truck (the conveyor's Bag Belt).</summary>
    public interface IBagIntake
    {
        /// <summary>Take this bag (launched from <paramref name="from"/>)? False = it goes on the pile instead.</summary>
        bool TryTakeFromTruck(BagContents contents, Vector3 from);
    }

    [Tooltip("Lever arm pivot (rotates about local X).")]
    public Transform lever;
    public float leverRestAngle = -40f;
    public float leverPulledAngle = 45f;
    [Tooltip("Optional status sign (\"TIP IT!\" / \"EMPTY\").")]
    public TextMeshPro statusText;

    [Header("Tipping floor (world space box the bags land in)")]
    public Vector3 pileCenter = new Vector3(5.75f, 0f, -3.2f);
    [Tooltip("World X / Z extent of the landing area.")]
    public Vector2 pileSize = new Vector2(1.5f, 2.2f);

    [Tooltip("The bay this lever serves (Depot Row pocket interiors set it). Empty = the scene's TruckSpawner-tagged marker.")]
    public Transform bayAnchor;

    [Header("Tuning")]
    [Tooltip("Truck must be within this many metres (flat) of the TruckSpawner.")]
    public float truckRange = 8f;
    public float bagInterval = 0.1f;
    [Tooltip("Seconds between bags with the auto-tipper (unload 10 s -> 3 s).")]
    public float autoTipperBagInterval = 0.03f;
    public Vector2 flightTime = new Vector2(0.7f, 0.8f);

    /// <summary>Direct feed (auto_tipper L2): set at runtime by the conveyor. Null = everything lands on the pile.</summary>
    [System.NonSerialized] public IBagIntake directFeed;

    // Bags leave the hopper in alternating lanes so consecutive ones don't spawn inside each other.
    static readonly float[] Lanes = { 0f, 0.45f, -0.45f, 0.22f, -0.22f };

    public bool Busy { get; private set; }

    TruckControllerV2 truck;
    Transform spawner;
    float nextLookup;
    float nextStatus;
    ulong tipClient;
    bool hasTipClient;
    List<Collider> tipTriggers;
    readonly List<(Collider[] a, Collider[] b)> ignoredBagPairs = new List<(Collider[], Collider[])>();
    int Lot => DepotRowWorld.Active ? DepotRowLayout.InteriorAt(transform.position) : -1;

    // ------------------------------------------------------------------ state

    public enum Status { Ready, NoTruck, Empty, Busy, Full }

    public Status CurrentStatus
    {
        get
        {
            if (Busy || Compactor.ShopBusy(this) || (DepotTruckCargoNet.Instance != null && DepotTruckCargoNet.Instance.Busy(Lot))) return Status.Busy;
            if (!TruckHere()) return Status.NoTruck;
            if (!DepotTruckCargo.HasCargo(truck)) return Status.Empty; // Depot Row: the parked truck's business's cargo
            if (!Businesses.IsAuthority && DepotTruckCargoNet.Instance != null)
                return DepotTruckCargoNet.Instance.Full(Lot) ? Status.Full : Status.Ready;
            var next = DepotTruckCargo.Peek(truck);
            bool full = !DepotBacklog.CanFit(Lot, next != null ? next.TotalMassKg : 0.01f);
            DepotTruckCargoNet.Instance?.SetFull(Lot, full);
            if (full) return Status.Full;
            return Status.Ready;
        }
    }

    public bool IsReady => CurrentStatus == Status.Ready;

    /// <summary>The truck parked in the bay right now (null when it isn't here). Cached a few times a second.</summary>
    public TruckControllerV2 ParkedTruck => TruckHere() ? truck : null;

    /// <summary>Flat speed (m/s) of the parked truck, or +inf when there's none.</summary>
    public float ParkedTruckSpeed
    {
        get
        {
            var t = ParkedTruck;
            if (t == null) return float.PositiveInfinity;
            var rb = t.GetComponent<Rigidbody>();
            if (rb == null) return 0f;
            Vector3 v = rb.linearVelocity; v.y = 0f;
            return v.magnitude;
        }
    }

    /// <summary>The pull was started by the auto-tipper (no hand on the lever).</summary>
    public bool LastPullWasAuto { get; private set; }

    /// <summary>The auto-tipper pulls the lever by itself (same as OnInteract(null), but says so).</summary>
    public bool AutoPull()
    {
        if (CurrentStatus != Status.Ready) return false;
        LastPullWasAuto = true;
        hasTipClient = false;
        GameHUD.FloatingText(transform.position + Vector3.up * 2.3f, "it pulled itself...", new Color(0.8f, 0.8f, 1f), 1f);
        Sfx.Play("squeak", transform.position + Vector3.up, 0.6f, 0.6f);
        StartCoroutine(TipRoutine());
        return true;
    }

    bool TruckHere()
    {
        if (Time.time >= nextLookup)
        {
            nextLookup = Time.time + 0.4f;
            if (bayAnchor != null) spawner = bayAnchor;
            if (spawner == null)
            {
                GameObject s = null;
                try { s = GameObject.FindWithTag("TruckSpawner"); } catch (UnityException) { }
                spawner = s != null ? s.transform : null;
            }
            // Several trucks in the one world (Depot Row): the one nearest this bay.
            Vector3 at = spawner != null ? spawner.position : transform.position;
            truck = null;
            float best = float.MaxValue;
            var all = TruckControllerV2.All;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (Lot >= 0 && DepotRowNet.Instance != null && t.IsSpawned && DepotRowNet.Instance.LotOfTruck(t.NetworkObjectId) != Lot) continue;
                Vector3 off = t.transform.position - at; off.y = 0f;
                float m = off.sqrMagnitude;
                if (m < best) { best = m; truck = t; }
            }
        }
        if (truck == null) return false;
        Vector3 anchor = spawner != null ? spawner.position : transform.position;
        float range = spawner != null ? truckRange : truckRange + 6f;
        Vector3 d = truck.transform.position - anchor;
        d.y = 0f;
        return d.magnitude <= range;
    }

    // ------------------------------------------------------------------ IInteractable

    public bool CanInteract() => !Busy && !SortingKit.LocalPlayerDriving;

    public string GetInteractionPrompt()
    {
        switch (CurrentStatus)
        {
            case Status.Busy: return "Tipping...";
            case Status.NoTruck: return "Park this shop's truck in the bay";
            case Status.Empty: return "Truck's empty";
            case Status.Full: return "Depot full — sort or compact first";
            default:
                if (!DepotTruckCargo.IsLocal(truck)) return $"TIP IT! ({DepotTruckCargo.LoadKg(truck):0} kg)";
                int n = GameSession.TruckBags.Count;
                return $"TIP IT! ({n} {SortingKit.Plural(n, "bag", "bags")} · {GameSession.TruckLoadKg:0} kg)";
        }
    }

    public void OnInteract(GameObject interactor)
    {
        var st = CurrentStatus;
        if (st == Status.Busy) return;
        if (interactor != null && !Permissions.LocalCanWorkAt(transform.position)) { Permissions.Deny(transform.position); return; }
        if (DepotRowWorld.Active && !Businesses.IsAuthority)
        {
            DepotTruckCargoNet.Instance?.RequestTip(truck, Lot);
            return;
        }
        if (st != Status.Ready)
        {
            GameHUD.Toast(st == Status.Full ? DepotBacklog.FullMessage : st == Status.NoTruck ? "Truck's not here. Park it in the bay!" : "Truck's empty. Go get some trash!", ToastStyle.Info);
            Sfx.Play("squeak", transform.position, 0.8f, 0.7f);
            StartCoroutine(Jiggle());
            return;
        }
        LastPullWasAuto = false;
        hasTipClient = false;
        StartCoroutine(TipRoutine());
    }

    public void BeginAuthorizedTip(ulong client)
    {
        if (!Businesses.IsAuthority || CurrentStatus == Status.Busy) return;
        if (CurrentStatus != Status.Ready)
        {
            DepotTruckCargoNet.Instance?.Notify(client, CurrentStatus == Status.Full ? DepotBacklog.FullMessage : GetInteractionPrompt());
            return;
        }
        tipClient = client;
        hasTipClient = true;
        LastPullWasAuto = false;
        StartCoroutine(TipRoutine());
    }

    void TipNotice(string message)
    {
        if (hasTipClient && DepotTruckCargoNet.Instance != null) DepotTruckCargoNet.Instance.Notify(tipClient, message);
        else if (!DepotRowWorld.Active || (PlayerController.Local != null && DepotRowLayout.InteriorAt(PlayerController.Local.transform.position) == Lot)) // coop:allow(local) local-only tip feedback
            GameHUD.Toast(message, ToastStyle.Info);
    }

    void OnDisable()
    {
        StopAllCoroutines();
        foreach (var pair in ignoredBagPairs) IgnorePairs(pair.a, pair.b, false);
        ignoredBagPairs.Clear();
        RestoreTriggers(tipTriggers);
        tipTriggers = null;
        SetLever(leverRestAngle);
        Busy = false;
        DepotTruckCargoNet.Instance?.SetBusy(Lot, false);
    }

    public void Highlight(bool highlight) { }

    void Start()
    {
        SetLever(leverRestAngle);
    }

    void Update()
    {
        if (statusText == null || Time.time < nextStatus) return;
        nextStatus = Time.time + 0.3f;
        switch (CurrentStatus)
        {
            case Status.Busy: statusText.text = "TIPPING!"; break;
            case Status.NoTruck: statusText.text = "NO TRUCK"; break;
            case Status.Empty: statusText.text = "EMPTY"; break;
            case Status.Full: statusText.text = "DEPOT FULL"; break;
            default: statusText.text = DepotTruckCargo.IsLocal(truck) ? $"{GameSession.TruckBags.Count} BAGS" : $"{DepotTruckCargo.LoadKg(truck):0} KG"; break;
        }
    }

    // ------------------------------------------------------------------ the cascade

    IEnumerator TipRoutine()
    {
        Busy = true;
        DepotTruckCargoNet.Instance?.SetBusy(Lot, true);
        try
        {
            var t = truck;
            yield return PullLever();
            if (t == null) { Busy = false; yield break; }

            Transform hopper = FindHopper(t.transform);
            tipTriggers = DisableTriggers(hopper);

            Vector3 rear = -t.transform.forward;
            Vector3 right = Vector3.ProjectOnPlane(t.transform.right, Vector3.up).normalized;
            Vector3 mouth = hopper != null ? hopper.position : t.transform.TransformPoint(0f, 1.9f, -3.0f);
            var recent = new List<Collider[]>();
            float floorY = SortingKit.FloorY(pileCenter, transform.position.y);

            Sfx.Play("beep", mouth, 0.9f);
            yield return new WaitForSeconds(0.25f);
            Sfx.Play("beep", mouth, 0.9f);
            yield return new WaitForSeconds(0.2f);

            int bags = 0, fed = 0;
            float kg = 0f;
            int tipperLevel = DepotMachineContext.Level(this, ShopIds.AutoTipper);
            float interval = tipperLevel >= 1 ? Mathf.Min(bagInterval, autoTipperBagInterval) : bagInterval;
            bool direct = tipperLevel >= 2 && directFeed != null;
            GameObject stinkAnchor = null;
            while (DepotTruckCargo.HasCargo(t))
            {
                var next = DepotTruckCargo.Peek(t);
                if (next == null) break;
                if (!DepotBacklog.CanFit(Lot, next.TotalMassKg)) { TipNotice(DepotBacklog.FullMessage); break; }
                // No yields between removing cargo and materializing ALL its mass: another tip sees the new stock.
                var contents = DepotTruckCargo.Take(t);
                if (contents == null) break;
                float laneOffset = Lanes[bags % Lanes.Length];
                float lane01 = laneOffset / 0.45f;
                bags++;
                kg += contents.TotalMassKg;
                Vector3 start = mouth + rear * 0.75f + right * laneOffset + Vector3.up * Random.Range(-0.05f, 0.1f);

                // Tip-O-Matic Direct Feed: whole bags go straight onto the Bag Belt while it has room.
                if (direct && contents.sackVariant != -1 && directFeed.TryTakeFromTruck(contents, start))
                {
                    fed++;
                    Sfx.Play("whoosh", start, 0.45f, 1.1f + (fed % 4) * 0.1f);
                    yield return new WaitForSeconds(interval);
                    continue;
                }

                if (contents.sackVariant == -1)
                {
                    // Loose cargo: individual items
                    foreach (var item in contents.items)
                    {
                        if (item == null) continue;
                        Vector3 target = RandomPilePoint(floorY + 0.1f);
                        float ft = Random.Range(flightTime.x, flightTime.y);
                        var li = LooseItem.Spawn(item, start + Random.insideUnitSphere * 0.15f, Random.rotation, SortingKit.Ballistic(start, target, ft), false);
                        li.Body.angularVelocity = Random.insideUnitSphere * 10f;
                        Sfx.Play("pop", start, 0.4f, Random.Range(0.9f, 1.4f));
                    }
                }
                else
                {
                    // Land in the matching lane of the pile (TrashBag pivot = bottom of the bag).
                    Vector3 target = ClampToPile(RandomPilePoint(floorY + 0.03f) + right * (lane01 * 0.4f * pileSize.y), floorY + 0.03f);
                    float ft = Random.Range(flightTime.x, flightTime.y);
                    var bag = TrashBag.Spawn(contents, start, Quaternion.Euler(Random.Range(-30f, 30f), Random.Range(0f, 360f), Random.Range(-30f, 30f)), SortingKit.Ballistic(start, target, ft));
                    if (bag != null)
                    {
                        if (bag.Body != null) bag.Body.angularVelocity = Random.insideUnitSphere * 5f;
                        var cols = bag.GetComponentsInChildren<Collider>();
                        for (int k = Mathf.Max(0, recent.Count - 4); k < recent.Count; k++) IgnorePairs(cols, recent[k], true);
                        StartCoroutine(RestorePairs(cols, new List<Collider[]>(recent.GetRange(Mathf.Max(0, recent.Count - 4), Mathf.Min(4, recent.Count))), 0.5f));
                        recent.Add(cols);
                    }
                    StartCoroutine(LandingPuff(target, ft));
                }
                Sfx.Play("whoosh", start, 0.45f, Random.Range(0.9f, 1.3f));
                if (bags % 3 == 1) Sfx.Play("rustle", start, 0.6f);
                yield return new WaitForSeconds(interval);
            }

            if (fed > 0)
                TipNotice(fed == bags ? $"Direct Feed: all {fed} {SortingKit.Plural(fed, "bag", "bags")} went straight onto the belt!"
                    : $"Direct Feed: {fed} onto the belt, {bags - fed} on the pile.");
            if (bags > fed)
            {
                stinkAnchor = GameObject.Find("TippingFloorStink");
                if (stinkAnchor == null)
                {
                    stinkAnchor = new GameObject("TippingFloorStink");
                    stinkAnchor.transform.position = new Vector3(pileCenter.x, floorY + 0.8f, pileCenter.z);
                    stinkAnchor.AddComponent<PileStink>().Init(pileCenter, new Vector3(pileSize.x + 1.6f, 3f, pileSize.y + 1.2f));
                    Fx.Stink(stinkAnchor.transform);
                }
                Sfx.Play("horn", mouth, 0.5f, 1.3f);
                int piled = bags - fed;
                if (fed == 0) TipNotice($"Dumped {piled} {SortingKit.Plural(piled, "bag", "bags")} ({kg:0} kg)! Rip 'em open on the sorting table.");
            }

            yield return new WaitForSeconds(0.6f);
            RestoreTriggers(tipTriggers);
            tipTriggers = null;
            yield return ReturnLever();
        }
        finally
        {
            RestoreTriggers(tipTriggers);
            tipTriggers = null;
            Busy = false;
            DepotTruckCargoNet.Instance?.SetBusy(Lot, false);
        }
    }

    Vector3 RandomPilePoint(float y)
    {
        return new Vector3(
            pileCenter.x + Random.Range(-0.5f, 0.5f) * pileSize.x,
            y,
            pileCenter.z + Random.Range(-0.5f, 0.5f) * pileSize.y);
    }

    Vector3 ClampToPile(Vector3 p, float y)
    {
        float hx = pileSize.x * 0.5f, hz = pileSize.y * 0.5f;
        return new Vector3(Mathf.Clamp(p.x, pileCenter.x - hx, pileCenter.x + hx), y, Mathf.Clamp(p.z, pileCenter.z - hz, pileCenter.z + hz));
    }

    static void IgnorePairs(Collider[] a, Collider[] b, bool ignore)
    {
        foreach (var x in a)
            foreach (var y in b)
                if (x != null && y != null && !x.isTrigger && !y.isTrigger) Physics.IgnoreCollision(x, y, ignore);
    }

    IEnumerator RestorePairs(Collider[] a, List<Collider[]> others, float delay)
    {
        foreach (var b in others) ignoredBagPairs.Add((a, b));
        yield return new WaitForSeconds(delay);
        foreach (var o in others) { IgnorePairs(a, o, false); ignoredBagPairs.Remove((a, o)); }
    }

    IEnumerator LandingPuff(Vector3 at, float delay)
    {
        yield return new WaitForSeconds(delay);
        Fx.Dust(at + Vector3.down * 0.25f, 0.8f);
        Sfx.Play("bag_land", at, 0.5f, Random.Range(0.85f, 1.1f));
    }

    static Transform FindHopper(Transform truck)
    {
        var h = truck.Find("Hopper");
        if (h != null) return h;
        foreach (var t in truck.GetComponentsInChildren<Transform>(true))
            if (t.name == "Hopper") return t;
        return null;
    }

    // The hopper's own receiver trigger would happily swallow bags we launch out of it; pause it.
    static List<Collider> DisableTriggers(Transform hopper)
    {
        var list = new List<Collider>();
        if (hopper == null) return list;
        foreach (var c in hopper.GetComponentsInChildren<Collider>())
            if (c.isTrigger && c.enabled) { c.enabled = false; list.Add(c); }
        return list;
    }

    static void RestoreTriggers(List<Collider> list)
    {
        if (list == null) return;
        foreach (var c in list) if (c != null) c.enabled = true;
    }

    // ------------------------------------------------------------------ lever anim

    void SetLever(float angle)
    {
        if (lever != null) lever.localRotation = Quaternion.Euler(angle, 0f, 0f);
    }

    IEnumerator PullLever()
    {
        Sfx.Play("click", transform.position, 1f, 0.9f);
        const float dur = 0.18f;
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            SetLever(Mathf.LerpUnclamped(leverRestAngle, leverPulledAngle, SortingKit.EaseOutBack(e / dur)));
            yield return null;
        }
        SetLever(leverPulledAngle);
        Sfx.Play("slam", transform.position, 0.7f, 1.2f);
        Juice.Shake(0.12f, 0.15f);
        if (lever != null) Juice.Punch(lever, 0.15f, 0.2f);
    }

    IEnumerator ReturnLever()
    {
        const float dur = 0.5f;
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            SetLever(Mathf.LerpUnclamped(leverPulledAngle, leverRestAngle, SortingKit.EaseOutBack(e / dur)));
            yield return null;
        }
        SetLever(leverRestAngle);
        Sfx.Play("click", transform.position, 0.6f, 1.3f);
    }

    IEnumerator Jiggle()
    {
        for (int i = 0; i < 4; i++)
        {
            SetLever(leverRestAngle + (i % 2 == 0 ? 12f : -6f));
            yield return new WaitForSeconds(0.05f);
        }
        SetLever(leverRestAngle);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.5f);
        Gizmos.DrawWireCube(pileCenter + Vector3.up * 0.3f, new Vector3(pileSize.x, 0.6f, pileSize.y));
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TIP IT, on the truck (Docs/Progression.md §6.1). The driver HOLDS the tip key (G / pad B, <see cref="DumpRules.HoldSeconds"/>)
/// and the hopper empties behind the truck wherever it stands. Inside a shop the driver may work in (and whose truck it
/// is), the bags land on that shop's <see cref="TippingFloor"/>, capped by <see cref="DepotBacklog"/> like the old lever.
/// Outdoors they land on the ground behind the truck as ordinary resting bags: LITTER until someone collects them
/// (<see cref="StreetLitter"/>); they have no curb record, so putting them back in a truck pays nothing.
/// The whole load takes <see cref="DumpRules.Seconds"/> (10 s; 3 s with the truck's business's Auto-Tipper). Driving off
/// mid-dump stops it and the rest stays in the truck.
/// Host decides (<see cref="HostBegin"/>): a guest driver asks through <see cref="DepotTruckCargoNet"/>. Cargo accounting is
/// <see cref="DepotTruckCargo"/>'s (the truck's business's cargo; GameSession.TruckBags for the host's own truck); pay
/// already happened at pickup. Added by <see cref="ShopEffectsRunner"/> to every truck.
/// </summary>
[DisallowMultipleComponent]
public class TruckTipper : MonoBehaviour
{
    public const KeyCode Key = KeyCode.G;
    /// <summary>The key cap for prompts on this machine.</summary>
    public static string KeyLabel => GameControls.UsingGamepad ? "B" : Keybinds.Cap(Key);

    // Bags leave the hopper in alternating lanes so consecutive ones don't spawn inside each other.
    static readonly float[] Lanes = { 0f, 0.45f, -0.45f, 0.22f, -0.22f };
    static readonly Vector2 FlightTime = new Vector2(0.7f, 0.8f);

    // Where each bag lands is cosmetic scatter, but it is decided from the bag's id (no RNG): the same load tips the
    // same way every time, and nothing here can drift between a host's run and a re-run.
    static float Roll(string id, string salt) => GarageRoll.Value((id ?? "bag") + "/tip/" + salt);
    static float Roll(string id, string salt, float min, float max) => Mathf.Lerp(min, max, Roll(id, salt));
    static Vector3 RollSphere(string id, string salt) =>
        new Vector3(Roll(id, salt + "x") - 0.5f, Roll(id, salt + "y") - 0.5f, Roll(id, salt + "z") - 0.5f) * 2f;

    /// <summary>A dump is running (the host's truth; guests hear it through DepotTruckCargoNet).</summary>
    public bool Dumping { get; private set; }
    /// <summary>Bags tipped by this truck this session (tests / stats).</summary>
    public int BagsTipped { get; private set; }

    TruckControllerV2 truck;
    Rigidbody body;
    ShopTruckMods mods;
    float holdFor;
    bool latched;   // the hold fired: let go before the next one
    bool prompted;
    float dumpEndsAt;
    TippingFloor activeFloor;   // the floor the running dump lands on (null outdoors)
    List<Collider> pausedTriggers;
    readonly List<(Collider[] a, Collider[] b)> ignoredBagPairs = new List<(Collider[], Collider[])>();

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
    }

    void OnDisable()
    {
        StopAllCoroutines();
        foreach (var pair in ignoredBagPairs) IgnorePairs(pair.a, pair.b, false);
        ignoredBagPairs.Clear();
        RestoreTriggers();
        if (Dumping) EndDump(activeFloor);
        ClearPrompt();
    }

    float FlatSpeed
    {
        get
        {
            if (body == null) return 0f;
            Vector3 v = body.linearVelocity; v.y = 0f;
            return v.magnitude;
        }
    }

    Transform Hopper
    {
        get
        {
            var h = transform.Find("Hopper");
            if (h != null) return h;
            foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Hopper") return t;
            return null;
        }
    }

    Vector3 Mouth
    {
        get
        {
            var h = Hopper;
            return h != null ? h.position : ShopModKit.TruckPoint(transform, new Vector3(0f, 1.9f, -3.0f));
        }
    }

    Vector3 Rear => -Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

    /// <summary>Where the pile lands outdoors: behind the hopper mouth, on the ground (if there is any).</summary>
    public bool OutdoorPile(out Vector3 center)
    {
        Vector3 mouth = Mouth;
        Vector3 at = mouth + Rear * DumpRules.PileBehind;
        if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out var hit, DumpRules.MaxGroundDrop + 1.5f + Mathf.Max(0f, mouth.y - at.y),
                            1 << SortingKit.GroundLayer, QueryTriggerInteraction.Ignore))
        {
            center = hit.point;
            return true;
        }
        center = at;
        return false;
    }

    // =====================================================================
    // The driver (this machine's seated player)
    // =====================================================================

    void Update()
    {
        if (truck == null || !truck.IsLocalPlayerSeated)
        {
            holdFor = 0f;
            ClearPrompt();
            return;
        }
        if (mods == null) mods = GetComponent<ShopTruckMods>();
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning) { holdFor = 0f; return; }

        if (Dumping || Time.time < dumpEndsAt)
        {
            holdFor = 0f;
            SetPrompt("TIPPING... hold still");
            return;
        }
        if (GameControls.Held(Key, GameControls.Action.TipIt))
        {
            if (latched) return;
            if (holdFor <= 0f) Sfx.Play("click", transform.position, 0.6f, 1.1f);
            holdFor += Time.deltaTime;
            SetPrompt($"TIP IT... {Bar(holdFor / DumpRules.HoldSeconds)}");
            if (holdFor >= DumpRules.HoldSeconds)
            {
                holdFor = 0f;
                latched = true;
                ClearPrompt();
                RequestDump();
            }
        }
        else
        {
            if (holdFor > 0f) ClearPrompt();
            holdFor = 0f;
            latched = false;
        }
    }

    static string Bar(float k)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(k * 10f), 0, 10);
        return "[" + new string('|', n) + new string('.', 10 - n) + "]";
    }

    void SetPrompt(string text)
    {
        prompted = true;
        if (mods != null) mods.HintOverride = text; else GameHUD.SetContextHint(text);
    }

    void ClearPrompt()
    {
        if (!prompted) return;
        prompted = false;
        if (mods != null) { mods.HintOverride = null; mods.RefreshHint(); }
    }

    /// <summary>The chip for the driving hint line ("hold G: TIP IT (12 bags)"), or null with an empty hopper.</summary>
    public string DriveHintChip()
    {
        if (truck == null || !DepotTruckCargo.HasCargo(truck)) return null;
        string load = DepotTruckCargo.IsLocal(truck)
            ? $"{GameSession.TruckBags.Count} {SortingKit.Plural(GameSession.TruckBags.Count, "bag", "bags")}"
            : $"{DepotTruckCargo.LoadKg(truck):0} kg";
        return $"hold {KeyLabel}: TIP IT ({load})";
    }

    /// <summary>The seated local driver asks to tip (the hold finished; tests and bots call it directly).</summary>
    public void RequestDump()
    {
        if (truck == null) return;
        if (Businesses.IsAuthority) { HostBegin(Permissions.LocalClientId); return; }
        // A guest: cheap local refusals first (no round trip for an empty hopper), then the host decides.
        if (!DepotTruckCargo.HasCargo(truck)) { Refuse(DumpRules.Verdict.Empty); return; }
        if (FlatSpeed > DumpRules.MaxSpeed) { Refuse(DumpRules.Verdict.Moving); return; }
        var net = DepotTruckCargoNet.Instance;
        if (net != null) net.RequestDump(truck);
    }

    static void Refuse(DumpRules.Verdict v)
    {
        string m = DumpRules.Message(v);
        if (m == null) return;
        GameHUD.Toast(m, v == DumpRules.Verdict.Empty ? ToastStyle.Info : ToastStyle.Bad, 2.2f);
        Sfx.Play("buzz", null, 0.5f, 1.1f);
    }

    // =====================================================================
    // The host
    // =====================================================================

    /// <summary>Where this truck stands, for <paramref name="client"/>: outdoors, a bay they may tip in, or someone else's.</summary>
    public DumpRules.Where Locate(ulong client, out TippingFloor floor)
    {
        floor = TippingFloor.At(transform.position, out bool inside);
        if (!inside) return DumpRules.Where.Outdoors;
        if (floor == null) return DumpRules.Where.ForeignBay;   // a shop interior with no floor up on this machine
        int lot = floor.Lot;
        if (lot < 0) return DumpRules.Where.OwnBay;             // the stand-alone garage: it is this save's
        var net = DepotRowNet.Instance;
        bool truckOfThisShop = net == null || !truck.IsSpawned || net.LotOfTruck(truck.NetworkObjectId) == lot;
        return truckOfThisShop && Permissions.CanWorkIn(client, lot) ? DumpRules.Where.OwnBay : DumpRules.Where.ForeignBay;
    }

    /// <summary>The host's verdict for <paramref name="client"/> tipping this truck right now.</summary>
    public DumpRules.Verdict Evaluate(ulong client, out TippingFloor floor)
    {
        floor = null;
        if (truck == null) return DumpRules.Verdict.NotDriver;
        bool driver = truck.IsSpawned ? truck.HasDriver && truck.DriverClientId == client && Permissions.CanDrive(client, truck)
                                      : truck.IsLocalPlayerSeated;
        var where = Locate(client, out floor);
        bool busy = Dumping || (floor != null && (floor.Busy || Compactor.ShopBusy(floor)));
        bool ground = OutdoorPile(out Vector3 pile);
        var next = DepotTruckCargo.Peek(truck);
        bool room = floor == null || DepotBacklog.CanFit(floor.Lot, next != null ? next.TotalMassKg : 0.01f);
        return DumpRules.Check(driver, busy, DepotTruckCargo.HasCargo(truck), FlatSpeed, where, ground, pile.y, room);
    }

    /// <summary>Host / solo: <paramref name="client"/> (the driver) wants to tip. Starts the dump or tells them why not.</summary>
    public bool HostBegin(ulong client)
    {
        if (!Businesses.IsAuthority) return false;
        var verdict = Evaluate(client, out var floor);
        if (verdict != DumpRules.Verdict.Ok)
        {
            Notice(client, DumpRules.Message(verdict), true);
            return false;
        }
        StartCoroutine(DumpRoutine(client, floor));
        return true;
    }

    void Notice(ulong client, string message, bool bad = false)
    {
        if (string.IsNullOrEmpty(message)) return;
        var net = DepotTruckCargoNet.Instance;
        if (net != null && net.IsSpawned && client != Permissions.LocalClientId) { net.Notify(client, message); return; }
        GameHUD.Toast(message, bad ? ToastStyle.Bad : ToastStyle.Info, 2.5f);
        if (bad) Sfx.Play("buzz", transform.position, 0.5f, 1.1f);
    }

    /// <summary>Every machine: the truck started / stopped tipping (beeps, the driver's "hold still" prompt).</summary>
    public void PresentDump(bool on, float seconds)
    {
        if (on)
        {
            dumpEndsAt = Time.time + Mathf.Max(0.5f, seconds);
            Vector3 mouth = Mouth;
            Sfx.Play("beep", mouth, 0.9f);
            Sfx.Play("slam", mouth, 0.5f, 1.2f);
            Fx.Dust(mouth + Rear * 0.8f, 0.8f);
        }
        else dumpEndsAt = 0f;
    }

    void BeginDump(TippingFloor floor, float seconds)
    {
        Dumping = true;
        activeFloor = floor;
        int lot = floor != null ? floor.Lot : -1;
        if (floor != null) floor.Busy = true;
        var net = DepotTruckCargoNet.Instance;
        if (net != null && net.IsSpawned) { net.SetBusy(lot, true); net.BroadcastDump(truck, true, seconds); }
        else PresentDump(true, seconds);
    }

    void EndDump(TippingFloor floor)
    {
        Dumping = false;
        activeFloor = null;
        if (floor != null) floor.Busy = false;
        var net = DepotTruckCargoNet.Instance;
        if (net != null && net.IsSpawned)
        {
            if (floor != null) net.SetBusy(floor.Lot, false);
            net.BroadcastDump(truck, false, 0f);
        }
        else PresentDump(false, 0f);
    }

    IEnumerator DumpRoutine(ulong client, TippingFloor floor)
    {
        int level = BusinessUpgradeNet.LevelForTruck(truck, ShopIds.AutoTipper);
        int count = DepotTruckCargo.Count(truck);
        float interval = DumpRules.BagInterval(level, count);
        BeginDump(floor, DumpRules.Seconds(level));
        int lot = floor != null ? floor.Lot : -1;
        try
        {
            pausedTriggers = DisableTriggers(Hopper);
            Vector3 mouth = Mouth;
            yield return new WaitForSeconds(DumpRules.LeadSeconds * 0.5f);
            Sfx.Play("beep", mouth, 0.9f);
            yield return new WaitForSeconds(DumpRules.LeadSeconds * 0.5f);

            var recent = new List<Collider[]>();
            int bags = 0;
            float kg = 0f;
            bool outdoors = floor == null;
            string stopped = null;
            while (DepotTruckCargo.HasCargo(truck))
            {
                if (FlatSpeed > DumpRules.MaxSpeed * 1.5f) { stopped = "Tipping stopped: you drove off. The rest stays in the truck."; break; }
                var next = DepotTruckCargo.Peek(truck);
                if (next == null) break;
                Vector3 pile = default;
                if (outdoors)
                {
                    // The truck may have crept: the pile follows the hopper, and still needs dry ground.
                    if (!OutdoorPile(out pile) || pile.y < DumpRules.WaterlineY) { stopped = "Tipping stopped: no solid ground behind the truck."; break; }
                }
                else if (!DepotBacklog.CanFit(lot, next.TotalMassKg)) { stopped = DepotBacklog.FullMessage; break; }

                // No yields between removing cargo and materializing ALL its mass: another tip sees the new stock.
                var contents = DepotTruckCargo.Take(truck);
                if (contents == null) break;
                mouth = Mouth;
                Vector3 rear = Rear;
                Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
                float laneOffset = Lanes[bags % Lanes.Length];
                float lane01 = laneOffset / 0.45f;
                bags++;
                kg += contents.TotalMassKg;
                string id = contents.bagId ?? ("load" + bags);
                Vector3 start = mouth + rear * 0.75f + right * laneOffset + Vector3.up * Roll(id, "up", -0.05f, 0.1f);

                if (contents.sackVariant == -1)
                {
                    // Loose cargo: individual items.
                    foreach (var item in contents.items)
                    {
                        if (item == null) continue;
                        string key = id + "/" + (item.uid ?? item.defId);
                        Vector3 target = outdoors ? OutdoorPoint(pile, 0f, 0.1f, key) : floor.PilePoint(floor.FloorY + 0.1f, key);
                        float ft = Roll(key, "ft", FlightTime.x, FlightTime.y);
                        var li = LooseItem.Spawn(item, start + RollSphere(key, "at") * 0.15f, Quaternion.Euler(RollSphere(key, "rot") * 180f), SortingKit.Ballistic(start, target, ft), false);
                        if (li != null && li.Body != null) li.Body.angularVelocity = RollSphere(key, "spin") * 10f;
                        Sfx.Play("pop", start, 0.4f, Roll(key, "pitch", 0.9f, 1.4f));
                    }
                }
                else
                {
                    // Land in the matching lane of the pile (TrashBag pivot = bottom of the bag).
                    Vector3 target;
                    if (outdoors) target = OutdoorPoint(pile, lane01, 0.03f, id);
                    else
                    {
                        float floorY = floor.FloorY;
                        target = floor.ClampToPile(floor.PilePoint(floorY + 0.03f, id) + right * (lane01 * 0.4f * floor.pileSize.y), floorY + 0.03f);
                    }
                    float ft = Roll(id, "ft", FlightTime.x, FlightTime.y);
                    var bag = TrashBag.Spawn(contents, start, Quaternion.Euler(Roll(id, "rx", -30f, 30f), Roll(id, "ry", 0f, 360f), Roll(id, "rz", -30f, 30f)), SortingKit.Ballistic(start, target, ft));
                    if (bag != null)
                    {
                        bag.NoBurstUntil = Time.time + 3f;   // a tipped bag lands as a bag, wherever it lands
                        if (bag.Body != null) bag.Body.angularVelocity = RollSphere(id, "spin") * 5f;
                        var cols = bag.GetComponentsInChildren<Collider>();
                        for (int k = Mathf.Max(0, recent.Count - 4); k < recent.Count; k++) IgnorePairs(cols, recent[k], true);
                        StartCoroutine(RestorePairs(cols, new List<Collider[]>(recent.GetRange(Mathf.Max(0, recent.Count - 4), Mathf.Min(4, recent.Count))), 0.5f));
                        recent.Add(cols);
                        BagsTipped++;
                    }
                    StartCoroutine(LandingPuff(target, ft));
                }
                Sfx.Play("whoosh", start, 0.45f, Roll(id, "whoosh", 0.9f, 1.3f));
                if (bags % 3 == 1) Sfx.Play("rustle", start, 0.6f);
                yield return new WaitForSeconds(interval);
            }

            if (bags > 0)
            {
                if (floor != null) floor.EnsureStink();
                Sfx.Play("horn", Mouth, 0.5f, 1.3f);
                string what = $"{bags} {SortingKit.Plural(bags, "bag", "bags")} ({kg:0} kg)";
                Notice(client, outdoors
                    ? $"Tipped {what} in the street. That's litter until somebody collects it!"
                    : $"Tipped {what}! Carry a bag to the sorting table and rip it open.");
                NetAudit.Event("tip", new Dictionary<string, object>
                {
                    { "business", DepotTruckCargo.BusinessOf(truck) }, { "bags", bags }, { "kg", Mathf.RoundToInt(kg) }, { "outdoors", outdoors }, { "lot", lot },
                });
            }
            if (stopped != null) Notice(client, stopped, true);
            yield return new WaitForSeconds(0.6f);
        }
        finally
        {
            RestoreTriggers();
            EndDump(floor);
        }
    }

    /// <summary>A landing point in the outdoor pile, snapped to the ground under it.</summary>
    Vector3 OutdoorPoint(Vector3 pile, float lane01, float lift, string key)
    {
        Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
        Vector3 p = pile + right * (lane01 * 0.7f + Roll(key, "px", -0.25f, 0.25f)) + Rear * Roll(key, "pz", -0.7f, 0.9f);
        if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, DumpRules.MaxGroundDrop + 2f, 1 << SortingKit.GroundLayer, QueryTriggerInteraction.Ignore)
            && hit.point.y >= DumpRules.WaterlineY)
            p.y = hit.point.y;
        else p = pile;
        p.y += lift;
        return p;
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
        Sfx.Play("bag_land", at, 0.5f, 0.85f + 0.25f * Mathf.Repeat(at.x * 3.7f + at.z * 1.3f, 1f));
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

    void RestoreTriggers()
    {
        if (pausedTriggers == null) return;
        foreach (var c in pausedTriggers) if (c != null) c.enabled = true;
        pausedTriggers = null;
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Dumpster Dunker (Docs/Progression.md §6.11): a rear-loader attachment. The driver backs the hopper up to a shop's
/// <see cref="Dumpster"/>, stops and holds B; the host lifts it and tips its bags into the truck one by one through the
/// hopper's own collect path (<see cref="TruckHopper.CollectCurbBag"/>), so each pays, ages and counts exactly like a
/// curb bag. On every truck (it reads the truck's business's level); a guest driver asks through <see cref="DumpsterNet"/>.
/// </summary>
[DisallowMultipleComponent]
public class TruckDumpsterLift : MonoBehaviour
{
    public const KeyCode Key = KeyCode.B;
    const string VisualRoot = "Dumpster Dunker";

    TruckControllerV2 truck;
    ShopTruckMods mods;
    Rigidbody body;
    Transform visuals, armPivot;
    int shownLevel = -1;
    float holdFor, nextApply, armUp, armWant, lastRefusal;
    bool prompted, latched;

    public bool Busy { get; private set; }
    int Level => Mathf.Clamp(BusinessUpgradeNet.LevelForTruck(truck, DumpsterRules.UpgradeId), 0, DumpsterRules.MaxLevel);
    bool RearLoader => TruckProgression.ModelFor(truck) == TruckProgression.RearLoader;

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
    }

    float FlatSpeed
    {
        get
        {
            if (body == null) return 0f;
            Vector3 v = body.linearVelocity;
            v.y = 0f;
            return v.magnitude;
        }
    }

    Vector3 HopperPoint
    {
        get
        {
            var h = transform.Find("Hopper");
            return h != null ? h.position : ShopModKit.TruckPoint(transform, new Vector3(0f, 1.9f, -2.4f));
        }
    }

    /// <summary>The dumpster this truck's rear is backed up to, or null.</summary>
    public Dumpster InReach()
    {
        Dumpster best = null;
        float bestSq = float.MaxValue;
        var all = Dumpster.All;
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            if (d == null) continue;
            Vector3 to = d.HookPoint - transform.position;
            if (to.sqrMagnitude > 12f * 12f) continue;
            Vector3 local = ShopModKit.ToTruckMetres(transform, d.HookPoint);
            float yawOff = Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), d.transform.forward, Vector3.up);
            if (!DumpsterRules.InReach(local, yawOff)) continue;
            if (to.sqrMagnitude < bestSq) { bestSq = to.sqrMagnitude; best = d; }
        }
        return best;
    }

    // ------------------------------------------------------------------ the driver (this machine's seated player)

    void Update()
    {
        if (truck == null) return;
        if (Time.time >= nextApply) { nextApply = Time.time + 0.5f; ApplyVisuals(); }
        armUp = Mathf.MoveTowards(armUp, armWant, Time.deltaTime * 1.6f);
        if (armPivot != null) armPivot.localRotation = Quaternion.Euler(Mathf.Lerp(0f, -105f, armUp), 0f, 0f);

        if (!truck.IsLocalPlayerSeated) { holdFor = 0f; ClearPrompt(); return; }
        if (mods == null) mods = GetComponent<ShopTruckMods>();
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning) { holdFor = 0f; return; }
        var target = Busy ? null : InReach();
        if (Busy) { holdFor = 0f; SetPrompt("DUNKING... hold still"); return; }
        if (target == null || target.Lifted) { holdFor = 0f; ClearPrompt(); return; }

        int bags = target.Bags;
        if (!RearLoader || Level <= 0)
        {
            if (bags > 0) SetPrompt($"Dumpster ({bags} {SortingKit.Plural(bags, "bag", "bags")}): " +
                                    (!RearLoader ? "needs the rear-loader" : $"needs the Dumpster Dunker ({Scrapazon.Name} > Truck)"));
            else ClearPrompt();
            return;
        }
        if (bags <= 0) { SetPrompt("Dumpster: empty"); holdFor = 0f; return; }
        string label = $"DUNK THE DUMPSTER ({bags} {SortingKit.Plural(bags, "bag", "bags")})";
        if (GameControls.Held(Key, GameControls.Action.Chug))
        {
            if (latched) return;
            if (holdFor <= 0f) Sfx.Play("click", transform.position, 0.6f, 1.1f);
            holdFor += Time.deltaTime;
            SetPrompt($"{label}... {Bar(holdFor / DumpsterRules.HoldSeconds)}");
            if (holdFor >= DumpsterRules.HoldSeconds)
            {
                holdFor = 0f;
                latched = true;
                Request(target);
            }
        }
        else
        {
            holdFor = 0f;
            latched = false;
            SetPrompt($"Hold {Keybinds.Cap(Key)}: {label}");
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

    /// <summary>The seated local driver asks to tip <paramref name="target"/> (the hold finished; probes call it directly).</summary>
    public void Request(Dumpster target)
    {
        if (truck == null || target == null) return;
        if (Businesses.IsAuthority) { HostBegin(Permissions.LocalClientId, target); return; }
        var net = DumpsterNet.Instance;
        if (net != null) net.RequestLift(truck, target.StopId);
    }

    // ------------------------------------------------------------------ the host decides and does it

    static readonly List<CurbBag> records = new List<CurbBag>();

    public DumpsterRules.Verdict Evaluate(ulong client, Dumpster target, out string business)
    {
        business = DepotTruckCargo.BusinessOf(truck);
        bool driver = truck.IsSpawned ? truck.HasDriver && truck.DriverClientId == client && Permissions.CanDrive(client, truck)
                                      : truck.IsLocalPlayerSeated;
        bool reach = target != null && InReach() == target && !target.Lifted;
        int bags = 0;
        bool fits = true;
        if (target != null)
        {
            target.Records(records);
            bags = records.Count;
            if (bags > 0 && records[0].contents != null && Businesses.TryGetTruckLoad(business, out float kg, out float cap))
                fits = kg + records[0].contents.TotalMassKg <= cap + 0.001f;
        }
        var district = target != null && target.Stop != null ? target.Stop.District : null;
        bool own = string.IsNullOrEmpty(business) || business == Businesses.LocalId;
        // A district won by contract is open to the whole Town (DistrictAccess), so no business needs its own permit there.
        bool permit = district == null || district.tier <= 1 || (Campaign.IsGrantedByContract(district.id) && DistrictAccess.OpenInTown(district)) ||
                      (own ? Districts.IsUnlocked(district) : BusinessUpgradeNet.HasPermitForBusiness(business, district));
        return DumpsterRules.Check(driver, Level, RearLoader, Busy, FlatSpeed, reach, bags, permit, fits);
    }

    /// <summary>Host / solo: <paramref name="client"/> (the driver) wants to tip <paramref name="target"/>.</summary>
    public bool HostBegin(ulong client, Dumpster target)
    {
        if (!Businesses.IsAuthority) return false;
        var verdict = Evaluate(client, target, out string business);
        if (verdict != DumpsterRules.Verdict.Ok)
        {
            if (verdict != DumpsterRules.Verdict.NotDriver && Time.time - lastRefusal > 0.5f)
            {
                lastRefusal = Time.time;
                Notice(client, DumpsterRules.Message(verdict), true);
            }
            return false;
        }
        StartCoroutine(LiftRoutine(client, target, business));
        return true;
    }

    IEnumerator LiftRoutine(ulong client, Dumpster target, string business)
    {
        Busy = true;
        int level = Level;
        float perBag = DumpsterRules.SecondsPerBag(level);
        target.Records(records);
        var todo = new List<CurbBag>(records);
        Vector3 hopper = HopperPoint;
        var net = DumpsterNet.Instance;
        if (net != null && net.IsSpawned) net.BroadcastLift(truck, target.StopId, todo.Count, perBag);
        else Present(target, todo.Count, perBag);
        yield return new WaitForSeconds(DumpsterRules.LiftSeconds);

        int tipped = 0;
        float paid = 0f;
        bool full = false;
        bool actorFeedback = !NetSession.InSession || client == Permissions.LocalClientId;
        foreach (var rec in todo)
        {
            if (FlatSpeed > DumpsterRules.MaxSpeed * 2.5f) break; // drove off with it in the air: the rest stays in the dumpster
            var live = GameSession.FindCurbBag(rec.bagId);
            if (live == null || live.contents == null) continue;
            if (Businesses.TryGetTruckLoad(business, out float kg, out float cap) && kg + live.contents.TotalMassKg > cap + 0.001f) { full = true; break; }
            paid += TruckHopper.CollectCurbBag(live.contents, business, hopper, true, live.stopId, actorFeedback, out var feedback);
            if (WorldState.Instance != null) WorldState.Instance.BroadcastCollectionFx(hopper, feedback, client);
            tipped++;
            yield return new WaitForSeconds(perBag);
        }
        yield return new WaitForSeconds(0.25f + DumpsterRules.LowerSeconds);
        Busy = false;
        if (tipped > 0)
            Notice(client, $"Dumpster dunked: {tipped} {SortingKit.Plural(tipped, "bag", "bags")}, +{Economy.Money(paid)}." + (full ? " Truck's full, the rest stayed in." : ""), false);
        else if (full) Notice(client, DumpsterRules.Message(DumpsterRules.Verdict.Full), true);
        NetAudit.Event("dumpster", new Dictionary<string, object> { { "stop", target.StopId }, { "bags", tipped }, { "pay", GameSession.ToCents(paid) } });
    }

    void Notice(ulong client, string message, bool bad)
    {
        if (string.IsNullOrEmpty(message)) return;
        var net = DepotTruckCargoNet.Instance;
        if (net != null && net.IsSpawned && client != Permissions.LocalClientId) { net.Notify(client, message); return; }
        GameHUD.Toast(message, bad ? ToastStyle.Bad : ToastStyle.Good, 3f);
        if (bad) Sfx.Play("buzz", transform.position, 0.5f, 1.1f);
    }

    /// <summary>Every machine: this truck's arms go up with <paramref name="target"/> on them and come back down.</summary>
    public void Present(Dumpster target, int bags, float secondsPerBag)
    {
        if (target == null) return;
        target.PresentLift(transform, HopperPoint, bags, secondsPerBag);
        StartCoroutine(Arms(DumpsterRules.LiftSeconds + bags * Mathf.Max(0.05f, secondsPerBag) + 0.25f));
    }

    IEnumerator Arms(float holdSeconds)
    {
        armWant = 1f;
        Sfx.Play("engine_start", transform.position, 0.5f, 0.7f);
        yield return new WaitForSeconds(holdSeconds);
        armWant = 0f;
    }

    // ------------------------------------------------------------------ the arms on the truck

    void ApplyVisuals()
    {
        int want = RearLoader ? Level : 0;
        if (want == shownLevel && (want == 0 || visuals != null)) return;
        bool fresh = shownLevel >= 0 && want > shownLevel;
        shownLevel = want;
        if (visuals != null) Destroy(visuals.gameObject);
        visuals = null;
        armPivot = null;
        if (want <= 0) return;

        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var chrome = ShopModKit.Lit("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
        var hazard = ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);
        var black = ShopModKit.Lit("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        visuals = ShopModKit.MetreRoot(transform, VisualRoot);
        // Two arms hinged on the tailgate posts, a hook bar across their ends, and a ram behind each (two each at level 2)
        armPivot = ShopModKit.Pivot("Arm pivot", visuals, new Vector3(0f, 1.25f, ShopModKit.BodyRearZ - 0.05f));
        for (int s = -1; s <= 1; s += 2)
        {
            ShopModKit.Prim(PrimitiveType.Cube, "Hinge block", visuals, black, new Vector3(s * 1.12f, 1.25f, ShopModKit.BodyRearZ + 0.05f), new Vector3(0.2f, 0.34f, 0.3f));
            ShopModKit.Prim(PrimitiveType.Cube, "Arm", armPivot, hazard, new Vector3(s * 1.12f, -0.38f, -0.42f), new Vector3(0.12f, 0.14f, 1.25f), new Vector3(-42f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Cube, "Hook", armPivot, steel, new Vector3(s * 1.12f, -0.72f, -0.92f), new Vector3(0.14f, 0.3f, 0.16f));
            for (int r = 0; r < want; r++)
            {
                float x = s * (1.12f - r * 0.2f);
                ShopModKit.Prim(PrimitiveType.Cylinder, "Ram", visuals, chrome, new Vector3(x, 1.75f, ShopModKit.BodyRearZ + 0.25f), new Vector3(0.09f, 0.42f, 0.09f), new Vector3(18f, 0f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Ram sleeve", visuals, steel, new Vector3(x, 2.1f, ShopModKit.BodyRearZ + 0.36f), new Vector3(0.13f, 0.24f, 0.13f), new Vector3(18f, 0f, 0f));
            }
        }
        ShopModKit.Prim(PrimitiveType.Cylinder, "Hook bar", armPivot, chrome, new Vector3(0f, -0.82f, -0.92f), new Vector3(0.09f, 1.16f, 0.09f), new Vector3(0f, 0f, 90f));
        if (fresh && Application.isPlaying)
        {
            Juice.PopIn(visuals, 0.35f);
            Sfx.Play("slam", transform.position, 0.5f, 1.2f);
        }
    }

    void OnDestroy()
    {
        if (visuals != null) Destroy(visuals.gameObject);
    }
}

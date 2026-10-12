using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Truck recovery (Docs/Progression.md §6.1, base game, not an upgrade): when the truck is upside down / on its side,
/// or stuck (under 0.3 m/s with throttle held for 3 s), the driver gets "Hold R: Call a Scrapazon Prime Tow (−$25)".
/// Holding R for 1.5 s sends a crane drone that lifts the truck, rights it and sets it down on the nearest road.
/// With truck_handling L1+ it's free and takes a 0.5 s hold ("Unflip button"). The tow fee goes through the Ledger.
/// Only the seated local driver (who simulates the truck) can call it. Added by <see cref="ShopEffectsRunner"/>.
/// <para>The same drone also brings an EMPTY truck to a player who pressed Esc &gt; Unstuck on foot (Docs/UIPass.md §5.5,
/// <see cref="TruckCall"/>): <see cref="StartDelivery"/> runs on the machine that simulates the truck (the host, for a parked
/// one) and the other machines only watch (<see cref="ObserveTow"/>): their drone follows the truck's replicated pose, so
/// nothing about the drone itself is networked.</para>
/// </summary>
[DisallowMultipleComponent]
public class TruckRecovery : MonoBehaviour
{
    public KeyCode recoverKey = KeyCode.R;

    const float FlippedUpDot = 0.45f;      // up·Y below this = on its side / roof
    const float FlippedSeconds = 1.0f;
    const float StuckSpeed = 0.3f;
    const float StuckSeconds = 3f;
    const float RoadSearchRadius = 60f;

    TruckControllerV2 truck;
    Rigidbody body;
    ShopTruckMods mods;
    float flippedFor, stuckFor, holdFor;
    bool offered, busy;
    float lastOfferToast = -99f;
    // the watching side of a delivery (ObserveTow); droneRoot is whichever drone this component is flying right now
    bool observing, observeEnded, observeFree;
    Vector3 observeTarget;
    Transform droneRoot;

    /// <summary>True while a tow / unflip / delivery is lifting the truck (here, or on the machine that moves it).</summary>
    public bool Busy => busy || observing;
    /// <summary>True while the "Hold R" prompt is up.</summary>
    public bool Offered => offered;

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
    }

    void OnDisable()
    {
        ClearPrompt();
        if (busy && body != null) { body.isKinematic = false; busy = false; }
        observing = false;
        if (droneRoot != null) { Destroy(droneRoot.gameObject); droneRoot = null; }
    }

    bool Upright => Vector3.Dot(transform.up, Vector3.up) >= FlippedUpDot;

    void Update()
    {
        if (truck == null || body == null) return;
        if (mods == null) mods = GetComponent<ShopTruckMods>();
        if (busy) return;
        if (!truck.IsLocalPlayerSeated || PlayerController.IsUIBlocking || SceneController.IsTransitioning)
        {
            flippedFor = stuckFor = holdFor = 0f;
            ClearPrompt();
            return;
        }

        float dt = Time.deltaTime;
        float speed = body.linearVelocity.magnitude;
        bool throttle = Mathf.Abs(GameControls.DriveAxes().y) > 0.3f;
        flippedFor = Upright ? 0f : flippedFor + dt;
        stuckFor = speed < StuckSpeed && throttle && Upright ? stuckFor + dt : (speed < StuckSpeed && Upright && stuckFor > StuckSeconds ? stuckFor : 0f);

        bool need = flippedFor >= FlippedSeconds || stuckFor >= StuckSeconds;
        if (!need)
        {
            holdFor = 0f;
            ClearPrompt();
            return;
        }

        var h = UpgradeEffects.HandlingAtLevel(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckHandling));
        bool free = h.freeUnflip;
        float holdNeeded = Mathf.Max(0.1f, h.unflipHoldSeconds);
        if (!offered)
        {
            offered = true;
            if (Time.time - lastOfferToast > 20f)
            {
                lastOfferToast = Time.time;
                string key = GameControls.UsingGamepad ? "D-pad up" : "R";
                GameHUD.Toast(free ? $"Physics won. Hold {key} to unflip (free, thanks Tony)." : $"Physics won. Hold {key} to call a {Scrapazon.Name} Prime Tow (−{Economy.Money(Economy.TowFee)}).", ToastStyle.Info, 3f);
                Sfx.Play("notify", null, 0.6f, 1f);
            }
        }

        if (GameControls.Held(recoverKey, GameControls.Action.Recover))
        {
            if (holdFor <= 0f) Sfx.Play("click", transform.position, 0.6f, 1.2f);
            holdFor += dt;
            if (holdFor >= holdNeeded)
            {
                holdFor = 0f;
                ClearPrompt();
                StartCoroutine(Recover(free));
                return;
            }
        }
        else holdFor = 0f;

        string verb = free ? "Unflip (free)" : $"Call a {Scrapazon.Name} Prime Tow (−{Economy.Money(Economy.TowFee)})";
        string prompt = holdFor > 0f ? $"{(free ? "Unflipping" : "Calling the tow")}… {Bar(holdFor / holdNeeded)}" : $"Hold {(GameControls.UsingGamepad ? "D-pad up" : "R")}: {verb}";
        if (mods != null) mods.HintOverride = prompt; else GameHUD.SetContextHint(prompt);
    }

    static string Bar(float k)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(k * 10f), 0, 10);
        return "[" + new string('|', n) + new string('.', 10 - n) + "]";
    }

    void ClearPrompt()
    {
        if (!offered) return;
        offered = false;
        if (mods != null) { mods.HintOverride = null; mods.RefreshHint(); }
    }

    // ------------------------------------------------------------------ the tow

    /// <summary>Start a recovery right now (debug / tests). free = no tow fee.</summary>
    public void RecoverNow(bool free)
    {
        if (!busy && isActiveAndEnabled) StartCoroutine(Recover(free));
    }

    /// <summary>
    /// The Esc menu's Unstuck while driving (Docs/UIPass.md §5.5; added by UP-3): the same tow as holding R, without the
    /// flipped/stuck check, for the seated local driver (who simulates the truck). The fee follows the hold-R rules
    /// (−$25 through the Ledger; free with handling L1+). Returns false with a readable <paramref name="whyNot"/>.
    /// </summary>
    public bool TryStartTow(bool fromMenu, out string whyNot)
    {
        whyNot = null;
        if (busy) { whyNot = "The tow drone is already on it."; return false; }
        if (!isActiveAndEnabled || truck == null || body == null) { whyNot = "The tow service can't reach this truck."; return false; }
        if (!truck.IsLocalPlayerSeated) { whyNot = "Only the driver can call the tow."; return false; }
        var no = GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned && !no.IsOwner) { whyNot = "Only the driver can call the tow."; return false; }
        flippedFor = stuckFor = holdFor = 0f;
        ClearPrompt();
        bool free = UpgradeEffects.HandlingAtLevel(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckHandling)).freeUnflip;
        if (fromMenu) Debug.Log($"[TruckRecovery] Tow from the Esc menu ({(free ? "free" : "fee")}).");
        StartCoroutine(Recover(free));
        return true;
    }

    /// <summary>Lift, right and set the truck down on the nearest road. Charges the tow fee unless free.</summary>
    public IEnumerator Recover(bool free)
    {
        GetSetDownPose(out Vector3 target, out Quaternion endRot);
        return Deliver(target, endRot, free, true, null, null, null);
    }

    /// <summary>
    /// The Esc menu's Unstuck on foot (Docs/UIPass.md §5.5): fly this EMPTY truck to <paramref name="position"/> /
    /// <paramref name="rotation"/> with the tow drone. Only the machine that simulates the truck can (the host, for a parked
    /// one): <see cref="TruckCall"/> has already checked who may call it and that it fits there. The fee is the caller's
    /// business and <see cref="TruckCall"/> posts it, so nothing is charged here. <paramref name="started"/> and
    /// <paramref name="finished"/> let the host tell the other machines to show the drone (<see cref="ObserveTow"/>);
    /// <paramref name="keepGoing"/> returning false (the caller left and the truck went home) stops the haul where it is.
    /// </summary>
    public bool StartDelivery(Vector3 position, Quaternion rotation, bool free, System.Action<Vector3> started,
                              System.Action<Vector3> finished, System.Func<bool> keepGoing, out string whyNot)
    {
        whyNot = null;
        if (busy || observing) { whyNot = "The tow drone is already on it."; return false; }
        if (!isActiveAndEnabled || truck == null || body == null) { whyNot = "The tow service can't reach this truck."; return false; }
        var no = GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned && !no.IsOwner) { whyNot = "The tow service can't reach this truck."; return false; }
        flippedFor = stuckFor = holdFor = 0f;
        ClearPrompt();
        StartCoroutine(Deliver(position, rotation, free, false, started, finished, keepGoing));
        return true;
    }

    /// <summary>
    /// The drone's whole job: fly in, lift the truck and right it, carry it to <paramref name="target"/>, set it down and
    /// leave. <paramref name="charge"/> = the seated driver's own tow (the fee is posted here, a quick hop to the nearest
    /// road); false = a haul to a player on foot (<see cref="TruckCall"/> posted the fee; a long way takes longer and arcs
    /// over the rooftops, and it stops if somebody climbs in or the truck changes hands). <paramref name="started"/> gets
    /// the point the drone starts from, <paramref name="finished"/> where it set the truck down.
    /// </summary>
    IEnumerator Deliver(Vector3 target, Quaternion endRot, bool free, bool charge, System.Action<Vector3> started, System.Action<Vector3> finished,
                        System.Func<bool> keepGoing)
    {
        if (busy) yield break;
        // Only the machine that simulates the truck may move it.
        var no = GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned && !no.IsOwner) yield break;
        busy = true;
        bool haul = !charge;
        Vector3 start = transform.position;
        Quaternion startRot = transform.rotation;
        // A truck parked in a shop bay is under the map: the drone drops it in from the sky instead of dragging it through the world.
        bool fromBay = haul && DepotRowLayout.InteriorAt(start) >= 0 && DepotRowLayout.InteriorAt(target) < 0;
        if (fromBay)
        {
            start = target + Vector3.up * 26f - (endRot * Vector3.forward) * 18f;
            startRot = endRot;
            DepotRowWorld.TeleportTruck(truck, start, startRot);
        }

        if (charge && !free)
            Ledger.Charge(Economy.TowFee, $"{Scrapazon.Name} Prime Tow", LedgerIds.Tow, start + Vector3.up * 3f);
        started?.Invoke(start);

        GameHUD.FloatingText(start + Vector3.up * 4.5f, free ? "UNFLIP!" : $"{Scrapazon.Name.ToUpperInvariant()} PRIME TOW!", new Color(1f, 0.6f, 0f), 1.5f);
        Sfx.Play("whoosh", start, 0.9f, 0.7f);
        if (haul && !fromBay) Sfx.Play("whoosh", target, 0.7f, 0.9f); // the other end hears it coming

        // --- the crane drone flies in
        var drone = BuildDrone(out Transform hook, out Transform cable, out Transform[] rotors);
        droneRoot = drone;
        // Somebody climbed in, the truck changed hands, or nobody wants it any more: stop moving it and let it be.
        bool Lost() => this == null || !isActiveAndEnabled || (no != null && no.IsSpawned && !no.IsOwner) ||
                       (haul && truck != null && truck.HasDriver) || (keepGoing != null && !keepGoing());
        bool lost = false;
        Vector3 hover = start + Vector3.up * 7.5f;
        Vector3 from = hover + new Vector3(0f, 12f, -8f);
        float t = 0f, dur = free ? 0.45f : 0.8f;
        if (fromBay) { drone.position = start + Vector3.up * 5.3f; t = 1f; }
        while (t < 1f && !lost)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / dur);
            drone.position = Vector3.Lerp(from, hover, 1f - (1f - t) * (1f - t));
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, transform.position + Vector3.up * 2.4f, t);
            yield return null;
            lost = Lost();
        }
        if (lost) { Abandon(drone, no, finished); yield break; }
        if (!fromBay)
        {
            Sfx.Play("click", hover, 0.8f, 0.8f);
            Sfx.Play("squeak", hover, 0.5f, 1.4f);
        }

        // --- lift, right, carry to the road, set down
        // Zero the velocities BEFORE going kinematic (setting them on a kinematic body only logs a warning).
        if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        body.isKinematic = true;
        Vector3 up = fromBay ? start : start + Vector3.up * 2.2f;
        // a short tow stays level with the truck and drops the last bit; a haul comes down to the spot (the truck may start
        // from a bluff, or from the sky over a bay)
        Vector3 over = new Vector3(target.x, (haul ? target.y : Mathf.Max(target.y, start.y)) + 2.2f, target.z);
        float liftDur = free ? 0.6f : 0.9f;
        float reach = (over - up).magnitude;
        float carryDur = Mathf.Clamp(reach / 9f, 0.3f, 1.6f);
        float arc = 0f, cruise = 0f;
        if (haul && reach > 14.4f)
        {
            // from across town: about 25-40 m/s, up and over the roofs (and the hills: never closer than `cruise` to the ground mid-flight)
            carryDur = Mathf.Clamp(1.6f + (reach - 14.4f) / 45f, 1.6f, 6f);
            arc = Mathf.Clamp((reach - 12f) * 0.12f, 0f, 16f);
            if (reach > 25f) cruise = 14f;
        }
        t = fromBay ? 1f : 0f;
        while (t < 1f && !lost)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / liftDur);
            float e = Mathf.SmoothStep(0f, 1f, t);
            Vector3 p = Vector3.Lerp(start, up, e) + Vector3.right * Mathf.Sin(t * 18f) * 0.05f * (1f - t);
            Quaternion r = Quaternion.Slerp(startRot, endRot, e);
            MoveTruck(p, r);
            drone.position = p + Vector3.up * 5.3f;
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, p + Vector3.up * 2.4f, 1f);
            yield return null;
            lost = Lost();
        }
        t = 0f;
        while (t < 1f && !lost)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / carryDur);
            float e = Mathf.SmoothStep(0f, 1f, t);
            float belly = Mathf.Sin(e * Mathf.PI);
            Vector3 p = Vector3.Lerp(up, over, e) + Vector3.up * (belly * arc);
            if (cruise > 0f) p.y = Mathf.Max(p.y, TownRelief.Height(p.x, p.z) + cruise * Mathf.Min(1f, belly * 3f));
            MoveTruck(p, endRot);
            drone.position = p + Vector3.up * 5.3f;
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, p + Vector3.up * 2.4f, 1f);
            yield return null;
            lost = Lost();
        }
        t = 0f;
        while (t < 1f && !lost)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / 0.35f);
            Vector3 p = Vector3.Lerp(over, target, t * t);
            MoveTruck(p, endRot);
            drone.position = p + Vector3.up * 5.3f;
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, p + Vector3.up * 2.4f, 1f);
            yield return null;
            lost = Lost();
        }
        if (lost) { Abandon(drone, no, finished); yield break; }
        MoveTruck(target, endRot);
        body.isKinematic = false;
        body.linearVelocity = Vector3.down * 0.5f;
        body.angularVelocity = Vector3.zero;
        busy = false;
        flippedFor = stuckFor = holdFor = 0f;
        finished?.Invoke(target);

        // --- juice: thunk, dust, the drone waves and leaves
        Sfx.Play("thud", target, 1f, 0.75f);
        Sfx.Play("slam", target, 0.6f, 0.8f);
        Fx.Dust(target + Vector3.up * 0.3f, 1.6f);
        if (NearView(target, 35f)) Juice.Shake(0.2f, 0.25f);
        GameHUD.FloatingText(target + Vector3.up * 4.2f, free ? "Good as new-ish!" : "Delivered! (it's your truck)", new Color(0.55f, 1f, 0.55f), 1.2f);
        yield return DroneLeaves(drone, hook, cable, rotors);
    }

    /// <summary>The tow stopped early (the truck is somebody's again, or this component went away): give the body back if
    /// it is still ours, tell the watchers, and send the drone off.</summary>
    void Abandon(Transform drone, NetworkObject no, System.Action<Vector3> finished)
    {
        if (this != null)
        {
            bool still = no == null || !no.IsSpawned || no.IsOwner;
            if (still && body != null) { body.isKinematic = false; body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            busy = false;
            finished?.Invoke(transform.position);
        }
        if (drone != null) Juice.ShrinkAndDestroy(drone.gameObject, 0.25f);
        if (this != null && droneRoot == drone) droneRoot = null;
    }

    /// <summary>The drone waves, rises away and shrinks out of the world.</summary>
    IEnumerator DroneLeaves(Transform drone, Transform hook, Transform cable, Transform[] rotors)
    {
        var go = drone.gameObject;
        float t = 0f;
        Vector3 leave0 = drone.position;
        Vector3 leave1 = leave0 + new Vector3(6f, 16f, 4f);
        while (t < 1f && drone != null)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / 0.9f);
            drone.position = Vector3.Lerp(leave0, leave1, t * t);
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, drone.position + Vector3.down * 1.2f, 1f - t);
            yield return null;
        }
        if (go != null) Juice.ShrinkAndDestroy(go, 0.25f);
        if (droneRoot == drone) droneRoot = null;
    }

    /// <summary>The local camera is within <paramref name="radius"/> of a point (a thump only shakes the screen of whoever is near it).</summary>
    static bool NearView(Vector3 p, float radius)
    {
        var cam = Camera.main;
        return cam == null || (cam.transform.position - p).sqrMagnitude <= radius * radius;
    }

    // ------------------------------------------------------------------ the watching side of a delivery

    /// <summary>
    /// A delivery of this truck started on the machine that simulates it (<see cref="TruckCall"/>): show the drone. It is
    /// only paint: it flies in at <paramref name="at"/>, then hangs over the truck wherever its replicated pose goes, until
    /// <see cref="ObserveTowEnd"/> (or a 45 s watchdog) sends it off.
    /// </summary>
    public void ObserveTow(Vector3 at, bool free)
    {
        if (busy || observing || !isActiveAndEnabled) return;
        observing = true;
        observeEnded = false;
        observeFree = free;
        StartCoroutine(Observe(at, free));
    }

    /// <summary>The delivery ended: the drone sets down, thumps and leaves.</summary>
    public void ObserveTowEnd(Vector3 target)
    {
        if (!observing) return;
        observeTarget = target;
        observeEnded = true;
    }

    IEnumerator Observe(Vector3 at, bool free)
    {
        var drone = BuildDrone(out Transform hook, out Transform cable, out Transform[] rotors);
        droneRoot = drone;
        GameHUD.FloatingText(at + Vector3.up * 4.5f, free ? "UNFLIP!" : $"{Scrapazon.Name.ToUpperInvariant()} PRIME TOW!", new Color(1f, 0.6f, 0f), 1.5f);
        Sfx.Play("whoosh", at, 0.9f, 0.7f);
        Vector3 hover = at + Vector3.up * 7.5f;
        Vector3 from = hover + new Vector3(0f, 12f, -8f);
        float t = 0f, dur = free ? 0.45f : 0.8f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / dur);
            drone.position = Vector3.Lerp(from, hover, 1f - (1f - t) * (1f - t));
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, transform.position + Vector3.up * 2.4f, t);
            yield return null;
        }
        Sfx.Play("click", hover, 0.8f, 0.8f);
        Sfx.Play("squeak", hover, 0.5f, 1.4f);

        // hang over the truck wherever the network says it is
        float giveUp = Time.time + 45f;
        while (!observeEnded && Time.time < giveUp)
        {
            Vector3 want = transform.position + Vector3.up * 5.3f;
            drone.position = Vector3.Lerp(drone.position, want, 1f - Mathf.Exp(-22f * Time.deltaTime)); // tight: a haul moves 25-40 m/s
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, transform.position + Vector3.up * 2.4f, 1f);
            yield return null;
        }
        Vector3 target = observeEnded ? observeTarget : transform.position;
        observing = false;
        Sfx.Play("thud", target, 1f, 0.75f);
        Sfx.Play("slam", target, 0.6f, 0.8f);
        Fx.Dust(target + Vector3.up * 0.3f, 1.6f);
        if (NearView(target, 35f)) Juice.Shake(0.2f, 0.25f);
        GameHUD.FloatingText(target + Vector3.up * 4.2f, observeFree ? "Good as new-ish!" : "Delivered! (it's your truck)", new Color(0.55f, 1f, 0.55f), 1.2f);
        yield return DroneLeaves(drone, hook, cable, rotors);
    }

    /// <summary>Where a recovery puts the truck: upright (keeping its heading) on the nearest road point.</summary>
    public void GetSetDownPose(out Vector3 pos, out Quaternion rot)
    {
        pos = FindSetDownPoint(transform.position);
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) { fwd = transform.up; fwd.y = 0f; }
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
    }

    void MoveTruck(Vector3 p, Quaternion r)
    {
        transform.SetPositionAndRotation(p, r);
        body.position = p;
        body.rotation = r;
    }

    /// <summary>The nearest point on a road slab (Ground layer, "Road_*"/depot) within 60 m, at truck-root height; else here.</summary>
    Vector3 FindSetDownPoint(Vector3 from)
    {
        Vector3 best = from;
        float bestD = float.MaxValue;
        // Coastal streets are non-convex ribbon MeshColliders, not the old rectangular slabs.
        // ClosestPoint cannot query those meshes, and their AABB centre may be inside a river/lot on a bend.
        // Work from their authored centreline instead; pocket garages still use their own local ground.
        if (DepotRowLayout.InteriorAt(from) < 0 && WorldLifeAnchor.Latest != null &&
            WorldLifeAnchor.Latest.Graph.layoutId == CoastalCityLayout.LayoutId)
        {
            // The blueprint's roads are plan coordinates: search in plan, then stand the point on the relief.
            var plan = from; plan.y = 0f;
            if (TryRoadSetDownPoint(plan, CoastalCityLayout.Roads, RoadSearchRadius, out var coastal)) best = TownRelief.OnGround(coastal);
            return SnapSetDownPoint(best, from.y);
        }
        int n = Physics.OverlapSphereNonAlloc(from, RoadSearchRadius, roadBuffer, 1 << 6, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = roadBuffer[i];
            if (c == null || c.transform.IsChildOf(transform)) continue;
            string nm = c.gameObject.name;
            if (!nm.StartsWith("Road") && nm.IndexOf("Depot", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            Vector3 p = c.ClosestPoint(from);
            // Stay a little inside the slab so the truck's width fits on the asphalt.
            Vector3 inward = c.bounds.center - p; inward.y = 0f;
            if (inward.sqrMagnitude > 0.01f) p += inward.normalized * Mathf.Min(2.5f, inward.magnitude);
            float d = (p - from).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return SnapSetDownPoint(best, from.y);
    }

    /// <summary>Pure recovery projection onto actual road segments, never a whole-road bounding box. The centreline
    /// leaves room for the truck on both sides and is valid at bends/endpoints. Coordinates and widths may be edited.</summary>
    public static bool TryRoadSetDownPoint(Vector3 from, IReadOnlyList<CoastalCityLayout.RoadDef> roads, float maxDistance, out Vector3 point)
    {
        point = from;
        if (roads == null || !Finite(from) || float.IsNaN(maxDistance) || float.IsInfinity(maxDistance) || maxDistance < 0f) return false;
        float best = maxDistance * maxDistance;
        bool found = false;
        foreach (var road in roads)
        {
            if (road?.points == null || road.points.Length < 2) continue;
            for (int i = 1; i < road.points.Length; i++)
            {
                Vector3 a = road.points[i - 1], b = road.points[i];
                if (!Finite(a) || !Finite(b)) continue;
                Vector3 delta = b - a; delta.y = 0f;
                if (delta.sqrMagnitude < .000001f) continue;
                float u = Mathf.Clamp01(Vector3.Dot(from - a, delta) / delta.sqrMagnitude);
                Vector3 candidate = Vector3.Lerp(a, b, u);
                float distance = (candidate - from).sqrMagnitude;
                if (distance > best || (found && distance >= best)) continue;
                best = distance; point = candidate; found = true;
            }
        }
        return found;
    }

    static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
        float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));

    static Vector3 SnapSetDownPoint(Vector3 best, float fallbackY)
    {
        // Snap onto the ground there; the root sits ~0.4 m below the wheels' contact, drop it from a little above.
        if (Physics.Raycast(best + Vector3.up * 6f, Vector3.down, out var hit, 20f, 1 << 6, QueryTriggerInteraction.Ignore))
            best = hit.point;
        else best.y = fallbackY;
        return best + Vector3.up * 0.15f;
    }

    static readonly Collider[] roadBuffer = new Collider[64];

    // ------------------------------------------------------------------ the drone

    static Transform BuildDrone(out Transform hook, out Transform cable, out Transform[] rotors)
    {
        var yellow = ShopMaterials.Get("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
        var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var white = ShopMaterials.Get("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
        var chrome = ShopMaterials.Get("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);

        var root = new GameObject($"{Scrapazon.Name} Tow Drone").transform;
        ShopModKit.Prim(PrimitiveType.Cube, "Body", root, yellow, Vector3.zero, new Vector3(1.6f, 0.45f, 1.6f));
        // the Scrapazon smile + googly eyes on the front
        for (int i = -1; i <= 1; i++)
            ShopModKit.Prim(PrimitiveType.Cube, "Smile", root, black, new Vector3(i * 0.22f, -0.08f + (i == 0 ? -0.05f : 0f), 0.81f), new Vector3(0.24f, 0.05f, 0.02f), new Vector3(0f, 0f, -i * 22f));
        for (int i = -1; i <= 1; i += 2)
        {
            ShopModKit.Prim(PrimitiveType.Sphere, "Eye", root, white, new Vector3(i * 0.3f, 0.1f, 0.81f), new Vector3(0.26f, 0.3f, 0.08f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Pupil", root, black, new Vector3(i * 0.3f + 0.04f * i, 0.06f, 0.85f), new Vector3(0.12f, 0.14f, 0.04f));
        }
        rotors = new Transform[4];
        int k = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 c = new Vector3(sx * 1.15f, 0.2f, sz * 1.15f);
                ShopModKit.Prim(PrimitiveType.Cube, "Strut", root, black, c * 0.55f, new Vector3(0.12f, 0.1f, 1.1f), new Vector3(0f, sx * sz * 45f, 0f));
                ShopModKit.Prim(PrimitiveType.Cylinder, "Motor", root, chrome, c, new Vector3(0.2f, 0.15f, 0.2f));
                var rotor = ShopModKit.Pivot("Rotor", root, c + Vector3.up * 0.18f);
                ShopModKit.Prim(PrimitiveType.Cube, "Blade", rotor, black, Vector3.zero, new Vector3(1.3f, 0.02f, 0.12f));
                ShopModKit.Prim(PrimitiveType.Cube, "Blade", rotor, black, Vector3.zero, new Vector3(0.12f, 0.02f, 1.3f));
                rotors[k++] = rotor;
            }
        cable = ShopModKit.Prim(PrimitiveType.Cylinder, "Cable", root, black, Vector3.zero, new Vector3(0.05f, 1f, 0.05f));
        hook = ShopModKit.Pivot("Hook", root, Vector3.down);
        ShopModKit.Prim(PrimitiveType.Cube, "Hook Bar", hook, chrome, Vector3.zero, new Vector3(1.2f, 0.12f, 0.12f));
        for (int i = -1; i <= 1; i += 2)
            ShopModKit.Prim(PrimitiveType.Cube, "Hook Claw", hook, chrome, new Vector3(i * 0.55f, -0.2f, 0f), new Vector3(0.1f, 0.4f, 0.1f));
        return root;
    }

    static void SpinRotors(Transform[] rotors)
    {
        float a = Time.deltaTime * 1500f;
        foreach (var r in rotors) if (r != null) r.Rotate(0f, a, 0f, Space.Self);
    }

    static void SetCable(Transform cable, Transform hook, Vector3 droneWorld, Vector3 hookWorld, float extend01)
    {
        if (cable == null || hook == null) return;
        var root = cable.parent;
        Vector3 end = Vector3.Lerp(droneWorld + Vector3.down * 0.4f, hookWorld, Mathf.Clamp01(extend01));
        Vector3 localEnd = root.InverseTransformPoint(end);
        Vector3 localStart = new Vector3(0f, -0.25f, 0f);
        Vector3 mid = (localStart + localEnd) * 0.5f;
        float len = Mathf.Max(0.05f, (localEnd - localStart).magnitude);
        cable.localPosition = mid;
        cable.localRotation = Quaternion.FromToRotation(Vector3.up, (localEnd - localStart).normalized);
        cable.localScale = new Vector3(0.05f, len * 0.5f, 0.05f);
        hook.localPosition = localEnd;
        hook.localRotation = Quaternion.identity;
    }
}

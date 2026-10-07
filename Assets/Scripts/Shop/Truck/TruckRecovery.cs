using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Truck recovery (Docs/Progression.md §6.1, base game, not an upgrade): when the truck is upside down / on its side,
/// or stuck (under 0.3 m/s with throttle held for 3 s), the driver gets "Hold R: Call a Scrapazon Prime Tow (−$25)".
/// Holding R for 1.5 s sends a crane drone that lifts the truck, rights it and sets it down on the nearest road.
/// With truck_handling L1+ it's free and takes a 0.5 s hold ("Unflip button"). The tow fee goes through the Ledger.
/// Only the seated local driver (who simulates the truck) can call it. Added by <see cref="ShopEffectsRunner"/>.
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

    /// <summary>True while a tow / unflip is lifting the truck.</summary>
    public bool Busy => busy;
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

        var h = UpgradeEffects.Handling;
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
        bool free = UpgradeEffects.Handling.freeUnflip;
        if (fromMenu) Debug.Log($"[TruckRecovery] Tow from the Esc menu ({(free ? "free" : "fee")}).");
        StartCoroutine(Recover(free));
        return true;
    }

    /// <summary>Lift, right and set the truck down on the nearest road. Charges the tow fee unless free.</summary>
    public IEnumerator Recover(bool free)
    {
        if (busy) yield break;
        // Only the machine that simulates the truck may move it.
        var no = GetComponent<NetworkObject>();
        if (no != null && no.IsSpawned && !no.IsOwner) yield break;
        busy = true;
        Vector3 start = transform.position;
        Quaternion startRot = transform.rotation;
        GetSetDownPose(out Vector3 target, out Quaternion endRot);

        if (!free)
            Ledger.Charge(Economy.TowFee, $"{Scrapazon.Name} Prime Tow", LedgerIds.Tow, start + Vector3.up * 3f);

        GameHUD.FloatingText(start + Vector3.up * 4.5f, free ? "UNFLIP!" : $"{Scrapazon.Name.ToUpperInvariant()} PRIME TOW!", new Color(1f, 0.6f, 0f), 1.5f);
        Sfx.Play("whoosh", start, 0.9f, 0.7f);

        // --- the crane drone flies in
        var drone = BuildDrone(out Transform hook, out Transform cable, out Transform[] rotors);
        Vector3 hover = start + Vector3.up * 7.5f;
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

        // --- lift, right, carry to the road, set down
        // Zero the velocities BEFORE going kinematic (setting them on a kinematic body only logs a warning).
        if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        body.isKinematic = true;
        Vector3 up = start + Vector3.up * 2.2f;
        Vector3 over = new Vector3(target.x, Mathf.Max(target.y, start.y) + 2.2f, target.z);
        float liftDur = free ? 0.6f : 0.9f;
        float carryDur = Mathf.Clamp((over - up).magnitude / 9f, 0.3f, 1.6f);
        t = 0f;
        while (t < 1f)
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
        }
        t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / carryDur);
            Vector3 p = Vector3.Lerp(up, over, Mathf.SmoothStep(0f, 1f, t));
            MoveTruck(p, endRot);
            drone.position = p + Vector3.up * 5.3f;
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, p + Vector3.up * 2.4f, 1f);
            yield return null;
        }
        t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / 0.35f);
            Vector3 p = Vector3.Lerp(over, target, t * t);
            MoveTruck(p, endRot);
            drone.position = p + Vector3.up * 5.3f;
            SpinRotors(rotors);
            SetCable(cable, hook, drone.position, p + Vector3.up * 2.4f, 1f);
            yield return null;
        }
        MoveTruck(target, endRot);
        body.isKinematic = false;
        body.linearVelocity = Vector3.down * 0.5f;
        body.angularVelocity = Vector3.zero;
        busy = false;
        flippedFor = stuckFor = holdFor = 0f;

        // --- juice: thunk, dust, the drone waves and leaves
        Sfx.Play("thud", target, 1f, 0.75f);
        Sfx.Play("slam", target, 0.6f, 0.8f);
        Fx.Dust(target + Vector3.up * 0.3f, 1.6f);
        Juice.Shake(0.2f, 0.25f);
        GameHUD.FloatingText(target + Vector3.up * 4.2f, free ? "Good as new-ish!" : "Delivered! (it's your truck)", new Color(0.55f, 1f, 0.55f), 1.2f);
        var go = drone.gameObject;
        t = 0f;
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
        // Snap onto the ground there; the root sits ~0.4 m below the wheels' contact, drop it from a little above.
        if (Physics.Raycast(best + Vector3.up * 6f, Vector3.down, out var hit, 20f, 1 << 6, QueryTriggerInteraction.Ignore))
            best = hit.point;
        else best.y = from.y;
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

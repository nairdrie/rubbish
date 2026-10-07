using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bag cannon (F3, Docs/Multiplayer.md §14.3; Scrapazon <see cref="ShopIds.BagCannon"/>, a truck upgrade). With a bag in your
/// hands within <see cref="Range"/> m of your own truck, <b>hold R</b>: a dotted arc runs from your hands to the hopper (the cannon on
/// the truck's side swivels to track you); <b>let go</b>: FWOOMP, the bag flies the arc into the hopper as an ordinary physical
/// throw (co-op: N2 streams it and the host confirms the delivery and pays by the truck's business, like any throw). Heavy bags are
/// too heavy. <see cref="MisfirePct"/>% of shots misfire (a hash of the bag id and your shot count): the bag bursts in your hands, a
/// juice splat lands at your feet and you get bonked onto your back.
/// On every player copy (by <see cref="TellsRuntime"/>); only the owner acts, every copy plays the relayed FWOOMP / misfire.
/// </summary>
[DisallowMultipleComponent]
public class BagCannon : MonoBehaviour
{
    public static BagCannon Local { get; private set; }
    public const KeyCode Key = KeyCode.R;
    public const float Range = 14f, MinRange = 2.2f, Cooldown = 1.2f, MisfirePct = 12.5f;
    public const string ReadyHint = "[Hold R] BAG CANNON it into the truck   [E] Toss  [Hold LMB] Throw  [G] Drop";
    const string ClearAimHint = "Release [R] to fire the bag cannon";
    const string PadReadyHint = "[Hold D-pad up] BAG CANNON   [X] Toss  [Hold RT] Throw  [B] Drop";
    const string PadClearAimHint = "Release [D-pad up] to fire the bag cannon";
    const string BlockedAimHint = "BAG CANNON BLOCKED — move for a clear arc";

    /// <summary>Stats (bots, tests).</summary>
    public static int Shots, Misfires;
    /// <summary>Tests: behave as if R is held (aim); set false to fire.</summary>
    public static bool TestAim;

    PlayerController pc;
    TrashCarrier carrier;
    LineRenderer arc;
    bool aiming, arcClear;
    float nextShotAt, nextFind;
    int shotSerial;
    TruckHopper target;
    static readonly Vector3[] arcPts = new Vector3[28];
    static Material arcMat;

    public bool IsLocal => pc != null && CoopPlayerHooks.IsLocalOwner(pc);
    public ulong ClientId => pc != null && pc.IsSpawned ? pc.OwnerClientId : 0;
    /// <summary>Owned, holding a bag that can go, and a hopper in range (shows the hint).</summary>
    public bool Ready { get; private set; }
    public string Hint => aiming ? (arcClear ? (GameControls.UsingGamepad ? PadClearAimHint : ClearAimHint) : BlockedAimHint) :
                                 (GameControls.UsingGamepad ? PadReadyHint : ReadyHint);
    public bool Aiming => aiming;
    public static bool Owned => UpgradeEffects.Level(ShopIds.BagCannon) > 0;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
    }

    void OnEnable() => BagTellsNet.FxPlayed += OnFx;

    void OnDisable()
    {
        BagTellsNet.FxPlayed -= OnFx;
        if (Local == this) Local = null;
        ShowArc(false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Local = null; Shots = Misfires = 0; TestAim = false; }

    // ==================================================================================== owner

    void Update()
    {
        if (!IsLocal) return;
        Local = this;
        Ready = false;
        var held = carrier != null ? carrier.Held : null;
        if (!CanFire(held)) { Cancel(); return; }
        if (Time.unscaledTime >= nextFind || target == null)
        {
            nextFind = Time.unscaledTime + 0.25f;
            target = FindHopper(transform.position);
        }
        if (target == null) { Cancel(); return; }
        Ready = !aiming && Time.time >= nextShotAt && ReleaseOk();
        if (Ready && (GameControls.Down(Key, GameControls.Action.Cannon) || TestAim))
        {
            aiming = true;
            Sfx.Play("click", transform.position + Vector3.up, 0.6f, 0.8f);
        }
        if (!aiming) return;
        Vector3 from = held.WorldCenter;
        Vector3 to = AimPoint(target);
        arcClear = TrySolveClear(from, to, transform, out Vector3 v, out float T);
        DrawArc(from, v, T, arcClear);
        BagCannonMount.Track(target, transform.position);
        if (!GameControls.Held(Key, GameControls.Action.Cannon) && !TestAim)
        {
            if (arcClear) Fire(held, from, to, v);
            else
            {
                Cancel();
                GameHUD.Toast("Bag cannon shot blocked. Move for a clear arc.", ToastStyle.Bad, 1.8f);
            }
        }
    }

    bool CanFire(Carryable held) => IsLocal && Owned && held is TrashBag bag && bag.Tell != BagTell.Heavy &&
                                    pc != null && !pc.IsDriving && !pc.IsPlayingChess && !PlayerController.IsUIBlocking &&
                                    carrier != null && !carrier.ExternalControl && !SceneController.IsTransitioning;

    bool ReleaseOk()
    {
        var f = TrashCarrier.NetCanRelease;
        if (f == null) return true;
        try { return f(carrier); }
        catch (System.Exception e) { Debug.LogException(e); return true; }
    }

    void Cancel()
    {
        if (aiming) { aiming = false; ShowArc(false); }
        arcClear = false;
        Ready = false;
    }

    /// <summary>Owner: fire now (bots call it with the bag already in hand). Returns false when there's nothing to fire at.</summary>
    public bool FireNow()
    {
        var held = carrier != null ? carrier.Held : null;
        if (!CanFire(held) || Time.time < nextShotAt || !ReleaseOk()) return false;
        var t = FindHopper(transform.position);
        if (t == null) return false;
        target = t;
        Vector3 from = held.WorldCenter, to = AimPoint(t);
        if (!TrySolveClear(from, to, transform, out Vector3 v, out _)) return false;
        return Fire(held, from, to, v);
    }

    bool Fire(Carryable held, Vector3 from, Vector3 to, Vector3 v)
    {
        aiming = false;
        ShowArc(false);
        if (!CanFire(held) || carrier.Held != held || Time.time < nextShotAt || !ReleaseOk()) return false;
        nextShotAt = Time.time + Cooldown;
        Shots++;
        shotSerial++;
        string key = held is TrashBag tb && tb.Contents != null ? tb.Contents.bagId : held.name;
        uint roll = (uint)PickupVisuals.StableHash($"{key}#cannon#{ClientId}#{shotSerial}") % 1000u;
        bool misfire = roll < (uint)(MisfirePct * 10f);
        NetAudit.Event("tells", new Dictionary<string, object> { { "op", misfire ? "cannon-misfire" : "cannon" }, { "client", (long)ClientId }, { "key", key ?? "" }, { "dist", Mathf.Round(Vector3.Distance(from, to) * 10f) / 10f } });
        if (misfire) { Misfire(held, from); return true; }

        // An ordinary physical throw on a solved arc: N2 sees a release + a flight; the hopper's trigger catches it.
        carrier.ReleaseToScript();
        held.EndCarry(v, true);
        IgnoreMe(held, 0.6f); // it leaves from your hands: don't let your own body knock it back
        if (held.Body != null) held.Body.angularVelocity = Vector3.Cross(Vector3.up, v.normalized) * 6f;
        if (held is TrashBag bag) bag.NoBurstUntil = Time.time + 4f;
        StartCoroutine(NoDrag(held, 4f));
        StartCoroutine(Trace(held, from, to, v));
        Juice.Shake(0.12f, 0.12f);
        BagTellsNet.Send(BagTellsNet.FxCannon, ClientId, from, to);
        return true;
    }

    static IEnumerator Trace(Carryable c, Vector3 from, Vector3 to, Vector3 v)
    {
        Vector3 v0 = c != null && c.Body != null ? c.Body.linearVelocity : Vector3.zero;
        Vector3 p0 = c != null ? c.WorldCenter : Vector3.zero;
        var near = new List<string>();
        if (c != null)
            foreach (var col in Physics.OverlapSphere(p0 + v.normalized * 0.4f, 0.7f, ~0, QueryTriggerInteraction.Ignore))
                if (!col.transform.IsChildOf(c.transform)) near.Add(col.name + "/" + col.GetType().Name + "/L" + col.gameObject.layer + (col.attachedRigidbody != null ? "/rb" : ""));
        yield return new WaitForFixedUpdate();
        Vector3 v1 = c != null && c.Body != null ? c.Body.linearVelocity : Vector3.zero;
        Vector3 p1 = c != null ? c.WorldCenter : Vector3.zero;
        yield return new WaitForSeconds(0.1f);
        NetAudit.Event("tells", new Dictionary<string, object>
        {
            { "op", "cannon-launch" }, { "from", from.ToString() }, { "to", to.ToString() }, { "v", v.ToString() }, { "v0", v0.ToString() }, { "p0", p0.ToString() },
            { "v1", v1.ToString() }, { "p1", p1.ToString() }, { "v2", c != null && c.Body != null ? c.Body.linearVelocity.ToString() : "-" }, { "p2", c != null ? c.WorldCenter.ToString() : "-" },
            { "kin", c != null && c.Body != null && c.Body.isKinematic }, { "state", c != null ? c.State.ToString() : "-" }, { "near", string.Join(",", near) },
        });
    }

    void IgnoreMe(Carryable c, float seconds)
    {
        var cc = GetComponent<CharacterController>();
        if (c == null || cc == null || !cc.enabled) return;
        var cols = c.GetComponentsInChildren<Collider>();
        foreach (var col in cols) if (col != null && col.enabled) Physics.IgnoreCollision(col, cc, true);
        StartCoroutine(Unignore(cols, cc, seconds));
    }

    static IEnumerator Unignore(Collider[] cols, CharacterController cc, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (cc == null || !cc.enabled) yield break;
        foreach (var col in cols) if (col != null && col.enabled && col.gameObject.activeInHierarchy) Physics.IgnoreCollision(col, cc, false);
    }

    /// <summary>Bags fly with linear damping: the cannon shot flies clean (restored when it lands or after a few seconds).</summary>
    static IEnumerator NoDrag(Carryable c, float max)
    {
        if (c == null || c.Body == null) yield break;
        float was = c.Body.linearDamping;
        c.Body.linearDamping = 0f;
        float until = Time.time + max;
        yield return new WaitForSeconds(0.2f);
        while (c != null && c.Body != null && c.State == CarryState.Thrown && Time.time < until) yield return null;
        if (c != null && c.Body != null) c.Body.linearDamping = was;
    }

    void Misfire(Carryable held, Vector3 from)
    {
        Misfires++;
        carrier.Drop();
        Vector3 feet = transform.position;
        Puddles.Spawn(feet + transform.forward * 0.6f, 1.15f, PuddleKind.Splat);
        if (held is TrashBag bag) bag.BurstNow(from, -transform.forward);
        // Bonk: knocked back onto your bum with stars.
        var kb = GetComponent<CritterKnockback>() ?? gameObject.AddComponent<CritterKnockback>();
        kb.Knock(-transform.forward, 1.8f, 0.4f, 0.4f);
        DizzyStars.Attach(transform, 1.4f, 0.35f, 2.15f);
        Juice.Shake(0.4f, 0.3f);
        Juice.HitStop(0.06f, 0.1f);
        BagTellsNet.Send(BagTellsNet.FxMisfire, ClientId, from, feet);
        if (Time.unscaledTime - lastToast > 10f)
        {
            lastToast = Time.unscaledTime;
            GameHUD.Toast("MISFIRE! The cannon backfired. You are now wearing that bag.", ToastStyle.Bad, 2.5f);
        }
    }

    static float lastToast = -99f;

    // ==================================================================================== every copy: the relayed moment

    void OnFx(byte kind, ulong who, ulong other, Vector3 a, Vector3 b)
    {
        if (who != ClientId || pc == null) return;
        if (kind == BagTellsNet.FxCannon)
        {
            Sfx.Play("boom", a, 0.45f, 1.6f);
            Sfx.Play("whoosh", a, 0.9f, 0.7f);
            Fx.Burst(a, new Color(0.9f, 0.9f, 0.95f), 16, 3.5f);
            Fx.Dust(a, 0.8f);
            GameHUD.FloatingText(a + Vector3.up * 0.7f, "FWOOMP!", new Color(1f, 0.6f, 0.2f), 1.3f);
            BagCannonMount.Puff(b);
        }
        else if (kind == BagTellsNet.FxMisfire)
        {
            Sfx.Play("boom", a, 0.8f, 0.8f);
            Sfx.Play("splat", a, 1f, 0.8f);
            Sfx.Play("ow", a + Vector3.up * 0.6f, 0.9f, 1.1f);
            Fx.Burst(a, new Color(0.36f, 0.27f, 0.10f), 30, 6f);
            GameHUD.FloatingText(a + Vector3.up * 0.9f, "KA-BLORP! (misfire)", new Color(0.9f, 0.5f, 0.2f), 1.5f);
        }
        else if (kind == BagTellsNet.FxNeighbourSplat)
        {
            var victim = PlayerTells.For(other);
            Vector3 at = victim != null ? victim.transform.position + Vector3.up * 1.15f : b;
            Sfx.Play("splat", at, 0.9f, 0.9f);
            Fx.Burst(at, new Color(0.37f, 0.36f, 0.12f), 22, 3f);
            GameHUD.FloatingText(at + Vector3.up * 0.7f, "SPLATTERED!", new Color(0.8f, 0.95f, 0.25f), 1.2f);
            if (victim != null) ShowNeighbourSplat(victim.transform);
        }
    }

    static void ShowNeighbourSplat(Transform victim)
    {
        var root = new GameObject("Cannon neighbour splat").transform;
        root.SetParent(victim, false);
        root.localPosition = new Vector3(0f, 1.18f, 0.35f);
        var juice = ShopModKit.Lit("mat/f3_cannon_splatter", new Color(0.38f, 0.39f, 0.10f), 0.85f);
        ShopModKit.Prim(PrimitiveType.Sphere, "Chest splat", root, juice, Vector3.zero, new Vector3(0.33f, 0.27f, 0.07f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Drip left", root, juice, new Vector3(-0.11f, -0.2f, 0.01f), new Vector3(0.07f, 0.26f, 0.05f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Drip right", root, juice, new Vector3(0.12f, -0.17f, 0.01f), new Vector3(0.06f, 0.21f, 0.05f));
        foreach (var col in root.GetComponentsInChildren<Collider>(true)) { col.enabled = false; Destroy(col); }
        Destroy(root.gameObject, 4f);
    }

    // ==================================================================================== aiming maths

    /// <summary>The nearest hopper of a truck that has your cannon, within range.</summary>
    public static TruckHopper FindHopper(Vector3 me)
    {
        TruckHopper best = null;
        float bestD = Range * Range;
        foreach (var t in TruckControllerV2.All)
        {
            if (t == null || !BagCannonMount.IsMine(t)) continue;
            var h = BagCannonMount.HopperOf(t);
            if (h == null || !h.isActiveAndEnabled) continue;
            Vector3 d = h.TossTargetPosition - me; d.y = 0f;
            float d2 = d.sqrMagnitude;
            if (d2 < MinRange * MinRange || d2 > bestD) continue;
            bestD = d2;
            best = h;
        }
        return best;
    }

    /// <summary>The collection volume sits behind the tailgate. Aim into it, not at the solid tailgate just beyond it.</summary>
    public static Vector3 AimPoint(TruckHopper h)
    {
        if (h == null) return Vector3.zero;
        var trigger = h.transform.Find("HopperTrigger");
        var box = trigger != null ? trigger.GetComponent<BoxCollider>() : null;
        return box != null ? box.bounds.center : h.TossTargetPosition - h.transform.forward * 0.45f;
    }

    /// <summary>Launch velocity to hit <paramref name="to"/> from <paramref name="from"/> (flight time grows with distance).</summary>
    public static Vector3 Solve(Vector3 from, Vector3 to, out float T)
    {
        Vector3 d = to - from;
        float flat = new Vector2(d.x, d.z).magnitude;
        T = Mathf.Clamp(0.55f + flat * 0.065f, 0.6f, 1.5f);
        return VelocityFor(from, to, T);
    }

    static Vector3 VelocityFor(Vector3 from, Vector3 to, float time) => (to - from) / time - 0.5f * Physics.gravity * time;

    static readonly RaycastHit[] arcHits = new RaycastHit[32];
    const int ArcSteps = 18;
    const float ArcRadius = 0.22f;
    /// <summary>Last world collider that rejected a candidate arc (bot diagnostics only).</summary>
    public static string LastClearanceBlocker { get; private set; }

    /// <summary>Find a physically clear path, using a flatter shot when the usual lob crosses an awning or branch.</summary>
    public static bool TrySolveClear(Vector3 from, Vector3 to, Transform owner, out Vector3 velocity, out float flightTime)
    {
        LastClearanceBlocker = null;
        Solve(from, to, out float usual);
        for (int i = 0; i < 5; i++)
        {
            float time = i switch
            {
                0 => usual,
                1 => Mathf.Min(usual, 0.85f),
                2 => 0.7f,
                3 => 0.6f,
                _ => Mathf.Max(usual, 1.35f),
            };
            var v = VelocityFor(from, to, time);
            if (!ArcBlocked(from, v, time, owner))
            {
                LastClearanceBlocker = null;
                velocity = v;
                flightTime = time;
                return true;
            }
        }
        flightTime = usual;
        velocity = VelocityFor(from, to, usual);
        return false;
    }

    static bool ArcBlocked(Vector3 from, Vector3 velocity, float flightTime, Transform owner)
    {
        Vector3 prev = from;
        for (int i = 1; i <= ArcSteps; i++)
        {
            float t = flightTime * i / ArcSteps;
            Vector3 next = from + velocity * t + 0.5f * Physics.gravity * t * t;
            Vector3 d = next - prev;
            float length = d.magnitude;
            if (length > 0.001f)
            {
                int count = Physics.SphereCastNonAlloc(prev, ArcRadius, d / length, arcHits, length,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int hit = 0; hit < count; hit++)
                {
                    var col = arcHits[hit].collider;
                    if (col == null || owner != null && col.transform.IsChildOf(owner)) continue;
                    if (col.GetComponentInParent<PlayerController>() != null || col.GetComponentInParent<Carryable>() != null) continue;
                    LastClearanceBlocker = col.transform.root.name + "/" + col.name;
                    return true;
                }
            }
            prev = next;
        }
        return false;
    }

    void DrawArc(Vector3 from, Vector3 v, float T, bool clear)
    {
        ShowArc(true);
        for (int i = 0; i < arcPts.Length; i++)
        {
            float t = T * i / (arcPts.Length - 1);
            arcPts[i] = from + v * t + 0.5f * Physics.gravity * t * t;
        }
        arc.positionCount = arcPts.Length;
        arc.SetPositions(arcPts);
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 10f);
        arc.startColor = clear ? Color.Lerp(new Color(1f, 0.6f, 0.15f), Color.white, pulse) : new Color(1f, 0.25f, 0.2f);
        arc.endColor = clear ? new Color(0.4f, 1f, 0.45f) : new Color(1f, 0.25f, 0.2f);
    }

    void ShowArc(bool on)
    {
        if (!on) { if (arc != null) arc.enabled = false; return; }
        if (arc == null)
        {
            var go = new GameObject("F3 CannonArc");
            arc = go.AddComponent<LineRenderer>();
            if (arcMat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
                arcMat = new Material(sh) { name = "F3_CannonArc", hideFlags = HideFlags.DontSave };
                arcMat.SetColor("_BaseColor", Color.white);
            }
            arc.sharedMaterial = arcMat;
            arc.widthMultiplier = 0.07f;
            arc.numCapVertices = 2;
            arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arc.receiveShadows = false;
            arc.useWorldSpace = true;
        }
        arc.enabled = true;
    }

    void OnDestroy()
    {
        if (arc != null) Destroy(arc.gameObject);
    }
}

/// <summary>
/// The bag cannon bolted to the side of your own truck (cosmetic; shown while you own <see cref="ShopIds.BagCannon"/>). It swivels to
/// track you while you aim and puffs when you fire. Added to every truck by <see cref="TellsRuntime"/>.
/// </summary>
[DisallowMultipleComponent]
public class BagCannonMount : MonoBehaviour
{
    static readonly List<BagCannonMount> all = new List<BagCannonMount>();
    TruckControllerV2 truck;
    TruckHopper hopper;
    Transform root, barrel;
    float nextCheck, trackUntil;
    Vector3 trackAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        hopper = GetComponentInChildren<TruckHopper>(true);
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    public static TruckHopper HopperOf(TruckControllerV2 t)
    {
        if (t == null) return null;
        var m = t.GetComponent<BagCannonMount>();
        return m != null ? m.hopper : t.GetComponentInChildren<TruckHopper>(true);
    }

    /// <summary>Is this the local business's own truck? (Solo: any truck.)</summary>
    public static bool IsMine(TruckControllerV2 t)
    {
        var h = HopperOf(t);
        if (h == null) return false;
        // Depot Row (co-op one world): the truck of the lot you own. Solo / the legacy flow: your (only) truck.
        if (DepotRowWorld.Active)
        {
            var dr = DepotRowNet.Instance;
            int mine = DepotRowWorld.LocalLot;
            return dr != null && mine >= 0 && t.NetworkObject != null && t.NetworkObject.IsSpawned && dr.LotOfTruck(t.NetworkObjectId) == mine;
        }
        return true;
    }

    /// <summary>The cannon on <paramref name="hopper"/>'s truck turns toward <paramref name="at"/> for a moment.</summary>
    public static void Track(TruckHopper hopper, Vector3 at)
    {
        foreach (var m in all) if (m != null && m.hopper == hopper) { m.trackAt = at; m.trackUntil = Time.time + 0.3f; }
    }

    /// <summary>Puff the cannon nearest <paramref name="hopperPos"/> (a shot just went off).</summary>
    public static void Puff(Vector3 hopperPos)
    {
        BagCannonMount best = null;
        float bd = 8f * 8f;
        foreach (var m in all)
        {
            if (m == null || m.hopper == null || m.barrel == null || !m.root.gameObject.activeSelf) continue;
            float d = (m.hopper.TossTargetPosition - hopperPos).sqrMagnitude;
            if (d < bd) { bd = d; best = m; }
        }
        if (best == null) return;
        Vector3 mouth = best.barrel.position + best.barrel.up * 0.45f;
        Fx.Burst(mouth, new Color(1f, 0.85f, 0.4f), 14, 3f);
        Juice.Punch(best.barrel, 0.25f, 0.2f);
    }

    void Update()
    {
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 1f;
            bool want = BagCannon.Owned && IsMine(truck);
            if (want && root == null) Build();
            if (root != null && root.gameObject.activeSelf != want) root.gameObject.SetActive(want);
        }
        if (root == null || !root.gameObject.activeSelf) return;
        Vector3 look = Time.time < trackUntil ? trackAt - root.position : -transform.forward + Vector3.up * 0.2f;
        look.y = Mathf.Max(0.3f, look.y);
        Quaternion want2 = Quaternion.LookRotation(look.normalized, Vector3.up) * Quaternion.Euler(60f, 0f, 0f);
        root.rotation = Quaternion.Slerp(root.rotation, want2, 1f - Mathf.Exp(-6f * Time.deltaTime));
    }

    void Build()
    {
        var box = TruckControllerV2.BodyBoxMetres;
        var metres = ShopModKit.MetreRoot(transform, "F3 BagCannon");
        var steel = ShopModKit.Lit("mat/f3_cannon_steel", new Color(0.32f, 0.34f, 0.38f), 0.6f, 0.6f);
        var orange = ShopModKit.Lit("mat/f3_cannon_orange", new Color(1f, 0.55f, 0.1f), 0.4f);
        var parent = ShopModKit.Pivot("Mount", metres, new Vector3(box.max.x + 0.08f, box.center.y + box.extents.y * 0.25f, box.center.z - box.extents.z * 0.1f));
        root = ShopModKit.Pivot("Swivel", parent, Vector3.zero);
        barrel = ShopModKit.Pivot("Barrel", root, Vector3.zero);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Tube", barrel, steel, new Vector3(0f, 0.25f, 0f), new Vector3(0.22f, 0.3f, 0.22f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Ring", barrel, orange, new Vector3(0f, 0.5f, 0f), new Vector3(0.27f, 0.04f, 0.27f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Base", parent, steel, Vector3.zero, Vector3.one * 0.28f);
    }
}

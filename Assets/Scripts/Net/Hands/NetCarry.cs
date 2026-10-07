using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One networked carryable on this machine (package N2, Docs/Multiplayer.md §4.5–4.6, §5.2). Added by <see cref="HandsNet"/> to a
/// TrashBag or LooseItem once the host has published it (the host's own object) or built a copy of it (a guest).
/// <list type="bullet">
/// <item><b>Held</b>: posed at the holder's hands on every machine (the holder's <see cref="TrashCarrier"/> on each machine).</item>
/// <item><b>Flight</b>: the thrower's machine simulates it (normal physics) and streams its pose; everyone else holds a
/// kinematic copy on that stream. The thrower also detects catches, bonks, receivers and bursts; the host confirms.</item>
/// <item><b>Rest</b>: the host simulates it (so trucks can nudge it) and streams when it moves; guests follow.</item>
/// <item><b>Tug</b>: posed between the two pullers from the tug meter.</item>
/// </list>
/// A copy this machine doesn't simulate has <see cref="Carryable.NetRemote"/> set, so it never bursts, settles or hits a
/// receiver here (rule 13: no outcome depends on two machines' physics).
/// </summary>
[DisallowMultipleComponent]
public class NetCarry : MonoBehaviour
{
    public uint Id { get; private set; }
    public CarryDesc Desc { get; private set; }
    public CarryNetState St;
    /// <summary>The last state the host decided (no local predictions): what the audit hash uses.</summary>
    public CarryNetState HostSt;
    /// <summary>Thrown or dropped items that were later forced back into their thrower's hands or a tug (must stay 0).</summary>
    public static int Reverts;
    public Carryable Item { get; private set; }
    HandsNet net;

    /// <summary>The local player grabbed it and the host hasn't confirmed yet (the heave: throws wait).</summary>
    public bool PendingGrab;
    /// <summary>A guest's copy went into a receiver (or burst) and is hidden until the host answers.</summary>
    public bool PendingGone;
    /// <summary>Host: flags for the Gone broadcast when it's consumed here (received / burst / rip).</summary>
    public byte GoneFlags;
    /// <summary>Host: true once it has been announced gone.</summary>
    public bool Announced;
    /// <summary>The real contents / item (JSON) every copy is built from ("" for a curb bag: the host keeps those, §6.3).</summary>
    public string Data = "";
    /// <summary>Host: the guest whose machine made it (None = the host's own).</summary>
    public ulong Origin = HandsNet.None;

    // ---- host bookkeeping (heave window, §4.6)
    public bool WindowOpen;
    public float WindowEnd;
    public readonly List<(ulong client, double stamp)> Contenders = new List<(ulong, double)>();

    // ---- following another machine's stream
    struct Snap { public float t; public Vector3 p; public Quaternion r; }
    readonly List<Snap> snaps = new List<Snap>(8);
    const float InterpDelay = 0.11f;
    Vector3 restPos;
    Quaternion restRot;
    bool easeToRest;

    // ---- streaming (when this machine simulates it)
    Vector3 lastSentPos;
    Quaternion lastSentRot;
    float lastSendAt = -10f, flightStart, slowSince = -1f;
    Vector3 lastSimPos;
    bool settleSent;
    readonly HashSet<ulong> touched = new HashSet<ulong>(); // players already caught/bonked by this flight

    // ---- pass lob
    bool lobbing;
    float lobT, lobDur;
    Vector3 lobFrom;

    bool hidden;
    float nextReconcile;

    public void Init(HandsNet owner, uint id, CarryDesc desc, Carryable item)
    {
        net = owner;
        Id = id;
        Desc = desc;
        Item = item;
        item.NetId = id;
        St.id = id;
        St.rot = Quaternion.identity;
    }

    public void MarkRevealed(ulong finderClientId)
    {
        var desc = Desc;
        desc.revealed = true;
        desc.finderClientId = finderClientId;
        Desc = desc;
    }

    /// <summary>This machine simulates it right now (posing, physics, detection).</summary>
    public bool LocalSim
    {
        get
        {
            if (net == null) return true;
            switch (St.Phase)
            {
                case HandsPhase.Rest: return net.IsServer;
                case HandsPhase.Held:
                case HandsPhase.Flight: return St.holder == net.LocalId;
                default: return false;
            }
        }
    }

    public bool HeldBy(ulong client) => St.Phase == HandsPhase.Held && St.holder == client;

    // ======================================================================== state

    /// <summary>Apply the host's word (also the host's own transitions). Never yanks an item this machine is flying.</summary>
    public void ApplyState(CarryNetState s)
    {
        var prev = St;
        if (s.Phase != HandsPhase.Tug) ResetGripVisual();
        St = s;
        HostSt = s;
        if (Item == null) return;
        ulong me = net.LocalId;
        // A revert: something this machine threw (it flies it) is pulled back into our hands or into a tug (§4.6 gate: never).
        if (prev.Phase == HandsPhase.Flight && prev.holder == me && Item.State != CarryState.Consumed &&
            (s.Phase == HandsPhase.Tug || (s.Phase == HandsPhase.Held && s.holder == me && !s.Has(CarryNetState.FlagPass) && !s.Has(CarryNetState.FlagCaught))))
        {
            Reverts++;
            NetAudit.Event("revert", new System.Collections.Generic.Dictionary<string, object>
            {
                { "id", Id }, { "phase", s.Phase.ToString() }, { "holder", s.holder == HandsNet.None ? -1 : (long)s.holder }, { "seq", s.seq },
            });
        }
        lobbing = lobbing && s.Phase == HandsPhase.Held && s.holder == prev.holder;
        switch (s.Phase)
        {
            case HandsPhase.Rest:
                PendingGrab = false;
                Detach();
                if (net.IsServer)
                {
                    Item.NetRemote = false;
                    if (Item.Body != null && Item.Body.isKinematic)
                    {
                        Place(s.pos, s.rot);
                        Item.NetSetState(CarryState.Resting);
                        Item.NetSetKinematic(false);
                        Item.Body.linearVelocity = Vector3.zero;
                        Item.Body.angularVelocity = Vector3.zero;
                    }
                }
                else
                {
                    Item.NetRemote = true;
                    Item.NetSetState(CarryState.Resting);
                    Item.NetSetKinematic(true);
                    snaps.Clear();
                    restPos = s.pos;
                    restRot = s.rot;
                    easeToRest = true;
                }
                Show(true);
                break;

            case HandsPhase.Held:
            {
                easeToRest = false;
                snaps.Clear();
                // Someone else's heave while ours is still waiting for the window: keep lifting ours until the host decides.
                if (s.holder != me && PendingGrab && s.Has(CarryNetState.FlagHeave)) break;
                PendingGrab = s.holder == me && s.Has(CarryNetState.FlagHeave);
                Item.NetRemote = s.holder != me;
                var carrier = net.CarrierOf(s.holder);
                if (carrier != null && Item.Holder == carrier) break; // already there (our own prediction)
                bool pass = s.Has(CarryNetState.FlagPass) && carrier != null;
                if (pass && lobbing) break;
                Detach();
                Show(true);
                if (pass) StartLob();
                else if (!Attach(carrier))
                {
                    Item.NetSetState(CarryState.Thrown);
                    Item.NetSetKinematic(true, false);
                    Place(s.pos, s.rot);
                }
                if (s.Has(CarryNetState.FlagYoink) && prev.holder == me && s.holder != me)
                    GameHUD.FloatingText(Item.WorldCenter + Vector3.up * 0.6f, "YOINK!", new Color(1f, 0.4f, 0.3f), 1.2f);
                break;
            }

            case HandsPhase.Flight:
                PendingGrab = false;
                easeToRest = false;
                settleSent = false;
                touched.Clear();
                flightStart = Time.time;
                slowSince = -1f;
                if (s.holder == me)
                {
                    Item.NetRemote = false;
                    bool already = prev.Phase == HandsPhase.Flight && prev.holder == me; // our own release / take: keep going
                    if (!already && (Item.Holder != null || (Item.Body != null && Item.Body.isKinematic && !Item.IsInScriptedFlight)))
                    {
                        // The host handed us the simulation (a bounce out of a receiver, a lost connection): fly from its pose.
                        Detach();
                        Place(s.pos, s.rot);
                        Item.NetSetState(CarryState.Thrown);
                        Item.NetSetKinematic(false);
                        Item.Body.linearVelocity = s.vel;
                    }
                    if (!PendingGone) Show(true);
                }
                else
                {
                    Detach();
                    Item.NetRemote = true;
                    Item.NetSetState(CarryState.Thrown);
                    Item.NetSetKinematic(true);
                    snaps.Clear();
                    snaps.Add(new Snap { t = Time.time, p = s.pos, r = s.rot });
                    if (!PendingGone) Show(true);
                }
                break;

            case HandsPhase.Tug:
                PendingGrab = false;
                easeToRest = false;
                lobbing = false;
                Detach();
                Item.NetRemote = true;
                Item.NetSetState(CarryState.Thrown);
                Item.NetSetKinematic(true, false);
                Show(true);
                break;

            case HandsPhase.Gone:
                Vanish(s);
                break;
        }
    }

    /// <summary>The local player's own release (drop, throw, toss): predict the flight until the host confirms.</summary>
    public void PredictLocalFlight()
    {
        St.phase = (byte)HandsPhase.Flight;
        St.holder = net.LocalId;
        PendingGrab = false;
        Item.NetRemote = false;
        settleSent = false;
        touched.Clear();
        flightStart = Time.time;
        slowSince = -1f;
    }

    /// <summary>The local player grabbed it: predict Held until the host answers.</summary>
    public void PredictLocalGrab()
    {
        PendingGrab = true;
        St.phase = (byte)HandsPhase.Held;
        St.holder = net.LocalId;
        Item.NetRemote = false;
        easeToRest = false;
        snaps.Clear();
    }

    /// <summary>A pass we threw: start the lob at once (the host confirms; a refusal brings it back).</summary>
    public void PredictPass(ulong target)
    {
        St.phase = (byte)HandsPhase.Held;
        St.holder = target;
        St.flags = CarryNetState.FlagPass;
        Item.NetRemote = true;
        Detach();
        StartLob();
    }

    /// <summary>Hide a guest's copy while the host decides (a receiver took it, or it burst).</summary>
    public void HideForHost()
    {
        PendingGone = true;
        Detach();
        Item.NetSetKinematic(true, false);
        Show(false);
    }

    /// <summary>The host said no (a receiver refused it): show it again; the host's Flight state moves it.</summary>
    public void Unhide()
    {
        PendingGone = false;
        Show(true);
    }

    void Vanish(CarryNetState s)
    {
        if (Item == null) return;
        Vector3 at = PendingGone ? s.pos : Item.WorldCenter;
        bool burst = s.Has(CarryNetState.FlagBurst);
        if (burst && !PendingGone)
        {
            Sfx.Play("bag_burst", at, 1f, 1f);
            Sfx.Play("splat", at, 0.8f, 1f);
            Fx.Burst(at, new Color(0.06f, 0.06f, 0.07f), 30, 6f);
            Fx.Burst(at, new Color(0.38f, 0.30f, 0.12f), 12, 4f);
            Fx.Dust(at, 1.3f);
            GameHUD.FloatingText(at + Vector3.up * 0.6f, s.Has(CarryNetState.FlagRip) ? "RIIIP!" : "SPLORCH!", new Color(0.6f, 0.9f, 0.2f), 1.3f);
        }
        net.Forget(this);
        net.applying++;
        try
        {
            Detach();
            if (Item.State != CarryState.Consumed) Item.MarkConsumed();
        }
        finally { net.applying--; }
        if (burst || PendingGone) Destroy(Item.gameObject);
        else Juice.ShrinkAndDestroy(Item.gameObject, 0.18f);
    }

    // ======================================================================== carriers

    bool Attach(TrashCarrier c)
    {
        if (c == null || Item == null) return false;
        if (c.Held != null && c.Held != Item)
        {
            // That carrier's copy still holds something else (state still arriving): try again shortly.
            return false;
        }
        net.applying++;
        try { return c.PickUp(Item); }
        finally { net.applying--; }
    }

    void Detach()
    {
        if (Item == null) return;
        var h = Item.Holder;
        if (h == null) return;
        net.applying++;
        try { h.ReleaseToScript(); }
        finally { net.applying--; }
    }

    void StartLob()
    {
        lobbing = true;
        lobT = 0f;
        lobFrom = Item.transform.position;
        var target = net.CarrierOf(St.holder);
        float d = target != null ? Vector3.Distance(lobFrom, target.transform.position) : 4f;
        lobDur = Mathf.Lerp(0.35f, 0.6f, Mathf.InverseLerp(3f, 8f, d));
        Item.NetSetState(CarryState.Thrown);
        Item.NetSetKinematic(true, false);
        Sfx.Play("whoosh", lobFrom, 0.7f, 1.1f);
    }

    void Place(Vector3 p, Quaternion r)
    {
        var t = Item.transform;
        t.SetPositionAndRotation(p, r);
        if (Item.Body != null) { Item.Body.position = p; Item.Body.rotation = r; }
    }

    void Show(bool on)
    {
        if (hidden == !on || Item == null) return;
        hidden = !on;
        foreach (var r in Item.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
    }

    // ======================================================================== per frame

    void Update()
    {
        if (net == null || Item == null) return;
        float now = Time.time;
        if (St.Phase == HandsPhase.Held && !lobbing && now >= nextReconcile)
        {
            nextReconcile = now + 0.25f;
            var c = net.CarrierOf(St.holder);
            if (c != null && Item.Holder != c && (St.holder != net.LocalId || !PendingGrab))
            {
                // A copy that drifted from the host's word (a remote carrier dropped it on its own): put it back in their hands.
                if (St.holder != net.LocalId || Item.Holder == null) { Detach(); Attach(c); }
            }
        }
        if (lobbing) StepLob(Time.deltaTime);
        if (LocalSim) StepSim(now);
    }

    void LateUpdate()
    {
        if (net == null || Item == null || lobbing || PendingGone) return;
        if (St.Phase == HandsPhase.Tug) { PoseTug(); return; }
        if (LocalSim || St.Phase == HandsPhase.Held) return;
        Follow(Time.time);
    }

    void StepLob(float dt)
    {
        var target = net.CarrierOf(St.holder);
        lobT += dt / Mathf.Max(0.1f, lobDur);
        Vector3 end = target != null ? target.transform.position + target.transform.rotation * target.holdLocalPosition : lobFrom;
        float k = Mathf.Clamp01(lobT);
        Vector3 p = Vector3.Lerp(lobFrom, end, k);
        p.y += Mathf.Sin(k * Mathf.PI) * (0.6f + 0.1f * Vector3.Distance(lobFrom, end));
        Item.transform.position = p - Item.transform.rotation * Vector3.Scale(Item.LocalBounds().center, Item.transform.lossyScale);
        Item.transform.rotation = Quaternion.AngleAxis(360f * dt, Vector3.right) * Item.transform.rotation;
        if (k < 1f) return;
        lobbing = false;
        if (target != null)
        {
            Attach(target);
            Sfx.Play("pickup", end, 0.7f, 1.1f);
            if (target.IsLocalOwner) GameHUD.FloatingText(end + Vector3.up * 0.7f, "Got it!", new Color(0.5f, 1f, 0.5f), 0.9f);
        }
    }

    void Follow(float now)
    {
        if (snaps.Count == 0)
        {
            if (!easeToRest) return;
            var t = Item.transform;
            float k = 1f - Mathf.Exp(-14f * Time.deltaTime);
            Vector3 p = Vector3.Lerp(t.position, restPos, k);
            Quaternion r = Quaternion.Slerp(t.rotation, restRot, k);
            Place(p, r);
            if ((p - restPos).sqrMagnitude < 1e-6f) easeToRest = false;
            return;
        }
        float rt = now - InterpDelay;
        int n = snaps.Count;
        if (rt <= snaps[0].t) { Place(snaps[0].p, snaps[0].r); return; }
        for (int i = 0; i < n - 1; i++)
        {
            var a = snaps[i];
            var b = snaps[i + 1];
            if (rt > b.t) continue;
            float u = Mathf.InverseLerp(a.t, b.t, rt);
            Place(Vector3.Lerp(a.p, b.p, u), Quaternion.Slerp(a.r, b.r, u));
            return;
        }
        Place(snaps[n - 1].p, snaps[n - 1].r);
        if (n > 6) snaps.RemoveRange(0, n - 6);
    }

    /// <summary>A pose from the simulating machine (host relays a guest's; the host streams its own).</summary>
    public void OnPose(uint seq, Vector3 p, Quaternion r)
    {
        if (LocalSim || seq != St.seq) return;
        if (St.Phase != HandsPhase.Flight && St.Phase != HandsPhase.Rest) return;
        float t = Time.time;
        if (snaps.Count > 0 && t <= snaps[snaps.Count - 1].t) t = snaps[snaps.Count - 1].t + 0.001f;
        snaps.Add(new Snap { t = t, p = p, r = r });
        if (St.Phase == HandsPhase.Rest) { restPos = p; restRot = r; easeToRest = true; }
        if (snaps.Count > 12) snaps.RemoveRange(0, snaps.Count - 12);
    }

    Transform gripVisual;
    Vector3 gripRestScale;
    bool gripScaled;

    void ResetGripVisual()
    {
        if (gripScaled && gripVisual != null) gripVisual.localScale = gripRestScale;
        gripScaled = false;
        gripVisual = null;
    }

    void PoseTug()
    {
        var t = net.TugForItem(Id);
        var a = PlayerBody.For(St.holder);
        var b = PlayerBody.For(St.other);
        if (a == null || b == null) return;
        float meter = t.HasValue ? t.Value.meter : 0f;
        // Anchor to the players' shoulders, not their facing: either may turn to walk backwards while carrying.
        Vector3 pa = a.transform.position + Vector3.up * 1.05f;
        Vector3 pb = b.transform.position + Vector3.up * 1.05f;
        Vector3 mid = Vector3.Lerp(pa, pb, 0.5f + 0.5f * Mathf.Clamp(meter, -1f, 1f) * 0.25f);
        double now = net.ServerNow;
        float strain = t.HasValue ? Mathf.Clamp01((t.Value.strainH + t.Value.strainC) / Mathf.Max(1f, t.Value.ripAt)) : 0f;
        float wob = Mathf.Sin((float)now * Mathf.Lerp(7f, 24f, strain)) * (0.01f + strain * 0.025f);
        Vector3 direction = pb - pa; direction.y = 0f;
        Vector3 across = Vector3.Cross(Vector3.up, direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.right);
        Vector3 c = mid + across * wob + Vector3.up * Mathf.Abs(wob);
        var rot = Quaternion.LookRotation(across, Vector3.up) * Quaternion.Euler(0f, 0f, wob * 160f);
        Item.transform.SetPositionAndRotation(c - rot * Vector3.Scale(Item.LocalBounds().center, Item.transform.lossyScale), rot);
        if (!gripScaled && Item.JuiceTarget != Item.transform)
        {
            gripVisual = Item.JuiceTarget;
            if (gripVisual != null) { gripRestScale = gripVisual.localScale; gripScaled = true; }
        }
        if (gripVisual != null) gripVisual.localScale = Vector3.Scale(gripRestScale,
            new Vector3(1f + 0.4f * strain, 1f - 0.12f * strain, 1f - 0.12f * strain));
    }

    // ======================================================================== simulating machine

    void StepSim(float now)
    {
        if (Item.Holder != null || Item.State == CarryState.Consumed || PendingGone) return;
        var t = Item.transform;
        if (now - lastSendAt >= 0.05f &&
            ((t.position - lastSentPos).sqrMagnitude > 0.0001f || Quaternion.Angle(t.rotation, lastSentRot) > 2f))
        {
            lastSendAt = now;
            lastSentPos = t.position;
            lastSentRot = t.rotation;
            net.SendPose(this, t.position, t.rotation);
        }
        if (St.Phase != HandsPhase.Flight || settleSent) return;
        if (Item.IsInScriptedFlight) return;
        var rb = Item.Body;
        bool moved = (t.position - lastSimPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f) > 0.3f; // scripted (kinematic) motion too
        lastSimPos = t.position;
        bool slow = !moved && (rb == null || rb.isKinematic || (rb.linearVelocity.sqrMagnitude < 0.05f && rb.angularVelocity.sqrMagnitude < 0.6f));
        if (slow) { if (slowSince < 0f) slowSince = now; }
        else slowSince = -1f;
        if ((slowSince >= 0f && now - slowSince > 0.35f) || Item.State == CarryState.Resting || now - flightStart > 7f)
        {
            settleSent = true;
            if (rb != null && !rb.isKinematic && now - flightStart > 7f) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            net.SendSettle(this, t.position, t.rotation);
        }
    }

    void FixedUpdate()
    {
        if (net == null || Item == null || St.Phase != HandsPhase.Flight || !LocalSim || Item.Holder != null || PendingGone) return;
        if (Item.State != CarryState.Thrown || Item.IsInScriptedFlight) return;
        var rb = Item.Body;
        if (rb == null || rb.isKinematic) return;
        float speed = rb.linearVelocity.magnitude;
        if (speed < 1.5f) return;
        Vector3 c = Item.WorldCenter;
        foreach (var pb in PlayerBody.All)
        {
            if (pb == null || pb.ClientId == net.LocalId || touched.Contains(pb.ClientId)) continue;
            Vector3 chest = pb.transform.position + Vector3.up * 1.1f;
            Vector3 d = c - chest;
            if (d.sqrMagnitude > 1.1f * 1.1f) continue;
            if (Mathf.Abs(d.y) > 1.1f) continue;
            touched.Add(pb.ClientId);
            Vector3 flat = new Vector3(-d.x, 0f, -d.z);
            Vector3 toItem = new Vector3(d.x, 0f, d.z);
            bool facing = toItem.sqrMagnitude < 0.01f || Vector3.Angle(pb.transform.forward, toItem) <= HandsTuning.CatchFacing;
            if (d.magnitude <= HandsTuning.CatchRadius + 0.2f && facing && pb.CanCatch && !net.IsHolding(pb.ClientId))
            {
                net.ClaimCatch(this, pb.ClientId);
                return;
            }
            if (speed >= HandsTuning.BoopSpeed) net.ClaimBonk(this, pb.ClientId, speed, flat);
            else
            {
                Sfx.Play("boop", chest, 0.6f, 1.2f);
                GameHUD.FloatingText(chest + Vector3.up * 0.8f, "boop", new Color(1f, 0.9f, 0.6f), 0.7f);
            }
        }
    }

    void OnDestroy()
    {
        if (net != null) net.OnCarryDestroyed(this);
    }
}

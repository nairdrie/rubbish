using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The look and sound of a bag's tell (F3, Docs/Multiplayer.md §14.3), added by <see cref="TrashBag.Spawn"/> to every bag with a
/// tell, on every machine (the tell is a hash of the bag id, so every copy agrees):
/// <list type="bullet">
/// <item><b>Leaky:</b> a wet greenish sheen and drips (faster while carried or flying); the authority drops juice puddles along
/// its path (<see cref="TellsRuntime"/> reads <see cref="Leaky"/>).</item>
/// <item><b>Heavy:</b> a comically oversized version of the original tied trash sack; a <see cref="HeavyLiftHandle"/> lets a second player
/// take the other end while someone holds it.</item>
/// <item><b>Over-stuffed:</b> bulging, with junk poking out of the top, and it creaks and strains now and then.</item>
/// <item><b>Mystery:</b> wobbles in fits, ticks and buzzes (only when someone's near enough to hear).</item>
/// </list>
/// Cheap: one Update per bag with a tell, timers and a camera distance check before anything else.
/// </summary>
[DisallowMultipleComponent]
public class BagTellFx : MonoBehaviour
{
    static readonly List<BagTellFx> leaky = new List<BagTellFx>();
    /// <summary>Every live leaky bag (the puddle authority walks it).</summary>
    public static IReadOnlyList<BagTellFx> Leaky => leaky;

    public TrashBag Bag { get; private set; }
    public BagTell Tell { get; private set; }

    // puddle trail bookkeeping (authority only)
    internal Vector3 lastPuddleAt;
    internal bool hasPuddle;
    internal float lastPuddleTime = -999f;
    internal int puddleCount;

    Transform visual, model;
    float nextFx, wobbleUntil, nextTick, nextCrowd;

    /// <summary>Focused acceptance probes: actual local cue and nearby pedestrian response counts.</summary>
    public int BuzzCount { get; private set; }
    public int TickCount { get; private set; }
    public int CrowdDrawn { get; private set; }
    Quaternion visualRot0;
    static readonly Color JuiceColor = new Color(0.42f, 0.46f, 0.14f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => leaky.Clear();

    /// <summary>Give this bag its tell's look (no-op for plain bags). Returns the component, or null.</summary>
    public static BagTellFx Attach(TrashBag bag)
    {
        if (bag == null) return null;
        var t = BagTells.Of(bag);
        if (t == BagTell.None) return null;
        var fx = bag.GetComponent<BagTellFx>() ?? bag.gameObject.AddComponent<BagTellFx>();
        fx.Init(bag, t);
        return fx;
    }

    void Init(TrashBag bag, BagTell t)
    {
        if (Bag == bag && Tell == t) return; // Reattaching presentation must not keep enlarging the sack.
        Bag = bag;
        Tell = t;
        visual = bag.transform.Find("Visual");
        model = visual != null ? visual.Find("Model") : null;
        if (visual != null) visualRot0 = visual.localRotation;
        nextFx = Time.time + ShopModKit.Range(0.2f, 1.5f);
        switch (t)
        {
            case BagTell.Leaky: Tint(new Color(0.10f, 0.13f, 0.04f), 0.95f); break;
            case BagTell.Heavy: BuildHeavy(); break;
            case BagTell.OverStuffed: BuildOverStuffed(); break;
            case BagTell.Mystery: Tint(new Color(0.12f, 0.06f, 0.16f), 0.75f); break;
        }
        if (t == BagTell.Heavy && GetComponent<HeavyLiftHandle>() == null) gameObject.AddComponent<HeavyLiftHandle>();
    }

    void OnEnable() { if (Tell == BagTell.Leaky && !leaky.Contains(this)) leaky.Add(this); }
    void OnDisable() => leaky.Remove(this);
    void Start() { if (Tell == BagTell.Leaky && isActiveAndEnabled && !leaky.Contains(this)) leaky.Add(this); }

    // ==================================================================================== looks

    void Tint(Color c, float smooth)
    {
        if (model == null) return;
        var mpb = new MaterialPropertyBlock();
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            mpb.SetFloat("_Smoothness", smooth);
            r.SetPropertyBlock(mpb);
        }
    }

    void BuildHeavy()
    {
        EnlargeHeavySack(model);
    }

    /// <summary>
    /// Heavy bags keep the authored sack, knot and creases: size alone is their visual tell. Only the model changes, not
    /// the carryable's physics, network scale or shared-grip reach. Also used by resident-carried cosmetic sacks.
    /// </summary>
    internal static void EnlargeHeavySack(Transform model)
    {
        if (model == null) return;
        var before = LocalBoundsOf(model, model.parent);
        model.localScale *= 1.65f;
        var after = LocalBoundsOf(model, model.parent);
        // Imported props need not have a bottom-centred pivot. Preserve the original resting point instead of
        // expanding through the floor or shifting sideways relative to the unchanged physical bag.
        model.localPosition += new Vector3(before.center.x - after.center.x, before.min.y - after.min.y,
            before.center.z - after.center.z);
    }

    void BuildOverStuffed()
    {
        if (model == null) return;
        model.localScale = Vector3.Scale(model.localScale, new Vector3(1.14f, 1.12f, 1.14f));
        var b = LocalBoundsOf(model.parent);
        var root = new GameObject("PokingOut").transform;
        root.SetParent(model.parent, false);
        uint h = (uint)PickupVisuals.StableHash(Bag.Contents != null ? Bag.Contents.bagId : "x");
        var box = ShopModKit.Lit("mat/f3_poke_box", new Color(0.78f, 0.6f, 0.36f), 0.2f);
        var bottle = ShopModKit.Lit("mat/f3_poke_bottle", new Color(0.45f, 0.75f, 1f), 0.8f);
        var peel = ShopModKit.Lit("mat/f3_poke_peel", new Color(0.95f, 0.85f, 0.2f), 0.4f);
        float top = b.max.y;
        ShopModKit.Prim(PrimitiveType.Cube, "BoxCorner", root, box, new Vector3(b.center.x + 0.08f, top - 0.04f, b.center.z), new Vector3(0.22f, 0.2f, 0.16f), new Vector3(15f, (h % 90u), 25f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Bottle", root, bottle, new Vector3(b.center.x - 0.1f, top, b.center.z + 0.05f), new Vector3(0.07f, 0.14f, 0.07f), new Vector3(-30f, 0f, 20f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Peel", root, peel, new Vector3(b.center.x + b.extents.x * 0.85f, b.center.y + 0.1f, b.center.z), new Vector3(0.08f, 0.16f, 0.06f), new Vector3(0f, 0f, -40f));
        foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static Bounds LocalBoundsOf(Transform t) => LocalBoundsOf(t, t);

    static Bounds LocalBoundsOf(Transform t, Transform relativeTo)
    {
        var inv = relativeTo != null ? relativeTo.worldToLocalMatrix : Matrix4x4.identity;
        bool any = false;
        var b = new Bounds(Vector3.up * 0.35f, new Vector3(0.6f, 0.7f, 0.6f));
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var m = inv * r.transform.localToWorldMatrix;
            var mb = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = m.MultiplyPoint3x4(c);
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
            }
        }
        return b;
    }

    // ==================================================================================== per frame

    void Update()
    {
        if (Bag == null || Bag.State == CarryState.Consumed) return;
        float now = Time.time;
        if (Tell == BagTell.Mystery)
        {
            MysteryWobble(now);
            if (now >= nextCrowd)
            {
                nextCrowd = now + 0.4f;
                DrawCuriousPedestrians();
            }
        }
        if (now < nextFx) return;
        var cam = Camera.main;
        Vector3 pos = Bag.WorldCenter;
        float d2 = cam != null ? (cam.transform.position - pos).sqrMagnitude : 0f;
        bool moving = Bag.State != CarryState.Resting;
        switch (Tell)
        {
            case BagTell.Leaky:
                nextFx = now + (moving ? ShopModKit.Range(0.18f, 0.32f) : ShopModKit.Range(0.9f, 1.6f));
                if (d2 > 40f * 40f) break;
                Fx.Burst(pos - Vector3.up * 0.25f, JuiceColor, moving ? 3 : 1, 0.4f);
                if (d2 < 8f * 8f && moving) Sfx.Play("pop", pos, 0.06f, ShopModKit.Range(2.2f, 2.8f));
                break;
            case BagTell.OverStuffed:
                nextFx = now + ShopModKit.Range(3f, 6f) * (moving ? 0.5f : 1f);
                if (d2 > 25f * 25f) break;
                Juice.Squash(Bag.JuiceTarget, 0.08f, 0.3f);
                if (d2 < 10f * 10f) Sfx.Play("squeak", pos, 0.12f, ShopModKit.Range(0.45f, 0.6f));
                break;
            case BagTell.Heavy:
                nextFx = now + 2f;
                break;
            case BagTell.Mystery:
                nextFx = now + ShopModKit.Range(2.2f, 4.5f);
                if (d2 > 22f * 22f) break;
                wobbleUntil = now + 0.7f;
                nextTick = now;
                if (d2 < 12f * 12f)
                {
                    Sfx.Play("buzz", pos, 0.12f, ShopModKit.Range(1.6f, 2.1f));
                    BuzzCount++;
                }
                break;
        }
    }

    void MysteryWobble(float now)
    {
        if (visual == null) return;
        if (now < wobbleUntil)
        {
            float t = wobbleUntil - now;
            float a = Mathf.Sin(now * 55f) * 9f * Mathf.Clamp01(t / 0.7f);
            visual.localRotation = visualRot0 * Quaternion.Euler(a * 0.6f, 0f, a);
            if (now >= nextTick)
            {
                nextTick = now + 0.17f;
                var cam = Camera.main;
                if (cam != null && (cam.transform.position - Bag.WorldCenter).sqrMagnitude < 12f * 12f)
                {
                    Sfx.Play("click", Bag.WorldCenter, 0.22f, ShopModKit.Range(1.7f, 2.0f));
                    TickCount++;
                }
            }
        }
        else if (visual.localRotation != visualRot0) visual.localRotation = visualRot0;
    }

    void DrawCuriousPedestrians()
    {
        CrowdDrawn = 0;
        if (Bag == null || Bag.State != CarryState.Resting) return;
        Vector3 at = Bag.WorldCenter;
        if (!Weather.IsOutdoors(at)) return;
        var dir = ResidentDirector.Instance;
        if (dir == null) return;
        var agents = dir.Agents;
        // Presentation only. We borrow at most three already-out pedestrians, never spawn new residents or alter schedules.
        for (int i = 0; i < agents.Count && CrowdDrawn < 3; i++)
        {
            var a = agents[i];
            if (a == null || a.owner != AgentOwner.External || a.reqPose == PersonPose.Inside || a.reqPose == PersonPose.Segway || a.Interrupted) continue;
            Vector3 d = a.reqPos - at; d.y = 0f;
            if (d.sqrMagnitude > 6f * 6f) continue;
            a.CuriousAbout(at, 1.0f, CrowdDrawn);
            if (a.Curious) CrowdDrawn++;
        }
    }
}

/// <summary>
/// "Help lift!" on a heavy bag somebody else is holding (co-op): an <see cref="InteractionZone"/> that follows the bag (unparented,
/// so it never tumbles with it) and asks the host for the other end (<see cref="BagTellsNet.RequestHelp"/>).
/// </summary>
[DisallowMultipleComponent]
public class HeavyLiftHandle : MonoBehaviour, IInteractable
{
    TrashBag bag;
    Transform zoneT;
    InteractionZone zone;

    void Awake() => bag = GetComponent<TrashBag>();

    void OnDestroy()
    {
        if (zoneT != null) Destroy(zoneT.gameObject);
    }

    void Update()
    {
        if (bag == null) return;
        bool want = bag.NetId != 0 && bag.State == CarryState.Carried && Net != null;
        if (want && zoneT == null)
        {
            zoneT = new GameObject("F3 LiftZone").transform;
            zone = zoneT.gameObject.AddComponent<InteractionZone>();
            zone.center = new Vector3(0f, 0.2f, 0f);
            zone.size = new Vector3(3.6f, 3f, 3.6f);
            zone.mode = ZoneMode.OnFoot;
            zone.score = 2.6f;
            zone.targetOverride = this;
        }
        if (zoneT == null) return;
        if (zoneT.gameObject.activeSelf != want) zoneT.gameObject.SetActive(want);
        if (want) zoneT.SetPositionAndRotation(bag.WorldCenter, Quaternion.identity);
    }

    static BagTellsNet Net => BagTellsNet.Instance != null && BagTellsNet.Instance.IsSpawned && HandsNet.Instance != null && HandsNet.Instance.Active
        ? BagTellsNet.Instance : null;

    public bool CanInteract()
    {
        var net = Net;
        if (net == null || bag == null || bag.NetId == 0) return false;
        var nc = HandsNet.Instance.Get(bag.NetId);
        if (nc == null || nc.St.Phase != HandsPhase.Held || nc.St.holder == net.LocalId) return false;
        var me = TrashCarrier.Local;
        if (me == null || me.IsCarrying) return false;
        if (HandsNet.Instance.IsHolding(net.LocalId)) return false;
        if (net.TryLift(bag.NetId, out _) || net.TryHelping(net.LocalId, out _)) return false;
        var pb = PlayerBody.For(net.LocalId);
        return (pb == null || pb.State == BodyState.Normal) && !CompactorCube.IsCubed(net.LocalId);
    }

    public string GetInteractionPrompt() => "Grab the other side of the HEAVY bag";

    public void OnInteract(GameObject interactor)
    {
        if (!CanInteract()) return;
        if (!Permissions.LocalCanWorkAt(bag.transform.position)) { Permissions.Deny(bag.transform.position); return; }
        Net.RequestHelp(bag.NetId);
        Sfx.Play("pickup", bag.WorldCenter, 0.6f, 0.7f);
    }

    public void Highlight(bool highlight) { }
}

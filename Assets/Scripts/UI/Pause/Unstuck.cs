using System;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The Esc menu's Unstuck (Docs/UIPass.md §5.5). Owner authority only: it moves this machine's own player, or asks the truck the
/// local player drives for the existing tow drone. Never moves anyone else.
/// <list type="bullet">
/// <item><b>On foot:</b> drop what you hold, then teleport to the best safe point: (a) the newest <b>safe-position ring</b>
/// sample more than 3 m away (sampled every 2 s while grounded on layer Ground and not overlapping a wall or a static prop);
/// else (b) the nearest sidewalk point (the town's <c>Sidewalk_*</c> slabs); else (c) the scene's PlayerSpawner. Toast
/// "Unstuck!".</item>
/// <item><b>Driving:</b> <see cref="TruckRecovery.TryStartTow"/> (lift, right and set down on the nearest road; −$25 through
/// the Ledger, free with handling L1+).</item>
/// <item><b>Carried by a friend or flopped</b> (M2): disabled with "Wriggle free first!" through <see cref="BlockedReason"/>.</item>
/// </list>
/// Cooldown 10 s (real time). Every use is logged with <c>NetAudit.Event("unstuck", …)</c>.
/// </summary>
public static class Unstuck
{
    public const float CooldownSeconds = 10f;
    public const float SampleEverySeconds = 2f;
    public const float MinSampleDistance = 3f;
    public const int RingSize = 8;
    const float SidewalkSearchRadius = 80f;

    const int GroundMask = 1 << 6;
    /// <summary>What counts as "stuck in something": Walls (7) and static Default props (houses, hedges, lamps).</summary>
    const int SolidMask = (1 << 7) | (1 << 0);

    static readonly Vector3[] ring = new Vector3[RingSize];
    static readonly float[] ringTime = new float[RingSize];
    static int ringCount, ringNext;
    static float lastUse = -999f, nextSample;
    static int ringScene = -1;
    static readonly Collider[] overlap = new Collider[16];
    static readonly Collider[] nearby = new Collider[128];
    static Runner runner;

    /// <summary>M2 hook: a reason Unstuck is unavailable right now ("Wriggle free first!" while carried or flopped), or null.</summary>
    public static Func<string> BlockedReason;

    /// <summary>Seconds until Unstuck can be used again (0 = ready).</summary>
    public static float CooldownLeft => Mathf.Max(0f, lastUse + CooldownSeconds - Time.realtimeSinceStartup);

    /// <summary>Safe samples in the ring right now (tests).</summary>
    public static int RingCount => ringCount;

    /// <summary>What the last use did ("ring 6.2 m", "sidewalk", "spawn", "tow", "failed: …"), for tests and logs.</summary>
    public static string LastResult { get; private set; }

    /// <summary>The reason the button is disabled right now, or null when it can be pressed.</summary>
    public static string Unavailable()
    {
        var me = PauseService.Me;
        if (me == null) return "Not in a game.";
        if (BlockedReason != null)
        {
            string r = null;
            try { r = BlockedReason(); }
            catch (Exception e) { Debug.LogException(e); }
            if (!string.IsNullOrEmpty(r)) return r;
        }
        if (CooldownLeft > 0f) return Mathf.CeilToInt(CooldownLeft).ToString();
        return null;
    }

    /// <summary>
    /// Unstick this machine's player now. Returns false (and a readable <paramref name="whyNot"/>) when nothing happened.
    /// The pause menu resumes first, so the tow (which runs on scaled time) can fly.
    /// </summary>
    public static bool Use(out string whyNot)
    {
        whyNot = Unavailable();
        if (whyNot != null) { LastResult = "refused: " + whyNot; return false; }
        var me = PauseService.Me;
        bool ok;
        if (me.IsDriving && me.currentTruck != null) ok = Tow(me, out whyNot);
        else ok = OnFoot(me, out whyNot);
        if (ok) lastUse = Time.realtimeSinceStartup;
        else LastResult = "failed: " + whyNot;
        return ok;
    }

    static bool Tow(PlayerController me, out string whyNot)
    {
        var rec = me.currentTruck.GetComponent<TruckRecovery>();
        if (rec == null) { whyNot = "The tow service can't reach this truck."; return false; }
        if (!rec.TryStartTow(true, out whyNot)) return false;
        LastResult = "tow";
        NetAudit.Event("unstuck", "tow from menu");
        return true;
    }

    static bool OnFoot(PlayerController me, out string whyNot)
    {
        whyNot = null;
        var carrier = me.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying)
        {
            try { carrier.Drop(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        var cc = me.GetComponent<CharacterController>();
        Vector3 from = me.transform.position;
        string how;
        if (!TryRing(me, cc, from, out Vector3 to)) { if (TrySidewalk(me, cc, from, out to)) how = "sidewalk"; else if (TrySpawn(out to)) how = "spawn"; else how = null; }
        else how = $"ring {Vector3.Distance(from, to):0.0} m";
        if (how == null) { whyNot = UiText.Pause.UnstuckNowhere; return false; }

        Teleport(me, cc, to);
        LastResult = how;
        GameHUD.Toast(UiText.Pause.UnstuckToast, ToastStyle.Good, 1.6f);
        Sfx.Play("pop", null, 0.6f, 1.1f);
        Fx.Dust(to + Vector3.up * 0.1f, 0.8f);
        NetAudit.Event("unstuck", $"foot {how} from ({from.x:0.0},{from.y:0.0},{from.z:0.0}) to ({to.x:0.0},{to.y:0.0},{to.z:0.0})");
        // the old spot is (probably) a bad one: forget samples close to it
        ForgetNear(from, 1.5f);
        return true;
    }

    static void Teleport(PlayerController me, CharacterController cc, Vector3 to)
    {
        bool ccOn = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        var t = me.transform;
        t.position = to;
        Physics.SyncTransforms();
        if (cc != null) cc.enabled = ccOn;
        var nt = me.GetComponent<NetworkTransform>();
        if (nt != null && nt.IsSpawned && nt.CanCommitToTransform)
        {
            try { nt.Teleport(to, t.rotation, t.localScale); }
            catch (Exception e) { Debug.LogWarning("[Unstuck] NetworkTransform teleport: " + e.Message); }
        }
    }

    // ------------------------------------------------------------------ candidates

    static bool TryRing(PlayerController me, CharacterController cc, Vector3 from, out Vector3 to)
    {
        to = from;
        for (int k = 0; k < ringCount; k++)
        {
            int i = ((ringNext - 1 - k) % RingSize + RingSize) % RingSize;
            Vector3 p = ring[i];
            if ((p - from).sqrMagnitude < MinSampleDistance * MinSampleDistance) continue;
            if (!IsSafe(me, cc, p)) continue;
            to = p;
            return true;
        }
        return false;
    }

    static bool TrySidewalk(PlayerController me, CharacterController cc, Vector3 from, out Vector3 to)
    {
        to = from;
        var map = TownMapData.Load();
        if (map != null && map.layoutId == CoastalCityLayout.LayoutId)
        {
            // Coastal sidewalks are concave ribbon meshes. Collider.ClosestPoint does not support them, and
            // the middle of a curved ribbon's world AABB can be a lawn, building or river rather than a sidewalk.
            foreach (var candidate in CoastalCandidates(map, from))
                if (SnapToGround(candidate, out var grounded) && IsSafe(me, cc, grounded))
                { to = grounded; return true; }
            return false;
        }
        float best = float.MaxValue;
        bool found = false;
        int n = Physics.OverlapSphereNonAlloc(from, SidewalkSearchRadius, nearby, GroundMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = nearby[i];
            if (c == null || !c.name.StartsWith("Sidewalk", StringComparison.Ordinal)) continue;
            if (c is MeshCollider mesh && !mesh.convex) continue;
            Vector3 p = c.ClosestPoint(from);
            Vector3 inward = c.bounds.center - p; inward.y = 0f;
            if (inward.sqrMagnitude > 0.01f) p += inward.normalized * Mathf.Min(0.8f, inward.magnitude);
            if (!SnapToGround(p, out p) || !IsSafe(me, cc, p)) continue;
            float d = (p - from).sqrMagnitude;
            if (d < best) { best = d; to = p; found = true; }
        }
        return found;
    }

    /// <summary>Pure coastal recovery candidates, sidewalk first then road. Physics still verifies ground and
    /// player clearance before any teleport. Keeps the old 80m search limit and works when the player fell below town.</summary>
    public static Vector3[] CoastalCandidates(TownMapData map, Vector3 from)
    {
        if (map == null || map.layoutId != CoastalCityLayout.LayoutId) return Array.Empty<Vector3>();
        var candidates = new System.Collections.Generic.List<Vector3>(2);
        void Add(Vector3 point)
        {
            float dx = point.x - from.x, dz = point.z - from.z;
            if (dx * dx + dz * dz > SidewalkSearchRadius * SidewalkSearchRadius) return;
            // TownMap is XZ data and coastal streets are level. Raycast from the street datum, not from under it.
            point.y = 0f;
            foreach (var existing in candidates) if ((existing - point).sqrMagnitude < .01f) return;
            candidates.Add(point);
        }
        if (map.TryNearestArea(from, MapAreaKind.Sidewalk, out var sidewalk)) Add(sidewalk);
        if (map.TryNearestRoad(from, out var road, out _)) Add(road);
        return candidates.ToArray();
    }

    static bool TrySpawn(out Vector3 to)
    {
        to = default;
        GameObject[] tagged;
        try { tagged = GameObject.FindGameObjectsWithTag(FlowUtil.PlayerSpawnerTag); }
        catch (UnityException) { return false; }
        foreach (var g in tagged)
        {
            if (g == null || !g.activeInHierarchy) continue;
            to = g.transform.position;
            if (SnapToGround(to + Vector3.up * 0.5f, out var snapped)) to = snapped;
            return true;
        }
        return false;
    }

    static bool SnapToGround(Vector3 p, out Vector3 onGround)
    {
        onGround = p;
        if (!Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, GroundMask, QueryTriggerInteraction.Ignore)) return false;
        onGround = hit.point + Vector3.up * 0.02f;
        return true;
    }

    /// <summary>Standing at <paramref name="feet"/> overlaps no wall or static prop (the player's own capsule).</summary>
    public static bool IsSafe(PlayerController me, CharacterController cc, Vector3 feet)
    {
        float r = cc != null ? cc.radius : 0.28f, h = cc != null ? cc.height : 1.8f;
        Vector3 c = cc != null ? cc.center : new Vector3(0f, 0.9f, 0f);
        Vector3 a = feet + Vector3.up * (c.y - h * 0.5f + r + 0.05f);
        Vector3 b = feet + Vector3.up * (c.y + h * 0.5f - r);
        int n = Physics.OverlapCapsuleNonAlloc(a, b, r * 0.95f, overlap, SolidMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = overlap[i];
            if (col == null || col.attachedRigidbody != null) continue; // bags, trucks, critters move
            if (me != null && col.transform.IsChildOf(me.transform)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Grounded on layer Ground right now (a short ray from the feet).</summary>
    static bool GroundedOnGround(Vector3 feet) =>
        Physics.Raycast(feet + Vector3.up * 0.25f, Vector3.down, 0.6f, GroundMask, QueryTriggerInteraction.Ignore);

    // ------------------------------------------------------------------ the ring

    static void Sample()
    {
        var me = PauseService.Me;
        if (me == null || me.IsDriving || PauseService.Frozen) return;
        int scene = me.gameObject.scene.handle;
        if (scene != ringScene) { ringCount = ringNext = 0; ringScene = scene; }
        var cc = me.GetComponent<CharacterController>();
        if (cc == null || !cc.enabled) return;
        Vector3 p = me.transform.position;
        if (!GroundedOnGround(p) || !IsSafe(me, cc, p)) return;
        if (ringCount > 0)
        {
            int last = (ringNext - 1 + RingSize) % RingSize;
            if ((ring[last] - p).sqrMagnitude < 0.25f) { ringTime[last] = Time.realtimeSinceStartup; return; } // standing still
        }
        ring[ringNext] = p;
        ringTime[ringNext] = Time.realtimeSinceStartup;
        ringNext = (ringNext + 1) % RingSize;
        ringCount = Mathf.Min(RingSize, ringCount + 1);
    }

    static void ForgetNear(Vector3 p, float radius)
    {
        int kept = 0;
        var keep = new Vector3[RingSize];
        for (int k = ringCount - 1; k >= 0; k--)
        {
            int i = ((ringNext - 1 - k) % RingSize + RingSize) % RingSize; // oldest first
            if ((ring[i] - p).sqrMagnitude > radius * radius) keep[kept++] = ring[i];
        }
        ringCount = ringNext = 0;
        for (int i = 0; i < kept; i++) { ring[ringNext] = keep[i]; ringNext = (ringNext + 1) % RingSize; ringCount++; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode || runner != null || !Application.isPlaying) return;
        var go = new GameObject("[Unstuck]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
        SceneManager.activeSceneChanged += (a, b) => { ringCount = ringNext = 0; ringScene = -1; };
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        ringCount = ringNext = 0;
        ringScene = -1;
        lastUse = -999f;
        nextSample = 0f;
        runner = null;
        BlockedReason = null;
        LastResult = null;
    }

    sealed class Runner : MonoBehaviour
    {
        void Update()
        {
            if (Time.realtimeSinceStartup < nextSample) return;
            nextSample = Time.realtimeSinceStartup + SampleEverySeconds;
            try { Sample(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}

using System;
using System.Collections;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The Esc menu's Unstuck (Docs/UIPass.md §5.5). Owner authority only: it moves this machine's own player, or asks the
/// host (through <see cref="TruckCall"/>) to fly the player's truck over. Never moves anyone else.
/// <list type="bullet">
/// <item><b>On foot:</b> calls YOUR truck to where you are: the Scrapazon Prime Tow drone lifts it from wherever it is
/// (parked across town, flipped, wedged, in the river) and sets it down upright on the nearest clear, drivable ground beside
/// you, roads preferred (−$25 through the Ledger, free with handling L1+). You are only moved when you are really stuck
/// (<see cref="Embedded"/> in a wall or a prop, or <see cref="FellOutOfWorld"/>), or when there is nothing to call (no truck in
/// this scene, you are indoors): drop what you hold, then teleport to the best safe point, (a) the newest <b>safe-position
/// ring</b> sample more than 3 m away (sampled every 2 s while grounded on layer Ground and not overlapping a wall or a static
/// prop), else (b) the nearest sidewalk point (the town's <c>Sidewalk_*</c> slabs), else (c) the scene's PlayerSpawner, toast
/// "Unstuck!". When you were really stuck and a truck can be called, it then comes to the new spot.</item>
/// <item><b>Driving:</b> <see cref="TruckRecovery.TryStartTow"/> (lift, right and set down on the nearest road; same fee).</item>
/// <item><b>Rear step:</b> hop off first (<see cref="RideRider.Unhitch"/>), then the on-foot rule.</item>
/// <item><b>Disabled</b> with a reason on the button: carried or flopped (M2: "Wriggle free first!" through
/// <see cref="BlockedReason"/>), piloting or flung ("Land first!"), and a truck that exists but is out on a job or already on
/// its way (you are not stuck, so the old hop would be a surprise).</item>
/// </list>
/// Cooldown 10 s (real time); a call the host refuses gives it back. Every use is logged with <c>NetAudit.Event("unstuck", …)</c>.
/// </summary>
public static class Unstuck
{
    public const float CooldownSeconds = 10f;
    public const float SampleEverySeconds = 2f;
    public const float MinSampleDistance = 3f;
    public const int RingSize = 8;
    const float SidewalkSearchRadius = 80f;
    /// <summary>Below this (and not in a shop's or the casino's pocket, which sit 20 m under the town on purpose) you fell through the world. The sea bed is -7.</summary>
    const float FellBelow = -12f;

    const int GroundMask = 1 << 6;
    /// <summary>What counts as "stuck in something": Walls (7) and static Default props (houses, hedges, lamps).</summary>
    const int SolidMask = (1 << 7) | (1 << 0);

    static readonly Vector3[] ring = new Vector3[RingSize];
    static readonly float[] ringTime = new float[RingSize];
    static int ringCount, ringNext;
    static float lastUse = -999f, nextSample;
    static int ringScene = -1;
    static bool callHopped;
    static readonly Collider[] overlap = new Collider[16];
    static readonly Collider[] nearby = new Collider[128];
    static Runner runner;

    /// <summary>M2 hook: a reason Unstuck is unavailable right now ("Wriggle free first!" while carried or flopped), or null.</summary>
    public static Func<string> BlockedReason;

    /// <summary>Seconds until Unstuck can be used again (0 = ready).</summary>
    public static float CooldownLeft => Mathf.Max(0f, lastUse + CooldownSeconds - Time.realtimeSinceStartup);

    /// <summary>Safe samples in the ring right now (tests).</summary>
    public static int RingCount => ringCount;

    /// <summary>What the last use did ("ring 6.2 m", "sidewalk", "spawn", "tow", "call", "ring 6.2 m + call", "failed: …"), for tests and logs.</summary>
    public static string LastResult { get; private set; }

    /// <summary>What a press does right now.</summary>
    public enum Kind { None, Tow, Call, Hop, Dismount }

    /// <summary>What a press would do for this player (<see cref="MakePlan"/>): the button, its confirm and <see cref="Use"/> all read it.</summary>
    public readonly struct Plan
    {
        public readonly Kind kind;
        /// <summary>The player is moved to a safe spot first (really stuck, or nothing to call).</summary>
        public readonly bool hop;
        /// <summary>Their truck is called (after the hop, if any).</summary>
        public readonly bool call;
        /// <summary>It costs the tow fee (the confirm says so).</summary>
        public readonly bool fee;
        /// <summary>Why nothing can be done (<see cref="Kind.None"/>), short enough for the button's tape.</summary>
        public readonly string whyNot;

        public Plan(Kind kind, bool hop = false, bool call = false, bool fee = false, string whyNot = null)
        {
            this.kind = kind; this.hop = hop; this.call = call; this.fee = fee; this.whyNot = whyNot;
        }

        public static Plan Blocked(string why) => new Plan(Kind.None, whyNot: why);
    }

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
        return MakePlan(me).whyNot;
    }

    /// <summary>What a press would do for <paramref name="me"/> right now. Cheap: the pause menu asks twice a second.</summary>
    public static Plan MakePlan(PlayerController me)
    {
        if (me.IsDriving)
        {
            // In the seat: the tow. Piloting something else (the Hoverhog sets IsDriving too): nothing to hop or call.
            var seat = me.currentTruck;
            return seat != null ? new Plan(Kind.Tow, fee: !TruckCall.IsFree(seat)) : Plan.Blocked(UiText.Pause.LandFirst);
        }
        var rider = me.GetComponent<RideRider>();
        if (rider != null && rider.IsFlying) return Plan.Blocked(UiText.Pause.LandFirst);
        var look = LookAround(me, me.GetComponent<CharacterController>());
        if (rider != null && rider.IsRiding)
            return new Plan(Kind.Dismount, call: look.callable, fee: look.callable && !TruckCall.IsFree(look.truck) && !TruckCall.IsRightHere(look.truck, me.transform.position));
        if (!look.callable && !look.stuck)
        {
            // Nothing to call and not stuck: the old hop. Unless the truck exists and is simply busy right now: say so.
            if (look.truck != null && look.block != null && !look.indoors) return Plan.Blocked(look.block);
            return new Plan(Kind.Hop, hop: true);
        }
        bool fee = look.callable && !TruckCall.IsFree(look.truck) && (look.stuck || !TruckCall.IsRightHere(look.truck, me.transform.position));
        return new Plan(look.callable ? Kind.Call : Kind.Hop, hop: look.stuck || !look.callable, call: look.callable, fee: fee);
    }

    struct Look
    {
        public bool stuck, indoors, callable;
        public TruckControllerV2 truck;
        public string block;
    }

    static Look LookAround(PlayerController me, CharacterController cc)
    {
        var look = new Look();
        Vector3 feet = me.transform.position;
        look.stuck = FellOutOfWorld(feet) || Embedded(me, cc, feet);
        look.indoors = DepotRowLayout.InteriorAt(feet) >= 0 || CasinoLayout.Contains(feet);
        ulong client = Permissions.LocalClientId;
        look.truck = TruckCall.TruckFor(client);
        look.block = look.truck != null ? TruckCall.WhyNotCallable(look.truck, client) : null;
        look.callable = look.truck != null && look.block == null && !look.indoors;
        return look;
    }

    /// <summary>
    /// Do it now for this machine's player. Returns false (and a readable <paramref name="whyNot"/>, empty when there is
    /// nothing to say) when nothing happened and the cooldown shouldn't start. The pause menu resumes first, so the tow
    /// (which runs on scaled time) can fly.
    /// </summary>
    public static bool Use(out string whyNot)
    {
        whyNot = Unavailable();
        if (whyNot != null) { LastResult = "refused: " + whyNot; return false; }
        var me = PauseService.Me;
        var plan = MakePlan(me);
        // Start the cooldown first: the host's own answer to a call arrives inside OnFoot and may hand it back.
        float before = lastUse;
        lastUse = Time.realtimeSinceStartup;
        bool ok;
        switch (plan.kind)
        {
            case Kind.Tow: ok = Tow(me, out whyNot); break;
            case Kind.Dismount: ok = Dismount(me, out whyNot); break;
            default: ok = OnFoot(me, out whyNot); break;
        }
        if (!ok)
        {
            lastUse = before;
            if (!string.IsNullOrEmpty(whyNot)) LastResult = "failed: " + whyNot;
        }
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

    /// <summary>Climb off the rear step first (solo at once; in co-op the host takes you off a moment later), then the on-foot rule.</summary>
    static bool Dismount(PlayerController me, out string whyNot)
    {
        whyNot = null;
        var rider = me.GetComponent<RideRider>();
        rider.Unhitch();
        if (!rider.IsRiding) return OnFoot(me, out whyNot);
        if (runner != null) runner.StartCoroutine(AfterDismount(me, rider));
        LastResult = "dismount";
        return true;
    }

    static IEnumerator AfterDismount(PlayerController me, RideRider rider)
    {
        float until = Time.realtimeSinceStartup + 1.5f;
        while (rider != null && rider.IsRiding && Time.realtimeSinceStartup < until) yield return null;
        if (me == null || rider == null || rider.IsRiding) yield break;
        if (OnFoot(me, out string why)) yield break;
        lastUse = -999f; // hopping off was all it came to
        if (!string.IsNullOrEmpty(why)) GameHUD.Toast(why, ToastStyle.Bad, 2.5f);
    }

    static bool OnFoot(PlayerController me, out string whyNot)
    {
        whyNot = null;
        var cc = me.GetComponent<CharacterController>();
        var look = LookAround(me, cc);
        // Not stuck, and the truck is out on a job (you hopped off a friend's truck, say): the button already told you.
        if (!look.stuck && !look.callable && look.truck != null && look.block != null && !look.indoors) { whyNot = look.block; return false; }
        // Only a really stuck player is moved, or one with nothing to call; everyone else keeps their feet (and their bag).
        bool hopped = false;
        string how = null;
        if (look.stuck || !look.callable)
        {
            if (!Hop(me, cc, me.transform.position, out how, out whyNot)) return false;
            hopped = true;
        }
        if (!look.callable) return true;

        var result = CallTruck(me, look.truck, hopped, how);
        if (result == Called.NoRoom && !hopped)
        {
            // Nowhere near you to put it (a garage, a fenced-in yard): the old hop, then try again from the new spot.
            if (!Hop(me, cc, me.transform.position, out how, out _)) { whyNot = UiText.Pause.TruckNoRoom; return false; }
            hopped = true;
            result = CallTruck(me, look.truck, hopped, how);
        }
        switch (result)
        {
            case Called.Here:
                GameHUD.Toast(UiText.Pause.TruckHere, ToastStyle.Info, 2f);
                return hopped; // just looking at it costs nothing, and starts no cooldown
            case Called.NoRoom:
                GameHUD.Toast(UiText.Pause.TruckNoRoom, ToastStyle.Bad, 2.5f);
                return hopped;
            default:
                return true;
        }
    }

    enum Called { Sent, Here, NoRoom }

    /// <summary>Call the player's truck to where they stand now (after the hop, if there was one).</summary>
    static Called CallTruck(PlayerController me, TruckControllerV2 truck, bool hopped, string how)
    {
        Vector3 at = me.transform.position;
        string moved = hopped ? how + " + " : "";
        if (TruckCall.IsRightHere(truck, at)) { LastResult = moved + "here"; return Called.Here; }
        if (!TruckCall.TryFindDrop(truck, at, me.transform.eulerAngles.y, me.transform, out Vector3 root, out float rootYaw))
        {
            LastResult = moved + "no room";
            return Called.NoRoom;
        }
        callHopped = hopped;
        LastResult = moved + "call";
        TruckCall.Send(at, root, rootYaw); // the answer comes back through OnCallReply
        return Called.Sent;
    }

    /// <summary>The host's answer to the call (its own at once, a guest's a moment after the press).</summary>
    internal static void OnCallReply(bool ok, string text)
    {
        if (ok)
        {
            GameHUD.Toast(UiText.Pause.TruckComing, ToastStyle.Good, 2.4f);
            Sfx.Play("notify", null, 0.6f, 1.1f);
            return;
        }
        if (!string.IsNullOrEmpty(text)) GameHUD.Toast(text, ToastStyle.Bad, 2.8f);
        Sfx.Play("buzz", null, 0.5f, 1f);
        LastResult = "refused: " + text;
        if (!callHopped) lastUse = -999f; // nothing happened to anyone: no cooldown
    }

    /// <summary>Drop what you hold and teleport to the best safe point (ring, sidewalk, spawn). False when there is none.</summary>
    static bool Hop(PlayerController me, CharacterController cc, Vector3 from, out string how, out string whyNot)
    {
        whyNot = null;
        var carrier = me.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying)
        {
            try { carrier.Drop(); }
            catch (Exception e) { Debug.LogException(e); }
        }
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

    /// <summary>The player's capsule is buried in a wall or a prop (a smaller probe than <see cref="IsSafe"/>, at the player's real size, so brushing past one never counts).</summary>
    static bool Embedded(PlayerController me, CharacterController cc, Vector3 feet) => Overlaps(me, cc, feet, 0.6f, true);

    /// <summary>Fell through the floor: far under the town, and not in a shop's or the casino's pocket (those sit 20 m down).</summary>
    public static bool FellOutOfWorld(Vector3 feet) =>
        feet.y < FellBelow && DepotRowLayout.InteriorAt(feet) < 0 && !CasinoLayout.Contains(feet);

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
            // TownMap is XZ data. Raycast from the street's own height (the relief), not from under it.
            point.y = TownRelief.Height(point.x, point.z);
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
    public static bool IsSafe(PlayerController me, CharacterController cc, Vector3 feet) => !Overlaps(me, cc, feet, 0.95f, false);

    /// <summary>
    /// The player's capsule (<paramref name="shrink"/> × its radius) at <paramref name="feet"/> overlaps a wall or a static prop.
    /// <paramref name="worldSize"/>: use the player's real (scaled) size; the ring's own probe has always used the local one.
    /// </summary>
    static bool Overlaps(PlayerController me, CharacterController cc, Vector3 feet, float shrink, bool worldSize)
    {
        float r = cc != null ? cc.radius : 0.28f, h = cc != null ? cc.height : 1.8f;
        Vector3 c = cc != null ? cc.center : new Vector3(0f, 0.9f, 0f);
        if (worldSize && me != null)
        {
            Vector3 s = me.transform.lossyScale;
            r *= Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            h *= Mathf.Abs(s.y);
            c.y *= Mathf.Abs(s.y);
        }
        Vector3 a = feet + Vector3.up * (c.y - h * 0.5f + r + 0.05f);
        Vector3 b = feet + Vector3.up * (c.y + h * 0.5f - r);
        int n = Physics.OverlapCapsuleNonAlloc(a, b, r * shrink, overlap, SolidMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = overlap[i];
            if (col == null || col.attachedRigidbody != null) continue; // bags, trucks, critters move
            if (me != null && col.transform.IsChildOf(me.transform)) continue;
            return true;
        }
        return false;
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
        callHopped = false;
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

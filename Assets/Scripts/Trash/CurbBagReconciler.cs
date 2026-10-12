using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The reverse of <see cref="PickupStop"/>.SyncBags: that one tidies bags whose record is gone; this one finds curb records
/// whose BAG is gone, so a stop's glow (<see cref="PickupStop.BagsRemaining"/> counts records, including a carried bag) can't
/// outlive its trash. A yard dog (or anything else) that loses a bag leaves the record behind with nothing to collect.
/// <para><b>Rule.</b> Every <see cref="PassSeconds"/>, on solo and the host only (a guest never respawns anything), a record
/// at a loaded stop whose id matches no live <see cref="TrashBag"/> (none in <see cref="Carryable.All"/> that isn't Consumed
/// or burst) for <see cref="OrphanSeconds"/> running is given a fresh bag at <see cref="PickupStop.PileCenter"/> through
/// <see cref="PickupStop.MaterializeRecord"/>, after a co-op "Published" mark is cleared
/// (<see cref="WorldState.HostUnpublish"/>) so every machine shows it at the curb again. Records a resident is still walking
/// out (<see cref="PickupStop.IsPresenting"/>), dumpster slots, and records a connected client still claims are left alone,
/// and a bag a player, dog or truck is holding is a live bag. A bag that exists but is stranded (below the kill plane,
/// non-finite, or sunk into scenery) for <see cref="StrandedSeconds"/> is carried back to its pile instead.</para>
/// </summary>
public sealed class CurbBagReconciler : MonoBehaviour
{
    public const float PassSeconds = 2f, OrphanSeconds = 10f, StrandedSeconds = 6f;
    /// <summary>Radius of the probe at a resting bag's centre: a solid collider overlapping it means the bag is inside scenery.</summary>
    const float EmbedRadius = 0.05f;
    static readonly int SolidMask = (1 << 0) | (1 << 6) | (1 << 7);   // Default, Ground (houses, cars), Walls

    public static CurbBagReconciler Instance { get; private set; }

    readonly HashSet<string> live = new HashSet<string>();
    readonly Dictionary<string, float> orphanSince = new Dictionary<string, float>();
    readonly Dictionary<TrashBag, float> strandedSince = new Dictionary<TrashBag, float>();
    readonly HashSet<string> seen = new HashSet<string>();
    readonly List<string> tmpIds = new List<string>();
    readonly List<TrashBag> tmpBags = new List<TrashBag>();
    readonly HashSet<TrashBag> strandedNow = new HashSet<TrashBag>();
    static readonly Collider[] probe = new Collider[8];
    float nextPass;

    // ------------------------------------------------------------------ rules (pure; EditMode tests reach them by reflection)

    /// <summary>Does this record need a bag it hasn't got? Only the authority acts, and never on a dumpster record, one a
    /// resident is still presenting, or one with a live bag.</summary>
    public static bool NeedsBag(bool connectedGuest, bool dumpsterSlot, bool presenting, bool hasLiveBag) =>
        !connectedGuest && !dumpsterSlot && !presenting && !hasLiveBag;

    /// <summary>Has a record been without a bag since <paramref name="since"/> (Time.time) for long enough to respawn it?</summary>
    public static bool OrphanDue(float since, float now) => now - since >= OrphanSeconds;

    /// <summary>Has a bag been stranded since <paramref name="since"/> for long enough to be carried back to its pile?</summary>
    public static bool StrandedDue(float since, float now) => now - since >= StrandedSeconds;

    /// <summary>Out of the world: not a number, infinite, or below the kill plane's rescue band.</summary>
    public static bool IsOutOfWorld(Vector3 p) =>
        float.IsNaN(p.x + p.y + p.z) || float.IsInfinity(p.x + p.y + p.z) || p.y < Carryable.KillPlaneY + 5f;

    // ------------------------------------------------------------------ boot

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null || !Application.isPlaying) return;
        var go = new GameObject("[CurbBagReconciler]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CurbBagReconciler>();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ the pass

    void Update()
    {
        float now = Time.time;
        if (now < nextPass) return;
        nextPass = now + PassSeconds;
        // A guest only mirrors the host's records; a scene with no stops has nothing to reconcile (and nothing to keep timing).
        if (GameSession.IsConnectedGuest || PickupStop.All.Count == 0)
        {
            orphanSince.Clear();
            strandedSince.Clear();
            return;
        }
        Pass(now);
    }

    void Pass(float now)
    {
        // 1. Which bag ids have a live object? Stranded ones count as live (they get carried back, not duplicated).
        live.Clear();
        strandedNow.Clear();
        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (!(all[i] is TrashBag tb) || tb == null || tb.Contents == null || string.IsNullOrEmpty(tb.Contents.bagId)) continue;
            if (tb.State == CarryState.Consumed || tb.HasBurst) continue;
            live.Add(tb.Contents.bagId);
            var rec = GameSession.FindCurbBag(tb.Contents.bagId);
            if (rec == null || DumpsterRules.IsDumpsterSlot(rec.slot)) continue;
            if (IsStranded(tb)) strandedNow.Add(tb);
        }

        // 2. Carry stranded bags back to their pile once they've been stuck a while.
        tmpBags.Clear();
        foreach (var kv in strandedSince) if (!strandedNow.Contains(kv.Key)) tmpBags.Add(kv.Key);
        foreach (var b in tmpBags) strandedSince.Remove(b);
        foreach (var tb in strandedNow)
        {
            if (!strandedSince.TryGetValue(tb, out float since)) { strandedSince[tb] = now; continue; }
            if (StrandedDue(since, now)) { Rescue(tb); strandedSince.Remove(tb); }
        }

        // 3. Records with no live bag: respawn after the grace period.
        seen.Clear();
        var recs = GameSession.CurbBags;
        for (int i = 0; i < recs.Count; i++)
        {
            var rec = recs[i];
            if (rec == null || rec.contents == null || string.IsNullOrEmpty(rec.bagId)) continue;
            var stop = PickupStop.Find(rec.stopId);
            if (stop == null) continue;
            if (!NeedsBag(false, DumpsterRules.IsDumpsterSlot(rec.slot), stop.IsPresenting(rec.bagId), live.Contains(rec.bagId))) continue;
            seen.Add(rec.bagId);
            if (!orphanSince.TryGetValue(rec.bagId, out float first)) { orphanSince[rec.bagId] = now; continue; }
            if (!OrphanDue(first, now)) continue;
            // A connected client who still claims it carries it: the claim dies with them (WorldState.OnPlayerLeft).
            if (WorldState.Instance != null && !WorldState.Instance.HostUnpublish(rec.bagId)) continue;
            var bag = stop.MaterializeRecord(rec, stop.PileCenter + Vector3.up * 0.4f, Vector3.zero, false);
            if (bag == null) continue;
            Debug.Log($"[CurbBags] {rec.bagId} at {rec.stopId} had no bag for {now - first:0}s: respawned at its pile.");
            NetAudit.Event("curb", new Dictionary<string, object> { { "op", "orphan-respawn" }, { "bag", rec.bagId }, { "stop", rec.stopId ?? "" } });
            orphanSince.Remove(rec.bagId);
        }
        tmpIds.Clear();
        foreach (var kv in orphanSince) if (!seen.Contains(kv.Key)) tmpIds.Add(kv.Key);
        foreach (var id in tmpIds) orphanSince.Remove(id);
    }

    /// <summary>A resting, free bag that is out of the world or whose centre sits inside scenery. Never one that is held, flying
    /// on a script, in a dog's mouth (kinematic), or simulated on another machine.</summary>
    static bool IsStranded(TrashBag tb)
    {
        if (tb.Holder != null || tb.NetRemote || tb.IsInScriptedFlight || tb.State == CarryState.Carried) return false;
        Vector3 p = tb.transform.position;
        if (IsOutOfWorld(p)) return true;
        if (tb.State != CarryState.Resting || tb.Body == null || tb.Body.isKinematic) return false;
        int n = Physics.OverlapSphereNonAlloc(tb.WorldCenter, EmbedRadius, probe, SolidMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = probe[i];
            if (c == null || c.attachedRigidbody != null || c is CharacterController || c.transform.IsChildOf(tb.transform)) continue;
            return true;
        }
        return false;
    }

    static void Rescue(TrashBag tb)
    {
        if (tb == null || tb.State == CarryState.Consumed || tb.Holder != null) return;
        var stop = tb.SourceStop != null ? tb.SourceStop : tb.CurbRecord != null ? PickupStop.Find(tb.CurbRecord.stopId) : null;
        if (stop == null) return;
        Vector3 p = stop.PileCenter + Vector3.up * 0.4f;
        tb.transform.position = p;
        var body = tb.Body;
        if (body != null)
        {
            body.position = p;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.WakeUp(); }
        }
        Physics.SyncTransforms();
        tb.NoteSafePosition();
        Fx.Dust(p, 0.6f);
        Debug.Log($"[CurbBags] stranded bag {tb.Contents.bagId} carried back to {stop.stopId}.");
    }
}

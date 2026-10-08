using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public partial class HandsNet
{
    const float CrossingKnockdownSeconds = 6f;
    const int MaxCrossingActors = 12;
    readonly HashSet<(ulong truck, uint actor, long crossing)> pendingCrossingImpacts = new HashSet<(ulong, uint, long)>();
    readonly HashSet<(ulong sender, uint actor, long crossing)> pendingCrossingContacts = new HashSet<(ulong, uint, long)>();
    readonly Dictionary<uint, CrossingImpactRecord> crossingImpactRecords = new Dictionary<uint, CrossingImpactRecord>(MaxCrossingActors);
    readonly Dictionary<uint, uint> receivedCrossingImpacts = new Dictionary<uint, uint>(MaxCrossingActors);
    readonly List<uint> expiredCrossingImpacts = new List<uint>(MaxCrossingActors);
    uint crossingImpactVersion;

    struct CrossingImpactRecord
    {
        public uint version;
        public ulong truck;
        public long crossing;
        public NpcHit hit;
    }

    void SpawnCrossingImpacts()
    {
        pendingCrossingImpacts.Clear(); pendingCrossingContacts.Clear(); crossingImpactRecords.Clear(); receivedCrossingImpacts.Clear(); crossingImpactVersion = 0;
        if (IsServer)
        {
            SessionService.SetGuard("rb.crossing.impact", new NetGuard(12f, 24, 64));
            SessionService.SetGuard("rb.crossing.contact", new NetGuard(8f, 16, 64));
            SessionService.SetGuard("rb.crossing.sync", new NetGuard(1f, 3, 16));
        }
        else StartCoroutine(RequestCrossingSnapshotWhenReady());
    }

    IEnumerator RequestCrossingSnapshotWhenReady()
    {
        // NetSession normally spawns before the outdoor scene. Do not ask while the clock/site roster is unready.
        while (IsSpawned && (!WorldLifeRunner.IsAwake || !GameClock.IsNetClock)) yield return null;
        if (!IsSpawned) yield break;
        yield return null; // the Schedule director registers this frame's crossing bodies
        if (IsSpawned) CrossingImpactSyncRpc();
    }

    static bool CrossingTarget(uint packed, long crossing)
    {
        var id = LifeId.FromPacked(packed);
        return crossing >= 0 && id.Packed == packed && id.kind == LifeKind.Crossing && id.index / 8 < 2 && id.index % 8 <= 5;
    }

    /// <summary>True consumes the networked contact even if invalid, so a rejected claim cannot fall back to a local hit.</summary>
    public bool ReportCrossingImpact(TruckControllerV2 truck, LifeId actor, Vector3 seenAt)
    {
        if (!IsSpawned || Solo || actor.kind != LifeKind.Crossing) return false;
        if (truck == null || !truck.IsSpawned || !truck.IsOwner) return true;
        long crossing = CrossingGuardDirector.EventSerial(actor);
        if (!CrossingTarget(actor.Packed, crossing) || !VehicleCrashRules.Finite(seenAt)) return true;
        if (IsServer) HostCrossingImpact(LocalId, truck.NetworkObjectId, actor.Packed, crossing, seenAt);
        else CrossingImpactClaimRpc(truck.NetworkObjectId, actor.Packed, crossing, seenAt);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CrossingImpactClaimRpc(ulong truck, uint actor, long crossing, Vector3 seenAt, RpcParams rp = default)
    {
        if (Allowed("rb.crossing.impact", rp.Receive.SenderClientId, 40))
            HostCrossingImpact(rp.Receive.SenderClientId, truck, actor, crossing, seenAt);
    }

    void HostCrossingImpact(ulong sender, ulong truck, uint actor, long crossing, Vector3 seenAt)
    {
        var key = (truck, actor, crossing);
        if (!IsServer || !CrossingTarget(actor, crossing) || !VehicleCrashRules.Finite(seenAt) ||
            pendingCrossingImpacts.Count >= MaxCrossingActors || pendingCrossingImpacts.Contains(key)) return;
        StartCoroutine(WaitForCrossingImpact(sender, truck, actor, crossing, seenAt));
    }

    IEnumerator WaitForCrossingImpact(ulong sender, ulong truckId, uint packed, long crossing, Vector3 seenAt)
    {
        var key = (truckId, packed, crossing);
        pendingCrossingImpacts.Add(key);
        float deadline = Time.unscaledTime + .35f;
        try
        {
            do
            {
                if (TryHostCrossingImpact(sender, truckId, LifeId.FromPacked(packed), crossing, seenAt)) break;
                yield return null;
            } while (IsSpawned && IsServer && Time.unscaledTime < deadline);
        }
        finally { pendingCrossingImpacts.Remove(key); }
    }

    // True = resolved/rejected. False = the independently observed truck pose may still arrive within the retry window.
    bool TryHostCrossingImpact(ulong sender, ulong truckId, LifeId id, long crossing, Vector3 seenAt)
    {
        var truck = ImpactTruck(truckId);
        var actor = CrossingGuardDirector.FindBody(id, crossing);
        if (truck == null || truck.OwnerClientId != sender || !truck.isActiveAndEnabled || truck.Body == null ||
            actor == null || !actor.Hittable || (seenAt - actor.Position).sqrMagnitude > 2.5f * 2.5f ||
            Mathf.Abs(seenAt.y - actor.Position.y) > 1.5f) return true;
        Vector3 velocity = TruckImpact.Ensure(truck).RecentVelocity;
        if (!VehicleCrashRules.Finite(velocity)) return true;
        velocity.y = 0f;
        float speed = velocity.magnitude;
        if (speed < WorldLifeTuning.Hits.NudgeSpeed || speed > Mathf.Min(35f, truck.maxSpeed * 1.25f + 3f)) return false;
        if (!VehicleCrashRules.CarNearTruckBounds(actor.Position, actor.HalfExtents,
            truck.Body.position, truck.Body.rotation, truck.ModelBodyBoxMetres)) return false;
        var claim = new NpcHitClaim { target = id, tick = WorldLifeRunner.FixedTickCount, truckVelocity = velocity,
            actorSlot = WorldLifeAuthority.SlotOf(truck), truck = truck };
        if (!WorldLifeAuthority.Instance.Decide(in claim, actor, RulesBooks.Active, out var hit, out _)) return true;
        // No Commit: these are town event actors, not household residents with a fine/grudge/save record.
        BroadcastCrossingHit(truckId, crossing, in hit, false);
        return true;
    }

    void BroadcastCrossingHit(ulong truckId, long crossing, in NpcHit hit, bool contact)
    {
        var record = new CrossingImpactRecord { version = ++crossingImpactVersion, truck = truckId, crossing = crossing, hit = hit };
        PruneCrossingImpactRecords();
        if (hit.kind == NpcHitKind.Flatten || hit.kind == NpcHitKind.ShortFlop) crossingImpactRecords[hit.target.Packed] = record;
        else crossingImpactRecords.Remove(hit.target.Packed);
        CrossingImpactConfirmedRpc(record.version, truckId, hit.target.Packed, crossing, (byte)hit.kind, hit.atHours, hit.tick,
            hit.point, hit.launch, hit.truckVelocity, hit.seed, hit.actorSlot, contact);
    }

    /// <summary>Local thrown/bump authority calls PlayHit; network sessions replace that prediction with the host's word.</summary>
    public bool ReportCrossingContact(in NpcHit hit)
    {
        if (!IsSpawned || Solo || hit.target.kind != LifeKind.Crossing) return false;
        // Truck contacts have their separate truck-ownership/observed-speed path. Never trust a generic contact as a flatten.
        if (hit.kind != NpcHitKind.Boop && hit.kind != NpcHitKind.Stumble && hit.kind != NpcHitKind.ShortFlop) return true;
        long crossing = CrossingGuardDirector.EventSerial(hit.target);
        if (!CrossingTarget(hit.target.Packed, crossing) || !VehicleCrashRules.Finite(hit.truckVelocity)) return true;
        uint item = 0;
        float nearest = 2.5f * 2.5f;
        foreach (var nc in items.Values)
        {
            if (nc == null || nc.Item == null || nc.St.Phase != HandsPhase.Flight || nc.St.holder != LocalId) continue;
            float distance = (nc.Item.WorldCenter - (hit.point + Vector3.up * .7f)).sqrMagnitude;
            if (distance < nearest) { nearest = distance; item = nc.Id; }
        }
        Vector3 velocity = Vector3.ClampMagnitude(hit.truckVelocity, 25f);
        if (IsServer) HostCrossingContact(LocalId, hit.target.Packed, crossing, (byte)hit.kind, item, velocity);
        else CrossingContactClaimRpc(hit.target.Packed, crossing, (byte)hit.kind, item, velocity);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CrossingContactClaimRpc(uint actor, long crossing, byte kind, uint item, Vector3 velocity, RpcParams rp = default)
    {
        if (Allowed("rb.crossing.contact", rp.Receive.SenderClientId, 40))
            HostCrossingContact(rp.Receive.SenderClientId, actor, crossing, kind, item, velocity);
    }

    void HostCrossingContact(ulong sender, uint packed, long crossing, byte requestedKind, uint item, Vector3 velocity)
    {
        var key = (sender, packed, crossing);
        if (!IsServer || !CrossingTarget(packed, crossing) || !VehicleCrashRules.Finite(velocity) ||
            (requestedKind != (byte)NpcHitKind.Boop && requestedKind != (byte)NpcHitKind.Stumble && requestedKind != (byte)NpcHitKind.ShortFlop) ||
            pendingCrossingContacts.Count >= MaxCrossingActors || pendingCrossingContacts.Contains(key)) return;
        StartCoroutine(WaitForCrossingContact(sender, packed, crossing, requestedKind, item, Vector3.ClampMagnitude(velocity, 25f)));
    }

    IEnumerator WaitForCrossingContact(ulong sender, uint packed, long crossing, byte requestedKind, uint item, Vector3 velocity)
    {
        var key = (sender, packed, crossing);
        pendingCrossingContacts.Add(key);
        float deadline = Time.unscaledTime + .35f;
        try
        {
            do
            {
                if (TryHostCrossingContact(sender, LifeId.FromPacked(packed), crossing, requestedKind, item, velocity)) break;
                yield return null;
            } while (IsSpawned && IsServer && Time.unscaledTime < deadline);
        }
        finally { pendingCrossingContacts.Remove(key); }
    }

    bool TryHostCrossingContact(ulong sender, LifeId id, long crossing, byte requestedKind, uint itemId, Vector3 claimedVelocity)
    {
        var actor = CrossingGuardDirector.FindBody(id, crossing);
        if (actor == null || !actor.Hittable) return true;
        Vector3 direction, velocity;
        NpcHitKind kind;
        if (itemId != 0)
        {
            var nc = Get(itemId);
            if (nc == null) return false; // release/publish may be arriving just behind the contact claim
            if (nc.Item == null || nc.St.holder != sender) return true;
            if (nc.St.Phase != HandsPhase.Flight) return false;
            Vector3 center = actor.Position + Vector3.up * actor.HalfExtents.y;
            if (!VehicleCrashRules.Finite(nc.Item.WorldCenter) || (nc.Item.WorldCenter - center).sqrMagnitude > 2.2f * 2.2f) return false;
            // The host owns the item's flight/thrower state and sees its actual nearby replica. Clients supply only
            // bounded contact velocity (never launch, pose, or chosen effect); gravity can raise it above release speed.
            float releasedSpeed = VehicleCrashRules.Finite(nc.St.vel) ? nc.St.vel.magnitude : 0f;
            velocity = Vector3.ClampMagnitude(claimedVelocity, Mathf.Min(25f, releasedSpeed + 10f));
            if (velocity.sqrMagnitude < 1f) return true;
            float speed = velocity.magnitude;
            kind = speed >= WorldLifeTuning.Hits.ThrownFlopSpeed ? NpcHitKind.ShortFlop :
                speed >= WorldLifeTuning.Hits.ThrownStumbleSpeed ? NpcHitKind.Stumble : NpcHitKind.Boop;
            direction = new Vector3(velocity.x, 0, velocity.z);
        }
        else
        {
            // No owned thrown item: only the small stumble is legal, at an independently observed nearby player.
            var player = PlayerBody.For(sender);
            if (requestedKind != (byte)NpcHitKind.Stumble || player == null || player.Player == null ||
                player.Player.IsDriving || !HostPlayerPos(sender, out Vector3 playerPos)) return true;
            if ((playerPos - actor.Position).sqrMagnitude > 3f * 3f || Mathf.Abs(playerPos.y - actor.Position.y) > 1.5f) return false;
            float speed = new Vector2(claimedVelocity.x, claimedVelocity.z).magnitude;
            if (speed < WorldLifeTuning.Hits.PlayerBumpStumbleSpeed) return true;
            direction = actor.Position - playerPos; direction.y = 0;
            velocity = (direction.sqrMagnitude > .001f ? direction.normalized : Vector3.forward) * Mathf.Min(speed, 8.5f);
            kind = NpcHitKind.Stumble;
        }
        direction = direction.sqrMagnitude > .001f ? direction.normalized : Vector3.forward;
        Vector3 launch = kind == NpcHitKind.ShortFlop ? direction * RulesTuning.ShortFlopBack + Vector3.up * RulesTuning.ShortFlopUp :
            kind == NpcHitKind.Stumble ? direction * WorldLifeTuning.Hits.PlayerBumpPush : Vector3.zero;
        var hit = new NpcHit { target = id, kind = kind, atHours = GameClock.TotalHours, tick = WorldLifeRunner.FixedTickCount,
            point = actor.Position, launch = launch, truckVelocity = velocity, actorSlot = (byte)(sender > 255UL ? 255UL : sender),
            seed = (byte)LifeHash.Mix(id.Packed ^ (ulong)WorldLifeRunner.FixedTickCount) };
        // Contact callers already raise their local hit fact; do not recursively call body.PlayHit or double-fire it.
        BroadcastCrossingHit(None, crossing, in hit, true);
        return true;
    }

    [Rpc(SendTo.Everyone)]
    void CrossingImpactConfirmedRpc(uint version, ulong truck, uint actor, long crossing, byte kind, double hours, int tick,
        Vector3 point, Vector3 launch, Vector3 velocity, byte seed, byte slot, bool contact, RpcParams rp = default)
    {
        if (rp.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId) return;
        ReceiveCrossingImpact(version, truck, actor, crossing, kind, hours, tick, point, launch, velocity, seed, slot, contact);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CrossingImpactSyncRpc(RpcParams rp = default)
    {
        ulong client = rp.Receive.SenderClientId;
        if (!Allowed("rb.crossing.sync", client, 8)) return;
        PruneCrossingImpactRecords();
        foreach (var record in crossingImpactRecords.Values)
        {
            var hit = record.hit;
            CrossingImpactSnapshotRpc(record.version, record.truck, hit.target.Packed, record.crossing, (byte)hit.kind,
                hit.atHours, hit.tick, hit.point, hit.launch, hit.truckVelocity, hit.seed, hit.actorSlot,
                RpcTarget.Single(client, RpcTargetUse.Temp));
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void CrossingImpactSnapshotRpc(uint version, ulong truck, uint actor, long crossing, byte kind, double hours, int tick,
        Vector3 point, Vector3 launch, Vector3 velocity, byte seed, byte slot, RpcParams rp = default)
    {
        if (IsServer || rp.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId) return;
        ReceiveCrossingImpact(version, truck, actor, crossing, kind, hours, tick, point, launch, velocity, seed, slot, true);
    }

    void ReceiveCrossingImpact(uint version, ulong truckId, uint packed, long crossing, byte kind, double hours, int tick,
        Vector3 point, Vector3 launch, Vector3 velocity, byte seed, byte slot, bool snapshot)
    {
        if (!CrossingTarget(packed, crossing) || version == 0 || double.IsNaN(hours) || double.IsInfinity(hours) ||
            !VehicleCrashRules.Finite(point) || !VehicleCrashRules.Finite(launch) || !VehicleCrashRules.Finite(velocity) ||
            (kind != (byte)NpcHitKind.Flatten && kind != (byte)NpcHitKind.Nudge && kind != (byte)NpcHitKind.Dodge &&
             kind != (byte)NpcHitKind.ShortFlop && kind != (byte)NpcHitKind.Stumble && kind != (byte)NpcHitKind.Boop)) return;
        if (receivedCrossingImpacts.TryGetValue(packed, out uint last) && version <= last) return;
        receivedCrossingImpacts[packed] = version; // per actor: a snapshot for actor A must not discard actor B's older hit
        var hit = new NpcHit { target = LifeId.FromPacked(packed), kind = (NpcHitKind)kind, atHours = hours, tick = tick,
            point = point, launch = launch, truckVelocity = velocity, seed = seed, actorSlot = slot, truck = ImpactTruck(truckId) };
        if (!ApplyCrossingImpact(crossing, in hit, snapshot))
            StartCoroutine(WaitToPresentCrossingImpact(version, crossing, hit, snapshot));
    }

    IEnumerator WaitToPresentCrossingImpact(uint version, long crossing, NpcHit hit, bool snapshot)
    {
        float deadline = Time.unscaledTime + 1f;
        while (IsSpawned && Time.unscaledTime < deadline && receivedCrossingImpacts.TryGetValue(hit.target.Packed, out uint latest) && latest == version)
        {
            yield return null;
            if (ApplyCrossingImpact(crossing, in hit, snapshot)) yield break;
        }
    }

    static bool ApplyCrossingImpact(long crossing, in NpcHit hit, bool snapshot)
    {
        double age = (GameClock.TotalHours - hit.atHours) * GameClock.SecondsPerGameHour;
        if (age > CrossingKnockdownSeconds || age < -.5) return true;
        if (CrossingGuardDirector.FindBody(hit.target, crossing) == null) return false;
        CrossingGuardDirector.PresentHit(hit.target, crossing, in hit);
        if (!snapshot) WorldLifeEvents.RaiseHitDecided(in hit);
        return true;
    }

    void PruneCrossingImpactRecords()
    {
        expiredCrossingImpacts.Clear();
        foreach (var pair in crossingImpactRecords)
        {
            var entry = pair.Value;
            double age = (GameClock.TotalHours - entry.hit.atHours) * GameClock.SecondsPerGameHour;
            if (age < 0 || age > CrossingKnockdownSeconds || CrossingGuardDirector.EventSerial(entry.hit.target) != entry.crossing)
                expiredCrossingImpacts.Add(pair.Key);
        }
        foreach (uint id in expiredCrossingImpacts) crossingImpactRecords.Remove(id);
    }
}

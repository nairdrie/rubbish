using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public partial class HandsNet
{
    readonly HashSet<(ulong, uint)> pendingCarImpacts = new HashSet<(ulong, uint)>();
    uint carImpactSerial, lastCarImpactSerial;
    public uint CarImpactConfirmations => lastCarImpactSerial;

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CarImpactSyncRpc(RpcParams rp = default)
    {
        ulong client = rp.Receive.SenderClientId;
        if (!Allowed("rb.car.sync", client, 8)) return;
        var totals = new Dictionary<(uint, int), int>();
        foreach (var entry in LifeOverrides.All)
        {
            if (entry.id.kind != LifeKind.Car || entry.kind != NpcHitKind.Shunt) continue;
            var key = (entry.id.Packed, entry.day);
            totals.TryGetValue(key, out int count);
            totals[key] = count + entry.delayCs;
        }
        foreach (var entry in totals)
            CarDelaySnapshotRpc(carImpactSerial, entry.Key.Item1, entry.Key.Item2, entry.Value, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void CarDelaySnapshotRpc(uint serial, uint car, int day, int cumulativeCs, RpcParams rp = default)
    {
        if (rp.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId || IsServer) return;
        var id = LifeId.FromPacked(car);
        if (id.kind != LifeKind.Car) return;
        int remaining = Mathf.Clamp(cumulativeCs - Mathf.RoundToInt(LifeOverrides.DelaySeconds(id, day) * 100f), 0, 6000000);
        // A replayed snapshot or an event received before this snapshot cannot add the same delay twice.
        while (remaining > 0)
        {
            ushort chunk = (ushort)Mathf.Min(ushort.MaxValue, remaining);
            LifeOverrides.Add(new NpcOverride { id = id, day = day, delayCs = chunk, kind = NpcHitKind.Shunt });
            remaining -= chunk;
        }
        if (serial > lastCarImpactSerial) lastCarImpactSerial = serial;
    }

    /// <summary>True means the host owns this claim; the sensor must not also play a local cartoon.</summary>
    public bool ReportCarImpact(TruckControllerV2 truck, LifeId car, Vector3 seenAt)
    {
        if (!IsSpawned || Solo || truck == null || !truck.IsSpawned || !truck.IsOwner || car.kind != LifeKind.Car) return false;
        if (IsServer) HostCarImpact(LocalId, truck.NetworkObjectId, car.Packed, seenAt);
        else CarImpactClaimRpc(truck.NetworkObjectId, car.Packed, seenAt);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CarImpactClaimRpc(ulong truck, uint car, Vector3 seenAt, RpcParams rp = default)
    {
        if (Allowed("rb.car.impact", rp.Receive.SenderClientId, 32)) HostCarImpact(rp.Receive.SenderClientId, truck, car, seenAt);
    }

    void HostCarImpact(ulong sender, ulong truck, uint car, Vector3 seenAt)
    {
        if (!IsServer || !VehicleCrashRules.Finite(seenAt) || LifeId.FromPacked(car).kind != LifeKind.Car || pendingCarImpacts.Count >= 16 ||
            pendingCarImpacts.Contains((truck, car))) return;
        StartCoroutine(WaitForCarImpact(sender, truck, car, seenAt));
    }

    IEnumerator WaitForCarImpact(ulong sender, ulong truckId, uint carId, Vector3 seenAt)
    {
        var key = (truckId, carId);
        pendingCarImpacts.Add(key);
        float deadline = Time.time + .35f;
        do
        {
            if (TryHostCarImpact(sender, truckId, LifeId.FromPacked(carId), seenAt)) break;
            yield return null;
        } while (IsSpawned && IsServer && Time.time < deadline);
        pendingCarImpacts.Remove(key);
    }

    bool TryHostCarImpact(ulong sender, ulong truckId, LifeId carId, Vector3 seenAt)
    {
        var truck = ImpactTruck(truckId);
        var traffic = TrafficDirector.Instance;
        if (truck == null || truck.OwnerClientId != sender || !truck.isActiveAndEnabled || truck.Body == null ||
            traffic == null || !traffic.TryImpactBodyAt(carId, seenAt, out var car) || !car.Hittable) return true;
        Vector3 velocity = TruckImpact.Ensure(truck).RecentVelocity;
        velocity.y = 0f;
        float speed = velocity.magnitude;
        if (speed < WorldLifeTuning.Hits.ShuntSpeed || speed > Mathf.Min(35f, truck.maxSpeed * 1.25f + 3f)) return false;
        // Car position is constrained to its actual scheduled route and bounded lag window; the truck pose/speed are
        // independently observed by the host. The client cannot place a car elsewhere or choose the launch impulse.
        if (!VehicleCrashRules.CarNearTruck(car.Position, car.HalfExtents, truck.Body.position, truck.Body.rotation)) return false;
        var claim = new NpcHitClaim { target = carId, tick = WorldLifeRunner.FixedTickCount, truckVelocity = velocity,
            actorSlot = WorldLifeAuthority.SlotOf(truck), truck = truck };
        var authority = WorldLifeAuthority.Instance;
        if (!authority.Decide(in claim, car, RulesBooks.Active, out var hit, out bool consequential)) return true;
        // The truck's business pays, including an employee driving their boss's truck. Never route guests to host cash.
        using (Ledger.ToBusiness(DepotTruckCargo.BusinessOf(truck))) authority.Commit(in hit, car, consequential, RulesBooks.Active);
        CarImpactConfirmedRpc(++carImpactSerial, truckId, carId.Packed, hit.tick, hit.atHours, hit.point,
            hit.truckVelocity, hit.seed, hit.actorSlot, hit.delayCs);
        return true;
    }

    [Rpc(SendTo.Everyone)]
    void CarImpactConfirmedRpc(uint serial, ulong truckId, uint carId, int tick, double hours, Vector3 point,
        Vector3 velocity, byte seed, byte actor, ushort delayCs, RpcParams rp = default)
    {
        if (rp.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId || serial <= lastCarImpactSerial) return;
        lastCarImpactSerial = serial;
        var truck = ImpactTruck(truckId);
        var hit = new NpcHit { target = LifeId.FromPacked(carId), kind = NpcHitKind.Shunt, tick = tick, atHours = hours,
            point = point, truckVelocity = velocity, launch = velocity.normalized * VehicleCrashRules.CarSlideDistance(velocity.magnitude),
            seed = seed, actorSlot = actor, delayCs = delayCs, truck = truck };
        TrafficDirector.Instance?.PresentNetworkImpact(in hit);
        if (!IsServer) LifeOverrides.Add(new NpcOverride { id = hit.target, day = RulesBooks.DayOf(hours), delayCs = delayCs,
            kind = NpcHitKind.Shunt, untilTick = WorldLifeRunner.FixedTickCount + Mathf.CeilToInt(RulesTuning.OverrideHoldSeconds / Mathf.Max(.005f, Time.fixedDeltaTime)), actorSlot = actor });
        WorldLifeEvents.RaiseHitDecided(in hit);
        TruckLifeSensor.Instance.BumpTruck(truck, point);
    }
}

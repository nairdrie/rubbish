using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public partial class HandsNet
{
    readonly Dictionary<(ulong, ulong), float> truckImpactCooldowns = new Dictionary<(ulong, ulong), float>();
    readonly HashSet<(ulong, ulong)> pendingTruckImpacts = new HashSet<(ulong, ulong)>();
    public int TruckImpactConfirmations { get; private set; }

    public void ReportTruckImpact(TruckControllerV2 own, TruckControllerV2 other)
    {
        if (!IsSpawned || own == null || other == null || !own.IsSpawned || !other.IsSpawned || !own.IsOwner) return;
        if (IsServer) HostTruckImpact(LocalId, own.NetworkObjectId, other.NetworkObjectId);
        else TruckImpactClaimRpc(own.NetworkObjectId, other.NetworkObjectId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TruckImpactClaimRpc(ulong ownId, ulong otherId, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed("rb.truck.impact", sender, 24)) return;
        HostTruckImpact(sender, ownId, otherId);
    }

    TruckControllerV2 ImpactTruck(ulong id) => NetworkManager.SpawnManager != null &&
        NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(id, out var no) && no != null
        ? no.GetComponent<TruckControllerV2>() : null;

    void HostTruckImpact(ulong sender, ulong ownId, ulong otherId)
    {
        if (!IsServer || ownId == otherId) return;
        var pair = ownId < otherId ? (ownId, otherId) : (otherId, ownId);
        if (pendingTruckImpacts.Contains(pair) || pendingTruckImpacts.Count >= 8) return;
        if (truckImpactCooldowns.TryGetValue(pair, out float until) && Time.time < until) return;
        StartCoroutine(WaitForTruckImpact(sender, ownId, otherId, pair));
    }

    IEnumerator WaitForTruckImpact(ulong sender, ulong ownId, ulong otherId, (ulong, ulong) pair)
    {
        pendingTruckImpacts.Add(pair);
        float deadline = Time.time + .35f;
        do
        {
            if (TryHostTruckImpact(sender, ownId, otherId, pair)) break;
            yield return null;
        } while (IsSpawned && IsServer && Time.time < deadline);
        pendingTruckImpacts.Remove(pair);
    }

    bool TryHostTruckImpact(ulong sender, ulong ownId, ulong otherId, (ulong, ulong) pair)
    {
        var a = ImpactTruck(ownId); var b = ImpactTruck(otherId);
        if (a == null || b == null || a.OwnerClientId != sender || !a.isActiveAndEnabled || !b.isActiveAndEnabled ||
            a.Body == null || b.Body == null) return true;
        // Owner reports contain IDs ONLY: no client chooses force, speed, position or direction.
        // Wait briefly for the host's interpolated guest truck to reach contact; never expand the hitbox to hide lag.
        if (!VehicleCrashRules.TrucksNear(a.Body.position, a.Body.rotation, b.Body.position, b.Body.rotation, .8f)) return false;
        Vector3 direction = TruckImpact.FlatDirection(a, b);
        float closing = TruckImpact.Ensure(a).ApproachSpeed(direction) + TruckImpact.Ensure(b).ApproachSpeed(-direction);
        if (!VehicleCrashRules.Finite(closing) || closing < VehicleCrashRules.MinimumClosingSpeed || closing > VehicleCrashRules.MaximumClosingSpeed) return false;
        if (truckImpactCooldowns.Count > 64) truckImpactCooldowns.Clear();
        truckImpactCooldowns[pair] = Time.time + VehicleCrashRules.PairCooldown;
        TruckImpactConfirmedRpc(ownId, otherId, direction, closing);
        return true;
    }

    [Rpc(SendTo.Everyone)]
    void TruckImpactConfirmedRpc(ulong a, ulong b, Vector3 direction, float closing, RpcParams rp = default)
    {
        if (rp.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId) return;
        TruckImpactConfirmations++;
        TruckImpact.ApplyPair(ImpactTruck(a), ImpactTruck(b), direction, closing);
    }
}

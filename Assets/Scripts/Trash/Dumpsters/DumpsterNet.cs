using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Co-op for the Dumpster Dunker: a guest driver asks the HOST to tip the dumpster their truck is backed up to
/// (the host re-checks everything), and the host tells every machine to play the lift.</summary>
[NetSessionComponent(Order = 28)]
public class DumpsterNet : NetworkBehaviour
{
    public static DumpsterNet Instance { get; private set; }
    public override void OnNetworkSpawn() => Instance = this;
    public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }
    public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

    public void RequestLift(TruckControllerV2 truck, string stopId)
    {
        if (truck == null || !truck.IsSpawned || !IsSpawned || string.IsNullOrEmpty(stopId)) return;
        LiftRpc(new NetworkObjectReference(truck.NetworkObject), new FixedString32Bytes(stopId));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LiftRpc(NetworkObjectReference truckRef, FixedString32Bytes stopId, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!SessionService.GuardFor("rb.dumpster.lift").Allow(sender, 24, Time.realtimeSinceStartupAsDouble)) return;
        if (!truckRef.TryGet(out var obj, NetworkManager) || obj == null) return;
        var truck = obj.GetComponent<TruckControllerV2>();
        // Only the driver may dunk: the seat is the host's own NetworkVariable, never the sender's claim.
        if (truck == null || !truck.HasDriver || truck.DriverClientId != sender) return;
        var lift = truck.GetComponent<TruckDumpsterLift>();
        if (lift == null) lift = truck.gameObject.AddComponent<TruckDumpsterLift>();
        lift.HostBegin(sender, Dumpster.ForStop(stopId.ToString()));
    }

    /// <summary>Host: every machine plays this truck lifting that stop's dumpster.</summary>
    public void BroadcastLift(TruckControllerV2 truck, string stopId, int bags, float secondsPerBag)
    {
        if (!IsServer || truck == null || !truck.IsSpawned) return;
        LiftFxRpc(new NetworkObjectReference(truck.NetworkObject), new FixedString32Bytes(stopId), (byte)Mathf.Clamp(bags, 0, 255), secondsPerBag);
    }

    [Rpc(SendTo.Everyone)]
    void LiftFxRpc(NetworkObjectReference truckRef, FixedString32Bytes stopId, byte bags, float secondsPerBag)
    {
        if (!truckRef.TryGet(out var obj, NetworkManager) || obj == null) return;
        var lift = obj.GetComponent<TruckDumpsterLift>();
        if (lift == null) lift = obj.gameObject.AddComponent<TruckDumpsterLift>();
        lift.Present(Dumpster.ForStop(stopId.ToString()), bags, secondsPerBag);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

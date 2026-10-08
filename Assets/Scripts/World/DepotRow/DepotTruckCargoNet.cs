using Unity.Netcode;
using UnityEngine;

/// <summary>A guest driver asks the HOST to tip their truck (TruckTipper). Cargo stays authoritative until each bag is spawned.</summary>
[NetSessionComponent(Order = 22)]
public class DepotTruckCargoNet : NetworkBehaviour
{
    public static DepotTruckCargoNet Instance { get; private set; }
    readonly NetworkVariable<byte> busyLots = new NetworkVariable<byte>();
    public override void OnNetworkSpawn() => Instance = this;
    public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }
    public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

    public bool Busy(int lot) => lot >= 0 && lot < DepotRowLayout.LotCount && (busyLots.Value & (1 << lot)) != 0;
    public void SetBusy(int lot, bool busy)
    {
        if (!IsServer || lot < 0 || lot >= DepotRowLayout.LotCount) return;
        busyLots.Value = (byte)(busy ? busyLots.Value | (1 << lot) : busyLots.Value & ~(1 << lot));
    }

    /// <summary>The seated driver of <paramref name="truck"/> asks the host to tip it where it stands.</summary>
    public void RequestDump(TruckControllerV2 truck)
    {
        if (truck == null || !truck.IsSpawned) return;
        DumpRpc(new NetworkObjectReference(truck.NetworkObject));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DumpRpc(NetworkObjectReference truckRef, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!SessionService.GuardFor("rb.cargo.tip").Allow(sender, 24, Time.realtimeSinceStartupAsDouble)) return;
        if (!truckRef.TryGet(out var obj, NetworkManager) || obj == null) return;
        var truck = obj.GetComponent<TruckControllerV2>();
        // Only the driver may tip: the seat is the host's own NetworkVariable, never the sender's claim.
        if (truck == null || !truck.HasDriver || truck.DriverClientId != sender) return;
        var tipper = truck.GetComponent<TruckTipper>();
        if (tipper == null) tipper = truck.gameObject.AddComponent<TruckTipper>();
        tipper.HostBegin(sender);
    }

    /// <summary>Host: tell every machine a truck started / stopped tipping (beeps, the driver's "hold still").</summary>
    public void BroadcastDump(TruckControllerV2 truck, bool on, float seconds)
    {
        if (!IsServer || truck == null || !truck.IsSpawned) return;
        DumpFxRpc(new NetworkObjectReference(truck.NetworkObject), on, seconds);
    }

    [Rpc(SendTo.Everyone)]
    void DumpFxRpc(NetworkObjectReference truckRef, bool on, float seconds)
    {
        if (!truckRef.TryGet(out var obj, NetworkManager) || obj == null) return;
        var tipper = obj.GetComponent<TruckTipper>();
        if (tipper != null) tipper.PresentDump(on, seconds);
    }

    public void Notify(ulong client, string message)
    {
        if (!IsServer) return;
        NoticeRpc(message, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(string message, RpcParams rpc = default) => GameHUD.Toast(message, ToastStyle.Info);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

using Unity.Netcode;
using UnityEngine;

/// <summary>Guests request the lot's HOST unloader. Cargo stays authoritative until each bag is spawned.</summary>
[NetSessionComponent(Order = 22)]
public class DepotTruckCargoNet : NetworkBehaviour
{
    public static DepotTruckCargoNet Instance { get; private set; }
    readonly NetworkVariable<byte> busyLots = new NetworkVariable<byte>();
    readonly NetworkVariable<byte> fullLots = new NetworkVariable<byte>();
    public override void OnNetworkSpawn() => Instance = this;
    public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }
    public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

    public bool Busy(int lot) => lot >= 0 && lot < DepotRowLayout.LotCount && (busyLots.Value & (1 << lot)) != 0;
    public bool Full(int lot) => lot >= 0 && lot < DepotRowLayout.LotCount && (fullLots.Value & (1 << lot)) != 0;
    public void SetFull(int lot, bool full)
    {
        if (!IsServer || lot < 0 || lot >= DepotRowLayout.LotCount) return;
        fullLots.Value = (byte)(full ? fullLots.Value | (1 << lot) : fullLots.Value & ~(1 << lot));
    }
    public void SetBusy(int lot, bool busy)
    {
        if (!IsServer || lot < 0 || lot >= DepotRowLayout.LotCount) return;
        busyLots.Value = (byte)(busy ? busyLots.Value | (1 << lot) : busyLots.Value & ~(1 << lot));
    }

    public void RequestTip(TruckControllerV2 truck, int lot)
    {
        if (truck == null || !truck.IsSpawned) return;
        TipRpc(new NetworkObjectReference(truck.NetworkObject), (sbyte)lot);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TipRpc(NetworkObjectReference truckRef, sbyte lot, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!SessionService.GuardFor("rb.cargo.tip").Allow(sender, 24, Time.realtimeSinceStartupAsDouble)) return;
        var net = DepotRowNet.Instance;
        if (net == null || !net.OneWorld || lot < 0 || lot >= DepotRowLayout.LotCount) return;
        if (!Permissions.CanWorkIn(sender, lot)) { Notify(sender, Permissions.NotYourShop); return; }
        var player = NightPlayers.ForClient(sender);
        if (player == null || DepotRowLayout.InteriorAt(player.transform.position) != lot) return;
        if (!truckRef.TryGet(out var obj, NetworkManager) || obj == null) return;
        if (net.LotOfTruck(obj.NetworkObjectId) != lot) { Notify(sender, "Park this shop's truck in its bay first."); return; }
        var truck = obj.GetComponent<TruckControllerV2>();
        foreach (var unloader in Object.FindObjectsByType<TruckUnloader>(FindObjectsSortMode.None))
        {
            if (DepotRowLayout.InteriorAt(unloader.transform.position) != lot || unloader.ParkedTruck != truck) continue;
            if (Vector3.Distance(player.transform.position, unloader.transform.position) > 12f) return;
            unloader.BeginAuthorizedTip(sender);
            return;
        }
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

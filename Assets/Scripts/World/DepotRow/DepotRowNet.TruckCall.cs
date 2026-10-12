using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Esc > Unstuck on foot calls the player's own truck to them (Docs/UIPass.md §5.5, TruckCall). The wire part lives here
// because DepotRowNet already holds the lots and their trucks and is on NetSession.prefab (a new NetworkBehaviour would
// need that prefab edited). Three messages, all small:
//  - guest -> host: TruckCallRpc, once per use (where the caller stands, where they want the truck, which way it faces).
//    The host checks all of it again (TruckCall.ServerCall) and answers the caller alone (TruckCallReplyRpc: a toast).
//  - host -> everyone else: TowDroneRpc, twice per delivery (it started, it ended), so every machine shows the drone.
//    Paint only: the truck itself moves through its own NetworkTransform and the drone follows that.
public partial class DepotRowNet
{
    NetGuard truckCallGuard;

    /// <summary>Guest: ask the host to fly your truck to <paramref name="root"/>, beside you at <paramref name="player"/>.</summary>
    public void RequestTruckCall(Vector3 player, Vector3 root, float yawDeg) => TruckCallRpc(player, root, yawDeg);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void TruckCallRpc(Vector3 player, Vector3 root, float yawDeg, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (truckCallGuard == null) truckCallGuard = new NetGuard(0.5f, 2, 64);
        if (!truckCallGuard.Allow(sender, 40, Time.realtimeSinceStartupAsDouble))
        {
            TruckCallReplyRpc(false, Text128(UiText.Pause.TruckTooFast), RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }
        bool ok = TruckCall.ServerCall(sender, player, root, yawDeg, out string reply);
        TruckCallReplyRpc(ok, Text128(reply), RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void TruckCallReplyRpc(bool ok, FixedString128Bytes text, RpcParams p = default) => Unstuck.OnCallReply(ok, text.ToString());

    /// <summary>Host: tell every other machine a delivery of <paramref name="truck"/> started (or ended) at <paramref name="at"/>.</summary>
    public void BroadcastTow(TruckControllerV2 truck, bool started, bool free, Vector3 at)
    {
        if (!IsServer || !IsSpawned || truck == null) return;
        var no = truck.GetComponent<NetworkObject>();
        if (no == null || !no.IsSpawned) return;
        TowDroneRpc(new NetworkObjectReference(no), started, free, at);
    }

    [Rpc(SendTo.NotServer)]
    void TowDroneRpc(NetworkObjectReference truck, bool started, bool free, Vector3 at, RpcParams p = default)
    {
        if (!truck.TryGet(out NetworkObject no, NetworkManager)) return;
        var rec = no.GetComponent<TruckRecovery>();
        if (rec == null) rec = no.gameObject.AddComponent<TruckRecovery>(); // ShopEffectsRunner adds it every half second; this is not worth waiting for
        if (started) rec.ObserveTow(at, free);
        else rec.ObserveTowEnd(at);
    }

    static FixedString128Bytes Text128(string s)
    {
        var f = new FixedString128Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }
}

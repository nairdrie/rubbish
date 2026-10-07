using Unity.Netcode;
using UnityEngine;

// Pole reach uses the ordinary host ownership/shared-grip transaction, with its own geometry gate.
public partial class HandsNet
{
    readonly System.Collections.Generic.Dictionary<ulong, float> poleAt = new System.Collections.Generic.Dictionary<ulong, float>();

    public bool LocalPoleGrab(Carryable item)
    {
        if (item == null || !GrabberPole.Owned) return false;
        string key = item is TrashBag bag && bag.IsCurbBag ? bag.Contents?.bagId : null;
        if (item.NetId == 0 && IsServer) HostPublishNow(item);
        if (!IsServer && item.NetId == 0)
        {
            if (string.IsNullOrEmpty(key)) { GameHUD.Toast("Item still arriving — try again.", ToastStyle.Info); return false; }
            pendingCurb[key] = item; // adopt this curb copy on publication, never leave a duplicate
        }
        if (IsServer) HostPoleGrab(LocalId, item.NetId, key, item.WorldCenter);
        else PoleGrabRpc(item.NetId, Fixed64(key ?? ""), item.WorldCenter);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PoleGrabRpc(uint id, Unity.Collections.FixedString64Bytes curbKey, Vector3 at, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed("rb.hands.grab", sender, 80)) return;
        HostPoleGrab(sender, id, curbKey.Length > 0 ? curbKey.ToString() : null, at);
    }

    void HostPoleGrab(ulong client, uint id, string curbKey, Vector3 at)
    {
        if (!IsServer || !HostPlayerPos(client, out var feet)) return;
        if (!BusinessUpgradeNet.HasGrabberPoleForClient(client))
        { HostLose(client, id, curbKey, "You need a grabber pole. If you just bought it, try again in a moment."); return; }
        float now = Time.unscaledTime;
        if (poleAt.TryGetValue(client, out var used) && now - used < GrabberPole.Cooldown - 0.08f) return;
        var player = PlayerBody.For(client);
        var body = BodyOf(client);
        if (player == null || player.Player == null || player.Player.IsDriving || player.Player.IsPlayingChess ||
            body.State != BodyState.Normal || body.carrying != None || TugOf(client).HasValue || HostHolding(client, null) ||
            CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client) ||
            player.Carrier != null && player.Carrier.ExternalControl)
        { HostLose(client, id, curbKey, "Hands busy!"); return; }
        var nc = Get(id);
        if (nc == null && !string.IsNullOrEmpty(curbKey))
        {
            if (byBagId.TryGetValue(curbKey, out var published)) nc = Get(published);
            if (nc == null) nc = HostPublishCurb(curbKey, at);
        }
        if (nc == null || nc.Item == null) return;
        Vector3 hand = feet + Vector3.up * 1.1f;
        float reach = nc.St.Phase == HandsPhase.Held || nc.St.Phase == HandsPhase.Tug ? GrabberPole.SnatchRange : GrabberPole.ReachGrab;
        if (!PoleTargeting.WithinReach(hand, nc.Item.WorldCenter, reach + 0.6f) ||
            !PoleTargeting.ClearPath(hand, nc.Item.WorldCenter, player.transform, nc.Item.transform))
        { HostLose(client, nc.Id, curbKey, "Pole can't reach through that wall."); return; }
        poleAt[client] = now;
        HostGrab(client, nc.Id, null, ServerNow, nc.Item.transform.position);
    }
}

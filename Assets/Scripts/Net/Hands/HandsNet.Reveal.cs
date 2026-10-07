using Unity.Netcode;
using UnityEngine;

/// <summary>Finder attribution and reveal state for networked loose items.</summary>
public partial class HandsNet
{
    int applyingReveal;

    public void NotifyItemRevealed(LooseItem li)
    {
        if (li == null || li.NetId == 0 || !IsSpawned || !active || applyingReveal > 0) return;
        var nc = Get(li.NetId);
        if (nc == null || nc.Item != li) return;
        if (!IsServer)
        {
            RevealRequestRpc(li.NetId, li.FinderClientId);
            return;
        }
        if (nc.Desc.revealed) return;
        nc.MarkRevealed(li.FinderClientId);
        RevealRpc(li.NetId, li.FinderClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RevealRequestRpc(uint id, ulong finderClientId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!Allowed("rb.hands.reveal", sender, 24)) return;
        var nc = Get(id);
        if (nc == null || !(nc.Item is LooseItem li) || nc.Desc.revealed) return;
        if (finderClientId != sender && finderClientId != FeedbackAudience.UnknownFinder &&
            finderClientId != nc.Desc.finderClientId) return;
        if (!HostPlayerPos(sender, out var playerPos) || (playerPos - li.transform.position).sqrMagnitude > 16f * 16f) return;
        if (!Permissions.CanWorkIn(sender, DepotRowLayout.InteriorAt(li.transform.position))) return;
        li.FinderClientId = finderClientId;
        li.Reveal(); // host broadcast is triggered by Reveal exactly once
    }

    [Rpc(SendTo.NotServer)]
    void RevealRpc(uint id, ulong finderClientId)
    {
        var nc = Get(id);
        if (nc == null || !(nc.Item is LooseItem li)) return;
        ApplyNetworkReveal(nc, li, finderClientId);
    }

    void ApplyNetworkReveal(NetCarry nc, LooseItem li, ulong finderClientId)
    {
        if (nc == null || li == null) return;
        nc.MarkRevealed(finderClientId);
        li.FinderClientId = finderClientId;
        if (li.Revealed) return;
        applyingReveal++;
        try { li.Reveal(); }
        finally { applyingReveal--; }
    }
}

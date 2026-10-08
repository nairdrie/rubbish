using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Networking for the Raccoon calling, as partials of two session components that are already on the NetSession prefab
// (no new [NetSessionComponent], no prefab change):
//   BusinessRegistry: the hutch roster rides in the business's replicated workshop state (WorkshopState.hutch).
//   NightNet: one guarded request RPC (guest -> host), one cosmetic broadcast, one private toast.

public partial class BusinessRegistry
{
    /// <summary>A business's encoded hutch roster straight from the replicated state (no clone). Null while unknown.</summary>
    internal string HutchRaw(string businessId) =>
        !string.IsNullOrEmpty(businessId) && workshopCache.TryGetValue(businessId, out var state) ? state.hutch ?? "" : null;

    /// <summary>Host: change one business's hutch and publish it (all or nothing). <paramref name="edit"/> returns false to
    /// abandon the change. Never reachable from an RPC without the caller's own validation.</summary>
    internal bool HutchEdit(string businessId, Func<HutchState, bool> edit)
    {
        if (!IsServer || edit == null || !Businesses.TryGet(businessId, out var business)) return false;
        var state = WorkshopSnapshot(businessId);
        if (state == null) return false;
        var hutch = HutchState.Parse(state.hutch) ?? new HutchState();
        if (!edit(hutch) || !hutch.Valid()) return false;
        string encoded = hutch.Encode();
        if (encoded.Length > HutchState.MaxChars) return false;
        state.hutch = encoded;
        if (!state.Valid()) return false;
        PublishWorkshop(business, state);
        return true;
    }
}

public partial class NightNet
{
    const string MsgRaccoon = "rb.night.raccoon";

    /// <summary>The local player asks the host for something (hutch commands, the feral pounce). Solo: decided here.</summary>
    public static void RaccoonAsk(byte op, int a, int b, string text = null)
    {
        var n = Instance;
        if (!Streaming)
        {
            if (!GameSession.IsConnectedGuest) RaccoonCalling.HostAsk(Permissions.LocalClientId, op, a, b, text ?? "");
            return;
        }
        if (n.IsServer) { RaccoonCalling.HostAsk(n.LocalId, op, a, b, text ?? ""); return; }
        var t = new FixedString32Bytes();
        t.CopyFromTruncated(text ?? "");
        n.RaccoonAskRpc(op, a, b, t);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RaccoonAskRpc(byte op, int a, int b, FixedString32Bytes text, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgRaccoon).Allow(sender, 48, Time.realtimeSinceStartupAsDouble)) return;
        RaccoonCalling.HostAsk(sender, op, a, b, text.ToString());
    }

    /// <summary>Host: a cosmetic moment everybody sees (a companion's fetch run, a pounce, the King's line).</summary>
    public static void RaccoonFx(byte kind, ulong who, Vector3 a, Vector3 b, string text = null)
    {
        RaccoonCalling.PlayFx(kind, who, a, b, text ?? "");
        var n = Instance;
        if (!Streaming || !n.IsServer || !n.AnyGuest) return;
        var t = new FixedString128Bytes();
        t.CopyFromTruncated(text ?? "");
        n.RaccoonFxRpc(kind, who, a, b, t);
    }

    [Rpc(SendTo.NotServer)]
    void RaccoonFxRpc(byte kind, ulong who, Vector3 a, Vector3 b, FixedString128Bytes text)
    {
        if (IsServer) return;
        RaccoonCalling.PlayFx(kind, who, a, b, text.ToString());
    }

    /// <summary>Host: a toast for one player (the answer to their request, their trap's raccoon moving in).</summary>
    public static void RaccoonTell(ulong client, bool good, string text)
    {
        var n = Instance;
        if (!Streaming || client == n.LocalId) { GameHUD.Toast(text, good ? ToastStyle.Good : ToastStyle.Bad, 3.5f); return; }
        if (!n.IsServer) return;
        var t = new FixedString512Bytes();
        t.CopyFromTruncated(text ?? "");
        n.RaccoonTellRpc(good, t, n.RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RaccoonTellRpc(bool good, FixedString512Bytes text, RpcParams rp = default) =>
        GameHUD.Toast(text.ToString(), good ? ToastStyle.Good : ToastStyle.Bad, 3.5f);
}

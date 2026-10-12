using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The guest's line to the house (Docs/Casino.md §8.5, §10; Docs/Multiplayer.md §9 rules 10, 15, 16). A guest ASKS through a
/// guarded RPC (<c>rb.casino.*</c>), the host decides in <see cref="CasinoTables"/> / <see cref="CasinoBank"/> and answers that
/// one client with a <see cref="CasinoResult"/>. Table STATE is never asked for: the host broadcasts a public snapshot per
/// table whenever it changes, plus every slot spin and occupancy change, and a client that enters the casino asks once for
/// the current picture (<see cref="CasinoClient.RequestSync"/>). The host's own player and a solo game never use the RPCs;
/// <see cref="CasinoClient"/> routes them straight to <see cref="CasinoHost"/>.
/// <para>Session component: the prefab gets it when Rubbish/Setup/Coop is run. Until then <see cref="Instance"/> is null
/// and a guest is told the house isn't answering; solo and the host are unaffected.</para>
/// <para>The shared-table RPCs are in <c>CasinoNet.Tables.cs</c>. Changing any RPC changes the wire: bump
/// <see cref="NetVersion.Protocol"/> (not yet: the feature is unreleased and stays inside protocol 21).</para>
/// </summary>
[NetSessionComponent(Order = 47)]
public sealed partial class CasinoNet : NetworkBehaviour
{
    public static CasinoNet Instance { get; private set; }

    public const string MsgBar = "rb.casino.bar";
    /// <summary>A guest gives up on a reply after this long (real seconds).</summary>
    public const float ReplyTimeout = 8f;

    bool hooked;

    // guest-side: the bar has one order in flight
    Action<BarView> barDone;
    byte barTicket, ticket;
    float barAt;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (!IsServer) return;
        SessionService.SetGuard(MsgBar, new NetGuard(2f, 4, 8));
        SetTableGuards();
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback += ClientLeft;
        CasinoHost.BigWinToBroadcast += Broadcast;
        CasinoHost.BarServedToBroadcast += BroadcastBar;
        HookTables();
        hooked = true;
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        FailPending();
        CasinoClient.ClearReplicated();
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hooked) return;
        hooked = false;
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= ClientLeft;
        CasinoHost.BigWinToBroadcast -= Broadcast;
        CasinoHost.BarServedToBroadcast -= BroadcastBar;
        UnhookTables();
    }

    void ClientLeft(ulong client)
    {
        if (!IsServer) return;
        CasinoHost.ForgetClient(client);
        ForgetClientGuards(client);
    }

    bool Allowed(string msg, ulong sender, int bytes)
    {
        if (!IsServer) return false;
        if (SessionService.GuardFor(msg).Allow(sender, bytes, Time.realtimeSinceStartupAsDouble)) return true;
        NetAudit.Event("guard", $"{msg} from client {sender} over its limit");
        return false;
    }

    RpcParams To(ulong client) => RpcTarget.Single(client, RpcTargetUse.Temp);

    byte NextTicket()
    {
        ticket++;
        if (ticket == 0) ticket = 1; // 0 means "nobody asked"
        return ticket;
    }

    void Update()
    {
        if (!IsSpawned || IsServer) return;
        float now = Time.unscaledTime;
        if (barDone != null && now - barAt > ReplyTimeout) { var d = barDone; barDone = null; Call(d, new BarView { error = CasinoError.Unavailable, cashCents = -1 }); }
        SweepPending(now);
    }

    void FailPending()
    {
        var d = barDone;
        barDone = null;
        if (d != null) Call(d, new BarView { error = CasinoError.Unavailable, cashCents = -1 });
        FailTablePending();
    }

    static void Call<T>(Action<T> done, T view)
    {
        if (done == null) return;
        try { done(view); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ================================================================================================ the bar

    public void AskDrink(int drink, Action<BarView> done)
    {
        if (!IsSpawned || IsServer) { Call(done, new BarView { error = CasinoError.Unavailable, cashCents = -1 }); return; }
        if (barDone != null) { Call(done, new BarView { error = CasinoError.Busy, cashCents = -1 }); return; }
        barDone = done; barAt = Time.unscaledTime; barTicket = NextTicket();
        DrinkRpc(barTicket, (byte)Mathf.Clamp(drink, 0, 255));
    }

    /// <summary>Validates: guard (2/s, burst 4); then <see cref="CasinoBank.Drink"/>: a real drink id, caller at the bar,
    /// the bartender's cut-off (alcohol only) against the caller's replicated drunk level, and the caller's business can
    /// cover the price.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DrinkRpc(byte askTicket, byte drink, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed(MsgBar, sender, 2)) return;
        DrinkReplyRpc(askTicket, CasinoHost.Drink(sender, drink), To(sender));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void DrinkReplyRpc(byte askTicket, BarView view, RpcParams rp = default)
    {
        if (IsServer || barDone == null || askTicket != barTicket) return;
        var d = barDone; barDone = null;
        Call(d, view);
    }

    // ================================================================================================ big wins

    static FixedString64Bytes F64(string s) { var f = new FixedString64Bytes(); f.CopyFromTruncated(s ?? ""); return f; }

    /// <summary>Host: tell every guest (their own machine decides whether it is in earshot).</summary>
    void Broadcast(ulong winner, string who, string what, long cents)
    {
        if (!IsSpawned || !IsServer) return;
        BigWinRpc(winner, F64(who), F64(what), cents);
    }

    /// <summary>Host → guests only (a guest cannot invoke it). Presentation: a toast for players inside the casino.</summary>
    [Rpc(SendTo.NotServer)]
    void BigWinRpc(ulong winner, FixedString64Bytes who, FixedString64Bytes what, long cents) =>
        CasinoHost.HearBigWin(winner, who.ToString(), what.ToString(), cents);

    // ================================================================================================ drinks served (presentation)

    /// <summary>Host: a drink was paid for; tell every guest so they see the bartender pour it (their own Drink reply is unchanged).</summary>
    void BroadcastBar(ulong who, int drink, int spot)
    {
        if (!IsSpawned || !IsServer) return;
        BarServedRpc(who, (byte)Mathf.Clamp(drink, 0, 255), (byte)Mathf.Clamp(spot, 0, 255));
    }

    /// <summary>Host → guests only (a guest cannot invoke it, and the host ignores it). Presentation: the bartender pours and slides a glass to <paramref name="spot"/>.</summary>
    [Rpc(SendTo.NotServer)]
    void BarServedRpc(ulong who, byte drink, byte spot)
    {
        if (IsServer) return;
        CasinoHost.HearBarServed(who, drink, spot);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
    }
}

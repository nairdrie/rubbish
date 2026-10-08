using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Scavenger calling (Docs/Progression.md "Callings: Scavenger"): the guest's side of every host decision. A guest ASKS
// (rate- and size-limited by NetGuard), the host answers that one client. All rules live in ScavengerHost; this file only
// carries them. The host's own player never uses these: Scavenger calls ScavengerHost directly.
public partial class HandsNet
{
    bool scavGuards;

    bool ScavAllowed(string guard, ulong sender, int bytes)
    {
        if (!IsServer) return false;
        if (!scavGuards)
        {
            scavGuards = true;
            SessionService.SetGuard(ScavengerIds.GuardPeek, new NetGuard(4f, 8, 96));
            SessionService.SetGuard(ScavengerIds.GuardCall, new NetGuard(0.5f, 2, 96));
            SessionService.SetGuard(ScavengerIds.GuardCase, new NetGuard(2f, 4, 64));
            SessionService.SetGuard(ScavengerIds.GuardBreak, new NetGuard(2f, 4, 64));
            SessionService.SetGuard(ScavengerIds.GuardFence, new NetGuard(3f, 6, 2048));
            NetworkManager.OnClientDisconnectCallback -= ScavengerHost.ForgetClient;
            NetworkManager.OnClientDisconnectCallback += ScavengerHost.ForgetClient;
        }
        return Allowed(guard, sender, bytes);
    }

    RpcParams ScavTo(ulong client) => RpcTarget.Single(client, RpcTargetUse.Temp);
    static string ScavText(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);

    // ------------------------------------------------------------------ x-ray specs

    public void ScavAskPeek(string bagId, uint netId) { if (IsSpawned && !IsServer) ScavPeekRpc(Fixed64(bagId), netId); }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavPeekRpc(FixedString64Bytes bagId, uint netId, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!ScavAllowed(ScavengerIds.GuardPeek, sender, 72)) return;
        var s = ScavengerHost.Peek(sender, bagId.ToString(), netId);
        ScavPeekReplyRpc(bagId, s.level, s.counts, s.hasValuable, Fixed64(s.bestDefId), ScavTo(sender));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ScavPeekReplyRpc(FixedString64Bytes bagId, byte level, uint counts, bool hasValuable, FixedString64Bytes best, RpcParams rpc = default)
    {
        if (IsServer) return;
        Scavenger.OnPeek(bagId.ToString(), new XRaySummary { level = level, counts = counts, hasValuable = hasValuable, bestDefId = best.ToString() });
    }

    // ------------------------------------------------------------------ call it

    public void ScavAskCall(string bagId, uint netId, int band) { if (IsSpawned && !IsServer) ScavCallRpc(Fixed64(bagId), netId, (byte)Mathf.Clamp(band, 0, 255)); }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavCallRpc(FixedString64Bytes bagId, uint netId, byte band, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!ScavAllowed(ScavengerIds.GuardCall, sender, 73)) return;
        var r = ScavengerHost.CallIt(sender, bagId.ToString(), netId, band);
        ScavCallReplyRpc(r.status, r.called, r.actual, r.bonusCents, Fixed64(r.why), ScavTo(sender));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ScavCallReplyRpc(byte status, byte called, byte actual, long bonusCents, FixedString64Bytes why, RpcParams rpc = default)
    {
        if (IsServer) return;
        Scavenger.OnCall(new CallResult { status = status, called = called, actual = actual, bonusCents = bonusCents, why = why.ToString() });
    }

    // ------------------------------------------------------------------ burglary

    public void ScavAskCase(string stopId) { if (IsSpawned && !IsServer) ScavCaseRpc(Fixed64(stopId)); }
    public void ScavTellBreakStart(string stopId) { if (IsSpawned && !IsServer) ScavBreakStartRpc(Fixed64(stopId)); }
    public void ScavAskBreakFinish(string stopId, int misses, int hotHeld)
    { if (IsSpawned && !IsServer) ScavBreakFinishRpc(Fixed64(stopId), (byte)Mathf.Clamp(misses, 0, 255), (byte)Mathf.Clamp(hotHeld, 0, 255)); }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavCaseRpc(FixedString64Bytes stopId, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!ScavAllowed(ScavengerIds.GuardCase, sender, 64)) return;
        var c = ScavengerHost.Case(sender, stopId.ToString());
        ScavCaseReplyRpc(stopId, (byte)c.status, c.awake, c.asleep, c.street, c.players, c.dogs, c.risk, c.lockOdds, c.richTier, c.readyInDays, c.suspicion, ScavTo(sender));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ScavCaseReplyRpc(FixedString64Bytes stopId, byte status, byte awake, byte asleep, byte street, byte players, byte dogs, byte risk, byte lockOdds,
        byte richTier, byte readyInDays, byte suspicion, RpcParams rpc = default)
    {
        if (IsServer) return;
        Scavenger.OnCase(stopId.ToString(), new CaseInfo
        {
            status = (CaseStatus)status, awake = awake, asleep = asleep, street = street, players = players, dogs = dogs, risk = risk,
            lockOdds = lockOdds, richTier = richTier, readyInDays = readyInDays, suspicion = suspicion,
        });
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavBreakStartRpc(FixedString64Bytes stopId, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!ScavAllowed(ScavengerIds.GuardBreak, sender, 64)) return;
        ScavengerHost.BreakStart(sender, stopId.ToString());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavBreakFinishRpc(FixedString64Bytes stopId, byte misses, byte hotHeld, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!ScavAllowed(ScavengerIds.GuardBreak, sender, 66)) return;
        var r = ScavengerHost.BreakFinish(sender, stopId.ToString(), misses, hotHeld);
        ScavBreakReplyRpc(stopId, (byte)r.outcome, Fixed64(r.districtId), ScavText(r.why, 120), ScavText(r.lootJson, 6000), r.fineCents,
            ScavText(r.letterSubject, 120), ScavText(r.letterBody, 900), r.readyInDays, ScavTo(sender));
        if (r.outcome == BreakInOutcome.Caught || r.outcome == BreakInOutcome.Spotted) ScavAlarm(sender, r.stopId);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ScavBreakReplyRpc(FixedString64Bytes stopId, byte outcome, FixedString64Bytes districtId, string why, string lootJson, long fineCents,
        string letterSubject, string letterBody, byte readyInDays, RpcParams rpc = default)
    {
        if (IsServer) return;
        Scavenger.OnBreakIn(new BreakInResult
        {
            outcome = (BreakInOutcome)outcome, stopId = stopId.ToString(), districtId = districtId.ToString(), why = why ?? "", lootJson = lootJson ?? "",
            fineCents = fineCents, letterSubject = letterSubject ?? "", letterBody = letterBody ?? "", readyInDays = readyInDays,
        });
    }

    /// <summary>Host: everybody near the door hears about it (presentation only).</summary>
    public void ScavAlarm(ulong burglar, string stopId)
    {
        if (!ScavHouses.TryDoor(stopId, out var door)) return;
        string who = NameOf(burglar);
        Scavenger.PlayAlarm(door, who, burglar);
        if (IsSpawned && IsServer && active) ScavAlarmRpc(door, Fixed64(who), burglar);
    }

    [Rpc(SendTo.NotServer)]
    void ScavAlarmRpc(Vector3 door, FixedString64Bytes who, ulong burglar) => Scavenger.PlayAlarm(door, who.ToString(), burglar);

    // ------------------------------------------------------------------ the fence

    public void ScavAskFence(string itemJson) { if (IsSpawned && !IsServer && !string.IsNullOrEmpty(itemJson)) ScavFenceRpc(itemJson); }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ScavFenceRpc(string itemJson, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (itemJson == null || !ScavAllowed(ScavengerIds.GuardFence, sender, itemJson.Length * 2)) return;
        ItemInstance item = null;
        try { item = JsonUtility.FromJson<ItemInstance>(itemJson); } catch (System.Exception) { }
        var r = ScavengerHost.FenceSell(sender, item);
        ScavFenceReplyRpc(Fixed64(r.uid), r.ok, r.cents, Fixed64(r.why), ScavTo(sender));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ScavFenceReplyRpc(FixedString64Bytes uid, bool ok, long cents, FixedString64Bytes why, RpcParams rpc = default)
    {
        if (IsServer) return;
        Scavenger.OnFence(new FenceResult { uid = uid.ToString(), ok = ok, cents = cents, why = why.ToString() });
    }
}

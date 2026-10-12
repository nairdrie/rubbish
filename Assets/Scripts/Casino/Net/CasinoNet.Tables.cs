using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// The shared-table half of CasinoNet (Docs/Casino.md §10). Guest -> host: intents, each answered with one CasinoResult sent to
// that client alone. Host -> clients: public table snapshots, slot spins, slot occupancy, per-player notices, the sync reply.
public sealed partial class CasinoNet
{
    // ---- guard names (one NetGuard each: per-client token bucket + max payload bytes)
    public const string MsgSeat = "rb.casino.seat", MsgPlay = "rb.casino.play", MsgBets = "rb.casino.bets",
        MsgSlot = "rb.casino.slot", MsgSync = "rb.casino.sync";
    /// <summary>The most replies a guest may wait for at once.</summary>
    public const int MaxPending = 24;

    static readonly string[] tableGuards = { MsgSeat, MsgPlay, MsgBets, MsgSlot, MsgSync };

    struct Pending { public Action<CasinoResult> done; public float at; }
    readonly Dictionary<byte, Pending> pending = new Dictionary<byte, Pending>();
    readonly List<byte> scratchTickets = new List<byte>();
    /// <summary>Refusal replies are rate limited too, so a flooding client can't make the host answer every packet.</summary>
    NetGuard refusalGuard = new NetGuard(4f, 4, 64);
    CasinoTables hookedTables;

    // ================================================================================================ host: guards and hooks

    void SetTableGuards()
    {
        SessionService.SetGuard(MsgSeat, new NetGuard(2f, 6, 16));
        SessionService.SetGuard(MsgPlay, new NetGuard(6f, 12, 16));
        SessionService.SetGuard(MsgBets, new NetGuard(4f, 8, RouletteRules.MaxBetsPerSpin * CasinoWire.BetBytes + 16));
        SessionService.SetGuard(MsgSlot, new NetGuard(1f, 2, 16));
        SessionService.SetGuard(MsgSync, new NetGuard(0.5f, 2, 8));
        refusalGuard = new NetGuard(4f, 4, 64);
    }

    void ForgetClientGuards(ulong client)
    {
        for (int i = 0; i < tableGuards.Length; i++) SessionService.GuardFor(tableGuards[i]).Forget(client);
        refusalGuard.Forget(client);
    }

    void HookTables()
    {
        var t = CasinoHost.Tables;
        hookedTables = t;
        t.BlackjackChanged += OnBlackjackChanged;
        t.RouletteChanged += OnRouletteChanged;
        t.SlotSpun += OnSlotSpun;
        t.SlotOccupancyChanged += OnSlotOccupancy;
        t.NoticeSent += OnNotice;
    }

    void UnhookTables()
    {
        var t = hookedTables;
        hookedTables = null;
        if (t == null) return;
        t.BlackjackChanged -= OnBlackjackChanged;
        t.RouletteChanged -= OnRouletteChanged;
        t.SlotSpun -= OnSlotSpun;
        t.SlotOccupancyChanged -= OnSlotOccupancy;
        t.NoticeSent -= OnNotice;
    }

    /// <summary>Guard check; a refused request is answered Throttled (itself rate limited) so the caller's callback doesn't wait for the timeout.</summary>
    bool Admit(string msg, ulong sender, int bytes, byte askTicket)
    {
        if (!IsServer) return false;
        if (Allowed(msg, sender, bytes)) return true;
        if (askTicket != 0 && refusalGuard.Allow(sender, 1, Time.realtimeSinceStartupAsDouble))
            Reply(sender, askTicket, CasinoResult.Fail(CasinoError.Throttled));
        return false;
    }

    void Reply(ulong sender, byte askTicket, CasinoResult r)
    {
        if (!IsServer || NetworkManager == null || !NetworkManager.ConnectedClients.ContainsKey(sender)) return;
        if (sender == NetworkManager.LocalClientId) return; // the host's own player never goes through the RPCs
        ResultRpc(askTicket, r, To(sender));
    }

    // ================================================================================================ host -> clients

    void OnBlackjackChanged(BlackjackTableSnapshot snap, byte[] bytes)
    {
        if (!IsSpawned || !IsServer) return;
        BlackjackSnapshotRpc(bytes);
    }

    void OnRouletteChanged(RouletteTableSnapshot snap, byte[] bytes)
    {
        if (!IsSpawned || !IsServer) return;
        RouletteSnapshotRpc(bytes);
    }

    void OnSlotSpun(SlotEventInfo e)
    {
        if (!IsSpawned || !IsServer) return;
        SlotEventRpc(e.machine, e.stop0, e.stop1, e.stop2, e.rule, e.winCents, e.stakeCents, e.key, e.start, e.big);
    }

    void OnSlotOccupancy(int machine, int key)
    {
        if (!IsSpawned || !IsServer) return;
        SlotOccupancyRpc((byte)machine, key);
    }

    void OnNotice(ulong client, string table, CasinoNotice notice)
    {
        if (!IsSpawned || !IsServer || NetworkManager == null) return;
        if (client == NetworkManager.LocalClientId || !NetworkManager.ConnectedClients.ContainsKey(client)) return;
        int wire = CasinoTableIds.ToWire(table);
        if (wire < 0) return;
        NoticeRpc((byte)wire, (byte)notice, To(client));
    }

    /// <summary>Host -> guests only. The public blackjack snapshot (<see cref="BlackjackTableSnapshot.Serialize"/>): no shoe, no hole card until revealed. Always the whole table; the newest wins (reliable, ordered).</summary>
    [Rpc(SendTo.NotServer)]
    void BlackjackSnapshotRpc(byte[] bytes) => CasinoClient.OnWireBlackjack(bytes);

    /// <summary>Host -> guests only. The public roulette snapshot.</summary>
    [Rpc(SendTo.NotServer)]
    void RouletteSnapshotRpc(byte[] bytes) => CasinoClient.OnWireRoulette(bytes);

    /// <summary>Host -> guests only. One slot pull: the real stops, the line, who pulled, and the table time it starts.</summary>
    [Rpc(SendTo.NotServer)]
    void SlotEventRpc(byte machine, byte stop0, byte stop1, byte stop2, sbyte rule, int winCents, int stakeCents, int key, double start, bool big) =>
        CasinoClient.OnWireSlotSpun(new SlotEventInfo
        {
            machine = machine, stop0 = stop0, stop1 = stop1, stop2 = stop2, rule = rule, winCents = winCents,
            stakeCents = stakeCents, key = key, start = start, big = big,
        });

    /// <summary>Host -> guests only. A slot machine's occupant changed (-1 = free).</summary>
    [Rpc(SendTo.NotServer)]
    void SlotOccupancyRpc(byte machine, int key) => CasinoClient.OnWireSlotOccupancy(machine, key);

    /// <summary>Host -> one guest. Something the host did to this player (cancelled bets, stood up, idle release).</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(byte table, byte notice, RpcParams rp = default)
    {
        if (IsServer) return;
        string id = CasinoTableIds.FromWire(table);
        if (id == null) return;
        CasinoClient.OnWireNotice(id, (CasinoNotice)notice);
    }

    /// <summary>Host -> one guest: the answer to an intent (matched by ticket).</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void ResultRpc(byte askTicket, CasinoResult result, RpcParams rp = default)
    {
        if (IsServer || !pending.TryGetValue(askTicket, out var p)) return;
        pending.Remove(askTicket);
        CallResult(p.done, result);
    }

    // ================================================================================================ guest: bookkeeping

    static void CallResult(Action<CasinoResult> done, CasinoResult r)
    {
        if (done == null) return;
        try { done(r); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Reserve a ticket for a callback. 0 means the callback already ran (not connected, or too many requests in flight).</summary>
    byte Begin(Action<CasinoResult> done)
    {
        if (!IsSpawned || IsServer) { CallResult(done, CasinoResult.Fail(CasinoError.Unavailable)); return 0; }
        if (pending.Count >= MaxPending) { CallResult(done, CasinoResult.Fail(CasinoError.Busy)); return 0; }
        byte t = 0;
        for (int guard = 0; guard < 256; guard++)
        {
            t = NextTicket();
            if (!pending.ContainsKey(t)) break;
        }
        pending[t] = new Pending { done = done, at = Time.unscaledTime };
        return t;
    }

    void SweepPending(float now)
    {
        if (pending.Count == 0) return;
        scratchTickets.Clear();
        foreach (var kv in pending) if (now - kv.Value.at > ReplyTimeout) scratchTickets.Add(kv.Key);
        for (int i = 0; i < scratchTickets.Count; i++)
        {
            var p = pending[scratchTickets[i]];
            pending.Remove(scratchTickets[i]);
            CallResult(p.done, CasinoResult.Fail(CasinoError.Unavailable));
        }
        scratchTickets.Clear();
    }

    void FailTablePending()
    {
        if (pending.Count == 0) return;
        var all = new List<Pending>(pending.Values);
        pending.Clear();
        for (int i = 0; i < all.Count; i++) CallResult(all[i].done, CasinoResult.Fail(CasinoError.Unavailable));
    }

    // ================================================================================================ guest -> host: intents

    /// <summary>Guest: sit at a table or machine (<see cref="CasinoTableIds"/>). <paramref name="done"/> always runs exactly once.</summary>
    public void AskSit(string table, int preferredSeat, Action<CasinoResult> done)
    {
        int wire = CasinoTableIds.ToWire(table);
        if (wire < 0) { CallResult(done, CasinoResult.Fail(CasinoError.BadMachine)); return; }
        byte t = Begin(done);
        if (t != 0) SitRpc(t, (byte)wire, (sbyte)Mathf.Clamp(preferredSeat, -1, 126));
    }

    public void AskLeave(string table, Action<CasinoResult> done)
    {
        int wire = CasinoTableIds.ToWire(table);
        if (wire < 0) { CallResult(done, CasinoResult.Fail(CasinoError.BadMachine)); return; }
        byte t = Begin(done);
        if (t != 0) LeaveRpc(t, (byte)wire);
    }

    public void AskBlackjackBet(long cents, Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) BlackjackBetRpc(t, (int)Math.Max(0, Math.Min(int.MaxValue, cents)));
    }

    public void AskBlackjackSitOut(Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) BlackjackSitOutRpc(t);
    }

    public void AskBlackjackAct(BlackjackAction action, Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) BlackjackActRpc(t, (byte)action);
    }

    public void AskRouletteJoin(Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) RouletteJoinRpc(t);
    }

    public void AskRouletteSetBets(RouletteBet[] bets, int count, Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) RouletteSetBetsRpc(t, CasinoWire.PackBets(bets, count));
    }

    public void AskRouletteReady(bool ready, Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) RouletteReadyRpc(t, ready);
    }

    public void AskSlotSpin(int machine, Action<CasinoResult> done)
    {
        byte t = Begin(done);
        if (t != 0) SlotSpinRpc(t, (byte)Mathf.Clamp(machine, 0, 255));
    }

    /// <summary>Guest: ask for the current picture (both snapshots and the slot occupants). The answer arrives through <see cref="CasinoClient"/>'s events.</summary>
    public void AskSync()
    {
        if (!IsSpawned || IsServer) return;
        SyncRequestRpc();
    }

    // ================================================================================================ host: intent handlers

    /// <summary>Validates: guard (2/s, burst 6); a real table id; then <see cref="CasinoTables.Sit"/>: caller within 5 m of the
    /// station, has a business, the seat is free (blackjack: the preferred seat if free, else the lowest free; roulette: a chip
    /// colour, 4 at most; slot: unoccupied). Sitting stands the caller up from every other station.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SitRpc(byte askTicket, byte table, sbyte preferredSeat, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgSeat, sender, 4, askTicket)) return;
        string id = CasinoTableIds.FromWire(table);
        Reply(sender, askTicket, id == null ? CasinoResult.Fail(CasinoError.BadMachine) : CasinoHost.Tables.Sit(sender, id, preferredSeat, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard; a real table id. Always allowed for a seated player (no range check).</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void LeaveRpc(byte askTicket, byte table, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgSeat, sender, 3, askTicket)) return;
        string id = CasinoTableIds.FromWire(table);
        Reply(sender, askTicket, id == null ? CasinoResult.Fail(CasinoError.BadMachine) : CasinoHost.Tables.Leave(sender, id, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard (6/s, burst 12, shared by the play RPCs); caller seated at the table and within range; phase and
    /// deadline; whole dollars $5 to $200; one stake per seat; the caller can cover it. Charged only if all of that passed.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BlackjackBetRpc(byte askTicket, int cents, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgPlay, sender, 5, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.BlackjackBet(sender, cents, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard; caller seated; Betting phase, before a stake is down.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BlackjackSitOutRpc(byte askTicket, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgPlay, sender, 1, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.BlackjackSitOut(sender, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard; the action byte is a real <see cref="BlackjackAction"/>; the caller is seated, in the round, and
    /// it is their turn (or the insurance window); the action is legal for the hand; the caller can cover its cost (a double, a
    /// split and insurance are charged before the card is drawn). The caller's seat comes from the transport, never the message.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BlackjackActRpc(byte askTicket, byte action, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgPlay, sender, 2, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.BlackjackAct(sender, (BlackjackAction)Math.Min(action, (byte)200), CasinoHost.TableNow));
    }

    /// <summary>Validates: guard (2/s, burst 6); caller within range; the wheel has a free chip colour (4 participants).</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RouletteJoinRpc(byte askTicket, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgSeat, sender, 1, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.RouletteJoin(sender, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard (4/s, burst 8, at most 64 bets of 6 bytes); the packet is whole bets; caller within range and
    /// taking part; Idle / Betting before the deadline; every spot real, whole dollars, spot and spin limits. Replaces the
    /// caller's previous set; nothing is charged until the lock.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RouletteSetBetsRpc(byte askTicket, byte[] raw, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgBets, sender, raw != null ? raw.Length + 1 : 1, askTicket)) return;
        var bets = CasinoWire.UnpackBets(raw);
        if (bets == null)
        {
            var bad = CasinoResult.Of(CasinoTableError.BadBets, -1, raw != null && raw.Length > CasinoWire.BetBytes * RouletteRules.MaxBetsPerSpin ? RouletteBetError.TooManyBets : RouletteBetError.BadSpot);
            Reply(sender, askTicket, bad);
            return;
        }
        Reply(sender, askTicket, CasinoHost.Tables.RouletteSetBets(sender, bets, bets.Length, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard; caller taking part; Betting phase.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RouletteReadyRpc(byte askTicket, bool ready, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgPlay, sender, 2, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.RouletteReady(sender, ready, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard (1/s, burst 2); a real machine; caller within range and its occupant; 1 s since their last pull;
    /// the caller can cover the stake (charged before the reels are drawn). The spin itself reaches everybody by SlotEventRpc.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SlotSpinRpc(byte askTicket, byte machine, RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Admit(MsgSlot, sender, 2, askTicket)) return;
        Reply(sender, askTicket, CasinoHost.Tables.SlotSpin(sender, machine, CasinoHost.TableNow));
    }

    /// <summary>Validates: guard (1 per 2 s, burst 2). Answers this client alone with both snapshots and the slot occupants.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SyncRequestRpc(RpcParams rp = default)
    {
        ulong sender = rp.Receive.SenderClientId;
        if (!Allowed(MsgSync, sender, 1)) return;
        if (NetworkManager == null || sender == NetworkManager.LocalClientId || !NetworkManager.ConnectedClients.ContainsKey(sender)) return;
        var s = CasinoHost.Tables.Sync(CasinoHost.TableNow);
        SyncReplyRpc(s.blackjack, s.roulette, s.slotOccupants, To(sender));
    }

    /// <summary>Host -> one guest: the casino as it is now. A snapshot is the only thing that carries table state; this is two of them plus who sits at the machines.</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void SyncReplyRpc(byte[] blackjack, byte[] roulette, int[] slotOccupants, RpcParams rp = default)
    {
        if (IsServer) return;
        CasinoClient.OnWireSync(blackjack, roulette, slotOccupants);
    }
}

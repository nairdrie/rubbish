using System;
using UnityEngine;

/// <summary>
/// What the in-world casino code calls (Docs/Casino.md §10): the same surface on every machine. On the machine with authority
/// (host, solo, no session) an intent goes straight to <see cref="CasinoHost.Tables"/> for this machine's own player and its
/// callback runs before the call returns; on a guest it goes through <see cref="CasinoNet"/> and the callback runs when the host
/// answers (or with <see cref="CasinoError.Unavailable"/> after <see cref="CasinoNet.ReplyTimeout"/>). Either way the callback
/// runs exactly once, on the main thread, and is optional (null = fire and forget).
/// <para><b>State is event driven.</b> The host broadcasts a public snapshot per table whenever it changes; this class keeps the
/// latest one and raises <see cref="BlackjackSnapshotChanged"/> / <see cref="RouletteSnapshotChanged"/> (the host's own machine
/// gets them too, with offset 0). Call <see cref="RequestSync"/> on entering the casino: it fills <see cref="LatestBlackjack"/>,
/// <see cref="LatestRoulette"/> and <see cref="OccupantOfSlot"/> and raises the events once.</para>
/// <para><b>Table time.</b> Every time in a snapshot or slot event is HOST seconds (the host's real-time clock). A guest maps it to
/// its own <c>Time.realtimeSinceStartupAsDouble</c> with <see cref="ClockOffset"/> (<c>local = host + offset</c>, estimated from the
/// snapshots; 0 on the host and in solo). To animate a spin: <c>t = TableTime() - snapshot.spinStart</c>; for a countdown:
/// <c>left = snapshot.phaseDeadline - TableTime()</c>; or hand the offset to your own clock: <c>localStart = spinStart + offset</c>.</para>
/// </summary>
public static class CasinoClient
{
    static ulong Me => Permissions.LocalClientId;
    static CasinoNet Net => CasinoNet.Instance != null && CasinoNet.Instance.IsSpawned ? CasinoNet.Instance : null;

    // ================================================================================================ events

    /// <summary>The blackjack table changed (or <see cref="RequestSync"/> answered). The offset is <see cref="ClockOffset"/> at that moment.</summary>
    public static event Action<BlackjackTableSnapshot, double> BlackjackSnapshotChanged;
    public static event Action<RouletteTableSnapshot, double> RouletteSnapshotChanged;
    /// <summary>One slot pull anywhere in the room: the real stops, the line, who, and the host time it starts. Fires for the player's own pull too.</summary>
    public static event Action<SlotEventInfo> SlotSpun;
    /// <summary>(machine 0..4, occupant client id or -1, occupant's name or "").</summary>
    public static event Action<int, int, string> SlotOccupancyChanged;
    /// <summary>A big win somewhere in the room: (client id, name, what, cents). Presentation only.</summary>
    public static event Action<int, string, string, long> BigWin;
    /// <summary>The host did something to THIS player: (table id, notice): cancelled bets, stood up, idle release, a timed-out hand.</summary>
    public static event Action<string, CasinoNotice> Notice;
    /// <summary>A drink was served at the bar for somebody (client id, drink id, bar spot 0..2): the bartender pours and a glass slides over. Presentation only.</summary>
    public static event Action<int, int, int> BarServed;

    // ================================================================================================ state

    static CasinoTables attached;
    static BlackjackTableSnapshot bjSnap;
    static RouletteTableSnapshot rlSnap;
    static bool hasBj, hasRl;
    static readonly int[] occupants = { -1, -1, -1, -1, -1 };
    static double offset;
    static bool hasOffset;

    /// <summary>The latest blackjack snapshot (default, with no seats, until <see cref="HasBlackjack"/>).</summary>
    public static BlackjackTableSnapshot LatestBlackjack { get { Attach(); return bjSnap; } }
    public static RouletteTableSnapshot LatestRoulette { get { Attach(); return rlSnap; } }
    public static bool HasBlackjack => hasBj;
    public static bool HasRoulette => hasRl;

    /// <summary>The client id sitting at slot machine <paramref name="machine"/>, or -1.</summary>
    public static int OccupantOfSlot(int machine) { Attach(); return machine >= 0 && machine < occupants.Length ? occupants[machine] : -1; }

    /// <summary>This player's key (client id) in every snapshot and event.</summary>
    public static int LocalKey => (int)Math.Min((ulong)int.MaxValue, Me);

    /// <summary>The seat (blackjack), chip colour (roulette) or 0 (slot machine) this player has at a table id, or -1.</summary>
    public static int LocalSeatAt(string tableId)
    {
        Attach();
        int me = LocalKey;
        if (!CasinoTableIds.TryParse(tableId, out var game, out int index)) return -1;
        switch (game)
        {
            case CasinoGame.Blackjack:
                if (hasBj && bjSnap.seats != null)
                    for (int i = 0; i < bjSnap.seats.Length; i++) if (bjSnap.seats[i].key == me) return i;
                return -1;
            case CasinoGame.Roulette:
                if (hasRl && rlSnap.participants != null)
                    for (int i = 0; i < rlSnap.participants.Length; i++)
                        if (rlSnap.participants[i].key == me && !rlSnap.participants[i].Has(RouletteParticipantSnapshot.FlagAbsent)) return rlSnap.participants[i].colour;
                return -1;
            default:
                return OccupantOfSlot(index) == me ? 0 : -1;
        }
    }

    /// <summary>The name to show for a client id (their business's owner name).</summary>
    public static string NameOf(int key) => key < 0 ? "" : HandsNet.NameOf((ulong)key);

    /// <summary>False on a guest whose host has no casino component (an older session prefab).</summary>
    public static bool Reachable => CasinoHost.Authority || Net != null;

    /// <summary>The local player's business cash as this machine knows it (a guest: the host's last word).</summary>
    public static long CashCents => GameSession.CashCents;

    // ================================================================================================ table time

    /// <summary>Add to a host time to get this machine's <c>Time.realtimeSinceStartupAsDouble</c> (0 on the host and in solo).</summary>
    public static double ClockOffset => CasinoHost.Authority ? 0.0 : hasOffset ? offset : 0.0;

    /// <summary>The host's clock now, as best this machine can tell (an estimate within about one network trip on a guest).</summary>
    public static double TableTime() => Time.realtimeSinceStartupAsDouble - ClockOffset;

    public static double ToLocalTime(double hostTime) => hostTime + ClockOffset;
    public static double ToTableTime(double localRealtime) => localRealtime - ClockOffset;

    /// <summary>Called with the host time stamped on something that just arrived. The estimate keeps the quickest delivery seen and drifts slowly up.</summary>
    static void Sample(double hostNow)
    {
        double s = Time.realtimeSinceStartupAsDouble - hostNow;
        if (!hasOffset) { offset = s; hasOffset = true; }
        else if (s < offset) offset = s;
        else offset += (s - offset) * 0.02;
    }

    // ================================================================================================ HUD cash

    static readonly object hudHolder = new object();

    /// <summary>
    /// Freeze (true) or release (false) the HUD's cash readout, so a win is not given away before the dealer, the wheel or the
    /// reels have shown it. Call <c>HoldHudCash(true)</c> BEFORE the request that can move the money (a bet at roulette or
    /// blackjack, a spin), and <c>HoldHudCash(false)</c> once your reveal has played; the cash changes in between show as one change
    /// on release. A hold nobody releases ends by itself after <see cref="FeelHud.CashHoldMaxSeconds"/>.
    /// </summary>
    public static void HoldHudCash(bool hold)
    {
        if (hold) FeelHud.HoldCash(hudHolder);
        else FeelHud.ReleaseCash(hudHolder);
    }

    // ================================================================================================ intents

    static void Run(Action<CasinoResult> done, CasinoResult r)
    {
        if (done == null) return;
        try { done(r); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>The tables when this machine decides, otherwise null.</summary>
    static CasinoTables Local()
    {
        if (!CasinoHost.Authority) return null;
        Attach();
        return attached;
    }

    static void Done(CasinoTables t, Action<CasinoResult> done, CasinoResult r)
    {
        t.Flush(); // the snapshots this request caused arrive before its callback
        Run(done, r);
    }

    static CasinoResult Unavailable => CasinoResult.Fail(CasinoError.Unavailable);

    /// <summary>Sit at <c>bj0</c> (a seat; <paramref name="preferredSeat"/> -1 = lowest free), <c>rl0</c> (join the wheel) or <c>slot0</c>..<c>slot4</c> (take the machine).
    /// The result's <c>seat</c> is the seat / chip colour. Sitting stands you up from any other station.</summary>
    public static void Sit(string table, int preferredSeat = -1, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.Sit(Me, table, preferredSeat, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskSit(table, preferredSeat, done);
    }

    /// <summary>Stand up. Safe to call when not seated (NotSeated). A hand in play keeps playing and settles to you; locked roulette bets pay.</summary>
    public static void Leave(string table, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.Leave(Me, table, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskLeave(table, done);
    }

    /// <summary>Blackjack: your opening stake (whole dollars $5-$200), charged now.</summary>
    public static void BlackjackBet(long cents, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.BlackjackBet(Me, cents, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskBlackjackBet(cents, done);
    }

    public static void BlackjackSitOut(Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.BlackjackSitOut(Me, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskBlackjackSitOut(done);
    }

    /// <summary>Blackjack: Hit / Stand / Double / Split on your turn, Insurance / NoInsurance in the insurance window.</summary>
    public static void BlackjackAct(BlackjackAction action, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.BlackjackAct(Me, action, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskBlackjackAct(action, done);
    }

    /// <summary>Roulette: join the wheel (same as <c>Sit("rl0")</c>).</summary>
    public static void RouletteJoin(Action<CasinoResult> done = null) => Sit(CasinoTableIds.Roulette, -1, done);

    /// <summary>Roulette: replace your WHOLE bet set (the first <paramref name="count"/> of <paramref name="bets"/>; 0 clears it).
    /// Nothing is charged until the host locks the bets.</summary>
    public static void RouletteSetBets(RouletteBet[] bets, int count, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.RouletteSetBets(Me, bets, count, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskRouletteSetBets(bets, count, done);
    }

    public static void RouletteReady(bool ready, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.RouletteReady(Me, ready, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskRouletteReady(ready, done);
    }

    /// <summary>Slots: pull your machine (you must hold it). The spin comes back to everybody, you included, as <see cref="SlotSpun"/>.</summary>
    public static void SlotSpin(int machine, Action<CasinoResult> done = null)
    {
        var t = Local();
        if (t != null) { Done(t, done, t.SlotSpin(Me, machine, CasinoHost.TableNow)); return; }
        var net = Net;
        if (net == null) { Run(done, Unavailable); return; }
        net.AskSlotSpin(machine, done);
    }

    /// <summary>The bar (unchanged, still one order in flight): the host charges the tab and answers with the drink.</summary>
    public static void Drink(int drink, Action<BarView> done)
    {
        if (CasinoHost.Authority) { RunBar(done, CasinoHost.Drink(Me, drink)); return; }
        var net = Net;
        if (net == null) { RunBar(done, new BarView { error = CasinoError.Unavailable, cashCents = -1 }); return; }
        net.AskDrink(drink, done);
    }

    static void RunBar(Action<BarView> done, BarView v)
    {
        if (done == null) return;
        try { done(v); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Ask for the casino's current picture: call it when the local player enters the room. Raises the snapshot and
    /// occupancy events once with the answer (immediately on the host, after a network trip on a guest).</summary>
    public static void RequestSync()
    {
        var t = Local();
        if (t != null)
        {
            var s = t.Sync(CasinoHost.TableNow);
            ApplySync(s.blackjack, s.roulette, s.slotOccupants);
            return;
        }
        var net = Net;
        if (net != null) net.AskSync();
    }

    // ================================================================================================ host machine: tables -> this client

    static void Attach()
    {
        if (!CasinoHost.Authority) return;
        var t = CasinoHost.Tables;
        if (t == attached) return;
        Detach();
        attached = t;
        t.BlackjackChanged += OnHostBlackjack;
        t.RouletteChanged += OnHostRoulette;
        t.SlotSpun += OnHostSlotSpun;
        t.SlotOccupancyChanged += OnHostSlotOccupancy;
        t.NoticeSent += OnHostNotice;
    }

    static void Detach()
    {
        var t = attached;
        attached = null;
        if (t == null) return;
        t.BlackjackChanged -= OnHostBlackjack;
        t.RouletteChanged -= OnHostRoulette;
        t.SlotSpun -= OnHostSlotSpun;
        t.SlotOccupancyChanged -= OnHostSlotOccupancy;
        t.NoticeSent -= OnHostNotice;
    }

    static void OnHostBlackjack(BlackjackTableSnapshot snap, byte[] bytes) => ApplyBlackjack(snap);
    static void OnHostRoulette(RouletteTableSnapshot snap, byte[] bytes) => ApplyRoulette(snap);
    static void OnHostSlotSpun(SlotEventInfo e) => RaiseSlotSpun(e);
    static void OnHostSlotOccupancy(int machine, int key) => ApplyOccupancy(machine, key);

    static void OnHostNotice(ulong client, string table, CasinoNotice notice)
    {
        if (client == Me) RaiseNotice(table, notice);
    }

    // ================================================================================================ guest: wire -> this client (called by CasinoNet)

    internal static void OnWireBlackjack(byte[] bytes)
    {
        if (!BlackjackTableSnapshot.TryDeserialize(bytes, out var snap)) return;
        Sample(snap.now);
        ApplyBlackjack(snap);
    }

    internal static void OnWireRoulette(byte[] bytes)
    {
        if (!RouletteTableSnapshot.TryDeserialize(bytes, out var snap)) return;
        Sample(snap.now);
        ApplyRoulette(snap);
    }

    internal static void OnWireSlotSpun(SlotEventInfo e)
    {
        Sample(e.start);
        RaiseSlotSpun(e);
    }

    internal static void OnWireSlotOccupancy(int machine, int key) => ApplyOccupancy(machine, key);

    internal static void OnWireNotice(string table, CasinoNotice notice) => RaiseNotice(table, notice);

    internal static void OnWireSync(byte[] blackjack, byte[] roulette, int[] slotOccupants)
    {
        if (BlackjackTableSnapshot.TryDeserialize(blackjack, out var b)) Sample(b.now);
        ApplySync(blackjack, roulette, slotOccupants);
    }

    /// <summary>The session ended or the casino component went away: forget the replicated picture.</summary>
    internal static void ClearReplicated()
    {
        hasBj = hasRl = false;
        bjSnap = default;
        rlSnap = default;
        for (int i = 0; i < occupants.Length; i++) occupants[i] = -1;
        hasOffset = false;
    }

    // ================================================================================================ apply and raise

    static void ApplySync(byte[] blackjack, byte[] roulette, int[] slotOccupants)
    {
        if (BlackjackTableSnapshot.TryDeserialize(blackjack, out var b)) ApplyBlackjack(b);
        if (RouletteTableSnapshot.TryDeserialize(roulette, out var r)) ApplyRoulette(r);
        if (slotOccupants != null)
            for (int i = 0; i < occupants.Length && i < slotOccupants.Length; i++) ApplyOccupancy(i, slotOccupants[i]);
    }

    static void ApplyBlackjack(BlackjackTableSnapshot snap)
    {
        if (hasBj && snap.now < bjSnap.now) return; // an older picture that arrived late
        bjSnap = snap;
        hasBj = true;
        var h = BlackjackSnapshotChanged;
        if (h == null) return;
        double off = ClockOffset;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<BlackjackTableSnapshot, double>)d)(snap, off); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void ApplyRoulette(RouletteTableSnapshot snap)
    {
        if (hasRl && snap.now < rlSnap.now) return;
        rlSnap = snap;
        hasRl = true;
        var h = RouletteSnapshotChanged;
        if (h == null) return;
        double off = ClockOffset;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<RouletteTableSnapshot, double>)d)(snap, off); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void ApplyOccupancy(int machine, int key)
    {
        if (machine < 0 || machine >= occupants.Length || occupants[machine] == key) return;
        occupants[machine] = key;
        var h = SlotOccupancyChanged;
        if (h == null) return;
        string name = NameOf(key);
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<int, int, string>)d)(machine, key, name); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void RaiseSlotSpun(SlotEventInfo info)
    {
        var h = SlotSpun;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<SlotEventInfo>)d)(info); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void RaiseNotice(string table, CasinoNotice notice)
    {
        var h = Notice;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<string, CasinoNotice>)d)(table, notice); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void OnBigWinHeard(ulong winner, string who, string what, long cents)
    {
        var h = BigWin;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<int, string, string, long>)d)((int)Math.Min((ulong)int.MaxValue, winner), who, what, cents); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void OnBarServedHeard(ulong who, int drink, int spot)
    {
        var h = BarServed;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<int, int, int>)d)((int)Math.Min((ulong)int.MaxValue, who), drink, spot); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        CasinoHost.BigWinHeard -= OnBigWinHeard;
        CasinoHost.BigWinHeard += OnBigWinHeard;
        CasinoHost.BarServedHeard -= OnBarServedHeard;
        CasinoHost.BarServedHeard += OnBarServedHeard;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        attached = null;
        hasBj = hasRl = hasOffset = false;
        bjSnap = default;
        rlSnap = default;
        for (int i = 0; i < occupants.Length; i++) occupants[i] = -1;
        BlackjackSnapshotChanged = null;
        RouletteSnapshotChanged = null;
        SlotSpun = null;
        SlotOccupancyChanged = null;
        BigWin = null;
        Notice = null;
        BarServed = null;
    }
}

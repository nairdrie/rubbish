using System;
using UnityEngine;

/// <summary>
/// The Lucky Raccoon's house, on the machine with authority (the host, or a solo game with or without a session):
/// <see cref="CasinoTables"/> (the shared blackjack table, roulette wheel and slot seats) and <see cref="CasinoBank"/> (the
/// bar) wired to the real world. Money goes through <see cref="Ledger"/> into the CALLER's own business (never an
/// employer's), every roll comes from the host dice, and a remote caller must be standing at the station. Guests reach this
/// through the guarded RPCs in <see cref="CasinoNet"/>; this machine's own player calls it directly through
/// <see cref="CasinoClient"/>, exactly like <c>ScavengerHost</c>.
/// </summary>
public static class CasinoHost
{
    /// <summary>This machine decides: hosting, solo, or no session at all.</summary>
    public static bool Authority => !GameSession.IsConnectedGuest;

    /// <summary>How far from a station's seat a remote caller may stand (metres, flat), with slack for lag.</summary>
    public const float StationRange = 5f;
    /// <summary>One ledger posting never carries more than this, so dollars-as-float stays exact to the cent.</summary>
    const long MaxPostingCents = 10_000_000;

    /// <summary>Beyond this far from its table (flat, metres) a seated player is stood up by the presence sweep.</summary>
    public const float LeaveRange = 9f;

    static CasinoBank bank;
    static CasinoTables tables;
    static CasinoHostRunner runner;

    /// <summary>(winner client, winner name, what they hit, cents). Raised on every machine that should hear about it:
    /// on the host by the bank, on guests by <see cref="CasinoNet"/>. Presentation only.</summary>
    public static event Action<ulong, string, string, long> BigWinHeard;
    /// <summary>Host only: a big win to pass on to the guests.</summary>
    public static event Action<ulong, string, string, long> BigWinToBroadcast;

    /// <summary>(client, drink id, bar spot 0..2). Raised on every machine that should SEE a drink being served: on the host by <see cref="Drink"/>,
    /// on guests by <see cref="CasinoNet"/>. Presentation only (the bartender pours, a glass slides to the right spot).</summary>
    public static event Action<ulong, int, int> BarServedHeard;
    /// <summary>Host only: a drink was just served; pass it on to the guests.</summary>
    public static event Action<ulong, int, int> BarServedToBroadcast;

    public static CasinoBank Bank => bank ??= Build();

    /// <summary>The shared tables (Authority only; guests must go through <see cref="CasinoNet"/>). Created, wired and ticking on first use.</summary>
    public static CasinoTables Tables
    {
        get
        {
            if (tables == null) tables = BuildTables();
            EnsureRunner();
            return tables;
        }
    }

    /// <summary>True once <see cref="Tables"/> exists (nothing has been created yet on a machine that never played).</summary>
    public static bool HasTables => tables != null;

    /// <summary>The table clock: host seconds, monotonic (real time, unaffected by pause). Snapshots and slot events carry it.</summary>
    public static double TableNow => Now;

    static double Now => Time.realtimeSinceStartupAsDouble;
    static bool IsLocal(ulong client) => client == Permissions.LocalClientId;

    static CasinoBank Build() => new CasinoBank
    {
        CashOf = CashOf,
        Charge = (client, cents, id, reason) => Move(client, cents, false, id, reason),
        Credit = (client, cents, id, reason) => Move(client, cents, true, id, reason),
        Near = Near,
        DrunkLevelOf = DrunkLevelOf,
    };

    static CasinoTables BuildTables() => new CasinoTables
    {
        CashOf = CashOf,
        Charge = (client, cents, id, reason) => Move(client, cents, false, id, reason),
        Credit = (client, cents, id, reason) => Move(client, cents, true, id, reason),
        Dice = HostDice,
        Near = NearTable,
        Present = Present,
        BigWin = OnBigWin,
    };

    // ------------------------------------------------------------------ requests (authority only)

    public static BarView Drink(ulong client, int drink)
    {
        if (!Authority) return new BarView { error = CasinoError.NotHost, cashCents = -1 };
        EnsureRunner();
        var view = Bank.Drink(client, drink, Now);
        if (view.Ok) OnServed(client, view.drink);
        return view;
    }

    /// <summary>A drink was paid for: tell the room so the bartender pours it and the glass lands at the buyer's spot.</summary>
    static void OnServed(ulong client, int drink)
    {
        int spot = 1;
        try
        {
            var body = PlayerBody.For(client);
            var bar = CasinoStation.Find(CasinoStationKind.Bar);
            if (body != null && bar != null) { int s = bar.NearestSpot(body.transform.position); if (s >= 0) spot = s; }
        }
        catch (Exception) { spot = 1; }
        HearBarServed(client, drink, spot);
        var h = BarServedToBroadcast;
        if (h == null) return;
        try { h(client, drink, spot); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>A drink was served at the bar (host: by <see cref="Drink"/>; guest: from the host's broadcast). Presentation only.</summary>
    public static void HearBarServed(ulong client, int drink, int spot)
    {
        if (!CasinoRules.IsDrink(drink)) return;
        var h = BarServedHeard;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<ulong, int, int>)d)(client, drink, Mathf.Clamp(spot, 0, CasinoLayout.BarSpots - 1)); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>A client left: they stand up at every table and machine. Hands in play and locked bets settle in absentia while
    /// the table plays on (paid to their business if it still exists, otherwise counted as unpaid).</summary>
    public static void ForgetClient(ulong client)
    {
        if (!Authority) return;
        if (bank != null) bank.Forget(client);
        if (tables != null) { tables.Disconnected(client, Now); tables.Flush(); }
    }

    /// <summary>The session started or ended: settle what is open, free every seat (client ids do not carry over).</summary>
    public static void ResetSession()
    {
        if (bank != null) bank.Close();
        if (tables != null) tables.Close(Now);
    }

    internal static void Tick()
    {
        if (!Authority) return;
        if (tables != null) tables.Tick(Now);
    }

    // ------------------------------------------------------------------ the bank's world

    /// <summary>The Ledger scope for a client's OWN business (null = this machine's). False while a remote client's
    /// business is not connected.</summary>
    static bool OwnBusiness(ulong client, out string ledgerTarget)
    {
        if (IsLocal(client)) { ledgerTarget = null; return true; }
        if (Businesses.TryGetForClient(client, out var b)) { ledgerTarget = b.Id; return true; }
        ledgerTarget = null;
        return false;
    }

    static long CashOf(ulong client)
    {
        if (IsLocal(client)) return GameSession.CashCents;
        return Businesses.TryGetForClient(client, out var b) ? b.cashCents : -1;
    }

    /// <summary>
    /// One charge or credit on the caller's own business, through the Ledger (Multiplayer.md §9 rule 1). The explicit
    /// <see cref="Ledger.ToBusiness"/> scope keeps an employee's bets out of the employer's books (no payroll redirect).
    /// Returns true only when the balance really moved by exactly that much.
    /// </summary>
    static bool Move(ulong client, long cents, bool credit, string ledgerId, string reason)
    {
        if (!Authority || cents <= 0) return false;
        if (!OwnBusiness(client, out string target)) return false;
        long before = CashOf(client);
        if (before < 0) return false;
        using (Ledger.ToBusiness(target))
        {
            for (long left = cents; left > 0; left -= MaxPostingCents)
            {
                float dollars = (float)(Math.Min(left, MaxPostingCents) / 100.0);
                if (credit) Ledger.Credit(dollars, reason, ledgerId);
                else Ledger.Charge(dollars, reason, ledgerId);
            }
        }
        long moved = CashOf(client) - before;
        if (moved == (credit ? cents : -cents)) return true;
        Debug.LogWarning($"[Casino] {(credit ? "credit" : "charge")} of {cents}c for client {client} moved {moved}c ({reason}).");
        return false;
    }

    /// <summary>Exactly uniform 0..n-1 from the host dice (rejection: no modulo bias on a 312-card shuffle).</summary>
    static int HostDice(int n)
    {
        if (n <= 1) return 0;
        uint un = (uint)n, limit = uint.MaxValue - uint.MaxValue % un;
        for (int guard = 0; guard < 64; guard++)
        {
            uint r = NightDice.Host.Next();
            if (r < limit) return (int)(r % un);
        }
        return (int)(NightDice.Host.Next() % un);
    }

    /// <summary>The proximity check for a table id (<c>bj0</c>, <c>rl0</c>, <c>slotN</c>).</summary>
    static bool NearTable(ulong client, string table)
    {
        if (!CasinoTableIds.TryParse(table, out var game, out int index)) return false;
        return Near(client, (int)game, index);
    }

    /// <summary>Is this seated player still at their table? The local player is trusted unless they are passing out; a remote one
    /// must be inside the room and within <see cref="LeaveRange"/> of the table's station (a pass-out, a teleport or a carry moves
    /// them). When the room is not loaded here only the room itself is checked.</summary>
    static bool Present(ulong client, string table)
    {
        if (IsLocal(client) && DrunkStatus.PassingOut) return false;
        var body = PlayerBody.For(client);
        if (body == null) return IsLocal(client);
        Vector3 p = body.transform.position;
        if (!CasinoLayout.Contains(p)) return false;
        if (!CasinoTableIds.TryParse(table, out var game, out int index)) return true;
        CasinoStation station = null;
        try { station = CasinoStation.Find(KindOf(game), game == CasinoGame.Slot ? index : 0); }
        catch (Exception) { station = null; }
        if (station == null) return true;
        Vector3 d = p - (station.Seat != null ? station.Seat.position : station.transform.position);
        d.y = 0f;
        return d.sqrMagnitude <= LeaveRange * LeaveRange;
    }

    static CasinoStationKind KindOf(CasinoGame game) =>
        game == CasinoGame.Roulette ? CasinoStationKind.Roulette : game == CasinoGame.Slot ? CasinoStationKind.Slot :
        game == CasinoGame.Bar ? CasinoStationKind.Bar : CasinoStationKind.Blackjack;

    /// <summary>Is the caller standing at that station? This machine's own player is trusted (its screen only opens at
    /// a station, and the debug menu opens tables from anywhere). A remote caller must be inside the casino and, when
    /// the room is loaded here, within <see cref="StationRange"/> of the station's seat.</summary>
    static bool Near(ulong client, int game, int index)
    {
        if (IsLocal(client)) return true;
        var body = PlayerBody.For(client);
        if (body == null) return false;
        Vector3 p = body.transform.position;
        if (!CasinoLayout.Contains(p)) return false;
        CasinoStation station = null;
        try { station = CasinoStation.Find(KindOf((CasinoGame)game), game == (int)CasinoGame.Slot ? index : 0); }
        catch (Exception) { station = null; }
        if (station == null) return true;
        var seat = station.Seat;
        Vector3 d = p - (seat != null ? seat.position : station.transform.position);
        d.y = 0f;
        return d.sqrMagnitude <= StationRange * StationRange;
    }

    static float DrunkLevelOf(ulong client) => IsLocal(client) ? DrunkStatus.Level : DrunkNet.LevelOf(client);

    static void OnBigWin(ulong client, int game, string what, long cents)
    {
        string who = HandsNet.NameOf(client);
        HearBigWin(client, who, what, cents);
        var h = BigWinToBroadcast;
        if (h == null) return;
        try { h(client, who, what, cents); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>A big win happened somewhere in the room (host: from the bank; guest: from the host's broadcast).</summary>
    public static void HearBigWin(ulong winner, string who, string what, long cents)
    {
        var h = BigWinHeard;
        if (h == null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<ulong, string, string, long>)d)(winner, who, what, cents); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying) return;
        var go = new GameObject("[CasinoHost]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<CasinoHostRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        bank = null;
        tables = null;
        runner = null;
        BigWinHeard = null;
        BigWinToBroadcast = null;
        BarServedHeard = null;
        BarServedToBroadcast = null;
    }
}

/// <summary>Hidden driver for <see cref="CasinoHost"/>: ticks the shared tables every frame, and gives a clean slate whenever a
/// session starts or stops (so a solo seat never leaks into a hosted game and the other way round).</summary>
public sealed class CasinoHostRunner : MonoBehaviour
{
    float nextTick;
    bool wasGuest, wasListening;

    void Awake()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        wasListening = nm != null && nm.IsListening;
        wasGuest = GameSession.IsConnectedGuest;
    }

    void Update()
    {
        CasinoHost.Tick();
        if (Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + 0.5f;
        var nm = Unity.Netcode.NetworkManager.Singleton;
        bool listening = nm != null && nm.IsListening, guest = GameSession.IsConnectedGuest;
        if (listening != wasListening || guest != wasGuest)
        {
            // Settle with the authority we had a moment ago if we still have it; a machine that just became a guest
            // has nothing of its own to settle (its rounds lived on the host).
            if (!guest) CasinoHost.ResetSession();
            wasListening = listening;
            wasGuest = guest;
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Netcode;

/// <summary>Why the house said no (Docs/Casino.md §4). One byte in every reply; <see cref="CasinoBank.Describe"/> words it.</summary>
public enum CasinoError : byte
{
    Ok = 0,
    /// <summary>This machine may not decide (a guest called the host logic directly).</summary>
    NotHost,
    /// <summary>The session has no casino component, or the host never answered.</summary>
    Unavailable,
    /// <summary>The caller's business is not connected (yet).</summary>
    NoBusiness,
    /// <summary>The caller is not standing at that station.</summary>
    TooFar,
    /// <summary>A request is already being settled.</summary>
    Busy,
    /// <summary>Too soon after the last spin or drink.</summary>
    Throttled,
    BadStake,
    CantAfford,
    BadMachine,
    /// <summary>Roulette: see <see cref="CasinoResult.betError"/>.</summary>
    BadBets,
    /// <summary>A blackjack round is still in play: finish it first.</summary>
    RoundOpen,
    /// <summary>A blackjack action with no round in play.</summary>
    NoRound,
    IllegalAction,
    BadDrink,
    /// <summary>The bartender will not pour another one.</summary>
    CutOff,
}

/// <summary>The host's answer to one order at the bar. The buyer's machine adds <see cref="units"/> to its own drunk level.</summary>
public struct BarView : INetworkSerializable
{
    public CasinoError error;
    public byte drink;
    public int priceCents;
    public float units;
    public long cashCents;

    public bool Ok => error == CasinoError.Ok;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref error);
        s.SerializeValue(ref drink);
        s.SerializeValue(ref priceCents);
        s.SerializeValue(ref units);
        s.SerializeValue(ref cashCents);
    }
}

/// <summary>
/// The casino's cashier for the BAR (Docs/Casino.md §5, §10): the host's decision for one drink order, and the pure money
/// helpers (charge, credit, the big-win rule, the words for a refusal) shared with <see cref="CasinoTables"/>. The shared
/// blackjack, roulette and slot play moved to <see cref="CasinoTables"/>. Pure: money, positions and the clock all come in
/// through the delegates, so the same code runs on the host (wired to <see cref="Ledger"/> by <see cref="CasinoHost"/>) and in
/// EditMode tests (wired to a fake till).
/// <para><b>Order, always:</b> validate → check the caller can afford it → charge → reply.</para>
/// </summary>
public sealed class CasinoBank
{
    /// <summary>The host's own floor between two drinks by one player. The screen waits longer.</summary>
    public const double BarMinSeconds = 0.5;
    /// <summary>From this drunk level the bartender only sells water (the cap is <see cref="DrunkRules.MaxLevel"/>).</summary>
    public const float BarCutOffLevel = 1.15f;
    /// <summary>A win this large (or <see cref="BigWinMultiple"/> × the stake) is announced to the room.</summary>
    public const long BigWinCents = 100000;
    public const int BigWinMultiple = 50;

    // ---- the outside world
    /// <summary>The caller's business cash in cents, or a negative number when they have no business here.</summary>
    public Func<ulong, long> CashOf;
    /// <summary>(client, cents, ledger id, reason) → applied. Cents are always positive.</summary>
    public Func<ulong, long, string, string, bool> Charge, Credit;
    /// <summary>(client, <see cref="CasinoGame"/>, station index) → the caller is standing there. Null = no check.</summary>
    public Func<ulong, int, int, bool> Near;
    /// <summary>The caller's drunk level as the host knows it. Null = sober.</summary>
    public Func<ulong, float> DrunkLevelOf;

    // ---- the books (for audits and tests)
    public long TotalChargedCents { get; private set; }
    public long TotalCreditedCents { get; private set; }

    readonly Dictionary<ulong, double> lastBarAt = new Dictionary<ulong, double>();
    bool busy;

    bool Wired => CashOf != null && Charge != null && Credit != null;

    long CashNow(ulong client)
    {
        try { return CashOf != null ? CashOf(client) : -1; }
        catch (Exception) { return -1; }
    }

    CasinoError Gate(ulong client, CasinoGame game, int index)
    {
        if (!Wired) return CasinoError.NotHost;
        if (Near != null && !Near(client, (int)game, index)) return CasinoError.TooFar;
        return CasinoError.Ok;
    }

    /// <summary>Affordability, then the charge. Nothing is charged unless Ok comes back.</summary>
    CasinoError Take(ulong client, long cents, string ledgerId, string reason)
    {
        if (cents <= 0) return CasinoError.BadStake;
        long cash = CashOf(client);
        if (cash < 0) return CasinoError.NoBusiness;
        if (!CasinoRules.CanAfford(cash, cents)) return CasinoError.CantAfford;
        if (!Charge(client, cents, ledgerId, reason)) return CasinoError.NoBusiness;
        TotalChargedCents += cents;
        return CasinoError.Ok;
    }

    /// <summary>A win this large (≥ $1,000), or this many times the stake (50×, when there is a stake), is announced to the room.</summary>
    public static bool IsBig(long wonCents, long stakeCents) =>
        wonCents >= BigWinCents || (stakeCents > 0 && wonCents >= stakeCents * BigWinMultiple);

    // ================================================================================================ the bar

    /// <summary>True when the bartender will not serve this drink at this level: alcohol from <see cref="BarCutOffLevel"/>, never water.</summary>
    public static bool BarRefuses(float level, CasinoDrink drink) => drink.units > 0f && level >= BarCutOffLevel;

    public BarView Drink(ulong client, int drinkId, double now)
    {
        var v = new BarView { drink = (byte)(drinkId < 0 || drinkId > 255 ? 255 : drinkId), cashCents = -1 };
        if (busy) { v.error = CasinoError.Busy; return v; }
        busy = true;
        try { DrinkCore(client, drinkId, now, ref v); }
        finally { busy = false; }
        v.cashCents = CashNow(client);
        return v;
    }

    void DrinkCore(ulong client, int drinkId, double now, ref BarView v)
    {
        if (!CasinoRules.TryGetDrink(drinkId, out var drink)) { v.error = CasinoError.BadDrink; return; }
        v.priceCents = drink.priceCents;
        v.error = Gate(client, CasinoGame.Bar, 0);
        if (v.error != CasinoError.Ok) return;
        if (lastBarAt.TryGetValue(client, out double last) && now - last < BarMinSeconds) { v.error = CasinoError.Throttled; return; }
        float level = DrunkLevelOf != null ? DrunkLevelOf(client) : 0f;
        if (BarRefuses(level, drink)) { v.error = CasinoError.CutOff; return; }
        v.error = Take(client, drink.priceCents, LedgerIds.BarTab, drink.name);
        if (v.error != CasinoError.Ok) return;
        lastBarAt[client] = now;
        v.units = drink.units;
    }

    /// <summary>A client left the session: forget their drink timer.</summary>
    public void Forget(ulong client) => lastBarAt.Remove(client);

    /// <summary>The session ended: forget everybody.</summary>
    public void Close() => lastBarAt.Clear();

    // ================================================================================================ words

    /// <summary>A short line for the player. Roulette's layout errors are worded by <see cref="Describe(RouletteBetError)"/>.</summary>
    public static string Describe(CasinoError e)
    {
        switch (e)
        {
            case CasinoError.Ok: return "";
            case CasinoError.NotHost: return "Only the house decides that.";
            case CasinoError.Unavailable: return "The house isn't answering. Try again.";
            case CasinoError.NoBusiness: return "Your business isn't connected yet.";
            case CasinoError.TooFar: return "Step up to it first.";
            case CasinoError.Busy: return "One thing at a time.";
            case CasinoError.Throttled: return "Easy. Give it a second.";
            case CasinoError.BadStake: return "That isn't a legal bet here.";
            case CasinoError.CantAfford: return "You can't cover that.";
            case CasinoError.BadMachine: return "That machine is out of order.";
            case CasinoError.BadBets: return "The table won't take those bets.";
            case CasinoError.RoundOpen: return "Finish your blackjack hand first.";
            case CasinoError.NoRound: return "There's no hand in play.";
            case CasinoError.IllegalAction: return "You can't do that now.";
            case CasinoError.BadDrink: return "That's not on the menu.";
            case CasinoError.CutOff: return "\"You've had enough, pal. Water?\"";
            default: return "No.";
        }
    }

    public static string Describe(RouletteBetError e)
    {
        switch (e)
        {
            case RouletteBetError.Ok: return "";
            case RouletteBetError.NoBets: return "Place a bet first.";
            case RouletteBetError.TooManyBets: return "Too many separate bets for one spin (" + RouletteRules.MaxBetsPerSpin + " at most).";
            case RouletteBetError.BadSpot: return "That isn't a spot on the layout.";
            case RouletteBetError.BadAmount: return "Whole dollars only.";
            case RouletteBetError.BelowMinimum: return "Table minimum is " + CasinoRules.Dollars(CasinoRules.RouletteMinSpotCents) + " a spot.";
            case RouletteBetError.AboveSpotMaximum:
                return "Spot maximum: " + CasinoRules.Dollars(CasinoRules.RouletteMaxInsideCents) + " inside, " +
                       CasinoRules.Dollars(CasinoRules.RouletteMaxOutsideCents) + " outside.";
            case RouletteBetError.AboveTableMaximum: return "Table maximum is " + CasinoRules.Dollars(CasinoRules.RouletteMaxSpinCents) + " a spin.";
            default: return "The table won't take that.";
        }
    }
}

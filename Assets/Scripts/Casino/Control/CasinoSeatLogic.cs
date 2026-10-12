using System;
using System.Collections.Generic;

// Pure rules of the local seat controllers (Docs/Casino.md §12): no scene access, no Unity types, so EditMode tests can drive
// them by reflection like the rest of the casino. The controllers (MonoBehaviours) own input, picking and presentation.

/// <summary>The selected chip denomination (blackjack 5 / 10 / 25 / 50 / 100, roulette 1 / 5 / 25 / 100) and how it cycles.</summary>
public sealed class ChipSelector
{
    readonly int[] denoms;

    public ChipSelector(int[] denominations, int start = 0)
    {
        if (denominations == null || denominations.Length == 0) throw new ArgumentException("no denominations");
        denoms = (int[])denominations.Clone();
        Index = Math.Max(0, Math.Min(denoms.Length - 1, start));
    }

    public int Index { get; private set; }
    public int Count => denoms.Length;
    public int Cents => denoms[Index];
    public int CentsAt(int i) => denoms[Math.Max(0, Math.Min(denoms.Length - 1, i))];

    /// <summary>Select by index (clamped). True when the selection changed.</summary>
    public bool Select(int i)
    {
        i = Math.Max(0, Math.Min(denoms.Length - 1, i));
        if (i == Index) return false;
        Index = i;
        return true;
    }

    /// <summary>One step along the rack with wrap-around (+1 = next, -1 = previous). Returns the new index.</summary>
    public int Step(int dir)
    {
        if (dir == 0) return Index;
        Index = ((Index + (dir > 0 ? 1 : -1)) % denoms.Length + denoms.Length) % denoms.Length;
        return Index;
    }

    /// <summary>The largest denomination not above <paramref name="cents"/> (the smallest if none): "pick the chip I can afford".</summary>
    public bool SelectLargestAtMost(long cents)
    {
        int best = 0;
        for (int i = 0; i < denoms.Length; i++) if (denoms[i] <= cents) best = i;
        return Select(best);
    }

    /// <summary>A total split into rack chips, largest first (greedy). The remainder that no chip covers is dropped.</summary>
    public static List<int> Decompose(long cents, int[] denominations)
    {
        var sorted = (int[])denominations.Clone();
        Array.Sort(sorted);
        var list = new List<int>();
        for (int i = sorted.Length - 1; i >= 0; i--)
            while (cents >= sorted[i]) { list.Add(sorted[i]); cents -= sorted[i]; }
        return list;
    }

    /// <summary>The index of a denomination in the rack, or -1.</summary>
    public int IndexOf(int cents) => Array.IndexOf(denoms, cents);
}

public enum PendingAdd { Added, AtMaximum, CantAfford, TooManyChips, BadChip }

/// <summary>
/// The blackjack stake the player is building on the bet circle before the host sees it. The host takes ONE stake per round
/// (<c>AlreadyBet</c> after that), so chips are only stacked locally and the total goes out once, on confirm or just before the
/// betting clock ends. Clamped to the table limits ($5 to $200, whole dollars) and to the cash the player has.
/// </summary>
public sealed class PendingBet
{
    public const int MaxChips = 40;
    readonly List<int> chips = new List<int>();

    public long Total { get; private set; }
    public int Count => chips.Count;
    public IReadOnlyList<int> Chips => chips;
    public bool IsEmpty => chips.Count == 0;

    /// <summary>Is the stack a legal opening stake (whole dollars, $5 to $200)?</summary>
    public bool Legal => CasinoRules.IsLegalBlackjackStake(Total);

    public PendingAdd Add(int cents, long cashCents, long maxCents = CasinoRules.BlackjackMaxCents)
    {
        if (cents <= 0 || cents % CasinoRules.ChipCents != 0) return PendingAdd.BadChip;
        if (chips.Count >= MaxChips) return PendingAdd.TooManyChips;
        if (Total + cents > maxCents) return PendingAdd.AtMaximum;
        if (Total + cents > cashCents) return PendingAdd.CantAfford;
        chips.Add(cents);
        Total += cents;
        return PendingAdd.Added;
    }

    /// <summary>Take the last chip off. False when the stack is empty.</summary>
    public bool RemoveLast(out int cents)
    {
        cents = 0;
        if (chips.Count == 0) return false;
        cents = chips[chips.Count - 1];
        chips.RemoveAt(chips.Count - 1);
        Total -= cents;
        return true;
    }

    public void Clear() { chips.Clear(); Total = 0; }

    /// <summary>Replace the stack with a total ("same bet again"), split into rack chips. Returns the total actually stacked.</summary>
    public long SetTotal(long cents, int[] rack)
    {
        Clear();
        foreach (int c in ChipSelector.Decompose(Math.Max(0, Math.Min(cents, CasinoRules.BlackjackMaxCents)), rack))
        {
            if (chips.Count >= MaxChips) break;
            chips.Add(c);
            Total += c;
        }
        return Total;
    }

    /// <summary>Why this stack cannot be sent now: Ok, BelowMinimum, AboveMaximum, NotWholeDollars or CantAfford.</summary>
    public CasinoTableError Check(long cashCents)
    {
        var limit = BlackjackTable.CheckStake(Total);
        if (limit != CasinoTableError.Ok) return limit;
        return Total > cashCents ? CasinoTableError.CantAfford : CasinoTableError.Ok;
    }

    /// <summary>The words for a refused add.</summary>
    public static string Say(PendingAdd r, int cents, long cashCents)
    {
        switch (r)
        {
            case PendingAdd.AtMaximum: return "Table maximum is " + CasinoRules.Dollars(CasinoRules.BlackjackMaxCents) + " a hand.";
            case PendingAdd.CantAfford: return "You can't cover another " + CasinoRules.Dollars(cents) + " (you have " + CasinoRules.Dollars(cashCents) + ").";
            case PendingAdd.TooManyChips: return "That's a lot of chips. Send it!";
            case PendingAdd.BadChip: return "Whole-dollar chips only.";
            default: return "";
        }
    }

    /// <summary>The words for a refused send (also what the host said back).</summary>
    public static string Say(CasinoTableError e, long cashCents)
    {
        switch (e)
        {
            case CasinoTableError.BelowMinimum: return "Bets start at " + CasinoRules.Dollars(CasinoRules.BlackjackMinCents) + ".";
            case CasinoTableError.AboveMaximum: return "Table maximum is " + CasinoRules.Dollars(CasinoRules.BlackjackMaxCents) + " a hand.";
            case CasinoTableError.CantAfford: return "You can't cover that (you have " + CasinoRules.Dollars(cashCents) + ").";
            case CasinoTableError.TooLate: return "Too late: the cards are out.";
            case CasinoTableError.WrongPhase: return "Betting is closed. Next hand.";
            case CasinoTableError.AlreadyBet: return "Your bet is already down.";
            default: return CasinoTables.Describe(e);
        }
    }
}

/// <summary>When the controller sends the stake by itself: once, a second before the betting clock ends.</summary>
public static class BetTiming
{
    /// <summary>Seconds before the betting deadline at which a legal pending stake is sent without asking ("bet auto-placed").</summary>
    public const double AutoSendLead = 1.0;

    public static bool ShouldAutoSend(BlackjackTablePhase phase, double secondsLeft, bool pendingLegal, bool betPlaced, bool sitOut, bool sendInFlight)
    {
        if (phase != BlackjackTablePhase.Betting || !pendingLegal || betPlaced || sitOut || sendInFlight) return false;
        return secondsLeft <= AutoSendLead && secondsLeft > 0.0;
    }
}

/// <summary>What a blackjack seat may do and what it costs, worked out from the PUBLIC snapshot (a guest has no table object).</summary>
public static class BlackjackSnapshotRules
{
    /// <summary>The seat this key sits at, or -1.</summary>
    public static int SeatOf(BlackjackTableSnapshot s, int key)
    {
        if (s.seats == null || key < 0) return -1;
        for (int i = 0; i < s.seats.Length; i++) if (s.seats[i].key == key) return i;
        return -1;
    }

    /// <summary>The seat's round as the rules see it: its hands, the stake, the dealer's up card. Null when the seat has no round.</summary>
    public static BlackjackRound MirrorOf(BlackjackTableSnapshot s, int seat)
    {
        if (s.seats == null || seat < 0 || seat >= s.seats.Length) return null;
        var o = s.seats[seat];
        if (o.hands == null || o.hands.Length == 0) return null;
        var m = new BlackjackRound
        {
            phase = s.phase == BlackjackTablePhase.Insurance ? BlackjackPhase.Insurance :
                    s.phase == BlackjackTablePhase.Turns ? BlackjackPhase.Player : BlackjackPhase.Idle,
            handCount = Math.Min(o.hands.Length, BlackjackRules.MaxHands),
            active = s.activeSeat == seat ? Math.Max(0, Math.Min(s.activeHand, BlackjackRules.MaxHands - 1)) : 0,
            baseStakeCents = o.betCents,
            insuranceCents = o.insuranceCents,
            holeRevealed = !s.HoleHidden,
        };
        if (s.dealer != null)
        {
            m.dealerCount = Math.Min(s.dealer.Length, BlackjackRules.MaxHandCards);
            Array.Copy(s.dealer, m.dealer, m.dealerCount);
        }
        for (int h = 0; h < m.handCount; h++)
        {
            var src = o.hands[h];
            var hand = m.hands[h];
            hand.count = src.cards == null ? 0 : Math.Min(src.cards.Length, BlackjackRules.MaxHandCards);
            if (hand.count > 0) Array.Copy(src.cards, hand.cards, hand.count);
            hand.stakeCents = src.stakeCents;
            hand.doubled = src.Has(BlackjackHandSnapshot.FlagDoubled);
            hand.fromSplit = src.Has(BlackjackHandSnapshot.FlagFromSplit);
            hand.splitAces = src.Has(BlackjackHandSnapshot.FlagSplitAces);
            hand.done = src.Has(BlackjackHandSnapshot.FlagDone);
        }
        return m;
    }

    /// <summary>The actions this seat may take right now (what <c>BlackjackTable.LegalActionsFor</c> says on the host), or None.</summary>
    public static BlackjackActions Legal(BlackjackTableSnapshot s, int seat)
    {
        if (s.seats == null || seat < 0 || seat >= s.seats.Length) return BlackjackActions.None;
        var o = s.seats[seat];
        if (o.key < 0 || !o.Has(BlackjackSeatSnapshot.FlagInRound) || o.Has(BlackjackSeatSnapshot.FlagAbsent) || o.ownerKey != o.key) return BlackjackActions.None;
        if (s.phase == BlackjackTablePhase.Insurance)
            return o.Has(BlackjackSeatSnapshot.FlagInsuranceDecided) ? BlackjackActions.None : BlackjackActions.Insurance | BlackjackActions.NoInsurance;
        if (s.phase == BlackjackTablePhase.Turns && s.activeSeat == seat)
        {
            var m = MirrorOf(s, seat);
            return m == null ? BlackjackActions.None : BlackjackRules.LegalActions(m);
        }
        return BlackjackActions.None;
    }

    public static bool Can(BlackjackActions legal, BlackjackAction a) => ((int)legal & 1 << (int)a) != 0;

    /// <summary>What the action costs the seat (Double / Split: the opening stake again, Insurance: half), or -1 when it is not legal.</summary>
    public static long Cost(BlackjackTableSnapshot s, int seat, BlackjackAction a)
    {
        if (!Can(Legal(s, seat), a)) return -1;
        var m = MirrorOf(s, seat);
        if (m == null)
        {
            // Insurance before any hand is mirrored cannot happen (the seat has hands), but never throw for a hint.
            return a == BlackjackAction.Double || a == BlackjackAction.Split ? s.seats[seat].betCents : a == BlackjackAction.Insurance ? s.seats[seat].betCents / 2 : 0;
        }
        return BlackjackRules.ActionCostCents(m, a);
    }

    /// <summary>Basic strategy for this seat's decision (NoInsurance in the insurance window). False without a decision.</summary>
    public static bool TryAdvice(BlackjackTableSnapshot s, int seat, out BlackjackAction action)
    {
        action = BlackjackAction.Stand;
        var legal = Legal(s, seat);
        if (legal == BlackjackActions.None) return false;
        if (s.phase == BlackjackTablePhase.Insurance) { action = BlackjackAction.NoInsurance; return true; }
        var m = MirrorOf(s, seat);
        if (m == null || m.dealerCount < 1 || m.dealer[0] == BlackjackTable.Hidden) return false;
        m.dealerCount = Math.Min(2, m.dealerCount);
        action = BlackjackRules.BasicStrategy(m);
        return true;
    }

    /// <summary>Can this seat put a stake down now? Idle, or Betting before the deadline, no stake yet, not sitting out.</summary>
    public static bool CanBet(BlackjackTableSnapshot s, int seat, double secondsLeft)
    {
        if (s.seats == null || seat < 0 || seat >= s.seats.Length) return false;
        var o = s.seats[seat];
        if (o.key < 0) return false;
        if (s.phase == BlackjackTablePhase.Idle) return true;
        if (s.phase != BlackjackTablePhase.Betting) return false;
        return o.betCents <= 0 && !o.Has(BlackjackSeatSnapshot.FlagSitOut) && secondsLeft > 0.0;
    }

    public static bool BetPlaced(BlackjackTableSnapshot s, int seat) =>
        s.seats != null && seat >= 0 && seat < s.seats.Length && s.seats[seat].betCents > 0 && s.seats[seat].ownerKey == s.seats[seat].key;

    /// <summary>The seat's total net once its round settled (FlagSettled), otherwise null.</summary>
    public static long? SettledNet(BlackjackTableSnapshot s, int seat)
    {
        if (s.seats == null || seat < 0 || seat >= s.seats.Length) return null;
        var o = s.seats[seat];
        return o.Has(BlackjackSeatSnapshot.FlagSettled) && o.ownerKey == o.key ? o.totalNetCents : (long?)null;
    }
}

/// <summary>How a 3D plate looks for an action: enabled / lit / label. Pure so the mapping from legal actions is tested.</summary>
public struct PlateState
{
    public bool enabled, lit;
    public string label;
    /// <summary>Why the plate is dark (shown when hovered): empty when it is available.</summary>
    public string reason;
}

public static class BlackjackPlates
{
    /// <summary>The look of the plate <paramref name="id"/> (Hit, Stand, Double, Split, Insurance) for a seat that may do <paramref name="legal"/>.</summary>
    public static PlateState For(string id, BlackjackActions legal, long baseStakeCents, long cashCents)
    {
        bool insuranceWindow = BlackjackSnapshotRules.Can(legal, BlackjackAction.NoInsurance) || BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance);
        switch (id)
        {
            case "Hit":
                return Plate(BlackjackSnapshotRules.Can(legal, BlackjackAction.Hit), "HIT", insuranceWindow ? "Not now" : "Not your turn");
            case "Stand":
                if (insuranceWindow) return Plate(BlackjackSnapshotRules.Can(legal, BlackjackAction.NoInsurance), "NO\nINSUR.", "Not now");
                return Plate(BlackjackSnapshotRules.Can(legal, BlackjackAction.Stand), "STAND", "Not your turn");
            case "Double":
            {
                bool can = BlackjackSnapshotRules.Can(legal, BlackjackAction.Double);
                string label = can ? "DOUBLE\n+" + CasinoRules.Dollars(baseStakeCents) : "DOUBLE";
                if (can && cashCents < baseStakeCents) return new PlateState { enabled = false, lit = false, label = label, reason = "You can't cover +" + CasinoRules.Dollars(baseStakeCents) };
                return Plate(can, label, "Not now");
            }
            case "Split":
            {
                bool can = BlackjackSnapshotRules.Can(legal, BlackjackAction.Split);
                string label = can ? "SPLIT\n+" + CasinoRules.Dollars(baseStakeCents) : "SPLIT";
                if (can && cashCents < baseStakeCents) return new PlateState { enabled = false, lit = false, label = label, reason = "You can't cover +" + CasinoRules.Dollars(baseStakeCents) };
                return Plate(can, label, "Not now");
            }
            case "Insurance":
            {
                bool can = BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance);
                long cost = baseStakeCents / 2;
                string label = can ? "INSUR.\n+" + CasinoRules.Dollars(cost) : "INSUR.";
                if (can && cashCents < cost) return new PlateState { enabled = false, lit = false, label = label, reason = "You can't cover +" + CasinoRules.Dollars(cost) };
                return Plate(can, label, "Only when the dealer shows an ace");
            }
        }
        return new PlateState { enabled = false, label = id, reason = "" };
    }

    static PlateState Plate(bool can, string label, string why) => new PlateState { enabled = can, lit = can, label = label, reason = can ? "" : why };

    /// <summary>The action a plate id stands for (Stand means NoInsurance while the insurance window is open).</summary>
    public static bool TryAction(string id, BlackjackActions legal, out BlackjackAction action)
    {
        action = BlackjackAction.Stand;
        bool window = BlackjackSnapshotRules.Can(legal, BlackjackAction.NoInsurance) || BlackjackSnapshotRules.Can(legal, BlackjackAction.Insurance);
        switch (id)
        {
            case "Hit": action = BlackjackAction.Hit; return true;
            case "Stand": action = window ? BlackjackAction.NoInsurance : BlackjackAction.Stand; return true;
            case "Double": action = BlackjackAction.Double; return true;
            case "Split": action = BlackjackAction.Split; return true;
            case "Insurance": action = BlackjackAction.Insurance; return true;
        }
        return false;
    }
}

/// <summary>Hold-to-auto-spin and the pull spacing at a slot machine: min 2.6 s between pulls (the host floors at 1 s), auto after holding 0.5 s.</summary>
public sealed class AutoSpin
{
    public const float MinSpinSeconds = 2.6f, HoldSeconds = 0.5f;

    float heldFor;
    double lastPullAt = -1000.0;

    public bool Auto { get; private set; }
    public double LastPullAt => lastPullAt;

    /// <summary>Feed the hold state every frame. Returns true while auto-spin is on.</summary>
    public bool Tick(bool held, float dt)
    {
        heldFor = held ? heldFor + dt : 0f;
        Auto = held && heldFor >= HoldSeconds;
        return Auto;
    }

    public bool CanPullNow(double now) => now - lastPullAt >= MinSpinSeconds;

    public double WaitSeconds(double now) => Math.Max(0.0, MinSpinSeconds - (now - lastPullAt));

    public void MarkPull(double now) => lastPullAt = now;

    public void StopAuto() { Auto = false; heldFor = 0f; }

    /// <summary>A pull is due: a tap (<paramref name="tapped"/>) or auto-spin, never sooner than <see cref="MinSpinSeconds"/> after the last.</summary>
    public bool ShouldPull(bool tapped, double now) => (tapped || Auto) && CanPullNow(now);
}

/// <summary>Words and numbers the controllers share.</summary>
public static class CasinoSeatText
{
    /// <summary>"12s" / "4.2s" / "" (no clock). <paramref name="seconds"/> is a deadline minus now.</summary>
    public static string Countdown(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "";
        if (seconds <= 0.0) return "0s";
        return seconds < 10.0 ? seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : Math.Ceiling(seconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    public static string Signed(long cents) => (cents > 0 ? "+" : "") + CasinoRules.Dollars(cents);

    /// <summary>"Corner 17 / 18 / 20 / 21".</summary>
    public static string SpotName(RouletteBetType type, int index)
    {
        switch (type)
        {
            case RouletteBetType.Dozen: return index == 0 ? "First dozen (1-12)" : index == 1 ? "Second dozen (13-24)" : "Third dozen (25-36)";
            case RouletteBetType.Column: return "Column " + (index + 1) + ", " + (index + 4) + ", " + (index + 7) + "...";
            case RouletteBetType.Red: return "Red";
            case RouletteBetType.Black: return "Black";
            case RouletteBetType.Odd: return "Odd";
            case RouletteBetType.Even: return "Even";
            case RouletteBetType.Low: return "1 to 18";
            case RouletteBetType.High: return "19 to 36";
        }
        var p = RouletteRules.Pockets(type, index);
        var names = new string[p.Length];
        for (int i = 0; i < p.Length; i++) names[i] = RouletteRules.PocketName(p[i]);
        string list = string.Join("-", names);
        switch (type)
        {
            case RouletteBetType.Straight: return "Straight up " + list;
            case RouletteBetType.Split: return "Split " + list;
            case RouletteBetType.Street: return "Street " + list;
            case RouletteBetType.Corner: return "Corner " + list;
            case RouletteBetType.SixLine: return "Six line " + names[0] + " to " + names[names.Length - 1];
            default: return "Top line 0-00-1-2-3";
        }
    }

    /// <summary>"pays 8 to 1".</summary>
    public static string Pays(RouletteBetType type) => "pays " + RouletteRules.Odds(type) + " to 1";

    /// <summary>The roulette limit wording for a refused set.</summary>
    public static string Say(RouletteBetError e, bool inside)
    {
        switch (e)
        {
            case RouletteBetError.AboveSpotMaximum:
                return inside ? "Inside spots max " + CasinoRules.Dollars(CasinoRules.RouletteMaxInsideCents) : "Outside spots max " + CasinoRules.Dollars(CasinoRules.RouletteMaxOutsideCents);
            case RouletteBetError.AboveTableMaximum: return "Table max " + CasinoRules.Dollars(CasinoRules.RouletteMaxSpinCents) + " a spin";
            case RouletteBetError.BelowMinimum: return "Table minimum is " + CasinoRules.Dollars(CasinoRules.RouletteMinSpotCents) + " a spot";
            case RouletteBetError.TooManyBets: return "Too many separate bets (" + RouletteRules.MaxBetsPerSpin + " at most)";
            default: return CasinoBank.Describe(e);
        }
    }
}

/// <summary>When a pull's reveal is over (the reels have stopped and the win has shown): the HUD cash stays quiet until then.</summary>
public static class SlotRevealTiming
{
    /// <summary>The presenters stop reel 2 at 1.25 + 0.4 x 2 s after the start (3.1 s when the first two reels carry the machine's top symbol: the tease), then show the win.</summary>
    public const float ReelsStopSeconds = 2.05f, TeaseStopSeconds = 3.1f, ShowSeconds = 0.35f;

    public static bool Tease(SlotEventInfo e)
    {
        if (!SlotRules.IsMachine(e.machine)) return false;
        byte top = SlotRules.TopSymbol(e.machine);
        return SlotRules.SymbolAt(e.machine, 0, e.stop0) == top && SlotRules.SymbolAt(e.machine, 1, e.stop1) == top;
    }

    /// <summary>Seconds from the event's start to the end of the reveal.</summary>
    public static float Seconds(SlotEventInfo e) => (Tease(e) ? TeaseStopSeconds : ReelsStopSeconds) + ShowSeconds;
}

/// <summary>The bar's words (the menu card, the bartender's mood, how you feel): pure, from <see cref="CasinoRules"/> and <see cref="DrunkRules"/>.</summary>
public static class BarMenu
{
    static readonly string[] Blurbs =
    {
        "Poured this morning. Possibly yesterday morning.",
        "The label just says WHISKY. In pen.",
        "Three spirits, one match. Served lit.",
        "Tap. No ice. A dollar anyway.",
    };
    static readonly string[] Effects = { "A gentle buzz", "Warms you right up", "Hits like a bin lorry", "Sobers you up a little" };
    public static readonly string[] StageNames = { "SOBER", "TIPSY", "DRUNK", "WASTED" };
    public static readonly string[] StageHex = { "7BF08A", "F2D675", "FF9A3D", "FF5A4F" };

    public static string Blurb(int drink) => drink >= 0 && drink < Blurbs.Length ? Blurbs[drink] : "";
    public static string Effect(int drink) => drink >= 0 && drink < Effects.Length ? Effects[drink] : "";

    /// <summary>Is this drink off the menu for somebody at this level? Alcohol is cut off from <see cref="CasinoBank.BarCutOffLevel"/>; water is always sold.</summary>
    public static bool Refused(float level, int drink) => CasinoRules.TryGetDrink(drink, out var d) && CasinoBank.BarRefuses(level, d);

    /// <summary>Why a drink can't be ordered right now, or empty.</summary>
    public static string WhyNot(float level, long cashCents, int drink)
    {
        if (!CasinoRules.TryGetDrink(drink, out var d)) return "That's not on the menu.";
        if (CasinoBank.BarRefuses(level, d)) return CasinoBank.Describe(CasinoError.CutOff);
        if (cashCents < d.priceCents) return "You can't cover " + CasinoRules.Dollars(d.priceCents) + ".";
        return "";
    }

    public static int Stage(float level) => Math.Max(0, Math.Min(3, (int)DrunkRules.Stage(level)));

    /// <summary>The bartender's remark for this level (empty when sober).</summary>
    public static string Warning(float level)
    {
        int stage = Stage(level);
        if (level >= CasinoBank.BarCutOffLevel) return "\"That's it. Water or the door.\"";
        return stage >= (int)DrunkStage.Wasted ? "The bartender eyes you. Maybe water?"
             : stage == (int)DrunkStage.Drunk ? "The room has a slight lean to it."
             : stage == (int)DrunkStage.Tipsy ? "Everything's a bit funnier than it was." : "";
    }

    /// <summary>"DRUNK |#######-----|" with the stage's colour (rich text; no square brackets, the strip's markup owns those).</summary>
    public static string Meter(float level)
    {
        int stage = Stage(level);
        const int cells = 12;
        int fill = Math.Max(0, Math.Min(cells, (int)Math.Round(level / DrunkRules.MaxLevel * cells)));
        return "<color=#" + StageHex[stage] + ">" + StageNames[stage] + "</color> |" + new string('#', fill) + new string('-', cells - fill) + "|";
    }

    /// <summary>The whole line: stage, meter and the bartender's remark.</summary>
    public static string FeelLine(float level)
    {
        string w = Warning(level);
        return "Feeling " + Meter(level) + (w.Length > 0 ? "   " + w : "");
    }
}

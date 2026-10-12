using System;

/// <summary>What a player (or the insurance prompt) can ask for. One byte in a request.</summary>
public enum BlackjackAction : byte { Hit = 0, Stand = 1, Double = 2, Split = 3, Insurance = 4, NoInsurance = 5 }

/// <summary>The set of actions legal right now, one bit per <see cref="BlackjackAction"/> (bit = 1 &lt;&lt; action).</summary>
[Flags]
public enum BlackjackActions : byte { None = 0, Hit = 1, Stand = 2, Double = 4, Split = 8, Insurance = 16, NoInsurance = 32 }

/// <summary>Where a round is. Insurance is only offered against an ace; Settled rounds are ready for <see cref="BlackjackRules.Settle"/>.</summary>
public enum BlackjackPhase : byte { Idle = 0, Insurance = 1, Player = 2, Settled = 3 }

/// <summary>How one hand ended.</summary>
public enum BlackjackOutcome : byte { None = 0, Lose = 1, Bust = 2, Push = 3, Win = 4, Blackjack = 5 }

/// <summary>
/// A six-deck shoe, 312 real cards dealt without replacement. A card is a byte 0..51: rank = card % 13 (0 ace, 1..8 are
/// 2..9, 9 ten, 10 jack, 11 queen, 12 king), suit = card / 13. The shoe is host-only state: guests never see it.
/// </summary>
[Serializable]
public sealed class BlackjackShoe
{
    public byte[] cards = new byte[BlackjackRules.ShoeCards];
    /// <summary>Index of the next card to deal.</summary>
    public int next = BlackjackRules.ShoeCards;
    /// <summary>How many times it has been shuffled (0 = never: the first deal shuffles).</summary>
    public int shuffles;

    public int Remaining => BlackjackRules.ShoeCards - next;
    /// <summary>The cut card is out: the next round starts with a fresh shuffle.</summary>
    public bool NeedsShuffle => shuffles == 0 || next >= BlackjackRules.CutCard;
}

/// <summary>One player hand. Cards live in a fixed buffer; only the first <see cref="count"/> are real.</summary>
[Serializable]
public sealed class BlackjackHand
{
    public byte[] cards = new byte[BlackjackRules.MaxHandCards];
    public int count;
    /// <summary>What is riding on this hand now (twice the opening stake after a double).</summary>
    public long stakeCents;
    public bool doubled, fromSplit, splitAces, done;
}

/// <summary>
/// One round between one player and the dealer: up to three hands after splits. Host state. Guests may be sent everything
/// except <c>dealer[1]</c> while <see cref="BlackjackRules.HoleHidden"/> is true.
/// </summary>
[Serializable]
public sealed class BlackjackRound
{
    public BlackjackPhase phase;
    public byte[] dealer = new byte[BlackjackRules.MaxHandCards];
    /// <summary>Dealer cards on the table. dealer[0] is the upcard, dealer[1] the hole card.</summary>
    public int dealerCount;
    public BlackjackHand[] hands = { new BlackjackHand(), new BlackjackHand(), new BlackjackHand() };
    public int handCount;
    /// <summary>The hand being played during <see cref="BlackjackPhase.Player"/>.</summary>
    public int active;
    /// <summary>The opening stake; every split hand and the double add this much again.</summary>
    public long baseStakeCents;
    /// <summary>0 when insurance was declined or never offered, otherwise half the opening stake.</summary>
    public long insuranceCents;
    /// <summary>The dealer has turned the hole card over. Always true once the round is settled.</summary>
    public bool holeRevealed;
}

/// <summary>
/// The money of a settled round, all in cents. No arrays, so it goes straight into an RPC. Nets are relative to what the
/// hand staked (a doubled win nets twice the opening stake). The host has charged <see cref="stakedCents"/> along the way
/// and now credits <see cref="returnCents"/>.
/// </summary>
[Serializable]
public struct BlackjackResult
{
    public byte handCount;
    public BlackjackOutcome outcome0, outcome1, outcome2;
    public long net0, net1, net2;
    /// <summary>+2× the insurance when the dealer had blackjack, −1× when not, 0 when none was taken.</summary>
    public long insuranceNetCents;
    /// <summary>Everything put up: hands, doubles, splits and insurance.</summary>
    public long stakedCents;
    /// <summary>Hands and insurance together.</summary>
    public long totalNetCents;
    /// <summary>stakedCents + totalNetCents: what goes back to the player, never negative.</summary>
    public long returnCents;
    public bool dealerBlackjack;
    public byte dealerTotal;

    public long HandNet(int hand) => hand == 0 ? net0 : hand == 1 ? net1 : hand == 2 ? net2 : 0;
    public BlackjackOutcome Outcome(int hand) => hand == 0 ? outcome0 : hand == 1 ? outcome1 : hand == 2 ? outcome2 : BlackjackOutcome.None;
}

/// <summary>What a long basic-strategy session came to (<see cref="BlackjackRules.SimulateBasicStrategy"/>).</summary>
[Serializable]
public struct BlackjackSimStats
{
    public long rounds, hands, initialStakeCents, wageredCents, netCents;
    public long blackjacks, doubles, splits, busts, pushes, shuffles;
    /// <summary>The house's take as a fraction of the opening stakes (0.006 = 0.6%), the way house edge is quoted.</summary>
    public double HouseEdge => initialStakeCents == 0 ? 0 : -(double)netCents / initialStakeCents;
}

/// <summary>
/// Blackjack as The Lucky Raccoon deals it (Docs/Casino.md §4.1): six decks, dealer hits soft 17, blackjack pays 3:2,
/// insurance 2:1 against an ace, the dealer peeks on an ace or a ten, double on any first two cards (after a split too),
/// split to three hands, split aces get one card each and cannot be split again, and a 21 on a split hand is not a
/// blackjack. No surrender. Pure: the only randomness is the shuffle, which takes the caller's dice.
/// <para>Flow: <see cref="Deal"/> → while <see cref="LegalActions"/> is not None, <see cref="Apply"/> → <see cref="Settle"/>.
/// The host charges the stake before Deal and <see cref="ActionCostCents"/> before each Apply, then credits the result.</para>
/// </summary>
public static class BlackjackRules
{
    public const int Decks = 6, ShoeCards = Decks * 52;
    /// <summary>The cut card sits 75% of the way in: a round that starts at or past it shuffles first.</summary>
    public const int CutCard = ShoeCards * 3 / 4;
    public const int MaxHands = 3;
    /// <summary>No hand can hold more: 21 aces make hard 21.</summary>
    public const int MaxHandCards = 22;

    // ---- cards
    public static int Rank(byte card) => card % 13;
    public static int Suit(byte card) => card / 13 % 4;
    public static bool IsAce(byte card) => card % 13 == 0;
    /// <summary>1 for an ace, 10 for ten to king, otherwise the pips.</summary>
    public static int CardValue(byte card)
    {
        int r = card % 13;
        return r >= 9 ? 10 : r + 1;
    }

    static readonly string[] rankNames = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
    static readonly string[] suitNames = { "♠", "♥", "♦", "♣" };
    /// <summary>"A♠", "10♦": for logs and a quick UI.</summary>
    public static string CardName(byte card) => rankNames[Rank(card)] + suitNames[Suit(card)];

    /// <summary>The best total of a hand and whether an ace still counts as 11 in it (a soft hand).</summary>
    public static int HandValue(byte[] cards, int count, out bool soft)
    {
        int total = 0;
        bool ace = false;
        for (int i = 0; i < count; i++)
        {
            int v = CardValue(cards[i]);
            total += v;
            ace |= v == 1;
        }
        soft = ace && total + 10 <= 21;
        return soft ? total + 10 : total;
    }

    public static int HandValue(byte[] cards, int count) => HandValue(cards, count, out _);
    public static bool IsSoft(byte[] cards, int count) { HandValue(cards, count, out bool soft); return soft; }
    public static bool IsBust(byte[] cards, int count) => HandValue(cards, count, out _) > 21;
    /// <summary>An ace and a ten-card as the first two cards. A split hand's 21 is tested by <see cref="IsBlackjack(BlackjackHand)"/>.</summary>
    public static bool IsNatural(byte[] cards, int count) => count == 2 && HandValue(cards, 2, out _) == 21;

    public static int HandValue(BlackjackHand hand) => HandValue(hand.cards, hand.count, out _);
    public static bool IsBlackjack(BlackjackHand hand) => !hand.fromSplit && IsNatural(hand.cards, hand.count);
    public static int DealerValue(BlackjackRound round) => HandValue(round.dealer, round.dealerCount, out _);
    public static bool DealerHasBlackjack(BlackjackRound round) => IsNatural(round.dealer, round.dealerCount);

    /// <summary>House rule: the dealer draws on 16 or less and on a soft 17, and stands on everything else.</summary>
    public static bool DealerMustHit(byte[] cards, int count)
    {
        int total = HandValue(cards, count, out bool soft);
        return total < 17 || (total == 17 && soft);
    }

    /// <summary>True while guests must not be told <c>dealer[1]</c>.</summary>
    public static bool HoleHidden(BlackjackRound round) => round.dealerCount >= 2 && !round.holeRevealed;

    // ---- the shoe
    /// <summary>Fills the shoe with six fresh decks and shuffles them (Fisher-Yates). <paramref name="nextInt"/>(n) must return a uniform 0..n-1.</summary>
    public static void Shuffle(BlackjackShoe shoe, Func<int, int> nextInt)
    {
        var c = shoe.cards;
        for (int i = 0; i < ShoeCards; i++) c[i] = (byte)(i % 52);
        for (int i = ShoeCards - 1; i > 0; i--)
        {
            int j = nextInt(i + 1);
            if (j < 0 || j > i) throw new ArgumentOutOfRangeException(nameof(nextInt), "dice returned " + j + " for " + (i + 1));
            byte t = c[i]; c[i] = c[j]; c[j] = t;
        }
        shoe.next = 0;
        shoe.shuffles++;
    }

    /// <summary>Takes the next card. A round can never outrun the shoe: at least 78 cards remain behind the cut card.</summary>
    public static byte Draw(BlackjackShoe shoe)
    {
        if (shoe.next >= ShoeCards) throw new InvalidOperationException("the shoe is empty");
        return shoe.cards[shoe.next++];
    }

    // ---- a round
    /// <summary>
    /// Starts a round in <paramref name="round"/> (reused, nothing allocated): shuffles first if the cut card is out, deals
    /// player, dealer up, player, dealer hole, then peeks. Afterwards the phase is Insurance (ace up), Player, or already
    /// Settled (a natural on either side). Returns false and deals nothing for an illegal stake or a round still in play.
    /// </summary>
    public static bool Deal(BlackjackRound round, BlackjackShoe shoe, long stakeCents, Func<int, int> nextInt)
    {
        if (!CasinoRules.IsLegalBlackjackStake(stakeCents)) return false;
        if (round.phase == BlackjackPhase.Insurance || round.phase == BlackjackPhase.Player) return false;
        if (shoe.NeedsShuffle) Shuffle(shoe, nextInt);

        round.handCount = 1;
        round.active = 0;
        round.dealerCount = 0;
        round.baseStakeCents = stakeCents;
        round.insuranceCents = 0;
        round.holeRevealed = false;
        for (int i = 0; i < MaxHands; i++)
        {
            var h = round.hands[i];
            h.count = 0; h.stakeCents = 0; h.doubled = h.fromSplit = h.splitAces = h.done = false;
        }
        var hand = round.hands[0];
        hand.stakeCents = stakeCents;
        hand.cards[hand.count++] = Draw(shoe);
        round.dealer[round.dealerCount++] = Draw(shoe);
        hand.cards[hand.count++] = Draw(shoe);
        round.dealer[round.dealerCount++] = Draw(shoe);

        if (IsAce(round.dealer[0])) round.phase = BlackjackPhase.Insurance;
        else AfterPeek(round);
        return true;
    }

    /// <summary>Convenience for callers that do not keep a round object around.</summary>
    public static BlackjackRound Deal(BlackjackShoe shoe, long stakeCents, Func<int, int> nextInt)
    {
        var round = new BlackjackRound();
        return Deal(round, shoe, stakeCents, nextInt) ? round : null;
    }

    /// <summary>The peek (after any insurance decision): a dealer natural or a player natural ends the round on the spot.</summary>
    static void AfterPeek(BlackjackRound round)
    {
        if (DealerHasBlackjack(round) || IsBlackjack(round.hands[0]))
        {
            round.hands[0].done = true;
            round.holeRevealed = true;
            round.phase = BlackjackPhase.Settled;
        }
        else round.phase = BlackjackPhase.Player;
    }

    /// <summary>Everything the player may do now. None once the round is settled (or was never dealt).</summary>
    public static BlackjackActions LegalActions(BlackjackRound round)
    {
        if (round.phase == BlackjackPhase.Insurance) return BlackjackActions.Insurance | BlackjackActions.NoInsurance;
        if (round.phase != BlackjackPhase.Player) return BlackjackActions.None;
        var hand = round.hands[round.active];
        var legal = BlackjackActions.Hit | BlackjackActions.Stand;
        if (hand.count == 2) legal |= BlackjackActions.Double;
        if (CanSplit(round, hand)) legal |= BlackjackActions.Split;
        return legal;
    }

    /// <summary>A pair of equal value (any two ten-cards count), fewer than three hands, and never aces that came from a split.</summary>
    static bool CanSplit(BlackjackRound round, BlackjackHand hand) =>
        hand.count == 2 && round.handCount < MaxHands && !hand.splitAces && CardValue(hand.cards[0]) == CardValue(hand.cards[1]);

    public static bool IsLegal(BlackjackRound round, BlackjackAction action) => ((int)LegalActions(round) & 1 << (int)action) != 0;

    /// <summary>
    /// What the host must charge before applying a legal action: the opening stake again for a double or a split, half of
    /// it for insurance, nothing otherwise.
    /// </summary>
    public static long ActionCostCents(BlackjackRound round, BlackjackAction action) =>
        action == BlackjackAction.Double || action == BlackjackAction.Split ? round.baseStakeCents :
        action == BlackjackAction.Insurance ? round.baseStakeCents / 2 : 0L;

    /// <summary>
    /// Applies one action, drawing from the shoe. Returns false and changes nothing when it is not legal. When the last hand
    /// is finished the dealer plays out at once and the phase becomes Settled. No dice are needed: the order of the cards
    /// was fixed by the shuffle.
    /// </summary>
    public static bool Apply(BlackjackRound round, BlackjackShoe shoe, BlackjackAction action)
    {
        if (!IsLegal(round, action)) return false;
        if (round.phase == BlackjackPhase.Insurance)
        {
            round.insuranceCents = action == BlackjackAction.Insurance ? round.baseStakeCents / 2 : 0;
            AfterPeek(round);
            return true;
        }

        var hand = round.hands[round.active];
        switch (action)
        {
            case BlackjackAction.Hit:
                hand.cards[hand.count++] = Draw(shoe);
                if (HandValue(hand) >= 21) hand.done = true; // bust, or 21: nothing left to decide
                break;
            case BlackjackAction.Stand:
                hand.done = true;
                break;
            case BlackjackAction.Double:
                hand.stakeCents += round.baseStakeCents;
                hand.doubled = true;
                hand.cards[hand.count++] = Draw(shoe);
                hand.done = true;
                break;
            case BlackjackAction.Split:
            {
                // The new hand goes straight after this one, so hands are played left to right.
                for (int i = round.handCount; i > round.active + 1; i--) Swap(round.hands, i, i - 1);
                round.handCount++;
                var other = round.hands[round.active + 1];
                bool aces = IsAce(hand.cards[0]);
                other.count = 0; other.doubled = false; other.done = false;
                other.cards[other.count++] = hand.cards[1];
                other.stakeCents = round.baseStakeCents;
                hand.count = 1;
                hand.fromSplit = other.fromSplit = true;
                hand.splitAces = other.splitAces = aces;
                hand.cards[hand.count++] = Draw(shoe);
                other.cards[other.count++] = Draw(shoe);
                // Split aces get one card each and stand. Any other split hand that lands on 21 has nothing to decide.
                hand.done = aces || HandValue(hand) == 21;
                other.done = aces || HandValue(other) == 21;
                break;
            }
        }

        while (round.active < round.handCount && round.hands[round.active].done) round.active++;
        if (round.active >= round.handCount)
        {
            round.active = round.handCount - 1;
            DealerPlay(round, shoe);
        }
        return true;
    }

    static void Swap(BlackjackHand[] hands, int a, int b) { var t = hands[a]; hands[a] = hands[b]; hands[b] = t; }

    /// <summary>The dealer turns the hole card and draws to the house rule. With every player hand bust there is nothing to draw for.</summary>
    static void DealerPlay(BlackjackRound round, BlackjackShoe shoe)
    {
        round.holeRevealed = true;
        bool live = false;
        for (int i = 0; i < round.handCount; i++) live |= HandValue(round.hands[i]) <= 21;
        while (live && DealerMustHit(round.dealer, round.dealerCount)) round.dealer[round.dealerCount++] = Draw(shoe);
        round.phase = BlackjackPhase.Settled;
    }

    /// <summary>
    /// The money of a settled round (a default result while it is still in play). A natural pays 3:2 (stake × 3 / 2, exact
    /// because stakes are whole dollars; an odd cent would round down to the house). Insurance pays 2:1 when the dealer has
    /// blackjack and is lost otherwise. A bust loses even if the dealer busts too.
    /// </summary>
    public static BlackjackResult Settle(BlackjackRound round)
    {
        var r = new BlackjackResult();
        if (round.phase != BlackjackPhase.Settled) return r;
        bool dealerNatural = DealerHasBlackjack(round);
        int dealer = DealerValue(round);
        r.handCount = (byte)round.handCount;
        r.dealerBlackjack = dealerNatural;
        r.dealerTotal = (byte)dealer;
        for (int i = 0; i < round.handCount; i++)
        {
            var hand = round.hands[i];
            int total = HandValue(hand);
            bool natural = IsBlackjack(hand);
            BlackjackOutcome outcome;
            long net;
            if (total > 21) { outcome = BlackjackOutcome.Bust; net = -hand.stakeCents; }
            else if (natural && !dealerNatural) { outcome = BlackjackOutcome.Blackjack; net = hand.stakeCents * 3 / 2; }
            else if (dealerNatural) { outcome = natural ? BlackjackOutcome.Push : BlackjackOutcome.Lose; net = natural ? 0 : -hand.stakeCents; }
            else if (dealer > 21 || total > dealer) { outcome = BlackjackOutcome.Win; net = hand.stakeCents; }
            else if (total == dealer) { outcome = BlackjackOutcome.Push; net = 0; }
            else { outcome = BlackjackOutcome.Lose; net = -hand.stakeCents; }

            if (i == 0) { r.outcome0 = outcome; r.net0 = net; }
            else if (i == 1) { r.outcome1 = outcome; r.net1 = net; }
            else { r.outcome2 = outcome; r.net2 = net; }
            r.stakedCents += hand.stakeCents;
            r.totalNetCents += net;
        }
        if (round.insuranceCents > 0)
        {
            r.insuranceNetCents = dealerNatural ? round.insuranceCents * 2 : -round.insuranceCents;
            r.stakedCents += round.insuranceCents;
            r.totalNetCents += r.insuranceNetCents;
        }
        r.returnCents = r.stakedCents + r.totalNetCents;
        return r;
    }

    // ---- basic strategy
    /// <summary>
    /// The mathematically best play for these exact rules (six decks, dealer hits soft 17, double after split, no
    /// surrender), from the player's cards and the dealer's upcard only. Never takes insurance. Used by the odds test and
    /// usable as an in-game hint. Call only while <see cref="LegalActions"/> is not None.
    /// </summary>
    public static BlackjackAction BasicStrategy(BlackjackRound round)
    {
        if (round.phase == BlackjackPhase.Insurance) return BlackjackAction.NoInsurance;
        var legal = LegalActions(round);
        var hand = round.hands[round.active];
        int up = CardValue(round.dealer[0]); // 1 = ace, 2..10
        bool canDouble = (legal & BlackjackActions.Double) != 0;
        int total = HandValue(hand.cards, hand.count, out bool soft);

        if ((legal & BlackjackActions.Split) != 0 && ShouldSplit(CardValue(hand.cards[0]), up)) return BlackjackAction.Split;

        if (soft)
        {
            switch (total)
            {
                case 13: case 14: return canDouble && up >= 5 && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
                case 15: case 16: return canDouble && up >= 4 && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
                case 17: return canDouble && up >= 3 && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
                case 18:
                    if (up >= 2 && up <= 6) return canDouble ? BlackjackAction.Double : BlackjackAction.Stand;
                    return up == 7 || up == 8 ? BlackjackAction.Stand : BlackjackAction.Hit;
                case 19: return canDouble && up == 6 ? BlackjackAction.Double : BlackjackAction.Stand;
                default: return total >= 20 ? BlackjackAction.Stand : BlackjackAction.Hit; // soft 12 is a pair of aces that cannot split
            }
        }

        if (total >= 17) return BlackjackAction.Stand;
        if (total >= 13) return up >= 2 && up <= 6 ? BlackjackAction.Stand : BlackjackAction.Hit;
        if (total == 12) return up >= 4 && up <= 6 ? BlackjackAction.Stand : BlackjackAction.Hit;
        if (total == 11) return canDouble ? BlackjackAction.Double : BlackjackAction.Hit;
        if (total == 10) return canDouble && up >= 2 && up <= 9 ? BlackjackAction.Double : BlackjackAction.Hit;
        if (total == 9) return canDouble && up >= 3 && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
        return BlackjackAction.Hit;
    }

    /// <summary>Pair splitting with double after split allowed. <paramref name="value"/> is one card of the pair (1 = ace), <paramref name="up"/> the dealer's.</summary>
    static bool ShouldSplit(int value, int up)
    {
        switch (value)
        {
            case 1: case 8: return true;
            case 10: case 5: return false;
            case 9: return up >= 2 && up <= 9 && up != 7;
            case 7: case 3: case 2: return up >= 2 && up <= 7;
            case 6: return up >= 2 && up <= 6;
            case 4: return up == 5 || up == 6;
            default: return false;
        }
    }

    /// <summary>
    /// Plays <paramref name="rounds"/> rounds of <see cref="BasicStrategy"/> at a flat stake through the real state machine
    /// and one continuous shoe, and adds up the money. For the odds test and a debug readout; allocates nothing per round.
    /// </summary>
    public static BlackjackSimStats SimulateBasicStrategy(long rounds, long stakeCents, Func<int, int> nextInt)
    {
        var stats = new BlackjackSimStats();
        if (!CasinoRules.IsLegalBlackjackStake(stakeCents)) return stats;
        var shoe = new BlackjackShoe();
        var round = new BlackjackRound();
        for (long n = 0; n < rounds; n++)
        {
            Deal(round, shoe, stakeCents, nextInt);
            while (round.phase != BlackjackPhase.Settled)
            {
                var action = BasicStrategy(round);
                if (action == BlackjackAction.Double) stats.doubles++;
                else if (action == BlackjackAction.Split) stats.splits++;
                Apply(round, shoe, action);
            }
            var result = Settle(round);
            stats.rounds++;
            stats.hands += result.handCount;
            stats.initialStakeCents += stakeCents;
            stats.wageredCents += result.stakedCents;
            stats.netCents += result.totalNetCents;
            for (int i = 0; i < result.handCount; i++)
            {
                var o = result.Outcome(i);
                if (o == BlackjackOutcome.Blackjack) stats.blackjacks++;
                else if (o == BlackjackOutcome.Bust) stats.busts++;
                else if (o == BlackjackOutcome.Push) stats.pushes++;
            }
        }
        stats.shuffles = shoe.shuffles;
        return stats;
    }
}

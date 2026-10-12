using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// Blackjack at The Lucky Raccoon (Docs/Casino.md §4.1): each table rule on a stacked shoe, the shoe itself, and the house
/// edge measured over two million hands of basic strategy. Reflection because the rules live in Assembly-CSharp.
/// </summary>
public sealed class CasinoBlackjackTests
{
    const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
    static Type Rules => Type.GetType("BlackjackRules, Assembly-CSharp", true);
    static Type ShoeType => Type.GetType("BlackjackShoe, Assembly-CSharp", true);
    static Type RoundType => Type.GetType("BlackjackRound, Assembly-CSharp", true);
    static Type ActionType => Type.GetType("BlackjackAction, Assembly-CSharp", true);

    static object Call(string method, params object[] args)
    {
        foreach (var m in Rules.GetMethods(Static))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            var ps = m.GetParameters();
            bool fits = true;
            for (int i = 0; i < ps.Length && fits; i++) fits = args[i] == null || ps[i].ParameterType.IsInstanceOfType(args[i]);
            if (fits) return m.Invoke(null, args);
        }
        Assert.Fail("BlackjackRules." + method + "/" + args.Length + " not found");
        return null;
    }

    static T Get<T>(object target, string name)
    {
        var field = target.GetType().GetField(name);
        return field != null ? (T)field.GetValue(target) : (T)target.GetType().GetProperty(name).GetValue(target);
    }

    static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);

    /// <summary>A seeded xorshift64* with rejection, so every n is exactly uniform.</summary>
    static Func<int, int> Dice(ulong seed)
    {
        ulong s = seed * 0x9E3779B97F4A7C15UL + 1UL;
        return n =>
        {
            uint limit = uint.MaxValue - uint.MaxValue % (uint)n;
            while (true)
            {
                s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
                uint r = (uint)(s * 0x2545F4914F6CDD1DUL >> 32);
                if (r < limit) return (int)(r % (uint)n);
            }
        };
    }

    static readonly Func<int, int> noDice = n => throw new InvalidOperationException("a stacked shoe must not be shuffled");

    const string Ranks = "A23456789TJQK";
    /// <summary>A card of a rank (spades): "A", "2".."9", "T", "J", "Q", "K".</summary>
    static byte Card(char rank) => (byte)Ranks.IndexOf(rank);
    static byte[] Cards(string ranks) => ranks.Select(Card).ToArray();

    /// <summary>A shoe that deals these ranks in order (player, dealer up, player, dealer hole, then draws), then twos.</summary>
    static object Stacked(string ranks)
    {
        object shoe = Activator.CreateInstance(ShoeType);
        var cards = Get<byte[]>(shoe, "cards");
        for (int i = 0; i < cards.Length; i++) cards[i] = i < ranks.Length ? Card(ranks[i]) : Card('2');
        Set(shoe, "next", 0);
        Set(shoe, "shuffles", 1);
        return shoe;
    }

    static object Deal(object shoe, long stake = 500)
    {
        object round = Call("Deal", shoe, stake, noDice);
        Assert.IsNotNull(round, "the deal was refused");
        return round;
    }

    static bool Act(object round, object shoe, string action) => (bool)Call("Apply", round, shoe, Enum.Parse(ActionType, action));
    static string Phase(object round) => Get<object>(round, "phase").ToString();
    static string Legal(object round) => Call("LegalActions", round).ToString();
    static string Hint(object round) => Call("BasicStrategy", round).ToString();
    static object Hand(object round, int i) => ((Array)Get<object>(round, "hands")).GetValue(i);
    static string HandCards(object round, int i) =>
        new string(Get<byte[]>(Hand(round, i), "cards").Take(Get<int>(Hand(round, i), "count")).Select(c => Ranks[c % 13]).ToArray());
    static string DealerCards(object round) => new string(Get<byte[]>(round, "dealer").Take(Get<int>(round, "dealerCount")).Select(c => Ranks[c % 13]).ToArray());
    static object Settle(object round) => Call("Settle", round);
    static long Net(object result, int hand) => (long)result.GetType().GetMethod("HandNet").Invoke(result, new object[] { hand });
    static string Outcome(object result, int hand) => result.GetType().GetMethod("Outcome").Invoke(result, new object[] { hand }).ToString();
    static long Total(object result) => Get<long>(result, "totalNetCents");

    static int Value(string ranks) => (int)Call("HandValue", Cards(ranks), ranks.Length);
    static bool Soft(string ranks) => (bool)Call("IsSoft", Cards(ranks), ranks.Length);

    // ---- hand values

    [Test]
    public void HandsAreCountedSoftAndHard()
    {
        Assert.AreEqual(17, Value("A6")); Assert.IsTrue(Soft("A6"));
        Assert.AreEqual(17, Value("A6T")); Assert.IsFalse(Soft("A6T"));
        Assert.AreEqual(12, Value("AA")); Assert.IsTrue(Soft("AA"));
        Assert.AreEqual(21, Value("AA9")); Assert.IsTrue(Soft("AA9"));
        Assert.AreEqual(21, Value("AK")); Assert.IsTrue(Soft("AK"));
        Assert.AreEqual(20, Value("JQ")); Assert.IsFalse(Soft("JQ"));
        Assert.AreEqual(22, Value("KQ2")); Assert.IsTrue((bool)Call("IsBust", Cards("KQ2"), 3));
        Assert.AreEqual(14, Value("AAAA"));
        Assert.AreEqual(21, Value(new string('A', 21)), "the longest possible hand");
        Assert.AreEqual(0, Value(""));
        Assert.IsTrue((bool)Call("IsNatural", Cards("TA"), 2));
        Assert.IsFalse((bool)Call("IsNatural", Cards("777"), 3), "21 in three cards is not a blackjack");
        // Every card id has the right value: 52 ids, four suits of A, 2..10, J, Q, K.
        for (int c = 0; c < 52; c++)
        {
            int rank = c % 13;
            Assert.AreEqual(rank == 0 ? 1 : rank >= 9 ? 10 : rank + 1, (int)Call("CardValue", (byte)c));
        }
    }

    // ---- the dealer

    [Test]
    public void TheDealerHitsSoftSeventeenAndStandsOnHardSeventeen()
    {
        Assert.IsTrue((bool)Call("DealerMustHit", Cards("A6"), 2), "soft 17");
        Assert.IsTrue((bool)Call("DealerMustHit", Cards("A33"), 3), "soft 17 in three");
        Assert.IsFalse((bool)Call("DealerMustHit", Cards("T7"), 2), "hard 17");
        Assert.IsFalse((bool)Call("DealerMustHit", Cards("A6T"), 3), "hard 17 with an ace");
        Assert.IsFalse((bool)Call("DealerMustHit", Cards("A7"), 2), "soft 18");
        Assert.IsTrue((bool)Call("DealerMustHit", Cards("T6"), 2));

        // Player 20 against A-6: the dealer must draw, catches a 3 for soft 20 and pushes.
        object shoe = Stacked("TAT63");
        object round = Deal(shoe);
        Assert.AreEqual("Insurance", Phase(round));
        Assert.IsTrue(Act(round, shoe, "NoInsurance"));
        Assert.IsTrue(Act(round, shoe, "Stand"));
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual("A63", DealerCards(round));
        Assert.AreEqual("Push", Outcome(Settle(round), 0));
        Assert.AreEqual(0L, Total(Settle(round)));

        // Hard 17 stands and loses to 20.
        shoe = Stacked("TTT7");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Stand"));
        Assert.AreEqual("T7", DealerCards(round));
        Assert.AreEqual("Win", Outcome(Settle(round), 0));

        // 16 draws; a 5 makes 21 and beats 20.
        shoe = Stacked("TTT65");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Stand"));
        Assert.AreEqual("T65", DealerCards(round));
        Assert.AreEqual("Lose", Outcome(Settle(round), 0));
        Assert.AreEqual(-500L, Total(Settle(round)));
    }

    // ---- naturals, the peek, insurance

    [Test]
    public void BlackjackPaysThreeToTwo()
    {
        object shoe = Stacked("A9K7");
        object round = Deal(shoe, 500);
        Assert.AreEqual("Settled", Phase(round), "a natural needs no decisions");
        Assert.AreEqual("None", Legal(round));
        object r = Settle(round);
        Assert.AreEqual("Blackjack", Outcome(r, 0));
        Assert.AreEqual(750L, Net(r, 0));
        Assert.AreEqual(500L, Get<long>(r, "stakedCents"));
        Assert.AreEqual(1250L, Get<long>(r, "returnCents"));
        Assert.AreEqual(4, Get<int>(shoe, "next"), "nobody drew");

        Assert.AreEqual(30000L, Total(Settle(Deal(Stacked("K9A7"), 20000))), "$200 pays $300");
        Assert.AreEqual(1050L, Total(Settle(Deal(Stacked("QTA7"), 700))), "$7 pays $10.50, exact in cents");
    }

    [Test]
    public void TheDealerPeeksOnATenAndTheRoundEndsAtOnce()
    {
        object shoe = Stacked("TK9A");
        object round = Deal(shoe);
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual("None", Legal(round));
        Assert.IsTrue(Get<bool>(round, "holeRevealed"));
        Assert.IsFalse(Act(round, shoe, "Hit"), "no doubling into a dealer blackjack");
        object r = Settle(round);
        Assert.AreEqual("Lose", Outcome(r, 0));
        Assert.AreEqual(-500L, Total(r));
        Assert.IsTrue(Get<bool>(r, "dealerBlackjack"));

        // Blackjack against blackjack is a push.
        r = Settle(Deal(Stacked("AKKA")));
        Assert.AreEqual("Push", Outcome(r, 0));
        Assert.AreEqual(0L, Total(r));
        Assert.AreEqual(500L, Get<long>(r, "returnCents"));

        // No blackjack under the ten: play goes on and the hole card stays secret.
        round = Deal(Stacked("TK99"));
        Assert.AreEqual("Player", Phase(round));
        Assert.IsTrue((bool)Call("HoleHidden", round));
        Assert.IsFalse(Get<bool>(Settle(round), "dealerBlackjack"));
        Assert.AreEqual(0, Get<byte>(Settle(round), "handCount"), "an unfinished round settles to nothing");
    }

    [Test]
    public void InsurancePaysTwoToOneAgainstAnAce()
    {
        // Dealer has it: the insurance wins what the hand loses.
        object shoe = Stacked("TA9K");
        object round = Deal(shoe);
        Assert.AreEqual("Insurance", Phase(round));
        Assert.AreEqual("Insurance, NoInsurance", Legal(round));
        Assert.IsTrue((bool)Call("HoleHidden", round));
        Assert.IsFalse(Act(round, shoe, "Hit"), "the insurance question comes first");
        Assert.AreEqual(250L, (long)Call("ActionCostCents", round, Enum.Parse(ActionType, "Insurance")));
        Assert.IsTrue(Act(round, shoe, "Insurance"));
        Assert.AreEqual("Settled", Phase(round));
        object r = Settle(round);
        Assert.AreEqual(500L, Get<long>(r, "insuranceNetCents"));
        Assert.AreEqual(-500L, Net(r, 0));
        Assert.AreEqual(0L, Total(r));
        Assert.AreEqual(750L, Get<long>(r, "stakedCents"));
        Assert.AreEqual(750L, Get<long>(r, "returnCents"));

        // Declined: the whole stake goes.
        shoe = Stacked("TA9K");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "NoInsurance"));
        Assert.AreEqual(-500L, Total(Settle(round)));
        Assert.AreEqual(0L, Get<long>(Settle(round), "insuranceNetCents"));

        // Dealer does not have it: the insurance is lost and the hand plays on (19 beats the dealer's soft 18).
        shoe = Stacked("TA952");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Insurance"));
        Assert.AreEqual("Player", Phase(round));
        Assert.IsTrue(Act(round, shoe, "Stand"));
        r = Settle(round);
        Assert.AreEqual("A52", DealerCards(round));
        Assert.AreEqual(-250L, Get<long>(r, "insuranceNetCents"));
        Assert.AreEqual(500L, Net(r, 0));
        Assert.AreEqual(250L, Total(r));
        Assert.AreEqual(1000L, Get<long>(r, "returnCents"));

        // "Even money": an insured blackjack against a dealer blackjack nets exactly the stake.
        shoe = Stacked("AAKK");
        round = Deal(shoe);
        Assert.AreEqual("Insurance", Phase(round), "the player's own natural waits for the insurance answer");
        Assert.IsTrue(Act(round, shoe, "Insurance"));
        Assert.AreEqual(500L, Total(Settle(round)));
        // And an insured blackjack against no dealer blackjack: 3:2 less the lost insurance.
        shoe = Stacked("AAK5");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Insurance"));
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual(750L - 250L, Total(Settle(round)));

        // Insurance is only ever offered against an ace.
        Assert.AreEqual("Player", Phase(Deal(Stacked("T9T7"))));
        Assert.IsFalse(Legal(Deal(Stacked("TK99"))).Contains("Insurance"));
    }

    // ---- doubling and splitting

    [Test]
    public void DoubleIsOneCardOnAnyFirstTwo()
    {
        object shoe = Stacked("566T9K");
        object round = Deal(shoe);
        Assert.AreEqual("Hit, Stand, Double", Legal(round));
        Assert.AreEqual(500L, (long)Call("ActionCostCents", round, Enum.Parse(ActionType, "Double")));
        Assert.IsTrue(Act(round, shoe, "Double"));
        Assert.AreEqual("Settled", Phase(round), "one card and the hand is over");
        Assert.AreEqual("569", HandCards(round, 0));
        Assert.AreEqual("6TK", DealerCards(round));
        object r = Settle(round);
        Assert.AreEqual(1000L, Net(r, 0));
        Assert.AreEqual(1000L, Get<long>(r, "stakedCents"));
        Assert.AreEqual(2000L, Get<long>(r, "returnCents"));

        // Any two cards, even a hard 19; never after a hit.
        Assert.IsTrue(Legal(Deal(Stacked("T797"))).Contains("Double"));
        shoe = Stacked("263T4");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Hit"));
        Assert.AreEqual("Hit, Stand", Legal(round));
        Assert.IsFalse(Act(round, shoe, "Double"));
        Assert.AreEqual(5, Get<int>(shoe, "next"), "a refused action draws nothing");

        // A lost double loses both stakes.
        shoe = Stacked("56T956");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Double"));
        Assert.AreEqual(-1000L, Total(Settle(round)));
    }

    [Test]
    public void SplitAcesGetOneCardEachAndTwentyOneIsNotBlackjack()
    {
        object shoe = Stacked("A6ATK54");
        object round = Deal(shoe);
        Assert.AreEqual("Hit, Stand, Double, Split", Legal(round));
        Assert.AreEqual(500L, (long)Call("ActionCostCents", round, Enum.Parse(ActionType, "Split")));
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("Settled", Phase(round), "split aces are not played");
        Assert.AreEqual(2, Get<int>(round, "handCount"));
        Assert.AreEqual("AK", HandCards(round, 0));
        Assert.AreEqual("A5", HandCards(round, 1));
        Assert.AreEqual("6T4", DealerCards(round));
        object r = Settle(round);
        Assert.AreEqual("Win", Outcome(r, 0), "ace-king after a split is 21, not a blackjack");
        Assert.AreEqual(500L, Net(r, 0), "so it pays even money");
        Assert.AreEqual("Lose", Outcome(r, 1));
        Assert.AreEqual(0L, Total(r));
        Assert.AreEqual(1000L, Get<long>(r, "stakedCents"));
        Assert.IsFalse((bool)Call("IsBlackjack", Hand(round, 0)));

        // Aces cannot be split again: a second pair of aces just stands as soft 12.
        shoe = Stacked("A6ATAA5");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual(2, Get<int>(round, "handCount"));
        Assert.AreEqual("AA", HandCards(round, 0));

        // A ten-value split hand that draws an ace is 21 but not a blackjack either.
        shoe = Stacked("K6QTA97");
        round = Deal(shoe);
        Assert.IsTrue(Legal(round).Contains("Split"), "any two ten-cards are a pair");
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("KA", HandCards(round, 0));
        Assert.AreEqual(1, Get<int>(round, "active"), "21 has nothing to decide: on to the next hand");
        Assert.IsTrue(Act(round, shoe, "Stand"));
        r = Settle(round);
        Assert.AreEqual("6T7", DealerCards(round));
        Assert.AreEqual("Win", Outcome(r, 0));
        Assert.AreEqual(500L, Net(r, 0));
    }

    [Test]
    public void SplitToThreeHandsAndDoubleAfterSplit()
    {
        // 8-8 against a 6. Split, catch another 8, split again, then double two of the three hands.
        object shoe = Stacked("868T832TA95");
        object round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual(2, Get<int>(round, "handCount"));
        Assert.AreEqual("88", HandCards(round, 0));
        Assert.AreEqual("83", HandCards(round, 1));
        Assert.AreEqual("Hit, Stand, Double, Split", Legal(round), "a second split is allowed");
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual(3, Get<int>(round, "handCount"));
        Assert.AreEqual("82", HandCards(round, 0));
        Assert.AreEqual("8T", HandCards(round, 1), "the new hand sits next to the one it came from");
        Assert.AreEqual("83", HandCards(round, 2));
        Assert.AreEqual(0, Get<int>(round, "active"));

        Assert.AreEqual("Hit, Stand, Double", Legal(round), "double after split");
        Assert.IsTrue(Act(round, shoe, "Double"));
        Assert.AreEqual("82A", HandCards(round, 0));
        Assert.AreEqual(1, Get<int>(round, "active"));
        Assert.IsTrue(Act(round, shoe, "Stand"));
        Assert.AreEqual(2, Get<int>(round, "active"));
        Assert.IsTrue(Act(round, shoe, "Double"));
        Assert.AreEqual("839", HandCards(round, 2));
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual("6T5", DealerCards(round));

        object r = Settle(round);
        Assert.AreEqual(3, Get<byte>(r, "handCount"));
        Assert.AreEqual("Push", Outcome(r, 0)); Assert.AreEqual(0L, Net(r, 0));
        Assert.AreEqual("Lose", Outcome(r, 1)); Assert.AreEqual(-500L, Net(r, 1));
        Assert.AreEqual("Lose", Outcome(r, 2)); Assert.AreEqual(-1000L, Net(r, 2));
        Assert.AreEqual(-1500L, Total(r));
        Assert.AreEqual(2500L, Get<long>(r, "stakedCents"));
        Assert.AreEqual(1000L, Get<long>(r, "returnCents"));

        // Three hands is the limit: a third pair of eights cannot be split.
        shoe = Stacked("868T8882");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("88", HandCards(round, 0));
        Assert.AreEqual("Hit, Stand, Double", Legal(round));
        Assert.IsFalse(Act(round, shoe, "Split"));

        // Not a pair, no split.
        Assert.IsFalse(Legal(Deal(Stacked("8697"))).Contains("Split"));
    }

    [Test]
    public void ABustLosesAtOnceAndTheDealerDoesNotDraw()
    {
        object shoe = Stacked("T66TK");
        object round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Hit"));
        Assert.AreEqual("Settled", Phase(round));
        Assert.AreEqual("6T", DealerCards(round), "16 would have to draw, but there is nobody left to beat");
        Assert.IsTrue(Get<bool>(round, "holeRevealed"));
        object r = Settle(round);
        Assert.AreEqual("Bust", Outcome(r, 0));
        Assert.AreEqual(-500L, Total(r));
        Assert.AreEqual(0L, Get<long>(r, "returnCents"));

        // The dealer busting pays every hand still standing.
        shoe = Stacked("T6TTK");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Stand"));
        Assert.AreEqual(500L, Total(Settle(round)));
        Assert.AreEqual(26, Get<byte>(Settle(round), "dealerTotal"));
    }

    [Test]
    public void StakesOutsideTheTableLimitsAreNotDealt()
    {
        foreach (long bad in new long[] { 0, -500, 100, 400, 499, 550, 20100, 1000000 })
        {
            object shoe = Stacked("T6T7");
            Assert.IsNull(Call("Deal", shoe, bad, noDice), bad + " cents");
            Assert.AreEqual(0, Get<int>(shoe, "next"), "a refused deal takes no cards");
        }
        foreach (long good in new long[] { 500, 600, 2500, 20000 }) Assert.IsNotNull(Call("Deal", Stacked("T6T7"), good, noDice), good + " cents");

        // A round in play cannot be dealt over.
        object live = Stacked("T6T7");
        object round = Deal(live);
        Assert.IsFalse((bool)Call("Deal", round, live, 500L, noDice));
        Assert.AreEqual(4, Get<int>(live, "next"));
        Assert.IsTrue(Act(round, live, "Stand"));
        Assert.IsTrue((bool)Call("Deal", round, live, 500L, noDice), "a settled round object is reused for the next deal");
    }

    // ---- the shoe

    static void AssertSixDecks(byte[] cards)
    {
        Assert.AreEqual(312, cards.Length);
        var perCard = new int[52];
        foreach (byte c in cards) { Assert.Less(c, 52); perCard[c]++; }
        for (int c = 0; c < 52; c++) Assert.AreEqual(6, perCard[c], "six of card " + c);
        var perRank = new int[13];
        foreach (byte c in cards) perRank[c % 13]++;
        foreach (int n in perRank) Assert.AreEqual(24, n, "24 of each rank");
        Assert.AreEqual(96, cards.Count(c => c % 13 >= 9), "96 ten-cards");
    }

    [Test]
    public void TheShoeIsSixRealDecksShuffledByTheDice()
    {
        Assert.AreEqual(312, (int)Rules.GetField("ShoeCards", Static).GetValue(null));
        Assert.AreEqual(234, (int)Rules.GetField("CutCard", Static).GetValue(null), "the cut card is 75% in");

        object a = Activator.CreateInstance(ShoeType), b = Activator.CreateInstance(ShoeType), c = Activator.CreateInstance(ShoeType);
        Assert.IsTrue(Get<bool>(a, "NeedsShuffle"), "a new shoe is shuffled before its first deal");
        Call("Shuffle", a, Dice(1));
        Call("Shuffle", b, Dice(1));
        Call("Shuffle", c, Dice(2));
        AssertSixDecks(Get<byte[]>(a, "cards"));
        AssertSixDecks(Get<byte[]>(c, "cards"));
        CollectionAssert.AreEqual(Get<byte[]>(a, "cards"), Get<byte[]>(b, "cards"), "same dice, same shoe");
        CollectionAssert.AreNotEqual(Get<byte[]>(a, "cards"), Get<byte[]>(c, "cards"));
        Assert.AreEqual(0, Get<int>(a, "next"));
        Assert.AreEqual(312, Get<int>(a, "Remaining"));
        Assert.IsFalse(Get<bool>(a, "NeedsShuffle"));

        // Not left in order: hardly any card is still where a fresh pack would have it.
        var cards = Get<byte[]>(a, "cards");
        Assert.Less(Enumerable.Range(0, 312).Count(i => cards[i] == i % 52), 30);

        // Fisher-Yates is fair: over many shuffles every position sees every rank about 1/13 of the time.
        var dice = Dice(77);
        var acesAt = new int[312];
        const int shuffles = 2600;
        for (int n = 0; n < shuffles; n++)
        {
            Call("Shuffle", a, dice);
            for (int i = 0; i < 312; i++) if (cards[i] % 13 == 0) acesAt[i]++;
        }
        Assert.AreEqual(shuffles * 24, acesAt.Sum());
        // Expect 200 aces per position, standard deviation about 13.6.
        Assert.That(acesAt.Min(), Is.GreaterThan(140), "a position is starved of aces");
        Assert.That(acesAt.Max(), Is.LessThan(260), "a position is rich in aces");
    }

    [Test]
    public void CardsAreDealtWithoutReplacementAndReshuffledAtTheCutCard()
    {
        var dice = Dice(4242);
        object shoe = Activator.CreateInstance(ShoeType);
        object round = Activator.CreateInstance(RoundType);
        int reshuffles = 0, deepest = 0;
        var seen = new int[52];
        for (int n = 0; n < 3000; n++)
        {
            bool due = Get<bool>(shoe, "NeedsShuffle");
            int before = Get<int>(shoe, "next"), shuffles = Get<int>(shoe, "shuffles");
            Assert.AreEqual(n == 0 || before >= 234, due, "a shuffle is due exactly when the cut card is out");
            Assert.IsTrue((bool)Call("Deal", round, shoe, 500L, dice));
            if (due)
            {
                Assert.AreEqual(shuffles + 1, Get<int>(shoe, "shuffles"));
                AssertSixDecks(Get<byte[]>(shoe, "cards"));
                Array.Clear(seen, 0, seen.Length);
                before = 0;
                reshuffles++;
            }
            else Assert.AreEqual(shuffles, Get<int>(shoe, "shuffles"), "no shuffle in the middle of a shoe");

            while (Phase(round) != "Settled") Assert.IsTrue(Act(round, shoe, Hint(round)));

            // Every card on the table came off the shoe, in order, once.
            int after = Get<int>(shoe, "next");
            int onTable = Get<int>(round, "dealerCount");
            for (int h = 0; h < Get<int>(round, "handCount"); h++) onTable += Get<int>(Hand(round, h), "count");
            Assert.AreEqual(after - before, onTable, "cards on the table = cards taken from the shoe");
            var cards = Get<byte[]>(shoe, "cards");
            for (int i = before; i < after; i++) Assert.LessOrEqual(++seen[cards[i]], 6, "a seventh copy of a card was dealt");
            Assert.Less(after, 312);
            deepest = Math.Max(deepest, after);
        }
        Assert.Greater(reshuffles, 50);
        Assert.GreaterOrEqual(deepest, 234, "play reaches the cut card");
        Assert.Less(deepest, 234 + 40, "and only finishes the round it interrupted");
    }

    // ---- basic strategy

    /// <summary>What basic strategy says for a two-card hand against an upcard (the hole card is a 5, so nobody has a natural).</summary>
    static string HintFor(string hand, char up)
    {
        object shoe = Stacked("" + hand[0] + up + hand[1] + "5");
        object round = Deal(shoe);
        if (up == 'A')
        {
            Assert.AreEqual("NoInsurance", Hint(round), "basic strategy never insures");
            Assert.IsTrue(Act(round, shoe, "NoInsurance"));
        }
        return Hint(round);
    }

    [TestCase("T6", 'T', "Hit")]
    [TestCase("T6", '7', "Hit")]
    [TestCase("T6", '6', "Stand")]
    [TestCase("T2", '3', "Hit")]
    [TestCase("T2", '4', "Stand")]
    [TestCase("T7", 'A', "Stand")]
    [TestCase("56", 'A', "Double")] // 11 doubles against everything when the dealer hits soft 17
    [TestCase("64", 'T', "Hit")]
    [TestCase("64", '9', "Double")]
    [TestCase("63", '2', "Hit")]
    [TestCase("63", '3', "Double")]
    [TestCase("53", '6', "Hit")]
    [TestCase("A2", '5', "Double")]
    [TestCase("A2", '4', "Hit")]
    [TestCase("A6", '2', "Hit")]
    [TestCase("A6", '3', "Double")]
    [TestCase("A7", '2', "Double")] // soft 18 doubles against a 2 when the dealer hits soft 17
    [TestCase("A7", '7', "Stand")]
    [TestCase("A7", '9', "Hit")]
    [TestCase("A7", 'A', "Hit")]
    [TestCase("A8", '6', "Double")] // and soft 19 doubles against a 6
    [TestCase("A8", '5', "Stand")]
    [TestCase("A9", '6', "Stand")]
    [TestCase("AA", 'T', "Split")]
    [TestCase("88", 'A', "Split")]
    [TestCase("TT", '6', "Stand")]
    [TestCase("99", '7', "Stand")]
    [TestCase("99", '9', "Split")]
    [TestCase("99", 'T', "Stand")]
    [TestCase("77", '7', "Split")]
    [TestCase("77", '8', "Hit")]
    [TestCase("66", '6', "Split")]
    [TestCase("66", '7', "Hit")]
    [TestCase("55", '6', "Double")]
    [TestCase("44", '5', "Split")] // only because double after split is allowed
    [TestCase("44", '4', "Hit")]
    [TestCase("33", '7', "Split")]
    [TestCase("22", '8', "Hit")]
    public void BasicStrategyKnowsTheTable(string hand, char up, string expected) => Assert.AreEqual(expected, HintFor(hand, up));

    [Test]
    public void BasicStrategyFallsBackWhenAPlayIsNotAllowed()
    {
        // 11 in three cards cannot double: hit. Soft 18 in three cards against a 3 cannot double: stand.
        object shoe = Stacked("263T6");
        object round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Hit"));
        Assert.AreEqual("236", HandCards(round, 0));
        Assert.AreEqual("Hit", Hint(round));
        shoe = Stacked("A32T5");
        round = Deal(shoe);
        Assert.IsTrue(Act(round, shoe, "Hit"));
        Assert.AreEqual("A25", HandCards(round, 0));
        Assert.AreEqual("Stand", Hint(round));
        // A third pair of eights cannot be split: it is a hard 16 against a ten, so hit.
        shoe = Stacked("8T85888");
        round = Deal(shoe);
        Assert.AreEqual("Split", Hint(round));
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("Split", Hint(round));
        Assert.IsTrue(Act(round, shoe, "Split"));
        Assert.AreEqual("88", HandCards(round, 0));
        Assert.AreEqual("Hit", Hint(round));
    }

    // ---- the odds

    [Test, Timeout(120000)]
    public void BasicStrategyLosesAboutHalfAPercent()
    {
        const long rounds = 2000000, stake = 1000;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        object stats = Call("SimulateBasicStrategy", rounds, stake, Dice(20261009));
        watch.Stop();
        double edge = Get<double>(stats, "HouseEdge");
        long net = Get<long>(stats, "netCents"), initial = Get<long>(stats, "initialStakeCents");
        UnityEngine.Debug.Log("[CasinoBlackjackTests] " + rounds + " rounds of basic strategy in " + watch.ElapsedMilliseconds + " ms: house edge " +
            (edge * 100).ToString("F3") + "% of the opening stake (net " + net + " cents on " + initial + "), " +
            Get<long>(stats, "hands") + " hands, " + Get<long>(stats, "blackjacks") + " blackjacks, " + Get<long>(stats, "doubles") + " doubles, " +
            Get<long>(stats, "splits") + " splits, " + Get<long>(stats, "shuffles") + " shuffles, wagered " + Get<long>(stats, "wageredCents"));
        Assert.AreEqual(rounds, Get<long>(stats, "rounds"));
        Assert.AreEqual(rounds * stake, initial);
        Assert.AreEqual(-(double)net / initial, edge, 1e-12);
        Assert.That(edge, Is.InRange(0.002, 0.012), "house edge with basic strategy should be about 0.6%");
        // Sanity on the sample itself: naturals come about once in 21 hands, and the shoe really was reshuffled.
        Assert.That(Get<long>(stats, "blackjacks") / (double)rounds, Is.InRange(0.044, 0.049));
        Assert.Greater(Get<long>(stats, "shuffles"), rounds / 60);
        Assert.Greater(Get<long>(stats, "wageredCents"), initial, "doubles and splits add to the action");
    }
}

using System;

/// <summary>The fourteen roulette bets, inside bets first (Docs/Casino.md §4.2).</summary>
public enum RouletteBetType : byte
{
    Straight = 0, Split = 1, Street = 2, Corner = 3, SixLine = 4, TopLine = 5,
    Dozen = 6, Column = 7, Red = 8, Black = 9, Odd = 10, Even = 11, Low = 12, High = 13,
}

/// <summary>
/// One chip stack on one spot: six bytes on the wire. What <see cref="index"/> means depends on the type, see
/// <see cref="RouletteRules.SpotCount"/>; <see cref="RouletteRules.Pockets"/> lists the numbers any bet covers.
/// </summary>
[Serializable]
public struct RouletteBet
{
    public RouletteBetType type;
    public byte index;
    public int cents;

    public RouletteBet(RouletteBetType type, int index, int cents) { this.type = type; this.index = (byte)index; this.cents = cents; }
}

/// <summary>Why the table refuses a set of bets. Checked in this order.</summary>
public enum RouletteBetError : byte { Ok = 0, NoBets, TooManyBets, BadSpot, BadAmount, BelowMinimum, AboveSpotMaximum, AboveTableMaximum }

/// <summary>What one spin did to one player's bets. <see cref="returnCents"/> is what the host credits (stakes of winning bets included).</summary>
[Serializable]
public struct RouletteResult
{
    public byte pocket;
    public long stakedCents, returnCents;
    public long NetCents => returnCents - stakedCents;
}

/// <summary>
/// American roulette (Docs/Casino.md §4.2): 38 pockets, the real wheel order and colours, every bet a real layout allows,
/// and its true payout. Pure and deterministic given the dice. A pocket is a byte: 0, 1..36, and <see cref="DoubleZero"/>.
/// </summary>
public static class RouletteRules
{
    public const int PocketCount = 38;
    /// <summary>The pocket id of 00. Pockets 0..36 are their own numbers.</summary>
    public const byte DoubleZero = 37;
    public const int BetTypeCount = 14;
    /// <summary>The most chip stacks one spin's request may carry.</summary>
    public const int MaxBetsPerSpin = 64;

    /// <summary>Pockets clockwise round a real American wheel, starting at 0. For drawing the wheel only: every pocket is equally likely.</summary>
    public static readonly byte[] WheelOrder =
    {
        0, 28, 9, 26, 30, 11, 7, 20, 32, 17, 5, 22, 34, 15, 3, 24, 36, 13, 1,
        DoubleZero, 27, 10, 25, 29, 12, 8, 19, 31, 18, 6, 21, 33, 16, 4, 23, 35, 14, 2,
    };

    static readonly byte[] reds = { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

    /// <summary>What a winning bet pays to one, by type: 35, 17, 11, 8, 5, 6, then 2 for dozens and columns and 1 for the even chances.</summary>
    static readonly int[] odds = { 35, 17, 11, 8, 5, 6, 2, 2, 1, 1, 1, 1, 1, 1 };

    // Every spot of every type as a bit mask over the 38 pockets, built once from the layout:
    // three columns by twelve rows, number n at row (n-1)/3, column (n-1)%3, with 0 and 00 above the first row.
    static readonly ulong[][] masks = BuildMasks();
    static readonly ulong redMask = Mask(reds);

    static ulong Mask(params byte[] pockets)
    {
        ulong m = 0;
        foreach (byte p in pockets) m |= 1UL << p;
        return m;
    }

    static ulong[][] BuildMasks()
    {
        var all = new ulong[BetTypeCount][];

        var straight = new ulong[PocketCount];
        for (int p = 0; p < PocketCount; p++) straight[p] = 1UL << p;
        all[(int)RouletteBetType.Straight] = straight;

        // Splits: 24 across a row (0..23), 33 down a column (24..56), then 0-1, 0-2, 00-2, 00-3 and 0-00 (57..61).
        var split = new ulong[62];
        int k = 0;
        for (byte n = 1; n <= 36; n++) if (n % 3 != 0) split[k++] = Mask(n, (byte)(n + 1));
        for (byte n = 1; n <= 33; n++) split[k++] = Mask(n, (byte)(n + 3));
        split[k++] = Mask(0, 1);
        split[k++] = Mask(0, 2);
        split[k++] = Mask(DoubleZero, 2);
        split[k++] = Mask(DoubleZero, 3);
        split[k++] = Mask(0, DoubleZero);
        all[(int)RouletteBetType.Split] = split;

        // Streets: the twelve rows (0..11), then the zero trios 0-1-2, 0-00-2 and 00-2-3 (12..14).
        var street = new ulong[15];
        for (int r = 0; r < 12; r++) street[r] = Mask((byte)(3 * r + 1), (byte)(3 * r + 2), (byte)(3 * r + 3));
        street[12] = Mask(0, 1, 2);
        street[13] = Mask(0, DoubleZero, 2);
        street[14] = Mask(DoubleZero, 2, 3);
        all[(int)RouletteBetType.Street] = street;

        // Corners: index r*2 + c is the block whose lowest number is 3r + c + 1 (r 0..10, c 0..1).
        var corner = new ulong[22];
        for (int r = 0; r < 11; r++)
            for (int c = 0; c < 2; c++)
            {
                byte n = (byte)(3 * r + c + 1);
                corner[r * 2 + c] = Mask(n, (byte)(n + 1), (byte)(n + 3), (byte)(n + 4));
            }
        all[(int)RouletteBetType.Corner] = corner;

        // Six lines: rows r and r + 1 (r 0..10).
        var six = new ulong[11];
        for (int r = 0; r < 11; r++) six[r] = street[r] | street[r + 1];
        all[(int)RouletteBetType.SixLine] = six;

        all[(int)RouletteBetType.TopLine] = new[] { Mask(0, DoubleZero, 1, 2, 3) };

        var dozen = new ulong[3];
        var column = new ulong[3];
        ulong red = Mask(reds), black = 0, odd = 0, even = 0, low = 0, high = 0;
        for (int n = 1; n <= 36; n++)
        {
            ulong bit = 1UL << n;
            dozen[(n - 1) / 12] |= bit;
            column[(n - 1) % 3] |= bit;
            if ((red & bit) == 0) black |= bit;
            if (n % 2 == 1) odd |= bit; else even |= bit;
            if (n <= 18) low |= bit; else high |= bit;
        }
        all[(int)RouletteBetType.Dozen] = dozen;
        all[(int)RouletteBetType.Column] = column;
        all[(int)RouletteBetType.Red] = new[] { red };
        all[(int)RouletteBetType.Black] = new[] { black };
        all[(int)RouletteBetType.Odd] = new[] { odd };
        all[(int)RouletteBetType.Even] = new[] { even };
        all[(int)RouletteBetType.Low] = new[] { low };
        all[(int)RouletteBetType.High] = new[] { high };
        return all;
    }

    // ---- pockets
    public static bool IsPocket(int pocket) => pocket >= 0 && pocket < PocketCount;
    /// <summary>"0", "00" or the number.</summary>
    public static string PocketName(int pocket) => pocket == DoubleZero ? "00" : pocket.ToString();
    public static bool IsRed(int pocket) => IsPocket(pocket) && (redMask >> pocket & 1UL) != 0;
    /// <summary>Black is every number that is not red; 0 and 00 are green.</summary>
    public static bool IsBlack(int pocket) => pocket >= 1 && pocket <= 36 && !IsRed(pocket);
    public static bool IsGreen(int pocket) => pocket == 0 || pocket == DoubleZero;

    /// <summary>Where a pocket sits on the wheel (an index into <see cref="WheelOrder"/>), or -1.</summary>
    public static int WheelSlot(int pocket) => IsPocket(pocket) ? Array.IndexOf(WheelOrder, (byte)pocket) : -1;

    /// <summary>One spin: every pocket is equally likely. <paramref name="nextInt"/>(n) must return a uniform 0..n-1.</summary>
    public static byte Spin(Func<int, int> nextInt)
    {
        int p = nextInt(PocketCount);
        if (!IsPocket(p)) throw new ArgumentOutOfRangeException(nameof(nextInt), "dice returned " + p + " for " + PocketCount);
        return (byte)p;
    }

    // ---- spots
    /// <summary>
    /// How many spots a bet type has; a bet's index is 0..count-1.
    /// Straight 38: the pocket. Split 62: see <see cref="SplitIndex"/>. Street 15: rows 0..11 (1-2-3 is 0), then 0-1-2 (12),
    /// 0-00-2 (13), 00-2-3 (14). Corner 22: see <see cref="CornerIndex"/>. Six line 11: the upper row, 0..10. Top line 1.
    /// Dozen 3 and Column 3: column c holds c+1, c+4, ... The six even chances have one spot each (index 0).
    /// </summary>
    public static int SpotCount(RouletteBetType type) => (int)type < BetTypeCount ? masks[(int)type].Length : 0;

    public static bool IsLegalSpot(RouletteBetType type, int index) => index >= 0 && index < SpotCount(type);
    /// <summary>Inside bets are the number bets (straight to top line); they have the lower spot maximum.</summary>
    public static bool IsInside(RouletteBetType type) => type <= RouletteBetType.TopLine;
    /// <summary>What a winning bet of this type pays to one (35 for a straight).</summary>
    public static int Odds(RouletteBetType type) => (int)type < BetTypeCount ? odds[(int)type] : 0;

    static ulong MaskOf(RouletteBetType type, int index) => IsLegalSpot(type, index) ? masks[(int)type][index] : 0UL;

    /// <summary>The pockets a spot covers, lowest first (00 last). Empty for an illegal spot.</summary>
    public static byte[] Pockets(RouletteBetType type, int index)
    {
        ulong m = MaskOf(type, index);
        int n = 0;
        for (int p = 0; p < PocketCount; p++) if ((m >> p & 1UL) != 0) n++;
        var list = new byte[n];
        n = 0;
        for (int p = 0; p < PocketCount; p++) if ((m >> p & 1UL) != 0) list[n++] = (byte)p;
        return list;
    }

    /// <summary>
    /// The split between two pockets, or -1 when they do not share an edge on the layout. Order does not matter. Legal: two
    /// neighbours in a row, two neighbours down a column, 0-1, 0-2, 00-2, 00-3 and 0-00.
    /// </summary>
    public static int SplitIndex(int a, int b) => a == b || !IsPocket(a) || !IsPocket(b) ? -1 : Find(RouletteBetType.Split, 1UL << a | 1UL << b);

    /// <summary>The street holding exactly these three pockets (a row, or 0-1-2, 0-00-2, 00-2-3), or -1.</summary>
    public static int StreetIndex(int a, int b, int c) =>
        !IsPocket(a) || !IsPocket(b) || !IsPocket(c) || a == b || b == c || a == c ? -1 : Find(RouletteBetType.Street, 1UL << a | 1UL << b | 1UL << c);

    /// <summary>The corner whose lowest number is <paramref name="lowest"/> (it covers n, n+1, n+3, n+4), or -1 when there is none.</summary>
    public static int CornerIndex(int lowest)
    {
        if (lowest < 1 || lowest > 32 || lowest % 3 == 0) return -1;
        return (lowest - 1) / 3 * 2 + (lowest - 1) % 3;
    }

    /// <summary>The six line whose upper row starts at <paramref name="lowest"/> (1, 4, ... 31), or -1.</summary>
    public static int SixLineIndex(int lowest) => lowest >= 1 && lowest <= 31 && lowest % 3 == 1 ? (lowest - 1) / 3 : -1;

    static int Find(RouletteBetType type, ulong mask)
    {
        var list = masks[(int)type];
        for (int i = 0; i < list.Length; i++) if (list[i] == mask) return i;
        return -1;
    }

    // ---- winning and paying
    /// <summary>True when the bet wins on this pocket. An illegal bet covers nothing.</summary>
    public static bool Covers(RouletteBet bet, int pocket) => IsPocket(pocket) && (MaskOf(bet.type, bet.index) >> pocket & 1UL) != 0;

    /// <summary>The winnings on this pocket, in cents, stake not included: stake × odds, or 0 when the bet loses.</summary>
    public static long Payout(RouletteBet bet, int pocket) => Covers(bet, pocket) ? (long)bet.cents * Odds(bet.type) : 0L;

    /// <summary>What comes back across the felt: stake plus winnings, or 0 when the bet loses.</summary>
    public static long Return(RouletteBet bet, int pocket) => Covers(bet, pocket) ? (long)bet.cents * (Odds(bet.type) + 1) : 0L;

    /// <summary>The first <paramref name="count"/> bets added up.</summary>
    public static long TotalStake(RouletteBet[] bets, int count)
    {
        long total = 0;
        for (int i = 0; bets != null && i < count && i < bets.Length; i++) total += bets[i].cents;
        return total;
    }

    /// <summary>
    /// Checks one spin's bets against the layout and the limits (Docs/Casino.md §4.2): each a real spot, whole dollars, $1 or
    /// more, no spot over its maximum ($100 inside, $500 outside; stacks on the same spot are added up), $1,000 in total.
    /// </summary>
    public static RouletteBetError Validate(RouletteBet[] bets, int count)
    {
        if (bets == null || count <= 0) return RouletteBetError.NoBets;
        if (count > MaxBetsPerSpin || count > bets.Length) return RouletteBetError.TooManyBets;
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var b = bets[i];
            if (!IsLegalSpot(b.type, b.index)) return RouletteBetError.BadSpot;
            if (b.cents <= 0 || b.cents % CasinoRules.ChipCents != 0) return RouletteBetError.BadAmount;
            if (b.cents < CasinoRules.RouletteMinSpotCents) return RouletteBetError.BelowMinimum;
            long spot = 0;
            for (int j = 0; j < count; j++) if (bets[j].type == b.type && bets[j].index == b.index) spot += Math.Max(0, bets[j].cents);
            if (spot > CasinoRules.RouletteMaxSpotCents(IsInside(b.type))) return RouletteBetError.AboveSpotMaximum;
            total += b.cents;
        }
        return total > CasinoRules.RouletteMaxSpinCents ? RouletteBetError.AboveTableMaximum : RouletteBetError.Ok;
    }

    /// <summary>Settles validated bets against a pocket. The host has already charged <see cref="RouletteResult.stakedCents"/>.</summary>
    public static RouletteResult Resolve(RouletteBet[] bets, int count, int pocket)
    {
        var r = new RouletteResult { pocket = (byte)pocket };
        for (int i = 0; bets != null && i < count && i < bets.Length; i++)
        {
            r.stakedCents += bets[i].cents;
            r.returnCents += Return(bets[i], pocket);
        }
        return r;
    }

    /// <summary>
    /// The exact expected return of a spot per unit staked, over the 38 pockets: 36/38 everywhere (a 5.26% edge) except the
    /// top line's 35/38 (7.89%).
    /// </summary>
    public static double ExpectedReturn(RouletteBetType type, int index)
    {
        var bet = new RouletteBet(type, index, CasinoRules.ChipCents);
        long back = 0;
        for (int p = 0; p < PocketCount; p++) back += Return(bet, p);
        return back / (double)(PocketCount * CasinoRules.ChipCents);
    }
}

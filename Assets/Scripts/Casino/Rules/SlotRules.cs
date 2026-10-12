using System;

/// <summary>
/// One line of a pay table: the line wins when at least <see cref="count"/> of the three reels show a symbol in
/// <see cref="symbols"/> (a bit mask, bit n = symbol n). Three of a kind is one symbol, count 3; "any three bars" is a group,
/// count 3; a cherry-style pay is one symbol, count 2 or 1. It pays <see cref="multiple"/> × the stake (stake not returned).
/// </summary>
[Serializable]
public struct SlotPay
{
    public string name;
    public ushort symbols;
    public byte count;
    public int multiple;
}

/// <summary>A slot machine: its symbols (0 is always the blank), three reel strips, and a pay table in paying order.</summary>
public sealed class SlotMachine
{
    public byte id;
    public string name;
    public int stakeCents;
    /// <summary>The return the machine was designed for (0.95 = 95%). The real one is <see cref="SlotRules.Return"/>.</summary>
    public double targetReturn;
    public string[] symbols;
    /// <summary>Three strips of symbol ids. A reel stops on any of its stops with equal chance; what shows is what stopped.</summary>
    public byte[][] reels;
    /// <summary>Highest pay first. The first line that matches is the only one paid; pays[0] is the top prize.</summary>
    public SlotPay[] pays;
}

/// <summary>One spin: where each reel stopped and what it paid. Eight bytes on the wire; a client redraws the reels from the stops.</summary>
[Serializable]
public struct SlotSpin
{
    public byte machine, stop0, stop1, stop2;
    /// <summary>Index into the machine's pays, or -1 for no win.</summary>
    public sbyte rule;
    /// <summary>Winnings in cents, on top of nothing: the stake is spent either way.</summary>
    public int winCents;

    public int Stop(int reel) => reel == 0 ? stop0 : reel == 1 ? stop1 : stop2;
    public bool Won => rule >= 0;
}

/// <summary>A machine's exact numbers, from every stop combination (<see cref="SlotRules.Stats"/>).</summary>
public sealed class SlotStats
{
    public long combinations, hits, nearMisses;
    /// <summary>How many combinations each pay line wins, in pay-table order.</summary>
    public long[] ruleHits;
    /// <summary>Expected winnings per unit staked (0.95 = 95%).</summary>
    public double returnToPlayer;
    /// <summary>The share of spins that pay anything.</summary>
    public double hitFrequency;
    /// <summary>Standard deviation of one spin's winnings, in stakes: how wild the ride is.</summary>
    public double volatility;
    /// <summary>Share of spins with two top-prize symbols on the line and no top prize.</summary>
    public double nearMissFrequency;
    /// <summary>The top prize comes up once in this many spins.</summary>
    public double topPrizeOneIn;
}

/// <summary>
/// The five slot machines of The Lucky Raccoon (Docs/Casino.md §4.3): three reels, one pay line, fixed strips and pay
/// tables. Pure and deterministic given the dice. Each reel stops uniformly on one of its stops and the pay comes from the
/// three symbols on the line, so the return is exact and <see cref="Stats"/> works it out by trying every combination.
/// There are no display reels: near misses are whatever the strips really stopped on.
/// </summary>
public static class SlotRules
{
    public const byte LuckySevens = 0, TrashPandaBandit = 1, GoldenGnome = 2, DumpsterFire = 3, MysteryBag = 4;
    public const int ReelCount = 3;
    /// <summary>Symbol 0 on every machine.</summary>
    public const byte Blank = 0;

    static SlotPay Pay(string name, int multiple, int count, params int[] symbols)
    {
        ushort mask = 0;
        foreach (int s in symbols) mask |= (ushort)(1 << s);
        return new SlotPay { name = name, symbols = mask, count = (byte)count, multiple = multiple };
    }

    // Strips and pay tables were tuned offline against the targets and are frozen here; CasinoSlotTests re-derives every
    // number below from them. Changing a strip or a pay changes the return: rerun the tests.
    static readonly SlotMachine[] machines =
    {
        // 0 Lucky Sevens. Classic bars and sevens: frequent small hits, a modest top prize.
        // Exact: return 94.995%, hit frequency 26.72%, top prize 1 in 32,768.
        new SlotMachine
        {
            id = LuckySevens, name = "Lucky Sevens", stakeCents = 100, targetReturn = 0.95,
            symbols = new[] { "Blank", "Cherry", "Bar", "Double Bar", "Triple Bar", "Red Seven", "Lucky Seven" },
            reels = new[]
            {
                new byte[] { 3, 0, 5, 3, 4, 2, 0, 0, 2, 1, 5, 2, 4, 0, 6, 0, 2, 0, 0, 2, 0, 0, 2, 4, 3, 0, 5, 2, 0, 4, 1, 2 },
                new byte[] { 2, 5, 4, 0, 6, 0, 5, 4, 3, 0, 3, 0, 3, 2, 0, 2, 0, 2, 0, 2, 0, 1, 2, 0, 2, 4, 2, 3, 2, 5, 2, 0 },
                new byte[] { 3, 2, 4, 3, 1, 0, 2, 5, 4, 3, 2, 5, 0, 2, 3, 0, 2, 0, 6, 0, 0, 5, 0, 2, 0, 0, 2, 1, 4, 0, 2, 0 },
            },
            pays = new[]
            {
                Pay("Three Lucky Sevens", 500, 3, 6), // 1 in 32,768
                Pay("Three Red Sevens", 100, 3, 5), // 1 in 1,214
                Pay("Any Three Sevens", 40, 3, 5, 6), // 1 in 910
                Pay("Three Triple Bars", 30, 3, 4), // 1 in 910
                Pay("Three Double Bars", 20, 3, 3), // 1 in 683
                Pay("Three Cherries", 12, 3, 1), // 1 in 8,192
                Pay("Three Bars", 10, 3, 2), // 1 in 51
                Pay("Any Three Bars", 4, 3, 2, 3, 4), // 1 in 11
                Pay("Two Cherries", 4, 2, 1), // 1 in 134
                Pay("One Cherry", 1, 1, 1), // 1 in 7
            },
        },
        // 1 Trash Panda Bandit. Raccoons, masks and loot bags: the middle of the road.
        // Exact: return 92.000%, hit frequency 19.23%, top prize 1 in 70,400.
        new SlotMachine
        {
            id = TrashPandaBandit, name = "Trash Panda Bandit", stakeCents = 100, targetReturn = 0.92,
            symbols = new[] { "Blank", "Fish Bone", "Tin Can", "Trash Bag", "Loot Bag", "Bandit Mask", "Raccoon", "Raccoon King's Crown" },
            reels = new[]
            {
                new byte[] { 2, 5, 4, 1, 0, 2, 3, 2, 6, 2, 5, 2, 4, 5, 3, 0, 0, 7, 0, 2, 0, 2, 6, 3, 4, 1, 3, 2, 0, 2, 1, 2, 5, 2, 1, 6, 2, 6, 4, 1 },
                new byte[] { 1, 2, 4, 5, 3, 0, 7, 0, 6, 0, 1, 2, 0, 2, 3, 0, 3, 5, 2, 4, 1, 3, 4, 6, 3, 2, 4, 3, 5, 2, 3, 2, 1, 2, 0, 2, 4, 2, 1, 5 },
                new byte[] { 5, 2, 3, 2, 1, 4, 2, 1, 4, 5, 3, 1, 3, 2, 5, 6, 3, 6, 2, 3, 4, 2, 6, 3, 2, 0, 2, 0, 7, 0, 3, 0, 3, 2, 4, 0, 4, 5, 0, 5, 1, 0, 1, 3 },
            },
            pays = new[]
            {
                Pay("Three Crowns", 1000, 3, 7), // 1 in 70,400
                Pay("Three Raccoons", 150, 3, 6), // 1 in 2,933
                Pay("Three Masks", 50, 3, 5), // 1 in 880
                Pay("Three Loot Bags", 25, 3, 4), // 1 in 704
                Pay("The Whole Gang", 20, 3, 5, 6, 7), // 1 in 152
                Pay("Three Trash Bags", 12, 3, 3), // 1 in 279
                Pay("Three Fish Bones", 10, 3, 1), // 1 in 563
                Pay("Three Tin Cans", 8, 3, 2), // 1 in 65
                Pay("Mixed Haul", 3, 3, 2, 3, 4), // 1 in 8
                Pay("Two Fish Bones", 2, 2, 1), // 1 in 26
            },
        },
        // 2 Golden Gnome. Gnomes, toadstools and gold: long dry spells and one very large, very rare jackpot.
        // Exact: return 90.000%, hit frequency 12.21%, top prize 1 in 131,072.
        new SlotMachine
        {
            id = GoldenGnome, name = "Golden Gnome", stakeCents = 500, targetReturn = 0.90,
            symbols = new[] { "Blank", "Toadstool", "Lantern", "Fishing Rod", "Garden Gnome", "Pot of Gold", "Golden Gnome" },
            reels = new[]
            {
                new byte[] { 2, 4, 2, 0, 2, 1, 2, 0, 2, 1, 2, 0, 2, 1, 4, 0, 6, 0, 4, 1, 0, 1, 2, 0, 2, 3, 0, 3, 0, 3, 2, 0, 6, 0, 1, 3, 0, 3, 2, 1, 2, 0, 2, 3, 2, 3, 0, 3, 0, 3, 1, 0, 1, 2, 1, 2, 5, 3, 2, 5, 3, 1, 2, 0 },
                new byte[] { 2, 3, 0, 3, 0, 2, 0, 2, 5, 2, 0, 2, 1, 2, 4, 1, 3, 2, 4, 2, 1, 3, 0, 1, 5, 1, 0, 1, 2, 0, 2, 3, 2, 0, 2, 3, 1, 0, 1, 2, 0, 2, 0, 2, 3, 4, 2, 1, 0, 1, 0, 6, 0, 1, 0, 1, 0, 2, 3, 1, 3, 4, 0, 1 },
                new byte[] { 1, 2, 3, 0, 1, 0, 1, 2, 0, 2, 3, 2, 0, 2, 0, 1, 2, 4, 3, 0, 4, 0, 4, 0, 1, 0, 1, 0, 1, 2, 0, 6, 0, 1, 0, 1, 2, 5, 0, 2, 5, 3, 5, 3, 1, 0, 2, 0, 2, 3, 0, 2, 1, 4, 2, 1, 0, 1, 4, 2, 5, 0, 2, 0 },
            },
            pays = new[]
            {
                Pay("Three Golden Gnomes", 4000, 3, 6), // 1 in 131,072
                Pay("Three Pots of Gold", 500, 3, 5), // 1 in 16,384
                Pay("Three Garden Gnomes", 100, 3, 4), // 1 in 4,369
                Pay("Any Three Gnomes", 50, 3, 4, 6), // 1 in 2,979
                Pay("Three Fishing Rods", 40, 3, 3), // 1 in 441
                Pay("Three Lanterns", 20, 3, 2), // 1 in 57
                Pay("Three Toadstools", 10, 3, 1), // 1 in 131
                Pay("Two Toadstools", 3, 2, 1), // 1 in 11
            },
        },
        // 3 Dumpster Fire. Flames, bins and hazard cans: streaky. Reels one and two are thick with Dumpster Fires and reel three has exactly one, so two-out-of-three happens a lot. That is the strip, not a trick.
        // Exact: return 87.989%, hit frequency 14.20%, top prize 1 in 3,009.
        new SlotMachine
        {
            id = DumpsterFire, name = "Dumpster Fire", stakeCents = 500, targetReturn = 0.88,
            symbols = new[] { "Blank", "Spark", "Wheelie Bin", "Hazard Can", "Gas Can", "Flame", "Dumpster Fire" },
            reels = new[]
            {
                new byte[] { 6, 2, 0, 1, 0, 2, 3, 6, 1, 2, 6, 0, 3, 5, 1, 0, 4, 6, 2, 0, 6, 0, 0, 6, 0, 2, 4, 0, 3, 1, 5, 2, 4, 0, 2, 1, 0, 2, 5, 1, 4, 0, 6, 1, 3, 2, 4, 3 },
                new byte[] { 3, 0, 2, 0, 1, 0, 4, 2, 3, 5, 4, 0, 6, 0, 1, 3, 6, 4, 5, 2, 1, 3, 6, 0, 1, 0, 0, 1, 2, 3, 6, 2, 0, 6, 3, 0, 4, 0, 3, 2, 6, 3, 1, 2, 4, 0, 2, 6 },
                new byte[] { 4, 2, 0, 2, 0, 2, 0, 2, 5, 0, 5, 4, 1, 3, 4, 0, 4, 1, 2, 0, 2, 0, 6, 0, 1, 3, 2, 0, 1, 0, 2, 5, 0, 3, 4, 2, 3, 2, 0, 2, 4, 1, 0, 3, 0, 3, 1, 3, 5, 0, 1, 3, 4, 0, 2, 1, 2, 1, 4, 3, 5, 2, 4, 5 },
            },
            pays = new[]
            {
                Pay("Three Dumpster Fires", 500, 3, 6), // 1 in 3,009
                Pay("Three Flames", 80, 3, 5), // 1 in 4,096
                Pay("Three Gas Cans", 30, 3, 4), // 1 in 655
                Pay("Anything Burning", 25, 3, 5, 6), // 1 in 271
                Pay("Three Hazard Cans", 20, 3, 3), // 1 in 410
                Pay("Three Sparks", 15, 3, 1), // 1 in 390
                Pay("Three Wheelie Bins", 10, 3, 2), // 1 in 146
                Pay("Any Three Cans", 4, 3, 2, 3, 4), // 1 in 13
                Pay("Two Sparks", 2, 2, 1), // 1 in 21
            },
        },
        // 4 Mystery Bag. Sacks, question marks and the odd treasure: the high roller, with pays from money-back to 5,000 times the stake.
        // Exact: return 93.000%, hit frequency 18.51%, top prize 1 in 107,520.
        new SlotMachine
        {
            id = MysteryBag, name = "Mystery Bag", stakeCents = 2500, targetReturn = 0.93,
            symbols = new[] { "Blank", "Question Mark", "Paper Sack", "Burlap Sack", "Money Sack", "Old Coin", "Diamond Ring", "Golden Trash Bag" },
            reels = new[]
            {
                new byte[] { 3, 2, 5, 3, 4, 0, 3, 0, 7, 0, 0, 4, 3, 0, 5, 0, 3, 4, 0, 0, 7, 0, 0, 2, 0, 3, 0, 2, 6, 3, 1, 2, 0, 5, 1, 2, 4, 0, 3, 2, 6, 0, 3, 0, 6, 0, 3, 0, 2, 5, 1, 2, 0, 3, 0, 4 },
                new byte[] { 6, 2, 3, 0, 7, 0, 0, 2, 0, 5, 3, 0, 2, 0, 4, 0, 1, 6, 2, 3, 0, 2, 3, 0, 0, 3, 2, 0, 4, 0, 3, 2, 5, 4, 3, 0, 4, 0, 3, 0, 4, 0, 3, 0, 5, 2, 0, 3, 4, 0, 2, 4, 0, 3, 0, 2, 0, 0, 2, 0 },
                new byte[] { 2, 0, 0, 2, 0, 3, 0, 4, 2, 0, 6, 3, 0, 2, 4, 3, 0, 6, 0, 1, 6, 2, 0, 7, 0, 5, 0, 2, 0, 0, 2, 0, 0, 2, 5, 6, 0, 3, 2, 5, 3, 2, 0, 5, 0, 2, 5, 0, 3, 0, 1, 0, 3, 0, 4, 2, 0, 0, 2, 0, 4, 2, 0, 4 },
            },
            pays = new[]
            {
                Pay("Three Golden Trash Bags", 5000, 3, 7), // 1 in 107,520
                Pay("Three Diamond Rings", 500, 3, 6), // 1 in 8,960
                Pay("Three Old Coins", 200, 3, 5), // 1 in 3,584
                Pay("Any Three Treasures", 60, 3, 5, 6, 7), // 1 in 474
                Pay("Three Money Sacks", 40, 3, 4), // 1 in 1,229
                Pay("Three Question Marks", 30, 3, 1), // 1 in 35,840
                Pay("Three Burlap Sacks", 20, 3, 3), // 1 in 254
                Pay("Three Paper Sacks", 10, 3, 2), // 1 in 175
                Pay("Any Three Sacks", 5, 3, 2, 3, 4), // 1 in 14
                Pay("Two Question Marks", 4, 2, 1), // 1 in 333
                Pay("One Question Mark", 1, 1, 1), // 1 in 10
            },
        },    };

    static readonly SlotStats[] stats = new SlotStats[machines.Length];

    public static int MachineCount => machines.Length;
    public static bool IsMachine(int id) => id >= 0 && id < machines.Length;
    /// <summary>The machine with this id (0..4). An unknown id throws: check <see cref="IsMachine"/> on anything off the wire.</summary>
    public static SlotMachine Machine(int id) => machines[id];
    public static int StakeCents(int id) => machines[id].stakeCents;
    public static int ReelLength(int id, int reel) => machines[id].reels[reel].Length;

    /// <summary>
    /// The symbol a reel shows at <paramref name="row"/> rows below the pay line (0 = on the line, -1 the stop above, +1
    /// below) when it has stopped at <paramref name="stop"/>. The strip wraps.
    /// </summary>
    public static byte SymbolAt(int id, int reel, int stop, int row = 0)
    {
        var strip = machines[id].reels[reel];
        int i = (stop + row) % strip.Length;
        return strip[i < 0 ? i + strip.Length : i];
    }

    public static string SymbolName(int id, int symbol) => machines[id].symbols[symbol];

    /// <summary>The pay line that three symbols win (an index into the machine's pays), or -1. The first match in table order.</summary>
    public static int WinningRule(int id, byte a, byte b, byte c)
    {
        var pays = machines[id].pays;
        for (int i = 0; i < pays.Length; i++) if (Matches(pays[i], a, b, c)) return i;
        return -1;
    }

    /// <summary>True when the line a, b, c satisfies one pay line, whatever else it also satisfies.</summary>
    public static bool Matches(SlotPay pay, byte a, byte b, byte c) =>
        (pay.symbols >> a & 1) + (pay.symbols >> b & 1) + (pay.symbols >> c & 1) >= pay.count;

    /// <summary>What three stops pay. Clients can call this to check or redraw a result they were sent.</summary>
    public static SlotSpin Resolve(int id, int stop0, int stop1, int stop2)
    {
        var m = machines[id];
        int rule = WinningRule(id, m.reels[0][stop0], m.reels[1][stop1], m.reels[2][stop2]);
        return new SlotSpin
        {
            machine = (byte)id, stop0 = (byte)stop0, stop1 = (byte)stop1, stop2 = (byte)stop2, rule = (sbyte)rule,
            winCents = rule < 0 ? 0 : m.stakeCents * m.pays[rule].multiple,
        };
    }

    /// <summary>
    /// One spin: each reel stops on a uniformly drawn stop (three dice calls, reel 0 first) and the line is paid.
    /// <paramref name="nextInt"/>(n) must return a uniform 0..n-1. The host charges the stake first and credits winCents.
    /// </summary>
    public static SlotSpin Spin(int id, Func<int, int> nextInt)
    {
        var reels = machines[id].reels;
        int a = nextInt(reels[0].Length), b = nextInt(reels[1].Length), c = nextInt(reels[2].Length);
        if (a < 0 || a >= reels[0].Length || b < 0 || b >= reels[1].Length || c < 0 || c >= reels[2].Length)
            throw new ArgumentOutOfRangeException(nameof(nextInt), "dice returned a stop off the strip");
        return Resolve(id, a, b, c);
    }

    /// <summary>The top-prize symbol: the one pays[0] wants three of.</summary>
    public static byte TopSymbol(int id)
    {
        ushort mask = machines[id].pays[0].symbols;
        byte s = 0;
        while (s < 15 && (mask >> s & 1) == 0) s++;
        return s;
    }

    /// <summary>Two top-prize symbols on the line and the third reel somewhere else. Honest by construction: it reads the real stops.</summary>
    public static bool IsNearMiss(int id, int stop0, int stop1, int stop2)
    {
        var m = machines[id];
        byte top = TopSymbol(id);
        int n = (m.reels[0][stop0] == top ? 1 : 0) + (m.reels[1][stop1] == top ? 1 : 0) + (m.reels[2][stop2] == top ? 1 : 0);
        return n == 2;
    }

    /// <summary>The machine's exact numbers, from all of its stop combinations (at most 262,144). Worked out once and kept.</summary>
    public static SlotStats Stats(int id)
    {
        if (stats[id] != null) return stats[id];
        var m = machines[id];
        byte[] r0 = m.reels[0], r1 = m.reels[1], r2 = m.reels[2];
        byte top = TopSymbol(id);
        var s = new SlotStats { ruleHits = new long[m.pays.Length], combinations = (long)r0.Length * r1.Length * r2.Length };
        long sum = 0, squares = 0;
        for (int a = 0; a < r0.Length; a++)
            for (int b = 0; b < r1.Length; b++)
                for (int c = 0; c < r2.Length; c++)
                {
                    if ((r0[a] == top ? 1 : 0) + (r1[b] == top ? 1 : 0) + (r2[c] == top ? 1 : 0) == 2) s.nearMisses++;
                    int rule = WinningRule(id, r0[a], r1[b], r2[c]);
                    if (rule < 0) continue;
                    long x = m.pays[rule].multiple;
                    s.hits++;
                    s.ruleHits[rule]++;
                    sum += x;
                    squares += x * x;
                }
        double n = s.combinations, mean = sum / n;
        s.returnToPlayer = mean;
        s.hitFrequency = s.hits / n;
        s.volatility = Math.Sqrt(squares / n - mean * mean);
        s.nearMissFrequency = s.nearMisses / n;
        s.topPrizeOneIn = s.ruleHits[0] == 0 ? double.PositiveInfinity : n / s.ruleHits[0];
        return stats[id] = s;
    }

    /// <summary>The exact return: total pay over every stop combination, per combination, per stake (0.95 = 95%).</summary>
    public static double Return(int id) => Stats(id).returnToPlayer;
    /// <summary>The exact share of spins that pay anything.</summary>
    public static double HitFrequency(int id) => Stats(id).hitFrequency;

    /// <summary>Spins a machine <paramref name="spins"/> times and returns the total winnings in cents (staked: spins × stake). For tests and a debug readout.</summary>
    public static long SampleWinCents(int id, long spins, Func<int, int> nextInt)
    {
        long won = 0;
        for (long i = 0; i < spins; i++) won += Spin(id, nextInt).winCents;
        return won;
    }
}

using System;

/// <summary>What a casino station is for. Matches <c>CasinoStation.Kind</c> (Docs/Casino.md §3).</summary>
public enum CasinoGame : byte { Blackjack = 0, Roulette = 1, Slot = 2, Bar = 3 }

/// <summary>One line of the bartender's menu. <see cref="units"/> add to the drunk level; water is negative.</summary>
[Serializable]
public struct CasinoDrink
{
    public byte id;
    public string name;
    public int priceCents;
    public float units;
}

/// <summary>
/// Ids, table limits and the drink menu of The Lucky Raccoon (Docs/Casino.md §4, §5). Pure: no scene access and no dice.
/// All money is integer cents. The host validates every stake with these before it charges anything.
/// </summary>
public static class CasinoRules
{
    /// <summary>Table stakes are whole dollars, so a 3:2 blackjack and half-stake insurance are always exact in cents.</summary>
    public const int ChipCents = 100;

    // ---- blackjack: $5 to $200 a hand
    public const int BlackjackMinCents = 500, BlackjackMaxCents = 20000;

    /// <summary>A legal opening stake for one blackjack hand: whole dollars, $5 to $200.</summary>
    public static bool IsLegalBlackjackStake(long cents) =>
        cents >= BlackjackMinCents && cents <= BlackjackMaxCents && cents % ChipCents == 0;

    // ---- roulette: $1 a spot, $100 inside, $500 outside, $1,000 a spin
    public const int RouletteMinSpotCents = 100, RouletteMaxInsideCents = 10000, RouletteMaxOutsideCents = 50000;
    public const int RouletteMaxSpinCents = 100000;

    /// <summary>The most one spot may carry. Inside spots are number bets (straight to top line), outside the rest.</summary>
    public static int RouletteMaxSpotCents(bool inside) => inside ? RouletteMaxInsideCents : RouletteMaxOutsideCents;

    /// <summary>A legal amount on one roulette spot: whole dollars, $1 up to the spot's maximum.</summary>
    public static bool IsLegalRouletteSpot(bool inside, long cents) =>
        cents >= RouletteMinSpotCents && cents <= RouletteMaxSpotCents(inside) && cents % ChipCents == 0;

    /// <summary>A legal total for one spin (the sum of every spot).</summary>
    public static bool IsLegalRouletteTotal(long cents) => cents >= RouletteMinSpotCents && cents <= RouletteMaxSpinCents;

    // ---- slots
    public const int SlotMachineCount = 5;
    public static bool IsSlotMachine(int id) => id >= 0 && id < SlotMachineCount;

    /// <summary>True when a player holding <paramref name="cashCents"/> can cover a charge. Never true for a negative charge.</summary>
    public static bool CanAfford(long cashCents, long chargeCents) => chargeCents >= 0 && cashCents >= chargeCents;

    // ---- the bar (Docs/Casino.md §5)
    public const byte FlatBeer = 0, HouseWhisky = 1, DumpsterFireCocktail = 2, GlassOfWater = 3;

    static readonly CasinoDrink[] drinks =
    {
        new CasinoDrink { id = FlatBeer, name = "Flat Beer", priceCents = 600, units = 0.12f },
        new CasinoDrink { id = HouseWhisky, name = "House Whisky", priceCents = 1000, units = 0.20f },
        new CasinoDrink { id = DumpsterFireCocktail, name = "Dumpster Fire", priceCents = 1400, units = 0.30f },
        new CasinoDrink { id = GlassOfWater, name = "Glass of Water", priceCents = 100, units = -0.10f },
    };

    public static int DrinkCount => drinks.Length;
    public static bool IsDrink(int id) => id >= 0 && id < drinks.Length;

    /// <summary>The menu line for an id. Check <see cref="IsDrink"/> first: an unknown id throws.</summary>
    public static CasinoDrink Drink(int id) => drinks[id];

    public static bool TryGetDrink(int id, out CasinoDrink drink)
    {
        bool ok = IsDrink(id);
        drink = ok ? drinks[id] : default;
        return ok;
    }

    /// <summary>"$12" or "$2.50": cents shown the way a chip rack reads.</summary>
    public static string Dollars(long cents)
    {
        string sign = cents < 0 ? "-" : "";
        long a = Math.Abs(cents);
        string whole = sign + "$" + (a / 100).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return a % 100 == 0 ? whole : whole + "." + (a % 100).ToString("00");
    }
}

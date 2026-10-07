using System;
using System.Globalization;

/// <summary>Whether a seller's local ebae listing has reached the Town board.</summary>
public enum EbaePublicationState { Publishing, Live, Rejected }

/// <summary>Host-confirmed publication state and a reason when it was rejected.</summary>
public readonly struct EbaePublicationStatus
{
    public readonly EbaePublicationState State;
    public readonly string Reason;

    public EbaePublicationStatus(EbaePublicationState state, string reason = null)
    {
        State = state;
        Reason = reason ?? string.Empty;
    }
}

/// <summary>Shared ebae limits used by the laptop and the Town authority.</summary>
public static class EbaeTradingRules
{
    public const long MinPriceCents = 50;
    public const long MaxPriceCents = 5_000_000;
    public const int MaxActiveListings = 12;
    public const int MaxListingsPerBusiness = MaxActiveListings;

    public static bool IsValidPrice(long cents) => cents >= MinPriceCents && cents <= MaxPriceCents;

    public static string PriceError(long cents) => cents < MinPriceCents ? "minimum price is $0.50"
        : cents > MaxPriceCents ? "maximum price is $50,000.00" : null;

    public static bool CanCreateListing(int activeListings) => activeListings < MaxActiveListings;

    public static string CapacityError(int activeListings) => CanCreateListing(activeListings) ? null
        : "12 active listings max. Delist one before listing another.";

    /// <summary>Parse dollars without rounding away a third decimal place or overflowing cents.</summary>
    public static bool TryParsePrice(string text, out long cents, out string error)
        => TryParseDollars(text, MinPriceCents, out cents, out error);

    /// <summary>Neighbour cash offers may be $0 when paired with an item, but still obey the same cap.</summary>
    public static bool TryParseOfferCash(string text, out long cents, out string error)
        => TryParseDollars(text, 0, out cents, out error);

    static bool TryParseDollars(string text, long minimumCents, out long cents, out string error)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text)) { error = "enter a price first"; return false; }
        string clean = text.Replace("$", "").Replace(",", "").Trim();
        if (!decimal.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                              CultureInfo.InvariantCulture, out decimal dollars))
        {
            error = "enter a valid dollar price";
            return false;
        }
        if (dollars < minimumCents / 100m)
        {
            error = minimumCents == 0 ? "cash cannot be negative" : PriceError(0);
            return false;
        }
        if (dollars > MaxPriceCents / 100m) { error = PriceError(MaxPriceCents + 1); return false; }
        decimal exactCents = dollars * 100m;
        if (decimal.Truncate(exactCents) != exactCents)
        {
            error = "use at most two decimal places";
            return false;
        }
        cents = (long)exactCents;
        error = null;
        return true;
    }

    /// <summary>Validate prices supplied by preset buttons, which already come rounded by ebae.</summary>
    public static bool TryPriceCents(float dollars, out long cents, out string error)
    {
        cents = 0;
        if (float.IsNaN(dollars) || float.IsInfinity(dollars))
        {
            error = "enter a valid dollar price";
            return false;
        }
        if (dollars < MinPriceCents / 100f) { error = PriceError(0); return false; }
        if (dollars > MaxPriceCents / 100f) { error = PriceError(MaxPriceCents + 1); return false; }
        cents = (long)Math.Round(dollars * 100.0, MidpointRounding.AwayFromZero);
        error = PriceError(cents);
        return error == null;
    }
}

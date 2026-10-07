/// <summary>The map's strings (Docs/UIPass.md §7.4): one block of the single localisable <see cref="UiText"/> class.</summary>
public static partial class UiText
{
    public const string MapTitle = "RUBBISH CO. ROUTE MAP";
    public const string MapSubtitle = "property of Chuck · do not lick";
    public const string MapYouAreHereGarage = "You are here (garage)";
    public const string MapLegendTitle = "TODAY ON THE STREETS";
    public const string MapCity = "City";
    public const string MapBagsOut = "Bags out";
    public const string MapLate = "Late";
    public const string MapDistricts = "DISTRICTS";
    public const string MapFreshness = "FRESHNESS";
    public const string MapFresh = "Fresh";
    public const string MapLateSoon = "Late soon";
    public const string MapLateStage = "Late";
    public const string MapStinky = "Stinky";
    public const string MapLocked = "LOCKED";
    public const string MapTrust = "Trust";
    public const string MapControlsZoom = "zoom";
    public const string MapControlsDrag = "drag";
    public const string MapControlsRecentre = "you";
    public const string MapControlsClose = "close";
    public const string MapControlsPeek = "Hold Tab to peek";
    public const string MapGoblins = "here be goblins";
    public const string MapNoMap = "The map's still at the printers. (Run Rubbish/Setup/Town Map.)";
    public const string MapStale = "Town map is stale: run Rubbish/Setup/Town Map";
    public const string MapCurbInfoLater = "Curb info arrives when the host's world syncs.";

    /// <summary>"12 Maple St · 3 bags · 2 h left · Mildly Pleasant Acres"</summary>
    public static string MapHouseCard(string address, int bags, string freshness, string district) =>
        bags > 0 ? $"{address} · {bags} bag{(bags == 1 ? "" : "s")} · {freshness} · {district}" : $"{address} · curb's clean · {district}";

    /// <summary>"Snootington Hills · needs Rank 7 + a $3,500 permit · you're Rank 4"</summary>
    public static string MapLockedCard(string district, int rank, string price, int yourRank) =>
        $"{district} · needs Rank {rank} + a {price} permit · you're Rank {yourRank}";
}

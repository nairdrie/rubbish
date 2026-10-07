// M0 contract (Docs/Multiplayer.md §8.7.3, C1). Pure: lives in Rubbish.Net.Core (UnityEngine, Netcode, Collections only).

/// <summary>Caps and sizes shared by every networking package (D9).</summary>
public static class NetConstants
{
    /// <summary>
    /// Players per Town (Docs/Multiplayer.md §13.10 #1): every player owns a business, one per Depot Row lot and colour, so a
    /// 5th player reads "Town is full (4/4)". Relay allocates <c>TownCap − 1</c> connections.
    /// </summary>
    public const int TownCap = 4;
    /// <summary>Seats per <see cref="SlotTable"/> company. Since §13 every player sits in company 0 and the slot is their
    /// business colour (0..3), so this equals <see cref="TownCap"/>.</summary>
    public const int CompanyCap = 4;
    /// <summary>Old name for <see cref="TownCap"/> (the "Crew Town" is gone since §13; kept so callers compile).</summary>
    public const int CrewTownCap = TownCap;
    /// <summary>Businesses per Town (Depot Row lots): one per player.</summary>
    public const int MaxCompanies = 4;
    public const int TickRate = 30;
    public const ushort DefaultPort = 7777;
    /// <summary>The connection-approval payload cap (<see cref="JoinRequest"/>).</summary>
    public const int JoinRequestMaxBytes = 1200;
    /// <summary>Largest payload <see cref="BoundedGzip.TryInflate"/> may produce from another player's bytes.</summary>
    public const int InflateMaxBytes = 1024 * 1024;
}

/// <summary>Wire versions. A mismatch at the door is rejected with "Update your game".</summary>
public static class NetVersion
{
    /// <summary>Bump when messages, RPCs or replicated state change shape.</summary>
    public const ushort Protocol = 6;
    /// <summary>Bump with balance patches (prices, rolls) so mismatched content never shares a Town.</summary>
    public const ushort Content = 5;
}

/// <summary>Which transport a session runs on (D6).</summary>
public enum NetMode : byte
{
    /// <summary>No network session (menus, edit mode).</summary>
    Offline,
    /// <summary>127.0.0.1: solo, bots, MPPM. No internet or sign-in.</summary>
    LocalDirect,
    /// <summary>Unity Relay join codes (development; remote-flag fallback).</summary>
    UnityRelay,
    /// <summary>Steam Datagram Relay (wave-4 spike).</summary>
    SteamSdr,
}

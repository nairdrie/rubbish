/// <summary>Why a join was refused. Sent back as the disconnect reason and shown with <see cref="JoinRejectText"/>.</summary>
public enum JoinReject : byte
{
    None,
    ProtocolMismatch,
    TownFull,
    CompanyFull,
    Banned,
    AuthFailed,
    AuthTimeout,
    NotLobbyMember,
    DevKeyNotAllowed,
    BadRequest,
    HostLeaving,
    SoloOnly,
    TownMapMismatch,
    TownNotPublished,
}

/// <summary>Player-facing text for each <see cref="JoinReject"/> (short, friendly, no codes).</summary>
public static class JoinRejectText
{
    public static string For(JoinReject r) => r switch
    {
        JoinReject.None => "",
        JoinReject.ProtocolMismatch => "Update your game: you and the host are on different versions.",
        JoinReject.TownFull => $"Town is full ({NetConstants.TownCap}/{NetConstants.TownCap}).",
        JoinReject.CompanyFull => $"Crew is full ({NetConstants.CompanyCap}/{NetConstants.CompanyCap}).",
        JoinReject.Banned => "The host isn't letting you in.",
        JoinReject.AuthFailed => "Steam couldn't confirm who you are. Restart Steam and try again.",
        JoinReject.AuthTimeout => "Steam took too long to confirm who you are. Try again.",
        JoinReject.NotLobbyMember => "Join through the host's invite or the Steam friends list.",
        JoinReject.DevKeyNotAllowed => "This build needs Steam to join.",
        JoinReject.BadRequest => "The host couldn't read your join request. Update your game.",
        JoinReject.HostLeaving => "The host is closing the shift.",
        JoinReject.SoloOnly => "That game is solo only.",
        JoinReject.TownMapMismatch => "Your town map differs from the host's. Use the same published town build to play together.",
        JoinReject.TownNotPublished => "This town has unpublished map data. Save and bake the town, then use the same published build to play together.",
        _ => "Couldn't join.",
    };
}

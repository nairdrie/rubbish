/// <summary>
/// Join codes between scenes (package B1). The live code comes from <see cref="NetSession"/>; <see cref="LastJoinCode"/> is
/// what the player last typed, so the menu can offer it again after a failed join or a dropped game.
/// </summary>
public static class GameConfig
{
    /// <summary>The current session's Relay code ("" when not hosting or joined over Relay).</summary>
    public static string JoinCode => NetSession.JoinCode ?? "";

    /// <summary>The last code typed into the menu (normalized: upper-case letters and digits).</summary>
    public static string LastJoinCode = "";
}

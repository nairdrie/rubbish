#if !DISABLESTEAMWORKS
using Steamworks;                                         // hit 1
// coop_lint fixture: `steamapi` fires outside Net/Steam/** (3 hits).
public static class SteamOutside
{
    static void Go()
    {
        Steamworks.SteamAPI.Init();                       // hit 2
        var id = Steamworks.SteamUser.GetSteamID();       // hit 3
    }
}
#endif

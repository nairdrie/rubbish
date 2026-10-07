#if !DISABLESTEAMWORKS
using Steamworks;
// coop_lint fixture: Net/Steam/** may use Steamworks.
public static class SteamInside
{
    static void Go() { Steamworks.SteamAPI.Init(); }
}
#endif

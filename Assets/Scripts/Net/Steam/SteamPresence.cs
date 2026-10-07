#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using System.Globalization;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Steam rich presence (Docs/Multiplayer.md §8.7.4 B10): the <c>connect</c> key (<c>+connect_lobby &lt;id&gt;</c>) is what makes
/// <b>Join Game</b> appear on your name in a friend's Steam list; <c>steam_player_group</c> groups the crew there.
/// <c>steam_display</c> needs localization tokens uploaded for our own App ID, so it's left off on 480 (Spacewar shows
/// "Playing Spacewar" either way); <c>status</c> is plain text some Steam views show.
/// </summary>
public static class SteamPresence
{
    /// <summary>True once Rubbish has its own App ID with rich-presence tokens uploaded (Accounts.md §14).</summary>
    public static bool HasDisplayTokens => SteamBootstrap.AppId != 0 && SteamBootstrap.AppId != SteamPolicy.DevAppId;

    /// <summary>Advertise a lobby friends can join.</summary>
    public static void SetLobby(ulong lobbyId, int members, bool hosting)
    {
        if (!SteamBootstrap.IsInitialized || lobbyId == 0) return;
        Set("connect", ConnectArgs.ForLobby(lobbyId));
        Set("status", hosting ? "Running a Rubbish shift (join in!)" : "On a Rubbish shift with friends");
        Set("steam_player_group", lobbyId.ToString(CultureInfo.InvariantCulture));
        Set("steam_player_group_size", Math.Max(1, members).ToString(CultureInfo.InvariantCulture));
        if (HasDisplayTokens) Set("steam_display", hosting ? "#Status_Hosting" : "#Status_Crew");
    }

    /// <summary>Update the crew size shown in friends' lists.</summary>
    public static void SetGroupSize(int members)
    {
        if (!SteamBootstrap.IsInitialized) return;
        Set("steam_player_group_size", Math.Max(1, members).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Not joinable any more (left or closed the lobby).</summary>
    public static void Clear()
    {
#if !DISABLESTEAMWORKS
        if (!SteamBootstrap.IsInitialized) return;
        try { SteamFriends.ClearRichPresence(); }
        catch (Exception e) { Debug.LogWarning($"[SteamPresence] ClearRichPresence threw: {e.Message}"); }
#endif
    }

    static void Set(string key, string value)
    {
#if !DISABLESTEAMWORKS
        try
        {
            if (!SteamFriends.SetRichPresence(key, value ?? ""))
                Debug.LogWarning($"[SteamPresence] Steam refused rich presence '{key}'.");
        }
        catch (Exception e) { Debug.LogWarning($"[SteamPresence] SetRichPresence('{key}') threw: {e.Message}"); }
#endif
    }
}

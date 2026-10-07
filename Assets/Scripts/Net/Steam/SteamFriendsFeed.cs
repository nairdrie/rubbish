#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

// ---------------------------------------------------------------------------------------------------------------------------
// Plain DTOs for the menus (Docs/UIPass.md §4.4, §4.8, UP-5). No Steam types: the UI reads these and never touches Steam.
// ---------------------------------------------------------------------------------------------------------------------------

/// <summary>What a friend is doing in Rubbish.</summary>
public enum FriendActivity : byte
{
    /// <summary>In Rubbish with no lobby: the menu or a solo shift (only shown once Rubbish has its own App ID).</summary>
    Solo,
    /// <summary>Hosting a shift that friends can join.</summary>
    Hosting,
    /// <summary>Crew on someone's shift.</summary>
    Crew,
    /// <summary>Steam is still telling us about their lobby.</summary>
    Checking,
}

/// <summary>Why a friend's shift can't be joined right now (<see cref="None"/> = it can).</summary>
public enum FriendJoinBlock : byte
{
    None,
    /// <summary>Not in a lobby (playing solo, in the menu).</summary>
    NoLobby,
    /// <summary>Waiting for the lobby's data from Steam.</summary>
    Checking,
    /// <summary>Every seat is taken ("Crew is full (4/4)").</summary>
    Full,
    /// <summary>They're on a different game version.</summary>
    Update,
    /// <summary>We're already in that game.</summary>
    AlreadyThere,
    /// <summary>The lobby exists but isn't open to friends yet (no join code), or has closed.</summary>
    Closed,
}

/// <summary>Why there's no live friends list (<see cref="None"/> = Steam is up and signed in, or fakes are injected).</summary>
public enum FriendsFeedOff : byte
{
    None,
    /// <summary>Steamworks isn't compiled for this platform.</summary>
    NotCompiled,
    /// <summary>The Editor's default dev identity (Steam never starts in the Editor unless RUBBISH_EDITOR_STEAM).</summary>
    Editor,
    /// <summary>Started with <c>-nosteam</c>, an MPPM clone or batchmode.</summary>
    Skipped,
    /// <summary>A development build without <c>-steam</c>.</summary>
    DevBuild,
    /// <summary>Steam is required here but isn't running.</summary>
    NotRunning,
    /// <summary>Steam is running but signed out / offline.</summary>
    LoggedOff,
}

/// <summary>A Steam friend who's in Rubbish right now (one row of FRIENDS PLAYING).</summary>
public sealed class FriendPlaying
{
    public ulong steamId;
    /// <summary>Their Steam name, cleaned (display only, never saved).</summary>
    public string name;
    public FriendActivity activity;
    /// <summary>The Steam lobby of their shift (0 = none).</summary>
    public ulong lobbyId;
    /// <summary>The Town's display name, or null when the lobby doesn't say.</summary>
    public string town;
    /// <summary>Who runs that shift (their own name when hosting), or null.</summary>
    public string hostName;
    /// <summary>People on the shift and seats in it (0 = unknown).</summary>
    public int crew, crewCap;
    public FriendJoinBlock block = FriendJoinBlock.NoLobby;
    /// <summary>Their Steam avatar (64×64), or null while it loads / without Steam.</summary>
    public Texture2D avatar;

    public bool Joinable => block == FriendJoinBlock.None && lobbyId != 0;

    public FriendPlaying Clone() => (FriendPlaying)MemberwiseClone();

    public override string ToString() =>
        $"{name} ({steamId}) {activity} lobby={lobbyId} town={town ?? "-"} crew={crew}/{crewCap} block={block}";
}

/// <summary>A Steam lobby invite received this session (one row of INVITES).</summary>
public sealed class FriendInvite
{
    public ulong fromSteamId;
    public string fromName;
    public ulong lobbyId;
    public string town;
    public string hostName;
    /// <summary><c>Time.realtimeSinceStartup</c> when it arrived.</summary>
    public float receivedAt;
    /// <summary>Lobby data confirmed it's a Rubbish game (false while Steam is still answering).</summary>
    public bool verified;
    public Texture2D avatar;

    public FriendInvite Clone() => (FriendInvite)MemberwiseClone();

    public override string ToString() => $"{fromName} ({fromSteamId}) → lobby {lobbyId} town={town ?? "-"} verified={verified}";
}

/// <summary>
/// The Steam friends feed for the menus (Docs/UIPass.md UP-5, §4.4 "Join a friend", §4.8 invites): a façade over Steam's friends,
/// rich presence, lobby data and <c>LobbyInvite_t</c> that hands the UI plain DTOs (<see cref="FriendPlaying"/>,
/// <see cref="FriendInvite"/>), so nothing outside <c>Net/Steam</c> touches Steamworks (lint rule <c>steamapi</c>).
/// <list type="bullet">
/// <item><b>Friends playing:</b> immediate friends in our App ID, with their lobby (from the game-played info or the
/// <c>connect</c> rich presence) checked against its <c>rb_*</c> data (<see cref="LobbyInfo"/>: App 480 is shared, so a lobby
/// only counts when <c>rb_game=rubbish</c>), the Town, the crew count (<c>steam_player_group_size</c>) and whether we can
/// join (seats, protocol, a join code).</item>
/// <item><b>Invites:</b> every <c>LobbyInvite_t</c> for our game this session, newest first, verified the same way;
/// <see cref="InviteReceived"/> fires once Steam confirms it (or after a few seconds).</item>
/// <item><b>Join:</b> <see cref="Join"/> hands the lobby id to <see cref="SteamJoinRouter.RequestJoin"/> (which leaves the
/// current game, enters the lobby and joins over Relay).</item>
/// </list>
/// Safe without Steam: <see cref="Off"/> says why there's no list, and the UI shows a friendly empty state.
/// <see cref="InjectFake"/> replaces the live data for tests and previews (no Steam needed).
/// </summary>
public static class SteamFriendsFeed
{
    // ------------------------------------------------------------------ public state

    /// <summary>Why there's no live list right now (<see cref="FriendsFeedOff.None"/> when Steam is up or fakes are in).</summary>
    public static FriendsFeedOff Off
    {
        get
        {
            if (IsFake) return FriendsFeedOff.None;
            if (!SteamBootstrap.Compiled) return FriendsFeedOff.NotCompiled;
            if (SteamBootstrap.IsInitialized) return loggedOn ? FriendsFeedOff.None : FriendsFeedOff.LoggedOff;
            var d = PlayerIdentityBoot.Decision;
            if (d.Mode == IdentityMode.Steam) return FriendsFeedOff.NotRunning;
            if (d.SteamSkipped) return FriendsFeedOff.Skipped;
            return Application.isEditor ? FriendsFeedOff.Editor : FriendsFeedOff.DevBuild;
        }
    }

    /// <summary>The list is live (Steam up and signed in) or faked.</summary>
    public static bool Available => Off == FriendsFeedOff.None;

    /// <summary>Test/preview data is in place of Steam (<see cref="InjectFake"/>).</summary>
    public static bool IsFake { get; private set; }

    /// <summary>Friends in Rubbish: joinable first, then the rest; by name.</summary>
    public static IReadOnlyList<FriendPlaying> Friends => friends;

    /// <summary>Invites received this session, newest first (only verified or timed-out ones; pending ones wait).</summary>
    public static IReadOnlyList<FriendInvite> Invites => invites;

    public static int JoinableCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < friends.Count; i++) if (friends[i].Joinable) n++;
            return n;
        }
    }

    /// <summary>The number for the "JOIN A FRIEND (2)" badge: joinable friends + invites.</summary>
    public static int BadgeCount => JoinableCount + invites.Count;

    /// <summary>The lists changed (rows, names, avatars, crew counts).</summary>
    public static event Action Changed;

    /// <summary>A friend invited us (once per invite; a repeat invite from the same friend fires again).</summary>
    public static event Action<FriendInvite> InviteReceived;

    /// <summary>The last lobby <see cref="Join"/> handed on, and why (diagnostics and tests).</summary>
    public static ulong LastJoinLobbyId { get; private set; }
    public static string LastJoinWhy { get; private set; }

    /// <summary>
    /// Where <see cref="Join"/> sends a lobby id. The default calls <see cref="SteamJoinRouter.RequestJoin"/>; tests may wrap it
    /// (call the previous value to keep the real path).
    /// </summary>
    public static Action<ulong, string> JoinSink = RouterJoin;

    static void RouterJoin(ulong lobbyId, string why) => SteamJoinRouter.RequestJoin(lobbyId, why);

    // ------------------------------------------------------------------ tuning

    const float ScanWatchedSeconds = 3f, ScanIdleSeconds = 12f, DirtyDebounceSeconds = 0.3f;
    const float LobbyRefreshSeconds = 15f, LobbyForgetSeconds = 90f, InviteVerifySeconds = 4f, InviteRecheckSeconds = 20f;
    const int MaxInvites = 10, MaxFriendsScanned = 500;

    // ------------------------------------------------------------------ state

    static readonly List<FriendPlaying> friends = new List<FriendPlaying>();
    static readonly List<FriendInvite> invites = new List<FriendInvite>();
    static readonly List<FriendInvite> pendingInvites = new List<FriendInvite>();
    static readonly List<float> pendingSince = new List<float>();
    static readonly Dictionary<ulong, LobbyCache> lobbies = new Dictionary<ulong, LobbyCache>();
    static readonly Dictionary<ulong, Texture2D> avatars = new Dictionary<ulong, Texture2D>();
    static readonly List<Texture2D> retired = new List<Texture2D>();
    static readonly List<float> retiredAt = new List<float>();
    static int watchers;
    static float nextScan, nextInviteRecheck;
    static bool loggedOn, started, hooked;
    static string signature = "";

    sealed class LobbyCache
    {
        public LobbyInfo info;
        public string error;
        public bool answered, ok, gone;
        public int memberLimit;
        public float requestedAt = -999f, usedAt;
    }

#if !DISABLESTEAMWORKS
    static Callback<LobbyInvite_t> inviteCb;
    static Callback<LobbyDataUpdate_t> lobbyDataCb;
    static Callback<PersonaStateChange_t> personaCb;
    static Callback<AvatarImageLoaded_t> avatarCb;
    static Callback<FriendRichPresenceUpdate_t> presenceCb;
#endif

    // ------------------------------------------------------------------ API

    /// <summary>
    /// The UI is showing the feed: scan more often until the handle is disposed. Also scans right away.
    /// </summary>
    public static IDisposable Watch()
    {
        watchers++;
        Refresh();
        return new WatchHandle();
    }

    sealed class WatchHandle : IDisposable
    {
        bool done;
        public void Dispose()
        {
            if (done) return;
            done = true;
            watchers = Mathf.Max(0, watchers - 1);
        }
    }

    /// <summary>Rescan now (a no-op without Steam or with fakes).</summary>
    public static void Refresh()
    {
        if (IsFake || !SteamBootstrap.IsInitialized) return;
        Scan();
    }

    /// <summary>
    /// Join the shift in <paramref name="lobbyId"/> (a FRIENDS PLAYING row, an invite). Hands it to
    /// <see cref="SteamJoinRouter.RequestJoin"/>; false with a player-readable <paramref name="whyNot"/> when it can't.
    /// </summary>
    public static bool Join(ulong lobbyId, string why, out string whyNot)
    {
        whyNot = null;
        if (lobbyId == 0) { whyNot = "That shift isn't open to friends right now."; return false; }
        if (!IsFake && !SteamBootstrap.IsInitialized)
        {
            whyNot = "Steam isn't running, so that game can't be opened. Join codes still work.";
            return false;
        }
        if (!IsFake && SteamJoinRouter.Busy) { whyNot = "Already joining a game… one moment."; return false; }
        LastJoinLobbyId = lobbyId;
        LastJoinWhy = why;
        Debug.Log($"[FriendsFeed] Join lobby {lobbyId} ({why}){(IsFake ? " [fake friends]" : "")}.");
        if (IsFake && Application.isPlaying && !SteamBootstrap.IsInitialized)
            SteamHud.Say("Pretend friends: this would join their shift if Steam were running.", false);
        try { (JoinSink ?? RouterJoin)(lobbyId, why); }
        catch (Exception e)
        {
            Debug.LogException(e);
            whyNot = "Couldn't start joining: " + e.Message;
            return false;
        }
        return true;
    }

    /// <summary>Forget an invite (the row's ✕).</summary>
    public static void Dismiss(FriendInvite invite)
    {
        if (invite == null) return;
        bool removed = false;
        for (int i = invites.Count - 1; i >= 0; i--)
        {
            if (invites[i] != invite && !(invites[i].lobbyId == invite.lobbyId && invites[i].fromSteamId == invite.fromSteamId)) continue;
            invites.RemoveAt(i);
            removed = true;
        }
        if (removed) RaiseChanged(force: true);
    }

    // ------------------------------------------------------------------ fakes (tests, previews)

    /// <summary>
    /// Replace the live Steam data with <paramref name="fakeFriends"/> and <paramref name="fakeInvites"/> (copied) until
    /// <see cref="ClearFake"/>. Works in edit mode and without Steam. Raises <see cref="Changed"/> (not InviteReceived;
    /// use <see cref="PushFakeInvite"/> for that).
    /// </summary>
    public static void InjectFake(IEnumerable<FriendPlaying> fakeFriends, IEnumerable<FriendInvite> fakeInvites)
    {
        IsFake = true;
        friends.Clear();
        invites.Clear();
        pendingInvites.Clear();
        pendingSince.Clear();
        if (fakeFriends != null) foreach (var f in fakeFriends) if (f != null) friends.Add(f.Clone());
        if (fakeInvites != null) foreach (var i in fakeInvites) if (i != null) invites.Add(i.Clone());
        SortFriends(friends);
        invites.Sort((a, b) => b.receivedAt.CompareTo(a.receivedAt));
        Debug.Log($"[FriendsFeed] Fake friends injected: {friends.Count} friends ({JoinableCount} joinable), {invites.Count} invites.");
        RaiseChanged(force: true);
    }

    /// <summary>Add a fake invite on top and raise <see cref="InviteReceived"/> as a real one would (fakes must be on).</summary>
    public static void PushFakeInvite(FriendInvite invite)
    {
        if (invite == null) return;
        if (!IsFake) InjectFake(null, null);
        var copy = invite.Clone();
        if (copy.receivedAt <= 0f) copy.receivedAt = Time.realtimeSinceStartup;
        copy.verified = true;
        AddInvite(copy);
    }

    /// <summary>Back to live Steam data (or the offline state).</summary>
    public static void ClearFake()
    {
        if (!IsFake) return;
        IsFake = false;
        friends.Clear();
        invites.Clear();
        signature = "";
        if (SteamBootstrap.IsInitialized) Scan();
        RaiseChanged(force: true);
    }

    /// <summary>
    /// The standard fake set (UIPass §11 UP5-1): three friends, one of them joinable (Dot hosting Maple Hollow, 2/4), one on a
    /// full crew, one solo; and one invite. <paramref name="now"/> is the realtime the invite arrived.
    /// </summary>
    public static void SampleSet(float now, out List<FriendPlaying> sampleFriends, out List<FriendInvite> sampleInvites)
    {
        sampleFriends = new List<FriendPlaying>
        {
            new FriendPlaying { steamId = FakeUserId(1), name = "Dot", activity = FriendActivity.Hosting, lobbyId = FakeLobbyId(101),
                town = "Maple Hollow", hostName = "Dot", crew = 2, crewCap = NetConstants.TownCap, block = FriendJoinBlock.None },
            new FriendPlaying { steamId = FakeUserId(2), name = "Ozzie the Brave", activity = FriendActivity.Crew, lobbyId = FakeLobbyId(102),
                town = "Binchester", hostName = "BigAl", crew = 4, crewCap = NetConstants.TownCap, block = FriendJoinBlock.Full },
            new FriendPlaying { steamId = FakeUserId(3), name = "bert", activity = FriendActivity.Solo, lobbyId = 0,
                block = FriendJoinBlock.NoLobby },
        };
        sampleInvites = new List<FriendInvite>
        {
            new FriendInvite { fromSteamId = FakeUserId(4), fromName = "Gus", lobbyId = FakeLobbyId(103), town = "Lower Puddleton",
                hostName = "Gus", receivedAt = now - 125f, verified = true },
        };
    }

    /// <summary>A well-formed (but made-up) Steam lobby id: universe 1, chat account type, lobby instance flag.</summary>
    public static ulong FakeLobbyId(uint account) => (1UL << 56) | (8UL << 52) | (0x40000UL << 32) | account;

    /// <summary>A well-formed (but made-up) individual SteamID64.</summary>
    public static ulong FakeUserId(uint account) => (1UL << 56) | (1UL << 52) | (1UL << 32) | (0x7000000u + account);

    // ------------------------------------------------------------------ lifetime

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (hooked) return;
        hooked = true;
        if (SteamBootstrap.IsInitialized) Start();
        else SteamBootstrap.Initialized += Start; // "Try again" on the needs-Steam screen, or a late init

        if (Application.isEditor || Debug.isDebugBuild)
        {
            // Dev aid: "-fakefriends" shows the sample set without Steam (screenshots, a quick look on a Mac).
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (!string.Equals(a, "-fakefriends", StringComparison.OrdinalIgnoreCase)) continue;
                SampleSet(Time.realtimeSinceStartup, out var f, out var i);
                InjectFake(f, i);
                break;
            }
        }
    }

    static void Start()
    {
        if (started || !SteamBootstrap.IsInitialized) return;
        started = true;
#if !DISABLESTEAMWORKS
        inviteCb = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
        lobbyDataCb = Callback<LobbyDataUpdate_t>.Create(OnLobbyData);
        personaCb = Callback<PersonaStateChange_t>.Create(OnPersona);
        avatarCb = Callback<AvatarImageLoaded_t>.Create(OnAvatarLoaded);
        presenceCb = Callback<FriendRichPresenceUpdate_t>.Create(_ => MarkDirty());
        try { loggedOn = SteamUser.BLoggedOn(); }
        catch (Exception) { loggedOn = false; }
#endif
        SteamBootstrap.Ticked += Tick;
        SteamBootstrap.ShuttingDown += Stop;
        nextScan = 0f;
        Debug.Log("[FriendsFeed] Watching Steam friends and invites.");
    }

    static void Stop()
    {
#if !DISABLESTEAMWORKS
        inviteCb?.Dispose(); inviteCb = null;
        lobbyDataCb?.Dispose(); lobbyDataCb = null;
        personaCb?.Dispose(); personaCb = null;
        avatarCb?.Dispose(); avatarCb = null;
        presenceCb?.Dispose(); presenceCb = null;
#endif
        SteamBootstrap.Ticked -= Tick;
        SteamBootstrap.ShuttingDown -= Stop;
        started = false;
        lobbies.Clear();
        pendingInvites.Clear();
        pendingSince.Clear();
        DestroyAvatars();
        if (!IsFake)
        {
            friends.Clear();
            invites.Clear();
            RaiseChanged(force: true);
        }
    }

    static void Tick()
    {
        if (IsFake) return;
        float now = Time.realtimeSinceStartup;
        if (now >= nextScan) Scan();
        TickPendingInvites(now);
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            if (now - retiredAt[i] < 2f) continue;
            if (retired[i] != null) UnityEngine.Object.Destroy(retired[i]);
            retired.RemoveAt(i);
            retiredAt.RemoveAt(i);
        }
        if (watchers > 0 && now >= nextInviteRecheck)
        {
            nextInviteRecheck = now + InviteRecheckSeconds;
            for (int i = 0; i < invites.Count; i++) RequestLobby(invites[i].lobbyId, force: true);
        }
    }

    static void MarkDirty()
    {
        float soon = Time.realtimeSinceStartup + DirtyDebounceSeconds;
        if (nextScan > soon) nextScan = soon;
    }

    // ------------------------------------------------------------------ scan

    static readonly List<FriendPlaying> scratch = new List<FriendPlaying>();

    static void Scan()
    {
        float now = Time.realtimeSinceStartup;
        nextScan = now + (watchers > 0 ? ScanWatchedSeconds : ScanIdleSeconds);
        if (!SteamBootstrap.IsInitialized) return;
#if !DISABLESTEAMWORKS
        scratch.Clear();
        try
        {
            loggedOn = SteamUser.BLoggedOn();
            uint app = SteamBootstrap.AppId;
            bool sharedApp = app == SteamPolicy.DevAppId;
            int n = Mathf.Min(SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate), MaxFriendsScanned);
            for (int i = 0; i < n; i++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game)) continue;
                if (!game.m_gameID.IsSteamApp() || game.m_gameID.AppID().m_AppId != app) continue;

                ulong lobby = game.m_steamIDLobby.IsValid() && game.m_steamIDLobby.IsLobby() ? game.m_steamIDLobby.m_SteamID : 0;
                if (lobby == 0 && ConnectArgs.TryParse(SplitArgs(SteamFriends.GetFriendRichPresence(id, "connect")), out ulong fromPresence))
                    lobby = fromPresence;

                var row = new FriendPlaying
                {
                    steamId = id.m_SteamID,
                    name = LobbyInfo.Clean(SteamFriends.GetFriendPersonaName(id)) ?? "A friend",
                    lobbyId = lobby,
                    avatar = AvatarFor(id.m_SteamID),
                };
                if (lobby == 0)
                {
                    // App 480 is shared by thousands of test games: without a Rubbish lobby we can't tell Rubbish from Spacewar.
                    if (sharedApp) continue;
                    row.activity = FriendActivity.Solo;
                    row.block = FriendJoinBlock.NoLobby;
                    scratch.Add(row);
                    continue;
                }

                int.TryParse(SteamFriends.GetFriendRichPresence(id, "steam_player_group_size"), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out row.crew);
                var lc = RequestLobby(lobby, force: false);
                lc.usedAt = now;
                if (!lc.answered)
                {
                    row.activity = FriendActivity.Checking;
                    row.block = FriendJoinBlock.Checking;
                    scratch.Add(row);
                    continue;
                }
                if (lc.gone || !lc.ok)
                {
                    if (!lc.gone && sharedApp) continue; // a lobby of some other App 480 game
                    row.activity = lc.gone ? FriendActivity.Solo : FriendActivity.Crew;
                    row.block = lc.gone ? FriendJoinBlock.NoLobby : FriendJoinBlock.Closed;
                    if (lc.gone) row.lobbyId = 0;
                    scratch.Add(row);
                    continue;
                }
                Describe(row, lc);
                scratch.Add(row);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FriendsFeed] Steam friends scan failed: {e.Message}");
            return;
        }
        ForgetOldLobbies(now);
        SortFriends(scratch);
        friends.Clear();
        friends.AddRange(scratch);
        scratch.Clear();
        PruneAcceptedInvites();
        RaiseChanged(force: false);
#endif
    }

    /// <summary>Fill the shift details and joinability from a lobby we've read.</summary>
    static void Describe(FriendPlaying row, LobbyCache lc)
    {
        var info = lc.info;
        row.town = DisplayTown(info.town);
        row.hostName = info.hostName;
        bool hosting = !string.IsNullOrEmpty(info.hostName) && string.Equals(info.hostName, row.name, StringComparison.Ordinal);
        row.activity = hosting ? FriendActivity.Hosting : FriendActivity.Crew;
        row.crewCap = lc.memberLimit > 0 ? lc.memberLimit : NetConstants.TownCap;
        if (row.crew <= 0) row.crew = 1;

        if (row.lobbyId == SteamLobby.CurrentLobbyId && SteamLobby.CurrentLobbyId != 0) row.block = FriendJoinBlock.AlreadyThere;
        else if (!info.TryGetJoinTarget(row.lobbyId, 0, out _, out JoinReject why, out _))
            row.block = why == JoinReject.ProtocolMismatch ? FriendJoinBlock.Update : FriendJoinBlock.Closed;
        else if (row.crew >= row.crewCap) row.block = FriendJoinBlock.Full;
        else row.block = FriendJoinBlock.None;
    }

    /// <summary>
    /// The Town name a lobby advertises, if it's a name. Until M1 hosts write the Town's guid there (B10 passes
    /// <c>HostOptions.townGuid</c>), which is no use to a player: those come back null and the UI says "Dot's shift".
    /// </summary>
    public static string DisplayTown(string town)
    {
        town = LobbyInfo.Clean(town);
        if (town == null) return null;
        int hex = 0;
        foreach (char c in town)
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '-') hex++;
        return town.Length >= 16 && hex == town.Length ? null : town;
    }

    static void SortFriends(List<FriendPlaying> list)
    {
        list.Sort((a, b) =>
        {
            int ra = Rank(a), rb = Rank(b);
            if (ra != rb) return ra.CompareTo(rb);
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
    }

    static int Rank(FriendPlaying f) => f.block switch
    {
        FriendJoinBlock.None => 0,
        FriendJoinBlock.AlreadyThere => 1,
        FriendJoinBlock.Full => 2,
        FriendJoinBlock.Update => 3,
        FriendJoinBlock.Checking => 4,
        FriendJoinBlock.Closed => 5,
        _ => 6,
    };

    static string[] SplitArgs(string line) =>
        string.IsNullOrEmpty(line) || line.Length > ConnectArgs.MaxLineChars
            ? Array.Empty<string>()
            : line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ lobby data

    static LobbyCache RequestLobby(ulong lobby, bool force)
    {
        if (!lobbies.TryGetValue(lobby, out var lc)) lobbies[lobby] = lc = new LobbyCache();
        float now = Time.realtimeSinceStartup;
        if (lobby == 0 || (!force && now - lc.requestedAt < LobbyRefreshSeconds)) return lc;
        lc.requestedAt = now;
#if !DISABLESTEAMWORKS
        if (!SteamBootstrap.IsInitialized) return lc;
        try
        {
            if (!SteamMatchmaking.RequestLobbyData(new CSteamID(lobby)))
            {
                lc.answered = true;
                lc.ok = false;
                lc.gone = true;
            }
        }
        catch (Exception e) { Debug.LogWarning($"[FriendsFeed] RequestLobbyData threw: {e.Message}"); }
#endif
        return lc;
    }

    static void ForgetOldLobbies(float now)
    {
        List<ulong> drop = null;
        foreach (var kv in lobbies)
        {
            if (now - kv.Value.usedAt < LobbyForgetSeconds || IsInviteLobby(kv.Key)) continue;
            (drop ??= new List<ulong>()).Add(kv.Key);
        }
        if (drop != null) foreach (var k in drop) lobbies.Remove(k);
    }

    static bool IsInviteLobby(ulong lobby)
    {
        for (int i = 0; i < invites.Count; i++) if (invites[i].lobbyId == lobby) return true;
        for (int i = 0; i < pendingInvites.Count; i++) if (pendingInvites[i].lobbyId == lobby) return true;
        return false;
    }

#if !DISABLESTEAMWORKS
    static void OnLobbyData(LobbyDataUpdate_t u)
    {
        if (u.m_ulSteamIDLobby != u.m_ulSteamIDMember) return; // a member's data, not the lobby's
        if (!lobbies.TryGetValue(u.m_ulSteamIDLobby, out var lc)) return; // not one we asked about (SteamLobby's own)
        ulong id = u.m_ulSteamIDLobby;
        lc.answered = true;
        if (u.m_bSuccess == 0)
        {
            lc.ok = false;
            lc.gone = true;
            lc.info = null;
        }
        else
        {
            lc.gone = false;
            lc.ok = LobbyInfo.TryDecode(k => SteamLobby.GetData(id, k), out lc.info, out lc.error);
            try { lc.memberLimit = SteamMatchmaking.GetLobbyMemberLimit(new CSteamID(id)); }
            catch (Exception) { lc.memberLimit = 0; }
        }
        OnLobbyAnswered(id, lc);
        MarkDirty();
    }
#endif

    // ------------------------------------------------------------------ invites

#if !DISABLESTEAMWORKS
    static void OnLobbyInvite(LobbyInvite_t e)
    {
        if (IsFake) return;
        try
        {
            var game = new CGameID(e.m_ulGameID);
            if (e.m_ulGameID != 0 && game.IsSteamApp() && game.AppID().m_AppId != SteamBootstrap.AppId) return; // another game
        }
        catch (Exception) { return; }
        ulong lobby = e.m_ulSteamIDLobby;
        if (!ConnectArgs.LooksLikeLobbyId(lobby)) return;
        if (lobby == SteamLobby.CurrentLobbyId) return; // already there
        var inv = new FriendInvite
        {
            fromSteamId = e.m_ulSteamIDUser,
            fromName = LobbyInfo.Clean(SteamBootstrap.NameOf(e.m_ulSteamIDUser)) ?? "A friend",
            lobbyId = lobby,
            receivedAt = Time.realtimeSinceStartup,
            avatar = AvatarFor(e.m_ulSteamIDUser),
        };
        Debug.Log($"[FriendsFeed] Invite from {inv.fromName} to lobby {lobby}; checking it with Steam.");
        for (int i = pendingInvites.Count - 1; i >= 0; i--)
            if (pendingInvites[i].lobbyId == lobby && pendingInvites[i].fromSteamId == inv.fromSteamId)
            {
                pendingInvites.RemoveAt(i);
                pendingSince.RemoveAt(i);
            }
        pendingInvites.Add(inv);
        pendingSince.Add(inv.receivedAt);
        var lc = RequestLobby(lobby, force: true);
        if (lc.answered && !lc.gone) OnLobbyAnswered(lobby, lc);
    }
#endif

    /// <summary>Lobby data arrived: settle pending invites for it and refresh known ones.</summary>
    static void OnLobbyAnswered(ulong lobby, LobbyCache lc)
    {
        for (int i = pendingInvites.Count - 1; i >= 0; i--)
        {
            var inv = pendingInvites[i];
            if (inv.lobbyId != lobby) continue;
            pendingInvites.RemoveAt(i);
            pendingSince.RemoveAt(i);
            if (lc.gone) { Debug.Log($"[FriendsFeed] Invite from {inv.fromName}: that game has already ended."); continue; }
            if (!lc.ok) { Debug.Log($"[FriendsFeed] Ignoring an invite from {inv.fromName}: {lc.error}."); continue; }
            Fill(inv, lc.info);
            inv.verified = true;
            AddInvite(inv);
        }
        bool changed = false;
        for (int i = invites.Count - 1; i >= 0; i--)
        {
            if (invites[i].lobbyId != lobby) continue;
            if (lc.gone || !lc.ok) { invites.RemoveAt(i); changed = true; continue; } // the shift ended
            if (!invites[i].verified || invites[i].town == null) { Fill(invites[i], lc.info); invites[i].verified = true; changed = true; }
        }
        if (changed) RaiseChanged(force: true);
    }

    static void Fill(FriendInvite inv, LobbyInfo info)
    {
        if (info == null) return;
        inv.town = DisplayTown(info.town);
        inv.hostName = info.hostName;
    }

    static void TickPendingInvites(float now)
    {
        for (int i = pendingInvites.Count - 1; i >= 0; i--)
        {
            if (now - pendingSince[i] < InviteVerifySeconds) continue;
            // Steam hasn't answered: show it anyway (joining checks the lobby properly).
            var inv = pendingInvites[i];
            pendingInvites.RemoveAt(i);
            pendingSince.RemoveAt(i);
            AddInvite(inv);
        }
    }

    static void AddInvite(FriendInvite inv)
    {
        for (int i = invites.Count - 1; i >= 0; i--)
            if (invites[i].lobbyId == inv.lobbyId && invites[i].fromSteamId == inv.fromSteamId) invites.RemoveAt(i);
        invites.Insert(0, inv);
        while (invites.Count > MaxInvites) invites.RemoveAt(invites.Count - 1);
        RaiseChanged(force: true);
        if (InviteReceived == null) return;
        foreach (Action<FriendInvite> h in InviteReceived.GetInvocationList())
        {
            try { h(inv); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>Invites to the game we're now in were accepted: drop them.</summary>
    static void PruneAcceptedInvites()
    {
        ulong here = SteamLobby.CurrentLobbyId;
        if (here == 0) return;
        for (int i = invites.Count - 1; i >= 0; i--) if (invites[i].lobbyId == here) invites.RemoveAt(i);
    }

    // ------------------------------------------------------------------ names and avatars

#if !DISABLESTEAMWORKS
    static void OnPersona(PersonaStateChange_t e)
    {
        if ((e.m_nChangeFlags & EPersonaChange.k_EPersonaChangeAvatar) != 0 && avatars.ContainsKey(e.m_ulSteamID))
        {
            // a new picture: swap it in when Steam has it (now, or with AvatarImageLoaded_t); the old one shows until then
            try { SetAvatar(e.m_ulSteamID, LoadAvatar(SteamFriends.GetMediumFriendAvatar(new CSteamID(e.m_ulSteamID)))); }
            catch (Exception) { }
        }
        if ((e.m_nChangeFlags & EPersonaChange.k_EPersonaChangeName) != 0)
        {
            string n = LobbyInfo.Clean(SteamBootstrap.NameOf(e.m_ulSteamID));
            if (n != null)
            {
                foreach (var inv in invites) if (inv.fromSteamId == e.m_ulSteamID) inv.fromName = n;
                foreach (var inv in pendingInvites) if (inv.fromSteamId == e.m_ulSteamID) inv.fromName = n;
            }
        }
        MarkDirty();
    }

    static void OnAvatarLoaded(AvatarImageLoaded_t e) => SetAvatar(e.m_steamID.m_SteamID, LoadAvatar(e.m_iImage));

    /// <summary>Use <paramref name="tex"/> as <paramref name="id"/>'s avatar everywhere, then free the old one.</summary>
    static void SetAvatar(ulong id, Texture2D tex)
    {
        if (tex == null) return;
        avatars.TryGetValue(id, out var old);
        avatars[id] = tex;
        foreach (var inv in invites) if (inv.fromSteamId == id) inv.avatar = tex;
        foreach (var inv in pendingInvites) if (inv.fromSteamId == id) inv.avatar = tex;
        foreach (var f in friends) if (f.steamId == id) f.avatar = tex;
        RaiseChanged(force: true);
        if (old != null && old != tex) { retired.Add(old); retiredAt.Add(Time.realtimeSinceStartup); } // freed once the UI moved on
    }

    static Texture2D LoadAvatar(int handle)
    {
        if (handle <= 0) return null;
        try
        {
            if (!SteamUtils.GetImageSize(handle, out uint w, out uint h) || w == 0 || h == 0 || w > 512 || h > 512) return null;
            var buf = new byte[w * h * 4];
            if (!SteamUtils.GetImageRGBA(handle, buf, buf.Length)) return null;
            // Steam's rows run top-down; textures run bottom-up.
            var flipped = new byte[buf.Length];
            int row = (int)w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * row, flipped, ((int)h - 1 - y) * row, row);
            var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false)
            {
                name = "SteamAvatar",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.LoadRawTextureData(flipped);
            tex.Apply(false, true);
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FriendsFeed] Couldn't read a Steam avatar: {e.Message}");
            return null;
        }
    }
#endif

    /// <summary>A friend's avatar texture: cached, loaded now if Steam has it, else null (it arrives with a callback).</summary>
    static Texture2D AvatarFor(ulong steamId)
    {
        if (avatars.TryGetValue(steamId, out var t) && t != null) return t;
#if !DISABLESTEAMWORKS
        if (!SteamBootstrap.IsInitialized || steamId == 0) return null;
        try
        {
            int handle = SteamFriends.GetMediumFriendAvatar(new CSteamID(steamId));
            if (handle <= 0) return null; // 0 = none, -1 = loading (AvatarImageLoaded_t follows)
            t = LoadAvatar(handle);
            if (t != null) avatars[steamId] = t;
            return t;
        }
        catch (Exception) { return null; }
#else
        return null;
#endif
    }

    static void DestroyAvatars()
    {
        foreach (var kv in avatars) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
        avatars.Clear();
        foreach (var t in retired) if (t != null) UnityEngine.Object.Destroy(t);
        retired.Clear();
        retiredAt.Clear();
    }

    // ------------------------------------------------------------------ change notification

    static void RaiseChanged(bool force)
    {
        string sig = Signature();
        if (!force && sig == signature) return;
        signature = sig;
        if (Changed == null) return;
        foreach (Action h in Changed.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    static readonly System.Text.StringBuilder sigBuilder = new System.Text.StringBuilder(256);

    static string Signature()
    {
        var sb = sigBuilder;
        sb.Clear();
        foreach (var f in friends)
            sb.Append(f.steamId).Append(f.name).Append((int)f.activity).Append(f.lobbyId).Append(f.town).Append(f.crew)
              .Append(f.crewCap).Append((int)f.block).Append(f.avatar != null ? 1 : 0).Append('|');
        sb.Append('#');
        foreach (var i in invites) sb.Append(i.lobbyId).Append(i.fromName).Append(i.town).Append(i.avatar != null ? 1 : 0).Append('|');
        return sb.ToString();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        friends.Clear();
        invites.Clear();
        pendingInvites.Clear();
        pendingSince.Clear();
        lobbies.Clear();
        avatars.Clear(); // Stop() freed the textures when Steam shut down (leaving play mode / quit)
        retired.Clear();
        retiredAt.Clear();
        watchers = 0;
        nextScan = nextInviteRecheck = 0f;
        loggedOn = false;
        started = hooked = false;
        signature = "";
        IsFake = false;
        Changed = null;
        InviteReceived = null;
        LastJoinLobbyId = 0;
        LastJoinWhy = null;
        JoinSink = RouterJoin;
#if !DISABLESTEAMWORKS
        inviteCb = null;
        lobbyDataCb = null;
        personaCb = null;
        avatarCb = null;
        presenceCb = null;
#endif
    }
}

/// <summary>Main-menu strings (Docs/UIPass.md §4; UP-4). Head Office voice, same rules as the rest of <see cref="UiText"/>.</summary>
public static partial class UiText
{
    // ---- title + splash
    public const string MenuTitle = "RUBBISH";

    /// <summary>The sticker under the logo (one per launch, cosmetic).</summary>
    public static readonly string[] MenuSplashes =
    {
        "Now with 40% more raccoons!",
        "Smells like teamwork!",
        "Certified 3% less sticky!",
        "Ask about our compactor!",
        "Bins go in the truck. Not you.",
        "Now with extra honk!",
        "Gertrude says FEED ME.",
        "Mostly bag-shaped!",
    };

    // ---- buttons
    public const string MenuContinue = "CONTINUE";
    public const string MenuPlaySolo = "PLAY SOLO";
    public const string MenuHostFriends = "HOST WITH FRIENDS";
    public const string MenuJoinFriend = "JOIN A FRIEND";
    public const string MenuChangeSave = "CHANGE SAVE…";
    public const string MenuCrewCard = "CREW CARD";
    public const string MenuSettings = "SETTINGS";
    public const string MenuQuit = "QUIT";
    public const string MenuNeedsInternet = "NEEDS INTERNET";
    public const string MenuBusy = "ONE SEC";

    // ---- continue card
    /// <summary>The Town every legacy save lives in (the exit-road signs say "Now leaving Maple Hollow"). B14 replaces it.</summary>
    public const string MenuLegacyTown = "Maple Hollow";
    /// <summary>The company every legacy save belongs to, until B14's company cards.</summary>
    public const string MenuLegacyCompany = "RUBBISH CO.";
    public const string MenuModeSolo = "SOLO";
    public const string MenuModeFriends = "WITH FRIENDS";
    public const string MenuCardNewTitle = "FIRST SHIFT";
    public const string MenuCardNewLine = "Gertrude is warmed up. The bins are full.";
    public const string MenuLastPlayedNever = "never clocked in";
    public const string MenuLastPlayedNow = "last played just now";
    public const string MenuLastPlayedFmt = "last played {0} ago";

    // ---- quit
    public const string MenuQuitBody = "Rubbish Co. saves itself. The raccoons will wait.";
    public const string MenuQuitEditor = "Quit asked in the Editor: logging instead of quitting.";

    // ---- join screen
    public const string MenuJoinTitle = "JOIN A FRIEND";
    public const string MenuJoinCopy = "You'll join as crew in their shop. Your own companies stay safe at home.";
    public const string MenuJoinPlateTop = "RUBBISH CO.";
    public const string MenuJoinPlateBottom = "THE BIN STATE";
    public const string MenuJoinHint = "Type or paste their 6-letter code. They see it in their Esc menu.";
    public const string MenuJoinSteamHint = "Got a Steam invite? Accept it, or use Join Game in your Steam friends list.";
    public const string MenuJoinButton = "JOIN";
    public const string MenuJoinNeedCode = "That's not six letters yet.";

    // ---- busy / leave cards (NetErrorPanel restyle, §4.6)
    public const string MenuBusyHost = "Opening the shutters…";
    public const string MenuBusyHostFriends = "Opening the shop to friends…";
    public const string MenuBusyJoin = "Hopping on your friend's truck…";
    public const string MenuBusyJoinFmt = "Hopping on {0}'s truck…";
    public const string MenuBusyTitle = "ONE MOMENT";
    public const string MenuCancel = "Cancel";
    public const string MenuOk = "OK";
    public const string MenuTryAgain = "Try again";
    public const string MenuStampShiftOver = "SHIFT OVER";
    public const string MenuStampKicked = "SENT HOME";
    public const string MenuStampLost = "LOST";
    public const string MenuStampOops = "OOPS";

    // ---- identity / footer
    public const string MenuIdentitySteam = "Steam";
    public const string MenuIdentityDevBuild = "dev identity · no Steam";
    public const string MenuVersionFmt = "v{0} {1} · Archway Games";

    // ---- invite cards (UP-5 fills them)
    public const string MenuInviteFmt = "{0} invited you";
    public const string MenuInviteJoin = "JOIN";
}

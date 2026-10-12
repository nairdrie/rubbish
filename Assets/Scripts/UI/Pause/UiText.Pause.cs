// The Esc menu's strings (UP-3), part of the one UiText class (Docs/UIPass.md §3 "Copy voice": Head Office's voice).
public static partial class UiText
{
    /// <summary>The Esc timesheet's strings (nested so the menu builder's additions to UiText never collide).</summary>
    public static class Pause
    {
        // ---- timesheet header
        public const string TimesheetTitle = "RUBBISH CO. TIMESHEET";
        /// <summary>The Town's name until M1's SaveSlots names Towns (B8 imports the legacy save as "Maple Hollow").</summary>
        public const string DefaultTownName = "Maple Hollow";
        public const string StampPaused = "PAUSED";
        public const string StampOnBreak = "ON BREAK";
        public const string WorldKeepsGoing = "The world keeps going while you're on break.";

        /// <summary>Head Office sticky notes on the clipboard (a different one each time you pause).</summary>
        public static readonly string[] BreakNotes =
        {
            "Breaks are 15 minutes. We're counting.",
            "Raccoons do not get breaks.",
            "Hydrate. Then chuck.",
            "Gertrude misses you already.",
            "Do NOT eat the lunch in the compactor.",
            "Smile! The bins can tell.",
        };

        // ---- left column
        public const string Resume = "RESUME";
        public const string OpenShop = "OPEN SHOP TO FRIENDS";
        public const string InviteFriends = "INVITE FRIENDS";
        public const string SettingsButton = "SETTINGS";
        public const string ControlsButton = "CONTROLS";
        public const string Unstuck = "UNSTUCK";
        public const string UnstuckCooldown = "UNSTUCK ({0})";
        public const string Messages = "MESSAGES";
        public const string MessagesCount = "MESSAGES ({0})";
        public const string SaveQuit = "SAVE & QUIT TO MENU";
        public const string CloseShop = "CLOSE TOWN";
        public const string LeaveShift = "LEAVE TOWN";
        public const string QuitDesktop = "QUIT TO DESKTOP";

        // ---- right column tabs
        public static readonly string[] PauseTabNames = { "BUSINESS", "PLAYERS", "RULES", "INVITE", "MESSAGES" };

        // ---- confirms (§5.7)
        public const string OpenShopTitle = "Open shop to friends?";
        public const string OpenShopBody = "Saves, then reopens this Town as hosted. You'll start in the garage.";
        public const string OpenShopYes = "Open the shutters";
        public const string OpenShopNo = "Stay solo";
        public const string OpeningShop = "Opening the shutters…";
        public const string SaveQuitTitle = "Head home for the day?";
        public const string SaveQuitBody = "Everything's filed. The trash will still be here tomorrow.";
        public const string SaveQuitYes = "Save & quit";
        public const string CloseShopTitle = "Close this Town session?";
        public const string CloseShopBody = "You are the session host. Everyone disconnects and session jobs end. This is not firing an employee.";
        public const string CloseShopYes = "Close Town";
        public const string KeepWorking = "Keep working";
        public const string LeaveTitle = "Leave {0}'s Town?";
        public const string LeaveBody = "Disconnects from this multiplayer session and ends any current job. To stop working for someone but stay here, choose QUIT JOB instead.";
        public const string LeaveYes = "Leave Town";
        public const string Stay = "Stay";
        public const string QuitBodySolo = "We'll save first. Chuck will wait by the truck.";
        public const string QuitBodyHost = "This closes the Town session for everyone and closes the game.";
        public const string QuitBodyGuest = "You'll leave this Town session, end any current job and close the game.";
        public const string TheBoss = "the host";

        // ---- shift card (§5.3)
        public const string TodaysShift = "YOUR BUSINESS";
        public const string Bags = "BAGS";
        public const string Net = "NET";
        public const string Finds = "FINDS";
        public const string WorkOrdersHeading = "WORK ORDERS";
        public const string NoWorkOrders = "No Work Orders today. Head Office is on holiday.";
        public const string RankLabel = "RANK {0}";
        public const string RepLine = "{0} Rep";
        public const string NeedForRank = "{0} to Rank {1}";
        public const string CleanDays = "{0} clean day{1}";
        public const string PromotionAtMidnight = "Promotion at the midnight review!";
        public const string TopRank = "Top of the heap. Head Office salutes you.";
        public const string BestFind = "Best find today: {0} (about {1})";
        public const string CityLine = "City {0}% · {1}";
        public const string GuestShiftNote = "You're visiting {0}'s Town. HOST / GUEST means who runs the session, not who employs you. You only work for someone after accepting their job offer. QUIT JOB keeps you in Town; LEAVE TOWN disconnects.";

        // ---- crew (§5.6)
        public const string Boss = "HOST";
        public const string Crew = "GUEST";
        public const string Kick = "Kick";
        public const string KickTitle = "Kick {0}?";
        public const string KickBody = "They can rejoin.";
        public const string KickYes = "Send them home";
        public const string KickReason = "The session host removed you from Town. You can rejoin.";
        public const string NobodyYet = "Nobody else is here yet. Send an invite!";
        public const string PingMs = "{0} ms";

        // ---- town rules (§5.6)
        public const string RulesFooterHost = "Changes apply to everyone now and are saved for the next Town you host. Other game mechanics are not rule switches.";
        public const string RulesFooterGuest = "Only the session host can change this rule. Other game mechanics are always on.";

        // ---- invite (§5.2)
        public const string InviteCode = "INVITE CODE";
        public const string Show = "Show";
        public const string Hide = "Hide";
        public const string Copy = "Copy";
        public const string Copied = "Invite code copied. Paste it to your friends.";
        public const string InviteSteam = "INVITE STEAM FRIENDS";
        public const string InviteHostHelp = "Friends type this code in Join a friend. Streamer mode keeps it hidden until you press Show.";
        public const string InviteGuestHelp = "Only the session host can send Steam invites. Pass this code on to bring more players.";
        public const string InviteNoCode = "This game has no invite code (it's a local game). Host with friends from the menu to get one.";
        public const string Plate = "RUBBISH CO.";

        // ---- messages (§5.6)
        public const string NoMessages = "No messages yet. Head Office is suspiciously quiet.";
        public const string MessagesNote = "This shift's last 30 messages, newest first. Not saved.";
        public const string Join = "JOIN";

        // ---- unstuck (§5.5)
        public const string UnstuckToast = "Unstuck!";
        public const string WriggleFree = "Wriggle free first!";
        public const string TowTitle = "Call a tow?";
        public const string TowBody = "A {0} Prime Tow lifts the truck onto the nearest road. {1}, thanks.";
        public const string TowYes = "Call the tow";
        public const string UnstuckNowhere = "Couldn't find a safe spot nearby. Try walking a bit first.";
        // On foot, Unstuck calls your truck to you (TruckCall). The short ones fit the tape on the disabled button.
        public const string CallTitle = "Call your truck?";
        public const string CallBody = "A {0} Prime Tow drone flies it over to wherever you're standing. {1}, thanks.";
        public const string CallYes = "Send the drone";
        public const string TruckComing = "Truck's on its way! Don't stand under it.";
        public const string TruckHere = "Your truck is right there. Wave at it.";
        public const string TruckNoRoom = "No room for the truck near you. Try a wider spot.";
        public const string TruckIndoors = "Step outside first. The drone can't do doorways.";
        public const string TruckNone = "No truck here to call.";
        public const string TruckInUse = "Truck's in use!";
        public const string TruckInTransit = "Truck's coming!";
        public const string TruckRolling = "The truck is still rolling to a stop. Try again in a sec.";
        public const string TruckTooFast = "The drone needs a minute. Try again.";
        public const string TruckOffline = "The tow service can't reach the Town yet. Try again in a sec.";
        public const string LandFirst = "Land first!";
    }
}

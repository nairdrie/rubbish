using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// UP-5's strings, part of the one UiText class (Docs/UIPass.md §3 "Copy voice": Head Office, cheerful, never mean).
public static partial class UiText
{
    /// <summary>"Join a friend" strings (UP-5), nested so other packages' additions to UiText never collide.</summary>
    public static class Friends
    {
        public const string SheetTitle = "JOIN A FRIEND";
        public const string CrewCopy = "You'll join as crew in their shop. Your own companies stay safe at home.";
        public const string FriendsOnlyStamp = "FRIENDS ONLY";
        public const string TabPlaying = "FRIENDS PLAYING";
        public const string TabInvites = "INVITES";
        public const string TabCode = "CODE";
        public const string Join = "JOIN";
        public const string UseCode = "Use a join code";

        public const string CaptionPlaying = "Friends in Rubbish right now · live from Steam";
        public const string CaptionInvites = "Invites you got this session · newest first";
        public const string CaptionFake = "Pretend friends (test data, not Steam)";
        public const string CaptionJoining = "Joining… hang on to your hat.";

        // row lines
        public const string Solo = "Playing solo";
        public const string Checking = "Checking their game…";
        public const string Closed = "Not open to friends right now";
        public const string InYourGame = "In your game";
        public const string StampFull = "FULL";
        public const string StampUpdate = "UPDATE";
        public const string StampHere = "YOU'RE HERE";
        public const string InvitedYou = "{0} invited you";
        public const string CheckingInvite = "Checking the invite…";

        public static string ShiftOf(string host) => string.IsNullOrEmpty(host) ? "a shift" : host + "'s shift";

        /// <summary>"Hosting Maple Hollow · 2/4 crew", "On BigAl's shift in Binchester · 4/4", "Playing solo"…</summary>
        public static string FriendLine(FriendPlaying f)
        {
            if (f == null) return "";
            switch (f.block)
            {
                case FriendJoinBlock.NoLobby: return Solo;
                case FriendJoinBlock.Checking: return Checking;
                case FriendJoinBlock.Closed: return Closed;
                case FriendJoinBlock.AlreadyThere: return InYourGame;
            }
            string seats = f.crewCap > 0 ? $"{Mathf.Max(1, f.crew)}/{f.crewCap}" : null;
            if (f.activity == FriendActivity.Hosting)
                return (string.IsNullOrEmpty(f.town) ? "Hosting a shift" : "Hosting " + f.town) + (seats != null ? $" · {seats} crew" : "");
            string where = "On " + ShiftOf(f.hostName) + (string.IsNullOrEmpty(f.town) ? "" : " in " + f.town);
            return where + (seats != null ? " · " + seats : "");
        }

        /// <summary>The narrow-panel line: "Maple Hollow · 2/4", "Playing solo"…</summary>
        public static string FriendLineShort(FriendPlaying f)
        {
            if (f == null) return "";
            switch (f.block)
            {
                case FriendJoinBlock.NoLobby: return Solo;
                case FriendJoinBlock.Checking: return "Checking…";
                case FriendJoinBlock.Closed: return "Not open to friends";
                case FriendJoinBlock.AlreadyThere: return InYourGame;
            }
            string where = !string.IsNullOrEmpty(f.town) ? f.town : ShiftOf(f.hostName);
            return f.crewCap > 0 ? $"{where} · {Mathf.Max(1, f.crew)}/{f.crewCap}" : where;
        }

        /// <summary>Where an invite is to: the Town, "Gus's shift", or "Checking the invite…".</summary>
        public static string InviteWhere(FriendInvite i) =>
            i == null ? "" : !i.verified ? CheckingInvite : !string.IsNullOrEmpty(i.town) ? i.town : ShiftOf(i.hostName ?? i.fromName);

        /// <summary><see cref="FeelUi.Fit"/> for null-safe strings.</summary>
        public static string FitTo(TMPro.TMP_Text t, string s, float w) => FeelUi.Fit(t, s ?? "", w);

        /// <summary>"Maple Hollow · 2 min ago" / "Gus's shift · just now".</summary>
        public static string InviteLine(FriendInvite i, float now)
        {
            if (i == null) return "";
            return InviteWhere(i) + " · " + Ago(now - i.receivedAt);
        }

        public static string Ago(float seconds)
        {
            if (seconds < 60f) return "just now";
            int m = Mathf.FloorToInt(seconds / 60f);
            if (m < 60) return m + " min ago";
            return Mathf.FloorToInt(m / 60f) + " h ago";
        }

        // empty / offline states
        public const string EmptyPlayingTitle = "Nobody's on shift.";
        public const string EmptyPlayingBody = "None of your Steam friends are running a Rubbish shift right now. Host with friends and invite them, or poke them on Discord.";
        public const string EmptyInvitesTitle = "No invites yet.";
        public const string EmptyInvitesBody = "When a friend invites you, it lands here (and pops up in the corner). Invites last until their shift ends.";

        public static void OffState(FriendsFeedOff why, out string title, out string body)
        {
            const string codes = "\nJoin codes still work.";
            switch (why)
            {
                case FriendsFeedOff.LoggedOff:
                    title = "Steam is offline.";
                    body = "Your friends show up here when Steam reconnects." + codes;
                    return;
                case FriendsFeedOff.Editor:
                    title = "Steam's napping.";
                    body = "The Editor runs without Steam, so there's no friends list here." + codes;
                    return;
                case FriendsFeedOff.Skipped:
                    title = "Steam's napping.";
                    body = "Rubbish started without Steam (-nosteam), so there's no friends list." + codes;
                    return;
                case FriendsFeedOff.DevBuild:
                    title = "Steam's napping.";
                    body = "This build started without Steam (add -steam to use it), so there's no friends list." + codes;
                    return;
                case FriendsFeedOff.NotRunning:
                    title = "Steam isn't running.";
                    body = "Start Steam and sign in, then restart Rubbish to see your friends." + codes;
                    return;
                default:
                    title = "No Steam here.";
                    body = "Steam isn't available on this computer, so there's no friends list." + codes;
                    return;
            }
        }

        // invite pop-ups
        public static string InviteToast(FriendInvite i) =>
            $"{i.fromName} invited you to {(string.IsNullOrEmpty(i.town) ? ShiftOf(i.hostName ?? i.fromName) : i.town)}! Shift+Tab to accept in Steam.";

        public static string InviteMenuSay(FriendInvite i) =>
            $"{i.fromName} invited you to {(string.IsNullOrEmpty(i.town) ? ShiftOf(i.hostName ?? i.fromName) : i.town)}. Join a friend → Invites.";
    }
}

/// <summary>
/// The FRIENDS PLAYING and INVITES pages of "Join a friend" (Docs/UIPass.md §4.4, UP-5), built on <see cref="UiKit"/> from
/// <see cref="SteamFriendsFeed"/>'s plain DTOs (no Steam calls here). Mount one into any page rect with <see cref="Create"/>
/// (UP-4's JoinScreen hosts it under its own FRIENDS PLAYING | INVITES | CODE tabs and calls <see cref="Show"/>);
/// <see cref="FriendsSheet"/> is a ready-made screen around it.
/// <list type="bullet">
/// <item>Friends rows: polaroid avatar (Steam picture, or a Dumpling-coloured initial), name, what they're doing
/// ("Hosting Maple Hollow · 2/4 crew"), crew seats, and [JOIN] → <see cref="SteamFriendsFeed.Join"/> →
/// <c>SteamJoinRouter.RequestJoin</c>. Full or out-of-date shifts get a rubber stamp instead of a button.</item>
/// <item>Invite rows: "Dot invited you", the Town and how long ago, [JOIN] and [✕].</item>
/// <item>Empty and offline states with a raccoon doodle and friendly copy (no Steam on this machine → "Steam's napping").</item>
/// </list>
/// Updates live (the feed's <see cref="SteamFriendsFeed.Changed"/>), keeps keyboard focus on the same row across updates, and
/// works in edit mode for previews (call <see cref="RebuildNow"/>).
/// </summary>
public sealed class FriendsPanel : MonoBehaviour
{
    public enum Page { Playing, Invites }

    /// <summary>Toast in-game invites (§4.8). Off for hosts that show their own.</summary>
    public static bool ToastInGame = true;

    public Page ShownPage { get; private set; }

    /// <summary>The JOIN buttons on the current page, top to bottom.</summary>
    public IReadOnlyList<UiButton> JoinButtons => joinButtons;

    /// <summary>Rows on the current page (0 = the empty or offline state is showing).</summary>
    public int RowCount { get; private set; }

    /// <summary>The empty/offline state is showing (and which title it has).</summary>
    public bool ShowingEmpty => emptyRoot != null && emptyRoot.gameObject.activeSelf;
    public string EmptyTitle { get; private set; }

    /// <summary>The first thing to focus on this page (the first JOIN, or the empty state's button); null if none.</summary>
    public Selectable FirstSelectable { get; private set; }

    /// <summary>What Up from the first row selects (the host's tabs).</summary>
    public Selectable NavUp { get; set; }

    /// <summary>The page was rebuilt (counts may have changed: refresh tab labels with <see cref="TabLabel"/>).</summary>
    public event Action Rebuilt;

    /// <summary>A JOIN was pressed and handed to Steam (lobby id).</summary>
    public event Action<ulong> JoinRequested;

    /// <summary>"FRIENDS PLAYING (1)" / "INVITES (2)" (no number when zero).</summary>
    public static string TabLabel(Page p)
    {
        int n = p == Page.Playing ? SteamFriendsFeed.JoinableCount : SteamFriendsFeed.Invites.Count;
        string name = p == Page.Playing ? UiText.Friends.TabPlaying : UiText.Friends.TabInvites;
        return n > 0 ? $"{name} ({n})" : name;
    }

    // ------------------------------------------------------------------ layout constants (reference px)

    const float RowH = 112f, CaptionH = 44f;

    /// <summary>Row density by width: Full (≥ 860: seat pips), Medium (≥ 620), Compact (narrow side panels like the menu's
    /// Join page: smaller avatar and buttons, the short status line).</summary>
    public enum Density { Full, Medium, Compact }
    public Density RowDensity { get; private set; }

    float avatarSize = 84f, joinW = 170f, joinH = 66f, dismissSize = 56f, cursorGap = 56f, leftPad = 24f;

    RectTransform root, list, emptyRoot;
    ScrollRect scroll;
    TextMeshProUGUI caption;
    readonly List<UiButton> joinButtons = new List<UiButton>();
    readonly List<RowView> rows = new List<RowView>();
    readonly List<TextMeshProUGUI> zees = new List<TextMeshProUGUI>();
    Action useCode;
    IDisposable watch;
    bool dirty, built;
    float nextRebuild, nextAgoTick, errorUntil;
    string errorText;
    float builtWidth;

    sealed class RowView
    {
        public ulong key;
        public UiButton join, dismiss;
        public RectTransform rect;
    }

    // ------------------------------------------------------------------ create

    /// <summary>
    /// Build the panel filling <paramref name="host"/> (a page rect on a paper surface). <paramref name="useCode"/> (optional)
    /// adds "Use a join code" to the empty and offline states.
    /// </summary>
    public static FriendsPanel Create(RectTransform host, Page page, Action useCode = null)
    {
        var rt = FeelUi.Stretch("FriendsPanel", host);
        var p = rt.gameObject.AddComponent<FriendsPanel>();
        p.useCode = useCode;
        p.root = rt;
        p.ShownPage = page;
        p.BuildFrame();
        p.RebuildNow();
        return p;
    }

    /// <summary>Switch page (rebuilds now).</summary>
    public void Show(Page p)
    {
        ShownPage = p;
        if (scroll != null) scroll.verticalNormalizedPosition = 1f;
        RebuildNow();
    }

    void BuildFrame()
    {
        caption = UiKit.Label(root, "Caption", "", UiTheme.Small, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic, UiTheme.InkSoft);
        var cr = caption.rectTransform;
        cr.anchorMin = new Vector2(0f, 1f);
        cr.anchorMax = new Vector2(1f, 1f);
        cr.pivot = new Vector2(0f, 1f);
        cr.offsetMin = new Vector2(12f, -CaptionH);
        cr.offsetMax = new Vector2(-12f, 0f);

        scroll = UiKit.ScrollList(root, new Vector2(800f, 400f));
        UiKit.Inset((RectTransform)scroll.transform, 0f, 0f, 0f, CaptionH);
        list = scroll.content;

        emptyRoot = FeelUi.Stretch("Empty", root);
        emptyRoot.offsetMax = new Vector2(0f, -CaptionH);
        emptyRoot.gameObject.SetActive(false);
    }

    void OnEnable()
    {
        SteamFriendsFeed.Changed += OnFeedChanged;
        if (Application.isPlaying) watch = SteamFriendsFeed.Watch();
        if (built) dirty = true;
    }

    void OnDisable()
    {
        SteamFriendsFeed.Changed -= OnFeedChanged;
        watch?.Dispose();
        watch = null;
    }

    void OnFeedChanged() => dirty = true;

    void OnRectTransformDimensionsChange()
    {
        if (built && root != null && Mathf.Abs(root.rect.width - builtWidth) > 1f) dirty = true;
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (ShownPage == Page.Invites && SteamFriendsFeed.Invites.Count > 0 && now >= nextAgoTick) { nextAgoTick = now + 10f; dirty = true; }
        if (errorUntil > 0f && Time.realtimeSinceStartup > errorUntil) { errorUntil = 0f; RefreshCaption(); }
        bool busy = SteamJoinRouter.Busy && !SteamFriendsFeed.IsFake;
        foreach (var b in joinButtons)
            if (b != null && b.interactable == busy) b.SetDisabled(busy ? "busy" : null, showTape: false);
        if (busy) RefreshCaption();
        for (int i = 0; i < zees.Count; i++)
        {
            if (zees[i] == null) continue;
            var r = zees[i].rectTransform;
            float t = Mathf.Repeat(now * 0.45f + i * 0.33f, 1f);
            r.anchoredPosition = new Vector2(r.anchoredPosition.x, 40f + i * 22f + t * 26f);
            zees[i].alpha = Mathf.Sin(t * Mathf.PI);
        }
        if (dirty && now >= nextRebuild) RebuildNow();
    }

    // ------------------------------------------------------------------ rebuild

    /// <summary>Rebuild the current page from the feed now (previews call this; play mode does it on feed changes).</summary>
    public void RebuildNow()
    {
        dirty = false;
        nextRebuild = Time.unscaledTime + 0.25f;
        built = true;
        builtWidth = root.rect.width;

        ulong focusKey = 0;
        bool focusDismiss = false;
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        foreach (var r in rows)
        {
            if (sel == null) break;
            if (r.join != null && sel == r.join.gameObject) focusKey = r.key;
            if (r.dismiss != null && sel == r.dismiss.gameObject) { focusKey = r.key; focusDismiss = true; }
        }

        for (int i = list.childCount - 1; i >= 0; i--) DestroyNow(list.GetChild(i).gameObject);
        for (int i = emptyRoot.childCount - 1; i >= 0; i--) DestroyNow(emptyRoot.GetChild(i).gameObject);
        rows.Clear();
        joinButtons.Clear();
        zees.Clear();
        FirstSelectable = null;
        EmptyTitle = null;

        float rowW = RowWidth();
        RowDensity = rowW >= 860f ? Density.Full : rowW >= 620f ? Density.Medium : Density.Compact;
        bool compact = RowDensity == Density.Compact;
        avatarSize = compact ? 66f : 84f;
        joinW = compact ? 110f : RowDensity == Density.Medium ? 150f : 170f;
        joinH = compact ? 58f : 66f;
        dismissSize = compact ? 44f : 56f;
        cursorGap = compact ? 40f : 56f;
        leftPad = compact ? 16f : 24f;
        float now = Time.realtimeSinceStartup;
        var off = SteamFriendsFeed.Off;
        if (off != FriendsFeedOff.None)
        {
            UiText.Friends.OffState(off, out string t, out string b);
            BuildEmpty(t, b, sad: true);
        }
        else if (ShownPage == Page.Playing)
        {
            var fs = SteamFriendsFeed.Friends;
            for (int i = 0; i < fs.Count; i++) BuildFriendRow(fs[i], i, rowW);
            if (fs.Count == 0) BuildEmpty(UiText.Friends.EmptyPlayingTitle, UiText.Friends.EmptyPlayingBody, sad: false);
        }
        else
        {
            var inv = SteamFriendsFeed.Invites;
            for (int i = 0; i < inv.Count; i++) BuildInviteRow(inv[i], i, rowW, now);
            if (inv.Count == 0) BuildEmpty(UiText.Friends.EmptyInvitesTitle, UiText.Friends.EmptyInvitesBody, sad: false);
        }
        RowCount = rows.Count;
        scroll.gameObject.SetActive(rows.Count > 0);
        emptyRoot.gameObject.SetActive(rows.Count == 0);

        // keyboard: JOIN buttons top to bottom; ✕ sits to the right of its JOIN
        UiKit.ChainVertical(joinButtons, NavUp, null);
        foreach (var r in rows)
        {
            if (r.join == null || r.dismiss == null) continue;
            var n = r.join.navigation; n.selectOnRight = r.dismiss; r.join.navigation = n;
            var d = r.dismiss.navigation;
            d.mode = Navigation.Mode.Explicit;
            d.selectOnLeft = r.join;
            d.selectOnUp = n.selectOnUp;
            d.selectOnDown = n.selectOnDown;
            r.dismiss.navigation = d;
        }
        if (FirstSelectable == null && joinButtons.Count > 0) FirstSelectable = joinButtons[0];
        RefreshCaption();

        if (focusKey != 0)
        {
            foreach (var r in rows)
            {
                if (r.key != focusKey) continue;
                var target = focusDismiss && r.dismiss != null ? r.dismiss : r.join;
                if (target != null) UiKit.Focus(target.gameObject);
                break;
            }
        }
        if (Application.isPlaying == false) Canvas.ForceUpdateCanvases();
        try { Rebuilt?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    float RowWidth()
    {
        float w = root.rect.width;
        if (w < 10f) w = 1000f;
        return w - 22f; // the scroll list's bar gutter
    }

    void RefreshCaption()
    {
        if (caption == null) return;
        if (errorUntil > 0f && Time.realtimeSinceStartup <= errorUntil)
        {
            caption.text = errorText;
            caption.color = UiTheme.StampRed;
            caption.fontStyle = FontStyles.Bold;
            return;
        }
        caption.color = UiTheme.InkSoft;
        caption.fontStyle = FontStyles.Italic;
        string s;
        if (SteamFriendsFeed.Off != FriendsFeedOff.None || rows.Count == 0) s = "";
        else if (SteamJoinRouter.Busy && !SteamFriendsFeed.IsFake) s = UiText.Friends.CaptionJoining;
        else if (SteamFriendsFeed.IsFake) s = UiText.Friends.CaptionFake;
        else s = ShownPage == Page.Playing ? UiText.Friends.CaptionPlaying : UiText.Friends.CaptionInvites;
        caption.text = FeelUi.Fit(caption, s, Mathf.Max(100f, root.rect.width - 24f));
    }

    // ------------------------------------------------------------------ rows

    RectTransform NewRow(string name, int index, bool highlight)
    {
        var rt = FeelUi.Rect(name, list, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(900f, RowH));
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = RowH;
        var card = FeelUi.Img(rt, "Card", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.PaperDark, index % 2 == 0 ? 0.75f : 0.45f), 0.3f);
        card.rectTransform.offsetMin = new Vector2(4f, 4f);
        card.rectTransform.offsetMax = new Vector2(-4f, -4f);
        card.raycastTarget = true;
        if (highlight)
        {
            var stripe = FeelUi.Img(rt, "Ready", FeelUi.Rounded, UiTheme.Go, 0.15f);
            stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
            stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
            stripe.rectTransform.pivot = new Vector2(0f, 0.5f);
            stripe.rectTransform.offsetMin = new Vector2(4f, 12f);
            stripe.rectTransform.offsetMax = new Vector2(12f, -12f);
        }
        return rt;
    }

    void BuildFriendRow(FriendPlaying f, int index, float rowW)
    {
        var rt = NewRow("Friend " + f.name, index, f.Joinable);
        var view = new RowView { key = f.steamId, rect = rt };
        Color dot = f.Joinable ? UiTheme.Go
            : f.block == FriendJoinBlock.NoLobby || f.block == FriendJoinBlock.Checking ? UiTheme.Disabled : UiTheme.Gold;
        Polaroid(rt, f.steamId, f.name, f.avatar, dot, envelope: false);

        // right side: JOIN, or a stamp saying why not
        float right = 22f;
        if (f.Joinable)
        {
            ulong lobby = f.lobbyId;
            string who = f.name;
            var b = UiKit.Button(rt, UiText.Friends.Join, () => OnJoin(lobby, "Friends playing: " + who), UiButtonStyle.Go);
            b.Size(joinW, joinH);
            UiKit.Place(b.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(joinW, joinH));
            view.join = b;
            joinButtons.Add(b);
            right += joinW + cursorGap; // room for the focus bag cursor
        }
        else
        {
            string stamp = f.block switch
            {
                FriendJoinBlock.Full => UiText.Friends.StampFull,
                FriendJoinBlock.Update => UiText.Friends.StampUpdate,
                FriendJoinBlock.AlreadyThere => UiText.Friends.StampHere,
                _ => null,
            };
            if (stamp != null)
                right += Stamp(rt, stamp, right, f.block == FriendJoinBlock.AlreadyThere ? UiTheme.Go : UiTheme.StampRed,
                               RowDensity == Density.Compact ? 22f : 26f) + 24f;
        }

        // crew seats: filled Dumplings for people on the shift, empty rings for free seats (the line says "2/4" too)
        if (RowDensity == Density.Full && f.crewCap > 0 && f.lobbyId != 0 && f.block != FriendJoinBlock.Checking && f.block != FriendJoinBlock.Closed)
            right += Seats(rt, Mathf.Clamp(f.crew, 1, f.crewCap), f.crewCap, right) + 20f;

        string line = RowDensity == Density.Compact ? UiText.Friends.FriendLineShort(f) : UiText.Friends.FriendLine(f);
        TextColumn(rt, f.name, line, TextLeft, rowW - right, RowDensity == Density.Compact);
        rows.Add(view);
    }

    void BuildInviteRow(FriendInvite inv, int index, float rowW, float now)
    {
        var rt = NewRow("Invite " + inv.fromName, index, inv.verified);
        var view = new RowView { key = inv.lobbyId ^ inv.fromSteamId, rect = rt };
        Polaroid(rt, inv.fromSteamId, inv.fromName, inv.avatar, inv.verified ? UiTheme.Go : UiTheme.Disabled, envelope: true);

        float right = 22f;
        var x = UiKit.Button(rt, "", () => OnDismiss(inv), UiButtonStyle.Quiet);
        x.name = "Button Dismiss";
        x.Size(dismissSize, dismissSize);
        UiKit.Place(x.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(dismissSize, dismissSize));
        var cross = FeelUi.Img(x.Visual, "Cross", UiKit.Sprites.Cross, UiTheme.Ink);
        float ci = Mathf.Round(dismissSize * 0.25f);
        UiKit.Inset(cross.rectTransform, ci, ci, ci, ci);
        view.dismiss = x;
        right += dismissSize + (RowDensity == Density.Compact ? 10f : 14f);

        ulong lobby = inv.lobbyId;
        string who = inv.fromName;
        var b = UiKit.Button(rt, UiText.Friends.Join, () => OnJoin(lobby, "Invite from " + who), UiButtonStyle.Go);
        b.Size(joinW, joinH);
        UiKit.Place(b.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(joinW, joinH));
        view.join = b;
        joinButtons.Add(b);
        right += joinW + cursorGap;

        if (RowDensity == Density.Compact)
            TextColumn3(rt, inv.fromName, UiText.Friends.InviteWhere(inv), UiText.Friends.Ago(now - inv.receivedAt), TextLeft, rowW - right);
        else
            TextColumn(rt, string.Format(UiText.Friends.InvitedYou, inv.fromName), UiText.Friends.InviteLine(inv, now), TextLeft, rowW - right, false);
        rows.Add(view);
    }

    float TextLeft => leftPad + avatarSize + leftPad;

    /// <summary>Name (bold) over a status line, both fitted with "…" to the space left of the controls.</summary>
    static void TextColumn(RectTransform row, string title, string line, float left, float rightEdge, bool compact)
    {
        float w = Mathf.Max(100f, rightEdge - left);
        var name = UiKit.Label(row, "Name", "", compact ? 26f : 30f, TextAlignmentOptions.BottomLeft, false, FontStyles.Bold);
        UiKit.Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(left, 2f), new Vector2(w, 40f));
        name.text = FeelUi.Fit(name, title ?? "", w);
        var sub = UiKit.Label(row, "Line", "", compact ? 20f : 22f, TextAlignmentOptions.TopLeft, false, FontStyles.Normal, UiTheme.InkSoft);
        UiKit.Place(sub.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(left, -2f), new Vector2(w, 32f));
        sub.text = FeelUi.Fit(sub, line ?? "", w);
    }

    /// <summary>Narrow invite rows: the name over two short lines (where, when); the envelope on the photo says "invite".</summary>
    static void TextColumn3(RectTransform row, string title, string line1, string line2, float left, float rightEdge)
    {
        float w = Mathf.Max(100f, rightEdge - left);
        var name = UiKit.Label(row, "Name", "", 26f, TextAlignmentOptions.BottomLeft, false, FontStyles.Bold);
        UiKit.Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(left, 14f), new Vector2(w, 34f));
        name.text = UiText.Friends.FitTo(name, title, w);
        var a = UiKit.Label(row, "Line", "", 20f, TextAlignmentOptions.TopLeft, false, FontStyles.Normal, UiTheme.InkSoft);
        UiKit.Place(a.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(left, 12f), new Vector2(w, 26f));
        a.text = UiText.Friends.FitTo(a, line1, w);
        var b = UiKit.Label(row, "When", "", 20f, TextAlignmentOptions.TopLeft, false, FontStyles.Italic, UiTheme.InkSoft);
        UiKit.Place(b.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(left, -14f), new Vector2(w, 26f));
        b.text = UiText.Friends.FitTo(b, line2, w);
    }

    /// <summary>A tilted polaroid: the Steam avatar, or the friend's initial on a Dumpling colour; a status dot; an envelope.</summary>
    void Polaroid(RectTransform row, ulong steamId, string name, Texture2D avatar, Color dot, bool envelope)
    {
        float av = avatarSize, k = avatarSize / 84f;
        var frame = FeelUi.Rect("Polaroid", row, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(leftPad + av * 0.5f, 0f),
                                new Vector2(av, av));
        frame.localRotation = Quaternion.Euler(0f, 0f, UiKit.TiltFor("p" + steamId, 4f));
        var sh = FeelUi.Img(frame, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.3f), 0.15f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(3f, -4f);
        FeelUi.Img(frame, "Frame", FeelUi.Rounded, Color.white, 0.15f);
        FeelUi.Img(frame, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.15f);
        var photoRt = FeelUi.Rect("Photo", frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 3f),
                                  new Vector2(av - 14f * k, av - 18f * k));
        if (avatar != null)
        {
            var raw = photoRt.gameObject.AddComponent<RawImage>();
            raw.texture = avatar;
            raw.raycastTarget = false;
        }
        else
        {
            Color c = TintFor(steamId);
            var bg = photoRt.gameObject.AddComponent<Image>();
            bg.color = c;
            bg.raycastTarget = false;
            string initial = "?";
            if (!string.IsNullOrEmpty(name))
                foreach (char ch in name)
                    if (char.IsLetterOrDigit(ch)) { initial = char.ToUpperInvariant(ch).ToString(); break; }
            var t = UiKit.OutlineLabel(photoRt, "Initial", initial, Mathf.Round(40f * k), Color.white, TextAlignmentOptions.Center);
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
        }

        var ring = FeelUi.Img(frame, "DotRing", UiKit.Sprites.Disc, Color.white);
        UiKit.Place(ring.rectTransform, new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), new Vector2(-4f, 6f), new Vector2(34f, 34f) * k);
        var d = FeelUi.Img(frame, "Dot", UiKit.Sprites.Disc, dot);
        UiKit.Place(d.rectTransform, new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), new Vector2(-4f, 6f), new Vector2(24f, 24f) * k);

        if (envelope)
        {
            var env = FeelUi.Img(frame, "Envelope", Doodles.Envelope, Color.white);
            UiKit.Place(env.rectTransform, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-2f, -4f), new Vector2(46f, 34f) * k);
            env.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 12f);
        }
    }

    /// <summary>Seats as Dumpling-coloured discs (taken) and ink rings (free), right-aligned at <paramref name="right"/>. Returns the width.</summary>
    static float Seats(RectTransform row, int taken, int cap, float right)
    {
        const float S = 20f, Gap = 6f;
        cap = Mathf.Clamp(cap, 1, 8);
        float w = cap * S + (cap - 1) * Gap;
        var holder = FeelUi.Rect("Seats", row, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(w, S));
        var variants = ChuckPartsData.Variants;
        for (int i = 0; i < cap; i++)
        {
            bool full = i < taken;
            Color c = full
                ? (variants != null && variants.Length > 0 ? variants[i % variants.Length].playerColor : UiTheme.Tangerine)
                : UiTheme.WithAlpha(UiTheme.Ink, 0.45f);
            var img = FeelUi.Img(holder, full ? "Seat" : "Free", full ? UiKit.Sprites.Disc : UiKit.Sprites.Ring, c);
            UiKit.Place(img.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(i * (S + Gap), 0f), new Vector2(S, S));
            if (!full) continue;
            var edge = FeelUi.Img(img.rectTransform, "Edge", UiKit.Sprites.Ring, UiTheme.WithAlpha(UiTheme.Ink, 0.55f));
            edge.rectTransform.offsetMin = new Vector2(-1f, -1f);
            edge.rectTransform.offsetMax = new Vector2(1f, 1f);
        }
        return w;
    }

    /// <summary>A tilted rubber stamp ("FULL") right-aligned at <paramref name="right"/>. Returns its width.</summary>
    static float Stamp(RectTransform row, string text, float right, Color ink, float size)
    {
        var holder = FeelUi.Rect("Stamp " + text, row, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(10f, 50f));
        var t = UiKit.InkLabel(holder, "Text", text, size, ink, TextAlignmentOptions.Center, true);
        t.characterSpacing = 4f;
        float w = Mathf.Ceil(t.GetPreferredValues(text).x) + 30f;
        holder.sizeDelta = new Vector2(w, 50f);
        holder.localRotation = Quaternion.Euler(0f, 0f, -7f);
        var box = FeelUi.Img(holder, "Box", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(ink, 0.85f), 0.25f);
        box.transform.SetAsFirstSibling();
        return w;
    }

    // ------------------------------------------------------------------ empty / offline

    void BuildEmpty(string title, string body, bool sad)
    {
        EmptyTitle = title;
        float w = Mathf.Clamp(root.rect.width - 80f, 320f, 720f);
        var col = FeelUi.Rect("Column", emptyRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(w, 360f));

        var t = UiKit.Label(col, "Title", title, 40f, TextAlignmentOptions.Top, true);
        t.enableAutoSizing = true;
        t.fontSizeMax = 40f;
        t.fontSizeMin = 28f;
        var b = UiKit.Label(col, "Body", body, 24f, TextAlignmentOptions.Top, false, FontStyles.Normal, UiTheme.InkSoft);
        b.textWrappingMode = TextWrappingModes.Normal;
        float bodyH = Mathf.Ceil(b.GetPreferredValues(body, w, 0f).y) + 4f;
        bool button = useCode != null && (ShownPage == Page.Playing || SteamFriendsFeed.Off != FriendsFeedOff.None);
        float textH = 58f + bodyH + (button ? 18f + 68f : 0f);

        // the raccoon only when there's room (short pages drop it; text never shrinks below the legibility floor)
        float avail = emptyRoot.rect.height > 10f ? emptyRoot.rect.height : 600f;
        bool doodleFits = textH + 162f <= avail - 10f;
        float top = doodleFits ? 162f : 0f;
        if (doodleFits)
        {
            var doodle = FeelUi.Img(col, "Raccoon", sad ? Doodles.RaccoonSad : Doodles.RaccoonSleepy, Color.white);
            UiKit.Place(doodle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(150f, 150f));
            doodle.rectTransform.localRotation = Quaternion.Euler(0f, 0f, sad ? 5f : -6f);
            if (!sad)
            {
                for (int i = 0; i < 3; i++)
                {
                    var z = UiKit.InkLabel(doodle.rectTransform, "Z" + i, "z", 22f + i * 6f, UiTheme.InkSoft, TextAlignmentOptions.Center, true);
                    UiKit.Place(z.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(46f + i * 16f, 40f + i * 22f), new Vector2(40f, 40f));
                    zees.Add(z);
                }
            }
        }
        UiKit.Place(t.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -top), new Vector2(w, 52f));
        UiKit.Place(b.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -top - 58f), new Vector2(w, bodyH));
        float h = top + 58f + bodyH;
        if (button)
        {
            var btn = UiKit.Button(col, UiText.Friends.UseCode, () =>
            {
                try { useCode?.Invoke(); }
                catch (Exception e) { Debug.LogException(e); }
            }, UiButtonStyle.Paper);
            btn.Size(300f, 68f);
            UiKit.Place(btn.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -h - 18f), new Vector2(300f, 68f));
            var n = btn.navigation; n.mode = Navigation.Mode.Explicit; n.selectOnUp = NavUp; btn.navigation = n;
            FirstSelectable = btn;
            h += 18f + 68f;
        }
        col.sizeDelta = new Vector2(w, h);
    }

    // ------------------------------------------------------------------ actions

    void OnJoin(ulong lobby, string why)
    {
        // On the main menu: which save are you bringing (or a new one), THEN connect. In a game the join goes straight
        // through and the Town asks, as before.
        if (NetSession.MenuOpen && !NetSession.Busy && !BusinessPicker.IsShowing)
        { BusinessPicker.ShowFor(BusinessPicker.Purpose.Join, () => JoinNow(lobby, why)); return; }
        JoinNow(lobby, why);
    }

    void JoinNow(ulong lobby, string why)
    {
        if (SteamFriendsFeed.Join(lobby, why, out string whyNot))
        {
            UiTheme.Play(UiTheme.SfxConfirm, 0.5f, 1f);
            try { JoinRequested?.Invoke(lobby); }
            catch (Exception e) { Debug.LogException(e); }
            return;
        }
        UiTheme.Play(UiTheme.SfxError, 0.45f, 1f);
        errorText = whyNot ?? "Couldn't join that one.";
        errorUntil = Time.realtimeSinceStartup + 5f;
        RefreshCaption();
    }

    void OnDismiss(FriendInvite inv)
    {
        UiTheme.Play(UiTheme.SfxBack, 0.4f, 1.1f);
        SteamFriendsFeed.Dismiss(inv);
        if (!Application.isPlaying) RebuildNow();
    }

    static void DestroyNow(GameObject go)
    {
        if (go == null) return;
        go.transform.SetParent(null, false);
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    // ------------------------------------------------------------------ checks (previews and evals)

    /// <summary>
    /// Every visible text under <paramref name="root"/> against the legibility floor (18 px reference) and for overflow, the way
    /// SettingsScreen audits itself. Returns the problems ("" = none) and how many texts were checked.
    /// </summary>
    public static string AuditTexts(Transform root, out int checkedTexts)
    {
        Canvas.ForceUpdateCanvases();
        var sb = new System.Text.StringBuilder();
        checkedTexts = 0;
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
            checkedTexts++;
            t.ForceMeshUpdate();
            float used = t.fontSize;
            string path = t.transform.parent != null ? t.transform.parent.name + "/" + t.name : t.name;
            if (used < UiTheme.MinText - 0.01f) sb.Append($"small {path} {used:0.#}px; ");
            if (t.isTextOverflowing) sb.Append($"overflow {path} '{t.text}'; ");
            var r = t.rectTransform.rect;
            var pref = t.GetPreferredValues(t.text, t.textWrappingMode == TextWrappingModes.NoWrap ? 100000f : r.width, 0f);
            if (t.textWrappingMode == TextWrappingModes.NoWrap && !t.enableAutoSizing && pref.x > r.width + 1f)
                sb.Append($"wide {path} {pref.x:0}>{r.width:0} '{t.text}'; ");
            if (pref.y > r.height + 2f && !t.enableAutoSizing) sb.Append($"tall {path} {pref.y:0}>{r.height:0} '{t.text}'; ");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ invite pop-ups (§4.8)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void HookInvites()
    {
        if (Application.isBatchMode) return;
        SteamFriendsFeed.InviteReceived -= OnInvite;
        SteamFriendsFeed.InviteReceived += OnInvite;
    }

    /// <summary>
    /// The menu: UP-4's invite card ([JOIN] → <see cref="SteamFriendsFeed.Join"/>). In a game: a toast (UP-3's Messages logs
    /// it; Shift+Tab accepts in Steam). Anywhere else (a menu without the card stack): a SteamHud line.
    /// </summary>
    static void OnInvite(FriendInvite inv)
    {
        if (inv == null) return;
        ulong lobby = inv.lobbyId;
        string from = inv.fromName;
        var card = new InviteCards.Invite
        {
            from = from,
            town = string.IsNullOrEmpty(inv.town) ? UiText.Friends.ShiftOf(inv.hostName ?? from) : inv.town,
            tint = TintFor(inv.fromSteamId),
            join = () =>
            {
                void Go() { if (!SteamFriendsFeed.Join(lobby, "Invite card from " + from, out string whyNot)) SteamHud.Say(whyNot, false); }
                if (NetSession.MenuOpen && !NetSession.Busy && !BusinessPicker.IsShowing) BusinessPicker.ShowFor(BusinessPicker.Purpose.Join, Go);
                else Go();
            },
        };
        if (InviteCards.Push(card)) return; // plays Sfx notify itself
        bool inGame = PlayerController.Local != null; // coop:allow(local) HUD only: where to show the local player's invite
        if (inGame && !ToastInGame) return;
        if (inGame)
        {
            string text = UiText.Friends.InviteToast(inv);
            GameHUD.Toast(text, ToastStyle.Good, 5f);
            NotificationLog.Add(text, ToastStyle.Good, card.join); // the Esc menu's Messages: the same line, with [JOIN] (§5.6)
        }
        else SteamHud.Say(UiText.Friends.InviteMenuSay(inv), false);
        UiTheme.Play(UiTheme.SfxInvite, 0.6f, 1f);
    }

    /// <summary>A friend's stable Dumpling colour (placeholder avatars, invite card stripes).</summary>
    public static Color TintFor(ulong steamId)
    {
        var variants = ChuckPartsData.Variants;
        return variants != null && variants.Length > 0 ? variants[(int)(steamId % (ulong)variants.Length)].playerColor : UiTheme.Tangerine;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => ToastInGame = true;

    // ------------------------------------------------------------------ doodles

    /// <summary>Procedural doodles for this panel (no asset files): a raccoon (sleepy or sad) and an invite envelope.</summary>
    public static class Doodles
    {
        static Sprite sleepy, sad, envelope;

        public static Sprite RaccoonSleepy => sleepy != null ? sleepy : sleepy = Raccoon(false);
        public static Sprite RaccoonSad => sad != null ? sad : sad = Raccoon(true);
        public static Sprite Envelope => envelope != null ? envelope : envelope = MakeEnvelope();

        struct Layer
        {
            public Func<float, float, bool> inside;
            public Color32 color;
            public Layer(Color32 c, Func<float, float, bool> f) { color = c; inside = f; }
        }

        static bool Ell(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx, dy = (y - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }

        static bool Seg(float x, float y, float ax, float ay, float bx, float by, float hw)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
            float ex = px - dx * t, ey = py - dy * t;
            return ex * ex + ey * ey <= hw * hw;
        }

        static bool Tri(float x, float y, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (x - b.x) * (a.y - b.y) - (a.x - b.x) * (y - b.y);
            float d2 = (x - c.x) * (b.y - c.y) - (b.x - c.x) * (y - c.y);
            float d3 = (x - a.x) * (c.y - a.y) - (c.x - a.x) * (y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// <summary>Paint <paramref name="layers"/> (back to front) into a w×h sprite with 4×4 supersampling.</summary>
        static Sprite Paint(string name, int w, int h, List<Layer> layers)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[w * h];
            float aspect = (float)w / h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float u = ((x + (sx + 0.5f) / 4f) / w - 0.5f) * aspect, v = (y + (sy + 0.5f) / 4f) / h - 0.5f;
                            for (int i = layers.Count - 1; i >= 0; i--)
                            {
                                if (!layers[i].inside(u, v)) continue;
                                var c = layers[i].color;
                                r += c.r; g += c.g; b += c.b; a += c.a;
                                break;
                            }
                        }
                    // average over the 16 samples (misses count as clear), then back to straight alpha
                    float ai = a / 16f;
                    px[y * w + x] = ai <= 0.5f ? new Color32(0, 0, 0, 0)
                        : new Color32(B(r / 16f * 255f / ai), B(g / 16f * 255f / ai), B(b / 16f * 255f / ai), B(ai));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

        static Color32 C(int rgb, byte a = 255) => new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), a);

        static Sprite Raccoon(bool isSad)
        {
            var ink = C(0x1F1A2E);
            var fur = C(0x9A94A3);
            var furDark = C(0x6E6878);
            var cream = C(0xEDE7F0);
            var earIn = C(0xD9A9A0);
            var white = C(0xFFFFFF);
            var tear = C(0x6EC5E9);
            const float O = 0.022f; // ink outline width
            var L = new List<Layer>
            {
                // outlines first (slightly bigger ink copies), then fills
                new Layer(ink, (x, y) => Tri(x, y, new Vector2(-0.36f, 0.08f), new Vector2(-0.30f, 0.40f + O), new Vector2(-0.08f, 0.22f))
                                         || Tri(x, y, new Vector2(0.36f, 0.08f), new Vector2(0.30f, 0.40f + O), new Vector2(0.08f, 0.22f))),
                new Layer(furDark, (x, y) => Tri(x, y, new Vector2(-0.33f, 0.10f), new Vector2(-0.29f, 0.37f), new Vector2(-0.11f, 0.22f))
                                             || Tri(x, y, new Vector2(0.33f, 0.10f), new Vector2(0.29f, 0.37f), new Vector2(0.11f, 0.22f))),
                new Layer(earIn, (x, y) => Tri(x, y, new Vector2(-0.29f, 0.16f), new Vector2(-0.27f, 0.31f), new Vector2(-0.17f, 0.22f))
                                           || Tri(x, y, new Vector2(0.29f, 0.16f), new Vector2(0.27f, 0.31f), new Vector2(0.17f, 0.22f))),
                new Layer(ink, (x, y) => Ell(x, y, 0f, -0.02f, 0.40f + O, 0.30f + O)),
                new Layer(fur, (x, y) => Ell(x, y, 0f, -0.02f, 0.40f, 0.30f)),
                new Layer(cream, (x, y) => Ell(x, y, 0f, -0.15f, 0.30f, 0.15f) && y < -0.02f),
                new Layer(furDark, (x, y) => Ell(x, y, 0f, 0.17f, 0.045f, 0.11f)),
                // the bandit mask
                new Layer(ink, (x, y) => Ell(x, y, -0.15f, 0.03f, 0.15f, 0.085f) || Ell(x, y, 0.15f, 0.03f, 0.15f, 0.085f)
                                         || (Mathf.Abs(x) < 0.08f && Mathf.Abs(y - 0.05f) < 0.035f)),
            };
            if (isSad)
            {
                // big wet eyes looking up, droopy lids, a tear, a wobbly frown
                L.Add(new Layer(white, (x, y) => Ell(x, y, -0.14f, 0.035f, 0.06f, 0.06f) || Ell(x, y, 0.14f, 0.035f, 0.06f, 0.06f)));
                L.Add(new Layer(ink, (x, y) => Ell(x, y, -0.13f, 0.05f, 0.032f, 0.034f) || Ell(x, y, 0.15f, 0.05f, 0.032f, 0.034f)));
                L.Add(new Layer(white, (x, y) => Ell(x, y, -0.12f, 0.065f, 0.011f, 0.011f) || Ell(x, y, 0.16f, 0.065f, 0.011f, 0.011f)));
                L.Add(new Layer(ink, (x, y) => (Seg(x, y, -0.21f, 0.11f, -0.08f, 0.075f, 0.012f)) || Seg(x, y, 0.21f, 0.11f, 0.08f, 0.075f, 0.012f)));
                L.Add(new Layer(tear, (x, y) => Ell(x, y, 0.20f, -0.06f, 0.025f, 0.038f) || Tri(x, y, new Vector2(0.178f, -0.05f), new Vector2(0.222f, -0.05f), new Vector2(0.20f, -0.005f))));
                L.Add(new Layer(ink, (x, y) => Ell(x, y, 0f, -0.075f, 0.05f, 0.034f)));
                L.Add(new Layer(ink, (x, y) => Seg(x, y, -0.06f, -0.185f, 0f, -0.15f, 0.011f) || Seg(x, y, 0f, -0.15f, 0.06f, -0.185f, 0.011f)));
            }
            else
            {
                // asleep: closed-eye arcs, a little smile
                L.Add(new Layer(white, (x, y) => Seg(x, y, -0.20f, 0.04f, -0.14f, 0.015f, 0.012f) || Seg(x, y, -0.14f, 0.015f, -0.08f, 0.04f, 0.012f)
                                                 || Seg(x, y, 0.08f, 0.04f, 0.14f, 0.015f, 0.012f) || Seg(x, y, 0.14f, 0.015f, 0.20f, 0.04f, 0.012f)));
                L.Add(new Layer(ink, (x, y) => Ell(x, y, 0f, -0.075f, 0.05f, 0.034f)));
                L.Add(new Layer(ink, (x, y) => Seg(x, y, -0.05f, -0.15f, 0f, -0.17f, 0.011f) || Seg(x, y, 0f, -0.17f, 0.05f, -0.15f, 0.011f)));
                L.Add(new Layer(C(0xF4C4C4), (x, y) => Ell(x, y, -0.24f, -0.07f, 0.045f, 0.025f) || Ell(x, y, 0.24f, -0.07f, 0.045f, 0.025f)));
            }
            return Paint(isSad ? "FriendsRaccoonSad" : "FriendsRaccoonSleepy", 160, 160, L);
        }

        static Sprite MakeEnvelope()
        {
            var ink = C(0x1F1A2E);
            var paper = C(0xFFF5DB);
            var red = C(0xDB1F29);
            const float hw = 0.62f, hh = 0.34f, O = 0.035f; // aspect 4:3 → x spans ±0.667
            var L = new List<Layer>
            {
                new Layer(ink, (x, y) => Mathf.Abs(x) <= hw + O && Mathf.Abs(y) <= hh + O),
                new Layer(paper, (x, y) => Mathf.Abs(x) <= hw && Mathf.Abs(y) <= hh),
                new Layer(ink, (x, y) => Seg(x, y, -hw, hh, 0f, -0.02f, 0.022f) || Seg(x, y, 0f, -0.02f, hw, hh, 0.022f)),
                new Layer(red, (x, y) => Ell(x, y, -0.05f, 0.02f, 0.09f, 0.09f) || Ell(x, y, 0.05f, 0.02f, 0.09f, 0.09f)
                                         || Tri(x, y, new Vector2(-0.135f, -0.01f), new Vector2(0.135f, -0.01f), new Vector2(0f, -0.17f))),
            };
            return Paint("FriendsEnvelope", 96, 72, L);
        }
    }
}

/// <summary>
/// FRIENDS PLAYING | INVITES | CODE folder tabs for a Join page that already has a code entry (UP-4's JoinScreen, §4.4): builds
/// the tabs and a <see cref="FriendsPanel"/> page under them, and shows the page's own code parts only on CODE. Wiring is a few
/// lines in the host: build the code parts lower by <see cref="StripHeight"/>, then
/// <c>FriendsTabs.Build(panel, top, bottom, side, new[] { plate.gameObject, hint.gameObject, joinB.gameObject }, field)</c>.
/// Opens on INVITES when there are invites, FRIENDS PLAYING when Steam is up, else CODE.
/// </summary>
public sealed class FriendsTabs : MonoBehaviour
{
    /// <summary>Tab strip height (reference px): move the code parts down by this much.</summary>
    public const float StripHeight = 78f;
    public const int CodeIndex = 2;

    public UiTabs Tabs { get; private set; }
    public FriendsPanel Panel { get; private set; }
    public int Index => Tabs != null ? Tabs.Index : CodeIndex;

    /// <summary>The tab changed (index: 0 friends, 1 invites, 2 code).</summary>
    public event Action<int> Changed;

    GameObject[] codeParts;
    Selectable codeFirst;
    RectTransform pageBg;

    /// <summary>
    /// Build under <paramref name="card"/>: the strip at <paramref name="top"/> px below its top edge, the friends page down to
    /// <paramref name="bottom"/> px above its bottom, <paramref name="side"/> px from each side. <paramref name="siblingIndex"/>
    /// puts the tabs and page just above the card's own background (e.g. 3 = after Shadow, Paper, Edge) so the code parts draw
    /// on top of the page; -1 = on top of everything.
    /// </summary>
    public static FriendsTabs Build(RectTransform card, float top, float bottom, float side, IReadOnlyList<GameObject> codeParts,
                                    Selectable codeFirst, int siblingIndex = -1)
    {
        var holder = FeelUi.Stretch("FriendsTabs", card);
        if (siblingIndex >= 0) holder.SetSiblingIndex(Mathf.Min(siblingIndex, card.childCount - 1));
        var t = holder.gameObject.AddComponent<FriendsTabs>();
        t.codeParts = codeParts != null ? new List<GameObject>(codeParts).ToArray() : Array.Empty<GameObject>();
        t.codeFirst = codeFirst;
        float w = card.rect.width > 10f ? card.rect.width : card.sizeDelta.x;

        t.Tabs = UiKit.Tabs(holder, new[] { FriendsPanel.TabLabel(FriendsPanel.Page.Playing), FriendsPanel.TabLabel(FriendsPanel.Page.Invites),
                                            UiText.Friends.TabCode }, t.Select);
        UiKit.Place((RectTransform)t.Tabs.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(side, -top), new Vector2(w - side * 2f, 70f));
        t.Tabs.FitTo(w - side * 2f);

        t.pageBg = FeelUi.Rect("Page", holder, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        UiKit.Inset(t.pageBg, side - 10f, bottom, side - 10f, top + 64f);
        FeelUi.Img(t.pageBg, "Bg", FeelUi.Rounded, UiTheme.Paper, 0.3f);
        FeelUi.Img(t.pageBg, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.2f), 0.3f);
        var inner = FeelUi.Stretch("Inner", t.pageBg);
        UiKit.Inset(inner, 10f, 8f, 6f, 6f);
        t.Panel = FriendsPanel.Create(inner, FriendsPanel.Page.Playing, () => t.Select(CodeIndex));
        t.Panel.Rebuilt += t.RefreshLabels;
        t.Select(DefaultIndex);
        return t;
    }

    /// <summary>Invites waiting → INVITES; Steam up → FRIENDS PLAYING; else CODE.</summary>
    public static int DefaultIndex =>
        SteamFriendsFeed.Available && SteamFriendsFeed.Invites.Count > 0 ? 1 : SteamFriendsFeed.Available ? 0 : CodeIndex;

    /// <summary>Show tab <paramref name="i"/> (0 friends playing, 1 invites, 2 code).</summary>
    public void Select(int i)
    {
        i = Mathf.Clamp(i, 0, CodeIndex);
        if (Tabs.Index != i) Tabs.Select(i, false);
        bool code = i == CodeIndex;
        Panel.gameObject.SetActive(!code);
        Panel.NavUp = Tabs.Buttons[i];
        if (!code) Panel.Show((FriendsPanel.Page)i);
        foreach (var go in codeParts) if (go != null) go.SetActive(code);
        RefreshLabels();
        try { Changed?.Invoke(i); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>What to focus when the page opens (the first JOIN, or the code field).</summary>
    public Selectable FirstSelectable => Index == CodeIndex ? codeFirst : Panel.FirstSelectable != null ? Panel.FirstSelectable : codeFirst;

    void OnEnable() => SteamFriendsFeed.Changed += RefreshLabels;
    void OnDisable() => SteamFriendsFeed.Changed -= RefreshLabels;

    void RefreshLabels()
    {
        if (Tabs == null) return;
        var b = Tabs.Buttons;
        if (b.Count > 0) b[0].SetLabel(FriendsPanel.TabLabel(FriendsPanel.Page.Playing));
        if (b.Count > 1) b[1].SetLabel(FriendsPanel.TabLabel(FriendsPanel.Page.Invites));
        var down = Index == CodeIndex ? codeFirst : Panel != null && Panel.FirstSelectable != null ? Panel.FirstSelectable : codeFirst;
        Tabs.SetDown(down);
        if (Panel != null && b.Count > 0) Panel.NavUp = b[Mathf.Clamp(Tabs.Index, 0, b.Count - 1)];
    }
}

/// <summary>
/// A ready-made "JOIN A FRIEND" sheet around <see cref="FriendsPanel"/> (paper, FRIENDS PLAYING | INVITES tabs, Back): the
/// preview chrome for screenshots, and a fallback screen until UP-4's JoinScreen (which adds the CODE tab) hosts the panel.
/// </summary>
public sealed class FriendsSheet : UiScreen
{
    static FriendsSheet instance;

    public FriendsPanel Panel { get; private set; }
    public UiTabs Tabs { get; private set; }

    Canvas canvas;
    RectTransform sheet;
    UiButton back;
    bool preview;

    static readonly Vector2 SheetSize = new Vector2(1240f, 820f);
    static readonly Color Manila = UiTheme.Hex(0xE3C98F), ManilaInk = UiTheme.Hex(0x5E4B2A);

    /// <summary>Open the sheet (menus: no gameplay lock; in a game it locks input like any menu).</summary>
    public static FriendsSheet Show(FriendsPanel.Page page = FriendsPanel.Page.Playing, Action useCode = null)
    {
        if (instance != null) { instance.Panel.Show(page); instance.Tabs.Select((int)page, false); return instance; }
        var c = UiKit.Canvas("[FriendsSheet]", UiLayers.Menu + 10);
        var s = c.gameObject.AddComponent<FriendsSheet>();
        s.canvas = c;
        s.Build(UiScale.ReferenceSize(Screen.width, Screen.height), page, useCode);
        instance = s;
        s.Open(lockGameplay: true);
        return s;
    }

    /// <summary>Edit-mode preview under <paramref name="parent"/>, drawn through <paramref name="cam"/> (never opened).</summary>
    public static FriendsSheet CreatePreview(Transform parent, Camera cam, int w, int h, FriendsPanel.Page page, float uiScale = -1f)
    {
        var c = UiKit.Canvas("[FriendsSheet Preview]", UiLayers.Menu + 10, parent);
        UiKit.PreviewCanvas(c, cam, w, h, uiScale);
        var s = c.gameObject.AddComponent<FriendsSheet>();
        s.canvas = c;
        s.preview = true;
        s.Build(UiScale.ReferenceSize(w, h, uiScale), page, null);
        return s;
    }

    void Build(Vector2 refSize, FriendsPanel.Page page, Action useCode)
    {
        var root = (RectTransform)transform;
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.5f));
        dim.raycastTarget = true;

        float W = Mathf.Min(SheetSize.x, refSize.x - 48f), H = Mathf.Min(SheetSize.y, refSize.y - 40f);
        sheet = UiKit.Paper(root, new Vector2(W, H));
        sheet.localRotation = Quaternion.Euler(0f, 0f, 0.35f);
        // a manila folder like the settings screen: the page inside is the paper
        var body = sheet.Find("Body")?.GetComponent<Image>();
        if (body != null) { body.color = Manila; body.raycastTarget = true; }
        FeelUi.Img(sheet, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.35f);
        var clip = FeelUi.Img(sheet, "Clip", UiKit.Sprites.Paperclip, UiTheme.Hex(0xA7AFB8));
        UiKit.Place(clip.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(64f, 4f), new Vector2(46f, 92f));
        clip.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 8f);

        var title = UiKit.InkLabel(sheet, "Title", UiText.Friends.SheetTitle, UiTheme.Heading, UiTheme.Ink, TextAlignmentOptions.MidlineLeft);
        title.characterSpacing = 4f;
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -16f), new Vector2(Mathf.Min(560f, W - 380f), 70f));
        title.enableAutoSizing = true;
        title.fontSizeMax = UiTheme.Heading;
        title.fontSizeMin = 34f;

        var copy = UiKit.InkLabel(sheet, "Copy", UiText.Friends.CrewCopy, UiTheme.Small, ManilaInk, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        float copyW = W - 150f;
        UiKit.Place(copy.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -84f), new Vector2(copyW, 30f));
        copy.text = FeelUi.Fit(copy, UiText.Friends.CrewCopy, copyW);

        // a Head Office rubber stamp: Steam lobbies are friends-only (MP §11 decision 10)
        var stamp = FeelUi.Rect("FriendsOnly", sheet, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -22f), new Vector2(260f, 64f));
        stamp.localRotation = Quaternion.Euler(0f, 0f, 6f);
        var st = UiKit.InkLabel(stamp, "Text", UiText.Friends.FriendsOnlyStamp, 30f, UiTheme.StampRed, TextAlignmentOptions.Center, true);
        st.characterSpacing = 5f;
        float sw = Mathf.Ceil(st.GetPreferredValues(UiText.Friends.FriendsOnlyStamp).x) + 36f;
        stamp.sizeDelta = new Vector2(sw, 64f);
        var box = FeelUi.Img(stamp, "Box", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.StampRed, 0.9f), 0.3f);
        box.transform.SetAsFirstSibling();
        // only when it clears both the title and the copy line under it (narrow folders: 5:4, big UI scales)
        float stampLeft = W - 40f - sw;
        float titleRight = 110f + title.GetPreferredValues(UiText.Friends.SheetTitle).x + 30f;
        float copyRight = 110f + copy.GetPreferredValues(copy.text).x + 20f;
        stamp.gameObject.SetActive(stampLeft > titleRight && stampLeft > copyRight);

        // folder-style tabs over a slightly darker page
        Tabs = UiKit.Tabs(sheet, new[] { FriendsPanel.TabLabel(FriendsPanel.Page.Playing), FriendsPanel.TabLabel(FriendsPanel.Page.Invites) },
                          i =>
                          {
                              Panel.NavUp = Tabs.Buttons[i]; // Up from the first row goes back to the tab you're on
                              Panel.Show((FriendsPanel.Page)i);
                          });
        UiKit.Place((RectTransform)Tabs.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -122f), new Vector2(W - 88f, 70f));
        Tabs.FitTo(W - 88f);

        var pageRect = FeelUi.Rect("Page", sheet, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        UiKit.Inset(pageRect, 36f, 110f, 36f, 190f);
        var pageShadow = FeelUi.Img(pageRect, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.18f), 0.3f);
        pageShadow.rectTransform.offsetMin = pageShadow.rectTransform.offsetMax = new Vector2(4f, -5f);
        FeelUi.Img(pageRect, "Bg", FeelUi.Rounded, UiTheme.Paper, 0.3f);
        FeelUi.Img(pageRect, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.18f), 0.3f);
        var inner = FeelUi.Stretch("Inner", pageRect);
        UiKit.Inset(inner, 16f, 12f, 12f, 8f);
        Canvas.ForceUpdateCanvases();
        Panel = FriendsPanel.Create(inner, page: FriendsPanel.Page.Playing, useCode);
        Panel.Rebuilt += RefreshTabs;
        Panel.JoinRequested += _ => { if (!preview) Close(); }; // the router's busy card takes over
        if (page != FriendsPanel.Page.Playing) { Tabs.Select((int)page, false); Panel.Show(page); }

        back = UiKit.Button(sheet, UiText.Back, () => { if (!preview) Close(); }, UiButtonStyle.Primary);
        back.Size(260f, 76f);
        UiKit.Place(back.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 22f), new Vector2(260f, 76f));
        var esc = UiKit.KeyCap(sheet, "Esc");
        UiKit.Place(esc, new Vector2(0f, 0f), new Vector2(0f, 0.5f), new Vector2(48f, 60f), new Vector2(0f, 44f));
        var escT = UiKit.Label(sheet, "EscHint", UiText.Back, UiTheme.Small, TextAlignmentOptions.MidlineLeft, false, FontStyles.Bold, ManilaInk);
        UiKit.Place(escT.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0.5f), new Vector2(112f, 60f), new Vector2(160f, 44f));

        Tabs.SetDown(Panel.FirstSelectable != null ? Panel.FirstSelectable : back);
        Panel.NavUp = Tabs.Buttons.Count > 0 ? Tabs.Buttons[Tabs.Index] : null;
        UiPop.On(sheet.gameObject).Play(true);
    }

    void RefreshTabs()
    {
        if (Tabs == null || Panel == null) return;
        var b = Tabs.Buttons;
        if (b.Count > 0) b[0].SetLabel(FriendsPanel.TabLabel(FriendsPanel.Page.Playing));
        if (b.Count > 1) b[1].SetLabel(FriendsPanel.TabLabel(FriendsPanel.Page.Invites));
        Tabs.SetDown(Panel.FirstSelectable != null ? Panel.FirstSelectable : back);
        if (b.Count > 0) Panel.NavUp = b[Mathf.Clamp(Tabs.Index, 0, b.Count - 1)];
    }

    void OnEnable() => UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    void OnDisable() => UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;

    /// <summary>The canvas survives scene loads (UiKit.Canvas): a scene change (joining, leaving) closes the sheet.</summary>
    void OnSceneChanged(UnityEngine.SceneManagement.Scene a, UnityEngine.SceneManagement.Scene b)
    {
        if (preview) return;
        if (IsOpen) Close();
        else if (canvas != null) Destroy(gameObject);
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f);
        var first = Panel != null && Panel.FirstSelectable != null ? Panel.FirstSelectable.gameObject : back != null ? back.gameObject : null;
        UiKit.Focus(first);
    }

    public override void OnHide()
    {
        UiTheme.Play(UiTheme.SfxBack, 0.4f, 1f);
        if (instance == this) instance = null;
        if (preview || canvas == null) return;
        UiPop.On(sheet.gameObject).Play(false, () => { if (this != null) Destroy(gameObject); });
    }

    /// <summary>The audit of every text on the sheet (see <see cref="FriendsPanel.AuditTexts"/>).</summary>
    public string Audit(out int n) => FriendsPanel.AuditTexts(transform, out n);

    protected override void OnDestroy()
    {
        if (instance == this) instance = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}

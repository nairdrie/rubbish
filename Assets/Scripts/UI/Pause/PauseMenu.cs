using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using PT = UiText.Pause;

/// <summary>Which Esc menu you get (Docs/UIPass.md §5.2).</summary>
public enum PauseVariant
{
    /// <summary>Offline, or hosting solo (nobody can join): the world freezes, the stamp says PAUSED.</summary>
    Solo,
    /// <summary>Hosting friends (even alone): the world keeps going, ON BREAK.</summary>
    Host,
    /// <summary>On someone else's shift: ON BREAK.</summary>
    Guest,
}

/// <summary>
/// The Esc menu (Docs/UIPass.md §5): a Rubbish Co. <b>timesheet on a clipboard</b>. Left column: Resume, Open shop / Invite
/// friends, Settings, Unstuck, Messages, Leave (worded per role) and Quit. Right column: folder tabs with TODAY'S SHIFT
/// (<see cref="ShiftCardView"/>), and in co-op the CREW (<see cref="CrewPanel"/>), the TOWN RULES (<see cref="TownRulesCard"/>)
/// and the INVITE plate, plus MESSAGES (<see cref="NotificationLog"/>). A rubber stamp says <b>PAUSED</b> in solo (the world
/// freezes through <see cref="PauseService"/>) and <b>ON BREAK</b> in co-op (it never does).
/// <para>Opened by <see cref="UiStack"/>'s Esc router (<see cref="UiStack.PauseMenuOpener"/>), so Esc never fights the laptop,
/// the sort station, the full map or the chess board (seated, Esc stands you up instead). Esc again resumes; Settings opens on
/// top and Esc comes back here. It replaces B1's minimal M0 Esc overlay (<c>SessionPauseMenu</c>, retired and deleted), so
/// there's exactly one Esc menu.</para>
/// Edit-mode previews (<see cref="CreatePreview"/>) use fixed sample data and never touch the game's state.
/// </summary>
public sealed class PauseMenu : UiScreen
{
    /// <summary>The clipboard's design size (reference px). It scales down uniformly when the reference is smaller.</summary>
    public static readonly Vector2 BoardSize = new Vector2(1320f, 900f);
    const float PaperL = 28f, PaperR = 28f, PaperB = 24f, PaperT = 56f;
    const float ColLeftX = 36f, ColLeftW = 420f, ColRightX = 492f, ColRightPad = 36f;
    const float ButtonH = 66f, ButtonGap = 10f;
    float leftButtonHeight = ButtonH;

    enum Page { Shift, Crew, Rules, Invite, Messages }

    static PauseMenu instance;
    static int noteIndex;

    /// <summary>The pause menu is open (HUD layout reads this: solo hides the HUD, co-op keeps it under the dim).</summary>
    public static new bool IsOpen => instance != null && UiStack.Contains(instance);

    /// <summary>The Esc router opens this menu (set once at boot, play mode only).</summary>
    public static bool Installed { get; private set; }

    /// <summary>The variant of the open (or last) menu.</summary>
    public static PauseVariant Variant => instance != null ? instance.variant : PauseVariant.Solo;

    /// <summary>The live menu (null until first opened).</summary>
    public static PauseMenu Instance => instance;

    /// <summary>Tests only: thrown inside <see cref="Resume"/> (the freeze must still come undone).</summary>
    public static Action TestFault;

    /// <summary>M1 hook: the Town's name for the header (SaveSlots); null = <see cref="UiText.Pause.DefaultTownName"/>.</summary>
    public static Func<string> TownNameSource;

    Canvas canvas;
    RectTransform root, pop, board, paper, pageRoot, pageContent, stampRt;
    TextMeshProUGUI subline, stamp, plateText, inviteStatus;
    UiTabs tabs;
    UiButton resumeB, settingsB, controlsB, unstuckB, messagesB, showB;
    ShiftCardView shiftView;
    CrewPanel crewView;
    TownRulesCardView rulesView;
    readonly List<Page> tabPages = new List<Page>();
    readonly List<UiButton> leftButtons = new List<UiButton>();
    readonly List<Selectable> pageSelectables = new List<Selectable>();
    Page page = Page.Shift;
    PauseVariant variant;
    bool preview, codeRevealed, stampSounded;
    float refreshAt, stampT = 99f;
    Vector2 builtFor;
    int messagesShown = -1;

    // ------------------------------------------------------------------ open / close

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        UiStack.PauseMenuOpener = TryOpen;
        Installed = true;
        // Streamer mode (§13 Q7, on by default): a fresh invite code starts hidden on the HUD too, until Show.
        NetSession.JoinCodeReady += code =>
        {
            if (GameSettings.StreamerMode) JoinCodeUI.HiddenByPlayer = true;
        };
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        Installed = false;
        TestFault = null;
        TownNameSource = null;
    }

    /// <summary>The Esc router's step 3: open over the game (false = nothing opened).</summary>
    public static bool TryOpen()
    {
        var me = PauseService.Me;
        if (me == null || NetSession.MenuOpen) return false;
        // Seated at the chess board, Esc stands you up (like the laptop and the sort station, chess gets Esc first). A pause
        // over the board would also re-lock the cursor the board needs when it closed.
        if (StandUpFromChess(me)) return true;
        var s = NetSession.State;
        if (s == NetSessionState.Preparing || s == NetSessionState.Joining || s == NetSessionState.Leaving) return false;
        if (instance == null)
        {
            var c = UiKit.Canvas("[Pause Menu]", UiLayers.Pause);
            instance = c.gameObject.AddComponent<PauseMenu>();
            instance.canvas = c;
        }
        instance.Show();
        return true;
    }

    /// <summary>Esc while seated at the chess board: leave the board (the same as its E "Leave Game"). False when not seated.</summary>
    static bool StandUpFromChess(PlayerController me)
    {
        var depot = me.currentDepotChessBoard;
        if (depot != null && me.IsPlayingChess)
        {
            try { depot.OnInteract(me.gameObject); }
            catch (Exception e) { Debug.LogException(e); }
            UiTheme.Play(UiTheme.SfxBack, 0.4f, 1.1f);
            return true;
        }
        var chess = me.currentChessController;
        if (chess == null || !me.IsPlayingChess) return false;
        try { chess.OnInteract(me.gameObject); }
        catch (Exception e) { Debug.LogException(e); }
        UiTheme.Play(UiTheme.SfxBack, 0.4f, 1.1f);
        return true;
    }

    void Show()
    {
        variant = DetectVariant();
        noteIndex++;
        page = Page.Shift;
        // the HUD line and the plate always agree: hidden = this run's Show/Hide choice, else Streamer mode (on by default)
        codeRevealed = !JoinCodeUI.HiddenByPlayer;
        Build(UiScale.ReferenceSize(Screen.width, Screen.height));
        canvas.enabled = true;
        Open(true);
        if (variant == PauseVariant.Solo && !PauseService.Freeze())
            Debug.LogWarning("[PauseMenu] Solo menu opened but the world couldn't freeze.");
    }

    static PauseVariant DetectVariant()
    {
        if (PauseService.CanFreeze) return PauseVariant.Solo;
        var nm = NetBoot.Manager;
        if (NetSession.IsHost || (nm != null && nm.IsListening && nm.IsServer)) return PauseVariant.Host;
        return PauseVariant.Guest;
    }

    /// <summary>Close the menu and get back to work (unfreezes in solo).</summary>
    void QuitJob()
    {
        if (!Payroll.TryGetLocalJob(out var job)) return;
        UiKit.Confirm("Quit your job?", $"Stop working for {Payroll.BusinessLabelOf(job.employerClientId)}? You stay in this Town and keep your own business. Fixed wages settle the part hour; paid earnings shares stay yours.",
            "Quit Job", "Keep working", yes => { if (yes) { Payroll.Quit(); Resume(); } });
    }

    public void Resume()
    {
        try { TestFault?.Invoke(); }
        finally { Close(); }
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.95f);
        if (pop != null) UiPop.On(pop.gameObject).Play(true);
        stampT = 0f;
        stampSounded = false;
        refreshAt = 0f;
        if (resumeB != null) UiKit.Focus(resumeB.gameObject);
    }

    public override void OnHide()
    {
        try
        {
            UiTheme.Play(UiTheme.SfxBack, 0.4f, 1.1f);
            if (pop != null && Application.isPlaying && canvas != null)
                UiPop.On(pop.gameObject).Play(false, () => { if (canvas != null && !IsOpen) canvas.enabled = false; });
            else if (canvas != null) canvas.enabled = false;
        }
        finally { PauseService.Unfreeze("resume"); }
    }

    public override bool OnBack()
    {
        Resume();
        return true;
    }

    // ------------------------------------------------------------------ preview

    /// <summary>
    /// Edit-mode preview for screenshots: built under <paramref name="parent"/>, drawn through <paramref name="cam"/> at the
    /// scale a <paramref name="w"/>×<paramref name="h"/> screen would use, with sample data. Never opened (no stack, no freeze).
    /// <paramref name="tab"/>: 0 shift, 1 crew, 2 rules, 3 invite, 4 messages (solo has only shift and messages).
    /// </summary>
    public static PauseMenu CreatePreview(Transform parent, Camera cam, int w, int h, PauseVariant v, int tab = 0, float uiScale = -1f,
                                          bool codeShown = false)
    {
        var c = UiKit.Canvas("[Pause Preview]", UiLayers.Pause, parent);
        var m = c.gameObject.AddComponent<PauseMenu>();
        m.canvas = c;
        m.preview = true;
        m.variant = v;
        m.codeRevealed = codeShown;
        m.page = (Page)Mathf.Clamp(tab, 0, 4);
        if (v == PauseVariant.Solo && m.page != Page.Shift) m.page = Page.Messages;
        UiKit.PreviewCanvas(c, cam, w, h, uiScale);
        m.Build(UiScale.ReferenceSize(w, h, uiScale));
        m.stampT = 99f;
        m.ApplyStamp();
        m.resumeB.PreviewFocus(true);
        return m;
    }

    // ------------------------------------------------------------------ build

    void Build(Vector2 refSize)
    {
        builtFor = refSize;
        root = (RectTransform)transform;
        for (int i = root.childCount - 1; i >= 0; i--) PauseUi.DestroyNow(root.GetChild(i).gameObject);
        leftButtons.Clear();
        shiftView = null;
        crewView = null;
        bool coop = variant != PauseVariant.Solo;

        // the world behind: solo dims more (it's frozen); co-op dims a little so you can see it still moving
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, coop ? 0.25f : 0.55f));
        dim.raycastTarget = true;

        float k = Mathf.Min(1f, (refSize.x - 32f) / BoardSize.x, (refSize.y - 24f) / BoardSize.y);
        pop = FeelUi.Stretch("Pop", root); // UiPop animates this; the board keeps its fit scale
        board = FeelUi.Rect("Clipboard", pop, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -8f * k), BoardSize);
        board.localScale = Vector3.one * k;
        board.localRotation = Quaternion.Euler(0f, 0f, -0.5f);
        var sh = FeelUi.Img(board, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.5f), 0.35f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(12f, -16f);
        var wood = FeelUi.Img(board, "Board", FeelUi.Rounded, UiTheme.Hex(0xB8834F), 0.35f);
        wood.raycastTarget = true;
        var grain = FeelUi.Img(board, "Grain", UiKit.Sprites.Stripes, UiTheme.Hex(0x8A5E33, 0.07f));
        grain.type = Image.Type.Tiled;
        grain.pixelsPerUnitMultiplier = 0.5f;
        UiKit.Inset(grain.rectTransform, 6f, 6f, 6f, 6f);
        FeelUi.Img(board, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.Hex(0x6B4524, 0.8f), 0.35f);

        paper = FeelUi.Rect("Paper", board, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        UiKit.Inset(paper, PaperL, PaperB, PaperR, PaperT);
        var psh = FeelUi.Img(paper, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.22f), 0.15f);
        psh.rectTransform.offsetMin = psh.rectTransform.offsetMax = new Vector2(4f, -5f);
        FeelUi.Img(paper, "Sheet", FeelUi.Rounded, UiTheme.Hex(0xFFF8E6), 0.15f);
        float paperW = BoardSize.x - PaperL - PaperR, paperH = BoardSize.y - PaperB - PaperT;

        // a coffee ring behind the title (Head Office drinks a lot of coffee)
        var ring = FeelUi.Img(paper, "Coffee", UiKit.Sprites.CoffeeRing, UiTheme.Hex(0x7A4A22, 0.28f));
        UiKit.Place(ring.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(84f, -50f), new Vector2(140f, 140f));
        ring.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 30f);

        // header
        var title = UiKit.InkLabel(paper, "Title", PT.TimesheetTitle, 50f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft, true);
        title.characterSpacing = 3f;
        title.enableAutoSizing = true;
        title.fontSizeMax = 50f;
        title.fontSizeMin = 32f;
        PauseUi.TopLeft(title.rectTransform, ColLeftX, 22f, 760f, 62f);
        subline = UiKit.InkLabel(paper, "Subline", "", 24f, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        PauseUi.TopLeft(subline.rectTransform, ColLeftX + 2f, 86f, 820f, 32f);

        // the rubber stamp
        // co-op: a little flatter and higher, so it clears the hazard tape under the header
        stampRt = FeelUi.Rect("Stamp", paper, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-196f, coop ? -66f : -74f),
                              coop ? new Vector2(330f, 92f) : new Vector2(330f, 100f));
        stampRt.localRotation = Quaternion.Euler(0f, 0f, coop ? -6f : -9f);
        FeelUi.Img(stampRt, "Frame", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.StampRed, 0.85f), 0.35f);
        var inner = FeelUi.Img(stampRt, "Inner", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.StampRed, 0.6f), 0.3f);
        UiKit.Inset(inner.rectTransform, 9f, 9f, 9f, 9f);
        stamp = UiKit.InkLabel(stampRt, "Text", coop ? PT.StampOnBreak : PT.StampPaused, 60f, UiTheme.WithAlpha(UiTheme.StampRed, 0.92f),
                               TextAlignmentOptions.Center, true);
        stamp.characterSpacing = 6f;
        stamp.enableAutoSizing = true;
        stamp.fontSizeMax = 60f;
        stamp.fontSizeMin = 36f;
        UiKit.Inset(stamp.rectTransform, 18f, 6f, 18f, 6f);

        float bodyTop = 136f;
        if (coop)
        {
            // hazard tape across the sheet: the world keeps going
            var tape = FeelUi.Rect("BreakTape", paper, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -158f), new Vector2(paperW - 40f, 44f));
            tape.localRotation = Quaternion.Euler(0f, 0f, 0.6f);
            FeelUi.Img(tape, "Strip", UiKit.Sprites.Tape, UiTheme.Hex(0xFFE873, 0.95f));
            var stripes = FeelUi.Img(tape, "Hazard", UiKit.Sprites.Stripes, UiTheme.WithAlpha(Color.white, 0.55f));
            stripes.type = Image.Type.Tiled;
            stripes.pixelsPerUnitMultiplier = 2f;
            UiKit.Place(stripes.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(40f, 30f));
            var stripes2 = FeelUi.Img(tape, "Hazard2", UiKit.Sprites.Stripes, UiTheme.WithAlpha(Color.white, 0.55f));
            stripes2.type = Image.Type.Tiled;
            stripes2.pixelsPerUnitMultiplier = 2f;
            UiKit.Place(stripes2.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10f, 0f), new Vector2(40f, 30f));
            var tt = UiKit.InkLabel(tape, "Text", PT.WorldKeepsGoing, 22f, UiTheme.Ink, TextAlignmentOptions.Center, true);
            UiKit.Inset(tt.rectTransform, 60f, 0f, 60f, 0f);
            bodyTop = 186f;
        }
        else PauseUi.Rule(paper, 128f, paperW - 2f * ColLeftX, ColLeftX);

        BuildLeft(bodyTop, paperH);
        BuildRight(bodyTop, paperW, paperH);
        BuildDoodle(paperH);

        // the clip on top of everything on the board
        BuildClip();
        BuildSticky(refSize, k);
        RefreshLive(true);
    }

    void BuildLeft(float bodyTop, float paperH)
    {
        float y = bodyTop + 12f;
        bool hasJob = variant != PauseVariant.Solo && !preview && Payroll.TryGetLocalJob(out _);
        int rows = hasJob ? 8 : 7; // Controls / Messages occupy one row.
        // Leave the supervisor signature its own footer band, including tilted-button shadows/hover growth.
        // Design-space sizing scales with the clipboard at 1080p, 768p and other supported resolutions.
        leftButtonHeight = Mathf.Min(ButtonH, (paperH - 104f - y - (rows - 1) * ButtonGap) / rows);
        resumeB = LeftButton(PT.Resume, Resume, UiButtonStyle.Primary, ref y);
        if (variant == PauseVariant.Solo) LeftButton(PT.OpenShop, AskOpenShop, UiButtonStyle.Sticker, ref y);
        else LeftButton(PT.InviteFriends, () => SelectPage(Page.Invite, true), UiButtonStyle.Sticker, ref y);
        settingsB = LeftButton(PT.SettingsButton, OpenSettings, UiButtonStyle.Sticker, ref y);
        unstuckB = LeftButton(PT.Unstuck, OnUnstuck, UiButtonStyle.Sticker, ref y);
        // Controls and Messages share one row so the job actions fit the clipboard.
        float half = (ColLeftW - ButtonGap) * 0.5f;
        controlsB = UiKit.Button(paper, PT.ControlsButton, OpenControls, UiButtonStyle.Sticker);
        controlsB.Size(half, leftButtonHeight);
        PauseUi.TopLeft(controlsB.Rect, ColLeftX + 6f, y, half, leftButtonHeight);
        controlsB.Text.fontSizeMax = 24f;
        leftButtons.Add(controlsB);
        messagesB = UiKit.Button(paper, PT.Messages, () => SelectPage(Page.Messages, true), UiButtonStyle.Sticker);
        messagesB.Size(half, leftButtonHeight);
        PauseUi.TopLeft(messagesB.Rect, ColLeftX + 6f + half + ButtonGap, y, half, leftButtonHeight);
        messagesB.Text.fontSizeMax = 23f;
        leftButtons.Add(messagesB);
        y += leftButtonHeight + ButtonGap;
        // N4 payroll (Docs/Multiplayer.md §13.6): an employee quits from here, instantly.
        if (hasJob) LeftButton("QUIT JOB", QuitJob, UiButtonStyle.Sticker, ref y);
        string leave = variant == PauseVariant.Solo ? PT.SaveQuit
            : variant == PauseVariant.Host ? PT.CloseShop
            : PT.LeaveShift;
        LeftButton(leave, AskLeave, UiButtonStyle.Danger, ref y);
        LeftButton(PT.QuitDesktop, AskQuit, UiButtonStyle.Danger, ref y);
        var sel = new List<Selectable>();
        foreach (var b in leftButtons) sel.Add(b);
        UiKit.ChainVertical(sel);
    }

    UiButton LeftButton(string label, Action click, UiButtonStyle style, ref float y)
    {
        var b = UiKit.Button(paper, label, click, style);
        b.Size(ColLeftW, leftButtonHeight);
        PauseUi.TopLeft(b.Rect, ColLeftX + 6f, y, ColLeftW, leftButtonHeight);
        b.Text.fontSizeMax = 30f;
        leftButtons.Add(b);
        y += leftButtonHeight + ButtonGap;
        return b;
    }

    void BuildRight(float bodyTop, float paperW, float paperH)
    {
        float w = paperW - ColRightX - ColRightPad;
        tabPages.Clear();
        tabPages.Add(Page.Shift);
        if (variant != PauseVariant.Solo) { tabPages.Add(Page.Crew); tabPages.Add(Page.Rules); tabPages.Add(Page.Invite); }
        tabPages.Add(Page.Messages);
        var names = new List<string>();
        foreach (var p in tabPages) names.Add(PT.PauseTabNames[(int)p]);

        // tabs first, then the page panel over their bottoms: the active tab is paper-coloured and joins the panel
        float tabsY = bodyTop + 4f, panelY = tabsY + 62f;
        tabs = UiKit.Tabs(paper, names, i => SelectPage(tabPages[Mathf.Clamp(i, 0, tabPages.Count - 1)], false));
        PauseUi.TopLeft((RectTransform)tabs.transform, ColRightX + 10f, tabsY, w - 20f, 64f);
        tabs.FitTo(w - 20f);
        foreach (var b in tabs.Buttons) b.Text.fontSizeMax = 22f;

        pageRoot = PauseUi.TopLeft(FeelUi.Rect("Page", paper, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), ColRightX, panelY, w, paperH - panelY - 22f);
        var psh = FeelUi.Img(pageRoot, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.12f), 0.3f);
        psh.rectTransform.offsetMin = psh.rectTransform.offsetMax = new Vector2(3f, -4f);
        var body = FeelUi.Img(pageRoot, "Body", FeelUi.Rounded, UiTheme.Paper, 0.3f);
        body.raycastTarget = true;

        pageContent = FeelUi.Rect("Content", pageRoot, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero);
        UiKit.Inset(pageContent, 20f, 18f, 20f, 18f);
        SelectPage(tabPages.Contains(page) ? page : Page.Shift, false);
    }

    /// <summary>The raccoon supervisor signs every timesheet (bottom-left, under the buttons).</summary>
    void BuildDoodle(float paperH)
    {
        float y = paperH - 70f;
        var label = UiKit.InkLabel(paper, "Supervisor", "Supervisor:", 20f, UiTheme.InkSoft, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        PauseUi.TopLeft(label.rectTransform, ColLeftX + 6f, y + 4f, 130f, 36f);
        var line = FeelUi.Img(paper, "SignLine", UiKit.Sprites.Dash, UiTheme.WithAlpha(UiTheme.Ink, 0.4f));
        line.type = Image.Type.Tiled;
        PauseUi.TopLeft(line.rectTransform, ColLeftX + 140f, y + 36f, 270f, 4f);
        var face = PauseUi.TopLeft(FeelUi.Rect("Raccoon", paper, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), ColLeftX + 220f, y - 4f, 64f, 44f);
        face.localRotation = Quaternion.Euler(0f, 0f, 8f);
        Color fur = UiTheme.Hex(0x8E8A99), ink = UiTheme.WithAlpha(UiTheme.Ink, 0.9f);
        for (int s = -1; s <= 1; s += 2)
        {
            var ear = FeelUi.Img(face, "Ear", UiKit.Sprites.Triangle, ink);
            UiKit.Place(ear.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(s * 17f, 15f), new Vector2(20f, 20f));
            ear.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -s * 18f);
        }
        var head = FeelUi.Img(face, "Head", FeelUi.Circle, fur);
        UiKit.Place(head.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(52f, 40f));
        var mask = FeelUi.Img(face, "Mask", FeelUi.Rounded, ink, 0.6f);
        UiKit.Place(mask.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 3f), new Vector2(46f, 14f));
        for (int s = -1; s <= 1; s += 2)
        {
            var eye = FeelUi.Img(face, "Eye", FeelUi.Circle, Color.white);
            UiKit.Place(eye.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(s * 10f, 3f), new Vector2(11f, 11f));
            var pupil = FeelUi.Img(face, "Pupil", FeelUi.Circle, UiTheme.Ink);
            UiKit.Place(pupil.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(s * 10f + 1.5f, 2f), new Vector2(5f, 5f));
        }
        var nose = FeelUi.Img(face, "Nose", FeelUi.Circle, UiTheme.Ink);
        UiKit.Place(nose.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -9f), new Vector2(9f, 7f));
        var scribble = UiKit.InkLabel(paper, "Signature", "~raccoon~", 22f, UiTheme.WithAlpha(UiTheme.Hex(0x2F4FA8), 0.85f),
                                      TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        PauseUi.TopLeft(scribble.rectTransform, ColLeftX + 296f, y + 4f, 130f, 34f);
        scribble.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 4f);
    }

    /// <summary>The galvanised clipboard clip at the top of the board.</summary>
    void BuildClip()
    {
        var clip = FeelUi.Rect("Clip", board, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -22f), new Vector2(300f, 84f));
        var sh = FeelUi.Img(clip, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.35f), 0.3f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(4f, -7f);
        FeelUi.Img(clip, "Back", FeelUi.Rounded, UiTheme.Hex(0x8E96A0), 0.3f);
        var front = FeelUi.Img(clip, "Front", FeelUi.Rounded, UiTheme.Hex(0xBCC4CD), 0.3f);
        UiKit.Inset(front.rectTransform, 10f, 10f, 10f, 10f);
        FeelUi.Img(clip, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.Hex(0x5E6670), 0.3f);
        var lever = FeelUi.Rect("Lever", clip, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), new Vector2(150f, 26f));
        FeelUi.Img(lever, "Body", FeelUi.Rounded, UiTheme.Hex(0x6E7681), 0.5f);
        FeelUi.Img(lever, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.Hex(0x4A5059), 0.5f);
        for (int s = -1; s <= 1; s += 2)
        {
            var rivet = FeelUi.Img(clip, "Rivet", FeelUi.Circle, UiTheme.Hex(0x5E6670));
            UiKit.Place(rivet.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(s * 118f, -4f), new Vector2(18f, 18f));
            var shine = FeelUi.Img(rivet.transform, "Shine", FeelUi.Circle, UiTheme.WithAlpha(Color.white, 0.6f));
            UiKit.Place(shine.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-3f, 3f), new Vector2(6f, 6f));
        }
        var engraved = UiKit.InkLabel(clip, "Engraved", "HEAD OFFICE", 18f, UiTheme.Hex(0x4A5059), TextAlignmentOptions.Center, true);
        engraved.characterSpacing = 8f;
        UiKit.Inset(engraved.rectTransform, 40f, 12f, 40f, 30f);
    }

    /// <summary>A Head Office sticky note hanging off the board's right edge (only when there's room beside the board).</summary>
    void BuildSticky(Vector2 refSize, float k)
    {
        float room = (refSize.x - BoardSize.x * k) * 0.5f;
        if (room < 190f * k) return;
        var note = FeelUi.Rect("StickyNote", board, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(70f, -250f), new Vector2(240f, 132f));
        note.localRotation = Quaternion.Euler(0f, 0f, 6f);
        var nsh = FeelUi.Img(note, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.22f), 0.12f);
        nsh.rectTransform.offsetMin = nsh.rectTransform.offsetMax = new Vector2(4f, -5f);
        FeelUi.Img(note, "Paper", FeelUi.Rounded, UiTheme.Hex(0xFFE873), 0.12f);
        var tape = FeelUi.Img(note, "Tape", UiKit.Sprites.Tape, UiTheme.Hex(0xF6F0DC, 0.85f));
        UiKit.Place(tape.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-6f, 0f), new Vector2(96f, 24f));
        tape.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -8f);
        var notes = PT.BreakNotes;
        var t = UiKit.InkLabel(note, "Tip", notes[noteIndex % notes.Length], 22f, UiTheme.Ink, TextAlignmentOptions.Center, false);
        t.textWrappingMode = TextWrappingModes.Normal;
        t.enableAutoSizing = true;
        t.fontSizeMax = 22f;
        t.fontSizeMin = UiTheme.MinText;
        t.lineSpacing = -8f;
        UiKit.Inset(t.rectTransform, 14f, 10f, 14f, 18f);
        var sign = UiKit.InkLabel(note, "From", "— Head Office", 18f, UiTheme.InkSoft, TextAlignmentOptions.BottomRight, false, FontStyles.Italic);
        UiKit.Inset(sign.rectTransform, 10f, 4f, 14f, 100f);
        t.rectTransform.offsetMin = new Vector2(14f, 28f);
    }

    // ------------------------------------------------------------------ pages

    void SelectPage(Page p, bool fromButton)
    {
        if (!tabPages.Contains(p)) p = Page.Shift;
        page = p;
        int idx = tabPages.IndexOf(p);
        if (tabs != null && tabs.Index != idx) tabs.Select(idx, false);
        if (fromButton) UiTheme.Play(UiTheme.SfxOpen, 0.4f, 1.1f);
        BuildPage();
    }

    void BuildPage()
    {
        if (pageContent == null) return;
        for (int i = pageContent.childCount - 1; i >= 0; i--) PauseUi.DestroyNow(pageContent.GetChild(i).gameObject);
        foreach (var c in pageContent.GetComponents<MonoBehaviour>()) PauseUi.DestroyNow(c);
        shiftView = null;
        crewView = null;
        rulesView = null;
        plateText = inviteStatus = null;
        showB = null;
        pageSelectables.Clear();
        messagesShown = -1;
        switch (page)
        {
            case Page.Shift:
                shiftView = ShiftCardView.Build(pageContent, ShiftNow());
                if (shiftView.ScrollControl != null) pageSelectables.Add(shiftView.ScrollControl);
                break;
            case Page.Crew:
                crewView = CrewPanel.Build(pageContent, CrewNow(), variant == PauseVariant.Host && !preview ? AskKick : (variant == PauseVariant.Host ? (Action<CrewRow>)(_ => { }) : null));
                pageSelectables.AddRange(crewView.Selectables);
                break;
            case Page.Rules:
                rulesView = TownRulesCard.Build(pageContent, variant == PauseVariant.Host, preview);
                if (rulesView.Selectable != null) pageSelectables.Add(rulesView.Selectable);
                break;
            case Page.Invite: BuildInvite(); break;
            default: BuildMessages(); break;
        }
        WireNavigation();
    }

    void WireNavigation()
    {
        if (tabs == null || tabs.Buttons.Count == 0) return;
        int idx = Mathf.Clamp(tabPages.IndexOf(page), 0, tabs.Buttons.Count - 1);
        var tab = tabs.Buttons[idx];
        foreach (var b in leftButtons)
        {
            var n = b.navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnRight = tab;
            b.navigation = n;
        }
        tabs.SetDown(pageSelectables.Count > 0 ? pageSelectables[0] : null);
        var first = tabs.Buttons[0].navigation;
        first.selectOnLeft = resumeB;
        tabs.Buttons[0].navigation = first;
        if (pageSelectables.Count > 0) UiKit.ChainVertical(pageSelectables, tab, null);
    }

    // ---- invite

    void BuildInvite()
    {
        float w = Mathf.Max(400f, pageContent.rect.width);
        string code = CodeNow();
        bool host = variant == PauseVariant.Host;
        if (string.IsNullOrEmpty(code))
        {
            var t = PauseUi.Text(pageContent, "NoCode", PT.InviteNoCode, 22f, TextAlignmentOptions.TopLeft, false, UiTheme.InkSoft, FontStyles.Normal);
            t.textWrappingMode = TextWrappingModes.Normal;
            PauseUi.TopLeft(t.rectTransform, 4f, 4f, w - 8f, 120f);
            return;
        }
        var cap = PauseUi.Text(pageContent, "Caption", PT.InviteCode, 20f, TextAlignmentOptions.BottomLeft, true, UiTheme.StampRed);
        cap.characterSpacing = 5f;
        PauseUi.TopLeft(cap.rectTransform, 4f, 0f, 300f, 30f);

        // the licence plate
        var plate = PauseUi.TopLeft(FeelUi.Rect("Plate", pageContent, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 4f, 38f, 440f, 132f);
        plate.localRotation = Quaternion.Euler(0f, 0f, -1.2f);
        var psh = FeelUi.Img(plate, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.3f), 0.3f);
        psh.rectTransform.offsetMin = psh.rectTransform.offsetMax = new Vector2(4f, -6f);
        FeelUi.Img(plate, "Body", FeelUi.Rounded, UiTheme.Hex(0xFFD640), 0.3f);
        var inner = FeelUi.Img(plate, "Inner", UiKit.Sprites.RoundedOutline, UiTheme.Ink, 0.25f);
        UiKit.Inset(inner.rectTransform, 8f, 8f, 8f, 8f);
        FeelUi.Img(plate, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.6f), 0.3f);
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                var bolt = FeelUi.Img(plate, "Bolt", FeelUi.Circle, UiTheme.Hex(0xA7AFB8));
                UiKit.Place(bolt.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(sx * 196f, sy * 46f), new Vector2(14f, 14f));
                FeelUi.Img(bolt.transform, "Rim", UiKit.Sprites.Ring, UiTheme.WithAlpha(UiTheme.Ink, 0.7f));
            }
        var top = UiKit.InkLabel(plate, "Top", PT.Plate, 18f, UiTheme.Ink, TextAlignmentOptions.Top, true);
        top.characterSpacing = 8f;
        UiKit.Inset(top.rectTransform, 30f, 90f, 30f, 14f);
        plateText = UiKit.InkLabel(plate, "Code", "", 64f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        plateText.characterSpacing = 14f;
        plateText.enableAutoSizing = true;
        plateText.fontSizeMax = 56f;
        plateText.fontSizeMin = 36f;
        UiKit.Inset(plateText.rectTransform, 30f, 40f, 30f, 36f);
        var town = UiKit.InkLabel(plate, "Town", TownName().ToUpperInvariant(), 18f, UiTheme.Ink, TextAlignmentOptions.Bottom, false);
        town.characterSpacing = 4f;
        UiKit.Inset(town.rectTransform, 40f, 14f, 40f, 92f);

        showB = UiKit.Button(pageContent, codeRevealed ? PT.Hide : PT.Show, ToggleCode, UiButtonStyle.Paper);
        showB.Size(200f, 58f);
        PauseUi.TopLeft(showB.Rect, 470f, 42f, 200f, 58f);
        var copyB = UiKit.Button(pageContent, PT.Copy, CopyCode, UiButtonStyle.Paper);
        copyB.Size(200f, 58f);
        PauseUi.TopLeft(copyB.Rect, 470f, 110f, 200f, 58f);
        pageSelectables.Add(showB);
        pageSelectables.Add(copyB);
        ApplyCode();

        float y = 196f;
        if (host)
        {
            var steamB = UiKit.Button(pageContent, PT.InviteSteam, InviteSteam, UiButtonStyle.Go);
            steamB.Size(440f, 68f);
            PauseUi.TopLeft(steamB.Rect, 4f, y, 440f, 68f);
            steamB.Text.fontSizeMax = 28f;
            pageSelectables.Insert(0, steamB);
            inviteStatus = PauseUi.Text(pageContent, "Status", "", 20f, TextAlignmentOptions.TopLeft, false, UiTheme.StampRed, FontStyles.Normal);
            inviteStatus.textWrappingMode = TextWrappingModes.Normal;
            PauseUi.TopLeft(inviteStatus.rectTransform, 6f, y + 76f, w - 12f, 52f);
            y += 136f;
        }
        var help = PauseUi.Text(pageContent, "Help", host ? PT.InviteHostHelp : PT.InviteGuestHelp, 22f, TextAlignmentOptions.TopLeft, false,
                                UiTheme.InkSoft, FontStyles.Normal);
        help.textWrappingMode = TextWrappingModes.Normal;
        PauseUi.TopLeft(help.rectTransform, 6f, y, w - 12f, 90f);
    }

    void ApplyCode()
    {
        if (plateText == null) return;
        string code = CodeNow();
        plateText.text = codeRevealed ? code : new string('•', Mathf.Max(4, code != null ? code.Length : 6));
        if (showB != null) showB.SetLabel(codeRevealed ? PT.Hide : PT.Show);
    }

    void ToggleCode()
    {
        codeRevealed = !codeRevealed;
        if (!preview) JoinCodeUI.HiddenByPlayer = !codeRevealed; // the HUD's code follows the Esc menu's Show/Hide
        ApplyCode();
    }

    void CopyCode()
    {
        string code = CodeNow();
        if (string.IsNullOrEmpty(code)) return;
        GUIUtility.systemCopyBuffer = code; // copying never shows it on screen (streamer-safe)
        UiTheme.Play(UiTheme.SfxConfirm, 0.45f, 1.1f);
        GameHUD.Toast(PT.Copied, ToastStyle.Good, 2f);
    }

    void InviteSteam()
    {
        if (preview) return;
        if (SteamLobby.OpenInviteDialog(out string whyNot))
        {
            UiTheme.Play(UiTheme.SfxInvite, 0.5f, 1f);
            if (inviteStatus != null) inviteStatus.text = "";
            return;
        }
        UiTheme.Play(UiTheme.SfxError, 0.4f, 1f);
        if (inviteStatus != null) inviteStatus.text = string.IsNullOrEmpty(whyNot) ? "Steam invites aren't available right now." : whyNot;
    }

    // ---- messages

    void BuildMessages()
    {
        float w = Mathf.Max(400f, pageContent.rect.width), h = Mathf.Max(200f, pageContent.rect.height);
        var note = PauseUi.Text(pageContent, "Note", PT.MessagesNote, 18f, TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Italic);
        PauseUi.TopLeft(note.rectTransform, 4f, 0f, w - 8f, 28f);
        var list = UiKit.ScrollList(pageContent, new Vector2(w, h - 36f));
        UiKit.Place((RectTransform)list.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -36f), new Vector2(w, h - 36f));
        var content = list.content;
        float rowW = w - 26f;
        var entries = MessagesNow();
        messagesShown = preview ? -2 : NotificationLog.Version;
        if (entries.Count == 0)
        {
            var empty = FeelUi.Rect("Empty", content, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(rowW, 80f));
            var le0 = empty.gameObject.AddComponent<LayoutElement>();
            le0.minHeight = le0.preferredHeight = 80f;
            var t = PauseUi.Text(empty, "Text", PT.NoMessages, 22f, TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Italic);
            t.textWrappingMode = TextWrappingModes.Normal;
            UiKit.Inset(t.rectTransform, 8f, 0f, 8f, 0f);
            return;
        }
        foreach (var e in entries)
        {
            var row = FeelUi.Rect("Message", content, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(rowW, 60f));
            float textW = rowW - 40f - (e.join != null ? 140f : 8f);
            var text = PauseUi.Text(row, "Text", e.text, 22f, TextAlignmentOptions.TopLeft, false, UiTheme.Ink, FontStyles.Normal);
            text.textWrappingMode = TextWrappingModes.Normal;
            float th = Mathf.Ceil(text.GetPreferredValues(e.text, textW, 0f).y) + 2f;
            float rh = 30f + th + 12f;
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = rh;
            row.sizeDelta = new Vector2(rowW, rh);
            PauseUi.TopLeft(text.rectTransform, 32f, 30f, textW, th);
            var dot = FeelUi.Img(row, "Dot", FeelUi.Circle, StyleColor(e.style));
            PauseUi.TopLeft(dot.rectTransform, 8f, 8f, 16f, 16f);
            string when = e.day > 0 ? $"Day {e.day} · {e.time}" : e.time;
            if (e.repeats > 1) when += $"  ×{e.repeats}";
            var whenT = PauseUi.Text(row, "When", when, 18f, TextAlignmentOptions.MidlineLeft, false, UiTheme.InkSoft, FontStyles.Bold);
            PauseUi.TopLeft(whenT.rectTransform, 32f, 2f, textW, 28f);
            if (e.join != null)
            {
                var join = e.join;
                var jb = UiKit.Button(row, PT.Join, () => { Resume(); join(); }, UiButtonStyle.Go);
                jb.Size(120f, 50f);
                UiKit.Place(jb.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(120f, 50f));
                pageSelectables.Add(jb);
            }
            var rule = FeelUi.Img(row, "Rule", UiKit.Sprites.Dash, UiTheme.WithAlpha(UiTheme.Ink, 0.14f));
            rule.type = Image.Type.Tiled;
            rule.rectTransform.anchorMin = new Vector2(0f, 0f);
            rule.rectTransform.anchorMax = new Vector2(1f, 0f);
            rule.rectTransform.offsetMin = new Vector2(6f, -1f);
            rule.rectTransform.offsetMax = new Vector2(-6f, 2f);
        }
        if (!preview) NotificationLog.MarkRead();
    }

    static Color StyleColor(ToastStyle s) => s switch
    {
        ToastStyle.Good => UiTheme.Go,
        ToastStyle.Bad => UiTheme.StampRed,
        ToastStyle.Rare => UiTheme.Hex(0xE0A800),
        ToastStyle.Legendary => UiTheme.Tangerine,
        _ => UiTheme.Disabled,
    };

    // ------------------------------------------------------------------ data (live, or samples for previews)

    ShiftData ShiftNow() => preview ? ShiftData.Sample(variant == PauseVariant.Guest) : ShiftData.Live(variant == PauseVariant.Guest, HostName());

    List<CrewRow> CrewNow() => preview ? CrewPanel.Sample(variant == PauseVariant.Host) : CrewPanel.Live();

    string CodeNow()
    {
        if (preview) return "K7M2QX";
        return variant == PauseVariant.Host ? NetSession.JoinCode : variant == PauseVariant.Guest ? GameConfig.LastJoinCode : null;
    }

    string TownName()
    {
        if (preview || TownNameSource == null) return PT.DefaultTownName;
        try
        {
            string n = TownNameSource();
            return string.IsNullOrEmpty(n) ? PT.DefaultTownName : n;
        }
        catch (Exception e) { Debug.LogException(e); return PT.DefaultTownName; }
    }

    string HostName()
    {
        if (preview) return "Nick";
        var roster = NetRoster.Instance;
        if (roster != null)
            foreach (var e in roster.Entries)
                if (e.IsHost && e.displayName.Length > 0) return e.displayName.ToString();
        return PT.TheBoss;
    }

    string EmployeeName()
    {
        foreach (var r in CrewNow())
            if (r.isMe) return r.VariantName;
        return "Chuck";
    }

    List<NotificationLog.Entry> MessagesNow()
    {
        var list = new List<NotificationLog.Entry>();
        if (preview)
        {
            list.Add(new NotificationLog.Entry { text = "+$8 Stop cleared! 12 Maple St", style = ToastStyle.Good, day = 14, time = "7:41 PM", repeats = 1 });
            list.Add(new NotificationLog.Entry { text = "Truck's full! Go dump it.", style = ToastStyle.Bad, day = 14, time = "7:38 PM", repeats = 2 });
            list.Add(new NotificationLog.Entry { text = "RARE FIND! Grandma's wedding ring (worth about $340). Into the KEEP crate it goes.", style = ToastStyle.Rare, day = 14, time = "7:20 PM", repeats = 1 });
            list.Add(new NotificationLog.Entry { text = "BEEP: Ozzie clocked in 7:02 PM (LATE)", style = ToastStyle.Info, day = 14, time = "7:02 PM", repeats = 1 });
            list.Add(new NotificationLog.Entry { text = "Contaminated! Glass doesn't go in PAPER. −$0.25", style = ToastStyle.Bad, day = 14, time = "6:55 PM", repeats = 1 });
            return list;
        }
        for (int i = 0; i < NotificationLog.Count; i++) list.Add(NotificationLog.Newest(i));
        return list;
    }

    // ------------------------------------------------------------------ live refresh

    void RefreshLive(bool force)
    {
        if (subline != null)
        {
            string day = preview ? "14" : GameClock.Day.ToString();
            string clock = preview ? "7:42 PM" : GameClock.ClockText;
            subline.text = $"{TownName()} · Day {day} · {clock} · Employee: {(preview ? (variant == PauseVariant.Guest ? "Dot" : "Chuck") : EmployeeName())}";
        }
        if (unstuckB != null)
        {
            string why = preview ? null : Unstuck.Unavailable();
            if (why == null) { unstuckB.SetLabel(PT.Unstuck); if (!unstuckB.interactable) unstuckB.SetDisabled(null); }
            else if (int.TryParse(why, out int secs)) { unstuckB.SetLabel(string.Format(PT.UnstuckCooldown, secs)); if (unstuckB.interactable) unstuckB.SetDisabled(why, false); }
            else { unstuckB.SetLabel(PT.Unstuck); if (unstuckB.DisabledReason != why) unstuckB.SetDisabled(why); }
        }
        if (messagesB != null)
        {
            int n = preview ? 5 : NotificationLog.Count;
            messagesB.SetLabel(n > 0 ? string.Format(PT.MessagesCount, n) : PT.Messages);
        }
        if (preview && !force) return;
        if (shiftView != null) shiftView.Apply(ShiftNow());
        if (crewView != null && !preview) crewView.Refresh(CrewNow(), variant == PauseVariant.Host ? AskKick : (Action<CrewRow>)null);
        if (rulesView != null) rulesView.Apply(preview || NightShift.RabiesTag);
        if (page == Page.Messages && !preview && messagesShown != NotificationLog.Version) BuildPage();
    }

    void Update()
    {
        if (preview || canvas == null || !IsOpen) return;
        // the game this menu belongs to is gone (left, kicked, despawned) or is changing scene (the host drove out): close
        var s = NetSession.State;
        if (PauseService.Me == null || NetSession.MenuOpen || SceneController.IsTransitioning || s == NetSessionState.Leaving ||
            s == NetSessionState.Failed || s == NetSessionState.Preparing || s == NetSessionState.Joining)
        {
            Close();
            return;
        }
        var size = root.rect.size;
        if (size.x > 10f && (Mathf.Abs(size.x - builtFor.x) > 1f || Mathf.Abs(size.y - builtFor.y) > 1f))
        {
            Build(size);
            if (resumeB != null) UiKit.Focus(resumeB.gameObject);
        }
        if (UiStack.Top == this && !UiDialog.IsShowing && tabs != null && tabPages.Count > 1)
        {
            int idx = tabPages.IndexOf(page);
            if (GameControls.Down(KeyCode.Q, GameControls.Action.PreviousTab)) tabs.Select((idx + tabPages.Count - 1) % tabPages.Count, true);
            else if (GameControls.Down(KeyCode.E, GameControls.Action.NextTab)) tabs.Select((idx + 1) % tabPages.Count, true);
        }
        if (Time.unscaledTime >= refreshAt)
        {
            refreshAt = Time.unscaledTime + 0.5f;
            try { RefreshLive(false); }
            catch (Exception e) { Debug.LogException(e); }
        }
        if (stampT < 1f)
        {
            stampT += Time.unscaledDeltaTime;
            ApplyStamp();
        }
    }

    /// <summary>The stamp thunks down when the menu opens (scale 1.9 → 1 with a bounce, a soft thud).</summary>
    void ApplyStamp()
    {
        if (stampRt == null) return;
        float t = Mathf.Clamp01(stampT / 0.22f);
        stampRt.localScale = Vector3.one * Mathf.LerpUnclamped(1.9f, 1f, FeelEase.OutBack(t, 1.6f));
        var g = stampRt.GetComponent<CanvasGroup>();
        if (g == null) g = stampRt.gameObject.AddComponent<CanvasGroup>();
        g.alpha = Mathf.Clamp01(stampT / 0.08f);
        if (!stampSounded && stampT >= 0.12f && Application.isPlaying && !preview)
        {
            stampSounded = true;
            UiTheme.Play("thud", 0.4f, 1.25f);
        }
    }

    // ------------------------------------------------------------------ actions

    void OpenSettings()
    {
        if (preview) return;
        SettingsScreen.Show(() => { if (this != null && IsOpen && settingsB != null) UiKit.Focus(settingsB.gameObject); });
    }

    void OpenControls()
    {
        if (preview) return;
        SettingsScreen.Show(() => { if (this != null && IsOpen && controlsB != null) UiKit.Focus(controlsB.gameObject); }, 1);
    }

    void OnUnstuck()
    {
        if (preview) return;
        string why = Unstuck.Unavailable();
        if (why != null) { UiTheme.Play(UiTheme.SfxError, 0.4f, 1f); return; }
        var me = PauseService.Me;
        var plan = me != null ? Unstuck.MakePlan(me) : default;
        if (plan.fee)
        {
            // The tow drone costs money whether it brings your truck to you or rights the one you sit in.
            bool tow = plan.kind == Unstuck.Kind.Tow;
            UiKit.Confirm(tow ? PT.TowTitle : PT.CallTitle, string.Format(tow ? PT.TowBody : PT.CallBody, Scrapazon.Name, Economy.Money(Economy.TowFee)),
                          tow ? PT.TowYes : PT.CallYes, UiText.NeverMind, yes => { if (yes) DoUnstuck(); });
            return;
        }
        DoUnstuck();
    }

    void DoUnstuck()
    {
        Resume(); // back to work first: the tow runs on game time
        if (!Unstuck.Use(out string why) && !string.IsNullOrEmpty(why)) GameHUD.Toast(why, ToastStyle.Bad, 2.5f);
    }

    void AskKick(CrewRow r)
    {
        string who = string.IsNullOrEmpty(r.displayName) ? r.VariantName : $"{r.VariantName} ({r.displayName})";
        UiKit.Confirm(string.Format(PT.KickTitle, who), PT.KickBody, PT.KickYes, UiText.NeverMind, yes =>
        {
            if (!yes) return;
            SessionService.Kick(r.clientId, PT.KickReason);
        });
    }

    void AskOpenShop()
    {
        if (preview) return;
        UiKit.Confirm(PT.OpenShopTitle, PT.OpenShopBody, PT.OpenShopYes, PT.OpenShopNo, yes =>
        {
            if (!yes || this == null) return;
            SaveOwn();
            Close();
            StartCoroutine(Rehost());
        });
    }

    /// <summary>"Open shop to friends" (§13 Q8: a quick re-host now): save, leave to the menu, host over Relay into the garage.</summary>
    IEnumerator Rehost()
    {
        NetErrorPanel.ShowBusy(PT.OpeningShop);
        NetSession.Leave("open to friends");
        float until = Time.realtimeSinceStartup + 15f;
        while (Time.realtimeSinceStartup < until)
        {
            var nm = NetBoot.Manager;
            if (NetSession.State == NetSessionState.Idle && (nm == null || (!nm.IsListening && !nm.ShutdownInProgress))) break;
            yield return null;
        }
        yield return null;
        yield return null; // let the menu scene settle
        var task = NetSession.HostAsync(new HostOptions { mode = NetMode.UnityRelay, maxPlayers = NetConstants.CrewTownCap });
        while (!task.IsCompleted) yield return null;
        NetErrorPanel.HideBusy();
    }

    void AskLeave()
    {
        if (preview) return;
        switch (variant)
        {
            case PauseVariant.Solo:
                UiKit.Confirm(PT.SaveQuitTitle, PT.SaveQuitBody, PT.SaveQuitYes, UiText.QuitNo, yes => { if (yes) Leave("menu"); });
                break;
            case PauseVariant.Host:
                UiKit.Confirm(PT.CloseShopTitle, PT.CloseShopBody, PT.CloseShopYes, PT.KeepWorking, yes => { if (yes) Leave("closed the shop"); });
                break;
            default:
                UiKit.Confirm(string.Format(PT.LeaveTitle, HostName()), PT.LeaveBody, PT.LeaveYes, PT.Stay, yes => { if (yes) Leave("left"); });
                break;
        }
    }

    void Leave(string reason)
    {
        SaveOwn();
        Close();
        NetSession.Leave(reason);
    }

    /// <summary>Solo and host saves their own world before leaving (a guest's statics are someone else's: never).</summary>
    void SaveOwn()
    {
        if (variant == PauseVariant.Guest) return;
        try { if (SaveGuard.WritesAllowed) GameSession.Save(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void AskQuit()
    {
        if (preview) return;
        string body = variant == PauseVariant.Solo ? PT.QuitBodySolo : variant == PauseVariant.Host ? PT.QuitBodyHost : PT.QuitBodyGuest;
        UiKit.Confirm(UiText.QuitTitle, body, UiText.QuitYes, UiText.QuitNo, yes =>
        {
            if (!yes) return;
            SaveOwn();
            Close();
#if UNITY_EDITOR
            Debug.Log("[PauseMenu] Quit does nothing in the Editor; leaving to the menu instead.");
            NetSession.Leave("quit (Editor)");
#else
            Application.Quit(); // NetBoot tells guests "The Boss went home" first when hosting
#endif
        });
    }

    // ------------------------------------------------------------------ audit (acceptance UP3-6)

    /// <summary>
    /// Every text checked against the legibility floor (18 px at the 1080 reference at 100% UI scale: a text's size × the
    /// clipboard's fit scale × <paramref name="uiScale"/>) and for overflow, and the main blocks checked for overlaps and for
    /// staying on the paper. Returns the problems ("" = none).
    /// </summary>
    public string Audit(out int checkedTexts, float uiScale = -1f)
    {
        if (uiScale <= 0f) uiScale = UiScale.Factor;
        Canvas.ForceUpdateCanvases();
        var sb = new System.Text.StringBuilder();
        checkedTexts = 0;
        float canvasScale = transform.lossyScale.y;
        foreach (var t in GetComponentsInChildren<TMP_Text>(true))
        {
            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
            checkedTexts++;
            t.ForceMeshUpdate();
            float eff = t.fontSize * (t.rectTransform.lossyScale.y / Mathf.Max(1e-5f, canvasScale)) * uiScale;
            string path = t.transform.parent != null ? t.transform.parent.name + "/" + t.name : t.name;
            if (t.transform.IsChildOf(stampRt)) eff = t.fontSize * board.localScale.y * uiScale; // the stamp's thunk scale is transient
            if (eff < UiTheme.MinText - 0.05f) sb.Append($"small {path} {eff:0.#}px; ");
            if (t.isTextOverflowing) sb.Append($"overflow {path} '{Trim(t.text)}'; ");
            var r = t.rectTransform.rect;
            var pref = t.GetPreferredValues(t.text, t.textWrappingMode == TextWrappingModes.NoWrap ? 100000f : r.width, 0f);
            if (t.textWrappingMode == TextWrappingModes.NoWrap && !t.enableAutoSizing && pref.x > r.width + 1f)
                sb.Append($"wide {path} {pref.x:0}>{r.width:0} '{Trim(t.text)}'; ");
            if (pref.y > r.height + 2f && !t.enableAutoSizing) sb.Append($"tall {path} {pref.y:0}>{r.height:0} '{Trim(t.text)}'; ");
        }
        // blocks: no two overlap, all inside the paper
        var blocks = new List<(string name, Rect r)>();
        void Add(string name, RectTransform rt) { if (rt != null && rt.gameObject.activeInHierarchy) blocks.Add((name, ScreenRect(rt))); }
        Add("title", paper.Find("Title") as RectTransform);
        Add("stamp", stampRt);
        foreach (var b in leftButtons) Add("button " + b.Text.text, b.Rect);
        Add("tabs", (RectTransform)tabs.transform);
        Add("page", pageRoot);
        Add("tape", paper.Find("BreakTape") as RectTransform);
        Add("signature", paper.Find("Raccoon") as RectTransform);
        Rect paperR = ScreenRect(paper);
        for (int i = 0; i < blocks.Count; i++)
        {
            var a = blocks[i];
            if (!Inside(paperR, a.r, 3f)) sb.Append($"off-paper {a.name}; ");
            for (int j = i + 1; j < blocks.Count; j++)
            {
                var b = blocks[j];
                if (a.name == "tabs" && b.name == "page") continue; // the tabs sit on the page's top edge by design
                if (Overlap(a.r, b.r, 2f)) sb.Append($"overlap {a.name} × {b.name}; ");
            }
        }
        return sb.ToString();
    }

    static Rect ScreenRect(RectTransform rt)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        float xmin = Mathf.Min(c[0].x, c[1].x, c[2].x, c[3].x), xmax = Mathf.Max(c[0].x, c[1].x, c[2].x, c[3].x);
        float ymin = Mathf.Min(c[0].y, c[1].y, c[2].y, c[3].y), ymax = Mathf.Max(c[0].y, c[1].y, c[2].y, c[3].y);
        return Rect.MinMaxRect(xmin, ymin, xmax, ymax);
    }

    static bool Overlap(Rect a, Rect b, float slack) =>
        a.xMin < b.xMax - slack && b.xMin < a.xMax - slack && a.yMin < b.yMax - slack && b.yMin < a.yMax - slack;

    static bool Inside(Rect outer, Rect inner, float slack) =>
        inner.xMin >= outer.xMin - slack && inner.xMax <= outer.xMax + slack && inner.yMin >= outer.yMin - slack && inner.yMax <= outer.yMax + slack;

    static string Trim(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    /// <summary>Preview hook: the scale the clipboard was drawn at (1 = design size).</summary>
    public float BoardScale => board != null ? board.localScale.x : 1f;

    protected override void OnDestroy()
    {
        if (instance == this) instance = null;
        base.OnDestroy();
        if (!preview) PauseService.Unfreeze("menu destroyed");
    }
}

/// <summary>Small layout helpers for the Esc menu's pages (top-left origin, y grows downward).</summary>
internal static class PauseUi
{
    /// <summary>Anchor <paramref name="rt"/> to its parent's top-left; <paramref name="y"/> is measured downward.</summary>
    public static RectTransform TopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    /// <summary>Ink text on paper (display = the display font).</summary>
    public static TextMeshProUGUI Text(Transform p, string name, string text, float size, TextAlignmentOptions align, bool display,
                                       Color color, FontStyles style = FontStyles.Bold) =>
        UiKit.InkLabel(p, name, text, size, color, align, display, style);

    /// <summary>A dashed ink rule at <paramref name="y"/> (downward), <paramref name="w"/> wide, from <paramref name="x"/>.</summary>
    public static Image Rule(RectTransform p, float y, float w, float x = 0f)
    {
        var rule = FeelUi.Img(p, "Rule", UiKit.Sprites.Dash, UiTheme.WithAlpha(UiTheme.Ink, 0.3f));
        rule.type = Image.Type.Tiled;
        TopLeft(rule.rectTransform, x, y, w, 4f);
        return rule;
    }

    public static void DestroyNow(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying)
        {
            if (o is GameObject go) go.SetActive(false);
            UnityEngine.Object.Destroy(o);
        }
        else UnityEngine.Object.DestroyImmediate(o);
    }
}

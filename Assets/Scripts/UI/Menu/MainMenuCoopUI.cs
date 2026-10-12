using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The main menu shell (Docs/UIPass.md §4; package UP-4; B14's file name, so M1's company cards slot in here): the wobbly
/// RUBBISH logo, the Continue card, the sticker column (Play solo · Host with friends · Join a friend · Crew Card [SOON] ·
/// Settings · Quit), the identity chip, the version line and the invite cards, over <see cref="MenuDiorama"/>'s live curb set.
/// <list type="bullet">
/// <item><b>Actions</b> stay B1's: every button calls <see cref="MenuManager"/> (Play solo / Continue = solo host on the legacy
/// selected business, Host = Relay + Steam lobby, Join = code, with the SaveGuard latch), whose busy and error cards are
/// <see cref="NetErrorPanel"/>'s.</item>
/// <item><b>Pages</b> are <see cref="UiStack"/> screens (Join, Settings, dialogs), so Esc goes back; at the root Esc does nothing
/// (<see cref="UiStack.MenuBack"/> returns false).</item>
/// <item><b>Busy:</b> while NetSession is preparing, joining or leaving every button greys out; without internet Host shows
/// NEEDS INTERNET tape; Crew Card always shows SOON tape (§13 Q6); Choose business selects any existing local slot.</item>
/// <item><b>Layout</b> works in reference units (1920×1080 ÷ UI scale) and adapts to the height (21:9 is 935 tall, UI 150% at
/// 16:9 is 720): the logo shrinks, buttons get shorter, and below 52 px they go two to a row. The column's right edge is
/// passed to the diorama so the camera frames the set in the free space.</item>
/// </list>
/// </summary>
public sealed class MainMenuCoopUI : MonoBehaviour
{
    /// <summary>What a hovered/focused button means to the diorama (§4.7 hover reactions).</summary>
    public enum HoverKind { None, Continue, PlaySolo, Host, Join, CrewCard, Settings, Quit }

    /// <summary>The open menu (null outside the main menu).</summary>
    public static MainMenuCoopUI Instance { get; private set; }

    /// <summary>Seconds from entering play (or launching) to the first menu taking keyboard focus (acceptance UP4-1).</summary>
    public static float ReadyAfterSeconds { get; private set; } = -1f;
    /// <summary>Where the time to <see cref="ReadyAfterSeconds"/> went (boot → menu scene awake → shell built → first focus).</summary>
    public static string ReadyBreakdown { get; private set; } = "";
    static float bootRealtime, awakeAt, builtAt;

    MenuManager host;
    Canvas canvas;
    Camera uiCam;
    RectTransform root, column;
    MenuTitle title;
    ContinueCard card;
    JoinScreen join;
    InviteCards invites;
    UiButton soloB, hostB, joinB, crewB, settingsB, quitB;
    readonly List<UiButton> columnButtons = new List<UiButton>();
    readonly List<Selectable> chain = new List<Selectable>();
    TextMeshProUGUI versionText;
    RectTransform idChip;
    Vector2 builtFor;
    bool preview, busyShown, internetShown = true, joinWasOpen;
    float nextNetCheck;
    string splash;
    ContinueCard.Data? previewData;
    bool joinCoversSplash;
    HoverKind hover;

    /// <summary>Reference-px metrics of the last layout (audits, the diorama).</summary>
    public float ColumnRight { get; private set; }
    /// <summary>The fraction of the screen width the menu column covers (the diorama frames the set right of it).</summary>
    public float DioramaInsetLeft { get; private set; } = 0.36f;
    public bool TwoColumns { get; private set; }
    public float ButtonHeight { get; private set; }
    public RectTransform Root => root;
    public JoinScreen Join => join;
    public UiButton FirstButton => card != null ? card.Button : soloB;

    // ------------------------------------------------------------------ create

    /// <summary>Build the menu for <paramref name="menu"/> (B1's scene object) on its own canvas in the menu scene.</summary>
    public static MainMenuCoopUI Create(MenuManager menu)
    {
        // §4.6: SteamHud's IMGUI invite chip and menu status line hide once this UI exists (the identity chip, the Esc
        // menu's Invite button and the Join page take over); its "Rubbish needs Steam" blocker, messages and F2 stay.
        SteamHud.SuppressChrome = true;
        if (awakeAt <= 0f) awakeAt = Time.realtimeSinceStartup;
        var c = UiKit.Canvas("[Main Menu]", UiLayers.Menu, menu.transform); // parented: it dies with the menu scene
        var ui = c.gameObject.AddComponent<MainMenuCoopUI>();
        ui.host = menu;
        ui.canvas = c;
        ui.splash = PickSplash();
        ui.Build(UiScale.ReferenceSize(Screen.width, Screen.height));
        if (builtAt <= 0f) builtAt = Time.realtimeSinceStartup;
        return ui;
    }

    /// <summary>
    /// Edit-mode preview for screenshots: built under <paramref name="parent"/>, drawn through <paramref name="cam"/> at the
    /// scale a <paramref name="w"/>×<paramref name="h"/> screen would use. <paramref name="page"/> 1 opens the Join page.
    /// </summary>
    public static MainMenuCoopUI CreatePreview(Transform parent, Camera cam, int w, int h, float uiScale, int page,
                                               ContinueCard.Data? data = null, int splashIndex = 0)
    {
        var c = UiKit.Canvas("[Main Menu Preview]", UiLayers.Menu, parent);
        UiKit.PreviewCanvas(c, cam, w, h, uiScale);
        var ui = c.gameObject.AddComponent<MainMenuCoopUI>();
        ui.canvas = c;
        ui.uiCam = cam;
        ui.preview = true;
        ui.previewData = data;
        ui.splash = UiText.MenuSplashes[Mathf.Abs(splashIndex) % UiText.MenuSplashes.Length];
        ui.Build(UiScale.ReferenceSize(w, h, uiScale));
        if (page == 1)
        {
            ui.column.gameObject.SetActive(false);
            ui.ShowSplash(false);
            ui.join.Panel.gameObject.SetActive(true);
            ui.join.SetCode("RMK6");
        }
        return ui;
    }

    static string PickSplash() =>
        UiText.MenuSplashes[UnityEngine.Random.Range(0, UiText.MenuSplashes.Length)]; // coop:allow(random) cosmetic: menu splash line

    void OnEnable()
    {
        if (preview) return;
        Instance = this;
        UiStack.MenuBack = MenuBack;
    }

    void OnDisable()
    {
        if (preview) return;
        if (Instance == this) Instance = null;
        if (UiStack.MenuBack == (Func<bool>)MenuBack) UiStack.MenuBack = null;
        SetHover(HoverKind.None);
    }

    /// <summary>Esc with no screen open: the root page has nowhere to go back to.</summary>
    bool MenuBack() => false;

    // ------------------------------------------------------------------ build

    void Build(Vector2 refSize)
    {
        builtFor = refSize;
        root = (RectTransform)transform;
        for (int i = root.childCount - 1; i >= 0; i--) DestroyNow(root.GetChild(i).gameObject);
        columnButtons.Clear();

        float W = refSize.x, H = refSize.y;
        float x0 = Mathf.Clamp(W * 0.05f, 40f, 96f);
        float top = Mathf.Clamp(H * 0.035f, 22f, 44f);
        float colW = Mathf.Min(544f, W * 0.4f);
        float footer = 64f; // (SteamHud's bottom-left status line is suppressed while this menu exists)

        // ---- logo
        float titleH = Mathf.Clamp(H * 0.15f, 92f, 160f);
        title = MenuTitle.Build(root, titleH, splash ?? UiText.MenuSplashes[0], uiCam, !preview);
        ((RectTransform)title.transform).anchoredPosition = new Vector2(x0, -top);
        float colTop = top + titleH + MenuTitle.SplashHeight + 22f;

        // ---- column: card + buttons, sized to the height we have
        var data = previewData ?? ContinueCard.FromLegacySave();
        int n = 7; // solo, host, join, choose business, crew card, settings, quit
        float avail = H - colTop - footer;
        float cardH = 150f, gapCard = 22f, gap = 12f;
        float btnH = (avail - cardH - gapCard) / n - gap;
        if (btnH < 58f) { cardH = 112f; btnH = (avail - cardH - gapCard) / n - gap; }
        TwoColumns = btnH < 52f;
        if (TwoColumns)
        {
            gap = 10f;
            btnH = Mathf.Min(76f, (avail - cardH - gapCard) / Mathf.Ceil(n / 2f) - gap);
        }
        btnH = Mathf.Clamp(btnH, 46f, 76f);
        ButtonHeight = btnH;

        column = FeelUi.Rect("Column", root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, -colTop), new Vector2(colW, avail));
        card = ContinueCard.Build(column, new Vector2(colW, cardH), OnContinue);
        UiKit.Place(card.Button.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(colW, cardH));
        card.Show(data);
        Relay(card.Button, HoverKind.Continue);

        soloB = MakeButton(UiText.MenuPlaySolo, OnPlaySolo, UiButtonStyle.Primary, HoverKind.PlaySolo);
        hostB = MakeButton(UiText.MenuHostFriends, OnHost, UiButtonStyle.Go, HoverKind.Host);
        joinB = MakeButton(UiText.MenuJoinFriend, OnJoin, UiButtonStyle.Sticker, HoverKind.Join);
        crewB = MakeButton(UiText.MenuCrewCard, null, UiButtonStyle.Sticker, HoverKind.CrewCard);
        settingsB = MakeButton(UiText.MenuSettings, OnSettings, UiButtonStyle.Sticker, HoverKind.Settings);
        quitB = MakeButton(UiText.MenuQuit, OnQuit, UiButtonStyle.Danger, HoverKind.Quit);

        float y = cardH + gapCard;
        int col = 0;
        float bw = TwoColumns ? (colW - 12f) * 0.5f : colW;
        foreach (var b in columnButtons)
        {
            if (!b.gameObject.activeSelf) continue;
            b.Size(bw, btnH);
            float x = TwoColumns ? col * (bw + 12f) : 0f;
            UiKit.Place(b.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(bw, btnH));
            if (!TwoColumns || col == 1) { y += btnH + gap; col = 0; }
            else col = 1;
        }
        crewB.SetDisabled(UiText.Soon); // §13 Q6: shown, disabled, SOON tape until M4
        ColumnRight = x0 + colW;
        DioramaInsetLeft = Mathf.Clamp01((ColumnRight + 32f) / Mathf.Max(1f, W));

        // ---- identity chip (top-right), version (bottom-left), invites (bottom-right)
        BuildIdentityChip(W);
        versionText = UiKit.OutlineLabel(root, "Version", VersionLine(), UiTheme.Small, new Color(1f, 1f, 1f, 0.85f),
                                         TextAlignmentOptions.BottomLeft, false, FontStyles.Bold);
        UiKit.Place(versionText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(x0, 22f), new Vector2(colW + 200f, 30f));
        invites = InviteCards.Build(root, preview);

        // ---- the Join page (a UiStack screen) takes the column's place
        join = JoinScreen.Build(root, new Vector2(Mathf.Min(colW + 76f, W - x0 - 24f), avail), OnJoinCode, OnJoinClosed, preview);
        // if the page still needs more height than the column had (UI 150% on a short screen), it slides up over the logo
        float over = join.Panel.sizeDelta.y - avail;
        float joinTop = over > 0f ? Mathf.Max(top, colTop - over) : colTop;
        joinCoversSplash = joinTop < colTop - 1f;
        ((RectTransform)join.transform).anchoredPosition = new Vector2(x0, -joinTop);

        BuildChain();
        ApplyBusy(IsBusy(), true);
        ApplyInternet(true);
    }

    static void DestroyNow(GameObject go)
    {
        if (Application.isPlaying) { go.SetActive(false); Destroy(go); }
        else DestroyImmediate(go);
    }

    UiButton MakeButton(string label, Action click, UiButtonStyle style, HoverKind kind)
    {
        var b = UiKit.Button(column, label, click, style);
        columnButtons.Add(b);
        Relay(b, kind);
        return b;
    }

    void Relay(UiButton b, HoverKind kind)
    {
        if (kind == HoverKind.None) return;
        var r = b.gameObject.AddComponent<MenuHoverRelay>();
        r.kind = kind;
        r.owner = this;
    }

    void BuildIdentityChip(float W)
    {
        string name = "editor", source = UiText.MenuIdentityDevBuild;
        try
        {
            var id = PlayerIdentity.Local;
            if (id != null)
            {
                if (!string.IsNullOrEmpty(id.DisplayName)) name = id.DisplayName;
                source = id.IsSteam ? UiText.MenuIdentitySteam : UiText.MenuIdentityDevBuild;
            }
        }
        catch (Exception) { /* identity not booted (edit-mode preview) */ }
        idChip = FeelUi.Rect("Identity", root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -24f), new Vector2(320f, 70f));
        FeelUi.Panel(idChip, UiTheme.Plum, 0.6f);
        var av = FeelUi.Rect("Avatar", idChip, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(40f, 0f), new Vector2(50f, 50f));
        FeelUi.Img(av, "Disc", FeelUi.Circle, UiTheme.Tangerine);
        FeelUi.Img(av, "Rim", UiKit.Sprites.Ring, new Color(1f, 1f, 1f, 0.8f));
        UiKit.OutlineLabel(av, "Initial", name.Substring(0, 1).ToUpperInvariant(), 26f, Color.white, TextAlignmentOptions.Center);
        var nameT = UiKit.OutlineLabel(idChip, "Name", name, 24f, Color.white, TextAlignmentOptions.MidlineLeft, false);
        var srcT = UiKit.OutlineLabel(idChip, "Source", source, UiTheme.MinText, new Color(0.82f, 0.8f, 0.88f), TextAlignmentOptions.MidlineLeft, false, FontStyles.Normal);
        float tw = Mathf.Clamp(Mathf.Max(nameT.GetPreferredValues(name).x, srcT.GetPreferredValues(source).x), 80f, 300f);
        nameT.text = FeelUi.Fit(nameT, name, tw);
        idChip.sizeDelta = new Vector2(84f + tw + 24f, 70f);
        UiKit.Place(nameT.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -6f), new Vector2(tw + 6f, 32f));
        UiKit.Place(srcT.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -36f), new Vector2(tw + 6f, 26f));
    }

    static string VersionLine()
    {
        string flavour = Application.isEditor ? "editor" :
#if RUBBISH_PLAYTEST
            "playtest";
#elif DEVELOPMENT_BUILD
            "dev";
#else
            "";
#endif
        return string.Format(UiText.MenuVersionFmt, Application.version, flavour).Replace("  ", " ");
    }

    // ------------------------------------------------------------------ keyboard

    void BuildChain()
    {
        chain.Clear();
        if (card != null && card.Button.interactable) chain.Add(card.Button);
        foreach (var b in columnButtons)
            if (b.gameObject.activeSelf && b.interactable) chain.Add(b);
        // vertical chain, wrapping top ↔ bottom; two-column layouts also link left/right
        for (int i = 0; i < chain.Count; i++)
        {
            var s = chain[i];
            var nav = s.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = chain[(i - 1 + chain.Count) % chain.Count];
            nav.selectOnDown = chain[(i + 1) % chain.Count];
            nav.selectOnLeft = TwoColumns ? nav.selectOnUp : null;
            nav.selectOnRight = TwoColumns ? nav.selectOnDown : null;
            s.navigation = nav;
        }
    }

    /// <summary>Give keyboard focus to the first live control (the Continue card, else Play solo).</summary>
    public void FocusDefault()
    {
        if (chain.Count > 0) UiKit.Focus(chain[0].gameObject);
    }

    // ------------------------------------------------------------------ actions

    bool IsBusy() => (host != null && host.Busy) || NetSession.Busy || NetSession.State == NetSessionState.Leaving;

    void OnContinue()
    {
        if (host == null || IsBusy()) return;
        PlayHonk();
        host.Continue();
    }

    // Nick, 2026-10-09: "first choice should be solo or play with friends THEN a separate choose a save or new menu.
    // same for join. join with save or new". Continue (the card) is still one click on the last save.
    void OnPlaySolo()
    {
        if (host == null || IsBusy()) return;
        BusinessPicker.ShowFor(BusinessPicker.Purpose.Solo, () => { if (host == null || IsBusy()) return; PlayHonk(); host.PlaySolo(); }, () => AfterSavePicker(soloB));
    }

    void OnHost()
    {
        if (host == null || IsBusy()) return;
        BusinessPicker.ShowFor(BusinessPicker.Purpose.Host, () => { if (host == null || IsBusy()) return; PlayHonk(); host.HostWithFriends(); }, () => AfterSavePicker(hostB));
    }

    /// <summary>The save step closed (picked or backed out): the Continue card shows whatever is selected now.</summary>
    void AfterSavePicker(UiButton back)
    {
        if (this == null) return;
        card?.Show(ContinueCard.FromLegacySave());
        if (back != null && column != null && column.gameObject.activeInHierarchy) UiKit.Focus(back.gameObject);
    }

    void OnJoin()
    {
        if (join == null || IsBusy()) return;
        column.gameObject.SetActive(false);
        ShowSplash(false);
        join.Show(GameConfig.LastJoinCode);
    }

    /// <summary>The splash sticker hides while a slid-up Join page would cover it (short screens, big UI scale).</summary>
    void ShowSplash(bool on)
    {
        if (title != null && title.SplashRect != null) title.SplashRect.gameObject.SetActive(on || !joinCoversSplash);
    }

    void OnJoinCode(string code)
    {
        if (host == null || IsBusy()) return;
        BusinessPicker.ShowFor(BusinessPicker.Purpose.Join, () => { if (host != null && !IsBusy()) host.Join(code); });
    }

    void OnJoinClosed()
    {
        if (column == null) return;
        ShowSplash(true);
        column.gameObject.SetActive(true);
        UiPop.On(column.gameObject).Play(true);
        if (joinB != null) UiKit.Focus(joinB.gameObject);
    }

    void OnSettings()
    {
        SettingsScreen.Show(() => { if (settingsB != null) UiKit.Focus(settingsB.gameObject); });
    }

    void OnQuit()
    {
        SetHover(HoverKind.Quit);
        UiKit.Confirm(UiText.QuitTitle, UiText.MenuQuitBody, UiText.QuitYes, UiText.QuitNo, yes =>
        {
            if (!yes)
            {
                if (quitB != null) UiKit.Focus(quitB.gameObject);
                return;
            }
            Quit();
        });
    }

    /// <summary>"Yes, I'm tired": quit the game (the Editor logs instead).</summary>
    public static void Quit()
    {
#if UNITY_EDITOR
        Debug.Log("[MainMenu] " + UiText.MenuQuitEditor);
#else
        Application.Quit();
#endif
    }

    void PlayHonk()
    {
        UiTheme.Play(UiTheme.SfxPlay, 0.5f, 1f);
        var d = MenuDiorama.Active;
        if (d != null) d.OnPlayPressed();
    }

    // ------------------------------------------------------------------ hover → diorama

    internal void SetHover(HoverKind k, bool on = true)
    {
        var next = on ? k : (hover == k ? HoverKind.None : hover);
        if (next == hover) return;
        hover = next;
        if (title != null) title.SetFly(hover == HoverKind.Quit);
        var d = MenuDiorama.Active;
        if (d != null) d.SetHover(hover);
    }

    // ------------------------------------------------------------------ frame

    void Update()
    {
        if (preview) return;
        // UI scale or the window changed: rebuild at the new reference size (keeps the Join page and its code)
        var size = root.rect.size;
        if (size.x > 10f && (Mathf.Abs(size.x - builtFor.x) > 1f || Mathf.Abs(size.y - builtFor.y) > 1f)) Rebuild(size);

        bool busy = IsBusy();
        if (busy != busyShown) ApplyBusy(busy, false);
        if (Time.unscaledTime >= nextNetCheck)
        {
            nextNetCheck = Time.unscaledTime + 2f;
            ApplyInternet(false);
        }

        // keyboard users who clicked on nothing: arrows / Tab bring focus back
        var es = EventSystem.current;
        if (es != null && !UiStack.AnyOpen && !NetErrorPanel.IsShowing &&
            (es.currentSelectedGameObject == null || !es.currentSelectedGameObject.activeInHierarchy) &&
            (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.Tab) ||
             Input.GetKeyDown(KeyCode.Return) || GameControls.MenuNavigationRequested))
            FocusDefault();
    }

    void Start()
    {
        if (preview) return;
        FocusDefault();
        if (ReadyAfterSeconds < 0f)
        {
            float now = Time.realtimeSinceStartup;
            ReadyAfterSeconds = now - bootRealtime;
            ReadyBreakdown = $"boot→menu scene awake {awakeAt - bootRealtime:0.00} s, shell build {builtAt - awakeAt:0.00} s, " +
                             $"→ first frame/focus {now - builtAt:0.00} s (diorama build {MenuDiorama.BuildSeconds:0.00} s)";
            Debug.Log($"[MainMenu] interactive {ReadyAfterSeconds:0.00} s after boot ({ReadyBreakdown})");
        }
    }

    void Rebuild(Vector2 size)
    {
        bool joinOpen = join != null && join.IsOpen;
        string code = join != null ? join.Code : "";
        int tab = joinOpen && join.Friends != null ? join.Friends.Index : -1; // a resize keeps the tab the player picked
        if (joinOpen) UiStack.Pop(join);
        Build(size);
        if (joinOpen)
        {
            column.gameObject.SetActive(false);
            ShowSplash(false);
            join.SetCode(code);
            join.Show(code, tab);
        }
        else FocusDefault();
    }

    void ApplyBusy(bool busy, bool force)
    {
        if (!force && busy == busyShown) return;
        busyShown = busy;
        foreach (var b in columnButtons)
        {
            if (b == crewB) continue;
            if (b == hostB && !internetShown) continue;
            b.SetDisabled(busy ? UiText.MenuBusy : null, false);
        }
        if (card != null)
        {
            card.Button.SetDisabled(busy ? UiText.MenuBusy : null, false);
            card.SetBusy(busy);
        }
        if (join != null) join.SetBusy(busy);
        BuildChain();
        if (!busy && !UiStack.AnyOpen && Application.isPlaying)
        {
            var es = EventSystem.current;
            if (es != null && (es.currentSelectedGameObject == null || !es.currentSelectedGameObject.activeInHierarchy)) FocusDefault();
        }
    }

    void ApplyInternet(bool force)
    {
        bool online = preview || Application.internetReachability != NetworkReachability.NotReachable;
        if (!force && online == internetShown) return;
        internetShown = online;
        if (hostB == null) return;
        if (!online) hostB.SetDisabled(UiText.MenuNeedsInternet);
        else if (!busyShown) hostB.SetDisabled(null);
        BuildChain();
    }

    // ------------------------------------------------------------------ preview audit (acceptance UP4-5)

    /// <summary>
    /// Layout audit: texts under 18 px or overflowing, buttons or the card overlapping the logo, anything outside the screen,
    /// the card's text spilling out of the card. Returns the problems ("" = none) and how many texts were checked.
    /// </summary>
    public string Audit(out int checkedTexts)
    {
        Canvas.ForceUpdateCanvases();
        var sb = new StringBuilder();
        checkedTexts = 0;
        foreach (var t in GetComponentsInChildren<TMP_Text>(false))
        {
            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
            checkedTexts++;
            t.ForceMeshUpdate();
            string path = t.transform.parent != null ? t.transform.parent.name + "/" + t.name : t.name;
            if (t.fontSize < UiTheme.MinText - 0.01f) sb.Append($"small {path} {t.fontSize:0.#}px; ");
            if (t.isTextOverflowing) sb.Append($"overflow {path}; ");
            var r = t.rectTransform.rect;
            if (!t.enableAutoSizing && t.textWrappingMode == TextWrappingModes.NoWrap && path.IndexOf("Glyph", StringComparison.Ordinal) < 0)
            {
                float pw = t.GetPreferredValues(t.text, 100000f, 0f).x;
                if (pw > r.width + 2f) sb.Append($"wide {path} {pw:0}>{r.width:0}; ");
            }
        }
        Rect logo = ScreenRect((RectTransform)title.transform);
        Rect splashR = title.SplashRect.gameObject.activeInHierarchy ? ScreenRect(title.SplashRect) : new Rect(-1e6f, -1e6f, 0f, 0f);
        Rect screen = ScreenRect(root);
        var boxes = new List<(string, Rect)>();
        bool columnUp = column != null && column.gameObject.activeInHierarchy;
        if (columnUp) boxes.Add(("card", ScreenRect(card.Button.Rect)));
        foreach (var b in columnButtons)
            if (b.gameObject.activeInHierarchy) boxes.Add((b.Text.text, ScreenRect(b.Rect)));
        if (join != null && join.Panel.gameObject.activeInHierarchy) boxes.Add(("join", ScreenRect(join.Panel)));
        boxes.Add(("identity", ScreenRect(idChip)));
        boxes.Add(("version", ScreenRect(versionText.rectTransform)));
        foreach (var (name, r) in boxes)
        {
            if (r.Overlaps(logo)) sb.Append($"{name} overlaps logo; ");
            if (r.Overlaps(splashR)) sb.Append($"{name} overlaps splash; ");
            if (r.xMin < screen.xMin - 1f || r.xMax > screen.xMax + 1f || r.yMin < screen.yMin - 1f || r.yMax > screen.yMax + 1f)
                sb.Append($"{name} off-screen; ");
        }
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                if (boxes[i].Item2.Overlaps(boxes[j].Item2)) sb.Append($"{boxes[i].Item1} overlaps {boxes[j].Item1}; ");
        // the card's lines (as rendered) stay inside the card and clear of the rank badge
        Rect cardR = ScreenRect(card.Button.Rect);
        if (!columnUp) return sb.ToString();
        var badgeT = card.transform.Find("Visual/Face/RankBadge") as RectTransform;
        Rect badgeR = badgeT != null ? ScreenRect(badgeT) : new Rect(-1e6f, -1e6f, 0f, 0f);
        foreach (var t in card.GetComponentsInChildren<TMP_Text>(false))
        {
            if (string.IsNullOrEmpty(t.text)) continue;
            var tr = RenderedRect(t);
            if (tr.xMin < cardR.xMin - 2f || tr.xMax > cardR.xMax + 2f || tr.yMin < cardR.yMin - 2f || tr.yMax > cardR.yMax + 2f)
                sb.Append($"card text '{t.name}' outside the card; ");
            if (badgeT != null && !t.transform.IsChildOf(badgeT) && tr.Overlaps(badgeR)) sb.Append($"card text '{t.name}' under the rank badge; ");
        }
        return sb.ToString();
    }

    /// <summary>The screen box of the glyphs actually drawn (not the text's rect).</summary>
    Rect RenderedRect(TMP_Text t)
    {
        t.ForceMeshUpdate();
        var b = t.textBounds;
        var tr = t.rectTransform;
        Vector2 p0 = RectTransformUtility.WorldToScreenPoint(uiCam, tr.TransformPoint(b.min));
        Vector2 p1 = RectTransformUtility.WorldToScreenPoint(uiCam, tr.TransformPoint(b.max));
        return Rect.MinMaxRect(Mathf.Min(p0.x, p1.x), Mathf.Min(p0.y, p1.y), Mathf.Max(p0.x, p1.x), Mathf.Max(p0.y, p1.y));
    }

    Rect ScreenRect(RectTransform rt)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(uiCam, c[0]), b = RectTransformUtility.WorldToScreenPoint(uiCam, c[2]);
        // rotated stickers: use the axis-aligned box of all four corners
        float xMin = Mathf.Min(a.x, b.x), xMax = Mathf.Max(a.x, b.x), yMin = Mathf.Min(a.y, b.y), yMax = Mathf.Max(a.y, b.y);
        for (int i = 1; i < 4; i += 2)
        {
            var p = RectTransformUtility.WorldToScreenPoint(uiCam, c[i]);
            xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
        }
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        ReadyAfterSeconds = -1f;
        ReadyBreakdown = "";
        awakeAt = builtAt = 0f;
        bootRealtime = Time.realtimeSinceStartup;
    }
}

/// <summary>Tells the menu which button is hovered or focused (diorama reactions, the fly on Quit).</summary>
public sealed class MenuHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    internal MainMenuCoopUI.HoverKind kind;
    internal MainMenuCoopUI owner;

    public void OnPointerEnter(PointerEventData e) { if (owner != null) owner.SetHover(kind, true); }
    public void OnPointerExit(PointerEventData e) { if (owner != null) owner.SetHover(kind, false); }
    public void OnSelect(BaseEventData e) { if (owner != null) owner.SetHover(kind, true); }
    public void OnDeselect(BaseEventData e) { if (owner != null) owner.SetHover(kind, false); }
}

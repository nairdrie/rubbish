using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Join a friend (Docs/UIPass.md §4.4): FRIENDS PLAYING | INVITES | CODE folder tabs. The first two are UP-5's
/// <see cref="FriendsTabs"/> / <see cref="FriendsPanel"/> (Steam friends feed), hosted here; it opens on INVITES when there
/// are invites, FRIENDS PLAYING when Steam is up, else CODE. On CODE the code is typed onto a <b>truck licence plate</b>: six letter tiles that upper-case themselves,
/// accept only A–Z and 0–9 and take a paste (Ctrl/Cmd+V). [JOIN] hands the code to the menu's join flow (B1's
/// <see cref="NetSession.JoinAsync"/> through <see cref="MenuManager"/>), whose busy and error cards are
/// <see cref="NetErrorPanel"/>'s. A UiStack screen, so Esc goes back to the main page.
/// </summary>
public sealed class JoinScreen : UiScreen
{
    public const int CodeLength = 6;

    RectTransform panel;
    TMP_InputField field;
    TextMeshProUGUI[] tileText;
    Image[] tileLine;
    RectTransform plate;
    TextMeshProUGUI error;
    UiButton joinB, backB;
    Action<string> onJoin;
    Action onClosed;
    float shakeT = 9f, errorT = 9f;
    Vector2 plateHome;
    bool preview;
    FriendsTabs friends;
    GameObject hintGo;

    /// <summary>UP-5's tabs (null if they couldn't be built).</summary>
    public FriendsTabs Friends => friends;

    /// <summary>The code typed so far (upper-case letters and digits).</summary>
    public string Code => field != null ? Normalize(field.text) : "";

    public RectTransform Panel => panel;

    /// <summary>Build the screen (hidden) inside <paramref name="parent"/>, <paramref name="size"/> reference px.</summary>
    public static JoinScreen Build(RectTransform parent, Vector2 size, Action<string> join, Action closed, bool preview = false)
    {
        var rt = FeelUi.Rect("JoinScreen", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, size);
        var s = rt.gameObject.AddComponent<JoinScreen>();
        s.panel = rt;
        s.onJoin = join;
        s.onClosed = closed;
        s.preview = preview;
        s.Make(size);
        rt.gameObject.SetActive(false); // opened with Show (previews switch it on themselves)
        return s;
    }

    void Make(Vector2 size)
    {
        float W = size.x, H = size.y;
        var shadow = FeelUi.Img(panel, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.4f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(8f, -10f);
        var body = FeelUi.Img(panel, "Paper", FeelUi.Rounded, UiTheme.Paper, 0.4f);
        body.raycastTarget = true;
        FeelUi.Img(panel, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.4f);
        panel.localRotation = Quaternion.Euler(0f, 0f, 0.6f);

        var title = UiKit.InkLabel(panel, "Title", UiText.MenuJoinTitle, 44f, UiTheme.Ink, TextAlignmentOptions.MidlineLeft);
        title.characterSpacing = 3f;
        title.enableAutoSizing = true;
        title.fontSizeMax = 44f;
        title.fontSizeMin = 28f;
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -24f), new Vector2(W - 72f, 56f));
        var rule = FeelUi.Img(panel, "Rule", FeelUi.Rounded, UiTheme.StampRed, 0.1f);
        UiKit.Place(rule.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(38f, -84f), new Vector2(160f, 6f));

        var copy = UiKit.InkLabel(panel, "Copy", UiText.MenuJoinCopy, 22f, UiTheme.InkSoft, TextAlignmentOptions.TopLeft, false, FontStyles.Normal);
        copy.textWrappingMode = TextWrappingModes.Normal;
        float copyH = Mathf.Ceil(copy.GetPreferredValues(UiText.MenuJoinCopy, W - 72f, 0f).y) + 4f;
        UiKit.Place(copy.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -100f), new Vector2(W - 72f, copyH));

        // ---- the licence plate (under UP-5's tab strip); it takes what's left of the height (21:9 and big UI scales are
        // short): the hint and the buttons need their room first, and on very short screens the crew copy line goes
        string hintMeasure = UiText.MenuJoinHint + (SteamUp() ? "\n" + UiText.MenuJoinSteamHint : "");
        copy.textWrappingMode = TextWrappingModes.Normal;
        float hintGuess = Mathf.Ceil(copy.GetPreferredValues(hintMeasure, W - 100f, 0f).y * (UiTheme.Small / 22f)) + 4f;
        float fixedH = FriendsTabs.StripHeight + 18f + 16f + hintGuess + 152f;
        if (H - (100f + copyH + 8f) - fixedH < 300f * 0.36f)
        {
            copy.gameObject.SetActive(false);
            copyH = -10f;
        }
        float stripTop = 100f + copyH + 8f;
        float plateTop = stripTop + FriendsTabs.StripHeight + 18f;
        float roomH = H - stripTop - fixedH;
        float plateW = Mathf.Clamp(Mathf.Min(W - 92f, 500f, roomH / 0.36f), 300f, 500f), plateH = Mathf.Round(plateW * 0.36f);
        plate = FeelUi.Rect("Plate", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -plateTop), new Vector2(plateW, plateH));
        plateHome = plate.anchoredPosition;
        var ps = FeelUi.Img(plate, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.35f);
        ps.rectTransform.offsetMin = ps.rectTransform.offsetMax = new Vector2(4f, -6f);
        var plateBody = FeelUi.Img(plate, "Body", FeelUi.Rounded, UiTheme.Hex(0xF7F3E8), 0.35f);
        plateBody.raycastTarget = true;
        var navy = UiTheme.Hex(0x1E3A6E);
        FeelUi.Img(plate, "Rim", UiKit.Sprites.RoundedOutline, navy, 0.35f);
        var inner = FeelUi.Img(plate, "InnerRim", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(navy, 0.5f), 0.3f);
        inner.rectTransform.offsetMin = new Vector2(8f, 8f);
        inner.rectTransform.offsetMax = new Vector2(-8f, -8f);
        var top = UiKit.InkLabel(plate, "Top", UiText.MenuJoinPlateTop, 20f, UiTheme.StampRed, TextAlignmentOptions.Center);
        top.characterSpacing = 8f;
        bool smallPlate = plateH < 150f;
        UiKit.Place(top.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, smallPlate ? -6f : -10f), new Vector2(plateW - 120f, smallPlate ? 22f : 26f));
        if (smallPlate) top.fontSize = UiTheme.MinText;
        var bottom = UiKit.InkLabel(plate, "Bottom", UiText.MenuJoinPlateBottom, UiTheme.MinText, navy, TextAlignmentOptions.Center, true, FontStyles.Bold | FontStyles.Italic);
        bottom.characterSpacing = 6f;
        UiKit.Place(bottom.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, smallPlate ? 4f : 8f), new Vector2(plateW - 120f, smallPlate ? 22f : 24f));
        foreach (var c in new[] { new Vector2(22f, -20f), new Vector2(-22f, -20f), new Vector2(22f, 20f), new Vector2(-22f, 20f) })
        {
            var a = new Vector2(c.x > 0 ? 0f : 1f, c.y < 0 ? 1f : 0f);
            var bolt = FeelUi.Rect("Bolt", plate, a, new Vector2(0.5f, 0.5f), c, new Vector2(18f, 18f));
            FeelUi.Img(bolt, "Img", MenuArt.PlateBolt, Color.white);
        }

        float tileW = Mathf.Floor((plateW - 110f) / CodeLength), tileH = plateH - (smallPlate ? 56f : 76f);
        var tiles = FeelUi.Rect("Tiles", plate, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(tileW * CodeLength + 18f, tileH));
        tileText = new TextMeshProUGUI[CodeLength];
        tileLine = new Image[CodeLength];
        for (int i = 0; i < CodeLength; i++)
        {
            float x = i * tileW + (i >= CodeLength / 2 ? 18f : 0f);
            var tile = FeelUi.Rect("Tile" + i, tiles, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(tileW - 6f, tileH));
            tileText[i] = UiKit.InkLabel(tile, "Char", "", Mathf.Min(64f, tileH * 0.95f), navy, TextAlignmentOptions.Center);
            tileText[i].enableAutoSizing = true; // "W" in a narrow tile, short plates on short screens
            tileText[i].fontSizeMax = Mathf.Max(UiTheme.MinText, Mathf.Min(64f, tileH * 0.95f));
            tileText[i].fontSizeMin = UiTheme.MinText;
            var line = FeelUi.Img(tile, "Line", FeelUi.Rounded, UiTheme.WithAlpha(navy, 0.35f), 0.08f);
            line.rectTransform.anchorMin = new Vector2(0f, 0f);
            line.rectTransform.anchorMax = new Vector2(1f, 0f);
            line.rectTransform.offsetMin = new Vector2(6f, -2f);
            line.rectTransform.offsetMax = new Vector2(-6f, 4f);
            tileLine[i] = line;
        }
        var dot = FeelUi.Rect("Dot", tiles, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(tileW * (CodeLength / 2) + 6f, 0f), new Vector2(10f, 10f));
        FeelUi.Img(dot, "Img", FeelUi.Circle, UiTheme.WithAlpha(navy, 0.6f));

        // the real input: an invisible TMP field over the plate (it owns focus, typing, paste and Enter)
        var area = FeelUi.Stretch("Input", plate);
        area.gameObject.AddComponent<RectMask2D>();
        var hidden = UiKit.InkLabel(area, "Text", "", 30f, new Color(0f, 0f, 0f, 0f), TextAlignmentOptions.Center, false);
        field = plate.gameObject.AddComponent<TMP_InputField>();
        field.textViewport = area;
        field.textComponent = hidden;
        field.targetGraphic = plateBody;
        field.characterLimit = CodeLength;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.onValidateInput = (text, index, ch) => Accept(ch);
        field.customCaretColor = true;
        field.caretColor = new Color(0f, 0f, 0f, 0f);
        field.selectionColor = new Color(0f, 0f, 0f, 0f);
        field.richText = false;
        field.restoreOriginalTextOnEscape = false;
        field.resetOnDeActivation = false;
        var colors = field.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 0.98f, 0.9f);
        colors.selectedColor = Color.white;
        colors.pressedColor = new Color(0.95f, 0.92f, 0.85f);
        field.colors = colors;
        field.onValueChanged.AddListener(_ => { errorT = 9f; Refresh(); });
        field.onSubmit.AddListener(_ => TryJoin());

        // hints, error line and buttons
        float y = plateTop + plateH + 16f;
        string hintText = UiText.MenuJoinHint + (SteamUp() ? "\n" + UiText.MenuJoinSteamHint : "");
        var hint = UiKit.InkLabel(panel, "Hint", hintText, UiTheme.Small, UiTheme.InkSoft, TextAlignmentOptions.TopLeft, false, FontStyles.Normal);
        hint.textWrappingMode = TextWrappingModes.Normal;
        float hintH = Mathf.Ceil(hint.GetPreferredValues(hintText, W - 100f, 0f).y) + 2f;
        UiKit.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(50f, -y), new Vector2(W - 100f, hintH));
        hintGo = hint.gameObject;
        error = UiKit.InkLabel(panel, "Error", "", 22f, UiTheme.StampRed, TextAlignmentOptions.BottomLeft, false);
        UiKit.Place(error.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(36f, 104f), new Vector2(W - 72f, 30f));

        backB = UiKit.Button(panel, UiText.Back, () => Close(), UiButtonStyle.Paper);
        backB.Size(200f, 68f);
        UiKit.Place(backB.Rect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(36f, 26f), new Vector2(200f, 68f));
        joinB = UiKit.Button(panel, UiText.MenuJoinButton, TryJoin, UiButtonStyle.Go);
        joinB.Size(240f, 72f);
        UiKit.Place(joinB.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-36f, 24f), new Vector2(240f, 72f));

        // keyboard: plate ↓ Join ↔ Back ↑ plate
        var fn = field.navigation; fn.mode = Navigation.Mode.Explicit; fn.selectOnDown = joinB; field.navigation = fn;
        var jn = joinB.navigation; jn.mode = Navigation.Mode.Explicit; jn.selectOnUp = field; jn.selectOnLeft = backB; joinB.navigation = jn;
        var bn = backB.navigation; bn.mode = Navigation.Mode.Explicit; bn.selectOnUp = field; bn.selectOnRight = joinB; backB.navigation = bn;

        // the panel must hold everything: grow it if the copy wrapped long
        float need = y + hintH + 150f;
        if (need > H) panel.sizeDelta = new Vector2(W, need);

        // UP-5: FRIENDS PLAYING | INVITES | CODE; the plate, hint and JOIN show on CODE only (drawn above the page)
        try
        {
            friends = FriendsTabs.Build(panel, stripTop, 104f, 24f, new[] { plate.gameObject, hintGo, joinB.gameObject }, field, 3);
            friends.Changed += OnTab;
            var tabs = friends.Tabs.Buttons;
            fn = field.navigation; fn.selectOnUp = tabs.Count > FriendsTabs.CodeIndex ? tabs[FriendsTabs.CodeIndex] : null; field.navigation = fn;
            for (int i = 0; i < tabs.Count; i++)
            {
                var tn = tabs[i].navigation;
                tn.mode = Navigation.Mode.Explicit;
                tn.selectOnLeft = i > 0 ? tabs[i - 1] : null;
                tn.selectOnRight = i < tabs.Count - 1 ? tabs[i + 1] : null;
                tabs[i].navigation = tn;
            }
        }
        catch (Exception e)
        {
            // the code tab alone still works
            friends = null;
            Debug.LogException(e);
        }
        Refresh();
    }

    static bool SteamUp()
    {
        try { return SteamBootstrap.IsInitialized; }
        catch (Exception) { return false; }
    }

    static char Accept(char ch)
    {
        if (ch >= 'a' && ch <= 'z') return char.ToUpperInvariant(ch);
        if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return ch;
        return '\0'; // spaces, dashes, junk from a paste: dropped
    }

    static string Normalize(string s)
    {
        string n = SessionKeys.NormalizeJoinCode(s);
        return n.Length > CodeLength ? n.Substring(0, CodeLength) : n;
    }

    /// <summary>Preview / tests: put <paramref name="code"/> on the plate.</summary>
    public void SetCode(string code)
    {
        if (field == null) return;
        field.SetTextWithoutNotify(Normalize(code));
        Refresh();
    }

    void Refresh()
    {
        string c = Code;
        int caret = Mathf.Min(c.Length, CodeLength - 1);
        bool focused = field != null && field.isFocused;
        for (int i = 0; i < CodeLength; i++)
        {
            tileText[i].text = i < c.Length ? c[i].ToString() : "";
            bool next = i == caret && c.Length < CodeLength && (focused || preview);
            tileLine[i].color = next ? UiTheme.Gold : UiTheme.WithAlpha(UiTheme.Hex(0x1E3A6E), i < c.Length ? 0.6f : 0.3f);
        }
    }

    void TryJoin()
    {
        string c = Code;
        if (c.Length < CodeLength)
        {
            error.text = UiText.MenuJoinNeedCode;
            errorT = 0f;
            shakeT = 0f;
            UiTheme.Play(UiTheme.SfxError, 0.45f, 1f);
            if (field != null) field.ActivateInputField();
            return;
        }
        error.text = "";
        UiTheme.Play(UiTheme.SfxPlay, 0.4f, 1.2f);
        onJoin?.Invoke(c);
    }

    /// <summary>Busy (joining): the buttons and plate stop taking input.</summary>
    public void SetBusy(bool on)
    {
        if (joinB != null) joinB.SetDisabled(on ? UiText.MenuBusy : null, false);
        if (backB != null) backB.SetDisabled(on ? UiText.MenuBusy : null, false);
        if (field != null) field.interactable = !on;
    }

    /// <summary>Open (also prefill the last code typed this run). <paramref name="tab"/>: -1 = the feed's default
    /// (invites → friends → code), else 0 friends playing, 1 invites, 2 code.</summary>
    public void Show(string prefill, int tab = -1)
    {
        if (!string.IsNullOrEmpty(prefill) && string.IsNullOrEmpty(Code)) SetCode(prefill);
        panel.gameObject.SetActive(true);
        if (friends != null) friends.Select(tab >= 0 ? tab : FriendsTabs.DefaultIndex);
        Open(false);
    }

    /// <summary>Preview / tests: show tab <paramref name="i"/> (0 friends playing, 1 invites, 2 code).</summary>
    public void SelectTab(int i)
    {
        if (friends != null) friends.Select(i);
    }

    void OnTab(int i)
    {
        error.text = "";
        if (!IsOpen || !Application.isPlaying) return;
        if (i == FriendsTabs.CodeIndex && field != null) field.ActivateInputField();
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f);
        UiPop.On(panel.gameObject).Play(true);
        error.text = "";
        if (!Application.isPlaying) return;
        var first = friends != null ? friends.FirstSelectable : field;
        if (first != null) UiKit.Focus(first.gameObject);
        if (first == field && field != null) field.ActivateInputField();
    }

    public override void OnHide()
    {
        UiTheme.Play(UiTheme.SfxBack, 0.45f, 1f);
        if (panel != null && Application.isPlaying)
            UiPop.On(panel.gameObject).Play(false, () => { if (panel != null && !IsOpen) panel.gameObject.SetActive(false); });
        else if (panel != null) panel.gameObject.SetActive(false);
        try { onClosed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Update()
    {
        if (preview || !IsOpen) return;
        Refresh();
        float dt = Time.unscaledDeltaTime;
        // the caret tile blinks
        if (field != null && field.isFocused && Code.Length < CodeLength)
        {
            int i = Code.Length;
            var c = tileLine[i].color;
            c.a = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 4f));
            tileLine[i].color = c;
        }
        if (shakeT < 0.35f)
        {
            shakeT += dt;
            plate.anchoredPosition = plateHome + new Vector2(Mathf.Sin(shakeT * 70f) * 10f * (1f - shakeT / 0.35f), 0f);
            if (shakeT >= 0.35f) plate.anchoredPosition = plateHome;
        }
        if (errorT < 4f)
        {
            errorT += dt;
            if (errorT >= 4f) error.text = "";
        }
        // nothing focused (the plate went non-interactable while joining, a card closed, a click on blank paper): give the
        // keyboard back to the page's first control, so "Try again" lands on the plate and arrows always work
        var es = EventSystem.current;
        if (es != null && UiStack.Top == this && !NetErrorPanel.IsShowing && !UiDialog.IsShowing &&
            (es.currentSelectedGameObject == null || !es.currentSelectedGameObject.activeInHierarchy))
        {
            var first = friends != null ? friends.FirstSelectable : field;
            if (first != null && first.gameObject.activeInHierarchy && first.IsInteractable()) UiKit.Focus(first.gameObject);
            else if (backB != null && backB.IsInteractable()) UiKit.Focus(backB.gameObject);
        }
    }
}

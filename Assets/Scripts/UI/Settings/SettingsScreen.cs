using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The settings screen (Docs/UIPass.md §6.1): a manila "Form 27-B" folder with five tabs (AUDIO · CONTROLS · VIDEO ·
/// INTERFACE · ACCESSIBILITY), one row per setting (label, control, grey one-liner), live preview as you move a control,
/// [Reset tab] and [Back] (Back saves). Opened from the main menu (UP-4) and the Esc menu (UP-3) with <see cref="Show"/>;
/// it sits on <see cref="UiLayers.Settings"/> and goes through <see cref="UiStack"/>, so Esc goes back.
/// Display mode and resolution changes ask "Keep these settings? Reverting in 10…" in builds.
/// </summary>
public sealed class SettingsScreen : UiScreen
{
    static SettingsScreen instance;
    static int noteIndex;

    /// <summary>Folder size at 100% (reference px); shrinks to fit small reference sizes (large UI scale, 5:4).</summary>
    public static readonly Vector2 FolderSize = new Vector2(1280f, 800f);

    Canvas canvas;
    RectTransform root, folder, sheet;
    UiTabs tabs;
    ScrollRect list;
    RectTransform content;
    UiButton backB, resetB;
    TextMeshProUGUI stamp;
    float stampT = 99f;
    Action onClosed;
    int tab;
    bool preview, closing;
    Vector2 builtFor;
    readonly List<Selectable> rowControls = new List<Selectable>();
    readonly List<UiRow> rows = new List<UiRow>();
    UiCycler resolutionCycler;
    List<Vector2Int> resolutions = new List<Vector2Int>();

    /// <summary>The screen is open.</summary>
    public static bool IsShowing => instance != null && instance.IsOpen;

    /// <summary>The live screen (null until first shown).</summary>
    public static SettingsScreen Instance => instance;

    /// <summary>The open tab (0 audio … 4 accessibility).</summary>
    public int Tab => tab;

    /// <summary>Open the settings (from the main menu or the Esc menu). <paramref name="onClosed"/> runs after Back.</summary>
    public static SettingsScreen Show(Action onClosed = null, int tab = -1)
    {
        if (instance == null)
        {
            var c = UiKit.Canvas("[Settings Screen]", UiLayers.Settings);
            instance = c.gameObject.AddComponent<SettingsScreen>();
            instance.canvas = c;
            instance.Build(UiScale.ReferenceSize(Screen.width, Screen.height));
        }
        else
        {
            noteIndex++; // a new Head Office note each visit
            instance.Build(UiScale.ReferenceSize(Screen.width, Screen.height));
        }
        instance.onClosed = onClosed;
        if (tab >= 0) instance.ShowTab(tab);
        instance.canvas.enabled = true;
        instance.closing = false;
        instance.stampT = 99f;
        if (instance.stamp != null) instance.stamp.alpha = 0f;
        instance.Open(true);
        return instance;
    }

    /// <summary>
    /// Edit-mode preview for screenshots (no play mode): built under <paramref name="parent"/> and drawn through
    /// <paramref name="cam"/> at the scale a <paramref name="w"/>×<paramref name="h"/> screen would use.
    /// </summary>
    public static SettingsScreen CreatePreview(Transform parent, Camera cam, int w, int h, float uiScale = -1f)
    {
        var c = UiKit.Canvas("[Settings Preview]", UiLayers.Settings, parent);
        var s = c.gameObject.AddComponent<SettingsScreen>();
        s.canvas = c;
        s.preview = true;
        UiKit.PreviewCanvas(c, cam, w, h, uiScale);
        s.Build(UiScale.ReferenceSize(w, h, uiScale));
        return s;
    }

    // ------------------------------------------------------------------ build

    void Build(Vector2 refSize)
    {
        builtFor = refSize;
        root = (RectTransform)transform;
        for (int i = root.childCount - 1; i >= 0; i--) DestroyNow(root.GetChild(i).gameObject);

        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.5f));
        dim.raycastTarget = true;

        float W = Mathf.Min(FolderSize.x, refSize.x - 48f), H = Mathf.Min(FolderSize.y, refSize.y - 40f);
        folder = FeelUi.Rect("Folder", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
        folder.localRotation = Quaternion.Euler(0f, 0f, -0.4f);
        var shadow = FeelUi.Img(folder, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.4f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(10f, -12f);
        var manila = FeelUi.Img(folder, "Manila", FeelUi.Rounded, UiTheme.Hex(0xE3C98F), 0.4f);
        manila.raycastTarget = true;
        FeelUi.Img(folder, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.4f);

        // header: title + form number on the left, a Head Office sticky note on the right (the derpy office touches)
        var title = UiKit.InkLabel(folder, "Title", UiText.SettingsTitle, UiTheme.Heading, UiTheme.Ink, TextAlignmentOptions.MidlineLeft);
        title.characterSpacing = 4f;
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -12f), new Vector2(380f, 70f));
        const float NoteW = 270f;
        float formW = W - 44f - 380f - NoteW - 60f;
        var form = UiKit.InkLabel(folder, "Form", UiText.SettingsForm, UiTheme.Small, UiTheme.Hex(0x5E4B2A), TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic);
        UiKit.Place(form.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f + 380f + 8f, -34f), new Vector2(Mathf.Max(10f, formW), 34f));
        form.gameObject.SetActive(form.GetPreferredValues(UiText.SettingsForm).x <= formW); // narrow folders drop it

        var note = FeelUi.Rect("StickyNote", folder, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-NoteW * 0.5f - 26f, -44f), new Vector2(NoteW, 70f));
        note.localRotation = Quaternion.Euler(0f, 0f, 3.5f);
        var noteShadow = FeelUi.Img(note, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(Color.black, 0.18f), 0.12f);
        noteShadow.rectTransform.offsetMin = noteShadow.rectTransform.offsetMax = new Vector2(3f, -4f);
        FeelUi.Img(note, "Paper", FeelUi.Rounded, UiTheme.Hex(0xFFE873), 0.12f);
        var tape = FeelUi.Img(note, "Tape", UiKit.Sprites.Tape, UiTheme.Hex(0xF6F0DC, 0.85f));
        UiKit.Place(tape.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(90f, 22f));
        tape.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -6f);
        var tip = UiKit.InkLabel(note, "Tip", UiText.StickyNotes[noteIndex % UiText.StickyNotes.Length], 20f, UiTheme.Ink, TextAlignmentOptions.Center, false);
        tip.textWrappingMode = TextWrappingModes.Normal;
        tip.lineSpacing = -6f;
        UiKit.Inset(tip.rectTransform, 12f, 4f, 12f, 8f);

        // tabs first, so the sheet covers their bottoms (the active tab is paper and joins the sheet)
        tabs = UiKit.Tabs(folder, UiText.SettingsTabs, i => ShowTab(i));
        UiKit.Place((RectTransform)tabs.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -88f), new Vector2(W - 68f, 64f));
        tabs.FitTo(W - 68f);

        sheet = FeelUi.Rect("Sheet", folder, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        UiKit.Inset(sheet, 18f, 18f, 18f, 152f);
        var sheetBody = FeelUi.Img(sheet, "Body", FeelUi.Rounded, UiTheme.Paper, 0.3f);
        sheetBody.raycastTarget = true;
        var sheetShadow = FeelUi.Img(sheet, "TopShade", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Ink, 0.06f), 0.3f);
        sheetShadow.rectTransform.offsetMin = new Vector2(0f, 0f);
        sheetShadow.transform.SetAsFirstSibling();

        float sheetW = W - 36f, sheetH = H - 152f - 18f;
        list = UiKit.ScrollList(sheet, new Vector2(sheetW - 24f, sheetH - 16f - 96f));
        UiKit.Place((RectTransform)list.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(sheetW - 24f, sheetH - 16f - 96f));
        content = list.content;

        // footer
        var footRule = FeelUi.Img(sheet, "FootRule", UiKit.Sprites.Dash, UiTheme.WithAlpha(UiTheme.Ink, 0.25f));
        footRule.type = Image.Type.Tiled;
        footRule.rectTransform.anchorMin = new Vector2(0f, 0f);
        footRule.rectTransform.anchorMax = new Vector2(1f, 0f);
        footRule.rectTransform.offsetMin = new Vector2(24f, 94f);
        footRule.rectTransform.offsetMax = new Vector2(-24f, 98f);
        var stain = FeelUi.Img(sheet, "CoffeeStain", FeelUi.Circle, UiTheme.Hex(0x9A6A3A, 0.08f));
        UiKit.Place(stain.rectTransform, new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), new Vector2(-410f, 60f), new Vector2(98f, 98f));
        var coffee = FeelUi.Img(sheet, "Coffee", UiKit.Sprites.CoffeeRing, UiTheme.Hex(0x7A4A22, 0.42f));
        UiKit.Place(coffee.rectTransform, new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), new Vector2(-410f, 60f), new Vector2(112f, 112f));
        resetB = UiKit.Button(sheet, UiText.ResetTab, AskResetTab, UiButtonStyle.Paper);
        resetB.Size(250f, 66f);
        UiKit.Place(resetB.Rect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 14f), new Vector2(250f, 66f));
        backB = UiKit.Button(sheet, UiText.Back, Back, UiButtonStyle.Primary);
        backB.Size(260f, 72f);
        UiKit.Place(backB.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-36f, 12f), new Vector2(260f, 72f));

        stamp = UiKit.InkLabel(sheet, "Stamp", UiText.SettingsSaved, 40f, UiTheme.StampRed, TextAlignmentOptions.Center);
        stamp.characterSpacing = 6f;
        UiKit.Place(stamp.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 48f), new Vector2(300f, 60f));
        stamp.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 8f);
        stamp.alpha = 0f;

        var rn = resetB.navigation; rn.mode = Navigation.Mode.Explicit; rn.selectOnRight = backB; resetB.navigation = rn;
        var bn = backB.navigation; bn.mode = Navigation.Mode.Explicit; bn.selectOnLeft = resetB; backB.navigation = bn;

        tabs.Select(tab, false);
        BuildPage();
    }

    static void DestroyNow(GameObject go)
    {
        if (Application.isPlaying) { go.SetActive(false); Destroy(go); }
        else DestroyImmediate(go);
    }

    // ------------------------------------------------------------------ pages

    /// <summary>Switch to tab <paramref name="i"/> (0 audio … 4 accessibility).</summary>
    public void ShowTab(int i)
    {
        i = Mathf.Clamp(i, 0, UiText.SettingsTabs.Length - 1);
        tab = i;
        if (tabs != null && tabs.Index != i) tabs.Select(i, false);
        BuildPage();
    }

    static string SectionFor(int t) => t switch
    {
        0 => GameSettings.Audio,
        1 => GameSettings.Controls,
        2 => GameSettings.Video,
        3 => GameSettings.Interface,
        _ => GameSettings.Access,
    };

    void BuildPage()
    {
        if (content == null) return;
        for (int i = content.childCount - 1; i >= 0; i--) DestroyNow(content.GetChild(i).gameObject);
        rowControls.Clear();
        rows.Clear();
        resolutionCycler = null;
        var s = GameSettings.Values;
        switch (tab)
        {
            case 0: AudioPage(s); break;
            case 1: ControlsPage(s); break;
            case 2: VideoPage(s); break;
            case 3: InterfacePage(s); break;
            default: AccessPage(s); break;
        }
        // keyboard: tabs ↓ first row … last row ↓ Back
        UiKit.ChainVertical(rowControls, tabs.Buttons.Count > 0 ? tabs.Buttons[tab] : null, backB);
        tabs.SetDown(rowControls.Count > 0 ? rowControls[0] : backB);
        var bn = backB.navigation; bn.selectOnUp = rowControls.Count > 0 ? rowControls[rowControls.Count - 1] : tabs.Buttons[tab]; backB.navigation = bn;
        var rn = resetB.navigation; rn.selectOnUp = bn.selectOnUp; resetB.navigation = rn;
        list.verticalNormalizedPosition = 1f;
        content.anchoredPosition = Vector2.zero;
        Canvas.ForceUpdateCanvases();
    }

    T Add<T>(T row) where T : Component
    {
        var r = row.GetComponentInParent<UiRow>();
        if (r != null)
        {
            rows.Add(r);
            if (r.Control != null) rowControls.Add(r.Control);
        }
        return row;
    }

    void Edit(string section, Action<GameSettingsData> change) => GameSettings.Edit(section, change);

    static string Pct(float v) => Mathf.RoundToInt(v) + "%";
    static string Num(float v) => Mathf.RoundToInt(v).ToString();

    void AudioPage(GameSettingsData s)
    {
        Add(UiKit.Slider(content, UiText.Master, 0, 100, s.audio.master, Num, v => Edit(GameSettings.Audio, d => d.audio.master = Mathf.RoundToInt(v))))
            .Row.Describe(UiText.MasterDesc);
        Add(UiKit.Slider(content, UiText.SfxVol, 0, 100, s.audio.sfx, Num, v => Edit(GameSettings.Audio, d => d.audio.sfx = Mathf.RoundToInt(v))))
            .Row.Describe(UiText.SfxVolDesc);
        Add(UiKit.Slider(content, UiText.AmbienceVol, 0, 100, s.audio.ambience, Num, v => Edit(GameSettings.Audio, d => d.audio.ambience = Mathf.RoundToInt(v))))
            .Row.Describe(UiText.AmbienceVolDesc);
        // Music (no track yet, §13 Q5) and Voice chat (M3) stay hidden; their values survive in the file.
        Add(UiKit.Toggle(content, UiText.MuteUnfocused, s.audio.muteUnfocused, v => Edit(GameSettings.Audio, d => d.audio.muteUnfocused = v)))
            .Row.Describe(UiText.MuteUnfocusedDesc);
    }

    void ControlsPage(GameSettingsData s)
    {
        var sens = Add(UiKit.Slider(content, UiText.LookSens, 0.25f, 3f, s.controls.lookSensitivity, v => v.ToString("0.00") + "×",
            v => Edit(GameSettings.Controls, d => d.controls.lookSensitivity = v)));
        sens.Step = 0.05f;
        sens.Row.Describe(UiText.LookSensDesc);
        Add(UiKit.Toggle(content, UiText.InvertY, s.controls.invertY, v => Edit(GameSettings.Controls, d => d.controls.invertY = v)))
            .Row.Describe(UiText.InvertYDesc);
        Add(UiKit.Slider(content, UiText.Fov, 50, 90, GameSettings.ShownFov, v => Mathf.RoundToInt(v) + "°",
            v => Edit(GameSettings.Controls, d => d.controls.fov = Mathf.RoundToInt(v))))
            .Row.Describe(UiText.FovDesc);

        UiKit.Heading(content, "CONTROLLER");
        var padNote = UiKit.Label(NoteRow(36f), "Pad note", "Xbox labels shown. PlayStation: A=Cross, B=Circle, X=Square, Y=Triangle.",
                                  UiTheme.Small, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic, UiTheme.InkSoft);
        UiKit.Inset(padNote.rectTransform, UiKit.RowPadX + 14f, 0f, UiKit.RowPadX, 0f);
        var padGroups = Keybinds.ControllerGroups;
        bool twoColumns = (content.rect.width > 100f ? content.rect.width : 1180f) >= 1100f;
        for (int g = 0; g < padGroups.Count; g += twoColumns ? 2 : 1)
            KeyGroupPair(padGroups[g], twoColumns && g + 1 < padGroups.Count ? padGroups[g + 1] : (Keybinds.Group?)null);

        UiKit.Heading(content, "KEYBOARD & MOUSE");
        var note = UiKit.Label(NoteRow(36f), "Note", UiText.RebindingSoon, UiTheme.Small, TextAlignmentOptions.MidlineLeft, false,
                               FontStyles.Italic, UiTheme.InkSoft);
        UiKit.Inset(note.rectTransform, UiKit.RowPadX + 14f, 0f, UiKit.RowPadX, 0f);
        var groups = Keybinds.Groups;
        for (int g = 0; g < groups.Count; g += twoColumns ? 2 : 1)
            KeyGroupPair(groups[g], twoColumns && g + 1 < groups.Count ? groups[g + 1] : (Keybinds.Group?)null);
    }

    RectTransform NoteRow(float h)
    {
        var rt = FeelUi.Rect("Note", content, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(900f, h));
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = h;
        return rt;
    }

    void KeyGroupPair(Keybinds.Group a, Keybinds.Group? b)
    {
        const float LineH = 50f, TitleH = 44f;
        int n = Mathf.Max(a.binds.Length, b.HasValue ? b.Value.binds.Length : 0);
        float h = TitleH + n * LineH + 16f;
        var pair = NoteRow(h);
        pair.name = "Keys " + a.title;
        float fullW = content.rect.width > 100f ? content.rect.width : 1180f;
        float colW = b.HasValue ? fullW * 0.5f : Mathf.Min(fullW, 640f);
        KeyBlock(pair, a, 0f, colW);
        if (b.HasValue) KeyBlock(pair, b.Value, colW, colW);

        void KeyBlock(RectTransform parent, Keybinds.Group grp, float x, float w)
        {
            var title = UiKit.Label(parent, "Group", grp.title.ToUpperInvariant(), 20f, TextAlignmentOptions.BottomLeft, true,
                                    FontStyles.Bold, UiTheme.StampRed);
            title.characterSpacing = 4f;
            UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x + UiKit.RowPadX + 14f, -6f), new Vector2(w - 60f, TitleH - 10f));
            for (int i = 0; i < grp.binds.Length; i++)
            {
                var bind = grp.binds[i];
                float y = -TitleH - i * LineH;
                var cap = UiKit.KeyCap(parent, bind.keys);
                UiKit.Place(cap, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(x + UiKit.RowPadX + 14f, y - LineH * 0.5f), new Vector2(0f, 44f));
                const float CapCol = 176f;
                string act = string.IsNullOrEmpty(bind.gate) ? bind.action : $"{bind.action} <color=#{FeelUi.Hex(UiTheme.InkSoft)}><size=85%><i>({bind.gate})</i></size></color>";
                var t = UiKit.Label(parent, "Action", act, 22f, TextAlignmentOptions.MidlineLeft, false, FontStyles.Normal);
                UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(x + UiKit.RowPadX + 14f + CapCol, y - LineH * 0.5f),
                            new Vector2(w - CapCol - UiKit.RowPadX - 24f, LineH));
            }
        }
    }

    void VideoPage(GameSettingsData s)
    {
        if (Application.isEditor)
        {
            var note = UiKit.Label(NoteRow(40f), "EditorNote", "Editor: display mode, resolution, VSync and frame cap are logged, not applied.",
                                   UiTheme.Small, TextAlignmentOptions.MidlineLeft, false, FontStyles.Italic, UiTheme.StampRed);
            UiKit.Inset(note.rectTransform, UiKit.RowPadX + 14f, 0f, UiKit.RowPadX, 0f);
        }
        Add(UiKit.Cycler(content, UiText.DisplayMode, UiText.DisplayModes, (int)s.video.displayMode,
            i => ChangeDisplay(d => d.video.displayMode = (DisplayModeSetting)i))).Row.Describe(UiText.DisplayModeDesc);

        resolutions = SettingsApplier.Resolutions();
        var resNames = new List<string> { UiText.Native };
        int resIndex = 0;
        for (int i = 0; i < resolutions.Count; i++)
        {
            resNames.Add($"{resolutions[i].x} × {resolutions[i].y}");
            if (resolutions[i].x == s.video.width && resolutions[i].y == s.video.height) resIndex = i + 1;
        }
        resolutionCycler = Add(UiKit.Cycler(content, UiText.Resolution, resNames, resIndex, i => ChangeDisplay(d =>
        {
            if (i <= 0 || i > resolutions.Count) { d.video.width = d.video.height = 0; }
            else { d.video.width = resolutions[i - 1].x; d.video.height = resolutions[i - 1].y; }
        })));
        resolutionCycler.Row.Describe(UiText.ResolutionDesc);

        Add(UiKit.Toggle(content, UiText.VSync, s.video.vsync, v => Edit(GameSettings.Video, d => d.video.vsync = v)))
            .Row.Describe(UiText.VSyncDesc);
        var caps = new List<string>();
        int capIndex = SettingsOptions.FrameCaps.Length - 1;
        for (int i = 0; i < SettingsOptions.FrameCaps.Length; i++)
        {
            int c = SettingsOptions.FrameCaps[i];
            caps.Add(c > 0 ? c + " fps" : UiText.Unlimited);
            if (c == s.video.frameCap) capIndex = i;
        }
        Add(UiKit.Cycler(content, UiText.FrameCap, caps, capIndex, i => Edit(GameSettings.Video, d => d.video.frameCap = SettingsOptions.FrameCaps[i])))
            .Row.Describe(UiText.FrameCapDesc);
        UiSlider scaleSlider = null;
        Add(UiKit.Cycler(content, UiText.Quality, UiText.Qualities, (int)s.video.quality, i =>
        {
            Edit(GameSettings.Video, d => { d.video.quality = (QualityPreset)i; d.video.renderScale = 0; });
            if (scaleSlider != null) scaleSlider.SetValueWithoutNotify(SettingsApplier.RenderScaleFor((QualityPreset)i, 0) * 100f);
        })).Row.Describe(UiText.QualityDesc);
        scaleSlider = Add(UiKit.Slider(content, UiText.RenderScale, 50, 100, SettingsApplier.RenderScaleFor(s.video.quality, s.video.renderScale) * 100f, Pct,
            v => Edit(GameSettings.Video, d => d.video.renderScale = Mathf.RoundToInt(v))));
        scaleSlider.Step = 5f;
        scaleSlider.Row.Describe(UiText.RenderScaleDesc);
    }

    /// <summary>Display mode / resolution: apply, then (in builds) ask to keep it, reverting after 10 s.</summary>
    void ChangeDisplay(Action<GameSettingsData> change)
    {
        var before = GameSettings.Values.video;
        var snapshot = new GameSettingsData.VideoS
        {
            displayMode = before.displayMode, width = before.width, height = before.height, vsync = before.vsync,
            frameCap = before.frameCap, quality = before.quality, renderScale = before.renderScale,
        };
        Edit(GameSettings.Video, change);
        if (Application.isEditor || preview) return; // nothing was actually changed in the Editor
        UiKit.Confirm(UiText.KeepDisplayTitle, UiText.KeepDisplayBody, UiText.Keep, UiText.Revert, keep =>
        {
            if (keep) return;
            Edit(GameSettings.Video, d => d.video = snapshot);
            if (this != null && tab == 2) BuildPage();
        }, 10f);
    }

    void InterfacePage(GameSettingsData s)
    {
        var scales = new List<string>();
        int si = 2;
        for (int i = 0; i < SettingsOptions.UiScales.Length; i++)
        {
            scales.Add(SettingsOptions.UiScales[i] + "%");
            if (SettingsOptions.UiScales[i] == s.ui.scale) si = i;
        }
        Add(UiKit.Cycler(content, UiText.UiScaleLabel, scales, si, i => Edit(GameSettings.Interface, d => d.ui.scale = SettingsOptions.UiScales[i])))
            .Row.Describe(UiText.UiScaleDesc);
        Add(UiKit.Cycler(content, UiText.MinimapSizeLabel, UiText.MinimapSizes, (int)s.ui.minimapSize,
            i => Edit(GameSettings.Interface, d => d.ui.minimapSize = (MinimapSize)i))).Row.Describe(UiText.MinimapSizeDesc);
        Add(UiKit.Cycler(content, UiText.MinimapTurn, UiText.MinimapTurns, s.ui.minimapNorthUp ? 1 : 0,
            i => Edit(GameSettings.Interface, d => d.ui.minimapNorthUp = i == 1))).Row.Describe(UiText.MinimapTurnDesc);
        Add(UiKit.Toggle(content, UiText.StreetLabel, s.ui.minimapStreetLabel, v => Edit(GameSettings.Interface, d => d.ui.minimapStreetLabel = v)))
            .Row.Describe(UiText.StreetLabelDesc);
        int mk = s.ui.mapKeyToggle && s.ui.mapKeyPeek ? 0 : s.ui.mapKeyToggle ? 1 : 2;
        Add(UiKit.Cycler(content, UiText.MapKeys, UiText.MapKeyOptions, mk, i => Edit(GameSettings.Interface, d =>
        {
            d.ui.mapKeyToggle = i != 2;
            d.ui.mapKeyPeek = i != 1;
        }))).Row.Describe(UiText.MapKeysDesc);
        Add(UiKit.Toggle(content, UiText.Hints, s.ui.controlHints, v => Edit(GameSettings.Interface, d => d.ui.controlHints = v)))
            .Row.Describe(UiText.HintsDesc);
        int ti = Array.IndexOf(SettingsOptions.ToastTimes, s.ui.toastTime);
        Add(UiKit.Cycler(content, UiText.ToastTimeLabel, new[] { "1×", "1.5×", "2×" }, Mathf.Max(0, ti),
            i => Edit(GameSettings.Interface, d => d.ui.toastTime = SettingsOptions.ToastTimes[i]))).Row.Describe(UiText.ToastTimeDesc);
        Add(UiKit.Toggle(content, UiText.Streamer, s.ui.streamerMode, v => Edit(GameSettings.Interface, d => d.ui.streamerMode = v)))
            .Row.Describe(UiText.StreamerDesc);
    }

    readonly List<Image> legendSwatches = new List<Image>();

    void AccessPage(GameSettingsData s)
    {
        var fx = Add(UiKit.Slider(content, UiText.ScreenFx, 0, 100, s.access.screenFx, Pct, v => Edit(GameSettings.Access, d => d.access.screenFx = Mathf.RoundToInt(v))));
        fx.Step = 5f;
        fx.Row.Describe(UiText.ScreenFxDesc);
        Add(UiKit.Toggle(content, UiText.Colorblind, s.access.colorblind, v =>
        {
            Edit(GameSettings.Access, d => d.access.colorblind = v);
            RefreshLegend();
        })).Row.Describe(UiText.ColorblindDesc);
        Legend();
        Add(UiKit.Toggle(content, UiText.ReduceFlashing, s.access.reduceFlashing, v => Edit(GameSettings.Access, d => d.access.reduceFlashing = v)))
            .Row.Describe(UiText.ReduceFlashingDesc);
    }

    /// <summary>A live freshness legend: four swatches with their shapes, in the current palette.</summary>
    void Legend()
    {
        legendSwatches.Clear();
        var rt = NoteRow(64f);
        rt.name = "Legend";
        float x = UiKit.RowPadX + 14f;
        for (int i = 0; i < 4; i++)
        {
            var chip = FeelUi.Rect("Chip " + i, rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(210f, 48f));
            FeelUi.Img(chip, "Body", FeelUi.Rounded, UiTheme.Hex(0xFFFDF6), 0.3f);
            FeelUi.Img(chip, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.2f), 0.3f);
            var back = FeelUi.Img(chip, "GlyphBack", UiKit.Sprites.Glyph(i), UiTheme.Ink);
            UiKit.Place(back.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(30f, 0f), new Vector2(38f, 38f));
            var sw = FeelUi.Img(chip, "Glyph", UiKit.Sprites.Glyph(i), Color.white);
            UiKit.Place(sw.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(30f, 0f), new Vector2(30f, 30f));
            legendSwatches.Add(sw);
            var t = UiKit.Label(chip, "Name", UiText.FreshnessNames[i], 20f, TextAlignmentOptions.MidlineLeft, false, FontStyles.Bold);
            UiKit.Inset(t.rectTransform, 58f, 0f, 8f, 0f);
            x += 222f;
        }
        RefreshLegend();
    }

    void RefreshLegend()
    {
        for (int i = 0; i < legendSwatches.Count; i++)
        {
            if (legendSwatches[i] == null) continue;
            var st = (FreshnessStage)i;
            legendSwatches[i].color = CurbTrash.StageColor(st);
            // shapes only matter (and only show) in colour-blind mode; otherwise a plain dot
            legendSwatches[i].sprite = GameSettings.ColorblindMarkers ? UiKit.Sprites.Glyph(i) : UiKit.Sprites.Disc;
            var back = legendSwatches[i].transform.parent.Find("GlyphBack")?.GetComponent<Image>();
            if (back != null) back.sprite = legendSwatches[i].sprite;
        }
    }

    // ------------------------------------------------------------------ footer

    void AskResetTab()
    {
        string name = UiText.SettingsTabs[tab].ToLowerInvariant();
        UiKit.Confirm(UiText.ResetTabTitle, string.Format(UiText.ResetTabBody, name), UiText.ResetTab, UiText.NeverMind, yes =>
        {
            if (!yes) return;
            GameSettings.Reset(SectionFor(tab));
            if (this != null) BuildPage();
        });
    }

    /// <summary>Save, thunk the FILED! stamp, close, and hand back to whoever opened it.</summary>
    public void Back()
    {
        if (closing) return;
        closing = true;
        bool saved = GameSettings.SaveNow();
        UiTheme.Play(saved ? UiTheme.SfxConfirm : UiTheme.SfxError, 0.45f, 1f);
        stampT = 0f;
        if (stamp != null) stamp.text = saved ? UiText.SettingsSaved : UiText.SettingsNotSaved;
        if (!Application.isPlaying || preview) Close();
        // else Update closes after the stamp lands (0.3 s)
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f);
        UiPop.On(folder.gameObject).Play(true);
        if (tabs != null && tabs.Buttons.Count > tab) UiKit.Focus(tabs.Buttons[tab].gameObject);
    }

    public override void OnHide()
    {
        var cb = onClosed;
        onClosed = null;
        if (folder != null && Application.isPlaying)
            UiPop.On(folder.gameObject).Play(false, () => { if (canvas != null && !IsOpen) canvas.enabled = false; });
        else if (canvas != null) canvas.enabled = false;
        try { cb?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public override bool OnBack()
    {
        Back();
        return true;
    }

    void Update()
    {
        if (preview || !IsOpen) return;
        // UI scale or the window changed under us: rebuild at the new reference size (keeps the open tab)
        var size = root.rect.size;
        if (size.x > 10f && (Mathf.Abs(size.x - builtFor.x) > 1f || Mathf.Abs(size.y - builtFor.y) > 1f))
        {
            Build(size);
            if (tabs.Buttons.Count > tab) UiKit.Focus(tabs.Buttons[tab].gameObject);
        }
        if (GameControls.Down(KeyCode.Q, GameControls.Action.PreviousTab) && !UiDialog.IsShowing) { tabs.Select((tab + 4) % 5, true); }
        if (GameControls.Down(KeyCode.E, GameControls.Action.NextTab) && !UiDialog.IsShowing) { tabs.Select((tab + 1) % 5, true); }
        KeepSelectionVisible();
        if (stampT < 1.4f)
        {
            stampT += Time.unscaledDeltaTime;
            stamp.alpha = stampT < 1f ? 1f : 1f - (stampT - 1f) / 0.4f;
            stamp.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.6f, 1f, FeelEase.OutBack(Mathf.Clamp01(stampT / 0.2f), 2f));
            if (closing && stampT >= 0.3f) { stampT = 99f; stamp.alpha = 0f; Close(); }
        }
    }

    void KeepSelectionVisible()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null || !sel.transform.IsChildOf(content)) return;
        var row = sel.GetComponentInParent<UiRow>();
        var target = row != null ? (RectTransform)row.transform : (RectTransform)sel.transform;
        var vp = list.viewport;
        var corners = new Vector3[4];
        target.GetWorldCorners(corners);
        Vector3 top = vp.InverseTransformPoint(corners[1]), bottom = vp.InverseTransformPoint(corners[0]);
        float vTop = vp.rect.yMax, vBottom = vp.rect.yMin;
        var pos = content.anchoredPosition;
        if (top.y > vTop) pos.y -= top.y - vTop;
        else if (bottom.y < vBottom) pos.y += vBottom - bottom.y;
        else return;
        float max = Mathf.Max(0f, content.rect.height - vp.rect.height);
        pos.y = Mathf.Clamp(pos.y, 0f, max);
        content.anchoredPosition = Vector2.Lerp(content.anchoredPosition, pos, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
    }

    // ------------------------------------------------------------------ preview + audit (acceptance UP0-5)

    /// <summary>Preview hook: show row <paramref name="index"/> focused (and nothing else).</summary>
    public void PreviewFocusRow(int index)
    {
        for (int i = 0; i < rows.Count; i++) rows[i].PreviewFocus(i == index);
    }

    /// <summary>The scroll position for previews (1 = top, 0 = bottom).</summary>
    public void PreviewScroll(float normalized)
    {
        Canvas.ForceUpdateCanvases();
        list.verticalNormalizedPosition = normalized;
        Canvas.ForceUpdateCanvases();
        var hints = list.GetComponent<UiScrollHints>();
        if (hints != null) hints.Refresh();
    }

    /// <summary>Preview hook: pick the sticky note.</summary>
    public static int NoteIndex { get => noteIndex; set => noteIndex = Mathf.Max(0, value); }

    /// <summary>
    /// Every text on the screen checked against the legibility floor (18 px reference) and for overflow: returns the problems
    /// ("" = none) and the number of texts checked.
    /// </summary>
    public string Audit(out int checkedTexts)
    {
        Canvas.ForceUpdateCanvases();
        var sb = new System.Text.StringBuilder();
        checkedTexts = 0;
        foreach (var t in GetComponentsInChildren<TMP_Text>(true))
        {
            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
            checkedTexts++;
            t.ForceMeshUpdate();
            float used = t.fontSize; // after a mesh update, an auto-sized text's chosen size
            string path = t.transform.parent != null ? t.transform.parent.name + "/" + t.name : t.name;
            if (used < UiTheme.MinText - 0.01f) sb.Append($"small {path} {used:0.#}px; ");
            if (t.enableAutoSizing && t.fontSizeMin < UiTheme.MinText - 0.01f) sb.Append($"small-min {path} {t.fontSizeMin:0.#}px; ");
            if (t.isTextOverflowing) sb.Append($"overflow {path} '{Trim(t.text)}'; ");
            var r = t.rectTransform.rect;
            var pref = t.GetPreferredValues(t.text, t.textWrappingMode == TextWrappingModes.NoWrap ? 100000f : r.width, 0f);
            if (t.textWrappingMode == TextWrappingModes.NoWrap && !t.enableAutoSizing && pref.x > r.width + 1f)
                sb.Append($"wide {path} {pref.x:0}>{r.width:0} '{Trim(t.text)}'; ");
            if (pref.y > r.height + 2f && !t.enableAutoSizing) sb.Append($"tall {path} {pref.y:0}>{r.height:0} '{Trim(t.text)}'; ");
        }
        return sb.ToString();
    }

    static string Trim(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    protected override void OnDestroy()
    {
        if (instance == this) instance = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;
}

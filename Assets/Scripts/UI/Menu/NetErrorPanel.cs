using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Readable network messages (package B1): an error card ("Couldn't join" / "That code didn't work…" / OK) and a busy card
/// ("Joining your friend's game…" / Cancel). Runtime uGUI on its own canvas that survives scene loads, so a message raised
/// while leaving a game is still there in the menu. It listens to <see cref="NetSession.Failed"/> and
/// <see cref="NetSession.Ended"/> by itself; menus call <see cref="Show"/> and <see cref="ShowBusy"/> directly. Nothing is
/// built in batchmode (bots log instead).
/// <para><b>Look (UP-4 restyle, Docs/UIPass.md §4.6; the logic is still B1's):</b> errors are a red sticker with a sad raccoon
/// doodle; busy cards are paper with a spinning bin lid and appear only after 0.3 s, so quick operations don't flash; a
/// session that ended under you (the Boss went home, kicked, lost) is a paper card with a rubber stamp.</para>
/// </summary>
public class NetErrorPanel : MonoBehaviour
{
    static NetErrorPanel instance;
    static NetSessionState lastAttempt = NetSessionState.Idle;

    /// <summary>Seconds a busy card waits before it shows (quick operations never flash it).</summary>
    public const float BusyDelay = 0.3f;

    enum Look { Error, Busy, Paper }

    Canvas canvas;
    RectTransform root, card, spinner;
    TextMeshProUGUI title, body, buttonLabel;
    Button button;
    Action onButton;
    bool busy;
    Look shownLook;
    string busyText;
    float busyShowAt = -1f;
    GameObject focusBefore; // keyboard focus to give back when the card goes (Try again → the plate / JOIN)

    /// <summary>A card (error or busy, including a busy card still inside its 0.3 s delay) is up.</summary>
    public static bool IsShowing => instance != null && instance.canvas != null && (instance.canvas.enabled || instance.busyShowAt > 0f);

    /// <summary>Show an error card with an OK button. Replaces any busy card.</summary>
    public static void Show(string heading, string message) => ShowError(heading, message, UiText.MenuOk);

    /// <summary>An error card whose button reads <paramref name="buttonText"/> ("Try again" after a failed join).</summary>
    internal static void ShowError(string heading, string message, string buttonText)
    {
        Debug.Log($"[NetErrorPanel] {heading}: {message}");
        var p = Ensure();
        if (p == null) return;
        p.busy = false;
        p.busyShowAt = -1f;
        p.Present(Look.Error, heading, message, string.IsNullOrEmpty(buttonText) ? UiText.MenuOk : buttonText, null);
    }

    /// <summary>A paper "session ended" card with a stamp (the Boss went home, kicked, connection lost).</summary>
    internal static void ShowEnded(string heading, string message)
    {
        Debug.Log($"[NetErrorPanel] {heading}: {message}");
        var p = Ensure();
        if (p == null) return;
        p.busy = false;
        p.busyShowAt = -1f;
        p.Present(Look.Paper, heading, message, UiText.MenuOk, null);
    }

    /// <summary>Show a busy card; with <paramref name="onCancel"/> it gets a Cancel button. It appears after
    /// <see cref="BusyDelay"/> seconds unless <see cref="HideBusy"/> comes first.</summary>
    public static void ShowBusy(string message, Action onCancel = null)
    {
        var p = Ensure();
        if (p == null) return;
        p.busy = true;
        p.busyText = message;
        p.Present(Look.Busy, UiText.MenuBusyTitle, message, onCancel != null ? UiText.MenuCancel : null, onCancel);
        if (!p.canvas.enabled) p.busyShowAt = Time.unscaledTime + BusyDelay;
    }

    /// <summary>Hide the busy card (an error card stays up).</summary>
    public static void HideBusy()
    {
        if (instance != null && instance.busy) instance.SetVisible(false);
    }

    public static void Hide()
    {
        if (instance != null) instance.SetVisible(false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        lastAttempt = NetSessionState.Idle;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        NetSession.StateChanged += s =>
        {
            if (s == NetSessionState.Preparing || s == NetSessionState.Joining) lastAttempt = s;
        };
        NetSession.Failed += msg =>
        {
            HideBusy();
            bool join = lastAttempt != NetSessionState.Preparing;
            ShowError(join ? "Couldn't join" : "Couldn't open the game", msg, join ? UiText.MenuTryAgain : UiText.MenuOk);
        };
        NetSession.Ended += msg =>
        {
            HideBusy();
            ShowEnded(TitleForEnd(NetSession.LastEnd), msg);
        };
    }

    static string TitleForEnd(SessionEndReason end)
    {
        if (end.TryGetReject(out var r) && r == JoinReject.HostLeaving) return "Shift over";
        switch (end.Code)
        {
            case SessionEndReason.Kicked: return "Removed from the game";
            case SessionEndReason.Replaced: return "Joined from somewhere else";
            default: return "Connection lost";
        }
    }

    static string StampForEnd(SessionEndReason end)
    {
        if (end.TryGetReject(out var r) && r == JoinReject.HostLeaving) return UiText.MenuStampShiftOver;
        switch (end.Code)
        {
            case SessionEndReason.Kicked: return UiText.MenuStampKicked;
            case SessionEndReason.Replaced: return UiText.MenuStampOops;
            default: return UiText.MenuStampLost;
        }
    }

    static NetErrorPanel Ensure()
    {
        if (Application.isBatchMode || !Application.isPlaying || NetBoot.Quitting) return null;
        if (instance != null) return instance;
        var go = new GameObject("[NetErrorPanel]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<NetErrorPanel>();
        instance.Build();
        return instance;
    }

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000; // above the HUD, menus and dialogs; below the fader and the loading overlay
        canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal |
                                           AdditionalCanvasShaderChannels.Tangent;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        UiScale.Register(scaler); // 1920×1080 ÷ UI scale, match 0.5
        gameObject.AddComponent<GraphicRaycaster>();
        root = (RectTransform)transform;
        canvas.enabled = false;
    }

    // ------------------------------------------------------------------ cards (rebuilt per message: rare, cheap)

    void Present(Look look, string heading, string message, string buttonText, Action action)
    {
        onButton = action;
        BuildCard(look, heading ?? "", SessionEndReason.Clean(message, 400), buttonText,
                  look == Look.Paper ? StampForEnd(NetSession.LastEnd) : null);
        if (look != Look.Busy || canvas.enabled) SetVisible(true);
    }

    void BuildCard(Look look, string heading, string message, string buttonText, string stampText)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            var old = root.GetChild(i).gameObject;
            old.SetActive(false); // Destroy is deferred: never show two cards for a frame
            Destroy(old);
        }
        shownLook = look;
        spinner = null;
        button = null;
        buttonLabel = null;

        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, look == Look.Busy ? 0.45f : 0.6f));
        dim.raycastTarget = true; // swallow clicks meant for the menu behind

        bool hasButton = !string.IsNullOrEmpty(buttonText);
        const float W = 860f;
        float textX = look == Look.Error ? 210f : look == Look.Busy ? 190f : 56f;
        float textW = W - textX - 56f;

        card = FeelUi.Rect("Card", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, 300f));
        var shadow = FeelUi.Img(card, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.5f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(8f, -10f);
        Color bodyColor = look == Look.Error ? UiTheme.StampRed : UiTheme.Paper;
        FeelUi.Img(card, "Body", FeelUi.Rounded, bodyColor, 0.5f);
        if (look == Look.Error)
        {
            // sticker: a white peel border inside the edge
            var peel = FeelUi.Img(card, "Peel", UiKit.Sprites.RoundedOutline, new Color(1f, 1f, 1f, 0.55f), 0.5f);
            peel.rectTransform.offsetMin = new Vector2(10f, 10f);
            peel.rectTransform.offsetMax = new Vector2(-10f, -10f);
            card.localRotation = Quaternion.Euler(0f, 0f, -1.5f);
            var doodleBack = FeelUi.Img(card, "DoodleBack", FeelUi.Circle, new Color(1f, 0.96f, 0.86f, 1f));
            UiKit.Place(doodleBack.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(110f, -110f), new Vector2(150f, 150f));
            var doodle = FeelUi.Img(card, "SadRaccoon", MenuArt.SadRaccoon, Color.white);
            UiKit.Place(doodle.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(110f, -104f), new Vector2(150f, 150f));
            doodle.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 6f);
        }
        else
        {
            FeelUi.Img(card, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.2f), 0.5f);
            card.localRotation = Quaternion.Euler(0f, 0f, look == Look.Busy ? 0.6f : -0.8f);
        }
        if (look == Look.Busy)
        {
            var lid = FeelUi.Rect("BinLid", card, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(98f, 0f), new Vector2(112f, 112f));
            var lidShadow = FeelUi.Img(lid, "Shadow", MenuArt.BinLid, new Color(0f, 0f, 0f, 0.3f));
            lidShadow.rectTransform.offsetMin = lidShadow.rectTransform.offsetMax = new Vector2(4f, -5f);
            var lidImg = FeelUi.Img(lid, "Lid", MenuArt.BinLid, Color.white);
            spinner = lidImg.rectTransform;
        }

        // heading
        title = look == Look.Error
            ? UiKit.OutlineLabel(card, "Title", heading, 44f, Color.white, TextAlignmentOptions.TopLeft)
            : UiKit.InkLabel(card, "Title", heading, look == Look.Busy ? 22f : 44f, look == Look.Busy ? UiTheme.InkSoft : UiTheme.Ink, TextAlignmentOptions.TopLeft);
        if (look == Look.Busy) title.characterSpacing = 6f;
        title.enableAutoSizing = true;
        title.fontSizeMax = title.fontSize;
        title.fontSizeMin = Mathf.Min(title.fontSize, 28f);
        if (look == Look.Busy) title.fontSizeMin = 20f;
        float titleH = look == Look.Busy ? 30f : 58f;

        // body
        body = look == Look.Error
            ? UiKit.OutlineLabel(card, "Body", message, 28f, Color.white, TextAlignmentOptions.TopLeft, false, FontStyles.Normal)
            : UiKit.InkLabel(card, "Body", message, look == Look.Busy ? 32f : 28f, look == Look.Busy ? UiTheme.Ink : UiTheme.InkSoft,
                             TextAlignmentOptions.TopLeft, false, look == Look.Busy ? FontStyles.Bold : FontStyles.Normal);
        body.textWrappingMode = TextWrappingModes.Normal;
        string measure = look == Look.Busy ? message + "..." : message;
        float bodyH = Mathf.Clamp(body.GetPreferredValues(measure, textW, 0f).y + 6f, 40f, 260f);
        float top = look == Look.Busy ? 40f : 44f;
        float buttonsH = hasButton ? 112f : 30f;
        float H = Mathf.Max(look == Look.Busy ? 200f : 270f, top + titleH + 12f + bodyH + buttonsH);
        if (look == Look.Error) H = Mathf.Max(H, 250f + (hasButton ? 30f : 0f));
        card.sizeDelta = new Vector2(W, H);
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(textX, -top), new Vector2(textW, titleH));
        UiKit.Place(body.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(textX, -top - titleH - 12f), new Vector2(textW, bodyH));

        if (!string.IsNullOrEmpty(stampText))
        {
            var stamp = FeelUi.Rect("Stamp", card, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-150f, -30f), new Vector2(250f, 64f));
            stamp.localRotation = Quaternion.Euler(0f, 0f, 9f);
            FeelUi.Img(stamp, "Ring", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.StampRed, 0.85f), 0.25f);
            var st = UiKit.InkLabel(stamp, "Text", stampText, 30f, UiTheme.WithAlpha(UiTheme.StampRed, 0.9f), TextAlignmentOptions.Center);
            st.characterSpacing = 5f;
            st.enableAutoSizing = true;
            st.fontSizeMax = 30f;
            st.fontSizeMin = 20f;
            UiKit.Inset(st.rectTransform, 10f, 4f, 10f, 4f);
            // keep the title clear of the stamp
            title.rectTransform.sizeDelta = new Vector2(textW - 230f, titleH);
        }

        if (hasButton)
        {
            var style = look == Look.Error ? UiButtonStyle.Paper : look == Look.Busy ? UiButtonStyle.Paper : UiButtonStyle.Primary;
            var b = UiKit.Button(card, buttonText, Click, style);
            b.Size(260f, 70f);
            bool right = look == Look.Busy;
            UiKit.Place(b.Rect, new Vector2(right ? 1f : 0.5f, 0f), new Vector2(right ? 1f : 0.5f, 0f), new Vector2(right ? -40f : 0f, 26f), new Vector2(260f, 70f));
            button = b;
            buttonLabel = b.Text;
        }
        UiPop.On(card.gameObject).Play(true);
    }

    /// <summary>A chunky rounded button anchored bottom-centre of <paramref name="parent"/> (shared with the pause menu).</summary>
    internal static Button MakeButton(Transform parent, string name, string label, Vector2 pos, Vector2 size, Color color,
                                      Action onClick, out TextMeshProUGUI text, bool bottom = true)
    {
        var anchor = bottom ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 1f);
        var rt = FeelUi.Rect(name, parent, anchor, anchor, pos, size);
        var shadow = FeelUi.Img(rt, "Shadow", FeelUi.Rounded, FeelUi.ShadowColor, 0.6f);
        shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = new Vector2(3, -4);
        var img = FeelUi.Img(rt, "Body", FeelUi.Rounded, color, 0.6f);
        img.raycastTarget = true;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        if (onClick != null) b.onClick.AddListener(() => onClick());
        text = FeelUi.Text(rt, "Label", 30, Color.white, TextAlignmentOptions.Center);
        text.text = label;
        return b;
    }

    /// <summary>uGUI needs an EventSystem to click anything; scenes usually have one, a bare scene might not.</summary>
    internal static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var es = new GameObject("EventSystem (net)");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }

    void SetVisible(bool on)
    {
        if (canvas == null) return;
        busyShowAt = -1f;
        bool was = canvas.enabled;
        canvas.enabled = on;
        var es = EventSystem.current;
        if (!on)
        {
            // hand keyboard focus back to what had it (a hidden card button would swallow every arrow press)
            if (was && es != null)
            {
                var sel = es.currentSelectedGameObject;
                if (sel == null || sel.transform.IsChildOf(transform))
                    es.SetSelectedGameObject(focusBefore != null && focusBefore.activeInHierarchy ? focusBefore : null);
            }
            focusBefore = null;
            return;
        }
        if (!was && es != null)
        {
            var sel = es.currentSelectedGameObject;
            if (sel != null && !sel.transform.IsChildOf(transform)) focusBefore = sel;
        }
        EnsureEventSystem();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (button != null) UiKit.Focus(button.gameObject);
        UiTheme.Play(shownLook == Look.Busy ? UiTheme.SfxOpen : shownLook == Look.Paper ? UiTheme.SfxConfirm : UiTheme.SfxError, 0.4f, 1f);
    }

    void Click()
    {
        var a = onButton;
        onButton = null;
        SetVisible(false);
        busy = false;
        try { a?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Update()
    {
        if (canvas == null) return;
        if (busyShowAt > 0f && busy && Time.unscaledTime >= busyShowAt) SetVisible(true);
        if (!canvas.enabled) return;
        if (busy)
        {
            int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
            body.text = SessionEndReason.Clean(busyText, 400) + new string('.', dots);
            if (spinner != null) spinner.localRotation = Quaternion.Euler(0f, 0f, -Time.unscaledTime * 240f);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape) ||
            GameControls.PadDown(GameControls.Action.Jump) || GameControls.PadDown(GameControls.Action.Back)) Click();
        if (EventSystem.current == null) EnsureEventSystem();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}

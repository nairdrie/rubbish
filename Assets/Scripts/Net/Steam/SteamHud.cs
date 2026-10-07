using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The small Steam layer on screen (Docs/Multiplayer.md §8.7.4 B3/B10), drawn with IMGUI so it works in every scene with no
/// EventSystem or prefab: the blocking "Rubbish needs Steam" screen (Steam required but not running: no fallback to a dev
/// identity), short status messages ("Joining Nick's shift…", errors), the <b>Invite friends</b> chip while a Steam lobby is
/// open (also <b>F2</b>), and a one-line Steam status at the menu. B14's menu can call <see cref="SteamLobby.OpenInviteDialog"/>
/// itself later; this stays as the fallback.
/// </summary>
[AddComponentMenu("")]
public sealed class SteamHud : MonoBehaviour
{
    static SteamHud instance;
    static string message;
    static bool messageBad;
    static float messageUntil;
    static string requiredError;
    static Func<bool> retry;

    /// <summary>
    /// The UI pass's menus draw their own Steam chrome (Docs/UIPass.md §4.6, §10.6: the identity chip, Invite friends in the Esc
    /// menu, the Join a friend screen): set this to hide the IMGUI <b>invite chip</b> and the menu <b>status line</b>. The
    /// "Rubbish needs Steam" blocker, short messages (<see cref="Say"/>) and F2 always stay. Set it at BeforeSceneLoad or later
    /// (statics reset at SubsystemRegistration).
    /// </summary>
    public static bool SuppressChrome;

    GameObject blocker;
    GUIStyle title, body, small, button, chip;
    float styleScale = -1f;
    Texture2D white;

    /// <summary>Show a short message for a few seconds (also logged).</summary>
    public static void Say(string text, bool bad)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (bad) Debug.LogWarning("[Steam] " + text);
        else Debug.Log("[Steam] " + text);
        message = text;
        messageBad = bad;
        messageUntil = Time.realtimeSinceStartup + (bad ? 8f : 5f);
        Ensure();
    }

    /// <summary>Block the game with the "Rubbish needs Steam" screen. <paramref name="tryAgain"/> returns true once Steam is up.</summary>
    public static void ShowSteamRequired(string error, Func<bool> tryAgain)
    {
        requiredError = string.IsNullOrEmpty(error) ? "Steam couldn't start." : error;
        retry = tryAgain;
        Ensure();
        instance.SetBlocker(true);
    }

    public static bool SteamRequiredShowing => requiredError != null;

    internal static void Ensure()
    {
        if (instance != null || Application.isBatchMode) return;
        var go = new GameObject("[SteamHud]") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SteamHud>();
    }

    void Update()
    {
        if (requiredError != null)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.f2Key.wasPressedThisFrame && SteamBootstrap.IsInitialized) Invite();
    }

    static void Invite()
    {
        if (!SteamLobby.OpenInviteDialog(out string whyNot)) Say(whyNot, false);
    }

    void SetBlocker(bool on)
    {
        if (!on)
        {
            if (blocker != null) Destroy(blocker);
            blocker = null;
            return;
        }
        if (blocker != null) return;
        // A uGUI layer that swallows clicks to the menu underneath; IMGUI draws the screen itself on top.
        blocker = new GameObject("[SteamRequiredBlocker]", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        blocker.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(blocker);
        var canvas = blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var img = new GameObject("Dim", typeof(RectTransform), typeof(Image));
        img.transform.SetParent(blocker.transform, false);
        var rt = (RectTransform)img.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var image = img.GetComponent<Image>();
        image.color = new Color(0.05f, 0.04f, 0.08f, 0.94f);
        image.raycastTarget = true;
    }

    void OnGUI()
    {
        float s = Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
        BuildStyles(s);

        if (requiredError != null) { DrawRequired(s); return; }

        bool chrome = !SuppressChrome && CursorFree();
        if (chrome && SteamLobby.CurrentLobbyId != 0 && SteamBootstrap.IsInitialized) DrawInviteChip(s);
        else if (chrome && NetSession.State == NetSessionState.Idle && !SteamJoinRouter.Busy) DrawStatusLine(s);

        if (message != null && Time.realtimeSinceStartup < messageUntil) DrawMessage(s);
    }

    static bool CursorFree() => Cursor.visible || Cursor.lockState != CursorLockMode.Locked;

    void DrawRequired(float s)
    {
        float w = Mathf.Min(760f * s, Screen.width - 40f), h = 360f * s;
        var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        Panel(r, FeelUiColor(0.11f, 0.09f, 0.17f, 1f));
        var inner = new Rect(r.x + 36f * s, r.y + 28f * s, r.width - 72f * s, r.height - 56f * s);
        GUI.Label(new Rect(inner.x, inner.y, inner.width, 56f * s), "Rubbish needs Steam", title);
        GUI.Label(new Rect(inner.x, inner.y + 64f * s, inner.width, 110f * s), requiredError, body);
        GUI.Label(new Rect(inner.x, inner.y + 170f * s, inner.width, 60f * s),
                  "This build plays with your Steam account so friends can invite you and join from the Steam friends list.", small);

        float bw = 220f * s, bh = 58f * s, by = r.yMax - bh - 28f * s;
        if (Btn(new Rect(r.center.x - bw - 10f * s, by, bw, bh), "Try again"))
        {
            bool ok = false;
            try { ok = retry != null && retry(); }
            catch (Exception e) { Debug.LogException(e); }
            if (ok)
            {
                requiredError = null;
                SetBlocker(false);
                Say("Steam is up. Have fun!", false);
            }
            else if (SteamBootstrap.InitError != null) requiredError = SteamBootstrap.InitError;
        }
        if (Btn(new Rect(r.center.x + 10f * s, by, bw, bh), "Quit")) Application.Quit();
    }

    void DrawInviteChip(float s)
    {
        float w = 360f * s, h = 96f * s;
        var r = new Rect(Screen.width - w - 18f * s, 18f * s, w, h);
        Panel(r, FeelUiColor(0.11f, 0.09f, 0.17f, 0.88f));
        int n = SteamLobby.MemberCount;
        string what = SteamLobby.IsHostLobby ? $"Steam lobby open ({Mathf.Max(1, n)}/{NetConstants.TownCap})" : $"In a Steam lobby ({n})";
        GUI.Label(new Rect(r.x + 16f * s, r.y + 8f * s, r.width - 32f * s, 30f * s), what, chip);
        if (Btn(new Rect(r.x + 16f * s, r.y + 42f * s, r.width - 32f * s, 44f * s), "Invite Steam friends  (F2)")) Invite();
    }

    void DrawStatusLine(float s)
    {
        string line = StatusText();
        if (line == null) return;
        GUI.Label(new Rect(14f * s, Screen.height - 34f * s, Screen.width * 0.6f, 28f * s), line, small);
    }

    /// <summary>One line for the menu: who we are to Steam. Null in the Editor's default dev mode (nothing to say).</summary>
    static string StatusText()
    {
        if (SteamBootstrap.IsInitialized)
            return $"Steam: {SteamBootstrap.LocalName}  ·  invites and Join Game on" + (SteamBootstrap.AppId == SteamPolicy.DevAppId ? "  (test app 480)" : "");
        if (Application.isEditor) return null;
        var id = PlayerIdentity.Local;
        if (id == null) return null;
        return PlayerIdentityBoot.Facts.isPlaytestBuild
            ? $"Steam off ({id.Key}, started with -nosteam). Only join codes work."
            : $"Steam off ({id.Key}). Start with -steam to play with Steam friends.";
    }

    void DrawMessage(float s)
    {
        float w = Mathf.Min(900f * s, Screen.width - 40f);
        float h = body.CalcHeight(new GUIContent(message), w - 40f * s) + 28f * s;
        // Lower third: the top of the screen is where the invite code and the TEST DATA ROOT watermark live.
        var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.70f, w, h);
        Panel(r, messageBad ? FeelUiColor(0.35f, 0.08f, 0.1f, 0.92f) : FeelUiColor(0.11f, 0.09f, 0.17f, 0.9f));
        GUI.Label(new Rect(r.x + 20f * s, r.y + 14f * s, r.width - 40f * s, r.height - 28f * s), message, body);
    }

    static readonly Color Gold = new Color(1f, 0.84f, 0.25f);   // FeelUi.Gold
    static readonly Color Shadow = new Color(0f, 0f, 0f, 0.45f); // FeelUi.ShadowColor

    /// <summary>A chunky gold button with a drop shadow (FeelUi's look, drawn with IMGUI).</summary>
    bool Btn(Rect r, string label)
    {
        float rad = Mathf.Min(r.height * 0.5f, 22f * styleScale);
        bool hover = r.Contains(Event.current.mousePosition);
        Round(new Rect(r.x + 3f, r.y + 4f, r.width, r.height), C(Shadow), rad);
        Round(r, C(hover ? Color.Lerp(Gold, Color.white, 0.3f) : Gold), rad);
        return GUI.Button(r, label, button);
    }

    /// <summary>A rounded panel with FeelUi's offset drop shadow.</summary>
    void Panel(Rect r, Color c)
    {
        float rad = 22f * styleScale;
        Round(new Rect(r.x + 5f, r.y + 6f, r.width, r.height), C(Shadow), rad);
        Round(r, c, rad);
    }

    void Round(Rect r, Color c, float radius) =>
        GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);

    static Color FeelUiColor(float r, float g, float b, float a) => C(new Color(r, g, b, a));

    /// <summary>
    /// IMGUI colours are linear in a Linear-colour-space project; convert the sRGB design colours so they look like FeelUi's.
    /// </summary>
    static Color C(Color srgb) => QualitySettings.activeColorSpace == ColorSpace.Linear ? srgb.linear : srgb;

    void BuildStyles(float s)
    {
        if (title != null && white != null && Mathf.Approximately(styleScale, s)) return;
        styleScale = s;
        if (white == null)
        {
            white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            white.SetPixel(0, 0, Color.white);
            white.Apply();
        }

        title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(40 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = true };
        title.normal.textColor = C(Gold);
        body = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(24 * s), fontStyle = FontStyle.Bold, wordWrap = true, alignment = TextAnchor.UpperLeft };
        body.normal.textColor = Color.white;
        small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * s), wordWrap = true };
        small.normal.textColor = C(new Color(1f, 1f, 1f, 0.75f));
        chip = new GUIStyle(small) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false };
        chip.normal.textColor = C(new Color(0.35f, 1f, 0.45f));

        // Text only: Btn draws the gold background itself.
        button = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(22 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        button.normal.textColor = C(new Color(0.12f, 0.09f, 0.17f));
        button.hover.textColor = button.active.textColor = Color.black;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (blocker != null) Destroy(blocker);
        if (white != null) Destroy(white);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        message = null;
        messageUntil = 0f;
        requiredError = null;
        retry = null;
        SuppressChrome = false;
    }
}

using UnityEngine;
using UnityEngine.UI;    // for Button
using TMPro;             // for TMP_InputField
using System.Threading;

/// <summary>
/// The main menu's actions (package B1) and, since UP-4, the host of the new menu shell (<see cref="MainMenuCoopUI"/>,
/// Docs/UIPass.md §4): <b>Play Solo</b> / <b>Continue</b> (offline, no sign-in), <b>Host with Friends</b> (Unity Relay + a
/// Steam lobby; the join code shows in the HUD and the Esc menu) and <b>Join</b> by code. A guest never loads a scene here:
/// NGO syncs the host's scene. Failures come back as sentences on <see cref="NetErrorPanel"/>.
/// <para>The scene's legacy uGUI buttons ("Host World", "Join World", the code field) are hidden while the shell is up; if the
/// shell can't be built they stay as the fallback, wired exactly as B1 left them.</para>
/// </summary>
public class MenuManager : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private Button hostButton;
    [SerializeField] private Button joinButton;

    [Header("Build Settings")]
    [Tooltip("First scene when hosting or playing solo (empty: HostOptions' default)")]
    [SerializeField] private string startScene = "";

    Button soloButton;
    CancellationTokenSource joinCts;
    bool busy;
    MainMenuCoopUI shell;

    /// <summary>A host or join started from this menu is in flight.</summary>
    public bool Busy => busy;

    void Awake()
    {
        NetSession.MenuOpen = true;
        bool restored = BusinessSlots.RestoreSelection(out string selectionError);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // UP-4: the new menu shell; the legacy buttons stay as the fallback if it can't be built
        if (!Application.isBatchMode)
        {
            try { shell = MainMenuCoopUI.Create(this); }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                shell = null;
            }
        }
        if (!restored) NetErrorPanel.Show("Choose a business", selectionError);
        if (shell != null)
        {
            HideLegacy();
            return;
        }

        soloButton = MakeSoloButton();
        SetLabel(hostButton, "Host with Friends");
        SetLabel(joinButton, "Join");
        if (soloButton != null) soloButton.onClick.AddListener(OnSoloClicked);
        hostButton.onClick.AddListener(OnHostClicked);
        joinButton.onClick.AddListener(OnJoinClicked);
        if (joinCodeInput != null)
        {
            joinCodeInput.characterLimit = SessionKeys.MaxJoinCode;
            if (string.IsNullOrEmpty(joinCodeInput.text) && !string.IsNullOrEmpty(GameConfig.LastJoinCode))
                joinCodeInput.text = GameConfig.LastJoinCode;
            joinCodeInput.onSubmit.AddListener(_ => OnJoinClicked());
        }
    }

    void OnDestroy() => NetSession.MenuOpen = false;

    /// <summary>The legacy scene canvas (Host World / code / Join World) hides under the new shell.</summary>
    void HideLegacy()
    {
        var any = hostButton != null ? (Component)hostButton : joinButton != null ? joinButton : joinCodeInput;
        var c = any != null ? any.GetComponentInParent<Canvas>() : null;
        if (c != null) c.rootCanvas.gameObject.SetActive(false);
    }

    string FirstScene() => string.IsNullOrEmpty(startScene) ? new HostOptions().firstScene : startScene;

    // ------------------------------------------------------------------ actions (the shell calls these)

    /// <summary>Continue the selected business, played solo.</summary>
    public void Continue() => OnSoloClicked();

    /// <summary>Play solo: LocalDirect on this PC, no internet or sign-in needed.</summary>
    public void PlaySolo() => OnSoloClicked();

    /// <summary>Host with friends: Unity Relay join code (+ a Steam lobby when Steam is up).</summary>
    public void HostWithFriends() => OnHostClicked();

    /// <summary>Join a friend by their Relay code.</summary>
    public void Join(string code) => JoinCode(code);

    async void OnSoloClicked()
    {
        if (busy) return;
        if (!CheckBusiness()) return;
        SetBusy(true);
        NetErrorPanel.ShowBusy(UiText.MenuBusyHost);
        bool ok = await NetSession.HostAsync(new HostOptions { mode = NetMode.Offline, firstScene = FirstScene() });
        NetErrorPanel.HideBusy();
        if (!ok && this != null) SetBusy(false);
    }

    async void OnHostClicked()
    {
        if (busy) return;
        if (!CheckBusiness()) return;
        SetBusy(true);
        NetErrorPanel.ShowBusy(UiText.MenuBusyHostFriends);
        bool ok = await NetSession.HostAsync(new HostOptions
        {
            mode = NetMode.UnityRelay,
            maxPlayers = NetConstants.CrewTownCap,
            // Co-op is the one world (Depot Row, N3): start in the town; DepotRowNet adds Depot Row on top.
            firstScene = DepotRowWorld.ForceLegacy ? FirstScene() : DepotRowWorld.TownSceneName,
            steamLobby = true, // B10 opens the lobby from HostStarted when Steam is up
        });
        NetErrorPanel.HideBusy();
        if (!ok && this != null) SetBusy(false);
    }

    void OnJoinClicked() => JoinCode(joinCodeInput != null ? joinCodeInput.text : "");

    bool CheckBusiness()
    {
        if (BusinessSlots.CanPlaySelected) return true;
        NetErrorPanel.Show("Choose a business", BusinessSlots.SelectionError ?? "This save couldn't be read. Click Play Solo and choose a readable save; nothing will be overwritten.");
        return false;
    }

    async void JoinCode(string typed)
    {
        if (busy) return;
        if (!CheckBusiness()) return;
        var code = SessionKeys.NormalizeJoinCode(typed);
        if (string.IsNullOrEmpty(code))
        {
            NetErrorPanel.Show("Join a friend", "Type your friend's invite code first. They see it at the top of their screen and in their Esc menu.");
            return;
        }
        GameConfig.LastJoinCode = code;
        SaveGuard.MarkForeign("join"); // L0: before any scene load; nothing saves until our own data is reloaded

        SetBusy(true);
        joinCts = new CancellationTokenSource();
        NetErrorPanel.ShowBusy(UiText.MenuBusyJoin, () => joinCts?.Cancel());
        bool ok = await NetSession.JoinAsync(JoinTarget.Relay(code), joinCts.Token);
        NetErrorPanel.HideBusy();
        joinCts?.Dispose();
        joinCts = null;
        if (!ok)
        {
            BusinessSlots.ForgetJoinPick();     // the save picked for this join does not answer for the next one
            SaveGuard.ReloadOwn("join failed"); // L0 (NetSession already did; a no-op then)
            if (this != null) SetBusy(false);
        }
    }

    void SetBusy(bool on)
    {
        busy = on;
        if (soloButton != null) soloButton.interactable = !on;
        if (hostButton != null) hostButton.interactable = !on;
        if (joinButton != null) joinButton.interactable = !on;
        if (joinCodeInput != null) joinCodeInput.interactable = !on;
    }

    /// <summary>A copy of the Host button one row above it, labelled Play Solo (legacy fallback only).</summary>
    Button MakeSoloButton()
    {
        if (hostButton == null) return null;
        var go = Instantiate(hostButton.gameObject, hostButton.transform.parent);
        go.name = "SoloButton";
        var b = go.GetComponent<Button>();
        b.onClick.RemoveAllListeners();
        var rt = (RectTransform)go.transform;
        var hostRt = (RectTransform)hostButton.transform;
        float step = joinCodeInput != null
            ? Mathf.Abs(((RectTransform)joinCodeInput.transform).anchoredPosition.y - hostRt.anchoredPosition.y)
            : hostRt.rect.height + 8f;
        if (step < 1f) step = hostRt.rect.height + 8f;
        rt.anchoredPosition = hostRt.anchoredPosition + new Vector2(0f, step);
        SetLabel(b, "Play Solo");
        return b;
    }

    static void SetLabel(Button b, string text)
    {
        if (b == null) return;
        var label = b.GetComponentInChildren<TMP_Text>(true);
        if (label == null) return;
        // One line that shrinks to fit the scene's small buttons ("Host with Friends" would otherwise wrap out of them).
        float size = label.fontSize;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.enableAutoSizing = true;
        label.fontSizeMax = size;
        label.fontSizeMin = Mathf.Min(size, 10f);
        label.text = text;
    }
}

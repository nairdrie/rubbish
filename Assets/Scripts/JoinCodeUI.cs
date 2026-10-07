using UnityEngine;

/// <summary>
/// The invite code on the HUD while hosting friends over Unity Relay (package B1). Reads <see cref="NetSession.JoinCode"/>
/// whenever it shows (so it's right after every scene change), hides itself when there's no code (solo, guests) or the code
/// is hidden (streamers).
/// <para><b>Retired as a source of truth (UP-4, Docs/UIPass.md §6.2, §13 Q7):</b> whether the code is hidden now forwards to
/// the Interface → <b>Streamer mode</b> setting (<see cref="GameSettings.StreamerMode"/>, on by default). Show/Hide in an Esc
/// menu sets a per-run override through <see cref="HiddenByPlayer"/>, which lasts until the setting changes or the game
/// restarts. The pause menu (UP-3) reads the same property, so the HUD line and the Esc card always agree.</para>
/// </summary>
public class JoinCodeUI : MonoBehaviour
{

    [SerializeField] TMPro.TMP_Text joinCodeText;

    /// <summary>
    /// The code is hidden on screen: the player's Show/Hide choice this run, else the Streamer mode setting (default on).
    /// </summary>
    public static bool HiddenByPlayer
    {
        get => overridden ? hidden : StreamerDefault();
        set
        {
            bool was = HiddenByPlayer;
            overridden = true;
            hidden = value;
            if (was != value) Changed?.Invoke();
        }
    }

    static bool hidden, overridden, hooked;
    static event System.Action Changed;

    static bool StreamerDefault()
    {
        try { return GameSettings.StreamerMode; }
        catch (System.Exception) { return true; } // settings not loaded (edit mode, tests): hide, the safe default
    }

    static void OnSettingsChanged(string section)
    {
        if (section != GameSettings.Interface && section != GameSettings.All) return;
        overridden = false; // the setting is the answer again
        Changed?.Invoke();
    }

    void OnEnable()
    {
        if (!hooked)
        {
            hooked = true;
            GameSettings.Changed += OnSettingsChanged;
        }
        NetSession.JoinCodeReady += UpdateText;
        NetSession.StateChanged += OnState;
        Changed += Refresh;
        Refresh();
    }

    void OnDisable()
    {
        NetSession.JoinCodeReady -= UpdateText;
        NetSession.StateChanged -= OnState;
        Changed -= Refresh;
    }

    private void UpdateText(string joinCode) => Refresh();

    void OnState(NetSessionState s) => Refresh();

    void Refresh()
    {
        if (joinCodeText == null) return;
        string code = NetSession.IsHost ? NetSession.JoinCode : null;
        // hidden (streamer mode, the default): nothing on the HUD at all; the code lives on the Esc menu's INVITE plate
        joinCodeText.text = string.IsNullOrEmpty(code) || HiddenByPlayer ? "" : $"Invite code: {code}";
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        hidden = false;
        overridden = false;
        hooked = false;
        Changed = null;
    }
}

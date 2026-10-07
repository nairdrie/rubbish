using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The solo freeze behind the Esc menu (Docs/UIPass.md §5.4, D6, §13 Q2: solo freezes, co-op never does).
/// <para><b>Freeze</b> (solo only, <see cref="CanFreeze"/>): <c>Time.timeScale = 0</c> (the one approved <c>timescale</c> allow,
/// in <see cref="SetTimeScale"/>), <see cref="GameClock.SetHeld"/> (bag ages, the day and the sky stand still: the clock runs on
/// real time, so timeScale alone wouldn't stop it) and <see cref="AudioListener.pause"/> (UI sounds keep playing: Feel's 2D pool
/// ignores the listener pause). <see cref="FeelJuice.FrozenCheck"/> keeps hit stops away from timeScale while frozen. The
/// pause menu's own <see cref="UiStack"/> entry holds the input lock.</para>
/// <para><b>Unfreeze</b> undoes each step on its own (one failing step never leaves the others stuck) and always ends at
/// <c>timeScale == 1</c>. A watchdog unfreezes on a scene change, when the session stops being solo or ends, on quit (and
/// leaving play mode), and whenever <see cref="Frozen"/> is set while the pause menu is closed (an exception inside the menu).
/// While frozen it also re-asserts timeScale 0 if anything else changed it.</para>
/// Co-op (hosting friends, or a guest) never freezes: the world is shared (Multiplayer.md rule 4).
/// </summary>
public static class PauseService
{
    /// <summary>The world is frozen (solo pause).</summary>
    public static bool Frozen { get; private set; }

    /// <summary>
    /// A freeze is allowed: a solo session (LocalDirect refusing remote clients, nobody else connected), or no network session
    /// at all (offline dev). Hosting friends (even alone) and guests never freeze.
    /// </summary>
    public static bool CanFreeze
    {
        get
        {
            var nm = NetBoot.Manager;
            bool listening = nm != null && nm.IsListening;
            if (!listening) return !NetSession.InSession && NetSession.State != NetSessionState.Joining;
            if (!NetSession.IsSolo || !nm.IsServer) return false;
            return nm.ConnectedClientsIds.Count <= 1;
        }
    }

    /// <summary>Raised after a freeze (true) or an unfreeze (false).</summary>
    public static event Action<bool> FrozenChanged;

    /// <summary>Real seconds the current freeze has lasted (0 when not frozen).</summary>
    public static float FrozenSeconds => Frozen ? Time.realtimeSinceStartup - frozenAt : 0f;

    /// <summary>Why the last unfreeze happened ("resume", "scene change", "quit", "menu closed", …), for tests and logs.</summary>
    public static string LastUnfreezeReason { get; private set; }

    /// <summary>Freezes so far this play session (tests).</summary>
    public static int FreezeCount { get; private set; }

    /// <summary>The local player, for the pause menu's own HUD/input work only (never rules or pay).</summary>
    internal static PlayerController Me => PlayerController.Local; // coop:allow(local) pause menu acts for this machine's player only

    static float frozenAt;
    static bool clockHeld, listenerPaused;
    static Runner runner;

    /// <summary>Freeze the world if <see cref="CanFreeze"/>. Returns true when frozen after the call.</summary>
    public static bool Freeze()
    {
        if (Frozen) return true;
        if (!CanFreeze) return false;
        EnsureRunner();
        Frozen = true;
        frozenAt = Time.realtimeSinceStartup;
        FreezeCount++;
        try
        {
            SetTimeScale(0f);
            clockHeld = GameClock.SetHeld(true);
            listenerPaused = true;
            AudioListener.pause = true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Unfreeze("freeze failed");
            return false;
        }
        Debug.Log($"[PauseService] Frozen (solo pause). clockHeld={clockHeld}");
        Raise(true);
        return true;
    }

    /// <summary>Undo the freeze (no-op when not frozen). Every step runs even if an earlier one throws.</summary>
    public static void Unfreeze(string reason)
    {
        if (!Frozen) return;
        Frozen = false;
        LastUnfreezeReason = string.IsNullOrEmpty(reason) ? "resume" : reason;
        try { SetTimeScale(1f); }
        catch (Exception e) { Debug.LogException(e); }
        finally
        {
            try { if (clockHeld || GameClock.Held) GameClock.SetHeld(false); }
            catch (Exception e) { Debug.LogException(e); }
            clockHeld = false;
            try { if (listenerPaused) AudioListener.pause = false; }
            catch (Exception e) { Debug.LogException(e); }
            listenerPaused = false;
        }
        Debug.Log($"[PauseService] Unfrozen ({LastUnfreezeReason}) after {Time.realtimeSinceStartup - frozenAt:0.0} s.");
        Raise(false);
    }

    /// <summary>The only <c>Time.timeScale</c> write in the UI pass (lint allow approved by the lead for the solo pause).</summary>
    static void SetTimeScale(float v)
    {
        Time.timeScale = v; // coop:allow(timescale) solo pause only; co-op never pauses (UIPass Q2, lead-approved 2026-10-01)
    }

    static void Raise(bool frozen)
    {
        try { FrozenChanged?.Invoke(frozen); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ watchdog

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        EnsureRunner();
        // HitStop does nothing while frozen, so its timeScale restore can never unpause the game (UP-1's hook).
        FeelJuice.FrozenCheck = () => Frozen;
    }

    static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying) return;
        var go = new GameObject("[PauseService]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<Runner>();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        NetSession.StateChanged -= OnSessionState;
        NetSession.StateChanged += OnSessionState;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    static void OnActiveSceneChanged(Scene a, Scene b) => Unfreeze("scene change");
    static void OnSceneLoaded(Scene s, LoadSceneMode m) => Unfreeze("scene change");
    static void OnQuitting() => Unfreeze("quit");

    static void OnSessionState(NetSessionState s)
    {
        if (s != NetSessionState.Hosting) Unfreeze("session " + s.ToString().ToLowerInvariant());
    }

    /// <summary>Called every frame by the runner (unscaled).</summary>
    internal static void Watch()
    {
        if (!Frozen) return;
        if (!PauseMenu.IsOpen) { Unfreeze("menu closed"); return; }
        if (!CanFreeze) { Unfreeze("not solo any more"); return; }
        if (!Mathf.Approximately(Time.timeScale, 0f))
        {
            Debug.LogWarning($"[PauseService] Something set timeScale to {Time.timeScale} during the solo pause; freezing again.");
            SetTimeScale(0f);
        }
        if (!GameClock.Held) clockHeld = GameClock.SetHeld(true);
        if (!AudioListener.pause) AudioListener.pause = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Frozen = false;
        clockHeld = listenerPaused = false;
        frozenAt = 0f;
        runner = null;
        FrozenChanged = null;
        LastUnfreezeReason = null;
        FreezeCount = 0;
    }

    sealed class Runner : MonoBehaviour
    {
        void Update() => Watch();
        void OnApplicationQuit() => Unfreeze("quit");
        void OnDestroy()
        {
            Unfreeze("runner destroyed");
            if (runner == this) runner = null;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

/// <summary>A UI-pass screen (pause, settings, map, dialogs, menu pages). Opened and closed through <see cref="UiStack"/>.</summary>
public abstract class UiScreen : MonoBehaviour
{
    /// <summary>Called by <see cref="UiStack.Push"/> after the screen is on the stack.</summary>
    public virtual void OnShow() { }
    /// <summary>Called by <see cref="UiStack.Pop"/> after the screen left the stack.</summary>
    public virtual void OnHide() { }
    /// <summary>Esc / Back while this is the top screen. True = handled; false = the stack closes it.</summary>
    public virtual bool OnBack() => false;

    /// <summary>This screen is on the stack.</summary>
    public bool IsOpen => UiStack.Contains(this);

    /// <summary>Push this screen (no-op if already open).</summary>
    public void Open(bool lockGameplay) => UiStack.Push(this, lockGameplay);

    /// <summary>Pop this screen (no-op if not open).</summary>
    public void Close() => UiStack.Pop(this);

    protected virtual void OnDestroy() => UiStack.Pop(this);
}

/// <summary>
/// The UI pass's screen stack and the one Esc router (Docs/UIPass.md §5.1, D5).
/// <para><b>Locks.</b> <see cref="Push"/> with <c>lockGameplay</c> calls <see cref="PlayerController.PushUILock"/> exactly once for
/// that screen (only in a game: menus have no player and keep a free cursor), and <see cref="Pop"/> (or the screen being
/// destroyed) releases it exactly once.</para>
/// <para><b>Esc</b> (runner at execution order +500, so legacy Esc readers have already run this frame). A snapshot of the
/// previous frame is taken in LateUpdate:
/// <c>foreignBlock</c> (<see cref="PlayerController.IsUIBlocking"/> while this stack holds none of the locks: the laptop),
/// <c>focusOwner</c> (<see cref="PlayerController.InteractionFocusOverride"/> claims the local player: the sort station),
/// <c>transitioning</c> (<see cref="SceneController.IsTransitioning"/>) and B1's <see cref="NetErrorPanel"/>. On Esc:
/// (1) if any was true last frame, the legacy owner handles its own Esc and this one is ignored; (2) else the top screen's
/// <see cref="UiScreen.OnBack"/> runs (not handled = closed); (3) else, in a game, <see cref="PauseMenuOpener"/> (UP-3);
/// (4) else, in the menu, <see cref="MenuBack"/> (UP-4).</para>
/// </summary>
public static class UiStack
{
    struct Entry
    {
        public UiScreen screen;
        public bool locked;
    }

    static readonly List<Entry> stack = new List<Entry>();
    static int locksHeld;
    static bool foreignBlockLast, focusOwnerLast, transitioningLast, errorPanelLast;
    static int escHandledFrame = -1;
    static EventSystem ownEventSystem;

    /// <summary>UP-3's pause menu registers here: open it and return true (false = nothing opened).</summary>
    public static Func<bool> PauseMenuOpener;

    /// <summary>UP-4's menu shell registers here: go back one menu page and return true (false = at the root).</summary>
    public static Func<bool> MenuBack;

    /// <summary>A screen was pushed / popped (after OnShow / OnHide).</summary>
    public static event Action<UiScreen> Pushed, Popped;

    /// <summary>The top screen, or null.</summary>
    public static UiScreen Top
    {
        get
        {
            Prune();
            return stack.Count > 0 ? stack[stack.Count - 1].screen : null;
        }
    }

    /// <summary>Any UI-pass screen is open.</summary>
    public static bool AnyOpen
    {
        get
        {
            Prune();
            return stack.Count > 0;
        }
    }

    /// <summary>Gameplay locks this stack holds right now (one per open screen pushed with lockGameplay).</summary>
    public static int LocksHeld => locksHeld;

    /// <summary>Open screens, bottom first (diagnostics).</summary>
    public static int Count
    {
        get
        {
            Prune();
            return stack.Count;
        }
    }

    /// <summary>The router consumed Esc this frame (for anything else that reads Esc after +500).</summary>
    public static bool EscHandledThisFrame => escHandledFrame == Time.frameCount;

    public static bool Contains(UiScreen s)
    {
        if (s == null) return false;
        for (int i = 0; i < stack.Count; i++)
            if (stack[i].screen == s) return true;
        return false;
    }

    /// <summary>Open <paramref name="s"/> on top (no-op if already open). <paramref name="lockGameplay"/> frees the cursor and
    /// freezes the local player's input until it's popped.</summary>
    public static void Push(UiScreen s, bool lockGameplay)
    {
        if (s == null || Contains(s)) return;
        EnsureRunner();
        // In the menus there's no player to freeze and the cursor is already free; PlayerController's lock would re-lock
        // (and hide) the cursor when it's released, so only take it in a game.
        if (PlayerController.Local == null) lockGameplay = false; // coop:allow(local) input lock for the local screen only
        stack.Add(new Entry { screen = s, locked = lockGameplay });
        if (lockGameplay)
        {
            locksHeld++;
            PlayerController.PushUILock();
        }
        if (Application.isPlaying) EnsureEventSystem();
        try { s.OnShow(); }
        catch (Exception e) { Debug.LogException(e); }
        try { Pushed?.Invoke(s); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Close <paramref name="s"/> wherever it is in the stack (no-op if not open). Releases its lock once.</summary>
    public static void Pop(UiScreen s)
    {
        if (ReferenceEquals(s, null)) return;
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(stack[i].screen, s)) continue;
            var e = stack[i];
            stack.RemoveAt(i);
            Release(e);
            if (s != null)
            {
                try { s.OnHide(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            try { Popped?.Invoke(s); }
            catch (Exception ex) { Debug.LogException(ex); }
            return;
        }
    }

    /// <summary>Close everything (scene change, leaving a session).</summary>
    public static void PopAll()
    {
        for (int guard = 0; stack.Count > 0 && guard < 64; guard++) Pop(stack[stack.Count - 1].screen);
    }

    static void Release(Entry e)
    {
        if (!e.locked) return;
        if (locksHeld > 0) locksHeld--;
        PlayerController.PopUILock();
        // The player left while the screen was up (Leave → menu): keep the menu's cursor free.
        if (PlayerController.Local == null && !PlayerController.IsUIBlocking) // coop:allow(local) cursor for the local screen only
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>Destroyed screens (scene unloads) leave the stack and release their locks.</summary>
    static void Prune()
    {
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].screen != null) continue;
            var e = stack[i];
            stack.RemoveAt(i);
            Release(e);
        }
    }

    // ------------------------------------------------------------------ EventSystem

    /// <summary>
    /// uGUI needs exactly one EventSystem. If none is present in the loaded scenes, create one DontDestroyOnLoad with an
    /// <see cref="InputSystemUIInputModule"/>; if a scene brings its own later, ours steps aside.
    /// </summary>
    public static void EnsureEventSystem()
    {
        if (!Application.isPlaying) return;
        if (EventSystem.current != null && EventSystem.current.isActiveAndEnabled) return;
        if (ownEventSystem != null)
        {
            ownEventSystem.gameObject.SetActive(true);
            return;
        }
        if (AnySceneEventSystem(null) != null) return;
        var go = new GameObject("EventSystem (UiStack)");
        UnityEngine.Object.DontDestroyOnLoad(go);
        ownEventSystem = go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    /// <summary>An enabled EventSystem in a loaded scene other than <paramref name="except"/> (no Find*ObjectByType).</summary>
    static EventSystem AnySceneEventSystem(EventSystem except)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var sc = SceneManager.GetSceneAt(i);
            if (!sc.isLoaded) continue;
            foreach (var root in sc.GetRootGameObjects())
            {
                if (!root.activeInHierarchy) continue;
                var es = root.GetComponentInChildren<EventSystem>(false);
                if (es != null && es != except && es.enabled) return es;
            }
        }
        return null;
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        if (ownEventSystem == null || !ownEventSystem.gameObject.activeSelf) return;
        // A scene brought its own EventSystem: ours steps aside (two would log warnings and double-handle input).
        if (AnySceneEventSystem(ownEventSystem) != null) ownEventSystem.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ Esc router

    static UiStackRunner runner;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        EnsureRunner();
    }

    static void EnsureRunner()
    {
        if (runner != null || !Application.isPlaying || Application.isBatchMode) return;
        var go = new GameObject("[UiStack]") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<UiStackRunner>();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    internal static void TickEsc()
    {
        Prune();
        bool esc = Input.GetKeyDown(KeyCode.Escape);
        bool start = GameControls.PadDown(GameControls.Action.Pause);
        bool back = Top != null && GameControls.PadDown(GameControls.Action.Back);
        if (!esc && !start && !back) return;
        HandleEsc();
    }

    /// <summary>Route one Esc press (public for tests; the runner calls it on a real press).</summary>
    public static bool HandleEsc()
    {
        // (1) a legacy owner had the keyboard last frame: it handles its own Esc this frame
        if (foreignBlockLast || focusOwnerLast || transitioningLast || errorPanelLast) return false;
        escHandledFrame = Time.frameCount;

        // (2) the top screen goes back (not handled = close it)
        var top = Top;
        if (top != null)
        {
            bool handled = false;
            try { handled = top.OnBack(); }
            catch (Exception e) { Debug.LogException(e); }
            if (!handled) Pop(top);
            return true;
        }

        // (3) in a game: the pause menu (UP-3); (4) in the menu: back one page (UP-4)
        if (PlayerController.Local != null) // coop:allow(local) input routing for the local screen only
        {
            if (PauseMenuOpener != null)
            {
                try { return PauseMenuOpener(); }
                catch (Exception e) { Debug.LogException(e); }
            }
            escHandledFrame = -1;
            return false;
        }
        if (MenuBack != null)
        {
            try { return MenuBack(); }
            catch (Exception e) { Debug.LogException(e); }
        }
        escHandledFrame = -1;
        return false;
    }

    /// <summary>End-of-frame snapshot for the one-frame look-back.</summary>
    internal static void Snapshot()
    {
        Prune();
        foreignBlockLast = PlayerController.IsUIBlocking && locksHeld == 0;
        var local = PlayerController.Local; // coop:allow(local) input routing for the local screen only
        bool focus = false;
        if (local != null && PlayerController.InteractionFocusOverride != null)
        {
            try { focus = PlayerController.InteractionFocusOverride(local); }
            catch (Exception) { focus = false; }
        }
        focusOwnerLast = focus;
        transitioningLast = SceneController.IsTransitioning;
        errorPanelLast = NetErrorPanel.IsShowing;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        stack.Clear();
        locksHeld = 0;
        foreignBlockLast = focusOwnerLast = transitioningLast = errorPanelLast = false;
        escHandledFrame = -1;
        ownEventSystem = null;
        runner = null;
        PauseMenuOpener = null;
        MenuBack = null;
        Pushed = null;
        Popped = null;
    }
}

/// <summary>Hidden DontDestroyOnLoad host for <see cref="UiStack"/>'s Esc router (after the legacy Esc readers).</summary>
[DefaultExecutionOrder(500)]
public sealed class UiStackRunner : MonoBehaviour
{
    void Update() => UiStack.TickEsc();
    void LateUpdate() => UiStack.Snapshot();
}

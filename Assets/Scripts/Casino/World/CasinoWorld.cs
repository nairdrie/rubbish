using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The Lucky Raccoon as a place (Docs/Casino.md §2): keeps <c>Casino.unity</c> loaded next to the town, and moves THIS
/// machine's player in and out with a door-style fade and teleport. Nothing is sent to anyone: the player's pose rides its
/// owner-authoritative NetworkTransform, and the pocket shares the world's coordinates, so players inside see each other.
/// <list type="bullet">
/// <item><b>Scene:</b> the host loads <c>Casino</c> additively through NGO whenever the town is up (after Depot Row, never
/// during a door transition), exactly like <see cref="DepotRowNet"/> loads <c>DepotRow</c>; guests and late joiners get it
/// from NGO's scene sync, and the next Single load (solo: back into the garage) unloads it. The scene holds no
/// NetworkObjects. With no session at all (a bare editor test) it is loaded locally.</item>
/// <item><b>Where am I:</b> <see cref="LocalInside"/> is where the player IS (<see cref="CasinoLayout.Contains"/>), not
/// whether a door was used. Lighting, the interior's activation, the minimap, weather and ambience follow it, so a
/// pass-out, Unstuck, a tow or a scene change out of the pocket needs no clean-up call.</item>
/// <item><b>Doors:</b> <see cref="Enter"/> / <see cref="Exit"/>. On foot only; hands must be empty (like the garage door).
/// Vehicles stay on the street.</item>
/// </list>
/// </summary>
public static class CasinoWorld
{
    /// <summary>A door fade is running on this machine.</summary>
    public static bool Transitioning { get; private set; }

    /// <summary>The local player is on the way in (kept true through the fade so the interior is up when it clears).</summary>
    public static bool PendingInside { get; private set; }

    /// <summary>Raised on this machine when its player enters (true) or leaves (false) the pocket, by any means.</summary>
    public static event Action<bool> LocalInsideChanged;

    /// <summary>This machine's own player (camera/HUD/input only, Multiplayer.md §9 rule 8: the local door/teleport).</summary>
    internal static PlayerController Me => PlayerController.Local; // coop:allow(local) the casino door, lighting and HUD state are this machine's own player's

    /// <summary><c>Casino.unity</c> is loaded and its root is up.</summary>
    public static bool SceneReady => CasinoScene.Instance != null;

    /// <summary>The build has the scene at all (false until <c>Rubbish/Setup/Casino</c> has run).</summary>
    public static bool SceneInBuild => Application.CanStreamedLevelBeLoaded(CasinoLayout.SceneName);

    /// <summary>True while this machine's player is in the casino (or fading into it).</summary>
    public static bool LocalInside
    {
        get
        {
            if (PendingInside) return true;
            var me = Me;
            return me != null && CasinoLayout.Contains(me.transform.position);
        }
    }

    /// <summary>Is this player (any machine's) standing in the casino?</summary>
    public static bool IsInside(PlayerController pc) => pc != null && CasinoLayout.Contains(pc.transform.position);

    /// <summary>Is this world point in the casino?</summary>
    public static bool Contains(Vector3 worldPos) => CasinoLayout.Contains(worldPos);

    /// <summary>Where you arrive inside.</summary>
    public static Pose EntrancePose
    {
        get
        {
            var s = CasinoScene.Instance;
            if (s != null && s.entrance != null) return new Pose(s.entrance.position, FlowUtil.Upright(s.entrance.rotation));
            return CasinoLayout.EntrancePose;
        }
    }

    /// <summary>Where you stand after walking out: in front of the street door, facing the street.</summary>
    public static Pose StreetPose => CasinoFront.StreetPose;

    // ------------------------------------------------------------------ doors

    /// <summary>The street door was used. Returns false (with feedback) when it can't be.</summary>
    public static bool Enter(GameObject interactor)
    {
        if (!CanUseDoor(interactor, out var pc)) return false;
        if (!SceneReady)
        {
            GameHUD.Toast(SceneInBuild ? "The Lucky Raccoon is still opening up. Give it a second." : "CLOSED FOR RENOVATIONS", ToastStyle.Info, 2f);
            Sfx.Play("buzz", pc.transform.position, 0.5f);
            return false;
        }
        Run(DoorRoutine(pc, EntrancePose, true));
        return true;
    }

    /// <summary>The door inside was used: back to the street front.</summary>
    public static bool Exit(GameObject interactor)
    {
        if (!CanUseDoor(interactor, out var pc)) return false;
        Run(DoorRoutine(pc, StreetPose, false));
        return true;
    }

    /// <summary>Local, no questions asked (debug menu, respawns): put this machine's player at the entrance.</summary>
    public static bool LocalGoInside()
    {
        var pc = Me;
        if (pc == null || !pc.IsOwner || pc.IsDriving || !SceneReady || Busy) return false;
        Run(DoorRoutine(pc, EntrancePose, true));
        return true;
    }

    /// <summary>Local: put this machine's player in front of the street door.</summary>
    public static bool LocalGoToStreet()
    {
        var pc = Me;
        if (pc == null || !pc.IsOwner || pc.IsDriving || Busy) return false;
        Run(DoorRoutine(pc, StreetPose, false));
        return true;
    }

    static bool Busy => Transitioning || DepotRowWorld.Transitioning || SceneController.IsTransitioning || SceneTransitionData.IsPending;

    static bool CanUseDoor(GameObject interactor, out PlayerController pc)
    {
        pc = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (pc == null || !pc.IsOwner || Busy) return false;
        if (pc.IsDriving) return false; // the door is on-foot only; the truck and the Hoverhog stay outside
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Hands full! Drop it first (G)", ToastStyle.Bad, 2f);
            Sfx.Play("buzz", pc.transform.position, 0.7f);
            return false;
        }
        return true;
    }

    static void Run(IEnumerator routine)
    {
        var host = CasinoRunner.Instance;
        if (host != null && host.isActiveAndEnabled) host.StartCoroutine(Guard(routine));
        else
        {
            // No runner: do it in one step (no fade).
            while (routine.MoveNext()) { }
            EndTransition(false);
        }
    }

    static IEnumerator Guard(IEnumerator inner)
    {
        // Exceptions inside the routine must never leave the screen black.
        while (true)
        {
            object cur;
            try
            {
                if (!inner.MoveNext()) break;
                cur = inner.Current;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                break;
            }
            yield return cur;
        }
        if (Transitioning) EndTransition(true);
    }

    static void EndTransition(bool fadeIn)
    {
        bool was = Transitioning;
        Transitioning = false;
        PendingInside = false;
        if (!was) return;
        FlowCamera.SetCutMode(false);
        if (fadeIn) ScreenFader.FadeIn(ScreenFader.DefaultFadeIn);
    }

    static IEnumerator DoorRoutine(PlayerController pc, Pose target, bool inside)
    {
        Transitioning = true;
        PendingInside = inside;
        Sfx.Play("door", pc.transform.position);
        GameHUD.SetContextHint(null);
        yield return ScreenFader.FadeOut(0.35f, false);
        FlowCamera.SetCutMode(true);

        if (pc == null) yield break;
        if (pc.IsDriving) yield break; // climbed into something during the fade: stay put
        CasinoFront.PulseDoor();
        DepotRowWorld.TeleportPlayer(pc, target.position, target.rotation);
        Sfx.Play("door", target.position, 0.7f, 0.9f);

        // Let the interior switch on (CasinoScene) and the camera catch up while black.
        yield return null;
        yield return new WaitForSecondsRealtime(0.2f);
        FlowCamera.SetCutMode(false);
        PendingInside = false;
        Transitioning = false;
        yield return ScreenFader.FadeIn(0.35f);
    }

    // ------------------------------------------------------------------ scene availability

    static bool loading, missingWarned;
    static float loadingUntil, townSeenAt = -1f;

    /// <summary>Host (or a session-less test): load <c>Casino</c> next to the town when it is missing.</summary>
    internal static void EnsureScene()
    {
        if (NetBoot.Quitting) return;
        bool here = DepotRowWorld.IsSceneLoaded(CasinoLayout.SceneName);
        if (here) { loading = false; return; }
        if (loading && Time.realtimeSinceStartup < loadingUntil) return;
        loading = false;

        if (!DepotRowWorld.IsSceneLoaded(DepotRowWorld.TownSceneName)) { townSeenAt = -1f; return; }
        if (townSeenAt < 0f) townSeenAt = Time.realtimeSinceStartup;
        if (!SceneInBuild)
        {
            if (!missingWarned)
            {
                missingWarned = true;
                Debug.LogWarning("[Casino] Casino.unity isn't in the build settings (run Rubbish/Setup/Casino): The Lucky Raccoon stays shut.");
            }
            return;
        }
        if (Busy) return; // never start a scene event while a door transition is mid-load

        var nm = NetBoot.Manager;
        bool session = nm != null && nm.IsListening;
        if (session && !nm.IsServer) return; // a guest: NGO brings the scene (running session or late-join sync)
        float settled = Time.realtimeSinceStartup - townSeenAt;
        if (settled < 1f) return; // let the town's own load event finish

        if (session && nm.SceneManager != null)
        {
            // Depot Row goes first (the legacy flow keeps the screen black until its street side is up).
            bool depotWanted = Application.CanStreamedLevelBeLoaded(DepotRowLayout.SceneName) && DepotRowNet.Instance != null;
            if (depotWanted && !DepotRowWorld.IsSceneLoaded(DepotRowLayout.SceneName) && settled < 20f) return;
            var st = nm.SceneManager.LoadScene(CasinoLayout.SceneName, LoadSceneMode.Additive);
            if (st == SceneEventProgressStatus.Started)
            {
                loading = true;
                loadingUntil = Time.realtimeSinceStartup + 30f;
                Debug.Log("[Casino] Loading The Lucky Raccoon next to the town.");
                NetAudit.Event("casino", "scene load started");
            }
            // else: NGO is finishing another scene event; the runner asks again in half a second.
            return;
        }

        // No session (a bare test in the editor): a plain local additive load.
        var op = SceneManager.LoadSceneAsync(CasinoLayout.SceneName, LoadSceneMode.Additive);
        if (op != null) { loading = true; loadingUntil = Time.realtimeSinceStartup + 30f; }
    }

    // ------------------------------------------------------------------ the local watch

    static bool wasInside;
    static float fellAt = -1f;

    /// <summary>Every frame on this machine: notice the player entering / leaving the pocket by any means, and catch a
    /// fall through the floor.</summary>
    internal static void Watch()
    {
        var me = Me;
        bool inside = LocalInside;
        if (inside != wasInside)
        {
            wasInside = inside;
            if (!inside) CasinoStation.LocalBusy = null;
            try { LocalInsideChanged?.Invoke(inside); }
            catch (Exception e) { Debug.LogException(e); }
        }

        if (me == null || !me.IsOwner || Transitioning || me.IsDriving) { fellAt = -1f; return; }
        if (CasinoLayout.Under(me.transform.position))
        {
            // Under the room: back to the entrance (or the street when the room is gone).
            if (fellAt < 0f) fellAt = Time.unscaledTime;
            if (Time.unscaledTime - fellAt < 0.25f) return;
            fellAt = -1f;
            Pose to = SceneReady ? EntrancePose : StreetPose;
            DepotRowWorld.TeleportPlayer(me, to.position, to.rotation);
            NetAudit.Event("casino", "caught a fall under the room");
        }
        else fellAt = -1f;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Transitioning = false;
        PendingInside = false;
        LocalInsideChanged = null;
        loading = false;
        missingWarned = false;
        townSeenAt = -1f;
        wasInside = false;
        fellAt = -1f;
    }
}

/// <summary>The casino's heartbeat (DontDestroyOnLoad, created at startup like <see cref="DumpsterRuntime"/> and
/// <see cref="HogRuntime"/>): keeps the scene and the street front up, runs the door fades and the local watch.</summary>
public class CasinoRunner : MonoBehaviour
{
    public static CasinoRunner Instance { get; private set; }
    float nextScan;
    CasinoMusic music;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("[Casino]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CasinoRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    static bool SceneReadyForPresenters() => CasinoWorld.SceneReady;

    void Update()
    {
        CasinoWorld.Watch();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.5f;
        CasinoWorld.EnsureScene();
        CasinoFront.Ensure();
        if (SceneReadyForPresenters()) CasinoPresenters.Ensure(); // the 3D table state (cards, chips, wheel, reels, drinks), see Docs/Casino.md §11
        if (music == null) music = gameObject.AddComponent<CasinoMusic>();
    }
}

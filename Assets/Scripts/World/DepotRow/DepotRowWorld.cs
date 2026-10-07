using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The co-op "one world" (Docs/Multiplayer.md §13.4, package N3): the town and the additive <c>DepotRow</c> scene stay
/// loaded together for everyone, and doors move only the player who uses them.
/// <list type="bullet">
/// <item><b>Which flow:</b> a co-op host (anything but Play Solo) turns the one world on (<see cref="HostWantsOneWorld"/>,
/// then <see cref="DepotRowNet.ServerBeginOneWorld"/>). Solo keeps the legacy ShopScene ⇄ Neighborhood swap.</item>
/// <item><b>Doors:</b> <see cref="UseDoor"/>: on foot a short local fade + teleport; driving, the truck (with you in it) is
/// teleported through. Nothing is sent to anyone: player and truck poses ride the owner-authoritative NetworkTransforms.</item>
/// <item><b>Where am I:</b> <see cref="LocalInterior"/> (the pocket interior the local player is in, or −1) replaces the
/// scene-name checks (critters, ambience, lighting).</item>
/// </list>
/// </summary>
public static class DepotRowWorld
{
    /// <summary>Host: set when a co-op (non-solo) session starts; the session object then loads the one world.</summary>
    public static bool HostWantsOneWorld { get; private set; }

    /// <summary>True on every machine while the one world is on.</summary>
    public static bool Active
    {
        get
        {
            var n = DepotRowNet.Instance;
            return n != null && n.IsSpawned && n.OneWorld;
        }
    }

    /// <summary>The town scene (the one the houses live in): HouseDirectory's scene.</summary>
    public static string TownSceneName
    {
        get
        {
            if (string.IsNullOrEmpty(townScene))
            {
                var dir = HouseDirectory.Load();
                townScene = dir != null && !string.IsNullOrEmpty(dir.sceneName) ? dir.sceneName : GarageDoorController.DefaultTownScene;
            }
            return townScene;
        }
    }
    static string townScene;

    public static bool IsSceneLoaded(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return false;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.isLoaded && s.name == sceneName) return true;
        }
        return false;
    }

    /// <summary>A door transition (fade) is running on this machine.</summary>
    public static bool Transitioning { get; private set; }

    /// <summary>The lot whose interior the local player is heading into (kept active through the fade), or −1.</summary>
    public static int PendingInterior { get; private set; } = -1;

    // ------------------------------------------------------------------ local queries

    public static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;

    /// <summary>The lot of the local player's business (−1 = none / not in the one world).</summary>
    public static int LocalLot => Active ? DepotRowNet.Instance.LotOwnedBy(LocalClientId) : -1;

    /// <summary>The local player's controller (camera/HUD/input only, §9 rule 8: this is the local door/teleport).</summary>
    static PlayerController LocalPlayer => PlayerController.Local; // coop:allow(local) doors and teleports move only this machine's own player

    /// <summary>Where the local view is: the player, else the main camera.</summary>
    public static Vector3 LocalViewPoint
    {
        get
        {
            var p = LocalPlayer;
            if (p != null) return p.transform.position;
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }
    }

    /// <summary>The pocket interior the local player is standing (or driving) in, or −1 (street / town / not one world).</summary>
    public static int LocalInterior => DepotRowLayout.InteriorAt(LocalViewPoint);

    /// <summary>True while the local player is inside any shop (garage) in the one world.</summary>
    public static bool LocalIndoors => Active && LocalInterior >= 0;

    // ------------------------------------------------------------------ poses

    public static Pose InteriorOnFootPose(int lot)
    {
        var l = DepotRowScene.Lot(lot);
        if (l != null && l.interiorOnFoot != null) return new Pose(l.interiorOnFoot.position, FlowUtil.Upright(l.interiorOnFoot.rotation));
        return new Pose(DepotRowLayout.InteriorOrigin(lot) + new Vector3(-5.8f, 0.05f, 0.6f), Quaternion.Euler(0f, 90f, 0f));
    }

    public static Pose InteriorTruckPose(int lot)
    {
        var l = DepotRowScene.Lot(lot);
        if (l != null && l.interiorTruck != null) return new Pose(l.interiorTruck.position, FlowUtil.Upright(l.interiorTruck.rotation));
        return new Pose(DepotRowLayout.InteriorOrigin(lot) + new Vector3(-0.11f, 0.15f, -2.47f), Quaternion.Euler(0f, 270f, 0f));
    }

    /// <summary>Spawn pose for a player whose business has a lot (PlayerSpawner asks; null = the default spawner).</summary>
    public static Pose? SpawnPoseFor(ulong clientId)
    {
        if (!Active) return null;
        int lot = DepotRowNet.Instance.LotOwnedBy(clientId);
        if (lot < 0) return null;
        return DepotRowLayout.StreetSpawn(lot, (int)(clientId % 3));
    }

    // ------------------------------------------------------------------ doors

    /// <summary>
    /// A Depot Row door was used by the local player. <paramref name="toStreet"/>: the door inside the pocket interior.
    /// Returns false (with feedback) when it can't be used.
    /// </summary>
    public static bool UseDoor(int lot, bool toStreet, GameObject interactor)
    {
        var pc = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (pc == null || !pc.IsOwner || Transitioning) return false;
        bool driving = pc.IsDriving && pc.currentTruck != null && pc.currentTruck.IsLocalPlayerSeated;
        if (!toStreet && driving && !Permissions.CanWorkIn(LocalClientId, lot))
        {
            Permissions.Deny(pc.transform.position, "Not your garage! Park outside.");
            return false;
        }
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (!driving && carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Can't take trash through the door! Drop it first (G)", ToastStyle.Bad, 2f);
            Sfx.Play("buzz", pc.transform.position, 0.7f);
            return false;
        }
        Pose target = toStreet
            ? (driving ? DepotRowLayout.StreetTruck(lot) : DepotRowLayout.StreetOnFoot(lot))
            : (driving ? InteriorTruckPose(lot) : InteriorOnFootPose(lot));
        Run(DoorRoutine(pc, driving ? pc.currentTruck : null, target, toStreet ? -1 : lot));
        return true;
    }

    /// <summary>Local: go to your lot's street apron (host's request on the first load, a reopened shop, a tow home).</summary>
    public static void LocalGoToStreet(int lot, bool spawnSpot)
    {
        var pc = LocalPlayer;
        if (pc == null) return;
        Pose p = spawnSpot ? DepotRowLayout.StreetSpawn(lot) : DepotRowLayout.StreetOnFoot(lot);
        if (pc.IsDriving && pc.currentTruck != null && pc.currentTruck.IsLocalPlayerSeated)
            Run(DoorRoutine(pc, pc.currentTruck, DepotRowLayout.StreetTruck(lot), -1));
        else
            Run(DoorRoutine(pc, null, p, -1));
    }

    /// <summary>Local: into your own shop (on foot, or driving your truck into the bay).</summary>
    public static void LocalGoInside(int lot, bool driving)
    {
        var pc = LocalPlayer;
        if (pc == null || lot < 0) return;
        bool seated = pc.IsDriving && pc.currentTruck != null && pc.currentTruck.IsLocalPlayerSeated;
        if (driving && seated) Run(DoorRoutine(pc, pc.currentTruck, InteriorTruckPose(lot), lot));
        else
        {
            if (seated) pc.currentTruck.ForceExit();
            Run(DoorRoutine(pc, null, InteriorOnFootPose(lot), lot));
        }
    }

    static void Run(IEnumerator routine)
    {
        var host = DepotRowScene.Instance;
        if (host != null && host.isActiveAndEnabled) host.StartCoroutine(Guard(routine));
        else
        {
            // No scene runner: do it in one step (no fade).
            while (routine.MoveNext()) { }
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
        if (Transitioning)
        {
            Transitioning = false;
            PendingInterior = -1;
            FlowCamera.SetCutMode(false);
            ScreenFader.FadeIn(ScreenFader.DefaultFadeIn);
        }
    }

    static IEnumerator DoorRoutine(PlayerController pc, TruckControllerV2 truck, Pose target, int intoLot)
    {
        Transitioning = true;
        PendingInterior = intoLot;
        if (truck != null)
        {
            Sfx.Play("engine_start", truck.transform.position);
            Sfx.Play("horn", truck.transform.position, 0.5f, 1.15f);
        }
        else Sfx.Play("door", pc.transform.position);
        GameHUD.SetContextHint(null);
        yield return ScreenFader.FadeOut(0.35f, false);
        FlowCamera.SetCutMode(true);

        if (pc == null) yield break;
        if (truck != null && truck.IsLocalPlayerSeated) TeleportTruck(truck, target.position + Vector3.up * 0.15f, target.rotation);
        else TeleportPlayer(pc, target.position, target.rotation);

        // Let the interior switch on (DepotRowScene) and the camera catch up while black.
        yield return null;
        yield return new WaitForSecondsRealtime(0.15f);
        FlowCamera.SetCutMode(false);
        PendingInterior = -1;
        Transitioning = false;
        yield return ScreenFader.FadeIn(0.35f);
    }

    // ------------------------------------------------------------------ teleports

    /// <summary>Move a player this machine owns (CharacterController off for the jump; NetworkTransform told it's a teleport).</summary>
    public static void TeleportPlayer(PlayerController me, Vector3 to, Quaternion rot)
    {
        if (me == null) return;
        var cc = me.GetComponent<CharacterController>();
        bool ccOn = cc != null && cc.enabled;
        if (cc != null) cc.enabled = false;
        var t = me.transform;
        t.SetPositionAndRotation(to, rot);
        Physics.SyncTransforms();
        if (cc != null) cc.enabled = ccOn;
        var nt = me.GetComponent<NetworkTransform>();
        if (nt != null && nt.IsSpawned && nt.CanCommitToTransform)
        {
            try { nt.Teleport(to, rot, t.localScale); }
            catch (Exception e) { Debug.LogWarning("[DepotRow] player teleport: " + e.Message); }
        }
    }

    /// <summary>Move a truck this machine simulates (its driver, or the host when nobody drives).</summary>
    public static void TeleportTruck(TruckControllerV2 truck, Vector3 position, Quaternion rotation)
    {
        if (truck == null) return;
        var rb = truck.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        truck.transform.SetPositionAndRotation(position, rotation);
        if (rb != null) { rb.position = position; rb.rotation = rotation; }
        Physics.SyncTransforms();
        var nt = truck.GetComponent<NetworkTransform>();
        if (nt != null && nt.IsSpawned && nt.CanCommitToTransform)
        {
            try { nt.Teleport(position, rotation, truck.transform.localScale); }
            catch (Exception e) { Debug.LogWarning("[DepotRow] truck teleport: " + e.Message); }
        }
    }

    // ------------------------------------------------------------------ session hooks

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        HostWantsOneWorld = false;
        Transitioning = false;
        PendingInterior = -1;
        townScene = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook()
    {
        NetSession.HostStarted -= OnHostStarted;
        NetSession.HostStarted += OnHostStarted;
        NetSession.StateChanged -= OnStateChanged;
        NetSession.StateChanged += OnStateChanged;
        PlayerSpawner.SpawnPoseOverride = SpawnPoseFor;
    }

    static void OnHostStarted(HostOptions o, string joinCode)
    {
        // Solo keeps today's garage ⇄ town scene flow (§13.4: "Solo keeps today's scene flow until the one world is proven").
        HostWantsOneWorld = o != null && o.mode != NetMode.Offline && !ForceLegacy;
        if (!HostWantsOneWorld) return;
        Debug.Log("[DepotRow] Co-op host: starting the one world (town + Depot Row).");
        var net = DepotRowNet.Instance;
        if (net != null && net.IsSpawned) net.ServerBeginOneWorld();
        else Debug.LogWarning("[DepotRow] No DepotRowNet on the session object (run Rubbish/Setup/Coop): legacy scene flow.");
    }

    static void OnStateChanged(NetSessionState s)
    {
        if (s == NetSessionState.Idle || s == NetSessionState.Failed || s == NetSessionState.Leaving)
        {
            HostWantsOneWorld = false;
            Transitioning = false;
            PendingInterior = -1;
        }
    }

    /// <summary>Dev escape hatch: keep the legacy flow even when hosting co-op (command line <c>-depotrow-off</c>).</summary>
    public static bool ForceLegacy
    {
        get
        {
            if (!forceLegacyRead)
            {
                forceLegacyRead = true;
                try { forceLegacy = Array.IndexOf(Environment.GetCommandLineArgs(), "-depotrow-off") >= 0; }
                catch (Exception) { forceLegacy = false; }
            }
            return forceLegacy;
        }
    }
    static bool forceLegacy, forceLegacyRead;
}

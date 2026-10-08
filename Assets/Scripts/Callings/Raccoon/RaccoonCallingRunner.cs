using UnityEngine;

/// <summary>
/// The Raccoon calling's one runtime object ("[RaccoonCalling]", DontDestroyOnLoad, created at startup like [Night] and
/// [Critters]; no scene or prefab setup). Every machine: hutch models, companions and helpers, feral ears and tails, the
/// local player's feral input and night eyes, the owner's morning report. Host only (and solo): the hutch's daily tick,
/// crew sweeps, companion fetches and Alpha deference, and the King's Rival watch.
/// </summary>
[DefaultExecutionOrder(-25)]
public sealed class RaccoonCallingRunner : MonoBehaviour
{
    public static RaccoonCallingRunner Instance { get; private set; }
    readonly FeralNightVision nightVision = new FeralNightVision();
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null || !Application.isPlaying) return;
        var go = new GameObject("[RaccoonCalling]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<RaccoonCallingRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    void OnDestroy() { nightVision.Dispose(); if (Instance == this) Instance = null; }

    void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 1f;
            HutchWorld.EnsureAll();
            foreach (var pc in NightPlayers.All)
                if (pc != null && pc.GetComponent<FeralLook>() == null) pc.gameObject.AddComponent<FeralLook>();
            RaccoonCrew.LocalTick();
        }
        PackCompanions.Tick(Time.deltaTime);
        FeralStatus.LocalTick();
        var me = NightPlayers.LocalPlayer;
        bool eyes = me != null && FeralStatus.LocalPerks && GameClock.IsNight && !NightPlayers.IsIndoors(me);
        nightVision.Apply(eyes, me != null ? (me.IsDriving && me.currentTruck != null ? me.currentTruck.transform : me.transform) : null);

        // Docs/Multiplayer.md rule 15: everything below decides, so it runs on the host (or solo) only.
        if (!CritterWorld.IsHost || GameSession.IsConnectedGuest || CurbTrashSpawner.Houses.Count == 0) return;
        Hutch.HostDayTick();
        RaccoonCrew.HostTick();
        PackCompanions.HostTick();
        KingsRival.HostTick();
    }
}

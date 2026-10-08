using UnityEngine;

/// <summary>
/// The Hauler calling's heartbeat (DontDestroyOnLoad, created at startup like <see cref="TellsRuntime"/>): puts
/// <see cref="RocketBlades"/> and <see cref="CompactorPack"/> on EVERY player copy (so skates, flames and the filling pack
/// show on all machines), keeps each lot's <see cref="FootHatch"/> in step with the replicated truth, and ticks the host's
/// backpack upkeep (<c>HandsNet.Pack.cs</c>).
/// </summary>
public class HaulerRuntime : MonoBehaviour
{
    static HaulerRuntime instance;
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[Hauler]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<HaulerRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void OnEnable() => CoopPlayerHooks.Spawned += Ensure;
    void OnDisable() => CoopPlayerHooks.Spawned -= Ensure;

    static void Ensure(PlayerController pc)
    {
        if (pc == null) return;
        var go = pc.gameObject;
        if (go.GetComponent<RocketBlades>() == null) go.AddComponent<RocketBlades>();
        if (go.GetComponent<CompactorPack>() == null) go.AddComponent<CompactorPack>();
    }

    void Update()
    {
        var hands = HandsNet.Instance;
        if (hands != null) hands.PackHostTick();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        // Every player copy already carries PlayerTells (TellsRuntime), so its list is the roster: no scene search.
        var all = PlayerTells.All;
        for (int i = 0; i < all.Count; i++) if (all[i] != null) Ensure(all[i].Player);
        FootHatch.Sync();
    }
}

using UnityEngine;

/// <summary>
/// DontDestroyOnLoad heartbeat for <see cref="EbaeMarket"/>: ticks offers in real (unscaled) time in
/// every scene and flushes the ebae save. Created automatically by EbaeMarket at startup; never
/// place one in a scene by hand.
/// <para>Local offers run offline/on the host. A connected guest receives its own host-simulated
/// offers through EbaeNeighbours; it must never independently simulate or auto-sell the same listing.
/// Pause/quit saves belong to LegacySave; guest checkpoints belong to BusinessSlots.</para>
/// </summary>
public class EbaeMarketRunner : MonoBehaviour
{
    void Update()
    {
        if (GameSession.IsGuest || SaveGuard.Foreign) return;
        EbaeMarket.Tick();
    }

    void LateUpdate() => EbaeMarket.FlushIfDirty();
}

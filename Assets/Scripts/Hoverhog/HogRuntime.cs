using UnityEngine;

/// <summary>
/// The Hoverhog's heartbeat (DontDestroyOnLoad, created at startup like <see cref="HaulerRuntime"/>): keeps each lot's
/// <see cref="Helipad"/> and <see cref="Hoverhog"/> in step with the replicated upgrade level, ticks the host's parked
/// batteries, and finishes a stairwell trip in the solo scene flow (where the roof and the shop are different scenes).
/// </summary>
public class HogRuntime : MonoBehaviour
{
    enum Arrival { None, Roof, Shop }

    static HogRuntime instance;
    static Arrival pending;
    static float pendingUntil;
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[Hoverhog]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<HogRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        pending = Arrival.None;
    }

    void Update()
    {
        Hogs.HostTick(Time.deltaTime);
        if (pending != Arrival.None) FinishStairs();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.5f;
        Helipad.Sync();
        Hoverhog.Sync();
    }

    public static Pose RoofArrival(int lot) => DepotRowLayout.StreetPose(lot, HogTuning.RoofArrivalLocal, HogTuning.RoofArrivalYaw);

    /// <summary>The local player takes the helipad stairwell of <paramref name="lot"/>, up or down.</summary>
    public static void UseStairs(int lot, bool toRoof)
    {
        if (DepotRowWorld.Active)
        {
            // One world: the shop is a pocket interior, so the stairs are a door-style move for this player only.
            Pose to = toRoof ? RoofArrival(lot) : new Pose(DepotRowLayout.InteriorOrigin(lot) + HogTuning.ShopArrivalLocal, Quaternion.identity);
            DepotRowWorld.LocalGoTo(to, toRoof ? -1 : lot);
            return;
        }
        // Solo scene flow: the roof is in the town, the shop is its own scene. Take the ordinary door trip and step
        // onto the roof / the stair foot once the other side is up.
        var scenes = SceneController.Instance;
        if (scenes == null || SceneController.IsTransitioning) return;
        pending = toRoof ? Arrival.Roof : Arrival.Shop;
        pendingUntil = Time.unscaledTime + 25f;
        if (toRoof) scenes.TransitionToScene(DepotRowWorld.TownSceneName, DepotFlowRules.TownOnFootMarker, false, DepotFlowRules.TownOnFootMarker);
        else scenes.TransitionToScene(SceneController.GarageSceneName, DepotFlowRules.GarageOnFootMarker, false, DepotFlowRules.GarageOnFootMarker);
        if (!SceneController.IsTransitioning) pending = Arrival.None; // the door was jammed
    }

    static void FinishStairs()
    {
        if (Time.unscaledTime > pendingUntil) { pending = Arrival.None; return; }
        if (SceneController.IsTransitioning) return;
        var me = PlayerController.Local; // coop:allow(local) the stairwell moves only this machine's own player
        if (me == null || me.IsDriving) return;
        Pose to;
        if (pending == Arrival.Roof)
        {
            var depot = DepotRowScene.Lot(DepotFlowRules.LegacyShopLot);
            if (depot == null || depot.facade == null || !depot.facade.gameObject.activeInHierarchy) return;
            to = RoofArrival(DepotFlowRules.LegacyShopLot);
        }
        else
        {
            var floors = TippingFloor.All;
            if (floors.Count == 0 || floors[0] == null) return;
            to = new Pose(Helipad.ShopOrigin(floors[0]) + HogTuning.ShopArrivalLocal, Quaternion.identity);
        }
        pending = Arrival.None;
        DepotRowWorld.TeleportPlayer(me, to.position, to.rotation);
        Sfx.Play("door", to.position);
    }
}

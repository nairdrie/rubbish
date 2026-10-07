using System;
using System.Collections;
using UnityEngine;

/// <summary>Physical shop stock that is temporarily driven by the belt, not held by a player or stored in a truck.</summary>
public static class ConveyorStock
{
    public static bool OwnedInShop(TrashBag bag, Compactor shop)
    {
        if (bag == null || shop == null) return false;
        foreach (var feeder in ConveyorFeeder.Feeders)
            if (SameShop(feeder, shop) && feeder.Owns(bag)) return true;
        foreach (var line in ConveyorLine.Lines)
            if (SameShop(line, shop) && line.Owns(bag)) return true;
        foreach (var tray in ConveyorTray.Trays)
            if (SameShop(tray, shop) && tray.Owns(bag)) return true;
        return false;
    }

    static bool SameShop(Component machine, Compactor shop)
    {
        if (machine == null) return false;
        int lot = DepotMachineContext.LotOf(shop);
        if (lot >= 0) return DepotMachineContext.LotOf(machine) == lot;
        return machine.gameObject.scene == shop.gameObject.scene && shop.ContainsShopPoint(machine.transform.position);
    }

    /// <summary>Revoke every machine anchor before consuming a bag. Never drop/re-publish it on the way out.</summary>
    public static void Detach(TrashBag bag)
    {
        if (bag == null) return;
        foreach (var feeder in ConveyorFeeder.Feeders) if (feeder != null) feeder.Detach(bag);
        foreach (var line in ConveyorLine.Lines) if (line != null) line.Detach(bag);
        foreach (var tray in ConveyorTray.Trays) if (tray != null) tray.Detach(bag);
    }

    /// <summary>A revoked belt/tray/chute arc cannot drag a consumed or handed-off bag on its next frame.</summary>
    internal static IEnumerator WhileOwned(TrashBag bag, Func<bool> owns, IEnumerator motion)
    {
        while (bag != null && bag.State != CarryState.Consumed && bag.State != CarryState.Carried && owns())
        {
            if (!motion.MoveNext()) yield break;
            yield return motion.Current;
        }
    }
}

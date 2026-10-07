using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A stowaway raccoon eating a truck's cargo (F4, host only). The host's own truck keeps its bags in
/// <see cref="GameSession.TruckBags"/>; every other business's truck in <see cref="Businesses.HostCargo"/> (N1/N4), so the
/// cargo shrinks for whoever owns the truck. Raccoons eat junk, not treasure: valuables stay in the bag.
/// </summary>
public static class NightCargo
{
    /// <summary>Eat one item off the top bag; returns its name (null = nothing edible).</summary>
    public static string EatOne(TruckControllerV2 t)
    {
        if (t == null || GameSession.IsConnectedGuest) return null;
        bool local = DepotTruckCargo.IsLocal(t);
        string biz = local ? null : DepotTruckCargo.BusinessOf(t);
        List<BagContents> bags = local ? GameSession.TruckBags : Businesses.HostCargo(biz);
        if (bags == null || bags.Count == 0) return null;
        for (int b = bags.Count - 1; b >= 0; b--)
        {
            var bag = bags[b];
            if (bag == null || bag.items == null) continue;
            for (int i = bag.items.Count - 1; i >= 0; i--)
            {
                var it = bag.items[i];
                var def = it != null ? it.Def : null;
                if (def != null && def.category == ItemCategory.Valuable) continue;
                bag.items.RemoveAt(i);
                Touch(local, biz, bags);
                return def != null ? def.name : "something";
            }
        }
        return null;
    }

    static void Touch(bool local, string biz, List<BagContents> bags)
    {
        if (local)
        {
            GameSession.SetTruckCapacity(GameSession.TruckCapacityKg); // nudges GameSession.Changed (HUD load) and the save
            return;
        }
        float kg = 0f;
        for (int i = 0; i < bags.Count; i++) if (bags[i] != null) kg += bags[i].TotalMassKg;
        float cap = 0f;
        if (Businesses.TryGet(biz, out var info)) cap = info.truckCapKg;
        Businesses.SetTruckLoad(biz, kg, cap > 0f ? cap : Economy.StartingTruckCapacityKg);
    }
}

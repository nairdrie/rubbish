using UnityEditor;
using UnityEngine;

/// <summary>Play-mode shortcuts for F4 night shift (Docs/Multiplayer.md §14.4): Rubbish/Debug/Night/…</summary>
public static class NightDebugMenu
{
    const string M = "Rubbish/Debug/Night/";
    static bool Playing() => Application.isPlaying;

    static bool Me(out PlayerController pc, out Vector3 ahead)
    {
        pc = NightPlayers.LocalPlayer;
        ahead = Vector3.zero;
        if (pc == null) { Debug.Log("[Night] No local player."); return false; }
        Transform t = pc.IsDriving && pc.currentTruck != null ? pc.currentTruck.transform : pc.transform;
        ahead = t.position + CritterWorld.Flat(t.forward).normalized * 6f;
        return true;
    }

    [MenuItem(M + "Make It 23:00")]
    static void Night() => GameClock.DebugSetHour(23f);
    [MenuItem(M + "Make It 23:00", true)] static bool V0() => Playing();

    [MenuItem(M + "Raccoon King Now")]
    static void King() => Debug.Log("[Night] " + RaccoonKingEvent.DebugStart());
    [MenuItem(M + "Raccoon King Now", true)] static bool V1() => Playing();

    [MenuItem(M + "Stage A Heist Ahead")]
    static void Heist()
    {
        if (!Me(out _, out var p)) return;
        var r = NightDebug.StageHeist(p, "menu-" + Time.frameCount);
        Debug.Log("[Night] Heist: " + (r != null ? r.LootDefId : "failed"));
    }
    [MenuItem(M + "Stage A Heist Ahead", true)] static bool V2() => Playing();

    [MenuItem(M + "Rabid Raccoon Ahead (latch test)")]
    static void Rabid()
    {
        if (Me(out _, out var p)) NightDebug.SpawnRaccoon(p, true);
    }
    [MenuItem(M + "Rabid Raccoon Ahead (latch test)", true)] static bool V3() => Playing();

    [MenuItem(M + "Stowaway Into My Truck")]
    static void Stow()
    {
        foreach (var t in TruckControllerV2.All)
        {
            if (t == null || !DepotTruckCargo.IsLocal(t)) continue;
            if (!DepotTruckCargo.HasCargo(t))
            {
                var bag = new BagContents { bagId = "menu/cargo", foundAt = "menu" };
                foreach (var d in ItemCatalog.All) if (d.category == ItemCategory.Junk && bag.items.Count < 6) bag.items.Add(new ItemInstance { uid = "menu-" + bag.items.Count, defId = d.id, massKg = 0.5f });
                GameSession.AddBagToTruck(bag);
            }
            var r = NightDebug.SpawnRaccoon(t.transform.TransformPoint(new Vector3(1.5f, 0f, -7f)), false);
            Debug.Log("[Night] Stowaway: " + r.TryStowaway(t));
            return;
        }
        Debug.Log("[Night] No truck of yours here.");
    }
    [MenuItem(M + "Stowaway Into My Truck", true)] static bool V4() => Playing();

    [MenuItem(M + "Give 2 Raccoon Traps")]
    static void Traps() { GameSession.AddConsumable(RaccoonTrap.ConsumableId, 2); Debug.Log("[Night] 2 traps: press T on foot."); }
    [MenuItem(M + "Give 2 Raccoon Traps", true)] static bool V5() => Playing();

    [MenuItem(M + "Get Rabies (symptoms now)")]
    static void Rabies()
    {
        GameSession.CureRabies();
        GameSession.Infect(GameClock.TotalHours - Economy.RabiesIncubationHours - 0.5);
    }
    [MenuItem(M + "Get Rabies (symptoms now)", true)] static bool V6() => Playing();

    [MenuItem(M + "Toggle Rabies Tag")]
    static void Tag() => NightShift.ToggleRabiesTag();
    [MenuItem(M + "Toggle Rabies Tag", true)] static bool V7() => Playing();
}

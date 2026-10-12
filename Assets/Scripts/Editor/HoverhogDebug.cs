using UnityEditor;
using UnityEngine;

/// <summary>Play-mode shortcuts for trying the Hoverhog (Docs/Progression.md §6.10) without rank 11 and $85,000.</summary>
public static class HoverhogDebug
{
    const string Menu = "Rubbish/Debug/Hoverhog/";

    [MenuItem(Menu + "Give Me A Hoverhog (free)")]
    static void Give()
    {
        if (GameSession.IsConnectedGuest) { Debug.LogWarning("[Hoverhog] Guests can't cheat upgrades in."); return; }
        GameSession.SetUpgradeLevel(HogShop.Hoverhog, 1);
        Debug.Log("[Hoverhog] Owned. The helipad is on your depot's roof; the stairs are by the tipping floor.");
    }

    [MenuItem(Menu + "Take It Away")]
    static void Take()
    {
        SetLines(0);
        GameSession.SetUpgradeLevel(HogShop.Hoverhog, 0);
    }

    [MenuItem(Menu + "Upgrades: All Maxed")]
    static void MaxLines() { Give(); SetLines(HogLevels.Max); }

    [MenuItem(Menu + "Upgrades: One Level Up Each")]
    static void BumpLines()
    {
        Give();
        foreach (var id in Lines) GameSession.SetUpgradeLevel(id, Mathf.Min(HogLevels.Max, GameSession.GetUpgradeLevel(id) + 1));
    }

    [MenuItem(Menu + "Upgrades: Back To Stock")]
    static void StockLines() => SetLines(0);

    static readonly string[] Lines = { HogShop.Belly, HogShop.Claw, HogShop.Rotors, HogShop.Battery, HogShop.SatPad };

    static void SetLines(int level)
    {
        if (GameSession.IsConnectedGuest) return;
        foreach (var id in Lines) GameSession.SetUpgradeLevel(id, level);
    }

    [MenuItem(Menu + "Battery: Full")]
    static void Full() => SetBattery(1f);

    [MenuItem(Menu + "Battery: 5%")]
    static void Low() => SetBattery(0.05f);

    static void SetBattery(float value)
    {
        for (int lot = 0; lot < DepotRowLayout.LotCount; lot++)
            if (Hogs.Level(lot) > 0 && Hogs.IsOwnBusiness(Hogs.BusinessOf(lot))) Hogs.Of(lot).battery = value;
    }

    [MenuItem(Menu + "Upgrades: All Maxed", true), MenuItem(Menu + "Upgrades: One Level Up Each", true), MenuItem(Menu + "Upgrades: Back To Stock", true),
     MenuItem(Menu + "Give Me A Hoverhog (free)", true), MenuItem(Menu + "Take It Away", true),
     MenuItem(Menu + "Battery: Full", true), MenuItem(Menu + "Battery: 5%", true)]
    static bool Playing() => Application.isPlaying;
}

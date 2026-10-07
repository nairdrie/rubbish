using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Who may do what in whose shop (Docs/Multiplayer.md §13.5). One place for every rule, so N4 (payroll) only has to extend
/// <see cref="CanWorkIn"/> through <see cref="EmployeeHook"/>:
/// <list type="table">
/// <item>Walk into any shop and look: everyone (no check).</item>
/// <item>Pick up / sort / bin / smash trash, use the station, tip the truck, put things in the keep crate:
/// <see cref="CanWorkIn"/> (owner + on-shift employees through <see cref="EmployeeHook"/>, set by N4's PayrollNet).</item>
/// <item>Rearrange the display shelves: <see cref="LocalCanManageAt"/> (owner only).</item>
/// <item>Laptop (upgrades, Scrapazon, ebae, rename): <see cref="CanUseLaptop"/> (owner only).</item>
/// <item>Drive a business truck: <see cref="CanDrive"/> (owner; N4 adds on-shift employees).</item>
/// </list>
/// Outside the one world (solo, the legacy garage flow) every check passes. Checks are cheap; call them every frame.
/// </summary>
public static class Permissions
{
    /// <summary>
    /// N4: return true when <c>clientId</c> works for the business on lot <c>lot</c> right now (on shift). Called only for
    /// non-owners. Null = no employees yet.
    /// </summary>
    public static Func<ulong, int, bool> EmployeeHook;

    /// <summary>N4: same for driving (defaults to <see cref="EmployeeHook"/> when null).</summary>
    public static Func<ulong, int, bool> DriverHook;

    public const string NotYourShop = "Not your shop!";
    public const string NotYourTruck = "Not your truck!";

    static float lastDenyAt = -10f;

    /// <summary>May <paramref name="clientId"/> handle trash and machines in the shop on <paramref name="lot"/>?</summary>
    public static bool CanWorkIn(ulong clientId, int lot)
    {
        var net = DepotRowNet.Instance;
        if (net == null || !net.OneWorld || lot < 0) return true;
        if (!net.TryGetLot(lot, out var s) || !s.Claimed) return false;
        if (s.Online && s.ownerClientId == clientId) return true;
        return Ask(EmployeeHook, clientId, lot);
    }

    /// <summary>The laptop (upgrades, Scrapazon, ebae, payroll, rename) is the owner's alone.</summary>
    public static bool CanUseLaptop(ulong clientId, int lot)
    {
        var net = DepotRowNet.Instance;
        if (net == null || !net.OneWorld || lot < 0) return true;
        return net.TryGetLot(lot, out var s) && s.Claimed && s.Online && s.ownerClientId == clientId;
    }

    public static bool CanRename(ulong clientId, int lot) => CanUseLaptop(clientId, lot);

    /// <summary>May <paramref name="clientId"/> drive this truck? Unowned trucks (solo, legacy) are anyone's.</summary>
    public static bool CanDrive(ulong clientId, TruckControllerV2 truck)
    {
        var net = DepotRowNet.Instance;
        if (net == null || !net.OneWorld || truck == null || !truck.IsSpawned) return true;
        int lot = net.LotOfTruck(truck.NetworkObjectId);
        if (lot < 0) return true;
        if (!net.TryGetLot(lot, out var s) || !s.Claimed) return false;
        if (s.Online && s.ownerClientId == clientId) return true;
        return Ask(DriverHook ?? EmployeeHook, clientId, lot);
    }

    // ------------------------------------------------------------------ local helpers (this machine's player)

    public static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;

    /// <summary>The shop interior a world point is in (−1 = the street / not a shop).</summary>
    public static int LotAt(Vector3 worldPos) => DepotRowLayout.InteriorAt(worldPos);

    /// <summary>May the local player touch trash / machines at this point? (Always true outside shop interiors.)</summary>
    public static bool LocalCanWorkAt(Vector3 worldPos)
    {
        int lot = LotAt(worldPos);
        return lot < 0 || CanWorkIn(LocalClientId, lot);
    }

    /// <summary>Owner-only shop things (rearranging the display shelves): employees may put things in, not take or move them.</summary>
    public static bool LocalCanManageAt(Vector3 worldPos) => LocalCanUseLaptopAt(worldPos);

    public static bool LocalCanUseLaptopAt(Vector3 worldPos)
    {
        int lot = LotAt(worldPos);
        return lot < 0 || CanUseLaptop(LocalClientId, lot);
    }

    /// <summary>"Not your shop!" toast + buzz (rate-limited so holding E doesn't spam).</summary>
    public static void Deny(Vector3? at = null, string text = NotYourShop)
    {
        if (Time.unscaledTime - lastDenyAt < 0.6f) return;
        lastDenyAt = Time.unscaledTime;
        GameHUD.Toast(text, ToastStyle.Bad, 1.6f);
        Sfx.Play("buzz", at, 0.7f);
    }

    static bool Ask(Func<ulong, int, bool> hook, ulong clientId, int lot)
    {
        if (hook == null) return false;
        try { return hook(clientId, lot); }
        catch (Exception e) { Debug.LogException(e); return false; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { lastDenyAt = -10f; }
}

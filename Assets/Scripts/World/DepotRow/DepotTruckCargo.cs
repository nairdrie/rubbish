using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Which business a Depot Row truck belongs to, and where its cargo lives (Docs/Multiplayer.md §13.5, N1 × N3 × N4).
/// <list type="bullet">
/// <item><b>Owner:</b> <see cref="TruckHopper.BusinessResolver"/> maps a lot truck to its lot's business (by the lot's
/// PlayerKey, so every machine agrees), so pickups pay the business whose truck took the bag: a guest's truck pays the
/// guest's business, and an employee driving the boss's truck pays the boss.</item>
/// <item><b>Cargo:</b> the host's own truck keeps its bags in <see cref="GameSession.TruckBags"/> on the host; every other
/// business's bags stay on the host (<see cref="Businesses.HostCargo"/>). A guest driver asks the host's
/// <see cref="TruckTipper"/> to tip; indoors each bag leaves authoritative cargo only when there is depot space to spawn it.</item>
/// </list>
/// Outside the one world (solo, legacy) the resolver answers nothing and all cargo is local, exactly as before.
/// </summary>
public static class DepotTruckCargo
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install() => TruckHopper.BusinessResolver = ResolveLotTruck;

    /// <summary>The business of the lot whose truck this hopper sits on (null = not a lot truck: the host's, as before).</summary>
    static string ResolveLotTruck(TruckHopper h)
    {
        var net = DepotRowNet.Instance;
        if (h == null || net == null || !net.IsSpawned || !net.OneWorld) return null;
        var t = h.GetComponentInParent<TruckControllerV2>();
        if (t == null || !t.IsSpawned) return null;
        int lot = net.LotOfTruck(t.NetworkObjectId);
        return BusinessOfLot(lot);
    }

    /// <summary>N1's business id for a lot's owner (online or not), or null.</summary>
    public static string BusinessOfLot(int lot)
    {
        var net = DepotRowNet.Instance;
        if (net == null || lot < 0 || !net.TryGetLot(lot, out var s) || !s.Claimed) return null;
        if (PlayerKey.TryParse(s.businessKey.ToString(), out var k) && Businesses.TryGetForOwner(k, out var b) && b.IsValid) return b.Id;
        if (s.Online && Businesses.TryGetForClient(s.ownerClientId, out b) && b.IsValid) return b.Id;
        return null;
    }

    /// <summary>The business that owns this truck (its hopper's answer).</summary>
    public static string BusinessOf(TruckControllerV2 t)
    {
        var h = t != null ? t.GetComponentInChildren<TruckHopper>(true) : null;
        return h != null ? h.ResolveBusiness() : null;
    }

    /// <summary>This truck's bags are in this machine's <see cref="GameSession.TruckBags"/> (solo, the host's own truck).</summary>
    public static bool IsLocal(TruckControllerV2 t)
    {
        if (t == null || !DepotRowWorld.Active) return true;
        string id = BusinessOf(t);
        return Businesses.IsAuthority && (string.IsNullOrEmpty(id) || id == Businesses.LocalId);
    }

    /// <summary>Load (kg) of the truck's business (exact for local cargo; as last replicated otherwise).</summary>
    public static float LoadKg(TruckControllerV2 t)
    {
        if (IsLocal(t)) return GameSession.TruckLoadKg;
        return Businesses.TryGetTruckLoad(BusinessOf(t), out float kg, out _) ? kg : 0f;
    }

    public static bool HasCargo(TruckControllerV2 t) => IsLocal(t) ? GameSession.TruckBags.Count > 0 : LoadKg(t) > 0.01f;

    /// <summary>Host-only: how many cargo entries (bags and loose piles) this truck carries.</summary>
    public static int Count(TruckControllerV2 t)
    {
        if (!Businesses.IsAuthority) return 0;
        var cargo = IsLocal(t) ? GameSession.TruckBags : Businesses.HostCargo(BusinessOf(t));
        return cargo != null ? cargo.Count : 0;
    }

    /// <summary>Host-only next bag, without removing it. Used for the atomic capacity check.</summary>
    public static BagContents Peek(TruckControllerV2 t)
    {
        if (!Businesses.IsAuthority) return null;
        var cargo = IsLocal(t) ? GameSession.TruckBags : Businesses.HostCargo(BusinessOf(t));
        return cargo != null && cargo.Count > 0 ? cargo[cargo.Count - 1] : null;
    }

    public static BagContents Take(TruckControllerV2 t)
    {
        if (!Businesses.IsAuthority) return null;
        if (IsLocal(t)) return GameSession.TakeTruckBag();
        string id = BusinessOf(t);
        var cargo = Businesses.HostCargo(id);
        if (cargo == null || cargo.Count == 0) return null;
        var bag = cargo[cargo.Count - 1];
        cargo.RemoveAt(cargo.Count - 1);
        Businesses.TryGetTruckLoad(id, out float kg, out float cap);
        Businesses.SetTruckLoad(id, kg, cap);
        return bag;
    }
}

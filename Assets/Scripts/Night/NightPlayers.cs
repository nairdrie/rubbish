using System.Collections.Generic;
using UnityEngine;

/// <summary>A player a critter could care about (F4 §14.4): where they stand and what they're doing.</summary>
public struct NightTarget
{
    public PlayerController pc;
    public ulong client;
    /// <summary>Feet, or the truck's position while driving.</summary>
    public Vector3 feet;
    /// <summary>Walking around outdoors (not driving, riding, playing chess or inside a shop).</summary>
    public bool onFoot;
    public bool driving;
    public bool riding;
    /// <summary>This machine's own player.</summary>
    public bool local;
    public float dist;
    public bool Valid => pc != null;
}

/// <summary>
/// Every player in the session, for critters and night features (Docs/Multiplayer.md §4.10 "critters chase the nearest player
/// on foot, not just the local one"). Solo: just the local player. Built from <see cref="CoopPlayerHooks.Spawned"/> plus the
/// local player, so nothing scans the scene.
/// </summary>
public static class NightPlayers
{
    static readonly List<PlayerController> players = new List<PlayerController>(8);
    static bool hooked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        players.Clear();
        hooked = false;
    }

    static void Hook()
    {
        if (hooked) return;
        hooked = true;
        CoopPlayerHooks.Spawned += p => { if (p != null && !players.Contains(p)) players.Add(p); };
    }

    /// <summary>This machine's player (input/HUD/camera only).</summary>
    public static PlayerController LocalPlayer => PlayerController.Local; // coop:allow(local) the local player's own input, HUD and camera

    /// <summary>All live players (prunes destroyed ones).</summary>
    public static IReadOnlyList<PlayerController> All
    {
        get
        {
            Hook();
            for (int i = players.Count - 1; i >= 0; i--)
                if (players[i] == null) players.RemoveAt(i);
            var me = LocalPlayer;
            if (me != null && !players.Contains(me)) players.Add(me);
            return players;
        }
    }

    public static ulong ClientOf(PlayerController pc) => pc != null && pc.IsSpawned ? pc.OwnerClientId : 0;

    public static bool IsLocal(PlayerController pc) => pc != null && (pc == LocalPlayer || (pc.IsSpawned && pc.IsOwner));

    public static PlayerController ForClient(ulong client)
    {
        var all = All;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p != null && p.isActiveAndEnabled && ClientOf(p) == client) return p;
        }
        return null;
    }

    /// <summary>Riding a truck's step (N2 hitching / F1 riding).</summary>
    public static bool IsRiding(PlayerController pc)
    {
        if (pc == null) return false;
        var riders = RideRider.All; // F1 riding (solo and co-op)
        for (int i = 0; i < riders.Count; i++)
            if (riders[i] != null && riders[i].Player == pc && riders[i].TryGetRide(out _, out _)) return true;
        var net = HandsNet.Instance;
        if (net == null || !net.Active) return false;
        return net.BodyOf(ClientOf(pc)).State == BodyState.Riding;
    }

    /// <summary>Inside a shop interior (one world) or the solo garage.</summary>
    public static bool IsIndoors(PlayerController pc)
    {
        if (pc == null) return false;
        if (DepotRowWorld.Active) return DepotRowLayout.InteriorAt(pc.transform.position) >= 0;
        return CritterWorld.InGarage;
    }

    public static NightTarget Describe(PlayerController pc, Vector3 from)
    {
        var t = new NightTarget { pc = pc, client = ClientOf(pc), local = IsLocal(pc) };
        if (pc == null) return t;
        t.driving = pc.IsDriving;
        t.riding = !t.driving && IsRiding(pc);
        t.feet = t.driving && pc.currentTruck != null ? pc.currentTruck.transform.position : pc.transform.position;
        t.onFoot = !t.driving && !t.riding && !pc.IsPlayingChess && !IsIndoors(pc);
        t.dist = CritterWorld.Flat(t.feet - from).magnitude;
        return t;
    }

    /// <summary>
    /// The player a critter at <paramref name="from"/> should care about: the nearest one on foot (outdoors), else the nearest
    /// one at all (a driver counts at their truck). False when nobody is around.
    /// </summary>
    public static bool Nearest(Vector3 from, out NightTarget best)
    {
        best = default;
        NightTarget anyBest = default;
        float bestFoot = float.MaxValue, bestAny = float.MaxValue;
        var all = All;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc == null || !pc.isActiveAndEnabled) continue;
            var t = Describe(pc, from);
            if (IsIndoors(pc)) continue;
            if (t.onFoot && t.dist < bestFoot) { bestFoot = t.dist; best = t; }
            if (t.dist < bestAny) { bestAny = t.dist; anyBest = t; }
        }
        if (best.Valid) return true;
        best = anyBest;
        return best.Valid;
    }

    /// <summary>How many players are in the session (1 solo).</summary>
    public static int Count
    {
        get
        {
            int n = 0;
            var all = All;
            for (int i = 0; i < all.Count; i++) if (all[i] != null) n++;
            return Mathf.Max(1, n);
        }
    }
}

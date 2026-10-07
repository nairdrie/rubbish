using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The Critters system's read-only view of the world: curb stops and their bags, neglected piles, how dirty the
/// city is, the local player and the truck. Everything here reads other systems through their public APIs.
///
/// The living-world Trash rework is being written in parallel, so the members the design doc promises
/// (PickupStop.IsNeglected / OldestBagAgeHours / All, CityCleanliness) are looked up by reflection once and used
/// when they exist; otherwise the same numbers are derived from frozen data (GameSession.CurbBags,
/// CurbBag.putOutAtHours, Economy.NeglectedHours, PickupStop.SpawnedBags). Nothing here writes game state.
/// </summary>
public static class CritterWorld
{
    public const int DefaultLayer = 0;
    public const int InteractableLayer = 3;
    public const int GroundLayer = 6;
    public const int WallsLayer = 7;
    /// <summary>Critter trigger colliders live on Ignore Raycast so interaction scans / hold clamps never see them.</summary>
    public const int CritterLayer = 2;

    public const int GroundMask = 1 << GroundLayer;
    /// <summary>
    /// What critters steer around: props (Default), the truck (Interactable), map walls, and Ground — the
    /// NeighborhoodBuilder puts house / car MeshColliders on Ground, so steep Ground faces count as walls
    /// (walkable tops and the 0.15 m curbs are below the probe and ignored).
    /// </summary>
    public const int ObstacleMask = (1 << DefaultLayer) | (1 << InteractableLayer) | (1 << WallsLayer) | (1 << GroundLayer);

    /// <summary>Obstacle probes start this high above the feet (plus the probe radius) so curbs are steppable.</summary>
    public const float StepHeight = 0.24f;

    /// <summary>A bag counts as "at the curb" for a stop when it's this close to the stop's marker.</summary>
    public const float CurbRadius = 4.5f;

    // ------------------------------------------------------------------ host / scene

    /// <summary>Host-authoritative logic runs on the server, or offline.</summary>
    public static bool IsHost
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }
    }

    /// <summary>True in the garage (ShopScene).</summary>
    public static bool InGarage => DepotRowWorld.Active ? DepotRowWorld.LocalIndoors : SceneManager.GetActiveScene().name == SceneController.GarageSceneName;

    /// <summary>True while a scene with curb stops (the Neighborhood) is the active scene.</summary>
    public static bool InNeighborhood
    {
        get
        {
            var s = SceneManager.GetActiveScene();
            if (s.name == "Neighborhood") return true;
            return !InGarage && Stops.Count > 0;
        }
    }

    // ------------------------------------------------------------------ stops

    static readonly List<PickupStop> stops = new List<PickupStop>();
    static float nextStopRefresh = -1f;

    public static IReadOnlyList<PickupStop> Stops
    {
        get
        {
            if (Time.unscaledTime >= nextStopRefresh || stops.Exists(s => s == null)) RefreshStops();
            return stops;
        }
    }

    /// <summary>Re-scan for PickupStops (called on scene load and every few seconds).</summary>
    public static void RefreshStops()
    {
        nextStopRefresh = Time.unscaledTime + 4f;
        stops.Clear();
        var all = StaticEnumerable(typeof(PickupStop), "All");
        if (all != null)
        {
            foreach (var o in all)
                if (o is PickupStop s && s != null && s.isActiveAndEnabled) stops.Add(s);
        }
        else
        {
            foreach (var s in UnityEngine.Object.FindObjectsByType<PickupStop>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                stops.Add(s);
        }
    }

    /// <summary>Resting bags lying at this stop's curb right now (not carried, not burst, not flying).</summary>
    public static int BagsAt(PickupStop stop, List<TrashBag> into)
    {
        into.Clear();
        if (stop == null) return 0;
        IReadOnlyList<TrashBag> spawned;
        try { spawned = stop.SpawnedBags; }
        catch { return 0; }
        if (spawned == null) return 0;
        Vector3 at = stop.transform.position;
        for (int i = 0; i < spawned.Count; i++)
        {
            var b = spawned[i];
            if (!IsGrabbableBag(b)) continue;
            Vector3 d = b.transform.position - at; d.y = 0f;
            if (d.sqrMagnitude > CurbRadius * CurbRadius) continue;
            into.Add(b);
        }
        return into.Count;
    }

    public static bool IsGrabbableBag(TrashBag b) =>
        b != null && !b.HasBurst && b.State != CarryState.Carried && b.State != CarryState.Consumed && !b.IsInScriptedFlight;

    /// <summary>Curb bags this house has out (world records, falls back to the spawned ones).</summary>
    public static int CurbBagCount(PickupStop stop)
    {
        if (stop == null) return 0;
        int n = 0;
        try { n = GameSession.CurbBagCountAt(stop.stopId); } catch { }
        if (n > 0) return n;
        return BagsAt(stop, scratchBags);
    }
    static readonly List<TrashBag> scratchBags = new List<TrashBag>();

    /// <summary>Age in in-game hours of the oldest bag at this stop (0 when it has none).</summary>
    public static double OldestBagAgeHours(PickupStop stop)
    {
        if (stop == null) return 0;
        if (TryInstance(stop, "OldestBagAgeHours", out double reflected)) return reflected;
        double oldest = 0;
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null || b.stopId != stop.stopId) continue;
            double age = GameClock.AgeHours(b.putOutAtHours);
            if (age > oldest) oldest = age;
        }
        return oldest;
    }

    /// <summary>A pile whose oldest bag is older than Economy.NeglectedHours: stinks, and Pile Goblins move in.</summary>
    public static bool IsNeglected(PickupStop stop)
    {
        if (stop == null) return false;
        if (TryInstanceBool(stop, "IsNeglected", out bool reflected)) return reflected;
        return OldestBagAgeHours(stop) > Economy.NeglectedHours;
    }

    /// <summary>Center of the bags at this stop (or the curb spot when it has none).</summary>
    public static Vector3 PileCenter(PickupStop stop, List<TrashBag> bags)
    {
        if (bags == null || bags.Count == 0) return stop != null ? stop.transform.position - stop.transform.forward * 0.3f : Vector3.zero;
        Vector3 c = Vector3.zero;
        for (int i = 0; i < bags.Count; i++) c += bags[i].transform.position;
        return c / bags.Count;
    }

    /// <summary>Highest renderer top among the bags (world y), or fallback.</summary>
    public static float PileTop(List<TrashBag> bags, float fallback)
    {
        float top = float.MinValue;
        if (bags != null)
        {
            for (int i = 0; i < bags.Count; i++)
            {
                var b = bags[i];
                if (b == null) continue;
                var r = b.GetComponentInChildren<Renderer>();
                if (r != null) top = Mathf.Max(top, r.bounds.max.y);
            }
        }
        return top > float.MinValue ? top : fallback;
    }

    // ------------------------------------------------------------------ cleanliness

    static float dirtCache;
    static float dirtCachedAt = -99f;

    /// <summary>0 = spotless city, 1 = STINKY. Drives raccoon / goblin pressure and the rabid chance.</summary>
    public static float Dirt01
    {
        get
        {
            if (Time.unscaledTime - dirtCachedAt < 1f) return dirtCache;
            dirtCachedAt = Time.unscaledTime;
            dirtCache = Mathf.Clamp01(ComputeDirt());
            return dirtCache;
        }
    }

    static float ComputeDirt()
    {
        if (TryCityCleanliness(out float clean01)) return 1f - clean01;
        // Fallback (same idea as the design doc): fresh bags barely count, late / neglected ones a lot.
        float sum = 0f;
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null) continue;
            double age = GameClock.AgeHours(b.putOutAtHours);
            sum += age <= Economy.OnTimeHours ? 0.08f
                 : age <= Economy.LateFloorHours ? 0.4f
                 : age <= Economy.NeglectedHours ? 0.75f
                 : 1f;
        }
        return sum / 22f;
    }

    static bool cleanlinessResolved;
    static MemberInfo cleanlinessMember;
    static bool cleanlinessIs01;

    static bool TryCityCleanliness(out float clean01)
    {
        clean01 = 1f;
        if (!cleanlinessResolved)
        {
            cleanlinessResolved = true;
            var t = FindType("CityCleanliness");
            if (t != null)
            {
                string[] names = { "Clean01", "Cleanliness01", "Value01", "Percent01", "Current01", "Percent", "Value", "Cleanliness", "Current", "CleanPercent" };
                foreach (var n in names)
                {
                    var m = (MemberInfo)t.GetProperty(n, BindingFlags.Public | BindingFlags.Static) ?? t.GetField(n, BindingFlags.Public | BindingFlags.Static);
                    if (m == null) continue;
                    Type vt = m is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)m).FieldType;
                    if (vt != typeof(float) && vt != typeof(double) && vt != typeof(int)) continue;
                    cleanlinessMember = m;
                    cleanlinessIs01 = n.Contains("01");
                    break;
                }
            }
        }
        if (cleanlinessMember == null) return false;
        try
        {
            object v = cleanlinessMember is PropertyInfo p ? p.GetValue(null) : ((FieldInfo)cleanlinessMember).GetValue(null);
            float f = Convert.ToSingle(v);
            clean01 = Mathf.Clamp01(cleanlinessIs01 ? f : f / 100f);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ player / truck

    /// <summary>The local player's feet when walking around (false while driving / chess / none).</summary>
    public static bool PlayerOnFoot(out PlayerController player, out Vector3 feet)
    {
        player = PlayerController.Local;
        feet = Vector3.zero;
        if (player == null || !player.isActiveAndEnabled) return false;
        feet = player.transform.position;
        return !player.IsDriving && !player.IsPlayingChess;
    }

    static TruckControllerV2 truck;
    static Rigidbody truckBody;
    static float nextTruckLookup;

    public static TruckControllerV2 Truck
    {
        get
        {
            if (truck == null && Time.unscaledTime >= nextTruckLookup)
            {
                nextTruckLookup = Time.unscaledTime + 1f;
                truck = TruckControllerV2.All.Count > 0 ? TruckControllerV2.All[0] : null;
                truckBody = truck != null ? truck.GetComponent<Rigidbody>() : null;
            }
            return truck;
        }
    }

    public static Vector3 TruckVelocity => Truck != null && truckBody != null ? truckBody.linearVelocity : Vector3.zero;

    /// <summary>Is a world point inside the truck's body box (local x ±1.5, z -2.6..4.2)?</summary>
    public static bool InsideTruckBox(Vector3 p, float pad = 0f)
    {
        var t = Truck;
        if (t == null) return false;
        Vector3 l = t.transform.InverseTransformPoint(p);
        return Mathf.Abs(l.x) < 1.5f + pad && l.z > -2.6f - pad && l.z < 4.2f + pad && l.y > -1.2f && l.y < 3.6f;
    }

    /// <summary>Is a world point inside a given truck's body box (local x ±1.5, z -2.6..4.2)?</summary>
    public static bool InsideTruckBox(TruckControllerV2 t, Vector3 p, float pad = 0f)
    {
        if (t == null) return false;
        Vector3 l = t.transform.InverseTransformPoint(p);
        return Mathf.Abs(l.x) < 1.5f + pad && l.z > -2.6f - pad && l.z < 4.2f + pad && l.y > -1.2f && l.y < 3.6f;
    }

    static readonly Dictionary<TruckControllerV2, Rigidbody> truckBodies = new Dictionary<TruckControllerV2, Rigidbody>();

    /// <summary>Any truck's velocity (rigidbodies cached).</summary>
    public static Vector3 VelocityOf(TruckControllerV2 t)
    {
        if (t == null) return Vector3.zero;
        if (!truckBodies.TryGetValue(t, out var rb) || rb == null)
        {
            if (truckBodies.Count > 16) truckBodies.Clear();
            rb = t.GetComponent<Rigidbody>();
            truckBodies[t] = rb;
        }
        return rb != null ? rb.linearVelocity : Vector3.zero;
    }

    /// <summary>
    /// Places critters never go (F4, Docs/Multiplayer.md §4.10): any shop interior, and in the one world the Depot Row lots
    /// (<paramref name="lots"/>), so nobody can lead rabid raccoons into a neighbour's bay.
    /// </summary>
    public static bool KeepOut(Vector3 p, bool lots)
    {
        if (DepotRowLayout.InteriorAt(p) >= 0) return true;
        return lots && DepotRowWorld.Active && DepotRowLayout.PadAt(p) >= 0;
    }

    // ------------------------------------------------------------------ helpers

    public static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    /// <summary>Ground height under p (Ground layer first, then anything solid), or p.y.</summary>
    public static float GroundY(Vector3 p, float fallback)
    {
        Vector3 o = new Vector3(p.x, p.y + 1.4f, p.z);
        if (Physics.Raycast(o, Vector3.down, out var hit, 4f, GroundMask, QueryTriggerInteraction.Ignore)) return hit.point.y;
        return fallback;
    }

    public static bool CameraCanSee(Vector3 p)
    {
        var cam = Camera.main;
        if (cam == null) return false;
        Vector3 v = cam.WorldToViewportPoint(p);
        return v.z > 0f && v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f;
    }

    static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

    static Type FindType(string name)
    {
        if (typeCache.TryGetValue(name, out var t)) return t;
        t = typeof(PickupStop).Assembly.GetType(name);
        typeCache[name] = t;
        return t;
    }

    static readonly Dictionary<string, MemberInfo> memberCache = new Dictionary<string, MemberInfo>();

    static MemberInfo Member(Type type, string name, bool isStatic)
    {
        string key = type.FullName + (isStatic ? "::" : ".") + name;
        if (memberCache.TryGetValue(key, out var m)) return m;
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        m = (MemberInfo)type.GetProperty(name, flags) ?? type.GetField(name, flags);
        memberCache[key] = m;
        return m;
    }

    static object Read(MemberInfo m, object target)
    {
        if (m is PropertyInfo p) return p.GetValue(target);
        if (m is FieldInfo f) return f.GetValue(target);
        return null;
    }

    static IEnumerable StaticEnumerable(Type type, string name)
    {
        var m = Member(type, name, true);
        if (m == null) return null;
        try { return Read(m, null) as IEnumerable; } catch { return null; }
    }

    static bool TryInstance(object target, string name, out double value)
    {
        value = 0;
        var m = Member(target.GetType(), name, false);
        if (m == null) return false;
        try { value = Convert.ToDouble(Read(m, target)); return true; } catch { return false; }
    }

    static bool TryInstanceBool(object target, string name, out bool value)
    {
        value = false;
        var m = Member(target.GetType(), name, false);
        if (m == null) return false;
        try { var o = Read(m, target); if (o is bool b) { value = b; return true; } return false; } catch { return false; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        stops.Clear();
        nextStopRefresh = -1f;
        dirtCachedAt = -99f;
        truck = null; truckBody = null; nextTruckLookup = 0f; truckBodies.Clear();
        cleanlinessResolved = false; cleanlinessMember = null;
        memberCache.Clear(); typeCache.Clear();
    }
}

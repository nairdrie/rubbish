using System;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Network feel for the garbage truck (Docs/Multiplayer.md §8.7.4, package B2). <c>Setup_Coop</c> adds it to
/// <c>Truck V2.prefab</c>; it never edits <see cref="TruckControllerV2"/> (owned by the Flow/Progression B work).
/// <list type="bullet">
/// <item><b>Rigidbody interpolation for the driving owner only.</b> Whoever drives owns the truck and simulates it; the
/// truck camera follows the rigidbody, so without interpolation it judders at frame rates above the physics rate. Everyone
/// else sees the truck through <c>NetworkTransform</c> interpolation, and <c>NetworkRigidbody</c> keeps their copy kinematic
/// with interpolation off, so this only touches the driver's copy and puts back the prefab's setting afterwards.</item>
/// <item><b>A driver who disconnects frees the seat (host only).</b> The prefab has <c>DontDestroyWithOwner</c> on, so when
/// a guest's game drops mid-drive NGO hands the truck back to the host instead of destroying it. The truck's driver
/// variable would still name the departed client and nobody could get in, so the host clears it.
/// <c>TruckControllerV2</c> has no public call for that yet, so this uses its private <c>driverClientId</c> /
/// <c>ServerClearDriver</c> through reflection, and logs one warning (and does nothing) if they're ever renamed.
/// Integration note: give TruckControllerV2 a public server-side release and drop the reflection.</item>
/// </list>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class TruckNetFeel : MonoBehaviour
{
    Rigidbody rb;
    NetworkObject netObj;
    TruckControllerV2 truck;
    RigidbodyInterpolation prefabInterpolation;
    bool interpolating;
    NetworkManager hooked;

    static bool reflected;
    static FieldInfo driverField;
    static MethodInfo clearDriver;
    static bool reflectionWarned;

    /// <summary>True while this copy is the driving owner and has interpolation on.</summary>
    public bool Interpolating => interpolating;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        netObj = GetComponent<NetworkObject>();
        truck = GetComponent<TruckControllerV2>();
        prefabInterpolation = rb != null ? rb.interpolation : RigidbodyInterpolation.None;
    }

    void OnDisable()
    {
        Hook(null);
        if (interpolating && rb != null) rb.interpolation = prefabInterpolation;
        interpolating = false;
    }

    void OnDestroy() => Hook(null);

    /// <summary>The local machine drives and simulates this truck.</summary>
    public bool IsDrivingOwner
    {
        get
        {
            if (truck == null) return false;
            if (netObj != null && netObj.IsSpawned) return netObj.IsOwner && truck.IsLocalDriver;
            return truck.IsLocalPlayerSeated; // offline preview: the seated local player drives
        }
    }

    void Update()
    {
        if (rb == null) return;

        // NetworkRigidbody resets interpolation when ownership or kinematic state changes, so check every frame.
        bool want = IsDrivingOwner && !rb.isKinematic;
        if (want)
        {
            if (rb.interpolation != RigidbodyInterpolation.Interpolate) rb.interpolation = RigidbodyInterpolation.Interpolate;
            interpolating = true;
        }
        else if (interpolating)
        {
            if (rb.interpolation == RigidbodyInterpolation.Interpolate) rb.interpolation = prefabInterpolation;
            interpolating = false;
        }

        var nm = netObj != null && netObj.IsSpawned ? netObj.NetworkManager : null;
        Hook(nm != null && nm.IsServer ? nm : null);
    }

    void Hook(NetworkManager nm)
    {
        if (hooked == nm) return;
        if (hooked != null) hooked.OnClientDisconnectCallback -= OnClientDisconnected;
        hooked = nm;
        if (hooked != null) hooked.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (truck == null || hooked == null || !hooked.IsServer || clientId == NetworkManager.ServerClientId) return;
        if (netObj == null || !netObj.IsSpawned || !truck.HasDriver) return;
        if (!TryGetDriver(truck, out ulong driver) || driver != clientId) return;
        if (TryClearDriver(truck))
            Debug.Log($"[TruckNetFeel] The driver (client {clientId}) left; the truck is the host's again and free to drive.");
    }

    static bool Reflect()
    {
        if (!reflected)
        {
            reflected = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            driverField = typeof(TruckControllerV2).GetField("driverClientId", F);
            clearDriver = typeof(TruckControllerV2).GetMethod("ServerClearDriver", F, null, Type.EmptyTypes, null);
            if (driverField == null || driverField.FieldType != typeof(NetworkVariable<ulong>)) driverField = null;
        }
        if (driverField != null && clearDriver != null) return true;
        if (!reflectionWarned)
        {
            reflectionWarned = true;
            Debug.LogWarning("[TruckNetFeel] TruckControllerV2.driverClientId / ServerClearDriver not found; a guest who " +
                             "disconnects while driving will leave the driver seat taken until the scene changes.");
        }
        return false;
    }

    /// <summary>The client the truck thinks is driving (ulong.MaxValue = nobody). False if it can't be read.</summary>
    public static bool TryGetDriver(TruckControllerV2 t, out ulong driver)
    {
        driver = ulong.MaxValue;
        if (t == null || !Reflect()) return false;
        try
        {
            if (driverField.GetValue(t) is NetworkVariable<ulong> v) { driver = v.Value; return true; }
        }
        catch (Exception e) { Debug.LogWarning("[TruckNetFeel] Couldn't read the driver: " + e.Message); }
        return false;
    }

    static bool TryClearDriver(TruckControllerV2 t)
    {
        if (t == null || !Reflect()) return false;
        try
        {
            clearDriver.Invoke(t, null);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[TruckNetFeel] Couldn't free the driver seat: " + (e.InnerException ?? e).Message);
            return false;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => reflectionWarned = false;
}

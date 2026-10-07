using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One business in the Town, as every machine sees it (Docs/Multiplayer.md §13.1). Host-written in
/// <see cref="BusinessRegistry"/>; read through <see cref="Businesses"/>.
/// </summary>
public struct BusinessInfo : INetworkSerializable, IEquatable<BusinessInfo>
{
    /// <summary><see cref="flags"/>: this is the host's business (lot 0 by convention, the Town's save).</summary>
    public const byte FlagHost = 1;

    public FixedString64Bytes id;
    /// <summary>The owner's <see cref="PlayerKey"/> value.</summary>
    public FixedString64Bytes ownerKey;
    /// <summary>The business name (≤24 characters, <see cref="BusinessColors.CleanName"/>).</summary>
    public FixedString64Bytes name;
    /// <summary>The owner's display name (Steam persona or dev name), for "Nick's" on the sign.</summary>
    public FixedString32Bytes ownerName;
    public ulong ownerClientId;
    /// <summary>Cash in cents, as the host last applied it (the HUD of other players, payroll checks).</summary>
    public long cashCents;
    /// <summary>The owner's colour, 0..3 (= their roster slot; <see cref="BusinessColors"/>).</summary>
    public byte color;
    /// <summary>The Depot Row lot, 0..3, or -1 until N3 assigns one (<see cref="Businesses.AssignLot"/>).</summary>
    public sbyte lot;
    public byte flags;
    /// <summary>Truck load and capacity (kg) for the truck this business owns, so guests can say "Truck's full!" early.</summary>
    public float truckKg, truckCapKg;
    /// <summary>Saved operating hours for municipal startup protection; host advances it while this business is online.</summary>
    public float municipalHours;

    public bool IsValid => id.Length > 0;
    public bool IsHostBusiness => (flags & FlagHost) != 0;
    public string Id => id.ToString();
    public string Name => name.ToString();
    public string OwnerName => ownerName.ToString();
    public Color32 Tint => BusinessColors.For(color);
    /// <summary>"Nick's" (the small line on the sign).</summary>
    public string OwnerLine => BusinessColors.OwnerLine(OwnerName);
    public float Cash => cashCents / 100f;
    public bool TryGetOwnerKey(out PlayerKey k) => PlayerKey.TryParse(ownerKey.ToString(), out k);

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref ownerKey);
        s.SerializeValue(ref name);
        s.SerializeValue(ref ownerName);
        s.SerializeValue(ref ownerClientId);
        s.SerializeValue(ref cashCents);
        s.SerializeValue(ref color);
        s.SerializeValue(ref lot);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref truckKg);
        s.SerializeValue(ref truckCapKg);
        s.SerializeValue(ref municipalHours);
    }

    public bool Equals(BusinessInfo o) =>
        id.Equals(o.id) && ownerKey.Equals(o.ownerKey) && name.Equals(o.name) && ownerName.Equals(o.ownerName) &&
        ownerClientId == o.ownerClientId && cashCents == o.cashCents && color == o.color && lot == o.lot && flags == o.flags &&
        truckKg.Equals(o.truckKg) && truckCapKg.Equals(o.truckCapKg) && municipalHours.Equals(o.municipalHours);

    public override bool Equals(object obj) => obj is BusinessInfo o && Equals(o);
    public override int GetHashCode() => id.GetHashCode();
    public override string ToString() => $"{Name} ({Id}) owner {ownerKey} c{ownerClientId} colour {color} lot {lot} ${Cash:0.00}";
}

/// <summary>
/// The businesses in this Town (Docs/Multiplayer.md §13, package N1). Every player owns one: the host's is their save; a
/// guest picks one of theirs (or starts a new one) when they join, and takes it home when they leave.
/// <para><b>Read (everyone):</b> <see cref="All"/>, <see cref="Local"/>, <see cref="Get"/>, <see cref="TryGetForClient"/>,
/// <see cref="Changed"/>. Without a session (menus, edit mode) <see cref="All"/> holds just the local business.</para>
/// <para><b>Host:</b> <see cref="AssignLot"/> (N3: Depot Row lots), <see cref="HostApply"/> / <see cref="HostApplyRep"/> (what
/// <see cref="Ledger"/> calls inside <see cref="Ledger.ToBusiness"/>), <see cref="HostCargo"/> (bags in a guest business's
/// truck), <see cref="SetTruckLoad"/>.</para>
/// <para><b>Money</b> always moves through <see cref="Ledger"/>: <c>using (Ledger.ToBusiness(id)) Ledger.Credit(…)</c> on the host
/// pays any business (N4 payroll, N5 trades).</para>
/// </summary>
public static class Businesses
{

    /// <summary>The list changed (a business opened, closed, was renamed, got a lot, or its cash moved).</summary>
    public static event Action Changed;

    /// <summary>The registry of the current session, or null (menus, edit mode, before it spawns).</summary>
    public static BusinessRegistry Registry => BusinessRegistry.Instance != null && BusinessRegistry.Instance.IsSpawned ? BusinessRegistry.Instance : null;

    /// <summary>The id of the business this machine's <see cref="GameSession"/> holds (made on first use).</summary>
    public static string LocalId => GameSession.EnsureBusinessId();

    /// <summary>This machine plays the business's host: hosting, solo, or no session at all.</summary>
    public static bool IsAuthority
    {
        get
        {
            var r = Registry;
            if (r != null) return r.IsServer;
            return !GameSession.IsConnectedGuest;
        }
    }

    /// <summary>Every business in the Town (a fresh snapshot each call: safe to keep, safe to call other members while
    /// looping over it).</summary>
    public static IReadOnlyList<BusinessInfo> All
    {
        get
        {
            int n = Count;
            var copy = new List<BusinessInfo>(n);
            for (int i = 0; i < n; i++) copy.Add(At(i));
            return copy;
        }
    }

    /// <summary>Businesses in the Town (1 without a session).</summary>
    static int Count
    {
        get
        {
            var r = Registry;
            return r == null ? 1 : r.List.Count;
        }
    }

    static BusinessInfo At(int i)
    {
        var r = Registry;
        return r == null ? OfflineLocal() : r.List[i];
    }

    /// <summary>Businesses open in the Town right now (≥ 1). Trash put-out scales with this (§13.7).</summary>
    public static int OnlineCount
    {
        get
        {
            var r = Registry;
            return r == null ? 1 : Mathf.Max(1, r.List.Count);
        }
    }

    /// <summary>The local player's business (in a session: once the host has it; offline: from GameSession).</summary>
    public static BusinessInfo Local
    {
        get
        {
            var r = Registry;
            if (r == null) return OfflineLocal();
            return TryGet(LocalId, out var b) ? b : default;
        }
    }

    /// <summary>The host's business (the Town's owner), or default.</summary>
    public static BusinessInfo Host
    {
        get
        {
            for (int i = 0, n = Count; i < n; i++) { var b = At(i); if (b.IsHostBusiness) return b; }
            return default;
        }
    }

    public static bool TryGet(string id, out BusinessInfo b)
    {
        b = default;
        if (string.IsNullOrEmpty(id)) return false;
        for (int i = 0, n = Count; i < n; i++)
        {
            var x = At(i);
            if (x.id.ToString() == id) { b = x; return true; }
        }
        return false;
    }

    /// <summary>The business with this id, or default (<see cref="BusinessInfo.IsValid"/> false).</summary>
    public static BusinessInfo Get(string id) => TryGet(id, out var b) ? b : default;

    /// <summary>The business owned by a connected client.</summary>
    public static bool TryGetForClient(ulong clientId, out BusinessInfo b)
    {
        for (int i = 0, n = Count; i < n; i++)
        {
            var x = At(i);
            if (x.ownerClientId == clientId && x.IsValid) { b = x; return true; }
        }
        b = default;
        return false;
    }

    /// <summary>The business owned by a player.</summary>
    public static bool TryGetForOwner(PlayerKey k, out BusinessInfo b)
    {
        for (int i = 0, n = Count; i < n && k.IsValid; i++)
        {
            var x = At(i);
            if (x.ownerKey.ToString() == k.Value) { b = x; return true; }
        }
        b = default;
        return false;
    }

    // ------------------------------------------------------------------ host API

    /// <summary>Host: put a business on a Depot Row lot (0..3; -1 = none). Remembered for its rejoin. False if not host.</summary>
    public static bool AssignLot(string id, int lot)
    {
        var r = Registry;
        if (r == null || !r.IsServer) return false;
        return r.ServerAssignLot(id, lot);
    }

    /// <summary>
    /// Host: apply a cash change of <paramref name="cents"/> to business <paramref name="id"/> (kind = Ledger.Kind*), tell its
    /// owner and post it on <see cref="Ledger.PostedAnyBusiness"/>. A spend is refused when it can't be afforded. Called by
    /// <see cref="Ledger"/>; use <c>using (Ledger.ToBusiness(id))</c> instead of calling this.
    /// </summary>
    public static bool HostApply(string id, string kind, long cents, string reason, string ledgerId, Vector3? pos, string districtId)
    {
        var r = Registry;
        if (r == null || !r.IsServer)
        {
            Debug.LogWarning($"[Businesses] Only the host can move business {id}'s money ({kind} {cents}c \"{reason}\"); dropped.");
            return false;
        }
        return r.ServerApplyCash(id, kind, cents, reason, ledgerId, pos, districtId, allowOverdraw: false);
    }

    /// <summary>Host: Rep for business <paramref name="id"/> (its owner's game applies it). Called by <see cref="Ledger"/>.</summary>
    public static bool HostApplyRep(string id, float applied, string repId, string districtId, Vector3? pos)
    {
        var r = Registry;
        if (r == null || !r.IsServer) return false;
        return r.ServerApplyRep(id, applied, repId, districtId, pos);
    }

    /// <summary>
    /// Host: the truck cargo of a business that isn't the host's (the host's own cargo is <see cref="GameSession.TruckBags"/>).
    /// Contents never leave the host until N3 unloads them in that business's shop. Null for an unknown id.
    /// </summary>
    public static List<BagContents> HostCargo(string id)
    {
        var r = Registry;
        return r != null && r.IsServer ? r.ServerCargo(id) : null;
    }

    /// <summary>Host: publish a business's truck load (kg) and capacity so every machine can show "Truck's full!".</summary>
    public static void SetTruckLoad(string id, float kg, float capacityKg)
    {
        var r = Registry;
        if (r != null && r.IsServer) r.ServerSetTruck(id, kg, capacityKg);
    }

    /// <summary>Truck load / capacity of a business (host: exact; guests: as last replicated).</summary>
    public static bool TryGetTruckLoad(string id, out float kg, out float capacityKg)
    {
        kg = 0f; capacityKg = 0f;
        if (string.IsNullOrEmpty(id)) return false;
        if (IsAuthority && id == LocalId) { kg = GameSession.TruckLoadKg; capacityKg = GameSession.TruckCapacityKg; return true; }
        var r = Registry;
        if (r != null && r.IsServer)
        {
            var cargo = r.ServerCargo(id);
            if (cargo != null)
            {
                foreach (var b in cargo) if (b != null) kg += b.TotalMassKg;
                capacityKg = TryGet(id, out var bi) && bi.truckCapKg > 0f ? bi.truckCapKg : Economy.StartingTruckCapacityKg;
                return true;
            }
        }
        if (!TryGet(id, out var info)) return false;
        kg = info.truckKg;
        capacityKg = info.truckCapKg > 0f ? info.truckCapKg : Economy.StartingTruckCapacityKg;
        return true;
    }

    // ------------------------------------------------------------------ owner API

    /// <summary>
    /// Rename the local player's business (≤24 characters, cleaned): saved with it, and shown to everyone in the Town (the host
    /// updates its record; a guest's rename is sent to the host).
    /// </summary>
    public static void RenameLocal(string name)
    {
        GameSession.SetBusinessName(name);
        var r = Registry;
        if (r != null && !r.IsServer) r.SendRename(GameSession.BusinessName);
    }

    // ------------------------------------------------------------------ guest API

    /// <summary>Guest: ask the host to apply a cash change to this player's own business (what <see cref="Ledger"/> does on a guest).</summary>
    public static void RequestLedger(string kind, long cents, string reason, string ledgerId, Vector3? pos, string districtId)
    {
        var r = Registry;
        if (r == null || r.IsServer)
        {
            Debug.LogWarning($"[Businesses] No host to send \"{reason}\" ({cents}c) to; dropped.");
            return;
        }
        r.SendLedgerRequest(kind, cents, reason, ledgerId, pos, districtId);
    }

    // ------------------------------------------------------------------ internals

    static BusinessInfo OfflineLocal()
    {
        var b = new BusinessInfo
        {
            ownerClientId = NetworkManager.ServerClientId,
            cashCents = GameSession.CashCents,
            color = 0,
            lot = 0,
            flags = BusinessInfo.FlagHost,
            truckKg = GameSession.TruckLoadKg,
            truckCapKg = GameSession.TruckCapacityKg,
            municipalHours = MunicipalEconomy.LocalHours,
        };
        b.id.CopyFromTruncated(LocalId);
        b.name.CopyFromTruncated(GameSession.BusinessName);
        return b;
    }

    internal static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Changed = null;
}

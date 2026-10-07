using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Host-accepted upgrade levels for each online business. The owning save remains on its owner's machine;
/// the host needs this small snapshot to run that lot's garage machines and clients need it to show their parts.</summary>
public struct BusinessUpgradeInfo : INetworkSerializable, IEquatable<BusinessUpgradeInfo>
{
    public FixedString64Bytes businessId;
    public ulong ownerClientId;
    public ulong levels;
    // Protocol 3/content 2: garage pack is full. Bit0 = personal pole; bits1–2 = truck headlights (0..2).
    public byte gearBits;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref businessId);
        s.SerializeValue(ref ownerClientId);
        s.SerializeValue(ref levels);
        s.SerializeValue(ref gearBits);
    }

    public bool Equals(BusinessUpgradeInfo other) => businessId.Equals(other.businessId) &&
        ownerClientId == other.ownerClientId && levels == other.levels && gearBits == other.gearBits;
    public override bool Equals(object obj) => obj is BusinessUpgradeInfo other && Equals(other);
    public override int GetHashCode() => businessId.GetHashCode() ^ ownerClientId.GetHashCode() ^ levels.GetHashCode() ^ gearBits;
}

/// <summary>Transient host state for one lot's sorter; claim serial prevents a late phase from a previous owner.</summary>
public struct BusinessRobotInfo : INetworkSerializable, IEquatable<BusinessRobotInfo>
{
    public byte lot, phase;
    public ulong ownerClientId;
    public int plopSerial;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref lot);
        s.SerializeValue(ref phase);
        s.SerializeValue(ref ownerClientId);
        s.SerializeValue(ref plopSerial);
    }

    public bool Equals(BusinessRobotInfo other) => lot == other.lot && phase == other.phase &&
        ownerClientId == other.ownerClientId && plopSerial == other.plopSerial;
    public override bool Equals(object obj) => obj is BusinessRobotInfo other && Equals(other);
    public override int GetHashCode() => lot ^ (phase << 8) ^ plopSerial;
}

[NetSessionComponent(Order = 24)]
public sealed class BusinessUpgradeNet : NetworkBehaviour
{
    public static BusinessUpgradeNet Instance { get; private set; }
    public static event Action Changed;

    // Four bits per level; all maxima come from ShopIds/Scrapazon. Keep this order stable on both peers.
    static readonly string[] ids = {
        ShopIds.AutoTipper, ShopIds.ConveyorBelt, ShopIds.SortingTable, ShopIds.CompactorMk2,
        ShopIds.RecyclingContracts, ShopIds.HazmatContract, ShopIds.MagnusBrain, ShopIds.MagnusRailMotor,
        ShopIds.MagnusGripper, ShopIds.MagnusVision, ShopIds.TrophyShelf, ShopIds.DisplayPlankShelf,
        ShopIds.DisplayGlassCase, ShopIds.DisplayPedestal, ShopIds.DisplayVault, ShopIds.GarageDiscoBall,
    };
    static readonly int[] maxima = { 2, 3, 2, 3, 4, 2, 4, 1, 2, 2, 2, 1, 1, 1, 1, 1 };
    const float SendInterval = 0.25f;
    const string MsgUpgrades = "rb.biz.upgrades";
    const string MsgShoo = "rb.biz.magnus.shoo";
    NetworkList<BusinessUpgradeInfo> snapshots;
    NetworkList<BusinessRobotInfo> robots;
    readonly HashSet<string> deliveredItems = new HashSet<string>(StringComparer.Ordinal);
    string sentId;
    ulong sentLevels;
    byte sentGear;
    float lastSentAt;
    float nextSend;

    void Awake()
    {
        snapshots = new NetworkList<BusinessUpgradeInfo>();
        robots = new NetworkList<BusinessRobotInfo>();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        snapshots.OnListChanged += OnListChanged;
        if (IsServer)
        {
            BusinessRegistry.Closing += OnClosing;
            SyncHost();
        }
        Changed?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer) BusinessRegistry.Closing -= OnClosing;
        snapshots.OnListChanged -= OnListChanged;
        if (Instance == this) Instance = null;
        Changed?.Invoke();
    }

    public override void OnDestroy()
    {
        if (IsServer) BusinessRegistry.Closing -= OnClosing;
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void OnListChanged(NetworkListEvent<BusinessUpgradeInfo> _) => Changed?.Invoke();

    void Update()
    {
        if (!IsSpawned || Time.realtimeSinceStartup < nextSend) return;
        nextSend = Time.realtimeSinceStartup + SendInterval;
        if (IsServer) { SyncHost(); return; }
        if (!BusinessSlots.Declared || !Businesses.TryGet(GameSession.BusinessId, out var b) ||
            b.ownerClientId != NetworkManager.LocalClientId) return;
        ulong packed = PackLocal();
        byte gear = LocalGear();
        int current = IndexOf(b.Id);
        if (current >= 0 && snapshots[current].ownerClientId == b.ownerClientId && snapshots[current].levels == packed &&
            snapshots[current].gearBits == gear)
        {
            sentId = b.Id;
            sentLevels = packed;
            sentGear = gear;
            return;
        }
        // The host may have ignored an early declaration or a rate-limited message. Retry until its echo matches.
        if (sentId == b.Id && sentLevels == packed && sentGear == gear && Time.realtimeSinceStartup < lastSentAt + 2f) return;
        var id = new FixedString64Bytes();
        id.CopyFromTruncated(b.Id);
        DeclareRpc(id, packed, gear);
        sentId = b.Id;
        sentLevels = packed;
        sentGear = gear;
        lastSentAt = Time.realtimeSinceStartup;
    }

    void SyncHost()
    {
        if (!Businesses.TryGet(GameSession.BusinessId, out var b) || !b.IsHostBusiness) return;
        Put(b, PackLocal(), LocalGear());
    }

    static byte LocalHeadlights() => (byte)Mathf.Clamp(GameSession.GetUpgradeLevel(ShopIds.TruckHeadlights), 0, 2);
    static byte LocalGear() => (byte)((LocalHeadlights() << 1) | (GameSession.GetUpgradeLevel(ShopIds.GrabberPole) > 0 ? 1 : 0));

    static ulong PackLocal()
    {
        ulong packed = 0;
        for (int i = 0; i < ids.Length; i++)
            packed |= (ulong)Mathf.Clamp(GameSession.GetUpgradeLevel(ids[i]), 0, maxima[i]) << (i * 4);
        return packed;
    }

    static bool ValidPacked(ulong packed)
    {
        for (int i = 0; i < ids.Length; i++)
            if (((packed >> (i * 4)) & 15UL) > (ulong)maxima[i]) return false;
        return ids.Length == 16 || packed >> (ids.Length * 4) == 0UL;
    }

    static bool ValidDeclaration(ulong packed, byte gear) => (gear & ~7) == 0 && (gear >> 1) <= 2 && ValidPacked(packed);

    int IndexOf(string id)
    {
        for (int i = 0; i < snapshots.Count; i++) if (snapshots[i].businessId.ToString() == id) return i;
        return -1;
    }

    void Put(BusinessInfo b, ulong packed, byte gear)
    {
        var entry = new BusinessUpgradeInfo { businessId = b.id, ownerClientId = b.ownerClientId, levels = packed, gearBits = gear };
        int i = IndexOf(b.Id);
        if (i < 0) snapshots.Add(entry);
        else if (!snapshots[i].Equals(entry)) snapshots[i] = entry;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void DeclareRpc(FixedString64Bytes businessId, ulong packed, byte gear, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgUpgrades).Allow(sender, 80, Time.realtimeSinceStartupAsDouble) ||
            !ValidDeclaration(packed, gear) || !Businesses.TryGet(businessId.ToString(), out var b) ||
            b.IsHostBusiness || b.ownerClientId != sender) return;
        Put(b, packed, gear);
    }

    void OnClosing(string id, ulong owner)
    {
        int i = IndexOf(id);
        if (i >= 0 && snapshots[i].ownerClientId == owner) snapshots.RemoveAt(i);
        for (int r = robots.Count - 1; r >= 0; r--) if (robots[r].ownerClientId == owner) robots.RemoveAt(r);
        // A rejoining business can reuse item UIDs already sorted this session only if it is a new item;
        // keep the dedupe set until session end to reject a late duplicate RPC.
    }

    public static bool TryOwnerForLot(int lot, out string businessId, out ulong ownerClientId)
    {
        businessId = null;
        ownerClientId = 0;
        var row = DepotRowNet.Instance;
        if (!DepotRowWorld.Active || row == null || !row.TryGetLot(lot, out var state) ||
            !state.Claimed || !state.Online || !Businesses.TryGetForClient(state.ownerClientId, out var b) || b.lot != lot)
            return false;
        businessId = b.Id;
        ownerClientId = b.ownerClientId;
        return true;
    }

    public static bool HasStateForLot(int lot)
    {
        if (!TryOwnerForLot(lot, out var id, out var owner)) return false;
        if (id == GameSession.BusinessId) return true;
        var n = Instance;
        int i = n != null && n.IsSpawned ? n.IndexOf(id) : -1;
        return i >= 0 && n.snapshots[i].ownerClientId == owner;
    }

    public static int LevelForLot(int lot, string upgradeId)
    {
        if (!DepotRowWorld.Active || lot < 0) return GameSession.GetUpgradeLevel(upgradeId);
        if (!TryOwnerForLot(lot, out var id, out var owner)) return 0;
        if (id == GameSession.BusinessId) return GameSession.GetUpgradeLevel(upgradeId);
        var n = Instance;
        int i = n != null && n.IsSpawned ? n.IndexOf(id) : -1;
        if (i < 0 || n.snapshots[i].ownerClientId != owner) return 0;
        if (upgradeId == ShopIds.TruckHeadlights) return (n.snapshots[i].gearBits >> 1) & 3;
        int key = Array.IndexOf(ids, upgradeId);
        if (key < 0) return 0;
        return (int)((n.snapshots[i].levels >> (key * 4)) & 15UL);
    }

    /// <summary>Lights belong to the truck's business, not its driver, employer or the host's personal save.</summary>
    public static int HeadlightLevelForTruck(TruckControllerV2 truck)
    {
        if (truck == null) return 0;
        if (!DepotRowWorld.Active) return LocalHeadlights();
        var row = DepotRowNet.Instance;
        if (row == null || !truck.IsSpawned) return 0;
        int lot = row.LotOfTruck(truck.NetworkObjectId);
        return lot >= 0 ? LevelForLot(lot, ShopIds.TruckHeadlights) : 0;
    }

    /// <summary>Personal gear comes from the actor's own business save, never from their employer's account.</summary>
    public static bool HasGrabberPoleForClient(ulong client)
    {
        if (!Businesses.TryGetForClient(client, out var b)) return false;
        if (b.Id == GameSession.BusinessId) return GameSession.GetUpgradeLevel(ShopIds.GrabberPole) > 0;
        var n = Instance;
        int i = n != null && n.IsSpawned ? n.IndexOf(b.Id) : -1;
        return i >= 0 && n.snapshots[i].ownerClientId == client && (n.snapshots[i].gearBits & 1) != 0;
    }

    /// <summary>Host publishes only changes; observers and late joiners read the same phase as the station owner.</summary>
    public static void PublishRobotPhase(int lot, MagnusSorter.State phase)
    {
        var n = Instance;
        var row = DepotRowNet.Instance;
        if (n == null || !n.IsServer || !DepotRowWorld.Active || row == null ||
            !row.TryGetLot(lot, out var state) || !state.Claimed || !state.Online) return;
        var next = new BusinessRobotInfo { lot = (byte)lot, phase = (byte)phase,
            ownerClientId = state.ownerClientId, plopSerial = state.plopSerial };
        for (int i = 0; i < n.robots.Count; i++)
        {
            if (n.robots[i].lot != lot) continue;
            if (!n.robots[i].Equals(next)) n.robots[i] = next;
            return;
        }
        n.robots.Add(next);
    }

    public static MagnusSorter.State PhaseForLot(int lot)
    {
        var n = Instance;
        var row = DepotRowNet.Instance;
        if (n == null || !n.IsSpawned || row == null || !row.TryGetLot(lot, out var state)) return MagnusSorter.State.Off;
        for (int i = 0; i < n.robots.Count; i++)
        {
            var robot = n.robots[i];
            if (robot.lot == lot && robot.ownerClientId == state.ownerClientId &&
                robot.plopSerial == state.plopSerial && state.Online && robot.phase <= (byte)MagnusSorter.State.Returning)
                return (MagnusSorter.State)robot.phase;
        }
        return MagnusSorter.State.Off;
    }

    /// <summary>Guest station prompt asks the host to shoo the authoritative sorter.</summary>
    public static void RequestRobotShoo(int lot)
    {
        var n = Instance;
        if (n != null && n.IsSpawned && !n.IsServer && lot >= 0) n.ShooRpc((byte)lot);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ShooRpc(byte lot, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!SessionService.GuardFor(MsgShoo).Allow(sender, 16, Time.realtimeSinceStartupAsDouble) ||
            !TryOwnerForLot(lot, out _, out _) || !NetworkManager.ConnectedClients.TryGetValue(sender, out var client) ||
            client.PlayerObject == null) return;
        var player = client.PlayerObject.GetComponent<PlayerController>();
        var depot = DepotRowScene.Lot(lot);
        var sorter = depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<MagnusSorter>(true) : null;
        if (player == null || sorter == null || sorter.rig == null || sorter.rig.station == null) return;
        Vector3 d = player.transform.position - sorter.rig.station.transform.position;
        if (Mathf.Abs(d.y) > 2f || new Vector2(d.x, d.z).sqrMagnitude > 4f * 4f) return;
        sorter.Shoo();
    }

    /// <summary>Host: route a robot's completed bin result to the business owning this lot. False = legacy solo path.</summary>
    public static bool ReportRobotSort(int lot, ItemInstance item, ItemCategory bin, bool correct, bool byMagnus)
    {
        if (!DepotRowWorld.Active || lot < 0) return false;
        if (!TryOwnerForLot(lot, out var id, out var owner) || Instance == null || !Instance.IsServer) return true;
        Instance.RouteSort(id, owner, item, bin, correct, byMagnus);
        return true;
    }

    /// <summary>Host: deliver a rescued treasure to its lot owner exactly once. False = legacy solo path.</summary>
    public static bool KeepRobotItem(int lot, ItemInstance item)
    {
        if (!DepotRowWorld.Active || lot < 0) return false;
        if (!TryOwnerForLot(lot, out var id, out var owner) || Instance == null || !Instance.IsServer) return true;
        Instance.RouteKeep(id, owner, item, false);
        return true;
    }

    /// <summary>Host: record an item disposed of by a robot without inventing a bin sort or Rep fact.</summary>
    public static bool RecordRobotItem(int lot, ItemInstance item, bool recycled)
    {
        if (!DepotRowWorld.Active || lot < 0) return false;
        if (!TryOwnerForLot(lot, out var id, out var owner) || Instance == null || !Instance.IsServer) return true;
        if (item == null || item.Def == null) return true;
        if (owner == Instance.NetworkManager.LocalClientId) GameSession.RecordSorted(item, recycled);
        else
        {
            string json = JsonUtility.ToJson(item);
            if (json.Length > 4000) return true;
            var fixedId = new FixedString64Bytes(); fixedId.CopyFromTruncated(id);
            Instance.RobotItemRpc(fixedId, json, recycled, Instance.RpcTarget.Single(owner, RpcTargetUse.Temp));
        }
        return true;
    }

    void RouteSort(string id, ulong owner, ItemInstance item, ItemCategory bin, bool correct, bool byMagnus)
    {
        if (!EbaeItemValidation.IsValid(item, out _)) return;
        string json = JsonUtility.ToJson(item);
        if (json.Length > 4000) return;
        string district = SortBin.DistrictOf(item);
        if (owner != NetworkManager.LocalClientId)
            using (Ledger.ToBusiness(id))
                Ledger.Rep(correct ? RepIds.ItemSorted : RepIds.Contamination,
                    correct ? RepValues.ItemSorted : RepValues.Contamination, district, null);
        if (owner == NetworkManager.LocalClientId)
        {
            if (correct) GameSession.RecordSorted(item, ItemCategoryInfo.IsRecyclable(bin));
            ProgressionEvents.RaiseItemSorted(new ItemSortedFact { item = item, bin = bin, correct = correct,
                byMagnus = byMagnus, districtId = district });
            if (correct && bin == ItemCategory.Valuable) RouteKeep(id, owner, item, byMagnus);
            return;
        }
        var fixedId = new FixedString64Bytes(); fixedId.CopyFromTruncated(id);
        RobotSortRpc(fixedId, json, bin, correct, byMagnus, RpcTarget.Single(owner, RpcTargetUse.Temp));
        if (correct && bin == ItemCategory.Valuable) RouteKeep(id, owner, item, byMagnus);
    }

    void RouteKeep(string id, ulong owner, ItemInstance item, bool byMagnus)
    {
        if (!EbaeItemValidation.IsValid(item, out _)) return;
        string json = JsonUtility.ToJson(item);
        if (json.Length > 4000 || !deliveredItems.Add(id + "/" + item.uid)) return;
        var rarity = item.Def.rarity;
        string repId = rarity == Rarity.Legendary ? RepIds.TreasureLegendary :
            rarity == Rarity.Epic ? RepIds.TreasureEpic : rarity == Rarity.Rare ? RepIds.TreasureRare : RepIds.TreasureUncommon;
        if (owner != NetworkManager.LocalClientId)
            using (Ledger.ToBusiness(id)) Ledger.Rep(repId, Progression.TreasureRep(rarity), SortBin.DistrictOf(item), null);
        if (owner == NetworkManager.LocalClientId)
        {
            GameSession.AddToInventory(item);
            ProgressionEvents.RaiseTreasureKept(item, byMagnus);
            return;
        }
        var fixedId = new FixedString64Bytes(); fixedId.CopyFromTruncated(id);
        RobotKeepRpc(fixedId, json, byMagnus, RpcTarget.Single(owner, RpcTargetUse.Temp));
    }

    static bool ForLocalBusiness(FixedString64Bytes id) => BusinessSlots.Declared && Instance != null &&
        Instance.NetworkManager != null && id.ToString() == GameSession.BusinessId &&
        Businesses.Local.ownerClientId == Instance.NetworkManager.LocalClientId;

    [Rpc(SendTo.SpecifiedInParams)]
    void RobotSortRpc(FixedString64Bytes businessId, string json, ItemCategory bin, bool correct, bool byMagnus, RpcParams p = default)
    {
        if (!ForLocalBusiness(businessId) || string.IsNullOrEmpty(json) || json.Length > 4000) return;
        ItemInstance item;
        try { item = JsonUtility.FromJson<ItemInstance>(json); } catch (Exception) { return; }
        if (!EbaeItemValidation.IsValid(item, out _)) return;
        if (correct) GameSession.RecordSorted(item, ItemCategoryInfo.IsRecyclable(bin));
        Progression.ApplyHostRobotSort(new ItemSortedFact { item = item, bin = bin, correct = correct,
            byMagnus = byMagnus, districtId = SortBin.DistrictOf(item) });
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RobotKeepRpc(FixedString64Bytes businessId, string json, bool byMagnus, RpcParams p = default)
    {
        if (!ForLocalBusiness(businessId) || string.IsNullOrEmpty(json) || json.Length > 4000) return;
        ItemInstance item;
        try { item = JsonUtility.FromJson<ItemInstance>(json); } catch (Exception) { return; }
        if (!EbaeItemValidation.IsValid(item, out _)) return;
        // Also protect against a repeated RPC after a reconnect, without rejecting a distinct item of the same type.
        foreach (var owned in GameSession.Inventory) if (owned != null && owned.uid == item.uid) return;
        GameSession.AddToInventory(item);
        Progression.ApplyHostRobotTreasure(item, byMagnus);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RobotItemRpc(FixedString64Bytes businessId, string json, bool recycled, RpcParams p = default)
    {
        if (!ForLocalBusiness(businessId) || string.IsNullOrEmpty(json) || json.Length > 4000) return;
        ItemInstance item;
        try { item = JsonUtility.FromJson<ItemInstance>(json); } catch (Exception) { return; }
        if (EbaeItemValidation.IsValid(item, out _)) GameSession.RecordSorted(item, recycled);
    }

}

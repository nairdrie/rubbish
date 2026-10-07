using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Host-owned, per-lot bag admission. The table cannot infer another peer's rip from its local loose-item list:
/// guest-spawned pieces take time to publish. A reservation holds every as-yet-unseen item slot until the host
/// observes that item on the table (or resting elsewhere). No peer consumes a bag before its admission is granted.
/// </summary>
[NetSessionComponent(Order = 26)]
public sealed class SortingTableCapacityNet : NetworkBehaviour
{
    public static SortingTableCapacityNet Instance { get; private set; }
    const float PublicationWait = 3f;
    const float ReservationLifetime = 30f;
    const string Full = "Table's full! Sort some junk first";

    sealed class Reservation
    {
        public int lot, serial;
        public ulong owner;
        public uint netId;
        public float until;
        public readonly Dictionary<string, float> pending = new Dictionary<string, float>(StringComparer.Ordinal);
    }

    readonly Dictionary<string, Reservation> reservations = new Dictionary<string, Reservation>(StringComparer.Ordinal);
    readonly Dictionary<uint, Action<bool, string>> replies = new Dictionary<uint, Action<bool, string>>();
    readonly List<string> stale = new List<string>();
    uint nextRequest = 1;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer) NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        if (Instance == this) Instance = null;
        var callbacks = new List<Action<bool, string>>(replies.Values);
        replies.Clear();
        foreach (var reply in callbacks) reply?.Invoke(false, "Sorting table disconnected");
        reservations.Clear();
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void OnClientDisconnected(ulong client)
    {
        stale.Clear();
        foreach (var e in reservations)
            if (e.Value.owner == client) stale.Add(e.Key);
        foreach (var key in stale) reservations.Remove(key);
    }

    static SortingTable TableFor(int lot)
    {
        var depot = DepotRowScene.Lot(lot);
        return depot != null && depot.interior != null
            ? depot.interior.GetComponentInChildren<SortingTable>(true) : null;
    }

    static bool Claim(int lot, out DepotLotState state)
    {
        state = default;
        var row = DepotRowNet.Instance;
        return row != null && row.OneWorld && row.TryGetLot(lot, out state) && state.Claimed && state.Online;
    }

    static int Count(TrashBag bag)
    {
        int n = 0;
        var items = bag != null && bag.Contents != null ? bag.Contents.items : null;
        if (items != null) foreach (var item in items) if (item != null) n++;
        return n;
    }

    static string Key(TrashBag bag)
    {
        string id = bag != null && bag.Contents != null ? bag.Contents.bagId : null;
        return !string.IsNullOrEmpty(id) ? "bag/" + id : bag != null && bag.NetId != 0
            ? "net/" + bag.NetId : "local/" + bag.GetInstanceID();
    }

    /// <summary>Host's outstanding item slots in a lot; a published on-table item is counted by ItemsOnTable instead.</summary>
    public static int PendingForLot(int lot)
    {
        var n = Instance;
        return n != null && n.IsSpawned && n.IsServer ? n.HostPending(lot) : 0;
    }

    /// <summary>Host-side mass not yet represented by published loose carryables after a bag is consumed.</summary>
    public static float PendingMassForLot(int lot)
    {
        var n = Instance;
        if (n == null || !n.IsSpawned || !n.IsServer) return 0f;
        n.Refresh();
        var physical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in LooseItem.All)
            if (item != null && item.Item != null && !string.IsNullOrEmpty(item.Item.uid) &&
                item.State != CarryState.Consumed) physical.Add(item.Item.uid);
        float mass = 0f;
        foreach (var r in n.reservations.Values)
            if (r.lot == lot) foreach (var item in r.pending)
                if (!physical.Contains(item.Key)) mass += item.Value;
        return mass;
    }

    int HostPending(int lot)
    {
        Refresh();
        var table = TableFor(lot);
        var onTop = new HashSet<string>(StringComparer.Ordinal);
        if (table != null)
            foreach (var item in LooseItem.All)
                if (item != null && item.Item != null && !string.IsNullOrEmpty(item.Item.uid) &&
                    SortingKit.IsLoose(item) && table.IsOnTop(item.transform.position)) onTop.Add(item.Item.uid);
        int count = 0;
        foreach (var r in reservations.Values)
            if (r.lot == lot) foreach (var item in r.pending)
                if (!onTop.Contains(item.Key)) count++;
        return count;
    }

    void Refresh()
    {
        if (!IsServer) return;
        stale.Clear();
        float now = Time.unscaledTime;
        foreach (var entry in reservations)
        {
            var r = entry.Value;
            if (now > r.until || !Claim(r.lot, out var state) || state.plopSerial != r.serial)
            {
                stale.Add(entry.Key);
                continue;
            }
            if (r.pending.Count == 0) continue;
            var table = TableFor(r.lot);
            if (table == null) continue;
            foreach (var item in LooseItem.All)
            {
                if (item == null || item.Item == null || string.IsNullOrEmpty(item.Item.uid) ||
                    !r.pending.ContainsKey(item.Item.uid)) continue;
                // Keep its slot throughout flight, even if it briefly passes through the spatial tabletop count.
                // Once settled/carried/consumed, the world inventory count (or its absence) takes over.
                if (item.State == CarryState.Resting || item.State == CarryState.Carried ||
                    item.State == CarryState.Consumed)
                    r.pending.Remove(item.Item.uid);
            }
        }
        foreach (var key in stale) reservations.Remove(key);
    }

    /// <summary>Ask the host for all of this bag's item slots. Callback is immediate in solo/host, async on guests.</summary>
    public static void Request(SortingTable table, TrashBag bag, bool machine, Action<bool, string> done)
    {
        var n = Instance;
        int lot = DepotMachineContext.LotOf(table);
        if (lot < 0 || !DepotRowWorld.Active)
        {
            done?.Invoke(true, null); // solo uses SortingTable's local in-flight count
            return;
        }
        if (n == null || !n.IsSpawned || !Claim(lot, out var claim))
        {
            done?.Invoke(false, "Sorting table unavailable");
            return;
        }
        if (n.IsServer)
        {
            bool ok = n.HostReserve(lot, claim.plopSerial, bag, n.NetworkManager.LocalClientId, machine, out var reason);
            done?.Invoke(ok, reason);
        }
        else n.StartCoroutine(n.GuestRequest(table, bag, lot, claim.plopSerial, done));
    }

    IEnumerator GuestRequest(SortingTable table, TrashBag bag, int lot, int serial, Action<bool, string> done)
    {
        float until = Time.unscaledTime + PublicationWait;
        while (bag != null && bag.NetId == 0 && Time.unscaledTime < until) yield return null;
        if (bag == null || bag.State == CarryState.Consumed || table == null || bag.NetId == 0)
        {
            done?.Invoke(false, "Bag isn't ready yet");
            yield break;
        }
        uint request = nextRequest++;
        replies[request] = done;
        ReserveRpc(request, bag.NetId, (byte)lot, serial);
        until = Time.unscaledTime + PublicationWait;
        while (replies.ContainsKey(request) && Time.unscaledTime < until) yield return null;
        if (replies.TryGetValue(request, out var callback))
        {
            replies.Remove(request);
            callback?.Invoke(false, "Host didn't answer; try again");
        }
    }

    bool HostReserve(int lot, int serial, TrashBag bag, ulong sender, bool machine, out string reason)
    {
        reason = null;
        if (bag == null || bag.State == CarryState.Consumed || bag.HasBurst || bag.Contents == null ||
            !Claim(lot, out var state) || state.plopSerial != serial ||
            !(machine && sender == NetworkManager.LocalClientId) && !Permissions.CanWorkIn(sender, lot))
        { reason = "Can't sort this bag here"; return false; }
        var table = TableFor(lot);
        if (table == null || !table.isActiveAndEnabled ||
            DepotMachineContext.LotOf(table) != lot) { reason = "Sorting table unavailable"; return false; }
        if (Compactor.ShopBusy(table)) { reason = "Compactor running — wait for the suction to finish"; return false; }
        if (DepotRowLayout.InteriorAt(bag.WorldCenter) != lot)
        {
            reason = "Bring the bag inside first";
            return false;
        }
        if (sender != NetworkManager.LocalClientId)
        {
            var player = PlayerBody.For(sender);
            if (player == null || Vector3.Distance(player.transform.position, table.transform.position) > 12f)
            { reason = "Too far from the table"; return false; }
        }
        Refresh();
        string key = Key(bag);
        if (reservations.ContainsKey(key)) { reason = "Bag is already being ripped"; return false; }
        int contents = Count(bag);
        if (!SortingTableCapacityRules.Fits(table.MaxItems, table.ItemsOnTable(), HostPending(lot), contents))
        { reason = Full; return false; }
        var r = new Reservation { lot = lot, serial = serial, owner = sender, netId = bag.NetId,
            until = Time.unscaledTime + ReservationLifetime };
        if (bag.Contents.items != null)
            for (int i = 0; i < bag.Contents.items.Count; i++)
            {
                var item = bag.Contents.items[i];
                if (item == null) continue;
                string uid = !string.IsNullOrEmpty(item.uid) && !r.pending.ContainsKey(item.uid)
                    ? item.uid : key + "/untracked/" + i;
                r.pending[uid] = Mathf.Max(0f, item.massKg);
            }
        reservations.Add(key, r);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReserveRpc(uint request, uint bagNetId, byte lot, int serial, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        bool ok = false;
        string reason = "Bag isn't available";
        if (SessionService.GuardFor("rb.table.reserve").Allow(sender, 24, Time.realtimeSinceStartupAsDouble) &&
            lot < DepotRowLayout.LotCount && HandsNet.Instance != null)
        {
            var nc = HandsNet.Instance.Get(bagNetId);
            bool ownsSimulation = nc != null &&
                ((nc.St.Phase == HandsPhase.Held || nc.St.Phase == HandsPhase.Flight) && nc.St.holder == sender ||
                 nc.St.Phase == HandsPhase.Rest && nc.Origin == sender);
            if (ownsSimulation && nc.Item is TrashBag bag)
                ok = HostReserve(lot, serial, bag, sender, false, out reason);
            else reason = "Bag is being handled by someone else";
        }
        GrantRpc(request, bagNetId, lot, ok, reason ?? "", RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void GrantRpc(uint request, uint bagNetId, byte lot, bool accepted, string reason, RpcParams p = default)
    {
        if (IsServer || p.Receive.SenderClientId != NetworkManager.ServerClientId) return;
        if (replies.TryGetValue(request, out var callback))
        {
            replies.Remove(request);
            callback?.Invoke(accepted, reason);
        }
        else if (accepted) CancelRpc(lot, bagNetId); // our wait expired before this host grant arrived
    }

    /// <summary>Return an unused grant (e.g. player left the station before the host answered).</summary>
    public static void Cancel(SortingTable table, TrashBag bag)
    {
        var n = Instance;
        int lot = DepotMachineContext.LotOf(table);
        if (n == null || !n.IsSpawned || lot < 0 || bag == null) return;
        if (n.IsServer)
        {
            string key = Key(bag);
            if (n.reservations.TryGetValue(key, out var r) && r.lot == lot &&
                r.owner == n.NetworkManager.LocalClientId) n.reservations.Remove(key);
        }
        else if (bag.NetId != 0) n.CancelRpc((byte)lot, bag.NetId);
    }

    void HostCancel(int lot, uint netId, ulong sender)
    {
        if (netId == 0) return;
        stale.Clear();
        foreach (var e in reservations)
            if (e.Value.lot == lot && e.Value.netId == netId && e.Value.owner == sender) stale.Add(e.Key);
        foreach (var key in stale) reservations.Remove(key);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CancelRpc(byte lot, uint netId, RpcParams p = default) => HostCancel(lot, netId, p.Receive.SenderClientId);
}

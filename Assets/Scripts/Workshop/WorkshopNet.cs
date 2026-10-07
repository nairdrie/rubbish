using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Bounded, infrequently changed business snapshot. Kept on the existing registry: no new prefab component.</summary>
public struct WorkshopNetState : INetworkSerializable, IEquatable<WorkshopNetState>
{
    public FixedString64Bytes businessId;
    public ulong ownerClientId;
    public uint revision;
    public FixedString4096Bytes json;
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    { serializer.SerializeValue(ref businessId); serializer.SerializeValue(ref ownerClientId); serializer.SerializeValue(ref revision); serializer.SerializeValue(ref json); }
    public bool Equals(WorkshopNetState other) => businessId.Equals(other.businessId) && ownerClientId == other.ownerClientId && revision == other.revision && json.Equals(other.json);
    public override bool Equals(object other) => other is WorkshopNetState state && Equals(state);
    public override int GetHashCode() => businessId.GetHashCode() ^ (int)revision;
}

public partial class BusinessRegistry
{
    NetworkList<WorkshopNetState> workshopStates;
    readonly Dictionary<string, WorkshopState> workshopCache = new Dictionary<string, WorkshopState>(StringComparer.Ordinal);
    float nextWorkshopTick, nextWorkshopUpload;
    string workshopMirroredJson;
    bool workshopHooked;

    void AwakeWorkshop() => workshopStates = new NetworkList<WorkshopNetState>();
    void SpawnWorkshop()
    {
        if (IsServer) {
            SessionService.SetGuard("rb.workshop.upload", new NetGuard(1f, 3, 4300));
            SessionService.SetGuard("rb.workshop.command", new NetGuard(4f, 8, 1024));
            SessionService.SetGuard("rb.workshop.donate", new NetGuard(8f, 12, 48));
            SessionService.SetGuard("rb.workshop.gadget", new NetGuard(6f, 10, 96));
            SessionService.SetGuard("rb.workshop.gadget.sync", new NetGuard(16f, 32, 96));
        }
        workshopStates.OnListChanged += WorkshopListChanged;
        Closing += CloseWorkshop; workshopHooked = true;
        if (IsServer && Businesses.TryGetForClient(NetworkManager.ServerClientId, out var business)) PublishWorkshop(business, GameSession.WorkshopSave);
        RefreshWorkshopCache();
    }
    void UnhookWorkshop()
    {
        if (workshopHooked) { workshopStates.OnListChanged -= WorkshopListChanged; Closing -= CloseWorkshop; workshopHooked = false; }
        workshopCache.Clear(); workshopMirroredJson = null; nextWorkshopTick = nextWorkshopUpload = 0f;
    }
    int WorkshopIndex(string id)
    { for (int i = 0; i < workshopStates.Count; i++) if (workshopStates[i].businessId.ToString() == id) return i; return -1; }
    public WorkshopState WorkshopSnapshot(string businessId) => !string.IsNullOrEmpty(businessId) && workshopCache.TryGetValue(businessId, out var state) ? state.Clone() : null;
    void WorkshopListChanged(NetworkListEvent<WorkshopNetState> change) => RefreshWorkshopCache();
    void RefreshWorkshopCache()
    {
        workshopCache.Clear();
        for (int i = 0; i < workshopStates.Count; i++) {
            var entry = workshopStates[i]; var state = ParseWorkshop(entry.json.ToString());
            if (state != null) workshopCache[entry.businessId.ToString()] = state;
        }
        MirrorWorkshop(); Workshop.RaiseChanged();
    }
    void MirrorWorkshop()
    {
        // Initial list replication may precede the picker accepting this profile. Never overwrite an unselected save.
        if (!IsServer && (!BusinessSlots.Declared || acceptedBusinessId != GameSession.BusinessId)) return;
        int index = WorkshopIndex(GameSession.BusinessId);
        if (index < 0 || workshopStates[index].ownerClientId != NetworkManager.LocalClientId) return;
        string json = workshopStates[index].json.ToString();
        if (json == workshopMirroredJson || !workshopCache.TryGetValue(GameSession.BusinessId, out var state)) return;
        workshopMirroredJson = json; GameSession.SetWorkshopSave(state);
    }
    void TickWorkshop()
    {
        if (!IsSpawned || Time.unscaledTime < nextWorkshopTick) return;
        nextWorkshopTick = Time.unscaledTime + 0.4f;
        if (!Businesses.TryGetForClient(NetworkManager.LocalClientId, out var business) || business.Id != GameSession.BusinessId) return;
        if (!IsServer && (!BusinessSlots.Declared || acceptedBusinessId != business.Id)) return;
        int index = WorkshopIndex(business.Id);
        if (index < 0) {
            if (IsServer) PublishWorkshop(business, GameSession.WorkshopSave);
            else if (Time.unscaledTime >= nextWorkshopUpload) {
                nextWorkshopUpload = Time.unscaledTime + 2f;
                var state = GameSession.WorkshopSave; PruneLocalDisplays(state);
                WorkshopUploadRpc(business.id, WorkshopJson(state));
            }
            return;
        }
        MirrorWorkshop();
        // Owner inventory is the existing save authority. A pin is only a non-transferable visual reference.
        var snapshot = WorkshopSnapshot(business.Id);
        if (snapshot == null) return;
        foreach (var pin in snapshot.displays) {
            var item = Workshop.OwnedItem(pin.uid);
            if (item != null && item.defId == pin.defId) continue;
            if (IsServer) { snapshot.Pin(pin.recipeId, null); PublishWorkshop(business, snapshot); break; }
            WorkshopCommandRpc(business.lot, 3, WorkshopKey(pin.recipeId), 0, default); // clearing a stale display is allowed away from the shop
        }
    }
    static void PruneLocalDisplays(WorkshopState state) => state.displays.RemoveAll(pin => {
        var item = Workshop.OwnedItem(pin.uid); return item == null || item.defId != pin.defId;
    });
    void CloseWorkshop(string businessId, ulong owner)
    {
        if (!IsServer) return;
        int index = WorkshopIndex(businessId); if (index >= 0) workshopStates.RemoveAt(index);
    }
    static WorkshopState ParseWorkshop(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 4000) return null;
        try { var state = JsonUtility.FromJson<WorkshopState>(json); return state != null && state.Valid() ? state : null; }
        catch (Exception) { return null; }
    }
    static FixedString4096Bytes WorkshopJson(WorkshopState state) => new FixedString4096Bytes(JsonUtility.ToJson(state));
    static FixedString64Bytes WorkshopKey(string value) { var key = new FixedString64Bytes(); key.CopyFromTruncated(value ?? ""); return key; }
    void PublishWorkshop(BusinessInfo business, WorkshopState state)
    {
        if (!IsServer || state == null || !state.Valid()) return;
        int index = WorkshopIndex(business.Id);
        var next = new WorkshopNetState { businessId = business.id, ownerClientId = business.ownerClientId,
            revision = index >= 0 ? workshopStates[index].revision + 1 : 1, json = WorkshopJson(state) };
        if (index < 0) workshopStates.Add(next); else workshopStates[index] = next;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopUploadRpc(FixedString64Bytes id, FixedString4096Bytes json, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed("rb.workshop.upload", sender, json.Length + 80) || !Businesses.TryGetForClient(sender, out var business)) return;
        // Exactly once per accepted ownership. Retried/replayed uploads NEVER reset host-owned materials or projects.
        if (!MayInitializeWorkshop(business, sender, id.ToString(), WorkshopIndex(business.Id) >= 0)) return;
        var state = ParseWorkshop(json.ToString());
        if (state == null) { WorkshopReply(sender, false, "Workshop save was invalid; building is unavailable until it is repaired."); return; }
        PublishWorkshop(business, state);
    }
    static bool MayInitializeWorkshop(BusinessInfo business, ulong sender, string claimedBusiness, bool alreadyInitialized) =>
        business.IsValid && !business.IsHostBusiness && business.ownerClientId == sender && business.Id == claimedBusiness && !alreadyInitialized;

    public void WorkshopCommand(int lot, byte action, string recipe, int color, WorkshopDisplayData display)
    {
        var json = display == null ? default : new FixedString512Bytes(JsonUtility.ToJson(display));
        if (IsServer) HostWorkshopCommand(NetworkManager.LocalClientId, lot, action, recipe, color, display);
        else WorkshopCommandRpc(lot, action, WorkshopKey(recipe), color, json);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopCommandRpc(int lot, byte action, FixedString64Bytes recipe, int color, FixedString512Bytes preview, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed("rb.workshop.command", sender, preview.Length + 90)) return;
        WorkshopDisplayData display = null;
        if (preview.Length > 0) {
            try { display = JsonUtility.FromJson<WorkshopDisplayData>(preview.ToString()); }
            catch (Exception) { return; }
            if (display == null || !display.Valid()) return;
        }
        HostWorkshopCommand(sender, lot, action, recipe.ToString(), color, display);
    }
    void HostWorkshopCommand(ulong client, int lot, byte action, string recipe, int color, WorkshopDisplayData display)
    {
        if (!IsServer || !Workshop.TryBusinessAt(lot, out var business) || business.ownerClientId != client) {
            WorkshopReply(client, false, "Only the owner can manage this workshop."); return;
        }
        if (!(action == 3 && display == null) && !Workshop.InsideShop(client, lot)) {
            WorkshopReply(client, false, "Visit your workshop to build or customise."); return;
        }
        if (display != null && client == NetworkManager.LocalClientId) {
            var actual = Workshop.OwnedItem(display.uid);
            if (actual == null || actual.defId != display.defId) { WorkshopReply(client, false, "That find is no longer in your inventory."); return; }
            display = WorkshopDisplayData.From(recipe, actual);
        }
        var state = WorkshopSnapshot(business.Id);
        if (state == null) { WorkshopReply(client, false, "Workshop is still syncing. Try again in a moment."); return; }
        if (!Workshop.ApplyCommand(state, action, recipe, color, display, out string why)) { WorkshopReply(client, false, why); return; }
        PublishWorkshop(business, state); WorkshopReply(client, true, why);
    }

    public void WorkshopDonate(int lot, LooseItem item)
    {
        var hands = HandsNet.Instance;
        if (hands != null && hands.Active) {
            if (IsServer && item.NetId == 0) hands.HostPublishNow(item);
            var netItem = hands.Get(item.NetId);
            if (netItem == null || netItem.PendingGrab || !netItem.HeldBy(NetworkManager.LocalClientId)) { Workshop.Notice(false, "Wait until the item is firmly in your hands."); return; }
            if (IsServer) HostWorkshopDonate(NetworkManager.LocalClientId, lot, netItem.Id, netItem.St.seq);
            else WorkshopDonateRpc(lot, netItem.Id, netItem.St.seq);
        }
        else if (IsServer) HostWorkshopDonate(NetworkManager.LocalClientId, lot, 0, 0);
        else Workshop.Notice(false, "Workshop is still syncing. Try again in a moment.");
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopDonateRpc(int lot, uint item, uint sequence, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed("rb.workshop.donate", sender, 32)) return;
        HostWorkshopDonate(sender, lot, item, sequence);
    }
    void HostWorkshopDonate(ulong client, int lot, uint itemId, uint sequence)
    {
        if (!IsServer || !Workshop.TryBusinessAt(lot, out var business) || !Workshop.MayDonate(client, business) || !Workshop.InsideShop(client, lot)) {
            WorkshopReply(client, false, "Donate inside your own shop or your employer's shop."); return;
        }
        var state = WorkshopSnapshot(business.Id);
        if (state == null) { WorkshopReply(client, false, "Workshop is still syncing. Try again in a moment."); return; }
        var hands = HandsNet.Instance;
        LooseItem item;
        if (hands != null && hands.Active) {
            if (!hands.TryWorkshopHeld(client, itemId, sequence, out item)) { WorkshopReply(client, false, "That item is no longer safely in your hands."); return; }
        } else {
            if (client != NetworkManager.LocalClientId || !(TrashCarrier.Local?.Held is LooseItem local)) return;
            item = local;
        }
        if (!item.Revealed || item.State == CarryState.Consumed || !WorkshopCatalog.TryGrams(item.Item, out var material, out int grams)) {
            WorkshopReply(client, false, "Only revealed metal, plastic, glass and paper become building materials."); return;
        }
        ushort completedBefore = state.completed;
        if (!state.Donate(material, grams, out string why)) { WorkshopReply(client, false, why); return; }
        // Main-thread, non-yielding transaction. Consume publishes Gone and frees held ownership before publishing credit.
        item.Consume(); PublishWorkshop(business, state);
        WorkshopReply(client, true, state.completed != completedBefore ? "Project complete! Installed in the workshop." :
            $"Saved {grams / 1000f:0.###} kg {material.ToString().ToLowerInvariant()} for building.");
    }
    void WorkshopReply(ulong client, bool ok, string message)
    {
        if (!IsSpawned) return;
        var text = new FixedString512Bytes(); text.CopyFromTruncated(message);
        WorkshopNoticeRpc(ok, text, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopNoticeRpc(bool ok, FixedString512Bytes text, RpcParams rpc = default) => Workshop.Notice(ok, text.ToString());
}

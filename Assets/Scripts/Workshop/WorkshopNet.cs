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
    /// <summary>Host: every item uid a build has consumed this session, per business. An owner removes those items when
    /// the build is confirmed; this stops a replayed or doctored request paying with the same item twice.</summary>
    readonly HashSet<string> workshopSpentUids = new HashSet<string>(StringComparer.Ordinal);
    float nextWorkshopTick, nextWorkshopUpload, nextDecorAsk;
    string workshopMirroredJson;
    bool workshopHooked;

    void AwakeWorkshop() => workshopStates = new NetworkList<WorkshopNetState>();
    void SpawnWorkshop()
    {
        if (IsServer) {
            SessionService.SetGuard("rb.workshop.upload", new NetGuard(1f, 3, 4300));
            SessionService.SetGuard("rb.workshop.command", new NetGuard(4f, 8, 1024));
            // 3 a second: a Monument load is a chain of 100-piece requests, 0.4 s apart (Workshop.FeedSpacing)
            SessionService.SetGuard("rb.workshop.build", new NetGuard(3f, 4, Workshop.MaxOfferChars * 3 + 256));
            SessionService.SetGuard("rb.workshop.gadget", new NetGuard(6f, 10, 96));
            SessionService.SetGuard("rb.workshop.gadget.sync", new NetGuard(16f, 32, 96));
            SessionService.SetGuard("rb.workshop.event", new NetGuard(4f, 12, 32));
        }
        workshopStates.OnListChanged += WorkshopListChanged;
        Closing += CloseWorkshop; workshopHooked = true;
        if (IsServer && Businesses.TryGetForClient(NetworkManager.ServerClientId, out var business)) PublishWorkshop(business, GameSession.WorkshopSave);
        RefreshWorkshopCache();
    }
    void UnhookWorkshop()
    {
        if (workshopHooked) { workshopStates.OnListChanged -= WorkshopListChanged; Closing -= CloseWorkshop; workshopHooked = false; }
        workshopCache.Clear(); workshopSpentUids.Clear(); workshopMirroredJson = null; nextWorkshopTick = nextWorkshopUpload = nextDecorAsk = 0f;
    }
    int WorkshopIndex(string id)
    { for (int i = 0; i < workshopStates.Count; i++) if (workshopStates[i].businessId.ToString() == id) return i; return -1; }
    public WorkshopState WorkshopSnapshot(string businessId) => !string.IsNullOrEmpty(businessId) && workshopCache.TryGetValue(businessId, out var state) ? state.Clone() : null;
    /// <summary>A business's built level of one Callings node from the replicated state, without cloning it (0 if unknown).</summary>
    public int WorkshopCallingLevel(string businessId, int nodeIndex) =>
        !string.IsNullOrEmpty(businessId) && workshopCache.TryGetValue(businessId, out var state) ? state.CallingLevelAt(nodeIndex) : 0;
    /// <summary>A business is hunting for this catalog number (its wanted-part pins), from the replicated state.</summary>
    public bool WorkshopWants(string businessId, int catalogNumber) =>
        !string.IsNullOrEmpty(businessId) && workshopCache.TryGetValue(businessId, out var state) && state.IsPinned(catalogNumber);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>Debug/QA (Workshop.DebugEdit): the host replaces its OWN business's workshop state. Not an RPC.</summary>
    internal bool WorkshopDebugPublish(WorkshopState state, out string why)
    {
        why = "";
        if (!IsServer || !Businesses.TryGetForClient(NetworkManager.LocalClientId, out var business)) { why = "Host only."; return false; }
        PublishWorkshop(business, state); return true;
    }
#endif
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
            WorkshopCommandRpc(business.lot, Workshop.ActionPinDisplay, WorkshopKey(pin.recipeId), 0, default); // clearing a stale display is allowed away from the shop
        }
        // Decor is bought on Scrapazon (the owner's upgrade level) and installed here (the completed bit). If the two ever
        // disagree (a lost message, a purchase made while the workshop was still syncing), ask again, gently.
        if (Time.unscaledTime >= nextDecorAsk) {
            var missing = snapshot.DecorToInstall(GameSession.GetUpgradeLevel);
            if (missing.Count > 0) { nextDecorAsk = Time.unscaledTime + 3f; WorkshopCommand(business.lot, Workshop.ActionInstallDecor, missing[0], 0, null); }
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
        // Exactly once per accepted ownership. Retried/replayed uploads NEVER reset host-owned workshop state.
        if (!MayInitializeWorkshop(business, sender, id.ToString(), WorkshopIndex(business.Id) >= 0)) return;
        var state = ParseWorkshop(json.ToString());
        if (state == null) { WorkshopReply(sender, false, "Your shop save was invalid; printing is unavailable until it is repaired."); return; }
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
            WorkshopReply(client, false, "Only the owner can manage this shop."); return;
        }
        if (!Workshop.AllowedAway(action, display) && !Workshop.InsideShop(client, lot)) {
            WorkshopReply(client, false, "You have to be in your shop for that."); return;
        }
        if (display != null && client == NetworkManager.LocalClientId) {
            var actual = Workshop.OwnedItem(display.uid);
            if (actual == null || actual.defId != display.defId) { WorkshopReply(client, false, "That find is no longer in your inventory."); return; }
            display = WorkshopDisplayData.From(recipe, actual);
        }
        var state = WorkshopSnapshot(business.Id);
        if (state == null) { WorkshopReply(client, false, "The shop is still syncing. Try again in a moment."); return; }
        if (!Workshop.ApplyCommand(state, action, recipe, color, display, out string why)) { WorkshopReply(client, false, why); return; }
        PublishWorkshop(business, state); WorkshopReply(client, true, why);
    }

    // ------------------------------------------------------------------ prints: a blueprint level or a Monument load, paid in kept items

    /// <summary>Owner's machine: ask for a print with the instances it picked. The answer comes back as
    /// <see cref="Workshop.OnBuildReply"/>; only a yes removes the items.</summary>
    public void WorkshopBuild(int lot, byte action, string id, uint request, List<ItemInstance> offer)
    {
        if (IsServer) { HostWorkshopBuild(NetworkManager.LocalClientId, lot, action, id, request, offer); return; }
        string wire = Workshop.EncodeOffer(offer);
        if (wire == null) { Workshop.OnBuildReply(request, false, "Those parts can't be sent. Try again."); return; }
        WorkshopBuildRpc(lot, action, WorkshopKey(id), request, wire);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopBuildRpc(int lot, byte action, FixedString64Bytes id, uint request, string offer, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (!Allowed("rb.workshop.build", sender, (offer != null ? offer.Length * 3 : 0) + 96)) return;
        if (offer != null && offer.Length > Workshop.MaxOfferChars || !Workshop.TryDecodeOffer(offer, out var items)) { BuildReply(sender, request, false, "Those parts didn't arrive in one piece."); return; }
        HostWorkshopBuild(sender, lot, action, id.ToString(), request, items);
    }
    void HostWorkshopBuild(ulong client, int lot, byte action, string id, uint request, List<ItemInstance> offer)
    {
        if (!IsServer || !Workshop.TryBusinessAt(lot, out var business) || business.ownerClientId != client) {
            BuildReply(client, request, false, "Only the owner can print here. Your own blueprints are printed on your own laptop."); return;
        }
        if (!Workshop.InsideShop(client, lot)) { BuildReply(client, request, false, "Use the laptop in your own shop to print."); return; }
        var state = WorkshopSnapshot(business.Id);
        if (state == null) { BuildReply(client, request, false, "The print shop is still syncing. Try again in a moment."); return; }
        offer ??= new List<ItemInstance>();
        foreach (var item in offer)
            if (item == null || string.IsNullOrEmpty(item.uid) || workshopSpentUids.Contains(business.Id + "/" + item.uid)) {
                BuildReply(client, request, false, "One of those parts was already used. Check your inventory."); return;
            }
        if (client == NetworkManager.LocalClientId) { // the host's own inventory is right here: check it for real
            var stock = Workshop.StockFor(state);
            foreach (var item in offer) {
                bool mine = false;
                if (stock.free.TryGetValue(item.defId ?? "", out var free)) foreach (var owned in free) if (owned.uid == item.uid) { mine = true; break; }
                if (!mine) { BuildReply(client, request, false, "One of those parts is no longer free in your inventory."); return; }
            }
        }
        if (!Workshop.ApplyBuild(state, action, id, offer, out string why)) { BuildReply(client, request, false, why); return; }
        foreach (var item in offer) workshopSpentUids.Add(business.Id + "/" + item.uid);
        PublishWorkshop(business, state); BuildReply(client, request, true, why);
    }
    void BuildReply(ulong client, uint request, bool ok, string message)
    {
        if (!IsSpawned) return;
        if (client == NetworkManager.LocalClientId) { Workshop.OnBuildReply(request, ok, message); return; }
        var text = new FixedString512Bytes(); text.CopyFromTruncated(message ?? "");
        WorkshopBuildReplyRpc(request, ok, text, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopBuildReplyRpc(uint request, bool ok, FixedString512Bytes text, RpcParams rpc = default) => Workshop.OnBuildReply(request, ok, text.ToString());

    // ------------------------------------------------------------------ shop events: the SolidWaste 3D disk

    /// <summary>The machine that ripped a bag / kept the disk in the shop on <paramref name="lot"/> reports it.</summary>
    public void WorkshopShopEvent(int lot, byte kind)
    {
        if (IsServer) HostShopEvent(NetworkManager.LocalClientId, lot, kind, true);
        else WorkshopShopEventRpc(lot, kind);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopShopEventRpc(int lot, byte kind, RpcParams rpc = default)
    {
        ulong sender = rpc.Receive.SenderClientId;
        if (Allowed("rb.workshop.event", sender, 16)) HostShopEvent(sender, lot, kind);
    }
    /// <summary>A guest's report counts when they may work in the shop (owner, hired crew) and are standing in it. The
    /// host's own machine (<paramref name="local"/>) is believed as it is: it also reports for the robots it simulates
    /// (Magnus, the chute, the compactor's grate) in any shop, wherever its player happens to be. The disk is the lot owner's.</summary>
    void HostShopEvent(ulong client, int lot, byte kind, bool local = false)
    {
        if (!IsServer || !Workshop.TryBusinessAt(lot, out var business)) return;
        if (!local && (!Permissions.CanWorkIn(client, lot) || !Workshop.InsideShop(client, lot))) return;
        var state = WorkshopSnapshot(business.Id);
        if (state == null) return;
        if (!Workshop.ApplyShopEvent(state, kind, Workshop.DiskRoll(business.Id, state), Workshop.DiskIsOut(lot), out bool drop)) return;
        PublishWorkshop(business, state);
        if (kind == Workshop.EventDiskKept && client != business.ownerClientId)
            WorkshopReply(client, true, $"Disk kept. It went into {business.Name}'s laptop.");
        if (!drop) return;
        Workshop.NoteDiskIssued(lot);
        if (client == NetworkManager.LocalClientId) Workshop.OnDiskDrop(lot);
        else WorkshopDiskDropRpc(lot, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopDiskDropRpc(int lot, RpcParams rpc = default) => Workshop.OnDiskDrop(lot);

    void WorkshopReply(ulong client, bool ok, string message)
    {
        if (!IsSpawned || string.IsNullOrEmpty(message)) return;
        var text = new FixedString512Bytes(); text.CopyFromTruncated(message);
        WorkshopNoticeRpc(ok, text, RpcTarget.Single(client, RpcTargetUse.Temp));
    }
    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopNoticeRpc(bool ok, FixedString512Bytes text, RpcParams rpc = default) => Workshop.Notice(ok, text.ToString());
}

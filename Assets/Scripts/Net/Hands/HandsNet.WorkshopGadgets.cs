using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public partial class HandsNet
{
    public void UseWorkshopGadget(int lot, string recipe)
    {
        if (!IsSpawned || !WorkshopGadget.IsGadget(recipe)) return;
        var key = Fixed64(recipe);
        if (IsServer) HostWorkshopGadget(LocalId, lot, key); else WorkshopGadgetRpc(lot, key);
    }

    /// <summary>An idempotent exit request; repeated movement packets can never toggle the owner back onto a sofa.</summary>
    public void LeaveWorkshopLounge(int lot)
    {
        if (!IsSpawned) return;
        if (IsServer) HostLeaveWorkshopLounge(LocalId, lot); else WorkshopStandRpc(lot);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopStandRpc(int lot, RpcParams rp = default)
    {
        ulong client = rp.Receive.SenderClientId;
        if (Allowed("rb.workshop.gadget", client, 16)) HostLeaveWorkshopLounge(client, lot);
    }

    void HostLeaveWorkshopLounge(ulong client, int lot)
    {
        var sofa = WorkshopWorld.Find(lot, "break_sofa")?.GetComponent<WorkshopGadget>();
        if (sofa != null && sofa.SeatedClient == client) WorkshopEffect(lot, "break_sofa", "stand");
    }

    public void RequestWorkshopGadgetState(int lot, string recipe)
    {
        if (IsSpawned && !IsServer && WorkshopGadget.IsGadget(recipe)) WorkshopGadgetSyncRpc(lot, Fixed64(recipe));
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopGadgetSyncRpc(int lot, FixedString64Bytes recipe, RpcParams rp = default)
    {
        ulong client = rp.Receive.SenderClientId;
        if (!Allowed("rb.workshop.gadget.sync", client, 80)) return;
        var gadget = WorkshopWorld.Find(lot, recipe.ToString())?.GetComponent<WorkshopGadget>();
        if (gadget == null || Workshop.Snapshot(lot)?.Completed(recipe.ToString()) != true) return;
        WorkshopGadgetStateRpc(lot, recipe, gadget.RadioOn, gadget.SeatedClient, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopGadgetStateRpc(int lot, FixedString64Bytes recipe, bool radioOn, ulong seatedClient, RpcParams rp = default)
    {
        WorkshopWorld.Find(lot, recipe.ToString())?.GetComponent<WorkshopGadget>()?.ApplySnapshot(radioOn, seatedClient);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WorkshopGadgetRpc(int lot, FixedString64Bytes recipe, RpcParams rp = default)
    {
        ulong client = rp.Receive.SenderClientId;
        if (Allowed("rb.workshop.gadget", client, 80)) HostWorkshopGadget(client, lot, recipe);
    }

    void HostWorkshopGadget(ulong client, int lot, FixedString64Bytes recipe)
    {
        string id = recipe.ToString();
        var model = WorkshopWorld.Find(lot, id);
        var gadget = model != null ? model.GetComponent<WorkshopGadget>() : null;
        var player = WorkshopGadget.PlayerFor(client);
        if (gadget == null || !gadget.isActiveAndEnabled || Workshop.Snapshot(lot)?.Completed(id) != true || player == null ||
            (player.transform.position - gadget.ActionPosition).sqrMagnitude > 16f) return;
        var body = PlayerBody.For(client);
        if (body != null && (body.State != BodyState.Normal || !body.enabled) || player.IsDriving || player.IsPlayingChess ||
            CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client)) return;
        var localCarrier = player.GetComponent<TrashCarrier>();
        if (localCarrier != null && localCarrier.ExternalControl) return;
        bool toy = id == "doorbell" || id == "break_sofa";
        if (!toy && !Permissions.CanWorkIn(client, lot)) { WorkshopDeniedRpc("Owner or hired crew only", RpcTarget.Single(client, RpcTargetUse.Temp)); return; }
        if (Time.unscaledTime < gadget.NextUse) return;
        gadget.NextUse = Time.unscaledTime + (id == "doorbell" ? 3f : .6f);
        if (id == "scrap_radio") WorkshopEffect(lot, id, gadget.RadioOn ? "radio_off" : "radio_on");
        else if (id == "doorbell") WorkshopEffect(lot, id, "ring");
        else if (id == "break_sofa")
        {
            if (gadget.SeatedClient == client) WorkshopEffect(lot, id, "stand");
            else if (gadget.SeatedClient == None && !IsHolding(client) && (localCarrier == null || !localCarrier.IsCarrying) &&
                (body == null || body.Body.carrying == None))
                WorkshopEffect(lot, id, "sit", client);
        }
        else if (id == "sorting_chute")
        {
            if (!gadget.Feeding) gadget.Feed(client);
        }
        else if (id == "spring_launcher")
        {
            NetCarry nc = null;
            foreach (var entry in items.Values)
                if (entry != null && entry.HeldBy(client) && entry.Item is TrashBag) { nc = entry; break; }
            var bag = nc != null ? nc.Item as TrashBag : localCarrier != null ? localCarrier.Held as TrashBag : null;
            if (!Solo && nc == null && bag != null) nc = HostPublishNow(bag);
            if (bag == null || bag.IsCurbBag || (!Solo && (nc == null || !nc.HeldBy(client) || nc.WindowOpen)))
            { WorkshopDeniedRpc("Hold a collected bag to launch it", RpcTarget.Single(client, RpcTargetUse.Temp)); return; }
            var muzzle = model.Anchor("Muzzle") ?? model.transform;
            var target = model.Anchor("Landing");
            Vector3 to = target != null ? target.position : muzzle.position + muzzle.forward * 5f;
            const float seconds = 1.1f;
            Vector3 velocity = (to - muzzle.position) / seconds - Physics.gravity * (seconds * .5f);
            if (Solo)
            {
                localCarrier.ReleaseToScript();
                WorkshopBeginPhysicalFlight(bag, muzzle.position, velocity);
            }
            else WorkshopHostFlight(nc, muzzle.position, velocity);
            WorkshopEffect(lot, id, "launch");
        }
    }

    /// <summary>A slow manual three-bag feeder. Never takes held/contested/guest-flying bags.</summary>
    public bool WorkshopFeedBag(TrashBag bag, SortingTable table)
    {
        if (!IsServer || bag == null || table == null || !table.CanAccept(bag, out _)) return false;
        if (Solo)
        {
            if (bag.Holder != null || bag.State != CarryState.Resting || bag.IsInScriptedFlight) return false;
            bag.SetInFlight(); bag.TossArcInto(table, .9f);
            return true;
        }
        var nc = bag.NetId != 0 ? Get(bag.NetId) : HostPublishNow(bag);
        if (nc == null || nc.WindowOpen || nc.St.Phase != HandsPhase.Rest || bag.IsInScriptedFlight || bag.Holder != null) return false;
        WorkshopHostFlight(nc, bag.transform.position, Vector3.zero);
        bag.SetInFlight(); bag.TossArcInto(table, .9f);
        return true;
    }

    void WorkshopHostFlight(NetCarry nc, Vector3 from, Vector3 velocity)
    {
        var item = nc.Item;
        applying++;
        try { if (item.Holder != null) item.Holder.ReleaseToScript(); }
        finally { applying--; }
        var s = nc.St; s.phase = (byte)HandsPhase.Flight; s.holder = NetworkManager.ServerClientId;
        s.other = None; s.flags = 0; s.pos = from; s.vel = velocity; s.rot = item.transform.rotation;
        HostSet(nc, s);
        applying++;
        try { WorkshopBeginPhysicalFlight(item, from, velocity); }
        finally { applying--; }
    }

    internal static void WorkshopBeginPhysicalFlight(Carryable item, Vector3 from, Vector3 velocity)
    {
        item.transform.position = from;
        item.NetRemote = false;
        // EndCarry resets thrownSince/settleTimer/LastReleaseTime as well as physics. Merely setting Thrown let
        // old held bags hit Carryable's 8-second timeout on their first physics tick and silently miss the hoop.
        // The holder was already released, so this is a machine launch, not a solo heavy-bag hand throw.
        item.EndCarry(velocity, true);
    }

    public void WorkshopEffect(int lot, string recipe, string effect, ulong client = None)
    {
        if (IsServer) WorkshopEffectRpc(lot, new FixedString64Bytes(recipe), new FixedString32Bytes(effect), client);
    }

    [Rpc(SendTo.Everyone)]
    void WorkshopEffectRpc(int lot, FixedString64Bytes recipe, FixedString32Bytes effect, ulong client)
    {
        WorkshopWorld.Find(lot, recipe.ToString())?.GetComponent<WorkshopGadget>()?.Present(effect.ToString(), client);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void WorkshopDeniedRpc(FixedString128Bytes reason, RpcParams rp = default) => GameHUD.Toast(reason.ToString(), ToastStyle.Info, 2f);
}

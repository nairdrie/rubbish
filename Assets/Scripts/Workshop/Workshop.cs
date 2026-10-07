using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>Workshop UI/world API. The host owns live outcomes; GameSession holds only this machine's business mirror.</summary>
public static class Workshop
{
    public static event Action Changed;
    public static WorkshopState Snapshot(int lot)
    {
        if (!TryBusinessAt(lot, out var business)) return null;
        var registry = Businesses.Registry;
        return registry != null ? registry.WorkshopSnapshot(business.Id) : business.Id == GameSession.BusinessId ? GameSession.WorkshopSave : null;
    }
    public static bool TryBusinessAt(int lot, out BusinessInfo business)
    {
        foreach (var b in Businesses.All)
            if (b.IsValid && b.lot == lot && lot >= 0 && lot < DepotRowLayout.LotCount) { business = b; return true; }
        if (!DepotRowWorld.Active && (lot == -1 || lot == 0)) { business = Businesses.Local; return business.IsValid; }
        business = default; return false;
    }
    public static bool CanManage(int lot) => TryBusinessAt(lot, out var b) && b.ownerClientId == Permissions.LocalClientId;
    public static bool CanDonate(int lot) => TryBusinessAt(lot, out var b) && MayDonate(Permissions.LocalClientId, b);
    internal static bool MayDonate(ulong client, BusinessInfo business) => client == business.ownerClientId ||
        Payroll.TryGetJobOf(client, out var job) && job.EmployerBusinessId == business.Id && job.employerClientId == business.ownerClientId;
    internal static bool InsideShop(ulong client, int lot)
    {
        var body = PlayerBody.For(client);
        var player = body != null ? body.Player : client == Permissions.LocalClientId ? PlayerController.Local : null; // coop:allow(local) legacy solo player has no spawned PlayerBody; remote clients never fall back to this player
        if (player == null || player.IsDriving || player.IsPlayingChess || CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client)) return false;
        return DepotRowWorld.Active ? DepotRowLayout.InteriorAt(player.transform.position) == lot :
            player.gameObject.scene.name == "ShopScene"; // coop:allow(scene) legacy solo garage only, never a multiplayer permission shortcut
    }
    public static void DonateHeld(int lot)
    {
        if (!CanDonate(lot)) { Notice(false, "Only this business's owner and hired crew can donate."); return; }
        var carrier = TrashCarrier.Local;
        if (carrier == null || !(carrier.Held is LooseItem loose) || !loose.Revealed || !WorkshopCatalog.TryGrams(loose.Item, out _, out _))
        { Notice(false, "Hold a revealed metal, plastic, glass or paper item."); return; }
        var registry = Businesses.Registry;
        if (registry != null) { registry.WorkshopDonate(lot, loose); return; }
        if (GameSession.IsConnectedGuest || !InsideShop(Permissions.LocalClientId, lot)) return;
        var state = GameSession.WorkshopSave;
        WorkshopCatalog.TryGrams(loose.Item, out var material, out int grams);
        if (!state.Donate(material, grams, out string why)) { Notice(false, why); return; }
        loose.Consume(); GameSession.SetWorkshopSave(state); RaiseChanged();
        Notice(true, $"Saved {grams / 1000f:0.###} kg {material.ToString().ToLowerInvariant()} for building.");
    }
    public static void Start(int lot, string recipeId) => Command(lot, 0, recipeId, 0, null);
    public static void Cancel(int lot) => Command(lot, 1, "", 0, null);
    public static void Customize(int lot, int colorIndex) => Command(lot, 2, "", colorIndex, null);
    public static void PinDisplay(int lot, string recipeId, string itemUid)
    {
        var item = OwnedItem(itemUid);
        if (!string.IsNullOrEmpty(itemUid) && item == null) { Notice(false, "That find is no longer in your inventory."); return; }
        Command(lot, 3, recipeId, 0, WorkshopDisplayData.From(recipeId, item));
    }
    static void Command(int lot, byte action, string recipe, int color, WorkshopDisplayData display)
    {
        if (!CanManage(lot)) { Notice(false, "Only the owner can choose workshop projects or displays."); return; }
        var registry = Businesses.Registry;
        if (registry != null) { registry.WorkshopCommand(lot, action, recipe, color, display); return; }
        if (GameSession.IsConnectedGuest || !InsideShop(Permissions.LocalClientId, lot)) return;
        var state = GameSession.WorkshopSave;
        if (!ApplyCommand(state, action, recipe, color, display, out string why)) { Notice(false, why); return; }
        GameSession.SetWorkshopSave(state); RaiseChanged(); Notice(true, why);
    }
    internal static bool ApplyCommand(WorkshopState state, byte action, string recipe, int color, WorkshopDisplayData display, out string why)
    {
        why = "";
        switch (action) {
            case 0:
                if (!state.Start(recipe, out why)) return false;
                why = state.Completed(recipe) ? "Built! Your project is installed." : "Project started. Saved materials are allocated automatically."; return true;
            case 1:
                if (!state.Cancel()) { why = "No active workshop project."; return false; }
                why = "Project cancelled. All allocated materials returned."; return true;
            case 2:
                if (!state.Completed("business_sign") || color < 0 || color >= WorkshopCatalog.ColorCount) { why = "Build the business sign first."; return false; }
                state.colorIndex = color; why = "Sign colour updated."; return true;
            case 3:
                if (!state.Pin(recipe, display)) { why = "Build that display first, then choose a find from your inventory."; return false; }
                why = display == null ? "Display cleared." : "Display updated. The original stays in your inventory."; return true;
            default: why = "Unknown workshop action."; return false;
        }
    }
    internal static ItemInstance OwnedItem(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        foreach (var item in GameSession.Inventory) if (item != null && item.uid == uid && item.Def?.category == ItemCategory.Valuable) return item;
        return null;
    }
    internal static void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception e) { Debug.LogException(e); } }
    internal static void Notice(bool ok, string text) => GameHUD.Toast(text, ok ? ToastStyle.Good : ToastStyle.Bad, 3f);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() => Changed = null;
}

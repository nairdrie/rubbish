using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The print shop's API (SolidWaste 3D on the laptop, decor in the garage, the Monument outside). The host owns live
/// outcomes; GameSession holds only this machine's business mirror.
/// <para><b>Parts.</b> Anything thrown in the KEEP crate is a kept item in the owner's inventory. A blueprint level takes
/// specific kept items (<see cref="CallingRecipes"/>); a Monument tier takes kept items by sorted type, loaded a shelf at
/// a time (<see cref="MonumentCatalog"/>). The owner's machine picks the instances
/// (<see cref="SelectFor(PartRecipe, Stock)"/>, <see cref="SelectForMonument"/>), the host checks the offer and raises the
/// level, and the owner removes the items when the host says yes: the same trust-the-owner model as the fence and ebag
/// (inventories are saved by their owner; the host checks plausibility and never takes the same uid twice in a session).</para>
/// <para><b>The disk.</b> Nothing is printed until the business has its SolidWaste 3D floppy (<see cref="WorkshopState.disk"/>):
/// one hides in its first sorted bags (<see cref="BagRipped"/>) and KEEPing it installs the laptop's third tab
/// (<see cref="DiskKept"/>). Anyone working in the shop may report either; the host decides.</para>
/// </summary>
public static class Workshop
{
    public static event Action Changed;
    /// <summary>The local player's OWN business just got its disk (during play, never on load): the laptop grew a tab.</summary>
    public static event Action DiskInstalled;
    /// <summary>The host answered a print request made on this machine: (action, id, ok, text).</summary>
    public static event Action<byte, string, bool, string> BuildReplied;
    /// <summary>While the laptop is showing SolidWaste 3D it takes the print shop's notices as in-window toasts
    /// (return true = shown; false or null = the HUD toast as usual).</summary>
    public static Func<bool, string, bool> NoticeSink;
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
    /// <summary>The local player owns this business or is on its payroll.</summary>
    public static bool IsOwnerOrCrew(int lot) => TryBusinessAt(lot, out var b) && (b.ownerClientId == Permissions.LocalClientId ||
        Payroll.TryGetJobOf(Permissions.LocalClientId, out var job) && job.EmployerBusinessId == b.Id && job.employerClientId == b.ownerClientId);
    /// <summary>The LOCAL player's own business has SolidWaste 3D on its laptop (the third tab exists).</summary>
    public static bool HasPrinter => GameSession.WorkshopDisk;
    /// <summary>The lot of the local player's own business (-1: the legacy solo garage).</summary>
    public static int OwnLot => DepotRowWorld.Active && Businesses.Local.IsValid ? Businesses.Local.lot : -1;
    internal static bool InsideShop(ulong client, int lot)
    {
        var body = PlayerBody.For(client);
        var player = body != null ? body.Player : client == Permissions.LocalClientId ? PlayerController.Local : null; // coop:allow(local) legacy solo player has no spawned PlayerBody; remote clients never fall back to this player
        if (player == null || player.IsDriving || player.IsPlayingChess || CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client)) return false;
        return DepotRowWorld.Active ? DepotRowLayout.InteriorAt(player.transform.position) == lot :
            player.gameObject.scene.name == "ShopScene"; // coop:allow(scene) legacy solo garage only, never a multiplayer permission shortcut
    }

    // ------------------------------------------------------------------ commands (small, owner only)

    public static void Customize(int lot, int colorIndex) => Command(lot, ActionCustomize, "", colorIndex, null);
    public static void PinDisplay(int lot, string recipeId, string itemUid)
    {
        var item = OwnedItem(itemUid);
        if (!string.IsNullOrEmpty(itemUid) && item == null) { Notice(false, "That find is no longer in your inventory."); return; }
        Command(lot, ActionPinDisplay, recipeId, 0, WorkshopDisplayData.From(recipeId, item));
    }
    /// <summary>"Hunt for this": pin (or unpin) an item def for this business. A pinned def is marked WANTED on the HUD,
    /// tagged on ebag, and Magnus puts it in the KEEP crate instead of its bin.</summary>
    public static void SetWanted(int lot, string defId, bool wanted) => Command(lot, ActionWanted, defId, wanted ? 1 : 0, null);
    /// <summary>A decor piece was bought on Scrapazon: ask the host to install it (works from anywhere).</summary>
    public static void InstallDecor(int lot, string recipeId) => Command(lot, ActionInstallDecor, recipeId, 0, null);
    // Action bytes are wire values. 0 and 1 were "start project" and "cancel project" (the gram bank) and 7 was "build
    // sculpture" (the bench): never reuse them.
    internal const byte ActionCustomize = 2, ActionPinDisplay = 3, ActionBuildCalling = 4, ActionWanted = 5, ActionInstallDecor = 6, ActionFeedMonument = 8;
    /// <summary>Actions the owner may send from anywhere (the rest need them standing in the shop). The sign stands on the
    /// street, so its colour is one of them.</summary>
    internal static bool AllowedAway(byte action, WorkshopDisplayData display) =>
        action == ActionPinDisplay && display == null || action == ActionWanted || action == ActionInstallDecor || action == ActionCustomize;

    /// <summary>
    /// The built level of a Callings node for the business <paramref name="client"/> OWNS, readable on every machine (the
    /// host included) from the replicated workshop state. Host-side rules about another player's calling use this; the local
    /// player's own effects can keep reading <c>GameSession.GetUpgradeLevel</c>. 0 while that business is still syncing.
    /// </summary>
    public static int CallingLevelForClient(ulong client, string nodeId)
    {
        var node = CallingCatalog.Get(nodeId);
        if (node == null) return 0;
        var registry = Businesses.Registry;
        if (registry == null) return client == Permissions.LocalClientId ? GameSession.GetUpgradeLevel(node.upgradeId) : 0;
        return Businesses.TryGetForClient(client, out var business) ? registry.WorkshopCallingLevel(business.Id, node.index) : 0;
    }
    static string OwnerOnly(byte action) => action == ActionBuildCalling || action == ActionFeedMonument
        ? "Only the owner can print here. Your own blueprints are printed on your own laptop." : "Only the owner can change this shop.";
    static void Command(int lot, byte action, string recipe, int color, WorkshopDisplayData display)
    {
        if (!CanManage(lot)) { Notice(false, OwnerOnly(action)); return; }
        var registry = Businesses.Registry;
        if (registry != null) { registry.WorkshopCommand(lot, action, recipe, color, display); return; }
        if (GameSession.IsConnectedGuest || !AllowedAway(action, display) && !InsideShop(Permissions.LocalClientId, lot)) return;
        var state = GameSession.WorkshopSave;
        if (!ApplyCommand(state, action, recipe, color, display, out string why)) { if (!string.IsNullOrEmpty(why)) Notice(false, why); return; }
        GameSession.SetWorkshopSave(state); RaiseChanged(); Notice(true, why);
    }
    /// <summary>The small commands. Builds are not here: they carry the offered items (<see cref="ApplyBuild"/>).</summary>
    internal static bool ApplyCommand(WorkshopState state, byte action, string recipe, int color, WorkshopDisplayData display, out string why)
    {
        why = "";
        switch (action) {
            case ActionCustomize:
                if (!state.Completed("business_sign") || color < 0 || color >= WorkshopCatalog.ColorCount) { why = "Get the business sign first."; return false; }
                state.colorIndex = color; why = "Sign colour updated."; return true;
            case ActionPinDisplay:
                if (!state.Pin(recipe, display)) { why = "Get that display first, then choose a find from your inventory."; return false; }
                why = display == null ? "Display cleared." : "Display updated. The original stays in your inventory."; return true;
            case ActionWanted: {
                var def = ItemCatalog.Get(recipe);
                if (def == null) { why = "That isn't something you can hunt for."; return false; }
                if (!state.SetPin(CatalogNumber(recipe), color != 0, out why)) return false;
                why = color != 0 ? $"Hunting for {def.name}. It's marked WANTED, and Magnus will keep it." : $"Stopped hunting for {def.name}."; return true;
            }
            case ActionInstallDecor: {
                if (!state.InstallDecor(recipe, out why)) { if (state.Completed(recipe)) why = ""; return false; } // a repeat is silent
                why = WorkshopCatalog.Get(recipe).name + " delivered and installed!"; return true;
            }
            default: why = "Unknown shop action."; return false;
        }
    }

    // ------------------------------------------------------------------ kept items: what counts, what is free to use

    /// <summary>A kept NON-valuable: a part. It never counts as a treasure, never goes on a display, sells only for scrap.</summary>
    public static bool IsPart(ItemInstance item) => item != null && IsPart(item.Def);
    public static bool IsPart(ItemDef def) => def != null && def.category != ItemCategory.Valuable;

    static Dictionary<string, int> catalogNumbers;
    /// <summary>1-based position of a def in <see cref="ItemCatalog.All"/> (append-only, like DailyStats' saved index). 0 = unknown.</summary>
    public static int CatalogNumber(string defId)
    {
        if (string.IsNullOrEmpty(defId)) return 0;
        if (catalogNumbers == null) {
            catalogNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            var all = ItemCatalog.All;
            for (int i = 0; i < all.Count; i++) catalogNumbers[all[i].id] = i + 1;
        }
        return catalogNumbers.TryGetValue(defId, out int number) ? number : 0;
    }
    public static ItemDef DefOfNumber(int number) => number >= 1 && number <= ItemCatalog.All.Count ? ItemCatalog.All[number - 1] : null;

    /// <summary>Why a kept item cannot be used in a build right now, or null when it is free. Listed items are not in the
    /// inventory at all; these are the two ways an inventory item is spoken for.</summary>
    public static string BlockedReason(ItemInstance item, WorkshopState state)
    {
        if (item == null || string.IsNullOrEmpty(item.uid)) return "missing";
        if (EbaeMarket.IsReservedUid(item.uid)) return "offered in a trade";
        if (state?.displays != null) foreach (var pin in state.displays) if (pin != null && pin.uid == item.uid) return "on a display";
        if (DisplayPlanner.IsPinnedUid(item.uid)) return "pinned to a display";
        return null;
    }
    /// <summary>What the local player holds, by def: free to use, and spoken for.</summary>
    public sealed class Stock
    {
        internal readonly Dictionary<string, List<ItemInstance>> free = new Dictionary<string, List<ItemInstance>>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> blocked = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> blockedWhy = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Have(string defId) => defId != null && free.TryGetValue(defId, out var list) ? list.Count : 0;
        public int Blocked(string defId) => defId != null && blocked.TryGetValue(defId, out int n) ? n : 0;
        public string BlockedWhy(string defId) => defId != null && blockedWhy.TryGetValue(defId, out string why) ? why : null;
        /// <summary>Free kept items of a sorted material (Monument loads), leaving out defs <paramref name="skipDef"/> names
        /// (<see cref="MonumentSkip"/>: the Monument never eats a wanted part or one a blueprint still needs).</summary>
        public int HaveOf(WorkshopMaterial material, Func<string, bool> skipDef = null)
        {
            int n = 0;
            foreach (var kv in free) {
                if (kv.Value.Count == 0 || skipDef != null && skipDef(kv.Key)) continue;
                var def = kv.Value[0].Def;
                if (def != null && WorkshopCatalog.TryMaterial(def.category, out var mine) && mine == material) n += kv.Value.Count;
            }
            return n;
        }
        internal void Add(ItemInstance item, string why)
        {
            if (why != null) { blocked.TryGetValue(item.defId, out int n); blocked[item.defId] = n + 1; blockedWhy[item.defId] = why; return; }
            if (!free.TryGetValue(item.defId, out var list)) free[item.defId] = list = new List<ItemInstance>();
            list.Add(item);
        }
    }
    /// <summary>The local inventory as build stock for <paramref name="state"/> (this business's display pins block their finds).</summary>
    public static Stock StockFor(WorkshopState state) => StockOf(GameSession.Inventory, item => BlockedReason(item, state));
    internal static Stock StockOf(IEnumerable<ItemInstance> inventory, Func<ItemInstance, string> blockedReason)
    {
        var stock = new Stock();
        if (inventory != null)
            foreach (var item in inventory)
                if (item != null && item.Def != null && !string.IsNullOrEmpty(item.uid)) stock.Add(item, blockedReason?.Invoke(item));
        foreach (var list in stock.free.Values) list.Sort(CheapestFirst); // builds spend the least valuable instances first
        return stock;
    }
    static int CheapestFirst(ItemInstance a, ItemInstance b)
    {
        int byValue = a.value.CompareTo(b.value); if (byValue != 0) return byValue;
        if (a.hot != b.hot) return a.hot ? -1 : 1; // then stolen ones: only a fence would take those anyway
        return string.CompareOrdinal(a.uid, b.uid);
    }
    /// <summary>The instances a recipe would consume from <paramref name="stock"/>, or null when a slot is short.</summary>
    internal static List<ItemInstance> SelectFor(PartRecipe recipe, Stock stock)
    {
        var ids = recipe?.Pick(stock.Have);
        if (ids == null) return null;
        var taken = new Dictionary<string, int>(StringComparer.Ordinal); var items = new List<ItemInstance>(ids.Count);
        foreach (string id in ids) { taken.TryGetValue(id, out int n); items.Add(stock.free[id][n]); taken[id] = n + 1; }
        return items;
    }
    /// <summary>The Monument leaves these defs alone: anything this business is hunting for, and anything the NEXT level of
    /// a blueprint it could print still needs (the same "NEEDED FOR" notion ebag shows). One pass over the tree.</summary>
    public static Func<string, bool> MonumentSkip(WorkshopState state)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (state != null)
            foreach (var node in CallingCatalog.All) {
                var recipe = state.NextRecipe(node);
                if (recipe == null) continue;
                var status = state.StatusOf(node);
                if (status == CallingStatus.Locked || status == CallingStatus.ComingSoon) continue;
                foreach (var slot in recipe.slots) foreach (string option in slot.anyOf) keep.Add(option);
            }
        return defId => keep.Contains(defId) || state != null && state.IsPinned(CatalogNumber(defId));
    }
    /// <summary>Free kept items the Monument may take right now, per sorted type (<see cref="MonumentSkip"/> applied).</summary>
    public static WorkshopMaterials MonumentStock(WorkshopState state, Stock stock)
    {
        var have = new WorkshopMaterials();
        if (stock == null) return have;
        var skip = MonumentSkip(state);
        for (int m = 0; m < 4; m++) have.Set((WorkshopMaterial)m, stock.HaveOf((WorkshopMaterial)m, skip));
        return have;
    }
    /// <summary>One request never carries more items than this (the offer has to fit a message). A full parts shelf is
    /// two requests; <see cref="FeedMonument"/> sends them one after the other.</summary>
    public const int MaxFeedItems = 100;
    /// <summary>
    /// The instances one Monument load would hand over: for each sorted type, up to what the next tier still needs, the
    /// lightest free items first (heavy ones are worth more in the bins), never a def <paramref name="skipDef"/> names.
    /// The types take turns so a capped load moves all four along. Empty when nothing can be loaded.
    /// </summary>
    internal static List<ItemInstance> SelectForMonument(WorkshopState state, Stock stock, Func<string, bool> skipDef = null, int cap = MaxFeedItems)
    {
        var items = new List<ItemInstance>();
        if (state == null || stock == null || state.monument >= MonumentCatalog.MaxLevel) return items;
        var need = state.MonumentNeed();
        var pools = new List<ItemInstance>[4];
        for (int m = 0; m < 4; m++) {
            var pool = pools[m] = new List<ItemInstance>();
            foreach (var kv in stock.free) {
                if (skipDef != null && skipDef(kv.Key)) continue;
                foreach (var item in kv.Value) if (WorkshopCatalog.TryMaterial(item.Def.category, out var mine) && (int)mine == m) pool.Add(item);
            }
            pool.Sort((a, b) => { int byMass = a.massKg.CompareTo(b.massKg); return byMass != 0 ? byMass : string.CompareOrdinal(a.uid, b.uid); });
            int take = Math.Min(need.Get((WorkshopMaterial)m), pool.Count);
            if (take < pool.Count) pool.RemoveRange(take, pool.Count - take);
        }
        for (int round = 0; items.Count < cap; round++) {
            bool any = false;
            for (int m = 0; m < 4 && items.Count < cap; m++)
                if (round < pools[m].Count) { items.Add(pools[m][round]); any = true; }
            if (!any) break;
        }
        return items;
    }

    // ------------------------------------------------------------------ printing (blueprint levels and Monument loads)

    const float BuildTimeout = 8f;
    static uint nextRequest;
    static uint pendingRequest;
    static float pendingUntil;
    static List<string> pendingUids;
    static byte pendingAction;
    static string pendingId;
    static int pendingLot;
    // a Monument load in progress: the state the next request is picked against (the host's answer applied here, so the
    // chain never waits for the replicated state to catch up), and the items the pending request offered
    static bool feedChain;
    static WorkshopState feedState;
    static List<string> pendingDefIds;
    // pieces loaded so far by the running chain (its one closing line counts them all), and the beat between requests
    static int feedLoaded;
    static float feedWaitUntil;
    /// <summary>Seconds between the requests of one Monument load: under the host's three-a-second print guard
    /// (WorkshopNet "rb.workshop.build"), and slow enough to watch the spools fill.</summary>
    internal const float FeedSpacing = 0.4f;
    /// <summary>A print request is on its way to the host, or a Monument load is between two requests (the app shows
    /// "Printing...").</summary>
    public static bool BuildPending => Time.unscaledTime < feedWaitUntil || pendingUids != null && Time.unscaledTime < pendingUntil;
    /// <summary>What the pending request is printing (a calling node id, or <see cref="MonumentCatalog.NodeId"/>); null when idle.</summary>
    public static string PendingId => BuildPending ? pendingId : null;

    /// <summary>Print the next level of a blueprint (a Callings node) from the owner's kept items. Owner only, standing in
    /// the shop; the reply is the level's how-to line. Nothing leaves the inventory unless the print happens.</summary>
    public static void BuildCalling(int lot, string nodeId) { feedChain = false; feedState = null; Build(lot, ActionBuildCalling, nodeId); }
    /// <summary>Load every kept piece the next Monument tier can take (a shelf can be more than one request: they follow
    /// each other). The load that fills the tier prints it.</summary>
    public static void FeedMonument(int lot)
    {
        if (BuildPending) { Notice(false, "Still printing the last thing. One moment."); return; }
        feedChain = true; feedState = null; feedLoaded = 0; Build(lot, ActionFeedMonument, MonumentCatalog.NodeId);
    }

    static void Build(int lot, byte action, string id)
    {
        if (!CanManage(lot)) { feedChain = false; Notice(false, OwnerOnly(action)); return; }
        if (BuildPending) { Notice(false, "Still printing the last thing. One moment."); return; }
        var state = action == ActionFeedMonument && feedState != null ? feedState : Snapshot(lot);
        if (state == null) { feedChain = false; Notice(false, "The print shop is still syncing. Try again in a moment."); return; }
        var stock = StockFor(state);
        List<ItemInstance> offer;
        if (action == ActionBuildCalling) {
            var node = CallingCatalog.Get(id);
            offer = SelectFor(state.NextRecipe(node), stock);
            if (offer == null && state.DiskInstalled && state.StatusOf(node) == CallingStatus.CantAfford) { Notice(false, ShortText(state.NextRecipe(node), stock)); return; }
        } else {
            offer = SelectForMonument(state, stock, MonumentSkip(state));
            if (offer.Count == 0 && state.DiskInstalled && state.monument < MonumentCatalog.MaxLevel)
            { feedChain = false; Notice(false, "Not enough kept scrap yet. Throw sorted pieces in the KEEP crate."); return; }
        }
        offer ??= new List<ItemInstance>(); // let the rules say why (locked, maxed, coming soon, no disk)
        pendingAction = action; pendingId = id; pendingLot = lot;
        pendingDefIds = new List<string>(offer.Count); foreach (var item in offer) pendingDefIds.Add(item.defId);
        if (action == ActionFeedMonument) feedState = state.Clone();
        var registry = Businesses.Registry;
        if (registry != null) {
            pendingRequest = ++nextRequest; pendingUids = Uids(offer); pendingUntil = Time.unscaledTime + BuildTimeout;
            registry.WorkshopBuild(lot, action, id, pendingRequest, offer); return;
        }
        if (GameSession.IsConnectedGuest || !InsideShop(Permissions.LocalClientId, lot)) { feedChain = false; return; }
        pendingRequest = ++nextRequest; pendingUids = Uids(offer); pendingUntil = Time.unscaledTime + BuildTimeout;
        bool ok = ApplyBuild(state, action, id, offer, out string why);
        if (ok) GameSession.SetWorkshopSave(state);
        OnBuildReply(pendingRequest, ok, why);
    }
    static List<string> Uids(List<ItemInstance> items) { var uids = new List<string>(items.Count); foreach (var item in items) uids.Add(item.uid); return uids; }
    /// <summary>"Still need: 2 Old Work Boot, 1 Oven Mitt." plus a hint when the missing piece is merely spoken for.</summary>
    internal static string ShortText(PartRecipe recipe, Stock stock)
    {
        if (recipe == null) return "You don't have all the parts for that yet.";
        var missing = new List<string>(); string free = null;
        foreach (var slot in recipe.slots) {
            int short_ = slot.count - PartRecipe.Covered(slot, stock.Have);
            if (short_ <= 0) continue;
            missing.Add(short_ + " " + SlotName(slot));
            foreach (string option in slot.anyOf) if (free == null && stock.Blocked(option) > 0) free = $"Your {NameOf(option)} is {stock.BlockedWhy(option)}: free it first.";
        }
        return "Still need: " + string.Join(", ", missing) + "." + (free != null ? " " + free : "");
    }
    public static string NameOf(string defId) => ItemCatalog.Get(defId)?.name ?? defId;
    public static string SlotName(RecipeSlot slot)
    {
        var names = new List<string>(slot.anyOf.Count);
        foreach (string option in slot.anyOf) names.Add(NameOf(option));
        return string.Join(" or ", names);
    }
    /// <summary>Apply a print to <paramref name="state"/> from the offered items: every one plausible for its catalog entry
    /// and distinct, their defs exactly the recipe (or a legal Monument load). The success text is the how-to line. Pure
    /// apart from the catalog.</summary>
    internal static bool ApplyBuild(WorkshopState state, byte action, string id, IReadOnlyList<ItemInstance> offered, out string why)
    {
        var defIds = new List<string>(offered?.Count ?? 0); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (offered != null)
            foreach (var item in offered) {
                if (!EbaeItemValidation.IsPlausible(item, out _) || !seen.Add(item.uid)) { why = "One of those parts doesn't look right."; return false; }
                defIds.Add(item.defId);
            }
        if (action == ActionBuildCalling) {
            if (!state.BuildCalling(id, defIds, out why)) return false;
            var node = CallingCatalog.Get(id); var level = node.Level(state.CallingLevelAt(node.index));
            why = string.IsNullOrEmpty(level.howTo) ? level.title + " printed!" : level.howTo; return true;
        }
        if (action == ActionFeedMonument) {
            if (!state.FeedMonument(defIds, out bool printed, out why)) return false;
            why = printed ? MonumentPrintedText(state.monument) : MonumentLoadedText(defIds.Count, state.MonumentNeed().Total);
            return true;
        }
        why = "Unknown shop action."; return false;
    }
    internal static string MonumentPrintedText(int tier) =>
        $"MONUMENT TIER {tier}: {MonumentCatalog.Tier(tier)?.name ?? "?"} printed! It's standing outside your depot.";
    internal static string MonumentLoadedText(int loaded, int left) => $"Loaded {loaded} piece{(loaded == 1 ? "" : "s")}. {left} to go.";
    /// <summary>The host answered a print request: on yes, the offered items leave the inventory (and only then). A
    /// Monument load that still has pieces to send follows up by itself.</summary>
    internal static void OnBuildReply(uint request, bool ok, string text)
    {
        byte action = 0; string id = null; int lot = -1; bool mine = pendingUids != null && request == pendingRequest;
        if (mine) {
            action = pendingAction; id = pendingId; lot = pendingLot;
            if (ok) Consume(pendingUids);
            pendingUids = null;
        }
        bool more = false;
        if (mine && ok && action == ActionFeedMonument && feedChain && feedState != null) {
            // apply the host's yes to the state the next request is picked against; a printed tier ends the chain
            int sent = pendingDefIds != null ? pendingDefIds.Count : 0;
            feedLoaded += sent;
            bool applied = feedState.FeedMonument(pendingDefIds, out bool printed, out _);
            more = applied && !printed && SelectForMonument(feedState, StockFor(feedState), MonumentSkip(feedState)).Count > 0;
            // the host's line only counts its own request: the closing line of a longer load counts all of it
            if (applied && !printed && !more && feedLoaded > sent) text = MonumentLoadedText(feedLoaded, feedState.MonumentNeed().Total);
        }
        if (!more) { feedChain = false; feedState = null; feedLoaded = 0; }
        if (!more) Notice(ok, text); // one line for a whole load, not one per request
        if (mine) { try { BuildReplied?.Invoke(action, id, ok, more ? "" : text); } catch (Exception e) { Debug.LogException(e); } }
        RaiseChanged();
        if (more) FollowUp(lot);
    }
    /// <summary>The next request of a Monument load, a beat later (<see cref="FeedSpacing"/>).</summary>
    static void FollowUp(int lot)
    {
        feedWaitUntil = Time.unscaledTime + FeedSpacing + 0.5f; // bounded: a session that ends mid-load must not jam the printer
        if (NetBoot.Run(FollowUpSoon(lot)) != null) return;
        feedWaitUntil = 0f; Build(lot, ActionFeedMonument, MonumentCatalog.NodeId); // no runner (not playing): right away
    }
    static IEnumerator FollowUpSoon(int lot)
    {
        yield return new WaitForSecondsRealtime(FeedSpacing);
        feedWaitUntil = 0f;
        if (feedChain) Build(lot, ActionFeedMonument, MonumentCatalog.NodeId);
    }
    static void Consume(List<string> uids) { foreach (string uid in uids) GameSession.RemoveFromInventory(uid); }

    // ---- the offer on the wire: one record per item, enough for the host's plausibility check, compact enough that a
    //      full Monument load (MaxFeedItems) fits one RPC. Fields: uid, catalog number, grams, condition per mille, cents, hot.
    const char FieldSep = '\u001f', ItemSep = '\u001e';
    internal const int MaxOfferItems = 120, MaxOfferChars = 4200;
    internal static string EncodeOffer(IReadOnlyList<ItemInstance> items)
    {
        var sb = new StringBuilder(items.Count * 34);
        foreach (var item in items) {
            if (item == null || string.IsNullOrEmpty(item.uid) || item.uid.IndexOf(FieldSep) >= 0 || item.uid.IndexOf(ItemSep) >= 0) return null;
            int number = CatalogNumber(item.defId); if (number <= 0) return null;
            if (sb.Length > 0) sb.Append(ItemSep);
            sb.Append(item.uid).Append(FieldSep).Append(number.ToString(CultureInfo.InvariantCulture)).Append(FieldSep)
              .Append(((int)Math.Round(item.massKg * 1000d)).ToString(CultureInfo.InvariantCulture)).Append(FieldSep)
              .Append(((int)Math.Round(item.condition * 1000d)).ToString(CultureInfo.InvariantCulture)).Append(FieldSep)
              .Append(((long)Math.Round(item.value * 100d)).ToString(CultureInfo.InvariantCulture)).Append(FieldSep).Append(item.hot ? '1' : '0');
        }
        return sb.Length > MaxOfferChars ? null : sb.ToString();
    }
    internal static bool TryDecodeOffer(string text, out List<ItemInstance> items)
    {
        items = new List<ItemInstance>();
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > MaxOfferChars) return false;
        foreach (string record in text.Split(ItemSep)) {
            var f = record.Split(FieldSep);
            if (f.Length != 6 || items.Count >= MaxOfferItems) return false;
            if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || DefOfNumber(number) == null ||
                !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out int grams) ||
                !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int condition) ||
                !long.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out long cents) || f[5] != "0" && f[5] != "1") return false;
            items.Add(new ItemInstance { uid = f[0], defId = DefOfNumber(number).id, massKg = grams / 1000f, condition = condition / 1000f, value = cents / 100f, hot = f[5] == "1" });
        }
        return true;
    }

    // ------------------------------------------------------------------ wanted parts

    /// <summary>The LOCAL player's business is hunting for this def (HUD "WANTED PART!", ebag tags).</summary>
    public static bool IsWanted(string defId) => GameSession.WorkshopWants(CatalogNumber(defId));
    public static bool IsWanted(ItemInstance item) => item != null && IsWanted(item.defId);
    /// <summary>The business at <paramref name="lot"/> is hunting for this def. Readable on the host for every lot (Magnus).</summary>
    public static bool IsWantedAt(int lot, string defId)
    {
        int number = CatalogNumber(defId);
        if (number <= 0 || !TryBusinessAt(lot, out var business)) return false;
        var registry = Businesses.Registry;
        return registry != null ? registry.WorkshopWants(business.Id, number) : business.Id == GameSession.BusinessId && GameSession.WorkshopWants(number);
    }
    /// <summary>Any def the business owned by <paramref name="client"/> wants is among <paramref name="items"/> (host: the X-ray plate).</summary>
    public static bool AnyWantedBy(ulong client, IReadOnlyList<ItemInstance> items)
    {
        if (items == null) return false;
        var registry = Businesses.Registry; BusinessInfo business = default;
        if (registry == null ? client != Permissions.LocalClientId : !Businesses.TryGetForClient(client, out business)) return false;
        foreach (var item in items) {
            int number = item != null ? CatalogNumber(item.defId) : 0;
            if (number > 0 && (registry == null ? GameSession.WorkshopWants(number) : registry.WorkshopWants(business.Id, number))) return true;
        }
        return false;
    }
    /// <summary>The blueprints whose NEXT level needs this def, as "Night Headlamp, Hard Hat" (ebag's "needed for" tag).
    /// Only nodes this business could print next: prerequisites met, not maxed.</summary>
    public static string NeededFor(string defId, WorkshopState state)
    {
        if (state == null || string.IsNullOrEmpty(defId)) return "";
        var names = new List<string>();
        foreach (var node in CallingCatalog.All) {
            var recipe = state.NextRecipe(node);
            if (recipe == null || state.StatusOf(node) == CallingStatus.Locked || state.StatusOf(node) == CallingStatus.ComingSoon) continue;
            foreach (var slot in recipe.slots) if (slot.Accepts(defId)) { names.Add(node.name); break; }
        }
        return string.Join(", ", names);
    }

    // ------------------------------------------------------------------ the SolidWaste 3D disk (shop events)

    // Event bytes are wire values. Anyone who may WORK in a shop (owner, hired crew) may report these for it.
    internal const byte EventBagRipped = 1, EventDiskKept = 2;
    static readonly Dictionary<int, SortingTable> ripTables = new Dictionary<int, SortingTable>();
    static readonly Dictionary<int, float> diskIssuedAt = new Dictionary<int, float>();
    const float DiskIssueGrace = 20f;

    /// <summary>A bag was just ripped open on <paramref name="table"/> by this machine. While the shop's business has no
    /// disk yet, the host decides whether this bag had one in it (<see cref="WorkshopState.BagRipped"/>); if so it pops
    /// out of the bag on this machine (<see cref="OnDiskDrop"/>).</summary>
    public static void BagRipped(SortingTable table)
    {
        if (table == null || !Application.isPlaying) return;
        int lot = DepotMachineContext.LotOf(table);
        var known = Snapshot(lot);
        if (known != null && known.DiskInstalled) return; // the hunt is over for this shop: nothing to report
        ripTables[lot] = table;
        ShopEvent(lot, EventBagRipped);
    }
    /// <summary>The floppy disk went into the KEEP crate (or the popup's KEEP button) of the shop on <paramref name="lot"/>:
    /// it goes into that business's laptop.</summary>
    public static void DiskKept(int lot) => ShopEvent(lot, EventDiskKept);

    static void ShopEvent(int lot, byte kind)
    {
        if (!TryBusinessAt(lot, out var business)) return;
        var registry = Businesses.Registry;
        if (registry != null) { registry.WorkshopShopEvent(lot, kind); return; }
        if (GameSession.IsConnectedGuest || business.Id != GameSession.BusinessId) return;
        var state = GameSession.WorkshopSave;
        if (!ApplyShopEvent(state, kind, DiskRoll(business.Id, state), DiskIsOut(lot), out bool drop)) return;
        GameSession.SetWorkshopSave(state); RaiseChanged();
        if (drop) { diskIssuedAt[lot] = Time.unscaledTime; OnDiskDrop(lot); }
    }
    /// <summary>The roll for the NEXT bag of a business's disk window: a hash of the business, the window and the bag
    /// number (no UnityEngine.Random: Docs/Multiplayer.md §9 rule 2), so the host and a solo game decide alike.</summary>
    internal static double DiskRoll(string businessId, WorkshopState state) =>
        GarageRoll.Value($"disk/{businessId}/{state.diskDrops}/{state.diskBags + 1}");
    /// <summary>Apply a shop event to <paramref name="state"/>. True when the state changed; <paramref name="dropDisk"/> =
    /// this bag carries the disk. Pure.</summary>
    internal static bool ApplyShopEvent(WorkshopState state, byte kind, double roll01, bool diskIsOut, out bool dropDisk)
    {
        dropDisk = false;
        if (state == null) return false;
        switch (kind) {
            case EventBagRipped: {
                int bags = state.diskBags, drops = state.diskDrops;
                dropDisk = state.BagRipped(roll01, diskIsOut);
                return dropDisk || bags != state.diskBags || drops != state.diskDrops;
            }
            // a disk can only be kept once one was handed out (and only once)
            case EventDiskKept: return state.diskDrops > 0 && state.InsertDisk();
            default: return false;
        }
    }
    /// <summary>A disk is already lying around the shop on <paramref name="lot"/> (or was handed out moments ago and is
    /// still on its way): the next one waits.</summary>
    internal static bool DiskIsOut(int lot)
    {
        if (diskIssuedAt.TryGetValue(lot, out float at) && Time.unscaledTime - at < DiskIssueGrace) return true;
        bool oneWorld = DepotRowWorld.Active;
        var items = LooseItem.All;
        for (int i = 0; i < items.Count; i++) {
            var li = items[i];
            if (li == null || li.State == CarryState.Consumed || !DiskItem.Is(li.Item)) continue;
            if (!oneWorld || DepotRowLayout.InteriorAt(li.transform.position) == lot) return true;
        }
        return false;
    }
    internal static void NoteDiskIssued(int lot) => diskIssuedAt[lot] = Time.unscaledTime;
    /// <summary>The host said the bag this machine just ripped on <paramref name="lot"/> had the disk in it: pop it out.</summary>
    internal static void OnDiskDrop(int lot)
    {
        ripTables.TryGetValue(lot, out var table);
        if (table == null)
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<SortingTable>(FindObjectsSortMode.None))
                if (candidate != null && candidate.isActiveAndEnabled && DepotMachineContext.LotOf(candidate) == lot) { table = candidate; break; }
        if (table != null) DiskItem.SpawnOn(table);
    }
    /// <summary>GameSession: this machine's own business just got its disk while playing.</summary>
    internal static void RaiseDiskInstalled() { try { DiskInstalled?.Invoke(); } catch (Exception e) { Debug.LogException(e); } }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // ---- debug / QA (Rubbish > Debug > Shop). Solo, or the host editing its OWN business; never reachable from an RPC.
    static bool DebugEdit(Action<WorkshopState> edit, out string why)
    {
        why = "";
        if (GameSession.IsConnectedGuest) { why = "Solo or host only."; return false; }
        var state = GameSession.WorkshopSave; edit(state);
        state.NormalizePrinter(); // printing anything implies the disk
        if (!state.Valid()) { why = "That edit made an invalid print-shop state."; return false; }
        var registry = Businesses.Registry;
        if (registry != null) return registry.WorkshopDebugPublish(state, out why);
        GameSession.SetWorkshopSave(state); RaiseChanged(); return true;
    }
    /// <summary>Every printable blueprint level at once, free (true), or the whole tree back to nothing (false). Upgrade levels
    /// are only ever raised by the tree, so a reset must also zero the gear ids itself (Setup_Shop does).</summary>
    public static bool DebugSetCallings(bool built, out string why) => DebugEdit(state => {
        state.callings.Clear();
        if (built) foreach (var node in CallingCatalog.All) if (node.implemented) state.SetCallingLevel(node.index, node.implementedLevels);
    }, out why);
    /// <summary>QA: give (true) or take away (false) the SolidWaste 3D disk. Taking it away also clears everything it
    /// printed (a state with prints and no disk is not valid) and restarts the first-ten-bags window.</summary>
    public static bool DebugSetDisk(bool installed, out string why) => DebugEdit(state => {
        if (installed) { state.disk = 1; return; }
        state.callings.Clear(); state.monument = 0; state.fed = default; state.disk = 0; state.diskBags = 0; state.diskDrops = 0;
    }, out why);
    /// <summary>QA: count a disk as handed out, the way a lucky bag does, so one dropped from the debug menu can be KEPT
    /// (<see cref="ApplyShopEvent"/> only installs a disk that was issued). Nothing to do once the disk is in.</summary>
    public static bool DebugIssueDisk(out string why) => DebugEdit(state => {
        if (state.disk == 0) { state.diskBags = 0; state.diskDrops = Mathf.Max(1, state.diskDrops + 1); }
    }, out why);
    /// <summary>QA: set the Monument to a tier (0..10), nothing loaded toward the next.</summary>
    public static bool DebugSetMonument(int tier, out string why) => DebugEdit(state => {
        state.monument = Mathf.Clamp(tier, 0, MonumentCatalog.MaxLevel); state.fed = default; state.disk = 1;
    }, out why);
    static int debugScrapBatch;
    /// <summary>QA: <paramref name="each"/> plain kept pieces of every sorted type (cans, bottles, jars, newspapers) into
    /// the local inventory: Monument food. Solo or host only; ignores the parts shelf cap on purpose.</summary>
    public static bool DebugAddScrap(int each, out string why)
    {
        why = "";
        if (GameSession.IsConnectedGuest) { why = "Solo or host only."; return false; }
        string stamp = $"{Time.frameCount:x}-{++debugScrapBatch:x}";
        foreach (string id in new[] { "soda_can", "water_bottle", "beer_bottle", "newspaper" }) {
            var def = ItemCatalog.Get(id); if (def == null) { why = "Missing catalog item: " + id; return false; }
            for (int i = 0; i < Mathf.Clamp(each, 1, 1000); i++)
                GameSession.AddToInventory(new ItemInstance {
                    uid = $"dbs{stamp}-{CatalogNumber(def.id):x}-{i}", defId = def.id, massKg = def.minMass, condition = 1f, value = 0f,
                    foundAt = "Debug Drawer", dayFound = GameSession.Day });
        }
        RaiseChanged(); return true;
    }
    /// <summary>QA: put <paramref name="copies"/> of every part any recipe names (and that many of each sorted material)
    /// into the local inventory. Solo or host only; these are ordinary kept items.</summary>
    public static bool DebugAddParts(int copies, out string why)
    {
        why = "";
        if (GameSession.IsConnectedGuest) { why = "Solo or host only."; return false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in CallingCatalog.All)
            for (int level = 1; level <= node.maxLevel; level++)
                if (node.RecipeOf(level) is PartRecipe recipe) foreach (var slot in recipe.slots) foreach (string option in slot.anyOf) ids.Add(option);
        foreach (string id in new[] { "soda_can", "water_bottle", "beer_bottle", "newspaper" }) ids.Add(id);
        foreach (string id in ids) {
            var def = ItemCatalog.Get(id); if (def == null) continue;
            for (int i = 0; i < Mathf.Clamp(copies, 1, 12); i++) // plain mid-range instances: nothing here is rolled
                GameSession.AddToInventory(new ItemInstance {
                    uid = $"dbg{Time.frameCount:x}-{CatalogNumber(def.id):x}-{i}", defId = def.id, massKg = (def.minMass + def.maxMass) * 0.5f, condition = 1f,
                    value = def.category == ItemCategory.Valuable ? Mathf.Max(1f, Mathf.Round((def.minValue + def.maxValue) * 0.5f)) : 0f,
                    foundAt = "Debug Drawer", dayFound = GameSession.Day });
        }
        RaiseChanged(); return true;
    }
#endif
    internal static ItemInstance OwnedItem(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        foreach (var item in GameSession.Inventory) if (item != null && !item.hot && item.uid == uid && item.Def?.category == ItemCategory.Valuable) return item;
        return null;
    }
    internal static void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception e) { Debug.LogException(e); } }
    internal static void Notice(bool ok, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { if (NoticeSink != null && NoticeSink(ok, text)) return; } catch (Exception e) { Debug.LogException(e); }
        GameHUD.Toast(text, ok ? ToastStyle.Good : ToastStyle.Bad, 3f);
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        Changed = null; DiskInstalled = null; BuildReplied = null; NoticeSink = null;
        pendingUids = null; pendingDefIds = null; pendingId = null; pendingAction = 0; pendingLot = -1;
        pendingRequest = nextRequest = 0; pendingUntil = 0f; catalogNumbers = null;
        feedChain = false; feedState = null; feedLoaded = 0; feedWaitUntil = 0f; ripTables.Clear(); diskIssuedAt.Clear();
    }
}

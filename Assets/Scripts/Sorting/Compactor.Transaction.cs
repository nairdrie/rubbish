using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Outcomes are a single host transaction. Suction is a presentation of that captured stock, not a second
// physics/consumption simulation on every peer. No ledger scope or half-consumed load survives a yield.
public partial class Compactor
{
    sealed class Confirmation
    {
        public string signature;
        public float at, until;
    }
    readonly Dictionary<(ulong, bool), Confirmation> confirmations = new Dictionary<(ulong, bool), Confirmation>();
    float bulkConfirmationHoldUntil = -1f;
    uint suctionSequence;
    public bool LocalWatching => PlayerController.Local != null && ContainsShopPoint(PlayerController.Local.transform.position); // coop:allow(local) presentation audience only

    public bool ContainsShopPoint(Vector3 p)
    {
        int lot = DepotMachineContext.LotOf(this);
        if (lot >= 0) return DepotRowLayout.InteriorAt(p) == lot;
        // The legacy single shop has no DepotLot. Never sweep an entire town/additive scene by accident.
        return gameObject.scene.name == "ShopScene" && new Bounds(new Vector3(0.5f, 4f, 1f), new Vector3(30f, 16f, 30f)).Contains(p); // coop:allow(scene) legacy solo shop bounds
    }

    public static bool AvailableForCompact(Carryable c)
    {
        if (c == null || !c.isActiveAndEnabled || !(c is TrashBag || c is LooseItem) ||
            !SortingKit.IsLoose(c) || c.IsInScriptedFlight || c.Holder != null) return false;
        var nc = c.GetComponent<NetCarry>();
        // Network copies at rest are kinematic on guests: that is NOT a held/scripted item.
        if (nc != null) return nc.St.Phase == HandsPhase.Rest && !nc.WindowOpen && !nc.PendingGrab && !nc.PendingGone
            && (c.NetRemote || c.Body == null || !c.Body.isKinematic);
        return c.Body == null || !c.Body.isKinematic;
    }

    public List<Carryable> CollectForCompact(bool bulk)
    {
        var result = bulk ? new List<Carryable>(Carryable.All) : Contents(new List<Carryable>());
        result.RemoveAll(c => c == null || !ContainsShopPoint(c.transform.position) ||
            !(AvailableForCompact(c) || bulk && AvailableConveyorStock(c)));
        return result;
    }

    // Only bulk may revoke a known belt/chute/tray anchor. Generic scripted throws, Magnus fetches,
    // guest-held flights, and pending grabs remain excluded; a kinematic body alone proves nothing.
    bool AvailableConveyorStock(Carryable c)
    {
        if (!(c is TrashBag bag) || !bag.isActiveAndEnabled || bag.HasBurst || !SortingKit.IsLoose(bag) ||
            bag.Holder != null || !ConveyorStock.OwnedInShop(bag, this)) return false;
        var nc = bag.GetComponent<NetCarry>();
        if (nc == null) return true;
        if (nc.WindowOpen || nc.PendingGrab || nc.PendingGone) return false;
        return nc.St.Phase == HandsPhase.Rest ||
            nc.St.Phase == HandsPhase.Flight && nc.St.holder == Unity.Netcode.NetworkManager.ServerClientId;
    }

    internal static bool ShopBusy(Component source)
    {
        if (source == null) return false;
        int lot = DepotMachineContext.LotOf(source);
        if (CompactorNet.IsBusy(lot)) return true;
        var c = ShopCompactor(source);
        return c != null && c.busy;
    }

    static Compactor ShopCompactor(Component source)
    {
        if (source == null) return null;
        if (source is Compactor self) return self;
        var root = source.GetComponentInParent<DepotLot>();
        return root != null ? root.GetComponentInChildren<Compactor>(true)
            : source.transform.parent != null ? source.transform.parent.GetComponentInChildren<Compactor>(true) : null;
    }

    // A guest's UI warning is not authority. Only PressHost arms this short, unscaled machine pause.
    // Already-running bag arcs/rips finish normally; the next automatic pickup/slit waits for confirmation or expiry.
    internal void HoldBulkConfirmation(float now) =>
        bulkConfirmationHoldUntil = now + Mathf.Max(ConfirmMinDelay, crushAllConfirmWindow);

    internal bool BulkConfirmationHeldAt(float now) => now < bulkConfirmationHoldUntil;

    internal static bool AutomationPaused(Component source)
    {
        if (ShopBusy(source)) return true;
        var c = ShopCompactor(source);
        return c != null && c.BulkConfirmationHeldAt(Time.unscaledTime);
    }

    void SetBusyNetwork(bool value) => CompactorNet.Instance?.SetBusy(DepotMachineContext.LotOf(this), value);

    bool WorkSettling()
    {
        FindParts();
        int lot = DepotMachineContext.LotOf(this);
        if (table != null && table.IsRipping || SortingTableCapacityNet.PendingForLot(lot) > 0) return true;
        var root = GetComponentInParent<DepotLot>();
        var tip = root != null ? root.GetComponentInChildren<TruckUnloader>(true)
            : transform.parent != null ? transform.parent.GetComponentInChildren<TruckUnloader>(true) : null;
        return tip != null && tip.Busy;
    }

    void RequestCompact(bool bulk)
    {
        if (Busy) return;
        if (!Permissions.LocalCanWorkAt(transform.position)) { Permissions.Deny(transform.position); return; }
        var net = CompactorNet.Instance;
        if (net != null && net.IsSpawned) net.Request(this, bulk);
        else if (!GameSession.IsConnectedGuest) PressHost(Permissions.LocalClientId, bulk);
    }

    /// <summary>After the station's explicit treasure warning. The bag stays held until the host accepts it.</summary>
    public void CompactWholeBag(TrashBag bag)
    {
        if (bag == null || Busy) return;
        var net = CompactorNet.Instance;
        if (net != null && net.IsSpawned) net.RequestWholeBag(this, bag);
        else if (!GameSession.IsConnectedGuest) WholeBagHost(Permissions.LocalClientId, bag);
    }

    internal void WholeBagHost(ulong actor, TrashBag bag)
    {
        if (Busy || bag == null || bag.HasBurst || bag.State == CarryState.Consumed) return;
        if (!Permissions.CanWorkIn(actor, DepotMachineContext.LotOf(this))) return;
        var nc = bag.GetComponent<NetCarry>();
        if (nc != null && (nc.WindowOpen || nc.PendingGrab || nc.PendingGone))
        { Notice(actor, false, false, "Get a firm grip on the bag first."); return; }
        bool held = nc != null ? nc.HeldBy(actor) : bag.Holder != null && bag.Holder == TrashCarrier.Local && actor == Permissions.LocalClientId;
        if (!held) { Notice(actor, false, false, "Hold that bag first."); return; }
        BeginHostCompact(actor, false, new List<Carryable> { bag });
    }

    internal void PressHost(ulong actor, bool bulk)
    {
        if (!Businesses.IsAuthority) return;
        if (Busy) { Notice(actor, bulk, false, "Compactor busy — hold on."); return; }
        if (!Permissions.CanWorkIn(actor, DepotMachineContext.LotOf(this))) { Notice(actor, bulk, false, Permissions.NotYourShop); return; }
        // Arm even while an existing rip settles, otherwise a fast slitter can start the next bag before the retry.
        if (bulk) HoldBulkConfirmation(Time.unscaledTime);
        if (bulk && WorkSettling()) { Notice(actor, true, false, "Wait for the current bag or unloading to finish, then bulk compact."); return; }
        var stock = CollectForCompact(bulk);
        var people = bulk ? CompactorCube.PlayersInShop(this, new List<PlayerTells>()) : CompactorCube.PlayersIn(this, new List<PlayerTells>());
        if (stock.Count == 0 && people.Count == 0) { Notice(actor, bulk, false, bulk ? "Shop's clear. Truck cargo and held items stay untouched." : "Chamber empty. Toss trash in, or use Bulk compact shop."); return; }
        bool needsConfirmation = bulk;
        foreach (var c in stock) if (c is TrashBag && !confirmedBags.Contains(c)) needsConfirmation = true;
        var keys = new List<string>();
        float kg = 0f;
        foreach (var c in stock) { keys.Add(c.NetId != 0 ? "n" + c.NetId : "l" + c.GetInstanceID()); kg += c.MassKg; }
        foreach (var p in people) keys.Add("p" + p.ClientId);
        keys.Sort(StringComparer.Ordinal);
        string signature = string.Join(",", keys);
        var key = (actor, bulk);
        float now = Time.unscaledTime;
        if (needsConfirmation)
        {
            if (!confirmations.TryGetValue(key, out var confirm) || confirm.until < now || confirm.signature != signature)
            {
                confirmations[key] = new Confirmation { signature = signature, at = now, until = now + crushAllConfirmWindow };
                string scope = bulk ? "BULK COMPACT SHOP" : "COMPACT CHAMBER";
                string peopleWarning = bulk ? " Everyone inside gets boxed!\nTruck, held and saved items stay safe." : "";
                Notice(actor, bulk, true, $"{scope}: {stock.Count} things / {kg:0.#} kg → {Economy.Money(SortingKit.Cents(kg * LazyPerKgHere))}\nTreasures get crushed too.{peopleWarning}\nPress again to confirm.");
                return;
            }
            if (now - confirm.at < ConfirmMinDelay) return;
        }
        confirmations.Clear();
        Notice(actor, bulk, false, "");
        BeginHostCompact(actor, bulk, stock);
    }

    internal void ApplyNotice(bool bulk, bool armed, string message)
    {
        if (bulk) crushAllArmedUntil = armed ? Time.time + crushAllConfirmWindow : -1f;
        else smashArmedUntil = armed ? Time.unscaledTime + crushAllConfirmWindow : -1f;
        if (armed) CompactorConfirmOverlay.Show(this, message, crushAllConfirmWindow);
        else
        {
            CompactorConfirmOverlay.Clear(this);
            if (!string.IsNullOrEmpty(message)) GameHUD.Toast(message, ToastStyle.Info, 3f);
        }
    }

    void Notice(ulong actor, bool bulk, bool armed, string message)
    {
        if (CompactorNet.Instance != null && CompactorNet.Instance.IsSpawned)
            CompactorNet.Instance.Notify(actor, DepotMachineContext.LotOf(this), bulk, armed, message);
        else ApplyNotice(bulk, armed, message);
    }

    sealed class CompactReceipt
    {
        public float kg, pay;
        public int bags, hazards;
        public readonly List<string> lost = new List<string>();
        public readonly List<ItemInstance> rescued = new List<ItemInstance>();
        public readonly List<Color> colors = new List<Color>();
    }

    CompactReceipt Tally(List<Carryable> stock, bool bulk)
    {
        var receipt = new CompactReceipt();
        foreach (var c in stock)
        {
            receipt.kg += Mathf.Max(0f, c.MassKg);
            if (c is TrashBag bag)
            {
                receipt.bags++;
                receipt.colors.Add(SortingKit.SackBlack);
                if (bag.Contents?.items != null) foreach (var item in bag.Contents.items) TallyItem(item, receipt);
            }
            else if (c is LooseItem li)
            {
                if (li.Def != null) receipt.colors.Add(li.Def.color);
                TallyItem(li.Item, receipt);
            }
        }
        receipt.pay = SortingKit.Cents(receipt.kg * LazyPerKgHere);
        return receipt;
    }

    void TallyItem(ItemInstance item, CompactReceipt receipt)
    {
        var def = item?.Def;
        if (def == null) return;
        if (def.category == ItemCategory.Valuable)
        {
            if (GrateKeepHere > 0f) receipt.rescued.Add(Dent(item, GrateKeepHere));
            else receipt.lost.Add(def.name);
        }
        if (def.category == ItemCategory.Hazard && !HazardSafeHere && LazyBoomRoll(item)) receipt.hazards++;
    }

    internal void BeginHostCompact(ulong actor, bool bulk, List<Carryable> stock)
    {
        if (Busy || !Businesses.IsAuthority) return;
        Busy = true; SetBusyNetwork(true);
        bulkConfirmationHoldUntil = -1f;
        crushAllArmedUntil = smashArmedUntil = -1f;
        FindParts();
        var receipt = Tally(stock, bulk);
        // Snapshot render-only ghosts BEFORE the reliable Gone messages; capture once and never race a guest grab.
        var ids = new uint[stock.Count];
        for (int i = 0; i < stock.Count; i++)
        {
            if (stock[i].NetId == 0 && !NetSession.IsSolo && !SessionService.SoloOnly) HandsNet.Instance?.HostPublishNow(stock[i]);
            ids[i] = stock[i].NetId;
        }
        CompactorNet.Instance?.ShowStart(DepotMachineContext.LotOf(this), bulk, ids);
        StartVisualIntake(bulk, stock);
        if (bulk) suctionSequence = CompactorCube.BeginSuction(this, CompactorCube.PlayersInShop(this, new List<PlayerTells>()), CompactorFx.SuctionDuration);
        foreach (var c in stock)
        {
            if (bulk && c is TrashBag beltBag) ConveyorStock.Detach(beltBag);
            if (c is LooseItem li)
            {
                if (!bulk && li.Category == ItemCategory.Junk) RaiseCrushedFact(li, true, robotDropped.Contains(li));
                else if (!BusinessUpgradeNet.RecordRobotItem(DepotMachineContext.LotOf(this), li.Item, false)) GameSession.RecordSorted(li.Item, false);
            }
            c.Consume();
        }
        DepotMachineContext.Credit(this, receipt.pay, $"{(bulk ? "Bulk compact shop" : "Compact chamber")} {receipt.kg:0.#} kg", bulk ? LedgerIds.LazyCrush : LedgerIds.Scrap, TossTargetPosition);
        foreach (var item in receipt.rescued)
            if (!BusinessUpgradeNet.KeepRobotItem(DepotMachineContext.LotOf(this), item))
            { GameSession.AddToInventory(item); ProgressionEvents.RaiseTreasureKept(item, false); }
        CompactorNet.Instance?.RecordLoad(DepotMachineContext.LotOf(this), receipt.bags, receipt.kg, receipt.lost.Count, bulk);
        if (CompactorNet.Instance == null && bulk) ProgressionEvents.RaiseLoadCrushed(receipt.bags, receipt.kg, receipt.lost.Count);
        string lost = receipt.lost.Count == 0 ? "" : receipt.lost.Count == 1 ? $" RIP {receipt.lost[0]} — treasure crushed." : $" {receipt.lost.Count} treasures crushed.";
        string rescued = receipt.rescued.Count > 0 ? $" Grate saved {receipt.rescued.Count} dented treasures." : "";
        string summary = $"Compacted {receipt.kg:0.#} kg: +{Economy.Money(receipt.pay)}. No disposal fees.{lost}{rescued}";
        StartCoroutine(FinishHostCompact(actor, bulk, receipt, summary));
    }

    internal void StartVisualIntake(bool bulk, List<Carryable> stock)
    {
        CompactorFx.BeginSuction(transform, TossTargetPosition, stock, bulk ? CompactorFx.SuctionDuration : 0.55f);
    }

    IEnumerator FinishHostCompact(ulong actor, bool bulk, CompactReceipt receipt, string summary)
    {
        yield return new WaitForSeconds(bulk ? CompactorFx.SuctionDuration : 0.55f);
        int lot = DepotMachineContext.LotOf(this);
        CompactorNet.Instance?.ShowPress(lot, bulk, receipt.kg, receipt.hazards > 0);
        yield return PresentationPress(bulk, receipt.kg, receipt.hazards > 0, () =>
        {
            if (bulk) { CompactorCube.FinishSuction(this, suctionSequence); suctionSequence = 0; }
            else CompactorCube.Squish(this, CompactorCube.PlayersIn(this, new List<PlayerTells>()));
        });
        Notice(actor, bulk, false, summary);
        Busy = false; SetBusyNetwork(false);
        statsFrame = -1;
    }

    internal IEnumerator PresentationPress(bool bulk, float kg, bool hazardBang, Action impact = null)
    {
        yield return PressSlam(new List<Carryable>(), bulk ? 1.15f : 0.8f, impact);
        if (hazardBang) yield return Boom(1, "Hazard");
        if (kg > 0f)
        {
            PopBale(kg, new List<Color> { SortingKit.SackBlack }, hazardBang);
            if (LocalWatching) SortingKit.CashFloater(TossTargetPosition + Vector3.up * 0.6f, SortingKit.Cents(kg * LazyPerKgHere), 1.2f);
        }
        yield return PressReturn();
    }
}

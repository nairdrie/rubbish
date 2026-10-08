using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One neighbour's ebag listing as every machine in the Town sees it (host-written; <see cref="EbaeNeighbours"/>).</summary>
public struct NeighbourListing : INetworkSerializable, IEquatable<NeighbourListing>
{
    public const byte FlagSettling = 1;

    /// <summary>The seller's <see cref="EbaeListing.id"/>.</summary>
    public FixedString32Bytes id;
    /// <summary>The seller's business id.</summary>
    public FixedString64Bytes seller;
    public FixedString64Bytes defId;
    public FixedString64Bytes itemName;
    /// <summary>Owner's item UID and publish version, so a seller can confirm this exact copy is live.</summary>
    public FixedString64Bytes itemUid;
    public int publicationVersion;
    public long askingCents, appraisalCents;
    public byte rarity, flags;
    public float condition;

    public string Id => id.ToString();
    public string Seller => seller.ToString();
    public string ItemName => itemName.ToString();
    public float Asking => askingCents / 100f;
    public float Appraisal => appraisalCents / 100f;
    public Rarity Rarity => (Rarity)Mathf.Clamp(rarity, 0, (int)Rarity.Legendary);
    public bool Settling => (flags & FlagSettling) != 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref seller);
        s.SerializeValue(ref defId);
        s.SerializeValue(ref itemName);
        s.SerializeValue(ref itemUid);
        s.SerializeValue(ref publicationVersion);
        s.SerializeValue(ref askingCents);
        s.SerializeValue(ref appraisalCents);
        s.SerializeValue(ref rarity);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref condition);
    }

    public bool Equals(NeighbourListing o) =>
        id.Equals(o.id) && seller.Equals(o.seller) && defId.Equals(o.defId) && itemName.Equals(o.itemName) &&
        itemUid.Equals(o.itemUid) && publicationVersion == o.publicationVersion &&
        askingCents == o.askingCents && appraisalCents == o.appraisalCents && rarity == o.rarity && flags == o.flags &&
        condition.Equals(o.condition);

    public override bool Equals(object obj) => obj is NeighbourListing o && Equals(o);
    public override int GetHashCode() => id.GetHashCode();
}

/// <summary>A player's offer on a neighbour's listing: cash, an item from their keep crate, or both (host-written).</summary>
public struct NeighbourOffer : INetworkSerializable, IEquatable<NeighbourOffer>
{
    public const byte TurnSeller = 0, TurnBuyer = 1;

    public int id;
    public FixedString32Bytes listingId;
    public FixedString64Bytes seller, buyer;
    /// <summary>Cash on the table now (the buyer pays it; the seller gets it minus ebag's fee).</summary>
    public long cashCents;
    /// <summary>The buyer's own last amount (a buyer counter must beat it).</summary>
    public long buyerCents;
    public FixedString32Bytes tradeUid;
    public FixedString64Bytes tradeDefId, tradeName;
    public byte tradeRarity;
    public long tradeAppraisalCents;
    /// <summary>Who answers next: <see cref="TurnSeller"/> (a new offer or the buyer's counter) or <see cref="TurnBuyer"/>.</summary>
    public byte turn;
    /// <summary>Counters so far (both sides; capped at <see cref="EbaeNeighbours.MaxCounterRounds"/>).</summary>
    public byte rounds;
    /// <summary>1 while the host moves the item and the cash.</summary>
    public byte settling;
    /// <summary>Server time (s) the offer runs out.</summary>
    public double expiresAt;
    public float lifetime;

    public string ListingId => listingId.ToString();
    public string Seller => seller.ToString();
    public string Buyer => buyer.ToString();
    public bool HasTrade => tradeUid.Length > 0;
    public string TradeUid => tradeUid.ToString();
    public string TradeName => tradeName.ToString();
    public Rarity TradeRarity => (Rarity)Mathf.Clamp(tradeRarity, 0, (int)Rarity.Legendary);
    public float Cash => cashCents / 100f;
    public bool Settling => settling != 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref listingId);
        s.SerializeValue(ref seller);
        s.SerializeValue(ref buyer);
        s.SerializeValue(ref cashCents);
        s.SerializeValue(ref buyerCents);
        s.SerializeValue(ref tradeUid);
        s.SerializeValue(ref tradeDefId);
        s.SerializeValue(ref tradeName);
        s.SerializeValue(ref tradeRarity);
        s.SerializeValue(ref tradeAppraisalCents);
        s.SerializeValue(ref turn);
        s.SerializeValue(ref rounds);
        s.SerializeValue(ref settling);
        s.SerializeValue(ref expiresAt);
        s.SerializeValue(ref lifetime);
    }

    public bool Equals(NeighbourOffer o) =>
        id == o.id && listingId.Equals(o.listingId) && seller.Equals(o.seller) && buyer.Equals(o.buyer) && cashCents == o.cashCents &&
        buyerCents == o.buyerCents && tradeUid.Equals(o.tradeUid) && tradeDefId.Equals(o.tradeDefId) && tradeName.Equals(o.tradeName) &&
        tradeRarity == o.tradeRarity && tradeAppraisalCents == o.tradeAppraisalCents && turn == o.turn && rounds == o.rounds &&
        settling == o.settling && expiresAt.Equals(o.expiresAt) && lifetime.Equals(o.lifetime);

    public override bool Equals(object obj) => obj is NeighbourOffer o && Equals(o);
    public override int GetHashCode() => id;
}

/// <summary>
/// ebag between players (Docs/Multiplayer.md §13.8, package N5), on <c>NetSession.prefab</c>.
/// <list type="bullet">
/// <item><b>Listings.</b> Every player's ebag listings live in their own business (<see cref="EbaeMarket"/>, saved with it). In a
/// Town each owner publishes them to the host, which shows them to everyone (<see cref="Listings"/>, the laptop's Neighbours
/// tab). A business that leaves takes its listings home; they come back when it rejoins and publishes again.</item>
/// <item><b>Buying.</b> <see cref="RequestBuyNow"/> at the asking price, or <see cref="RequestOffer"/>: cash, an item from the
/// buyer's keep crate, or both. The seller answers Accept / Counter / Decline (<see cref="Respond"/>) with the NPC rules: offers
/// live 60–90 s, a counter has to beat the offer and can't go over asking, at most <see cref="MaxCounterRounds"/> counters,
/// <see cref="EbaeMarket.MaxOffersPerListing"/> offers per listing, ebag's fee (the seller's own) comes off the cash.</item>
/// <item><b>Settling (host).</b> The host locks the listing, asks the seller (and the buyer, for a traded item) to hold the
/// item(s), then in one frame takes the cash from the buyer and pays the seller through <see cref="Ledger"/> and tells both
/// owners to swap the items. If anything is missing (item gone, buyer can't pay, someone left) nothing moves and the holds are
/// released. Two buyers racing for one listing: the first request locks it, the second is told it's gone.</item>
/// <item><b>Trust.</b> Every guest message is rate-limited (<see cref="SessionService.SetGuard"/>); prices come from the host's
/// copy of the listing, never from the buyer; a guest can only publish its own business's listings and only answer offers it
/// is a party to.</item>
/// </list>
/// </summary>
[NetSessionComponent(Order = 30)]
public partial class EbaeNeighbours : NetworkBehaviour
{
    public static EbaeNeighbours Instance { get; private set; }

    /// <summary>Listings or offers changed (any machine).</summary>
    public static event Action Changed;
    /// <summary>A neighbour-trade message for this player (text, style): offers, counters, sales, cancellations.</summary>
    public static event Action<string, ToastStyle> Notice;

    public const int MaxListingsPerBusiness = EbaeTradingRules.MaxActiveListings;
    public const int MaxOpenOffersPerBuyer = 6;
    public const int MaxCounterRounds = 2;
    public const int MaxItemJson = 3500;
    const int MaxListingIdBytes = 29; // FixedString32Bytes reserves length and terminator bytes
    const int MaxItemUidBytes = 60; // FixedString64Bytes carries the item through NPC and P2P settlement
    public const long MaxPriceCents = EbaeTradingRules.MaxPriceCents; // $50,000
    const double SettleTimeout = 10.0;
    const string MsgPublish = "rb.ebae.publish", MsgTrade = "rb.ebae.trade", MsgHold = "rb.ebae.hold";

    public const byte ActAccept = 0, ActCounter = 1, ActDecline = 2;

    NetworkList<NeighbourListing> listings;
    NetworkList<NeighbourOffer> offers;

    public NetworkList<NeighbourListing> Listings => listings;
    public NetworkList<NeighbourOffer> Offers => offers;

    void Awake()
    {
        listings = new NetworkList<NeighbourListing>();
        offers = new NetworkList<NeighbourOffer>();
    }

    // ------------------------------------------------------------------ lifecycle

    bool hooked;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        listings.OnListChanged += OnListingsChanged;
        offers.OnListChanged += OnOffersChanged;
        EbaeMarket.Changed += MarkOwnDirty;
        Businesses.Changed += MarkOwnDirty;
        EbaeMarket.IsReserved = IsReservedLocally;
        hooked = true;
        ownPublications.Clear();
        ownWithdrawals.Clear();
        hostPublicationVersions.Clear();
        nextPublicationVersion = 1;
        nextPublicationWake = float.PositiveInfinity;
        syncedBusinessId = null;
        holds.Clear();
        pendingReserve.Clear();
        pendingReserveUntil.Clear();
        ownDirty = true;
        if (IsServer)
        {
            SessionService.SetGuard(MsgPublish, new NetGuard(4f, 30, 4400));
            SessionService.SetGuard(MsgTrade, new NetGuard(2f, 8, 4400));
            SessionService.SetGuard(MsgHold, new NetGuard(10f, 30, 64));
            SessionService.PlayerLeft += OnPlayerLeft;
            Businesses.Changed += QueuePrune;
        }
        InitNpc();
        RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        Unhook();
        if (Instance == this) Instance = null;
        RaiseChanged();
    }

    public override void OnDestroy()
    {
        Unhook();
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void Unhook()
    {
        if (!hooked) return;
        hooked = false;
        StopNpc();
        listings.OnListChanged -= OnListingsChanged;
        offers.OnListChanged -= OnOffersChanged;
        EbaeMarket.Changed -= MarkOwnDirty;
        Businesses.Changed -= MarkOwnDirty;
        if (EbaeMarket.IsReserved == (Func<string, bool>)IsReservedLocally) EbaeMarket.IsReserved = null;
        SessionService.PlayerLeft -= OnPlayerLeft;
        Businesses.Changed -= QueuePrune;
        // The session ended mid-trade: nothing moved, so every hold on this machine is released.
        holds.Clear();
        ownPublications.Clear();
        ownWithdrawals.Clear();
        hostPublicationVersions.Clear();
        nextPublicationWake = float.PositiveInfinity;
        syncedBusinessId = null;
        pendingReserve.Clear();
        pendingReserveUntil.Clear();
        EbaeMarket.ClearNeighbourHolds();
    }

    void OnListingsChanged(NetworkListEvent<NeighbourListing> e) { MarkOwnDirty(); RaiseChanged(); }
    void OnOffersChanged(NetworkListEvent<NeighbourOffer> e) => RaiseChanged();

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (ownDirty || Time.realtimeSinceStartup >= nextPublicationWake) OwnerSync();
        if (IsServer) HostTick();
        TickNpc();
        if (pendingReserve.Count > 0)
        {
            float now = Time.realtimeSinceStartup;
            pendingReserve.RemoveWhere(k => pendingReserveUntil.TryGetValue(k, out float t) && now > t);
        }
    }

    // ------------------------------------------------------------------ queries (every machine)

    /// <summary>A neighbour Town is running (the laptop shows the Neighbours tab).</summary>
    public static bool Active => Instance != null && Instance.IsSpawned;

    public double Now => NetworkManager != null ? NetworkManager.ServerTime.Time : 0.0;

    /// <summary>Seconds left on an offer (server clock).</summary>
    public float TimeLeft(in NeighbourOffer o) => Mathf.Max(0f, (float)(o.expiresAt - Now));

    public float TimeLeft01(in NeighbourOffer o) => o.lifetime <= 0f ? 0f : Mathf.Clamp01(TimeLeft(o) / o.lifetime);

    public bool TryGetListing(string seller, string listingId, out NeighbourListing l)
    {
        for (int i = 0; i < listings.Count; i++)
            if (listings[i].Id == listingId && listings[i].Seller == seller) { l = listings[i]; return true; }
        l = default;
        return false;
    }

    public bool TryGetOffer(int offerId, out NeighbourOffer o)
    {
        for (int i = 0; i < offers.Count; i++) if (offers[i].id == offerId) { o = offers[i]; return true; }
        o = default;
        return false;
    }

    /// <summary>Offers that wait for this player's answer (badges).</summary>
    public int WaitingOnMe
    {
        get
        {
            string me = Businesses.LocalId;
            int n = 0;
            for (int i = 0; i < offers.Count; i++)
            {
                var o = offers[i];
                if (o.Settling) continue;
                if ((o.turn == NeighbourOffer.TurnSeller && o.Seller == me) || (o.turn == NeighbourOffer.TurnBuyer && o.Buyer == me)) n++;
            }
            return n;
        }
    }

    /// <summary>"Nick's Rubbish Co." for a business id.</summary>
    public static string ShopName(string businessId)
    {
        if (!Businesses.TryGet(businessId, out var b)) return "a neighbour";
        string owner = b.OwnerLine;
        return string.IsNullOrEmpty(owner) ? b.Name : owner + " " + b.Name;
    }

    /// <summary>"Nick" (the owner's name) for a business id.</summary>
    public static string OwnerName(string businessId)
    {
        if (!Businesses.TryGet(businessId, out var b)) return "a neighbour";
        return string.IsNullOrEmpty(b.OwnerName) ? b.Name : b.OwnerName;
    }

    public static Color32 Tint(string businessId) => Businesses.TryGet(businessId, out var b) ? b.Tint : new Color32(128, 128, 128, 255);

    /// <summary>The fee the seller pays on a sale (1–8%: theirs, with ebag Power Seller).</summary>
    public static float ClampFee(float fee) => float.IsNaN(fee) ? Economy.EbaeFeeFraction : Mathf.Clamp(fee, 0.01f, Economy.EbaeFeeFraction);

    // ------------------------------------------------------------------ requests (any player)

    /// <summary>Buy a neighbour's listing at its asking price (the host checks the price and your cash).</summary>
    public void RequestBuyNow(string seller, string listingId)
    {
        if (!IsSpawned) return;
        Audit("ebae-req", ("op", "buynow"), ("seller", seller), ("listing", listingId));
        if (IsServer) HostBuyNow(NetworkManager.LocalClientId, seller, listingId);
        else BuyNowRpc(F64(seller), F32(listingId));
    }

    /// <summary>Offer cash and/or an item from your keep crate (inventory uid) on a neighbour's listing.</summary>
    public bool RequestOffer(string seller, string listingId, long cashCents, string tradeUid)
    {
        if (!IsSpawned) return false;
        string json = "";
        if (!string.IsNullOrEmpty(tradeUid))
        {
            var item = FindInventory(tradeUid);
            if (item == null || EbaeMarket.IsReservedUid(tradeUid)) return false;
            json = JsonUtility.ToJson(item);
            if (json.Length > MaxItemJson) return false;
            pendingReserve.Add(tradeUid);
            pendingReserveUntil[tradeUid] = Time.realtimeSinceStartup + 6f;
            EbaeMarket.Notify();
        }
        Audit("ebae-req", ("op", "offer"), ("seller", seller), ("listing", listingId), ("cash", cashCents), ("trade", tradeUid ?? ""));
        if (IsServer) HostOffer(NetworkManager.LocalClientId, seller, listingId, cashCents, json);
        else OfferRpc(F64(seller), F32(listingId), cashCents, F4096(json));
        return true;
    }

    /// <summary>Answer an offer you're a party to: accept, counter (with a new cash amount) or decline / withdraw.</summary>
    public void Respond(int offerId, byte action, long counterCents = 0)
    {
        if (!IsSpawned) return;
        Audit("ebae-req", ("op", ActName(action)), ("offer", offerId), ("counter", counterCents));
        if (IsServer) HostRespond(NetworkManager.LocalClientId, offerId, action, counterCents);
        else RespondRpc(offerId, action, counterCents);
    }

    static string ActName(byte a) => a == ActAccept ? "accept" : a == ActCounter ? "counter" : "decline";

    // ------------------------------------------------------------------ owner side (every machine, for its own business)

    sealed class OwnPublication
    {
        public string itemUid, snapshotJson, rejection;
        public long asking;
        public float fee;
        public int version, attempts;
        public bool sent, accepted, retryable, localRejected;
        public float nextRetry;
    }

    sealed class OwnWithdrawal
    {
        public int version, attempts;
        public float nextRetry;
    }

    readonly Dictionary<string, OwnPublication> ownPublications = new Dictionary<string, OwnPublication>(StringComparer.Ordinal);
    readonly Dictionary<string, OwnWithdrawal> ownWithdrawals = new Dictionary<string, OwnWithdrawal>(StringComparer.Ordinal);
    readonly Dictionary<int, Hold> holds = new Dictionary<int, Hold>();
    readonly HashSet<string> pendingReserve = new HashSet<string>(StringComparer.Ordinal);
    readonly Dictionary<string, float> pendingReserveUntil = new Dictionary<string, float>(StringComparer.Ordinal);
    bool ownDirty;
    float nextPublicationWake = float.PositiveInfinity;
    int nextPublicationVersion = 1;
    string syncedBusinessId;

    struct Hold { public string listingId, tradeUid; }

    void MarkOwnDirty() => ownDirty = true;

    /// <summary>What the seller can honestly say about this exact saved listing right now.</summary>
    public EbaePublicationStatus PublicationStatus(string listingId)
    {
        if (string.IsNullOrEmpty(listingId))
            return new EbaePublicationStatus(EbaePublicationState.Rejected, "Listing ID is missing; delist and list again.");
        var listing = EbaeMarket.FindListingById(listingId);
        if (listing == null || listing.item == null) return new EbaePublicationStatus(EbaePublicationState.Rejected, "Listing item is missing.");
        if (Utf8Length(listingId) > MaxListingIdBytes || string.IsNullOrEmpty(listing.item.uid) ||
            Utf8Length(listing.item.uid) > MaxItemUidBytes)
            return new EbaePublicationStatus(EbaePublicationState.Rejected, "Listing or item ID is invalid; delist and list again.");
        if (!EbaeItemValidation.IsValid(listing.item, out string itemReason))
            return new EbaePublicationStatus(EbaePublicationState.Rejected, itemReason);
        if (!TryAskingCents(listing.askingPrice, out long cents))
            return new EbaePublicationStatus(EbaePublicationState.Rejected, "Asking price is invalid; reprice this listing.");
        string priceError = EbaeTradingRules.PriceError(cents);
        if (priceError != null) return new EbaePublicationStatus(EbaePublicationState.Rejected, priceError);
        string currentJson = JsonUtility.ToJson(listing.item);
        if (Utf8Length(currentJson) > MaxItemJson)
            return new EbaePublicationStatus(EbaePublicationState.Rejected, "Item data is too large to publish.");
        float currentFee = ClampFee(UpgradeEffects.EbaeFee);
        if (!IsSpawned) return new EbaePublicationStatus(EbaePublicationState.Publishing, "Joining the Town…");
        if (!ownPublications.TryGetValue(listingId, out var p) || p.asking != cents || p.itemUid != listing.item.uid ||
            p.snapshotJson != currentJson || !Mathf.Approximately(p.fee, currentFee))
            return new EbaePublicationStatus(EbaePublicationState.Publishing, "Sending to neighbours…");
        if (!string.IsNullOrEmpty(p.rejection))
            return new EbaePublicationStatus(EbaePublicationState.Rejected, p.rejection);
        if (p.accepted && ExactOwnListing(listingId, p))
            return new EbaePublicationStatus(EbaePublicationState.Live);
        return new EbaePublicationStatus(EbaePublicationState.Publishing, p.accepted ? "Waiting for Town update…" : "Sending to neighbours…");
    }

    /// <summary>Retry a rejected or timed-out publication without changing the saved item.</summary>
    public void RetryPublication(string listingId)
    {
        if (string.IsNullOrEmpty(listingId) || EbaeMarket.FindListingById(listingId) == null) return;
        ownPublications.Remove(listingId);
        ownDirty = true;
        nextPublicationWake = 0f;
        RaiseChanged();
    }

    static bool TryAskingCents(float asking, out long cents)
    {
        cents = 0;
        double raw = (double)asking * 100.0;
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw > long.MaxValue || raw < long.MinValue) return false;
        cents = (long)Math.Round(raw);
        return true;
    }

    static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value ?? "");

    bool ExactOwnListing(string id, OwnPublication p)
    {
        string me = Businesses.LocalId;
        return p.accepted && TryGetListing(me, id, out var live) && live.askingCents == p.asking &&
               live.itemUid.ToString() == p.itemUid && live.publicationVersion == p.version;
    }

    static ItemInstance FindInventory(string uid)
    {
        foreach (var it in GameSession.Inventory) if (it != null && it.uid == uid) return it;
        return null;
    }

    /// <summary>An inventory item this player offered in a live trade (it stays put until the offer ends).</summary>
    bool IsReservedLocally(string uid)
    {
        if (IsNpcInventoryHeld(uid)) return true;
        if (pendingReserve.Contains(uid)) return true;
        if (offers == null) return false;
        string me = Businesses.LocalId;
        for (int i = 0; i < offers.Count; i++)
            if (offers[i].Buyer == me && offers[i].TradeUid == uid) return true;
        return false;
    }

    /// <summary>Publish local listings, and keep requests pending until the host acknowledges their exact version.</summary>
    void OwnerSync()
    {
        ownDirty = false;
        float now = Time.realtimeSinceStartup;
        nextPublicationWake = float.PositiveInfinity;
        string me = Businesses.LocalId;
        if (syncedBusinessId != me)
        {
            // Switching a saved business in a Town must not reuse the last seller's publication results.
            ownPublications.Clear();
            ownWithdrawals.Clear();
            syncedBusinessId = me;
        }
        if (!Businesses.TryGet(me, out var mine) || !mine.IsValid)
        {
            nextPublicationWake = now + 1f; // not declared yet; never spin every frame
            return;
        }
        if (offers != null && pendingReserve.Count > 0)
            for (int i = 0; i < offers.Count; i++) if (offers[i].Buyer == me) pendingReserve.Remove(offers[i].TradeUid);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int eligible = 0;
        bool changed = false;
        float fee = ClampFee(UpgradeEffects.EbaeFee);
        foreach (var l in EbaeMarket.Listings)
        {
            if (l == null || string.IsNullOrEmpty(l.id)) continue;
            seen.Add(l.id);
            long cents = 0;
            string why = l.item == null ? "Listing item is missing." :
                Utf8Length(l.id) > MaxListingIdBytes ? "Listing ID is invalid; delist and list again." :
                string.IsNullOrEmpty(l.item.uid) || Utf8Length(l.item.uid) > MaxItemUidBytes ? "Item ID is invalid; delist and list again." :
                !TryAskingCents(l.askingPrice, out cents) ? "Asking price is invalid; reprice this listing." :
                EbaeTradingRules.PriceError(cents);
            if (why == null) EbaeItemValidation.IsValid(l.item, out why);
            string json = why == null ? JsonUtility.ToJson(l.item) : null;
            if (why == null && Utf8Length(json) > MaxItemJson) why = "Item data is too large to publish.";
            if (why == null && eligible >= MaxListingsPerBusiness) why = $"Only {MaxListingsPerBusiness} listings can be live. Delist one first.";
            if (why != null)
            {
                QueueOwnWithdrawal(me, l.id, now);
                if (!ownPublications.TryGetValue(l.id, out var bad) || bad.rejection != why || !bad.localRejected ||
                    bad.asking != cents || bad.itemUid != l.item?.uid)
                {
                    ownPublications[l.id] = new OwnPublication { asking = cents, itemUid = l.item?.uid, rejection = why, localRejected = true };
                    changed = true;
                }
                continue; // invalid saved listings stay in the save and remain visible in Your Listings
            }
            eligible++;
            ownWithdrawals.Remove(l.id); // a newer publish version supersedes an in-flight withdrawal
            if (!ownPublications.TryGetValue(l.id, out var p) || p.localRejected || p.asking != cents ||
                p.itemUid != l.item.uid || p.snapshotJson != json || !Mathf.Approximately(p.fee, fee))
            {
                p = new OwnPublication { asking = cents, itemUid = l.item.uid, snapshotJson = json, fee = fee,
                    version = nextPublicationVersion++, nextRetry = now };
                ownPublications[l.id] = p;
                changed = true;
            }
            if (ExactOwnListing(l.id, p))
            {
                nextPublicationWake = Mathf.Min(nextPublicationWake, now + 3f); // catch in-place polish/fee changes without per-frame sends
                continue;
            }
            if (l.neighbourHold)
            {
                nextPublicationWake = Mathf.Min(nextPublicationWake, now + 1f);
                continue; // never mutate the host's sale snapshot while the owner has it held
            }
            if (p.rejection != null && !p.retryable) continue;
            if (now < p.nextRetry) { nextPublicationWake = Mathf.Min(nextPublicationWake, p.nextRetry); continue; }
            if (p.accepted) // accepted but not replicated after the timeout: ask again with a fresh version
            {
                p.version = nextPublicationVersion++;
                p.accepted = false;
                p.sent = false;
                p.attempts = 0;
            }
            p.rejection = null;
            p.retryable = false;
            p.sent = true;
            p.attempts++;
            p.nextRetry = now + Mathf.Min(20f, 3f * Mathf.Pow(2f, p.attempts - 1));
            nextPublicationWake = Mathf.Min(nextPublicationWake, p.nextRetry);
            if (IsServer) HostPublish(NetworkManager.LocalClientId, l.id, p.version, cents, json, fee);
            else PublishRpc(F32(l.id), p.version, cents, F4096(json), fee);
            changed = true;
        }
        List<string> gone = null;
        foreach (var id in ownPublications.Keys) if (!seen.Contains(id)) (gone ??= new List<string>()).Add(id);
        if (gone != null) foreach (var id in gone)
        {
            QueueOwnWithdrawal(me, id, now);
            ownPublications.Remove(id);
            changed = true;
        }
        SendDueWithdrawals(now);
        if (changed) RaiseChanged();
    }

    void QueueOwnWithdrawal(string me, string id, float now)
    {
        if (ownWithdrawals.ContainsKey(id)) return;
        bool possiblyLive = ownPublications.TryGetValue(id, out var p) && p.sent || TryGetListing(me, id, out _);
        if (!possiblyLive) return;
        ownWithdrawals[id] = new OwnWithdrawal { version = nextPublicationVersion++, nextRetry = now };
        if (p != null) { p.sent = false; p.accepted = false; }
    }

    void SendDueWithdrawals(float now)
    {
        // A local host answers synchronously and may remove an entry. Snapshot due IDs before sending.
        var due = new List<string>();
        foreach (var pair in ownWithdrawals)
        {
            var w = pair.Value;
            if (now < w.nextRetry) { nextPublicationWake = Mathf.Min(nextPublicationWake, w.nextRetry); continue; }
            due.Add(pair.Key);
        }
        foreach (var id in due)
        {
            if (!ownWithdrawals.TryGetValue(id, out var w)) continue;
            w.attempts++;
            w.nextRetry = now + Mathf.Min(20f, 3f * Mathf.Pow(2f, w.attempts - 1));
            nextPublicationWake = Mathf.Min(nextPublicationWake, w.nextRetry);
            if (IsServer) HostWithdraw(NetworkManager.LocalClientId, id, w.version);
            else WithdrawRpc(F32(id), w.version);
        }
    }

    void OwnerPublishResult(string id, int version, bool accepted, string reason, bool retryable)
    {
        if (!ownPublications.TryGetValue(id, out var p) || p.version != version) return;
        p.accepted = accepted;
        p.rejection = accepted ? null : string.IsNullOrWhiteSpace(reason) ? "The Town rejected this listing." : reason;
        p.retryable = !accepted && retryable;
        p.nextRetry = accepted ? Time.realtimeSinceStartup + 4f : retryable ? Time.realtimeSinceStartup + 4f : float.PositiveInfinity;
        nextPublicationWake = Mathf.Min(nextPublicationWake, p.nextRetry);
        RaiseChanged();
    }

    void OwnerWithdrawResult(string id, int version, bool accepted, bool retryable)
    {
        if (!ownWithdrawals.TryGetValue(id, out var w) || w.version != version) return;
        if (accepted || !retryable) ownWithdrawals.Remove(id);
        else { w.nextRetry = Time.realtimeSinceStartup + 4f; nextPublicationWake = Mathf.Min(nextPublicationWake, w.nextRetry); }
    }

    /// <summary>Owner: the host wants to settle a trade: hold the listing (seller) or the traded item (buyer).</summary>
    bool OwnerHold(int settleId, string listingId, string tradeUid) =>
        OwnerHold(settleId, listingId, tradeUid, -1, null);

    bool OwnerHold(int settleId, string listingId, string tradeUid, long expectedAsking, string expectedItemUid)
    {
        bool ok;
        if (!string.IsNullOrEmpty(listingId))
        {
            var l = EbaeMarket.FindListingById(listingId);
            ok = l != null && !l.neighbourHold;
            if (ok && expectedAsking >= 0)
                ok = l.item != null && l.item.uid == expectedItemUid &&
                     TryAskingCents(l.askingPrice, out long localAsking) && localAsking == expectedAsking;
            if (ok) l.neighbourHold = true;
        }
        else
        {
            var it = FindInventory(tradeUid);
            ok = it != null;
        }
        if (ok) holds[settleId] = new Hold { listingId = listingId, tradeUid = tradeUid };
        Audit("ebae-own", ("op", "hold"), ("settle", settleId), ("listing", listingId ?? ""), ("trade", tradeUid ?? ""), ("ok", ok));
        return ok;
    }

    void OwnerRelease(int settleId)
    {
        npcOwnerHolds.Remove(settleId);
        npcOwnerSales.Remove(settleId);
        if (!holds.TryGetValue(settleId, out var h)) return;
        holds.Remove(settleId);
        if (!string.IsNullOrEmpty(h.listingId))
        {
            var l = EbaeMarket.FindListingById(h.listingId);
            if (l != null) { l.neighbourHold = false; EbaeMarket.Notify(); }
        }
        Audit("ebae-own", ("op", "release"), ("settle", settleId));
    }

    void OwnerCommitSeller(int settleId, string listingId, string buyerName, long cash, long net, string tradeJson)
    {
        holds.Remove(settleId);
        var trade = ParseItem(tradeJson);
        string itemName = EbaeMarket.FindListingById(listingId)?.ItemName ?? "item";
        bool ok = EbaeMarket.CompleteNeighbourSale(listingId, buyerName, cash / 100f, net / 100f, trade, out _);
        Audit("ebae-own", ("op", "sold"), ("settle", settleId), ("listing", listingId), ("ok", ok), ("cash", cash), ("net", net),
              ("got", trade != null ? trade.uid : ""));
        string what = trade != null
            ? $"{buyerName} traded you their {EbaeMarket.NameOf(trade)}" + (cash > 0 ? $" + {Economy.Money(cash / 100f)}" : "") + $" for your {itemName}!"
            : $"{buyerName} bought your {itemName} for {Economy.Money(cash / 100f)}!";
        ShowNotice("ebag: " + what, ToastStyle.Rare);
        Sfx.Play("cash");
    }

    void OwnerCommitBuyer(int settleId, string tradeUid, string itemJson, string sellerName, long cash)
    {
        holds.Remove(settleId);
        if (!string.IsNullOrEmpty(tradeUid)) GameSession.RemoveFromInventory(tradeUid);
        pendingReserve.Remove(tradeUid ?? "");
        var item = ParseItem(itemJson);
        EbaeMarket.ReceiveItem(item);
        Audit("ebae-own", ("op", "bought"), ("settle", settleId), ("item", item != null ? item.uid : ""), ("gave", tradeUid ?? ""), ("cash", cash));
        ShowNotice($"ebag: you got {sellerName}'s {EbaeMarket.NameOf(item)}! it's in your keep crate.", ToastStyle.Good);
        Sfx.Play("cash");
    }

    void ShowNotice(string text, ToastStyle style)
    {
        Audit("ebae-notice", ("text", text));
        try { Notice?.Invoke(text, style); }
        catch (Exception e) { Debug.LogException(e); }
        if (EbaeUI.IsOpen) EbaeUI.Instance.LaptopToast(text.StartsWith("ebag: ") ? text.Substring(6) : text, style == ToastStyle.Bad ? "bad" : "offer");
        else GameHUD.Toast(text, style, 3f);
        Sfx.Play("notify");
    }

    static ItemInstance ParseItem(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var it = JsonUtility.FromJson<ItemInstance>(json);
            return it != null && !string.IsNullOrEmpty(it.uid) ? it : null;
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ host: records

    sealed class HostListing
    {
        public NeighbourListing pub;
        public string json;
        public ulong owner;
        public float fee;
    }

    struct HostPublicationVersion
    {
        public ulong owner;
        public int version;
        public bool withdrawn;
    }

    sealed class Settlement
    {
        public int id, offerId;
        public string key, seller, buyer, listingId, tradeUid, tradeJson;
        public ulong sellerClient, buyerClient;
        public long cash;
        public bool sellerOk, buyerOk, buyNow;
        public double deadline;
    }

    readonly Dictionary<string, HostListing> hostListings = new Dictionary<string, HostListing>(StringComparer.Ordinal);
    readonly Dictionary<string, HostPublicationVersion> hostPublicationVersions = new Dictionary<string, HostPublicationVersion>(StringComparer.Ordinal);
    readonly Dictionary<int, string> tradeJsonByOffer = new Dictionary<int, string>();
    readonly Dictionary<int, Settlement> settlements = new Dictionary<int, Settlement>();
    int nextOfferId = 1, nextSettleId = 1;
    bool pruneQueued;

    static string Key(string seller, string id) => seller + "/" + id;

    int ListingIndex(string seller, string id)
    {
        for (int i = 0; i < listings.Count; i++) if (listings[i].Id == id && listings[i].Seller == seller) return i;
        return -1;
    }

    int OfferIndex(int id)
    {
        for (int i = 0; i < offers.Count; i++) if (offers[i].id == id) return i;
        return -1;
    }

    static bool BizOf(ulong client, out BusinessInfo b) => Businesses.TryGetForClient(client, out b) && b.IsValid;

    ulong ClientOf(string biz) => Businesses.TryGet(biz, out var b) ? b.ownerClientId : ulong.MaxValue;

    long CashOf(string biz)
    {
        if (biz == GameSession.BusinessId) return GameSession.CashCents;
        return Businesses.TryGet(biz, out var b) ? b.cashCents : 0;
    }

    void HostTick()
    {
        double now = Now;
        // offers run out (never while settling)
        for (int i = offers.Count - 1; i >= 0; i--)
        {
            var o = offers[i];
            if (o.Settling || now < o.expiresAt) continue;
            RemoveOffer(i, "expired");
            TellBiz(o.Buyer, $"ebag: your offer on {ShopName(o.Seller)}'s {ListingName(o)} ran out.", ToastStyle.Info);
            TellBiz(o.Seller, $"ebag: {OwnerName(o.Buyer)}'s offer on your {ListingName(o)} ran out.", ToastStyle.Info);
        }
        if (settlements.Count > 0)
        {
            List<Settlement> late = null;
            foreach (var s in settlements.Values) if (now > s.deadline) (late ??= new List<Settlement>()).Add(s);
            if (late != null) foreach (var s in late) Abort(s, "the other side didn't answer");
        }
        if (pruneQueued) { pruneQueued = false; Prune(); }
    }

    string ListingName(in NeighbourOffer o)
    {
        int i = ListingIndex(o.Seller, o.ListingId);
        return i >= 0 ? listings[i].ItemName : "listing";
    }

    void QueuePrune() => pruneQueued = true;

    void OnPlayerLeft(PlayerKey key, ulong clientId)
    {
        if (!IsServer) return;
        Prune();
        pruneQueued = true; // the registry may drop the business after us
    }

    /// <summary>Host: drop the listings, offers and trades of businesses that left the Town (or re-picked).</summary>
    void Prune()
    {
        if (!IsServer || !IsSpawned) return;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Businesses.All) if (b.IsValid) live.Add(b.Id);

        List<Settlement> dead = null;
        foreach (var s in settlements.Values)
            if (!live.Contains(s.seller) || !live.Contains(s.buyer)) (dead ??= new List<Settlement>()).Add(s);
        if (dead != null) foreach (var s in dead) Abort(s, "someone left the Town");

        for (int i = offers.Count - 1; i >= 0; i--)
        {
            var o = offers[i];
            bool sellerGone = !live.Contains(o.Seller), buyerGone = !live.Contains(o.Buyer);
            if (!sellerGone && !buyerGone) continue;
            RemoveOffer(i, sellerGone ? "seller left" : "buyer left");
            if (!buyerGone) TellBiz(o.Buyer, "ebag: your offer was cancelled. the seller went home.", ToastStyle.Bad);
            if (!sellerGone) TellBiz(o.Seller, $"ebag: {OwnerName(o.Buyer)} went home; their offer is off.", ToastStyle.Info);
        }
        for (int i = listings.Count - 1; i >= 0; i--)
        {
            var l = listings[i];
            if (live.Contains(l.Seller)) continue;
            ForgetNpcListing(l.Seller, l.Id);
            hostListings.Remove(Key(l.Seller, l.Id));
            hostPublicationVersions.Remove(Key(l.Seller, l.Id));
            listings.RemoveAt(i);
            Audit("ebae-trade", ("op", "unlist"), ("seller", l.Seller), ("listing", l.Id), ("why", "seller left"));
        }
        // Keep stale RPC versions only while that business remains in this Town. A returning owner
        // starts a fresh client-side sequence, even if the previous visit ended after a sale.
        List<string> oldVersions = null;
        foreach (var key in hostPublicationVersions.Keys)
        {
            int slash = key.IndexOf('/');
            if (slash >= 0 && !live.Contains(key.Substring(0, slash))) (oldVersions ??= new List<string>()).Add(key);
        }
        if (oldVersions != null) foreach (var key in oldVersions) hostPublicationVersions.Remove(key);
    }

    // ------------------------------------------------------------------ host: listings

    void HostPublish(ulong sender, string id, int version, long asking, string json, float fee)
    {
        if (!BizOf(sender, out var biz)) { PublishAnswer(sender, id, version, false, "Business not registered in this Town yet.", true); return; }
        if (string.IsNullOrEmpty(id) || Utf8Length(id) > MaxListingIdBytes || version <= 0)
        {
            PublishAnswer(sender, id, version, false, "Listing ID is invalid; delist and list again.", false);
            return;
        }
        string seller = biz.Id, key = Key(seller, id);
        if (hostPublicationVersions.TryGetValue(key, out var last) && last.owner == sender && version <= last.version)
        {
            bool alreadyLive = version == last.version && !last.withdrawn &&
                hostListings.TryGetValue(key, out var current) && current.pub.publicationVersion == version;
            PublishAnswer(sender, id, version, alreadyLive, alreadyLive ? null : "An older listing update was ignored.", false);
            return;
        }
        string priceError = EbaeTradingRules.PriceError(asking);
        if (priceError != null) { PublishAnswer(sender, id, version, false, priceError, false); return; }
        var item = ParseItem(json);
        if (item == null || Utf8Length(item.uid) > MaxItemUidBytes || Utf8Length(json) > MaxItemJson)
        {
            PublishAnswer(sender, id, version, false, "Item data is invalid or too large to publish.", false);
            return;
        }
        if (!EbaeItemValidation.IsValid(item, out string itemReason))
        {
            PublishAnswer(sender, id, version, false, itemReason, false);
            return;
        }
        bool exists = hostListings.TryGetValue(key, out var h);
        if (exists && h.pub.Settling)
        {
            PublishAnswer(sender, id, version, false, "Sale in progress; listing update will retry.", true);
            return;
        }
        if (exists && h.pub.itemUid.ToString() != item.uid)
        {
            PublishAnswer(sender, id, version, false, "This listing's item changed; delist it first.", false);
            return;
        }
        if (exists && (h.pub.askingCents != asking || h.json != json))
            ForgetNpcListing(seller, id); // an old NPC offer cannot be accepted during a reprice frame
        if (!exists)
        {
            int count = 0;
            foreach (var x in hostListings.Values) if (x.pub.Seller == seller) count++;
            if (count >= MaxListingsPerBusiness)
            {
                PublishAnswer(sender, id, version, false, $"Only {MaxListingsPerBusiness} listings can be live. Delist one first.", true);
                return;
            }
            h = new HostListing();
            hostListings[key] = h;
        }
        var def = item.Def;
        var pub = new NeighbourListing
        {
            askingCents = asking,
            appraisalCents = (long)Math.Round(EbaeMarket.Appraise(item) * 100.0),
            rarity = (byte)(def != null ? def.rarity : Rarity.Common),
            condition = Mathf.Clamp01(item.condition),
            flags = exists ? h.pub.flags : (byte)0,
            publicationVersion = version,
        };
        pub.id.CopyFromTruncated(id);
        pub.seller.CopyFromTruncated(seller);
        pub.defId.CopyFromTruncated(item.defId ?? "");
        pub.itemName.CopyFromTruncated(EbaeMarket.NameOf(item));
        pub.itemUid.CopyFromTruncated(item.uid);
        h.pub = pub;
        h.json = json;
        h.owner = sender;
        h.fee = ClampFee(fee);
        int i = ListingIndex(seller, id);
        if (i >= 0) { if (!listings[i].Equals(pub)) listings[i] = pub; }
        else listings.Add(pub);
        hostPublicationVersions[key] = new HostPublicationVersion { owner = sender, version = version };
        PublishAnswer(sender, id, version, true, null, false);
        Audit("ebae-trade", ("op", exists ? "reprice" : "list"), ("seller", seller), ("listing", id), ("item", item.uid), ("asking", asking));
    }

    void HostWithdraw(ulong sender, string id, int version)
    {
        if (!BizOf(sender, out var biz)) { WithdrawAnswer(sender, id, version, false, true); return; }
        if (string.IsNullOrEmpty(id) || Utf8Length(id) > MaxListingIdBytes || version <= 0)
        {
            WithdrawAnswer(sender, id, version, false, false);
            return;
        }
        string seller = biz.Id;
        string key = Key(seller, id);
        if (hostPublicationVersions.TryGetValue(key, out var last) && last.owner == sender && version <= last.version)
        {
            WithdrawAnswer(sender, id, version, true, false); // newer owner intent already won, or this is a duplicate
            return;
        }
        if (hostListings.TryGetValue(key, out var current) && current.pub.Settling)
        {
            WithdrawAnswer(sender, id, version, false, true);
            return;
        }
        int i = ListingIndex(seller, id);
        hostPublicationVersions[key] = new HostPublicationVersion { owner = sender, version = version, withdrawn = true };
        ForgetNpcListing(seller, id);
        if (i < 0) { hostListings.Remove(key); WithdrawAnswer(sender, id, version, true, false); return; }
        hostListings.Remove(key);
        listings.RemoveAt(i);
        CancelOffersOn(seller, id, "the seller pulled the listing", -1);
        WithdrawAnswer(sender, id, version, true, false);
        Audit("ebae-trade", ("op", "unlist"), ("seller", seller), ("listing", id), ("why", "withdrawn"));
    }

    void PublishAnswer(ulong sender, string id, int version, bool accepted, string reason, bool retryable)
    {
        if (sender == NetworkManager.LocalClientId) { OwnerPublishResult(id, version, accepted, reason, retryable); return; }
        PublishResultRpc(F32(id), version, accepted, F128(reason), retryable,
            RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    void WithdrawAnswer(ulong sender, string id, int version, bool accepted, bool retryable)
    {
        if (sender == NetworkManager.LocalClientId) { OwnerWithdrawResult(id, version, accepted, retryable); return; }
        WithdrawResultRpc(F32(id), version, accepted, retryable, RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    void CancelOffersOn(string seller, string listingId, string why, int exceptOffer)
    {
        for (int i = offers.Count - 1; i >= 0; i--)
        {
            var o = offers[i];
            if (o.id == exceptOffer || o.Seller != seller || o.ListingId != listingId) continue;
            if (o.Settling) continue; // its settlement finds the listing gone and aborts
            RemoveOffer(i, why);
            TellBiz(o.Buyer, $"ebag: your offer on {ShopName(seller)}'s listing is off: {why}.", ToastStyle.Bad);
        }
    }

    void RemoveOffer(int index, string why)
    {
        var o = offers[index];
        offers.RemoveAt(index);
        tradeJsonByOffer.Remove(o.id);
        Audit("ebae-trade", ("op", "offer-end"), ("offer", o.id), ("why", why), ("seller", o.Seller), ("buyer", o.Buyer), ("listing", o.ListingId));
    }

    // ------------------------------------------------------------------ host: buying

    void HostBuyNow(ulong sender, string seller, string listingId)
    {
        if (!BizOf(sender, out var buyer)) return;
        if (!hostListings.TryGetValue(Key(seller, listingId), out var h))
        {
            Tell(sender, "ebag: too slow! that listing is gone.", ToastStyle.Bad);
            Refused(sender, "buynow", "gone");
            return;
        }
        if (seller == buyer.Id) { Tell(sender, "ebag: you can't buy your own stuff.", ToastStyle.Bad); return; }
        if (h.pub.Settling) { Tell(sender, "ebag: too slow! someone else is buying that.", ToastStyle.Bad); Refused(sender, "buynow", "settling"); return; }
        long cash = h.pub.askingCents;
        if (CashOf(buyer.Id) < cash) { Tell(sender, $"ebag: you need {Economy.Money(cash / 100f)} for that.", ToastStyle.Bad); return; }
        StartSettlement(h, buyer.Id, sender, cash, null, null, -1, buyNow: true);
    }

    void HostOffer(ulong sender, string seller, string listingId, long cash, string tradeJson)
    {
        if (!BizOf(sender, out var buyerBiz)) return;
        string buyer = buyerBiz.Id;
        void Fail(string why) { Tell(sender, "ebag: " + why, ToastStyle.Bad); Refused(sender, "offer", why); }
        if (!hostListings.TryGetValue(Key(seller, listingId), out var h)) { Fail("that listing is gone."); return; }
        if (seller == buyer) { Fail("you can't make an offer on your own stuff."); return; }
        if (h.pub.Settling) { Fail("someone is buying that right now."); return; }
        var trade = ParseItem(tradeJson);
        if (!string.IsNullOrEmpty(tradeJson) && trade == null) { Fail("couldn't read the item you offered."); return; }
        if (cash < 0 || cash > h.pub.askingCents || cash > MaxPriceCents) { Fail("offers go from $0 up to the asking price."); return; }
        if (cash == 0 && trade == null) { Fail("offer some cash or an item."); return; }
        if (CashOf(buyer) < cash) { Fail($"you don't have {Economy.Money(cash / 100f)}."); return; }
        int mine = 0, onListing = 0;
        for (int i = 0; i < offers.Count; i++)
        {
            var o = offers[i];
            if (o.Buyer == buyer) mine++;
            if (o.Seller == seller && o.ListingId == listingId) onListing++;
            if (o.Buyer == buyer && o.Seller == seller && o.ListingId == listingId) { Fail("you already have an offer on that."); return; }
            if (trade != null && o.Buyer == buyer && o.TradeUid == trade.uid) { Fail("that item is already in another offer."); return; }
        }
        if (mine >= MaxOpenOffersPerBuyer) { Fail("you have too many offers out. wait for some answers."); return; }
        if (onListing >= EbaeMarket.MaxOffersPerListing) { Fail("that listing has too many offers already."); return; }

        int id = nextOfferId++;
        float life = LifetimeFor(id);
        var no = new NeighbourOffer
        {
            id = id,
            cashCents = cash,
            buyerCents = cash,
            turn = NeighbourOffer.TurnSeller,
            expiresAt = Now + life,
            lifetime = life,
        };
        no.listingId.CopyFromTruncated(listingId);
        no.seller.CopyFromTruncated(seller);
        no.buyer.CopyFromTruncated(buyer);
        if (trade != null)
        {
            no.tradeUid.CopyFromTruncated(trade.uid);
            no.tradeDefId.CopyFromTruncated(trade.defId ?? "");
            no.tradeName.CopyFromTruncated(EbaeMarket.NameOf(trade));
            no.tradeRarity = (byte)(trade.Def != null ? trade.Def.rarity : Rarity.Common);
            no.tradeAppraisalCents = (long)Math.Round(EbaeMarket.Appraise(trade) * 100.0);
            tradeJsonByOffer[id] = tradeJson;
        }
        offers.Add(no);
        Audit("ebae-trade", ("op", "offer"), ("offer", id), ("seller", seller), ("buyer", buyer), ("listing", listingId), ("cash", cash),
              ("trade", trade != null ? trade.uid : ""));
        string what = Economy.Money(cash / 100f) + (trade != null ? (cash > 0 ? " + " : "") + EbaeMarket.NameOf(trade) : "");
        if (trade != null && cash == 0) what = "their " + EbaeMarket.NameOf(trade);
        TellBiz(seller, $"ebag: {OwnerName(buyer)} (neighbour) offered {what} for your {h.pub.ItemName}!", ToastStyle.Good);
    }

    /// <summary>60–90 s, like an NPC offer (spread by id; no RNG on the host for this).</summary>
    static float LifetimeFor(int id) => EbaeMarket.MinOfferLifetime + (id * 37 % 31) * (EbaeMarket.MaxOfferLifetime - EbaeMarket.MinOfferLifetime) / 30f;

    void HostRespond(ulong sender, int offerId, byte action, long counter)
    {
        if (!BizOf(sender, out var party)) return;
        int i = OfferIndex(offerId);
        if (i < 0) { Tell(sender, "ebag: that offer is gone.", ToastStyle.Bad); return; }
        var o = offers[i];
        bool isSeller = party.Id == o.Seller, isBuyer = party.Id == o.Buyer;
        if (!isSeller && !isBuyer) { Refused(sender, "respond", "not a party"); return; }
        if (o.Settling) { Tell(sender, "ebag: that deal is already going through.", ToastStyle.Info); return; }
        if (Now >= o.expiresAt)
        {
            RemoveOffer(i, "expired");
            Tell(sender, "ebag: that offer has expired.", ToastStyle.Bad);
            return;
        }
        bool myTurn = (o.turn == NeighbourOffer.TurnSeller && isSeller) || (o.turn == NeighbourOffer.TurnBuyer && isBuyer);
        string item = ListingName(o);

        if (action == ActDecline)
        {
            RemoveOffer(i, isSeller ? "declined" : "withdrawn");
            if (isSeller) TellBiz(o.Buyer, $"ebag: {OwnerName(o.Seller)} declined your offer on their {item}.", ToastStyle.Bad);
            else TellBiz(o.Seller, $"ebag: {OwnerName(o.Buyer)} walked away from the deal on your {item}.", ToastStyle.Info);
            return;
        }
        if (!myTurn) { Tell(sender, "ebag: waiting on the other side.", ToastStyle.Info); return; }

        if (action == ActCounter)
        {
            if (o.rounds >= MaxCounterRounds) { Tell(sender, "ebag: no more haggling. accept or decline.", ToastStyle.Bad); return; }
            int li = ListingIndex(o.Seller, o.ListingId);
            long asking = li >= 0 ? listings[li].askingCents : o.cashCents;
            bool ok = isSeller ? counter > o.cashCents && counter <= asking
                               : counter > o.buyerCents && counter < o.cashCents;
            if (!ok)
            {
                Tell(sender, isSeller ? $"ebag: a counter has to beat {Economy.Money(o.Cash)} and can't go over asking ({Economy.Money(asking / 100f)})."
                                      : $"ebag: a counter has to land between {Economy.Money(o.buyerCents / 100f)} and {Economy.Money(o.Cash)}.", ToastStyle.Bad);
                return;
            }
            if (isBuyer && CashOf(o.Buyer) < counter) { Tell(sender, "ebag: you can't afford that.", ToastStyle.Bad); return; }
            o.cashCents = counter;
            if (isBuyer) o.buyerCents = counter;
            o.turn = isSeller ? NeighbourOffer.TurnBuyer : NeighbourOffer.TurnSeller;
            o.rounds++;
            o.lifetime = LifetimeFor(o.id + o.rounds);
            o.expiresAt = Now + o.lifetime;
            offers[i] = o;
            Audit("ebae-trade", ("op", "counter"), ("offer", o.id), ("by", isSeller ? "seller" : "buyer"), ("cash", counter), ("round", o.rounds));
            if (isSeller) TellBiz(o.Buyer, $"ebag: {OwnerName(o.Seller)} countered at {Economy.Money(counter / 100f)} for their {item}.", ToastStyle.Good);
            else TellBiz(o.Seller, $"ebag: {OwnerName(o.Buyer)} came back with {Economy.Money(counter / 100f)} for your {item}.", ToastStyle.Good);
            return;
        }

        // accept
        if (!hostListings.TryGetValue(Key(o.Seller, o.ListingId), out var h)) { RemoveOffer(i, "listing gone"); Tell(sender, "ebag: that listing is gone.", ToastStyle.Bad); return; }
        if (h.pub.Settling) { Tell(sender, "ebag: someone else is buying that right now.", ToastStyle.Bad); return; }
        if (CashOf(o.Buyer) < o.cashCents)
        {
            Tell(sender, isSeller ? $"ebag: {OwnerName(o.Buyer)} can't afford that right now." : "ebag: you can't afford that right now.", ToastStyle.Bad);
            return;
        }
        o.settling = 1;
        offers[i] = o;
        tradeJsonByOffer.TryGetValue(o.id, out string tj);
        StartSettlement(h, o.Buyer, ClientOf(o.Buyer), o.cashCents, o.HasTrade ? o.TradeUid : null, tj, o.id, buyNow: false);
    }

    // ------------------------------------------------------------------ host: settling

    void StartSettlement(HostListing h, string buyer, ulong buyerClient, long cash, string tradeUid, string tradeJson, int offerId, bool buyNow)
    {
        var s = new Settlement
        {
            id = nextSettleId++,
            offerId = offerId,
            key = Key(h.pub.Seller, h.pub.Id),
            seller = h.pub.Seller,
            buyer = buyer,
            listingId = h.pub.Id,
            tradeUid = tradeUid,
            tradeJson = tradeJson,
            sellerClient = h.owner,
            buyerClient = buyerClient,
            cash = cash,
            buyNow = buyNow,
            deadline = Now + SettleTimeout,
        };
        settlements[s.id] = s;
        if (string.IsNullOrEmpty(tradeUid)) s.buyerOk = true; // set before any hold: a host-side hold answers (and commits) at once
        SetListingSettling(s.seller, s.listingId, true);
        Audit("ebae-trade", ("op", "settle-start"), ("settle", s.id), ("offer", offerId), ("seller", s.seller), ("buyer", buyer),
              ("listing", s.listingId), ("cash", cash), ("trade", tradeUid ?? ""), ("buyNow", buyNow));

        if (!string.IsNullOrEmpty(tradeUid)) SendHold(s.buyerClient, s.id, null, tradeUid);
        if (settlements.ContainsKey(s.id)) SendHold(s.sellerClient, s.id, s.listingId, null);
    }

    void SetListingSettling(string seller, string listingId, bool on)
    {
        if (hostListings.TryGetValue(Key(seller, listingId), out var h))
            h.pub.flags = on ? (byte)(h.pub.flags | NeighbourListing.FlagSettling) : (byte)(h.pub.flags & ~NeighbourListing.FlagSettling);
        int i = ListingIndex(seller, listingId);
        if (i < 0) return;
        var l = listings[i];
        l.flags = on ? (byte)(l.flags | NeighbourListing.FlagSettling) : (byte)(l.flags & ~NeighbourListing.FlagSettling);
        listings[i] = l;
    }

    void SendHold(ulong client, int settleId, string listingId, string tradeUid)
    {
        long expectedAsking = -1;
        string expectedItemUid = null;
        if (!string.IsNullOrEmpty(listingId))
        {
            if (!settlements.TryGetValue(settleId, out var s) || !hostListings.TryGetValue(s.key, out var h))
            {
                HostHoldResult(client, settleId, false);
                return;
            }
            expectedAsking = h.pub.askingCents;
            expectedItemUid = h.pub.itemUid.ToString();
        }
        if (client == NetworkManager.LocalClientId)
        {
            HostHoldResult(client, settleId, OwnerHold(settleId, listingId, tradeUid, expectedAsking, expectedItemUid));
            return;
        }
        HoldRpc(settleId, F32(listingId), F32(tradeUid), expectedAsking, F64(expectedItemUid),
            RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    void HostHoldResult(ulong sender, int settleId, bool ok)
    {
        if (!settlements.TryGetValue(settleId, out var s)) { if (ok) SendRelease(sender, settleId); return; }
        bool fromSeller = sender == s.sellerClient, fromBuyer = sender == s.buyerClient && !string.IsNullOrEmpty(s.tradeUid);
        if (!fromSeller && !fromBuyer) { Refused(sender, "hold", "not a party"); return; }
        if (!ok) { Abort(s, fromSeller ? "the item isn't for sale any more" : "the traded item isn't there any more"); return; }
        if (fromSeller) s.sellerOk = true;
        if (fromBuyer) s.buyerOk = true;
        if (s.sellerOk && s.buyerOk) Commit(s);
    }

    void SendRelease(ulong client, int settleId)
    {
        if (client == NetworkManager.LocalClientId) { OwnerRelease(settleId); return; }
        if (client == ulong.MaxValue) return;
        ReleaseRpc(settleId, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    void Abort(Settlement s, string why)
    {
        if (!settlements.Remove(s.id)) return;
        if (s.sellerOk) SendRelease(s.sellerClient, s.id);
        if (s.buyerOk && !string.IsNullOrEmpty(s.tradeUid)) SendRelease(s.buyerClient, s.id);
        SetListingSettling(s.seller, s.listingId, false);
        int oi = s.offerId >= 0 ? OfferIndex(s.offerId) : -1;
        if (oi >= 0) RemoveOffer(oi, "fell through: " + why);
        Audit("ebae-trade", ("op", "settle-abort"), ("settle", s.id), ("offer", s.offerId), ("why", why), ("seller", s.seller), ("buyer", s.buyer));
        TellBiz(s.buyer, $"ebag: the deal fell through ({why}). nothing changed hands.", ToastStyle.Bad);
        if (!s.buyNow) TellBiz(s.seller, $"ebag: the deal fell through ({why}). nothing changed hands.", ToastStyle.Bad);
    }

    /// <summary>Host, one frame: re-check, take the cash, pay the seller, swap the items. Never half a trade.</summary>
    void Commit(Settlement s)
    {
        if (!hostListings.TryGetValue(s.key, out var h)) { Abort(s, "the listing is gone"); return; }
        if (!Businesses.TryGet(s.seller, out var sellerBiz) || !Businesses.TryGet(s.buyer, out var buyerBiz)) { Abort(s, "someone left the Town"); return; }
        if (sellerBiz.ownerClientId != s.sellerClient || buyerBiz.ownerClientId != s.buyerClient) { Abort(s, "someone rejoined"); return; }
        if (!string.IsNullOrEmpty(s.tradeUid) && ParseItem(s.tradeJson) == null) { Abort(s, "couldn't read the traded item"); return; }

        string itemName = h.pub.ItemName, buyerName = OwnerName(s.buyer), sellerName = OwnerName(s.seller);
        long net = 0;
        if (s.cash > 0)
        {
            bool paid;
            using (Ledger.ToBusiness(s.buyer))
                paid = Ledger.Spend(s.cash / 100f, $"ebag: {itemName} from {sellerName}", LedgerIds.Ebae);
            if (!paid) { Abort(s, $"{buyerName} can't afford it"); return; }
            net = (long)Math.Round(s.cash * (1.0 - h.fee));
            if (net > 0)
                using (Ledger.ToBusiness(s.seller))
                    Ledger.Credit(net / 100f, $"ebag sale to {buyerName}", LedgerIds.Ebae);
        }
        settlements.Remove(s.id);

        // the items: seller's listing → buyer's keep crate; buyer's traded item → seller's keep crate
        SendCommitSeller(s, buyerName, net);
        SendCommitBuyer(s, h.json, sellerName);

        ForgetNpcListing(s.seller, s.listingId);
        hostListings.Remove(s.key);
        int li = ListingIndex(s.seller, s.listingId);
        if (li >= 0) listings.RemoveAt(li);
        int oi = s.offerId >= 0 ? OfferIndex(s.offerId) : -1;
        if (oi >= 0) RemoveOffer(oi, "accepted");
        CancelOffersOn(s.seller, s.listingId, "it sold to someone else", s.offerId);

        var item = ParseItem(h.json);
        Audit("ebae-trade", ("op", "settled"), ("settle", s.id), ("offer", s.offerId), ("buyNow", s.buyNow), ("seller", s.seller), ("buyer", s.buyer),
              ("listing", s.listingId), ("item", item != null ? item.uid : ""), ("trade", s.tradeUid ?? ""), ("cash", s.cash), ("net", net),
              ("fee", s.cash - net), ("sellerCash", CashOf(s.seller)), ("buyerCash", CashOf(s.buyer)));
    }

    void SendCommitSeller(Settlement s, string buyerName, long net)
    {
        string tradeJson = s.tradeJson ?? "";
        if (s.sellerClient == NetworkManager.LocalClientId) { OwnerCommitSeller(s.id, s.listingId, buyerName, s.cash, net, tradeJson); return; }
        CommitSellerRpc(s.id, F32(s.listingId), F64(buyerName), s.cash, net, F4096(tradeJson), RpcTarget.Single(s.sellerClient, RpcTargetUse.Temp));
    }

    void SendCommitBuyer(Settlement s, string itemJson, string sellerName)
    {
        if (s.buyerClient == NetworkManager.LocalClientId) { OwnerCommitBuyer(s.id, s.tradeUid, itemJson, sellerName, s.cash); return; }
        CommitBuyerRpc(s.id, F32(s.tradeUid), F4096(itemJson), F64(sellerName), s.cash, RpcTarget.Single(s.buyerClient, RpcTargetUse.Temp));
    }

    // ------------------------------------------------------------------ host: messages to players

    void TellBiz(string biz, string text, ToastStyle style)
    {
        ulong c = ClientOf(biz);
        if (c != ulong.MaxValue) Tell(c, text, style);
    }

    void Tell(ulong client, string text, ToastStyle style)
    {
        if (client == NetworkManager.LocalClientId) { ShowNotice(text, style); return; }
        var t = new FixedString128Bytes();
        t.CopyFromTruncated(text ?? "");
        NoticeRpc(t, (byte)style, RpcTarget.Single(client, RpcTargetUse.Temp));
    }

    void Refused(ulong sender, string what, string why) =>
        Audit("ebae-refused", ("client", sender), ("what", what), ("why", why));

    // ------------------------------------------------------------------ RPCs: guest → host

    bool Allowed(string msg, ulong sender, int bytes)
    {
        if (SessionService.GuardFor(msg).Allow(sender, bytes, Time.realtimeSinceStartupAsDouble)) return true;
        NetAudit.Event("guard", $"{msg} from client {sender} over its limit");
        return false;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PublishRpc(FixedString32Bytes id, int version, long asking, FixedString4096Bytes json, float fee, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgPublish, s, json.Length + 52))
        {
            PublishAnswer(s, id.ToString(), version, false, "Too many ebag updates; retrying shortly.", true);
            return;
        }
        HostPublish(s, id.ToString(), version, asking, json.ToString(), fee);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void WithdrawRpc(FixedString32Bytes id, int version, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgPublish, s, 36)) { WithdrawAnswer(s, id.ToString(), version, false, true); return; }
        HostWithdraw(s, id.ToString(), version);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BuyNowRpc(FixedString64Bytes seller, FixedString32Bytes listingId, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgTrade, s, 96)) return;
        HostBuyNow(s, seller.ToString(), listingId.ToString());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void OfferRpc(FixedString64Bytes seller, FixedString32Bytes listingId, long cash, FixedString4096Bytes tradeJson, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgTrade, s, tradeJson.Length + 104)) return;
        HostOffer(s, seller.ToString(), listingId.ToString(), cash, tradeJson.ToString());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RespondRpc(int offerId, byte action, long counter, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgTrade, s, 16)) return;
        HostRespond(s, offerId, action, counter);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void HoldResultRpc(int settleId, bool ok, RpcParams p = default)
    {
        ulong s = p.Receive.SenderClientId;
        if (!Allowed(MsgHold, s, 8)) return;
        HostHoldResult(s, settleId, ok);
    }

    // ------------------------------------------------------------------ RPCs: host → one owner

    [Rpc(SendTo.SpecifiedInParams)]
    void PublishResultRpc(FixedString32Bytes id, int version, bool accepted, FixedString128Bytes reason, bool retryable, RpcParams p)
    {
        if (IsServer) return;
        OwnerPublishResult(id.ToString(), version, accepted, reason.ToString(), retryable);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void WithdrawResultRpc(FixedString32Bytes id, int version, bool accepted, bool retryable, RpcParams p)
    {
        if (IsServer) return;
        OwnerWithdrawResult(id.ToString(), version, accepted, retryable);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void HoldRpc(int settleId, FixedString32Bytes listingId, FixedString32Bytes tradeUid,
        long expectedAsking, FixedString64Bytes expectedItemUid, RpcParams p)
    {
        if (IsServer) return;
        bool ok = OwnerHold(settleId, Str(listingId), Str(tradeUid), expectedAsking, expectedItemUid.ToString());
        HoldResultRpc(settleId, ok);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ReleaseRpc(int settleId, RpcParams p)
    {
        if (IsServer) return;
        OwnerRelease(settleId);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void CommitSellerRpc(int settleId, FixedString32Bytes listingId, FixedString64Bytes buyerName, long cash, long net, FixedString4096Bytes tradeJson, RpcParams p)
    {
        if (IsServer) return;
        OwnerCommitSeller(settleId, listingId.ToString(), buyerName.ToString(), cash, net, tradeJson.ToString());
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void CommitBuyerRpc(int settleId, FixedString32Bytes tradeUid, FixedString4096Bytes itemJson, FixedString64Bytes sellerName, long cash, RpcParams p)
    {
        if (IsServer) return;
        OwnerCommitBuyer(settleId, Str(tradeUid), itemJson.ToString(), sellerName.ToString(), cash);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void NoticeRpc(FixedString128Bytes text, byte style, RpcParams p)
    {
        if (IsServer) return;
        ShowNotice(text.ToString(), (ToastStyle)Mathf.Clamp(style, 0, (int)ToastStyle.Legendary));
    }

    // ------------------------------------------------------------------ helpers

    static FixedString32Bytes F32(string s)
    {
        var f = new FixedString32Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    static FixedString64Bytes F64(string s)
    {
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    static FixedString4096Bytes F4096(string s)
    {
        var f = new FixedString4096Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    static string Str(FixedString32Bytes f) => f.Length == 0 ? null : f.ToString();

    static void Audit(string kind, params (string k, object v)[] fields)
    {
        var d = new Dictionary<string, object>(fields.Length);
        foreach (var (k, v) in fields) d[k] = v;
        NetAudit.Event(kind, d);
    }

    /// <summary>Every machine: this business's inventory and listing uids, for the bot checker (item conservation).</summary>
    public static void AuditItems(string why)
    {
        var inv = new List<string>();
        foreach (var it in GameSession.Inventory) if (it != null) inv.Add(it.uid);
        var lst = new List<string>();
        foreach (var l in EbaeMarket.Listings) if (l?.item != null) lst.Add(l.item.uid);
        Audit("ebae-items", ("why", why), ("business", Businesses.LocalId), ("inventory", string.Join(",", inv)), ("listed", string.Join(",", lst)),
              ("cash", GameSession.CashCents));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Changed = null;
        Notice = null;
    }
}

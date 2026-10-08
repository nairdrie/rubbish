using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>Guest NPC buyers run on the host, against accepted publications. Their sales share the
/// neighbour listing lock; only a confirmed sale removes the owner's item and records its history.</summary>
public partial class EbaeNeighbours
{
    const string MsgNpc = "rb.ebae.npc", MsgNpcSettings = "rb.ebae.npc-settings";
    const string NpcCashPrefix = "ebag NPC sale #";
    public static bool UsesHostNpc => Active && !Instance.IsServer;

    sealed class NpcMarket
    {
        public HostListing published;
        public long asking;
        public string json;
        public EbaeListing listing;
    }

    struct NpcSettings
    {
        public float speed, monocle, autoAccept;
        public int maxOffers;
    }

    sealed class NpcSalePending
    {
        public int id;
        public string business, listingId, uid, buyer, offerId, itemJson;
        public ulong owner;
        public long gross, net;
        public bool pawn;
        public double deadline;
        public HostListing listing;
    }

    [Serializable] sealed class NpcOfferSnapshot
    {
        public int received;
        public List<EbaeOffer> offers = new List<EbaeOffer>();
    }

    readonly Dictionary<string, NpcMarket> npcMarkets = new Dictionary<string, NpcMarket>();
    readonly Dictionary<ulong, NpcSettings> npcSettings = new Dictionary<ulong, NpcSettings>();
    readonly Dictionary<int, NpcSalePending> npcSales = new Dictionary<int, NpcSalePending>();
    readonly Dictionary<string, float> npcPending = new Dictionary<string, float>();
    readonly Dictionary<int, string> npcOwnerHolds = new Dictionary<int, string>();
    readonly Dictionary<int, NpcSalePending> npcOwnerSales = new Dictionary<int, NpcSalePending>();
    readonly HashSet<int> npcCompleted = new HashSet<int>();
    float npcSettingsAt;
    int npcSettingsLevel = -1;
    bool npcSettingsMonocle;

    void InitNpc()
    {
        npcMarkets.Clear(); npcSettings.Clear(); npcSales.Clear(); npcPending.Clear();
        npcOwnerHolds.Clear(); npcOwnerSales.Clear(); npcCompleted.Clear(); npcSettingsLevel = -1; npcSettingsAt = 0f;
        if (IsServer)
        {
            SessionService.SetGuard(MsgNpc, new NetGuard(2f, 8, 4400));
            SessionService.SetGuard(MsgNpcSettings, new NetGuard(1f, 3, 16));
        }
    }

    void StopNpc()
    {
        npcMarkets.Clear(); npcSettings.Clear(); npcSales.Clear(); npcPending.Clear();
        npcOwnerHolds.Clear(); npcOwnerSales.Clear(); npcCompleted.Clear();
    }

    public bool IsNpcPending(string listingIdOrUid)
    {
        if (string.IsNullOrEmpty(listingIdOrUid)) return false;
        if (npcPending.ContainsKey(listingIdOrUid)) return true;
        var l = EbaeMarket.FindListing(listingIdOrUid);
        return l != null && (l.neighbourHold || npcPending.ContainsKey(l.id));
    }

    bool IsNpcInventoryHeld(string uid)
    {
        if (npcPending.ContainsKey(uid)) return true;
        foreach (var held in npcOwnerHolds.Values) if (held == uid) return true;
        return false;
    }

    public bool RequestNpcAccept(string offerId) => RequestNpcAnswer(offerId, true);
    public bool RequestNpcDecline(string offerId) => RequestNpcAnswer(offerId, false);

    bool RequestNpcAnswer(string offerId, bool accept)
    {
        if (!UsesHostNpc || !EbaeMarket.FindOffer(offerId, out var l, out var o) ||
            l.neighbourHold || IsNpcPending(l.id) || o.TimeLeft <= 0f) return false;
        npcPending[l.id] = Time.unscaledTime + 15f;
        NpcAnswerRpc(F32(l.id), F32(offerId), accept);
        EbaeMarket.Notify();
        return true;
    }

    public bool RequestNpcPawn(string uid)
    {
        if (!UsesHostNpc || IsNpcPending(uid)) return false;
        var l = EbaeMarket.FindListing(uid);
        var item = l != null ? l.item : FindInventory(uid);
        if (item == null || (l != null && l.neighbourHold) || EbaeMarket.IsReservedUid(item.uid)) return false;
        string json = JsonUtility.ToJson(item);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxItemJson) return false;
        string key = l != null ? l.id : item.uid;
        npcPending[key] = Time.unscaledTime + 15f;
        NpcPawnRpc(F32(l != null ? l.id : null), F64(item.uid), F4096(json));
        EbaeMarket.Notify();
        return true;
    }

    void TickNpc()
    {
        if (!IsServer)
        {
            // Same owner-declared upgrade trust model as the existing seller fee, bounded to actual upgrade tiers.
            int level = UpgradeEffects.EbaeAutoAccept > 0f ? 3 : UpgradeEffects.EbaeOfferSpeed > 1f ? 2 : UpgradeEffects.EbaeExtraBuyers > 0 ? 1 : 0;
            bool monocle = EbaeMarket.MonocleBonus > 1f;
            if (BusinessSlots.Declared && Time.unscaledTime >= npcSettingsAt && (level != npcSettingsLevel || monocle != npcSettingsMonocle))
            {
                npcSettingsAt = Time.unscaledTime + 1f; npcSettingsLevel = level; npcSettingsMonocle = monocle;
                NpcSettingsRpc((byte)level, monocle);
            }
            var expired = new List<string>();
            foreach (var p in npcPending) if (Time.unscaledTime > p.Value) expired.Add(p.Key);
            foreach (var key in expired)
            {
                npcPending.Remove(key);
                ShowNotice("ebag: no confirmation yet. Your item has not been sold locally; check the listing before retrying.", ToastStyle.Bad);
                EbaeMarket.Notify();
            }
            return;
        }

        var ended = new List<NpcSalePending>();
        foreach (var s in npcSales.Values)
            if (Now > s.deadline || !BizOf(s.owner, out var b) || b.Id != s.business) ended.Add(s);
        foreach (var s in ended) AbortNpc(s, "the sale timed out or the seller left");

        // Use a snapshot: auto-accept may finish synchronously in host tests and remove a publication.
        foreach (var h in new List<HostListing>(hostListings.Values))
        {
            if (h.owner == NetworkManager.LocalClientId || h.pub.Settling) continue;
            if (!BizOf(h.owner, out var business) || business.Id != h.pub.Seller) continue;
            string key = Key(h.pub.Seller, h.pub.Id);
            var settings = npcSettings.TryGetValue(h.owner, out var declared) ? declared : new NpcSettings { speed = 1f, monocle = 1f, maxOffers = 5 };
            bool dirty = false;
            if (!npcMarkets.TryGetValue(key, out var market) || market.published != h || market.asking != h.pub.askingCents || market.json != h.json)
            {
                var l = new EbaeListing { id = h.pub.Id, item = ParseItem(h.json), askingPrice = h.pub.Asking };
                l.nextOfferAt = Time.unscaledTime + EbaeMarket.FirstOfferDelay(l.Greed, settings.speed);
                market = new NpcMarket { published = h, asking = h.pub.askingCents, json = h.json, listing = l };
                npcMarkets[key] = market;
                dirty = true;
            }
            var listing = market.listing;
            float now = Time.unscaledTime;
            for (int i = listing.offers.Count - 1; i >= 0; i--)
                if (listing.offers[i].expiresAt <= now) { listing.offers.RemoveAt(i); dirty = true; }
            if (listing.counterAt > 0f && listing.counterAt <= now && listing.offers.Count < settings.maxOffers)
            {
                listing.offers.Add(EbaeMarket.MakeCounterOffer(listing)); listing.offersReceived++; dirty = true;
            }
            if (now >= listing.nextOfferAt)
            {
                if (listing.offers.Count < settings.maxOffers)
                {
                    listing.offers.Add(EbaeMarket.MakeOffer(listing, settings.monocle)); listing.offersReceived++; dirty = true;
                    if (settings.maxOffers > 5 && listing.offersReceived == 1)
                    { listing.offers.Add(EbaeMarket.MakeOffer(listing, settings.monocle)); listing.offersReceived++; }
                }
                listing.nextOfferAt = now + EbaeMarket.NextOfferInterval(listing.Greed, settings.speed);
            }
            if (dirty) SendNpcOffers(h, listing);
            if (settings.autoAccept > 0f)
                foreach (var o in listing.offers)
                    if (!o.isJoke && o.amount >= Mathf.Max(0.5f, listing.Appraisal) * settings.autoAccept)
                    { HostNpcAnswer(h.owner, listing.id, o.id, true); break; }
        }
    }

    void SendNpcOffers(HostListing h, EbaeListing l)
    {
        foreach (var o in l.offers) o.remaining = o.TimeLeft;
        string json = JsonUtility.ToJson(new NpcOfferSnapshot { received = l.offersReceived, offers = l.offers });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxItemJson) return;
        NpcOffersRpc(F32(l.id), h.pub.publicationVersion, h.pub.askingCents, h.pub.itemUid,
            F4096(json), RpcTarget.Single(h.owner, RpcTargetUse.Temp));
    }

    void ForgetNpcListing(string seller, string id) => npcMarkets.Remove(Key(seller, id));

    void HostNpcAnswer(ulong sender, string listingId, string offerId, bool accept)
    {
        if (!BizOf(sender, out var biz)) { ReplyNpc(sender, listingId, "your business isn't connected yet", false); return; }
        string key = Key(biz.Id, listingId);
        if (!hostListings.TryGetValue(key, out var h) || h.pub.Settling || !npcMarkets.TryGetValue(key, out var m))
        { ReplyNpc(sender, listingId, "the listing is unavailable or a sale is already in progress", false); return; }
        if (m.published != h || m.asking != h.pub.askingCents || m.json != h.json)
        { ForgetNpcListing(biz.Id, listingId); ReplyNpc(sender, listingId, "the listing changed; wait for fresh offers", false); return; }
        var o = m.listing.offers.Find(x => x.id == offerId);
        if (o == null || o.TimeLeft <= 0f) { ReplyNpc(sender, listingId, "that offer expired or was already answered", false); return; }
        if (!accept)
        {
            string reply = EbaeMarket.DeclineNpcOffer(m.listing, o);
            SendNpcOffers(h, m.listing);
            ReplyNpc(sender, listingId, o.buyer + ": " + reply, true);
            return;
        }
        long gross = (long)Math.Round(Math.Min(o.amount, MaxPriceCents / 100f) * 100.0);
        StartNpcSale(h, sender, biz.Id, listingId, m.listing.item.uid, o.buyer, gross, (long)Math.Round(gross * (1.0 - h.fee)), false, o.id, h.json);
    }

    void HostNpcPawn(ulong sender, string listingId, string uid, string json)
    {
        string pendingKey = string.IsNullOrEmpty(listingId) ? uid : listingId;
        if (!BizOf(sender, out var biz)) { ReplyNpc(sender, pendingKey, "your business isn't connected yet", false); return; }
        HostListing h = null;
        ItemInstance item;
        if (!string.IsNullOrEmpty(listingId))
        {
            if (!hostListings.TryGetValue(Key(biz.Id, listingId), out h) || h.pub.Settling)
            { ReplyNpc(sender, pendingKey, "wait for this listing to be live, or delist it before pawning", false); return; }
            item = ParseItem(h.json);
        }
        else item = ParseItem(json);
        if (!EbaeItemValidation.IsValid(item, out _) || item.uid != uid)
        { ReplyNpc(sender, pendingKey, "that item could not be verified", false); return; }
        float price = EbaeMarket.PawnPrice(item);
        if (float.IsNaN(price) || float.IsInfinity(price) || price < 0f || price > MaxPriceCents / 100f)
        { ReplyNpc(sender, pendingKey, "Pawn Pete cannot buy this item at that price", false); return; }
        long gross = (long)Math.Round(price * 100.0);
        StartNpcSale(h, sender, biz.Id, listingId, uid, EbaeContent.PawnBuyer, gross, gross, true, null, JsonUtility.ToJson(item));
    }

    void StartNpcSale(HostListing h, ulong owner, string business, string listingId, string uid, string buyer, long gross, long net, bool pawn, string offerId, string itemJson)
    {
        foreach (var s in npcSales.Values)
            if (s.business == business && s.uid == uid) { ReplyNpc(owner, string.IsNullOrEmpty(listingId) ? uid : listingId, "that sale is already being confirmed", false); return; }
        var sale = new NpcSalePending { id = nextSettleId++, owner = owner, business = business, listingId = listingId,
            uid = uid, buyer = buyer, gross = gross, net = net, pawn = pawn, listing = h, offerId = offerId, itemJson = itemJson, deadline = Now + SettleTimeout };
        npcSales[sale.id] = sale;
        if (h != null) SetListingSettling(business, listingId, true);
        NpcHoldRpc(sale.id, F32(listingId), F64(uid), h != null ? h.pub.askingCents : 0,
            F4096(itemJson), F64(buyer), gross, net, pawn, RpcTarget.Single(owner, RpcTargetUse.Temp));
    }

    void HostNpcHoldResult(ulong sender, int id, bool ok)
    {
        if (!npcSales.TryGetValue(id, out var s)) { if (ok) SendRelease(sender, id); return; }
        if (s.owner != sender) return;
        if (!ok || Now > s.deadline || !BizOf(sender, out var biz) || biz.Id != s.business)
        { AbortNpc(s, "the item is no longer available"); return; }
        if (s.listing != null && (!hostListings.TryGetValue(Key(s.business, s.listingId), out var h) || h != s.listing))
        { AbortNpc(s, "the listing changed"); return; }
        // Consume the in-memory transaction once, before any events/duplicate hold replies can re-enter it.
        npcSales.Remove(id);
        // The cash RPC carries this ticket. The guest consumes payment AND the held item in that
        // same callback, so disconnecting between CashRpc and NpcCommitRpc cannot save cash + item.
        using (Ledger.ToBusiness(s.business)) Ledger.Credit(s.net / 100f, NpcCashPrefix + id, s.pawn ? LedgerIds.Pawn : LedgerIds.Ebae);
        NpcCommitRpc(id, F32(s.listingId), F64(s.uid), F64(s.buyer), s.gross, s.net, s.pawn, CashOf(s.business), RpcTarget.Single(sender, RpcTargetUse.Temp));
        if (s.listing != null)
        {
            ForgetNpcListing(s.business, s.listingId);
            hostListings.Remove(Key(s.business, s.listingId));
            int li = ListingIndex(s.business, s.listingId);
            if (li >= 0) listings.RemoveAt(li);
            CancelOffersOn(s.business, s.listingId, "it sold to an NPC", -1);
        }
        Audit("ebae-npc", ("op", "sold"), ("business", s.business), ("uid", s.uid), ("gross", s.gross), ("net", s.net), ("pawn", s.pawn));
    }

    void AbortNpc(NpcSalePending s, string why)
    {
        if (!npcSales.Remove(s.id)) return;
        if (s.listing != null) SetListingSettling(s.business, s.listingId, false);
        if (NetworkManager.ConnectedClients.ContainsKey(s.owner))
        {
            NpcAbortRpc(s.id, F64(string.IsNullOrEmpty(s.listingId) ? s.uid : s.listingId), F64(why), RpcTarget.Single(s.owner, RpcTargetUse.Temp));
        }
    }

    void ReplyNpc(ulong owner, string key, string message, bool ok) =>
        NpcReplyRpc(F64(key), F128(message), ok, RpcTarget.Single(owner, RpcTargetUse.Temp));

    static FixedString128Bytes F128(string s) { var f = new FixedString128Bytes(); f.CopyFromTruncated(s ?? ""); return f; }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NpcSettingsRpc(byte level, bool monocle, RpcParams p = default)
    {
        ulong owner = p.Receive.SenderClientId;
        if (!Allowed(MsgNpcSettings, owner, 2) || !BizOf(owner, out _) || level > 3) return;
        npcSettings[owner] = new NpcSettings { speed = level >= 2 ? 2f : 1f, monocle = monocle ? 1.03f : 1f,
            maxOffers = level >= 1 ? 6 : 5, autoAccept = level >= 3 ? 0.97f : 0f };
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NpcAnswerRpc(FixedString32Bytes listingId, FixedString32Bytes offerId, bool accept, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgNpc, sender, 65)) { ReplyNpc(sender, listingId.ToString(), "too many requests; try again shortly", false); return; }
        HostNpcAnswer(sender, listingId.ToString(), offerId.ToString(), accept);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NpcPawnRpc(FixedString32Bytes listingId, FixedString64Bytes uid, FixedString4096Bytes json, RpcParams p = default)
    {
        ulong sender = p.Receive.SenderClientId;
        if (!Allowed(MsgNpc, sender, json.Length + 96)) { ReplyNpc(sender, listingId.Length > 0 ? listingId.ToString() : uid.ToString(), "too many requests; try again shortly", false); return; }
        HostNpcPawn(sender, Str(listingId), uid.ToString(), json.ToString());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NpcHoldResultRpc(int id, bool ok, RpcParams p = default)
    {
        if (Allowed(MsgHold, p.Receive.SenderClientId, 8)) HostNpcHoldResult(p.Receive.SenderClientId, id, ok);
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void NpcOffersRpc(FixedString32Bytes listingId, int version, long asking, FixedString64Bytes uid, FixedString4096Bytes json, RpcParams p)
    {
        if (IsServer) return;
        string lid = listingId.ToString();
        var local = EbaeMarket.FindListingById(lid);
        if (local?.item == null || local.item.uid != uid.ToString() ||
            (long)Math.Round(local.askingPrice * 100.0) != asking ||
            !ownPublications.TryGetValue(lid, out var publication) || publication.version != version) return;
        var snapshot = JsonUtility.FromJson<NpcOfferSnapshot>(json.ToString());
        if (snapshot != null) EbaeMarket.ApplyNpcOffers(lid, snapshot.offers, snapshot.received);
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void NpcHoldRpc(int id, FixedString32Bytes listingId, FixedString64Bytes uid, long asking, FixedString4096Bytes itemJson,
        FixedString64Bytes buyer, long gross, long net, bool pawn, RpcParams p)
    {
        if (IsServer) return;
        string lid = Str(listingId), itemUid = uid.ToString();
        bool ok = true;
        if (lid != null)
        {
            var l = EbaeMarket.FindListingById(lid);
            ok = l != null && l.item != null && l.item.uid == itemUid && !l.neighbourHold &&
                 (long)Math.Round(l.askingPrice * 100.0) == asking;
        }
        else
        {
            ok = FindInventory(itemUid) != null && !pendingReserve.Contains(itemUid);
            foreach (var o in offers) if (o.Buyer == Businesses.LocalId && o.TradeUid == itemUid) ok = false;
            foreach (var held in npcOwnerHolds.Values) if (held == itemUid) ok = false;
        }
        var localItem = lid != null ? EbaeMarket.FindListingById(lid)?.item : FindInventory(itemUid);
        ok = ok && EbaeItemValidation.FieldsMatch(localItem, ParseItem(itemJson.ToString()));
        if (ok) ok = OwnerHold(id, lid, lid == null ? itemUid : null);
        if (ok)
        {
            npcOwnerHolds[id] = itemUid;
            npcOwnerSales[id] = new NpcSalePending { id = id, listingId = lid, uid = itemUid,
                buyer = buyer.ToString(), gross = gross, net = net, pawn = pawn };
        }
        NpcHoldResultRpc(id, ok);
        EbaeMarket.Notify();
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void NpcCommitRpc(int id, FixedString32Bytes listingId, FixedString64Bytes uid, FixedString64Bytes buyer, long gross, long net, bool pawn, long balance, RpcParams p)
    {
        if (!IsServer) CompleteOwnerNpcSale(id, balance);
    }

    /// <summary>Consume an NPC credit and its held item together on the receiving guest. Called only
    /// by the host's cash RPC; ordinary wages, fees and player trades retain their usual path.</summary>
    public static bool TryApplyNpcCash(long balance, string reason)
    {
        if (!UsesHostNpc || reason == null || !reason.StartsWith(NpcCashPrefix, StringComparison.Ordinal) ||
            !int.TryParse(reason.Substring(NpcCashPrefix.Length), out int id)) return false;
        Instance.CompleteOwnerNpcSale(id, balance);
        return true; // Never separately mirror this credit, including after the fallback commit won.
    }

    void CompleteOwnerNpcSale(int id, long balance)
    {
        if (npcCompleted.Contains(id) || !npcOwnerSales.TryGetValue(id, out var pending) ||
            !npcOwnerHolds.TryGetValue(id, out var held) || held != pending.uid) return;
        if (EbaeMarket.CompleteNpcSale(pending.listingId, pending.uid, pending.buyer,
            pending.gross / 100f, pending.net / 100f, pending.pawn, out var sale))
        {
            npcCompleted.Add(id);
            npcOwnerSales.Remove(id); npcOwnerHolds.Remove(id); holds.Remove(id);
            npcPending.Remove(string.IsNullOrEmpty(pending.listingId) ? pending.uid : pending.listingId);
            GameSession.NetMirror.SetCashCents(balance, 0, "ebag confirmed", null);
            BusinessSlots.HandBack("confirmed ebag NPC sale");
            ShowNotice($"ebag: {sale.buyer} bought your {sale.itemName}. You received {Economy.Money(sale.net)}!", ToastStyle.Good);
            Sfx.Play("cash");
        }
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void NpcReplyRpc(FixedString64Bytes key, FixedString128Bytes message, bool ok, RpcParams p)
    {
        if (IsServer) return;
        npcPending.Remove(key.ToString());
        ShowNotice("ebag: " + message, ok ? ToastStyle.Info : ToastStyle.Bad);
        EbaeMarket.Notify();
    }

    [Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
    void NpcAbortRpc(int id, FixedString64Bytes key, FixedString64Bytes reason, RpcParams p)
    {
        if (IsServer) return;
        npcOwnerHolds.Remove(id); npcOwnerSales.Remove(id); npcPending.Remove(key.ToString()); OwnerRelease(id);
        ShowNotice("ebag: sale cancelled (" + reason + "). Nothing changed hands.", ToastStyle.Bad);
        EbaeMarket.Notify();
    }
}

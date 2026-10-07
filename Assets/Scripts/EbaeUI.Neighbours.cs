using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The laptop's <b>Neighbours</b> tab (Docs/Multiplayer.md §13.8, package N5): other players' ebae listings (with their name and
/// colour), Buy now / Make offer (cash, an item from your keep crate, or both), and the offers waiting on you with Accept /
/// Counter / Decline. Everything goes through <see cref="EbaeNeighbours"/>; the host decides. Hidden outside a Town.
/// </summary>
public partial class EbaeUI
{
    static int NeighbourWaiting => EbaeNeighbours.Active ? EbaeNeighbours.Instance.WaitingOnMe : 0;

    static int NeighbourOffersOn(string listingId)
    {
        if (!EbaeNeighbours.Active) return 0;
        string me = Businesses.LocalId;
        int n = 0;
        var offers = EbaeNeighbours.Instance.Offers;
        for (int i = 0; i < offers.Count; i++) if (offers[i].Seller == me && offers[i].ListingId == listingId) n++;
        return n;
    }

    void WireNeighboursTab(VisualElement root)
    {
        tabNeighbours = null;
        var bar = root.Q<VisualElement>("tabBar");
        if (bar == null || tabSold == null) return;
        tabNeighbours = new VisualElement { name = "tabNeighbours" };
        tabNeighbours.AddToClassList("tab");
        tabNeighboursLabel = L("Neighbours", "tab-label");
        tabNeighboursBadge = L("0", "tab-badge tab-badge--hidden");
        tabNeighboursBadge.pickingMode = PickingMode.Ignore;
        tabNeighbours.Add(tabNeighboursLabel);
        tabNeighbours.Add(tabNeighboursBadge);
        int at = bar.IndexOf(tabSold);
        bar.Insert(at + 1, tabNeighbours);
        OnClick(tabNeighbours, () => SelectTab(Tab.Neighbours));
        RefreshNeighboursTab();
    }

    void RefreshNeighboursTab()
    {
        if (tabNeighbours == null) return;
        bool on = EbaeNeighbours.Active;
        tabNeighbours.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        if (!on && tab == Tab.Neighbours) { tab = Tab.Junk; builtSignature = null; }
        if (!on) return;
        int count = 0;
        string me = Businesses.LocalId;
        var ls = EbaeNeighbours.Instance.Listings;
        for (int i = 0; i < ls.Count; i++) if (ls[i].Seller != me) count++;
        SetTab(tabNeighbours, tabNeighboursLabel, Tab.Neighbours, $"Neighbours ({count})");
        int waiting = NeighbourWaiting;
        tabNeighboursBadge.text = waiting.ToString();
        tabNeighboursBadge.EnableInClassList("tab-badge--hidden", waiting == 0);
    }

    void NeighbourSignature(StringBuilder sb)
    {
        if (!EbaeNeighbours.Active) { sb.Append("off"); return; }
        var n = EbaeNeighbours.Instance;
        for (int i = 0; i < n.Listings.Count; i++)
        {
            var l = n.Listings[i];
            sb.Append(l.Seller).Append('/').Append(l.Id).Append(':').Append(l.askingCents).Append(':').Append(l.flags).Append(';');
        }
        sb.Append('|');
        for (int i = 0; i < n.Offers.Count; i++)
        {
            var o = n.Offers[i];
            sb.Append(o.id).Append(':').Append(o.cashCents).Append(':').Append(o.turn).Append(':').Append(o.settling).Append(';');
        }
        sb.Append('|').Append(openOfferForm).Append('|');
        foreach (var it in GameSession.Inventory) sb.Append(it != null ? it.uid : "-").Append(',');
        foreach (var b in Businesses.All) sb.Append(b.Id).Append(b.Name).Append(';');
    }

    // ------------------------------------------------------------------ build

    void BuildNeighbours()
    {
        if (!EbaeNeighbours.Active)
        {
            content.Add(SectionHead("Neighbours", null));
            content.Add(Empty("(-_-)", "Nobody lives next door.", "Invite friends to your Town and their junk shows up here."));
            return;
        }
        var n = EbaeNeighbours.Instance;
        string me = Businesses.LocalId;

        // 1) offers waiting on you, then your other deals in flight
        var mine = new List<NeighbourOffer>();
        var waiting = new List<NeighbourOffer>();
        for (int i = 0; i < n.Offers.Count; i++)
        {
            var o = n.Offers[i];
            if (o.Seller != me && o.Buyer != me) continue;
            bool myTurn = (o.turn == NeighbourOffer.TurnSeller && o.Seller == me) || (o.turn == NeighbourOffer.TurnBuyer && o.Buyer == me);
            (myTurn && !o.Settling ? waiting : mine).Add(o);
        }
        if (waiting.Count > 0)
        {
            content.Add(SectionHead("Waiting on you", $"{waiting.Count} neighbour deal{(waiting.Count == 1 ? "" : "s")}. accept, counter or decline."));
            var box = V("offers");
            foreach (var o in waiting) box.Add(BuildNeighbourOfferRow(o, true));
            content.Add(box);
        }
        if (mine.Count > 0)
        {
            content.Add(SectionHead("Your deals", "waiting for the other side..."));
            var box = V("offers");
            foreach (var o in mine) box.Add(BuildNeighbourOfferRow(o, false));
            content.Add(box);
        }

        // 2) the neighbours' stuff
        int others = 0;
        for (int i = 0; i < n.Listings.Count; i++) if (n.Listings[i].Seller != me) others++;
        int myListed = 0;
        for (int i = 0; i < n.Listings.Count; i++) if (n.Listings[i].Seller == me) myListed++;
        content.Add(SectionHead("Next door", others == 0 ? null
            : $"{others} thing{(others == 1 ? "" : "s")} for sale in the Town. you're showing {myListed} of yours."));
        if (others == 0)
        {
            content.Add(Empty("o_O", "Your neighbours aren't selling anything.", myListed > 0
                ? $"your {myListed} listing{(myListed == 1 ? " is" : "s are")} showing to them though."
                : "List something from Your Junk and they'll see it too."));
            return;
        }
        int index = 0;
        for (int i = 0; i < n.Listings.Count; i++)
        {
            var l = n.Listings[i];
            if (l.Seller == me) continue;
            content.Add(BuildNeighbourListingCard(l, index++));
        }
    }

    VisualElement SellerChip(string businessId)
    {
        var row = V("buyer-row");
        var dot = L(Initial(EbaeNeighbours.OwnerName(businessId)), "avatar");
        dot.style.backgroundColor = (Color)EbaeNeighbours.Tint(businessId);
        dot.style.width = 26; dot.style.height = 26; dot.style.fontSize = 13; dot.style.marginRight = 6;
        row.Add(dot);
        row.Add(L(EbaeNeighbours.ShopName(businessId), "buyer-name"));
        return row;
    }

    static string Initial(string s) => string.IsNullOrEmpty(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();

    static string Key(in NeighbourListing l) => l.Seller + "/" + l.Id;

    VisualElement BuildNeighbourListingCard(NeighbourListing l, int index)
    {
        var n = EbaeNeighbours.Instance;
        string me = Businesses.LocalId, key = Key(l);
        var card = V("card listing-card " + (index % 2 == 0 ? "card--tilt-r" : "card--tilt-l"));
        card.style.borderLeftWidth = 8;
        card.style.borderLeftColor = (Color)EbaeNeighbours.Tint(l.Seller);

        var head = V("listing-head");
        head.Add(Icon(l.defId.ToString(), l.Rarity, l.ItemName, true));
        var info = V("listing-info");
        info.Add(SellerChip(l.Seller));
        var title = V("title-row");
        title.Add(L(RarityInfo.Label(l.Rarity), "rarity-chip rar--" + l.Rarity.ToString().ToLowerInvariant()));
        title.Add(L(l.ItemName, "item-name"));
        info.Add(title);
        var sub = V("listing-sub");
        sub.Add(L("asking " + Economy.Money(l.Asking), "listing-asking"));
        var tier = EbaeMarket.GreedTier(l.Asking / Mathf.Max(0.5f, l.Appraisal));
        sub.Add(L(tier.ToString().ToUpperInvariant(), "greed-tag greed-tag--" + tier.ToString().ToLowerInvariant()));
        sub.Add(L($"appraised {Economy.Money(l.Appraisal)} · condition {Mathf.RoundToInt(l.condition * 100f)}%", "item-meta"));
        info.Add(sub);
        head.Add(info);

        var buttons = V("listing-buttons");
        bool mineOffer = false;
        for (int i = 0; i < n.Offers.Count; i++)
            if (n.Offers[i].Buyer == me && n.Offers[i].Seller == l.Seller && n.Offers[i].ListingId == l.Id) mineOffer = true;
        if (l.Settling) buttons.Add(L("someone's buying it...", "item-meta"));
        else
        {
            bool afford = GameSession.CashCents >= l.askingCents;
            var buy = Btn($"Buy now {Economy.Money(l.Asking)}", "btn-accept btn-small", () => DoNeighbourBuy(card, l));
            if (!afford) buy.style.opacity = 0.5f;
            buttons.Add(buy);
            if (!mineOffer)
                buttons.Add(Btn(openOfferForm == key ? "Close" : "Make offer", "btn-list btn-small", () =>
                {
                    openOfferForm = openOfferForm == key ? null : key;
                    Refresh(true);
                }));
        }
        head.Add(buttons);
        card.Add(head);

        if (mineOffer) card.Add(L("you've got a deal going on this - see above.", "offers-empty"));
        else if (openOfferForm == key && !l.Settling) card.Add(BuildOfferForm(l));

        PopInOnce(card, "nlisting:" + key, "card--pop");
        return card;
    }

    VisualElement BuildOfferForm(NeighbourListing l)
    {
        string key = Key(l);
        var box = V("offers");
        box.Add(L($"make {EbaeNeighbours.OwnerName(l.Seller)} an offer: cash, something from your keep crate, or both", "item-meta"));

        var row = V("custom-row");
        var field = new TextField { maxLength = 10 };
        field.AddToClassList("price-field");
        field.textEdition.placeholder = "cash $ (0 for a straight swap)";
        if (offerCash.TryGetValue(key, out var typed)) field.SetValueWithoutNotify(typed);
        field.RegisterValueChangedCallback(e => offerCash[key] = e.newValue);
        row.Add(field);

        // the trade item: cycle through what's in your keep crate (not listed, not already offered)
        var pool = new List<ItemInstance>();
        foreach (var it in GameSession.Inventory) if (it != null && !EbaeMarket.IsReservedUid(it.uid)) pool.Add(it);
        offerTrade.TryGetValue(key, out string uid);
        ItemInstance chosen = null;
        foreach (var it in pool) if (it.uid == uid) chosen = it;
        if (chosen == null) offerTrade.Remove(key);
        if (chosen != null) row.Add(Icon(chosen, true));
        string tradeLabel = chosen == null ? (pool.Count == 0 ? "+ item: (keep crate empty)" : "+ item: none  >")
                                           : $"+ {EbaeMarket.NameOf(chosen)} ({Economy.Money(EbaeMarket.Appraise(chosen))})  >";
        row.Add(Btn(tradeLabel, "btn-preset btn-fair btn-small", () =>
        {
            if (pool.Count == 0) return;
            int at = chosen == null ? 0 : pool.IndexOf(chosen) + 1;
            if (at >= pool.Count) offerTrade.Remove(key);
            else offerTrade[key] = pool[at].uid;
            offerCash[key] = field.value;
            Refresh(true);
        }));
        row.Add(Btn("Send offer", "btn-list", () => DoNeighbourOffer(l, field)));
        box.Add(row);
        return box;
    }

    VisualElement BuildNeighbourOfferRow(NeighbourOffer o, bool myTurn)
    {
        var n = EbaeNeighbours.Instance;
        string me = Businesses.LocalId;
        bool iSell = o.Seller == me;
        string other = iSell ? o.Buyer : o.Seller;
        string item = ListingName(o);

        var row = V("offer-row");
        if (o.HasTrade) row.AddToClassList("offer-row--special");
        var avatar = L(Initial(EbaeNeighbours.OwnerName(other)), "avatar");
        avatar.style.backgroundColor = (Color)EbaeNeighbours.Tint(other);
        row.Add(avatar);

        var body = V("offer-body");
        var nameRow = V("buyer-row");
        nameRow.Add(L(EbaeNeighbours.ShopName(other), "buyer-name"));
        nameRow.Add(L("neighbour", "buyer-flair buyer-flair--special"));
        if (o.rounds > 0) nameRow.Add(L(o.rounds == 1 ? "countered" : "final round", "buyer-flair"));
        body.Add(nameRow);
        string what = (o.cashCents > 0 ? Economy.Money(o.Cash) : "") +
                      (o.HasTrade ? (o.cashCents > 0 ? " + " : "") + (iSell ? "their " : "your ") + $"{o.TradeName} ({RarityInfo.Label(o.TradeRarity)}, appraised {Economy.Money(o.tradeAppraisalCents / 100f)})" : "");
        string line;
        if (o.Settling) line = "deal going through...";
        else if (iSell) line = o.turn == NeighbourOffer.TurnSeller
            ? (o.rounds == 0 ? $"offers {what} for your {item}" : $"came back with {what} for your {item}")
            : $"you countered at {what} for your {item}. waiting...";
        else line = o.turn == NeighbourOffer.TurnBuyer
            ? $"countered: {what} for their {item}"
            : $"you offered {what} for their {item}. waiting...";
        body.Add(L(line, "buyer-message"));
        var photos = V("trade-photos");
        if (n.TryGetListing(o.Seller, o.ListingId, out var listing))
            photos.Add(Icon(listing.defId.ToString(), listing.Rarity, listing.ItemName, true));
        if (o.HasTrade)
        {
            photos.Add(L("↔", "item-meta"));
            photos.Add(Icon(o.tradeDefId.ToString(), o.TradeRarity, o.TradeName, true));
        }
        if (photos.childCount > 0) body.Add(photos);
        row.Add(body);

        var amount = V("offer-amount");
        amount.Add(L(o.cashCents > 0 ? Economy.Money(o.Cash) : "swap", "offer-gross"));
        if (iSell && o.cashCents > 0) amount.Add(L($"you get {Economy.Money(EbaeMarket.NetAfterFee(o.Cash))}", "offer-net"));
        row.Add(amount);

        var timer = V("offer-timer");
        var time = L("", "offer-time");
        var bar = V("timer-bar");
        var fill = V("timer-fill");
        bar.Add(fill);
        timer.Add(time);
        timer.Add(bar);
        row.Add(timer);
        neighbourTimers.Add(new NeighbourTimer { offerId = o.id, time = time, fill = fill });

        if (o.Settling) { }
        else if (myTurn)
        {
            row.Add(Btn("Accept", "btn-accept", () => DoNeighbourRespond(row, o, EbaeNeighbours.ActAccept, null)));
            if (o.rounds < EbaeNeighbours.MaxCounterRounds)
            {
                var field = new TextField { maxLength = 10 };
                field.AddToClassList("price-field");
                field.style.minWidth = 80;
                field.textEdition.placeholder = "$";
                if (counterText.TryGetValue(o.id, out var typed)) field.SetValueWithoutNotify(typed);
                int id = o.id;
                field.RegisterValueChangedCallback(e => counterText[id] = e.newValue);
                row.Add(field);
                row.Add(Btn("Counter", "btn-list btn-small", () => DoNeighbourRespond(row, o, EbaeNeighbours.ActCounter, field)));
            }
            row.Add(Btn("Decline", "btn-decline", () => DoNeighbourRespond(row, o, EbaeNeighbours.ActDecline, null)));
        }
        else row.Add(Btn(iSell ? "Call it off" : "Withdraw", "btn-decline btn-small", () => DoNeighbourRespond(row, o, EbaeNeighbours.ActDecline, null)));

        if (seenOffers.Add("n" + o.id + ":" + o.rounds)) PopIn(row, "offer-row--pop");
        return row;
    }

    static string ListingName(in NeighbourOffer o)
    {
        if (EbaeNeighbours.Active && EbaeNeighbours.Instance.TryGetListing(o.Seller, o.ListingId, out var l)) return l.ItemName;
        return "listing";
    }

    // ------------------------------------------------------------------ actions

    void DoNeighbourBuy(VisualElement card, NeighbourListing l)
    {
        if (!EbaeNeighbours.Active) return;
        if (GameSession.CashCents < l.askingCents)
        {
            Sfx.Play("buzz");
            UiToast($"you need {Economy.Money(l.Asking)} for that. go pick up some trash.", "bad");
            return;
        }
        Sfx.Play("whoosh");
        EbaeNeighbours.Instance.RequestBuyNow(l.Seller, l.Id);
        UiToast($"buying {l.ItemName} from {EbaeNeighbours.OwnerName(l.Seller)}...", "info");
    }

    void DoNeighbourOffer(NeighbourListing l, TextField field)
    {
        if (!EbaeNeighbours.Active) return;
        string key = Key(l);
        offerTrade.TryGetValue(key, out string uid);
        long cents = 0;
        string text = field.value;
        bool hasCash = !string.IsNullOrWhiteSpace(text);
        string error = null;
        if (hasCash && !EbaeTradingRules.TryParseOfferCash(text, out cents, out error))
        {
            Sfx.Play("buzz", null, 0.6f);
            Flash(field, "price-field--shake", 120);
            UiToast(error, "bad");
            return;
        }
        if (cents == 0 && string.IsNullOrEmpty(uid)) { Sfx.Play("buzz"); UiToast("offer some cash or pick an item to swap", "bad"); return; }
        if (cents > l.askingCents) { Sfx.Play("buzz"); UiToast($"that's more than they're asking ({Economy.Money(l.Asking)}). just Buy now?", "bad"); return; }
        if (cents > GameSession.CashCents) { Sfx.Play("buzz"); UiToast("you don't have that much cash", "bad"); return; }
        if (!EbaeNeighbours.Instance.RequestOffer(l.Seller, l.Id, cents, uid))
        {
            Sfx.Play("buzz");
            UiToast("couldn't send that offer. is the item still in your crate?", "bad");
            return;
        }
        openOfferForm = null;
        offerCash.Remove(key);
        offerTrade.Remove(key);
        Sfx.Play("ding", null, 1f, 1.1f);
        UiToast($"offer sent to {EbaeNeighbours.OwnerName(l.Seller)}!", "good");
        Refresh(true);
    }

    void DoNeighbourRespond(VisualElement row, NeighbourOffer o, byte action, TextField field)
    {
        if (!EbaeNeighbours.Active) return;
        long counter = 0;
        if (action == EbaeNeighbours.ActCounter)
        {
            string error = null;
            if (field == null || !EbaeTradingRules.TryParsePrice(field.value, out counter, out error))
            {
                Sfx.Play("buzz", null, 0.6f);
                if (field != null) Flash(field, "price-field--shake", 120);
                UiToast(field == null ? "type your counter price first" : error, "bad");
                return;
            }
        }
        if (action == EbaeNeighbours.ActDecline) { if (!BeginLeaving(row, "offer-row--leaving")) return; Sfx.Play("squeak", null, 0.7f); }
        else Sfx.Play(action == EbaeNeighbours.ActAccept ? "ding" : "whoosh");
        counterText.Remove(o.id);
        EbaeNeighbours.Instance.Respond(o.id, action, counter);
    }

    void TickNeighbourTimers()
    {
        if (neighbourTimers.Count == 0 || !EbaeNeighbours.Active) return;
        var n = EbaeNeighbours.Instance;
        foreach (var v in neighbourTimers)
        {
            if (!n.TryGetOffer(v.offerId, out var o)) continue;
            float left = n.TimeLeft(o);
            int sec = Mathf.CeilToInt(left);
            if (sec != v.lastSecond) { v.lastSecond = sec; v.time.text = o.Settling ? "..." : sec + "s"; }
            v.fill.style.width = Length.Percent(n.TimeLeft01(o) * 100f);
            bool urgent = left < 10f;
            v.time.EnableInClassList("offer-time--urgent", urgent);
            v.fill.EnableInClassList("timer-fill--urgent", urgent);
        }
    }
}

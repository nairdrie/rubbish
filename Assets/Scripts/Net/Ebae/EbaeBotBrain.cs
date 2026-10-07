using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// <c>-botbrain ebae</c> (package N5 test rig, 3 bots: bot-1 hosts, bot-2 and bot-3 join). Each bot stocks its keep crate with
/// six cheap valuables (uids <c>&lt;bot&gt;-i0..i5</c>, item k = the k-th cheapest valuable), tops its cash up to $200 through the
/// Ledger, lists i0–i2 on ebae and then plays a script on the shared server clock (s since the host started):
/// <list type="table">
/// <item>30: bot-2 buys bot-1's i0 (Buy now). bot-3 offers half on bot-1's i1; bot-1 counters at 85%, bot-3 accepts.</item>
/// <item>36: bot-2 offers half on bot-3's i0; bot-3 declines.</item>
/// <item>40: bot-3 offers its i3 + $5 for bot-2's i1 (a trade); bot-2 accepts trades.</item>
/// <item>44: bot-1 (host) offers its i4 straight up for bot-2's i2; bot-2 accepts.</item>
/// <item>50: bot-2 and bot-3 both Buy now bot-1's i2 on the same server tick: exactly one may win.</item>
/// <item>54: bot-1 (host) and bot-3 both Buy now bot-2's i0 (a guest seller, so the listing is locked while it settles).</item>
/// <item>58: bot-2 offers on bot-3's i2; bot-3 never answers (run with <c>--rejoin bot-3@100</c>: the seller leaves, the
/// offer is cancelled, and bot-3's listings come back when it rejoins).</item>
/// </list>
/// Seller policy for everyone: a trade → accept; i1 → counter once at 85% then accept the comeback; bot-3's i2 → ignore;
/// anything else → decline. Buyers accept counters. Every 10 s each bot writes <c>ebae-items</c> (its inventory and listings)
/// and <c>ebae-bot view</c> (the listings it can see per seller) to NetAudit; the checker reads those.
/// </summary>
public sealed class EbaeBotBrain : IBotBrain
{
    readonly IBotBrain idle = BotBrains.Create("idle");
    readonly HashSet<string> done = new HashSet<string>();
    readonly Dictionary<string, double> seenAt = new Dictionary<string, double>();
    string me = "";
    bool stocked;
    double nextAudit;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("ebae", () => new EbaeBotBrain());

    public void Begin(BotContext c)
    {
        idle.Begin(c);
        string label = c.Options.label ?? "bot";
        int at = label.LastIndexOf(':');
        me = at >= 0 ? label.Substring(at + 1) : label;
    }

    public void End(BotContext c)
    {
        idle.End(c);
        if (EbaeNeighbours.Active) EbaeNeighbours.AuditItems("end");
    }

    static List<ItemDef> cheap;

    /// <summary>The valuables sorted cheapest first (the same order on every machine).</summary>
    static List<ItemDef> Cheap()
    {
        if (cheap != null) return cheap;
        cheap = new List<ItemDef>(ItemCatalog.InCategory(ItemCategory.Valuable));
        cheap.Sort((a, b) => a.maxValue != b.maxValue ? a.maxValue.CompareTo(b.maxValue) : string.CompareOrdinal(a.id, b.id));
        return cheap;
    }

    static int IndexOfDef(string defId)
    {
        var list = Cheap();
        for (int i = 0; i < list.Count && i < 6; i++) if (list[i].id == defId) return i;
        return -1;
    }

    public void Tick(BotContext c, float dt)
    {
        idle.Tick(c, dt);
        if (!EbaeNeighbours.Active || c.Nm == null) return;
        var n = EbaeNeighbours.Instance;
        string myBiz = Businesses.LocalId;
        if (!Businesses.TryGet(myBiz, out var mine) || !mine.IsValid) return; // not declared yet
        double t = c.Nm.ServerTime.Time;

        if (!stocked) { stocked = true; Stock(); }
        if (t >= nextAudit)
        {
            nextAudit = t + 10.0;
            EbaeNeighbours.AuditItems("tick");
            var view = new Dictionary<string, object> { { "phase", "view" }, { "t", Math.Round(t, 1) } };
            for (int i = 0; i < n.Listings.Count; i++)
            {
                string who = OwnerOf(n.Listings[i].Seller);
                view[who] = (view.TryGetValue(who, out var v) ? (int)v : 0) + 1;
            }
            NetAudit.Event("ebae-bot", view);
        }

        AnswerOffers(c, n, myBiz, t);
        Script(n, t);
    }

    void Stock()
    {
        var defs = Cheap();
        bool has(string uid)
        {
            foreach (var it in GameSession.Inventory) if (it != null && it.uid == uid) return true;
            return EbaeMarket.FindListing(uid) != null;
        }
        int added = 0;
        bool fresh = !has($"{me}-i0") && !has($"{me}-i1") && !has($"{me}-i2") && !has($"{me}-i3");
        if (fresh)
        {
            for (int k = 0; k < 6 && k < defs.Count; k++)
            {
                var d = defs[k];
                GameSession.Inventory.Add(new ItemInstance
                {
                    uid = $"{me}-i{k}", defId = d.id, massKg = d.minMass, value = 10f + 2f * k, condition = 1f,
                    foundAt = me + "'s test crate", dayFound = 1,
                });
                added++;
            }
            GameSession.NotifyChanged();
            if (GameSession.CashCents < 20000) Ledger.Credit(200f, "bot float", "bot_float");
        }
        int listed = 0;
        for (int k = 0; k < 3; k++)
        {
            string uid = $"{me}-i{k}";
            if (EbaeMarket.FindListing(uid) != null) continue;
            var it = GameSession.Inventory.Find(x => x != null && x.uid == uid);
            if (it != null && EbaeMarket.List(uid, EbaeMarket.PresetPrice(it, EbaePricePreset.Fair))) listed++;
        }
        NetAudit.Event("ebae-bot", new Dictionary<string, object> { { "phase", "stock" }, { "fresh", fresh }, { "added", added }, { "listed", listed },
                                                                    { "cash", GameSession.CashCents } });
    }

    string OwnerOf(string biz) => Businesses.TryGet(biz, out var b) ? b.OwnerName : "?";

    bool FindListing(EbaeNeighbours n, string owner, int index, out NeighbourListing l)
    {
        for (int i = 0; i < n.Listings.Count; i++)
        {
            var x = n.Listings[i];
            if (OwnerOf(x.Seller) == owner && IndexOfDef(x.defId.ToString()) == index) { l = x; return true; }
        }
        l = default;
        return false;
    }

    /// <summary>Fire step <paramref name="name"/> once, in the window [at, at + 8) of server time (a rejoined bot skips old steps).</summary>
    bool Due(string name, double t, double at)
    {
        if (t < at || t >= at + 8.0 || done.Contains(name)) return false;
        done.Add(name);
        return true;
    }

    void Log(string step, object detail) =>
        NetAudit.Event("ebae-bot", new Dictionary<string, object> { { "phase", "step" }, { "step", step }, { "detail", detail ?? "" } });

    void Script(EbaeNeighbours n, double t)
    {
        if (me == "bot-2")
        {
            if (Due("buynow", t, 30)) Buy(n, "bot-1", 0, "buynow");
            if (Due("decline-me", t, 36)) Offer(n, "bot-3", 0, 0.5f, null, "decline-me");
            if (Due("race-prep", t, 46)) Aim(n, "bot-1", 2, "race");
            if (Due("race", t, 50)) Fire(n, "race");
            if (Due("seller-leaves", t, 58)) Offer(n, "bot-3", 2, 0.6f, null, "seller-leaves");
        }
        else if (me == "bot-3")
        {
            if (Due("counter-me", t, 30)) Offer(n, "bot-1", 1, 0.5f, null, "counter-me");
            if (Due("trade", t, 40)) Offer(n, "bot-2", 1, 0f, $"{me}-i3", "trade", 500);
            if (Due("race-prep", t, 46)) Aim(n, "bot-1", 2, "race");
            if (Due("race", t, 50)) Fire(n, "race");
            if (Due("race2-prep", t, 51)) Aim(n, "bot-2", 0, "race2");
            if (Due("race2", t, 54)) Fire(n, "race2");
        }
        else if (me == "bot-1")
        {
            if (Due("host-trade", t, 44)) Offer(n, "bot-2", 2, 0f, $"{me}-i4", "host-trade");
            if (Due("race2-prep", t, 51)) Aim(n, "bot-2", 0, "race2");
            if (Due("race2", t, 54)) Fire(n, "race2");
        }
    }

    readonly Dictionary<string, NeighbourListing> aimed = new Dictionary<string, NeighbourListing>();

    /// <summary>Races: pick the target a few seconds early, so both racers fire on the same tick even if the other already won.</summary>
    void Aim(EbaeNeighbours n, string owner, int index, string step)
    {
        if (FindListing(n, owner, index, out var l)) aimed[step] = l;
        else Log(step, $"no listing {owner}/{index} to aim at");
    }

    void Fire(EbaeNeighbours n, string step)
    {
        if (!aimed.TryGetValue(step, out var l)) return;
        n.RequestBuyNow(l.Seller, l.Id);
        Log(step, $"buy now {OwnerOf(l.Seller)} {l.Id} at {l.askingCents} (still listed here: {n.TryGetListing(l.Seller, l.Id, out _)})");
    }

    void Buy(EbaeNeighbours n, string owner, int index, string step)
    {
        if (!FindListing(n, owner, index, out var l)) { Log(step, $"no listing {owner}/{index}"); return; }
        n.RequestBuyNow(l.Seller, l.Id);
        Log(step, $"buy now {owner}/{index} {l.Id} at {l.askingCents}");
    }

    void Offer(EbaeNeighbours n, string owner, int index, float share, string tradeUid, string step, long cashOverride = -1)
    {
        if (!FindListing(n, owner, index, out var l)) { Log(step, $"no listing {owner}/{index}"); return; }
        long cash = cashOverride >= 0 ? cashOverride : (long)Math.Round(l.askingCents * share);
        bool sent = n.RequestOffer(l.Seller, l.Id, cash, tradeUid);
        Log(step, $"offer {owner}/{index} {l.Id} cash {cash} trade {tradeUid ?? "-"} sent {sent}");
    }

    void AnswerOffers(BotContext c, EbaeNeighbours n, string myBiz, double t)
    {
        for (int i = 0; i < n.Offers.Count; i++)
        {
            var o = n.Offers[i];
            if (o.Settling) continue;
            string k = o.id + ":" + o.rounds;
            bool sellerTurn = o.Seller == myBiz && o.turn == NeighbourOffer.TurnSeller;
            bool buyerTurn = o.Buyer == myBiz && o.turn == NeighbourOffer.TurnBuyer;
            if (!sellerTurn && !buyerTurn) continue;
            if (!seenAt.TryGetValue(k, out double first)) { seenAt[k] = t; continue; }
            if (t - first < 2.0 || done.Contains("answer:" + k)) continue; // think about it for 2 s
            done.Add("answer:" + k);

            if (buyerTurn)
            {
                n.Respond(o.id, EbaeNeighbours.ActAccept);
                Log("answer", $"buyer accepts counter on offer {o.id} at {o.cashCents}");
                continue;
            }
            n.TryGetListing(o.Seller, o.ListingId, out var l);
            int index = IndexOfDef(l.defId.ToString());
            if (o.HasTrade)
            {
                n.Respond(o.id, EbaeNeighbours.ActAccept);
                Log("answer", $"seller accepts trade {o.id} ({o.TradeUid} + {o.cashCents})");
            }
            else if (index == 1 && o.rounds == 0)
            {
                long counter = Math.Max(o.cashCents + 50, (long)Math.Round(l.askingCents * 0.85));
                counter = Math.Min(counter, l.askingCents);
                n.Respond(o.id, EbaeNeighbours.ActCounter, counter);
                Log("answer", $"seller counters {o.id} at {counter}");
            }
            else if (index == 1)
            {
                n.Respond(o.id, EbaeNeighbours.ActAccept);
                Log("answer", $"seller accepts {o.id} at {o.cashCents}");
            }
            else if (me == "bot-3" && index == 2)
            {
                Log("answer", $"seller ignores {o.id} (going home)");
            }
            else
            {
                n.Respond(o.id, EbaeNeighbours.ActDecline);
                Log("answer", $"seller declines {o.id}");
            }
        }
    }
}

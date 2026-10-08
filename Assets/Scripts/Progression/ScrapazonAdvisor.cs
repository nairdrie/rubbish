using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shopping advice for the Scrapazon UI (Docs/Progression.md §2.2 "Best next buy", §9.5 A.4 "New for you").
/// Pure reads of the catalog + company state; no side effects except remembering which rank the shop was last seen at.
/// </summary>
public static class ScrapazonAdvisor
{
    const string SeenRankCounter = "sz_seen_rank";

    /// <summary>Lower = buy sooner. Pacing-critical lines (permits, hopper, arm, Magnus's career path) are favoured.</summary>
    static float Weight(string id)
    {
        switch (id)
        {
            case ShopIds.PermitMinivanMeadows:
            case ShopIds.PermitCouponCommons:
            case ShopIds.PermitSnootingtonHills:
            case ShopIds.PermitStackingtonHeights:
            case ShopIds.PermitBillionaireBluffs: return 0.3f;
            case ShopIds.TruckCapacity: return 0.55f;
            case ShopIds.TruckModel: return 0.4f; // after cheap sideboards, trade-up beats buying the pricier second extension
            case ShopIds.TruckArm: return 0.6f;
            case ShopIds.RecyclingContracts:
            case ShopIds.MagnusBrain:
            case ShopIds.MagnusRailMotor:
            case ShopIds.MagnusGripper:
            case ShopIds.MagnusVision:
            case ShopIds.ConveyorBelt: return 0.8f;
            case ShopIds.TruckTurbo:
            case ShopIds.TruckHandling: return 0.85f;
            case ShopIds.GoldenTruck: return 1f;
            default: return 1f;
        }
    }

    public struct Suggestion
    {
        public ScrapazonItem item;
        public int level;
        public float price;
        public bool affordable;
        public float shortfall;
        /// <summary>Rough in-game days to save up at yesterday's net (0 when affordable).</summary>
        public float days;
    }

    /// <summary>Up to <paramref name="count"/> unlocked, not-maxed upgrades worth buying next (cosmetics and consumables excluded).</summary>
    public static List<Suggestion> BestNextBuys(int count)
    {
        var pool = new List<(float score, Suggestion s)>();
        float cash = GameSession.Cash;
        float perDay = Mathf.Max(Economy.WorkOrderMinPay, DailyStats.Yesterday.Net);
        foreach (var it in ScrapazonCatalog.All)
        {
            if (!it.IsUpgrade || it.dept == "cosmetics" || ScrapazonStore.IsMaxed(it) || ScrapazonStore.IsLocked(it)) continue;
            float price = ScrapazonStore.NextPrice(it);
            if (price < 0f) continue;
            bool afford = cash + 0.001f >= price;
            float shortfall = Mathf.Max(0f, price - cash);
            var s = new Suggestion
            {
                item = it, level = ScrapazonStore.NextLevel(it), price = price, affordable = afford, shortfall = shortfall,
                days = afford ? 0f : shortfall / perDay,
            };
            pool.Add((price * Weight(it.id), s));
        }
        pool.Sort((a, b) => a.score.CompareTo(b.score));
        var list = new List<Suggestion>();
        for (int i = 0; i < pool.Count && list.Count < count; i++) list.Add(pool[i].s);
        return list;
    }

    /// <summary>The rank at which the shop was last looked at (ribbons mark what unlocked since).</summary>
    public static int SeenRank
    {
        get
        {
            int r = GameSession.GetCounter(SeenRankCounter);
            return r <= 0 ? Progression.Rank : r;
        }
    }

    /// <summary>First boot of a save: start "seen" at the current rank so old saves don't ribbon everything.</summary>
    public static void EnsureSeenInit()
    {
        if (GameSession.GetCounter(SeenRankCounter) <= 0) GameSession.SetCounter(SeenRankCounter, Progression.Rank);
    }

    /// <summary>Remember that the shop has been seen at the current rank (call when the laptop closes).</summary>
    public static void MarkSeen()
    {
        if (GameSession.GetCounter(SeenRankCounter) != Progression.Rank) GameSession.SetCounter(SeenRankCounter, Progression.Rank);
    }

    /// <summary>True when the item's next level unlocked after the last visit ("NEW FOR YOU" ribbon).</summary>
    public static bool IsNewForYou(ScrapazonItem it)
    {
        if (it == null || it.IsTeaser || ScrapazonStore.IsMaxed(it) || ScrapazonStore.IsLocked(it)) return false;
        int need = it.RankFor(ScrapazonStore.NextLevel(it));
        return need > SeenRank && need <= Progression.Rank;
    }

    /// <summary>How many products carry the ribbon right now.</summary>
    public static int NewForYouCount()
    {
        int n = 0;
        foreach (var it in ScrapazonCatalog.All) if (IsNewForYou(it)) n++;
        return n;
    }
}

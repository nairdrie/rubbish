using System.Collections.Generic;

/// <summary>The Dumpster Dunker on Scrapazon (Docs/Progression.md §6.11): a rear-loader attachment, department Truck.</summary>
public static class DumpsterShop
{
    static ScrapazonReview R(int s, string t, string a) => new ScrapazonReview(s, t, a);

    /// <summary>Appended to <see cref="ScrapazonCatalog.All"/> once (idempotent).</summary>
    public static void AddTo(List<ScrapazonItem> all)
    {
        if (all == null) return;
        foreach (var it in all) if (it != null && it.id == DumpsterRules.UpgradeId) return;
        all.Add(new ScrapazonItem
        {
            id = DumpsterRules.UpgradeId, kind = ShopItemKind.Upgrade, dept = "truck", tags = new[] { "truck" },
            brand = "Big Tip Hydraulics",
            // Rank 4: the Commons contract needs it, so it must be on sale the moment Commons opens (the Meadows contract is the real gate).
            prices = new[] { 2400f, 9000f }, maxLevel = 2, rankRequired = new[] { 4, 7 },
            prereqs = new[]
            {
                new[] { ShopIds.TruckModel + ":1", ShopIds.PermitCouponCommons },
                new[] { DumpsterRules.UpgradeId + ":1" },
            },
            title = "Dumpster Dunker Rear Tipper (Lifts 1 Dumpster, 0 Feelings)",
            levelTitles = new[] { "Dumpster Dunker Rear Tipper (Lifts 1 Dumpster, 0 Feelings)", "Dumpster Dunker II: Twin Rams" },
            blurb = "Two arms, one bar, and the confidence to pick up a metal box full of bins.",
            levelBlurbs = new[]
            {
                "Two arms, one bar, and the confidence to pick up a metal box full of bins.",
                "Same dunk. Twice the rams. The dumpster doesn't even have time to be scared.",
            },
            levelEffects = new[]
            {
                "Rear-loader only. Back the hopper up to a shop's dumpster, stop, and HOLD B: it lifts the dumpster and tips every bag " +
                "in it into the truck. Each bag pays like a curb bag. Shops in Coupon Commons put out both, and the Commons contract can't be won without it.",
                "Tips more than twice as fast (a full dumpster in about a second).",
            },
            listPrice = 3999f, rating = 4.7f, ratingCount = 1288, badge = "Commercial Grade",
            reviews = new[]
            {
                R(5, "picked up the dumpster. picked up the cat that lived in the dumpster. cat is fine. cat is driving now", "Big Lou"),
                R(4, "backing up is the hard part. the dunking is the fun part", "Dee"),
                R(5, "six bags in one go. i felt like a real business for a second", "Rosa"),
                R(2, "tried it on the little truck. do not try it on the little truck", "Ferris"),
            },
        });
    }
}

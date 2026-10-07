using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// F1's Scrapazon upgrades (Docs/Multiplayer.md §14.1). Same model as every other upgrade: a level in the business's save
/// (<see cref="GameSession.GetUpgradeLevel"/>), bought through <see cref="ScrapazonStore.TryBuy"/> (Ledger.Spend).
/// <list type="bullet">
/// <item><b>Grip Gloves</b> (gear, yours): grip drains ×0.75 / ×0.55. They work on any truck you ride.</item>
/// <item><b>Padded Step</b> (truck): jolts cost ×0.65 / ×0.4 for everyone riding this truck.</item>
/// <item><b>Safety Bar</b> (truck): nobody riding it is flung below 8 m/s while the bar is down; riders flip it with B.</item>
/// </list>
/// A truck's mods come from its business (the owner's machine reports them; <see cref="HandsNet"/> shares them in co-op).
/// </summary>
public static class RideShop
{
    public const string GripGloves = "grip_gloves", PaddedStep = "padded_step", SafetyBar = "safety_bar";

    public static float GloveDrainMult(int level) => level >= 2 ? 0.55f : level == 1 ? 0.75f : 1f;
    public static float PadJoltMult(int level) => level >= 2 ? 0.4f : level == 1 ? 0.65f : 1f;

    /// <summary>This machine's own levels (your business).</summary>
    public static int Local(string id) => GameSession.GetUpgradeLevel(id);

    static ScrapazonReview R(int s, string t, string a) => new ScrapazonReview(s, t, a);

    static ScrapazonItem Up(string id, string dept, string brand, float[] prices, int[] ranks, string[] titles, string[] blurbs,
                            string[] effects, float list, float rating, int count, string badge, ScrapazonReview[] reviews)
    {
        return new ScrapazonItem
        {
            id = id, kind = ShopItemKind.Upgrade, dept = dept, tags = new[] { dept }, brand = brand,
            prices = prices, maxLevel = prices.Length, rankRequired = ranks,
            title = titles[0], levelTitles = titles, levelBlurbs = blurbs, blurb = blurbs[0], levelEffects = effects,
            listPrice = list, rating = rating, ratingCount = count, badge = badge, reviews = reviews,
        };
    }

    /// <summary>Appended to <see cref="ScrapazonCatalog.All"/> once (idempotent).</summary>
    public static void AddTo(List<ScrapazonItem> all)
    {
        if (all == null) return;
        foreach (var it in all) if (it != null && it.id == GripGloves) return;
        all.Add(Up(GripGloves, "gear", "Grippo Workwear",
            new[] { 250f, 1500f }, new[] { 2, 5 },
            new[] { "Grip Gloves (Pre-Slimed for Extra Stick)", "Octopus Grip Gloves" },
            new[] { "Hold on to the truck. And to your dreams.", "Eight arms' worth of grip. Two arms' worth of you." },
            new[] { "Riding the truck: your grip drains 25% slower.", "Riding the truck: your grip drains 45% slower." },
            399f, 4.6f, 2210, "#1 Best Seller in Hanging On",
            new[]
            {
                R(5, "my driver took a corner at 40. i'm still on. i live here now", "Rosa"),
                R(4, "they're sticky. everything is sticky. the truck is mine now", "Big Lou"),
                R(2, "still got flung. landed in a hedge. hedge gave 5 stars", "Ferris"),
            }));
        all.Add(Up(PaddedStep, "truck", "Cushy Commutes Inc.",
            new[] { 300f, 1200f }, new[] { 2, 4 },
            new[] { "Padded Rear Step (Memory Foam)", "Hydraulic Comfort Step (Has Cupholder)" },
            new[] { "Potholes? Never heard of them.", "Bumps become gentle suggestions." },
            new[] { "Bumps, curbs and crashes cost riders 35% less grip.", "Bumps, curbs and crashes cost riders 60% less grip." },
            499f, 4.4f, 980, null,
            new[]
            {
                R(5, "hit a speed bump at 30. my crew didn't even spill their coffee", "Dee"),
                R(4, "the cupholder holds a raccoon perfectly", "Marv"),
            }));
        all.Add(Up(SafetyBar, "truck", "Bonk Safety Supply",
            new[] { 600f }, new[] { 3 },
            new[] { "Fold-Down Safety Bar (Optional Safety)" },
            new[] { "Like a rollercoaster, but it smells." },
            new[] { "While the bar is down nobody is flung off this truck below 8 m/s. Riders press B to flip it up (for the clips)." },
            999f, 4.2f, 640, null,
            new[]
            {
                R(5, "my crew is finally safe. they keep flipping it up anyway", "Safety Steve"),
                R(3, "worked great until i put it up to film a tiktok", "Kayla"),
                R(5, "rated for 1 kevin", "Bonk Safety QA"),
            }));
    }
}

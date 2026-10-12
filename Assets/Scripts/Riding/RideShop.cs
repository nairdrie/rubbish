using System.Collections.Generic;

/// <summary>
/// F1's Scrapazon upgrades (Docs/Multiplayer.md §14.1). Same model as every other upgrade: a level in the business's save
/// (<see cref="GameSession.GetUpgradeLevel"/>), bought through <see cref="ScrapazonStore.TryBuy"/> (Ledger.Spend). Riders
/// stay on unless the truck crashes, so both truck items raise how hard a crash must be to throw them off
/// (<see cref="RideTuning.CrashFlingDv"/>):
/// <list type="bullet">
/// <item><b>Padded Step</b> (truck): crashes need +1.5 / +3 m/s more Δv to fling anyone off this truck.</item>
/// <item><b>Safety Bar</b> (truck): while the bar is down nobody riding it is flung by a crash under 8 m/s; riders flip it
/// with B.</item>
/// <item><b>Grip Gloves</b>: retired 12 Oct 2026 with the grip meter. No longer sold; the id stays so a save that bought
/// them still loads (the level just isn't read).</item>
/// </list>
/// A truck's mods come from its business (the owner's machine reports them; <see cref="HandsNet"/> shares them in co-op).
/// </summary>
public static class RideShop
{
    public const string GripGloves = "grip_gloves", PaddedStep = "padded_step", SafetyBar = "safety_bar";

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
        foreach (var it in all) if (it != null && it.id == PaddedStep) return;
        all.Add(Up(PaddedStep, "truck", "Cushy Commutes Inc.",
            new[] { 300f, 1200f }, new[] { 2, 4 },
            new[] { "Padded Rear Step (Memory Foam)", "Hydraulic Comfort Step (Has Cupholder)" },
            new[] { "Fender benders? Never heard of them.", "Crashes become gentle suggestions." },
            new[] { "Riders hang on through harder crashes: a hit needs 1.5 m/s more to fling anyone off this truck.",
                    "Riders hang on through harder crashes: a hit needs 3 m/s more to fling anyone off this truck." },
            499f, 4.4f, 980, null,
            new[]
            {
                R(5, "backed into a lamp post at 20. my crew didn't even spill their coffee", "Dee"),
                R(4, "the cupholder holds a raccoon perfectly", "Marv"),
            }));
        all.Add(Up(SafetyBar, "truck", "Bonk Safety Supply",
            new[] { 600f }, new[] { 3 },
            new[] { "Fold-Down Safety Bar (Optional Safety)" },
            new[] { "Like a rollercoaster, but it smells." },
            new[] { "While the bar is down nobody is flung off this truck by a crash under 8 m/s. Riders press B to flip it up (for the clips)." },
            999f, 4.2f, 640, null,
            new[]
            {
                R(5, "my crew is finally safe. they keep flipping it up anyway", "Safety Steve"),
                R(3, "worked great until i put it up to film a tiktok", "Kayla"),
                R(5, "rated for 1 kevin", "Bonk Safety QA"),
            }));
    }
}

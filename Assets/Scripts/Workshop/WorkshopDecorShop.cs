using System.Collections.Generic;

/// <summary>
/// Shop DECOR on Scrapazon (department "decor"): the eleven pieces of furniture and gadgets bought for cash. Same model as
/// every other upgrade: a level in the business's save (<c>decor_&lt;piece&gt;</c>, 0 or 1), bought through
/// <see cref="ScrapazonStore.TryBuy"/> (Ledger.Spend). Owning the product installs the piece: the owner's machine asks the
/// host to set the piece's <see cref="WorkshopState.completed"/> bit (<c>BusinessRegistry.TickWorkshop</c>, or
/// <see cref="WorkshopRunner"/> outside a session), and a piece that was already installed before decor moved here shows as
/// owned (<see cref="WorkshopState.CallingUpgrades"/>). Prices and names come from <see cref="WorkshopCatalog"/>.
/// The items carry dept "cosmetics" so shop logic treats them as optional sinks (never a "best next buy"), and the single
/// tag "decor" so they sit in their own aisle.
/// </summary>
public static class WorkshopDecorShop
{
    public const string Tag = "decor";

    static ScrapazonReview R(int s, string t, string a) => new ScrapazonReview(s, t, a);

    sealed class Copy
    {
        public string title, brand, blurb, effect, badge; public float rating; public int count; public ScrapazonReview[] reviews;
    }

    // Shop copy only. What a piece is, what it costs and its saved bit live in WorkshopCatalog.
    static readonly Dictionary<string, Copy> copy = new Dictionary<string, Copy>
    {
        { "bottle_cabinet", new Copy { title = "Bottle Cabinet (Glass Front, Some Assembly)", brand = "Shiny Things Showcase Co.",
            blurb = "A little glass-front cabinet for one favourite find.", effect = "Installed in your garage. Press E at it to pick the valuable it shows (it stays in your inventory).",
            rating = 4.3f, count = 812, reviews = new[] { R(5, "put my moon rock in it. it looks expensive now", "Pip"), R(3, "glass front. glass sides. glass everything. fingerprints: mine", "Greta") } } },
        { "rotating_pedestal", new Copy { title = "Rotating Pedestal (Turns Slowly, Judges Quickly)", brand = "Shiny Things Showcase Co.",
            blurb = "One treasured find, turning forever under its own spotlight.", effect = "Installed in your garage. Slowly turns one valuable: press E at it to pick which.",
            rating = 4.6f, count = 440, reviews = new[] { R(5, "i watched a lava lamp rotate for an hour. no regrets", "Dee"), R(4, "the motor hums a song. i know the song now", "Marv") } } },
        { "wall_display", new Copy { title = "Wall Display (Above the Mess)", brand = "Nailed It Carpentry",
            blurb = "Get a favourite find up on the wall, clear of the sorting floor.", effect = "Installed on your garage wall. Press E at it to pick the valuable it shows.",
            rating = 4.1f, count = 655, reviews = new[] { R(4, "four nails this time. progress", "Honest Abe's Lumber"), R(5, "my trophy is finally higher than magnus can reach", "Rosa") } } },
        { "grand_display", new Copy { title = "Grand Display (Illuminated, Slightly Much)", brand = "Shiny Things Showcase Co.", badge = "#1 Best Seller in Showing Off",
            blurb = "A grand, lit centrepiece for the best thing you ever pulled out of a bag.", effect = "Installed in your garage: a big illuminated centrepiece for one valuable. Press E at it to pick which.",
            rating = 4.8f, count = 203, reviews = new[] { R(5, "visitors gasp. it's a tin robot. they still gasp", "Big Lou"), R(4, "electricity bill went up. worth it", "Kayla") } } },
        { "business_sign", new Copy { title = "Custom Business Sign (Your Name in Lights)", brand = "Bright Idea Signage",
            blurb = "Your business name in lights, in a colour you pick.", effect = "Installed outside your depot. Press E at it to pick one of 8 colours or rename your business.",
            rating = 4.5f, count = 1930, reviews = new[] { R(5, "people can finally find us. this is a mixed blessing", "Ferris"), R(4, "one letter flickers. it's the important one", "Safety Steve") } } },
        { "bag_hoop", new Copy { title = "Bag Hoop (Regulation-ish)", brand = "Bonk Sporting Goods",
            blurb = "A trick-shot target for your forecourt. Bragging rights not included. (They are included.)", effect = "Installed outside your depot. Toss a bag through it for style.",
            rating = 4.7f, count = 1288, reviews = new[] { R(5, "nothing but net. the bag burst. still counts", "Marv"), R(4, "productivity is down. morale is UP", "Dee") } } },
        { "sorting_chute", new Copy { title = "Sorting Chute (Hand Crank, No Batteries)", brand = "Belts & Braces Conveyor Co.",
            blurb = "Crank it and floor bags slide onto the sorting table.", effect = "Installed in your garage. Crank to feed up to three floor bags onto the sorting table; it pauses when the table is full.",
            rating = 4.4f, count = 371, reviews = new[] { R(5, "i haven't bent down in a week. my knees sent a thank-you card", "Greta"), R(4, "the crank squeaks. the raccoons have learned what it means", "Pip") } } },
        { "break_sofa", new Copy { title = "Break Sofa (Pre-Sat-In)", brand = "Kerbside Comforts",
            blurb = "A patched-up sofa where the crew can take a breather.", effect = "Installed in your garage. Sit down. You've earned it. Probably.",
            rating = 4.2f, count = 2240, reviews = new[] { R(5, "found three remotes and a sandwich in it. keeping all four", "Big Lou"), R(3, "smells like a kerb. because it was on one", "Rosa") } } },
        { "scrap_radio", new Copy { title = "Scrap Radio (Enormous, On/Off Only)", brand = "Funky Junk Lighting",
            blurb = "An oversized homemade radio with exactly one control.", effect = "Installed in your garage. Switch it on. Switch it off. That is the whole radio.",
            rating = 4.5f, count = 904, reviews = new[] { R(5, "it only gets one station. the station is good", "Kayla"), R(4, "magnus nods along. i think. hard to tell with an arm", "Ferris") } } },
        { "spring_launcher", new Copy { title = "Spring Launcher (Do Not Stand On)", brand = "Bonk Sporting Goods", badge = "#1 Best Seller in Bad Ideas",
            blurb = "Load a bag. Step back. Further than that.", effect = "Installed outside your depot, aimed at the Bag Hoop's spot. Load a collected bag and send it flying with a spring-powered launch.",
            rating = 4.9f, count = 156, reviews = new[] { R(5, "the bag went over the depot. we never found the bag", "Marv"), R(5, "i stood on it. the reviews were right", "Safety Steve") } } },
        { "doorbell", new Copy { title = "Ridiculous Doorbell (Cheerful, Unnecessary)", brand = "Bright Idea Signage",
            blurb = "A doorbell for a building with a garage door.", effect = "Installed at your depot. Visitors can ring it. They will. Repeatedly.",
            rating = 4.0f, count = 1477, reviews = new[] { R(5, "ding dong. ding dong. ding dong. i am the visitor now", "Dee"), R(2, "my neighbour found it", "Greta") } } },
    };

    /// <summary>Appended to <see cref="ScrapazonCatalog.All"/> once (idempotent), in <see cref="WorkshopCatalog"/> order of price.</summary>
    public static void AddTo(List<ScrapazonItem> all)
    {
        if (all == null) return;
        foreach (var it in all) if (it != null && WorkshopCatalog.DecorFor(it.id) != null) return;
        var decor = new List<WorkshopRecipe>();
        foreach (var recipe in WorkshopCatalog.All) if (recipe.isDecor) decor.Add(recipe);
        decor.Sort((a, b) => a.price != b.price ? a.price.CompareTo(b.price) : a.index.CompareTo(b.index));
        foreach (var recipe in decor)
        {
            if (!copy.TryGetValue(recipe.id, out var c)) c = new Copy { title = recipe.name, brand = Scrapazon.Name, blurb = recipe.description, effect = "Installed in your garage.", rating = 4f, count = 1, reviews = new ScrapazonReview[0] };
            all.Add(new ScrapazonItem
            {
                id = recipe.upgradeId, kind = ShopItemKind.Upgrade, dept = "cosmetics", tags = new[] { Tag }, brand = c.brand,
                prices = new[] { (float)recipe.price }, maxLevel = 1, rankRequired = new[] { 1 },
                title = c.title, levelTitles = new[] { c.title }, levelBlurbs = new[] { c.blurb }, blurb = c.blurb, levelEffects = new[] { c.effect },
                listPrice = recipe.price * 1.6f - 0.01f, rating = c.rating, ratingCount = c.count, badge = c.badge, reviews = c.reviews,
            });
        }
    }
}

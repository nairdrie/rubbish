using System.Collections.Generic;

/// <summary>
/// The Hoverhog on Scrapazon (Docs/Progression.md §6.10), department "air". The drone itself comes with the helipad, the
/// stairwell and the roof hatch (stage 1); stage 2 adds its four plain upgrade lines (belly, claw to beam, rotors,
/// battery) and the satellite pads. Ropes, the cannon, the brain line and the Mothership are later stages.
/// </summary>
public static class HogShop
{
    public const string Dept = "air";
    public const string Hoverhog = "hoverhog";
    public const string Belly = "hog_belly", Claw = "hog_claw", Rotors = "hog_rotors", Battery = "hog_battery", SatPad = "hog_sat_pad";

    static string[] P(params string[] ids) => ids;

    static ScrapazonItem Line(string id, string brand, float[] prices, int[] ranks, string[] titles, string[] blurbs, string[] effects,
                              float list, float rating, int count, string badge, string[][] prereqs, ScrapazonReview[] reviews)
    {
        return new ScrapazonItem
        {
            id = id, kind = ShopItemKind.Upgrade, dept = Dept, tags = new[] { Dept }, brand = brand,
            prices = prices, maxLevel = prices.Length, rankRequired = ranks, prereqs = prereqs,
            title = titles[0], levelTitles = titles, blurb = blurbs[0], levelBlurbs = blurbs, levelEffects = effects,
            listPrice = list, rating = rating, ratingCount = count, badge = badge, reviews = reviews,
        };
    }

    static ScrapazonReview R(int s, string t, string a) => new ScrapazonReview(s, t, a);

    /// <summary>Appended to <see cref="ScrapazonCatalog.All"/> once (idempotent).</summary>
    public static void AddTo(List<ScrapazonItem> all)
    {
        if (all == null) return;
        foreach (var it in all) if (it != null && it.id == Hoverhog) return;
        all.Add(new ScrapazonItem
        {
            id = Hoverhog, kind = ShopItemKind.Upgrade, dept = Dept, tags = new[] { Dept, "truck" },
            brand = $"{Scrapazon.Name} Prime Air",
            prices = new[] { 85000f }, maxLevel = 1, rankRequired = new[] { 11 },
            prereqs = new[] { new[] { ShopIds.TruckArm + ":2", ShopIds.PermitBillionaireBluffs } },
            title = "HOVERHOG H-1 Heavy Garbage Drone (Roof Not Included. Wait, Roof Included)",
            levelTitles = new[] { "HOVERHOG H-1 Heavy Garbage Drone (Roof Not Included. Wait, Roof Included)" },
            blurb = "Eight rotors, one belly, zero licences required, apparently.",
            levelBlurbs = new[] { "Eight rotors, one belly, zero licences required, apparently." },
            levelEffects = new[]
            {
                "A second vehicle: your truck stays in the bay. Comes with a rooftop helipad, stairs from the shop and a roof hatch. " +
                $"{HogTuning.BellyKg:0} kg belly, a claw-machine Claw (hover over bags, press X), a bomb bay (hold G: over your roof it " +
                $"unloads into the shop, anywhere else it is litter) and {HogTuning.FlightSeconds / 60f:0} min of battery that charges on your pad.",
            },
            listPrice = 129999f, rating = 4.7f, ratingCount = 88, badge = "Delivered By Itself",
            reviews = new[]
            {
                R(5, "it delivered itself. then it delivered my neighbour's bins. to the sea. 5 stars", "Trash Tycoon Tina"),
                R(4, "the claw is exactly like the arcade one. i have lost $40 in feelings", "Gus"),
                R(5, "ran out of battery over a pool party. they clapped. i think they clapped", "Marv"),
                R(1, "saw a light. my bags are gone. i am not ok", "A Resident"),
            },
        });

        string air = $"{Scrapazon.Name} Prime Air";
        all.Add(Line(Belly, air, new[] { 15000f, 35000f, 70000f }, new[] { 11, 12, 12 },
            new[] { "Bigger Belly", "Compactor Gut", "Bottomless Pit (Has a Bottom)" },
            new[] { "It's not fat, it's storage.", "Chews before it swallows.", "We checked. There is a bottom. It is far away." },
            new[]
            {
                "Belly holds 450 kg (was 300).",
                "Belly holds 600 kg, and thumps every bag on the way in.",
                "Belly holds 850 kg. The bomb bay empties any load in 3 seconds, and a TAP of G drops just one bag.",
            },
            99999f, 4.6f, 311, null,
            new[] { P(Hoverhog), P(Belly + ":1"), P(Belly + ":2") },
            new[]
            {
                R(5, "it ate a whole cul-de-sac and asked for seconds", "Dee"),
                R(4, "one bag at a time, right onto my friend's lot. precision littering", "Ferris"),
                R(2, "landed on a shed. the shed lost", "Marv"),
            }));
        all.Add(Line(Claw, "Grabby Industries Aerospace", new[] { 20000f, 75000f, 160000f }, new[] { 11, 12, 12 },
            new[] { "Three-Finger Discount Claw", "UFO Beam (Totally Not Alien Technology)", "Mothership-Grade Beam" },
            new[] { "Now with a fourth finger. We can't count either.", "Residents report 'a light' and 'my bins are gone'.", "Now with crop circles." },
            new[]
            {
                "The Claw grabs up to 3 bags a drop in 2.5 seconds, and never slips.",
                "Replaces the Claw. HOLD X at a slow hover (under 12 m): every curb bag and loose bit of litter within 6 m floats up into the belly, one every 0.6 s.",
                "The beam works while you crawl (up to 6 m/s), reaches 11 m, takes a bag every quarter second, and leaves crop circles on lawns.",
            },
            249999f, 4.9f, 1947, "#1 Best Seller in Unexplained Lights",
            new[] { P(Hoverhog), P(Claw + ":1"), P(Claw + ":2", ShopIds.KeyToTheCity) },
            new[]
            {
                R(5, "i hover. bags rise. i feel like a god of small plastic sacks", "Gus"),
                R(5, "the crop circles are tasteful", "Kayla"),
                R(1, "it took my gnome. it gave him back. he's different now", "A Resident"),
                R(4, "raccoon tried to ride the beam up. respect", "Nadia"),
            }));
        all.Add(Line(Rotors, "Tony's Lift & Thrust", new[] { 12000f, 30000f, 60000f }, new[] { 11, 12, 12 },
            new[] { "More Rotors", "Even More Rotors", "Jet Pods (the Garbage Still Gets Warm)" },
            new[] { "Tony says more is more.", "Tony was right.", "Tony is no longer allowed near the airport." },
            new[]
            {
                "Top speed 15 m/s (was 12).",
                "Top speed 18 m/s.",
                "Top speed 22 m/s, a 60 m ceiling, and SHIFT fires the jets: +6 m/s for 3 seconds (15 s to cool down).",
            },
            89999f, 4.5f, 702, null,
            new[] { P(Hoverhog), P(Rotors + ":1"), P(Rotors + ":2") },
            new[]
            {
                R(5, "faster than my truck. faster than my problems", "Cyrus"),
                R(3, "the jets set my hat on fire. it was a nice hat", "Big Lou"),
            }));
        all.Add(Line(Battery, "Zappo Cells", new[] { 10000f, 25000f, 55000f }, new[] { 11, 12, 12 },
            new[] { "Extended Pack", "Car Battery (Several)", "Garbage-Juice Reactor" },
            new[] { "Like the old pack, but there's more of it.", "Jump leads included. Do not jump.", "Runs on trash. Finally." },
            new[]
            {
                "8 minutes of flight (was 5).",
                "12 minutes of flight.",
                "HOLD F in the air to burn belly load for charge: 1 kg = 3 seconds of flight, lightest bag first. A burned bag was still paid for at the curb; whatever was in it is gone.",
            },
            79999f, 4.3f, 455, null,
            new[] { P(Hoverhog), P(Battery + ":1"), P(Battery + ":2", Belly + ":2") },
            new[]
            {
                R(5, "it runs on garbage now. so do i", "Rosa"),
                R(2, "burned a bag. it had a wedding ring in it. the reactor says thank you", "Safety Steve"),
                R(4, "glows a bit. probably fine", "Zappo QA"),
            }));
        all.Add(Line(SatPad, air, new[] { 10000f, 10000f, 10000f }, new[] { 11, 11, 11 },
            new[] { "Satellite Pad: Minivan Meadows", "Satellite Pad: Snootington Hills", "Satellite Pad: Billionaire Bluffs" },
            new[] { "A second place to sit down.", "The HOA has been informed.", "The butler allows it." },
            new[]
            {
                "A charging pad in Minivan Meadows. Land on it to charge, exactly like your roof.",
                "A charging pad in Snootington Hills.",
                "A charging pad in Billionaire Bluffs.",
            },
            14999f, 4.4f, 96, null,
            new[] { P(Hoverhog, ShopIds.PermitMinivanMeadows), P(SatPad + ":1", ShopIds.PermitSnootingtonHills), P(SatPad + ":2", ShopIds.PermitBillionaireBluffs) },
            new[]
            {
                R(5, "landed, charged, left. the neighbours filmed it", "Trash Tycoon Tina"),
                R(3, "someone else's hog was on it. awkward", "Gus"),
            }));
    }
}

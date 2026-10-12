using UnityEngine;

public static partial class ItemCatalog
{
    /// <summary>
    /// The Keepsake bonus: a lucky bag (profile.keepsakeChance) gets ONE extra Common or Uncommon valuable, in the same
    /// proportion as the normal pool (<see cref="CommonShare"/>). It is added, never swapped in, so the bag's original contents
    /// are untouched, and it never produces Rare+ outcomes.
    /// </summary>
    static void ApplyKeepsakeBonus(BagContents bag, int seed, float houseWealth, Theme theme, LootProfile profile, string foundAt)
    {
        if (profile == null || profile.keepsakeChance <= 0f) return;
        // Runs only after all original draws, minimum-weight filling, guarantees, shuffling and personality shaping,
        // on its own RNG lane so it never consumes or re-evaluates the bag's own stream.
        var extra = new System.Random(unchecked(seed ^ 0x49bc71ad)); // coop:allow(random) deterministic lane derived from the authoritative bag seed, never local time
        if (extra.NextDouble() >= profile.keepsakeChance) return;
        var rarity = extra.NextDouble() < CommonShare(houseWealth, profile) ? Rarity.Common : Rarity.Uncommon;
        var def = WeightedPick(extra, d => d.category == ItemCategory.Valuable && d.rarity == rarity, ThemeTag(theme), theme, profile);
        if (def == null) return;
        var find = RollItem(def, extra, foundAt);
        if (bag.TotalMassKg + find.massKg > MaxBagKg) return;
        bag.items.Insert(extra.Next(bag.items.Count + 1), find);
    }

    // One unique mesh key per new collectible. Existing U/R/E/L normal-loot variety grows by
    // approximately one quarter (5/4/4/4 additions); Common gets its first eight real keepsakes.
    static void BuildCollectibleDefs()
    {
        const Rarity C = Rarity.Common, U = Rarity.Uncommon, R = Rarity.Rare, E = Rarity.Epic, L = Rarity.Legendary;
        Val("bobble_pigeon", "Bobblehead Pigeon", C, 0.2f, 0.35f, 4f, 12f, "find_pigeon", 0x93A5AE, 1f, "old", "Nods along with every terrible decision you make.");
        Val("windup_snail", "Wind-Up Racing Snail", C, 0.15f, 0.3f, 5f, 15f, "find_snail", 0xE7A745, 1f, "old", "Personal best: the other side of the desk. Tuesday.");
        Val("ceramic_cactus", "Ceramic Cactus in Shades", C, 0.35f, 0.65f, 4f, 14f, "find_cactus", 0x64A657, 1f, "fragile", "Finally, a plant you can neglect professionally.");
        Val("newton_cradle", "Newton's Mildly Annoying Cradle", C, 0.4f, 0.7f, 6f, 18f, "find_cradle", 0xD4DDE0, 1f, "old", "Conserves momentum. Destroys concentration.");
        Val("mini_lighthouse", "Souvenir Lighthouse", C, 0.25f, 0.5f, 5f, 16f, "find_lighthouse", 0xE15E4F, 1f, "fragile", "Guides tiny boats away from your unpaid bills.");
        Val("squeaky_frog", "Frog Prince Desk Ornament", C, 0.2f, 0.4f, 4f, 13f, "find_frog", 0x75B73A, 1f, "", "One kiss from royalty. Several washes from hygienic.");
        RichVal(2, "seashell_clock", "Seashell Alarm Clock", C, 0.35f, 0.65f, 7f, 19f, "find_shellclock", 0xEDBCA2, 1f, "fragile", "Ocean sounds not included. Ticking, unfortunately, is.");
        RichVal(3, "desk_planetarium", "Pocket Planetarium", C, 0.4f, 0.7f, 8f, 22f, "find_planetarium", 0x295C91, 1f, "old", "Contains eight planets and one very bitter dwarf.");

        Val("rotary_desk_phone", "Mustard Rotary Telephone", U, 0.9f, 1.5f, 15f, 45f, "find_phone", 0xD3A337, 1f, "old", "Dial nine. Regret nine. Start again.");
        Val("tin_space_robot", "Tin Space Patrol Robot", U, 0.4f, 0.8f, 18f, 55f, "find_robot", 0x68A6B8, 1f, "old", "Defends the galaxy at approximately three steps a minute.");
        Val("ship_in_bottle", "Ship in a Bottle", U, 0.5f, 0.9f, 20f, 60f, "find_bottleship", 0x88CAD1, 1f, "old,fragile", "The captain has been looking for the exit since 1978.");
        Val("pocket_accordion", "Pocket Accordion", U, 0.8f, 1.4f, 15f, 50f, "find_accordion", 0xAD3440, 1f, "old", "For emergencies that require a polka.");
        RichVal(2, "neon_mushroom_lamp", "Neon Mushroom Lamp", U, 0.6f, 1.0f, 30f, 95f, "find_mushroom", 0xCB6FE8, 1f, "fragile", "Do not forage the lighting department.");

        Val("astronaut_helmet", "Junior Astronaut Helmet", R, 0.6f, 1.1f, 75f, 220f, "find_helmet", 0xE8E4D7, 1f, "old,fragile", "Approved for space. Not approved BY space.");
        Val("antique_sextant", "Brass Captain's Sextant", R, 0.3f, 0.6f, 90f, 260f, "find_sextant", 0xD2AD57, 1f, "old", "Finds your latitude. Cannot find where you parked.");
        RichVal(2, "tabletop_jukebox", "Tabletop Diner Jukebox", R, 1.5f, 2.5f, 180f, 480f, "find_jukebox", 0xCB3E45, 1f, "old,fragile", "Every selection plays the same heartbreak song.");
        RichVal(3, "clockwork_orrery", "Clockwork Brass Orrery", R, 1.0f, 2.0f, 320f, 850f, "find_orrery", 0xC7A049, 1f, "old,fragile", "A whole solar system that needs winding on Mondays.");

        Val("meteorite_magnet", "Floating Meteorite Display", E, 0.5f, 1.0f, 350f, 1100f, "find_meteorite", 0x3A4662, 1f, "fragile", "Suspended by magnets. Marketed as forbidden science.");
        RichVal(2, "crystal_skull", "Prismatic Crystal Skull", E, 0.8f, 1.5f, 650f, 2100f, "find_skull", 0x7ADCD0, 1f, "fragile", "The ancient curse mostly affects your credit score.");
        RichVal(3, "mechanical_songbird", "Mechanical Gilded Songbird", E, 1.0f, 1.8f, 1300f, 3800f, "find_songbird", 0xE6C056, 1f, "old,fragile", "Sings beautifully. Knows exactly one song.");
        RichVal(4, "mini_submersible", "Billionaire's Desktop Submersible", E, 2f, 3f, 3000f, 9000f, "find_submersible", 0xEDD66A, 1f, "fragile", "Rated to a depth of one executive aquarium.");

        Val("alien_teapot", "Unidentified Flying Teapot", L, 0.4f, 0.8f, 1700f, 4800f, "find_alienteapot", 0x90CCD1, 1f, "fragile", "We come in peace. Two sugars, please.");
        RichVal(3, "royal_hourglass", "The Royal Five-Minute Hourglass", L, 2f, 4f, 5500f, 15000f, "find_hourglass", 0xEDCE70, 0.8f, "old,fragile", "Five minutes for you. An eternity for the servants.");
        RichVal(4, "cosmic_music_box", "Celestial Mechanical Music Box", L, 4f, 7f, 18000f, 42000f, "find_musicbox", 0x263D89, 0.8f, "old,fragile", "Plays the music of the spheres. Comes with a tiny mute button.");
        RichVal(4, "bonsai_money_tree", "The Literal Money Tree", L, 6f, 10f, 20000f, 48000f, "find_moneytree", 0x6BC06D, 0.8f, "fragile", "The financial advisor said to diversify. So he bought a plant.");
    }
}

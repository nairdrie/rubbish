using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// All item definitions + loot rolling. Owned by the Items system. Public API is a FROZEN CONTRACT
/// (All, Get, RollBag, RollItem); everything else here is additive.
///
/// Loot model (per bag, deterministic from the seed, System.Random only), driven by a district LootProfile
/// (Docs/Progression.md §5.4; the frozen 4-argument RollBag uses the starter profile, Mildly Pleasant Acres):
///   - minItems..maxItems picks (5..10 in the starter suburb), bag total kept under ~18 kg (heavy picks are rerolled).
///   - each pick: valuable with p = (p0 + p1 * houseWealth) × profile.valuableMult (starter: 0.5% + 0.8%·w), else a
///     category by weight (starter: Junk 40 / Paper 18 / Plastic 15 / Metal 11 / Glass 10 / Hazard 4), then an item.
///   - valuable rarity weights lerp by wealth (starter: 84/14/1.7/0.3 at w=0 to 74/20/5/1 at w=1).
///   - only items with tier ≤ the profile tier drop; district valuables are weighted by profile.tierBoost[tier] and
///     district non-valuables ×3.
///   - ~18% of bags are themed (party, spring cleaning, new baby, garage cleanout) which reshapes the mix; tier 2+
///     districts add rich themes (divorce bag, moving day, influencer haul, estate clean-out, yacht party, butler
///     mix-up) whose chance scales with the district's Trust.
/// </summary>
public static partial class ItemCatalog
{
    static List<ItemDef> all;
    static Dictionary<string, ItemDef> byId;
    static Dictionary<string, Meta> meta;

    /// <summary>Loot-table metadata that doesn't belong on the frozen ItemDef.</summary>
    class Meta
    {
        public float weight = 1f;     // relative pick weight inside its category / rarity
        public string[] tags;         // "party", "baby", "garage", "old", "kitchen", "bulk", "garage_only", "fragile"
        public int tier = 1;          // earliest district tier the item appears in (1 = everywhere)
    }

    public static IReadOnlyList<ItemDef> All { get { Ensure(); return all; } }

    public static ItemDef Get(string id)
    {
        Ensure();
        return id != null && byId.TryGetValue(id, out var d) ? d : null;
    }

    static void Ensure()
    {
        if (all != null) return;
        all = new List<ItemDef>();
        meta = new Dictionary<string, Meta>();
        BuildDefs();
        BuildEventDefs();
        BuildCollectibleDefs(); // append only: DailyStats persists existing catalog indices
        byId = new Dictionary<string, ItemDef>();
        foreach (var d in all) byId[d.id] = d;
    }

    // ------------------------------------------------------------------ definitions

    static Color C(int hex) => new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

    static void Add(string id, string name, ItemCategory cat, float minKg, float maxKg, string visual, int color,
        float weight, string tags, string flavor)
    {
        all.Add(new ItemDef
        {
            id = id, name = name, category = cat, rarity = Rarity.Common,
            minMass = minKg, maxMass = maxKg, visual = visual, color = C(color), flavor = flavor
        });
        meta[id] = new Meta { weight = weight, tags = string.IsNullOrEmpty(tags) ? new string[0] : tags.Split(',') };
    }

    static void Val(string id, string name, Rarity rarity, float minKg, float maxKg, float minValue, float maxValue,
        string visual, int color, float weight, string tags, string flavor)
    {
        all.Add(new ItemDef
        {
            id = id, name = name, category = ItemCategory.Valuable, rarity = rarity,
            minMass = minKg, maxMass = maxKg, minValue = minValue, maxValue = maxValue,
            visual = visual, color = C(color), flavor = flavor
        });
        meta[id] = new Meta { weight = weight, tags = string.IsNullOrEmpty(tags) ? new string[0] : tags.Split(',') };
    }

    static void BuildDefs()
    {
        const ItemCategory J = ItemCategory.Junk, PL = ItemCategory.Plastic, GL = ItemCategory.Glass,
            ME = ItemCategory.Metal, PA = ItemCategory.Paper, HZ = ItemCategory.Hazard;

        // ---------------- JUNK (compactor) ----------------
        Add("banana_peel", "Banana Peel", J, 0.1f, 0.25f, "peel", 0xF2D530, 3f, "kitchen", "Slippery. Historically.");
        Add("pizza_box", "Greasy Pizza Box", J, 0.4f, 0.9f, "pizzabox", 0xC89B62, 3f, "party,kitchen", "Too greasy to recycle. Too sad to look at.");
        Add("diaper", "Used Diaper", J, 0.4f, 0.9f, "diaper", 0xF4F1E6, 2f, "baby", "Heavier than it has any right to be. (ew)");
        Add("chip_bag", "Empty Chip Bag", J, 0.03f, 0.06f, "chips", 0xFF7A1A, 3f, "party", "Ninety percent air. Was ninety percent air when they sold it, too.");
        Add("coffee_grounds", "Coffee Grounds", J, 0.6f, 1.4f, "grounds", 0x4A2E1C, 3f, "kitchen,bulk", "Still smells better than everything else in here.");
        Add("broken_umbrella", "Broken Umbrella", J, 0.4f, 0.7f, "umbrella", 0x2A2A38, 1f, "old", "Lost a fight with the wind. The wind is undefeated.");
        Add("lone_sock", "Lone Sock", J, 0.04f, 0.08f, "sock", 0xE8E8E0, 2f, "", "Its partner is out there somewhere. Waiting.");
        Add("mystery_leftovers", "Mystery Leftovers", J, 0.8f, 2.0f, "leftovers", 0x7A9A3A, 3f, "kitchen,bulk", "Smells like a science fair.");
        Add("fish_bones", "Fish Bones", J, 0.2f, 0.5f, "bones", 0xEDE6D0, 1.5f, "kitchen", "The fish is having a worse day than you.");
        Add("gross_toothbrush", "Gross Toothbrush", J, 0.02f, 0.04f, "toothbrush", 0x3FA7F5, 1f, "", "Bristles pointing in every direction, like a startled cat.");
        Add("cat_litter", "Cat Litter Clump", J, 1.2f, 3.0f, "litter", 0x9A958C, 2.5f, "bulk", "Do not think about it. Just sort it.");
        Add("broken_action_figure", "Broken Action Figure", J, 0.1f, 0.3f, "toy", 0x3C7D3A, 1.5f, "old", "Lost an arm defending the sandbox. A hero.");
        Add("used_tissues", "Used Tissues", J, 0.1f, 0.3f, "tissue", 0xF7F7F2, 3f, "", "A whole cold's worth of memories.");
        Add("apple_core", "Apple Core", J, 0.04f, 0.08f, "core", 0xD8342C, 2f, "kitchen", "Someone ate around the good part.");
        Add("burnt_toast", "Burnt Toast", J, 0.04f, 0.08f, "toast", 0x2B1A10, 2f, "kitchen", "The smoke alarm's favorite snack.");
        Add("single_croc", "A Single Croc", J, 0.15f, 0.25f, "shoe", 0x8BD13A, 1f, "", "Holes: 13. Dignity: 0. Where is the other one.");
        Add("broken_vhs", "Broken VHS Tape", J, 0.2f, 0.3f, "vhs", 0x1C1C20, 1f, "old", "'HOME MOVIES - DO NOT TAPE OVER'. Someone taped over it.");
        Add("grass_clippings", "Grass Clippings", J, 2.0f, 5.0f, "clippings", 0x58A63A, 2f, "bulk", "Smells like a dad's Saturday.");
        Add("soggy_towel", "Soggy Towel", J, 1.0f, 2.0f, "towel", 0x2F9C9A, 1f, "bulk", "Wet. Why is it wet. It's been in a bag.");
        Add("moldy_lemon", "Moldy Lemon", J, 0.1f, 0.15f, "prefab:lemon", 0xF5E13A, 1f, "kitchen,party", "When life gives you lemons, eventually this happens.");
        Add("bald_tire", "Bald Tire", J, 7f, 9f, "prefab:tire", 0x202022, 0.35f, "garage,garage_only", "Who puts a TIRE in a trash bag?? Somebody did.");
        Add("party_hat", "Squashed Party Hat", J, 0.03f, 0.06f, "partyhat", 0xE8398F, 1f, "party", "Somebody had a great time. Briefly.");
        Add("broken_boss_mug", "#1 Boss Mug (Broken)", J, 0.3f, 0.4f, "mug", 0xF2F2EE, 1f, "kitchen", "The boss was, in fact, not #1.");
        Add("crusty_sponge", "Crusty Sponge", J, 0.04f, 0.08f, "sponge", 0xF2D43A, 2f, "kitchen", "Has seen things. Has cleaned none of them.");
        Add("baby_wipes", "Wad of Baby Wipes", J, 0.3f, 0.8f, "wipes", 0x9AD2F0, 1f, "baby", "Used for everything except babies, probably.");
        Add("knotted_baggie", "Mystery Knotted Baggie", J, 0.3f, 0.6f, "baggie", 0x2E4A2A, 1.2f, "", "Double knotted. Respect the knot. Do not open the knot.");
        Add("mystery_sludge", "Tub of Mystery Sludge", J, 1.5f, 3.0f, "sludge", 0x6A5A2A, 2f, "kitchen,bulk", "Nobody knows what it was. Nobody wants to find out.");
        Add("old_pumpkin", "Saggy Old Pumpkin", J, 2.0f, 4.5f, "pumpkin", 0xF08A1E, 0.8f, "bulk", "Carved in October. It is not October.");
        Add("broken_flowerpot", "Broken Flower Pot", J, 0.8f, 2.0f, "flowerpot", 0xC8643A, 1f, "old", "The plant didn't make it. Neither did the pot.");
        Add("potato_peels", "Heap of Potato Peels", J, 0.6f, 1.6f, "peels", 0xC8A060, 2f, "kitchen,bulk", "Somebody made a LOT of mashed potatoes. Respect.");
        Add("duct_tape", "Nearly-Empty Duct Tape", J, 0.1f, 0.2f, "prefab:duct_tape", 0x9AA0A6, 0.8f, "garage", "Held a car together for three years. Retired with honors.");

        // ---------------- PLASTIC ----------------
        Add("water_bottle", "Crinkled Water Bottle", PL, 0.03f, 0.05f, "bottle", 0x9CD2FF, 3f, "party", "Half full. Of what, though?");
        Add("milk_jug", "Milk Jug", PL, 0.1f, 0.6f, "jug", 0xF4F4EE, 2f, "kitchen", "Expired in a previous administration.");
        Add("soda_2l", "2-Liter Soda Bottle", PL, 0.05f, 0.1f, "bottle2l", 0x6FD37A, 2f, "party", "Flat. Like your jokes.");
        Add("yogurt_tub", "Yogurt Tub", PL, 0.03f, 0.06f, "tub", 0xF7F2FF, 2f, "kitchen", "Licked clean. Suspiciously clean.");
        Add("red_cups", "Stack of Red Cups", PL, 0.06f, 0.15f, "cups", 0xD9262B, 2f, "party", "Beer pong's fallen soldiers.");
        Add("detergent_jug", "Detergent Jug", PL, 0.3f, 1.0f, "jug", 0xFF8A1F, 1.5f, "", "Smells aggressively of 'Mountain Breeze'.");
        Add("shampoo_bottle", "Shampoo Bottle", PL, 0.05f, 0.12f, "shampoo", 0xF06FA8, 1.5f, "", "'Rinse and repeat.' They repeated.");
        Add("baby_bottle", "Baby Bottle", PL, 0.05f, 0.1f, "babybottle", 0x8FCBF5, 1f, "baby", "Nipple chewed flat. Tiny menace.");
        Add("rubber_duck", "Rubber Duck", PL, 0.05f, 0.1f, "duck", 0xFFD21F, 0.8f, "baby", "Squeaks when sad. Squeaked a lot.");
        Add("takeout_clamshell", "Takeout Clamshell", PL, 0.03f, 0.06f, "clamshell", 0xF0F0EA, 2f, "party,kitchen", "Contains one (1) fry. Guarding it.");
        Add("bag_of_bags", "Bag of Bags", PL, 0.08f, 0.25f, "bagofbags", 0xF2F2F2, 1.5f, "", "A plastic bag stuffed with plastic bags. The circle of life.");

        // ---------------- GLASS ----------------
        Add("beer_bottle", "Beer Bottle", GL, 0.2f, 0.35f, "bottle", 0x6B3D12, 3f, "party", "Tuesday.");
        Add("wine_bottle", "Wine Bottle", GL, 0.45f, 0.7f, "wine", 0x1F4A2A, 2f, "party", "'For cooking.' They did not cook.");
        Add("pickle_jar", "Pickle Jar", GL, 0.3f, 0.55f, "jar", 0x7FA83A, 2f, "kitchen", "One pickle remains. Guarding the brine.");
        Add("jam_jar", "Jam Jar", GL, 0.2f, 0.35f, "jar", 0x7A1F4A, 1.5f, "kitchen", "Scraped with a spoon for twenty minutes. Worth it.");
        Add("baby_food_jar", "Baby Food Jar", GL, 0.1f, 0.16f, "jarsmall", 0xF0A040, 1f, "baby", "Flavor: 'Mashed Regret'.");
        Add("perfume_bottle", "Perfume Bottle", GL, 0.1f, 0.2f, "perfume", 0xFF8FC8, 0.8f, "", "Smells like a department store fought a flower.");
        Add("cracked_mirror", "Cracked Mirror", GL, 1.0f, 2.0f, "mirror", 0x8A5A30, 0.6f, "old", "Seven years bad luck. Six to go.");
        Add("hot_sauce_bottle", "Hot Sauce Bottle", GL, 0.15f, 0.25f, "sauce", 0xD9261C, 1f, "kitchen,party", "Label: 'EXTREME'. Bottle: empty. Mouth: gone.");
        Add("dead_lightbulb", "Dead Lightbulb", GL, 0.03f, 0.06f, "bulb", 0xFFF3B0, 1f, "", "Had one idea and it burned out.");

        // ---------------- METAL ----------------
        Add("soda_can", "Crushed Soda Can", ME, 0.015f, 0.02f, "can_crushed", 0xD91E1E, 4f, "party", "Somebody really wanted to crush it.");
        Add("beer_can", "Beer Can", ME, 0.015f, 0.02f, "can", 0x3C78D8, 3f, "party", "'Lite'. Heavy regrets.");
        Add("tuna_can", "Tuna Can", ME, 0.05f, 0.08f, "tuna", 0x2F6FD6, 2f, "kitchen", "Cat-approved. Cat-licked.");
        Add("soup_can", "Soup Can", ME, 0.06f, 0.1f, "tincan", 0xD8232A, 2f, "kitchen", "'Cream of Something.'");
        Add("frying_pan", "Frying Pan", ME, 1.2f, 2.2f, "pan", 0x2C2C30, 1f, "kitchen,old", "Bonk-rated. Egg-stained.");
        Add("broken_toaster", "Broken Toaster", ME, 1.5f, 2.5f, "toaster", 0xC8CCD2, 0.8f, "kitchen,old", "Only toasts one side. Of your hand.");
        Add("hubcap", "Hubcap", ME, 1.0f, 2.0f, "hubcap", 0xD6DAE0, 0.7f, "garage", "Fell off at 70 mph. Came here to rest.");
        Add("bike_chain", "Rusty Bike Chain", ME, 0.3f, 0.6f, "bikechain", 0x8A5A3A, 1f, "garage", "The bike is still out there somewhere. Chainless.");
        Add("coat_hangers", "Tangle of Coat Hangers", ME, 0.2f, 0.5f, "hangers", 0xB8BCC4, 1f, "old", "Impossible to untangle. Do not try. Many have tried.");
        Add("rusty_hammer", "Rusty Hammer", ME, 0.5f, 0.8f, "prefab:hammer", 0x8A5A3A, 0.6f, "garage", "Hit one nail. Missed the rest.");
        Add("dented_pot", "Dented Stock Pot", ME, 1.0f, 2.0f, "pot", 0xB0B6BE, 0.6f, "kitchen,old", "Survivor of the Great Chili Incident.");
        Add("formula_tin", "Baby Formula Tin", ME, 0.15f, 0.3f, "tin", 0x9CC8F0, 1f, "baby", "Baby's first all-you-can-eat buffet.");
        Add("empty_toolbox", "Empty Toolbox", ME, 2.5f, 4.5f, "prefab:toolbox", 0xD4231F, 0.35f, "garage", "Contains zero tools and one (1) mouse nest.");

        // ---------------- PAPER ----------------
        Add("newspaper", "Soggy Newspaper", PA, 0.3f, 0.8f, "newspaper", 0xE6E3D8, 3f, "", "Nobody reads these. Somebody still gets them.");
        Add("cardboard_box", "Cardboard Box", PA, 0.5f, 1.2f, "box", 0xC4935A, 3f, "", "Held a toaster once. Now holds nothing but dreams.");
        Add("junk_mail", "Junk Mail", PA, 0.1f, 0.3f, "mail", 0xF2F0E8, 3f, "", "'You may already be a winner!' You are not.");
        Add("egg_carton", "Egg Carton", PA, 0.03f, 0.06f, "carton", 0xB8B0A0, 2f, "kitchen", "Held a dozen. Now holds a smell.");
        Add("phone_book", "Phone Book", PA, 1.2f, 2.5f, "book", 0xF5D52A, 1.2f, "old,bulk", "A paper internet. Grandpa's search engine.");
        Add("kids_homework", "Kid's Homework", PA, 0.01f, 0.03f, "homework", 0xFBFBF6, 1f, "", "Graded 'A-'. Fridge-worthy, yet here we are.");
        Add("cereal_box", "Cereal Box", PA, 0.05f, 0.1f, "cerealbox", 0xE8362A, 2f, "kitchen", "'Frosted Sugar Bombs'. Part of this balanced breakfast.");
        Add("magazine_stack", "Stack of Magazines", PA, 1.0f, 2.5f, "magazines", 0x4A8AD8, 1.2f, "old,bulk", "Lawn Enthusiast Monthly. Fourteen issues. Unread.");
        Add("paper_bag", "Crumpled Paper Bag", PA, 0.03f, 0.06f, "paperbag", 0xB08452, 1.5f, "kitchen", "Once held a lunch. Loved by no one.");
        Add("wrapping_paper", "Wad of Wrapping Paper", PA, 0.1f, 0.3f, "wrap", 0xE0303A, 1f, "party", "Somebody got socks. Again.");

        // ---------------- HAZARD (hazard barrel; compacting one goes boom) ----------------
        Add("aa_battery", "AA Battery", HZ, 0.02f, 0.03f, "battery", 0xE08A1E, 4f, "", "Do NOT compact. Seriously.");
        Add("car_battery", "Car Battery", HZ, 13f, 17f, "prefab:car_battery", 0x1E1E22, 0.35f, "garage,garage_only", "Fifteen kilos of angry acid. Lift with your legs.");
        Add("spray_paint", "Spray Paint Can", HZ, 0.2f, 0.4f, "spray", 0x2FB84A, 1.5f, "garage", "Rattles like a maraca of doom when you shake it.");
        Add("old_flip_phone", "Old Flip Phone", HZ, 0.1f, 0.15f, "flipphone", 0xA8ACB4, 1.5f, "old", "3 missed calls from 2006.");
        Add("paint_tin", "Paint Tin", HZ, 1.0f, 3.0f, "prefab:paint_bucket", 0xE8E0C8, 1f, "garage", "Color: 'Eggshell Beige #47'. Mostly on the outside.");
        Add("fluorescent_tube", "Fluorescent Tube", HZ, 0.2f, 0.3f, "tube", 0xF4F8FF, 0.7f, "garage", "Flickers even when unplugged. Somehow.");
        Add("motor_oil", "Motor Oil Jug", HZ, 0.4f, 1.2f, "prefab:jerrycan", 0x4A5A2A, 0.8f, "garage", "Do not drink. We legally have to say that.");
        Add("propane_tank", "Camping Propane Tank", HZ, 0.5f, 1.2f, "prefab:propane", 0xF2F2F2, 0.5f, "garage", "It's probably empty. Probably.");
        Add("fire_extinguisher", "Expired Fire Extinguisher", HZ, 2.0f, 3.5f, "prefab:extinguisher", 0xD4231F, 0.4f, "garage", "Expired in 1994. Still extremely confident.");

        // ---------------- VALUABLES: UNCOMMON ($8-60) ----------------
        const Rarity U = Rarity.Uncommon, R = Rarity.Rare, E = Rarity.Epic, L = Rarity.Legendary;
        Val("garden_gnome", "Garden Gnome", U, 1.5f, 3f, 15f, 45f, "gnome", 0xD9262B, 1.2f, "old", "Has witnessed everything. Says nothing.");
        Val("lava_lamp", "Lava Lamp", U, 1.0f, 1.6f, 20f, 55f, "lavalamp", 0xFF6A1F, 1f, "old,fragile", "Groovy. Still lukewarm.");
        Val("instapix_camera", "Instapix Camera", U, 0.4f, 0.7f, 25f, 60f, "camera", 0xF2EEE4, 1f, "old,fragile", "Prints photos you immediately regret.");
        Val("dad_trophy", "\"#1 Dad\" Trophy", U, 0.3f, 0.6f, 8f, 20f, "trophy", 0xFFC933, 1.2f, "", "Awarded by a six-year-old. Contested by Mom.");
        Val("jar_of_pennies", "Jar of Pennies", U, 2f, 4f, 12f, 35f, "coinjar", 0xC87533, 1f, "old", "Hundreds of pennies. You are not going to count them.");
        Val("mixtape", "Mixtape", U, 0.05f, 0.08f, 8f, 25f, "cassette", 0x2A2A30, 1.2f, "old", "Hand-labeled with love and a leaky marker.");
        Val("tamagochu", "Tamagochu", U, 0.05f, 0.08f, 15f, 45f, "tamagochu", 0xFF7FB8, 1f, "old", "Still beeping. It's hungry. It's been hungry since 1998.");
        Val("bowling_ball", "Bowling Ball", U, 5f, 7f, 20f, 50f, "bowling", 0x5A2A9A, 0.7f, "garage", "Has three holes and a strike record it won't shut up about.");
        Val("fondue_set", "Fondue Set", U, 1.5f, 2.5f, 15f, 40f, "fondue", 0xE8641E, 0.8f, "kitchen,old", "Wedding gift. Used once. Cheese-scarred.");
        Val("cordless_drill", "Cordless Drill", U, 1.2f, 1.8f, 20f, 55f, "prefab:drill", 0xF08A1E, 0.8f, "garage", "Battery's dead. The drill is fine. Probably.");
        Val("vinyl_record", "Vinyl Record", U, 0.15f, 0.25f, 10f, 50f, "vinyl", 0xE8C83A, 1f, "old,fragile", "'Smooth Jazz for Hamsters, Vol. 3'.");
        Val("snow_globe", "Snow Globe", U, 0.4f, 0.8f, 10f, 30f, "snowglobe", 0x8A5A30, 1f, "fragile", "Tiny town. Eternal blizzard. Relatable.");
        Val("love_letters", "Bundle of Love Letters", U, 0.1f, 0.3f, 10f, 35f, "letters", 0xF5E6C8, 0.8f, "old", "Tied with a ribbon. Smells like perfume and bad decisions.");
        Val("cat_figurine", "Porcelain Cat Figurine", U, 0.2f, 0.5f, 10f, 40f, "catfig", 0xF4F0EA, 0.8f, "old,fragile", "Judging you. Politely. In porcelain.");

        // ---------------- VALUABLES: RARE ($60-300) ----------------
        Val("gameboi", "GameBoi", R, 0.2f, 0.3f, 70f, 180f, "gameboi", 0xB8B8B0, 1.2f, "old,fragile", "Still has 'Tetrus' in it. High score: BOB.");
        Val("signed_baseball", "Signed Baseball", R, 0.14f, 0.16f, 80f, 280f, "baseball", 0xF7F4EC, 1f, "", "Comes in a little case. Very official.");
        Val("captain_raccoon_1", "\"Captain Raccoon #1\" Comic", R, 0.05f, 0.1f, 90f, 300f, "comic", 0xFFD21F, 1f, "old", "First appearance of the trash panda hero. Fitting.");
        Val("pocket_watch", "Antique Pocket Watch", R, 0.1f, 0.15f, 100f, 280f, "pocketwatch", 0xE8B830, 1f, "old,fragile", "Stopped at 4:17. It's always 4:17.");
        Val("war_medal", "Grandpa's War Medal", R, 0.05f, 0.1f, 80f, 250f, "medal", 0xD4A030, 0.9f, "old", "For Valor. And for snacks, according to the family.");
        Val("class_ring", "Class Ring", R, 0.015f, 0.03f, 60f, 180f, "ring_class", 0xE8B830, 1f, "old", "Chunky. Gaudy. Beloved.");
        Val("silver_candlestick", "Silver Candlestick", R, 0.6f, 1.2f, 70f, 200f, "candlestick", 0xD6DAE0, 1f, "old", "Colonel Mustard's alibi.");
        Val("plumber_cartridge", "\"Super Plumber Bros\" Cartridge", R, 0.05f, 0.08f, 60f, 240f, "cartridge", 0x9A9AA0, 1f, "old", "Blow in it. It works now.");
        Val("cursed_doll", "Cursed Porcelain Doll", R, 0.6f, 1.0f, 15f, 900f, "doll", 0xB48AD8, 0.8f, "old,fragile", "Its eyes follow you. Its price follows nothing.");

        // ---------------- VALUABLES: EPIC ($300-1500) ----------------
        Val("rollex_watch", "\"Rollex\" Watch", E, 0.1f, 0.18f, 40f, 1500f, "watch", 0xFFC933, 1f, "fragile", "Says ROLLEX on the face. Two L's. Hmm.");
        Val("holo_charizord", "Holo \"Charizord\" Card", E, 0.005f, 0.01f, 350f, 1400f, "card", 0xFF7A1F, 1f, "old", "Shiny! First edition-ish! Kept in a sleeve like a precious baby.");
        Val("diamond_earring", "Single Diamond Earring", E, 0.005f, 0.01f, 300f, 900f, "earring", 0xDDF4FF, 1f, "", "Its twin is out there in another bag, living its best life.");
        Val("sock_of_cash", "Sock Full of Cash", E, 0.3f, 0.8f, 300f, 1200f, "sockcash", 0xF2F2EA, 1f, "old", "Grandma's bank. Grandma is going to be SO mad.");
        Val("gold_chain", "Gold Chain", E, 0.05f, 0.15f, 400f, 1300f, "chain", 0xFFC933, 1f, "", "24 karat. Or 24 carrot. Hard to tell. Heavy either way.");
        Val("pigcasso", "\"Pigcasso\" Painting", E, 2f, 4f, 500f, 1500f, "painting", 0xFF9EC0, 0.8f, "old,fragile", "Painted by an actual pig. Art critics weep.");

        // ---------------- VALUABLES: LEGENDARY ($1200-5000) ----------------
        // (wedding_ring is Epic $400-1,400 since Docs/Progression.md §3.2: the story item now turns up in the starter suburb)
        Val("wedding_ring", "Wedding Ring", E, 0.005f, 0.01f, 400f, 1400f, "ring", 0xFFD23A, 1.3f, "", "Engraved on the inside. Oh no. Somebody is looking for this.");
        Val("moon_rock", "Moon Rock (Probably)", L, 0.4f, 1.2f, 1500f, 5000f, "moonrock", 0x9A9CA4, 1f, "fragile", "Comes with a certificate of authenticity. Written in crayon.");
        Val("faberge_egg", "Faberge-ish Egg", L, 0.3f, 0.6f, 2500f, 5000f, "fabergeegg", 0x2A4AC8, 0.7f, "old,fragile", "Imperial-adjacent. Jewel-encrusted. Egg-shaped. Priceless-ish.");
        Val("lottery_ticket", "Winning-ish Lottery Ticket", L, 0.002f, 0.004f, 1200f, 4000f, "ticket", 0x2FB84A, 1f, "", "Five out of six numbers! That's like, most of them.");
        Val("gold_dentures", "Gold Dentures", L, 0.1f, 0.2f, 1400f, 3500f, "teeth", 0xFFC933, 0.9f, "old", "Grandpa's bling. Grandpa is gumming his soup now.");

        BuildDistrictDefs();
        BuildTellDefs();
        BuildNightDefs();
    }

    /// <summary>
    /// F4 night shift (Docs/Multiplayer.md §14.4): keep-crate pieces you earn, never loot (tier 99): a raccoon from a
    /// trash-can trap kept as a shop pet, and the Raccoon King's crown. Valuables, so display shelves show them off.
    /// </summary>
    static void BuildNightDefs()
    {
        Val("pet_raccoon", "Bandit the Pet Raccoon", Rarity.Epic, 3f, 6f, 120f, 200f, "critter_raccoon", 0x80808A, 0f, "night_only",
            "Caught fair and square in a trash-can trap. Loves fish. Bites (affectionately). Guards the shelf.");
        meta["pet_raccoon"].tier = 99;
        Val("raccoon_king_crown", "The Raccoon King's Crown", Rarity.Legendary, 0.6f, 1.0f, 400f, 650f, "critter_crown", 0xFFC233, 0f, "night_only",
            "Prised off the King's head on Depot Row. Smells like royalty (garbage).");
        meta["raccoon_king_crown"].tier = 99;
    }

    /// <summary>
    /// F3 bag tells (Docs/Multiplayer.md §14.3): odd things that only turn up in buzzing/ticking mystery bags, and the fillers
    /// that make heavy bags heavy. Tier 99, so normal loot never draws them; <see cref="BagTells.ShapeContents"/> picks them by id.
    /// </summary>
    static void BuildTellDefs()
    {
        const ItemCategory J = ItemCategory.Junk, ME = ItemCategory.Metal, HZ = ItemCategory.Hazard;
        const Rarity U = Rarity.Uncommon;
        Odd("chattering_teeth", "Wind-Up Chattering Teeth", J, 0.1f, 0.2f, "teeth", 0xF2F2EE, "Still chattering. Nobody has wound it since 1994.");
        Odd("possessed_egg_timer", "Egg Timer That Won't Stop", J, 0.1f, 0.2f, "toy", 0xF2E23A, "Ding! Ding! Ding! The eggs are long gone. The timer doesn't care.");
        Odd("vibrating_flip_phone", "Flip Phone Stuck on Vibrate", HZ, 0.1f, 0.2f, "flipphone", 0x4A4AE0, "Bzzzt. 9,999 unread texts from 'Mom'. Battery: somehow full.");
        Odd("jar_of_bees", "Jar of Bees", HZ, 0.5f, 0.8f, "jar", 0xF2C21A, "Lid on. Bees mad. Hazard bin, and quickly.");
        OddVal("ticking_alarm_clock", "Ticking Alarm Clock (Set for ??:??)", U, 0.3f, 0.6f, 12f, 40f, "pocketwatch", 0xE8402A, "Tick. Tock. It's set for an hour that doesn't exist.");
        OddVal("singing_bass", "Singing Bass Plaque", U, 1.0f, 1.6f, 30f, 85f, "trophy", 0x3A7A3A, "'Take me to the river.' It will not stop asking.");
        OddVal("haunted_tamagochu", "Haunted Tamagochu", U, 0.08f, 0.12f, 25f, 70f, "tamagochu", 0x9A3AE8, "Beeps at 3 a.m. The pet died in 1998. It's still hungry.");
        Odd("wet_sand_sack", "Sack of Wet Sand", J, 6f, 8f, "litter", 0xC8B07A, "Someone emptied a sandbox. Then it rained. Then they bagged it.");
        Odd("cinder_block", "Cinder Block (Why?)", J, 7f, 9f, "box", 0x9A9A9A, "Nobody knows why it was in a trash bag. Least of all the bag.");
        Odd("cracked_cast_iron_pot", "Cracked Cast-Iron Pot", ME, 4f, 6f, "pot", 0x2A2A2A, "Seasoned for forty years. Dropped once.");
    }

    /// <summary>An F3 tell-only non-valuable (tier 99: never in normal loot).</summary>
    static void Odd(string id, string name, ItemCategory cat, float minKg, float maxKg, string visual, int color, string flavor)
    {
        Add(id, name, cat, minKg, maxKg, visual, color, 1f, "tell_only", flavor);
        meta[id].tier = 99;
    }

    /// <summary>An F3 tell-only valuable (tier 99).</summary>
    static void OddVal(string id, string name, Rarity rarity, float minKg, float maxKg, float minValue, float maxValue, string visual, int color,
        string flavor)
    {
        Val(id, name, rarity, minKg, maxKg, minValue, maxValue, visual, color, 1f, "tell_only", flavor);
        meta[id].tier = 99;
    }

    /// <summary>
    /// District items (Docs/Progression.md §5.7 / §5.8). Item tier = the earliest district tier they appear in; a bag only
    /// draws items with tier ≤ its loot profile's tier (so the starter suburb never sees them). F = "fragile" tag.
    /// </summary>
    static void BuildDistrictDefs()
    {
        const ItemCategory J = ItemCategory.Junk, PL = ItemCategory.Plastic, GL = ItemCategory.Glass,
            ME = ItemCategory.Metal, PA = ItemCategory.Paper, HZ = ItemCategory.Hazard;
        const Rarity U = Rarity.Uncommon, R = Rarity.Rare, E = Rarity.Epic, L = Rarity.Legendary;

        // ---------------- tier 2: Minivan Meadows (upper-middle suburbia) ----------------
        RichVal(2, "air_fryer_xxl", "Air Fryer XXL (Used Once)", U, 3.5f, 5.0f, 40f, 110f, "airfryer", 0x2A2A2E, 1.2f, "kitchen,fragile", "Fried one (1) nugget. Achieved enlightenment. Retired.");
        RichVal(2, "yeeti_tumbler", "Yeet-i Mega Tumbler", U, 0.4f, 0.7f, 25f, 60f, "tumbler", 0x3FB8C8, 1.2f, "", "Keeps coffee hot for 3 days. Owner's interest lasted 3 hours.");
        RichVal(2, "essential_oil_crate", "Crate of Essential Oils (MLM)", U, 1.5f, 3.0f, 20f, 90f, "oilcrate", 0xC8A06A, 1.0f, "", "Cures nothing. Smells like everything. Aunt Denise is Diamond Rank now.");
        RichVal(2, "fantasy_trophy", "Fantasy Football League Trophy", U, 1.0f, 2.0f, 30f, 120f, "trophy_big", 0xD8D8DC, 1.0f, "", "Won by Brad, 2019. Brad has mentioned it 4,000 times. Brad threw it out after the divorce.");
        RichVal(2, "pedaltron_seat", "Pedaltron Smart-Bike Seat", R, 0.8f, 1.2f, 150f, 400f, "bikeseat", 0x1E1E24, 1.0f, "", "The bike is still in the living room. It is a coat rack now.");
        RichVal(2, "crashed_drone", "Crashed Camera Drone", R, 0.9f, 1.4f, 180f, 520f, "drone", 0xE8E8EC, 1.0f, "fragile", "Last recorded footage: a tree, getting closer.");
        RichVal(2, "occulus_wift", "Occulus Wift VR Headset", R, 0.5f, 0.7f, 200f, 480f, "vrheadset", 0xF2F2F4, 1.0f, "fragile", "Smells like forehead. Last played: 'Beat Sabre', Expert, 4 hours straight.");
        RichVal(2, "air_jordumb", "Air Jordumb Retro Sneakers", E, 0.8f, 1.2f, 600f, 2200f, "sneakers", 0xD8262E, 1.0f, "", "Deadstock! Worn once, to the wrong wedding.");
        RichVal(2, "bon_jovial_guitar", "Guitar Signed by 'Bon Jovial'", E, 3.0f, 4.5f, 800f, 2600f, "guitar", 0xC8402A, 0.8f, "fragile", "Signed in Sharpie. Or ketchup. The autograph expert fainted either way.");
        RichVal(2, "minivan_title", "Pink Slip to a Minivan", L, 0.01f, 0.02f, 2500f, 6000f, "deed", 0xFFB8D0, 0.8f, "", "The family 'wanted something sportier'. They bought a slightly bigger minivan.");

        // ---------------- tier 3: Snootington Hills (old money, hedges) ----------------
        RichVal(3, "monogram_robe", "Monogrammed Silk Robe", U, 0.8f, 1.2f, 80f, 220f, "robe", 0x7A1E3A, 1.0f, "", "Embroidered 'H.W.T. III'. The first two are also in the trash.");
        RichVal(3, "caviar_tin", "Tin of Beluga-ish Caviar", R, 0.2f, 0.4f, 300f, 800f, "caviar", 0x1E2A4A, 1.0f, "fragile", "Tiny fish eggs. Tiny price? Absolutely not.");
        RichVal(3, "sterling_tea_set", "Sterling Silver Tea Set", R, 3.0f, 5.0f, 400f, 1100f, "teaset", 0xD6DAE0, 1.0f, "fragile", "Pinkies up. One handle snapped because a pinky went TOO up.");
        RichVal(3, "polo_cup", "Polo Championship Cup", R, 2.0f, 3.5f, 350f, 950f, "trophy_cup", 0xE8E4D8, 0.9f, "", "Awarded to a horse. The horse was not consulted.");
        RichVal(3, "louis_vuitto", "'Louis Vuitto' Handbag", E, 0.8f, 1.3f, 1200f, 4200f, "handbag", 0x7A4A22, 1.0f, "", "Designer! Real? Unclear. The zipper says 'Luis'.");
        RichVal(3, "chateau_fance", "Chateau Le Fance 1961", E, 1.3f, 1.6f, 1000f, 3800f, "wine_fancy", 0x3A0E1A, 1.0f, "fragile", "Aged to perfection. Then aged a bit more. Then forgotten behind the good cheese.");
        RichVal(3, "poodle_portrait", "Oil Portrait of a Pampered Poodle", E, 4.0f, 7.0f, 1400f, 4800f, "painting_poodle", 0xF2E6F0, 0.8f, "fragile", "Commissioned. Framed in gold. The poodle hated it.");
        RichVal(3, "gold_putter", "Gold-Plated Putter", E, 0.9f, 1.3f, 1100f, 3600f, "putter", 0xFFC24A, 0.9f, "", "Scored one hole-in-one, then retired so it could never miss again.");
        RichVal(3, "dented_chandelier", "Crystal Chandelier (Slightly Dented)", L, 9.0f, 14.0f, 5000f, 14000f, "chandelier", 0xE8C860, 0.7f, "fragile", "Fell during a dinner party. Nobody said anything. Very polite people.");
        RichVal(3, "heirloom_tiara", "Great-Grandmama's Tiara", L, 0.3f, 0.5f, 6000f, 16000f, "tiara", 0xE0E4EA, 0.8f, "", "Worn to 3 coronations and 1 very awkward Applebee's.");

        // ---------------- tier 4: Billionaire Bluffs (absurd) ----------------
        RichVal(4, "emerald_monocle", "Emerald Monocle", E, 0.05f, 0.10f, 3000f, 9000f, "monocle", 0x2AC86A, 1.0f, "fragile", "Popped out in shock when the stock dipped 0.2%.");
        RichVal(4, "diamond_dog_collar", "Diamond Dog Collar", E, 0.2f, 0.4f, 4000f, 12000f, "dogcollar", 0xC8264A, 1.0f, "", "The dog upgraded to platinum. This was the 'casual' collar.");
        RichVal(4, "platinum_tamagochu", "Platinum Tamagochu (1 of 3)", E, 0.08f, 0.12f, 3500f, 10000f, "tamagochu_plat", 0xDCE2EA, 0.9f, "fragile", "Still hungry. Hungrier. Richer.");
        RichVal(4, "celebrity_toupee", "A-List Toupee (Signed Inside)", E, 0.1f, 0.2f, 2500f, 11000f, "toupee", 0xE8B858, 0.9f, "", "You know whose it is. You can't say whose it is. Legal says no.");
        RichVal(4, "nft_hard_drive", "Hard Drive Full of NFTs", L, 0.3f, 0.5f, 1f, 60000f, "harddrive", 0x2A2A32, 1.0f, "fragile", "4,000 cartoon apes. Worth millions! Worth nothing! Depends on the minute.");
        RichVal(4, "gold_toilet_seat", "Solid Gold Toilet Seat", L, 16f, 22f, 15000f, 40000f, "toiletseat_gold", 0xFFC633, 0.8f, "", "Cold. So, so cold. Heavier than your life choices.");
        RichVal(4, "yacht_keys", "Keys to a Yacht (Which Yacht?)", L, 0.1f, 0.2f, 18000f, 45000f, "keys_yacht", 0x2A6AE0, 0.9f, "", "Keychain says 'SS Tax Write-Off'. The yacht is somewhere. Probably.");
        RichVal(4, "dino_egg", "Genuine* Dinosaur Egg", L, 6f, 9f, 20000f, 50000f, "dinoegg", 0x9AB86A, 0.8f, "fragile", "*Certified by the same guy who certified the moon rock.");
        RichVal(4, "space_ticket", "Unused Space Tourism Ticket", L, 0.005f, 0.01f, 25000f, 60000f, "ticket_space", 0x1A1A3A, 0.8f, "", "Seat 1A. Window. To SPACE. They had a brunch conflict.");
        RichVal(4, "island_deed", "Deed to a Private Island (Very Small)", L, 0.01f, 0.02f, 30000f, 80000f, "deed_island", 0xF2E2B0, 0.6f, "", "Island area: 4 m². Includes one palm tree and a sovereignty dispute.");
        RichVal(4, "owner_statue", "Golden Statue of the Owner's Face", L, 24f, 30f, 20000f, 55000f, "statue_face", 0xFFC24A, 0.5f, "", "Commissioned for his 40th. Replaced for his 41st. Two-hand carry.");

        // ---------------- district non-valuables (§5.8) ----------------
        Rich(2, "kombucha_bottle", "Kombucha Bottle", GL, 0.4f, 0.6f, "kombucha", 0xC8862A, 2.0f, "", "Tastes like a feeling you can't name.");
        Rich(2, "oat_milk_carton", "Oat Milk Carton", PA, 0.1f, 0.2f, "milkcarton", 0xEADCB8, 2.0f, "kitchen", "Barista Edition. No barista was consulted.");
        Rich(2, "coffee_pod_pile", "Pile of Coffee Pods", PL, 0.2f, 0.5f, "pods", 0x6A3A2A, 2.0f, "kitchen", "Forty tiny cups of regret.");
        Rich(2, "scrapazon_box_mountain", "Mountain of Scrapazon Boxes", PA, 2.0f, 4.0f, "boxpile", 0xC4935A, 2.0f, "bulk", "Somebody has a problem. It arrives in 0.3 seconds.");
        Rich(2, "trampoline_spring", "Trampoline Spring", ME, 0.2f, 0.4f, "spring", 0xB8BCC4, 1.0f, "", "Boing. Boing. Boi— (snapped).");
        Rich(2, "mlm_leggings", "400 Pairs of MLM Leggings", J, 0.8f, 2.0f, "leggings", 0x6A3AC8, 1.5f, "bulk", "Pattern: 'Galaxy Taco'. 400 pairs.");
        Rich(2, "participation_trophies", "Bag of Participation Trophies", PL, 0.5f, 1.5f, "trophies", 0xE8C040, 1.0f, "", "Everyone's a winner. Everyone's in the trash.");
        Rich(3, "champagne_bottle", "Champagne Bottle", GL, 0.8f, 1.0f, "champagne", 0x1E3A26, 3.0f, "party", "Popped for a Tuesday.");
        Rich(3, "shredded_tax_docs", "Shredded Tax Documents", PA, 2.0f, 4.5f, "shreds", 0xF2F0E8, 2.0f, "bulk", "Nothing to see here. Literally. It's confetti.");
        Rich(3, "truffle_fry_cone", "Truffle Fry Cone (1 Fry)", J, 0.1f, 0.2f, "fries", 0x2A2A2A, 1.0f, "kitchen,party", "One fry left. It costs $6.");
        Rich(3, "wagyu_scraps", "Wagyu Scraps", J, 0.5f, 1.5f, "steak", 0xE87A8A, 1.5f, "kitchen", "The cow had a massage schedule.");
        Rich(3, "espresso_capsules", "Espresso Capsules", ME, 0.2f, 0.5f, "pods", 0xC8A040, 2.0f, "kitchen", "Recyclable if you peel off the foil. Nobody peels off the foil.");
        Rich(3, "topiary_clippings", "Topiary Clippings", J, 3.0f, 6.0f, "topiary", 0x2E7A34, 2.0f, "bulk", "Even the yard waste is pretentious.");
        Rich(3, "broken_croquet_mallet", "Broken Croquet Mallet", J, 1.0f, 1.8f, "mallet", 0x8A5A30, 0.8f, "old", "Snapped in a 'friendly' match. Lawyers were involved.");
        Rich(4, "caviar_tin_empty", "Empty Caviar Tin", ME, 0.1f, 0.2f, "caviar_empty", 0x1E2A4A, 1.5f, "kitchen", "Licked clean by a man worth $4 billion.");
        Rich(4, "gold_leaf_pizza_box", "Gold-Leaf Pizza Box", J, 0.5f, 1.0f, "pizzabox", 0xE8C060, 1.5f, "party,kitchen", "The grease is still grease.");
        Rich(4, "ice_sculpture_puddle", "Ice Sculpture Puddle", J, 3.0f, 6.0f, "puddle", 0xBFE8FF, 1.5f, "bulk,party", "It was a swan. It is now a vibe.");
        Rich(4, "designer_shopping_bags", "Designer Shopping Bags", PA, 0.3f, 0.8f, "shopbags", 0xF2EEE6, 2.0f, "", "The bags cost more than your tires.");
        Rich(4, "smart_fridge_screen", "Smart Fridge Screen", HZ, 2.0f, 4.0f, "fridgescreen", 0x2A2E36, 0.8f, "", "Still tells you you're out of oat milk.");
        Rich(4, "escooter_battery", "E-Scooter Battery", HZ, 3.0f, 5.0f, "ebattery", 0x1E1E22, 0.8f, "", "Lithium. Grumpy. VERY do not compact.");
        Rich(4, "yacht_flare", "Expired Yacht Flare", HZ, 0.3f, 0.6f, "flare", 0xE8301E, 0.8f, "", "Expired: either a dud or a firework. Find out (don't).");
    }

    /// <summary>A district non-valuable (tier 2..4). Named apart from Add so tools that parse Add/Val lines see only tier 1.</summary>
    static void Rich(int tier, string id, string name, ItemCategory cat, float minKg, float maxKg, string visual, int color,
        float weight, string tags, string flavor)
    {
        Add(id, name, cat, minKg, maxKg, visual, color, weight, tags, flavor);
        meta[id].tier = tier;
    }

    /// <summary>A district-exclusive valuable (tier 2..4).</summary>
    static void RichVal(int tier, string id, string name, Rarity rarity, float minKg, float maxKg, float minValue, float maxValue,
        string visual, int color, float weight, string tags, string flavor)
    {
        Val(id, name, rarity, minKg, maxKg, minValue, maxValue, visual, color, weight, tags, flavor);
        meta[id].tier = tier;
    }

    /// <summary>
    /// F5 Big Cleanup prizes (Docs/Multiplayer.md §14.5): weight 0, so they never drop from a bag; the event hands them out.
    /// Appended after every other def (DailyStats saves the best find as an index into <see cref="All"/>).
    /// </summary>
    static void BuildEventDefs()
    {
        Val("big_cleanup_cup", "Big Cleanup Cup", Rarity.Legendary, 1.4f, 1.4f, 250f, 250f, "trophy", 0xFFD23F, 0f, "",
            "The city's finest trash-grabbers. Engraved, slightly sticky.");
        Val("big_cleanup_ribbon", "Big Cleanup Ribbon", Rarity.Rare, 0.1f, 0.1f, 40f, 40f, "trophy", 0x4FA3FF, 0f, "",
            "Personal best! The city printed it on recycled pizza boxes.");
    }

    // ------------------------------------------------------------------ additive helpers

    /// <summary>True when the item carries a loot tag ("party", "baby", "garage", "old", "kitchen", "bulk", "garage_only").</summary>
    public static bool HasTag(ItemDef def, string tag)
    {
        Ensure();
        if (def == null || !meta.TryGetValue(def.id, out var m)) return false;
        foreach (var t in m.tags) if (t == tag) return true;
        return false;
    }

    /// <summary>All defs of a category (e.g. every Valuable for a collection screen).</summary>
    public static List<ItemDef> InCategory(ItemCategory c)
    {
        Ensure();
        return all.FindAll(d => d.category == c);
    }

    /// <summary>
    /// Rough $ worth of an item: Valuables = rolled market value; recyclables = what the correct bin pays
    /// (before combo); Hazard = disposal pay; Junk = compactor scrap.
    /// </summary>
    public static float EstimatedValue(ItemInstance item)
    {
        if (item == null) return 0f;
        var def = item.Def;
        if (def == null) return 0f;
        switch (def.category)
        {
            case ItemCategory.Valuable: return item.value;
            case ItemCategory.Hazard: return Economy.HazardDisposalPay;
            case ItemCategory.Junk: return Economy.CompactorScrapPerKg * item.massKg;
            default: return Mathf.Max(Economy.MinRecyclePayout, Economy.RecycleRatePerKg(def.category) * item.massKg);
        }
    }

    /// <summary>
    /// Human appraisal string: valuables are rounded to a "vibe" number ("~$2,300", "~$85"), everything else
    /// shows its exact scrap/recycle worth ("$0.42").
    /// </summary>
    public static string AppraisalText(ItemInstance item)
    {
        if (item == null) return "$0.00";
        float v = EstimatedValue(item);
        var def = item.Def;
        if (def == null || def.category != ItemCategory.Valuable) return Economy.Money(v);
        float step = v >= 1000f ? 100f : v >= 200f ? 10f : v >= 50f ? 5f : 1f;
        float r = Mathf.Max(1f, Mathf.Round(v / step) * step);
        return "~$" + r.ToString("N0");
    }

    /// <summary>Theme label ("Party Bag!", "Garage Cleanout") of a bag rolled this session, or null for a plain bag.</summary>
    public static string BagTheme(BagContents bag)
    {
        if (bag == null || string.IsNullOrEmpty(bag.bagId)) return null;
        return themeByBag.TryGetValue(bag.bagId, out var t) ? ThemeLabel(t) : null;
    }

    // ------------------------------------------------------------------ loot tables

    enum Theme
    {
        None, Party, SpringCleaning, NewBaby, GarageCleanout,
        // Rich themes (tier 2+ districts, Docs/Progression.md §5.5), rolled after the 4 base themes miss.
        DivorceBag, MovingDay, InfluencerHaul, EstateCleanout, YachtParty, ButlerMixup
    }

    static readonly Dictionary<string, Theme> themeByBag = new Dictionary<string, Theme>();

    static string ThemeLabel(Theme t) => t switch
    {
        Theme.Party => "Party Bag!",
        Theme.SpringCleaning => "Spring Cleaning!",
        Theme.NewBaby => "New Baby Bag",
        Theme.GarageCleanout => "Garage Cleanout",
        Theme.DivorceBag => "Divorce Bag",
        Theme.MovingDay => "Moving Day",
        Theme.InfluencerHaul => "Influencer Unboxing Haul",
        Theme.EstateCleanout => "Estate Clean-Out!",
        Theme.YachtParty => "Yacht Party",
        Theme.ButlerMixup => "Butler Mix-Up!",
        _ => null
    };

    /// <summary>One rich theme row (§5.5): min district tier, chance at 100 % Trust, valuable chance ×, extra picks,
    /// guaranteed minimum rarity (Common = none).</summary>
    struct RichTheme
    {
        public Theme theme; public int minTier; public double chance; public float valuableMult; public int extraPicks; public Rarity guarantee;
        public RichTheme(Theme t, int tier, double ch, float vm, int extra, Rarity g)
        { theme = t; minTier = tier; chance = ch; valuableMult = vm; extraPicks = extra; guarantee = g; }
    }

    static readonly RichTheme[] RichThemes =
    {
        new RichTheme(Theme.DivorceBag, 2, 0.04, 2.5f, 0, Rarity.Common),
        new RichTheme(Theme.MovingDay, 2, 0.05, 1.8f, 4, Rarity.Common),
        new RichTheme(Theme.InfluencerHaul, 2, 0.03, 1.0f, 0, Rarity.Uncommon),
        new RichTheme(Theme.EstateCleanout, 3, 0.06, 3.0f, 0, Rarity.Common),
        new RichTheme(Theme.YachtParty, 4, 0.05, 1.0f, 0, Rarity.Rare),
        new RichTheme(Theme.ButlerMixup, 4, 0.01, 1.0f, 0, Rarity.Epic),
    };

    static int RichIndex(Theme t)
    {
        for (int i = 0; i < RichThemes.Length; i++) if (RichThemes[i].theme == t) return i;
        return -1;
    }

    static readonly ItemCategory[] Cats =
        { ItemCategory.Junk, ItemCategory.Paper, ItemCategory.Plastic, ItemCategory.Metal, ItemCategory.Glass, ItemCategory.Hazard };

    // Weights in the same order as Cats.
    static readonly float[] PartyWeights = { 24f, 8f, 20f, 24f, 23f, 1f };
    static readonly float[] SpringWeights = { 32f, 24f, 12f, 16f, 10f, 6f };
    static readonly float[] BabyWeights = { 50f, 14f, 18f, 8f, 8f, 2f };
    static readonly float[] GarageWeights = { 24f, 8f, 10f, 34f, 4f, 20f };
    static readonly float[] PlainWeights = { 40f, 18f, 15f, 11f, 10f, 4f };

    /// <summary>Category weights for a theme; the plain and rich themes use the profile's weights when it has some.</summary>
    static float[] CategoryWeights(Theme t, LootProfile prof) => t switch
    {
        Theme.Party => PartyWeights,
        Theme.SpringCleaning => SpringWeights,
        Theme.NewBaby => BabyWeights,
        Theme.GarageCleanout => GarageWeights,
        _ => prof != null && prof.categoryWeights != null && prof.categoryWeights.Length == Cats.Length ? prof.categoryWeights : PlainWeights,
    };

    static string ThemeTag(Theme t) => t switch
    {
        Theme.Party => "party",
        Theme.SpringCleaning => "old",
        Theme.NewBaby => "baby",
        Theme.GarageCleanout => "garage",
        Theme.EstateCleanout => "old",       // "old" tag boost ×5 in estate clean-outs
        _ => null
    };

    const float ThemeTagBoost = 5f;
    /// <summary>District non-valuables (tier ≥ 2) are this much likelier than tier-1 items of the same category.</summary>
    const float DistrictItemBoost = 3f;
    public const float MaxBagKg = 18f;
    public const float MinBagKg = 4f;
    /// <summary>Spring Cleaning multiplies the valuable chance by this (plus the profile's flat bonus).</summary>
    public const float SpringValuableMult = 2.5f;

    /// <summary>The starter loot profile (Mildly Pleasant Acres); the frozen 4-argument RollBag uses it.</summary>
    public static LootProfile StarterProfile => Districts.Starter.loot;

    /// <summary>Earliest district tier an item appears in (1 = everywhere, 2..4 = district-exclusive).</summary>
    public static int TierOf(ItemDef def)
    {
        Ensure();
        return def != null && meta.TryGetValue(def.id, out var m) ? m.tier : 1;
    }

    /// <summary>Per-item chance that a pick is a Valuable in the starter suburb (before theme modifiers).</summary>
    public static float ValuableChance(float houseWealth) => ValuableChance(houseWealth, StarterProfile);

    /// <summary>Per-item valuable chance under a loot profile: (p0 + p1·wealth) × valuableMult (before theme modifiers).</summary>
    public static float ValuableChance(float houseWealth, LootProfile profile)
    {
        var p = profile ?? StarterProfile;
        return (p.p0 + p.p1 * Mathf.Clamp01(houseWealth)) * (p.valuableMult > 0f ? p.valuableMult : 1f);
    }

    /// <summary>Rarity weights (Uncommon, Rare, Epic, Legendary) for a house wealth 0..1 in the starter suburb.</summary>
    public static float[] RarityWeights(float houseWealth) => RarityWeights(houseWealth, StarterProfile);

    /// <summary>Rarity weights (Uncommon, Rare, Epic, Legendary) under a loot profile.</summary>
    public static float[] RarityWeights(float houseWealth, LootProfile profile)
    {
        var p = profile ?? StarterProfile;
        float w = Mathf.Clamp01(houseWealth);
        var r = new float[4];
        for (int i = 0; i < 4; i++)
        {
            float a = p.rarity0 != null && i < p.rarity0.Length ? p.rarity0[i] : 0f;
            float b = p.rarity1 != null && i < p.rarity1.Length ? p.rarity1[i] : a;
            r[i] = Mathf.Lerp(a, b, w);
        }
        return r;
    }

    /// <summary>
    /// Roll a bag's contents deterministically from seed with the starter profile (Mildly Pleasant Acres).
    /// houseWealth 0..1 skews toward nicer finds. FROZEN signature.
    /// </summary>
    public static BagContents RollBag(string bagId, int seed, float houseWealth, string foundAt) =>
        RollBag(bagId, seed, houseWealth, foundAt, StarterProfile, 1f);

    /// <summary>
    /// Roll a bag under a district loot profile (Docs/Progression.md §5.4 rules). trust01 (the district's Trust) scales
    /// the rich-theme chances: table chance × (0.5 + 1.5·Trust) / 2. Deterministic for (seed, wealth, profile, trust).
    /// </summary>
    public static BagContents RollBag(string bagId, int seed, float houseWealth, string foundAt, LootProfile profile, float trust01 = 1f)
        => RollBagCore(bagId, seed, houseWealth, foundAt, profile, trust01, true);

    // The audit compares the same completed core bag with/without the independent everyday-find pass.
    // There is deliberately no mutable game-wide loot switch or save setting for this.
    static BagContents RollBagCore(string bagId, int seed, float houseWealth, string foundAt, LootProfile profile, float trust01, bool everydayFinds)
    {
        Ensure();
        var prof = profile ?? StarterProfile;
        houseWealth = Mathf.Clamp01(houseWealth);
        var rng = new System.Random(seed);
        var bag = new BagContents { bagId = bagId, foundAt = foundAt, sackVariant = rng.Next(0, 3) };

        Theme theme = RollTheme(rng, houseWealth, prof, Mathf.Clamp01(trust01));
        if (!string.IsNullOrEmpty(bagId))
        {
            if (theme == Theme.None) themeByBag.Remove(bagId);
            else themeByBag[bagId] = theme;
        }

        int ri = RichIndex(theme);
        int extra = ri >= 0 ? RichThemes[ri].extraPicks : 0;
        int minItems = prof.maxItems > 0 ? Mathf.Max(1, prof.minItems) : 5;
        int maxItems = prof.maxItems > 0 ? Mathf.Max(minItems, prof.maxItems) : 10;
        int n = rng.Next(minItems, maxItems + 1) + extra;
        float total = bag.bagMassKg;
        for (int i = 0; i < n; i++)
        {
            ItemInstance item = null;
            // Heavy picks that would overflow the sack are rerolled a few times, then skipped.
            for (int attempt = 0; attempt < 4 && item == null; attempt++)
            {
                var def = PickDef(rng, houseWealth, theme, prof);
                var candidate = RollItem(def, rng, foundAt);
                if (total + candidate.massKg <= MaxBagKg) item = candidate;
            }
            if (item == null) continue;
            bag.items.Add(item);
            total += item.massKg;
        }

        // Rich-theme guarantee (influencer haul, yacht party, butler mix-up): swap the first non-valuable for a valuable
        // of at least that rarity. Doesn't check mass, which is the only way an owner_statue fits in a sack.
        if (ri >= 0 && RichThemes[ri].guarantee != Rarity.Common)
            total = ApplyGuarantee(bag, rng, houseWealth, theme, prof, RichThemes[ri].guarantee, foundAt, total);

        // Too light? Stuff in some bulky gunk (or swap out the lightest junk) so every bag has some heft.
        int maxCount = maxItems + extra;
        for (int guard = 0; guard < 4 && total < MinBagKg; guard++)
        {
            var filler = RollItem(PickBulk(rng, theme, prof), rng, foundAt);
            if (total + filler.massKg > MaxBagKg) continue;
            if (bag.items.Count < maxCount)
            {
                bag.items.Add(filler);
                total += filler.massKg;
            }
            else
            {
                int lightest = -1;
                for (int k = 0; k < bag.items.Count; k++)
                {
                    var d = bag.items[k].Def;
                    if (d == null || d.category == ItemCategory.Valuable) continue;
                    if (lightest < 0 || bag.items[k].massKg < bag.items[lightest].massKg) lightest = k;
                }
                if (lightest < 0) break;
                total += filler.massKg - bag.items[lightest].massKg;
                bag.items[lightest] = filler;
            }
        }

        // Shuffle so the treasure isn't always at a predictable index (Fisher-Yates, same rng).
        for (int k = bag.items.Count - 1; k > 0; k--)
        {
            int j = rng.Next(k + 1);
            (bag.items[k], bag.items[j]) = (bag.items[j], bag.items[k]);
        }
        // F3 (Docs/Multiplayer.md §14.3): heavy / over-stuffed / mystery bags get contents to match their tell.
        BagTells.ShapeContents(bag, rng, foundAt);
        if (everydayFinds) ApplyEverydayFinds(bag, seed, theme, prof, foundAt);
        return bag;
    }

    static float ApplyGuarantee(BagContents bag, System.Random rng, float wealth, Theme theme, LootProfile prof, Rarity minRarity,
        string foundAt, float total)
    {
        foreach (var it in bag.items)
        {
            var d = it != null ? it.Def : null;
            if (d != null && d.category == ItemCategory.Valuable && d.rarity >= minRarity) return total;
        }
        var rw = RarityWeights(wealth, prof);
        int minIdx = (int)minRarity - 1;                       // Uncommon = 0
        var tail = new float[4 - minIdx];
        for (int i = 0; i < tail.Length; i++) tail[i] = rw[minIdx + i];
        var rarity = (Rarity)(minIdx + WeightedIndex(rng, tail) + 1);
        var def = WeightedPick(rng, d => d.category == ItemCategory.Valuable && d.rarity == rarity, null, theme, prof);
        if (def == null) return total;
        var item = RollItem(def, rng, foundAt);
        int swap = -1;
        for (int k = 0; k < bag.items.Count; k++)
        {
            var d = bag.items[k].Def;
            if (d == null || d.category != ItemCategory.Valuable) { swap = k; break; }
        }
        if (swap < 0) bag.items.Add(item);
        else
        {
            total -= bag.items[swap].massKg;
            bag.items[swap] = item;
        }
        return total + item.massKg;
    }

    static Theme RollTheme(System.Random rng, float wealth, LootProfile prof, float trust01)
    {
        double r = rng.NextDouble();
        double party = 0.06, spring = 0.035 + 0.03 * wealth, baby = 0.03, garage = 0.05;
        if (r < party) return Theme.Party;
        r -= party;
        if (r < spring) return Theme.SpringCleaning;
        r -= spring;
        if (r < baby) return Theme.NewBaby;
        r -= baby;
        if (r < garage) return Theme.GarageCleanout;
        r -= garage;
        if (prof.tier >= 2)
        {
            // Table chances are at 100 % Trust: × (0.5 + 1.5·Trust) / 2 (at 40 % Trust that's 55 % of the table).
            double f = (0.5 + 1.5 * trust01) / 2.0;
            foreach (var rt in RichThemes)
            {
                if (prof.tier < rt.minTier) continue;
                double c = rt.chance * f;
                if (r < c) return rt.theme;
                r -= c;
            }
        }
        return Theme.None;
    }

    static ItemDef PickDef(System.Random rng, float wealth, Theme theme, LootProfile prof)
    {
        float vChance = ValuableChance(wealth, prof);
        int ri = RichIndex(theme);
        if (theme == Theme.SpringCleaning) vChance = vChance * SpringValuableMult + prof.springFlat;   // attics are treasure chests
        else if (theme == Theme.GarageCleanout) vChance *= 1.5f;
        else if (ri >= 0) vChance *= RichThemes[ri].valuableMult;

        string tag = ThemeTag(theme);
        if (rng.NextDouble() < vChance)
        {
            var rw = RarityWeights(wealth, prof);
            int rIdx = WeightedIndex(rng, rw);
            var rarity = (Rarity)(rIdx + 1);
            // Golden gnome: each Rare roll has a chance to become Epic (only draws when the profile asks for it).
            if (rarity == Rarity.Rare && prof.rareToEpic > 0f && rng.NextDouble() < prof.rareToEpic) rarity = Rarity.Epic;
            var d = WeightedPick(rng, def => def.category == ItemCategory.Valuable && def.rarity == rarity, tag, theme, prof);
            if (d != null) return d;
        }

        var cat = Cats[WeightedIndex(rng, CategoryWeights(theme, prof))];
        return WeightedPick(rng, def => def.category == cat, tag, theme, prof) ?? all[0];
    }

    static ItemDef PickBulk(System.Random rng, Theme theme, LootProfile prof) =>
        WeightedPick(rng, d => d.category != ItemCategory.Valuable && HasTag(d, "bulk"), ThemeTag(theme), theme, prof) ?? all[0];

    static readonly List<ItemDef> scratchDefs = new List<ItemDef>();
    static readonly List<float> scratchWeights = new List<float>();

    static ItemDef WeightedPick(System.Random rng, System.Predicate<ItemDef> filter, string boostTag, Theme theme, LootProfile prof)
    {
        scratchDefs.Clear();
        scratchWeights.Clear();
        int profTier = prof != null ? Mathf.Max(1, prof.tier) : 1;
        foreach (var d in all)
        {
            if (!filter(d)) continue;
            var m = meta[d.id];
            if (m.tier > profTier) continue;                                   // not sold in this district (yet)
            bool garageOnly = false, boosted = false;
            foreach (var t in m.tags)
            {
                if (t == "garage_only") garageOnly = true;
                if (boostTag != null && t == boostTag) boosted = true;
            }
            if (garageOnly && theme != Theme.GarageCleanout) continue;
            float w = m.weight * (boosted ? ThemeTagBoost : 1f);
            if (theme == Theme.NewBaby && boosted) w *= 2f; // babies generate a LOT of diapers
            if (d.category == ItemCategory.Valuable) w *= TierBoost(prof, m.tier);
            else if (m.tier > 1) w *= DistrictItemBoost;
            if (w <= 0f) continue;
            scratchDefs.Add(d);
            scratchWeights.Add(w);
        }
        if (scratchDefs.Count == 0) return null;
        float sum = 0f;
        foreach (var w in scratchWeights) sum += w;
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < scratchDefs.Count; i++)
        {
            r -= scratchWeights[i];
            if (r <= 0) return scratchDefs[i];
        }
        return scratchDefs[scratchDefs.Count - 1];
    }

    /// <summary>Valuable pool weight × for an item tier (profile.tierBoost; index = item tier, missing = ×1).</summary>
    static float TierBoost(LootProfile prof, int tier)
    {
        var tb = prof != null ? prof.tierBoost : null;
        return tb != null && tier >= 0 && tier < tb.Length ? tb[tier] : 1f;
    }

    static int WeightedIndex(System.Random rng, float[] weights)
    {
        float sum = 0f;
        foreach (var w in weights) sum += w;
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return i;
        }
        return weights.Length - 1;
    }

    // ------------------------------------------------------------------ item rolls

    public static ItemInstance RollItem(ItemDef def, System.Random rng, string foundAt)
    {
        Ensure();
        if (def == null) def = all[0];
        float t = (float)rng.NextDouble();
        bool valuable = def.category == ItemCategory.Valuable;
        // Valuables skew toward decent condition (nobody throws out a mint Faberge egg... except they do).
        float cond = valuable
            ? Mathf.Lerp(0.55f, 1f, Mathf.Pow((float)rng.NextDouble(), 0.7f))
            : Mathf.Lerp(0.35f, 1f, (float)rng.NextDouble());
        var item = new ItemInstance
        {
            uid = GameSession.NewUid(),
            defId = def.id,
            massKg = Mathf.Lerp(def.minMass, def.maxMass, t),
            value = valuable ? Mathf.Round(Mathf.Lerp(def.minValue, def.maxValue, (float)rng.NextDouble()) * cond) : 0f,
            condition = cond,
            foundAt = foundAt,
            dayFound = GameSession.Day,
        };
        ApplySpecial(item, def, rng);
        if (valuable) item.value = Mathf.Max(1f, item.value);
        return item;
    }

    static string Pick(System.Random rng, string[] pool) => pool[rng.Next(pool.Length)];

    static readonly string[] Engravings =
    {
        "Forever yours - Dave",
        "To Linda, from Steve (sorry)",
        "You're my lobster - R",
        "Til death do us part - M & J",
        "Property of Greg. Hands off.",
        "To my wife, from the guy who forgot our anniversary",
        "Always & forever-ish - T",
        "Love you more than pizza - Sal",
        "Don't lose this. - Mom",
        "B + D 4EVER (2019-2021)",
        "If found, please return to Brenda",
        "Second time's the charm - Phil",
        "I do (again) - Rick",
        "To Kevin, my one and only (this year)",
        "You had me at 'free breadsticks'",
    };

    static readonly string[] Leftovers =
    {
        "Label: 'DO NOT EAT - GARY'. Gary is gone.",
        "Label: 'Tuesday?'",
        "Label: 'Science project (Mom don't throw out)'",
        "Label: '???'",
        "Label: 'Grandma's casserole. Sorry Grandma.'",
    };

    static readonly string[] Headlines =
    {
        "Headline: LOCAL MAN LOSES RING",
        "Headline: RACCOONS UNIONIZE",
        "Headline: GARBAGE DAY MOVED TO THURSDAY",
        "Headline: MAN WINS LOTTERY, THROWS AWAY TICKET",
        "Headline: GNOME SIGHTINGS ON THE RISE",
        "Headline: AREA DAD FINALLY CLEANS GARAGE",
    };

    static readonly string[] Mixtapes =
    {
        "'Summer Jamz 97 (DO NOT TAPE OVER)'",
        "'Songs 2 Cry 2 Vol. 4'",
        "'ROAD TRIP BANGERS!!!'",
        "'For Jenny (please call me)'",
        "'Slow Jamz for Slow Dancing'",
        "'Dad's Rock Mix (LOUD)'",
    };

    static readonly string[] Schools = { "Westfield", "Pinecrest", "North Valley", "Lakeview", "St. Bartholomew's", "Maple Grove" };
    static readonly string[] Mascots = { "Wolverines", "Fighting Clams", "Otters", "Mighty Moose", "Screaming Eagles", "Angry Beavers" };
    static readonly string[] Signers = { "Babe Rude", "Slugger McBatface", "Lefty Two-Gloves", "Dusty Homerun", "Big Bat Barry" };
    static readonly string[] Surnames = { "Kowalski", "Henderson", "Nakamura", "O'Doyle", "Buttersworth", "Grimsby", "Delgado" };
    static readonly string[] KidNames = { "Tyler", "Madison", "Brayden", "Ava", "Jaxon", "Lily", "Ethan" };
    static readonly string[] PetNames = { "Mr. Beeps", "Sir Poopsalot", "Gerald", "Princess Byte", "Lil Blip" };

    /// <summary>Per-item flavor notes and special value rolls. Uses the same rng so it stays deterministic.</summary>
    static void ApplySpecial(ItemInstance item, ItemDef def, System.Random rng)
    {
        switch (def.id)
        {
            case "wedding_ring":
                item.note = "Engraved: \"" + Pick(rng, Engravings) + "\"";
                break;
            case "class_ring":
                item.note = $"Class of '{rng.Next(78, 100):00} - {Pick(rng, Schools)} {Pick(rng, Mascots)}";
                break;
            case "mystery_leftovers":
                item.note = Pick(rng, Leftovers);
                break;
            case "newspaper":
                item.note = Pick(rng, Headlines);
                break;
            case "kids_homework":
                item.note = $"Name: {Pick(rng, KidNames)}. Grade: A-. 'See me about the dinosaur drawing.'";
                break;
            case "mixtape":
                item.note = "Label: " + Pick(rng, Mixtapes);
                break;
            case "tamagochu":
                item.note = $"Pet name: '{Pick(rng, PetNames)}'. Status: STARVING. Age: {rng.Next(4000, 9999)} days.";
                break;
            case "signed_baseball":
                item.note = $"Signed: '{Pick(rng, Signers)}'. Probably legit.";
                break;
            case "captain_raccoon_1":
                item.note = Pick(rng, new[]
                {
                    "Condition: Near Mint (one Cheeto thumbprint)",
                    "Condition: Very Fine (smells like basement)",
                    "Condition: Fair (someone colored in the raccoon)",
                });
                break;
            case "war_medal":
                item.note = $"Awarded to Pvt. {Pick(rng, Surnames)}. For Valor (and snacks).";
                break;
            case "pocket_watch":
                item.note = Pick(rng, new[]
                {
                    "Engraved: 'To J.P. - 40 years of never being on time'",
                    "Engraved: 'Tick tock, Bartholomew'",
                    "Engraved: 'Time flies. So did your father.'",
                });
                break;
            case "rollex_watch":
            {
                // Huge spread: mostly knockoffs, sometimes the real deal.
                double r = rng.NextDouble();
                if (r < 0.65)
                {
                    item.value = Mathf.Round(Mathf.Lerp(40f, 140f, (float)rng.NextDouble()) * item.condition);
                    item.note = Pick(rng, new[]
                    {
                        "Ticks loudly. Too loudly. The crown is a Tic Tac.",
                        "Made in 'Switzerlnd'. Hmm.",
                        "The gold is rubbing off onto your thumb.",
                    });
                }
                else
                {
                    item.value = Mathf.Round(Mathf.Lerp(700f, 1500f, (float)rng.NextDouble()) * item.condition);
                    item.note = "Heavy, smooth, smug. Honestly? This one might be legit.";
                }
                break;
            }
            case "cursed_doll":
            {
                double r = rng.NextDouble();
                if (r < 0.55)
                {
                    item.value = Mathf.Round(Mathf.Lerp(15f, 60f, (float)rng.NextDouble()));
                    item.note = "It blinked. Probably dust. Probably.";
                }
                else if (r < 0.9)
                {
                    item.value = Mathf.Round(Mathf.Lerp(60f, 260f, (float)rng.NextDouble()));
                    item.note = "Hums lullabies at 3 a.m. Collectors love that.";
                }
                else
                {
                    item.value = Mathf.Round(Mathf.Lerp(500f, 900f, (float)rng.NextDouble()));
                    item.note = "Definitely haunted. Certified. Collectors are FOAMING.";
                }
                break;
            }
            case "sock_of_cash":
                item.note = $"Counted: {Economy.Money(item.value)} in mostly ones. And one button.";
                break;
            case "moon_rock":
                item.note = Pick(rng, new[]
                {
                    "Certificate signed 'NASA (probably)'.",
                    "Label: 'MOON ROCK - DO NOT LICK'.",
                    "Smells faintly of cheese. Encouraging.",
                    "Came in a box marked 'NOT a driveway rock'.",
                });
                break;
            case "lottery_ticket":
            {
                var nums = new List<int>();
                while (nums.Count < 6)
                {
                    int x = rng.Next(1, 50);
                    if (!nums.Contains(x)) nums.Add(x);
                }
                nums.Sort();
                item.note = $"Numbers: {string.Join(" ", nums)}. Matched 5 of 6!";
                break;
            }
            case "gold_dentures":
                item.note = "Still has a sesame seed stuck in it. Bonus!";
                break;
            case "pigcasso":
                item.note = Pick(rng, new[] { "Titled 'Oink No. 5'. Signed with a hoofprint.", "Titled 'Self Portrait With Mud'.", "Titled 'Starry Sty'." });
                break;
            case "faberge_egg":
                item.note = "Opens to reveal a smaller egg. Which opens to reveal a smaller egg...";
                break;
            case "love_letters":
                item.note = $"Signed 'Forever yours - Dave'. Addressed to {Pick(rng, new[] { "Linda", "Carol", "Brenda", "Susan" })}.";
                break;
            case "diamond_earring":
                item.note = "Left one. Or right one. Hard to say without the other one.";
                break;
            case "junk_mail":
                item.note = "Addressed to 'Current Resident'. That's you, kind of.";
                break;
        }
    }
}

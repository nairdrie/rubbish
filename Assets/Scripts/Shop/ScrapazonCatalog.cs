using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The shop's brand. The store name lives in exactly one place so it can be renamed in seconds
/// (alternatives pitched to the user: "Bin There Depot", "Hoarder's Hardware").
/// </summary>
public static class Scrapazon
{
    public const string Name = "Scrapazon";

    /// <summary>"www.scrapazon.com"</summary>
    public static string Domain
    {
        get
        {
            var sb = new System.Text.StringBuilder("www.");
            foreach (char c in Name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Append(".com").ToString();
        }
    }
}

/// <summary>
/// Upgrade / consumable ids. CONTRACT (Docs/LivingWorld.md §4): other systems read these through
/// GameSession.GetUpgradeLevel / GetConsumable.
/// </summary>
public static class ShopIds
{
    // Numbers for every upgrade level live in UpgradeEffects (Docs/Progression.md §6); levels below are the §6 maxima.
    public const string RabiesShot = "rabies_shot";     // consumable: cures rabies (auto-used when infected)
    public const string Repellent = "repellent";        // consumable: one bottle protects you until morning (Critters spend it)
    public const string TruckModel = "truck_model";       // 0 = 120 kg Curb Pup; 1 = 200 kg rear-loader; upgrades transfer
    public const string TruckCapacity = "truck_capacity"; // upgrade 0-4: +40 / +80 / +120 / +180 kg on either chassis
    public const string TruckTurbo = "truck_turbo";     // upgrade 0-3: absolute engine tiers (L1 = today's turbo)
    public const string AirHorn = "air_horn";           // upgrade 0-2: horn scare radius 12 -> 25 -> 40 m
    public const string Headlamp = "headlamp";          // Callings 0-2 (not sold): light on your head at night; L2 UV treasure glow
    public const string ConveyorBelt = "conveyor_belt"; // upgrade 0-3 (Bag Belt, auto-slitter, twin lanes)
    public const string MagnusBrain = "magnus_brain";   // upgrade 0-4: L1 = Magnus's job (rail branch + tongs + brain), MagnusCareer

    // ---- §6.1 truck
    public const string TruckHandling = "truck_handling";       // 0-3
    public const string TruckArm = "truck_arm";                 // 0-3 side-loader
    public const string TruckHeadlights = "truck_headlights";   // 0-2
    // ---- §6.2 garage
    public const string AutoTipper = "auto_tipper";             // 0-1 (truck dept: the truck tips itself; old L2 is refunded on load)
    public const string SortingTable = "sorting_table";         // 0-2
    public const string CompactorMk2 = "compactor_mk2";         // 0-3
    public const string RecyclingContracts = "recycling_contracts"; // 0-4
    public const string HazmatContract = "hazmat_contract";     // 0-2
    public const string TrophyShelf = "trophy_shelf";           // 0-2
    // ---- §6.8 displays (garage dept): show off your finds (DisplayPlanner)
    public const string DisplayPlankShelf = "display_plank_shelf"; // 0-1
    public const string DisplayGlassCase = "display_glass_case";   // 0-1
    public const string DisplayPedestal = "display_pedestal";      // 0-1
    public const string DisplayVault = "display_vault";            // 0-1
    // ---- §6.3 Magnus (one career path: MagnusCareer.Steps)
    public const string MagnusRailMotor = "magnus_rail_motor";  // 0-1 (old saves' L2 clamps to 1)
    public const string MagnusGripper = "magnus_gripper";       // 0-2: lobster pincer, quad-claw (old saves' L3 clamps to 2)
    public const string MagnusVision = "magnus_vision";         // 0-2
    // ---- §6.4 personal gear: NOT sold on Scrapazon any more. Printed in SolidWaste 3D (CallingCatalog / CallingIds
    //      carry the same strings); effects still read these ids through GameSession.GetUpgradeLevel.
    public const string WorkGloves = "work_gloves";             // 0-3
    public const string WorkBoots = "work_boots";               // 0-2
    public const string TreasureWand = "treasure_wand";         // 0-2
    public const string RabiesVaccine = "rabies_vaccine";       // 0-2
    public const string HardHat = "hard_hat";                   // 0-1
    // ---- F3 bag personalities and tools (Docs/Multiplayer.md §14.3)
    public const string BagCannon = "bag_cannon";               // 0-1 truck: hold R with a bag near your truck to lob it in from ~10 m
    public const string GrabberPole = "grabber_pole";           // 0-1 Callings "Grabby Hand" (not sold): V = long reach grab / poke / snatch
    // ---- §6.5 business + permits
    public const string PermitMinivanMeadows = "permit_minivan_meadows";       // 0-1
    public const string PermitSnootingtonHills = "permit_snootington_hills";   // 0-1
    public const string PermitBillionaireBluffs = "permit_billionaire_bluffs"; // 0-1
    public const string PermitCouponCommons = "permit_coupon_commons";       // 0-1
    public const string PermitStackingtonHeights = "permit_stackington_heights"; // 0-1
    public const string EbaePowerSeller = "ebae_power_seller";  // 0-3
    public const string GoldenTruck = "golden_truck";           // 0-1 (needs the Key to the City)
    // ---- §6.6 consumables
    public const string GarbageJuice = "garbage_juice";
    public const string LuckyGnome = "lucky_gnome";
    public const string GoldenGnome = "golden_gnome";
    // ---- §6.7 cosmetics
    public const string TruckPaintPrimer = "truck_paint_primer";
    public const string TruckPaintFlames = "truck_paint_flames";
    public const string TruckPaintRaccoonCamo = "truck_paint_raccoon_camo";
    public const string TruckPaintChrome = "truck_paint_chrome";
    public const string HornPackMusical = "horn_pack_musical";
    public const string TrashFmRadio = "trash_fm_radio";
    public const string GarageDiscoBall = "garage_disco_ball";
    // ---- pseudo-id (prereq only, never sold): GameSession.KeyToCity
    public const string KeyToTheCity = "key_to_the_city";
    // ---- §7.5 teaser (Coming Soon™ only, never sold)
    public const string FranchiseMode = "franchise_mode";
}

public enum ShopItemKind { Consumable, Upgrade, Teaser }

[Serializable]
public class ScrapazonReview
{
    public int stars;
    public string text;
    public string author;

    public ScrapazonReview(int stars, string text, string author)
    {
        this.stars = stars; this.text = text; this.author = author;
    }
}

/// <summary>One product on Scrapazon.</summary>
public class ScrapazonItem
{
    public string id;
    public ShopItemKind kind;
    public string[] tags = Array.Empty<string>();   // nav filters: the dept + cross-listings (critters, pharmacy...)
    public string title;
    public string brand;
    public float[] prices = Array.Empty<float>();   // upgrades: price of level i+1; consumables: [price]
    public int maxLevel = 1;                        // upgrades: max level; consumables: pocket cap
    public float listPrice;                         // the strikethrough "List:" price (a lie)
    public float rating;                            // 0..5
    public int ratingCount;
    public string badge;                            // "#1 Best Seller in ..." ribbon (optional)
    public string teaserLine;                       // teasers: "BACK-ORDERED since 1997"
    public ScrapazonReview[] reviews = Array.Empty<ScrapazonReview>();

    // ---- progression (Docs/Progression.md §6, §9.2). Per-level arrays are indexed by level - 1; empty = no gate / no text.
    public string dept;                                   // truck, garage, magnus, gear, business, permits, pharmacy, cosmetics
    public int[] rankRequired = Array.Empty<int>();       // Business Rank needed for each level
    public string[][] prereqs = Array.Empty<string[]>();  // per level: "id:level" (or "key_to_the_city")
    public string[] levelTitles = Array.Empty<string>();
    public string[] levelBlurbs = Array.Empty<string>();
    public string blurb;
    /// <summary>Plain-words effect of each level (the green "what it does" box). Index = level - 1.</summary>
    public string[] levelEffects = Array.Empty<string>();

    public bool IsUpgrade => kind == ShopItemKind.Upgrade;
    public bool IsConsumable => kind == ShopItemKind.Consumable;
    public bool IsTeaser => kind == ShopItemKind.Teaser;
    public bool HasTag(string tag) => Array.IndexOf(tags, tag) >= 0;

    /// <summary>Entry of a per-level array for 1-based <paramref name="level"/> (clamped), or null.</summary>
    public static string AtLevel(string[] arr, int level)
    {
        if (arr == null || arr.Length == 0) return null;
        return arr[Mathf.Clamp(level - 1, 0, arr.Length - 1)];
    }

    /// <summary>Business Rank needed for 1-based <paramref name="level"/> (1 when ungated).</summary>
    public int RankFor(int level)
    {
        if (rankRequired == null || rankRequired.Length == 0 || level < 1) return 1;
        return rankRequired[Mathf.Clamp(level - 1, 0, rankRequired.Length - 1)];
    }

    /// <summary>Title of 1-based <paramref name="level"/> (falls back to <see cref="title"/>).</summary>
    public string TitleFor(int level) => AtLevel(levelTitles, level) ?? title;

    /// <summary>Blurb of 1-based <paramref name="level"/> (falls back to <see cref="blurb"/>).</summary>
    public string BlurbFor(int level) => AtLevel(levelBlurbs, level) ?? blurb;

    /// <summary>Effect line of 1-based <paramref name="level"/>, or null.</summary>
    public string EffectFor(int level) => AtLevel(levelEffects, level);
}

/// <summary>Everything Scrapazon sells (and a couple of things it pretends to). Docs/Progression.md §6.</summary>
public static class ScrapazonCatalog
{
    public const float CapacityPerLevelKg = 40f;

    static List<ScrapazonItem> all;

    public static IReadOnlyList<ScrapazonItem> All
    {
        get
        {
            if (all == null) { all = Build(); RideShop.AddTo(all); WorkshopDecorShop.AddTo(all); } // F1 riding upgrades (Riding/RideShop.cs)
            return all;
        }
    }

    public static ScrapazonItem Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var it in All) if (it.id == id) return it;
        return null;
    }

    /// <summary>Nav filters shown in the Scrapazon header strip: (tag, label). "career" is the Career page, not a filter.</summary>
    public static readonly (string tag, string label)[] Departments =
    {
        ("all", "All"),
        ("truck", "Truck"),
        ("garage", "Garage"),
        ("magnus", "Magnus"),
        ("gear", "Gear"),
        ("critters", "Critters"),
        ("pharmacy", "Pharmacy"),
        ("business", "Business"),
        ("permits", "Permits"),
        ("cosmetics", "Cosmetics"),
        (WorkshopDecorShop.Tag, "Decor"),   // workshop furniture and gadgets (Workshop/WorkshopDecorShop.cs)
        ("career", "Career"),
        ("soon", "Coming Soon™"),
    };

    public const string CareerTag = "career";

    // ------------------------------------------------------------------ builders

    static ScrapazonReview R(int stars, string text, string author) => new ScrapazonReview(stars, text, author);
    static string[] P(params string[] ids) => ids;

    /// <summary>A multi-level upgrade. Title = level 1's title.</summary>
    static ScrapazonItem Up(string id, string dept, string[] tags, string brand, float[] prices, int[] ranks,
                            string[] titles, string[] blurbs, string[] effects, float list, float rating, int count,
                            string badge, ScrapazonReview[] reviews, string[][] prereqs = null)
    {
        var tagList = new List<string> { dept };
        if (tags != null) foreach (var t in tags) if (!tagList.Contains(t)) tagList.Add(t);
        return new ScrapazonItem
        {
            id = id, kind = ShopItemKind.Upgrade, dept = dept, tags = tagList.ToArray(), brand = brand,
            prices = prices, maxLevel = prices.Length, rankRequired = ranks,
            title = titles[0], levelTitles = titles, levelBlurbs = blurbs ?? Array.Empty<string>(), blurb = blurbs != null && blurbs.Length > 0 ? blurbs[0] : null,
            levelEffects = effects ?? Array.Empty<string>(),
            listPrice = list, rating = rating, ratingCount = count, badge = badge,
            reviews = reviews ?? Array.Empty<ScrapazonReview>(),
            prereqs = prereqs ?? Array.Empty<string[]>(),
        };
    }

    static ScrapazonItem Consumable(string id, string dept, string[] tags, string title, string brand, float price, int cap,
                                    int rank, string blurb, string effect, float list, float rating, int count, string badge,
                                    ScrapazonReview[] reviews)
    {
        var tagList = new List<string> { dept };
        if (tags != null) foreach (var t in tags) if (!tagList.Contains(t)) tagList.Add(t);
        return new ScrapazonItem
        {
            id = id, kind = ShopItemKind.Consumable, dept = dept, tags = tagList.ToArray(), title = title, brand = brand,
            prices = new[] { price }, maxLevel = cap, rankRequired = new[] { rank }, blurb = blurb,
            levelEffects = effect != null ? new[] { effect } : Array.Empty<string>(),
            listPrice = list, rating = rating, ratingCount = count, badge = badge,
            reviews = reviews ?? Array.Empty<ScrapazonReview>(),
        };
    }

    static ScrapazonItem Cosmetic(string id, string[] tags, string title, string brand, float price, int rank, string blurb,
                                  string effect, float list, float rating, int count, ScrapazonReview[] reviews) =>
        Up(id, "cosmetics", tags, brand, new[] { price }, new[] { rank }, new[] { title }, new[] { blurb }, new[] { effect },
           list, rating, count, null, reviews);

    static List<ScrapazonItem> Build() => new List<ScrapazonItem>
    {
        // =============================================================== PHARMACY / CRITTERS (consumables)
        new ScrapazonItem
        {
            id = ShopIds.RabiesShot, kind = ShopItemKind.Consumable, dept = "pharmacy", tags = new[] { "pharmacy", "critters" },
            title = "Rabies Shot™ (Human-Sized, Very Large Needle)",
            brand = "Dr. Needleman's Discount Pharmacy",
            prices = new[] { 35f }, maxLevel = 5, listPrice = 99.99f, rating = 4.4f, ratingCount = 1204, rankRequired = new[] { 1 },
            badge = "#1 Best Seller in Getting Bitten",
            reviews = new[]
            {
                R(4, "my raccoon bite is gone", "Gary"),
                R(5, "needle was the size of a baguette. 10/10 would scream again", "Pam"),
                R(2, "cured my rabies but now i'm afraid of doctors AND raccoons", "Dale"),
                R(5, "arrived before i clicked buy. is that legal", "Trent"),
                R(4, "cheaper than the hospital. the hospital also has raccoons", "Marisol"),
            }
        },
        new ScrapazonItem
        {
            id = ShopIds.Repellent, kind = ShopItemKind.Consumable, dept = "critters", tags = new[] { "critters", "pharmacy" },
            title = "Eau de Raccoon-Be-Gone (Night Spray, 1 Bottle)",
            brand = "Stinkwell Fragrances",
            prices = new[] { 20f }, maxLevel = 9, listPrice = 49.99f, rating = 3.9f, ratingCount = 877, rankRequired = new[] { 1 },
            badge = "Scrapazon's Choice",
            reviews = new[]
            {
                R(4, "raccoons won't come near me. neither will my wife", "Rick"),
                R(5, "smells like a wet penny in a gym sock. works great", "Barb"),
                R(5, "a raccoon read the label and left", "Nguyen"),
                R(3, "sprayed it on my cereal by accident. 3 stars", "Kyle"),
            }
        },
        // F4 night shift (Docs/Multiplayer.md §14.4): press T on foot to set it; Animal Control bounty or a shop pet.
        Consumable(RaccoonTrap.ConsumableId, "critters", new[] { "critters", "gear" }, "Humane-ish Raccoon Trap (Trash Can, Fish Included)",
            "Bandit Begone Outfitters", 45f, 4, 1, "A trash can on its side with a fish in it. Raccoons can't resist. Neither could our intern.",
            "Press T on foot to set it. At night raccoons sniff their way in: call Animal Control for a bounty, or keep one as a shop pet.",
            89.99f, 4.1f, 532, "Raccoons hate this one weird trick",
            new[]
            {
                R(5, "caught a raccoon. caught my neighbor's cat. caught my neighbor", "Hank"),
                R(4, "named him Bandit. he lives on my shelf now. he judges me", "Priya"),
                R(2, "the fish was the best part honestly", "Doug"),
            }),
        Consumable(ShopIds.GarbageJuice, "pharmacy", new[] { "gear" }, "Garbage Juice™ Energy Drink (Hiccup Flavor)", "Hurl Beverage Co.",
            15f, 5, 1, "Tastes like a dumpster's dreams. Extremely legal.",
            "+25% move and carry speed for 2 in-game hours. Hiccups.", 39.99f, 4.2f, 2290, "Limited time deal",
            new[]
            {
                R(5, "i can hear colors now *hic*", "Brayden"),
                R(4, "carried 3 bags uphill. both ways. *hic*", "Rosa"),
                R(2, "the hiccups have lasted 9 days", "Elliot"),
            }),
        Consumable(ShopIds.LuckyGnome, "gear", null, "Lucky Garden Gnome (Rub Belly, Receive Bling)", "Gnome Depot",
            150f, 3, 3, "He's lucky. You're not. Borrow some.",
            "For 24 in-game hours, bags put out in tier 1-2 districts are ×1.25 likelier to hold treasure. Extra gnomes wait in line.",
            399f, 4.5f, 611, null,
            new[]
            {
                R(5, "rubbed his belly, found a wedding ring. rubbed it again, found a second wedding ring. concerning", "Lou"),
                R(4, "he stares at me while i sleep. but the BLING", "Hattie"),
                R(3, "lucky? sure. pleasant? no", "Dmitri"),
            }),
        Consumable(ShopIds.GoldenGnome, "gear", null, "GOLDEN Garden Gnome (He Knows What He's Worth)", "Gnome Depot Platinum",
            7500f, 2, 9, "Solid gold-ish. Insufferable. Effective.",
            "For 24 in-game hours, every district is ×1.40 likelier to hold treasure, and each Rare roll has a 10% chance to become Epic.",
            19999f, 4.8f, 37, "Scrapazon's Choice",
            new[]
            {
                R(5, "he made eye contact with a bag and it became a tiara", "Prudence"),
                R(5, "costs more than my car. finds more than my car", "Baxter"),
                R(4, "demanded his own parking spot. got one", "Gnome Union Rep"),
            }),

        // =============================================================== TRUCK
        Up(ShopIds.TruckModel, "truck", null, "Secondhand Sanitation Motors",
            new[] { TruckProgression.RearLoaderPrice }, new[] { 1 },
            new[] { "Rear-loader Garbage Truck (Curb Pup Trade-In)" },
            new[] { "Your first proper big rig. Longer routes, bigger piles. The mirrors are finally proportional." },
            new[] { "Replaces your Curb Pup: 200 kg stock payload (+80 kg). All hopper upgrades, equipment and cargo carry over. Trade-in included; no extra fee." },
            1999f, 4.7f, 284, "Your next big truck",
            new[]
            {
                R(5, "the little truck grew up. i cried. then i filled it with garbage", "Mabel"),
                R(4, "kept my sideboards and my terrible paint job. no hidden fees", "Dwayne"),
                R(5, "we look like a real company now. we are not a real company", "Pat"),
            }),
        Up(ShopIds.TruckCapacity, "truck", null, "Big Bin Energy LLC",
            new[] { 100f, 450f, 1200f, 3000f }, new[] { 1, 2, 3, 5 },
            new[] { "Hopper Extension Kit (+40 kg, Duct Tape Included)", "Hopper Extension Kit (+40 kg, Duct Tape Included)",
                    "Hopper Extension Kit (+40 kg, Duct Tape Included)", "Hopper Mk II: Compaction Ram" },
            new[] { "Duct-taped sideboards. Holds 40 more kg of regret.", "Duct-taped sideboards. Holds 40 more kg of regret.",
                    "Duct-taped sideboards. Holds 40 more kg of regret.", "Squishes bags as they go in. Also squishes gloves. Keep hands out." },
            new[] { "+40 kg payload. Fits either truck and transfers free when you trade up.",
                    "+80 kg total payload over your truck's stock hopper. Transfers free.",
                    "+120 kg total payload over stock. Transfers free.",
                    "+180 kg total payload over stock. A ram plate thumps every bag that goes in. Transfers free." },
            1999f, 4.6f, 3310, "#1 Best Seller in Truck Stuff",
            new[]
            {
                R(5, "my truck now holds 40 more kilograms of regret", "Hank"),
                R(4, "installed it myself with duct tape. only lost one finger", "Deb"),
                R(5, "fit an entire couch in there. the couch was full of raccoons", "Omar"),
                R(5, "the ram plate goes THUNK. i make it go THUNK for fun now", "Marguerite"),
            }),
        Up(ShopIds.TruckTurbo, "truck", null, "Fast & Filthy Motorsports",
            new[] { 300f, 2800f, 11000f }, new[] { 2, 4, 7 },
            new[] { "TURBO Garbage Truck Engine Swap (Flames Not Included*)", "Twin-Turbo 'Stinkmaster' V8", "Jet-Assisted Hopper Thrusters" },
            new[] { "Garbage, but faster.", "Smells faster.", "Rear-mounted. Yes, the garbage gets warm." },
            new[] { "675 Nm per wheel, top speed 12.5 m/s (+25%). Flames: yes.",
                    "800 Nm per wheel, top speed 15 m/s, stronger brakes.",
                    "900 Nm per wheel, top speed 17 m/s, stronger brakes and a cornering assist." },
            4999f, 4.7f, 942, "Limited time deal",
            new[]
            {
                R(4, "went from 0 to 60 in 0 to 60 seconds", "Chad"),
                R(5, "the truck screams when i press W now. so do i", "Luis"),
                R(5, "got a speeding ticket in a garbage truck. worth it", "Mo"),
                R(3, "*flames were included", "Legal Dept."),
            }),
        Up(ShopIds.TruckHandling, "truck", null, "Tony's Tire & Tantrum",
            new[] { 250f, 2000f, 9000f }, new[] { 2, 4, 8 },
            new[] { "Sticky Tires + Unflip Button", "Racing Suspension (Tuned by a Guy Named Tony)", "Drift Kit for Garbage Trucks" },
            new[] { "For when physics wins.", "Tony says it's fine.", "Tokyo Drift, but it smells." },
            new[] { "Grippier tires, a lower center of gravity, and a FREE instant unflip (hold R for 0.5 s, no tow fee).",
                    "Even grippier, plus an anti-tip assist below 12 m/s.",
                    "Grippiest. Hold Space while driving to handbrake-drift." },
            1499f, 4.3f, 1288, null,
            new[]
            {
                R(5, "flipped my truck 11 times. unflipped it 11 times. no tow guy. tow guy is sad", "Wendell"),
                R(4, "tony came with the suspension. tony lives in my garage now", "Kat"),
                R(5, "drifted a garbage truck through a cul-de-sac. the HOA is drafting a letter", "Ryo"),
            }),
        Up(ShopIds.TruckArm, "truck", null, "Grabby Industries",
            new[] { 1800f, 9000f, 39000f }, new[] { 3, 6, 10 },
            new[] { "Grabby Arm™ Side-Loader (Press X to Grab)", "Grabby Arm 2: Crawl & Grab", "Octo-Arm 9000 (Both Sides, No Mercy)" },
            new[] { "It has one job and a 95% success rate. That's an A.",
                    "Never leave the cab again. Your legs will atrophy. Worth it.",
                    "Eight arms' worth of grabbing in two arms. Don't ask." },
            new[] { "Stopped at a curb? Hold X: grabs bags on the RIGHT within 2.4 m. 2.5 s per bag. 5% fumbles.",
                    "Press X for AUTO: grabs from both sides while you crawl (up to 3 m/s). 3.2 m reach, 1.6 s per bag, no fumbles.",
                    "AUTO up to 4 m/s, 5 m reach on both sides (drive the center line), 0.9 s per bag, plus a Litter Vac for loose items." },
            59999f, 4.8f, 4012, "#1 Best Seller in Never Leaving the Cab",
            new[]
            {
                R(5, "it grabbed my bag. then my neighbor's mailbox. 5 stars", "Gus"),
                R(4, "95% success rate. the other 5% bonk off the roof. it's art", "Nadia"),
                R(5, "my legs have atrophied. my bank account has not", "Cyrus"),
                R(1, "it grabbed me", "Raccoon (verified purchase)"),
            },
            new[] { P("truck_handling:1"), P("truck_arm:1"), P("truck_arm:2", "truck_capacity:4") }),
        // Tipping lives on the truck now (TruckTipper): one level, the fast dump. Its old level 2 ("Direct Feed") is gone;
        // saves that owned it keep level 1 and are refunded on load (UpgradeMigrations.MigrateAutoTipper).
        Up(ShopIds.AutoTipper, "truck", null, "Leverage Labs",
            new[] { 600f }, new[] { 3 },
            new[] { "Auto-Tipper (Hydraulic Hopper Ram, Goes FWOOMP)" },
            new[] { "The whole load, out the back, in three seconds. Stand clear. Stand very clear." },
            new[] { "TIP IT (hold G in the driver's seat) empties the hopper in 3 s instead of 10 s. Works anywhere you can tip." },
            1499f, 4.2f, 402, null,
            new[]
            {
                R(5, "held G. blinked. the truck was empty and my eyebrows were gone. 5 stars", "Ottilie"),
                R(3, "it went FWOOMP at a red light. that was me. i held the button. still its fault", "Gerald"),
                R(5, "ten seconds was too long to think about my choices. three is perfect", "Priscilla"),
            }),
        Up(ShopIds.AirHorn, "truck", new[] { "critters" }, "HONKCO",
            new[] { 120f, 1200f }, new[] { 1, 6 },
            new[] { "Industrial Air Horn (Scares Raccoons, Neighbors, God)", "Foghorn of the Gods" },
            new[] { "HOOOONK.", "Audible from space. Several complaints from space." },
            new[] { "Honking (H) startles critters within 25 m instead of 12 m. Raccoons recover quickly and bite back.",
                    "Honking startles critters within 40 m. Nearby hedges shake. Angry raccoons do not stay scared." },
            299f, 4.8f, 2051, "#1 Best Seller in Critter Defense",
            new[]
            {
                R(5, "scared a raccoon so bad it paid my rent", "Ashley"),
                R(5, "my neighbors hate me now. 5 stars", "Doug"),
                R(5, "HOOOOOOOOONK", "Goose"),
                R(4, "can confirm it is louder. i can no longer confirm anything else", "Bev"),
            }),
        Up(ShopIds.TruckHeadlights, "truck", new[] { "critters" }, "Brightish Ideas",
            new[] { 200f, 4500f }, new[] { 2, 6 },
            new[] { "Brighter-ish Headlights", "Stadium Light Bar" },
            new[] { "Now 40% less 'is that a raccoon or a bag?'", "Turns night into a Tuesday afternoon." },
            new[] { "Headlight range ×1.6. Calm raccoons within 20 m briefly flinch; angry and foamy ones ignore it.",
                    "A roof light bar: brief flinches out to 30 m, plus within 8 m of the truck. Better visibility, not a raccoon force field." },
            899f, 4.4f, 733, null,
            new[]
            {
                R(5, "it WAS a raccoon. now it's a very well-lit angry raccoon", "Imani"),
                R(4, "the light bar is visible from the moon. the moon complained", "Felix"),
                R(3, "moths have unionized around my truck", "Stan"),
            }),

        // =============================================================== GARAGE
        // Conveyor (Docs/Progression.md §6.2, ConveyorRules): it carries bags and never opens one. Prices and ranks were
        // rebalanced with the rework (was $1,500 / $9,000 / $29,000 at ranks 4 / 6 / 9).
        Up(ShopIds.ConveyorBelt, "garage", null, "Beltway Industries",
            new[] { 900f, 1200f, 14000f }, new[] { 3, 5, 8 },
            new[] { "Bag Belt (Automatic*, Loud)", "Heavy-Duty Belt Motor", "Junk Diverter (Shake 'n' Sift Grate)" },
            new[] { "Finally back in stock (1997-2026).", "It used to say 'no' to the big ones. Now it says 'hnnngh'.", "Shakes every bag like it owes it money." },
            new[] { "Bag Belt: carries normal bags from the bay pile to a queue rack beside the sorting table, one every 2 s. Grab one from the rack and rip it open. HEAVY bags are refused and stay on the pile. It never opens a bag.",
                    "Heavy-Duty Motor: the belt carries HEAVY bags to the queue too.",
                    "Junk Diverter: on the way in, each bag is shaken over a grate. Small junk (4 kg or less) drops straight into the compactor and is paid like any junk, so bags arrive with fewer items to sort. Never drops treasure, hazards or recyclables." },
            12000f, 4.9f, 12, "Finally back in stock",
            new[]
            {
                R(1, "been waiting since 1997. it arrived. i don't know what to do with my life now", "Walt"),
                R(5, "the anticipation was the product. the product is also good", "Zen"),
                R(5, "i no longer walk to the pile. i have forgotten where the pile is", "Haruki"),
                R(4, "the grate shook a banana peel out of my bag and looked proud about it", "Odette"),
            },
            new[] { P(), P("conveyor_belt:1"), P("conveyor_belt:2") }),
        Up(ShopIds.SortingTable, "garage", null, "Big Table Energy",
            new[] { 700f, 6000f }, new[] { 3, 6 },
            new[] { "XL Sorting Table (Fits 2 Bags + a Sandwich)", "Double-Wide Table: Second Sorting Spot, Cupholders & Magnet Strip" },
            new[] { "Room for two bags and your lunch. Keep them apart.", "Two can sort. Metal just... leaves." },
            new[] { "Table holds 60 items instead of 40. Sorting takes ×0.95 the time.",
                    "A second sorting spot at the left end of the table: sort beside Magnus, or two of you at once. Plus the magnet strip: slides a Metal item into the METAL bin every 1.5 s, all by itself." },
            2499f, 4.5f, 1876, null,
            new[]
            {
                R(5, "fits 2 bags and a sandwich. the sandwich is now trash. my fault", "Ines"),
                R(5, "the magnet stole my belt buckle. sorted it as metal. correct", "Hugo"),
                R(4, "cupholders are great. do not put a soda next to a raccoon", "Tamsin"),
            }),
        Up(ShopIds.CompactorMk2, "garage", null, "CrushCo Heavy Industries",
            new[] { 900f, 2500f, 8000f }, new[] { 3, 5, 9 },
            new[] { "Compactor Mk II: Auto-Bale", "Hazard Sniffer Nose", "Treasure Rescue Grate" },
            new[] { "It's hungry on its own schedule now.", "Achoo means no.", "RIP 'RIP Wedding Ring' toasts." },
            new[] { "Auto-compacts at 80% full. COMPACT and BULK COMPACT SHOP pay $0.25/kg; bulk is faster. Recycling still pays more.",
                    "Dampens hazard bangs for quieter compacting. COMPACT and BULK COMPACT SHOP pay $0.30/kg. No compaction fees at any level.",
                    "Valuables survive compaction and pop into the KEEP crate (dented, 30% value). COMPACT and BULK COMPACT SHOP pay $0.35/kg; recycling still pays more." },
            9999f, 4.6f, 2207, null,
            new[]
            {
                R(5, "the batteries used to go BANG. now the machine sneezes. my eyebrows appreciate it", "Bartholomew"),
                R(4, "auto-bales while i nap. we have an understanding", "Freya"),
                R(5, "rescued a wedding ring from bulk compact. it's dented. so is my marriage. we're both fine", "Carl"),
            },
            new[] { P(), P(), P("compactor_mk2:2") }),
        Up(ShopIds.RecyclingContracts, "garage", new[] { "business" }, "Glassy McGlassface Co.",
            new[] { 300f, 1500f, 4000f, 11500f }, new[] { 3, 5, 8, 11 },
            new[] { "Glassy McGlassface Co. Recycling Contract", "Tin Can Tony's Metals", "Big Plastic (Not That One)", "The Cardboard Cartel" },
            new[] { "A guy who buys your recycling. He seems legit.", "Tony pays cash. Tony asks no questions.",
                    "A different Big Plastic. A nicer one. Probably.", "They control the boxes. All of them." },
            new[] { "Recycling pays ×1.5 (by hand and by Magnus, after your combo).", "Recycling pays ×2.",
                    "Recycling pays ×3.", "Recycling pays ×4." },
            0f, 4.1f, 951, null,
            new[]
            {
                R(5, "glassy mcglassface showed up in a trench coat full of bottles. legit", "Nora"),
                R(4, "tin can tony bought my cans and my car. i had a car", "Desmond"),
                R(3, "the cardboard cartel sent me a box. inside was a smaller box", "Agnes"),
            }),
        Up(ShopIds.HazmatContract, "garage", new[] { "business" }, "Glowing Solutions LLC",
            new[] { 300f, 2500f }, new[] { 4, 7 },
            new[] { "Hazmat Disposal License (Laminated)", "E-Waste Buyback Program" },
            new[] { "Laminated, so it's official.", "They want your batteries. Don't ask why. Don't ask them anything." },
            new[] { "Hazard items pay $1.50 each instead of $0.75.", "Hazard items pay $4.00 each." },
            999f, 4.0f, 308, null,
            new[]
            {
                R(5, "the license glows a little. that's normal right", "Ozzie"),
                R(4, "sold them 40 batteries. they said 'more'. i'm scared", "Linnea"),
            }),
        Up(ShopIds.TrophyShelf, "garage", null, "Humble Brag Furniture",
            new[] { 1000f, 6000f }, new[] { 4, 8 },
            new[] { "Trophy Shelf (Holds 6 Finds, Judges You)", "Museum Wing (24 Plinths + Velvet Rope)" },
            new[] { "Show off your 6 best finds. The shelf will judge the rest.", "Admission: free. Touching: no." },
            new[] { "A shelf over the sort station shows 6 of your best finds (fancier displays get first pick; you can still sell them). +40 Rep every midnight.",
                    "18 more plinths and a velvet rope above it. +160 Rep every midnight." },
            2999f, 4.7f, 445, null,
            new[]
            {
                R(5, "my wedding ring sits on a shelf now. my ex can see it from the street", "Vivian"),
                R(5, "a raccoon tried to buy a ticket to the museum wing", "Curator Kev"),
                R(4, "the shelf judged me. it was right", "Ambrose"),
            }),
        // ---- the display line (Docs/Progression.md §6.8): kept finds show themselves, best find in the best piece.
        Up(ShopIds.DisplayPlankShelf, "garage", null, "Nailed It Carpentry",
            new[] { 150f }, new[] { 1 },
            new[] { "Wobbly Plank Shelf (Holds 3 Finds, Barely)" },
            new[] { "One plank. Two nails. Infinite confidence." },
            new[] { "A plank on the wall by the compactor shows 3 of your finds (you can still sell them). It wobbles. +0 Rep: nobody is impressed by a plank. You are, a little." },
            39.99f, 3.6f, 2213, "#1 Best Seller in Planks",
            new[]
            {
                R(5, "it wobbles every time i walk past. i walk past a lot", "Greta"),
                R(4, "my gold watch has never been safer. probably", "Pip"),
                R(2, "this is a plank", "Honest Abe's Lumber"),
            }),
        Up(ShopIds.DisplayGlassCase, "garage", null, "Shiny Things Showcase Co.",
            new[] { 2500f }, new[] { 6 },
            new[] { "Lit Glass Display Case (Fingerprints Sold Separately)" },
            new[] { "Like a jewelry store. In a garage. Next to a garage door." },
            new[] { "A lit glass case by the garage door shows 4 finds, turning slowly under little lights. +10 Rep every midnight while it shows something, plus Showcase Rep for each Rare (+2), Epic (+4) or Legendary (+8) inside." },
            5999f, 4.6f, 318, null,
            new[]
            {
                R(5, "put a tiara in it. the raccoons pressed their faces on the glass for an hour", "Delphine"),
                R(4, "the little lights make my dented trumpet look like a crime scene exhibit. classy", "Marv"),
                R(5, "i dust it every day. i have never dusted anything else", "Kenji"),
            }),
        Up(ShopIds.DisplayPedestal, "garage", null, "Velvet Rope Enterprises",
            new[] { 9000f }, new[] { 9 },
            new[] { "Pedestal of Honor (Velvet Rope + Spotlight)" },
            new[] { "For your single greatest find. Do not touch. Do not breathe on it." },
            new[] { "A marble pedestal behind a velvet rope, under its own spotlight, shows your single best find. +25 Rep every midnight while it shows something, plus double Showcase Rep for its find (a Legendary: +16)." },
            19999f, 4.9f, 77, "Museum Quality*",
            new[]
            {
                R(5, "my best find is a golden toilet seat. it has a spotlight now. i salute it every morning", "Archibald"),
                R(5, "the velvet rope has stopped zero raccoons. they respect it though", "Curator Kev"),
                R(4, "*museum quality according to the museum we made", "Legal Dept."),
            }),
        Up(ShopIds.DisplayVault, "garage", null, "Fort Knocks Security",
            new[] { 22000f }, new[] { 11 },
            new[] { "The Legendary Vault (Epic+ Only, No Riff-Raff)" },
            new[] { "A bank vault for your best garbage. The door weighs more than your truck." },
            new[] { "A gold-lit vault wall by the office (never behind the truck) with a huge round door: 6 niches for Epic and Legendary finds only. +40 Rep every midnight while it holds something, plus Showcase Rep for each find inside (Epic +4, Legendary +8)." },
            99999f, 4.8f, 12, "Ultra Exclusive",
            new[]
            {
                R(5, "the door is round. the bolts are huge. inside: a LEGENDARY rubber chicken", "Baroness Bins"),
                R(5, "my neighbors asked what's in the vault. i said 'garbage'. technically correct", "Mo"),
                R(3, "the vault says 'ooooh' when a legendary goes in. it's a door. doors don't talk", "Gus"),
            }),

        // =============================================================== MAGNUS (a career path: MagnusCareer, Docs/Progression.md §6.3)
        // Step 1 Job (brain L1: rips + sorts bags within reach) -> 2 Rail Motor -> 3 Lobster Pincer (gripper L1: 25 kg items
        // + heavy bags) -> 4 Googly Eyes (vision L1) -> 5 Treasure
        // Sense (brain L2) -> 6 Monocle (vision L2) -> 7 Hazmat (brain L3) -> 8 Quad-Claw (gripper L2) -> 9 Grandmaster (brain L4).
        // Every effect line says exactly what that step ADDS; prices from the balance sim (run.py, Magnus career).
        Up(ShopIds.MagnusBrain, "magnus", null, "Big Brain Robotics",
            new[] { 500f, 12000f, 40000f, 60000f }, new[] { 2, 7, 9, 11 },
            new[] { "Magnus Gets a Job (Rail to the Table + Kitchen Tongs + Brain Module 1)", "Brain Module 2: Treasure Sense",
                    "Brain Module 3: Hazmat Certified", "Grandmaster Module (Also Plays Chess Now. Better.)" },
            new[] { "He's always wanted a commute. And a purpose. Mostly the commute.", "He can smell bling.",
                    "Now legally allowed to touch batteries.", "The final form. He's been waiting." },
            new[] { "His first job: a rail from the chess board to your sort table, taped kitchen tongs (items up to 5 kg) and Brain Module 1. He commutes over, picks up the bags within his reach (on or within 2 m of the table, and the Bag Belt's queue), rips them open and sorts the recyclables + junk: 8 items/min, 85% accurate, 6 AM-10 PM. No conveyor needed: carry bags over and he does the rest. He can't lift HEAVY bags yet and never takes from the dump pile; treasure and hazards go to LEFTOVERS.",
                    "Adds treasure: valuables go in the KEEP crate instead of LEFTOVERS. Faster and sharper: 12 items/min, 92% accurate.",
                    "Adds hazards: into the barrel instead of LEFTOVERS. 18 items/min, 96% accurate.",
                    "Adds the night shift: works 24 hours a day at 30 items/min, 99.5% accurate. And MAGNUS PRIME at chess." },
            25000f, 5f, 3, "Now hiring",
            new[]
            {
                R(5, "he commutes 4 meters now and complains about traffic", "Kim"),
                R(4, "kitchen tongs. on a robot. he made a salad and sorted it", "Maeve"),
                R(5, "my robot arm is now smarter than me. low bar", "Ivan"),
                R(5, "it beat me at chess and then sorted my feelings", "Kim"),
                R(4, "he said 'I AM HELPING' and then he was", "Tobias"),
            },
            new[] { P(), P("magnus_vision:1"), P("magnus_gripper:1"), P("magnus_rail_motor:1", "magnus_gripper:2", "magnus_vision:2") }),
        Up(ShopIds.MagnusRailMotor, "magnus", null, "Big Brain Robotics",
            new[] { 300f }, new[] { 3 },
            new[] { "Magnus Rail Motor (Now With Racing Stripes)" },
            new[] { "Racing stripes make it go faster. That's science." },
            new[] { "Adds speed: he commutes twice as fast (0.7 → 1.4 m/s) and sorts ×1.25 faster at the table. Plus racing stripes and a tiny spoiler." },
            1999f, 4.6f, 88, null,
            new[]
            {
                R(5, "magnus did a lap around the garage and said 'vroom' in binary", "Ivan"),
                R(4, "the spoiler does nothing. he says it does something. we don't argue", "Dot"),
            },
            new[] { P("magnus_brain:1") }),
        Up(ShopIds.MagnusGripper, "magnus", null, "Big Brain Robotics",
            new[] { 1200f, 16000f }, new[] { 4, 10 },
            new[] { "Heavy-Duty Lobster Pincer", "Quad-Claw (Like the Arcade, But It Actually Grabs)" },
            new[] { "Pinch. Pinch. PINCH.", "Finally, a claw machine that works." },
            new[] { "Adds muscle: lifts items up to 25 kg (tongs: 5 kg) and handles HEAVY bags: he yoinks them from the queue or from beside the table and SLAMS them open like any other bag.",
                    "Adds speed: sorts ×1.4 faster. Grabs like he means it." },
            3999f, 4.4f, 131, null,
            new[]
            {
                R(5, "lobster claw goes snip snip. so does magnus, out loud", "Pierre"),
                R(5, "he yoinked a 24 kg bag off the rack like it was a grape. i felt that", "Rhoda"),
                R(5, "finally won a stuffed animal. it was in the trash", "Jojo"),
            },
            new[] { P("magnus_brain:1"), P() }),
        Up(ShopIds.MagnusVision, "magnus", null, "Big Brain Robotics",
            new[] { 4000f, 18000f }, new[] { 5, 8 },
            new[] { "X-Ray Googly Eyes", "Appraisal Monocle HUD" },
            new[] { "He sees everything. He judges most of it.", "Classy. Smug. Correct." },
            new[] { "Adds treasure flags: 60% of treasure bags in the garage get a gold glint and a '!'. The Bag Belt takes flagged bags off the pile first, and Magnus rips flagged bags first.",
                    "Every treasure bag flagged, glinting in its rarity color. Your ebag sales earn +3%." },
            6999f, 4.9f, 54, null,
            new[]
            {
                R(5, "googly eyes but they see THROUGH BAGS. also through me", "Ursula"),
                R(5, "he wears the monocle to chess now. insufferable. still loses sometimes", "Kim"),
            },
            new[] { P("magnus_brain:1"), P("magnus_brain:2") }),

        // =============================================================== GEAR
        // Personal gear (work gloves, boots, headlamp, treasure wand, rabies vaccine, hard hat, grabber pole) is no longer
        // sold here: it is printed from kept parts in SolidWaste 3D's blueprint tree (Workshop/CallingCatalog.cs).
        // What stays in this department: consumables, gnomes, the trap and RideShop's grip gloves.

        // F3 (Docs/Multiplayer.md §14.3): the bag cannon (truck).
        Up(ShopIds.BagCannon, "truck", null, "Blammo Pneumatics",
            new[] { 1200f }, new[] { 2 },
            new[] { "Bag Cannon™ (Truck-Mounted, Mostly Safe)" },
            new[] { "Bolted to the truck's side. Fires your bag into the hopper on a big lazy arc. Usually." },
            new[] { "Hold R with a bag within ~14 m of your truck: aim arc to the hopper, let go to FWOOMP it in. 1 in 8 shots misfire (splat + bonk). Not heavy bags." },
            2499f, 4.1f, 803, "Recall notice: none (yet)",
            new[]
            {
                R(5, "lobbed a bag 12 metres into the hopper. the neighbours applauded. then the next one exploded", "Bart"),
                R(4, "misfired once and i got bonked by my own garbage. 10/10 would bonk again", "Fern"),
                R(2, "the instructions just say 'point away from face'", "Lloyd"),
            }),

        // =============================================================== BUSINESS + PERMITS
        Up(ShopIds.PermitMinivanMeadows, "permits", new[] { "business" }, "Minivan Meadows HOA",
            new[] { 600f }, new[] { 4 },
            new[] { "Minivan Meadows Collection Permit (Laminated, Slightly Sticky)" },
            new[] { "An HOA community. Pop. 64 minivans." },
            new[] { "Opens Minivan Meadows east of Mildly Pleasant Acres. $15 per bag, Rep ×1.5. Trust starts at 40%." },
            0f, 3.8f, 64, "New district",
            new[]
            {
                R(3, "the permit is sticky. we don't know why. the HOA won't say", "Karen (HOA President)"),
                R(5, "they pay $15 a bag. FIFTEEN. for TRASH", "Every Garbage Person"),
                R(4, "the minivans watch you. all 64 of them", "Brent"),
            }),
        Up(ShopIds.PermitCouponCommons, "permits", new[] { "business" }, "Coupon Commons Merchants' Association",
            new[] { 1800f }, new[] { 5 },
            new[] { "Coupon Commons Commercial Collection Permit" },
            new[] { "Loading alleys, flattened boxes and everyone's closing-time rubbish." },
            new[] { "After Meadows: cross the river to Coupon Commons. Cardboard-heavy commercial collections pay $25 per bag + $5 on-time tip, Rep ×1.75." },
            0f, 4.1f, 18, "New district",
            new[] { R(5, "the boxes were already flattened. employee of the month", "Pat (Loading Bay)") },
            new[] { P(ShopIds.PermitMinivanMeadows + ":1") }),
        Up(ShopIds.PermitSnootingtonHills, "permits", new[] { "business" }, "Snootington Estates Trust",
            new[] { 3500f }, new[] { 7 },
            new[] { "Snootington Hills Collection Permit (Embossed, Smells Like Old Money)" },
            new[] { "Hedges. Monocles. Very nice trash." },
            new[] { "After Coupon Commons: opens the scenic northern roads into Snootington Hills. $40 per bag + a $15 tip, Rep ×2. Trust starts at 40%." },
            0f, 4.2f, 14, "New district",
            new[]
            {
                R(5, "the permit is embossed. i keep stroking it", "Reginald III"),
                R(4, "found a polo cup in their trash. the horse wants it back", "Polo Horse"),
            }, new[] { P(ShopIds.PermitCouponCommons + ":1") }),
        Up(ShopIds.PermitStackingtonHeights, "permits", new[] { "business" }, "Stackington Heights Condo Board",
            new[] { 7000f }, new[] { 8 },
            new[] { "Stackington Heights High-Rise Collection Permit" },
            new[] { "More floors. More doorbells. One very busy loading bay." },
            new[] { "After Snootington Hills: opens the riverside condo district. Glass- and metal-heavy collections pay $55 per bag + $20 on-time tip, Rep ×2.4." },
            0f, 4.3f, 16, "New district",
            new[] { R(4, "please use the service entrance. all forty of us signed this review", "The Condo Board") },
            new[] { P(ShopIds.PermitSnootingtonHills + ":1") }),
        Up(ShopIds.PermitBillionaireBluffs, "permits", new[] { "business" }, "Bluffs Butler Bureau",
            new[] { 12000f }, new[] { 10 },
            new[] { "Billionaire Bluffs Collection Permit (Signed by a Butler)" },
            new[] { "The guard will say 'Name? ...Garbage? ...Fine.'" },
            new[] { "Raises the Bluffs boom barrier. $80 per bag + a $40 butler tip, Rep ×3. Butler Drops at 7 AM and 5 PM. Trust starts at 40%." },
            0f, 4.9f, 8, "New district",
            new[]
            {
                R(5, "a butler handed me a bag on a silver platter. inside: a yacht key", "Jeeves"),
                R(5, "they throw out more gold than i've ever seen", "Gold Toilet Seat (verified)"),
            }, new[] { P(ShopIds.PermitStackingtonHeights + ":1") }),
        Up(ShopIds.EbaePowerSeller, "business", null, "ebag Seller Services",
            new[] { 1200f, 9000f, 29000f }, new[] { 4, 7, 10 },
            new[] { "ebag Power Seller Badge", "ebag Bulk Lister Pro", "ebag Diamond Seller (Auto-Accept)" },
            new[] { "A shiny badge. Buyers trust shiny badges.", "List faster. Sell faster. Sleep less.", "It sells your stuff while you sleep. Mostly fairly." },
            new[] { "ebag's fee drops from 8% to 5%, and one more buyer shows up for every listing.",
                    "ebag's fee drops to 3%. Offers arrive twice as fast.",
                    "ebag's fee drops to 1%. Any offer at 97% of the appraisal or more is accepted automatically." },
            0f, 4.3f, 7777, null,
            new[]
            {
                R(5, "my badge is shinier than my items. sold everything", "xX_FlipKing_Xx"),
                R(4, "auto-accepted an offer while i was in the shower. $14k. i'm still in the shower", "Gordo"),
                R(3, "the badge is a sticker. a very persuasive sticker", "Mildred"),
            }),
        Up(ShopIds.GoldenTruck, "business", new[] { "truck" }, "Rubbish Co. Prestige Motors",
            new[] { 140000f }, new[] { 12 },
            new[] { "The Golden Garbage Truck (24K, Street Legal-ish)" },
            new[] { "Delivered by a swarm of Scrapazon drones. Mind the drones." },
            new[] { "Gold paint, a sparkle trail, a fanfare horn and curbs that cheer as you pass. Top speed 18 m/s." },
            999999f, 5f, 1, "The Capstone",
            new[]
            {
                R(5, "i drove it once and a raccoon bowed", "Supreme Garbage Overlord"),
                R(5, "the curbs cheer for me. i cheer back. we're friends now", "Anonymous Tycoon"),
                R(1, "it's still a garbage truck", "Snooty Neighbor"),
            },
            new[] { P("truck_turbo:3", "truck_arm:3", ShopIds.KeyToTheCity) }),

        // =============================================================== COSMETICS
        Cosmetic(ShopIds.TruckPaintPrimer, new[] { "truck" }, "Truck Paint: Primer Grey", "Drip Drop Paint Co.", 250f, 2,
            "Looks unfinished. Is unfinished.", "Matte grey primer for the truck. Very mysterious. Very cheap.", 499f, 3.9f, 610,
            new[] { R(4, "my truck looks like a rough draft. i love it", "Penny"), R(3, "the raccoons think it's a rock", "Lyle") }),
        Cosmetic(ShopIds.TruckPaintFlames, new[] { "truck" }, "Truck Paint: Hand-Painted Flames", "Drip Drop Paint Co.", 1500f, 5,
            "+0 speed. +100 cool.", "Hand-painted flames down both sides. Hot. Not literally (mostly).", 2999f, 4.8f, 2380,
            new[] { R(5, "flames on a garbage truck. i am a god now", "Dale"), R(5, "goes great with the turbo flames. which were included", "Legal Dept.") }),
        Cosmetic(ShopIds.TruckPaintRaccoonCamo, new[] { "truck" }, "Truck Paint: Raccoon Camo", "Drip Drop Paint Co.", 4000f, 7,
            "Raccoons can't see you. You can't see you.", "Grey-and-black bandit camo. Blends right into the night shift.", 7999f, 4.4f, 512,
            new[] { R(5, "lost my truck in the parking lot for 3 days", "Mona"), R(4, "a raccoon tried to adopt it", "Rocco") }),
        Cosmetic(ShopIds.TruckPaintChrome, new[] { "truck" }, "Truck Paint: Mirror Chrome", "Drip Drop Paint Co. Luxe", 15000f, 9,
            "Blinds the neighbors at noon.", "Full mirror chrome. You will see yourself in your garbage truck. Daily.", 29999f, 4.9f, 99,
            new[] { R(5, "i can do my hair in the hopper door", "Lorenzo"), R(5, "neighbors wear sunglasses when i drive by. respect", "Chrome Dome") }),
        Cosmetic(ShopIds.HornPackMusical, new[] { "truck" }, "Musical Horn Pack (La Cucaracha, Dixie, A Goose)", "HONKCO", 600f, 4,
            "Honk, but make it music.", "Three new horn tunes for the truck. One of them is a goose.", 1199f, 4.7f, 3301,
            new[] { R(5, "played la cucaracha at a funeral. by accident. mostly", "Ernesto"), R(5, "the goose one makes real geese show up", "Goose") }),
        Cosmetic(ShopIds.TrashFmRadio, new[] { "truck" }, "Trash FM In-Cab Radio", "Trash FM 101.3", 300f, 3,
            "All garbage. All day.", "An in-cab radio tuned to Trash FM: smooth jams and traffic reports about raccoons.", 599f, 4.2f, 870,
            new[] { R(4, "the DJ is a raccoon. great taste in music", "Lola"), R(5, "traffic report: 'a bag fell off a truck on Maple. it was you'", "Ned") }),
        Cosmetic(ShopIds.GarageDiscoBall, new[] { "garage", "magnus" }, "Garage Disco Ball (Magnus Dances)", "Funky Junk Lighting", 1200f, 5,
            "Saturday Night Sorting Fever.", "A disco ball for the garage. Magnus dances when he wins at chess.", 2499f, 4.9f, 1979,
            new[] { R(5, "magnus did the robot. he IS the robot", "Kim"), R(5, "sorting trash is now a party. still trash", "DJ Bin") }),

        // =============================================================== COMING SOON
        new ScrapazonItem
        {
            id = ShopIds.FranchiseMode, kind = ShopItemKind.Teaser, dept = "soon", tags = new[] { "soon" },
            title = "Franchise Mode (Sell Rubbish Co. to GoldStar Premium Waste)",
            brand = "GoldStar Premium Waste (Our Rival)",
            listPrice = 99999999f, rating = 2.1f, ratingCount = 4,
            teaserLine = "coming soon™ (once you own the whole city)",
            blurb = "Sell the company, start over at Rank 1, keep one perk. GoldStar is very excited. Too excited.",
            reviews = new[]
            {
                R(1, "goldstar called me 'champ'. i don't trust goldstar", "Walt"),
                R(5, "new game plus but for garbage. the dream", "Zen"),
            }
        },
    };
}

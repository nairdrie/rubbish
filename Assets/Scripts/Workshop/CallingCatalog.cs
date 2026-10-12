using System;
using System.Collections.Generic;

// Callings (the player sees BLUEPRINTS): the identity tree for upgrades to YOURSELF, 3D-printed in SolidWaste 3D on the
// shop laptop from SPECIFIC kept items (Docs/Progression.md "Blueprints"). Pure data + rules: no UnityEngine here (Tools/workshop_state_probe.cs compiles this
// file standalone next to WorkshopState.cs, WorkshopCatalog.cs and CallingRecipes.cs, where every level's recipe lives).

public enum Calling : byte { Basics, Hauler, Scavenger, Raccoon }

/// <summary>
/// Upgrade ids the Callings tree drives. A node's level is mirrored into <c>GameSession.GetUpgradeLevel(id)</c> for the
/// business that built it, so effects read a calling exactly like any other gear. The first block is the old Scrapazon
/// gear (same strings as <c>ShopIds</c>; pinned by CallingsTests); the rest are new and prefixed <c>calling_</c>.
/// </summary>
public static class CallingIds
{
    // ---- existing gear ids (must equal ShopIds.*)
    public const string WorkGloves = "work_gloves";
    public const string WorkBoots = "work_boots";
    public const string HardHat = "hard_hat";
    public const string GrabberPole = "grabber_pole";
    public const string TreasureWand = "treasure_wand";
    public const string Headlamp = "headlamp";
    public const string RabiesVaccine = "rabies_vaccine";
    // ---- Hauler (stage 2)
    public const string RocketBlades = "calling_rocket_blades";
    public const string CompactorBackpack = "calling_compactor_backpack";
    public const string OneManRoute = "calling_one_man_route";
    // ---- Scavenger (stage 3)
    public const string XRaySpecs = "calling_xray_specs";
    public const string NoseForMoney = "calling_nose_for_money";
    public const string TheItch = "calling_the_itch";
    public const string Lockpicks = "calling_lockpicks";
    public const string SoftSoles = "calling_soft_soles";
    public const string SwagSack = "calling_swag_sack";
    public const string TheFence = "calling_the_fence";
    public const string CallIt = "calling_call_it";
    // ---- Raccoon (stage 4)
    public const string EmbraceIt = "calling_embrace_it";
    public const string CritterHutch = "calling_critter_hutch";
    public const string BreedingBox = "calling_breeding_box";
    public const string CollarChip = "calling_collar_chip";
    public const string BrainChip = "calling_brain_chip";
    public const string PackLeader = "calling_pack_leader";
    public const string KingsRival = "calling_kings_rival";
}

/// <summary>One level of a node: what it is called, what it does, how to use it once printed, and the parts it takes.</summary>
public sealed class CallingLevel
{
    public readonly string title, effect, howTo;
    /// <summary>The kept items this level consumes (<see cref="CallingRecipes"/>). Null = no recipe authored: not buildable.</summary>
    public PartRecipe recipe { get; internal set; }
    internal CallingLevel(string title, string effect, string howTo) { this.title = title; this.effect = effect; this.howTo = howTo; }
}

public sealed class CallingNode
{
    /// <summary>Saved position in <c>WorkshopState.callings</c>. Append new nodes; never reorder or reuse an index.</summary>
    public readonly int index;
    public readonly string id, name, description;
    public readonly Calling calling;
    /// <summary>Tree cell inside its calling (column, row). Prerequisites always sit in an earlier row.</summary>
    public readonly int column, row;
    /// <summary>"nodeId:level" (a bare id means level 1). All must be met to build ANY level of this node.</summary>
    public readonly IReadOnlyList<string> prereqs;
    /// <summary>The <c>GameSession</c> upgrade id this node's level is mirrored into (today always the node's own id).</summary>
    public readonly string upgradeId;
    /// <summary>How many levels can be built today. 0 = shown as "coming soon"; fewer than <see cref="maxLevel"/> = the
    /// later levels are coming soon. A later stage raises this when the gameplay for those levels ships.</summary>
    public readonly int implementedLevels;
    readonly CallingLevel[] levels;
    public int maxLevel => levels.Length;
    public bool implemented => implementedLevels > 0;
    public bool isCapstone { get; }
    /// <summary>1-based; null outside 1..maxLevel.</summary>
    public CallingLevel Level(int level) => level >= 1 && level <= levels.Length ? levels[level - 1] : null;
    /// <summary>The recipe of 1-based <paramref name="level"/>, or null (out of range, or none authored).</summary>
    public PartRecipe RecipeOf(int level) => Level(level)?.recipe;

    internal CallingNode(int index, Calling calling, int column, int row, string id, string name, string description,
        int implementedLevels, string[] prereqs, bool capstone, CallingLevel[] levels)
    {
        this.index = index; this.calling = calling; this.column = column; this.row = row; this.id = id; this.upgradeId = id;
        this.name = name; this.description = description; this.levels = levels; isCapstone = capstone;
        this.implementedLevels = Math.Max(0, Math.Min(implementedLevels, levels.Length));
        this.prereqs = Array.AsReadOnly(prereqs ?? Array.Empty<string>());
        for (int i = 0; i < levels.Length; i++) levels[i].recipe = CallingRecipes.For(id, i + 1);
    }
}

public sealed class CallingInfo
{
    public readonly Calling calling;
    public readonly string name, tagline;
    /// <summary>Tree width in cells.</summary>
    public readonly int columns;
    internal CallingInfo(Calling calling, string name, string tagline, int columns)
    { this.calling = calling; this.name = name; this.tagline = tagline; this.columns = columns; }
}

public static class CallingCatalog
{
    static readonly CallingInfo[] infos = {
        new CallingInfo(Calling.Basics, "Basics", "Gloves first, then pick who you are.", 3),
        new CallingInfo(Calling.Hauler, "Hauler", "You are the truck.", 2),
        new CallingInfo(Calling.Scavenger, "Scavenger", "One person's trash is your whole personality.", 3),
        new CallingInfo(Calling.Raccoon, "Raccoon", "Bitten one time too many.", 3),
    };
    public static CallingInfo Info(Calling calling) => infos[(int)calling];
    public static IReadOnlyList<CallingInfo> Callings { get; } = Array.AsReadOnly(infos);

    // What each level costs is not here: CallingRecipes.cs is the one place to retune recipes.
    /// <summary>Every calling's first nodes hang off this trunk node (the cheapest thing on the tree).</summary>
    public const string TrunkId = CallingIds.WorkGloves;
    const string Trunk = TrunkId + ":1";

    static CallingLevel L(string title, string effect, string howTo = null) => new CallingLevel(title, effect, howTo);
    static string[] Needs(params string[] ids) => ids;
    static CallingNode N(int index, Calling calling, int column, int row, string id, string name, string description,
        int implementedLevels, string[] prereqs, params CallingLevel[] levels) =>
        new CallingNode(index, calling, column, row, id, name, description, implementedLevels, prereqs, false, levels);
    static CallingNode Capstone(int index, Calling calling, int column, int row, string id, string name, string description,
        int implementedLevels, string[] prereqs, params CallingLevel[] levels) =>
        new CallingNode(index, calling, column, row, id, name, description, implementedLevels, prereqs, true, levels);

    // Indices are saved positions. Append new nodes; never reorder or reuse one.
    // Layout rule (CallingsTests): a prerequisite is in an earlier row of the same calling (or is a Basics node), and the
    // cells straight below it are empty down to the row above the child, so the connector never runs through a node.
    static readonly CallingNode[] nodes = {
        // =============================================================== BASICS (the shared trunk)
        N(0, Calling.Basics, 0, 0, CallingIds.WorkGloves, "Work Gloves", "Grip strength of a raccoon. Smell of a raccoon.", 3, null,
            L("Grippy Work Gloves", "Carrying slows you half as much. Throws reach 15.5 m/s.",
                "Grippy Work Gloves on! Heavy bags slow you down half as much. Throws go further."),
            L("Double-Fisted Gloves", "Back Sack: press E on a second bag to stash the first on your back. One E at the hopper tosses both.",
                "Double-Fisted Gloves on! Carrying a bag, press E on another: the first goes on your back."),
            L("Gorilla Grip Gauntlets", "No carry slowdown at all. The Back Sack holds 2 (3 bags at once).",
                "Gorilla Grip Gauntlets on! No carry slowdown. Two bags on your back.")),
        N(1, Calling.Basics, 1, 0, CallingIds.WorkBoots, "Work Boots", "Squeak squeak. Faster.", 2, null,
            L("Squeaky Sneakers of Speed", "Walk and sprint 10% faster.",
                "Squeaky Sneakers on! +10% walk and sprint. Squeak squeak."),
            L("Spring-Loaded Stompers", "Walk and sprint 20% faster. Ctrl stomps scare critters within 4 m (6 s cooldown).",
                "Spring-Loaded Stompers on! +20% speed. Ctrl to STOMP critters away.")),
        N(2, Calling.Basics, 2, 0, CallingIds.HardHat, "Hard Hat", "Rated for 1,000 bonks. Tested on the intern.", 1, null,
            L("Hard Hat (Bonk-Rated)", "Critter bites and bonks knock you back half as far, and you keep your bag 50% of the time.",
                "Hard Hat on! Critter bonks knock you half as far, and you often keep hold of your bag.")),

        // =============================================================== HAULER: you are the truck
        // Numbers: HaulerTuning (Assets/Scripts/Callings/Hauler). Keep these lines in step with it.
        N(3, Calling.Hauler, 0, 0, CallingIds.GrabberPole, "Grabby Hand", "Why walk to the bag when the bag can come to you?", 3, Needs(Trunk),
            L("Grabby Hand (Extendo-Claw)", "Press V: grab a bag up to 4.5 m away (over fences too), poke a friend or snatch the bag they're holding.",
                "Grabby Hand printed! Press V: grab far bags (over fences), poke friends, snatch their bags."),
            L("Long Arm of the Claw", "Reach 6.5 m, and a hard YANK that reels the bag straight into your hands.",
                "Long Arm printed! V reaches 6.5 m now, and the bag comes in with a YANK."),
            L("Two-Fisted Claw", "Reach 8.5 m, and one squeeze grabs TWO bags: the second goes into your backpack (or lands at your feet).",
                "Two-Fisted Claw printed! V reaches 8.5 m and grabs two bags: one in hand, one on your back.")),
        N(4, Calling.Hauler, 1, 0, CallingIds.RocketBlades, "Rocket Blades", "Roller blades. Then rockets on the roller blades.", 3, Needs(Trunk),
            L("Curb Blades", "Sprinting is skating: 22% faster, with real momentum. Glide when you let go, carve wide when you're quick. Not indoors.",
                "Curb Blades on! Hold sprint to skate. Let go of the stick to glide, let go of sprint to toe-stop."),
            L("Rocket Blades", "Q: a 1.6 s rocket boost that outruns yard dogs (6 s cooldown). Hit a wall at speed and you wipe out, bags and all.",
                "Rockets fitted! Press Q to BOOST. Mind the corners: a wall at full speed is a wipe-out."),
            L("Afterburner & Grind Rails", "Hold Q to keep boosting while the heat meter fills (full = flame-out). Glancing walls, kerbs and fences are grinds: no speed lost.",
                "Afterburners on! HOLD Q to keep boosting, and watch the heat. Scrape along a wall or kerb to grind.")),
        N(5, Calling.Hauler, 0, 1, CallingIds.CompactorBackpack, "Compactor Backpack", "The hopper, but it's you.", 3, Needs(CallingIds.GrabberPole + ":1"),
            L("Big Pack", "Carry 2 bags on your back (plus any glove slots). E at a truck unloads the lot. A full pack slows you a little.",
                "Big Pack on! Holding a bag, press E on another (or tap C) to put it on your back. E at a truck unloads it all."),
            L("Crusher Pack", "4 slots, and hold C to CRUSH: a crushed bag takes half a slot and half the weight (up to 7 bags). Nothing inside is harmed.",
                "Crusher Pack on! HOLD C to crush the bags on your back: twice as many fit. The contents are safe."),
            L("Bale Ejector", "6 slots (up to 11 bags), unloading is one fast bale, and B ejects the whole load at your feet.",
                "Bale Ejector on! Unloading is one fast bale now, and B drops the whole load where you stand.")),
        Capstone(6, Calling.Hauler, 1, 2, CallingIds.OneManRoute, "One-Man Route", "No truck. No problem. Mostly.", 1,
            Needs(CallingIds.RocketBlades + ":2", CallingIds.CompactorBackpack + ":2"),
            L("One-Man Route", "A walk-up hatch on your depot: bags you carry there on foot pay like a truck, +25%, and slide straight onto your tipping floor.",
                "One-Man Route! A walk-up hatch is on your depot, left of the door. Bags you walk in pay +25%.")),

        // =============================================================== SCAVENGER
        N(7, Calling.Scavenger, 0, 0, CallingIds.Headlamp, "Night Headlamp", "Moths included. Moths not optional.", 2, Needs(Trunk),
            L("Night Shift Headlamp", "A light on your head that clicks on by itself when it gets dark.",
                "Night Shift Headlamp printed! It clicks on by itself at night."),
            L("UV Treasure Headlamp", "At night, 35% of curb bags that hold a valuable glow faintly purple within 15 m.",
                "UV Treasure Headlamp printed! At night, some treasure bags glow purple.")),
        N(8, Calling.Scavenger, 2, 0, CallingIds.TreasureWand, "Treasure Wand", "Beep = bling. No beep = probably still trash.", 2, Needs(Trunk),
            L("Metal Detector Wand", "Picking up a bag beeps if it holds a valuable: 45% detection, never a false alarm.",
                "Metal Detector Wand printed! It beeps when you grab a bag with treasure in it. Sometimes."),
            L("Quantum Bling Wand", "80% detection, and it beeps when you walk within 2 m of such a curb bag.",
                "Quantum Bling Wand printed! Beeps at bling when you grab a bag or walk past one.")),
        N(9, Calling.Scavenger, 0, 1, CallingIds.NoseForMoney, "Nose for Money", "You can smell a rich house from the road.", 2, Needs(CallingIds.Headlamp + ":1"),
            L("Nose for Money", "A gold coin glints over the roof of every well-off house within 28 m.",
                "Nose for Money printed! Rich houses give themselves away: watch the rooftops for a glint of gold."),
            L("Bloodhound for Bling", "Smell them from 55 m, see HOW rich (one to three coins), and rich houses show as gold diamonds on your map.",
                "Bloodhound for Bling printed! More coins, more money. Rich houses are gold diamonds on the map.")),
        N(10, Calling.Scavenger, 2, 1, CallingIds.XRaySpecs, "X-Ray Specs", "See what's in the bag before you commit to the bag.", 2, Needs(CallingIds.TreasureWand + ":1"),
            L("X-Ray Specs", "Look at a bag, or hold one: a plate shows how much of each kind of trash is inside and whether anything in there is valuable.",
                "X-Ray Specs printed! Look at a bag or pick one up: the plate shows what's inside."),
            L("Hi-Def X-Ray Specs", "The plate also names the best thing in the bag and how rare it is. Never the price.",
                "Hi-Def X-Ray Specs printed! The plate now names the best thing in the bag.")),
        N(11, Calling.Scavenger, 0, 2, CallingIds.TheItch, "The Itch", "You only wanted a look inside. Honest.", 1, Needs(CallingIds.NoseForMoney + ":1"),
            L("The Itch", "Honest kerb bags turn grey and dull to you. Front doors start to glow. At a house door: E to case the joint, E again to break in. Houses only.",
                "You have The Itch. Bags look dull. Doors glow. Walk up to a front door and press E.")),
        Capstone(12, Calling.Scavenger, 2, 2, CallingIds.CallIt, "Call It", "Name it before you open it.", 1,
            Needs(CallingIds.XRaySpecs + ":2", CallingIds.NoseForMoney + ":2"),
            L("Call It", "Once a day, holding an unopened bag, press N and call what its best find is worth. Call it right for a bonus of up to $400.",
                "Call It printed! Hold a bag and press N to name what it's worth. One call a day.")),
        N(13, Calling.Scavenger, 0, 3, CallingIds.Lockpicks, "Lockpicks", "A hobby. For locks you own. Probably.", 2, Needs(CallingIds.TheItch + ":1"),
            L("Bent Hairpin", "A break-in takes 6.5 s instead of 9, with 3 pins instead of 4, and the lock gives 78% of the time (55% bare-handed).",
                "Bent Hairpin printed! Locks are quicker and give more often."),
            L("Proper Picks", "4.5 s on the doorstep, a wider sweet spot, and the lock gives 94% of the time.",
                "Proper Picks printed! In and out in under five seconds.")),
        N(14, Calling.Scavenger, 1, 3, CallingIds.SoftSoles, "Soft Soles", "Tiptoes, professionally.", 2, Needs(CallingIds.TheItch + ":1"),
            L("Soft Soles", "At a door, neighbours, dogs and rivals notice you from 30% less far, and people indoors hear 25% less.",
                "Soft Soles printed! Witnesses have to be closer to notice you at a door."),
            L("Cat Feet", "Noticed from 55% less far, and people indoors hear half as much.",
                "Cat Feet printed! You could rob a library.")),
        N(15, Calling.Scavenger, 2, 3, CallingIds.SwagSack, "Swag Sack", "It's a bag. For things. Don't ask.", 2, Needs(CallingIds.TheItch + ":1"),
            L("Swag Sack", "Hold 4 hot goods at once instead of 2.",
                "Swag Sack printed! You can carry 4 hot goods."),
            L("Deep Swag Sack", "Hold 8 hot goods at once.",
                "Deep Swag Sack printed! You can carry 8 hot goods.")),
        N(16, Calling.Scavenger, 1, 4, CallingIds.TheFence, "The Fence", "He knows a guy. He is the guy.", 1, Needs(CallingIds.SwagSack + ":1"),
            L("The Fence", "A raccoon in a trench coat waits in the Depot Row alley after dark and buys hot goods for 35% of their worth. He answers to the Raccoon King.",
                "The Fence knows you now. Find him in the alley on Depot Row after dark.")),

        // =============================================================== RACCOON: bitten one time too many
        N(17, Calling.Raccoon, 0, 0, CallingIds.RabiesVaccine, "Thick Skin", "Get ahead of the foam.", 2, Needs(Trunk),
            L("Pre-Exposure Rabies Vaccine", "A rabid bite infects you 25% of the time instead of 50%.",
                "Vaccine administered! Rabid bites only infect you half as often."),
            L("Rabies Booster (Immune, Smug)", "Immune to rabies. Bites still knock you over (sorry).",
                "Booster administered! You're immune to rabies. Smug.")),
        // Stage 4 (Callings/Raccoon/*; numbers in RaccoonTuning.cs).
        N(18, Calling.Raccoon, 1, 0, CallingIds.CritterHutch, "Critter Hutch", "Somewhere for your new friends to plot.", 2, Needs(Trunk),
            L("Critter Hutch", "A hutch at your depot with 3 bunks. F on a sprung trap sends the raccoon there. Kibble: $15 a head a day.",
                "Critter Hutch printed, and standing outside your depot! Trap a raccoon at night, then press F on the trap to send it home. 3 bunks."),
            L("Critter Condo", "A second storey: 6 bunks, and bigger crew sweeps (up to 10 bags, 20 with a Brain Chip).",
                "Critter Condo printed! Six bunks, bigger sweeps. The neighbours have questions.")),
        N(19, Calling.Raccoon, 2, 0, CallingIds.EmbraceIt, "Embrace It", "Or don't cure it. Lean in.", 3, Needs(Trunk),
            L("A Little Bit Feral", "Rabies never makes you pass out or pay the hospital. A stored shot waits until you choose (K twice). The symptoms shrink to a hint.",
                "A Little Bit Feral! Rabies can't floor you any more, and a stored shot waits for K, K. Go get bitten."),
            L("Night Creature", "While rabid: 15% faster on foot at night, night eyes, and wild raccoons leave you alone.",
                "Night Creature! While rabid: faster after dark, you see in it, and raccoons know their own."),
            L("Fully Feral", "While rabid: Y pounces 7 m (9 s cooldown). Raccoons where you land are friends for 20 s and drop their loot.",
                "Fully Feral! While rabid, press Y to POUNCE. Raccoons where you land befriend you and drop what they stole.")),
        N(20, Calling.Raccoon, 0, 1, CallingIds.CollarChip, "Collar Chip", "A tiny collar. A tiny job.", 1, Needs(CallingIds.CritterHutch + ":1"),
            L("Collar Chip", "Crew raccoons take the smelliest kerb bags at 22:00 and 02:00 (never the Bluffs): up to 6 a sweep, for 35% of the fee plus night pay. They eat some and nap.",
                "Collar Chip printed! At your hutch, put raccoons on the crew. They sweep the kerbs at 22:00 and 02:00. Mostly.")),
        N(21, Calling.Raccoon, 1, 1, CallingIds.BreedingBox, "Breeding Box", "Nature finds a way. In a box.", 1, Needs(CallingIds.CritterHutch + ":1"),
            L("Breeding Box", "Pair two rested adults at the hutch: kits in 2 days, grown up 2 days later, with their parents' traits and a small mutation.",
                "Breeding Box printed! At your hutch, press Pair on two rested adults. Kits arrive in 2 days.")),
        N(22, Calling.Raccoon, 0, 2, CallingIds.BrainChip, "Brain Chip", "Smarter than the average bin. Low bar.", 2, Needs(CallingIds.CollarChip + ":1"),
            L("Brain Chip", "No naps, no wandering. A noon sweep, a cart (2-6 bags each), cheapest streets first: up to 12 bags a sweep (20 with the Condo). Crew kibble: $24 a head.",
                "Brain Chip printed! Your crew is reliable now, does a noon run and works the cheap streets first. Smarter than the average bin."),
            L("Full-Time Chip", "Full-time: a 07:00 run too, up to 16 bags a sweep (26 with the Condo). A bred crew of six covers your cheap districts. Crew kibble: $210 a head.",
                "Full-Time Chip printed! Four sweeps a day and bigger loads. The crew now eats premium kibble: $210 a head a day. Rest the slackers.")),
        N(23, Calling.Raccoon, 2, 2, CallingIds.PackLeader, "Pack Leader", "They follow you now. All of them.", 2,
            Needs(CallingIds.EmbraceIt + ":2", CallingIds.BreedingBox + ":1"),
            L("Pack Leader", "Take one hutch raccoon along: it follows you, rides in your hopper, and fetches a nearby kerb bag to your truck every 30 s.",
                "Pack Leader! At your hutch, press Take along. Your raccoon follows you and fetches kerb bags to your truck."),
            L("Alpha", "A pack of three. Wild raccoons never bite you, and back off when your pack walks up.",
                "Alpha! Take three along. Wild raccoons won't touch you and scatter before your pack.")),
        Capstone(24, Calling.Raccoon, 1, 3, CallingIds.KingsRival, "King's Rival", "There can be only one Raccoon King.", 1,
            Needs(CallingIds.BrainChip + ":1", CallingIds.PackLeader + ":2"),
            L("King's Rival", "The Raccoon King comes for YOU first. Dethrone him: +$250 and a King's Collar. Let him walk: $60 tribute.",
                "King's Rival! When the Raccoon King raids, he hunts you. Beat him for $250 and a King's Collar. Or pay tribute.")),
    };

    public static IReadOnlyList<CallingNode> All { get; } = Array.AsReadOnly(nodes);
    public static int Count => nodes.Length;
    public static CallingNode Get(string id)
    { if (!string.IsNullOrEmpty(id)) foreach (var node in nodes) if (node.id == id) return node; return null; }
    public static CallingNode At(int index) => index >= 0 && index < nodes.Length && nodes[index].index == index ? nodes[index] : null;

    /// <summary>"work_gloves:2" -> (node, 2); a bare id means level 1. False for an unknown node or a bad level.</summary>
    public static bool TryParsePrereq(string prereq, out CallingNode node, out int level)
    {
        node = null; level = 1;
        if (string.IsNullOrEmpty(prereq)) return false;
        int colon = prereq.IndexOf(':');
        string id = colon < 0 ? prereq : prereq.Substring(0, colon);
        if (colon >= 0 && !int.TryParse(prereq.Substring(colon + 1), out level)) return false;
        node = Get(id.Trim());
        return node != null && level >= 1 && level <= node.maxLevel;
    }

}

public static partial class ItemCatalog
{
    /// <summary>Relative pick weight of an item inside its category (0 = never rolled into a bag: prizes and night-shift keeps).</summary>
    public static float WeightOf(ItemDef def)
    {
        Ensure();
        return def != null && meta.TryGetValue(def.id, out var m) ? m.weight : 0f;
    }

    /// <summary>
    /// Findable PARTS for the Callings recipes (Docs/Progression.md "Callings"): ordinary non-valuables you sort or KEEP.
    /// Appended after every other def (DailyStats saves a best find as an index into <see cref="All"/>); built last from
    /// <c>Ensure</c>. Each has its own model in ItemVisuals.Parts.cs ("part_*" keys).
    /// TUNING: weights are low on purpose. A part turns up roughly 0.8 to 2 times in a fully collected day of its district
    /// (35 bags in the Acres, 30 in the Meadows; Tools/collectible_loot_probe.cs prints the measured numbers), and the
    /// things already in each category get about a tenth rarer (a fifth for metal). Category odds do not move at all:
    /// a bag picks the category first. None is tagged "bulk", so none is ever a make-weight filler.
    /// </summary>
    static void BuildPartDefs()
    {
        const ItemCategory J = ItemCategory.Junk, PL = ItemCategory.Plastic, GL = ItemCategory.Glass,
            ME = ItemCategory.Metal, HZ = ItemCategory.Hazard;

        // ---------------- tier 1: Mildly Pleasant Acres (and every district after it) ----------------
        Add("old_work_boot", "Old Work Boot", J, 0.6f, 1.0f, "part_boot", 0x7A5230, 0.8f, "garage,old", "Steel toe. Zero sole. One previous owner, very tired.");
        Add("leather_belt", "Cracked Leather Belt", J, 0.15f, 0.3f, "part_belt", 0x5A3A22, 0.6f, "old", "Three extra holes, punched with a fork. Thanksgiving.");
        Add("garden_hose", "Kinked Garden Hose", J, 1.2f, 2.5f, "part_hose", 0x3FA34D, 0.45f, "garage", "Kinked in eleven places. Sprays sideways only.");
        Add("bike_inner_tube", "Bike Inner Tube", J, 0.15f, 0.25f, "part_tube", 0x2C2C30, 0.6f, "garage", "Patched six times. The seventh was the last straw.");
        Add("skate_wheel", "Roller Skate Wheel", PL, 0.08f, 0.14f, "part_skatewheel", 0xF24A9A, 0.95f, "old", "Still spins. Squeaks like it's telling on you.");
        Add("broom_handle", "Broom Handle (No Broom)", J, 0.4f, 0.7f, "part_broom", 0xC4935A, 0.6f, "old", "The broom part left. The handle stayed for the kids.");
        Add("extension_cord", "Frayed Extension Cord", J, 0.4f, 0.9f, "part_cord", 0xF08A1E, 0.5f, "garage", "Fifteen metres of orange spaghetti. Tingles a bit.");
        Add("circuit_board", "Green Circuit Board", HZ, 0.08f, 0.2f, "part_circuit", 0x2E8A4A, 1.0f, "old", "From a machine that did one thing. Nobody remembers what.");
        Add("tv_remote", "TV Remote (Whose?)", PL, 0.1f, 0.18f, "part_remote", 0x2A2A30, 0.5f, "old", "Controls a TV three houses down. Probably.");
        Add("reading_glasses", "Reading Glasses", GL, 0.03f, 0.06f, "part_glasses", 0x8A5A30, 0.6f, "old", "Found on someone's head, eventually. Then lost again.");
        Add("magnifying_glass", "Magnifying Glass", GL, 0.12f, 0.22f, "part_magnifier", 0x9CD2FF, 0.5f, "old", "Makes small problems look like big problems.");
        Add("old_backpack", "Old School Backpack", J, 0.5f, 0.9f, "part_backpack", 0xC8452E, 0.5f, "old", "Contains one fossilised sandwich and a permission slip from 2009.");
        Add("bungee_cord", "Bungee Cord", J, 0.1f, 0.2f, "part_bungee", 0xE8C21E, 0.6f, "garage", "Holds anything to anything. Has opinions about your eye.");
        Add("jar_of_screws", "Jar of Mystery Screws", ME, 0.5f, 0.9f, "part_screwjar", 0xB8BCC4, 0.65f, "garage", "Every size except the one you need. Dad's life's work.");
        Add("hairpins", "Card of Hairpins", ME, 0.01f, 0.03f, "part_hairpins", 0x3A3A40, 1.0f, "", "Bent already. Somebody was busy.");
        Add("door_hinge", "Squeaky Door Hinge", ME, 0.15f, 0.3f, "part_hinge", 0xC8A040, 0.7f, "garage,old", "Announced every midnight snack for twenty years.");
        Add("desk_fan", "Wobbly Desk Fan", ME, 1.2f, 2.0f, "part_fan", 0x7FB8C8, 0.65f, "old", "Two speeds: off and helicopter.");
        Add("bike_helmet", "Kid's Bike Helmet", PL, 0.3f, 0.45f, "part_helmet", 0x2F8AE0, 0.45f, "garage", "Covered in dinosaur stickers. Structurally: also dinosaur stickers.");
        Add("first_aid_tin", "First Aid Tin", ME, 0.25f, 0.45f, "part_firstaid", 0xF4F2EC, 0.65f, "", "One plaster left. It has a cartoon on it.");
        Add("dog_collar", "Chewed Dog Collar", J, 0.08f, 0.15f, "part_collar", 0xC8262E, 0.55f, "", "The tag says BISCUIT. Biscuit chewed through it.");
        Add("pet_carrier", "Plastic Pet Carrier", PL, 1.5f, 2.5f, "part_carrier", 0xBFC9D6, 0.5f, "", "Something got out. From the inside.");
        Add("chicken_wire", "Roll of Chicken Wire", ME, 0.6f, 1.2f, "part_chickenwire", 0xA8ACB4, 0.65f, "garage", "No chickens. Several scratches. Worth it.");
        Add("kitchen_tongs", "Kitchen Tongs", ME, 0.15f, 0.25f, "part_tongs", 0xC8CCD2, 0.65f, "kitchen", "Click click. You have to click them twice. It's the law.");
        Add("oven_mitt", "Singed Oven Mitt", J, 0.08f, 0.15f, "part_mitt", 0xE86A4A, 0.5f, "kitchen", "Shaped like a lobster. Smells like a lasagne.");
        Add("bucket", "Cracked Bucket", PL, 0.4f, 0.7f, "part_bucket", 0x2FA8D8, 0.5f, "garage", "Holds water for almost four seconds.");

        // ---------------- tier 2: Minivan Meadows (and richer) ----------------
        Rich(2, "stroller_wheel", "Stroller Wheel", PL, 0.3f, 0.5f, "part_strollerwheel", 0x4A4A52, 0.5f, "baby", "All-terrain. The terrain was a shopping centre.");
        Rich(2, "kids_scooter", "Kid's Kick Scooter", ME, 2.5f, 3.5f, "part_scooter", 0x3FD0C8, 0.4f, "", "Outgrown in a week. Shin-height. Shin-seeking.");
        Rich(2, "yoga_mat", "Rolled-Up Yoga Mat", J, 0.9f, 1.4f, "part_yogamat", 0x9A6AD8, 0.45f, "", "Used once, as a slide. Namaste in the bin.");
        Rich(2, "baby_monitor", "Baby Monitor", HZ, 0.2f, 0.35f, "part_babymonitor", 0xF2F2F4, 0.6f, "baby", "Still picking up something. The baby is nineteen.");
        Rich(2, "fitness_tracker", "Fitness Tracker (Guilt Band)", HZ, 0.03f, 0.05f, "part_tracker", 0x2A2A32, 0.6f, "", "Last entry: 212 steps. Then a long, honest silence.");
    }
}

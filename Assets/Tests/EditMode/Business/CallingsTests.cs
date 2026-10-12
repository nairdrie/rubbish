using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The Callings tree, which the player sees as BLUEPRINTS printed in SolidWaste 3D (Workshop/CallingCatalog.cs +
/// CallingRecipes.cs + WorkshopState callings): catalog and recipe integrity, build rules (a level takes exactly its
/// recipe of kept items, and only once the business has its disk), the replicated JSON budget, legacy-gear seeding and
/// the raise-only bridge into upgrade levels. Where a blueprint sits on the honeycomb is BlueprintTreeTests; the disk
/// itself is DiskTests. Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like WorkshopStateTests.
/// </summary>
public class CallingsTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Val(object value, string member)
    {
        var type = value as Type ?? value.GetType(); object target = value is Type ? null : value;
        var field = type.GetField(member, Flags); if (field != null) return field.GetValue(target);
        var property = type.GetProperty(member, Flags); Assert.NotNull(property, type.Name + "." + member);
        return property.GetValue(target);
    }
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);

    static object[] Nodes => ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>().ToArray();
    static object Node(string id) => Static("CallingCatalog", "Get", id);
    static string Id(object node) => (string)Val(node, "id");
    static int Index(object node) => (int)Val(node, "index");
    static int Max(object node) => (int)Val(node, "maxLevel");
    static int Buildable(object node) => (int)Val(node, "implementedLevels");
    static string Branch(object node) => Val(node, "calling").ToString();
    static int Col(object node) => (int)Val(node, "column");
    static int Row(object node) => (int)Val(node, "row");
    static string[] Prereqs(object node) => ((IEnumerable)Val(node, "prereqs")).Cast<string>().ToArray();
    static (object node, int level) Prereq(string text)
    {
        var args = new object[] { text, null, 0 };
        Assert.IsTrue((bool)T("CallingCatalog").GetMethod("TryParsePrereq", Flags).Invoke(null, args), "prerequisite '" + text + "' must resolve");
        return (args[1], (int)args[2]);
    }

    static int Level(object state, string id) => (int)Call(state, "CallingLevel", id);
    static object Recipe(object node, int level) => Call(node, "RecipeOf", level);
    static object[] Slots(object recipe) => ((IEnumerable)Val(recipe, "slots")).Cast<object>().ToArray();
    static string[] Options(object slot) => ((IEnumerable)Val(slot, "anyOf")).Cast<string>().ToArray();
    static readonly Func<string, int> Plenty = id => 99;
    /// <summary>One def id per item: exactly the recipe of a node's level (the first option of an "or" slot).</summary>
    static List<string> Parts(string id, int level) => (List<string>)Call(Recipe(Node(id), level), "Pick", Plenty);
    /// <summary>"2 old_work_boot + 1 oven_mitt" ("1 a or b" for an "or" slot): the recipe in the table's own words.</summary>
    static string Spec(object recipe) => (string)Call(recipe, "Text", (Func<string, string>)(id => id));
    static string Status(object state, string id, Func<string, int> have = null) => Call(state, "StatusOf", Node(id), have).ToString();
    static bool Build(object state, string id, List<string> offered) => (bool)Call(state, "BuildCalling", id, offered, null);
    static bool BuildNext(object state, string id) => Build(state, id, Parts(id, Level(state, id) + 1));
    static object Item(string defId, string uid, bool hot = false)
    {
        var def = Static("ItemCatalog", "Get", defId); Assert.NotNull(def, defId);
        var item = New("ItemInstance"); Set(item, "uid", uid); Set(item, "defId", defId); Set(item, "massKg", Val(def, "minMass")); Set(item, "condition", 1f);
        Set(item, "value", Val(def, "category").ToString() == "Valuable" ? Val(def, "minValue") : 0f); Set(item, "hot", hot);
        return item;
    }
    static IList Items(IEnumerable<string> defIds)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ItemInstance"))); int n = 0;
        foreach (string defId in defIds) list.Add(Item(defId, "calltest-" + n++));
        return list;
    }
    static void SetLevel(object state, string id, int level) => Call(state, "SetCallingLevel", Index(Node(id)), level);
    /// <summary>A business that has found and kept its SolidWaste 3D floppy: nothing is printed without it (DiskTests).</summary>
    static object Printer() { var state = New("WorkshopState"); Set(state, "disk", 1); return state; }

    static readonly string[] GearIds = { "work_gloves", "work_boots", "headlamp", "treasure_wand", "rabies_vaccine", "hard_hat", "grabber_pole" };
    /// <summary>Stage 4 (Raccoon): node id -> levels whose gameplay shipped (Callings/Raccoon/*; rules pinned by RaccoonCallingTests).</summary>
    static readonly Dictionary<string, int> RaccoonShipped = new Dictionary<string, int> {
        { "calling_critter_hutch", 2 }, { "calling_embrace_it", 3 }, { "calling_collar_chip", 1 }, { "calling_breeding_box", 1 },
        { "calling_brain_chip", 2 }, { "calling_pack_leader", 2 }, { "calling_kings_rival", 1 } };
    /// <summary>New (calling_*) nodes whose gameplay has shipped: id -> buildable levels. A stage adds its rows here.</summary>
    static readonly Dictionary<string, int> ShippedCallings = new Dictionary<string, int> {
        // stage 2, Hauler (Assets/Scripts/Callings/Hauler; rules pinned by HaulerTests)
        { "calling_rocket_blades", 3 }, { "calling_compactor_backpack", 3 }, { "calling_one_man_route", 1 },
        // stage 3, Scavenger (Assets/Scripts/Callings/Scavenger; rules pinned by ScavengerTests)
        { "calling_nose_for_money", 2 }, { "calling_xray_specs", 2 }, { "calling_the_itch", 1 }, { "calling_lockpicks", 2 },
        { "calling_soft_soles", 2 }, { "calling_swag_sack", 2 }, { "calling_the_fence", 1 }, { "calling_call_it", 1 },
    };

    // ------------------------------------------------------------------ catalog integrity

    [Test]
    public void NodesKeepTheirSavedIndicesAndUniqueIds()
    {
        // Saved positions: APPEND to this list when a node is added; never reorder, rename or reuse an entry.
        var expected = new[] {
            "work_gloves", "work_boots", "hard_hat",
            "grabber_pole", "calling_rocket_blades", "calling_compactor_backpack", "calling_one_man_route",
            "headlamp", "treasure_wand", "calling_nose_for_money", "calling_xray_specs", "calling_the_itch", "calling_call_it",
            "calling_lockpicks", "calling_soft_soles", "calling_swag_sack", "calling_the_fence",
            "rabies_vaccine", "calling_critter_hutch", "calling_embrace_it", "calling_collar_chip", "calling_breeding_box",
            "calling_brain_chip", "calling_pack_leader", "calling_kings_rival" };
        var nodes = Nodes;
        Assert.AreEqual(expected.Length, nodes.Length); Assert.AreEqual(nodes.Length, Val(T("CallingCatalog"), "Count"));
        for (int i = 0; i < nodes.Length; i++) {
            Assert.AreEqual(expected[i], Id(nodes[i])); Assert.AreEqual(i, Index(nodes[i]));
            Assert.AreSame(nodes[i], Static("CallingCatalog", "At", i)); Assert.AreSame(nodes[i], Node(expected[i]));
            Assert.AreEqual(Id(nodes[i]), Val(nodes[i], "upgradeId"), "a node drives the upgrade id it is named after");
            Assert.IsTrue(GearIds.Contains(Id(nodes[i])) || Id(nodes[i]).StartsWith("calling_"), Id(nodes[i]));
        }
        Assert.AreEqual(nodes.Length, nodes.Select(Id).Distinct().Count());
        Assert.IsNull(Node("made_up")); Assert.IsNull(Node(null)); Assert.IsNull(Static("CallingCatalog", "At", -1)); Assert.IsNull(Static("CallingCatalog", "At", nodes.Length));
        foreach (string branch in new[] { "Basics", "Hauler", "Scavenger", "Raccoon" }) Assert.IsTrue(nodes.Any(n => Branch(n) == branch), branch);
        Assert.AreEqual(1, nodes.Count(n => Branch(n) == "Hauler" && (bool)Val(n, "isCapstone")));
        Assert.AreEqual(1, nodes.Count(n => Branch(n) == "Scavenger" && (bool)Val(n, "isCapstone")));
        Assert.AreEqual(1, nodes.Count(n => Branch(n) == "Raccoon" && (bool)Val(n, "isCapstone")));
    }

    [Test]
    public void PrerequisitesResolveRunDownTheTreeAndNeverCycle()
    {
        var nodes = Nodes;
        foreach (var node in nodes)
            foreach (string text in Prereqs(node)) {
                var (other, level) = Prereq(text);
                Assert.AreNotSame(node, other); Assert.That(level, Is.InRange(1, Max(other)), text);
                if (Branch(other) == Branch(node)) {
                    Assert.Less(Row(other), Row(node), Id(node) + " must sit below " + Id(other));
                    // the connector drops down the prerequisite's column to the gap above this row: nothing may be in its way
                    for (int row = Row(other) + 1; row < Row(node); row++)
                        Assert.IsFalse(nodes.Any(n => Branch(n) == Branch(node) && Col(n) == Col(other) && Row(n) == row), Id(other) + " -> " + Id(node) + " runs through a node");
                }
                else Assert.AreEqual("Basics", Branch(other), "only the Basics trunk feeds another calling");
            }
        // explicit cycle check (belt and braces: the row rule already forbids one)
        var visiting = new HashSet<string>(); var done = new HashSet<string>();
        void Visit(object node)
        {
            if (done.Contains(Id(node))) return;
            Assert.IsTrue(visiting.Add(Id(node)), "prerequisite cycle through " + Id(node));
            foreach (string text in Prereqs(node)) Visit(Prereq(text).node);
            visiting.Remove(Id(node)); done.Add(Id(node));
        }
        foreach (var node in nodes) Visit(node);
        // every calling's first row hangs off the trunk, and the trunk itself needs nothing
        string trunk = (string)T("CallingCatalog").GetField("TrunkId", Flags).GetRawConstantValue();
        Assert.AreEqual("work_gloves", trunk); Assert.IsEmpty(Prereqs(Node(trunk)));
        foreach (var node in nodes.Where(n => Branch(n) != "Basics" && Row(n) == 0)) CollectionAssert.AreEqual(new[] { trunk + ":1" }, Prereqs(node), Id(node));
    }

    [Test]
    public void GridCellsAreUniquePerCallingAndFitItsColumns()
    {
        foreach (var group in Nodes.GroupBy(Branch)) {
            int columns = (int)Val(Static("CallingCatalog", "Info", Enum.Parse(T("Calling"), group.Key)), "columns");
            Assert.AreEqual(group.Count(), group.Select(n => (Col(n), Row(n))).Distinct().Count(), group.Key + " has two nodes in one cell");
            foreach (var node in group) { Assert.That(Col(node), Is.InRange(0, columns - 1), Id(node)); Assert.GreaterOrEqual(Row(node), 0); }
        }
        foreach (var info in ((IEnumerable)Val(T("CallingCatalog"), "Callings")).Cast<object>()) {
            Assert.IsNotEmpty((string)Val(info, "name")); Assert.IsNotEmpty((string)Val(info, "tagline"));
        }
    }

    [Test]
    public void BuildableLevelsAreExactlyTheLevelsTheGameHasEffectsFor()
    {
        foreach (string gear in GearIds) {
            var node = Node(gear); Assert.NotNull(node, gear + " must be on the tree: Scrapazon no longer sells it");
            int gameMax = (int)Static("UpgradeEffects", "GearMaxLevel", gear);
            Assert.Greater(gameMax, 0); Assert.AreEqual(gameMax, Buildable(node), gear + ": buildable levels must equal the levels with an effect");
        }
        foreach (var node in Nodes) {
            Assert.That(Buildable(node), Is.InRange(0, Max(node)));
            if (GearIds.Contains(Id(node))) continue;
            if (RaccoonShipped.TryGetValue(Id(node), out int raccoonLevels)) {
                Assert.AreEqual(raccoonLevels, Buildable(node), Id(node) + ": the Raccoon stage ships exactly these levels");
                Assert.AreEqual(0, (int)Static("UpgradeEffects", "GearMaxLevel", Id(node)), Id(node) + " is a calling, not legacy gear");
                continue;
            }
            // A new calling becomes buildable only together with its gameplay: the stage that flips it must extend this test.
            int shipped = ShippedCallings.TryGetValue(Id(node), out int levels) ? levels : 0;
            Assert.AreEqual(shipped, Buildable(node), Id(node) + ": buildable levels must equal the levels a stage has registered an effect for here");
            Assert.AreEqual(0, (int)Static("UpgradeEffects", "GearMaxLevel", Id(node)));
        }
        // CallingIds repeats the legacy strings so the catalog stays free of UnityEngine; they must never drift from ShopIds.
        foreach (var (calling, shop) in new[] { ("WorkGloves", "WorkGloves"), ("WorkBoots", "WorkBoots"), ("HardHat", "HardHat"), ("GrabberPole", "GrabberPole"),
            ("TreasureWand", "TreasureWand"), ("Headlamp", "Headlamp"), ("RabiesVaccine", "RabiesVaccine") })
            Assert.AreEqual(T("ShopIds").GetField(shop).GetRawConstantValue(), T("CallingIds").GetField(calling).GetRawConstantValue());
        var ids = T("CallingIds").GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()).ToArray();
        CollectionAssert.AreEquivalent(Nodes.Select(Id), ids, "every CallingIds constant is a node and every node has a constant");
    }

    /// <summary>The recipe table the owner approved, in the table's own words (levels beyond these rows are checked for
    /// shape only). Changing a recipe means changing it here too, on purpose.</summary>
    static readonly Dictionary<string, string[]> ApprovedRecipes = new Dictionary<string, string[]> {
        { "work_gloves", new[] { "2 old_work_boot + 1 oven_mitt", "1 leather_belt + 2 bike_inner_tube + 1 duct_tape", "2 yoga_mat + 1 bowling_ball + 2 leather_belt" } },
        { "work_boots", new[] { "2 old_work_boot + 1 bike_inner_tube", "2 trampoline_spring + 1 single_croc + 1 duct_tape" } },
        { "hard_hat", new[] { "1 bike_helmet + 1 dented_pot + 1 duct_tape" } },
        { "grabber_pole", new[] { "1 broom_handle + 1 kitchen_tongs + 1 bike_chain", "2 broom_handle + 1 bungee_cord + 2 coat_hangers", "1 extension_cord + 2 trampoline_spring + 1 desk_fan" } },
        { "calling_rocket_blades", new[] { "8 skate_wheel + 2 old_work_boot + 1 jar_of_screws", "2 fire_extinguisher + 1 broken_toaster + 1 bungee_cord", "1 propane_tank + 1 kids_scooter + 2 hubcap" } },
        { "calling_compactor_backpack", new[] { "1 old_backpack + 2 bungee_cord + 1 leather_belt", "1 dented_pot + 2 door_hinge + 1 bike_chain + 1 rusty_hammer", "1 empty_toolbox + 4 trampoline_spring + 1 car_battery" } },
        { "calling_one_man_route", new[] { "4 stroller_wheel + 1 bald_tire + 1 pet_carrier + 1 cordless_drill" } },
        { "headlamp", new[] { "1 bike_helmet + 1 dead_lightbulb + 4 aa_battery", "1 fluorescent_tube + 1 perfume_bottle + 1 lava_lamp" } },
        { "treasure_wand", new[] { "1 broom_handle + 1 old_flip_phone + 1 coat_hangers", "1 circuit_board + 1 tv_remote + 1 jar_of_pennies" } },
        { "calling_nose_for_money", new[] { "1 perfume_bottle + 1 magnifying_glass + 1 junk_mail", "1 baby_monitor + 1 love_letters + 2 magazine_stack" } },
        { "calling_xray_specs", new[] { "2 reading_glasses + 1 cracked_mirror + 1 circuit_board", "1 instapix_camera + 1 fitness_tracker + 1 magnifying_glass" } },
        { "calling_the_itch", new[] { "1 sock_of_cash or jar_of_pennies + 1 lone_sock + 1 broken_boss_mug" } },
        { "calling_lockpicks", new[] { "6 hairpins + 1 coat_hangers + 1 door_hinge", "1 pocket_watch + 1 jar_of_screws + 1 bike_chain" } },
        { "calling_soft_soles", new[] { "2 lone_sock + 1 soggy_towel + 1 single_croc", "1 yoga_mat + 2 oven_mitt + 1 mlm_leggings" } },
        { "calling_swag_sack", new[] { "1 old_backpack + 1 soggy_towel + 1 bungee_cord", "2 old_backpack + 1 leather_belt + 1 scrapazon_box_mountain" } },
        { "calling_the_fence", new[] { "1 garden_gnome + 1 old_flip_phone + 1 fish_bones" } },
        { "calling_call_it", new[] { "1 snow_globe + 1 magnifying_glass + 1 gameboi + 1 fitness_tracker" } },
        { "rabies_vaccine", new[] { "1 first_aid_tin + 1 hot_sauce_bottle + 2 used_tissues", "2 first_aid_tin + 1 essential_oil_crate" } },
        { "calling_critter_hutch", new[] { "1 pet_carrier + 2 chicken_wire + 1 soggy_towel", "2 pet_carrier + 1 cardboard_box + 2 door_hinge + 1 bucket" } },
        { "calling_embrace_it", new[] { "1 fish_bones + 1 mystery_sludge + 1 lone_sock", "1 cursed_doll + 2 mystery_leftovers + 1 dog_collar", "1 captain_raccoon_1 + 1 moldy_lemon + 1 fluorescent_tube" } },
        { "calling_collar_chip", new[] { "1 dog_collar + 1 tv_remote + 2 aa_battery" } },
        { "calling_breeding_box", new[] { "1 cardboard_box + 1 baby_bottle + 1 soggy_towel + 1 tuna_can" } },
        { "calling_brain_chip", new[] { "1 circuit_board + 1 fitness_tracker + 1 tamagochu" } },
        { "calling_pack_leader", new[] { "3 dog_collar + 1 bucket + 2 tuna_can", "1 crashed_drone + 1 baby_monitor + 3 fish_bones" } },
        { "calling_kings_rival", new[] { "1 raccoon_king_crown or gold_chain + 1 garden_gnome + 1 war_medal + 1 pet_raccoon" } },
    };

    [Test]
    public void EveryLevelHasTextAndARecipeOfRealCatalogItems()
    {
        const int rows = 6; // SolidWaste 3D's parts list scrolls (the old tree screen's fixed row count went with it): this is only a sanity bound
        foreach (var node in Nodes) {
            Assert.IsNotEmpty((string)Val(node, "name")); Assert.IsNotEmpty((string)Val(node, "description"));
            Assert.IsNull(Call(node, "Level", 0)); Assert.IsNull(Call(node, "Level", Max(node) + 1));
            Assert.IsNull(Recipe(node, 0)); Assert.IsNull(Recipe(node, Max(node) + 1));
            Assert.GreaterOrEqual((int)Static("CallingRecipes", "RowsFor", Id(node)), Max(node), Id(node) + ": every level needs a recipe row in CallingRecipes");
            Assert.IsTrue(ApprovedRecipes.ContainsKey(Id(node)), Id(node) + " has no approved recipe");
            for (int level = 1; level <= Max(node); level++) {
                var data = Call(node, "Level", level); string where = Id(node) + " Lv " + level;
                Assert.IsNotEmpty((string)Val(data, "title"), Id(node)); Assert.IsNotEmpty((string)Val(data, "effect"), Id(node));
                if (level <= Buildable(node)) Assert.IsNotEmpty((string)Val(data, "howTo"), where + " needs its how-to-use toast");
                if (Val(data, "howTo") is string howTo) Assert.LessOrEqual(howTo.Length, 200, "the how-to rides in a 512-byte network reply");
                var recipe = Recipe(node, level); Assert.NotNull(recipe, where + " has no recipe"); Assert.AreSame(recipe, Val(data, "recipe"));
                var slots = Slots(recipe); Assert.That(slots.Length, Is.InRange(2, rows), where + ": a shopping list is two to " + rows + " different things");
                Assert.AreEqual(slots.Sum(s => (int)Val(s, "count")), (int)Val(recipe, "total")); Assert.That((int)Val(recipe, "total"), Is.InRange(3, 16), where);
                var seen = new HashSet<string>();
                foreach (var slot in slots) {
                    Assert.That((int)Val(slot, "count"), Is.InRange(1, 8), where); Assert.AreEqual(Options(slot).Length > 1, (bool)Val(slot, "isChoice"));
                    foreach (string option in Options(slot)) {
                        Assert.NotNull(Static("ItemCatalog", "Get", option), where + " names '" + option + "', which is not in the item catalog");
                        Assert.IsTrue(seen.Add(option), where + ": '" + option + "' appears in two slots (filling slots in order would stop being exact)");
                    }
                }
                if (level <= ApprovedRecipes[Id(node)].Length) Assert.AreEqual(ApprovedRecipes[Id(node)][level - 1], Spec(recipe), where);
                // and it reads as words
                string text = (string)Call(recipe, "Text", (Func<string, string>)(id => (string)Val(Static("ItemCatalog", "Get", id), "name")));
                StringAssert.Contains((string)Val(Static("ItemCatalog", "Get", Options(slots[0])[0]), "name"), text);
            }
        }
        CollectionAssert.IsSubsetOf(((IEnumerable<string>)Val(T("CallingRecipes"), "NodeIds")).ToArray(), Nodes.Select(Id).ToArray(), "a recipe row for a node that is not on the tree");
        Assert.IsNull(Static("CallingRecipes", "For", "made_up", 1)); Assert.IsNull(Static("CallingRecipes", "For", "work_gloves", 0)); Assert.IsNull(Static("CallingRecipes", "For", null, 1));
        foreach (string bad in new[] { "", "   ", "0 sock", "x sock", "100 sock", "1 a + ", "1 a|" }) Assert.IsNull(Static("PartRecipe", "Parse", bad), "'" + bad + "'");
    }

    [Test]
    public void EveryRecipePartCanBeFoundByAPlayerWhoCanReachTheNode()
    {
        // Where a part comes from is derived from the catalog (PartSources.KindOf): ordinary loot of a district, or one of
        // the special sources, which must be flagged here by name. Tell-only oddities and event prizes are never ingredients.
        var special = new SortedSet<string>();
        foreach (var node in Nodes)
            for (int level = 1; level <= Max(node); level++)
                foreach (var slot in Slots(Recipe(node, level))) {
                    bool obtainable = false;
                    foreach (string option in Options(slot)) {
                        var def = Static("ItemCatalog", "Get", option); string kind = Static("PartSources", "KindOf", def).ToString(); string where = Id(node) + " Lv " + level + " needs " + option;
                        switch (kind) {
                            case "Loot":
                                Assert.Greater((float)Static("ItemCatalog", "WeightOf", def), 0f, where + ", which never rolls into a bag");
                                Assert.That((int)Static("ItemCatalog", "TierOf", def), Is.InRange(1, 2), where + ": no recipe may need Snootington Hills or the Bluffs");
                                obtainable = true; break;
                            case "GarageOnly": Assert.Greater((float)Static("ItemCatalog", "WeightOf", def), 0f, where); special.Add(option + " (garage-cleanout bags)"); obtainable = true; break;
                            case "NightShift": special.Add(option + " (night shift)"); obtainable = true; break;
                            default: Assert.Fail(where + ", which only comes from " + kind); break;
                        }
                        Assert.IsNotEmpty((string)Static("PartSources", "Hint", def, false), option + " needs a 'found in' hint"); Assert.IsNotEmpty((string)Static("PartSources", "Hint", def, true));
                    }
                    Assert.IsTrue(obtainable);
                }
        CollectionAssert.AreEqual(new[] { "bald_tire (garage-cleanout bags)", "car_battery (garage-cleanout bags)", "pet_raccoon (night shift)", "raccoon_king_crown (night shift)" }, special,
            "the only ingredients that do not come from ordinary bags; flag any new one here on purpose");
        // nothing on the trunk needs a district you unlock later than Minivan Meadows, and the cheapest thing on the tree is pure Acres
        foreach (string part in Parts("work_gloves", 1)) Assert.AreEqual(1, (int)Static("ItemCatalog", "TierOf", Static("ItemCatalog", "Get", part)), part);
        // the hints say what the catalog says
        string Hint(string id) => (string)Static("PartSources", "Hint", Static("ItemCatalog", "Get", id), false);
        StringAssert.Contains("anywhere", Hint("old_work_boot")); StringAssert.Contains("garage", Hint("old_work_boot")); StringAssert.Contains("garage-cleanout", Hint("bald_tire"));
        StringAssert.Contains("Meadows", Hint("yoga_mat")); StringAssert.Contains("steal", Hint("gold_chain")); StringAssert.Contains("EPIC", Hint("gold_chain")); StringAssert.Contains("King", Hint("raccoon_king_crown")); StringAssert.Contains("trap", Hint("pet_raccoon"));
    }

    [Test]
    public void ScrapazonNoLongerSellsPersonalGearButKeepsEverythingElse()
    {
        foreach (string gear in GearIds) Assert.IsNull(Static("ScrapazonCatalog", "Get", gear), gear + " is built on the Callings tree, not bought");
        // (grip_gloves left with the riding grip meter, 12 Oct 2026: RideBoardingTests covers it.)
        foreach (string kept in new[] { "repellent", "rabies_shot", "raccoon_trap", "bag_cannon", "lucky_gnome", "golden_gnome", "garbage_juice" })
            Assert.NotNull(Static("ScrapazonCatalog", "Get", kept), kept);
        // the Gear aisle is not an empty hole: it still lists products, and nothing in the shop needs a removed id to unlock
        var all = ((IEnumerable)Val(T("ScrapazonCatalog"), "All")).Cast<object>().ToArray();
        Assert.IsTrue(all.Any(item => (bool)Call(item, "HasTag", "gear")));
        foreach (var item in all)
            foreach (var level in (string[][])Val(item, "prereqs"))
                foreach (string prereq in level ?? new string[0])
                    Assert.IsFalse(GearIds.Contains(prereq.Split(':')[0]), Val(item, "id") + " still requires " + prereq);
        // callings carry no rank gate at all: materials are the gate
        Assert.IsNull(T("CallingNode").GetField("rankRequired", Flags)); Assert.IsNull(T("CallingNode").GetProperty("rankRequired", Flags));
    }

    // ------------------------------------------------------------------ build rules

    [Test]
    public void BuildingTakesExactlyTheRecipeInPrerequisiteAndLevelOrder()
    {
        var state = Printer();
        Assert.AreEqual("CantAfford", Status(state, "work_gloves"), "with no parts known, nothing is ever 'ready'"); Assert.AreEqual("Ready", Status(state, "work_gloves", Plenty));
        Assert.AreEqual("Locked", Status(state, "headlamp", Plenty));
        string before = JsonUtility.ToJson(state);
        Assert.IsFalse(Build(state, "headlamp", Parts("headlamp", 1)), "the trunk comes first"); Assert.AreEqual(before, JsonUtility.ToJson(state), "a refused build changes nothing");
        Assert.IsFalse(Build(state, "made_up", new List<string>())); Assert.IsFalse(Build(state, null, null)); Assert.AreEqual(before, JsonUtility.ToJson(state));

        // exactly the recipe: one short, one extra, a wrong part, nothing at all
        var gloves = Parts("work_gloves", 1); CollectionAssert.AreEquivalent(new[] { "old_work_boot", "old_work_boot", "oven_mitt" }, gloves);
        foreach (var bad in new[] { gloves.Skip(1).ToList(), gloves.Concat(new[] { "oven_mitt" }).ToList(), new List<string> { "old_work_boot", "oven_mitt", "leather_belt" },
                     new List<string> { "old_work_boot", "old_work_boot", null }, new List<string>(), null }) {
            Assert.IsFalse(Build(state, "work_gloves", bad)); Assert.AreEqual(before, JsonUtility.ToJson(state));
        }
        Assert.IsTrue(Build(state, "work_gloves", gloves)); Assert.AreEqual(1, Level(state, "work_gloves"));
        Assert.AreEqual("Ready", Status(state, "headlamp", Plenty)); Assert.IsTrue(BuildNext(state, "headlamp")); Assert.IsTrue((bool)Call(state, "Valid"));

        // one level at a time, each with its OWN recipe, and never past the top
        Assert.IsFalse(Build(state, "work_gloves", gloves), "level 2 is a different recipe");
        Assert.IsTrue(BuildNext(state, "work_gloves")); Assert.AreEqual(2, Level(state, "work_gloves"));
        Assert.IsTrue(BuildNext(state, "work_gloves")); Assert.AreEqual(3, Level(state, "work_gloves")); Assert.AreEqual("Maxed", Status(state, "work_gloves", Plenty));
        before = JsonUtility.ToJson(state);
        Assert.IsFalse(Build(state, "work_gloves", Parts("work_gloves", 3)), "cannot exceed the top level"); Assert.AreEqual(before, JsonUtility.ToJson(state));
    }

    [Test]
    public void OnePartShortIsNotReadyAndEitherSideOfAnOrSlotBuilds()
    {
        var fresh = Printer();
        Assert.AreEqual("CantAfford", Status(fresh, "work_gloves", id => id == "old_work_boot" ? 1 : 5), "one boot short");
        Assert.AreEqual("Ready", Status(fresh, "work_gloves", id => id == "old_work_boot" ? 2 : id == "oven_mitt" ? 1 : 0), "exactly enough");
        Assert.AreEqual("CantAfford", Status(fresh, "work_gloves", id => -3), "a nonsense count is nothing");

        // The Itch: (sock_of_cash OR jar_of_pennies) + lone_sock + broken_boss_mug
        object Itchy() { var s = Printer(); SetLevel(s, "work_gloves", 1); SetLevel(s, "headlamp", 1); SetLevel(s, "calling_nose_for_money", 1); return s; }
        foreach (string coin in new[] { "sock_of_cash", "jar_of_pennies" }) {
            var s = Itchy(); Assert.IsTrue(Build(s, "calling_the_itch", new List<string> { "lone_sock", coin, "broken_boss_mug" }), coin + " fills the slot (order never matters)");
        }
        var both = Itchy(); string before = JsonUtility.ToJson(both);
        Assert.IsFalse(Build(both, "calling_the_itch", new List<string> { "sock_of_cash", "jar_of_pennies", "lone_sock", "broken_boss_mug" }), "one of them, never both");
        Assert.IsFalse(Build(both, "calling_the_itch", new List<string> { "lone_sock", "broken_boss_mug" })); Assert.AreEqual(before, JsonUtility.ToJson(both));
        var itch = Recipe(Node("calling_the_itch"), 1);
        CollectionAssert.Contains((List<string>)Call(itch, "Pick", Plenty), "sock_of_cash", "the first option is preferred");
        CollectionAssert.Contains((List<string>)Call(itch, "Pick", (Func<string, int>)(id => id == "sock_of_cash" ? 0 : 1)), "jar_of_pennies");
        Assert.IsNull(Call(itch, "Pick", (Func<string, int>)(id => id == "lone_sock" ? 1 : 0))); Assert.IsNull(Call(itch, "Pick", new object[] { null }));
        Assert.AreEqual("Ready", Status(Itchy(), "calling_the_itch", id => id == "sock_of_cash" ? 0 : 1));

        // every "or" slot on the tree: each option alone satisfies it
        int choices = 0;
        foreach (var node in Nodes)
            for (int level = 1; level <= Max(node); level++) {
                var recipe = Recipe(node, level);
                foreach (var slot in Slots(recipe).Where(s => (bool)Val(s, "isChoice"))) {
                    choices++; var options = Options(slot);
                    foreach (string option in options) {
                        var picked = (List<string>)Call(recipe, "Pick", (Func<string, int>)(id => options.Contains(id) ? (id == option ? 9 : 0) : 9));
                        Assert.NotNull(picked, Id(node)); CollectionAssert.Contains(picked, option); Assert.IsTrue((bool)Call(recipe, "Matches", picked), Id(node) + " with " + option);
                    }
                }
            }
        Assert.GreaterOrEqual(choices, 2, "The Itch and King's Rival both offer a choice");
    }

    [Test]
    public void ComingSoonLevelsRefuseWhateverPartsAreOffered()
    {
        var state = Printer();
        Assert.IsTrue(BuildNext(state, "work_gloves")); Assert.IsTrue(BuildNext(state, "grabber_pole"));
        string before = JsonUtility.ToJson(state);
        foreach (var node in Nodes.Where(n => Buildable(n) == 0)) {
            Assert.AreEqual("ComingSoon", Status(state, Id(node), Plenty), Id(node));
            Assert.IsFalse(Build(state, Id(node), Parts(Id(node), 1)), Id(node)); Assert.AreEqual(before, JsonUtility.ToJson(state));
        }
        // and a state that claims a level this build has no effect for (coming soon, or past the top) is not a valid upload
        var unshipped = Nodes.FirstOrDefault(n => Buildable(n) < Max(n)) ?? Node("grabber_pole");
        Assert.IsTrue((bool)Call(state, "Valid"), "valid up to here, so the next line fails for the level alone");
        SetLevel(state, Id(unshipped), Buildable(unshipped) + 1); Assert.IsFalse((bool)Call(state, "Valid"));
        Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", JsonUtility.ToJson(state)));
    }

    [Test]
    public void ABuildChecksEveryOfferedItemAndAnswersWithTheHowToLine()
    {
        var state = Printer();
        byte action = (byte)T("Workshop").GetField("ActionBuildCalling", Flags).GetRawConstantValue(); Assert.AreEqual(4, action, "wire value");
        Assert.IsNull(T("Workshop").GetField("ActionBuildSculpture", Flags), "byte 7 built a bench sculpture: the bench is gone and the byte is dead (MonumentTests pins the live ones)");
        var apply = T("Workshop").GetMethod("ApplyBuild", Flags);
        object[] Args(string id, IList items, byte kind = 4) => new object[] { state, kind, id, items, null };
        var args = Args("work_gloves", Items(new string[0])); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.IsNotEmpty((string)args[4], "a refusal says why");
        string before = JsonUtility.ToJson(state);
        // a doctored item (heavier than the catalog allows) or the same uid twice refuses the whole offer
        var heavy = Items(Parts("work_gloves", 1)); Set(heavy[0], "massKg", 40f);
        args = Args("work_gloves", heavy); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.AreEqual(before, JsonUtility.ToJson(state));
        var twice = Items(Parts("work_gloves", 1)); Set(twice[1], "uid", Val(twice[0], "uid"));
        args = Args("work_gloves", twice); Assert.IsFalse((bool)apply.Invoke(null, args)); Assert.AreEqual(before, JsonUtility.ToJson(state));
        // stolen parts are welcome: the printer does not ask where a boot came from
        var hot = Items(Parts("work_gloves", 1)); foreach (var item in hot) Set(item, "hot", true);
        args = Args("work_gloves", hot); Assert.IsTrue((bool)apply.Invoke(null, args));
        Assert.AreEqual(Val(Call(Node("work_gloves"), "Level", 1), "howTo"), args[4]); Assert.AreEqual(1, Level(state, "work_gloves"));
        args = Args("made_up", Items(new string[0])); Assert.IsFalse((bool)apply.Invoke(null, args));
        // (a node no stage has shipped yet, while there still is one, answers "coming soon")
        string soon = Nodes.Where(n => Buildable(n) == 0).Select(Id).FirstOrDefault();
        if (soon != null) { args = Args(soon, Items(new string[0])); Assert.IsFalse((bool)apply.Invoke(null, args)); StringAssert.Contains("soon", ((string)args[4]).ToLowerInvariant()); }
        // the dead sculpture byte builds nothing, whatever it is handed: not a retired sculpture, not a blueprint
        before = JsonUtility.ToJson(state);
        var pieces = Enumerable.Repeat("soda_can", 12).Concat(Enumerable.Repeat("water_bottle", 8)).Concat(Enumerable.Repeat("beer_bottle", 4)).Concat(Enumerable.Repeat("newspaper", 6));
        args = Args("junk_sculpture", Items(pieces), 7); Assert.IsFalse((bool)apply.Invoke(null, args), "byte 7 is dead"); Assert.AreEqual("Unknown shop action.", args[4]);
        args = Args("work_gloves", Items(Parts("work_gloves", 2)), 7); Assert.IsFalse((bool)apply.Invoke(null, args), "byte 7 is dead for blueprints too");
        args = Args("junk_sculpture", Items(pieces), 4); Assert.IsFalse((bool)apply.Invoke(null, args), "a sculpture id is not a blueprint");
        Assert.AreEqual(before, JsonUtility.ToJson(state)); Assert.IsFalse((bool)Call(state, "Completed", "junk_sculpture"));

        // the small commands answer on their own bytes; the bank's two bytes, the sculpture's and the two print bytes are not commands
        var command = T("Workshop").GetMethod("ApplyCommand", Flags);
        foreach (byte dead in new byte[] { 0, 1, 4, 7, 8, 99 }) {
            var c = new object[] { state, dead, "bottle_cabinet", 0, null, null }; Assert.IsFalse((bool)command.Invoke(null, c)); Assert.AreEqual("Unknown shop action.", c[5]);
        }
        Assert.AreEqual(before, JsonUtility.ToJson(state));
        var pin = new object[] { state, (byte)5, "old_work_boot", 1, null, null }; Assert.IsTrue((bool)command.Invoke(null, pin)); StringAssert.Contains("Old Work Boot", (string)pin[5]);
        Assert.IsTrue((bool)Call(state, "IsPinned", Static("Workshop", "CatalogNumber", "old_work_boot")));
        pin = new object[] { state, (byte)5, "old_work_boot", 0, null, null }; Assert.IsTrue((bool)command.Invoke(null, pin)); Assert.AreEqual("", Val(state, "pins"));
        pin = new object[] { state, (byte)5, "made_up", 1, null, null }; Assert.IsFalse((bool)command.Invoke(null, pin));
        var decor = new object[] { state, (byte)6, "doorbell", 0, null, null }; Assert.IsTrue((bool)command.Invoke(null, decor)); Assert.IsTrue((bool)Call(state, "Completed", "doorbell"));
        decor = new object[] { state, (byte)6, "doorbell", 0, null, null }; Assert.IsFalse((bool)command.Invoke(null, decor)); Assert.AreEqual("", decor[5], "a repeated install is silent");
        decor = new object[] { state, (byte)6, "sculpture_tower", 0, null, null }; Assert.IsFalse((bool)command.Invoke(null, decor)); Assert.IsNotEmpty((string)decor[5]);
        // hunting for parts and taking a delivery do not need the disk: only printing does
        var diskless = New("WorkshopState");
        pin = new object[] { diskless, (byte)5, "old_work_boot", 1, null, null }; Assert.IsTrue((bool)command.Invoke(null, pin));
        decor = new object[] { diskless, (byte)6, "break_sofa", 0, null, null }; Assert.IsTrue((bool)command.Invoke(null, decor)); Assert.IsTrue((bool)Call(diskless, "Valid"));
        Assert.IsTrue((bool)Call(state, "Valid"));
    }

    // ------------------------------------------------------------------ replication + saves

    [Test]
    public void AFullyBuiltTreeStaysFarInsideTheNetworkLimitAndRoundTrips()
    {
        // worst case for the tree on the wire: every node at its top level, every piece installed, four displays with the longest ids
        // (WorkshopStateTests adds a full hutch and pin list on top)
        var worst = New("WorkshopState");
        foreach (var node in Nodes) SetLevel(worst, Id(node), Max(node));
        Set(worst, "completed", (ushort)T("WorkshopCatalog").GetField("AllBits").GetRawConstantValue()); Set(worst, "colorIndex", 7);
        var displays = (IList)Val(worst, "displays");
        foreach (string recipe in new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" }) {
            var pin = New("WorkshopDisplayData");
            Set(pin, "recipeId", recipe); Set(pin, "uid", new string('u', 64)); Set(pin, "defId", new string('d', 64)); Set(pin, "massKg", 99.999f); Set(pin, "condition", 0.123456f);
            displays.Add(pin);
        }
        int length = JsonUtility.ToJson(worst).Length;
        Assert.Less(length, 2000, "the replicated workshop JSON is capped at 4000 characters; callings must leave plenty of room");
        var onlyTree = New("WorkshopState"); foreach (var node in Nodes) SetLevel(onlyTree, Id(node), Max(node));
        Assert.Less(JsonUtility.ToJson(onlyTree).Length - JsonUtility.ToJson(New("WorkshopState")).Length, 100, "the whole tree costs the wire a few dozen characters");

        // everything buildable today: valid, survives the wire byte for byte, and a clone is independent
        var built = Printer();
        foreach (var node in Nodes) SetLevel(built, Id(node), Buildable(node));
        Assert.IsTrue((bool)Call(built, "Valid"));
        string json = JsonUtility.ToJson(built); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, JsonUtility.ToJson(restored));
        foreach (var node in Nodes) Assert.AreEqual(Buildable(node), Level(restored, Id(node)));
        var copy = Call(built, "Clone"); SetLevel(copy, "work_gloves", 0);
        Assert.AreEqual(json, JsonUtility.ToJson(built)); Assert.AreEqual(3, Level(built, "work_gloves"));
    }

    [Test]
    public void OldSavesLoadAsAnEmptyTreeAndBadLevelsCannotBeUploaded()
    {
        var old = Static("BusinessRegistry", "ParseWorkshop", "{\"completed\":16,\"displays\":[]}");
        Assert.NotNull(old, "a state written before Callings existed is still valid"); Assert.AreEqual((ushort)16, Val(old, "completed"));
        foreach (var node in Nodes) Assert.AreEqual(0, Level(old, Id(node)));
        Assert.IsTrue((bool)Call(old, "Valid"));
        Set(old, "callings", null); Assert.IsTrue((bool)Call(old, "Valid")); Assert.AreEqual(0, Level(old, "work_gloves"));
        Assert.NotNull(Val(Call(old, "Clone"), "callings"));

        string tooMany = "[" + string.Join(",", Enumerable.Repeat("0", Nodes.Length + 1)) + "]";
        // one level past the top of a node, and (while a stage is still unshipped) one level past what this build can own
        string LevelAt(object node, int level) => "[" + string.Join(",", Enumerable.Range(0, Index(node) + 1).Select(i => i == Index(node) ? level.ToString() : "0")) + "]";
        var unshipped = Nodes.FirstOrDefault(n => Buildable(n) < Max(n)) ?? Node("grabber_pole");
        foreach (string callings in new[] { "[4]", "[-1]", "[0,3]", "[0,0,2]", LevelAt(Node("grabber_pole"), Max(Node("grabber_pole")) + 1),
                     LevelAt(unshipped, Buildable(unshipped) + 1), tooMany })
            Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", "{\"displays\":[],\"disk\":1,\"callings\":" + callings + "}"), callings);
        Assert.NotNull(Static("BusinessRegistry", "ParseWorkshop", "{\"displays\":[],\"disk\":1,\"callings\":[3,2,1,1]}"));
        Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", "{\"displays\":[],\"callings\":[3,2,1,1]}"), "printed levels without the disk that prints them");

        // a LOCAL save with a level this build cannot own is repaired, never thrown away with its installed pieces
        var future = New("WorkshopState"); Set(future, "completed", (ushort)777);
        SetLevel(future, "work_gloves", 9); SetLevel(future, "calling_kings_rival", 7); ((IList)Val(future, "callings")).Add(5);
        Assert.IsFalse((bool)Call(future, "Valid")); Assert.IsTrue((bool)Call(future, "ValidIgnoringCallings"));
        Call(future, "NormalizeCallings");
        Assert.IsTrue((bool)Call(future, "Valid")); Assert.AreEqual(3, Level(future, "work_gloves"));
        Assert.AreEqual(1, Val(future, "disk"), "a save that was printing before the disk existed is handed its disk by the same repair");
        Assert.AreEqual(Buildable(Node("calling_kings_rival")), Level(future, "calling_kings_rival"), "clamped to what this build can own");
        Assert.AreEqual((ushort)777, Val(future, "completed")); Assert.AreEqual(Nodes.Length, ((IList)Val(future, "callings")).Count);
    }

    // ------------------------------------------------------------------ legacy gear: seeded free, never lowered

    static Func<string, int> Owned(params (string id, int level)[] levels) => id => levels.Where(l => l.id == id).Select(l => l.level).FirstOrDefault();

    [Test]
    public void GearASaveAlreadyOwnsShowsAsBuiltForFreeAndSeedingIsIdempotent()
    {
        var state = New("WorkshopState"); Set(state, "completed", (ushort)5); Assert.IsTrue((bool)Call(state, "SetPin", 12, true, null));
        string untouched = JsonUtility.ToJson(state);
        Assert.IsFalse((bool)Call(state, "SeedCallings", Owned()), "a new business has nothing to seed"); Assert.AreEqual(untouched, JsonUtility.ToJson(state));
        Assert.IsFalse((bool)Call(state, "SeedCallings", new object[] { null })); Assert.AreEqual(untouched, JsonUtility.ToJson(state));

        // (a node no stage has shipped yet, if there still is one: owning its id must never seed it)
        string soon = Nodes.Where(n => Buildable(n) == 0).Select(Id).FirstOrDefault() ?? "calling_not_on_the_tree";
        var legacy = Owned(("work_gloves", 2), ("headlamp", 5), ("grabber_pole", 1), ("rabies_vaccine", 1), (soon, 3), ("truck_turbo", 3));
        Assert.IsTrue((bool)Call(state, "SeedCallings", legacy));
        Assert.AreEqual(2, Level(state, "work_gloves")); Assert.AreEqual(2, Level(state, "headlamp"), "an out-of-range legacy level clamps to the top");
        Assert.AreEqual(1, Level(state, "grabber_pole")); Assert.AreEqual(1, Level(state, "rabies_vaccine"));
        Assert.AreEqual(0, Level(state, soon), "a coming-soon node is never seeded"); Assert.AreEqual(0, Level(state, "work_boots"));
        Assert.AreEqual((ushort)5, Val(state, "completed")); Assert.AreEqual("c", Val(state, "pins"), "seeding touches nothing else");
        // seeded gear is printed gear, and printing needs the disk: the load path repairs right after seeding (GameSession.LoadWorkshop)
        Assert.AreEqual(0, Val(state, "disk")); Assert.IsFalse((bool)Call(state, "Valid"));
        Call(state, "NormalizePrinter"); Assert.AreEqual(1, Val(state, "disk")); Assert.IsTrue((bool)Call(state, "Valid"));

        string seeded = JsonUtility.ToJson(state);
        Assert.IsFalse((bool)Call(state, "SeedCallings", legacy), "seeding twice changes nothing"); Assert.AreEqual(seeded, JsonUtility.ToJson(state));
        Assert.IsFalse((bool)Call(state, "SeedCallings", Owned(("work_gloves", 1))), "seeding never lowers a level"); Assert.AreEqual(seeded, JsonUtility.ToJson(state));

        // the next level takes ITS recipe: the seeded ones were free, not prepaid
        Assert.IsFalse(Build(state, "work_gloves", Parts("work_gloves", 1)), "level 1's parts do not build level 3");
        Assert.IsTrue(Build(state, "work_gloves", Parts("work_gloves", 3))); Assert.AreEqual(3, Level(state, "work_gloves"));
    }

    [Test]
    public void BuiltCallingsOnlyEverRaiseUpgradeLevels()
    {
        var state = New("WorkshopState");
        SetLevel(state, "work_gloves", 1); SetLevel(state, "headlamp", 2); SetLevel(state, "hard_hat", 1);
        List<KeyValuePair<string, int>> Raises(Func<string, int> current) => (List<KeyValuePair<string, int>>)Call(state, "CallingUpgrades", current);

        CollectionAssert.AreEquivalent(new[] { "work_gloves:1", "hard_hat:1", "headlamp:2" }, Raises(Owned()).Select(r => r.Key + ":" + r.Value));
        // gloves bought with cash at level 3 stay level 3; an equal level is not rewritten; a lower one is raised
        var raises = Raises(Owned(("work_gloves", 3), ("hard_hat", 1), ("headlamp", 1)));
        CollectionAssert.AreEqual(new[] { "headlamp:2" }, raises.Select(r => r.Key + ":" + r.Value));
        Assert.IsEmpty(Raises(Owned(("work_gloves", 9), ("hard_hat", 9), ("headlamp", 9))));
        Assert.AreEqual(3, Raises(null).Count, "no reader means nothing is known to be owned");
        foreach (var pair in new[] { (1, 0), (2, 1), (3, 2), (3, 3), (0, 3), (1, 3) }) {
            var one = New("WorkshopState"); SetLevel(one, "work_gloves", pair.Item1);
            foreach (var raise in (List<KeyValuePair<string, int>>)Call(one, "CallingUpgrades", Owned(("work_gloves", pair.Item2))))
                Assert.Greater(raise.Value, pair.Item2, "a calling can never lower an upgrade level");
        }
    }

    [Test]
    public void LoadingASaveSeedsTheTreeFromItsGearAndItsGearFromTheTree()
    {
        var session = T("GameSession");
        var workshopField = session.GetField("workshop", Flags); var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        object previousWorkshop = workshopField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels);
        try {
            levels.Clear(); levels["work_gloves"] = 2; levels["truck_turbo"] = 1; levels["headlamp"] = 2;
            var saved = New("WorkshopState"); Set(saved, "completed", (ushort)(1 << 5)); // a bench sculpture: retired, never a Scrapazon product
            SetLevel(saved, "headlamp", 1); SetLevel(saved, "hard_hat", 1); SetLevel(saved, "work_gloves", 9); // 9: not ownable, repaired on load
            Static("GameSession", "LoadWorkshop", saved);
            var loaded = workshopField.GetValue(null);
            Assert.AreNotSame(saved, loaded); Assert.IsTrue((bool)Call(loaded, "Valid"));
            Assert.AreEqual(3, Level(loaded, "work_gloves")); Assert.AreEqual(2, Level(loaded, "headlamp"), "owned gear raises the tree");
            Assert.AreEqual(1, Level(loaded, "hard_hat"));
            Assert.AreEqual((ushort)0, Val(loaded, "completed"), "the retired sculpture's bit is gone"); Assert.AreEqual(1, Val(loaded, "monument"), "and stands outside as Monument tier 1");
            Assert.AreEqual(1, Val(loaded, "disk"), "a save that owns gear has its disk: it keeps printing");
            Assert.AreEqual(3, levels["work_gloves"], "the tree raises gear"); Assert.AreEqual(1, levels["hard_hat"]);
            Assert.AreEqual(2, levels["headlamp"], "and never lowers it"); Assert.AreEqual(1, levels["truck_turbo"]); Assert.AreEqual(4, levels.Count);

            // a brand-new business and a pre-Callings save both come through with an empty tree
            levels.Clear(); Static("GameSession", "LoadWorkshop", new object[] { null });
            loaded = workshopField.GetValue(null);
            foreach (var node in Nodes) Assert.AreEqual(0, Level(loaded, Id(node))); Assert.IsEmpty(levels);
            Assert.AreEqual(0, Val(loaded, "disk"), "and no disk: a new business finds its floppy in the trash");
        }
        finally { workshopField.SetValue(null, previousWorkshop); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted"); }
        // This test never saves, switches profiles or raises game events: LoadWorkshop is the silent load path.
    }
}

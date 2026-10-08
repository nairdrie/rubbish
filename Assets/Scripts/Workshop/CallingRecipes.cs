using System;
using System.Collections.Generic;

// What a Callings level costs: SPECIFIC kept items (Docs/Progression.md "Callings"). Pure data + rules, no UnityEngine:
// Tools/workshop_state_probe.cs compiles this file standalone with CallingCatalog.cs, WorkshopState.cs and WorkshopCatalog.cs.

/// <summary>One line of a recipe: <see cref="count"/> items whose def is any of <see cref="anyOf"/> ("A or B" fills the same slot).</summary>
public sealed class RecipeSlot
{
    public readonly IReadOnlyList<string> anyOf;
    public readonly int count;
    /// <summary>The first (preferred) def id: what the shopping list shows first.</summary>
    public string defId => anyOf[0];
    public bool isChoice => anyOf.Count > 1;
    public bool Accepts(string id) { foreach (string option in anyOf) if (option == id) return true; return false; }
    internal RecipeSlot(string[] anyOf, int count) { this.anyOf = Array.AsReadOnly(anyOf); this.count = count; }
}

/// <summary>
/// A list of (item def, count) slots. Any instance of a def counts (stolen ones and valuables included). No def appears in
/// two slots of one recipe (CallingsTests), so filling slots in order is exact.
/// </summary>
public sealed class PartRecipe
{
    public readonly IReadOnlyList<RecipeSlot> slots;
    /// <summary>How many items the recipe consumes in all.</summary>
    public readonly int total;

    PartRecipe(RecipeSlot[] slots) { this.slots = Array.AsReadOnly(slots); foreach (var slot in slots) total += slot.count; }

    /// <summary>"2 old_work_boot + 1 oven_mitt"; "sock_of_cash|jar_of_pennies + lone_sock" (a bare id means 1; '|' = or).
    /// Null for empty or malformed text: a level without a recipe cannot be built.</summary>
    public static PartRecipe Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var slots = new List<RecipeSlot>();
        foreach (string raw in text.Split('+')) {
            string part = raw.Trim(); int count = 1;
            int space = part.IndexOf(' ');
            if (space > 0) { if (!int.TryParse(part.Substring(0, space), out count)) return null; part = part.Substring(space + 1).Trim(); }
            if (count < 1 || count > 99 || part.Length == 0) return null;
            var options = part.Split('|');
            for (int i = 0; i < options.Length; i++) { options[i] = options[i].Trim(); if (options[i].Length == 0) return null; }
            slots.Add(new RecipeSlot(options, count));
        }
        return slots.Count == 0 ? null : new PartRecipe(slots.ToArray());
    }

    /// <summary>How many of <paramref name="slot"/> are covered (0..slot.count) when <paramref name="have"/> says how many
    /// usable items of a def the builder holds.</summary>
    public static int Covered(RecipeSlot slot, Func<string, int> have)
    {
        if (slot == null || have == null) return 0;
        int got = 0;
        foreach (string option in slot.anyOf) got += Math.Max(0, have(option));
        return Math.Min(slot.count, got);
    }
    public bool CanBuild(Func<string, int> have)
    {
        foreach (var slot in slots) if (Covered(slot, have) < slot.count) return false;
        return true;
    }
    /// <summary>The def ids to hand over, one entry per item (first option of an "or" slot first). Null when short.</summary>
    public List<string> Pick(Func<string, int> have)
    {
        if (have == null) return null;
        var picked = new List<string>(total);
        foreach (var slot in slots) {
            int need = slot.count;
            foreach (string option in slot.anyOf) {
                int take = Math.Min(need, Math.Max(0, have(option)));
                for (int i = 0; i < take; i++) picked.Add(option);
                need -= take;
                if (need == 0) break;
            }
            if (need > 0) return null;
        }
        return picked;
    }
    /// <summary>True when <paramref name="offered"/> (one def id per item) fills every slot exactly, with nothing left over.</summary>
    public bool Matches(IReadOnlyList<string> offered)
    {
        if (offered == null || offered.Count != total) return false;
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in offered) { if (string.IsNullOrEmpty(id)) return false; left.TryGetValue(id, out int n); left[id] = n + 1; }
        foreach (var slot in slots) {
            int need = slot.count;
            foreach (string option in slot.anyOf) {
                if (!left.TryGetValue(option, out int n) || n <= 0) continue;
                int take = Math.Min(need, n); left[option] = n - take; need -= take;
                if (need == 0) break;
            }
            if (need > 0) return false;
        }
        foreach (var kv in left) if (kv.Value != 0) return false;
        return true;
    }
    /// <summary>"2 Old Work Boot + 1 Oven Mitt"; an "or" slot reads "1 Sock Full of Cash or Jar of Pennies".</summary>
    public string Text(Func<string, string> nameOf)
    {
        var parts = new List<string>(slots.Count);
        foreach (var slot in slots) {
            var names = new List<string>(slot.anyOf.Count);
            foreach (string option in slot.anyOf) names.Add(nameOf != null ? nameOf(option) ?? option : option);
            parts.Add(slot.count + " " + string.Join(" or ", names));
        }
        return string.Join(" + ", parts);
    }
}

/// <summary>
/// THE recipe table: node id -> one recipe per level. This is the one place to retune what a calling costs. Ids are
/// <c>ItemCatalog</c> def ids (CallingsTests checks every one exists and can be found by a player who can reach the node).
/// A level with no row here has no recipe and cannot be built.
/// </summary>
public static class CallingRecipes
{
    static readonly Dictionary<string, string[]> table = new Dictionary<string, string[]>(StringComparer.Ordinal) {
        // ---------------------------------------------------------------- BASICS
        { CallingIds.WorkGloves, new[] { "2 old_work_boot + 1 oven_mitt", "1 leather_belt + 2 bike_inner_tube + 1 duct_tape", "2 yoga_mat + 1 bowling_ball + 2 leather_belt" } },
        { CallingIds.WorkBoots, new[] { "2 old_work_boot + 1 bike_inner_tube", "2 trampoline_spring + 1 single_croc + 1 duct_tape" } },
        { CallingIds.HardHat, new[] { "1 bike_helmet + 1 dented_pot + 1 duct_tape" } },
        // ---------------------------------------------------------------- HAULER
        { CallingIds.GrabberPole, new[] { "1 broom_handle + 1 kitchen_tongs + 1 bike_chain", "2 broom_handle + 1 bungee_cord + 2 coat_hangers", "1 extension_cord + 2 trampoline_spring + 1 desk_fan" } },
        { CallingIds.RocketBlades, new[] { "8 skate_wheel + 2 old_work_boot + 1 jar_of_screws", "2 fire_extinguisher + 1 broken_toaster + 1 bungee_cord", "1 propane_tank + 1 kids_scooter + 2 hubcap" } },
        { CallingIds.CompactorBackpack, new[] { "1 old_backpack + 2 bungee_cord + 1 leather_belt", "1 dented_pot + 2 door_hinge + 1 bike_chain + 1 rusty_hammer", "1 empty_toolbox + 4 trampoline_spring + 1 car_battery" } },
        { CallingIds.OneManRoute, new[] { "4 stroller_wheel + 1 bald_tire + 1 pet_carrier + 1 cordless_drill" } },
        // ---------------------------------------------------------------- SCAVENGER
        { CallingIds.Headlamp, new[] { "1 bike_helmet + 1 dead_lightbulb + 4 aa_battery", "1 fluorescent_tube + 1 perfume_bottle + 1 lava_lamp" } },
        { CallingIds.TreasureWand, new[] { "1 broom_handle + 1 old_flip_phone + 1 coat_hangers", "1 circuit_board + 1 tv_remote + 1 jar_of_pennies" } },
        { CallingIds.NoseForMoney, new[] { "1 perfume_bottle + 1 magnifying_glass + 1 junk_mail", "1 baby_monitor + 1 love_letters + 2 magazine_stack" } },
        { CallingIds.XRaySpecs, new[] { "2 reading_glasses + 1 cracked_mirror + 1 circuit_board", "1 instapix_camera + 1 fitness_tracker + 1 magnifying_glass" } },
        { CallingIds.TheItch, new[] { "1 sock_of_cash|jar_of_pennies + 1 lone_sock + 1 broken_boss_mug" } },
        { CallingIds.Lockpicks, new[] { "6 hairpins + 1 coat_hangers + 1 door_hinge", "1 pocket_watch + 1 jar_of_screws + 1 bike_chain" } },
        { CallingIds.SoftSoles, new[] { "2 lone_sock + 1 soggy_towel + 1 single_croc", "1 yoga_mat + 2 oven_mitt + 1 mlm_leggings" } },
        { CallingIds.SwagSack, new[] { "1 old_backpack + 1 soggy_towel + 1 bungee_cord", "2 old_backpack + 1 leather_belt + 1 scrapazon_box_mountain" } },
        { CallingIds.TheFence, new[] { "1 garden_gnome + 1 old_flip_phone + 1 fish_bones" } },
        { CallingIds.CallIt, new[] { "1 snow_globe + 1 magnifying_glass + 1 gameboi + 1 fitness_tracker" } },
        // ---------------------------------------------------------------- RACCOON
        { CallingIds.RabiesVaccine, new[] { "1 first_aid_tin + 1 hot_sauce_bottle + 2 used_tissues", "2 first_aid_tin + 1 essential_oil_crate" } },
        { CallingIds.CritterHutch, new[] { "1 pet_carrier + 2 chicken_wire + 1 soggy_towel", "2 pet_carrier + 1 cardboard_box + 2 door_hinge + 1 bucket", "3 chicken_wire + 2 broom_handle + 1 kids_scooter + 2 bucket" } },
        { CallingIds.EmbraceIt, new[] { "1 fish_bones + 1 mystery_sludge + 1 lone_sock", "1 cursed_doll + 2 mystery_leftovers + 1 dog_collar", "1 captain_raccoon_1 + 1 moldy_lemon + 1 fluorescent_tube" } },
        { CallingIds.CollarChip, new[] { "1 dog_collar + 1 tv_remote + 2 aa_battery", "2 dog_collar + 1 circuit_board + 1 baby_monitor" } },
        { CallingIds.BreedingBox, new[] { "1 cardboard_box + 1 baby_bottle + 1 soggy_towel + 1 tuna_can", "2 cardboard_box + 1 baby_monitor + 2 baby_bottle + 1 yoga_mat" } },
        // (level 2 onwards of the chip, hutch 3, collar 2 and box 2 are spare rows: used only if the tree grows those levels)
        { CallingIds.BrainChip, new[] { "1 circuit_board + 1 fitness_tracker + 1 tamagochu", "2 circuit_board + 1 fitness_tracker + 1 occulus_wift" } },
        { CallingIds.PackLeader, new[] { "3 dog_collar + 1 bucket + 2 tuna_can", "1 crashed_drone + 1 baby_monitor + 3 fish_bones" } },
        { CallingIds.KingsRival, new[] { "1 raccoon_king_crown|gold_chain + 1 garden_gnome + 1 war_medal + 1 pet_raccoon" } },
    };
    static readonly Dictionary<string, PartRecipe[]> parsed = new Dictionary<string, PartRecipe[]>(StringComparer.Ordinal);

    /// <summary>The recipe of 1-based <paramref name="level"/> of a node, or null when none is authored.</summary>
    public static PartRecipe For(string nodeId, int level)
    {
        if (string.IsNullOrEmpty(nodeId) || level < 1) return null;
        if (!parsed.TryGetValue(nodeId, out var recipes)) {
            if (!table.TryGetValue(nodeId, out var rows)) return null;
            recipes = new PartRecipe[rows.Length];
            for (int i = 0; i < rows.Length; i++) recipes[i] = PartRecipe.Parse(rows[i]);
            parsed[nodeId] = recipes;
        }
        return level <= recipes.Length ? recipes[level - 1] : null;
    }
    /// <summary>How many recipe rows a node has (tests: at least its level count).</summary>
    public static int RowsFor(string nodeId) => nodeId != null && table.TryGetValue(nodeId, out var rows) ? rows.Length : 0;
    public static IEnumerable<string> NodeIds => table.Keys;
}

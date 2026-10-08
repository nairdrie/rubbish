using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Specific-parts crafting, everything outside the pure tree rules (those are CallingsTests / WorkshopStateTests):
/// the findable parts in the item catalog, KEEP-takes-anything and its three closed loopholes (scrap value, treasure
/// facts, displays), how the print shop picks instances from an inventory, the offer on the wire, the decor aisle and the
/// wanted-part plumbing. (The Monument's own picking rules are MonumentTests.) Game code is reached by reflection (this assembly cannot reference Assembly-CSharp).
/// </summary>
public class PartsCraftingTests
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
    static object[] Catalog => ((IEnumerable)T("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>().ToArray();
    static object Def(string id) { var def = Static("ItemCatalog", "Get", id); Assert.NotNull(def, id); return def; }
    static string Category(object def) => Val(def, "category").ToString();
    static object Item(string defId, string uid, float mass01 = 0f, float value = -1f, bool hot = false)
    {
        var def = Def(defId); var item = New("ItemInstance");
        Set(item, "uid", uid); Set(item, "defId", defId); Set(item, "condition", 1f); Set(item, "hot", hot);
        Set(item, "massKg", Mathf.Lerp((float)Val(def, "minMass"), (float)Val(def, "maxMass"), mass01));
        Set(item, "value", value >= 0f ? value : Category(def) == "Valuable" ? (float)Val(def, "minValue") : 0f);
        return item;
    }
    static IList ItemList(params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ItemInstance")));
        foreach (var item in items) list.Add(item);
        return list;
    }
    static string BlockedBy(object item) => null;
    static object Stock(IList inventory, Func<object, string> blocked = null)
    {
        var type = typeof(Func<,>).MakeGenericType(T("ItemInstance"), typeof(string));
        Delegate reason = blocked == null ? null : Delegate.CreateDelegate(type, blocked.Target, blocked.Method);
        return Static("Workshop", "StockOf", inventory, reason);
    }
    static IList Select(object recipe, object stock) =>
        (IList)T("Workshop").GetMethod("SelectFor", Flags, null, new[] { T("PartRecipe"), T("Workshop").GetNestedType("Stock") }, null).Invoke(null, new[] { recipe, stock });
    static object Recipe(string node, int level) => Call(Static("CallingCatalog", "Get", node), "RecipeOf", level);

    static readonly string[] AcresParts = { "old_work_boot", "leather_belt", "garden_hose", "bike_inner_tube", "skate_wheel", "broom_handle", "extension_cord", "circuit_board",
        "tv_remote", "reading_glasses", "magnifying_glass", "old_backpack", "bungee_cord", "jar_of_screws", "hairpins", "door_hinge", "desk_fan", "bike_helmet", "first_aid_tin",
        "dog_collar", "pet_carrier", "chicken_wire", "kitchen_tongs", "oven_mitt", "bucket" };
    static readonly string[] MeadowsParts = { "stroller_wheel", "kids_scooter", "yoga_mat", "baby_monitor", "fitness_tracker" };

    // ------------------------------------------------------------------ the findable parts

    [Test]
    public void ThirtyPartsAreAppendedAfterEveryOlderItemAsOrdinarySortableFinds()
    {
        var defs = Catalog; var ids = defs.Select(d => (string)Val(d, "id")).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct().Count(), "catalog ids are unique");
        int first = Array.IndexOf(ids, "old_work_boot");
        Assert.Greater(first, Array.IndexOf(ids, "bonsai_money_tree"), "parts come after the last collectible: saved catalog indices (DailyStats) must not move");
        Assert.Greater(first, Array.IndexOf(ids, "big_cleanup_ribbon"));
        CollectionAssert.AreEqual(AcresParts.Concat(MeadowsParts), ids.Skip(first).Take(30), "exactly these thirty, in this order");
        CollectionAssert.AreEqual(new[] { "floppy_disk" }, ids.Skip(first + 30), "and nothing after them but the story items (the SolidWaste 3D floppy: DiskTests)");
        var visuals = new HashSet<string>();
        foreach (string id in AcresParts.Concat(MeadowsParts)) {
            var def = Def(id);
            Assert.AreNotEqual("Valuable", Category(def), id + " is a part, not a treasure"); Assert.AreEqual("Common", Val(def, "rarity").ToString());
            Assert.AreEqual(0f, (float)Val(def, "maxValue"), id + " has no market value: it is worth its bin");
            Assert.That((float)Val(def, "minMass"), Is.GreaterThan(0f).And.LessThanOrEqualTo((float)Val(def, "maxMass"))); Assert.LessOrEqual((float)Val(def, "maxMass"), 4f, id + " must fit a sack many times over");
            Assert.AreEqual(Array.IndexOf(AcresParts, id) >= 0 ? 1 : 2, (int)Static("ItemCatalog", "TierOf", def), id);
            Assert.That((float)Static("ItemCatalog", "WeightOf", def), Is.InRange(0.3f, 1.05f), id + ": parts are scarcer than everyday junk");
            Assert.IsFalse((bool)Static("ItemCatalog", "HasTag", def, "bulk"), id + " must never be a make-weight filler");
            Assert.IsFalse((bool)Static("ItemCatalog", "HasTag", def, "garage_only")); Assert.IsFalse((bool)Static("ItemCatalog", "HasTag", def, "tell_only"));
            StringAssert.StartsWith("part_", (string)Val(def, "visual")); Assert.IsTrue(visuals.Add((string)Val(def, "visual")), id + " shares a model");
            Assert.IsNotEmpty((string)Val(def, "name")); Assert.IsNotEmpty((string)Val(def, "flavor"));
            Assert.AreEqual("Loot", Static("PartSources", "KindOf", def).ToString());
        }
        // every sorted type and the compactor get some, so keeping parts is a real choice at every bin
        foreach (string category in new[] { "Junk", "Plastic", "Glass", "Metal", "Hazard" })
            Assert.IsTrue(AcresParts.Any(id => Category(Def(id)) == category), "no Acres part is " + category);
        // every part is needed by at least one recipe, or it is just more junk
        var used = new HashSet<string>();
        foreach (var node in ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>())
            for (int level = 1; level <= (int)Static("CallingRecipes", "RowsFor", Val(node, "id")); level++)
                foreach (var slot in (IEnumerable)Val(Static("CallingRecipes", "For", Val(node, "id"), level), "slots")) foreach (string option in (IEnumerable)Val(slot, "anyOf")) used.Add(option);
        foreach (string id in AcresParts.Concat(MeadowsParts)) Assert.IsTrue(used.Contains(id) || id == "garden_hose", id + " is in no recipe");
    }

    [Test]
    public void EveryPartHasItsOwnHandBuiltModel()
    {
        var build = T("ItemVisuals").GetMethod("Build");
        var signatures = new HashSet<string>();
        foreach (string id in AcresParts.Concat(MeadowsParts))
        {
            var parent = new GameObject("Part visual audit");
            try
            {
                var model = (GameObject)build.Invoke(null, new object[] { Def(id), parent.transform });
                Assert.Zero(model.GetComponentsInChildren<Collider>(true).Length, id); Assert.Zero(model.GetComponentsInChildren<Rigidbody>(true).Length, id);
                var meshes = model.GetComponentsInChildren<MeshFilter>(true);
                Assert.GreaterOrEqual(meshes.Length, 6, id + " must not fall back to its category's placeholder");
                string geometry = string.Join("|", meshes.Select(m => m.sharedMesh.name + ":" + m.transform.localPosition + ":" + m.transform.localScale + ":" + m.transform.localRotation));
                Assert.IsTrue(signatures.Add(geometry), id + " is geometry-identical to another part");
                var bounds = new Bounds(model.transform.position, Vector3.zero);
                foreach (var renderer in model.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(renderer.bounds);
                Assert.That(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), Is.InRange(0.05f, 2.5f), id + " has a sane size");
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
        }
    }

    // ------------------------------------------------------------------ loophole 1: a part sells for scrap, never more than its bin

    [Test]
    public void ANonValuableIsNeverWorthMoreOnEbaeOrAtPawnPeteThanItsBinPays()
    {
        float Scrap(object item) => (float)Static("EbaeMarket", "ScrapValue", item);
        foreach (var def in Catalog.Where(d => Category(d) != "Valuable")) {
            string id = (string)Val(def, "id");
            foreach (float mass01 in new[] { 0f, 0.37f, 1f }) {
                // even a doctored instance (a $1 rescue-grate value, or a fortune) is worth its bin and no more
                var item = Item(id, "scrap-test", mass01, mass01 == 1f ? 5000f : 1f);
                float kg = (float)Val(item, "massKg");
                float bin = Category(def) == "Hazard" ? (float)T("Economy").GetField("HazardDisposalPay").GetRawConstantValue()
                    : Category(def) == "Junk" ? (float)T("Economy").GetField("CompactorScrapPerKg").GetRawConstantValue() * kg
                    : Mathf.Max((float)T("Economy").GetField("MinRecyclePayout").GetRawConstantValue(), (float)Static("Economy", "RecycleRatePerKg", Val(def, "category")) * kg);
                float scrap = Scrap(item);
                Assert.That(scrap, Is.InRange(0f, bin + 0.0001f), id + " scrap value must not exceed what its bin pays");
                Assert.AreEqual(scrap, Mathf.Floor(scrap * 100f + 0.01f) / 100f, 1e-5f, "whole cents");
                Assert.AreEqual(scrap, (float)Static("EbaeMarket", "Appraise", item), id + ": ebag appraises a part at its scrap value");
                float pawn = (float)Static("EbaeMarket", "PawnPrice", item);
                Assert.That(pawn, Is.InRange(0f, scrap * 0.45f + 0.0001f), id + ": Pawn Pete pays a part less than recycling it would");
                Assert.AreEqual(pawn, Mathf.Floor(pawn * 100f + 0.01f) / 100f, 1e-5f, "pawn in whole cents");
                if (scrap >= 0.03f) Assert.Less(pawn, scrap, id + ": pawning a part never matches its bin");
                Assert.IsTrue((bool)Static("EbaeMarket", "IsPart", item)); Assert.IsTrue((bool)T("Workshop").GetMethod("IsPart", new[] { T("ItemInstance") }).Invoke(null, new[] { item }));
            }
            // a weight no real one could have pays as the heaviest real one
            var fat = Item(id, "scrap-fat", 1f); Set(fat, "massKg", 500f);
            Assert.AreEqual(Scrap(Item(id, "scrap-max", 1f)), Scrap(fat), id);
        }
        // treasures are untouched: rolled value, 45% at the pawn shop
        var gnome = Item("garden_gnome", "scrap-gnome", 0.5f, 40f);
        Assert.IsFalse((bool)Static("EbaeMarket", "IsPart", gnome)); Assert.AreEqual(40f, (float)Static("EbaeMarket", "Appraise", gnome));
        Assert.AreEqual(18f, (float)Static("EbaeMarket", "PawnPrice", gnome), 1e-4f);

        // and a part cannot be listed at all (no buyers, no whales, no haggling); a treasure of the same uid pattern can
        var inventory = (IList)T("GameSession").GetField("Inventory").GetValue(null);
        var boot = Item("old_work_boot", "scrap-test-boot");
        inventory.Add(boot);
        try { Assert.IsFalse((bool)T("EbaeMarket").GetMethod("List", Flags).Invoke(null, new object[] { "scrap-test-boot", 50f }), "parts cannot be listed"); }
        finally { inventory.Remove(boot); }
    }

    // ------------------------------------------------------------------ loophole 2: only valuables are treasure

    [Test]
    public void KeepingAPartIsSilentBookkeepingAndOnlyValuablesCountAsTreasure()
    {
        var session = T("GameSession"); var inventory = (IList)session.GetField("Inventory").GetValue(null);
        var total = session.GetField("TotalTreasuresFound");
        int before = (int)total.GetValue(null), parts = (int)Val(session, "PartCount"), finds = (int)Val(session, "FindCount");
        var boot = Item("old_work_boot", "keep-test-boot"); var gnome = Item("garden_gnome", "keep-test-gnome");
        try {
            Assert.IsFalse((bool)Static("Progression", "IsTreasure", boot)); Assert.IsTrue((bool)Static("Progression", "IsTreasure", gnome)); Assert.IsFalse((bool)Static("Progression", "IsTreasure", new object[] { null }));
            Static("GameSession", "AddToInventory", boot);
            Assert.AreEqual(before, (int)total.GetValue(null), "a kept part is not a treasure find (and must not end Rookie Luck)");
            Assert.AreEqual(parts + 1, (int)Val(session, "PartCount")); Assert.AreEqual(finds, (int)Val(session, "FindCount"), "the HUD's Finds chip counts valuables only");
            Static("GameSession", "AddToInventory", gnome);
            Assert.AreEqual(before + 1, (int)total.GetValue(null)); Assert.AreEqual(finds + 1, (int)Val(session, "FindCount")); Assert.AreEqual(parts + 1, (int)Val(session, "PartCount"));
        }
        finally { inventory.Remove(boot); inventory.Remove(gnome); total.SetValue(null, before); }
        Assert.AreEqual(200, (int)session.GetField("PartsCap").GetRawConstantValue(), "the parts shelf");
        // the KEEP crate no longer has a "hoarder" path: it takes a part or hands it back only when the shelf is full
        var bin = T("SortBin");
        Assert.IsNull(bin.GetMethod("Hoarder", Flags)); Assert.NotNull(bin.GetMethod("KeepPart", Flags)); Assert.NotNull(bin.GetMethod("PartsShelfFull", Flags));
        // and the Trash-dex is still valuables only
        foreach (var def in (IEnumerable)Val(T("Progression"), "DexDefs")) Assert.AreEqual("Valuable", Category(def));
    }

    // ------------------------------------------------------------------ loophole 3: displays show valuables only

    [Test]
    public void DisplaysShowValuablesOnlyAndAPinnedFindIsNeverSpentByTheBench()
    {
        var gnome = Item("garden_gnome", "show-gnome", 0.5f, 40f); var boot = Item("old_work_boot", "show-boot"); var can = Item("soda_can", "show-can");
        var stolen = Item("gold_chain", "show-hot", 0.5f, 900f, true);
        var ranked = (IList)Static("DisplayPlanner", "Ranked", ItemList(boot, gnome, can, stolen));
        CollectionAssert.AreEqual(new[] { gnome }, ranked, "parts and hot goods never reach a shelf");
        Func<string, int> everyPiece = id => 2, noPins = key => 0;
        var plan = Static("DisplayPlanner", "Plan", ItemList(boot, can, gnome), everyPiece, noPins);
        var shown = ((IDictionary)Val(plan, "byPiece")).Values.Cast<IEnumerable>().SelectMany(slots => slots.Cast<object>()).Where(item => item != null).ToArray();
        CollectionAssert.AreEqual(new[] { gnome }, shown);
        // Arrange pins: a pinned uid is spoken for
        int tag = (int)Static("DisplayPlanner", "PinTag", "show-gnome"); string key = (string)Static("DisplayPlanner", "PinKey", "trophy_shelf", 0);
        Func<string, int> pinned = k => k == key ? tag : 0;
        Assert.IsTrue((bool)Static("DisplayPlanner", "IsPinnedUid", "show-gnome", everyPiece, pinned));
        Assert.IsFalse((bool)Static("DisplayPlanner", "IsPinnedUid", "show-boot", everyPiece, pinned)); Assert.IsFalse((bool)Static("DisplayPlanner", "IsPinnedUid", "show-gnome", everyPiece, noPins));
        Assert.IsFalse((bool)Static("DisplayPlanner", "IsPinnedUid", "show-gnome", (Func<string, int>)(id => 0), pinned), "a pin on a piece you do not own means nothing");
        Assert.IsFalse((bool)Static("DisplayPlanner", "IsPinnedUid", null, everyPiece, pinned));

        // the bench's own reasons: a find on a workshop display, or offered in a neighbour trade
        var state = New("WorkshopState"); Set(state, "completed", (ushort)1);
        Assert.IsTrue((bool)Call(state, "Pin", "bottle_cabinet", Static("WorkshopDisplayData", "From", "bottle_cabinet", gnome)));
        StringAssert.Contains("display", (string)Static("Workshop", "BlockedReason", gnome, state)); Assert.IsNull(Static("Workshop", "BlockedReason", boot, state));
        var reserved = T("EbaeMarket").GetField("IsReserved"); object previous = reserved.GetValue(null);
        try {
            reserved.SetValue(null, (Func<string, bool>)(uid => uid == "show-boot"));
            StringAssert.Contains("trade", (string)Static("Workshop", "BlockedReason", boot, state)); Assert.IsNull(Static("Workshop", "BlockedReason", can, state));
        }
        finally { reserved.SetValue(null, previous); }
    }

    // ------------------------------------------------------------------ how the bench picks instances

    [Test]
    public void TheBenchPicksExactlyTheRecipeFromFreeItemsCheapestFirstAndStolenPartsCount()
    {
        var recipe = Recipe("work_gloves", 1); // 2 old_work_boot + 1 oven_mitt
        var bootA = Item("old_work_boot", "pick-a"); var bootB = Item("old_work_boot", "pick-b", 0f, 0f, true); var bootC = Item("old_work_boot", "pick-c");
        var mitt = Item("oven_mitt", "pick-m"); var extra = Item("leather_belt", "pick-x");
        var all = ItemList(bootA, bootB, bootC, mitt, extra);
        var stock = Stock(all);
        Assert.AreEqual(3, (int)Call(stock, "Have", "old_work_boot")); Assert.AreEqual(0, (int)Call(stock, "Have", "garden_hose")); Assert.AreEqual(0, (int)Call(stock, "Blocked", "old_work_boot"));
        var picked = Select(recipe, stock);
        Assert.AreEqual(3, picked.Count, "exactly the recipe: nothing extra leaves the inventory");
        CollectionAssert.AreEquivalent(new[] { "old_work_boot", "old_work_boot", "oven_mitt" }, picked.Cast<object>().Select(i => (string)Val(i, "defId")));
        CollectionAssert.Contains(picked, bootB, "a stolen part is a part (and is spent first: only a fence would take it anyway)"); CollectionAssert.DoesNotContain(picked, extra);
        Assert.AreEqual(3, picked.Cast<object>().Select(i => Val(i, "uid")).Distinct().Count());

        // one short: nothing is picked, and the bench says what is missing
        var shortStock = Stock(ItemList(bootA, mitt));
        Assert.IsNull(Select(recipe, shortStock)); StringAssert.Contains("1 Old Work Boot", (string)Static("Workshop", "ShortText", recipe, shortStock));
        // a boot that is spoken for (listed items are not in the inventory at all; reserved and display-pinned ones are blocked)
        Func<object, string> blocked = item => (string)Val(item, "uid") == "pick-a" || (string)Val(item, "uid") == "pick-b" ? "pinned to a display" : null;
        var blockedStock = Stock(ItemList(bootA, bootB, bootC, mitt), blocked);
        Assert.AreEqual(1, (int)Call(blockedStock, "Have", "old_work_boot")); Assert.AreEqual(2, (int)Call(blockedStock, "Blocked", "old_work_boot"));
        Assert.IsNull(Select(recipe, blockedStock), "a blocked item is never consumed: the player frees it first");
        string why = (string)Static("Workshop", "ShortText", recipe, blockedStock);
        StringAssert.Contains("pinned to a display", why); StringAssert.Contains("free it first", why);

        // valuables are allowed as ingredients, and the least valuable instance goes first
        var treasure = Recipe("calling_the_fence", 1); // garden_gnome + old_flip_phone + fish_bones
        var cheap = Item("garden_gnome", "pick-g1", 0.5f, 16f); var dear = Item("garden_gnome", "pick-g2", 0.5f, 44f);
        var chosen = Select(treasure, Stock(ItemList(dear, cheap, Item("old_flip_phone", "pick-p"), Item("fish_bones", "pick-f"))));
        CollectionAssert.Contains(chosen, cheap); CollectionAssert.DoesNotContain(chosen, dear);
        // an "or" slot uses whichever the builder holds
        var itch = Recipe("calling_the_itch", 1);
        var pennies = Select(itch, Stock(ItemList(Item("jar_of_pennies", "pick-j"), Item("lone_sock", "pick-s"), Item("broken_boss_mug", "pick-u"))));
        Assert.NotNull(pennies); Assert.IsTrue(pennies.Cast<object>().Any(i => (string)Val(i, "defId") == "jar_of_pennies"));

        // a Monument load takes the lightest pieces of each sorted type and never a part the business is hunting for
        // (the whole rule set is MonumentTests; this is the same inventory the blueprints are picked from)
        var printer = New("WorkshopState"); Set(printer, "disk", 1); // tier 1: ten of each
        var pile = new List<object>(); int n = 0;
        foreach (string id in new[] { "soda_can", "water_bottle", "beer_bottle", "newspaper" })
            for (int i = 0; i < 10; i++) pile.Add(Item(id, "pile-" + n++, i / 20f));
        var hinge = Item("door_hinge", "pile-hinge"); Set(hinge, "massKg", 0.001f); pile.Add(hinge); // lighter than any can
        pile.Add(bootA); pile.Add(Item("garden_gnome", "pile-gnome", 0.5f, 30f)); // a part that is junk, and a treasure: never Monument food
        var scrapStock = Stock(ItemList(pile.ToArray()));
        var selectLoad = T("Workshop").GetMethod("SelectForMonument", Flags);
        int cap = (int)T("Workshop").GetField("MaxFeedItems", Flags).GetRawConstantValue();
        var free = (IList)selectLoad.Invoke(null, new object[] { printer, scrapStock, null, cap });
        Assert.AreEqual(40, free.Count); CollectionAssert.Contains(free, hinge, "the lightest metal goes first");
        CollectionAssert.DoesNotContain(free, bootA); Assert.IsFalse(free.Cast<object>().Any(i => Category(Val(i, "Def")) == "Valuable" || Category(Val(i, "Def")) == "Junk"));
        var hunting = (IList)selectLoad.Invoke(null, new object[] { printer, scrapStock, (Func<string, bool>)(id => id == "door_hinge"), cap });
        Assert.AreEqual(40, hunting.Count); CollectionAssert.DoesNotContain(hunting, hinge, "a wanted part is never fed to the Monument");
        pile.RemoveAt(0);
        var shortLoad = (IList)selectLoad.Invoke(null, new object[] { printer, Stock(ItemList(pile.ToArray())), (Func<string, bool>)(id => id == "door_hinge"), cap });
        Assert.AreEqual(39, shortLoad.Count, "one can short is still a load: the Monument is filled a shelf at a time");
        Assert.AreEqual(10, (int)T("Workshop").GetNestedType("Stock").GetMethod("HaveOf")
            .Invoke(Stock(ItemList(pile.ToArray())), new object[] { Enum.Parse(T("WorkshopMaterial"), "Metal"), null }), "the hinge counts as metal when nobody is hunting for it");
        Assert.AreEqual(9, (int)T("Workshop").GetNestedType("Stock").GetMethod("HaveOf")
            .Invoke(Stock(ItemList(pile.ToArray())), new object[] { Enum.Parse(T("WorkshopMaterial"), "Metal"), (Func<string, bool>)(id => id == "door_hinge") }), "and does not when somebody is");
        // the bench's sculpture grid picker went with the bench
        Assert.IsNull(T("Workshop").GetMethod("SelectFor", Flags, null, new[] { T("WorkshopRecipe"), T("Workshop").GetNestedType("Stock"), typeof(Func<string, bool>) }, null));
    }

    [Test]
    public void AnOfferSurvivesTheWireWithEverythingTheHostChecks()
    {
        var items = ItemList(Item("old_work_boot", "wire-1", 0.4f), Item("gold_chain", "wire-2", 0.5f, 912.5f, true), Item("hairpins", "burglary/mm-12/d4-0", 1f), Item("soda_can", "wire-4"));
        Set(items[0], "condition", 0.4567f);
        string wire = (string)Static("Workshop", "EncodeOffer", items); Assert.IsNotEmpty(wire);
        var args = new object[] { wire, null }; Assert.IsTrue((bool)T("Workshop").GetMethod("TryDecodeOffer", Flags).Invoke(null, args));
        var back = (IList)args[1]; Assert.AreEqual(items.Count, back.Count);
        for (int i = 0; i < items.Count; i++) {
            Assert.AreEqual(Val(items[i], "uid"), Val(back[i], "uid")); Assert.AreEqual(Val(items[i], "defId"), Val(back[i], "defId")); Assert.AreEqual(Val(items[i], "hot"), Val(back[i], "hot"));
            Assert.AreEqual((float)Val(items[i], "massKg"), (float)Val(back[i], "massKg"), 0.0006f); Assert.AreEqual((float)Val(items[i], "value"), (float)Val(back[i], "value"), 0.006f);
            Assert.AreEqual((float)Val(items[i], "condition"), (float)Val(back[i], "condition"), 0.0006f);
            var plausible = new object[] { back[i], null }; Assert.IsTrue((bool)T("EbaeItemValidation").GetMethod("IsPlausible").Invoke(null, plausible), "what arrives still passes the host's catalog check");
        }
        // the biggest request the game can make (a full Monument load: MaxFeedItems items, every one at its longest) fits one message
        // (MonumentTests round-trips such a load; this pins the room that is left)
        int feed = (int)T("Workshop").GetField("MaxFeedItems", Flags).GetRawConstantValue(); var big = new List<object>();
        for (int i = 0; i < feed; i++) big.Add(Item("fire_extinguisher", (0x0123456789abL + i).ToString("x12"), 1f, 0f, true));
        string monument = (string)Static("Workshop", "EncodeOffer", ItemList(big.ToArray()));
        Assert.NotNull(monument); Assert.Less(monument.Length, (int)T("Workshop").GetField("MaxOfferChars", Flags).GetRawConstantValue() - 600);
        Assert.LessOrEqual(feed, (int)T("Workshop").GetField("MaxOfferItems", Flags).GetRawConstantValue());
        // garbage is refused, never guessed at; an empty offer is an empty list (the rules then say why the build fails)
        foreach (string bad in new[] { "nonsense", "a\u001f1\u001f2\u001f3\u001f4", "a\u001fzz\u001f100\u001f1000\u001f0\u001f0", "a\u001f999999\u001f100\u001f1000\u001f0\u001f0", "a\u001f1\u001f-5\u001f1000\u001f0\u001f0", "a\u001f1\u001f100\u001f1000\u001f0\u001f2", new string('x', 5000) }) {
            args = new object[] { bad, null }; Assert.IsFalse((bool)T("Workshop").GetMethod("TryDecodeOffer", Flags).Invoke(null, args), bad.Length > 40 ? "too long" : bad);
        }
        args = new object[] { "", null }; Assert.IsTrue((bool)T("Workshop").GetMethod("TryDecodeOffer", Flags).Invoke(null, args)); Assert.AreEqual(0, ((IList)args[1]).Count);
        Assert.IsNull(Static("Workshop", "EncodeOffer", ItemList(Item("old_work_boot", "bad\u001fuid"))), "a uid that would break the framing is never sent");
        // catalog numbers are 1-based positions and round-trip
        int number = (int)Static("Workshop", "CatalogNumber", "old_work_boot"); Assert.Greater(number, 0); Assert.AreSame(Def("old_work_boot"), Static("Workshop", "DefOfNumber", number));
        Assert.AreEqual(0, (int)Static("Workshop", "CatalogNumber", "made_up")); Assert.IsNull(Static("Workshop", "DefOfNumber", 0)); Assert.IsNull(Static("Workshop", "DefOfNumber", 100000));
        Assert.AreEqual(1, (int)Static("Workshop", "CatalogNumber", Val(Catalog[0], "id")));
    }

    // ------------------------------------------------------------------ decor on Scrapazon

    [Test]
    public void ElevenDecorPiecesAreSoldOnScrapazonAtTheirCatalogPriceAndTheRetiredSculpturesAreNot()
    {
        var departments = ((Array)T("ScrapazonCatalog").GetField("Departments").GetValue(null)).Cast<object>().Select(d => d.ToString()).ToArray();
        Assert.IsTrue(departments.Any(d => d.Contains("decor") && d.Contains("Decor")), "the Decor aisle is in the shop's nav");
        var sold = ((IEnumerable)Val(T("ScrapazonCatalog"), "All")).Cast<object>().Where(item => ((string)Val(item, "id")).StartsWith("decor_")).ToArray();
        Assert.AreEqual(11, sold.Length);
        foreach (var recipe in ((IEnumerable)Val(T("WorkshopCatalog"), "All")).Cast<object>()) {
            string id = (string)Val(recipe, "id");
            if ((bool)Val(recipe, "retired")) { Assert.IsNull(Static("ScrapazonCatalog", "Get", "decor_" + id), id + " is retired (it became a Monument tier), never sold"); continue; }
            var item = Static("ScrapazonCatalog", "Get", (string)Val(recipe, "upgradeId")); Assert.NotNull(item, id + " must be on Scrapazon");
            CollectionAssert.AreEqual(new[] { (float)(int)Val(recipe, "price") }, (float[])Val(item, "prices"), id + ": one price, the catalog's");
            Assert.AreEqual(1, (int)Val(item, "maxLevel")); Assert.IsTrue((bool)Val(item, "IsUpgrade")); Assert.IsTrue((bool)Call(item, "HasTag", "decor"));
            Assert.AreEqual("cosmetics", Val(item, "dept"), "an optional sink: never a 'best next buy'"); Assert.AreEqual(1, (int)Call(item, "RankFor", 1), "no rank gate, like the bench before it");
            StringAssert.DoesNotContain("workshop", ((string)Call(item, "EffectFor", 1)).ToLowerInvariant(), id + ": there is no workshop room to install it in any more");
            Assert.IsNotEmpty((string)Val(item, "title")); Assert.IsNotEmpty((string)Call(item, "EffectFor", 1)); Assert.Greater(((Array)Val(item, "reviews")).Length, 0, id + " deserves a review");
            Assert.AreSame(recipe, Static("WorkshopCatalog", "DecorFor", Val(item, "id")));
        }
        // adding the aisle twice changes nothing
        var copy = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ScrapazonItem")));
        Static("WorkshopDecorShop", "AddTo", copy); int once = copy.Count; Static("WorkshopDecorShop", "AddTo", copy); Assert.AreEqual(11, once); Assert.AreEqual(once, copy.Count);
        CollectionAssert.IsOrdered(copy.Cast<object>().Select(item => ((float[])Val(item, "prices"))[0]).ToArray(), "cheapest first on the shelf");
    }

    // ------------------------------------------------------------------ wanted parts

    [Test]
    public void WantedPartsReachTheHudTheXRayPlateAndTheEbayTag()
    {
        var session = T("GameSession"); var workshopField = session.GetField("workshop", Flags); object previous = workshopField.GetValue(null);
        try {
            var state = New("WorkshopState"); int boot = (int)Static("Workshop", "CatalogNumber", "old_work_boot");
            Assert.IsTrue((bool)Call(state, "SetPin", boot, true, null));
            workshopField.SetValue(null, state); Static("GameSession", "RebuildWanted");
            Assert.IsTrue((bool)T("Workshop").GetMethod("IsWanted", new[] { typeof(string) }).Invoke(null, new object[] { "old_work_boot" }));
            Assert.IsFalse((bool)T("Workshop").GetMethod("IsWanted", new[] { typeof(string) }).Invoke(null, new object[] { "oven_mitt" }));
            Assert.IsFalse((bool)T("Workshop").GetMethod("IsWanted", new[] { typeof(string) }).Invoke(null, new object[] { "made_up" }));
            Assert.AreEqual(1, (int)Val(session, "WorkshopWantedCount"));
            // "needed for": the nodes this business could build next that take the part (locked ones are not nagged about)
            string needed = (string)Static("Workshop", "NeededFor", "old_work_boot", state);
            StringAssert.Contains("Work Gloves", needed); StringAssert.Contains("Work Boots", needed); StringAssert.DoesNotContain("Rocket Blades", needed);
            Assert.AreEqual("", Static("Workshop", "NeededFor", "banana_peel", state)); Assert.AreEqual("", Static("Workshop", "NeededFor", "old_work_boot", null));
            Call(state, "SetCallingLevel", Val(Static("CallingCatalog", "Get", "work_gloves"), "index"), 1);
            StringAssert.Contains("Rocket Blades", (string)Static("Workshop", "NeededFor", "old_work_boot", state), "unlocked by the trunk");
            StringAssert.DoesNotContain("Work Gloves", (string)Static("Workshop", "NeededFor", "old_work_boot", state), "gloves level 2 takes other parts");
        }
        finally { workshopField.SetValue(null, previous); Static("GameSession", "RebuildWanted"); }
        // the X-ray plate's flag rides in the one spare bit of the six 4-bit counts and never disturbs them
        var summary = New("XRaySummary"); uint bit = (uint)T("XRaySummary").GetField("WantedBit").GetRawConstantValue();
        Assert.AreEqual(1u << 31, bit); Assert.IsFalse((bool)Val(summary, "wanted"));
        Set(summary, "counts", 0x00FEDCBAu | bit); Assert.IsTrue((bool)Val(summary, "wanted"));
        int[] counts = Enumerable.Range(0, 6).Select(cat => (int)Call(summary, "Count", cat)).ToArray();
        CollectionAssert.AreEqual(new[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF }, counts); Assert.AreEqual(75, (int)Val(summary, "Total"));
        Assert.IsFalse((bool)Val(Static("ScavengerRules", "Summarize", New("BagContents"), 2), "wanted"), "only the host's Peek sets it");
    }
}

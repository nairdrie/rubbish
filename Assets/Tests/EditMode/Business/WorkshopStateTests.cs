using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// WorkshopState + WorkshopCatalog after the gram bank and the workbench were retired: decor is bought (Scrapazon) and
/// installed by a saved bit, the three bench sculptures are retired (a save's best one becomes a Monument tier, once), the
/// old bank converts to cash exactly once, and wanted-part pins ride in the replicated JSON. The Monument's own rules are
/// MonumentTests, the floppy disk's are DiskTests. Game code is reached by reflection (this assembly cannot reference
/// Assembly-CSharp).
/// </summary>
public class WorkshopStateTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Get(object value, string member)
    {
        var field = value.GetType().GetField(member, Flags); if (field != null) return field.GetValue(value);
        var property = value.GetType().GetProperty(member, Flags); Assert.NotNull(property, value.GetType().Name + "." + member);
        return property.GetValue(value);
    }
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static object Material(int index) => Enum.ToObject(T("WorkshopMaterial"), index);
    static object Materials(int metal, int plastic, int glass, int paper) => Activator.CreateInstance(T("WorkshopMaterials"), metal, plastic, glass, paper);
    static IEnumerable<object> Recipes => ((IEnumerable)T("WorkshopCatalog").GetProperty("All").GetValue(null)).Cast<object>();
    static object Recipe(string id) => Static("WorkshopCatalog", "Get", id);
    static int Amount(object materials, int material) => (int)Call(materials, "Get", Material(material));
    static int Total(object materials) => Enumerable.Range(0, 4).Sum(m => Amount(materials, m));
    static bool Completed(object state, string id) => (bool)Call(state, "Completed", id);
    static bool Valid(object state) => (bool)Call(state, "Valid");
    static bool Install(object state, string id) => (bool)Call(state, "InstallDecor", id, null);
    static ushort Bits(string name) => (ushort)T("WorkshopCatalog").GetField(name).GetRawConstantValue();
    static readonly string[] Sculptures = { "junk_sculpture", "sculpture_tower", "sculpture_monument" };
    const ushort SculptureBits = (1 << 5) | (1 << 6) | (1 << 7);

    [Test]
    public void PiecesKeepTheirSavedBitsAndSplitIntoElevenDecorAndThreeRetiredSculptures()
    {
        var expected = new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display", "business_sign", "junk_sculpture",
            "sculpture_tower", "sculpture_monument", "bag_hoop", "sorting_chute", "break_sofa", "scrap_radio", "spring_launcher", "doorbell" };
        var recipes = Recipes.ToArray(); Assert.AreEqual(14, recipes.Length);
        for (int i = 0; i < recipes.Length; i++) { Assert.AreEqual(expected[i], Get(recipes[i], "id")); Assert.AreEqual(i, Get(recipes[i], "index")); }
        // every bit a save may carry, the three that are retired, and what is left for a live state
        Assert.AreEqual((ushort)((1 << 14) - 1), Bits("AllBits")); Assert.AreEqual(SculptureBits, Bits("RetiredBits"));
        Assert.AreEqual((ushort)(Bits("AllBits") & ~SculptureBits), Bits("DecorBits")); Assert.AreEqual(16159, Bits("DecorBits"));
        Assert.AreEqual(4, recipes.Count(r => (bool)Get(r, "isDisplay")));
        var retired = recipes.Where(r => (bool)Get(r, "retired")).ToArray();
        CollectionAssert.AreEqual(Sculptures, retired.Select(r => Get(r, "id")), "the bench sculptures: indices 5, 6, 7 stay taken for ever");
        foreach (var piece in retired) {
            string id = (string)Get(piece, "id");
            Assert.IsFalse((bool)Get(piece, "isDecor"), id); Assert.IsFalse((bool)Get(piece, "isDisplay"), id);
            Assert.AreEqual(0, (int)Get(piece, "price"), id + " is not for sale"); Assert.IsNull(Get(piece, "upgradeId"), id + " is no Scrapazon product");
            Assert.AreNotEqual(0, Bits("RetiredBits") & (1 << (int)Get(piece, "index")), id); Assert.AreEqual(0, Bits("DecorBits") & (1 << (int)Get(piece, "index")), id);
            Assert.IsNotEmpty((string)Get(piece, "name")); Assert.Greater(Total(Get(piece, "legacyGrams")), 0);
        }
        // a piece is bought or it is nothing: the scrap-grid recipe went with the workbench
        foreach (string gone in new[] { "isSculpture", "grid", "Need" }) Assert.IsEmpty(T("WorkshopRecipe").GetMember(gone, Flags), "WorkshopRecipe." + gone);
        var decor = recipes.Where(r => (bool)Get(r, "isDecor")).ToArray(); Assert.AreEqual(11, decor.Length);
        foreach (var piece in decor) {
            Assert.IsFalse((bool)Get(piece, "retired")); Assert.AreNotEqual(0, Bits("DecorBits") & (1 << (int)Get(piece, "index")), (string)Get(piece, "id"));
            Assert.That((int)Get(piece, "price"), Is.InRange(250, 15000), (string)Get(piece, "id"));
            Assert.AreEqual("decor_" + Get(piece, "id"), Get(piece, "upgradeId")); Assert.AreSame(piece, Static("WorkshopCatalog", "DecorFor", Get(piece, "upgradeId")));
        }
        // a pricier piece never cost less scrap than a cheaper one did: the old ordering survives the move to cash
        var byPrice = decor.OrderBy(r => (int)Get(r, "price")).ToArray();
        for (int i = 1; i < byPrice.Length; i++)
            Assert.GreaterOrEqual(Total(Get(byPrice[i], "legacyGrams")), Total(Get(byPrice[i - 1], "legacyGrams")), Get(byPrice[i], "id") + " is priced out of order");
        Assert.AreEqual(decor.Length, decor.Select(r => Get(r, "price")).Distinct().Count(), "every decor piece has its own price");
        foreach (string bad in new[] { "decor_junk_sculpture", "decor_sculpture_tower", "decor_sculpture_monument", "decor_made_up", "decor_monument", "bottle_cabinet", "", null })
            Assert.IsNull(Static("WorkshopCatalog", "DecorFor", bad), bad);
    }

    [Test]
    public void ASavedSculptureBecomesItsMonumentTierOnceAndItsBitIsNeverValidAgain()
    {
        const ushort decor = (1 << 4) | (1 << 8) | (1 << 13); // a sign, a hoop and a doorbell either side of the sculptures' bits
        // Junk Sculpture = tier 1, Scrap Tower = tier 2, Scrap Monument = tier 3; the best one a save built is the one that counts
        foreach (var (bits, tier) in new[] { (1 << 5, 1), (1 << 6, 2), (1 << 7, 3), (1 << 5 | 1 << 6, 2), (1 << 5 | 1 << 7, 3), (1 << 6 | 1 << 7, 3), (1 << 5 | 1 << 6 | 1 << 7, 3) }) {
            string what = "sculpture bits " + bits;
            Assert.AreEqual(tier, (int)Static("WorkshopCatalog", "LegacyMonumentLevel", (ushort)bits), what);
            Assert.AreEqual(tier, (int)Static("WorkshopCatalog", "LegacyMonumentLevel", (ushort)(bits | decor)), what + " with decor beside them");
            var state = New("WorkshopState"); Set(state, "completed", (ushort)(bits | decor)); Set(state, "colorIndex", 3);
            Assert.IsFalse(Valid(state), what + ": a state with a retired bit has not been through the migration");
            Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", JsonUtility.ToJson(state)), what + " cannot be uploaded");
            Assert.IsTrue((bool)Call(state, "RetireSculptures"), what);
            Assert.AreEqual(decor, Get(state, "completed"), what + ": the sculptures' bits are cleared and the decor next to them is untouched");
            Assert.AreEqual(tier, Get(state, "monument"), what); Assert.AreEqual(0, Total(Get(state, "fed"))); Assert.AreEqual(3, Get(state, "colorIndex"));
            foreach (string id in Sculptures) Assert.IsFalse(Completed(state, id), id);
            Assert.IsTrue(Completed(state, "business_sign")); Assert.IsTrue(Completed(state, "bag_hoop")); Assert.IsTrue(Completed(state, "doorbell"));
            // a Monument needs the software that prints it: the load path's repair hands the save its disk
            Assert.IsFalse(Valid(state), what + ": a Monument without a disk"); Call(state, "NormalizePrinter");
            Assert.AreEqual(1, Get(state, "disk")); Assert.IsTrue(Valid(state), what);
            string once = JsonUtility.ToJson(state);
            Assert.IsFalse((bool)Call(state, "RetireSculptures"), what + ": converting twice finds nothing"); Assert.AreEqual(once, JsonUtility.ToJson(state));
        }
        Assert.AreEqual(0, (int)Static("WorkshopCatalog", "LegacyMonumentLevel", (ushort)0)); Assert.AreEqual(0, (int)Static("WorkshopCatalog", "LegacyMonumentLevel", Bits("DecorBits")));
        // nothing to convert: nothing changes
        var plain = New("WorkshopState"); Set(plain, "completed", decor); string untouched = JsonUtility.ToJson(plain);
        Assert.IsFalse((bool)Call(plain, "RetireSculptures")); Assert.AreEqual(untouched, JsonUtility.ToJson(plain)); Assert.AreEqual(0, Get(plain, "monument"));
        Assert.IsFalse((bool)Call(New("WorkshopState"), "RetireSculptures"));
        // raises only: a Monument that is already further along keeps its tier AND what is loaded toward the next
        var ahead = New("WorkshopState"); Set(ahead, "completed", (ushort)(1 << 6)); Set(ahead, "monument", 5); Set(ahead, "fed", Materials(7, 0, 2, 0)); Set(ahead, "disk", 1);
        Assert.IsTrue((bool)Call(ahead, "RetireSculptures"), "the bit still has to go"); Assert.AreEqual((ushort)0, Get(ahead, "completed"));
        Assert.AreEqual(5, Get(ahead, "monument")); Assert.AreEqual(9, Total(Get(ahead, "fed"))); Assert.IsTrue(Valid(ahead));
        var level = New("WorkshopState"); Set(level, "completed", (ushort)(1 << 5)); Set(level, "monument", 1); Set(level, "fed", Materials(2, 0, 0, 0)); Set(level, "disk", 1);
        Assert.IsTrue((bool)Call(level, "RetireSculptures")); Assert.AreEqual(1, Get(level, "monument")); Assert.AreEqual(2, Total(Get(level, "fed")), "the same tier: nothing printed, nothing lost");
        // a lower tier is raised, and the load that was meant for the tier it skipped is dropped (it would not fit the new one's books)
        var behind = New("WorkshopState"); Set(behind, "completed", (ushort)(1 << 7)); Set(behind, "monument", 1); Set(behind, "fed", Materials(3, 3, 3, 3)); Set(behind, "disk", 1);
        Assert.IsTrue((bool)Call(behind, "RetireSculptures")); Assert.AreEqual(3, Get(behind, "monument")); Assert.AreEqual(0, Total(Get(behind, "fed"))); Assert.IsTrue(Valid(behind));

        // and nobody can build, buy or install one ever again
        foreach (string id in Sculptures) {
            var state = New("WorkshopState"); Set(state, "disk", 1); string before = JsonUtility.ToJson(state);
            var install = new object[] { id, null };
            Assert.IsFalse((bool)state.GetType().GetMethod("InstallDecor", Flags).Invoke(state, install), id + " cannot be installed"); Assert.IsNotEmpty((string)install[1]);
            var command = new object[] { state, (byte)6, id, 0, null, null };
            Assert.IsFalse((bool)T("Workshop").GetMethod("ApplyCommand", Flags).Invoke(null, command), id + ": the install command refuses it too"); Assert.IsNotEmpty((string)command[5]);
            Assert.IsFalse((bool)Call(state, "BuildCalling", id, Enumerable.Repeat("soda_can", 30).ToList(), null), id + " is not a blueprint");
            Assert.AreEqual(before, JsonUtility.ToJson(state)); Assert.IsFalse(Completed(state, id));
            Assert.IsNull(Static("WorkshopCatalog", "DecorFor", "decor_" + id)); Assert.IsNull(Static("ScrapazonCatalog", "Get", "decor_" + id), id + " is not on Scrapazon");
            Assert.IsNull(Static("ScrapazonCatalog", "Get", id)); Assert.IsNull(Static("CallingCatalog", "Get", id));
            Assert.IsNull(Static("WorkshopModels", "Build", id, null, Color.white, null), id + " has no model any more: the Monument replaced it");
        }
        var sold = ((IEnumerable)T("ScrapazonCatalog").GetProperty("All").GetValue(null)).Cast<object>().Select(item => (string)Get(item, "id")).Where(id => id.StartsWith("decor_")).ToArray();
        Assert.AreEqual(11, sold.Length, "still exactly eleven decor products"); Assert.AreEqual(11, sold.Distinct().Count());
        CollectionAssert.AreEquivalent(Recipes.Where(r => (bool)Get(r, "isDecor")).Select(r => (string)Get(r, "upgradeId")), sold);
    }

    [Test]
    public void DecorInstallsOnceFromAPurchaseAndNeverFromScrap()
    {
        foreach (var piece in Recipes.Where(r => (bool)Get(r, "isDecor"))) {
            string id = (string)Get(piece, "id"); var state = New("WorkshopState"); Set(state, "disk", 1); string before = JsonUtility.ToJson(state);
            // no amount of kept scrap makes a decor piece: it is neither a blueprint nor a Monument tier
            Assert.IsFalse((bool)Call(state, "BuildCalling", id, Enumerable.Repeat("soda_can", 60).ToList(), null), id + " cannot be printed"); Assert.AreEqual(before, JsonUtility.ToJson(state));
            Assert.IsTrue(Install(state, id)); Assert.IsTrue(Completed(state, id)); Assert.IsTrue(Valid(state));
            before = JsonUtility.ToJson(state); Assert.IsFalse(Install(state, id), "installing twice changes nothing"); Assert.AreEqual(before, JsonUtility.ToJson(state));
            Assert.AreEqual(0, Get(state, "monument"), "decor is not the Monument");
        }
        var empty = New("WorkshopState"); Assert.IsFalse(Install(empty, "made_up")); Assert.IsFalse(Install(empty, null)); Assert.IsFalse(Install(empty, "monument")); Assert.AreEqual((ushort)0, Get(empty, "completed"));
        // every piece installed is a valid state, and exactly the decor bits
        var all = New("WorkshopState"); foreach (var piece in Recipes.Where(r => (bool)Get(r, "isDecor"))) Assert.IsTrue(Install(all, (string)Get(piece, "id")));
        Assert.AreEqual(Bits("DecorBits"), Get(all, "completed")); Assert.IsTrue(Valid(all));

        // the purchase (an upgrade level on the owner's save) and the installed bit are reconciled in both directions, raise only
        Func<string, int> owns = id => id == "decor_doorbell" || id == "decor_break_sofa" ? 1 : 0;
        var bought = New("WorkshopState"); Assert.IsTrue(Install(bought, "bag_hoop"));
        CollectionAssert.AreEquivalent(new[] { "doorbell", "break_sofa" }, (List<string>)Call(bought, "DecorToInstall", owns));
        CollectionAssert.IsEmpty((List<string>)Call(bought, "DecorToInstall", new object[] { null }));
        var raises = (List<KeyValuePair<string, int>>)Call(bought, "CallingUpgrades", owns);
        CollectionAssert.AreEqual(new[] { "decor_bag_hoop:1" }, raises.Select(r => r.Key + ":" + r.Value), "an installed piece is an owned product");
        Assert.IsEmpty((List<KeyValuePair<string, int>>)Call(bought, "CallingUpgrades", (Func<string, int>)(id => 1)));
        // a save that somehow "owns" a retired sculpture's product id gets nothing installed for it
        CollectionAssert.IsEmpty((List<string>)Call(New("WorkshopState"), "DecorToInstall", (Func<string, int>)(id => id.Contains("sculpture") ? 1 : 0)));
        // the Monument never becomes a product, whatever its tier
        var printed = New("WorkshopState"); Set(printed, "disk", 1); Set(printed, "monument", 10);
        Assert.IsEmpty((List<KeyValuePair<string, int>>)Call(printed, "CallingUpgrades", (Func<string, int>)(id => 0)));
    }

    [Test]
    public void TheRetiredBankConvertsToCashOnceExactlyAtBinRates()
    {
        // the rate IS what the bins pay per kilogram, before contracts and combos
        string[] consts = { "MetalCentsPerKg", "PlasticCentsPerKg", "GlassCentsPerKg", "PaperCentsPerKg" }; string[] categories = { "Metal", "Plastic", "Glass", "Paper" };
        for (int m = 0; m < 4; m++) {
            float rate = (float)Static("Economy", "RecycleRatePerKg", Enum.Parse(T("ItemCategory"), categories[m]));
            Assert.AreEqual(Mathf.RoundToInt(rate * 100f), (int)T("WorkshopCatalog").GetField(consts[m]).GetRawConstantValue(), categories[m]);
        }
        long Cents(int metal, int plastic, int glass, int paper) => (long)Static("WorkshopCatalog", "BankCents", Materials(metal, plastic, glass, paper));
        Assert.AreEqual(0, Cents(0, 0, 0, 0)); Assert.AreEqual(410, Cents(1000, 1000, 1000, 1000)); Assert.AreEqual(200, Cents(1000, 0, 0, 0));
        Assert.AreEqual(1, Cents(0, 0, 0, 12), "0.54 cents rounds half up"); Assert.AreEqual(0, Cents(0, 0, 0, 11)); Assert.AreEqual(0, Cents(-5000, 0, 0, 0));
        Assert.AreEqual(205000, Cents(500000, 500000, 500000, 500000), "the most two full banks could ever pay: $2,050");

        var state = New("WorkshopState");
        Set(state, "bank", Materials(12500, 3000, 2200, 7000)); Set(state, "allocated", Materials(1000, 500, 0, 0)); Set(state, "activeRecipe", "spring_launcher");
        Set(state, "completed", (ushort)0b10011); Set(state, "colorIndex", 3);
        Assert.IsFalse(Valid(state), "a state that still has a bank has not been through the migration");
        long expected = (13500L * 200 + 3500L * 110 + 2200L * 55 + 7000L * 45 + 500) / 1000; Assert.AreEqual(3521, expected);
        Assert.AreEqual(expected, (long)Call(state, "RetireBank"));
        Assert.AreEqual(0, Total(Get(state, "bank"))); Assert.AreEqual(0, Total(Get(state, "allocated"))); Assert.AreEqual("", Get(state, "activeRecipe"));
        Assert.AreEqual((ushort)0b10011, Get(state, "completed"), "built pieces stay built"); Assert.AreEqual(3, Get(state, "colorIndex")); Assert.IsTrue(Valid(state));
        Assert.AreEqual(0L, (long)Call(state, "RetireBank"), "idempotent: a second pass finds nothing");
        Assert.AreEqual(0L, (long)Call(New("WorkshopState"), "RetireBank"));
        // a corrupt number pays nothing rather than a fortune
        var corrupt = New("WorkshopState"); Set(corrupt, "bank", Materials(int.MaxValue, -7, 250001, 1000));
        Assert.AreEqual(45L, (long)Call(corrupt, "RetireBank")); Assert.IsTrue(Valid(corrupt));
    }

    [Test]
    public void LoadingAnOldSavePaysTheBankOutOnceNotesTheLetterAndKeepsEverythingBuilt()
    {
        var session = T("GameSession"); var workshopField = session.GetField("workshop", Flags);
        var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        var counters = (Dictionary<string, int>)session.GetField("counters", Flags).GetValue(null);
        object previousWorkshop = workshopField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels); var previousCounters = new Dictionary<string, int>(counters);
        string pending = (string)T("WorkshopBankRefund").GetField("PendingCounter").GetRawConstantValue();
        try {
            levels.Clear(); counters.Clear(); levels["decor_scrap_radio"] = 1; // bought on Scrapazon, not installed yet
            var old = New("WorkshopState");
            Set(old, "bank", Materials(10000, 0, 0, 2000)); Set(old, "allocated", Materials(0, 6000, 0, 0)); Set(old, "activeRecipe", "break_sofa"); Set(old, "completed", (ushort)(1 << 13));
            long refund = (long)Static("GameSession", "LoadWorkshop", old);
            Assert.AreEqual(2000 + 660 + 90, refund, "10 kg metal + 6 kg plastic + 2 kg paper at bin rates");
            Assert.AreEqual(10000, Amount(Get(old, "bank"), 0), "the caller's object is never edited");
            var loaded = workshopField.GetValue(null);
            Assert.IsTrue(Valid(loaded)); Assert.AreEqual(0, Total(Get(loaded, "bank"))); Assert.AreEqual("", Get(loaded, "activeRecipe"));
            Assert.IsTrue(Completed(loaded, "doorbell"), "what was built stays built"); Assert.IsFalse(Completed(loaded, "break_sofa"), "the unfinished project is cancelled, not finished");
            Assert.IsTrue(Completed(loaded, "scrap_radio"), "a purchase the save holds is installed on load");
            Assert.AreEqual(1, levels["decor_doorbell"], "an installed piece shows as owned on Scrapazon"); Assert.AreEqual(1, levels["decor_scrap_radio"]);
            Assert.AreEqual((int)refund, counters[pending], "the one-time letter is noted");
            // loading what was just loaded (the next save) pays nothing more and leaves the note alone
            counters.Remove(pending);
            Assert.AreEqual(0L, (long)Static("GameSession", "LoadWorkshop", loaded)); Assert.IsFalse(counters.ContainsKey(pending));
            // a brand-new business and a save with no workshop at all pay nothing
            Assert.AreEqual(0L, (long)Static("GameSession", "LoadWorkshop", new object[] { null })); Assert.AreEqual(0L, (long)Static("GameSession", "LoadWorkshop", New("WorkshopState")));
            Assert.IsFalse(counters.ContainsKey(pending));
            // a workshop that cannot be kept (a corrupt colour) still pays its bank before it is reset
            var broken = New("WorkshopState"); Set(broken, "bank", Materials(0, 0, 2000, 0)); Set(broken, "colorIndex", 99);
            Assert.AreEqual(110L, (long)Static("GameSession", "LoadWorkshop", broken)); Assert.AreEqual(0, Get(workshopField.GetValue(null), "colorIndex"));
            // the letter says the amount, and says the bench takes kept items now
            var args = new object[] { 2750L, null, null }; T("WorkshopBankRefund").GetMethod("Letter").Invoke(null, args);
            Assert.IsNotEmpty((string)args[1]); StringAssert.Contains("$27.50", (string)args[2]); StringAssert.Contains("KEEP", (string)args[2]);
        }
        finally {
            workshopField.SetValue(null, previousWorkshop); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value;
            counters.Clear(); foreach (var kv in previousCounters) counters[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted");
        }
        // This test never saves, switches profiles or raises game events: LoadWorkshop is the silent load path.
    }

    [Test]
    public void LoadingASaveWithABenchSculptureStandsItOutsideAsAMonumentTierWithItsPrinter()
    {
        var session = T("GameSession"); var workshopField = session.GetField("workshop", Flags);
        var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        object previousWorkshop = workshopField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels);
        try {
            levels.Clear();
            var old = New("WorkshopState"); Set(old, "completed", (ushort)((1 << 5) | (1 << 6) | (1 << 13))); // a Junk Sculpture, a Scrap Tower and a doorbell
            Assert.AreEqual(0L, (long)Static("GameSession", "LoadWorkshop", old), "no bank, no refund");
            Assert.AreEqual((ushort)((1 << 5) | (1 << 6) | (1 << 13)), Get(old, "completed"), "the caller's object is never edited");
            var loaded = workshopField.GetValue(null);
            Assert.IsTrue(Valid(loaded)); Assert.AreEqual((ushort)(1 << 13), Get(loaded, "completed"), "the sculptures' bits are gone, the doorbell stays");
            Assert.AreEqual(2, Get(loaded, "monument"), "the Scrap Tower was the best one: tier 2"); Assert.AreEqual(0, Total(Get(loaded, "fed")));
            Assert.AreEqual(1, Get(loaded, "disk"), "a save that has a Monument has the software that prints the next tier");
            Assert.AreEqual(2, (int)session.GetProperty("WorkshopMonument").GetValue(null)); Assert.IsTrue((bool)session.GetProperty("WorkshopDisk").GetValue(null));
            Assert.AreEqual(1, levels["decor_doorbell"]); Assert.IsFalse(levels.Keys.Any(id => id.Contains("sculpture") || id.Contains("monument")), "a Monument is not a Scrapazon product");
            // the next load of what was just loaded changes nothing
            string once = JsonUtility.ToJson(loaded);
            Assert.AreEqual(0L, (long)Static("GameSession", "LoadWorkshop", loaded)); Assert.AreEqual(once, JsonUtility.ToJson(workshopField.GetValue(null)));
            // a save that already printed further keeps its tier and its load; the old bit just goes
            levels.Clear();
            var ahead = New("WorkshopState"); Set(ahead, "completed", (ushort)(1 << 7)); Set(ahead, "monument", 6); Set(ahead, "fed", Materials(40, 0, 0, 12)); Set(ahead, "disk", 1);
            Static("GameSession", "LoadWorkshop", ahead); loaded = workshopField.GetValue(null);
            Assert.AreEqual((ushort)0, Get(loaded, "completed")); Assert.AreEqual(6, Get(loaded, "monument")); Assert.AreEqual(52, Total(Get(loaded, "fed"))); Assert.IsTrue(Valid(loaded));
            // a brand-new business has neither
            Static("GameSession", "LoadWorkshop", new object[] { null }); loaded = workshopField.GetValue(null);
            Assert.AreEqual(0, Get(loaded, "monument")); Assert.AreEqual(0, Get(loaded, "disk")); Assert.AreEqual(0, (int)session.GetProperty("WorkshopMonument").GetValue(null));
        }
        finally { workshopField.SetValue(null, previousWorkshop); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted"); }
        // This test never saves, switches profiles or raises game events: LoadWorkshop is the silent load path.
    }

    [Test]
    public void InvalidSaveStateCannotUploadBanksUnknownOrRetiredPiecesBadColoursBadPinsOrImpossiblePrints()
    {
        foreach (string json in new[] {
            "{\"activeRecipe\":\"made_up\",\"displays\":[]}", "{\"activeRecipe\":\"bottle_cabinet\",\"displays\":[]}",
            "{\"completed\":32768,\"displays\":[]}", "{\"completed\":16384,\"displays\":[]}",
            // the three retired sculpture bits, alone or among decor
            "{\"completed\":32,\"displays\":[]}", "{\"completed\":64,\"displays\":[]}", "{\"completed\":128,\"displays\":[]}", "{\"completed\":16383,\"colorIndex\":7,\"displays\":[]}",
            // a Monument or a blueprint without the disk, tiers and loads that cannot be, broken disk counters
            "{\"displays\":[],\"monument\":1}", "{\"displays\":[],\"callings\":[1]}", "{\"displays\":[],\"disk\":1,\"monument\":11}", "{\"displays\":[],\"disk\":1,\"monument\":-1}",
            "{\"displays\":[],\"disk\":1,\"fed\":{\"metal\":11}}", "{\"displays\":[],\"disk\":1,\"fed\":{\"paper\":-1}}",
            "{\"displays\":[],\"disk\":1,\"fed\":{\"metal\":10,\"plastic\":10,\"glass\":10,\"paper\":10}}", "{\"displays\":[],\"disk\":1,\"monument\":10,\"fed\":{\"glass\":1}}",
            "{\"displays\":[],\"disk\":2}", "{\"displays\":[],\"disk\":-1}", "{\"displays\":[],\"diskBags\":11}", "{\"displays\":[],\"diskBags\":-1}", "{\"displays\":[],\"diskDrops\":-1}",
            "{\"bank\":{\"metal\":1},\"displays\":[]}", "{\"bank\":{\"metal\":-1},\"displays\":[]}", "{\"allocated\":{\"glass\":5},\"displays\":[]}",
            "{\"colorIndex\":8,\"displays\":[]}", "{\"displays\":[],\"pins\":\"1,1\"}", "{\"displays\":[],\"pins\":\"0\"}", "{\"displays\":[],\"pins\":\"01\"}",
            "{\"displays\":[],\"pins\":\"A1\"}", "{\"displays\":[],\"pins\":\"zzzz\"}", "{\"displays\":[],\"pins\":\"1,,2\"}", "{\"displays\":[],\"pins\":\"1;2\"}",
            "{\"displays\":[],\"pins\":\"" + string.Join(",", Enumerable.Range(1, 25)) + "\"}", "not json" })
            Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", json), json);
        foreach (string json in new[] { "{\"displays\":[]}", "{\"completed\":16159,\"colorIndex\":7,\"displays\":[]}", "{\"displays\":[],\"pins\":\"1,2z,zzz\"}",
                     "{\"displays\":[],\"pins\":\"" + string.Join(",", Enumerable.Range(1, 24)) + "\"}",
                     "{\"displays\":[],\"disk\":1}", "{\"displays\":[],\"disk\":1,\"monument\":10}", "{\"displays\":[],\"disk\":1,\"callings\":[1]}",
                     "{\"displays\":[],\"disk\":1,\"monument\":2,\"fed\":{\"metal\":34,\"plastic\":35,\"glass\":35,\"paper\":35}}",
                     "{\"displays\":[],\"diskBags\":10,\"diskDrops\":3}" })
            Assert.NotNull(Static("BusinessRegistry", "ParseWorkshop", json), json);
    }

    [Test]
    public void WantedPinsToggleStayCompactAndRoundTrip()
    {
        int max = (int)T("WorkshopState").GetField("MaxPins").GetRawConstantValue(), maxChars = (int)T("WorkshopState").GetField("MaxPinChars").GetRawConstantValue();
        bool SetPin(object s, int number, bool on) => (bool)Call(s, "SetPin", number, on, null);
        var state = New("WorkshopState"); Assert.AreEqual("", Get(state, "pins")); Assert.AreEqual(0, Get(state, "PinCount"));
        Assert.IsFalse(SetPin(state, 0, true)); Assert.IsFalse(SetPin(state, -3, true)); Assert.IsFalse(SetPin(state, 36 * 36 * 36, true)); Assert.IsFalse(SetPin(state, 5, false), "unpinning what is not pinned changes nothing");
        Assert.IsTrue(SetPin(state, 5, true)); Assert.IsFalse(SetPin(state, 5, true)); Assert.IsTrue((bool)Call(state, "IsPinned", 5)); Assert.IsFalse((bool)Call(state, "IsPinned", 6));
        Assert.IsTrue(SetPin(state, 46655, true)); Assert.IsTrue(SetPin(state, 100, true)); Assert.AreEqual("5,zzz,2s", Get(state, "pins"), "base 36, in the order pinned");
        Assert.IsTrue(SetPin(state, 46655, false)); Assert.AreEqual("5,2s", Get(state, "pins")); Assert.IsTrue(Valid(state));
        for (int n = 200; (int)Get(state, "PinCount") < max; n++) Assert.IsTrue(SetPin(state, n, true));
        string full = (string)Get(state, "pins"); var why = new object[] { 9999, true, null };
        Assert.IsFalse((bool)state.GetType().GetMethod("SetPin", Flags).Invoke(state, why), "the list is full"); StringAssert.Contains(max.ToString(), (string)why[2]); Assert.AreEqual(full, Get(state, "pins"));
        Assert.LessOrEqual(full.Length, maxChars); Assert.IsTrue(Valid(state));
        // survives the wire byte for byte, a clone is independent, and a broken local string is repaired without touching the rest
        string json = JsonUtility.ToJson(state); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, JsonUtility.ToJson(restored)); CollectionAssert.AreEqual((List<int>)Call(state, "PinList"), (List<int>)Call(restored, "PinList"));
        var copy = Call(state, "Clone"); Assert.IsTrue(SetPin(copy, 5, false)); Assert.IsTrue((bool)Call(state, "IsPinned", 5));
        var broken = New("WorkshopState"); Set(broken, "pins", "1,1,\"x\""); Set(broken, "completed", (ushort)3);
        Assert.IsFalse(Valid(broken)); Assert.IsTrue((bool)Call(broken, "ValidIgnoringCallings")); Call(broken, "NormalizeCallings");
        Assert.IsTrue(Valid(broken)); Assert.AreEqual("", Get(broken, "pins")); Assert.AreEqual((ushort)3, Get(broken, "completed"));
    }

    [Test]
    public void TheWorstCaseStateStaysWellInsideTheReplicatedJsonLimit()
    {
        // every node at its top level, every piece installed, four displays with the longest ids, a full hutch string, 24 three-character pins,
        // the Monument one piece short of its last tier and the disk counters at their longest
        var worst = New("WorkshopState");
        Set(worst, "monument", 9); Set(worst, "fed", Materials(999, 1000, 1000, 1000)); Set(worst, "disk", 1); Set(worst, "diskBags", 10); Set(worst, "diskDrops", 9999);
        foreach (var node in ((IEnumerable)T("CallingCatalog").GetProperty("All").GetValue(null)).Cast<object>()) Call(worst, "SetCallingLevel", Get(node, "index"), Get(node, "maxLevel"));
        Set(worst, "completed", (ushort)((1 << 14) - 1)); Set(worst, "colorIndex", 7);
        Set(worst, "hutch", new string('8', (int)T("WorkshopState").GetField("MaxHutchChars").GetRawConstantValue()));
        for (int n = 46655; (int)Get(worst, "PinCount") < (int)T("WorkshopState").GetField("MaxPins").GetRawConstantValue(); n--) Assert.IsTrue((bool)Call(worst, "SetPin", n, true, null));
        var displays = (IList)Get(worst, "displays");
        foreach (string recipe in new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" }) {
            var pin = New("WorkshopDisplayData");
            Set(pin, "recipeId", recipe); Set(pin, "uid", new string('u', 64)); Set(pin, "defId", new string('d', 64)); Set(pin, "massKg", 99.999f); Set(pin, "condition", 0.123456f);
            displays.Add(pin);
        }
        int length = JsonUtility.ToJson(worst).Length;
        Assert.Less(length, 2800, "the replicated workshop JSON is capped at 4000 characters; a full tree, hutch and pin list must leave room");
        var printer = New("WorkshopState"); Set(printer, "monument", 9); Set(printer, "fed", Materials(999, 1000, 1000, 1000)); Set(printer, "disk", 1); Set(printer, "diskBags", 10); Set(printer, "diskDrops", 9999);
        Assert.Less(JsonUtility.ToJson(printer).Length - JsonUtility.ToJson(New("WorkshopState")).Length, 30, "the Monument and the disk cost the wire a couple of dozen characters at most");
        var withPins = New("WorkshopState"); for (int n = 46655; (int)Get(withPins, "PinCount") < 24; n--) Call(withPins, "SetPin", n, true, null);
        Assert.Less(JsonUtility.ToJson(withPins).Length - JsonUtility.ToJson(New("WorkshopState")).Length, 100, "24 pins cost the wire under 100 characters");
    }

    [Test]
    public void StateRoundTripRetainsInstalledPiecesAndCloneIsIndependent()
    {
        var state = New("WorkshopState"); Set(state, "completed", (ushort)(1 << 4)); Set(state, "colorIndex", 5);
        Assert.IsTrue(Install(state, "grand_display")); Assert.IsTrue((bool)Call(state, "SetPin", 42, true, null));
        Set(state, "disk", 1); Set(state, "monument", 2); Set(state, "fed", Materials(3, 0, 1, 34)); Set(state, "diskDrops", 2);
        string json = JsonUtility.ToJson(state); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, JsonUtility.ToJson(restored));
        var copy = Call(state, "Clone"); Assert.AreEqual(json, JsonUtility.ToJson(copy), "a clone carries every field, the Monument and the disk included");
        Assert.IsTrue(Install(copy, "doorbell")); Set(copy, "monument", 3); Set(copy, "fed", Materials(0, 0, 0, 0));
        Assert.AreEqual(json, JsonUtility.ToJson(state)); Assert.AreNotEqual(json, JsonUtility.ToJson(copy));
        Assert.IsTrue(Completed(restored, "business_sign")); Assert.IsTrue(Completed(restored, "grand_display")); Assert.IsFalse(Completed(restored, "doorbell"));
        Assert.AreEqual(2, Get(restored, "monument")); Assert.AreEqual(38, Total(Get(restored, "fed"))); Assert.AreEqual(1, Get(restored, "disk")); Assert.AreEqual(2, Get(restored, "diskDrops"));
    }

    [Test]
    public void DisplayPinsMoveBetweenSlotsNeverCreateValuableInventoryOrMonetaryCopies()
    {
        var state = New("WorkshopState"); Set(state, "completed", (ushort)15);
        var defs = ((IEnumerable)T("ItemCatalog").GetProperty("All").GetValue(null)).Cast<object>();
        var def = defs.First(d => Get(d, "category").ToString() == "Valuable");
        var item = New("ItemInstance"); Set(item, "uid", "test_owned"); Set(item, "defId", Get(def, "id")); Set(item, "massKg", 1f); Set(item, "value", 999f);
        var pin = Static("WorkshopDisplayData", "From", "bottle_cabinet", item);
        Assert.IsTrue((bool)Call(state, "Pin", "bottle_cabinet", pin));
        var preview = Call(state, "Display", "bottle_cabinet"); Assert.AreEqual(0f, Get(preview, "value")); Assert.AreEqual(999f, Get(item, "value"));
        var moved = Static("WorkshopDisplayData", "From", "wall_display", item);
        Assert.IsTrue((bool)Call(state, "Pin", "wall_display", moved)); Assert.IsNull(Call(state, "Display", "bottle_cabinet"));
        Assert.AreEqual(1, ((IList)Get(state, "displays")).Count); Assert.IsTrue(Valid(state));
        var clone = Call(state, "Clone"); Set(((IList)Get(clone, "displays"))[0], "uid", "changed_copy");
        Assert.AreEqual("test_owned", Get(Call(state, "Display", "wall_display"), "uid"));
        // a kept PART is never a display piece
        var part = New("ItemInstance"); Set(part, "uid", "test_part"); Set(part, "defId", "old_work_boot"); Set(part, "massKg", 0.8f);
        Assert.IsFalse((bool)Call(Static("WorkshopDisplayData", "From", "bottle_cabinet", part), "Valid"));
    }

    [Test]
    public void BusinessSaveDtoIncludesWorkshopAndOldSaveDefaultsRemainEmpty()
    {
        var dto = T("GameSession").GetNestedType("SaveData", BindingFlags.NonPublic);
        Assert.AreEqual(T("WorkshopState"), dto.GetField("workshop").FieldType);
        var legacy = JsonUtility.FromJson("{\"day\":3,\"cash\":10}", dto);
        // JsonUtility may instantiate omitted nested objects. Loading either that default or null must clear a
        // previously selected business's workshop rather than leak its progress into this legacy business.
        var session = T("GameSession"); var savedField = session.GetField("workshop", Flags);
        var levels = (Dictionary<string, int>)session.GetField("upgradeLevels", Flags).GetValue(null);
        object previous = savedField.GetValue(null); var previousLevels = new Dictionary<string, int>(levels);
        try {
            levels.Clear();
            var priorBusiness = New("WorkshopState"); Assert.IsTrue(Install(priorBusiness, "doorbell")); Assert.IsTrue((bool)Call(priorBusiness, "SetPin", 7, true, null));
            Static("GameSession", "LoadWorkshop", priorBusiness); Assert.IsTrue((bool)Static("GameSession", "WorkshopWants", 7));
            levels.Clear();
            Static("GameSession", "LoadWorkshop", dto.GetField("workshop").GetValue(legacy));
            var empty = savedField.GetValue(null);
            Assert.IsTrue(Valid(empty)); Assert.AreEqual((ushort)0, Get(empty, "completed")); Assert.AreEqual("", Get(empty, "pins"));
            Assert.IsFalse((bool)Static("GameSession", "WorkshopWants", 7), "the previous business's hunt list is gone too"); Assert.IsEmpty(levels);
        }
        finally { savedField.SetValue(null, previous); levels.Clear(); foreach (var kv in previousLevels) levels[kv.Key] = kv.Value; Static("GameSession", "RebuildWanted"); }
        // This test never saves, resets profiles or raises game events.
        var state = New("WorkshopState"); Assert.IsTrue(Install(state, "break_sofa")); Assert.IsTrue((bool)Call(state, "SetPin", 99, true, null));
        dto.GetField("workshop").SetValue(legacy, state);
        var restored = dto.GetField("workshop").GetValue(JsonUtility.FromJson(JsonUtility.ToJson(legacy), dto));
        Assert.IsTrue(Completed(restored, "break_sofa")); Assert.IsTrue((bool)Call(restored, "IsPinned", 99));
        // a save written before the bank was retired still parses with its bank intact, so the load migration can pay it out
        var old = dto.GetField("workshop").GetValue(JsonUtility.FromJson("{\"workshop\":{\"bank\":{\"metal\":4231,\"paper\":10},\"activeRecipe\":\"doorbell\",\"displays\":[]}}", dto));
        Assert.AreEqual(4231, Amount(Get(old, "bank"), 0)); Assert.AreEqual(847L, (long)Call(old, "RetireBank"));
    }

    [Test]
    public void GuestSaveUploadIsOwnAcceptedBusinessOnlyAndCannotReplayOverLiveProgress()
    {
        var business = New("BusinessInfo");
        var idType = business.GetType().GetField("id").FieldType;
        Set(business, "id", Activator.CreateInstance(idType, new object[] { "workshop_test_business" }));
        Set(business, "ownerClientId", 42UL);
        Assert.IsTrue((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", false));
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", true), "Duplicate/replayed owner upload must not replace live state.");
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 43UL, "workshop_test_business", false), "A worker cannot upload their employer's save.");
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "different_business", false));
        Set(business, "flags", (byte)1);
        Assert.IsFalse((bool)Static("BusinessRegistry", "MayInitializeWorkshop", business, 42UL, "workshop_test_business", false), "Host imports directly, never from a guest packet.");
    }
}

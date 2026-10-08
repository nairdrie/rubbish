// Standalone deterministic probe against the ACTUAL WorkshopCatalog.cs, WorkshopState.cs, CallingCatalog.cs,
// CallingRecipes.cs and MonumentCatalog.cs (compile this file with those five and nothing else: they must stay free of
// UnityEngine). No Unity, editor, scenes, saves, networking or player files are accessed.
//
// Build and run (Unity's own Roslyn and .NET runtime; there is no system dotnet). Output goes to a scratch folder, never
// into the project:
//   U=/Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/Resources/Scripting
//   FW=$U/NetCoreRuntime/shared/Microsoft.NETCore.App/6.0.21; W=Assets/Scripts/Workshop; OUT=<scratch folder>
//   $U/NetCoreRuntime/dotnet $U/DotNetSdkRoslyn/csc.dll -nologo -noconfig -nostdlib+ -target:exe -langversion:9.0 \
//     -out:$OUT/workshop_state_probe.dll $(for f in $FW/System*.dll $FW/mscorlib.dll $FW/netstandard.dll; do printf -- '-r:%s ' $f; done) \
//     Tools/workshop_state_probe.cs $W/WorkshopCatalog.cs $W/WorkshopState.cs $W/CallingCatalog.cs $W/CallingRecipes.cs $W/MonumentCatalog.cs
//   echo '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"LatestMinor"}}' \
//     > $OUT/workshop_state_probe.runtimeconfig.json
//   $U/NetCoreRuntime/dotnet $OUT/workshop_state_probe.dll        (prints one PASS line, or throws on the first broken rule)
//
// Item conservation: every kept item a builder ever held is still held, or was consumed by exactly one printed blueprint
// level or loaded into the Monument (a printed tier, or the load toward the next one). Nothing prints without the
// SolidWaste 3D disk, which a business's bags hide (never before the third, always within the first ten, a replacement
// within three). A refused print changes nothing. The retired gram bank pays out once, exactly; a retired bench
// sculpture becomes its Monument tier once.
using System;
using System.Collections.Generic;
using System.Linq;

public enum ItemCategory { Junk, Plastic, Glass, Metal, Paper, Hazard, Valuable }
public class ItemDef { public string id; public ItemCategory category; }
public class ItemInstance {
    public string uid, defId; public float massKg, condition, value;
    public ItemDef Def => ItemCatalog.Get(defId);
}
/// <summary>Stub: an id that names a category IS an item of that category ("Metal"); any other id is junk.</summary>
public static class ItemCatalog {
    public static ItemDef Get(string id) => string.IsNullOrEmpty(id) ? null :
        new ItemDef { id = id, category = Enum.TryParse(id, out ItemCategory category) ? category : ItemCategory.Junk };
}
static class WorkshopStateProbe
{
    static void Check(bool okay, string reason) { if (!okay) throw new Exception(reason); }
    static string Json(WorkshopState s) => s.completed + "|" + s.colorIndex + "|" + string.Join(",", s.callings) + "|" + s.pins + "|" + s.hutch + "|" +
        s.bank.Total + "|" + s.allocated.Total + "|" + s.activeRecipe + "|" + s.monument + "|" + s.fed.metal + "," + s.fed.plastic + "," + s.fed.glass + "," + s.fed.paper + "|" +
        s.disk + "|" + s.diskBags + "|" + s.diskDrops;
    static readonly string[] Materials = { "Metal", "Plastic", "Glass", "Paper" };
    static readonly int[] Each = { 10, 20, 35, 60, 100, 160, 250, 400, 650, 1000 };
    static List<string> Load(int metal, int plastic, int glass, int paper)
    {
        var list = new List<string>();
        for (int i = 0; i < metal; i++) list.Add("Metal"); for (int i = 0; i < plastic; i++) list.Add("Plastic");
        for (int i = 0; i < glass; i++) list.Add("Glass"); for (int i = 0; i < paper; i++) list.Add("Paper");
        return list;
    }
    static List<string> Load(WorkshopMaterials m) => Load(m.metal, m.plastic, m.glass, m.paper);
    static WorkshopState Printer(int tier = 0) => new WorkshopState { disk = 1, monument = tier };
    static void Refused(WorkshopState state, List<string> offer, string what, string exactly = null)
    {
        string before = Json(state);
        Check(!state.FeedMonument(offer, out bool printed, out string why) && !printed && !string.IsNullOrEmpty(why) && Json(state) == before, what + " must refuse and change nothing");
        Check(exactly == null || why == exactly, what + " must say: " + exactly);
    }
    /// <summary>Rip bags until one has the disk; its 1-based number in the window.</summary>
    static int BagWithTheDisk(WorkshopState state, Func<double> roll)
    {
        for (int bag = 1; bag <= 100; bag++) {
            int before = state.diskBags;
            if (state.BagRipped(roll(), false)) { Check(state.diskBags == 0, "a new window starts after a drop"); return bag; }
            Check(state.diskBags == before + 1, "a bag without the disk still counts");
        }
        throw new Exception("a hundred bags and no disk");
    }

    public static void Main()
    {
        int checks = 0, callingsBuilt = 0, callingsRefused = 0, tiersPrinted = 0, loads = 0, loadsRefused = 0, disksFound = 0, disksKept = 0, pinsSet = 0;
        var random = new Random(43604);

        // ---- pieces: eleven decor pieces install once from a purchase; the three bench sculptures are retired
        Check(WorkshopCatalog.All.Count == 14 && WorkshopCatalog.All.Count(r => r.retired) == 3 && WorkshopCatalog.All.Count(r => r.isDecor) == 11, "11 decor + 3 retired");
        Check(WorkshopCatalog.RetiredBits == ((1 << 5) | (1 << 6) | (1 << 7)) && WorkshopCatalog.DecorBits == (WorkshopCatalog.AllBits & ~WorkshopCatalog.RetiredBits), "the retired bits are 5, 6, 7");
        foreach (var recipe in WorkshopCatalog.All) {
            var state = Printer(); string before = Json(state);
            Check(!state.BuildCalling(recipe.id, Load(50, 50, 50, 50), out _) && Json(state) == before, "a piece is never printed from scrap");
            if (recipe.isDecor) {
                Check(recipe.price > 0 && recipe.upgradeId == "decor_" + recipe.id && WorkshopCatalog.DecorFor(recipe.upgradeId) == recipe, "decor is priced");
                Check((WorkshopCatalog.DecorBits & (1 << recipe.index)) != 0, "a decor bit");
                Check(state.InstallDecor(recipe.id, out _) && state.Completed(recipe.id) && state.Valid(), "decor installs");
                before = Json(state); Check(!state.InstallDecor(recipe.id, out _) && Json(state) == before, "decor installs once");
            } else {
                Check(recipe.price == 0 && recipe.upgradeId == null && WorkshopCatalog.DecorFor("decor_" + recipe.id) == null, "a retired sculpture is not sold");
                Check((WorkshopCatalog.RetiredBits & (1 << recipe.index)) != 0, "a retired bit");
                Check(!state.InstallDecor(recipe.id, out string why) && why.Length > 0 && Json(state) == before, "a retired sculpture cannot be installed");
            }
            checks++;
        }
        // ---- retired sculptures: the best one a save built becomes that Monument tier, once; decor bits never move
        for (int sculptures = 0; sculptures < 8; sculptures++)
            for (int trial = 0; trial < 40; trial++) {
                ushort decor = (ushort)(random.Next(1 << 14) & WorkshopCatalog.DecorBits);
                int had = random.Next(0, MonumentCatalog.MaxLevel + 1);
                var fed = had < MonumentCatalog.MaxLevel && trial % 2 == 0 ? new WorkshopMaterials(random.Next(Each[had]), 0, random.Next(Each[had]), 0) : default;
                var state = new WorkshopState { completed = (ushort)(decor | (sculptures << 5)), monument = had, fed = fed, disk = had > 0 || fed.Total > 0 ? 1 : 0 };
                int legacy = (sculptures & 4) != 0 ? 3 : (sculptures & 2) != 0 ? 2 : (sculptures & 1) != 0 ? 1 : 0;
                Check(WorkshopCatalog.LegacyMonumentLevel(state.completed) == legacy, "the highest sculpture counts");
                Check(state.Valid() == (sculptures == 0), "a state with a retired bit is not valid");
                Check(state.RetireSculptures() == (sculptures != 0), "something to convert exactly when a retired bit is set");
                Check(state.completed == decor, "the retired bits are cleared and the decor bits untouched");
                Check(state.monument == Math.Max(had, legacy), "raise only");
                Check(legacy > had ? state.fed.Total == 0 : state.fed.Total == fed.Total, "a tier that was not raised keeps its load");
                string once = Json(state); Check(!state.RetireSculptures() && Json(state) == once, "converting twice changes nothing");
                state.NormalizePrinter(); Check(state.Valid() && (state.monument == 0 || state.disk == 1), "a converted save is valid and can keep printing");
                checks++;
            }

        // ---- the Monument: ten tiers, each exactly its cost of every sorted type, strictly more each time
        Check(MonumentCatalog.MaxLevel == 10 && MonumentCatalog.All.Count == 10 && MonumentCatalog.Tier(0) == null && MonumentCatalog.Tier(11) == null, "ten tiers");
        for (int tier = 1; tier <= 10; tier++) {
            var cost = MonumentCatalog.Cost(tier);
            Check(MonumentCatalog.Tier(tier).level == tier && MonumentCatalog.Tier(tier).each == Each[tier - 1], "tier " + tier + " costs " + Each[tier - 1] + " of each");
            Check(cost.metal == Each[tier - 1] && cost.plastic == cost.metal && cost.glass == cost.metal && cost.paper == cost.metal, "the same of every type");
            Check(tier == 1 || MonumentCatalog.Tier(tier).each > MonumentCatalog.Tier(tier - 1).each, "costs strictly increase");
        }
        Check(MonumentCatalog.Cost(10).Total == 4000 && MonumentCatalog.Cost(0).Total == 0 && MonumentCatalog.Cost(11).Total == 0, "tier 10 is 1,000 of each");
        {
            // without the disk nothing loads; with it, every refusal leaves the state as it was
            Refused(new WorkshopState(), Load(1, 1, 1, 1), "no disk", WorkshopState.NoDiskText);
            var state = Printer(); long consumed = 0;
            for (int tier = 1; tier <= 10; tier++) {
                Refused(state, null, "nothing offered"); Refused(state, new List<string>(), "an empty offer");
                Refused(state, Load(1, 1, 1, 1).Concat(new[] { "Junk" }).ToList(), "junk"); Refused(state, Load(1, 0, 0, 0).Concat(new[] { "Valuable" }).ToList(), "a valuable");
                Refused(state, Load(0, 1, 0, 0).Concat(new[] { "Hazard" }).ToList(), "a hazard"); Refused(state, new List<string> { "banana_peel" }, "an unsorted thing");
                while (state.monument < tier) {
                    var need = state.MonumentNeed();
                    for (int m = 0; m < 4; m++) {
                        var over = new WorkshopMaterials(); over.Set((WorkshopMaterial)m, need.Get((WorkshopMaterial)m) + 1);
                        Refused(state, Load(over), "one more " + Materials[m] + " than tier " + tier + " still needs");
                    }
                    var load = new WorkshopMaterials(random.Next(need.metal + 1), random.Next(need.plastic + 1), random.Next(need.glass + 1), random.Next(need.paper + 1));
                    if (load.Total == 0) continue;
                    var before = state.fed; bool finishes = load.Total == need.Total;
                    Check(state.FeedMonument(Load(load), out bool printed, out string why) && why == "" && printed == finishes, "a load within the need is accepted, and prints exactly when it fills the tier");
                    consumed += load.Total; loads++;
                    Check(printed ? state.monument == tier && state.fed.Total == 0 : state.monument == tier - 1 && state.fed.Total == before.Total + load.Total, "the load is remembered until the tier prints");
                    Check(state.Valid(), "state invariant");
                    checks++;
                }
                tiersPrinted++;
                Check(consumed == Each.Take(tier).Sum(e => 4L * e), "exactly the cost of the printed tiers was consumed");
            }
            Check(state.monument == 10 && state.MonumentNeed().Total == 0, "the tenth tier ends it");
            Refused(state, Load(1, 1, 1, 1), "a finished Monument");
        }
        // ---- an impossible Monument or disk is not valid, and a local save is repaired without ever lowering a tier
        foreach (var bad in new[] {
            new WorkshopState { disk = 1, monument = -1 }, new WorkshopState { disk = 1, monument = 11 }, new WorkshopState { monument = 1 },
            new WorkshopState { disk = 1, fed = new WorkshopMaterials(-1, 0, 0, 0) }, new WorkshopState { disk = 1, fed = new WorkshopMaterials(11, 0, 0, 0) },
            new WorkshopState { disk = 1, fed = new WorkshopMaterials(10, 10, 10, 10) }, new WorkshopState { disk = 1, monument = 10, fed = new WorkshopMaterials(0, 0, 1, 0) },
            new WorkshopState { disk = 2 }, new WorkshopState { diskBags = -1 }, new WorkshopState { diskBags = WorkshopState.DiskFirstWindow + 1 }, new WorkshopState { diskDrops = -1 } }) {
            Check(!bad.Valid(), "an impossible Monument / disk state is not valid: " + Json(bad));
            int tier = bad.monument; bad.NormalizePrinter();
            Check(bad.Valid() && (tier < 0 || tier > 10 || bad.monument >= tier), "repaired, and a tier in range is never lowered"); checks++;
        }
        for (int i = 0; i < 4000; i++) {
            int tier = random.Next(0, 11);
            var state = new WorkshopState { monument = tier, disk = random.Next(2), diskBags = random.Next(-5, 30), diskDrops = random.Next(-5, 20000),
                fed = new WorkshopMaterials(random.Next(-50, 1500), random.Next(-50, 1500), random.Next(-50, 1500), random.Next(-50, 1500)) };
            state.NormalizePrinter(); string once = Json(state); state.NormalizePrinter();
            Check(state.Valid() && Json(state) == once && state.monument >= tier && state.monument <= Math.Min(10, tier + 1) && (state.monument == 0 || state.disk == 1), "NormalizePrinter repairs, raises only, idempotent");
            checks++;
        }

        // ---- the disk: never before the third bag, always within the first ten, a replacement within three
        Check(WorkshopState.DiskFirstWindow == 10 && WorkshopState.DiskEarliestBag == 3 && WorkshopState.DiskRetryWindow == 3, "the disk windows");
        Check(BagWithTheDisk(new WorkshopState(), () => 0.0) == 3 && BagWithTheDisk(new WorkshopState(), () => 0.999999) == 10 && BagWithTheDisk(new WorkshopState(), () => double.NaN) == 10, "first window: bag 3 at the earliest, bag 10 at the latest");
        Check(BagWithTheDisk(new WorkshopState { diskDrops = 1 }, () => 0.0) == 1 && BagWithTheDisk(new WorkshopState { diskDrops = 1 }, () => 0.999999) == 3, "retry window: three bags");
        var firstBag = new int[11]; var retryBag = new int[4];
        for (int business = 0; business < 16000; business++) {
            var state = new WorkshopState();
            int bag = BagWithTheDisk(state, random.NextDouble); Check(bag >= 3 && bag <= 10 && state.diskDrops == 1 && state.Valid(), "the first disk is in bags 3..10"); firstBag[bag]++;
            // lost (binned, crushed): while it lies around nothing moves; afterwards another one within three bags
            string before = Json(state);
            for (int i = 0; i < 5; i++) Check(!state.BagRipped(random.NextDouble(), true) && Json(state) == before, "one disk at a time");
            bag = BagWithTheDisk(state, random.NextDouble); Check(bag >= 1 && bag <= 3 && state.diskDrops == 2, "a replacement within three bags"); retryBag[bag]++;
            // kept: installed once, and no bag ever counts again
            Check(state.InsertDisk() && state.disk == 1 && state.diskBags == 0 && !state.InsertDisk() && state.Valid(), "kept once");
            before = Json(state);
            for (int i = 0; i < 12; i++) Check(!state.BagRipped(i % 2 == 0 ? 0.0 : random.NextDouble(), i % 3 == 0) && Json(state) == before, "nothing at all once installed");
            checks++;
        }
        for (int bag = 3; bag <= 10; bag++) Check(Math.Abs(firstBag[bag] - 2000) < 300, "every bag of the first window is equally likely (bag " + bag + ": " + firstBag[bag] + " of 16000)");
        for (int bag = 1; bag <= 3; bag++) Check(Math.Abs(retryBag[bag] - 16000 / 3) < 400, "every bag of a retry window is equally likely (bag " + bag + ": " + retryBag[bag] + ")");

        // ---- callings: nothing without the disk; with it, exactly the next level's recipe builds it, in prerequisite order
        {
            Func<string, int> plenty = id => 99, nothing = id => 0;
            foreach (var node in CallingCatalog.All) {
                var locked = new WorkshopState(); string before = Json(locked);
                Check(!locked.BuildCalling(node.id, node.RecipeOf(1).Pick(plenty), out string why) && why == WorkshopState.NoDiskText && Json(locked) == before, "no disk, no print: " + node.id);
            }
            var state = Printer(); bool progress = true;
            while (progress) {
                progress = false;
                foreach (var node in CallingCatalog.All) {
                    int level = state.CallingLevelAt(node.index); string before = Json(state);
                    var status = state.StatusOf(node, plenty);
                    if (status == CallingStatus.Maxed || status == CallingStatus.ComingSoon || status == CallingStatus.Locked) {
                        var any = node.RecipeOf(Math.Min(level + 1, node.maxLevel))?.Pick(plenty) ?? new List<string>();
                        Check(!state.BuildCalling(node.id, any, out _) && Json(state) == before, "closed or locked level refuses"); continue;
                    }
                    Check(status == CallingStatus.Ready && state.StatusOf(node, nothing) == CallingStatus.CantAfford && state.StatusOf(node) == CallingStatus.CantAfford, "status follows the parts held");
                    var recipe = state.NextRecipe(node); var exact = recipe.Pick(plenty);
                    Check(exact != null && exact.Count == recipe.total && recipe.Matches(exact) && recipe.Pick(nothing) == null, "a recipe picks exactly itself");
                    foreach (var bad in new[] { exact.Skip(1).ToList(), exact.Concat(new[] { exact[0] }).ToList(), exact.Skip(1).Concat(new[] { "not_a_part" }).ToList(), new List<string>(), null })
                        Check(!state.BuildCalling(node.id, bad, out string why) && why.Length > 0 && Json(state) == before, "an inexact offer refuses");
                    foreach (var slot in recipe.slots.Where(s => s.isChoice)) { // every "or" option fills its slot
                        foreach (string option in slot.anyOf) {
                            var swapped = recipe.Pick(id => slot.Accepts(id) ? (id == option ? 99 : 0) : 99);
                            Check(swapped != null && swapped.Contains(option) && recipe.Matches(swapped), "either side of an 'or' satisfies the slot");
                        }
                    }
                    Check(state.BuildCalling(node.id, exact, out _) && state.CallingLevelAt(node.index) == level + 1 && state.Valid(), "the exact recipe builds one level");
                    checks++; progress = true;
                }
            }
            foreach (var node in CallingCatalog.All) Check(state.CallingLevelAt(node.index) == node.implementedLevels, "every buildable level reachable: " + node.id);
            // a save that owned gear before the disk existed: seeded, then handed its disk; a brand-new one is not
            var veteran = new WorkshopState(); Check(veteran.SeedCallings(id => id == "work_gloves" ? 2 : 0) && !veteran.Valid(), "seeded gear is printed gear");
            veteran.NormalizePrinter(); Check(veteran.disk == 1 && veteran.Valid(), "a save that already owns gear has its disk");
            var rookie = new WorkshopState(); string untouched = Json(rookie);
            Check(!rookie.SeedCallings(id => 0) && Json(rookie) == untouched, "nothing to seed"); rookie.NormalizePrinter(); Check(rookie.disk == 0 && Json(rookie) == untouched, "a new business has no disk");
        }
        // ---- random sessions: every other business starts from its first bag (no disk yet), the rest already have theirs; items are conserved
        var partIds = CallingCatalog.All.SelectMany(n => Enumerable.Range(1, n.maxLevel).Select(n.RecipeOf)).Where(r => r != null)
            .SelectMany(r => r.slots).SelectMany(s => s.anyOf).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        for (int session = 0; session < 1000; session++) {
            var state = session % 2 == 0 ? new WorkshopState() : Printer(); var held = new Dictionary<string, int>(); long gained = 0, consumed = 0; bool diskOut = false;
            int Have(string id) => held.TryGetValue(id, out int n) ? n : 0;
            void Take(List<string> ids) { foreach (string id in ids) { held[id]--; consumed++; } }
            for (int action = 0; action < 200; action++) {
                int operation = random.Next(12); string before = Json(state);
                if (operation <= 3) { // keep a few parts (plain sorted scrap one time in three)
                    string id = random.Next(3) == 0 ? Materials[random.Next(4)] : partIds[random.Next(partIds.Length)];
                    int n = random.Next(1, 12); held[id] = Have(id) + n; gained += n;
                } else if (operation <= 6) {
                    var node = CallingCatalog.All[random.Next(CallingCatalog.Count)]; int level = state.CallingLevelAt(node.index);
                    var offer = state.NextRecipe(node)?.Pick(Have);
                    bool expected = state.DiskInstalled && offer != null && state.StatusOf(node, Have) == CallingStatus.Ready;
                    bool built = state.BuildCalling(node.id, offer ?? new List<string>(), out _);
                    Check(built == expected, "a print succeeds exactly when the disk, the tree and the parts allow it");
                    if (built) { Take(offer); callingsBuilt++; Check(state.CallingLevelAt(node.index) == level + 1, "one level"); }
                    else { callingsRefused++; Check(Json(state) == before, "a refusal changes nothing"); }
                } else if (operation <= 8) { // load the Monument with whatever sorted scrap is held, never more than it needs
                    var need = state.MonumentNeed();
                    var load = new WorkshopMaterials(Math.Min(need.metal, Have("Metal")), Math.Min(need.plastic, Have("Plastic")), Math.Min(need.glass, Have("Glass")), Math.Min(need.paper, Have("Paper")));
                    if (random.Next(4) == 0) load.metal = Math.Min(load.metal, 1); // sometimes a single piece
                    bool expected = state.DiskInstalled && state.monument < MonumentCatalog.MaxLevel && load.Total > 0;
                    int tier = state.monument; var offer = Load(load);
                    bool loaded = state.FeedMonument(offer, out bool printed, out _);
                    Check(loaded == expected && printed == (expected && load.Total == need.Total), "a load is accepted exactly when the disk is in and there is something the tier needs");
                    if (loaded) { Take(offer); loads++; if (printed) { tiersPrinted++; Check(state.monument == tier + 1 && state.fed.Total == 0, "one tier"); } }
                    else { loadsRefused++; Check(Json(state) == before, "a refusal changes nothing"); }
                } else if (operation == 9) {
                    var recipe = WorkshopCatalog.All[random.Next(WorkshopCatalog.All.Count)];
                    Check(state.InstallDecor(recipe.id, out _) == (recipe.isDecor && (((ushort)(1 << recipe.index)) & ushort.Parse(before.Split('|')[0])) == 0), "decor installs once, a retired sculpture never");
                } else if (operation == 10) { // a bag is ripped open; a disk that is out gets kept two times in three, crushed otherwise
                    if (diskOut) {
                        if (random.Next(3) > 0) { Check(state.InsertDisk(), "the disk that was found is kept"); disksKept++; }
                        diskOut = false;
                    } else if (state.BagRipped(random.NextDouble(), false)) { diskOut = true; disksFound++; }
                    Check(!state.DiskInstalled || !state.BagRipped(0.0, false), "no disk ever drops for a business that has one");
                } else {
                    int number = random.Next(1, 400); bool on = random.Next(3) > 0;
                    bool was = state.IsPinned(number); int count = state.PinCount;
                    bool changed = state.SetPin(number, on, out _);
                    Check(changed == (was != on && (!on || count < WorkshopState.MaxPins)), "pin toggles exactly when it should");
                    Check(state.PinCount <= WorkshopState.MaxPins && WorkshopState.PinsTextValid(state.pins) && state.pins.Length <= WorkshopState.MaxPinChars, "pins stay compact"); if (changed) pinsSet++;
                }
                Check(state.Valid(), "state invariant");
                Check((state.completed & WorkshopCatalog.RetiredBits) == 0, "no retired bit is ever set");
                Check(held.Values.All(n => n >= 0) && gained == consumed + held.Values.Sum(n => (long)n), "item conservation");
                long spent = state.fed.Total;
                foreach (var node in CallingCatalog.All) for (int level = 1; level <= state.CallingLevelAt(node.index); level++) spent += node.RecipeOf(level).total;
                for (int tier = 1; tier <= state.monument; tier++) spent += MonumentCatalog.Cost(tier).Total;
                Check(spent == consumed, "every consumed item paid for exactly one printed level, one printed tier or the load toward the next");
                Check(state.DiskInstalled || spent == 0, "nothing was ever consumed before the disk was in");
                checks++;
            }
        }
        // ---- the retired bank: paid once, exactly, at the bin rates (and a save with a sculpture is converted alongside)
        long paid = 0;
        for (int i = 0; i < 2000; i++) {
            ushort built = (ushort)(random.Next(1 << 14) & (i % 4 == 0 ? WorkshopCatalog.AllBits : WorkshopCatalog.DecorBits));
            var state = new WorkshopState {
                bank = new WorkshopMaterials(random.Next(250001), random.Next(250001), random.Next(250001), random.Next(250001)),
                allocated = i % 3 == 0 ? new WorkshopMaterials(random.Next(30001), random.Next(10001), random.Next(7001), random.Next(8001)) : default,
                activeRecipe = i % 3 == 0 ? "spring_launcher" : "", completed = built };
            Check(!state.Valid(), "an unconverted bank is not a valid state");
            long m = (long)state.bank.metal + state.allocated.metal, p = (long)state.bank.plastic + state.allocated.plastic,
                 g = (long)state.bank.glass + state.allocated.glass, pa = (long)state.bank.paper + state.allocated.paper;
            long expected = (m * 200 + p * 110 + g * 55 + pa * 45 + 500) / 1000;
            long cents = state.RetireBank(); paid += cents;
            Check(cents == expected && state.RetireBank() == 0 && state.bank.Total == 0 && state.allocated.Total == 0 && state.activeRecipe == "" && state.completed == built,
                "bank conversion exact, idempotent, keeps built pieces");
            // the rest of the load path: sculptures become a tier, and what they printed implies the disk
            state.RetireSculptures(); state.NormalizeCallings();
            Check(state.Valid() && state.completed == (built & WorkshopCatalog.DecorBits) && state.monument == WorkshopCatalog.LegacyMonumentLevel(built) && (state.monument == 0) == (state.disk == 0),
                "an old save loads as a valid state with its decor, its Monument tier and (only then) its disk");
            checks++;
        }
        Check(new WorkshopState().RetireBank() == 0, "an empty bank pays nothing");
        Check(WorkshopCatalog.BankCents(new WorkshopMaterials(1000, 1000, 1000, 1000)) == 410, "1 kg of each = $4.10");
        Console.WriteLine($"PASS: {checks} deterministic state checks across 1000 sessions / 200000 operations; {callingsBuilt} blueprint levels printed ({callingsRefused} refused); " +
            $"{loads} Monument loads ({loadsRefused} refused), {tiersPrinted} tiers printed; 16000 disk hunts (first disk always in bags 3-10, replacements within 3), {disksFound} disks found and {disksKept} kept in sessions; " +
            $"{pinsSet} pin changes; kept-item conservation exact; 2000 retired banks paid ${paid / 100.0:F2} once each.");
    }
}

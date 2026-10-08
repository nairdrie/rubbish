using System;
using System.Collections.Generic;

/// <summary>
/// Business-owned workshop save data: installed decor, the blueprint tree (Callings), the Monument, the SolidWaste 3D disk,
/// display pins, the hutch and the wanted-part pins.
/// Pure and deterministic (no UnityEngine, no GameSession): a print is told which kept items were offered and decides.
/// The items themselves live in the owner's inventory; this state never holds them.
/// </summary>
[Serializable]
public sealed class WorkshopState
{
    /// <summary>RETIRED (the gram bank). Kept only so an older save still parses: <see cref="RetireBank"/> turns whatever is
    /// here into cash once, on load, and a valid state always has all three empty.</summary>
    public WorkshopMaterials bank, allocated;
    public string activeRecipe = "";
    /// <summary>One bit per <see cref="WorkshopRecipe.index"/>: the decor piece is installed. THE source of truth for decor,
    /// however it was paid for (scrap before the bank was retired, Scrapazon since). The three retired sculpture bits are
    /// turned into Monument tiers on load (<see cref="RetireSculptures"/>) and are never set in a valid state.</summary>
    public ushort completed;
    public int colorIndex;
    public List<WorkshopDisplayData> displays = new List<WorkshopDisplayData>();
    /// <summary>Callings tree: <see cref="CallingNode.index"/> -> built level. Shorter than the catalog (or missing, in an
    /// older save) means level 0. Lives here so the unlock is one replicated, saved transaction.</summary>
    public List<int> callings = new List<int>();
    /// <summary>Raccoon calling: the Critter Hutch roster, encoded by <c>HutchState.Encode</c> (Callings/Raccoon/HutchState.cs).
    /// An opaque, length- and alphabet-checked string here, so this file stays free of that type. "" = no hutch (old saves).</summary>
    public string hutch = "";
    public const int MaxHutchChars = 700;
    /// <summary>Wanted parts ("hunt for this"): item catalog numbers in base 36, comma separated ("1f,2a,9"). Compact on
    /// purpose: it rides in the replicated workshop JSON so the host's Magnus can read every owner's list.</summary>
    public string pins = "";
    public const int MaxPins = 24, MaxPinChars = MaxPins * 4;
    /// <summary>The Monument: printed tier, 0..<see cref="MonumentCatalog.MaxLevel"/>.</summary>
    public int monument;
    /// <summary>Kept items already loaded toward the NEXT Monument tier, per sorted type (never more than that tier costs;
    /// all zero once the last tier is printed). The big tiers are loaded a parts shelf at a time.</summary>
    public WorkshopMaterials fed;
    /// <summary>1 = the SolidWaste 3D floppy disk is in this business's laptop: the print tab exists and blueprints can be
    /// printed. 0 = not found yet (or found and thrown away).</summary>
    public int disk;
    /// <summary>Bags ripped open on this business's table in the current disk window (<see cref="BagRipped"/>).</summary>
    public int diskBags;
    /// <summary>How many disks this business has been handed so far. The first hides in the first
    /// <see cref="DiskFirstWindow"/> bags; a replacement for a lost one comes within <see cref="DiskRetryWindow"/>.</summary>
    public int diskDrops;

    public bool Completed(string id) { var r = WorkshopCatalog.Get(id); return r != null && (completed & (1 << r.index)) != 0; }
    public ItemInstance Display(string recipeId)
    { if (displays != null) foreach (var d in displays) if (d != null && d.recipeId == recipeId) return d.Preview(); return null; }
    public WorkshopState Clone()
    {
        var copy = new WorkshopState { bank = bank, allocated = allocated, activeRecipe = activeRecipe ?? "", completed = completed, colorIndex = colorIndex };
        if (displays != null) foreach (var d in displays) if (d != null) copy.displays.Add(d.Clone());
        if (callings != null) copy.callings.AddRange(callings);
        copy.hutch = hutch ?? ""; copy.pins = pins ?? "";
        copy.monument = monument; copy.fed = fed; copy.disk = disk; copy.diskBags = diskBags; copy.diskDrops = diskDrops;
        return copy;
    }
    public bool Valid() => ValidIgnoringCallings() && CallingsValid() && HutchTextValid(hutch) && PinsTextValid(pins) && PrinterValid();
    /// <summary>Short, and only digits, letters, spaces and ",;|-": nothing JSON has to escape.</summary>
    public static bool HutchTextValid(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > MaxHutchChars) return false;
        foreach (char c in text)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == ' ' || c == ',' || c == ';' || c == '|' || c == '-')) return false;
        return true;
    }
    /// <summary>Everything except the callings levels, the hutch, the pins, the Monument and the disk (a local save is
    /// repaired by <see cref="NormalizeCallings"/> instead of being thrown away; a network upload must pass the full
    /// <see cref="Valid"/>).</summary>
    internal bool ValidIgnoringCallings()
    {
        if ((completed & ~WorkshopCatalog.DecorBits) != 0 || colorIndex < 0 || colorIndex >= WorkshopCatalog.ColorCount || displays == null || displays.Count > 4) return false;
        if (!string.IsNullOrEmpty(activeRecipe) || bank.Total != 0 || allocated.Total != 0 || HasNegative(bank) || HasNegative(allocated)) return false; // the bank is retired
        var slots = new HashSet<string>(); var uids = new HashSet<string>();
        foreach (var d in displays) if (d == null || !d.Valid() || !Completed(d.recipeId) || !slots.Add(d.recipeId) || !uids.Add(d.uid)) return false;
        return true;
    }
    static bool HasNegative(WorkshopMaterials m) => m.metal < 0 || m.plastic < 0 || m.glass < 0 || m.paper < 0;
    /// <summary>A LOCAL save whose decor fields fail <see cref="ValidIgnoringCallings"/> is repaired, not thrown away:
    /// a stale display pin or a bad sign colour must not cost the things nothing can re-derive (the Monument, its load,
    /// the disk, printed blueprints). Unknown decor bits, a bad colour and bad or duplicate display pins are dropped.</summary>
    internal void RepairDecor()
    {
        completed &= WorkshopCatalog.DecorBits;
        if (colorIndex < 0 || colorIndex >= WorkshopCatalog.ColorCount) colorIndex = 0;
        displays ??= new List<WorkshopDisplayData>();
        var slots = new HashSet<string>(); var uids = new HashSet<string>();
        displays.RemoveAll(d => d == null || !d.Valid() || !Completed(d.recipeId) || !slots.Add(d.recipeId) || !uids.Add(d.uid));
        if (displays.Count > 4) displays.RemoveRange(4, displays.Count - 4);
    }

    // ------------------------------------------------------------------ the retired gram bank

    /// <summary>
    /// The one-time conversion of the retired bank: every banked AND allocated gram becomes cash at what its bin would have
    /// paid (<see cref="WorkshopCatalog.BankCents"/>), the active project is cancelled, and the three legacy fields are
    /// cleared. Returns the cents to pay out. Idempotent: a second call finds nothing and returns 0. Built pieces stay built.
    /// </summary>
    public long RetireBank()
    {
        var grams = new WorkshopMaterials(Clamp(bank.metal) + Clamp(allocated.metal), Clamp(bank.plastic) + Clamp(allocated.plastic),
            Clamp(bank.glass) + Clamp(allocated.glass), Clamp(bank.paper) + Clamp(allocated.paper));
        bank = default; allocated = default; activeRecipe = "";
        return WorkshopCatalog.BankCents(grams);
    }
    /// <summary>A material never held more than 250 kg; a corrupt number pays nothing rather than a fortune.</summary>
    const int LegacyMaxBankGrams = 250000;
    static int Clamp(int grams) => grams < 0 || grams > LegacyMaxBankGrams ? 0 : grams;

    // ------------------------------------------------------------------ decor (Scrapazon) and the retired sculptures

    /// <summary>A decor piece was bought on Scrapazon: install it. False (state unchanged) for a retired piece, an unknown
    /// id or a piece that is already installed.</summary>
    public bool InstallDecor(string recipeId, out string why)
    {
        var recipe = WorkshopCatalog.Get(recipeId);
        if (recipe == null || !recipe.isDecor) { why = "Scrapazon doesn't sell that."; return false; }
        if (Completed(recipeId)) { why = "Already installed!"; return false; }
        completed |= (ushort)(1 << recipe.index); why = ""; return true;
    }
    /// <summary>
    /// The one-time conversion of the three bench sculptures: the best one a save built becomes that Monument tier (Junk
    /// Sculpture 1, Scrap Tower 2, Scrap Monument 3) and their bits are cleared. Raises only; idempotent. True when the
    /// state changed.
    /// </summary>
    public bool RetireSculptures()
    {
        if ((completed & WorkshopCatalog.RetiredBits) == 0) return false;
        int legacy = WorkshopCatalog.LegacyMonumentLevel(completed);
        completed = (ushort)(completed & ~WorkshopCatalog.RetiredBits);
        if (legacy > monument) { monument = legacy; fed = default; }
        return true;
    }

    // ------------------------------------------------------------------ the SolidWaste 3D disk

    /// <summary>The first disk hides somewhere in a business's first ten bags, never before the third (learn to sort
    /// first); a replacement for one that was binned or crushed turns up within three.</summary>
    public const int DiskFirstWindow = 10, DiskEarliestBag = 3, DiskRetryWindow = 3;
    public bool DiskInstalled => disk != 0;
    /// <summary>The floppy was KEPT: it goes into the laptop. False (unchanged) when it is already installed.</summary>
    public bool InsertDisk()
    {
        if (disk != 0) return false;
        disk = 1; diskBags = 0; return true;
    }
    /// <summary>
    /// A bag was ripped open on this business's sorting table. True when THIS bag carries the disk.
    /// <paramref name="roll01"/> is a uniform roll in [0, 1); <paramref name="diskIsOut"/> = a disk is already lying around
    /// this shop (one at a time: the window waits for it). Every bag of a window is equally likely, and the last bag of
    /// the window always has it.
    /// </summary>
    public bool BagRipped(double roll01, bool diskIsOut)
    {
        if (disk != 0 || diskIsOut) return false;
        bool first = diskDrops <= 0;
        int window = first ? DiskFirstWindow : DiskRetryWindow, earliest = first ? DiskEarliestBag : 1;
        diskBags = Math.Max(0, diskBags) + 1;
        if (diskBags < earliest) return false;
        int left = window - diskBags; // bags of this window still to come after this one
        if (left > 0 && !(roll01 >= 0.0 && roll01 < 1.0 / (left + 1))) return false;
        diskBags = 0; diskDrops = Math.Min(MaxDiskDrops, Math.Max(0, diskDrops) + 1);
        return true;
    }
    const int MaxDiskDrops = 9999;

    // ------------------------------------------------------------------ the Monument (MonumentCatalog)

    /// <summary>What the NEXT tier still needs, per sorted type (all zero once the last tier is printed).</summary>
    public WorkshopMaterials MonumentNeed()
    {
        var cost = MonumentCatalog.Cost(monument + 1);
        return new WorkshopMaterials(Math.Max(0, cost.metal - fed.metal), Math.Max(0, cost.plastic - fed.plastic),
            Math.Max(0, cost.glass - fed.glass), Math.Max(0, cost.paper - fed.paper));
    }
    /// <summary>How many of the offered def ids are each sorted type (junk, hazards and valuables count for nothing).</summary>
    public static WorkshopMaterials GridOf(IReadOnlyList<string> defIds)
    {
        var grid = new WorkshopMaterials();
        if (defIds != null)
            foreach (string id in defIds) {
                var def = ItemCatalog.Get(id);
                if (def != null && WorkshopCatalog.TryMaterial(def.category, out var material)) grid.Set(material, grid.Get(material) + 1);
            }
        return grid;
    }
    /// <summary>
    /// Load <paramref name="offered"/> (one def id per kept item handed over) toward the next Monument tier: sorted metal,
    /// plastic, glass and paper only, never more of a type than the tier still needs. When the last piece goes in, the
    /// tier is printed (<paramref name="printed"/>) and loading starts again from nothing. All or nothing; the caller
    /// removes the offered items from the builder's inventory.
    /// </summary>
    public bool FeedMonument(IReadOnlyList<string> offered, out bool printed, out string why)
    {
        printed = false;
        if (!DiskInstalled) { why = NoDiskText; return false; }
        if (monument >= MonumentCatalog.MaxLevel) { why = "The Monument is finished. There is nothing bigger."; return false; }
        if (offered == null || offered.Count == 0) { why = "Not enough kept scrap yet. Throw sorted pieces in the KEEP crate."; return false; }
        var grid = GridOf(offered);
        if (grid.Total != offered.Count) { why = "Only sorted metal, plastic, glass and paper go into the Monument."; return false; }
        var need = MonumentNeed();
        if (grid.metal > need.metal || grid.plastic > need.plastic || grid.glass > need.glass || grid.paper > need.paper)
        { why = "That is more than this tier still needs."; return false; }
        fed = new WorkshopMaterials(fed.metal + grid.metal, fed.plastic + grid.plastic, fed.glass + grid.glass, fed.paper + grid.paper);
        if (MonumentNeed().Total == 0) { monument++; fed = default; printed = true; }
        why = ""; return true;
    }
    internal const string NoDiskText = "You need SolidWaste 3D first. KEEP the floppy disk when one turns up.";
    bool PrinterValid()
    {
        if (disk != 0 && disk != 1 || diskBags < 0 || diskBags > DiskFirstWindow || diskDrops < 0 || diskDrops > MaxDiskDrops) return false;
        if (monument < 0 || monument > MonumentCatalog.MaxLevel) return false;
        var cost = MonumentCatalog.Cost(monument + 1);
        if (fed.metal < 0 || fed.plastic < 0 || fed.glass < 0 || fed.paper < 0 || fed.metal > cost.metal || fed.plastic > cost.plastic ||
            fed.glass > cost.glass || fed.paper > cost.paper) return false;
        if (cost.Total > 0 && fed.Total >= cost.Total) return false; // a full load is a printed tier, never a resting state
        return !(monument > 0 || fed.Total > 0 || HasAnyCalling()) || disk == 1;  // nothing is printed or loaded without the software
    }
    bool HasAnyCalling()
    {
        if (callings != null) foreach (int level in callings) if (level > 0) return true;
        return false;
    }
    /// <summary>Repair the Monument and disk fields of a local save (never a reason to throw a workshop away): clamp the
    /// tier and the load, print a tier whose load is somehow full, and give a save that already printed something its
    /// disk (it was printing before the disk existed). Never lowers a tier.</summary>
    internal void NormalizePrinter()
    {
        monument = Math.Max(0, Math.Min(monument, MonumentCatalog.MaxLevel));
        var cost = MonumentCatalog.Cost(monument + 1);
        fed = new WorkshopMaterials(Math.Max(0, Math.Min(fed.metal, cost.metal)), Math.Max(0, Math.Min(fed.plastic, cost.plastic)),
            Math.Max(0, Math.Min(fed.glass, cost.glass)), Math.Max(0, Math.Min(fed.paper, cost.paper)));
        if (cost.Total > 0 && fed.Total >= cost.Total) { monument++; fed = default; }
        disk = disk != 0 || monument > 0 || fed.Total > 0 || HasAnyCalling() ? 1 : 0;
        diskBags = disk != 0 ? 0 : Math.Max(0, Math.Min(diskBags, DiskFirstWindow));
        diskDrops = Math.Max(0, Math.Min(diskDrops, MaxDiskDrops));
    }
    public bool Pin(string recipeId, WorkshopDisplayData display)
    {
        if (!Completed(recipeId) || WorkshopCatalog.Get(recipeId)?.isDisplay != true || display != null && (!display.Valid() || display.recipeId != recipeId)) return false;
        displays.RemoveAll(d => d.recipeId == recipeId || display != null && d.uid == display.uid);
        if (display != null) displays.Add(display.Clone());
        return true;
    }

    // ------------------------------------------------------------------ wanted parts (pins)

    const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    const int MaxPinNumber = 36 * 36 * 36 - 1;
    static string Base36(int n)
    {
        if (n <= 0) return "0";
        string s = "";
        while (n > 0) { s = Digits[n % 36] + s; n /= 36; }
        return s;
    }
    static bool TryBase36(string text, out int n)
    {
        n = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 3) return false;
        foreach (char c in text) {
            int d = Digits.IndexOf(c);
            if (d < 0) return false;
            n = n * 36 + d;
        }
        return n > 0;
    }
    /// <summary>The pinned catalog numbers, in the order they were pinned. Malformed entries are skipped.</summary>
    public List<int> PinList()
    {
        var list = new List<int>();
        if (!string.IsNullOrEmpty(pins))
            foreach (string token in pins.Split(',')) if (TryBase36(token, out int n) && !list.Contains(n)) list.Add(n);
        return list;
    }
    public int PinCount => PinList().Count;
    public bool IsPinned(int catalogNumber) => catalogNumber > 0 && !string.IsNullOrEmpty(pins) && PinList().Contains(catalogNumber);
    /// <summary>At most <see cref="MaxPins"/> distinct numbers 1..46655, base 36, canonical (what <see cref="SetPin"/> writes).</summary>
    public static bool PinsTextValid(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > MaxPinChars) return false;
        var seen = new HashSet<int>();
        foreach (string token in text.Split(',')) if (!TryBase36(token, out int n) || Base36(n) != token || !seen.Add(n)) return false;
        return seen.Count <= MaxPins;
    }
    /// <summary>Pin or unpin one catalog number. False (unchanged) when nothing would change, the number is out of range
    /// or the list is full.</summary>
    public bool SetPin(int catalogNumber, bool on, out string why)
    {
        why = "";
        if (catalogNumber <= 0 || catalogNumber > MaxPinNumber) { why = "That isn't something you can hunt for."; return false; }
        var list = PinList(); bool had = list.Contains(catalogNumber);
        if (had == on) return false;
        if (on && list.Count >= MaxPins) { why = $"You can hunt for {MaxPins} things at once. Unpin one first."; return false; }
        if (on) list.Add(catalogNumber); else list.Remove(catalogNumber);
        var tokens = new string[list.Count];
        for (int i = 0; i < list.Count; i++) tokens[i] = Base36(list[i]);
        pins = string.Join(",", tokens); return true;
    }

    // ------------------------------------------------------------------ callings (CallingCatalog)

    public int CallingLevel(string nodeId) { var node = CallingCatalog.Get(nodeId); return node == null ? 0 : CallingLevelAt(node.index); }
    public int CallingLevelAt(int index) => callings != null && index >= 0 && index < callings.Count ? callings[index] : 0;
    internal void SetCallingLevel(int index, int level)
    {
        if (callings == null) callings = new List<int>();
        while (callings.Count <= index) callings.Add(0);
        callings[index] = level;
    }
    bool CallingsValid()
    {
        if (callings == null) return true;
        if (callings.Count > CallingCatalog.Count) return false;
        for (int i = 0; i < callings.Count; i++) {
            var node = CallingCatalog.At(i);
            if (callings[i] < 0 || callings[i] > (node?.implementedLevels ?? 0)) return false; // coming-soon levels can't be owned
        }
        return true;
    }
    /// <summary>Clamp every level into what this build can own and drop unknown trailing entries; a broken hutch or pin
    /// string is emptied and the Monument and disk fields are repaired (<see cref="NormalizePrinter"/>). Never raises a
    /// calling level.</summary>
    internal void NormalizeCallings()
    {
        if (!HutchTextValid(hutch)) hutch = ""; // a local save is repaired, like its levels
        if (!PinsTextValid(pins)) pins = "";
        if (callings == null) callings = new List<int>();
        if (callings.Count > CallingCatalog.Count) callings.RemoveRange(CallingCatalog.Count, callings.Count - CallingCatalog.Count);
        for (int i = 0; i < callings.Count; i++)
            callings[i] = Math.Max(0, Math.Min(callings[i], CallingCatalog.At(i)?.implementedLevels ?? 0));
        NormalizePrinter();
    }
    /// <summary>The first unmet prerequisite of <paramref name="node"/> as (node, level), or false when all are met.</summary>
    public bool MissingPrereq(CallingNode node, out CallingNode needs, out int needsLevel)
    {
        needs = null; needsLevel = 0;
        if (node == null) return false;
        foreach (string prereq in node.prereqs) {
            if (!CallingCatalog.TryParsePrereq(prereq, out var other, out int level)) continue;
            if (CallingLevelAt(other.index) < level) { needs = other; needsLevel = level; return true; }
        }
        return false;
    }
    /// <summary>The recipe of the NEXT level of a node, or null (maxed, or no recipe authored).</summary>
    public PartRecipe NextRecipe(CallingNode node) => node?.RecipeOf(CallingLevelAt(node.index) + 1);
    /// <summary>Where the NEXT level of <paramref name="node"/> stands for this business. <paramref name="have"/> says how many
    /// usable kept items of a def the builder holds (null = none: Ready is never reported). Pure: no state changes.</summary>
    public CallingStatus StatusOf(CallingNode node, Func<string, int> have = null)
    {
        if (node == null) return CallingStatus.Unknown;
        int level = CallingLevelAt(node.index);
        if (level >= node.maxLevel) return CallingStatus.Maxed;
        if (level >= node.implementedLevels || node.RecipeOf(level + 1) == null) return CallingStatus.ComingSoon;
        if (MissingPrereq(node, out _, out _)) return CallingStatus.Locked;
        return have != null && node.RecipeOf(level + 1).CanBuild(have) ? CallingStatus.Ready : CallingStatus.CantAfford;
    }
    /// <summary>Print the next level of a node from <paramref name="offered"/> (one def id per kept item handed over):
    /// the disk must be installed, prerequisites and level order checked, the offer must be exactly that level's recipe,
    /// the level raised. All or nothing. The caller removes the offered items from the builder's inventory.</summary>
    public bool BuildCalling(string nodeId, IReadOnlyList<string> offered, out string why)
    {
        var node = CallingCatalog.Get(nodeId);
        if (node != null && !DiskInstalled) { why = NoDiskText; return false; }
        switch (StatusOf(node)) {
            case CallingStatus.Unknown: why = "Unknown blueprint."; return false;
            case CallingStatus.Maxed: why = "Already fully printed!"; return false;
            case CallingStatus.ComingSoon: why = "Coming soon. Nobody can print that yet."; return false;
            case CallingStatus.Locked:
                MissingPrereq(node, out var needs, out int needsLevel);
                why = $"Print {needs.Level(needsLevel).title} first."; return false;
        }
        int next = CallingLevelAt(node.index) + 1;
        if (!node.RecipeOf(next).Matches(offered)) { why = "You don't have all the parts for that yet."; return false; }
        SetCallingLevel(node.index, next);
        why = ""; return true;
    }
    /// <summary>
    /// A save that already owns gear (bought with cash before Callings existed) shows those nodes as built, free of charge.
    /// Raises only, clamps to what can be built today; calling it again changes nothing.
    /// </summary>
    public bool SeedCallings(Func<string, int> ownedUpgradeLevel)
    {
        if (ownedUpgradeLevel == null) return false;
        bool changed = false;
        foreach (var node in CallingCatalog.All) {
            if (!node.implemented || string.IsNullOrEmpty(node.upgradeId)) continue;
            int owned = Math.Min(ownedUpgradeLevel(node.upgradeId), node.implementedLevels);
            if (owned > CallingLevelAt(node.index)) { SetCallingLevel(node.index, owned); changed = true; }
        }
        return changed;
    }
    /// <summary>The (upgrade id, level) pairs a business must RAISE so its upgrade levels match its printed callings AND its
    /// installed decor (a decor piece is Scrapazon product <c>decor_&lt;id&gt;</c> level 1). Never contains a level at or
    /// below <paramref name="currentUpgradeLevel"/>: nothing a save owns is ever lowered.</summary>
    public List<KeyValuePair<string, int>> CallingUpgrades(Func<string, int> currentUpgradeLevel)
    {
        var raises = new List<KeyValuePair<string, int>>();
        foreach (var node in CallingCatalog.All) {
            int level = CallingLevelAt(node.index);
            if (level <= 0 || string.IsNullOrEmpty(node.upgradeId)) continue;
            if (currentUpgradeLevel == null || currentUpgradeLevel(node.upgradeId) < level) raises.Add(new KeyValuePair<string, int>(node.upgradeId, level));
        }
        foreach (var recipe in WorkshopCatalog.All)
            if (recipe.isDecor && Completed(recipe.id) && (currentUpgradeLevel == null || currentUpgradeLevel(recipe.upgradeId) < 1))
                raises.Add(new KeyValuePair<string, int>(recipe.upgradeId, 1));
        return raises;
    }
    /// <summary>Decor a business paid Scrapazon for but which is not installed yet (the purchase is the owner's upgrade
    /// level; the install is this state's bit). Empty when the two agree.</summary>
    public List<string> DecorToInstall(Func<string, int> ownedUpgradeLevel)
    {
        var list = new List<string>();
        if (ownedUpgradeLevel != null)
            foreach (var recipe in WorkshopCatalog.All)
                if (recipe.isDecor && !Completed(recipe.id) && ownedUpgradeLevel(recipe.upgradeId) >= 1) list.Add(recipe.id);
        return list;
    }
}

/// <summary>Where a calling node's next level stands for one business (<see cref="WorkshopState.StatusOf"/>).
/// <c>CantAfford</c> = the parts are not all in the builder's inventory yet.</summary>
public enum CallingStatus : byte { Unknown, Ready, CantAfford, Locked, ComingSoon, Maxed }

/// <summary>Visual-only reference. No value, sale data or inventory insertion path: selling/listing the original unpins it.</summary>
[Serializable]
public sealed class WorkshopDisplayData
{
    public string recipeId, uid, defId;
    public float massKg, condition;
    public bool Valid() => WorkshopCatalog.Get(recipeId)?.isDisplay == true && !string.IsNullOrEmpty(uid) && uid.Length <= 64 &&
        !string.IsNullOrEmpty(defId) && defId.Length <= 64 && ItemCatalog.Get(defId)?.category == ItemCategory.Valuable &&
        !float.IsNaN(massKg) && !float.IsInfinity(massKg) && massKg > 0f && massKg <= 100f &&
        !float.IsNaN(condition) && condition >= 0f && condition <= 1f;
    public ItemInstance Preview() => new ItemInstance { uid = uid, defId = defId, massKg = massKg, condition = condition };
    public WorkshopDisplayData Clone() => new WorkshopDisplayData { recipeId = recipeId, uid = uid, defId = defId, massKg = massKg, condition = condition };
    public static WorkshopDisplayData From(string recipeId, ItemInstance item) => item == null ? null : new WorkshopDisplayData
    { recipeId = recipeId, uid = item.uid, defId = item.defId, massKg = item.massKg, condition = item.condition };
}

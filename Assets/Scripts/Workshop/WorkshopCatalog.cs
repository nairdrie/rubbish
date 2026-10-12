using System;
using System.Collections.Generic;

public enum WorkshopMaterial : byte { Metal, Plastic, Glass, Paper }

/// <summary>Four whole numbers, one per sorted material. Today: how many kept ITEMS of each category a Monument tier takes
/// (<see cref="MonumentCatalog"/>) and how many are loaded so far (<see cref="WorkshopState.fed"/>). Until the gram bank was
/// retired it also counted grams (<see cref="WorkshopState.bank"/>).</summary>
[Serializable]
public struct WorkshopMaterials
{
    public int metal, plastic, glass, paper;
    public WorkshopMaterials(int metal, int plastic, int glass, int paper)
    { this.metal = metal; this.plastic = plastic; this.glass = glass; this.paper = paper; }
    public int Get(WorkshopMaterial material) => material switch
    { WorkshopMaterial.Metal => metal, WorkshopMaterial.Plastic => plastic, WorkshopMaterial.Glass => glass, WorkshopMaterial.Paper => paper, _ => 0 };
    public void Set(WorkshopMaterial material, int value)
    {
        switch (material) { case WorkshopMaterial.Metal: metal = value; break; case WorkshopMaterial.Plastic: plastic = value; break;
            case WorkshopMaterial.Glass: glass = value; break; case WorkshopMaterial.Paper: paper = value; break; }
    }
    public int Total => metal + plastic + glass + paper;
    public int TotalGrams => Total;
}

/// <summary>
/// One piece of shop DECOR: bought for cash on Scrapazon (<see cref="price"/>, department "decor"); buying one sets its
/// <see cref="WorkshopState.completed"/> bit, which is the only thing that installs a piece. Three old entries are
/// <see cref="retired"/>: the bench sculptures, replaced by the ten-tier Monument (<see cref="MonumentCatalog"/>). Their
/// saved bits become Monument tiers on load (<see cref="WorkshopState.RetireSculptures"/>) and are never set again.
/// </summary>
public sealed class WorkshopRecipe
{
    public readonly string id, name, description;
    public readonly int index;
    public readonly bool isDisplay, retired;
    /// <summary>The Scrapazon price in whole dollars. 0 for a retired piece.</summary>
    public readonly int price;
    /// <summary>What the piece cost in grams before the bank was retired. Only orders the decor prices (and the docs).</summary>
    public readonly WorkshopMaterials legacyGrams;
    public bool isDecor => !retired;
    /// <summary>The Scrapazon product / upgrade id that owns this decor piece ("decor_bottle_cabinet"); null for a retired piece.</summary>
    public string upgradeId => retired ? null : WorkshopCatalog.DecorPrefix + id;
    internal WorkshopRecipe(int index, string id, string name, string description, int price, WorkshopMaterials legacyGrams, bool display = false, bool retired = false)
    { this.index = index; this.id = id; this.name = name; this.description = description; this.price = price;
        this.legacyGrams = legacyGrams; isDisplay = display; this.retired = retired; }
}

public static class WorkshopCatalog
{
    public const string DecorPrefix = "decor_";
    static WorkshopMaterials G(int metal, int plastic, int glass, int paper) => new WorkshopMaterials(metal, plastic, glass, paper);
    static WorkshopRecipe Decor(int index, string id, string name, string description, int price, WorkshopMaterials legacy, bool display = false) =>
        new WorkshopRecipe(index, id, name, description, price, legacy, display);
    static WorkshopRecipe Retired(int index, string id, string name, WorkshopMaterials legacy) =>
        new WorkshopRecipe(index, id, name, "Retired. It became a tier of the Monument.", 0, legacy, false, true);

    // Indices are saved bits. Append new pieces; never reorder or reuse one.
    // TUNING: decor prices rise with what a piece used to cost in scrap ($250 for the 3 kg cabinet, $15,000 for the 45 kg
    // launcher) and sit beside Scrapazon's displays ($150 plank shelf, $2,500 glass case, $9,000 pedestal).
    static readonly WorkshopRecipe[] recipes = {
        Decor(0, "bottle_cabinet", "Bottle Cabinet", "A handmade glass-front cabinet for a favourite find.", 250, G(1000, 500, 1000, 500), true),
        Decor(1, "rotating_pedestal", "Rotating Pedestal", "Give one treasured find its own slowly turning spotlight.", 900, G(3000, 1500, 1000, 500), true),
        Decor(2, "wall_display", "Wall Display", "Put a favourite find up on the wall, clear of the sorting floor.", 1500, G(3000, 1500, 1500, 3000), true),
        Decor(3, "grand_display", "Grand Display", "A grand, illuminated centrepiece for your collection.", 6000, G(10000, 5000, 7000, 2000), true),
        Decor(4, "business_sign", "Custom Business Sign", "Put your business name in lights with a choice of colours.", 400, G(1500, 1500, 500, 500)),
        Retired(5, "junk_sculpture", "Junk Sculpture", G(4000, 3000, 1000, 2000)),
        Retired(6, "sculpture_tower", "Scrap Tower", G(10000, 5000, 2000, 3000)),
        Retired(7, "sculpture_monument", "Scrap Monument", G(20000, 10000, 5000, 5000)),
        Decor(8, "bag_hoop", "Bag Hoop", "A trick-shot target for your forecourt. Toss a bag through for bragging rights.", 2500, G(12000, 6000, 0, 2000)),
        Decor(9, "sorting_chute", "Sorting Chute", "Crank to feed up to three floor bags onto the sorting table. Pauses when the table is full.", 9000, G(18000, 10000, 0, 2000)),
        Decor(10, "break_sofa", "Break Sofa", "A patched-up sofa where the crew can take a breather.", 2000, G(4000, 6000, 0, 8000)),
        Decor(11, "scrap_radio", "Scrap Radio", "An oversized homemade radio with an on/off switch.", 3500, G(12000, 7000, 2000, 1000)),
        Decor(12, "spring_launcher", "Spring Launcher", "Load a collected bag and send it flying with a spring-powered launch.", 15000, G(30000, 10000, 2000, 3000)),
        Decor(13, "doorbell", "Ridiculous Doorbell", "A cheerful, unnecessary doorbell for visitors to ring.", 3000, G(10000, 6000, 2000, 2000))
    };
    public static IReadOnlyList<WorkshopRecipe> All { get; } = Array.AsReadOnly(recipes);
    /// <summary>Every bit a save may carry (the three retired ones included: an old save still parses).</summary>
    public const ushort AllBits = (1 << 14) - 1;
    /// <summary>The retired sculptures' bits (5, 6, 7): cleared on load, never valid in a live state.</summary>
    public const ushort RetiredBits = (1 << 5) | (1 << 6) | (1 << 7);
    /// <summary>The bits a live state may have set: the eleven decor pieces.</summary>
    public const ushort DecorBits = AllBits & ~RetiredBits;
    public const int ColorCount = 8;
    public static WorkshopRecipe Get(string id)
    { if (!string.IsNullOrEmpty(id)) foreach (var recipe in recipes) if (recipe.id == id) return recipe; return null; }
    /// <summary>The decor piece a Scrapazon product id installs ("decor_doorbell" -> doorbell), or null.</summary>
    public static WorkshopRecipe DecorFor(string upgradeId)
    {
        if (string.IsNullOrEmpty(upgradeId) || !upgradeId.StartsWith(DecorPrefix, StringComparison.Ordinal)) return null;
        var recipe = Get(upgradeId.Substring(DecorPrefix.Length));
        return recipe != null && recipe.isDecor ? recipe : null;
    }
    /// <summary>The Monument tier a save's old sculptures are worth: the Junk Sculpture is tier 1, the Scrap Tower tier 2, the
    /// Scrap Monument tier 3 (the highest one built counts). 0 when none was built.</summary>
    public static int LegacyMonumentLevel(ushort completed) =>
        (completed & (1 << 7)) != 0 ? 3 : (completed & (1 << 6)) != 0 ? 2 : (completed & (1 << 5)) != 0 ? 1 : 0;
    public static bool TryMaterial(ItemCategory category, out WorkshopMaterial material)
    {
        switch (category) {
            case ItemCategory.Metal: material = WorkshopMaterial.Metal; return true;
            case ItemCategory.Plastic: material = WorkshopMaterial.Plastic; return true;
            case ItemCategory.Glass: material = WorkshopMaterial.Glass; return true;
            case ItemCategory.Paper: material = WorkshopMaterial.Paper; return true;
            default: material = default; return false;
        }
    }

    // ------------------------------------------------------------------ the retired gram bank
    /// <summary>Cents per kilogram the retired bank converts at: exactly what the four recycling bins pay per kilogram
    /// (Economy.RecycleRatePerKg, before contracts and combos; pinned to it by WorkshopStateTests).</summary>
    public const int MetalCentsPerKg = 800, PlasticCentsPerKg = 440, GlassCentsPerKg = 220, PaperCentsPerKg = 180;
    /// <summary>What <paramref name="grams"/> of banked material would have paid in its bins, in whole cents (half up).</summary>
    public static long BankCents(WorkshopMaterials grams)
    {
        long milli = Math.Max(0, grams.metal) * (long)MetalCentsPerKg + Math.Max(0, grams.plastic) * (long)PlasticCentsPerKg +
                     Math.Max(0, grams.glass) * (long)GlassCentsPerKg + Math.Max(0, grams.paper) * (long)PaperCentsPerKg;
        return (milli + 500) / 1000;
    }
}

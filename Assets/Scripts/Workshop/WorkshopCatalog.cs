using System;
using System.Collections.Generic;

public enum WorkshopMaterial : byte { Metal, Plastic, Glass, Paper }

/// <summary>Whole grams only. No cash, timers, rolled recipes or progression requirements.</summary>
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
    public int TotalGrams => metal + plastic + glass + paper;
}

public sealed class WorkshopRecipe
{
    public readonly string id, name, description;
    public readonly WorkshopMaterials cost;
    public readonly int index;
    public readonly bool isDisplay;
    public int Cost(WorkshopMaterial material) => cost.Get(material);
    internal WorkshopRecipe(int index, string id, string name, string description, int metal, int plastic, int glass, int paper, bool display = false)
    { this.index = index; this.id = id; this.name = name; this.description = description;
        cost = new WorkshopMaterials(metal, plastic, glass, paper); isDisplay = display; }
}

public static class WorkshopCatalog
{
    // Indices are saved bits. Append new recipes; never reorder or reuse one.
    static readonly WorkshopRecipe[] recipes = {
        new WorkshopRecipe(0, "bottle_cabinet", "Bottle Cabinet", "A handmade glass-front cabinet for a favourite find.", 1000, 500, 1000, 500, true),
        new WorkshopRecipe(1, "rotating_pedestal", "Rotating Pedestal", "Give one treasured find its own slowly turning spotlight.", 3000, 1500, 1000, 500, true),
        new WorkshopRecipe(2, "wall_display", "Wall Display", "Put a favourite find up on the wall, clear of the sorting floor.", 3000, 1500, 1500, 3000, true),
        new WorkshopRecipe(3, "grand_display", "Grand Display", "A grand, illuminated centrepiece for your collection.", 10000, 5000, 7000, 2000, true),
        new WorkshopRecipe(4, "business_sign", "Custom Business Sign", "Put your business name in lights with a choice of colours.", 1500, 1500, 500, 500),
        new WorkshopRecipe(5, "junk_sculpture", "Junk Sculpture", "A small welded character made from your saved scrap.", 4000, 3000, 1000, 2000),
        new WorkshopRecipe(6, "sculpture_tower", "Scrap Tower", "An ambitious, wobbling stack of reclaimed shapes.", 10000, 5000, 2000, 3000),
        new WorkshopRecipe(7, "sculpture_monument", "Scrap Monument", "A towering trophy to your crew's recycling obsession.", 20000, 10000, 5000, 5000),
        new WorkshopRecipe(8, "bag_hoop", "Bag Hoop", "A workshop trick-shot target. Toss a bag through for bragging rights.", 12000, 6000, 0, 2000),
        new WorkshopRecipe(9, "sorting_chute", "Sorting Chute", "Crank to feed up to three floor bags onto the sorting table. Pauses when the table is full.", 18000, 10000, 0, 2000),
        new WorkshopRecipe(10, "break_sofa", "Break Sofa", "A patched-up sofa where the crew can take a breather.", 4000, 6000, 0, 8000),
        new WorkshopRecipe(11, "scrap_radio", "Scrap Radio", "An oversized homemade radio with an on/off switch.", 12000, 7000, 2000, 1000),
        new WorkshopRecipe(12, "spring_launcher", "Spring Launcher", "Load a collected bag and send it flying with a spring-powered launch.", 30000, 10000, 2000, 3000),
        new WorkshopRecipe(13, "doorbell", "Ridiculous Doorbell", "A cheerful, unnecessary doorbell for visitors to ring.", 10000, 6000, 2000, 2000)
    };
    public static IReadOnlyList<WorkshopRecipe> All { get; } = Array.AsReadOnly(recipes);
    public const ushort AllBits = (1 << 14) - 1;
    public const int ColorCount = 8;
    public static WorkshopRecipe Get(string id)
    { foreach (var recipe in recipes) if (recipe.id == id) return recipe; return null; }
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
    public static bool TryGrams(ItemInstance item, out WorkshopMaterial material, out int grams)
    {
        grams = 0; material = default;
        if (item?.Def == null || !TryMaterial(item.Def.category, out material) || float.IsNaN(item.massKg) ||
            float.IsInfinity(item.massKg) || item.massKg <= 0 || item.massKg > 100f) return false;
        grams = (int)Math.Round(item.massKg * 1000d, MidpointRounding.AwayFromZero);
        return grams > 0;
    }
}

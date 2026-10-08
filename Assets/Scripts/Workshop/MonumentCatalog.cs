using System;
using System.Collections.Generic;

// The Monument: a ten-tier trophy printed in SolidWaste 3D (Docs/Progression.md "Blueprints"). One way, no branches: every
// tier replaces the last with a bigger, stranger, prettier heap, and costs more sorted scrap than the one before.
// Pure data, no UnityEngine (Tools/workshop_state_probe.cs compiles this file standalone). The models are MonumentModels.

/// <summary>One tier of the Monument: what it is called, what the plaque says, and what it costs.</summary>
public sealed class MonumentTier
{
    /// <summary>1..<see cref="MonumentCatalog.MaxLevel"/>.</summary>
    public readonly int level;
    public readonly string name, plaque, description;
    /// <summary>Kept items of EACH sorted type (metal, plastic, glass, paper) this tier takes.</summary>
    public readonly int each;
    public WorkshopMaterials cost => new WorkshopMaterials(each, each, each, each);
    internal MonumentTier(int level, string name, string plaque, int each, string description)
    { this.level = level; this.name = name; this.plaque = plaque; this.each = each; this.description = description; }
}

public static class MonumentCatalog
{
    public const int MaxLevel = 10;
    /// <summary>The tree node id of the Monument's spine (it is not a Callings node: it has tiers, not recipes).</summary>
    public const string NodeId = "monument";

    // TUNING: "each" is kept items PER sorted type, so a tier takes four times that. Roughly x1.6 a tier; the last one is
    // meant to be absurd (4,000 items: weeks of keeping instead of binning). The parts shelf holds 200, so the big tiers
    // are loaded a shelf at a time (WorkshopState.fed remembers how far you are).
    static readonly MonumentTier[] tiers = {
        new MonumentTier(1, "Scrap Pal", "SCRAP PAL", 10, "A small welded friend with a saucepan hat. He believes in you."),
        new MonumentTier(2, "Tin Totem", "HIGHER PURPOSE", 20, "Three scrap pals standing on each other's shoulders. Nobody asked the bottom one."),
        new MonumentTier(3, "Hubcap Sunrise", "GOOD MORNING, GARBAGE", 35, "The totem gets a halo of spoons and hubcaps. Blinding at noon."),
        new MonumentTier(4, "Bottle Colonnade", "IN GLASS WE TRUST", 60, "A ring of green and blue bottle pillars. Classy, in a returnable-deposit way."),
        new MonumentTier(5, "Bike-Wheel Orrery", "ROUND AND ROUND", 100, "Bicycle wheels orbit the heap like planets. It squeaks on the hour."),
        new MonumentTier(6, "Lightbulb Spire", "A BRIGHT IDEA", 160, "A tiered spire of jars and dead lightbulbs that somehow still glow."),
        new MonumentTier(7, "The Recyclone", "REDUCE, REUSE, REVOLVE", 250, "A double helix of cans spirals up the spire. Visible from the motorway."),
        new MonumentTier(8, "Garbage Angel", "FLY, YOU FILTHY THING", 400, "Licence-plate wings, a gilded dustbin-lid crown. People leave flowers."),
        new MonumentTier(9, "Cathedral of Cans", "OUR LADY OF PERPETUAL RECYCLING", 650, "Flying buttresses, bottle-glass windows, four spires. Pilgrims welcome."),
        new MonumentTier(10, "Trashcendence", "TRASHCENDENCE", 1000, "A golden colossus of pure rubbish under turning halos. You did this. All of it."),
    };

    public static IReadOnlyList<MonumentTier> All { get; } = Array.AsReadOnly(tiers);
    /// <summary>1-based; null outside 1..<see cref="MaxLevel"/>.</summary>
    public static MonumentTier Tier(int level) => level >= 1 && level <= tiers.Length ? tiers[level - 1] : null;
    /// <summary>What tier <paramref name="level"/> costs (zero outside 1..<see cref="MaxLevel"/>).</summary>
    public static WorkshopMaterials Cost(int level) => Tier(level)?.cost ?? default;
}

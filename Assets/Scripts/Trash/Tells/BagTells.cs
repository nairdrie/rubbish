using System.Collections.Generic;
using UnityEngine;

/// <summary>A bag's personality (Docs/Multiplayer.md §14.3, package F3). At most one per bag.</summary>
public enum BagTell : byte { None = 0, Leaky = 1, Heavy = 2, OverStuffed = 3, Mystery = 4 }

/// <summary>How hard a thing is to push around (wind, critters, knockbacks). F5's wind ignores <see cref="Heavy"/>.</summary>
public enum BagWeightClass : byte { Light = 0, Normal = 1, Heavy = 2 }

/// <summary>
/// Bag tells (F3, §14.3). A bag's tell is a pure function of its <c>bagId</c> (FNV hash, salt "#tell"), so the host's roll and
/// every guest's copy agree without sending a byte; the host also shapes the bag's <i>contents</i> to match when it rolls them
/// (<see cref="ShapeContents"/>, called from <see cref="ItemCatalog.RollBag(string,int,float,string,LootProfile,float)"/>): heavy bags get
/// a heavy filler (20–25 kg), over-stuffed bags get 4–6 extra light items, mystery bags get one or two odd items (§6.3: guests only
/// ever see the mass and the tell, never the contents).
/// <para>Frequencies per bag (per mille): leaky 120, heavy 60, over-stuffed 100, mystery 30; the rest are plain bags.</para>
/// <para>Hooks for other packages: <see cref="Of(Carryable)"/> / <see cref="Of(string)"/>, <see cref="WeightClass"/>,
/// <see cref="Label"/>. Puddles and slipperiness: <see cref="Puddles"/>.</para>
/// </summary>
public static class BagTells
{
    // ---- frequencies (per mille, cumulative order: leaky, heavy, over-stuffed, mystery)
    public const int LeakyPerMille = 120, HeavyPerMille = 60, OverStuffedPerMille = 100, MysteryPerMille = 30;

    /// <summary>Heavy bags are filled to at least this (kg) so the weight is real everywhere (the mass replicates).</summary>
    public const float HeavyMinKg = 20f, HeavyMaxKg = 24.5f;
    /// <summary>A free-flying over-stuffed bag bursts above this impact speed (a plain bag: <see cref="TrashBag.BurstImpactSpeed"/>).</summary>
    public const float OverStuffedBurstSpeed = 5.2f;
    /// <summary>...and even a dropped / tumbling (not thrown) over-stuffed bag pops above this (slammed off a table, off the truck).</summary>
    public const float OverStuffedSlamSpeed = 6.5f;

    /// <summary>Odd things that only turn up in mystery bags (ItemCatalog tier 99, never in normal loot).</summary>
    public static readonly string[] MysteryItems =
        { "chattering_teeth", "ticking_alarm_clock", "vibrating_flip_phone", "jar_of_bees", "singing_bass", "haunted_tamagochu", "possessed_egg_timer" };
    /// <summary>Of those, the ones worth money (a mystery bag has a 50% chance of one of these).</summary>
    public static readonly string[] MysteryValuables = { "ticking_alarm_clock", "singing_bass", "haunted_tamagochu" };
    /// <summary>Heavy fillers (tier 99): what makes a heavy bag heavy.</summary>
    public static readonly string[] HeavyFillers = { "wet_sand_sack", "cinder_block", "cracked_cast_iron_pot" };

    static readonly Dictionary<string, BagTell> cache = new Dictionary<string, BagTell>(System.StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => cache.Clear();

    /// <summary>The tell of the bag with this id (None for loose piles, test/pass-through ids and null).</summary>
    public static BagTell Of(string bagId)
    {
        if (string.IsNullOrEmpty(bagId)) return BagTell.None;
        if (cache.TryGetValue(bagId, out var t)) return t;
        t = Roll(bagId);
        if (cache.Count > 4096) cache.Clear();
        cache[bagId] = t;
        return t;
    }

    /// <summary>The tell of a carryable (None for loose items and loose piles).</summary>
    public static BagTell Of(Carryable c)
    {
        if (!(c is TrashBag tb) || tb.Contents == null || tb.Contents.sackVariant < 0) return BagTell.None;
        return Of(tb.Contents.bagId);
    }

    static BagTell Roll(string bagId)
    {
        // Ids that aren't real bags (loose piles, unnamed network bags, bot race bags) stay plain.
        if (bagId.StartsWith("loose/") || bagId.StartsWith("net/") || bagId.StartsWith("race/") || bagId.StartsWith("menu/"))
            return BagTell.None;
        // Bot / test bags can force a tell: "...#tell=heavy" (the F3 bot brains use it).
        int force = bagId.IndexOf("#tell=", System.StringComparison.Ordinal);
        if (force >= 0)
        {
            string w = bagId.Substring(force + 6);
            if (w.StartsWith("leaky")) return BagTell.Leaky;
            if (w.StartsWith("heavy")) return BagTell.Heavy;
            if (w.StartsWith("over")) return BagTell.OverStuffed;
            if (w.StartsWith("mystery")) return BagTell.Mystery;
            return BagTell.None;
        }
        uint u = (uint)PickupVisuals.StableHash(bagId + "#tell") % 1000u;
        if (u < LeakyPerMille) return BagTell.Leaky;
        u -= LeakyPerMille;
        if (u < HeavyPerMille) return BagTell.Heavy;
        u -= HeavyPerMille;
        if (u < OverStuffedPerMille) return BagTell.OverStuffed;
        u -= OverStuffedPerMille;
        if (u < MysteryPerMille) return BagTell.Mystery;
        return BagTell.None;
    }

    /// <summary>Weight class for wind / critters: heavy bags (tell or ≥ 15 kg) don't blow around; light = under 3 kg.</summary>
    public static BagWeightClass WeightClass(Carryable c)
    {
        if (c == null) return BagWeightClass.Normal;
        if (Of(c) == BagTell.Heavy || c.MassKg >= 15f) return BagWeightClass.Heavy;
        return c.MassKg < 3f ? BagWeightClass.Light : BagWeightClass.Normal;
    }

    /// <summary>Short label for prompts and HUD ("LEAKY", "HEAVY"...), or null.</summary>
    public static string Label(BagTell t) => t switch
    {
        BagTell.Leaky => "Leaky",
        BagTell.Heavy => "HEAVY",
        BagTell.OverStuffed => "Over-Stuffed",
        BagTell.Mystery => "Buzzing",
        _ => null,
    };

    /// <summary>The display name of a bag with this tell (wraps <see cref="TrashBag.NameFor"/>).</summary>
    public static string NameFor(BagContents c, BagTell t, string plain) => t switch
    {
        BagTell.Leaky => "Leaky Drippy Trash Bag",
        BagTell.Heavy => "HEAVY Trash Bag (2-person lift)",
        BagTell.OverStuffed => "Over-Stuffed Trash Bag",
        BagTell.Mystery => "Buzzing... Ticking... Trash Bag?",
        _ => plain,
    };

    // ==================================================================================== host: contents to match

    /// <summary>
    /// Shape freshly rolled contents to the bag's tell. Deterministic for (bagId, rng state). Called by ItemCatalog.RollBag on the
    /// machine that rolls contents (the host / solo).
    /// </summary>
    public static void ShapeContents(BagContents bag, System.Random rng, string foundAt)
    {
        if (bag == null || bag.items == null) return;
        switch (Of(bag.bagId))
        {
            case BagTell.Heavy:
            {
                float total = bag.TotalMassKg;
                float target = Mathf.Lerp(HeavyMinKg, HeavyMaxKg, (float)rng.NextDouble());
                for (int guard = 0; guard < 4 && total < target; guard++)
                {
                    var def = ItemCatalog.Get(HeavyFillers[rng.Next(HeavyFillers.Length)]);
                    if (def == null) break;
                    var it = ItemCatalog.RollItem(def, rng, foundAt);
                    if (total + it.massKg > HeavyMaxKg + 1f) it.massKg = Mathf.Max(def.minMass * 0.5f, HeavyMaxKg - total);
                    bag.items.Add(it);
                    total += it.massKg;
                }
                break;
            }
            case BagTell.OverStuffed:
            {
                int extra = 4 + rng.Next(3);
                for (int i = 0; i < extra; i++)
                {
                    var def = LightFiller(rng);
                    if (def != null) bag.items.Add(ItemCatalog.RollItem(def, rng, foundAt));
                }
                break;
            }
            case BagTell.Mystery:
            {
                int n = 1 + (rng.NextDouble() < 0.35 ? 1 : 0);
                for (int i = 0; i < n; i++)
                {
                    string id = i == 0 && rng.NextDouble() < 0.5
                        ? MysteryValuables[rng.Next(MysteryValuables.Length)]
                        : MysteryItems[rng.Next(MysteryItems.Length)];
                    var def = ItemCatalog.Get(id);
                    if (def == null) continue;
                    var it = ItemCatalog.RollItem(def, rng, foundAt);
                    // Swap out a non-valuable so the bag doesn't get heavier or richer than its neighbours (balance).
                    int swap = -1;
                    for (int k = 0; k < bag.items.Count; k++)
                    {
                        var d = bag.items[k] != null ? bag.items[k].Def : null;
                        if (d != null && d.category != ItemCategory.Valuable && !IsMysteryId(d.id)) { swap = k; break; }
                    }
                    if (swap >= 0) bag.items[swap] = it;
                    else bag.items.Add(it);
                }
                break;
            }
        }
    }

    public static bool IsMysteryId(string defId) => defId != null && System.Array.IndexOf(MysteryItems, defId) >= 0;

    static readonly List<ItemDef> lightPool = new List<ItemDef>();

    static ItemDef LightFiller(System.Random rng)
    {
        if (lightPool.Count == 0)
        {
            foreach (var d in ItemCatalog.All)
            {
                if (d == null || d.category == ItemCategory.Valuable || d.category == ItemCategory.Hazard) continue;
                if (d.maxMass > 0.5f || ItemCatalog.TierOf(d) > 1 || ItemCatalog.HasTag(d, "garage_only")) continue;
                lightPool.Add(d);
            }
        }
        return lightPool.Count > 0 ? lightPool[rng.Next(lightPool.Count)] : null;
    }
}

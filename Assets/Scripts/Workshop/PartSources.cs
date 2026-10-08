using System.Collections.Generic;

/// <summary>
/// "Where do I find one?" for the Callings shopping list and ebag, derived from the item catalog alone (tier, loot tags,
/// rarity, weight) so it can never drift from the loot tables. Words only; the numbers stay in ItemCatalog.
/// </summary>
public static class PartSources
{
    public enum Kind : byte { Loot, GarageOnly, MysteryBag, NightShift, Prize, Unknown }

    /// <summary>How a def can be obtained at all. <c>Loot</c> = it rolls into ordinary kerb bags of its tier's districts.</summary>
    public static Kind KindOf(ItemDef def)
    {
        if (def == null) return Kind.Unknown;
        if (ItemCatalog.HasTag(def, "night_only")) return Kind.NightShift;
        if (ItemCatalog.HasTag(def, "tell_only")) return Kind.MysteryBag;
        if (ItemCatalog.HasTag(def, "garage_only")) return Kind.GarageOnly;
        if (ItemCatalog.WeightOf(def) <= 0f || ItemCatalog.TierOf(def) > 4) return Kind.Prize;
        return Kind.Loot;
    }

    /// <summary>The first district (in unlock order) whose bags can hold an item of this tier, or null.</summary>
    public static DistrictDef FirstDistrict(int tier)
    {
        foreach (var district in Districts.ProgressionOrder) if (district.tier >= tier) return district;
        return null;
    }
    static string ShortName(DistrictDef district)
    {
        if (district == null) return "richer districts";
        string name = district.name ?? district.id;
        int cut = name.IndexOf(" (");
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    /// <summary>One short line (the shopping list has room for about 26 characters after "HUNTING"): "anywhere + garage bags",
    /// "from Minivan Meadows", "garage-cleanout bags only", "RARE · anywhere, or steal it", "night: beat the Raccoon King".</summary>
    /// <param name="brief">Where only, no extras: for a line that also has to say "HUNTING".</param>
    public static string Hint(ItemDef def, bool brief = false)
    {
        switch (KindOf(def)) {
            case Kind.Unknown: return "nobody knows";
            case Kind.NightShift: return brief ? "night shift" : def.id == "raccoon_king_crown" ? "night: beat the Raccoon King" : def.id == "pet_raccoon" ? "night: a trash-can trap" : "night shift only";
            case Kind.MysteryBag: return "buzzing, ticking or HEAVY bags";
            case Kind.GarageOnly: return "garage-cleanout bags only";
            case Kind.Prize: return "event prize";
        }
        int tier = ItemCatalog.TierOf(def);
        string where = tier <= 1 ? "anywhere" : "from " + ShortName(FirstDistrict(tier));
        if (def.category == ItemCategory.Valuable)
            return brief ? RarityInfo.Label(def.rarity) + " · " + where : Fit(RarityInfo.Label(def.rarity) + " · " + where, ", or steal it"); // burglary takes valuables of the district's tier
        if (brief) return where;
        string extra = ItemCatalog.HasTag(def, "garage") ? "garage bags" : ItemCatalog.HasTag(def, "baby") ? "baby bags"
            : ItemCatalog.HasTag(def, "party") ? "party bags" : ItemCatalog.HasTag(def, "old") ? "spring cleaning" : null;
        return extra == null ? where : Fit(where, " + " + extra);
    }
    const int HintChars = 34;
    static string Fit(string text, string more) => text.Length + more.Length <= HintChars ? text + more : text;
}

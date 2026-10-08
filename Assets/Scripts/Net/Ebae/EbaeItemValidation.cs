using System;
using System.Text;

/// <summary>
/// Conservative catalog bounds for an item a guest asks the host to sell. This is a plausibility
/// check, not proof of ownership: guest inventory and saves are still owned by the guest.
/// </summary>
public static class EbaeItemValidation
{
    public const int MaxUidBytes = 60;
    public const float MassEpsilonKg = 0.01f;

    public static bool IsValid(ItemInstance item, out string reason)
    {
        if (!IsPlausible(item, out reason)) return false;
        // Stolen goods (ItemInstance.hot, Scavenger calling) never go through ebag, Pawn Pete or a neighbour trade.
        if (item.hot) { reason = "Hot goods. ebag won't touch them: find a fence."; return false; }
        return true;
    }

    /// <summary>The catalog bounds alone (what <see cref="IsValid"/> checked before hot goods existed). The fence uses this.</summary>
    public static bool IsPlausible(ItemInstance item, out string reason)
    {
        reason = null;
        if (item == null) { reason = "Item is missing."; return false; }
        if (string.IsNullOrEmpty(item.uid) || Encoding.UTF8.GetByteCount(item.uid) > MaxUidBytes)
        { reason = "Item ID is invalid."; return false; }

        var def = ItemCatalog.Get(item.defId);
        if (def == null) { reason = "Item type is not in the catalog."; return false; }
        if (!Finite(item.massKg) || item.massKg < 0f || item.massKg > def.maxMass + MassEpsilonKg)
        { reason = "Item weight is invalid for this type."; return false; }
        if (!Finite(item.condition) || item.condition < 0f || item.condition > 1f)
        { reason = "Item condition is invalid."; return false; }
        // Valuables may be rolled below the definition minimum after damage. The rescue grate can
        // also leave a non-valuable at $1, so use at least $1 as the allowed upper bound.
        if (!Finite(item.value) || item.value < 0f || item.value > Math.Max(def.maxValue, 1f))
        { reason = "Item appraisal is invalid for this type."; return false; }
        return true;
    }

    /// <summary>All serialized ItemInstance fields must still describe the same physical item at hold time.</summary>
    public static bool FieldsMatch(ItemInstance a, ItemInstance b)
    {
        return a != null && b != null &&
               string.Equals(a.uid, b.uid, StringComparison.Ordinal) &&
               string.Equals(a.defId, b.defId, StringComparison.Ordinal) &&
               a.massKg == b.massKg && a.value == b.value && a.condition == b.condition &&
               // JsonUtility may normalize absent optional strings to empty on a network round-trip.
               string.Equals(a.foundAt ?? "", b.foundAt ?? "", StringComparison.Ordinal) &&
               a.dayFound == b.dayFound && a.hot == b.hot &&
               string.Equals(a.note ?? "", b.note ?? "", StringComparison.Ordinal);
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

using System;
using System.Collections.Generic;
using UnityEngine;

// Shared data types for the trash loop. FROZEN CONTRACT: other systems depend on these shapes.
// Add new fields only if every consumer tolerates the default value.

public enum ItemCategory
{
    Junk,       // landfill -> compactor
    Plastic,    // recyclable
    Glass,      // recyclable
    Metal,      // recyclable
    Paper,      // recyclable (paper + cardboard)
    Hazard,     // batteries, spray cans, paint: hazard bin. Compacting one goes boom.
    Valuable    // treasure: keep crate -> inventory -> sell on ebae
}

public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

/// <summary>Static definition of an item type. Lives in ItemCatalog.</summary>
[Serializable]
public class ItemDef
{
    public string id;               // stable key, snake_case, e.g. "wedding_ring"
    public string name;             // display name, e.g. "Wedding Ring"
    public ItemCategory category;
    public Rarity rarity;
    public float minMass = 0.1f;    // kg
    public float maxMass = 0.5f;    // kg
    public float minValue;          // Valuables: ebae market value range ($). Others: ignored (see Economy).
    public float maxValue;
    [TextArea] public string flavor; // one-line derpy description shown on reveal / ebae
    public string visual;           // key understood by ItemVisuals.Build (e.g. "ring", "bottle", "prefab:item/tire")
    public Color color = Color.gray; // primary tint for the visual
}

/// <summary>A concrete rolled item (one physical thing found in a bag).</summary>
[Serializable]
public class ItemInstance
{
    public string uid;              // unique per instance
    public string defId;            // ItemDef.id
    public float massKg;
    public float value;             // Valuables: rolled market value ($). Others: 0 (value derives from mass via Economy).
    [Range(0, 1)] public float condition = 1f; // 0 = trashed, 1 = mint. Scales valuable value.
    public string foundAt;          // address, e.g. "14 Maple St"
    public int dayFound;
    public string note;             // optional flavor (e.g. ring engraving "Forever yours - Dave")

    public ItemDef Def => ItemCatalog.Get(defId);
}

/// <summary>The contents of one trash bag. Travels: curb -> truck -> dump pile -> sorting table.</summary>
[Serializable]
public class BagContents
{
    public string bagId;            // stable per day: "{stopId}/d{day}/b{index}"
    public string foundAt;          // address
    public float bagMassKg = 0.15f; // the plastic sack itself
    public int sackVariant;         // 0..2 -> which sack prefab to show
    public List<ItemInstance> items = new List<ItemInstance>();

    public float TotalMassKg
    {
        get
        {
            float m = bagMassKg;
            for (int i = 0; i < items.Count; i++) m += items[i].massKg;
            return m;
        }
    }
}

/// <summary>
/// A bag a house has put out at its curb, as world data. Lives in GameSession.CurbBags whether or not the
/// Neighborhood scene is loaded; PickupStops materialize them as TrashBags when the scene is up.
/// </summary>
[Serializable]
public class CurbBag
{
    public string bagId;            // unique (GameSession.NewUid based); also contents.bagId
    public string stopId;           // PickupStop.stopId of the house
    public int slot;                // curb position index at that house (0 = first spot)
    public double putOutAtHours;    // GameClock.TotalHours when the house put it out (lateness pay)
    public BagContents contents;
}

public static class ItemCategoryInfo
{
    public static bool IsRecyclable(ItemCategory c) =>
        c == ItemCategory.Plastic || c == ItemCategory.Glass || c == ItemCategory.Metal || c == ItemCategory.Paper;

    public static string DisplayName(ItemCategory c) => c switch
    {
        ItemCategory.Junk => "Junk",
        ItemCategory.Plastic => "Plastic",
        ItemCategory.Glass => "Glass",
        ItemCategory.Metal => "Metal",
        ItemCategory.Paper => "Paper",
        ItemCategory.Hazard => "Hazard",
        ItemCategory.Valuable => "Valuable",
        _ => c.ToString()
    };

    public static Color Color(ItemCategory c) => c switch
    {
        ItemCategory.Junk => new Color(0.45f, 0.40f, 0.35f),
        ItemCategory.Plastic => new Color(0.20f, 0.55f, 1.00f),
        ItemCategory.Glass => new Color(0.30f, 0.85f, 0.55f),
        ItemCategory.Metal => new Color(0.75f, 0.78f, 0.82f),
        ItemCategory.Paper => new Color(0.85f, 0.70f, 0.45f),
        ItemCategory.Hazard => new Color(1.00f, 0.80f, 0.10f),
        ItemCategory.Valuable => new Color(1.00f, 0.84f, 0.20f),
        _ => UnityEngine.Color.white
    };
}

public static class RarityInfo
{
    public static Color Color(Rarity r) => r switch
    {
        Rarity.Common => new Color(0.80f, 0.80f, 0.80f),
        Rarity.Uncommon => new Color(0.35f, 0.90f, 0.35f),
        Rarity.Rare => new Color(0.30f, 0.60f, 1.00f),
        Rarity.Epic => new Color(0.75f, 0.35f, 1.00f),
        Rarity.Legendary => new Color(1.00f, 0.65f, 0.10f),
        _ => UnityEngine.Color.white
    };

    public static string Label(Rarity r) => r.ToString().ToUpperInvariant();
}

/// <summary>All money / tuning numbers in one place. Tweak here, not in systems.</summary>
public static class Economy
{
    // Collection (legacy fixed-route values; the living-world pickup pay below replaces them)
    public const float RoutePayPerStop = 8f;
    public const float RouteCompletionBonus = 25f;

    // Collection pay: every bag earns a pickup fee when it goes into the truck, scaled by how late you are.
    public const float PickupFeePerBag = 4f;        // full fee when collected on time
    public const float CurbClearedBonus = 2f;       // a house's curb is left completely clean
    public const float OnTimeHours = 4f;            // in-game hours after put-out that still count as on time
    public const float LateFloorHours = 12f;        // pay has decayed to the floor by this age
    public const float LatePayFloor = 0.25f;        // fraction of the fee still paid when very late
    public const float NeglectedHours = 16f;        // a pile with a bag this old is "neglected" (stinks, squatters move in)

    /// <summary>Fraction of PickupFeePerBag earned for a bag of this age (in-game hours). 1 when on time.
    /// Starter-district windows; richer districts pass their own (Districts.PayFraction).</summary>
    public static float PickupPayFraction(double ageHours) =>
        PickupPayFraction(ageHours, OnTimeHours, LateFloorHours, LatePayFloor);

    /// <summary>Pay fraction with a district's windows: 1 until onTimeHours, then linear down to latePayFloor at lateFloorHours.</summary>
    public static float PickupPayFraction(double ageHours, float onTimeHours, float lateFloorHours, float latePayFloor)
    {
        if (ageHours <= onTimeHours) return 1f;
        float t = Mathf.InverseLerp(onTimeHours, lateFloorHours, (float)ageHours);
        return Mathf.Lerp(1f, latePayFloor, t);
    }

    /// <summary>"On time!" / "A bit late" / "Late" / "Super late" for toasts and marker labels (starter windows).</summary>
    public static string LatenessLabel(double ageHours) => LatenessLabel(ageHours, OnTimeHours, LateFloorHours);

    /// <summary>Lateness label with a district's windows.</summary>
    public static string LatenessLabel(double ageHours, float onTimeHours, float lateFloorHours)
    {
        if (ageHours <= onTimeHours) return "On time!";
        if (ageHours <= (onTimeHours + lateFloorHours) * 0.5f) return "A bit late";
        if (ageHours <= lateFloorHours) return "Late";
        return "Super late";
    }

    // Rabies
    public const float RabiesIncubationHours = 1.5f; // in-game hours before symptoms kick in
    public const float RabiesPassOutHours = 20f;     // untreated this long -> pass out, hospital bill, cured
    public const float HospitalBill = 100f;
    public const float StartingTruckCapacityKg = 160f;

    // Sorting ($ per kg, credited instantly when an item lands in the correct bin). Docs/Progression.md §3.1 rebalance.
    public static float RecycleRatePerKg(ItemCategory c) => c switch
    {
        ItemCategory.Metal => 2.00f,
        ItemCategory.Plastic => 1.10f,
        ItemCategory.Glass => 0.55f,
        ItemCategory.Paper => 0.45f,
        _ => 0f
    };
    public const float MinRecyclePayout = 0.10f;    // floor per recycled item so tiny things still "ding"
    public const float HazardDisposalPay = 0.75f;   // per hazard item binned correctly (hazmat_contract: 1.50 / 4.00)
    public const float WrongBinPenalty = 0.30f;     // "Contaminated!"
    public const float CompactorScrapPerKg = 0.20f; // base compact pay; unopened bags and loose scrap use the same rate
    public const float HazardCompactFine = 0f;     // legacy API: hazard effects never charge a compaction fee
    public const float LazyDumpPerKg = CompactorScrapPerKg; // bulk compaction is a shortcut, not a different pay scale
    public const float LazyCrushHazardBoomChance = 0.25f; // bulk compact: per-hazard comic boom chance, no cash penalty

    // Trick-shot style pay (Docs/Progression.md §3.5): units × TrickShotUnitOfFee × the district fee × streak
    public const float TrickShotUnitOfFee = 0.25f;
    public const float TrickShotBand1 = 8f, TrickShotBand2 = 12f, TrickShotBand3 = 16f; // m: 1 / 2 / 3 units
    public const float TrickShotStreakStep = 0.25f, TrickShotStreakCap = 2.5f;          // ×(1 + 0.25·(n − 1)), capped

    // Work Orders: pay = max(WorkOrderMinPay, WorkOrderPayFraction × yesterday's net)
    public const float WorkOrderPayFraction = 0.05f;
    public const float WorkOrderMinPay = 25f;
    public const int WorkOrdersPerDay = 3;

    // Kevin's Crew
    public const float KevinFeeShare = 0.70f;       // of the district fee × pay fraction
    public const float KevinBurstChance = 0.05f;    // his bags that burst on the way (no fee, gone)

    public const float TowFee = 25f;                // "Hold R" truck recovery without truck_handling L1

    // Sort combo: consecutive correct sorts within ComboWindow seconds multiply payouts
    public const float ComboWindow = 2.5f;
    public const float ComboStep = 0.1f;            // +10% per combo step
    public const float ComboMax = 2.0f;             // x2 cap

    // ebae
    public const float PawnFraction = 0.45f;        // instant sell at 45% of market value
    public const float EbaeFeeFraction = 0.08f;     // ebae takes 8% of a sale

    public static string Money(float v) => (v < 0 ? "-$" : "$") + Mathf.Abs(v).ToString(Mathf.Abs(v) >= 100 ? "N0" : "0.00");
}

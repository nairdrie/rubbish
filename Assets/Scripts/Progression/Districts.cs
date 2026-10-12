using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>How bags roll in one district (Docs/Progression.md §5.4). Rolled by D's <c>ItemCatalog.RollBag</c> overload.</summary>
[Serializable]
public class LootProfile
{
    public string id;
    public int tier;
    public float p0, p1;                 // valuable chance per item pick = p0 + p1·wealth
    public float[] rarity0, rarity1;     // (C+U),R,E,L at w=0 and w=1: the first bucket is Common + Uncommon together
    public float common0, common1;       // Common's part of that first bucket at w=0 and w=1 (0 = no Common in the pool)
    public float keepsakeChance;         // per-bag chance of one extra Common/Uncommon keepsake (the Keepsake bonus)
    public int minItems, maxItems;
    public float[] categoryWeights;      // J,P,Pl,M,G,H (ItemCatalog.Cats order); null = today's plain weights
    public float[] tierBoost;            // index 1..4 (item tier -> pool weight ×); index 0 unused
    public float springFlat;             // Spring Cleaning's flat valuable bonus
    public float valuableMult = 1f;
    /// <summary>Chance that a Rare valuable roll becomes Epic (the golden gnome, §6.6). 0 = off (no extra RNG draw).</summary>
    public float rareToEpic;

    /// <summary>A copy with a different valuable multiplier / Rare→Epic chance (gnomes, Rookie Luck). Arrays are shared.</summary>
    public LootProfile With(float valuableMultiplier, float rareToEpicChance)
    {
        var p = (LootProfile)MemberwiseClone();
        p.valuableMult = valuableMultiplier;
        p.rareToEpic = rareToEpicChance;
        return p;
    }
}

/// <summary>One district row (Docs/Progression.md §5.1).</summary>
[Serializable]
public class DistrictDef
{
    public string id, name, stopPrefix, permitId;
    public int tier, rankRequired, houses;
    public float permitPrice, bagsPerDay;
    public int curbCap;                  // district-wide curb bag cap
    public float fee, tip, curbBonus, onTimeHours, lateFloorHours, latePayFloor, neglectedHours,
        cleanStreetsMax, dirtForZero, neglectFine, landfillPerBag, repMult, trustStart, critterMult;
    public bool butlerDrops;
    public LootProfile loot;
    public Rect boundsXZ;                // x = x0, y = z0, width, height (world XZ)
}

/// <summary>
/// Six districts in one coastal town. All is the append-only save/network order; ProgressionOrder is the route
/// unlock order. District tier remains the original 1..4 economy/loot band, not an array index.
/// </summary>
public static class Districts
{
    public const string MildlyPleasant = "mildly_pleasant", MinivanMeadows = "minivan_meadows",
                        SnootingtonHills = "snootington_hills", BillionaireBluffs = "billionaire_bluffs",
                        CouponCommons = "coupon_commons", StackingtonHeights = "stackington_heights";

    static DistrictDef[] all;
    static DistrictDef[] progression;
    static Dictionary<string, DistrictDef> byId;

    /// <summary>Stable save/network indices: Acres 0, Meadows 1, Hills 2, Bluffs 3, Commons 4, Heights 5. Append only.</summary>
    public static IReadOnlyList<DistrictDef> All { get { Ensure(); return all; } }

    /// <summary>Collection progression: Acres → Meadows → Commons → Hills → Heights → Bluffs.</summary>
    public static IReadOnlyList<DistrictDef> ProgressionOrder { get { Ensure(); return progression; } }

    public static int IndexOf(string id)
    {
        Ensure();
        for (int i = 0; i < all.Length; i++) if (all[i].id == id) return i;
        return 0;
    }

    /// <summary>Explicit authoring refresh after reloading a blueprint. Do not call from the layout loader itself:
    /// initial district construction already reads the layout and must not recurse.</summary>
    public static void RefreshLayoutBounds()
    {
        Ensure();
        foreach (var district in all) district.boundsXZ = CoastalCityLayout.DistrictBounds(district.id);
    }

    /// <summary>Existing presentation/loot band; district bytes must never be interpreted as wealth tiers.</summary>
    public static int StyleIndex(int districtIndex)
    {
        Ensure();
        return Mathf.Clamp(all[Mathf.Clamp(districtIndex, 0, all.Length - 1)].tier - 1, 0, 3);
    }

    /// <summary>v5 inserts districts along existing routes. Old upper-district owners keep access without rebuying
    /// their new connecting permits. Pure load migration: no cash, items, events or new-business grants.</summary>
    public static void MigrateLegacyPermits(IDictionary<string, int> levels, int saveVersion)
    {
        if (levels == null || saveVersion >= 5) return;
        bool Owns(string id) => levels.TryGetValue(id, out int level) && level > 0;
        void Grant(string id) { if (!Owns(id)) levels[id] = 1; }
        bool bluffs = Owns(ShopIds.PermitBillionaireBluffs);
        if (bluffs)
        {
            Grant(ShopIds.PermitSnootingtonHills);
            Grant(ShopIds.PermitStackingtonHeights);
        }
        if (bluffs || Owns(ShopIds.PermitSnootingtonHills))
        {
            Grant(ShopIds.PermitMinivanMeadows);
            Grant(ShopIds.PermitCouponCommons);
        }
    }

    /// <summary>Mildly Pleasant Acres (tier 1, always unlocked).</summary>
    public static DistrictDef Starter { get { Ensure(); return all[0]; } }

    /// <summary>Null-safe: unknown or empty ids give the Starter district.</summary>
    public static DistrictDef Get(string id)
    {
        Ensure();
        return !string.IsNullOrEmpty(id) && byId.TryGetValue(id, out var d) ? d : all[0];
    }

    /// <summary>Hand-authored identities keep their ID when moved between districts: resolve their published
    /// metadata first. Legacy generated identities retain the mm-/sh-/bb-/cc-/st- prefix contract.</summary>
    public static DistrictDef ForStopId(string stopId)
    {
        Ensure();
        if (!string.IsNullOrEmpty(stopId) && stopId.StartsWith("user-", StringComparison.Ordinal))
        {
            var live = CurbTrashSpawner.FindHouse(stopId);
            if (live != null && !string.IsNullOrEmpty(live.districtId)) return Get(live.districtId);
            var directory = HouseDirectory.Load();
            if (directory != null)
            {
                string authoredDistrict = AuthoredDistrictFor(stopId, directory.houses);
                if (!string.IsNullOrEmpty(authoredDistrict)) return Get(authoredDistrict);
            }
        }
        if (!string.IsNullOrEmpty(stopId))
            for (int i = 1; i < all.Length; i++)
                if (!string.IsNullOrEmpty(all[i].stopPrefix) && stopId.StartsWith(all[i].stopPrefix, StringComparison.Ordinal))
                    return all[i];
        return all[0];
    }

    public static string AuthoredDistrictFor(string stopId, IEnumerable<HouseDirectory.Entry> entries)
    {
        if (string.IsNullOrEmpty(stopId) || entries == null) return null;
        foreach (var entry in entries)
            if (entry != null && entry.stopId == stopId && !string.IsNullOrEmpty(entry.districtId)) return entry.districtId;
        return null;
    }

    /// <summary>bagId = "{stopId}/..." -> the stop's district.</summary>
    public static DistrictDef ForBagId(string bagId)
    {
        if (string.IsNullOrEmpty(bagId)) return Starter;
        int slash = bagId.IndexOf('/');
        return ForStopId(slash >= 0 ? bagId.Substring(0, slash) : bagId);
    }

    /// <summary>House address ("14 Maple St") via the CurbTrashSpawner house registry, else Starter.</summary>
    public static DistrictDef ForAddress(string address)
    {
        if (string.IsNullOrEmpty(address)) return Starter;
        var houses = CurbTrashSpawner.Houses;
        for (int i = 0; i < houses.Count; i++)
            if (houses[i] != null && string.Equals(houses[i].address, address, StringComparison.Ordinal))
                return ForStopId(houses[i].stopId);
        return Starter;
    }

    /// <summary>World position -> coastal layout district bounds, else Starter (including Depot Row).</summary>
    public static DistrictDef AtPosition(Vector3 world)
    {
        Ensure();
        var p = new Vector2(world.x, world.z);
        for (int i = all.Length - 1; i >= 1; i--)
            if (all[i].boundsXZ.Contains(p)) return all[i];
        return all[0];
    }

    /// <summary>Tier 1, or the company owns the district's permit.</summary>
    public static bool IsUnlocked(DistrictDef d)
    {
        if (d == null) return false;
        return d.tier <= 1 || string.IsNullOrEmpty(d.permitId) || GameSession.GetUpgradeLevel(d.permitId) > 0;
    }

    static int unlockedMask = -1;
    static IReadOnlyList<DistrictDef> unlocked;

    /// <summary>Unlocked districts in progression order. A new list whenever the set changes.</summary>
    public static IReadOnlyList<DistrictDef> Unlocked
    {
        get
        {
            Ensure();
            int mask = 0;
            for (int i = 0; i < all.Length; i++) if (IsUnlocked(all[i])) mask |= 1 << i;
            if (mask != unlockedMask || unlocked == null)
            {
                var list = new List<DistrictDef>(all.Length);
                foreach (var d in progression) if ((mask & (1 << IndexOf(d.id))) != 0) list.Add(d);
                unlocked = list;
                unlockedMask = mask;
            }
            return unlocked;
        }
    }

    /// <summary>The furthest unlocked district in progression order (Starter at worst).</summary>
    public static DistrictDef Top
    {
        get
        {
            Ensure();
            for (int i = progression.Length - 1; i >= 1; i--) if (IsUnlocked(progression[i])) return progression[i];
            return all[0];
        }
    }

    /// <summary>Fraction of the district fee earned for a bag of this age (district windows).</summary>
    public static float PayFraction(DistrictDef d, double ageHours)
    {
        d ??= Starter;
        return Economy.PickupPayFraction(ageHours, d.onTimeHours, d.lateFloorHours, d.latePayFloor);
    }

    /// <summary>fee × pay fraction + (on time ? tip : 0).</summary>
    public static float PickupPay(DistrictDef d, double ageHours)
    {
        d ??= Starter;
        return d.fee * PayFraction(d, ageHours) + (IsLate(d, ageHours) ? 0f : d.tip);
    }

    public static bool IsLate(DistrictDef d, double ageHours) => ageHours > (d ?? Starter).onTimeHours;
    public static bool IsNeglected(DistrictDef d, double ageHours) => ageHours >= (d ?? Starter).neglectedHours;

    // ------------------------------------------------------------------ the §5.1 / §5.4 table

    static void Ensure()
    {
        if (all != null) return;
        all = new[]
        {
            new DistrictDef
            {
                id = MildlyPleasant, name = "Mildly Pleasant Acres", stopPrefix = "", permitId = null,
                tier = 1, rankRequired = 1, houses = 37, permitPrice = 0f, bagsPerDay = 35f, curbCap = 60,
                fee = Economy.PickupFeePerBag, tip = 0f, curbBonus = Economy.CurbClearedBonus,
                onTimeHours = Economy.OnTimeHours, lateFloorHours = Economy.LateFloorHours, latePayFloor = Economy.LatePayFloor,
                neglectedHours = Economy.NeglectedHours, cleanStreetsMax = 180f, dirtForZero = 30f, neglectFine = 2f,
                landfillPerBag = 0f, repMult = 1f, trustStart = 0.60f, critterMult = 1.0f, butlerDrops = false,
                boundsXZ = CoastalCityLayout.DistrictBounds(MildlyPleasant),
                loot = new LootProfile
                {
                    id = MildlyPleasant, tier = 1, p0 = 0.0050f, p1 = 0.0080f,
                    rarity0 = new[] { 84f, 14f, 1.7f, 0.3f }, rarity1 = new[] { 74f, 20f, 5f, 1f },
                    common0 = 62f, common1 = 50f, keepsakeChance = 0.05f,
                    minItems = 5, maxItems = 10, categoryWeights = null,  // today's 40/18/15/11/10/4
                    tierBoost = new[] { 1f, 1f, 0f, 0f, 0f }, springFlat = 0.012f
                }
            },
            new DistrictDef
            {
                id = MinivanMeadows, name = "Minivan Meadows", stopPrefix = "mm-", permitId = ShopIds.PermitMinivanMeadows,
                tier = 2, rankRequired = 4, houses = 22, permitPrice = 600f, bagsPerDay = 30f, curbCap = 50,
                fee = 45f, tip = 0f, curbBonus = 24f, onTimeHours = 3.5f, lateFloorHours = 10f, latePayFloor = 0.20f,
                neglectedHours = 14f, cleanStreetsMax = 750f, dirtForZero = 26f, neglectFine = 40f,
                landfillPerBag = 0.5f, repMult = 1.5f, trustStart = 0.40f, critterMult = 0.9f, butlerDrops = false,
                boundsXZ = CoastalCityLayout.DistrictBounds(MinivanMeadows),
                loot = new LootProfile
                {
                    id = MinivanMeadows, tier = 2, p0 = 0.0060f, p1 = 0.0070f,
                    rarity0 = new[] { 68f, 25f, 6f, 1f }, rarity1 = new[] { 58f, 30f, 10f, 2f },
                    common0 = 48f, common1 = 38f, keepsakeChance = 0.04f,
                    minItems = 6, maxItems = 11, categoryWeights = new[] { 34f, 22f, 16f, 12f, 11f, 5f },
                    tierBoost = new[] { 1f, 1f, 5f, 0f, 0f }, springFlat = 0.020f
                }
            },
            new DistrictDef
            {
                id = SnootingtonHills, name = "Snootington Hills", stopPrefix = "sh-", permitId = ShopIds.PermitSnootingtonHills,
                tier = 3, rankRequired = 7, houses = 14, permitPrice = 3500f, bagsPerDay = 24f, curbCap = 40,
                fee = 120f, tip = 45f, curbBonus = 90f, onTimeHours = 3f, lateFloorHours = 8f, latePayFloor = 0.10f,
                neglectedHours = 12f, cleanStreetsMax = 2700f, dirtForZero = 21f, neglectFine = 200f,
                landfillPerBag = 3f, repMult = 2f, trustStart = 0.40f, critterMult = 0.85f, butlerDrops = false,
                boundsXZ = CoastalCityLayout.DistrictBounds(SnootingtonHills),
                loot = new LootProfile
                {
                    id = SnootingtonHills, tier = 3, p0 = 0.0068f, p1 = 0.0068f,
                    rarity0 = new[] { 52f, 32f, 13f, 3f }, rarity1 = new[] { 42f, 35f, 18f, 5f },
                    common0 = 34f, common1 = 26f, keepsakeChance = 0.03f,
                    minItems = 7, maxItems = 12, categoryWeights = new[] { 28f, 22f, 12f, 14f, 18f, 6f },
                    tierBoost = new[] { 0.6f, 0.6f, 2f, 8f, 0f }, springFlat = 0.025f
                }
            },
            new DistrictDef
            {
                id = BillionaireBluffs, name = "Billionaire Bluffs", stopPrefix = "bb-", permitId = ShopIds.PermitBillionaireBluffs,
                tier = 4, rankRequired = 10, houses = 8, permitPrice = 12000f, bagsPerDay = 20f, curbCap = 34,
                fee = 240f, tip = 120f, curbBonus = 240f, onTimeHours = 2f, lateFloorHours = 6f, latePayFloor = 0f,
                neglectedHours = 8f, cleanStreetsMax = 4500f, dirtForZero = 17f, neglectFine = 400f,
                landfillPerBag = 12f, repMult = 3f, trustStart = 0.40f, critterMult = 0.8f, butlerDrops = true,
                boundsXZ = CoastalCityLayout.DistrictBounds(BillionaireBluffs),
                loot = new LootProfile
                {
                    id = BillionaireBluffs, tier = 4, p0 = 0.0066f, p1 = 0.0049f,
                    rarity0 = new[] { 55f, 30f, 12f, 3f }, rarity1 = new[] { 47f, 33f, 15f, 5f },
                    common0 = 34f, common1 = 28f, keepsakeChance = 0.03f,
                    minItems = 8, maxItems = 13, categoryWeights = new[] { 24f, 20f, 10f, 16f, 20f, 10f },
                    tierBoost = new[] { 0.25f, 0.25f, 0.8f, 3f, 12f }, springFlat = 0.030f
                }
            },
            new DistrictDef
            {
                id = CouponCommons, name = "Coupon Commons", stopPrefix = "cc-", permitId = ShopIds.PermitCouponCommons,
                tier = 2, rankRequired = 5, houses = 18, permitPrice = 1800f, bagsPerDay = 28f, curbCap = 48,
                fee = 75f, tip = 15f, curbBonus = 54f, onTimeHours = 3.5f, lateFloorHours = 9f, latePayFloor = 0.15f,
                neglectedHours = 13f, cleanStreetsMax = 1500f, dirtForZero = 25f, neglectFine = 90f,
                landfillPerBag = 1.5f, repMult = 1.75f, trustStart = 0.40f, critterMult = 1.1f, butlerDrops = false,
                boundsXZ = CoastalCityLayout.DistrictBounds(CouponCommons),
                loot = new LootProfile
                {
                    id = CouponCommons, tier = 2, p0 = 0.0060f, p1 = 0.0070f,
                    rarity0 = new[] { 68f, 25f, 6f, 1f }, rarity1 = new[] { 58f, 30f, 10f, 2f },
                    common0 = 48f, common1 = 38f, keepsakeChance = 0.04f,
                    minItems = 6, maxItems = 11, categoryWeights = new[] { 25f, 38f, 15f, 10f, 8f, 4f },
                    tierBoost = new[] { 1f, 1f, 5f, 0f, 0f }, springFlat = 0.020f
                }
            },
            new DistrictDef
            {
                id = StackingtonHeights, name = "Stackington Heights", stopPrefix = "st-", permitId = ShopIds.PermitStackingtonHeights,
                tier = 3, rankRequired = 8, houses = 16, permitPrice = 7000f, bagsPerDay = 26f, curbCap = 44,
                fee = 165f, tip = 60f, curbBonus = 135f, onTimeHours = 2.75f, lateFloorHours = 7f, latePayFloor = 0.05f,
                neglectedHours = 10f, cleanStreetsMax = 3450f, dirtForZero = 22f, neglectFine = 275f,
                landfillPerBag = 5f, repMult = 2.4f, trustStart = 0.40f, critterMult = 0.9f, butlerDrops = false,
                boundsXZ = CoastalCityLayout.DistrictBounds(StackingtonHeights),
                loot = new LootProfile
                {
                    id = StackingtonHeights, tier = 3, p0 = 0.0068f, p1 = 0.0068f,
                    rarity0 = new[] { 52f, 32f, 13f, 3f }, rarity1 = new[] { 42f, 35f, 18f, 5f },
                    common0 = 34f, common1 = 26f, keepsakeChance = 0.03f,
                    minItems = 7, maxItems = 12, categoryWeights = new[] { 22f, 18f, 12f, 20f, 23f, 5f },
                    tierBoost = new[] { 0.6f, 0.6f, 2f, 8f, 0f }, springFlat = 0.025f
                }
            },
        };
        byId = new Dictionary<string, DistrictDef>(StringComparer.Ordinal);
        foreach (var d in all) byId[d.id] = d;
        progression = new[] { all[0], all[1], all[4], all[2], all[5], all[3] };
    }

    // Data is immutable, but rebuild per play session so an edited table (domain reload off) is picked up.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all = null; progression = null; byId = null;
        unlockedMask = -1; unlocked = null;
    }
}

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
    public float[] rarity0, rarity1;     // U,R,E,L at w=0 and w=1
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
/// The four districts (Docs/Progression.md §5). STEP 0 CONTRACT, written by builder A; after Step 0 this file belongs to
/// builder D. Company-scoped: "unlocked" = the company owns the permit.
/// Map (all in the one Neighborhood scene, PlayBounds x -60..440, z -40..640; built by NeighborhoodBuilder): Mildly
/// Pleasant is today's loop; Minivan Meadows is east of the depot (Minivan Mile, gate at x = 206); Snootington Hills is
/// north of Birch St (Old Money Rd = Elm Ave continued, gate at z = 230); Billionaire Bluffs is north of Snootington (Tax
/// Haven Heights Rd continues straight up Old Money Rd, boom barrier at (0, 460)).
/// </summary>
public static class Districts
{
    public const string MildlyPleasant = "mildly_pleasant", MinivanMeadows = "minivan_meadows",
                        SnootingtonHills = "snootington_hills", BillionaireBluffs = "billionaire_bluffs";

    static DistrictDef[] all;
    static Dictionary<string, DistrictDef> byId;

    /// <summary>Every district, ordered by tier (1..4).</summary>
    public static IReadOnlyList<DistrictDef> All { get { Ensure(); return all; } }

    /// <summary>Mildly Pleasant Acres (tier 1, always unlocked).</summary>
    public static DistrictDef Starter { get { Ensure(); return all[0]; } }

    /// <summary>Null-safe: unknown or empty ids give the Starter district.</summary>
    public static DistrictDef Get(string id)
    {
        Ensure();
        return !string.IsNullOrEmpty(id) && byId.TryGetValue(id, out var d) ? d : all[0];
    }

    /// <summary>"mm-" / "sh-" / "bb-" stopId prefix, else Starter.</summary>
    public static DistrictDef ForStopId(string stopId)
    {
        Ensure();
        if (!string.IsNullOrEmpty(stopId))
            for (int i = 1; i < all.Length; i++)
                if (!string.IsNullOrEmpty(all[i].stopPrefix) && stopId.StartsWith(all[i].stopPrefix, StringComparison.Ordinal))
                    return all[i];
        return all[0];
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

    /// <summary>World position -> district by <see cref="DistrictDef.boundsXZ"/> (richest first), else Starter.</summary>
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

    /// <summary>Unlocked districts in tier order. A new list instance whenever the set changes (old ones stay valid).</summary>
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
                for (int i = 0; i < all.Length; i++) if ((mask & (1 << i)) != 0) list.Add(all[i]);
                unlocked = list;
                unlockedMask = mask;
            }
            return unlocked;
        }
    }

    /// <summary>The highest-tier unlocked district (Starter at worst).</summary>
    public static DistrictDef Top
    {
        get
        {
            Ensure();
            for (int i = all.Length - 1; i >= 1; i--) if (IsUnlocked(all[i])) return all[i];
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
                neglectedHours = Economy.NeglectedHours, cleanStreetsMax = 60f, dirtForZero = 30f, neglectFine = 2f,
                landfillPerBag = 0f, repMult = 1f, trustStart = 0.60f, critterMult = 1.0f, butlerDrops = false,
                boundsXZ = new Rect(-60f, -40f, 280f, 270f),            // today's PlayBounds x -60..220, z -40..230
                loot = new LootProfile
                {
                    id = MildlyPleasant, tier = 1, p0 = 0.0050f, p1 = 0.0080f,
                    rarity0 = new[] { 84f, 14f, 1.7f, 0.3f }, rarity1 = new[] { 74f, 20f, 5f, 1f },
                    minItems = 5, maxItems = 10, categoryWeights = null,  // today's 40/18/15/11/10/4
                    tierBoost = new[] { 1f, 1f, 0f, 0f, 0f }, springFlat = 0.012f
                }
            },
            new DistrictDef
            {
                id = MinivanMeadows, name = "Minivan Meadows", stopPrefix = "mm-", permitId = ShopIds.PermitMinivanMeadows,
                tier = 2, rankRequired = 4, houses = 22, permitPrice = 600f, bagsPerDay = 30f, curbCap = 50,
                fee = 15f, tip = 0f, curbBonus = 8f, onTimeHours = 3.5f, lateFloorHours = 10f, latePayFloor = 0.20f,
                neglectedHours = 14f, cleanStreetsMax = 250f, dirtForZero = 26f, neglectFine = 40f,
                landfillPerBag = 0.5f, repMult = 1.5f, trustStart = 0.40f, critterMult = 0.9f, butlerDrops = false,
                boundsXZ = new Rect(206f, -40f, 234f, 270f),            // x 206..440, z -40..230 (Oak Ave's outer lots end at x 204)
                loot = new LootProfile
                {
                    id = MinivanMeadows, tier = 2, p0 = 0.0060f, p1 = 0.0070f,
                    rarity0 = new[] { 68f, 25f, 6f, 1f }, rarity1 = new[] { 58f, 30f, 10f, 2f },
                    minItems = 6, maxItems = 11, categoryWeights = new[] { 34f, 22f, 16f, 12f, 11f, 5f },
                    tierBoost = new[] { 1f, 1f, 5f, 0f, 0f }, springFlat = 0.020f
                }
            },
            new DistrictDef
            {
                id = SnootingtonHills, name = "Snootington Hills", stopPrefix = "sh-", permitId = ShopIds.PermitSnootingtonHills,
                tier = 3, rankRequired = 7, houses = 14, permitPrice = 3500f, bagsPerDay = 24f, curbCap = 40,
                fee = 40f, tip = 15f, curbBonus = 30f, onTimeHours = 3f, lateFloorHours = 8f, latePayFloor = 0.10f,
                neglectedHours = 12f, cleanStreetsMax = 900f, dirtForZero = 21f, neglectFine = 200f,
                landfillPerBag = 3f, repMult = 2f, trustStart = 0.40f, critterMult = 0.85f, butlerDrops = false,
                boundsXZ = new Rect(-60f, 230f, 280f, 230f),            // x -60..220, z 230..460
                loot = new LootProfile
                {
                    id = SnootingtonHills, tier = 3, p0 = 0.0068f, p1 = 0.0068f,
                    rarity0 = new[] { 52f, 32f, 13f, 3f }, rarity1 = new[] { 42f, 35f, 18f, 5f },
                    minItems = 7, maxItems = 12, categoryWeights = new[] { 28f, 22f, 12f, 14f, 18f, 6f },
                    tierBoost = new[] { 0.6f, 0.6f, 2f, 8f, 0f }, springFlat = 0.025f
                }
            },
            new DistrictDef
            {
                id = BillionaireBluffs, name = "Billionaire Bluffs", stopPrefix = "bb-", permitId = ShopIds.PermitBillionaireBluffs,
                tier = 4, rankRequired = 10, houses = 8, permitPrice = 12000f, bagsPerDay = 20f, curbCap = 34,
                fee = 80f, tip = 40f, curbBonus = 80f, onTimeHours = 2f, lateFloorHours = 6f, latePayFloor = 0f,
                neglectedHours = 8f, cleanStreetsMax = 1500f, dirtForZero = 17f, neglectFine = 400f,
                landfillPerBag = 12f, repMult = 3f, trustStart = 0.40f, critterMult = 0.8f, butlerDrops = true,
                boundsXZ = new Rect(-20f, 460f, 360f, 180f),            // x -20..340, z 460..640 (Tax Haven Heights Rd runs up x = 0)
                loot = new LootProfile
                {
                    id = BillionaireBluffs, tier = 4, p0 = 0.0066f, p1 = 0.0049f,
                    rarity0 = new[] { 55f, 30f, 12f, 3f }, rarity1 = new[] { 47f, 33f, 15f, 5f },
                    minItems = 8, maxItems = 13, categoryWeights = new[] { 24f, 20f, 10f, 16f, 20f, 10f },
                    tierBoost = new[] { 0.25f, 0.25f, 0.8f, 3f, 12f }, springFlat = 0.030f
                }
            },
        };
        byId = new Dictionary<string, DistrictDef>(StringComparer.Ordinal);
        foreach (var d in all) byId[d.id] = d;
    }

    // Data is immutable, but rebuild per play session so an edited table (domain reload off) is picked up.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all = null; byId = null;
        unlockedMask = -1; unlocked = null;
    }
}

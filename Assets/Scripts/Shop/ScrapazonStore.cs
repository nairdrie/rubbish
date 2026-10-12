using System;
using UnityEngine;

// Locked was added for Docs/Progression.md §9.5 (rank / prereq / Key to the City gates); keep it last so saved or
// serialized values of the older members never shift.
public enum ScrapazonPurchase { Ok, NotFound, NotForSale, MaxedOut, CantAfford, Locked }

/// <summary>
/// Scrapazon purchase logic (UI-free, so debug tools and future systems can buy too). Purchases are
/// instant ("Scrapazon Prime: delivered in 0.3 seconds") and persisted through GameSession's upgrade /
/// consumable store; <see cref="ShopEffects"/> applies what you bought. Money goes through <see cref="Ledger.Spend"/>
/// (company-scoped, excluded from "net" income). Rank / prerequisite gates come from <see cref="Progression.CanBuy"/>.
/// </summary>
public static class ScrapazonStore
{
    /// <summary>(item, new level for upgrades / new pocket count for consumables).</summary>
    public static event Action<ScrapazonItem, int> Purchased;

    /// <summary>Purchases made this session (the header "cart" counter).</summary>
    public static int SessionPurchases { get; private set; }

    public static int Level(string id) => GameSession.GetUpgradeLevel(id);
    public static int Count(string id) => GameSession.GetConsumable(id);

    /// <summary>Upgrade level or consumable count (what the card shows as "owned").</summary>
    public static int Owned(ScrapazonItem it)
    {
        if (it == null) return 0;
        return it.IsUpgrade ? Level(it.id) : it.IsConsumable ? Count(it.id) : 0;
    }

    public static bool IsMaxed(ScrapazonItem it)
    {
        if (it == null || it.IsTeaser) return false;
        return Owned(it) >= Mathf.Max(1, it.maxLevel);
    }

    /// <summary>The 1-based level the next purchase would buy (consumables: always 1).</summary>
    public static int NextLevel(ScrapazonItem it)
    {
        if (it == null) return 1;
        return it.IsUpgrade ? Mathf.Clamp(Level(it.id) + 1, 1, Mathf.Max(1, it.maxLevel)) : 1;
    }

    /// <summary>What the next purchase costs, or -1 when it can't be bought (teaser / maxed).</summary>
    public static float NextPrice(ScrapazonItem it)
    {
        if (it == null || it.IsTeaser || IsMaxed(it) || it.prices == null || it.prices.Length == 0) return -1f;
        int i = it.IsUpgrade ? Mathf.Clamp(Level(it.id), 0, it.prices.Length - 1) : 0;
        return it.prices[i];
    }

    public static bool CanAfford(ScrapazonItem it)
    {
        float p = NextPrice(it);
        return p >= 0f && GameSession.Cash + 0.0001f >= p;
    }

    /// <summary>
    /// True when the next level is gated by rank, a prerequisite or the Key to the City (never for teasers / maxed
    /// items). <paramref name="whyNot"/>: "Requires Rank 6: Hopper Whisperer", "Requires Grabby Arm Lv 1", ...
    /// </summary>
    public static bool IsLocked(ScrapazonItem it, out string whyNot)
    {
        whyNot = null;
        if (it == null || it.IsTeaser || IsMaxed(it)) return false;
        if (!Progression.CanBuy(it, NextLevel(it), out whyNot)) return true;
        return it.id == ShopIds.TruckModel && !TruckProgression.CanTradeIn(out whyNot);
    }

    public static bool IsLocked(ScrapazonItem it) => IsLocked(it, out _);

    /// <summary>Total upgrade levels + consumables in your pocket (fun header number).</summary>
    public static int TotalStuffOwned()
    {
        int n = 0;
        foreach (var it in ScrapazonCatalog.All) n += Owned(it);
        return n;
    }

    /// <summary>Everything Scrapazon actually sells (not teasers).</summary>
    public static int ProductCount()
    {
        int n = 0;
        foreach (var it in ScrapazonCatalog.All) if (!it.IsTeaser) n++;
        return n;
    }

    public static ScrapazonPurchase TryBuy(string id) => TryBuy(id, out _);

    /// <summary>Buy one of item id: spends cash, bumps the level / pocket count, triggers the effect.</summary>
    public static ScrapazonPurchase TryBuy(string id, out string message)
    {
        var it = ScrapazonCatalog.Get(id);
        if (it == null) { message = "that product doesn't exist (yet)"; return ScrapazonPurchase.NotFound; }
        if (it.IsTeaser)
        {
            message = $"{it.teaserLine}. we'll notify you (we won't)";
            return ScrapazonPurchase.NotForSale;
        }
        if (IsMaxed(it))
        {
            message = it.IsUpgrade ? "you already own the best one. stop." : "your pockets are full. use some first.";
            return ScrapazonPurchase.MaxedOut;
        }
        if (IsLocked(it, out string why))
        {
            message = why;
            return ScrapazonPurchase.Locked;
        }

        float price = NextPrice(it);
        if (!Ledger.Spend(price, $"{Scrapazon.Name}: {ShortName(it)}", LedgerIds.Purchase))
        {
            message = $"payment declined. your card is also garbage. ({Economy.Money(price - GameSession.Cash)} short)";
            return ScrapazonPurchase.CantAfford;
        }

        int now;
        if (it.IsUpgrade)
        {
            now = Level(it.id) + 1;
            GameSession.SetUpgradeLevel(it.id, now);
        }
        else
        {
            GameSession.AddConsumable(it.id, 1);
            now = Count(it.id);
        }
        SessionPurchases++;
        message = $"{Scrapazon.Name} Prime: {ShortName(it)} delivered in 0.3 seconds";

        // Progression side effects first (permit → district unlocked, gnome → timer), then the item's own effect.
        try { Progression.OnPurchased(it, now); } catch (Exception e) { Debug.LogException(e); }
        try { ShopEffects.OnPurchased(it, now); } catch (Exception e) { Debug.LogException(e); }
        try { Purchased?.Invoke(it, now); } catch (Exception e) { Debug.LogException(e); }
        return ScrapazonPurchase.Ok;
    }

    /// <summary>Short display name for toasts and lock messages ("Rabies Shot", "Grabby Arm").</summary>
    public static string ShortName(ScrapazonItem it)
    {
        if (it == null) return "thing";
        switch (it.id)
        {
            case ShopIds.RabiesShot: return "Rabies Shot";
            case ShopIds.Repellent: return "Raccoon-Be-Gone";
            case ShopIds.TruckModel: return "Rear-loader Truck";
            case DumpsterRules.UpgradeId: return "Dumpster Dunker";
            case HogShop.Hoverhog: return "Hoverhog";
            case HogShop.Belly: return "Hoverhog Belly";
            case HogShop.Claw: return "Hoverhog Claw";
            case HogShop.Rotors: return "Hoverhog Rotors";
            case HogShop.Battery: return "Hoverhog Battery";
            case HogShop.SatPad: return "Satellite Pad";
            case ShopIds.TruckCapacity: return "Hopper Extension";
            case ShopIds.TruckTurbo: return "TURBO";
            case ShopIds.AirHorn: return "Air Horn";
            case ShopIds.ConveyorBelt: return "Conveyor Belt";
            case ShopIds.MagnusBrain: return "Magnus Brain";
            case ShopIds.TruckHandling: return "Sticky Tires";
            case ShopIds.TruckArm: return "Grabby Arm";
            case ShopIds.TruckHeadlights: return "Headlights";
            case ShopIds.AutoTipper: return "Auto-Tipper";
            case ShopIds.SortingTable: return "XL Sorting Table";
            case ShopIds.CompactorMk2: return "Compactor Mk II";
            case ShopIds.RecyclingContracts: return "Recycling Contract";
            case ShopIds.HazmatContract: return "Hazmat License";
            case ShopIds.TrophyShelf: return "Trophy Shelf";
            case ShopIds.DisplayPlankShelf: return "Plank Shelf";
            case ShopIds.DisplayGlassCase: return "Glass Case";
            case ShopIds.DisplayPedestal: return "Pedestal";
            case ShopIds.DisplayVault: return "Legendary Vault";
            case ShopIds.MagnusRailMotor: return "Magnus Rail Motor";
            case ShopIds.MagnusGripper: return "Magnus Gripper";
            case ShopIds.MagnusVision: return "Magnus Vision";
            case ShopIds.BagCannon: return "Bag Cannon";
            case ShopIds.PermitMinivanMeadows: return "Minivan Meadows Permit";
            case ShopIds.PermitCouponCommons: return "Coupon Commons Permit";
            case ShopIds.PermitSnootingtonHills: return "Snootington Hills Permit";
            case ShopIds.PermitStackingtonHeights: return "Stackington Heights Permit";
            case ShopIds.PermitBillionaireBluffs: return "Billionaire Bluffs Permit";
            case ShopIds.EbaePowerSeller: return "ebag Power Seller";
            case ShopIds.GoldenTruck: return "Golden Garbage Truck";
            case ShopIds.GarbageJuice: return "Garbage Juice";
            case ShopIds.LuckyGnome: return "Lucky Gnome";
            case ShopIds.GoldenGnome: return "Golden Gnome";
            case ShopIds.TruckPaintPrimer: return "Primer Paint";
            case ShopIds.TruckPaintFlames: return "Flames Paint";
            case ShopIds.TruckPaintRaccoonCamo: return "Raccoon Camo Paint";
            case ShopIds.TruckPaintChrome: return "Chrome Paint";
            case ShopIds.HornPackMusical: return "Musical Horn Pack";
            case ShopIds.TrashFmRadio: return "Trash FM Radio";
            case ShopIds.GarageDiscoBall: return "Disco Ball";
            case ShopIds.FranchiseMode: return "Franchise Mode";
            default: return it.title;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Purchased = null;
        SessionPurchases = 0;
    }
}

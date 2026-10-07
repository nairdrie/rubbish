using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Applies what you bought on Scrapazon, and the read-only API other systems use to honor it.
/// A DontDestroyOnLoad <see cref="ShopEffectsRunner"/> (auto-created in play mode) re-applies every effect
/// on scene load, player spawn and truck spawn:
///   truck_capacity -> GameSession.SetTruckCapacity(UpgradeEffects.CapacityKg[level]) (200/240/280/340 kg)
///   truck_* -> on every TruckControllerV2: <see cref="ShopTruckMods"/> (absolute engine tiers, horn L1/L2, headlights,
///              hopper ram), <see cref="TruckHandlingMods"/>, <see cref="TruckRecovery"/> (Hold R), <see cref="TruckSideLoader"/>
///              (Grabby Arm), <see cref="GoldenTruckFx"/> (golden truck, paints, horn pack, radio)
///   gear -> <see cref="GearMods"/> on the local player (gloves + Back Sack, boots, wand, UV lamp, Garbage Juice);
///           rabies_vaccine -> GameSession.InfectionResist01; hard_hat -> read by CritterKnockback
///   headlamp -> <see cref="ShopHeadlamp"/> on PlayerController.Local (on at night)
/// All numbers come from <see cref="UpgradeEffects"/> (Docs/Progression.md §6).
///   rabies_shot -> bought while infected: used instantly (RabiesStatus.CureWithShot via reflection, fallback
///                  GameSession.CureRabies); otherwise stored and auto-used ~1.5 s after an infection (CritterSpawner
///                  does the same; whichever runs first wins, a shot is never spent twice)
///   repellent -> consumed by the Critters system on demand (CritterRepellent: the first bite/bonk of a night
///                spritzes a bottle, protected until morning); the Shop only sells it and shows stink lines while active
/// Everything here is local (single-player early game); no networking.
/// </summary>
public static class ShopEffects
{
    // ---------------- tuning (Docs/LivingWorld.md §4) ----------------
    public const float TurboTorqueMultiplier = 1.35f;
    public const float TurboSpeedMultiplier = 1.25f;
    public const float BaseHornScareRadius = 12f;
    public const float AirHornScareRadius = 25f;     // air_horn L1 (UpgradeEffects.HornScareRadius is the source of truth)
    public const float FoghornScareRadius = 40f;     // air_horn L2, Foghorn of the Gods

    // ---------------- read-only API for other systems ----------------
    public static bool HasTurbo => GameSession.GetUpgradeLevel(ShopIds.TruckTurbo) > 0;
    public static int AirHornLevel => GameSession.GetUpgradeLevel(ShopIds.AirHorn);
    public static bool HasAirHorn => AirHornLevel > 0;
    public static bool HasHeadlamp => GameSession.GetUpgradeLevel(ShopIds.Headlamp) > 0;
    public static int TruckCapacityLevel => GameSession.GetUpgradeLevel(ShopIds.TruckCapacity);
    public static int RabiesShots => GameSession.GetConsumable(ShopIds.RabiesShot);
    public static int RepellentBottles => GameSession.GetConsumable(ShopIds.Repellent);
    public static int GarbageJuiceCans => GameSession.GetConsumable(ShopIds.GarbageJuice);

    /// <summary>Radius (m) in which a truck honk scares critters: 12 / 25 / 40 m by air_horn level (UpgradeEffects).</summary>
    public static float HornScareRadius => UpgradeEffects.HornScareRadius;

    /// <summary>Hopper kg for a truck_capacity level: 160 / 200 / 240 / 280 / 340 (the UpgradeEffects table, not a formula).</summary>
    public static float TruckCapacityFor(int level)
    {
        var t = UpgradeEffects.CapacityKg;
        return t[Mathf.Clamp(level, 0, t.Length - 1)];
    }

    /// <summary>
    /// True while a spritzed bottle of Eau de Raccoon-Be-Gone is protecting the player (until morning). Mirrors
    /// the Critters system's CritterRepellent.IsActive (read by reflection so the Shop never breaks the build if
    /// Critters renames things); false when that isn't available.
    /// </summary>
    public static bool RepellentActive => ReadCritterBool("CritterRepellent", "IsActive");

    /// <summary>Would a critter be repelled right now (active, or a bottle in your pocket to spritz)?</summary>
    public static bool RepellentWouldProtect => RepellentActive || RepellentBottles > 0;

    // ---------------- purchase hook ----------------

    /// <summary>Called by ScrapazonStore right after a successful purchase (level/count already updated).</summary>
    public static void OnPurchased(ScrapazonItem it, int now)
    {
        if (it == null) return;
        bool quiet = EbaeUI.IsOpen; // the laptop shows its own celebration; HUD toasts would hide behind it

        switch (it.id)
        {
            case ShopIds.TruckCapacity:
                ApplyTruckCapacity();
                if (!quiet) GameHUD.Toast($"Hopper extended! Truck holds {GameSession.TruckCapacityKg:0} kg now", ToastStyle.Good);
                break;
            case ShopIds.RabiesShot:
                if (GameSession.HasRabies)
                    ShopEffectsRunner.ScheduleRabiesShot(quiet ? 1.25f : 0.15f);
                else if (!quiet)
                    GameHUD.Toast("Rabies Shot stored. It auto-injects if something rabid bites you.", ToastStyle.Good);
                break;
            case ShopIds.Repellent:
                if (!quiet)
                    GameHUD.Toast("Raccoon-Be-Gone in your pocket. It spritzes itself the moment a critter tries something.", ToastStyle.Good);
                break;
            case ShopIds.Headlamp:
            case ShopIds.TruckTurbo:
            case ShopIds.AirHorn:
                ApplyAll();
                if (!quiet) GameHUD.Toast(InstallMessage(it, now), ToastStyle.Good, 3f);
                break;
            case ShopIds.GoldenTruck:
                ApplyAll();
                GoldenTruckFx.PlayDelivery();
                break;
            default:
                // Every other id (Docs/Progression.md §6): effects re-apply from the level (idempotent), then a toast.
                ApplyAll();
                if (!quiet) GameHUD.Toast(InstallMessage(it, now), ToastStyle.Good, 3f);
                break;
        }

        ScrapazonBox.Deliver(it);
    }

    /// <summary>"{name} installed!" plus how to use it, for the toast after a purchase.</summary>
    public static string InstallMessage(ScrapazonItem it, int now)
    {
        if (it == null) return "";
        string name = ScrapazonStore.ShortName(it);
        // Level 2+ often has its own product name ("Twin-Turbo 'Stinkmaster' V8"); level 1 keeps the short name.
        if (now >= 2 && it.levelTitles != null && now <= it.levelTitles.Length && !string.IsNullOrEmpty(it.levelTitles[now - 1]))
            name = it.levelTitles[now - 1];
        switch (it.id)
        {
            case ShopIds.TruckTurbo:
                return $"{name} installed! Top speed {UpgradeEffects.Engine.maxSpeed:0.#} m/s. Flames: yes.";
            case ShopIds.AirHorn:
                return now >= 2 ? $"{name} installed! Honk (H) scares critters {FoghornScareRadius:0} m away. And the sky."
                                : $"{name} installed! Honk (H) scares critters within {AirHornScareRadius:0} m.";
            case ShopIds.Headlamp:
                return now >= 2 ? $"{name} installed! At night, some treasure bags glow purple." : $"{name} installed! It clicks on by itself at night.";
            case ShopIds.TruckHandling:
                return now >= 3 ? $"{name} installed! Hold Space while driving to DRIFT." :
                       now == 2 ? $"{name} installed! Tony's anti-tip keeps you upright under 12 m/s." :
                                  $"{name} installed! Flipped or stuck? Hold R to unflip. Free.";
            case ShopIds.TruckArm:
                return now >= 3 ? $"{name} installed! X toggles AUTO. Drive the centre line under 4 m/s: both curbs + litter."
                     : now == 2 ? $"{name} installed! X toggles AUTO. Crawl past curbs under 3 m/s, both sides."
                                : $"{name} installed! Stop beside a curb bag (right side) and HOLD X.";
            case ShopIds.TruckHeadlights:
                return now >= 2 ? $"{name} installed! Night is now a Tuesday afternoon. Raccoons hate it."
                                : $"{name} installed! Raccoons in the beam run for it.";
            case ShopIds.WorkGloves:
                return now >= 3 ? $"{name} on! No carry slowdown. Two bags on your back."
                     : now == 2 ? $"{name} on! Carrying a bag, press E on another: the first goes on your back."
                                : $"{name} on! Heavy bags slow you down half as much. Throws go further.";
            case ShopIds.WorkBoots:
                return now >= 2 ? $"{name} on! +20% speed. Ctrl to STOMP critters away." : $"{name} on! +10% walk and sprint. Squeak squeak.";
            case ShopIds.TreasureWand:
                return now >= 2 ? $"{name} acquired! Beeps at bling when you grab a bag or walk past one." : $"{name} acquired! It beeps when you grab a bag with treasure in it. Sometimes.";
            case ShopIds.RabiesVaccine:
                return now >= 2 ? $"{name} administered! You're immune to rabies. Smug." : $"{name} administered! Rabid bites only infect you half as often.";
            case ShopIds.HardHat:
                return $"{name} on! Critter bonks knock you half as far, and you often keep hold of your bag.";
            case ShopIds.BagCannon:
                return $"{name} bolted on! Hold a bag within ~14 m of your truck and HOLD R to aim, let go to FWOOMP.";
            case ShopIds.GrabberPole:
                return $"{name} acquired! Press V: grab far bags (over fences), poke friends, snatch their bags.";
            case ShopIds.GarbageJuice:
                return $"Garbage Juice™ in your pocket ({GarbageJuiceCans}). Press J to chug: +25% speed for 2 hours.";
            case ShopIds.HornPackMusical:
                return $"{name} installed! Honk (H) for a little tune.";
            case ShopIds.TrashFmRadio:
                return $"{name} installed! TRASH FM plays while you drive.";
        }
        if (it.IsConsumable) return $"{name} added to your pocket!";
        if (it.dept == "permits") return $"{name} acquired!";
        return $"{name} installed!";
    }

    // ---------------- appliers ----------------

    /// <summary>Re-apply every owned effect (scene load / spawn / debug). Safe to call any time.</summary>
    public static void ApplyAll()
    {
        ApplyTruckCapacity();
        ApplyInfectionResist();
        if (Application.isPlaying) ShopEffectsRunner.EnsureAttached();
    }

    /// <summary>rabies_vaccine: GameSession.InfectionResist01 (not saved; re-applied on spawn / load / purchase).</summary>
    public static void ApplyInfectionResist()
    {
        GameSession.InfectionResist01 = UpgradeEffects.InfectionResist;
    }

    public static void ApplyTruckCapacity()
    {
        float want = TruckCapacityFor(TruckCapacityLevel);
        if (Mathf.Abs(GameSession.TruckCapacityKg - want) > 0.01f) GameSession.SetTruckCapacity(want);
    }

    // ---------------- rabies ----------------

    static bool cureLookedUp;
    static MethodInfo cureMethod;

    /// <summary>
    /// Use one Rabies Shot from your pocket if you're infected. Prefers the Critters system's
    /// RabiesStatus.CureWithShot() (giant syringe moment); falls back to GameSession.CureRabies() plus our
    /// own "OWWW. Cured!" juice. Returns false if not infected or no shot in your pocket.
    /// </summary>
    public static bool TryUseRabiesShot()
    {
        if (!GameSession.HasRabies || RabiesShots <= 0) return false;

        int before = RabiesShots;
        double infectedAt = GameSession.RabiesInfectedAtHours;
        var outcome = InvokeCritterCure();
        if (outcome == CureOutcome.NotInfected) return false;
        if (outcome == CureOutcome.Unavailable)
        {
            GameSession.CureRabies();
            CureJuice();
        }
        // Don't double-spend if something else already took the shot out of the pocket.
        if (RabiesShots >= before) GameSession.AddConsumable(ShopIds.RabiesShot, -1);
        if (outcome == CureOutcome.Cured && GameSession.HasRabies) ShopEffectsRunner.WatchCure(infectedAt);
        return true;
    }

    enum CureOutcome { Unavailable, Cured, NotInfected }

    static CureOutcome InvokeCritterCure()
    {
        try
        {
            if (!cureLookedUp)
            {
                cureLookedUp = true;
                Type t = FindType("RabiesStatus");
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "CureWithShot" && Array.TrueForAll(m.GetParameters(), p => p.IsOptional))
                        { cureMethod = m; break; }
                if (cureMethod == null) Debug.Log("[Shop] RabiesStatus.CureWithShot() not found; Rabies Shots use GameSession.CureRabies().");
            }
            if (cureMethod == null) return CureOutcome.Unavailable;
            var ps = cureMethod.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
            object r = cureMethod.Invoke(null, args);
            return r is bool ok && !ok ? CureOutcome.NotInfected : CureOutcome.Cured;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Shop] RabiesStatus.CureWithShot() failed, curing directly: {e.GetBaseException().Message}");
            return CureOutcome.Unavailable;
        }
    }

    static readonly System.Collections.Generic.Dictionary<string, PropertyInfo> boolProps =
        new System.Collections.Generic.Dictionary<string, PropertyInfo>();

    /// <summary>Read a public static bool property of another system's class by name (false if missing).</summary>
    static bool ReadCritterBool(string typeName, string property)
    {
        string key = typeName + "." + property;
        try
        {
            if (!boolProps.TryGetValue(key, out var prop))
            {
                var t = FindType(typeName);
                prop = t?.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.PropertyType != typeof(bool)) prop = null;
                boolProps[key] = prop;
            }
            return prop != null && (bool)prop.GetValue(null);
        }
        catch { return false; }
    }

    static Type FindType(string name)
    {
        var t = Type.GetType(name);
        if (t != null) return t;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    internal static void CureJuice()
    {
        GameHUD.Toast("OWWW. Cured!", ToastStyle.Good, 3f);
        Sfx.Play("syringe");
        Sfx.Play("ow", null, 1f, 1.1f);
        Juice.Shake(0.3f, 0.25f);
        var p = PlayerController.Local;
        if (p != null)
        {
            Vector3 head = p.transform.position + Vector3.up * 2.0f;
            GameHUD.FloatingText(head, "OWWW!", new Color(1f, 0.35f, 0.35f), 1.5f);
            Fx.Burst(head, new Color(0.55f, 1f, 0.45f), 24, 3f);
            Fx.Sparkle(p.transform, new Color(0.6f, 1f, 0.6f), 1.5f);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cureLookedUp = false;
        cureMethod = null;
        boolProps.Clear();
    }
}

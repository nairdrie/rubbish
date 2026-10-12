using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// How clean the city is, 0..1 (Trash system), PER DISTRICT (Docs/Progression.md §3.3). Each district's value comes from
/// its curb bags weighted by age in that district's windows — fresh bags barely count, late ones a lot, neglected
/// piles most — plus loose litter lying in that district (loose items, and stray bags with no curb record such as a load
/// tipped in the street: <see cref="StreetLitter"/>), against the district's DirtForZero (30 / 26 / 21 / 17).
/// <see cref="Value01"/> (the HUD meter, critter pressure) is the bags-per-day-weighted average of the UNLOCKED
/// districts, so with only the starter suburb open it is exactly the old city-wide number.
///
/// Midnight (<see cref="MidnightInspection"/>, called once per rollover by the CurbTrashSpawner on the host) pays each
/// unlocked district's Clean Streets bonus = Max_d × clamp01((clean_d − 0.50) / 0.45) × Streak_d × CleanCity, where
/// Streak_d = min(1.5, 1 + 0.1 × consecutive previous midnights with that district ≥ 90 %) (GameSession counter
/// "clean_streak_{id}") and CleanCity = 0.4 + 0.6 × clamp01((worst unlocked district − 0.30) / 0.60); then it fines
/// every neglected curb (district's neglect fine), all through the Ledger, and fills a MidnightReport.
///
/// Reference points (starter suburb, DirtForZero = 30): 8 mostly-fresh bags ≈ 93 %; 20 bags averaging a few hours late
/// ≈ 50 %; 15 neglected piles' worth of bags or 60 late bags ≈ 0 %. A burst bag's litter (~7 items) ≈ -3.5 %.
/// </summary>
public static class CityCleanliness
{
    /// <summary>Starter suburb's dirt at which it reads 0 % (each district has its own: DistrictDef.dirtForZero).</summary>
    public const float DirtForZero = 30f;
    public const float LitterDirt = 0.15f;         // per loose item lying in the Neighborhood
    /// <summary>Starter suburb's Clean Streets maximum (each district has its own: DistrictDef.cleanStreetsMax).</summary>
    public const float CleanStreetsBonusMax = 60f;
    /// <summary>A district at or above this at midnight extends its clean streak (+10 % bonus per night, up to +50 %).</summary>
    public const float StreakThreshold = 0.9f;

    /// <summary>Fired on the host when the midnight bonus is paid (total amount, city cleanliness 0..1).</summary>
    public static event Action<float, float> CleanStreetsBonusPaid;

    /// <summary>The last midnight's report (null before the first midnight of the session).</summary>
    public static MidnightReport LastReport { get; private set; }

    static float cached = 1f;
    static float cachedAt = -999f;
    static int litterCount;
    static float[] districtDirt = new float[4];
    static float[] districtClean = { 1f, 1f, 1f, 1f };
    static int[] districtLitter = new int[4];

    /// <summary>0 = filthy, 1 = spotless (bags-per-day-weighted average of the unlocked districts).</summary>
    public static float Value01
    {
        get
        {
            // Cheap, but the HUD + critters poll it every frame: recompute at most 4× a second.
            if (cachedAt < 0f || Time.unscaledTime - cachedAt >= 0.25f) Recompute();
            return cached;
        }
    }

    /// <summary>Same as <see cref="Value01"/> (the name the HUD / critter probes look for first).</summary>
    public static float Clean01 => Value01;

    /// <summary>1 - Value01 (critter spawn pressure).</summary>
    public static float Dirtiness01 => 1f - Value01;

    public static int Percent => Mathf.RoundToInt(Value01 * 100f);

    /// <summary>One district's cleanliness 0..1 (its own bags, litter and DirtForZero). Locked districts read 1.</summary>
    public static float District01(string districtId)
    {
        _ = Value01;
        int i = IndexOf(districtId);
        return i >= 0 ? districtClean[i] : 1f;
    }

    /// <summary>One district's dirt (curb bags + litter) right now.</summary>
    public static float DistrictDirt(string districtId)
    {
        _ = Value01;
        int i = IndexOf(districtId);
        return i >= 0 ? districtDirt[i] : 0f;
    }

    /// <summary>The worst unlocked district's cleanliness (what the Clean City factor looks at).</summary>
    public static float Worst01
    {
        get
        {
            _ = Value01;
            float w = 1f;
            // A district under contract is meant to start filthy: it doesn't drag everybody's Clean Streets bonus down (Campaign).
            foreach (var d in Districts.All) if (DistrictAccess.OpenInTown(d) && !Campaign.IsUnderContract(d.id)) w = Mathf.Min(w, District01(d.id));
            return w;
        }
    }

    /// <summary>0.4 + 0.6 × clamp01((worst unlocked district − 0.30) / 0.60): one dirty district shrinks EVERY district's
    /// Clean Streets bonus.</summary>
    public static float CleanCityFactor => 0.4f + 0.6f * Mathf.Clamp01((Worst01 - 0.30f) / 0.60f);

    /// <summary>Consecutive previous midnights this district was ≥ 90 % clean (GameSession counter).</summary>
    public static int CleanStreak(string districtId) => GameSession.GetCounter(StreakKey(districtId));

    /// <summary>The streak multiplier tonight's bonus would get: min(1.5, 1 + 0.1 × streak).</summary>
    public static float StreakMult(string districtId) => Mathf.Min(1.5f, 1f + 0.1f * CleanStreak(districtId));

    public static string StreakKey(string districtId) => "clean_streak_" + (districtId ?? Districts.MildlyPleasant);

    /// <summary>"City: 87% clean" / "City: 31% - STINKY".</summary>
    public static string Label
    {
        get
        {
            int p = Percent;
            if (p >= 90) return $"City: {p}% clean - sparkling!";
            if (p >= 60) return $"City: {p}% clean";
            if (p >= 35) return $"City: {p}% - getting smelly";
            return $"City: {p}% - STINKY";
        }
    }

    /// <summary>Meter color: green → yellow → orange → red.</summary>
    public static Color Color => ColorFor(Value01);

    /// <summary>Meter color for any cleanliness value 0..1.</summary>
    public static Color ColorFor(float v)
    {
        if (v >= 0.6f) return Color.Lerp(CurbTrash.SoonColor, CurbTrash.FreshColor, Mathf.InverseLerp(0.6f, 0.9f, v));
        if (v >= 0.35f) return Color.Lerp(CurbTrash.LateColor, CurbTrash.SoonColor, Mathf.InverseLerp(0.35f, 0.6f, v));
        return Color.Lerp(CurbTrash.NeglectedColor, CurbTrash.LateColor, Mathf.InverseLerp(0.1f, 0.35f, v));
    }

    /// <summary>Loose items lying around the Neighborhood, stray bags counted as their contents (last known count while you're elsewhere).</summary>
    public static int LitterCount { get { _ = Value01; return litterCount; } }

    /// <summary>Dirt contributed by one curb bag of this age (starter-suburb windows).</summary>
    public static float BagDirt(double ageHours) => BagDirt(Districts.Starter, ageHours);

    /// <summary>Dirt contributed by one curb bag of this age in its district's windows.</summary>
    public static float BagDirt(DistrictDef d, double ageHours)
    {
        d ??= Districts.Starter;
        float a = (float)ageHours;
        if (a <= d.onTimeHours) return Mathf.Lerp(0.1f, 0.25f, a / d.onTimeHours);
        if (a <= d.lateFloorHours) return Mathf.Lerp(0.25f, 1f, Mathf.InverseLerp(d.onTimeHours, d.lateFloorHours, a));
        if (a < d.neglectedHours) return Mathf.Lerp(1f, 1.5f, Mathf.InverseLerp(d.lateFloorHours, d.neglectedHours, a));
        return Mathf.Min(3f, 2f + 0.05f * (a - d.neglectedHours));
    }

    /// <summary>Total dirt right now (curb bags + litter, all districts).</summary>
    public static float TotalDirt
    {
        get { _ = Value01; return lastDirt; }
    }
    static float lastDirt;

    static int IndexOf(string districtId)
    {
        var all = Districts.All;
        for (int i = 0; i < all.Count; i++) if (all[i].id == districtId) return i;
        return string.IsNullOrEmpty(districtId) ? 0 : -1;
    }

    static int IndexOf(DistrictDef d)
    {
        var all = Districts.All;
        for (int i = 0; i < all.Count; i++) if (all[i] == d) return i;
        return 0;
    }

    static void Recompute()
    {
        cachedAt = Time.unscaledTime;
        var all = Districts.All;
        if (districtDirt.Length != all.Count)
        {
            districtDirt = new float[all.Count]; districtClean = new float[all.Count]; districtLitter = new int[all.Count];
        }
        for (int i = 0; i < all.Count; i++) districtDirt[i] = 0f;

        double now = GameClock.TotalHours;
        var bags = GameSession.CurbBags;
        // The district under contract (Campaign) counts each OLD bag as an equal share of "all the mess", so its bar moves with
        // every bag taken away instead of sitting at 0% until the last few; every other bag, and every other district, is by age.
        var contract = Campaign.Active;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null) continue;
            var d = Districts.ForStopId(b.stopId);
            districtDirt[IndexOf(d)] += contract != null && contract.districtId == d.id && CampaignRules.IsBacklogBagId(b.bagId)
                ? Campaign.HeapDirtPerBag(d)
                : BagDirt(d, Math.Max(0, now - b.putOutAtHours));
        }

        // Litter: only counted in the Neighborhood (the scene with the TrashRoute); remembered while away.
        var route = TrashRoute.Current;
        if (route != null)
        {
            var scene = route.gameObject.scene;
            for (int i = 0; i < districtLitter.Length; i++) districtLitter[i] = 0;
            int n = 0;
            var loose = LooseItem.All;
            for (int i = 0; i < loose.Count; i++)
            {
                var li = loose[i];
                if (li == null || li.State == CarryState.Consumed || li.State == CarryState.Carried) continue;
                if (li.gameObject.scene != scene) continue;
                n++;
                districtLitter[IndexOf(Districts.AtPosition(li.transform.position))]++;
            }
            // Stray bags on the street (a load tipped outdoors, a bag carried out of a shop): each counts as its contents
            // lying loose. Curb bags are counted above by their records; bags in hands, trucks or shops are not litter.
            var carryables = Carryable.All;
            for (int i = 0; i < carryables.Count; i++)
            {
                if (!(carryables[i] is TrashBag bag) || bag.Contents == null || bag.HasBurst) continue;
                if (bag.gameObject.scene != scene) continue;
                Vector3 at = bag.transform.position;
                bool held = bag.State == CarryState.Consumed || bag.State == CarryState.Carried;
                if (!StreetLitter.Counts(bag.IsCurbBag, bag.SourceStop != null, held, DepotRowLayout.InteriorAt(at) >= 0)) continue;
                int worth = StreetLitter.ItemEquivalents(bag.Contents.items != null ? bag.Contents.items.Count : 0);
                n += worth;
                districtLitter[IndexOf(Districts.AtPosition(at))] += worth;
            }
            litterCount = n;
        }

        float total = 0f, weighted = 0f, weights = 0f;
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            districtDirt[i] += districtLitter[i] * LitterDirt;
            total += districtDirt[i];
            districtClean[i] = Mathf.Clamp01(1f - districtDirt[i] / Mathf.Max(1f, d.dirtForZero));
            if (!DistrictAccess.OpenInTown(d)) { districtClean[i] = 1f; continue; }
            float w = Mathf.Max(0.01f, d.bagsPerDay);
            weighted += districtClean[i] * w;
            weights += w;
        }
        lastDirt = total;
        cached = weights > 0f ? Mathf.Clamp01(weighted / weights) : districtClean[0];
    }

    /// <summary>Force a recompute on the next read.</summary>
    public static void Invalidate() { cachedAt = -999f; }

    // ------------------------------------------------------------------ midnight

    /// <summary>Legacy name: runs <see cref="MidnightInspection"/> (pays bonuses and fines) without raising the fact.</summary>
    [Obsolete("Use MidnightInspection (the CurbTrashSpawner runs it once per rollover).")]
    public static void PayCleanStreetsBonus() => MidnightInspection();

    /// <summary>
    /// The midnight inspection (host, once per day rollover): per unlocked district pays the Clean Streets bonus and the
    /// neglect fines through the Ledger, updates the clean streaks, toasts the result and returns the report (the
    /// Midnight fact is the spawner's next step; Docs/Progression.md §9.3 midnight order).
    /// </summary>
    public static MidnightReport MidnightInspection()
    {
        if (!Businesses.IsAuthority) return LastReport; // guests never mint or charge a second inspection
        Invalidate();
        _ = Value01;
        var report = new MidnightReport
        {
            day = Mathf.Max(1, GameClock.Day - 1),          // the day that just ended
            bagsIntoTruckToday = CurbTrash.TakeBagsIntoTruckToday(),
        };

        float factor = CleanCityFactor;
        float totalBonus = 0f;
        var unlocked = Districts.ProgressionOrder;
        var parts = new StringBuilder();
        var businesses = MunicipalEconomy.OperatingBusinesses();
        var bonuses = new long[businesses.Count];
        var fines = new long[businesses.Count];
        int neglectedTotal = 0;
        foreach (var d in unlocked)
        {
            if (!DistrictAccess.OpenInTown(d)) continue;
            float clean = District01(d.id);
            int streakNights = CleanStreak(d.id);
            float streak = Mathf.Min(1.5f, 1f + 0.1f * streakNights);
            float bonus = Mathf.Round(d.cleanStreetsMax * Mathf.Clamp01((clean - 0.50f) / 0.45f) * streak * factor * 100f) / 100f;
            if (bonus < 0.01f) bonus = 0f;
            totalBonus += bonus;

            // No neglect fines or Rep losses for the district under contract: its heaps are the job (Campaign).
            int neglected = Campaign.IsUnderContract(d.id) ? 0 : CurbTrash.NeglectedStopsIn(d);
            neglectedTotal += neglected;
            long bonusPool = GameSession.ToCents(bonus), finePool = GameSession.ToCents(neglected * d.neglectFine);
            for (int i = 0; i < businesses.Count; i++)
            {
                var business = businesses[i];
                long paid = MunicipalEconomy.Share(bonusPool, businesses.Count, i);
                long charged = MunicipalEconomy.FineShare(finePool, businesses.Count, i, business.municipalHours, fines[i], Districts.Top.tier);
                using (Ledger.ToBusiness(business.Id))
                {
                    if (paid > 0) Ledger.Credit(paid / 100f, $"City bonus: 1/{businesses.Count} operating businesses", LedgerIds.CleanStreets, null, d.id);
                    if (charged > 0) Ledger.Charge(charged / 100f,
                        $"{FineReason(d)}: {neglected} curbs x {Economy.Money(d.neglectFine)} / {businesses.Count} businesses (relief/cap applied)",
                        LedgerIds.NeglectFine, null, d.id);
                }
                bonuses[i] += paid;
                fines[i] += charged;
                report.finesPaid += charged / 100f;
            }

            GameSession.SetCounter(StreakKey(d.id), clean >= StreakThreshold ? streakNights + 1 : 0);
            report.clean01[d.id] = clean;
            report.neglectedCurbs[d.id] = neglected;
            report.cleanBonusPaid[d.id] = bonus;

            int pct = Mathf.RoundToInt(clean * 100f);
            if (parts.Length > 0) parts.Append(" · ");
            parts.Append(unlocked.Count == 1 ? $"city {pct}% clean" : $"{ShortName(d)} {pct}%");
            if (streakNights > 0 && bonus > 0f) parts.Append($" (streak x{streak:0.0})");
        }
        LastReport = report;

        // Each owner sees their actual share, not the host's receipt or the undivided town pool.
        for (int i = 0; i < businesses.Count; i++)
        {
            var b = businesses[i];
            string relief = MunicipalEconomy.InGrace(b.municipalHours) ? "Startup grace: neglect fines waived."
                : b.municipalHours < MunicipalEconomy.RookieHours ? "Starter relief: half fines, $10/night maximum."
                : $"Nightly fine cap {Economy.Money(MunicipalEconomy.NightCap(b.municipalHours, Districts.Top.tier) / 100f)}.";
            string message = $"{b.Name} — midnight city inspection\nYour share (1/{businesses.Count} operating businesses): +{Economy.Money(bonuses[i] / 100f)} bonus, -{Economy.Money(fines[i] / 100f)} neglect fines.\n{neglectedTotal} neglected curbs town-wide. {relief}\nHired crews count under their employer; waived fines aren't passed to others.";
            if (Businesses.Registry != null) Businesses.Registry.MunicipalReceipt(b.ownerClientId, message, fines[i] > 0);
            else MunicipalEconomy.ShowReceipt(message, fines[i] > 0);
        }
        foreach (var b in Businesses.All)
        {
            if (!Payroll.TryGetJobOf(b.ownerClientId, out var job)) continue;
            Businesses.Registry?.MunicipalReceipt(b.ownerClientId,
                $"Midnight city inspection: {Payroll.BusinessLabelOf(job.employerClientId)} receives your crew's bonus/bill. Your own business has no municipal charge while you're hired.", false);
        }

        try { CleanStreetsBonusPaid?.Invoke(totalBonus, Value01); } catch (Exception e) { Debug.LogException(e); }
        return report;
    }

    static string FineReason(DistrictDef d) => d.tier switch
    {
        1 => "Neglect fine",
        2 => "HOA letter",
        3 => "HOA tribunal",
        _ => "Estate complaint",
    };

    /// <summary>Short district name for HUD lines ("Meadows", "Snootington", ...).</summary>
    public static string ShortName(DistrictDef d)
    {
        if (d == null) return "";
        switch (d.id)
        {
            case Districts.MildlyPleasant: return "Mildly Pleasant";
            case Districts.MinivanMeadows: return "Meadows";
            case Districts.CouponCommons: return "Coupon Commons";
            case Districts.SnootingtonHills: return "Snootington";
            case Districts.StackingtonHeights: return "Stackington";
            case Districts.BillionaireBluffs: return "Bluffs";
            default: return d.name;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        CleanStreetsBonusPaid = null;
        LastReport = null;
        cached = 1f; cachedAt = -999f;
        litterCount = 0; lastDirt = 0f;
        for (int i = 0; i < districtDirt.Length; i++) { districtDirt[i] = 0f; districtClean[i] = 1f; districtLitter[i] = 0; }
    }
}

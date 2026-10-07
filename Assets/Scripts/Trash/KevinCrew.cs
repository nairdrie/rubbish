using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Kevin's Crew (Docs/Progression.md §6.5): "the truck collects garbage by itself later", at city scale. Data only, no
/// driving AI, HOST ONLY (driven by the CurbTrashSpawner's Update, which only runs on the host).
///   - His houses: the first ⌈coverage × subscribed houses⌉ of each covered district, ordered by
///     StableHash("kevin" + stopId). Coverage = UpgradeEffects.KevinCoverage(district tier) (hire_kevin L1: 60 % of
///     Mildly Pleasant; L2: all of it + 50 % of Meadows; L3: + all of Meadows + 40 % of Snootington; never the Bluffs).
///   - Sweeps at 08:00, 14:00 and 20:00: every curb bag at his houses goes. Each pays fee × PayFraction(age, district
///     windows) × Economy.KevinFeeShare (70 %) through the Ledger, raises BagCollected (byKevin = true, A halves the
///     Rep), and goes to GameSession.DepotDeliveries (builder C drops them on the tipping floor). 5 % burst on the way
///     (deterministic per bag): no fee, gone.
///   - A bag a player is carrying (or has thrown) is never taken: you can still beat Kevin to his houses.
///   - Wage: UpgradeEffects.KevinWage, charged at midnight (step 2 of the midnight order).
/// </summary>
public static class KevinCrew
{
    public static readonly float[] SweepHours = { 8f, 14f, 20f };

    /// <summary>Fired after each sweep (host): bags collected, fees paid, bags burst.</summary>
    public static event Action<int, float, int> Swept;

    /// <summary>Bags Kevin collected this session (debug / fun stat).</summary>
    public static int BagsThisSession { get; private set; }

    static double lastTick = -1;

    /// <summary>Kevin is on the payroll (hire_kevin ≥ 1).</summary>
    public static bool Hired => UpgradeEffects.Level(ShopIds.HireKevin) > 0;

    /// <summary>A new server session (the clock restarted): sweeps resume from now.</summary>
    public static void BeginSession(double nowHours) => lastTick = nowHours;

    /// <summary>Host tick: runs every sweep time that passed since the last tick (a debug skip may pass several).</summary>
    public static void Tick(double nowHours)
    {
        if (lastTick < 0 || nowHours < lastTick) { lastTick = nowHours; return; }
        if (nowHours - lastTick > 48.0) lastTick = nowHours - 48.0;
        int d0 = (int)Math.Floor(lastTick / 24.0), d1 = (int)Math.Floor(nowHours / 24.0);
        for (int day = d0; day <= d1; day++)
            foreach (float h in SweepHours)
            {
                double t = day * 24.0 + h;
                if (t > lastTick && t <= nowHours) Sweep(t);
            }
        lastTick = nowHours;
    }

    // ------------------------------------------------------------------ his houses

    static readonly Dictionary<string, bool> servedCache = new Dictionary<string, bool>();
    static int servedFrame = -1, servedLevel = -1;
    static readonly List<(int hash, string stopId)> tmpOrder = new List<(int, string)>();

    /// <summary>True when Kevin sweeps this house (covered district, subscribed, among his first ⌈coverage × N⌉).</summary>
    public static bool Serves(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return false;
        EnsureServed();
        return servedCache.TryGetValue(stopId, out bool v) && v;
    }

    /// <summary>How many houses Kevin covers in a district right now.</summary>
    public static int HousesServed(DistrictDef d)
    {
        if (d == null) return 0;
        EnsureServed();
        int n = 0;
        foreach (var kv in servedCache) if (kv.Value && Districts.ForStopId(kv.Key) == d) n++;
        return n;
    }

    static void EnsureServed()
    {
        int lvl = UpgradeEffects.Level(ShopIds.HireKevin);
        if (servedFrame == Time.frameCount && servedLevel == lvl) return;
        servedFrame = Time.frameCount; servedLevel = lvl;
        servedCache.Clear();
        if (lvl <= 0) return;
        var houses = CurbTrashSpawner.Houses;
        foreach (var d in Districts.All)
        {
            float cov = UpgradeEffects.KevinCoverage(d.tier);
            if (cov <= 0f || !Districts.IsUnlocked(d)) continue;
            tmpOrder.Clear();
            for (int i = 0; i < houses.Count; i++)
            {
                var h = houses[i];
                if (h == null || h.districtId != d.id || !Progression.IsSubscribed(h.stopId)) continue;
                tmpOrder.Add((PickupVisuals.StableHash("kevin" + h.stopId), h.stopId));
            }
            tmpOrder.Sort((a, b) => a.hash != b.hash ? a.hash.CompareTo(b.hash) : string.CompareOrdinal(a.stopId, b.stopId));
            int take = Mathf.Min(tmpOrder.Count, Mathf.CeilToInt(cov * tmpOrder.Count - 1e-4f));
            for (int i = 0; i < tmpOrder.Count; i++) servedCache[tmpOrder[i].stopId] = i < take;
        }
    }

    // ------------------------------------------------------------------ sweep

    public struct SweepResult { public int collected, burst; public float fees; }

    static readonly List<CurbBag> tmpBags = new List<CurbBag>();

    /// <summary>Kevin sweeps his houses now (atHours = the sweep time). Host only. Returns what happened.</summary>
    public static SweepResult Sweep(double atHours)
    {
        var res = new SweepResult();
        if (!Hired) return res;
        EnsureServed();

        tmpBags.Clear();
        foreach (var b in GameSession.CurbBags) if (b != null && Serves(b.stopId) && !HeldByAPlayer(b)) tmpBags.Add(b);
        if (tmpBags.Count == 0) { Announce(res, null, atHours); return res; }

        var feesByDistrict = new Dictionary<DistrictDef, float>();
        var streetCount = new Dictionary<string, int>();
        foreach (var b in tmpBags)
        {
            var d = Districts.ForStopId(b.stopId);
            double age = Math.Max(0, atHours - b.putOutAtHours);
            GameSession.RemoveCurbBag(b.bagId);
            bool burst = UpgradeEffects.DetectRoll(b.bagId, "kevin_burst", Economy.KevinBurstChance);
            if (burst) { res.burst++; continue; }

            float pay = d.fee * Districts.PayFraction(d, age) * Economy.KevinFeeShare;
            feesByDistrict[d] = (feesByDistrict.TryGetValue(d, out var f) ? f : 0f) + pay;
            res.fees += pay;
            res.collected++;
            TownWorkDirector.Collected();
            if (b.contents != null) GameSession.AddDepotDelivery(b.contents);

            var house = CurbTrashSpawner.FindHouse(b.stopId);
            string street = StreetOf(house != null ? house.address : b.contents != null ? b.contents.foundAt : null);
            if (!string.IsNullOrEmpty(street)) streetCount[street] = (streetCount.TryGetValue(street, out var sc) ? sc : 0) + 1;

            try
            {
                ProgressionEvents.RaiseBagCollected(new BagCollectedFact
                {
                    bagId = b.bagId, stopId = b.stopId, districtId = d.id, ageHours = age, onTime = !Districts.IsLate(d, age),
                    byArm = false, byKevin = true, pay = pay, pos = house != null ? house.position : Vector3.zero,
                });
            }
            catch (Exception e) { Debug.LogException(e); }
        }
        CurbTrash.Invalidate();
        CityCleanliness.Invalidate();

        foreach (var kv in feesByDistrict)
            if (kv.Value > 0f) Ledger.Credit(Mathf.Round(kv.Value * 100f) / 100f, "Kevin's Crew collected", LedgerIds.KevinFees, null, kv.Key.id);

        BagsThisSession += res.collected;
        Announce(res, WhereText(streetCount), atHours);
        try { Swept?.Invoke(res.collected, res.fees, res.burst); } catch (Exception e) { Debug.LogException(e); }
        return res;
    }

    /// <summary>A bag being carried / thrown / tossed is the player's: Kevin leaves it alone.</summary>
    static bool HeldByAPlayer(CurbBag rec)
    {
        var stop = PickupStop.Find(rec.stopId);
        if (stop == null) return false;
        var bags = stop.SpawnedBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var tb = bags[i];
            if (tb == null || tb.Contents == null || tb.Contents.bagId != rec.bagId) continue;
            return tb.State != CarryState.Resting || tb.IsInScriptedFlight;
        }
        return false;
    }

    static string StreetOf(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        int sp = address.IndexOf(' ');
        return sp > 0 && sp < address.Length - 1 && char.IsDigit(address[0]) ? address.Substring(sp + 1) : address;
    }

    static readonly string[] Greetings =
    {
        "He says hi.", "He lost a shoe again.", "He found a sandwich. He ate the sandwich.", "He waved at a raccoon. It waved back.",
        "He'd like you to know he's 'killing it'.", "He named one of the bags Gerald.",
    };
    static readonly string[] Naps =
    {
        "Kevin swept his houses. Nothing out. He took a nap in the hedge.",
        "Kevin's route was spotless. He practised his wave instead.",
    };

    /// <summary>" on Oak Ave", " on Oak Ave and Elm St", or " across 5 streets (mostly Oak Ave)": a sweep spans many
    /// streets, so naming only the busiest one read as if he had skipped the rest.</summary>
    static string WhereText(Dictionary<string, int> streetCount)
    {
        if (streetCount == null || streetCount.Count == 0) return "";
        string top = null, second = null; int best = 0, next = 0;
        foreach (var kv in streetCount)
        {
            if (kv.Value > best || (kv.Value == best && string.CompareOrdinal(kv.Key, top) < 0))
            { second = top; next = best; top = kv.Key; best = kv.Value; }
            else if (kv.Value > next || (kv.Value == next && string.CompareOrdinal(kv.Key, second) < 0))
            { second = kv.Key; next = kv.Value; }
        }
        if (streetCount.Count == 1) return $" on {top}";
        if (streetCount.Count == 2) return $" on {top} and {second}";
        return $" across {streetCount.Count} streets (mostly {top})";
    }

    static void Announce(SweepResult r, string where, double atHours)
    {
        int hint = (int)(Math.Floor(atHours) % 97);
        if (r.collected == 0 && r.burst == 0)
        {
            if (Hired && hint % 3 == 0) GameHUD.Toast(Naps[hint % Naps.Length], ToastStyle.Info, 3f);
            return;
        }
        where = where ?? "";
        string burst = r.burst > 0 ? $" ({r.burst} burst on the way. Oops.)" : "";
        GameHUD.Toast($"Kevin collected {r.collected} bag{(r.collected == 1 ? "" : "s")}{where}. {Greetings[hint % Greetings.Length]} +{Economy.Money(r.fees)}{burst}",
                      ToastStyle.Good, 4f);
        Sfx.Play("cash", null, 0.7f, 1.1f);
        Sfx.Play("horn", null, 0.25f, 1.6f);   // a tiny distant toot from his van
    }

    // ------------------------------------------------------------------ wage

    /// <summary>Midnight step 2 (host): pay Kevin. Returns the wage charged (0 when he isn't hired).</summary>
    public static float ChargeWage(MidnightReport report)
    {
        float wage = UpgradeEffects.KevinWage;
        if (wage <= 0f || !Hired) return 0f;
        Ledger.Charge(wage, "Kevin's wage", LedgerIds.KevinWage);
        int lvl = UpgradeEffects.Level(ShopIds.HireKevin);
        string who = lvl >= 3 ? "The Kevins (Unionized)" : lvl == 2 ? "Kevin + Other Kevin" : "Kevin";
        GameHUD.Toast($"Paid {who} {Economy.Money(wage)}. {(lvl >= 3 ? "They bought matching hats." : "He bought a hat.")}", ToastStyle.Info, 3.5f);
        return wage;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Swept = null;
        BagsThisSession = 0;
        lastTick = -1;
        servedCache.Clear();
        servedFrame = -1; servedLevel = -1;
    }
}

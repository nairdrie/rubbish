using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared rules + helpers for curb trash (Trash system). Everything that reads GameSession.CurbBags goes through
/// here so the marker colors, HUD route panel, pay, cleanliness and the route planner all agree:
///   - freshness: green (fresh) -> yellow (going late soon) -> orange (late) -> red (neglected pile)
///   - labels: "3 bags · 2 h late"
///   - per-bag route urgency (bags close to going late or already late score highest)
///   - collecting a bag into the truck (lateness-scaled fee, record removal, "Curb clean!" bonus)
///   - per-house stats (bag count, oldest age, late count, kg), cached once per frame.
/// Ages are in-game hours (GameClock; 1 h = 50 real seconds). Every window (on time, pay floor, neglected) is the
/// bag's DISTRICT's (Docs/Progression.md §5.1); the one-argument helpers use the starter suburb's, which are today's
/// Economy constants. Collecting raises ProgressionEvents.BagCollected / CurbCleared and pays through the Ledger.
/// </summary>
public static class CurbTrash
{
    /// <summary>True only while a synchronous collection fact belongs to this screen's actor.</summary>
    public static bool ActorFeedbackActive { get; private set; } = true;

    public struct CollectFeedback
    {
        public bool onTime, curbCleared, tipped;
        public long curbBonusCents;
        public string label, address;
        public Vector3 curbFxPos;
    }

    // ------------------------------------------------------------------ colors
    public static readonly Color FreshColor = new Color(0.35f, 1f, 0.3f);      // just put out
    public static readonly Color SoonColor = new Color(1f, 0.9f, 0.15f);       // about to go late
    public static readonly Color LateColor = new Color(1f, 0.55f, 0.08f);      // late (pay decaying)
    public static readonly Color NeglectedColor = new Color(1f, 0.18f, 0.14f); // neglected pile (stinks)

    // Colour-blind-safe ramp (Okabe–Ito), used while Settings → Accessibility → Colour-blind-safe markers is on
    // (Docs/UIPass.md §6.2). Shapes go with it (● fresh, ◆ late soon, ▲ late, ✖ stinky: UiKit.Sprites.Glyph).
    public static readonly Color CbFreshColor = new Color(0x56 / 255f, 0xB4 / 255f, 0xE9 / 255f);     // sky blue
    public static readonly Color CbSoonColor = new Color(0xF0 / 255f, 0xE4 / 255f, 0x42 / 255f);      // yellow
    public static readonly Color CbLateColor = new Color(0xE6 / 255f, 0x9F / 255f, 0x00 / 255f);      // orange
    public static readonly Color CbNeglectedColor = new Color(0xD5 / 255f, 0x5E / 255f, 0x00 / 255f); // vermilion

    static Color Fresh => GameSettings.ColorblindMarkers ? CbFreshColor : FreshColor;
    static Color Soon => GameSettings.ColorblindMarkers ? CbSoonColor : SoonColor;
    static Color Late => GameSettings.ColorblindMarkers ? CbLateColor : LateColor;
    static Color Neglected => GameSettings.ColorblindMarkers ? CbNeglectedColor : NeglectedColor;

    /// <summary>The colour of a freshness stage in the current palette (legends, empty markers).</summary>
    public static Color StageColor(global::FreshnessStage s) => s switch
    {
        global::FreshnessStage.LateSoon => Soon,
        global::FreshnessStage.Late => Late,
        global::FreshnessStage.Stinky => Neglected,
        _ => Fresh,
    };

    /// <summary>Hours before going late at which a stop counts as "late soon!" (yellow).</summary>
    public const float SoonWindowHours = 1.5f;

    /// <summary>Marker / HUD color for a bag (or a stop's oldest bag) of this age (starter-suburb windows).</summary>
    public static Color FreshnessColor(double ageHours) => FreshnessColor(Districts.Starter, ageHours);

    /// <summary>Marker / HUD color for a bag of this age in a district (its own on-time / floor / neglect windows).</summary>
    public static Color FreshnessColor(DistrictDef d, double ageHours)
    {
        d ??= Districts.Starter;
        float a = (float)ageHours;
        float onTime = d.onTimeHours;
        Color fresh = Fresh, soon = Soon, late = Late, neglected = Neglected;
        if (a <= onTime - SoonWindowHours - 0.5f) return fresh;
        if (a <= onTime) return Color.Lerp(fresh, soon, Mathf.InverseLerp(onTime - SoonWindowHours - 0.5f, onTime - SoonWindowHours + 0.25f, a));
        if (a < d.neglectedHours)
            return Color.Lerp(late, Color.Lerp(late, neglected, 0.45f), Mathf.InverseLerp(d.lateFloorHours, d.neglectedHours, a));
        return neglected;
    }

    /// <summary>The freshness stage of a bag of this age in a district (the colour-blind shape; same cut-offs as
    /// <see cref="FreshnessLevel(DistrictDef, double)"/>).</summary>
    public static global::FreshnessStage FreshnessStage(DistrictDef d, double ageHours) => (global::FreshnessStage)FreshnessLevel(d, ageHours);

    /// <summary>Starter-suburb <see cref="FreshnessStage(DistrictDef, double)"/>.</summary>
    public static global::FreshnessStage FreshnessStage(double ageHours) => (global::FreshnessStage)FreshnessLevel(ageHours);

    /// <summary>0 fresh, 1 late soon, 2 late, 3 neglected (starter-suburb windows).</summary>
    public static int FreshnessLevel(double ageHours) => FreshnessLevel(Districts.Starter, ageHours);

    /// <summary>0 fresh, 1 late soon, 2 late, 3 neglected (district windows).</summary>
    public static int FreshnessLevel(DistrictDef d, double ageHours)
    {
        d ??= Districts.Starter;
        if (ageHours >= d.neglectedHours) return 3;
        if (ageHours > d.onTimeHours) return 2;
        if (ageHours > d.onTimeHours - SoonWindowHours) return 1;
        return 0;
    }

    public static bool IsLate(double ageHours) => ageHours > Economy.OnTimeHours;
    public static bool IsNeglected(double ageHours) => ageHours >= Economy.NeglectedHours;
    public static bool IsLate(DistrictDef d, double ageHours) => Districts.IsLate(d, ageHours);
    public static bool IsNeglected(DistrictDef d, double ageHours) => Districts.IsNeglected(d, ageHours);

    /// <summary>"fresh" / "late soon!" / "2 h late" / "18 h late · STINKY" (starter-suburb windows).</summary>
    public static string AgeText(double ageHours) => AgeText(Districts.Starter, ageHours);

    /// <summary>"fresh" / "late soon!" / "2 h late" / "18 h late · STINKY" (district windows).</summary>
    public static string AgeText(DistrictDef d, double ageHours)
    {
        d ??= Districts.Starter;
        if (!IsLate(d, ageHours)) return ageHours > d.onTimeHours - SoonWindowHours ? "late soon!" : "fresh";
        int late = Mathf.Max(1, Mathf.RoundToInt((float)(ageHours - d.onTimeHours)));
        return IsNeglected(d, ageHours) ? $"{late} h late · STINKY" : $"{late} h late";
    }

    /// <summary>"3 bags · 2 h late" (age = the oldest bag's age; starter-suburb windows).</summary>
    public static string StopLabel(int bags, double oldestAgeHours) => StopLabel(Districts.Starter, bags, oldestAgeHours);

    /// <summary>"3 bags · 2 h late" (district windows).</summary>
    public static string StopLabel(DistrictDef d, int bags, double oldestAgeHours)
    {
        if (bags <= 0) return "all clean!";
        return $"{bags} bag{(bags == 1 ? "" : "s")} · {AgeText(d, oldestAgeHours)}";
    }

    // ------------------------------------------------------------------ pay + urgency

    /// <summary>Pickup fee for a bag of this age in the starter suburb (full $4 on time, decaying to 25 % by 12 h).</summary>
    public static float PayFor(double ageHours) => Economy.PickupFeePerBag * Economy.PickupPayFraction(ageHours);

    /// <summary>Pickup pay for a bag of this age in a district: fee × pay fraction + (on time ? tip : 0).</summary>
    public static float PayFor(DistrictDef d, double ageHours) => Districts.PickupPay(d, ageHours);

    /// <summary>
    /// Route-planner urgency of one bag (starter-suburb windows). Fresh bags ramp up as they approach the on-time limit;
    /// late bags (pay decaying right now) score highest per bag; neglected piles stay high (they stink up the city
    /// and grow Pile Goblins).
    /// </summary>
    public static float Urgency(double ageHours) => Urgency(Districts.Starter, ageHours);

    /// <summary>Route-planner urgency of one bag in its district's windows.</summary>
    public static float Urgency(DistrictDef d, double ageHours)
    {
        d ??= Districts.Starter;
        float a = (float)ageHours;
        float onTime = d.onTimeHours;
        if (a <= onTime) { float t = a / onTime; return 0.35f + 0.75f * t * t; }        // 0.35 .. 1.10
        if (a <= d.lateFloorHours) return 1.3f;                                          // losing $ every hour
        if (a < d.neglectedHours) return 1.15f;                                          // pay at the floor
        return 1.45f;                                                                    // neglected: stinks
    }

    /// <summary>
    /// How much more a district's curbs are worth to the route planner than the starter suburb's: √((fee + tip) / $4).
    /// 1 in Mildly Pleasant (so its routing is unchanged), ≈ 1.9 / 3.7 / 5.5 in the richer districts.
    /// </summary>
    public static float PayWeight(DistrictDef d)
    {
        if (d == null) return 1f;
        return Mathf.Sqrt(Mathf.Max(0.25f, (d.fee + d.tip) / Mathf.Max(0.01f, Economy.PickupFeePerBag)));
    }

    /// <summary>Planner bonus for being able to leave a curb completely clean (the $2 bonus).</summary>
    public const float CurbCleanAppeal = 0.35f;

    // ------------------------------------------------------------------ per-stop stats (cached per frame)

    public struct StopStats
    {
        public int bags;
        public int lateBags;
        public double oldestAgeHours;
        public float urgency;     // sum of Urgency over the bags
        public float kg;          // total mass sitting at the curb
        public int usedSlotMask;  // bit i = slot i taken
    }

    static readonly Dictionary<string, StopStats> stats = new Dictionary<string, StopStats>();
    static readonly Dictionary<DistrictDef, int> districtBags = new Dictionary<DistrictDef, int>();
    static int statsFrame = -1, statsCount = -1, statsVersion = -1;
    static double statsHours = double.NaN;
    static int version;
    static int totalLate, totalNeglectedStops;

    /// <summary>Call after editing CurbBag records in place (e.g. putOutAtHours) so stats rebuild now.</summary>
    public static void Invalidate() => version++;

    static void EnsureStats()
    {
        var bags = GameSession.CurbBags;
        double now = GameClock.TotalHours;
        bool sameTime = !double.IsNaN(statsHours) && System.Math.Abs(now - statsHours) < 0.01;
        if (statsFrame == Time.frameCount && statsCount == bags.Count && statsVersion == version && sameTime) return;
        statsFrame = Time.frameCount; statsCount = bags.Count; statsVersion = version; statsHours = now;
        stats.Clear();
        districtBags.Clear();
        totalLate = 0;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null || string.IsNullOrEmpty(b.stopId)) continue;
            double age = System.Math.Max(0, now - b.putOutAtHours);
            var d = Districts.ForStopId(b.stopId);
            districtBags[d] = (districtBags.TryGetValue(d, out var dn) ? dn : 0) + 1;
            stats.TryGetValue(b.stopId, out var s);
            s.bags++;
            if (IsLate(d, age)) { s.lateBags++; totalLate++; }
            if (age > s.oldestAgeHours) s.oldestAgeHours = age;
            s.urgency += Urgency(d, age);
            s.kg += b.contents != null ? b.contents.TotalMassKg : 6f;
            if (b.slot >= 0 && b.slot < 31) s.usedSlotMask |= 1 << b.slot;
            stats[b.stopId] = s;
        }
        totalNeglectedStops = 0;
        foreach (var kv in stats) if (IsNeglected(Districts.ForStopId(kv.Key), kv.Value.oldestAgeHours)) totalNeglectedStops++;
    }

    /// <summary>Stats for one house (all zero when its curb is clean).</summary>
    public static StopStats StatsFor(string stopId)
    {
        EnsureStats();
        return stopId != null && stats.TryGetValue(stopId, out var s) ? s : default;
    }

    /// <summary>Bags at every curb in town that are past the on-time window.</summary>
    public static int LateBagCount { get { EnsureStats(); return totalLate; } }
    /// <summary>Houses whose oldest bag is older than their district's neglect window.</summary>
    public static int NeglectedStopCount { get { EnsureStats(); return totalNeglectedStops; } }
    /// <summary>Houses in one district whose oldest bag is past that district's neglect window (midnight fines).</summary>
    public static int NeglectedStopsIn(DistrictDef d)
    {
        EnsureStats();
        if (d == null) return 0;
        int n = 0;
        foreach (var kv in stats)
        {
            var sd = Districts.ForStopId(kv.Key);
            if (sd == d && kv.Value.bags > 0 && IsNeglected(sd, kv.Value.oldestAgeHours)) n++;
        }
        return n;
    }

    /// <summary>Bags at the curbs of one district (cached with the per-house stats).</summary>
    public static int BagsIn(DistrictDef d)
    {
        if (d == null) return 0;
        EnsureStats();
        return districtBags.TryGetValue(d, out var n) ? n : 0;
    }

    /// <summary>Houses with at least one bag out.</summary>
    public static int StopsWithTrashCount { get { EnsureStats(); return stats.Count; } }

    /// <summary>Oldest curb bag in town (0 when the streets are clean).</summary>
    public static double OldestAgeHours
    {
        get
        {
            EnsureStats();
            double o = 0;
            foreach (var kv in stats) if (kv.Value.oldestAgeHours > o) o = kv.Value.oldestAgeHours;
            return o;
        }
    }

    /// <summary>Records at one house, sorted by slot.</summary>
    public static void BagsAt(string stopId, List<CurbBag> into)
    {
        into.Clear();
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++) if (bags[i] != null && bags[i].stopId == stopId) into.Add(bags[i]);
        into.Sort((a, b) => a.slot.CompareTo(b.slot));
    }

    /// <summary>Lowest free curb slot at a house, or -1 when its curb is full.</summary>
    public static int FreeSlot(string stopId, int capacity)
    {
        int mask = StatsFor(stopId).usedSlotMask;
        for (int i = 0; i < capacity; i++) if ((mask & (1 << i)) == 0) return i;
        return -1;
    }

    // ------------------------------------------------------------------ collection

    static bool lateHintShown;
    static readonly Color[] Confetti =
    {
        new Color(1f, 0.85f, 0.2f), new Color(0.3f, 1f, 0.45f), new Color(0.35f, 0.7f, 1f), new Color(1f, 0.4f, 0.75f)
    };

    /// <summary>
    /// WorldLife grudge hook (Docs/WorldLife.md §9.1, W0): extra in-game hours a house's bags are aged by for PAY only
    /// (stopId → hours; null or 0 = none). While it's &gt; 0 the bag pays fee × PayFraction(age + extra) and the on-time tip
    /// is void. Lateness, the BagCollected fact (and its Rep) and the label still use the real age.
    /// </summary>
    public static System.Func<string, float> ExtraAgeHours;

    static float GrudgeHours(string stopId)
    {
        var f = ExtraAgeHours;
        if (f == null || string.IsNullOrEmpty(stopId)) return 0f;
        try { return Mathf.Max(0f, f(stopId)); }
        catch (System.Exception e) { Debug.LogException(e); return 0f; }
    }

    /// <summary>Curb bags that went into the truck (by hand or the side-loader arm, not Kevin) since the last midnight.
    /// Reset by the midnight inspection (MidnightReport.bagsIntoTruckToday).</summary>
    public static int BagsIntoTruckToday { get; private set; }

    /// <summary>Called by the midnight inspection: returns today's count and starts a new day.</summary>
    public static int TakeBagsIntoTruckToday()
    {
        int n = BagsIntoTruckToday;
        BagsIntoTruckToday = 0;
        return n;
    }

    /// <summary>
    /// A curb bag went into the truck: pays its district's fee × PayFraction(age) (+ the tip when on time; floating
    /// "+$4.00 On time!" / "+$1.60 Late…" at <paramref name="fxPos"/>), removes its CurbBag record, pays the district's
    /// curb bonus when that was the house's last bag, and raises the BagCollected / CurbCleared facts. Returns the pay
    /// (0 if the bag had no record, e.g. a bag from the shop pile). Host-side, local only.
    /// </summary>
    public static float CollectIntoTruck(BagContents contents, Vector3 fxPos) => CollectIntoTruck(contents, fxPos, false);

    /// <summary>As <see cref="CollectIntoTruck(BagContents, Vector3)"/>; byArm = the side-loader arm put it in.</summary>
    public static float CollectIntoTruck(BagContents contents, Vector3 fxPos, bool byArm) =>
        CollectIntoTruck(contents, fxPos, byArm, true, out _);

    /// <summary>Same authoritative payment, with actor-only HUD separated from nearby shared-world effects.</summary>
    public static float CollectIntoTruck(BagContents contents, Vector3 fxPos, bool byArm, bool actorFeedback, out CollectFeedback feedback)
    {
        feedback = default;
        if (contents == null || string.IsNullOrEmpty(contents.bagId)) return 0f;
        // Co-op (N1): only the host collects and pays; a guest's toss is a request (WorldState.RequestHopper).
        if (GameSession.IsConnectedGuest) return 0f;
        var rec = GameSession.FindCurbBag(contents.bagId);
        if (rec == null) return 0f;

        var d = Districts.ForStopId(rec.stopId);
        double age = GameClock.AgeHours(rec.putOutAtHours);
        bool onTime = !IsLate(d, age);
        float grudge = GrudgeHours(rec.stopId);
        // A grudge (WorldLife): paid as if older, and no on-time tip. Without one this is exactly PayFor(d, age).
        float pay = grudge > 0f ? d.fee * Districts.PayFraction(d, age + grudge) : PayFor(d, age);
        float tip = onTime && grudge <= 0f ? d.tip : 0f;
        string label = Economy.LatenessLabel(age, d.onTimeHours, d.lateFloorHours);
        feedback.onTime = onTime;
        feedback.tipped = tip > 0f;
        feedback.label = label;
        bool nearby = PlayerController.Local != null && FeedbackAudience.Nearby( // coop:allow(local) local observer's spatial FX only
            (PlayerController.Local.transform.position - fxPos).sqrMagnitude, 28f); // coop:allow(local) local observer's spatial FX only
        GameSession.RemoveCurbBag(rec.bagId);
        version++;
        BagsIntoTruckToday++;
        TownWorkDirector.Collected();

        // Pay (no worldPos: we show our own floating text with the lateness label). Reason strings are what the HUD's
        // cash ticker parses for its lateness colour ("On time!", "A bit late", "Late", "Super late").
        Ledger.Credit(pay, onTime ? label : label + "…", LedgerIds.PickupFee, null, d.id);
        Color c = onTime ? FreshColor : FreshnessColor(d, age);
        string text = onTime ? $"+{Economy.Money(pay)} {label}" : $"+{Economy.Money(pay)} {label}…";
        if (grudge > 0f) text += " (grudge)";
        if (actorFeedback) GameHUD.FloatingText(fxPos + Vector3.up * 1.15f, text, c, onTime ? 1.2f : 1f);
        if (nearby) Sfx.Play("cash", fxPos, onTime ? 0.9f : 0.6f, onTime ? 1.15f : 0.85f);
        if (onTime && nearby) Sfx.Play("ding", fxPos, 0.5f, 1.35f);
        else if (!onTime && actorFeedback && !lateHintShown)
        {
            lateHintShown = true;
            GameHUD.Toast("Late trash pays less! Follow the arrow — it goes for the most urgent curbs first.", ToastStyle.Info, 4f);
        }
        if (tip > 0f)
        {
            // Rich districts tip for punctuality: a second, golden floater + a coin shower.
            if (actorFeedback) GameHUD.FloatingText(fxPos + Vector3.up * 1.75f, d.butlerDrops ? $"+{Economy.Money(tip)} butler tip!" : $"incl. {Economy.Money(tip)} tip!",
                                                     new Color(1f, 0.85f, 0.3f), 0.9f);
            if (nearby) { Fx.Burst(fxPos + Vector3.up * 0.8f, new Color(1f, 0.82f, 0.2f), 10, 3.5f); Sfx.Play("cash", fxPos, 0.7f, 1.45f); }
        }

        bool previousActorFeedback = ActorFeedbackActive;
        ActorFeedbackActive = actorFeedback;
        try
        {
            ProgressionEvents.RaiseBagCollected(new BagCollectedFact
            {
                bagId = rec.bagId, stopId = rec.stopId, districtId = d.id, ageHours = age, onTime = onTime,
                byArm = byArm, byKevin = false, pay = pay, pos = fxPos,
            });
        }
        catch (System.Exception e) { Debug.LogException(e); }
        finally { ActorFeedbackActive = previousActorFeedback; }

        // Last bag at that house -> curb clean bonus.
        if (GameSession.CurbBagCountAt(rec.stopId) == 0)
        {
            var stop = PickupStop.Find(rec.stopId);
            Vector3 at = stop != null ? stop.PileCenter + Vector3.up * 1.6f : fxPos;
            string addr = stop != null ? stop.address : rec.contents != null ? rec.contents.foundAt : "";
            float bonus = d.curbBonus;
            feedback.curbCleared = true;
            feedback.curbBonusCents = GameSession.ToCents(bonus);
            feedback.address = addr;
            feedback.curbFxPos = at;
            Ledger.Credit(bonus, "Curb clean!", LedgerIds.CurbBonus, null, d.id);
            if (actorFeedback)
            {
                GameHUD.FloatingText(at + Vector3.up * 0.6f, $"+{Economy.Money(bonus)} Curb clean!", new Color(0.55f, 1f, 0.95f), d.tier >= 3 ? 1.45f : 1.25f);
                GameHUD.Toast(string.IsNullOrEmpty(addr) ? $"Curb clean! +{Economy.Money(bonus)}" : $"Curb clean! {addr} +{Economy.Money(bonus)}", ToastStyle.Good);
            }
            if (nearby)
            {
                Sfx.Play("ding", at, 0.8f, 1.2f);
                Sfx.Play("sparkle", at, 0.6f, 1.1f);
                Vector3 confettiAt = stop != null && stop.Marker != null ? stop.Marker.ArrowPosition : at;
                foreach (var col in Confetti) Fx.Burst(confettiAt, col, d.tier >= 3 ? 22 : 14, 5f);
                if (stop != null && stop.Marker != null) stop.Marker.Celebrate();
            }
            previousActorFeedback = ActorFeedbackActive;
            ActorFeedbackActive = actorFeedback;
            try { ProgressionEvents.RaiseCurbCleared(rec.stopId, d.id, stop != null ? stop.PileCenter : fxPos); }
            catch (System.Exception e) { Debug.LogException(e); }
            finally { ActorFeedbackActive = previousActorFeedback; }
        }
        return pay;
    }

    /// <summary>A curb bag burst (or was otherwise destroyed): drop its record, no pay. Returns true if it had one.
    /// On a guest the host drops it (WorldState.RequestBurst).</summary>
    public static bool RemoveRecord(BagContents contents)
    {
        if (contents == null || string.IsNullOrEmpty(contents.bagId)) return false;
        if (GameSession.IsConnectedGuest)
        {
            if (GameSession.FindCurbBag(contents.bagId) == null) return false;
            WorldState.Instance?.RequestBurst(contents.bagId);
            return true;
        }
        if (GameSession.RemoveCurbBag(contents.bagId) == null) return false;
        version++;
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        stats.Clear();
        districtBags.Clear();
        statsFrame = statsCount = statsVersion = -1;
        statsHours = double.NaN;
        lateHintShown = false;
        ActorFeedbackActive = true;
        BagsIntoTruckToday = 0;
        ExtraAgeHours = null;
    }
}

/// <summary>Curb freshness stages: the colour ramp's steps and the colour-blind shapes (● ◆ ▲ ✖).</summary>
public enum FreshnessStage { Fresh = 0, LateSoon = 1, Late = 2, Stinky = 3 }

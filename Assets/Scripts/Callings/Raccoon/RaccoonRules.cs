using System;
using System.Collections.Generic;

// Pure rules of the Raccoon calling (no UnityEngine): crew sweeps, Embrace It gating, King's Rival, raccoon standing.
// The runtime (RaccoonCrew, FeralStatus, KingsRival, RaccoonStanding) only feeds these with live values.

/// <summary>What one crew raccoon did in one sweep.</summary>
public struct CrewTake
{
    public int id;
    /// <summary>Bags it took off the kerb: the first <see cref="eaten"/> are eaten (gone, no pay), the rest delivered.</summary>
    public int taken, eaten;
    public int evt;
    /// <summary>Cash from antics, in cents (a found wallet is positive, garden gnome damages negative).</summary>
    public int cents;
    public int Delivered => taken - eaten;
}

public static class CrewRules
{
    /// <summary>The hours (0-23) a crew sweeps: none without a Collar Chip; the Brain Chip adds noon, the Full-Time Chip
    /// (Brain Chip level 2) the morning run as well.</summary>
    public static int[] SweepHours(int collarLevel, int brainLevel) =>
        collarLevel <= 0 ? Array.Empty<int>() : brainLevel >= 2 ? RaccoonTuning.FullTimeSweepHours
        : brainLevel == 1 ? RaccoonTuning.BrainSweepHours : RaccoonTuning.CollarSweepHours;

    /// <summary>Most bags one business's whole crew takes in one sweep: grows with the chip and with the hutch.</summary>
    public static int SweepCap(int hutchLevel, int collarLevel, int brainLevel)
    {
        if (collarLevel <= 0 || hutchLevel <= 0) return 0;
        int[] byHutch = RaccoonTuning.CrewSweepCaps[Math.Min(Math.Max(brainLevel, 0), RaccoonTuning.CrewSweepCaps.Length - 1)];
        return byHutch[Math.Min(hutchLevel, byHutch.Length) - 1];
    }

    /// <summary>May a crew collect in a district of this tier at all? Never above RaccoonTuning.CrewMaxDistrictTier.</summary>
    public static bool WorksTier(int districtTier) => districtTier >= 1 && districtTier <= RaccoonTuning.CrewMaxDistrictTier;

    /// <summary>
    /// Which of two kerb bags a crew takes first (negative: a). Collar Chip raccoons go for the oldest (smelliest) bag.
    /// A Brain Chip plans a route: the cheapest district first, the oldest bag first inside it, so the dear streets are
    /// left for the owner. The bag id breaks ties, so the order is total and the same on every run.
    /// </summary>
    public static int CompareBags(int brainLevel, float feeA, double putOutA, string bagIdA, float feeB, double putOutB, string bagIdB)
    {
        if (brainLevel > 0 && feeA != feeB) return feeA < feeB ? -1 : 1;
        int byAge = putOutA.CompareTo(putOutB);
        return byAge != 0 ? byAge : string.CompareOrdinal(bagIdA, bagIdB);
    }

    /// <summary>Kibble for one raccoon for one day, in cents: crew members on a Brain Chip eat better (see RaccoonTuning).</summary>
    public static int FoodCents(HutchRaccoon r, int brainLevel)
    {
        if (r == null) return 0;
        if (r.job != (int)HutchJob.Crew || brainLevel <= 0) return RaccoonTuning.FoodCentsPerHeadPerDay;
        return brainLevel >= 2 ? RaccoonTuning.CrewFoodCentsFullTime : RaccoonTuning.CrewFoodCentsBrain;
    }

    /// <summary>The shift a moment belongs to: shifts roll over at RaccoonTuning.ShiftStartHour, so a whole night is one.</summary>
    public static int ShiftOf(int day, float hourOfDay) => Math.Max(0, hourOfDay < RaccoonTuning.ShiftStartHour ? day - 1 : day);

    /// <summary>Sweep slots (day index x 24 + hour) in (fromHours, toHours], oldest first. At most two days are caught up.</summary>
    public static List<int> DueSlots(double fromHours, double toHours, int[] hours)
    {
        var due = new List<int>();
        if (hours == null || hours.Length == 0 || toHours <= fromHours) return due;
        if (toHours - fromHours > 48.0) fromHours = toHours - 48.0;
        int d0 = (int)Math.Floor(fromHours / 24.0), d1 = (int)Math.Floor(toHours / 24.0);
        for (int day = d0; day <= d1; day++)
            foreach (int h in hours)
            {
                double t = day * 24.0 + h;
                if (t > fromHours && t <= toHours) due.Add(day * 24 + h);
            }
        due.Sort();
        return due;
    }

    public static int BaseBags(int carry) => 1 + (carry >= RaccoonTuning.CarryTwoBags ? 1 : 0) + (carry >= RaccoonTuning.CarryThreeBags ? 1 : 0);
    /// <summary>Bags a raccoon sets out to take in one sweep, before its Zoom roll: 1-3 by Carry, and with a Brain Chip a
    /// cart on a string that holds 1 + Carry / 5 more (2-6 in all).</summary>
    public static int CarryBags(int carry, int brainLevel) =>
        BaseBags(carry) + (brainLevel > 0 ? RaccoonTuning.BrainExtraBags + Math.Max(0, carry) / RaccoonTuning.BrainCarryPerExtraBag : 0);
    public static int EatPercent(HutchRaccoon r) => r.greed * RaccoonTuning.GreedEatPercentPerPoint + (r.rabid ? RaccoonTuning.RabidExtraEatPercent : 0);
    /// <summary>Chance a dog (or a horn) sends it home empty-pawed. Rabid ones are the scary thing on the street.</summary>
    public static int ScarePercent(HutchRaccoon r, bool brain)
    {
        if (r.rabid) return 0;
        int p = (RaccoonTuning.TraitMax - r.nerve) * RaccoonTuning.ScarePercentPerMissingNerve;
        return brain ? p / RaccoonTuning.BrainScareDivisor : p;
    }

    /// <summary>The pay for one delivered bag, in dollars, before the Night Shift bonus everybody gets at night.</summary>
    public static float BagPay(float fee, float payFraction) => fee * payFraction * RaccoonTuning.CrewFeeSharePercent / 100f;

    /// <summary>
    /// One sweep of one business's crew over <paramref name="available"/> kerb bags. Decides, per chipped raccoon at home,
    /// its antics, how many bags it takes and how many of those it eats; updates the roster's shift numbers; never takes
    /// more than is available or than <see cref="SweepCap"/> in total. The caller removes and pays the actual bags.
    /// </summary>
    public static List<CrewTake> Sweep(HutchState h, int hutchLevel, int collarLevel, int brainLevel, int available, Func<int, int, int> range)
    {
        var takes = new List<CrewTake>();
        if (h == null || collarLevel <= 0) return takes;
        bool brain = brainLevel > 0;
        int left = Math.Max(0, Math.Min(available, SweepCap(hutchLevel, collarLevel, brainLevel)));
        foreach (var r in h.raccoons)
        {
            if (r.job != (int)HutchJob.Crew || !r.Available) continue;
            var take = new CrewTake { id = r.id };
            bool works = true;
            int roll = range(0, 100), edge = ScarePercent(r, brain);
            if (roll < edge) { take.evt = CrewEvent.Dog; works = false; }
            else if (!brain && roll < (edge += RaccoonTuning.NapPercent)) { take.evt = CrewEvent.Nap; works = false; }
            else if (!brain && roll < (edge += RaccoonTuning.WanderPercent))
            { take.evt = CrewEvent.Wandered; works = false; r.awayDays = RaccoonTuning.WanderAwayDays; }
            else if (!brain && roll < (edge += RaccoonTuning.GnomePercent))
            { take.evt = CrewEvent.Gnome; take.cents = -RaccoonTuning.GnomeDamagesCents; if (h.gnomes < RaccoonTuning.MaxGnomeTrophies) h.gnomes++; }
            else if (roll < edge + RaccoonTuning.WalletPercent) { take.evt = CrewEvent.Wallet; take.cents = RaccoonTuning.WalletCents; }

            if (works && left > 0)
            {
                int wants = CarryBags(r.carry, brainLevel) + (range(0, 100) < r.speed * RaccoonTuning.SpeedBonusPercentPerPoint ? 1 : 0);
                take.taken = Math.Min(wants, left);
                int eat = EatPercent(r);
                for (int i = 0; i < take.taken; i++) if (range(0, 100) < eat) take.eaten++;
                left -= take.taken;
            }
            r.brought = Math.Min(99, r.brought + take.Delivered);
            r.ate = Math.Min(99, r.ate + take.eaten);
            r.total = Math.Min(99999, r.total + take.Delivered);
            if (take.evt != CrewEvent.None) r.evt = take.evt;
            takes.Add(take);
        }
        return takes;
    }

    /// <summary>A new shift starts: yesterday's numbers are wiped. True when anything was cleared.</summary>
    public static bool BeginShift(HutchState h, int shift)
    {
        if (h == null || h.shift == shift) return false;
        foreach (var r in h.raccoons) { r.brought = 0; r.ate = 0; r.evt = CrewEvent.None; }
        h.shift = shift; h.told = false;
        return true;
    }

    public static bool HasShiftNews(HutchState h)
    {
        if (h != null) foreach (var r in h.raccoons) if (r.brought > 0 || r.ate > 0 || r.evt != CrewEvent.None) return true;
        return false;
    }

    /// <summary>Close the shift's report (once). True when there was something to tell.</summary>
    public static bool CloseShift(HutchState h)
    {
        if (h == null || h.told) return false;
        h.told = true;
        if (!HasShiftNews(h)) return false;
        h.reportDay++;
        return true;
    }

    /// <summary>"Biscuit brought 6, ate 2, got chased by a dog." One line per raccoon with news.</summary>
    public static List<string> ReportLines(HutchState h)
    {
        var lines = new List<string>();
        if (h == null) return lines;
        foreach (var r in h.raccoons)
        {
            if (r.brought <= 0 && r.ate <= 0 && r.evt == CrewEvent.None) continue;
            var parts = new List<string>(3);
            if (r.brought > 0 || r.ate > 0 || r.evt == CrewEvent.None) parts.Add($"brought {r.brought}");
            if (r.ate > 0) parts.Add($"ate {r.ate}");
            if (r.evt != CrewEvent.None) parts.Add(CrewEvent.Text(r.evt));
            lines.Add($"{r.name} {string.Join(", ", parts)}.");
        }
        return lines;
    }

    public static void ShiftTotals(HutchState h, out int brought, out int ate)
    {
        brought = ate = 0;
        if (h != null) foreach (var r in h.raccoons) { brought += r.brought; ate += r.ate; }
    }
}

/// <summary>Embrace It (calling_embrace_it): what rabies does to you once you stop fighting it.</summary>
public static class FeralRules
{
    /// <summary>L1+: you never pass out from rabies and never pay the hospital.</summary>
    public static bool PassesOut(int embraceLevel) => embraceLevel <= 0;
    /// <summary>L1+: a stored Rabies Shot is no longer used on its own. You cure yourself on purpose, or not at all.</summary>
    public static bool AutoShot(int embraceLevel) => embraceLevel <= 0;
    /// <summary>The symptom strength actually shown: capped to a stylised hint once embraced.</summary>
    public static float ShownIntensity(float intensity, int embraceLevel) =>
        embraceLevel <= 0 ? intensity : Math.Min(intensity, RaccoonTuning.FeralSymptomCap);
    /// <summary>L1+: no stumbling drift and no hiccup hops.</summary>
    public static bool Stumbles(int embraceLevel) => embraceLevel <= 0;
    /// <summary>The perks (L2+) only run while you actually carry the virus.</summary>
    public static bool PerksActive(int embraceLevel, bool infected) => embraceLevel >= 2 && infected;
    public static float SpeedMult(int embraceLevel, bool infected, bool night)
    {
        if (!PerksActive(embraceLevel, infected)) return 1f;
        return night ? RaccoonTuning.FeralNightSpeed : embraceLevel >= 3 ? RaccoonTuning.FeralDaySpeed : 1f;
    }
    public static bool CanPounce(int embraceLevel, bool infected) => embraceLevel >= 3 && infected;
    /// <summary>Wild raccoons leave this player alone: feral and infected (L2+), or an Alpha (Pack Leader L2).</summary>
    public static bool RaccoonsDefer(int embraceLevel, bool infected, int packLevel) => PerksActive(embraceLevel, infected) || packLevel >= 2;
}

/// <summary>How the raccoon underworld sees a player. Other systems may read this (RaccoonStanding.For).</summary>
public enum RaccoonTier { Stranger = 0, Friend = 1, Family = 2, Made = 3, Rival = 4, Boss = 5 }

public static class RaccoonStandingRules
{
    /// <summary>
    /// Stranger: nothing. Friend: keeps a hutch or has embraced the bite. Family: feral perks, a chipped crew or a companion.
    /// Made: an Alpha or a Brain Chip crew. Rival: built King's Rival. Boss: a Rival who has dethroned the King.
    /// </summary>
    public static RaccoonTier Tier(int hutch, int embrace, int collar, int brain, int pack, int rival, int kingsCollars)
    {
        if (rival > 0) return kingsCollars > 0 ? RaccoonTier.Boss : RaccoonTier.Rival;
        if (pack >= 2 || brain > 0) return RaccoonTier.Made;
        if (embrace >= 2 || collar > 0 || pack >= 1) return RaccoonTier.Family;
        if (hutch > 0 || embrace > 0) return RaccoonTier.Friend;
        return RaccoonTier.Stranger;
    }
    public static string Title(RaccoonTier tier) => tier switch
    {
        RaccoonTier.Friend => "Friend of the Bins", RaccoonTier.Family => "Family", RaccoonTier.Made => "Made Raccoon",
        RaccoonTier.Rival => "The King's Rival", RaccoonTier.Boss => "Boss of the Bins", _ => "Stranger",
    };
}

/// <summary>
/// King's Rival (calling_kings_rival). ONE rule set: the Raccoon King runs the competition, and to him a Rival is a
/// business problem. When he raids, he comes for the Rivals first. Dethrone him with a Rival's hit on the board and that
/// Rival is paid a turf bonus and wins a King's Collar; let him walk away undefeated and his collectors take tribute from
/// every Rival in town.
/// </summary>
public static class KingsRivalRules
{
    public static bool IsRival(int rivalLevel) => rivalLevel > 0;
    /// <summary>The King switches from the nearest player to a Rival when the Rival is on foot and inside his focus range.</summary>
    public static bool PrefersRival(bool rivalOnFoot, float rivalDistance) => rivalOnFoot && rivalDistance <= RaccoonTuning.RivalFocusRange;
    /// <summary>Turf bonus (cents) when the King falls: only for a Rival who landed at least one hit.</summary>
    public static int BonusCents(int rivalLevel, int hits) => IsRival(rivalLevel) && hits > 0 ? RaccoonTuning.RivalBonusCents : 0;
    public static bool WinsCollar(int rivalLevel, int hits) => BonusCents(rivalLevel, hits) > 0;
    /// <summary>Tribute (cents) when he leaves undefeated: every Rival in town pays, fighter or not.</summary>
    public static int TributeCents(int rivalLevel) => IsRival(rivalLevel) ? RaccoonTuning.TributeCents : 0;

    static readonly string[] arrive =
    {
        "{0}. We need to talk about MY bins.", "Nice hutch, {0}. Shame if somebody... ate it.",
        "{0}! You run raccoons in MY town?", "I made you an offer, {0}. It was garbage. Literally.",
    };
    static readonly string[] hit = { "You come at the King...", "That's coming out of your cut.", "Who taught you that? ME?", "Not the crown, kid!" };
    static readonly string[] win = { "This town ain't big enough... fine. It's yours. For now.", "Tell the bins... I loved them." };
    static readonly string[] leave = { "Nothing personal, {0}. Strictly business.", "The family thanks you for your tribute, {0}." };
    public static string ArriveLine(string rival, int pick) => string.Format(arrive[Mod(pick, arrive.Length)], rival);
    public static string HitLine(int pick) => hit[Mod(pick, hit.Length)];
    public static string WinLine(int pick) => win[Mod(pick, win.Length)];
    public static string LeaveLine(string rival, int pick) => string.Format(leave[Mod(pick, leave.Length)], rival);
    static int Mod(int v, int n) => ((v % n) + n) % n;
}

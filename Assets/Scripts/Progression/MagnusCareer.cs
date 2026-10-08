using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Magnus's career path (Docs/Progression.md §6.3): the Magnus department read as ONE ordered path instead of four loose
/// upgrade lines. Step 1, "Magnus Gets a Job" (magnus_brain L1), bundles the rail to your sort table, taped kitchen tongs
/// and Brain Module 1: he moves from the chess board to the sort station, picks up bags within reach, rips them and sorts
/// them, with or without a conveyor. Every later step adds exactly one thing (speed, heavier items + heavy bags, treasure
/// flags, treasure, hazards, the night shift); <see cref="Capabilities"/> proves no step is a dead purchase. Prerequisites live in the
/// catalog (ScrapazonCatalog) and are checked by <see cref="Progression.CanBuy"/>; this class only names the steps, says
/// where you are on the path and what Magnus can do right now. Pure reads (no state, co-op safe).
/// </summary>
public static class MagnusCareer
{
    public readonly struct Step
    {
        public readonly string id;
        public readonly int level;
        /// <summary>Short step name ("Lobster Pincer").</summary>
        public readonly string name;
        /// <summary>What this step adds, finishing the sentence "Magnus ..." ("lifts items up to 25 kg").</summary>
        public readonly string adds;

        public Step(string id, int level, string name, string adds)
        {
            this.id = id; this.level = level; this.name = name; this.adds = adds;
        }

        public ScrapazonItem Item => ScrapazonCatalog.Get(id);
        public bool Done => GameSession.GetUpgradeLevel(id) >= level;
        public int Rank { get { var it = Item; return it != null ? it.RankFor(level) : 1; } }

        public float Price
        {
            get
            {
                var it = Item;
                if (it == null || it.prices == null || it.prices.Length == 0) return 0f;
                return it.prices[Mathf.Clamp(level - 1, 0, it.prices.Length - 1)];
            }
        }
    }

    /// <summary>The path, in order. Levels per id must stay increasing (the store sells the next level only).</summary>
    public static readonly Step[] Steps =
    {
        new Step(ShopIds.MagnusBrain, 1, "Magnus Gets a Job", "moves to your sort table, rips the bags within his reach and sorts recyclables + junk"),
        new Step(ShopIds.MagnusRailMotor, 1, "Rail Motor", "commutes twice as fast and works ×1.25 faster"),
        new Step(ShopIds.MagnusGripper, 1, "Lobster Pincer", "lifts items up to 25 kg and handles HEAVY bags"),
        new Step(ShopIds.MagnusVision, 1, "X-Ray Googly Eyes", "flags 60% of treasure bags (the belt and he take those first)"),
        new Step(ShopIds.MagnusBrain, 2, "Treasure Sense", "puts treasure in the KEEP crate"),
        new Step(ShopIds.MagnusVision, 2, "Appraisal Monocle", "flags every treasure bag (+3% on ebag)"),
        new Step(ShopIds.MagnusBrain, 3, "Hazmat Certified", "bins hazards in the barrel"),
        new Step(ShopIds.MagnusGripper, 2, "Quad-Claw", "works ×1.4 faster"),
        new Step(ShopIds.MagnusBrain, 4, "Grandmaster Module", "works 24 hours a day (and becomes MAGNUS PRIME)"),
    };

    public static int Count => Steps.Length;

    public static bool IsCareerItem(string id) =>
        id == ShopIds.MagnusBrain || id == ShopIds.MagnusRailMotor || id == ShopIds.MagnusGripper || id == ShopIds.MagnusVision;

    /// <summary>1-based step number of (id, level), or 0 when it isn't on the path.</summary>
    public static int StepNumber(string id, int level)
    {
        for (int i = 0; i < Steps.Length; i++)
            if (Steps[i].id == id && Steps[i].level == level) return i + 1;
        return 0;
    }

    /// <summary>"Step 3: Lobster Pincer" for (id, level), or null when it isn't on the path.</summary>
    public static string StepLabel(string id, int level)
    {
        int n = StepNumber(id, level);
        return n > 0 ? $"Step {n}: {Steps[n - 1].name}" : null;
    }

    /// <summary>Steps owned (in any order; prerequisites allow a little reordering).</summary>
    public static int DoneCount
    {
        get
        {
            int n = 0;
            foreach (var s in Steps) if (s.Done) n++;
            return n;
        }
    }

    public static bool Complete => DoneCount >= Steps.Length;

    /// <summary>Index of the first step on the path not owned yet, or -1 when the career is complete.</summary>
    public static int NextIndex
    {
        get
        {
            for (int i = 0; i < Steps.Length; i++) if (!Steps[i].Done) return i;
            return -1;
        }
    }

    /// <summary>
    /// Can this step be bought right now (cash aside)? <paramref name="why"/> says what's missing: an earlier level of the
    /// same part ("Comes after Step 4: Treasure Sense"), a rank ("Requires Rank 5: ...") or another step
    /// ("Requires Step 4: X-Ray Googly Eyes").
    /// </summary>
    public static bool CanBuyStep(Step s, out string why)
    {
        why = null;
        if (s.Done) return false;
        var it = s.Item;
        if (it == null) { why = "not sold yet"; return false; }
        int owned = GameSession.GetUpgradeLevel(s.id);
        if (owned < s.level - 1)
        {
            // Name the step it directly builds on (Hazmat "comes after Step 5: Treasure Sense", not Step 1): that's the
            // one the store sells right before it.
            why = $"Comes after {StepLabel(s.id, s.level - 1) ?? ScrapazonStore.ShortName(it) + " Lv " + (s.level - 1)}";
            return false;
        }
        return Progression.CanBuy(it, s.level, out why);
    }

    /// <summary>The step the card for <paramref name="it"/> would buy next (0 when maxed / not a career item).</summary>
    public static int NextStepNumberFor(ScrapazonItem it)
    {
        if (it == null || !IsCareerItem(it.id) || ScrapazonStore.IsMaxed(it)) return 0;
        return StepNumber(it.id, ScrapazonStore.NextLevel(it));
    }

    // ------------------------------------------------------------------ "Magnus can now"

    /// <summary>Plain lines: what Magnus does today, from the live upgrade levels.</summary>
    public static List<string> CanNow()
    {
        var lines = new List<string>();
        if (!UpgradeEffects.MagnusEmployed)
        {
            lines.Add("Plays chess (badly). That's it. He has no job yet.");
            return lines;
        }
        var b = UpgradeEffects.Brain;
        float rate = b.itemsPerMin * UpgradeEffects.MagnusSpeedMult;
        string what = b.hazards ? "everything: recyclables, junk, treasure and hazards"
                    : b.valuables ? "recyclables, junk and treasure"
                    : "recyclables and junk";
        string hours = b.endHour - b.startHour >= 23.99f ? "around the clock" : $"{Hour(b.startHour)}-{Hour(b.endHour)}";
        lines.Add($"Commutes to your sort table and sorts {what}: {rate:0.#} items/min, {Mathf.RoundToInt(b.accuracy * 1000f) / 10f:0.#}% accurate, {hours}.");
        lines.Add(UpgradeEffects.MagnusHandlesHeavyBags
            ? $"Rips every bag within his reach, heavy ones too (the table, {MagnusReach.ReachMetres:0} m around it and the Bag Belt's queue; never the dump pile). Lifts items up to {UpgradeEffects.GripperMaxKg:0} kg."
            : $"Rips the bags within his reach (the table, {MagnusReach.ReachMetres:0} m around it and the Bag Belt's queue; never the dump pile). Heavy bags are yours. Lifts items up to {UpgradeEffects.GripperMaxKg:0} kg.");
        if (!b.hazards)
            lines.Add(b.valuables ? "Hazards go to LEFTOVERS for you." : "Treasure and hazards go to LEFTOVERS for you.");
        float flags = UpgradeEffects.VisionDetect;
        if (flags > 0f) lines.Add(flags >= 1f ? "Flags every treasure bag in the garage (+3% on ebag)." : $"Flags {Mathf.RoundToInt(flags * 100f)}% of treasure bags in the garage.");
        if (UpgradeEffects.Level(ShopIds.MagnusRailMotor) >= 1) lines.Add("Rides a rail motor with racing stripes (commutes twice as fast).");
        return lines;
    }

    /// <summary>"Chess: tier 3 (~800 Elo)".</summary>
    public static string ChessLine()
    {
        int tier = UpgradeEffects.MagnusChessTier;
        return $"Chess: tier {tier}/11 ({UpgradeEffects.Chess(tier).label})";
    }

    /// <summary>One-line summary of the next step: "Next: Step 3: Lobster Pincer ($1,200, Rank 4): lifts ... himself."</summary>
    public static string NextLine()
    {
        int i = NextIndex;
        if (i < 0) return "Career complete. He is MAGNUS PRIME. He knows.";
        var s = Steps[i];
        return $"Next: Step {i + 1}: {s.name} ({Economy.Money(s.Price)}, Rank {s.Rank}): Magnus {s.adds}.";
    }

    // ------------------------------------------------------------------ what a set of levels can do (pure)

    /// <summary>Everything Magnus can do at a set of career levels, as numbers. Two different sets of levels that give
    /// the same Capabilities would mean a step that does nothing: <see cref="StepAdds"/> (and CareerStepIntegrityTests).</summary>
    public struct Capabilities : System.IEquatable<Capabilities>
    {
        public bool employed, ripsBags, heavyBags, valuables, hazards;
        public float itemsPerMin, accuracy, hoursPerDay, maxItemKg, railSpeed, flagShare, ebaeBonus;
        public int chessTier;

        public bool Equals(Capabilities o) =>
            employed == o.employed && ripsBags == o.ripsBags && heavyBags == o.heavyBags && valuables == o.valuables &&
            hazards == o.hazards && itemsPerMin == o.itemsPerMin && accuracy == o.accuracy && hoursPerDay == o.hoursPerDay &&
            maxItemKg == o.maxItemKg && railSpeed == o.railSpeed && flagShare == o.flagShare && ebaeBonus == o.ebaeBonus;
        // chessTier is left out on purpose: every step raises it, so it would hide a step with no sorting effect.
        public override bool Equals(object o) => o is Capabilities c && Equals(c);
        public override int GetHashCode() => itemsPerMin.GetHashCode() ^ maxItemKg.GetHashCode() ^ (heavyBags ? 1 : 0);
    }

    /// <summary>Capabilities at explicit levels. Without the job (brain 0) nothing else counts: he only plays chess.</summary>
    public static Capabilities CapabilitiesAt(int brain, int rail, int gripper, int vision)
    {
        var b = UpgradeEffects.BrainAt(brain);
        bool job = brain >= 1;
        return new Capabilities
        {
            employed = job,
            ripsBags = job && MagnusReach.MayRip(brain, gripper, false),
            heavyBags = job && MagnusReach.MayRip(brain, gripper, true),
            valuables = b.valuables, hazards = b.hazards,
            itemsPerMin = b.itemsPerMin * (job ? UpgradeEffects.MagnusSpeedMultAt(rail, gripper) : 0f),
            accuracy = b.accuracy,
            hoursPerDay = Mathf.Max(0f, b.endHour - b.startHour),
            maxItemKg = UpgradeEffects.GripperMaxKgAt(brain, gripper),
            railSpeed = job ? UpgradeEffects.RailSpeedAt(rail) : 0f,
            flagShare = UpgradeEffects.VisionDetectAt(vision),
            ebaeBonus = UpgradeEffects.MonocleEbaeBonusAt(vision),
            chessTier = UpgradeEffects.ChessTierFor(rail, gripper, vision, brain),
        };
    }

    /// <summary>
    /// Does career step <paramref name="index"/> (0-based) change what Magnus can do, bought on top of every step before
    /// it on the path? (Chess strength alone does not count.)
    /// </summary>
    public static bool StepAdds(int index)
    {
        if (index < 0 || index >= Steps.Length) return false;
        int brain = 0, rail = 0, gripper = 0, vision = 0;
        void Apply(Step s)
        {
            if (s.id == ShopIds.MagnusBrain) brain = Mathf.Max(brain, s.level);
            else if (s.id == ShopIds.MagnusRailMotor) rail = Mathf.Max(rail, s.level);
            else if (s.id == ShopIds.MagnusGripper) gripper = Mathf.Max(gripper, s.level);
            else if (s.id == ShopIds.MagnusVision) vision = Mathf.Max(vision, s.level);
        }
        for (int i = 0; i < index; i++) Apply(Steps[i]);
        var before = CapabilitiesAt(brain, rail, gripper, vision);
        Apply(Steps[index]);
        return !before.Equals(CapabilitiesAt(brain, rail, gripper, vision));
    }

    static string Hour(float h)
    {
        int hh = Mathf.FloorToInt(h) % 24;
        int h12 = hh % 12 == 0 ? 12 : hh % 12;
        return $"{h12} {(hh < 12 ? "AM" : "PM")}";
    }
}

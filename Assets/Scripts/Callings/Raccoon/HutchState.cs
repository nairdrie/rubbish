using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// The Critter Hutch roster (Raccoon calling). Pure data + rules: no UnityEngine, so EditMode tests and tools can run it.
//
// WHERE IT LIVES: as one compact string in WorkshopState.hutch. That state is already owned by the business, validated and
// published by the host (BusinessRegistry workshop list, a 4000-character JSON snapshot), mirrored into the owner's own
// save, sent to late joiners and dropped when the owner leaves, so the roster gets host authority, replication and saving
// without a new network list, a new save field or a prefab change. WorkshopState only sees a length-checked string, so it
// stays free of this type (Tools/workshop_state_probe.cs compiles it standalone). A full hutch of six is under 500
// characters (cap HutchState.MaxChars); the whole workshop JSON with it stays far inside the 4000 budget (HutchTests).

public enum HutchJob { Rest = 0, Crew = 1, Companion = 2 }

/// <summary>Special kits (RaccoonTuning.VariantPercent). Looks and a trait nudge at birth.</summary>
public static class HutchVariant
{
    public const int Normal = 0, Golden = 1, Albino = 2, Chonk = 3, Zoomer = 4, Count = 5;
    public static string Name(int variant) => variant switch { Golden => "Golden", Albino => "Albino", Chonk => "Chonk", Zoomer => "Zoomer", _ => "" };
}

/// <summary>What happened to a crew raccoon on its last sweep (shown in the morning report).</summary>
public static class CrewEvent
{
    public const int None = 0, Dog = 1, Wandered = 2, Gnome = 3, Wallet = 4, Nap = 5, Count = 6;
    public static string Text(int evt) => evt switch
    {
        Dog => "got chased by a dog", Wandered => "wandered off", Gnome => "stole a garden gnome",
        Wallet => "found a wallet", Nap => "fell asleep in a bin", _ => "",
    };
}

[Serializable]
public sealed class HutchRaccoon
{
    /// <summary>Stable for the life of the roster (never reused).</summary>
    public int id;
    public string name = "";
    /// <summary>Traits, RaccoonTuning.TraitMin..TraitMax. Speed: extra bags. Carry: bags per sweep. Greed: eats the haul.
    /// Nerve: not scared off by dogs and horns.</summary>
    public int speed = 5, carry = 5, greed = 5, nerve = 5;
    public bool rabid;
    public int variant;
    /// <summary>Look seed (fur shade, collar colour, accessory), 0..65535.</summary>
    public int look;
    public int job;
    /// <summary>Above 0: a kit, grown up in this many days.</summary>
    public int kitDays;
    /// <summary>Partner's id while paired in the Breeding Box (0 = none).</summary>
    public int mate;
    /// <summary>Above 0 on ONE of a pair: the litter arrives in this many days.</summary>
    public int dueDays;
    public int restDays, awayDays;
    /// <summary>This shift's haul and the last thing that happened (CrewEvent), and bags delivered in total.</summary>
    public int brought, ate, evt, total;

    public bool IsKit => kitDays > 0;
    public bool Paired => mate != 0;
    /// <summary>May hold a job at all (a wanderer keeps its job and picks it up again when it is back).</summary>
    public bool CanHoldJob => !IsKit && !Paired && restDays <= 0;
    /// <summary>A rested adult at home: may be paired, chipped onto the crew or taken along, and works today.</summary>
    public bool Available => CanHoldJob && awayDays <= 0;
    public HutchRaccoon Clone() => (HutchRaccoon)MemberwiseClone();
}

[Serializable]
public sealed class HutchState
{
    public const int Version = 1;
    /// <summary>Longest encoded roster WorkshopState accepts.</summary>
    public const int MaxChars = 700;

    /// <summary>The GameClock.Day this roster last had its daily tick (food, gestation, growing up).</summary>
    public int day;
    /// <summary>The crew shift (CrewRules.ShiftOf) the brought/ate/evt numbers belong to, and whether its report is out.</summary>
    public int shift;
    public bool told;
    /// <summary>Counts up each time a shift report is closed; the owner's machine announces when it changes.</summary>
    public int reportDay;
    public int nextId = 1;
    /// <summary>King's Collars won by dethroning the Raccoon King as his Rival.</summary>
    public int collars;
    /// <summary>Stolen garden gnomes on the hutch roof (0..RaccoonTuning.MaxGnomeTrophies).</summary>
    public int gnomes;
    public List<HutchRaccoon> raccoons = new List<HutchRaccoon>();

    public int Count => raccoons.Count;
    public HutchRaccoon Find(int id) { if (id > 0) foreach (var r in raccoons) if (r.id == id) return r; return null; }
    public int CountJob(HutchJob job) { int n = 0; foreach (var r in raccoons) if (r.job == (int)job) n++; return n; }

    public HutchState Clone()
    {
        var copy = (HutchState)MemberwiseClone();
        copy.raccoons = new List<HutchRaccoon>(raccoons.Count);
        foreach (var r in raccoons) copy.raccoons.Add(r.Clone());
        return copy;
    }

    public bool Valid()
    {
        if (raccoons == null || raccoons.Count > RaccoonTuning.MaxRaccoons || nextId < 1 || collars < 0 || collars > 999 ||
            gnomes < 0 || gnomes > RaccoonTuning.MaxGnomeTrophies || day < 0 || shift < 0 || reportDay < 0) return false;
        var ids = new HashSet<int>();
        foreach (var r in raccoons)
        {
            if (r == null || r.id < 1 || r.id >= nextId || !ids.Add(r.id)) return false;
            if (string.IsNullOrEmpty(r.name) || r.name != HutchRules.CleanName(r.name)) return false;
            if (!Trait(r.speed) || !Trait(r.carry) || !Trait(r.greed) || !Trait(r.nerve)) return false;
            if (r.variant < 0 || r.variant >= HutchVariant.Count || r.look < 0 || r.look > 65535 || r.job < 0 || r.job > 2) return false;
            if (r.kitDays < 0 || r.kitDays > 9 || r.dueDays < 0 || r.dueDays > 9 || r.restDays < 0 || r.restDays > 9 || r.awayDays < 0 || r.awayDays > 9) return false;
            if (r.brought < 0 || r.brought > 99 || r.ate < 0 || r.ate > 99 || r.evt < 0 || r.evt >= CrewEvent.Count || r.total < 0 || r.total > 99999) return false;
            if (r.job != (int)HutchJob.Rest && !r.CanHoldJob) return false; // kits, pairs and the resting never hold a job
            if (r.IsKit && r.Paired) return false;
        }
        foreach (var r in raccoons)
        {
            if (!r.Paired) { if (r.dueDays != 0) return false; continue; }
            var other = Find(r.mate);
            if (other == null || other == r || other.mate != r.id) return false;
            if ((r.dueDays > 0) == (other.dueDays > 0)) return false; // exactly one of a pair carries the countdown
        }
        return true;
    }
    static bool Trait(int v) => v >= RaccoonTuning.TraitMin && v <= RaccoonTuning.TraitMax;

    // ------------------------------------------------------------------ codec

    /// <summary>The compact saved/replicated form. Only digits, letters, spaces and ",;|-": safe inside JSON as is.</summary>
    public string Encode()
    {
        var sb = new StringBuilder(96 + raccoons.Count * 64);
        sb.Append(Version).Append(';').Append(day).Append(';').Append(shift).Append(';').Append(told ? 1 : 0).Append(';')
          .Append(reportDay).Append(';').Append(nextId).Append(';').Append(collars).Append(';').Append(gnomes);
        foreach (var r in raccoons)
        {
            sb.Append('|').Append(r.id).Append(',').Append(HutchRules.CleanName(r.name)).Append(',').Append(r.speed).Append(',')
              .Append(r.carry).Append(',').Append(r.greed).Append(',').Append(r.nerve).Append(',')
              .Append((r.rabid ? 1 : 0) | (r.variant << 1)).Append(',').Append(r.look).Append(',').Append(r.job).Append(',')
              .Append(r.kitDays).Append(',').Append(r.mate).Append(',').Append(r.dueDays).Append(',').Append(r.restDays).Append(',')
              .Append(r.awayDays).Append(',').Append(r.brought).Append(',').Append(r.ate).Append(',').Append(r.evt).Append(',').Append(r.total);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read a saved roster. Empty means an empty hutch. Every value is clamped into its legal range and broken links are
    /// cut, so whatever comes out is <see cref="Valid"/> (a hand-edited save cannot smuggle in a super raccoon). Null only
    /// when the text is not a roster at all (wrong version, garbage).
    /// </summary>
    public static HutchState Parse(string text)
    {
        var h = new HutchState();
        if (string.IsNullOrEmpty(text)) return h;
        if (text.Length > MaxChars) return null;
        string[] blocks = text.Split('|');
        string[] head = blocks[0].Split(';');
        if (head.Length < 8 || Int(head[0], 0) != Version) return null;
        h.day = Clamp(Int(head[1], 0), 0, 999999);
        h.shift = Clamp(Int(head[2], 0), 0, 999999);
        h.told = Int(head[3], 0) != 0;
        h.reportDay = Clamp(Int(head[4], 0), 0, 999999);
        h.collars = Clamp(Int(head[6], 0), 0, 999);
        h.gnomes = Clamp(Int(head[7], 0), 0, RaccoonTuning.MaxGnomeTrophies);
        int maxId = 0;
        for (int i = 1; i < blocks.Length && h.raccoons.Count < RaccoonTuning.MaxRaccoons; i++)
        {
            string[] f = blocks[i].Split(',');
            if (f.Length < 18) continue;
            int id = Int(f[0], 0);
            if (id < 1 || id > 999999 || h.Find(id) != null) continue;
            int flags = Int(f[6], 0);
            var r = new HutchRaccoon
            {
                id = id, name = HutchRules.CleanName(f[1]),
                speed = ClampTrait(Int(f[2], 5)), carry = ClampTrait(Int(f[3], 5)), greed = ClampTrait(Int(f[4], 5)), nerve = ClampTrait(Int(f[5], 5)),
                rabid = (flags & 1) != 0, variant = Clamp(flags >> 1, 0, HutchVariant.Count - 1), look = Clamp(Int(f[7], 0), 0, 65535),
                job = Clamp(Int(f[8], 0), 0, 2), kitDays = Clamp(Int(f[9], 0), 0, 9), mate = Math.Max(0, Int(f[10], 0)),
                dueDays = Clamp(Int(f[11], 0), 0, 9), restDays = Clamp(Int(f[12], 0), 0, 9), awayDays = Clamp(Int(f[13], 0), 0, 9),
                brought = Clamp(Int(f[14], 0), 0, 99), ate = Clamp(Int(f[15], 0), 0, 99), evt = Clamp(Int(f[16], 0), 0, CrewEvent.Count - 1),
                total = Clamp(Int(f[17], 0), 0, 99999),
            };
            if (r.name.Length == 0) r.name = "Raccoon " + id;
            h.raccoons.Add(r);
            maxId = Math.Max(maxId, id);
        }
        h.nextId = Math.Max(Clamp(Int(head[5], 1), 1, 1000000), maxId + 1);
        h.Repair();
        return h;
    }

    /// <summary>Cut half-links and impossible combinations (used after parsing and after a raccoon leaves).</summary>
    internal void Repair()
    {
        foreach (var r in raccoons)
        {
            if (r.IsKit) { r.mate = 0; r.dueDays = 0; }
            if (!r.Paired) { r.dueDays = 0; continue; }
            var other = Find(r.mate);
            if (other == null || other == r || other.mate != r.id || other.IsKit) { r.mate = 0; r.dueDays = 0; }
        }
        foreach (var r in raccoons)
        {
            if (r.Paired)
            {
                var other = Find(r.mate);
                if (r.dueDays > 0 && other.dueDays > 0) other.dueDays = 0;
                else if (r.dueDays == 0 && other.dueDays == 0) (r.id < other.id ? r : other).dueDays = 1;
            }
            if (!r.CanHoldJob) r.job = (int)HutchJob.Rest;
        }
    }

    static int Int(string s, int fallback) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    static int ClampTrait(int v) => Clamp(v, RaccoonTuning.TraitMin, RaccoonTuning.TraitMax);
}

/// <summary>Everything the hutch decides, as pure functions over a <see cref="HutchState"/>. The host calls these with its
/// own dice (<c>NightDice.Host.Range</c>); tests call them with <see cref="Seeded"/>.</summary>
public static class HutchRules
{
    static readonly string[] names =
    {
        "Biscuit", "Bandit", "Pickles", "Noodle", "Trashley", "Gizmo", "Waffles", "Rocket", "Mango", "Scraps", "Dumpster",
        "Nugget", "Peanut", "Sprocket", "Crumbs", "Gary", "Meatball", "Toast", "Pebble", "Wiggles", "Tater", "Beans", "Ziggy",
        "Goblin", "Muffin", "Rascal", "Socks", "Tuna", "Velcro", "Kevin Jr", "Raccoony", "Mr Fingers", "Trash Panda", "Dorito",
        "Lasagna", "Chimney", "Burrito", "Sir Bites", "Fuzzbucket", "Moth",
    };
    public static IReadOnlyList<string> Names => names;

    /// <summary>A deterministic [min, max) roller for tests and tools (xorshift32, the same generator NightDice uses).</summary>
    public static Func<int, int, int> Seeded(uint seed)
    {
        // scramble first (murmur3's finaliser): neighbouring seeds must not give neighbouring first rolls
        uint s = seed + 0x9E3779B9u;
        s ^= s >> 16; s *= 0x85EBCA6Bu; s ^= s >> 13; s *= 0xC2B2AE35u; s ^= s >> 16;
        if (s == 0) s = 0x9E3779B9u;
        return (min, max) =>
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            return max <= min ? min : min + (int)(s % (uint)(max - min));
        };
    }

    public static int Capacity(int hutchLevel) => hutchLevel >= 2 ? RaccoonTuning.HutchCapacityL2 : hutchLevel == 1 ? RaccoonTuning.HutchCapacityL1 : 0;
    public static int CompanionLimit(int packLevel) => packLevel >= 2 ? RaccoonTuning.CompanionsL2 : packLevel == 1 ? RaccoonTuning.CompanionsL1 : 0;
    public static bool HasRoom(HutchState h, int hutchLevel) => h != null && h.Count < Capacity(hutchLevel);

    /// <summary>Letters, digits, spaces and dashes only, trimmed, at most RaccoonTuning.NameMaxChars. May return "".</summary>
    public static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(RaccoonTuning.NameMaxChars);
        foreach (char c in raw)
        {
            bool ok = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ';
            if (ok && sb.Length < RaccoonTuning.NameMaxChars) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    static string FreshName(HutchState h, Func<int, int, int> range)
    {
        int start = range(0, names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            string candidate = names[(start + i) % names.Length];
            bool taken = false;
            foreach (var r in h.raccoons) if (r.name == candidate) { taken = true; break; }
            if (!taken) return candidate;
        }
        return CleanName("Raccoon " + h.nextId);
    }

    /// <summary>A freshly trapped wild raccoon joins (traits rolled here). Null when the hutch is full.</summary>
    public static HutchRaccoon AdmitWild(HutchState h, int hutchLevel, bool rabid, Func<int, int, int> range)
    {
        if (!HasRoom(h, hutchLevel)) return null;
        int lo = RaccoonTuning.WildTraitMin, hi = RaccoonTuning.WildTraitMax + 1;
        var r = new HutchRaccoon
        {
            id = h.nextId++, name = FreshName(h, range), speed = range(lo, hi), carry = range(lo, hi), greed = range(lo, hi), nerve = range(lo, hi),
            rabid = rabid, look = range(0, 65536),
        };
        h.raccoons.Add(r);
        return r;
    }

    static int Clamp(int v) => v < RaccoonTuning.TraitMin ? RaccoonTuning.TraitMin : v > RaccoonTuning.TraitMax ? RaccoonTuning.TraitMax : v;

    static int InheritTrait(int a, int b, Func<int, int, int> range)
    {
        int sum = a + b, avg = sum / 2 + ((sum & 1) != 0 ? range(0, 2) : 0); // a half rounds either way
        int reach = range(0, 100) < RaccoonTuning.BigMutationPercent ? 2 : 1;
        return Clamp(avg + range(-reach, reach + 1));
    }

    /// <summary>A kit of two parents: each trait is the parents' average, nudged by a small mutation; now and then a
    /// special variant. Does NOT add it to the roster.</summary>
    public static HutchRaccoon Inherit(HutchRaccoon a, HutchRaccoon b, Func<int, int, int> range)
    {
        var kit = new HutchRaccoon
        {
            speed = InheritTrait(a.speed, b.speed, range), carry = InheritTrait(a.carry, b.carry, range),
            greed = InheritTrait(a.greed, b.greed, range), nerve = InheritTrait(a.nerve, b.nerve, range),
            rabid = a.rabid && b.rabid || (a.rabid || b.rabid) && range(0, 100) < RaccoonTuning.OneRabidParentPercent,
            look = range(0, 65536), kitDays = RaccoonTuning.GrowUpDays,
        };
        if (range(0, 100) < RaccoonTuning.VariantPercent)
        {
            kit.variant = range(1, HutchVariant.Count);
            switch (kit.variant)
            {
                case HutchVariant.Golden: kit.carry = Clamp(kit.carry + 2); kit.greed = Clamp(kit.greed - 1); break;
                case HutchVariant.Albino: kit.nerve = Clamp(kit.nerve + 3); break;
                case HutchVariant.Chonk: kit.carry = Clamp(kit.carry + 3); kit.speed = Clamp(kit.speed - 2); kit.greed = Clamp(kit.greed + 1); break;
                case HutchVariant.Zoomer: kit.speed = Clamp(kit.speed + 3); kit.nerve = Clamp(kit.nerve - 1); break;
            }
        }
        return kit;
    }

    public static bool Pair(HutchState h, int idA, int idB, bool breedingBox, out string why)
    {
        why = "";
        if (!breedingBox) { why = "Build the Breeding Box first."; return false; }
        var a = h.Find(idA); var b = h.Find(idB);
        if (a == null || b == null || a == b) { why = "Pick two different raccoons."; return false; }
        if (!a.Available || !b.Available) { why = "Both need to be rested adults at home."; return false; }
        a.mate = b.id; b.mate = a.id;
        a.job = b.job = (int)HutchJob.Rest;
        (a.id < b.id ? a : b).dueDays = RaccoonTuning.GestationDays;
        why = $"{a.name} and {b.name} moved into the Breeding Box. Kits in {RaccoonTuning.GestationDays} days.";
        return true;
    }

    public static bool Unpair(HutchState h, int id, out string why)
    {
        var a = h.Find(id); var b = a != null ? h.Find(a.mate) : null;
        if (a == null || b == null) { why = "They aren't paired."; return false; }
        a.mate = b.mate = 0; a.dueDays = b.dueDays = 0;
        why = $"{a.name} and {b.name} are just friends again."; return true;
    }

    /// <summary>Put a raccoon on the crew (needs a Collar Chip), take it along (Pack Leader, limited) or send it to rest.</summary>
    public static bool SetJob(HutchState h, int id, HutchJob job, int collarLevel, int packLevel, out string why)
    {
        var r = h.Find(id);
        if (r == null) { why = "That raccoon isn't in your hutch."; return false; }
        if (job != HutchJob.Rest && !r.Available)
        { why = r.IsKit ? $"{r.name} is still a kit." : r.Paired ? $"{r.name} is busy in the Breeding Box." : r.awayDays > 0 ? $"{r.name} has wandered off." : $"{r.name} is resting."; return false; }
        if (job == HutchJob.Crew && collarLevel <= 0) { why = "Build the Collar Chip first."; return false; }
        if (job == HutchJob.Companion)
        {
            int limit = CompanionLimit(packLevel);
            if (limit <= 0) { why = "Build Pack Leader first."; return false; }
            if (r.job != (int)HutchJob.Companion && h.CountJob(HutchJob.Companion) >= limit)
            { why = limit == 1 ? "Your pack is one raccoon for now. Alpha makes it three." : "Your pack is full (three)."; return false; }
        }
        r.job = (int)job;
        why = job == HutchJob.Crew ? $"{r.name} is on the crew. Tiny collar: on." : job == HutchJob.Companion ? $"{r.name} is coming with you." : $"{r.name} is off duty.";
        return true;
    }

    public static bool Rename(HutchState h, int id, string name, out string why)
    {
        var r = h.Find(id); string clean = CleanName(name);
        if (r == null || clean.Length == 0) { why = "Names are letters, digits, spaces and dashes."; return false; }
        r.name = clean; why = $"Say hello to {clean}."; return true;
    }

    public static bool Release(HutchState h, int id, out string why)
    {
        var r = h.Find(id);
        if (r == null) { why = "That raccoon isn't in your hutch."; return false; }
        h.raccoons.Remove(r); h.Repair();
        why = $"{r.name} waddled back into the night. Free!"; return true;
    }

    /// <summary>Food for one day, in cents: plain kibble a head, and the crew's dearer rate once the business has a Brain
    /// Chip (<see cref="CrewRules.FoodCents"/>).</summary>
    public static int FoodCents(HutchState h, int brainLevel)
    {
        int cents = 0;
        if (h != null) foreach (var r in h.raccoons) cents += CrewRules.FoodCents(r, brainLevel);
        return cents;
    }

    /// <summary>
    /// One in-game day passes: absentees come home, the rested get up, kits grow, and due litters are born (as many kits as
    /// there is room for; with no room the litter waits a day). <paramref name="news"/> gets a line per event.
    /// </summary>
    public static void AdvanceDay(HutchState h, int hutchLevel, Func<int, int, int> range, List<string> news)
    {
        int capacity = Capacity(hutchLevel);
        foreach (var r in h.raccoons)
        {
            if (r.awayDays > 0 && --r.awayDays == 0) news?.Add($"{r.name} came home. No explanation.");
            if (r.restDays > 0) r.restDays--;
            if (r.kitDays > 0 && --r.kitDays == 0) news?.Add($"{r.name} is all grown up.");
        }
        foreach (var carrier in h.raccoons.ToArray())
        {
            if (carrier.dueDays <= 0 || --carrier.dueDays > 0) continue;
            var other = h.Find(carrier.mate);
            if (other == null) { carrier.mate = 0; continue; }
            int room = capacity - h.Count;
            if (room <= 0) { carrier.dueDays = 1; news?.Add($"{carrier.name} and {other.name} are waiting: no room in the hutch for kits."); continue; }
            int litter = Math.Min(room, range(1, RaccoonTuning.LitterMax + 1));
            for (int i = 0; i < litter; i++)
            {
                var kit = Inherit(carrier, other, range);
                kit.id = h.nextId++; kit.name = FreshName(h, range);
                h.raccoons.Add(kit);
                string special = kit.variant != HutchVariant.Normal ? $" A {HutchVariant.Name(kit.variant).ToUpperInvariant()} one!" : "";
                news?.Add($"{carrier.name} and {other.name} had a kit: {kit.name}.{special}");
            }
            carrier.mate = other.mate = 0;
            carrier.restDays = other.restDays = RaccoonTuning.RestDaysAfterLitter;
            carrier.job = other.job = (int)HutchJob.Rest;
        }
    }

    /// <summary>One line for the roster card.</summary>
    public static string StateText(HutchState h, HutchRaccoon r)
    {
        if (r.IsKit) return r.kitDays == 1 ? "Kit: grown up tomorrow" : $"Kit: grown up in {r.kitDays} days";
        if (r.Paired)
        {
            var other = h.Find(r.mate); int due = Math.Max(r.dueDays, other != null ? other.dueDays : 0);
            return $"Breeding Box with {(other != null ? other.name : "?")}: {(due <= 1 ? "kits tomorrow" : "kits in " + due + " days")}";
        }
        if (r.awayDays > 0) return "Wandered off. Back tomorrow. Probably.";
        if (r.restDays > 0) return "Resting after the litter";
        return r.job == (int)HutchJob.Crew ? "On the crew" : r.job == (int)HutchJob.Companion ? "In your pack" : "Lounging";
    }
}

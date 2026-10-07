using System.Text;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §9.1 "Letters"): complaint letters through the existing Head Office inbox
/// (<see cref="HeadOffice.Send"/>, presentation only). At most one pops up per in-game hour; the rest go to the inbox.
/// The voice changes by district: Gary's handwritten note in Mildly Pleasant, the HOA Lady's violation notices in the
/// Meadows, Mr. Pemberton in Snootington, Dewey, Cheatham &amp; Howe LLP in the Bluffs. Car shunts get an "insurance"
/// letter, night honks one batched letter in the morning, City Works one invoice per district at midnight.
/// Sent by the host (the consequences' owner); package F relays them.
/// </summary>
public static class NeighborLetters
{
    static double lastPopupHours = double.NegativeInfinity;
    static int notice = 4470;

    public static int Sent { get; private set; }
    public static int Popups { get; private set; }

    /// <summary>Send a letter, popping it up only when the last pop-up was ≥ 1 in-game hour ago.</summary>
    public static void Send(IRulesBooks b, string subject, string body)
    {
        bool popup = b.Hours - lastPopupHours >= WorldLifeTuning.Fines.LetterPopupIntervalHours;
        if (popup) { lastPopupHours = b.Hours; Popups++; }
        Sent++;
        b.Letter(subject, body, popup);
    }

    // ------------------------------------------------------------------ incidents

    /// <summary>A resident or pedestrian was flattened (§9.1 "complaint letter").</summary>
    public static void Flattened(IRulesBooks b, byte district, string name, string address, PuppetProp prop, in NeighborConsequences.Bill bill)
    {
        string time = TimeText(b);
        name = string.IsNullOrEmpty(name) ? "A Neighbour" : name;
        string addr = string.IsNullOrEmpty(address) ? "down the street" : address;
        string subject, body;
        int v = Variant(name, 3);
        switch (district)
        {
            case 1:
                subject = $"HOA VIOLATION NOTICE #{++notice}";
                body = v == 0 ? $"Resident {name} ({addr}) was flattened by your truck at {time}.\nBylaw 7(b): Residents Shall Remain Upright.\n— {HoaLadyName()}, Minivan Meadows HOA"
                     : v == 1 ? $"At {time} your truck flattened {name} ({addr}) onto a regulation-height lawn. Bylaw 12: No Horizontal Neighbours Before Noon.\n— {HoaLadyName()}, Minivan Meadows HOA"
                     : $"Re: {name} of {addr}, flattened and briefly airborne at {time}. Airborne residents are NOT an approved front-yard feature (see Appendix C).\n— {HoaLadyName()}, Minivan Meadows HOA";
                break;
            case 2:
                subject = "We are most displeased.";
                body = v == 0 ? $"At {time} your conveyance upended {name} of {addr}. The Hills have taken note.\n— Mr. Pemberton, HOA Inspector"
                     : v == 1 ? $"One does not simply get flattened at {time}, and yet {name} of {addr} was. One's monocle fell into one's tea.\n— Mr. Pemberton, HOA Inspector"
                     : $"Your vehicle flattened {name} of {addr} at {time}. Only one of you was dressed for the occasion.\n— Mr. Pemberton, HOA Inspector";
                break;
            case 3:
                subject = "From the desk of Dewey, Cheatham & Howe LLP";
                body = v == 0 ? $"Our client, {name} of {addr}, was flattened by your truck at {time}. Damages have been deducted. This letter will be billed by the hour."
                     : v == 1 ? $"Re: {name} of {addr} v. One (1) Garbage Truck, flattened at {time}. Our client's lawn has been declared a disaster area. Damages have been deducted."
                     : $"Our client, {name} of {addr}, wishes it known that at {time} they were NOT flattened, merely \"strategically reclined\". Damages have been deducted anyway.";
                break;
            default:
                subject = "Dear Garbage Person";
                body = v == 0 ? $"Dear Garbage Person,\nat {time} your truck flattened me. I was holding {Holding(prop)}.\n— {name}, {addr}"
                     : v == 1 ? $"Dear Garbage Person,\nat {time} your truck flattened me like a pancake. A PANCAKE. I was holding {Holding(prop)}.\n— {name}, {addr}"
                     : $"To whoever drives the garbage truck,\nat {time} you flattened me. I went UP, then I came DOWN, still holding {Holding(prop)}. Not cool.\n— {name}, {addr}";
                break;
        }
        Send(b, subject, body + FineLine(bill, "flattening") + GrudgeLine(address));
    }

    /// <summary>A car was shunted, moving or parked (§9.1 "insurance" letter).</summary>
    public static void CarShunted(IRulesBooks b, byte district, string owner, string address, in NeighborConsequences.Bill bill)
    {
        string time = TimeText(b);
        owner = string.IsNullOrEmpty(owner) ? "our policyholder" : owner;
        string addr = string.IsNullOrEmpty(address) ? "a driveway near you" : address;
        string body;
        switch (district)
        {
            case 1: body = $"HOA VIOLATION NOTICE: unauthorised minivan rearrangement at {addr} ({time}). The minivan is a LEASE.\n— {HoaLadyName()}, Minivan Meadows HOA"; break;
            case 2: body = $"We are most displeased. At {time} your truck nudged {owner}'s town car ({addr}). It now sits at an angle.\n— Mr. Pemberton, HOA Inspector"; break;
            case 3: body = $"Dewey, Cheatham & Howe LLP, re: the limousine at {addr} ({time}). Our client's chauffeur now requires counselling. Damages have been deducted."; break;
            default: body = $"Maple Hollow Mutual writes: at {time} your truck shunted {owner}'s car ({addr}). They say it's a LEASE."; break;
        }
        Send(b, $"Insurance claim #{++notice}", body + FineLine(bill, "shunt") + GrudgeLine(address));
    }

    /// <summary>Last night's noise complaints, batched into one letter in the morning (§8.5).</summary>
    public static void NoiseMorning(IRulesBooks b, int[] perDistrict)
    {
        var sb = new StringBuilder("Last night your horn woke ");
        int parts = 0, total = 0;
        for (int d = 0; d < perDistrict.Length; d++)
        {
            if (perDistrict[d] <= 0) continue;
            if (parts++ > 0) sb.Append(", ");
            sb.Append(WorldLifeIds.District((byte)d).name).Append(" (").Append(perDistrict[d]).Append(perDistrict[d] == 1 ? " complaint)" : " complaints)");
            total += perDistrict[d];
        }
        if (total == 0) return;
        sb.Append(". Some of us have jobs.\n— Your Neighbours");
        Send(b, "Noise complaints", sb.ToString());
    }

    /// <summary>The nightly City Works invoice for one district (§9.1).</summary>
    public static void CityWorks(IRulesBooks b, byte district, int things, float amount, float cap, bool capped)
    {
        string name = WorldLifeIds.District(district).name;
        string body = $"City Works put back {things} thing{(things == 1 ? "" : "s")} you knocked over in {name} yesterday. Invoice: {Economy.Money(amount)}" +
                      (capped ? $" (capped at {Economy.Money(cap)}; the rest is on the house)." : ".");
        Send(b, "City Works invoice", body);
    }

    // ------------------------------------------------------------------ helpers

    static string FineLine(in NeighborConsequences.Bill bill, string what)
    {
        if (bill.waived) return $"\nFine waived (Town rules). We will remember, though.";
        if (bill.charged <= 0f) return "";
        return bill.mult > 1.001f
            ? $"\nFine: {Economy.Money(bill.charged)} ({what} #{bill.n} in this district today, ×{bill.mult:0.##})."
            : $"\nFine: {Economy.Money(bill.charged)}.";
    }

    static string GrudgeLine(string address) => string.IsNullOrEmpty(address)
        ? ""
        : "\nP.S. Their bags count as an hour older until midnight. Clear their curb to apologise.";

    static string TimeText(IRulesBooks b) => GameClock.FormatHours((float)(b.Hours % 24.0));

    /// <summary>Which take of a letter: a stable hash of the neighbour and how many letters went out (cosmetic).</summary>
    static int Variant(string who, int takes) => (int)((LifeHash.Str(who) + (uint)Sent * 2654435761u) % (uint)takes);

    static string Holding(PuppetProp p)
    {
        switch (p)
        {
            case PuppetProp.Mug: return "a latte";
            case PuppetProp.Newspaper: return "the morning paper";
            case PuppetProp.Briefcase: return "a very important briefcase";
            case PuppetProp.Leash: return "a leash (the dog is fine)";
            case PuppetProp.Clipboard: return "my clipboard";
            case PuppetProp.Cane: return "my good cane";
            case PuppetProp.Tumbler: return "a 64-ounce tumbler";
            case PuppetProp.Weights: return "two hand weights";
            case PuppetProp.Platter: return "a silver platter";
            case PuppetProp.Racket: return "a tennis racket";
            case PuppetProp.Duster: return "a feather duster";
            case PuppetProp.Trowel: return "a trowel";
            case PuppetProp.Cigar: return "a cigar";
            case PuppetProp.Flashlight: return "a flashlight";
            case PuppetProp.Phone: return "my phone";
            default: return "nothing but my dignity";
        }
    }

    static string hoaLady;
    static string HoaLadyName()
    {
        if (hoaLady != null) return hoaLady;
        if (ResidentCast.Count == 0) return "Brenda";   // the cast isn't built yet (no Neighborhood): don't cache
        for (int i = 0; i < ResidentCast.Count; i++)
        {
            var id = LifeId.Resident(i);
            if (ResidentCast.ArchetypeOf(id) != ResidentArchetype.HoaLady) continue;
            hoaLady = ResidentCast.Style(id).name;
            break;
        }
        if (string.IsNullOrEmpty(hoaLady)) hoaLady = "Brenda";
        return hoaLady;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { lastPopupHours = double.NegativeInfinity; notice = 4470; Sent = Popups = 0; hoaLady = null; }

    /// <summary>Tests: let the next letter pop up.</summary>
    public static void ResetPopupLimit() => lastPopupHours = double.NegativeInfinity;
}

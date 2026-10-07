using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §9.1): the City Works invoice. Every prop the host confirms knocked over
/// (<see cref="IWorldLifeAuthority.ClaimKnock"/>) adds its fine to that district's tally for the day ($0.50 / $2 / $5 /
/// $15 a prop, hydrants ×4), capped at $5 / $20 / $60 / $200 a day. At midnight each district with a tally gets ONE
/// invoice (Ledger.Charge, LedgerIds.CityWorks) and one letter. The tally lives in company counters (rule 9), so it keeps
/// adding up and bills on time even if you're in the garage at midnight. The Town fines toggle doesn't apply (§8.6: the
/// invoice is already capped).
///
/// Stored as W0's packed day counters (<see cref="WorldLifeIds.CityWorksCounter"/>) in half-dollar units (every prop fine
/// is a multiple of $0.50; the Bluffs cap is 400 units, inside the 10-bit count), plus a props-knocked count for the
/// letter. Host only (rules 15, 16).
/// </summary>
public static class CityWorksInvoice
{
    const float Unit = 0.5f;
    static string[] tallyKeys, countKeys;

    /// <summary>A district's invoice was posted (district byte, amount).</summary>
    public static event System.Action<byte, float> Invoiced;
    public static int Invoices { get; private set; }

    static void EnsureKeys()
    {
        if (tallyKeys != null) return;
        int n = Districts.All.Count;
        tallyKeys = new string[n]; countKeys = new string[n];
        for (int i = 0; i < n; i++)
        {
            string did = Districts.All[i].id;
            tallyKeys[i] = WorldLifeIds.CityWorksCounter(did);
            countKeys[i] = "wl_cityworks_n:" + did;
        }
    }

    /// <summary>Half-dollar units for a dollar amount.</summary>
    public static int Units(float dollars) => Mathf.RoundToInt(dollars / Unit);

    /// <summary>How many units a prop adds to a tally that's already at <paramref name="current"/> (pure; capped).</summary>
    public static int AddUnits(int current, int units, int capUnits) => Mathf.Clamp(capUnits - current, 0, Mathf.Max(0, units));

    /// <summary>Host: count one knocked prop against its district's City Works tally for today.</summary>
    public static void Tally(in KnockFact f, IRulesBooks b)
    {
        if (b == null || !b.IsHost) return;
        EnsureKeys();
        BillDue(b);
        int d = Mathf.Clamp(f.district, 0, tallyKeys.Length - 1);
        int day = RulesBooks.DayOf(b.Hours);
        int cur = RulesBooks.DailyGet(b, tallyKeys[d], day);
        int add = AddUnits(cur, Units(WorldLifeTuning.Fines.PropFine(d, f.kind)), Units(WorldLifeTuning.Fines.CityWorksCap(d)));
        // Even a capped prop keeps the day stamp current, so the count below and the tally always bill together.
        RulesBooks.DailyAdd(b, tallyKeys[d], day, add);
        RulesBooks.DailyAdd(b, countKeys[d], day, 1);
    }

    /// <summary>Today's tally for a district, dollars (already capped).</summary>
    public static float Today(byte district, IRulesBooks b)
    {
        EnsureKeys();
        int d = Mathf.Clamp(district, 0, tallyKeys.Length - 1);
        return RulesBooks.DailyGet(b, tallyKeys[d], RulesBooks.DayOf(b.Hours)) * Unit;
    }

    /// <summary>Host: post the invoice for every district whose tally is from another day (yesterday, or an earlier
    /// session). Returns how many invoices were posted.</summary>
    public static int BillDue(IRulesBooks b)
    {
        if (b == null || !b.IsHost) return 0;
        EnsureKeys();
        int today = RulesBooks.DayOf(b.Hours), posted = 0;
        for (int d = 0; d < tallyKeys.Length; d++)
        {
            bool hasTally = RulesBooks.DailyRead(b, tallyKeys[d], out int tDay, out int units);
            bool hasCount = RulesBooks.DailyRead(b, countKeys[d], out int cDay, out int things);
            if (hasTally && tDay == today) continue;
            if (!hasTally && (!hasCount || cDay == today)) continue;
            b.SetCounter(tallyKeys[d], 0);
            if (!hasCount || cDay != today) b.SetCounter(countKeys[d], 0);
            if (!hasTally || units <= 0) continue;
            float amount = units * Unit, cap = WorldLifeTuning.Fines.CityWorksCap(d);
            string did = Districts.All[d].id;
            b.Charge(amount, "City Works invoice", LedgerIds.CityWorks, null, did);
            NeighborLetters.CityWorks(b, (byte)d, Mathf.Max(1, things), amount, cap, amount >= cap - 0.001f && things > 0);
            Invoices++;
            posted++;
            var h = Invoiced;
            if (h != null) { try { h((byte)d, amount); } catch (System.Exception e) { Debug.LogException(e); } }
        }
        return posted;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { tallyKeys = countKeys = null; Invoiced = null; Invoices = 0; }
}

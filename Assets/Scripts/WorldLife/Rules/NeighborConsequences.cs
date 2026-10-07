using System;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §9): what an incident costs, applied on the host once the authority has decided
/// it. Small, funny and accountable:
/// <list type="bullet">
/// <item><b>Fines</b> (Ledger.Charge, <see cref="LedgerIds.NeighborDamages"/>) in the district where it happened: a
/// flattened resident $3 / $12 / $40 / $120, a shunted car $5 / $20 / $60 / $200. The n-th flatten or shunt in a district
/// on one in-game day costs × min(3, 1 + 0.25 (n − 1)). The Town toggle (<see cref="WorldLifeTuning.NeighborFines"/>)
/// turns them into receipt shame only.</item>
/// <item><b>Rep</b> through <see cref="ProgressionEvents.RaiseNeighborIncident"/> (Progression applies −4 / −3 / −1 × the
/// district multiplier and its caps; never escalated).</item>
/// <item><b>A grudge</b> on the victim's house (<see cref="NeighborGrudge"/>) and <b>a letter</b>
/// (<see cref="NeighborLetters"/>).</item>
/// </list>
/// Dog near misses and thrown-item flops are Rep only. Hits are always cartoon flops; nobody is ever hurt.
/// </summary>
public static class NeighborConsequences
{
    /// <summary>What an incident cost (pure, so tests and letters agree).</summary>
    public struct Bill
    {
        public byte district;
        /// <summary>The n-th flatten / shunt in this district today (1-based).</summary>
        public int n;
        public float baseFine, mult, charged;
        /// <summary>Fines are off (Town toggle): receipt shame only.</summary>
        public bool waived;
    }

    /// <summary>The price of the n-th flatten (or shunt) today in a district (pure).</summary>
    public static Bill Price(NpcHitKind kind, byte district, int nthToday, bool finesOn)
    {
        float baseFine = kind == NpcHitKind.Shunt ? WorldLifeTuning.Fines.ShuntFine(district) : WorldLifeTuning.Fines.FlattenFine(district);
        float mult = WorldLifeTuning.Fines.Escalation(nthToday);
        float fine = Mathf.Round(baseFine * mult * 100f) / 100f;   // integer cents (rule 9)
        return new Bill
        {
            district = district, n = nthToday, baseFine = baseFine, mult = mult,
            charged = finesOn ? fine : 0f, waived = !finesOn,
        };
    }

    // ------------------------------------------------------------------ stats (acceptance evals, the HUD later)

    public static int Flattened { get; private set; }
    public static int Shunted { get; private set; }
    public static int DogNearMisses { get; private set; }
    public static int ThrownFlops { get; private set; }
    public static int Waived { get; private set; }
    public static float FinesCharged { get; private set; }
    public static Bill LastBill { get; private set; }
    /// <summary>An incident was booked (kind, bill, stopId).</summary>
    public static event Action<NeighborIncidentKind, Bill, string> Booked;

    // ------------------------------------------------------------------ apply (host)

    /// <summary>
    /// Host: book a decided hit. Flatten → fine + Rep −4 + grudge + letter; Shunt → fine + Rep −3 + grudge + insurance
    /// letter; DogLeap (a real near miss) → Rep −1; ShortFlop (a hard throw) → Rep −1. Anything else is free.
    /// </summary>
    public static void Apply(in NpcHit hit, ILifeBody body, IRulesBooks b)
    {
        if (b == null || !b.IsHost) return;
        string stopId = body != null ? body.StopId : "";
        switch (hit.kind)
        {
            case NpcHitKind.Flatten:
                Flattened++;
                Incident(b, hit, stopId, NeighborIncidentKind.Flattened);
                break;
            case NpcHitKind.Shunt:
                Shunted++;
                Incident(b, hit, stopId, NeighborIncidentKind.CarShunt);
                break;
            case NpcHitKind.DogLeap:
                DogNearMisses++;
                RepOnly(b, hit, stopId, NeighborIncidentKind.DogNearMiss);
                break;
            case NpcHitKind.ShortFlop:
                ThrownFlops++;
                RepOnly(b, hit, stopId, NeighborIncidentKind.ThrownFlop);
                break;
        }
    }

    static void Incident(IRulesBooks b, in NpcHit hit, string stopId, NeighborIncidentKind kind)
    {
        var dd = Districts.AtPosition(hit.point);      // charged in the district where it happened (§9.1)
        byte district = WorldLifeIds.DistrictIndex(dd);
        int n = RulesBooks.DailyAdd(b, WorldLifeIds.IncidentCounter(dd.id), RulesBooks.DayOf(b.Hours), 1);
        var hitKind = kind == NeighborIncidentKind.CarShunt ? NpcHitKind.Shunt : NpcHitKind.Flatten;
        var bill = Price(hitKind, district, n, b.FinesOn);
        LastBill = bill;

        // The receipt: who and where, short enough for the cash ticker (≤ 22 chars shows inline).
        string who = WhoOf(hit.target, stopId);
        string house = AddressOf(stopId);
        string reason = (kind == NeighborIncidentKind.CarShunt ? "Shunted " + who + "'s car" : "Flattened " + who);
        Vector3 at = hit.point + Vector3.up * 1.2f;
        if (bill.charged > 0f)
        {
            b.Charge(bill.charged, reason, LedgerIds.NeighborDamages, at, dd.id);
            FinesCharged += bill.charged;
        }
        else
        {
            Waived++;
            GameHUD.FloatingText(at + Vector3.up * 0.4f, "Fine waived (Town rules)", new Color(1f, 0.75f, 0.45f), 0.8f);
        }

        b.Incident(new NeighborIncidentFact
        {
            kind = kind, stopId = stopId ?? "", districtId = dd.id, pos = hit.point, escalation = bill.mult,
        });
        if (!string.IsNullOrEmpty(stopId)) NeighborGrudge.Add(stopId, b);

        if (kind == NeighborIncidentKind.CarShunt) NeighborLetters.CarShunted(b, district, who, house, in bill);
        else NeighborLetters.Flattened(b, district, who, house, PropOf(hit.target), in bill);
        Raise(kind, bill, stopId);
    }

    static void RepOnly(IRulesBooks b, in NpcHit hit, string stopId, NeighborIncidentKind kind)
    {
        var dd = Districts.AtPosition(hit.point);
        b.Incident(new NeighborIncidentFact { kind = kind, stopId = stopId ?? "", districtId = dd.id, pos = hit.point, escalation = 1f });
        Raise(kind, new Bill { district = WorldLifeIds.DistrictIndex(dd), mult = 1f }, stopId);
    }

    static void Raise(NeighborIncidentKind kind, Bill bill, string stopId)
    {
        var h = Booked;
        if (h != null) { try { h(kind, bill, stopId); } catch (Exception e) { Debug.LogException(e); } }
    }

    // ------------------------------------------------------------------ the Town toggle (§9.3)

    /// <summary>Neighbour fines are charged right now (the Town toggle; default on).</summary>
    public static bool FinesOn => WorldLifeTuning.NeighborFines;

    /// <summary>Host: switch neighbour fines on (default) or to receipt shame only. Before M1 the toggle is the company
    /// counter <see cref="WorldLifeIds.FinesOffCounter"/>; F points <see cref="WorldLifeTuning.NeighborFinesProvider"/>
    /// at TownRules.NeighborFines instead. Rep, grudges, letters and flops always apply.</summary>
    public static bool SetFines(bool on)
    {
        if (GameSession.IsGuest) return false;
        GameSession.SetCounter(WorldLifeIds.FinesOffCounter, on ? 0 : 1);
        return true;
    }

    // ------------------------------------------------------------------ names (letters, receipts)

    /// <summary>The victim's name: a resident's, or a car's driver (cast names, e.g. "Gary").</summary>
    public static string WhoOf(LifeId id, string stopId)
    {
        LifeId person = id;
        if (id.kind == LifeKind.Car)
        {
            var sim = LifeSim.Active;
            person = sim != null && id.index < sim.Roster.cars.Length ? LifeId.Resident(sim.Roster.cars[id.index].driver) : LifeId.None;
        }
        if (ResidentCast.IsValid(person))
        {
            string n = ResidentCast.Style(person).name;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return "a neighbour";
    }

    static PuppetProp PropOf(LifeId id) => ResidentCast.IsValid(id) ? ResidentCast.Style(id).prop : PuppetProp.None;

    /// <summary>"14 Maple St" for a stopId ("" when unknown).</summary>
    public static string AddressOf(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return "";
        var h = CurbTrashSpawner.FindHouse(stopId);
        return h != null && !string.IsNullOrEmpty(h.address) ? h.address : stopId;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Flattened = Shunted = DogNearMisses = ThrownFlops = Waived = 0;
        FinesCharged = 0f; LastBill = default; Booked = null;
    }

    /// <summary>Tests: zero the stats.</summary>
    public static void ResetStats() => ResetStatics();
}
